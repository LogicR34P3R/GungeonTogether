using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// A reward pedestal (the boss's item, a master round). Host → client: one appeared with this
    /// item (Spawn), or is gone (Gone: a mimic woke up). Both ways, relayed by the host: someone
    /// took its item (Taken). Pedestals are identified by position.
    /// </summary>
    public class PedestalPacket : INetworkPacket
    {
        public enum PedestalEvent : byte
        {
            Spawn = 0,
            Taken = 1,
            Gone = 2
        }

        public PacketType Type => PacketType.Pedestal;

        public PedestalEvent Event;
        public Vector2 Position;
        public int PickupId = -1;      // Spawn: the item on it
        public string PrefabName = ""; // Spawn: the pedestal prefab
        public bool IsMimic;           // Spawn: taking it wakes a mimic on the host instead

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Event);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(PickupId);
            writer.Write(PrefabName ?? "");
            writer.Write(IsMimic);
        }

        public void Deserialize(BinaryReader reader)
        {
            Event = (PedestalEvent)reader.ReadByte();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            PickupId = reader.ReadInt32();
            PrefabName = reader.ReadString();
            IsMimic = reader.ReadBoolean();
        }
    }
}
