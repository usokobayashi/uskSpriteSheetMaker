using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    internal sealed class RoundedButton : Button
    {
        private bool hover;
        private bool pressed;

        public int CornerRadius { get; set; } = 6;
        public Color BorderColor { get; set; } = Color.Empty;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backdrop = RoundedPaint.ResolveBackdrop(Parent);
            Color baseColor = BackColor.A == 255 ? BackColor : backdrop;
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

    // UIフォントの生成を一か所に集約する。試験導入のLINE Seed JPが無い環境では
    // Meiryo UIに戻る。元のフォントへ戻す場合はUseLineSeedをfalseにする。
    // 中国語表示のときだけ、簡体字の字形が安定するMicrosoft YaHei UIを使う。
    internal static class UiFont
    {
        private const bool UseLineSeed = true;
        private const string ChineseFamilyName = "Microsoft YaHei UI";
        private static readonly string regularFamily;
        private static readonly string boldFamily;
        private static readonly string chineseFamily;
        private static bool useChinese;

        static UiFont()
        {
            regularFamily = "Meiryo UI";
            boldFamily = null;
            var installed = new System.Collections.Generic.HashSet<string>();
            foreach (FontFamily family in FontFamily.Families) installed.Add(family.Name);
            chineseFamily = installed.Contains(ChineseFamilyName) ? ChineseFamilyName : null;
            if (!UseLineSeed) return;
            foreach (string prefix in new[] { "LINE Seed JP_TTF", "LINE Seed JP App_TTF", "LINE Seed JP_OTF" })
            {
                if (!installed.Contains(prefix + " Regular")) continue;
                regularFamily = prefix + " Regular";
                if (installed.Contains(prefix + " Bold")) boldFamily = prefix + " Bold";
                break;
            }
        }

        public static void SetLanguage(UiLanguage language)
        {
            useChinese = language == UiLanguage.ChineseSimplified && chineseFamily != null;
        }

        public static Font Create(float size, FontStyle style, GraphicsUnit unit)
        {
            if (useChinese) return new Font(chineseFamily, size, style, unit);
            bool bold = (style & FontStyle.Bold) != 0;
            if (bold && boldFamily != null)
                return new Font(boldFamily, size, style & ~FontStyle.Bold, unit);
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
