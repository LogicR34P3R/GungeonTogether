using Steamworks;

namespace GungeonTogether.Networking.Transport
{
    /// <summary>
    /// Small direct-call helpers for Steam identity lookups that don't belong to a
    /// specific transport/lobby instance (used by UI and sync code).
    /// </summary>
    public static class SteamIdentity
    {
        public static ulong GetLocalSteamId() => SteamUser.GetSteamID().m_SteamID;

        public static string GetPlayerName(ulong steamId) => SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
    }
}
