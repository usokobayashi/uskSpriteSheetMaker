//==================================================
// MapLayerDemo
// 個別の8×8素材（MAP_LAYER_DEMO_SOURCE のフォルダ）から、レイヤー機能のデモプロジェクトを作って検証する。
// 素材フォルダの指定がなければ何もしない（素材はリポジトリに含めないため）。
// 出力先は MAP_LAYER_DEMO_OUTPUT（既定は素材フォルダの隣の LayerDemo）。元のプロジェクトは上書きしない。
//==================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class MapLayerDemo
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static T Field<T>(MainForm f, string n) { return (T)typeof(MainForm).GetField(n, Flags).GetValue(f); }
        private static object Call(MainForm f, string n, params object[] a) { return typeof(MainForm).GetMethod(n, Flags).Invoke(f, a); }
        private static void Pump(int ms)
        { var until = DateTime.UtcNow.AddMilliseconds(ms); do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < until); }

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Map_LayerDemoProjectFromSprites", Action = Build };
        }

        private static readonly string[] BrickNames =
        {
            "brick_top_left", "brick_top", "brick_top_right",
            "brick_left", "brick_center", "brick_right",
            "brick_bottom_left", "brick_bottom", "brick_bottom_right"
        };

        private static void Build()
        {
            string source = Environment.GetEnvironmentVariable("MAP_LAYER_DEMO_SOURCE");
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) return;
            string root = Environment.GetEnvironmentVariable("MAP_LAYER_DEMO_OUTPUT") ?? Path.Combine(Path.GetDirectoryName(source.TrimEnd('\\', '/')), "LayerDemo");
            string sprites = Path.Combine(root, "Sprites"); Directory.CreateDirectory(sprites);

            // 素材は個別画像のまま取り込む（合成画像は使わない）。取り込み順＝セル番号。
            string[] names = BrickNames.Concat(new[] { "grass", "ladder", "coin_00", "coin_01", "coin_02", "coin_03", "goal_00", "goal_01", "goal_02", "goal_03" }).ToArray();
            var doc = new ProjectDocument { PreviewMode = "Map", Columns = 8, EndCell = 1 };
            var folder = new ProjectFolder { Name = "LayerDemoSprites" }; doc.Folders.Add(folder);
            var map = new MapDocument();
            var ids = new Dictionary<string, string>();
            foreach (string name in names)
            {
                string from = Path.Combine(source, name + ".png");
                Assert.IsTrue(File.Exists(from), "sprite exists: " + name);
                string path = Path.Combine(sprites, name + ".png"); File.Copy(from, path, true);
                using (var image = new Bitmap(path)) Assert.AreEqual(new Size(8, 8), image.Size, "8x8: " + name);
                var asset = new MapAsset { Path = path, Width = 8, Height = 8, Cell = map.Assets.Count + 1 };
                map.Assets.Add(asset); ids[name] = asset.Id;
                folder.Images.Add(new ProjectImage { Path = path, MapId = asset.Id });
            }
            // 追加の素材（MAP_LAYER_DEMO_EXTRA、; 区切り）。大きさは画像から読む（例: 4×4マスの木箱 32×32）。
            var extras = new List<string>();
            foreach (string from in (Environment.GetEnvironmentVariable("MAP_LAYER_DEMO_EXTRA") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.IsTrue(File.Exists(from), "extra sprite exists: " + from);
                string name = Path.GetFileNameWithoutExtension(from), path = Path.Combine(sprites, name + ".png"); File.Copy(from, path, true);
                Size size; using (var image = new Bitmap(path)) size = image.Size;
                var asset = new MapAsset { Path = path, Width = size.Width, Height = size.Height, Cell = map.Assets.Count + 1 };
                map.Assets.Add(asset); ids[name] = asset.Id; extras.Add(name);
                folder.Images.Add(new ProjectImage { Path = path, MapId = asset.Id });
            }
            map.SetBasis(map.Asset(ids["brick_center"]));
            var coin = new MapAnimation { Fps = 6, Frames = Enumerable.Range(0, 4).Select(i => ids["coin_0" + i]).ToList() };
            var goal = new MapAnimation { Fps = 6, Frames = Enumerable.Range(0, 4).Select(i => ids["goal_0" + i]).ToList() };
            map.Animations.Add(coin); map.Animations.Add(goal);
            // シートはレンガが3×3の形に見える行で並べる（読みやすい並びの例）。
            map.Rows = new List<List<string>>
            {
                new List<string> { ids["brick_top_left"], ids["brick_top"], ids["brick_top_right"], ids["grass"], ids["ladder"] },
                new List<string> { ids["brick_left"], ids["brick_center"], ids["brick_right"] }.Concat(coin.Frames).ToList(),
                new List<string> { ids["brick_bottom_left"], ids["brick_bottom"], ids["brick_bottom_right"] }.Concat(goal.Frames).ToList()
            };
            if (extras.Count > 0) map.Rows.Add(extras.Select(n => ids[n]).ToList());

            // 9方向のレンガで (x0,y0)-(x1,y1) の四角を敷く。
            Action<int, int, int, int> panel = (x0, y0, x1, y1) =>
            {
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int col = x == x0 ? 0 : x == x1 ? 2 : 1, row = y == y0 ? 0 : y == y1 ? 2 : 1;
                        map.SetTile(x, y, ids[BrickNames[row * 3 + col]], false, false);
                    }
            };
            // レイヤー1（最背面）: はしごの隙間から見えるレンガの壁。
            map.ActiveLayer = 0; panel(5, 4, 11, 14);
            // 追加の素材（木箱など）は、床の上の左側に背景として置く（手前の草が重なる）。
            int extraX = 0;
            foreach (string name in extras) { MapAsset a = map.Asset(ids[name]); int h = Math.Max(1, a.Height / 8); map.SetTile(extraX, 15 - h, ids[name], false, false); extraX += Math.Max(1, a.Width / 8); }
            // レイヤー2: 地形（床と浮き足場）。
            map.ActiveLayer = 1; panel(0, 15, 19, 19); panel(0, extras.Count > 0 ? 7 : 10, 3, extras.Count > 0 ? 8 : 11);
            // レイヤー3: はしご（縦につなぐ）と旗。
            map.ActiveLayer = 2;
            for (int y = 6; y <= 14; y++) map.SetTile(8, y, ids["ladder"], false, false);
            map.SetTile(13, 14, goal.Id, true, false);
            // レイヤー4（手前）: 草（旗とはしごの根元に重ねる）とコイン。右上のレイヤー一覧に隠れないよう、見どころは左寄せ。
            map.ActiveLayer = 3;
            foreach (int x in new[] { 1, 3, 8, 11, 14 }) map.SetTile(x, 14, ids["grass"], false, false);
            foreach (int x in new[] { 0, 1, 2, 3 }) map.SetTile(x, extras.Count > 0 ? 5 : 8, coin.Id, true, false);
            foreach (int x in new[] { 8, 9 }) map.SetTile(x, 4, coin.Id, true, false);
            map.ActiveLayer = 1;
            doc.MapJson = map.ToJson();
            string project = Path.Combine(root, "LayerDemo.smproj");
            ProjectFile.Save(project, doc);

            // アプリで開いて検証する。
            Loc.SettingsPath = Path.Combine(root, "test-settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
            using (var form = new MainForm())
            {
                form.Show(); Assert.IsTrue(form.OpenProjectFile(project), "open demo"); Pump(400);
                Type page = typeof(MainForm).GetNestedType("PreviewWorkspacePage", BindingFlags.NonPublic);
                Call(form, "SetPreviewWorkspacePage", Enum.Parse(page, "StateTransitions")); Pump(400);
                MapDocument opened = Field<MapDocument>(form, "mapDocument");
                MapAsset basis = opened.Asset(opened.BasisId);
                Assert.IsTrue(basis != null && Path.GetFileNameWithoutExtension(basis.Path) == "brick_center", "basis is the 8x8 brick");
                Assert.AreEqual(8, opened.CellWidth, "basis width"); Assert.AreEqual(8, opened.CellHeight, "basis height");
                Assert.AreEqual(2, opened.Animations.Count, "coin and flag are separate animations");
                for (int layer = 0; layer < 4; layer++) Assert.IsTrue(opened.Tiles.Any(t => t.Layer == layer), "layer has tiles: " + (layer + 1));
                Assert.AreEqual(0, opened.Assets.Count(opened.NeedsNormalization), "no normalization warnings");

                var canvas = Field<MapCanvas>(form, "mapCanvas"); var palette = Field<MapCanvas>(form, "mapPalette");
                canvas.Fit(true); palette.Fit(true); Pump(400);
                Snap(form, Path.Combine(root, "LayerDemo_Editor.png"));
                Bitmap all = Capture(canvas); all.Save(Path.Combine(root, "LayerDemo_AllLayers.png"));
                // はしごの隙間（透過）から背後のレンガが見える: レイヤー1を隠すと隙間の色が変わる。
                var shots = new List<Bitmap>();
                for (int layer = 0; layer < 4; layer++)
                {
                    opened.VisibleLayers[layer] = false; canvas.Invalidate(); Pump(350);
                    Bitmap hidden = Capture(canvas); hidden.Save(Path.Combine(root, "LayerDemo_Hide" + (layer + 1) + ".png"));
                    Assert.IsTrue(Differs(all, hidden), "hiding layer " + (layer + 1) + " changes the map");
                    shots.Add(hidden);
                    opened.VisibleLayers[layer] = true; canvas.Invalidate(); Pump(350);
                }
                Point gap = LadderGap(opened, canvas);
                Assert.IsTrue(all.GetPixel(gap.X, gap.Y).ToArgb() != shots[0].GetPixel(gap.X, gap.Y).ToArgb(), "back bricks show through the ladder gap");

                // 保存して開き直しても同じ。
                string saved = Path.Combine(root, "LayerDemo_Reloaded.smproj");
                ProjectFile.Save(saved, (ProjectDocument)Call(form, "CaptureProjectDocument"));
                Assert.IsTrue(form.OpenProjectFile(saved), "reopen"); Pump(300);
                MapDocument reloaded = Field<MapDocument>(form, "mapDocument");
                Assert.AreEqual(opened.Tiles.Count, reloaded.Tiles.Count, "tiles survive save");
                Assert.AreEqual(MapDocument.RowsKey(opened.LayoutRows(8)), MapDocument.RowsKey(reloaded.LayoutRows(8)), "sheet rows survive save");
                Assert.AreEqual(string.Join(",", opened.Animations.Select(a => a.Fps + ":" + a.Frames.Count)), string.Join(",", reloaded.Animations.Select(a => a.Fps + ":" + a.Frames.Count)), "animations survive save");
                File.Delete(saved);
                // 配置したマップの1ループを GIF / WebP に書き出す（見本）。
                Type format = typeof(MainForm).GetNestedType("ImageOutputFormat", BindingFlags.NonPublic);
                Call(form, "ExportToFile", Enum.Parse(format, "Gif"), Path.Combine(root, "LayerDemo_Loop.gif"));
                Call(form, "ExportToFile", Enum.Parse(format, "WebP"), Path.Combine(root, "LayerDemo_Loop.webp"));
                using (var gif = Image.FromFile(Path.Combine(root, "LayerDemo_Loop.gif")))
                    Assert.AreEqual(4, gif.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time), "coin and flag (4 frames at 6 FPS) make a 4-frame loop");
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
            File.WriteAllText(Path.Combine(root, "StartDemo.cmd"), "@echo off\r\nstart \"\" \"%~dp0uskSpriteSheetMaker.exe\" \"%~dp0LayerDemo.smproj\"\r\n");
        }

        // はしごの左右の柱の間（透過している画素）の画面上の位置。
        private static Point LadderGap(MapDocument map, MapCanvas canvas)
        {
            MapPlacement ladder = map.Tiles.First(t => t.Layer == 2 && map.Asset(t.Source) != null && Path.GetFileNameWithoutExtension(map.Asset(t.Source).Path) == "ladder" && t.Y == 10);
            using (var image = new Bitmap(map.Asset(ladder.Source).Path))
            {
                Point px = Enumerable.Range(0, 64).Select(i => new Point(i % 8, i / 8)).First(p => p.X >= 2 && p.X <= 5 && image.GetPixel(p.X, p.Y).A == 0);
                Point center = canvas.CellScreen(ladder.X, ladder.Y);
                float cell = (float)typeof(MapCanvas).GetProperty("Zoom").GetValue(canvas) * 8;
                return new Point((int)(center.X - cell / 2 + (px.X + .5f) * cell / 8), (int)(center.Y - cell / 2 + (px.Y + .5f) * cell / 8));
            }
        }
        private static Bitmap Capture(Control c)
        {
            var image = new Bitmap(c.Width, c.Height); c.DrawToBitmap(image, new Rectangle(Point.Empty, c.Size)); return image;
        }
        private static void Snap(Control c, string path) { using (Bitmap image = Capture(c)) image.Save(path); }
        private static bool Differs(Bitmap a, Bitmap b)
        {
            for (int y = 0; y < a.Height; y += 2) for (int x = 0; x < a.Width; x += 2) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return true;
            return false;
        }
    }
}
