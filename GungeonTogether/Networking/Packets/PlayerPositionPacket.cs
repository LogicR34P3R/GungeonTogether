using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;

namespace GungeonTogether.Networking.Packets
{
    public class PlayerPositionPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerPosition;

        public ulong PlayerId;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Rotation;
        public bool IsGrounded;
        public bool IsDodgeRolling;
        public int CharacterId; // (int)PlayableCharacters of the sender
        public int SpriteId;    // sender's current tk2d sprite frame, in its character's collection
        public bool FlipX;
        public float SendTime; // sender's Time.realtimeSinceStartup - lets the receiver interpolate on the sender's timeline

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Velocity.x);
            writer.Write(Velocity.y);
            writer.Write(Rotation);
            writer.Write(IsGrounded);
            writer.Write(IsDodgeRolling);
            writer.Write(CharacterId);
            writer.Write(SpriteId);
            writer.Write(FlipX);
            writer.Write(SendTime);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            IsGrounded = reader.ReadBoolean();
            IsDodgeRolling = reader.ReadBoolean();
            CharacterId = reader.ReadInt32();
            SpriteId = reader.ReadInt32();
            FlipX = reader.ReadBoolean();
            SendTime = reader.ReadSingle();
        }
    }
}
