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
                for (int i = 1; i <= 20; i++)
                    using (var bmp = new Bitmap(4, 4)) bmp.Save(Path.Combine(dir, "a" + i + ".png"), ImageFormat.Png);
                using (MainForm form = ShownMainForm())
                {
                    typeof(MainForm).GetMethod("AddDirectoryFolder", Flags).Invoke(form, new object[] { dir });
                    typeof(MainForm).GetMethod("UpdateTree", Flags).Invoke(form, null);
                    Pump(20);
                    var tree = Field<FolderTreeView>(form, "treeView");
                    FolderNode folder = tree.Nodes[0];
                    Assert.IsTrue(folder.IsExpanded, "a new folder starts open");
                    Assert.AreEqual(1f, tree.OpennessOf(folder), "a new folder appears open at once");

                    // 閉じると、中の行が途中の開き具合を通って隠れる（覆いの画像は使わない）。
                    folder.Collapse();
                    Assert.IsTrue(tree.OpennessOf(folder) > 0.5f, "collapsing starts from open");
                    Assert.IsTrue(WaitUntil(() => tree.OpennessOf(folder) == 0f, 600), "the collapse motion ends");
                    Assert.IsTrue(folder.Nodes[0].Bounds.IsEmpty, "closed rows have no place");
                    Assert.IsTrue(tree.GetNodeAt(10, tree.ItemHeight + 5) == null || tree.GetNodeAt(10, tree.ItemHeight + 5).Parent == null, "nothing under a closed folder");
                    folder.Expand();
                    Assert.IsTrue(tree.OpennessOf(folder) < 0.5f, "expanding starts from closed");
                    Assert.IsTrue(WaitUntil(() => tree.OpennessOf(folder) == 1f, 600), "the expand motion ends");
                    Assert.IsTrue(ReferenceEquals(folder.Nodes[0], tree.GetNodeAt(10, tree.ItemHeight + 5)), "the first image row is under the folder");
                    Assert.AreEqual(0, tree.Controls.Count, "nothing is laid over the list");

                    // 途中で開閉し直すと、今の開き具合から戻る（跳ばない）。
                    folder.Collapse();
                    Pump(60);
                    float mid = tree.OpennessOf(folder);
                    folder.Expand();
                    Assert.IsTrue(Math.Abs(tree.OpennessOf(folder) - mid) < 0.15f, "a reversed toggle continues from where it was");
                    Assert.IsTrue(WaitUntil(() => tree.OpennessOf(folder) == 1f, 600), "and ends open");

                    // ホイールのスクロールは、1px 単位で途中を通って目標へ近づく。
                    typeof(Control).GetMethod("OnMouseWheel", Flags).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120) });
                    var offsets = new System.Collections.Generic.List<int>();
                    WaitUntil(() => { offsets.Add(tree.ScrollOffset); return tree.ScrollOffset == tree.ItemHeight * 2; }, 600);
                    Assert.AreEqual(tree.ItemHeight * 2, tree.ScrollOffset, "one wheel notch scrolls two rows");
                    Assert.IsTrue(offsets.Exists(o => o > 0 && o < tree.ItemHeight * 2 && o % tree.ItemHeight != 0), "the scroll passes through in-between pixels");

                    // 監査 P2: 子の画像を選んでから親を閉じると、隠れた画像は選択から外れ、フォルダが選ばれる（Delete の対象が見た目と一致する）。
                    object images = Field<object>(form, "selectedImages");
                    object chosenFolders = Field<object>(form, "selectedFolders");
                    Func<object, object, bool> has = (set, item) => (bool)set.GetType().GetMethod("Contains").Invoke(set, new[] { item });
                    Func<object, int> count = set => (int)set.GetType().GetProperty("Count").GetValue(set, null);
                    typeof(MainForm).GetMethod("TreeView_NodeMouseClick", Flags).Invoke(form, new object[] { tree, new FolderNodeMouseEventArgs(folder.Nodes[0], MouseButtons.Left, 30, 30) });
                    Assert.IsTrue(has(images, folder.Nodes[0].Tag), "an image row is selected");
                    folder.Collapse();
                    Assert.AreEqual(0, count(images), "hidden images leave the selection");
                    Assert.IsTrue(has(chosenFolders, folder.Tag), "the closed folder takes the selection");
                    Assert.IsTrue(ReferenceEquals(tree.SelectedNode, folder), "the list marks the folder");
                    folder.Expand();
                    Assert.IsTrue(WaitUntil(() => tree.OpennessOf(folder) == 1f, 600), "opens again");

                    // 監査 P2: つまみのドラッグ中にマウスの取り込みを奪われたら、ドラッグを終える（ボタンなしの移動でスクロールしない）。
                    Rectangle thumb = (Rectangle)typeof(FolderTreeView).GetMethod("ThumbBounds", Flags).Invoke(tree, null);
                    Assert.IsFalse(thumb.IsEmpty, "the list can scroll");
                    Point grab = new Point(thumb.X + thumb.Width / 2, thumb.Y + thumb.Height / 2);
                    typeof(Control).GetMethod("OnMouseDown", Flags).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.Left, 1, grab.X, grab.Y, 0) });
                    Assert.IsTrue((bool)typeof(FolderTreeView).GetField("dragThumb", Flags).GetValue(tree), "the thumb is grabbed");
                    tree.Capture = false;
                    Assert.IsFalse((bool)typeof(FolderTreeView).GetField("dragThumb", Flags).GetValue(tree), "losing the capture ends the thumb drag");
                    int before = tree.ScrollOffset;
                    typeof(Control).GetMethod("OnMouseMove", Flags).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.None, 0, grab.X, grab.Y + 60, 0) });
                    Assert.AreEqual(before, tree.ScrollOffset, "a button-free move does not scroll");
                    // 取り込みが残っていても、ボタンが離れた移動ならドラッグを終える。
                    typeof(Control).GetMethod("OnMouseDown", Flags).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.Left, 1, grab.X, grab.Y, 0) });
                    typeof(Control).GetMethod("OnMouseMove", Flags).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.None, 0, grab.X, grab.Y + 60, 0) });
                    Assert.IsFalse((bool)typeof(FolderTreeView).GetField("dragThumb", Flags).GetValue(tree), "a button-free move ends the thumb drag");
                    Assert.AreEqual(before, tree.ScrollOffset, "and does not scroll");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
