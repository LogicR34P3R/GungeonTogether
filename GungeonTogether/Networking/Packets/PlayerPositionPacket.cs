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
        // Body sprite position relative to Position. Flipping to aim left shifts the sprite by its
        // width, so the avatar needs the live offset, not the prefab's.
        public Vector2 SpriteOffset;
        public float SendTime; // sender's Time.realtimeSinceStartup - lets the receiver interpolate on the sender's timeline

        // The held gun, drawn on the avatar - some guns show their whole attack on the gun sprite
        // (e.g. the Blasphemy's sword swing), which no projectile copy can stand in for.
        public int GunId = -1;      // PickupObjectId, -1 for none
        public int GunSpriteId;     // current frame, in the gun's own collection
        public Vector2 GunOffset;   // gun sprite position relative to Position
        public float GunAngle;
        public bool GunFlipY;
        public float GunHeight;     // HeightOffGround: the game moves the gun behind the player when aiming up
        public bool GunVisible;

        // Which level the sender is on (hash of WorldStateReplicator.CurrentSceneName). An avatar only
        // exists while both are on the same level: otherwise its position means nothing here.
        public int SceneHash;

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
            writer.Write(GunId);
            writer.Write(GunSpriteId);
            writer.Write(GunOffset.x);
            writer.Write(GunOffset.y);
            writer.Write(GunAngle);
            writer.Write(GunFlipY);
            writer.Write(GunHeight);
            writer.Write(GunVisible);
            writer.Write(SceneHash);
            writer.Write(SpriteOffset.x);
            writer.Write(SpriteOffset.y);
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
            GunId = reader.ReadInt32();
            GunSpriteId = reader.ReadInt32();
            GunOffset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            GunAngle = reader.ReadSingle();
            GunFlipY = reader.ReadBoolean();
            GunHeight = reader.ReadSingle();
            GunVisible = reader.ReadBoolean();
            SceneHash = reader.ReadInt32();
            SpriteOffset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
