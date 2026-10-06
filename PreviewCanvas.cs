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
        private RectangleF sceneColliderRect;      // キャラクターのコライダー（シーン座標）。空なら表示しない
        private RectangleF pendingColliderRect;
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

        // カーソルを置いたセルの位置の札（シート表示のときだけ）。
        private bool showCoordinates;
        public bool ShowCoordinates { get { return showCoordinates; } set { if (showCoordinates == value) return; showCoordinates = value; Invalidate(); } }
        public UvCoordinateFormat UvFormat { get; set; }
        private Point coordinateHover = new Point(-1, -1);

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
            DoubleClick += (s, e) => ResetToFitAnimated();
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

        // 全体表示へ戻る途中も「全体表示」として扱う（拡大率の表示などが先に100%になる）。
        public bool IsFitMode
        {
            get { return zoom <= 0.0f || fitAnimating; }
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

        // 次の SetScene で一緒に描くコライダー。表示しないときは RectangleF.Empty。
        public void SetSceneCollider(RectangleF logicalRect)
        {
            pendingColliderRect = logicalRect;
        }

        public RectangleF SceneCollider
        {
            get { return sceneColliderRect; }
        }

        public void SetScene(Bitmap sprite, Size logicalSceneSize, PointF logicalPosition,
            Size logicalSpriteSize, Color floorColor, float floorRatio, bool mirrorHorizontally)
        {
            bool wasScene = sceneSprite != null && !sceneSize.IsEmpty && image == null;
            RectangleF previousSprite = sceneSpriteRect;
            RectangleF previousCollider = sceneColliderRect;
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
            sceneColliderRect = pendingColliderRect;
            sceneFloorColor = floorColor;
            sceneFloorRatio = Math.Max(0, Math.Min(1, floorRatio));
            sceneMirrorHorizontally = mirrorHorizontally;
            rects.Clear();

            // キャラクターだけが動いたときは、前の位置と今の位置を含む範囲だけを描き直す。
            bool onlySpriteChanged = wasScene && previousScene == sceneSize && previousFloorColor == sceneFloorColor &&
                previousRatio == sceneFloorRatio && sprite != null && Width > 0 && Height > 0;
            if (onlySpriteChanged)
            {
                RectangleF changed = RectangleF.Union(previousSprite, sceneSpriteRect);
                if (!previousCollider.IsEmpty) changed = RectangleF.Union(changed, previousCollider);
                if (!sceneColliderRect.IsEmpty) changed = RectangleF.Union(changed, sceneColliderRect);
                RectangleF dirty = SheetToScreen(changed);
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

        //--------------
        // 全体表示へ戻る動き
        //--------------
        public const int FitAnimationMilliseconds = 200;
        private Timer fitTimer;
        private readonly System.Diagnostics.Stopwatch fitClock = new System.Diagnostics.Stopwatch();
        private bool fitAnimating;
        private float fitFromZoom;
        private PointF fitFromPan;

        // F キー・全体表示ボタンなど、利用者や自動の「全体表示に戻る」は0.2秒かけて戻す。
        // 表示前・中身が無い・すでに全体表示・Windowsのアニメーション効果がオフのときは、すぐに戻す。
        public void ResetToFitAnimated()
        {
            if (!HasContent || !IsHandleCreated || !Visible || zoom <= 0.0f || !SystemInformation.UIEffectsEnabled)
            {
                ResetToFit();
                return;
            }
            fitFromZoom = GetEffectiveZoom();
            fitFromPan = GetEffectivePan(fitFromZoom);
            fitAnimating = true;
            fitClock.Restart();
            if (fitTimer == null)
            {
                fitTimer = new Timer { Interval = 15 };
                fitTimer.Tick += (s, e) => StepFitAnimation();
            }
            fitTimer.Start();
            RaiseZoomChanged();
        }

        private void StepFitAnimation()
        {
            float t = PageTransitionOverlay.Ease(fitClock.ElapsedMilliseconds / (float)FitAnimationMilliseconds);
            if (t >= 1f || !HasContent)
            {
                ResetToFit();
                return;
            }
            // 行き先（全体表示の拡大率と位置）は窓の大きさで変わるので、毎回求め直す。
            zoom = 0.0f;
            float toZoom = GetEffectiveZoom();
            PointF toPan = GetEffectivePan(toZoom);
            zoom = fitFromZoom + (toZoom - fitFromZoom) * t;
            pan = new PointF(fitFromPan.X + (toPan.X - fitFromPan.X) * t, fitFromPan.Y + (toPan.Y - fitFromPan.Y) * t);
            Invalidate();
        }

        // ホイール・ドラッグなどで利用者が動かしたら、戻る動きはその場で止める。
        private void StopFitAnimation()
        {
            if (!fitAnimating) return;
            fitAnimating = false;
            if (fitTimer != null) fitTimer.Stop();
        }

        public void ResetToFit()
        {
            StopFitAnimation();
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
            StopFitAnimation();
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
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) StopFitAnimation();

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
                ResetToFitAnimated();
                e.Handled = true;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (coordinateHover.X >= 0) { coordinateHover = new Point(-1, -1); Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (showCoordinates && image != null && sceneSprite == null) { coordinateHover = e.Location; Invalidate(); }

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

                if (!sceneColliderRect.IsEmpty)
                {
                    var colliderDestination = new RectangleF(
                        effectivePan.X + sceneColliderRect.X * effectiveZoom,
                        effectivePan.Y + sceneColliderRect.Y * effectiveZoom,
                        sceneColliderRect.Width * effectiveZoom,
                        sceneColliderRect.Height * effectiveZoom);
                    using (var fill = new SolidBrush(Color.FromArgb(48, 80, 230, 120)))
                    using (var outline = new Pen(Color.FromArgb(230, 80, 230, 120), 1.5f))
                    {
                        g.FillRectangle(fill, colliderDestination);
                        g.DrawRectangle(outline, colliderDestination.X, colliderDestination.Y, colliderDestination.Width, colliderDestination.Height);
                    }
                }
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
                    Color.FromArgb(0, 0, 0, 0), TerrainShade, LinearGradientMode.Vertical))
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
                // 地面と同じ基準で敷き、方眼のマスが地面とそろうようにする。
                brush.ResetTransform();
                brush.TranslateTransform(pan.X, pan.Y + terrain.GroundTop * zoom);
                brush.ScaleTransform(zoom, zoom);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillRectangle(brush, rect);
            }
            using (var shade = new LinearGradientBrush(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height + 1),
                Color.FromArgb(0, 0, 0, 0), TerrainShade, LinearGradientMode.Vertical))
                g.FillRectangle(shade, rect);
            g.Restore(state);
            float edge = Math.Max(1, 2 * zoom);
            using (var accent = new SolidBrush(sceneFloorColor))
                g.FillRectangle(accent, rect.X, rect.Y, rect.Width, edge);
        }

        // 地形の質感: デバッグ用と割り切った方眼（Q-011 の回答「B。テーマカラーに合わせる」）。
        // 面はパネルの色、線は区切り線の色（MainForm の panelElevated・dividerColor と同じ）。
        private static readonly Color TerrainFill = Color.FromArgb(31, 40, 47);
        private static readonly Color TerrainGridLine = Color.FromArgb(63, 73, 81);
        private static readonly Color TerrainShade = Color.FromArgb(90, 12, 17, 22);   // 下へ向かって暗くする影
        private const int TerrainGridCell = 16;   // 1マスの大きさ（シーンの論理px。キャラクターの大きさの目安になる）

        private static Bitmap CreateFloorTile()
        {
            var tile = new Bitmap(TerrainGridCell, TerrainGridCell);
            for (int y = 0; y < TerrainGridCell; y++)
                for (int x = 0; x < TerrainGridCell; x++)
                    tile.SetPixel(x, y, x == 0 || y == 0 ? TerrainGridLine : TerrainFill);
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

        //--------------
        // 選択したセルの枠が一瞬ふくらんで戻る動き
        //--------------
        public const int SelectionPopMilliseconds = 180;
        private const float SelectionPopPixels = 4f;
        private HashSet<int> drawnSelectedCells = new HashSet<int>();
        private readonly Dictionary<int, System.Diagnostics.Stopwatch> selectionPops = new Dictionary<int, System.Diagnostics.Stopwatch>();
        private Timer selectionPopTimer;

        // 今回の描画で新しく選ばれたセルなら動きを始め、ふくらみの量（px）を返す。
        private float SelectionPopAmount(int cell, HashSet<int> selectedNow)
        {
            selectedNow.Add(cell);
            System.Diagnostics.Stopwatch clock;
            if (!drawnSelectedCells.Contains(cell) && UiMotion.Enabled && !selectionPops.ContainsKey(cell))
            {
                selectionPops[cell] = System.Diagnostics.Stopwatch.StartNew();
                if (selectionPopTimer == null)
                {
                    selectionPopTimer = new Timer { Interval = 15 };
                    selectionPopTimer.Tick += (s, e) =>
                    {
                        foreach (int done in selectionPops.Where(p => p.Value.ElapsedMilliseconds >= SelectionPopMilliseconds).Select(p => p.Key).ToList())
                            selectionPops.Remove(done);
                        if (selectionPops.Count == 0) selectionPopTimer.Stop();
                        Invalidate();
                    };
                }
                selectionPopTimer.Start();
            }
            if (!selectionPops.TryGetValue(cell, out clock)) return 0f;
            float t = Math.Min(1f, clock.ElapsedMilliseconds / (float)SelectionPopMilliseconds);
            return SelectionPopPixels * (float)Math.Sin(Math.PI * t);
        }

        private void DrawRects(Graphics g, PointF effectivePan, float effectiveZoom)
        {
            var selectedNow = new HashSet<int>();
            try
            {
                DrawRectsCore(g, effectivePan, effectiveZoom, selectedNow);
            }
            finally
            {
                drawnSelectedCells = selectedNow;
            }
        }

        private void DrawRectsCore(Graphics g, PointF effectivePan, float effectiveZoom, HashSet<int> selectedNow)
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
                        float pop = SelectionPopAmount(r.Number, selectedNow);
                        using (var fill = new SolidBrush(Color.FromArgb(80, 84, 73, 255)))
                            g.FillRectangle(fill, sr.X, sr.Y, sr.Width, sr.Height);
                        using (var selectedPen = new Pen(Color.FromArgb(150, 140, 255), 3f))
                            g.DrawRectangle(selectedPen, sr.X + 1 - pop, sr.Y + 1 - pop, Math.Max(1, sr.Width - 2 + pop * 2), Math.Max(1, sr.Height - 2 + pop * 2));
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
            if (showCoordinates && coordinateHover.X >= 0 && !dragging && !selectingCells && draggingMemo == null) DrawCoordinateCard(g, effectivePan, effectiveZoom);
        }

        // 付箋: 灰色の面＋白寄りの灰色の縁の角丸。文字はコメントのような緑。シート本体にも重なる。
        //--------------
        // メモの出入りの動き（追加: 小さい所から広がる / 削除: 縮んで消える）
        //--------------
        public const int MemoMotionMilliseconds = 180;
        private readonly Dictionary<SheetMemo, System.Diagnostics.Stopwatch> appearingMemos = new Dictionary<SheetMemo, System.Diagnostics.Stopwatch>();
        private readonly List<KeyValuePair<SheetMemo, System.Diagnostics.Stopwatch>> vanishingMemos = new List<KeyValuePair<SheetMemo, System.Diagnostics.Stopwatch>>();
        private Timer memoMotionTimer;

        public void AnimateMemoAppear(SheetMemo memo)
        {
            if (!UiMotion.Enabled || memo == null) return;
            appearingMemos[memo] = System.Diagnostics.Stopwatch.StartNew();
            StartMemoMotion();
        }

        // 一覧から外したメモを、縮みながら消えるように少しの間だけ描く。
        public void AnimateMemoVanish(SheetMemo memo)
        {
            if (!UiMotion.Enabled || memo == null) return;
            appearingMemos.Remove(memo);
            vanishingMemos.Add(new KeyValuePair<SheetMemo, System.Diagnostics.Stopwatch>(memo, System.Diagnostics.Stopwatch.StartNew()));
            StartMemoMotion();
        }

        public bool MemoMotionRunning
        {
            get { return appearingMemos.Count > 0 || vanishingMemos.Count > 0; }
        }

        private void StartMemoMotion()
        {
            if (memoMotionTimer == null)
            {
                memoMotionTimer = new Timer { Interval = 15 };
                memoMotionTimer.Tick += (s, e) =>
                {
                    foreach (SheetMemo done in appearingMemos.Where(p => p.Value.ElapsedMilliseconds >= MemoMotionMilliseconds).Select(p => p.Key).ToList())
                        appearingMemos.Remove(done);
                    vanishingMemos.RemoveAll(p => p.Value.ElapsedMilliseconds >= MemoMotionMilliseconds);
                    if (!MemoMotionRunning) memoMotionTimer.Stop();
                    Invalidate();
                };
            }
            memoMotionTimer.Start();
            Invalidate();
        }

        // 0〜1 の大きさ（1 が通常）。出入りの途中でなければ 1。
        private float MemoScale(SheetMemo memo, bool vanishing, System.Diagnostics.Stopwatch clock)
        {
            float t = PageTransitionOverlay.Ease(clock.ElapsedMilliseconds / (float)MemoMotionMilliseconds);
            return vanishing ? 1f - t : 0.55f + 0.45f * t;
        }

        private void DrawMemos(Graphics g, PointF pan, float zoom)
        {
            if (!MemosActive) return;
            SmoothingMode previousSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (KeyValuePair<SheetMemo, System.Diagnostics.Stopwatch> gone in vanishingMemos)
                DrawMemo(g, pan, zoom, gone.Key, MemoScale(gone.Key, true, gone.Value));
            foreach (SheetMemo memo in Memos)
            {
                System.Diagnostics.Stopwatch appearing;
                DrawMemo(g, pan, zoom, memo, appearingMemos.TryGetValue(memo, out appearing) ? MemoScale(memo, false, appearing) : 1f);
            }
            g.SmoothingMode = previousSmoothing;
        }

        // scale が 1 未満のときは中心を軸に縮めて描く（文字はその間だけ GDI+ で描く。TextRenderer は縮小に対応しないため）。
        private void DrawMemo(Graphics g, PointF pan, float zoom, SheetMemo memo, float scale)
        {
            if (scale <= 0.01f) return;
            {
                var rect = new RectangleF(pan.X + memo.X * zoom, pan.Y + memo.Y * zoom, memo.Width * zoom, memo.Height * zoom);
                if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > Width || rect.Top > Height) return;
                GraphicsState saved = null;
                if (scale < 1f)
                {
                    saved = g.Save();
                    float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;
                    g.TranslateTransform(cx, cy);
                    g.ScaleTransform(scale, scale);
                    g.TranslateTransform(-cx, -cy);
                }
                try
                {
                    DrawMemoBody(g, memo, rect, zoom, scale < 1f);
                }
                finally
                {
                    if (saved != null) g.Restore(saved);
                }
            }
        }

        private void DrawMemoBody(Graphics g, SheetMemo memo, RectangleF rect, float zoom, bool scaled)
        {
            {
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
                if (editing || string.IsNullOrEmpty(memo.Text)) return;

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
                    return;
                }
                using (Font font = UiFont.Create(fontPixels, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    if (scaled)
                    {
                        using (var brush = new SolidBrush(MemoTextColor))
                            for (int i = 0; i < memo.Lines.Count; i++)
                                g.DrawString(memo.Lines[i], font, brush, rect.X + pad, rect.Y + pad + i * lineStep, StringFormat.GenericTypographic);
                        return;
                    }
                    for (int i = 0; i < memo.Lines.Count; i++)
                        TextRenderer.DrawText(g, memo.Lines[i], font,
                            new Rectangle((int)Math.Round(rect.X + pad), (int)Math.Round(rect.Y + pad + i * lineStep),
                                (int)Math.Ceiling(rect.Width - pad) + 2, (int)Math.Ceiling(lineStep) + 2),
                            MemoTextColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                            TextFormatFlags.Left | TextFormatFlags.Top);
                }
            }
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

        // 列・行はシート上の並び（左から・上から 1 始まり）。px と UV はシート全体の大きさに対する位置。
        private void DrawCoordinateCard(Graphics g, PointF pan, float zoom)
        {
            if (image == null || sceneSprite != null || rects.Count == 0) return;
            PointF world = ScreenToWorld(coordinateHover, zoom, pan);
            PreviewRect hit = rects.FirstOrDefault(r => r.Rect.Contains((int)Math.Floor(world.X), (int)Math.Floor(world.Y)));
            if (hit == null) return;
            var rows = new List<List<PreviewRect>>();
            foreach (PreviewRect r in rects.OrderBy(c => c.Rect.Y).ThenBy(c => c.Rect.X))
            {
                if (rows.Count == 0 || r.Rect.Y >= rows[rows.Count - 1][0].Rect.Bottom) rows.Add(new List<PreviewRect>());
                rows[rows.Count - 1].Add(r);
            }
            int row = rows.FindIndex(list => list.Contains(hit));
            int column = rows[row].OrderBy(c => c.Rect.X).ToList().IndexOf(hit);
            string[] lines = CoordinateCard.Lines(hit.Number, (column + 1).ToString(), (row + 1).ToString(), Size.Empty, hit.Rect, WorldSize, UvFormat);
            using (Font font = UiFont.Create(12, FontStyle.Regular, GraphicsUnit.Pixel))
                CoordinateCard.Draw(g, ClientRectangle, coordinateHover, lines, font);
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
            if (disposing && fitTimer != null) { fitTimer.Dispose(); fitTimer = null; }
            if (disposing && memoMotionTimer != null) { memoMotionTimer.Dispose(); memoMotionTimer = null; }
            if (disposing && selectionPopTimer != null) { selectionPopTimer.Dispose(); selectionPopTimer = null; }
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
