using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class EnemyDeathPacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyDeath;

        public int EnemyId;
        public bool Killed; // died (play its death); otherwise it just left the game (e.g. despawned) - remove quietly

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Killed);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Killed = reader.ReadBoolean();
        }
    }
}
