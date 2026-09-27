using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class ImagePipelineTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Pipeline_Load_DownscalesByDivisor", Action = Load_DownscalesByDivisor };
            yield return new TestCase { Name = "Pipeline_Load_AppliesBlackTransparency", Action = Load_AppliesBlackTransparency };
            yield return new TestCase { Name = "Pipeline_Load_AppliesColorAdjustment", Action = Load_AppliesColorAdjustment };
            yield return new TestCase { Name = "Pipeline_Load_ReflectsFileChangeAfterCacheHit", Action = Load_ReflectsFileChangeAfterCacheHit };
            yield return new TestCase { Name = "ConvertBlackToAlpha_TurnsBlackOpaqueIntoTransparent", Action = ConvertBlackToAlpha_TurnsBlackOpaqueIntoTransparent };
            yield return new TestCase { Name = "ApplyColorAdjustment_MultiplyByWhiteIsNoOp", Action = ApplyColorAdjustment_MultiplyByWhiteIsNoOp };
        }

        private static string TempPngPath()
        {
            return Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid().ToString("N") + ".png");
        }

        private static void SavePng(string path, int width, int height, Color fill)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(fill);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static List<LoadFolderRequest> SingleItemRequest(string path)
        {
            var item = new LoadItemRequest { Source = null, Path = path };
            var folder = new LoadFolderRequest { Name = "folder" };
            folder.Items.Add(item);
            return new List<LoadFolderRequest> { folder };
        }

        private static void Load_DownscalesByDivisor()
        {
            string path = TempPngPath();
            try
            {
                SavePng(path, 40, 20, Color.White);
                var pipeline = new SpriteImagePipeline();
                List<LoadedFolder> loaded = pipeline.Load(SingleItemRequest(path), 2, false,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                try
                {
                    Bitmap result = loaded[0].Items[0].Bitmap;
                    Assert.AreEqual(20, result.Width, "divisor 2 should halve width");
                    Assert.AreEqual(10, result.Height, "divisor 2 should halve height");
                }
                finally
                {
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Load_AppliesBlackTransparency()
        {
            string path = TempPngPath();
            try
            {
                SavePng(path, 2, 2, Color.Black);
                var pipeline = new SpriteImagePipeline();
                List<LoadedFolder> loaded = pipeline.Load(SingleItemRequest(path), 1, true,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                try
                {
                    Color pixel = loaded[0].Items[0].Bitmap.GetPixel(0, 0);
                    Assert.AreEqual(0, (int)pixel.A, "opaque black should become fully transparent when black-transparency is enabled");
                }
                finally
                {
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Load_AppliesColorAdjustment()
        {
            string path = TempPngPath();
            try
            {
                SavePng(path, 2, 2, Color.White);
                var pipeline = new SpriteImagePipeline();
                List<LoadedFolder> loaded = pipeline.Load(SingleItemRequest(path), 1, false,
                    SpriteColorBlendMode.Multiply, Color.FromArgb(0, 128, 255), 100, CancellationToken.None);
                try
                {
                    Color pixel = loaded[0].Items[0].Bitmap.GetPixel(0, 0);
                    Assert.AreEqual(0, (int)pixel.R, "full-strength multiply by (0,128,255) should zero R");
                    Assert.AreEqual(128, (int)pixel.G, "full-strength multiply by (0,128,255) should keep G at 128");
                    Assert.AreEqual(255, (int)pixel.B, "full-strength multiply by (0,128,255) should keep B at 255");
                }
                finally
                {
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // divisorやconvertBlackが変わらなくても、ソースファイル自体が書き換わったら
        // キャッシュを再利用せず最新の内容を読み直すことを確認する。
        private static void Load_ReflectsFileChangeAfterCacheHit()
        {
            string path = TempPngPath();
            try
            {
                SavePng(path, 2, 2, Color.Red);
                var pipeline = new SpriteImagePipeline();
                List<LoadedFolder> first = pipeline.Load(SingleItemRequest(path), 1, false,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                Color firstPixel = first[0].Items[0].Bitmap.GetPixel(0, 0);
                SpriteImagePipeline.DisposeLoaded(first);
                Assert.AreEqual(255, (int)firstPixel.R, "first load should read the red source");

                // LastWriteTimeが変わったと確実にみなされるよう、タイムスタンプを明示的に進める。
                SavePng(path, 2, 2, Color.Blue);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

                List<LoadedFolder> second = pipeline.Load(SingleItemRequest(path), 1, false,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                Color secondPixel = second[0].Items[0].Bitmap.GetPixel(0, 0);
                SpriteImagePipeline.DisposeLoaded(second);
                Assert.AreEqual(255, (int)secondPixel.B, "second load should see the updated blue source, not a stale cache entry");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void ConvertBlackToAlpha_TurnsBlackOpaqueIntoTransparent()
        {
            using (var bitmap = new Bitmap(2, 1, PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(255, 0, 0, 0));
                bitmap.SetPixel(1, 0, Color.FromArgb(255, 64, 32, 0));
                SpriteImagePipeline.ConvertBlackToAlpha(bitmap, CancellationToken.None);

                Color removedBlack = bitmap.GetPixel(0, 0);
                Color glow = bitmap.GetPixel(1, 0);
                Assert.AreEqual(0, (int)removedBlack.A, "pure black should become fully transparent");
                Assert.AreEqual(64, (int)glow.A, "brightness becomes the new alpha");
                Assert.AreEqual(255, (int)glow.R, "color channel is re-normalized by brightness");
            }
        }

        private static void ApplyColorAdjustment_MultiplyByWhiteIsNoOp()
        {
            using (var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(200, 10, 20, 30));
                SpriteImagePipeline.ApplySpriteColorAdjustment(bitmap, SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                Color pixel = bitmap.GetPixel(0, 0);
                Assert.AreEqual(200, (int)pixel.A, "multiply by white should not change alpha");
                Assert.AreEqual(10, (int)pixel.R, "multiply by white should be a no-op on R");
                Assert.AreEqual(20, (int)pixel.G, "multiply by white should be a no-op on G");
                Assert.AreEqual(30, (int)pixel.B, "multiply by white should be a no-op on B");
            }
        }
    }
}
