using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class ProjectFileTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "ProjectFile_RoundTrip_KeepsSettingsFoldersAndImageBytes", Action = RoundTrip_KeepsSettingsFoldersAndImageBytes };
            yield return new TestCase { Name = "ProjectFile_Save_SkipsMissingImagesAndReportsThem", Action = Save_SkipsMissingImagesAndReportsThem };
            yield return new TestCase { Name = "ProjectFile_Save_ReplacesExistingFileWithoutLeavingTemp", Action = Save_ReplacesExistingFileWithoutLeavingTemp };
            yield return new TestCase { Name = "ProjectFile_Load_RejectsBrokenFiles", Action = Load_RejectsBrokenFiles };
            yield return new TestCase { Name = "ProjectFile_Load_FlagsNewerFormatButStillLoads", Action = Load_FlagsNewerFormatButStillLoads };
            yield return new TestCase { Name = "ProjectFile_Load_ReportsImagesMissingFromArchive", Action = Load_ReportsImagesMissingFromArchive };
            yield return new TestCase { Name = "AppSettings_Set_KeepsOtherKeysAndSupportsLists", Action = AppSettings_Set_KeepsOtherKeysAndSupportsLists };
        }

        private static string NewWorkDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteImage(string dir, string name, byte fill, int length)
        {
            string path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(fill + i);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static ProjectDocument SampleDocument(params string[][] folderImagePaths)
        {
            var document = new ProjectDocument
            {
                NextFolderNumber = 4, Columns = 24, ScaleIndex = 2, Fps = 30, StartCell = 3, EndCell = 171,
                ExportNumbers = true, PreviewMode = "Player", PlayerMoveSpeed = 1.5m, PlayerJump = 3.2m,
                PlayerGravity = 2.1m, PlayerGroundOffset = -12, MirrorMissingDirections = true,
                PlayerColliderWidth = 24, PlayerColliderHeight = 40, ShowCollider = true,
                BackgroundPalette = 1, BlackTransparency = true, ColorBlendMode = "Multiply",
                AdjustmentColorArgb = unchecked((int)0xFF3366CC), AdjustmentStrength = 65,
                EffectDirectionX = -1, EffectDirectionY = 0.5m, EffectSpeed = 4.5m,
                SimulationRangesInitialized = true, SheetPaneRatio = 0.6, ShowAxisNumbers = true
            };
            document.EffectClip = new ProjectClip { Enabled = true, StartCell = 5, EndCell = 9, Fps = 24 };
            document.PlayerClips.Add(new ProjectClip { State = "Idle", Enabled = true, StartCell = 1, EndCell = 48, Fps = 12 });
            document.PlayerClips.Add(new ProjectClip { State = "AttackRight1", Enabled = false, StartCell = 50, EndCell = 56, Fps = 20 });
            document.Keys.Add(new ProjectKey { Action = "Attack1", Key = "G" });
            document.Keys.Add(new ProjectKey { Action = "Attack2", Key = "R" });
            int index = 0;
            foreach (string[] paths in folderImagePaths)
            {
                var folder = new ProjectFolder { Name = "フォルダ" + (++index) };
                foreach (string path in paths) folder.Images.Add(new ProjectImage { Path = path });
                document.Folders.Add(folder);
            }
            return document;
        }

        private static void RoundTrip_KeepsSettingsFoldersAndImageBytes()
        {
            string work = NewWorkDir();
            string a = WriteImage(Path.Combine(work, "src1"), "スライム_001.png", 1, 300);
            string b = WriteImage(Path.Combine(work, "src1"), "slime_002.png", 50, 5000);
            // 別の場所にある同名ファイル（同じフォルダに入れても衝突しない）
            string c = WriteImage(Path.Combine(work, "src2"), "slime_002.png", 99, 700);
            string d = WriteImage(Path.Combine(work, "src2"), "other.png", 7, 40);
            ProjectDocument document = SampleDocument(new[] { a, b, c }, new[] { d });

            string file = Path.Combine(work, "テスト.smproj");
            ProjectSaveResult saved = ProjectFile.Save(file, document);
            Assert.AreEqual(4, saved.SavedImages, "all images are saved");
            Assert.AreEqual(0, saved.MissingImages.Count, "nothing is missing");

            ProjectLoadResult loaded = ProjectFile.Load(file, Path.Combine(work, "extract"));
            ProjectDocument r = loaded.Document;
            Assert.IsFalse(loaded.NewerFormat, "current format");
            Assert.AreEqual(24, r.Columns, "columns"); Assert.AreEqual(2, r.ScaleIndex, "scale");
            Assert.AreEqual(30, r.Fps, "fps"); Assert.AreEqual(3, r.StartCell, "start"); Assert.AreEqual(171, r.EndCell, "end");
            Assert.AreEqual(4, r.NextFolderNumber, "next folder number"); Assert.IsTrue(r.ExportNumbers, "export numbers");
            Assert.AreEqual("Player", r.PreviewMode, "preview mode");
            Assert.AreEqual(1.5m, r.PlayerMoveSpeed, "move speed"); Assert.AreEqual(3.2m, r.PlayerJump, "jump");
            Assert.AreEqual(2.1m, r.PlayerGravity, "gravity"); Assert.AreEqual(-12m, r.PlayerGroundOffset, "ground offset");
            Assert.AreEqual(24m, r.PlayerColliderWidth, "collider width"); Assert.AreEqual(40m, r.PlayerColliderHeight, "collider height");
            Assert.IsTrue(r.ShowCollider, "collider shown");
            Assert.IsTrue(r.MirrorMissingDirections, "mirror"); Assert.AreEqual(1, r.BackgroundPalette, "background");
            Assert.IsTrue(r.BlackTransparency, "black transparency"); Assert.AreEqual("Multiply", r.ColorBlendMode, "blend");
            Assert.AreEqual(unchecked((int)0xFF3366CC), r.AdjustmentColorArgb, "adjust color"); Assert.AreEqual(65, r.AdjustmentStrength, "adjust strength");
            Assert.AreEqual(-1m, r.EffectDirectionX, "effect x"); Assert.AreEqual(0.5m, r.EffectDirectionY, "effect y");
            Assert.AreEqual(4.5m, r.EffectSpeed, "effect speed"); Assert.IsTrue(r.SimulationRangesInitialized, "ranges initialized");
            Assert.AreEqual(0.6, r.SheetPaneRatio, "sheet pane ratio");
            Assert.IsTrue(r.ShowAxisNumbers, "axis numbers option");
            Assert.IsTrue(r.EffectClip.Enabled && r.EffectClip.StartCell == 5 && r.EffectClip.EndCell == 9 && r.EffectClip.Fps == 24, "effect clip");
            Assert.AreEqual(2, r.PlayerClips.Count, "clips");
            Assert.AreEqual("AttackRight1", r.PlayerClips[1].State, "clip state");
            Assert.AreEqual("R", r.Keys.First(k => k.Action == "Attack2").Key, "key binding");

            Assert.AreEqual(2, r.Folders.Count, "folders");
            Assert.AreEqual("フォルダ1", r.Folders[0].Name, "folder name");
            Assert.AreEqual(3, r.Folders[0].Images.Count, "images in folder 1");
            string[] originals = { a, b, c, d };
            var loadedImages = r.Folders.SelectMany(f => f.Images).ToList();
            for (int i = 0; i < originals.Length; i++)
            {
                Assert.IsTrue(File.Exists(loadedImages[i].Path), "extracted file exists " + loadedImages[i].Name);
                Assert.IsTrue(File.ReadAllBytes(originals[i]).SequenceEqual(File.ReadAllBytes(loadedImages[i].Path)), "image bytes are identical: " + loadedImages[i].Name);
            }
            Assert.AreEqual("スライム_001.png", r.Folders[0].Images[0].Name, "Japanese file name is kept");
            Assert.AreEqual("slime_002.png", r.Folders[0].Images[1].Name, "first duplicate keeps its name");
            Assert.AreEqual("slime_002_2.png", r.Folders[0].Images[2].Name, "second duplicate is renamed, not overwritten");
        }

        private static void Save_SkipsMissingImagesAndReportsThem()
        {
            string work = NewWorkDir();
            string a = WriteImage(work, "a.png", 1, 100);
            string missing = Path.Combine(work, "gone.png");
            string file = Path.Combine(work, "p.smproj");
            ProjectSaveResult saved = ProjectFile.Save(file, SampleDocument(new[] { a, missing }));
            Assert.AreEqual(1, saved.SavedImages, "only the existing image is saved");
            Assert.AreEqual(1, saved.MissingImages.Count, "missing image is reported");
            Assert.AreEqual(missing, saved.MissingImages[0], "missing image path");
            ProjectLoadResult loaded = ProjectFile.Load(file, Path.Combine(work, "x"));
            Assert.AreEqual(1, loaded.Document.Folders[0].Images.Count, "the project only lists saved images");
        }

        private static void Save_ReplacesExistingFileWithoutLeavingTemp()
        {
            string work = NewWorkDir();
            string a = WriteImage(work, "a.png", 1, 100);
            string file = Path.Combine(work, "p.smproj");
            ProjectFile.Save(file, SampleDocument(new[] { a }));
            ProjectDocument second = SampleDocument(new[] { a, a });
            second.Fps = 44;
            ProjectFile.Save(file, second);
            Assert.IsFalse(File.Exists(file + ".tmp"), "no temporary file is left behind");
            Assert.AreEqual(44, ProjectFile.Load(file, Path.Combine(work, "x")).Document.Fps, "the file holds the latest save");
        }

        private static void Load_RejectsBrokenFiles()
        {
            string work = NewWorkDir();
            string notZip = Path.Combine(work, "notzip.smproj");
            File.WriteAllText(notZip, "this is not a zip");
            AssertThrowsFormat(() => ProjectFile.Load(notZip, Path.Combine(work, "x")), "not a zip");

            string noDocument = Path.Combine(work, "nodoc.smproj");
            using (var zip = ZipFile.Open(noDocument, ZipArchiveMode.Create))
                zip.CreateEntry("readme.txt");
            AssertThrowsFormat(() => ProjectFile.Load(noDocument, Path.Combine(work, "x")), "no project.json");

            string badJson = Path.Combine(work, "badjson.smproj");
            using (var zip = ZipFile.Open(badJson, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open(), Encoding.UTF8))
                writer.Write("{ this is not json");
            AssertThrowsFormat(() => ProjectFile.Load(badJson, Path.Combine(work, "x")), "broken json");
        }

        private static void AssertThrowsFormat(Action action, string what)
        {
            try { action(); }
            catch (ProjectFormatException) { return; }
            throw new AssertFailedException(what + ": ProjectFormatException expected");
        }

        private static void Load_FlagsNewerFormatButStillLoads()
        {
            string work = NewWorkDir();
            string file = Path.Combine(work, "future.smproj");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open(), new UTF8Encoding(false)))
                writer.Write("{\"formatVersion\": 99, \"fps\": 17, \"unknownFutureField\": [1,2,3], \"folders\": []}");
            ProjectLoadResult loaded = ProjectFile.Load(file, Path.Combine(work, "x"));
            Assert.IsTrue(loaded.NewerFormat, "newer format is flagged");
            Assert.AreEqual(17, loaded.Document.Fps, "known fields still load");
            Assert.AreEqual(0, loaded.Document.PlayerClips.Count, "missing lists are normalized to empty");
        }

        // 言語と最近使ったファイルは同じ settings.ini に入る。一方を書いても他方を消さない。
        private static void AppSettings_Set_KeepsOtherKeysAndSupportsLists()
        {
            Loc.ResetForTests();
            Loc.SettingsPath = Path.Combine(NewWorkDir(), "settings.ini");
            Loc.Initialize();
            Loc.SetLanguage(UiLanguage.English);
            AppSettings.SetList("recent", new List<string> { "a.smproj", "b.smproj", "c.smproj" }, 5);
            Assert.AreEqual("en", AppSettings.Get("language"), "language is kept after writing the list");
            Assert.AreEqual(3, AppSettings.GetList("recent", 5).Count, "list is stored");
            AppSettings.SetList("recent", new List<string> { "z.smproj" }, 5);
            Assert.AreEqual(1, AppSettings.GetList("recent", 5).Count, "shorter list drops the old entries");
            Loc.SetLanguage(UiLanguage.Japanese);
            Assert.AreEqual("z.smproj", AppSettings.GetList("recent", 5)[0], "list is kept after changing the language");
            Loc.ResetForTests();
        }

        private static void Load_ReportsImagesMissingFromArchive()
        {
            string work = NewWorkDir();
            string file = Path.Combine(work, "partial.smproj");
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open(), new UTF8Encoding(false)))
                    writer.Write("{\"formatVersion\": 1, \"folders\": [{\"name\": \"f\", \"images\": [{\"name\": \"here.png\"}, {\"name\": \"lost.png\"}, {\"name\": \"..\"}]}]}");
                using (var stream = zip.CreateEntry("images/000/here.png").Open()) stream.WriteByte(1);
            }
            ProjectLoadResult loaded = ProjectFile.Load(file, Path.Combine(work, "x"));
            Assert.AreEqual(1, loaded.Document.Folders[0].Images.Count, "only images present in the archive remain");
            Assert.AreEqual(2, loaded.MissingImages.Count, "missing and unsafe entries are reported");
        }
    }
}
