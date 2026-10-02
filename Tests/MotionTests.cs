//==================================================
// MotionTests
// 画面の動き（ダイアログ・通知カード・チップ・ドロップ受け皿・割り当ての暗転・メモ・書き出しバー・
// セル選択・フォルダの開閉）が起きて、決めた時間で終わり、後に何も残らないことの検証。
//==================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class MotionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Motion_DialogRisesAndDimIsRemoved", Action = DialogRisesAndDimIsRemoved };
            yield return new TestCase { Name = "Motion_UpdateCardRisesSinksAndChipGlowsOnce", Action = UpdateCardRisesSinksAndChipGlowsOnce };
            yield return new TestCase { Name = "Motion_DropZoneBouncesOnlyWhileHovering", Action = DropZoneBouncesOnlyWhileHovering };
            yield return new TestCase { Name = "Motion_AssignOverlayFades", Action = AssignOverlayFades };
            yield return new TestCase { Name = "Motion_MemoAppearsAndVanishes", Action = MemoAppearsAndVanishes };
            yield return new TestCase { Name = "Motion_ExportBarFillsFlashesAndHides", Action = ExportBarFillsFlashesAndHides };
            yield return new TestCase { Name = "Motion_SelectedCellPopsOnce", Action = SelectedCellPopsOnce };
            yield return new TestCase { Name = "Motion_FolderCollapseAndExpandSlide", Action = FolderCollapseAndExpandSlide };
        }

        //--------------
        // 補助
        //--------------
        private static void Pump(int n)
        {
            for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(10); }
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < timeoutMs) Pump(1);
            return condition();
        }

        private static MainForm ShownMainForm()
        {
            Loc.SettingsPath = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerTests-motion.ini");
            UpdateChecker.IsEnabled = false;
            var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
                Size = new Size(1280, 800),
                PromptOnUnsavedChanges = false
            };
            form.Show();
            Pump(20);
            return form;
        }

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, Flags).GetValue(target);
        }

        //--------------
        // テスト
        //--------------
        private static void DialogRisesAndDimIsRemoved()
        {
            if (!UiMotion.Enabled) return;
            using (var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var dialog = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-3900, -3900), Size = new Size(300, 200), ShowInTaskbar = false })
            {
                owner.Show();
                DialogMotion.Attach(dialog, owner);
                Assert.AreEqual(0.0, dialog.Opacity, "starts invisible");
                dialog.Show(owner);
                Assert.IsTrue(WaitUntil(() => dialog.Opacity >= 0.999, 600), "fades in within the motion time");
                Assert.AreEqual(new Point(-3900, -3900), dialog.Location, "ends at its own position");
                int ownedBefore = owner.OwnedForms.Length;
                Assert.IsTrue(ownedBefore >= 2, "a dim layer is shown behind the dialog");
                dialog.Close();
                Assert.IsTrue(WaitUntil(() => owner.OwnedForms.Length == 0, 600), "the dim layer is closed after the dialog");
            }
        }

        private static void UpdateCardRisesSinksAndChipGlowsOnce()
        {
            if (!UiMotion.Enabled) return;
            using (MainForm form = ShownMainForm())
            {
                var release = new ReleaseInfo { Tag = "v9.0.0", Version = new Version(9, 0, 0), PageUrl = UpdateChecker.ReleasesPage, Notes = "・test" };
                form.ShowUpdateNotice(release, true);
                var card = Field<Control>(form, "updateCard");
                var chip = Field<Button>(form, "updateChip");
                int firstTop = card.Top;
                Assert.IsTrue(WaitUntil(() => chip.BackColor != Field<Color>(form, "accentColor"), 600), "the chip glows when it appears");
                Assert.IsTrue(WaitUntil(() => card.Top < firstTop - 20, 600), "the card rises into place");
                int finalTop = card.Top;
                Assert.IsTrue(WaitUntil(() => chip.BackColor == Field<Color>(form, "accentColor"), 2000), "the chip returns to its color after one glow");

                typeof(MainForm).GetMethod("SinkUpdateCard", Flags).Invoke(form, null);
                Assert.IsTrue(Field<Control>(form, "updateCard") == null, "counts as closed at once");
                int lowestTop = card.Top;
                Assert.IsTrue(WaitUntil(() => { if (!card.IsDisposed) lowestTop = Math.Max(lowestTop, card.Top); return lowestTop > finalTop + 10 || card.IsDisposed; }, 400),
                    "sinks downward (top " + finalTop + " -> " + lowestTop + ")");
                Assert.IsTrue(WaitUntil(() => card.IsDisposed, 600), "removed after sinking");
            }
        }

        private static void DropZoneBouncesOnlyWhileHovering()
        {
            if (!UiMotion.Enabled) return;
            using (MainForm form = ShownMainForm())
            {
                MethodInfo setHover = typeof(MainForm).GetMethod("SetDropZoneHover", Flags);
                var zone = Field<Control>(form, "emptyDropZone");
                Control icon = zone.Controls[0];
                setHover.Invoke(form, new object[] { true, icon });
                float lowest = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 500) { Pump(1); lowest = Math.Min(lowest, Field<float>(form, "dropArrowOffset")); }
                Assert.InRange(lowest, -5.01, -2, "the arrow bounces up while hovering");
                setHover.Invoke(form, new object[] { false, icon });
                Pump(5);
                Assert.AreEqual(0f, Field<float>(form, "dropArrowOffset"), "stops at rest when the drag leaves");
            }
        }

        private static void AssignOverlayFades()
        {
            if (!UiMotion.Enabled) return;
            using (var overlay = new AssignOverlayForm())
            {
                overlay.SetConstantAlpha(0);
                bool done = false;
                overlay.FadeTo(255, 150, () => done = true);
                Assert.IsTrue(WaitUntil(() => done, 600), "fade in finishes");
                Assert.AreEqual((byte)255, Field<byte>(overlay, "constantAlpha"), "fully shown");
                done = false;
                overlay.FadeTo(0, 150, () => done = true);
                Assert.IsTrue(WaitUntil(() => done, 600), "fade out finishes");
                Assert.AreEqual((byte)0, Field<byte>(overlay, "constantAlpha"), "fully hidden");
            }
        }

        private static void MemoAppearsAndVanishes()
        {
            if (!UiMotion.Enabled) return;
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(400, 300), ShowInTaskbar = false })
            {
                var canvas = new PreviewCanvas { Dock = DockStyle.Fill };
                form.Controls.Add(canvas);
                form.Show();
                var memo = new SheetMemo { Width = 80, X = 10, Y = 10, Text = "memo" };
                canvas.AnimateMemoAppear(memo);
                Assert.IsTrue(canvas.MemoMotionRunning, "appear motion running");
                Assert.IsTrue(WaitUntil(() => !canvas.MemoMotionRunning, 600), "appear motion ends");
                canvas.AnimateMemoVanish(memo);
                Assert.IsTrue(canvas.MemoMotionRunning, "vanish motion running");
                Assert.IsTrue(WaitUntil(() => !canvas.MemoMotionRunning, 600), "the vanished memo is no longer drawn");
            }
        }

        private static void ExportBarFillsFlashesAndHides()
        {
            if (!UiMotion.Enabled) return;
            using (MainForm form = ShownMainForm())
            {
                Type type = typeof(MainForm);
                type.GetMethod("StartExportBar", Flags).Invoke(form, null);
                type.GetField("exportBarTarget", Flags).SetValue(form, 0.5f);
                Assert.IsTrue(WaitUntil(() => Field<float>(form, "exportBarShown") > 0.45f, 600), "the bar grows smoothly toward the progress");
                type.GetMethod("FinishExportBar", Flags).Invoke(form, new object[] { true });
                Assert.IsTrue(WaitUntil(() => Field<object>(form, "exportBarState").ToString() == "Hidden", 1500), "fills, flashes and hides");
                Assert.InRange(Field<float>(form, "exportBarShown"), 0.99, 1.0, "reached the end before hiding");

                type.GetMethod("StartExportBar", Flags).Invoke(form, null);
                type.GetMethod("FinishExportBar", Flags).Invoke(form, new object[] { false });
                Assert.AreEqual("Hidden", Field<object>(form, "exportBarState").ToString(), "a canceled export hides the bar at once");
            }
        }

        private static void SelectedCellPopsOnce()
        {
            if (!UiMotion.Enabled) return;
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(400, 300), ShowInTaskbar = false })
            {
                var canvas = new PreviewCanvas { Dock = DockStyle.Fill };
                form.Controls.Add(canvas);
                form.Show();
                var selected = new HashSet<int>();
                canvas.IsCellSelected = n => selected.Contains(n);
                canvas.SetImage(new Bitmap(64, 32), new List<PreviewRect>
                {
                    new PreviewRect { Rect = new Rectangle(0, 0, 32, 32), Number = 1 },
                    new PreviewRect { Rect = new Rectangle(32, 0, 32, 32), Number = 2 }
                });
                // 画面外の窓には描画の要求が来ないため、描画を直接走らせる。
                Action paint = () => { using (var shot = new Bitmap(canvas.Width, canvas.Height)) canvas.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height)); };
                paint();
                var pops = Field<IDictionary>(canvas, "selectionPops");
                Assert.AreEqual(0, pops.Count, "nothing pops before a selection");
                selected.Add(2);
                paint();
                Assert.IsTrue(pops.Contains(2), "the newly selected cell pops");
                Assert.IsTrue(WaitUntil(() => pops.Count == 0, 600), "the pop ends");
                paint();
                Assert.AreEqual(0, pops.Count, "a cell that stays selected does not pop again");
            }
        }

        private static void FolderCollapseAndExpandSlide()
        {
            if (!UiMotion.Enabled) return;
            string dir = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerTests-tree-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 1; i <= 4; i++)
                    using (var bmp = new Bitmap(4, 4)) bmp.Save(Path.Combine(dir, "a" + i + ".png"), ImageFormat.Png);
                using (MainForm form = ShownMainForm())
                {
                    typeof(MainForm).GetMethod("AddDirectoryFolder", Flags).Invoke(form, new object[] { dir });
                    typeof(MainForm).GetMethod("UpdateTree", Flags).Invoke(form, null);
                    Pump(20);
                    var tree = Field<TreeView>(form, "treeView");
                    TreeNode folder = tree.Nodes[0];
                    Assert.IsTrue(folder.IsExpanded, "a new folder starts open");

                    folder.Collapse();
                    Assert.IsTrue(Field<object>(form, "treeAccordion") != null, "collapsing slides the rows up");
                    Assert.IsTrue(WaitUntil(() => Field<object>(form, "treeAccordion") == null, 600), "the collapse motion ends");
                    folder.Expand();
                    Assert.IsTrue(Field<object>(form, "treeAccordion") != null, "expanding slides the rows down");
                    Assert.IsTrue(WaitUntil(() => Field<object>(form, "treeAccordion") == null, 600), "the expand motion ends");
                    foreach (Control c in tree.Controls) Assert.IsFalse(c is TreeAccordionOverlay, "nothing is left over the tree");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
