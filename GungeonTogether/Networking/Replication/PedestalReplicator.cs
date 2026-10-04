using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Reward pedestals: the boss's item and the master round on their stands. The client only got
    /// the hearts around them (those spawn through LootEngine), never the pedestals: a boss room's
    /// reward only runs on the host (HandleBossClearReward), and a pedestal holds its item itself
    /// rather than dropping it on the floor.
    ///
    /// The host reports each pedestal it spawns once its item is chosen; the client puts up its
    /// own pedestal with that item (no hearts - those are mirrored loot - and never a mimic).
    /// Whoever takes the item keeps it and tells the other side, which empties its stand
    /// (optimistic, like loot). A mimic stays the host's: a client's touch is sent to the host,
    /// which wakes it there; the mimic arrives as a puppet and the copy goes (Gone).
    ///
    /// Pedestals are matched by position. Only runtime spawns (RewardPedestal.Spawn) are synced.
    /// Harmony wiring: GungeonTogether.Patches.PedestalPatches.
    /// </summary>
    public class PedestalReplicator : MonoSingleton<PedestalReplicator>
    {
        private const float MatchRadius = 1f;

        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo DisplaySpriteField = typeof(RewardPedestal).GetField("m_itemDisplaySprite", Private);
        private static readonly FieldInfo IconRoomField = typeof(RewardPedestal).GetField("m_registeredIconRoom", Private);
        private static readonly FieldInfo IconField = typeof(RewardPedestal).GetField("minimapIconInstance", Private);

        private class Tracked
        {
            public RewardPedestal Pedestal;
            public Vector2 Position;
            public string PrefabName;
            public bool Announced;   // host: Spawn sent
            public bool TakenSent;   // its item is gone, and both sides know (or will)
            public bool HostMimic;   // client: taking it wakes a mimic on the host
        }

        private readonly List<Tracked> _tracked = new List<Tracked>();

        private static bool InLevel()
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            return gm != null && !gm.IsLoadingLevel && !gm.IsFoyer && gm.Dungeon != null;
        }

        private Tracked Find(Vector2 position)
        {
            Tracked best = null;
            float bestDist = MatchRadius;
            foreach (Tracked t in _tracked)
            {
                if (t.Pedestal == null) continue;
                float dist = Vector2.Distance(t.Position, position);
                if (dist <= bestDist)
                {
                    best = t;
                    bestDist = dist;
                }
            }
            return best;
        }

        // ---- Entry points for the Harmony patches ----

        /// <summary>RewardPedestal.Spawn postfix (host): reported once its item is chosen (LateUpdate).</summary>
        public static void OnSpawned(RewardPedestal pedestal, RewardPedestal prefab)
        {
            if (!NetworkSession.Instance.IsHost || pedestal == null || prefab == null || !InLevel()) return;
            Instance._tracked.Add(new Tracked { Pedestal = pedestal, Position = pedestal.transform.position, PrefabName = prefab.name });
        }

        /// <summary>
        /// RewardPedestal.Interact prefix: false (skip) for a client's copy of a host mimic - the
        /// host wakes it instead, so the client doesn't get a free item.
        /// </summary>
        public static bool OnInteract(RewardPedestal pedestal)
        {
            if (!NetworkSession.Instance.IsClient || pedestal == null) return true;
            Tracked t = Instance._tracked.Find(x => x.Pedestal == pedestal);
            if (t == null || !t.HostMimic) return true;
            if (!t.TakenSent)
            {
                t.TakenSent = true;
                NetworkSession.Instance.SendToHost(new PedestalPacket { Event = PedestalPacket.PedestalEvent.Taken, Position = t.Position }, reliable: true);
            }
            return false;
        }

        // ---- Both sides ----

        private void LateUpdate()
        {
            if (_tracked.Count == 0) return;
            if (!NetworkSession.Instance.IsConnected || !InLevel())
            {
                _tracked.Clear(); // a level unload destroys them; that's nothing to report
                return;
            }
            bool isHost = NetworkSession.Instance.IsHost;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                Tracked t = _tracked[i];
                if (t.Pedestal == null)
                {
                    // Host: gone outright - a mimic that woke up. The client's copy goes too.
                    if (isHost && t.Announced) Send(new PedestalPacket { Event = PedestalPacket.PedestalEvent.Gone, Position = t.Position });
                    _tracked.RemoveAt(i);
                    continue;
                }
                if (isHost && !t.Announced && t.Pedestal.contents != null)
                {
                    t.Announced = true;
                    Send(new PedestalPacket
                    {
                        Event = PedestalPacket.PedestalEvent.Spawn,
                        Position = t.Position,
                        PickupId = t.Pedestal.contents.PickupObjectId,
                        PrefabName = t.PrefabName,
                        IsMimic = t.Pedestal.IsMimic
                    });
                    Debug.LogInfo($"[Pedestal] Host pedestal at {t.Position} holds item {t.Pedestal.contents.PickupObjectId}{(t.Pedestal.IsMimic ? " (mimic)" : "")}.");
                }
                // Taken here. A host mimic that woke up instead reports Gone once it's destroyed.
                if (t.Pedestal.pickedUp && !t.TakenSent && (isHost ? t.Announced && !t.Pedestal.IsMimic : true))
                {
                    t.TakenSent = true;
                    Send(new PedestalPacket { Event = PedestalPacket.PedestalEvent.Taken, Position = t.Position });
                }
            }
        }

        private static void Send(INetworkPacket packet)
        {
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: true);
            else if (NetworkSession.Instance.IsClient) NetworkSession.Instance.SendToHost(packet, reliable: true);
        }

        public void HandlePedestal(ulong senderId, PedestalPacket packet)
        {
            if (!InLevel()) return;
            switch (packet.Event)
            {
                case PedestalPacket.PedestalEvent.Spawn:
                    if (NetworkSession.Instance.IsClient) SpawnCopy(packet);
                    break;
                case PedestalPacket.PedestalEvent.Taken:
                    HandleTaken(senderId, packet);
                    break;
                case PedestalPacket.PedestalEvent.Gone:
                    if (NetworkSession.Instance.IsClient) RemoveCopy(packet.Position);
                    break;
            }
        }

        private void HandleTaken(ulong senderId, PedestalPacket packet)
        {
            if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, excludeId: senderId, reliable: true);

            Tracked t = Find(packet.Position);
            if (t == null || t.TakenSent) return; // both took it at once (accepted duplicate)
            t.TakenSent = true;

            // A client touched the host's mimic: wake it here (Interact on a mimic gives nothing).
            if (NetworkSession.Instance.IsHost && t.Pedestal.IsMimic)
            {
                PlayerController host = GameManager.Instance.PrimaryPlayer;
                if (host != null) t.Pedestal.Interact(host);
                return;
            }
            Empty(t.Pedestal);
        }

        /// <summary>The look of a taken pedestal: no item on the stand, nothing to interact with.</summary>
        private static void Empty(RewardPedestal pedestal)
        {
            pedestal.pickedUp = true;
            DeregisterInteractable(pedestal);
            var display = DisplaySpriteField?.GetValue(pedestal) as tk2dBaseSprite;
            if (display != null)
            {
                SpriteOutlineManager.RemoveOutlineFromSprite(display);
                Object.Destroy(display.gameObject);
            }
            var iconRoom = IconRoomField?.GetValue(pedestal) as RoomHandler;
            var icon = IconField?.GetValue(pedestal) as GameObject;
            if (iconRoom != null && icon != null && Minimap.HasInstance) Minimap.Instance.DeregisterRoomIcon(iconRoom, icon);
        }

        private static void DeregisterInteractable(RewardPedestal pedestal)
        {
            RoomHandler room = pedestal.transform.position.GetAbsoluteRoom();
            if (room != null) room.DeregisterInteractable(pedestal);
        }

        // ---- Client ----

        private void SpawnCopy(PedestalPacket packet)
        {
            if (Find(packet.Position) != null) return;
            RewardPedestal prefab = FindPrefab(packet.PrefabName);
            if (prefab == null) return;

            // Instantiated rather than Spawn()ed: Spawn starts the landing sequence at once, which
            // would roll its own item and drop its own hearts before anything here could stop it.
            // Start runs it next frame instead, with these settings: the host's item, no hearts.
            GameObject go = Object.Instantiate(prefab.gameObject, new Vector3(packet.Position.x, packet.Position.y, 0f), Quaternion.identity);
            RewardPedestal copy = go.GetComponent<RewardPedestal>();
            if (copy == null)
            {
                Object.Destroy(go);
                return;
            }
            copy.UsesSpecificItem = true;
            copy.SpecificItemId = packet.PickupId;
            copy.IsBossRewardPedestal = false;
            copy.SpawnsTertiarySet = false;
            copy.ReturnCoopPlayerOnLand = false;
            copy.MimicGuid = null;

            RoomHandler room = go.transform.position.GetAbsoluteRoom();
            if (room != null) copy.RegisterChestOnMinimap(room);

            _tracked.Add(new Tracked { Pedestal = copy, Position = packet.Position, PrefabName = packet.PrefabName, Announced = true, HostMimic = packet.IsMimic });
            Debug.LogInfo($"[Pedestal] Host's pedestal at {packet.Position} with item {packet.PickupId}{(packet.IsMimic ? " (a mimic on the host)" : "")}.");
        }

        private void RemoveCopy(Vector2 position)
        {
            Tracked t = Find(position);
            if (t == null) return;
            _tracked.Remove(t);
            Empty(t.Pedestal);
            Object.Destroy(t.Pedestal.gameObject);
        }

        // Pedestal prefabs by name (the boss's comes from the floor's shared settings, loaded on
        // both sides). Searching is slow, so each name is looked up once, misses included.
        private readonly Dictionary<string, RewardPedestal> _prefabs = new Dictionary<string, RewardPedestal>();

        private RewardPedestal FindPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_prefabs.TryGetValue(name, out RewardPedestal prefab)) return prefab;
            prefab = null;
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(RewardPedestal)))
            {
                RewardPedestal candidate = o as RewardPedestal;
                if (candidate != null && candidate.gameObject.name == name && string.IsNullOrEmpty(candidate.gameObject.scene.name))
                {
                    prefab = candidate;
                    break;
                }
            }
            if (prefab == null) Debug.LogWarning($"[Pedestal] Pedestal prefab '{name}' not found here; can't show it.");
            _prefabs[name] = prefab;
            return prefab;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState() => _tracked.Clear();
    }
}
