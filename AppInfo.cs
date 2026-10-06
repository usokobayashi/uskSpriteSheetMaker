using System;
using System.IO;

namespace SpriteSheetMaker
{
    // アプリ名と、設定・記録などを置くフォルダを一か所で決める。
    internal static class AppInfo
    {
        public const string Name = "uskSpriteSheetMaker";
        private const string LegacyName = "SpriteMakerUntukAnjing";
        // 表示・配布物のバージョン番号（ここだけを変えれば、タイトルバー・exeのファイル情報・
        // プロジェクトファイルのappVersionすべてに反映される）。更新のたびに上げる。
        public const string Version = "1.1.0";

        // %LOCALAPPDATA%\uskSpriteSheetMaker。以前の名前のフォルダが残っていれば、初回にそのまま引き継ぐ。
        public static string DataDirectory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string current = Path.Combine(root, Name);
                string legacy = Path.Combine(root, LegacyName);
                try
                {
                    if (!Directory.Exists(current) && Directory.Exists(legacy)) Directory.Move(legacy, current);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return current;
            }
        }
    }
}
