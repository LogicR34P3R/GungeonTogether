using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Entities;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Client damage to enemies (step 4b). Puppets are immune locally (PreventAllDamage) so the
    /// client can't kill one and drop loot of its own - but its hits must still count. A Harmony
    /// prefix on HealthHaver.ApplyDamage (GungeonTogether.Patches.DamagePatches) catches every hit on
    /// a puppet before that immunity check, and this forwards it to the host, which applies it to
    /// the real enemy with the game's own ApplyDamage: resistances, hit flashes and death logic all
    /// run there, and the resulting death, drops and room clear reach clients through the existing
    /// sync. Hits are summed per enemy per frame (beams and shotguns hit many times a frame).
    ///
    /// Damage to the client's own player stays local (4a: the client owns damage to itself).
    /// </summary>
    public class DamageReplicator : MonoSingleton<DamageReplicator>
    {
        // Host-side sanity bound per packet - a frame's worth of hits on one enemy won't come near it.
        private const float MaxDamagePerPacket = 10000f;

        private struct PendingKey
        {
            public int EnemyId;
            public DamageCategory Category;
        }

        private class Pending
        {
            public float Damage;
            public Vector2 Direction;
            public CoreDamageTypes Types;
        }

        private readonly Dictionary<PendingKey, Pending> _pending = new Dictionary<PendingKey, Pending>();

        /// <summary>
        /// ApplyDamage prefix. Returns false (skip the original) for hits on puppets on a client -
        /// that also stops Unstoppable damage from switching the puppet's PreventAllDamage off.
        /// </summary>
        public static bool OnApplyDamage(HealthHaver healthHaver, float damage, Vector2 direction,
            CoreDamageTypes damageTypes, DamageCategory damageCategory)
        {
            if (!NetworkSession.Instance.IsClient || healthHaver == null || healthHaver.aiActor == null) return true;

            NetworkPuppet puppet = healthHaver.GetComponent<NetworkPuppet>();
            if (puppet == null) return true;

            if (damage > 0f) Instance.Accumulate(puppet.EnemyId, damage, direction, damageTypes, damageCategory);
            return false;
        }

        private void Accumulate(int enemyId, float damage, Vector2 direction, CoreDamageTypes types, DamageCategory category)
        {
            var key = new PendingKey { EnemyId = enemyId, Category = category };
            if (!_pending.TryGetValue(key, out Pending pending))
            {
                pending = new Pending();
                _pending[key] = pending;
            }
            pending.Damage += damage;
            pending.Direction = direction;
            pending.Types |= types;
        }

        private void LateUpdate()
        {
            if (_pending.Count == 0) return;
            if (NetworkSession.Instance.IsClient)
            {
                foreach (var kv in _pending)
                {
                    NetworkSession.Instance.SendToHost(new EnemyDamagePacket
                    {
                        EnemyId = kv.Key.EnemyId,
                        Damage = kv.Value.Damage,
                        Direction = kv.Value.Direction,
                        DamageTypes = (int)kv.Value.Types,
                        Category = (byte)kv.Key.Category
                    }, reliable: true);
                }
            }
            _pending.Clear();
        }

        // ---- Host ----

        public void HandleEnemyDamage(ulong senderId, EnemyDamagePacket packet)
        {
            if (float.IsNaN(packet.Damage) || float.IsInfinity(packet.Damage) || packet.Damage <= 0f || packet.Damage > MaxDamagePerPacket)
            {
                Debug.LogWarningThrottled($"Damage.Invalid:{senderId}", $"[DamageReplicator] Ignored implausible damage {packet.Damage} from {senderId}.");
                return;
            }

            if (!NetworkEntityManager.Instance.TryGetEntity(packet.EnemyId, out object entity)) return; // room changed meanwhile
            AIActor enemy = entity as AIActor;
            if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead) return;

            enemy.healthHaver.ApplyDamage(packet.Damage, packet.Direction, "Co-op partner",
                (CoreDamageTypes)packet.DamageTypes, (DamageCategory)packet.Category);
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState() => _pending.Clear();
    }
}
