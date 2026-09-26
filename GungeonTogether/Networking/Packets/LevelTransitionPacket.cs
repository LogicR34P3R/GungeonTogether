using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: the host has started loading this level (by scene name). Sent at the start
    /// of the host's load rather than the end, so the client generates the same floor in parallel.
    /// </summary>
    public class LevelTransitionPacket : INetworkPacket
    {
        public PacketType Type => PacketType.LevelTransition;

        public string SceneName;

        public void Serialize(BinaryWriter writer) => writer.Write(SceneName ?? "");
        public void Deserialize(BinaryReader reader) => SceneName = reader.ReadString();
    }
}
