using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Host → client: the run seed both sides feed to GameManager.InitializeForRunWithSeed.</summary>
    public class RunSeedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.RunSeed;

        public int Seed;

        public void Serialize(BinaryWriter writer) => writer.Write(Seed);
        public void Deserialize(BinaryReader reader) => Seed = reader.ReadInt32();
    }
}
