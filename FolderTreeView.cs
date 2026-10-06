//==================================================
// FolderTreeView
// 左ペインのフォルダ一覧。Windows 標準の TreeView は行単位でしかスクロールできず、
// スクロールや開閉のたびにがたつき・ちらつきが出るため、自前で描く一覧に置き換えた。
// ・1px 単位のなめらかなスクロール（ホイールは目標位置へ減速しながら近づく）
// ・二重バッファで、見えている行だけを描く
// ・フォルダの開閉は、中の行が引き出し式に伸び縮みする（行の位置は毎フレーム計算）
// 行の見た目は DrawNode で呼び出し側が描く（MainForm.TreeView_DrawNode）。
//==================================================
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal sealed class FolderNode
    {
        public FolderNode(string text)
        {
            Text = text;
            Nodes = new FolderNodeCollection(null, this);
        }

        public string Text { get; set; }
        public object Tag { get; set; }
        public string ToolTipText { get; set; }
        public FolderNode Parent { get; internal set; }
        public FolderNodeCollection Nodes { get; private set; }
        internal FolderTreeView Owner;                       // 一覧の直下にあるときだけ入る

        public FolderTreeView TreeView { get { return Parent != null ? Parent.TreeView : Owner; } }
        public FolderNode LastNode { get { return Nodes.Count == 0 ? null : Nodes[Nodes.Count - 1]; } }
        public int Index
        {
            get
            {
                if (Parent != null) return Parent.Nodes.IndexOf(this);
                return Owner != null ? Owner.Nodes.IndexOf(this) : -1;
            }
        }

        //--------------
        // 開閉（openness は 0=閉じた〜1=開いた。動きの途中の値も持つ）
        //--------------
        public bool IsExpanded { get; private set; }
        private float openFrom, openTo;
        private double openAt = FolderTreeView.NoMotion;

        internal float Openness(double now)
        {
            float t = FolderTreeView.Progress(openAt, now, FolderTreeView.ToggleSeconds);
            return openFrom + (openTo - openFrom) * PageTransitionOverlay.Ease(t);
        }
        internal bool IsOpening(double now) { return now - openAt < FolderTreeView.ToggleSeconds; }

        public void Expand() { SetExpanded(true); }
        public void Collapse() { SetExpanded(false); }
        public void Toggle() { SetExpanded(!IsExpanded); }

        private void SetExpanded(bool expanded)
        {
            if (IsExpanded == expanded) return;
            FolderTreeView tree = TreeView;
            double now = FolderTreeView.Now;
            bool animate = tree != null && tree.CanAnimate && Nodes.Count > 0;
            // 動きの途中で開閉し直したら、今見えている開き具合から次へ動かす。
            openFrom = animate ? Openness(now) : (expanded ? 1f : 0f);
            openTo = expanded ? 1f : 0f;
            openAt = animate ? now : FolderTreeView.NoMotion;
            IsExpanded = expanded;
            if (tree != null) tree.OnToggled(this, animate);
        }

        public Rectangle Bounds { get { FolderTreeView tree = TreeView; return tree == null ? Rectangle.Empty : tree.BoundsOf(this); } }
        public FolderNode PrevVisibleNode { get { FolderTreeView tree = TreeView; return tree == null ? null : tree.StepVisible(this, -1); } }
        public FolderNode NextVisibleNode { get { FolderTreeView tree = TreeView; return tree == null ? null : tree.StepVisible(this, 1); } }
        public void EnsureVisible() { FolderTreeView tree = TreeView; if (tree != null) tree.EnsureVisible(this); }

        // 親がすべて開いているか（＝一覧に行として出るか）。
        internal bool IsShown
        {
            get
            {
                for (FolderNode p = Parent; p != null; p = p.Parent) if (!p.IsExpanded) return false;
                return true;
            }
        }
    }

    internal sealed class FolderNodeCollection : Collection<FolderNode>
    {
        private readonly FolderTreeView tree;
        private readonly FolderNode owner;
        internal FolderNodeCollection(FolderTreeView tree, FolderNode owner) { this.tree = tree; this.owner = owner; }

        private FolderTreeView Tree { get { return tree ?? (owner == null ? null : owner.TreeView); } }

        private void Attach(FolderNode node)
        {
            node.Parent = owner;
            node.Owner = owner == null ? tree : null;
        }
        private static void Detach(FolderNode node) { node.Parent = null; node.Owner = null; }

        protected override void InsertItem(int index, FolderNode item)
        {
            Attach(item);
            base.InsertItem(index, item);
            Changed();
        }
        protected override void SetItem(int index, FolderNode item)
        {
            Detach(this[index]);
            Attach(item);
            base.SetItem(index, item);
            Changed();
        }
        protected override void RemoveItem(int index)
        {
            FolderTreeView t = Tree;
            FolderNode removed = this[index];
            base.RemoveItem(index);
            if (t != null) t.OnRemoved(removed);
            Detach(removed);
            Changed();
        }
        protected override void ClearItems()
        {
            FolderTreeView t = Tree;
            foreach (FolderNode node in this) { if (t != null) t.OnRemoved(node); Detach(node); }
            base.ClearItems();
            Changed();
        }
        private void Changed() { FolderTreeView t = Tree; if (t != null) t.OnStructureChanged(); }
    }

    internal sealed class FolderNodeDrawEventArgs : EventArgs
    {
        public FolderNodeDrawEventArgs(Graphics graphics, FolderNode node, Rectangle bounds) { Graphics = graphics; Node = node; Bounds = bounds; }
        public Graphics Graphics { get; private set; }
        public FolderNode Node { get; private set; }
        public Rectangle Bounds { get; private set; }
    }

    internal sealed class FolderNodeMouseEventArgs : MouseEventArgs
    {
        public FolderNodeMouseEventArgs(FolderNode node, MouseButtons button, int x, int y) : base(button, 1, x, y, 0) { Node = node; }
        public FolderNode Node { get; private set; }
    }

    internal sealed class FolderNodeEventArgs : EventArgs
    {
        public FolderNodeEventArgs(FolderNode node, TreeViewAction action) { Node = node; Action = action; }
        public FolderNode Node { get; private set; }
        public TreeViewAction Action { get; private set; }
    }

    internal sealed class FolderTreeView : Control
    {
        internal const double ToggleSeconds = 0.18;
        internal const double NoMotion = -1e9;
        private const double ScrollEaseSeconds = 0.07;   // ホイールの目標位置へ近づく速さ（小さいほど速い）
        private const int ScrollBarWidth = 6;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        internal static double Now { get { return clock.Elapsed.TotalSeconds; } }
        internal static float Progress(double at, double now, double seconds)
        {
            return (float)Math.Max(0.0, Math.Min(1.0, (now - at) / seconds));
        }

        public event EventHandler<FolderNodeDrawEventArgs> DrawNode;
        public event EventHandler<FolderNodeMouseEventArgs> NodeMouseClick;
        public event EventHandler<FolderNodeEventArgs> AfterSelect;
        public event EventHandler<FolderNodeEventArgs> AfterCollapse;   // 閉じた直後（選択の移動を通知した後）
        public event ItemDragEventHandler ItemDrag;

        public FolderNodeCollection Nodes { get; private set; }
        public int ItemHeight { get; set; }
        public Color ScrollThumbColor { get; set; }
        public Color DropLineColor { get; set; }
        // フォルダの行の右端から、この幅は開閉の矢印の場所（ダブルクリックで二重に開閉しない）。
        public int ToggleZoneWidth { get; set; }

        private readonly Timer frameTimer = new Timer { Interval = 15 };
        private readonly ToolTip tip = HelpTipStyle.Create();
        private readonly Timer tipTimer = new Timer { Interval = 300 };
        private FolderNode selected;
        private float scrollY, scrollTarget;
        private double lastFrame;
        private int updateDepth;

        public FolderTreeView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            Nodes = new FolderNodeCollection(this, null);
            ItemHeight = 20;
            ToggleZoneWidth = 44;
            ScrollThumbColor = Color.FromArgb(120, 128, 136);
            DropLineColor = Color.FromArgb(150, 140, 255);
            TabStop = true;
            frameTimer.Tick += (s, e) => Frame();
            tipTimer.Tick += (s, e) => { tipTimer.Stop(); ShowTip(); };
        }

        // 演出してよい状態か（OSの効果がオフ・表示前・最小化中なら、すぐ切り替える）。
        internal bool CanAnimate
        {
            get { return UiMotion.Enabled && IsHandleCreated && Visible && updateDepth == 0 && ClientSize.Height > 0; }
        }

        // 一覧自身の動き（スクロール・開閉・ホバー）による描き直し中なら true。選択の変化ではない。
        public bool InVisualRefresh { get; private set; }
        private void Redraw()
        {
            InVisualRefresh = true;
            try { Invalidate(); }
            finally { InVisualRefresh = false; }
        }

        public void BeginUpdate() { updateDepth++; }
        public void EndUpdate()
        {
            if (updateDepth > 0) updateDepth--;
            if (updateDepth == 0) { ClampScroll(true); Invalidate(); }
        }

        public FolderNode SelectedNode
        {
            get { return selected; }
            set { Select(value, TreeViewAction.Unknown); }
        }

        private void Select(FolderNode node, TreeViewAction action)
        {
            if (node != null && node.TreeView != this) node = null;
            if (ReferenceEquals(selected, node)) return;
            selected = node;
            Invalidate();
            if (AfterSelect != null) AfterSelect(this, new FolderNodeEventArgs(node, action));
        }

        internal void OnRemoved(FolderNode node)
        {
            for (FolderNode n = selected; n != null; n = n.Parent)
                if (ReferenceEquals(n, node)) { selected = null; break; }
            if (ReferenceEquals(hovered, node)) hovered = null;
        }

        internal void OnStructureChanged()
        {
            if (updateDepth > 0) return;
            ClampScroll(true);
            Invalidate();
        }

        internal void OnToggled(FolderNode node, bool animate)
        {
            // 閉じたフォルダの中を選んでいたら、選択をフォルダへ移す（標準の TreeView と同じ）。
            if (!node.IsExpanded)
                for (FolderNode n = selected == null ? null : selected.Parent; n != null; n = n.Parent)
                    if (ReferenceEquals(n, node)) { Select(node, TreeViewAction.Collapse); break; }
            if (!node.IsExpanded && AfterCollapse != null) AfterCollapse(this, new FolderNodeEventArgs(node, TreeViewAction.Collapse));
            if (animate) StartFrames();
            else ClampScroll(true);
            Redraw();
        }

        //--------------
        // 行の位置（openness を反映した、今の見た目の位置）
        //--------------
        private delegate bool RowVisitor(FolderNode node, float top, float clipTop, float clipBottom);

        // 見えている順に行をたどる。clip は親の開き具合で切り取られる範囲（内容の座標）。visitor が false を返すと止める。
        private float Walk(RowVisitor visitor, bool settled)
        {
            double now = Now;
            float y = 0f;
            bool stop = false;
            WalkLevel(Nodes, ref y, float.NegativeInfinity, float.PositiveInfinity, visitor, settled, now, ref stop);
            return y;
        }

        private void WalkLevel(FolderNodeCollection nodes, ref float y, float clipTop, float clipBottom, RowVisitor visitor, bool settled, double now, ref bool stop)
        {
            foreach (FolderNode node in nodes)
            {
                if (stop) return;
                if (y < clipBottom && y + ItemHeight > clipTop && visitor != null && !visitor(node, y, clipTop, clipBottom)) { stop = true; return; }
                y += ItemHeight;
                if (node.Nodes.Count == 0) continue;
                float open = settled ? (node.IsExpanded ? 1f : 0f) : node.Openness(now);
                if (open <= 0f) continue;
                float full = ChildrenHeight(node, settled, now);
                float shown = full * open;
                // 中の行は、フォルダの行の下へ引き出されるように動く（上へ隠れていた分だけずらす）。
                float childY = y - (full - shown);
                float innerTop = Math.Max(clipTop, y), innerBottom = Math.Min(clipBottom, y + shown);
                WalkLevel(node.Nodes, ref childY, innerTop, innerBottom, visitor, settled, now, ref stop);
                y += shown;
            }
        }

        private float ChildrenHeight(FolderNode node, bool settled, double now)
        {
            float h = 0f;
            foreach (FolderNode child in node.Nodes)
            {
                h += ItemHeight;
                if (child.Nodes.Count > 0) h += ChildrenHeight(child, settled, now) * (settled ? (child.IsExpanded ? 1f : 0f) : child.Openness(now));
            }
            return h;
        }

        private float ContentHeight(bool settled) { return Walk(null, settled); }
        private float MaxScroll(bool settled) { return Math.Max(0f, ContentHeight(settled) - ClientSize.Height); }

        // 内容の座標での行の上端（settled=true なら動き終わった後の位置）。見えない行は NaN。
        private float TopOf(FolderNode target, bool settled)
        {
            float found = float.NaN;
            Walk((node, top, ct, cb) =>
            {
                if (!ReferenceEquals(node, target)) return true;
                found = top;
                return false;
            }, settled);
            return found;
        }

        internal Rectangle BoundsOf(FolderNode node)
        {
            if (!node.IsShown) return Rectangle.Empty;
            float top = TopOf(node, false);
            if (float.IsNaN(top)) return Rectangle.Empty;
            return new Rectangle(0, (int)Math.Round(top - scrollY), ClientSize.Width, ItemHeight);
        }

        public FolderNode GetNodeAt(Point p) { return GetNodeAt(p.X, p.Y); }
        public FolderNode GetNodeAt(int x, int y)
        {
            if (x < 0 || x >= ClientSize.Width || y < 0 || y >= ClientSize.Height) return null;
            float contentY = y + (float)Math.Round(scrollY);
            FolderNode hit = null;
            Walk((node, top, ct, cb) =>
            {
                if (contentY >= top && contentY < top + ItemHeight && contentY >= ct && contentY < cb) hit = node;
                return top <= contentY;   // 過ぎたら止める
            }, false);
            return hit;
        }

        internal FolderNode StepVisible(FolderNode from, int step)
        {
            var rows = new System.Collections.Generic.List<FolderNode>();
            Walk((node, top, ct, cb) => { if (node.IsShown) rows.Add(node); return true; }, true);
            int i = rows.IndexOf(from);
            if (i < 0) return null;
            i += step;
            return i >= 0 && i < rows.Count ? rows[i] : null;
        }

        //--------------
        // スクロール
        //--------------
        public int ScrollOffset { get { return (int)Math.Round(scrollY); } }
        public float OpennessOf(FolderNode node) { return node.Openness(Now); }

        public void EnsureVisible(FolderNode node)
        {
            float top = TopOf(node, true);
            if (float.IsNaN(top)) return;
            float target = scrollTarget;
            if (top < target) target = top;
            else if (top + ItemHeight > target + ClientSize.Height) target = top + ItemHeight - ClientSize.Height;
            ScrollTo(target, true);
        }

        private void ScrollTo(float target, bool animate)
        {
            scrollTarget = Math.Max(0f, Math.Min(MaxScroll(true), target));
            if (!animate || !CanAnimate) { scrollY = scrollTarget; Redraw(); return; }
            StartFrames();
        }

        // 内容が縮んだら、はみ出さない位置へ戻す。
        private void ClampScroll(bool settled)
        {
            float max = MaxScroll(settled);
            if (scrollTarget > max) scrollTarget = max;
            if (scrollY > MaxScroll(false)) scrollY = MaxScroll(false);
            if (scrollTarget < 0f) scrollTarget = 0f;
            if (scrollY < 0f) scrollY = 0f;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            HideTip();
            // 1目盛り（120）で約2行。タッチパッドの細かい量はそのまま細かく動く。
            float step = ItemHeight * 2f * -e.Delta / 120f;
            ScrollTo(scrollTarget + step, true);
        }

        private void StartFrames()
        {
            if (!frameTimer.Enabled) { lastFrame = Now; frameTimer.Start(); }
        }

        private void Frame()
        {
            double now = Now;
            double dt = Math.Max(0.0, now - lastFrame);
            lastFrame = now;
            bool moving = false;
            foreach (FolderNode node in Nodes) if (node.IsOpening(now)) { moving = true; break; }
            ClampScroll(true);
            float max = MaxScroll(false);
            if (scrollY > max) scrollY = max;
            if (Math.Abs(scrollTarget - scrollY) > 0.5f)
            {
                // 目標との差を時間で指数的に縮める（フレームが遅れても同じ速さに見える）。
                scrollY = scrollTarget + (scrollY - scrollTarget) * (float)Math.Exp(-dt / ScrollEaseSeconds);
                moving = true;
            }
            else scrollY = scrollTarget;
            if (dragThumb) moving = true;
            Redraw();
            if (!moving) frameTimer.Stop();
        }

        //--------------
        // スクロールバー（細い角丸のつまみ。ドラッグで動かせる）
        //--------------
        private bool dragThumb, hoverThumb;
        private int thumbGrabOffset;

        private Rectangle ThumbBounds()
        {
            float content = ContentHeight(false);
            int h = ClientSize.Height;
            if (content <= h + 0.5f || h <= 0) return Rectangle.Empty;
            int thumbH = Math.Max(28, (int)(h * h / content));
            float max = Math.Max(1f, content - h);
            int y = (int)Math.Round((h - thumbH) * Math.Max(0f, Math.Min(1f, scrollY / max)));
            return new Rectangle(ClientSize.Width - ScrollBarWidth - 2, y, ScrollBarWidth, thumbH);
        }

        private bool OnThumbZone(Point p)
        {
            Rectangle thumb = ThumbBounds();
            return !thumb.IsEmpty && p.X >= thumb.X - 4 && p.Y >= thumb.Y && p.Y < thumb.Bottom;
        }

        //--------------
        // 描画
        //--------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int offset = (int)Math.Round(scrollY);
            int height = ClientSize.Height;
            Walk((node, top, ct, cb) =>
            {
                int y = (int)Math.Round(top) - offset;
                if (y >= height) return false;
                if (y + ItemHeight <= 0) return true;
                var bounds = new Rectangle(0, y, ClientSize.Width, ItemHeight);
                GraphicsState state = g.Save();
                if (!float.IsInfinity(ct) || !float.IsInfinity(cb))
                {
                    int clipTop = float.IsInfinity(ct) ? y : (int)Math.Round(ct) - offset;
                    int clipBottom = float.IsInfinity(cb) ? y + ItemHeight : (int)Math.Round(cb) - offset;
                    g.SetClip(Rectangle.FromLTRB(0, Math.Max(y, clipTop), ClientSize.Width, Math.Min(y + ItemHeight, clipBottom)), CombineMode.Intersect);
                }
                if (DrawNode != null) DrawNode(this, new FolderNodeDrawEventArgs(g, node, bounds));
                g.Restore(state);
                return true;
            }, false);

            if (dropLineVisible) PaintDropLine(g);

            Rectangle thumb = ThumbBounds();
            if (!thumb.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int alpha = dragThumb ? 230 : hoverThumb ? 190 : 120;
                using (var brush = new SolidBrush(Color.FromArgb(alpha, ScrollThumbColor)))
                using (var path = Rounded(thumb, ScrollBarWidth / 2))
                    g.FillPath(brush, path);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        //--------------
        // ドラッグ中の「ここに入る」線
        //--------------
        private bool dropLineVisible;
        private int dropLineY, dropLineX;
        public bool DropLineVisible { get { return dropLineVisible; } }

        public void ShowDropLine(int y, int x)
        {
            if (dropLineVisible && dropLineY == y && dropLineX == x) return;
            dropLineVisible = true; dropLineY = y; dropLineX = x;
            Redraw();
        }

        public void HideDropLine()
        {
            if (!dropLineVisible) return;
            dropLineVisible = false;
            Redraw();
        }

        private void PaintDropLine(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int width = Math.Max(20, ClientSize.Width - dropLineX - 10);
            using (var brush = new SolidBrush(DropLineColor))
            {
                g.FillEllipse(brush, dropLineX, dropLineY - 3, 6, 6);
                using (var path = Rounded(new Rectangle(dropLineX + 5, dropLineY - 1, Math.Max(2, width - 6), 2), 1))
                    g.FillPath(brush, path);
            }
        }

        // ドラッグ中に上下の端へ近づいたら、少しずつスクロールする。
        public void DragAutoScroll(Point client)
        {
            int edge = ItemHeight;
            if (client.Y < edge) ScrollTo(scrollTarget - (edge - client.Y) * 0.6f, true);
            else if (client.Y > ClientSize.Height - edge) ScrollTo(scrollTarget + (client.Y - (ClientSize.Height - edge)) * 0.6f, true);
        }

        //--------------
        // マウス
        //--------------
        private FolderNode pressNode, hovered;
        private Point pressAt;
        private MouseButtons pressButton;
        private bool dragStarted;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (CanFocus) Focus();
            HideTip();
            if (e.Button == MouseButtons.Left && OnThumbZone(e.Location))
            {
                dragThumb = true;
                pressNode = null;
                thumbGrabOffset = e.Y - ThumbBounds().Y;
                Capture = true;
                StartFrames();
                return;
            }
            pressNode = GetNodeAt(e.Location);
            pressAt = e.Location;
            pressButton = e.Button;
            dragStarted = false;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // ボタンが離れているのにつまみのドラッグ中のまま（離したことを受け取れなかった）なら、ここで終える。
            if (dragThumb && (e.Button & MouseButtons.Left) == 0) EndThumbDrag();
            if (dragThumb)
            {
                Rectangle thumb = ThumbBounds();
                int travel = Math.Max(1, ClientSize.Height - thumb.Height);
                float ratio = Math.Max(0f, Math.Min(1f, (e.Y - thumbGrabOffset) / (float)travel));
                scrollTarget = scrollY = ratio * MaxScroll(false);
                Redraw();
                return;
            }
            bool thumbHover = OnThumbZone(e.Location);
            if (thumbHover != hoverThumb) { hoverThumb = thumbHover; Redraw(); }
            if (pressNode != null && !dragStarted && (e.Button & (MouseButtons.Left | MouseButtons.Right)) != 0 &&
                (Math.Abs(e.X - pressAt.X) > SystemInformation.DragSize.Width / 2 || Math.Abs(e.Y - pressAt.Y) > SystemInformation.DragSize.Height / 2))
            {
                dragStarted = true;
                FolderNode node = pressNode;
                pressNode = null;
                HideTip();
                if (ItemDrag != null) ItemDrag(this, new ItemDragEventArgs(pressButton, node));
                return;
            }
            FolderNode over = GetNodeAt(e.Location);
            if (!ReferenceEquals(over, hovered))
            {
                hovered = over;
                HideTip();
                if (over != null && !string.IsNullOrEmpty(over.ToolTipText)) tipTimer.Start();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragThumb)
            {
                EndThumbDrag();
                return;
            }
            FolderNode node = pressNode;
            pressNode = null;
            if (node != null && !dragStarted && ReferenceEquals(GetNodeAt(e.Location), node) && NodeMouseClick != null)
                NodeMouseClick(this, new FolderNodeMouseEventArgs(node, e.Button, e.X, e.Y));
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            FolderNode node = GetNodeAt(e.Location);
            // フォルダの行のダブルクリックで開閉（矢印の場所は1回のクリックで開閉するので除く）。
            if (e.Button == MouseButtons.Left && node != null && node.Nodes.Count > 0 && e.X <= ClientSize.Width - ToggleZoneWidth)
                node.Toggle();
            base.OnMouseDoubleClick(e);
        }

        // 他の窓などにマウスの取り込みを奪われたら、つまみのドラッグを終える（勝手にスクロールし続けない）。
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (dragThumb && !Capture) EndThumbDrag();
            base.OnMouseCaptureChanged(e);
        }

        private void EndThumbDrag()
        {
            if (!dragThumb) return;
            dragThumb = false;
            if (Capture) Capture = false;
            Redraw();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = null;
            HideTip();
            if (hoverThumb) { hoverThumb = false; Redraw(); }
            base.OnMouseLeave(e);
        }

        //--------------
        // ヒント（アプリの見た目の札。0.3秒乗せると出る）
        //--------------
        private void ShowTip()
        {
            if (hovered == null || string.IsNullOrEmpty(hovered.ToolTipText) || !IsHandleCreated || !HelpTipStyle.HintsEnabled) return;
            Point p = PointToClient(Cursor.Position);
            if (!ReferenceEquals(GetNodeAt(p), hovered)) return;
            tip.SetToolTip(this, hovered.ToolTipText);
            tip.Show(hovered.ToolTipText, this, p.X + 12, p.Y + 20, tip.AutoPopDelay);
        }

        private void HideTip()
        {
            tipTimer.Stop();
            if (!IsHandleCreated) return;
            tip.Hide(this);
            tip.SetToolTip(this, null);
        }

        //--------------
        // キー操作
        //--------------
        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Control || e.Alt) return;
            FolderNode current = selected;
            FolderNode next = null;
            int page = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight) - 1);
            switch (e.KeyCode)
            {
                case Keys.Up: next = current == null ? First() : current.PrevVisibleNode; break;
                case Keys.Down: next = current == null ? First() : current.NextVisibleNode; break;
                case Keys.Home: next = First(); break;
                case Keys.End: next = Last(); break;
                case Keys.PageUp: next = Step(current, -page); break;
                case Keys.PageDown: next = Step(current, page); break;
                case Keys.Left:
                    if (current == null) break;
                    if (current.Nodes.Count > 0 && current.IsExpanded) { current.Collapse(); e.Handled = true; return; }
                    next = current.Parent;
                    break;
                case Keys.Right:
                    if (current == null || current.Nodes.Count == 0) break;
                    if (!current.IsExpanded) { current.Expand(); e.Handled = true; return; }
                    next = current.Nodes[0];
                    break;
                default: return;
            }
            e.Handled = true;
            if (next == null) return;
            Select(next, TreeViewAction.ByKeyboard);
            EnsureVisible(next);
        }

        private FolderNode First() { return Nodes.Count > 0 ? Nodes[0] : null; }
        private FolderNode Last()
        {
            FolderNode last = null;
            Walk((node, top, ct, cb) => { if (node.IsShown) last = node; return true; }, true);
            return last;
        }
        private FolderNode Step(FolderNode from, int count)
        {
            if (from == null) return First();
            FolderNode node = from;
            for (int i = 0; i < Math.Abs(count); i++)
            {
                FolderNode n = count < 0 ? node.PrevVisibleNode : node.NextVisibleNode;
                if (n == null) break;
                node = n;
            }
            return node;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll(true);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                frameTimer.Dispose();
                tipTimer.Dispose();
                tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
