//==================================================
// PageTransitionTests
// 左ペインの切り替え演出: 進み具合の曲線と、演出が終わると覆いが外れること。
//==================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class PageTransitionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "PageTransition_EaseGoesFromZeroToOneAndSlowsDown", Action = EaseGoesFromZeroToOneAndSlowsDown };
            yield return new TestCase { Name = "PageTransition_OverlayIsRemovedAfterSwitching", Action = OverlayIsRemovedAfterSwitching };
            yield return new TestCase { Name = "FitAnimation_ReturnsToFitInAboutPointTwoSeconds", Action = FitAnimation_ReturnsToFitInAboutPointTwoSeconds };
            yield return new TestCase { Name = "TabBar_IndicatorSlidesToTheClickedTab", Action = TabBar_IndicatorSlidesToTheClickedTab };
        }

        private static void EaseGoesFromZeroToOneAndSlowsDown()
        {
            Assert.AreEqual(0f, PageTransitionOverlay.Ease(0f), "start");
            Assert.AreEqual(1f, PageTransitionOverlay.Ease(1f), "end");
            Assert.AreEqual(1f, PageTransitionOverlay.Ease(5f), "clamped after the end");
            float early = PageTransitionOverlay.Ease(0.2f) - PageTransitionOverlay.Ease(0.1f);
            float late = PageTransitionOverlay.Ease(0.9f) - PageTransitionOverlay.Ease(0.8f);
            Assert.IsTrue(early > late, "moves fast first and slows at the end");
        }

        // 表示中の画面でタブを切り替えると覆いが重なり、0.3〜0.4秒ほどで外れる。連続で切り替えても1枚だけ。
        private static void OverlayIsRemovedAfterSwitching()
        {
            if (!SystemInformation.UIEffectsEnabled) return;   // Windowsのアニメーション効果がオフの環境では演出しない
            Loc.SettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpriteSheetMakerTests-transition.ini");
            UpdateChecker.IsEnabled = false;
            using (var form = new MainForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.ShowInTaskbar = false;
                form.Size = new Size(1280, 800);
                form.Show();
                Pump(20);
                Type pageType = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                MethodInfo setPage = typeof(MainForm).GetMethod("SetPreviewWorkspacePage", Flags);
                FieldInfo overlayField = typeof(MainForm).GetField("leftWorkspaceTransition", Flags);
                var host = (Control)typeof(MainForm).GetField("leftWorkspaceHost", Flags).GetValue(form);

                setPage.Invoke(form, new[] { Enum.Parse(pageType, "Parameters") });
                Assert.IsTrue(overlayField.GetValue(form) != null, "fade overlay while switching");
                setPage.Invoke(form, new[] { Enum.Parse(pageType, "Preview") });
                int overlays = 0;
                foreach (Control c in host.Controls) if (c is PageTransitionOverlay) overlays++;
                Assert.AreEqual(1, overlays, "a quick second switch replaces the first overlay");

                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (overlayField.GetValue(form) != null && wait.ElapsedMilliseconds < 2000) Pump(1);
                Assert.IsTrue(overlayField.GetValue(form) == null, "overlay removed after the animation");
                foreach (Control c in host.Controls) Assert.IsFalse(c is PageTransitionOverlay, "nothing left on top of the pages");
            }
        }

        // 拡大していた画面は、0.2秒ほどかけて全体表示へ戻る。途中でホイールを回すとそこで止まる。
        private static void FitAnimation_ReturnsToFitInAboutPointTwoSeconds()
        {
            if (!SystemInformation.UIEffectsEnabled) return;
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(500, 400), ShowInTaskbar = false })
            using (var image = new Bitmap(100, 80))
            {
                var canvas = new PreviewCanvas { Dock = DockStyle.Fill };
                form.Controls.Add(canvas);
                form.Show();
                canvas.SetImage(new Bitmap(image), new List<PreviewRect>());
                FieldInfo zoomField = typeof(PreviewCanvas).GetField("zoom", Flags);

                zoomField.SetValue(canvas, 3f);
                canvas.ResetToFitAnimated();
                Assert.IsTrue(canvas.IsFitMode, "counts as the fit view while returning");
                Pump(5);
                float during = (float)zoomField.GetValue(canvas);
                Assert.IsTrue(during > 0f, "still moving shortly after the start (zoom " + during + ")");
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while ((float)zoomField.GetValue(canvas) > 0f && wait.ElapsedMilliseconds < 1000) Pump(1);
                Assert.AreEqual(0f, (float)zoomField.GetValue(canvas), "back to the fit view");
                Assert.InRange(wait.ElapsedMilliseconds, 50, 400, "finishes in about 0.2 s");

                zoomField.SetValue(canvas, 3f);
                canvas.ResetToFitAnimated();
                Pump(3);
                typeof(PreviewCanvas).GetMethod("OnMouseWheel", Flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.None, 0, 50, 50, 120) });
                Pump(30);
                Assert.IsFalse(canvas.IsFitMode, "a wheel turn during the motion stops it and keeps the user's zoom");
            }
        }

        // タブの選択表示は台と同じ描画で滑り、最後はクリックしたタブの範囲にぴったり重なる。
        private static void TabBar_IndicatorSlidesToTheClickedTab()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(500, 200), ShowInTaskbar = false })
            {
                var bar = new SegmentedTabBar(3) { Bounds = new Rectangle(12, 10, 361, 40) };
                form.Controls.Add(bar);
                form.Show();
                Pump(2);
                Assert.AreEqual(0, bar.Controls.Count, "draws everything itself (no child buttons to fall out of step)");
                Rectangle first = bar.GetTabBounds(0), second = bar.GetTabBounds(1), last = bar.GetTabBounds(2);
                Assert.AreEqual(first.Width, second.Width, "equal tab widths");
                Assert.AreEqual(bar.ClientSize.Width - 4, last.Right, "last tab reaches the right padding");
                Assert.AreEqual((RectangleF)first, bar.IndicatorBounds, "starts on the first tab");

                int clicked = -1;
                bar.TabClicked += i => clicked = i;
                Point inside = new Point(last.X + last.Width / 2, last.Y + last.Height / 2);
                typeof(SegmentedTabBar).GetMethod("OnMouseDown", Flags).Invoke(bar, new object[] { new MouseEventArgs(MouseButtons.Left, 1, inside.X, inside.Y, 0) });
                typeof(SegmentedTabBar).GetMethod("OnMouseUp", Flags).Invoke(bar, new object[] { new MouseEventArgs(MouseButtons.Left, 1, inside.X, inside.Y, 0) });
                Assert.AreEqual(2, clicked, "click on the third tab");

                bar.SelectTab(2, true);
                Assert.AreEqual(2, bar.SelectedIndex, "selected right away");
                if (SystemInformation.UIEffectsEnabled)
                {
                    Assert.IsTrue(bar.IsAnimating, "slides");
                    Assert.IsTrue(bar.IndicatorBounds.X < last.X, "has not jumped to the end");
                }
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (bar.IsAnimating && wait.ElapsedMilliseconds < 1500) Pump(1);
                Assert.IsFalse(bar.IsAnimating, "the slide ends");
                Assert.AreEqual((RectangleF)last, bar.IndicatorBounds, "ends exactly on the tab");

                bar.Width = 420;
                Assert.AreEqual((RectangleF)bar.GetTabBounds(2), bar.IndicatorBounds, "follows a resize");
            }
        }

        private static void Pump(int n)
        {
            for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(10); }
        }
    }
}
