using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Host-authoritative chests (step 3c) - "puppet chests", mirroring the enemy puppet model.
    ///
    /// Host: plays normally. Harmony postfixes (GungeonTogether.Patches.ChestPatches) report when a
    /// chest opens, breaks, turns out to be a mimic, or spawns mid-level (room-clear reward chests),
    /// and a client's ChestInteract opens the host's copy - paid for from the shared key pool (3b).
    /// The contents spew through LootEngine on the host, so 3a mirrors them to clients.
    ///
    /// Client: never opens a chest itself. A Harmony prefix on Chest.Interact turns the interaction
    /// into a request to the host; its chests are made unbreakable (so shooting one can't spew local
    /// contents); and on the host's word it plays the open/break visuals with no contents, or
    /// removes the chest if it became a mimic (the mimic enemy then arrives as a host puppet).
    ///
    /// Chests are matched by world position (nearest within MatchRadius): the shared seed puts
    /// pre-placed chests in the same spots, and mirrored chests are spawned at the host's exact spot.
    /// </summary>
    public class ChestReplicator : MonoSingleton<ChestReplicator>
    {
        private const float MatchRadius = 1f;

        private GameManager _subscribedTo;
        private Dictionary<string, Chest> _prefabsByName;

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || _subscribedTo == gm) return;
            if (_subscribedTo != null) _subscribedTo.OnNewLevelFullyLoaded -= OnLevelLoaded;
            gm.OnNewLevelFullyLoaded += OnLevelLoaded;
            _subscribedTo = gm;
        }

        private void OnLevelLoaded()
        {
            if (!NetworkSession.Instance.IsClient) return;
            foreach (Chest chest in StaticReferenceManager.AllChests) MakePuppet(chest);
        }

        private static void MakePuppet(Chest chest)
        {
            // Shooting a chest open would spew local contents (duplicated to the host by loot sync).
            if (chest != null && chest.majorBreakable != null) chest.majorBreakable.TemporarilyInvulnerable = true;
        }

        private static Chest FindChest(Vector2 position)
        {
            Chest best = null;
            float bestDist = MatchRadius;
            foreach (Chest chest in StaticReferenceManager.AllChests)
            {
                if (chest == null) continue;
                float dist = Vector2.Distance(chest.transform.position, position);
                if (dist <= bestDist)
                {
                    best = chest;
                    bestDist = dist;
                }
            }
            return best;
        }

        // ---- Entry points for the Harmony patches ----

        /// <summary>Chest.Interact prefix. Returns false to skip the original.</summary>
        public static bool OnInteract(Chest chest)
        {
            if (!NetworkSession.Instance.IsClient) return true;

            if (!chest.IsOpen && !chest.IsBroken)
            {
                NetworkSession.Instance.SendToHost(new ChestInteractPacket { Position = chest.transform.position }, reliable: true);
                Debug.Log($"[ChestReplicator] Asked host to open chest at {chest.transform.position}.");
            }
            return false;
        }

        /// <summary>Chest.Open postfix. Open returns early for some paths (no player, glitch chest), so check the outcome.</summary>
        public static void OnOpened(Chest chest)
        {
            if (!NetworkSession.Instance.IsHost) return;

            if (chest.IsMimic) SendState(chest, ChestState.BecameMimic);
            else if (chest.IsOpen)
            {
                SendState(chest, ChestState.Opened);
                // Diagnostics: a client saw only part of a chest's loot. Compare with the
                // [LootReplicator] lines on both sides.
                var ids = new List<string>();
                if (chest.contents != null) foreach (PickupObject item in chest.contents) ids.Add(item != null ? item.PickupObjectId.ToString() : "null");
                Debug.LogInfo($"[ChestReplicator] Host opened chest at {chest.transform.position}: items [{string.Join(", ", ids.ToArray())}].");
            }
        }

        /// <summary>Chest.OnBroken postfix.</summary>
        public static void OnBroken(Chest chest)
        {
            if (NetworkSession.Instance.IsHost) SendState(chest, ChestState.Broken);
        }

        /// <summary>Chest.Spawn postfix - only runtime spawns; chests placed during generation exist on both sides already.</summary>
        public static void OnSpawned(Chest chest, Chest prefab)
        {
            if (!NetworkSession.Instance.IsHost || chest == null || prefab == null) return;
            if (GameManager.Instance == null || GameManager.Instance.IsLoadingLevel) return;

            NetworkSession.Instance.Broadcast(new ChestSpawnPacket
            {
                PrefabName = prefab.name,
                Position = chest.transform.position,
                Locked = chest.IsLocked
            }, reliable: true);
            Debug.Log($"[ChestReplicator] Host spawned chest {prefab.name} at {chest.transform.position}.");
        }

        private static void SendState(Chest chest, ChestState state)
        {
            NetworkSession.Instance.Broadcast(new ChestStatePacket { Position = chest.transform.position, State = state }, reliable: true);
            Debug.Log($"[ChestReplicator] Chest at {chest.transform.position}: {state}.");
        }

        // ---- Host: client requests ----

        public void HandleInteract(ChestInteractPacket packet)
        {
            Chest chest = FindChest(packet.Position);
            if (chest == null)
            {
                Debug.LogWarning($"[ChestReplicator] Client tried to open a chest at {packet.Position} that the host doesn't have (layout mismatch?).");
                return;
            }
            if (chest.IsOpen || chest.IsBroken) return;

            PlayerController hostPlayer = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
            if (hostPlayer == null) return;

            // The real interaction, on the host's copy: key cost comes out of the shared pool, and a
            // successful open is reported back to every client by the Open postfix.
            chest.Interact(hostPlayer);
        }

        // ---- Client: host's chests ----

        public void HandleSpawn(ChestSpawnPacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel || FindChest(packet.Position) != null) return;

            Chest prefab = FindPrefab(packet.PrefabName);
            if (prefab == null)
            {
                Debug.LogWarningThrottled($"Chest.UnknownPrefab:{packet.PrefabName}", $"[ChestReplicator] Unknown chest prefab '{packet.PrefabName}'; can't mirror it.");
                return;
            }

            Vector3 position = packet.Position;
            RoomHandler room = position.GetAbsoluteRoom();
            Chest chest = Chest.Spawn(prefab, position, room, ForceNoMimic: true); // the host decides mimics
            if (chest == null) return;

            MakePuppet(chest);
            if (!packet.Locked && chest.IsLocked) chest.ForceUnlock();
        }

        public void HandleState(ChestStatePacket packet)
        {
            Chest chest = FindChest(packet.Position);
            if (chest == null) return;

            switch (packet.State)
            {
                case ChestState.Opened:
                    ShowOpened(chest);
                    break;
                case ChestState.Broken:
                    ShowBroken(chest);
                    break;
                case ChestState.BecameMimic:
                    RemoveChest(chest);
                    break;
            }
        }

        // Visuals only - the contents come from the host's copy via loot sync.

        private static void ShowOpened(Chest chest)
        {
            if (chest.IsLocked) chest.ForceUnlock();
            chest.IsOpen = true;
            chest.pickedUp = true;
            if (chest.spriteAnimator != null && !string.IsNullOrEmpty(chest.openAnimName)) chest.spriteAnimator.Play(chest.openAnimName);
            DeregisterInteractable(chest);
        }

        private static void ShowBroken(Chest chest)
        {
            chest.IsBroken = true;
            chest.pickedUp = true;
            if (chest.spriteAnimator != null && !string.IsNullOrEmpty(chest.breakAnimName)) chest.spriteAnimator.Play(chest.breakAnimName);
            if (chest.specRigidbody != null) chest.specRigidbody.enabled = false;
            if (chest.LockAnimator != null) Object.Destroy(chest.LockAnimator.gameObject);
            DeregisterInteractable(chest);
        }

        private static void RemoveChest(Chest chest)
        {
            DeregisterInteractable(chest);
            Object.Destroy(chest.gameObject);
        }

        private static void DeregisterInteractable(Chest chest)
        {
            RoomHandler room = chest.transform.position.GetAbsoluteRoom();
            if (room != null) room.DeregisterInteractable(chest);
        }

        /// <summary>Chest prefabs a runtime spawn can use - RewardManager's public Chest fields (D/C/B/A/S, rainbow, synergy...).</summary>
        private Chest FindPrefab(string prefabName)
        {
            if (_prefabsByName == null)
            {
                _prefabsByName = new Dictionary<string, Chest>();
                RewardManager rewards = GameManager.Instance != null ? GameManager.Instance.RewardManager : null;
                if (rewards != null)
                {
                    foreach (FieldInfo field in typeof(RewardManager).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (field.FieldType != typeof(Chest)) continue;
                        var prefab = field.GetValue(rewards) as Chest;
                        if (prefab != null && !_prefabsByName.ContainsKey(prefab.name)) _prefabsByName[prefab.name] = prefab;
                    }
                }
            }
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (_prefabsByName.TryGetValue(prefabName, out Chest found)) return found;

            // Boss reward chests come from the current dungeon's shared settings instead, which
            // differ per floor - so look there on a miss rather than caching up front.
            WeightedGameObjectCollection bossChests = GameManager.Instance != null && GameManager.Instance.Dungeon != null && GameManager.Instance.Dungeon.sharedSettingsPrefab != null
                ? GameManager.Instance.Dungeon.sharedSettingsPrefab.ChestsForBosses
                : null;
            if (bossChests != null && bossChests.elements != null)
            {
                foreach (WeightedGameObject element in bossChests.elements)
                {
                    Chest chest = element != null && element.gameObject != null ? element.gameObject.GetComponent<Chest>() : null;
                    if (chest != null && chest.name == prefabName)
                    {
                        _prefabsByName[prefabName] = chest;
                        return chest;
                    }
                }
            }
            return null;
        }
    }
}
