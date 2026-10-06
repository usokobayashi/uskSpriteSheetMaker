//==================================================
// MapCanvas
// マップと詰め合わせシートの表示、入力、短い配置・ズーム演出を担当する。
// 演出（チップの切り替え・レイヤー枠の移動・表示切替・長押しの進み具合など）は
// 変化した時刻だけを覚え、描画のたびに経過時間から形を決める。
//==================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // ツール（右上の配置チップを右クリックすると有効になる。既定は削除）。
    internal enum MapTool { Erase, Move, RotateClockwise, RotateCounterClockwise, Flip, FlipVertical }

    internal sealed class MapCanvas : Control
    {
        // 既存画面（MainForm）と同じ配色。
        private static readonly Color Accent = Color.FromArgb(84, 73, 255);
        private static readonly Color AccentLine = Color.FromArgb(124, 112, 255);
        private static readonly Color Danger = Color.FromArgb(255, 122, 130);
        private static readonly Color PanelBack = Color.FromArgb(31, 40, 47);
        private static readonly Color PanelHover = Color.FromArgb(38, 48, 57);
        private static readonly Color Divider = Color.FromArgb(63, 73, 81);
        private static readonly Color TextColor = Color.FromArgb(243, 245, 247);
        private static readonly Color MutedText = Color.FromArgb(190, 198, 205);
        private static readonly Color DisabledText = Color.FromArgb(104, 114, 122);
        private const double NoMotion = -1e9;
        private const int AxisMarginLeft = 34, AxisMarginTop = 22;
        private const double HoldSeconds = .55;

        public MapDocument Document;
        public Func<string, Bitmap> ImageFor;
        public ToolStripRenderer MenuRenderer;
        public bool Palette;
        public int PaletteColumns = 8;
        public event Action ViewChanged;
        public string Selected = "";
        public bool SelectedAnimation;
        public bool Eraser;   // ツールが有効（プレビュー下部にツールバーを出す）
        public MapTool ActiveTool = MapTool.Erase;
        public ToolTip Hints;   // ツールバーのアイコンの説明に使う（MainForm と同じヒント）
        public event Action<string> AssetSelected;
        public event Action StrokeStarted;
        public event Action StrokeFinished;
        public event Action BrushChanged;
        public event Action<string> BasisAssigned;
        public event Action LayersChanged;
        public event Action OrderChanged;
        // シートの範囲選択（Shift＋左クリック／ドラッグ）。選んだチップの上の右クリックでメニューを出す。
        public readonly HashSet<string> SelectedSet = new HashSet<string>();
        public event Action SelectionChanged;
        public event Action<Point> SelectionMenu;
        public event Action<int, Point> LayerMenu;   // レイヤーの行の右クリック（レイヤー番号 0〜3）
        public event Action<Point> EmptyMenu;        // シートの何もない所の右クリック
        public event Action<string> DeleteRequested; // 右長押しのメニューの「削除」
        public event Action PressStarted, PressEnded; // シートを押している間（説明の札を出さないため）
        private string selectAnchor, selectPressId, selectionMoveId;   // selectionMoveId: 選択中のチップを Shift＋左で押した（動かせば選択をまとめて移動）
        private bool selecting, leftMoved, selectDragged;
        private PointF selectPressWorld;          // Shift＋左ドラッグを始めた所（シートの座標）
        private RectangleF selectBox = RectangleF.Empty;   // ドラッグで囲んでいる四角（表示用）
        private Point leftPress;
        internal Func<Keys> ModifierState = () => Control.ModifierKeys;   // キーの押し下げ状態（テストで差し替える）
        private readonly Timer holdTimer = new Timer { Interval = (int)(HoldSeconds * 1000) };
        private string heldAsset;
        private Point holdPoint;
        private double holdAt;
        private ContextMenuStrip basisMenu;
        private Dictionary<string, Rectangle> packing = new Dictionary<string, Rectangle>();
        private Dictionary<string, int> numbers = new Dictionary<string, int>();
        // 背景（設定タブの「背景」と同じ組み合わせ）。透明な部分は市松模様で見せる。
        private Color checkerA = Color.FromArgb(47, 47, 47), checkerB = Color.FromArgb(59, 59, 59);
        private TextureBrush checker;
        private bool showAxisNumbers;
        public bool ShowAxisNumbers { get { return showAxisNumbers; } set { if (showAxisNumbers == value) return; showAxisNumbers = value; Invalidate(); } }
        public Dictionary<string, int> Numbers { get { return numbers; } }
        // カーソルを置いたチップの位置の札（シートのときだけ）。
        private bool showCoordinates;
        public bool ShowCoordinates { get { return showCoordinates; } set { if (showCoordinates == value) return; showCoordinates = value; Invalidate(); } }
        // 書き出しの幅・高さを4の倍数にそろえる（右と下の透明な余白を点線の枠で示し、UV もその大きさで出す）。
        private bool alignSheetTo4;
        public bool AlignSheetTo4 { get { return alignSheetTo4; } set { if (alignSheetTo4 == value) return; alignSheetTo4 = value; Invalidate(); } }
        public Size SheetSize
        {
            get
            {
                if (packing.Count == 0) return Size.Empty;
                int w = packing.Values.Max(p => p.Right), h = packing.Values.Max(p => p.Bottom);
                return alignSheetTo4 ? new Size((w + 3) / 4 * 4, (h + 3) / 4 * 4) : new Size(w, h);
            }
        }
        public UvCoordinateFormat UvFormat { get; set; }
        private Point coordinateHover = new Point(-1, -1);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer timer = new Timer { Interval = 15 };
        private Timer viewMotion;
        private float zoom = 4;
        private PointF pan = new PointF(20, 20);
        private Point lastMouse;
        private Point hover = new Point(-1, -1);
        private bool dragging, painting, changed;
        private Point previousPaint = new Point(-1, -1);
        private readonly List<Pulse> pulses = new List<Pulse>();
        private string focusAsset;
        // 演出の状態。
        private BrushState shownBrush, previousBrush;
        private double brushAt = NoMotion;
        private int shownLayer = -1, previousLayer = -1;
        private double layerAt = NoMotion;
        private readonly bool[] shownVisible = { true, true, true, true };
        private readonly double[] visibleAt = { NoMotion, NoMotion, NoMotion, NoMotion };
        private string shownPaletteSelection;
        private double paletteAt = NoMotion;
        private string paletteHover;
        private RectangleF hoverFrom, hoverDrawn;
        private bool hoverDrawnValid;
        private double hoverAt = NoMotion;
        // シートの並び替え（左長押しで持ち上げ、離した位置へ入れる）。
        private const double LiftSeconds = .35;
        private readonly Timer liftTimer = new Timer { Interval = (int)(LiftSeconds * 1000) };
        private string liftCandidate, reorderId;
        private Point liftPoint, reorderMouse;
        private SizeF grabOffset;
        // 持ち上げたまとまりを除いた並び（行 → まとまり）と、入れる場所。
        private List<List<List<string>>> reorderBase;
        private int targetRow, targetPosition;
        private bool targetNewRow;
        private string reorderStartKey;
        private List<string> reorderUnit;
        private Dictionary<string, SizeF> unitOffsets;
        private double liftAt = NoMotion;
        private Dictionary<string, Rectangle> reorderPacking;
        // 表示中の位置（ワールド座標）。並びが変わると、ここから新しい位置へ滑る。
        private readonly Dictionary<string, RectangleF> shownRects = new Dictionary<string, RectangleF>();
        private double lastPaletteFrame;
        private bool paletteSettling;
        private double lastRainbowFrame;
        // ツールバーと、ツールの動き。
        private static readonly MapTool[] Tools = { MapTool.Erase, MapTool.Move, MapTool.RotateClockwise, MapTool.RotateCounterClockwise, MapTool.Flip, MapTool.FlipVertical };
        private bool shownToolMode;
        private double toolbarAt = NoMotion;
        private int toolHover = -1;
        private readonly Timer toolHintTimer = new Timer { Interval = 300 };
        private sealed class TileMotion { public float FromAngle, FromFlip, FromFlipY = 1; public RectangleF FromRect; public bool HasRect; public double At; }
        private readonly Dictionary<MapPlacement, TileMotion> tileMotions = new Dictionary<MapPlacement, TileMotion>();
        private MapPlacement movingTile;
        // プレビューの範囲選択（Shift＋左ドラッグ。マス単位）。選んだ範囲の中を右クリックすると、配置・ツールを範囲にまとめて使う。
        private Rectangle cellSelection = Rectangle.Empty;
        private Point cellAnchor = new Point(-1, -1), cellPress;
        private bool cellSelecting, cellDragged;
        private List<MapPlacement> movingGroup;   // 範囲ごと動かしているチップ
        private Point groupGrab;                  // 範囲を掴んだマス
        public Rectangle CellSelection { get { return cellSelection; } }
        private Point movingMouse;
        private Size movingGrab;   // つかんだマスと、チップの左上のマスの差
        private double movingAt = NoMotion;
        private int overlayHoverLayer = -1;
        private bool overlayHoverEye, overlayHoverBrush;
        public Rectangle BrushBounds { get { return new Rectangle(Math.Max(0, Width - 104), 12, 92, 92); } }
        public float Zoom { get { return zoom; } }
        public Rectangle LayerBounds(int layer) { return new Rectangle(BrushBounds.X - 36, BrushBounds.Bottom + 10 + (3 - layer) * 66, 128, 60); }
        public Rectangle LayerEyeBounds(int layer) { Rectangle r = LayerBounds(layer); return new Rectangle(r.Right - 34, r.Y + 15, 30, 30); }
        private static Rectangle LayerThumbBounds(Rectangle row) { return new Rectangle(row.X + 24, row.Y + 8, 62, 44); }
        private Rectangle LayersBounds { get { return Rectangle.Union(LayerBounds(3), LayerBounds(0)); } }
        private bool OverlayContains(Point p) { return BrushBounds.Contains(p) || LayersBounds.Contains(p) || (Eraser && ToolbarBounds.Contains(p)); }
        // プレビュー下部の中央。指が届きやすいよう下に置く。
        public Rectangle ToolbarBounds
        {
            get
            {
                int button = 40, gap = 4, pad = 6;
                int w = pad * 2 + Tools.Length * button + (Tools.Length - 1) * gap, h = pad * 2 + button;
                return new Rectangle(Math.Max(0, (Width - w) / 2), Math.Max(0, Height - h - 14), w, h);
            }
        }
        public Rectangle ToolButtonBounds(int index)
        {
            Rectangle bar = ToolbarBounds;
            return new Rectangle(bar.X + 6 + index * 44, bar.Y + 6, 40, 40);
        }
        private sealed class Pulse { public int X, Y; public bool Erase; public double At; }
        private sealed class BrushState
        {
            public string Id; public bool Animated; public bool Eraser; public MapTool Tool;
            public bool Same(BrushState other) { return other != null && Eraser == other.Eraser && (Eraser ? Tool == other.Tool : (Id == other.Id && Animated == other.Animated)); }
        }

        public MapCanvas()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(16, 23, 29); ForeColor = Color.White; TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            holdTimer.Tick += (s, e) => ShowBasisAssignment();
            toolHintTimer.Tick += (s, e) => { toolHintTimer.Stop(); ShowToolHint(); };
            liftTimer.Tick += (s, e) => StartReorder();
            timer.Tick += (s, e) =>
            {
                if (!Visible || Document == null) return;
                bool hadPulses = pulses.Count > 0;
                pulses.RemoveAll(p => Now - p.At > .22);
                bool rainbow = Palette && UiMotion.Enabled && Now - lastRainbowFrame > .033 && Document.Animations.Any(a => a.Frames.Count > 0);
                if (hadPulses || Animating || rainbow || (!Palette && Document.Animations.Any(a => a.Enabled))) Invalidate();
            };
            VisibleChanged += (s, e) => { if (Visible) timer.Start(); else timer.Stop(); };
        }
        private double Now { get { return clock.Elapsed.TotalSeconds; } }
        // 演出の開始時刻。Windows の「アニメーション効果」がオフなら、最初から終わった状態にする。
        private double Stamp() { return UiMotion.Enabled ? Now : NoMotion; }
        private float Progress(double at, double seconds) { return PageTransitionOverlay.Ease((float)((Now - at) / seconds)); }
        private bool Animating
        {
            get
            {
                double n = Now;
                return heldAsset != null || reorderId != null || paletteSettling || movingTile != null || movingGroup != null || n - toolbarAt < .25 || n - movingAt < .25 || tileMotions.Values.Any(m => n - m.At < .25) || n - liftAt < .25 || n - brushAt < .25 || n - layerAt < .25 || n - paletteAt < .25 || n - hoverAt < .15 || visibleAt.Any(a => n - a < .25);
            }
        }
        private void CancelHold() { holdTimer.Stop(); if (heldAsset != null) Invalidate(); heldAsset = null; }
        private void ShowBasisAssignment()
        {
            string id = heldAsset; CancelHold(); Capture = false; EndPress();
            if (id == null || !Visible || !Palette) return;
            if (basisMenu != null) basisMenu.Dispose();
            basisMenu = new ContextMenuStrip { BackColor = Color.FromArgb(24, 32, 38), ForeColor = TextColor, Font = Font, ShowImageMargin = false };
            if (MenuRenderer != null) basisMenu.Renderer = MenuRenderer;
            basisMenu.Items.Add(Loc.T("menu.setBasisCell"), null, (s, e) => { if (BasisAssigned != null) BasisAssigned(id); });
            if (DeleteRequested != null) basisMenu.Items.Add(Loc.T("button.delete"), null, (s, e) => DeleteRequested(id));
            basisMenu.Show(this, holdPoint);
        }
        public void RefreshPacking()
        {
            if (Document != null)
            {
                List<List<List<string>>> rows = Document.LayoutRows(PaletteColumns);
                packing = Document.PackRows(rows); numbers = MapDocument.NumbersOf(packing);
            }
            Invalidate();
        }
        public void SetBackgroundPalette(Color background, Color a, Color b)
        {
            BackColor = background; checkerA = a; checkerB = b;
            if (checker != null) { checker.Dispose(); checker = null; }
            Invalidate();
        }
        private TextureBrush Checker
        {
            get
            {
                if (checker != null) return checker;
                using (var tile = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(tile))
                    using (var a = new SolidBrush(checkerA)) using (var b = new SolidBrush(checkerB))
                    { g.FillRectangle(a, 0, 0, 16, 16); g.FillRectangle(b, 8, 0, 8, 8); g.FillRectangle(b, 0, 8, 8, 8); }
                    checker = new TextureBrush(tile);
                }
                return checker;
            }
        }
        // 市松模様は画面に対して固定し、シートの原点に合わせる。
        private void FillChecker(Graphics g, RectangleF r)
        {
            TextureBrush brush = Checker; brush.ResetTransform(); brush.TranslateTransform(pan.X, pan.Y);
            g.FillRectangle(brush, r);
        }
        public void Fit(bool entire = false)
        {
            if (Document == null || Width < 1 || Height < 1) return;
            float w = Document.CellWidth * (entire ? 20 : 10), h = Document.CellHeight * (entire ? 20 : 10);
            if (Palette) { w = packing.Values.Select(r => r.Right).DefaultIfEmpty(80).Max(); h = packing.Values.Select(r => r.Bottom).DefaultIfEmpty(80).Max(); }
            // 外側の縦横番号を出すときは、左と上に番号の分の余白を空ける。
            int left = Palette && showAxisNumbers ? AxisMarginLeft : 0, top = Palette && showAxisNumbers ? AxisMarginTop : 0;
            float z = Math.Max(.002f, Math.Min((Width - 40 - left) / w, (Height - 40 - top) / h));
            float centerW = Palette ? w : Document.CellWidth * 20, centerH = Palette ? h : Document.CellHeight * 20;
            MoveView(z, new PointF(left + (Width - left - centerW * z) / 2, top + (Height - top - centerH * z) / 2));
        }
        public void FocusAsset(string id)
        {
            Rectangle r;
            if (!packing.TryGetValue(id, out r)) return;
            focusAsset = id;
            float z = Math.Min(Width * .36f / r.Width, Height * .36f / r.Height);
            MoveView(z, new PointF(Width / 2f - (r.X + r.Width / 2f) * z, Height / 2f - (r.Y + r.Height / 2f) * z));
        }
        private void MoveView(float z, PointF at)
        {
            UiMotion.Stop(ref viewMotion); float oldZ = zoom; PointF oldP = pan;
            viewMotion = UiMotion.Animate(200, t => { zoom = oldZ + (z - oldZ) * t; pan = new PointF(oldP.X + (at.X - oldP.X) * t, oldP.Y + (at.Y - oldP.Y) * t); Invalidate(); if (ViewChanged != null) ViewChanged(); });
        }
        private PointF World(Point p) { return new PointF((p.X - pan.X) / zoom, (p.Y - pan.Y) / zoom); }
        private RectangleF Screen(RectangleF r) { return new RectangleF(pan.X + r.X * zoom, pan.Y + r.Y * zoom, r.Width * zoom, r.Height * zoom); }
        public Point CellScreen(int x, int y) { return new Point((int)(pan.X + (x + .5f) * Document.CellWidth * zoom), (int)(pan.Y + (y + .5f) * Document.CellHeight * zoom)); }
        private Point Cell(Point p) { PointF w = World(p); return new Point((int)Math.Floor(w.X / Document.CellWidth), (int)Math.Floor(w.Y / Document.CellHeight)); }
        private RectangleF CellWorld(Point c) { return new RectangleF(c.X * Document.CellWidth, c.Y * Document.CellHeight, Document.CellWidth, Document.CellHeight); }
        private string PaletteAssetAt(Point p)
        {
            PointF w = World(p);
            foreach (var entry in packing) if (entry.Value.Contains((int)w.X, (int)w.Y)) return entry.Key;
            return null;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus(); if (Document == null) return;
            if (!Palette)
                for (int layer = 0; layer < 4; layer++)
                    if (LayerBounds(layer).Contains(e.Location))
                    {
                        if (e.Button == MouseButtons.Left)
                        {
                            if (LayerEyeBounds(layer).Contains(e.Location)) Document.VisibleLayers[layer] = !Document.VisibleLayers[layer];
                            else Document.ActiveLayer = layer;
                            if (LayersChanged != null) LayersChanged(); Invalidate();
                        }
                        else if (e.Button == MouseButtons.Right && LayerMenu != null) LayerMenu(layer, e.Location);
                        return;
                    }
            if (!Palette && LayersBounds.Contains(e.Location)) return;
            if (!Palette && BrushBounds.Contains(e.Location))
            { if (e.Button == MouseButtons.Right) { Eraser = true; ActiveTool = MapTool.Erase; if (BrushChanged != null) BrushChanged(); Invalidate(); } return; }
            if (!Palette && Eraser && ToolbarBounds.Contains(e.Location))
            {
                for (int i = 0; i < Tools.Length; i++)
                    if (ToolButtonBounds(i).Contains(e.Location) && e.Button == MouseButtons.Left) { ActiveTool = Tools[i]; if (BrushChanged != null) BrushChanged(); Invalidate(); }
                return;
            }
            UiMotion.Stop(ref viewMotion);
            lastMouse = e.Location;
            if (Palette && e.Button == MouseButtons.Left && (ModifierState() & Keys.Shift) == Keys.Shift)
            {
                string id = PaletteAssetAt(e.Location);
                if (id != null && SelectedSet.Contains(id))
                {
                    // 選んだ範囲の上から押した: 動かせば選んだチップをまとめて持ち上げる。動かさずに離せば従来の範囲選択。
                    selectionMoveId = id; leftPress = e.Location; Capture = true; RaisePress();
                    return;
                }
                // プレビューと同じく四角く選ぶ（配置が自由なので、番号の順では一部だけを選べない）。
                // クリック: 直前に選んだチップと押したチップを囲む四角。ドラッグ: 押した所から囲んだ四角（何もない所からでもよい）。
                selecting = true; selectDragged = false; selectPressId = id; Capture = true; leftPress = e.Location; selectPressWorld = World(e.Location);
                if (id != null) SelectBetween(selectAnchor ?? id, id);
                return;
            }
            if (!Palette && e.Button == MouseButtons.Left && (ModifierState() & Keys.Shift) == Keys.Shift && !OverlayContains(e.Location))
            {
                Point cell = Cell(e.Location);
                if (InGrid(cell))
                {
                    // 直前の起点から押したマスまで。そのままドラッグすると押したマスからの範囲になる。
                    cellSelecting = true; cellDragged = false; cellPress = cell; Capture = true;
                    SetCellSelection(InGrid(cellAnchor) && !cellSelection.IsEmpty ? cellAnchor : cell, cell);
                    return;
                }
            }
            if (e.Button == MouseButtons.Left) { leftPress = e.Location; leftMoved = false; }
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                dragging = true; Capture = true;
                // シートのチップを動かさずに押し続けると、持ち上げて並び替えられる。
                if (Palette && e.Button == MouseButtons.Left) { liftCandidate = PaletteAssetAt(e.Location); liftPoint = e.Location; if (liftCandidate != null) { liftTimer.Start(); RaisePress(); } }
                return;
            }
            if (e.Button != MouseButtons.Right) return;
            if (Palette)
            {
                string id = PaletteAssetAt(e.Location);
                if (id != null && SelectedSet.Contains(id) && SelectionMenu != null) { SelectionMenu(e.Location); return; }
                if (id == null && EmptyMenu != null) { EmptyMenu(e.Location); return; }
                if (id != null)
                {
                    // 配置に使うチップにするのは、長押しでないと分かった（短く離した）とき。
                    heldAsset = id; holdPoint = e.Location; holdAt = Now; Capture = true; holdTimer.Start(); RaisePress(); Invalidate();
                }
                return;
            }
            if (!cellSelection.IsEmpty && !OverlayContains(e.Location))
            {
                // 選んだ範囲の中: 範囲にまとめて使う。外: 範囲選択を外して、いつもどおり1マスに使う。
                if (cellSelection.Contains(Cell(e.Location))) { ApplyToSelection(e.Location); return; }
                ClearCellSelection();
            }
            if (Eraser && ActiveTool != MapTool.Erase) { UseTool(e.Location); return; }
            painting = true; changed = false; previousPaint = new Point(-1, -1); Capture = true;
            if (StrokeStarted != null) StrokeStarted(); PaintAt(e.Location);
        }
        // 移動・回転・反転。右クリックしたマスを覆っているチップ（選択中のレイヤー）に使う。
        private void UseTool(Point at)
        {
            if (OverlayContains(at)) return;
            Point cell = Cell(at);
            MapPlacement tile = Document.TileAt(Document.ActiveLayer, cell.X, cell.Y);
            if (tile == null) return;
            if (ActiveTool == MapTool.Move)
            {
                // つかむ: 少し持ち上げて指先に付いてくる。離したマスへ置く。
                movingTile = tile; movingMouse = at; movingGrab = new Size(cell.X - tile.X, cell.Y - tile.Y); movingAt = Stamp(); Capture = true; Invalidate();
                return;
            }
            if (StrokeStarted != null) StrokeStarted();
            TransformTile(tile);
            Invalidate();
            if (StrokeFinished != null) StrokeFinished();
        }
        // 回転・反転を1つのチップに掛け、動きを付ける。
        private void TransformTile(MapPlacement tile)
        {
            var motion = new TileMotion { FromAngle = 90f * tile.Rotation, FromFlip = tile.FlipX ? -1 : 1, FromFlipY = tile.FlipY ? -1 : 1, At = Stamp() };
            if (ActiveTool == MapTool.RotateClockwise) tile.Rotation = (tile.Rotation + 1) % 4;
            else if (ActiveTool == MapTool.RotateCounterClockwise) tile.Rotation = (tile.Rotation + 3) % 4;
            else if (ActiveTool == MapTool.Flip) tile.FlipX = !tile.FlipX;
            else if (ActiveTool == MapTool.FlipVertical) tile.FlipY = !tile.FlipY;
            // 回転は短い向きに回す（270°→0°は +90°として動かす）。
            float target = 90f * tile.Rotation;
            if (ActiveTool == MapTool.RotateClockwise && target < motion.FromAngle) motion.FromAngle -= 360;
            if (ActiveTool == MapTool.RotateCounterClockwise && target > motion.FromAngle) motion.FromAngle += 360;
            tileMotions[tile] = motion;
        }
        private static bool InGrid(Point c) { return c.X >= 0 && c.Y >= 0 && c.X < MapDocument.Extent && c.Y < MapDocument.Extent; }
        private static Point ClampCell(Point c) { return new Point(Math.Max(0, Math.Min(MapDocument.Extent - 1, c.X)), Math.Max(0, Math.Min(MapDocument.Extent - 1, c.Y))); }
        private void SetCellSelection(Point a, Point b)
        {
            a = ClampCell(a); b = ClampCell(b);
            Rectangle r = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
            if (r == cellSelection) return;
            cellSelection = r; Invalidate();
        }
        public void ClearCellSelection()
        {
            if (cellSelection.IsEmpty && cellAnchor.X < 0) return;
            cellSelection = Rectangle.Empty; cellAnchor = new Point(-1, -1); Invalidate();
        }
        // チップが占めるマス（大きなチップは覆う範囲）。
        private Rectangle CellsOf(MapPlacement tile)
        {
            Size f = Document.FootprintOf(tile);
            return new Rectangle(tile.X, tile.Y, Math.Max(1, (f.Width + Document.CellWidth - 1) / Document.CellWidth), Math.Max(1, (f.Height + Document.CellHeight - 1) / Document.CellHeight));
        }
        // 選んだ範囲に掛かっている、選択中のレイヤーのチップ。
        public List<MapPlacement> TilesInSelection()
        {
            if (cellSelection.IsEmpty || Document == null) return new List<MapPlacement>();
            return Document.Tiles.Where(tile => tile.Layer == Document.ActiveLayer && CellsOf(tile).IntersectsWith(cellSelection)).ToList();
        }
        // 範囲の中のチップをすべて消す（元に戻すで戻せる）。消したら true。
        public bool EraseSelection()
        {
            List<MapPlacement> removed = TilesInSelection();
            if (removed.Count == 0) return false;
            if (StrokeStarted != null) StrokeStarted();
            foreach (MapPlacement tile in removed)
            {
                Document.Tiles.Remove(tile);
                if (UiMotion.Enabled) pulses.Add(new Pulse { X = tile.X, Y = tile.Y, Erase = true, At = Now });
            }
            Invalidate();
            if (StrokeFinished != null) StrokeFinished();
            return true;
        }
        // 選んだ範囲の中を右クリック: 配置なら範囲を敷き詰め、ツールなら範囲のチップすべてに使う。
        private void ApplyToSelection(Point at)
        {
            if (Eraser && ActiveTool == MapTool.Move)
            {
                // 範囲のチップをまとめて掴む（起点が範囲の中にあるもの）。離したマスへ、並びを保ったまま動かす。
                List<MapPlacement> group = Document.Tiles.Where(tile => tile.Layer == Document.ActiveLayer && cellSelection.Contains(tile.X, tile.Y)).ToList();
                if (group.Count == 0) return;
                movingGroup = group; groupGrab = Cell(at); movingMouse = at; movingAt = Stamp(); Capture = true; Invalidate();
                return;
            }
            if (Eraser && ActiveTool == MapTool.Erase) { EraseSelection(); return; }
            if (StrokeStarted != null) StrokeStarted();
            bool any = false;
            if (!Eraser)
            {
                // 置くチップの大きさごとに、範囲の左上から敷き詰める。
                Rectangle step = CellsOf(new MapPlacement { Source = Selected ?? "", Animated = SelectedAnimation });
                for (int y = cellSelection.Top; y < cellSelection.Bottom; y += step.Height)
                    for (int x = cellSelection.Left; x < cellSelection.Right; x += step.Width)
                        if (Document.SetTile(x, y, Selected, SelectedAnimation, false))
                        { any = true; if (UiMotion.Enabled) pulses.Add(new Pulse { X = x, Y = y, At = Now }); }
            }
            else
                foreach (MapPlacement tile in TilesInSelection()) { TransformTile(tile); any = true; }
            Invalidate();
            if (any && StrokeFinished != null) StrokeFinished();
        }
        // 範囲ごと動かしているときの移動量（マス）。範囲が地図の外へ出ない量に抑える。
        private Size GroupDelta()
        {
            Point now = Cell(movingMouse);
            int dx = now.X - groupGrab.X, dy = now.Y - groupGrab.Y;
            int minX = movingGroup.Min(t => t.X), minY = movingGroup.Min(t => t.Y), maxX = movingGroup.Max(t => t.X), maxY = movingGroup.Max(t => t.Y);
            dx = Math.Max(-minX, Math.Min(MapDocument.Extent - 1 - maxX, dx));
            dy = Math.Max(-minY, Math.Min(MapDocument.Extent - 1 - maxY, dy));
            return new Size(dx, dy);
        }
        private RectangleF TileWorld(MapPlacement tile, Size delta)
        {
            Size f = Document.FootprintOf(tile);
            return new RectangleF((tile.X + delta.Width) * Document.CellWidth, (tile.Y + delta.Height) * Document.CellHeight, f.Width, f.Height);
        }
        private void FinishGroupMove(bool commit)
        {
            List<MapPlacement> group = movingGroup; if (group == null) return;
            Size delta = GroupDelta();
            var floating = group.ToDictionary(tile => tile, tile => TileWorld(tile, delta));
            movingGroup = null; movingAt = Stamp();
            if (commit && (delta.Width != 0 || delta.Height != 0))
            {
                if (StrokeStarted != null) StrokeStarted();
                var targets = new HashSet<Point>(group.Select(tile => new Point(tile.X + delta.Width, tile.Y + delta.Height)));
                // 動かした先のマスにあった、ほかのチップは置き換える。
                Document.Tiles.RemoveAll(tile => !group.Contains(tile) && tile.Layer == Document.ActiveLayer && targets.Contains(new Point(tile.X, tile.Y)));
                foreach (MapPlacement tile in group) { tile.X += delta.Width; tile.Y += delta.Height; }
                cellSelection.Offset(delta.Width, delta.Height);
                cellSelection.Intersect(new Rectangle(0, 0, MapDocument.Extent, MapDocument.Extent));
                if (cellAnchor.X >= 0) cellAnchor = ClampCell(new Point(cellAnchor.X + delta.Width, cellAnchor.Y + delta.Height));
                if (StrokeFinished != null) StrokeFinished();
            }
            // 離した位置から、置いたマスへ収まる。
            foreach (MapPlacement tile in group)
            {
                TileMotion motion; if (!tileMotions.TryGetValue(tile, out motion)) motion = new TileMotion { FromAngle = 90f * tile.Rotation, FromFlip = tile.FlipX ? -1 : 1, FromFlipY = tile.FlipY ? -1 : 1 };
                motion.FromRect = floating[tile]; motion.HasRect = true; motion.At = Stamp(); tileMotions[tile] = motion;
            }
            Invalidate();
        }
        private void FinishMove(bool commit)
        {
            MapPlacement tile = movingTile; if (tile == null) return;
            RectangleF floating = MovingRect();
            movingTile = null; movingAt = Stamp();
            Point cell = Cell(movingMouse);
            int x = cell.X - movingGrab.Width, y = cell.Y - movingGrab.Height;
            if (commit && x >= 0 && y >= 0 && x < MapDocument.Extent && y < MapDocument.Extent && (x != tile.X || y != tile.Y))
            {
                if (StrokeStarted != null) StrokeStarted();
                Document.MoveTile(tile, x, y);
                if (StrokeFinished != null) StrokeFinished();
            }
            // 離した位置から、置いたマスへ収まる。
            TileMotion motion; if (!tileMotions.TryGetValue(tile, out motion)) motion = new TileMotion { FromAngle = 90f * tile.Rotation, FromFlip = tile.FlipX ? -1 : 1, FromFlipY = tile.FlipY ? -1 : 1 };
            motion.FromRect = floating; motion.HasRect = true; motion.At = Stamp(); tileMotions[tile] = motion;
            Invalidate();
        }
        // 動かしているチップの位置（ワールド座標）。つかんだマスが指先に来る。
        private RectangleF MovingRect()
        {
            Size f = Document.FootprintOf(movingTile);
            PointF w = World(movingMouse);
            float gx = (movingGrab.Width + .5f) * Document.CellWidth, gy = (movingGrab.Height + .5f) * Document.CellHeight;
            return new RectangleF(w.X - gx, w.Y - gy, f.Width, f.Height);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (Document == null) return;
            if (heldAsset != null && (Math.Abs(e.X - holdPoint.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - holdPoint.Y) > SystemInformation.DragSize.Height)) CancelHold();
            if (liftCandidate != null && (Math.Abs(e.X - liftPoint.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - liftPoint.Y) > SystemInformation.DragSize.Height)) CancelLift();
            if (selectionMoveId != null && (Math.Abs(e.X - leftPress.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - leftPress.Y) > SystemInformation.DragSize.Height))
            {
                liftCandidate = selectionMoveId; liftPoint = leftPress; selectionMoveId = null;
                LiftChips(true);
                if (reorderId != null) { reorderMoved = true; reorderMouse = e.Location; UpdateReorderTarget(); }
                Invalidate(); return;
            }
            if (selectionMoveId != null) return;
            if (reorderId != null)
            {
                if (Math.Abs(e.X - liftPoint.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - liftPoint.Y) > SystemInformation.DragSize.Height) reorderMoved = true;
                reorderMouse = e.Location; UpdateReorderTarget(); Invalidate(); return;
            }
            if (selecting)
            {
                if (Math.Abs(e.X - leftPress.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - leftPress.Y) > SystemInformation.DragSize.Height) selectDragged = true;
                if (selectDragged)
                {
                    PointF w = World(e.Location);
                    selectBox = RectangleF.FromLTRB(Math.Min(w.X, selectPressWorld.X), Math.Min(w.Y, selectPressWorld.Y), Math.Max(w.X, selectPressWorld.X), Math.Max(w.Y, selectPressWorld.Y));
                    SelectBox(selectBox);
                }
                paletteHover = PaletteAssetAt(e.Location); Invalidate(); return;
            }
            if (dragging && (Math.Abs(e.X - leftPress.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - leftPress.Y) > SystemInformation.DragSize.Height)) leftMoved = true;
            if (movingTile != null || movingGroup != null) { movingMouse = e.Location; hover = Cell(e.Location); Invalidate(); return; }
            if (cellSelecting)
            {
                Point pressed = Cell(e.Location);
                if (!cellDragged && pressed != cellPress) cellDragged = true;
                if (cellDragged) SetCellSelection(cellPress, pressed);
                return;
            }
            if (dragging) { pan.X += e.X - lastMouse.X; pan.Y += e.Y - lastMouse.Y; lastMouse = e.Location; }
            if (Palette) { paletteHover = dragging ? null : PaletteAssetAt(e.Location); coordinateHover = e.Location; }
            else UpdateOverlayHover(e.Location);
            Point cell = !Palette && !OverlayContains(e.Location) ? Cell(e.Location) : new Point(-1, -1);
            if (cell != hover)
            {
                // 塗っている間は枠を遅らせない。止まっているときだけ次のマスへ滑らせる。
                hoverFrom = hoverDrawnValid ? hoverDrawn : CellWorld(cell);
                hoverAt = painting ? NoMotion : Stamp();
                hover = cell;
            }
            if (painting) PaintAt(e.Location);
            Invalidate();
        }
        private void UpdateOverlayHover(Point p)
        {
            overlayHoverBrush = BrushBounds.Contains(p); overlayHoverLayer = -1; overlayHoverEye = false;
            for (int layer = 0; layer < 4; layer++)
                if (LayerBounds(layer).Contains(p)) { overlayHoverLayer = layer; overlayHoverEye = LayerEyeBounds(layer).Contains(p); }
            int tool = -1;
            if (Eraser) for (int i = 0; i < Tools.Length; i++) if (ToolButtonBounds(i).Contains(p)) tool = i;
            if (tool != toolHover)
            {
                toolHover = tool; toolHintTimer.Stop();
                if (Hints != null) Hints.Hide(this);
                if (tool >= 0) toolHintTimer.Start();
            }
            Cursor = overlayHoverLayer >= 0 || tool >= 0 ? Cursors.Hand : Eraser && ActiveTool == MapTool.Move ? Cursors.SizeAll : Cursors.Default;
        }
        // レイヤーのチップをすべて消す。消えたマスは赤く光って消える。
        public bool ClearLayer(int layer)
        {
            List<MapPlacement> removed = Document.Tiles.Where(t => t.Layer == layer).ToList();
            if (removed.Count == 0) return false;
            if (StrokeStarted != null) StrokeStarted();
            foreach (MapPlacement tile in removed)
            {
                Document.Tiles.Remove(tile);
                if (UiMotion.Enabled && Document.VisibleLayers[layer]) pulses.Add(new Pulse { X = tile.X, Y = tile.Y, Erase = true, At = Now });
            }
            Invalidate();
            if (StrokeFinished != null) StrokeFinished();
            return true;
        }
        private static string ToolKey(MapTool tool)
        {
            switch (tool)
            {
                case MapTool.Move: return "tooltip.tool.move";
                case MapTool.RotateClockwise: return "tooltip.tool.rotateClockwise";
                case MapTool.RotateCounterClockwise: return "tooltip.tool.rotateCounterClockwise";
                case MapTool.Flip: return "tooltip.tool.flip";
                case MapTool.FlipVertical: return "tooltip.tool.flipVertical";
                default: return "tooltip.tool.erase";
            }
        }
        // アイコンに0.3秒カーソルを乗せると説明を出す（ほかのヘルプと同じ札）。
        private void ShowToolHint()
        {
            if (Hints == null || toolHover < 0 || !Eraser || !HelpTipStyle.HintsEnabled) return;
            Rectangle b = ToolButtonBounds(toolHover);
            Hints.Show(Loc.T(ToolKey(Tools[toolHover])), this, b.X, ToolbarBounds.Y - 8 - HelpTipStyle.Measure(Loc.T(ToolKey(Tools[toolHover]))).Height, 8000);
        }
        private void PaintAt(Point at)
        {
            if (OverlayContains(at)) { previousPaint = new Point(-1, -1); return; }
            Point cell = Cell(at);
            if (cell.X < 0 || cell.Y < 0 || cell.X >= 20 || cell.Y >= 20) { previousPaint = new Point(-1, -1); return; }
            Point from = previousPaint.X < 0 ? cell : previousPaint;
            int count = Math.Max(Math.Abs(cell.X - from.X), Math.Abs(cell.Y - from.Y));
            for (int i = 0; i <= count; i++)
            {
                float t = count == 0 ? 1 : i / (float)count;
                int x = (int)Math.Round(from.X + (cell.X - from.X) * t), y = (int)Math.Round(from.Y + (cell.Y - from.Y) * t);
                if (Eraser ? Document.EraseAt(x, y) : Document.SetTile(x, y, Selected, SelectedAnimation, false))
                { changed = true; if (UiMotion.Enabled) pulses.Add(new Pulse { X = x, Y = y, Erase = Eraser, At = Now }); }
            }
            previousPaint = cell; Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasReorder = reorderId != null;
            // 右ボタンを長押しにならずに離した: そのチップを配置に使う。
            if (e.Button == MouseButtons.Right && heldAsset != null && Palette)
            { string id = heldAsset; CancelHold(); if (AssetSelected != null) AssetSelected(id); }
            if (cellSelecting)
            {
                cellSelecting = false;
                if (cellDragged || !InGrid(cellAnchor)) cellAnchor = cellPress;
            }
            // プレビューの何もない操作の左クリック（動かさずに離した）で範囲選択を外す。
            else if (!Palette && e.Button == MouseButtons.Left && dragging && !leftMoved && !cellSelection.IsEmpty) ClearCellSelection();
            if (selectionMoveId != null)
            {
                // 動かさずに離した Shift＋左クリック: 直前に選んだチップから押したチップまでを選ぶ（従来どおり）。
                string id = selectionMoveId; selectionMoveId = null;
                SelectBetween(selectAnchor ?? id, id); selectAnchor = id;
            }
            else if (selecting)
            {
                selecting = false; selectBox = RectangleF.Empty;
                // 次の Shift＋左クリックの起点: 押したチップ（何もない所から囲んだときは、囲んだ中で番号が一番小さいチップ）。
                selectAnchor = selectPressId ?? SelectedInOrder().FirstOrDefault() ?? selectAnchor;
                Invalidate();
            }
            // 何もない所・チップの上を（動かさずに）左クリックしたら選択を外す。
            else if (Palette && e.Button == MouseButtons.Left && dragging && !leftMoved && !wasReorder && SelectedSet.Count > 0) ClearSelection();
            CancelHold(); CancelLift(); FinishReorder(true); FinishMove(true); FinishGroupMove(true); EndStroke(); dragging = false; Capture = false; EndPress();
        }
        private bool pressing;
        private void RaisePress() { if (pressing) return; pressing = true; if (PressStarted != null) PressStarted(); }
        private void EndPress() { if (!pressing) return; pressing = false; if (PressEnded != null) PressEnded(); }
        public void ClearSelection()
        {
            if (SelectedSet.Count == 0 && selectAnchor == null) return;
            SelectedSet.Clear(); selectAnchor = null; Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }
        // 2つのチップを囲む四角に掛かるチップを選ぶ（どちら向きに選んでも同じ）。
        private void SelectBetween(string fromId, string toId)
        {
            Rectangle a, b;
            if (!packing.TryGetValue(toId, out b)) return;
            if (fromId == null || !packing.TryGetValue(fromId, out a)) a = b;
            SelectBox(Rectangle.Union(a, b));
        }
        // 四角（シートの座標）に掛かるチップを選ぶ。
        private void SelectBox(RectangleF box)
        {
            var chosen = new HashSet<string>(packing.Where(p => box.IntersectsWith(p.Value)).Select(p => p.Key));
            if (chosen.SetEquals(SelectedSet)) return;
            SelectedSet.Clear(); SelectedSet.UnionWith(chosen);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }
        // 選んだチップ（番号の順）。
        public List<string> SelectedInOrder() { return SelectedSet.Where(numbers.ContainsKey).OrderBy(id => numbers[id]).ToList(); }
        protected override void OnMouseCaptureChanged(EventArgs e)
        { base.OnMouseCaptureChanged(e); if (!Capture) { selecting = false; selectBox = RectangleF.Empty; selectionMoveId = null; cellSelecting = false; CancelHold(); CancelLift(); FinishReorder(false); FinishMove(false); FinishGroupMove(false); EndStroke(); dragging = false; EndPress(); } }
        private void CancelLift() { liftTimer.Stop(); liftCandidate = null; }
        private void StartReorder() { LiftChips(liftCandidate != null && SelectedSet.Contains(liftCandidate)); }   // 選んだ範囲の上で長押ししたら、選んだチップをまとめて持ち上げる
        // withSelection: 押したチップが範囲選択に入っていれば、選んだチップをまとめて持ち上げる（番号の順に1列に並べて運ぶ）。
        private void LiftChips(bool withSelection)
        {
            string id = liftCandidate; CancelLift();
            if (id == null || !Palette || !Visible || !packing.ContainsKey(id)) return;
            // アニメーションのコマは、まとまりごと持ち上げる。
            List<List<List<string>>> rows = Document.LayoutRows(PaletteColumns);
            reorderStartKey = MapDocument.RowsKey(rows);
            int row = rows.FindIndex(r => r.Any(u => u.Contains(id)));
            if (row < 0) return;
            var chosen = withSelection && SelectedSet.Contains(id) ? new HashSet<string>(SelectedSet) : new HashSet<string>();
            chosen.Add(id);
            // 押したチップの前にある、残るまとまりの数（＝元の場所）。
            int position = rows[row].TakeWhile(u => !u.Contains(id)).Count(u => !u.Any(chosen.Contains));
            var unit = new List<string>(); var kept = new List<List<List<string>>>(); int keptRow = 0; bool rowEmptied = false;
            for (int r = 0; r < rows.Count; r++)
            {
                List<List<string>> rest = rows[r].Where(u => !u.Any(chosen.Contains)).ToList();
                foreach (List<string> u in rows[r]) if (u.Any(chosen.Contains)) unit.AddRange(u);
                if (r == row) { keptRow = kept.Count; rowEmptied = rest.Count == 0; }
                if (rest.Count > 0) kept.Add(rest);
            }
            reorderUnit = unit;
            // 1つだけの行なら行ごと抜き、元の場所へ新しい行として戻せるようにする。
            targetNewRow = rowEmptied; targetRow = keptRow; targetPosition = position; reorderBase = kept;
            liftNewRow = rowEmptied; liftRow = keptRow;
            reorderBlock = null; reorderMoved = false;
            List<List<List<string>>> chosenRows = rows.Where(r => r.Any(u => u.Any(chosen.Contains))).ToList();
            if (chosenRows.Count > 1 && reorderUnit.All(packing.ContainsKey))
            {
                // 選んだ範囲が複数の行にまたがる: 形（行ごとの並びと横の位置）を保ったまま運び、行ごとに戻す。
                reorderBlock = chosenRows.Select(r => r.Where(u => u.Any(chosen.Contains)).ToList()).ToList();
                int minX = reorderUnit.Min(f => packing[f].X), minY = reorderUnit.Min(f => packing[f].Y);
                blockDx = reorderBlock.Select(b => (float)(packing[b[0][0]].X - minX)).ToList();
                unitOffsets = reorderUnit.ToDictionary(f => f, f => new SizeF(packing[f].X - minX, packing[f].Y - minY));
                basePacking = Document.PackRows(kept);
                targetX = minX; targetNewRow = false;
                targetRow = Math.Min(kept.Count, rows.Take(rows.IndexOf(chosenRows[0])).Count(r => r.Any(u => !u.Any(chosen.Contains))));
            }
            else
            {
                // 運ぶ形は、置いたときと同じ1列の並び。
                unitOffsets = new Dictionary<string, SizeF>(); float x = 0;
                foreach (string f in reorderUnit) { MapAsset a = Document.Asset(f); unitOffsets[f] = new SizeF(x, 0); x += a == null ? 0 : a.Size.Width; }
            }
            Rectangle pressed = packing[id];
            SizeF pressedOffset = unitOffsets[id];
            PointF grab = World(liftPoint);
            // 押したチップの、押した所が指先に付いてくる。
            grabOffset = new SizeF(grab.X - pressed.X + pressedOffset.Width, grab.Y - pressed.Y + pressedOffset.Height);
            reorderId = id; dragging = false; Capture = true; liftAt = Stamp();
            reorderMouse = PointToClient(Cursor.Position);
            reorderPacking = Document.PackRows(PreviewRows());
            Cursor = Cursors.SizeAll; Invalidate();
        }
        private bool liftNewRow; private int liftRow;
        // 複数の行にまたがる選択を運んでいるとき: 行ごとのまとまり、各行の左端のずれ、置く左端、持ち上げる前の並び（自分を除く）。
        private List<List<List<string>>> reorderBlock;
        private List<float> blockDx;
        private float targetX;
        private Dictionary<string, Rectangle> basePacking;
        private bool reorderMoved;   // 持ち上げてから動かしたか（動かさずに離したら何も変えない）
        private List<List<List<string>>> PreviewRows() { return reorderBlock != null ? BlockRowsFor(targetRow, targetX, targetNewRow) : RowsFor(targetRow, targetPosition, targetNewRow); }
        // 形を保つ置き方: 1行目を row 行目に、2行目をその次の行に…、各行は左端 x（＋その行のずれ）の位置へ入れる。足りない行は下に足す。
        // newRows のときは、row の位置に新しい行として入れる。
        private List<List<List<string>>> BlockRowsFor(int row, float x, bool newRows)
        {
            var rows = reorderBase.Select(r => new List<List<string>>(r)).ToList();
            int start = Math.Min(row, rows.Count);
            for (int k = 0; k < reorderBlock.Count; k++)
            {
                int index = start + k;
                if (newRows || index >= rows.Count) { rows.Insert(Math.Min(index, rows.Count), new List<List<string>>(reorderBlock[k])); continue; }
                float left = x + blockDx[k];
                int position = rows[index].Count(u =>
                {
                    Rectangle first, last;
                    if (!basePacking.TryGetValue(u[0], out first) || !basePacking.TryGetValue(u[u.Count - 1], out last)) return false;
                    return (first.X + last.Right) / 2f <= left;
                });
                rows[index].InsertRange(position, reorderBlock[k]);
            }
            return rows;
        }
        // 形を保って運んでいるときの置き先: 各行の候補と下の新しい行を並べ、置いたときの左上が浮いている絵の左上に一番近いもの。
        private void UpdateBlockTarget()
        {
            RectangleF floating = reorderUnit.Select(FloatingRect).Aggregate(RectangleF.Union);
            float x = floating.Left;
            var candidates = new List<Tuple<int, bool>>();
            for (int r = 0; r < reorderBase.Count; r++) candidates.Add(Tuple.Create(r, false));
            candidates.Add(Tuple.Create(reorderBase.Count, true));
            Func<Tuple<int, bool>, double> distance = c =>
            {
                Dictionary<string, Rectangle> packed = Document.PackRows(BlockRowsFor(c.Item1, x, c.Item2));
                if (!reorderUnit.All(packed.ContainsKey)) return double.MaxValue;
                RectangleF box = reorderUnit.Select(f => (RectangleF)packed[f]).Aggregate(RectangleF.Union);
                double dx = box.Left - floating.Left, dy = box.Top - floating.Top;
                return dx * dx + dy * dy;
            };
            var current = Tuple.Create(targetRow, targetNewRow);
            Tuple<int, bool> best = current; double bestDistance = distance(current);
            foreach (var c in candidates)
            {
                if (c.Equals(current)) continue;
                double d = distance(c);
                if (d < bestDistance - 1e-9) { best = c; bestDistance = d; }
            }
            targetRow = best.Item1; targetNewRow = best.Item2; targetX = x;
            reorderPacking = Document.PackRows(PreviewRows());
        }
        private List<List<List<string>>> RowsFor(int row, int position, bool newRow)
        {
            var rows = reorderBase.Select(r => new List<List<string>>(r)).ToList();
            if (newRow || row >= rows.Count) rows.Insert(Math.Min(row, rows.Count), new List<List<string>> { reorderUnit });
            else rows[row].Insert(Math.Min(position, rows[row].Count), reorderUnit);
            return rows;
        }
        private bool IsLifted(string id) { return reorderUnit != null && reorderUnit.Contains(id); }
        // 入る場所の候補（各行で指先より左にあるまとまりの後ろ・下に新しい行・元の自分だけの行）を並べ、
        // 実際に置いたときのまとまりの範囲が指先に一番近いものを選ぶ。チップは上に詰めて置かれるので、
        // 大きなチップの横の空きを指せば、その空きへ入る候補が選ばれる。同じ近さなら今の場所を保つ（行き来しない）。
        private void UpdateReorderTarget()
        {
            if (reorderBlock != null) { UpdateBlockTarget(); return; }
            PointF w = World(reorderMouse);
            var candidates = new List<Tuple<int, int, bool>>();
            for (int r = 0; r < reorderBase.Count; r++)
            {
                int position = reorderBase[r].Count(u =>
                {
                    Rectangle first, last;
                    if (!reorderPacking.TryGetValue(u[0], out first) || !reorderPacking.TryGetValue(u[u.Count - 1], out last)) return false;
                    return (first.X + last.Right) / 2f <= w.X;
                });
                candidates.Add(Tuple.Create(r, position, false));
            }
            candidates.Add(Tuple.Create(reorderBase.Count, 0, true));
            if (liftNewRow) candidates.Add(Tuple.Create(liftRow, 0, true));
            Func<Tuple<int, int, bool>, double> distance = candidate =>
            {
                Dictionary<string, Rectangle> packed = Document.PackRows(RowsFor(candidate.Item1, candidate.Item2, candidate.Item3));
                RectangleF box = RectangleF.Empty; bool any = false;
                foreach (string f in reorderUnit) { Rectangle r; if (!packed.TryGetValue(f, out r)) continue; box = any ? RectangleF.Union(box, r) : r; any = true; }
                if (!any) return double.MaxValue;
                double dx = Math.Max(0, Math.Max(box.Left - w.X, w.X - box.Right)), dy = Math.Max(0, Math.Max(box.Top - w.Y, w.Y - box.Bottom));
                double cx = w.X - (box.Left + box.Right) / 2, cy = w.Y - (box.Top + box.Bottom) / 2;
                return (dx * dx + dy * dy) * 1000 + Math.Sqrt(cx * cx + cy * cy) / 1000;   // 範囲の外の距離を優先し、同じなら中心の近さ
            };
            var current = Tuple.Create(targetRow, targetPosition, targetNewRow);
            Tuple<int, int, bool> best = current; double bestDistance = distance(current);
            foreach (var candidate in candidates)
            {
                if (candidate.Equals(current)) continue;
                double d = distance(candidate);
                if (d < bestDistance - 1e-9) { best = candidate; bestDistance = d; }
            }
            if (best.Equals(current)) return;
            targetRow = best.Item1; targetPosition = best.Item2; targetNewRow = best.Item3;
            reorderPacking = Document.PackRows(PreviewRows());
        }
        // 持ち上げているチップの位置（ワールド座標）。まとまりは持ち上げたときの並びのまま付いてくる。
        private RectangleF FloatingRect(string id)
        {
            MapAsset a = Document.Asset(id); Size size = a == null ? new Size(Document.CellWidth, Document.CellHeight) : a.Size;
            PointF w = World(reorderMouse); SizeF offset;
            if (unitOffsets == null || !unitOffsets.TryGetValue(id, out offset)) offset = SizeF.Empty;
            return new RectangleF(w.X - grabOffset.Width + offset.Width, w.Y - grabOffset.Height + offset.Height, size.Width, size.Height);
        }
        private void FinishReorder(bool commit)
        {
            if (reorderId == null) return;
            List<List<List<string>>> rows = PreviewRows();
            foreach (string f in reorderUnit) shownRects[f] = FloatingRect(f);   // 離した位置から収まる場所へ滑る。
            if (!reorderMoved) commit = false;   // 動かさずに離した: 並びは変えない
            reorderId = null; reorderPacking = null; reorderBase = null; reorderUnit = null; unitOffsets = null; reorderBlock = null; blockDx = null; basePacking = null; Cursor = Cursors.Default;
            if (commit && MapDocument.RowsKey(rows) != reorderStartKey)
            { Document.SetRows(rows); RefreshPacking(); if (OrderChanged != null) OrderChanged(); }
            Invalidate();
        }
        private void EndStroke() { if (!painting) return; painting = false; if (changed && StrokeFinished != null) StrokeFinished(); }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e); hover = new Point(-1, -1); hoverDrawnValid = false; paletteHover = null; coordinateHover = new Point(-1, -1);
            overlayHoverLayer = -1; overlayHoverEye = false; overlayHoverBrush = false; toolHover = -1; toolHintTimer.Stop(); Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e); UiMotion.Stop(ref viewMotion); PointF before = World(e.Location);
            zoom = Math.Max(.002f, Math.Min(128, zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f)));
            pan = new PointF(e.X - before.X * zoom, e.Y - before.Y * zoom); hoverDrawnValid = false; Invalidate(); if (ViewChanged != null) ViewChanged();
        }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.F || keyData == Keys.Escape || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F) { Fit(true); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && reorderId != null) { FinishReorder(false); Capture = false; e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && (movingTile != null || movingGroup != null))
            {
                // 移動中の Esc: 動かさずに元へ戻す（離しても確定しない）。範囲選択は残す。
                FinishMove(false); FinishGroupMove(false); Capture = false; e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && !Palette && !cellSelection.IsEmpty) { ClearCellSelection(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (Document == null) return;
            Graphics g = e.Graphics; g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
            if (Palette) DrawPalette(g); else { TrackMapChanges(); DrawMap(g); }
        }
        // 外から書き換えられた選択・レイヤーの変化を、描く直前に拾って演出を始める。
        private void TrackMapChanges()
        {
            if (Eraser != shownToolMode) { shownToolMode = Eraser; toolbarAt = Stamp(); if (!Eraser && Hints != null) Hints.Hide(this); }
            var brush = new BrushState { Id = Selected, Animated = SelectedAnimation, Eraser = Eraser, Tool = ActiveTool };
            if (shownBrush == null) shownBrush = brush;
            else if (!brush.Same(shownBrush)) { previousBrush = shownBrush; shownBrush = brush; brushAt = Stamp(); }
            int active = Document.ActiveLayer;
            if (shownLayer < 0) shownLayer = active;
            else if (active != shownLayer) { previousLayer = shownLayer; shownLayer = active; layerAt = Stamp(); }
            for (int layer = 0; layer < 4; layer++)
                if (Document.VisibleLayers[layer] != shownVisible[layer]) { shownVisible[layer] = Document.VisibleLayers[layer]; visibleAt[layer] = Stamp(); }
        }
        // 0（見えない）～1（見える）。表示を切り替えた直後は短く薄くなる／濃くなる。
        private float LayerOpacity(int layer)
        {
            float p = Progress(visibleAt[layer], .16);
            return Document.VisibleLayers[layer] ? p : 1 - p;
        }
        private void DrawImage(Graphics g, string id, RectangleF rect, float alpha = 1)
        {
            if (!rect.IntersectsWith(ClientRectangle) || alpha <= 0) return;
            DrawImageCore(g, id, rect, alpha);
        }
        private void DrawImageCore(Graphics g, string id, RectangleF rect, float alpha)
        {
            if (alpha <= 0) return;
            Bitmap image = ImageFor == null ? null : ImageFor(id);
            if (image == null)
            {
                using (var pen = new Pen(Color.FromArgb((int)(255 * Math.Min(1, alpha)), 170, 185, 195), 1)) { g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height); g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom); }
                return;
            }
            var source = new RectangleF(0, 0, image.Width, image.Height);
            if (alpha >= 1) { g.DrawImage(image, rect, source, GraphicsUnit.Pixel); return; }
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(image, new[] { new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top), new PointF(rect.Left, rect.Bottom) }, source, GraphicsUnit.Pixel, attributes);
            }
        }
        private static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
            return path;
        }
        private static RectangleF Lerp(RectangleF a, RectangleF b, float t)
        { return new RectangleF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t); }
        // 並びの変化に合わせて、表示中の位置を新しい位置へ近づける（指数的に追いつく）。
        private Dictionary<string, RectangleF> SettlePalette(Dictionary<string, Rectangle> layout)
        {
            double now = Now, dt = paletteSettling ? Math.Max(0, Math.Min(.05, now - lastPaletteFrame)) : .016; lastPaletteFrame = now;
            float k = UiMotion.Enabled ? (float)(1 - Math.Exp(-dt / .045)) : 1;
            var result = new Dictionary<string, RectangleF>(); paletteSettling = false;
            foreach (var entry in layout)
            {
                RectangleF target = entry.Value, shown;
                if (!shownRects.TryGetValue(entry.Key, out shown)) shown = target;
                shown = Lerp(shown, target, k);
                if (Math.Abs(shown.X - target.X) + Math.Abs(shown.Y - target.Y) + Math.Abs(shown.Width - target.Width) + Math.Abs(shown.Height - target.Height) < .02f) shown = target;
                else paletteSettling = true;
                result[entry.Key] = shown;
            }
            shownRects.Clear(); foreach (var entry in result) shownRects[entry.Key] = entry.Value;
            return result;
        }
        private void DrawPalette(Graphics g)
        {
            string selected = Selected;
            if (selected != shownPaletteSelection) { if (shownPaletteSelection != null || !string.IsNullOrEmpty(selected)) paletteAt = Stamp(); shownPaletteSelection = selected; }
            Dictionary<string, RectangleF> layout = SettlePalette(reorderId != null ? reorderPacking : packing);
            double time = Now; lastRainbowFrame = time;
            if (reorderId != null)
            {
                // 置ける範囲（全チップの横の合計 × 縦の合計）をうっすら示す。
                Size range = Document.RangeSize();
                RectangleF area = Screen(new RectangleF(0, 0, range.Width, range.Height));
                using (var fill = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillRectangle(fill, area);
                using (var pen = new Pen(Color.FromArgb(90, MutedText)) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, area.X, area.Y, area.Width, area.Height);
            }
            foreach (var p in layout)
            {
                if (IsLifted(p.Key)) continue;
                RectangleF r = Screen(p.Value); if (!r.IntersectsWith(ClientRectangle)) continue;
                FillChecker(g, r);
                DrawImage(g, p.Key, r);
                MapAsset a = Document.Asset(p.Key); bool warning = Document.NeedsNormalization(a);
                if (p.Key == paletteHover && p.Key != selected && !warning && reorderId == null)
                    using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillRectangle(b, r);
                Color line = warning ? Danger : p.Key == focusAsset ? AccentLine : p.Key == paletteHover ? MutedText : Color.FromArgb(85, 98, 108);
                using (var pen = new Pen(line, warning || p.Key == focusAsset ? 2 : 1)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                if (r.Width > 25 && r.Height > 18) { using (var back = new SolidBrush(Color.FromArgb(170, 16, 20, 27))) g.FillRectangle(back, r.X, r.Y, 25, 17); TextRenderer.DrawText(g, NumberOf(p.Key), Font, new Point((int)r.X + 2, (int)r.Y), Color.White); }
            }
            // 範囲選択したチップ（通常のシートの選択と同じく、紫で覆って枠を付ける）。
            foreach (string id in SelectedSet)
            {
                RectangleF r; if (!layout.TryGetValue(id, out r) || IsLifted(id)) continue;
                RectangleF s = Screen(r);
                using (var fill = new SolidBrush(Color.FromArgb(70, Accent))) g.FillRectangle(fill, s);
                using (var pen = new Pen(AccentLine, 2)) g.DrawRectangle(pen, s.X + 1, s.Y + 1, s.Width - 2, s.Height - 2);
            }
            // 光が隣のチップに隠れないよう、枠はまとめて最後に描く。
            DrawAnimationOutlines(g, layout, time);
            if (!selectBox.IsEmpty)
            {
                RectangleF box = Screen(selectBox);
                using (var fill = new SolidBrush(Color.FromArgb(25, Accent))) g.FillRectangle(fill, box);
                using (var pen = new Pen(AccentLine, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            }
            RectangleF chosen;
            if (!string.IsNullOrEmpty(selected) && !IsLifted(selected) && layout.TryGetValue(selected, out chosen))
            {
                // 選んだチップの枠は、少し外側から縮んで収まる。
                float p = Progress(paletteAt, .18);
                RectangleF r = Screen(chosen); r.Inflate(6 * (1 - p), 6 * (1 - p));
                var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var glow = new Pen(Color.FromArgb((int)(90 * (1 - p)), AccentLine), 6)) g.DrawRectangle(glow, r.X, r.Y, r.Width, r.Height);
                using (var pen = new Pen(AccentLine, 2)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.Restore(state);
            }
            if (alignSheetTo4 && reorderId == null && packing.Count > 0)
            {
                // 書き出す範囲（4の倍数にそろえた大きさ）を点線で示す。
                Size sheet = SheetSize;
                RectangleF area = Screen(new RectangleF(0, 0, sheet.Width, sheet.Height));
                using (var pen = new Pen(Color.FromArgb(150, MutedText)) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, area.X, area.Y, area.Width, area.Height);
            }
            if (showAxisNumbers) DrawAxisNumbers(g);
            if (reorderId != null) DrawLifted(g, layout, time);
            if (heldAsset != null) DrawHoldRing(g);
            if (showCoordinates && reorderId == null && heldAsset == null && paletteHover != null && !dragging) DrawCoordinateCard(g, paletteHover);
        }
        // 列・行は基準セルの大きさで区切った目盛り（外側の縦横番号と同じ）。px と UV はシート全体に対する位置。
        private void DrawCoordinateCard(Graphics g, string id)
        {
            Rectangle r; int number;
            if (!packing.TryGetValue(id, out r) || !numbers.TryGetValue(id, out number)) return;
            int cw = Math.Max(1, Document.CellWidth), ch = Math.Max(1, Document.CellHeight);
            string columns = CoordinateCard.Range(r.Left / cw + 1, (r.Right - 1) / cw + 1), rows = CoordinateCard.Range(r.Top / ch + 1, (r.Bottom - 1) / ch + 1);
            Size sheet = SheetSize;
            string[] lines = CoordinateCard.Lines(number, columns, rows, new Size(cw, ch), r, sheet, UvFormat);
            using (Font font = UiFont.Create(12, FontStyle.Regular, GraphicsUnit.Pixel))
                CoordinateCard.Draw(g, ClientRectangle, coordinateHover, lines, font);
        }
        // 持ち上げたチップ（アニメーションならまとまりごと）。入る場所には点線の枠を出し、
        // チップは少し大きく影を付けて指先に付いてくる。
        private void DrawLifted(Graphics g, Dictionary<string, RectangleF> layout, double time)
        {
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            List<RectangleF> slots = reorderUnit.Where(layout.ContainsKey).Select(f => Screen(layout[f])).ToList();
            if (slots.Count > 0)
            {
                RectangleF r = slots.Aggregate(RectangleF.Union);
                using (var b = new SolidBrush(Color.FromArgb(40, AccentLine))) g.FillRectangle(b, r);
                using (var pen = new Pen(AccentLine, 2) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            }
            float lift = Progress(liftAt, .15);
            List<RectangleF> floating = reorderUnit.Select(f => Screen(FloatingRect(f))).ToList();
            RectangleF all = floating.Aggregate(RectangleF.Union);
            // まとまり全体を、中心から少し大きくする。
            float grow = 1 + .06f * lift; PointF center = new PointF(all.X + all.Width / 2, all.Y + all.Height / 2);
            Func<RectangleF, RectangleF> scale = r => new RectangleF(center.X + (r.X - center.X) * grow, center.Y + (r.Y - center.Y) * grow, r.Width * grow, r.Height * grow);
            all = scale(all);
            using (var shadow = new SolidBrush(Color.FromArgb((int)(110 * lift), 0, 0, 0))) g.FillRectangle(shadow, all.X + 5 * lift, all.Y + 7 * lift, all.Width, all.Height);
            g.Restore(state);
            for (int i = 0; i < reorderUnit.Count; i++)
            {
                RectangleF f = scale(floating[i]); string id = reorderUnit[i];
                FillChecker(g, f);
                DrawImage(g, id, f);
                MapAsset lifted = Document.Asset(id);
                if (lifted != null && f.Width > 25 && f.Height > 18) { using (var back = new SolidBrush(Color.FromArgb(170, 16, 20, 27))) g.FillRectangle(back, f.X, f.Y, 25, 17); TextRenderer.DrawText(g, NumberOf(id), Font, new Point((int)f.X + 2, (int)f.Y), Color.White); }
            }
            if (Document.GroupOf(reorderId) != null) DrawRainbow(g, all, time);
            state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(AccentLine, 2)) g.DrawRectangle(pen, all.X, all.Y, all.Width, all.Height);
            g.Restore(state);
        }
        // アニメーションのコマに使われているチップを囲む虹色の枠。色はゆっくり一周する。
        private static readonly Color[] Rainbow =
        {
            Color.FromArgb(255, 80, 80), Color.FromArgb(255, 170, 60), Color.FromArgb(255, 235, 80), Color.FromArgb(90, 235, 120),
            Color.FromArgb(70, 200, 255), Color.FromArgb(120, 110, 255), Color.FromArgb(235, 90, 255), Color.FromArgb(255, 80, 80)
        };
        // 同じアニメーションで隣り合うチップは、ひとつながりの枠で囲む。
        private void DrawAnimationOutlines(Graphics g, Dictionary<string, RectangleF> layout, double time)
        {
            foreach (MapAnimation clip in Document.Animations)
            {
                List<RectangleF> rects = clip.Frames.Distinct().Where(id => !IsLifted(id) && layout.ContainsKey(id))
                    .Select(id => Screen(layout[id])).Where(r => r.IntersectsWith(ClientRectangle))
                    .Select(r => RectangleF.FromLTRB((float)Math.Round(r.Left), (float)Math.Round(r.Top), (float)Math.Round(r.Right), (float)Math.Round(r.Bottom))).ToList();
                foreach (List<RectangleF> group in ConnectedGroups(rects))
                    using (GraphicsPath path = OutlinePath(group))
                        if (path != null) DrawRainbowPath(g, path, group.Aggregate(RectangleF.Union), time);
            }
        }
        private static List<List<RectangleF>> ConnectedGroups(List<RectangleF> rects)
        {
            int[] parent = Enumerable.Range(0, rects.Count).ToArray();
            Func<int, int> find = null; find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    RectangleF a = rects[i], b = rects[j];
                    // 辺を共有している（角だけで接するものは別扱い）。
                    bool touch = a.Left <= b.Right + .5f && b.Left <= a.Right + .5f && a.Top <= b.Bottom + .5f && b.Top <= a.Bottom + .5f
                        && (Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 1 || Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 1);
                    if (touch) parent[find(i)] = find(j);
                }
            return Enumerable.Range(0, rects.Count).GroupBy(find).Select(gr => gr.Select(i => rects[i]).ToList()).ToList();
        }
        // 矩形の和の外周（穴は除く）を、角を丸めた1本の線にする。
        private static GraphicsPath OutlinePath(List<RectangleF> rects)
        {
            float[] xs = rects.SelectMany(r => new[] { r.Left, r.Right }).Distinct().OrderBy(v => v).ToArray();
            float[] ys = rects.SelectMany(r => new[] { r.Top, r.Bottom }).Distinct().OrderBy(v => v).ToArray();
            int w = xs.Length - 1, h = ys.Length - 1;
            if (w < 1 || h < 1) return null;
            var filled = new bool[w, h];
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++)
                {
                    float cx = (xs[i] + xs[i + 1]) / 2, cy = (ys[j] + ys[j + 1]) / 2;
                    filled[i, j] = rects.Any(r => r.Left < cx && cx < r.Right && r.Top < cy && cy < r.Bottom);
                }
            Func<int, int, bool> at = (i, j) => i >= 0 && j >= 0 && i < w && j < h && filled[i, j];
            // 時計回りの辺（格子点の番号で持つ）。
            var edges = new Dictionary<Point, List<Point>>();
            Action<Point, Point> add = (from, to) => { List<Point> list; if (!edges.TryGetValue(from, out list)) edges[from] = list = new List<Point>(); list.Add(to); };
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++)
                {
                    if (!filled[i, j]) continue;
                    if (!at(i, j - 1)) add(new Point(i, j), new Point(i + 1, j));
                    if (!at(i + 1, j)) add(new Point(i + 1, j), new Point(i + 1, j + 1));
                    if (!at(i, j + 1)) add(new Point(i + 1, j + 1), new Point(i, j + 1));
                    if (!at(i - 1, j)) add(new Point(i, j + 1), new Point(i, j));
                }
            // 一番長い輪（外周）だけを使う。
            List<Point> best = null;
            while (edges.Count > 0)
            {
                Point start = edges.Keys.First(), current = start; var loop = new List<Point>();
                do
                {
                    loop.Add(current);
                    List<Point> next; if (!edges.TryGetValue(current, out next)) break;
                    Point to = next[0]; next.RemoveAt(0); if (next.Count == 0) edges.Remove(current);
                    current = to;
                } while (current != start && loop.Count < 100000);
                if (best == null || loop.Count > best.Count) best = loop;
            }
            if (best == null || best.Count < 4) return null;
            // 一直線に並ぶ点を省き、角だけ残す。
            var corners = new List<PointF>();
            for (int k = 0; k < best.Count; k++)
            {
                Point prev = best[(k + best.Count - 1) % best.Count], cur = best[k], next = best[(k + 1) % best.Count];
                if ((prev.X == cur.X && cur.X == next.X) || (prev.Y == cur.Y && cur.Y == next.Y)) continue;
                corners.Add(new PointF(xs[cur.X], ys[cur.Y]));
            }
            if (corners.Count < 4) return null;
            float minSide = rects.Min(r => Math.Min(r.Width, r.Height));
            float radius = Math.Max(2, Math.Min(12, minSide * .18f));
            var path = new GraphicsPath();
            for (int k = 0; k < corners.Count; k++)
            {
                PointF a = corners[(k + corners.Count - 1) % corners.Count], c = corners[k], b = corners[(k + 1) % corners.Count];
                float ra = Math.Min(radius, Distance(a, c) / 2), rb = Math.Min(radius, Distance(c, b) / 2);
                PointF from = Toward(c, a, ra), to = Toward(c, b, rb);
                path.AddBezier(from, Mix(from, c, .552f), Mix(to, c, .552f), to);
            }
            path.CloseFigure();
            return path;
        }
        private static float Distance(PointF a, PointF b) { return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }
        private static PointF Toward(PointF from, PointF to, float length)
        { float d = Distance(from, to); return d <= 0 ? from : new PointF(from.X + (to.X - from.X) * length / d, from.Y + (to.Y - from.Y) * length / d); }
        private static PointF Mix(PointF a, PointF b, float t) { return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }
        private static void DrawRainbow(Graphics g, RectangleF r, double time)
        {
            if (r.Width < 4 || r.Height < 4) return;
            using (GraphicsPath path = RoundedPath(r, Math.Max(2, Math.Min(12, Math.Min(r.Width, r.Height) * .18f)))) DrawRainbowPath(g, path, r, time);
        }
        private static void DrawRainbowPath(Graphics g, GraphicsPath path, RectangleF bounds, double time)
        {
            float angle = UiMotion.Enabled ? (float)(time * 90 % 360) : 45;
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            // 太く薄い光 → 中くらいの光 → 細く濃い線の順に重ね、ネオンのように光らせる。
            int[] alphas = { 45, 110, 255 }; float[] widths = { 9, 5, 2.2f };
            for (int pass = 0; pass < alphas.Length; pass++)
            {
                int alpha = alphas[pass];
                using (var brush = new LinearGradientBrush(RectangleF.Inflate(bounds, 6, 6), Color.Red, Color.Blue, angle))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = Rainbow.Select(c => Color.FromArgb(alpha, c)).ToArray(),
                        Positions = Enumerable.Range(0, Rainbow.Length).Select(i => i / (float)(Rainbow.Length - 1)).ToArray()
                    };
                    using (var pen = new Pen(brush, widths[pass]) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                }
            }
            g.Restore(state);
        }
        // 持ち上げ中は、落としたあとの番号を先に見せる。
        private string NumberOf(string id)
        {
            Dictionary<string, int> source = reorderId != null ? MapDocument.NumbersOf(reorderPacking) : numbers;
            int n; return source.TryGetValue(id, out n) ? n.ToString() : "";
        }
        // シートの外側に、基準セルの大きさで区切った列番号（上）と行番号（左）を出す（UV の確認用）。
        // 小さすぎて隣と重なるときは出さない。
        private void DrawAxisNumbers(Graphics g)
        {
            Dictionary<string, Rectangle> layout = reorderId != null ? reorderPacking : packing;
            if (layout == null || layout.Count == 0) return;
            int columns = (int)Math.Ceiling(layout.Values.Max(r => r.Right) / (double)Document.CellWidth);
            int rows = (int)Math.Ceiling(layout.Values.Max(r => r.Bottom) / (double)Document.CellHeight);
            float cellW = Document.CellWidth * zoom, cellH = Document.CellHeight * zoom;
            using (var font = UiFont.Create(12, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Size columnText = TextRenderer.MeasureText(columns.ToString(), font), rowText = TextRenderer.MeasureText(rows.ToString(), font);
                if (cellW >= columnText.Width + 2)
                    for (int c = 0; c < columns; c++)
                        TextRenderer.DrawText(g, (c + 1).ToString(), font, new Rectangle((int)(pan.X + c * cellW), (int)pan.Y - AxisMarginTop, (int)Math.Ceiling(cellW), AxisMarginTop - 3),
                            Color.FromArgb(205, 212, 218), TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (cellH >= rowText.Height)
                    for (int r = 0; r < rows; r++)
                        TextRenderer.DrawText(g, (r + 1).ToString(), font, new Rectangle((int)pan.X - AxisMarginLeft, (int)(pan.Y + r * cellH), AxisMarginLeft - 5, (int)Math.Ceiling(cellH)),
                            Color.FromArgb(205, 212, 218), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
        // 右長押しの進み具合。満ちると割り当てメニューが出る。
        private void DrawHoldRing(Graphics g)
        {
            float p = (float)Math.Min(1, (Now - holdAt) / HoldSeconds);
            if (p < .12) return;
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            var ring = new RectangleF(holdPoint.X - 15, holdPoint.Y - 15, 30, 30);
            using (var back = new SolidBrush(Color.FromArgb(170, 16, 20, 27))) g.FillEllipse(back, RectangleF.Inflate(ring, 4, 4));
            using (var track = new Pen(Color.FromArgb(90, MutedText), 3)) g.DrawEllipse(track, ring);
            using (var pen = new Pen(AccentLine, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, ring, -90, 360 * (p - .12f) / .88f);
            g.Restore(state);
        }
        private void DrawMap(Graphics g)
        {
            RectangleF bounds = Screen(new RectangleF(0, 0, Document.CellWidth * 20, Document.CellHeight * 20));
            FillChecker(g, bounds);
            double time = Now;
            float[] opacity = Enumerable.Range(0, 4).Select(LayerOpacity).ToArray();
            foreach (MapPlacement tile in Document.Tiles.OrderBy(t => t.Layer))
            {
                if (opacity[tile.Layer] <= 0 || tile == movingTile || (movingGroup != null && movingGroup.Contains(tile))) continue;
                string id = Document.FrameId(tile.Source, tile.Animated, time);
                Size foot = Document.FootprintOf(tile);
                RectangleF world = new RectangleF(tile.X * Document.CellWidth, tile.Y * Document.CellHeight, foot.Width, foot.Height);
                float angle = 90f * tile.Rotation, flip = tile.FlipX ? -1 : 1, flipY = tile.FlipY ? -1 : 1;
                TileMotion motion;
                if (tileMotions.TryGetValue(tile, out motion))
                {
                    float p = Progress(motion.At, .18);
                    angle = motion.FromAngle + (angle - motion.FromAngle) * p;
                    flip = motion.FromFlip + (flip - motion.FromFlip) * p;
                    flipY = motion.FromFlipY + (flipY - motion.FromFlipY) * p;
                    if (motion.HasRect) world = Lerp(motion.FromRect, world, p);
                    if (p >= 1) tileMotions.Remove(tile);
                }
                RectangleF r = Screen(world);
                Pulse pulse = pulses.LastOrDefault(p => !p.Erase && p.X == tile.X && p.Y == tile.Y);
                if (pulse != null) { float grow = .12f * (1 - PageTransitionOverlay.Ease((float)((time - pulse.At) / .2))); r.Inflate(r.Width * grow / 2, r.Height * grow / 2); }
                DrawTile(g, id, r, tile.Rotation, angle, flip, flipY, opacity[tile.Layer]);
            }
            using (var pen = new Pen(Color.FromArgb(55, 150, 166, 181)))
                for (int i = 0; i <= 20; i++) { float x = pan.X + i * Document.CellWidth * zoom, y = pan.Y + i * Document.CellHeight * zoom; g.DrawLine(pen, x, bounds.Top, x, bounds.Bottom); g.DrawLine(pen, bounds.Left, y, bounds.Right, y); }
            DrawCellSelection(g);
            if (movingGroup == null) DrawHover(g);
            foreach (Pulse p in pulses.Where(p => p.Erase))
            { RectangleF r = Screen(CellWorld(new Point(p.X, p.Y))); using (var b = new SolidBrush(Color.FromArgb((int)(130 * Math.Max(0, 1 - (time - p.At) / .22)), Danger))) g.FillRectangle(b, r); }
            if (movingTile != null) DrawMovingTile(g, time);
            if (movingGroup != null) DrawMovingGroup(g, time);
            DrawBrushChip(g, time);
            DrawLayers(g, time);
            DrawToolbar(g);
        }
        // 置いたチップを、回転（angle 度）と左右反転（flip: 1 / -1、途中は伸び縮み）を掛けて描く。
        // footprint は回転後に占める範囲（画面座標）。rotation は確定した回転で、元画像の縦横を決める。
        private void DrawTile(Graphics g, string id, RectangleF footprint, int rotation, float angle, float flip, float flipY, float alpha)
        {
            if (!footprint.IntersectsWith(ClientRectangle) || alpha <= 0) return;
            bool plain = Math.Abs(angle - 90f * rotation) < .01f && rotation == 0 && flip >= 1 && flipY >= 1;
            if (plain) { DrawImage(g, id, footprint, alpha); return; }
            float w = rotation % 2 == 1 ? footprint.Height : footprint.Width, h = rotation % 2 == 1 ? footprint.Width : footprint.Height;
            var state = g.Save();
            g.TranslateTransform(footprint.X + footprint.Width / 2, footprint.Y + footprint.Height / 2);
            g.RotateTransform(angle);
            g.ScaleTransform(Math.Abs(flip) < .02f ? .02f * Math.Sign(flip == 0 ? 1 : flip) : flip, Math.Abs(flipY) < .02f ? .02f * Math.Sign(flipY == 0 ? 1 : flipY) : flipY);
            DrawImageCore(g, id, new RectangleF(-w / 2, -h / 2, w, h), alpha);
            g.Restore(state);
        }
        private void DrawMovingTile(Graphics g, double time)
        {
            float lift = Progress(movingAt, .15);
            RectangleF r = Screen(MovingRect());
            // 置ける場所の枠
            Point cell = Cell(movingMouse);
            Size f = Document.FootprintOf(movingTile);
            RectangleF slot = Screen(new RectangleF((cell.X - movingGrab.Width) * Document.CellWidth, (cell.Y - movingGrab.Height) * Document.CellHeight, f.Width, f.Height));
            using (var pen = new Pen(AccentLine, 2) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, slot.X, slot.Y, slot.Width, slot.Height);
            r.Inflate(r.Width * .06f * lift, r.Height * .06f * lift);
            using (var shadow = new SolidBrush(Color.FromArgb((int)(110 * lift), 0, 0, 0))) g.FillRectangle(shadow, r.X + 5 * lift, r.Y + 7 * lift, r.Width, r.Height);
            DrawTile(g, Document.FrameId(movingTile.Source, movingTile.Animated, time), r, movingTile.Rotation, 90f * movingTile.Rotation, movingTile.FlipX ? -1 : 1, movingTile.FlipY ? -1 : 1, 1);
        }
        // 範囲選択（シートの選択と同じく、紫で覆って枠を付ける）。
        private void DrawCellSelection(Graphics g)
        {
            if (cellSelection.IsEmpty) return;
            RectangleF r = Screen(new RectangleF(cellSelection.X * Document.CellWidth, cellSelection.Y * Document.CellHeight, cellSelection.Width * Document.CellWidth, cellSelection.Height * Document.CellHeight));
            using (var fill = new SolidBrush(Color.FromArgb(movingGroup != null ? 25 : 55, Accent))) g.FillRectangle(fill, r);
            using (var pen = new Pen(AccentLine, 2)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
        // 範囲ごと動かしているチップ。置ける場所を点線で示し、チップは少し持ち上げて指先に付いてくる。
        private void DrawMovingGroup(Graphics g, double time)
        {
            float lift = Progress(movingAt, .15);
            Size delta = GroupDelta();
            Point now = Cell(movingMouse);
            PointF w = World(movingMouse);
            // 指先とのずれ（マスの中の位置）だけ浮かせる。
            float fx = w.X - (now.X + .5f) * Document.CellWidth, fy = w.Y - (now.Y + .5f) * Document.CellHeight;
            foreach (MapPlacement tile in movingGroup)
            {
                RectangleF slot = Screen(TileWorld(tile, delta));
                using (var pen = new Pen(AccentLine, 2) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, slot.X, slot.Y, slot.Width, slot.Height);
            }
            foreach (MapPlacement tile in movingGroup)
            {
                RectangleF world = TileWorld(tile, delta); world.Offset(fx, fy);
                RectangleF r = Screen(world);
                r.Inflate(r.Width * .06f * lift, r.Height * .06f * lift);
                using (var shadow = new SolidBrush(Color.FromArgb((int)(90 * lift), 0, 0, 0))) g.FillRectangle(shadow, r.X + 5 * lift, r.Y + 7 * lift, r.Width, r.Height);
                DrawTile(g, Document.FrameId(tile.Source, tile.Animated, time), r, tile.Rotation, 90f * tile.Rotation, tile.FlipX ? -1 : 1, tile.FlipY ? -1 : 1, 1);
            }
        }
        // プレビュー下部のツールバー。ツールが有効になると下から出て、無効になると下へ消える。
        private void DrawToolbar(Graphics g)
        {
            float p = Progress(toolbarAt, .18);
            float shown = Eraser ? p : 1 - p;
            if (shown <= 0) return;
            Rectangle bar = ToolbarBounds;
            float offset = (1 - shown) * 24;
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(0, offset);
            int a = (int)(255 * shown);
            using (GraphicsPath path = RoundedPath(new RectangleF(bar.X + .5f, bar.Y + .5f, bar.Width - 1, bar.Height - 1), 10))
            {
                using (var back = new SolidBrush(Color.FromArgb(a * 240 / 255, PanelBack))) g.FillPath(back, path);
                using (var pen = new Pen(Color.FromArgb(a, Divider))) g.DrawPath(pen, path);
            }
            for (int i = 0; i < Tools.Length; i++)
            {
                Rectangle b = ToolButtonBounds(i);
                bool selected = ActiveTool == Tools[i], hot = toolHover == i;
                if (selected || hot)
                    using (GraphicsPath path = RoundedPath(new RectangleF(b.X, b.Y, b.Width, b.Height), 7))
                    using (var fill = new SolidBrush(Color.FromArgb(a * (selected ? 255 : 90) / 255, selected ? Accent : PanelHover))) g.FillPath(fill, path);
                DrawToolIcon(g, Tools[i], b.X + b.Width / 2f, b.Y + b.Height / 2f, .42f, shown, selected ? Color.White : hot ? TextColor : MutedText);
            }
            g.Restore(state);
        }
        // ツールのアイコン。ほかのアイコンと同じ太さの線で描く（scale 1 で 44px 角程度）。
        private static void DrawToolIcon(Graphics g, MapTool tool, float cx, float cy, float scale, float alpha, Color color)
        {
            if (tool == MapTool.Erase) { DrawEraserIcon(g, cx, cy, scale, alpha); return; }
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx, cy); g.ScaleTransform(scale, scale);
            int a = (int)(255 * Math.Min(1, alpha));
            using (var pen = new Pen(Color.FromArgb(a, color), 2f / Math.Max(.2f, scale) * .9f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (tool == MapTool.Move)
                {
                    g.DrawLine(pen, -20, 0, 20, 0); g.DrawLine(pen, 0, -20, 0, 20);
                    foreach (int sx in new[] { -1, 1 })
                    {
                        g.DrawLines(pen, new[] { new PointF(sx * 13, -7), new PointF(sx * 20, 0), new PointF(sx * 13, 7) });
                        g.DrawLines(pen, new[] { new PointF(-7, sx * 13), new PointF(0, sx * 20), new PointF(7, sx * 13) });
                    }
                }
                else if (tool == MapTool.RotateClockwise || tool == MapTool.RotateCounterClockwise)
                {
                    if (tool == MapTool.RotateCounterClockwise) g.ScaleTransform(-1, 1);
                    g.DrawArc(pen, -16, -16, 32, 32, -60, 300);
                    // 矢じり（弧の終わり＝右上で時計回り）
                    double end = (-60 + 300) * Math.PI / 180;
                    var tip = new PointF((float)(16 * Math.Cos(end)), (float)(16 * Math.Sin(end)));
                    g.DrawLines(pen, new[] { new PointF(tip.X - 9, tip.Y - 3), tip, new PointF(tip.X - 2, tip.Y + 9) });
                }
                else if (tool == MapTool.Flip || tool == MapTool.FlipVertical)
                {
                    if (tool == MapTool.FlipVertical) g.RotateTransform(90);
                    using (var dash = new Pen(Color.FromArgb(a, color), pen.Width) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, 0, -20, 0, 20);
                    g.DrawPolygon(pen, new[] { new PointF(-5, -14), new PointF(-5, 14), new PointF(-21, 14) });
                    using (var fill = new SolidBrush(Color.FromArgb(a, color))) g.FillPolygon(fill, new[] { new PointF(5, -14), new PointF(5, 14), new PointF(21, 14) });
                }
            }
            g.Restore(state);
        }
        private void DrawHover(Graphics g)
        {
            if (hover.X < 0 || hover.X >= 20 || hover.Y < 0 || hover.Y >= 20) { hoverDrawnValid = false; return; }
            float p = Progress(hoverAt, .09);
            hoverDrawn = Lerp(hoverFrom, CellWorld(hover), p); hoverDrawnValid = true;
            RectangleF r = Screen(hoverDrawn);
            bool erase = Eraser && ActiveTool == MapTool.Erase;
            // ツールのときは、カーソルの下のチップ全体（大きなチップは覆う範囲）を囲む。
            if (Eraser && movingTile == null)
            {
                MapPlacement under = Document.TileAt(Document.ActiveLayer, hover.X, hover.Y);
                if (under != null) { Size f = Document.FootprintOf(under); r = Screen(new RectangleF(under.X * Document.CellWidth, under.Y * Document.CellHeight, f.Width, f.Height)); }
                else if (!erase) return;
            }
            Color tone = erase ? Danger : AccentLine;
            using (var b = new SolidBrush(Color.FromArgb(erase ? 80 : 50, tone))) g.FillRectangle(b, r);
            using (var pen = new Pen(tone, 2))
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                if (erase) { g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom); g.DrawLine(pen, r.Right, r.Top, r.Left, r.Bottom); }
            }
        }
        // 右上の配置チップ。切り替えると前のチップが縮んで消え、新しいチップが広がって出る。
        private void DrawBrushChip(Graphics g, double time)
        {
            Rectangle chip = BrushBounds;
            float p = Progress(brushAt, .18);
            BrushState current = shownBrush ?? new BrushState { Id = Selected, Animated = SelectedAnimation, Eraser = Eraser, Tool = ActiveTool };
            Color border = current.Eraser && current.Tool == MapTool.Erase ? Danger : Accent;
            if (p < 1 && previousBrush != null) border = UiMotion.Mix(previousBrush.Eraser && previousBrush.Tool == MapTool.Erase ? Danger : Accent, border, p);
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(new RectangleF(chip.X + 1, chip.Y + 1, chip.Width - 2, chip.Height - 2), 8))
            {
                using (var b = new SolidBrush(overlayHoverBrush ? PanelHover : PanelBack)) g.FillPath(b, path);
                using (var pen = new Pen(border, 2)) g.DrawPath(pen, path);
            }
            g.Restore(state);
            state = g.Save(); g.SetClip(Rectangle.Inflate(chip, -3, -3));
            if (p < 1 && previousBrush != null) DrawBrush(g, chip, previousBrush, time, 1 - p, 1 - .2f * p);
            DrawBrush(g, chip, current, time, previousBrush == null ? 1 : p, .8f + .2f * p);
            g.Restore(state);
        }
        private void DrawBrush(Graphics g, Rectangle chip, BrushState brush, double time, float alpha, float scale)
        {
            if (alpha <= 0) return;
            float cx = chip.X + chip.Width / 2f, cy = chip.Y + chip.Height / 2f;
            if (brush.Eraser) { DrawToolIcon(g, brush.Tool, cx, cy, scale, alpha, TextColor); return; }
            string id = Document.FrameId(brush.Id, brush.Animated, time); MapAsset a = Document.Asset(id);
            Size size = a == null ? new Size(40, 40) : a.Size;
            float ratio = (a == null ? 1 : Math.Min(66f / a.Size.Width, 58f / a.Size.Height)) * scale;
            DrawImage(g, id, new RectangleF(cx - size.Width * ratio / 2, cy - size.Height * ratio / 2, size.Width * ratio, size.Height * ratio), alpha);
        }
        // 消しゴム。ほかのアイコンと同じ太さの線で描き、先端だけ色を付ける。
        private static void DrawEraserIcon(Graphics g, float cx, float cy, float scale, float alpha)
        {
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx, cy - 4 * scale); g.ScaleTransform(scale, scale); g.RotateTransform(-35);
            int a = (int)(255 * Math.Min(1, alpha));
            var body = new RectangleF(-22, -11, 44, 22);
            using (GraphicsPath path = RoundedPath(body, 4))
            {
                using (var fill = new SolidBrush(Color.FromArgb(a * 40 / 255, TextColor))) g.FillPath(fill, path);
                var state2 = g.Save(); g.SetClip(new RectangleF(-22, -11, 16, 22));
                using (var tip = new SolidBrush(Color.FromArgb(a, Danger))) g.FillPath(tip, path);
                g.Restore(state2);
                using (var pen = new Pen(Color.FromArgb(a, TextColor), 2) { LineJoin = LineJoin.Round }) { g.DrawPath(pen, path); g.DrawLine(pen, -6, -11, -6, 11); }
            }
            g.Restore(state);
            using (var pen = new Pen(Color.FromArgb(a, MutedText), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, cx - 20 * scale, cy + 20 * scale, cx + 20 * scale, cy + 20 * scale);
        }
        private void DrawLayers(Graphics g, double time)
        {
            for (int layer = 3; layer >= 0; layer--)
            {
                Rectangle r = LayerBounds(layer);
                bool active = Document.ActiveLayer == layer;
                var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath shape = RoundedPath(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), 6))
                {
                    using (var b = new SolidBrush(overlayHoverLayer == layer && !overlayHoverEye ? PanelHover : PanelBack)) g.FillPath(b, shape);
                    using (var pen = new Pen(Divider)) g.DrawPath(pen, shape);
                }
                g.Restore(state);
                TextRenderer.DrawText(g, (layer + 1).ToString(), Font, new Rectangle(r.X + 4, r.Y, 20, r.Height), active ? TextColor : MutedText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                Rectangle thumb = LayerThumbBounds(r);
                using (var b = new SolidBrush(Color.FromArgb(14, 20, 26))) g.FillRectangle(b, thumb);
                state = g.Save(); g.SetClip(thumb);
                // 非表示のレイヤーもサムネイルは残し、薄く表示する。
                float alpha = .35f + .65f * LayerOpacity(layer);
                float scale = Math.Min(thumb.Width / (20f * Document.CellWidth), thumb.Height / (20f * Document.CellHeight));
                float x = thumb.X + (thumb.Width - 20 * Document.CellWidth * scale) / 2, y = thumb.Y + (thumb.Height - 20 * Document.CellHeight * scale) / 2;
                foreach (var tile in Document.Tiles.Where(t => t.Layer == layer))
                {
                    string id = Document.FrameId(tile.Source, tile.Animated, time);
                    Size foot = Document.FootprintOf(tile);
                    DrawTile(g, id, new RectangleF(x + tile.X * Document.CellWidth * scale, y + tile.Y * Document.CellHeight * scale, foot.Width * scale, foot.Height * scale), tile.Rotation, 90f * tile.Rotation, tile.FlipX ? -1 : 1, tile.FlipY ? -1 : 1, alpha);
                }
                g.Restore(state);
                DrawEye(g, layer);
            }
            // 選択枠は前のレイヤーから滑って移る。
            Rectangle to = LayerBounds(Document.ActiveLayer);
            float p = Progress(layerAt, .16);
            RectangleF frame = previousLayer >= 0 && p < 1 ? Lerp(LayerBounds(previousLayer), to, p) : to;
            var frameState = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(new RectangleF(frame.X + 1, frame.Y + 1, frame.Width - 2, frame.Height - 2), 6))
            using (var pen = new Pen(AccentLine, 2)) g.DrawPath(pen, path);
            g.Restore(frameState);
        }
        private void DrawEye(Graphics g, int layer)
        {
            Rectangle eye = LayerEyeBounds(layer);
            bool visible = Document.VisibleLayers[layer], hot = overlayHoverLayer == layer && overlayHoverEye;
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hot) using (var b = new SolidBrush(Color.FromArgb(36, 255, 255, 255))) g.FillEllipse(b, eye);
            Color color = visible ? (hot ? Color.White : Color.FromArgb(220, 224, 235)) : (hot ? MutedText : DisabledText);
            using (var p = new Pen(color, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(p, eye.Left + 3, eye.Top + 15, eye.Left + 10, eye.Top + 3, eye.Right - 10, eye.Top + 3, eye.Right - 3, eye.Top + 15);
                g.DrawBezier(p, eye.Left + 3, eye.Top + 15, eye.Left + 10, eye.Bottom - 3, eye.Right - 10, eye.Bottom - 3, eye.Right - 3, eye.Top + 15);
                g.DrawEllipse(p, eye.Left + 11, eye.Top + 11, 8, 8);
                // 斜線は切り替えに合わせて伸び縮みする。
                float strike = 1 - LayerOpacity(layer);
                if (strike > 0) g.DrawLine(p, eye.Left + 4, eye.Top + 4, eye.Left + 4 + (eye.Width - 8) * strike, eye.Top + 4 + (eye.Height - 8) * strike);
            }
            g.Restore(state);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { UiMotion.Stop(ref viewMotion); holdTimer.Dispose(); liftTimer.Dispose(); toolHintTimer.Dispose(); if (checker != null) checker.Dispose(); if (basisMenu != null) basisMenu.Dispose(); timer.Dispose(); } base.Dispose(disposing); }
    }
}
