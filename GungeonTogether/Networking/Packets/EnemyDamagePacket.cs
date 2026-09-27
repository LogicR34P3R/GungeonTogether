using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Client → host: the client hit a puppet; the host applies it to the real enemy. One packet per
    /// enemy per damage category per frame - hits within a frame are summed.
    /// </summary>
    public class EnemyDamagePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyDamage;

        public int EnemyId;
        public float Damage;
        public Vector2 Direction;
        public int DamageTypes; // CoreDamageTypes flags
        public byte Category;   // DamageCategory

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Damage);
            writer.Write(Direction.x);
            writer.Write(Direction.y);
            writer.Write(DamageTypes);
            writer.Write(Category);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Damage = reader.ReadSingle();
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            DamageTypes = reader.ReadInt32();
            Category = reader.ReadByte();
        }
    }
}
