using System;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Enemy goop replicated to clients. See GoopReplicator.

    // ---- Sources: an enemy laying goop (host) ----

    /// <summary>
    /// Enemy trails. Skipping prefix on a client's puppets only: their goop comes from the host, and
    /// their own would double it.
    /// </summary>
    [HarmonyPatch(typeof(GoopDoer), "GoopItUp")]
    internal static class GoopDoer_GoopItUp_Patch
    {
        private static bool Prefix(GoopDoer __instance, out bool __state) => GoopReplicator.OnGoopDoer(__instance, out __state);

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.ExitEnemyGoop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SpewGoopBehavior), "AnimationEventTriggered")]
    internal static class SpewGoopBehavior_AnimationEventTriggered_Patch
    {
        private static void Prefix(out bool __state) => __state = GoopReplicator.EnterEnemyGoop();

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.ExitEnemyGoop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SpawnGoopBehavior), nameof(SpawnGoopBehavior.Update))]
    internal static class SpawnGoopBehavior_Update_Patch
    {
        private static void Prefix(out bool __state) => __state = GoopReplicator.EnterEnemyGoop();

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.ExitEnemyGoop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DemonWallSpewBehavior), "AnimationEventTriggered")]
    internal static class DemonWallSpewBehavior_AnimationEventTriggered_Patch
    {
        private static void Prefix(out bool __state) => __state = GoopReplicator.EnterEnemyGoop();

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.ExitEnemyGoop(__state);
            return __exception;
        }
    }

    // ---- Shapes: what the goop manager was asked to lay ----

    [HarmonyPatch(typeof(DeadlyDeadlyGoopManager), nameof(DeadlyDeadlyGoopManager.AddGoopCircle),
        new[] { typeof(Vector2), typeof(float), typeof(int), typeof(bool), typeof(int) })]
    internal static class DeadlyDeadlyGoopManager_AddGoopCircle_Patch
    {
        private static void Prefix(DeadlyDeadlyGoopManager __instance, Vector2 center, float radius, bool suppressSplashes, out bool __state) =>
            __state = GoopReplicator.BeginAdd(__instance, new GoopPacket { Shape = GoopPacket.GoopShape.Circle, A = center, Radius = radius, SuppressSplashes = suppressSplashes });

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.EndAdd(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DeadlyDeadlyGoopManager), nameof(DeadlyDeadlyGoopManager.TimedAddGoopCircle))]
    internal static class DeadlyDeadlyGoopManager_TimedAddGoopCircle_Patch
    {
        private static void Prefix(DeadlyDeadlyGoopManager __instance, Vector2 center, float radius, float duration, bool suppressSplashes, out bool __state) =>
            __state = GoopReplicator.BeginAdd(__instance, new GoopPacket { Shape = GoopPacket.GoopShape.TimedCircle, A = center, Radius = radius, Duration = duration, SuppressSplashes = suppressSplashes });

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.EndAdd(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DeadlyDeadlyGoopManager), nameof(DeadlyDeadlyGoopManager.AddGoopLine))]
    internal static class DeadlyDeadlyGoopManager_AddGoopLine_Patch
    {
        private static void Prefix(DeadlyDeadlyGoopManager __instance, Vector2 p1, Vector2 p2, float radius, out bool __state) =>
            __state = GoopReplicator.BeginAdd(__instance, new GoopPacket { Shape = GoopPacket.GoopShape.Line, A = p1, B = p2, Radius = radius });

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.EndAdd(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DeadlyDeadlyGoopManager), nameof(DeadlyDeadlyGoopManager.TimedAddGoopLine))]
    internal static class DeadlyDeadlyGoopManager_TimedAddGoopLine_Patch
    {
        private static void Prefix(DeadlyDeadlyGoopManager __instance, Vector2 p1, Vector2 p2, float radius, float duration, out bool __state) =>
            __state = GoopReplicator.BeginAdd(__instance, new GoopPacket { Shape = GoopPacket.GoopShape.TimedLine, A = p1, B = p2, Radius = radius, Duration = duration });

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.EndAdd(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DeadlyDeadlyGoopManager), nameof(DeadlyDeadlyGoopManager.TimedAddGoopArc))]
    internal static class DeadlyDeadlyGoopManager_TimedAddGoopArc_Patch
    {
        private static void Prefix(DeadlyDeadlyGoopManager __instance, Vector2 origin, float radius, float arcDegrees, Vector2 direction, float duration, AnimationCurve goopCurve, out bool __state) =>
            __state = GoopReplicator.BeginAdd(__instance, new GoopPacket { Shape = GoopPacket.GoopShape.TimedArc, A = origin, B = direction, Radius = radius, Arc = arcDegrees, Duration = duration, Curve = goopCurve != null ? goopCurve.keys : null });

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GoopReplicator.EndAdd(__state);
            return __exception;
        }
    }
}
