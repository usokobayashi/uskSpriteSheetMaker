using System;
using System.Collections.Generic;

namespace SpriteSheetMakerTests
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            var cases = new List<TestCase>();
            cases.AddRange(GifWriterTests.All());
            cases.AddRange(ImagePipelineTests.All());
            cases.AddRange(PreviewCanvasTests.All());
            cases.AddRange(TgaWriterTests.All());
            cases.AddRange(UndoManagerTests.All());
            cases.AddRange(PreviewSimulationTests.All());
            cases.AddRange(ColorPickerTests.All());
            cases.AddRange(LocalizationTests.All());
            cases.AddRange(ProjectFileTests.All());
            cases.AddRange(MemoTests.All());
            cases.AddRange(StreamingExportTests.All());
            cases.AddRange(SecurityTests.All());

            int failed = TestRunner.RunAll(cases);
            return failed == 0 ? 0 : 1;
        }
    }
}
