using System;
using System.Collections.Generic;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Transport;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Protocol
{
    /// <summary>
    /// Sits between NetworkSession and the raw transport: frames every outgoing packet with a
    /// per-peer sequence number, drops stale/duplicate packets that arrived out of order on the
    /// unreliable channel, and splits/reassembles payloads too big for one P2P datagram.
    /// </summary>
    public class PacketChannel
    {
        // Steam P2P packets can be much larger than this in practice, but relayed connections
        // (no direct route between peers) have a much lower real-world ceiling. Chunk well under
        // it so nothing here depends on how two given peers happen to be routed.
        private const int MaxChunkPayloadBytes = 1100;
        private const float ChunkReassemblyTimeoutSeconds = 5f;

        private readonly ISteamTransport _transport;

        private readonly Dictionary<ulong, ushort> _sendSequence = new Dictionary<ulong, ushort>();
        private readonly Dictionary<ulong, ushort> _lastAcceptedUnreliableSequence = new Dictionary<ulong, ushort>();
        private readonly Dictionary<ulong, Dictionary<ushort, PendingGroup>> _pendingChunks = new Dictionary<ulong, Dictionary<ushort, PendingGroup>>();

        /// <summary>Fires for every raw frame received from a peer, chunk or not - useful as a liveness signal.</summary>
        public event Action<ulong> FrameReceived;

        /// <summary>Fires once a packet has been fully reassembled (if needed) and deserialized.</summary>
        public event Action<ulong, INetworkPacket> PacketReceived;

        public PacketChannel(ISteamTransport transport)
        {
            _transport = transport;
            _transport.PacketReceived += OnRawDataReceived;
        }

        public void Send(ulong targetId, byte[] payload, SendReliability reliability)
        {
            ushort sequence = NextSequence(targetId);
            bool reliableFlag = reliability == SendReliability.Reliable;

            if (payload.Length <= MaxChunkPayloadBytes)
            {
                SendFrame(targetId, reliableFlag, sequence, 0, 1, payload, 0, payload.Length, reliability);
                return;
            }

            int chunkCount = (payload.Length + MaxChunkPayloadBytes - 1) / MaxChunkPayloadBytes;
            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * MaxChunkPayloadBytes;
                int length = Math.Min(MaxChunkPayloadBytes, payload.Length - offset);
                SendFrame(targetId, reliableFlag, sequence, i, chunkCount, payload, offset, length, reliability);
            }
        }

        /// <summary>Expires any chunk groups that never finished reassembling in time.</summary>
        public void Update(float now)
        {
            foreach (var peerGroups in _pendingChunks.Values)
            {
                List<ushort> expired = null;
                foreach (var kv in peerGroups)
                {
                    if (now - kv.Value.FirstSeenAt > ChunkReassemblyTimeoutSeconds)
                    {
                        (expired ?? (expired = new List<ushort>())).Add(kv.Key);
                    }
                }
                if (expired != null)
                {
                    foreach (var seq in expired) peerGroups.Remove(seq);
                }
            }
        }

        private ushort NextSequence(ulong peerId)
        {
            ushort next = (ushort)(_sendSequence.TryGetValue(peerId, out ushort current) ? current + 1 : 1);
            _sendSequence[peerId] = next;
            return next;
        }

        private void SendFrame(ulong targetId, bool reliableFlag, ushort sequence, int chunkIndex, int chunkCount, byte[] source, int offset, int length, SendReliability reliability)
        {
            // Header: reliableFlag(1) + sequence(2) + chunkIndex(2) + chunkCount(2) = 7 bytes.
            byte[] frame = new byte[7 + length];
            frame[0] = reliableFlag ? (byte)1 : (byte)0;
            WriteUInt16(frame, 1, sequence);
            WriteUInt16(frame, 3, (ushort)chunkIndex);
            WriteUInt16(frame, 5, (ushort)chunkCount);
            Array.Copy(source, offset, frame, 7, length);

            _transport.TrySend(targetId, frame, reliability);
        }

        private void OnRawDataReceived(ulong senderId, byte[] frame)
        {
            FrameReceived?.Invoke(senderId);

            if (frame.Length < 7) return;

            bool reliableFlag = frame[0] != 0;
            ushort sequence = ReadUInt16(frame, 1);
            ushort chunkIndex = ReadUInt16(frame, 3);
            ushort chunkCount = ReadUInt16(frame, 5);

            byte[] completePayload;
            if (chunkCount <= 1)
            {
                completePayload = new byte[frame.Length - 7];
                Array.Copy(frame, 7, completePayload, 0, completePayload.Length);
            }
            else
            {
                completePayload = ReassembleChunk(senderId, sequence, chunkIndex, chunkCount, frame);
                if (completePayload == null) return; // group not complete yet
            }

            if (!reliableFlag)
            {
                // Reliable frames are delivered in order with no duplicates by Steam itself, so
                // only the unreliable stream needs staleness filtering here.
                if (_lastAcceptedUnreliableSequence.TryGetValue(senderId, out ushort lastAccepted)
                    && IsStaleOrDuplicate(lastAccepted, sequence))
                {
                    return;
                }
                _lastAcceptedUnreliableSequence[senderId] = sequence;
            }

            INetworkPacket packet = PacketSerializer.Deserialize(completePayload);
            if (packet == null)
            {
                Debug.LogWarning($"[PacketChannel] Failed to deserialize packet from {senderId}.");
                return;
            }

            PacketReceived?.Invoke(senderId, packet);
        }

        private byte[] ReassembleChunk(ulong senderId, ushort sequence, ushort chunkIndex, ushort chunkCount, byte[] frame)
        {
            if (!_pendingChunks.TryGetValue(senderId, out var groups))
            {
                groups = new Dictionary<ushort, PendingGroup>();
                _pendingChunks[senderId] = groups;
            }

            if (!groups.TryGetValue(sequence, out var group))
            {
                group = new PendingGroup(chunkCount);
                groups[sequence] = group;
            }

            int chunkLength = frame.Length - 7;
            byte[] chunkPayload = new byte[chunkLength];
            Array.Copy(frame, 7, chunkPayload, 0, chunkLength);
            group.SetChunk(chunkIndex, chunkPayload);

            if (!group.IsComplete) return null;

            groups.Remove(sequence);
            return group.Combine();
        }

        // ushort sequence wraps around; treat anything "behind" within half the range as stale.
        private static bool IsStaleOrDuplicate(ushort lastAccepted, ushort candidate)
        {
            ushort delta = (ushort)(candidate - lastAccepted);
            return delta == 0 || delta > 0x8000;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private class PendingGroup
        {
            private readonly byte[][] _chunks;
            public float FirstSeenAt;
            private int _received;

            public PendingGroup(int chunkCount)
            {
                _chunks = new byte[chunkCount][];
                FirstSeenAt = UnityEngine.Time.realtimeSinceStartup;
            }

            public bool IsComplete => _received == _chunks.Length;

            public void SetChunk(int index, byte[] data)
            {
                if (index < 0 || index >= _chunks.Length) return;
                if (_chunks[index] == null) _received++;
                _chunks[index] = data;
            }

            public byte[] Combine()
            {
                int total = 0;
                foreach (var c in _chunks) total += c.Length;

                byte[] result = new byte[total];
                int offset = 0;
                foreach (var c in _chunks)
                {
                    Array.Copy(c, 0, result, offset, c.Length);
                    offset += c.Length;
                }
                return result;
            }
        }
    }
}
