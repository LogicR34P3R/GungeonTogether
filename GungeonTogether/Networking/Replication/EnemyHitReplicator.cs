using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Players;
using GungeonTogether.Networking.Session;
using GungeonTogether.Systems;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Replication
{
    /// <summary>
    /// Enemy attacks that hurt their target directly instead of with bullets. On the host they hit
    /// the target's HealthHaver or grab a local PlayerController; a client's stand-in
    /// (RemotePlayerTarget) has neither, so the client never took them. The host runs the same
    /// range check against the stand-in and sends that client an EnemyHit.
    ///
    /// - Gatling Gull: leap landing ("land_impact") and beak strike ("melee_hit").
    /// - Tarnisher (ConsumeTargetBehavior): the grab ("hit"), then the release, which punishes as
    ///   vanilla does - damage, a smaller clip, 15% of the gun's ammo.
    ///
    /// A HealthHaver on the stand-in was rejected: it joins StaticReferenceManager.AllHealthHavers,
    /// so the host's own explosions would count it as an enemy (friendly fire), and its OnDestroy
    /// throws without a sprite animator. Harmony wiring: GungeonTogether.Patches.EnemyHitPatches.
    /// </summary>
    public class EnemyHitReplicator : MonoSingleton<EnemyHitReplicator>
    {
        // ---- Host: Gatling Gull ----

        /// <summary>GatlingGullLeapBehavior.HandleAnimationEvent prefix (observe): the same test the game runs on landing.</summary>
        public static void OnGullLeapEvent(GatlingGullLeapBehavior leap, AIActor gull, tk2dSpriteAnimationClip clip, int frameNo)
        {
            if (!NetworkSession.Instance.IsHost || leap == null || gull == null || clip == null) return;
            if (clip.GetFrame(frameNo).eventInfo != "land_impact") return;
            if (!TryGetStandIn(gull.TargetRigidbody, out RemotePlayerTarget standIn)) return;

            Vector2 direction = gull.TargetRigidbody.UnitCenter - gull.specRigidbody.UnitCenter;
            if (direction.magnitude >= leap.DamageRadius) return;
            SendDamage(standIn, leap.Damage, direction, leap.Force, gull);
        }

        /// <summary>GatlingGullMelee.HandleAnimationEvent prefix (observe).</summary>
        public static void OnGullMeleeEvent(GatlingGullMelee melee, AIActor gull, tk2dSpriteAnimationClip clip, int frameNo)
        {
            if (!NetworkSession.Instance.IsHost || melee == null || gull == null || clip == null || melee.CenterPoint == null) return;
            if (clip.GetFrame(frameNo).eventInfo != "melee_hit") return;
            SpeculativeRigidbody target = gull.TargetRigidbody;
            if (!TryGetStandIn(target, out RemotePlayerTarget standIn)) return;

            Vector2 direction = target.GetUnitCenter(ColliderType.HitBox) - (Vector2)melee.CenterPoint.transform.position;
            if (direction.magnitude >= melee.DamageDistance) return;
            SendDamage(standIn, melee.Damage, direction, melee.KnockbackForce, gull);
        }

        private static bool TryGetStandIn(SpeculativeRigidbody target, out RemotePlayerTarget standIn)
        {
            standIn = target != null ? target.GetComponent<RemotePlayerTarget>() : null;
            return standIn != null;
        }

        private static void SendDamage(RemotePlayerTarget standIn, float damage, Vector2 direction, float knockback, AIActor source)
        {
            NetworkSession.Instance.SendPacket(standIn.SteamId, new EnemyHitPacket
            {
                Kind = EnemyHitPacket.HitKind.Damage,
                Damage = damage,
                Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Random.insideUnitCircle.normalized,
                Knockback = knockback,
                Source = source.GetActorName()
            }, reliable: true);
        }

        // ---- Host: Tarnisher ----

        private static readonly MethodInfo FlattenMethod = typeof(ConsumeTargetBehavior).GetMethod("Flatten", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo SafeEndPointMethod = typeof(ConsumeTargetBehavior).GetMethod("GetSafeEndPoint", BindingFlags.NonPublic | BindingFlags.Instance);

        // Tarnishers holding a client right now → that client.
        private static readonly Dictionary<ConsumeTargetBehavior, ulong> _grabbed = new Dictionary<ConsumeTargetBehavior, ulong>();

        /// <summary>
        /// ConsumeTargetBehavior.AnimationEventTriggered prefix. On its "hit" frame with a stand-in in
        /// its jaws, run the game's success path (all but ConsumePlayer, which needs a local player)
        /// and tell that client it was swallowed. Returns false to skip the original, which would
        /// call it a miss: the stand-in isn't a PlayerController.
        /// </summary>
        public static bool OnConsumeEvent(ConsumeTargetBehavior consume, AIActor tarnisher, BehaviorSpeculator speculator,
            ref ConsumeTargetBehavior.State state, tk2dSpriteAnimationClip clip, int frameNo)
        {
            if (!NetworkSession.Instance.IsHost || consume == null || tarnisher == null || clip == null) return true;
            if (state != ConsumeTargetBehavior.State.GrabBegin || clip.GetFrame(frameNo).eventInfo != "hit") return true;
            SpeculativeRigidbody target = speculator != null ? speculator.TargetRigidbody : null;
            if (!TryGetStandIn(target, out RemotePlayerTarget standIn)) return true;
            if (FlattenMethod == null || SafeEndPointMethod == null) return true;

            PixelCollider theirs = target.HitboxPixelCollider;
            PixelCollider mine = tarnisher.specRigidbody != null ? tarnisher.specRigidbody.HitboxPixelCollider : null;
            if (theirs == null || mine == null || !theirs.Overlaps(mine)) return true; // a miss: the original handles it

            consume.ForceBlank();
            FlattenMethod.Invoke(consume, new object[] { false });
            tarnisher.knockbackDoer.SetImmobile(true, "ConsumeTargetBehavior");
            state = ConsumeTargetBehavior.State.GrabSuccess;
            SafeEndPointMethod.Invoke(consume, null);

            _grabbed[consume] = standIn.SteamId;
            NetworkSession.Instance.SendPacket(standIn.SteamId, new EnemyHitPacket { Kind = EnemyHitPacket.HitKind.Grab, Source = tarnisher.GetActorName() }, reliable: true);
            Debug.Log($"[EnemyHit] {tarnisher.GetActorName()} grabbed player {standIn.SteamId}.");
            return false;
        }

        /// <summary>ConsumeTargetBehavior.UnconsumePlayer prefix: the release, punished when the grab ran its course.</summary>
        public static void OnUnconsume(ConsumeTargetBehavior consume, AIActor tarnisher, bool punish)
        {
            if (consume == null || !_grabbed.TryGetValue(consume, out ulong steamId)) return;
            _grabbed.Remove(consume);
            bool jammed = tarnisher != null && tarnisher.IsBlackPhantom;
            NetworkSession.Instance.SendPacket(steamId, new EnemyHitPacket
            {
                Kind = EnemyHitPacket.HitKind.Release,
                Damage = punish ? (jammed ? 1f : 0.5f) : 0f,
                ClipPenalty = punish ? consume.PlayerClipSizePenalty : 0f,
                Source = tarnisher != null ? tarnisher.GetActorName() : ""
            }, reliable: true);
        }

        /// <summary>
        /// EndContinuousUpdate/Destroy prefix: an interrupted grab (Tarnisher killed, attack cut
        /// short). The game only releases when it holds a local player, so release ours here.
        /// </summary>
        public static void OnConsumeEnded(ConsumeTargetBehavior consume, AIActor tarnisher) => OnUnconsume(consume, tarnisher, punish: false);

        // ---- Client ----

        // Released by ourselves if the host's release never comes (it left, the Tarnisher vanished).
        private const float MaxGrabSeconds = 6f;
        private float _grabbedSince = -1f;

        public void HandleHit(EnemyHitPacket packet)
        {
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (player == null || player.healthHaver == null) return;

            switch (packet.Kind)
            {
                case EnemyHitPacket.HitKind.Damage:
                    if (player.IsGhost || player.healthHaver.IsDead || player.IsEthereal) return;
                    if (packet.Damage > 0f) player.healthHaver.ApplyDamage(packet.Damage, packet.Direction, packet.Source);
                    if (packet.Knockback > 0f && player.knockbackDoer != null) player.knockbackDoer.ApplyKnockback(packet.Direction, packet.Knockback);
                    break;
                case EnemyHitPacket.HitKind.Grab:
                    Grab(player);
                    break;
                case EnemyHitPacket.HitKind.Release:
                    Release(player, packet);
                    break;
            }
        }

        /// <summary>ConsumeTargetBehavior.ConsumePlayer, for our own player.</summary>
        private void Grab(PlayerController player)
        {
            if (_grabbedSince >= 0f || !player.CanBeGrabbed) return;
            _grabbedSince = Time.realtimeSinceStartup;
            player.specRigidbody.Velocity = Vector2.zero;
            player.knockbackDoer.TriggerTemporaryKnockbackInvulnerability(1f);
            player.ToggleRenderer(false, "consumed");
            player.ToggleHandRenderers(false, "consumed");
            player.ToggleGunRenderers(false, "consumed");
            player.CurrentInputState = PlayerInputState.NoInput;
            player.healthHaver.IsVulnerable = false;
            Debug.LogInfo("[EnemyHit] Grabbed by the host's Tarnisher.");
        }

        /// <summary>ConsumeTargetBehavior.UnconsumePlayer + PunishPlayer, for our own player.</summary>
        private void Release(PlayerController player, EnemyHitPacket packet)
        {
            if (_grabbedSince < 0f) return;
            _grabbedSince = -1f;
            player.healthHaver.IsVulnerable = true;
            if (packet != null && packet.Damage > 0f)
            {
                player.healthHaver.ApplyDamage(packet.Damage, Vector2.zero, packet.Source);
                if (player.ownerlessStatModifiers != null && player.stats != null && packet.ClipPenalty > 0f)
                {
                    player.ownerlessStatModifiers.Add(new StatModifier
                    {
                        statToBoost = PlayerStats.StatType.TarnisherClipCapacityMultiplier,
                        amount = -packet.ClipPenalty,
                        modifyType = StatModifier.ModifyMethod.ADDITIVE
                    });
                    player.stats.RecalculateStats(player);
                }
                if (player.CurrentGun != null && player.CurrentGun.ammo > 0)
                {
                    player.CurrentGun.ammo = Mathf.RoundToInt(player.CurrentGun.ammo * 0.85f);
                }
            }
            player.ToggleRenderer(true, "consumed");
            player.ToggleHandRenderers(true, "consumed");
            player.ToggleGunRenderers(true, "consumed");
            player.CurrentInputState = PlayerInputState.AllInput;
            player.DoSpitOut();
        }

        private void Update()
        {
            if (_grabbedSince < 0f || Time.realtimeSinceStartup - _grabbedSince < MaxGrabSeconds) return;
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (player != null) Release(player, null);
            else _grabbedSince = -1f;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _grabbed.Clear();
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (_grabbedSince >= 0f && player != null) Release(player, null);
            _grabbedSince = -1f;
        }
    }
}
