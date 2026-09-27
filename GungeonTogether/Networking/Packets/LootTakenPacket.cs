using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>The sender picked up (or otherwise lost) this item; the receiver removes its copy.</summary>
    public class LootTakenPacket : INetworkPacket
    {
        public PacketType Type => PacketType.LootTaken;

        public ulong OwnerId;
        public int LocalId;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(OwnerId);
            writer.Write(LocalId);
        }

        public void Deserialize(BinaryReader reader)
        {
            OwnerId = reader.ReadUInt64();
            LocalId = reader.ReadInt32();
        }
    }
}
