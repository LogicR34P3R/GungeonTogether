using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Client → host: the client gained or spent money/keys (relative change, e.g. +3 / -1).</summary>
    public class ConsumablesDeltaPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ConsumablesDelta;

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
