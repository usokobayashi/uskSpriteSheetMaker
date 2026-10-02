using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class LocalizationTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Loc_Parse_HandlesCommentsEscapesAndFirstDuplicate", Action = Loc_Parse_HandlesCommentsEscapesAndFirstDuplicate };
            yield return new TestCase { Name = "Loc_FallsBackToJapaneseThenKey", Action = Loc_FallsBackToJapaneseThenKey };
            yield return new TestCase { Name = "Loc_LanguageFiles_HaveNoDuplicateOrOrphanKeys", Action = Loc_LanguageFiles_HaveNoDuplicateOrOrphanKeys };
            yield return new TestCase { Name = "Loc_LanguageFiles_AreCompleteAndFreeOfJapanese", Action = Loc_LanguageFiles_AreCompleteAndFreeOfJapanese };
            yield return new TestCase { Name = "Loc_Translations_KeepPlaceholders", Action = Loc_Translations_KeepPlaceholders };
        }

        private static void Loc_Parse_HandlesCommentsEscapesAndFirstDuplicate()
        {
            string text = "# comment\r\n\r\na.b=one\r\nc=line1\\nline2\r\nd = back\\\\slash \r\na.b=two\r\n";
            Dictionary<string, string> table = Loc.Parse(text);
            Assert.AreEqual("one", table["a.b"], "first duplicate wins");
            Assert.AreEqual("line1\nline2", table["c"], "backslash-n becomes a newline");
            Assert.AreEqual("back\\slash", table["d"], "spaces are trimmed and a double backslash becomes one");
            Assert.AreEqual(3, table.Count, "comments and blank lines are ignored");
            Assert.AreEqual(1, Loc.FindDuplicateKeys("x=1\nx=2\ny=3").Count, "duplicates are detected");
        }

        private static void Loc_FallsBackToJapaneseThenKey()
        {
            Loc.ResetForTests();
            Loc.SettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpriteSheetMakerTests-loc.ini");
            Loc.Initialize();
            Loc.SetTableForTests(UiLanguage.Japanese, "k.both=日本語\nk.jaOnly=日本語のみ");
            Loc.SetTableForTests(UiLanguage.English, "k.both=English\nk.empty=");
            Loc.SetLanguage(UiLanguage.English);
            Assert.AreEqual("English", Loc.T("k.both"), "translated key");
            Assert.AreEqual("日本語のみ", Loc.T("k.jaOnly"), "untranslated key falls back to Japanese");
            Assert.AreEqual("k.unknown", Loc.T("k.unknown"), "unknown key returns the key");
            Assert.IsTrue(Loc.GetMissingKeys().Contains("k.unknown"), "unknown key is recorded");
            Loc.ResetForTests();
        }

        private static void Loc_LanguageFiles_HaveNoDuplicateOrOrphanKeys()
        {
            Loc.ResetForTests();
            Dictionary<string, string> ja = Loc.Parse(Loc.GetResourceText(UiLanguage.Japanese));
            Assert.IsTrue(ja.Count > 0, "ja.lang is embedded");
            foreach (LanguageInfo info in Loc.All)
            {
                string text = Loc.GetResourceText(info.Language);
                Assert.AreEqual(0, Loc.FindDuplicateKeys(text).Count, info.Code + ".lang has duplicate keys");
                foreach (string key in Loc.Parse(text).Keys)
                    Assert.IsTrue(ja.ContainsKey(key), info.Code + ".lang has a key missing in ja.lang: " + key);
            }
            foreach (KeyValuePair<string, string> pair in ja)
                Assert.IsTrue(pair.Value.Length > 0, "ja.lang value is empty: " + pair.Key);
        }

        // どの言語にも日本語の全キーがあり（未訳で日本語が出ることがない）、英・中・インドネシア語の値に
        // かな・日本語の中黒「・」が残っていない（直訳のまま日本語の記号が混ざるのを防ぐ）。
        private static void Loc_LanguageFiles_AreCompleteAndFreeOfJapanese()
        {
            Loc.ResetForTests();
            Dictionary<string, string> ja = Loc.Parse(Loc.GetResourceText(UiLanguage.Japanese));
            foreach (LanguageInfo info in Loc.All)
            {
                if (info.Language == UiLanguage.Japanese) continue;
                Dictionary<string, string> table = Loc.Parse(Loc.GetResourceText(info.Language));
                foreach (KeyValuePair<string, string> pair in ja)
                {
                    string value;
                    Assert.IsTrue(table.TryGetValue(pair.Key, out value) && value.Length > 0, info.Code + ".lang is missing " + pair.Key);
                    foreach (char c in value)
                        Assert.IsFalse(c >= '぀' && c <= 'ヿ', info.Code + ".lang has Japanese kana or ・ in " + pair.Key + ": " + value);
                }
            }
        }

        private static void Loc_Translations_KeepPlaceholders()
        {
            Loc.ResetForTests();
            Dictionary<string, string> ja = Loc.Parse(Loc.GetResourceText(UiLanguage.Japanese));
            foreach (LanguageInfo info in Loc.All)
            {
                foreach (KeyValuePair<string, string> pair in Loc.Parse(Loc.GetResourceText(info.Language)))
                {
                    string expected = Placeholders(ja[pair.Key]);
                    Assert.AreEqual(expected, Placeholders(pair.Value), info.Code + " placeholders differ for " + pair.Key);
                }
            }
        }

        private static string Placeholders(string value)
        {
            return string.Join(",", Regex.Matches(value, @"\{\d+\}").Cast<Match>().Select(m => m.Value).OrderBy(v => v));
        }
    }
}
