using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Host-authoritative enemies ("puppet" model).
    ///
    /// Host: plays normally. Syncs the enemies in its current room - diffing against the last tick
    /// to send spawns (room entry or mid-room) and deaths - and tells clients when a room clears.
    ///
    /// Client: fights only the host's enemies. Its own native enemies are removed (every room on
    /// level load, plus a per-frame sweep of its current room for anything spawned later) and its
    /// rooms' reinforcement waves are emptied, so it never runs its own fights. Host enemies are
    /// spawned as puppets: AI off, ignored for room clear, immune to local damage - so destroying
    /// one never fires the client's own clear/wave/reward logic, and the client can't kill one
    /// locally and drop loot. The host's RoomCleared recharges the client's active items; there is
    /// no client room-clear loot (a deliberate first-version choice). Client doors mirror the host's via RoomSealState.
    ///
    /// Boss rooms and bosses are left entirely alone on both sides for now: each player fights its
    /// own boss.
    /// </summary>
    public class EnemyReplicator : MonoSingleton<EnemyReplicator>
    {
        private const float StateSyncInterval = 0.2f;

        // RoomHandler keeps its pending reinforcement waves private; there's no public way to cancel them.
        private static readonly FieldInfo ReinforcementLayersField =
            typeof(RoomHandler).GetField("remainingReinforcementLayers", BindingFlags.NonPublic | BindingFlags.Instance);

        // Host side.
        private string _currentRoomName = "";
        private float _nextStateSyncTime;
        // Ids synced on the previous tick. An id new on the next tick gets an EnemySpawn; one
        // missing from it died (or otherwise left the room's active list) and gets an EnemyDeath.
        private HashSet<int> _liveIds = new HashSet<int>();
        private HashSet<int> _currentIds = new HashSet<int>();

        // Host side: the room whose doors are sealed right now, for a joining client's snapshot.
        private string _hostSealedRoom;

        // Client side.
        private string _hostRoomName = "";
        private readonly List<EnemySpawnPacket> _pendingSpawns = new List<EnemySpawnPacket>();
        // Seal states that arrived while we were still loading onto the floor (newest per room).
        private readonly Dictionary<string, bool> _pendingSealStates = new Dictionary<string, bool>();
        // Rooms we sealed because the host did - unsealed again if the session ends mid-fight.
        private readonly List<RoomHandler> _networkSealedRooms = new List<RoomHandler>();

        private GameManager _subscribedTo;

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return;
            EnsureSubscribed(gm);

            if (NetworkSession.Instance.IsHost)
            {
                HostUpdate();
            }
            else if (NetworkSession.Instance.IsClient && !gm.IsLoadingLevel && !gm.IsFoyer)
            {
                // Catches native enemies that appear after load (e.g. spawned by room events).
                RoomHandler room = CurrentRoom();
                if (room != null && !IsBossRoom(room)) RemoveNativeEnemies(room);
            }
        }

        private void EnsureSubscribed(GameManager gm)
        {
            if (_subscribedTo == gm) return;
            if (_subscribedTo != null) _subscribedTo.OnNewLevelFullyLoaded -= OnLevelLoaded;
            gm.OnNewLevelFullyLoaded += OnLevelLoaded;
            _subscribedTo = gm;
        }

        private void OnLevelLoaded()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsFoyer || gm.Dungeon == null || gm.Dungeon.data == null) return;

            // Hooked on every level regardless of role - the handler checks for host at fire time,
            // so a session started mid-level still reports clears.
            foreach (RoomHandler room in gm.Dungeon.data.rooms)
            {
                RoomHandler captured = room;
                room.OnEnemiesCleared = (Action)Delegate.Combine(room.OnEnemiesCleared, new Action(() => OnHostRoomCleared(captured)));
                room.OnSealChanged = (Action<bool>)Delegate.Combine(room.OnSealChanged, new Action<bool>(isSealed => OnHostRoomSealChanged(captured, isSealed)));
            }

            // Rooms from the previous level are gone.
            _hostSealedRoom = null;
            _networkSealedRooms.Clear();

            if (NetworkSession.Instance.IsClient)
            {
                SuppressNativeFights(gm);
                ReplayPendingSpawns();
                ReplayPendingSealStates();
            }
        }

        private static RoomHandler CurrentRoom()
        {
            PlayerController player = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            return player != null ? player.CurrentRoom : null;
        }

        private static bool IsBossRoom(RoomHandler room)
        {
            return room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
        }

        private static RoomHandler FindRoomByName(string roomName)
        {
            GameManager gm = GameManager.Instance;
            if (string.IsNullOrEmpty(roomName) || gm == null || gm.Dungeon == null || gm.Dungeon.data == null) return null;
            foreach (RoomHandler room in gm.Dungeon.data.rooms)
            {
                if (room.GetRoomName() == roomName) return room;
            }
            return null;
        }

        // ---- Host ----

        private void HostUpdate()
        {
            RoomHandler room = CurrentRoom();
            if (room == null) return;

            string roomName = room.GetRoomName();
            if (roomName != _currentRoomName)
            {
                _currentRoomName = roomName;
                OnRoomChanged(roomName);
            }

            if (IsBossRoom(room)) return; // bosses: each player fights its own for now

            if (Time.time >= _nextStateSyncTime)
            {
                _nextStateSyncTime = Time.time + StateSyncInterval;
                SyncEnemyStates(room);
            }
        }

        private void OnRoomChanged(string roomName)
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

        private void OnHostRoomCleared(RoomHandler room)
        {
            if (!NetworkSession.Instance.IsHost) return;
            string roomName = room.GetRoomName();
            NetworkSession.Instance.Broadcast(new RoomClearedPacket { RoomName = roomName }, reliable: true);
            Debug.Log($"[EnemyReplicator] Host cleared room {roomName}.");
        }

        private void OnHostRoomSealChanged(RoomHandler room, bool isSealed)
        {
            // The client's own SealRoom/UnsealRoom calls fire this too - only the host reports.
            if (!NetworkSession.Instance.IsHost) return;

            string roomName = room.GetRoomName();
            if (isSealed) _hostSealedRoom = roomName;
            else if (_hostSealedRoom == roomName) _hostSealedRoom = null;

            NetworkSession.Instance.Broadcast(new RoomSealStatePacket { RoomName = roomName, Sealed = isSealed }, reliable: true);
            Debug.Log($"[EnemyReplicator] Host room {roomName} {(isSealed ? "sealed" : "unsealed")}.");
        }

        /// <summary>Host: a joining client learns about a fight already in progress.</summary>
        public void SendCurrentRoomStateTo(ulong targetId)
        {
            if (_hostSealedRoom == null) return;
            NetworkSession.Instance.SendPacket(targetId, new RoomSealStatePacket { RoomName = _hostSealedRoom, Sealed = true }, reliable: true);
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
            if (enemies != null)
            {
                foreach (AIActor enemy in enemies)
                {
                    // A dying enemy can still be in the active list for a frame or two - treat it as gone.
                    if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead) continue;
                    if (enemy.healthHaver.IsBoss) continue; // e.g. a miniboss outside a boss room

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

        // ---- Client: suppress native fights ----

        private void SuppressNativeFights(GameManager gm)
        {
            if (ReinforcementLayersField == null)
            {
                Debug.LogWarning("[EnemyReplicator] RoomHandler.remainingReinforcementLayers not found - client rooms may still spawn their own waves.");
            }

            int removed = 0;
            foreach (RoomHandler room in gm.Dungeon.data.rooms)
            {
                if (IsBossRoom(room)) continue;

                removed += RemoveNativeEnemies(room);
                var layers = ReinforcementLayersField != null ? ReinforcementLayersField.GetValue(room) as System.Collections.IList : null;
                if (layers != null) layers.Clear();
            }
            Debug.LogInfo($"[EnemyReplicator] Removed {removed} native enemies and all reinforcement waves; this client fights the host's enemies only.");
        }

        /// <summary>
        /// Removes the client's own enemies from a room the way ETG's enemy-replacement code does:
        /// deregister with clear checks suppressed, then destroy - so no clear/wave/reward fires.
        /// Leaves puppets, companions, non-enemies, and bosses alone.
        /// </summary>
        private static int RemoveNativeEnemies(RoomHandler room)
        {
            List<AIActor> active = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (active == null || active.Count == 0) return 0;

            int removed = 0;
            // Live list, and DeregisterEnemy removes from it - iterate backwards instead of copying
            // (this runs every frame on the client's current room).
            for (int i = active.Count - 1; i >= 0; i--)
            {
                AIActor actor = active[i];
                if (actor == null) continue;
                if (actor.GetComponent<NetworkPuppet>() != null) continue;
                if (actor.CompanionOwner != null || !actor.IsNormalEnemy) continue;
                if (actor.healthHaver != null && actor.healthHaver.IsBoss) continue;

                room.DeregisterEnemy(actor, suppressClearChecks: true);
                Object.Destroy(actor.gameObject);
                removed++;
            }
            return removed;
        }

        // ---- Client: host enemies as puppets ----

        public void HandleRoomChange(RoomChangePacket packet)
        {
            _hostRoomName = packet.RoomName ?? "";
            NetworkEntityManager.Instance.Clear();
            _pendingSpawns.Clear(); // spawns buffered for the previous room are stale now
        }

        public void HandleSpawn(EnemySpawnPacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel || gm.Dungeon == null)
            {
                // Still following the host onto this floor - spawn once our level has loaded and
                // our native enemies are gone.
                _pendingSpawns.Add(packet);
                return;
            }
            SpawnPuppet(packet);
        }

        private void ReplayPendingSpawns()
        {
            if (_pendingSpawns.Count == 0) return;
            var spawns = new List<EnemySpawnPacket>(_pendingSpawns);
            _pendingSpawns.Clear();
            foreach (EnemySpawnPacket spawn in spawns) SpawnPuppet(spawn);
        }

        private void SpawnPuppet(EnemySpawnPacket packet)
        {
            // Never stack a second copy on an id we already have - AddRemote would orphan the first.
            if (NetworkEntityManager.Instance.GetRemote(packet.EnemyId) != null) return;

            AIActor prefab = EnemyDatabase.GetOrLoadByGuid(packet.EnemyGuid);
            if (prefab == null)
            {
                Debug.LogWarning($"[EnemyReplicator] Unknown enemy guid {packet.EnemyGuid}, cannot spawn.");
                return;
            }

            // The host's room, by name - requires the shared seed to have produced the same layout.
            // Falls back to wherever we are if it can't be found.
            RoomHandler room = FindRoomByName(_hostRoomName) ?? CurrentRoom();
            if (room == null || IsBossRoom(room)) return;

            AIActor spawned = AIActor.Spawn(prefab, packet.Position, room);
            if (spawned == null) return;

            MakePuppet(spawned);
            NetworkEntityManager.Instance.AddRemote(packet.EnemyId, spawned.gameObject);
        }

        private static void MakePuppet(AIActor actor)
        {
            actor.gameObject.AddComponent<NetworkPuppet>();

            // No local AI: the host decides movement and attacks.
            if (actor.behaviorSpeculator != null) actor.behaviorSpeculator.InterruptAndDisable();

            // Its destruction (on EnemyDeath) must never run the client's own room-clear logic.
            actor.IgnoreForRoomClear = true;

            // The client can't kill it locally (and drop loot); only the host's EnemyDeath removes it.
            if (actor.healthHaver != null) actor.healthHaver.PreventAllDamage = true;
        }

        public void HandleState(EnemyStatePacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            if (remote == null) return;

            remote.transform.position = packet.Position;
            remote.transform.rotation = Quaternion.Euler(0, 0, packet.Rotation);
        }

        public void HandleDeath(EnemyDeathPacket packet)
        {
            NetworkEntityManager.Instance.RemoveRemote(packet.EnemyId);
        }

        public void HandleRoomSealState(RoomSealStatePacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel || gm.Dungeon == null)
            {
                _pendingSealStates[packet.RoomName ?? ""] = packet.Sealed;
                return;
            }
            ApplySealState(packet.RoomName, packet.Sealed);
        }

        private void ReplayPendingSealStates()
        {
            if (_pendingSealStates.Count == 0) return;
            var states = new Dictionary<string, bool>(_pendingSealStates);
            _pendingSealStates.Clear();
            foreach (var kv in states) ApplySealState(kv.Key, kv.Value);
        }

        /// <summary>
        /// Mirrors the host's doors. The client's own rooms never seal by themselves (no local
        /// RoomClear enemies), so this is the only thing locking them. A client outside the room
        /// when it seals is locked out until the host clears it - same doors, same rules.
        /// </summary>
        private void ApplySealState(string roomName, bool isSealed)
        {
            RoomHandler room = FindRoomByName(roomName);
            if (room == null)
            {
                Debug.Log($"[EnemyReplicator] Seal state for unknown room {roomName} (layout mismatch?) - ignored.");
                return;
            }
            if (IsBossRoom(room)) return; // the client runs its own boss room, doors included

            if (isSealed && !room.IsSealed)
            {
                room.SealRoom();
                if (!_networkSealedRooms.Contains(room)) _networkSealedRooms.Add(room);
            }
            else if (!isSealed && room.IsSealed)
            {
                room.UnsealRoom();
                _networkSealedRooms.Remove(room);
            }
        }

        public void HandleRoomCleared(RoomClearedPacket packet)
        {
            RoomHandler room = FindRoomByName(packet.RoomName);
            if (room != null && IsBossRoom(room)) return; // the client fights its own boss

            // Same player effects as a local clear (active-item recharge etc.), but no room reward:
            // loot isn't synced yet, so a local roll would hand out items the host never sees.
            PlayerController player = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            if (player != null) player.OnRoomCleared();

            // Normally a RoomSealState(false) handles this; belt and braces in case it was missed.
            if (room != null && room.IsSealed)
            {
                room.UnsealRoom();
                _networkSealedRooms.Remove(room);
            }
            Debug.Log($"[EnemyReplicator] Host cleared room {packet.RoomName}.");
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            // Don't leave the client locked in a room whose fight will never finish: the host that
            // was going to clear it is gone.
            GameManager gm = GameManager.Instance;
            if (gm != null && !gm.IsLoadingLevel)
            {
                foreach (RoomHandler room in _networkSealedRooms)
                {
                    try
                    {
                        if (room != null && room.IsSealed) room.UnsealRoom();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[EnemyReplicator] Failed to unseal a room on session end: {e.Message}");
                    }
                }
            }
            _networkSealedRooms.Clear();

            _currentRoomName = "";
            _liveIds.Clear();
            _hostSealedRoom = null;
            _hostRoomName = "";
            _pendingSpawns.Clear();
            _pendingSealStates.Clear();
        }
    }
}
