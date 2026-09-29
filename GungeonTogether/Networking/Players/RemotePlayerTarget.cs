using System.Collections.Generic;
using UnityEngine;

namespace GungeonTogether.Networking.Players
{
    /// <summary>
    /// A stand-in enemies can target in place of a remote player. On the host its real enemies target
    /// it; on a client, enemy puppets are pointed at it so replayed bullet scripts aim at that player. The game only
    /// ever picks a local PlayerController as a target, so enemies ignored clients entirely - and with
    /// the host a ghost, they stopped attacking. A target can be any GameActor (charmed enemies target
    /// other enemies), so this one is a bare GameActor with a player-sized hitbox that follows the avatar
    /// (EnemyReplicator.OnTargetSearch hands it out).
    ///
    /// It collides with nothing: bullets aimed at it fly on through on the host, and the client, whose
    /// copies of those bullets hit its real player, takes the damage. No HealthHaver - the game's
    /// target checks all allow for a target without one.
    /// </summary>
    public class RemotePlayerTarget : GameActor
    {
        public ulong SteamId { get; private set; }

        public override Gun CurrentGun => null;
        public override Transform GunPivot => transform;
        public override bool SpriteFlipped => false;
        public override Vector3 SpriteDimensions => Vector3.one;

        public static RemotePlayerTarget Create(Transform avatar, ulong steamId)
        {
            GameObject go = new GameObject("EnemyTarget");
            go.transform.parent = avatar;
            go.transform.localPosition = Vector3.zero;

            // Set up before its Start registers it with the physics engine.
            SpeculativeRigidbody body = go.AddComponent<SpeculativeRigidbody>();
            body.CollideWithOthers = false;
            body.CollideWithTileMap = false;
            // A ground collider too: behaviours read the target's GroundPixelCollider (the
            // Tarnisher's grab) and ground centre, and there's no null check where they do.
            body.PixelColliders = new List<PixelCollider> { PlayerSizedGround(), PlayerSizedHitbox() };

            RemotePlayerTarget target = go.AddComponent<RemotePlayerTarget>();
            target.SteamId = steamId;
            return target;
        }

        /// <summary>The local player's feet collider shape.</summary>
        private static PixelCollider PlayerSizedGround()
        {
            PlayerController local = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            PixelCollider ground = local != null && local.specRigidbody != null ? local.specRigidbody.GroundPixelCollider : null;
            return new PixelCollider
            {
                ColliderGenerationMode = PixelCollider.PixelColliderGeneration.Manual,
                CollisionLayer = CollisionLayer.PlayerCollider,
                ManualOffsetX = ground != null ? ground.Offset.x : 3,
                ManualOffsetY = ground != null ? ground.Offset.y : 0,
                ManualWidth = ground != null ? ground.Dimensions.x : 10,
                ManualHeight = ground != null ? ground.Dimensions.y : 4
            };
        }

        /// <summary>The local player's hitbox shape (characters differ only slightly), so enemies aim at the body's centre.</summary>
        private static PixelCollider PlayerSizedHitbox()
        {
            PlayerController local = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            PixelCollider hitbox = local != null && local.specRigidbody != null ? local.specRigidbody.HitboxPixelCollider : null;
            return new PixelCollider
            {
                ColliderGenerationMode = PixelCollider.PixelColliderGeneration.Manual,
                CollisionLayer = CollisionLayer.PlayerHitBox,
                ManualOffsetX = hitbox != null ? hitbox.Offset.x : 3,
                ManualOffsetY = hitbox != null ? hitbox.Offset.y : 1,
                ManualWidth = hitbox != null ? hitbox.Dimensions.x : 10,
                ManualHeight = hitbox != null ? hitbox.Dimensions.y : 12
            };
        }

        /// <summary>
        /// AIActor line-of-sight prefix. The physics raycast skips bodies with CollideWithOthers off,
        /// so enemies never "saw" a stand-in and every attack needing line of sight held fire - they
        /// turned towards the client but only ever shot the host. Collides for this one raycast.
        /// Returns the body to restore afterwards (null when it isn't a stand-in).
        /// </summary>
        public static SpeculativeRigidbody BeginSightCheck(SpeculativeRigidbody target)
        {
            if (target == null || target.CollideWithOthers || target.GetComponent<RemotePlayerTarget>() == null) return null;
            target.CollideWithOthers = true;
            return target;
        }

        public static void EndSightCheck(SpeculativeRigidbody target)
        {
            if (target != null) target.CollideWithOthers = false;
        }

        /// <summary>After the avatar moved: the rigidbody keeps its own position (see CLAUDE.md).</summary>
        public void SyncPosition()
        {
            if (specRigidbody != null) specRigidbody.Reinitialize();
        }
    }
}
