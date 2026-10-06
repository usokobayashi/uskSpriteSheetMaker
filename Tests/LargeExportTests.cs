//==================================================
// LargeExportTests
// 大きなマップチップ（基準セルが大きい）を GIF / WebP / PNG に書き出しても落ちないことを確かめる。
// 時間とメモリを使うため、LARGE_EXPORT_TEST=1 のときだけ動かす。結果（時間・最大メモリ）を出力する。
//==================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal static class LargeExportTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static object Call(MainForm f, string n, params object[] a) { return typeof(MainForm).GetMethod(n, Flags).Invoke(f, a); }

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Export_LargeMapDoesNotCrash", Action = LargeMap };
        }

        private static void LargeMap()
        {
            if (Environment.GetEnvironmentVariable("LARGE_EXPORT_TEST") != "1") return;
            foreach (int basis in new[] { 128, 256, 512 })
            {
                string root = Path.Combine(Path.GetTempPath(), "LargeExport-" + basis + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    string project = BuildProject(root, basis);
                    Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja\ncheckUpdates=0\n");
                    using (var form = new MainForm())
                    {
                        Assert.IsTrue(form.OpenProjectFile(project), "open");
                        Type format = typeof(MainForm).GetNestedType("ImageOutputFormat", BindingFlags.NonPublic);
                        foreach (string kind in new[] { "Png", "Gif", "WebP" })
                        {
                            string path = Path.Combine(root, "large." + kind.ToLowerInvariant());
                            var clock = Stopwatch.StartNew();
                            Call(form, "ExportToFile", Enum.Parse(format, kind), path);
                            clock.Stop();
                            Process.GetCurrentProcess().Refresh();
                            Console.WriteLine("  LARGE basis={0} {1}: {2:0.0}s, {3:0.0}MB, peak {4:0}MB", basis, kind, clock.Elapsed.TotalSeconds,
                                new FileInfo(path).Length / 1048576.0, Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0);
                            Assert.IsTrue(new FileInfo(path).Length > 0, kind + " written");
                            File.Delete(path);
                        }
                        typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
                    }
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                GC.Collect();
            }
        }

        // 基準セル basis px の地形と、4コマ・3コマの2つのアニメーション（1ループ 2秒）を並べたマップ。
        private static string BuildProject(string root, int basis)
        {
            string assets = Path.Combine(root, "Tiles"); Directory.CreateDirectory(assets);
            var doc = new ProjectDocument { PreviewMode = "Map", Columns = 8, EndCell = 1 };
            var folder = new ProjectFolder { Name = "Large" }; doc.Folders.Add(folder); var map = new MapDocument();
            var rng = new Random(basis);
            Func<string, Color, string> add = (name, tint) =>
            {
                string path = Path.Combine(assets, name + ".png");
                using (var b = new Bitmap(basis, basis, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.Clear(tint);
                        for (int i = 0; i < 40; i++)
                            using (var brush = new SolidBrush(Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256))))
                                g.FillRectangle(brush, rng.Next(basis), rng.Next(basis), basis / 6, basis / 6);
                    }
                    b.Save(path, ImageFormat.Png);
                }
                var a = new MapAsset { Path = path, Width = basis, Height = basis, Cell = map.Assets.Count + 1 };
                map.Assets.Add(a); folder.Images.Add(new ProjectImage { Path = path, MapId = a.Id }); return a.Id;
            };
            string ground = add("ground", Color.SaddleBrown);
            var run = new MapAnimation { Fps = 2, Frames = Enumerable.Range(0, 4).Select(i => add("run" + i, Color.FromArgb(40 + i * 40, 80, 160))).ToList() };
            var blink = new MapAnimation { Fps = 3, Frames = Enumerable.Range(0, 3).Select(i => add("blink" + i, Color.FromArgb(200, 60 + i * 50, 60))).ToList() };
            map.Animations.Add(run); map.Animations.Add(blink); map.SetBasis(map.Asset(ground));
            map.ActiveLayer = 0;
            for (int x = 0; x < 20; x++) for (int y = 15; y < 20; y++) map.SetTile(x, y, ground, false, false);
            map.ActiveLayer = 1;
            for (int x = 0; x < 20; x += 2) { map.SetTile(x, 10, run.Id, true, false); map.SetTile(x + 1, 6, blink.Id, true, false); }
            doc.MapJson = map.ToJson(); string project = Path.Combine(root, "Large.smproj"); ProjectFile.Save(project, doc);
            return project;
        }
    }
}
