using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KK2Blender.Format
{
    /// <summary>
    /// A set of per-frame channels, written to one ".k2t" track file (see docs/bake-format.md).
    ///
    /// A channel is a named stream of fixed-width float32 records, one per frame, from the frame it
    /// first appeared on. Frames where the source didn't exist (an outfit object that was destroyed
    /// and rebuilt) hold NaN. A channel whose every record is identical is stored once and flagged
    /// "constant" on write.
    /// </summary>
    public sealed class TrackSet
    {
        public const string Magic = "K2T1";

        private readonly Dictionary<string, Channel> byName = new Dictionary<string, Channel>();
        private readonly List<Channel> order = new List<Channel>();

        public int FrameCount { get; private set; }

        public IList<Channel> Channels { get { return order; } }

        /// <summary>Starts frame <paramref name="frame"/>. Frames must be begun in order, from 0.</summary>
        public void BeginFrame(int frame)
        {
            if (frame != FrameCount)
            {
                throw new InvalidOperationException("Frames must be recorded in order: expected " + FrameCount + ", got " + frame + ".");
            }
            FrameCount = frame + 1;
        }

        /// <summary>The channel with this name, created on first use with the given kind and width.</summary>
        public Channel Get(string name, string kind, int width)
        {
            Channel ch;
            if (byName.TryGetValue(name, out ch))
            {
                if (ch.Width != width)
                {
                    throw new InvalidOperationException("Channel '" + name + "' was created with width " + ch.Width + ", not " + width + ".");
                }
                return ch;
            }
            ch = new Channel(name, kind, width, FrameCount - 1);
            byName.Add(name, ch);
            order.Add(ch);
            return ch;
        }

        public bool TryGet(string name, out Channel channel)
        {
            return byName.TryGetValue(name, out channel);
        }

        /// <summary>Writes the record of the current frame.</summary>
        public void Put(string name, string kind, float[] record)
        {
            Get(name, kind, record.Length).Put(FrameCount - 1, record);
        }

        public void Put(string name, string kind, float value)
        {
            Get(name, kind, 1).Put(FrameCount - 1, value);
        }

        /// <summary>Writes the file: magic, u32 header length, UTF-8 JSON header, then each channel's float32 data.</summary>
        public void Save(Stream stream, JsonObject extraHeader = null)
        {
            var channels = new List<object>();
            long offset = 0;
            var payloads = new List<float[]>();
            foreach (var ch in order)
            {
                ch.PadTo(FrameCount);
                bool constant = ch.IsConstant();
                int count = constant ? 1 : ch.Count;
                var data = new float[count * ch.Width];
                Array.Copy(ch.Data, data, data.Length);
                var info = new JsonObject()
                    .Set("name", ch.Name)
                    .Set("kind", ch.Kind)
                    .Set("width", ch.Width)
                    .Set("start", ch.Start)
                    .Set("count", ch.Count)
                    .Set("constant", constant)
                    .Set("offset", offset);
                channels.Add(info);
                payloads.Add(data);
                offset += data.Length * 4L;
            }

            var header = extraHeader ?? new JsonObject();
            header.Set("frame_count", FrameCount);
            header.Set("channels", channels);
            byte[] headerBytes = Encoding.UTF8.GetBytes(JsonWriter.Write(header, false));
            // Pad the header so the float data starts on a 4-byte boundary (numpy reads it in place).
            int pad = (4 - (headerBytes.Length % 4)) % 4;

            var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write((uint)(headerBytes.Length + pad));
            w.Write(headerBytes);
            for (int i = 0; i < pad; i++)
            {
                w.Write((byte)' ');
            }
            var buffer = new byte[64 * 1024];
            foreach (var data in payloads)
            {
                int done = 0;
                while (done < data.Length)
                {
                    int n = Math.Min(buffer.Length / 4, data.Length - done);
                    Buffer.BlockCopy(data, done * 4, buffer, 0, n * 4);
                    if (!BitConverter.IsLittleEndian)
                    {
                        throw new NotSupportedException("Big-endian hosts are not supported.");
                    }
                    w.Write(buffer, 0, n * 4);
                    done += n;
                }
            }
            w.Flush();
        }

        public void Save(string path, JsonObject extraHeader = null)
        {
            using (var f = File.Create(path))
            {
                Save(f, extraHeader);
            }
        }
    }

    public sealed class Channel
    {
        private float[] data;

        public Channel(string name, string kind, int width, int start)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException("width");
            }
            Name = name;
            Kind = kind;
            Width = width;
            Start = Math.Max(0, start);
            data = new float[width * 64];
        }

        public string Name { get; private set; }
        public string Kind { get; private set; }
        public int Width { get; private set; }

        /// <summary>The first frame this channel has a record for.</summary>
        public int Start { get; private set; }

        /// <summary>How many records it holds, from Start.</summary>
        public int Count { get; private set; }

        internal float[] Data { get { return data; } }

        private void Ensure(int records)
        {
            int need = records * Width;
            if (need <= data.Length)
            {
                return;
            }
            int size = data.Length;
            while (size < need)
            {
                size *= 2;
            }
            var bigger = new float[size];
            Array.Copy(data, bigger, Count * Width);
            data = bigger;
        }

        /// <summary>Fills NaN records up to (not including) frame-relative index.</summary>
        public void PadTo(int frameEnd)
        {
            int records = frameEnd - Start;
            if (records <= Count)
            {
                return;
            }
            Ensure(records);
            for (int i = Count * Width; i < records * Width; i++)
            {
                data[i] = float.NaN;
            }
            Count = records;
        }

        public void Put(int frame, float[] record)
        {
            if (record.Length != Width)
            {
                throw new ArgumentException("Record width " + record.Length + " doesn't match channel '" + Name + "' width " + Width + ".");
            }
            int index = frame - Start;
            if (index < 0)
            {
                throw new ArgumentException("Frame " + frame + " is before channel '" + Name + "' started (" + Start + ").");
            }
            if (index < Count)
            {
                throw new ArgumentException("Channel '" + Name + "' already has frame " + frame + ".");
            }
            PadTo(frame);
            Ensure(index + 1);
            Array.Copy(record, 0, data, index * Width, Width);
            Count = index + 1;
        }

        public void Put(int frame, float value)
        {
            Put(frame, new[] { value });
        }

        /// <summary>True when every record equals the first, bit for bit (NaN equal to NaN).</summary>
        public bool IsConstant()
        {
            for (int r = 1; r < Count; r++)
            {
                for (int c = 0; c < Width; c++)
                {
                    float a = data[c];
                    float b = data[r * Width + c];
                    if (a != b && !(float.IsNaN(a) && float.IsNaN(b)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public float At(int frame, int component)
        {
            int index = frame - Start;
            if (index < 0 || index >= Count)
            {
                return float.NaN;
            }
            return data[index * Width + component];
        }
    }
}
