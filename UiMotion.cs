//==================================================
// UiMotion
// 画面の小さな動き（通知カード・ダイアログ・ドロップ受け皿など）の時間管理をまとめる。
// Windows の「アニメーション効果」がオフのときは、どの動きもすぐに終わった状態にする。
//==================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal static class UiMotion
    {
        public static bool Enabled
        {
            get { return SystemInformation.UIEffectsEnabled; }
        }

        //--------------
        // Animate
        //--------------
        // milliseconds かけて step(0→1) を呼ぶ（終わり際をゆっくりにした進み具合）。最後に done。
        // 戻り値の Timer を Stop に渡すと途中で止められる（done は呼ばない）。
        public static Timer Animate(int milliseconds, Action<float> step, Action done = null)
        {
            if (!Enabled || milliseconds <= 0)
            {
                step(1f);
                if (done != null) done();
                return null;
            }
            var clock = Stopwatch.StartNew();
            var timer = new Timer { Interval = 15 };
            timer.Tick += (s, e) =>
            {
                float raw = clock.ElapsedMilliseconds / (float)milliseconds;
                step(PageTransitionOverlay.Ease(raw));
                if (raw < 1f) return;
                timer.Stop();
                timer.Dispose();
                if (done != null) done();
            };
            step(0f);
            timer.Start();
            return timer;
        }

        public static void Stop(ref Timer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }

        public static Color Mix(Color from, Color to, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * t),
                (int)Math.Round(from.R + (to.R - from.R) * t),
                (int)Math.Round(from.G + (to.G - from.G) * t),
                (int)Math.Round(from.B + (to.B - from.B) * t));
        }
    }

    //==================================================
    // DialogMotion
    // ダイアログを少し下から浮き上がらせて出し、後ろの本体を薄く暗くする。
    //==================================================
    internal static class DialogMotion
    {
        private const int AppearMilliseconds = 150;
        private const int DimMilliseconds = 120;
        private const int RisePixels = 14;
        private const double DimOpacity = 0.32;

        public static void Attach(Form dialog, Form owner)
        {
            if (!UiMotion.Enabled) return;
            DimForm dim = null;
            dialog.Opacity = 0;
            dialog.Shown += (s, e) =>
            {
                Point final = dialog.Location;
                if (owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized)
                {
                    dim = new DimForm(owner.RectangleToScreen(owner.ClientRectangle));
                    dim.Show(owner);
                    dialog.BringToFront();
                    dialog.Activate();
                }
                DimForm shownDim = dim;
                UiMotion.Animate(AppearMilliseconds, t =>
                {
                    if (dialog.IsDisposed) return;
                    dialog.Opacity = t;
                    dialog.Location = new Point(final.X, final.Y + (int)Math.Round(RisePixels * (1f - t)));
                    if (shownDim != null && !shownDim.IsDisposed) shownDim.Opacity = DimOpacity * t;
                });
            };
            dialog.FormClosed += (s, e) =>
            {
                DimForm closing = dim;
                dim = null;
                if (closing == null) return;
                UiMotion.Animate(DimMilliseconds, t => { if (!closing.IsDisposed) closing.Opacity = DimOpacity * (1f - t); },
                    () => { closing.Close(); closing.Dispose(); });
            };
        }

        // 本体を覆う黒い半透明の窓。前面に出ず、クリックでもアクティブにならない。
        private sealed class DimForm : Form
        {
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WM_MOUSEACTIVATE = 0x21;
            private const int MA_NOACTIVATE = 3;

            public DimForm(Rectangle screenBounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black;
                Opacity = 0;
                Bounds = screenBounds;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return parameters;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
                base.WndProc(ref m);
            }
        }
    }
}
