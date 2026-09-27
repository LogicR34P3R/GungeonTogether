using System;
using HarmonyLib;
using Brave.BulletScript;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Boss attack script replay (step 4c-2). See ScriptReplicator. All observe-only: none skip the
    // original; the Bullet patches only swap UnityEngine.Random's state around it.

    [HarmonyPatch(typeof(BulletScriptSource), nameof(BulletScriptSource.Initialize))]
    internal static class BulletScriptSource_Initialize_Patch
    {
        private static void Prefix(BulletScriptSource __instance) => ScriptReplicator.BeginSourceInit(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ScriptReplicator.EndSourceInit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BulletScriptSelector), nameof(BulletScriptSelector.CreateInstance))]
    internal static class BulletScriptSelector_CreateInstance_Patch
    {
        private static void Postfix(Bullet __result) => ScriptReplicator.OnRootCreated(__result);
    }

    /// <summary>
    /// Second patch class on this method (4a's capture is the other); Harmony stacks them. Gives the
    /// child its stream, then suspends the parent's stream for the spawn itself.
    /// </summary>
    [HarmonyPatch(typeof(AIBulletBank), nameof(AIBulletBank.BulletSpawnedHandler))]
    internal static class AIBulletBank_BulletSpawnedHandler_Replay_Patch
    {
        private static void Prefix(Bullet bullet)
        {
            ScriptReplicator.OnChildSpawned(bullet);
            ScriptReplicator.SuspendStream();
        }

        private static Exception Finalizer(Exception __exception)
        {
            ScriptReplicator.ResumeStream();
            return __exception;
        }
    }

    // Finalizers rather than postfixes so the Random state is always restored, even if a script throws.

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.Initialize))]
    internal static class Bullet_Initialize_Patch
    {
        private static void Prefix(Bullet __instance) => ScriptReplicator.EnterBullet(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ScriptReplicator.ExitBullet();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.FrameUpdate))]
    internal static class Bullet_FrameUpdate_Patch
    {
        private static void Prefix(Bullet __instance) => ScriptReplicator.EnterBullet(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ScriptReplicator.ExitBullet();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BulletScriptSource), nameof(BulletScriptSource.ForceStop))]
    internal static class BulletScriptSource_ForceStop_Patch
    {
        private static void Prefix(BulletScriptSource __instance) => ScriptReplicator.OnSourceStopped(__instance);
    }
}
