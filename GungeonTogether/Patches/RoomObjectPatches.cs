using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Room props. See RoomObjectReplicator. The flip/break prefixes only record the state before (nothing is
    // skipped), so the postfix reports only a flip/break that actually happened on this call.

    /// <summary>The overload a player's flip goes through.</summary>
    [HarmonyPatch(typeof(FlippableCover), nameof(FlippableCover.Flip), new[] { typeof(SpeculativeRigidbody) })]
    internal static class FlippableCover_Flip_Patch
    {
        private static void Prefix(FlippableCover __instance, out bool __state) => __state = __instance.IsFlipped;
        private static void Postfix(FlippableCover __instance, bool __state) => RoomObjectReplicator.OnTableFlipped(__instance, __state);
    }

    [HarmonyPatch(typeof(MinorBreakable), nameof(MinorBreakable.Break), new[] { typeof(Vector2) })]
    internal static class MinorBreakable_BreakDirection_Patch
    {
        private static void Prefix(MinorBreakable __instance, out bool __state) => __state = __instance.IsBroken;
        private static void Postfix(MinorBreakable __instance, bool __state, Vector2 direction) => RoomObjectReplicator.OnMinorBroken(__instance, __state, direction);
    }

    [HarmonyPatch(typeof(MinorBreakable), nameof(MinorBreakable.Break), new System.Type[0])]
    internal static class MinorBreakable_Break_Patch
    {
        private static void Prefix(MinorBreakable __instance, out bool __state) => __state = __instance.IsBroken;
        private static void Postfix(MinorBreakable __instance, bool __state) => RoomObjectReplicator.OnMinorBroken(__instance, __state, Vector2.zero);
    }

    /// <summary>
    /// Skipping prefix (returns nothing): a client's minecart factories spawn only when the host's
    /// did (RoomObjectReplicator.SpawnCartForHost). The postfix tracks the new cart. Protected, so by name.
    /// </summary>
    /// <summary>
    /// Skipping prefix, for carts the other side is driving: the wheel animation is picked from
    /// physics velocity, which such a cart doesn't have, so the synced animation froze every frame.
    /// </summary>
    [HarmonyPatch(typeof(MineCartController), "UpdateAnimations")]
    internal static class MineCartController_UpdateAnimations_Patch
    {
        private static bool Prefix(MineCartController __instance) => RoomObjectReplicator.AllowCartAnimation(__instance);
    }

    [HarmonyPatch(typeof(MineCartFactory), "DoSpawnCart")]
    internal static class MineCartFactory_DoSpawnCart_Patch
    {
        private static bool Prefix() => RoomObjectReplicator.AllowCartSpawn();
        private static void Postfix(MineCartFactory __instance) => RoomObjectReplicator.OnCartSpawned(__instance);
    }

    [HarmonyPatch(typeof(MajorBreakable), nameof(MajorBreakable.Break), new[] { typeof(Vector2) })]
    internal static class MajorBreakable_Break_Patch
    {
        private static void Prefix(MajorBreakable __instance, out bool __state) => __state = __instance.IsDestroyed;
        private static void Postfix(MajorBreakable __instance, bool __state, Vector2 sourceDirection) => RoomObjectReplicator.OnMajorBroken(__instance, __state, sourceDirection);
    }
}
