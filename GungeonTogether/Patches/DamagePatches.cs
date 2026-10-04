using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Client damage forwarding (step 4b). See DamageReplicator.

    /// <summary>
    /// Skips the original only for hits on puppets on a client (they're forwarded to the host
    /// instead); every other ApplyDamage - players, the host's own enemies - runs untouched.
    /// </summary>
    [HarmonyPatch(typeof(HealthHaver), nameof(HealthHaver.ApplyDamage))]
    internal static class HealthHaver_ApplyDamage_Patch
    {
        private static bool Prefix(HealthHaver __instance, float damage, Vector2 direction,
            CoreDamageTypes damageTypes, DamageCategory damageCategory) =>
            DamageReplicator.OnApplyDamage(__instance, damage, direction, damageTypes, damageCategory);
    }

    /// <summary>
    /// Lets a client land the killing blow on a boss while the host is dead (see
    /// PlayerLifeReplicator.AllowBossDamage). Only turns a refusal into a yes.
    /// </summary>
    [HarmonyPatch(typeof(HealthHaver), "BossHealthSanityCheck")]
    internal static class HealthHaver_BossHealthSanityCheck_Patch
    {
        private static void Postfix(ref bool __result) => __result = PlayerLifeReplicator.AllowBossDamage(__result);
    }
}
