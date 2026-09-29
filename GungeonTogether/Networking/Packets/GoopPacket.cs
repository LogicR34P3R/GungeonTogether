using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    /// <summary>
    /// Host → client: an enemy put down goop (a trail, a spew, a puddle). The client adds the same
    /// goop, found by its GoopDefinition's name.
    /// </summary>
    public class GoopPacket : INetworkPacket
    {
        public enum GoopShape : byte
        {
            Circle = 0,      // A = centre, Radius
            TimedCircle = 1, // A = centre, Radius, Duration
            Line = 2,        // A to B, Radius
            TimedLine = 3,   // A to B, Radius, Duration
            TimedArc = 4     // A = origin, B = direction, Radius, Arc, Duration
        }

        public PacketType Type => PacketType.Goop;

        public string GoopName = "";
        public GoopShape Shape;
        public Vector2 A;
        public Vector2 B;
        public float Radius;
        public float Arc;
        public float Duration;
        public bool SuppressSplashes;
        // TimedArc: how the goop spreads over the arc (the attack's goopCurve); null for the default.
        public Keyframe[] Curve;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(GoopName ?? "");
            writer.Write((byte)Shape);
            writer.Write(A.x);
            writer.Write(A.y);
            writer.Write(B.x);
            writer.Write(B.y);
            writer.Write(Radius);
            writer.Write(Arc);
            writer.Write(Duration);
            writer.Write(SuppressSplashes);
            int keys = Curve != null ? Curve.Length : 0;
            writer.Write(keys);
            for (int i = 0; i < keys; i++)
            {
                writer.Write(Curve[i].time);
                writer.Write(Curve[i].value);
                writer.Write(Curve[i].inTangent);
                writer.Write(Curve[i].outTangent);
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            GoopName = reader.ReadString();
            Shape = (GoopShape)reader.ReadByte();
            A = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            B = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Radius = reader.ReadSingle();
            Arc = reader.ReadSingle();
            Duration = reader.ReadSingle();
            SuppressSplashes = reader.ReadBoolean();
            int keys = reader.ReadInt32();
            if (keys < 0 || keys > 64) throw new InvalidDataException("Goop curve key count " + keys);
            Curve = keys > 0 ? new Keyframe[keys] : null;
            for (int i = 0; i < keys; i++)
            {
                Curve[i] = new Keyframe(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }
        }
    }
}
