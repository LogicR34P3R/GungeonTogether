using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// The sender bought (or stole) this shop item; the receiver marks its matching slot sold.
    /// Matched by position plus item id - stock is seeded, but the game can exclude items a player
    /// already owns, so the same slot could hold something else on the other side.
    /// </summary>
    public class ShopItemSoldPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ShopItemSold;

        public Vector2 Position;
        public int PickupId;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(PickupId);
        }

        public void Deserialize(BinaryReader reader)
        {
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            PickupId = reader.ReadInt32();
        }
    }
}
