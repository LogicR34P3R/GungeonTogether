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
            public int EnemyId;      // resolved at capture: the owner may be gone by LateUpdate
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
            Add(bullet.Projectile, OwnerOf(bank), EnemyProjectileKind.Bank, bullet.BankName, bullet);
        }

        /// <summary>CreateProjectileFromBank postfix - direct bank shots only; script bullets are captured above.</summary>
        public static void CaptureBankShot(AIBulletBank bank, GameObject projectileObject, string bulletName)
        {
            if (!Capturing || _scriptSpawnDepth > 0 || bank == null || projectileObject == null) return;
            Add(projectileObject.GetComponent<Projectile>(), OwnerOf(bank), EnemyProjectileKind.Bank, bulletName, null);
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

        // WizardSpinShootBehavior (Gunjurers): spawns bullets straight through SpawnManager, outside any
        // path above, circles them around the caster, then releases them one by one - none ever
        // reached the client. Comparing the held bullets before and after each step: a new one
        // starts circling (SpinHold - the client circles a copy of its own), one no longer held was
        // just sent flying (SpinRelease - the client launches that copy).

        /// <summary>Prefix: the bullets circling the caster right now (null when not capturing).</summary>
        public static List<Projectile> HeldSpinBullets(List<Tuple<Projectile, float>> held)
        {
            if (!Capturing || held == null) return null;
            var projectiles = new List<Projectile>(held.Count);
            foreach (Tuple<Projectile, float> entry in held)
            {
                if (entry != null && entry.First != null) projectiles.Add(entry.First);
            }
            return projectiles;
        }

        /// <summary>Postfix: report bullets that started circling and ones that were sent flying.</summary>
        public static void CaptureSpinChanges(WizardSpinShootBehavior behavior, AIActor caster, List<Tuple<Projectile, float>> held, List<Projectile> before)
        {
            if (before == null || caster == null) return;
            foreach (Projectile projectile in before)
            {
                if (projectile == null || projectile.ManualControl || IsHeld(held, projectile)) continue;
                Add(projectile, caster, EnemyProjectileKind.SpinRelease, behavior.OverrideBulletName, null);
            }

            if (held == null || behavior.ShootPoint == null) return;
            foreach (Tuple<Projectile, float> entry in held)
            {
                if (entry == null || entry.First == null || before.Contains(entry.First)) continue;
                if (!EnemyReplicator.Instance.TryGetSyncedId(caster, out int enemyId)) return;
                // Sent at once, reliably: its release (reported in LateUpdate) must find it there.
                NetworkSession.Instance.Broadcast(new EnemyProjectilePacket
                {
                    EnemyId = enemyId,
                    Kind = EnemyProjectileKind.SpinHold,
                    BankName = behavior.OverrideBulletName,
                    Position = behavior.ShootPoint.position - caster.transform.position,
                    Direction = entry.Second,
                    Speed = behavior.BulletCircleSpeed,
                    Radius = behavior.BulletCircleRadius
                }, reliable: true);
                _statSent++;
            }
        }

        private static bool IsHeld(List<Tuple<Projectile, float>> held, Projectile projectile)
        {
            if (held == null) return false;
            foreach (Tuple<Projectile, float> entry in held)
            {
                if (entry != null && entry.First == projectile) return true;
            }
            return false;
        }

        /// <summary>
        /// The enemy a bullet bank fires for. A bank on a child object has no aiActor of its own,
        /// and its bullets were dropped entirely (no owner, so neither replayed nor copied).
        /// </summary>
        public static AIActor OwnerOf(AIBulletBank bank)
        {
            if (bank == null) return null;
            return bank.aiActor != null ? bank.aiActor : bank.GetComponentInParent<AIActor>();
        }

        private static void Add(Projectile projectile, AIActor owner, EnemyProjectileKind kind, string bankName, Bullet scriptBullet)
        {
            if (projectile == null || owner == null) return;
            // Only enemies the client has a puppet for (or gets one for right now). Resolved here, not
            // in LateUpdate: suicide shooters (Bullats) fire, kill themselves and destroy their object
            // in the same frame, and a bullet whose owner was gone by LateUpdate was never sent.
            if (!EnemyReplicator.Instance.TryGetSyncedId(owner, out int enemyId)) return;
            _captured.Add(new Captured { Projectile = projectile, Owner = owner, EnemyId = enemyId, Kind = kind, BankName = bankName, ScriptBullet = scriptBullet });
        }

        // ---- Host: report ----

        // ---- Diagnostics ----
        // Clients took damage from replayed enemy bullets they couldn't see, so: counts every 15s,
        // plus the render state of one sample bullet a moment after it's fired (either side), to
        // compare a bullet that shows with one that doesn't.

        private const float StatsInterval = 15f;
        private static int _statSent, _statReceived, _statFired, _statNoPuppet, _statFailed;
        private static float _nextStatsTime;
        private static Projectile _sample;
        private static string _sampleLabel;
        private static float _sampleTime, _nextSampleAllowed;

        public static void SampleForDiagnostics(Projectile projectile, string label)
        {
            float now = Time.realtimeSinceStartup;
            if (_sample != null || now < _nextSampleAllowed || projectile == null) return;
            _sample = projectile;
            _sampleLabel = label;
            _sampleTime = now;
            _nextSampleAllowed = now + StatsInterval;
        }

        private static void UpdateDiagnostics()
        {
            float now = Time.realtimeSinceStartup;
            if (_sampleLabel != null && now - _sampleTime >= 0.25f)
            {
                if (_sample == null)
                {
                    Debug.LogInfo($"[ProjectileReplicator] Sample {_sampleLabel}: gone within 0.25s.");
                }
                else
                {
                    Renderer r = _sample.sprite != null ? _sample.sprite.renderer : _sample.GetComponentInChildren<Renderer>();
                    Vector3 p = _sample.transform.position;
                    string render = r == null ? "no renderer"
                        : $"layer={LayerMask.LayerToName(r.gameObject.layer)}, enabled={r.enabled}, isVisible={r.isVisible}, spriteZ={r.transform.position.z:0.0}";
                    Debug.LogInfo($"[ProjectileReplicator] Sample {_sampleLabel} '{_sample.name}': pos={p}, active={_sample.gameObject.activeInHierarchy}, {render}, " +
                                  $"heightOffGround={(_sample.sprite != null ? _sample.sprite.HeightOffGround : 0f):0.00}");
                }
                _sample = null;
                _sampleLabel = null;
            }

            if (now < _nextStatsTime) return;
            _nextStatsTime = now + StatsInterval;
            if (_statSent + _statReceived == 0) return;
            Debug.LogInfo($"[ProjectileReplicator] Last {StatsInterval:0}s: sent={_statSent}, received={_statReceived}, fired={_statFired}, " +
                          $"noPuppet={_statNoPuppet}, failed={_statFailed}");
            _statSent = _statReceived = _statFired = _statNoPuppet = _statFailed = 0;
        }

        private void LateUpdate()
        {
            UpdateDiagnostics();
            if (_captured.Count == 0) return;
            if (!NetworkSession.Instance.IsHost)
            {
                _captured.Clear();
                return;
            }

            foreach (Captured c in _captured)
            {
                if (c.Projectile == null) continue;
                // A shot from an enemy that just died (a Bullat's burst) goes reliably: the same
                // ordered channel as its EnemyDeath, so it can't arrive after the puppet is removed.
                bool ownerGone = c.Owner == null || c.Owner.healthHaver == null || c.Owner.healthHaver.IsDead;

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
                    EnemyId = c.EnemyId,
                    Kind = c.Kind,
                    BankName = c.BankName,
                    Position = position,
                    Direction = direction,
                    Speed = speed
                }, reliable: ownerGone || c.Kind == EnemyProjectileKind.SpinRelease);
                _statSent++;
                SampleForDiagnostics(c.Projectile, "host enemy bullet");
            }
            _captured.Clear();
        }

        // ---- Client: fire it from the puppet ----

        public void HandleEnemyProjectile(EnemyProjectilePacket packet)
        {
            _statReceived++;
            GameObject remote = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
            AIActor puppet = remote != null ? remote.GetComponent<AIActor>() : null;
            if (puppet == null)
            {
                _statNoPuppet++;
                return;
            }

            try
            {
                if (packet.Kind == EnemyProjectileKind.SpinHold)
                {
                    HoldSpinCopy(puppet, packet);
                    return;
                }
                if (packet.Kind == EnemyProjectileKind.SpinRelease && TryLaunchSpinCopy(packet))
                {
                    _statFired++;
                    return;
                }

                GameObject projectileObject = SpawnFromPuppet(puppet, packet);
                Projectile projectile = projectileObject != null ? projectileObject.GetComponent<Projectile>() : null;
                if (projectile == null)
                {
                    _statFailed++;
                    Debug.LogWarningThrottled($"Projectile.NoPrefab:{packet.Kind}:{packet.BankName}",
                        $"[ProjectileReplicator] Puppet {packet.EnemyId} has nothing to fire a {packet.Kind} bullet '{packet.BankName}' from.");
                    return;
                }

                // Straight-line approximation: nothing script-driven may steer it.
                BulletScriptBehavior scriptMotion = projectileObject.GetComponent<BulletScriptBehavior>();
                if (scriptMotion != null) scriptMotion.enabled = false;
                projectile.IsBulletScript = false;

                projectile.baseData.speed = packet.Speed;
                projectile.UpdateSpeed();
                projectile.SendInDirection(BraveMathCollege.DegreesToVector(packet.Direction), resetDistance: true);
                _statFired++;
                SampleForDiagnostics(projectile, "client enemy bullet");
            }
            catch (Exception e)
            {
                _statFailed++;
                Debug.LogWarningThrottled($"Projectile.SpawnFailed:{packet.Kind}:{packet.BankName}",
                    $"[ProjectileReplicator] Couldn't fire {packet.Kind} bullet '{packet.BankName}' from puppet {packet.EnemyId}: {e.Message}");
            }
        }

        private static GameObject SpawnFromPuppet(AIActor puppet, EnemyProjectilePacket packet)
        {
            if (packet.Kind != EnemyProjectileKind.Gun) // bank shots, and spin releases with no copy to launch
            {
                if (puppet.bulletBank == null) return null;
                string bulletName = string.IsNullOrEmpty(packet.BankName) ? "default" : packet.BankName;
                // Also plays the enemy's muzzle flash/audio. Owner is set to the puppet by the bank.
                return puppet.bulletBank.CreateProjectileFromBank(packet.Position, packet.Direction, bulletName);
            }

            // DefaultModule, not singleModule: guns with a volley (several modules) have no singleModule.
            Gun gun = puppet.aiShooter != null ? puppet.aiShooter.CurrentGun : null;
            ProjectileModule module = gun != null ? gun.DefaultModule : null;
            Projectile prefab = module != null ? module.GetCurrentProjectile() : null;
            if (prefab == null) return null;

            GameObject spawned = SpawnManager.SpawnProjectile(prefab.gameObject, packet.Position, Quaternion.Euler(0f, 0f, packet.Direction));
            Projectile projectile = spawned != null ? spawned.GetComponent<Projectile>() : null;
            if (projectile != null) projectile.SetOwnerSafe(puppet, puppet.ActorName);
            return spawned;
        }

        // ---- Client: Gunjurer spin copies ----

        // A copy nobody launched (release lost, host left) is dropped after this long.
        private const float MaxSpinSeconds = 12f;
        // A release launches the circling copy nearest its position, if one is this close.
        private const float SpinMatchDistance = 3f;

        private class SpinCopy
        {
            public Projectile Projectile;
            public AIActor Caster;
            public int EnemyId;
            public Vector2 CenterOffset;
            public float Angle, Speed, Radius, Since;
        }

        private readonly List<SpinCopy> _spinCopies = new List<SpinCopy>();

        /// <summary>The same bullet the caster spawns (WizardSpinShootBehavior's Spawn state), circling it.</summary>
        private void HoldSpinCopy(AIActor puppet, EnemyProjectilePacket packet)
        {
            if (puppet.bulletBank == null) return;
            AIBulletBank.Entry bullet = puppet.bulletBank.GetBullet(string.IsNullOrEmpty(packet.BankName) ? null : packet.BankName);
            if (bullet == null || bullet.BulletObject == null) return;

            var copy = new SpinCopy
            {
                Caster = puppet,
                EnemyId = packet.EnemyId,
                CenterOffset = packet.Position,
                Angle = packet.Direction,
                Speed = packet.Speed,
                Radius = packet.Radius,
                Since = Time.realtimeSinceStartup
            };
            GameObject spawned = SpawnManager.SpawnProjectile(bullet.BulletObject, SpinPosition(copy), Quaternion.identity);
            Projectile projectile = spawned != null ? spawned.GetComponent<Projectile>() : null;
            if (projectile == null) return;
            if (bullet.OverrideProjectile) projectile.baseData.SetAll(bullet.ProjectileData);
            projectile.SetOwnerSafe(puppet, puppet.ActorName);
            projectile.Shooter = puppet.specRigidbody;
            projectile.specRigidbody.Velocity = Vector2.zero;
            projectile.ManualControl = true;
            projectile.specRigidbody.CollideWithTileMap = false;
            projectile.UpdateCollisionMask();
            copy.Projectile = projectile;
            _spinCopies.Add(copy);
            _statFired++;
        }

        private static Vector2 SpinPosition(SpinCopy copy) =>
            (Vector2)copy.Caster.transform.position + copy.CenterOffset + BraveMathCollege.DegreesToVector(copy.Angle) * copy.Radius;

        private bool TryLaunchSpinCopy(EnemyProjectilePacket packet)
        {
            SpinCopy best = null;
            float bestDistance = SpinMatchDistance;
            foreach (SpinCopy copy in _spinCopies)
            {
                if (copy.EnemyId != packet.EnemyId || !IsAlive(copy.Projectile)) continue;
                float distance = Vector2.Distance(copy.Projectile.transform.position, packet.Position);
                if (distance < bestDistance)
                {
                    best = copy;
                    bestDistance = distance;
                }
            }
            if (best == null) return false;

            _spinCopies.Remove(best);
            Projectile projectile = best.Projectile;
            projectile.ManualControl = false;
            projectile.specRigidbody.CollideWithTileMap = true;
            projectile.baseData.speed = packet.Speed;
            projectile.UpdateSpeed();
            projectile.SendInDirection(BraveMathCollege.DegreesToVector(packet.Direction), resetDistance: true);
            projectile.transform.rotation = Quaternion.Euler(0f, 0f, packet.Direction);
            return true;
        }

        // Pooled projectiles are deactivated, not destroyed, when they die.
        private static bool IsAlive(Projectile projectile) => projectile != null && projectile.gameObject.activeInHierarchy;

        /// <summary>Circles the copies the way WizardSpinShootBehavior.ContinuousUpdate does: by velocity, so they still collide.</summary>
        private void Update()
        {
            if (_spinCopies.Count == 0) return;
            float dt = BraveTime.DeltaTime;
            for (int i = _spinCopies.Count - 1; i >= 0; i--)
            {
                SpinCopy copy = _spinCopies[i];
                if (!IsAlive(copy.Projectile) || !copy.Projectile.ManualControl)
                {
                    _spinCopies.RemoveAt(i);
                    continue;
                }
                if (copy.Caster == null || Time.realtimeSinceStartup - copy.Since > MaxSpinSeconds)
                {
                    copy.Projectile.DieInAir();
                    _spinCopies.RemoveAt(i);
                    continue;
                }
                if (dt <= 0f) continue;
                copy.Angle += copy.Speed * dt;
                Vector2 target = SpinPosition(copy);
                copy.Projectile.specRigidbody.Velocity = (target - (Vector2)copy.Projectile.transform.position) / dt;
                copy.Projectile.ResetDistance();
            }
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            foreach (SpinCopy copy in _spinCopies)
            {
                if (IsAlive(copy.Projectile)) copy.Projectile.DieInAir();
            }
            _spinCopies.Clear();
            _captured.Clear();
            _scriptSpawnDepth = 0;
            _shooterDepth = 0;
            _shooter = null;
        }
    }
}
