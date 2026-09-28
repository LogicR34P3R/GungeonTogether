using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;
using UnityEngine;

namespace GungeonTogether.Networking.Packets
{
    public class EnemyStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyState;

        public int EnemyId;
        public Vector2 Position;
        public float Rotation;
        public int Health;
        public int MaxHealth; // applied to boss puppets, so the client's boss health bar tracks the host's boss
        public int AIState;

        // The host enemy's own animation, played by the puppet (whose AI - and so its animation
        // choice - is off): attacks, charge-ups, death.
        public string Clip = "";   // tk2dSpriteAnimator clip name, "" for none
        public int Frame;
        public bool FlipX;
        public bool Dying;         // dead on the host, playing its death; the puppet stops taking hits
        public bool HasGun;
        public float GunAngle;     // where an armed enemy aims
        public ulong TargetId;     // steam id of the player it targets (0: none) - the puppet aims its scripts there

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
            writer.Write(Health);
            writer.Write(MaxHealth);
            writer.Write(AIState);
            writer.Write(Clip ?? "");
            writer.Write(Frame);
            writer.Write(FlipX);
            writer.Write(Dying);
            writer.Write(HasGun);
            writer.Write(GunAngle);
            writer.Write(TargetId);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            Health = reader.ReadInt32();
            MaxHealth = reader.ReadInt32();
            AIState = reader.ReadInt32();
            Clip = reader.ReadString();
            Frame = reader.ReadInt32();
            FlipX = reader.ReadBoolean();
            Dying = reader.ReadBoolean();
            HasGun = reader.ReadBoolean();
            GunAngle = reader.ReadSingle();
            TargetId = reader.ReadUInt64();
        }
    }
}