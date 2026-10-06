using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpriteSheetMaker
{
    public static class GifWriter
    {
        private const int TransparentIndex = 0;
        private const int PaletteSize = 256;

        public static void SaveAnimatedGif(string path, IList<Bitmap> frames, int delayMs)
        {
            if (frames == null || frames.Count == 0)
                throw new ArgumentException("There are no GIF frames.", "frames");

            int width = frames[0].Width;
            int height = frames[0].Height;
            foreach (Bitmap frame in frames)
            {
                if (frame.Width != width || frame.Height != height)
                    throw new ArgumentException("All GIF frames must have the same size.", "frames");
            }

            SaveAnimatedGif(path, width, height, frames.Count, index => frames[index], false, delayMs, null, CancellationToken.None);
        }

        // フレームを1枚ずつ作って書き出す。全フレームを同時にメモリへ持たずに済む。
        // getFrame(i) は width x height のビットマップを返す。disposeFrames が true のときは書き出した直後に破棄する。
        public static void SaveAnimatedGif(string path, int width, int height, int frameCount,
            Func<int, Bitmap> getFrame, bool disposeFrames, int delayMs,
            Action<double> progress, CancellationToken cancellationToken)
        {
            SaveAnimatedGif(path, width, height, frameCount, getFrame, disposeFrames, i => delayMs, progress, cancellationToken);
        }

        // コマごとに表示時間（ミリ秒）を変えられる形。GIF は1/100秒単位なので四捨五入する。
        public static void SaveAnimatedGif(string path, int width, int height, int frameCount,
            Func<int, Bitmap> getFrame, bool disposeFrames, Func<int, int> delayMsOf,
            Action<double> progress, CancellationToken cancellationToken)
        {
            if (frameCount <= 0)
                throw new ArgumentException("There are no GIF frames.", "frameCount");
            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
                throw new ArgumentOutOfRangeException("width", "The GIF size is out of range.");

            // 1回目: 全コマで使われている色を数え、共通の色表を作る（コマ間で色がちらつかない）。
            // 255色以下ならそのままの色、超えるときだけ減色する（ディザはかけない）。
            var counter = new ColorCounter();
            for (int i = 0; i < frameCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Bitmap frame = getFrame(i);
                try
                {
                    if (frame.Width != width || frame.Height != height)
                        throw new ArgumentException("All GIF frames must have the same size.", "getFrame");
                    counter.Add(frame);
                }
                finally
                {
                    if (disposeFrames) frame.Dispose();
                }
                if (progress != null) progress((i + 1) / (double)frameCount * 0.5);
            }
            var palette = new GifPalette(counter.Build(PaletteSize - 1));

            // 2回目: 作り直したコマを、その色表の番号にして書く。
            SheetStreaming.WriteAtomically(path, stream =>
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
                {
                    WriteHeader(writer, width, height);
                    WriteGlobalPalette(writer, palette);
                    WriteLoopExtension(writer);
                    for (int i = 0; i < frameCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Bitmap frame = getFrame(i);
                        try
                        {
                            if (frame.Width != width || frame.Height != height)
                                throw new ArgumentException("All GIF frames must have the same size.", "getFrame");
                            int delay = Math.Max(1, Math.Min(ushort.MaxValue, (int)Math.Round(Math.Max(1, delayMsOf(i)) / 10.0)));
                            WriteGraphicControlExtension(writer, delay);
                            WriteImageDescriptor(writer, width, height);
                            WriteImageData(writer, ConvertToPaletteIndices(frame, palette));
                        }
                        finally
                        {
                            if (disposeFrames) frame.Dispose();
                        }
                        if (progress != null) progress(0.5 + (i + 1) / (double)frameCount * 0.5);
                    }
                    writer.Write((byte)0x3B);
                }
            });
        }

        private static void WriteHeader(BinaryWriter writer, int width, int height)
        {
            writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0xF7);
            writer.Write((byte)TransparentIndex);
            writer.Write((byte)0);
        }

        // 0番は透明。1番から使う色を並べ、残りは黒で埋める。
        private static void WriteGlobalPalette(BinaryWriter writer, GifPalette palette)
        {
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            for (int index = 1; index < PaletteSize; index++)
            {
                int color = index - 1 < palette.Colors.Count ? palette.Colors[index - 1] : 0;
                writer.Write((byte)(color >> 16)); writer.Write((byte)(color >> 8)); writer.Write((byte)color);
            }
        }

        private static void WriteLoopExtension(BinaryWriter writer)
        {
            writer.Write((byte)0x21);
            writer.Write((byte)0xFF);
            writer.Write((byte)0x0B);
            writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            writer.Write((byte)0x03);
            writer.Write((byte)0x01);
            writer.Write((ushort)0);
            writer.Write((byte)0x00);
        }

        private static void WriteGraphicControlExtension(BinaryWriter writer, int delay)
        {
            writer.Write((byte)0x21);
            writer.Write((byte)0xF9);
            writer.Write((byte)0x04);
            writer.Write((byte)0x09);
            writer.Write((ushort)delay);
            writer.Write((byte)TransparentIndex);
            writer.Write((byte)0x00);
        }

        private static void WriteImageDescriptor(BinaryWriter writer, int width, int height)
        {
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0x00);
        }

        private static byte[] ConvertToPaletteIndices(Bitmap source, GifPalette palette)
        {
            // すでに32bit ARGBなら、そのまま画素を読む（描き直すと大きな画像で数秒かかる）。
            Bitmap normalized = null;
            Bitmap pixelSource = source;
            if (source.PixelFormat != PixelFormat.Format32bppArgb)
            {
                normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(normalized))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(source, 0, 0);
                }
                pixelSource = normalized;
            }

            try
            {
                int width = pixelSource.Width;
                int height = pixelSource.Height;
                var indices = new byte[width * height];
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = pixelSource.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = width * 4;
                    var row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                        int output = y * width;
                        for (int offset = 0; offset < rowBytes; offset += 4)
                        {
                            indices[output++] = row[offset + 3] < 128
                                ? (byte)TransparentIndex
                                : palette.IndexOf((row[offset + 2] << 16) | (row[offset + 1] << 8) | row[offset]);
                        }
                    }
                }
                finally
                {
                    pixelSource.UnlockBits(data);
                }
                return indices;
            }
            finally
            {
                if (normalized != null) normalized.Dispose();
            }
        }

        private static void WriteImageData(BinaryWriter writer, byte[] indices)
        {
            const int minimumCodeSize = 8;
            writer.Write((byte)minimumCodeSize);
            byte[] compressed = CompressLzw(indices, minimumCodeSize);
            int offset = 0;
            while (offset < compressed.Length)
            {
                int length = Math.Min(255, compressed.Length - offset);
                writer.Write((byte)length);
                writer.Write(compressed, offset, length);
                offset += length;
            }
            writer.Write((byte)0x00);
        }

        private static byte[] CompressLzw(byte[] input, int minimumCodeSize)
        {
            int clearCode = 1 << minimumCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minimumCodeSize + 1;
            // 辞書は Dictionary ではなく固定長の開番地ハッシュ表にする（大きな画像で数倍速い）。
            // 符号は最大4096個なので、表は8192個あれば半分以下しか埋まらない。
            const int HashSize = 1 << 13;
            var hashKeys = new int[HashSize];
            var hashValues = new int[HashSize];
            for (int h = 0; h < HashSize; h++) hashKeys[h] = -1;
            var bits = new LsbBitWriter(input.Length / 3 + 16);

            bits.Write(clearCode, codeSize);
            if (input.Length == 0)
            {
                bits.Write(endCode, codeSize);
                return bits.ToArray();
            }

            int prefix = input[0];
            for (int i = 1; i < input.Length; i++)
            {
                int suffix = input[i];
                int key = (prefix << 8) | suffix;
                int slot = (int)(((uint)key * 2654435761u) >> 19);
                while (hashKeys[slot] != -1 && hashKeys[slot] != key) slot = (slot + 1) & (HashSize - 1);
                if (hashKeys[slot] == key)
                {
                    prefix = hashValues[slot];
                    continue;
                }

                bits.Write(prefix, codeSize);
                if (nextCode < 4096)
                {
                    hashKeys[slot] = key;
                    hashValues[slot] = nextCode++;
                    // The decoder adds a dictionary entry one emitted code later than the encoder.
                    // Keep the old width for the boundary code, then widen on the following entry.
                    if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
                }
                else
                {
                    bits.Write(clearCode, codeSize);
                    for (int h = 0; h < HashSize; h++) hashKeys[h] = -1;
                    nextCode = endCode + 1;
                    codeSize = minimumCodeSize + 1;
                }
                prefix = suffix;
            }

            bits.Write(prefix, codeSize);
            bits.Write(endCode, codeSize);
            return bits.ToArray();
        }

        private sealed class LsbBitWriter
        {
            private readonly List<byte> bytes;
            private int buffer;
            private int bitCount;

            public LsbBitWriter(int capacity)
            {
                bytes = new List<byte>(Math.Max(16, capacity));
            }

            public void Write(int value, int length)
            {
                buffer |= value << bitCount;
                bitCount += length;
                while (bitCount >= 8)
                {
                    bytes.Add((byte)(buffer & 0xFF));
                    buffer >>= 8;
                    bitCount -= 8;
                }
            }

            public byte[] ToArray()
            {
                if (bitCount > 0) bytes.Add((byte)(buffer & 0xFF));
                return bytes.ToArray();
            }
        }

        //--------------
        // 色表
        //--------------
        // コマの不透明な画素の色を数える。色の種類が多すぎるとき（写真など）は、各色5ビットに丸めて数える。
        private sealed class ColorCounter
        {
            private const int ReduceAbove = 1 << 20;
            private Dictionary<int, long> counts = new Dictionary<int, long>();
            private bool reduced;

            private static int Reduce(int color)
            {
                int r = (color >> 16) & 0xF8, g = (color >> 8) & 0xF8, b = color & 0xF8;
                return ((r | r >> 5) << 16) | ((g | g >> 5) << 8) | (b | b >> 5);
            }

            public void Add(Bitmap frame)
            {
                using (Bitmap pixels = frame.PixelFormat == PixelFormat.Format32bppArgb ? null : new Bitmap(frame))
                {
                    Bitmap source = pixels ?? frame;
                    var rect = new Rectangle(0, 0, source.Width, source.Height);
                    BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int rowBytes = source.Width * 4; var row = new byte[rowBytes];
                        for (int y = 0; y < source.Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                            for (int offset = 0; offset < rowBytes; offset += 4)
                            {
                                if (row[offset + 3] < 128) continue;
                                int color = (row[offset + 2] << 16) | (row[offset + 1] << 8) | row[offset];
                                if (reduced) color = Reduce(color);
                                long count; counts.TryGetValue(color, out count); counts[color] = count + 1;
                            }
                            if (!reduced && counts.Count > ReduceAbove)
                            {
                                var merged = new Dictionary<int, long>();
                                foreach (var entry in counts) { int key = Reduce(entry.Key); long count; merged.TryGetValue(key, out count); merged[key] = count + entry.Value; }
                                counts = merged; reduced = true;
                            }
                        }
                    }
                    finally { source.UnlockBits(data); }
                }
            }

            // maximum 色以内の色表。収まるときはそのままの色、超えるときはメディアンカットで減らす。
            public List<int> Build(int maximum)
            {
                if (counts.Count <= maximum) return counts.OrderByDescending(e => e.Value).Select(e => e.Key).ToList();
                var boxes = new List<List<KeyValuePair<int, long>>> { counts.ToList() };
                while (boxes.Count < maximum)
                {
                    int best = -1, bestRange = 0, bestShift = 0;
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        if (boxes[i].Count < 2) continue;
                        foreach (int shift in new[] { 16, 8, 0 })
                        {
                            int min = 255, max = 0;
                            foreach (var entry in boxes[i]) { int v = (entry.Key >> shift) & 255; if (v < min) min = v; if (v > max) max = v; }
                            if (max - min > bestRange) { bestRange = max - min; best = i; bestShift = shift; }
                        }
                    }
                    if (best < 0) break;
                    List<KeyValuePair<int, long>> box = boxes[best];
                    int s = bestShift;
                    box.Sort((x, y) => ((x.Key >> s) & 255).CompareTo((y.Key >> s) & 255));
                    long total = box.Sum(e => e.Value), sum = 0; int split = 1;
                    for (int i = 0; i < box.Count; i++) { sum += box[i].Value; if (sum * 2 >= total) { split = Math.Max(1, Math.Min(box.Count - 1, i + 1)); break; } }
                    boxes[best] = box.GetRange(0, split); boxes.Add(box.GetRange(split, box.Count - split));
                }
                return boxes.Select(box =>
                {
                    long total = Math.Max(1, box.Sum(e => e.Value));
                    long r = 0, g = 0, b = 0;
                    foreach (var entry in box) { r += ((entry.Key >> 16) & 255) * entry.Value; g += ((entry.Key >> 8) & 255) * entry.Value; b += (entry.Key & 255) * entry.Value; }
                    return (int)(((r / total) << 16) | ((g / total) << 8) | (b / total));
                }).ToList();
            }
        }

        // 色 → 色表の番号（1から）。同じ色があればその番号、なければ一番近い色（結果は覚えておく）。
        private sealed class GifPalette
        {
            public readonly List<int> Colors;
            private readonly Dictionary<int, byte> lookup = new Dictionary<int, byte>();
            public GifPalette(List<int> colors)
            {
                Colors = colors;
                for (int i = 0; i < colors.Count; i++) if (!lookup.ContainsKey(colors[i])) lookup[colors[i]] = (byte)(i + 1);
            }
            public byte IndexOf(int color)
            {
                byte index;
                if (lookup.TryGetValue(color, out index)) return index;
                int r = (color >> 16) & 255, g = (color >> 8) & 255, b = color & 255, best = 0, bestDistance = int.MaxValue;
                for (int i = 0; i < Colors.Count; i++)
                {
                    int c = Colors[i], dr = ((c >> 16) & 255) - r, dg = ((c >> 8) & 255) - g, db = (c & 255) - b;
                    int distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (distance < bestDistance) { bestDistance = distance; best = i; }
                }
                index = (byte)(best + 1);
                if (lookup.Count < (1 << 20)) lookup[color] = index;
                return index;
            }
        }
    }
}
