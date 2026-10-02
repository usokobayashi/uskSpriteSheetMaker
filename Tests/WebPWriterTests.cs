//==================================================
// WebPWriterTests
// アニメーション WebP の器（RIFF・VP8X・ANIM・ANMF）と、可逆圧縮の符号作りの検証。
// 画素の一致は外部デコーダー（Pillow）で別途確認している。
//==================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class WebPWriterTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "WebP_PrefixEncode_RoundTrips", Action = PrefixEncode_RoundTrips };
            yield return new TestCase { Name = "WebP_BuildLengths_CompleteAndLimited", Action = BuildLengths_CompleteAndLimited };
            yield return new TestCase { Name = "WebP_File_HasAnimationContainer", Action = File_HasAnimationContainer };
            yield return new TestCase { Name = "WebP_RejectsTooLargeOrMismatchedFrames", Action = RejectsTooLargeOrMismatchedFrames };
        }

        // 仕様の復号式で元の値に戻ること。
        private static void PrefixEncode_RoundTrips()
        {
            for (int value = 1; value <= 1 << 20; value = value < 5000 ? value + 1 : value * 2)
            {
                int prefix, bits, extra;
                WebPWriter.PrefixEncode(value, out prefix, out bits, out extra);
                int decoded;
                if (prefix < 4) decoded = prefix + 1;
                else
                {
                    int extraBits = (prefix - 2) >> 1;
                    Assert.AreEqual(extraBits, bits, "extra bit count for " + value);
                    decoded = ((2 + (prefix & 1)) << extraBits) + extra + 1;
                }
                Assert.AreEqual(value, decoded, "prefix round trip");
                if (value <= 4096) Assert.IsTrue(prefix < 24, "lengths up to 4096 fit the 24 length prefixes");
            }
        }

        // 符号長は上限以内で、木として過不足がない（Kraft の和がちょうど1）。
        private static void BuildLengths_CompleteAndLimited()
        {
            var rnd = new Random(3);
            for (int trial = 0; trial < 200; trial++)
            {
                int size = trial % 2 == 0 ? 280 : 19;
                int limit = size == 19 ? 7 : 15;
                var histogram = new int[size];
                int used = 0;
                for (int s = 0; s < size; s++)
                {
                    if (rnd.Next(3) == 0) continue;
                    // 極端に偏った出現数で、上限を超えやすくする
                    histogram[s] = trial % 3 == 0 ? 1 << rnd.Next(25) : rnd.Next(1, 1000);
                    used++;
                }
                if (used < 2) { histogram[0] = 5; histogram[1] = 9; }
                int[] lengths = WebPWriter.BuildLengths(histogram, limit);
                double kraft = 0;
                for (int s = 0; s < size; s++)
                {
                    Assert.IsTrue(lengths[s] <= limit, "length limit");
                    Assert.AreEqual(histogram[s] > 0, lengths[s] > 0, "only used symbols get a code");
                    if (lengths[s] > 0) kraft += Math.Pow(2, -lengths[s]);
                }
                Assert.InRange(kraft, 1 - 1e-9, 1 + 1e-9, "complete prefix code");
            }
        }

        private static void File_HasAnimationContainer()
        {
            string path = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerTests-webp-" + Guid.NewGuid().ToString("N") + ".webp");
            try
            {
                var frames = new List<Bitmap>();
                for (int i = 0; i < 3; i++)
                {
                    var bmp = new Bitmap(5, 3, PixelFormat.Format32bppArgb);
                    bmp.SetPixel(i, 1, Color.FromArgb(128, 255, 0, 0));
                    frames.Add(bmp);
                }
                WebPWriter.SaveAnimatedWebP(path, 5, 3, 3, i => frames[i], true, 83, null, CancellationToken.None);
                byte[] data = File.ReadAllBytes(path);
                Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(data, 0, 4), "RIFF");
                Assert.AreEqual(data.Length - 8, BitConverter.ToInt32(data, 4), "RIFF size covers the whole file");
                Assert.AreEqual("WEBPVP8X", System.Text.Encoding.ASCII.GetString(data, 8, 8), "VP8X first");
                Assert.AreEqual(0x12, (int)data[20], "alpha and animation flags");
                Assert.AreEqual(4, data[24] | data[25] << 8 | data[26] << 16, "canvas width - 1");
                Assert.AreEqual(2, data[27] | data[28] << 8 | data[29] << 16, "canvas height - 1");

                int offset = 30, anmf = 0;
                Assert.AreEqual("ANIM", System.Text.Encoding.ASCII.GetString(data, offset, 4), "ANIM after VP8X");
                while (offset < data.Length)
                {
                    string type = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                    int size = BitConverter.ToInt32(data, offset + 4);
                    if (type == "ANMF")
                    {
                        anmf++;
                        int body = offset + 8;
                        Assert.AreEqual(83, data[body + 12] | data[body + 13] << 8 | data[body + 14] << 16, "frame duration");
                        Assert.AreEqual(2, (int)data[body + 15], "no blending, no disposal");
                        Assert.AreEqual("VP8L", System.Text.Encoding.ASCII.GetString(data, body + 16, 4), "lossless frame");
                        Assert.AreEqual(0x2F, (int)data[body + 24], "VP8L signature");
                    }
                    offset += 8 + size + (size & 1);
                }
                Assert.AreEqual(data.Length, offset, "chunks end exactly at the file end");
                Assert.AreEqual(3, anmf, "one ANMF per frame");
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void RejectsTooLargeOrMismatchedFrames()
        {
            string path = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerTests-webp-bad.webp");
            bool threw = false;
            try { WebPWriter.SaveAnimatedWebP(path, WebPWriter.MaxDimension + 1, 1, 1, i => null, false, 10, null, CancellationToken.None); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Assert.IsTrue(threw, "too wide");

            threw = false;
            try
            {
                using (var small = new Bitmap(2, 2))
                    WebPWriter.SaveAnimatedWebP(path, 3, 3, 1, i => small, false, 10, null, CancellationToken.None);
            }
            catch (ArgumentException) { threw = true; }
            Assert.IsTrue(threw, "frame size mismatch");
            Assert.IsFalse(File.Exists(path), "no partial file is left");
        }
    }
}
