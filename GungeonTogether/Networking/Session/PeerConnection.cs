namespace GungeonTogether.Networking.Session
{
    /// <summary>
    /// Tracks one remote peer's connection lifecycle. The host keeps one of these per
    /// connected client; a client keeps exactly one, representing the host.
    /// </summary>
    public class PeerConnection
    {
        private const float ConnectRetryInterval = 1f;
        private const float TimeoutSeconds = 10f;
        private const float ConnectTimeoutSeconds = 15f;

        private readonly NetworkSession _session;

        public ulong PeerId { get; }
        public ulong PlayerId { get; set; }
        public ConnectionState State { get; private set; }

        private float _lastSeen;
        private float _nextRetryTime;
        private float _connectDeadline = -1f;

        public PeerConnection(NetworkSession session, ulong peerId)
        {
            _session = session;
            PeerId = peerId;
            PlayerId = peerId;
            State = ConnectionState.Connecting;
        }

        // Weight of each new sample in the smoothed round-trip time - smooths out a single slow packet.
        private const float PingSmoothing = 0.2f;

        /// <summary>Smoothed round-trip time to this peer in milliseconds, or -1 before the first measurement.</summary>
        public float PingMs { get; private set; } = -1f;

        /// <summary>Whether Steam is relaying this connection (null: not known yet). Refreshed with each ping.</summary>
        public bool? Relayed { get; set; }

        public void RecordPing(float roundTripSeconds)
        {
            float sampleMs = roundTripSeconds * 1000f;
            if (sampleMs < 0f || sampleMs > 60000f) return; // nonsense (e.g. an echo from before a restart)
            PingMs = PingMs < 0f ? sampleMs : PingMs + (sampleMs - PingMs) * PingSmoothing;
        }

        public void MarkSeen(float now) => _lastSeen = now;

        public void MarkConnected(float now)
        {
            State = ConnectionState.Connected;
            _lastSeen = now;
        }

        public void Update(float now)
        {
            switch (State)
            {
                case ConnectionState.Connecting:
                    // Only the client side actively retries; the host just waits for a
                    // ConnectionRequest and responds to it.
                    if (_session.Role != NetworkRole.Client) break;

                    if (_connectDeadline < 0f) _connectDeadline = now + ConnectTimeoutSeconds;
                    if (now >= _connectDeadline)
                    {
                        // Host never answered (gone, or ignoring us as a non-lobby-member) - give up
                        // instead of retrying forever.
                        State = ConnectionState.TimedOut;
                        _session.HandlePeerTimeout(PeerId);
                        break;
                    }

                    if (now >= _nextRetryTime)
                    {
                        _nextRetryTime = now + ConnectRetryInterval;
                        _session.SendConnectionRequest(PeerId);
                    }
                    break;

                case ConnectionState.Connected:
                    // No packet at all (NetworkSession sends a heartbeat every second even when
                    // there's no gameplay traffic) for this long means the peer is gone even if
                    // Steam never raised a session-failed callback for it.
                    if (now - _lastSeen > TimeoutSeconds)
                    {
                        State = ConnectionState.TimedOut;
                        _session.HandlePeerTimeout(PeerId);
                    }
                    break;
            }
        }
    }
}
