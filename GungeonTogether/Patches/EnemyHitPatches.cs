using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Direct-damage enemy attacks on a client's stand-in (host). See EnemyHitReplicator.

    [HarmonyPatch(typeof(GatlingGullLeapBehavior), "HandleAnimationEvent")]
    internal static class GatlingGullLeapBehavior_HandleAnimationEvent_Patch
    {
        private static void Prefix(GatlingGullLeapBehavior __instance, AIActor ___m_aiActor, tk2dSpriteAnimationClip clip, int frameNo) =>
            EnemyHitReplicator.OnGullLeapEvent(__instance, ___m_aiActor, clip, frameNo);
    }

    [HarmonyPatch(typeof(GatlingGullMelee), "HandleAnimationEvent")]
    internal static class GatlingGullMelee_HandleAnimationEvent_Patch
    {
        private static void Prefix(GatlingGullMelee __instance, AIActor ___m_aiActor, tk2dSpriteAnimationClip clip, int frameNo) =>
            EnemyHitReplicator.OnGullMeleeEvent(__instance, ___m_aiActor, clip, frameNo);
    }

    /// <summary>
    /// Skipping prefix: on a grab of a client's stand-in it runs the success path itself, because
    /// the original only knows local players and would turn the grab into a miss.
    /// </summary>
    [HarmonyPatch(typeof(ConsumeTargetBehavior), "AnimationEventTriggered")]
    internal static class ConsumeTargetBehavior_AnimationEventTriggered_Patch
    {
        private static bool Prefix(ConsumeTargetBehavior __instance, AIActor ___m_aiActor, BehaviorSpeculator ___m_behaviorSpeculator,
            ref ConsumeTargetBehavior.State ___m_state, tk2dSpriteAnimationClip clip, int frameNo) =>
            EnemyHitReplicator.OnConsumeEvent(__instance, ___m_aiActor, ___m_behaviorSpeculator, ref ___m_state, clip, frameNo);
    }

    [HarmonyPatch(typeof(ConsumeTargetBehavior), "UnconsumePlayer")]
    internal static class ConsumeTargetBehavior_UnconsumePlayer_Patch
    {
        private static void Prefix(ConsumeTargetBehavior __instance, AIActor ___m_aiActor, bool punishPlayer) =>
            EnemyHitReplicator.OnUnconsume(__instance, ___m_aiActor, punishPlayer);
    }

    [HarmonyPatch(typeof(ConsumeTargetBehavior), nameof(ConsumeTargetBehavior.EndContinuousUpdate))]
    internal static class ConsumeTargetBehavior_EndContinuousUpdate_Patch
    {
        private static void Prefix(ConsumeTargetBehavior __instance, AIActor ___m_aiActor) => EnemyHitReplicator.OnConsumeEnded(__instance, ___m_aiActor);
    }

    [HarmonyPatch(typeof(ConsumeTargetBehavior), nameof(ConsumeTargetBehavior.Destroy))]
    internal static class ConsumeTargetBehavior_Destroy_Patch
    {
        private static void Prefix(ConsumeTargetBehavior __instance, AIActor ___m_aiActor) => EnemyHitReplicator.OnConsumeEnded(__instance, ___m_aiActor);
    }
}
