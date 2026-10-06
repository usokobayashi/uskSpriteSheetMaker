using System;
using System.Collections.Generic;

namespace SpriteSheetMakerTests
{
    internal sealed class AssertFailedException : Exception
    {
        public AssertFailedException(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void IsTrue(bool condition, string message)
        {
            if (!condition) throw new AssertFailedException(message);
        }

        public static void IsFalse(bool condition, string message)
        {
            if (condition) throw new AssertFailedException(message);
        }

        public static void AreEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertFailedException(message + " (expected " + expected + ", got " + actual + ")");
        }

        public static void InRange(double value, double min, double max, string message)
        {
            if (value < min || value > max)
                throw new AssertFailedException(message + " (value " + value + " not in [" + min + ", " + max + "])");
        }
    }

    internal sealed class TestCase
    {
        public string Name;
        public Action Action;
    }

    internal static class TestRunner
    {
        public static int RunAll(IList<TestCase> cases)
        {
            int failed = 0;
            // 調査用: TEST_WATCHDOG=1 のとき、1件が90秒を超えたら実行中の場所（呼び出し履歴）を出す。
            System.Threading.Thread main = System.Threading.Thread.CurrentThread;
            string current = null; var started = System.Diagnostics.Stopwatch.StartNew(); bool dumped = false;
            if (Environment.GetEnvironmentVariable("TEST_WATCHDOG") == "1")
            {
                var watch = new System.Threading.Thread(() =>
                {
                    while (true)
                    {
                        System.Threading.Thread.Sleep(5000);
                        if (current != null && !dumped && started.Elapsed.TotalSeconds > 90)
                        {
                            dumped = true;
#pragma warning disable 618
                            try { main.Suspend(); Console.WriteLine("WATCHDOG " + current + Environment.NewLine + new System.Diagnostics.StackTrace(main, true)); }
                            catch (Exception ex) { Console.WriteLine("WATCHDOG failed " + ex.Message); }
                            finally { try { main.Resume(); } catch { } }
#pragma warning restore 618
                            Console.Out.Flush();
                        }
                    }
                }) { IsBackground = true };
                watch.Start();
            }
            foreach (TestCase test in cases)
            {
                current = test.Name; started.Restart(); dumped = false;
                try
                {
                    test.Action();
                    Console.WriteLine("[PASS] " + test.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("[FAIL] " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine(cases.Count + " 件中 " + (cases.Count - failed) + " 件成功、" + failed + " 件失敗");
            return failed;
        }
    }
}
