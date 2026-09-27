using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpriteSheetMaker
{
    public static class TgaWriter
    {
        public static void Save32Bit(string path, Bitmap source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            // すでにFormat32bppArgbならソースを直接LockBitsしてバイト精度を保つ。
            // Graphics.DrawImageによる合成描画（SourceCopy指定でも）はGDI+内部の
            // 変換でRGBが±1階調ずれることがあるため、それ以外の形式のときだけ
            // フォーマット変換用に使う。
            Bitmap normalized = null;
            try
            {
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

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    WriteHeader(bw, pixelSource.Width, pixelSource.Height);

                    var rect = new Rectangle(0, 0, pixelSource.Width, pixelSource.Height);
                    BitmapData data = pixelSource.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                    try
                    {
                        int bytesPerRow = pixelSource.Width * 4;
                        byte[] row = new byte[bytesPerRow];

                        for (int y = pixelSource.Height - 1; y >= 0; y--)
                        {
                            IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                            Marshal.Copy(rowPtr, row, 0, bytesPerRow);
                            bw.Write(row);
                        }
                    }
                    finally
                    {
                        pixelSource.UnlockBits(data);
                    }

                    WriteFooter(bw);
                }
            }
            finally
            {
                if (normalized != null) normalized.Dispose();
            }
        }

        internal static void WriteFooter(BinaryWriter bw)
        {
            bw.Write(0);
            bw.Write(0);
            bw.Write(Encoding.ASCII.GetBytes("TRUEVISION-XFILE."));
            bw.Write((byte)0);
        }

        internal static void WriteHeader(BinaryWriter bw, int width, int height)
        {
            if (width <= 0 || width > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException("width");
            }

            if (height <= 0 || height > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException("height");
            }

            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)2);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((byte)0);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)width);
            bw.Write((ushort)height);
            bw.Write((byte)32);
            bw.Write((byte)8);
        }
    }
}
