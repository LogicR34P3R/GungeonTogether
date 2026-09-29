using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public enum EnemyProjectileKind : byte
    {
        Bank = 0, // from the enemy's AIBulletBank (BulletScripts and direct bank shots) - BankName says which bullet
        Gun = 1,  // from the enemy's held gun (AIShooter volleys)
        // Gunjurer spin attack (WizardSpinShootBehavior): a bullet starts circling the caster -
        // Position = circle centre relative to the enemy, Direction = its angle on the circle,
        // Speed = degrees per second, Radius = circle radius.
        SpinHold = 2,
        // ...and is sent flying (Position/Direction/Speed as usual): the client launches the
        // circling copy nearest Position instead of spawning a new one.
        SpinRelease = 3
    }

    /// <summary>
    /// Host → client: a synced enemy fired a bullet. Sent unreliably - at bullet-hell rates a lost
    /// bullet is better than a late one. The client fires the same bullet from the enemy's puppet in
    /// a straight line (scripted curves/homing aren't reproduced - option A of the step-4 plan).
    /// </summary>
    public class EnemyProjectilePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyProjectile;

        public int EnemyId;
        public EnemyProjectileKind Kind;
        public string BankName;
        public Vector2 Position;
        public float Direction; // degrees
        public float Speed;     // units per second
        public float Radius;    // SpinHold only

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write((byte)Kind);
            writer.Write(BankName ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Direction);
            writer.Write(Speed);
            writer.Write(Radius);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Kind = (EnemyProjectileKind)reader.ReadByte();
            BankName = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = reader.ReadSingle();
            Speed = reader.ReadSingle();
            Radius = reader.ReadSingle();
        }
    }
}
