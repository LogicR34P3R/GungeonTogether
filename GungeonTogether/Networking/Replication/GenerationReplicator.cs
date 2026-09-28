using System.Collections;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Makes the client generate the host's floors even though their save files differ.
    ///
    /// The shared seed (DungeonSeedReplicator) only fixes the dice. Generation also asks the save
    /// file: DungeonPrerequisite checks (unlocks, rescued NPCs, stats, the character played) pick the
    /// flow, the run's special-room blueprint and every weighted room, and a few GetFlag reads add or
    /// skip rooms. Different answers consume the dice differently, so a new player's Keep came out as
    /// a different map (seen: same seed, "layout DIFFERS").
    ///
    /// So the host records, in order, every answer given inside the synchronous generation steps and
    /// sends them; the client replays them in the same steps instead of asking its own save:
    /// - the run blueprint (MetaInjectionData.PreprocessRun), which runs the moment a run seed is
    ///   set - in the Breach - so its answers go out just before the seed (DungeonSeedReplicator);
    /// - each floor's flow selection and layout, sent when the host's layout is done. The client
    ///   waits for them before following the host onto the floor (WorldStateReplicator).
    /// Only the outermost hooked call is recorded/replayed: a replayed prerequisite never runs its
    /// inner GetFlag calls. Two plain fields read during generation are copied over for the window.
    /// </summary>
    public class GenerationReplicator : MonoSingleton<GenerationReplicator>
    {
        public enum WindowKind : byte
        {
            FlowSelection = 0, // GameLevelDefinition.LovinglySelectDungeonFlow
            RunBlueprint = 1,  // MetaInjectionData.PreprocessRun
            Layout = 2         // LoopDungeonGenerator.GenerateDungeonLayoutDeferred (spread over frames)
        }

        // Host: what's being recorded, and the last complete sets (for joiners).
        private GenerationDecisionsPacket _floorRecording;
        private GenerationDecisionsPacket.Window _openWindow;
        private GenerationDecisionsPacket _lastBlueprint;
        private GenerationDecisionsPacket _lastFloor;

        // Client: the host's answers not yet used.
        private GenerationDecisionsPacket _blueprint;
        private GenerationDecisionsPacket _floor;
        private GenerationDecisionsPacket _replaySource;
        private GenerationDecisionsPacket.Window _replayWindow;
        private int _replayIndex;
        private bool _replayFailed;
        private int _savedRunsWithoutSpawn;
        private bool _savedIsChump;
        private bool _fieldsOverridden;

        // Nesting depth of hooked calls; only depth 1 (the outermost) is recorded/replayed.
        private int _depth;
        // Inside a window and its code is running now. The layout window spans several frames; between
        // its chunks other game code runs (and reads flags) and must be left alone.
        private bool _active;

        // ---- Windows ----

        public static void BeginWindow(WindowKind kind) => Instance.Begin(kind);
        public static void EndWindow(WindowKind kind) => Instance.End(kind);

        /// <summary>
        /// LoopDungeonGenerator.GenerateDungeonLayoutDeferred postfix: every layout (the level load's
        /// frame-by-frame one, and GenerateDungeonLayout, which just runs it to the end) goes through
        /// this enumerable. The wrapper keeps the Layout window active only while a chunk of it runs.
        /// </summary>
        public static IEnumerable WrapLayout(IEnumerable layout) => layout == null ? null : new LayoutSteps(layout);

        private sealed class LayoutSteps : IEnumerable
        {
            private readonly IEnumerable _inner;
            public LayoutSteps(IEnumerable inner) => _inner = inner;
            public IEnumerator GetEnumerator() => new Stepper(_inner.GetEnumerator());
        }

        private sealed class Stepper : IEnumerator
        {
            private readonly IEnumerator _inner;
            private bool _started, _finished;

            public Stepper(IEnumerator inner) => _inner = inner;
            public object Current => _inner.Current;
            public void Reset() => _inner.Reset();

            public bool MoveNext()
            {
                if (_finished) return false;
                GenerationReplicator self = Instance;
                if (!_started)
                {
                    _started = true;
                    self.Begin(WindowKind.Layout);
                }
                else
                {
                    self._active = true;
                    self._depth = 0;
                }

                bool more = false;
                try
                {
                    more = _inner.MoveNext();
                }
                finally
                {
                    if (more)
                    {
                        self._active = false; // yielding a frame: other code runs until the next chunk
                    }
                    else
                    {
                        _finished = true;
                        self.End(WindowKind.Layout);
                    }
                }
                return more;
            }
        }

        private void Begin(WindowKind kind)
        {
            _depth = 0;
            _active = true;
            var session = NetworkSession.Instance;
            if (session.IsHost)
            {
                _openWindow = new GenerationDecisionsPacket.Window { Kind = (byte)kind };
                if (kind != WindowKind.RunBlueprint)
                {
                    // Flow selection starts every floor's generation.
                    if (kind == WindowKind.FlowSelection || _floorRecording == null) _floorRecording = new GenerationDecisionsPacket();
                    _floorRecording.Windows.Add(_openWindow);
                }
            }
            else if (session.IsClient)
            {
                _replaySource = kind == WindowKind.RunBlueprint ? _blueprint : _floor;
                _replayWindow = _replaySource != null ? TakeWindow(_replaySource, kind) : null;
                _replayIndex = 0;
                _replayFailed = false;
                if (_replayWindow != null) OverrideFields(_replaySource);
                else Debug.LogWarning($"[Generation] No host answers for {kind}; generating from the local save (the layout may differ).");
            }
        }

        private void End(WindowKind kind)
        {
            _depth = 0;
            _active = false;
            if (NetworkSession.Instance.IsHost && _openWindow != null)
            {
                GenerationDecisionsPacket.Window window = _openWindow;
                _openWindow = null;
                if (kind == WindowKind.RunBlueprint)
                {
                    var packet = new GenerationDecisionsPacket();
                    packet.Windows.Add(window);
                    _lastBlueprint = Send(packet, "the run blueprint");
                }
                else if (kind == WindowKind.Layout && _floorRecording != null)
                {
                    _floorRecording.SceneName = WorldStateReplicator.CurrentSceneName();
                    _lastFloor = Send(_floorRecording, _floorRecording.SceneName);
                    _floorRecording = null;
                }
            }

            if (_replayWindow != null)
            {
                if (_replayIndex != _replayWindow.Answers.Count && !_replayFailed)
                {
                    Debug.LogWarning($"[Generation] {kind}: used {_replayIndex} of the host's {_replayWindow.Answers.Count} answers - the layout may differ.");
                }
                else if (!_replayFailed)
                {
                    Debug.Log($"[Generation] {kind}: generated from the host's {_replayIndex} answers.");
                }
                _replayWindow = null;
                RestoreFields();
            }
        }

        private static GenerationDecisionsPacket.Window TakeWindow(GenerationDecisionsPacket source, WindowKind kind)
        {
            for (int i = 0; i < source.Windows.Count; i++)
            {
                if (source.Windows[i].Kind != (byte)kind) continue;
                GenerationDecisionsPacket.Window window = source.Windows[i];
                source.Windows.RemoveAt(i);
                return window;
            }
            return null;
        }

        // ---- Hooked save reads ----

        /// <summary>Prefix of every hooked save read. True: skip the original and use this answer.</summary>
        public static bool TryReplay(out bool answer)
        {
            GenerationReplicator self = Instance;
            answer = false;
            self._depth++;
            if (!self._active || self._replayWindow == null || self._replayFailed || self._depth != 1) return false;

            if (self._replayIndex >= self._replayWindow.Answers.Count)
            {
                self._replayFailed = true;
                Debug.LogWarning("[Generation] Ran out of the host's answers - the rest of this step uses the local save.");
                return false;
            }
            answer = self._replayWindow.Answers[self._replayIndex++];
            return true;
        }

        /// <summary>Postfix of every hooked save read (it runs after a replayed one too).</summary>
        public static void Record(bool answer)
        {
            GenerationReplicator self = Instance;
            if (self._active && self._depth == 1 && self._openWindow != null) self._openWindow.Answers.Add(answer);
            if (self._depth > 0) self._depth--;
        }

        // ---- Host ----

        private static GenerationDecisionsPacket Send(GenerationDecisionsPacket packet, string what)
        {
            GameStatsManager stats = GameStatsManager.HasInstance ? GameStatsManager.Instance : null;
            if (stats != null)
            {
                packet.NumberRunsValidCellWithoutSpawn = stats.NumberRunsValidCellWithoutSpawn;
                packet.IsChump = stats.isChump;
            }

            int answers = 0;
            foreach (var window in packet.Windows) answers += window.Answers.Count;
            Debug.LogInfo($"[Generation] Sending {answers} generation answers for {what} to clients.");
            NetworkSession.Instance.Broadcast(packet, reliable: true);
            return packet;
        }

        /// <summary>A joining client may generate the blueprint and our floor straight away.</summary>
        public void SendCurrentDecisionsTo(ulong peerId)
        {
            if (_lastBlueprint != null) NetworkSession.Instance.SendPacket(peerId, _lastBlueprint, reliable: true);
            if (_lastFloor != null) NetworkSession.Instance.SendPacket(peerId, _lastFloor, reliable: true);
        }

        // ---- Client ----

        public void HandleDecisions(GenerationDecisionsPacket packet)
        {
            bool isBlueprint = packet.Windows.Count > 0 && packet.Windows[0].Kind == (byte)WindowKind.RunBlueprint;
            if (isBlueprint) _blueprint = packet;
            else _floor = packet;
            Debug.Log($"[Generation] Got the host's generation answers for {(isBlueprint ? "the run blueprint" : packet.SceneName)}.");
        }

        /// <summary>A new LevelTransition: floor answers we still hold are from an older visit.</summary>
        public void DiscardFloorFor(string sceneName)
        {
            if (_floor != null && _floor.SceneName == sceneName) _floor = null;
        }

        public bool HasFloorDecisionsFor(string sceneName) => _floor != null && _floor.SceneName == sceneName;

        private void OverrideFields(GenerationDecisionsPacket source)
        {
            GameStatsManager stats = GameStatsManager.HasInstance ? GameStatsManager.Instance : null;
            if (stats == null || _fieldsOverridden) return;
            _savedRunsWithoutSpawn = stats.NumberRunsValidCellWithoutSpawn;
            _savedIsChump = stats.isChump;
            stats.NumberRunsValidCellWithoutSpawn = source.NumberRunsValidCellWithoutSpawn;
            stats.isChump = source.IsChump;
            _fieldsOverridden = true;
        }

        private void RestoreFields()
        {
            if (!_fieldsOverridden) return;
            if (GameStatsManager.HasInstance)
            {
                GameStatsManager.Instance.NumberRunsValidCellWithoutSpawn = _savedRunsWithoutSpawn;
                GameStatsManager.Instance.isChump = _savedIsChump;
            }
            _fieldsOverridden = false;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            RestoreFields();
            _floorRecording = null;
            _openWindow = null;
            _lastBlueprint = null;
            _lastFloor = null;
            _blueprint = null;
            _floor = null;
            _replayWindow = null;
            _depth = 0;
        }
    }
}
