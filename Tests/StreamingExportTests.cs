using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    // 大きな画像向けの経路（帯ごとの書き出し・プレビュー縮小・キャッシュ上限）の検証。
    internal static class StreamingExportTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Streaming_Png_MultiBandRoundTripsExactPixels", Action = Png_MultiBandRoundTripsExactPixels };
            yield return new TestCase { Name = "Streaming_Png_ReadableByGdiForManyBandHeights", Action = Png_ReadableByGdiForManyBandHeights };
            yield return new TestCase { Name = "Streaming_Tga_MatchesTgaWriterByteForByte", Action = Tga_MatchesTgaWriterByteForByte };
            yield return new TestCase { Name = "Streaming_Tga_RejectsTooLarge", Action = Tga_RejectsTooLarge };
            yield return new TestCase { Name = "Streaming_Cancel_LeavesNoPartialFileAndKeepsExisting", Action = Cancel_LeavesNoPartialFileAndKeepsExisting };
            yield return new TestCase { Name = "Streaming_Gif_FrameProviderMatchesListOverload", Action = Gif_FrameProviderMatchesListOverload };
            yield return new TestCase { Name = "Streaming_Gif_HashLzwMatchesDictionaryReference", Action = Gif_HashLzwMatchesDictionaryReference };
            yield return new TestCase { Name = "Streaming_Gif_PaletteIndicesMatchRedrawReference", Action = Gif_PaletteIndicesMatchRedrawReference };
            yield return new TestCase { Name = "Idle_InputActivityWakesAndTracksHeldKeys", Action = Idle_InputActivityWakesAndTracksHeldKeys };
            yield return new TestCase { Name = "Idle_PlayerIsRestingOnlyWhenStill", Action = Idle_PlayerIsRestingOnlyWhenStill };
            yield return new TestCase { Name = "Idle_SecondsToNextFrameFollowsClip", Action = Idle_SecondsToNextFrameFollowsClip };
            yield return new TestCase { Name = "Streaming_Canvas_CloneImageRegionIsIndependentAndClamped", Action = Canvas_CloneImageRegionIsIndependentAndClamped };
            yield return new TestCase { Name = "Streaming_LruCache_EvictsOldestWithinBudget", Action = LruCache_EvictsOldestWithinBudget };
            yield return new TestCase { Name = "Streaming_ChoosePreviewShrink", Action = ChoosePreviewShrink };
            yield return new TestCase { Name = "Streaming_ChoosePreviewSheetScale_FitsLimits", Action = ChoosePreviewSheetScale_FitsLimits };
            yield return new TestCase { Name = "Streaming_Pipeline_ShrinksPreviewButKeepsNominalSize", Action = Pipeline_ShrinksPreviewButKeepsNominalSize };
            yield return new TestCase { Name = "Streaming_Pipeline_SourceCacheStaysWithinBudget", Action = Pipeline_SourceCacheStaysWithinBudget };
            yield return new TestCase { Name = "Streaming_Pipeline_LoadOneMatchesLoad", Action = Pipeline_LoadOneMatchesLoad };
            yield return new TestCase { Name = "Streaming_Canvas_WorldSizeDrivesHitTestAndZoom", Action = Canvas_WorldSizeDrivesHitTestAndZoom };
        }

        private static string Temp(string extension)
        {
            return Path.Combine(Path.GetTempPath(), "stream_test_" + Guid.NewGuid().ToString("N") + extension);
        }

        // ランダムな画素（透明含む）を LockBits で直接書き込む。
        private static Bitmap RandomBitmap(int width, int height, int seed)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rng = new Random(seed);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // 単色の面とノイズを混ぜ、どのフィルターの行も出るようにする。
                        bool flat = ((x / 8) + (y / 8)) % 3 == 0;
                        row[x * 4] = flat ? (byte)40 : (byte)rng.Next(256);
                        row[x * 4 + 1] = flat ? (byte)80 : (byte)rng.Next(256);
                        row[x * 4 + 2] = flat ? (byte)120 : (byte)rng.Next(256);
                        row[x * 4 + 3] = flat ? (byte)255 : (byte)rng.Next(256);
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private static byte[] Bytes(Bitmap bitmap)
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bitmap.Width * 4;
                var bytes = new byte[rowBytes * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * rowBytes, rowBytes);
                return bytes;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        // 元画像の y0 から h 行を、画素を変えずにそのまま切り出す（DrawImage は透明度のある画素を丸めるため使わない）。
        private static SheetBandRenderer BandsOf(Bitmap source)
        {
            return (y0, h) =>
            {
                byte[] all = Bytes(source);
                var band = new Bitmap(source.Width, h, PixelFormat.Format32bppArgb);
                int rowBytes = source.Width * 4;
                BitmapData data = band.LockBits(new Rectangle(0, 0, source.Width, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(all, (y0 + y) * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                }
                finally
                {
                    band.UnlockBits(data);
                }
                return band;
            };
        }

        private static void WithBandBudget(long bytes, Action action)
        {
            long previous = SheetStreaming.BandBudgetBytes;
            SheetStreaming.BandBudgetBytes = bytes;
            try { action(); }
            finally { SheetStreaming.BandBudgetBytes = previous; }
        }

        private static void Png_MultiBandRoundTripsExactPixels()
        {
            string path = Temp(".png");
            try
            {
                using (Bitmap source = RandomBitmap(97, 61, 7))
                {
                    byte[] expected = Bytes(source);
                    // 1帯 = 5行 → 13帯に分かれる
                    WithBandBudget(97L * 4 * 5, () =>
                        PngStreamWriter.Save(path, source.Width, source.Height, BandsOf(source), null, CancellationToken.None));
                    using (var decoded = new Bitmap(path))
                    {
                        Assert.AreEqual(97, decoded.Width, "width");
                        Assert.AreEqual(61, decoded.Height, "height");
                        // GDI+は完全に透明な画素の色を保持しないことがあるため、A=0の画素は色を比べない。
                        byte[] actual = Bytes(decoded);
                        for (int i = 0; i < expected.Length; i += 4)
                        {
                            Assert.AreEqual(expected[i + 3], actual[i + 3], "alpha at byte " + i);
                            if (expected[i + 3] == 0) continue;
                            Assert.AreEqual(expected[i], actual[i], "B at byte " + i);
                            Assert.AreEqual(expected[i + 1], actual[i + 1], "G at byte " + i);
                            Assert.AreEqual(expected[i + 2], actual[i + 2], "R at byte " + i);
                        }
                    }
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // 帯の切れ目の位置にかかわらず、GDI+で読めて同じ内容になること（Upフィルターが帯をまたぐ）。
        private static void Png_ReadableByGdiForManyBandHeights()
        {
            using (Bitmap source = RandomBitmap(33, 40, 3))
            {
                byte[] expected = Bytes(source);
                foreach (int rowsPerBand in new[] { 1, 2, 7, 40, 100 })
                {
                    string path = Temp(".png");
                    try
                    {
                        WithBandBudget(33L * 4 * rowsPerBand, () =>
                            PngStreamWriter.Save(path, source.Width, source.Height, BandsOf(source), null, CancellationToken.None));
                        using (var decoded = new Bitmap(path))
                        {
                            byte[] actual = Bytes(decoded);
                            for (int i = 0; i < expected.Length; i += 4)
                            {
                                if (expected[i + 3] == 0) continue;
                                if (expected[i] != actual[i] || expected[i + 1] != actual[i + 1] || expected[i + 2] != actual[i + 2] ||
                                    expected[i + 3] != actual[i + 3])
                                    throw new AssertFailedException("pixel differs at byte " + i + " with " + rowsPerBand + " rows per band");
                            }
                        }
                    }
                    finally
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
            }
        }

        private static void Tga_MatchesTgaWriterByteForByte()
        {
            string expectedPath = Temp(".tga");
            string actualPath = Temp(".tga");
            try
            {
                using (Bitmap source = RandomBitmap(50, 37, 11))
                {
                    TgaWriter.Save32Bit(expectedPath, source);
                    WithBandBudget(50L * 4 * 6, () =>
                        TgaStreamWriter.Save(actualPath, source.Width, source.Height, BandsOf(source), null, CancellationToken.None));
                }
                byte[] expected = File.ReadAllBytes(expectedPath);
                byte[] actual = File.ReadAllBytes(actualPath);
                Assert.AreEqual(expected.Length, actual.Length, "file length");
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i]) throw new AssertFailedException("TGA bytes differ at " + i);
            }
            finally
            {
                if (File.Exists(expectedPath)) File.Delete(expectedPath);
                if (File.Exists(actualPath)) File.Delete(actualPath);
            }
        }

        private static void Tga_RejectsTooLarge()
        {
            string path = Temp(".tga");
            bool threw = false;
            try
            {
                TgaStreamWriter.Save(path, 70000, 10, (y0, h) => { throw new InvalidOperationException("must not render"); },
                    null, CancellationToken.None);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "TGA wider than 65535 must be rejected");
            Assert.IsFalse(File.Exists(path), "no file for a rejected size");
        }

        private static void Cancel_LeavesNoPartialFileAndKeepsExisting()
        {
            string path = Temp(".png");
            try
            {
                File.WriteAllText(path, "existing");
                using (Bitmap source = RandomBitmap(20, 20, 5))
                using (var cancel = new CancellationTokenSource())
                {
                    SheetBandRenderer renderer = BandsOf(source);
                    bool canceled = false;
                    WithBandBudget(20L * 4 * 2, () =>
                    {
                        try
                        {
                            PngStreamWriter.Save(path, 20, 20, (y0, h) =>
                            {
                                if (y0 >= 6) cancel.Cancel();
                                return renderer(y0, h);
                            }, null, cancel.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                        }
                    });
                    Assert.IsTrue(canceled, "cancellation should surface");
                }
                Assert.AreEqual("existing", File.ReadAllText(path), "existing file is untouched by a canceled export");
                Assert.IsFalse(File.Exists(path + ".part"), "partial file is removed");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".part")) File.Delete(path + ".part");
            }
        }

        private static void Gif_FrameProviderMatchesListOverload()
        {
            string listPath = Temp(".gif");
            string streamPath = Temp(".gif");
            try
            {
                var frames = new List<Bitmap>();
                try
                {
                    for (int i = 0; i < 3; i++) frames.Add(RandomBitmap(16, 12, 20 + i));
                    GifWriter.SaveAnimatedGif(listPath, frames, 100);
                    int disposed = 0;
                    GifWriter.SaveAnimatedGif(streamPath, 16, 12, 3, index =>
                    {
                        return (Bitmap)frames[index].Clone();
                    }, true, 100, p => disposed++, CancellationToken.None);
                    Assert.AreEqual(3, disposed, "progress is reported per frame");
                }
                finally
                {
                    foreach (Bitmap frame in frames) frame.Dispose();
                }
                byte[] a = File.ReadAllBytes(listPath);
                byte[] b = File.ReadAllBytes(streamPath);
                Assert.AreEqual(a.Length, b.Length, "gif length");
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) throw new AssertFailedException("GIF bytes differ at " + i);
            }
            finally
            {
                if (File.Exists(listPath)) File.Delete(listPath);
                if (File.Exists(streamPath)) File.Delete(streamPath);
            }
        }

        // GIFの圧縮を辞書(Dictionary)版から表(ハッシュ)版へ替えても、出力が1バイトも変わらないこと。
        // ここに置いた ReferenceLzw が替える前の実装のコピー。
        private static void Gif_HashLzwMatchesDictionaryReference()
        {
            var method = typeof(GifWriter).GetMethod("CompressLzw", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue(method != null, "CompressLzw exists");
            var rng = new Random(99);
            var inputs = new List<byte[]>();
            inputs.Add(new byte[0]);
            inputs.Add(new byte[] { 5 });
            inputs.Add(new byte[5000]);                                   // ひたすら同じ値（辞書が伸びる）
            var noise = new byte[20000]; rng.NextBytes(noise); inputs.Add(noise);   // 辞書がいっぱいになり何度もクリアされる
            var mixed = new byte[60000];
            for (int i = 0; i < mixed.Length; i++) mixed[i] = (byte)((i / 37) % 7 == 0 ? rng.Next(256) : (i / 5) % 200);
            inputs.Add(mixed);
            foreach (byte[] input in inputs)
            {
                byte[] expected = ReferenceLzw(input, 8);
                byte[] actual = (byte[])method.Invoke(null, new object[] { input, 8 });
                Assert.AreEqual(expected.Length, actual.Length, "compressed length for input of " + input.Length);
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i]) throw new AssertFailedException("LZW output differs at " + i + " for input of " + input.Length);
            }
        }

        private static byte[] ReferenceLzw(byte[] input, int minimumCodeSize)
        {
            int clearCode = 1 << minimumCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minimumCodeSize + 1;
            var dictionary = new Dictionary<int, int>(4096);
            var bytes = new List<byte>();
            int buffer = 0, bitCount = 0;
            Action<int, int> write = (value, length) =>
            {
                buffer |= value << bitCount;
                bitCount += length;
                while (bitCount >= 8) { bytes.Add((byte)(buffer & 0xFF)); buffer >>= 8; bitCount -= 8; }
            };
            write(clearCode, codeSize);
            if (input.Length == 0)
            {
                write(endCode, codeSize);
                if (bitCount > 0) bytes.Add((byte)(buffer & 0xFF));
                return bytes.ToArray();
            }
            int prefix = input[0];
            for (int i = 1; i < input.Length; i++)
            {
                int suffix = input[i];
                int key = (prefix << 8) | suffix;
                int existing;
                if (dictionary.TryGetValue(key, out existing)) { prefix = existing; continue; }
                write(prefix, codeSize);
                if (nextCode < 4096)
                {
                    dictionary.Add(key, nextCode++);
                    if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
                }
                else
                {
                    write(clearCode, codeSize);
                    dictionary.Clear();
                    nextCode = endCode + 1;
                    codeSize = minimumCodeSize + 1;
                }
                prefix = suffix;
            }
            write(prefix, codeSize);
            write(endCode, codeSize);
            if (bitCount > 0) bytes.Add((byte)(buffer & 0xFF));
            return bytes.ToArray();
        }

        // パレット番号への変換を「描き直してから読む」方式から「直接読む」方式へ替えても、不透明・透明の画素は
        // 同じ番号になること。半透明の画素は、描き直す方式だとGDI+の丸めで色が±1階調ずれることがあり、直接読む方が
        // 元の色に忠実なため、その画素だけは番号が1段階ずれてもよいものとする。
        private static void Gif_PaletteIndicesMatchRedrawReference()
        {
            var method = typeof(GifWriter).GetMethod("ConvertToPaletteIndices", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue(method != null, "ConvertToPaletteIndices exists");
            using (Bitmap source = RandomBitmap(64, 48, 1234))
            {
                var actual = (byte[])method.Invoke(null, new object[] { source });
                byte[] expected;
                using (var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.Clear(Color.Transparent);
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.DrawImageUnscaled(source, 0, 0);
                    }
                    byte[] px = Bytes(copy);
                    expected = new byte[source.Width * source.Height];
                    for (int i = 0; i < expected.Length; i++)
                    {
                        int o = i * 4;
                        expected[i] = px[o + 3] < 128 ? (byte)0
                            : (byte)(1 + ((px[o + 2] * 5 + 127) / 255) * 42 + ((px[o + 1] * 6 + 127) / 255) * 6 + ((px[o] * 5 + 127) / 255));
                    }
                }
                Assert.AreEqual(expected.Length, actual.Length, "index count");
                int different = 0;
                for (int i = 0; i < expected.Length; i++)
                {
                    if (expected[i] == actual[i]) continue;
                    int alpha = Bytes(source)[i * 4 + 3];
                    if (alpha == 0 || alpha == 255) different++;
                    else if (actual[i] == 0 || expected[i] == 0) different++;   // 透明かどうかの判定は同じはず
                }
                Assert.AreEqual(0, different, "opaque/transparent pixels whose palette index differs");
            }
        }

        private static void Idle_InputActivityWakesAndTracksHeldKeys()
        {
            var input = new PreviewInputController();
            int wakes = 0;
            input.Activity += () => wakes++;
            Assert.IsFalse(input.AnyActive, "nothing is pressed at first");
            input.KeyDown(Keys.D);
            Assert.AreEqual(1, wakes, "a key press wakes the timer");
            Assert.IsTrue(input.AnyActive, "held key is active");
            input.MouseDown(MouseButtons.Left);
            Assert.AreEqual(2, wakes, "a mouse press also wakes the timer");
            input.Clear();
            Assert.IsFalse(input.AnyActive, "Clear releases everything");
        }

        private static void Idle_PlayerIsRestingOnlyWhenStill()
        {
            var player = new PlayerStateController();
            var input = new PreviewInputController();
            Func<PlayerAnimationState, float> duration = state => 0.3f;
            player.Update(0.016f, input, new Size(640, 360), new Size(64, 64), duration, 100, 300, 800);
            Assert.IsTrue(player.IsResting, "standing on the floor with no input is resting");
            input.KeyDown(Keys.Space);
            player.Update(0.016f, input, new Size(640, 360), new Size(64, 64), duration, 100, 300, 800);
            Assert.IsFalse(player.IsResting, "a jump is not resting");
        }

        private static void Idle_SecondsToNextFrameFollowsClip()
        {
            var clips = new Dictionary<PlayerAnimationState, AnimationClipSettings>();
            clips[PlayerAnimationState.Idle] = new AnimationClipSettings { Enabled = true, StartCell = 1, EndCell = 4, Fps = 8 };
            var animator = new SpriteAnimationController(clips);
            animator.Reset(PlayerAnimationState.Idle, 10);
            Assert.InRange(animator.GetSecondsToNextFrame(10), 0.124, 0.126, "a fresh 8 fps clip waits 1/8 s");
            animator.Update(0.05, PlayerAnimationState.Idle, 10);
            Assert.InRange(animator.GetSecondsToNextFrame(10), 0.074, 0.076, "0.05 s later 0.075 s remain");
            var empty = new SpriteAnimationController(new Dictionary<PlayerAnimationState, AnimationClipSettings>());
            empty.Reset(PlayerAnimationState.Idle, 10);
            Assert.InRange(empty.GetSecondsToNextFrame(10), 0.09, 0.11, "no clip falls back to 0.1 s");
        }

        private static void Canvas_CloneImageRegionIsIndependentAndClamped()
        {
            using (var canvas = new PreviewCanvas())
            using (var source = new Bitmap(40, 30, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(source)) g.Clear(Color.Red);
                canvas.SetImage(source, null);
                using (Bitmap part = canvas.CloneImageRegion(new Rectangle(10, 5, 100, 100)))
                {
                    Assert.AreEqual(30, part.Width, "width is clamped to the image");
                    Assert.AreEqual(25, part.Height, "height is clamped to the image");
                    Assert.AreEqual(Color.Red.ToArgb(), part.GetPixel(0, 0).ToArgb(), "pixels are copied");
                }
                using (Bitmap outside = canvas.CloneImageRegion(new Rectangle(500, 500, 10, 10)))
                    Assert.AreEqual(1, outside.Width, "an outside region gives a 1x1 placeholder");
                canvas.SetImage(null, null);
                Assert.IsTrue(canvas.CloneImageRegion(new Rectangle(0, 0, 5, 5)) == null, "no image gives null");
            }
        }

        private static void LruCache_EvictsOldestWithinBudget()
        {
            // 1枚 10x10x4 = 400バイト、上限 1000バイト → 同時に2枚まで。
            using (var cache = new BitmapLruCache(1000))
            {
                int loads = 0;
                Func<int, Bitmap> get = key => cache.Get(key, () => { loads++; return new Bitmap(10, 10); });
                get(1); get(2);
                Assert.AreEqual(2, cache.Count, "two entries fit");
                get(1);                        // 1 を最近使ったことにする
                get(3);                        // 2 が捨てられる
                Assert.AreEqual(2, cache.Count, "still within budget");
                Assert.IsTrue(cache.TotalBytes <= 1000, "total within budget");
                int before = loads;
                get(1);
                Assert.AreEqual(before, loads, "1 stayed cached");
                get(2);
                Assert.AreEqual(before + 1, loads, "2 was evicted and reloaded");
            }
        }

        private static void ChoosePreviewShrink()
        {
            Assert.AreEqual(1, SpriteImagePipeline.ChoosePreviewShrink(1000, 4000), "under budget");
            Assert.AreEqual(1, SpriteImagePipeline.ChoosePreviewShrink(4000, 4000), "exactly the budget");
            Assert.AreEqual(2, SpriteImagePipeline.ChoosePreviewShrink(16000, 4000), "4x pixels needs 1/2 each side");
            Assert.AreEqual(3, SpriteImagePipeline.ChoosePreviewShrink(16001, 4000), "just over rounds up");
            long total = 16L * 4096 * 4096;  // 16枚の4096px
            int shrink = SpriteImagePipeline.ChoosePreviewShrink(total);
            Assert.IsTrue(total / ((long)shrink * shrink) <= SpriteImagePipeline.PreviewPixelBudget, "budget respected");
            Assert.IsTrue(shrink >= 2, "large sets shrink");
        }

        private static void ChoosePreviewSheetScale_FitsLimits()
        {
            Assert.AreEqual(1, MainForm.ChoosePreviewSheetScale(1024, 768, 1), "small sheet is unscaled");
            Assert.AreEqual(3, MainForm.ChoosePreviewSheetScale(1024, 768, 3), "never below the load-time shrink");
            foreach (var size in new[] { new Size(16384, 16384), new Size(40000, 500), new Size(500, 100000), new Size(60000, 60000) })
            {
                int scale = MainForm.ChoosePreviewSheetScale(size.Width, size.Height, 1);
                long w = (size.Width + scale - 1) / scale;
                long h = (size.Height + scale - 1) / scale;
                Assert.IsTrue(w <= 32767 && h <= 32767, "GDI+ dimension limit for " + size);
                Assert.IsTrue(w * h <= 64000000, "pixel budget for " + size);
            }
        }

        private static string SaveSolid(int width, int height, Color color)
        {
            string path = Temp(".png");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
                bitmap.Save(path, ImageFormat.Png);
            }
            return path;
        }

        private static List<LoadFolderRequest> Request(IEnumerable<string> paths)
        {
            var folder = new LoadFolderRequest { Name = "f" };
            foreach (string path in paths) folder.Items.Add(new LoadItemRequest { Source = null, Path = path });
            return new List<LoadFolderRequest> { folder };
        }

        private static void Pipeline_ShrinksPreviewButKeepsNominalSize()
        {
            var paths = new List<string>();
            try
            {
                for (int i = 0; i < 4; i++) paths.Add(SaveSolid(64, 48, Color.CornflowerBlue));
                var pipeline = new SpriteImagePipeline();
                pipeline.PreviewPixelBudgetValue = 4000;   // 4 x 3072 = 12288 画素 → 1/2 に縮小すれば 3072 画素で予算内
                List<LoadedFolder> loaded = pipeline.Load(Request(paths), 1, false,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                try
                {
                    LoadedItem item = loaded[0].Items[0];
                    Assert.AreEqual(2, item.Shrink, "shrink");
                    Assert.AreEqual(new Size(64, 48), item.NominalSize, "nominal size is the export size");
                    Assert.AreEqual(32, item.Bitmap.Width, "preview bitmap is shrunk");
                    Assert.AreEqual(24, item.Bitmap.Height, "preview bitmap is shrunk");
                }
                finally
                {
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }

                // 大きさだけの一覧は画像を読み込まない。
                List<LoadedFolder> sizes = pipeline.LoadSizes(Request(paths), 2);
                Assert.AreEqual(new Size(32, 24), sizes[0].Items[0].NominalSize, "nominal size with divisor 2");
                Assert.IsTrue(sizes[0].Items[0].Bitmap == null, "no bitmap is loaded for sizes only");
            }
            finally
            {
                foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Pipeline_SourceCacheStaysWithinBudget()
        {
            var paths = new List<string>();
            try
            {
                for (int i = 0; i < 6; i++) paths.Add(SaveSolid(32, 32, Color.OrangeRed));   // 各 4096 バイト
                var pipeline = new SpriteImagePipeline();
                pipeline.SourceCacheBudgetValue = 4096 * 2 + 10;                              // 2枚まで
                List<LoadedFolder> loaded = pipeline.Load(Request(paths), 1, false,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                SpriteImagePipeline.DisposeLoaded(loaded);
                Assert.IsTrue(pipeline.CachedSourceBytes <= pipeline.SourceCacheBudgetValue, "cache bytes within budget");
                Assert.IsTrue(pipeline.CachedSourceCount <= 2, "cache keeps at most 2 entries");
                // 捨てられた分は読み直せる（結果は変わらない）。
                loaded = pipeline.Load(Request(paths), 1, false, SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                try
                {
                    Assert.AreEqual(6, loaded[0].Items.Count, "all items reload after eviction");
                    Assert.AreEqual(Color.OrangeRed.ToArgb(), loaded[0].Items[0].Bitmap.GetPixel(3, 3).ToArgb(), "pixels are intact");
                }
                finally
                {
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }
            }
            finally
            {
                foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Pipeline_LoadOneMatchesLoad()
        {
            string path = SaveSolid(40, 30, Color.Black);
            try
            {
                var pipeline = new SpriteImagePipeline();
                List<LoadedFolder> loaded = pipeline.Load(Request(new[] { path }), 2, true,
                    SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                Bitmap one = SpriteImagePipeline.LoadOne(path, 2, true, SpriteColorBlendMode.Multiply, Color.White, 100, CancellationToken.None);
                try
                {
                    byte[] a = Bytes(loaded[0].Items[0].Bitmap);
                    byte[] b = Bytes(one);
                    Assert.AreEqual(a.Length, b.Length, "same size");
                    for (int i = 0; i < a.Length; i++)
                        if (a[i] != b[i]) throw new AssertFailedException("LoadOne differs from Load at byte " + i);
                }
                finally
                {
                    one.Dispose();
                    SpriteImagePipeline.DisposeLoaded(loaded);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Canvas_WorldSizeDrivesHitTestAndZoom()
        {
            using (var canvas = new PreviewCanvas())
            {
                canvas.Size = new Size(400, 300);
                // 実際の画像は 100x50 だが、表すシートの大きさは 400x200（4倍に縮小したプレビュー）。
                canvas.SetImage(new Bitmap(100, 50), new List<PreviewRect>
                {
                    new PreviewRect { Rect = new Rectangle(0, 0, 400, 200), Number = 1 }
                }, new Size(400, 200));
                RectangleF whole = canvas.SheetToScreen(new RectangleF(0, 0, 400, 200));
                Assert.IsTrue(whole.Width <= 400.5f, "fit zoom is based on the represented size, not the bitmap");
                RectangleF a = canvas.SheetToScreen(new RectangleF(350, 150, 0, 0));
                RectangleF b = canvas.SheetToScreen(new RectangleF(450, 150, 0, 0));
                Point inside = new Point((int)a.X, (int)a.Y);
                Point outside = new Point((int)b.X, (int)b.Y);
                Assert.IsFalse(canvas.IsOutsideSheet(inside), "inside the represented sheet even beyond the bitmap size");
                Assert.IsTrue(canvas.IsOutsideSheet(outside), "outside the represented sheet");
            }
        }
    }
}
