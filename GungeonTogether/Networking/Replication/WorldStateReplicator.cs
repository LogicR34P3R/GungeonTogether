using System.Collections;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Keeps the client on the same level as the host.
    ///
    /// Host: broadcasts a LevelTransition as soon as it starts loading a new floor (so the client
    /// generates in parallel), and a WorldState once a load completes (plus a snapshot to each
    /// joining client). Levels are identified by scene name: ETG's CurrentFloor is -1 on every
    /// secret floor, so an index can't tell the Oubliette from the Abbey.
    ///
    /// Client: follows the host to its floor via DelayedLoadCustomLevel(sceneName), which handles
    /// main and secret floors alike (LoadNextLevel would use the client's own nextLevelIndex /
    /// InjectedLevelName and go wrong on any secret route); follows into the foyer via
    /// ReturnToFoyer; teleports onto the host once on join if already on the same level. Position
    /// is deliberately NOT part of the host's change check - it used to be, which teleported the
    /// client onto the host every frame.
    /// </summary>
    public class WorldStateReplicator : MonoSingleton<WorldStateReplicator>
    {
        private const float PollInterval = 0.1f;
        private const float FoyerLoadTimeoutSeconds = 30f;
        private const float FollowFadeSeconds = 0.5f;

        // Scene names that aren't dungeon floors to follow with DelayedLoadCustomLevel - the foyer
        // is reached via ReturnToFoyer instead (it resets the run, which a plain load wouldn't).
        private const string FoyerSceneName = "tt_foyer";

        // Host side.
        private string _lastBroadcastKey = "";
        private bool _hostWasLoading;
        private GameLevelDefinition _definitionAtLoadStart;
        private bool _transitionSent;

        // Client side.
        private WorldStatePacket _pendingPacket;
        private bool _applyQueued;
        private bool _hasInitialState;
        private bool _returningToFoyer;
        private string _followTarget;
        private bool _followQueued;

        /// <summary>Scene name of the level we're on ("" in the foyer or before any level loads).</summary>
        public static string CurrentSceneName()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsFoyer) return "";
            GameLevelDefinition def = gm.GetLastLoadedLevelDefinition();
            return def != null ? def.dungeonSceneName ?? "" : "";
        }

        private void Update()
        {
            if (GameManager.Instance == null) return;
            UpdateElevatorWait();

            if (!NetworkSession.Instance.IsHost) return;
            DetectHostTransitionStart();
            SyncHostWorldState();
        }

        /// <summary>
        /// GameManager.Pause postfix. The pause menu freezes the game by setting its time scale to 0
        /// - on the host that froze every enemy, so the client ran around unable to hurt anything.
        /// Online, the game keeps running behind the menu (the paused player just stands still: input
        /// is blocked while paused). Death's own pause (PauseRaw + its own multiplier) is untouched.
        /// </summary>
        public static void OnPaused(GameManager gm)
        {
            if (NetworkSession.Instance.IsConnected && gm != null) BraveTime.ClearMultiplier(gm.gameObject);
        }

        // ---- Leaving a floor together ----

        // The exit elevator the local player stepped into, while it waits for everyone.
        private ElevatorDepartureController _waitingElevator;
        private SpeculativeRigidbody _waitingTrigger;

        /// <summary>
        /// ElevatorDepartureController.OnElevatorTriggerEnter prefix. Vanilla co-op leaves only once
        /// every living player stands in the elevator, whoever stepped in first; it can only see
        /// local players, so the waiting moves to UpdateElevatorWait. False skips the original.
        /// </summary>
        public static bool OnElevatorEntered(ElevatorDepartureController elevator, SpeculativeRigidbody trigger,
            SpeculativeRigidbody enterer, Tribool arrived)
        {
            if (!NetworkSession.Instance.IsConnected || elevator == null || trigger == null || enterer == null) return true;
            PlayerController local = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            if (local == null || enterer.gameObject != local.gameObject) return true;
            // Not ready to leave yet (still arriving), or the rare cryo variant: the original decides.
            if (arrived != Tribool.Ready) return true;

            Instance._waitingElevator = elevator;
            Instance._waitingTrigger = trigger;
            return false;
        }

        /// <summary>
        /// While the local player waits in an exit elevator: once every living partner's avatar is
        /// in it too, the host leaves (the client follows as for any host level change). The client
        /// never leaves on its own - the host sees the same two players in its elevator and goes.
        /// </summary>
        private void UpdateElevatorWait()
        {
            if (_waitingElevator == null) return;

            GameManager gm = GameManager.Instance;
            PlayerController local = gm.PrimaryPlayer;
            if (!NetworkSession.Instance.IsConnected || gm.IsLoadingLevel || local == null || _waitingTrigger == null
                || !_waitingTrigger.ContainsPoint(local.SpriteBottomCenter, int.MaxValue, true))
            {
                // Stepped back out (or the session/level ended): nothing to wait for.
                _waitingElevator = null;
                _waitingTrigger = null;
                return;
            }

            // Avatars carry the partner's transform origin; test their feet like the game does.
            Vector2 feetOffset = (Vector2)(local.SpriteBottomCenter - local.transform.position);
            SpeculativeRigidbody trigger = _waitingTrigger;
            if (!PlayerReplicator.Instance.AllPartners(pos => trigger.ContainsPoint(pos + feetOffset, int.MaxValue, true), includeGhosts: false))
            {
                PlayerReplicator.Instance.ShowWaitingHint(local);
                return;
            }
            if (!NetworkSession.Instance.IsHost) return;

            Debug.LogInfo("[WorldStateReplicator] Everyone is in the elevator; leaving the floor.");
            ElevatorDepartureController elevator = _waitingElevator;
            _waitingElevator = null;
            _waitingTrigger = null;
            elevator.DoDeparture();
        }

        // ---- Host ----

        /// <summary>
        /// GameManager assigns the target level definition near the start of its async load, before
        /// generation - so watching it change while IsLoadingLevel is true tells us where the host
        /// is going without patching every exit (elevator, secret floor, shortcut, pitfall...).
        /// </summary>
        private void DetectHostTransitionStart()
        {
            GameManager gm = GameManager.Instance;
            bool loading = gm.IsLoadingLevel;

            if (loading && !_hostWasLoading)
            {
                _definitionAtLoadStart = gm.GetLastLoadedLevelDefinition();
                _transitionSent = false;
            }
            _hostWasLoading = loading;

            if (!loading || _transitionSent) return;

            GameLevelDefinition target = gm.GetLastLoadedLevelDefinition();
            if (target == null || target == _definitionAtLoadStart) return;
            _transitionSent = true;

            if (!IsFollowableFloor(target)) return;

            Debug.LogInfo($"[WorldStateReplicator] Host started loading {target.dungeonSceneName}; telling clients to follow.");
            NetworkSession.Instance.Broadcast(new LevelTransitionPacket { SceneName = target.dungeonSceneName }, reliable: true);
        }

        private static bool IsFollowableFloor(GameLevelDefinition def)
        {
            return !string.IsNullOrEmpty(def.dungeonSceneName)
                && !string.IsNullOrEmpty(def.dungeonPrefabPath) // prefab-less scenes: foyer, main menu, etc.
                && def.dungeonSceneName != FoyerSceneName;
        }

        private void SyncHostWorldState()
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            // Mid-load, level identity and position can still describe the old level.
            if (GameManager.Instance.IsLoadingLevel) return;

            bool isFoyer = GameManager.Instance.IsFoyer;
            string sceneName = CurrentSceneName();
            string key = $"{isFoyer}|{sceneName}";
            if (key == _lastBroadcastKey) return;
            _lastBroadcastKey = key;

            string roomId = player.CurrentRoom != null ? player.CurrentRoom.GetRoomName() : "";
            Debug.Log($"[WorldStateReplicator] Host arrived: isFoyer={isFoyer}, scene={sceneName}, room={roomId}");
            NetworkSession.Instance.Broadcast(BuildPacket(player, isFoyer, sceneName, roomId), reliable: true);
        }

        /// <summary>Sends a snapshot of the current world state directly to one peer (used on join).</summary>
        public void SendCurrentStateTo(ulong targetId)
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            bool isFoyer = GameManager.Instance.IsFoyer;
            string sceneName = CurrentSceneName();
            string roomId = player.CurrentRoom != null ? player.CurrentRoom.GetRoomName() : "";

            NetworkSession.Instance.SendPacket(targetId, BuildPacket(player, isFoyer, sceneName, roomId), reliable: true);
            Debug.Log($"[WorldStateReplicator] Sent initial world state to {targetId}: isFoyer={isFoyer}, scene={sceneName}");
        }

        private static WorldStatePacket BuildPacket(PlayerController player, bool isFoyer, string sceneName, string roomId)
        {
            Vector3 pos = player.transform.position;
            return new WorldStatePacket
            {
                IsFoyer = isFoyer,
                SceneName = sceneName,
                RoomIdentifier = roomId,
                Position = new Vector2(pos.x, pos.y),
                Rotation = player.transform.eulerAngles.z
            };
        }

        // ---- Client: session state ----

        /// <summary>Forget everything about the last session, so the next join gets its initial teleport.</summary>
        public void ResetClientState()
        {
            StopAllCoroutines();
            _pendingPacket = null;
            _applyQueued = false;
            _hasInitialState = false;
            _returningToFoyer = false;
            _followTarget = null;
            _followQueued = false;
        }

        // ---- Client: follow the host between levels ----

        public void HandleLevelTransition(LevelTransitionPacket packet)
        {
            if (string.IsNullOrEmpty(packet.SceneName)) return;
            // The host has only just started loading: any answers we hold for this scene are stale.
            GenerationReplicator.Instance.DiscardFloorFor(packet.SceneName);
            RequestFollow(packet.SceneName);
        }

        // How long a following client waits for the host's generation answers before generating
        // from its own save anyway. The host sends them once its layout is done - normally a second or two.
        private const float GenerationAnswersTimeout = 20f;

        private void RequestFollow(string sceneName)
        {
            // Newest target wins; at most one waiting coroutine.
            _followTarget = sceneName;
            if (_followQueued) return;

            _followQueued = true;
            StartCoroutine(FollowWhenReady());
        }

        private IEnumerator FollowWhenReady()
        {
            // DelayedLoadCustomLevel is a no-op while we're mid-load, so wait our own load out first.
            while (GameManager.Instance == null || GameManager.Instance.IsLoadingLevel)
                yield return new WaitForSeconds(PollInterval);

            string target = _followTarget;
            if (string.IsNullOrEmpty(target) || CurrentSceneName() == target)
            {
                _followTarget = null;
                _followQueued = false;
                yield break; // already there
            }

            // Generate the floor from the host's save answers (GenerationReplicator), not our own.
            float waitUntil = Time.realtimeSinceStartup + GenerationAnswersTimeout;
            while (!GenerationReplicator.Instance.HasFloorDecisionsFor(target) && Time.realtimeSinceStartup < waitUntil
                   && NetworkSession.Instance.IsClient && _followTarget == target)
            {
                yield return new WaitForSeconds(PollInterval);
            }
            if (_followTarget != target) // a newer target arrived meanwhile; start over for it
            {
                StartCoroutine(FollowWhenReady());
                yield break;
            }
            if (!GenerationReplicator.Instance.HasFloorDecisionsFor(target))
            {
                Debug.LogWarning($"[WorldStateReplicator] No generation answers from the host for {target} after {GenerationAnswersTimeout:0}s; following anyway.");
            }

            _followTarget = null;
            _followQueued = false;
            if (!NetworkSession.Instance.IsClient) yield break;
            BeginFollow(target);
        }

        private static void BeginFollow(string sceneName)
        {
            GameManager gm = GameManager.Instance;

            // The generation seed has to be in place before the new floor generates. A client that
            // joined mid-run (or is leaving the foyer right now) may still be holding it back.
            DungeonSeedReplicator.Instance.ApplyPendingSeedNow();

            // Same presentation as a normal elevator exit (ElevatorDepartureController), minus its
            // DoMidgameSave - a co-op run can't be resumed solo anyway.
            if (gm.AllPlayers != null)
            {
                foreach (PlayerController p in gm.AllPlayers)
                {
                    if (p != null) p.PrepareForSceneTransition();
                }
            }
            if (Pixelator.Instance != null) Pixelator.Instance.FadeToBlack(FollowFadeSeconds);
            if (GameUIRoot.Instance != null)
            {
                GameUIRoot.Instance.HideCoreUI(string.Empty);
                GameUIRoot.Instance.ToggleLowerPanels(targetVisible: false, permanent: false, string.Empty);
            }

            Debug.LogInfo($"[WorldStateReplicator] Following host to {sceneName}.");
            gm.DelayedLoadCustomLevel(FollowFadeSeconds, sceneName);

            // Leaving the Breach through its door/elevator calls OnDepartedFoyer, which is the only
            // thing that clears GameManager.IsFoyer and hands players their guns back. Loading a
            // level directly skips it, so a following client would arrive gunless and still "in the
            // foyer" - CurrentSceneName() then reads "" and the next WorldState re-loads the level.
            // Called right after starting the load, as FoyerGungeonDoor does.
            if (gm.IsFoyer && Foyer.Instance != null)
            {
                try
                {
                    Foyer.Instance.OnDepartedFoyer();
                }
                catch (System.Exception ex)
                {
                    // It assumes every player holds a gun; don't let that abort the follow.
                    gm.IsFoyer = false;
                    Debug.LogWarning($"[WorldStateReplicator] OnDepartedFoyer failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ---- Client: world state ----

        public void ApplyWorldState(WorldStatePacket packet)
        {
            // Keep only the newest packet and at most one waiting coroutine - an older state is
            // never worth applying once a newer one has arrived.
            _pendingPacket = packet;
            if (_applyQueued) return;

            _applyQueued = true;
            StartCoroutine(ApplyWhenNotLoading());
        }

        private IEnumerator ApplyWhenNotLoading()
        {
            while (IsAnyoneLoading())
                yield return new WaitForSeconds(PollInterval);

            WorldStatePacket packet = _pendingPacket;
            _pendingPacket = null;
            _applyQueued = false;
            if (packet != null) ApplyNow(packet);
        }

        private void ApplyNow(WorldStatePacket packet)
        {
            bool isInitialState = !_hasInitialState;
            _hasInitialState = true;

            if (packet.IsFoyer)
            {
                if (!GameManager.Instance.IsFoyer)
                {
                    if (_returningToFoyer) return; // already on our way - don't trigger a second load

                    _returningToFoyer = true;
                    GameManager.Instance.ReturnToFoyer();
                    StartCoroutine(TeleportAfterFoyerLoad(packet.Position, packet.Rotation));
                }
                else if (isInitialState)
                {
                    TeleportLocalPlayer(packet.Position, packet.Rotation);
                }
                return;
            }

            if (CurrentSceneName() != packet.SceneName)
            {
                // A LevelTransition should normally have got us there already; this covers joining
                // mid-run and any transition we missed. The game spawns us at the level entrance,
                // so no teleport afterwards.
                RequestFollow(packet.SceneName);
                return;
            }

            // Same level: only snap to the host on join. Afterwards the client moves on its own.
            if (isInitialState)
            {
                TeleportLocalPlayer(packet.Position, packet.Rotation);
            }
        }

        private IEnumerator TeleportAfterFoyerLoad(Vector2 pos, float rot)
        {
            // ReturnToFoyer may not flip IsLoadingLevel on the very same frame, so give it a moment
            // before polling, and cap the wait so a failed load can't leave this stuck forever.
            yield return new WaitForSeconds(0.5f);

            float deadline = Time.realtimeSinceStartup + FoyerLoadTimeoutSeconds;
            while ((GameManager.Instance.IsLoadingLevel || !GameManager.Instance.IsFoyer)
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSeconds(PollInterval);
            }

            _returningToFoyer = false;
            if (GameManager.Instance.IsFoyer)
            {
                TeleportLocalPlayer(pos, rot);
            }
            else
            {
                Debug.LogWarning("[WorldStateReplicator] Timed out waiting for the foyer to load; skipping teleport.");
            }
        }

        private static bool IsAnyoneLoading()
        {
            return LoadingStateReplicator.Instance.IsClientLoading
                || (GameManager.Instance != null && GameManager.Instance.IsLoadingLevel);
        }

        private static void TeleportLocalPlayer(Vector2 position, float rotation)
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            player.transform.position = new Vector3(position.x, position.y, 0f);
            player.transform.rotation = Quaternion.Euler(0f, 0f, rotation);
        }

        private static PlayerController LocalPlayer()
        {
            return GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
        }
    }
}
