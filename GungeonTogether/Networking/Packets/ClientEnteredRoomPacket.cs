using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Client → host: the client walked into another room (by room name). If it's a combat room the
    /// host hasn't started, the host warps in so the fight runs on the host, which owns the enemies.
    /// </summary>
    public class ClientEnteredRoomPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ClientEnteredRoom;

        public string RoomName;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(RoomName ?? "");
        }

        public void Deserialize(BinaryReader reader)
        {
            RoomName = reader.ReadString();
        }
    }
}
