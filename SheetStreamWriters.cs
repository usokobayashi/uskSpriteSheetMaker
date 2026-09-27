using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpriteSheetMaker
{
    // 帯（画像の上から数行ぶん）を1枚のビットマップとして作る関数。
    // 幅は常にシート全体の幅、高さは height。Format32bppArgb で返し、書き出し側が破棄する。
    public delegate Bitmap SheetBandRenderer(int y0, int height);

    // シート全体を1枚のビットマップとして持たずに、帯ごとに作って順にファイルへ流し込む書き出し。
    // GDI+のBitmapは約1GB（幅×高さ×4）を超えると作れないため、それより大きなシートもこの経路で出せる。
    public static class SheetStreaming
    {
        // 1回に作る帯の大きさの目安（バイト）。
        public const long DefaultBandBudgetBytes = 64L * 1024 * 1024;

        // テストで小さくして、複数の帯に分かれる場合を確かめられるようにしてある。
        internal static long BandBudgetBytes = DefaultBandBudgetBytes;

        public static int ChooseBandHeight(int width, int height)
        {
            long rowBytes = Math.Max(1L, (long)width * 4);
            long rows = Math.Max(1L, BandBudgetBytes / rowBytes);
            return (int)Math.Min(height, rows);
        }

        // 書き込み途中のファイルを完成するまで別名にしておき、最後に置き換える。途中で失敗・中止しても
        // 元のファイルを壊さず、中途半端なファイルも残さない。
        internal static void WriteAtomically(string path, Action<Stream> write)
        {
            string temp = path + ".part";
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    write(fs);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    // PNG（32bit RGBA）を帯ごとに書き出す。縦横とも 2^31-1 まで（PNGの規格の上限）。
    public static class PngStreamWriter
    {
        private static readonly uint[] CrcTable = BuildCrcTable();
        private static readonly byte[] Cost = BuildCostTable();

        public static void Save(string path, int width, int height, SheetBandRenderer render,
            Action<double> progress, CancellationToken cancellationToken)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");
            SheetStreaming.WriteAtomically(path, fs =>
            {
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new byte[13];
                WriteBigEndian(header, 0, width);
                WriteBigEndian(header, 4, height);
                header[8] = 8;   // ビット深度
                header[9] = 6;   // RGBA
                WriteChunk(fs, "IHDR", header, header.Length);

                int bandHeight = SheetStreaming.ChooseBandHeight(width, height);
                int rowBytes = width * 4;
                var raw = new byte[rowBytes];
                var previous = new byte[rowBytes];
                var sub = new byte[rowBytes];
                var up = new byte[rowBytes];
                var line = new byte[rowBytes + 1];
                uint adlerA = 1, adlerB = 0;

                using (var idat = new IdatStream(fs))
                {
                    idat.WriteByte(0x78);
                    idat.WriteByte(0x9C);
                    using (var deflate = new DeflateStream(idat, CompressionLevel.Optimal, true))
                    {
                        for (int y0 = 0; y0 < height; y0 += bandHeight)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int h = Math.Min(bandHeight, height - y0);
                            using (Bitmap band = render(y0, h))
                            {
                                if (band.Width != width || band.Height != h)
                                    throw new InvalidOperationException("band size mismatch");
                                BitmapData data = band.LockBits(new Rectangle(0, 0, width, h),
                                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                                try
                                {
                                    for (int y = 0; y < h; y++)
                                    {
                                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), raw, 0, rowBytes);
                                        for (int i = 0; i < rowBytes; i += 4)
                                        {
                                            byte b = raw[i];
                                            raw[i] = raw[i + 2];
                                            raw[i + 2] = b;
                                        }
                                        FilterRow(raw, previous, sub, up, line, rowBytes);
                                        deflate.Write(line, 0, line.Length);
                                        UpdateAdler(ref adlerA, ref adlerB, line, line.Length);
                                        byte[] swap = previous; previous = raw; raw = swap;
                                    }
                                }
                                finally
                                {
                                    band.UnlockBits(data);
                                }
                            }
                            if (progress != null) progress(Math.Min(1.0, (y0 + h) / (double)height));
                        }
                    }
                    uint adler = (adlerB << 16) | adlerA;
                    idat.WriteByte((byte)(adler >> 24));
                    idat.WriteByte((byte)(adler >> 16));
                    idat.WriteByte((byte)(adler >> 8));
                    idat.WriteByte((byte)adler);
                    idat.Finish();
                }
                WriteChunk(fs, "IEND", new byte[0], 0);
            });
        }

        // None / Sub / Up のうち、値の偏りが小さくなる（＝圧縮しやすい）ものを選んで line に入れる。
        private static void FilterRow(byte[] raw, byte[] previous, byte[] sub, byte[] up, byte[] line, int rowBytes)
        {
            int costNone = 0, costSub = 0, costUp = 0;
            for (int i = 0; i < rowBytes; i++)
            {
                byte value = raw[i];
                byte s = i >= 4 ? (byte)(value - raw[i - 4]) : value;
                byte u = (byte)(value - previous[i]);
                sub[i] = s;
                up[i] = u;
                costNone += Cost[value];
                costSub += Cost[s];
                costUp += Cost[u];
            }
            if (costSub <= costNone && costSub <= costUp)
            {
                line[0] = 1;
                Buffer.BlockCopy(sub, 0, line, 1, rowBytes);
            }
            else if (costUp < costNone)
            {
                line[0] = 2;
                Buffer.BlockCopy(up, 0, line, 1, rowBytes);
            }
            else
            {
                line[0] = 0;
                Buffer.BlockCopy(raw, 0, line, 1, rowBytes);
            }
        }

        private static void UpdateAdler(ref uint a, ref uint b, byte[] data, int length)
        {
            const uint Mod = 65521;
            int offset = 0;
            while (offset < length)
            {
                int count = Math.Min(5552, length - offset);
                for (int i = 0; i < count; i++)
                {
                    a += data[offset + i];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
                offset += count;
            }
        }

        private static byte[] BuildCostTable()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = (byte)(i < 128 ? i : 256 - i);
            return table;
        }

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

        private static uint UpdateCrc(uint crc, byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data, int length)
        {
            var head = new byte[8];
            WriteBigEndian(head, 0, length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            stream.Write(head, 0, 8);
            uint crc = UpdateCrc(0xFFFFFFFFu, head, 4, 4);
            if (length > 0)
            {
                stream.Write(data, 0, length);
                crc = UpdateCrc(crc, data, 0, length);
            }
            crc ^= 0xFFFFFFFFu;
            var tail = new byte[4];
            WriteBigEndian(tail, 0, unchecked((int)crc));
            stream.Write(tail, 0, 4);
        }

        // 圧縮データを一定の大きさごとに IDAT チャンクへ区切って流す。
        private sealed class IdatStream : Stream
        {
            private const int ChunkSize = 1 << 18;
            private readonly Stream target;
            private readonly byte[] buffer = new byte[ChunkSize];
            private int count;

            public IdatStream(Stream target) { this.target = target; }

            public override void Write(byte[] source, int offset, int length)
            {
                while (length > 0)
                {
                    int take = Math.Min(length, ChunkSize - count);
                    Buffer.BlockCopy(source, offset, buffer, count, take);
                    count += take;
                    offset += take;
                    length -= take;
                    if (count == ChunkSize) Flush();
                }
            }

            public override void WriteByte(byte value)
            {
                buffer[count++] = value;
                if (count == ChunkSize) Flush();
            }

            public override void Flush()
            {
                if (count == 0) return;
                WriteChunk(target, "IDAT", buffer, count);
                count = 0;
            }

            public void Finish() { Flush(); }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
        }
    }

    // 画像の下の行から先に並べる TGA（32bit）を、帯を下から順に作って書き出す。
    // TGAの規格では幅・高さとも65,535pxまで。
    public static class TgaStreamWriter
    {
        public static void Save(string path, int width, int height, SheetBandRenderer render,
            Action<double> progress, CancellationToken cancellationToken)
        {
            if (width <= 0 || width > ushort.MaxValue) throw new ArgumentOutOfRangeException("width");
            if (height <= 0 || height > ushort.MaxValue) throw new ArgumentOutOfRangeException("height");
            SheetStreaming.WriteAtomically(path, fs =>
            {
                using (var bw = new BinaryWriter(fs, Encoding.ASCII, true))
                {
                    TgaWriter.WriteHeader(bw, width, height);
                    int bandHeight = SheetStreaming.ChooseBandHeight(width, height);
                    int rowBytes = width * 4;
                    var row = new byte[rowBytes];
                    int done = 0;
                    var starts = new List<int>();
                    for (int y0 = 0; y0 < height; y0 += bandHeight) starts.Add(y0);
                    for (int k = starts.Count - 1; k >= 0; k--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int y0 = starts[k];
                        int h = Math.Min(bandHeight, height - y0);
                        using (Bitmap band = render(y0, h))
                        {
                            if (band.Width != width || band.Height != h)
                                throw new InvalidOperationException("band size mismatch");
                            BitmapData data = band.LockBits(new Rectangle(0, 0, width, h),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                            try
                            {
                                for (int y = h - 1; y >= 0; y--)
                                {
                                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                                    bw.Write(row);
                                }
                            }
                            finally
                            {
                                band.UnlockBits(data);
                            }
                        }
                        done += h;
                        if (progress != null) progress(Math.Min(1.0, done / (double)height));
                    }
                    TgaWriter.WriteFooter(bw);
                }
            });
        }
    }

    // 読み込み済みの画像を、合計の大きさの上限つきで使い回す入れ物（古い順に破棄する）。
    // 書き出し中に同じ行の画像を帯ごとに読み直さずに済ませる。
    internal sealed class BitmapLruCache : IDisposable
    {
        private readonly long budgetBytes;
        private readonly Dictionary<int, LinkedListNode<KeyValuePair<int, Bitmap>>> map =
            new Dictionary<int, LinkedListNode<KeyValuePair<int, Bitmap>>>();
        private readonly LinkedList<KeyValuePair<int, Bitmap>> order = new LinkedList<KeyValuePair<int, Bitmap>>();
        private long totalBytes;

        public BitmapLruCache(long budgetBytes) { this.budgetBytes = budgetBytes; }

        public int Count { get { return map.Count; } }
        public long TotalBytes { get { return totalBytes; } }

        // 返した画像は、次にこのキャッシュを呼ぶまで有効。呼び出し側は破棄しない。
        public Bitmap Get(int key, Func<Bitmap> load)
        {
            LinkedListNode<KeyValuePair<int, Bitmap>> node;
            if (map.TryGetValue(key, out node))
            {
                order.Remove(node);
                order.AddFirst(node);
                return node.Value.Value;
            }

            Bitmap bitmap = load();
            long bytes = (long)bitmap.Width * bitmap.Height * 4;
            while (order.Last != null && totalBytes + bytes > budgetBytes)
            {
                LinkedListNode<KeyValuePair<int, Bitmap>> oldest = order.Last;
                order.RemoveLast();
                map.Remove(oldest.Value.Key);
                totalBytes -= (long)oldest.Value.Value.Width * oldest.Value.Value.Height * 4;
                oldest.Value.Value.Dispose();
            }
            map[key] = order.AddFirst(new KeyValuePair<int, Bitmap>(key, bitmap));
            totalBytes += bytes;
            return bitmap;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<int, Bitmap> entry in order) entry.Value.Dispose();
            order.Clear();
            map.Clear();
            totalBytes = 0;
        }
    }
}
