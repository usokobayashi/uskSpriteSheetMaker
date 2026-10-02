//==================================================
// DetailFixTests
// 細部不具合レビュー（2026-10-02 Codex）の5件の再現と修正の確認。
//   1. 長く開いたままのプロジェクトの展開先を、別ウィンドウ起動時の掃除で消さない
//   2. 見つからない画像を除いて保存しても、状態の割り当てが同じ画像を指す
//   3. 書き出し中にプロジェクトを切り替えても、書き出し元の画像は書き出しが終わるまで残る
//   4. 大きすぎる設定ファイルでも、設定の変更が効く
//   5. ジャンプ用の補足文は攻撃のグループには出さない
//==================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class DetailFixTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Detail_StaleCleanupKeepsDirectoriesInUse", Action = StaleCleanupKeepsDirectoriesInUse };
            yield return new TestCase { Name = "Detail_SavingWithoutMissingImagesKeepsAssignments", Action = SavingWithoutMissingImagesKeepsAssignments };
            yield return new TestCase { Name = "Detail_SwitchingDuringExportKeepsSourceImages", Action = SwitchingDuringExportKeepsSourceImages };
            yield return new TestCase { Name = "Detail_OversizedSettingsFileIsReplaced", Action = OversizedSettingsFileIsReplaced };
            yield return new TestCase { Name = "Detail_JumpHintOnlyOnJumpGroups", Action = JumpHintOnlyOnJumpGroups };
        }

        private static string NewWorkDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerDetailTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteImage(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            using (var bmp = new Bitmap(2, 2)) bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        //--------------
        // 1
        //--------------
        private static void StaleCleanupKeepsDirectoriesInUse()
        {
            string work = NewWorkDir();
            Loc.SettingsPath = Path.Combine(work, "settings.ini");
            string projects = Path.Combine(work, "projects");
            string inUse = Path.Combine(projects, "inuse");
            string leftover = Path.Combine(projects, "leftover");
            Directory.CreateDirectory(Path.Combine(inUse, "images"));
            Directory.CreateDirectory(Path.Combine(leftover, "images"));
            string image = WriteImage(Path.Combine(inUse, "images"), "frame1.png");   // 使用中のプロジェクトの画像
            using (FileStream held = MainForm.LockProjectDirectory(inUse))
            {
                Assert.IsTrue(held != null, "the in-use marker is created");
                // 異常終了で残った印（誰も開いていない）も作っておく
                File.WriteAllText(Path.Combine(leftover, ".in-use"), "");
                DateTime old = DateTime.UtcNow.AddDays(-3);
                Directory.SetLastWriteTimeUtc(inUse, old);
                Directory.SetLastWriteTimeUtc(leftover, old);

                MainForm.DeleteStaleProjectFolders();
                Assert.IsTrue(Directory.Exists(inUse), "a project open in another window is kept even when old");
                Assert.IsTrue(File.Exists(image), "its images are not deleted (the old cleanup deleted them and only failed on the marker)");
                Assert.IsFalse(Directory.Exists(leftover), "an old folder that nobody uses is cleaned up");
            }
            Assert.IsFalse(File.Exists(Path.Combine(inUse, ".in-use")), "the marker disappears when the project is closed");
        }

        //--------------
        // 2
        //--------------
        private static void SavingWithoutMissingImagesKeepsAssignments()
        {
            string work = NewWorkDir();
            string a = WriteImage(work, "frame1.png"), b = WriteImage(work, "frame2.png"), c = WriteImage(work, "frame3.png");
            var document = new ProjectDocument { Columns = 8, StartCell = 3, EndCell = 3 };
            var folder = new ProjectFolder { Name = "F" };
            foreach (string image in new[] { a, b, c }) folder.Images.Add(new ProjectImage { Path = image });
            document.Folders.Add(folder);
            document.PlayerClips.Add(new ProjectClip { State = "Idle", Enabled = true, StartCell = 3, EndCell = 3, Fps = 12 });
            document.PlayerClips.Add(new ProjectClip { State = "MoveRight", Enabled = true, StartCell = 1, EndCell = 3, Fps = 12 });
            document.EffectClip = new ProjectClip { Enabled = true, StartCell = 2, EndCell = 3, Fps = 12 };
            File.Delete(b);   // frame2 が見つからない

            string path = Path.Combine(work, "p.smproj");
            ProjectSaveResult saved = ProjectFile.Save(path, document);
            Assert.AreEqual(1, saved.MissingImages.Count, "one image is missing");
            ProjectLoadResult loaded = ProjectFile.Load(path, Path.Combine(work, "extract"));
            ProjectDocument r = loaded.Document;
            Assert.AreEqual("frame1.png,frame3.png", string.Join(",", r.Folders[0].Images.Select(i => i.Name)), "frame2 is left out");
            ProjectClip idle = r.PlayerClips.First(x => x.State == "Idle");
            Assert.AreEqual(2, idle.StartCell, "Idle still points at frame3 (now cell 2)");
            Assert.AreEqual(2, idle.EndCell, "Idle still points at frame3 (now cell 2)");
            ProjectClip move = r.PlayerClips.First(x => x.State == "MoveRight");
            Assert.AreEqual(1, move.StartCell, "a range keeps its first remaining image");
            Assert.AreEqual(2, move.EndCell, "a range keeps its last remaining image");
            Assert.AreEqual(2, r.EffectClip.StartCell, "effect range: frame2 is gone, frame3 remains");
            Assert.AreEqual(2, r.EffectClip.EndCell, "effect range ends at frame3");
            Assert.AreEqual(2, r.StartCell, "the standard range moves too");
        }

        //--------------
        // 3
        //--------------
        private static void SwitchingDuringExportKeepsSourceImages()
        {
            string work = NewWorkDir();
            Loc.SettingsPath = Path.Combine(work, "settings.ini");
            UpdateChecker.IsEnabled = false;
            var document = new ProjectDocument { Columns = 8 };
            var folder = new ProjectFolder { Name = "F" };
            folder.Images.Add(new ProjectImage { Path = WriteImage(work, "frame1.png") });
            document.Folders.Add(folder);
            string path = Path.Combine(work, "p.smproj");
            ProjectFile.Save(path, document);

            using (var form = new MainForm { PromptOnUnsavedChanges = false })
            {
                Assert.IsTrue(form.OpenProjectFile(path), "opened");
                string extract = (string)typeof(MainForm).GetField("projectExtractDir", Flags).GetValue(form);
                Assert.IsTrue(MainForm.IsDirectoryInUse(extract), "the opened project's folder is marked as in use");

                typeof(MainForm).GetField("exportBusy", Flags).SetValue(form, true);   // 書き出しの途中
                form.ResetToNewProject();
                Assert.IsTrue(Directory.Exists(extract), "the images stay while the export may still read them");

                typeof(MainForm).GetField("exportBusy", Flags).SetValue(form, false);
                typeof(MainForm).GetMethod("FinishExport", Flags).Invoke(form, new object[] { "PNG", Path.Combine(work, "out.png"), true, null });
                Assert.IsFalse(Directory.Exists(extract), "removed once the export has finished");
            }
        }

        //--------------
        // 4
        //--------------
        private static void OversizedSettingsFileIsReplaced()
        {
            string work = NewWorkDir();
            Loc.SettingsPath = Path.Combine(work, "settings.ini");
            File.WriteAllText(Loc.SettingsPath, "junk=" + new string('x', 1100 * 1024) + "\n");
            AppSettings.Set("checkUpdates", "0");
            Assert.AreEqual("0", AppSettings.Get("checkUpdates"), "the new setting takes effect");
            Assert.IsFalse(UpdateChecker.IsEnabled, "turning updates off works");
            Assert.IsTrue(File.Exists(Loc.SettingsPath + ".toolarge"), "the oversized file is kept aside");
            Assert.IsTrue(new FileInfo(Loc.SettingsPath).Length < 1024, "the settings file is small again");
        }

        //--------------
        // 5
        //--------------
        private static void JumpHintOnlyOnJumpGroups()
        {
            Loc.SettingsPath = Path.Combine(NewWorkDir(), "settings.ini");
            UpdateChecker.IsEnabled = false;
            using (var form = new MainForm())
            {
                var list = (Control)typeof(MainForm).GetField("playerTransitionList", Flags).GetValue(form);
                string hint = Loc.T("hint.jumpFallback");
                int count = 0;
                var stack = new Stack<Control>();
                stack.Push(list);
                while (stack.Count > 0)
                {
                    Control c = stack.Pop();
                    if (c is Label && c.Text == hint) count++;
                    foreach (Control child in c.Controls) stack.Push(child);
                }
                Assert.AreEqual(2, count, "only the two jump groups show the jump fallback hint");
            }
        }
    }
}
