using System.Collections.Generic;
using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: every answer the host's save file gave while it generated a floor, so the
    /// client can build the identical floor from its own, different save. See GenerationReplicator.
    /// </summary>
    public class GenerationDecisionsPacket : INetworkPacket
    {
        public PacketType Type => PacketType.GenerationDecisions;

        public class Window
        {
            public byte Kind;                          // GenerationReplicator.WindowKind
            public List<bool> Answers = new List<bool>();
        }

        public string SceneName;
        // Plain GameStatsManager fields read during generation (fields can't be patched, so they're copied).
        public int NumberRunsValidCellWithoutSpawn;
        public bool IsChump;
        public List<Window> Windows = new List<Window>();

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(SceneName ?? "");
            writer.Write(NumberRunsValidCellWithoutSpawn);
            writer.Write(IsChump);
            writer.Write(Windows.Count);
            foreach (Window window in Windows)
            {
                writer.Write(window.Kind);
                writer.Write(window.Answers.Count);
                foreach (bool answer in window.Answers) writer.Write(answer);
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            SceneName = reader.ReadString();
            NumberRunsValidCellWithoutSpawn = reader.ReadInt32();
            IsChump = reader.ReadBoolean();
            int windowCount = reader.ReadInt32();
            Windows = new List<Window>(windowCount);
            for (int w = 0; w < windowCount; w++)
            {
                var window = new Window { Kind = reader.ReadByte() };
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++) window.Answers.Add(reader.ReadBoolean());
                Windows.Add(window);
            }
        }
    }
}
