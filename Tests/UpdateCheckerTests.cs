//==================================================
// UpdateCheckerTests
// 新しいバージョンの通知: バージョン番号の比較・Releases 応答の解析・1日1回の判定・変更点の要約・カードの表示（通信はしない）。
//==================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class UpdateCheckerTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Update_Version_ParsesTagsAndCompares", Action = Version_ParsesTagsAndCompares };
            yield return new TestCase { Name = "Update_Parse_ReadsLatestRelease", Action = Parse_ReadsLatestRelease };
            yield return new TestCase { Name = "Update_Parse_IgnoresDraftPrereleaseAndBadTags", Action = Parse_IgnoresDraftPrereleaseAndBadTags };
            yield return new TestCase { Name = "Update_Parse_OnlyTrustsThisRepositoryUrl", Action = Parse_OnlyTrustsThisRepositoryUrl };
            yield return new TestCase { Name = "Update_Notes_SummarizesMarkdown", Action = Notes_SummarizesMarkdown };
            yield return new TestCase { Name = "Update_ShouldCheckNow_OncePerDay", Action = ShouldCheckNow_OncePerDay };
            yield return new TestCase { Name = "Update_Card_ShowsInEveryLanguageAndCloses", Action = Card_ShowsInEveryLanguageAndCloses };
        }

        private static void Version_ParsesTagsAndCompares()
        {
            Version v;
            Assert.IsTrue(UpdateChecker.TryParseVersion("v1.0.1", out v) && v == new Version(1, 0, 1), "v prefix");
            Assert.IsTrue(UpdateChecker.TryParseVersion("1.2", out v) && v == new Version(1, 2, 0), "missing patch is 0");
            Assert.IsTrue(UpdateChecker.TryParseVersion("V2", out v) && v == new Version(2, 0, 0), "major only");
            Assert.IsFalse(UpdateChecker.TryParseVersion("latest", out v), "not a version");
            Assert.IsFalse(UpdateChecker.TryParseVersion(null, out v), "null");
            Assert.IsTrue(UpdateChecker.IsNewer(new Version(1, 0, 1), "1.0.0"), "patch is newer");
            Assert.IsTrue(UpdateChecker.IsNewer(new Version(1, 10, 0), "1.9.9"), "numeric, not text order");
            Assert.IsFalse(UpdateChecker.IsNewer(new Version(1, 0, 0), "1.0.0"), "same version");
            Assert.IsFalse(UpdateChecker.IsNewer(new Version(0, 9, 0), "1.0.0"), "older release");
        }

        private static void Parse_ReadsLatestRelease()
        {
            string json = "{\"tag_name\":\"v1.0.1\",\"html_url\":\"https://github.com/usokobayashi/uskSpriteSheetMaker/releases/tag/v1.0.1\"," +
                "\"draft\":false,\"prerelease\":false,\"body\":\"## 変更点\\r\\n- 横セル数の修正\\r\\n- **更新の通知**\",\"assets\":[]}";
            ReleaseInfo info = UpdateChecker.ParseLatestRelease(json);
            Assert.IsTrue(info != null, "parsed");
            Assert.AreEqual("v1.0.1", info.Tag, "tag");
            Assert.AreEqual(new Version(1, 0, 1), info.Version, "version");
            Assert.AreEqual("https://github.com/usokobayashi/uskSpriteSheetMaker/releases/tag/v1.0.1", info.PageUrl, "page");
            Assert.AreEqual("・横セル数の修正\n・更新の通知", info.Notes, "notes (headings are dropped)");
            Assert.AreEqual("・fix", UpdateChecker.SummarizeNotes("## What's Changed\n* fix\n\n**Full Changelog**: https://github.com/x/y/compare/v1...v2"),
                "GitHub's generated heading and changelog link are dropped");
        }

        private static void Parse_IgnoresDraftPrereleaseAndBadTags()
        {
            Assert.IsTrue(UpdateChecker.ParseLatestRelease("{\"tag_name\":\"v2.0.0\",\"draft\":true}") == null, "draft");
            Assert.IsTrue(UpdateChecker.ParseLatestRelease("{\"tag_name\":\"v2.0.0\",\"prerelease\":true}") == null, "prerelease");
            Assert.IsTrue(UpdateChecker.ParseLatestRelease("{\"tag_name\":\"nightly\"}") == null, "tag is not a version");
            Assert.IsTrue(UpdateChecker.ParseLatestRelease("") == null, "empty response");
        }

        private static void Parse_OnlyTrustsThisRepositoryUrl()
        {
            ReleaseInfo info = UpdateChecker.ParseLatestRelease("{\"tag_name\":\"v1.0.1\",\"html_url\":\"https://evil.example.com/usokobayashi/uskSpriteSheetMaker/releases/\"}");
            Assert.AreEqual(UpdateChecker.ReleasesPage, info.PageUrl, "other host falls back to the fixed Releases page");
            Assert.IsFalse(UpdateChecker.IsTrustedReleaseUrl("http://github.com/usokobayashi/uskSpriteSheetMaker/releases/tag/v1"), "http");
            Assert.IsFalse(UpdateChecker.IsTrustedReleaseUrl("https://github.com/someone/else/releases/tag/v1"), "other repository");
            Assert.IsTrue(UpdateChecker.IsTrustedReleaseUrl("https://github.com/usokobayashi/uskSpriteSheetMaker/releases/tag/v1.0.1"), "this repository");
        }

        private static void Notes_SummarizesMarkdown()
        {
            Assert.AreEqual("", UpdateChecker.SummarizeNotes(null), "no body");
            Assert.AreEqual("ab", UpdateChecker.SummarizeNotes("a\u0007b" + (char)0x202E), "control and direction characters are removed");
            var many = new List<string>();
            for (int i = 1; i <= 20; i++) many.Add("- item " + i);
            string summary = UpdateChecker.SummarizeNotes(string.Join("\n", many));
            string[] lines = summary.Split('\n');
            Assert.AreEqual(9, lines.Length, "8 lines and an ellipsis");
            Assert.AreEqual("…", lines[8], "ellipsis line");
            string longLine = UpdateChecker.SummarizeNotes(new string('x', 300));
            Assert.AreEqual(91, longLine.Length, "long lines are cut with an ellipsis");
        }

        private static void ShouldCheckNow_OncePerDay()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            Func<DateTime, string> iso = t => t.ToString("o", CultureInfo.InvariantCulture);
            Assert.IsTrue(UpdateChecker.ShouldCheckNow(now, null), "never checked");
            Assert.IsTrue(UpdateChecker.ShouldCheckNow(now, "broken"), "unreadable record");
            Assert.IsFalse(UpdateChecker.ShouldCheckNow(now, iso(now.AddHours(-3))), "checked 3 hours ago");
            Assert.IsFalse(UpdateChecker.ShouldCheckNow(now, iso(now.AddHours(-23.9))), "just under a day");
            Assert.IsTrue(UpdateChecker.ShouldCheckNow(now, iso(now.AddHours(-24))), "a day later");
            Assert.IsTrue(UpdateChecker.ShouldCheckNow(now, iso(now.AddDays(2))), "record in the future (clock moved back)");
        }

        // カードはどの言語でも画面内に収まり、文字が切れない幅になる。× で閉じるとチップだけ残る。
        private static void Card_ShowsInEveryLanguageAndCloses()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Loc.SettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpriteSheetMakerTests-update.ini");
            UiLanguage original = Loc.Current;
            try
            {
                UpdateChecker.IsEnabled = false;   // 表示しても通信しない
                using (var form = new MainForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new System.Drawing.Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Size = new System.Drawing.Size(1400, 900);
                    form.Show();
                    var release = new ReleaseInfo
                    {
                        Tag = "v1.0.1",
                        Version = new Version(1, 0, 1),
                        PageUrl = UpdateChecker.ReleasesPage,
                        Notes = "・横セル数を変えたときの割り当てのずれを修正\n・新しいバージョンの通知"
                    };
                    foreach (UiLanguage language in Enum.GetValues(typeof(UiLanguage)))
                    {
                        Loc.SetLanguage(language);
                        form.ShowUpdateNotice(release, true);
                        var chip = (Button)typeof(MainForm).GetField("updateChip", flags).GetValue(form);
                        var card = (Control)typeof(MainForm).GetField("updateCard", flags).GetValue(form);
                        Assert.IsTrue(chip.Visible, "chip visible (" + language + ")");
                        Assert.IsTrue(chip.Text.Contains("v1.0.1"), "chip shows the version (" + language + ")");
                        Assert.IsTrue(card != null && card.Parent == form, "card shown (" + language + ")");
                        Assert.IsTrue(card.Right <= form.ClientSize.Width && card.Left >= 0, "card fits horizontally (" + language + ")");
                        foreach (Control c in Descendants(card))
                        {
                            var label = c as Label;
                            if (label == null) continue;
                            Assert.IsTrue(label.PreferredSize.Width <= Math.Max(label.Width, label.MaximumSize.Width) + 1,
                                "label is not cut: " + label.Text + " (" + language + ")");
                        }
                    }
                    form.ShowUpdateNotice(release, false);
                    Assert.IsTrue(typeof(MainForm).GetField("updateCard", flags).GetValue(form) == null, "card closed when not requested");
                    form.ShowUpdateNotice(null, false);
                    Assert.IsFalse(((Button)typeof(MainForm).GetField("updateChip", flags).GetValue(form)).Visible, "chip hidden without an update");
                }
            }
            finally
            {
                Loc.SetLanguage(original);
            }
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control grandchild in Descendants(child)) yield return grandchild;
            }
        }
    }
}
