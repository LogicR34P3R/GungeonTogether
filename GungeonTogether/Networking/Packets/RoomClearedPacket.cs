using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Host → client: the host cleared this room (by room name). The client's own rooms never clear by themselves.</summary>
    public class RoomClearedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.RoomCleared;

        public string RoomName;

        public void Serialize(BinaryWriter writer) => writer.Write(RoomName ?? "");
        public void Deserialize(BinaryReader reader) => RoomName = reader.ReadString();
    }
}
