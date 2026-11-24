using System;
using System.IO;
using System.Text;

namespace OurSummerCar.Network
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
        public void WriteBytes(byte[] buffer)
        {
            if (buffer != null && buffer.Length > 0)
            {
                writer.Write(buffer);
            }
        }

        public void WriteByteArray(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                writer.Write((int)0);
            }
            else
            {
                writer.Write((int)buffer.Length);
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
            writer.Close();
            stream.Dispose();
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
        public byte[] ReadBytes(int count)
        {
            if (count <= 0) return new byte[0];
            return reader.ReadBytes(count);
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
