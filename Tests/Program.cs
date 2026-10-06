using System;
using System.Collections.Generic;
using System.Linq;

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
            cases.AddRange(MapTests.All());
            cases.AddRange(MapLayerDemo.All());
            cases.AddRange(LargeExportTests.All());
            cases.AddRange(PerfProbe.All());
            if (Environment.GetEnvironmentVariable("MAP_TEST_ONLY") == "1") cases = new List<TestCase>(MapTests.All().Concat(MapLayerDemo.All()).Concat(LargeExportTests.All()).Concat(PerfProbe.All()));

            int failed = TestRunner.RunAll(cases);
            return failed == 0 ? 0 : 1;
        }
    }
}
