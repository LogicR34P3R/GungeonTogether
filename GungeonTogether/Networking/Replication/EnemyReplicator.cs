using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Players;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Transport;
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
    /// no local room-clear roll - the host's reward arrives via LootReplicator. Client doors mirror the host's via RoomSealState.
    ///
    /// Bosses (step 4c-1): the client keeps its own native bosses but neutralises them as id-less
    /// puppets on level load, then adopts the matching one as the puppet of the host's boss - keeping
    /// its intro, room hooks and health bar, which tracks the host's boss health. A boss's death
    /// removes the puppet abruptly (no death sequence yet), and the host's Dungeon.FloorCleared is
    /// replayed on the client.
    /// </summary>
    public class EnemyReplicator : MonoSingleton<EnemyReplicator>
    {
        private const float StateSyncInterval = 0.05f; // 20 Hz; clients smooth and extrapolate between updates (NetworkPuppet)

        // RoomHandler keeps its pending reinforcement waves private; there's no public way to cancel them.
        private static readonly FieldInfo ReinforcementLayersField =
            typeof(RoomHandler).GetField("remainingReinforcementLayers", BindingFlags.NonPublic | BindingFlags.Instance);

        // Host side.
        private string _currentRoomName = "";
        private float _nextStateSyncTime;
        // Every enemy the clients have (id -> enemy) for this level; see SyncEnemyStates.
        private readonly Dictionary<int, AIActor> _synced = new Dictionary<int, AIActor>();
        private readonly HashSet<int> _currentIds = new HashSet<int>();
        private readonly List<int> _goneIds = new List<int>();
        // Dead enemies still playing their death (id -> when it started); see SyncEnemyStates.
        private readonly Dictionary<int, float> _dyingSince = new Dictionary<int, float>();
        private const float MaxDyingSeconds = 8f;
        private readonly List<RoomHandler> _syncedRooms = new List<RoomHandler>();
        // Each client's current room (ClientEnteredRoom), by name.
        private readonly Dictionary<ulong, string> _clientRooms = new Dictionary<ulong, string>();

        // Host side: the room whose doors are sealed right now, for a joining client's snapshot.
        private string _hostSealedRoom;

        // Client side.
        private string _hostRoomName = "";
        private readonly List<EnemySpawnPacket> _pendingSpawns = new List<EnemySpawnPacket>();
        // Seal states that arrived while we were still loading onto the floor (newest per room).
        private readonly Dictionary<string, bool> _pendingSealStates = new Dictionary<string, bool>();
        // Rooms we sealed because the host did - unsealed again if the session ends mid-fight.
        private readonly List<RoomHandler> _networkSealedRooms = new List<RoomHandler>();
        // Rooms the host sealed while this client was still outside - sealed here once it walks in.
        private readonly List<RoomHandler> _heldSeals = new List<RoomHandler>();

        // Client side: every puppet, including native bosses awaiting adoption - see EnforcePuppets.
        private static readonly List<AIActor> _puppets = new List<AIActor>();

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
                if (room != null) RemoveNativeEnemies(room);
                EnforcePuppets();
                HideEmptiedBossBars();
                ApplyHeldSeal(room);
                ReportRoomChange(room);
            }
        }

        // Client: last room reported to the host via ClientEnteredRoom.
        private RoomHandler _reportedRoom;

        private void ReportRoomChange(RoomHandler room)
        {
            if (room == null || room == _reportedRoom) return;
            _reportedRoom = room;
            NetworkSession.Instance.SendToHost(new ClientEnteredRoomPacket { RoomName = room.GetRoomName() }, reliable: true);
        }

        /// <summary>
        /// Host: a client changed rooms; that room's enemies get synced from now on (SyncedRooms).
        /// Nobody is warped: doors wait for both players (PlayerReplicator.CanOpenDoor), so the
        /// players go in together and the host's own room entry wakes the enemies.
        /// </summary>
        public void HandleClientEnteredRoom(ulong senderId, ClientEnteredRoomPacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer || gm.Dungeon == null) return;

            _clientRooms[senderId] = packet.RoomName ?? "";
            _nextStateSyncTime = 0f;
        }

        /// <summary>
        /// A puppet's AI must stay off, but game code can switch it back on - notably a boss's intro
        /// sequence when it ends. Cheap: only a handful of puppets exist at a time.
        /// </summary>
        private static void EnforcePuppets()
        {
            for (int i = _puppets.Count - 1; i >= 0; i--)
            {
                AIActor puppet = _puppets[i];
                if (puppet == null)
                {
                    _puppets.RemoveAt(i);
                    continue;
                }
                if (puppet.behaviorSpeculator != null && puppet.behaviorSpeculator.enabled)
                {
                    puppet.behaviorSpeculator.InterruptAndDisable();
                }
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

            // Rooms and enemies from the previous level are gone.
            _hostSealedRoom = null;
            _networkSealedRooms.Clear();
            _heldSeals.Clear();
            _synced.Clear();
            _dyingSince.Clear();
            _clientRooms.Clear();
            _bossBars.Clear();
            if (_hideBossBarsAt >= 0f) HideBossHealthBars(); // left the floor before it was hidden
            _hideBossBarsAt = -1f;
            _currentRoomName = "";
            _reportedRoom = null;
            NetworkEntityManager.Instance.Clear();

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

            if (Time.time >= _nextStateSyncTime)
            {
                _nextStateSyncTime = Time.time + StateSyncInterval;
                SyncEnemyStates();
            }
        }

        private void OnRoomChanged(string roomName)
        {
            // Just the fallback room for a spawn that names none; ids live for the whole level.
            NetworkSession.Instance.Broadcast(new RoomChangePacket { RoomName = roomName });
            // Sync the new room's enemies this frame rather than waiting out the interval.
            _nextStateSyncTime = 0f;
        }

        // A target switch needs the other player this much closer, so an enemy between two players
        // doesn't flip back and forth (and interrupt its attacks) every frame.
        private const float RetargetMargin = 1f;

        /// <summary>
        /// Host: TargetPlayerBehavior.Update postfix. The game picks targets from local players only,
        /// so remote players were never attacked, and with the host a ghost nobody was. Re-picks the
        /// nearest living player - the host, or a remote player's stand-in in the enemy's room.
        /// </summary>
        public static void OnTargetSearch(BehaviorSpeculator speculator)
        {
            if (!NetworkSession.Instance.IsHost || speculator == null) return;
            AIActor enemy = speculator.aiActor;
            if (enemy == null || enemy.CompanionOwner != null || !enemy.CanTargetPlayers || enemy.CanTargetEnemies) return;
            if (enemy.specRigidbody == null) return;

            Vector2 from = enemy.specRigidbody.UnitCenter;
            GameActor current = speculator.PlayerTarget;
            GameActor best = null;
            float bestDistance = float.MaxValue, currentDistance = float.MaxValue;

            void Consider(GameActor candidate)
            {
                float distance = Vector2.Distance(from, candidate.CenterPosition);
                if (candidate == current) currentDistance = distance;
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            PlayerController host = GameManager.Instance.PrimaryPlayer;
            if (host != null && !host.IsGhost && !host.healthHaver.IsDead && !host.IsFalling && !host.IsStealthed) Consider(host);

            DungeonData data = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data : null;
            foreach (RemotePlayerTarget target in PlayerReplicator.Instance.LivingEnemyTargets())
            {
                // Only in the enemy's own room: the stand-in is never in a room for the game's own
                // room logic, and enemies shouldn't chase a player through walls.
                RoomHandler targetRoom = data != null ? data.GetAbsoluteRoomFromPosition(target.CenterPosition.ToIntVector2(VectorConversions.Floor)) : null;
                if (targetRoom != null && targetRoom == enemy.ParentRoom) Consider(target);
            }

            if (best == null)
            {
                // Everyone left or died; drop a stand-in the game itself would never clear.
                if (current is RemotePlayerTarget) speculator.PlayerTarget = null;
                return;
            }
            if (best == current || currentDistance <= bestDistance + RetargetMargin) return;

            speculator.PlayerTarget = best;
            enemy.HasBeenEngaged = true;
            if (enemy.aiShooter != null) enemy.aiShooter.AimAtPoint(best.CenterPosition);
        }

        private void OnHostRoomCleared(RoomHandler room)
        {
            if (!NetworkSession.Instance.IsHost) return;
            string roomName = room.GetRoomName();
            NetworkSession.Instance.Broadcast(new RoomClearedPacket { RoomName = roomName }, reliable: true);
            Debug.Log($"[EnemyReplicator] Host cleared room {roomName}.");
        }

        /// <summary>Dungeon.FloorCleared postfix (its floor boss died). The client never kills a boss itself.</summary>
        public static void OnFloorCleared()
        {
            if (!NetworkSession.Instance.IsHost) return;
            NetworkSession.Instance.Broadcast(new FloorClearedPacket(), reliable: true);
            Debug.LogInfo("[EnemyReplicator] Host cleared the floor.");
            PlayerLifeReplicator.Instance.OnFloorCleared(); // vanilla revives co-op ghosts on the boss kill
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

        /// <summary>
        /// Host: the id clients know an attacking enemy by, announcing it right now if the 20 Hz sync
        /// hasn't yet. An enemy attacking between appearing (wave, summon) and its first sync had no
        /// id, so that attack was never sent. The spawn goes out ahead of the attack on the same
        /// reliable, ordered channel, so the client has the puppet before the script start arrives.
        /// </summary>
        public bool TryGetSyncedId(AIActor enemy, out int id)
        {
            if (NetworkEntityManager.Instance.TryGetId(enemy, out id)) return true;
            if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead) return false;

            RoomHandler room = enemy.ParentRoom;
            if (room == null || !SyncedRooms().Contains(room) || !IsActiveIn(enemy, room)) return false;
            if (string.IsNullOrEmpty(enemy.EnemyGuid)) return false;

            id = NetworkEntityManager.Instance.GetOrAssignId(enemy);
            _synced[id] = enemy;
            BroadcastSpawn(enemy, room);
            return true;
        }

        private void BroadcastSpawn(AIActor enemy, RoomHandler room)
        {
            // The EnemyDatabase guid - what the client's GetOrLoadByGuid and boss adoption look up.
            // Not encounterTrackable.EncounterGuid: that's the Ammonomicon entry, which differs for
            // many enemies, so their puppets never spawned on clients.
            string guid = enemy.EnemyGuid;
            if (string.IsNullOrEmpty(guid)) return;

            int id = NetworkEntityManager.Instance.GetOrAssignId(enemy);
            var packet = new EnemySpawnPacket
            {
                EnemyId = id,
                EnemyGuid = guid,
                Position = enemy.transform.position,
                Rotation = enemy.transform.eulerAngles.z,
                Health = enemy.healthHaver != null ? Mathf.RoundToInt(enemy.healthHaver.GetCurrentHealth()) : 0,
                IsBoss = enemy.healthHaver != null && enemy.healthHaver.IsBoss,
                RoomName = room.GetRoomName()
            };
            NetworkSession.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// The rooms whose enemies are synced: every room a player is in - the host's, and each
        /// client's last reported room. Only syncing the host's room left a client alone in a
        /// hallway (which doesn't seal) facing enemies it couldn't see, hit, or be hit by.
        /// </summary>
        private List<RoomHandler> SyncedRooms()
        {
            _syncedRooms.Clear();
            RoomHandler hostRoom = CurrentRoom();
            if (hostRoom != null) _syncedRooms.Add(hostRoom);
            foreach (string roomName in _clientRooms.Values)
            {
                RoomHandler room = FindRoomByName(roomName);
                if (room != null && !_syncedRooms.Contains(room)) _syncedRooms.Add(room);
            }
            return _syncedRooms;
        }

        private void SyncEnemyStates()
        {
            _currentIds.Clear();

            foreach (RoomHandler room in SyncedRooms())
            {
                List<AIActor> enemies = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
                if (enemies == null) continue;
                foreach (AIActor enemy in enemies)
                {
                    // A dying enemy can still be in the active list for a frame or two - treat it as gone.
                    if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead) continue;

                    int id = NetworkEntityManager.Instance.GetOrAssignId(enemy);
                    if (!_currentIds.Add(id)) continue;

                    // New to the clients: its room just got a player in it, or it appeared mid-room
                    // (reinforcement wave, summon).
                    if (!_synced.ContainsKey(id))
                    {
                        _synced[id] = enemy;
                        BroadcastSpawn(enemy, room);
                    }

                    NetworkSession.Instance.Broadcast(StatePacket(id, enemy, dying: false), reliable: false);
                }
            }

            // Synced before but not seen this tick. Only a real death or disappearance ends it: an
            // enemy whose room merely has no player in it any more stays, and its client copy just
            // waits for the next update. Deaths used to be inferred from "not in the host's room",
            // so any death after the host walked out was never sent (seen with a Bullet Bros twin).
            _goneIds.Clear();
            foreach (var kv in _synced)
            {
                if (_currentIds.Contains(kv.Key)) continue;
                AIActor enemy = kv.Value;
                bool killed = enemy != null && enemy.healthHaver != null && enemy.healthHaver.IsDead;

                // Dead but still there: its death is playing. Keep sending its animation so the
                // client shows the real death (not a stand-in), until the object goes or it's been long enough.
                if (killed)
                {
                    if (!_dyingSince.TryGetValue(kv.Key, out float since)) _dyingSince[kv.Key] = since = Time.time;
                    if (Time.time - since < MaxDyingSeconds)
                    {
                        NetworkSession.Instance.Broadcast(StatePacket(kv.Key, enemy, dying: true), reliable: false);
                        continue;
                    }
                }

                bool vanished = enemy == null || enemy.ParentRoom == null || !IsActiveIn(enemy, enemy.ParentRoom);
                if (!killed && !vanished) continue;

                _goneIds.Add(kv.Key);
                killed |= _dyingSince.ContainsKey(kv.Key); // destroyed at the end of its death
                _dyingSince.Remove(kv.Key);
                NetworkSession.Instance.Broadcast(new EnemyDeathPacket { EnemyId = kv.Key, Killed = killed }, reliable: true);
                bool isBoss = enemy != null && enemy.healthHaver != null && enemy.healthHaver.IsBoss;
                if (isBoss) Debug.LogInfo($"[EnemyReplicator] Boss {kv.Key} {(killed ? "died" : "left")}, broadcast EnemyDeath.");
                else Debug.Log($"[EnemyReplicator] Enemy {kv.Key} {(killed ? "died" : "left")}, broadcast EnemyDeath.");
            }
            foreach (int id in _goneIds) _synced.Remove(id);
        }

        /// <summary>
        /// Position, health, and what the enemy looks like right now: its animation clip and frame,
        /// facing, and gun aim. The puppet's AI is off, so without these it never showed attacks,
        /// charge-ups (e.g. a bomb enemy about to blow) or its real death.
        /// </summary>
        private static EnemyStatePacket StatePacket(int id, AIActor enemy, bool dying)
        {
            tk2dSpriteAnimator animator = enemy.spriteAnimator;
            tk2dSpriteAnimationClip clip = animator != null ? animator.CurrentClip : null;
            Gun gun = enemy.aiShooter != null ? enemy.aiShooter.CurrentGun : null;
            return new EnemyStatePacket
            {
                EnemyId = id,
                Position = enemy.transform.position,
                Rotation = enemy.transform.eulerAngles.z,
                Health = Mathf.RoundToInt(enemy.healthHaver.GetCurrentHealth()),
                MaxHealth = Mathf.RoundToInt(enemy.healthHaver.GetMaxHealth()),
                AIState = (int)enemy.State,
                Clip = clip != null ? clip.name ?? "" : "",
                Frame = clip != null ? animator.CurrentFrame : 0,
                FlipX = enemy.sprite != null && enemy.sprite.FlipX,
                Dying = dying,
                HasGun = gun != null,
                GunAngle = gun != null ? gun.CurrentAngle : 0f,
                TargetId = TargetIdOf(enemy.PlayerTarget)
            };
        }

        /// <summary>Host: the steam id of the player an enemy targets - the host itself, or a client's stand-in.</summary>
        internal static ulong TargetIdOf(GameActor target)
        {
            if (target is PlayerController) return SteamIdentity.GetLocalSteamId();
            RemotePlayerTarget standIn = target as RemotePlayerTarget;
            return standIn != null ? standIn.SteamId : 0UL;
        }

        private static bool IsActiveIn(AIActor enemy, RoomHandler room)
        {
            List<AIActor> active = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            return active != null && active.Contains(enemy);
        }

        // ---- Client: suppress native fights ----

        private void SuppressNativeFights(GameManager gm)
        {
            if (ReinforcementLayersField == null)
            {
                Debug.LogWarning("[EnemyReplicator] RoomHandler.remainingReinforcementLayers not found - client rooms may still spawn their own waves.");
            }

            int removed = 0, bosses = 0;
            foreach (RoomHandler room in gm.Dungeon.data.rooms)
            {
                removed += RemoveNativeEnemies(room);
                bosses += NeutraliseNativeBosses(room);
                var layers = ReinforcementLayersField != null ? ReinforcementLayersField.GetValue(room) as System.Collections.IList : null;
                if (layers != null) layers.Clear();
            }
            Debug.LogInfo($"[EnemyReplicator] Removed {removed} native enemies and all reinforcement waves, neutralised {bosses} native boss(es); this client fights the host's enemies only.");
        }

        /// <summary>
        /// Native bosses are kept rather than removed: each is later adopted as the puppet of the
        /// host's boss (HandleSpawn), which keeps its intro sequence, room hooks and health bar
        /// intact. Until then it's made a harmless puppet with no id, so the client never fights a
        /// boss of its own - even if it walks in before the host does.
        /// </summary>
        private static int NeutraliseNativeBosses(RoomHandler room)
        {
            List<AIActor> active = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (active == null) return 0;

            int count = 0;
            foreach (AIActor actor in active)
            {
                if (actor == null || actor.healthHaver == null || !actor.healthHaver.IsBoss) continue;
                if (actor.GetComponent<NetworkPuppet>() != null) continue;
                MakePuppet(actor, enemyId: 0);
                count++;
            }
            return count;
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

        /// <summary>
        /// Only records the host's room now. It used to destroy every puppet: enemies were only
        /// synced from the host's room, so each host room change wiped the client's copies.
        /// </summary>
        public void HandleRoomChange(RoomChangePacket packet)
        {
            _hostRoomName = packet.RoomName ?? "";
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

            // The enemy's own host room, by name - requires the shared seed to have produced the same
            // layout. Falls back to the host's room, then wherever we are.
            RoomHandler room = FindRoomByName(packet.RoomName) ?? FindRoomByName(_hostRoomName) ?? CurrentRoom();
            if (room == null) return;

            if (packet.IsBoss && TryAdoptNativeBoss(room, packet)) return;

            AIActor spawned = AIActor.Spawn(prefab, packet.Position, room);
            if (spawned == null) return;

            MakePuppet(spawned, packet.EnemyId);
            NetworkEntityManager.Instance.AddRemote(packet.EnemyId, spawned.gameObject);

            // Many enemies spawn hidden until they wake (invisibleUntilAwaken: renderers off, no
            // collisions, IsGone) and wake when their AI first engages a player - which a puppet's
            // never does, so they stayed invisible and unhittable for the whole fight. Engaging runs
            // AIActor.OnEngaged: visible, plays its appear animation, and AIActor.Update restores
            // the hitbox once that ends. Bosses are left to their intro.
            if (!packet.IsBoss) spawned.HasBeenEngaged = true;
        }

        // Client: boss health bars already shown.
        private static readonly HashSet<HealthHaver> _bossBars = new HashSet<HealthHaver>();

        /// <summary>
        /// A boss's bar is registered by AIActor.OnEngaged, which a puppet (AI off) never reaches -
        /// so the client saw no boss health bar. Registered the same way here, once its intro is over.
        /// </summary>
        private static void ShowBossBar(AIActor actor, HealthHaver health)
        {
            if (_bossBars.Contains(health) || !health.HasHealthBar || GameManager.IsBossIntro || !GameUIRoot.HasInstance) return;
            _bossBars.Add(health);

            GameUIRoot ui = GameUIRoot.Instance;
            GameUIBossHealthController bar = health.UsesVerticalBossBar ? ui.bossControllerSide
                : health.UsesSecondaryBossBar ? ui.bossController2 : ui.bossController;
            if (bar == null) return;
            string bossName = !string.IsNullOrEmpty(health.overrideBossName)
                ? StringTableManager.GetEnemiesString(health.overrideBossName)
                : actor.GetActorName();
            bar.RegisterBossHealthHaver(health, bossName);
        }

        // How long an emptied bar stays on screen before it's hidden, so the drop to 0 is seen.
        private const float EmptyBarSeconds = 2f;
        private static float _hideBossBarsAt = -1f;

        /// <summary>
        /// A dying boss's bar drops to 0 (deregistering does that); the rest stay up while another
        /// boss (e.g. the other twin) lives. Once none is left, the bar is hidden a moment later.
        /// </summary>
        private static void EmptyBossBar(HealthHaver health)
        {
            if (!_bossBars.Remove(health)) return;
            GameUIRoot ui = GameUIRoot.HasInstance ? GameUIRoot.Instance : null;
            if (ui != null)
            {
                if (ui.bossController != null) ui.bossController.DeregisterBossHealthHaver(health);
                if (ui.bossController2 != null) ui.bossController2.DeregisterBossHealthHaver(health);
                if (ui.bossControllerSide != null) ui.bossControllerSide.DeregisterBossHealthHaver(health);
            }
            _bossBars.RemoveWhere(h => h == null);
            if (_bossBars.Count == 0) _hideBossBarsAt = Time.realtimeSinceStartup + EmptyBarSeconds;
        }

        /// <summary>The boss is gone: empty its bar if that hasn't happened yet.</summary>
        private static void RemoveBossBar(HealthHaver health)
        {
            EmptyBossBar(health);
            _bossBars.RemoveWhere(h => h == null);
            if (_bossBars.Count == 0 && _hideBossBarsAt < 0f) _hideBossBarsAt = Time.realtimeSinceStartup + EmptyBarSeconds;
        }

        /// <summary>Client: hides the emptied boss bar once its moment on screen is over.</summary>
        private static void HideEmptiedBossBars()
        {
            if (_hideBossBarsAt < 0f || Time.realtimeSinceStartup < _hideBossBarsAt) return;
            _hideBossBarsAt = -1f;
            if (_bossBars.Count == 0) HideBossHealthBars();
        }

        /// <summary>Links the host's boss to our own neutralised copy of it (same guid, same room).</summary>
        private static bool TryAdoptNativeBoss(RoomHandler room, EnemySpawnPacket packet)
        {
            List<AIActor> active = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (active == null) return false;

            foreach (AIActor actor in active)
            {
                if (actor == null || actor.EnemyGuid != packet.EnemyGuid) continue;
                NetworkPuppet puppet = actor.GetComponent<NetworkPuppet>();
                if (puppet == null || puppet.EnemyId != 0) continue; // not a native boss awaiting adoption

                puppet.EnemyId = packet.EnemyId;
                NetworkEntityManager.Instance.AddRemote(packet.EnemyId, actor.gameObject);
                Debug.LogInfo($"[EnemyReplicator] Adopted native boss {actor.EnemyGuid} as the host's boss {packet.EnemyId}.");
                return true;
            }
            Debug.LogWarning($"[EnemyReplicator] No native boss {packet.EnemyGuid} to adopt in {room.GetRoomName()} - spawning one instead.");
            return false;
        }

        /// <param name="enemyId">The host enemy it stands for, or 0 for a native boss not yet adopted.</param>
        private static void MakePuppet(AIActor actor, int enemyId)
        {
            actor.gameObject.AddComponent<NetworkPuppet>().EnemyId = enemyId;
            _puppets.Add(actor);

            // No local AI: the host decides movement and attacks.
            if (actor.behaviorSpeculator != null) actor.behaviorSpeculator.InterruptAndDisable();

            // Its destruction (on EnemyDeath) must never run the client's own room-clear logic.
            actor.IgnoreForRoomClear = true;

            // The client can't kill it locally (and drop loot); only the host's EnemyDeath removes it.
            // Hits still count: DamageReplicator forwards them to the host's real enemy.
            if (actor.healthHaver != null) actor.healthHaver.PreventAllDamage = true;
        }

        public void HandleState(EnemyStatePacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            if (remote == null) return;

            // Smoothed and extrapolated by the puppet itself (which also keeps its hitbox in step).
            NetworkPuppet puppet = remote.GetComponent<NetworkPuppet>();
            if (puppet != null)
            {
                float pingMs = NetworkSession.Instance.GetPingMs();
                puppet.ApplyState(packet.Position, pingMs > 0f ? pingMs / 2000f : 0f);
            }
            remote.transform.rotation = Quaternion.Euler(0, 0, packet.Rotation);
            AIActor lookActor = remote.GetComponent<AIActor>();
            if (lookActor != null) ApplyLook(lookActor, puppet, packet);

            // Boss health bars read the puppet's own HealthHaver, which never takes local damage -
            // mirror the host's boss instead. ForceSetCurrentHealth can't kill (death comes from
            // EnemyDeath), and 0 is skipped anyway.
            HealthHaver health = remote.GetComponent<HealthHaver>();
            if (health != null && health.IsBoss && packet.Health > 0 && packet.MaxHealth > 0)
            {
                if (Mathf.Abs(health.GetMaxHealth() - packet.MaxHealth) > 0.5f) health.SetHealthMaximum(packet.MaxHealth);
                if (Mathf.Abs(health.GetCurrentHealth() - packet.Health) > 0.5f) health.ForceSetCurrentHealth(packet.Health);
                AIActor bossActor = remote.GetComponent<AIActor>();
                if (bossActor != null) ShowBossBar(bossActor, health);
            }
            // Dead on the host but still playing its death: its health (0) is skipped above, so the
            // bar would stay at its last sliver. Empty it now, as the game does when a boss dies.
            else if (health != null && health.IsBoss && (packet.Dying || (packet.MaxHealth > 0 && packet.Health <= 0)))
            {
                EmptyBossBar(health);
            }
        }

        /// <summary>
        /// Ends the puppet's wake-up once the host's enemy is awake. AIActor.Update ends it when the
        /// awaken animation stops, asked through AIAnimator.IsPlaying - and with the AIAnimator off,
        /// its action state never clears while the host's clips keep playing, so the puppet stayed
        /// Awakening for good. Enemies that spawn hidden (invisibleUntilAwaken) then stayed IsGone
        /// with no collisions and their gun hidden: bullets went through them like ghosts.
        /// Does what AIActor.Update would; its next frame restores collisions and IsGone.
        /// </summary>
        private static void FinishAwakening(AIActor actor, EnemyStatePacket packet)
        {
            if (actor.State != AIActor.ActorState.Awakening) return;
            var hostState = (AIActor.ActorState)packet.AIState;
            if (hostState == AIActor.ActorState.Inactive || hostState == AIActor.ActorState.Awakening) return;

            if (actor.aiShooter != null)
            {
                actor.aiShooter.ToggleGunAndHandRenderers(true, "Reinforce");
                actor.aiShooter.ToggleGunAndHandRenderers(true, "Awaken");
            }
            actor.State = AIActor.ActorState.Normal;
        }

        /// <summary>
        /// Shows the host enemy's animation on its puppet: the clip it's playing (switched when the
        /// host switches, then run locally at the clip's own speed), facing and gun aim. The
        /// puppet's AIAnimator is switched off so it doesn't pick its own clips over the host's -
        /// for a boss only after its intro, which the AIAnimator drives.
        /// </summary>
        private static void ApplyLook(AIActor actor, NetworkPuppet puppet, EnemyStatePacket packet)
        {
            if (actor.healthHaver != null && actor.healthHaver.IsBoss && GameManager.IsBossIntro) return;
            if (actor.aiAnimator != null && actor.aiAnimator.enabled) actor.aiAnimator.enabled = false;
            FinishAwakening(actor, packet);

            tk2dSpriteAnimator animator = actor.spriteAnimator;
            if (animator != null && !string.IsNullOrEmpty(packet.Clip)
                && (animator.CurrentClip == null || animator.CurrentClip.name != packet.Clip))
            {
                tk2dSpriteAnimationClip clip = animator.GetClipByName(packet.Clip);
                if (clip != null && clip.frames != null && clip.frames.Length > 0)
                {
                    animator.PlayFromFrame(clip, Mathf.Clamp(packet.Frame, 0, clip.frames.Length - 1));
                }
            }
            if (actor.sprite != null) actor.sprite.FlipX = packet.FlipX;
            if (packet.HasGun && actor.aiShooter != null)
            {
                actor.aiShooter.AimInDirection(BraveMathCollege.DegreesToVector(packet.GunAngle));
            }

            // Same target as the host's enemy: replayed bullet scripts aim at the puppet's target
            // (BulletManager.PlayerPosition), and with the AI off it had none - scripts aimed at a
            // fallback point instead of at a player.
            GameActor target = TargetFor(packet.TargetId);
            if (target != null && actor.PlayerTarget != target) actor.PlayerTarget = target;

            if (packet.Dying && puppet != null && !puppet.SawDying)
            {
                // Dying on the host: nothing may hit it any more (a hit would go to a dead enemy).
                puppet.SawDying = true;
                if (actor.specRigidbody != null) actor.specRigidbody.enabled = false;
                if (actor.aiShooter != null) actor.aiShooter.ToggleGunAndHandRenderers(false, "death");
            }
        }

        /// <summary>Client: our own player, or the stand-in on another player's avatar.</summary>
        internal static GameActor TargetFor(ulong steamId)
        {
            if (steamId == 0) return null;
            if (steamId == SteamIdentity.GetLocalSteamId()) return GameManager.Instance.PrimaryPlayer;
            return PlayerReplicator.Instance.TryGetEnemyTarget(steamId, out RemotePlayerTarget standIn) ? standIn : null;
        }

        // Longest a dead puppet lingers for its death animation.
        private const float MaxDeathAnimSeconds = 3f;

        public void HandleDeath(EnemyDeathPacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            NetworkEntityManager.Instance.ForgetRemote(packet.EnemyId);
            if (remote == null) return;

            HealthHaver health = remote.GetComponent<HealthHaver>();
            if (health != null && health.IsBoss)
            {
                RemoveBossBar(health);
                Debug.LogInfo($"[EnemyReplicator] Host's boss {packet.EnemyId} {(packet.Killed ? "died" : "left")}.");
            }

            // AIActor.OnDestroy deliberately skips deregistering bosses, which would leave a destroyed
            // boss puppet in its room's enemy list - so deregister explicitly, clear checks suppressed.
            AIActor actor = remote.GetComponent<AIActor>();
            if (actor != null && actor.ParentRoom != null) actor.ParentRoom.DeregisterEnemy(actor, suppressClearChecks: true);

            // Normally the host's death animation already played here (EnemyState.Dying); the
            // stand-in death is only for an enemy whose dying frames never arrived.
            NetworkPuppet deadPuppet = remote.GetComponent<NetworkPuppet>();
            bool deathShown = deadPuppet != null && deadPuppet.SawDying;
            float linger = packet.Killed && actor != null && !deathShown ? PlayDeathVisuals(actor) : 0f;
            Destroy(remote, linger);
        }

        /// <summary>
        /// The look of a death only: the "death" animation and the death effect. The game's own
        /// HealthHaver.Die also runs on-death bullet scripts, loot and boss sequences - things that
        /// already happened for real on the host. Returns how long the corpse should stay.
        /// </summary>
        private static float PlayDeathVisuals(AIActor actor)
        {
            _puppets.Remove(actor);
            NetworkPuppet puppet = actor.GetComponent<NetworkPuppet>();
            if (puppet != null) puppet.enabled = false; // stop extrapolating the last velocity
            if (actor.specRigidbody != null) actor.specRigidbody.enabled = false; // nothing hits a corpse
            if (actor.aiShooter != null) actor.aiShooter.ToggleGunAndHandRenderers(false, "death");

            HealthHaver health = actor.healthHaver;
            if (health != null && health.deathEffect != null)
            {
                SpawnManager.SpawnVFX(health.deathEffect, actor.transform.position, Quaternion.identity);
            }

            if (actor.aiAnimator == null || !actor.aiAnimator.HasDirectionalAnimation("death")) return 0f;
            actor.aiAnimator.PlayUntilFinished("death");
            tk2dSpriteAnimationClip clip = actor.spriteAnimator != null ? actor.spriteAnimator.CurrentClip : null;
            return clip != null ? Mathf.Clamp(clip.BaseClipLength, 0f, MaxDeathAnimSeconds) : 0f;
        }

        public void HandleFloorCleared()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.Dungeon == null) return;
            // Every boss is dead (a twin may still be playing its death): empty the bar, hide it shortly.
            foreach (HealthHaver boss in new List<HealthHaver>(_bossBars)) EmptyBossBar(boss);
            if (_hideBossBarsAt < 0f) _hideBossBarsAt = Time.realtimeSinceStartup + EmptyBarSeconds;
            gm.Dungeon.FloorCleared();
            Debug.LogInfo("[EnemyReplicator] Floor cleared by the host.");
            PlayerLifeReplicator.Instance.OnFloorCleared();
        }

        /// <summary>The puppet boss is removed abruptly (no death sequence yet), so its bar is hidden by hand.</summary>
        private static void HideBossHealthBars()
        {
            GameUIRoot ui = GameUIRoot.HasInstance ? GameUIRoot.Instance : null;
            if (ui == null) return;
            if (ui.bossController != null) ui.bossController.DisableBossHealth();
            if (ui.bossController2 != null) ui.bossController2.DisableBossHealth();
            if (ui.bossControllerSide != null) ui.bossControllerSide.DisableBossHealth();
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
        /// RoomClear enemies), so this is the only thing locking them. Nobody is warped in, so a
        /// room the host sealed while this client was still outside - typically a step behind it
        /// in the doorway - stays open here until the client walks in (ApplyHeldSeal), the way a
        /// room seals behind the player who enters it. Shutting it at once locked the client out
        /// of the fight.
        /// </summary>
        private void ApplySealState(string roomName, bool isSealed)
        {
            RoomHandler room = FindRoomByName(roomName);
            if (room == null)
            {
                Debug.Log($"[EnemyReplicator] Seal state for unknown room {roomName} (layout mismatch?) - ignored.");
                return;
            }
            if (isSealed && CurrentRoom() != room)
            {
                if (!_heldSeals.Contains(room)) _heldSeals.Add(room);
                Debug.Log($"[EnemyReplicator] Host sealed {roomName} while we're outside; it seals here once we're in.");
                return;
            }
            _heldSeals.Remove(room);
            if (isSealed && !room.IsSealed)
            {
                room.SealRoom();
                if (!_networkSealedRooms.Contains(room)) _networkSealedRooms.Add(room);
            }
            else if (!isSealed && room.IsSealed)
            {
                _networkSealedRooms.Remove(room); // first: AllowUnseal blocks rooms still in the list
                room.UnsealRoom();
            }
        }

        /// <summary>Client: seals a room the host sealed earlier, now that we've walked into it.</summary>
        private void ApplyHeldSeal(RoomHandler room)
        {
            if (room == null || _heldSeals.Count == 0 || !_heldSeals.Contains(room)) return;
            ApplySealState(room.GetRoomName(), true);
        }

        /// <summary>
        /// RoomHandler.UnsealRoom prefix. Every frame a player stands in a room, the game unseals it
        /// if it has no enemies that count towards the clear. A client's puppets never count
        /// (IgnoreForRoomClear), so a room the host sealed would reopen on the very next frame and
        /// the client could walk out mid-fight. On a client, only the host opens a room it sealed.
        /// </summary>
        public static bool AllowUnseal(RoomHandler room) =>
            !NetworkSession.Instance.IsClient || !Instance._networkSealedRooms.Contains(room);

        public void HandleRoomCleared(RoomClearedPacket packet)
        {
            RoomHandler room = FindRoomByName(packet.RoomName);

            // Same player effects as a local clear (active-item recharge etc.), but no local reward roll:
            // the host rolled the reward, and LootReplicator mirrors it here - a local roll would duplicate it.
            PlayerController player = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            if (player != null) player.OnRoomCleared();

            // Normally a RoomSealState(false) handles this; belt and braces in case it was missed.
            _heldSeals.Remove(room);
            if (room != null && room.IsSealed)
            {
                _networkSealedRooms.Remove(room); // first: see AllowUnseal
                room.UnsealRoom();
            }
            Debug.Log($"[EnemyReplicator] Host cleared room {packet.RoomName}.");
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            // Don't leave the client locked in a room whose fight will never finish: the host that
            // was going to clear it is gone.
            // Emptied first: see AllowUnseal.
            var sealedRooms = new List<RoomHandler>(_networkSealedRooms);
            _networkSealedRooms.Clear();
            GameManager gm = GameManager.Instance;
            if (gm != null && !gm.IsLoadingLevel)
            {
                foreach (RoomHandler room in sealedRooms)
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
            _synced.Clear();
            _dyingSince.Clear();
            _clientRooms.Clear();
            _bossBars.Clear();
            _hideBossBarsAt = -1f;
            _hostSealedRoom = null;
            _hostRoomName = "";
            _reportedRoom = null;
            _pendingSpawns.Clear();
            _pendingSealStates.Clear();
            _heldSeals.Clear();
            _puppets.Clear();
        }
    }
}
