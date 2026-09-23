using System;
using Steamworks;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Transport
{
    /// <summary>
    /// ISteamTransport backed directly by ETG's bundled Steamworks.NET (Assembly-CSharp-firstpass).
    /// Those types are public, so this talks to them like any normal compiled reference -
    /// no reflection needed anywhere in this class.
    /// </summary>
    public class SteamP2PTransport : ISteamTransport
    {
        private static SteamP2PTransport _instance;
        public static SteamP2PTransport Instance => _instance ??= new SteamP2PTransport();

        private const int ChannelIndex = 0;
        private const int MaxPacketsPerUpdate = 100;

        public bool IsInitialised { get; private set; }
        public ulong LocalId { get; private set; }

        public event Action<ulong, byte[]> PacketReceived;
        public event Action<ulong> SessionRequested;
        public event Action<ulong> SessionFailed;

        private Callback<P2PSessionRequest_t> _sessionRequestCallback;
        private Callback<P2PSessionConnectFail_t> _sessionFailCallback;

        private SteamP2PTransport() { }

        public void Initialise()
        {
            if (IsInitialised) return;

            try
            {
                LocalId = SteamUser.GetSteamID().m_SteamID;

                _sessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
                _sessionFailCallback = Callback<P2PSessionConnectFail_t>.Create(OnSessionFail);

                IsInitialised = true;
                Debug.Log($"[Transport] Initialised. LocalId={LocalId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Transport] Initialise failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public void Update()
        {
            if (!IsInitialised) return;

            // Steamworks may not have had a valid user ID yet at Initialise() time.
            if (LocalId == 0)
            {
                LocalId = SteamUser.GetSteamID().m_SteamID;
            }

            SteamAPI.RunCallbacks();
            ReadPackets();
        }

        public bool TrySend(ulong targetId, byte[] data, SendReliability reliability)
        {
            if (!IsInitialised) return false;

            var target = new CSteamID(targetId);
            var sendType = reliability == SendReliability.Reliable
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliable;

            bool ok = SteamNetworking.SendP2PPacket(target, data, (uint)data.Length, sendType, ChannelIndex);
            if (!ok)
            {
                Debug.LogWarning($"[Transport] SendP2PPacket to {targetId} failed (size={data.Length}).");
            }
            return ok;
        }

        private void ReadPackets()
        {
            int read = 0;
            while (read++ < MaxPacketsPerUpdate && SteamNetworking.IsP2PPacketAvailable(out uint size, ChannelIndex))
            {
                var buffer = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint actualSize, out CSteamID sender, ChannelIndex))
                {
                    continue;
                }

                if (actualSize != buffer.Length)
                {
                    Array.Resize(ref buffer, (int)actualSize);
                }

                PacketReceived?.Invoke(sender.m_SteamID, buffer);
            }
        }

        private void OnSessionRequest(P2PSessionRequest_t data)
        {
            ulong remoteId = data.m_steamIDRemote.m_SteamID;
            bool accepted = SteamNetworking.AcceptP2PSessionWithUser(data.m_steamIDRemote);
            Debug.Log($"[Transport] Session request from {remoteId}, accepted={accepted}");
            SessionRequested?.Invoke(remoteId);
        }

        private void OnSessionFail(P2PSessionConnectFail_t data)
        {
            ulong remoteId = data.m_steamIDRemote.m_SteamID;
            Debug.LogWarning($"[Transport] Session with {remoteId} failed (error={data.m_eP2PSessionError}).");
            SessionFailed?.Invoke(remoteId);
        }
    }
}
