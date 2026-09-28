using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Both directions: a player flipped a table or broke something (pot, crate, barrel, table).
    /// Matched on the other side by position - the shared seed places room props identically. The
    /// host relays a client's.
    /// </summary>
    public class RoomObjectPacket : INetworkPacket
    {
        public enum ObjectEvent : byte
        {
            TableFlipped = 0,
            MinorBroken = 1,  // MinorBreakable: pots, crates, barrels
            MajorBroken = 2,  // MajorBreakable: tables and other sturdy props
            ObjectMoved = 3,  // something moved (pushed, kicked, ridden): Position is where it started (its identity), MovedTo where it is
            CartSpawned = 4   // host only: a MineCartFactory at Position made cart number Serial
        }

        public PacketType Type => PacketType.RoomObject;

        public ObjectEvent Event;
        public Vector2 Position;   // the object's transform position
        public int FlipDirection;  // (int)DungeonData.Direction, for TableFlipped
        public Vector2 BreakDirection;
        public Vector2 MovedTo;
        public byte MovableKind; // RoomObjectReplicator.MovableKind, for ObjectMoved
        public int Serial;       // 0 for objects placed with the level; n for a MineCartFactory's nth cart
        public string Clip = ""; // its animation now (a kicked barrel rolling, a cart's wheels), for ObjectMoved

        public void Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Event);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(FlipDirection);
            writer.Write(BreakDirection.x);
            writer.Write(BreakDirection.y);
            writer.Write(MovedTo.x);
            writer.Write(MovedTo.y);
            writer.Write(MovableKind);
            writer.Write(Serial);
            writer.Write(Clip ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            Event = (ObjectEvent)reader.ReadByte();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            FlipDirection = reader.ReadInt32();
            BreakDirection = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            MovedTo = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            MovableKind = reader.ReadByte();
            Serial = reader.ReadInt32();
            Clip = reader.ReadString();
        }
    }
}
