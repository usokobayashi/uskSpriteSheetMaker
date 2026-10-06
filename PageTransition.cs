//==================================================
// PageTransition
// 左ペインのページ切り替えの演出。切り替え前後の画面を画像にして、その上に重ねた
// 覆いで「古いページが押し出され、新しいページが入ってくる」横移動を描き、終わったら覆いを外す。
// WinForms のコントロールは動かしながら描き直すとちらつくため、画像を動かして表現する。
//==================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal enum PageTransitionKind
    {
        SlideFromRight,   // 右のタブへ: 新しいページが右から入り、古いページは左へ抜ける
        SlideFromLeft     // 左のタブへ: 新しいページが左から入り、古いページは右へ抜ける
    }

    internal sealed class PageTransitionOverlay : Control
    {
        private readonly Bitmap from;
        private Bitmap to;                 // null の間は「元の画面の絵」だけを出して待つ（下でページを切り替える間の目隠し）
        private PageTransitionKind kind;
        private int durationMs = 1;
        // 最初に画面へ出た時点から数える（切り替えの重い処理で表示が遅れても、動きが省かれないように）。
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Stopwatch sinceCreated = Stopwatch.StartNew();   // 一度も描かれない場合の打ち切り用
        private readonly Timer timer = new Timer { Interval = 15 };
        private bool finished;

        private const int WM_SETREDRAW = 0x000B;
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        // 元の画面の絵だけを表示する覆い。ページを切り替える前に重ね、Begin で動きを始める。
        public PageTransitionOverlay(Bitmap from)
        {
            this.from = from;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
            timer.Tick += (s, e) =>
            {
                if ((clock.IsRunning && clock.ElapsedMilliseconds >= durationMs) ||
                    (!clock.IsRunning && sinceCreated.ElapsedMilliseconds >= durationMs * 3)) Finish();
                else Invalidate();
            };
        }

        public PageTransitionOverlay(Bitmap from, Bitmap to, PageTransitionKind kind, int durationMs) : this(from)
        {
            Begin(to, kind, durationMs);
        }

        // 新しいページの絵がそろったら、横移動を始める（時間は次に描かれた時点から数える）。
        public void Begin(Bitmap to, PageTransitionKind kind, int durationMs)
        {
            this.to = to;
            this.kind = kind;
            this.durationMs = Math.Max(1, durationMs);
            sinceCreated.Restart();
            timer.Start();
            Invalidate();
        }

        // 0→1 の進み具合（終わり際をゆっくりにする）。
        internal static float Ease(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            float inverse = 1f - t;
            return 1f - inverse * inverse * inverse;
        }

        // 演出をすぐに終わらせる（連続で切り替えたとき・閉じるとき）。
        public void Finish()
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            Control parent = Parent;
            if (parent != null)
            {
                // 覆いを外した直後に下のコントロールが1つずつ描き直されて見えないよう、
                // 描画を止めて外し、全部を一度に描き直す。
                bool freeze = parent.IsHandleCreated && parent.Visible;
                if (freeze) SendMessage(parent.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    parent.Controls.Remove(this);
                }
                finally
                {
                    if (freeze)
                    {
                        SendMessage(parent.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                        RedrawWindow(parent.Handle, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    }
                }
            }
            Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (to == null)
            {
                g.DrawImageUnscaled(from, 0, 0);   // まだ待っている間は、切り替える前の画面そのまま
                return;
            }
            if (!clock.IsRunning) clock.Start();
            float progress = Ease(clock.ElapsedMilliseconds / (float)durationMs);
            g.Clear(BackColor);
            int shift = (int)Math.Round(Width * progress);
            int direction = kind == PageTransitionKind.SlideFromRight ? -1 : 1;
            g.DrawImageUnscaled(from, direction * shift, 0);
            g.DrawImageUnscaled(to, direction * shift - direction * Width, 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                from.Dispose();
                if (to != null) to.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

namespace SpriteSheetMaker
{
    //==================================================
    // CrossfadeOverlay
    // 中身が大きく入れ替わる切り替え（モード・プロジェクト）で、切り替え前の絵で覆い、
    // 切り替え後の絵へ溶け込ませてから外す。下のコントロールが1つずつ描かれる様子（がたつき・ちらつき）を見せない。
    //==================================================
    internal sealed class CrossfadeOverlay : Control
    {
        private readonly Bitmap from;
        private Bitmap to;
        private int durationMs = 1;
        private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        private readonly System.Diagnostics.Stopwatch sinceCreated = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
        private bool finished;
        private const int WM_SETREDRAW = 0x000B;
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        public CrossfadeOverlay(Bitmap from)
        {
            this.from = from;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
            timer.Tick += (s, e) =>
            {
                // 描かれないまま時間が過ぎたとき（最小化中など）も必ず外す。
                if ((clock.IsRunning && clock.ElapsedMilliseconds >= durationMs) || sinceCreated.ElapsedMilliseconds >= durationMs * 3 + 2000) Finish();
                else Invalidate();
            };
            timer.Start();
        }

        public void Begin(Bitmap after, int milliseconds)
        {
            to = after; durationMs = Math.Max(1, milliseconds);
            sinceCreated.Restart(); Invalidate();
        }

        public void Finish()
        {
            if (finished) return;
            finished = true; timer.Stop();
            Control parent = Parent;
            if (parent != null)
            {
                bool freeze = parent.IsHandleCreated && parent.Visible;
                if (freeze) SendMessage(parent.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                try { parent.Controls.Remove(this); }
                finally
                {
                    if (freeze)
                    {
                        SendMessage(parent.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                        RedrawWindow(parent.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    }
                }
            }
            Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(from, 0, 0);
            if (to == null) return;
            if (!clock.IsRunning) clock.Start();
            float progress = PageTransitionOverlay.Ease(clock.ElapsedMilliseconds / (float)durationMs);
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = progress });
                g.DrawImage(to, new Rectangle(0, 0, to.Width, to.Height), 0, 0, to.Width, to.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); from.Dispose(); if (to != null) to.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
