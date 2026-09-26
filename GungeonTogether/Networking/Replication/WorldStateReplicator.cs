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
    /// Host: broadcasts floor/foyer transitions (plus a snapshot to each joining client).
    /// Client: follows the host into the foyer, and teleports to the host once on join - after
    /// that, the client moves freely. Position is deliberately NOT part of the change check:
    /// it used to be, which re-sent world state every frame the host moved and teleported the
    /// client onto the host each time, making it impossible for the client to move on its own.
    /// </summary>
    public class WorldStateReplicator : MonoSingleton<WorldStateReplicator>
    {
        private const float PollInterval = 0.1f;
        private const float FoyerLoadTimeoutSeconds = 30f;

        // Host side.
        private string _lastBroadcastKey = "";

        // Client side.
        private WorldStatePacket _pendingPacket;
        private bool _applyQueued;
        private bool _hasInitialState;
        private bool _returningToFoyer;

        private void Update()
        {
            if (!NetworkSession.Instance.IsHost) return;
            SyncHostWorldState();
        }

        private void SyncHostWorldState()
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            // Mid-load, floor index and position can still describe the old level.
            if (GameManager.Instance.IsLoadingLevel) return;

            bool isFoyer = GameManager.Instance.IsFoyer;
            int floorIndex = GameManager.Instance.CurrentFloor;
            string key = $"{isFoyer}|{floorIndex}";
            if (key == _lastBroadcastKey) return;
            _lastBroadcastKey = key;

            string roomId = player.CurrentRoom != null ? player.CurrentRoom.GetRoomName() : "";
            Debug.Log($"[WorldStateReplicator] Host moved to isFoyer={isFoyer}, floor={floorIndex}, room={roomId}");
            NetworkSession.Instance.Broadcast(BuildPacket(player, isFoyer, floorIndex, roomId), reliable: true);
        }

        /// <summary>Sends a snapshot of the current world state directly to one peer (used on join).</summary>
        public void SendCurrentStateTo(ulong targetId)
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            bool isFoyer = GameManager.Instance.IsFoyer;
            int floorIndex = GameManager.Instance.CurrentFloor;
            string roomId = player.CurrentRoom != null ? player.CurrentRoom.GetRoomName() : "";

            NetworkSession.Instance.SendPacket(targetId, BuildPacket(player, isFoyer, floorIndex, roomId), reliable: true);
            Debug.Log($"[WorldStateReplicator] Sent initial world state to {targetId}: isFoyer={isFoyer}, floor={floorIndex}");
        }

        private static WorldStatePacket BuildPacket(PlayerController player, bool isFoyer, int floorIndex, string roomId)
        {
            Vector3 pos = player.transform.position;
            return new WorldStatePacket
            {
                IsFoyer = isFoyer,
                FloorIndex = floorIndex,
                RoomIdentifier = roomId,
                Position = new Vector2(pos.x, pos.y),
                Rotation = player.transform.eulerAngles.z
            };
        }

        // ---- Client-side apply ----

        /// <summary>Forget everything about the last session, so the next join gets its initial teleport.</summary>
        public void ResetClientState()
        {
            StopAllCoroutines();
            _pendingPacket = null;
            _applyQueued = false;
            _hasInitialState = false;
            _returningToFoyer = false;
        }

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

            int currentFloor = GameManager.Instance.CurrentFloor;
            if (currentFloor != packet.FloorIndex)
            {
                // ETG generates dungeon floors procedurally per run rather than loading them by
                // index on demand, and no verified API exists here for "jump this client straight
                // to floor N" - so cross-floor catch-up is a known gap rather than something faked.
                Debug.LogWarning($"[WorldStateReplicator] Host is on floor {packet.FloorIndex}, we're on {currentFloor} - cross-floor sync isn't implemented yet, skipping teleport.");
                return;
            }

            // Same floor: only snap to the host on join. Afterwards the client moves on its own.
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
