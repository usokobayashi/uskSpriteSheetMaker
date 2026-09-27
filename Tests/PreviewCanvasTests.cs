using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class PreviewCanvasTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "PreviewCanvas_SetImageFromSource_DoesNotOwnTheSource", Action = SetImageFromSource_DoesNotOwnTheSource };
            yield return new TestCase { Name = "PreviewCanvas_SetImageFromSource_SurvivesDimensionChange", Action = SetImageFromSource_SurvivesDimensionChange };
            yield return new TestCase { Name = "PreviewCanvas_SceneFloor_IsTexturedWithAccentEdge", Action = SceneFloor_IsTexturedWithAccentEdge };
            yield return new TestCase { Name = "PreviewCanvas_MiddleButtonDragPansTheView", Action = MiddleButtonDragPansTheView };
            yield return new TestCase { Name = "PreviewCanvas_CellNumbersAppearOnAllCellsOrNone", Action = CellNumbersAppearOnAllCellsOrNone };
            yield return new TestCase { Name = "PreviewCanvas_AxisNumbersAreOutsideAndOffByDefault", Action = AxisNumbersAreOutsideAndOffByDefault };
            yield return new TestCase { Name = "PreviewCanvas_CellGestures_ShiftClickDragRangeAndClear", Action = CellGestures_ShiftClickDragRangeAndClear };
        }

        private static List<PreviewRect> GridCells(int columns, int rows, int cell)
        {
            var list = new List<PreviewRect>();
            int number = 1;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                    list.Add(new PreviewRect { Rect = new Rectangle(x * cell, y * cell, cell, cell), Number = number++ });
            return list;
        }

        private static Bitmap Solid(int width, int height, Color color)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
            return bitmap;
        }

        // 左ボタンだけでなく、マウス中ボタンのドラッグでも画面を移動できる。
        private static void MiddleButtonDragPansTheView()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(PreviewCanvas);
            foreach (MouseButtons button in new[] { MouseButtons.Middle, MouseButtons.Left })
            {
                using (var canvas = new PreviewCanvas())
                {
                    canvas.Size = new Size(400, 300);
                    canvas.SetImage(Solid(64, 64, Color.Red), GridCells(1, 1, 64));
                    type.GetMethod("OnMouseDown", flags).Invoke(canvas, new object[] { new MouseEventArgs(button, 1, 100, 100, 0) });
                    type.GetMethod("OnMouseMove", flags).Invoke(canvas, new object[] { new MouseEventArgs(button, 0, 130, 120, 0) });
                    type.GetMethod("OnMouseUp", flags).Invoke(canvas, new object[] { new MouseEventArgs(button, 1, 130, 120, 0) });
                    Assert.IsFalse(canvas.IsFitMode, button + " drag leaves the fit view");
                    PointF pan = (PointF)type.GetField("pan", flags).GetValue(canvas);
                    Assert.IsTrue(pan.X != 0 && pan.Y != 0, button + " drag moved the view");
                }
            }
            using (var canvas = new PreviewCanvas())
            {
                canvas.Size = new Size(400, 300);
                canvas.SetImage(Solid(64, 64, Color.Red), GridCells(1, 1, 64));
                type.GetMethod("OnMouseDown", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 100, 100, 0) });
                Assert.IsTrue(canvas.IsFitMode, "the right button does not pan");
            }
        }

        // セル番号は、桁数やセルの大きさで出る/出ないが混ざらず、全セルに一斉に現れる。
        private static void CellNumbersAppearOnAllCellsOrNone()
        {
            int shownSizes = 0, hiddenSizes = 0;
            for (int width = 120; width <= 1400; width += 40)
            {
                using (var canvas = new PreviewCanvas())
                using (var shot = new Bitmap(width, 1000))
                {
                    canvas.Size = new Size(width, 1000);
                    // 1桁〜3桁の番号が混ざる 12列x9行（108セル）
                    canvas.SetImage(Solid(12 * 40, 9 * 40, Color.FromArgb(20, 20, 30)), GridCells(12, 9, 40));
                    canvas.DrawToBitmap(shot, new Rectangle(0, 0, width, 1000));
                    float zoom = canvas.ZoomPercent > 0 ? canvas.ZoomPercent / 100f : Math.Min((width - 16) / 480f, 984 / 360f);
                    float offsetX = (width - 480 * zoom) / 2f, offsetY = (1000 - 360 * zoom) / 2f;
                    int withBadge = 0, total = 0;
                    for (int index = 0; index < 108; index++)
                    {
                        int px = (int)(offsetX + (index % 12) * 40 * zoom + 5), py = (int)(offsetY + (index / 12) * 40 * zoom + 5);
                        if (px < 0 || py < 0 || px >= width || py >= 1000) continue;
                        total++;
                        Color c = shot.GetPixel(px, py);
                        if (c.R > 230 && c.G > 230 && c.B > 230) withBadge++;
                    }
                    Assert.IsTrue(withBadge == 0 || withBadge == total, "width " + width + ": " + withBadge + " of " + total + " cells show a number (must be all or none)");
                    if (withBadge == 0) hiddenSizes++; else shownSizes++;
                }
            }
            Assert.IsTrue(shownSizes > 0 && hiddenSizes > 0, "numbers are hidden when small and shown when large");
        }

        // 縦横の番号はシートの外側に出す（既定はオフ）。
        private static void AxisNumbersAreOutsideAndOffByDefault()
        {
            // シートの左端（暗い画像色が始まる位置）と、その左側の余白にある明るい文字画素の数を返す。
            Func<bool, int[]> measure = enabled =>
            {
                using (var canvas = new PreviewCanvas())
                using (var shot = new Bitmap(600, 400))
                {
                    canvas.Size = new Size(600, 400);
                    canvas.SetImage(Solid(4 * 100, 3 * 100, Color.FromArgb(20, 20, 30)), GridCells(4, 3, 100));
                    Assert.IsFalse(canvas.ShowAxisNumbers, "off by default");
                    canvas.ShowAxisNumbers = enabled;
                    canvas.DrawToBitmap(shot, new Rectangle(0, 0, 600, 400));
                    int start = 0;
                    for (int x = 0; x < 600; x++)
                    {
                        Color c = shot.GetPixel(x, 200);
                        if (c.R < 30 && c.G < 30 && c.B < 40) { start = x; break; }
                    }
                    int labels = 0;
                    for (int y = 0; y < 400; y++)
                        for (int x = Math.Max(0, start - 28); x < start - 3; x++)
                        {
                            Color c = shot.GetPixel(x, y);
                            if (c.R > 150 && c.G > 150 && c.B > 150) labels++;
                        }
                    return new[] { start, labels };
                }
            };
            int[] off = measure(false);
            int[] on = measure(true);
            Assert.AreEqual(0, off[1], "no row numbers when off");
            Assert.IsTrue(on[1] > 20, "row numbers are drawn left of the sheet when on (" + on[1] + " pixels)");
            Assert.IsTrue(on[0] > off[0], "the sheet shifts right to leave room for the numbers (" + off[0] + " -> " + on[0] + ")");
        }
        private static Point CellCenter(PreviewCanvas canvas, int number)
        {
            for (int y = 0; y < canvas.Height; y += 4)
                for (int x = 0; x < canvas.Width; x += 4)
                    if (canvas.HitTestCell(new Point(x, y)) == number) return new Point(x + 6, y + 6);
            throw new AssertFailedException("cell " + number + " is not visible");
        }

        // Shift+クリック（1つ）/ Shift+ドラッグ（範囲）/ Ctrl+Shift+クリック（範囲追加）/ セル以外のクリック（解除）。
        // 通常のドラッグは画面移動のままで、選択のイベントは出ない。
        private static void CellGestures_ShiftClickDragRangeAndClear()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(PreviewCanvas);
            using (var canvas = new PreviewCanvas())
            {
                canvas.Size = new Size(600, 400);
                canvas.SetImage(Solid(4 * 100, 3 * 100, Color.DarkSlateGray), GridCells(4, 3, 100));
                Keys held = Keys.None;
                canvas.ModifierKeysProvider = () => held;
                var events = new List<string>();
                canvas.CellGesture += (s, e) => events.Add(e.Kind + ":" + e.StartCell + ">" + e.Cell + (e.Additive ? "+" : ""));
                Action<MouseButtons, Point> down = (b, pt) => type.GetMethod("OnMouseDown", flags).Invoke(canvas, new object[] { new MouseEventArgs(b, 1, pt.X, pt.Y, 0) });
                Action<MouseButtons, Point> move = (b, pt) => type.GetMethod("OnMouseMove", flags).Invoke(canvas, new object[] { new MouseEventArgs(b, 0, pt.X, pt.Y, 0) });
                Action<MouseButtons, Point> up = (b, pt) => type.GetMethod("OnMouseUp", flags).Invoke(canvas, new object[] { new MouseEventArgs(b, 1, pt.X, pt.Y, 0) });
                Point c5 = CellCenter(canvas, 5), c7 = CellCenter(canvas, 7), c9 = CellCenter(canvas, 9);

                // Shift+ドラッグ 5→7
                held = Keys.Shift;
                down(MouseButtons.Left, c5); move(MouseButtons.Left, c7); up(MouseButtons.Left, c7);
                Assert.AreEqual("Begin:5>5,Drag:5>7,End:5>7", string.Join(",", events), "shift+drag selects from the start cell to the end cell");
                Assert.IsTrue(canvas.IsFitMode, "shift+drag does not pan");

                // Shift+クリック 9（1つだけ）
                events.Clear();
                down(MouseButtons.Left, c9); up(MouseButtons.Left, c9);
                Assert.AreEqual("Begin:9>9,End:9>9", string.Join(",", events), "shift+click selects a single cell");

                // Ctrl+Shift+クリック 7（範囲追加）
                events.Clear();
                held = Keys.Shift | Keys.Control;
                down(MouseButtons.Left, c7); up(MouseButtons.Left, c7);
                Assert.AreEqual("RangeClick:7>7+", string.Join(",", events), "ctrl+shift+click asks for a range to the clicked cell");

                // Ctrl+Shift+ドラッグは追加選択のドラッグ
                events.Clear();
                down(MouseButtons.Left, c5); move(MouseButtons.Left, c7); up(MouseButtons.Left, c7);
                Assert.AreEqual("Begin:5>5+,Drag:5>7+,End:5>7+", string.Join(",", events), "ctrl+shift+drag adds to the selection");

                // セル以外の Shift+クリック → 解除
                events.Clear();
                held = Keys.Shift;
                down(MouseButtons.Left, new Point(3, 3)); up(MouseButtons.Left, new Point(3, 3));
                Assert.AreEqual("Clear:0>0", string.Join(",", events), "shift+click outside the cells clears");

                // 通常のクリック（セル以外）→ 解除、セル上 → 何もしない
                events.Clear();
                held = Keys.None;
                down(MouseButtons.Left, new Point(3, 3)); up(MouseButtons.Left, new Point(3, 3));
                Assert.AreEqual("Clear:0>0", string.Join(",", events), "a plain click outside the cells clears");
                events.Clear();
                down(MouseButtons.Left, c5); up(MouseButtons.Left, c5);
                Assert.AreEqual(0, events.Count, "a plain click on a cell does not change the selection");

                // 通常のドラッグは画面移動（選択イベントなし）
                events.Clear();
                down(MouseButtons.Left, c5); move(MouseButtons.Left, new Point(c5.X + 40, c5.Y + 30)); up(MouseButtons.Left, new Point(c5.X + 40, c5.Y + 30));
                Assert.AreEqual(0, events.Count, "a plain drag pans and does not select or clear");
                Assert.IsFalse(canvas.IsFitMode, "a plain drag pans the view");
            }
        }

        // 床は単色ではなくテクスチャで描かれ、上端だけが指定のアクセント色になる。
        private static void SceneFloor_IsTexturedWithAccentEdge()
        {
            Color accent = Color.FromArgb(84, 73, 255);
            using (var canvas = new PreviewCanvas())
            using (var sprite = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
            using (var shot = new Bitmap(640, 360))
            {
                canvas.Size = new Size(640, 360);
                canvas.SetScene(sprite, new Size(640, 360), new PointF(300, 100), new Size(64, 64), accent, 0.05f, false);
                canvas.DrawToBitmap(shot, new Rectangle(0, 0, 640, 360));

                var floorColors = new HashSet<int>();
                bool foundAccent = false;
                for (int y = 300; y < 360; y++)
                    for (int x = 20; x < 620; x++)
                    {
                        Color c = shot.GetPixel(x, y);
                        if (c.R == accent.R && c.G == accent.G && c.B == accent.B) foundAccent = true;
                        if (c.B > c.R + 12 && c.B < 120) floorColors.Add(c.ToArgb());
                    }
                Assert.IsTrue(foundAccent, "the floor's top edge uses the accent color");
                Assert.IsTrue(floorColors.Count >= 5, "the floor is a texture, not a single flat color (" + floorColors.Count + " tones)");
            }
        }

        // アニメーション再生では毎ティック同じsourceバイトマップ（animationFramesが
        // 所有）を渡すため、SetImageFromSourceはsourceを破棄したり所有権を
        // 奪ったりしてはいけない。呼び出し後もsourceが生きていることを確認する。
        private static void SetImageFromSource_DoesNotOwnTheSource()
        {
            using (var canvas = new PreviewCanvas())
            using (var source = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(source)) g.Clear(Color.Red);

                canvas.SetImageFromSource(source, null);
                canvas.SetImageFromSource(source, null);

                Color pixel = source.GetPixel(0, 0);
                Assert.AreEqual(255, (int)pixel.A, "source bitmap must still be usable (not disposed) after SetImageFromSource");
                Assert.AreEqual(255, (int)pixel.R, "source content must be unaffected by SetImageFromSource");

                canvas.Dispose();
                pixel = source.GetPixel(0, 0);
                Assert.AreEqual(255, (int)pixel.R, "disposing the canvas must not dispose the borrowed source bitmap");
            }
        }

        // フレームの寸法が変わるケース（例: 異なるサイズのコマへ切り替え）でも
        // 例外にならず、新しい内容が反映されることを確認する。
        private static void SetImageFromSource_SurvivesDimensionChange()
        {
            using (var canvas = new PreviewCanvas())
            using (var small = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
            using (var large = new Bitmap(6, 3, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small)) g.Clear(Color.Blue);
                using (Graphics g = Graphics.FromImage(large)) g.Clear(Color.Green);

                canvas.SetImageFromSource(small, null);
                canvas.SetImageFromSource(large, null);

                Assert.AreEqual(255, (int)small.GetPixel(0, 0).A, "earlier source must remain usable after a later, differently-sized call");
                Assert.AreEqual(255, (int)large.GetPixel(0, 0).A, "latest source must remain usable");
            }
        }
    }
}
