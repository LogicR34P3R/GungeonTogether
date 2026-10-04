using HarmonyLib;
using Dungeonator;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Reward pedestals (boss items) replicated to clients. See PedestalReplicator.

    /// <summary>The overload the boss reward (and the 2-argument overload) goes through.</summary>
    [HarmonyPatch(typeof(RewardPedestal), nameof(RewardPedestal.Spawn), new[] { typeof(RewardPedestal), typeof(IntVector2), typeof(RoomHandler) })]
    internal static class RewardPedestal_Spawn_Patch
    {
        private static void Postfix(RewardPedestal __result, RewardPedestal pedestalPrefab) => PedestalReplicator.OnSpawned(__result, pedestalPrefab);
    }

    /// <summary>
    /// Skipping prefix, only for a client's copy of a pedestal that is a mimic on the host: the
    /// touch goes to the host, which wakes the mimic, instead of handing out the item here.
    /// </summary>
    [HarmonyPatch(typeof(RewardPedestal), nameof(RewardPedestal.Interact))]
    internal static class RewardPedestal_Interact_Patch
    {
        private static bool Prefix(RewardPedestal __instance) => PedestalReplicator.OnInteract(__instance);
    }
}
