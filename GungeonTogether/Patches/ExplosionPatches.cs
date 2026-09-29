using System;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Host explosions replayed on clients. See ExplosionReplicator. All observe-only.

    [HarmonyPatch(typeof(Exploder), nameof(Exploder.Explode))]
    internal static class Exploder_Explode_Patch
    {
        private static void Prefix(Vector3 position, ExplosionData data) => ExplosionReplicator.OnExplode(position, data);
    }

    /// <summary>An enemy bullet exploding: the client's copy of the bullet explodes by itself.</summary>
    [HarmonyPatch(typeof(ExplosiveModifier), nameof(ExplosiveModifier.Explode))]
    internal static class ExplosiveModifier_Explode_Patch
    {
        private static void Prefix(ExplosiveModifier __instance, out bool __state) =>
            __state = ExplosionReplicator.EnterLocalOnly(ExplosionReplicator.IsEnemyProjectile(__instance));

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            ExplosionReplicator.ExitLocalOnly(__state);
            return __exception;
        }
    }

    /// <summary>An explosive pot: the other side's mirrored break explodes by itself.</summary>
    [HarmonyPatch(typeof(MinorBreakable), "OnBreakAnimationComplete")]
    internal static class MinorBreakable_OnBreakAnimationComplete_Patch
    {
        private static void Prefix(out bool __state) => __state = ExplosionReplicator.EnterLocalOnly(true);

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            ExplosionReplicator.ExitLocalOnly(__state);
            return __exception;
        }
    }
}
