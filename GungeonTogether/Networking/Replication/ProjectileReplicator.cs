using System;
using System.Collections.Generic;
using UnityEngine;
using Brave.BulletScript;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Enemy attacks on the client (step 4a, option A: straight bullets).
    ///
    /// Puppets have their AI off, so on their own they fire nothing on the client. Instead the host
    /// reports every bullet a synced enemy fires, and the client fires the same bullet from that
    /// enemy's puppet - so it hits (and hurts) the client's player locally; the client owns damage to
    /// itself. Bullets fly straight from where/how they were fired: scripted curves, homing and splits
    /// aren't reproduced (option B, script replay, is planned for bosses).
    ///
    /// Host capture, via Harmony (GungeonTogether.Patches.ProjectilePatches), covers the three ways
    /// enemies fire: BulletScripts (AIBulletBank.BulletSpawnedHandler), direct bank shots
    /// (CreateProjectileFromBank) and held-gun volleys (AIShooter → SpawnManager.SpawnProjectile).
    /// Callers adjust speed after spawning (black phantoms, override data), so captures are reported
    /// in LateUpdate with their final speed/direction rather than at spawn.
    /// </summary>
    public class ProjectileReplicator : MonoSingleton<ProjectileReplicator>
    {
        private class Captured
        {
            public Projectile Projectile;
            public AIActor Owner;
            public EnemyProjectileKind Kind;
            public string BankName;
            public Bullet ScriptBullet; // set for BulletScript bullets - their motion lives here, not on the Projectile
        }

        private static readonly List<Captured> _captured = new List<Captured>();
        private static int _scriptSpawnDepth;
        private static int _shooterDepth;
        private static AIShooter _shooter;

        private static bool Capturing => NetworkSession.Instance.IsHost;

        // ---- Entry points for the Harmony patches (host capture) ----

        public static void EnterScriptSpawn() => _scriptSpawnDepth++;
        public static void ExitScriptSpawn() { if (_scriptSpawnDepth > 0) _scriptSpawnDepth--; }

        /// <summary>BulletSpawnedHandler postfix: a BulletScript bullet now has its Projectile.</summary>
        public static void CaptureScriptBullet(AIBulletBank bank, Bullet bullet)
        {
            if (!Capturing || bank == null || bullet == null || bullet.Projectile == null) return;
            if (ScriptReplicator.IsReplayed(bullet)) return; // the client runs this script itself (4c-2)
            Add(bullet.Projectile, bank.aiActor, EnemyProjectileKind.Bank, bullet.BankName, bullet);
        }

        /// <summary>CreateProjectileFromBank postfix - direct bank shots only; script bullets are captured above.</summary>
        public static void CaptureBankShot(AIBulletBank bank, GameObject projectileObject, string bulletName)
        {
            if (!Capturing || _scriptSpawnDepth > 0 || bank == null || projectileObject == null) return;
            Add(projectileObject.GetComponent<Projectile>(), bank.aiActor, EnemyProjectileKind.Bank, bulletName, null);
        }

        public static void BeginShooter(AIShooter shooter)
        {
            _shooterDepth++;
            _shooter = shooter;
        }

        public static void EndShooter()
        {
            if (--_shooterDepth > 0) return;
            _shooterDepth = 0;
            _shooter = null;
        }

        /// <summary>SpawnManager.SpawnProjectile postfix - hot path; only captures inside an AIShooter volley.</summary>
        public static void OnProjectileSpawned(GameObject projectileObject)
        {
            if (_shooterDepth == 0 || _shooter == null || projectileObject == null || !Capturing) return;
            Add(projectileObject.GetComponent<Projectile>(), _shooter.aiActor, EnemyProjectileKind.Gun, null, null);
        }

        private static void Add(Projectile projectile, AIActor owner, EnemyProjectileKind kind, string bankName, Bullet scriptBullet)
        {
            if (projectile == null || owner == null) return;
            _captured.Add(new Captured { Projectile = projectile, Owner = owner, Kind = kind, BankName = bankName, ScriptBullet = scriptBullet });
        }

        // ---- Host: report ----

        private void LateUpdate()
        {
            if (_captured.Count == 0) return;
            if (!NetworkSession.Instance.IsHost)
            {
                _captured.Clear();
                return;
            }

            foreach (Captured c in _captured)
            {
                if (c.Projectile == null || c.Owner == null) continue;
                // Only enemies the client has a puppet for (skips bosses and anything outside the host's room).
                if (!NetworkEntityManager.Instance.TryGetId(c.Owner, out int enemyId)) continue;

                float direction, speed;
                Vector2 position;
                if (c.ScriptBullet != null)
                {
                    direction = c.ScriptBullet.Direction;
                    speed = c.ScriptBullet.Speed;
                    position = c.ScriptBullet.Position;
                }
                else
                {
                    direction = c.Projectile.Direction.ToAngle();
                    speed = c.Projectile.Speed;
                    position = c.Projectile.transform.position;
                }

                NetworkSession.Instance.Broadcast(new EnemyProjectilePacket
                {
                    EnemyId = enemyId,
                    Kind = c.Kind,
                    BankName = c.BankName,
                    Position = position,
                    Direction = direction,
                    Speed = speed
                }, reliable: false);
            }
            _captured.Clear();
        }

        // ---- Client: fire it from the puppet ----

        public void HandleEnemyProjectile(EnemyProjectilePacket packet)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            if (remote == null) return;
            AIActor puppet = remote.GetComponent<AIActor>();
            if (puppet == null) return;

            try
            {
                GameObject projectileObject = SpawnFromPuppet(puppet, packet);
                if (projectileObject == null) return;

                Projectile projectile = projectileObject.GetComponent<Projectile>();
                if (projectile == null) return;

                // Straight-line approximation: nothing script-driven may steer it.
                BulletScriptBehavior scriptMotion = projectileObject.GetComponent<BulletScriptBehavior>();
                if (scriptMotion != null) scriptMotion.enabled = false;
                projectile.IsBulletScript = false;

                projectile.baseData.speed = packet.Speed;
                projectile.UpdateSpeed();
                projectile.SendInDirection(BraveMathCollege.DegreesToVector(packet.Direction), resetDistance: true);
            }
            catch (Exception e)
            {
                Debug.LogWarningThrottled($"Projectile.SpawnFailed:{packet.Kind}:{packet.BankName}",
                    $"[ProjectileReplicator] Couldn't fire {packet.Kind} bullet '{packet.BankName}' from puppet {packet.EnemyId}: {e.Message}");
            }
        }

        private static GameObject SpawnFromPuppet(AIActor puppet, EnemyProjectilePacket packet)
        {
            if (packet.Kind == EnemyProjectileKind.Bank)
            {
                if (puppet.bulletBank == null) return null;
                string bulletName = string.IsNullOrEmpty(packet.BankName) ? "default" : packet.BankName;
                // Also plays the enemy's muzzle flash/audio. Owner is set to the puppet by the bank.
                return puppet.bulletBank.CreateProjectileFromBank(packet.Position, packet.Direction, bulletName);
            }

            Gun gun = puppet.aiShooter != null ? puppet.aiShooter.CurrentGun : null;
            Projectile prefab = gun != null && gun.singleModule != null ? gun.singleModule.GetCurrentProjectile() : null;
            if (prefab == null) return null;

            GameObject spawned = SpawnManager.SpawnProjectile(prefab.gameObject, packet.Position, Quaternion.Euler(0f, 0f, packet.Direction));
            Projectile projectile = spawned != null ? spawned.GetComponent<Projectile>() : null;
            if (projectile != null) projectile.SetOwnerSafe(puppet, puppet.ActorName);
            return spawned;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _captured.Clear();
            _scriptSpawnDepth = 0;
            _shooterDepth = 0;
            _shooter = null;
        }
    }
}
