using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Enemy laser beams replicated to clients. See BeamReplicator. All observe-only.

    [HarmonyPatch(typeof(AIBeamShooter), nameof(AIBeamShooter.StartFiringLaser))]
    internal static class AIBeamShooter_StartFiringLaser_Patch
    {
        private static void Postfix(AIBeamShooter __instance) => BeamReplicator.OnStart(__instance);
    }

    [HarmonyPatch(typeof(AIBeamShooter), nameof(AIBeamShooter.StopFiringLaser))]
    internal static class AIBeamShooter_StopFiringLaser_Patch
    {
        private static void Prefix(AIBeamShooter __instance) => BeamReplicator.OnStop(__instance);
    }

    [HarmonyPatch(typeof(BeholsterController), nameof(BeholsterController.StartFiringLaser))]
    internal static class BeholsterController_StartFiringLaser_Patch
    {
        private static void Postfix(BeholsterController __instance) => BeamReplicator.OnStart(__instance);
    }

    [HarmonyPatch(typeof(BeholsterController), nameof(BeholsterController.StopFiringLaser))]
    internal static class BeholsterController_StopFiringLaser_Patch
    {
        private static void Prefix(BeholsterController __instance) => BeamReplicator.OnStop(__instance);
    }
}
