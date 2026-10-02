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
            cases.AddRange(ApngWriterTests.All());
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
            cases.AddRange(CellRemapTests.All());
            cases.AddRange(UpdateCheckerTests.All());
            cases.AddRange(WebPWriterTests.All());
            cases.AddRange(ColliderTests.All());
            cases.AddRange(FolderAddTests.All());
            cases.AddRange(PageTransitionTests.All());
            cases.AddRange(MotionTests.All());
            cases.AddRange(DetailFixTests.All());

            int failed = TestRunner.RunAll(cases);
            return failed == 0 ? 0 : 1;
        }
    }
}
