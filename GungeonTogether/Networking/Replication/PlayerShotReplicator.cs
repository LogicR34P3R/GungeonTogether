using System;
using System.Collections.Generic;
using UnityEngine;
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
    /// Shows each player's gunfire on the other side, as harmless copies.
    ///
    /// Capture: PlayerController.PostProcessProjectile - the game's own public hook, raised for every
    /// projectile a player's gun fires - so no Harmony patch. Reported in LateUpdate so speed and
    /// direction include whatever post-processing (items, synergies) changed.
    ///
    /// Replay: the same projectile prefab (found by gun id + projectile name), fired from the same
    /// spot, that can't touch anything but walls. Damage already travels on its own path - a
    /// client's hits via DamageReplicator, the host's are the real thing - so a copy that could hit
    /// would count every shot twice. Straight flight only: homing and on-hit/on-death effects that
    /// act at a distance (explosions, spawned projectiles, goop) are stripped.
    /// </summary>
    public class PlayerShotReplicator : MonoSingleton<PlayerShotReplicator>
    {
        private struct Captured
        {
            public Projectile Projectile;
            public int GunId;
            public Vector2 Position;
        }

        private readonly List<Captured> _captured = new List<Captured>();
        private PlayerController _subscribedPlayer;

        // (gunId, projectileName) -> prefab; searching a gun's modules on every shot is wasteful.
        private readonly Dictionary<string, Projectile> _prefabCache = new Dictionary<string, Projectile>();

        // ---- Local capture ----

        private void Update()
        {
            PlayerController player = NetworkSession.Instance.IsConnected && GameManager.HasInstance
                ? GameManager.Instance.PrimaryPlayer
                : null;
            if (player == _subscribedPlayer) return;

            // New PlayerController each level (and none between sessions): move the subscription.
            if (_subscribedPlayer != null) _subscribedPlayer.PostProcessProjectile -= OnLocalProjectile;
            if (player != null) player.PostProcessProjectile += OnLocalProjectile;
            _subscribedPlayer = player;
        }

        private void OnLocalProjectile(Projectile projectile, float effectChanceScalar)
        {
            if (projectile == null || !NetworkSession.Instance.IsConnected) return;

            Gun gun = projectile.PossibleSourceGun != null ? projectile.PossibleSourceGun
                : _subscribedPlayer != null ? _subscribedPlayer.CurrentGun : null;
            if (gun == null) return;

            _captured.Add(new Captured
            {
                Projectile = projectile,
                GunId = gun.PickupObjectId,
                Position = projectile.transform.position
            });
        }

        private void LateUpdate()
        {
            if (_captured.Count == 0) return;

            ulong localId = SteamIdentity.GetLocalSteamId();
            foreach (Captured c in _captured)
            {
                if (c.Projectile == null) continue;
                var packet = new PlayerProjectilePacket
                {
                    PlayerId = localId,
                    GunId = c.GunId,
                    ProjectileName = PrefabName(c.Projectile.name),
                    Position = c.Position,
                    Direction = c.Projectile.Direction.ToAngle(),
                    Speed = c.Projectile.Speed
                };

                if (NetworkSession.Instance.IsHost) NetworkSession.Instance.Broadcast(packet, reliable: false);
                else if (NetworkSession.Instance.IsClient) NetworkSession.Instance.SendToHost(packet, reliable: false);
            }
            _captured.Clear();
        }

        private static string PrefabName(string instanceName)
        {
            int clone = instanceName.IndexOf("(Clone)", StringComparison.Ordinal);
            return (clone >= 0 ? instanceName.Substring(0, clone) : instanceName).Trim();
        }

        // ---- Remote replay ----

        public void HandlePlayerProjectile(PlayerProjectilePacket packet)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsLoadingLevel) return;

            Projectile prefab = FindPrefab(packet.GunId, packet.ProjectileName);
            if (prefab == null)
            {
                Debug.LogWarningThrottled($"PlayerShot.NoPrefab:{packet.GunId}:{packet.ProjectileName}",
                    $"[PlayerShotReplicator] No projectile '{packet.ProjectileName}' on gun {packet.GunId}; shot not shown.");
                return;
            }

            try
            {
                // Unpooled: the copy is modified below, and a pooled instance would carry that into
                // the local player's own next shot of the same projectile.
                GameObject spawned = SpawnManager.SpawnProjectile(prefab.gameObject, packet.Position,
                    Quaternion.Euler(0f, 0f, packet.Direction), ignoresPools: true);
                Projectile projectile = spawned != null ? spawned.GetComponent<Projectile>() : null;
                if (projectile == null) return;
                projectile.OnSpawned(); // CreateProjectileFromBank does this for unpooled spawns too

                MakeHarmless(projectile);
                projectile.baseData.speed = packet.Speed;
                projectile.UpdateSpeed();
                projectile.SendInDirection(BraveMathCollege.DegreesToVector(packet.Direction), resetDistance: true);
                ProjectileReplicator.SampleForDiagnostics(projectile, "remote player shot");
            }
            catch (Exception e)
            {
                Debug.LogWarningThrottled($"PlayerShot.SpawnFailed:{packet.GunId}:{packet.ProjectileName}",
                    $"[PlayerShotReplicator] Couldn't show '{packet.ProjectileName}' from gun {packet.GunId}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void MakeHarmless(Projectile projectile)
        {
            projectile.collidesWithEnemies = false;
            projectile.collidesWithPlayer = false;
            projectile.collidesWithProjectiles = false;
            projectile.baseData.damage = 0f;

            // Effects that reach past the projectile itself would still hurt enemies/players.
            foreach (var c in projectile.GetComponents<ExplosiveModifier>()) Object.Destroy(c);
            foreach (var c in projectile.GetComponents<SpawnProjModifier>()) Object.Destroy(c);
            foreach (var c in projectile.GetComponents<GoopModifier>()) Object.Destroy(c);
            foreach (var c in projectile.GetComponents<HomingModifier>()) Object.Destroy(c);
        }

        private Projectile FindPrefab(int gunId, string projectileName)
        {
            string key = gunId + ":" + projectileName;
            if (_prefabCache.TryGetValue(key, out Projectile cached) && cached != null) return cached;

            Gun gun = PickupObjectDatabase.GetById(gunId) as Gun;
            if (gun == null) return null;

            Projectile found = null;
            if (gun.Volley != null && gun.Volley.projectiles != null)
            {
                foreach (ProjectileModule module in gun.Volley.projectiles)
                {
                    found = FindInModule(module, projectileName);
                    if (found != null) break;
                }
            }
            if (found == null) found = FindInModule(gun.singleModule, projectileName);

            // Items/synergies can swap in projectiles from elsewhere; the gun's own default is a
            // better stand-in than showing nothing.
            if (found == null && gun.DefaultModule != null && gun.DefaultModule.projectiles != null
                && gun.DefaultModule.projectiles.Count > 0)
            {
                found = gun.DefaultModule.projectiles[0];
            }

            if (found != null) _prefabCache[key] = found;
            return found;
        }

        private static Projectile FindInModule(ProjectileModule module, string projectileName)
        {
            if (module == null) return null;
            if (module.projectiles != null)
            {
                foreach (Projectile p in module.projectiles)
                    if (p != null && p.name == projectileName) return p;
            }
            if (module.chargeProjectiles != null)
            {
                foreach (ProjectileModule.ChargeProjectile charge in module.chargeProjectiles)
                    if (charge != null && charge.Projectile != null && charge.Projectile.name == projectileName) return charge.Projectile;
            }
            if (module.finalProjectile != null && module.finalProjectile.name == projectileName) return module.finalProjectile;
            return null;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _captured.Clear();
        }
    }
}
