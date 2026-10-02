using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // Control.Regionによる角丸はアンチエイリアスされず、小さい半径では階段状の
    // ジャギーになるため、背景を自前でアンチエイリアス描画するコントロール群。
    internal static class RoundedPaint
    {
        public static Color ResolveBackdrop(Control parent)
        {
            for (Control c = parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return SystemColors.Control;
        }

        public static void Draw(Graphics g, Size size, Color fill, Color border, int radius, Color backdrop)
        {
            if (size.Width < 2 || size.Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(backdrop);
            Rectangle bounds = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
            int r = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            using (GraphicsPath path = MainForm.CreateRoundedPath(bounds, r))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border))
                        g.DrawPath(pen, path);
            }
        }
    }

    internal static class ControlStyleHelper
    {
        // Paintイベントで描画する素のPanelは、サイズ変更時に拡大された部分しか
        // 再描画されず、以前の枠線などが残像として残る。全面再描画を有効にする。
        public static void EnableResizeRedraw(Control control)
        {
            typeof(Control).GetMethod("SetStyle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(control, new object[] { ControlStyles.ResizeRedraw, true });
        }
    }

    // アプリ内のマウスクリックを監視し、クリック先のコントロールを通知する。
    // 「最後にクリックしたのがどこか」を、フォーカスの有無に依存せず正確に知るために使う。
    internal sealed class ClickTracker : IMessageFilter
    {
        private const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_NCLBUTTONDOWN = 0x0A1;
        private readonly Form owner;
        private readonly Action<Control> onClick;

        public ClickTracker(Form owner, Action<Control> onClick) { this.owner = owner; this.onClick = onClick; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN || m.Msg == WM_NCLBUTTONDOWN)
            {
                Control clicked = Control.FromChildHandle(m.HWnd);
                // 別ウィンドウ（ダイアログ等）や不明な場所のクリックは「キャンバスではない」扱い。
                onClick(clicked != null && clicked.FindForm() == owner ? clicked : null);
            }
            return false;
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; } = 8;
        public Color BackdropColor { get; set; } = Color.Empty;
        public Color BorderColor { get; set; } = Color.Empty;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color backdrop = BackdropColor != Color.Empty ? BackdropColor : RoundedPaint.ResolveBackdrop(Parent);
            RoundedPaint.Draw(e.Graphics, ClientSize, BackColor, BorderColor, CornerRadius, backdrop);
        }
    }

    internal sealed class RoundedFlowPanel : FlowLayoutPanel
    {
        public int CornerRadius { get; set; } = 8;

        public RoundedFlowPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            RoundedPaint.Draw(e.Graphics, ClientSize, BackColor, Color.Empty, CornerRadius,
                RoundedPaint.ResolveBackdrop(Parent));
        }
    }

    internal sealed class RoundedLabel : Label
    {
        public int CornerRadius { get; set; } = 4;

        public RoundedLabel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            RoundedPaint.Draw(e.Graphics, ClientSize, BackColor, Color.Empty, CornerRadius,
                RoundedPaint.ResolveBackdrop(Parent));
        }
    }

    // 左ペイン上部のタブ（フォルダ・状態遷移・設定）。台・選択中の色付き角丸・ホバー・文字を
    // ひとつの OnPaint でまとめて描く。ボタンを子に並べると台とボタンが別々の時点で描かれ、
    // 滑っている選択表示が境目でずれたりちらついたりするため、子コントロールを持たない。
    internal sealed class SegmentedTabBar : Control
    {
        private const int Pad = 4;
        private const int Gap = 4;
        private readonly string[] texts;
        private int selectedIndex;
        private int hoverIndex = -1;
        private int pressedIndex = -1;
        private RectangleF indicatorRect;
        private RectangleF indicatorFrom;
        private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        private readonly Timer timer = new Timer { Interval = 15 };

        public int CornerRadius { get; set; } = 8;
        public int IndicatorRadius { get; set; } = 4;
        public int AnimationMilliseconds { get; set; } = 300;
        public Color IndicatorColor { get; set; } = Color.SlateBlue;
        public Color HoverColor { get; set; } = Color.Empty;
        public Color SelectedForeColor { get; set; } = Color.White;
        public event Action<int> TabClicked;

        public SegmentedTabBar(int count)
        {
            texts = new string[Math.Max(1, count)];
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor = Cursors.Hand;
            timer.Tick += (s, e) => StepAnimation();
        }

        public int TabCount { get { return texts.Length; } }
        public int SelectedIndex { get { return selectedIndex; } }
        public bool IsAnimating { get { return timer.Enabled; } }
        // 今描いている選択表示の位置（テスト・確認用）。
        public RectangleF IndicatorBounds { get { return indicatorRect; } }

        public void SetTabText(int index, string text)
        {
            texts[index] = text ?? "";
            AccessibilityNotifyClients(AccessibleEvents.NameChange, index);
            Invalidate();
        }

        // 各タブの範囲。幅は等分し、割り切れない分は最後のタブへ足す。
        public Rectangle GetTabBounds(int index)
        {
            int count = texts.Length;
            int height = Math.Max(1, ClientSize.Height - Pad * 2);
            int colWidth = Math.Max(20, (ClientSize.Width - Pad * 2 - Gap * (count - 1)) / count);
            int x = Pad + index * (colWidth + Gap);
            int width = index == count - 1 ? Math.Max(colWidth, ClientSize.Width - Pad - x) : colWidth;
            return new Rectangle(x, Pad, width, height);
        }

        // 選択を変える。animate のとき、今の位置から新しいタブへ滑らせる（時間はここから数える）。
        public void SelectTab(int index, bool animate)
        {
            index = Math.Max(0, Math.Min(texts.Length - 1, index));
            if (index == selectedIndex && !timer.Enabled && !indicatorRect.IsEmpty) return;
            selectedIndex = index;
            if (!animate || !UiMotion.Enabled || indicatorRect.IsEmpty || !IsHandleCreated || !Visible)
            {
                timer.Stop();
                indicatorRect = GetTabBounds(index);
                Invalidate();
                return;
            }
            indicatorFrom = indicatorRect;
            clock.Restart();
            timer.Start();
            StepAnimation();
        }

        private void StepAnimation()
        {
            RectangleF goal = GetTabBounds(selectedIndex);
            float t = PageTransitionOverlay.Ease(clock.ElapsedMilliseconds / (float)Math.Max(1, AnimationMilliseconds));
            if (clock.ElapsedMilliseconds >= AnimationMilliseconds)
            {
                timer.Stop();
                indicatorRect = goal;
            }
            else
            {
                indicatorRect = new RectangleF(
                    indicatorFrom.X + (goal.X - indicatorFrom.X) * t, goal.Y,
                    indicatorFrom.Width + (goal.Width - indicatorFrom.Width) * t, goal.Height);
            }
            // 次のタイマーを待たずにすぐ描く（ほかの描画に後回しにされると動きが飛ぶ）。
            Invalidate();
            Update();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!timer.Enabled) indicatorRect = GetTabBounds(selectedIndex);
            Invalidate();
        }

        private int HitTest(Point point)
        {
            for (int i = 0; i < texts.Length; i++)
                if (GetTabBounds(i).Contains(point)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            if (hit != hoverIndex) { hoverIndex = hit; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            pressedIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) pressedIndex = HitTest(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int pressed = pressedIndex;
            pressedIndex = -1;
            if (e.Button != MouseButtons.Left || pressed < 0 || HitTest(e.Location) != pressed) return;
            Action<int> handler = TabClicked;
            if (handler != null) handler(pressed);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            RoundedPaint.Draw(g, ClientSize, BackColor, Color.Empty, CornerRadius, RoundedPaint.ResolveBackdrop(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (HoverColor != Color.Empty && hoverIndex >= 0 && hoverIndex != selectedIndex && Enabled)
                using (var brush = new SolidBrush(HoverColor))
                using (GraphicsPath path = CreateRoundedPath(GetTabBounds(hoverIndex), IndicatorRadius))
                    g.FillPath(brush, path);
            if (indicatorRect.Width > 1)
                using (var brush = new SolidBrush(IndicatorColor))
                using (GraphicsPath path = CreateRoundedPath(indicatorRect, IndicatorRadius))
                    g.FillPath(brush, path);
            for (int i = 0; i < texts.Length; i++)
            {
                if (string.IsNullOrEmpty(texts[i])) continue;
                // 文字の色は選択表示が重なっている割合で白へ寄せる（滑っている途中で先に色が変わらないように）。
                Rectangle tab = GetTabBounds(i);
                float overlap = Math.Max(0f, Math.Min(tab.Right, indicatorRect.Right) - Math.Max(tab.Left, indicatorRect.Left)) / Math.Max(1, tab.Width);
                TextRenderer.DrawText(g, texts[i], Font, tab, Blend(ForeColor, SelectedForeColor, overlap),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }
        }

        private static Color Blend(Color from, Color to, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * t),
                (int)Math.Round(from.G + (to.G - from.G) * t),
                (int)Math.Round(from.B + (to.B - from.B) * t));
        }

        // 小数の位置のまま角丸を作る（整数へ丸めると、滑らせたときに1pxずつ跳ねて見える）。
        private static GraphicsPath CreateRoundedPath(RectangleF rect, float radius)
        {
            var bounds = new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            float r = Math.Max(0.5f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            float d = r * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TabBarAccessible(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        // 読み上げソフト向けに、各タブをページタブとして見せる。
        private sealed class TabBarAccessible : ControlAccessibleObject
        {
            private readonly SegmentedTabBar bar;
            public TabBarAccessible(SegmentedTabBar bar) : base(bar) { this.bar = bar; }
            public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
            public override int GetChildCount() { return bar.texts.Length; }
            public override AccessibleObject GetChild(int index) { return new TabAccessible(bar, index); }
        }

        private sealed class TabAccessible : AccessibleObject
        {
            private readonly SegmentedTabBar bar;
            private readonly int index;
            public TabAccessible(SegmentedTabBar bar, int index) { this.bar = bar; this.index = index; }
            public override string Name { get { return bar.texts[index]; } }
            public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }
            public override AccessibleObject Parent { get { return bar.AccessibilityObject; } }
            public override Rectangle Bounds { get { return bar.RectangleToScreen(bar.GetTabBounds(index)); } }
            public override AccessibleStates State
            {
                get { return index == bar.selectedIndex ? AccessibleStates.Selected | AccessibleStates.Selectable : AccessibleStates.Selectable; }
            }
            public override string DefaultAction { get { return Loc.T("access.selectTab"); } }
            public override void DoDefaultAction()
            {
                Action<int> handler = bar.TabClicked;
                if (handler != null) handler(index);
            }
        }
    }

    internal sealed class RoundedButton : Button
    {
        private bool hover;
        private bool pressed;

        public int CornerRadius { get; set; } = 6;
        public Color BorderColor { get; set; } = Color.Empty;
        // BackColor が透明のとき、マウスを乗せた間だけ塗る色（Empty なら塗らない）。
        public Color TransparentHoverColor { get; set; } = Color.Empty;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        // 透明のときは OnPaint で親ごと描くので、標準の背景処理は行わない。
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            if (BackColor == Color.Transparent) return;
            base.OnPaintBackground(pevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (BackColor == Color.Transparent)
            {
                // 後ろの親（タブの台と、その上の選択表示）を自分で描き写す。二重バッファの中身は
                // 前の描画が残っているので、ここで必ず全面を塗る（塗らないと他の文字が透けて見える）。
                PaintParentUnderneath(e.Graphics);
                if (hover && Enabled && TransparentHoverColor != Color.Empty)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                    int r = Math.Max(1, Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2));
                    using (GraphicsPath path = MainForm.CreateRoundedPath(bounds, r))
                    using (var brush = new SolidBrush(TransparentHoverColor))
                        e.Graphics.FillPath(brush, path);
                }
                if (!string.IsNullOrEmpty(Text))
                    TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            Color backdrop = RoundedPaint.ResolveBackdrop(Parent);
            Color baseColor = BackColor.A == 255 ? BackColor : backdrop;
            PaintSolid(e, backdrop, baseColor);
        }

        // 親の背景と描画（Paint イベントで描くもの）を、このボタンの位置に合わせて描く。
        private void PaintParentUnderneath(Graphics g)
        {
            Control parent = Parent;
            if (parent == null)
            {
                g.Clear(SystemColors.Control);
                return;
            }
            g.Clear(RoundedPaint.ResolveBackdrop(parent));
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(-Left, -Top);
                var area = new Rectangle(Left, Top, Width, Height);
                using (var args = new PaintEventArgs(g, area))
                {
                    InvokePaintBackground(parent, args);
                    InvokePaint(parent, args);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }

        private void PaintSolid(PaintEventArgs e, Color backdrop, Color baseColor)
        {
            Color fill = !Enabled ? ControlPaint.Dark(baseColor, 0.2f)
                : pressed ? ControlPaint.Dark(baseColor, 0.1f)
                : hover ? ControlPaint.Light(baseColor, 0.15f) : baseColor;
            RoundedPaint.Draw(e.Graphics, ClientSize, fill, BorderColor, CornerRadius, backdrop);

            if (Image != null)
                e.Graphics.DrawImage(Image, (Width - Image.Width) / 2, (Height - Image.Height) / 2);
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                    ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // UIフォントの生成を一か所に集約する。LINE Seed JP（SIL Open Font License 1.1、
    // ライセンス全文は docs/third_party_licenses）はexeに同梱し、利用者の環境へ
    // インストールされていなくても同じ書体で表示できるよう、実行時にメモリへ読み込む
    // （PrivateFontCollectionはプロセス内だけで有効。OSのフォント一覧には出ない）。
    // 読み込みに失敗した環境ではMeiryo UIへ戻る。元のフォントへ戻す場合はUseLineSeedをfalseにする。
    // 中国語表示のときだけ、簡体字の字形が安定するMicrosoft YaHei UIを使う
    // （こちらはライセンス上同梱できないため、従来どおりOSにインストールされている前提）。
    internal static class UiFont
    {
        private const bool UseLineSeed = true;
        private const string ChineseFamilyName = "Microsoft YaHei UI";
        private static readonly string regularFamily = "Meiryo UI";
        private static readonly string boldFamily;
        private static readonly string chineseFamily;
        private static readonly FontFamily regularFontFamily;
        private static readonly FontFamily boldFontFamily;
        private static readonly FontFamily chineseFontFamily;
        // AddMemoryFontに渡した非管理メモリは、フォントを使い続ける間（＝アプリの終了まで）
        // 解放してはいけない（解放するとGDI+が不正なメモリを参照する）ため、保持し続ける。
        private static readonly System.Collections.Generic.List<IntPtr> pinnedFontMemory =
            new System.Collections.Generic.List<IntPtr>();
        private static bool useChinese;

        static UiFont()
        {
            var installed = new System.Collections.Generic.HashSet<string>();
            foreach (FontFamily family in FontFamily.Families) installed.Add(family.Name);
            if (installed.Contains(ChineseFamilyName))
            {
                chineseFamily = ChineseFamilyName;
                chineseFontFamily = new FontFamily(ChineseFamilyName);
            }
            if (!UseLineSeed) return;
            try
            {
                var collection = new PrivateFontCollection();
                byte[] regularBytes = ReadEmbeddedFont("LINESeedJP_Regular.ttf");
                byte[] boldBytes = ReadEmbeddedFont("LINESeedJP_Bold.ttf");
                if (regularBytes == null) return;
                AddMemoryFont(collection, regularBytes);
                if (boldBytes != null) AddMemoryFont(collection, boldBytes);
                // Families の並びは追加した順とは限らない（環境によりアルファベット順などになる）ため、
                // 名前の末尾（Regular／Bold）で対応付ける。
                FontFamily foundRegular = null;
                FontFamily foundBold = null;
                foreach (FontFamily fam in collection.Families)
                {
                    if (fam.Name.EndsWith(" Regular", StringComparison.Ordinal)) foundRegular = fam;
                    else if (fam.Name.EndsWith(" Bold", StringComparison.Ordinal)) foundBold = fam;
                }
                if (foundRegular == null) return;
                regularFontFamily = foundRegular;
                regularFamily = foundRegular.Name;
                if (foundBold != null)
                {
                    boldFontFamily = foundBold;
                    boldFamily = foundBold.Name;
                }
            }
            catch
            {
                // 同梱フォントの読み込みに失敗しても起動は優先し、Meiryo UIのまま続行する。
                regularFontFamily = null;
                boldFontFamily = null;
                regularFamily = "Meiryo UI";
                boldFamily = null;
            }
        }

        private static byte[] ReadEmbeddedFont(string fileName)
        {
            string resourceName = "SpriteSheetMaker.Fonts." + fileName;
            using (Stream stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint fontCount);

        // PrivateFontCollection.AddMemoryFontは、渡したメモリの内容を直接参照し続けるため、
        // 呼び出し側でアンマネージメモリへコピーしてから渡し、以後は（アプリ終了まで）解放しない。
        // AddMemoryFont は GDI+（Graphics.DrawString）にしか登録されない。ラベル・ボタン・TextRenderer は
        // GDI で文字を描くので、GDI にも同じフォントを登録する（しないと GDI は別の書体で代わりに描く）。
        private static void AddMemoryFont(PrivateFontCollection collection, byte[] data)
        {
            IntPtr buffer = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);
            collection.AddMemoryFont(buffer, data.Length);
            pinnedFontMemory.Add(buffer);
            uint added = 0;
            AddFontMemResourceEx(buffer, (uint)data.Length, IntPtr.Zero, ref added);   // プロセス内だけで有効
        }

        public static void SetLanguage(UiLanguage language)
        {
            useChinese = language == UiLanguage.ChineseSimplified && chineseFamily != null;
        }

        public static Font Create(float size, FontStyle style, GraphicsUnit unit)
        {
            if (useChinese) return new Font(chineseFontFamily, size, style, unit);
            bool bold = (style & FontStyle.Bold) != 0;
            if (bold && boldFontFamily != null)
                return new Font(boldFontFamily, size, style & ~FontStyle.Bold, unit);
            if (regularFontFamily != null)
                return new Font(regularFontFamily, size, style, unit);
            // 同梱フォントの読み込みに失敗した場合のみ、OSにインストールされた名前で探す。
            return new Font(regularFamily, size, style, unit);
        }

        // 言語切替後、既に作成済みのコントロールのフォントを現在の言語の書体へ差し替える。
        // 親から継承しているフォントは親を差し替えれば追従するため、書体が違うものだけ設定する。
        public static void Restyle(Control root)
        {
            ApplyTo(root);
            foreach (Control child in root.Controls) Restyle(child);
        }

        // ContextMenuStrip はフォームの Controls に入らないため上の Restyle では届かない。
        // 項目（サブメニューを含む）を個別に辿ってフォントを差し替える。
        public static void Restyle(ToolStrip strip)
        {
            ApplyTo(strip);
            foreach (ToolStripItem item in strip.Items) RestyleItem(item);
        }

        private static void RestyleItem(ToolStripItem item)
        {
            ApplyTo(item);
            var withDropDown = item as ToolStripDropDownItem;
            if (withDropDown != null)
                foreach (ToolStripItem child in withDropDown.DropDownItems) RestyleItem(child);
        }

        private static void ApplyTo(Control control)
        {
            Font font = control.Font;
            bool isUiFamily = font.Name == regularFamily || font.Name == boldFamily || font.Name == chineseFamily;
            if (!isUiFamily) return;
            bool bold = font.Name == boldFamily || (font.Style & FontStyle.Bold) != 0;
            FontStyle style = (font.Style & ~FontStyle.Bold) | (bold ? FontStyle.Bold : FontStyle.Regular);
            Font replacement = Create(font.Size, style, font.Unit);
            if (replacement.Name == font.Name && replacement.Style == font.Style) { replacement.Dispose(); return; }
            control.Font = replacement;
        }

        private static void ApplyTo(ToolStripItem item)
        {
            Font font = item.Font;
            bool isUiFamily = font.Name == regularFamily || font.Name == boldFamily || font.Name == chineseFamily;
            if (!isUiFamily) return;
            bool bold = font.Name == boldFamily || (font.Style & FontStyle.Bold) != 0;
            FontStyle style = (font.Style & ~FontStyle.Bold) | (bold ? FontStyle.Bold : FontStyle.Regular);
            Font replacement = Create(font.Size, style, font.Unit);
            if (replacement.Name == font.Name && replacement.Style == font.Style) { replacement.Dispose(); return; }
            item.Font = replacement;
        }
    }

    internal sealed class RoundedCheckBox : CheckBox
    {
        private bool hover;

        public Color AccentColor { get; set; } = Color.FromArgb(84, 73, 255);
        public Color BoxBackColor { get; set; } = Color.FromArgb(17, 23, 28);
        public Color BoxBorderColor { get; set; } = Color.FromArgb(75, 86, 95);
        public int BoxSizeDip { get; set; } = 18;
        public int GapDip { get; set; } = 8;

        public RoundedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        private int BoxSize { get { return (int)Math.Round(BoxSizeDip * DeviceDpi / 96.0); } }
        private int Gap { get { return (int)Math.Round(GapDip * DeviceDpi / 96.0); } }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text ?? "", Font);
            return new Size(BoxSize + Gap + text.Width + 4, Math.Max(BoxSize + 4, text.Height + 4));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(RoundedPaint.ResolveBackdrop(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int size = BoxSize;
            Rectangle box = new Rectangle(1, (Height - size) / 2, size - 2, size - 2);
            bool isChecked = CheckState == CheckState.Checked;
            Color fill = isChecked ? AccentColor : BoxBackColor;
            Color border = isChecked ? AccentColor : (hover ? ControlPaint.Light(BoxBorderColor, 0.3f) : BoxBorderColor);
            if (!Enabled)
            {
                fill = ControlPaint.Dark(fill, 0.2f);
                border = ControlPaint.Dark(border, 0.2f);
            }
            using (GraphicsPath path = MainForm.CreateRoundedPath(box, 4))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            if (isChecked)
            {
                using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.X + box.Width * 0.24f, box.Y + box.Height * 0.54f),
                        new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.72f),
                        new PointF(box.X + box.Width * 0.76f, box.Y + box.Height * 0.30f)
                    });
            }
            if (Focused && ShowFocusCues)
            {
                Rectangle ring = Rectangle.Inflate(box, 2, 2);
                using (GraphicsPath path = MainForm.CreateRoundedPath(ring, 5))
                using (var pen = new Pen(Color.FromArgb(120, AccentColor)))
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(size + Gap, 0, Math.Max(1, Width - size - Gap), Height), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
