using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: an enemy started a BulletScript attack. The client runs the same script from its
    /// puppet, with the same seed for the script's randomness, so the pattern matches.
    /// </summary>
    public class BossScriptStartPacket : INetworkPacket
    {
        public PacketType Type => PacketType.BossScriptStart;

        public int EnemyId;
        public int ScriptId;          // host-assigned, so a later BossScriptStop can name it
        public string ScriptTypeName; // BulletScriptSelector.scriptTypeName
        public Vector2 Offset;        // script source position relative to the enemy
        public float Rotation;        // script source rotation (degrees)
        public int Seed;
        // Who the enemy is aiming at when it starts (see EnemyState.TargetId). Scripts read the aim
        // on their first tick, before the next state packet could set it on the puppet.
        public ulong TargetId;
        // The bullet bank the script fires from, as a path below the enemy root ("" for the root
        // itself): some enemies keep banks on child objects.
        public string BankPath;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(ScriptId);
            writer.Write(ScriptTypeName ?? "");
            writer.Write(Offset.x);
            writer.Write(Offset.y);
            writer.Write(Rotation);
            writer.Write(Seed);
            writer.Write(TargetId);
            writer.Write(BankPath ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            ScriptId = reader.ReadInt32();
            ScriptTypeName = reader.ReadString();
            Offset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            Seed = reader.ReadInt32();
            TargetId = reader.ReadUInt64();
            BankPath = reader.ReadString();
        }
    }
}
