using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class MemoTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Memo_Sanitize_RemovesControlCharsAndJoinsLines", Action = Sanitize_RemovesControlCharsAndJoinsLines };
            yield return new TestCase { Name = "Memo_Sanitize_LimitsLengthWithoutSplittingCharacters", Action = Sanitize_LimitsLengthWithoutSplittingCharacters };
            yield return new TestCase { Name = "Memo_SafeNumbers_ClampBrokenValues", Action = SafeNumbers_ClampBrokenValues };
            yield return new TestCase { Name = "Memo_Height_GrowsOneLinePerTenCharacters", Action = Height_GrowsOneLinePerTenCharacters };
            yield return new TestCase { Name = "Memo_Canvas_GrabBandMovesAndCenterEdits", Action = Canvas_GrabBandMovesAndCenterEdits };
            yield return new TestCase { Name = "Memo_Project_LoadSanitizesBrokenMemos", Action = Project_LoadSanitizesBrokenMemos };
        }

        private static readonly string emoji = char.ConvertFromUtf32(0x1F600);

        private static void Sanitize_RemovesControlCharsAndJoinsLines()
        {
            Assert.AreEqual("abc def", MemoText.Sanitize("abc\r\ndef"), "line breaks become a space");
            Assert.AreEqual("a b", MemoText.Sanitize("a\tb"), "tabs become a space");
            Assert.AreEqual("ab", MemoText.Sanitize("a\u0000\u0007\u001Bb"), "control characters are removed");
            Assert.AreEqual("ab", MemoText.Sanitize("a" + (char)0x202E + "b" + (char)0x200B), "direction overrides and zero-width characters are removed");
            Assert.AreEqual("", MemoText.Sanitize(null), "null is empty");
            Assert.AreEqual("あいう", MemoText.Sanitize("あいう"), "Japanese text is kept");
            Assert.AreEqual("ab", MemoText.Sanitize("a\uD800b"), "a lone surrogate is removed");
            Assert.AreEqual("a" + emoji + "b", MemoText.Sanitize("a" + emoji + "b"), "a valid surrogate pair (emoji) is kept");
        }

        private static void Sanitize_LimitsLengthWithoutSplittingCharacters()
        {
            string longText = new string('あ', 5000);
            Assert.AreEqual(MemoText.MaxLength, MemoText.Sanitize(longText).Length, "pasting a huge text is cut to the limit");
            string emojis = string.Concat(Enumerable.Repeat(emoji, 400));
            string cut = MemoText.Sanitize(emojis);
            Assert.AreEqual(MemoText.MaxLength * 2, cut.Length, "emoji count is limited by characters, not UTF-16 units");
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.Length - 1]), "the cut never leaves half of an emoji");
        }

        private static void SafeNumbers_ClampBrokenValues()
        {
            Assert.AreEqual(0f, MemoText.SafeCoordinate(float.NaN), "NaN position");
            Assert.AreEqual(0f, MemoText.SafeCoordinate(float.PositiveInfinity), "infinite position");
            Assert.AreEqual(MemoText.MaxCoordinate, MemoText.SafeCoordinate(9e30f), "huge position is clamped");
            Assert.AreEqual(MemoText.MinWidth, MemoText.SafeWidth(-5f), "negative width");
            Assert.AreEqual(MemoText.MaxWidth, MemoText.SafeWidth(1e20f), "huge width");
            Assert.AreEqual(80f, MemoText.SafeWidth(float.NaN), "NaN width falls back to a default");
        }

        // 幅は全角10文字が1行に収まる基準。10文字なら1行、11文字目で2行、20文字を超えると3行。
        private static void Height_GrowsOneLinePerTenCharacters()
        {
            var memo = new SheetMemo { Width = 200 };
            float empty = memo.Height;
            memo.Text = new string('あ', 10);
            float ten = memo.Height;
            memo.Text = new string('あ', 11);
            float eleven = memo.Height;
            memo.Text = new string('あ', 21);
            float twentyOne = memo.Height;
            Assert.IsTrue(Math.Abs(empty - ten) < 1f, "an empty memo and a 10-character memo are one line tall (" + empty + " / " + ten + ")");
            float line = eleven - ten;
            Assert.IsTrue(line > 5f, "the 11th character adds a line (" + line + ")");
            Assert.IsTrue(Math.Abs((twentyOne - eleven) - line) < 1.5f, "the 21st character adds one more line, no more (" + (twentyOne - eleven) + " vs " + line + ")");
        }

        // メモの外側20%はつかんで動かす範囲、中央は文字を打つ範囲。
        private static void Canvas_GrabBandMovesAndCenterEdits()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(PreviewCanvas);
            using (var canvas = new PreviewCanvas())
            using (var bitmap = new Bitmap(400, 300, PixelFormat.Format32bppArgb))
            {
                canvas.Size = new Size(600, 450);
                var cells = new List<PreviewRect> { new PreviewRect { Rect = new Rectangle(0, 0, 400, 300), Number = 1 } };
                canvas.SetImage(bitmap, cells);
                var memo = new SheetMemo { X = 100, Y = 80, Width = 120, Text = "メモ" };
                canvas.Memos = new List<SheetMemo> { memo };
                RectangleF screen = canvas.SheetToScreen(new RectangleF(memo.X, memo.Y, memo.Width, memo.Height));

                bool grab;
                var center = new Point((int)(screen.X + screen.Width / 2), (int)(screen.Y + screen.Height / 2));
                Assert.IsTrue(canvas.HitTestMemo(center, out grab) == memo && !grab, "the center is the typing area");
                var edge = new Point((int)(screen.X + 2), (int)(screen.Y + screen.Height / 2));
                Assert.IsTrue(canvas.HitTestMemo(edge, out grab) == memo && grab, "the outer band is the grab area");
                Assert.IsTrue(canvas.HitTestMemo(new Point(1, 1), out grab) == null, "outside the memo is not a memo");

                int editRequests = 0, moves = 0, finished = 0;
                canvas.MemoEditRequested += (s, e) => editRequests++;
                canvas.MemoMoved += (s, e) => moves++;
                canvas.MemoMoveFinished += (s, e) => finished++;

                type.GetMethod("OnMouseDown", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0) });
                type.GetMethod("OnMouseUp", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0) });
                Assert.AreEqual(1, editRequests, "clicking the center asks to edit the text");
                Assert.AreEqual(0, moves, "clicking the center does not move the memo");

                float startX = memo.X, startY = memo.Y;
                type.GetMethod("OnMouseDown", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, edge.X, edge.Y, 0) });
                type.GetMethod("OnMouseMove", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 0, edge.X + 60, edge.Y + 30, 0) });
                type.GetMethod("OnMouseUp", flags).Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, edge.X + 60, edge.Y + 30, 0) });
                Assert.IsTrue(memo.X > startX + 10 && memo.Y > startY + 5, "dragging the outer band moves the memo (" + startX + "," + startY + " -> " + memo.X + "," + memo.Y + ")");
                Assert.IsTrue(moves >= 1 && finished == 1, "move events are raised");
                Assert.IsTrue(canvas.IsFitMode, "moving a memo does not pan the sheet");
            }
        }

        // 壊れたプロジェクトのメモ（不正な文字・巨大な値・多すぎる数）でも読み込める。
        private static void Project_LoadSanitizesBrokenMemos()
        {
            string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpriteSheetMakerMemoTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(work);
            string file = System.IO.Path.Combine(work, "broken.smproj");
            var memos = new System.Text.StringBuilder();
            for (int i = 0; i < 400; i++) memos.Append(i == 0 ? "" : ",").Append("{\"x\": 1e30, \"y\": -1e30, \"width\": -10, \"text\": \"a\\u0000b\\nc\"}");
            using (var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new System.IO.StreamWriter(zip.CreateEntry("project.json").Open(), new System.Text.UTF8Encoding(false)))
                writer.Write("{\"formatVersion\": 1, \"folders\": [], \"memos\": [" + memos + ", null]}");
            ProjectLoadResult loaded = ProjectFile.Load(file, System.IO.Path.Combine(work, "x"));
            Assert.AreEqual(MemoText.MaxMemos, loaded.Document.Memos.Count, "the number of memos is limited");
            ProjectMemo first = loaded.Document.Memos[0];
            Assert.AreEqual(MemoText.MaxCoordinate, first.X, "huge x is clamped");
            Assert.AreEqual(-MemoText.MaxCoordinate, first.Y, "huge negative y is clamped");
            Assert.AreEqual(MemoText.MinWidth, first.Width, "negative width is clamped");
            Assert.AreEqual("ab c", first.Text, "control characters and line breaks in the text are cleaned");
        }
    }
}
