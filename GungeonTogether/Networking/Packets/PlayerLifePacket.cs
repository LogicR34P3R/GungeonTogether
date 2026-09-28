using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Both directions: a player died, became a ghost, or was revived. Sent reliably on every change;
    /// the host relays a client's, stamped with the sender's id.
    /// </summary>
    public class PlayerLifePacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerLife;

        public ulong PlayerId;
        public PlayerLifeState State;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write((byte)State);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            State = (PlayerLifeState)reader.ReadByte();
        }
    }
}
