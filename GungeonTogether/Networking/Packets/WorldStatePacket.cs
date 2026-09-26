using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class WorldStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.WorldState;

        public bool IsFoyer;
        // Level identity by scene name (e.g. "tt_castle", "tt_sewer"), not floor index: ETG's
        // CurrentFloor is -1 for every secret floor, so an index can't tell them apart.
        public string SceneName;
        public string RoomIdentifier;   // room name or unique ID
        public Vector2 Position;
        public float Rotation;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(IsFoyer);
            writer.Write(SceneName ?? "");
            writer.Write(RoomIdentifier ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
        }

        public void Deserialize(BinaryReader reader)
        {
            IsFoyer = reader.ReadBoolean();
            SceneName = reader.ReadString();
            RoomIdentifier = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
        }
    }
}
