using System;
using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Protocol;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Networking.Replication;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Session
{
    public enum NetworkRole
    {
        None,
        Host,
        Client
    }

    /// <summary>
    /// Owns the local role (host/client/none), the peer table, and packet routing.
    /// Replaces the old NetworkManager + HostController + ClientController split -
    /// hosting and joining differ only in a handful of branches, not in enough behavior
    /// to warrant separate polymorphic role classes.
    /// </summary>
    public class NetworkSession
    {
        private static NetworkSession _instance;
        public static NetworkSession Instance => _instance ??= new NetworkSession();

        public const int ProtocolVersion = 1;

        private readonly ISteamTransport _transport = SteamP2PTransport.Instance;
        private readonly PacketChannel _packetChannel;
        private readonly Dictionary<ulong, PeerConnection> _peers = new Dictionary<ulong, PeerConnection>();

        public NetworkRole Role { get; private set; } = NetworkRole.None;
        public bool IsHost => Role == NetworkRole.Host;
        public bool IsClient => Role == NetworkRole.Client;
        public bool IsConnected => Role != NetworkRole.None;

        private NetworkSession()
        {
            _packetChannel = new PacketChannel(_transport);
        }

        public void Initialise()
        {
            try
            {
                _transport.Initialise();
                _packetChannel.FrameReceived += MarkPeerSeen;
                _packetChannel.PacketReceived += HandlePacket;
                _transport.SessionFailed += HandlePeerTimeout;

                SteamLobby.Instance.Initialise();
                SteamLobby.Instance.LobbyHostReady += _ => StartHosting();
                SteamLobby.Instance.LobbyJoinReady += ownerId => ConnectTo(ownerId);

                Debug.Log("[Session] Initialised.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Session] Exception during initialisation: {ex.GetType().Name}: {ex.Message}");
                Debug.LogError($"[Session] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void Update()
        {
            _transport.Update();

            float now = Time.realtimeSinceStartup;
            _packetChannel.Update(now);

            // PeerConnection.Update() can remove itself from _peers (timeout), so snapshot first.
            foreach (var peer in new List<PeerConnection>(_peers.Values))
            {
                peer.Update(now);
            }
        }

        public void StartHosting()
        {
            if (Role != NetworkRole.None) Shutdown();

            Role = NetworkRole.Host;
            Debug.LogInfo("[Session] Started hosting.");
        }

        public void ConnectTo(ulong hostId)
        {
            if (Role != NetworkRole.None) Shutdown();

            Role = NetworkRole.Client;
            _peers[hostId] = new PeerConnection(this, hostId);
            Debug.LogInfo($"[Session] Connecting to host {hostId}...");
        }

        public void Shutdown()
        {
            if (Role == NetworkRole.Client)
            {
                foreach (var peer in _peers.Values)
                {
                    if (peer.State == ConnectionState.Connected)
                    {
                        SendPacket(peer.PeerId, new DisconnectPacket(), reliable: true);
                    }
                }
            }

            _peers.Clear();
            Role = NetworkRole.None;
        }

        public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
        {
            byte[] payload = PacketSerializer.Serialize(packet);
            _packetChannel.Send(targetId, payload, reliable ? SendReliability.Reliable : SendReliability.Unreliable);
        }

        /// <summary>Host-only: sends to every connected client except excludeId.</summary>
        public void Broadcast(INetworkPacket packet, ulong excludeId = 0, bool reliable = true)
        {
            if (!IsHost) return;

            byte[] payload = PacketSerializer.Serialize(packet);
            var reliability = reliable ? SendReliability.Reliable : SendReliability.Unreliable;
            int count = 0;
            foreach (var peer in _peers.Values)
            {
                if (peer.PeerId == excludeId || peer.State != ConnectionState.Connected) continue;
                _packetChannel.Send(peer.PeerId, payload, reliability);
                count++;
            }
            Debug.LogTrace($"[Session] Broadcast {packet.Type} to {count} client(s) (excluded {excludeId}).");
        }

        /// <summary>Client-only: sends to the (single) host peer. No-op if not connected as a client.</summary>
        public void SendToHost(INetworkPacket packet, bool reliable = true)
        {
            var hostPeer = GetHostPeer();
            if (hostPeer == null || hostPeer.State != ConnectionState.Connected) return;
            SendPacket(hostPeer.PeerId, packet, reliable);
        }

        internal void SendConnectionRequest(ulong hostId)
        {
            SendPacket(hostId, new ConnectionRequestPacket
            {
                ClientId = _transport.LocalId,
                ProtocolVersion = ProtocolVersion
            }, reliable: true);
        }

        internal void HandlePeerTimeout(ulong peerId)
        {
            if (!_peers.Remove(peerId)) return;

            Debug.LogWarning($"[Session] Peer {peerId} disconnected (timeout or session failure).");
            if (IsHost)
            {
                PlayerReplicator.Instance.RemoveRemotePlayer(peerId);
            }
        }

        private PeerConnection GetHostPeer()
        {
            foreach (var peer in _peers.Values) return peer; // client only ever has one entry
            return null;
        }

        private void MarkPeerSeen(ulong senderId)
        {
            if (_peers.TryGetValue(senderId, out var peer))
            {
                peer.MarkSeen(Time.realtimeSinceStartup);
            }
        }

        private void HandlePacket(ulong senderId, INetworkPacket packet)
        {
            try
            {
                Route(senderId, packet);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] Error handling packet from {senderId}: {e.Message}");
            }
        }

        private void Route(ulong senderId, INetworkPacket packet)
        {
            switch (packet.Type)
            {
                case PacketType.ConnectionRequest:
                    if (IsHost) HandleJoinRequest(senderId, (ConnectionRequestPacket)packet);
                    break;

                case PacketType.ConnectionAccepted:
                    if (IsClient) HandleConnectionAccepted(senderId, (ConnectionAcceptedPacket)packet);
                    break;

                case PacketType.ConnectionRejected:
                    if (IsClient) HandleConnectionRejected(senderId, (ConnectionRejectedPacket)packet);
                    break;

                case PacketType.PlayerPosition:
                    var posPacket = (PlayerPositionPacket)packet;
                    if (IsClient && posPacket.PlayerId != _transport.LocalId)
                    {
                        PlayerReplicator.Instance.UpdateRemotePlayer(posPacket.PlayerId, posPacket.Position, posPacket.Rotation, posPacket.FlipX);
                    }
                    else if (IsHost)
                    {
                        HandlePlayerPosition(senderId, posPacket);
                    }
                    break;

                case PacketType.Disconnect:
                    if (IsHost) HandlePeerTimeout(senderId);
                    break;

                case PacketType.PlayerJoin:
                    var joinPacket = (PlayerJoinPacket)packet;
                    if (IsClient && joinPacket.PlayerId != _transport.LocalId)
                    {
                        PlayerReplicator.Instance.SpawnRemotePlayer(joinPacket.PlayerId, joinPacket.Position, joinPacket.Rotation);
                    }
                    break;

                case PacketType.PlayerLeave:
                    var leavePacket = (PlayerLeavePacket)packet;
                    if (IsClient)
                    {
                        PlayerReplicator.Instance.RemoveRemotePlayer(leavePacket.PlayerId);
                    }
                    else if (IsHost)
                    {
                        HandlePeerTimeout(leavePacket.PlayerId);
                    }
                    break;

                case PacketType.RoomChange:
                    NetworkEntityManager.Instance.Clear();
                    break;

                case PacketType.EnemySpawn:
                    if (IsClient) EnemyReplicator.Instance.HandleSpawn((EnemySpawnPacket)packet);
                    break;

                case PacketType.EnemyState:
                    if (IsClient) EnemyReplicator.Instance.HandleState((EnemyStatePacket)packet);
                    break;

                case PacketType.EnemyDeath:
                    if (IsClient) EnemyReplicator.Instance.HandleDeath((EnemyDeathPacket)packet);
                    break;

                case PacketType.WorldState:
                    if (IsClient)
                    {
                        WorldStateReplicator.Instance.ApplyWorldState((WorldStatePacket)packet);
                    }
                    break;

                case PacketType.PlayerState:
                    if (IsClient)
                    {
                        PlayerReplicator.Instance.ApplyPlayerState((PlayerStatePacket)packet);
                    }
                    break;

                case PacketType.LoadingState:
                    if (IsClient)
                    {
                        LoadingStateReplicator.Instance.ApplyLoadingState(((LoadingStatePacket)packet).IsLoading);
                    }
                    break;
            }
        }

        private void HandleJoinRequest(ulong transportId, ConnectionRequestPacket request)
        {
            Debug.Log($"[Session] Host received ConnectionRequest from {transportId}, version={request.ProtocolVersion}, expected={ProtocolVersion}");

            if (request.ProtocolVersion != ProtocolVersion)
            {
                SendPacket(transportId, new ConnectionRejectedPacket { ProtocolVersion = ProtocolVersion }, reliable: true);
                Debug.LogInfo($"[Session] Rejected {transportId} - protocol mismatch.");
                return;
            }

            if (_peers.TryGetValue(transportId, out var existing) && existing.State == ConnectionState.Connected)
            {
                Debug.Log($"[Session] {transportId} already connected.");
                return;
            }

            var peer = new PeerConnection(this, transportId) { PlayerId = request.ClientId != 0 ? request.ClientId : transportId };
            peer.MarkConnected(Time.realtimeSinceStartup);
            _peers[transportId] = peer;

            SendPacket(transportId, new ConnectionAcceptedPacket
            {
                HostId = _transport.LocalId,
                ProtocolVersion = ProtocolVersion
            }, reliable: true);

            WorldStateReplicator.Instance.SendCurrentStateTo(transportId);
        }

        private void HandleConnectionAccepted(ulong senderId, ConnectionAcceptedPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out var peer)) return;

            if (packet.ProtocolVersion != ProtocolVersion)
            {
                Debug.LogWarning($"[Session] Protocol mismatch. Host={packet.ProtocolVersion} Local={ProtocolVersion}");
                Shutdown();
                return;
            }

            peer.MarkConnected(Time.realtimeSinceStartup);
            Debug.LogInfo($"[Session] Connection accepted by host {senderId}.");
        }

        private void HandleConnectionRejected(ulong senderId, ConnectionRejectedPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out _)) return;

            _peers.Remove(senderId);
            Debug.LogWarning($"[Session] Connection rejected by host {senderId} (host protocol={packet.ProtocolVersion}, local={ProtocolVersion}). Halting connection attempts.");
        }

        private void HandlePlayerPosition(ulong senderId, PlayerPositionPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out var peer) || peer.State != ConnectionState.Connected)
            {
                Debug.LogWarning($"[Session] Ignored position packet from unrecognised/unconnected sender {senderId}.");
                return;
            }

            ulong playerId = peer.PlayerId != 0 ? peer.PlayerId : packet.PlayerId;
            PlayerReplicator.Instance.UpdateRemotePlayer(playerId, packet.Position, packet.Rotation, packet.FlipX);
            Broadcast(packet, senderId, reliable: false);
        }

    }
}
