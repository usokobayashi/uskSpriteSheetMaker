using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    // 壊れた・悪意のあるファイル（.smproj・画像）を読み込んでも、範囲外へ書き込まない・メモリやディスクを使い切らない・
    // 想定外の例外で落ちないことの検証。
    internal static class SecurityTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Security_Project_TraversalNamesStayInsideExtractDir", Action = Project_TraversalNamesStayInsideExtractDir };
            yield return new TestCase { Name = "Security_Project_ReservedDeviceNamesAreRenamed", Action = Project_ReservedDeviceNamesAreRenamed };
            yield return new TestCase { Name = "Security_Project_SafeFileNameLimitsLengthAndTrailingDots", Action = Project_SafeFileNameLimitsLengthAndTrailingDots };
            yield return new TestCase { Name = "Project_LegacyJumpPowerIsConvertedToTheNewScale", Action = Project_LegacyJumpPowerIsConvertedToTheNewScale };
            yield return new TestCase { Name = "Security_Project_OversizedDocumentIsRejected", Action = Project_OversizedDocumentIsRejected };
            yield return new TestCase { Name = "Security_Project_TooManyFoldersIsRejected", Action = Project_TooManyFoldersIsRejected };
            yield return new TestCase { Name = "Security_Project_JunkOnlyFailsAsFormatException", Action = Project_JunkOnlyFailsAsFormatException };
            yield return new TestCase { Name = "Security_Project_CopyBoundedStopsAtTheLimit", Action = Project_CopyBoundedStopsAtTheLimit };
            yield return new TestCase { Name = "Security_Image_DeclaredHugeSizeIsRefusedBeforeDecoding", Action = Image_DeclaredHugeSizeIsRefusedBeforeDecoding };
            yield return new TestCase { Name = "Security_Map_HugeDeclaredChipSizesDoNotOverflow", Action = Map_HugeDeclaredChipSizesDoNotOverflow };
        }

        // マップの JSON に、最大の幅・高さのチップを大量に書いたファイル。並べる計算が桁あふれで落ちたり、
        // メモリを使い切ったりせず、短い時間で終わる。
        private static void Map_HugeDeclaredChipSizesDoNotOverflow()
        {
            var map = new MapDocument();
            for (int i = 0; i < 3000; i++) map.Assets.Add(new MapAsset { Width = 32768, Height = 32768, Cell = i + 1 });
            map.Rows = new List<List<string>> { map.Assets.Select(a => a.Id).ToList() };
            MapDocument loaded = MapDocument.FromJson(map.ToJson());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, Rectangle> packed = loaded.Pack(8);
            Assert.IsTrue(clock.ElapsedMilliseconds < 5000, "packing finishes quickly");
            Assert.AreEqual(3000, packed.Count, "every chip is placed");
            Assert.IsTrue(packed.Values.All(r => r.X >= 0 && r.Y >= 0 && r.Right <= MapDocument.MaxRowWidth && r.Bottom > r.Y), "no overflowed coordinates");
            Assert.IsTrue(loaded.RangeSize().Width > 0 && loaded.RangeSize().Height > 0, "the range size does not overflow");
            Assert.IsTrue(MapDocument.NumbersOf(packed).Count == 3000, "numbers are assigned");
            // 1つのアニメーションが上限より広い（幅32768のコマ35枚）。コマが重ならず、上限を超えない（Codex 監査 2026-10-06）。
            var wide = new MapDocument();
            for (int i = 0; i < 35; i++) wide.Assets.Add(new MapAsset { Width = 32768, Height = 1, Cell = i + 1 });
            wide.Animations.Add(new MapAnimation { Id = "w", Frames = wide.Assets.Select(a => a.Id).ToList() });
            Assert.IsFalse(wide.FitsInRow(wide.Assets.Select(a => a.Id)), "too wide to make");
            Dictionary<string, Rectangle> wp = MapDocument.FromJson(wide.ToJson()).Pack(8);
            var list = wp.Values.ToList();
            for (int i = 0; i < list.Count; i++) for (int j = i + 1; j < list.Count; j++) Assert.IsFalse(list[i].IntersectsWith(list[j]), "frames do not overlap");
            Assert.IsTrue(list.All(r => r.Right <= MapDocument.MaxRowWidth), "frames stay within the width limit");
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sec_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteProject(string dir, string json, IDictionary<string, byte[]> entries)
        {
            string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".smproj");
            using (var stream = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                if (json != null)
                {
                    ZipArchiveEntry e = archive.CreateEntry("project.json");
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(json);
                }
                if (entries != null)
                    foreach (var pair in entries)
                    {
                        ZipArchiveEntry e = archive.CreateEntry(pair.Key);
                        using (Stream s = e.Open()) s.Write(pair.Value, 0, pair.Value.Length);
                    }
            }
            return path;
        }

        private static string Json(params string[] imageNames)
        {
            var images = string.Join(",", imageNames.Select(n => "{\"name\":" + Quote(n) + "}"));
            return "{\"formatVersion\":1,\"folders\":[{\"name\":\"f\",\"images\":[" + images + "]}]}";
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void Project_TraversalNamesStayInsideExtractDir()
        {
            string dir = TempDir();
            try
            {
                string extract = Path.Combine(dir, "extract");
                string outside = Path.Combine(dir, "evil.png");
                string path = WriteProject(dir, Json("..\\..\\evil.png", "../evil.png", "sub/../../evil.png", "C:\\evil.png", "ok.png"),
                    new Dictionary<string, byte[]>
                    {
                        { "images/000/..\\..\\evil.png", new byte[] { 1 } },
                        { "images/000/../evil.png", new byte[] { 1 } },
                        { "images/000/ok.png", new byte[] { 1, 2, 3 } }
                    });
                ProjectLoadResult result = ProjectFile.Load(path, extract);
                Assert.IsFalse(File.Exists(outside), "nothing is written outside the extract directory");
                string root = Path.GetFullPath(extract);
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(file);
                    if (full.EndsWith(".smproj")) continue;
                    Assert.IsTrue(full.StartsWith(root, StringComparison.OrdinalIgnoreCase), "file inside extract dir: " + full);
                }
                Assert.IsTrue(result.Document.Folders[0].Images.Any(i => i.Name == "ok.png"), "the normal image is kept");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Project_ReservedDeviceNamesAreRenamed()
        {
            foreach (string name in new[] { "CON", "con.png", "NUL.txt", "aux", "COM1.bmp", "lpt9", "PRN . png" })
            {
                string safe = ProjectFile.SafeFileName(name);
                Assert.IsTrue(safe.StartsWith("_"), "device name is renamed: " + name + " -> " + safe);
            }
            Assert.AreEqual("console.png", ProjectFile.SafeFileName("console.png"), "names that only start like a device name are unchanged");
            Assert.AreEqual("COM10.png", ProjectFile.SafeFileName("COM10.png"), "COM10 is not a device name");

            string dir = TempDir();
            try
            {
                string path = WriteProject(dir, Json("CON.png"), new Dictionary<string, byte[]> { { "images/000/CON.png", new byte[] { 9, 9 } } });
                ProjectLoadResult result = ProjectFile.Load(path, Path.Combine(dir, "x"));
                foreach (ProjectImage image in result.Document.Folders[0].Images)
                    Assert.IsTrue(File.Exists(image.Path), "the file exists as a normal file: " + image.Path);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Project_SafeFileNameLimitsLengthAndTrailingDots()
        {
            Assert.AreEqual("image", ProjectFile.SafeFileName("..."), "only dots");
            Assert.AreEqual("image", ProjectFile.SafeFileName("   "), "only spaces");
            Assert.AreEqual("a", ProjectFile.SafeFileName("a. . "), "trailing dots and spaces are removed");
            string longName = new string('x', 500) + ".png";
            string safe = ProjectFile.SafeFileName(longName);
            Assert.IsTrue(safe.Length <= 120, "length is capped");
            Assert.IsTrue(safe.EndsWith(".png"), "extension is kept");
            Assert.AreEqual("a_b_c.png", ProjectFile.SafeFileName("a:b*c.png"), "invalid characters (including stream marker ':') are replaced");
        }

        // 形式1（旧: 設定値1.6 = いまの1.0）で保存したジャンプ力は、読み込み時に新しい基準へ換算して、跳ぶ高さを保つ。
        private static void Project_LegacyJumpPowerIsConvertedToTheNewScale()
        {
            string dir = TempDir();
            try
            {
                string old = WriteProject(dir, "{\"formatVersion\":1,\"playerJump\":1.6,\"folders\":[]}", null);
                Assert.AreEqual(1.0m, ProjectFile.Load(old, Path.Combine(dir, "a")).Document.PlayerJump, "legacy 1.6 becomes 1.0");
                string old2 = WriteProject(dir, "{\"formatVersion\":1,\"playerJump\":3.2,\"folders\":[]}", null);
                Assert.AreEqual(2.0m, ProjectFile.Load(old2, Path.Combine(dir, "b")).Document.PlayerJump, "legacy 3.2 becomes 2.0");
                string current = WriteProject(dir, "{\"formatVersion\":2,\"playerJump\":1.6,\"folders\":[]}", null);
                Assert.AreEqual(1.6m, ProjectFile.Load(current, Path.Combine(dir, "c")).Document.PlayerJump, "current-format values are used as they are");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Project_OversizedDocumentIsRejected()
        {
            string dir = TempDir();
            try
            {
                string json = "{\"formatVersion\":1,\"folders\":[]," + new string(' ', (int)ProjectFile.MaxDocumentBytes + 10) + "}";
                string path = WriteProject(dir, json, null);
                bool rejected = false;
                try { ProjectFile.Load(path, Path.Combine(dir, "x")); }
                catch (ProjectFormatException) { rejected = true; }
                Assert.IsTrue(rejected, "a project.json over the limit is rejected");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Project_TooManyFoldersIsRejected()
        {
            string dir = TempDir();
            try
            {
                var sb = new StringBuilder("{\"formatVersion\":1,\"folders\":[");
                for (int i = 0; i < ProjectFile.MaxFolders + 1; i++) sb.Append(i == 0 ? "" : ",").Append("{\"name\":\"f\",\"images\":[]}");
                sb.Append("]}");
                string path = WriteProject(dir, sb.ToString(), null);
                bool rejected = false;
                try { ProjectFile.Load(path, Path.Combine(dir, "x")); }
                catch (ProjectFormatException) { rejected = true; }
                Assert.IsTrue(rejected, "too many folders are rejected");
            }
            finally { Directory.Delete(dir, true); }
        }

        // 壊れた内容は、どんな形でも ProjectFormatException として断られること（他の例外で落ちない）。
        private static void Project_JunkOnlyFailsAsFormatException()
        {
            string dir = TempDir();
            try
            {
                var cases = new List<string>
                {
                    "", "not json", "{", "[]", "null", "{\"formatVersion\":\"x\"}", "{\"formatVersion\":1,\"folders\":\"x\"}",
                    "{\"formatVersion\":1,\"folders\":[null]}", "{\"formatVersion\":1,\"folders\":[{\"images\":[null]}]}",
                    "{\"formatVersion\":99999999999999999999}", "{\"formatVersion\":1,\"columns\":1e999}",
                    new string('[', 20000), "{\"formatVersion\":1,\"memos\":[{\"x\":1e39,\"y\":-1e39,\"width\":-5,\"text\":null}]}",
                    "{\"formatVersion\":1,\"folders\":[{\"name\":\"" + new string('a', 100000) + "\",\"images\":[{\"name\":\"" + new string('b', 100000) + "\"}]}]}",
                    // マップの JSON が null・配列・中に null を含む（Codex 監査 2026-10-06）。
                    "{\"formatVersion\":1,\"map\":\"null\"}", "{\"formatVersion\":1,\"map\":\"[]\"}", "{\"formatVersion\":1,\"map\":\"not json\"}",
                    "{\"formatVersion\":1,\"map\":\"{\\\"Assets\\\":[null,{\\\"Id\\\":\\\"a\\\",\\\"Path\\\":null}],\\\"Rows\\\":[null,[null]],\\\"Animations\\\":[{\\\"Id\\\":\\\"x\\\",\\\"Frames\\\":null}],\\\"Tiles\\\":[null],\\\"BasisId\\\":null}\"}"
                };
                int index = 0;
                foreach (string json in cases)
                {
                    string path = WriteProject(dir, json, null);
                    try
                    {
                        ProjectLoadResult r = ProjectFile.Load(path, Path.Combine(dir, "x" + index));
                        Assert.IsTrue(r.Document != null, "case " + index + " loaded with a document");
                        if (!string.IsNullOrEmpty(r.Document.MapJson)) MapDocument.FromJson(r.Document.MapJson).Pack(8);   // 読めたマップは並べられる
                    }
                    catch (ProjectFormatException) { }
                    catch (IOException) { }   // 極端に長い名前でパスが作れない場合
                    index++;
                }
                // ZIPではないファイル、空のZIP
                string notZip = Path.Combine(dir, "a.smproj");
                File.WriteAllBytes(notZip, new byte[] { 1, 2, 3, 4, 5 });
                bool failedCleanly = false;
                try { ProjectFile.Load(notZip, Path.Combine(dir, "y")); } catch (ProjectFormatException) { failedCleanly = true; }
                Assert.IsTrue(failedCleanly, "a non-zip file fails as a format exception");
                string emptyZip = WriteProject(dir, null, null);
                failedCleanly = false;
                try { ProjectFile.Load(emptyZip, Path.Combine(dir, "z")); } catch (ProjectFormatException) { failedCleanly = true; }
                Assert.IsTrue(failedCleanly, "a zip without project.json fails as a format exception");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void Project_CopyBoundedStopsAtTheLimit()
        {
            var method = typeof(ProjectFile).GetMethod("CopyBounded", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue(method != null, "CopyBounded exists");
            using (var input = new MemoryStream(new byte[1000]))
            using (var output = new MemoryStream())
            {
                Assert.AreEqual(1000L, (long)method.Invoke(null, new object[] { input, output, 1000L }), "exactly the limit is fine");
            }
            bool stopped = false;
            using (var input = new MemoryStream(new byte[1001]))
            using (var output = new MemoryStream())
            {
                try { method.Invoke(null, new object[] { input, output, 1000L }); }
                catch (System.Reflection.TargetInvocationException ex) { stopped = ex.InnerException is ProjectFormatException; }
                Assert.IsTrue(output.Length <= 1001, "output is bounded");
            }
            Assert.IsTrue(stopped, "one byte over the limit stops the copy");
        }

        // 小さなファイルで巨大な寸法を宣言する画像（PNGのIHDRだけ）は、デコードせずに断る。
        private static void Image_DeclaredHugeSizeIsRefusedBeforeDecoding()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "bomb.png");
                File.WriteAllBytes(path, HugePngHeader(60000, 60000));
                var pipeline = new SpriteImagePipeline();
                bool refused = false;
                try { pipeline.GetNominalSize(path, 1); }
                catch (ImageTooLargeException) { refused = true; }
                Assert.IsTrue(refused, "a 60000 x 60000 declaration is refused");

                refused = false;
                try { SpriteImagePipeline.LoadOne(path, 1, false, SpriteColorBlendMode.Multiply, Color.White, 100, System.Threading.CancellationToken.None); }
                catch (ImageTooLargeException) { refused = true; }
                Assert.IsTrue(refused, "the export path refuses it too");

                var folder = new LoadFolderRequest { Name = "f" };
                folder.Items.Add(new LoadItemRequest { Source = null, Path = path });
                refused = false;
                try { pipeline.Load(new List<LoadFolderRequest> { folder }, 1, false, SpriteColorBlendMode.Multiply, Color.White, 100, System.Threading.CancellationToken.None); }
                catch (ImageTooLargeException) { refused = true; }
                Assert.IsTrue(refused, "the preview path refuses it too");
            }
            finally { Directory.Delete(dir, true); }
        }

        // 署名 + IHDR + IEND だけのPNG（画素データなし）。ヘッダーの寸法だけを偽る。
        private static byte[] HugePngHeader(int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
                ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
                ihdr[8] = 8; ihdr[9] = 6;
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
            s.Write(len, 0, 4);
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in t.Concat(data))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            crc ^= 0xFFFFFFFFu;
            s.Write(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
        }
    }
}
