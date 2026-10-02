using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class ApngWriterTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Apng_RoundTrip_AlphaAndColor", Action = RoundTrip_AlphaAndColor };
            yield return new TestCase { Name = "Apng_MultiFrame_PreservesEachFrameAndDelay", Action = MultiFrame_PreservesEachFrameAndDelay };
            yield return new TestCase { Name = "Apng_DefaultImage_OpensAsOrdinaryPngInGdi", Action = DefaultImage_OpensAsOrdinaryPngInGdi };
            yield return new TestCase { Name = "Apng_RejectsMismatchedFrameSizes", Action = RejectsMismatchedFrameSizes };
            yield return new TestCase { Name = "Apng_RejectsEmptyFrameList", Action = RejectsEmptyFrameList };
        }

        private static string TempPath()
        {
            return Path.Combine(Path.GetTempPath(), "apng_test_" + Guid.NewGuid().ToString("N") + ".apng");
        }

        private static void RoundTrip_AlphaAndColor()
        {
            string path = TempPath();
            try
            {
                using (var frame = new Bitmap(4, 1, PixelFormat.Format32bppArgb))
                {
                    frame.SetPixel(0, 0, Color.FromArgb(255, 255, 255, 255));
                    frame.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));
                    frame.SetPixel(2, 0, Color.FromArgb(0, 10, 20, 30));
                    // GIFと違い256色パレットへ量子化しないため、半透明もそのまま保持できる。
                    frame.SetPixel(3, 0, Color.FromArgb(127, 64, 128, 192));

                    ApngWriter.SaveAnimatedApng(path, new List<Bitmap> { frame }, 100);
                }

                MinimalApngReader.Apng apng = MinimalApngReader.Read(path);
                Assert.AreEqual(1, apng.FrameCount, "frame count");
                Assert.AreEqual(1, apng.Frames.Count, "decoded frame count");

                Color white = apng.Frames[0].GetPixel(0, 0);
                Assert.AreEqual(255, (int)white.A, "opaque white alpha");
                Assert.AreEqual(255, (int)white.R, "opaque white R");
                Assert.AreEqual(255, (int)white.G, "opaque white G");
                Assert.AreEqual(255, (int)white.B, "opaque white B");

                Color red = apng.Frames[0].GetPixel(1, 0);
                Assert.AreEqual(255, (int)red.R, "opaque red R");
                Assert.AreEqual(0, (int)red.G, "opaque red G");
                Assert.AreEqual(0, (int)red.B, "opaque red B");

                Color transparent = apng.Frames[0].GetPixel(2, 0);
                Assert.AreEqual(0, (int)transparent.A, "fully transparent source pixel stays transparent");

                // 半透明はGIFなら0か255へ丸められるが、APNGはそのまま保持されるはず。
                Color translucent = apng.Frames[0].GetPixel(3, 0);
                Assert.AreEqual(127, (int)translucent.A, "translucent alpha is preserved exactly (no palette rounding)");
                Assert.AreEqual(64, (int)translucent.R, "translucent R is preserved exactly");
                Assert.AreEqual(128, (int)translucent.G, "translucent G is preserved exactly");
                Assert.AreEqual(192, (int)translucent.B, "translucent B is preserved exactly");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void MultiFrame_PreservesEachFrameAndDelay()
        {
            string path = TempPath();
            try
            {
                Color[] frameColors = { Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 0, 255) };
                var frames = new List<Bitmap>();
                foreach (Color c in frameColors)
                {
                    var bmp = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.Clear(c);
                    frames.Add(bmp);
                }

                try
                {
                    ApngWriter.SaveAnimatedApng(path, frames, 250);
                }
                finally
                {
                    foreach (Bitmap bmp in frames) bmp.Dispose();
                }

                MinimalApngReader.Apng apng = MinimalApngReader.Read(path);
                Assert.AreEqual(frameColors.Length, apng.FrameCount, "acTL frame count");
                Assert.AreEqual(frameColors.Length, apng.Frames.Count, "decoded frame count");
                Assert.AreEqual(0, apng.NumPlays, "num_plays should be 0 (infinite loop, matching GIF)");

                for (int i = 0; i < frameColors.Length; i++)
                {
                    Assert.AreEqual(3, apng.Frames[i].Width, "frame " + i + " width");
                    Assert.AreEqual(2, apng.Frames[i].Height, "frame " + i + " height");
                    Color pixel = apng.Frames[i].GetPixel(0, 0);
                    Assert.AreEqual((int)frameColors[i].R, (int)pixel.R, "frame " + i + " R");
                    Assert.AreEqual((int)frameColors[i].G, (int)pixel.G, "frame " + i + " G");
                    Assert.AreEqual((int)frameColors[i].B, (int)pixel.B, "frame " + i + " B");
                    // delay_num/delay_den = 250/1000 秒 = 250ms。
                    Assert.AreEqual(250, apng.DelaysNum[i], "frame " + i + " delay_num");
                    Assert.AreEqual(1000, apng.DelaysDen[i], "frame " + i + " delay_den");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // APNGを認識しない普通のPNGデコーダー（.NETのGDI+含む）でも、フレーム0だけは
        // 通常のPNGとしてそのまま開けるはず（APNGの後方互換の要）。
        private static void DefaultImage_OpensAsOrdinaryPngInGdi()
        {
            string path = TempPath();
            try
            {
                var frames = new List<Bitmap>();
                for (int i = 0; i < 2; i++)
                {
                    var bmp = new Bitmap(5, 4, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.Clear(i == 0 ? Color.FromArgb(255, 10, 20, 30) : Color.FromArgb(255, 200, 201, 202));
                    frames.Add(bmp);
                }
                try { ApngWriter.SaveAnimatedApng(path, frames, 100); }
                finally { foreach (Bitmap bmp in frames) bmp.Dispose(); }

                using (var decoded = new Bitmap(path))
                {
                    Assert.AreEqual(5, decoded.Width, "GDI+ reads IHDR width");
                    Assert.AreEqual(4, decoded.Height, "GDI+ reads IHDR height");
                    Color pixel = decoded.GetPixel(0, 0);
                    Assert.AreEqual(10, (int)pixel.R, "GDI+ decodes frame 0 (default image) R");
                    Assert.AreEqual(20, (int)pixel.G, "GDI+ decodes frame 0 (default image) G");
                    Assert.AreEqual(30, (int)pixel.B, "GDI+ decodes frame 0 (default image) B");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void RejectsMismatchedFrameSizes()
        {
            using (var a = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
            using (var b = new Bitmap(3, 3, PixelFormat.Format32bppArgb))
            {
                bool threw = false;
                try
                {
                    ApngWriter.SaveAnimatedApng(TempPath(), new List<Bitmap> { a, b }, 100);
                }
                catch (ArgumentException)
                {
                    threw = true;
                }
                Assert.IsTrue(threw, "mismatched frame dimensions should raise ArgumentException");
            }
        }

        private static void RejectsEmptyFrameList()
        {
            bool threw = false;
            try
            {
                ApngWriter.SaveAnimatedApng(TempPath(), new List<Bitmap>(), 100);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "an empty frame list should raise ArgumentException");
        }

        // ApngWriterが正しく書いたことを、書いた側のコードを一切再利用せずに確かめるための
        // 最小限のAPNGリーダー（チャンクのCRC検証・zlib展開・PNGフィルターの復元を自前で行う）。
        internal static class MinimalApngReader
        {
            public sealed class Apng
            {
                public int FrameCount;
                public int NumPlays;
                public readonly List<Bitmap> Frames = new List<Bitmap>();
                public readonly List<int> DelaysNum = new List<int>();
                public readonly List<int> DelaysDen = new List<int>();
            }

            public static Apng Read(string path)
            {
                byte[] bytes = File.ReadAllBytes(path);
                int offset = 0;
                byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                for (int i = 0; i < 8; i++)
                    if (bytes[i] != signature[i]) throw new InvalidDataException("not a PNG file");
                offset = 8;

                var result = new Apng();
                int width = 0, height = 0;
                var pendingChunks = new List<byte[]>(); // 現在のフレームぶんの IDAT/fdAT データ（連番なし）
                int? pendingWidth = null, pendingHeight = null;
                int pendingDelayNum = 0, pendingDelayDen = 1;
                bool haveFrame = false;

                Action flushFrame = () =>
                {
                    if (!haveFrame) return;
                    int totalLen = 0;
                    foreach (byte[] c in pendingChunks) totalLen += c.Length;
                    var compressed = new byte[totalLen];
                    int at = 0;
                    foreach (byte[] c in pendingChunks) { Buffer.BlockCopy(c, 0, compressed, at, c.Length); at += c.Length; }
                    Bitmap bmp = DecodeZlibRgba(compressed, pendingWidth.Value, pendingHeight.Value);
                    result.Frames.Add(bmp);
                    result.DelaysNum.Add(pendingDelayNum);
                    result.DelaysDen.Add(pendingDelayDen);
                    pendingChunks.Clear();
                    haveFrame = false;
                };

                while (offset < bytes.Length)
                {
                    int length = ReadBigEndian(bytes, offset);
                    string type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
                    int dataOffset = offset + 8;
                    uint storedCrc = unchecked((uint)ReadBigEndian(bytes, dataOffset + length));
                    uint computedCrc = Crc32(bytes, offset + 4, 4 + length);
                    if (storedCrc != computedCrc) throw new InvalidDataException("bad CRC in chunk " + type);

                    if (type == "IHDR")
                    {
                        width = ReadBigEndian(bytes, dataOffset);
                        height = ReadBigEndian(bytes, dataOffset + 4);
                        if (bytes[dataOffset + 8] != 8 || bytes[dataOffset + 9] != 6)
                            throw new InvalidDataException("expected 8-bit RGBA PNG");
                    }
                    else if (type == "acTL")
                    {
                        result.FrameCount = ReadBigEndian(bytes, dataOffset);
                        result.NumPlays = ReadBigEndian(bytes, dataOffset + 4);
                    }
                    else if (type == "fcTL")
                    {
                        flushFrame();
                        pendingWidth = ReadBigEndian(bytes, dataOffset + 4);
                        pendingHeight = ReadBigEndian(bytes, dataOffset + 8);
                        pendingDelayNum = (bytes[dataOffset + 20] << 8) | bytes[dataOffset + 21];
                        pendingDelayDen = (bytes[dataOffset + 22] << 8) | bytes[dataOffset + 23];
                        if (pendingDelayDen == 0) pendingDelayDen = 100;
                        haveFrame = true;
                    }
                    else if (type == "IDAT")
                    {
                        var chunk = new byte[length];
                        Buffer.BlockCopy(bytes, dataOffset, chunk, 0, length);
                        pendingChunks.Add(chunk);
                    }
                    else if (type == "fdAT")
                    {
                        var chunk = new byte[length - 4];
                        Buffer.BlockCopy(bytes, dataOffset + 4, chunk, 0, length - 4);
                        pendingChunks.Add(chunk);
                    }
                    else if (type == "IEND")
                    {
                        flushFrame();
                        break;
                    }

                    offset = dataOffset + length + 4;
                }

                if (width == 0 || height == 0) throw new InvalidDataException("missing IHDR");
                return result;
            }

            private static Bitmap DecodeZlibRgba(byte[] zlibData, int width, int height)
            {
                // 先頭2バイト（zlibヘッダー）と末尾4バイト（Adler32）を除いた中身がdeflate本体。
                using (var ms = new MemoryStream(zlibData, 2, zlibData.Length - 2 - 4))
                using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
                using (var raw = new MemoryStream())
                {
                    deflate.CopyTo(raw);
                    byte[] filtered = raw.ToArray();
                    int rowBytes = width * 4;
                    if (filtered.Length != (rowBytes + 1) * height)
                        throw new InvalidDataException("unexpected decompressed size");

                    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    var previous = new byte[rowBytes];
                    var current = new byte[rowBytes];
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int pos = 0;
                        for (int y = 0; y < height; y++)
                        {
                            byte filterType = filtered[pos++];
                            for (int i = 0; i < rowBytes; i++)
                            {
                                byte raw8 = filtered[pos + i];
                                byte reconstructed;
                                switch (filterType)
                                {
                                    case 0: reconstructed = raw8; break;
                                    case 1: reconstructed = (byte)(raw8 + (i >= 4 ? current[i - 4] : (byte)0)); break;
                                    case 2: reconstructed = (byte)(raw8 + previous[i]); break;
                                    default: throw new InvalidDataException("unsupported PNG filter type " + filterType);
                                }
                                current[i] = reconstructed;
                            }
                            pos += rowBytes;
                            // PNGはRGBA順、GDI+のBitmapはBGRA順なので入れ替える。
                            var bgra = new byte[rowBytes];
                            for (int i = 0; i < rowBytes; i += 4)
                            {
                                bgra[i] = current[i + 2];
                                bgra[i + 1] = current[i + 1];
                                bgra[i + 2] = current[i];
                                bgra[i + 3] = current[i + 3];
                            }
                            Marshal.Copy(bgra, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                            byte[] swap = previous; previous = current; current = swap;
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }
                    return bitmap;
                }
            }

            private static int ReadBigEndian(byte[] data, int offset)
            {
                return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            }

            private static readonly uint[] CrcTable = BuildCrcTable();

            private static uint[] BuildCrcTable()
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                return table;
            }

            private static uint Crc32(byte[] data, int offset, int length)
            {
                uint crc = 0xFFFFFFFFu;
                for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xFFFFFFFFu;
            }
        }
    }
}
