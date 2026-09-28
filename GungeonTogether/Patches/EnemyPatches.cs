using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Enemy targeting on the host. See EnemyReplicator.OnTargetSearch and RemotePlayerTarget.

    /// <summary>Observes the game's own target search, then may swap in a remote player's stand-in.</summary>
    [HarmonyPatch(typeof(TargetPlayerBehavior), nameof(TargetPlayerBehavior.Update))]
    internal static class TargetPlayerBehavior_Update_Patch
    {
        private static void Postfix(BehaviorSpeculator ___m_behaviorSpeculator) => EnemyReplicator.OnTargetSearch(___m_behaviorSpeculator);
    }
}
