using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class TgaWriterTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Tga_RoundTrip_ExactPixels", Action = RoundTrip_ExactPixels };
            yield return new TestCase { Name = "Tga_HeaderFieldsMatchDimensions", Action = HeaderFieldsMatchDimensions };
            yield return new TestCase { Name = "Tga_RejectsNullSource", Action = RejectsNullSource };
        }

        private static string TempPath()
        {
            return Path.Combine(Path.GetTempPath(), "tga_test_" + Guid.NewGuid().ToString("N") + ".tga");
        }

        // TGAは無圧縮32bitなので、TgaWriterの出力はソース画像と完全一致するはずである
        // （GIFのようなパレット量子化やアルファ丸めが存在しない）。
        // ソース画像はSetPixelではなくLockBitsで直接書き込む。Bitmap.SetPixelは
        // Format32bppArgbであっても内部丸めで±1階調ずれることがあり、それは
        // GDI+側の話でTgaWriterの検証対象ではないため。
        private static void RoundTrip_ExactPixels()
        {
            string path = TempPath();
            try
            {
                const int width = 3;
                const int height = 2;
                var expected = new byte[height, width, 4]; // [y, x, (B,G,R,A)]
                using (var source = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    var rng = new Random(12345);
                    Rectangle rect = new Rectangle(0, 0, width, height);
                    BitmapData data = source.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = data.Stride;
                        byte[] row = new byte[Math.Abs(stride)];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int idx = x * 4;
                                byte b = (byte)rng.Next(256);
                                byte g = (byte)rng.Next(256);
                                byte r = (byte)rng.Next(256);
                                byte a = (byte)rng.Next(256);
                                row[idx] = b;
                                row[idx + 1] = g;
                                row[idx + 2] = r;
                                row[idx + 3] = a;
                                expected[y, x, 0] = b;
                                expected[y, x, 1] = g;
                                expected[y, x, 2] = r;
                                expected[y, x, 3] = a;
                            }
                            Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * stride), row.Length);
                        }
                    }
                    finally
                    {
                        source.UnlockBits(data);
                    }

                    TgaWriter.Save32Bit(path, source);
                }

                byte[] bytes = File.ReadAllBytes(path);
                Assert.IsTrue(bytes.Length >= 18 + width * height * 4, "file should contain header and full pixel payload");

                int readWidth = bytes[12] | (bytes[13] << 8);
                int readHeight = bytes[14] | (bytes[15] << 8);
                Assert.AreEqual(width, readWidth, "header width");
                Assert.AreEqual(height, readHeight, "header height");

                int offset = 18;
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Assert.AreEqual((int)expected[y, x, 0], (int)bytes[offset], "B at (" + x + "," + y + ")");
                        Assert.AreEqual((int)expected[y, x, 1], (int)bytes[offset + 1], "G at (" + x + "," + y + ")");
                        Assert.AreEqual((int)expected[y, x, 2], (int)bytes[offset + 2], "R at (" + x + "," + y + ")");
                        Assert.AreEqual((int)expected[y, x, 3], (int)bytes[offset + 3], "A at (" + x + "," + y + ")");
                        offset += 4;
                    }
                }

                // フッタ末尾18バイトは 17バイトの署名文字列 + 終端のNULバイト。
                string footerSignature = Encoding.ASCII.GetString(bytes, bytes.Length - 18, 17);
                Assert.AreEqual("TRUEVISION-XFILE.", footerSignature, "TGA 2.0 footer signature");
                Assert.AreEqual(0, (int)bytes[bytes.Length - 1], "footer should end with a NUL terminator");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void HeaderFieldsMatchDimensions()
        {
            string path = TempPath();
            try
            {
                using (var source = new Bitmap(10, 7, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(source)) g.Clear(Color.CornflowerBlue);
                    TgaWriter.Save32Bit(path, source);
                }

                byte[] bytes = File.ReadAllBytes(path);
                Assert.AreEqual(0, (int)bytes[0], "ID length should be 0");
                Assert.AreEqual(0, (int)bytes[1], "color map type should be 0 (none)");
                Assert.AreEqual(2, (int)bytes[2], "image type should be 2 (uncompressed true-color)");
                Assert.AreEqual(32, (int)bytes[16], "bits per pixel should be 32");
                Assert.AreEqual(8, (int)bytes[17], "image descriptor should mark 8 alpha bits");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void RejectsNullSource()
        {
            bool threw = false;
            try
            {
                TgaWriter.Save32Bit(TempPath(), null);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "null source bitmap should raise ArgumentNullException");
        }
    }
}
