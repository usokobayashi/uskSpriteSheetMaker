using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class GifWriterTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Gif_RoundTrip_AlphaAndColor", Action = RoundTrip_AlphaAndColor };
            yield return new TestCase { Name = "Gif_MultiFrame_PreservesEachFrame", Action = MultiFrame_PreservesEachFrame };
            yield return new TestCase { Name = "Gif_RejectsMismatchedFrameSizes", Action = RejectsMismatchedFrameSizes };
            yield return new TestCase { Name = "Gif_RejectsEmptyFrameList", Action = RejectsEmptyFrameList };
        }

        private static string TempPath()
        {
            return Path.Combine(Path.GetTempPath(), "gif_test_" + Guid.NewGuid().ToString("N") + ".gif");
        }

        // 元画像(32bppArgb)を透過背景へ合成し、GIFのパレット外／透過インデックスの
        // 挙動をGetPixelで検証できる状態にする。
        private static Bitmap CompositeOverTransparent(Bitmap decoded)
        {
            var composited = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(composited))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImageUnscaled(decoded, 0, 0);
            }
            return composited;
        }

        private static void RoundTrip_AlphaAndColor()
        {
            string path = TempPath();
            try
            {
                using (var frame = new Bitmap(4, 1, PixelFormat.Format32bppArgb))
                {
                    // 6x7x6パレットに正確に量子化される色（白・純赤）と、
                    // 透過しきい値(128)の境界ケースを1フレームに詰める。
                    frame.SetPixel(0, 0, Color.FromArgb(255, 255, 255, 255));
                    frame.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));
                    frame.SetPixel(2, 0, Color.FromArgb(0, 10, 20, 30));
                    frame.SetPixel(3, 0, Color.FromArgb(127, 255, 255, 255));

                    GifWriter.SaveAnimatedGif(path, new List<Bitmap> { frame }, 100);
                }

                using (var decoded = new Bitmap(path))
                using (var composited = CompositeOverTransparent(decoded))
                {
                    Color white = composited.GetPixel(0, 0);
                    Assert.AreEqual(255, (int)white.A, "opaque white alpha");
                    Assert.AreEqual(255, (int)white.R, "opaque white R");
                    Assert.AreEqual(255, (int)white.G, "opaque white G");
                    Assert.AreEqual(255, (int)white.B, "opaque white B");

                    Color red = composited.GetPixel(1, 0);
                    Assert.AreEqual(255, (int)red.A, "opaque red alpha");
                    Assert.AreEqual(255, (int)red.R, "opaque red R");
                    Assert.AreEqual(0, (int)red.G, "opaque red G");
                    Assert.AreEqual(0, (int)red.B, "opaque red B");

                    Color fullyTransparent = composited.GetPixel(2, 0);
                    Assert.AreEqual(0, (int)fullyTransparent.A, "fully transparent source pixel should decode transparent");

                    Color belowThreshold = composited.GetPixel(3, 0);
                    Assert.AreEqual(0, (int)belowThreshold.A, "alpha 127 (below 128 threshold) should decode transparent");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void MultiFrame_PreservesEachFrame()
        {
            string path = TempPath();
            try
            {
                Color[] frameColors = { Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 0, 255) };
                var frames = new List<Bitmap>();
                foreach (Color c in frameColors)
                {
                    var bmp = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.Clear(c);
                    frames.Add(bmp);
                }

                try
                {
                    GifWriter.SaveAnimatedGif(path, frames, 50);
                }
                finally
                {
                    foreach (Bitmap bmp in frames) bmp.Dispose();
                }

                using (var decoded = new Bitmap(path))
                {
                    FrameDimension dimension = new FrameDimension(decoded.FrameDimensionsList[0]);
                    int frameCount = decoded.GetFrameCount(dimension);
                    Assert.AreEqual(frameColors.Length, frameCount, "GIF should contain one frame per input bitmap");

                    for (int i = 0; i < frameColors.Length; i++)
                    {
                        decoded.SelectActiveFrame(dimension, i);
                        using (Bitmap composited = CompositeOverTransparent(decoded))
                        {
                            Color pixel = composited.GetPixel(0, 0);
                            Assert.AreEqual((int)frameColors[i].R, (int)pixel.R, "frame " + i + " R");
                            Assert.AreEqual((int)frameColors[i].G, (int)pixel.G, "frame " + i + " G");
                            Assert.AreEqual((int)frameColors[i].B, (int)pixel.B, "frame " + i + " B");
                        }
                    }
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
                    GifWriter.SaveAnimatedGif(TempPath(), new List<Bitmap> { a, b }, 100);
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
                GifWriter.SaveAnimatedGif(TempPath(), new List<Bitmap>(), 100);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "an empty frame list should raise ArgumentException");
        }
    }
}
