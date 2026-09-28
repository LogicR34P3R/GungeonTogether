using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Co-op rules for players across the network. See PlayerLifeReplicator and PlayerReplicator.

    /// <summary>
    /// Skipping prefix: the game only turns a death into a co-op ghost for a local second player.
    /// With a partner alive over the network, PlayerLifeReplicator makes the player a ghost and the
    /// original (the game over) must not run. Die returns nothing, so skipping it is safe. Protected,
    /// so patched by name.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "Die", new[] { typeof(Vector2) })]
    internal static class PlayerController_Die_Patch
    {
        private static bool Prefix(PlayerController __instance) => PlayerLifeReplicator.OnLocalDying(__instance);
    }

    /// <summary>
    /// Skipping prefix: like vanilla co-op, a door opens only once every living player is at it.
    /// The game checks that for local players only; skipping leaves the door shut, and the next touch
    /// checks again. Returns nothing. Private, so patched by name.
    /// </summary>
    [HarmonyPatch(typeof(DungeonDoorController), "CheckForPlayerCollision")]
    internal static class DungeonDoorController_CheckForPlayerCollision_Patch
    {
        private static bool Prefix(DungeonDoorController __instance, SpeculativeRigidbody otherRigidbody) =>
            PlayerReplicator.CanOpenDoor(__instance, otherRigidbody);
    }
}
