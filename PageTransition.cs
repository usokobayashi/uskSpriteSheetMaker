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
        private readonly Bitmap to;
        private readonly PageTransitionKind kind;
        private readonly int durationMs;
        // 最初に画面へ出た時点から数える（切り替えの重い処理で表示が遅れても、動きが省かれないように）。
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Stopwatch sinceCreated = Stopwatch.StartNew();   // 一度も描かれない場合の打ち切り用
        private readonly Timer timer = new Timer { Interval = 15 };
        private bool finished;

        private const int WM_SETREDRAW = 0x000B;
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        public PageTransitionOverlay(Bitmap from, Bitmap to, PageTransitionKind kind, int durationMs)
        {
            this.from = from;
            this.to = to;
            this.kind = kind;
            this.durationMs = Math.Max(1, durationMs);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
            timer.Tick += (s, e) =>
            {
                if ((clock.IsRunning && clock.ElapsedMilliseconds >= this.durationMs) ||
                    (!clock.IsRunning && sinceCreated.ElapsedMilliseconds >= this.durationMs * 3)) Finish();
                else Invalidate();
            };
            timer.Start();
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
            if (!clock.IsRunning) clock.Start();
            float progress = Ease(clock.ElapsedMilliseconds / (float)durationMs);
            Graphics g = e.Graphics;
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
                to.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

namespace SpriteSheetMaker
{
    //==================================================
    // TreeAccordionOverlay
    // フォルダの開閉で、中身が上から伸びる（閉じるときは縮む）ように見せる覆い。
    // 開閉の前後をツリーの画像にして、フォルダの行より下だけを動かす。
    //==================================================
    internal sealed class TreeAccordionOverlay : Control
    {
        private readonly Bitmap before;
        private readonly Bitmap after;
        private readonly int splitY;        // 開閉したフォルダの行の下端（ここより下が動く）
        private readonly int revealHeight;  // 子の行の高さの合計（見えている範囲まで）
        private readonly bool expanding;
        private readonly int durationMs;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };
        private bool finished;

        public TreeAccordionOverlay(Bitmap before, Bitmap after, int splitY, int revealHeight, bool expanding, int durationMs)
        {
            this.before = before;
            this.after = after;
            this.splitY = splitY;
            this.revealHeight = revealHeight;
            this.expanding = expanding;
            this.durationMs = Math.Max(1, durationMs);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
            timer.Tick += (s, e) =>
            {
                if (clock.ElapsedMilliseconds >= this.durationMs) Finish();
                else Invalidate();
            };
            timer.Start();
        }

        public void Finish()
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            Control parent = Parent;
            if (parent != null) parent.Controls.Remove(this);
            Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float progress = PageTransitionOverlay.Ease(clock.ElapsedMilliseconds / (float)durationMs);
            int shown = (int)Math.Round(revealHeight * (expanding ? progress : 1f - progress));
            Bitmap open = expanding ? after : before;      // 子が見えている方
            Bitmap closed = expanding ? before : after;    // 子が隠れている方
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            // フォルダの行まではそのまま、子の行は上から shown だけ見せ、その下に残りの行を続ける。
            g.DrawImage(open, new Rectangle(0, 0, Width, splitY), new Rectangle(0, 0, Width, splitY), GraphicsUnit.Pixel);
            if (shown > 0)
                g.DrawImage(open, new Rectangle(0, splitY, Width, shown), new Rectangle(0, splitY, Width, shown), GraphicsUnit.Pixel);
            int restHeight = Height - splitY - shown;
            if (restHeight > 0)
                g.DrawImage(closed, new Rectangle(0, splitY + shown, Width, restHeight), new Rectangle(0, splitY, Width, restHeight), GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                before.Dispose();
                after.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
