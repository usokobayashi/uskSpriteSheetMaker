using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // シート上に置く付箋メモ。位置と幅はシートの画像座標（ピクセル）で、ズームに合わせて拡大縮小する。
    // 文字数・座標・幅には上限があり、貼り付けや不正なファイルから来た値でも壊れないよう MemoText で整える。
    public sealed class SheetMemo
    {
        public float X;
        public float Y;
        public float Width = 80;
        private string text = "";
        private float height;
        private List<string> lines;
        private float lineHeight;
        private float layoutWidth;

        public string Text
        {
            get { return text; }
            set { text = MemoText.Sanitize(value); InvalidateLayout(); }
        }

        // 本文を、全角10文字ごと（英単語は単語ごと）に折り返した行。ズームによらず同じ折り返しにする。
        public IList<string> Lines
        {
            get { EnsureLayout(); return lines; }
        }

        public float LineHeight
        {
            get { EnsureLayout(); return lineHeight; }
        }

        // 幅と本文の行数から決まる高さ（本文が増えるたびに1行ずつ増える）。
        public float Height
        {
            get { EnsureLayout(); return height; }
        }

        public void InvalidateLayout() { height = 0; lines = null; }

        private void EnsureLayout()
        {
            if (lines != null && layoutWidth == Width && height > 0) return;
            MemoText.Layout(this, out lines, out lineHeight);
            layoutWidth = Width;
            height = 2 * MemoText.Padding(Width) + Math.Max(1, lines.Count) * lineHeight;
        }
    }

    // メモの寸法・文字・座標の規則と、安全対策。
    public static class MemoText
    {
        public const int MaxLength = 200;               // 文字（書記素）の上限
        public const float MinWidth = 24f, MaxWidth = 20000f;
        public const float MaxCoordinate = 100000f;
        public const int MaxMemos = 300;

        // 1行に全角10文字が収まる幅を基準にする。文字の大きさは、今のフォントで全角10文字が
        // 内側の幅の94%になる大きさ（11文字目で次の行になる）。フォントが変わったら測り直す。
        private static float fullWidthAdvance;   // 全角1文字の幅（フォントサイズに対する倍率）

        public static void ResetFontMetrics() { fullWidthAdvance = 0; }

        private static float FullWidthAdvance()
        {
            if (fullWidthAdvance <= 0)
            {
                using (Font font = UiFont.Create(100f, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    Size size = TextRenderer.MeasureText(new string('\u3042', 10), font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
                    fullWidthAdvance = Math.Max(0.5f, size.Width / 1000f);
                }
            }
            return fullWidthAdvance;
        }

        public static float FontSize(float width) { return InnerWidth(width) * 0.94f / (10f * FullWidthAdvance()); }
        public static float Padding(float width) { return width * 0.08f; }
        public static float InnerWidth(float width) { return width - 2 * Padding(width); }
        public static float CornerRadius(float width) { return width * 0.06f; }

        // 貼り付け・入力・ファイルからの文字を安全にする: 改行/タブは空白、制御文字と表示を乱す文字
        // （方向制御・ゼロ幅・私用領域の孤立サロゲート）は取り除き、上限で切る（絵文字などは途中で割らない）。
        public static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var builder = new StringBuilder(Math.Min(input.Length, MaxLength * 2));
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\r' && i + 1 < input.Length && input[i + 1] == '\n') continue;     // CRLFは空白1つ
                if (c == '\r' || c == '\n' || c == '\t' || c == '\u2028' || c == '\u2029') { builder.Append(' '); continue; }
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1])) { builder.Append(c).Append(input[i + 1]); i++; }
                    continue;                                                     // 孤立した上位サロゲート
                }
                if (char.IsLowSurrogate(c)) continue;                             // 孤立した下位サロゲート
                if (char.IsControl(c)) continue;
                if ((c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') ||
                    (c >= '\u2060' && c <= '\u2064') || c == '\uFEFF' || c == '\uFFFE' || c == '\uFFFF') continue;
                builder.Append(c);
            }
            return TruncateElements(builder.ToString(), MaxLength);
        }

        public static string TruncateElements(string value, int maxElements)
        {
            if (value.Length <= maxElements) { if (new StringInfo(value).LengthInTextElements <= maxElements) return value; }
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
            var builder = new StringBuilder();
            int count = 0;
            while (enumerator.MoveNext())
            {
                if (++count > maxElements) break;
                builder.Append((string)enumerator.Current);
            }
            return builder.ToString();
        }

        public static float SafeCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            return Math.Max(-MaxCoordinate, Math.Min(MaxCoordinate, value));
        }

        public static float SafeWidth(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 80;
            return Math.Max(MinWidth, Math.Min(MaxWidth, value));
        }

        // 幅と本文から、折り返した行と1行の高さ（画像座標）を求める。字形の丸めに左右されにくいよう
        // 4倍のサイズで測る。英数字の連なりは単語として扱い、収まらないときだけ途中で折る。
        public static void Layout(SheetMemo memo, out List<string> lines, out float lineHeight)
        {
            const int scale = 4;
            float fontWorld = FontSize(memo.Width);
            float maxWidth = InnerWidth(memo.Width) * scale;
            lines = new List<string>();
            using (Font font = UiFont.Create(Math.Max(1f, fontWorld * scale), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Func<string, float> measure = value => value.Length == 0 ? 0 : TextRenderer.MeasureText(value, font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left).Width;
                lineHeight = TextRenderer.MeasureText("\u3042A", font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left).Height / (float)scale;

                var current = new StringBuilder();
                foreach (string token in Tokenize(memo.Text))
                {
                    bool isSpace = token == " ";
                    if (measure(current.ToString() + token) <= maxWidth) { current.Append(token); continue; }
                    if (isSpace) { lines.Add(current.ToString()); current.Clear(); continue; }
                    if (current.Length > 0) { lines.Add(current.ToString().TrimEnd()); current.Clear(); }
                    if (measure(token) <= maxWidth) { current.Append(token); continue; }
                    // 1つの単語が行より長いときは、文字の単位で折る。
                    TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(token);
                    while (elements.MoveNext())
                    {
                        string element = (string)elements.Current;
                        if (current.Length > 0 && measure(current.ToString() + element) > maxWidth)
                        {
                            lines.Add(current.ToString());
                            current.Clear();
                        }
                        current.Append(element);
                    }
                }
                lines.Add(current.ToString().TrimEnd());
            }
        }

        // 英数字（と記号）の連なりを1つの単語、空白を1つ、それ以外（日本語など）は1文字ずつにする。
        private static IEnumerable<string> Tokenize(string text)
        {
            var word = new StringBuilder();
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string element = (string)elements.Current;
                bool latin = element.Length == 1 && element[0] > ' ' && element[0] < '\u0250';
                if (latin) { word.Append(element); continue; }
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return element == " " ? " " : element;
            }
            if (word.Length > 0) yield return word.ToString();
        }
    }

    public sealed class MemoEventArgs : EventArgs
    {
        public SheetMemo Memo;
    }
}
