using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Brave.BulletScript;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Enemy bullet capture on the host (step 4a). See ProjectileReplicator. All observe-only.

    /// <summary>
    /// BulletScript bullets. The prefix/finalizer mark "inside a script spawn" so the nested
    /// CreateProjectileFromBank call isn't captured a second time as a direct bank shot.
    /// </summary>
    [HarmonyPatch(typeof(AIBulletBank), nameof(AIBulletBank.BulletSpawnedHandler))]
    internal static class AIBulletBank_BulletSpawnedHandler_Patch
    {
        private static void Prefix() => ProjectileReplicator.EnterScriptSpawn();

        private static void Postfix(AIBulletBank __instance, Bullet bullet) => ProjectileReplicator.CaptureScriptBullet(__instance, bullet);

        private static Exception Finalizer(Exception __exception)
        {
            ProjectileReplicator.ExitScriptSpawn();
            return __exception;
        }
    }

    /// <summary>Direct bank shots (ShootBehavior, turrets, ...).</summary>
    [HarmonyPatch(typeof(AIBulletBank), nameof(AIBulletBank.CreateProjectileFromBank))]
    internal static class AIBulletBank_CreateProjectileFromBank_Patch
    {
        private static void Postfix(AIBulletBank __instance, GameObject __result, string bulletName) =>
            ProjectileReplicator.CaptureBankShot(__instance, __result, bulletName);
    }

    // Held-gun volleys: these AIShooter methods call SpawnManager.SpawnProjectile directly, so mark
    // the shooter for their duration and capture what SpawnProjectile returns meanwhile.

    [HarmonyPatch(typeof(AIShooter), "ShootAtTarget", new[] { typeof(ProjectileModule), typeof(string), typeof(Vector3), typeof(float) })]
    internal static class AIShooter_ShootAtTarget_Patch
    {
        private static void Prefix(AIShooter __instance) => ProjectileReplicator.BeginShooter(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ProjectileReplicator.EndShooter();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AIShooter), "ShootInDirection", new[] { typeof(Vector2), typeof(ProjectileModule), typeof(string), typeof(Vector3), typeof(float) })]
    internal static class AIShooter_ShootInDirection_Patch
    {
        private static void Prefix(AIShooter __instance) => ProjectileReplicator.BeginShooter(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ProjectileReplicator.EndShooter();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AIShooter), "ShootVolleyAtTarget", new[] { typeof(Vector3) })]
    internal static class AIShooter_ShootVolleyAtTarget_Patch
    {
        private static void Prefix(AIShooter __instance) => ProjectileReplicator.BeginShooter(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            ProjectileReplicator.EndShooter();
            return __exception;
        }
    }

    // Gunjurer spin attacks (WizardSpinShootBehavior): bullets are released in ContinuousUpdate and,
    // when the attack ends early or the caster dies, in FreeRemainingProjectiles. Compare the held
    // bullets before and after each.

    [HarmonyPatch(typeof(WizardSpinShootBehavior), nameof(WizardSpinShootBehavior.ContinuousUpdate))]
    internal static class WizardSpinShootBehavior_ContinuousUpdate_Patch
    {
        private static void Prefix(List<Tuple<Projectile, float>> ___m_bulletPositions, out List<Projectile> __state) =>
            __state = ProjectileReplicator.HeldSpinBullets(___m_bulletPositions);

        private static void Postfix(WizardSpinShootBehavior __instance, AIActor ___m_aiActor, List<Tuple<Projectile, float>> ___m_bulletPositions, List<Projectile> __state) =>
            ProjectileReplicator.CaptureSpinChanges(__instance, ___m_aiActor, ___m_bulletPositions, __state);
    }

    [HarmonyPatch(typeof(WizardSpinShootBehavior), "FreeRemainingProjectiles")]
    internal static class WizardSpinShootBehavior_FreeRemainingProjectiles_Patch
    {
        private static void Prefix(List<Tuple<Projectile, float>> ___m_bulletPositions, out List<Projectile> __state) =>
            __state = ProjectileReplicator.HeldSpinBullets(___m_bulletPositions);

        private static void Postfix(WizardSpinShootBehavior __instance, AIActor ___m_aiActor, List<Tuple<Projectile, float>> ___m_bulletPositions, List<Projectile> __state) =>
            ProjectileReplicator.CaptureSpinChanges(__instance, ___m_aiActor, ___m_bulletPositions, __state);
    }

    /// <summary>Hot path (every projectile, players' too) - a no-op outside an AIShooter volley.</summary>
    [HarmonyPatch(typeof(SpawnManager), nameof(SpawnManager.SpawnProjectile), new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(bool) })]
    internal static class SpawnManager_SpawnProjectile_Patch
    {
        private static void Postfix(GameObject __result) => ProjectileReplicator.OnProjectileSpawned(__result);
    }
}
