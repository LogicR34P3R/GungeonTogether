using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: an enemy's sky rocket (Gatling Gull) went up, picked where it comes down
    /// (the landing marker appears) or was destroyed in the air. The client flies a harmless copy;
    /// the blast arrives as an Explosion packet.
    /// </summary>
    public class SkyRocketPacket : INetworkPacket
    {
        public enum RocketEvent : byte
        {
            Launch = 0, // X/Y: start position
            Land = 1,   // X/Y: landing spot
            Stop = 2
        }

        public PacketType Type => PacketType.SkyRocket;

        public RocketEvent Event;
        public int RocketId;   // the host's own counter, per session
        public int EnemyId;    // Launch: the enemy it was fired from (0 if unknown)
        public float X;
        public float Y;
        public string PrefabName = ""; // Launch/Land: the rocket prefab

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Event);
            writer.Write(RocketId);
            writer.Write(EnemyId);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(PrefabName ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            Event = (RocketEvent)reader.ReadByte();
            RocketId = reader.ReadInt32();
            EnemyId = reader.ReadInt32();
            X = reader.ReadSingle();
            Y = reader.ReadSingle();
            PrefabName = reader.ReadString();
        }
    }
}
