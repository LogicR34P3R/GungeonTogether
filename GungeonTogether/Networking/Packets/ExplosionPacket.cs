using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: an explosion went off (an enemy's death blast, a Gatling Gull rocket, a barrel
    /// the host broke, ...). The client plays one at the same spot that only hurts its own player.
    /// </summary>
    public class ExplosionPacket : INetworkPacket
    {
        public PacketType Type => PacketType.Explosion;

        public Vector2 Position;
        public float DamageRadius;
        public float DamageToPlayer;   // 0 when it can't hurt players (e.g. a player's own explosive)
        public float PushRadius;
        public float Force;            // 0: no push
        public bool DestroyProjectiles;
        public bool ScreenShake;
        public string EffectName = ""; // the explosion's effect prefab; "" for the default look
        public bool ExplosionRing = true;
        public bool DefaultSfx = true;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(DamageRadius);
            writer.Write(DamageToPlayer);
            writer.Write(PushRadius);
            writer.Write(Force);
            writer.Write(DestroyProjectiles);
            writer.Write(ScreenShake);
            writer.Write(EffectName ?? "");
            writer.Write(ExplosionRing);
            writer.Write(DefaultSfx);
        }

        public void Deserialize(BinaryReader reader)
        {
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            DamageRadius = reader.ReadSingle();
            DamageToPlayer = reader.ReadSingle();
            PushRadius = reader.ReadSingle();
            Force = reader.ReadSingle();
            DestroyProjectiles = reader.ReadBoolean();
            ScreenShake = reader.ReadBoolean();
            EffectName = reader.ReadString();
            ExplosionRing = reader.ReadBoolean();
            DefaultSfx = reader.ReadBoolean();
        }
    }
}
