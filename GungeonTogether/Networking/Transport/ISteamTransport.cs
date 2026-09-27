using System;

namespace GungeonTogether.Networking.Transport
{
    public enum SendReliability
    {
        Unreliable,
        Reliable
    }

    /// <summary>
    /// Raw peer-to-peer byte transport. The only layer allowed to know Steam exists.
    /// </summary>
    public interface ISteamTransport
    {
        bool IsInitialised { get; }
        ulong LocalId { get; }

        void Initialise();
        void Update();
        bool TrySend(ulong targetId, byte[] data, SendReliability reliability);

        /// <summary>
        /// Whether traffic to this peer goes through Steam's relay servers rather than a direct
        /// connection - relaying typically adds tens to hundreds of ms of ping. False if unknown.
        /// </summary>
        bool TryGetRelayState(ulong peerId, out bool relayed);

        /// <summary>Raised when a full packet has been received from a peer.</summary>
        event Action<ulong, byte[]> PacketReceived;

        /// <summary>Raised when a remote peer is attempting to open a P2P session with us.</summary>
        event Action<ulong> SessionRequested;

        /// <summary>Raised when an existing P2P session with a peer breaks down.</summary>
        event Action<ulong> SessionFailed;
    }
}
