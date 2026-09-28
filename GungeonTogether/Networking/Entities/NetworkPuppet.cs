using UnityEngine;

namespace GungeonTogether.Networking.Entities
{
    /// <summary>
    /// Marks a client-side AIActor as a puppet of a host enemy (spawned by EnemyReplicator), so the
    /// client's native-enemy sweep never removes it, and records which host enemy it stands for so
    /// hits on it can be forwarded (DamageReplicator).
    ///
    /// Also moves the puppet between the host's position updates (10 Hz): it extrapolates along the
    /// last observed velocity - plus a lead of half the ping, so it's shown roughly where the host
    /// has it *now* rather than where it was when the update was sent - and eases towards that
    /// instead of snapping. Every move also re-syncs the physics body: SpeculativeRigidbody keeps
    /// its own position and only follows the transform on Reinitialize(), so without it the
    /// puppet's hitbox stays where it spawned and the client's shots hit empty air.
    /// </summary>
    public class NetworkPuppet : MonoBehaviour
    {
        // Jumps bigger than this (teleports, spawn corrections) snap instead of sliding across the room.
        private const float SnapDistance = 4f;
        // How quickly the drawn position eases towards the predicted one (per second).
        private const float FollowRate = 15f;
        // Never extrapolate further than this - if updates stop, the puppet must not keep drifting.
        private const float MaxExtrapolationSeconds = 0.25f;

        public int EnemyId;

        // Got the host's death animation frames (EnemyState.Dying): the death was already shown.
        public bool SawDying;

        private SpeculativeRigidbody _body;
        private bool _hasTarget;
        private Vector2 _target;
        private Vector2 _velocity;
        private float _targetTime;
        private float _leadSeconds;

        private void Awake()
        {
            _body = GetComponent<SpeculativeRigidbody>();
        }

        /// <param name="leadSeconds">One-way network delay to compensate for (half the ping).</param>
        public void ApplyState(Vector2 position, float leadSeconds)
        {
            float now = Time.realtimeSinceStartup;
            bool snap = !_hasTarget || Vector2.Distance(transform.position, position) > SnapDistance;

            if (_hasTarget && !snap && now - _targetTime > 0.001f)
            {
                _velocity = (position - _target) / (now - _targetTime);
            }
            else
            {
                _velocity = Vector2.zero; // first update or a teleport - no meaningful velocity yet
            }

            _target = position;
            _targetTime = now;
            _leadSeconds = leadSeconds;
            _hasTarget = true;

            if (snap) MoveTo(position);
        }

        private void Update()
        {
            if (!_hasTarget) return;

            float ahead = Mathf.Min(Time.realtimeSinceStartup - _targetTime + _leadSeconds, MaxExtrapolationSeconds);
            Vector2 predicted = _target + _velocity * ahead;
            MoveTo(Vector2.Lerp(transform.position, predicted, Mathf.Clamp01(Time.deltaTime * FollowRate)));
        }

        private void MoveTo(Vector2 position)
        {
            transform.position = new Vector3(position.x, position.y, transform.position.z);
            if (_body != null) _body.Reinitialize();
        }
    }
}
