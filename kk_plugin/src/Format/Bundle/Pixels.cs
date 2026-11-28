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

        /// <summary>Multiplies every pixel by a colour (0..1 per channel, alpha included).</summary>
        public static void Tint(byte[] rgba, float r, float g, float b, float a)
        {
            float[] k = { Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(a) };
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                for (int c = 0; c < 4; c++)
                {
                    rgba[i + c] = (byte)Math.Round(rgba[i + c] * k[c]);
                }
            }
        }

        private static float Clamp01(float v)
        {
            return float.IsNaN(v) ? 1f : Math.Max(0f, Math.Min(1f, v));
        }

        /// <summary>
        /// A texture as a material places it over UV space once: target pixel (u, v) shows the source at
        /// (u * scale + offset), repeating, sampled bilinearly. Source and target are bottom-up RGBA.
        /// </summary>
        public static byte[] Place(byte[] src, int sw, int sh, int dw, int dh, float scaleX, float scaleY, float offsetX, float offsetY)
        {
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                double v = ((y + 0.5) / dh * scaleY + offsetY) * sh - 0.5;
                int y0 = (int)Math.Floor(v);
                double fy = v - y0;
                int ya = Wrap(y0, sh), yb = Wrap(y0 + 1, sh);
                for (int x = 0; x < dw; x++)
                {
                    double u = ((x + 0.5) / dw * scaleX + offsetX) * sw - 0.5;
                    int x0 = (int)Math.Floor(u);
                    double fx = u - x0;
                    int xa = Wrap(x0, sw), xb = Wrap(x0 + 1, sw);
                    int o = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        double top = src[(ya * sw + xa) * 4 + c] * (1 - fx) + src[(ya * sw + xb) * 4 + c] * fx;
                        double bottom = src[(yb * sw + xa) * 4 + c] * (1 - fx) + src[(yb * sw + xb) * 4 + c] * fx;
                        dst[o + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(top * (1 - fy) + bottom * fy)));
                    }
                }
            }
            return dst;
        }

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }

        /// <summary>A copy filled with one colour (0..1 per channel).</summary>
        public static byte[] Solid(int pixels, float r, float g, float b, float a)
        {
            var px = new byte[pixels * 4];
            byte[] c = { (byte)Math.Round(Clamp01(r) * 255), (byte)Math.Round(Clamp01(g) * 255), (byte)Math.Round(Clamp01(b) * 255), (byte)Math.Round(Clamp01(a) * 255) };
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = c[0];
                px[i + 1] = c[1];
                px[i + 2] = c[2];
                px[i + 3] = c[3];
            }
            return px;
        }

        /// <summary>The average colour (0..1, alpha-weighted RGB and plain alpha) of a buffer.</summary>
        public static float[] Average(byte[] rgba)
        {
            double r = 0, g = 0, b = 0, a = 0;
            int n = rgba.Length / 4;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double w = rgba[i + 3] / 255.0;
                r += rgba[i] * w;
                g += rgba[i + 1] * w;
                b += rgba[i + 2] * w;
                a += w;
            }
            if (n == 0)
            {
                return new[] { 0f, 0f, 0f, 0f };
            }
            return a > 0
                ? new[] { (float)(r / a / 255), (float)(g / a / 255), (float)(b / a / 255), (float)(a / n) }
                : new[] { 0f, 0f, 0f, 0f };
        }

        /// <summary>
        /// The share (0..1) of pixels that show something: not transparent (alpha above 5%) and not
        /// black (a channel above 2%). A game bake far below the same material's texture-times-colour
        /// bake drew nothing useful (a shader a UV-space render can't draw).
        /// </summary>
        public static double Coverage(byte[] rgba)
        {
            int n = rgba.Length / 4;
            if (n == 0)
            {
                return 0;
            }
            int shown = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 3] > 12 && (rgba[i] > 5 || rgba[i + 1] > 5 || rgba[i + 2] > 5))
                {
                    shown++;
                }
            }
            return (double)shown / n;
        }

        /// <summary>Whether a game bake should be replaced by the Flat (texture x colour) one: it shows
        /// under a tenth of what the Flat bake shows, and the Flat bake shows a fair part (over 5%).</summary>
        public static bool GameBakeLooksEmpty(double gameCoverage, double flatCoverage)
        {
            return flatCoverage > 0.05 && gameCoverage < flatCoverage * 0.1;
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
