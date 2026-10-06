//==================================================
// HelpCatalog
// アプリのヘルプ（「？」の説明・カーソルを置くと出るヒント）の台帳。
// ID はユーザー向けノート「ヘルプ文章」の H-xx / T-xx と同じ。中身が変わるヒント（パスなど）は D-xx で、
// 確認用データから本番と同じ形の文章を作る。文言キーは画面側と同じものを使う。登録漏れはテストで検出する。
// （開発用のヘルプ一覧の窓は、リリースに向けて 2026-10-06 に削除した。）
//==================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal enum HelpKind { Mark, Tip, Dynamic }

    internal sealed class HelpEntry
    {
        public string Id;
        public HelpKind Kind;
        public string Key;              // 文言キー（中身が変わるヒントは null）
        public string Where;            // 出る場所（開発用。コード上の部品名）
        public Func<string> Sample;     // 中身が変わるヒントの確認用データ
        public bool UsesSample { get { return Sample != null; } }
    }

    internal static class HelpCatalog
    {
        public static readonly IList<HelpEntry> All = new List<HelpEntry>
        {
            Mark("H-01", "help.coordinates", "settings: coordinatesCheckBox"),
            Mark("H-02", "help.uvFormat", "settings: uvFormatComboBox"),
            Mark("H-03", "help.animationChips", "map settings: section.animationChips"),
            Mark("H-04", "help.normalize", "map settings: section.normalize"),
            Mark("H-05", "help.blackTransparency", "settings: blackTransparencyCheckBox"),
            Mark("H-06", "help.exportNumbers", "toolbar: gridNumberCheckBox"),
            Mark("H-07", "help.hints", "settings: hintsCheckBox"),
            Mark("H-08", "help.align4", "settings: align4CheckBox"),
            Tip("T-01", "tooltip.clearAll", "folder tab: removeButton"),
            Tip("T-02", "tooltip.moveUp", "folder tab: moveUpButton"),
            Tip("T-03", "tooltip.moveDown", "folder tab: moveDownButton"),
            Tip("T-04", "tooltip.playStop", "preview: playToggleButton"),
            Tip("T-05", "tooltip.mapSheet", "map: mapPalette"),
            Tip("T-06", "tooltip.mapCanvas", "map: mapCanvas"),
            Tip("T-07", "tooltip.animationIcon", "map settings: MapAnimationIcon"),
            Tip("T-08", "tooltip.removeNormalization", "map settings: normalized row ×"),
            Tip("T-09", "tooltip.keyAssign", "transitions (player): previewParametersLabel"),
            Tip("T-10", "tooltip.colorAll", "settings: colorAdjustmentButton"),
            Tip("T-11", "tooltip.tool.erase", "map: toolbar"),
            Tip("T-12", "tooltip.tool.move", "map: toolbar"),
            Tip("T-13", "tooltip.tool.rotateClockwise", "map: toolbar"),
            Tip("T-14", "tooltip.tool.rotateCounterClockwise", "map: toolbar"),
            Tip("T-15", "tooltip.tool.flip", "map: toolbar"),
            Tip("T-16", "tooltip.exportWebP", "toolbar: exportWebPButton"),
            Tip("T-17", "tooltip.tool.flipVertical", "map: toolbar"),
            Tip("T-20", "tooltip.fillEmptyCells", "toolbar: fillEmptyCellsButton"),
            // ボタン名と同じ文言を使うヒント（ここを変えるとボタン名も変わる）。
            Tip("T-18", "button.addFiles", "folder tab: addFileIconButton"),
            Tip("T-19", "button.addFolder", "folder tab: addFolderIconButton"),
            // 中身が変わるヒント（確認用データで表示する）。
            Dynamic("D-01", "folder list: folder row", () => "Folder_001"),
            Dynamic("D-02", "folder list: image row", () => @"C:\Sprites\Slime\slime_01.png"),
            Dynamic("D-03", "title menu: recent project", () => @"C:\Projects\sample_slime.smproj"),
        };

        private static HelpEntry Mark(string id, string key, string where) { return new HelpEntry { Id = id, Kind = HelpKind.Mark, Key = key, Where = where }; }
        private static HelpEntry Tip(string id, string key, string where) { return new HelpEntry { Id = id, Kind = HelpKind.Tip, Key = key, Where = where }; }
        private static HelpEntry Dynamic(string id, string where, Func<string> sample) { return new HelpEntry { Id = id, Kind = HelpKind.Dynamic, Where = where, Sample = sample }; }

        // 今の言語（language が null のとき）または指定の言語での文章。アプリの言語設定は変えない。
        public static string TextOf(HelpEntry entry, UiLanguage? language = null)
        {
            if (entry.Sample != null) return entry.Sample();
            if (language == null || language.Value == Loc.Current) return Loc.T(entry.Key);
            string text;
            return Loc.GetTable(language.Value).TryGetValue(entry.Key, out text) ? text : Loc.T(entry.Key);
        }
    }
}
