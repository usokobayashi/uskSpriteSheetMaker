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
            foreach (TestCase test in cases)
            {
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
