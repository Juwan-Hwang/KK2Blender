using System;
using System.IO;
using System.Text;
using KK2Blender.Format;
using Xunit;

namespace KK2Blender.Tests
{
    public class TrackSetTests
    {
        private static (string header, byte[] data) Split(byte[] file)
        {
            Assert.Equal("K2T1", Encoding.ASCII.GetString(file, 0, 4));
            int len = BitConverter.ToInt32(file, 4);
            string header = Encoding.UTF8.GetString(file, 8, len);
            var data = new byte[file.Length - 8 - len];
            Array.Copy(file, 8 + len, data, 0, data.Length);
            return (header, data);
        }

        [Fact]
        public void ConstantChannelsAreStoredOnce()
        {
            var t = new TrackSet();
            for (int f = 0; f < 5; f++)
            {
                t.BeginFrame(f);
                t.Put("still", "x", new[] { 1f, 2f });
                t.Put("moving", "x", (float)f);
            }
            var ms = new MemoryStream();
            t.Save(ms);
            var (header, data) = Split(ms.ToArray());
            Assert.Contains("\"name\":\"still\",\"kind\":\"x\",\"width\":2,\"start\":0,\"count\":5,\"constant\":true,\"offset\":0", header);
            Assert.Contains("\"name\":\"moving\",\"kind\":\"x\",\"width\":1,\"start\":0,\"count\":5,\"constant\":false,\"offset\":8", header);
            Assert.Equal(8 + 5 * 4, data.Length);
            Assert.Equal(4f, BitConverter.ToSingle(data, 8 + 16));
        }

        [Fact]
        public void LateAndMissingFramesArePaddedWithNaN()
        {
            var t = new TrackSet();
            for (int f = 0; f < 6; f++)
            {
                t.BeginFrame(f);
                if (f == 2 || f == 5)
                {
                    t.Put("late", "x", (float)f);
                }
            }
            var ch = t.Channels[0];
            Assert.Equal(2, ch.Start);
            ch.PadTo(t.FrameCount);
            Assert.Equal(4, ch.Count);
            Assert.Equal(2f, ch.At(2, 0));
            Assert.True(float.IsNaN(ch.At(3, 0)));
            Assert.True(float.IsNaN(ch.At(4, 0)));
            Assert.Equal(5f, ch.At(5, 0));
            Assert.True(float.IsNaN(ch.At(0, 0)));
        }

        [Fact]
        public void TrailingFramesArePaddedOnSave()
        {
            var t = new TrackSet();
            for (int f = 0; f < 4; f++)
            {
                t.BeginFrame(f);
                if (f < 2)
                {
                    t.Put("gone", "x", (float)f);
                }
            }
            var ms = new MemoryStream();
            t.Save(ms);
            var (header, data) = Split(ms.ToArray());
            Assert.Contains("\"count\":4", header);
            Assert.True(float.IsNaN(BitConverter.ToSingle(data, 12)));
        }

        [Fact]
        public void HeaderIsPaddedToFourBytes()
        {
            var t = new TrackSet();
            t.BeginFrame(0);
            t.Put("a", "x", 1f);
            for (int extra = 0; extra < 4; extra++)
            {
                var ms = new MemoryStream();
                t.Save(ms, new JsonObject().Set("pad", new string('p', extra)));
                Assert.Equal(0, BitConverter.ToInt32(ms.ToArray(), 4) % 4);
            }
        }

        [Fact]
        public void FramesMustBeInOrder()
        {
            var t = new TrackSet();
            t.BeginFrame(0);
            Assert.Throws<InvalidOperationException>(() => t.BeginFrame(2));
        }

        [Fact]
        public void ARecordCantBeWrittenTwice()
        {
            var t = new TrackSet();
            t.BeginFrame(0);
            t.Put("a", "x", 1f);
            Assert.Throws<ArgumentException>(() => t.Put("a", "x", 2f));
        }

        [Fact]
        public void WidthMustMatch()
        {
            var t = new TrackSet();
            t.BeginFrame(0);
            t.Put("a", "x", new[] { 1f, 2f });
            t.BeginFrame(1);
            Assert.Throws<InvalidOperationException>(() => t.Put("a", "x", new[] { 1f }));
        }

        /// <summary>Writes a sample file for the Python reader's cross-check (tests/python).</summary>
        [Fact]
        public void WritesTheCrossCheckSample()
        {
            string dir = Environment.GetEnvironmentVariable("K2T_SAMPLE_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            Directory.CreateDirectory(dir);
            var t = new TrackSet();
            for (int f = 0; f < 3; f++)
            {
                t.BeginFrame(f);
                t.Put("bone:a/b", "matrix", new float[] { 1, 0, 0, f, 0, 1, 0, 0, 0, 0, 1, 0 });
                t.Put("still", "bool", 1f);
                if (f >= 1)
                {
                    t.Put("late", "x", new float[] { f, -f });
                }
            }
            t.Save(Path.Combine(dir, "sample.k2t"), new JsonObject().Set("subject", "test").Set("text", "ü \"quoted\""));
        }
    }

    public class JsonWriterTests
    {
        [Fact]
        public void WritesNestedValuesInOrder()
        {
            var o = new JsonObject().Set("b", 1).Set("a", new object[] { 1.5f, true, null, "x" }).Set("c", new JsonObject().Set("d", float.NaN));
            Assert.Equal("{\"b\":1,\"a\":[1.5,true,null,\"x\"],\"c\":{\"d\":null}}", JsonWriter.Write(o, false));
        }

        [Fact]
        public void EscapesStrings()
        {
            Assert.Equal("\"a\\\"b\\\\c\\n\\u0001\"", JsonWriter.Write("a\"b\\c\n\u0001", false));
        }

        [Fact]
        public void UsesInvariantCulture()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("[0.25]", JsonWriter.Write(new[] { 0.25f }, false));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
