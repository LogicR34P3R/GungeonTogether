using System;
using System.Collections.Generic;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Mirrors world loot (step 3a). Every pickup spawned through LootEngine on either side - fed in
    /// by the Harmony patches in GungeonTogether.Patches.LootEnginePatches - gets a network id and,
    /// once it lands, a mirror on the other side. Whoever picks it up first keeps it; the other
    /// side's copy is removed ("optimistic" pickup: a near-simultaneous grab can duplicate an item,
    /// an accepted trade-off for instant pickups).
    ///
    /// World loot sources (enemy drops, room-clear rewards) already only fire on the host, so in
    /// practice the host's loot shows up for the client; loot spawned by a client's own item
    /// effects shows up for the host. Clients' spawns and removals are relayed by the host to
    /// any other clients.
    ///
    /// Not covered yet: items a player drops from their inventory (DropItemWithoutInstantiating),
    /// pre-placed chests and breakables (each side still has its own - step 3c), and shops (3d).
    /// </summary>
    public class LootReplicator : MonoSingleton<LootReplicator>
    {
        // Loot is announced once it lands (so the mirror appears where it settled, not mid-flight);
        // this covers anything whose DebrisObject never reports grounding.
        private const float AnnounceFallbackSeconds = 2f;

        private struct LootKey : IEquatable<LootKey>
        {
            public readonly ulong Owner;
            public readonly int Id;

            public LootKey(ulong owner, int id) { Owner = owner; Id = id; }

            public bool Equals(LootKey other) => Owner == other.Owner && Id == other.Id;
            public override bool Equals(object obj) => obj is LootKey other && Equals(other);
            public override int GetHashCode() => Owner.GetHashCode() * 397 ^ Id;
            public override string ToString() => $"{Owner}:{Id}";
        }

        private class Tracked
        {
            public LootKey Key;
            public GameObject Go;
            public PickupObject Pickup;
            public bool Announced;
            public float SpawnTime;
        }

        /// <summary>True while spawning a mirror, so the LootEngine patches don't track it as new local loot.</summary>
        public static bool IsMirroring { get; private set; }

        private static int _captureDepth;
        private static readonly List<GameObject> _captured = new List<GameObject>();

        private readonly Dictionary<LootKey, Tracked> _tracked = new Dictionary<LootKey, Tracked>();
        private readonly List<LootKey> _scratchKeys = new List<LootKey>();
        private int _nextLocalId = 1;

        // ---- Entry points for the Harmony patches ----

        /// <summary>A LootEngine call just spawned this item locally.</summary>
        public static void OnLocalSpawn(DebrisObject debris)
        {
            if (IsMirroring || debris == null || !NetworkSession.Instance.IsConnected) return;
            Instance.TrackLocal(debris);
        }

        /// <summary>SpawnCurrency spawns coins through SpawnManager.SpawnDebris and returns nothing - collect them while it runs.</summary>
        public static void BeginCapture() => _captureDepth++;

        public static void OnDebrisSpawned(GameObject go)
        {
            if (_captureDepth > 0 && go != null) _captured.Add(go);
        }

        public static void EndCapture()
        {
            if (_captureDepth > 0) _captureDepth--;
            if (_captureDepth > 0) return;

            // Coins get their DebrisObject right after SpawnDebris returns, so by now it exists.
            foreach (GameObject go in _captured)
            {
                if (go != null) OnLocalSpawn(go.GetComponent<DebrisObject>());
            }
            _captured.Clear();
        }

        // ---- Local loot ----

        /// <summary>
        /// A spawned gun isn't its own debris: Gun.DropGun parents it under a "ThrownGunProjectile"
        /// object and returns that. Looking only on the debris itself skipped every gun (chest guns,
        /// gun drops), so the other side never saw them.
        /// </summary>
        private static PickupObject PickupOf(DebrisObject debris) => debris.GetComponentInChildren<PickupObject>();

        private void TrackLocal(DebrisObject debris)
        {
            PickupObject pickup = PickupOf(debris);
            if (pickup == null || pickup.PickupObjectId < 0) return; // not something we can recreate by id

            var tracked = new Tracked
            {
                Key = new LootKey(SteamIdentity.GetLocalSteamId(), _nextLocalId++),
                Go = debris.gameObject,
                Pickup = pickup,
                SpawnTime = Time.realtimeSinceStartup
            };
            _tracked[tracked.Key] = tracked;
            debris.OnGrounded = (Action<DebrisObject>)Delegate.Combine(debris.OnGrounded, new Action<DebrisObject>(_ => Announce(tracked)));
        }

        private void Announce(Tracked tracked)
        {
            if (tracked.Announced || tracked.Go == null || !_tracked.ContainsKey(tracked.Key)) return;
            tracked.Announced = true;

            Send(new LootSpawnPacket
            {
                OwnerId = tracked.Key.Owner,
                LocalId = tracked.Key.Id,
                PickupId = tracked.Pickup.PickupObjectId,
                Position = tracked.Go.transform.position
            });
        }

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            // A level unload destroys every item - that's not "picked up", so don't report it.
            if (gm == null || gm.IsLoadingLevel || !NetworkSession.Instance.IsConnected)
            {
                if (_tracked.Count > 0) _tracked.Clear();
                return;
            }
            if (_tracked.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            _scratchKeys.Clear();
            _scratchKeys.AddRange(_tracked.Keys);
            foreach (LootKey key in _scratchKeys)
            {
                Tracked tracked = _tracked[key];
                if (IsGone(tracked))
                {
                    _tracked.Remove(key);
                    if (tracked.Announced) Send(new LootTakenPacket { OwnerId = key.Owner, LocalId = key.Id });
                }
                else if (!tracked.Announced && now - tracked.SpawnTime > AnnounceFallbackSeconds)
                {
                    Announce(tracked);
                }
            }
        }

        /// <summary>Picked up (or destroyed/despawned) on this side.</summary>
        private static bool IsGone(Tracked tracked)
        {
            // Deliberately no activeInHierarchy check: an item merely deactivated with its room must
            // not count as picked up, or the other side's copy would be deleted.
            if (tracked.Go == null || tracked.Pickup == null) return true;
            if (tracked.Pickup is Gun gun) return gun.CurrentOwner != null;
            if (tracked.Pickup is PassiveItem passive) return passive.PickedUp;
            if (tracked.Pickup is PlayerItem active) return active.PickedUp;
            return false; // consumables destroy themselves when collected
        }

        // ---- Remote loot ----

        public void HandleLootSpawn(ulong senderId, LootSpawnPacket packet)
        {
            // Only the host can speak for other players' items; a client's own spawns are its own.
            if (NetworkSession.Instance.IsHost) packet.OwnerId = senderId;

            var key = new LootKey(packet.OwnerId, packet.LocalId);
            if (!_tracked.ContainsKey(key))
            {
                SpawnMirror(key, packet);
            }

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(packet, excludeId: senderId, reliable: true);
            }
        }

        private void SpawnMirror(LootKey key, LootSpawnPacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel) return; // it belongs to a level we're not on yet

            PickupObject prefab = PickupObjectDatabase.GetById(packet.PickupId);
            if (prefab == null)
            {
                Debug.LogWarningThrottled($"Loot.UnknownPickup:{packet.PickupId}", $"[LootReplicator] Unknown pickup id {packet.PickupId}; can't mirror it.");
                return;
            }

            DebrisObject debris;
            IsMirroring = true;
            try
            {
                debris = LootEngine.SpawnItem(prefab.gameObject, packet.Position, Vector2.up, 0f, invalidUntilGrounded: true, doDefaultItemPoof: true);
            }
            finally
            {
                IsMirroring = false;
            }
            if (debris == null) return;

            // Already "announced": it came from the other side, which knows about it. If we pick it
            // up, the Update poll reports that back.
            _tracked[key] = new Tracked
            {
                Key = key,
                Go = debris.gameObject,
                Pickup = PickupOf(debris),
                Announced = true,
                SpawnTime = Time.realtimeSinceStartup
            };
            Debug.LogTrace($"[LootReplicator] Mirrored pickup {packet.PickupId} ({key}) at {packet.Position}.");
        }

        public void HandleLootTaken(ulong senderId, LootTakenPacket packet)
        {
            var key = new LootKey(packet.OwnerId, packet.LocalId);
            if (_tracked.TryGetValue(key, out Tracked tracked))
            {
                // Unregister first so the Update poll doesn't report this removal back.
                _tracked.Remove(key);
                if (!IsGone(tracked)) RemoveWorldItem(tracked);
            }
            // Else: already gone here too - both grabbed it at once (accepted duplicate) or it despawned.

            if (NetworkSession.Instance.IsHost)
            {
                NetworkSession.Instance.Broadcast(packet, excludeId: senderId, reliable: true);
            }
        }

        /// <summary>Removes an item from the world the way the game's own pickups do before destroying it.</summary>
        private static void RemoveWorldItem(Tracked tracked)
        {
            if (tracked.Pickup is IPlayerInteractable interactable)
            {
                RoomHandler.unassignedInteractableObjects.Remove(interactable);
                RoomHandler room = tracked.Go.transform.position.GetAbsoluteRoom();
                if (room != null) room.DeregisterInteractable(interactable);
            }
            Object.Destroy(tracked.Go);
        }

        private static void Send(INetworkPacket packet)
        {
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: true);
            else if (NetworkSession.Instance.IsClient) NetworkSession.Instance.SendToHost(packet, reliable: true);
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _tracked.Clear();
            _captured.Clear();
            _captureDepth = 0;
        }
    }
}
