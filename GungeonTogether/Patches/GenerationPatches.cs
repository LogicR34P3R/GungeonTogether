using HarmonyLib;
using Dungeonator;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Floor generation from the host's save answers. See GenerationReplicator.

    // ---- The three generation steps (windows): a prefix opens one, the postfix closes it ----

    [HarmonyPatch(typeof(GameLevelDefinition), nameof(GameLevelDefinition.LovinglySelectDungeonFlow))]
    internal static class GameLevelDefinition_LovinglySelectDungeonFlow_Patch
    {
        private static void Prefix() => GenerationReplicator.BeginWindow(GenerationReplicator.WindowKind.FlowSelection);
        private static void Postfix() => GenerationReplicator.EndWindow(GenerationReplicator.WindowKind.FlowSelection);
    }

    [HarmonyPatch(typeof(MetaInjectionData), nameof(MetaInjectionData.PreprocessRun))]
    internal static class MetaInjectionData_PreprocessRun_Patch
    {
        private static void Prefix() => GenerationReplicator.BeginWindow(GenerationReplicator.WindowKind.RunBlueprint);
        private static void Postfix() => GenerationReplicator.EndWindow(GenerationReplicator.WindowKind.RunBlueprint);
    }

    /// <summary>
    /// The level load runs this frame by frame (and GenerateDungeonLayout just runs it to the end),
    /// so the window is kept open by wrapping the returned steps rather than by prefix/postfix.
    /// </summary>
    [HarmonyPatch(typeof(LoopDungeonGenerator), nameof(LoopDungeonGenerator.GenerateDungeonLayoutDeferred))]
    internal static class LoopDungeonGenerator_GenerateDungeonLayoutDeferred_Patch
    {
        private static void Postfix(ref System.Collections.IEnumerable __result) => __result = GenerationReplicator.WrapLayout(__result);
    }

    // ---- Save reads: skipping prefixes that return the host's answer inside a window on a client ----
    // (both return a bool, set via __result; the postfix records the answer on the host)

    /// <summary>Skipping prefix: see GenerationReplicator. Outside a replayed window the original runs.</summary>
    [HarmonyPatch(typeof(DungeonPrerequisite), nameof(DungeonPrerequisite.CheckConditionsFulfilled), new System.Type[0])]
    internal static class DungeonPrerequisite_CheckConditionsFulfilled_Patch
    {
        private static bool Prefix(ref bool __result) => !GenerationReplicator.TryReplay(out __result);
        private static void Postfix(bool __result) => GenerationReplicator.Record(__result);
    }

    /// <summary>Skipping prefix: see GenerationReplicator. Outside a replayed window the original runs.</summary>
    [HarmonyPatch(typeof(GameStatsManager), nameof(GameStatsManager.GetFlag), new[] { typeof(GungeonFlags) })]
    internal static class GameStatsManager_GetFlag_Patch
    {
        private static bool Prefix(ref bool __result) => !GenerationReplicator.TryReplay(out __result);
        private static void Postfix(bool __result) => GenerationReplicator.Record(__result);
    }
}
