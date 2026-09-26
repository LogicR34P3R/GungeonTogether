using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Empty keep-alive; its arrival alone refreshes the sender's PeerConnection timeout.</summary>
    public class HeartbeatPacket : INetworkPacket
    {
        public PacketType Type => PacketType.Heartbeat;

        public void Serialize(BinaryWriter writer) { }
        public void Deserialize(BinaryReader reader) { }
    }
}
