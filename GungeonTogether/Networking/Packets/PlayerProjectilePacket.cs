using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// A projectile a player's gun fired, so the other side can show a harmless copy. Sent by the
    /// shooter (client → host, host → all); the host relays client shots, stamping PlayerId.
    /// The prefab is found again as "the projectile named ProjectileName on gun GunId".
    /// </summary>
    public class PlayerProjectilePacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerProjectile;

        public ulong PlayerId;
        public int GunId;
        public string ProjectileName;
        public Vector2 Position;
        public float Direction; // degrees
        public float Speed;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(GunId);
            writer.Write(ProjectileName ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Direction);
            writer.Write(Speed);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            GunId = reader.ReadInt32();
            ProjectileName = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = reader.ReadSingle();
            Speed = reader.ReadSingle();
        }
    }
}
