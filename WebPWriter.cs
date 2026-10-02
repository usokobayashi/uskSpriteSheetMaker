//==================================================
// WebPWriter
// アニメーション WebP を書き出す。各コマは可逆圧縮（VP8L）なので、ドット絵の色や半透明がそのまま残る。
// 外部ライブラリを使わず、仕様（RFC 9649）の範囲で次の簡単な圧縮だけを行う:
//   ・緑の引き算（Subtract Green）変換
//   ・直前の画素／1行上の画素と同じ並びをまとめる後方参照
//   ・コマごとのハフマン符号
//==================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpriteSheetMaker
{
    public static class WebPWriter
    {
        public const int MaxDimension = 16384;          // VP8L の幅・高さは14ビット
        private const int MaxDurationMs = 0xFFFFFF;     // ANMF の表示時間は24ビット
        private const int MinMatchLength = 3;
        private const int MaxMatchLength = 4096;
        private const int LiteralAlphabet = 256;
        private const int LengthPrefixCount = 24;
        private const int DistanceAlphabet = 40;
        private const int MaxCodeLength = 15;
        private const int MaxCodeLengthCodeLength = 7;
        private const int DistanceCodeAbove = 1;        // 距離表の (0,1): 1行上の画素
        private const int DistanceCodeLeft = 2;         // 距離表の (1,0): 直前の画素
        private static readonly int[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        // フレームを1枚ずつ作って書き出す。GifWriter・ApngWriter と同じ形。
        public static void SaveAnimatedWebP(string path, int width, int height, int frameCount,
            Func<int, Bitmap> getFrame, bool disposeFrames, int delayMs,
            Action<double> progress, CancellationToken cancellationToken)
        {
            if (frameCount <= 0)
                throw new ArgumentException("There are no WebP frames.", "frameCount");
            if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
                throw new ArgumentOutOfRangeException("width", "The WebP size is out of range.");
            int duration = Math.Max(1, Math.Min(MaxDurationMs, delayMs));

            SheetStreaming.WriteAtomically(path, fs =>
            {
                WriteAscii(fs, "RIFF");
                WriteUInt32(fs, 0);   // 全体の大きさは最後に書き戻す
                WriteAscii(fs, "WEBP");

                var vp8x = new byte[10];
                vp8x[0] = 0x10 | 0x02;   // アルファあり・アニメーション
                Write24(vp8x, 4, width - 1);
                Write24(vp8x, 7, height - 1);
                WriteChunk(fs, "VP8X", vp8x);

                var anim = new byte[6];   // 背景色 BGRA=透明、ループ回数 0=無限
                WriteChunk(fs, "ANIM", anim);

                for (int i = 0; i < frameCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Bitmap frame = getFrame(i);
                    byte[] bitstream;
                    try
                    {
                        if (frame.Width != width || frame.Height != height)
                            throw new ArgumentException("All WebP frames must have the same size.", "getFrame");
                        bitstream = EncodeLossless(ReadArgb(frame, width, height), width, height);
                    }
                    finally
                    {
                        if (disposeFrames) frame.Dispose();
                    }

                    int paddedBitstream = bitstream.Length + (bitstream.Length & 1);
                    var anmf = new byte[16 + 8 + paddedBitstream];
                    Write24(anmf, 0, 0);            // X / 2
                    Write24(anmf, 3, 0);            // Y / 2
                    Write24(anmf, 6, width - 1);
                    Write24(anmf, 9, height - 1);
                    Write24(anmf, 12, duration);
                    anmf[15] = 0x02;                // 前のコマと合成せず置き換える・破棄しない
                    anmf[16] = (byte)'V'; anmf[17] = (byte)'P'; anmf[18] = (byte)'8'; anmf[19] = (byte)'L';
                    WriteLittleEndian(anmf, 20, bitstream.Length);
                    Buffer.BlockCopy(bitstream, 0, anmf, 24, bitstream.Length);
                    WriteChunk(fs, "ANMF", anmf);

                    if (progress != null) progress((i + 1) / (double)frameCount);
                }

                long size = fs.Length - 8;
                if (size > uint.MaxValue) throw new IOException("The WebP file is too large.");
                fs.Position = 4;
                WriteUInt32(fs, (uint)size);
                fs.Position = fs.Length;
            });
        }

        //--------------
        // 画素の読み込み
        //--------------
        private static uint[] ReadArgb(Bitmap source, int width, int height)
        {
            Bitmap normalized = null;
            Bitmap pixelSource = source;
            if (source.PixelFormat != PixelFormat.Format32bppArgb)
            {
                normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
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
                var pixels = new uint[width * height];
                var row = new int[width];
                BitmapData data = pixelSource.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, width);
                        for (int x = 0; x < width; x++) pixels[y * width + x] = unchecked((uint)row[x]);
                    }
                }
                finally
                {
                    pixelSource.UnlockBits(data);
                }
                return pixels;
            }
            finally
            {
                if (normalized != null) normalized.Dispose();
            }
        }

        //--------------
        // VP8L（可逆）1枚ぶん
        //--------------
        private struct Token
        {
            public bool IsCopy;
            public uint Argb;       // 画素（IsCopy でないとき）
            public int Length;      // 後方参照の長さ
            public int DistanceCode;
        }

        internal static byte[] EncodeLossless(uint[] argb, int width, int height)
        {
            bool hasAlpha = false;
            var pixels = new uint[argb.Length];
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                if ((p >> 24) != 0xFF) hasAlpha = true;
                // 緑の引き算: 赤と青から緑を引く（多くの画像で値が0付近に集まり、符号が短くなる）
                uint g = (p >> 8) & 0xFF;
                uint r = (((p >> 16) & 0xFF) - g) & 0xFF;
                uint b = ((p & 0xFF) - g) & 0xFF;
                pixels[i] = (p & 0xFF00FF00u) | (r << 16) | b;
            }

            List<Token> tokens = Tokenize(pixels, width);

            var green = new int[LiteralAlphabet + LengthPrefixCount];
            var red = new int[256];
            var blue = new int[256];
            var alpha = new int[256];
            var distance = new int[DistanceAlphabet];
            foreach (Token t in tokens)
            {
                if (t.IsCopy)
                {
                    int lengthPrefix, lengthBits, lengthExtra, distPrefix, distBits, distExtra;
                    PrefixEncode(t.Length, out lengthPrefix, out lengthBits, out lengthExtra);
                    PrefixEncode(t.DistanceCode, out distPrefix, out distBits, out distExtra);
                    green[LiteralAlphabet + lengthPrefix]++;
                    distance[distPrefix]++;
                }
                else
                {
                    green[(t.Argb >> 8) & 0xFF]++;
                    red[(t.Argb >> 16) & 0xFF]++;
                    blue[t.Argb & 0xFF]++;
                    alpha[t.Argb >> 24]++;
                }
            }

            var writer = new BitWriter();
            writer.Write(0x2F, 8);                  // VP8L の印
            writer.Write(width - 1, 14);
            writer.Write(height - 1, 14);
            writer.Write(hasAlpha ? 1 : 0, 1);
            writer.Write(0, 3);                     // 版 0
            writer.Write(1, 1);                     // 変換あり
            writer.Write(2, 2);                     // 緑の引き算
            writer.Write(0, 1);                     // 変換はここまで
            writer.Write(0, 1);                     // カラーキャッシュなし
            writer.Write(0, 1);                     // メタ符号なし（全体で1組のハフマン符号）

            PrefixCode greenCode = WritePrefixCode(writer, green);
            PrefixCode redCode = WritePrefixCode(writer, red);
            PrefixCode blueCode = WritePrefixCode(writer, blue);
            PrefixCode alphaCode = WritePrefixCode(writer, alpha);
            PrefixCode distanceCode = WritePrefixCode(writer, distance);

            foreach (Token t in tokens)
            {
                if (t.IsCopy)
                {
                    int lengthPrefix, lengthBits, lengthExtra, distPrefix, distBits, distExtra;
                    PrefixEncode(t.Length, out lengthPrefix, out lengthBits, out lengthExtra);
                    PrefixEncode(t.DistanceCode, out distPrefix, out distBits, out distExtra);
                    greenCode.WriteSymbol(writer, LiteralAlphabet + lengthPrefix);
                    writer.Write(lengthExtra, lengthBits);
                    distanceCode.WriteSymbol(writer, distPrefix);
                    writer.Write(distExtra, distBits);
                }
                else
                {
                    greenCode.WriteSymbol(writer, (int)((t.Argb >> 8) & 0xFF));
                    redCode.WriteSymbol(writer, (int)((t.Argb >> 16) & 0xFF));
                    blueCode.WriteSymbol(writer, (int)(t.Argb & 0xFF));
                    alphaCode.WriteSymbol(writer, (int)(t.Argb >> 24));
                }
            }
            return writer.ToArray();
        }

        // 直前の画素の繰り返し（透明の余白など）と、1行上と同じ並びを、長い方から後方参照にする。
        private static List<Token> Tokenize(uint[] pixels, int width)
        {
            var tokens = new List<Token>();
            int count = pixels.Length;
            int i = 0;
            while (i < count)
            {
                int left = 0, above = 0;
                if (i >= 1)
                    while (left < MaxMatchLength && i + left < count && pixels[i + left] == pixels[i + left - 1]) left++;
                if (i >= width)
                    while (above < MaxMatchLength && i + above < count && pixels[i + above] == pixels[i + above - width]) above++;
                int best = Math.Max(left, above);
                if (best >= MinMatchLength)
                {
                    tokens.Add(new Token { IsCopy = true, Length = best, DistanceCode = above >= left ? DistanceCodeAbove : DistanceCodeLeft });
                    i += best;
                }
                else
                {
                    tokens.Add(new Token { Argb = pixels[i] });
                    i++;
                }
            }
            return tokens;
        }

        // 長さ・距離（1以上）を「接頭符号 + 追加ビット」に分ける（仕様の逆算）。
        internal static void PrefixEncode(int value, out int prefix, out int extraBits, out int extraValue)
        {
            int n = value - 1;
            if (n < 4)
            {
                prefix = n;
                extraBits = 0;
                extraValue = 0;
                return;
            }
            int highest = 31;
            while (((n >> highest) & 1) == 0) highest--;
            int second = (n >> (highest - 1)) & 1;
            prefix = 2 * highest + second;
            extraBits = highest - 1;
            extraValue = n & ((1 << extraBits) - 1);
        }

        //--------------
        // ハフマン符号
        //--------------
        private sealed class PrefixCode
        {
            public int[] Lengths;
            public int[] Codes;   // 書き込む順（ビット反転済み）

            public void WriteSymbol(BitWriter writer, int symbol)
            {
                if (Lengths[symbol] > 0) writer.Write(Codes[symbol], Lengths[symbol]);
            }
        }

        private static PrefixCode WritePrefixCode(BitWriter writer, int[] histogram)
        {
            var used = new List<int>();
            for (int s = 0; s < histogram.Length; s++) if (histogram[s] > 0) used.Add(s);

            // 1～2個で 256 未満の記号なら「単純な符号」。1個のときは0ビットで表す。
            if (used.Count <= 2 && (used.Count == 0 || used[used.Count - 1] < 256))
            {
                if (used.Count == 0) used.Add(0);
                writer.Write(1, 1);
                writer.Write(used.Count - 1, 1);
                if (used[0] < 2)
                {
                    writer.Write(0, 1);
                    writer.Write(used[0], 1);
                }
                else
                {
                    writer.Write(1, 1);
                    writer.Write(used[0], 8);
                }
                if (used.Count == 2) writer.Write(used[1], 8);

                var lengths = new int[histogram.Length];
                if (used.Count == 2) { lengths[used[0]] = 1; lengths[used[1]] = 1; }
                return new PrefixCode { Lengths = lengths, Codes = CanonicalCodes(lengths) };
            }

            int[] codeLengths = BuildLengths(histogram, MaxCodeLength);
            writer.Write(0, 1);   // 通常の符号

            // 符号長そのものも、0～15 の記号としてハフマン符号化する（繰り返し記号 16～18 は使わない）。
            var lengthHistogram = new int[19];
            foreach (int length in codeLengths) lengthHistogram[length]++;
            int distinct = 0;
            for (int s = 0; s < 16; s++) if (lengthHistogram[s] > 0) distinct++;
            if (distinct < 2) lengthHistogram[lengthHistogram[0] > 0 ? 1 : 0]++;   // 1種類だけだと0ビット符号になり扱いが特殊なため避ける
            int[] lengthCodeLengths = BuildLengths(lengthHistogram, MaxCodeLengthCodeLength);

            int stored = CodeLengthCodeOrder.Length;
            while (stored > 4 && lengthCodeLengths[CodeLengthCodeOrder[stored - 1]] == 0) stored--;
            writer.Write(stored - 4, 4);
            for (int i = 0; i < stored; i++) writer.Write(lengthCodeLengths[CodeLengthCodeOrder[i]], 3);
            writer.Write(0, 1);   // 記号数は字母の大きさのまま

            var lengthCode = new PrefixCode { Lengths = lengthCodeLengths, Codes = CanonicalCodes(lengthCodeLengths) };
            foreach (int length in codeLengths) lengthCode.WriteSymbol(writer, length);
            return new PrefixCode { Lengths = codeLengths, Codes = CanonicalCodes(codeLengths) };
        }

        // 出現数からハフマン符号の長さを作る。上限を超えたら、少ない記号の数を底上げして作り直す。
        internal static int[] BuildLengths(int[] histogram, int maxLength)
        {
            var lengths = new int[histogram.Length];
            int floor = 1;
            while (true)
            {
                var weights = new List<long>();   // 葉（使う記号の順）→ 内部節点の順に並べた木

                var parents = new List<int>();
                var active = new List<int>();
                for (int s = 0; s < histogram.Length; s++)
                {
                    if (histogram[s] <= 0) continue;
                    weights.Add(Math.Max(histogram[s], floor));
                    parents.Add(-1);
                    active.Add(weights.Count - 1);
                }
                int leafCount = weights.Count;
                if (leafCount == 0) return lengths;
                if (leafCount == 1)
                {
                    for (int s = 0; s < histogram.Length; s++) if (histogram[s] > 0) lengths[s] = 1;
                    return lengths;
                }
                while (active.Count > 1)
                {
                    active.Sort((a, b) => weights[a] != weights[b] ? weights[a].CompareTo(weights[b]) : a.CompareTo(b));
                    int first = active[0], second = active[1];
                    active.RemoveRange(0, 2);
                    weights.Add(weights[first] + weights[second]);
                    parents.Add(-1);
                    int merged = weights.Count - 1;
                    parents[first] = merged;
                    parents[second] = merged;
                    active.Add(merged);
                }

                int maxFound = 0;
                int leaf = 0;
                for (int s = 0; s < histogram.Length; s++)
                {
                    if (histogram[s] <= 0) { lengths[s] = 0; continue; }
                    int depth = 0;
                    for (int node = leaf; parents[node] >= 0; node = parents[node]) depth++;
                    lengths[s] = depth;
                    maxFound = Math.Max(maxFound, depth);
                    leaf++;
                }
                if (maxFound <= maxLength) return lengths;
                floor *= 2;
            }
        }

        // 正準ハフマン符号（短い順・記号順）を作り、VP8L の書き込み順（下位ビットから）に合わせて反転する。
        private static int[] CanonicalCodes(int[] lengths)
        {
            var codes = new int[lengths.Length];
            var countPerLength = new int[MaxCodeLength + 2];
            foreach (int length in lengths) if (length > 0) countPerLength[length]++;
            var next = new int[MaxCodeLength + 2];
            int code = 0;
            for (int length = 1; length <= MaxCodeLength + 1; length++)
            {
                code = (code + countPerLength[length - 1]) << 1;
                next[length] = code;
            }
            for (int s = 0; s < lengths.Length; s++)
            {
                int length = lengths[s];
                if (length == 0) continue;
                codes[s] = Reverse(next[length]++, length);
            }
            return codes;
        }

        private static int Reverse(int code, int length)
        {
            int result = 0;
            for (int i = 0; i < length; i++)
            {
                result = (result << 1) | (code & 1);
                code >>= 1;
            }
            return result;
        }

        //--------------
        // ビット・チャンクの書き込み
        //--------------
        private sealed class BitWriter
        {
            private readonly MemoryStream stream = new MemoryStream();
            private ulong buffer;
            private int used;

            public void Write(int value, int bits)
            {
                if (bits == 0) return;
                buffer |= ((ulong)(uint)value & ((1UL << bits) - 1)) << used;
                used += bits;
                while (used >= 8)
                {
                    stream.WriteByte((byte)buffer);
                    buffer >>= 8;
                    used -= 8;
                }
            }

            public byte[] ToArray()
            {
                if (used > 0) stream.WriteByte((byte)buffer);
                buffer = 0;
                used = 0;
                return stream.ToArray();
            }
        }

        private static void WriteChunk(Stream fs, string type, byte[] data)
        {
            WriteAscii(fs, type);
            WriteUInt32(fs, (uint)data.Length);
            fs.Write(data, 0, data.Length);
            if ((data.Length & 1) != 0) fs.WriteByte(0);
        }

        private static void WriteAscii(Stream fs, string text)
        {
            foreach (char c in text) fs.WriteByte((byte)c);
        }

        private static void WriteUInt32(Stream fs, uint value)
        {
            fs.WriteByte((byte)value);
            fs.WriteByte((byte)(value >> 8));
            fs.WriteByte((byte)(value >> 16));
            fs.WriteByte((byte)(value >> 24));
        }

        private static void WriteLittleEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }

        private static void Write24(byte[] target, int offset, int value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
        }
    }
}
