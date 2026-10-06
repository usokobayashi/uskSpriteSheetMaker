//==================================================
// PerfProbe
// 調査用（PERF_PROBE=1 のときだけ）: 実データに近いプロジェクトで、元に戻すの各処理にかかる時間を測って出す。
//==================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class PerfProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static object Call(MainForm f, string n, params object[] a) { return typeof(MainForm).GetMethod(n, Flags).Invoke(f, a); }
        private static T Field<T>(MainForm f, string n) { return (T)typeof(MainForm).GetField(n, Flags).GetValue(f); }
        private static void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < until); }

        public static IEnumerable<TestCase> All() { yield return new TestCase { Name = "Perf_UndoBreakdown", Action = Run }; }

        private static double Time(Action a) { var w = Stopwatch.StartNew(); a(); return w.Elapsed.TotalMilliseconds; }

        private static void Run()
        {
            string project = Environment.GetEnvironmentVariable("PERF_PROBE_PROJECT");
            if (Environment.GetEnvironmentVariable("PERF_PROBE") != "1" || project == null || !File.Exists(project)) return;
            string root = Path.Combine(Path.GetTempPath(), "Perf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Loc.SettingsPath = Path.Combine(root, "settings.ini"); File.WriteAllText(Loc.SettingsPath, "language=ja" + Environment.NewLine + "checkUpdates=0" + Environment.NewLine);
            using (var form = new MainForm())
            {
                form.Show(); form.OpenProjectFile(project); Pump(1500);
                var columns = Field<NumericUpDown>(form, "columnsBox");
                for (int round = 0; round < 3; round++)
                {
                    columns.Value = columns.Value == 8 ? 7 : 8; Pump(800);
                    Call(form, "CommitUndoNow");
                    object snapshot = null;
                    double capture = Time(() => snapshot = Call(form, "CaptureState"));
                    double cover = 0;
                    double tree = Time(() => Call(form, "UpdateTree"));
                    double undoTotal = Time(() => Call(form, "UndoLastOperation"));
                    var wait = Stopwatch.StartNew(); Pump(1500);
                    double idlePaint = Time(() => form.Refresh());
                    double treeDraw = Time(() => { using (var b = new Bitmap(form.Width, form.Height)) Field<FolderTreeView>(form, "treeView").DrawToBitmap(b, new Rectangle(0, 0, 300, 600)); });
                    Console.WriteLine(string.Format("  PERF round{0}: CaptureState {1:0}ms, CoverWorkspace(撮影) {2:0}ms, UpdateTree {3:0}ms, Undo全体(同期部分) {4:0}ms, 全体の再描画 {5:0}ms, ツリー描画 {6:0}ms",
                        round, capture, cover, tree, undoTotal, idlePaint, treeDraw));
                }
                typeof(MainForm).GetField("projectBaseline", Flags).SetValue(form, Call(form, "CaptureState")); form.Close();
            }
        }
    }
}
