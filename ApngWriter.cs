using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpriteSheetMaker
{
    // Animated PNG（APNG）。GIFと違って256色パレットに量子化しないため、半透明もそのまま残る。
    // 各フレームは独立したPNG画像（フレーム0はIDAT、以降はfdAT）として書き出す。APNGに対応していない
    // 古いビューアでは、フレーム0だけが普通のPNGとして表示される（APNGの仕様どおりの後方互換）。
    // PNGのチャンク書き込み・行フィルター・Adler32は PngStreamWriter のものをそのまま使う（二重実装で
    // 食い違うのを避けるため）。
    public static class ApngWriter
    {
        private const int ChunkSize = 1 << 18;

        public static void SaveAnimatedApng(string path, IList<Bitmap> frames, int delayMs)
        {
            if (frames == null || frames.Count == 0)
                throw new ArgumentException("There are no APNG frames.", "frames");

            int width = frames[0].Width;
            int height = frames[0].Height;
            foreach (Bitmap frame in frames)
            {
                if (frame.Width != width || frame.Height != height)
                    throw new ArgumentException("All APNG frames must have the same size.", "frames");
            }

            SaveAnimatedApng(path, width, height, frames.Count, index => frames[index], false, delayMs, null, CancellationToken.None);
        }

        // フレームを1枚ずつ作って書き出す。GifWriter.SaveAnimatedGifと同じ形（全フレームを同時にメモリへ持たずに済む）。
        public static void SaveAnimatedApng(string path, int width, int height, int frameCount,
            Func<int, Bitmap> getFrame, bool disposeFrames, int delayMs,
            Action<double> progress, CancellationToken cancellationToken)
        {
            if (frameCount <= 0)
                throw new ArgumentException("There are no APNG frames.", "frameCount");
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width", "The APNG size is out of range.");

            int delayNum = Math.Max(1, Math.Min(ushort.MaxValue, delayMs));
            const int delayDen = 1000;

            SheetStreaming.WriteAtomically(path, fs =>
            {
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new byte[13];
                PngStreamWriter.WriteBigEndian(header, 0, width);
                PngStreamWriter.WriteBigEndian(header, 4, height);
                header[8] = 8; // ビット深度
                header[9] = 6; // RGBA
                PngStreamWriter.WriteChunk(fs, "IHDR", header, header.Length);

                // acTL は先頭のIDATより前に置く（APNGの仕様）。num_plays=0 はGIF側と同じ無限ループ。
                var actl = new byte[8];
                PngStreamWriter.WriteBigEndian(actl, 0, frameCount);
                PngStreamWriter.WriteBigEndian(actl, 4, 0);
                PngStreamWriter.WriteChunk(fs, "acTL", actl, actl.Length);

                uint sequenceNumber = 0;
                for (int i = 0; i < frameCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Bitmap frame = getFrame(i);
                    try
                    {
                        if (frame.Width != width || frame.Height != height)
                            throw new ArgumentException("All APNG frames must have the same size.", "getFrame");

                        var fctl = new byte[26];
                        PngStreamWriter.WriteBigEndian(fctl, 0, unchecked((int)sequenceNumber));
                        sequenceNumber++;
                        PngStreamWriter.WriteBigEndian(fctl, 4, width);
                        PngStreamWriter.WriteBigEndian(fctl, 8, height);
                        PngStreamWriter.WriteBigEndian(fctl, 12, 0); // x_offset
                        PngStreamWriter.WriteBigEndian(fctl, 16, 0); // y_offset
                        fctl[20] = (byte)(delayNum >> 8);
                        fctl[21] = (byte)delayNum;
                        fctl[22] = (byte)(delayDen >> 8);
                        fctl[23] = unchecked((byte)delayDen);
                        fctl[24] = 0; // dispose_op = NONE
                        fctl[25] = 0; // blend_op = SOURCE（前のフレームと合成せず、そのまま置き換える）
                        PngStreamWriter.WriteChunk(fs, "fcTL", fctl, fctl.Length);

                        byte[] compressed = CompressFrame(frame, width, height);
                        if (i == 0)
                            WriteIdatChunks(fs, compressed);
                        else
                            WriteFdatChunks(fs, compressed, ref sequenceNumber);
                    }
                    finally
                    {
                        if (disposeFrames) frame.Dispose();
                    }
                    if (progress != null) progress((i + 1) / (double)frameCount);
                }

                PngStreamWriter.WriteChunk(fs, "IEND", new byte[0], 0);
            });
        }

        private static void WriteIdatChunks(Stream fs, byte[] compressed)
        {
            if (compressed.Length == 0)
            {
                PngStreamWriter.WriteChunk(fs, "IDAT", compressed, 0);
                return;
            }
            int offset = 0;
            while (offset < compressed.Length)
            {
                int take = Math.Min(ChunkSize, compressed.Length - offset);
                PngStreamWriter.WriteChunk(fs, "IDAT", Slice(compressed, offset, take), take);
                offset += take;
            }
        }

        // fdAT のチャンク本体は「4バイトの連番 + 圧縮データ」。連番はチャンク単位で振る（フレーム単位ではない）。
        private static void WriteFdatChunks(Stream fs, byte[] compressed, ref uint sequenceNumber)
        {
            int offset = 0;
            do
            {
                int take = Math.Min(ChunkSize, compressed.Length - offset);
                var chunkData = new byte[4 + take];
                PngStreamWriter.WriteBigEndian(chunkData, 0, unchecked((int)sequenceNumber));
                sequenceNumber++;
                if (take > 0) Buffer.BlockCopy(compressed, offset, chunkData, 4, take);
                PngStreamWriter.WriteChunk(fs, "fdAT", chunkData, chunkData.Length);
                offset += take;
            } while (offset < compressed.Length);
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            if (offset == 0 && length == source.Length) return source;
            var slice = new byte[length];
            Buffer.BlockCopy(source, offset, slice, 0, length);
            return slice;
        }

        // 1フレームぶんのRGBA画素を、独立したzlibストリーム（PNGの各画像が持つのと同じ形式）へ圧縮する。
        // 通常のPNG（PngStreamWriter）は帯をまたいで1本のzlibストリームに詰めるが、APNGの各フレームは
        // それぞれが完結したzlibストリームでなければならない。
        private static byte[] CompressFrame(Bitmap source, int width, int height)
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
                int rowBytes = width * 4;
                var raw = new byte[rowBytes];
                var previous = new byte[rowBytes];
                var sub = new byte[rowBytes];
                var up = new byte[rowBytes];
                var line = new byte[rowBytes + 1];
                uint adlerA = 1, adlerB = 0;

                using (var ms = new MemoryStream())
                {
                    ms.WriteByte(0x78);
                    ms.WriteByte(0x9C);
                    using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    {
                        BitmapData data = pixelSource.LockBits(new Rectangle(0, 0, width, height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            for (int y = 0; y < height; y++)
                            {
                                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), raw, 0, rowBytes);
                                for (int i = 0; i < rowBytes; i += 4)
                                {
                                    byte b = raw[i];
                                    raw[i] = raw[i + 2];
                                    raw[i + 2] = b;
                                }
                                PngStreamWriter.FilterRow(raw, previous, sub, up, line, rowBytes);
                                deflate.Write(line, 0, line.Length);
                                PngStreamWriter.UpdateAdler(ref adlerA, ref adlerB, line, line.Length);
                                byte[] swap = previous; previous = raw; raw = swap;
                            }
                        }
                        finally
                        {
                            pixelSource.UnlockBits(data);
                        }
                    }
                    uint adler = (adlerB << 16) | adlerA;
                    ms.WriteByte((byte)(adler >> 24));
                    ms.WriteByte((byte)(adler >> 16));
                    ms.WriteByte((byte)(adler >> 8));
                    ms.WriteByte((byte)adler);
                    return ms.ToArray();
                }
            }
            finally
            {
                if (normalized != null) normalized.Dispose();
            }
        }
    }
}
