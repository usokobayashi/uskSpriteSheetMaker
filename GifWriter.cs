using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
            if (frameCount <= 0)
                throw new ArgumentException("There are no GIF frames.", "frameCount");
            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
                throw new ArgumentOutOfRangeException("width", "The GIF size is out of range.");

            SheetStreaming.WriteAtomically(path, stream =>
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
                {
                    WriteHeader(writer, width, height);
                    WriteGlobalPalette(writer);
                    WriteLoopExtension(writer);
                    int delay = Math.Max(1, Math.Min(ushort.MaxValue,
                        (int)Math.Round(Math.Max(1, delayMs) / 10.0)));

                    for (int i = 0; i < frameCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Bitmap frame = getFrame(i);
                        try
                        {
                            if (frame.Width != width || frame.Height != height)
                                throw new ArgumentException("All GIF frames must have the same size.", "getFrame");
                            WriteGraphicControlExtension(writer, delay);
                            WriteImageDescriptor(writer, width, height);
                            WriteImageData(writer, ConvertToPaletteIndices(frame));
                        }
                        finally
                        {
                            if (disposeFrames) frame.Dispose();
                        }
                        if (progress != null) progress((i + 1) / (double)frameCount);
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

        private static void WriteGlobalPalette(BinaryWriter writer)
        {
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            for (int red = 0; red < 6; red++)
            {
                for (int green = 0; green < 7; green++)
                {
                    for (int blue = 0; blue < 6; blue++)
                    {
                        writer.Write((byte)Math.Round(red * 255.0 / 5.0));
                        writer.Write((byte)Math.Round(green * 255.0 / 6.0));
                        writer.Write((byte)Math.Round(blue * 255.0 / 5.0));
                    }
                }
            }
            for (int index = 253; index < PaletteSize; index++)
            {
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
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

        // 色 → パレット番号の対応表（0=透明、1〜252=6x7x6の色立方体）。画素ごとの割り算を避けるため先に作っておく。
        private static readonly byte[] BlueIndex = BuildLevelTable(5, 1);
        private static readonly byte[] GreenIndex = BuildLevelTable(6, 6);
        private static readonly byte[] RedIndex = BuildLevelTable(5, 42);

        private static byte[] BuildLevelTable(int levels, int weight)
        {
            var table = new byte[256];
            for (int value = 0; value < 256; value++) table[value] = (byte)(((value * levels + 127) / 255) * weight);
            return table;
        }

        private static byte[] ConvertToPaletteIndices(Bitmap source)
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
                                : (byte)(1 + RedIndex[row[offset + 2]] + GreenIndex[row[offset + 1]] + BlueIndex[row[offset]]);
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
    }
}
