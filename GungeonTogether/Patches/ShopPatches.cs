using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Shop stock sync (step 3d). See ShopReplicator.

    /// <summary>
    /// Interact returns early for plenty of non-purchases (full health, no money, failed steal), so
    /// only report when it actually flipped the slot to sold. Items that persist on purchase never
    /// flip, and don't need syncing.
    /// </summary>
    [HarmonyPatch(typeof(ShopItemController), nameof(ShopItemController.Interact))]
    internal static class ShopItemController_Interact_Patch
    {
        private static void Prefix(ShopItemController __instance, out bool __state) => __state = __instance.Acquired;

        private static void Postfix(ShopItemController __instance, bool __state)
        {
            if (!__state && __instance.Acquired) ShopReplicator.OnSold(__instance);
        }
    }

    /// <summary>Theft by item effects bypasses Interact.</summary>
    [HarmonyPatch(typeof(ShopItemController), nameof(ShopItemController.ForceSteal))]
    internal static class ShopItemController_ForceSteal_Patch
    {
        private static void Postfix(ShopItemController __instance) => ShopReplicator.OnSold(__instance);
    }
}
