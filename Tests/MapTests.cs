//==================================================
// MapTests
// マップ編集の保存・操作・参照と、寸法が確定したデバッグ素材を検証する。
//==================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class MapTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static T Field<T>(MainForm f, string n) { return (T)typeof(MainForm).GetField(n, Flags).GetValue(f); }
        private static object Call(MainForm f, string n, params object[] a) { return typeof(MainForm).GetMethod(n, Flags).Invoke(f, a); }
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Map_BoundsLayersAndOverflow", Action = Bounds };
            yield return new TestCase { Name = "Map_NormalizeAndIgnore", Action = Normalize };
            yield return new TestCase { Name = "Map_PackingDoesNotOverlapOrChangeReferences", Action = Packing };
            yield return new TestCase { Name = "Map_AnimationFpsAndMissingFrames", Action = Animation };
            yield return new TestCase { Name = "Map_BrushDoesNotPaintThrough", Action = Brush };
            yield return new TestCase { Name = "Map_ProjectRoundTripAndUndo", Action = RoundTrip };
            yield return new TestCase { Name = "Map_BasisRemovalKeepsDimensions", Action = MissingBasis };
            yield return new TestCase { Name = "Map_IndependentFoldSections", Action = FoldSections };
            yield return new TestCase { Name = "Map_LayerOverlayAndBasisHold", Action = LayerAndHold };
            yield return new TestCase { Name = "Map_SheetOrderMoveAndRoundTrip", Action = OrderModel };
            yield return new TestCase { Name = "Map_SheetReorderKeepsFoldersAndAnimationPick", Action = ReorderInForm };
            yield return new TestCase { Name = "Transition_JumpAndAttackGroupsFold", Action = TransitionGroupsFold };
            yield return new TestCase { Name = "Map_GifWebPExportOneMapLoop", Action = MapLoopExport };
            yield return new TestCase { Name = "Coordinates_CardUvFormatAndHelpMarks", Action = CoordinatesAndHelp };
            yield return new TestCase { Name = "Map_ToolbarRotateFlipMoveErase", Action = Tools };
            yield return new TestCase { Name = "Map_ShiftSelectAssignToAnimation", Action = ShiftAssign };
            yield return new TestCase { Name = "Map_ResetClearsMapPreview", Action = ResetClearsMap };
            yield return new TestCase { Name = "Map_ShiftDragMoveRightHoldDeleteAndDeleteKey", Action = ShiftMoveAndDelete };
            yield return new TestCase { Name = "Map_PreviewRangePlaceToolsMoveDelete", Action = PreviewRange };
            yield return new TestCase { Name = "Map_SheetFillsSpaceBesideLargeChip", Action = GapPlacement };
            yield return new TestCase { Name = "Map_SheetRangeIsRectangle", Action = SheetRectangleRange };
            yield return new TestCase { Name = "Sheet_FillEmptyCellsAndAlignTo4", Action = EmptyCellsAndAlign };
            yield return new TestCase { Name = "Folders_DragMovesTheWholeSelection", Action = FolderGroupMove };
            yield return new TestCase { Name = "Help_CatalogCoversEveryHelp", Action = HelpList };
            yield return new TestCase { Name = "Help_LineBreakRulesAndHintToggle", Action = KinsokuAndHints };
            yield return new TestCase { Name = "Loc_MapScreenHasNoJapaneseInOtherLanguages", Action = MapScreenLanguages };
            yield return new TestCase { Name = "Motion_StartupRevealAndContentCrossfade", Action = RevealAndCrossfade };
        }
        private static void LayerAndHold()
        {
            using (var host = new Form())
            using (var c = new MapCanvas { Document = Sample(), Size = new Size(500, 500), Selected = "base" })
            {
                host.ClientSize = c.Size; host.Controls.Add(c); host.Show(); Pump(50);
                Point p = new Point(c.LayerBounds(2).X + 20, c.LayerBounds(2).Y + 20);
                Mouse(c, "OnMouseDown", MouseButtons.Left, p); Mouse(c, "OnMouseUp", MouseButtons.Left, p);
                Assert.AreEqual(2, c.Document.ActiveLayer, "row selects layer");
                p = new Point(c.LayerEyeBounds(2).X + 10, c.LayerEyeBounds(2).Y + 10);
                Mouse(c, "OnMouseDown", MouseButtons.Left, p); Mouse(c, "OnMouseUp", MouseButtons.Left, p);
                Assert.IsFalse(c.Document.VisibleLayers[2], "eye hides layer");
                Mouse(c, "OnMouseDown", MouseButtons.Right, p); Mouse(c, "OnMouseUp", MouseButtons.Right, p);
                Assert.AreEqual(0, c.Document.Tiles.Count, "layer overlay blocks painting");
                c.Palette = true; c.RefreshPacking(); string assigned = null;
                c.BasisAssigned += id => assigned = id; p = c.CellScreen(0, 0);
                Mouse(c, "OnMouseDown", MouseButtons.Right, p); Mouse(c, "OnMouseUp", MouseButtons.Right, p); Pump(600);
                Assert.IsTrue(typeof(MapCanvas).GetField("basisMenu", Flags).GetValue(c) == null, "short click has no menu");
                Mouse(c, "OnMouseDown", MouseButtons.Right, p); Pump(650);
                var menu = (ContextMenuStrip)typeof(MapCanvas).GetField("basisMenu", Flags).GetValue(c);
                Assert.IsTrue(menu != null && menu.Visible, "hold opens assignment");
                menu.Items[0].PerformClick(); menu.Close(); Mouse(c, "OnMouseUp", MouseButtons.Right, p);
                Assert.AreEqual("base", assigned, "assign held asset"); host.Close();
            }
        }
        private static void OrderModel()
        {
            Func<MapDocument, string> rows = d => MapDocument.RowsKey(d.LayoutRows(2));
            var m = new MapDocument();
            for (int i = 1; i <= 4; i++) m.Assets.Add(new MapAsset { Id = "c" + i, Width = 8, Height = 8, Cell = i });
            Assert.AreEqual("c1,c2|c3,c4", rows(m), "first layout wraps by columns");
            m.FixLayout(2);
            Assert.AreEqual("c1,c2|c3,c4", MapDocument.RowsKey(m.LayoutRows(4)), "columns no longer matter once fixed");
            Assert.IsTrue(m.MoveUnit("c4", 0, 0), "move to front of the first row");
            Assert.AreEqual("c4,c1,c2|c3", rows(m), "row grows to the right");
            Assert.AreEqual(new Size(24, 16), new Size(m.Pack(2).Values.Max(r => r.Right), m.Pack(2).Values.Max(r => r.Bottom)), "packed without gaps");
            Assert.IsTrue(m.MoveUnit("c1", 2, 0), "new row below");
            Assert.AreEqual("c4,c2|c3|c1", rows(m), "taller layout");
            Assert.IsTrue(m.MoveUnit("c3", 0, 9), "end of row");
            Assert.AreEqual("c4,c2,c3|c1", rows(m), "empty rows removed");
            Assert.IsFalse(m.MoveUnit("c3", 0, 2), "same place is no change");
            Assert.AreEqual(new Size(32, 32), m.RangeSize(), "range is the sum of widths by the sum of heights");
            m.Assets.Add(new MapAsset { Id = "c5", Width = 8, Height = 8, Cell = 5 });
            Assert.AreEqual("c4,c2,c3|c1,c5", rows(m), "new image appended within the widest row");
            m.Asset("c2").Available = false;
            Assert.AreEqual("c4,c3|c1,c5", rows(m), "missing image skipped");
            var back = MapDocument.FromJson(m.ToJson());
            Assert.AreEqual(rows(m), rows(back), "rows saved");
            Assert.AreEqual(0, MapDocument.FromJson("{\"Assets\":[]}").Rows.Count, "old files have no rows");
            m.Animations.Add(new MapAnimation { Id = "off", Frames = new List<string> { "c3" }, Enabled = false });
            Assert.IsTrue(MapDocument.FromJson(m.ToJson()).Animations[0].Enabled, "animations always play");
            Assert.IsTrue(m.AnimationOf("c4") == null, "plain chip");
            // アニメーションのコマは、コマ順に左から右へ続けて並び、1行に収まる。
            var g = new MapDocument();
            for (int i = 1; i <= 6; i++) g.Assets.Add(new MapAsset { Id = "g" + i, Width = 8, Height = 8, Cell = i });
            g.Animations.Add(new MapAnimation { Id = "run", Frames = new List<string> { "g5", "g2", "g6" } });
            Assert.AreEqual("g1,g5,g2,g6,g3,g4", string.Join(",", g.PaletteOrder(4).Select(x => x.Id)), "frames gathered at the first frame");
            var packed = g.Pack(4);
            Assert.IsTrue(packed["g5"].Y == packed["g2"].Y && packed["g2"].Y == packed["g6"].Y, "frames stay on one row");
            Assert.IsTrue(packed["g5"].Right == packed["g2"].X && packed["g2"].Right == packed["g6"].X, "frames left to right");
            g.FixLayout(4);
            Assert.IsTrue(g.MoveUnit("g2", 0, 0), "moving one frame moves the group");
            Assert.AreEqual("g5,g2,g6,g1|g3,g4", MapDocument.RowsKey(g.LayoutRows(4)), "group moved together");
            // まとまりごと持ち上がる。
            using (var host = new Form { ClientSize = new Size(400, 300) })
            using (var c = new MapCanvas { Document = g, Palette = true, PaletteColumns = 4, Dock = DockStyle.Fill })
            {
                host.Controls.Add(c); host.Show(); c.RefreshPacking(); c.Fit(true); Pump(350); c.ModifierState = () => Keys.None;   // 実際のキーボードの Shift に左右されない
                var pack = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(c);
                Func<string, Point> at = id => { var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(c, new object[] { (RectangleF)pack[id] }); return new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); };
                Point from = at("g6"), to = new Point(at("g4").X + 40, at("g4").Y);
                Mouse(c, "OnMouseDown", MouseButtons.Left, from); Lift(c);
                var unit = (List<string>)typeof(MapCanvas).GetField("reorderUnit", Flags).GetValue(c);
                Assert.AreEqual("g5,g2,g6", unit == null ? "" : string.Join(",", unit), "whole animation lifted");
                Mouse(c, "OnMouseMove", MouseButtons.Left, to); SettledSnap(c, "SheetLiftedGroup.png"); Mouse(c, "OnMouseUp", MouseButtons.Left, to); Pump(50);
                Assert.AreEqual("g1|g3,g4,g5,g2,g6", MapDocument.RowsKey(g.LayoutRows(4)), "dropped as a group at the end of the row (sheet grows wider)");
                host.Close();
            }
        }
        // 確認ダイアログが出たら、指定のボタンで閉じる（出たかどうかを返す）。
        private static Func<bool> AnswerConfirm(MainForm form, DialogResult answer)
        {
            bool asked = false;
            var timer = new System.Windows.Forms.Timer { Interval = 40 };
            timer.Tick += (s, e) =>
            {
                Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f != form && f.Modal && f.Visible);
                if (dialog == null) return;
                Button button = dialog.Controls.OfType<Button>().FirstOrDefault(x => x.DialogResult == answer);
                if (button == null) return;
                asked = true; timer.Stop(); timer.Dispose(); button.PerformClick();
            };
            timer.Start();
            return () => asked;
        }
        private static void KinsokuAndHints()
        {
            // 禁則処理: どの幅でも枠からはみ出さず、行頭禁則の文字で始まる行・開きかっこで終わる行がない。
            var texts = new List<string>();
            foreach (LanguageInfo info in Loc.All)
                foreach (HelpEntry entry in HelpCatalog.All.Where(x => x.Key != null))
                    foreach (var block in HelpTipStyle.Parse(Loc.GetTable(info.Language)[entry.Key]))
                        if (block.Kind == HelpTipStyle.BlockKind.Paragraph || block.Kind == HelpTipStyle.BlockKind.Operation) texts.Add(block.Text);
            texts.Add("右クリックの「**割り当て**」を使います。（確認あり）「かっこ」が続く文章、ちょっと長めの文章です。");
            texts.Add("基準セルの**0.25〜8倍**に合わない大きさです。ヴァ・ッ・ャのような小さい文字や、ーの長音、……の後も確かめます。");
            Func<List<Tuple<string, bool>>, string> plain = line => string.Concat(line.Select(s => s.Item1));
            Func<List<Tuple<string, bool>>, int> widthOf = line => (int)typeof(HelpTipStyle).GetMethod("SegmentsWidth", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { line });
            for (int width = 120; width <= 360; width += 9)
                foreach (string text in texts)
                {
                    List<List<Tuple<string, bool>>> lines = HelpTipStyle.BreakLines(text, width);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = plain(lines[i]);
                        Assert.IsTrue(widthOf(lines[i]) <= width, "fits " + width + ": " + line);
                        if (line.Length == 0) continue;
                        if (i > 0) Assert.IsTrue(HelpTipStyle.NoLineStart.IndexOf(line[0]) < 0, "no forbidden line start at " + width + ": [" + plain(lines[i - 1]) + "] [" + line + "] in " + text);
                        if (i < lines.Count - 1) Assert.IsTrue(HelpTipStyle.NoLineEnd.IndexOf(line[line.Length - 1]) < 0, "no opening bracket at line end at " + width + ": " + line);
                    }
                    Assert.AreEqual(text.Replace("**", "").Replace(" ", ""), string.Concat(lines.Select(plain)).Replace(" ", ""), "no text is lost");
                }
            // 強調した操作名は途中で切らない。幅より長い1語（パス）は、はみ出さずに途中で切る。
            foreach (int width in new[] { 140, 200, 260 })
            {
                var lines = HelpTipStyle.BreakLines("ここで**Shift＋左クリック**して範囲を選んでから、右クリックします。", width).Select(plain).ToList();
                Assert.IsTrue(lines.Any(l => l.Contains("Shift＋左クリック")), "the operation name stays together at " + width);
            }
            string path = @"C:\Users\someone\Documents\Projects\VeryLongFolderName\slime_attack_frame_0001.png";
            foreach (int width in new[] { 60, 120, 200 })
                foreach (var line in HelpTipStyle.BreakLines(path, width)) Assert.IsTrue(widthOf(line) <= width, "a long path does not overflow at " + width);

            // ヒントの表示の切り替え（基本設定）。切ると操作のヒントは出ず、「？」の説明は出る。
            string root = Path.Combine(Path.GetTempPath(), "Hints-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); Pump(200);
                var check = Field<RoundedCheckBox>(form, "hintsCheckBox"); var tip = Field<ToolTip>(form, "toolTip");
                Assert.IsTrue(check.Checked && tip.Active, "hints are on by default");
                check.Checked = false; Pump(20);
                Assert.IsFalse(tip.Active, "turning it off stops the hints");
                Assert.IsFalse(HelpTipStyle.HintsEnabled, "the choice is saved");
                Assert.IsTrue(File.ReadAllText(Loc.SettingsPath).Contains(HelpTipStyle.HintsSettingKey + "=0"), "saved in the settings file");
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "Parameters")); Pump(400);   // 設定タブの項目は開いたときに並べる
                Assert.IsTrue(AllControls(form).OfType<HelpMark>().Any(m => m.HelpText == Loc.T("help.hints")), "the toggle has a ? help");
                Assert.IsTrue(Field<Control>(form, "hintsRow").Parent != null, "the toggle is on the settings page");
                string shots = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT");
                if (shots != null) using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(shots, "SettingsHints.png")); }
                var markTips = (ToolTip)typeof(HelpMark).GetProperty("Tips", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null);
                Assert.IsTrue(markTips.Active, "the ? help still shows");
                // 「？」は自分で出す: 0.3秒乗せると出る・押すとすぐ出る・離れると消える（アプリが前面でなくても出す）。
                HelpMark hintsMark = AllControls(form).OfType<HelpMark>().First(m => m.HelpText == Loc.T("help.hints"));
                Assert.IsTrue(markTips.ShowAlways, "shown even when the app is not in front");
                int shownBefore = HelpMark.ShowCount;
                typeof(Control).GetMethod("OnMouseEnter", Flags).Invoke(hintsMark, new object[] { EventArgs.Empty });
                Pump(150); Assert.AreEqual(shownBefore, HelpMark.ShowCount, "not yet after 0.15 s");
                Pump(300); Assert.AreEqual(shownBefore + 1, HelpMark.ShowCount, "shown after 0.3 s");
                Assert.AreEqual(Loc.T("help.hints"), markTips.GetToolTip(hintsMark), "the shown text is the help");
                typeof(Control).GetMethod("OnMouseLeave", Flags).Invoke(hintsMark, new object[] { EventArgs.Empty });
                Assert.AreEqual("", markTips.GetToolTip(hintsMark), "leaving hides it");
                typeof(Control).GetMethod("OnMouseDown", Flags).Invoke(hintsMark, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0) });
                Assert.AreEqual(shownBefore + 2, HelpMark.ShowCount, "a click shows it at once");
                typeof(Control).GetMethod("OnMouseLeave", Flags).Invoke(hintsMark, new object[] { EventArgs.Empty });
                check.Checked = true; Pump(20);
                Assert.IsTrue(tip.Active && HelpTipStyle.HintsEnabled, "turned back on");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void HelpList()
        {
            // 台帳: help.* / tooltip.* のキーはすべて登録され、登録したキーは全言語にある。ID は重ならない。
            var ja = Loc.GetTable(UiLanguage.Japanese);
            var registered = new HashSet<string>(HelpCatalog.All.Where(x => x.Key != null).Select(x => x.Key));
            foreach (string key in ja.Keys.Where(k => k.StartsWith("help.") || k.StartsWith("tooltip.")))
                Assert.IsTrue(registered.Contains(key), "registered in the help catalog: " + key);
            foreach (LanguageInfo info in Loc.All)
                foreach (string key in registered)
                    Assert.IsTrue(Loc.GetTable(info.Language).ContainsKey(key), info.Code + " has " + key);
            Assert.AreEqual(HelpCatalog.All.Count, HelpCatalog.All.Select(x => x.Id).Distinct().Count(), "ids are unique");
            // 中身が変わるヒントは確認用データで文章を作れる。
            Assert.IsTrue(HelpCatalog.All.Where(x => x.UsesSample).All(x => !string.IsNullOrEmpty(HelpCatalog.TextOf(x))), "samples produce text");
            Assert.IsTrue(HelpCatalog.All.Where(x => x.Key != null).All(x => HelpCatalog.TextOf(x, UiLanguage.English) == Loc.GetTable(UiLanguage.English)[x.Key]), "texts come from the language files");
        }
        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls) { yield return c; foreach (Control d in AllControls(c)) yield return d; }
        }
        // フォルダ欄: 選択中のものをつかんで動かすと、選択中のものがまとめて動く（2026-10-06 ユーザー指摘）。
        private static void FolderGroupMove()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "GroupMove-" + Guid.NewGuid().ToString("N"));
            var dirs = new[] { "A", "B", "C" };
            int[] counts = { 5, 2, 2 };
            for (int d = 0; d < 3; d++)
            {
                string dir = Path.Combine(baseDir, dirs[d]); Directory.CreateDirectory(dir);
                for (int i = 0; i < counts[d]; i++) using (var bmp = new Bitmap(4, 4)) bmp.Save(Path.Combine(dir, dirs[d].ToLower() + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            string root = Path.Combine(Path.GetTempPath(), "GroupMoveSettings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); Pump(200);
                foreach (string d in dirs) typeof(MainForm).GetMethod("AddDirectoryFolder", Flags).Invoke(form, new object[] { Path.Combine(baseDir, d) });
                Call(form, "UpdateTree"); Call(form, "CommitUndoableChange"); Call(form, "CommitUndoNow"); Pump(50);
                var tree = Field<FolderTreeView>(form, "treeView");
                Func<string> layout = () => string.Join(" | ", tree.Nodes.Cast<FolderNode>().Select(f => f.Text + ":" + string.Join(",", f.Nodes.Cast<FolderNode>().Select(i => Path.GetFileNameWithoutExtension(((string)i.Tag.GetType().GetField("Path").GetValue(i.Tag)))))));
                Func<string, FolderNode> node = name => tree.Nodes.Cast<FolderNode>().SelectMany(f => new[] { f }.Concat(f.Nodes.Cast<FolderNode>()))
                    .First(x => x.Tag.GetType().Name == "ImageFolder" ? x.Text == name : Path.GetFileNameWithoutExtension((string)x.Tag.GetType().GetField("Path").GetValue(x.Tag)) == name);
                object images = Field<object>(form, "selectedImages"), chosenFolders = Field<object>(form, "selectedFolders");
                Action<object, object[]> select = (set, items) => { set.GetType().GetMethod("Clear").Invoke(set, null); foreach (object x in items) set.GetType().GetMethod("Add").Invoke(set, new[] { x }); };
                Func<object, int> count = set => (int)set.GetType().GetProperty("Count").GetValue(set, null);
                string start = layout();
                // 画像 a1〜a3 を選び、a2 をつかんで b0 の後ろへ → a1・a2・a3 が並び順のまま B へ移る。
                select(chosenFolders, new object[0]);
                select(images, new[] { node("a1").Tag, node("a2").Tag, node("a3").Tag });
                Assert.IsTrue((bool)Call(form, "MoveTreeNodes", node("a2"), node("b0"), true), "moved");
                Assert.AreEqual("A:a0,a4 | B:b0,a1,a2,a3,b1 | C:c0,c1", layout(), "the whole selection moved in order");
                Assert.AreEqual(3, count(images), "the moved images stay selected");
                // 選んでいないものをつかんだら、それだけが動く。
                select(images, new[] { node("a1").Tag, node("a2").Tag });
                Assert.IsTrue((bool)Call(form, "MoveTreeNodes", node("b1"), node("c0"), false), "moved one");
                Assert.AreEqual("A:a0,a4 | B:b0,a1,a2,a3 | C:b1,c0,c1", layout(), "an unselected item moves alone");
                // 移動元が空になったら、そのフォルダは消える。
                select(images, new[] { node("a0").Tag, node("a4").Tag });
                Call(form, "MoveTreeNodes", node("a0"), node("C"), true);
                Assert.AreEqual("B:b0,a1,a2,a3 | C:b1,c0,c1,a0,a4", layout(), "the emptied folder is removed");
                // フォルダも、選択中のものをつかめばまとめて動く。
                select(images, new object[0]);
                select(chosenFolders, new[] { node("B").Tag, node("C").Tag });
                Call(form, "CreateEmptyFolder"); Pump(20);
                string newest = tree.Nodes.Cast<FolderNode>().Last().Text;
                select(chosenFolders, new[] { node("B").Tag, node("C").Tag });
                Assert.IsTrue((bool)Call(form, "MoveTreeNodes", node("B"), node(newest), true), "moved folders");
                Assert.AreEqual(newest, tree.Nodes.Cast<FolderNode>().First().Text, "the selected folders moved together after the target");
                Assert.AreEqual(2, count(chosenFolders), "the moved folders stay selected");
                // 元に戻す: 1回で直前の移動が戻り、続けると最初の並びまで戻る。
                Call(form, "UndoLastOperation"); Pump(50);
                Assert.IsTrue(newest != tree.Nodes.Cast<FolderNode>().First().Text, "one undo reverts the last move");
                for (int i = 0; i < 10 && layout() != start; i++) { Call(form, "UndoLastOperation"); Pump(50); }
                Assert.AreEqual(start, layout(), "undo restores the order");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void EmptyCellsAndAlign()
        {
            // 空きセル: フォルダごとに新しい行から始まるので、各フォルダの最後の行に残る空き。
            Func<int[], int, int> empty = (counts, columns) => (int)typeof(MainForm).GetMethod("EmptyCells", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { counts, columns });
            Func<int[], int, int> suggest = (counts, current) => (int)typeof(MainForm).GetMethod("ColumnsWithoutEmptyCells", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { counts.ToList(), current, int.MaxValue });
            Assert.AreEqual(2, empty(new[] { 10 }, 4), "10 frames in 4 columns leave 2 empty cells");
            Assert.AreEqual(5, suggest(new[] { 10 }, 4), "the nearest column count without empty cells");
            Assert.AreEqual(4, suggest(new[] { 12, 8 }, 5), "must divide every folder");
            Assert.AreEqual(1, suggest(new[] { 7, 5 }, 3), "coprime folders need one column");
            Assert.AreEqual(0, empty(new[] { 12, 8 }, 4), "no empty cells after the change");
            // 横セル数の上限（999）を超える数は選ばない（1009 は素数なので 1 列）。Codex 監査 2026-10-06。
            Assert.AreEqual(1, (int)typeof(MainForm).GetMethod("ColumnsWithoutEmptyCells", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new List<int> { 1009 }, 999, 999 }), "stays within the 999 limit");

            string dir = Path.Combine(Path.GetTempPath(), "EmptyCells-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            for (int i = 1; i <= 10; i++) using (var bmp = new Bitmap(5, 3)) bmp.Save(Path.Combine(dir, "f" + i.ToString("00") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            string root = Path.Combine(Path.GetTempPath(), "Align4-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); Pump(200);
                typeof(MainForm).GetMethod("AddDirectoryFolder", Flags).Invoke(form, new object[] { dir });
                Call(form, "UpdateTree");
                var columns = Field<NumericUpDown>(form, "columnsBox"); var button = Field<Button>(form, "fillEmptyCellsButton");
                columns.Value = 4; Call(form, "UpdatePreviewSafe"); Pump(100);
                Assert.IsTrue(button.Visible, "the button appears when there are empty cells");
                string shots = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT");
                if (shots != null)
                    foreach (UiLanguage lang in new[] { UiLanguage.Japanese, UiLanguage.English })
                    {
                        Loc.SetLanguage(lang); Pump(150);
                        using (var image = new Bitmap(form.Width, 130)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(shots, "Toolbar_" + lang + ".png")); }
                    }
                Loc.SetLanguage(UiLanguage.Japanese); Pump(100);
                button.PerformClick(); Call(form, "UpdatePreviewSafe"); Pump(100);
                Assert.AreEqual(5m, columns.Value, "the button picks 5 columns");
                Assert.IsFalse(button.Visible, "the button hides when no cells are empty");
                // 4の倍数にそろえる: 5×3 のコマを横5・縦2 → 25×6 px → 28×8 px（プレビューも書き出しも）。
                var align = Field<RoundedCheckBox>(form, "align4CheckBox");
                Assert.IsFalse(align.Checked, "off by default");
                Assert.AreEqual(new Size(25, 6), (Size)typeof(MainForm).GetField("lastSheetSize", Flags).GetValue(form), "natural size");
                align.Checked = true; Call(form, "UpdatePreviewSafe"); Pump(100);
                Assert.AreEqual(new Size(28, 8), (Size)typeof(MainForm).GetField("lastSheetSize", Flags).GetValue(form), "the preview sheet is padded to multiples of 4");
                object layout = Call(form, "BuildExportLayout", Call(form, "CaptureExportSnapshot"));
                Assert.AreEqual(28, (int)layout.GetType().GetField("Width").GetValue(layout), "the export width is a multiple of 4");
                Assert.AreEqual(8, (int)layout.GetType().GetField("Height").GetValue(layout), "the export height is a multiple of 4");
                Assert.IsTrue(File.ReadAllText(Loc.SettingsPath).Contains("alignSize4=1"), "the toggle is saved");
                Assert.IsTrue(Field<MapCanvas>(form, "mapPalette").AlignSheetTo4, "the map sheet follows the toggle");
                align.Checked = false; Pump(20);
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
            // マップチップのシートも、4の倍数にそろえた大きさで UV を出す。
            var d = new MapDocument();
            for (int i = 0; i < 3; i++) d.Assets.Add(new MapAsset { Width = 6, Height = 5, Cell = i + 1 });
            d.SetBasis(d.Assets[0]); d.Rows = new List<List<string>> { d.Assets.Select(x => x.Id).ToList() };
            using (var c = new MapCanvas { Document = d, Palette = true, PaletteColumns = 4 })
            {
                c.RefreshPacking();
                Assert.AreEqual(new Size(18, 5), c.SheetSize, "natural map sheet");
                c.AlignSheetTo4 = true;
                Assert.AreEqual(new Size(20, 8), c.SheetSize, "padded map sheet");
            }
        }
        // シートの範囲選択はプレビューと同じく四角く選ぶ（番号の順で前の行の残りまで選ばない）。
        private static void SheetRectangleRange()
        {
            var d = new MapDocument(); int cell = 0;
            Func<string> add = () => { var asset = new MapAsset { Width = 8, Height = 8, Cell = ++cell }; d.Assets.Add(asset); return asset.Id; };
            List<string> r1 = Enumerable.Range(0, 4).Select(i => add()).ToList(), r2 = Enumerable.Range(0, 4).Select(i => add()).ToList();
            d.SetBasis(d.Assets[0]); d.Rows = new List<List<string>> { r1, r2 };
            using (var c = new MapCanvas { Document = d, Palette = true, PaletteColumns = 4, Size = new Size(320, 240) })
            {
                Keys mods = Keys.Shift;
                c.RefreshPacking(); c.ModifierState = () => mods;
                typeof(MapCanvas).GetField("zoom", Flags).SetValue(c, 4f); typeof(MapCanvas).GetField("pan", Flags).SetValue(c, new PointF(20, 20));
                Func<float, float, Point> at = (x, y) => new Point((int)(20 + x * 4), (int)(20 + y * 4));
                Func<string> chosen = () => string.Join(",", r1.Concat(r2).Where(c.SelectedSet.Contains).Select(id => (r1.Contains(id) ? "a" + r1.IndexOf(id) : "b" + r2.IndexOf(id))));
                // Shift＋左クリックで a2、次に b1 → a1・a2・b1・b2 の四角だけ（a3 は選ばない）。
                Mouse(c, "OnMouseDown", MouseButtons.Left, at(20, 4)); Mouse(c, "OnMouseUp", MouseButtons.Left, at(20, 4));
                Mouse(c, "OnMouseDown", MouseButtons.Left, at(12, 12)); Mouse(c, "OnMouseUp", MouseButtons.Left, at(12, 12));
                Assert.AreEqual("a1,a2,b1,b2", chosen(), "Shift+click selects the box between the two chips");
                // 何もない所から Shift＋左ドラッグで囲む → 掛かったチップだけ。
                Mouse(c, "OnMouseDown", MouseButtons.Left, at(-3, 4)); Mouse(c, "OnMouseMove", MouseButtons.Left, at(10, 12));
                Assert.IsFalse(((RectangleF)typeof(MapCanvas).GetField("selectBox", Flags).GetValue(c)).IsEmpty, "the dragged box is shown");
                Mouse(c, "OnMouseUp", MouseButtons.Left, at(10, 12));
                Assert.AreEqual("a0,a1,b0,b1", chosen(), "Shift+drag selects the chips inside the box");
                Assert.IsTrue(((RectangleF)typeof(MapCanvas).GetField("selectBox", Flags).GetValue(c)).IsEmpty, "the box disappears on release");
                // 四角く選んだあと、選んだチップの上から Shift＋ドラッグ → 選んだチップがまとめて持ち上がる（2026-10-06 ユーザー指摘）。
                Mouse(c, "OnMouseDown", MouseButtons.Left, at(12, 4)); Mouse(c, "OnMouseMove", MouseButtons.Left, at(20, 20));
                var unit = (List<string>)typeof(MapCanvas).GetField("reorderUnit", Flags).GetValue(c);
                Assert.IsTrue(unit != null && unit.Count == 4, "Shift+drag on the box selection lifts all four");
                Mouse(c, "OnMouseUp", MouseButtons.Left, at(20, 20));
                Assert.AreEqual(4, c.SelectedSet.Count, "the selection stays after the move");
                // 2行にまたがる選択は、形（2×2）を保ったまま置かれる（横一列にならない。2026-10-06 ユーザー指摘）。
                var placed = d.Pack(4);
                Assert.IsTrue(placed[r1[0]].X == placed[r2[0]].X && placed[r1[1]].X == placed[r2[1]].X, "columns stay aligned: " + placed[r1[0]] + " " + placed[r2[0]]);
                Assert.IsTrue(placed[r2[0]].Y == placed[r1[0]].Y + 8 && placed[r1[1]].X == placed[r1[0]].X + 8, "the 2x2 shape is kept");
                // 持ち上げて動かさずに離したら、並びは変わらない。
                string keyBefore = MapDocument.RowsKey(d.LayoutRows(4));
                c.RefreshPacking();
                Rectangle stay = d.Pack(4)[r1[0]];
                mods = Keys.None;
                Mouse(c, "OnMouseDown", MouseButtons.Left, at(stay.X + 4, stay.Y + 4)); Lift(c);
                Mouse(c, "OnMouseUp", MouseButtons.Left, at(stay.X + 4, stay.Y + 4));
                Assert.AreEqual(4, c.SelectedSet.Count, "the selection stays");
                Assert.AreEqual(keyBefore, MapDocument.RowsKey(d.LayoutRows(4)), "lifting and releasing in place changes nothing");
                c.RefreshPacking();
                // 選んだチップの上で長押し（Shift なし）でも、まとめて持ち上がる。
                mods = Keys.None;
                var packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(c);
                Rectangle chosenRect = packing[c.SelectedSet.First()];
                Point hold = at(chosenRect.X + 4, chosenRect.Y + 4);
                Mouse(c, "OnMouseDown", MouseButtons.Left, hold); Lift(c);
                unit = (List<string>)typeof(MapCanvas).GetField("reorderUnit", Flags).GetValue(c);
                Assert.IsTrue(unit != null && unit.Count == 4, "a long press on the selection lifts all four");
                Mouse(c, "OnMouseUp", MouseButtons.Left, hold);
            }
        }
        // Codex の再現（2026-10-05）と同じ形: 8×8 のチップ6枚の右に 32×32 の木箱がある行。木箱の左下に空きができ、そこへ置けなかった。
        private static void GapPlacement()
        {
            var d = new MapDocument(); int cell = 0;
            Func<int, int, string> add = (w, h) => { var asset = new MapAsset { Width = w, Height = h, Cell = ++cell }; d.Assets.Add(asset); return asset.Id; };
            List<string> r1 = Enumerable.Range(0, 8).Select(i => add(8, 8)).ToList();
            List<string> r2 = Enumerable.Range(0, 6).Select(i => add(8, 8)).ToList(); string crate = add(32, 32);
            List<string> r3 = Enumerable.Range(0, 10).Select(i => add(8, 8)).ToList();
            d.SetBasis(d.Assets[0]);
            d.Rows = new List<List<string>> { r1, r2.Concat(new[] { crate }).ToList(), r3 };
            Action<Dictionary<string, Rectangle>, string> noOverlap = (packed, label) =>
            {
                var list = packed.ToList();
                for (int i = 0; i < list.Count; i++) for (int j = i + 1; j < list.Count; j++)
                        Assert.IsFalse(list[i].Value.IntersectsWith(list[j].Value), label + ": chips do not overlap");
            };
            var p = d.Pack(8);
            noOverlap(p, "packed");
            Assert.AreEqual(new Rectangle(48, 8, 32, 32), p[crate], "the crate keeps its place");
            Assert.AreEqual(new Point(0, 16), p[r3[0]].Location, "the next row rises into the space beside the crate");
            Assert.AreEqual(new Point(48, 40), p[r3[6]].Location, "chips under the crate start below it");
            var hole = new Rectangle(0, 24, 48, 16);
            Assert.IsFalse(p.Values.Any(r => r.IntersectsWith(hole)), "space is left beside the crate for the drop test");
            // 番号は読み順（上から、同じ高さなら左から）。
            var numbers = MapDocument.NumbersOf(p);
            var reading = p.OrderBy(x => x.Value.Y).ThenBy(x => x.Value.X).Select(x => x.Key).ToList();
            Assert.AreEqual(string.Join(",", Enumerable.Range(1, reading.Count)), string.Join(",", reading.Select(id => numbers[id])), "numbers follow the reading order");
            // 空きを指して離すと、その空きへ入る。
            using (var c = new MapCanvas { Document = d, Palette = true, PaletteColumns = 8, Size = new Size(640, 480) })
            {
                c.RefreshPacking();
                typeof(MapCanvas).GetField("zoom", Flags).SetValue(c, 4f); typeof(MapCanvas).GetField("pan", Flags).SetValue(c, PointF.Empty);
                string moving = r1[2]; Rectangle from = p[moving];
                typeof(MapCanvas).GetField("liftCandidate", Flags).SetValue(c, moving);
                typeof(MapCanvas).GetField("liftPoint", Flags).SetValue(c, new Point((from.X + 4) * 4, (from.Y + 4) * 4));
                typeof(MapCanvas).GetMethod("StartReorder", Flags).Invoke(c, null);
                Point drop = new Point(4 * 4, 28 * 4);
                Mouse(c, "OnMouseMove", MouseButtons.Left, drop); Mouse(c, "OnMouseUp", MouseButtons.Left, drop);
                var after = d.Pack(8);
                Assert.IsTrue(after[moving].Contains(4, 28), "dropped into the space beside the crate: " + after[moving]);
                noOverlap(after, "after the drop");
                Assert.AreEqual(new Rectangle(48, 8, 32, 32), after[crate], "the crate does not move");
                // 保存して読み直しても同じ位置。
                var again = MapDocument.FromJson(d.ToJson()).Pack(8);
                Assert.IsTrue(after.All(x => again[x.Key] == x.Value), "the placement survives save and reload");
            }
            // 調査用（GAP_PROBE_MAP=ユーザーのプロジェクトから取り出した map の JSON）: 実データでも重ならず、空きへ置けるか。
            string probe = Environment.GetEnvironmentVariable("GAP_PROBE_MAP");
            if (probe != null && File.Exists(probe))
            {
                MapDocument real = MapDocument.FromJson(File.ReadAllText(probe));
                var packed = real.Pack(8); noOverlap(packed, "real map");
                string crateId = real.Assets.First(x => Path.GetFileName(x.Path) == "crates_stack_4x4_32x32.png").Id;
                string tileId = real.Assets.First(x => Path.GetFileName(x.Path) == "ground_2_0.png").Id;
                int sheetW = packed.Values.Max(r => r.Right), sheetH = packed.Values.Max(r => r.Bottom);
                string dump = Environment.GetEnvironmentVariable("GAP_PROBE_DUMP");
                if (dump != null) File.WriteAllLines(dump, packed.Select(x => x.Key + "," + x.Value.X + "," + x.Value.Y + "," + x.Value.Width + "," + x.Value.Height));
                Console.WriteLine("  GAP real: crate=" + packed[crateId] + " ground_2_0=" + packed[tileId] + " sheet=" + sheetW + "x" + sheetH);
                // 木箱の左で、まだ空いているマスを探して、そこへ ground_2_0 を落とす。
                Rectangle crateRect = packed[crateId]; Point free = Point.Empty; bool found = false;
                for (int y = crateRect.Top; y < crateRect.Bottom && !found; y += real.CellHeight)
                    for (int x = 0; x < crateRect.Left && !found; x += real.CellWidth)
                        if (!packed.Values.Any(r => r.Contains(x, y))) { free = new Point(x, y); found = true; }
                Console.WriteLine("  GAP real: free cell beside the crate = " + (found ? free.ToString() : "none (the space is filled)"));
                if (found)
                    using (var c = new MapCanvas { Document = real, Palette = true, PaletteColumns = 8, Size = new Size(640, 480) })
                    {
                        c.RefreshPacking();
                        typeof(MapCanvas).GetField("zoom", Flags).SetValue(c, 4f); typeof(MapCanvas).GetField("pan", Flags).SetValue(c, PointF.Empty);
                        Rectangle from = packed[tileId];
                        typeof(MapCanvas).GetField("liftCandidate", Flags).SetValue(c, tileId);
                        typeof(MapCanvas).GetField("liftPoint", Flags).SetValue(c, new Point((from.X + 4) * 4, (from.Y + 4) * 4));
                        typeof(MapCanvas).GetMethod("StartReorder", Flags).Invoke(c, null);
                        Point drop = new Point((free.X + 4) * 4, (free.Y + 4) * 4);
                        Mouse(c, "OnMouseMove", MouseButtons.Left, drop); Mouse(c, "OnMouseUp", MouseButtons.Left, drop);
                        var after2 = real.Pack(8); noOverlap(after2, "real map after drop");
                        Console.WriteLine("  GAP real: dropped at " + new Point(free.X + 4, free.Y + 4) + " -> " + after2[tileId] + " crate=" + after2[crateId]);
                        Assert.IsTrue(after2[tileId].Contains(free.X + 4, free.Y + 4), "real map: dropped into the free cell");
                    }
            }
        }
        private static void PreviewRange()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapPreviewRange-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                var map = Field<MapDocument>(form, "mapDocument"); var canvas = Field<MapCanvas>(form, "mapCanvas");
                Keys modifiers = Keys.None; canvas.ModifierState = () => modifiers;
                map.Tiles.Clear(); map.ActiveLayer = 0; canvas.Invalidate(); Pump(50);
                Func<int, int, Point> at = (x, y) =>
                {
                    var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(canvas, new object[] { new RectangleF((x + .5f) * map.CellWidth, (y + .5f) * map.CellHeight, 0, 0) });
                    return new Point((int)r.X, (int)r.Y);
                };
                // 配置のチップ: 1マスの大きさのチップを選ぶ。
                MapAsset small = map.Assets.First(x => x.Available && x.Width == map.CellWidth && x.Height == map.CellHeight && map.AnimationOf(x.Id) == null);
                canvas.Selected = small.Id; canvas.SelectedAnimation = false; canvas.Eraser = false;
                // Shift＋左ドラッグで 3×2 マスを選ぶ。
                modifiers = Keys.Shift;
                Mouse(canvas, "OnMouseDown", MouseButtons.Left, at(2, 2)); Mouse(canvas, "OnMouseMove", MouseButtons.Left, at(4, 3)); Mouse(canvas, "OnMouseUp", MouseButtons.Left, at(4, 3));
                modifiers = Keys.None;
                Assert.AreEqual(new Rectangle(2, 2, 3, 2), canvas.CellSelection, "Shift+drag selects a cell range");
                // 範囲の中を右クリック: まとめて置く（1回で元に戻せる）。
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(3, 2)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(3, 2)); Pump(50);
                map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(6, map.Tiles.Count, "right-click inside the range fills it");
                // ツール（時計回り）: 範囲のチップすべてを回す。
                canvas.Eraser = true; canvas.ActiveTool = MapTool.RotateClockwise;
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(2, 3)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(2, 3)); Pump(50);
                Assert.IsTrue(map.Tiles.All(x => x.Rotation == 1), "the tool rotates every tile in the range");
                canvas.ActiveTool = MapTool.Flip;
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(2, 3)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(2, 3)); Pump(50);
                Assert.IsTrue(map.Tiles.All(x => x.FlipX), "the tool flips every tile in the range");
                // 移動: 範囲ごと掴んで動かす。範囲も付いてくる。
                canvas.ActiveTool = MapTool.Move;
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(2, 2)); Mouse(canvas, "OnMouseMove", MouseButtons.Right, at(7, 5)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(7, 5)); Pump(50);
                Assert.AreEqual(6, map.Tiles.Count, "moving keeps every tile");
                Assert.IsTrue(map.Tiles.All(x => x.X >= 7 && x.X <= 9 && x.Y >= 5 && x.Y <= 6), "the range moved together");
                Assert.AreEqual(new Rectangle(7, 5, 3, 2), canvas.CellSelection, "the range follows the tiles");
                // 移動中に Esc を押すと、離しても動かない（範囲・1枚とも。Codex 監査 2026-10-06）。
                string before = string.Join(";", map.Tiles.Select(x => x.X + "," + x.Y));
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(7, 5)); Mouse(canvas, "OnMouseMove", MouseButtons.Right, at(12, 9));
                typeof(Control).GetMethod("OnKeyDown", Flags).Invoke(canvas, new object[] { new KeyEventArgs(Keys.Escape) });
                Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(12, 9)); Pump(30);
                Assert.AreEqual(before, string.Join(";", map.Tiles.Select(x => x.X + "," + x.Y)), "Esc cancels the range move");
                Assert.AreEqual(new Rectangle(7, 5, 3, 2), canvas.CellSelection, "Esc during a move keeps the range");
                canvas.ClearCellSelection();
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(7, 5)); Mouse(canvas, "OnMouseMove", MouseButtons.Right, at(1, 1));
                typeof(Control).GetMethod("OnKeyDown", Flags).Invoke(canvas, new object[] { new KeyEventArgs(Keys.Escape) });
                Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(1, 1)); Pump(30);
                Assert.AreEqual(before, string.Join(";", map.Tiles.Select(x => x.X + "," + x.Y)), "Esc cancels a single move");
                modifiers = Keys.Shift;
                Mouse(canvas, "OnMouseDown", MouseButtons.Left, at(7, 5)); Mouse(canvas, "OnMouseMove", MouseButtons.Left, at(9, 6)); Mouse(canvas, "OnMouseUp", MouseButtons.Left, at(9, 6));
                modifiers = Keys.None;
                // 範囲の外を右クリックすると、範囲選択が外れていつもどおり1マスに使う。
                canvas.ActiveTool = MapTool.Erase;
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(0, 0)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(0, 0)); Pump(50);
                Assert.IsTrue(canvas.CellSelection.IsEmpty, "right-click outside clears the range");
                // Delete: 範囲のチップを確認してから消す。
                modifiers = Keys.Shift;
                Mouse(canvas, "OnMouseDown", MouseButtons.Left, at(7, 5)); Mouse(canvas, "OnMouseMove", MouseButtons.Left, at(8, 6)); Mouse(canvas, "OnMouseUp", MouseButtons.Left, at(8, 6));
                modifiers = Keys.None;
                canvas.Focus(); Pump(20);
                Func<bool> asked = AnswerConfirm(form, DialogResult.OK);
                Call(form, "MainForm_KeyDown", form, new KeyEventArgs(Keys.Delete));
                Assert.IsTrue(asked(), "Delete asks first");
                Assert.AreEqual(2, map.Tiles.Count, "the 2x2 range was deleted");
                Call(form, "UndoLastOperation"); Pump(100); map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(6, map.Tiles.Count, "undo restores them");
                // 配置チップを変えてから置き、元に戻す → 置いたチップだけ戻り、配置チップは変わらない（2026-10-06 ユーザー指摘）。
                MapAsset other = map.Assets.First(x => x.Available && x.Id != small.Id && x.Id != map.BasisId && map.AnimationOf(x.Id) == null);
                canvas.Eraser = false; canvas.Selected = other.Id; canvas.SelectedAnimation = false;
                int tilesBefore = map.Tiles.Count;
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, at(15, 15)); Mouse(canvas, "OnMouseUp", MouseButtons.Right, at(15, 15)); Pump(50);
                Assert.AreEqual(tilesBefore + 1, Field<MapDocument>(form, "mapDocument").Tiles.Count, "placed one tile");
                Call(form, "UndoLastOperation"); Pump(100); map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(tilesBefore, map.Tiles.Count, "undo removes the placed tile");
                Assert.AreEqual(other.Id, canvas.Selected, "undo keeps the chosen placement chip");
                Call(form, "RedoLastOperation"); Pump(100);
                Assert.AreEqual(other.Id, canvas.Selected, "redo keeps the chosen placement chip");
                Call(form, "UndoLastOperation"); Pump(100); map = Field<MapDocument>(form, "mapDocument");
                // Esc で範囲選択を外す。
                typeof(Control).GetMethod("OnKeyDown", Flags).Invoke(canvas, new object[] { new KeyEventArgs(Keys.Escape) });
                Assert.IsTrue(canvas.CellSelection.IsEmpty, "Esc clears the range");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void ShiftMoveAndDelete()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapShiftMove-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                var map = Field<MapDocument>(form, "mapDocument"); var palette = Field<MapCanvas>(form, "mapPalette"); var canvas = Field<MapCanvas>(form, "mapCanvas");
                Keys modifiers = Keys.None; palette.ModifierState = () => modifiers;
                Func<string, Point> at = id =>
                {
                    var packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(palette);
                    var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(palette, new object[] { (RectangleF)packing[id] });
                    return new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                };
                List<string> plain = map.PaletteOrder().Select(x => x.Id).Where(id => map.AnimationOf(id) == null).ToList();
                Assert.IsTrue(plain.Count >= 4, "enough plain chips");

                // 右ボタンは、長押しでないと分かる（離す）まで配置に使うチップにしない。
                string chosen = canvas.Selected, target = plain.First(id => id != chosen);
                Mouse(palette, "OnMouseDown", MouseButtons.Right, at(target));
                Assert.AreEqual(chosen, canvas.Selected, "not chosen while the right button may still become a long press");
                Mouse(palette, "OnMouseUp", MouseButtons.Right, at(target));
                Assert.AreEqual(target, canvas.Selected, "a short right-click chooses the chip on release");
                // 右長押しのメニューに「削除」がある。長押しになったら配置のチップは変えない。
                string other = plain.First(id => id != target);
                Mouse(palette, "OnMouseDown", MouseButtons.Right, at(other));
                typeof(MapCanvas).GetMethod("ShowBasisAssignment", Flags).Invoke(palette, null);
                var menu = (ContextMenuStrip)typeof(MapCanvas).GetField("basisMenu", Flags).GetValue(palette);
                Assert.IsTrue(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == Loc.T("button.delete")), "long press offers Delete");
                menu.Close(); Mouse(palette, "OnMouseUp", MouseButtons.Right, at(other));
                Assert.AreEqual(target, canvas.Selected, "a long press does not choose the chip");

                // 選んだ範囲を Shift＋左ドラッグすると、まとめて動く（長押しを待たない）。
                string a0 = plain[0], a1 = plain[1], last = map.PaletteOrder().Last().Id;
                modifiers = Keys.Shift;
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(a0)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(a0));
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(a1)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(a1));
                Assert.IsTrue(palette.SelectedSet.Contains(a0) && palette.SelectedSet.Contains(a1), "range selected");
                int selectedCount = palette.SelectedSet.Count;
                Point drop = new Point(at(last).X + 6, at(last).Y);
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(a0));
                Mouse(palette, "OnMouseMove", MouseButtons.Left, new Point(at(a0).X + 20, at(a0).Y + 20));
                Assert.IsTrue(typeof(MapCanvas).GetField("reorderId", Flags).GetValue(palette) != null, "Shift+drag on the selection lifts it at once");
                var unit = (List<string>)typeof(MapCanvas).GetField("reorderUnit", Flags).GetValue(palette);
                Assert.IsTrue(unit.Contains(a0) && unit.Contains(a1), "the whole selection is lifted");
                Mouse(palette, "OnMouseMove", MouseButtons.Left, drop); Mouse(palette, "OnMouseUp", MouseButtons.Left, drop); Pump(50);
                List<string> order = map.PaletteOrder().Select(x => x.Id).ToList();
                Assert.IsTrue(order.IndexOf(a1) == order.IndexOf(a0) + 1 && order.IndexOf(a0) > order.IndexOf(last), "the selection moved together after the last chip");
                Assert.AreEqual(selectedCount, palette.SelectedSet.Count, "the selection stays");
                modifiers = Keys.None;

                // Delete / Backspace: 選んだ範囲を、確認してから削除する。取り消せば何も消えない。
                Func<int> count = () => ((IList)Call(form, "GetAllItems")).Count;
                int before = count();
                Func<bool> asked = AnswerConfirm(form, DialogResult.Cancel);
                Call(form, "MainForm_KeyDown", form, new KeyEventArgs(Keys.Back));
                Assert.IsTrue(asked(), "Backspace asks first");
                Assert.AreEqual(before, count(), "cancel keeps the chips");
                Assert.AreEqual(selectedCount, palette.SelectedSet.Count, "cancel keeps the selection");
                asked = AnswerConfirm(form, DialogResult.OK);
                Call(form, "MainForm_KeyDown", form, new KeyEventArgs(Keys.Delete));
                Assert.IsTrue(asked(), "Delete asks first");
                Assert.AreEqual(before - selectedCount, count(), "the selected chips are deleted");
                Assert.AreEqual(0, palette.SelectedSet.Count, "selection cleared after deleting");
                Call(form, "UndoLastOperation"); Pump(100);
                Assert.AreEqual(before, count(), "undo brings them back");
                // 範囲選択の右クリックメニューの「削除」も、確認してから選んだチップを消す（2026-10-06 ユーザー指示）。
                {
                    map = Field<MapDocument>(form, "mapDocument"); palette.RefreshPacking(); Pump(50);
                    List<string> plainNow = map.PaletteOrder().Select(x => x.Id).Where(id => map.AnimationOf(id) == null).ToList();
                    modifiers = Keys.Shift;
                    Mouse(palette, "OnMouseDown", MouseButtons.Left, at(plainNow[0])); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(plainNow[0]));
                    Mouse(palette, "OnMouseDown", MouseButtons.Left, at(plainNow[0])); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(plainNow[0]));
                    modifiers = Keys.None;
                    int chosenNow = palette.SelectedSet.Count, beforeMenu = count();
                    Assert.IsTrue(chosenNow >= 1, "a range is selected");
                    Call(form, "ShowMapSelectionMenu", at(plainNow[0])); Pump(50);
                    var selectionMenu = Field<ContextMenuStrip>(form, "mapSelectionMenu");
                    ToolStripItem delete = selectionMenu.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == Loc.T("menu.sheetDelete"));
                    Assert.IsTrue(delete != null, "the selection menu has Delete");
                    selectionMenu.Close();
                    Func<bool> askedMenu = AnswerConfirm(form, DialogResult.OK);
                    delete.PerformClick(); Pump(600);   // シートの作り直し（並び情報の整理）まで待つ
                    Assert.IsTrue(askedMenu(), "the menu Delete asks first");
                    Assert.AreEqual(beforeMenu - chosenNow, count(), "the selected chips are deleted from the menu");
                    Call(form, "UndoLastOperation"); Pump(100);
                    Assert.AreEqual(beforeMenu, count(), "one undo brings them back (no extra step from the sheet rebuild)");
                    map = Field<MapDocument>(form, "mapDocument");
                }
                // 右長押しの「削除」も確認してから消す。
                map = Field<MapDocument>(form, "mapDocument");
                string victim = map.PaletteOrder().Select(x => x.Id).First(id => map.AnimationOf(id) == null);
                asked = AnswerConfirm(form, DialogResult.OK);
                Call(form, "DeleteMapChips", new List<string> { victim });
                Assert.IsTrue(asked(), "right-hold Delete asks first");
                Assert.AreEqual(before - 1, count(), "one chip deleted");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void ReorderInForm()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapReorder-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                var map = Field<MapDocument>(form, "mapDocument"); var palette = Field<MapCanvas>(form, "mapPalette"); var canvas = Field<MapCanvas>(form, "mapCanvas"); palette.ModifierState = () => Keys.None;
                Func<string> folderOrder = () => string.Join("|", Field<IList>(form, "folders").Cast<object>().SelectMany(f => ((IList)f.GetType().GetField("Items").GetValue(f)).Cast<object>().Select(i => (string)i.GetType().GetField("Path").GetValue(i))));
                string folders = folderOrder();
                List<string> before = map.PaletteOrder().Select(a => a.Id).ToList();
                var packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(palette);
                Func<string, Point> at = id => { var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(palette, new object[] { (RectangleF)packing[id] }); return new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); };
                Point from = at(before[0]), to = new Point(at(before[2]).X + 6, at(before[2]).Y);   // 中心より右＝そのチップの後ろへ
                Mouse(palette, "OnMouseDown", MouseButtons.Left, from); Mouse(palette, "OnMouseUp", MouseButtons.Left, from); Pump(450);
                Assert.AreEqual(string.Join(",", before), string.Join(",", map.PaletteOrder().Select(a => a.Id)), "short press does not lift");
                Mouse(palette, "OnMouseDown", MouseButtons.Left, from); Lift(palette);
                Assert.IsTrue(typeof(MapCanvas).GetField("reorderId", Flags).GetValue(palette) != null, "hold lifts the chip");
                Mouse(palette, "OnMouseMove", MouseButtons.Left, to); SettledSnap(palette, "SheetLifted.png"); Mouse(palette, "OnMouseUp", MouseButtons.Left, to); Pump(50);
                List<string> after = map.PaletteOrder().Select(a => a.Id).ToList();
                Assert.AreEqual(before[0], after[2], "dropped where released");
                Assert.AreEqual(before[1], after[0], "others shift");
                Assert.AreEqual(folders, folderOrder(), "folder list unchanged");
                // セル番号はシートの並び順。並び替えると振り直される（フォルダのセル番号ではない）。
                Assert.AreEqual(3, palette.Numbers[before[0]], "moved chip takes the number of its new place");
                Assert.AreEqual(1, palette.Numbers[before[1]], "others are renumbered");
                Assert.AreEqual(before[0], ((MapAsset)typeof(MainForm).GetMethod("MapAssetAtNumber", Flags).Invoke(form, new object[] { 3 })).Id, "number input finds the chip by sheet number");
                // 書き出しの番号も同じ番号（読み順 1, 2, 3…）。
                object snapshot = Call(form, "CaptureExportSnapshot");
                object exportLayout = Call(form, "BuildExportLayout", snapshot);
                var cells = ((IEnumerable)exportLayout.GetType().GetField("Cells").GetValue(exportLayout)).Cast<object>()
                    .Select(c => new { Rect = (Rectangle)c.GetType().GetField("Rect").GetValue(c), Number = (int)c.GetType().GetField("CellNumber").GetValue(c) })
                    .OrderBy(c => c.Rect.Y).ThenBy(c => c.Rect.X).Select(c => c.Number).ToList();
                Assert.AreEqual(string.Join(",", Enumerable.Range(1, cells.Count)), string.Join(",", cells), "exported numbers follow the sheet");
                Call(form, "UndoLastOperation"); Pump(50); map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(string.Join(",", before), string.Join(",", map.PaletteOrder().Select(a => a.Id)), "undo restores order");
                // 背景と外側の縦横番号もマップチップに効く。
                var backgrounds = Field<ComboBox>(form, "backgroundPaletteComboBox"); backgrounds.SelectedIndex = 2; Pump(50);
                Assert.AreEqual(Color.FromArgb(29, 26, 22).ToArgb(), palette.BackColor.ToArgb(), "sheet background follows the setting");
                var axis = Field<RoundedCheckBox>(form, "axisNumbersCheckBox"); axis.Checked = true; Pump(300);
                Assert.IsTrue(palette.ShowAxisNumbers, "axis numbers on the map sheet");
                Snap(palette, "SheetAxisNumbers.png");
                axis.Checked = false; backgrounds.SelectedIndex = 0; Pump(300);
                MapAnimation water = map.Animations[0]; string frame = water.Frames[2];
                packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(palette);
                Point p = at(frame); Mouse(palette, "OnMouseDown", MouseButtons.Right, p); Mouse(palette, "OnMouseUp", MouseButtons.Right, p);
                Assert.IsTrue(canvas.SelectedAnimation, "animation frame picks the clip"); Assert.AreEqual(water.Id, canvas.Selected, "clip id");
                p = at(map.BasisId); Mouse(palette, "OnMouseDown", MouseButtons.Right, p); Mouse(palette, "OnMouseUp", MouseButtons.Right, p);
                Assert.IsFalse(canvas.SelectedAnimation, "plain chip stays a chip");
                Snap(palette, "SheetRainbow.png");
                // カラー調整（乗算・緑だけ残す）がマップ表示にも掛かる。
                Func<string, Bitmap> image = id => (Bitmap)typeof(MainForm).GetMethod("MapImage", Flags).Invoke(form, new object[] { id });
                MapAsset grass = map.Asset(map.BasisId); Color plain;
                using (var original = new Bitmap(grass.Path)) plain = Enumerable.Range(0, original.Width * original.Height).Select(i => original.GetPixel(i % original.Width, i / original.Width)).First(c => c.A > 0 && c.R > 40);
                Bitmap untinted = image(grass.Id);
                typeof(MainForm).GetField("colorBlendMode", Flags).SetValue(form, SpriteColorBlendMode.Multiply);
                typeof(MainForm).GetField("spriteAdjustmentColor", Flags).SetValue(form, Color.FromArgb(0, 255, 0));
                typeof(MainForm).GetField("spriteAdjustmentStrength", Flags).SetValue(form, 100);
                Call(form, "UpdateColorAdjustmentButton");
                Bitmap tinted = image(grass.Id);
                Assert.IsTrue(!ReferenceEquals(untinted, tinted), "reloaded after color change");
                Assert.IsTrue(Enumerable.Range(0, tinted.Width * tinted.Height).Select(i => tinted.GetPixel(i % tinted.Width, i / tinted.Width)).Where(c => c.A > 0).All(c => c.R == 0 && c.B == 0), "map chips are color adjusted");
                Assert.IsTrue(plain.R > 40, "source had red");
                typeof(MainForm).GetField("spriteAdjustmentStrength", Flags).SetValue(form, 0); Call(form, "UpdateColorAdjustmentButton");
                var fit = Field<Button>(form, "previewFitButton"); var hint = Field<ToolStripStatusLabel>(form, "zoomHintLabel");
                Assert.IsTrue(fit.Visible, "preview header has fit in map mode");
                Assert.AreEqual(Loc.T("hint.mapControls"), hint.Text, "status bar shows map controls");
                var settings = Field<FlowLayoutPanel>(form, "mapSettings"); var texts = new List<string>(); var stack = new Stack<Control>(); stack.Push(settings);
                while (stack.Count > 0) { Control c = stack.Pop(); texts.Add(c.Text); foreach (Control child in c.Controls) stack.Push(child); }
                Assert.IsFalse(texts.Any(x => x.Contains("初期表示") || x.Contains("敷き詰め") || x.Contains("ホイール")), "old view row removed");
                var all = new List<Control>(); stack.Push(settings); while (stack.Count > 0) { Control c = stack.Pop(); all.Add(c); foreach (Control child in c.Controls) stack.Push(child); }
                Assert.IsFalse(all.OfType<RoundedCheckBox>().Any(), "no play checkbox");
                Assert.AreEqual(map.Animations.Count, all.OfType<MapAnimationIcon>().Count(), "animated icon per animation");
                Assert.IsFalse(texts.Any(x => x.Contains("水面")), "no animation names");
                string layout = MapDocument.RowsKey(map.LayoutRows(4));
                Field<NumericUpDown>(form, "columnsBox").Value = 2; Call(form, "UpdatePreviewSafe"); Pump(100);
                Assert.AreEqual(layout, MapDocument.RowsKey(Field<MapDocument>(form, "mapDocument").LayoutRows(2)), "columns do not move map chips");
                Field<NumericUpDown>(form, "columnsBox").Value = 4; Call(form, "UpdatePreviewSafe"); Pump(100);
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Player); Pump(100);
                Assert.IsFalse(fit.Visible, "fit hidden outside map"); Assert.IsTrue(Loc.T("hint.mapControls") != hint.Text, "normal hint restored");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
            // L字に並んだコマも、ひとつながりの枠で囲む（見た目の確認用）。
            var l = new MapDocument();
            for (int i = 1; i <= 6; i++) l.Assets.Add(new MapAsset { Id = "l" + i, Width = 8, Height = 8, Cell = i });
            l.SetBasis(l.Assets[0]); l.Animations.Add(new MapAnimation { Id = "L", Frames = new List<string> { "l2", "l3", "l4", "l5" } });
            using (var host = new Form { ClientSize = new Size(300, 240) })
            using (var c = new MapCanvas { Document = l, Palette = true, PaletteColumns = 3, Dock = DockStyle.Fill })
            { host.Controls.Add(c); host.Show(); c.RefreshPacking(); c.Fit(true); Pump(300); Snap(c, "SheetRainbowL.png"); host.Close(); }
        }
        private static void MapLoopExport()
        {
            // 1ループ = 各アニメーションのループの最小公倍数。切り替わりの時刻だけがコマになる。
            var m = Sample();
            for (int i = 0; i < 7; i++) m.Assets.Add(new MapAsset { Id = "f" + i, Width = 8, Height = 8, Cell = i + 2 });
            m.Animations.Add(new MapAnimation { Id = "a", Fps = 6, Frames = new List<string> { "f0", "f1", "f2", "f3" } });
            m.Animations.Add(new MapAnimation { Id = "b", Fps = 4, Frames = new List<string> { "f4", "f5", "f6" } });
            Func<List<double>> times = () => (List<double>)typeof(MainForm).GetMethod("MapLoopTimes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { m });
            Assert.AreEqual(1, times().Count, "no placed animation: a single still frame");
            m.SetTile(0, 0, "a", true, false); m.SetTile(1, 0, "b", true, false);
            List<double> t = times();
            Assert.IsTrue(Math.Abs(t[t.Count - 1] - 6.0) < 1e-9, "loop is 6 seconds (2/3 s and 3/4 s line up)");
            Assert.AreEqual(48, t.Count - 1, "frames only where something changes");
            m.VisibleLayers[0] = false;
            Assert.AreEqual(1, times().Count, "hidden layers are not exported");
            m.VisibleLayers[0] = true;
            // アプリから書き出すと、マップ全体（20×20マス）の1ループになる。
            string root = Path.Combine(Path.GetTempPath(), "MapLoop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                var map = Field<MapDocument>(form, "mapDocument");
                Type format = typeof(MainForm).GetNestedType("ImageOutputFormat", BindingFlags.NonPublic);
                string gif = Path.Combine(root, "loop.gif"), webp = Path.Combine(root, "loop.webp");
                Call(form, "ExportToFile", Enum.Parse(format, "Gif"), gif);
                Call(form, "ExportToFile", Enum.Parse(format, "WebP"), webp);
                using (var image = Image.FromFile(gif))
                {
                    Assert.AreEqual(new Size(map.CellWidth * 20, map.CellHeight * 20), image.Size, "whole map");
                    int frames = image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
                    Assert.AreEqual(map.Animations[0].Frames.Count, frames, "one loop of the water animation");
                    byte[] delays = image.GetPropertyItem(0x5100).Value;
                    int total = Enumerable.Range(0, frames).Sum(i => BitConverter.ToInt32(delays, i * 4));
                    Assert.AreEqual((int)Math.Round(100.0 * map.Animations[0].Frames.Count / map.Animations[0].Fps), total, "loop length in 1/100 s");
                }
                Assert.IsTrue(new FileInfo(webp).Length > 100, "webp written");
                byte[] head = File.ReadAllBytes(webp).Take(16).ToArray();
                Assert.AreEqual("WEBP", System.Text.Encoding.ASCII.GetString(head, 8, 4), "webp header");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void RevealAndCrossfade()
        {
            string root = Path.Combine(Path.GetTempPath(), "Reveal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja" + Environment.NewLine + "checkUpdates=0" + Environment.NewLine);
            using (var form = new MainForm())
            {
                // 起動時は透明で、手が空いたら全体が一度にフェードインする（必ず最後は不透明になる）。
                if (UiMotion.Enabled) Assert.IsTrue(form.Opacity < 1, "starts hidden while laying out");
                form.Show();
                for (int i = 0; i < 300 && form.Opacity < 1; i++) Pump(10);
                Assert.AreEqual(1.0, form.Opacity, "fully shown after the reveal");
                // モードを切り替えると、中央と右を前の絵で覆ってから溶け込ませ、終わったら覆いを外す。
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Player);
                object cover = typeof(MainForm).GetField("contentTransition", Flags).GetValue(form);
                if (UiMotion.Enabled) Assert.IsTrue(cover != null, "content is covered while switching");
                Pump(800);
                Assert.IsTrue(typeof(MainForm).GetField("contentTransition", Flags).GetValue(form) == null, "cover removed after the crossfade");
                Assert.IsFalse(Field<SplitContainer>(form, "mainSplit").Panel2.Controls.OfType<CrossfadeOverlay>().Any(), "no overlay left behind");
                // 元に戻すは、描き直しを止めて戻し、最後に一度に描き直す（白く光る重ね窓は使わない）。
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Effect); Pump(600);
                int formsBefore = Application.OpenForms.Count;
                Call(form, "UndoLastOperation");
                Assert.AreEqual(formsBefore, Application.OpenForms.Count, "undo opens no overlay window");
                Assert.IsFalse((bool)typeof(MainForm).GetField("workspaceFrozen", Flags).GetValue(form), "drawing is resumed after undo");
                Pump(200);
                Assert.IsFalse(form.Controls.OfType<CrossfadeOverlay>().Any() || Field<SplitContainer>(form, "mainSplit").Parent.Controls.OfType<CrossfadeOverlay>().Any(), "no undo overlay left behind");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void MapScreenLanguages()
        {
            foreach (UiLanguage language in new[] { UiLanguage.English, UiLanguage.ChineseSimplified, UiLanguage.Indonesian })
            {
                string root = Path.Combine(Path.GetTempPath(), "MapLang-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "checkUpdates=0" + Environment.NewLine);
                Loc.SetLanguage(language);
                using (var form = new MainForm())
                {
                    form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                    Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                    Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "StateTransitions")); Pump(400);
                    // マップの設定欄・見出しのチップに、日本語（かな・漢字）が残っていない。
                    var texts = new List<string>(); var stack = new Stack<Control>(); stack.Push(Field<FlowLayoutPanel>(form, "mapSettings"));
                    while (stack.Count > 0) { Control c = stack.Pop(); if (!string.IsNullOrEmpty(c.Text)) texts.Add(c.Text); foreach (Control child in c.Controls) stack.Push(child); }
                    texts.Add(Field<RoundedLabel>(form, "animInfoLabel").Text); texts.Add(Field<RoundedLabel>(form, "sheetCellsChip").Text);
                    Func<string, bool> japanese = x => x.Any(ch => (ch >= '\u3040' && ch <= '\u30ff') || (language != UiLanguage.ChineseSimplified && ch >= '\u4e00' && ch <= '\u9fff'));
                    string leftover = texts.FirstOrDefault(japanese);
                    Assert.IsTrue(leftover == null, language + ": Japanese left in the map screen: " + leftover);
                    // ボタン・ラベルの文字が切れない（幅が文字に足りている）。
                    stack.Push(Field<FlowLayoutPanel>(form, "mapSettings"));
                    while (stack.Count > 0)
                    {
                        Control c = stack.Pop(); foreach (Control child in c.Controls) stack.Push(child);
                        if ((c is Label || c is Button) && !string.IsNullOrEmpty(c.Text) && c.Visible)
                            Assert.IsTrue(TextRenderer.MeasureText(c.Text, c.Font).Width <= c.Width + 2, language + ": text fits: " + c.Text);
                    }
                    string output = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT");
                    if (output != null) using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(output, "MapScreen_" + language + ".png")); }
                    typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
                }
            }
            Loc.SetLanguage(UiLanguage.Japanese);
        }
        private static void ResetClearsMap()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapReset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja" + Environment.NewLine + "checkUpdates=0" + Environment.NewLine);
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(400);
                var map = Field<MapDocument>(form, "mapDocument"); var canvas = Field<MapCanvas>(form, "mapCanvas"); var palette = Field<MapCanvas>(form, "mapPalette");
                // レイヤーの行を右クリックするとメニューが出て、そのレイヤーをクリアできる（元に戻すで戻る）。
                int layer = map.Tiles.First().Layer, before = map.Tiles.Count, onLayer = map.Tiles.Count(t => t.Layer == layer);
                int menuLayer = -1; canvas.LayerMenu += (l, p) => menuLayer = l;
                Point row = new Point(canvas.LayerBounds(layer).X + 20, canvas.LayerBounds(layer).Y + 20);
                Mouse(canvas, "OnMouseDown", MouseButtons.Right, row); Mouse(canvas, "OnMouseUp", MouseButtons.Right, row);
                Assert.AreEqual(layer, menuLayer, "right-click on a layer row opens its menu");
                Assert.IsTrue(canvas.ClearLayer(layer), "layer cleared");
                Assert.AreEqual(before - onLayer, map.Tiles.Count, "only that layer is cleared");
                Call(form, "UndoLastOperation"); Pump(300); map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(before, map.Tiles.Count, "undo restores the layer");
                // マップチップのシートでも、何もない所の右クリックでリセットのメニューが出る。
                bool emptyMenu = false; palette.EmptyMenu += p => emptyMenu = true;
                Mouse(palette, "OnMouseDown", MouseButtons.Right, new Point(2, 2)); Mouse(palette, "OnMouseUp", MouseButtons.Right, new Point(2, 2));
                Assert.IsTrue(emptyMenu, "reset is reachable from the map sheet");
                // リセット（確認ダイアログは除いた中身）でマップも空になる。
                object snapshot = typeof(MainForm).GetField("defaultSnapshot", Flags).GetValue(form);
                Call(form, "RestoreState", snapshot); Call(form, "ResetViewsToFit"); Call(form, "CommitUndoableChange"); Pump(600);
                map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(0, map.Tiles.Count, "reset clears the map");
                Assert.IsTrue(ReferenceEquals(map, canvas.Document) && ReferenceEquals(map, palette.Document), "views show the reset map");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void ShiftAssign()
        {
            string root = Path.Combine(Path.GetTempPath(), "Assign-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja" + Environment.NewLine + "checkUpdates=0" + Environment.NewLine);
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "StateTransitions")); Pump(400);
                var map = Field<MapDocument>(form, "mapDocument"); var palette = Field<MapCanvas>(form, "mapPalette");
                var packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(palette);
                Func<int, Point> at = number => { string id = palette.Numbers.First(p => p.Value == number).Key; var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(palette, new object[] { (RectangleF)packing[id] }); return new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); };
                Keys held = Keys.Shift; palette.ModifierState = () => held;
                // Shift＋左クリックで 2 を選び、Shift＋左クリックで 5 まで広げる（番号の順に 2〜5）。
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(2)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(2));
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(5)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(5));
                List<string> chosen = palette.SelectedInOrder();
                Assert.AreEqual("2,3,4,5", string.Join(",", chosen.Select(id => palette.Numbers[id])), "range as a box (same row here)");
                // 選んだ範囲の上で右クリックするとメニュー（チップの選択や長押しにはならない）。
                Point menuAt = Point.Empty; palette.SelectionMenu += p => menuAt = p;
                string brushBefore = Field<MapCanvas>(form, "mapCanvas").Selected;
                held = Keys.None;
                Mouse(palette, "OnMouseDown", MouseButtons.Right, at(3)); Mouse(palette, "OnMouseUp", MouseButtons.Right, at(3));
                Assert.IsTrue(menuAt != Point.Empty, "right-click on the selection opens the menu");
                Assert.AreEqual(brushBefore, Field<MapCanvas>(form, "mapCanvas").Selected, "brush unchanged by the menu click");
                // 「割り当て」→ 既存のアニメーション（水面）の行を押すと、そのコマが選んだ範囲になる。
                MapAnimation water = map.Animations[0];
                Call(form, "BeginAssignMode"); Pump(100);
                Assert.IsTrue((bool)typeof(MainForm).GetField("assignMode", Flags).GetValue(form), "assign mode on");
                var rows = ((IEnumerable<Control>)typeof(MainForm).GetMethod("MapAssignRows", Flags).Invoke(form, null)).ToList();
                Control waterRow = rows.First(r => r.Tag == water);
                Point screen = waterRow.PointToScreen(new Point(waterRow.Width / 2, waterRow.Height / 2));
                Assert.IsTrue((bool)typeof(MainForm).GetMethod("TryAssignAt", Flags).Invoke(form, new object[] { screen }), "clicked the animation row");
                Assert.AreEqual(string.Join(",", chosen), string.Join(",", map.Animations[0].Frames), "frames set from the range");
                Assert.AreEqual(0, palette.SelectedSet.Count, "selection cleared after assigning");
                // 「新しいアニメーションにする」
                held = Keys.Shift;
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(1)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(1));
                Mouse(palette, "OnMouseDown", MouseButtons.Left, at(3)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(3));
                held = Keys.None;
                int before = map.Animations.Count;
                Call(form, "AssignSelectionToAnimation", new object[] { null });
                Assert.AreEqual(before + 1, map.Animations.Count, "new animation from the range");
                // 何もない所を左クリックすると選択が外れる。
                held = Keys.Shift; Mouse(palette, "OnMouseDown", MouseButtons.Left, at(2)); Mouse(palette, "OnMouseUp", MouseButtons.Left, at(2)); held = Keys.None;
                Mouse(palette, "OnMouseDown", MouseButtons.Left, new Point(3, 3)); Mouse(palette, "OnMouseUp", MouseButtons.Left, new Point(3, 3));
                Assert.AreEqual(0, palette.SelectedSet.Count, "plain click clears the selection");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void Tools()
        {
            var map = Sample();
            map.Assets.Add(new MapAsset { Id = "wide", Width = 16, Height = 8, Cell = 2 });
            map.ActiveLayer = 1; map.SetTile(2, 2, "wide", false, false);
            using (var host = new Form { ClientSize = new Size(560, 520) })
            using (var c = new MapCanvas { Document = map, Dock = DockStyle.Fill, Selected = "base" })
            {
                host.Controls.Add(c); host.Show(); c.Fit(true); Pump(300);
                int commits = 0; c.StrokeFinished += () => commits++;
                Func<int, int, Point> at = (x, y) => c.CellScreen(x, y);
                Action<Point> right = p => { Mouse(c, "OnMouseDown", MouseButtons.Right, p); Mouse(c, "OnMouseUp", MouseButtons.Right, p); };
                Action<int> pick = i => { Point p = new Point(c.ToolButtonBounds(i).X + 20, c.ToolButtonBounds(i).Y + 20); Mouse(c, "OnMouseDown", MouseButtons.Left, p); Mouse(c, "OnMouseUp", MouseButtons.Left, p); };
                right(new Point(c.BrushBounds.X + 40, c.BrushBounds.Y + 40));
                Assert.IsTrue(c.Eraser && c.ActiveTool == MapTool.Erase, "tools start with erase");
                pick(2); Assert.AreEqual(MapTool.RotateClockwise, c.ActiveTool, "toolbar picks rotate");
                right(at(3, 2));   // 横長のチップの2マス目でも当たる
                MapPlacement tile = map.Tiles.Single();
                Assert.AreEqual(1, tile.Rotation, "rotated clockwise");
                Assert.AreEqual(new Size(8, 16), map.FootprintOf(tile), "footprint turns with the chip");
                Assert.IsTrue(map.TileAt(1, 2, 3) == tile && map.TileAt(1, 3, 2) == null, "hit area follows the rotation");
                pick(3); right(at(2, 2)); Assert.AreEqual(0, tile.Rotation, "rotated back counterclockwise");
                pick(3); right(at(2, 2)); Assert.AreEqual(3, tile.Rotation, "counterclockwise wraps to 270");
                pick(2); right(at(2, 2)); Assert.AreEqual(0, tile.Rotation, "clockwise wraps to 0");
                pick(4); right(at(3, 2)); Assert.IsTrue(tile.FlipX, "flipped"); pick(5); right(at(3, 2)); Assert.IsTrue(tile.FlipY, "flipped vertically (Q-013 B)");
                Pump(250); SettledSnapCanvas(c, "MapTools.png");
                pick(1); Mouse(c, "OnMouseDown", MouseButtons.Right, at(3, 2)); Mouse(c, "OnMouseMove", MouseButtons.Right, at(8, 5)); Mouse(c, "OnMouseUp", MouseButtons.Right, at(8, 5));
                Assert.AreEqual(new Point(7, 5), new Point(tile.X, tile.Y), "moved keeping the grabbed cell under the cursor");
                pick(0); right(at(8, 5));
                Assert.AreEqual(0, map.Tiles.Count, "erase hits any covered cell");
                Assert.AreEqual(8, commits, "each tool action is one undo step");
                c.Eraser = false; Pump(250);
                Assert.IsTrue((bool)typeof(MapCanvas).GetField("shownToolMode", Flags).GetValue(c) == false, "toolbar hides when another chip is chosen");
                host.Close();
            }
            var saved = new MapDocument(); saved.Assets.Add(new MapAsset { Id = "a" }); saved.Tiles.Add(new MapPlacement { X = 1, Y = 1, Source = "a", Rotation = 7, FlipX = true, FlipY = true });
            MapPlacement back = MapDocument.FromJson(saved.ToJson()).Tiles.Single();
            Assert.IsTrue(back.Rotation == 3 && back.FlipX && back.FlipY, "rotation and flips are saved (rotation kept in 0-3)");
        }
        private static void SettledSnapCanvas(Control c, string name)
        {
            string root = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT"); if (root == null) return;
            using (var image = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(image, new Rectangle(Point.Empty, c.Size)); image.Save(Path.Combine(root, name)); }
        }
        private static void CoordinatesAndHelp()
        {
            // UV は DirectX（左上原点）と OpenGL（左下原点）で V の向きが逆になる。
            string[] dx = CoordinateCard.Lines(3, "3–4", "1", new Size(8, 8), new Rectangle(16, 0, 16, 8), new Size(56, 24), UvCoordinateFormat.DirectX);
            string[] gl = CoordinateCard.Lines(3, "3–4", "1", new Size(8, 8), new Rectangle(16, 0, 16, 8), new Size(56, 24), UvCoordinateFormat.OpenGL);
            Assert.IsTrue(dx[0].Contains("3–4") && dx[0].Contains("8×8"), "cell line: " + dx[0]);
            Assert.IsTrue(dx[1].Contains("x16") && dx[1].Contains("16×8"), "px line: " + dx[1]);
            Assert.IsTrue(dx[2].Contains("0.286") && dx[2].Contains("0.571") && dx[2].Contains("0.000") && dx[2].Contains("0.333") && dx[2].Contains("DirectX"), "directx uv: " + dx[2]);
            Assert.IsTrue(gl[2].Contains("0.667") && gl[2].Contains("1.000") && gl[2].Contains("OpenGL"), "opengl uv flips V: " + gl[2]);
            string root = Path.Combine(Path.GetTempPath(), "Coord-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(BuildProject(root)); Pump(300);
                var sheet = Field<PreviewCanvas>(form, "sheetCanvas"); var palette = Field<MapCanvas>(form, "mapPalette");
                Assert.IsTrue(Field<RoundedCheckBox>(form, "coordinatesCheckBox").Checked, "coordinates on by default");
                Assert.IsTrue(sheet.ShowCoordinates && palette.ShowCoordinates, "all sheets show coordinates");
                Assert.AreEqual(0, Field<ComboBox>(form, "uvFormatComboBox").SelectedIndex, "DirectX by default");
                Field<ComboBox>(form, "uvFormatComboBox").SelectedIndex = 1; Pump(50);
                Assert.AreEqual(UvCoordinateFormat.OpenGL, palette.UvFormat, "format reaches the map sheet");
                Assert.AreEqual(UvCoordinateFormat.OpenGL, sheet.UvFormat, "format reaches the normal sheet");
                Assert.AreEqual("opengl", AppSettings.Get(CoordinateCard.UvFormatSettingKey), "format saved");
                // マップのシート上にカーソルを置くと札が出る（画像で確認）。
                var packing = (Dictionary<string, Rectangle>)typeof(MapCanvas).GetField("packing", Flags).GetValue(palette);
                var r = (RectangleF)typeof(MapCanvas).GetMethod("Screen", Flags).Invoke(palette, new object[] { (RectangleF)packing.Values.First() });
                Mouse(palette, "OnMouseMove", MouseButtons.None, new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
                Snap(palette, "CoordinateCardMap.png");
                // ヘルプの「？」: 座標表示・UV座標形式・黒透過・書き出しに番号・アニメーションチップ・正規化。
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "Parameters")); Pump(400);   // 設定タブの項目は開いたときに並べる
                var marks = new List<HelpMark>(); var stack = new Stack<Control>(); stack.Push(form);
                while (stack.Count > 0) { Control c = stack.Pop(); if (c is HelpMark) marks.Add((HelpMark)c); foreach (Control child in c.Controls) stack.Push(child); }
                foreach (string key in new[] { "help.coordinates", "help.uvFormat", "help.blackTransparency", "help.exportNumbers", "help.animationChips", "help.normalize" })
                    Assert.IsTrue(marks.Any(m => m.HelpText == Loc.T(key)), "help mark: " + key);
                Assert.IsTrue(((ToolTip)typeof(MainForm).GetField("toolTip", Flags).GetValue(form)).OwnerDraw, "hints use the app style, not the Windows default");
                // 操作は1行ずつの表（操作の札＋結果）、普通の説明は段落。図は決めた名前だけ。
                var blocks = HelpTipStyle.Parse(Loc.T("tooltip.mapCanvas"));
                var ops = blocks.Where(x => x.Kind == HelpTipStyle.BlockKind.Operation).ToList();
                Assert.IsTrue(ops.Count == 5 && ops[0].Operation == "右クリック" && ops[0].Text == "チップを置く", "operation hint is a table");
                Assert.IsTrue(HelpTipStyle.Parse(Loc.T("help.coordinates")).All(x => x.Kind == HelpTipStyle.BlockKind.Paragraph), "plain help stays a sentence");
                Assert.IsTrue(HelpTipStyle.Parse(Loc.T("help.animationChips")).Any(x => x.Kind == HelpTipStyle.BlockKind.Figure && x.Figure == "frames"), "frame order has a figure");
                // 全言語・全ヘルプで、強調の ** が対になり、図の名前が正しく、見出し（# など）がない。
                foreach (LanguageInfo info in Loc.All)
                    foreach (HelpEntry entry in HelpCatalog.All.Where(x => x.Key != null))
                    {
                        string text = Loc.GetTable(info.Language)[entry.Key];
                        foreach (var block in HelpTipStyle.Parse(text))
                        {
                            string body = block.Operation + block.Text;
                            Assert.IsTrue((body.Length - body.Replace("**", "").Length) / 2 % 2 == 0, info.Code + " " + entry.Key + ": ** pairs");
                            if (block.Kind == HelpTipStyle.BlockKind.Figure) Assert.IsTrue(block.Figure == "frames" || block.Figure == "uv", info.Code + " " + entry.Key + ": known figure");
                            Assert.IsFalse(block.Text.StartsWith("#"), info.Code + " " + entry.Key + ": no headings");
                        }
                        Assert.IsTrue(HelpTipStyle.Measure(text).Width <= 400, info.Code + " " + entry.Key + ": fits the width");
                    }
                foreach (string key in new[] { "tooltip.mapCanvas", "tooltip.mapSheet", "help.uvFormat", "help.animationChips", "tooltip.tool.move", "help.normalize" })
                {
                    string text = Loc.T(key); Size size = HelpTipStyle.Measure(text);
                    string root2 = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT");
                    if (root2 != null) using (var image = new Bitmap(size.Width, size.Height)) { using (Graphics g = Graphics.FromImage(image)) HelpTipStyle.Paint(g, new Rectangle(Point.Empty, size), text); image.Save(Path.Combine(root2, "Hint_" + key + ".png")); }
                    string en = Loc.GetTable(UiLanguage.English)[key]; Size enSize = HelpTipStyle.Measure(en);
                    if (root2 != null) using (var image = new Bitmap(enSize.Width, enSize.Height)) { using (Graphics g = Graphics.FromImage(image)) HelpTipStyle.Paint(g, new Rectangle(Point.Empty, enSize), en); image.Save(Path.Combine(root2, "Hint_en_" + key + ".png")); }
                }
                // 通常のシートでも札が出る。
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Standard); Pump(300);
                RectangleF cell = sheet.SheetToScreen(new RectangleF(1, 1, 1, 1));
                typeof(PreviewCanvas).GetMethod("OnMouseMove", Flags).Invoke(sheet, new object[] { new MouseEventArgs(MouseButtons.None, 0, (int)cell.X, (int)cell.Y, 0) });
                Snap(sheet, "CoordinateCardSheet.png");
                Field<ComboBox>(form, "uvFormatComboBox").SelectedIndex = 0;
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void TransitionGroupsFold()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapFold-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); Pump(100);
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Player);
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "StateTransitions")); Pump(400);
                var list = Field<FlowLayoutPanel>(form, "playerTransitionList");
                Func<string, FoldGroupHeader> header = key => list.Controls.OfType<FoldGroupHeader>().First(h => h.Key == key);
                Func<string, List<Control>> rows = key =>
                {
                    var all = list.Controls.Cast<Control>().ToList(); int i = all.IndexOf(header(key)) + 1; var result = new List<Control>();
                    while (i < all.Count && !(all[i] is FoldGroupHeader)) result.Add(all[i++]);
                    return result;
                };
                Assert.AreEqual(4, list.Controls.OfType<FoldGroupHeader>().Count(), "jump and attack groups fold");
                Assert.AreEqual(3, rows("group.jumpRight").Count, "jump rows belong to the header");
                int full = rows("group.jumpRight")[0].Height;
                typeof(FoldGroupHeader).GetMethod("OnClick", Flags).Invoke(header("group.jumpRight"), new object[] { EventArgs.Empty });
                // 動きは時間で進むため、途中の高さではなく「動きが始まったこと」で確かめる（負荷で一気に終わることがある）。
                var motions = (IDictionary)typeof(MainForm).GetField("transitionFoldMotions", Flags).GetValue(form);
                Assert.IsTrue(motions.Contains(header("group.jumpRight")) || !UiMotion.Enabled, "rows fold with motion");
                Pump(400);
                Assert.IsTrue(rows("group.jumpRight").All(r => !r.Visible), "collapsed rows hidden");
                Assert.IsTrue(rows("group.jumpLeft").All(r => r.Visible), "other group unchanged");
                Assert.AreEqual(Loc.T("access.groupCollapsed"), header("group.jumpRight").AccessibleDescription, "state is read out");
                // 見出しの文字を押しても開閉する。
                Label title = header("group.attackLeft").Controls.OfType<Label>().First();
                typeof(Label).GetMethod("OnClick", Flags).Invoke(title, new object[] { EventArgs.Empty }); Pump(300);
                Assert.IsTrue(rows("group.attackLeft").All(r => !r.Visible), "label click folds"); list.AutoScrollPosition = Point.Empty; Pump(50); Snap(list, "TransitionGroupsFolded.png");
                Call(form, "RebuildStateTransitionList"); Pump(50);
                Assert.IsFalse(header("group.jumpRight").Expanded, "kept after rebuilding the list");
                Assert.IsTrue(rows("group.jumpRight").All(r => !r.Visible), "rebuilt rows stay folded");
                typeof(FoldGroupHeader).GetMethod("OnClick", Flags).Invoke(header("group.jumpRight"), new object[] { EventArgs.Empty }); Pump(300);
                Assert.IsTrue(rows("group.jumpRight").All(r => r.Visible && r.Height == full), "expanded to full height");
                Snap(list, "TransitionGroups.png");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        // 長押しの待ちを確かめてから持ち上げる。テスト中に実際のマウスが動くと、その移動で正しく取り消されるため、タイマーは待たずに呼ぶ。
        private static void Lift(MapCanvas c)
        {
            var timer = (System.Windows.Forms.Timer)typeof(MapCanvas).GetField("liftTimer", Flags).GetValue(c);
            Assert.IsTrue(timer.Enabled && timer.Interval >= 300, "hold timer armed");
            typeof(MapCanvas).GetMethod("StartReorder", Flags).Invoke(c, null);
        }
        // 持ち上げ中は、メッセージを処理すると実際のマウスの移動が届いて位置が変わるため、
        // 処理せずに描き直しを重ねて滑りを落ち着かせてから撮る。
        private static void SettledSnap(Control c, string name)
        {
            string root = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT"); if (root == null) return;
            using (var image = new Bitmap(c.Width, c.Height))
            {
                for (int i = 0; i < 20; i++) { Thread.Sleep(20); c.DrawToBitmap(image, new Rectangle(Point.Empty, c.Size)); }
                image.Save(Path.Combine(root, name));
            }
        }
        private static void Snap(Control c, string name)
        {
            string root = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT"); if (root == null) return;
            using (var image = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(image, new Rectangle(Point.Empty, c.Size)); image.Save(Path.Combine(root, name)); }
        }
        private static MapDocument Sample()
        {
            var map = new MapDocument(); map.Assets.Add(new MapAsset { Id = "base", Width = 8, Height = 8, Cell = 1 }); map.SetBasis(map.Assets[0]); return map;
        }
        private static void FoldSections()
        {
            using (var font = new Font("Yu Gothic UI", 10))
            using (var a = new MapFoldSection("アニメーションチップ", true, font, null))
            using (var n = new MapFoldSection("正規化", true, font, null))
            {
                a.Body.Controls.Add(new Button { Height = 30 }); n.Body.Controls.Add(new Button { Height = 30 }); a.RefreshHeight(); n.RefreshHeight();
                a.SetExpanded(false); Pump(220);
                Assert.AreEqual(36, a.Height, "collapsed header only"); Assert.IsFalse(a.Body.Visible, "content hidden");
                Assert.IsTrue(n.Expanded, "other group unchanged");
                a.SetExpanded(true); Pump(220); Assert.IsTrue(a.Height > 36, "expanded height"); Assert.IsTrue(a.Body.Enabled, "content enabled");
                Assert.AreEqual(1, a.Body.Controls.Count, "contents retained");
            }
        }
        private static void Bounds()
        {
            var m = Sample(); m.Assets.Add(new MapAsset { Id = "cloud", Width = 24, Height = 8 });
            Assert.IsFalse(m.SetTile(-1, 0, "base", false, false), "outside rejected");
            Assert.IsFalse(m.SetTile(20, 0, "base", false, false), "outside rejected");
            m.SetTile(1, 1, "cloud", false, false); m.SetTile(2, 1, "base", false, false);
            m.ActiveLayer = 3; m.SetTile(1, 1, "base", false, false); m.SetTile(1, 1, "", false, true);
            Assert.AreEqual(2, m.Tiles.Count, "only active layer erased; neighbor allowed");
            m.SetBasis(new MapAsset { Id = "other", Width = 16, Height = 4 });
            Assert.AreEqual(1, m.Tiles[0].X, "coordinate survives basis change");
        }
        private static void Normalize()
        {
            var m = Sample(); var a = new MapAsset { Width = 15, Height = 7 }; m.Assets.Add(a);
            Assert.IsTrue(m.NeedsNormalization(a), "warning"); m.Normalize(a);
            Assert.AreEqual(new Size(16, 8), a.Size, "nearest ratios independently");
            Assert.AreEqual(15, a.Width, "original retained");
            a.CorrectedWidth = a.CorrectedHeight = 0; a.Ignored = true;
            Assert.IsFalse(m.NeedsNormalization(a), "ignored warning hidden");
            Assert.AreEqual(2, MapDocument.NearestDimension(2, 8), "quarter");
            Assert.AreEqual(64, MapDocument.NearestDimension(80, 8), "cap 8");
        }
        private static void Packing()
        {
            var m = Sample();
            for (int i = 0; i < 45; i++) m.Assets.Add(new MapAsset { Width = 2 + i % 5 * 7, Height = 2 + i % 3 * 8, Cell = i + 2 });
            var p = m.Pack(64).ToList(); Assert.AreEqual(m.Assets.Count, p.Count, "all packed");
            for (int i = 0; i < p.Count; i++) for (int j = i + 1; j < p.Count; j++) Assert.IsFalse(p[i].Value.IntersectsWith(p[j].Value), "no overlap");
            Assert.AreEqual("base", m.BasisId, "basis stable");
        }
        private static void Animation()
        {
            var m = Sample(); m.Animations.Add(new MapAnimation { Id = "a", Frames = new List<string> { "base", "gone" }, Fps = 4 });
            Assert.AreEqual("base", m.FrameId("a", true, 0), "first");
            Assert.AreEqual("gone", m.FrameId("a", true, .25), "second");
            Assert.AreEqual("base", m.FrameId("a", true, .5), "loop");
            Assert.AreEqual("", m.FrameId("missing", true, 1), "missing definition");
        }
        private static void Mouse(MapCanvas c, string method, MouseButtons b, Point p)
        { typeof(MapCanvas).GetMethod(method, Flags).Invoke(c, new object[] { new MouseEventArgs(b, 1, p.X, p.Y, 0) }); }
        private static void Brush()
        {
            using (var c = new MapCanvas { Document = Sample(), Size = new Size(500, 420), Selected = "base" })
            {
                var p = new Point(c.BrushBounds.X + 25, c.BrushBounds.Y + 25);
                Mouse(c, "OnMouseDown", MouseButtons.Right, p); Mouse(c, "OnMouseUp", MouseButtons.Right, p);
                Assert.IsTrue(c.Eraser, "right enters erase"); Assert.AreEqual(0, c.Document.Tiles.Count, "overlay did not paint");
                c.Eraser = false; Mouse(c, "OnMouseDown", MouseButtons.Left, p); Mouse(c, "OnMouseUp", MouseButtons.Left, p);
                Assert.IsFalse(c.Eraser, "left does not switch");
                p = c.CellScreen(1, 1); Mouse(c, "OnMouseDown", MouseButtons.Right, p);
                Mouse(c, "OnMouseMove", MouseButtons.Right, c.CellScreen(6, 1)); Mouse(c, "OnMouseUp", MouseButtons.Right, c.CellScreen(6, 1));
                Assert.AreEqual(6, c.Document.Tiles.Count, "fast stroke has no gaps");
            }
        }
        private static void Pump(int ms)
        { var until = DateTime.UtcNow.AddMilliseconds(ms); do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < until); }
        private static void RoundTrip()
        {
            string root = Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "MapDebug-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); string project = BuildProject(root);
            Loc.SettingsPath = Path.Combine(root, "test-settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                Assert.IsTrue(form.OpenProjectFile(project), "open debug project");
                var map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(8, map.CellWidth, "basis preserved");
                Assert.IsTrue(map.Tiles.Count > 100, "populated project");
                string basis = map.BasisId;
                Field<NumericUpDown>(form, "columnsBox").Value = 4; Call(form, "UpdatePreviewSafe");
                Assert.AreEqual(basis, Field<MapDocument>(form, "mapDocument").BasisId, "columns keep basis");
                Call(form, "CommitUndoNow"); int before = map.Tiles.Count;
                var canvas = Field<MapCanvas>(form, "mapCanvas"); canvas.Size = new Size(500, 420); canvas.Selected = basis;
                Point p = canvas.CellScreen(1, 1); Mouse(canvas, "OnMouseDown", MouseButtons.Right, p); Mouse(canvas, "OnMouseUp", MouseButtons.Right, p);
                Assert.AreEqual(before + 1, map.Tiles.Count, "placed");
                Call(form, "UndoLastOperation"); map = Field<MapDocument>(form, "mapDocument"); Assert.AreEqual(before, map.Tiles.Count, "undo stroke");
                Call(form, "RedoLastOperation"); Assert.AreEqual(before + 1, Field<MapDocument>(form, "mapDocument").Tiles.Count, "redo stroke");
                ProjectDocument doc = (ProjectDocument)Call(form, "CaptureProjectDocument"); string saved = Path.Combine(root, "RoundTrip.smproj"); ProjectFile.Save(saved, doc);
                Assert.IsTrue(form.OpenProjectFile(saved), "reopen"); map = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(basis, map.BasisId, "identity through extraction"); Assert.IsTrue(File.Exists(map.Asset(basis).Path), "rebound path");
                MapAsset irregular = map.Assets.First(a => a.Width == 15);
                map.Normalize(irregular); Call(form, "MapChanged", true);
                string atlas = Path.Combine(root, "PackedTiles.png");
                Call(form, "ExportToFile", Enum.Parse(typeof(MainForm).GetNestedType("ImageOutputFormat", BindingFlags.NonPublic), "Png"), atlas);
                var packed = map.Pack(4);
                using (var image = new Bitmap(atlas))
                { Assert.AreEqual(packed.Values.Max(r => r.Right), image.Width, "packed export width"); Assert.AreEqual(packed.Values.Max(r => r.Bottom), image.Height, "packed export height"); }
                ProjectFile.Save(Path.Combine(root, "Normalized.smproj"), (ProjectDocument)Call(form, "CaptureProjectDocument"));
                Assert.IsTrue(form.OpenProjectFile(Path.Combine(root, "Normalized.smproj")), "normalized reload");
                map = Field<MapDocument>(form, "mapDocument"); Assert.AreEqual(new Size(16, 8), map.Asset(irregular.Id).Size, "correction survives save");
                map.Asset(irregular.Id).CorrectedWidth = map.Asset(irregular.Id).CorrectedHeight = 0;
                Call(form, "MapChanged", true);
                form.Show(); Pump(350);
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "Parameters")); Pump(350);
                foreach (PreviewTargetMode target in new[] { PreviewTargetMode.Map, PreviewTargetMode.Player, PreviewTargetMode.Effect, PreviewTargetMode.Standard, PreviewTargetMode.Map })
                {
                    Call(form, "SetPreviewTargetMode", target); Pump(50);
                    foreach (string name in new[] { "unifiedFpsGroup", "backgroundPaletteGroup", "languageGroup", "colorAdjustmentGroup" })
                        Assert.IsTrue(Field<Control>(form, name).Visible, target + " settings visible: " + name);
                }
                if (Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT") != null)
                { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, form.ClientRectangle); image.Save(Path.Combine(root, "MapSettings.png")); } }
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "StateTransitions")); Pump(350);
                canvas = Field<MapCanvas>(form, "mapCanvas"); canvas.Fit(true); Field<MapCanvas>(form, "mapPalette").Fit(true); Pump(300);
                if (Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT") != null)
                { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, form.ClientRectangle); image.Save(Path.Combine(root, "MapEditor.png")); } }
                var groups = Field<FlowLayoutPanel>(form, "mapSettings").Controls.OfType<MapFoldSection>().ToArray();
                Assert.AreEqual(2, groups.Length, "two accordion sections");
                foreach (var group in groups) group.SetExpanded(false); Pump(220);
                if (Environment.GetEnvironmentVariable("MAP_DEBUG_OUTPUT") != null)
                { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, form.ClientRectangle); image.Save(Path.Combine(root, "MapEditorCollapsed.png")); } }
                Assert.IsTrue(canvas.Visible, "map visible"); Assert.IsFalse(Field<PreviewCanvas>(form, "animCanvas").Visible, "old preview hidden");
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Player); Pump(250);
                Assert.IsFalse(canvas.Visible, "map hidden in character"); Assert.AreEqual(before + 1, map.Tiles.Count, "switch retains map");
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Map); Pump(250);
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
        private static void MissingBasis()
        {
            string root = Path.Combine(Path.GetTempPath(), "MapMissing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.OpenProjectFile(BuildProject(root)); var map = Field<MapDocument>(form, "mapDocument"); string id = map.BasisId;
                var folders = Field<IList>(form, "folders"); var items = (IList)folders[0].GetType().GetField("Items").GetValue(folders[0]); items.RemoveAt(0);
                Call(form, "UpdatePreviewSafe"); Assert.AreEqual(id, map.BasisId, "basis never silently replaced"); Assert.AreEqual(8, map.CellWidth, "width retained"); Assert.IsFalse(map.Asset(id).Available, "missing icon state");
                ProjectDocument captured = (ProjectDocument)Call(form, "CaptureProjectDocument"); string output = Path.Combine(root, "Missing.smproj"); ProjectFile.Save(output, captured);
                form.OpenProjectFile(output); map = Field<MapDocument>(form, "mapDocument"); Assert.AreEqual(id, map.BasisId, "missing identity roundtrip"); Assert.IsFalse(map.Asset(id).Available, "missing remains missing");
            }
        }
        public static string BuildProject(string root)
        {
            string assets = Path.Combine(root, "Tiles"); Directory.CreateDirectory(assets);
            var doc = new ProjectDocument { PreviewMode = "Map", Columns = 8, EndCell = 1 };
            var folder = new ProjectFolder { Name = "MapChipDebug" }; doc.Folders.Add(folder); var map = new MapDocument();
            Action<string, Bitmap> add = (name, bitmap) =>
            {
                string path = Path.Combine(assets, name + ".png"); bitmap.Save(path, ImageFormat.Png);
                var a = new MapAsset { Path = path, Width = bitmap.Width, Height = bitmap.Height, Cell = map.Assets.Count + 1 };
                map.Assets.Add(a); folder.Images.Add(new ProjectImage { Path = path, MapId = a.Id }); bitmap.Dispose();
            };
            // 内側は埋め、外側だけ透過した輪郭を付けて端・角を区別する。
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
            {
                var b = new Bitmap(8, 8);
                for (int py = 0; py < 8; py++) for (int px = 0; px < 8; px++)
                {
                    bool leftEdge = x == 0, rightEdge = x == 2, topEdge = y == 0, bottomEdge = y == 2;
                    int sideInset = 0;
                    bool outside = false;
                    int cornerX = leftEdge ? px : rightEdge ? 7 - px : 8;
                    int cornerY = topEdge ? py : bottomEdge ? 7 - py : 8;
                    // 四隅だけ段階的に透過させ、接続する辺の形は保つ。
                    int cornerInset = cornerY == 0 ? 3 : cornerY == 1 ? 1 : 0;
                    bool roundedCorner = (leftEdge || rightEdge) && (topEdge || bottomEdge) && cornerY <= 2;
                    if (roundedCorner && cornerX < cornerInset) outside = true;
                    Color c = Color.FromArgb(129, 83, 49);
                    if ((px == 3 || px == 4) && (py == 4 || py == 5)) c = py == 4 ? Color.FromArgb(181, 144, 95) : Color.FromArgb(99, 72, 49);
                    if ((px * 3 + py * 5) % 13 == 3) c = Color.FromArgb(155, 100, 56);
                    if ((leftEdge && px == sideInset) || (rightEdge && px == 7 - sideInset)) c = Color.FromArgb(68, 55, 40);
                    if ((leftEdge && px == sideInset + 1) || (rightEdge && px == 6 - sideInset)) c = Color.FromArgb(177, 127, 76);
                    if (bottomEdge && py == 7) c = Color.FromArgb(68, 55, 40);
                    if (bottomEdge && py == 6) c = px % 4 < 2 ? Color.FromArgb(174, 127, 80) : Color.FromArgb(105, 77, 53);
                    if (topEdge && py == 0) c = Color.FromArgb(183, 218, 83);
                    if (topEdge && py == 1) c = Color.FromArgb(103, 168, 47);
                    if (topEdge && py == 2 && px % 3 != 0) c = Color.FromArgb(57, 108, 38);
                    if (roundedCorner && cornerX == cornerInset)
                        c = topEdge ? Color.FromArgb(79, 137, 49) : Color.FromArgb(68, 55, 40);
                    if (outside) c = Color.Transparent;
                    b.SetPixel(px, py, c);
                }
                add("ground_" + y + "_" + x, b);
            }
            var cloud = new Bitmap(24, 8);
            int[] left = { 10, 8, 6, 3, 1, 0, 1, 3 }; int[] right = { 13, 15, 18, 20, 22, 23, 22, 20 };
            for (int y = 0; y < 8; y++) for (int x = left[y]; x <= right[y]; x++) cloud.SetPixel(x, y, y >= 6 ? Color.FromArgb(188, 219, 244) : Color.FromArgb(249, 252, 255));
            add("cloud_3x1", cloud);
            for (int frame = 0; frame < 4; frame++)
            {
                var b = new Bitmap(8, 8); for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) b.SetPixel(x, y, (x + y * 2 + frame * 2) % 8 < 2 ? Color.FromArgb(161, 230, 246) : Color.FromArgb(57, 145, 207)); add("water_" + frame, b);
            }
            using (var b = new Bitmap(15, 7)) { using (var g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(226, 177, 83)); add("irregular_15x7", new Bitmap(b)); }
            using (var b = new Bitmap(4, 4)) { using (var g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(173, 105, 218)); add("quarter_area_4x4", new Bitmap(b)); }
            map.SetBasis(map.Assets[0]);
            var animation = new MapAnimation { Name = "水面", Fps = 6, Frames = map.Assets.Skip(10).Take(4).Select(a => a.Id).ToList() }; map.Animations.Add(animation);
            map.ActiveLayer = 1;
            for (int x = 0; x < 20; x++) for (int y = 13; y < 20; y++) map.SetTile(x, y, map.Assets[y == 13 ? 1 : 4].Id, false, false);
            for (int x = 3; x < 8; x++) { map.SetTile(x, 9, map.Assets[x == 3 ? 0 : x == 7 ? 2 : 1].Id, false, false); map.SetTile(x, 10, map.Assets[x == 3 ? 6 : x == 7 ? 8 : 7].Id, false, false); }
            map.ActiveLayer = 0; map.SetTile(2, 3, map.Assets[9].Id, false, false); map.SetTile(11, 5, map.Assets[9].Id, false, false);
            map.ActiveLayer = 2; for (int x = 11; x < 17; x++) map.SetTile(x, 12, animation.Id, true, false);
            map.ActiveLayer = 3; map.SetTile(7, 13, map.Assets[14].Id, false, false); map.ActiveLayer = 1;
            doc.MapJson = map.ToJson(); string project = Path.Combine(root, "MapChipDebug.smproj"); ProjectFile.Save(project, doc);
            using (var preview = new Bitmap(600, 420))
            using (Graphics g = Graphics.FromImage(preview))
            using (var font = new Font("Yu Gothic UI", 11))
            {
                g.Clear(Color.FromArgb(27, 35, 44));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                string[] labels = { "左上", "上", "右上", "左", "中央", "右", "左下", "下", "右下" };
                for (int i = 0; i < 9; i++)
                {
                    int ox = 22 + i % 3 * 105, oy = 28 + i / 3 * 126;
                    for (int cy = 0; cy < 8; cy++) for (int cx = 0; cx < 8; cx++)
                        using (var brush = new SolidBrush((cx + cy) % 2 == 0 ? Color.FromArgb(56, 66, 78) : Color.FromArgb(43, 52, 64))) g.FillRectangle(brush, ox + cx * 10, oy + cy * 10, 10, 10);
                    using (var tile = new Bitmap(map.Assets[i].Path)) g.DrawImage(tile, new Rectangle(ox, oy, 80, 80), 0, 0, 8, 8, GraphicsUnit.Pixel);
                    g.DrawString(labels[i], font, Brushes.White, ox, oy + 82);
                }
                g.DrawString("接続例", font, Brushes.White, 366, 28);
                for (int ty = 0; ty < 3; ty++) for (int tx = 0; tx < 5; tx++)
                {
                    int index = ty * 3 + (tx == 0 ? 0 : tx == 4 ? 2 : 1);
                    using (var tile = new Bitmap(map.Assets[index].Path)) g.DrawImage(tile, new Rectangle(366 + tx * 40, 68 + ty * 40, 40, 40), 0, 0, 8, 8, GraphicsUnit.Pixel);
                }
                g.DrawString("各タイル 8 × 8 px\n外周は透過\n内側は隙間なく接続", font, Brushes.White, 366, 230);
                preview.Save(Path.Combine(root, "EdgeTiles.png"));
            }
            return project;
        }
    }
}
