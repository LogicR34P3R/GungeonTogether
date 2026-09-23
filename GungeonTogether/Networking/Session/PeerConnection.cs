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

        private readonly NetworkSession _session;

        public ulong PeerId { get; }
        public ulong PlayerId { get; set; }
        public ConnectionState State { get; private set; }

        private float _lastSeen;
        private float _nextRetryTime;

        public PeerConnection(NetworkSession session, ulong peerId)
        {
            _session = session;
            PeerId = peerId;
            PlayerId = peerId;
            State = ConnectionState.Connecting;
        }

        public void MarkSeen(float now) => _lastSeen = now;

        public void MarkConnected(float now)
        {
            State = ConnectionState.Connected;
            _lastSeen = now;
        }

        public void MarkRejected() => State = ConnectionState.Rejected;

        public void Update(float now)
        {
            switch (State)
            {
                case ConnectionState.Connecting:
                    // Only the client side actively retries; the host just waits for a
                    // ConnectionRequest and responds to it.
                    if (_session.Role == NetworkRole.Client && now >= _nextRetryTime)
                    {
                        _nextRetryTime = now + ConnectRetryInterval;
                        _session.SendConnectionRequest(PeerId);
                    }
                    break;

                case ConnectionState.Connected:
                    // No packet at all (position updates alone arrive every ~0.25s while
                    // connected) for this long means the peer is gone even if Steam never
                    // raised a session-failed callback for it.
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
