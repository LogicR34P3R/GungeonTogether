using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: the host's Dungeon.FloorCleared() ran (its floor boss died). The client never
    /// kills a boss itself - it fights the host's puppet - so it runs its floor clear on this.
    /// </summary>
    public class FloorClearedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.FloorCleared;

        public void Serialize(BinaryWriter writer) { }
        public void Deserialize(BinaryReader reader) { }
    }
}
