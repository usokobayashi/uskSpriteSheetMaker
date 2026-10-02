using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // 割り当て状態で、操作できる項目（穴）以外を暗くする窓。
    // ピクセルごとの透明度を持つ窓（UpdateLayeredWindow）なので、穴の角丸・破線の枠・案内の帯が
    // アンチエイリアスで描ける。完全に透明な穴の部分と、passThrough の範囲は下のコントロールへ
    // マウスが届く。窓は前面に出さない（アクティブにならない）。
    internal sealed class AssignOverlayForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCHITTEST = 0x84;
        private const int HTTRANSPARENT = -1;

        // 画面座標。この範囲は暗く見えてもクリックを下のコントロールへ通す（タブ、スクロール）。
        public readonly List<Rectangle> PassThroughRects = new List<Rectangle>();

        // 窓全体の濃さ（0=見えない、255=そのまま）。出入りのときに少しずつ変える。
        private byte constantAlpha = 255;
        private Bitmap lastBitmap;
        private Rectangle lastBounds;
        private Timer fadeTimer;

        public void SetConstantAlpha(byte alpha)
        {
            constantAlpha = alpha;
            if (lastBitmap != null) Present(lastBounds, lastBitmap);
        }

        // 濃さを target へ milliseconds かけて変え、終わったら done。
        public void FadeTo(byte target, int milliseconds, Action done)
        {
            UiMotion.Stop(ref fadeTimer);
            byte start = constantAlpha;
            fadeTimer = UiMotion.Animate(milliseconds, t =>
            {
                if (IsDisposed) return;
                SetConstantAlpha((byte)Math.Round(start + (target - start) * t));
            }, () =>
            {
                fadeTimer = null;
                if (done != null) done();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiMotion.Stop(ref fadeTimer);
                if (lastBitmap != null) { lastBitmap.Dispose(); lastBitmap = null; }
            }
            base.Dispose(disposing);
        }

        public AssignOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return parameters;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                long value = m.LParam.ToInt64();
                var point = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
                foreach (Rectangle rect in PassThroughRects)
                    if (rect.Contains(point)) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }

        // 暗い面（穴を抜く）・穴の破線の枠・案内の帯を描いた画像。holes は窓内の座標。
        public static Bitmap RenderBitmap(Size size, IList<Rectangle> holes, int radius, string banner,
            Font bannerFont, Color bannerBack, Color bannerText, Rectangle bannerArea)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.FromArgb(150, 0, 0, 0));

                // 穴（角丸）: 暗い面を抜いて元の画面を見せる。
                g.CompositingMode = CompositingMode.SourceCopy;
                var paths = new List<GraphicsPath>();
                foreach (Rectangle hole in holes)
                {
                    GraphicsPath path = CreateRoundedPath(hole, radius);
                    paths.Add(path);
                    using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0))) g.FillPath(clear, path);
                }
                g.CompositingMode = CompositingMode.SourceOver;

                // 「ここへ入れられる」ことを示す破線の枠。
                using (var dash = new Pen(Color.FromArgb(235, 240, 245), 1.6f) { DashPattern = new[] { 3.5f, 2.5f } })
                    foreach (GraphicsPath path in paths) g.DrawPath(dash, path);
                foreach (GraphicsPath path in paths) path.Dispose();

                // 案内の帯（角丸）。
                if (!string.IsNullOrEmpty(banner) && bannerArea.Width > 0)
                {
                    using (GraphicsPath pill = CreateRoundedPath(bannerArea, Math.Min(radius, bannerArea.Height / 2)))
                    using (var back = new SolidBrush(bannerBack))
                        g.FillPath(back, pill);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var text = new SolidBrush(bannerText))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(banner, bannerFont, text, bannerArea, format);
                }
            }
            return bitmap;
        }

        // 画面上の bounds に、画像を貼って表示を更新する。
        public void ShowBitmap(Rectangle bounds, Bitmap bitmap)
        {
            if (lastBitmap != null) lastBitmap.Dispose();
            lastBitmap = new Bitmap(bitmap);   // 濃さだけ変えて描き直すために控える
            lastBounds = bounds;
            Present(bounds, bitmap);
        }

        private void Present(Rectangle bounds, Bitmap bitmap)
        {
            Bounds = bounds;
            if (!IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previous = SelectObject(memoryDc, hBitmap);
            try
            {
                var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
                var source = new POINT { x = 0, y = 0 };
                var target = new POINT { x = bounds.X, y = bounds.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = constantAlpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref target, ref size, memoryDc, ref source, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memoryDc, previous);
                DeleteObject(hBitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            var arc = new Rectangle(rect.X, rect.Y, d, d);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - d - 1;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - d - 1;
            path.AddArc(arc, 0, 90);
            arc.X = rect.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    }

    // 割り当て状態の間だけ、アプリ内の入力を横取りする。
    //  - Esc: 解除 / その他のキー: 無視（他のショートカットを受け付けない）
    //  - 状態の行の左クリック: その行へ割り当てて終了 / タブの左クリック: 解除（クリックはそのまま通す）
    //  - 暗くした範囲のマウス操作: 無視 / 操作できる範囲（タブ・状態リスト）: そのまま通す（スクロールなど）
    internal sealed class AssignInputFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203;
        private const int WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
        private const int WM_MOUSEWHEEL = 0x20A;

        private readonly Func<Point, bool> isInsideHole;
        private readonly Func<Point, bool> isTab;
        private readonly Func<Point, bool> tryAssign;
        private readonly Action cancel;
        private bool swallowNextLeftUp;

        public AssignInputFilter(Func<Point, bool> isInsideHole, Func<Point, bool> isTab, Func<Point, bool> tryAssign, Action cancel)
        {
            this.isInsideHole = isInsideHole;
            this.isTab = isTab;
            this.tryAssign = tryAssign;
            this.cancel = cancel;
        }

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    if ((Keys)(int)m.WParam == Keys.Escape) cancel();
                    return true;
                case WM_KEYUP:
                case WM_SYSKEYUP:
                case WM_CHAR:
                    return true;
                case WM_LBUTTONDOWN:
                case WM_LBUTTONDBLCLK:
                {
                    Point point = Cursor.Position;
                    if (tryAssign(point)) { swallowNextLeftUp = true; return true; }
                    if (isTab(point)) { cancel(); return false; }
                    return !isInsideHole(point);
                }
                case WM_LBUTTONUP:
                    if (swallowNextLeftUp) { swallowNextLeftUp = false; return true; }
                    return !isInsideHole(Cursor.Position);
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                    return true;
                case WM_MOUSEWHEEL:
                    return !isInsideHole(Cursor.Position);
                default:
                    return false;
            }
        }
    }
}
