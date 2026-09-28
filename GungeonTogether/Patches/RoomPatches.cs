using HarmonyLib;
using Dungeonator;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Room doors on clients. See EnemyReplicator.

    /// <summary>
    /// Skipping prefix: on a client, a room the host sealed is only opened by the host. The game
    /// would unseal it the next frame, because the client's puppet enemies don't count towards the
    /// room clear. Returns nothing.
    /// </summary>
    [HarmonyPatch(typeof(RoomHandler), nameof(RoomHandler.UnsealRoom))]
    internal static class RoomHandler_UnsealRoom_Patch
    {
        private static bool Prefix(RoomHandler __instance) => EnemyReplicator.AllowUnseal(__instance);
    }
}
