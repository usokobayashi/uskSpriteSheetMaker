using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    public sealed class PreviewCanvas : Panel
    {
        private Bitmap image;
        private Size worldSize;
        private Bitmap sceneSprite;
        private Size sceneSize;
        private RectangleF sceneSpriteRect;
        private Color sceneFloorColor;
        private Bitmap floorTile;
        private Terrain terrain;
        private float sceneFloorRatio;
        private bool sceneMirrorHorizontally;
        private Color checkerColorA = Color.FromArgb(47, 47, 47);
        private Color checkerColorB = Color.FromArgb(59, 59, 59);
        private readonly List<PreviewRect> rects = new List<PreviewRect>();

        private float zoom = 0.0f; // 0 = fit
        private PointF pan = new PointF(0, 0);
        private bool dragging;
        private MouseButtons dragButton;
        private Point lastMouse;

        public event EventHandler ZoomChanged;

        // ---- 付箋メモ（シート上・画像座標）----
        // 一覧は呼び出し側（MainForm）が持ち、キャンバスは描画・つかむ・移動だけを担当する。
        public List<SheetMemo> Memos { get; set; }
        // 編集中のメモ（本文は編集用の入力欄が表示するので、ここでは描かない）。
        public SheetMemo EditingMemo { get; set; }
        public event EventHandler<MemoEventArgs> MemoEditRequested;   // 中央（文字を打つ範囲）のクリック
        public event EventHandler<MemoEventArgs> MemoMoved;           // つかんで動かしている間
        public event EventHandler<MemoEventArgs> MemoMoveFinished;    // 離した

        private SheetMemo draggingMemo;
        private PointF memoGrabOffset;
        private bool memoMoved;

        private bool MemosActive
        {
            get { return Memos != null && Memos.Count > 0 && image != null && sceneSprite == null; }
        }

        // 画面の点 → シート（画像）座標 / 逆。
        public PointF ScreenToSheet(Point screen)
        {
            float z = GetEffectiveZoom();
            return ScreenToWorld(screen, z, GetEffectivePan(z));
        }

        public RectangleF SheetToScreen(RectangleF sheetRect)
        {
            float z = GetEffectiveZoom();
            PointF p = GetEffectivePan(z);
            return new RectangleF(p.X + sheetRect.X * z, p.Y + sheetRect.Y * z, sheetRect.Width * z, sheetRect.Height * z);
        }

        // 画像（シート本体）の外側か。セルの上や画像の内側はfalse。
        public bool IsOutsideSheet(Point screen)
        {
            if (image == null) return true;
            PointF w = ScreenToSheet(screen);
            Size world = WorldSize;
            return w.X < 0 || w.Y < 0 || w.X >= world.Width || w.Y >= world.Height;
        }

        // 一番上（最後）のメモ。grabBand は「外側20%のつかむ範囲」。
        public SheetMemo HitTestMemo(Point screen, out bool grabBand)
        {
            grabBand = false;
            if (!MemosActive) return null;
            for (int i = Memos.Count - 1; i >= 0; i--)
            {
                SheetMemo memo = Memos[i];
                RectangleF rect = SheetToScreen(new RectangleF(memo.X, memo.Y, memo.Width, memo.Height));
                if (!rect.Contains(screen)) continue;
                RectangleF inner = RectangleF.Inflate(rect, -rect.Width * 0.2f, -rect.Height * 0.2f);
                grabBand = !inner.Contains(screen);
                return memo;
            }
            return null;
        }

        // セル選択のジェスチャー（Shift+クリック / Shift+ドラッグ / Ctrl+Shift+クリック / セル以外のクリック）。
        // 何を選ぶかは呼び出し側が決める。キャンバスは押した位置のセル番号を通知するだけ。
        public event EventHandler<CellGestureEventArgs> CellGesture;

        // セルが選択中かどうか。選択中のセルは塗りと太い枠で強調する。
        public Func<int, bool> IsCellSelected { get; set; }

        // 修飾キーの取得元（既定は実際のキーボード）。テストで差し替える。
        public Func<Keys> ModifierKeysProvider { get; set; }
        private Keys CurrentModifiers { get { return ModifierKeysProvider != null ? ModifierKeysProvider() : ModifierKeys; } }

        private bool selectingCells;
        private bool selectionBegan;
        private bool selectionRangeClick;
        private int selectionStartCell;
        private int selectionLastCell;
        private bool selectionAdditive;
        private Point pressPoint;
        private bool pressMoved;

        // 画面上の点にあるセルの番号（無ければ0）。シート表示（画像＋セル）のときだけ有効。
        public int HitTestCell(Point screenPoint)
        {
            if (image == null || sceneSprite != null || rects.Count == 0) return 0;
            float z = GetEffectiveZoom();
            PointF world = ScreenToWorld(screenPoint, z, GetEffectivePan(z));
            int x = (int)Math.Floor(world.X), y = (int)Math.Floor(world.Y);
            foreach (PreviewRect r in rects)
                if (r.Rect.Contains(x, y)) return r.Number;
            return 0;
        }

        private void RaiseCellGesture(CellGestureKind kind, int startCell, int cell, bool additive)
        {
            EventHandler<CellGestureEventArgs> handler = CellGesture;
            if (handler != null)
                handler(this, new CellGestureEventArgs { Kind = kind, StartCell = startCell, Cell = cell, Additive = additive });
        }

        // シートの外側（上と左）に列番号・行番号を出す。既定はオフ。
        private bool showAxisNumbers;
        public bool ShowAxisNumbers
        {
            get { return showAxisNumbers; }
            set
            {
                if (showAxisNumbers == value) return;
                showAxisNumbers = value;
                RaiseZoomChanged();
                Invalidate();
            }
        }

        private const int AxisMarginLeft = 30;
        private const int AxisMarginTop = 24;

        private bool AxisNumbersActive
        {
            get { return showAxisNumbers && image != null && sceneSprite == null && rects.Count > 0; }
        }

        public PreviewCanvas()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = Color.FromArgb(17, 23, 28);
            TabStop = true;
            DoubleClick += (s, e) => ResetToFit();
        }

        public float ZoomPercent
        {
            get
            {
                if (zoom <= 0.0f)
                {
                    return 0.0f;
                }
                return zoom * 100.0f;
            }
        }

        public bool IsFitMode
        {
            get { return zoom <= 0.0f; }
        }

        public void SetImage(Bitmap newImage, List<PreviewRect> newRects)
        {
            SetImage(newImage, newRects, Size.Empty);
        }

        // 表示中の画像の一部を、独立した複製として取り出す（範囲は画像の内側に切り詰める）。画像がなければ null。
        public Bitmap CloneImageRegion(Rectangle region)
        {
            if (image == null) return null;
            region.Intersect(new Rectangle(Point.Empty, image.Size));
            if (region.Width <= 0 || region.Height <= 0) return new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            return image.Clone(region, PixelFormat.Format32bppArgb);
        }

        // world は、画像が表す本来の大きさ（座標・セルの矩形はこの大きさの座標で持つ）。
        // 画像そのものが縮小プレビューのときに、その原寸を渡す。Empty なら画像の大きさ。
        public void SetImage(Bitmap newImage, List<PreviewRect> newRects, Size world)
        {
            if (image != null)
            {
                image.Dispose();
            }

            image = newImage;
            worldSize = world;
            sceneSprite = null;
            sceneSize = Size.Empty;

            rects.Clear();
            if (newRects != null)
            {
                rects.AddRange(newRects);
            }

            Invalidate();
        }

        // SetImageと違い、sourceの所有権は呼び出し側に残したまま内容だけを
        // 複製する。アニメーション再生のように毎フレーム呼ばれる場合、寸法が
        // 変わらない限り内部バッファを使い回すことで、Bitmapオブジェクトの
        // 再生成（ハンドル確保）を避ける。
        public void SetImageFromSource(Bitmap source, List<PreviewRect> newRects)
        {
            SetImageFromSource(source, newRects, Size.Empty);
        }

        public void SetImageFromSource(Bitmap source, List<PreviewRect> newRects, Size world)
        {
            if (source == null)
            {
                SetImage(null, newRects);
                return;
            }
            worldSize = world;

            if (image == null || image.Width != source.Width || image.Height != source.Height ||
                image.PixelFormat != source.PixelFormat)
            {
                if (image != null) image.Dispose();
                image = new Bitmap(source.Width, source.Height, source.PixelFormat);
            }

            using (Graphics g = Graphics.FromImage(image))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }

            sceneSprite = null;
            sceneSize = Size.Empty;

            rects.Clear();
            if (newRects != null)
            {
                rects.AddRange(newRects);
            }

            Invalidate();
        }

        // 見本地形（スロープ・直角の崖・足場）を描く。null なら平らな床だけ。
        public void SetTerrain(Terrain newTerrain)
        {
            if (ReferenceEquals(terrain, newTerrain)) return;
            terrain = newTerrain;
            Invalidate();
        }

        public void SetScene(Bitmap sprite, Size logicalSceneSize, PointF logicalPosition,
            Size logicalSpriteSize, Color floorColor, float floorRatio, bool mirrorHorizontally)
        {
            bool wasScene = sceneSprite != null && !sceneSize.IsEmpty && image == null;
            RectangleF previousSprite = sceneSpriteRect;
            Size previousScene = sceneSize;
            Color previousFloorColor = sceneFloorColor;
            float previousRatio = sceneFloorRatio;

            if (image != null)
            {
                image.Dispose();
                image = null;
            }

            sceneSprite = sprite;
            sceneSize = logicalSceneSize;
            sceneSpriteRect = new RectangleF(logicalPosition, logicalSpriteSize);
            sceneFloorColor = floorColor;
            sceneFloorRatio = Math.Max(0, Math.Min(1, floorRatio));
            sceneMirrorHorizontally = mirrorHorizontally;
            rects.Clear();

            // キャラクターだけが動いたときは、前の位置と今の位置を含む範囲だけを描き直す。
            bool onlySpriteChanged = wasScene && previousScene == sceneSize && previousFloorColor == sceneFloorColor &&
                previousRatio == sceneFloorRatio && sprite != null && Width > 0 && Height > 0;
            if (onlySpriteChanged)
            {
                RectangleF dirty = SheetToScreen(RectangleF.Union(previousSprite, sceneSpriteRect));
                Rectangle area = Rectangle.Ceiling(RectangleF.Inflate(dirty, 3, 3));
                area.Intersect(ClientRectangle);
                if (area.Width > 0 && area.Height > 0) Invalidate(area);
                return;
            }
            Invalidate();
        }

        public void SetRectColors(Func<int, Color> colorSelector)
        {
            foreach (PreviewRect rect in rects)
                rect.BorderColor = colorSelector == null ? Color.Empty : colorSelector(rect.Number);
            Invalidate();
        }

        public void SetBackgroundPalette(Color background, Color checkerA, Color checkerB)
        {
            BackColor = background;
            checkerColorA = checkerA;
            checkerColorB = checkerB;
            Invalidate();
        }

        public void ResetToFit()
        {
            zoom = 0.0f;
            pan = new PointF(0, 0);
            RaiseZoomChanged();
            Invalidate();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Focus();

            if (!HasContent)
            {
                return;
            }

            float oldZoom = GetEffectiveZoom();
            PointF oldPan = GetEffectivePan(oldZoom);

            float currentPercent = ZoomPercent;
            if (currentPercent <= 0.0f)
            {
                currentPercent = oldZoom * 100.0f;
            }

            float nextPercent = currentPercent + (e.Delta > 0 ? 5.0f : -5.0f);

            if (nextPercent <= 5.0f)
            {
                zoom = 0.0f;
                pan = new PointF(0, 0);
                RaiseZoomChanged();
                Invalidate();
                return;
            }

            nextPercent = Math.Max(5.0f, Math.Min(800.0f, nextPercent));
            zoom = nextPercent / 100.0f;

            PointF worldBefore = ScreenToWorld(e.Location, oldZoom, oldPan);
            PointF worldAfter = ScreenToWorld(e.Location, zoom, oldPan);

            pan = oldPan;
            pan.X += (worldAfter.X - worldBefore.X) * zoom;
            pan.Y += (worldAfter.Y - worldBefore.Y) * zoom;

            RaiseZoomChanged();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            pressPoint = e.Location;
            pressMoved = false;

            if (e.Button == MouseButtons.Left && MemosActive)
            {
                bool grab;
                SheetMemo memo = HitTestMemo(e.Location, out grab);
                if (memo != null)
                {
                    Focus();
                    if (grab)
                    {
                        PointF at = ScreenToSheet(e.Location);
                        draggingMemo = memo;
                        memoGrabOffset = new PointF(at.X - memo.X, at.Y - memo.Y);
                        memoMoved = false;
                        Memos.Remove(memo);                 // つかんだメモを手前へ
                        Memos.Add(memo);
                        Cursor = Cursors.SizeAll;
                    }
                    else if (MemoEditRequested != null) MemoEditRequested(this, new MemoEventArgs { Memo = memo });
                    return;
                }
            }

            // Shift+左ボタン: 画面移動ではなくセル選択。
            if (e.Button == MouseButtons.Left && (CurrentModifiers & Keys.Shift) != 0 && CellGesture != null &&
                image != null && sceneSprite == null && rects.Count > 0)
            {
                Focus();
                bool ctrl = (CurrentModifiers & Keys.Control) != 0;
                int cell = HitTestCell(e.Location);
                if (cell == 0)
                {
                    RaiseCellGesture(CellGestureKind.Clear, 0, 0, false);
                    return;
                }
                selectingCells = true;
                selectionStartCell = selectionLastCell = cell;
                selectionAdditive = ctrl;
                // Ctrl+Shift は「クリックなら範囲追加、ドラッグなら追加選択」。動くまで確定しない。
                selectionRangeClick = ctrl;
                selectionBegan = !ctrl;
                if (selectionBegan) RaiseCellGesture(CellGestureKind.Begin, cell, cell, false);
                return;
            }

            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                Focus();
                dragging = true;
                dragButton = e.Button;
                lastMouse = e.Location;
                pan = GetEffectivePan(GetEffectiveZoom());
                if (IsFitMode)
                {
                    zoom = GetEffectiveZoom();
                }
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
            {
                ResetToFit();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (draggingMemo != null)
            {
                PointF at = ScreenToSheet(e.Location);
                draggingMemo.X = MemoText.SafeCoordinate(at.X - memoGrabOffset.X);
                draggingMemo.Y = MemoText.SafeCoordinate(at.Y - memoGrabOffset.Y);
                memoMoved = true;
                if (MemoMoved != null) MemoMoved(this, new MemoEventArgs { Memo = draggingMemo });
                Invalidate();
                return;
            }
            if (!dragging && !selectingCells && MemosActive)
            {
                bool hoverGrab;
                SheetMemo hover = HitTestMemo(e.Location, out hoverGrab);
                Cursor = hover == null ? Cursors.Default : hoverGrab ? Cursors.SizeAll : Cursors.IBeam;
            }

            if (selectingCells)
            {
                int cell = HitTestCell(e.Location);
                if (cell != 0 && cell != selectionLastCell)
                {
                    if (!selectionBegan)
                    {
                        selectionBegan = true;
                        RaiseCellGesture(CellGestureKind.Begin, selectionStartCell, selectionStartCell, selectionAdditive);
                    }
                    selectionLastCell = cell;
                    RaiseCellGesture(CellGestureKind.Drag, selectionStartCell, cell, selectionAdditive);
                }
                return;
            }

            if (dragging)
            {
                if (Math.Abs(e.X - pressPoint.X) > 3 || Math.Abs(e.Y - pressPoint.Y) > 3) pressMoved = true;
                pan.X += e.X - lastMouse.X;
                pan.Y += e.Y - lastMouse.Y;
                lastMouse = e.Location;
                RaiseZoomChanged();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (draggingMemo != null && e.Button == MouseButtons.Left)
            {
                SheetMemo finished = draggingMemo;
                draggingMemo = null;
                Cursor = Cursors.Default;
                if (memoMoved && MemoMoveFinished != null) MemoMoveFinished(this, new MemoEventArgs { Memo = finished });
                return;
            }

            if (selectingCells && e.Button == MouseButtons.Left)
            {
                selectingCells = false;
                if (selectionRangeClick && !selectionBegan)
                    RaiseCellGesture(CellGestureKind.RangeClick, selectionStartCell, selectionStartCell, true);
                else
                    RaiseCellGesture(CellGestureKind.End, selectionStartCell, selectionLastCell, selectionAdditive);
                return;
            }

            if (e.Button == dragButton && dragging)
            {
                dragging = false;
                Cursor = Cursors.Default;
                // 動かさずに離した（クリック）で、セル以外なら選択を解除する。
                if (e.Button == MouseButtons.Left && !pressMoved && CellGesture != null && HitTestCell(e.Location) == 0
                    && image != null && sceneSprite == null && rects.Count > 0)
                    RaiseCellGesture(CellGestureKind.Clear, 0, 0, false);
            }
        }

        public int CornerRadius { get; set; }
        public Color BorderColor { get; set; } = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintContent(e);
            PaintRoundedCorners(e.Graphics);
        }

        // 四隅を親の背景色でアンチエイリアス付きに塗り戻し、キャンバスを角丸に見せる
        // （Regionはハードクリップでジャギーになるため使わない）。
        private void PaintRoundedCorners(Graphics g)
        {
            if (CornerRadius <= 0 || Width < 2 || Height < 2) return;
            Color backdrop = Parent != null ? Parent.BackColor : BackColor;
            int r = Math.Min(CornerRadius, Math.Min(Width, Height) / 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var inner = MainForm.CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), r))
            using (var outer = new GraphicsPath())
            using (var brush = new SolidBrush(backdrop))
            {
                outer.AddRectangle(new Rectangle(-1, -1, Width + 2, Height + 2));
                outer.AddPath(inner, false);
                g.FillPath(brush, outer);
                if (BorderColor != Color.Empty)
                    using (var pen = new Pen(BorderColor))
                        g.DrawPath(pen, inner);
            }
        }

        private void PaintContent(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;

            if (!HasContent)
            {
                g.Clear(BackColor);
                return;
            }

            float effectiveZoom = GetEffectiveZoom();
            PointF effectivePan = GetEffectivePan(effectiveZoom);

            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;

            // 背景（市松模様・足場）は変わらない限り作り置きの画像を貼るだけにする。
            // キャラクターが動くたびの再描画では、キャラクターと床の縁だけを描き直せば済む。
            bool isScene = sceneSprite != null && !sceneSize.IsEmpty;
            EnsureBackdropLayers(effectivePan, effectiveZoom, isScene);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(underLayer, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;

            if (isScene)
            {
                RectangleF spriteDestination = new RectangleF(
                    effectivePan.X + sceneSpriteRect.X * effectiveZoom,
                    effectivePan.Y + sceneSpriteRect.Y * effectiveZoom,
                    sceneSpriteRect.Width * effectiveZoom,
                    sceneSpriteRect.Height * effectiveZoom);
                if (sceneMirrorHorizontally)
                {
                    PointF[] destinationPoints =
                    {
                        new PointF(spriteDestination.Right, spriteDestination.Top),
                        new PointF(spriteDestination.Left, spriteDestination.Top),
                        new PointF(spriteDestination.Right, spriteDestination.Bottom)
                    };
                    g.DrawImage(sceneSprite, destinationPoints,
                        new RectangleF(0, 0, sceneSprite.Width, sceneSprite.Height), GraphicsUnit.Pixel);
                }
                else
                {
                    g.DrawImage(sceneSprite, spriteDestination,
                        new RectangleF(0, 0, sceneSprite.Width, sceneSprite.Height), GraphicsUnit.Pixel);
                }

                if (overLayer != null)
                    g.DrawImage(overLayer, new Rectangle(0, overTop, Width, Height - overTop),
                        new Rectangle(0, overTop, Width, Height - overTop), GraphicsUnit.Pixel);
            }
            else
            {
                Size world = WorldSize;
                RectangleF dst = new RectangleF(
                    effectivePan.X,
                    effectivePan.Y,
                    world.Width * effectiveZoom,
                    world.Height * effectiveZoom);

                g.DrawImage(image, dst, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
                DrawRects(g, effectivePan, effectiveZoom);
            }
        }

        // ---- 背景の作り置き ----
        private struct LayerKey : IEquatable<LayerKey>
        {
            public int Width, Height, CheckerA, CheckerB, Back, SceneWidth, SceneHeight, Floor;
            public float Zoom, PanX, PanY, Ratio;
            public bool Scene, ShowFloor;

            public bool Equals(LayerKey o)
            {
                return Width == o.Width && Height == o.Height && CheckerA == o.CheckerA && CheckerB == o.CheckerB &&
                    Back == o.Back && SceneWidth == o.SceneWidth && SceneHeight == o.SceneHeight && Floor == o.Floor &&
                    Zoom == o.Zoom && PanX == o.PanX && PanY == o.PanY && Ratio == o.Ratio && Scene == o.Scene && ShowFloor == o.ShowFloor;
            }
        }

        // 足場（床・地形）を描くか。エフェクトのプレビューでは不要なので切る。
        private bool showFloor = true;

        public bool ShowFloor
        {
            get { return showFloor; }
            set
            {
                if (showFloor == value) return;
                showFloor = value;
                Invalidate();
            }
        }

        private Bitmap underLayer;     // 背景色・市松模様・足場（キャラクターの後ろ）
        private Bitmap overLayer;      // 床・スロープ・崖（キャラクターの前。透過）
        private int overTop;           // overLayerの描く範囲の上端（画面のy）
        private LayerKey layerKey;
        private Terrain layerTerrain;

        private void EnsureBackdropLayers(PointF pan, float zoom, bool scene)
        {
            var key = new LayerKey
            {
                Width = Width, Height = Height, CheckerA = checkerColorA.ToArgb(), CheckerB = checkerColorB.ToArgb(),
                Back = BackColor.ToArgb(), SceneWidth = sceneSize.Width, SceneHeight = sceneSize.Height,
                Floor = sceneFloorColor.ToArgb(), Zoom = zoom, PanX = pan.X, PanY = pan.Y, Ratio = sceneFloorRatio, Scene = scene, ShowFloor = showFloor
            };
            if (underLayer != null && layerKey.Equals(key) && ReferenceEquals(layerTerrain, terrain)) return;

            if (underLayer != null) underLayer.Dispose();
            if (overLayer != null) overLayer.Dispose();
            underLayer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
            overLayer = null;
            using (Graphics g = Graphics.FromImage(underLayer))
            {
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                DrawCheckerBackground(g, pan);
                if (scene) DrawPlatform(g, pan, zoom);
            }
            if (scene && showFloor)
            {
                overLayer = new Bitmap(underLayer.Width, underLayer.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(overLayer))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.None;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    DrawFloor(g, pan, zoom);
                }
                float groundTop = terrain != null
                    ? terrain.GroundTop
                    : sceneSize.Height - (float)Math.Round(sceneSize.Height * sceneFloorRatio);
                float highest = terrain != null ? Math.Min(terrain.PeakTop, groundTop) : groundTop;
                overTop = Math.Max(0, Math.Min(underLayer.Height - 1, (int)Math.Floor(pan.Y + highest * zoom) - 6));
            }
            layerKey = key;
            layerTerrain = terrain;
        }

        // 床・スロープ・崖: UIのダーク×紫のスタイルに合わせた石畳。地面の縁だけアクセント色で強調する。
        // 床はシーンの外（キャンバスの左右・下いっぱい）まで広げ、厚みはシーンの高さの一定割合。
        // テクスチャは論理ピクセル(シーン座標)で作り、ズームに合わせて拡大してドット感を保つ。
        private void DrawFloor(Graphics g, PointF pan, float zoom)
        {
            float groundTop = terrain != null
                ? terrain.GroundTop
                : sceneSize.Height - (float)Math.Round(sceneSize.Height * sceneFloorRatio);
            float topY = pan.Y + groundTop * zoom;
            float left = 0, right = Width, bottom = Height + 2;
            if (floorTile == null) floorTile = CreateFloorTile();

            // 地面の輪郭: 左から、平ら → スロープ → 直角の崖 → 平ら。
            var top = new List<PointF> { new PointF(left, topY) };
            if (terrain != null)
            {
                float slopeStart = pan.X + terrain.SlopeStartX * zoom, slopeEnd = pan.X + terrain.SlopeEndX * zoom;
                float peakY = pan.Y + terrain.PeakTop * zoom;
                top.Add(new PointF(slopeStart, topY));
                top.Add(new PointF(slopeEnd, peakY));
                top.Add(new PointF(slopeEnd, topY));
            }
            top.Add(new PointF(right, topY));

            var outline = new List<PointF>(top);
            outline.Add(new PointF(right, bottom));
            outline.Add(new PointF(left, bottom));

            GraphicsState state = g.Save();
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(outline.ToArray());
                g.SetClip(path);
                using (var brush = new TextureBrush(floorTile, WrapMode.Tile))
                {
                    brush.ResetTransform();
                    brush.TranslateTransform(pan.X, pan.Y + groundTop * zoom);
                    brush.ScaleTransform(zoom, zoom);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.FillPath(brush, path);
                }
                float shadeTop = terrain != null ? pan.Y + terrain.PeakTop * zoom : topY;
                using (var shade = new LinearGradientBrush(new RectangleF(0, shadeTop, Width, Math.Max(2, bottom - shadeTop)),
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 8, 10, 22), LinearGradientMode.Vertical))
                    g.FillPath(shade, path);
            }
            g.Restore(state);

            // 縁: 上面（スロープを含む）をアクセント色、崖の壁は少し薄く。
            float edge = Math.Max(1, 2 * zoom);
            SmoothingMode previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var accent = new Pen(sceneFloorColor, edge) { LineJoin = LineJoin.Round })
            using (var wall = new Pen(Color.FromArgb(120, sceneFloorColor), Math.Max(1, zoom)))
            {
                for (int i = 0; i + 1 < top.Count; i++)
                {
                    bool isWall = top[i].X == top[i + 1].X;
                    g.DrawLine(isWall ? wall : accent, top[i], top[i + 1]);
                }
            }
            g.SmoothingMode = previousSmoothing;
        }

        // すり抜けられる足場（下からは上がれて、上に乗れる）。
        private void DrawPlatform(Graphics g, PointF pan, float zoom)
        {
            if (terrain == null) return;
            var rect = new RectangleF(pan.X + terrain.PlatformLeft * zoom, pan.Y + terrain.PlatformTop * zoom,
                (terrain.PlatformRight - terrain.PlatformLeft) * zoom, (terrain.PlatformBottom - terrain.PlatformTop) * zoom);
            if (floorTile == null) floorTile = CreateFloorTile();
            GraphicsState state = g.Save();
            g.SetClip(rect);
            using (var brush = new TextureBrush(floorTile, WrapMode.Tile))
            {
                brush.ResetTransform();
                brush.TranslateTransform(rect.X, rect.Y);
                brush.ScaleTransform(zoom, zoom);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillRectangle(brush, rect);
            }
            using (var shade = new LinearGradientBrush(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height + 1),
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(110, 8, 10, 22), LinearGradientMode.Vertical))
                g.FillRectangle(shade, rect);
            g.Restore(state);
            float edge = Math.Max(1, 2 * zoom);
            using (var accent = new SolidBrush(sceneFloorColor))
                g.FillRectangle(accent, rect.X, rect.Y, rect.Width, edge);
        }

        private static Bitmap CreateFloorTile()
        {
            // 32x12: 6px高のレンガを2段、段ごとに半分ずらして敷く。
            const int tileWidth = 32, tileHeight = 12, brickHeight = 6, brickWidth = 16;
            Color mortar = Color.FromArgb(22, 25, 40);
            Color[] bricks = { Color.FromArgb(38, 42, 66), Color.FromArgb(43, 48, 76) };
            Color highlight = Color.FromArgb(62, 68, 104);
            Color shadow = Color.FromArgb(29, 32, 52);
            Color speck = Color.FromArgb(54, 60, 94);
            var tile = new Bitmap(tileWidth, tileHeight);
            for (int y = 0; y < tileHeight; y++)
            {
                int row = y / brickHeight;
                int localY = y % brickHeight;
                int offset = row == 0 ? 0 : brickWidth / 2;
                for (int x = 0; x < tileWidth; x++)
                {
                    int bx = (x + offset) % tileWidth;
                    int localX = bx % brickWidth;
                    int brickIndex = (bx / brickWidth + row) % 2;
                    Color c = bricks[brickIndex];
                    if (localX == 0 || localY == brickHeight - 1) c = mortar;
                    else if (localY == 0) c = highlight;
                    else if (localY == brickHeight - 2) c = shadow;
                    else if ((localX * 7 + localY * 13 + row * 5) % 23 == 0) c = speck;
                    tile.SetPixel(x, y, c);
                }
            }
            return tile;
        }

        private bool HasContent
        {
            get { return image != null || (sceneSprite != null && !sceneSize.IsEmpty); }
        }

        private Size ContentSize
        {
            get { return sceneSprite != null && !sceneSize.IsEmpty ? sceneSize : image == null ? Size.Empty : WorldSize; }
        }

        private Size WorldSize
        {
            get { return worldSize.IsEmpty ? image.Size : worldSize; }
        }

        private float GetEffectiveZoom()
        {
            Size content = ContentSize;
            if (content.IsEmpty)
            {
                return 1.0f;
            }

            if (zoom > 0.0f)
            {
                return zoom;
            }

            if (content.Width <= 0 || content.Height <= 0 || Width <= 0 || Height <= 0)
            {
                return 1.0f;
            }

            int marginLeft = AxisNumbersActive ? AxisMarginLeft : 0;
            int marginTop = AxisNumbersActive ? AxisMarginTop : 0;
            float zx = (Width - 16 - marginLeft) / (float)content.Width;
            float zy = (Height - 16 - marginTop) / (float)content.Height;
            float fit = Math.Min(zx, zy);

            return Math.Max(0.001f, fit);
        }

        private PointF GetEffectivePan(float effectiveZoom)
        {
            Size content = ContentSize;
            if (content.IsEmpty)
            {
                return pan;
            }

            if (zoom > 0.0f)
            {
                return pan;
            }

            float w = content.Width * effectiveZoom;
            float h = content.Height * effectiveZoom;

            float leftMargin = AxisNumbersActive ? AxisMarginLeft : 0;
            float topMargin = AxisNumbersActive ? AxisMarginTop : 0;
            return new PointF(
                leftMargin + (Width - leftMargin - w) * 0.5f,
                topMargin + (Height - topMargin - h) * 0.5f);
        }

        private void DrawCheckerBackground(Graphics g, PointF effectivePan)
        {
            const int screenSize = 16;

            using (var a = new SolidBrush(checkerColorA))
            using (var b = new SolidBrush(checkerColorB))
            {
                int startX = (int)Math.Floor(-effectivePan.X / screenSize) * screenSize + (int)effectivePan.X;
                int startY = (int)Math.Floor(-effectivePan.Y / screenSize) * screenSize + (int)effectivePan.Y;

                for (int y = startY; y < Height; y += screenSize)
                {
                    for (int x = startX; x < Width; x += screenSize)
                    {
                        int cx = (int)Math.Floor((x - effectivePan.X) / screenSize);
                        int cy = (int)Math.Floor((y - effectivePan.Y) / screenSize);
                        bool even = ((cx + cy) & 1) == 0;
                        g.FillRectangle(even ? a : b, x, y, screenSize, screenSize);
                    }
                }
            }
        }

        private void DrawRects(Graphics g, PointF effectivePan, float effectiveZoom)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var font = UiFont.Create(15, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(Color.Black))
            using (var bgBrush = new SolidBrush(Color.White))
            {
                // 番号バッジは最も桁数の多い番号に合わせた共通サイズにし、最小のセルにも収まる
                // ときだけ「全セル」に出す。桁数やセルの大きさで出る/出ないが混ざると、
                // 拡大の途中で一部の番号だけが現れて視認性を損なうため。
                int widestNumber = 0;
                float smallestWidth = float.MaxValue, smallestHeight = float.MaxValue;
                foreach (PreviewRect r in rects)
                {
                    widestNumber = Math.Max(widestNumber, r.Number);
                    smallestWidth = Math.Min(smallestWidth, r.Rect.Width * effectiveZoom);
                    smallestHeight = Math.Min(smallestHeight, r.Rect.Height * effectiveZoom);
                }
                SizeF badgeSize = g.MeasureString(widestNumber.ToString(), font);
                bool showBadges = rects.Count > 0 &&
                    badgeSize.Width + 6 <= smallestWidth * 0.5f && badgeSize.Height + 4 <= smallestHeight * 0.5f;

                foreach (PreviewRect r in rects)
                {
                    RectangleF sr = new RectangleF(
                        effectivePan.X + r.Rect.X * effectiveZoom,
                        effectivePan.Y + r.Rect.Y * effectiveZoom,
                        Math.Max(1, r.Rect.Width * effectiveZoom),
                        Math.Max(1, r.Rect.Height * effectiveZoom));

                    Color border = r.BorderColor == Color.Empty
                        ? Color.FromArgb(225, 230, 234)
                        : r.BorderColor;
                    using (var pen = new Pen(border, r.BorderColor == Color.Empty ? 1.0f : 2.5f))
                        g.DrawRectangle(pen, sr.X, sr.Y, sr.Width, sr.Height);

                    if (IsCellSelected != null && IsCellSelected(r.Number))
                    {
                        using (var fill = new SolidBrush(Color.FromArgb(80, 84, 73, 255)))
                            g.FillRectangle(fill, sr.X, sr.Y, sr.Width, sr.Height);
                        using (var selectedPen = new Pen(Color.FromArgb(150, 140, 255), 3f))
                            g.DrawRectangle(selectedPen, sr.X + 1, sr.Y + 1, Math.Max(1, sr.Width - 2), Math.Max(1, sr.Height - 2));
                    }

                    // 縮小表示でセルが小さいと、一定サイズの番号バッジがセルを覆って
                    // 画像が見えなくなる。バッジがセルの半分を超える間は番号を出さない
                    // （拡大すると全セルに一斉に現れる。セルの枠線は常に表示する）。
                    if (!showBadges) continue;

                    // 番号は画面上の一定サイズ。ズームしても極端に小さくならない。
                    string text = r.Number.ToString();
                    float x = sr.X + 3;
                    float y = sr.Y + 3;

                    g.FillRectangle(bgBrush, x, y, badgeSize.Width + 6, badgeSize.Height + 4);
                    g.DrawString(text, font, textBrush, x + 3, y + 2);
                }
            }

            if (AxisNumbersActive) DrawAxisNumbers(g, effectivePan, effectiveZoom);
            DrawMemos(g, effectivePan, effectiveZoom);
        }

        // 付箋: 灰色の面＋白寄りの灰色の縁の角丸。文字はコメントのような緑。シート本体にも重なる。
        private void DrawMemos(Graphics g, PointF pan, float zoom)
        {
            if (!MemosActive) return;
            SmoothingMode previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (SheetMemo memo in Memos)
            {
                var rect = new RectangleF(pan.X + memo.X * zoom, pan.Y + memo.Y * zoom, memo.Width * zoom, memo.Height * zoom);
                if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > Width || rect.Top > Height) continue;
                float radius = Math.Max(1f, MemoText.CornerRadius(memo.Width) * zoom);
                bool editing = ReferenceEquals(memo, EditingMemo);
                using (GraphicsPath shadow = CreateMemoPath(new RectangleF(rect.X + 2, rect.Y + 3, rect.Width, rect.Height), radius))
                using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillPath(shadowBrush, shadow);
                using (GraphicsPath path = CreateMemoPath(rect, radius))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(72, 76, 82)))
                        g.FillPath(fill, path);
                    using (var border = new Pen(editing ? Color.FromArgb(150, 140, 255) : Color.FromArgb(205, 210, 215), editing ? 2f : 1.5f))
                        g.DrawPath(border, path);
                }
                if (editing || string.IsNullOrEmpty(memo.Text)) continue;

                float fontPixels = MemoText.FontSize(memo.Width) * zoom;
                float pad = MemoText.Padding(memo.Width) * zoom;
                float lineStep = memo.LineHeight * zoom;
                if (fontPixels < 3.5f)
                {
                    // 小さすぎて読めない間は、文字の代わりに薄い線で「書いてある」ことだけ示す。
                    using (var hint = new Pen(Color.FromArgb(140, 106, 170, 89), Math.Max(1f, fontPixels * 0.5f)))
                        for (int i = 0; i < memo.Lines.Count; i++)
                        {
                            if (memo.Lines[i].Length == 0) continue;
                            float y = rect.Y + pad + (i + 0.5f) * lineStep;
                            g.DrawLine(hint, rect.X + pad, y, rect.X + pad + Math.Max(2f, rect.Width - 2 * pad) * Math.Min(1f, memo.Lines[i].Length / 10f), y);
                        }
                    continue;
                }
                using (Font font = UiFont.Create(fontPixels, FontStyle.Regular, GraphicsUnit.Pixel))
                    for (int i = 0; i < memo.Lines.Count; i++)
                        TextRenderer.DrawText(g, memo.Lines[i], font,
                            new Rectangle((int)Math.Round(rect.X + pad), (int)Math.Round(rect.Y + pad + i * lineStep),
                                (int)Math.Ceiling(rect.Width - pad) + 2, (int)Math.Ceiling(lineStep) + 2),
                            MemoTextColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                            TextFormatFlags.Left | TextFormatFlags.Top);
            }
            g.SmoothingMode = previousSmoothing;
        }

        // コメントのような緑色。
        public static readonly Color MemoTextColor = Color.FromArgb(106, 170, 89);

        private static GraphicsPath CreateMemoPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 1行目のセルの上に列番号、各行の先頭セルの左に行番号を、シートの外側へ描く（1始まり）。
        // 小さすぎて隣と重なるときは、番号の一部だけが出ないよう全て出さない。
        private void DrawAxisNumbers(Graphics g, PointF pan, float zoom)
        {
            var rows = new List<List<PreviewRect>>();
            foreach (PreviewRect r in rects.OrderBy(c => c.Rect.Y).ThenBy(c => c.Rect.X))
            {
                if (rows.Count == 0 || r.Rect.Y >= rows[rows.Count - 1][0].Rect.Bottom)
                    rows.Add(new List<PreviewRect>());
                rows[rows.Count - 1].Add(r);
            }
            if (rows.Count == 0) return;

            using (var font = UiFont.Create(12, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(205, 212, 218)))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                SizeF columnText = g.MeasureString(rows.Max(row => row.Count).ToString(), font);
                SizeF rowText = g.MeasureString(rows.Count.ToString(), font);
                float narrowestCell = rows[0].Min(c => c.Rect.Width) * zoom;
                float shortestRow = rows.Min(row => row.Min(c => c.Rect.Height)) * zoom;
                var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var rightAligned = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

                if (narrowestCell >= columnText.Width + 4)
                {
                    int column = 1;
                    foreach (PreviewRect cell in rows[0].OrderBy(c => c.Rect.X))
                    {
                        float centerX = pan.X + (cell.Rect.X + cell.Rect.Width / 2f) * zoom;
                        g.DrawString(column.ToString(), font, brush,
                            new RectangleF(centerX - 30, pan.Y - AxisMarginTop, 60, AxisMarginTop - 3), centered);
                        column++;
                    }
                }
                if (shortestRow >= rowText.Height + 2)
                {
                    int rowNumber = 1;
                    foreach (List<PreviewRect> row in rows)
                    {
                        float centerY = pan.Y + (row[0].Rect.Y + row[0].Rect.Height / 2f) * zoom;
                        g.DrawString(rowNumber.ToString(), font, brush,
                            new RectangleF(pan.X - AxisMarginLeft, centerY - 10, AxisMarginLeft - 5, 20), rightAligned);
                        rowNumber++;
                    }
                }
                centered.Dispose();
                rightAligned.Dispose();
            }
        }

        private static PointF ScreenToWorld(Point screen, float z, PointF p)
        {
            return new PointF((screen.X - p.X) / z, (screen.Y - p.Y) / z);
        }

        private void RaiseZoomChanged()
        {
            if (ZoomChanged != null)
            {
                ZoomChanged(this, EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null)
            {
                image.Dispose();
                image = null;
            }
            sceneSprite = null;
            if (disposing && floorTile != null)
            {
                floorTile.Dispose();
                floorTile = null;
            }
            if (disposing && underLayer != null) { underLayer.Dispose(); underLayer = null; }
            if (disposing && overLayer != null) { overLayer.Dispose(); overLayer = null; }

            base.Dispose(disposing);
        }
    }

    public enum CellGestureKind { Begin, Drag, End, RangeClick, Clear }

    public sealed class CellGestureEventArgs : EventArgs
    {
        public CellGestureKind Kind;
        public int StartCell;
        public int Cell;
        public bool Additive;
    }

    public sealed class PreviewRect
    {
        public Rectangle Rect;
        public int Number;
        public Color BorderColor;
    }
}
