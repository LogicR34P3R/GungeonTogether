using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// A pickup landed on the sender's side; the receiver spawns a mirror of it. (OwnerId, LocalId)
    /// identifies the item on every machine - OwnerId is the SteamID of whoever spawned it.
    /// </summary>
    public class LootSpawnPacket : INetworkPacket
    {
        public PacketType Type => PacketType.LootSpawn;

        public ulong OwnerId;
        public int LocalId;
        public int PickupId; // PickupObject.PickupObjectId - resolved via PickupObjectDatabase.GetById
        public Vector2 Position;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(OwnerId);
            writer.Write(LocalId);
            writer.Write(PickupId);
            writer.Write(Position.x);
            writer.Write(Position.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            OwnerId = reader.ReadUInt64();
            LocalId = reader.ReadInt32();
            PickupId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
