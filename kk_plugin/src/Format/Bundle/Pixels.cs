using System;
using System.IO;
using System.IO.Compression;

namespace KK2Blender.Format.Bundle
{
    /// <summary>Pixel work on plain RGBA8 buffers (rows bottom-up, as Unity reads them).</summary>
    public static class Pixels
    {
        /// <summary>
        /// Unity 5.6's packed normal maps (DXT5nm: X in alpha, Y in green) as a standard tangent-space
        /// normal map: RGB = (X, Y, Z) * 0.5 + 0.5, alpha 255. X is read from alpha alone, as KK's Unity
        /// reads it, so maps that also carry Y in red decode the same way.
        /// </summary>
        public static void UnpackNormals(byte[] rgba)
        {
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double x = rgba[i + 3] / 255.0 * 2 - 1;
                double y = rgba[i + 1] / 255.0 * 2 - 1;
                double zz = 1 - x * x - y * y;
                double z = zz > 0 ? Math.Sqrt(zz) : 0;
                rgba[i] = ToByte(x);
                rgba[i + 1] = ToByte(y);
                rgba[i + 2] = ToByte(z);
                rgba[i + 3] = 255;
            }
        }

        private static byte ToByte(double v)
        {
            double b = Math.Round((v * 0.5 + 0.5) * 255);
            return (byte)Math.Max(0, Math.Min(255, b));
        }

        /// <summary>
        /// Straight (not premultiplied) RGBA of a surface rendered twice, once over transparent black and
        /// once over opaque white. A blended shader (SrcAlpha, OneMinusSrcAlpha) leaves c·a over black and
        /// c·a + (1 − a) over white, so a = 1 − (white − black) and c = black / a; an opaque or alpha-tested
        /// pixel is the same on both (a = 1) and a pixel nothing drew is black and white (a = 0). Reading
        /// the render target's own alpha instead gives a² and darkened colours for blended shaders.
        /// The result is written into <paramref name="onBlack"/>.
        /// </summary>
        public static void Unblend(byte[] onBlack, byte[] onWhite)
        {
            for (int i = 0; i + 3 < onBlack.Length && i + 3 < onWhite.Length; i += 4)
            {
                int diff = 0;
                for (int c = 0; c < 3; c++)
                {
                    diff += Math.Max(0, onWhite[i + c] - onBlack[i + c]);
                }
                double a = 1.0 - diff / (3.0 * 255.0);
                if (a <= 0.5 / 255.0)
                {
                    onBlack[i] = onBlack[i + 1] = onBlack[i + 2] = onBlack[i + 3] = 0;
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    onBlack[i + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(onBlack[i + c] / a)));
                }
                onBlack[i + 3] = (byte)Math.Max(0, Math.Min(255, Math.Round(a * 255.0)));
            }
        }

        /// <summary>Paints <paramref name="top"/> over <paramref name="under"/> where top has any alpha.</summary>
        public static void Over(byte[] under, byte[] top)
        {
            for (int i = 0; i + 3 < under.Length && i + 3 < top.Length; i += 4)
            {
                int a = top[i + 3];
                if (a == 0)
                {
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    under[i + c] = (byte)((top[i + c] * a + under[i + c] * (255 - a) + 127) / 255);
                }
                under[i + 3] = (byte)Math.Max(under[i + 3], a);
            }
        }

        /// <summary>A PNG (8-bit RGBA) of a bottom-up RGBA buffer.</summary>
        public static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            var raw = new byte[(width * 4 + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int src = (height - 1 - y) * width * 4;
                int dst = y * (width * 4 + 1);
                raw[dst] = 0; // no filter
                Buffer.BlockCopy(rgba, src, raw, dst + 1, width * 4);
            }
            var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", Zlib(raw));
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        private static byte[] Zlib(byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
            {
                deflate.Write(data, 0, data.Length);
            }
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            var adler = new byte[4];
            WriteBigEndian(adler, 0, (b << 16) | a);
            ms.Write(adler, 0, 4);
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBigEndian(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(typeBytes, 0xFFFFFFFFu);
            crc = Crc(data, crc) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            WriteBigEndian(c, 0, crc);
            s.Write(c, 0, 4);
        }

        private static uint[] crcTable;

        private static uint Crc(byte[] data, uint crc)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }
                    t[n] = c;
                }
                crcTable = t;
            }
            foreach (byte x in data)
            {
                crc = crcTable[(crc ^ x) & 0xff] ^ (crc >> 8);
            }
            return crc;
        }

        private static void WriteBigEndian(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
