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
    /// Enemy goop (poison/fire/water puddles and trails) on the client. It was host-only: spewing
    /// and puddle attacks (SpewGoopBehavior, SpawnGoopBehavior, DemonWallSpewBehavior) run in the
    /// puppet's switched-off AI, and trails (GoopDoer) only drip while the enemy has physics
    /// velocity, which a puppet moved by position never has.
    ///
    /// Host: goop added inside an enemy goop source (those behaviours, or a GoopDoer on a synced
    /// enemy) is reported shape by shape. Client: adds the same goop, and a puppet's own GoopDoer
    /// is skipped so nothing is laid twice. Goop from bullets isn't touched: the client's copies of
    /// enemy bullets lay their own. Harmony wiring: GungeonTogether.Patches.GoopPatches.
    /// </summary>
    public class GoopReplicator : MonoSingleton<GoopReplicator>
    {
        private static int _enemyGoopDepth; // inside an enemy goop source (host)
        private static bool _reporting;     // inside a reported add: its nested adds are part of it

        // ---- Host: sources ----

        /// <summary>Prefix on an enemy goop behaviour's goop step.</summary>
        public static bool EnterEnemyGoop()
        {
            if (!NetworkSession.Instance.IsHost) return false;
            _enemyGoopDepth++;
            return true;
        }

        public static void ExitEnemyGoop(bool entered)
        {
            if (entered && _enemyGoopDepth > 0) _enemyGoopDepth--;
        }

        /// <summary>
        /// GoopDoer.GoopItUp prefix. Host: a synced enemy's gooper is a source. Client: a puppet's
        /// gooper is skipped (entered = false, returns false) - the host's goop arrives instead.
        /// </summary>
        public static bool OnGoopDoer(GoopDoer doer, out bool entered)
        {
            entered = false;
            AIActor enemy = doer != null ? doer.aiActor : null;
            if (enemy == null) return true;
            if (NetworkSession.Instance.IsClient) return enemy.GetComponent<NetworkPuppet>() == null;
            if (NetworkSession.Instance.IsHost && EnemyReplicator.Instance.TryGetSyncedId(enemy, out _)) entered = EnterEnemyGoop();
            return true;
        }

        // ---- Host: shapes (DeadlyDeadlyGoopManager prefixes) ----

        /// <summary>Returns whether this call is the one reported (then its finalizer must call EndAdd).</summary>
        public static bool BeginAdd(DeadlyDeadlyGoopManager manager, GoopPacket packet)
        {
            if (_enemyGoopDepth == 0 || _reporting || manager == null || manager.goopDefinition == null) return false;
            _reporting = true;
            packet.GoopName = manager.goopDefinition.name;
            NetworkSession.Instance.Broadcast(packet, reliable: false);
            return true;
        }

        public static void EndAdd(bool reported)
        {
            if (reported) _reporting = false;
        }

        // ---- Client ----

        private readonly Dictionary<string, GoopDefinition> _goops = new Dictionary<string, GoopDefinition>();
        private float _nextRescan;

        public void HandleGoop(GoopPacket packet)
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || gm.IsLoadingLevel || gm.Dungeon == null || string.IsNullOrEmpty(packet.GoopName)) return;
            GoopDefinition goop = FindGoop(packet.GoopName);
            if (goop == null)
            {
                Debug.LogWarningThrottled($"Goop.Unknown:{packet.GoopName}", $"[Goop] Unknown goop '{packet.GoopName}'; not laid.");
                return;
            }

            DeadlyDeadlyGoopManager manager = DeadlyDeadlyGoopManager.GetGoopManagerForGoopType(goop);
            if (manager == null) return;
            switch (packet.Shape)
            {
                case GoopPacket.GoopShape.Circle: manager.AddGoopCircle(packet.A, packet.Radius, -1, packet.SuppressSplashes); break;
                case GoopPacket.GoopShape.TimedCircle: manager.TimedAddGoopCircle(packet.A, packet.Radius, packet.Duration, packet.SuppressSplashes); break;
                case GoopPacket.GoopShape.Line: manager.AddGoopLine(packet.A, packet.B, packet.Radius); break;
                case GoopPacket.GoopShape.TimedLine: manager.TimedAddGoopLine(packet.A, packet.B, packet.Radius, packet.Duration); break;
                case GoopPacket.GoopShape.TimedArc:
                    manager.TimedAddGoopArc(packet.A, packet.Radius, packet.Arc, packet.B, packet.Duration, packet.Curve != null ? new AnimationCurve(packet.Curve) : null);
                    break;
            }
        }

        /// <summary>
        /// By asset name. The definitions are loaded with the enemies that use them, and the client
        /// has spawned the same enemies (puppets), so the asset is in memory here too.
        /// </summary>
        private GoopDefinition FindGoop(string name)
        {
            if (_goops.TryGetValue(name, out GoopDefinition goop) && goop != null) return goop;
            if (Time.realtimeSinceStartup < _nextRescan) return null;
            _nextRescan = Time.realtimeSinceStartup + 1f;
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(GoopDefinition)))
            {
                GoopDefinition def = o as GoopDefinition;
                if (def != null && !string.IsNullOrEmpty(def.name)) _goops[def.name] = def;
            }
            return _goops.TryGetValue(name, out goop) ? goop : null;
        }

        /// <summary>Called from NetworkSession.Shutdown.</summary>
        public void ResetSessionState()
        {
            _enemyGoopDepth = 0;
            _reporting = false;
        }
    }
}
