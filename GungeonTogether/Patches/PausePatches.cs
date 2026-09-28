using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // The pause menu in a session. See WorldStateReplicator.OnPaused.

    /// <summary>Observes the pause menu opening, then undoes its time freeze while online.</summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Pause))]
    internal static class GameManager_Pause_Patch
    {
        private static void Postfix(GameManager __instance) => WorldStateReplicator.OnPaused(__instance);
    }
}
