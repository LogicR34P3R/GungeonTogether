using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Feed every pickup LootEngine spawns into LootReplicator (step 3a loot sync). These only
    // observe - the original spawn always runs, so game code that uses the returned object keeps
    // working on both sides. Applied by Harmony.PatchAll in GungeonTogetherMod.Awake.

    /// <summary>Also covers SpawnHealth and DelayedSpawnItem, which call SpawnItem.</summary>
    [HarmonyPatch(typeof(LootEngine), nameof(LootEngine.SpawnItem))]
    internal static class LootEngine_SpawnItem_Patch
    {
        private static void Postfix(DebrisObject __result) => LootReplicator.OnLocalSpawn(__result);
    }

    /// <summary>Chest contents and similar single-item spews.</summary>
    [HarmonyPatch(typeof(LootEngine), nameof(LootEngine.SpewLoot), new[] { typeof(GameObject), typeof(Vector3) })]
    internal static class LootEngine_SpewLootSingle_Patch
    {
        private static void Postfix(DebrisObject __result) => LootReplicator.OnLocalSpawn(__result);
    }

    [HarmonyPatch(typeof(LootEngine), nameof(LootEngine.SpewLoot), new[] { typeof(List<GameObject>), typeof(Vector3) })]
    internal static class LootEngine_SpewLootList_Patch
    {
        private static void Postfix(List<DebrisObject> __result)
        {
            if (__result == null) return;
            foreach (DebrisObject debris in __result) LootReplicator.OnLocalSpawn(debris);
        }
    }

    /// <summary>
    /// SpawnCurrency returns nothing - its coins come out of SpawnManager.SpawnDebris, so capture
    /// those for the duration of the call. A finalizer (not a postfix) so capture always ends,
    /// even if the original throws.
    /// </summary>
    [HarmonyPatch(typeof(LootEngine), nameof(LootEngine.SpawnCurrency),
        new[] { typeof(Vector2), typeof(int), typeof(bool), typeof(Vector2?), typeof(float?), typeof(float), typeof(float) })]
    internal static class LootEngine_SpawnCurrency_Patch
    {
        private static void Prefix() => LootReplicator.BeginCapture();

        private static Exception Finalizer(Exception __exception)
        {
            LootReplicator.EndCapture();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(LootEngine), nameof(LootEngine.SpawnCurrencyManual))]
    internal static class LootEngine_SpawnCurrencyManual_Patch
    {
        private static void Prefix() => LootReplicator.BeginCapture();

        private static Exception Finalizer(Exception __exception)
        {
            LootReplicator.EndCapture();
            return __exception;
        }
    }

    /// <summary>Hot path (all debris) - OnDebrisSpawned is a no-op unless a currency spawn is being captured.</summary>
    [HarmonyPatch(typeof(SpawnManager), nameof(SpawnManager.SpawnDebris), new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion) })]
    internal static class SpawnManager_SpawnDebris_Patch
    {
        private static void Postfix(GameObject __result) => LootReplicator.OnDebrisSpawned(__result);
    }
}
