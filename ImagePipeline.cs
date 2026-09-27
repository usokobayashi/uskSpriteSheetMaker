using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpriteSheetMaker
{
    // フォルダ／画像一覧から、縮小・黒透過・カラー調整を適用したビットマップ一式を
    // 組み立てる処理をMainFormから切り出したもの。UIコントロールには一切触れず、
    // すべての設定値を引数として受け取る。デコード＋縮小結果はパス単位でキャッシュし、
    // 色調整だけの再計算で毎回ディスクから再デコードしないようにする。
    internal sealed class SpriteImagePipeline
    {
        private readonly Dictionary<string, CachedSourceBitmap> sourceBitmapCache =
            new Dictionary<string, CachedSourceBitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly object sourceBitmapCacheLock = new object();
        private readonly Dictionary<string, CachedImageSize> imageSizeCache =
            new Dictionary<string, CachedImageSize>(StringComparer.OrdinalIgnoreCase);
        private long cacheBytes;
        private long useCounter;
        // 上の2つの上限。テストで小さな値へ差し替えられるよう、インスタンスの値として持つ。
        internal long PreviewPixelBudgetValue = PreviewPixelBudget;
        internal long SourceCacheBudgetValue = SourceCacheBudgetBytes;

        // プレビューが保持する画像の合計（縮小後の画素数）の目安。これを超える大きさになる場合は、
        // プレビューだけをさらに縮小して読み込む（書き出しは原寸のまま）。
        public const long PreviewPixelBudget = 40L * 1000 * 1000;

        // デコードして縮小した元画像を覚えておく上限（バイト）。超えたら使われていない古いものから捨てる。
        public const long SourceCacheBudgetBytes = 256L * 1024 * 1024;

        // 全画像の合計画素数が予算に収まるよう、プレビュー用の追加の縮小率（1以上の整数）を決める。
        public static int ChoosePreviewShrink(long totalPixels)
        {
            return ChoosePreviewShrink(totalPixels, PreviewPixelBudget);
        }

        internal static int ChoosePreviewShrink(long totalPixels, long budget)
        {
            if (totalPixels <= budget) return 1;
            return (int)Math.Ceiling(Math.Sqrt(totalPixels / (double)budget));
        }

        // 1枚あたりの元画像の画素数の上限。これを超える画像は、デコードでメモリを使い切るおそれがあるため読み込まない
        // （小さなファイルで巨大な寸法を宣言する画像への対策）。16384 x 16384 = 268,435,456 まで。
        public const long MaxSourcePixels = 16384L * 16384L;

        // 元画像の大きさ。中身は読み込まず、ヘッダーだけ読む。
        internal Size GetOriginalSize(string path)
        {
            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);
            lock (sourceBitmapCacheLock)
            {
                CachedImageSize cached;
                if (imageSizeCache.TryGetValue(path, out cached) && cached.SourceWriteTimeUtc == writeTimeUtc)
                    return cached.Size;
            }
            Size original = ReadHeaderSize(path);
            lock (sourceBitmapCacheLock)
                imageSizeCache[path] = new CachedImageSize { SourceWriteTimeUtc = writeTimeUtc, Size = original };
            return original;
        }

        internal static void EnsureDecodable(string path, Size original)
        {
            if ((long)original.Width * original.Height > MaxSourcePixels)
                throw new ImageTooLargeException(path, original);
        }

        // 画像の縮小後の大きさ（書き出しの大きさ）。
        public Size GetNominalSize(string path, int divisor)
        {
            divisor = Math.Max(1, divisor);
            Size original = GetOriginalSize(path);
            EnsureDecodable(path, original);
            return new Size(Math.Max(1, original.Width / divisor), Math.Max(1, original.Height / divisor));
        }

        // 画像を読み込まずに、大きさだけを入れた一覧を作る（書き出しの配置の計算用）。
        public List<LoadedFolder> LoadSizes(IList<LoadFolderRequest> request, int scaleDivisor)
        {
            var result = new List<LoadedFolder>();
            foreach (LoadFolderRequest folder in request)
            {
                var loadedFolder = new LoadedFolder { Name = folder.Name };
                result.Add(loadedFolder);
                foreach (LoadItemRequest item in folder.Items)
                    loadedFolder.Items.Add(new LoadedItem { Source = item.Source, NominalSize = GetNominalSize(item.Path, scaleDivisor) });
            }
            return result;
        }

        // 画像でないファイル・壊れたファイルは、GDI+の分かりにくい例外ではなく、ファイル名つきの説明にする。
        private static Size ReadHeaderSize(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Image image = Image.FromStream(stream, false, false))
                    return image.Size;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException || ex is ExternalException)
            {
                throw new InvalidOperationException(Loc.T("error.imageUnreadable", System.IO.Path.GetFileName(path)), ex);
            }
        }

        private static Bitmap OpenBitmap(string path)
        {
            try
            {
                return new Bitmap(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException || ex is ExternalException)
            {
                throw new InvalidOperationException(Loc.T("error.imageUnreadable", System.IO.Path.GetFileName(path)), ex);
            }
        }

        // 1枚だけ、縮小・黒透過・カラー調整を適用して読み込む。キャッシュは使わない（書き出し用）。
        public static Bitmap LoadOne(string path, int scaleDivisor, bool convertBlack,
            SpriteColorBlendMode blendMode, Color adjustmentColor, int adjustmentStrength,
            CancellationToken cancellationToken)
        {
            EnsureDecodable(path, ReadHeaderSize(path));
            Bitmap bitmap;
            using (Bitmap source = OpenBitmap(path))
                bitmap = CreateScaledBitmap(source, scaleDivisor);
            try
            {
                if (convertBlack) ConvertBlackToAlpha(bitmap, cancellationToken);
                ApplySpriteColorAdjustment(bitmap, blendMode, adjustmentColor, adjustmentStrength, cancellationToken);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public List<LoadedFolder> Load(
            IList<LoadFolderRequest> request, int scaleDivisor, bool convertBlack,
            SpriteColorBlendMode blendMode, Color adjustmentColor, int adjustmentStrength,
            CancellationToken cancellationToken)
        {
            var result = new List<LoadedFolder>();

            try
            {
                // 全体が大きすぎるときは、プレビュー用にさらに縮小して読み込む。
                var sizes = new Dictionary<LoadItemRequest, Size>();
                long totalPixels = 0;
                foreach (LoadFolderRequest folder in request)
                {
                    foreach (LoadItemRequest item in folder.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Size size = GetNominalSize(item.Path, scaleDivisor);
                        sizes[item] = size;
                        totalPixels += (long)size.Width * size.Height;
                    }
                }
                int shrink = ChoosePreviewShrink(totalPixels, PreviewPixelBudgetValue);

                foreach (LoadFolderRequest folder in request)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var loadedFolder = new LoadedFolder { Name = folder.Name };
                    result.Add(loadedFolder);

                    foreach (LoadItemRequest item in folder.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Bitmap loadedBitmap = GetScaledSourceBitmapClone(item.Path, scaleDivisor * shrink, cancellationToken);
                        try
                        {
                            if (convertBlack) ConvertBlackToAlpha(loadedBitmap, cancellationToken);
                            ApplySpriteColorAdjustment(loadedBitmap, blendMode, adjustmentColor,
                                adjustmentStrength, cancellationToken);
                            loadedFolder.Items.Add(new LoadedItem
                            {
                                Source = item.Source,
                                Bitmap = loadedBitmap,
                                NominalSize = sizes[item],
                                Shrink = shrink
                            });
                            loadedBitmap = null;
                        }
                        finally
                        {
                            if (loadedBitmap != null) loadedBitmap.Dispose();
                        }
                    }
                }

                PruneSourceBitmapCache(EnumeratePaths(request));
                return result;
            }
            catch
            {
                DisposeLoaded(result);
                throw;
            }
        }

        private static IEnumerable<string> EnumeratePaths(IList<LoadFolderRequest> request)
        {
            foreach (LoadFolderRequest folder in request)
                foreach (LoadItemRequest item in folder.Items)
                    yield return item.Path;
        }

        // 呼び出し元は返された複製を自由に破壊的編集してよい（キャッシュ本体は変更しない）。
        private Bitmap GetScaledSourceBitmapClone(string path, int divisor, CancellationToken cancellationToken)
        {
            divisor = Math.Max(1, divisor);
            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(path);

            lock (sourceBitmapCacheLock)
            {
                CachedSourceBitmap cached;
                if (sourceBitmapCache.TryGetValue(path, out cached) &&
                    cached.Divisor == divisor && cached.SourceWriteTimeUtc == writeTimeUtc)
                {
                    cached.LastUse = ++useCounter;
                    return (Bitmap)cached.Bitmap.Clone();
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureDecodable(path, GetOriginalSize(path));
                Bitmap scaled;
                using (Bitmap source = OpenBitmap(path))
                {
                    scaled = CreateScaledBitmap(source, divisor);
                }

                if (cached != null)
                {
                    cacheBytes -= BytesOf(cached.Bitmap);
                    cached.Bitmap.Dispose();
                }
                sourceBitmapCache[path] = new CachedSourceBitmap
                {
                    Divisor = divisor,
                    SourceWriteTimeUtc = writeTimeUtc,
                    Bitmap = scaled,
                    LastUse = ++useCounter
                };
                cacheBytes += BytesOf(scaled);
                Bitmap clone = (Bitmap)scaled.Clone();
                TrimCache(path);
                return clone;
            }
        }

        private static long BytesOf(Bitmap bitmap)
        {
            return (long)bitmap.Width * bitmap.Height * 4;
        }

        // 上限を超えている間、いま入れたもの以外で一番使われていないものを捨てる。呼び出し側でロック済み。
        private void TrimCache(string keep)
        {
            while (cacheBytes > SourceCacheBudgetValue && sourceBitmapCache.Count > 1)
            {
                string oldestKey = null;
                long oldestUse = long.MaxValue;
                foreach (KeyValuePair<string, CachedSourceBitmap> entry in sourceBitmapCache)
                {
                    if (string.Equals(entry.Key, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Value.LastUse < oldestUse)
                    {
                        oldestUse = entry.Value.LastUse;
                        oldestKey = entry.Key;
                    }
                }
                if (oldestKey == null) break;
                cacheBytes -= BytesOf(sourceBitmapCache[oldestKey].Bitmap);
                sourceBitmapCache[oldestKey].Bitmap.Dispose();
                sourceBitmapCache.Remove(oldestKey);
            }
        }

        // テスト用: 覚えている元画像の数と合計バイト。
        internal int CachedSourceCount { get { lock (sourceBitmapCacheLock) return sourceBitmapCache.Count; } }
        internal long CachedSourceBytes { get { lock (sourceBitmapCacheLock) return cacheBytes; } }

        private void PruneSourceBitmapCache(IEnumerable<string> keepPaths)
        {
            var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
            lock (sourceBitmapCacheLock)
            {
                List<string> stale = null;
                foreach (KeyValuePair<string, CachedSourceBitmap> entry in sourceBitmapCache)
                {
                    if (!keep.Contains(entry.Key))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(entry.Key);
                    }
                }
                if (stale == null) return;
                foreach (string path in stale)
                {
                    cacheBytes -= BytesOf(sourceBitmapCache[path].Bitmap);
                    sourceBitmapCache[path].Bitmap.Dispose();
                    sourceBitmapCache.Remove(path);
                }
            }
        }

        public void ClearCache()
        {
            lock (sourceBitmapCacheLock)
            {
                foreach (CachedSourceBitmap cached in sourceBitmapCache.Values) cached.Bitmap.Dispose();
                sourceBitmapCache.Clear();
                imageSizeCache.Clear();
                cacheBytes = 0;
            }
        }

        public static void DisposeLoaded(List<LoadedFolder> loaded)
        {
            foreach (var folder in loaded)
            {
                foreach (var item in folder.Items)
                {
                    if (item.Bitmap != null) item.Bitmap.Dispose();
                }
            }
        }

        private static Bitmap CreateScaledBitmap(Bitmap source, int divisor)
        {
            divisor = Math.Max(1, divisor);

            int w = Math.Max(1, source.Width / divisor);
            int h = Math.Max(1, source.Height / divisor);

            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, w, h), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }

            return result;
        }

        internal static void ConvertBlackToAlpha(Bitmap bitmap, CancellationToken cancellationToken)
        {
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return;
            Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                for (int y = 0; y < bitmap.Height; y++)
                {
                    if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int row = y * stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int index = row + x * 4;
                        int blue = pixels[index];
                        int green = pixels[index + 1];
                        int red = pixels[index + 2];
                        int sourceAlpha = pixels[index + 3];
                        int brightness = Math.Max(red, Math.Max(green, blue));
                        if (brightness == 0 || sourceAlpha == 0)
                        {
                            pixels[index] = pixels[index + 1] = pixels[index + 2] = pixels[index + 3] = 0;
                            continue;
                        }

                        pixels[index] = (byte)Math.Min(255, blue * 255 / brightness);
                        pixels[index + 1] = (byte)Math.Min(255, green * 255 / brightness);
                        pixels[index + 2] = (byte)Math.Min(255, red * 255 / brightness);
                        pixels[index + 3] = (byte)(sourceAlpha * brightness / 255);
                    }
                }
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        internal static void ApplySpriteColorAdjustment(Bitmap bitmap, SpriteColorBlendMode mode,
            Color color, int strength, CancellationToken cancellationToken)
        {
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || strength <= 0) return;
            strength = Math.Max(0, Math.Min(100, strength));
            if (mode == SpriteColorBlendMode.Multiply && color.ToArgb() == Color.White.ToArgb()) return;
            if (mode == SpriteColorBlendMode.Add && color.R == 0 && color.G == 0 && color.B == 0) return;

            Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                int amount = strength;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int row = y * stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int index = row + x * 4;
                        if (pixels[index + 3] == 0) continue;
                        if (mode == SpriteColorBlendMode.Multiply)
                        {
                            pixels[index] = BlendByte(pixels[index], pixels[index] * color.B / 255, amount);
                            pixels[index + 1] = BlendByte(pixels[index + 1], pixels[index + 1] * color.G / 255, amount);
                            pixels[index + 2] = BlendByte(pixels[index + 2], pixels[index + 2] * color.R / 255, amount);
                        }
                        else
                        {
                            pixels[index] = (byte)Math.Min(255, pixels[index] + color.B * amount / 100);
                            pixels[index + 1] = (byte)Math.Min(255, pixels[index + 1] + color.G * amount / 100);
                            pixels[index + 2] = (byte)Math.Min(255, pixels[index + 2] + color.R * amount / 100);
                        }
                    }
                }
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static byte BlendByte(int source, int adjusted, int amount)
        {
            return (byte)Math.Max(0, Math.Min(255, (source * (100 - amount) + adjusted * amount + 50) / 100));
        }

        private sealed class CachedSourceBitmap
        {
            public DateTime SourceWriteTimeUtc;
            public int Divisor;
            public Bitmap Bitmap;
            public long LastUse;
        }

        private sealed class CachedImageSize
        {
            public DateTime SourceWriteTimeUtc;
            public Size Size;
        }
    }

    internal sealed class LoadedFolder
    {
        public string Name;
        public readonly List<LoadedItem> Items = new List<LoadedItem>();
    }

    internal sealed class LoadedItem
    {
        public MainForm.ImageItem Source;
        // 読み込んだ画像。プレビューでは Shrink 分だけ縮小してあり、書き出し用の配置計算では null。
        public Bitmap Bitmap;
        // 縮小率を適用した書き出し時の大きさ（Bitmap の大きさに Shrink を掛けたもの）。
        public Size NominalSize;
        // プレビュー用に追加で縮小した倍率（1 = 原寸）。
        public int Shrink = 1;
    }

    internal sealed class LoadFolderRequest
    {
        public string Name;
        public readonly List<LoadItemRequest> Items = new List<LoadItemRequest>();
    }

    internal sealed class LoadItemRequest
    {
        public MainForm.ImageItem Source;
        public string Path;
    }

    // 大きすぎて読み込めない画像。
    internal sealed class ImageTooLargeException : InvalidOperationException
    {
        public ImageTooLargeException(string path, Size size)
            : base(Loc.T("error.imageTooLarge", System.IO.Path.GetFileName(path), size.Width, size.Height))
        {
        }
    }
}
