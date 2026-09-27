using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    // Chests are identified by world position: the shared seed places pre-placed chests at the same
    // spots on both sides, and runtime-spawned ones are mirrored at the host's exact position.

    /// <summary>Host → client: a chest appeared mid-level (e.g. a room-clear reward chest).</summary>
    public class ChestSpawnPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ChestSpawn;

        public string PrefabName; // matched against RewardManager's chest prefab fields
        public Vector2 Position;
        public bool Locked;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PrefabName ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Locked);
        }

        public void Deserialize(BinaryReader reader)
        {
            PrefabName = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Locked = reader.ReadBoolean();
        }
    }
}
