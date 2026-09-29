using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Object = UnityEngine.Object;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Explosions on the client. Every explosion ran on the host only: an enemy's death blast
    /// (ExplodeOnDeath, ExplodeInRadius, LeapExplosion), a Gatling Gull rocket (SkyRocket), a barrel
    /// the host broke - the client neither saw it nor got hurt by it. Puppets never die locally and
    /// a client's copies of those objects don't explode, so the host reports each one and the client
    /// plays it at the same spot with the same effect (found by prefab name), hurting only its own
    /// player: the host's blast already hit the real enemies.
    ///
    /// Not reported, because the client's own copy already explodes: enemy bullets (their
    /// projectiles on the client keep their ExplosiveModifier) and mirrored explosive pots
    /// (MinorBreakable). Harmony wiring: GungeonTogether.Patches.ExplosionPatches.
    /// </summary>
    public class ExplosionReplicator : MonoSingleton<ExplosionReplicator>
    {
        private static int _localOnlyDepth;   // inside an explosion the client plays by itself
        private static bool _skipInnerDefault; // Explode re-enters itself for useDefaultExplosion data

        /// <summary>Prefix on a source whose client copy explodes by itself; returns whether it counted.</summary>
        public static bool EnterLocalOnly(bool localOnly)
        {
            if (localOnly) _localOnlyDepth++;
            return localOnly;
        }

        public static void ExitLocalOnly(bool counted)
        {
            if (counted && _localOnlyDepth > 0) _localOnlyDepth--;
        }

        /// <summary>ExplosiveModifier.Explode: an enemy bullet's blast - the client's copy of the bullet does it.</summary>
        public static bool IsEnemyProjectile(ExplosiveModifier modifier) =>
            modifier != null && modifier.projectile != null && modifier.projectile.Owner is AIActor;

        /// <summary>Exploder.Explode prefix (host).</summary>
        public static void OnExplode(Vector3 position, ExplosionData data)
        {
            if (!NetworkSession.Instance.IsHost || data == null) return;
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer || gm.Dungeon == null || gm.Dungeon.sharedSettingsPrefab == null) return;

            ExplosionData defaults = gm.Dungeon.sharedSettingsPrefab.DefaultExplosionData;
            if (data == defaults && _skipInnerDefault)
            {
                _skipInnerDefault = false; // the outer call below already reported it
                return;
            }
            if (_localOnlyDepth > 0) return;

            // useDefaultExplosion: the numbers come from the default, which Explode calls itself
            // with next; who it ignores still comes from this one.
            bool usesDefault = data.useDefaultExplosion && defaults != null && data != defaults;
            ExplosionData effective = usesDefault ? defaults : data;
            _skipInnerDefault = usesDefault;

            // A player's own explosive ignores every player (ExplosiveModifier), in vanilla co-op too.
            PlayerController host = gm.PrimaryPlayer;
            bool ignoresPlayers = host != null && data.ignoreList != null && data.ignoreList.Contains(host.specRigidbody);
            bool hurtsPlayers = effective.doDamage && effective.damageToPlayer > 0f && !ignoresPlayers;

            NetworkSession.Instance.Broadcast(new ExplosionPacket
            {
                Position = position,
                DamageRadius = effective.GetDefinedDamageRadius(),
                DamageToPlayer = hurtsPlayers ? effective.damageToPlayer : 0f,
                PushRadius = effective.pushRadius,
                Force = effective.doForce && !effective.preventPlayerForce && !ignoresPlayers ? effective.force : 0f,
                DestroyProjectiles = effective.doDestroyProjectiles,
                ScreenShake = effective.doScreenShake,
                EffectName = effective.effect != null && (defaults == null || effective.effect != defaults.effect) ? effective.effect.name : "",
                ExplosionRing = effective.doExplosionRing,
                DefaultSfx = effective.playDefaultSFX
            }, reliable: true);
        }

        // ---- Client ----

        public void HandleExplosion(ExplosionPacket packet)
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.IsFoyer || gm.Dungeon == null || gm.Dungeon.sharedSettingsPrefab == null) return;
            ExplosionData defaults = gm.Dungeon.sharedSettingsPrefab.DefaultExplosionData;
            if (defaults == null) return;

            var data = new ExplosionData();
            data.CopyFrom(defaults); // the look (effect, sound, ring); a fresh ignore list
            data.useDefaultExplosion = false;
            data.forceUseThisRadius = true;
            data.damageRadius = packet.DamageRadius;
            data.doDamage = packet.DamageToPlayer > 0f;
            data.damageToPlayer = packet.DamageToPlayer;
            data.damage = 0f;               // enemies and props: the host's blast already hit them
            data.breakSecretWalls = false;
            data.forcePreventSecretWallDamage = true;
            data.isFreezeExplosion = false;
            data.doForce = packet.Force > 0f;
            data.force = packet.Force;
            data.pushRadius = packet.PushRadius;
            data.doDestroyProjectiles = packet.DestroyProjectiles;
            data.doScreenShake = packet.ScreenShake;
            data.doExplosionRing = packet.ExplosionRing;
            data.playDefaultSFX = packet.DefaultSfx;
            GameObject effect = FindEffect(packet.EffectName);
            if (effect != null) data.effect = effect;
            data.ignoreList = new List<SpeculativeRigidbody>();

            Exploder.Explode(new Vector3(packet.Position.x, packet.Position.y, 0f), data, Vector2.zero, null, ignoreQueues: true);
        }

        // Explosion effect prefabs by name. Whatever explodes on the host (an enemy, a Gull rocket) is
        // loaded here too - the client has the same enemies as puppets - so its effect prefab is in
        // memory. Searching all GameObjects is slow, so each name is looked up once, misses included.
        private readonly Dictionary<string, GameObject> _effects = new Dictionary<string, GameObject>();

        private GameObject FindEffect(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_effects.TryGetValue(name, out GameObject effect)) return effect;
            effect = null;
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
            {
                GameObject go = o as GameObject;
                // A prefab asset, not a live copy in the scene (those are named "... (Clone)" anyway).
                if (go != null && go.name == name && string.IsNullOrEmpty(go.scene.name))
                {
                    effect = go;
                    break;
                }
            }
            if (effect == null) Debug.LogWarning($"[Explosion] Effect '{name}' not found here; using the default look.");
            _effects[name] = effect;
            return effect;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _localOnlyDepth = 0;
            _skipInnerDefault = false;
        }
    }
}
