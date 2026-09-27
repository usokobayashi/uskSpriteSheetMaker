using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // プロジェクトデータ（.smproj）の保存・読み込みと、タイトルバーの「プロジェクト」操作。
    // ファイル形式そのものは ProjectFile.cs。ここは画面の状態（AppStateSnapshot）との橋渡し。
    public sealed partial class MainForm
    {
        private const int RecentProjectLimit = 5;
        private const string RecentProjectKey = "recent";
        private const string AppTitle = AppInfo.Name;

        private string projectPath;                     // 保存先。未保存の新規は null
        private string projectExtractDir;               // 開いたプロジェクトの画像の展開先
        private AppStateSnapshot projectBaseline;       // 保存・読み込み直後の状態（未保存判定の基準）
        private AppStateSnapshot defaultSnapshot;       // 起動直後の状態（「新規」で戻す先）
        private double? previewSheetRatio;              // ユーザーが動かしたシート/プレビュー境界（幅の割合）
        private RoundedButton projectChip;
        private Label projectNameLabel;
        private readonly ContextMenuStrip projectMenu = new ContextMenuStrip();

        // テストでは確認ダイアログを出さないようfalseにする。
        public bool PromptOnUnsavedChanges { get; set; } = true;

        // コマンドライン引数などで、起動直後に開くプロジェクト。
        public string InitialProjectPath { get; set; }

        //--------------
        // 初期化・タイトルバー
        //--------------
        private void InitializeProjectSupport()
        {
            defaultSnapshot = CaptureState();
            projectBaseline = defaultSnapshot;
            localizedBindings.Add(() => { FitProjectChip(); UpdateProjectTitle(); });
            DeleteStaleProjectFolders();
            Shown += (s, e) =>
            {
                string initial = InitialProjectPath;
                if (!string.IsNullOrEmpty(initial) && File.Exists(initial)) TryBeginInvoke(() => OpenProjectFile(initial));
            };
        }

        // タイトルバーへ「プロジェクト」チップとプロジェクト名を足す。位置は centerContent 側で揃える。
        private void BuildProjectTitleControls(Panel bar, Label title)
        {
            projectChip = new RoundedButton
            {
                CornerRadius = RadiusMd,
                BackColor = panelElevated,
                ForeColor = lightText,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
                Height = 32,
                TabStop = false
            };
            projectChip.FlatAppearance.BorderSize = 0;
            projectChip.FlatAppearance.MouseOverBackColor = treeSelectionColor;
            BindText(projectChip, "project.chip");
            projectChip.Click += (s, e) => ShowProjectMenu();

            projectNameLabel = new Label
            {
                AutoSize = true,
                ForeColor = mutedText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point),
                Visible = false
            };
            bar.Controls.Add(projectChip);
            bar.Controls.Add(projectNameLabel);
            FitProjectChip();
            projectChip.SizeChanged += (s, e) => PlaceProjectTitleControls(bar, title);
            projectNameLabel.SizeChanged += (s, e) => PlaceProjectTitleControls(bar, title);
            title.SizeChanged += (s, e) => PlaceProjectTitleControls(bar, title);
        }

        private void FitProjectChip()
        {
            if (projectChip == null) return;
            projectChip.Width = TextRenderer.MeasureText(projectChip.Text, projectChip.Font).Width + 36;
        }

        private void PlaceProjectTitleControls(Panel bar, Label title)
        {
            if (projectChip == null) return;
            projectChip.Left = title.Right + 28;
            projectChip.Top = (bar.Height - projectChip.Height) / 2;
            projectNameLabel.Left = projectChip.Right + 14;
            projectNameLabel.Top = (bar.Height - projectNameLabel.Height) / 2;
        }

        private string ProjectDisplayName()
        {
            return projectPath == null ? Loc.T("project.untitled") : Path.GetFileNameWithoutExtension(projectPath);
        }

        private bool IsProjectDirty()
        {
            return projectBaseline != null && !StatesEqualIgnoringSelection(CaptureState(), projectBaseline);
        }

        // 保存済みのプロジェクト、または変更のある新規のときだけ名前と未保存の印を出す。
        private void UpdateProjectTitle()
        {
            if (projectNameLabel == null || projectBaseline == null) return;
            bool dirty = IsProjectDirty();
            bool show = projectPath != null || dirty;
            string name = ProjectDisplayName();
            projectNameLabel.Text = name + (dirty ? "  ●" : "");
            projectNameLabel.Visible = show;
            Text = show ? name + (dirty ? "*" : "") + " - " + AppTitle : AppTitle;
        }

        //--------------
        // メニュー
        //--------------
        private void ShowProjectMenu()
        {
            projectMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            projectMenu.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            projectMenu.BackColor = darkPanel;
            projectMenu.ForeColor = lightText;
            projectMenu.ShowImageMargin = false;
            projectMenu.Items.Clear();
            projectMenu.Items.Add(CreateProjectMenuItem("menu.projectNew", null, NewProject));
            projectMenu.Items.Add(CreateProjectMenuItem("menu.projectOpen", "Ctrl+O", OpenProjectWithDialog));
            projectMenu.Items.Add(new ToolStripSeparator());
            projectMenu.Items.Add(CreateProjectMenuItem("menu.projectSave", "Ctrl+S", () => SaveProject()));
            projectMenu.Items.Add(CreateProjectMenuItem("menu.projectSaveAs", "Ctrl+Shift+S", () => SaveProjectAs()));
            projectMenu.Items.Add(new ToolStripSeparator());

            var recent = new ToolStripMenuItem(Loc.T("menu.projectRecent")) { ForeColor = lightText };
            recent.DropDown.Renderer = projectMenu.Renderer;
            recent.DropDown.BackColor = darkPanel;
            recent.DropDown.Font = projectMenu.Font;
            IList<string> recentPaths = GetRecentProjects();
            if (recentPaths.Count == 0)
            {
                recent.DropDownItems.Add(new ToolStripMenuItem(Loc.T("menu.projectRecentEmpty")) { Enabled = false, ForeColor = mutedText });
            }
            foreach (string path in recentPaths)
            {
                string captured = path;
                var item = new ToolStripMenuItem(Path.GetFileName(path)) { ForeColor = lightText, ToolTipText = path };
                item.Click += (s, e) => OpenProjectFromUser(captured);
                recent.DropDownItems.Add(item);
            }
            projectMenu.Items.Add(recent);
            projectMenu.Show(projectChip, new Point(0, projectChip.Height + 4));
        }

        private ToolStripMenuItem CreateProjectMenuItem(string textKey, string shortcut, Action action)
        {
            var item = new ToolStripMenuItem(Loc.T(textKey)) { ForeColor = lightText, ShortcutKeyDisplayString = shortcut };
            item.Click += (s, e) => action();
            return item;
        }

        //--------------
        // 新規・保存・開く
        //--------------
        private void NewProject()
        {
            if (!ConfirmProjectDiscard()) return;
            ResetToNewProject();
        }

        internal void ResetToNewProject()
        {
            string oldDirectory = projectExtractDir;
            RestoreState(defaultSnapshot);
            previewSheetRatio = null;
            ApplyPreviewSplitOnly();
            ResetUndoHistory();
            projectPath = null;
            projectExtractDir = null;
            projectBaseline = CaptureState();
            DeleteQuietly(oldDirectory);
            UpdateProjectTitle();
        }

        private bool SaveProject()
        {
            return projectPath == null ? SaveProjectAs() : SaveProjectTo(projectPath);
        }

        private bool SaveProjectAs()
        {
            PauseInteractivePreview();
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = Loc.T("dialog.saveProjectTitle");
                dialog.Filter = Loc.T("dialog.projectFilter");
                dialog.DefaultExt = ProjectFile.Extension.TrimStart('.');
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = (projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : Loc.T("project.untitled")) + ProjectFile.Extension;
                if (projectPath != null) dialog.InitialDirectory = Path.GetDirectoryName(projectPath);
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                return SaveProjectTo(dialog.FileName);
            }
        }

        internal bool SaveProjectTo(string path)
        {
            ProjectDocument document = CaptureProjectDocument();
            ProjectSaveResult result;
            try
            {
                result = ProjectFile.Save(path, document);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowProjectNotice(Loc.T("dialog.projectTitle"), Loc.T("message.projectSaveFailed", ex.Message));
                return false;
            }
            projectPath = path;
            projectBaseline = CaptureState();
            AddRecentProject(path);
            UpdateProjectTitle();
            statusLabel.Text = Loc.T("message.projectSaved", Path.GetFileName(path));
            if (result.MissingImages.Count > 0)
                ShowProjectNotice(Loc.T("dialog.projectTitle"),
                    Loc.T("message.projectSavedMissing", result.MissingImages.Count) + "\n" + SummarizeNames(result.MissingImages.Select(Path.GetFileName)));
            return true;
        }

        private void OpenProjectWithDialog()
        {
            if (!ConfirmProjectDiscard()) return;
            PauseInteractivePreview();
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = Loc.T("dialog.openProjectTitle");
                dialog.Filter = Loc.T("dialog.projectFilter");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) OpenProjectFile(dialog.FileName);
            }
        }

        // ドラッグ＆ドロップ・最近使ったファイルから。未保存の変更があれば確認する。
        private void OpenProjectFromUser(string path)
        {
            if (!ConfirmProjectDiscard()) return;
            OpenProjectFile(path);
        }

        // 読み込みが成功してから画面へ反映する。失敗しても現在の作業は変わらない。
        internal bool OpenProjectFile(string path)
        {
            string extractDirectory = Path.Combine(ProjectsRoot(), Guid.NewGuid().ToString("N"));
            ProjectLoadResult loaded;
            try
            {
                loaded = ProjectFile.Load(path, extractDirectory);
            }
            catch (ProjectFormatException)
            {
                DeleteQuietly(extractDirectory);
                ShowProjectNotice(Loc.T("dialog.projectTitle"), Loc.T("message.projectOpenFailed"));
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DeleteQuietly(extractDirectory);
                ShowProjectNotice(Loc.T("dialog.projectTitle"), Loc.T("message.projectOpenFailedIo", ex.Message));
                return false;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException ||
                ex is OutOfMemoryException || ex is System.Security.SecurityException)
            {
                // 壊れた・悪意のあるファイル（想定外の名前や値）。作業は変えずに読み込みだけ断る。
                DeleteQuietly(extractDirectory);
                ShowProjectNotice(Loc.T("dialog.projectTitle"), Loc.T("message.projectOpenFailed"));
                return false;
            }

            string oldDirectory = projectExtractDir;
            RestoreState(SnapshotFromDocument(loaded.Document));
            double ratio = loaded.Document.SheetPaneRatio;
            previewSheetRatio = ratio > 0.1 && ratio < 0.9 ? ratio : (double?)null;
            ApplyPreviewSplitOnly();
            ResetUndoHistory();
            projectPath = path;
            projectExtractDir = extractDirectory;
            projectBaseline = CaptureState();
            DeleteQuietly(oldDirectory);
            AddRecentProject(path);
            UpdateProjectTitle();
            statusLabel.Text = Loc.T("message.projectOpened", Path.GetFileName(path));

            var notes = new List<string>();
            if (loaded.NewerFormat) notes.Add(Loc.T("message.projectNewerFormat"));
            if (loaded.MissingImages.Count > 0)
                notes.Add(Loc.T("message.projectOpenMissing", loaded.MissingImages.Count) + "\n" + SummarizeNames(loaded.MissingImages));
            if (notes.Count > 0) ShowProjectNotice(Loc.T("dialog.projectTitle"), string.Join("\n\n", notes));
            return true;
        }

        private void ResetUndoHistory()
        {
            undoManager.Initialize();
            undoMenuItem.Enabled = false;
            redoMenuItem.Enabled = false;
        }

        // 未保存の変更があれば 保存 / 保存しない / キャンセル を確認し、続行してよいかを返す。
        private bool ConfirmProjectDiscard()
        {
            if (!PromptOnUnsavedChanges || !IsProjectDirty()) return true;
            DialogResult answer = ShowProjectChoice(Loc.T("dialog.unsavedTitle"),
                Loc.T("dialog.unsavedMessage", ProjectDisplayName()),
                new[]
                {
                    new ProjectChoice { Result = DialogResult.Cancel, Text = Loc.T("button.cancel") },
                    new ProjectChoice { Result = DialogResult.No, Text = Loc.T("button.discard"), Danger = true },
                    new ProjectChoice { Result = DialogResult.Yes, Text = Loc.T("button.save"), Accent = true }
                });
            if (answer == DialogResult.Yes) return SaveProject();
            return answer == DialogResult.No;
        }

        //--------------
        // 状態との変換
        //--------------
        private ProjectDocument CaptureProjectDocument()
        {
            AppStateSnapshot state = CaptureState();
            var document = new ProjectDocument
            {
                AppVersion = typeof(MainForm).Assembly.GetName().Version.ToString(),
                NextFolderNumber = state.NextFolderNumber,
                Columns = state.Columns,
                ScaleIndex = state.ScaleIndex,
                Fps = state.Fps,
                StartCell = state.StartCell,
                EndCell = state.EndCell,
                ExportNumbers = state.ExportNumbers,
                ShowAxisNumbers = state.ShowAxisNumbers,
                DisableTerrain = !state.UseTerrain,
                PreviewMode = state.PreviewMode.ToString(),
                PlayerMoveSpeed = state.PlayerMoveSpeed,
                PlayerJump = state.PlayerJumpDistance,
                PlayerGravity = state.PlayerGravity,
                PlayerGroundOffset = state.PlayerGroundOffset,
                MirrorMissingDirections = state.MirrorMissingDirections,
                BackgroundPalette = state.BackgroundPalette,
                BlackTransparency = state.BlackTransparency,
                ColorBlendMode = state.ColorBlendMode.ToString(),
                AdjustmentColorArgb = state.AdjustmentColor.ToArgb(),
                AdjustmentStrength = state.AdjustmentStrength,
                EffectDirectionX = state.EffectDirectionX,
                EffectDirectionY = state.EffectDirectionY,
                EffectSpeed = state.EffectSpeed,
                SimulationRangesInitialized = state.SimulationRangesInitialized,
                EffectClip = ToProjectClip("", state.EffectClip),
                SheetPaneRatio = previewSheetRatio ?? 0
            };
            foreach (KeyValuePair<PlayerAnimationState, AnimationClipSettings> pair in state.PlayerClips)
                document.PlayerClips.Add(ToProjectClip(pair.Key.ToString(), pair.Value));
            foreach (KeyValuePair<PreviewAction, Keys> pair in state.KeyBindings)
                document.Keys.Add(new ProjectKey { Action = pair.Key.ToString(), Key = pair.Value.ToString() });
            foreach (MemoState memo in state.Memos)
                document.Memos.Add(new ProjectMemo { X = memo.X, Y = memo.Y, Width = memo.Width, Text = memo.Text });
            foreach (FolderSnapshot folder in state.Folders)
            {
                var projectFolder = new ProjectFolder { Name = folder.Name };
                foreach (string path in folder.Paths) projectFolder.Images.Add(new ProjectImage { Path = path });
                document.Folders.Add(projectFolder);
            }
            return document;
        }

        // 欠けている項目（古い/新しいファイル）は、起動直後の状態で補う。
        private AppStateSnapshot SnapshotFromDocument(ProjectDocument document)
        {
            var state = new AppStateSnapshot
            {
                NextFolderNumber = Math.Max(1, document.NextFolderNumber),
                Columns = document.Columns,
                ScaleIndex = document.ScaleIndex,
                Fps = document.Fps,
                StartCell = document.StartCell,
                EndCell = document.EndCell,
                ExportNumbers = document.ExportNumbers,
                ShowAxisNumbers = document.ShowAxisNumbers,
                UseTerrain = !document.DisableTerrain,
                PreviewMode = ParseEnum(document.PreviewMode, defaultSnapshot.PreviewMode),
                PlayerMoveSpeed = document.PlayerMoveSpeed,
                PlayerJumpDistance = document.PlayerJump,
                PlayerGravity = document.PlayerGravity,
                PlayerGroundOffset = document.PlayerGroundOffset,
                MirrorMissingDirections = document.MirrorMissingDirections,
                BackgroundPalette = document.BackgroundPalette,
                BlackTransparency = document.BlackTransparency,
                ColorBlendMode = ParseEnum(document.ColorBlendMode, defaultSnapshot.ColorBlendMode),
                AdjustmentColor = Color.FromArgb(document.AdjustmentColorArgb),
                AdjustmentStrength = document.AdjustmentStrength,
                EffectDirectionX = document.EffectDirectionX,
                EffectDirectionY = document.EffectDirectionY,
                EffectSpeed = document.EffectSpeed,
                SimulationRangesInitialized = document.SimulationRangesInitialized,
                EffectClip = FromProjectClip(document.EffectClip)
            };
            foreach (KeyValuePair<PlayerAnimationState, AnimationClipSettings> pair in defaultSnapshot.PlayerClips)
                state.PlayerClips[pair.Key] = pair.Value.Clone();
            foreach (ProjectClip clip in document.PlayerClips)
            {
                PlayerAnimationState clipState;
                if (Enum.TryParse(clip.State, out clipState)) state.PlayerClips[clipState] = FromProjectClip(clip);
            }
            foreach (KeyValuePair<PreviewAction, Keys> pair in defaultSnapshot.KeyBindings)
                state.KeyBindings[pair.Key] = pair.Value;
            foreach (ProjectKey key in document.Keys)
            {
                PreviewAction action;
                Keys parsedKey;
                if (Enum.TryParse(key.Action, out action) && Enum.TryParse(key.Key, out parsedKey))
                    state.KeyBindings[action] = parsedKey;
            }
            foreach (ProjectMemo memo in document.Memos)
                state.Memos.Add(new MemoState { X = memo.X, Y = memo.Y, Width = memo.Width, Text = memo.Text });
            foreach (ProjectFolder folder in document.Folders)
            {
                var folderState = new FolderSnapshot { Name = folder.Name };
                foreach (ProjectImage image in folder.Images) folderState.Paths.Add(image.Path);
                state.Folders.Add(folderState);
            }
            return state;
        }

        private static ProjectClip ToProjectClip(string state, AnimationClipSettings clip)
        {
            return new ProjectClip { State = state, Enabled = clip.Enabled, StartCell = clip.StartCell, EndCell = clip.EndCell, Fps = clip.Fps };
        }

        private static AnimationClipSettings FromProjectClip(ProjectClip clip)
        {
            return new AnimationClipSettings
            {
                Enabled = clip.Enabled,
                StartCell = Math.Max(1, clip.StartCell),
                EndCell = Math.Max(1, clip.EndCell),
                Fps = Math.Max(1, clip.Fps)
            };
        }

        private static T ParseEnum<T>(string text, T fallback) where T : struct
        {
            T value;
            return Enum.TryParse(text, out value) && Enum.IsDefined(typeof(T), value) ? value : fallback;
        }

        //--------------
        // 最近使ったファイル・展開先
        //--------------
        private static IList<string> GetRecentProjects()
        {
            return AppSettings.GetList(RecentProjectKey, RecentProjectLimit);
        }

        private static void AddRecentProject(string path)
        {
            var list = GetRecentProjects().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, path);
            AppSettings.SetList(RecentProjectKey, list.Take(RecentProjectLimit).ToList(), RecentProjectLimit);
        }

        private static string ProjectsRoot()
        {
            Loc.Initialize();
            return Path.Combine(Path.GetDirectoryName(Loc.SettingsPath), "projects");
        }

        // 異常終了で残った古い展開フォルダを消す（別のウィンドウが使っていても壊さないよう2日以上前のものだけ）。
        private static void DeleteStaleProjectFolders()
        {
            try
            {
                string root = ProjectsRoot();
                if (!Directory.Exists(root)) return;
                foreach (string directory in Directory.GetDirectories(root))
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-2)) DeleteQuietly(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void DeleteQuietly(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string SummarizeNames(IEnumerable<string> names)
        {
            List<string> list = names.ToList();
            string shown = string.Join("\n", list.Take(5));
            return list.Count > 5 ? shown + "\n" + Loc.T("message.andMore", list.Count - 5) : shown;
        }

        //--------------
        // ダイアログ
        //--------------
        private sealed class ProjectChoice
        {
            public DialogResult Result;
            public string Text;
            public bool Accent;
            public bool Danger;
        }

        private void ShowProjectNotice(string title, string message)
        {
            ShowProjectChoice(title, message, new[] { new ProjectChoice { Result = DialogResult.OK, Text = "OK", Accent = true } });
        }

        // 本文の長さに合わせて高さを決め、ボタンは右詰め（配列の最後が右端・既定）で並べる。
        private DialogResult ShowProjectChoice(string title, string message, ProjectChoice[] choices)
        {
            PauseInteractivePreview();
            using (Form dialog = CreateDarkDialog(title, message, 210))
            {
                Label messageLabel = dialog.Controls.OfType<Label>().Skip(1).First();
                int textHeight = TextRenderer.MeasureText(message, messageLabel.Font, new Size(messageLabel.Width, 4000),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                messageLabel.Height = Math.Max(52, textHeight + 6);
                int buttonTop = messageLabel.Top + messageLabel.Height + 24;
                dialog.ClientSize = new Size(dialog.ClientSize.Width, buttonTop + 42 + 28);

                var buttons = new List<Button>();
                foreach (ProjectChoice choice in choices)
                {
                    var button = new Button { Text = choice.Text };
                    button.Size = new Size(Math.Max(110, TextRenderer.MeasureText(choice.Text, Font).Width + 36), 42);
                    ApplyButtonStyle(button);
                    if (choice.Accent) button.BackColor = accentColor;
                    if (choice.Danger) button.BackColor = dangerBackColor;
                    button.DialogResult = choice.Result;
                    dialog.Controls.Add(button);
                    if (choice.Accent) dialog.AcceptButton = button;
                    if (choice.Result == DialogResult.Cancel || choices.Length == 1) dialog.CancelButton = button;
                    buttons.Add(button);
                }

                // 左右の余白（本文と同じ28px）を等しくするため、複数のボタンは全幅に均等に並べる。
                // 訳が長くて収まらないときはダイアログの幅を広げる。1つだけのときは右寄せ。
                const int sideMargin = 28, gap = 12;
                int neededWidth = buttons.Sum(b => b.Width) + gap * (buttons.Count - 1);
                if (buttons.Count > 1 && neededWidth + sideMargin * 2 > dialog.ClientSize.Width)
                {
                    dialog.ClientSize = new Size(neededWidth + sideMargin * 2, dialog.ClientSize.Height);
                    messageLabel.Width = dialog.ClientSize.Width - sideMargin * 2;
                }
                int rowWidth = dialog.ClientSize.Width - sideMargin * 2;
                if (buttons.Count > 1)
                {
                    int spare = rowWidth - neededWidth;
                    int share = spare / buttons.Count;
                    for (int i = 0; i < buttons.Count; i++) buttons[i].Width += share + (i < spare % buttons.Count ? 1 : 0);
                }
                int x = buttons.Count > 1 ? sideMargin : dialog.ClientSize.Width - sideMargin - buttons[0].Width;
                foreach (Button button in buttons)
                {
                    button.Location = new Point(x, buttonTop);
                    x += button.Width + gap;
                }
                return dialog.ShowDialog(this);
            }
        }
    }
}
