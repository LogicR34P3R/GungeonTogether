using Steamworks;

namespace GungeonTogether.Networking.Transport
{
    /// <summary>
    /// Small direct-call helpers for Steam identity lookups that don't belong to a
    /// specific transport/lobby instance (used by UI and sync code).
    /// </summary>
    public static class SteamIdentity
    {
        /// <summary>
        /// Whether the game has initialised Steamworks yet. BepInEx loads plugins before the game's
        /// SteamManager runs SteamAPI.Init, so this is false for the first moments after launch.
        /// Deliberately not SteamManager.Instance/Initialized: that getter creates a SteamManager if
        /// the game hasn't made one yet.
        /// </summary>
        public static bool IsSteamReady()
        {
            try
            {
                InteropHelp.TestIfAvailableClient();
                return true;
            }
            catch (System.InvalidOperationException)
            {
                return false;
            }
        }

        public static ulong GetLocalSteamId() => SteamUser.GetSteamID().m_SteamID;

        public static string GetPlayerName(ulong steamId) => SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
    }
}
