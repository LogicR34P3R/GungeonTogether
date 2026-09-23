using System;
using System.Collections.Generic;
using Steamworks;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Transport
{
    /// <summary>
    /// Steam lobby create/join/invite/member-list, talking to Steamworks directly.
    /// Doesn't know about NetworkSession - it just announces "we created a lobby, host from here"
    /// or "we joined a lobby owned by X" via events, so the Session layer can react without
    /// this layer needing to depend on it.
    /// </summary>
    public class SteamLobby
    {
        private static SteamLobby _instance;
        public static SteamLobby Instance => _instance ??= new SteamLobby();

        private const string HostProtocolLobbyKey = "gt_proto";

        public bool IsInitialised { get; private set; }
        public bool IsInLobby { get; private set; }
        public ulong CurrentLobbyId { get; private set; }

        public event Action<ulong> LobbyHostReady;
        public event Action<ulong> LobbyJoinReady; // arg: host/owner steam id
        public event Action OnPlayerListChanged;

        private Callback<LobbyCreated_t> _lobbyCreatedCb;
        private Callback<LobbyEnter_t> _lobbyEnterCb;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdateCb;
        private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCb;
        private Callback<GameRichPresenceJoinRequested_t> _richPresenceJoinRequestedCb;

        private SteamLobby() { }

        public void Initialise()
        {
            if (IsInitialised) return;

            _lobbyCreatedCb = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _gameLobbyJoinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _richPresenceJoinRequestedCb = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);

            IsInitialised = true;
            Debug.Log("[Lobby] Initialised.");
        }

        public void CreateLobby(int maxMembers = 4)
        {
            if (!IsInitialised) return;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers);
            Debug.Log("[Lobby] Creating lobby...");
        }

        public void JoinLobby(ulong lobbyId)
        {
            if (!IsInitialised) return;
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            Debug.Log($"[Lobby] Joining lobby {lobbyId}...");
        }

        public void LeaveLobby()
        {
            if (!IsInitialised || !IsInLobby || CurrentLobbyId == 0) return;

            SteamMatchmaking.LeaveLobby(new CSteamID(CurrentLobbyId));
            IsInLobby = false;
            CurrentLobbyId = 0;
            Debug.Log("[Lobby] Left lobby.");
        }

        public void OpenInviteDialog()
        {
            if (!IsInitialised || !IsInLobby || CurrentLobbyId == 0)
            {
                Debug.LogWarning("[Lobby] Not in a lobby to invite from.");
                return;
            }

            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(CurrentLobbyId));
        }

        public List<ulong> GetLobbyMembers()
        {
            var members = new List<ulong>();
            if (!IsInLobby || CurrentLobbyId == 0) return members;

            var lobbyId = new CSteamID(CurrentLobbyId);
            int count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (int i = 0; i < count; i++)
            {
                members.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID);
            }
            return members;
        }

        private void OnLobbyCreated(LobbyCreated_t data)
        {
            if (data.m_eResult != EResult.k_EResultOK || data.m_ulSteamIDLobby == 0)
            {
                Debug.LogWarning($"[Lobby] CreateLobby failed: {data.m_eResult}");
                return;
            }

            CurrentLobbyId = data.m_ulSteamIDLobby;
            IsInLobby = true;

            var lobbyId = new CSteamID(CurrentLobbyId);
            ulong localId = SteamIdentity.GetLocalSteamId();
            SteamMatchmaking.SetLobbyData(lobbyId, "gt_host", localId.ToString());
            SteamMatchmaking.SetLobbyData(lobbyId, HostProtocolLobbyKey, Session.NetworkSession.ProtocolVersion.ToString());
            SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
            SteamFriends.SetRichPresence("connect", CurrentLobbyId.ToString());

            Debug.Log($"[Lobby] Created lobby {CurrentLobbyId}.");
            OnPlayerListChanged?.Invoke();
            LobbyHostReady?.Invoke(CurrentLobbyId);
        }

        private void OnLobbyEnter(LobbyEnter_t data)
        {
            if (data.m_ulSteamIDLobby == 0) return;

            CurrentLobbyId = data.m_ulSteamIDLobby;
            IsInLobby = true;

            var lobbyId = new CSteamID(CurrentLobbyId);
            ulong ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId).m_SteamID;
            ulong localId = SteamIdentity.GetLocalSteamId();

            Debug.Log($"[Lobby] Entered lobby {CurrentLobbyId}, owner={ownerId}, local={localId}");
            OnPlayerListChanged?.Invoke();

            if (ownerId != 0 && ownerId != localId)
            {
                LobbyJoinReady?.Invoke(ownerId);
            }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t data)
        {
            if (data.m_ulSteamIDLobby != CurrentLobbyId) return;
            OnPlayerListChanged?.Invoke();
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            ulong lobbyId = data.m_steamIDLobby.m_SteamID;
            if (lobbyId != 0)
            {
                JoinLobby(lobbyId);
            }
        }

        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            string connect = data.m_rgchConnect;
            if (string.IsNullOrEmpty(connect)) return;

            if (ulong.TryParse(connect, out ulong lobbyId) && lobbyId != 0)
            {
                JoinLobby(lobbyId);
            }
        }
    }
}
