using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal enum SpriteColorBlendMode
    {
        Multiply,
        Add
    }

    internal sealed class ColorAdjustmentDialog : Form
    {
        private readonly SquareColorPicker picker = new SquareColorPicker();
        private readonly ComboBox modeBox = new ComboBox();
        private readonly TrackBar strengthBar = new TrackBar();
        private readonly Label strengthValue = new Label();
        private readonly TextBox hexBox = new TextBox();
        private readonly Panel swatch = new Panel();
        private bool syncing;

        public event EventHandler AdjustmentChanged;
        public SpriteColorBlendMode BlendMode { get { return (SpriteColorBlendMode)Math.Max(0, modeBox.SelectedIndex); } }
        public Color SelectedColor { get { return picker.SelectedColor; } }
        public int Strength { get { return strengthBar.Value; } }

        public ColorAdjustmentDialog(SpriteColorBlendMode mode, Color color, int strength)
        {
            Text = Loc.T("dialog.colorTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 510);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(24, 32, 38);
            ForeColor = Color.FromArgb(243, 245, 247);
            Font = new Font("Meiryo UI", 10f);

            var title = new Label { Text = Loc.T("label.colorAll"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Location = new Point(20, 17) };
            var note = new Label { Text = Loc.T("hint.colorApply"), AutoSize = false, AutoEllipsis = true, Size = new Size(390, 22), ForeColor = Color.FromArgb(180, 190, 198), Location = new Point(20, 45) };
            picker.Location = new Point(20, 78);
            picker.Size = new Size(390, 245);
            picker.SelectedColorChanged += (s, e) => { if (!syncing) SyncFromPicker(true); };

            var modeLabel = MakeLabel(Loc.T("field.blend"), 20, 343);
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(new object[] { Loc.T("blend.multiply"), Loc.T("blend.add") });
            modeBox.Location = new Point(75, 338);
            modeBox.Size = new Size(112, 30);
            modeBox.FlatStyle = FlatStyle.Flat;
            modeBox.BackColor = Color.FromArgb(17, 23, 28);
            modeBox.ForeColor = ForeColor;
            modeBox.SelectedIndexChanged += (s, e) => RaiseChanged();

            var colorLabel = MakeLabel(Loc.T("field.color"), 210, 343);
            swatch.Location = new Point(270, 338);
            swatch.Size = new Size(42, 30);
            swatch.Paint += (s, e) => { using (var p = new Pen(Color.FromArgb(100, 112, 122))) e.Graphics.DrawRectangle(p, 0, 0, swatch.Width - 1, swatch.Height - 1); };
            hexBox.Location = new Point(318, 338);
            hexBox.Size = new Size(92, 30);
            hexBox.MaxLength = 7;
            hexBox.BackColor = Color.FromArgb(17, 23, 28);
            hexBox.ForeColor = ForeColor;
            hexBox.BorderStyle = BorderStyle.FixedSingle;
            hexBox.Leave += (s, e) => ApplyHex();
            hexBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyHex(); e.SuppressKeyPress = true; } };

            var strengthLabel = MakeLabel(Loc.T("field.strength"), 20, 393);
            strengthBar.Location = new Point(68, 382);
            strengthBar.Size = new Size(286, 45);
            strengthBar.Minimum = 0;
            strengthBar.Maximum = 100;
            strengthBar.TickFrequency = 10;
            strengthBar.ValueChanged += (s, e) => { strengthValue.Text = strengthBar.Value + "%"; RaiseChanged(); };
            strengthValue.Location = new Point(358, 391);
            strengthValue.Size = new Size(52, 26);
            strengthValue.TextAlign = ContentAlignment.MiddleRight;

            var reset = MakeButton(Loc.T("button.reset"), 20, 451, 92);
            var cancel = MakeButton(Loc.T("button.cancel"), 218, 451, 92);
            var apply = MakeButton(Loc.T("button.apply"), 318, 451, 92);
            apply.BackColor = Color.FromArgb(84, 73, 255);
            reset.Click += (s, e) =>
            {
                syncing = true;
                modeBox.SelectedIndex = 0;
                picker.SelectedColor = Color.White;
                strengthBar.Value = 100;
                syncing = false;
                SyncFromPicker(true);
            };
            cancel.DialogResult = DialogResult.Cancel;
            apply.DialogResult = DialogResult.OK;
            AcceptButton = apply;
            CancelButton = cancel;

            Controls.AddRange(new Control[] { title, note, picker, modeLabel, modeBox, colorLabel, swatch, hexBox,
                strengthLabel, strengthBar, strengthValue, reset, cancel, apply });

            syncing = true;
            modeBox.SelectedIndex = (int)mode;
            picker.SelectedColor = color;
            strengthBar.Value = Math.Max(0, Math.Min(100, strength));
            syncing = false;
            SyncFromPicker(false);
        }

        private Label MakeLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = ForeColor };
        }

        private Button MakeButton(string text, int x, int y, int width)
        {
            var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 38), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(31, 40, 47), ForeColor = ForeColor };
            button.FlatAppearance.BorderColor = Color.FromArgb(75, 86, 95);
            return button;
        }

        private void SyncFromPicker(bool notify)
        {
            swatch.BackColor = picker.SelectedColor;
            swatch.Invalidate();
            hexBox.Text = "#" + picker.SelectedColor.R.ToString("X2") + picker.SelectedColor.G.ToString("X2") + picker.SelectedColor.B.ToString("X2");
            if (notify) RaiseChanged();
        }

        private void ApplyHex()
        {
            string value = (hexBox.Text ?? "").Trim().TrimStart('#');
            int rgb;
            if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
            {
                SyncFromPicker(false);
                return;
            }
            picker.SelectedColor = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        private void RaiseChanged()
        {
            if (syncing) return;
            EventHandler handler = AdjustmentChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    internal sealed class SquareColorPicker : Control
    {
        private float hue;
        private float saturation;
        private float value = 1f;
        private bool draggingSquare;
        private bool draggingHue;
        public event EventHandler SelectedColorChanged;

        public SquareColorPicker()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public Color SelectedColor
        {
            get { return FromHsv(hue, saturation, value); }
            set
            {
                ToHsv(value, out hue, out saturation, out this.value);
                Invalidate();
                OnSelectedColorChanged();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle square = GetSquare();
            Rectangle hueRect = GetHueRect();
            using (var hueBrush = new SolidBrush(FromHsv(hue, 1, 1))) e.Graphics.FillRectangle(hueBrush, square);
            using (var white = new LinearGradientBrush(square, Color.White, Color.Transparent, LinearGradientMode.Horizontal)) e.Graphics.FillRectangle(white, square);
            using (var black = new LinearGradientBrush(square, Color.Transparent, Color.Black, LinearGradientMode.Vertical)) e.Graphics.FillRectangle(black, square);
            for (int y = 0; y < hueRect.Height; y++)
            {
                using (var pen = new Pen(FromHsv(y * 360f / Math.Max(1, hueRect.Height - 1), 1, 1)))
                    e.Graphics.DrawLine(pen, hueRect.Left, hueRect.Top + y, hueRect.Right, hueRect.Top + y);
            }
            int cx = square.Left + (int)Math.Round(saturation * square.Width);
            int cy = square.Top + (int)Math.Round((1f - value) * square.Height);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outer = new Pen(Color.Black, 3)) e.Graphics.DrawEllipse(outer, cx - 6, cy - 6, 12, 12);
            using (var inner = new Pen(Color.White, 1)) e.Graphics.DrawEllipse(inner, cx - 6, cy - 6, 12, 12);
            int hy = hueRect.Top + (int)Math.Round(hue / 360f * hueRect.Height);
            using (var pen = new Pen(Color.White, 2)) e.Graphics.DrawRectangle(pen, hueRect.Left - 2, hy - 2, hueRect.Width + 3, 4);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); CaptureAt(e.Location); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (draggingSquare) SetSquare(e.Location);
            else if (draggingHue) SetHue(e.Location);
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); draggingSquare = draggingHue = false; Capture = false; }

        private void CaptureAt(Point p)
        {
            if (GetSquare().Contains(p)) { draggingSquare = true; Capture = true; SetSquare(p); }
            else if (GetHueRect().Contains(p)) { draggingHue = true; Capture = true; SetHue(p); }
        }
        private void SetSquare(Point p)
        {
            Rectangle r = GetSquare();
            saturation = Clamp((p.X - r.Left) / (float)Math.Max(1, r.Width));
            value = 1f - Clamp((p.Y - r.Top) / (float)Math.Max(1, r.Height));
            Invalidate(); OnSelectedColorChanged();
        }
        private void SetHue(Point p)
        {
            Rectangle r = GetHueRect();
            hue = Clamp((p.Y - r.Top) / (float)Math.Max(1, r.Height)) * 360f;
            Invalidate(); OnSelectedColorChanged();
        }
        private Rectangle GetSquare() { return new Rectangle(1, 1, Math.Max(1, Width - 37), Math.Max(1, Height - 2)); }
        private Rectangle GetHueRect() { return new Rectangle(Math.Max(1, Width - 25), 1, 23, Math.Max(1, Height - 2)); }
        private void OnSelectedColorChanged() { var h = SelectedColorChanged; if (h != null) h(this, EventArgs.Empty); }
        private static float Clamp(float n) { return Math.Max(0, Math.Min(1, n)); }

        private static Color FromHsv(float h, float s, float v)
        {
            h = (h % 360 + 360) % 360;
            float c = v * s, x = c * (1 - Math.Abs(h / 60f % 2 - 1)), m = v - c;
            float r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        private static void ToHsv(Color color, out float h, out float s, out float v)
        {
            float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d == 0) h = 0; else if (max == r) h = 60 * (((g - b) / d) % 6); else if (max == g) h = 60 * ((b - r) / d + 2); else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            s = max == 0 ? 0 : d / max;
            v = max;
        }
    }
}
