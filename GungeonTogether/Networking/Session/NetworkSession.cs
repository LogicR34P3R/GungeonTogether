using System;
using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Protocol;
using GungeonTogether.Networking.Entities;
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

        // 2: added Heartbeat packet; WorldState is now sent only on floor/foyer change.
        // 3: added RunSeed and LayoutHash packets (shared dungeon seed).
        // 4: levels identified by scene name in WorldState/LayoutHash; added LevelTransition.
        // 5: added RoomCleared; clients now run host enemies as puppets.
        // 6: added RoomSealState (door sync).
        // 7: added LootSpawn/LootTaken (loot sync).
        // 8: added ConsumablesState/ConsumablesDelta (shared money/keys).
        // 9: added ChestSpawn/ChestInteract/ChestState (host-authoritative chests).
        public const int ProtocolVersion = 9;

        // Liveness must not depend on gameplay traffic: position packets stop whenever there's no
        // PrimaryPlayer (e.g. mid level load), which would otherwise trip PeerConnection's timeout.
        private const float HeartbeatInterval = 1f;
        private float _nextHeartbeatTime;

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

            if (Role != NetworkRole.None && now >= _nextHeartbeatTime)
            {
                _nextHeartbeatTime = now + HeartbeatInterval;
                foreach (var peer in _peers.Values)
                {
                    if (peer.State == ConnectionState.Connected)
                    {
                        SendPacket(peer.PeerId, new HeartbeatPacket(), reliable: false);
                    }
                }
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
            // Both roles say goodbye: a client tells the host, and a host tells every client, so
            // nobody has to sit out the timeout to notice.
            foreach (var peer in _peers.Values)
            {
                if (peer.State == ConnectionState.Connected)
                {
                    SendPacket(peer.PeerId, new DisconnectPacket(), reliable: true);
                }
            }

            _peers.Clear();
            Role = NetworkRole.None;

            // Otherwise remote avatars and client-side enemy copies linger, frozen, after leaving.
            PlayerReplicator.Instance.ClearAll();
            NetworkEntityManager.Instance.Clear();
            WorldStateReplicator.Instance.ResetClientState();
            LoadingStateReplicator.Instance.ApplyLoadingState(false);
            DungeonSeedReplicator.Instance.ResetSessionState();
            EnemyReplicator.Instance.ResetSessionState();
            LootReplicator.Instance.ResetSessionState();
            ConsumablesReplicator.Instance.ResetSessionState();
        }

        /// <summary>
        /// Client-only: the host is gone (timed out, disconnected, or refused us). Drop back to no
        /// role and leave the lobby too - staying in it would leave the UI claiming a session that
        /// no longer exists, with no way to reconnect from inside it.
        /// </summary>
        private void EndClientSession(string reason)
        {
            Debug.LogWarning($"[Session] Leaving session: {reason}");
            Shutdown();
            SteamLobby.Instance.LeaveLobby();
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

            Debug.LogWarning($"[Session] Peer {peerId} disconnected (disconnect, timeout, or session failure).");
            if (IsHost)
            {
                PlayerReplicator.Instance.RemoveRemotePlayer(peerId);
            }
            else if (IsClient)
            {
                // A client's only peer is the host - losing it means the session is over.
                EndClientSession($"lost connection to host {peerId}");
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
            if (!IsFromKnownPeer(senderId, packet))
            {
                // Trace, not Warning: a stray sender could otherwise flood the log at packet rate.
                Debug.LogTrace($"[Session] Dropped {packet.Type} from unknown/unconnected sender {senderId}.");
                return;
            }

            try
            {
                Route(senderId, packet);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] Error handling packet from {senderId}: {e.Message}");
            }
        }

        /// <summary>
        /// The transport accepts P2P sessions from anyone, so this is the gate: a host only takes a
        /// ConnectionRequest from strangers and everything else from connected clients; a client only
        /// takes packets from its host (whose handshake replies arrive while it's still Connecting).
        /// </summary>
        private bool IsFromKnownPeer(ulong senderId, INetworkPacket packet)
        {
            _peers.TryGetValue(senderId, out var peer);
            switch (Role)
            {
                case NetworkRole.Host:
                    return packet.Type == PacketType.ConnectionRequest
                        || (peer != null && peer.State == ConnectionState.Connected);
                case NetworkRole.Client:
                    return peer != null;
                default:
                    return false;
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
                    HandlePeerTimeout(senderId);
                    break;

                case PacketType.Heartbeat:
                    // Nothing to do - MarkPeerSeen already ran for this frame.
                    break;

                case PacketType.RunSeed:
                    if (IsClient) DungeonSeedReplicator.Instance.HandleRunSeed((RunSeedPacket)packet);
                    break;

                case PacketType.LayoutHash:
                    if (IsClient) DungeonSeedReplicator.Instance.HandleLayoutHash((LayoutHashPacket)packet);
                    break;

                case PacketType.LevelTransition:
                    if (IsClient) WorldStateReplicator.Instance.HandleLevelTransition((LevelTransitionPacket)packet);
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
                        // Only ever the sender itself - trusting leavePacket.PlayerId would let any
                        // client kick any other.
                        HandlePeerTimeout(senderId);
                    }
                    break;

                case PacketType.RoomChange:
                    if (IsClient) EnemyReplicator.Instance.HandleRoomChange((RoomChangePacket)packet);
                    break;

                case PacketType.RoomCleared:
                    if (IsClient) EnemyReplicator.Instance.HandleRoomCleared((RoomClearedPacket)packet);
                    break;

                case PacketType.RoomSealState:
                    if (IsClient) EnemyReplicator.Instance.HandleRoomSealState((RoomSealStatePacket)packet);
                    break;

                // Both directions: the host also relays a client's loot to other clients.
                case PacketType.LootSpawn:
                    LootReplicator.Instance.HandleLootSpawn(senderId, (LootSpawnPacket)packet);
                    break;

                case PacketType.LootTaken:
                    LootReplicator.Instance.HandleLootTaken(senderId, (LootTakenPacket)packet);
                    break;

                case PacketType.ConsumablesState:
                    if (IsClient) ConsumablesReplicator.Instance.HandleState((ConsumablesStatePacket)packet);
                    break;

                case PacketType.ConsumablesDelta:
                    if (IsHost) ConsumablesReplicator.Instance.HandleDelta((ConsumablesDeltaPacket)packet);
                    break;

                case PacketType.ChestSpawn:
                    if (IsClient) ChestReplicator.Instance.HandleSpawn((ChestSpawnPacket)packet);
                    break;

                case PacketType.ChestInteract:
                    if (IsHost) ChestReplicator.Instance.HandleInteract((ChestInteractPacket)packet);
                    break;

                case PacketType.ChestState:
                    if (IsClient) ChestReplicator.Instance.HandleState((ChestStatePacket)packet);
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
                    else if (IsHost)
                    {
                        HandlePlayerState(senderId, (PlayerStatePacket)packet);
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

            // Only players who joined our Steam lobby may connect - checked first, so strangers get
            // no reply at all. Deliberately silent rather than a rejection: Steam's local member
            // list can lag a lobby join slightly, and a real member's client re-sends its request
            // every second, so ignoring it self-heals once the list catches up.
            if (!SteamLobby.Instance.GetLobbyMembers().Contains(transportId))
            {
                Debug.Log($"[Session] Ignored ConnectionRequest from {transportId} - not a member of our lobby.");
                return;
            }

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

            // The Steam sender id is authenticated and is the client's own SteamID, so it *is* the
            // player id. request.ClientId is only the client's claim - trusting it would let one
            // client impersonate another.
            var peer = new PeerConnection(this, transportId) { PlayerId = transportId };
            peer.MarkConnected(Time.realtimeSinceStartup);
            _peers[transportId] = peer;

            SendPacket(transportId, new ConnectionAcceptedPacket
            {
                HostId = _transport.LocalId,
                ProtocolVersion = ProtocolVersion
            }, reliable: true);

            // Seed first: the world state may send a mid-run joiner straight to the host's floor,
            // and that floor must generate from the host's seed.
            DungeonSeedReplicator.Instance.SendCurrentSeedTo(transportId);
            WorldStateReplicator.Instance.SendCurrentStateTo(transportId);
            EnemyReplicator.Instance.SendCurrentRoomStateTo(transportId);
            ConsumablesReplicator.Instance.SendCurrentStateTo(transportId);
        }

        private void HandleConnectionAccepted(ulong senderId, ConnectionAcceptedPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out var peer)) return;

            if (packet.ProtocolVersion != ProtocolVersion)
            {
                EndClientSession($"protocol mismatch (host={packet.ProtocolVersion}, local={ProtocolVersion})");
                return;
            }

            peer.MarkConnected(Time.realtimeSinceStartup);
            Debug.LogInfo($"[Session] Connection accepted by host {senderId}.");
        }

        private void HandleConnectionRejected(ulong senderId, ConnectionRejectedPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out _)) return;

            _peers.Remove(senderId);
            EndClientSession($"rejected by host {senderId} (host protocol={packet.ProtocolVersion}, local={ProtocolVersion})");
        }

        private void HandlePlayerPosition(ulong senderId, PlayerPositionPacket packet)
        {
            if (!_peers.TryGetValue(senderId, out var peer) || peer.State != ConnectionState.Connected)
            {
                Debug.LogWarningThrottled($"Session.IgnoredPosition:{senderId}", $"[Session] Ignored position packet from unrecognised/unconnected sender {senderId}.");
                return;
            }

            // Stamp the sender's real id before applying or relaying, so a client can't move
            // someone else's avatar by writing their id into the packet.
            packet.PlayerId = peer.PlayerId;
            PlayerReplicator.Instance.UpdateRemotePlayer(packet.PlayerId, packet.Position, packet.Rotation, packet.FlipX);
            Broadcast(packet, senderId, reliable: false);
        }

        private void HandlePlayerState(ulong senderId, PlayerStatePacket packet)
        {
            // IsFromKnownPeer already guaranteed a connected peer; same id stamping as positions.
            if (!_peers.TryGetValue(senderId, out var peer)) return;

            packet.PlayerId = peer.PlayerId;
            PlayerReplicator.Instance.ApplyPlayerState(packet);
            Broadcast(packet, senderId, reliable: false);
        }

    }
}
