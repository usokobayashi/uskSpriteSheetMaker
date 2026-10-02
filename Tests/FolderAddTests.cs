//==================================================
// FolderAddTests
// 画像の追加: 選んだ順に関係なくファイル名の昇順（数字は数の大きさ順）で入ること。
//==================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class FolderAddTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "FolderAdd_AddedFilesAreSortedByNaturalName", Action = AddedFilesAreSortedByNaturalName };
        }

        private static void AddedFilesAreSortedByNaturalName()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SpriteSheetMakerTests-add-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var names = new[] { "slime_10.png", "slime_2.png", "slime_1.png", "slime_20.png" };   // ファイル選択の画面が返しうる順
                foreach (string name in names)
                    using (var bmp = new Bitmap(2, 2)) bmp.Save(Path.Combine(dir, name), ImageFormat.Png);

                using (var form = new MainForm())
                {
                    // 「ファイルを追加」と同じ呼び方（並べ替えあり・選択中のフォルダなし）
                    typeof(MainForm).GetMethod("AddAutoFolder", Flags).Invoke(form,
                        new object[] { names.Select(n => Path.Combine(dir, n)).ToArray(), true, null });
                    var folders = (IList)typeof(MainForm).GetField("folders", Flags).GetValue(form);
                    Assert.AreEqual(1, folders.Count, "one new folder");
                    object folder = folders[0];
                    var items = (IList)folder.GetType().GetField("Items").GetValue(folder);
                    string order = string.Join(",", items.Cast<MainForm.ImageItem>().Select(i => Path.GetFileName(i.Path)));
                    Assert.AreEqual("slime_1.png,slime_2.png,slime_10.png,slime_20.png", order, "natural ascending order");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
