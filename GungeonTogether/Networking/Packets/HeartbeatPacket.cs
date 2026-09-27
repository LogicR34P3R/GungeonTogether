using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Keep-alive that doubles as a ping. Its arrival alone refreshes the sender's PeerConnection
    /// timeout. The receiver echoes it straight back with the same Timestamp, and when the echo
    /// returns, the original sender's round-trip time is (now - Timestamp) on its own clock - so the
    /// two machines' clocks never need to agree.
    /// </summary>
    public class HeartbeatPacket : INetworkPacket
    {
        public PacketType Type => PacketType.Heartbeat;

        public float Timestamp; // sender's Time.realtimeSinceStartup when sent
        public bool IsEcho;     // true: this is the reply to a heartbeat we sent

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Timestamp);
            writer.Write(IsEcho);
        }

        public void Deserialize(BinaryReader reader)
        {
            Timestamp = reader.ReadSingle();
            IsEcho = reader.ReadBoolean();
        }
    }
}
