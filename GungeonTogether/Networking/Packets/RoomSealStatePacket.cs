using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Host → client: a room's doors sealed or unsealed on the host (by room name).</summary>
    public class RoomSealStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.RoomSealState;

        public string RoomName;
        public bool Sealed;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(RoomName ?? "");
            writer.Write(Sealed);
        }

        public void Deserialize(BinaryReader reader)
        {
            RoomName = reader.ReadString();
            Sealed = reader.ReadBoolean();
        }
    }
}
