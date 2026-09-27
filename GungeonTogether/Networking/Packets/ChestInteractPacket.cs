using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    // Chests are identified by world position: the shared seed places pre-placed chests at the same
    // spots on both sides, and runtime-spawned ones are mirrored at the host's exact position.

    /// <summary>Client → host: the client tried to open this chest; the host opens its own copy.</summary>
    public class ChestInteractPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ChestInteract;

        public Vector2 Position;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Position.x);
            writer.Write(Position.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
