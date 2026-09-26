using System;
using System.Collections.Generic;
using Dungeonator;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Makes host and client generate the same dungeon by sharing a run seed, using ETG's own
    /// seeded-run support (GameManager.InitializeForRunWithSeed - every floor's layout is then
    /// generated from that seed, and save-history effects like "avoid recently seen rooms" are
    /// switched off via BraveRandom.IgnoreGenerationDifferentiator).
    ///
    /// Host: rolls a fresh seed each time it enters the foyer (i.e. before every run), applies it,
    /// and sends it to clients. Client: applies the host's seed once it's in the foyer - applying
    /// mid-run would re-run the game's "startup seed data" (reward manifest, injection blueprint)
    /// in the middle of a run.
    ///
    /// Seeding isn't guaranteed to be enough: layout generation still reads each player's own save
    /// flags (unlocked rooms, meta-shop, Lost Adventurer progress). So after every floor load both
    /// sides fingerprint the layout and the client logs whether it matches the host's.
    ///
    /// ETG never un-seeds on its own (nothing resets CurrentRunSeed, and its setter forces
    /// IgnoreGenerationDifferentiator on), so once out of a session this puts both back, in the
    /// foyer, so later solo runs are normal random runs again.
    /// </summary>
    public class DungeonSeedReplicator : MonoSingleton<DungeonSeedReplicator>
    {
        private const int MaxSeed = 1000000000; // same range ETG uses for unseeded dungeon seeds

        // Host side.
        private int _hostSeed;
        private bool _hostWasInFoyer;

        // Client side.
        private int _pendingClientSeed;

        // Both sides.
        private bool _seedApplied; // we changed the game's seed and owe it a reset once out of a session
        private GameManager _subscribedTo;
        private LayoutHashPacket _hostLayout;  // client: host's latest fingerprint
        private LayoutHashPacket _localLayout; // client: own latest fingerprint

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return;

            EnsureSubscribed(gm);

            // All seed changes happen in the foyer between runs, never mid-level.
            bool inFoyerIdle = gm.IsFoyer && !gm.IsLoadingLevel;

            if (NetworkSession.Instance.IsHost)
            {
                if (gm.IsLoadingLevel) return;
                if (gm.IsFoyer && !_hostWasInFoyer) RollHostSeed();
                _hostWasInFoyer = gm.IsFoyer;
            }
            else if (NetworkSession.Instance.IsClient)
            {
                if (_pendingClientSeed != 0 && inFoyerIdle)
                {
                    ApplySeed(_pendingClientSeed);
                    Debug.LogInfo($"[DungeonSeed] Applied host run seed {_pendingClientSeed}.");
                    _pendingClientSeed = 0;
                }
            }
            else if (_seedApplied && inFoyerIdle)
            {
                ResetToUnseeded(gm);
            }
        }

        // ---- Seed ----

        private void RollHostSeed()
        {
            _hostSeed = new Random().Next(1, MaxSeed);
            ApplySeed(_hostSeed);
            NetworkSession.Instance.Broadcast(new RunSeedPacket { Seed = _hostSeed }, reliable: true);
            Debug.LogInfo($"[DungeonSeed] Rolled run seed {_hostSeed} and sent it to clients.");
        }

        /// <summary>Host: gives a joining client the seed for the current/next run.</summary>
        public void SendCurrentSeedTo(ulong targetId)
        {
            if (_hostSeed == 0) return; // hosting started mid-run; the next foyer visit rolls one
            NetworkSession.Instance.SendPacket(targetId, new RunSeedPacket { Seed = _hostSeed }, reliable: true);
        }

        /// <summary>Client: remember the host's seed; Update applies it once we're idle in the foyer.</summary>
        public void HandleRunSeed(RunSeedPacket packet)
        {
            if (packet.Seed == 0) return;
            _pendingClientSeed = packet.Seed;
            Debug.Log($"[DungeonSeed] Received host run seed {packet.Seed}; applying once in the foyer.");
        }

        /// <summary>
        /// Client: apply a held-back host seed right now, regardless of where we are. Called just
        /// before following the host to a new floor - it must be in place before that floor
        /// generates, and once the load starts we're no longer "idle in the foyer" for Update to do it.
        /// </summary>
        public void ApplyPendingSeedNow()
        {
            if (_pendingClientSeed == 0) return;
            ApplySeed(_pendingClientSeed);
            Debug.LogInfo($"[DungeonSeed] Applied host run seed {_pendingClientSeed} before following the host.");
            _pendingClientSeed = 0;
        }

        private void ApplySeed(int seed)
        {
            GameManager.Instance.InitializeForRunWithSeed(seed);
            _seedApplied = true;
        }

        private void ResetToUnseeded(GameManager gm)
        {
            gm.CurrentRunSeed = 0;
            BraveRandom.IgnoreGenerationDifferentiator = false;
            // The setter just seeded both RNGs with 0. Left like that, every later "random"
            // dungeon seed would come out the same, so reseed them from the clock like a fresh
            // game start does.
            BraveRandom.InitializeRandom();
            UnityEngine.Random.InitState(Environment.TickCount);
            _seedApplied = false;
            Debug.LogInfo("[DungeonSeed] Out of session - restored unseeded runs.");
        }

        /// <summary>Called from NetworkSession.Shutdown. The game's seed itself is reset lazily in Update.</summary>
        public void ResetSessionState()
        {
            _hostSeed = 0;
            _hostWasInFoyer = false;
            _pendingClientSeed = 0;
            _hostLayout = null;
            _localLayout = null;
        }

        // ---- Layout check ----

        private void EnsureSubscribed(GameManager gm)
        {
            if (_subscribedTo == gm) return;
            if (_subscribedTo != null) _subscribedTo.OnNewLevelFullyLoaded -= OnLevelLoaded;
            gm.OnNewLevelFullyLoaded += OnLevelLoaded;
            _subscribedTo = gm;
        }

        private void OnLevelLoaded()
        {
            if (!NetworkSession.Instance.IsConnected) return;

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsFoyer) return;

            LayoutHashPacket local = FingerprintLayout(gm);
            if (local == null) return;

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(local, reliable: true);
            }
            else
            {
                _localLayout = local;
                CompareLayouts();
            }
        }

        /// <summary>Client: host's fingerprint arrived - compare now if we've finished loading that floor too.</summary>
        public void HandleLayoutHash(LayoutHashPacket packet)
        {
            _hostLayout = packet;
            CompareLayouts();
        }

        private void CompareLayouts()
        {
            // Whichever side finishes loading second triggers the comparison.
            if (_hostLayout == null || _localLayout == null) return;
            if (_hostLayout.SceneName != _localLayout.SceneName) return;

            LayoutHashPacket host = _hostLayout, local = _localLayout;
            _hostLayout = null;
            _localLayout = null;

            if (host.Seed != local.Seed)
            {
                Debug.LogWarning($"[DungeonSeed] {local.SceneName}: seeds differ (host {host.Seed}, local {local.Seed}) - layouts aren't comparable.");
            }
            else if (host.Hash == local.Hash)
            {
                Debug.LogInfo($"[DungeonSeed] {local.SceneName}: layout matches host ({local.RoomCount} rooms, hash {local.Hash:X8}).");
            }
            else
            {
                Debug.LogWarning($"[DungeonSeed] {local.SceneName}: layout DIFFERS from host despite same seed {local.Seed} " +
                                 $"(host {host.RoomCount} rooms/{host.Hash:X8}, local {local.RoomCount} rooms/{local.Hash:X8}). " +
                                 "Likely different save progress - diff the Debug-level room lists from both logs.");
            }
        }

        /// <summary>
        /// Order-sensitive FNV-1a over every room's name and grid position. Also logs the room list
        /// at Debug so a mismatch can be diffed between the two players' logs.
        /// </summary>
        private static LayoutHashPacket FingerprintLayout(GameManager gm)
        {
            Dungeon dungeon = gm.Dungeon;
            if (dungeon == null || dungeon.data == null || dungeon.data.rooms == null) return null;

            List<RoomHandler> rooms = dungeon.data.rooms;
            var entries = new List<string>(rooms.Count);
            uint hash = 2166136261;
            foreach (RoomHandler room in rooms)
            {
                string entry = $"{room.GetRoomName() ?? ""}@{room.area.basePosition.x},{room.area.basePosition.y}";
                entries.Add(entry);
                foreach (char c in entry)
                {
                    hash = (hash ^ c) * 16777619;
                }
                hash = (hash ^ ';') * 16777619;
            }

            Debug.Log($"[DungeonSeed] {WorldStateReplicator.CurrentSceneName()} seed {gm.CurrentRunSeed} rooms: {string.Join(" | ", entries.ToArray())}");

            return new LayoutHashPacket
            {
                SceneName = WorldStateReplicator.CurrentSceneName(),
                Seed = gm.CurrentRunSeed,
                Hash = hash,
                RoomCount = rooms.Count
            };
        }
    }
}
