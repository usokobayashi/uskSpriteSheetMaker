//==================================================
// UpdateChecker
// GitHub Releases の最新版を確認し、現在のバージョンより新しければ知らせるための情報を返す。
// ダウンロードや入れ替えはしない（利用者が Releases のページから手で更新する）。
//==================================================

using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace SpriteSheetMaker
{
    internal sealed class ReleaseInfo
    {
        public string Tag;      // 例 v1.0.1
        public Version Version;
        public string PageUrl;  // Releases の該当ページ
        public string Notes;    // 変更点（Release の説明文を短くした平文）。なければ空
    }

    internal static class UpdateChecker
    {
        public const string SettingKey = "checkUpdates";   // settings.ini。0 なら確認しない
        private const string LastCheckKey = "updateLastCheck";      // 最後に確認できた時刻（UTC）
        private const string LatestTagKey = "updateLatestTag";      // そのとき見つけた最新版のタグ
        private const string LatestUrlKey = "updateLatestUrl";
        private const string CardShownKey = "updateCardShownTag";   // 通知カードを出したバージョン
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private const int NotesMaxLines = 8;
        private const int NotesMaxLineLength = 90;
        private static readonly string[] LineBreaks = { "\r\n", "\n", "\r" };
        private const string Repository = "usokobayashi/uskSpriteSheetMaker";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        private const int TimeoutMilliseconds = 10000;
        private const int MaxResponseBytes = 1024 * 1024;   // 想定外に大きい応答は読まない

        [DataContract]
        private sealed class LatestReleaseJson
        {
            [DataMember(Name = "tag_name")] public string TagName;
            [DataMember(Name = "html_url")] public string HtmlUrl;
            [DataMember(Name = "body")] public string Body;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "prerelease")] public bool Prerelease;
        }

        public static bool IsEnabled
        {
            get { return AppSettings.Get(SettingKey) != "0"; }
            set { AppSettings.Set(SettingKey, value ? "1" : "0"); }
        }

        //--------------
        // CheckAsync
        //--------------
        // 最新の公開版の情報。通信できない・応答がおかしいときは null。例外は外へ出さない。
        public static Task<ReleaseInfo> CheckAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    return ParseLatestRelease(Download(LatestReleaseApi));
                }
                catch (Exception ex) when (ex is WebException || ex is IOException || ex is SerializationException ||
                    ex is InvalidOperationException || ex is NotSupportedException || ex is ArgumentException)
                {
                    return null;
                }
            });
        }

        private static string Download(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = AppInfo.Name + "/" + AppInfo.Version;   // GitHub API は User-Agent 必須
            request.Accept = "application/vnd.github+json";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes) throw new IOException("response too large");
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        //--------------
        // 解析・比較（テスト対象）
        //--------------
        // 下書き・プレリリース・バージョン番号として読めないタグは無視する。ページの URL はこのリポジトリのものだけ採用する。
        internal static ReleaseInfo ParseLatestRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            LatestReleaseJson parsed;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                parsed = new DataContractJsonSerializer(typeof(LatestReleaseJson)).ReadObject(stream) as LatestReleaseJson;
            if (parsed == null || parsed.Draft || parsed.Prerelease) return null;
            Version version;
            if (!TryParseVersion(parsed.TagName, out version)) return null;
            return new ReleaseInfo
            {
                Tag = parsed.TagName,
                Version = version,
                PageUrl = IsTrustedReleaseUrl(parsed.HtmlUrl) ? parsed.HtmlUrl : ReleasesPage,
                Notes = SummarizeNotes(parsed.Body)
            };
        }

        // "v1.2.3" / "1.2" などを 3桁（足りない桁は0）の Version にする。
        internal static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(1);
            if (trimmed.IndexOf('.') < 0) trimmed += ".0";
            Version parsed;
            if (!Version.TryParse(trimmed, out parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        internal static bool IsNewer(Version latest, string current)
        {
            Version installed;
            return latest != null && TryParseVersion(current, out installed) && latest > installed;
        }

        // Markdown の説明文を、カードに出せる短い平文にする（見出し行は省き、強調記号を外し、箇条書きは「・」）。
        internal static string SummarizeNotes(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            var lines = new System.Collections.Generic.List<string>();
            bool truncated = false;
            foreach (string raw in body.Split(LineBreaks, StringSplitOptions.None))
            {
                string line = StripControl(raw).Trim();
                if (line.Length == 0) continue;
                // 見出し（カード側に「変更点」の見出しがある）と GitHub が自動で付ける比較リンクは出さない。
                if (line.StartsWith("#") || line.IndexOf("Full Changelog", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ ")) line = "・" + line.Substring(2).Trim();
                line = line.Replace("**", "").Replace("__", "").Replace("`", "");
                if (line.Length == 0) continue;
                if (lines.Count == NotesMaxLines) { truncated = true; break; }
                if (line.Length > NotesMaxLineLength) line = line.Substring(0, NotesMaxLineLength) + "…";
                lines.Add(line);
            }
            if (truncated) lines.Add("…");
            return string.Join("\n", lines);
        }

        private static string StripControl(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
                if (!char.IsControl(c) && c != (char)0x202E && c != (char)0x202D) builder.Append(c);
            return builder.ToString();
        }

        //--------------
        // 1日1回・カードはバージョンごとに1回（settings.ini に記録）
        //--------------
        public static bool ShouldCheckNow(DateTime nowUtc)
        {
            return ShouldCheckNow(nowUtc, AppSettings.Get(LastCheckKey));
        }

        // 前回から24時間たったら確認する。記録が読めない・未来の時刻（時計の巻き戻し）のときも確認する。
        internal static bool ShouldCheckNow(DateTime nowUtc, string lastCheck)
        {
            DateTime last;
            if (!DateTime.TryParse(lastCheck, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out last)) return true;
            return last > nowUtc || nowUtc - last >= CheckInterval;
        }

        public static void RememberCheck(DateTime nowUtc, ReleaseInfo latest)
        {
            AppSettings.Set(LastCheckKey, nowUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            AppSettings.Set(LatestTagKey, latest != null ? latest.Tag : null);
            AppSettings.Set(LatestUrlKey, latest != null ? latest.PageUrl : null);
        }

        // 今日すでに確認していて、そのとき新しいバージョンを見つけていれば、その情報（変更点は持たない）。
        public static ReleaseInfo CachedNewerRelease()
        {
            string tag = AppSettings.Get(LatestTagKey);
            Version version;
            if (!TryParseVersion(tag, out version) || !IsNewer(version, AppInfo.Version)) return null;
            string url = AppSettings.Get(LatestUrlKey);
            return new ReleaseInfo { Tag = tag, Version = version, PageUrl = IsTrustedReleaseUrl(url) ? url : ReleasesPage, Notes = "" };
        }

        public static bool ShouldShowCard(ReleaseInfo release)
        {
            return release != null && !string.Equals(AppSettings.Get(CardShownKey), release.Tag, StringComparison.Ordinal);
        }

        public static void MarkCardShown(ReleaseInfo release)
        {
            if (release != null) AppSettings.Set(CardShownKey, release.Tag);
        }

        internal static bool IsTrustedReleaseUrl(string url)
        {
            Uri uri;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/" + Repository + "/releases/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
