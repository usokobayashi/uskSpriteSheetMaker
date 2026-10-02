//==================================================
// CellRemapTests
// 横セル数の変更で状態への割り当てが同じ画像を指し続けること、プレビュー切り替えで全体表示へ戻ることの検証。
//==================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class CellRemapTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "CellRemap_ColumnsChange_KeepsAssignmentsOnSameImages", Action = ColumnsChange_KeepsAssignmentsOnSameImages };
            yield return new TestCase { Name = "CellRemap_SingleFolder_ColumnsChangeKeepsNumbers", Action = SingleFolder_ColumnsChangeKeepsNumbers };
            yield return new TestCase { Name = "CellRemap_UndoRestoresColumnsAndAssignmentsTogether", Action = UndoRestoresColumnsAndAssignmentsTogether };
            yield return new TestCase { Name = "CellRemap_PreviewModeChange_ResetsPreviewToFit", Action = PreviewModeChange_ResetsPreviewToFit };
        }

        //--------------
        // 補助
        //--------------
        private static T Field<T>(MainForm form, string name)
        {
            return (T)typeof(MainForm).GetField(name, Flags).GetValue(form);
        }

        private static object Call(MainForm form, string name, params object[] args)
        {
            return typeof(MainForm).GetMethod(name, Flags).Invoke(form, args);
        }

        // 画像を n 枚持つフォルダを足す（画像ファイルは読まないので、パスは存在しなくてよい）。
        private static void AddFolder(MainForm form, string name, int count)
        {
            Type folderType = typeof(MainForm).GetNestedType("ImageFolder", BindingFlags.NonPublic);
            object folder = Activator.CreateInstance(folderType, true);
            folderType.GetField("Name").SetValue(folder, name);
            var items = (IList)folderType.GetField("Items").GetValue(folder);
            for (int i = 0; i < count; i++) items.Add(new MainForm.ImageItem { Path = @"C:\nonexistent\" + name + i + ".png" });
            ((IList)Field<object>(form, "folders")).Add(folder);
        }

        private static AnimationClipSettings Clip(MainForm form, PlayerAnimationState state)
        {
            return Field<Dictionary<PlayerAnimationState, AnimationClipSettings>>(form, "playerClips")[state];
        }

        //--------------
        // テスト
        //--------------
        // フォルダA（3枚）・B（3枚）。横8ではBが9〜11、横2ではAが2行使うのでBは5〜7。
        private static void ColumnsChange_KeepsAssignmentsOnSameImages()
        {
            using (var form = new MainForm())
            {
                var columns = Field<NumericUpDown>(form, "columnsBox");
                columns.Value = 8;
                AddFolder(form, "A", 3);
                AddFolder(form, "B", 3);
                AnimationClipSettings clip = Clip(form, PlayerAnimationState.MoveRight);
                clip.StartCell = 9;
                clip.EndCell = 11;
                AnimationClipSettings partial = Clip(form, PlayerAnimationState.Idle);
                partial.StartCell = 2;
                partial.EndCell = 10;   // Aの2枚目〜Bの2枚目（間の空きセルを含む）

                columns.Value = 2;
                Assert.AreEqual(5, clip.StartCell, "B starts on the new row after A");
                Assert.AreEqual(7, clip.EndCell, "B ends on the same image");
                Assert.AreEqual(2, partial.StartCell, "a range across folders keeps its first image");
                Assert.AreEqual(6, partial.EndCell, "a range across folders keeps its last image");

                columns.Value = 3;
                Assert.AreEqual(4, clip.StartCell, "with 3 columns A fills one row exactly");
                Assert.AreEqual(6, clip.EndCell, "B moves with its images");

                columns.Value = 8;
                Assert.AreEqual(9, clip.StartCell, "returning to 8 columns restores the original numbers");
                Assert.AreEqual(11, clip.EndCell, "returning to 8 columns restores the original numbers");
            }
        }

        private static void SingleFolder_ColumnsChangeKeepsNumbers()
        {
            using (var form = new MainForm())
            {
                var columns = Field<NumericUpDown>(form, "columnsBox");
                columns.Value = 8;
                AddFolder(form, "A", 20);
                AnimationClipSettings clip = Clip(form, PlayerAnimationState.MoveLeft);
                clip.StartCell = 4;
                clip.EndCell = 15;
                columns.Value = 5;
                Assert.AreEqual(4, clip.StartCell, "one folder numbers images straight through");
                Assert.AreEqual(15, clip.EndCell, "one folder numbers images straight through");
            }
        }

        // 取り消しでは、保存した横セル数と割り当てをそのまま戻す（復元中に付け替えを重ねない）。
        private static void UndoRestoresColumnsAndAssignmentsTogether()
        {
            using (var form = new MainForm())
            {
                var columns = Field<NumericUpDown>(form, "columnsBox");
                columns.Value = 8;
                AddFolder(form, "A", 3);
                AddFolder(form, "B", 3);
                AnimationClipSettings clip = Clip(form, PlayerAnimationState.MoveRight);
                clip.StartCell = 9;
                clip.EndCell = 11;
                object before = Call(form, "CaptureState");

                columns.Value = 2;
                Assert.AreEqual(5, Clip(form, PlayerAnimationState.MoveRight).StartCell, "remapped after the change");

                Call(form, "RestoreState", before);
                AnimationClipSettings restored = Clip(form, PlayerAnimationState.MoveRight);
                Assert.AreEqual(8, (int)columns.Value, "columns restored");
                Assert.AreEqual(9, restored.StartCell, "assignment restored as saved");
                Assert.AreEqual(11, restored.EndCell, "assignment restored as saved");

                columns.Value = 2;
                Assert.AreEqual(5, Clip(form, PlayerAnimationState.MoveRight).StartCell, "remapping still works after a restore");
            }
        }

        private static void PreviewModeChange_ResetsPreviewToFit()
        {
            using (var form = new MainForm())
            {
                var canvas = Field<PreviewCanvas>(form, "animCanvas");
                typeof(PreviewCanvas).GetField("zoom", Flags).SetValue(canvas, 2.5f);
                Assert.IsFalse(canvas.IsFitMode, "zoomed in before switching");
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Player);
                Assert.IsTrue(canvas.IsFitMode, "switching the preview resets it like the F key");

                typeof(PreviewCanvas).GetField("zoom", Flags).SetValue(canvas, 2.5f);
                Call(form, "SetPreviewTargetMode", PreviewTargetMode.Effect);
                Assert.IsTrue(canvas.IsFitMode, "every switch resets the view");
            }
        }
    }
}
