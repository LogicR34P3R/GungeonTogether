using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Sky rockets (Gatling Gull) replicated to clients. See SkyRocketReplicator.

    [HarmonyPatch(typeof(SkyRocket), nameof(SkyRocket.Start))]
    internal static class SkyRocket_Start_Patch
    {
        private static void Postfix(SkyRocket __instance) => SkyRocketReplicator.OnStart(__instance);
    }

    [HarmonyPatch(typeof(SkyRocket), nameof(SkyRocket.DieInAir))]
    internal static class SkyRocket_DieInAir_Patch
    {
        private static void Postfix(SkyRocket __instance) => SkyRocketReplicator.OnDieInAir(__instance);
    }

    /// <summary>
    /// Skipping prefix: a client's copy waits at the top of its climb until the host says where it
    /// lands, instead of picking a spot from a target it doesn't have.
    /// </summary>
    [HarmonyPatch(typeof(SkyRocket), nameof(SkyRocket.Update))]
    internal static class SkyRocket_Update_Patch
    {
        private static bool Prefix(SkyRocket __instance) => SkyRocketReplicator.ShouldUpdate(__instance);
    }
}
