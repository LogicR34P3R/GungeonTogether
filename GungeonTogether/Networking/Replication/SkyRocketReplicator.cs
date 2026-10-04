using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Sky rockets on the client: the Gatling Gull's rocket barrage (rockets fly up, a landing marker
    /// appears, they come down and blow up). A SkyRocket isn't a projectile: GatlingGullRocketBehavior
    /// spawns it directly and it picks its landing spot itself, near its target, once it has gone up.
    /// The client only got the blast (ExplosionReplicator), never the rocket or the marker.
    ///
    /// The host reports each rocket as it starts (Launch) and when it picks its spot (Land). The
    /// client flies a copy up from the same place and holds it at the top until it knows the host's
    /// spot, then lets it come down there, marker and all. No SkyRocket explodes on a client: the
    /// host's blast arrives as an Explosion packet. That also covers rockets a replayed attack script
    /// fires on the client by itself (the Dragun's), which the host therefore doesn't report.
    /// Harmony wiring: GungeonTogether.Patches.SkyRocketPatches.
    /// </summary>
    public class SkyRocketReplicator : MonoSingleton<SkyRocketReplicator>
    {
        // Private state SkyRocket keeps for its flight: Ascend until m_timer runs out, then it picks
        // m_targetLandPosition and spawns the landing marker.
        private static readonly FieldInfo StateField =
            typeof(SkyRocket).GetField("m_state", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo TimerField =
            typeof(SkyRocket).GetField("m_timer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LandPositionField =
            typeof(SkyRocket).GetField("m_targetLandPosition", BindingFlags.NonPublic | BindingFlags.Instance);

        // A copy whose landing spot never arrives is removed this long after it reached the top.
        private const float MaxHoldSeconds = 3f;

        private class HostRocket
        {
            public SkyRocket Rocket;
            public int Id;
            public string PrefabName;
            public bool LandSent;
        }

        private class ClientRocket
        {
            public SkyRocket Rocket;
            public bool HasTarget;
            public float SpawnTime;
        }

        private static readonly List<HostRocket> _hostRockets = new List<HostRocket>();
        private static int _nextRocketId = 1;

        private static readonly Dictionary<int, ClientRocket> _clientRockets = new Dictionary<int, ClientRocket>();
        // Client copies still waiting for the host's landing spot - see ShouldUpdate.
        private static readonly HashSet<SkyRocket> _awaitingTarget = new HashSet<SkyRocket>();
        private static readonly List<int> _removeIds = new List<int>();

        private static SkyRocket.SkyRocketState StateOf(SkyRocket rocket) => (SkyRocket.SkyRocketState)StateField.GetValue(rocket);

        /// <summary>SkyRocket.Start postfix (both roles).</summary>
        public static void OnStart(SkyRocket rocket)
        {
            if (rocket == null) return;
            if (NetworkSession.Instance.IsClient)
            {
                // The host's blast arrives as an Explosion packet; one here would hit twice.
                rocket.DoExplosion = false;
                rocket.SpawnObject = null;
                return;
            }
            if (!NetworkSession.Instance.IsHost) return;

            // A rocket aimed at a fixed spot comes from an attack script, which the client replays
            // itself (ScriptReplicator). The Gull's rockets aim at its target and ride on its sprite
            // until they're up (GatlingGullRocketBehavior.FireRocket).
            if (rocket.TargetVector2 != Vector2.zero) return;
            tk2dBaseSprite parent = rocket.sprite != null ? rocket.sprite.attachParent : null;
            AIActor enemy = parent != null ? parent.GetComponentInParent<AIActor>() : null;
            if (enemy == null || !EnemyReplicator.Instance.TryGetSyncedId(enemy, out int enemyId)) return;

            var tracked = new HostRocket { Rocket = rocket, Id = _nextRocketId++, PrefabName = PrefabNameOf(rocket) };
            _hostRockets.Add(tracked);
            Vector3 position = rocket.transform.position;
            NetworkSession.Instance.Broadcast(new SkyRocketPacket
            {
                Event = SkyRocketPacket.RocketEvent.Launch,
                RocketId = tracked.Id,
                EnemyId = enemyId,
                X = position.x,
                Y = position.y,
                PrefabName = tracked.PrefabName
            }, reliable: true);
        }

        /// <summary>SkyRocket.DieInAir postfix (host): the Gull died, its rockets go with it.</summary>
        public static void OnDieInAir(SkyRocket rocket)
        {
            if (!NetworkSession.Instance.IsHost || rocket == null) return;
            int index = _hostRockets.FindIndex(r => r.Rocket == rocket);
            if (index < 0) return;
            NetworkSession.Instance.Broadcast(new SkyRocketPacket { Event = SkyRocketPacket.RocketEvent.Stop, RocketId = _hostRockets[index].Id }, reliable: true);
            _hostRockets.RemoveAt(index);
        }

        /// <summary>
        /// SkyRocket.Update prefix (skipping): a client copy that has gone all the way up without the
        /// host's landing spot waits at the top. Letting it carry on would pick a spot of its own -
        /// from a Target it doesn't have.
        /// </summary>
        public static bool ShouldUpdate(SkyRocket rocket)
        {
            if (_awaitingTarget.Count == 0 || !_awaitingTarget.Contains(rocket)) return true;
            if (StateOf(rocket) != SkyRocket.SkyRocketState.Ascend) return true;
            return (float)TimerField.GetValue(rocket) - BraveTime.DeltaTime > 0f;
        }

        private static string PrefabNameOf(SkyRocket rocket)
        {
            string name = rocket.gameObject.name;
            int clone = name.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone).TrimEnd() : name;
        }

        // ---- Host ----

        private void LateUpdate()
        {
            if (_hostRockets.Count == 0) return;
            if (!NetworkSession.Instance.IsHost)
            {
                _hostRockets.Clear();
                return;
            }
            for (int i = _hostRockets.Count - 1; i >= 0; i--)
            {
                HostRocket tracked = _hostRockets[i];
                if (tracked.Rocket == null)
                {
                    _hostRockets.RemoveAt(i); // landed (its blast went out as an Explosion)
                    continue;
                }
                if (tracked.LandSent || StateOf(tracked.Rocket) == SkyRocket.SkyRocketState.Ascend) continue;

                tracked.LandSent = true;
                Vector3 spot = (Vector3)LandPositionField.GetValue(tracked.Rocket);
                NetworkSession.Instance.Broadcast(new SkyRocketPacket
                {
                    Event = SkyRocketPacket.RocketEvent.Land,
                    RocketId = tracked.Id,
                    X = spot.x,
                    Y = spot.y,
                    PrefabName = tracked.PrefabName
                }, reliable: true);
            }
        }

        // ---- Client ----

        public void HandleSkyRocket(SkyRocketPacket packet)
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer) return;

            _clientRockets.TryGetValue(packet.RocketId, out ClientRocket copy);
            if (copy != null && copy.Rocket == null)
            {
                _clientRockets.Remove(packet.RocketId);
                copy = null;
            }

            switch (packet.Event)
            {
                case SkyRocketPacket.RocketEvent.Launch:
                    if (copy != null) return;
                    SkyRocket launched = Spawn(packet.PrefabName, new Vector3(packet.X, packet.Y, 0f));
                    if (launched == null) return;
                    // Like FireRocket: drawn with the enemy until it's up (SkyRocket detaches it).
                    GameObject puppet = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
                    AIActor enemy = puppet != null ? puppet.GetComponent<AIActor>() : null;
                    tk2dBaseSprite rocketSprite = launched.GetComponentInChildren<tk2dSprite>();
                    if (enemy != null && enemy.sprite != null && rocketSprite != null) enemy.sprite.AttachRenderer(rocketSprite);
                    _clientRockets[packet.RocketId] = new ClientRocket { Rocket = launched, SpawnTime = Time.time };
                    _awaitingTarget.Add(launched);
                    break;

                case SkyRocketPacket.RocketEvent.Land:
                    var spot = new Vector2(packet.X, packet.Y);
                    if (copy == null)
                    {
                        // Its launch never made it here: bring it straight down from the top.
                        SkyRocket late = Spawn(packet.PrefabName, spot);
                        if (late == null) return;
                        late.AscentTime = 0f;
                        late.TargetVector2 = spot;
                        _clientRockets[packet.RocketId] = new ClientRocket { Rocket = late, HasTarget = true, SpawnTime = Time.time };
                        return;
                    }
                    copy.Rocket.TargetVector2 = spot;
                    copy.HasTarget = true;
                    _awaitingTarget.Remove(copy.Rocket);
                    break;

                case SkyRocketPacket.RocketEvent.Stop:
                    if (copy == null) return;
                    _clientRockets.Remove(packet.RocketId);
                    _awaitingTarget.Remove(copy.Rocket);
                    copy.Rocket.DieInAir();
                    break;
            }
        }

        private SkyRocket Spawn(string prefabName, Vector3 position)
        {
            GameObject prefab = FindPrefab(prefabName);
            if (prefab == null) return null;
            GameObject spawned = SpawnManager.SpawnProjectile(prefab, position, Quaternion.identity);
            SkyRocket rocket = spawned != null ? spawned.GetComponent<SkyRocket>() : null;
            if (rocket == null)
            {
                if (spawned != null) Object.Destroy(spawned);
                return null;
            }
            // Start (next frame) does this too; set now so nothing can go off in between.
            rocket.DoExplosion = false;
            rocket.SpawnObject = null;
            return rocket;
        }

        private void Update()
        {
            if (_clientRockets.Count == 0) return;
            _removeIds.Clear();
            foreach (var kvp in _clientRockets)
            {
                ClientRocket copy = kvp.Value;
                if (copy.Rocket == null)
                {
                    _removeIds.Add(kvp.Key);
                    continue;
                }
                if (!copy.HasTarget && Time.time - copy.SpawnTime > copy.Rocket.AscentTime + MaxHoldSeconds)
                {
                    Debug.LogWarningThrottled("SkyRocket.NoLanding", $"[SkyRocket] Rocket {kvp.Key} never got its landing spot from the host; removed.");
                    Object.Destroy(copy.Rocket.gameObject);
                    _removeIds.Add(kvp.Key);
                }
            }
            foreach (int id in _removeIds)
            {
                if (_clientRockets.TryGetValue(id, out ClientRocket copy) && copy.Rocket != null) _awaitingTarget.Remove(copy.Rocket);
                _clientRockets.Remove(id);
            }
            _awaitingTarget.RemoveWhere(r => r == null);
        }

        // Rocket prefabs by name. The enemy that fires them exists here too (as a puppet), so its
        // rocket prefab is loaded. Searching is slow, so each name is looked up once, misses included.
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();

        private GameObject FindPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_prefabs.TryGetValue(name, out GameObject prefab)) return prefab;
            prefab = null;
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(SkyRocket)))
            {
                SkyRocket candidate = o as SkyRocket;
                // A prefab asset, not a live rocket in the scene.
                if (candidate != null && candidate.gameObject.name == name && string.IsNullOrEmpty(candidate.gameObject.scene.name))
                {
                    prefab = candidate.gameObject;
                    break;
                }
            }
            if (prefab == null) Debug.LogWarning($"[SkyRocket] Rocket prefab '{name}' not found here; its rockets won't show.");
            _prefabs[name] = prefab;
            return prefab;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _hostRockets.Clear();
            _clientRockets.Clear();
            _awaitingTarget.Clear();
        }
    }
}
