using System.Collections.Generic;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Host: detects room changes and periodically syncs active enemies, diffing against the last
    /// tick to send spawns (room entry or mid-room) and deaths. Client: spawns/updates/removes
    /// the corresponding remote AIActors.
    ///
    /// Replaces RoomSyncManager, which never actually worked: it read the current room via a
    /// GameManager.CurrentRoomHandler property that doesn't exist on this game version (always
    /// null, so SyncHost() returned immediately every frame), and read enemy health/AI state via
    /// an EnemyController type that doesn't exist either (AIActor is the real type).
    /// </summary>
    public class EnemyReplicator : MonoSingleton<EnemyReplicator>
    {
        private const float StateSyncInterval = 0.2f;

        private string _currentRoomName = "";
        private float _nextStateSyncTime;

        // Host side: ids synced on the previous tick. An id new on the next tick gets an EnemySpawn;
        // one missing from it died (or otherwise left the room's active list) and gets an EnemyDeath.
        private HashSet<int> _liveIds = new HashSet<int>();
        private HashSet<int> _currentIds = new HashSet<int>();

        private void Update()
        {
            if (!NetworkSession.Instance.IsHost) return;

            RoomHandler room = CurrentRoom();
            if (room == null) return;

            string roomName = room.GetRoomName();
            if (roomName != _currentRoomName)
            {
                _currentRoomName = roomName;
                OnRoomChanged(room, roomName);
            }

            if (Time.time >= _nextStateSyncTime)
            {
                _nextStateSyncTime = Time.time + StateSyncInterval;
                SyncEnemyStates(room);
            }
        }

        private static RoomHandler CurrentRoom()
        {
            PlayerController player = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            return player != null ? player.CurrentRoom : null;
        }

        private void OnRoomChanged(RoomHandler room, string roomName)
        {
            // A fresh room means every id assigned in the previous one is meaningless now -
            // clear on both sides so a late/duplicate packet can't resurrect a stale enemy id.
            NetworkEntityManager.Instance.Clear();
            _liveIds.Clear();
            NetworkSession.Instance.Broadcast(new RoomChangePacket { RoomName = roomName });

            // With _liveIds empty, the next sync spawns every enemy in the room. Run it this frame
            // rather than waiting out the interval.
            _nextStateSyncTime = 0f;
        }

        private void BroadcastSpawn(AIActor enemy)
        {
            string guid = enemy.encounterTrackable != null ? enemy.encounterTrackable.EncounterGuid : null;
            if (string.IsNullOrEmpty(guid)) return;

            int id = NetworkEntityManager.Instance.GetOrAssignId(enemy);
            var packet = new EnemySpawnPacket
            {
                EnemyId = id,
                EnemyGuid = guid,
                Position = enemy.transform.position,
                Rotation = enemy.transform.eulerAngles.z,
                Health = enemy.healthHaver != null ? Mathf.RoundToInt(enemy.healthHaver.GetCurrentHealth()) : 0
            };
            NetworkSession.Instance.Broadcast(packet, reliable: true);
        }

        private void SyncEnemyStates(RoomHandler room)
        {
            _currentIds.Clear();

            List<AIActor> enemies = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            foreach (AIActor enemy in enemies)
            {
                // A dying enemy can still be in the active list for a frame or two - treat it as gone.
                if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead) continue;

                int id = NetworkEntityManager.Instance.GetOrAssignId(enemy);
                _currentIds.Add(id);

                // Not synced last tick: either the room was just entered, or it appeared mid-room
                // (reinforcement wave, summon). Either way the client doesn't have it yet.
                if (!_liveIds.Contains(id)) BroadcastSpawn(enemy);

                var packet = new EnemyStatePacket
                {
                    EnemyId = id,
                    Position = enemy.transform.position,
                    Rotation = enemy.transform.eulerAngles.z,
                    Health = Mathf.RoundToInt(enemy.healthHaver.GetCurrentHealth()),
                    AIState = (int)enemy.State
                };
                NetworkSession.Instance.Broadcast(packet, reliable: false);
            }

            foreach (int id in _liveIds)
            {
                if (_currentIds.Contains(id)) continue;
                NetworkSession.Instance.Broadcast(new EnemyDeathPacket { EnemyId = id }, reliable: true);
                Debug.Log($"[EnemyReplicator] Enemy {id} died, broadcast EnemyDeath.");
            }

            var swap = _liveIds;
            _liveIds = _currentIds;
            _currentIds = swap;
        }

        // ---- Client-side apply ----

        public void HandleSpawn(EnemySpawnPacket packet)
        {
            // Never stack a second copy on an id we already have - AddRemote would orphan the first.
            if (NetworkEntityManager.Instance.GetRemote(packet.EnemyId) != null) return;

            AIActor prefab = EnemyDatabase.GetOrLoadByGuid(packet.EnemyGuid);
            if (prefab == null)
            {
                Debug.LogWarning($"[EnemyReplicator] Unknown enemy guid {packet.EnemyGuid}, cannot spawn.");
                return;
            }

            RoomHandler room = CurrentRoom();
            if (room == null) return;

            AIActor spawned = AIActor.Spawn(prefab, packet.Position, room);
            if (spawned != null)
            {
                NetworkEntityManager.Instance.AddRemote(packet.EnemyId, spawned.gameObject);
            }
        }

        public void HandleState(EnemyStatePacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            if (remote == null) return;

            remote.transform.position = packet.Position;
            remote.transform.rotation = Quaternion.Euler(0, 0, packet.Rotation);

            // Health/AIState are intentionally not applied to the remote AIActor's own
            // combat systems here - forcing health through HealthHaver can trigger real
            // death/hit-reaction logic (VFX, drops), which isn't something a purely
            // visual remote representation should set off client-side.
        }

        public void HandleDeath(EnemyDeathPacket packet)
        {
            NetworkEntityManager.Instance.RemoveRemote(packet.EnemyId);
        }
    }
}
