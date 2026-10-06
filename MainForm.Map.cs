//==================================================
// MainForm.Map
// マップ編集画面を既存モード・画像一覧・保存履歴へ接続する。
//==================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // アニメーションのアイコン。設定した FPS でコマを順に描き、押すとそのアニメーションを配置に使う。
    internal sealed class MapAnimationIcon : Control
    {
        private readonly MapAnimation clip;
        private readonly Func<string, Bitmap> imageFor;
        private readonly Timer timer = new Timer { Interval = 30 };
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private int shownFrame = -1;
        private bool hover;
        public bool Selected { get; set; }
        public string ClipId { get { return clip.Id; } }
        public MapAnimationIcon(MapAnimation clip, Func<string, Bitmap> imageFor)
        {
            this.clip = clip; this.imageFor = imageFor;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
            timer.Tick += (s, e) => { if (FrameIndex != shownFrame) Invalidate(); };
            VisibleChanged += (s, e) => { if (Visible) timer.Start(); else timer.Stop(); };
            timer.Start();
        }
        private int FrameIndex { get { return clip.Frames.Count == 0 ? -1 : (int)(clock.Elapsed.TotalSeconds * Math.Max(1, clip.Fps) % clip.Frames.Count); } }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(17, 23, 28));
            shownFrame = FrameIndex;
            Bitmap image = shownFrame < 0 ? null : imageFor(clip.Frames[shownFrame]);
            Rectangle inner = Rectangle.Inflate(ClientRectangle, -4, -4);
            if (image != null)
            {
                float ratio = Math.Min(inner.Width / (float)image.Width, inner.Height / (float)image.Height);
                var r = new RectangleF(inner.X + (inner.Width - image.Width * ratio) / 2, inner.Y + (inner.Height - image.Height * ratio) / 2, image.Width * ratio, image.Height * ratio);
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(image, r, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color border = Selected ? Color.FromArgb(124, 112, 255) : hover ? Color.FromArgb(190, 198, 205) : Color.FromArgb(63, 73, 81);
            using (GraphicsPath path = MainForm.CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 5))
            using (var pen = new Pen(border, Selected ? 2 : 1)) g.DrawPath(pen, path);
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
    // 折り畳み見出し。設定タブの節見出し（左の紫の線・太字・罫線）と同じ見た目に、開閉の矢印を加える。
    internal sealed class MapFoldHeader : Control
    {
        private static readonly Color Accent = Color.FromArgb(84, 73, 255);
        private static readonly Color Divider = Color.FromArgb(63, 73, 81);
        private static readonly Color Hover = Color.FromArgb(31, 40, 47);
        private static readonly Color TextColor = Color.FromArgb(243, 245, 247);
        private static readonly Color MutedText = Color.FromArgb(190, 198, 205);
        private float turn, hoverMix;
        // 見出しの文字の左端（左に「？」を置くときは右へずらす）。
        public int TextLeft { get; set; } = 12;
        public HelpMark Help { get; set; }
        private Timer turnMotion, hoverMotion;
        public MapFoldHeader(string caption, bool expanded)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = caption; turn = expanded ? 1 : 0; TabStop = true; Cursor = Cursors.Hand;
            Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point);
            AccessibleRole = AccessibleRole.OutlineButton; AccessibleName = caption;
        }
        public void SetExpanded(bool expanded, int milliseconds)
        {
            UiMotion.Stop(ref turnMotion); float from = turn, to = expanded ? 1 : 0;
            turnMotion = UiMotion.Animate(milliseconds, t => { turn = from + (to - from) * t; Invalidate(); });
        }
        private void FadeHover(bool on)
        {
            UiMotion.Stop(ref hoverMotion); float from = hoverMix, to = on ? 1 : 0;
            hoverMotion = UiMotion.Animate(120, t => { hoverMix = from + (to - from) * t; Invalidate(); });
        }
        protected override void OnMouseEnter(EventArgs e) { FadeHover(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { FadeHover(false); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color backdrop = RoundedPaint.ResolveBackdrop(Parent);
            g.Clear(backdrop); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hoverMix > 0)
                using (GraphicsPath path = MainForm.CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 6))
                using (var b = new SolidBrush(UiMotion.Mix(backdrop, Hover, hoverMix))) g.FillPath(b, path);
            using (var accent = new SolidBrush(Accent)) g.FillRectangle(accent, 0, 8, 3, Math.Max(1, Height - 16));
            Size text = TextRenderer.MeasureText(Text, Font);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(TextLeft, 0, Math.Max(1, Width - TextLeft - 40), Height), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            // 「？」は見出しの文字の右に置き、罫線はその後ろから引く。
            if (Help != null) Help.Location = new Point(TextLeft + text.Width + 4, (Height - Help.Height) / 2);
            int lineStart = (Help != null ? Help.Right : TextLeft + text.Width) + 10, lineEnd = Width - 36;
            if (lineEnd > lineStart) using (var line = new Pen(Divider)) g.DrawLine(line, lineStart, Height / 2, lineEnd, Height / 2);
            // 閉じているときは右向き、開いているときは下向き。開閉に合わせて回る。
            var state = g.Save();
            g.TranslateTransform(Width - 18, Height / 2f); g.RotateTransform(90 * turn);
            using (var pen = new Pen(UiMotion.Mix(MutedText, TextColor, hoverMix), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(-2.5f, -5), new PointF(2.5f, 0), new PointF(-2.5f, 5) });
            g.Restore(state);
            if (Focused && ShowFocusCues)
                using (GraphicsPath ring = MainForm.CreateRoundedPath(new Rectangle(1, 1, Width - 3, Height - 3), 6))
                using (var pen = new Pen(Color.FromArgb(120, Accent))) g.DrawPath(pen, ring);
        }
        protected override void Dispose(bool disposing) { if (disposing) { UiMotion.Stop(ref turnMotion); UiMotion.Stop(ref hoverMotion); } base.Dispose(disposing); }
    }
    // 見出しを常に残し、内容領域だけを同じイージングで開閉する。
    internal sealed class MapFoldSection : Panel
    {
        private static readonly Color PanelBack = Color.FromArgb(24, 32, 38);
        public readonly FlowLayoutPanel Body;
        public readonly MapFoldHeader Header;
        public bool Expanded { get; private set; }
        private readonly Action<bool> changed;
        private Timer motion;
        private bool arranging;
        public MapFoldSection(string title, bool expanded, Font font, Action<bool> onChanged, string help = null)
        {
            changed = onChanged; Expanded = expanded;
            Width = 400; Height = 38; Margin = new Padding(0, 6, 0, 4); BackColor = PanelBack;
            Header = new MapFoldHeader(title, expanded) { Height = 36 };
            Body = new FlowLayoutPanel { Location = new Point(4, 40), FlowDirection = FlowDirection.TopDown, WrapContents = false, Font = font, BackColor = BackColor };
            Controls.Add(Body); Controls.Add(Header); Header.BringToFront();
            if (help != null)
            {
                // 見出しの文字の右に「？」。押しても開閉しない（別の部品なので見出しのクリックにならない）。
                int size = (int)Math.Round(18 * DeviceDpi / 96.0);
                var mark = new HelpMark { Size = new Size(size, size), HelpText = help };
                Header.Controls.Add(mark); Header.Help = mark;
            }
            Header.Click += (s, e) => SetExpanded(!Expanded);
            SizeChanged += (s, e) => ArrangeRows();
            RefreshHeight();
        }
        private int ContentHeight { get { return Body.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical); } }
        private void ArrangeRows()
        {
            if (arranging) return; arranging = true;
            try { Header.Width = Width; Body.Width = Math.Max(1, Width - 8); foreach (Control row in Body.Controls) row.Width = Body.Width; Body.Height = ContentHeight; }
            finally { arranging = false; }
        }
        private void Describe()
        { Header.AccessibleDescription = Loc.T(Expanded ? "access.groupExpanded" : "access.groupCollapsed"); }
        public void RefreshHeight()
        {
            ArrangeRows(); Body.Visible = Expanded; Height = Expanded ? 42 + ContentHeight : 36; Describe();
        }
        public void SetExpanded(bool value)
        {
            UiMotion.Stop(ref motion); Expanded = value; if (changed != null) changed(value);
            Header.SetExpanded(value, 160); Describe();
            ArrangeRows(); int from = Height, target = value ? 42 + ContentHeight : 36;
            // 閉じ始めに操作対象から外し、見出しへキーボードフォーカスを戻す。
            if (!value && Body.ContainsFocus) Header.Focus();
            Body.Enabled = value; Body.Visible = true;
            motion = UiMotion.Animate(160, t => { Height = (int)Math.Round(from + (target - from) * t); }, () => { Body.Visible = Expanded; });
        }
        protected override void Dispose(bool disposing) { if (disposing) UiMotion.Stop(ref motion); base.Dispose(disposing); }
    }
    public sealed partial class MainForm
    {
        private MapDocument mapDocument = new MapDocument();
        private MapCanvas mapCanvas, mapPalette;
        private FlowLayoutPanel mapSettings;
        private readonly Dictionary<string, Bitmap> mapImages = new Dictionary<string, Bitmap>();
        private bool mapAnimationOpen = true, mapNormalizationOpen = true;
        private bool mapUiReady;
        // マップ表示用の画像に適用中の加工（黒透過・カラー調整）。変わったら読み直す。
        private bool mapImageBlack;
        private SpriteColorBlendMode mapImageBlend;
        private int mapImageColor = Color.White.ToArgb(), mapImageStrength;

        private void InitializeMapSupport()
        {
            mapCanvas = new MapCanvas { Dock = DockStyle.Fill, Font = Font, Document = mapDocument, ImageFor = MapImage, Visible = false };
            mapPalette = new MapCanvas { Dock = DockStyle.Fill, Font = Font, Document = mapDocument, ImageFor = MapImage, Palette = true, Visible = false, MenuRenderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable()) };
            animCanvas.Parent.Controls.Add(mapCanvas); mapCanvas.BringToFront();
            sheetCanvas.Parent.Controls.Add(mapPalette); mapPalette.BringToFront();
            mapSettings = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = darkPanel, Visible = false, Padding = new Padding(10, 8, 8, 8) };
            stateTransitionPage.Controls.Add(mapSettings); mapSettings.BringToFront();
            // モード選択の見出しは既存の共通コントロールを手前に残す。
            Control header = previewModeEnumHost.Parent.Parent;
            header.SendToBack();
            // アニメーションのコマに使われているチップは、どれを押してもそのアニメーションを配置する。
            mapPalette.AssetSelected += id =>
            {
                MapAnimation clip = mapDocument.AnimationOf(id);
                mapCanvas.Selected = clip != null ? clip.Id : id; mapCanvas.SelectedAnimation = clip != null; mapCanvas.Eraser = false;
                mapPalette.Selected = id; mapCanvas.Invalidate(); mapPalette.Invalidate(); RefreshAnimationIcons();
            };
            mapPalette.OrderChanged += () => { MapChanged(); RefreshMapHeaders(); };
            mapPalette.SelectionMenu += ShowMapSelectionMenu;
            mapPalette.EmptyMenu += ShowMapEmptyMenu;
            mapPalette.DeleteRequested += id => DeleteMapChips(new[] { id });
            // シートを押している間（長押し・持ち上げ・範囲の移動）は、操作の説明の札を出さない（動かすチップに重なるため）。
            mapPalette.PressStarted += () => { toolTip.Hide(mapPalette); toolTip.SetToolTip(mapPalette, null); };
            mapPalette.PressEnded += () => toolTip.SetToolTip(mapPalette, Loc.T("tooltip.mapSheet"));
            mapCanvas.LayerMenu += ShowMapLayerMenu;
            mapPalette.BasisAssigned += id => { MapAsset asset = mapDocument.Asset(id); if (asset == null || !asset.Available) return; mapDocument.SetBasis(asset); MapChanged(true); };
            mapCanvas.LayersChanged += () => CommitUndoNow();
            BindAction(() => toolTip.SetToolTip(mapPalette, Loc.T("tooltip.mapSheet")));
            mapCanvas.StrokeStarted += () => { if (commitScheduled) CommitUndoNow(); };
            mapCanvas.StrokeFinished += () => { CommitUndoNow(); };
            mapSettings.SizeChanged += (s, e) => ResizeMapRows();
            mapCanvas.ViewChanged += RefreshMapHeaders;
            mapPalette.ViewChanged += RefreshMapHeaders;
            BindAction(() => toolTip.SetToolTip(mapCanvas, Loc.T("tooltip.mapCanvas")));
            mapCanvas.Hints = toolTip;
            mapPalette.AlignSheetTo4 = align4CheckBox.Checked;
            mapUiReady = true; SyncMapAssets(); RefreshMapSettings(); ApplyMapMode();
            FormClosed += (s, e) => { foreach (Bitmap b in mapImages.Values) if (b != null) b.Dispose(); mapImages.Clear(); };
            blackTransparencyCheckBox.CheckedChanged += (s, e) => RefreshMapImages();
            // 背景と「シートの外側に縦横の番号を表示」もマップチップに効かせる。
            axisNumbersCheckBox.CheckedChanged += (s, e) => { mapPalette.ShowAxisNumbers = axisNumbersCheckBox.Checked; if (previewTargetMode == PreviewTargetMode.Map) mapPalette.Fit(true); };
            mapPalette.ShowAxisNumbers = axisNumbersCheckBox.Checked;
            ApplyPreviewBackgroundPalette();
            ApplyCoordinateSettings();
            RefreshMapImages();
        }
        private Bitmap MapImage(string id)
        {
            MapAsset a = mapDocument.Asset(id);
            if (a == null || !a.Available || string.IsNullOrEmpty(a.Path)) return null;
            // 描くたびに呼ばれるので、読めた画像も「無い・読めない」という結果も覚えておき、ファイルを毎回確かめない
            // （チップ×毎フレームでディスクを見に行くと描画が詰まる）。覚えた「無い」は画像一覧の同期で捨てる。
            Bitmap image;
            if (mapImages.TryGetValue(a.Path, out image)) return image;
            image = null;
            if (File.Exists(a.Path))
            {
                // シートと同じ加工（黒透過・カラー調整）を通す。縮小はマップの寸法を保つため掛けない。
                try { image = SpriteImagePipeline.LoadOne(a.Path, 1, mapImageBlack, mapImageBlend, Color.FromArgb(mapImageColor), mapImageStrength, System.Threading.CancellationToken.None); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { image = null; }
            }
            mapImages[a.Path] = image;
            return image;
        }
        // 加工の設定が変わったら、読み込み済みの画像を捨てて描き直す（設定欄のサムネイルも作り直す）。
        private void RefreshMapImages()
        {
            if (!mapUiReady) return;
            int color = spriteAdjustmentColor.ToArgb();
            bool black = blackTransparencyCheckBox.Checked;
            if (black == mapImageBlack && colorBlendMode == mapImageBlend && color == mapImageColor && spriteAdjustmentStrength == mapImageStrength) return;
            mapImageBlack = black; mapImageBlend = colorBlendMode; mapImageColor = color; mapImageStrength = spriteAdjustmentStrength;
            var old = mapImages.Values.ToList(); mapImages.Clear();
            RefreshMapSettings();   // サムネイルが古い画像を指さないよう、先に作り直してから捨てる。
            foreach (Bitmap b in old) if (b != null) b.Dispose();
            mapCanvas.Invalidate(); mapPalette.Invalidate();
        }
        private void SyncMapAssets()
        {
            if (!mapUiReady) return;
            foreach (MapAsset a in mapDocument.Assets) a.Available = false;
            foreach (var entry in cellItems.OrderBy(p => p.Key))
            {
                string path = entry.Value.Path;
                MapAsset asset = mapDocument.Assets.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
                if (asset == null)
                {
                    try { using (Image image = Image.FromFile(path)) asset = new MapAsset { Path = path, Width = image.Width, Height = image.Height }; }
                    catch { continue; }
                    mapDocument.Assets.Add(asset);
                }
                asset.Cell = entry.Key; asset.Available = File.Exists(path);
            }
            if (string.IsNullOrEmpty(mapDocument.BasisId)) mapDocument.SetBasis(mapDocument.Assets.FirstOrDefault(a => a.Available));
            var livePaths = new HashSet<string>(mapDocument.Assets.Where(a => a.Available).Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            // 使われなくなった画像と、前に「無い・読めない」だった記録を捨てる（ファイルが戻っていれば次に読み直す）。
            foreach (string path in mapImages.Keys.Where(p => !livePaths.Contains(p) || mapImages[p] == null).ToList()) { if (mapImages[path] != null) mapImages[path].Dispose(); mapImages.Remove(path); }
            mapCanvas.Document = mapDocument; mapPalette.Document = mapDocument;
            if (string.IsNullOrEmpty(mapCanvas.Selected)) mapCanvas.Selected = mapDocument.BasisId;
            mapPalette.PaletteColumns = (int)columnsBox.Value;
            // 横セル数は最初の並びにだけ使う。並びを確定して保存し、以後の変更では動かさない。
            mapDocument.FixLayout((int)columnsBox.Value);
            mapPalette.RefreshPacking(); mapCanvas.Invalidate(); RefreshMapSettings(); RefreshMapHeaders();
            // この整理は操作ではないので、元に戻すの1回分にしない（2026-10-06）。
            // まだ記録していない操作があるときは、その操作と一緒に記録されるので取り込まない。
            if (!restoringState && !commitScheduled && undoManager != null) undoManager.Resettle();
        }
        private void ApplyMapMode()
        {
            if (!mapUiReady) return;
            bool active = previewTargetMode == PreviewTargetMode.Map;
            mapCanvas.Visible = active; mapPalette.Visible = active; sheetCanvas.Visible = !active; animCanvas.Visible = !active;
            UpdateFillEmptyCellsButton();
            mapSettings.Visible = active && previewWorkspacePage == PreviewWorkspacePage.StateTransitions;
            previewFitButton.Visible = active;
            if (active) { animationTimer.Stop(); if (playbackBarPanel != null) playbackBarPanel.Visible = false; mapCanvas.BringToFront(); mapPalette.BringToFront(); RefreshMapHeaders(); }
            else UpdateZoomLabel();
            if (!active && !lastSheetSize.IsEmpty)
            { int columns = (int)columnsBox.Value; sheetCellsChip.Text = Loc.T("chip.cells", columns, (int)Math.Ceiling(GetAllItems().Count / (double)columns)); sheetSizeChip.Text = lastSheetSize.Width + " × " + lastSheetSize.Height + " px"; }
        }
        private void RefreshMapHeaders()
        {
            if (!mapUiReady || previewTargetMode != PreviewTargetMode.Map) return;
            animInfoLabel.Text = Loc.T("map.gridInfo", MapDocument.Extent, MapDocument.Extent, 4);
            sheetCellsChip.Text = Loc.T("map.chipCount", mapDocument.Assets.Count(a => a.Available));
            var packed = mapDocument.Pack((int)columnsBox.Value);
            sheetSizeChip.Text = SheetSizeRule.Round(packed.Values.Select(r => r.Right).DefaultIfEmpty(0).Max(), align4CheckBox.Checked) + " × " + SheetSizeRule.Round(packed.Values.Select(r => r.Bottom).DefaultIfEmpty(0).Max(), align4CheckBox.Checked) + " px";
            sheetZoomChip.Text = Math.Round(mapPalette.Zoom * 100) + "%"; animZoomChip.Text = Math.Round(mapCanvas.Zoom * 100) + "%";
            zoomHintLabel.Text = Loc.T("hint.mapControls");
        }
        private void MapChanged(bool repack = false)
        {
            if (repack) mapPalette.RefreshPacking();
            mapCanvas.Invalidate(); mapPalette.Invalidate(); RefreshMapSettings(); CommitUndoNow();
        }
        // シート上のセル番号（並びの順）から画像を探す。
        private MapAsset MapAssetAtNumber(int number)
        {
            string id = mapPalette.Numbers.Where(p => p.Value == number).Select(p => p.Key).FirstOrDefault();
            return id == null ? null : mapDocument.Asset(id);
        }
        //--------------
        // シートの範囲選択からの割り当て
        //--------------
        private const string MapAddAnimationTag = "map-add-animation";
        private readonly ContextMenuStrip mapSelectionMenu = new ContextMenuStrip();
        private void ShowMapSelectionMenu(Point at)
        {
            if (assignMode || mapPalette.SelectedSet.Count == 0) return;
            mapSelectionMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            mapSelectionMenu.ShowImageMargin = false;
            mapSelectionMenu.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            mapSelectionMenu.BackColor = darkPanel; mapSelectionMenu.ForeColor = lightText;
            mapSelectionMenu.Items.Clear();
            if (previewWorkspacePage == PreviewWorkspacePage.StateTransitions)
                mapSelectionMenu.Items.Add(CreateSheetMenuItem("menu.sheetAssign", BeginAssignMode));
            mapSelectionMenu.Items.Add(CreateSheetMenuItem("menu.mapNewAnimation", () => AssignSelectionToAnimation(null)));
            // 選んだチップを削除する（Delete／Backspace と同じ。確認ダイアログあり、元に戻すで戻せる）。
            mapSelectionMenu.Items.Add(new ToolStripSeparator());
            mapSelectionMenu.Items.Add(CreateSheetMenuItem("menu.sheetDelete", () => DeleteMapChips(mapPalette.SelectedInOrder())));
            mapSelectionMenu.Show(mapPalette, at);
        }
        // シートの何もない所の右クリック。通常のシートと同じく「プロジェクトをリセット」を出す。
        private void ShowMapEmptyMenu(Point at)
        {
            if (assignMode) return;
            PrepareMapMenu();
            mapSelectionMenu.Items.Add(CreateSheetMenuItem("menu.projectReset", ResetProjectContents));
            mapSelectionMenu.Show(mapPalette, at);
        }
        // レイヤーの行の右クリック。そのレイヤーのチップをすべて消せる（元に戻すで戻せる）。
        private void ShowMapLayerMenu(int layer, Point at)
        {
            if (assignMode) return;
            PrepareMapMenu();
            var clear = new ToolStripMenuItem(Loc.T("menu.clearLayer", layer + 1)) { ForeColor = lightText, Enabled = mapDocument.Tiles.Any(t => t.Layer == layer) };
            clear.Click += (s, e) => { if (mapCanvas.ClearLayer(layer)) { RefreshMapHeaders(); statusLabel.Text = Loc.T("message.layerCleared", layer + 1); } };
            mapSelectionMenu.Items.Add(clear);
            mapSelectionMenu.Show(mapCanvas, at);
        }
        private void PrepareMapMenu()
        {
            mapSelectionMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            mapSelectionMenu.ShowImageMargin = false;
            mapSelectionMenu.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            mapSelectionMenu.BackColor = darkPanel; mapSelectionMenu.ForeColor = lightText;
            mapSelectionMenu.Items.Clear();
        }
        // マップチップのシートのチップを削除する（その画像をプロジェクトから外す。確認ダイアログを出す。元に戻すで戻せる）。
        private void DeleteMapChips(IEnumerable<string> ids)
        {
            List<ImageItem> all = GetAllItems();
            List<ImageItem> items = ids.Select(id => mapDocument.Asset(id)).Where(a => a != null)
                .Select(a => all.FirstOrDefault(i => string.Equals(i.Path, a.Path, StringComparison.OrdinalIgnoreCase)))
                .Where(i => i != null).Distinct().ToList();
            if (items.Count == 0) return;
            List<ImageItem> keepImages = selectedImages.ToList();
            List<ImageFolder> keepFolders = selectedFolders.ToList();
            selectedFolders.Clear(); selectedImages.Clear();
            foreach (ImageItem item in items) selectedImages.Add(item);
            int before = all.Count;
            RemoveSelectedNode();
            if (GetAllItems().Count == before)
            {
                // 取り消したときは、元の選択に戻す。
                selectedImages.Clear(); selectedFolders.Clear();
                foreach (ImageItem item in keepImages) selectedImages.Add(item);
                foreach (ImageFolder folder in keepFolders) selectedFolders.Add(folder);
                RefreshSelectionViews();
                return;
            }
            mapPalette.ClearSelection();
        }
        // 選んだ範囲（番号の順）を、アニメーションのコマにする。clip が null なら新しいアニメーションを作る。
        internal void AssignSelectionToAnimation(MapAnimation clip)
        {
            List<string> frames = mapPalette.SelectedInOrder();
            if (frames.Count == 0) return;
            // コマは横一列に並べるので、幅の合計が上限を超えるものは作らない（Codex 監査 2026-10-06）。
            if (!mapDocument.FitsInRow(frames)) { ShowDarkNotice(Loc.T("dialog.cannotChange"), Loc.T("error.animationTooWide", MapDocument.MaxRowWidth)); return; }
            if (clip == null) { clip = new MapAnimation { Fps = (int)fpsBox.Value }; mapDocument.Animations.Add(clip); }
            clip.Frames = frames;
            mapCanvas.Selected = clip.Id; mapCanvas.SelectedAnimation = true; mapCanvas.Eraser = false;
            mapPalette.ClearSelection();
            MapChanged(true);
        }
        // 割り当て中に明るくする行（アニメーションの行と「＋ 追加」の行）。
        private IEnumerable<Control> MapAssignRows()
        {
            foreach (MapFoldSection section in mapSettings.Controls.OfType<MapFoldSection>())
                foreach (Control row in section.Body.Controls)
                    if ((row.Tag is MapAnimation || MapAddAnimationTag.Equals(row.Tag)) && row.Visible && section.Expanded) yield return row;
        }

        private void RefreshAnimationIcons()
        {
            var stack = new Stack<Control>(); stack.Push(mapSettings);
            while (stack.Count > 0)
            {
                Control c = stack.Pop();
                var icon = c as MapAnimationIcon;
                if (icon != null) { icon.Selected = mapCanvas.SelectedAnimation && mapCanvas.Selected == icon.ClipId; icon.Invalidate(); }
                foreach (Control child in c.Controls) stack.Push(child);
            }
        }
        private FlowLayoutPanel MapRow()
        {
            return new FlowLayoutPanel { Width = Math.Max(320, mapSettings.ClientSize.Width - 28), Height = ScaleDpi(40), WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        }
        private Label MapLabel(string text, int width)
        { return new Label { Text = text, ForeColor = lightText, Width = MapTextWidth(text, width, 6), Height = ScaleDpi(34), Margin = new Padding(3, 3, 3, 3), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        // 指定幅は最小値として扱い、拡大率や長い文言でも文字が切れない幅にする。
        private int MapTextWidth(string text, int width, int padding)
        { return Math.Max(ScaleDpi(width), TextRenderer.MeasureText(text, Font).Width + ScaleDpi(padding)); }
        private Button MapButton(string text, int width, Action action)
        {
            var button = new RoundedButton { Text = text, Width = MapTextWidth(text, width, 20), Height = ScaleDpi(34), FlatStyle = FlatStyle.Flat, BackColor = inputBack, ForeColor = lightText, Margin = new Padding(3), CornerRadius = RadiusMd, BorderColor = inputBorder, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = inputBorder; button.Click += (s, e) => action(); return button;
        }
        private NumericUpDown MapNumber(int value, int min, int max)
        { var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)) }; ApplyInputStyle(n); return n; }
        // 数値欄は他の画面と同じ角丸の入力欄にする（標準の NumericUpDown は白く浮くため表示しない）。
        private Panel MapNumberHost(NumericUpDown input, int width)
        { Panel host = CreateInputHost(input, width); host.Height = ScaleDpi(34); host.Margin = new Padding(3); return host; }
        private PictureBox MapThumbnail(MapAsset a, Action click)
        {
            var box = new PictureBox { Width = ScaleDpi(34), Height = ScaleDpi(34), SizeMode = PictureBoxSizeMode.Zoom, BackColor = inputBack, Image = a == null ? null : MapImage(a.Id), Margin = new Padding(3), Padding = new Padding(3), Cursor = Cursors.Hand };
            box.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
                if (box.Image == null) { int m = box.Width / 4, n = box.Width - m * 2; using (var pen = new Pen(mutedText)) { e.Graphics.DrawRectangle(pen, m, m, n, n); e.Graphics.DrawLine(pen, m, m, m + n, m + n); } }
            };
            box.Click += (s, e) => click(); return box;
        }
        private void ResizeMapRows() { if (mapSettings != null) foreach (Control c in mapSettings.Controls) c.Width = Math.Max(320, mapSettings.ClientSize.Width - 28); }
        private void RefreshMapSettings()
        {
            if (!mapUiReady) return;
            Point scroll = mapSettings.AutoScrollPosition;
            mapSettings.SuspendLayout();
            foreach (Control c in mapSettings.Controls.Cast<Control>().ToArray()) { mapSettings.Controls.Remove(c); c.Dispose(); }
            var basisRow = MapRow(); MapAsset basis = mapDocument.Asset(mapDocument.BasisId);
            basisRow.Controls.Add(MapThumbnail(basis, () => { if (basis != null) mapPalette.FocusAsset(basis.Id); }));
            basisRow.Controls.Add(MapLabel(Loc.T("map.basisCell"), 70));
            Dictionary<string, int> numbers = mapPalette.Numbers;
            int basisNumber; if (basis == null || !numbers.TryGetValue(basis.Id, out basisNumber)) basisNumber = 0;
            var basisBox = MapNumber(basisNumber, 0, Math.Max(1, numbers.Count));
            basisRow.Controls.Add(MapNumberHost(basisBox, 76));
            basisRow.Controls.Add(MapButton(Loc.T("button.apply"), 52, () => { MapAsset a = MapAssetAtNumber((int)basisBox.Value); if (a == null) return; mapDocument.SetBasis(a); MapChanged(true); }));
            basisRow.Controls.Add(MapLabel(mapDocument.CellWidth + " × " + mapDocument.CellHeight + " px", 110)); mapSettings.Controls.Add(basisRow);
            var animationSection = new MapFoldSection(Loc.T("section.animationChips"), mapAnimationOpen, Font, value => mapAnimationOpen = value, Loc.T("help.animationChips"));
            mapSettings.Controls.Add(animationSection);
            var addAnimation = MapRow(); addAnimation.Tag = MapAddAnimationTag; addAnimation.Controls.Add(MapButton(Loc.T("button.addAnimation"), 100, () => EditMapAnimation(null)));
            animationSection.Body.Controls.Add(addAnimation);
            for (int n = 0; n < mapDocument.Animations.Count; n++)
            {
                MapAnimation clip = mapDocument.Animations[n]; var row = MapRow(); row.Tag = clip;
                // 名前はなくし、動くアイコンで見分ける。押すと配置に使う。
                var icon = new MapAnimationIcon(clip, MapImage) { Width = ScaleDpi(34), Height = ScaleDpi(34), Margin = new Padding(3), AccessibleName = Loc.T("access.animationIcon", n + 1), Selected = mapCanvas.SelectedAnimation && mapCanvas.Selected == clip.Id };
                icon.Click += (s, e) => { mapCanvas.Selected = clip.Id; mapCanvas.SelectedAnimation = true; mapCanvas.Eraser = false; mapCanvas.Invalidate(); RefreshAnimationIcons(); };
                toolTip.SetToolTip(icon, Loc.T("tooltip.animationIcon"));
                row.Controls.Add(icon);
                // FPS は基準セルと同じ入力欄でその場で変える。
                var fps = MapNumber(clip.Fps, 1, 60);
                fps.ValueChanged += (s, e) => { clip.Fps = (int)fps.Value; mapCanvas.Invalidate(); CommitUndoNow(); };
                row.Controls.Add(MapNumberHost(fps, 76));
                row.Controls.Add(MapLabel("FPS", 40));
                row.Controls.Add(MapButton(Loc.T("button.edit"), 55, () => EditMapAnimation(clip)));
                row.Controls.Add(MapButton("×", 34, () => { mapDocument.Animations.Remove(clip); MapChanged(true); }));
                animationSection.Body.Controls.Add(row);
            }
            var normalizationSection = new MapFoldSection(Loc.T("section.normalize"), mapNormalizationOpen, Font, value => mapNormalizationOpen = value, Loc.T("help.normalize"));
            mapSettings.Controls.Add(normalizationSection);
            var all = MapRow(); all.Controls.Add(MapButton(Loc.T("button.ignoreAll"), 125, () => { foreach (MapAsset a in mapDocument.Assets.Where(mapDocument.NeedsNormalization)) a.Ignored = true; MapChanged(); }));
            normalizationSection.Body.Controls.Add(all);
            foreach (MapAsset a in mapDocument.Assets.Where(mapDocument.NeedsNormalization)) AddMapNormalizationRow(a, false, normalizationSection.Body);
            var title = MapLabel(Loc.T("map.processedTitle"), 330); title.ForeColor = mutedText; title.Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point); title.Height = ScaleDpi(26); normalizationSection.Body.Controls.Add(title);
            foreach (MapAsset a in mapDocument.Assets.Where(a => a.Available && (a.Ignored || a.CorrectedWidth > 0)).OrderBy(a => a.Ignored)) AddMapNormalizationRow(a, true, normalizationSection.Body);
            ResizeMapRows(); animationSection.RefreshHeight(); normalizationSection.RefreshHeight();
            mapSettings.ResumeLayout(); mapSettings.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        }
        private void AddMapNormalizationRow(MapAsset asset, bool processed, Control parent)
        {
            var row = MapRow(); row.Controls.Add(MapThumbnail(asset, () => mapPalette.FocusAsset(asset.Id)));
            int number; mapPalette.Numbers.TryGetValue(asset.Id, out number);
            var label = MapLabel("#" + number + "  " + Loc.T(processed ? asset.Ignored ? "map.state.ignored" : "map.state.corrected" : "map.state.pending"), 154);
            label.ForeColor = !processed ? Color.Salmon : asset.Ignored ? Color.FromArgb(230, 191, 112) : Color.FromArgb(137, 212, 179);
            label.Click += (s, e) => mapPalette.FocusAsset(asset.Id); row.Controls.Add(label);
            if (!processed)
            {
                row.Controls.Add(MapButton("✓", 42, () => { mapDocument.Normalize(asset); MapChanged(true); }));
                row.Controls.Add(MapButton("×", 42, () => { asset.Ignored = true; MapChanged(); }));
            }
            else
            {
                Action clear = () => { asset.CorrectedWidth = 0; asset.CorrectedHeight = 0; asset.Ignored = false; MapChanged(true); };
                row.Controls.Add(MapButton(Loc.T("menu.undo"), 92, clear));
                var remove = MapButton("×", 34, clear); toolTip.SetToolTip(remove, Loc.T("tooltip.removeNormalization")); row.Controls.Add(remove);
            }
            parent.Controls.Add(row);
        }
        private void EditMapAnimation(MapAnimation existing)
        {
            using (var dialog = new Form { Text = Loc.T("section.animationChips"), Width = 460, Height = 210, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = darkPanel, ForeColor = lightText, Font = Font })
            {
                var caption = MapLabel(Loc.T("dialog.animationFrames"), 410); caption.Location = new Point(20, 20);
                var frames = new TextBox { Text = existing == null ? "" : string.Join(",", existing.Frames.Where(mapPalette.Numbers.ContainsKey).Select(id => mapPalette.Numbers[id])), Bounds = new Rectangle(20, 60, 400, 28) }; ApplyInputStyle(frames);
                var ok = MapButton(Loc.T("button.save"), 95, () =>
                {
                    var ids = new List<string>();
                    foreach (string token in frames.Text.Split(new[] { ',', '、', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    { int cell; MapAsset a; if (!int.TryParse(token, out cell) || (a = MapAssetAtNumber(cell)) == null) { MessageBox.Show(dialog, Loc.T("message.cellNumberMissing"), Loc.T("dialog.cellNumbersTitle")); return; } ids.Add(a.Id); }
                    if (ids.Count == 0 || ids.Count > 256) { MessageBox.Show(dialog, Loc.T("message.frameCountRange"), Loc.T("dialog.cellNumbersTitle")); return; }
                    if (!mapDocument.FitsInRow(ids)) { MessageBox.Show(dialog, Loc.T("error.animationTooWide", MapDocument.MaxRowWidth), Loc.T("dialog.cellNumbersTitle")); return; }
                    MapAnimation clip = existing ?? new MapAnimation { Fps = (int)fpsBox.Value }; clip.Frames = ids;
                    if (existing == null) mapDocument.Animations.Add(clip); mapCanvas.Selected = clip.Id; mapCanvas.SelectedAnimation = true; mapCanvas.Eraser = false; dialog.DialogResult = DialogResult.OK;
                }); ok.Location = new Point(325, 120);
                // 決定ボタンは他のダイアログと同じく強調色にする。
                ((RoundedButton)ok).BackColor = accentColor; ((RoundedButton)ok).BorderColor = accentColor; ok.ForeColor = Color.White;
                dialog.Controls.AddRange(new Control[] { frames, caption, ok }); DialogMotion.Attach(dialog, this);
                if (dialog.ShowDialog(this) == DialogResult.OK) MapChanged(true);
            }
        }
        private string CaptureMap() { return mapDocument.ToJson(); }
        private void RestoreMap(string json)
        {
            mapDocument = MapDocument.FromJson(json);
            if (!mapUiReady) return;
            mapCanvas.Document = mapDocument; mapPalette.Document = mapDocument;
            // 配置に使うチップ（ツールの状態も）は元に戻すの対象ではない。戻したマップにも残っていれば、そのまま使い続ける
            // （置いたチップを戻すと、選んだ配置チップまで戻っていた。2026-10-06 ユーザー指摘）。
            // プロジェクトを開いた・リセットしたときなど、残っていなければ基準セルにする。
            bool keep = mapCanvas.SelectedAnimation
                ? mapDocument.Animations.Any(a => a.Id == mapCanvas.Selected)
                : !string.IsNullOrEmpty(mapCanvas.Selected) && mapDocument.Asset(mapCanvas.Selected) != null;
            if (!keep) { mapCanvas.Selected = mapDocument.BasisId; mapCanvas.SelectedAnimation = false; mapCanvas.Eraser = false; }
            if (!string.IsNullOrEmpty(mapPalette.Selected) && mapDocument.Asset(mapPalette.Selected) == null) mapPalette.Selected = mapCanvas.SelectedAnimation ? "" : mapCanvas.Selected;
        }
        private string MapAssetId(string path)
        { MapAsset a = mapDocument.Assets.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)); return a == null ? "" : a.Id; }
        private static string RestoreMapPaths(ProjectDocument document)
        {
            MapDocument map = MapDocument.FromJson(document.MapJson);
            foreach (MapAsset a in map.Assets) { a.Path = ""; a.Available = false; }
            foreach (ProjectImage image in document.Folders.SelectMany(f => f.Images))
            { MapAsset a = map.Asset(image.MapId); if (a != null) { a.Path = image.Path; a.Available = File.Exists(image.Path); } }
            return map.ToJson();
        }
    }
}
