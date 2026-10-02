using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // シートのセル選択（Shift+クリック/ドラッグ）、右クリックメニュー、状態への「割り当て」。
    // 選択はツリーの画像選択（selectedImages）と同じもので、フォルダタブの操作がそのまま使える。
    public sealed partial class MainForm
    {
        private readonly Dictionary<int, ImageItem> cellItems = new Dictionary<int, ImageItem>();
        private readonly ContextMenuStrip sheetContextMenu = new ContextMenuStrip();
        private HashSet<ImageItem> selectionBase = new HashSet<ImageItem>();
        private int selectionAnchorCell;

        private bool assignMode;
        private AssignOverlayForm assignOverlay;
        private AssignInputFilter assignFilter;
        private Timer assignTimer;
        private string assignOverlaySignature = "";
        private int assignFirstCell, assignLastCell;

        //--------------
        // 初期化
        //--------------
        private void InitializeSelectionSupport()
        {
            sheetCanvas.IsCellSelected = number =>
            {
                ImageItem item;
                return cellItems.TryGetValue(number, out item) && selectedImages.Contains(item);
            };
            sheetCanvas.CellGesture += SheetCanvas_CellGesture;
            // ツリー側の選択が変わったら、シートの強調も更新する。
            treeView.Invalidated += (s, e) => sheetCanvas.Invalidate();

            sheetContextMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            sheetContextMenu.ShowImageMargin = false;
            sheetContextMenu.Opening += SheetContextMenu_Opening;
            sheetCanvas.ContextMenuStrip = sheetContextMenu;

            Move += (s, e) => RepositionAssignOverlay();
            SizeChanged += (s, e) => RepositionAssignOverlay();
        }

        // シートを作り直すたびに、セル番号と画像の対応を取り直す。
        private void RebuildCellMap(SheetLayout layout)
        {
            cellItems.Clear();
            firstCellWidth = layout.Cells.Count > 0 ? layout.Cells[0].Rect.Width : 64;
            foreach (FramePlacement placement in layout.Placements)
                if (placement.Source != null) cellItems[placement.CellNumber] = placement.Source;
            if (selectionAnchorCell != 0 && !cellItems.ContainsKey(selectionAnchorCell)) selectionAnchorCell = 0;
        }

        //--------------
        // セル選択
        //--------------
        private void SheetCanvas_CellGesture(object sender, CellGestureEventArgs e)
        {
            if (assignMode || restoringState) return;
            switch (e.Kind)
            {
                case CellGestureKind.Clear:
                    if (selectedImages.Count == 0 && selectedFolders.Count == 0) return;
                    selectedFolders.Clear();
                    selectedImages.Clear();
                    imageSelectionAnchor = null;
                    folderSelectionAnchor = null;
                    selectionAnchorCell = 0;
                    RefreshSelectionViews();
                    CommitUndoableChange();
                    break;
                case CellGestureKind.Begin:
                    selectionBase = e.Additive ? new HashSet<ImageItem>(selectedImages) : new HashSet<ImageItem>();
                    ApplyCellRange(e.StartCell, e.StartCell);
                    break;
                case CellGestureKind.Drag:
                    ApplyCellRange(e.StartCell, e.Cell);
                    break;
                case CellGestureKind.End:
                    ApplyCellRange(e.StartCell, e.Cell);
                    selectionAnchorCell = e.Cell;
                    CommitUndoableChange();
                    break;
                case CellGestureKind.RangeClick:
                    // 直前に選んだセルから、クリックしたセルまでを番号順に追加する。
                    selectionBase = new HashSet<ImageItem>(selectedImages);
                    ApplyCellRange(selectionAnchorCell > 0 ? selectionAnchorCell : e.Cell, e.Cell);
                    selectionAnchorCell = e.Cell;
                    CommitUndoableChange();
                    break;
            }
        }

        // from〜to（大小どちらでも）のセル番号の画像を、基準の選択に加えて選ぶ。
        private void ApplyCellRange(int from, int to)
        {
            int low = Math.Min(from, to), high = Math.Max(from, to);
            selectedFolders.Clear();
            selectedImages.Clear();
            foreach (ImageItem item in selectionBase) selectedImages.Add(item);
            foreach (KeyValuePair<int, ImageItem> pair in cellItems)
                if (pair.Key >= low && pair.Key <= high) selectedImages.Add(pair.Value);
            ImageItem last;
            imageSelectionAnchor = cellItems.TryGetValue(to, out last) ? last : null;
            folderSelectionAnchor = null;
            RefreshSelectionViews();
        }

        private void RefreshSelectionViews()
        {
            treeView.Invalidate();
            sheetCanvas.Invalidate();
        }

        // 選択中の画像のセル番号（昇順）。
        private List<int> GetSelectedCellNumbers()
        {
            return cellItems.Where(pair => selectedImages.Contains(pair.Value)).Select(pair => pair.Key).OrderBy(n => n).ToList();
        }

        //--------------
        // 右クリックメニュー
        //--------------
        private void SheetContextMenu_Opening(object sender, CancelEventArgs e)
        {
            if (assignMode)
            {
                e.Cancel = true;
                return;
            }
            sheetContextMenu.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            sheetContextMenu.BackColor = darkPanel;
            sheetContextMenu.ForeColor = lightText;
            sheetContextMenu.Items.Clear();
            // メモの上、またはシートの外側なら、メモとリセットのメニュー。
            if (BuildMemoContextMenu(e)) return;
            if (GetSelectedCellNumbers().Count == 0)
            {
                e.Cancel = true;
                return;
            }
            // 並びは「割り当て → 新規フォルダ → 削除」（削除は危険な操作なので最後）。
            if (previewWorkspacePage == PreviewWorkspacePage.StateTransitions)
                sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.sheetAssign", BeginAssignMode));
            sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.sheetNewFolder", CreateFolderFromSelection));
            sheetContextMenu.Items.Add(CreateSheetMenuItem("menu.sheetDelete", RemoveSelectedNode));
        }

        private ToolStripMenuItem CreateSheetMenuItem(string textKey, Action action)
        {
            var item = new ToolStripMenuItem(Loc.T(textKey)) { ForeColor = lightText };
            item.Click += (s, e) => action();
            return item;
        }

        // 選択中の画像を、新しいフォルダへ「移す」（複製ではない）。フォルダ分けされていない連番の画像を整理する操作で、
        // 並び順は変えない: 新しいフォルダは、選んだ範囲があった位置に入る。
        //   ・範囲がフォルダの先頭      → 元のフォルダの手前に新しいフォルダ
        //   ・範囲がフォルダの末尾      → 元のフォルダの直後に新しいフォルダ
        //   ・範囲がフォルダの途中      → 元のフォルダ（前半）／新しいフォルダ（選んだ範囲）／新しいフォルダ（後半）の3つに分ける
        // フォルダごとにシートの新しい行から始まるため、セル番号は変わる。状態への割り当て（開始〜終了のセル）は、
        // 同じ画像を指し続けるよう新しいセル番号へ付け替える。今回の移動で空になったフォルダは残さない。
        private void CreateFolderFromSelection()
        {
            var moving = new List<ImageItem>();
            foreach (ImageFolder folder in folders)
                foreach (ImageItem item in folder.Items)
                    if (selectedImages.Contains(item)) moving.Add(item);
            if (moving.Count == 0) return;

            Dictionary<ImageItem, int> oldCells = ComputeCellNumbers(folders);

            // 最初の選択がある（並び順で最初の）フォルダと、そのフォルダ内の位置。
            int firstFolderIndex = folders.FindIndex(f => f.Items.Any(selectedImages.Contains));
            ImageFolder source = folders[firstFolderIndex];
            int firstItemIndex = source.Items.FindIndex(selectedImages.Contains);

            var head = source.Items.Take(firstItemIndex).ToList();                                             // 選択より前（すべて未選択）
            var tail = source.Items.Skip(firstItemIndex).Where(i => !selectedImages.Contains(i)).ToList();     // 選択より後の未選択

            // ユーザーが作った空のフォルダは消さないよう、いま中身のあるフォルダを覚えておく。
            var hadItems = new HashSet<ImageFolder>(folders.Where(f => f.Items.Count > 0));

            // 選択した画像を、元のフォルダすべてから外す。
            foreach (ImageFolder folder in folders)
                folder.Items.RemoveAll(selectedImages.Contains);

            var target = new ImageFolder { Name = GenerateUniqueFolderName() };
            target.Items.AddRange(moving);

            var order = new List<ImageFolder>(folders);
            int at = firstFolderIndex;
            if (head.Count == 0)
            {
                // 前半が空: 元のフォルダは後半を持ち、新しいフォルダはその手前へ。
                source.Items.Clear();
                source.Items.AddRange(tail);
                order.Insert(at, target);
            }
            else
            {
                source.Items.Clear();
                source.Items.AddRange(head);
                order.Insert(at + 1, target);
                if (tail.Count > 0)
                {
                    folders.Add(target);   // 名前の重複を避けるため、いったん一覧に入れてから次の名前を決める
                    var rest = new ImageFolder { Name = GenerateUniqueFolderName() };
                    folders.Remove(target);
                    rest.Items.AddRange(tail);
                    order.Insert(at + 2, rest);
                }
            }
            order.RemoveAll(f => f.Items.Count == 0 && hadItems.Contains(f));   // 今回の移動で空になったフォルダだけを消す

            folders.Clear();
            folders.AddRange(order);
            RemapCellAssignments(oldCells, ComputeCellNumbers(folders));
            UpdateTree();
            QueuePreviewUpdate();
            CommitUndoableChange();
        }

        // フォルダの構成から「画像 → セル番号」を求める。シートと同じ規則:
        // フォルダごとに新しい行から始まり、1行に「横セル数」ぶんのセルがある（空きのセルにも番号が付く）。
        private Dictionary<ImageItem, int> ComputeCellNumbers(IList<ImageFolder> list)
        {
            return ComputeCellNumbers(list, (int)columnsBox.Value);
        }

        private static Dictionary<ImageItem, int> ComputeCellNumbers(IList<ImageFolder> list, int columns)
        {
            columns = Math.Max(1, columns);
            var map = new Dictionary<ImageItem, int>();
            int next = 1;
            foreach (ImageFolder folder in list)
            {
                for (int i = 0; i < folder.Items.Count; i++) map[folder.Items[i]] = next + i;
                next += (int)Math.Ceiling(folder.Items.Count / (double)columns) * columns;
            }
            return map;
        }

        // 画像の並び・フォルダ分け・追加・削除でセル番号が変わるとき、状態への割り当て（開始〜終了）が同じ画像を指し続けるよう付け替える。
        // 範囲の中にあった最初と最後の画像の、新しいセル番号を使う。画像が1枚もない範囲は、そのままにする。
        private void RemapCellAssignments(Dictionary<ImageItem, int> oldCells, Dictionary<ImageItem, int> newCells)
        {
            Func<AnimationClipSettings, bool> remap = clip =>
            {
                List<KeyValuePair<ImageItem, int>> inside = oldCells
                    .Where(pair => pair.Value >= clip.StartCell && pair.Value <= clip.EndCell && newCells.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Value).ToList();
                if (inside.Count == 0) return false;
                int startCell = newCells[inside.First().Key];
                int endCell = newCells[inside.Last().Key];
                clip.StartCell = Math.Min(startCell, endCell);
                clip.EndCell = Math.Max(startCell, endCell);
                return true;
            };

            foreach (AnimationClipSettings clip in playerClips.Values) remap(clip);
            remap(effectClip);

            // 「デフォルト」の単体再生の開始・終了。
            var standard = new AnimationClipSettings { StartCell = (int)startCellBox.Value, EndCell = (int)endCellBox.Value };
            if (remap(standard))
            {
                startCellBox.Maximum = Math.Max(startCellBox.Maximum, 999);   // 上限は、プレビューを作り直したあとに合わせ直される
                endCellBox.Maximum = Math.Max(endCellBox.Maximum, 999);
                startCellBox.Value = ClampDecimal(standard.StartCell, startCellBox.Minimum, startCellBox.Maximum);
                endCellBox.Value = ClampDecimal(standard.EndCell, endCellBox.Minimum, endCellBox.Maximum);
            }

            RefreshStateTransitionEditors();
            LoadSelectedStateEditor();
            LoadEffectEditor();
        }

        //--------------
        // 割り当て
        //--------------
        private void BeginAssignMode()
        {
            List<int> cells = GetSelectedCellNumbers();
            if (assignMode || cells.Count == 0 || previewWorkspacePage != PreviewWorkspacePage.StateTransitions) return;
            assignFirstCell = cells[0];
            assignLastCell = cells[cells.Count - 1];
            assignMode = true;
            previewInput.Clear();

            assignOverlay = new AssignOverlayForm();
            assignFilter = new AssignInputFilter(IsInsideAssignHole, IsAssignTab, TryAssignAt, EndAssignMode);
            Application.AddMessageFilter(assignFilter);
            assignOverlaySignature = "";
            assignOverlay.SetConstantAlpha(UiMotion.Enabled ? (byte)0 : (byte)255);
            assignOverlay.Show(this);
            RepositionAssignOverlay();
            assignOverlay.FadeTo(255, AssignFadeMilliseconds, null);   // 暗転を0.15秒で入れる
            // 状態リストをスクロールすると、行の位置に合わせて穴も動かす。
            assignTimer = new Timer { Interval = 50 };
            assignTimer.Tick += (s, e) => RepositionAssignOverlay();
            assignTimer.Start();
        }

        internal void EndAssignMode()
        {
            if (!assignMode) return;
            assignMode = false;
            if (assignTimer != null) { assignTimer.Stop(); assignTimer.Dispose(); assignTimer = null; }
            if (assignFilter != null) Application.RemoveMessageFilter(assignFilter);
            assignFilter = null;
            if (assignOverlay != null)
            {
                // 入力の横取りはすぐ外し、見た目の暗転だけ0.15秒で消してから窓を閉じる。
                AssignOverlayForm fading = assignOverlay;
                assignOverlay = null;
                fading.FadeTo(0, AssignFadeMilliseconds, () => { fading.Close(); fading.Dispose(); });
            }
        }

        private const int AssignFadeMilliseconds = 150;

        // 暗くする窓を本体の位置・大きさ・スクロール状態に合わせる（変化があるときだけ描き直す）。
        private void RepositionAssignOverlay()
        {
            if (!assignMode || assignOverlay == null || assignOverlay.IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) { EndAssignMode(); return; }
            Rectangle client = RectangleToScreen(ClientRectangle);
            List<Rectangle> holes = GetAssignHoles();
            assignOverlay.PassThroughRects.Clear();
            assignOverlay.PassThroughRects.AddRange(GetAssignPassRects());

            string signature = client + "|" + string.Join(";", holes.Select(h => h.ToString())) + "|" + Loc.Current;
            if (signature == assignOverlaySignature) return;
            assignOverlaySignature = signature;
            using (Bitmap bitmap = RenderAssignOverlayBitmap(client, holes))
                assignOverlay.ShowBitmap(client, bitmap);
        }

        // 暗い面・角丸の穴と破線の枠・上部の案内（「割り当て中: Esc で解除」）を描いた画像。
        internal Bitmap RenderAssignOverlayBitmap(Rectangle clientScreen, IList<Rectangle> holesScreen)
        {
            var localHoles = holesScreen.Select(h => new Rectangle(h.X - clientScreen.X, h.Y - clientScreen.Y, h.Width, h.Height)).ToList();
            string banner = Loc.T("message.assignBanner");
            using (Font font = UiFont.Create(10.0f, FontStyle.Bold, GraphicsUnit.Point))
            {
                Size text = TextRenderer.MeasureText(banner, font);
                int width = text.Width + 56, height = 36;
                var area = new Rectangle((clientScreen.Width - width) / 2, titleBar.Height + 10, width, height);
                return AssignOverlayForm.RenderBitmap(clientScreen.Size, localHoles, RadiusLg + 2, banner, font, accentColor, Color.White, area);
            }
        }

        // 明るく見せる（割り当て可能な）範囲: 状態の行それぞれ。エフェクト/デフォルトは開始・終了の行。
        private List<Rectangle> GetAssignHoles()
        {
            var holes = new List<Rectangle>();
            if (previewTargetMode == PreviewTargetMode.Player)
            {
                Rectangle viewport = playerTransitionList.RectangleToScreen(playerTransitionList.ClientRectangle);
                foreach (StateRangeEditorControls editor in stateRangeEditors.Values)
                {
                    Panel row = editor.Row;
                    if (row == null || !row.Visible) continue;
                    Rectangle rect = Rectangle.Intersect(row.RectangleToScreen(row.ClientRectangle), viewport);
                    if (rect.Height >= 16 && rect.Width > 16) holes.Add(rect);
                }
            }
            else
            {
                Control area = GetAssignArea();
                if (area != null && area.Visible) holes.Add(area.RectangleToScreen(area.ClientRectangle));
            }
            return holes;
        }

        // 暗く見えていてもクリックを通す範囲: 3つのタブ（別タブで解除）と状態リスト（スクロール）。
        private List<Rectangle> GetAssignPassRects()
        {
            var rects = new List<Rectangle>();
            rects.Add(workspaceTabBar.RectangleToScreen(workspaceTabBar.ClientRectangle));
            Control area = GetAssignArea();
            if (area != null) rects.Add(area.RectangleToScreen(area.ClientRectangle));
            return rects;
        }

        private Control GetAssignArea()
        {
            return previewTargetMode == PreviewTargetMode.Player ? (Control)playerTransitionList
                : previewTargetMode == PreviewTargetMode.Effect ? effectSettingsRow : standardSettingsRow;
        }

        private bool IsInsideAssignHole(Point screenPoint)
        {
            foreach (Rectangle rect in GetAssignPassRects())
                if (rect.Contains(screenPoint)) return true;
            return false;
        }

        private bool IsAssignTab(Point screenPoint)
        {
            return workspaceTabBar.RectangleToScreen(workspaceTabBar.ClientRectangle).Contains(screenPoint);
        }

        // 状態の行（名前〜キー割り当てボタンの範囲）の左クリックで、その状態の開始〜終了へ選択範囲を設定する。
        private bool TryAssignAt(Point screenPoint)
        {
            if (previewTargetMode == PreviewTargetMode.Player)
            {
                foreach (KeyValuePair<PlayerAnimationState, StateRangeEditorControls> pair in stateRangeEditors)
                {
                    Panel row = pair.Value.Row;
                    if (row == null || !row.Visible) continue;
                    if (!playerTransitionList.RectangleToScreen(playerTransitionList.ClientRectangle).Contains(screenPoint)) continue;
                    if (row.RectangleToScreen(row.ClientRectangle).Contains(screenPoint))
                    {
                        AssignRangeToState(pair.Key);
                        EndAssignMode();
                        return true;
                    }
                }
                return false;
            }
            Control area = GetAssignArea();
            if (area != null && area.RectangleToScreen(area.ClientRectangle).Contains(screenPoint))
            {
                AssignRangeToRangeBoxes();
                EndAssignMode();
                return true;
            }
            return false;
        }

        internal void AssignRangeToState(PlayerAnimationState state)
        {
            StateRangeEditorControls editor;
            if (!stateRangeEditors.TryGetValue(state, out editor)) return;
            syncingStateEditor = true;
            editor.Enabled.Checked = true;
            editor.End.Value = ClampDecimal(assignLastCell, editor.End.Minimum, editor.End.Maximum);
            editor.Start.Value = ClampDecimal(assignFirstCell, editor.Start.Minimum, editor.Start.Maximum);
            syncingStateEditor = false;
            SaveStateRangeEditor(state);
        }

        // エフェクト/デフォルトの開始・終了へ設定する。
        private void AssignRangeToRangeBoxes()
        {
            NumericUpDown start = previewTargetMode == PreviewTargetMode.Effect ? effectStartBox : startCellBox;
            NumericUpDown end = previewTargetMode == PreviewTargetMode.Effect ? effectEndBox : endCellBox;
            end.Value = ClampDecimal(assignLastCell, end.Minimum, end.Maximum);
            start.Value = ClampDecimal(assignFirstCell, start.Minimum, start.Maximum);
        }

        //--------------
        // 取り消し/やり直し（選択）
        //--------------
        // 選択中の画像を (フォルダ番号, 位置) の並びで記録する。復元時にフォルダ内の位置で引き直す。
        private List<int[]> CaptureSelectedItems()
        {
            var list = new List<int[]>();
            for (int folderIndex = 0; folderIndex < folders.Count; folderIndex++)
                for (int itemIndex = 0; itemIndex < folders[folderIndex].Items.Count; itemIndex++)
                    if (selectedImages.Contains(folders[folderIndex].Items[itemIndex]))
                        list.Add(new[] { folderIndex, itemIndex });
            return list;
        }

        private void RestoreSelectedItems(IList<int[]> selection)
        {
            selectedImages.Clear();
            foreach (int[] index in selection)
                if (index[0] < folders.Count && index[1] < folders[index[0]].Items.Count)
                    selectedImages.Add(folders[index[0]].Items[index[1]]);
        }

        private static bool SelectionsEqual(IList<int[]> left, IList<int[]> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i][0] != right[i][0] || left[i][1] != right[i][1]) return false;
            return true;
        }
    }
}
