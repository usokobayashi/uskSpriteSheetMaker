using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpriteSheetMaker
{
    // アプリ全体の設定（`key=value` の行）を settings.ini に保存する。
    // 書き込み時は自分が扱わない行を残す。プロジェクトの内容（取り消しの対象）はここに置かない。
    public static class AppSettings
    {
        private const long MaxSettingsBytes = 1024 * 1024;

        public static string Get(string key)
        {
            string value;
            return ReadAll().TryGetValue(key, out value) ? value : null;
        }

        public static void Set(string key, string value)
        {
            SetMany(new Dictionary<string, string> { { key, value } });
        }

        // prefix1, prefix2 ... の連番キーで一覧を読み書きする（最近使ったファイルなど）。
        public static IList<string> GetList(string prefix, int maximum)
        {
            var list = new List<string>();
            Dictionary<string, string> all = ReadAll();
            for (int i = 1; i <= maximum; i++)
            {
                string value;
                if (all.TryGetValue(prefix + i, out value) && value.Length > 0) list.Add(value);
            }
            return list;
        }

        public static void SetList(string prefix, IList<string> values, int maximum)
        {
            var updates = new Dictionary<string, string>();
            for (int i = 1; i <= maximum; i++)
                updates[prefix + i] = i <= values.Count ? values[i - 1] : null;
            SetMany(updates);
        }

        // null の値はキーを削除する。
        private static void SetMany(IDictionary<string, string> updates)
        {
            try
            {
                Loc.Initialize();
                string path = Loc.SettingsPath;
                var lines = new List<string>();
                var written = new HashSet<string>();
                // 読み込みでは大きすぎるファイルを無視するので、そのまま書き足しても設定が効かない。
                // 異常な中身は別名で残し、今回の設定だけで作り直す。
                if (File.Exists(path) && new FileInfo(path).Length > MaxSettingsBytes)
                {
                    File.Copy(path, path + ".toolarge", true);
                    File.Delete(path);
                }
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int split = line.IndexOf('=');
                        string key = split > 0 ? line.Substring(0, split).Trim() : null;
                        if (key != null && updates.ContainsKey(key))
                        {
                            if (updates[key] != null && written.Add(key)) lines.Add(key + "=" + updates[key]);
                            continue;
                        }
                        lines.Add(line);
                    }
                }
                foreach (KeyValuePair<string, string> pair in updates)
                    if (pair.Value != null && written.Add(pair.Key)) lines.Add(pair.Key + "=" + pair.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 途中で止まっても設定が壊れないよう、別名へ書いてから置き換える。
                string temporary = path + ".tmp";
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Dictionary<string, string> ReadAll()
        {
            var all = new Dictionary<string, string>();
            try
            {
                Loc.Initialize();
                if (!File.Exists(Loc.SettingsPath)) return all;
                if (new FileInfo(Loc.SettingsPath).Length > MaxSettingsBytes) return all;   // 異常に大きいファイルは読まない
                foreach (string line in File.ReadAllLines(Loc.SettingsPath, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    all[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return all;
        }
    }
}
