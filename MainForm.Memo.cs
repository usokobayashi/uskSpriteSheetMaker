using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // シート上の付箋メモ（追加・編集・移動・削除）と「プロジェクトをリセット」。
    // シートの外側を右クリックしたときのメニューから使う。メモは取り消し/やり直しとプロジェクトの保存の対象。
    public sealed partial class MainForm
    {
        private readonly List<SheetMemo> memos = new List<SheetMemo>();
        private TextBox memoEditor;
        private SheetMemo editingMemo;
        private bool sanitizingMemoText;
        private float firstCellWidth = 64;
        private Point? contextMenuPointForTests;    // テストで右クリックの位置（キャンバス内）を指定する

        private sealed class MemoState
        {
            public float X, Y, Width;
            public string Text = "";
        }

        //--------------
        // 初期化
        //--------------
        private void InitializeMemoSupport()
        {
            sheetCanvas.Memos = memos;
            sheetCanvas.MemoEditRequested += (s, e) => BeginMemoEdit(e.Memo);
            sheetCanvas.MemoMoved += (s, e) => RepositionMemoEditor();
            sheetCanvas.MemoMoveFinished += (s, e) => CommitUndoableChange();
            // パン/ズームで位置が変わったら、編集中の入力欄も追従させる。
            sheetCanvas.ZoomChanged += (s, e) => RepositionMemoEditor();
        }

        //--------------
        // 右クリックメニュー（メモの上 / シートの外側）
        //--------------
        // メモの上: メモを削除。シートの外側: メモを追加・プロジェクトをリセット。それ以外は false。
        private bool BuildMemoContextMenu(CancelEventArgs e)
        {
            Point point = contextMenuPointForTests ?? sheetCanvas.PointToClient(Cursor.Position);
            bool grab;
            SheetMemo memo = sheetCanvas.HitTestMemo(point, out grab);
            if (memo != null)
            {
                sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.memoDelete", () => DeleteMemo(memo)));
                return true;
            }
            if (!sheetCanvas.IsOutsideSheet(point)) return false;
            PointF at = sheetCanvas.ScreenToSheet(point);
            sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.memoAdd", () => AddMemoAt(at)));
            sheetContextMenu.Items.Add(new ToolStripSeparator());
            sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.projectReset", ResetProjectContents));
            return true;
        }

        //--------------
        // メモの操作
        //--------------
        private void AddMemoAt(PointF sheetPoint)
        {
            if (memos.Count >= MemoText.MaxMemos) return;
            var memo = new SheetMemo
            {
                // 初めに入った画像より少し大きめ
                Width = MemoText.SafeWidth(firstCellWidth * 1.25f),
                X = MemoText.SafeCoordinate(sheetPoint.X),
                Y = MemoText.SafeCoordinate(sheetPoint.Y)
            };
            memos.Add(memo);
            sheetCanvas.Invalidate();
            sheetCanvas.AnimateMemoAppear(memo);
            BeginMemoEdit(memo);
        }

        private void DeleteMemo(SheetMemo memo)
        {
            if (ReferenceEquals(memo, editingMemo)) EndMemoEdit(false);
            if (!memos.Remove(memo)) return;
            sheetCanvas.AnimateMemoVanish(memo);
            sheetCanvas.Invalidate();
            CommitUndoableChange();
        }

        //--------------
        // 文字入力（メモの上に重ねる入力欄）
        //--------------
        private void BeginMemoEdit(SheetMemo memo)
        {
            if (ReferenceEquals(memo, editingMemo)) { memoEditor.Focus(); return; }
            EndMemoEdit(true);
            editingMemo = memo;
            sheetCanvas.EditingMemo = memo;

            memoEditor = new TextBox
            {
                Multiline = true,
                WordWrap = true,
                AcceptsReturn = false,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.None,
                BackColor = Color.FromArgb(72, 76, 82),
                ForeColor = PreviewCanvas.MemoTextColor,
                MaxLength = MemoText.MaxLength * 2,
                // フォーム全体はIMEを無効にしているが、メモは日本語・中国語などを入力できるようにする。
                ImeMode = ImeMode.NoControl,
                ShortcutsEnabled = true,
                Text = memo.Text
            };
            memoEditor.TextChanged += MemoEditor_TextChanged;
            memoEditor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    EndMemoEdit(true);
                }
            };
            memoEditor.Leave += (s, e) => EndMemoEdit(true);
            sheetCanvas.Controls.Add(memoEditor);
            RepositionMemoEditor();
            memoEditor.Focus();
            memoEditor.SelectionStart = memoEditor.TextLength;
        }

        // 貼り付けや無理な値でも、改行・制御文字・上限を整えて壊れないようにする。
        private void MemoEditor_TextChanged(object sender, EventArgs e)
        {
            if (sanitizingMemoText || editingMemo == null) return;
            string safe = MemoText.Sanitize(memoEditor.Text);
            if (safe != memoEditor.Text)
            {
                sanitizingMemoText = true;
                int caret = Math.Min(memoEditor.SelectionStart, safe.Length);
                memoEditor.Text = safe;
                memoEditor.SelectionStart = caret;
                sanitizingMemoText = false;
            }
            editingMemo.Text = safe;
            RepositionMemoEditor();
            sheetCanvas.Invalidate();
        }

        private void RepositionMemoEditor()
        {
            if (memoEditor == null || editingMemo == null || memoEditor.IsDisposed) return;
            SheetMemo memo = editingMemo;
            float pad = MemoText.Padding(memo.Width);
            RectangleF inner = sheetCanvas.SheetToScreen(new RectangleF(memo.X + pad, memo.Y + pad,
                MemoText.InnerWidth(memo.Width), memo.Height - 2 * pad));
            float zoom = Math.Max(0.01f, inner.Width / Math.Max(1f, MemoText.InnerWidth(memo.Width)));
            float fontPixels = Math.Max(4f, MemoText.FontSize(memo.Width) * zoom);
            if (memoEditor.Font == null || Math.Abs(memoEditor.Font.Size - fontPixels) > 0.25f || memoEditor.Font.Unit != GraphicsUnit.Pixel)
            {
                Font old = memoEditor.Font;
                memoEditor.Font = UiFont.Create(fontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
                if (old != null && !ReferenceEquals(old, Font)) old.Dispose();
            }
            memoEditor.SetBounds((int)Math.Round(inner.X), (int)Math.Round(inner.Y),
                Math.Max(8, (int)Math.Round(inner.Width)), Math.Max(8, (int)Math.Round(inner.Height)));
        }

        // keep=true: 入力を確定（空なら削除）。false: 何もせず閉じる。
        private void EndMemoEdit(bool keep)
        {
            if (memoEditor == null) return;
            SheetMemo memo = editingMemo;
            TextBox editor = memoEditor;
            memoEditor = null;
            editingMemo = null;
            sheetCanvas.EditingMemo = null;
            if (keep && memo != null) memo.Text = MemoText.Sanitize(editor.Text).Trim();
            editor.TextChanged -= MemoEditor_TextChanged;
            sheetCanvas.Controls.Remove(editor);
            editor.Dispose();
            if (memo != null && string.IsNullOrEmpty(memo.Text) && memos.Remove(memo)) sheetCanvas.AnimateMemoVanish(memo);
            sheetCanvas.Invalidate();
            if (!closing) sheetCanvas.Focus();
            CommitUndoableChange();
        }

        //--------------
        // プロジェクトをリセット
        //--------------
        // 配置した画像・遷移条件などの設定・メモをすべて初期状態へ戻す。確認を挟み、元に戻すで復元できる。
        private void ResetProjectContents()
        {
            if (!ShowDarkConfirm(Loc.T("dialog.resetTitle"), Loc.T("dialog.resetMessage"), Loc.T("button.reset"))) return;
            EndMemoEdit(false);
            EndAssignMode();
            RestoreState(defaultSnapshot);
            ResetViewsToFit();
            CommitUndoableChange();
            statusLabel.Text = Loc.T("message.projectReset");
        }

        //--------------
        // 取り消し/やり直し・プロジェクトとの変換
        //--------------
        private List<MemoState> CaptureMemos()
        {
            return memos.Select(m => new MemoState { X = m.X, Y = m.Y, Width = m.Width, Text = m.Text }).ToList();
        }

        private void RestoreMemos(IList<MemoState> states)
        {
            EndMemoEdit(false);
            memos.Clear();
            foreach (MemoState state in states.Take(MemoText.MaxMemos))
                memos.Add(new SheetMemo
                {
                    X = MemoText.SafeCoordinate(state.X),
                    Y = MemoText.SafeCoordinate(state.Y),
                    Width = MemoText.SafeWidth(state.Width),
                    Text = state.Text
                });
            sheetCanvas.Invalidate();
        }

        private static bool MemosEqual(IList<MemoState> left, IList<MemoState> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i].X != right[i].X || left[i].Y != right[i].Y || left[i].Width != right[i].Width || left[i].Text != right[i].Text)
                    return false;
            return true;
        }
    }
}
