using HarmonyLib;
using Dungeonator;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Boss sync (step 4c-1). See EnemyReplicator.

    /// <summary>The host's floor boss died; clients (which only fight its puppet) replay the floor clear.</summary>
    [HarmonyPatch(typeof(Dungeon), nameof(Dungeon.FloorCleared))]
    internal static class Dungeon_FloorCleared_Patch
    {
        private static void Postfix() => EnemyReplicator.OnFloorCleared();
    }
}
