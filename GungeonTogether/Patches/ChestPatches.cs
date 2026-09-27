using HarmonyLib;
using UnityEngine;
using Dungeonator;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Chest sync (step 3c). See ChestReplicator.

    /// <summary>
    /// The one prefix that skips the original: on a client, opening a chest becomes a request to the
    /// host (the host's copy opens and its contents are mirrored); the local copy never opens itself.
    /// </summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.Interact))]
    internal static class Chest_Interact_Patch
    {
        private static bool Prefix(Chest __instance) => ChestReplicator.OnInteract(__instance);
    }

    /// <summary>Protected, so patched by name.</summary>
    [HarmonyPatch(typeof(Chest), "Open")]
    internal static class Chest_Open_Patch
    {
        private static void Postfix(Chest __instance) => ChestReplicator.OnOpened(__instance);
    }

    /// <summary>Private (MajorBreakable.OnBreak handler), so patched by name.</summary>
    [HarmonyPatch(typeof(Chest), "OnBroken")]
    internal static class Chest_OnBroken_Patch
    {
        private static void Postfix(Chest __instance) => ChestReplicator.OnBroken(__instance);
    }

    /// <summary>The overload every Chest.Spawn call ends up in.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.Spawn), new[] { typeof(Chest), typeof(Vector3), typeof(RoomHandler), typeof(bool) })]
    internal static class Chest_Spawn_Patch
    {
        private static void Postfix(Chest __result, Chest chestPrefab) => ChestReplicator.OnSpawned(__result, chestPrefab);
    }
}
