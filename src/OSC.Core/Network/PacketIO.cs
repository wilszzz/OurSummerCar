using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace OSC.Core.Network
{
    public class PacketWriter : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly BinaryWriter writer;

        public PacketType Type { get; }

        public PacketWriter(PacketType type)
        {
            Type = type;
            stream = new MemoryStream();
            writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write((byte)type);
        }

        public void WriteByte(byte value) => writer.Write(value);
        public void WriteInt32(int value) => writer.Write(value);
        public void WriteInt64(long value) => writer.Write(value);
        public void WriteSingle(float value) => writer.Write(value);
        public void WriteString(string value) => writer.Write(value ?? string.Empty);
        public void WriteBoolean(bool value) => writer.Write(value);

        public void WriteVector3(Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        public void WriteQuaternion(Quaternion q)
        {
            writer.Write(q.x);
            writer.Write(q.y);
            writer.Write(q.z);
            writer.Write(q.w);
        }

        public void WriteByteArray(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                writer.Write(0);
            }
            else
            {
                writer.Write(buffer.Length);
                writer.Write(buffer);
            }
        }

        public byte[] GetBytes()
        {
            writer.Flush();
            return stream.ToArray();
        }

        public void Dispose()
        {
            try { writer.Close(); } catch { }
            try { stream.Dispose(); } catch { }
        }
    }

    public class PacketReader : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly BinaryReader reader;

        public PacketType Type { get; }

        public PacketReader(byte[] data)
        {
            stream = new MemoryStream(data);
            reader = new BinaryReader(stream, Encoding.UTF8);
            Type = (PacketType)reader.ReadByte();
        }

        public byte ReadByte() => reader.ReadByte();
        public int ReadInt32() => reader.ReadInt32();
        public long ReadInt64() => reader.ReadInt64();
        public float ReadSingle() => reader.ReadSingle();
        public string ReadString() => reader.ReadString();
        public bool ReadBoolean() => reader.ReadBoolean();

        public Vector3 ReadVector3()
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public Quaternion ReadQuaternion()
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public byte[] ReadByteArray()
        {
            int len = reader.ReadInt32();
            if (len <= 0) return new byte[0];
            return reader.ReadBytes(len);
        }

        public int UnreadBytes => (int)(reader.BaseStream.Length - reader.BaseStream.Position);
        public int RemainingBytes => UnreadBytes;

        public void Dispose()
        {
            try { reader.Close(); } catch { }
            try { stream.Dispose(); } catch { }
        }
    }
}
