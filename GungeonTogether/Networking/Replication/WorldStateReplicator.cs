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
    /// Host: detects and broadcasts changes to the local player's floor/room/foyer/position.
    /// Client: applies incoming world state to the local player, waiting out any in-progress
    /// scene load first.
    /// </summary>
    public class WorldStateReplicator : MonoSingleton<WorldStateReplicator>
    {
        private string _lastWorldState = "";

        private void Update()
        {
            if (!NetworkSession.Instance.IsHost) return;
            SyncHostWorldState();
        }

        private void SyncHostWorldState()
        {
            PlayerController player = LocalPlayer();
            if (player == null) return;

            bool isFoyer = GameManager.Instance.IsFoyer;
            int floorIndex = GameManager.Instance.CurrentFloor;
            string roomId = player.CurrentRoom != null ? player.CurrentRoom.GetRoomName() : "";
            Vector3 pos = player.transform.position;

            string currentState = $"{isFoyer}|{floorIndex}|{roomId}|{pos.x:F2}|{pos.y:F2}";
            if (currentState == _lastWorldState) return;
            _lastWorldState = currentState;

            Debug.Log($"[WorldStateReplicator] Host world state changed: isFoyer={isFoyer}, floor={floorIndex}, room={roomId}, pos={pos}");
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

        public void ApplyWorldState(WorldStatePacket packet)
        {
            if (LoadingStateReplicator.Instance.IsClientLoading)
            {
                StartCoroutine(DelayedApply(packet));
            }
            else
            {
                ApplyNow(packet);
            }
        }

        private IEnumerator DelayedApply(WorldStatePacket packet)
        {
            while (LoadingStateReplicator.Instance.IsClientLoading)
                yield return new WaitForSeconds(0.1f);
            ApplyNow(packet);
        }

        private void ApplyNow(WorldStatePacket packet)
        {
            if (packet.IsFoyer)
            {
                if (!GameManager.Instance.IsFoyer)
                {
                    GameManager.Instance.ReturnToFoyer();
                    StartCoroutine(SetPositionAfterDelay(packet.Position, packet.Rotation, 0.5f));
                }
                else
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
                // to floor N" - so unlike same-floor position sync (safe, done below), cross-floor
                // catch-up is a known gap rather than something faked with a guess.
                Debug.LogWarning($"[WorldStateReplicator] Host is on floor {packet.FloorIndex}, we're on {currentFloor} - cross-floor sync isn't implemented yet, skipping teleport.");
                return;
            }

            TeleportLocalPlayer(packet.Position, packet.Rotation);
        }

        private IEnumerator SetPositionAfterDelay(Vector2 pos, float rot, float delay)
        {
            yield return new WaitForSeconds(delay);
            TeleportLocalPlayer(pos, rot);
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
