using System;
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
    /// Enemy laser beams on the client. A beam isn't a bullet anyone captures: AIBeamShooter (and the
    /// Beholster's BeholsterController) spawns a BasicBeamController and turns it every frame while
    /// the AI steers LaserAngle, so the client never saw or felt one. The host reports start, the
    /// angle (20 Hz) and stop; the client fires the same beam from its puppet and eases towards the
    /// host's angle, predicted along its turning speed, so the beam hurts the client's player locally.
    ///
    /// Shooters are identified by their index among the enemy's AIBeamShooters (same prefab on
    /// both sides, same order); BeamPacket.BeholsterEye for the Beholster's eye.
    /// Harmony wiring: GungeonTogether.Patches.BeamPatches.
    /// </summary>
    public class BeamReplicator : MonoSingleton<BeamReplicator>
    {
        private const float AngleInterval = 1f / 30f;
        private const float AngleThreshold = 0.5f;  // degrees
        private const float FollowRate = 25f;       // client easing, per second
        // A sweeping beam is predicted along its turning speed - by the time since the last update
        // plus half the ping - so it isn't drawn a network delay behind the host's. Never further
        // ahead than this, so a beam that stops turning doesn't overshoot for long.
        private const float MaxPredictSeconds = 0.25f;
        private const float MaxTurnRate = 720f;     // degrees per second; faster is a jump, not a sweep

        private class Beam
        {
            public Component Shooter; // AIBeamShooter or BeholsterController
            public int EnemyId;
            public int Index;
            public float Angle;       // host: last sent; client: host's latest
            public float AngleTime;   // client: when that arrived
            public float TurnRate;    // client: degrees per second between the last two updates
        }

        private static readonly List<Beam> _hostBeams = new List<Beam>();
        private readonly List<Beam> _clientBeams = new List<Beam>();
        private float _nextAngleTime;

        private static float AngleOf(Component shooter)
        {
            if (shooter is AIBeamShooter beam) return beam.LaserAngle;
            if (shooter is BeholsterController eye) return eye.LaserAngle;
            return 0f;
        }

        private static bool IsFiring(Component shooter)
        {
            if (shooter is AIBeamShooter beam) return beam.IsFiringLaser;
            if (shooter is BeholsterController eye) return eye.FiringLaser;
            return false;
        }

        // ---- Host ----

        /// <summary>StartFiringLaser postfix (AIBeamShooter / BeholsterController).</summary>
        public static void OnStart(Component shooter)
        {
            if (!NetworkSession.Instance.IsHost || shooter == null) return;
            AIActor enemy = shooter.GetComponentInParent<AIActor>();
            if (enemy == null || !EnemyReplicator.Instance.TryGetSyncedId(enemy, out int enemyId)) return;

            int index = shooter is BeholsterController
                ? BeamPacket.BeholsterEye
                : Array.IndexOf(enemy.GetComponentsInChildren<AIBeamShooter>(true), (AIBeamShooter)shooter);
            if (index == -1 && !(shooter is BeholsterController)) return;

            _hostBeams.RemoveAll(b => b.Shooter == shooter);
            float angle = AngleOf(shooter);
            _hostBeams.Add(new Beam { Shooter = shooter, EnemyId = enemyId, Index = index, Angle = angle });
            NetworkSession.Instance.Broadcast(new BeamPacket { Event = BeamPacket.BeamEvent.Start, EnemyId = enemyId, ShooterIndex = index, Angle = angle }, reliable: true);
        }

        /// <summary>StopFiringLaser prefix.</summary>
        public static void OnStop(Component shooter)
        {
            if (!NetworkSession.Instance.IsHost || shooter == null) return;
            for (int i = _hostBeams.Count - 1; i >= 0; i--)
            {
                if (_hostBeams[i].Shooter != shooter) continue;
                SendStop(_hostBeams[i]);
                _hostBeams.RemoveAt(i);
            }
        }

        private static void SendStop(Beam beam) =>
            NetworkSession.Instance.Broadcast(new BeamPacket { Event = BeamPacket.BeamEvent.Stop, EnemyId = beam.EnemyId, ShooterIndex = beam.Index }, reliable: true);

        private void LateUpdate()
        {
            if (!NetworkSession.Instance.IsHost || _hostBeams.Count == 0) return;
            if (Time.realtimeSinceStartup < _nextAngleTime) return;
            _nextAngleTime = Time.realtimeSinceStartup + AngleInterval;

            for (int i = _hostBeams.Count - 1; i >= 0; i--)
            {
                Beam beam = _hostBeams[i];
                if (beam.Shooter == null || !IsFiring(beam.Shooter))
                {
                    SendStop(beam); // the enemy died or was destroyed mid-beam
                    _hostBeams.RemoveAt(i);
                    continue;
                }
                float angle = AngleOf(beam.Shooter);
                if (Mathf.Abs(Mathf.DeltaAngle(angle, beam.Angle)) < AngleThreshold) continue;
                beam.Angle = angle;
                NetworkSession.Instance.Broadcast(new BeamPacket { Event = BeamPacket.BeamEvent.Angle, EnemyId = beam.EnemyId, ShooterIndex = beam.Index, Angle = angle }, reliable: false);
            }
        }

        // ---- Client ----

        public void HandleBeam(BeamPacket packet)
        {
            Beam beam = _clientBeams.Find(b => b.EnemyId == packet.EnemyId && b.Index == packet.ShooterIndex);
            switch (packet.Event)
            {
                case BeamPacket.BeamEvent.Start:
                    if (beam == null)
                    {
                        Component shooter = FindShooter(packet.EnemyId, packet.ShooterIndex);
                        if (shooter == null)
                        {
                            Debug.LogWarningThrottled($"Beam.NoShooter:{packet.EnemyId}", $"[Beam] No beam shooter {packet.ShooterIndex} on puppet {packet.EnemyId}.");
                            return;
                        }
                        beam = new Beam { Shooter = shooter, EnemyId = packet.EnemyId, Index = packet.ShooterIndex };
                        _clientBeams.Add(beam);
                    }
                    beam.Angle = packet.Angle;
                    beam.AngleTime = Time.realtimeSinceStartup;
                    beam.TurnRate = 0f;
                    if (!IsFiring(beam.Shooter))
                    {
                        if (beam.Shooter is AIBeamShooter s) s.StartFiringLaser(packet.Angle);
                        else if (beam.Shooter is BeholsterController eye) eye.StartFiringLaser(packet.Angle);
                    }
                    break;
                case BeamPacket.BeamEvent.Angle:
                    if (beam != null)
                    {
                        float now = Time.realtimeSinceStartup;
                        float elapsed = now - beam.AngleTime;
                        beam.TurnRate = elapsed > 0.005f
                            ? Mathf.Clamp(Mathf.DeltaAngle(beam.Angle, packet.Angle) / elapsed, -MaxTurnRate, MaxTurnRate)
                            : 0f;
                        beam.Angle = packet.Angle;
                        beam.AngleTime = now;
                    }
                    break;
                case BeamPacket.BeamEvent.Stop:
                    if (beam == null) return;
                    _clientBeams.Remove(beam);
                    Stop(beam.Shooter);
                    break;
            }
        }

        private static void Stop(Component shooter)
        {
            if (shooter is AIBeamShooter s) s.StopFiringLaser();
            else if (shooter is BeholsterController eye) eye.StopFiringLaser();
        }

        private static Component FindShooter(int enemyId, int index)
        {
            GameObject remote = NetworkEntityManager.Instance.GetRemote(enemyId);
            if (remote == null) return null;
            if (index == BeamPacket.BeholsterEye) return remote.GetComponentInChildren<BeholsterController>(true);
            AIBeamShooter[] shooters = remote.GetComponentsInChildren<AIBeamShooter>(true);
            return index >= 0 && index < shooters.Length ? shooters[index] : null;
        }

        private void Update()
        {
            if (_clientBeams.Count == 0) return;
            float t = Mathf.Clamp01(Time.deltaTime * FollowRate);
            float pingMs = NetworkSession.Instance.GetPingMs();
            float halfPing = pingMs > 0f ? pingMs / 2000f : 0f;
            for (int i = _clientBeams.Count - 1; i >= 0; i--)
            {
                Beam beam = _clientBeams[i];
                if (beam.Shooter == null)
                {
                    _clientBeams.RemoveAt(i);
                    continue;
                }
                float ahead = Mathf.Min(Time.realtimeSinceStartup - beam.AngleTime + halfPing, MaxPredictSeconds);
                float angle = Mathf.LerpAngle(AngleOf(beam.Shooter), beam.Angle + beam.TurnRate * ahead, t);
                if (beam.Shooter is AIBeamShooter s) s.LaserAngle = angle;
                else if (beam.Shooter is BeholsterController eye) eye.LaserAngle = angle;
            }
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            foreach (Beam beam in _clientBeams)
            {
                if (beam.Shooter != null) Stop(beam.Shooter);
            }
            _clientBeams.Clear();
            _hostBeams.Clear();
        }
    }
}
