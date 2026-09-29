using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: an enemy's laser beam started, turned or stopped. The client fires the same
    /// beam from its puppet, so it's seen and hurts the client's player locally.
    /// </summary>
    public class BeamPacket : INetworkPacket
    {
        public enum BeamEvent : byte
        {
            Start = 0,
            Angle = 1,
            Stop = 2
        }

        // ShooterIndex for the Beholster's eye, which fires through BeholsterController instead.
        public const int BeholsterEye = -1;

        public PacketType Type => PacketType.Beam;

        public BeamEvent Event;
        public int EnemyId;
        public int ShooterIndex; // index among the enemy's AIBeamShooters (GetComponentsInChildren order)
        public float Angle;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Event);
            writer.Write(EnemyId);
            writer.Write(ShooterIndex);
            writer.Write(Angle);
        }

        public void Deserialize(BinaryReader reader)
        {
            Event = (BeamEvent)reader.ReadByte();
            EnemyId = reader.ReadInt32();
            ShooterIndex = reader.ReadInt32();
            Angle = reader.ReadSingle();
        }
    }
}
