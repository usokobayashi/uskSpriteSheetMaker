using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace SpriteSheetMaker
{
    // 表示言語。追加するときは、この列挙・LanguageInfo・Localization/<コード>.lang・
    // csprojのEmbeddedResourceの4か所を揃える（手順は仕様「多言語対応」に従う）。
    public enum UiLanguage
    {
        Japanese,
        English,
        ChineseSimplified,
        Indonesian
    }

    // 言語ごとの固定情報。表示名は「その言語での自称」で、翻訳対象にしない。
    public sealed class LanguageInfo
    {
        public UiLanguage Language;
        public string Code;       // 設定ファイル・.langファイル名に使う
        public string NativeName; // 言語選択の表示名
        public string CultureName;
    }

    // 文言テーブルと現在言語。文言はキーで参照し、現在言語→日本語→キー名の順に解決する。
    public static class Loc
    {
        private const string ResourcePrefix = "SpriteSheetMaker.Localization.";
        private const string SettingsKey = "language";

        private static readonly LanguageInfo[] Languages =
        {
            new LanguageInfo { Language = UiLanguage.Japanese, Code = "ja", NativeName = "日本語", CultureName = "ja" },
            new LanguageInfo { Language = UiLanguage.English, Code = "en", NativeName = "English", CultureName = "en" },
            new LanguageInfo { Language = UiLanguage.ChineseSimplified, Code = "zh-CN", NativeName = "简体中文", CultureName = "zh" },
            new LanguageInfo { Language = UiLanguage.Indonesian, Code = "id", NativeName = "Bahasa Indonesia", CultureName = "id" }
        };

        private static readonly Dictionary<UiLanguage, Dictionary<string, string>> Tables =
            new Dictionary<UiLanguage, Dictionary<string, string>>();
        private static readonly HashSet<string> MissingKeys = new HashSet<string>();
        private static bool initialized;

        public static event EventHandler LanguageChanged;

        public static UiLanguage Current { get; private set; }

        // 設定ファイルの場所。テストでは一時フォルダへ差し替える。
        public static string SettingsPath { get; set; }

        public static IList<LanguageInfo> All { get { return Languages; } }

        public static LanguageInfo Info(UiLanguage language)
        {
            foreach (LanguageInfo info in Languages)
                if (info.Language == language) return info;
            return Languages[0];
        }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            if (string.IsNullOrEmpty(SettingsPath))
                SettingsPath = Path.Combine(AppInfo.DataDirectory, "settings.ini");
            Current = ReadSavedLanguage() ?? DetectSystemLanguage();
        }

        public static void ResetForTests()
        {
            initialized = false;
            Tables.Clear();
            MissingKeys.Clear();
            Current = UiLanguage.Japanese;
        }

        public static void SetLanguage(UiLanguage language)
        {
            Initialize();
            if (Current == language) return;
            Current = language;
            WriteSavedLanguage(language);
            EventHandler handler = LanguageChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }

        public static string T(string key)
        {
            Initialize();
            string value;
            if (TryGet(Current, key, out value)) return value;
            if (Current != UiLanguage.Japanese && TryGet(UiLanguage.Japanese, key, out value)) return value;
            lock (MissingKeys) MissingKeys.Add(key);
            return key;
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }

        // 日本語(基準言語)に無いキーを参照した記録。空であることをテストで確認する。
        public static IList<string> GetMissingKeys()
        {
            lock (MissingKeys) return new List<string>(MissingKeys);
        }

        public static IDictionary<string, string> GetTable(UiLanguage language)
        {
            return Load(language);
        }

        public static void SetTableForTests(UiLanguage language, string text)
        {
            Tables[language] = Parse(text);
        }

        private static bool TryGet(UiLanguage language, string key, out string value)
        {
            value = null;
            Dictionary<string, string> table = Load(language);
            return table.TryGetValue(key, out value) && value.Length > 0;
        }

        private static Dictionary<string, string> Load(UiLanguage language)
        {
            Dictionary<string, string> table;
            if (Tables.TryGetValue(language, out table)) return table;
            table = Parse(GetResourceText(language));
            Tables[language] = table;
            return table;
        }

        // 埋め込まれた言語ファイルの原文。ファイルが無い言語は空文字。
        public static string GetResourceText(UiLanguage language)
        {
            string resourceName = ResourcePrefix + Info(language).Code + ".lang";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return "";
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        // 書式: `キー=値`。`#`で始まる行と空行は無視。値の`\n`は改行、`\\`は`\`。
        // 同じキーが複数ある場合は最初のものを採用する（重複はテストで検出する）。
        public static Dictionary<string, string> Parse(string text)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').TrimStart();
                if (line.Length == 0 || line[0] == '#') continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                string key = line.Substring(0, split).Trim();
                if (table.ContainsKey(key)) continue;
                table[key] = Unescape(line.Substring(split + 1).Trim());
            }
            return table;
        }

        public static IList<string> FindDuplicateKeys(string text)
        {
            var seen = new HashSet<string>();
            var duplicates = new List<string>();
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').TrimStart();
                if (line.Length == 0 || line[0] == '#') continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                string key = line.Substring(0, split).Trim();
                if (!seen.Add(key)) duplicates.Add(key);
            }
            return duplicates;
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == 'n') { builder.Append('\n'); i++; continue; }
                    if (next == '\\') { builder.Append('\\'); i++; continue; }
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static UiLanguage DetectSystemLanguage()
        {
            string twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            foreach (LanguageInfo info in Languages)
                if (string.Equals(info.CultureName, twoLetter, StringComparison.OrdinalIgnoreCase))
                    return info.Language;
            return UiLanguage.English;
        }

        private static UiLanguage? ReadSavedLanguage()
        {
            string code = AppSettings.Get(SettingsKey);
            if (code == null) return null;
            foreach (LanguageInfo info in Languages)
                if (string.Equals(info.Code, code, StringComparison.OrdinalIgnoreCase)) return info.Language;
            return null;
        }

        private static void WriteSavedLanguage(UiLanguage language)
        {
            AppSettings.Set(SettingsKey, Info(language).Code);
        }
    }
}