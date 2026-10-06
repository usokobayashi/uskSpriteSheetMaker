//==================================================
// CoordinateCard / HelpMark
// カーソルを置いたセルの位置（セル番号・px・UV）を出す札と、項目の横に置く「？」のヘルプ。
// 札はシート（通常・キャラクター・エフェクト・マップチップ）のどれでも同じ見た目で描く。
//==================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // UV の縦の向き。DirectX は左上が原点（下へ行くほど V が大きい）、OpenGL は左下が原点（上へ行くほど V が大きい）。
    public enum UvCoordinateFormat { DirectX, OpenGL }

    internal static class CoordinateCard
    {
        public const string ShowSettingKey = "showCoordinates";
        public const string UvFormatSettingKey = "uvFormat";

        public static bool ShowSetting
        {
            get { return AppSettings.Get(ShowSettingKey) != "0"; }
            set { AppSettings.Set(ShowSettingKey, value ? "1" : "0"); }
        }
        public static UvCoordinateFormat UvFormatSetting
        {
            get { return AppSettings.Get(UvFormatSettingKey) == "opengl" ? UvCoordinateFormat.OpenGL : UvCoordinateFormat.DirectX; }
            set { AppSettings.Set(UvFormatSettingKey, value == UvCoordinateFormat.OpenGL ? "opengl" : "directx"); }
        }

        // columns / rows は "3" や "3–4" の形。basis は基準セル（マップチップのときだけ。なければ Empty）。
        public static string[] Lines(int number, string columns, string rows, Size basis, Rectangle px, Size sheet, UvCoordinateFormat format)
        {
            string first = Loc.T("coord.cell", number, columns, rows);
            if (!basis.IsEmpty) first += Loc.T("coord.basis", basis.Width, basis.Height);
            double w = Math.Max(1, sheet.Width), h = Math.Max(1, sheet.Height);
            double u0 = px.Left / w, u1 = px.Right / w;
            double v0 = px.Top / h, v1 = px.Bottom / h;
            if (format == UvCoordinateFormat.OpenGL) { double top = 1 - v1, bottom = 1 - v0; v0 = top; v1 = bottom; }
            return new[]
            {
                first,
                Loc.T("coord.px", px.X, px.Y, px.Width, px.Height),
                Loc.T("coord.uv", u0.ToString("0.000"), u1.ToString("0.000"), v0.ToString("0.000"), v1.ToString("0.000"),
                    Loc.T(format == UvCoordinateFormat.OpenGL ? "uv.opengl.short" : "uv.directx.short"))
            };
        }

        public static string Range(int from, int to) { return from == to ? from.ToString() : from + "–" + to; }

        // カーソルの右下に出し、画面の端では内側へ折り返す。
        public static void Draw(Graphics g, Rectangle client, Point cursor, string[] lines, Font font)
        {
            if (lines == null || lines.Length == 0) return;
            Size[] sizes = Array.ConvertAll(lines, line => TextRenderer.MeasureText(line, font));
            int lineHeight = 0, width = 0;
            foreach (Size s in sizes) { lineHeight = Math.Max(lineHeight, s.Height); width = Math.Max(width, s.Width); }
            var box = new Rectangle(cursor.X + 16, cursor.Y + 18, width + 18, lineHeight * lines.Length + 12);
            if (box.Right > client.Right - 4) box.X = Math.Max(client.Left + 4, cursor.X - 12 - box.Width);
            if (box.Bottom > client.Bottom - 4) box.Y = Math.Max(client.Top + 4, cursor.Y - 12 - box.Height);
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = MainForm.CreateRoundedPath(box, 6))
            {
                using (var back = new SolidBrush(Color.FromArgb(235, 24, 32, 38))) g.FillPath(back, path);
                using (var pen = new Pen(Color.FromArgb(75, 86, 95))) g.DrawPath(pen, path);
            }
            g.Restore(state);
            for (int i = 0; i < lines.Length; i++)
                TextRenderer.DrawText(g, lines[i], font, new Point(box.X + 9, box.Y + 6 + i * lineHeight),
                    i == 0 ? Color.FromArgb(243, 245, 247) : Color.FromArgb(190, 198, 205), TextFormatFlags.NoPrefix);
        }
    }

    //==================================================
    // HelpTipStyle
    // ヘルプの札（「？」の説明・ヒント・ヘルプ一覧で共通）。文章は次の書き方で、言語ファイルに書く:
    //   \n                改行（論点ごとに分ける。見出しは付けない）
    //   **語句**          強調（読み飛ばすと誤解する操作名・対象・結果だけ）
    //   > 操作|結果       操作の行。続く行は表になり、操作の列がそろう
    //   [fig:名前]        図（コードで描く。frames: コマの並び順 / uv: UV の原点と向き）
    // 測り方と描き方は1か所（Layout）にまとめ、ポップアップと一覧で同じ結果にする。
    //==================================================
    internal static class HelpTipStyle
    {
        private static readonly Color Back = Color.FromArgb(31, 40, 47);
        private static readonly Color Border = Color.FromArgb(84, 73, 255);
        private static readonly Color TextColor = Color.FromArgb(243, 245, 247);
        private static readonly Color EmphasisColor = Color.FromArgb(206, 200, 255);
        private static readonly Color OperationColor = Color.FromArgb(190, 182, 255);
        private static readonly Color PillBack = Color.FromArgb(46, 52, 74);
        private static readonly Color FigureLine = Color.FromArgb(150, 160, 175);
        private const int PadX = 12, PadY = 9, Gap = 10, ParagraphGap = 7, RowGap = 5, WrapWidth = 360, PillPadX = 6;

        internal enum BlockKind { Paragraph, Operation, Figure }
        internal sealed class Block { public BlockKind Kind; public string Text = ""; public string Operation = ""; public string Figure = ""; }

        // 文章を段落・操作の行・図に分ける。
        internal static List<Block> Parse(string text)
        {
            var blocks = new List<Block>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[fig:") && line.EndsWith("]")) { blocks.Add(new Block { Kind = BlockKind.Figure, Figure = line.Substring(5, line.Length - 6).Trim() }); continue; }
                if (line.StartsWith(">"))
                {
                    string body = line.Substring(1).Trim(); int bar = body.IndexOf('|');
                    blocks.Add(new Block { Kind = BlockKind.Operation, Operation = bar < 0 ? body : body.Substring(0, bar).Trim(), Text = bar < 0 ? "" : body.Substring(bar + 1).Trim() });
                    continue;
                }
                blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = line });
            }
            return blocks;
        }

        // 画面に出す文字（** を外した文）。読み上げ名などに使う。
        internal static string PlainText(string text)
        {
            var parts = new List<string>();
            foreach (Block b in Parse(text))
                if (b.Kind == BlockKind.Operation) parts.Add(b.Operation + ": " + b.Text.Replace("**", ""));
                else if (b.Kind == BlockKind.Paragraph) parts.Add(b.Text.Replace("**", ""));
            return string.Join(Environment.NewLine, parts);
        }

        // カーソルを置いたときに出るヒントを出すか（設定タブ「基本設定」。既定はオン）。「？」の説明はこの設定によらず出す。
        public const string HintsSettingKey = "showHints";
        public static bool HintsEnabled
        {
            get { return AppSettings.Get(HintsSettingKey) != "0"; }
            set { AppSettings.Set(HintsSettingKey, value ? "1" : "0"); }
        }

        public static ToolTip Create()
        {
            // ShowAlways: アプリが前面でないとき（起動直後のフェードイン中など）も出す。
            var tip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 30000, OwnerDraw = true, ShowAlways = true, BackColor = Back, ForeColor = TextColor };
            tip.Popup += (s, e) => e.ToolTipSize = Measure(tip.GetToolTip(e.AssociatedControl));
            tip.Draw += (s, e) => Paint(e.Graphics, e.Bounds, e.ToolTipText);
            return tip;
        }

        public static Size Measure(string text) { return Layout(text).Size; }

        public static void Paint(Graphics g, Rectangle bounds, string text)
        {
            using (var back = new SolidBrush(Back)) g.FillRectangle(back, bounds);
            using (var pen = new Pen(Border)) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            HelpLayout layout = Layout(text);
            var state = g.Save();
            g.TranslateTransform(bounds.X, bounds.Y);
            foreach (LaidPill pill in layout.Pills)
            {
                var smoothing = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(PillBack)) using (GraphicsPath path = Rounded(pill.Bounds, 4)) g.FillPath(brush, path);
                g.SmoothingMode = smoothing;
            }
            foreach (LaidText run in layout.Texts)
            {
                Font font = run.Bold ? Fonts.Bold : Fonts.Regular;
                int lead = (run.Text.Length - run.Text.TrimStart(' ').Length) * SpaceWidth(font);
                TextRenderer.DrawText(g, run.Text.Trim(' '), font, new Point(run.At.X + lead, run.At.Y), run.Color, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            foreach (LaidFigure figure in layout.Figures) DrawFigure(g, figure);
            g.Restore(state);
        }

        //--------------
        // 組み立て（測る・描くで共通）
        //--------------
        private sealed class LaidText { public string Text; public Point At; public bool Bold; public Color Color; }
        private sealed class LaidPill { public Rectangle Bounds; }
        private sealed class LaidFigure { public string Name; public Rectangle Bounds; }
        private sealed class HelpLayout
        {
            public Size Size;
            public readonly List<LaidText> Texts = new List<LaidText>();
            public readonly List<LaidPill> Pills = new List<LaidPill>();
            public readonly List<LaidFigure> Figures = new List<LaidFigure>();
        }

        private static class Fonts
        {
            public static readonly Font Regular = UiFont.Create(9f, FontStyle.Regular, GraphicsUnit.Point);
            public static readonly Font Bold = UiFont.Create(9f, FontStyle.Bold, GraphicsUnit.Point);
        }

        private static readonly Dictionary<string, HelpLayout> cache = new Dictionary<string, HelpLayout>();

        private static HelpLayout Layout(string text)
        {
            text = text ?? "";
            HelpLayout cached;
            if (cache.TryGetValue(text, out cached)) return cached;
            var layout = new HelpLayout();
            List<Block> blocks = Parse(text);
            int lineHeight = Math.Max(TextRenderer.MeasureText("Ag", Fonts.Bold, Size.Empty, TextFormatFlags.NoPadding).Height,
                TextRenderer.MeasureText("Ag", Fonts.Regular, Size.Empty, TextFormatFlags.NoPadding).Height) + 3;
            // 操作の列の幅は、表の中で一番長い操作にそろえる（広すぎるときは抑える）。
            int opWidth = blocks.Where(b => b.Kind == BlockKind.Operation)
                .Select(b => TextRenderer.MeasureText(b.Operation, Fonts.Bold, Size.Empty, TextFormatFlags.NoPadding).Width + PillPadX * 2)
                .DefaultIfEmpty(0).Max();
            opWidth = Math.Min(opWidth, WrapWidth * 3 / 5);
            int y = PadY, right = 0;
            Block previous = null;
            foreach (Block block in blocks)
            {
                if (previous != null) y += previous.Kind == BlockKind.Operation && block.Kind == BlockKind.Operation ? RowGap : ParagraphGap;
                if (block.Kind == BlockKind.Paragraph)
                {
                    int bottom = FlowText(layout, block.Text, PadX, y, WrapWidth, lineHeight, TextColor, out int used);
                    right = Math.Max(right, PadX + used); y = bottom;
                }
                else if (block.Kind == BlockKind.Operation)
                {
                    // 操作は角丸の札に入れて色を変え、結果の文はその右の列で折り返す。札は中身の高さに合わせる。
                    int pillIndex = layout.Pills.Count;
                    layout.Pills.Add(new LaidPill());
                    int opBottom = FlowText(layout, "**" + block.Operation + "**", PadX + PillPadX, y, opWidth - PillPadX * 2, lineHeight, OperationColor, out int opUsed, true);
                    layout.Pills[pillIndex].Bounds = new Rectangle(PadX, y - 2, opWidth, opBottom - y + 2);
                    int textLeft = PadX + opWidth + Gap;
                    int bottom = FlowText(layout, block.Text, textLeft, y, Math.Max(120, WrapWidth - opWidth - Gap), lineHeight, TextColor, out int used);
                    right = Math.Max(right, textLeft + used); y = Math.Max(bottom, opBottom);
                }
                else
                {
                    Size size = FigureSize(block.Figure);
                    layout.Figures.Add(new LaidFigure { Name = block.Figure, Bounds = new Rectangle(PadX, y, size.Width, size.Height) });
                    right = Math.Max(right, PadX + size.Width); y += size.Height;
                }
                previous = block;
            }
            layout.Size = new Size(Math.Max(40, right + PadX), y + PadY);
            if (cache.Count > 256) cache.Clear();
            cache[text] = layout;
            return layout;
        }

        // 同じ書式なら前の区間につなげる。
        private static List<Tuple<string, bool>> Append(List<Tuple<string, bool>> line, Tuple<string, bool> token)
        {
            var result = new List<Tuple<string, bool>>(line);
            if (result.Count > 0 && result[result.Count - 1].Item2 == token.Item2)
                result[result.Count - 1] = Tuple.Create(result[result.Count - 1].Item1 + token.Item1, token.Item2);
            else result.Add(token);
            return result;
        }
        private static int SegmentsWidth(List<Tuple<string, bool>> segments) { return segments.Sum(s => SegmentWidth(s)); }
        // 区間の幅。前後の空白は測りに入らないので、空白の幅を足す。
        private static int SegmentWidth(Tuple<string, bool> segment)
        {
            Font font = segment.Item2 ? Fonts.Bold : Fonts.Regular;
            string core = segment.Item1.Trim(' ');
            int spaces = segment.Item1.Length - segment.Item1.TrimStart(' ').Length + segment.Item1.Length - segment.Item1.TrimEnd(' ').Length;
            if (core.Length == 0) spaces = segment.Item1.Length;
            int w = core.Length == 0 ? 0 : TextRenderer.MeasureText(core, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width - Overhang(font);
            return Math.Max(0, w) + spaces * SpaceWidth(font);
        }
        // 区切って測ると、区切りごとに文字の後ろの余白が加わる。その分（1区間あたり）。
        private static readonly Dictionary<Font, int> overhangs = new Dictionary<Font, int>();
        private static int Overhang(Font font)
        {
            int value;
            if (overhangs.TryGetValue(font, out value)) return value;
            Func<string, int> w = s => TextRenderer.MeasureText(s, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            value = Math.Max(0, (w("あ") * 2 - w("ああ") + w("n") * 2 - w("nn")) / 2);
            overhangs[font] = value;
            return value;
        }
        private static int SpaceWidth(Font font)
        {
            return Math.Max(2, TextRenderer.MeasureText("a a", font, Size.Empty, TextFormatFlags.NoPadding).Width - TextRenderer.MeasureText("aa", font, Size.Empty, TextFormatFlags.NoPadding).Width);
        }

        // ** で囲んだ部分を強調しながら、幅に収まるように折り返して並べる（禁則処理は BreakLines）。
        // 1文字ずつ測ると字間が開くので、同じ書式が続く区間をまとめて測り、まとめて描く。戻り値は下端。
        private static int FlowText(HelpLayout layout, string text, int left, int top, int width, int lineHeight, Color color, out int used, bool forceBold = false)
        {
            int y = top; used = 0;
            foreach (List<Tuple<string, bool>> segments in BreakLines(text, width, forceBold))
            {
                int x = 0;
                foreach (var segment in segments)
                {
                    layout.Texts.Add(new LaidText { Text = segment.Item1, At = new Point(left + x, y), Bold = segment.Item2, Color = segment.Item2 && !forceBold ? EmphasisColor : color });
                    x += SegmentWidth(segment);
                }
                used = Math.Max(used, x); y += lineHeight;
            }
            return Math.Max(y, top + lineHeight);
        }

        //--------------
        // 禁則処理（AI共通「禁則処理」）
        // ・文字を幅の外へ出さない（ぶら下げはしない）。行頭に来てはいけない文字は、直前の文字ごと次の行へ送る（追い出し）。
        // ・開きかっこは次の文字と一緒に送る。「〜」「＋」の前後、数字と続く単位、カタカナの語、強調した短い語句（操作名など）は切らない。
        // ・英語などは空白で折り返す。幅より長い1語だけは、はみ出さないよう途中で切る。
        //--------------
        internal const string NoLineStart = "、。，．,.)）」』】〕〉》］｝]}”’！？!?：:；;・…‥％%ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶー";
        internal const string NoLineEnd = "（「『【〔〈《［｛([{“‘";
        private const string Joiners = "〜～＋";

        // 1行ずつの区間（文字, 太字か）。各行の幅は width 以下。
        internal static List<List<Tuple<string, bool>>> BreakLines(string text, int width, bool forceBold = false)
        {
            width = Math.Max(1, width);
            List<List<Tuple<string, bool>>> units = Units(text, width, forceBold);
            var lines = new List<List<Tuple<string, bool>>>();
            var line = new List<Tuple<string, bool>>();
            Action newLine = () =>
            {
                // 行末の空白は描かない。
                while (line.Count > 0 && line[line.Count - 1].Item1.TrimEnd(' ').Length == 0) line.RemoveAt(line.Count - 1);
                if (line.Count > 0) { var last = line[line.Count - 1]; line[line.Count - 1] = Tuple.Create(last.Item1.TrimEnd(' '), last.Item2); }
                lines.Add(line); line = new List<Tuple<string, bool>>();
            };
            var queue = new Queue<List<Tuple<string, bool>>>(units);
            while (queue.Count > 0)
            {
                List<Tuple<string, bool>> unit = queue.Dequeue();
                bool blank = unit.All(s => s.Item1.Trim(' ').Length == 0);
                if (line.Count == 0 && blank) continue;   // 行頭の空白は描かない
                List<Tuple<string, bool>> trial = line;
                foreach (var s in unit) trial = Append(trial, s);
                if (SegmentsWidth(trial) <= width || blank) { line = trial; continue; }
                if (line.Count > 0) { newLine(); }
                List<Tuple<string, bool>> alone = new List<Tuple<string, bool>>();
                foreach (var s in unit) alone = Append(alone, s);
                if (SegmentsWidth(alone) <= width) { line = alone; continue; }
                // 1行に入らない塊: まず中を通常の単位に分け直して並べる（禁則は守る）。
                List<List<Tuple<string, bool>>> parts = Glue(unit.SelectMany(s => Tokens(s.Item1).Select(x => Tuple.Create(x, s.Item2))).ToList());
                if (parts.Count > 1)
                {
                    var rest = new Queue<List<Tuple<string, bool>>>(parts.Concat(queue));
                    queue = rest;
                    continue;
                }
                // それでも入らない1語（長いパスなど）: はみ出さないよう、入るところで切る。
                foreach (var s in unit)
                    foreach (char ch in s.Item1)
                    {
                        var piece = Tuple.Create(ch.ToString(), s.Item2);
                        List<Tuple<string, bool>> next = Append(line, piece);
                        if (line.Count > 0 && SegmentsWidth(next) > width) { newLine(); next = Append(line, piece); }
                        line = next;
                    }
            }
            if (line.Count > 0) newLine();
            return lines;
        }

        // 切ってよい位置で区切った塊（塊の中では折り返さない）。太字の区間をまたいでつなぐこともある（「**割り当て**」など）。
        private static List<List<Tuple<string, bool>>> Units(string text, int width, bool forceBold)
        {
            var tokens = new List<Tuple<string, bool>>();
            bool bold = false;
            foreach (string part in (text ?? "").Split(new[] { "**" }, StringSplitOptions.None))
            {
                bool style = bold || forceBold;
                // 強調した短い語句（空白を含まず、幅に収まる。操作名など）は1つの塊にする。
                if (part.Length > 0 && (bold || forceBold) && part.IndexOf(' ') < 0 && SegmentWidth(Tuple.Create(part, style)) <= width)
                    tokens.Add(Tuple.Create(part, style));
                else
                    foreach (string token in Tokens(part)) tokens.Add(Tuple.Create(token, style));
                bold = !bold;
            }
            return Glue(tokens);
        }

        // 切ってはいけない所でつなぐ（行頭禁則・開きかっこ・「〜」「＋」・数字と単位）。
        private static List<List<Tuple<string, bool>>> Glue(List<Tuple<string, bool>> tokens)
        {
            var units = new List<List<Tuple<string, bool>>>();
            bool glueNext = false;
            foreach (var token in tokens)
            {
                string s = token.Item1;
                if (s.Length == 0) continue;
                bool space = s.Trim(' ').Length == 0;
                List<Tuple<string, bool>> previous = units.Count > 0 ? units[units.Count - 1] : null;
                string before = previous == null ? "" : previous[previous.Count - 1].Item1;
                char last = before.Length > 0 ? before[before.Length - 1] : '\0';
                bool glue = previous != null && !space && last != ' ' && (glueNext
                    || NoLineStart.IndexOf(s[0]) >= 0                         // 行頭禁則: 前と一緒に送る
                    || Joiners.IndexOf(s[0]) >= 0                             // 「〜」「＋」の前で切らない
                    || NoLineEnd.IndexOf(last) >= 0                           // 開きかっこの後で切らない
                    || (char.IsDigit(last) && IsWide(s[0]) && NoLineEnd.IndexOf(s[0]) < 0));   // 数字と単位（8倍）
                if (glue) previous.Add(token); else units.Add(new List<Tuple<string, bool>> { token });
                glueNext = !space && Joiners.IndexOf(s[s.Length - 1]) >= 0;   // 「〜」「＋」の後でも切らない
            }
            return units;
        }

        private static bool IsWide(char ch) { return ch >= 0x2E80 || (ch >= 0xFF00 && ch <= 0xFFEF); }
        private static bool IsKatakana(char ch) { return ch >= 0x30A0 && ch <= 0x30FF; }

        // 空白・英数字の語・カタカナの語・それ以外の全角1文字に分ける。
        private static IEnumerable<string> Tokens(string text)
        {
            var current = new StringBuilder();
            int kind = 0;   // 1: 英数字の語, 2: カタカナの語
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                int k = ch == ' ' ? 0 : IsKatakana(ch) ? 2 : IsWide(ch) ? 3 : 1;
                if (current.Length > 0 && (k != kind || k == 3 || k == 0)) { yield return current.ToString(); current.Clear(); }
                current.Append(ch); kind = k;
            }
            if (current.Length > 0) yield return current.ToString();
        }

        //--------------
        // 図（コードで描く。図の文字は言語によらない記号・数字だけ）
        //--------------
        private static Size FigureSize(string name)
        {
            switch (name)
            {
                case "frames": return new Size(4 * 30 + 3 * 18, 34);
                case "uv": return new Size(2 * 96 + 16, 94);
                default: return new Size(0, 0);
            }
        }

        private static void DrawFigure(Graphics g, LaidFigure figure)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = figure.Bounds;
            if (figure.Name == "frames")
            {
                // コマ 1 → 2 → 3 → 4 の再生順。
                for (int i = 0; i < 4; i++)
                {
                    var box = new Rectangle(r.X + i * 48, r.Y + 2, 30, 30);
                    using (var fill = new SolidBrush(Color.FromArgb(60 + i * 25, 84, 73, 255))) using (GraphicsPath path = Rounded(box, 4)) g.FillPath(fill, path);
                    using (var pen = new Pen(Border)) using (GraphicsPath path = Rounded(box, 4)) g.DrawPath(pen, path);
                    TextRenderer.DrawText(g, (i + 1).ToString(), Fonts.Bold, box, TextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (i < 3) DrawArrow(g, new PointF(box.Right + 3, box.Y + 15), new PointF(box.Right + 15, box.Y + 15));
                }
            }
            else if (figure.Name == "uv")
            {
                // 左: DirectX（原点が左上、V は下へ）。右: OpenGL（原点が左下、V は上へ）。
                DrawUvBox(g, new Rectangle(r.X, r.Y, 96, 94), "DirectX", true);
                DrawUvBox(g, new Rectangle(r.X + 112, r.Y, 96, 94), "OpenGL", false);
            }
            g.Restore(state);
        }

        private static void DrawUvBox(Graphics g, Rectangle r, string label, bool originTop)
        {
            // 名前は図の下に置く（上へ伸びる V の矢印と重ならないように）。
            var box = new Rectangle(r.X + 14, r.Y + 12, 60, 50);
            TextRenderer.DrawText(g, label, Fonts.Bold, new Point(box.X, box.Bottom + 14), OperationColor, TextFormatFlags.NoPadding);
            using (var pen = new Pen(FigureLine)) g.DrawRectangle(pen, box);
            Point origin = originTop ? new Point(box.Left, box.Top) : new Point(box.Left, box.Bottom);
            using (var dot = new SolidBrush(Color.FromArgb(255, 196, 92))) g.FillEllipse(dot, origin.X - 4, origin.Y - 4, 8, 8);
            DrawArrow(g, new PointF(origin.X + 6, origin.Y), new PointF(box.Right + 8, origin.Y));
            TextRenderer.DrawText(g, "U", Fonts.Bold, new Point(box.Right + 10, origin.Y - 8), TextColor, TextFormatFlags.NoPadding);
            float vEnd = originTop ? box.Bottom + 8 : box.Top - 8;
            DrawArrow(g, new PointF(origin.X, origin.Y + (originTop ? 6 : -6)), new PointF(origin.X, vEnd));
            TextRenderer.DrawText(g, "V", Fonts.Bold, new Point(origin.X - 13, (int)vEnd - (originTop ? 12 : 2)), TextColor, TextFormatFlags.NoPadding);
        }

        private static void DrawArrow(Graphics g, PointF from, PointF to)
        {
            using (var pen = new Pen(FigureLine, 1.6f) { CustomEndCap = new AdjustableArrowCap(3.5f, 3.5f) }) g.DrawLine(pen, from, to);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath(); int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
    }
    // 項目の横に置く「？」。0.3秒カーソルを乗せると説明を出す。
    internal sealed class HelpMark : Control
    {
        private bool hover;
        private string help = "";

        private static ToolTip tipsInstance;
        private static ToolTip Tips { get { return tipsInstance ?? (tipsInstance = HelpTipStyle.Create()); } }
        // Windows の自動表示に任せると、起動直後やほかのヒントの直後に出ないことがあった。
        // 0.3秒乗せたら（押したらすぐ）自分で出す。出している間だけ札に文章を結び付ける（測り方は共通の Popup で行う）。
        private readonly Timer showTimer = new Timer { Interval = 300 };
        internal static int ShowCount;   // テスト用: 出した回数

        public HelpMark()
        {
            showTimer.Tick += (s, e) => { showTimer.Stop(); ShowHelp(); };
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Cursor = Cursors.Help; TabStop = false;
            AccessibleRole = AccessibleRole.HelpBalloon; AccessibleName = "?";
        }
        public string HelpText
        {
            get { return help; }
            set { help = value ?? ""; AccessibleDescription = HelpTipStyle.PlainText(help); if (shown) ShowHelp(); }
        }
        private bool shown;
        private void ShowHelp()
        {
            if (!IsHandleCreated || IsDisposed || string.IsNullOrEmpty(help) || !Visible) return;
            Tips.SetToolTip(this, help);
            Tips.Show(help, this, Width / 2 + 6, Height + 4, Tips.AutoPopDelay);
            shown = true; ShowCount++;
        }
        private void HideHelp()
        {
            showTimer.Stop();
            if (!shown) return;
            shown = false;
            if (IsHandleCreated) Tips.Hide(this);
            Tips.SetToolTip(this, null);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); showTimer.Stop(); showTimer.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); HideHelp(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { showTimer.Stop(); if (!shown) ShowHelp(); base.OnMouseDown(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) HideHelp(); base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { HideHelp(); showTimer.Dispose(); }
            base.Dispose(disposing);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color back = Parent != null ? RoundedPaint.ResolveBackdrop(Parent) : Color.FromArgb(24, 32, 38);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = Math.Min(Width, Height) - 2;
            var circle = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            Color line = hover ? Color.FromArgb(243, 245, 247) : Color.FromArgb(150, 160, 170);
            if (hover) using (var fill = new SolidBrush(Color.FromArgb(84, 73, 255))) g.FillEllipse(fill, circle);
            using (var pen = new Pen(hover ? Color.FromArgb(84, 73, 255) : line, 1.4f)) g.DrawEllipse(pen, circle);
            using (Font font = UiFont.Create(Math.Max(7f, size * 0.62f), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "?", font, circle, line, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
