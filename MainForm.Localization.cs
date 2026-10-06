using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // 表示言語の切り替えと、言語に依存する設定ページの組み立て。
    // 文言は必ずキー(Localization/*.lang)で参照し、ここに直接書かない。
    public sealed partial class MainForm
    {
        private Control characterStatusHeader;
        private Control effectStatusHeader;
        private Control languageGroup;
        private List<Control> standardExtraItems = new List<Control>();
        private List<Control> characterStatusItems = new List<Control>();
        private List<Control> effectStatusItems = new List<Control>();
        private readonly ComboBox languageComboBox = new ComboBox();
        private readonly RoundedCheckBox axisNumbersCheckBox = new RoundedCheckBox();
        // 座標表示（カーソルを置いたセルの位置の札）と UV座標形式。アプリの設定として保存する。
        private readonly RoundedCheckBox coordinatesCheckBox = new RoundedCheckBox();
        private readonly ComboBox uvFormatComboBox = new ComboBox();
        private Control coordinatesRow, uvFormatGroup, blackTransparencyRow, hintsRow, align4Row;
        // カーソルを置いたときに出るヒントを出すか（座標の札などが見づらくなるときに切る）。アプリの設定として保存する。
        private readonly RoundedCheckBox hintsCheckBox = new RoundedCheckBox();
        private readonly RoundedCheckBox terrainCheckBox = new RoundedCheckBox();
        private readonly Terrain sampleTerrain = new Terrain(SimulationSceneSize);
        private readonly List<Action> localizedBindings = new List<Action>();
        private bool applyingLanguage;
        private readonly List<Action> comboWidthRefits = new List<Action>();

        // 文言キーをコントロールへ結び付け、言語変更時に自動で差し替える。
        private void BindText(Control control, string key)
        {
            Action apply = () => control.Text = Loc.T(key);
            localizedBindings.Add(apply);
            apply();
        }

        // メニュー項目（ToolStripItem）へ結び付ける。
        private void BindText(ToolStripItem item, string key)
        {
            Action apply = () => item.Text = Loc.T(key);
            localizedBindings.Add(apply);
            apply();
        }

        // 座標表示と UV座標形式を、すべてのシート（通常・キャラクター・エフェクト・マップチップ）へ反映する。
        // ヒントを切ると、出ているものも消す。「？」の説明（HelpMark）は別の札なので、この設定によらず出る。
        private void ApplyHintSetting()
        {
            bool enabled = hintsCheckBox.Checked;
            toolTip.Active = enabled;
            if (!enabled && mapCanvas != null) toolTip.Hide(mapCanvas);
        }

        private void ApplyCoordinateSettings()
        {
            var format = (UvCoordinateFormat)Math.Max(0, uvFormatComboBox.SelectedIndex);
            sheetCanvas.ShowCoordinates = coordinatesCheckBox.Checked; sheetCanvas.UvFormat = format;
            if (mapPalette != null) { mapPalette.ShowCoordinates = coordinatesCheckBox.Checked; mapPalette.UvFormat = format; mapPalette.Invalidate(); }
            sheetCanvas.Invalidate();
        }

        private HelpMark NewHelpMark(string helpKey)
        {
            int size = ScaleDpi(18);
            var mark = new HelpMark { Size = new Size(size, size) };
            BindAction(() => mark.HelpText = Loc.T(helpKey));
            return mark;
        }

        // 項目の左（left）か右に「？」を付けた入れ物を返す。項目の外側の余白は入れ物へ移す。
        private FlowLayoutPanel WithHelp(Control item, string helpKey, bool left)
        {
            var host = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = item.Margin, Padding = Padding.Empty, BackColor = Color.Transparent };
            item.Margin = Padding.Empty;
            HelpMark mark = NewHelpMark(helpKey);
            int top = Math.Max(0, (item.GetPreferredSize(Size.Empty).Height - mark.Height) / 2);
            mark.Margin = left ? new Padding(0, top, ScaleDpi(6), 0) : new Padding(ScaleDpi(6), top, 0, 0);
            if (left) { host.Controls.Add(mark); host.Controls.Add(item); }
            else { host.Controls.Add(item); host.Controls.Add(mark); }
            return host;
        }

        // 文言以外（ヒント・読み上げ名など）を言語に合わせて設定し、言語変更時にも再実行する。
        private void BindAction(Action apply)
        {
            localizedBindings.Add(apply);
            apply();
        }

        private Label MakeLocalizedHint(string key)
        {
            Label label = MakeSettingsHint("");
            BindText(label, key);
            return label;
        }

        // 選択欄の項目の文字だけを差し替える（選択位置は保つ。変更通知は applyingLanguage で無視される）。
        private static void RelabelComboItems(ComboBox combo, params string[] keys)
        {
            int keep = combo.SelectedIndex;
            for (int i = 0; i < keys.Length && i < combo.Items.Count; i++)
                if (keys[i] != null) combo.Items[i] = Loc.T(keys[i]);
            combo.SelectedIndex = keep;
        }

        // 再生ボタンの読み上げ名。再生中は「停止」、止まっているときは「再生」。
        private void SetPlayAccessibleName(bool playing)
        {
            playToggleButton.AccessibleName = Loc.T(playing ? "label.stop" : "label.play");
        }

        private Label MakeLocalizedLabel(string key)
        {
            Label label = MakeSettingsLabel("");
            BindText(label, key);
            return label;
        }

        private void CreateLanguageSelector()
        {
            languageComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            languageComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            languageComboBox.ItemHeight = 28;
            languageComboBox.FlatStyle = FlatStyle.Flat;
            languageComboBox.Size = new Size(160, 42);
            foreach (LanguageInfo info in Loc.All) languageComboBox.Items.Add(info.NativeName);
            languageComboBox.DrawItem += ComboBox_DrawItem;
            ApplyInputStyle(languageComboBox);
            languageComboBox.SelectedIndex = (int)Loc.Current;
            languageComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (applyingLanguage || languageComboBox.SelectedIndex < 0) return;
                Loc.SetLanguage((UiLanguage)languageComboBox.SelectedIndex);
            };
            languageGroup = CreateSettingsGroup(MakeLocalizedLabel("field.language"),
                CreateComboHost(languageComboBox, 168));

            // シートの外側に列番号・行番号を出す表示オプション（既定はオフ）。
            BindText(axisNumbersCheckBox, "check.axisNumbers");
            axisNumbersCheckBox.AutoSize = true;
            axisNumbersCheckBox.ForeColor = lightText;
            axisNumbersCheckBox.BackColor = Color.Transparent;
            axisNumbersCheckBox.Margin = new Padding(10, 12, 10, 4);
            axisNumbersCheckBox.CheckedChanged += (s, e) =>
            {
                sheetCanvas.ShowAxisNumbers = axisNumbersCheckBox.Checked;
                CommitUndoableChange();
            };

            BindText(coordinatesCheckBox, "check.coordinates");
            coordinatesCheckBox.AutoSize = true;
            coordinatesCheckBox.ForeColor = lightText;
            coordinatesCheckBox.BackColor = Color.Transparent;
            coordinatesCheckBox.Margin = new Padding(10, 4, 10, 4);
            coordinatesCheckBox.Checked = CoordinateCard.ShowSetting;
            coordinatesCheckBox.CheckedChanged += (s, e) => { CoordinateCard.ShowSetting = coordinatesCheckBox.Checked; ApplyCoordinateSettings(); };
            coordinatesRow = WithHelp(coordinatesCheckBox, "help.coordinates", false);

            uvFormatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            uvFormatComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            uvFormatComboBox.ItemHeight = 28;
            uvFormatComboBox.FlatStyle = FlatStyle.Flat;
            uvFormatComboBox.Size = new Size(200, 42);
            uvFormatComboBox.DrawItem += ComboBox_DrawItem;
            ApplyInputStyle(uvFormatComboBox);
            BindAction(() =>
            {
                int selected = uvFormatComboBox.SelectedIndex < 0 ? (int)CoordinateCard.UvFormatSetting : uvFormatComboBox.SelectedIndex;
                uvFormatComboBox.Items.Clear();
                uvFormatComboBox.Items.Add(Loc.T("uv.directx"));
                uvFormatComboBox.Items.Add(Loc.T("uv.opengl"));
                uvFormatComboBox.SelectedIndex = selected;
            });
            uvFormatComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (applyingLanguage || uvFormatComboBox.SelectedIndex < 0) return;
                CoordinateCard.UvFormatSetting = (UvCoordinateFormat)uvFormatComboBox.SelectedIndex;
                ApplyCoordinateSettings();
            };
            // 「？」は見出しの文字の右（入力欄の手前）に置く。
            Label uvLabel = MakeLocalizedLabel("field.uvFormat");
            FlowLayoutPanel uvGroup = CreateSettingsGroup(uvLabel, CreateComboHost(uvFormatComboBox, 200));
            HelpMark uvHelp = NewHelpMark("help.uvFormat");
            uvHelp.Margin = new Padding(0, Math.Max(0, (uvLabel.GetPreferredSize(Size.Empty).Height - uvHelp.Height) / 2) + uvLabel.Margin.Top, ScaleDpi(4), 0);
            uvGroup.Controls.Add(uvHelp); uvGroup.Controls.SetChildIndex(uvHelp, 1);
            uvFormatGroup = uvGroup;
            ApplyCoordinateSettings();

            BindText(hintsCheckBox, "check.hints");
            hintsCheckBox.AutoSize = true;
            hintsCheckBox.ForeColor = lightText;
            hintsCheckBox.BackColor = Color.Transparent;
            hintsCheckBox.Margin = new Padding(10, 4, 10, 4);
            hintsCheckBox.Checked = HelpTipStyle.HintsEnabled;
            hintsCheckBox.CheckedChanged += (s, e) => { HelpTipStyle.HintsEnabled = hintsCheckBox.Checked; ApplyHintSetting(); };
            hintsRow = WithHelp(hintsCheckBox, "help.hints", false);
            align4Row = WithHelp(align4CheckBox, "help.align4", false);   // 書き出しの幅・高さを4の倍数に（ツールバーが狭いため設定に置く）
            ApplyHintSetting();

            // 見本地形（スロープ・直角の崖・すり抜けられる足場）を使うか。切ると従来の平らな床（上下キーで奥行き移動）。
            BindText(terrainCheckBox, "check.terrain");
            terrainCheckBox.AutoSize = true;
            terrainCheckBox.ForeColor = lightText;
            terrainCheckBox.BackColor = Color.Transparent;
            terrainCheckBox.Margin = new Padding(10, 4, 10, 0);
            terrainCheckBox.Checked = true;
            playerState.Terrain = sampleTerrain;
            terrainCheckBox.CheckedChanged += (s, e) =>
            {
                playerState.Terrain = terrainCheckBox.Checked ? sampleTerrain : null;
                UpdateAnimationPreview();
                CommitUndoableChange();
            };
        }

        private void FillBackgroundPaletteItems()
        {
            backgroundPaletteComboBox.Items.Clear();
            backgroundPaletteComboBox.Items.Add(Loc.T("background.gray"));
            backgroundPaletteComboBox.Items.Add(Loc.T("background.blueGray"));
            backgroundPaletteComboBox.Items.Add(Loc.T("background.warmGray"));
        }

        private void OnUiLanguageChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            applyingLanguage = true;
            try
            {
                UiFont.SetLanguage(Loc.Current);
                UiFont.Restyle(this);
                // 右クリックメニューはフォームの子コントロールではないため、個別にフォントを差し替える。
                UiFont.Restyle(treeContextMenu);
                UiFont.Restyle(sheetContextMenu);
                UiFont.Restyle(projectMenu);
                UiFont.Restyle(previewModeMenu);
                UiFont.Restyle(bottomStatusStrip);
                // メモの文字の大きさ・高さはフォントで決まるので測り直す。
                MemoText.ResetFontMetrics();
                foreach (SheetMemo memo in memos) memo.InvalidateLayout();
                sheetCanvas.Invalidate();
                foreach (Action apply in localizedBindings) apply();
                int paletteIndex = backgroundPaletteComboBox.SelectedIndex;
                FillBackgroundPaletteItems();
                backgroundPaletteComboBox.SelectedIndex = Math.Max(0, paletteIndex);
                languageComboBox.SelectedIndex = (int)Loc.Current;
                foreach (Action refit in comboWidthRefits) refit();
                PerformLayout();
                AdjustLeftWorkspaceWidth();
                Invalidate(true);
            }
            finally
            {
                applyingLanguage = false;
            }
        }

        // 設定タブの並び: 基本設定(FPS・背景・言語) → 加工(黒透過・カラー) → ステータス(種類ごとの動き) 。
        // 共通項目は表示中のモードの行へ移し、種類固有の項目はその行に残す。
        private void ArrangeSettingsPage(PreviewTargetMode mode)
        {
            FlowLayoutPanel row;
            Control statusHeader = null;
            IList<Control> own;
            if (mode == PreviewTargetMode.Player)
            {
                row = characterParameterRow; statusHeader = characterStatusHeader; own = characterStatusItems;
            }
            else if (mode == PreviewTargetMode.Effect)
            {
                row = effectParameterRow; statusHeader = effectStatusHeader; own = effectStatusItems;
            }
            else
            {
                row = standardParameterRow; own = standardExtraItems;
            }

            var ordered = new List<Control>
            {
                basicSettingsHeader, unifiedFpsGroup, backgroundPaletteGroup, languageGroup, axisNumbersCheckBox, coordinatesRow, uvFormatGroup, hintsRow, align4Row, updateCheckBox,
                processingSettingsHeader, blackTransparencyRow, colorAdjustmentGroup
            };
            if (statusHeader != null) ordered.Add(statusHeader);
            ordered.AddRange(own.Where(c => !ordered.Contains(c)));

            var breakAfter = new HashSet<Control>
            {
                basicSettingsHeader, backgroundPaletteGroup, languageGroup, axisNumbersCheckBox, coordinatesRow, uvFormatGroup, hintsRow, align4Row, updateCheckBox,
                processingSettingsHeader, colorAdjustmentGroup
            };
            if (statusHeader != null) breakAfter.Add(statusHeader);
            if (mode == PreviewTargetMode.Player)
            {
                breakAfter.Add(mirrorMissingDirectionsCheckBox);
                breakAfter.Add(terrainCheckBox);
                breakAfter.Add(playerGroundOffsetGroup);
                breakAfter.Add(playerColliderVisibleCheckBox);
                breakAfter.Add(playerColliderSizeGroup);   // 補足文はサイズ欄の右へ回り込ませず、必ず次の行へ
            }

            row.SuspendLayout();
            foreach (Control control in ordered)
                if (control.Parent != row) row.Controls.Add(control);
            for (int i = 0; i < ordered.Count; i++)
            {
                row.Controls.SetChildIndex(ordered[i], i);
                row.SetFlowBreak(ordered[i], breakAfter.Contains(ordered[i]));
                ordered[i].Visible = true;
            }
            UpdateParameterSectionHeaderWidths(row);
            row.ResumeLayout(true);
        }
    }
}
