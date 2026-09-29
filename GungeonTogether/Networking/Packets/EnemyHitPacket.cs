using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → one client: an enemy attack that damages its target directly rather than through a
    /// bullet (Gatling Gull's leap and melee, the Tarnisher's grab) hit that client's stand-in. The
    /// client applies it to its own player.
    /// </summary>
    public class EnemyHitPacket : INetworkPacket
    {
        public enum HitKind : byte
        {
            Damage = 0,   // Damage + Knockback along Direction
            Grab = 1,     // swallowed: hidden, no input, invulnerable until Release
            Release = 2   // spat out; Damage > 0 means punished (the full grab landed)
        }

        public PacketType Type => PacketType.EnemyHit;

        public HitKind Kind;
        public float Damage;
        public Vector2 Direction;
        public float Knockback;
        public string Source = ""; // the enemy's name, for the death screen
        public float ClipPenalty;  // Release when punished: the Tarnisher shrinks the clip (PlayerClipSizePenalty)

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Kind);
            writer.Write(Damage);
            writer.Write(Direction.x);
            writer.Write(Direction.y);
            writer.Write(Knockback);
            writer.Write(Source ?? "");
            writer.Write(ClipPenalty);
        }

        public void Deserialize(BinaryReader reader)
        {
            Kind = (HitKind)reader.ReadByte();
            Damage = reader.ReadSingle();
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Knockback = reader.ReadSingle();
            Source = reader.ReadString();
            ClipPenalty = reader.ReadSingle();
        }
    }
}
