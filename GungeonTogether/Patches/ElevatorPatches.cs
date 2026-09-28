using HarmonyLib;
using GungeonTogether.Networking.Replication;

namespace GungeonTogether.Patches
{
    // Leaving a floor together. See WorldStateReplicator.OnElevatorEntered.

    /// <summary>
    /// Skipping prefix: the exit elevator leaves once every living player is in it, local or remote,
    /// and only the host takes it (the client follows). Returns nothing. Private, so patched by name.
    /// </summary>
    [HarmonyPatch(typeof(ElevatorDepartureController), "OnElevatorTriggerEnter")]
    internal static class ElevatorDepartureController_OnElevatorTriggerEnter_Patch
    {
        private static bool Prefix(ElevatorDepartureController __instance, SpeculativeRigidbody otherSpecRigidbody,
            SpeculativeRigidbody sourceSpecRigidbody, Tribool ___m_isArrived) =>
            WorldStateReplicator.OnElevatorEntered(__instance, sourceSpecRigidbody, otherSpecRigidbody, ___m_isArrived);
    }
}
