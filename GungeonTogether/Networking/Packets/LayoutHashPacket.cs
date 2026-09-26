using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: fingerprint of the floor the host just generated, so the client can check
    /// whether the shared seed actually produced the same layout on its side.
    /// </summary>
    public class LayoutHashPacket : INetworkPacket
    {
        public PacketType Type => PacketType.LayoutHash;

        public string SceneName;
        public int Seed;
        public uint Hash;
        public int RoomCount;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(SceneName ?? "");
            writer.Write(Seed);
            writer.Write(Hash);
            writer.Write(RoomCount);
        }

        public void Deserialize(BinaryReader reader)
        {
            SceneName = reader.ReadString();
            Seed = reader.ReadInt32();
            Hash = reader.ReadUInt32();
            RoomCount = reader.ReadInt32();
        }
    }
}
