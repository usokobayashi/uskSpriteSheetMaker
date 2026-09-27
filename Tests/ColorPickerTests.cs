using System.Collections.Generic;
using System.Drawing;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class ColorPickerTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "ColorPicker_HsvRoundTrip", Action = HsvRoundTrip };
        }

        // SquareColorPickerは手書きのHSV<->RGB変換を持つため、往復精度を確認する。
        // 丸め誤差を見込んで±1階調まで許容する。
        private static void HsvRoundTrip()
        {
            var picker = new SquareColorPicker();
            Color[] colors =
            {
                Color.Red, Color.Lime, Color.Blue, Color.White, Color.Black,
                Color.FromArgb(128, 64, 200), Color.FromArgb(10, 10, 10), Color.FromArgb(200, 200, 60)
            };

            foreach (Color c in colors)
            {
                picker.SelectedColor = c;
                Color round = picker.SelectedColor;
                Assert.InRange(round.R, c.R - 1, c.R + 1, "R channel round-trip for " + c);
                Assert.InRange(round.G, c.G - 1, c.G + 1, "G channel round-trip for " + c);
                Assert.InRange(round.B, c.B - 1, c.B + 1, "B channel round-trip for " + c);
            }
        }
    }
}
