using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Host → client: the shared money/keys pool, absolute values.</summary>
    public class ConsumablesStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.ConsumablesState;

        public int Currency;
        public int KeyBullets;
        public int RatKeys;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Currency);
            writer.Write(KeyBullets);
            writer.Write(RatKeys);
        }

        public void Deserialize(BinaryReader reader)
        {
            Currency = reader.ReadInt32();
            KeyBullets = reader.ReadInt32();
            RatKeys = reader.ReadInt32();
        }
    }
}
