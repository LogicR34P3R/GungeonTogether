using HarmonyLib;
using GungeonTogether.Networking.Players;
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

    // Line of sight to a stand-in: the raycast skips bodies that don't collide, so every attack that
    // needs line of sight never fired at it. It's made visible to the ray for just that check.
    // A finalizer rather than a postfix, so it's restored even if the check throws.

    [HarmonyPatch(typeof(AIActor), nameof(AIActor.HasLineOfSightToRigidbody))]
    internal static class AIActor_HasLineOfSightToRigidbody_Patch
    {
        private static void Prefix(SpeculativeRigidbody targetRigidbody, out SpeculativeRigidbody __state) => __state = RemotePlayerTarget.BeginSightCheck(targetRigidbody);
        private static void Finalizer(SpeculativeRigidbody __state) => RemotePlayerTarget.EndSightCheck(__state);
    }

    [HarmonyPatch(typeof(AIActor), nameof(AIActor.HasLineOfSightToTargetFromPosition))]
    internal static class AIActor_HasLineOfSightToTargetFromPosition_Patch
    {
        private static void Prefix(AIActor __instance, out SpeculativeRigidbody __state) => __state = RemotePlayerTarget.BeginSightCheck(__instance.TargetRigidbody);
        private static void Finalizer(SpeculativeRigidbody __state) => RemotePlayerTarget.EndSightCheck(__state);
    }
}
