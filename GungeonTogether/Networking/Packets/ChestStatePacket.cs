using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    // Chests are identified by world position: the shared seed places pre-placed chests at the same
    // spots on both sides, and runtime-spawned ones are mirrored at the host's exact position.

    public enum ChestState : byte
    {
        Opened = 0,
        Broken = 1,
        BecameMimic = 2
    }

    /// <summary>Host → client: a chest opened, broke, or turned out to be a mimic on the host.</summary>
    public class ChestStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.ChestState;

        public Vector2 Position;
        public ChestState State;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write((byte)State);
        }

        public void Deserialize(BinaryReader reader)
        {
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            State = (ChestState)reader.ReadByte();
        }
    }
}
