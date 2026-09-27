using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>Host → client: the host cut this boss script short (ForceStop) - e.g. an interrupted attack.</summary>
    public class BossScriptStopPacket : INetworkPacket
    {
        public PacketType Type => PacketType.BossScriptStop;

        public int ScriptId;

        public void Serialize(BinaryWriter writer) => writer.Write(ScriptId);
        public void Deserialize(BinaryReader reader) => ScriptId = reader.ReadInt32();
    }
}
