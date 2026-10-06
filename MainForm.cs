using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CancellationToken = System.Threading.CancellationToken;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using SemaphoreSlim = System.Threading.SemaphoreSlim;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    public sealed partial class MainForm : Form
    {
        private readonly Color darkBack = Color.FromArgb(20, 27, 33);
        private readonly Color darkPanel = Color.FromArgb(24, 32, 38);
        private readonly Color titleBack = Color.FromArgb(12, 17, 22);
        private readonly Color workspaceBack = Color.FromArgb(17, 23, 28);
        private readonly Color inputBack = Color.FromArgb(17, 23, 28);
        private readonly Color inputBorder = Color.FromArgb(75, 86, 95);
        private readonly Color dividerColor = Color.FromArgb(63, 73, 81);
        private readonly Color accentColor = Color.FromArgb(84, 73, 255);
        private readonly Color lightText = Color.FromArgb(243, 245, 247);
        private readonly Color mutedText = Color.FromArgb(190, 198, 205);
        private readonly Color disabledText = Color.FromArgb(104, 114, 122);   // 触れない項目の文字・枠
        // 「操作パネルは一段明るい色」用。チップ・エクスポートカードなど
        // darkPanelよりわずかに明るい背景に統一して使う。
        private readonly Color panelElevated = Color.FromArgb(31, 40, 47);
        // 危険操作（削除系）で使う色を2値に統一。以前は文字色・確認ダイアログの
        // ボタン背景・閉じるボタンのホバー背景で微妙に異なる赤が個別に
        // ハードコードされていた。
        private readonly Color dangerColor = Color.FromArgb(255, 122, 130);
        private readonly Color dangerBackColor = Color.FromArgb(176, 48, 58);
        // accentColorのホバー/押下色。以前はplayToggleButton・StylePreviewTab・
        // addFilesDropButtonの3箇所に同じ値が個別にハードコードされていた。
        private readonly Color accentHoverColor = Color.FromArgb(101, 90, 255);
        private readonly Color accentPressedColor = Color.FromArgb(70, 60, 220);
        // ツリーの選択行強調色。accentColorとは別系統の4色目だったものを整理。
        private readonly Color treeSelectionColor = Color.FromArgb(62, 55, 145);
        // 角丸半径・アイコン線幅の共通値。以前は呼び出し元ごとに3/4/6/8pxが
        // バラバラに指定されていた。
        private const int RadiusSm = 4;
        private const int RadiusMd = 6;
        private const int RadiusLg = 8;
        private const float IconStroke = 1.5f;
        private readonly List<ImageFolder> folders = new List<ImageFolder>();
        private readonly List<AnimationFrameCache> animationFrames = new List<AnimationFrameCache>();
        private readonly HashSet<ImageFolder> selectedFolders = new HashSet<ImageFolder>();
        private readonly HashSet<ImageItem> selectedImages = new HashSet<ImageItem>();
        private readonly UndoManager<AppStateSnapshot> undoManager;
        private int nextFolderNumber = 1;
        private ImageFolder folderSelectionAnchor;
        private ImageItem imageSelectionAnchor;
        private bool restoringState;
        // 構築時にResize/HandleCreated駆動で自前配置するパネル群
        // （emptyDropZone、workspaceTabBar、fileActions、animInfoLabelなど）の
        // 再配置クロージャ。DPI拡大率が96以外の環境では、AutoScaleMode.Dpiによる
        // コントロールのスケーリングと、これらクロージャの初回実行タイミングが
        // ズレることがあり、その場合だけ数ピクセルの表示崩れが残ることがある。
        // Shownイベントで全て再実行し、最終的なレイアウトを確定させる。
        private readonly List<Action> lateLayoutActions = new List<Action>();
        private bool commitScheduled;

        private readonly FolderTreeView treeView = new FolderTreeView();
        private readonly ContextMenuStrip treeContextMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem undoMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem redoMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem addFolderMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem renameFolderMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem sortFolderMenuItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem deleteTreeMenuItem = new ToolStripMenuItem();
        // ヒントはヘルプの「？」と同じ見た目・同じ出方（0.3秒）にそろえる。
        private readonly ToolTip toolTip = HelpTipStyle.Create();
        private readonly RoundedPanel emptyDropZone = new RoundedPanel();
        private readonly Panel fileCountRow = new Panel();
        private readonly Label fileCountLabel = new Label();
        private readonly Button addFileIconButton = new Button();
        private readonly Button addFolderIconButton = new Button();
        private readonly NumericUpDown columnsBox = new NumericUpDown();
        private int appliedColumns = 8; // セル番号の付け替え用。直前の横セル数
        private readonly ComboBox scaleComboBox = new ComboBox();
        private readonly NumericUpDown fpsBox = new NumericUpDown();
        private readonly NumericUpDown startCellBox = new NumericUpDown();
        private readonly NumericUpDown endCellBox = new NumericUpDown();
        private readonly ComboBox stateConfigComboBox = new ComboBox();
        private readonly ComboBox previewModeEnumComboBox = new ComboBox();
        private readonly RoundedCheckBox stateEnabledCheckBox = new RoundedCheckBox();
        private readonly NumericUpDown stateStartBox = new NumericUpDown();
        private readonly NumericUpDown stateEndBox = new NumericUpDown();
        private readonly NumericUpDown effectDirectionXBox = new NumericUpDown();
        private readonly NumericUpDown effectDirectionYBox = new NumericUpDown();
        private readonly NumericUpDown effectSpeedBox = new NumericUpDown();
        private readonly NumericUpDown effectStartBox = new NumericUpDown();
        private readonly NumericUpDown effectEndBox = new NumericUpDown();
        private readonly NumericUpDown playerMoveSpeedBox = new NumericUpDown();
        private readonly NumericUpDown playerJumpDistanceBox = new NumericUpDown();
        private readonly NumericUpDown playerGravityBox = new NumericUpDown();
        private readonly NumericUpDown playerGroundOffsetBox = new NumericUpDown();
        private readonly NumericUpDown playerColliderWidthBox = new NumericUpDown();   // 画像ピクセル。0 は自動
        private readonly NumericUpDown playerColliderHeightBox = new NumericUpDown();
        private readonly RoundedCheckBox playerColliderVisibleCheckBox = new RoundedCheckBox();
        private Control playerGroundOffsetGroup;
        private Control playerColliderWidthHost, playerColliderHeightHost;
        private Label playerColliderWidthLabel, playerColliderHeightLabel;
        private FlowLayoutPanel playerColliderSizeGroup;
        private readonly RoundedCheckBox mirrorMissingDirectionsCheckBox = new RoundedCheckBox();
        private readonly ComboBox backgroundPaletteComboBox = new ComboBox();
        private readonly RoundedCheckBox blackTransparencyCheckBox = new RoundedCheckBox();
        private readonly Button colorAdjustmentButton = new Button();

        private readonly RoundedButton exportPngButton = new RoundedButton();
        private readonly RoundedButton exportTgaButton = new RoundedButton();
        private readonly RoundedButton exportGifButton = new RoundedButton();
        private readonly RoundedButton exportWebPButton = new RoundedButton();
        private readonly Button clearButton = new Button();
        private readonly Button removeButton = new Button();
        private readonly Button moveUpButton = new Button();
        private readonly Button moveDownButton = new Button();
        private readonly Button refreshPreviewButton = new Button();
        private readonly RoundedCheckBox align4CheckBox = new RoundedCheckBox();   // 書き出しの幅・高さを4の倍数に
        private readonly Button fillEmptyCellsButton = new Button();               // 横セル数を、空きセルが出ない数に変える
        private readonly Button playToggleButton = new Button();
        private readonly Button keySettingsButton = new Button();
        private readonly Button standardPreviewTabButton = new Button();
        private readonly Button playerPreviewTabButton = new Button();
        private readonly Button effectPreviewTabButton = new Button();
        // フォルダ・状態遷移・設定のタブ（並びは PreviewWorkspacePage と同じ）。
        private readonly SegmentedTabBar workspaceTabBar = new SegmentedTabBar(3);
        private readonly RoundedCheckBox gridNumberCheckBox = new RoundedCheckBox();

        private readonly Label exportLabel = new Label();
        private readonly Panel exportButtonGroup = new Panel();
        private RoundedFlowPanel exportCard;
        private readonly RoundedLabel animInfoLabel = new RoundedLabel();
        private readonly ToolStripStatusLabel zoomHintLabel = new ToolStripStatusLabel();
        private readonly RoundedLabel sheetCellsChip = new RoundedLabel();
        private readonly RoundedLabel sheetSizeChip = new RoundedLabel();
        private FlowLayoutPanel sheetHeaderLeft;
        private Label sheetTitleLabel;
        private bool sheetSizeChipWanted;
        private readonly RoundedLabel sheetZoomChip = new RoundedLabel();
        private readonly RoundedLabel animZoomChip = new RoundedLabel();
        private readonly Button sheetFitButton = new Button();
        // プレビュー見出しの「全体表示」（マップチップのときだけ出す）。
        private readonly Button previewFitButton = new Button();
        private readonly StatusStrip bottomStatusStrip = new StatusStrip();
        private readonly Label previewTitleLabel = new Label();
        private readonly Label previewModeSelectorLabel = new Label();
        private readonly Label frameAssignmentLabel = new Label();
        private readonly Label previewParametersLabel = new Label();
        private readonly Label loadingIndicatorLabel = new Label();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();

        private readonly PreviewCanvas sheetCanvas = new PreviewCanvas { CornerRadius = RadiusMd };
        private readonly PreviewCanvas animCanvas = new PreviewCanvas { CornerRadius = RadiusMd };

        private SplitContainer mainSplit;
        private SplitContainer previewSplit;
        private Panel titleBar;
        private Panel toolbarPanel;
        private FlowLayoutPanel primaryToolbarRow;

        private readonly Timer animationTimer = new Timer();
        private readonly Timer blackTransparencyCooldownTimer = new Timer { Interval = 700 };
        private int animationIndex = 0;
        private FolderNode internalDragNode;
        private readonly Dictionary<PlayerAnimationState, AnimationClipSettings> playerClips =
            new Dictionary<PlayerAnimationState, AnimationClipSettings>();
        private AnimationClipSettings effectClip = new AnimationClipSettings { Enabled = true, StartCell = 1, EndCell = 1, Fps = 12 };
        private readonly PreviewInputController previewInput = new PreviewInputController();
        private readonly PlayerStateController playerState = new PlayerStateController();
        private readonly EffectMotionController effectMotion = new EffectMotionController();
        private SpriteAnimationController playerAnimator;
        private PreviewTargetMode previewTargetMode = PreviewTargetMode.Standard;
        private readonly ContextMenuStrip previewModeMenu = new ContextMenuStrip();
        private FlowLayoutPanel characterSettingsRow;
        private FlowLayoutPanel effectSettingsRow;
        private FlowLayoutPanel standardSettingsRow;
        private FlowLayoutPanel standardParameterRow;
        private FlowLayoutPanel characterParameterRow;
        private FlowLayoutPanel effectParameterRow;
        private Control unifiedFpsGroup;
        private Control backgroundPaletteGroup;
        private Control colorAdjustmentGroup;
        private Panel leftWorkspaceHost;
        private bool pausedForMinimize;
        private Control basicSettingsHeader;
        private Control processingSettingsHeader;
        private Panel simulationSettingsPanel;
        private Panel previewModeEnumHost;
        private Panel stateTransitionPage;
        private Panel folderWorkspacePage;
        private Panel playerTransitionList;
        // 状態一覧のグループ（ジャンプ・攻撃）の開閉。言語切替で一覧を作り直しても保つ。
        private readonly HashSet<string> collapsedTransitionGroups = new HashSet<string>();
        private readonly Dictionary<Control, int> transitionRowHeights = new Dictionary<Control, int>();
        private readonly Dictionary<FoldGroupHeader, Timer> transitionFoldMotions = new Dictionary<FoldGroupHeader, Timer>();
        private Panel animHostPanel;
        private Panel playbackBarPanel;
        private readonly Dictionary<PlayerAnimationState, StateRangeEditorControls> stateRangeEditors =
            new Dictionary<PlayerAnimationState, StateRangeEditorControls>();
        private readonly Dictionary<PreviewAction, List<Button>> inlineBindingButtons =
            new Dictionary<PreviewAction, List<Button>>();
        private IMessageFilter inlineBindingFilter;
        private bool spaceShortcutHeld;
        private DateTime lastSimulationTickUtc;
        private double effectFrameAccumulator;
        private int effectCurrentCell = 1;
        private bool syncingStateEditor;
        private bool simulationRangesInitialized;
        private bool adjustingPreviewSplit;
        private bool parameterSettingsSelected;
        private bool closing;
        private int loadingDepth;
        private SpriteColorBlendMode colorBlendMode = SpriteColorBlendMode.Multiply;
        private Color spriteAdjustmentColor = Color.White;
        private int spriteAdjustmentStrength = 100;
        private CancellationTokenSource colorPreviewCancellation;
        private readonly SemaphoreSlim colorPreviewGate = new SemaphoreSlim(1, 1);
        private int colorPreviewGeneration;
        private string pendingPreviewNote;
        private readonly SpriteImagePipeline imagePipeline = new SpriteImagePipeline();
        private string statusBeforeLoading = "";
        private PreviewWorkspacePage previewWorkspacePage = PreviewWorkspacePage.Preview;
        private static readonly Size SimulationSceneSize = new Size(640, 360);

        public MainForm()
        {
            undoManager = new UndoManager<AppStateSnapshot>(CaptureState, RestoreState, StatesEqual);
            Loc.Initialize();
            UiFont.SetLanguage(Loc.Current);
            Loc.LanguageChanged += OnUiLanguageChanged;
            Text = AppTitle;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = titleBack;
            ForeColor = lightText;
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(ResizeGrip);
            Width = 1440;
            Height = 860;
            MinimumSize = new Size(1280, 700);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            ImeMode = ImeMode.Disable;
            clickTracker = new ClickTracker(this, NoteClickedControl);
            Application.AddMessageFilter(clickTracker);
            KeyDown += MainForm_KeyDown;
            KeyUp += MainForm_KeyUp;

            AllowDrop = true;
            DragEnter += MainForm_DragEnter;
            DragDrop += MainForm_DragDrop;

            animationTimer.Tick += AnimationTimer_Tick;
            previewInput.Activity += WakeSimulationTimer;

            InitializeSimulationSettings();

            sheetCanvas.ZoomChanged += (s, e) => UpdateZoomLabel();
            animCanvas.ZoomChanged += (s, e) => UpdateZoomLabel();
            animCanvas.MouseEnter += (s, e) =>
            {
                if (ContainsFocus && !IsTextEditorActive()) animCanvas.Focus();
            };
            animCanvas.MouseDown += (s, e) =>
            {
                if (previewTargetMode == PreviewTargetMode.Player && ContainsFocus && !IsTextEditorActive())
                    previewInput.MouseDown(e.Button);
            };
            animCanvas.MouseUp += (s, e) => previewInput.MouseUp(e.Button);

            restoringState = true;
            if (UiMotion.Enabled) Opacity = 0;
            BuildUi();
            Shown += (s, e) =>
            {
                ApplyResolutionAwareMinimumSize();
                ApplyInitialLayout();
                ApplyResponsiveToolbar();
                // DPI拡大率が96以外だと、AutoScaleMode.Dpiによる自動スケーリングと
                // 各パネルの初回自前レイアウトの実行順が環境によってズレることが
                // あるため、ウィンドウが実際に表示された後にもう一度確定させる。
                foreach (Action lateLayout in lateLayoutActions) lateLayout();
                ResetViewsToFit();
            };
            Resize += (s, e) =>
            {
                PausePlaybackWhileMinimized();
                ApplyResponsiveToolbar();
                AdjustLeftWorkspaceWidth();
                ApplyPreviewSplitOnly();
            };
            DpiChanged += (s, e) => TryBeginInvoke(ApplyResolutionAwareMinimumSize);
            Activated += (s, e) => StartPlayerPreviewIfPossible();
            Deactivate += (s, e) =>
            {
                // 別のウィンドウへ移っても再生は止めない（押しっぱなしのキーだけ離した扱いにする）。
                spaceShortcutHeld = false;
                previewInput.Clear();
            };
            UpdateTree();
            UpdatePreviewSafe();
            restoringState = false;
            undoManager.Initialize();
            InitializeMemoSupport();
            InitializeSelectionSupport();
            InitializeMapSupport();
            InitializeProjectSupport();
            Shown += (s, e) => RevealWhenIdle();
            InitializeUpdateSupport();
        }

        private void InitializeSimulationSettings()
        {
            foreach (PlayerAnimationState state in Enum.GetValues(typeof(PlayerAnimationState)))
            {
                playerClips[state] = new AnimationClipSettings
                {
                    Enabled = state == PlayerAnimationState.Idle,
                    StartCell = 1,
                    EndCell = 1,
                    Fps = 12
                };
            }
            playerAnimator = new SpriteAnimationController(playerClips);
            playerAnimator.Reset(PlayerAnimationState.Idle, 1);
            effectMotion.Reset(SimulationSceneSize, new Size(64, 64));
        }

        // 左ペインのファイルツリーと、その右クリックメニュー（元に戻す／やり直し／
        // フォルダ操作／削除）をまとめて構築する。ここで作る各コントロールは
        // すべてフィールドなので、他のBuildUi内セクションから参照されることはない。
        private void BuildFolderTreeAndContextMenu()
        {
            treeView.Dock = DockStyle.Fill;
            treeView.AllowDrop = true;
            treeView.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            treeView.ItemHeight = 50;
            treeView.BackColor = darkPanel;
            treeView.ForeColor = lightText;
            // 自前で描く一覧（FolderTreeView）。1px 単位でなめらかにスクロールし、開閉は中の行が伸び縮みする。
            treeView.DrawNode += TreeView_DrawNode;
            treeView.ItemDrag += TreeView_ItemDrag;
            treeView.DragEnter += TreeView_DragEnter;
            treeView.DragOver += TreeView_DragOver;
            treeView.DragDrop += TreeView_DragDrop;
            treeView.DragLeave += (s, e) => { internalDragNode = null; HideDropIndicator(); };
            treeView.NodeMouseClick += TreeView_NodeMouseClick;
            treeView.AfterSelect += (s, e) =>
            {
                if (e.Action != TreeViewAction.ByKeyboard) return;
                selectedFolders.Clear();
                selectedImages.Clear();
                ImageFolder keyboardFolder = e.Node == null ? null : e.Node.Tag as ImageFolder;
                ImageItem keyboardImage = e.Node == null ? null : e.Node.Tag as ImageItem;
                if (keyboardFolder != null)
                {
                    selectedFolders.Add(keyboardFolder);
                    folderSelectionAnchor = keyboardFolder;
                    imageSelectionAnchor = null;
                }
                else if (keyboardImage != null)
                {
                    selectedImages.Add(keyboardImage);
                    imageSelectionAnchor = keyboardImage;
                    folderSelectionAnchor = null;
                }
                else
                {
                    folderSelectionAnchor = null;
                    imageSelectionAnchor = null;
                }
                treeView.Invalidate();
            };
            // フォルダを閉じたら、隠れた画像を選択から外す（Delete などが見えない画像を対象にしないように）。
            // 選択が何も残らず、選択の印がフォルダへ移ったときは、そのフォルダを選ぶ。
            treeView.AfterCollapse += (s, e) =>
            {
                ImageFolder closed = e.Node == null ? null : e.Node.Tag as ImageFolder;
                if (closed == null) return;
                int removed = selectedImages.RemoveWhere(closed.Items.Contains);
                if (imageSelectionAnchor != null && closed.Items.Contains(imageSelectionAnchor)) imageSelectionAnchor = null;
                if (removed > 0 && selectedImages.Count == 0 && selectedFolders.Count == 0 && ReferenceEquals(treeView.SelectedNode, e.Node))
                {
                    selectedFolders.Add(closed);
                    folderSelectionAnchor = closed;
                }
                if (removed > 0) RefreshSelectionViews();
            };
            treeView.MouseDoubleClick += (s, e) =>
            {
                if (treeView.GetNodeAt(e.Location) == null) OpenFileDropDialog();
            };
            treeView.KeyDown += TreeView_KeyDown;

            BindText(undoMenuItem, "menu.undo");
            undoMenuItem.ShortcutKeyDisplayString = "Ctrl+Z";
            undoMenuItem.Click += (s, e) => UndoLastOperation();
            BindText(redoMenuItem, "menu.redo");
            redoMenuItem.ShortcutKeyDisplayString = "Ctrl+Y";
            redoMenuItem.Click += (s, e) => RedoLastOperation();
            BindText(addFolderMenuItem, "button.addFolder");
            addFolderMenuItem.Click += (s, e) => CreateEmptyFolder();
            BindText(renameFolderMenuItem, "menu.renameFolder");
            renameFolderMenuItem.Click += (s, e) => RenameSelectedFolder();
            BindText(sortFolderMenuItem, "menu.sortAscending");
            sortFolderMenuItem.Click += (s, e) => SortFoldersAscending();
            BindText(deleteTreeMenuItem, "button.delete");
            deleteTreeMenuItem.ShortcutKeyDisplayString = "Delete";
            deleteTreeMenuItem.Image = CreateDeleteIcon();
            deleteTreeMenuItem.Click += (s, e) => RemoveSelectedNode();
            treeContextMenu.BackColor = darkPanel;
            treeContextMenu.ForeColor = lightText;
            treeContextMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            StyleMenuItem(undoMenuItem);
            StyleMenuItem(redoMenuItem);
            StyleMenuItem(addFolderMenuItem);
            StyleMenuItem(renameFolderMenuItem);
            StyleMenuItem(sortFolderMenuItem);
            StyleMenuItem(deleteTreeMenuItem);
            // 元に戻す／やり直しは、フォルダだけでなくアプリ全体の操作なので、このメニューには入れない
            // （Ctrl+Z / Ctrl+Y のショートカットで使う）。undoMenuItem・redoMenuItem は有効/無効の状態を持つために残す。
            treeContextMenu.Items.Add(addFolderMenuItem);
            treeContextMenu.Items.Add(renameFolderMenuItem);
            treeContextMenu.Items.Add(sortFolderMenuItem);
            treeContextMenu.Items.Add(new ToolStripSeparator());
            treeContextMenu.Items.Add(deleteTreeMenuItem);
            treeView.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && treeView.Nodes.Count == 0)
                {
                    OpenFileDropDialog();
                }
            };
            treeView.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && treeView.GetNodeAt(e.Location) == null)
                    ShowTreeContextMenu(null, e.Location);
            };

            BuildEmptyDropZone();
        }

        // 画像が1枚もない状態で「ファイルを追加」という小さな文字リンクだけが
        // 表示される案内が分かりにくかったため、ドラッグ&ドロップ先そのものが
        // 見える破線の受け皿に置き換える。treeViewと同じ位置にDock=Fillで重ね、
        // 画像が無いときだけこちらを表示する（UpdateTree内で切り替え）。
        private bool dropZoneHover;                  // ファイルをドラッグして受け皿の上にいる
        private float dropArrowOffset;               // 弾む矢印の上下のずれ（px、上がマイナス）
        private Timer dropBounceTimer;
        private readonly System.Diagnostics.Stopwatch dropBounceClock = new System.Diagnostics.Stopwatch();
        private const int DropBouncePeriodMs = 650;

        // ドラッグで上に来たら、枠を明るくして矢印を上下に弾ませる（離れる・落とすと止める）。
        private void SetDropZoneHover(bool hover, Control icon)
        {
            if (dropZoneHover == hover) return;
            dropZoneHover = hover;
            if (hover && UiMotion.Enabled)
            {
                dropBounceClock.Restart();
                if (dropBounceTimer == null)
                {
                    dropBounceTimer = new Timer { Interval = 15 };
                    dropBounceTimer.Tick += (s, e) =>
                    {
                        double phase = (dropBounceClock.ElapsedMilliseconds % DropBouncePeriodMs) / (double)DropBouncePeriodMs;
                        dropArrowOffset = -5f * (float)Math.Abs(Math.Sin(Math.PI * phase));
                        icon.Invalidate();
                    };
                }
                dropBounceTimer.Start();
            }
            else
            {
                if (dropBounceTimer != null) dropBounceTimer.Stop();
                dropArrowOffset = 0;
                icon.Invalidate();
            }
            emptyDropZone.Invalidate();
        }

        private void BuildEmptyDropZone()
        {
            emptyDropZone.Dock = DockStyle.Fill;
            emptyDropZone.BackColor = Color.FromArgb(27, 36, 42);
            emptyDropZone.CornerRadius = 10;
            emptyDropZone.Margin = Padding.Empty;
            emptyDropZone.AllowDrop = true;
            emptyDropZone.DragEnter += TreeView_DragEnter;
            emptyDropZone.DragOver += TreeView_DragOver;
            emptyDropZone.DragDrop += TreeView_DragDrop;
            emptyDropZone.DragLeave += (s, e) => internalDragNode = null;
            emptyDropZone.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(1, 1, emptyDropZone.Width - 3, emptyDropZone.Height - 3);
                Color dash = dropZoneHover ? Color.FromArgb(150, 140, 255) : Color.FromArgb(75, 86, 95);
                using (var pen = new Pen(dash, dropZoneHover ? 2.2f : 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                using (var path = CreateRoundedPath(rect, 10))
                    e.Graphics.DrawPath(pen, path);
            };

            var iconCircle = new RoundedPanel
            {
                Size = new Size(56, 56),
                BackColor = Color.FromArgb(42, 40, 120),
                CornerRadius = 28,
                BackdropColor = Color.FromArgb(27, 36, 42)
            };
            iconCircle.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(169, 163, 255), 1.8f))
                {
                    int cx = iconCircle.Width / 2;
                    int cy = iconCircle.Height / 2;
                    float ay = cy + dropArrowOffset;   // 矢印だけが弾む（受け皿の線は動かない）
                    e.Graphics.DrawLine(pen, cx, ay - 10, cx, ay + 7);
                    e.Graphics.DrawLine(pen, cx - 7, ay - 3, cx, ay - 10);
                    e.Graphics.DrawLine(pen, cx + 7, ay - 3, cx, ay - 10);
                    e.Graphics.DrawLine(pen, cx - 9, cy + 10, cx - 9, cy + 14);
                    e.Graphics.DrawLine(pen, cx - 9, cy + 14, cx + 9, cy + 14);
                    e.Graphics.DrawLine(pen, cx + 9, cy + 14, cx + 9, cy + 10);
                }
            };

            var title = new Label
            {
                Text = Loc.T("label.dropHere"),
                AutoSize = true,
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(10.5f, FontStyle.Bold, GraphicsUnit.Point)
            };
            var subtitle = new Label
            {
                Text = Loc.T("hint.dropFormats"),
                AutoSize = true,
                ForeColor = mutedText,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point)
            };
            var addFilesDropButton = new Button { Text = Loc.T("button.addFiles"), Size = new Size(190, 36) };
            ApplyButtonStyle(addFilesDropButton);
            addFilesDropButton.BackColor = accentColor;
            addFilesDropButton.FlatAppearance.BorderColor = accentColor;
            addFilesDropButton.FlatAppearance.MouseOverBackColor = accentHoverColor;
            addFilesDropButton.FlatAppearance.MouseDownBackColor = accentPressedColor;
            addFilesDropButton.Click += (s, e) => OpenFileDropDialog();

            var addFolderDropButton = new Button { Text = Loc.T("button.addFolder"), Size = new Size(190, 36) };
            ApplyButtonStyle(addFolderDropButton);
            addFolderDropButton.Click += (s, e) => OpenFolderDropDialog();
            // 最初の画面から、保存したプロジェクト（サンプルなど）もすぐ開けるようにする。
            var openProjectDropButton = new Button { Text = Loc.T("button.openProject"), Size = new Size(190, 36) };
            ApplyButtonStyle(openProjectDropButton);
            openProjectDropButton.Click += (s, e) => OpenProjectWithDialog();
            BindText(title, "label.dropHere");
            BindText(subtitle, "hint.dropFormats");
            BindText(addFilesDropButton, "button.addFiles");
            BindText(addFolderDropButton, "button.addFolder");
            BindText(openProjectDropButton, "button.openProject");

            emptyDropZone.Controls.Add(iconCircle);
            emptyDropZone.DragEnter += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) SetDropZoneHover(true, iconCircle);
            };
            emptyDropZone.DragLeave += (s, e) => SetDropZoneHover(false, iconCircle);
            emptyDropZone.DragDrop += (s, e) => SetDropZoneHover(false, iconCircle);
            emptyDropZone.Controls.Add(title);
            emptyDropZone.Controls.Add(subtitle);
            emptyDropZone.Controls.Add(addFilesDropButton);
            emptyDropZone.Controls.Add(addFolderDropButton);
            emptyDropZone.Controls.Add(openProjectDropButton);

            Action layoutEmptyDropZone = () =>
            {
                if (emptyDropZone.IsDisposed || emptyDropZone.Width <= 0) return;
                int centerX = emptyDropZone.Width / 2;
                // 幅の広いフォントや狭いペインでも枠からはみ出さないよう、文字は枠幅に
                // 収まるまで折り返し、ボタンも枠幅を超えない大きさにする。
                int maxTextWidth = Math.Max(60, emptyDropZone.Width - 40);
                title.MaximumSize = new Size(maxTextWidth, 0);
                title.TextAlign = ContentAlignment.TopCenter;
                subtitle.MaximumSize = new Size(maxTextWidth, 0);
                addFilesDropButton.Width = Math.Min(190, maxTextWidth);
                addFolderDropButton.Width = Math.Min(190, maxTextWidth);
                openProjectDropButton.Width = Math.Min(190, maxTextWidth);
                int y = Math.Max(20, emptyDropZone.Height / 2 - 150);
                iconCircle.Location = new Point(centerX - iconCircle.Width / 2, y);
                y += iconCircle.Height + 14;
                title.Location = new Point(centerX - title.Width / 2, y);
                y += title.Height + 6;
                subtitle.Location = new Point(centerX - subtitle.Width / 2, y);
                y += subtitle.Height + 16;
                addFilesDropButton.Location = new Point(centerX - addFilesDropButton.Width / 2, y);
                y += addFilesDropButton.Height + 8;
                addFolderDropButton.Location = new Point(centerX - addFolderDropButton.Width / 2, y);
                y += addFolderDropButton.Height + 18;   // 画像の追加とは別の操作なので少し離す
                openProjectDropButton.Location = new Point(centerX - openProjectDropButton.Width / 2, y);
            };
            emptyDropZone.Resize += (s, e) => layoutEmptyDropZone();
            emptyDropZone.HandleCreated += (s, e) => layoutEmptyDropZone();
            lateLayoutActions.Add(layoutEmptyDropZone);
            layoutEmptyDropZone();
            localizedBindings.Add(layoutEmptyDropZone);
        }

        private void BuildUi()
        {
            BuildFolderTreeAndContextMenu();

            var columnsLabel = MakeTopLabel("");
            BindText(columnsLabel, "field.columns");
            columnsBox.Minimum = 1;
            columnsBox.Maximum = 999;
            columnsBox.Value = 8;
            columnsBox.AutoSize = false;
            columnsBox.Size = new Size(100, 44);
            columnsBox.Margin = new Padding(5, 8, 15, 3);
            columnsBox.ValueChanged += (s, e) =>
            {
                // フォルダごとに新しい行から始まるため、横セル数でセル番号が変わる。割り当てを同じ画像へ付け替える。
                int previousColumns = appliedColumns;
                appliedColumns = (int)columnsBox.Value;
                if (!restoringState && previousColumns != appliedColumns)
                    RemapCellAssignments(ComputeCellNumbers(folders, previousColumns), ComputeCellNumbers(folders));
                QueuePreviewUpdate();
                CommitUndoableChange();
            };
            ApplyInputStyle(columnsBox);

            var scaleLabel = MakeTopLabel("");
            BindText(scaleLabel, "field.scale");
            scaleComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            scaleComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            scaleComboBox.ItemHeight = 30;
            scaleComboBox.FlatStyle = FlatStyle.Flat;
            scaleComboBox.Size = new Size(122, 44);
            scaleComboBox.Margin = new Padding(5, 8, 18, 3);
            scaleComboBox.DrawItem += ComboBox_DrawItem;
            scaleComboBox.Items.Add(Loc.T("scale.original"));
            scaleComboBox.Items.Add("1/2");
            scaleComboBox.Items.Add("1/4");
            scaleComboBox.Items.Add("1/8");
            scaleComboBox.SelectedIndex = 0;
            localizedBindings.Add(() => RelabelComboItems(scaleComboBox, "scale.original"));
            scaleComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (applyingLanguage) return;
                QueuePreviewUpdate();
                CommitUndoableChange();
            };
            ApplyInputStyle(scaleComboBox);

            BindText(exportLabel, "label.export");
            exportLabel.AutoSize = true;
            exportLabel.Margin = new Padding(16, 12, 8, 3);

            // PNG/TGA/GIFは角丸のボタンとして並べ、独立したカード内に置く。
            Color exportGroupColor = Color.FromArgb(42, 40, 120);
            const int exportButtonWidth = 60;
            const int exportButtonHeight = 40;
            const int exportButtonGap = 6;
            exportPngButton.Text = "PNG";
            exportPngButton.Tag = "PNG";
            exportPngButton.Size = new Size(exportButtonWidth, exportButtonHeight);
            exportPngButton.Location = new Point(0, 0);
            exportPngButton.Click += (s, e) => Export(ImageOutputFormat.Png);
            StyleGroupedExportButton(exportPngButton, exportGroupColor);

            exportTgaButton.Text = "TGA";
            exportTgaButton.Tag = "TGA";
            exportTgaButton.Size = new Size(exportButtonWidth, exportButtonHeight);
            exportTgaButton.Location = new Point(exportButtonWidth + exportButtonGap, 0);
            exportTgaButton.Click += (s, e) => Export(ImageOutputFormat.Tga);
            StyleGroupedExportButton(exportTgaButton, exportGroupColor);

            exportGifButton.Text = "GIF";
            exportGifButton.Tag = "GIF";
            exportGifButton.Size = new Size(exportButtonWidth, exportButtonHeight);
            exportGifButton.Location = new Point((exportButtonWidth + exportButtonGap) * 2, 0);
            exportGifButton.Click += (s, e) => Export(ImageOutputFormat.Gif);
            StyleGroupedExportButton(exportGifButton, exportGroupColor);

            exportWebPButton.Text = "WebP";
            exportWebPButton.Tag = "WebP";
            exportWebPButton.Size = new Size(exportButtonWidth, exportButtonHeight);
            exportWebPButton.Location = new Point((exportButtonWidth + exportButtonGap) * 3, 0);
            exportWebPButton.Click += (s, e) => Export(ImageOutputFormat.WebP);
            StyleGroupedExportButton(exportWebPButton, exportGroupColor);
            // 動く WebP を1コマ目しか出さないビューアーがあるため、開くソフトを案内する（「？」は付けない）。
            BindAction(() => toolTip.SetToolTip(exportWebPButton, Loc.T("tooltip.exportWebP")));

            exportButtonGroup.Size = new Size(exportButtonWidth * 4 + exportButtonGap * 3, exportButtonHeight);
            exportButtonGroup.Margin = new Padding(4, 2, 12, 2);
            exportButtonGroup.BackColor = Color.Transparent;
            exportButtonGroup.Controls.Add(exportPngButton);
            exportButtonGroup.Controls.Add(exportTgaButton);
            exportButtonGroup.Controls.Add(exportGifButton);
            exportButtonGroup.Controls.Add(exportWebPButton);

            // エクスポート操作は他の設定項目と役割が異なる主要アクションのため、
            // ツールバーの右端に一段明るい背景の独立したカードとして視覚的に
            // 分離する（書き出しの挙動自体は各ボタン即時実行のまま変更しない）。
            exportCard = new RoundedFlowPanel
            {
                Location = new Point(0, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = panelElevated,
                CornerRadius = RadiusLg
            };
            exportCard.Controls.Add(exportLabel);
            exportCard.Controls.Add(exportButtonGroup);

            BindText(gridNumberCheckBox, "check.exportNumbers");
            gridNumberCheckBox.AutoSize = true;
            gridNumberCheckBox.Margin = new Padding(22, 19, 15, 3);
            gridNumberCheckBox.ForeColor = lightText;
            gridNumberCheckBox.BackColor = Color.Transparent;
            gridNumberCheckBox.CheckedChanged += (s, e) => CommitUndoableChange();
            BindText(align4CheckBox, "check.align4");
            align4CheckBox.AutoSize = true;
            align4CheckBox.Margin = new Padding(10, 4, 10, 4);
            align4CheckBox.ForeColor = lightText;
            align4CheckBox.BackColor = Color.Transparent;
            align4CheckBox.Checked = SheetSizeRule.Align4;
            align4CheckBox.CheckedChanged += (s, e) =>
            {
                SheetSizeRule.Align4 = align4CheckBox.Checked;
                if (mapPalette != null) { mapPalette.AlignSheetTo4 = align4CheckBox.Checked; RefreshMapHeaders(); }
                QueuePreviewUpdateCore(null, true);
            };
            BindText(fillEmptyCellsButton, "button.fillEmptyCells");
            fillEmptyCellsButton.Size = new Size(120, 46);
            ApplyButtonStyle(fillEmptyCellsButton);
            fillEmptyCellsButton.Margin = new Padding(0, 8, 10, 3);
            BindAction(() =>
            {
                fillEmptyCellsButton.Width = Math.Max(90, TextRenderer.MeasureText(fillEmptyCellsButton.Text, fillEmptyCellsButton.Font).Width + 28);
                toolTip.SetToolTip(fillEmptyCellsButton, Loc.T("tooltip.fillEmptyCells"));
            });
            fillEmptyCellsButton.Visible = false;
            fillEmptyCellsButton.Click += (s, e) => FillEmptyCells();

            BindText(refreshPreviewButton, "button.refresh");
            refreshPreviewButton.Size = new Size(94, 46);
            // 左の回転アイコンと余白（約44px）を足した幅にする。言語で文言の長さが変わるので切り替えのたびに測り直す。
            BindAction(() => refreshPreviewButton.Width = Math.Max(94,
                TextRenderer.MeasureText(refreshPreviewButton.Text, refreshPreviewButton.Font).Width + 44));
            refreshPreviewButton.Click += (s, e) => QueuePreviewUpdate();
            ApplyButtonStyle(refreshPreviewButton);

            // 「選択を削除」。以前はツールバー側にも同じ操作の「削除」ボタンが
            // 別にあり、左下の🗑（すべてクリア）と紛らわしかったため、
            // ここへ一本化する。
            BindText(clearButton, "button.deleteSelection");
            clearButton.Size = new Size(92, 36);
            ApplyButtonStyle(clearButton);
            clearButton.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            clearButton.Click += (s, e) => RemoveSelectedNode();

            // 320px幅の左ペインではアイコン+文字が収まらないため、文字のみの
            // 危険操作ボタン（赤字）にする。アイコンで示していた意味は
            // AccessibleName/ツールチップで補う。
            BindText(removeButton, "button.clearAll");
            removeButton.Height = 36;
            removeButton.AutoSize = false;
            removeButton.Click += (s, e) => ClearAllWithConfirm();
            removeButton.FlatStyle = FlatStyle.Flat;
            removeButton.FlatAppearance.BorderSize = 0;
            removeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 30, 32);
            removeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(54, 34, 37);
            removeButton.BackColor = darkPanel;
            removeButton.ForeColor = dangerColor;
            removeButton.UseVisualStyleBackColor = false;
            removeButton.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            removeButton.TextAlign = ContentAlignment.MiddleCenter;
            removeButton.Padding = Padding.Empty;
            BindAction(() =>
            {
                removeButton.AccessibleName = Loc.T("button.clearAll");
                toolTip.SetToolTip(removeButton, Loc.T("tooltip.clearAll"));
            });

            moveUpButton.Text = "↑";
            moveUpButton.Size = new Size(36, 36);
            moveUpButton.Click += (s, e) => MoveSelectedItem(-1);
            ApplyButtonStyle(moveUpButton);
            moveUpButton.Font = new Font("Segoe UI", 12.0f, FontStyle.Regular, GraphicsUnit.Point);
            BindAction(() =>
            {
                moveUpButton.AccessibleName = Loc.T("tooltip.moveUp");
                toolTip.SetToolTip(moveUpButton, Loc.T("tooltip.moveUp"));
            });

            moveDownButton.Text = "↓";
            moveDownButton.Size = new Size(36, 36);
            moveDownButton.Click += (s, e) => MoveSelectedItem(1);
            ApplyButtonStyle(moveDownButton);
            moveDownButton.Font = new Font("Segoe UI", 12.0f, FontStyle.Regular, GraphicsUnit.Point);
            BindAction(() =>
            {
                moveDownButton.AccessibleName = Loc.T("tooltip.moveDown");
                toolTip.SetToolTip(moveDownButton, Loc.T("tooltip.moveDown"));
            });

            fpsBox.Minimum = 1;
            fpsBox.Maximum = 120;
            fpsBox.Increment = 5;
            fpsBox.Value = 30;
            fpsBox.AutoSize = false;
            fpsBox.Size = new Size(76, 44);
            fpsBox.Margin = new Padding(5, 8, 12, 3);
            fpsBox.ValueChanged += (s, e) =>
            {
                SyncUnifiedFps();
                UpdateTimerInterval();
                CommitUndoableChange();
            };
            ApplyInputStyle(fpsBox);

            startCellBox.Minimum = 1;
            startCellBox.Maximum = 999;
            startCellBox.Value = 1;
            startCellBox.AutoSize = false;
            startCellBox.Size = new Size(76, 44);
            startCellBox.Margin = new Padding(5, 8, 12, 3);
            ApplyInputStyle(startCellBox);
            startCellBox.ValueChanged += (s, e) =>
            {
                ClampRangeBoxes();
                animationIndex = GetFirstPlayableFrameIndex();
                UpdateAnimationPreview();
                CommitUndoableChange();
            };

            endCellBox.Minimum = 1;
            endCellBox.Maximum = 999;
            endCellBox.Value = 1;
            endCellBox.AutoSize = false;
            endCellBox.Size = new Size(76, 44);
            endCellBox.Margin = new Padding(5, 8, 12, 3);
            ApplyInputStyle(endCellBox);
            endCellBox.ValueChanged += (s, e) =>
            {
                ClampRangeBoxes();
                animationIndex = GetFirstPlayableFrameIndex();
                UpdateAnimationPreview();
                CommitUndoableChange();
            };

            playToggleButton.Text = "";
            playToggleButton.Size = new Size(56, 46);
            ApplyButtonStyle(playToggleButton);
            playToggleButton.BackColor = accentColor;
            playToggleButton.FlatAppearance.BorderColor = Color.FromArgb(115, 103, 255);
            playToggleButton.FlatAppearance.MouseOverBackColor = accentHoverColor;
            playToggleButton.FlatAppearance.MouseDownBackColor = accentPressedColor;
            BindAction(() =>
            {
                SetPlayAccessibleName(animationTimer.Enabled);
                toolTip.SetToolTip(playToggleButton, Loc.T("tooltip.playStop"));
            });
            playToggleButton.Paint += PlayToggleButton_Paint;
            playToggleButton.Click += (s, e) => TogglePlayback();

            // Frame情報はプレビューのペイン見出しへ、操作ヒントは下部ステータスバーへ
            // 移すため、ここではテキストだけ設定する（見た目はStyleChip()で統一）。
            animInfoLabel.Text = "Frame: -";

            zoomHintLabel.Text = Loc.T("hint.zoom");
            zoomHintLabel.ForeColor = Color.FromArgb(140, 151, 160);
            zoomHintLabel.Alignment = ToolStripItemAlignment.Right;

            // 元は「操作ツールバー2段（上段が設定、下段がFrame情報とズームヒント）」
            // だったが、設定に無関係な情報が常時ツールバーを圧迫していたため1段化。
            toolbarPanel = new Panel();
            toolbarPanel.Dock = DockStyle.Top;
            toolbarPanel.Height = 68;
            toolbarPanel.BackColor = darkBack;
            toolbarPanel.ForeColor = lightText;
            ControlStyleHelper.EnableResizeRedraw(toolbarPanel);
            toolbarPanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor))
                    e.Graphics.DrawLine(pen, 0, toolbarPanel.Height - 1, toolbarPanel.Width, toolbarPanel.Height - 1);
            };

            BindText(loadingIndicatorLabel, "label.loadingIndicator");
            loadingIndicatorLabel.AutoSize = true;
            loadingIndicatorLabel.ForeColor = Color.FromArgb(255, 205, 64);
            loadingIndicatorLabel.BackColor = Color.Transparent;
            loadingIndicatorLabel.Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point);
            loadingIndicatorLabel.Margin = new Padding(18, 19, 8, 3);
            loadingIndicatorLabel.Visible = false;

            primaryToolbarRow = new FlowLayoutPanel();
            primaryToolbarRow.Dock = DockStyle.Fill;
            primaryToolbarRow.Height = 68;
            primaryToolbarRow.Padding = new Padding(22, 10, 18, 4);
            primaryToolbarRow.WrapContents = false;
            primaryToolbarRow.AutoScroll = false;
            primaryToolbarRow.BackColor = darkBack;
            primaryToolbarRow.Controls.Add(columnsLabel);
            primaryToolbarRow.Controls.Add(CreateInputHost(columnsBox, 100));
            primaryToolbarRow.Controls.Add(fillEmptyCellsButton);
            primaryToolbarRow.Controls.Add(scaleLabel);
            primaryToolbarRow.Controls.Add(CreateComboHost(scaleComboBox, 122));
            primaryToolbarRow.Controls.Add(MakeToolbarDivider());
            primaryToolbarRow.Controls.Add(WithHelp(gridNumberCheckBox, "help.exportNumbers", false));
            primaryToolbarRow.Controls.Add(refreshPreviewButton);
            primaryToolbarRow.Controls.Add(loadingIndicatorLabel);
            toolbarPanel.Controls.Add(primaryToolbarRow);
            // カードがツールバーの上下端に密着しないよう、余白付きの受け皿に入れて右寄せする。
            var exportHost = new Panel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 12, 8),
                BackColor = darkBack
            };
            exportHost.Controls.Add(exportCard);
            toolbarPanel.Controls.Add(exportHost);

            mainSplit = new SplitContainer();
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.FixedPanel = FixedPanel.Panel1;
            mainSplit.SplitterWidth = 1;
            // 利用者が分割線を動かして決めた左ペインの幅を覚え、タブを切り替えても戻さない。
            mainSplit.SplitterMoved += (s, e) =>
            {
                if (!adjustingLeftWidth) userLeftWidth = mainSplit.SplitterDistance;
            };
            mainSplit.BackColor = dividerColor;
            mainSplit.Panel1.BackColor = darkBack;
            mainSplit.Panel2.BackColor = workspaceBack;

            var leftPanel = new Panel();
            leftPanel.Dock = DockStyle.Fill;
            leftPanel.Padding = Padding.Empty;
            leftPanel.Width = 384;
            leftPanel.BackColor = darkPanel;

            var leftWorkspaceTabs = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = darkPanel
            };
            // 台・選択中の色付き角丸・文字はタブ自身がまとめて描く（ずれ・ちらつき防止）。
            workspaceTabBar.BackColor = workspaceBack;
            workspaceTabBar.CornerRadius = RadiusLg;
            workspaceTabBar.IndicatorRadius = RadiusSm;
            workspaceTabBar.IndicatorColor = accentColor;
            workspaceTabBar.HoverColor = panelElevated;
            workspaceTabBar.ForeColor = mutedText;
            workspaceTabBar.SelectedForeColor = Color.White;
            workspaceTabBar.Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point);
            BindAction(() =>
            {
                workspaceTabBar.SetTabText((int)PreviewWorkspacePage.Preview, Loc.T("tab.folder"));
                workspaceTabBar.SetTabText((int)PreviewWorkspacePage.StateTransitions, Loc.T("tab.transitions"));
                workspaceTabBar.SetTabText((int)PreviewWorkspacePage.Parameters, Loc.T("tab.settings"));
            });
            workspaceTabBar.TabClicked += index => SetPreviewWorkspacePage((PreviewWorkspacePage)index);
            leftWorkspaceTabs.Controls.Add(workspaceTabBar);
            // タブの台は左ペインの幅いっぱい（左右12px空け）。各タブの幅は台の中で等分する。
            Action layoutTabSegment = () =>
            {
                if (workspaceTabBar.IsDisposed || leftWorkspaceTabs.Width <= 0) return;
                const int outerMargin = 12;
                int totalWidth = Math.Max(60, leftWorkspaceTabs.Width - outerMargin * 2);
                workspaceTabBar.SetBounds(outerMargin, 10, totalWidth, 40);
            };
            leftWorkspaceTabs.Resize += (s, e) => layoutTabSegment();
            lateLayoutActions.Add(layoutTabSegment);
            layoutTabSegment();

            leftWorkspaceHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = darkPanel
            };
            folderWorkspacePage = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 12, 12, 0),
                BackColor = darkPanel
            };
            folderWorkspacePage.Controls.Add(treeView);
            folderWorkspacePage.Controls.Add(emptyDropZone);

            // 「選択を削除」（左：通常操作）と「すべてクリア」（右：取り消しにくい
            // 操作）を左右に分けて誤操作を防ぐ。すべてクリアは右端に固定したいため
            // FlowLayoutPanelではなくPanelへ手動配置する。
            var fileActions = new Panel();
            fileActions.Dock = DockStyle.Bottom;
            fileActions.Height = 60;
            fileActions.BackColor = darkPanel;
            ControlStyleHelper.EnableResizeRedraw(fileActions);
            fileActions.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawLine(pen, 0, 0, fileActions.Width, 0);
            };
            moveUpButton.SetBounds(12, 12, 36, 36);
            moveDownButton.SetBounds(54, 12, 36, 36);
            clearButton.SetBounds(96, 12, 92, 36);
            fileActions.Controls.Add(moveUpButton);
            fileActions.Controls.Add(moveDownButton);
            fileActions.Controls.Add(clearButton);
            fileActions.Controls.Add(removeButton);
            Action layoutFileActions = () =>
            {
                if (fileActions.IsDisposed) return;
                // 実際の描画幅は環境のテキスト幅で変わるため、固定幅にせず文字に合わせる。
                int clearWidth = Math.Max(92, TextRenderer.MeasureText(clearButton.Text, clearButton.Font).Width + 16);
                clearButton.SetBounds(96, 12, clearWidth, 36);
                int minX = 96 + clearWidth + 8;
                int removeWidth = TextRenderer.MeasureText(removeButton.Text, removeButton.Font).Width + 24;
                int x = Math.Max(minX, fileActions.Width - 12 - removeWidth);
                removeButton.SetBounds(x, (fileActions.Height - 36) / 2, removeWidth, 36);
            };
            fileActions.Resize += (s, e) => layoutFileActions();
            removeButton.SizeChanged += (s, e) => layoutFileActions();
            fileActions.HandleCreated += (s, e) => layoutFileActions();
            folderWorkspacePage.Controls.Add(fileActions);
            lateLayoutActions.Add(layoutFileActions);
            layoutFileActions();

            // ツリーの直上に「ファイル（Nフォルダ・M枚）」の件数表示と、追加操作の
            // アイコンボタンを置く。画像が1枚もない間はemptyDropZoneが全面を
            // 覆うため、この行自体をUpdateTree()側で非表示にする。
            fileCountRow.Dock = DockStyle.Top;
            fileCountRow.Height = 38;
            fileCountRow.BackColor = darkPanel;
            fileCountLabel.Dock = DockStyle.Fill;
            fileCountLabel.TextAlign = ContentAlignment.MiddleLeft;
            fileCountLabel.AutoEllipsis = true;
            fileCountLabel.ForeColor = mutedText;
            fileCountLabel.BackColor = Color.Transparent;
            fileCountLabel.Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point);
            addFileIconButton.Image = CreateAddFileIcon();
            StyleIconOnlyButton(addFileIconButton, Loc.T("button.addFiles"));
            BindAction(() => { addFileIconButton.AccessibleName = Loc.T("button.addFiles"); toolTip.SetToolTip(addFileIconButton, Loc.T("button.addFiles")); });
            addFileIconButton.Click += (s, e) => OpenFileDropDialog();
            addFolderIconButton.Image = CreateAddFolderIcon();
            StyleIconOnlyButton(addFolderIconButton, Loc.T("button.addFolder"));
            BindAction(() => { addFolderIconButton.AccessibleName = Loc.T("button.addFolder"); toolTip.SetToolTip(addFolderIconButton, Loc.T("button.addFolder")); });
            addFolderIconButton.Click += (s, e) => OpenFolderDropDialog();
            var fileCountButtons = new Panel { Dock = DockStyle.Right, Width = 66, BackColor = Color.Transparent };
            addFileIconButton.SetBounds(0, 4, 30, 30);
            addFolderIconButton.SetBounds(34, 4, 30, 30);
            fileCountButtons.Controls.Add(addFileIconButton);
            fileCountButtons.Controls.Add(addFolderIconButton);
            fileCountRow.Controls.Add(fileCountLabel);
            fileCountRow.Controls.Add(fileCountButtons);
            folderWorkspacePage.Controls.Add(fileCountRow);

            emptyDropZone.BringToFront();

            // 以前は左ペイン内（幅320px）だけの狭いステータス表示だったが、
            // 画像枚数・操作結果・ズーム操作ヒントはウィンドウ全体に関わる情報
            // なので、ウィンドウ最下部へ通し幅で表示する。
            bottomStatusStrip.Dock = DockStyle.Bottom;
            bottomStatusStrip.BackColor = titleBack;
            bottomStatusStrip.ForeColor = mutedText;
            bottomStatusStrip.SizingGrip = false;
            bottomStatusStrip.Padding = new Padding(16, 6, 16, 0);
            bottomStatusStrip.RenderMode = ToolStripRenderMode.System;
            bottomStatusStrip.GripStyle = ToolStripGripStyle.Hidden;
            bottomStatusStrip.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawLine(pen, 0, 0, bottomStatusStrip.Width, 0);
                PaintExportBar(e.Graphics);
            };
            statusLabel.ForeColor = mutedText;
            statusLabel.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            zoomHintLabel.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            bottomStatusStrip.Items.Add(statusLabel);
            bottomStatusStrip.Items.Add(zoomHintLabel);

            leftWorkspaceHost.Controls.Add(folderWorkspacePage);
            leftPanel.Controls.Add(leftWorkspaceHost);
            leftPanel.Controls.Add(leftWorkspaceTabs);

            previewSplit = new SplitContainer();
            previewSplit.Dock = DockStyle.Fill;
            previewSplit.Orientation = Orientation.Vertical;
            previewSplit.SplitterWidth = 1;
            previewSplit.BackColor = dividerColor;
            previewSplit.SplitterMoved += (s, e) =>
            {
                if (adjustingPreviewSplit || previewSplit.Width <= 0) return;
                previewSheetRatio = previewSplit.SplitterDistance / (double)previewSplit.Width;
            };

            sheetCanvas.Dock = DockStyle.Fill;
            animCanvas.Dock = DockStyle.Fill;
            // OS標準のFixedSingle枠は直角のため使わず、角丸に沿った枠線をキャンバス自身が描く。
            sheetCanvas.BorderColor = dividerColor;
            animCanvas.BorderColor = dividerColor;

            var sheetHost = new Panel();
            sheetHost.Dock = DockStyle.Fill;
            sheetHost.BackColor = workspaceBack;

            // シート／プレビュー双方に共通の見出し帯（タイトル＋チップ＋ズーム率）を
            // 追加し、以前は上部ツールバーやステータスバーに散らばっていた情報を
            // 各ペイン自身に持たせる。ヘッダーは端から端まで、内容表示部分だけに
            // 従来のPaddingを適用するため、内側にもう1枚コンテンツ用Panelを置く。
            var sheetHeaderBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = workspaceBack };
            ControlStyleHelper.EnableResizeRedraw(sheetHeaderBar);
            sheetHeaderBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor))
                    e.Graphics.DrawLine(pen, 0, sheetHeaderBar.Height - 1, sheetHeaderBar.Width, sheetHeaderBar.Height - 1);
            };
            sheetTitleLabel = new Label
            {
                Text = Loc.T("title.sheet"),
                AutoSize = true,
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(16, 14, 0, 0)
            };
            BindText(sheetTitleLabel, "title.sheet");
            StyleChip(sheetCellsChip);
            StyleChip(sheetSizeChip);
            StyleChip(sheetZoomChip);
            sheetCellsChip.Text = "—";
            sheetZoomChip.Text = "100%";
            animZoomChip.Text = "100%";
            BindText(sheetFitButton, "button.fit");
            sheetFitButton.AutoSize = true;
            sheetFitButton.Height = 28;
            sheetFitButton.Margin = new Padding(8, 8, 16, 0);
            sheetFitButton.Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point);
            ApplyButtonStyle(sheetFitButton);
            sheetFitButton.Click += (s, e) => { if (previewTargetMode == PreviewTargetMode.Map && mapPalette != null) mapPalette.Fit(true); else sheetCanvas.ResetToFitAnimated(); };
            BindText(previewFitButton, "button.fit");
            previewFitButton.AutoSize = true;
            previewFitButton.Height = 28;
            previewFitButton.Margin = new Padding(8, 8, 8, 0);
            previewFitButton.Font = sheetFitButton.Font;
            previewFitButton.Visible = false;
            ApplyButtonStyle(previewFitButton);
            previewFitButton.Click += (s, e) => { if (mapCanvas != null) mapCanvas.Fit(true); };
            sheetCellsChip.Margin = new Padding(8, 12, 0, 0);
            sheetSizeChip.Margin = new Padding(8, 12, 0, 0);
            sheetZoomChip.Margin = new Padding(8, 12, 0, 0);

            // 右側のボタンと重ならないよう、残りの幅に収める（はみ出したチップは切れる）。
            sheetHeaderLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = false,
                WrapContents = false, BackColor = Color.Transparent
            };
            sheetHeaderLeft.Controls.Add(sheetTitleLabel);
            sheetHeaderLeft.Controls.Add(sheetCellsChip);
            sheetHeaderLeft.Controls.Add(sheetSizeChip);
            // 幅が足りないときは、はみ出して切れる前に優先度の低いチップから隠す（サイズ→セル数）。
            sheetHeaderLeft.SizeChanged += (s, e) => FitSheetHeader();
            sheetTitleLabel.TextChanged += (s, e) => FitSheetHeader();
            sheetCellsChip.TextChanged += (s, e) => FitSheetHeader();
            sheetSizeChip.TextChanged += (s, e) => FitSheetHeader();
            FitSheetHeader();

            var sheetHeaderRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, BackColor = Color.Transparent
            };
            sheetHeaderRight.Controls.Add(sheetFitButton);
            sheetHeaderRight.Controls.Add(sheetZoomChip);

            sheetHeaderBar.Controls.Add(sheetHeaderLeft);
            sheetHeaderBar.Controls.Add(sheetHeaderRight);

            // 下だけ40pxという不均等な余白は、見出し帯（sheetHeaderBar）を追加する
            // 前のsheetHost自体のPaddingをそのまま踏襲した名残で、対応する下部要素は
            // 無い。そのためキャンバスが上寄りに見えていたので、プレビュー側
            // （animContentArea）に合わせて上下左右をほぼ均等にする。
            var sheetContentArea = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 20, 24, 20),
                BackColor = workspaceBack
            };
            sheetContentArea.Controls.Add(sheetCanvas);

            sheetHost.Controls.Add(sheetContentArea);
            sheetHost.Controls.Add(sheetHeaderBar);

            var animHost = new Panel();
            animHostPanel = animHost;
            animHost.Dock = DockStyle.Fill;
            animHost.BackColor = workspaceBack;

            var animHeaderBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = workspaceBack };
            ControlStyleHelper.EnableResizeRedraw(animHeaderBar);
            animHeaderBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor))
                    e.Graphics.DrawLine(pen, 0, animHeaderBar.Height - 1, animHeaderBar.Width, animHeaderBar.Height - 1);
            };
            var animTitleLabel = new Label
            {
                Text = Loc.T("title.preview"),
                AutoSize = true,
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(16, 14, 0, 0)
            };
            BindText(animTitleLabel, "title.preview");
            StyleChip(animInfoLabel);
            StyleChip(animZoomChip);
            animInfoLabel.Margin = new Padding(8, 12, 0, 0);
            animZoomChip.Margin = new Padding(0, 12, 16, 0);

            // 右側のボタンと重ならないよう、残りの幅に収める（はみ出したチップは切れる）。
            var animHeaderLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = false,
                WrapContents = false, BackColor = Color.Transparent
            };
            animHeaderLeft.Controls.Add(animTitleLabel);
            animHeaderLeft.Controls.Add(animInfoLabel);

            var animHeaderRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, BackColor = Color.Transparent
            };
            animHeaderRight.Controls.Add(previewFitButton);
            animHeaderRight.Controls.Add(animZoomChip);

            animHeaderBar.Controls.Add(animHeaderLeft);
            animHeaderBar.Controls.Add(animHeaderRight);

            var animContentArea = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(22, 22, 20, 18),
                BackColor = workspaceBack
            };

            BindText(previewTitleLabel, "title.preview");
            previewTitleLabel.ForeColor = lightText;
            previewTitleLabel.BackColor = Color.Transparent;
            previewTitleLabel.Font = UiFont.Create(10.0f, FontStyle.Bold, GraphicsUnit.Point);
            previewTitleLabel.AutoSize = true;
            previewTitleLabel.Location = new Point(8, 12);
            ConfigurePreviewModeEnum();
            int modeComboWidth = 126;
            foreach (object item in previewModeEnumComboBox.Items)
                modeComboWidth = Math.Max(modeComboWidth,
                    TextRenderer.MeasureText(item.ToString(), previewModeEnumComboBox.Font).Width + 60);
            previewModeEnumHost = CreateComboHost(previewModeEnumComboBox, modeComboWidth);
            previewModeEnumHost.Height = 36;
            previewModeEnumHost.Margin = new Padding(0, 6, 0, 0);

            BindText(frameAssignmentLabel, "label.frameAssign");
            frameAssignmentLabel.ForeColor = mutedText;
            frameAssignmentLabel.AutoSize = true;
            frameAssignmentLabel.Visible = false;
            BindText(previewParametersLabel, "label.paramsKeys");
            previewParametersLabel.ForeColor = mutedText;
            previewParametersLabel.AutoSize = true;
            previewParametersLabel.Visible = false;
            previewParametersLabel.Cursor = Cursors.Hand;
            previewParametersLabel.Click += (s, e) =>
            {
                if (previewTargetMode == PreviewTargetMode.Player) ShowKeySettingsDialog();
            };
            BindAction(() => toolTip.SetToolTip(previewParametersLabel, Loc.T("tooltip.keyAssign")));

            var playbackBar = new Panel();
            playbackBarPanel = playbackBar;
            playbackBar.Dock = DockStyle.Bottom;
            playbackBar.Height = 68;
            playbackBar.BackColor = workspaceBack;

            simulationSettingsPanel = BuildSimulationSettingsPanel();
            stateTransitionPage = BuildStateTransitionPage();
            leftWorkspaceHost.Controls.Add(simulationSettingsPanel);
            leftWorkspaceHost.Controls.Add(stateTransitionPage);
            folderWorkspacePage.BringToFront();

            var playbackContent = new FlowLayoutPanel();
            playbackContent.Height = 68;
            playbackContent.WrapContents = false;
            playbackContent.BackColor = workspaceBack;
            playbackContent.Controls.Add(playToggleButton);
            playbackBar.Controls.Add(playbackContent);

            int playbackPreferredWidth = GetFlowContentWidth(playbackContent);
            bool layingOutPlayback = false;
            Action layoutPlayback = () =>
            {
                if (layingOutPlayback || playbackBar.IsDisposed || animHost.ClientSize.Width <= 0) return;
                layingOutPlayback = true;
                bool compactPlayback = animHost.ClientSize.Width < playbackPreferredWidth + 4;
                int height = compactPlayback ? 118 : 68;
                playbackBar.Height = height;
                playbackContent.WrapContents = compactPlayback;
                playbackContent.SetBounds(
                    compactPlayback ? 0 : Math.Max(0, (playbackBar.ClientSize.Width - playbackPreferredWidth) / 2),
                    0,
                    compactPlayback ? playbackBar.ClientSize.Width : playbackPreferredWidth,
                    height);
                layingOutPlayback = false;
            };
            animHost.Resize += (s, e) =>
            {
                layoutPlayback();
                AdjustSimulationSettingsHeight();
            };

            // Frame/セル情報は以前は上部ツールバーの2段目や右上への絶対配置で
            // 表示していたが、プレビューペイン自身の見出し帯（animHeaderBar）へ
            // 統一し、FlowLayoutPanelに任せることで再配置クロージャが不要になった。
            animContentArea.Controls.Add(animCanvas);
            animContentArea.Controls.Add(playbackBar);
            animContentArea.Controls.Add(frameAssignmentLabel);
            animContentArea.Controls.Add(previewParametersLabel);
            frameAssignmentLabel.BringToFront();
            previewParametersLabel.BringToFront();
            animHost.Controls.Add(animContentArea);
            animHost.Controls.Add(animHeaderBar);
            layoutPlayback();
            AdjustSimulationSettingsHeight();

            previewSplit.Panel1.Controls.Add(sheetHost);
            previewSplit.Panel2.Controls.Add(animHost);

            mainSplit.Panel1.Controls.Add(leftPanel);
            mainSplit.Panel2.Controls.Add(previewSplit);

            Controls.Add(mainSplit);
            Controls.Add(bottomStatusStrip);
            Controls.Add(toolbarPanel);
            titleBar = BuildTitleBar();
            Controls.Add(titleBar);
            ApplyPreviewTargetModeUi();
        }

        private void ConfigurePreviewModeMenu()
        {
            previewModeMenu.BackColor = darkPanel;
            previewModeMenu.ForeColor = lightText;
            previewModeMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            var characterItem = new ToolStripMenuItem("");
            var effectItem = new ToolStripMenuItem("");
            BindText(characterItem, "mode.character");
            BindText(effectItem, "mode.effect");
            StyleMenuItem(characterItem);
            StyleMenuItem(effectItem);
            characterItem.Click += (s, e) => SetPreviewTargetMode(PreviewTargetMode.Player);
            effectItem.Click += (s, e) => SetPreviewTargetMode(PreviewTargetMode.Effect);
            previewModeMenu.Items.Add(characterItem);
            previewModeMenu.Items.Add(effectItem);
        }

        private void ConfigurePreviewTabButton(Button button, string text, int x, int width, PreviewTargetMode mode)
        {
            button.Text = text;
            button.SetBounds(x, 9, width, 36);
            button.Font = UiFont.Create(9.0f, FontStyle.Bold, GraphicsUnit.Point);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.ForeColor = lightText;
            button.Cursor = Cursors.Hand;
            button.Click += (s, e) => SetPreviewTargetMode(mode);
        }

        private void ConfigurePreviewModeEnum()
        {
            previewModeEnumComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            previewModeEnumComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            previewModeEnumComboBox.ItemHeight = 28;
            previewModeEnumComboBox.FlatStyle = FlatStyle.Flat;
            previewModeEnumComboBox.Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point);
            previewModeEnumComboBox.Items.Add(Loc.T("mode.default"));
            previewModeEnumComboBox.Items.Add(Loc.T("mode.effect"));
            previewModeEnumComboBox.Items.Add(Loc.T("mode.character"));
            previewModeEnumComboBox.Items.Add(Loc.T("mode.map"));
            previewModeEnumComboBox.SelectedIndex = 0;
            localizedBindings.Add(() => RelabelComboItems(previewModeEnumComboBox, "mode.default", "mode.effect", "mode.character", "mode.map"));
            previewModeEnumComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (applyingLanguage) return;
                PreviewTargetMode selected = previewModeEnumComboBox.SelectedIndex == 1
                    ? PreviewTargetMode.Effect
                    : previewModeEnumComboBox.SelectedIndex == 2
                        ? PreviewTargetMode.Player
                        : previewModeEnumComboBox.SelectedIndex == 3 ? PreviewTargetMode.Map : PreviewTargetMode.Standard;
                SetPreviewTargetMode(selected);
            };
            ApplyInputStyle(previewModeEnumComboBox);
        }

        private Panel BuildSimulationSettingsPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = darkPanel,
                Padding = new Padding(10, 12, 8, 4),
                Visible = false
            };
            ControlStyleHelper.EnableResizeRedraw(panel);
            panel.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
            };

            standardSettingsRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel
            };
            standardSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.start"), CreateInputHost(startCellBox, 76)));
            standardSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.end"), CreateInputHost(endCellBox, 76)));
            standardSettingsRow.Controls.Add(MakeLocalizedHint("hint.legacySingleFrame"));

            standardParameterRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel,
                Visible = false
            };
            Label fpsSharedHint = MakeSettingsHint("");
            BindText(fpsSharedHint, "hint.fpsShared");
            standardParameterRow.Controls.Add(fpsSharedHint);
            standardExtraItems = standardParameterRow.Controls.Cast<Control>().ToList();

            unifiedFpsGroup = CreateSettingsGroup(MakeLocalizedLabel("field.fps"), CreateInputHost(fpsBox, 76));

            backgroundPaletteComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            backgroundPaletteComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            backgroundPaletteComboBox.ItemHeight = 28;
            backgroundPaletteComboBox.FlatStyle = FlatStyle.Flat;
            backgroundPaletteComboBox.Size = new Size(160, 42);
            FillBackgroundPaletteItems();
            backgroundPaletteComboBox.DrawItem += ComboBox_DrawItem;
            ApplyInputStyle(backgroundPaletteComboBox);
            backgroundPaletteComboBox.SelectedIndexChanged += (s, e) =>
            {
                ApplyPreviewBackgroundPalette();
                CommitUndoableChange();
            };
            backgroundPaletteGroup = CreateSettingsGroup(MakeLocalizedLabel("field.background"),
                CreateComboHost(backgroundPaletteComboBox, 168));
            backgroundPaletteComboBox.SelectedIndex = 0;

            colorAdjustmentButton.Text = "";
            colorAdjustmentButton.Size = new Size(142, 42);
            colorAdjustmentButton.Margin = new Padding(0);
            ApplyButtonStyle(colorAdjustmentButton);
            colorAdjustmentButton.Paint += ColorAdjustmentButton_Paint;
            colorAdjustmentButton.Click += (s, e) => ShowColorAdjustmentDialog();
            BindAction(() => toolTip.SetToolTip(colorAdjustmentButton, Loc.T("tooltip.colorAll")));
            colorAdjustmentGroup = CreateSettingsGroup(MakeLocalizedLabel("field.color"), colorAdjustmentButton);
            basicSettingsHeader = CreateParameterSectionHeader("section.basic");
            processingSettingsHeader = CreateParameterSectionHeader("section.processing");
            characterStatusHeader = CreateParameterSectionHeader("section.character");
            effectStatusHeader = CreateParameterSectionHeader("section.effect");

            CreateLanguageSelector();

            ConfigureStateComboBox();
            ConfigureRangeInput(stateStartBox, 1, 999, 1);
            ConfigureRangeInput(stateEndBox, 1, 999, 1);

            BindText(stateEnabledCheckBox, "field.use");
            stateEnabledCheckBox.AutoSize = true;
            stateEnabledCheckBox.ForeColor = lightText;
            stateEnabledCheckBox.Margin = new Padding(6, 17, 8, 0);
            stateEnabledCheckBox.CheckedChanged += (s, e) => SaveSelectedStateEditor();
            stateStartBox.ValueChanged += (s, e) => SaveSelectedStateEditor();
            stateEndBox.ValueChanged += (s, e) => SaveSelectedStateEditor();
            ConfigureRangeInput(playerMoveSpeedBox, 0, 10, 1, 1);
            ConfigureRangeInput(playerJumpDistanceBox, 0.1m, 10, 1, 1);
            ConfigureRangeInput(playerGravityBox, 0.1m, 10, 1, 1);
            ConfigureRangeInput(playerGroundOffsetBox, GroundContact.MinOffset, GroundContact.MaxOffset, 0);
            playerMoveSpeedBox.ValueChanged += (s, e) => SavePlayerParameters();
            playerJumpDistanceBox.ValueChanged += (s, e) => SavePlayerParameters();
            playerGravityBox.ValueChanged += (s, e) => SavePlayerParameters();
            playerGroundOffsetBox.ValueChanged += (s, e) => { SavePlayerParameters(); UpdateAnimationPreview(); };
            ConfigureRangeInput(playerColliderWidthBox, 0, ColliderSize.MaxPixels, 0);
            ConfigureRangeInput(playerColliderHeightBox, 0, ColliderSize.MaxPixels, 0);
            // 0 を入れたら、画像に合わせた初期値へ戻す（0 のままだと、1 にした途端に極端に小さくなるため）。
            playerColliderWidthBox.ValueChanged += (s, e) => { EnsureColliderDefaults(); SavePlayerParameters(); UpdateAnimationPreview(); };
            playerColliderHeightBox.ValueChanged += (s, e) => { EnsureColliderDefaults(); SavePlayerParameters(); UpdateAnimationPreview(); };

            characterSettingsRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel,
                Visible = false
            };
            characterSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.state"), CreateComboHost(stateConfigComboBox, 112)));
            characterSettingsRow.Controls.Add(stateEnabledCheckBox);
            characterSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.start"), CreateInputHost(stateStartBox, 76)));
            characterSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.end"), CreateInputHost(stateEndBox, 76)));
            BindText(keySettingsButton, "button.keySettings");
            keySettingsButton.Size = new Size(88, 42);
            keySettingsButton.Margin = new Padding(8, 8, 2, 0);
            ApplyButtonStyle(keySettingsButton);
            keySettingsButton.Click += (s, e) => ShowKeySettingsDialog();
            characterSettingsRow.Controls.Add(keySettingsButton);

            characterParameterRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = true,   // コライダーの項目が増え、低い窓では下が切れるため縦にスクロールする
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel,
                Visible = false
            };
            characterParameterRow.Layout += (s, e) => HideStateTransitionHorizontalScroll(characterParameterRow);   // 折り返すので横スクロールは不要
            characterParameterRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.speed"), CreateInputHost(playerMoveSpeedBox, 76)));
            characterParameterRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.jump"), CreateInputHost(playerJumpDistanceBox, 76)));
            characterParameterRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.gravity"), CreateInputHost(playerGravityBox, 76)));
            playerGroundOffsetGroup = CreateSettingsGroup(MakeLocalizedLabel("field.groundOffset"), CreateInputHost(playerGroundOffsetBox, 76));
            characterParameterRow.Controls.Add(playerGroundOffsetGroup);

            // コライダー（当たり判定の箱）: 表示の切り替えと、大きさ（画像ピクセルの X=幅・Y=高さ）。
            BindText(playerColliderVisibleCheckBox, "check.showCollider");
            playerColliderVisibleCheckBox.AutoSize = true;
            playerColliderVisibleCheckBox.ForeColor = lightText;
            playerColliderVisibleCheckBox.BackColor = Color.Transparent;
            playerColliderVisibleCheckBox.Margin = new Padding(10, 12, 10, 0);
            playerColliderVisibleCheckBox.CheckedChanged += (s, e) =>
            {
                UpdateColliderInputsEnabled();
                UpdateAnimationPreview();
                CommitUndoableChange();
            };
            characterParameterRow.Controls.Add(playerColliderVisibleCheckBox);
            var colliderSizeGroup = playerColliderSizeGroup = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = darkPanel
            };
            playerColliderWidthLabel = MakeLocalizedLabel("field.colliderSizeX");
            playerColliderHeightLabel = MakeSettingsLabel("Y");
            playerColliderWidthHost = CreateInputHost(playerColliderWidthBox, 76);
            playerColliderHeightHost = CreateInputHost(playerColliderHeightBox, 76);
            colliderSizeGroup.Controls.Add(CreateSettingsGroup(playerColliderWidthLabel, playerColliderWidthHost));
            colliderSizeGroup.Controls.Add(CreateSettingsGroup(playerColliderHeightLabel, playerColliderHeightHost));
            characterParameterRow.Controls.Add(colliderSizeGroup);
            UpdateColliderInputsEnabled();
            // 長い訳でも切れないよう、欄の幅で折り返して高さを確保する。
            // 下の余白は Padding ではスクロール範囲に入らないため、最後の項目の下マージンで取る。
            Label colliderHint = MakeLocalizedHint("hint.colliderAuto");
            colliderHint.Margin = new Padding(colliderHint.Margin.Left, colliderHint.Margin.Top, 0, 24);
            Action fitColliderHint = () =>
            {
                var maximum = new Size(Math.Max(120, characterParameterRow.ClientSize.Width - characterParameterRow.Padding.Horizontal -
                    colliderHint.Margin.Horizontal - SystemInformation.VerticalScrollBarWidth), 0);
                if (colliderHint.MaximumSize == maximum) return;
                colliderHint.MaximumSize = maximum;
                characterParameterRow.PerformLayout();
            };
            characterParameterRow.SizeChanged += (s, e) => fitColliderHint();
            fitColliderHint();
            characterParameterRow.Controls.Add(colliderHint);
            BindText(mirrorMissingDirectionsCheckBox, "check.mirrorReverse");
            mirrorMissingDirectionsCheckBox.AutoSize = true;
            mirrorMissingDirectionsCheckBox.ForeColor = lightText;
            mirrorMissingDirectionsCheckBox.BackColor = Color.Transparent;
            mirrorMissingDirectionsCheckBox.Margin = new Padding(10, 19, 10, 0);
            mirrorMissingDirectionsCheckBox.CheckedChanged += (s, e) =>
            {
                UpdateAnimationPreview();
                CommitUndoableChange();
            };
            characterParameterRow.Controls.Add(mirrorMissingDirectionsCheckBox);
            characterStatusItems = characterParameterRow.Controls.Cast<Control>().ToList();
            characterStatusItems.Remove(mirrorMissingDirectionsCheckBox);
            characterStatusItems.Insert(0, mirrorMissingDirectionsCheckBox);
            characterStatusItems.Insert(1, terrainCheckBox);

            ConfigureRangeInput(effectStartBox, 1, 999, 1);
            ConfigureRangeInput(effectEndBox, 1, 999, 1);
            ConfigureRangeInput(effectDirectionXBox, -1, 1, 1, 2);
            ConfigureRangeInput(effectDirectionYBox, -1, 1, 0, 2);
            ConfigureRangeInput(effectSpeedBox, 0, 10, 1, 1);
            effectStartBox.ValueChanged += (s, e) => SaveEffectEditor();
            effectEndBox.ValueChanged += (s, e) => SaveEffectEditor();
            effectDirectionXBox.ValueChanged += (s, e) => SaveEffectEditor();
            effectDirectionYBox.ValueChanged += (s, e) => SaveEffectEditor();
            effectSpeedBox.ValueChanged += (s, e) => SaveEffectEditor();

            effectSettingsRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel,
                Visible = false
            };
            effectSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.start"), CreateInputHost(effectStartBox, 76)));
            Control effectEndGroup = CreateSettingsGroup(MakeLocalizedLabel("field.end"), CreateInputHost(effectEndBox, 76));
            effectSettingsRow.Controls.Add(effectEndGroup);
            effectSettingsRow.SetFlowBreak(effectEndGroup, true);   // 方向（X, Y）は次の行へ。Yだけが下に落ちないようにする
            effectSettingsRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.directionX"), CreateInputHost(effectDirectionXBox, 64)));
            effectSettingsRow.Controls.Add(CreateSettingsGroup(MakeSettingsLabel("Y"), CreateInputHost(effectDirectionYBox, 64)));

            effectParameterRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(2, 7, 0, 0),
                BackColor = darkPanel,
                Visible = false
            };
            effectParameterRow.Controls.Add(CreateSettingsGroup(MakeLocalizedLabel("field.speed"), CreateInputHost(effectSpeedBox, 76)));
            effectStatusItems = effectParameterRow.Controls.Cast<Control>().ToList();
            BindText(blackTransparencyCheckBox, "check.blackTransparency");
            blackTransparencyCheckBox.AutoSize = true;
            blackTransparencyCheckBox.ForeColor = lightText;
            blackTransparencyCheckBox.BackColor = Color.Transparent;
            blackTransparencyCheckBox.Margin = new Padding(10, 19, 10, 0);
            blackTransparencyRow = WithHelp(blackTransparencyCheckBox, "help.blackTransparency", false);
            blackTransparencyCooldownTimer.Tick += EndBlackTransparencyCooldown;
            blackTransparencyCheckBox.CheckedChanged += async (s, e) =>
            {
                if (restoringState) return;
                if (colorPreviewCancellation != null) colorPreviewCancellation.Cancel();
                int requestGeneration = ++colorPreviewGeneration;
                blackTransparencyCheckBox.Enabled = false;
                blackTransparencyCooldownTimer.Stop();
                List<LoadedFolder> prepared = null;
                BeginLoading();
                try
                {
                    if (GetAllItems().Count == 0)
                    {
                        UpdatePreviewSafe();
                    }
                    else
                    {
                        List<LoadFolderRequest> request = CaptureLoadRequest();
                        int divisor = GetScaleDivisor();
                        bool convertBlack = blackTransparencyCheckBox.Checked;
                        SpriteColorBlendMode blendMode = colorBlendMode;
                        Color adjustmentColor = spriteAdjustmentColor;
                        int adjustmentStrength = spriteAdjustmentStrength;
                        prepared = await Task.Run(() => imagePipeline.Load(request, divisor, convertBlack,
                            blendMode, adjustmentColor, adjustmentStrength, CancellationToken.None));
                        if (closing)
                        {
                            SpriteImagePipeline.DisposeLoaded(prepared);
                            prepared = null;
                            return;
                        }
                        if (requestGeneration != colorPreviewGeneration)
                        {
                            SpriteImagePipeline.DisposeLoaded(prepared);
                            prepared = null;
                            return;
                        }
                        List<LoadedFolder> applying = prepared;
                        prepared = null;
                        RunOnUiThread(() =>
                        {
                            UpdateSheetPreview(applying);
                            UpdateAnimationPreview();
                        });
                    }
                    CommitUndoableChange();
                }
                catch (Exception ex)
                {
                    if (prepared != null) SpriteImagePipeline.DisposeLoaded(prepared);
                    prepared = null;
                    if (!closing)
                    {
                        DisposeAnimationFrames();
                        sheetCanvas.SetImage(null, null);
                cellItems.Clear(); SyncMapAssets();
                        ResetSheetChips();
                        animCanvas.SetImage(null, null);
                        statusLabel.Text = Loc.T("status.previewError", ex.Message.Replace("\r", " ").Replace("\n", " "));
                    }
                }
                finally
                {
                    if (prepared != null) SpriteImagePipeline.DisposeLoaded(prepared);
                    // フォーム終了処理と競合すると、この継続がコントロール破棄後に
                    // 再開することがある。終了中ならローディング表示の後始末は不要。
                    if (!closing)
                    {
                        EndLoading();
                        blackTransparencyCooldownTimer.Start();
                    }
                }
            };
            effectParameterRow.Controls.Add(blackTransparencyRow);

            standardParameterRow.SizeChanged += (s, e) => UpdateParameterSectionHeaderWidths(standardParameterRow);
            characterParameterRow.SizeChanged += (s, e) => UpdateParameterSectionHeaderWidths(characterParameterRow);
            effectParameterRow.SizeChanged += (s, e) => UpdateParameterSectionHeaderWidths(effectParameterRow);

            panel.Controls.Add(standardSettingsRow);
            panel.Controls.Add(standardParameterRow);
            panel.Controls.Add(characterSettingsRow);
            panel.Controls.Add(characterParameterRow);
            panel.Controls.Add(effectSettingsRow);
            panel.Controls.Add(effectParameterRow);
            stateConfigComboBox.SelectedIndex = 0;
            LoadSelectedStateEditor();
            LoadEffectEditor();
            SyncUnifiedFps();
            return panel;
        }

        // 状態名の実測幅に合わせて列の右へのずらし量を決める（LINE Seedなど幅の広い
        // フォントでも、名前が切れたりチェックボックスに被ったりしないようにする）。
        private int transitionColumnShift;
        private int transitionCheckShift;
        private int transitionInputWidth = 64;
        private int transitionInputExtra;

        // シート/プレビューのうち、最後にクリックされた側（それ以外を最後にクリックしたらnull）。
        // Fキー（全体表示）はこれがある時だけ反応する。
        private PreviewCanvas lastClickedCanvas;
        private ClickTracker clickTracker;

        internal void NoteClickedControl(Control clicked)
        {
            lastClickedCanvas = clicked as PreviewCanvas;
        }

        private static bool IsTextInputFocused(Form form)
        {
            return form.ActiveControl is TextBoxBase;
        }

        private void ComputeTransitionColumnShift()
        {
            var rows = new[]
            {
                new KeyValuePair<string, int>(Loc.T("state.idle"), 0), new KeyValuePair<string, int>(Loc.T("state.moveRight"), 0),
                new KeyValuePair<string, int>(Loc.T("state.moveLeft"), 0), new KeyValuePair<string, int>(Loc.T("state.moveUp"), 0),
                new KeyValuePair<string, int>(Loc.T("state.moveDown"), 0), new KeyValuePair<string, int>(Loc.T("state.jumpStart"), 18),
                new KeyValuePair<string, int>(Loc.T("state.jumpAir"), 18), new KeyValuePair<string, int>(Loc.T("state.jumpLand"), 18),
                new KeyValuePair<string, int>(Loc.T("action.attack1"), 18), new KeyValuePair<string, int>(Loc.T("action.attack2"), 18)
            };
            int shift = 0;
            using (Font font = UiFont.Create(9.0f, FontStyle.Bold, GraphicsUnit.Point))
                foreach (var row in rows)
                    shift = Math.Max(shift, row.Value + TextRenderer.MeasureText(row.Key, font).Width + 6 - 82);
            transitionColumnShift = shift;
            // 「使用」チェックボックスの実幅（枠16+隙間6+文字+余白）が列間隔58pxを超える分。
            transitionCheckShift = Math.Max(0, TextRenderer.MeasureText(Loc.T("field.use"), Font).Width + 16 + 6 + 10 - 58);
            // 列の見出し「有効」も、開始の列までの幅（48px＋ずらした分）に収める。
            using (Font headerFont = UiFont.Create(8.0f, FontStyle.Regular, GraphicsUnit.Point))
                transitionCheckShift = Math.Max(transitionCheckShift, TextRenderer.MeasureText(Loc.T("field.enabled"), headerFont).Width + 6 - 48);
            // 開始・終了の入力欄は3桁が見える幅にし、列間隔（幅+6）と行の全幅に反映する。
            transitionInputWidth = Math.Max(64, RequiredInputWidth(new NumericUpDown { Minimum = 1, Maximum = 999 }));
            transitionInputExtra = 2 * (transitionInputWidth - 64);
        }

        private Panel BuildStateTransitionPage()
        {
            ComputeTransitionColumnShift();
            var page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = darkPanel,
                Padding = new Padding(10, 8, 8, 8),
                Visible = false
            };
            ControlStyleHelper.EnableResizeRedraw(page);
            page.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawLine(pen, 0, 0, page.Width, 0);
            };

            var modeHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = darkPanel,
                Padding = Padding.Empty
            };
            ControlStyleHelper.EnableResizeRedraw(modeHeader);
            modeHeader.Paint += (s, e) =>
            {
                using (var pen = new Pen(dividerColor))
                    e.Graphics.DrawLine(pen, 0, modeHeader.Height - 1, modeHeader.Width, modeHeader.Height - 1);
            };
            previewTitleLabel.Margin = new Padding(8, 14, 12, 0);
            var modeFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                AutoScroll = false,
                BackColor = darkPanel,
                Padding = Padding.Empty
            };
            modeFlow.Controls.Add(previewTitleLabel);
            modeFlow.Controls.Add(previewModeEnumHost);
            modeHeader.Controls.Add(modeFlow);

            standardSettingsRow.Visible = false;
            effectSettingsRow.Visible = false;
            page.Controls.Add(standardSettingsRow);
            page.Controls.Add(effectSettingsRow);

            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = darkPanel,
                Padding = new Padding(0, 2, 0, 8),
                Visible = false
            };
            playerTransitionList = list;
            list.HandleCreated += (s, e) => SetWindowTheme(list.Handle, "DarkMode_Explorer", null);
            list.SizeChanged += (s, e) => ResizeStateTransitionRows(list);
            list.Layout += (s, e) => HideStateTransitionHorizontalScroll(list);
            list.VisibleChanged += (s, e) => HideStateTransitionHorizontalScroll(list);

            PopulateStateTransitionList(list);
            localizedBindings.Add(RebuildStateTransitionList);
            localizedBindings.Add(ApplyResolutionAwareMinimumSize);
            localizedBindings.Add(RefreshLocalizedStatus);
            page.Controls.Add(list);
            page.Controls.Add(modeHeader);
            // Dockは背面のものから先に領域を確保する。見出し(Top)を最背面にして先に確保させ、
            // 下のFill領域（設定行・状態リスト）が見出しの裏に食い込まないようにする。
            modeHeader.SendToBack();
            RefreshStateTransitionEditors();
            return page;
        }

        // 状態の一覧（見出し行＋各状態の行）を作る。言語が変わったら RebuildStateTransitionList が作り直す。
        private void PopulateStateTransitionList(FlowLayoutPanel list)
        {
            list.Controls.Add(CreateTransitionColumnHeader());
            AddStateTransitionRow(list, PlayerAnimationState.Idle, Loc.T("state.idle"), 0);
            AddStateTransitionRow(list, PlayerAnimationState.MoveRight, Loc.T("state.moveRight"), 0);
            AddStateTransitionRow(list, PlayerAnimationState.MoveLeft, Loc.T("state.moveLeft"), 0);
            AddStateTransitionRow(list, PlayerAnimationState.MoveUp, Loc.T("state.moveUp"), 0);
            AddStateTransitionRow(list, PlayerAnimationState.MoveDown, Loc.T("state.moveDown"), 0);
            list.Controls.Add(CreateTransitionGroupHeader("group.jumpRight", Color.FromArgb(255, 205, 64), Loc.T("hint.jumpFallback")));
            AddStateTransitionRow(list, PlayerAnimationState.JumpRightStart, Loc.T("state.jumpStart"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.JumpRightAir, Loc.T("state.jumpAir"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.JumpRightLand, Loc.T("state.jumpLand"), 18);
            list.Controls.Add(CreateTransitionGroupHeader("group.jumpLeft", Color.FromArgb(255, 205, 64), Loc.T("hint.jumpFallback")));
            AddStateTransitionRow(list, PlayerAnimationState.JumpLeftStart, Loc.T("state.jumpStart"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.JumpLeftAir, Loc.T("state.jumpAir"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.JumpLeftLand, Loc.T("state.jumpLand"), 18);
            list.Controls.Add(CreateTransitionGroupHeader("group.attackRight", Color.FromArgb(70, 145, 255), null));
            AddStateTransitionRow(list, PlayerAnimationState.AttackRight1, Loc.T("action.attack1"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.AttackRight2, Loc.T("action.attack2"), 18);
            list.Controls.Add(CreateTransitionGroupHeader("group.attackLeft", Color.FromArgb(70, 145, 255), null));
            AddStateTransitionRow(list, PlayerAnimationState.AttackLeft1, Loc.T("action.attack1"), 18);
            AddStateTransitionRow(list, PlayerAnimationState.AttackLeft2, Loc.T("action.attack2"), 18);
            WireTransitionGroups(list);
        }

        // 見出しの後ろから次の見出しまでの行を、その見出しのグループとして開閉する。
        private void WireTransitionGroups(FlowLayoutPanel list)
        {
            FoldGroupHeader current = null;
            var rows = new Dictionary<FoldGroupHeader, List<Control>>();
            foreach (Control control in list.Controls)
            {
                var header = control as FoldGroupHeader;
                if (header != null) { current = header; rows[header] = new List<Control>(); continue; }
                if (current != null) rows[current].Add(control);
            }
            foreach (KeyValuePair<FoldGroupHeader, List<Control>> pair in rows)
            {
                FoldGroupHeader header = pair.Key; List<Control> members = pair.Value;
                foreach (Control row in members) transitionRowHeights[row] = row.Height;
                bool expanded = !collapsedTransitionGroups.Contains(header.Key);
                header.SetExpanded(expanded, false);
                DescribeTransitionGroup(header);
                foreach (Control row in members) row.Visible = expanded;
                header.Toggled += () => FoldTransitionGroup(list, header, members);
            }
        }

        private void DescribeTransitionGroup(FoldGroupHeader header)
        {
            header.AccessibleDescription = Loc.T(header.Expanded ? "access.groupExpanded" : "access.groupCollapsed");
        }

        // 行の高さを縮めて閉じる／伸ばして開く。行の中身は上から見えたまま、下側が隠れていく。
        private void FoldTransitionGroup(FlowLayoutPanel list, FoldGroupHeader header, List<Control> rows)
        {
            bool expand = header.Expanded;
            if (expand) collapsedTransitionGroups.Remove(header.Key); else collapsedTransitionGroups.Add(header.Key);
            DescribeTransitionGroup(header);
            Timer motion;
            bool midway = transitionFoldMotions.TryGetValue(header, out motion);
            if (midway) { UiMotion.Stop(ref motion); transitionFoldMotions.Remove(header); }
            // 閉じる行の中に入力中のものがあれば、見出しへフォーカスを移す。
            if (!expand && rows.Any(r => r.ContainsFocus)) header.Focus();
            var from = new Dictionary<Control, int>();
            foreach (Control row in rows)
            {
                int full; if (!transitionRowHeights.TryGetValue(row, out full)) transitionRowHeights[row] = full = row.Height;
                // 途中で押し直したときは今の高さから戻す。（Visible は親が隠れていると false になるので使わない）
                from[row] = midway ? row.Height : expand ? 0 : full;
                if (expand) { row.Height = from[row]; row.Visible = true; }
            }
            motion = UiMotion.Animate(180, t =>
            {
                list.SuspendLayout();
                foreach (Control row in rows)
                {
                    int full = transitionRowHeights[row];
                    int target = expand ? full : 0;
                    row.Height = Math.Max(0, (int)Math.Round(from[row] + (target - from[row]) * t));
                }
                list.ResumeLayout(true);
            }, () =>
            {
                transitionFoldMotions.Remove(header);
                foreach (Control row in rows)
                {
                    row.Height = transitionRowHeights[row];
                    row.Visible = expand;
                }
                HideStateTransitionHorizontalScroll(list);
            });
            if (motion != null) transitionFoldMotions[header] = motion;
        }

        // 言語変更時: 列の位置は文言の実測幅で決まるので、一覧を作り直す。値は playerClips に保持されているので失われない。
        private void RebuildStateTransitionList()
        {
            FlowLayoutPanel list = playerTransitionList as FlowLayoutPanel;
            if (list == null || list.IsDisposed) return;
            CancelInlineBindingCapture();
            list.SuspendLayout();
            foreach (Control old in list.Controls.Cast<Control>().ToList())
            {
                list.Controls.Remove(old);
                old.Dispose();
            }
            stateRangeEditors.Clear();
            inlineBindingButtons.Clear();
            foreach (Timer running in transitionFoldMotions.Values.ToList()) { Timer stop = running; UiMotion.Stop(ref stop); }
            transitionFoldMotions.Clear();
            transitionRowHeights.Clear();
            ComputeTransitionColumnShift();
            PopulateStateTransitionList(list);
            RefreshStateTransitionEditors();
            list.ResumeLayout(true);
            ResizeStateTransitionRows(list);
        }

        // hintText は見出しの右に出す補足（null なら出さない。未設定の段階の代用はジャンプだけの仕組み）。
        // 見出しを押すと、そのグループの行を折り畳む（WireTransitionGroups）。
        private Panel CreateTransitionGroupHeader(string textKey, Color color, string hintText)
        {
            string text = Loc.T(textKey);
            var row = new FoldGroupHeader { Key = textKey, AccessibleName = text, Height = 38, Width = 400 + transitionColumnShift + transitionCheckShift + transitionInputExtra, Margin = new Padding(0, 8, 0, 0), BackColor = darkPanel, HoverColor = panelElevated, ChevronColor = mutedText, ChevronHoverColor = lightText };
            var marker = new Panel { BackColor = color, Location = new Point(7, 9), Size = new Size(3, 20) };
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(18, 9),
                ForeColor = lightText,
                Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point)
            };
            var hint = new Label
            {
                Text = hintText ?? "",
                AutoEllipsis = true,
                Location = new Point(145, 10),
                Size = new Size(250, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = mutedText,
                Font = UiFont.Create(8.0f, FontStyle.Regular, GraphicsUnit.Point)
            };
            // 見出しの実測幅の右から注記を始める（幅の広いフォントでも被らない）。
            // 入りきらない分は省略記号にする。右端は開閉の矢印のために空ける。
            int hintX = label.Left + label.GetPreferredSize(Size.Empty).Width + 12;
            int chevronSpace = ScaleDpi(34);
            hint.Location = new Point(hintX, 10);
            hint.Size = new Size(Math.Max(40, row.Width - hintX - chevronSpace), 22);
            row.Controls.Add(marker);
            row.Controls.Add(label);
            if (hintText != null) row.Controls.Add(hint);
            row.AdoptChildren();
            return row;
        }

        private Panel CreateTransitionColumnHeader()
        {
            var row = new Panel { Height = 26, Width = 400 + transitionColumnShift + transitionCheckShift + transitionInputExtra, Margin = Padding.Empty, BackColor = darkPanel };
            row.Controls.Add(MakeTransitionColumnLabel(Loc.T("field.state"), 8, 82 + transitionColumnShift));
            row.Controls.Add(MakeTransitionColumnLabel(Loc.T("field.enabled"), 98 + transitionColumnShift, 48 + transitionCheckShift));
            row.Controls.Add(MakeTransitionColumnLabel(Loc.T("field.start"), 150 + transitionColumnShift + transitionCheckShift, transitionInputWidth));
            row.Controls.Add(MakeTransitionColumnLabel(Loc.T("field.end"), 150 + transitionColumnShift + transitionCheckShift + transitionInputWidth + 6, transitionInputWidth));
            row.Controls.Add(MakeTransitionColumnLabel(Loc.T("field.key"), 150 + transitionColumnShift + transitionCheckShift + 2 * (transitionInputWidth + 6), 96));
            return row;
        }

        private Label MakeTransitionColumnLabel(string text, int x, int width)
        {
            return new Label
            {
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(x, 2),
                Size = new Size(width, 22),
                ForeColor = mutedText,
                Font = UiFont.Create(8.0f, FontStyle.Regular, GraphicsUnit.Point)
            };
        }

        private void AddStateTransitionRow(FlowLayoutPanel list, PlayerAnimationState state, string text, int indent)
        {
            var row = new Panel
            {
                Height = 54,
                Width = 400 + transitionColumnShift + transitionCheckShift + transitionInputExtra,
                Margin = new Padding(0, 0, 0, 2),
                BackColor = Color.FromArgb(27, 36, 43),
                Tag = "state-transition-row"
            };
            Font nameFont = UiFont.Create(9.0f, FontStyle.Bold, GraphicsUnit.Point);
            // 高さは1行ぶん以上にする。省略記号つきのラベルは、高さに収まらない行を描かないため、
            // 表示倍率が高い環境（125%など）で24px固定だと状態名が丸ごと消えていた。
            int nameHeight = Math.Max(24, TextRenderer.MeasureText("Ag", nameFont).Height + 2);
            var name = new Label
            {
                Text = text,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(8 + indent, 17),
                Size = new Size(Math.Max(60, 82 + transitionColumnShift - indent), nameHeight),
                ForeColor = lightText,
                Font = nameFont
            };
            var enabled = new RoundedCheckBox
            {
                Text = Loc.T("field.use"),
                AutoSize = true,
                BoxSizeDip = 16,
                GapDip = 6,
                Location = new Point(92 + transitionColumnShift, 17),
                ForeColor = lightText,
                BackColor = Color.Transparent
            };
            var start = new NumericUpDown();
            var end = new NumericUpDown();
            ConfigureRangeInput(start, 1, 999, 1);
            ConfigureRangeInput(end, 1, 999, 1);
            Panel startHost = CreateInputHost(start, 64);
            Panel endHost = CreateInputHost(end, 64);
            startHost.SetBounds(150 + transitionColumnShift + transitionCheckShift, 6, transitionInputWidth, 42);
            endHost.SetBounds(150 + transitionColumnShift + transitionCheckShift + transitionInputWidth + 6, 6, transitionInputWidth, 42);
            startHost.Margin = endHost.Margin = Padding.Empty;
            row.Controls.Add(name);
            row.Controls.Add(enabled);
            // 「使用」の文字と縦の中心をそろえる（チェックボックスの高さは表示時の自動サイズで決まる）。
            EventHandler centerName = (s, e) => name.Top = Math.Max(0, enabled.Top + (enabled.Height - name.Height) / 2);
            enabled.SizeChanged += centerName;
            centerName(null, EventArgs.Empty);
            row.Controls.Add(startHost);
            row.Controls.Add(endHost);
            PreviewAction? action = GetStatePreviewAction(state);
            Button bindingButton = null;
            if (action.HasValue)
            {
                bindingButton = new Button
                {
                    Text = FormatKey(previewInput.GetBinding(action.Value)),
                    Location = new Point(150 + transitionColumnShift + transitionCheckShift + 2 * (transitionInputWidth + 6), 7),
                    Size = new Size(96, 40),
                    Tag = action.Value,
                    Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point)
                };
                ApplyButtonStyle(bindingButton);
                bindingButton.Click += (s, e) => BeginInlineBindingCapture((PreviewAction)((Button)s).Tag, (Button)s);
                row.Controls.Add(bindingButton);
                List<Button> actionButtons;
                if (!inlineBindingButtons.TryGetValue(action.Value, out actionButtons))
                {
                    actionButtons = new List<Button>();
                    inlineBindingButtons[action.Value] = actionButtons;
                }
                actionButtons.Add(bindingButton);
            }

            var controls = new StateRangeEditorControls
            {
                Enabled = enabled,
                Start = start,
                End = end,
                Binding = bindingButton,
                Row = row
            };
            stateRangeEditors[state] = controls;
            enabled.CheckedChanged += (s, e) => SaveStateRangeEditor(state);
            start.ValueChanged += (s, e) => SaveStateRangeEditor(state);
            end.ValueChanged += (s, e) => SaveStateRangeEditor(state);
            list.Controls.Add(row);
        }

        private static PreviewAction? GetStatePreviewAction(PlayerAnimationState state)
        {
            switch (state)
            {
                case PlayerAnimationState.MoveRight: return PreviewAction.MoveRight;
                case PlayerAnimationState.MoveLeft: return PreviewAction.MoveLeft;
                case PlayerAnimationState.MoveUp: return PreviewAction.MoveUp;
                case PlayerAnimationState.MoveDown: return PreviewAction.MoveDown;
                case PlayerAnimationState.JumpRightStart:
                case PlayerAnimationState.JumpRightAir:
                case PlayerAnimationState.JumpRightLand:
                case PlayerAnimationState.JumpLeftStart:
                case PlayerAnimationState.JumpLeftAir:
                case PlayerAnimationState.JumpLeftLand:
                    return PreviewAction.Jump;
                case PlayerAnimationState.AttackRight1:
                case PlayerAnimationState.AttackLeft1:
                    return PreviewAction.Attack1;
                case PlayerAnimationState.AttackRight2:
                case PlayerAnimationState.AttackLeft2:
                    return PreviewAction.Attack2;
                default: return null;
            }
        }

        private static void ResizeStateTransitionRows(FlowLayoutPanel list)
        {
            int width = Math.Max(390, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control control in list.Controls) control.Width = width;
            list.HorizontalScroll.Value = 0;
            HideStateTransitionHorizontalScroll(list);
        }

        private static void HideStateTransitionHorizontalScroll(FlowLayoutPanel list)
        {
            if (list == null || list.IsDisposed || !list.IsHandleCreated) return;
            ShowScrollBar(list.Handle, 0, false);
        }

        private void RefreshStateTransitionEditors()
        {
            if (stateRangeEditors.Count == 0) return;
            const int maxCell = 999;
            syncingStateEditor = true;
            foreach (KeyValuePair<PlayerAnimationState, StateRangeEditorControls> pair in stateRangeEditors)
            {
                AnimationClipSettings clip = playerClips[pair.Key];
                pair.Value.Start.Maximum = maxCell;
                pair.Value.End.Maximum = maxCell;
                pair.Value.Enabled.Checked = clip.Enabled;
                pair.Value.Start.Value = ClampDecimal(clip.StartCell, pair.Value.Start.Minimum, pair.Value.Start.Maximum);
                pair.Value.End.Value = ClampDecimal(clip.EndCell, pair.Value.End.Minimum, pair.Value.End.Maximum);
            }
            syncingStateEditor = false;
        }

        private void SaveStateRangeEditor(PlayerAnimationState state)
        {
            if (syncingStateEditor) return;
            StateRangeEditorControls editor;
            if (!stateRangeEditors.TryGetValue(state, out editor)) return;
            AnimationClipSettings clip = playerClips[state];
            clip.Enabled = editor.Enabled.Checked;
            clip.StartCell = (int)editor.Start.Value;
            clip.EndCell = Math.Max(clip.StartCell, (int)editor.End.Value);
            clip.Fps = Math.Max(1, (int)fpsBox.Value);
            if (editor.End.Value != clip.EndCell)
            {
                syncingStateEditor = true;
                editor.End.Value = clip.EndCell;
                syncingStateEditor = false;
            }
            playerAnimator.Reset(playerState.State, Math.Max(1, animationFrames.Count));
            UpdateSheetRangeColors();
            UpdateAnimationPreview();
            CommitUndoableChange();
        }

        private void AdjustSimulationSettingsHeight()
        {
            if (simulationSettingsPanel == null || simulationSettingsPanel.IsDisposed) return;
            simulationSettingsPanel.Invalidate();
        }

        private void ConfigureStateComboBox()
        {
            stateConfigComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            stateConfigComboBox.DrawMode = DrawMode.OwnerDrawFixed;
            stateConfigComboBox.ItemHeight = 28;
            stateConfigComboBox.FlatStyle = FlatStyle.Flat;
            stateConfigComboBox.Size = new Size(112, 42);
            stateConfigComboBox.DrawItem += ComboBox_DrawItem;
            foreach (PlayerAnimationState state in Enum.GetValues(typeof(PlayerAnimationState)))
                stateConfigComboBox.Items.Add(GetStateDisplayName(state));
            stateConfigComboBox.SelectedIndexChanged += (s, e) => { if (!applyingLanguage) LoadSelectedStateEditor(); };
            localizedBindings.Add(() =>
            {
                int keep = stateConfigComboBox.SelectedIndex;
                var all = (PlayerAnimationState[])Enum.GetValues(typeof(PlayerAnimationState));
                for (int i = 0; i < all.Length && i < stateConfigComboBox.Items.Count; i++)
                    stateConfigComboBox.Items[i] = GetStateDisplayName(all[i]);
                stateConfigComboBox.SelectedIndex = keep;
            });
            ApplyInputStyle(stateConfigComboBox);
        }

        private void ConfigureRangeInput(NumericUpDown input, decimal minimum, decimal maximum,
            decimal value, int decimalPlaces = 0)
        {
            input.Minimum = minimum;
            input.Maximum = maximum;
            input.DecimalPlaces = decimalPlaces;
            input.Increment = decimalPlaces > 0 ? 0.1m : 1m;
            input.Value = Math.Max(minimum, Math.Min(maximum, value));
            input.AutoSize = false;
            input.Size = new Size(68, 42);
            ApplyInputStyle(input);
        }

        private Label MakeSettingsLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Margin = new Padding(5, 16, 2, 0)
            };
        }

        private Label MakeSettingsHint(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = mutedText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(8, 17, 0, 0)
            };
        }

        private FlowLayoutPanel CreateSettingsGroup(Label label, Control inputHost)
        {
            var group = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = darkPanel
            };
            inputHost.Margin = new Padding(3, 6, 5, 0);
            group.Controls.Add(label);
            group.Controls.Add(inputHost);
            return group;
        }

        private Control CreateParameterSectionHeader(string textKey)
        {
            var header = new Panel
            {
                Height = 32,
                Width = 360,
                Margin = new Padding(2, 4, 2, 2),
                BackColor = Color.Transparent,
                Tag = "parameter-section"
            };
            ControlStyleHelper.EnableResizeRedraw(header);
            // 見出し文字の幅は言語・フォントで変わるため、文字の右端から罫線を引く。
            var label = new Label
            {
                AutoSize = true,
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(9.5f, FontStyle.Bold, GraphicsUnit.Point)
            };
            BindText(label, textKey);
            Action place = () =>
            {
                label.Location = new Point(12, Math.Max(0, (header.Height - label.Height) / 2));
                header.Invalidate();
            };
            label.SizeChanged += (s, e) => place();
            header.Paint += (s, e) =>
            {
                using (var accent = new SolidBrush(accentColor))
                    e.Graphics.FillRectangle(accent, 0, 6, 3, Math.Max(1, header.Height - 12));
                int lineStart = label.Right + 10;
                using (var line = new Pen(dividerColor))
                    e.Graphics.DrawLine(line, lineStart, header.Height / 2, Math.Max(lineStart, header.Width - 4), header.Height / 2);
            };
            header.Controls.Add(label);
            place();
            return header;
        }

        private static void UpdateParameterSectionHeaderWidths(FlowLayoutPanel row)
        {
            if (row == null || row.IsDisposed) return;
            int width = Math.Max(220, row.ClientSize.Width - row.Padding.Horizontal - 16);
            foreach (Control control in row.Controls)
            {
                if (control.Tag as string == "parameter-section") control.Width = width;
            }
        }

        private PlayerAnimationState SelectedEditorState
        {
            get
            {
                int index = Math.Max(0, stateConfigComboBox.SelectedIndex);
                return (PlayerAnimationState)Math.Min(index, Enum.GetValues(typeof(PlayerAnimationState)).Length - 1);
            }
        }

        private void LoadSelectedStateEditor()
        {
            if (stateConfigComboBox.SelectedIndex < 0) return;
            AnimationClipSettings clip = playerClips[SelectedEditorState];
            syncingStateEditor = true;
            stateEnabledCheckBox.Checked = clip.Enabled;
            stateStartBox.Value = ClampDecimal(clip.StartCell, stateStartBox.Minimum, stateStartBox.Maximum);
            stateEndBox.Value = ClampDecimal(clip.EndCell, stateEndBox.Minimum, stateEndBox.Maximum);
            syncingStateEditor = false;
        }

        private void SaveSelectedStateEditor()
        {
            if (syncingStateEditor || stateConfigComboBox.SelectedIndex < 0) return;
            AnimationClipSettings clip = playerClips[SelectedEditorState];
            clip.Enabled = stateEnabledCheckBox.Checked;
            clip.StartCell = (int)stateStartBox.Value;
            clip.EndCell = Math.Max(clip.StartCell, (int)stateEndBox.Value);
            clip.Fps = Math.Max(1, (int)fpsBox.Value);
            if (stateEndBox.Value != clip.EndCell)
            {
                syncingStateEditor = true;
                stateEndBox.Value = clip.EndCell;
                syncingStateEditor = false;
            }
            playerAnimator.Reset(playerState.State, Math.Max(1, animationFrames.Count));
            UpdateSheetRangeColors();
            UpdateAnimationPreview();
            CommitUndoableChange();
        }

        private void LoadEffectEditor()
        {
            syncingStateEditor = true;
            effectStartBox.Value = ClampDecimal(effectClip.StartCell, effectStartBox.Minimum, effectStartBox.Maximum);
            effectEndBox.Value = ClampDecimal(effectClip.EndCell, effectEndBox.Minimum, effectEndBox.Maximum);
            syncingStateEditor = false;
        }

        private void SaveEffectEditor()
        {
            if (syncingStateEditor) return;
            effectClip.StartCell = (int)effectStartBox.Value;
            effectClip.EndCell = Math.Max(effectClip.StartCell, (int)effectEndBox.Value);
            effectClip.Fps = Math.Max(1, (int)fpsBox.Value);
            if (effectEndBox.Value != effectClip.EndCell)
            {
                syncingStateEditor = true;
                effectEndBox.Value = effectClip.EndCell;
                syncingStateEditor = false;
            }
            ResetEffectPlayback();
            UpdateSheetRangeColors();
            UpdateAnimationPreview();
            CommitUndoableChange();
        }

        private void SavePlayerParameters()
        {
            if (syncingStateEditor) return;
            CommitUndoableChange();
        }

        private void SyncUnifiedFps()
        {
            int fps = Math.Max(1, (int)fpsBox.Value);
            foreach (AnimationClipSettings clip in playerClips.Values) clip.Fps = fps;
            effectClip.Fps = fps;
            if (playerAnimator != null) playerAnimator.Reset(playerState.State, GetMaxCellNumber());
            ResetEffectPlayback();
            UpdateAnimationPreview();
        }

        private void ApplyPreviewBackgroundPalette()
        {
            Color background;
            Color checkerA;
            Color checkerB;
            switch (backgroundPaletteComboBox.SelectedIndex)
            {
                case 1:
                    background = Color.FromArgb(18, 26, 33);
                    checkerA = Color.FromArgb(42, 54, 63);
                    checkerB = Color.FromArgb(55, 69, 78);
                    break;
                case 2:
                    background = Color.FromArgb(29, 26, 22);
                    checkerA = Color.FromArgb(61, 56, 49);
                    checkerB = Color.FromArgb(75, 69, 60);
                    break;
                default:
                    background = Color.FromArgb(17, 23, 28);
                    checkerA = Color.FromArgb(47, 47, 47);
                    checkerB = Color.FromArgb(59, 59, 59);
                    break;
            }
            sheetCanvas.SetBackgroundPalette(background, checkerA, checkerB);
            animCanvas.SetBackgroundPalette(background, checkerA, checkerB);
            if (mapCanvas != null) { mapCanvas.SetBackgroundPalette(background, checkerA, checkerB); mapPalette.SetBackgroundPalette(background, checkerA, checkerB); }
        }

        // 起動・プロジェクトの切り替え・リセットのあと、Fキーと同じ全体表示へ戻す。
        private void ResetViewsToFit()
        {
            if (previewTargetMode == PreviewTargetMode.Map && mapUiReady) { mapCanvas.Fit(); mapPalette.Fit(true); }
            sheetCanvas.ResetToFitAnimated();
            animCanvas.ResetToFitAnimated();
        }

        private void SetPreviewTargetMode(PreviewTargetMode mode)
        {
            if (previewTargetMode == mode) return;
            EndAssignMode();
            CrossfadeOverlay contentCover = CoverContent();
            // 状態遷移・設定のページは種類ごとに中身が変わるので、選択欄の並び（デフォルト・エフェクト・キャラクター）の向きへ動かす。
            PageTransitionOverlay cover = previewWorkspacePage != PreviewWorkspacePage.Preview ? CoverLeftWorkspace() : null;
            int previousModeOrder = PreviewModeOrder(previewTargetMode);
            previewTargetMode = mode;
            if (mode == PreviewTargetMode.Map && mapUiReady) { SyncMapAssets(); mapCanvas.Fit(); mapPalette.Fit(true); }
            lastClickedCanvas = animCanvas;
            previewInput.Clear();
            animationTimer.Stop();
            if (mode == PreviewTargetMode.Standard) animationIndex = GetFirstPlayableFrameIndex();
            ApplyPreviewTargetModeUi();
            if (cover != null)
                StartLeftWorkspaceTransition(cover, PreviewModeOrder(mode) > previousModeOrder
                    ? PageTransitionKind.SlideFromRight : PageTransitionKind.SlideFromLeft);
            animCanvas.ResetToFitAnimated();
            ResetSimulation();
            UpdateSheetRangeColors();
            UpdateAnimationPreview();
            StartPlayerPreviewIfPossible();
            StartContentCrossfade(contentCover);
            TryBeginInvoke(() => { if (!animCanvas.IsDisposed) animCanvas.Focus(); });
            CommitUndoableChange();
        }

        private void SetSettingsSection(bool parameters)
        {
            parameterSettingsSelected = parameters;
            SetPreviewWorkspacePage(parameters ? PreviewWorkspacePage.Parameters : PreviewWorkspacePage.StateTransitions);
        }

        private void SetPreviewWorkspacePage(PreviewWorkspacePage page)
        {
            EndAssignMode();
            PreviewWorkspacePage previousPage = previewWorkspacePage;
            // 先に今の画面の絵で覆ってから切り替える。新しいページの入力欄などが覆いより先に
            // 画面へ出て、一瞬ちらついて見えるのを防ぐ。
            PageTransitionOverlay cover = previousPage != page ? CoverLeftWorkspace() : null;
            try
            {
                SwitchPreviewWorkspacePage(page);
            }
            catch
            {
                if (cover != null) cover.Finish();
                throw;
            }
            // タブの並び（フォルダ・状態遷移・設定）に合わせて、右のタブへは右から、左のタブへは左から入れる。
            if (cover != null)
                StartLeftWorkspaceTransition(cover, (int)page > (int)previousPage
                    ? PageTransitionKind.SlideFromRight : PageTransitionKind.SlideFromLeft);
            // ページの切り替え（重い処理）が終わってから時間を数え始め、ページの横移動と同時に滑らせる。
            workspaceTabBar.SelectTab((int)page, cover != null);
        }

        private const int PageSlideMilliseconds = 300;
        private const int ContentFadeMilliseconds = 200;
        private CrossfadeOverlay contentTransition;

        // 中央（シート）と右（プレビュー）の今の見た目で覆う（見た目は変わらない）。演出できなければ null。
        private CrossfadeOverlay CoverContent()
        {
            if (contentTransition != null) contentTransition.Finish();
            contentTransition = null;
            Control host = mainSplit == null ? null : mainSplit.Panel2;
            if (host == null || !host.IsHandleCreated || !host.Visible || host.ClientSize.Width <= 0 || host.ClientSize.Height <= 0 ||
                WindowState == FormWindowState.Minimized || !SystemInformation.UIEffectsEnabled || Opacity < 1)
                return null;
            var overlay = new CrossfadeOverlay(CaptureContent()) { Dock = DockStyle.Fill, BackColor = workspaceBack };
            contentTransition = overlay;
            overlay.Disposed += (s, e) => { if (contentTransition == overlay) contentTransition = null; };
            host.Controls.Add(overlay);
            overlay.BringToFront();
            overlay.Update();
            return overlay;
        }

        // 覆いの下で切り替えた中身の絵を撮り、溶け込ませ始める。
        private void StartContentCrossfade(CrossfadeOverlay overlay)
        {
            if (overlay == null || overlay.IsDisposed) return;
            Control host = mainSplit.Panel2;
            host.PerformLayout();
            if (overlay.Size != host.ClientSize) { overlay.Finish(); return; }
            overlay.Begin(CaptureContent(), ContentFadeMilliseconds);
        }

        // 覆いを除いた中央・右の中身を画像にする（部品を奥から順に描く）。
        private Bitmap CaptureContent()
        {
            Control host = mainSplit.Panel2;
            var image = new Bitmap(Math.Max(1, host.ClientSize.Width), Math.Max(1, host.ClientSize.Height), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(workspaceBack);
                for (int i = host.Controls.Count - 1; i >= 0; i--)
                {
                    Control part = host.Controls[i];
                    if (!part.Visible || part is CrossfadeOverlay || part.Width <= 0 || part.Height <= 0) continue;
                    using (var bitmap = new Bitmap(part.Width, part.Height, PixelFormat.Format32bppArgb))
                    {
                        part.DrawToBitmap(bitmap, new Rectangle(0, 0, part.Width, part.Height));
                        g.DrawImageUnscaled(bitmap, part.Left, part.Top);
                    }
                }
            }
            return image;
        }

        // 元に戻す／やり直しの間は、作業領域（左のペイン・シート・プレビュー）の描き直しを止め、
        // 書き換えが終わってから全体を一度に描き直す。部品が1つずつ描かれる様子（がたつき）を見せない。
        // 以前は前の絵を別の窓で重ねて薄くしていたが、一瞬白く光って目に悪いため、明るさが変わる演出はしない（2026-10-06 ユーザー指示）。
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
        private bool workspaceFrozen;
        private void FreezeWorkspace()
        {
            if (workspaceFrozen || mainSplit == null || !mainSplit.IsHandleCreated || !mainSplit.Visible) return;
            SendMessage(mainSplit.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            workspaceFrozen = true;
        }
        private void ThawWorkspace()
        {
            if (!workspaceFrozen) return;
            workspaceFrozen = false;
            mainSplit.PerformLayout();
            SendMessage(mainSplit.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            RedrawWindow(mainSplit.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        // 起動時は透明にしておき、最初の配置やプロジェクトの読み込みが終わって手が空いたところで、全体を一度にフェードインする。
        // 部品が1つずつ描かれていく様子（描画のムラ）を見せないため。
        private const int RevealMilliseconds = 220;
        private Timer revealMotion;
        private void RevealWhenIdle()
        {
            if (Opacity >= 1) return;
            bool revealed = false;
            EventHandler idle = null;
            var fallback = new Timer { Interval = 1500 };
            Action reveal = () =>
            {
                if (revealed) return;
                revealed = true;
                Application.Idle -= idle;
                fallback.Stop(); fallback.Dispose();
                if (IsDisposed) return;
                Refresh();
                revealMotion = UiMotion.Animate(RevealMilliseconds, t => { if (!IsDisposed) Opacity = t; }, () => { if (!IsDisposed) Opacity = 1; });
            };
            idle = (s, e) => reveal();
            Application.Idle += idle;
            fallback.Tick += (s, e) => reveal();
            fallback.Start();
        }
        private PageTransitionOverlay leftWorkspaceTransition;

        // 左ペインの今の見た目を画像にする。演出できない状態（表示前・最小化中・Windowsのアニメーション効果がオフ）なら null。
        private Bitmap CaptureLeftWorkspace()
        {
            if (leftWorkspaceTransition != null) leftWorkspaceTransition.Finish();   // 連続で切り替えたら前の演出は打ち切る
            leftWorkspaceTransition = null;
            if (leftWorkspaceHost == null || !leftWorkspaceHost.IsHandleCreated || !leftWorkspaceHost.Visible ||
                leftWorkspaceHost.Width <= 0 || leftWorkspaceHost.Height <= 0 || WindowState == FormWindowState.Minimized ||
                !SystemInformation.UIEffectsEnabled)
                return null;
            var image = new Bitmap(leftWorkspaceHost.Width, leftWorkspaceHost.Height, PixelFormat.Format32bppArgb);
            leftWorkspaceHost.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            return image;
        }

        // 今の左ペインの絵で覆う（見た目は変わらない）。この下でページを切り替える。演出できなければ null。
        private PageTransitionOverlay CoverLeftWorkspace()
        {
            Bitmap before = CaptureLeftWorkspace();
            if (before == null) return null;
            var overlay = new PageTransitionOverlay(before)
            {
                Dock = DockStyle.Fill,
                BackColor = darkPanel
            };
            leftWorkspaceTransition = overlay;
            overlay.Disposed += (s, e) => { if (leftWorkspaceTransition == overlay) leftWorkspaceTransition = null; };
            leftWorkspaceHost.Controls.Add(overlay);
            overlay.BringToFront();
            overlay.Update();   // 切り替えの前に、覆いを確実に画面へ出しておく
            return overlay;
        }

        // 覆いの下で切り替えたページの絵を撮り、横移動を始める。
        private void StartLeftWorkspaceTransition(PageTransitionOverlay overlay, PageTransitionKind kind)
        {
            if (overlay.IsDisposed) return;
            if (overlay.Size != leftWorkspaceHost.ClientSize)
            {
                overlay.Finish();
                return;
            }
            leftWorkspaceHost.PerformLayout();
            overlay.Begin(CaptureLeftWorkspacePages(), kind, PageSlideMilliseconds);
        }

        // 覆いを除いた左ペインの中身（表示中のページ）を画像にする。
        // 親ごと DrawToBitmap すると重なり順が逆に描かれて覆いが混ざるため、ページを1つずつ描く。
        private Bitmap CaptureLeftWorkspacePages()
        {
            var image = new Bitmap(leftWorkspaceHost.ClientSize.Width, leftWorkspaceHost.ClientSize.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(darkPanel);
                for (int i = leftWorkspaceHost.Controls.Count - 1; i >= 0; i--)   // 奥から手前へ
                {
                    Control page = leftWorkspaceHost.Controls[i];
                    if (!page.Visible || page is PageTransitionOverlay || page.Width <= 0 || page.Height <= 0) continue;
                    using (var part = new Bitmap(page.Width, page.Height, PixelFormat.Format32bppArgb))
                    {
                        page.DrawToBitmap(part, new Rectangle(0, 0, part.Width, part.Height));
                        g.DrawImageUnscaled(part, page.Left, page.Top);
                    }
                }
            }
            return image;
        }

        private void SwitchPreviewWorkspacePage(PreviewWorkspacePage page)
        {
            previewWorkspacePage = page;
            parameterSettingsSelected = page == PreviewWorkspacePage.Parameters;
            // ページの表示切替を途中経過なしで一度に描画する（ちらつき防止）。
            bool freeze = leftWorkspaceHost != null && leftWorkspaceHost.IsHandleCreated;
            if (freeze) SendMessage(leftWorkspaceHost.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                ApplyPreviewTargetModeUi();
                AdjustLeftWorkspaceWidth();
            }
            finally
            {
                if (freeze)
                {
                    SendMessage(leftWorkspaceHost.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    leftWorkspaceHost.Invalidate(true);
                }
            }
        }

        private void ApplyPreviewTargetModeUi()
        {
            PreviewTargetMode mode = previewTargetMode;
            bool previewPage = previewWorkspacePage == PreviewWorkspacePage.Preview;
            bool transitionsPage = previewWorkspacePage == PreviewWorkspacePage.StateTransitions;
            bool parametersPage = previewWorkspacePage == PreviewWorkspacePage.Parameters;
            animCanvas.Visible = true;
            if (playbackBarPanel != null) playbackBarPanel.Visible = mode != PreviewTargetMode.Player;
            if (folderWorkspacePage != null) folderWorkspacePage.Visible = previewPage;
            if (stateTransitionPage != null) stateTransitionPage.Visible = transitionsPage;
            if (simulationSettingsPanel != null) simulationSettingsPanel.Visible = parametersPage;
            if (standardSettingsRow != null) standardSettingsRow.Visible = transitionsPage && mode == PreviewTargetMode.Standard;
            if (characterSettingsRow != null) characterSettingsRow.Visible = false;
            if (playerTransitionList != null) playerTransitionList.Visible = transitionsPage && mode == PreviewTargetMode.Player;
            if (effectSettingsRow != null) effectSettingsRow.Visible = transitionsPage && mode == PreviewTargetMode.Effect;
            if (standardParameterRow != null) standardParameterRow.Visible = parametersPage && (mode == PreviewTargetMode.Standard || mode == PreviewTargetMode.Map);
            if (characterParameterRow != null) characterParameterRow.Visible = parametersPage && mode == PreviewTargetMode.Player;
            if (effectParameterRow != null) effectParameterRow.Visible = parametersPage && mode == PreviewTargetMode.Effect;
            if (parametersPage && unifiedFpsGroup != null) ArrangeSettingsPage(mode);
            if (transitionsPage && mode == PreviewTargetMode.Player && playerTransitionList is FlowLayoutPanel)
                ResizeStateTransitionRows((FlowLayoutPanel)playerTransitionList);

            int targetIndex = mode == PreviewTargetMode.Map ? 3 : mode == PreviewTargetMode.Effect ? 1 : mode == PreviewTargetMode.Player ? 2 : 0;
            if (previewModeEnumComboBox.SelectedIndex != targetIndex) previewModeEnumComboBox.SelectedIndex = targetIndex;
            if (previewModeEnumHost != null)
            {
                previewModeEnumHost.BackColor = Color.FromArgb(38, 46, 53);
                previewModeEnumHost.Invalidate();
            }
            AdjustSimulationSettingsHeight();
            ApplyMapMode();
        }

        // 左ペインの幅（フォルダ・状態遷移・設定の3ページのうち最大）。言語で列の幅が変わるので、その都度求める。
        private int GetDesiredLeftWidth()
        {
            return Math.Max(Math.Max(384, 440),
                484 + transitionColumnShift + transitionCheckShift + transitionInputExtra);
        }

        // 中央のシートと右のプレビューが最低限必要とする幅。
        private const int SheetPaneMinWidth = 180;

        // シートの欄の最小幅。見出し（「シート」）と右の「全体表示」ボタンが重ならずに並ぶ幅を下回らない
        // （長い言語では 180px だと見出しがボタンに隠れていた）。
        private int SheetPaneMinimumWidth()
        {
            if (sheetTitleLabel == null || sheetFitButton == null) return SheetPaneMinWidth;
            int header = sheetTitleLabel.Margin.Horizontal + sheetTitleLabel.PreferredSize.Width +
                sheetFitButton.Margin.Horizontal + sheetFitButton.PreferredSize.Width + 8;
            return Math.Max(SheetPaneMinWidth, header);
        }
        private const int PreviewPaneMinWidth = 480;

        private bool adjustingLeftWidth;   // 幅をプログラムで変えている間（利用者の操作と区別する）
        private int? userLeftWidth;        // 利用者が分割線で広げた左ペインの幅

        private void AdjustLeftWorkspaceWidth()
        {
            if (mainSplit == null || mainSplit.Width <= 0) return;
            // タブを切り替えても左ペイン幅が動かないよう、3ページのうち最大の幅にする。
            // 利用者が分割線でそれより広げていれば、その幅を保つ（狭くする方向は最大の幅で止まる）。
            int desired = Math.Max(GetDesiredLeftWidth(), userLeftWidth ?? 0);
            int requiredPreviewWidth = SheetPaneMinimumWidth() + PreviewPaneMinWidth + 1;
            int maximum = mainSplit.Width - requiredPreviewWidth - mainSplit.SplitterWidth;
            int minimum = Math.Min(300, Math.Max(80, maximum));
            if (maximum < minimum) return;
            int target = Math.Max(minimum, Math.Min(maximum, desired));
            // 先に最小幅を緩めてから位置を変え、最後に「このタブに必要な幅」を最小幅にする。
            // これで分割線を狭く動かしても下部ボタンやタブ文字が切れない。
            int minimumWidth = Math.Max(minimum, Math.Min(maximum, GetDesiredLeftWidth()));
            if (mainSplit.SplitterDistance == target && mainSplit.Panel1MinSize == minimumWidth) return;
            adjustingLeftWidth = true;
            try
            {
                mainSplit.Panel1MinSize = 80;
                mainSplit.SplitterDistance = target;
                mainSplit.Panel1MinSize = minimumWidth;
            }
            finally
            {
                adjustingLeftWidth = false;
            }
            ApplyPreviewSplitOnly();
        }

        private static int PreviewModeOrder(PreviewTargetMode mode)
        {
            return mode == PreviewTargetMode.Map ? 3 : mode == PreviewTargetMode.Effect ? 1 : mode == PreviewTargetMode.Player ? 2 : 0;
        }

        private static string GetStateDisplayName(PlayerAnimationState state)
        {
            switch (state)
            {
                case PlayerAnimationState.Idle: return Loc.T("state.idle");
                case PlayerAnimationState.Move: return Loc.T("state.move");
                case PlayerAnimationState.MoveRight: return Loc.T("state.moveRight");
                case PlayerAnimationState.MoveLeft: return Loc.T("state.moveLeft");
                case PlayerAnimationState.MoveUp: return Loc.T("state.moveUp");
                case PlayerAnimationState.MoveDown: return Loc.T("state.moveDown");
                case PlayerAnimationState.Jump: return Loc.T("state.jump");
                case PlayerAnimationState.AttackRight1: return Loc.T("state.attackRight1");
                case PlayerAnimationState.AttackRight2: return Loc.T("state.attackRight2");
                case PlayerAnimationState.AttackLeft1: return Loc.T("state.attackLeft1");
                case PlayerAnimationState.AttackLeft2: return Loc.T("state.attackLeft2");
                case PlayerAnimationState.JumpStart: return Loc.T("state.jumpStart");
                case PlayerAnimationState.JumpAir: return Loc.T("state.jumpAir");
                case PlayerAnimationState.JumpLand: return Loc.T("state.jumpLand");
                case PlayerAnimationState.JumpRightStart: return Loc.T("state.jumpRightStart");
                case PlayerAnimationState.JumpRightAir: return Loc.T("state.jumpRightAir");
                case PlayerAnimationState.JumpRightLand: return Loc.T("state.jumpRightLand");
                case PlayerAnimationState.JumpLeftStart: return Loc.T("state.jumpLeftStart");
                case PlayerAnimationState.JumpLeftAir: return Loc.T("state.jumpLeftAir");
                case PlayerAnimationState.JumpLeftLand: return Loc.T("state.jumpLeftLand");
                default: return state.ToString();
            }
        }

        private void PauseInteractivePreview()
        {
            previewInput.Clear();
            FlushAnimInfo();
            if (!animationTimer.Enabled) return;
            animationTimer.Stop();
            SetPlayAccessibleName(false);
            if (playToggleButton.IsHandleCreated && !playToggleButton.IsDisposed)
                playToggleButton.Invalidate();
        }

        private void ShowKeySettingsDialog()
        {
            PauseInteractivePreview();
            IDictionary<PreviewAction, Keys> original = previewInput.GetBindingsCopy();
            using (var dialog = CreateDarkDialog(Loc.T("dialog.keySettingsTitle"), Loc.T("dialog.keySettingsMessage"), 590))
            {
                var buttons = new Dictionary<PreviewAction, Button>();
                PreviewAction? waiting = null;
                PreviewAction[] actions = (PreviewAction[])Enum.GetValues(typeof(PreviewAction));
                for (int i = 0; i < actions.Length; i++)
                {
                    PreviewAction action = actions[i];
                    var label = new Label
                    {
                        Text = GetActionDisplayName(action),
                        Location = new Point(30, 108 + i * 43),
                        Size = new Size(180, 34),
                        TextAlign = ContentAlignment.MiddleLeft,
                        ForeColor = lightText
                    };
                    var button = new Button
                    {
                        Text = FormatKey(previewInput.GetBinding(action)),
                        Location = new Point(230, 106 + i * 43),
                        Size = new Size(176, 36),
                        Tag = action
                    };
                    ApplyButtonStyle(button);
                    button.Click += (s, e) =>
                    {
                        waiting = (PreviewAction)((Button)s).Tag;
                        ((Button)s).Text = Loc.T("key.prompt");
                        dialog.Focus();
                    };
                    buttons[action] = button;
                    dialog.Controls.Add(label);
                    dialog.Controls.Add(button);
                }

                dialog.KeyDown += (s, e) =>
                {
                    if (!waiting.HasValue || e.KeyCode == Keys.Escape || e.KeyCode == Keys.ControlKey ||
                        e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
                    previewInput.SetBinding(waiting.Value, e.KeyCode);
                    foreach (KeyValuePair<PreviewAction, Button> pair in buttons)
                        pair.Value.Text = FormatKey(previewInput.GetBinding(pair.Key));
                    waiting = null;
                    e.SuppressKeyPress = true;
                };

                var cancel = new Button { Text = Loc.T("button.cancel"), Size = new Size(110, 42), Location = new Point(178, 530) };
                var save = new Button { Text = Loc.T("button.save"), Size = new Size(110, 42), Location = new Point(298, 530) };
                ApplyButtonStyle(cancel);
                ApplyButtonStyle(save);
                save.BackColor = accentColor;
                cancel.DialogResult = DialogResult.Cancel;
                save.DialogResult = DialogResult.OK;
                dialog.CancelButton = cancel;
                dialog.AcceptButton = save;
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(save);
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    previewInput.RestoreBindings(original);
                    RefreshInlineBindingButtons();
                }
                else
                {
                    RefreshInlineBindingButtons();
                    CommitUndoableChange();
                }
            }
        }

        private static string GetActionDisplayName(PreviewAction action)
        {
            switch (action)
            {
                case PreviewAction.MoveLeft: return Loc.T("action.moveLeft");
                case PreviewAction.MoveRight: return Loc.T("action.moveRight");
                case PreviewAction.MoveUp: return Loc.T("action.moveUp");
                case PreviewAction.MoveDown: return Loc.T("action.moveDown");
                case PreviewAction.Jump: return Loc.T("action.jump");
                case PreviewAction.Attack1: return Loc.T("action.attack1");
                case PreviewAction.Attack2: return Loc.T("action.attack2");
                default: return action.ToString();
            }
        }

        private static string FormatKey(Keys key)
        {
            switch (key)
            {
                case Keys.None: return Loc.T("key.unset");
                case Keys.LButton: return "Mouse Left";
                case Keys.RButton: return "Mouse Right";
                case Keys.MButton: return "Mouse Middle";
                case Keys.XButton1: return "Mouse X1";
                case Keys.XButton2: return "Mouse X2";
                case Keys.Space: return "Space";
                default: return new KeysConverter().ConvertToString(key);
            }
        }

        private void BeginInlineBindingCapture(PreviewAction action, Button button)
        {
            PauseInteractivePreview();
            CancelInlineBindingCapture();
            button.Text = "…";
            inlineBindingFilter = new NextInputMessageFilter(key =>
                TryBeginInvoke(() => CompleteInlineBindingCapture(action, key)));
            Application.AddMessageFilter(inlineBindingFilter);
        }

        private void CompleteInlineBindingCapture(PreviewAction action, Keys key)
        {
            CancelInlineBindingCapture();
            if (key == Keys.None) return;
            previewInput.SetBinding(action, key);
            RefreshInlineBindingButtons();
            CommitUndoableChange();
        }

        private void CancelInlineBindingCapture()
        {
            if (inlineBindingFilter != null)
            {
                Application.RemoveMessageFilter(inlineBindingFilter);
                inlineBindingFilter = null;
            }
            RefreshInlineBindingButtons();
        }

        private void RefreshInlineBindingButtons()
        {
            foreach (KeyValuePair<PreviewAction, List<Button>> pair in inlineBindingButtons)
                foreach (Button button in pair.Value)
                    if (!button.IsDisposed) button.Text = FormatKey(previewInput.GetBinding(pair.Key));
        }

        // シート／プレビューヘッダーの数値情報（セル数・サイズ・Frame・ズーム率）
        // に共通の「チップ」見た目（背景付きの角丸ボックス）を適用する。配置用の
        // Marginは呼び出し側の役割のためここでは触らない。
        private void StyleChip(RoundedLabel chip)
        {
            chip.CornerRadius = RadiusSm;
            chip.AutoSize = true;
            chip.BackColor = panelElevated;
            chip.ForeColor = mutedText;
            chip.Font = UiFont.Create(8.5f, FontStyle.Regular, GraphicsUnit.Point);
            chip.Padding = new Padding(8, 3, 8, 3);
            chip.TextAlign = ContentAlignment.MiddleCenter;
        }

        private Panel MakeToolbarDivider()
        {
            return new Panel
            {
                Size = new Size(1, 24),
                Margin = new Padding(9, 18, 9, 3),
                BackColor = dividerColor
            };
        }

        private Label MakeTopLabel(string text)
        {
            return MakeTopLabelInstance(text);
        }

        private Label MakeTopLabelInstance(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(8, 20, 3, 3),
                ForeColor = lightText,
                BackColor = Color.Transparent
            };
        }

        private Panel BuildTitleBar()
        {
            var bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 56;
            bar.BackColor = titleBack;

            var mark = new Panel();
            mark.Size = new Size(36, 36);
            mark.Location = new Point(16, 14);
            mark.BackColor = Color.Transparent;
            mark.Paint += (s, e) =>
            {
                // アイコン自体が青い角丸の土台を持つので、その背後に別の色の下地は敷かない。
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (Icon != null) e.Graphics.DrawIcon(Icon, new Rectangle(0, 0, mark.Width, mark.Height));
            };

            var title = new Label();
            title.Text = AppInfo.Name + "  v" + AppInfo.Version;
            title.AutoSize = true;
            title.Font = UiFont.Create(13.0f, FontStyle.Regular, GraphicsUnit.Point);
            title.ForeColor = lightText;
            title.BackColor = Color.Transparent;
            title.Margin = Padding.Empty;
            title.Left = 64;

            // 固定座標だとDPI・フォントで縦位置がずれるため、実サイズから縦中央に揃える。
            Action centerContent = () =>
            {
                mark.Top = (bar.Height - mark.Height) / 2;
                title.Top = (bar.Height - title.Height) / 2;
                PlaceProjectTitleControls(bar, title);
            };
            bar.Resize += (s, e) => centerContent();
            title.SizeChanged += (s, e) => centerContent();
            bar.HandleCreated += (s, e) => centerContent();
            centerContent();

            var caption = new FlowLayoutPanel();
            caption.Dock = DockStyle.Right;
            caption.Width = 180;
            caption.Height = 56;
            caption.FlowDirection = FlowDirection.LeftToRight;
            caption.WrapContents = false;
            caption.Margin = Padding.Empty;
            caption.Padding = Padding.Empty;
            caption.BackColor = titleBack;

            Button minimize = MakeCaptionButton(CaptionGlyph.Minimize, "Minimize");
            Button maximize = MakeCaptionButton(CaptionGlyph.Maximize, "Maximize");
            Button close = MakeCaptionButton(CaptionGlyph.Close, "Close");
            Resize += (s, e) => maximize.Invalidate();   // 最大化中は「元に戻す」の形にする
            minimize.Click += (s, e) => WindowState = FormWindowState.Minimized;
            maximize.Click += (s, e) => ToggleMaximize();
            close.FlatAppearance.MouseOverBackColor = dangerBackColor;
            close.Click += (s, e) => Close();
            caption.Controls.Add(minimize);
            caption.Controls.Add(maximize);
            caption.Controls.Add(close);

            MouseEventHandler drag = (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            };
            EventHandler maximizeDoubleClick = (s, e) => ToggleMaximize();
            bar.MouseDown += drag;
            mark.MouseDown += drag;
            title.MouseDown += drag;
            bar.DoubleClick += maximizeDoubleClick;
            mark.DoubleClick += maximizeDoubleClick;
            title.DoubleClick += maximizeDoubleClick;

            bar.Controls.Add(mark);
            bar.Controls.Add(title);
            BuildProjectTitleControls(bar, title);
            bar.Controls.Add(caption);
            BuildUpdateTitleControls(bar, caption);
            centerContent();
            return bar;
        }

        private enum CaptionGlyph { Minimize, Maximize, Close }

        // 最小化・最大化・閉じるの記号は文字（—□×）だと字形ごとに線の太さが違うため、同じ太さの線で描く。
        private Button MakeCaptionButton(CaptionGlyph glyph, string accessibleName)
        {
            var button = new Button();
            button.Text = "";
            button.AccessibleName = accessibleName;
            button.Paint += (s, e) => PaintCaptionGlyph(e.Graphics, button, glyph);
            button.Size = new Size(60, 56);
            button.Margin = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 47, 54);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(55, 63, 70);
            button.BackColor = titleBack;
            button.ForeColor = lightText;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void PaintCaptionGlyph(Graphics g, Button button, CaptionGlyph glyph)
        {
            float scale = button.DeviceDpi / 96f;
            int stroke = Math.Max(1, (int)Math.Round(scale));
            int size = (int)Math.Round(10 * scale);          // Windows 標準と同じ 10px 角（100%時）
            int left = (button.Width - size) / 2;
            int top = (button.Height - size) / 2;
            using (var brush = new SolidBrush(button.ForeColor))
            {
                // 縦横の線は塗りつぶしの帯で描き、どの記号も同じ太さ・同じ濃さにする。
                Action<int, int, int, int> outline = (x, y, w, h) =>
                {
                    g.FillRectangle(brush, x, y, w, stroke);
                    g.FillRectangle(brush, x, y + h - stroke, w, stroke);
                    g.FillRectangle(brush, x, y, stroke, h);
                    g.FillRectangle(brush, x + w - stroke, y, stroke, h);
                };
                switch (glyph)
                {
                    case CaptionGlyph.Minimize:
                        g.FillRectangle(brush, left, top + (size - stroke) / 2, size, stroke);
                        break;
                    case CaptionGlyph.Maximize:
                        if (WindowState == FormWindowState.Maximized)
                        {
                            // 元に戻す: 前に一回り小さい四角、右上に後ろの四角の角だけを見せる。
                            int offset = 2 * stroke;
                            int front = size - offset;
                            outline(left, top + offset, front, front);
                            g.FillRectangle(brush, left + offset, top, size - offset, stroke);
                            g.FillRectangle(brush, left + size - stroke, top, stroke, size - offset);
                        }
                        else
                        {
                            outline(left, top, size, size);
                        }
                        break;
                    case CaptionGlyph.Close:
                        // 斜めの線は滑らかにすると細く見えるので、少しだけ太くして縦横の線と見た目をそろえる。
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var pen = new Pen(button.ForeColor, stroke * 1.5f))
                        {
                            g.DrawLine(pen, left + 0.5f, top + 0.5f, left + size - 0.5f, top + size - 0.5f);
                            g.DrawLine(pen, left + size - 0.5f, top + 0.5f, left + 0.5f, top + size - 0.5f);
                        }
                        break;
                }
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        private void ApplyTopPanelColors(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Label || control is CheckBox)
                {
                    control.ForeColor = lightText;
                    control.BackColor = Color.Transparent;
                }

                if (control.HasChildren)
                {
                    ApplyTopPanelColors(control);
                }
            }
        }

        private void ComboBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox combo = (ComboBox)sender;

            Rectangle bounds = e.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            using (var back = new SolidBrush(selected ? Color.FromArgb(80, 80, 80) : inputBack))
            {
                e.Graphics.FillRectangle(back, bounds);

                string itemText = "";
                if (e.Index >= 0 && e.Index < combo.Items.Count)
                {
                    itemText = combo.Items[e.Index].ToString();
                }
                else if (combo.SelectedIndex >= 0 && combo.SelectedIndex < combo.Items.Count)
                {
                    itemText = combo.Items[combo.SelectedIndex].ToString();
                }
                else
                {
                    itemText = combo.Text;
                }

                TextRenderer.DrawText(e.Graphics, itemText, combo.Font,
                    new Rectangle(bounds.X + 4, bounds.Y, Math.Max(0, bounds.Width - 8), bounds.Height),
                    lightText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            using (var borderPen = new Pen(Color.White))
            {
                Rectangle border = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                e.Graphics.DrawRectangle(borderPen, border);
            }

            e.DrawFocusRectangle();
        }

        private void ApplyButtonStyle(Button button)
        {
            // Regionによる角丸クリッピングはアンチエイリアスされないピクセル単位の
            // ハードマスクのため、半径4-8px程度では階段状のジャギーにしかならず、
            // 通常の表示倍率ではほぼ直角にしか見えない（拡大スクリーンショットで
            // 確認済み）。Regionは使わず、Paintで背景・枠線・文字を
            // アンチエイリアス付きに自前描画する。
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.BackColor = Color.Transparent;
            button.ForeColor = lightText;
            button.UseVisualStyleBackColor = false;
            button.Margin = new Padding(4, 8, 4, 3);

            bool hover = false;
            bool pressed = false;
            button.MouseEnter += (s, e) => { hover = true; button.Invalidate(); };
            button.MouseLeave += (s, e) => { hover = false; pressed = false; button.Invalidate(); };
            button.MouseDown += (s, e) => { pressed = true; button.Invalidate(); };
            button.MouseUp += (s, e) => { pressed = false; button.Invalidate(); };
            button.EnabledChanged += (s, e) => button.Invalidate();

            button.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
                // 呼び出し元がApplyButtonStyle後にBackColorを個別に上書きする既存の
                // パターン（例: playToggleButton.BackColor = accentColor）に対応する
                // ため、固定色ではなく実行時のbutton.BackColorを都度読んで基準色にする。
                bool customFill = button.BackColor.A == 255;
                Color baseColor = customFill ? button.BackColor : inputBack;
                Color fill = pressed ? ControlPaint.Dark(baseColor, 0.1f)
                    : hover ? ControlPaint.Light(baseColor, 0.15f) : baseColor;
                // BackColorが不透明だと標準描画が矩形全体を先に塗ってしまい四隅が直角に
                // 残るため、親の背景色で一度塗り直してから角丸を描く。
                e.Graphics.Clear(RoundedPaint.ResolveBackdrop(button.Parent));
                using (var path = CreateRoundedPath(bounds, RadiusMd))
                {
                    using (var brush = new SolidBrush(fill))
                        e.Graphics.FillPath(brush, path);
                    // 個別に塗り色を指定したボタン（主要アクション等）は枠を見せない。
                    using (var pen = new Pen(customFill ? fill : inputBorder))
                        e.Graphics.DrawPath(pen, path);
                }
                TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, button.ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
        }

        private void StyleGroupedExportButton(RoundedButton button, Color backColor)
        {
            button.BackColor = backColor;
            button.ForeColor = lightText;
            button.BorderColor = accentColor;
            button.CornerRadius = RadiusMd;
            button.UseVisualStyleBackColor = false;
            button.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand;
        }

        private void PlayToggleButton_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int centerX = playToggleButton.ClientSize.Width / 2;
            int centerY = playToggleButton.ClientSize.Height / 2;
            using (var brush = new SolidBrush(Color.White))
            {
                if (animationTimer.Enabled)
                {
                    e.Graphics.FillRectangle(brush, centerX - 7, centerY - 9, 5, 18);
                    e.Graphics.FillRectangle(brush, centerX + 2, centerY - 9, 5, 18);
                }
                else
                {
                    e.Graphics.FillPolygon(brush, new[]
                    {
                        new Point(centerX - 6, centerY - 10),
                        new Point(centerX + 10, centerY),
                        new Point(centerX - 6, centerY + 10)
                    });
                }
            }
        }

        private void StyleMenuItem(ToolStripMenuItem item)
        {
            item.BackColor = darkPanel;
            item.ForeColor = lightText;
            item.Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point);
            item.AutoSize = false;
            item.Size = new Size(230, 36);
        }

        private bool ShowDarkConfirm(string title, string message, string confirmText)
        {
            return ShowDarkConfirm(title, message, confirmText, null);
        }

        private bool ShowDarkConfirm(string title, string message, string confirmText, Point? screenAnchor)
        {
            PauseInteractivePreview();
            using (var dialog = CreateDarkDialog(title, message, 210))
            {
                var cancel = new Button { Text = Loc.T("button.cancel"), Size = new Size(110, 42), Location = new Point(102, 148) };
                var confirm = new Button { Text = confirmText, Size = new Size(110, 42), Location = new Point(224, 148) };
                ApplyButtonStyle(cancel);
                ApplyButtonStyle(confirm);
                // 長い言語でも切れないよう、ボタンは文字に合わせて広げ、2つを中央にそろえて並べる。
                foreach (Button button in new[] { cancel, confirm })
                    button.Width = Math.Max(110, TextRenderer.MeasureText(button.Text, dialog.Font).Width + 28);   // 置いたあとはダイアログの書体で描かれる
                int buttonsLeft = Math.Max(28, (dialog.ClientSize.Width - cancel.Width - 12 - confirm.Width) / 2);
                cancel.Left = buttonsLeft;
                confirm.Left = cancel.Right + 12;
                confirm.BackColor = dangerBackColor;
                confirm.FlatAppearance.BorderColor = Color.FromArgb(255, 90, 100);
                cancel.DialogResult = DialogResult.Cancel;
                confirm.DialogResult = DialogResult.OK;
                dialog.CancelButton = cancel;
                dialog.AcceptButton = confirm;
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(confirm);
                if (screenAnchor.HasValue)
                {
                    dialog.StartPosition = FormStartPosition.Manual;
                    dialog.Location = CalculateConfirmationLocation(screenAnchor.Value, dialog.Size);
                }
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private static Point CalculateConfirmationLocation(Point screenAnchor, Size dialogSize)
        {
            Rectangle area = Screen.FromPoint(screenAnchor).WorkingArea;
            int left = screenAnchor.X - dialogSize.Width / 2;
            int top = screenAnchor.Y - 128;
            left = Math.Max(area.Left, Math.Min(left, area.Right - dialogSize.Width));
            top = Math.Max(area.Top, Math.Min(top, area.Bottom - dialogSize.Height));
            return new Point(left, top);
        }

        private void ShowDarkNotice(string title, string message)
        {
            PauseInteractivePreview();
            using (var dialog = CreateDarkDialog(title, message, 210))
            {
                var close = new Button { Text = "OK", Size = new Size(110, 42), Location = new Point(298, 148) };
                ApplyButtonStyle(close);
                close.BackColor = accentColor;
                close.DialogResult = DialogResult.OK;
                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                dialog.Controls.Add(close);
                dialog.ShowDialog(this);
            }
        }

        private bool ShowDarkTextPrompt(string title, string labelText, string initialValue, out string value)
        {
            PauseInteractivePreview();
            value = null;
            using (var dialog = CreateDarkDialog(title, labelText, 260))
            {
                var inputPanel = new RoundedPanel
                {
                    Location = new Point(28, 112),
                    Size = new Size(380, 42),
                    BackColor = inputBack,
                    BorderColor = inputBorder,
                    CornerRadius = RadiusMd
                };
                var editor = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = inputBack,
                    ForeColor = lightText,
                    Font = UiFont.Create(10.5f, FontStyle.Regular, GraphicsUnit.Point),
                    Location = new Point(10, 10),
                    Size = new Size(358, 24),
                    Text = initialValue ?? ""
                };
                inputPanel.Controls.Add(editor);

                var cancel = new Button { Text = Loc.T("button.cancel"), Size = new Size(110, 42), Location = new Point(178, 198) };
                var save = new Button { Text = Loc.T("button.change"), Size = new Size(110, 42), Location = new Point(298, 198) };
                ApplyButtonStyle(cancel);
                ApplyButtonStyle(save);
                save.BackColor = accentColor;
                cancel.DialogResult = DialogResult.Cancel;
                save.DialogResult = DialogResult.OK;
                dialog.CancelButton = cancel;
                dialog.AcceptButton = save;
                dialog.Controls.Add(inputPanel);
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(save);
                dialog.Shown += (s, e) => { editor.Focus(); editor.SelectAll(); };
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                value = editor.Text;
                return true;
            }
        }

        private Form CreateDarkDialog(string title, string message, int height)
        {
            var dialog = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                BackColor = darkBack,
                ForeColor = lightText,
                Font = Font,
                ClientSize = new Size(436, height),
                Padding = new Padding(1),
                AutoScaleMode = AutoScaleMode.Dpi,
                KeyPreview = true
            };
            var titleLabel = new Label
            {
                Text = title,
                AutoSize = false,
                Location = new Point(28, 24),
                Size = new Size(380, 30),
                ForeColor = lightText,
                Font = UiFont.Create(13.0f, FontStyle.Bold, GraphicsUnit.Point)
            };
            var messageLabel = new Label
            {
                Text = message,
                AutoSize = false,
                Location = new Point(28, 63),
                Size = new Size(380, 52),
                ForeColor = mutedText,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point)
            };
            dialog.Controls.Add(titleLabel);
            dialog.Controls.Add(messageLabel);
            dialog.Paint += (s, e) =>
            {
                using (var path = CreateRoundedPath(new Rectangle(0, 0, dialog.ClientSize.Width - 1, dialog.ClientSize.Height - 1), 10))
                using (var pen = new Pen(dividerColor)) e.Graphics.DrawPath(pen, path);
            };
            dialog.Shown += (s, e) => ApplyRoundedRegion(dialog, 10);
            dialog.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) dialog.DialogResult = DialogResult.Cancel;
            };
            DialogMotion.Attach(dialog, this);   // 少し下から浮き上がり、後ろを薄く暗くする
            return dialog;
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            // プロジェクト: Ctrl+S 保存 / Ctrl+Shift+S 名前を付けて保存 / Ctrl+O 開く
            if (e.Control && !e.Alt && (e.KeyCode == Keys.S || (e.KeyCode == Keys.O && !e.Shift)))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.O) OpenProjectWithDialog();
                else if (e.Shift) SaveProjectAs();
                else SaveProject();
                return;
            }
            if (previewTargetMode == PreviewTargetMode.Map && e.KeyCode == Keys.F && !e.Control && !e.Alt && !IsTextInputFocused(this))
            { if (mapPalette.ContainsFocus) mapPalette.Fit(true); else if (mapCanvas.ContainsFocus) mapCanvas.Fit(true); e.Handled = true; e.SuppressKeyPress = true; return; }
            // Fキー: シート/プレビューのどちらかを最後にクリックしていれば、その表示を
            // 中心・全体表示（100%）へ戻す。それ以外（他の場所を最後にクリック、文字入力中など）
            // では何もしない。
            if (e.KeyCode == Keys.F && !e.Control && !e.Alt && !e.Shift &&
                lastClickedCanvas != null && !IsTextInputFocused(this))
            {
                lastClickedCanvas.ResetToFitAnimated();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // 共通: 選択・範囲選択中に Delete / Backspace で削除する（確認ダイアログを出す）。文字の入力中は除く。
            if ((e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) && !e.Control && !e.Alt && !assignMode && !IsTextInputFocused(this))
            {
                if (previewTargetMode == PreviewTargetMode.Map && mapCanvas.ContainsFocus && !mapCanvas.CellSelection.IsEmpty)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    int tiles = mapCanvas.TilesInSelection().Count;
                    if (tiles > 0 && ShowDarkConfirm(Loc.T("dialog.deleteTilesTitle"), Loc.T("message.deleteTiles", tiles), Loc.T("button.delete")))
                        mapCanvas.EraseSelection();
                    return;
                }
                bool mapSelection = previewTargetMode == PreviewTargetMode.Map && mapPalette.SelectedSet.Count > 0 && !treeView.ContainsFocus;
                if (mapSelection || selectedImages.Count > 0 || selectedFolders.Count > 0)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (mapSelection) DeleteMapChips(mapPalette.SelectedInOrder());
                    else RemoveSelectedNode();
                    return;
                }
            }

            if (e.Control && !e.Shift && e.KeyCode == Keys.Z)
            {
                UndoLastOperation();
                e.SuppressKeyPress = true;
                return;
            }

            if ((e.Control && e.KeyCode == Keys.Y) || (e.Control && e.Shift && e.KeyCode == Keys.Z))
            {
                RedoLastOperation();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Space && previewTargetMode != PreviewTargetMode.Player && CanUsePlaybackShortcut())
            {
                if (!spaceShortcutHeld)
                {
                    spaceShortcutHeld = true;
                    TogglePlayback();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (previewTargetMode != PreviewTargetMode.Player || !previewInput.Handles(e.KeyCode) || IsTextEditorActive()) return;
            previewInput.KeyDown(e.KeyCode);
            if (!animationTimer.Enabled && animationFrames.Count > 0)
            {
                lastSimulationTickUtc = DateTime.UtcNow;
                animationTimer.Interval = 16;
                animationTimer.Start();
                SetPlayAccessibleName(true);
                playToggleButton.Invalidate();
            }
            e.SuppressKeyPress = true;
        }

        private void MainForm_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                spaceShortcutHeld = false;
                if (previewTargetMode != PreviewTargetMode.Player && CanUsePlaybackShortcut())
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
            }

            if (previewTargetMode != PreviewTargetMode.Player || !previewInput.Handles(e.KeyCode) || IsTextEditorActive()) return;
            previewInput.KeyUp(e.KeyCode);
            e.SuppressKeyPress = true;
        }

        private bool IsTextEditorActive()
        {
            Control active = ActiveControl;
            while (active is ContainerControl)
                active = ((ContainerControl)active).ActiveControl;
            return active is TextBoxBase || active is ComboBox || active is NumericUpDown;
        }

        private bool CanUsePlaybackShortcut()
        {
            Form activeForm = Form.ActiveForm;
            bool thisWindowIsActive = activeForm == this || (activeForm == null && ContainsFocus);
            return thisWindowIsActive && ContainsFocus && !IsTextEditorActive() && inlineBindingFilter == null;
        }

        private void TogglePlayback()
        {
            if (previewTargetMode == PreviewTargetMode.Map) return;
            if (previewTargetMode == PreviewTargetMode.Player) return;
            if (animationTimer.Enabled)
            {
                animationTimer.Stop();
                FlushAnimInfo();
                SetPlayAccessibleName(false);
                playToggleButton.Invalidate();
                return;
            }

            if (animationFrames.Count == 0) return;
            if (previewTargetMode == PreviewTargetMode.Standard)
            {
                if (!IsFrameInRange(animationIndex)) animationIndex = GetFirstPlayableFrameIndex();
                UpdateTimerInterval();
            }
            else
            {
                lastSimulationTickUtc = DateTime.UtcNow;
                animationTimer.Interval = 16;
            }
            animationTimer.Start();
            animCanvas.Focus();
            SetPlayAccessibleName(true);
            playToggleButton.Invalidate();
        }

        // 最小化している間だけ再生を止め、元に戻したら再開する。
        private void PausePlaybackWhileMinimized()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (pausedForMinimize || !animationTimer.Enabled) return;
                pausedForMinimize = true;
                previewInput.Clear();
                animationTimer.Stop();
                return;
            }
            if (!pausedForMinimize) return;
            pausedForMinimize = false;
            if (previewTargetMode == PreviewTargetMode.Player) StartPlayerPreviewIfPossible();
            else if (!animationTimer.Enabled) TogglePlayback();
        }

        private void StartPlayerPreviewIfPossible()
        {
            if (previewTargetMode != PreviewTargetMode.Player || animationFrames.Count == 0 || animationTimer.Enabled) return;
            lastSimulationTickUtc = DateTime.UtcNow;
            animationTimer.Interval = 16;
            animationTimer.Start();
        }

        // シートの作り直しが終わったときに自動で整う値（セル範囲の上限・マップの並び情報など）は操作ではない。
        // 新しい「元に戻す」の1回分にせず、直前の操作と同じ1回分にする（元に戻すを2回押さないと戻らなかった。2026-10-06）。
        private bool automaticUpdate;
        private void RunAutomatic(Action action)
        {
            bool pendingBefore = commitScheduled;
            automaticUpdate = true;
            try { action(); }
            finally { automaticUpdate = false; }
            // 作り直しの前に記録していない操作があれば、それと一緒に記録される。なければ、記録済みの状態へ取り込む。
            if (!pendingBefore && !commitScheduled && !restoringState && undoManager != null) undoManager.Resettle();
        }

        private void CommitUndoableChange()
        {
            if (automaticUpdate) return;   // 自動で整った値は RunAutomatic が取り込む
            if (restoringState || closing || IsDisposed || Disposing) return;
            if (!IsHandleCreated)
            {
                CommitUndoNow();
                return;
            }
            if (commitScheduled) return;
            commitScheduled = true;
            if (!TryBeginInvoke(() =>
            {
                commitScheduled = false;
                CommitUndoNow();
            })) commitScheduled = false;
        }

        private bool TryBeginInvoke(Action action)
        {
            if (action == null || closing || IsDisposed || Disposing || !IsHandleCreated) return false;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (closing || IsDisposed || Disposing) return;
                    action();
                }));
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void CommitUndoNow()
        {
            if (restoringState) return;
            commitScheduled = false;
            if (undoManager.CommitIfChanged())
            {
                undoMenuItem.Enabled = undoManager.CanUndo;
                redoMenuItem.Enabled = undoManager.CanRedo;
            }
            UpdateProjectTitle();
        }

        private void UndoLastOperation()
        {
            if (commitScheduled) CommitUndoNow();
            if (!undoManager.CanUndo) return;
            // 左・中央・右がまとめて書き換わるので、描き直しを止めて戻し、最後に一度に描き直す。
            FreezeWorkspace();
            try { if (!undoManager.Undo()) return; }
            finally { ThawWorkspace(); }
            undoMenuItem.Enabled = undoManager.CanUndo;
            redoMenuItem.Enabled = undoManager.CanRedo;
            statusLabel.Text = Loc.T("status.undone", GetAllItems().Count);
            UpdateProjectTitle();
        }

        private void RedoLastOperation()
        {
            if (commitScheduled) CommitUndoNow();
            if (!undoManager.CanRedo) return;
            FreezeWorkspace();
            try { if (!undoManager.Redo()) return; }
            finally { ThawWorkspace(); }
            undoMenuItem.Enabled = undoManager.CanUndo;
            redoMenuItem.Enabled = undoManager.CanRedo;
            statusLabel.Text = Loc.T("status.redone", GetAllItems().Count);
            UpdateProjectTitle();
        }

        private void RestoreState(AppStateSnapshot target)
        {
            restoringState = true;
            try
            {
                animationTimer.Stop();
                folders.Clear();
                foreach (FolderSnapshot sourceFolder in target.Folders)
                {
                    var folder = new ImageFolder { Name = sourceFolder.Name };
                    foreach (string path in sourceFolder.Paths) folder.Items.Add(new ImageItem { Path = path });
                    folders.Add(folder);
                }
                nextFolderNumber = target.NextFolderNumber;
                selectedFolders.Clear();
                selectedImages.Clear();
                folderSelectionAnchor = null;
                imageSelectionAnchor = null;
                RestoreSelectedItems(target.SelectedItems);
                RestoreMemos(target.Memos);

                columnsBox.Value = ClampDecimal(target.Columns, columnsBox.Minimum, columnsBox.Maximum);
                scaleComboBox.SelectedIndex = Math.Max(0, Math.Min(scaleComboBox.Items.Count - 1, target.ScaleIndex));
                fpsBox.Value = ClampDecimal(target.Fps, fpsBox.Minimum, fpsBox.Maximum);
                gridNumberCheckBox.Checked = target.ExportNumbers;
                axisNumbersCheckBox.Checked = target.ShowAxisNumbers;
                terrainCheckBox.Checked = target.UseTerrain;
                backgroundPaletteComboBox.SelectedIndex = Math.Max(0,
                    Math.Min(backgroundPaletteComboBox.Items.Count - 1, target.BackgroundPalette));
                blackTransparencyCheckBox.Checked = target.BlackTransparency;
                colorBlendMode = target.ColorBlendMode;
                spriteAdjustmentColor = target.AdjustmentColor;
                spriteAdjustmentStrength = target.AdjustmentStrength;
                UpdateColorAdjustmentButton();
                foreach (PlayerAnimationState state in Enum.GetValues(typeof(PlayerAnimationState)))
                {
                    AnimationClipSettings restoredClip;
                    if (target.PlayerClips.TryGetValue(state, out restoredClip))
                        playerClips[state] = restoredClip.Clone();
                }
                effectClip = target.EffectClip.Clone();
                SyncUnifiedFps();
                previewInput.RestoreBindings(target.KeyBindings);
                RefreshInlineBindingButtons();
                playerMoveSpeedBox.Value = ClampDecimal(target.PlayerMoveSpeed, playerMoveSpeedBox.Minimum, playerMoveSpeedBox.Maximum);
                playerJumpDistanceBox.Value = ClampDecimal(target.PlayerJumpDistance, playerJumpDistanceBox.Minimum, playerJumpDistanceBox.Maximum);
                playerGravityBox.Value = ClampDecimal(target.PlayerGravity, playerGravityBox.Minimum, playerGravityBox.Maximum);
                playerGroundOffsetBox.Value = ClampDecimal(target.PlayerGroundOffset, playerGroundOffsetBox.Minimum, playerGroundOffsetBox.Maximum);
                playerColliderWidthBox.Value = ClampDecimal(target.PlayerColliderWidth, playerColliderWidthBox.Minimum, playerColliderWidthBox.Maximum);
                playerColliderHeightBox.Value = ClampDecimal(target.PlayerColliderHeight, playerColliderHeightBox.Minimum, playerColliderHeightBox.Maximum);
                playerColliderVisibleCheckBox.Checked = target.ShowCollider;
                mirrorMissingDirectionsCheckBox.Checked = target.MirrorMissingDirections;
                effectDirectionXBox.Value = ClampDecimal(target.EffectDirectionX, effectDirectionXBox.Minimum, effectDirectionXBox.Maximum);
                effectDirectionYBox.Value = ClampDecimal(target.EffectDirectionY, effectDirectionYBox.Minimum, effectDirectionYBox.Maximum);
                effectSpeedBox.Value = ClampDecimal(target.EffectSpeed, effectSpeedBox.Minimum, effectSpeedBox.Maximum);
                RestoreMap(target.MapJson);
                previewTargetMode = target.PreviewMode;
                simulationRangesInitialized = target.SimulationRangesInitialized;
                playerAnimator = new SpriteAnimationController(playerClips);
                ApplyPreviewTargetModeUi();
                UpdateTree();
                UpdatePreviewSafe();
                endCellBox.Value = ClampDecimal(target.EndCell, endCellBox.Minimum, endCellBox.Maximum);
                startCellBox.Value = ClampDecimal(target.StartCell, startCellBox.Minimum, startCellBox.Maximum);
                animationIndex = GetFirstPlayableFrameIndex();
                UpdateAnimationPreview();
                playToggleButton.Invalidate();
            }
            finally
            {
                restoringState = false;
            }
            // 復元中に画像を読み込んだ場合、そこでは初期値を入れないので、ここで入れる（古いファイルの 0 も含む）。
            EnsureColliderDefaults();
        }

        private AppStateSnapshot CaptureState()
        {
            var state = new AppStateSnapshot
            {
                NextFolderNumber = nextFolderNumber,
                Columns = (int)columnsBox.Value,
                ScaleIndex = scaleComboBox.SelectedIndex,
                Fps = (int)fpsBox.Value,
                StartCell = (int)startCellBox.Value,
                EndCell = (int)endCellBox.Value,
                ExportNumbers = gridNumberCheckBox.Checked,
                ShowAxisNumbers = axisNumbersCheckBox.Checked,
                UseTerrain = terrainCheckBox.Checked
            };
            state.PreviewMode = previewTargetMode;
            state.MapJson = CaptureMap();
            state.SelectedItems = CaptureSelectedItems();
            state.Memos = CaptureMemos();
            state.PlayerMoveSpeed = playerMoveSpeedBox.Value;
            state.PlayerJumpDistance = playerJumpDistanceBox.Value;
            state.PlayerGravity = playerGravityBox.Value;
            state.PlayerGroundOffset = playerGroundOffsetBox.Value;
            state.PlayerColliderWidth = playerColliderWidthBox.Value;
            state.PlayerColliderHeight = playerColliderHeightBox.Value;
            state.ShowCollider = playerColliderVisibleCheckBox.Checked;
            state.MirrorMissingDirections = mirrorMissingDirectionsCheckBox.Checked;
            state.BackgroundPalette = backgroundPaletteComboBox.SelectedIndex;
            state.BlackTransparency = blackTransparencyCheckBox.Checked;
            state.ColorBlendMode = colorBlendMode;
            state.AdjustmentColor = spriteAdjustmentColor;
            state.AdjustmentStrength = spriteAdjustmentStrength;
            state.EffectDirectionX = effectDirectionXBox.Value;
            state.EffectDirectionY = effectDirectionYBox.Value;
            state.EffectSpeed = effectSpeedBox.Value;
            state.EffectClip = effectClip.Clone();
            state.SimulationRangesInitialized = simulationRangesInitialized;
            foreach (KeyValuePair<PlayerAnimationState, AnimationClipSettings> pair in playerClips)
                state.PlayerClips[pair.Key] = pair.Value.Clone();
            foreach (KeyValuePair<PreviewAction, Keys> pair in previewInput.GetBindingsCopy())
                state.KeyBindings[pair.Key] = pair.Value;
            foreach (ImageFolder folder in folders)
            {
                var folderState = new FolderSnapshot { Name = folder.Name };
                foreach (ImageItem item in folder.Items) folderState.Paths.Add(item.Path);
                state.Folders.Add(folderState);
            }
            return state;
        }

        // 取り消し/やり直しでは選択も別の状態として扱う。プロジェクトの未保存判定では選択を無視する。
        private static bool StatesEqual(AppStateSnapshot left, AppStateSnapshot right)
        {
            return StatesEqualIgnoringSelection(left, right) && SelectionsEqual(left.SelectedItems, right.SelectedItems);
        }

        private static bool StatesEqualIgnoringSelection(AppStateSnapshot left, AppStateSnapshot right)
        {
            return MemosEqual(left.Memos, right.Memos) && StatesEqualBody(left, right);
        }

        private static bool StatesEqualBody(AppStateSnapshot left, AppStateSnapshot right)
        {
            if (left.NextFolderNumber != right.NextFolderNumber || left.Columns != right.Columns ||
                left.ScaleIndex != right.ScaleIndex || left.Fps != right.Fps ||
                left.StartCell != right.StartCell || left.EndCell != right.EndCell ||
                left.ExportNumbers != right.ExportNumbers || left.ShowAxisNumbers != right.ShowAxisNumbers || left.UseTerrain != right.UseTerrain ||
                left.Folders.Count != right.Folders.Count ||
                left.MapJson != right.MapJson || left.PreviewMode != right.PreviewMode || left.PlayerMoveSpeed != right.PlayerMoveSpeed ||
                left.PlayerJumpDistance != right.PlayerJumpDistance || left.PlayerGravity != right.PlayerGravity ||
                left.PlayerGroundOffset != right.PlayerGroundOffset ||
                left.PlayerColliderWidth != right.PlayerColliderWidth || left.PlayerColliderHeight != right.PlayerColliderHeight ||
                left.ShowCollider != right.ShowCollider ||
                left.MirrorMissingDirections != right.MirrorMissingDirections ||
                left.BackgroundPalette != right.BackgroundPalette ||
                left.BlackTransparency != right.BlackTransparency ||
                left.ColorBlendMode != right.ColorBlendMode ||
                left.AdjustmentColor.ToArgb() != right.AdjustmentColor.ToArgb() ||
                left.AdjustmentStrength != right.AdjustmentStrength ||
                left.EffectDirectionX != right.EffectDirectionX || left.EffectDirectionY != right.EffectDirectionY ||
                left.EffectSpeed != right.EffectSpeed ||
                left.SimulationRangesInitialized != right.SimulationRangesInitialized ||
                !ClipSettingsEqual(left.EffectClip, right.EffectClip)) return false;
            foreach (PlayerAnimationState state in Enum.GetValues(typeof(PlayerAnimationState)))
            {
                AnimationClipSettings leftClip;
                AnimationClipSettings rightClip;
                if (!left.PlayerClips.TryGetValue(state, out leftClip) ||
                    !right.PlayerClips.TryGetValue(state, out rightClip) ||
                    !ClipSettingsEqual(leftClip, rightClip)) return false;
            }
            foreach (PreviewAction action in Enum.GetValues(typeof(PreviewAction)))
            {
                Keys leftKey;
                Keys rightKey;
                if (!left.KeyBindings.TryGetValue(action, out leftKey) ||
                    !right.KeyBindings.TryGetValue(action, out rightKey) || leftKey != rightKey) return false;
            }
            for (int folderIndex = 0; folderIndex < left.Folders.Count; folderIndex++)
            {
                FolderSnapshot a = left.Folders[folderIndex];
                FolderSnapshot b = right.Folders[folderIndex];
                if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || a.Paths.Count != b.Paths.Count) return false;
                for (int itemIndex = 0; itemIndex < a.Paths.Count; itemIndex++)
                {
                    if (!string.Equals(a.Paths[itemIndex], b.Paths[itemIndex], StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return true;
        }

        private static bool ClipSettingsEqual(AnimationClipSettings left, AnimationClipSettings right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            return left.Enabled == right.Enabled && left.StartCell == right.StartCell &&
                   left.EndCell == right.EndCell && left.Fps == right.Fps;
        }

        private static decimal ClampDecimal(decimal value, decimal minimum, decimal maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void ApplyInputStyle(Control control)
        {
            control.BackColor = inputBack;
            control.ForeColor = lightText;
            control.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
        }

        // 数値入力の表示幅。最低でも3桁("999")、最大値/最小値/小数の桁数が
        // それ以上ならそちらに合わせ、文字が見切れない幅を返す（左10 + 右の▲▼領域33 + 余白）。
        private int RequiredInputWidth(NumericUpDown input)
        {
            using (Font font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point))
            {
                string text = "999";
                string max = input.Maximum.ToString("0.##");
                string min = input.Minimum.ToString("0.##");
                if (max.Length > text.Length) text = max;
                if (min.Length > text.Length) text = min;
                if (input.DecimalPlaces > 0 && text.IndexOf('.') < 0) text += ".99";
                return TextRenderer.MeasureText(text, font).Width + 10 + 33 + 6;
            }
        }

        private Panel CreateInputHost(NumericUpDown input, int width)
        {
            width = Math.Max(width, RequiredInputWidth(input));
            var host = new Panel();
            host.Size = new Size(width, 46);
            host.Margin = new Padding(5, 8, 12, 3);
            host.BackColor = Color.Transparent;
            input.Visible = false;
            input.TabStop = false;

            var editor = new TextBox();
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = inputBack;
            editor.ForeColor = lightText;
            editor.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            editor.TextAlign = HorizontalAlignment.Left;
            editor.Text = input.Value.ToString("0.##");
            editor.TabStop = true;
            bool syncing = false;
            bool editorActive = false;

            Action updateEditorBounds = () =>
            {
                int height = editor.PreferredHeight;
                editor.SetBounds(10, Math.Max(1, (host.Height - height) / 2),
                    Math.Max(10, host.Width - 43), height);
            };

            Action syncEditorFromInput = () =>
            {
                string valueText = input.Value.ToString("0.##");
                if (editor.Text == valueText) return;
                syncing = true;
                editor.Text = valueText;
                syncing = false;
            };
            Action commitEditor = () =>
            {
                decimal parsed;
                if (!decimal.TryParse(editor.Text, out parsed)) parsed = input.Value;
                parsed = Math.Max(input.Minimum, Math.Min(input.Maximum, parsed));
                input.Value = parsed;
                syncEditorFromInput();
                editor.SelectionStart = editor.Text.Length;
            };
            input.ValueChanged += (s, e) =>
            {
                // Keep the user's partially typed text intact until editing is committed.
                if (editorActive) return;
                syncEditorFromInput();
            };
            editor.TextChanged += (s, e) =>
            {
                if (syncing) return;
                // Validation is intentionally deferred to Leave/Enter so an empty or
                // incomplete value can be replaced without the control restoring it.
            };
            editor.Enter += (s, e) =>
            {
                // 範囲を何度も打ち直すため、入力中も再生は止めない（押しっぱなしのキーだけ離す）。
                editorActive = true;
                previewInput.Clear();
            };
            editor.Leave += (s, e) =>
            {
                editorActive = false;
                commitEditor();
            };
            editor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Up) { commitEditor(); input.UpButton(); syncEditorFromInput(); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { commitEditor(); input.DownButton(); syncEditorFromInput(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { commitEditor(); e.SuppressKeyPress = true; }
            };
            editor.MouseWheel += (s, e) =>
            {
                commitEditor();
                if (e.Delta > 0) input.UpButton();
                else if (e.Delta < 0) input.DownButton();
                syncEditorFromInput();
            };

            var spinner = new Panel();
            spinner.Size = new Size(28, host.Height - 4);
            spinner.Location = new Point(host.Width - 31, 2);
            spinner.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            spinner.Margin = Padding.Empty;
            spinner.BackColor = inputBack;
            spinner.Cursor = Cursors.Hand;
            spinner.TabStop = false;
            bool spinnerHover = false;
            spinner.MouseEnter += (s, e) => { spinnerHover = true; spinner.Invalidate(); };
            spinner.MouseLeave += (s, e) => { spinnerHover = false; spinner.Invalidate(); };
            spinner.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                // 編集中(editorActive)はValueChangedが表示欄を更新しない仕様のため、
                // キーボードの↑↓と同じく、入力途中の文字を確定 → 増減 → 表示を明示同期する。
                // これが無いと値だけ変わって表示が古いままで、フォーカスを外した時に
                // 古い表示が値へ書き戻され変更が取り消されていた。
                commitEditor();
                if (e.Y < spinner.Height / 2) input.UpButton();
                else input.DownButton();
                syncEditorFromInput();
                editor.Focus();
            };
            spinner.Paint += (s, e) =>
            {
                Color color = !host.Enabled ? disabledText : spinnerHover ? lightText : mutedText;
                using (var pen = new Pen(color, 1.4f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    int center = spinner.Width / 2;
                    int upper = Math.Max(5, spinner.Height / 2 - 5);
                    int lower = Math.Min(spinner.Height - 5, spinner.Height / 2 + 5);
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(center - 3, upper + 2), new Point(center, upper - 1), new Point(center + 3, upper + 2)
                    });
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(center - 3, lower - 2), new Point(center, lower + 1), new Point(center + 3, lower - 2)
                    });
                }
            };

            host.Controls.Add(input);
            host.Controls.Add(editor);
            host.Controls.Add(spinner);
            spinner.BringToFront();
            editor.BringToFront();
            host.Resize += (s, e) =>
            {
                updateEditorBounds();
                spinner.SetBounds(host.Width - 31, 2, 28, Math.Max(1, host.Height - 4));
            };
            host.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, host.Width - 1, host.Height - 1);
                using (var path = CreateRoundedPath(bounds, RadiusMd))
                {
                    using (var brush = new SolidBrush(host.Enabled ? inputBack : darkPanel))
                        e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(host.Enabled ? inputBorder : dividerColor))
                        e.Graphics.DrawPath(pen, path);
                }
            };
            // 無効のときは、枠・文字・矢印を灰色にして、触れないことが分かるようにする。
            host.EnabledChanged += (s, e) =>
            {
                editor.BackColor = host.Enabled ? inputBack : darkPanel;
                editor.ForeColor = host.Enabled ? lightText : disabledText;
                spinner.BackColor = host.Enabled ? inputBack : darkPanel;
                spinner.Cursor = host.Enabled ? Cursors.Hand : Cursors.Default;
                spinner.Invalidate();
                host.Invalidate();
            };
            updateEditorBounds();
            return host;
        }

        private Panel CreateComboHost(ComboBox combo, int width)
        {
            var host = new Panel();
            host.Size = new Size(width, 46);
            host.Margin = new Padding(5, 8, 18, 3);
            host.BackColor = Color.Transparent;
            host.Cursor = Cursors.Hand;
            combo.Visible = false;
            combo.TabStop = false;
            host.Controls.Add(combo);

            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.BackColor = darkPanel;
            menu.ForeColor = lightText;
            menu.Font = UiFont.Create(10.0f, FontStyle.Regular, GraphicsUnit.Point);
            menu.Padding = new Padding(2);
            // 項目文字は言語で変わるため、メニューは開くたびに現在の項目から作り、
            // ホスト幅は最も長い項目が省略されない幅へ合わせる。
            Action rebuildMenu = () =>
            {
                menu.Items.Clear();
                for (int index = 0; index < combo.Items.Count; index++)
                {
                    int selectedIndex = index;
                    var item = new ToolStripMenuItem(combo.Items[index].ToString());
                    item.AutoSize = false;
                    item.Size = new Size(host.Width - 4, 34);
                    item.BackColor = darkPanel;
                    item.ForeColor = lightText;
                    item.Click += (s, e) =>
                    {
                        combo.SelectedIndex = selectedIndex;
                        host.Invalidate();
                    };
                    menu.Items.Add(item);
                }
            };
            Action refitWidth = () =>
            {
                int needed = width;
                foreach (object entry in combo.Items)
                    needed = Math.Max(needed, TextRenderer.MeasureText(entry.ToString(), combo.Font).Width + 48);
                host.Width = Math.Min(needed, 320);
                host.Invalidate();
            };
            refitWidth();
            comboWidthRefits.Add(refitWidth);

            bool hover = false;
            host.MouseEnter += (s, e) => { hover = true; host.Invalidate(); };
            host.MouseLeave += (s, e) => { hover = false; host.Invalidate(); };
            host.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                rebuildMenu();
                menu.Show(host, new Point(0, host.Height));
            };
            combo.SelectedIndexChanged += (s, e) => host.Invalidate();
            host.Disposed += (s, e) => menu.Dispose();
            host.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, host.Width - 1, host.Height - 1);
                using (var path = CreateRoundedPath(bounds, RadiusMd))
                {
                    using (var brush = new SolidBrush(inputBack))
                        e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(hover ? Color.FromArgb(105, 115, 124) : inputBorder))
                        e.Graphics.DrawPath(pen, path);
                }

                string text = combo.SelectedItem == null ? "" : combo.SelectedItem.ToString();
                TextRenderer.DrawText(e.Graphics, text, combo.Font,
                    new Rectangle(12, 0, Math.Max(1, host.Width - 48), host.Height), lightText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                Color arrowColor = hover ? lightText : mutedText;
                using (var arrow = new Pen(arrowColor, 1.5f))
                {
                    arrow.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    arrow.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    int x = host.Width - 19;
                    int y = host.Height / 2;
                    e.Graphics.DrawLines(arrow, new[]
                    {
                        new Point(x - 3, y - 2), new Point(x, y + 1), new Point(x + 3, y - 2)
                    });
                }
            };
            return host;
        }

        private int ScaleDpi(int pixels)
        {
            return (int)Math.Round(pixels * DeviceDpi / 96.0);
        }

        private void ApplyResponsiveToolbar()
        {
            if (toolbarPanel == null || primaryToolbarRow == null || ClientSize.Width <= 0) return;
            int primaryContentWidth = GetFlowContentWidth(primaryToolbarRow);
            // 右端のエクスポートカードが占める幅を除いた残りで判定・中央寄せする。
            // 全幅で計算すると左余白が大きくなりすぎ、読み込み中表示などが
            // カードの下に隠れていた。
            int cardWidth = exportCard != null && exportCard.Parent != null ? exportCard.Parent.Width : 0;
            int availableWidth = ClientSize.Width - cardWidth;
            bool compact = availableWidth < primaryContentWidth + 44;
            // 生のピクセル値で上書きすると高DPIで縮んで要素が切れるため、DPIに合わせる。
            int desiredToolbarHeight = ScaleDpi(compact ? 124 : 68);
            int desiredPrimaryHeight = ScaleDpi(compact ? 124 : 68);
            primaryToolbarRow.AutoScroll = false;
            int primaryLeft = compact ? 22 : Math.Max(22, (availableWidth - primaryContentWidth) / 2);
            primaryToolbarRow.Padding = new Padding(primaryLeft, 10, 18, 4);
            if (toolbarPanel.Height == desiredToolbarHeight &&
                primaryToolbarRow.Height == desiredPrimaryHeight &&
                primaryToolbarRow.WrapContents == compact) return;

            toolbarPanel.SuspendLayout();
            primaryToolbarRow.SuspendLayout();
            toolbarPanel.Height = desiredToolbarHeight;
            primaryToolbarRow.Height = desiredPrimaryHeight;
            primaryToolbarRow.WrapContents = compact;
            primaryToolbarRow.ResumeLayout(true);
            toolbarPanel.ResumeLayout(true);
            toolbarPanel.Invalidate();
        }

        private static int GetFlowContentWidth(FlowLayoutPanel panel)
        {
            int width = 0;
            foreach (Control control in panel.Controls)
                width += control.Width + control.Margin.Horizontal;
            return width;
        }

        private FlowLayoutPanel CreatePlaybackInputGroup(Label label, Panel inputHost)
        {
            var group = new FlowLayoutPanel();
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.WrapContents = false;
            group.Margin = Padding.Empty;
            group.Padding = Padding.Empty;
            group.BackColor = workspaceBack;
            group.Controls.Add(label);
            group.Controls.Add(inputHost);
            group.Size = group.GetPreferredSize(Size.Empty);
            return group;
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            using (var path = CreateRoundedPath(new Rectangle(0, 0, control.Width, control.Height), radius))
            {
                Region old = control.Region;
                control.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        internal static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int diameter = Math.Max(1, radius * 2);
            Rectangle arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void ApplyInitialLayout()
        {
            if (mainSplit != null && mainSplit.Width > 0)
            {
                mainSplit.Panel1MinSize = 80;
                mainSplit.Panel2MinSize = 120;

                // タブを切り替えたときと同じ幅（3ページのうち最大）で始める。起動直後だけ狭いと、最初の切り替えで幅が跳ねる。
                int target = GetDesiredLeftWidth();
                int min = mainSplit.Panel1MinSize;
                int max = mainSplit.Width - mainSplit.Panel2MinSize - mainSplit.SplitterWidth;

                if (max >= min)
                {
                    adjustingLeftWidth = true;
                    try
                    {
                        mainSplit.SplitterDistance = Math.Max(min, Math.Min(max, target));
                    }
                    finally
                    {
                        adjustingLeftWidth = false;
                    }
                }
            }

            AdjustLeftWorkspaceWidth();
            ApplyPreviewSplitOnly();
        }

        private void ApplyResolutionAwareMinimumSize()
        {
            Rectangle working = Screen.FromControl(this).WorkingArea;
            float dpiScale = Math.Max(1.0f, DeviceDpi / 96.0f);
            // 幅は「中身が切れずに収まる最低限」（左ペイン＋シート＋プレビュー）。画面が小さい・拡大率が高いときでも、
            // 広げ縮めできる余地が残るよう、画面の80%までにとどめる。ただし中身の最低限（文字が拡大率で大きくなる分を含む）は割らない。
            int contentWidth = GetDesiredLeftWidth() + SheetPaneMinimumWidth() + PreviewPaneMinWidth + 4 + 2 * ResizeGrip;
            int idealWidth = (int)Math.Round(contentWidth * dpiScale);
            int neededWidth = (int)Math.Round(contentWidth * (1 + 0.4 * (dpiScale - 1)));
            int minimumWidth = Math.Min(idealWidth, Math.Max(neededWidth, (int)(working.Width * 0.80)));
            int idealHeight = (int)Math.Round(640 * dpiScale);
            int neededHeight = (int)Math.Round((560 + 2 * ResizeGrip) * (1 + 0.4 * (dpiScale - 1)));
            int minimumHeight = Math.Min(idealHeight, Math.Max(neededHeight, (int)(working.Height * 0.80)));
            MinimumSize = new Size(
                Math.Min(working.Width, Math.Max(900, minimumWidth)),
                Math.Min(working.Height, Math.Max(540, minimumHeight)));
            if (Width < MinimumSize.Width) Width = MinimumSize.Width;
            if (Height < MinimumSize.Height) Height = MinimumSize.Height;
        }

        private void ApplyPreviewSplitOnly()
        {
            if (adjustingPreviewSplit || previewSplit == null || previewSplit.Width <= 0) return;
            adjustingPreviewSplit = true;
            try
            {
                previewSplit.Panel1MinSize = SheetPaneMinimumWidth();
                previewSplit.Panel2MinSize = PreviewPaneMinWidth;

                int desiredRight = Math.Max(PreviewPaneMinWidth, (int)Math.Round(previewSplit.Width * 0.46));
                int half = previewSheetRatio.HasValue
                    ? (int)Math.Round(previewSplit.Width * previewSheetRatio.Value)
                    : previewSplit.Width - desiredRight - previewSplit.SplitterWidth;
                int min = previewSplit.Panel1MinSize;
                int max = previewSplit.Width - previewSplit.Panel2MinSize - previewSplit.SplitterWidth;

                if (max >= min)
                {
                    int target = Math.Max(min, Math.Min(max, half));
                    if (previewSplit.SplitterDistance != target) previewSplit.SplitterDistance = target;
                }
            }
            finally
            {
                adjustingPreviewSplit = false;
            }
        }

        private int GetScaleDivisor()
        {
            switch (scaleComboBox.SelectedIndex)
            {
                case 1: return 2;
                case 2: return 4;
                case 3: return 8;
                default: return 1;
            }
        }

        private void OpenFileDropDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = Loc.T("filter.images") + " (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
                dialog.Multiselect = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                // ドラッグ＆ドロップ・フォルダ追加と同じく、ファイル名の昇順（数字は数の大きさ順）で入れる。
                if (!dialog.FileNames.Any(IsSupportedImagePath))
                {
                    ShowNoImagesFoundWarning();
                    return;
                }
                AddAutoFolder(dialog.FileNames, true, GetSelectedTargetFolder());
            }
        }

        private void OpenFolderDropDialog()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                if (AddDirectoryFolder(dialog.SelectedPath) == 0) ShowNoImagesFoundWarning();
                UpdateTree();
                QueuePreviewUpdate();
                CommitUndoableChange();
            }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            string[] droppedPaths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (droppedPaths.Length == 1 && string.Equals(Path.GetExtension(droppedPaths[0]), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                OpenProjectFromUser(droppedPaths[0]);
                return;
            }
            AddDroppedPaths(droppedPaths, GetSelectedTargetFolder());
        }

        private void TreeView_DrawNode(object sender, FolderNodeDrawEventArgs e)
        {
            if (e.Node == null) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            ImageFolder drawnFolder = e.Node.Tag as ImageFolder;
            ImageItem drawnImage = e.Node.Tag as ImageItem;
            bool selected = drawnFolder != null
                ? selectedFolders.Contains(drawnFolder)
                : drawnImage != null
                    ? selectedImages.Contains(drawnImage)
                    : ReferenceEquals(e.Node, treeView.SelectedNode);
            Rectangle row = new Rectangle(3, e.Bounds.Y + 3,
                Math.Max(1, treeView.ClientSize.Width - 9), Math.Max(1, treeView.ItemHeight - 6));

            if (selected)
            {
                using (var path = CreateRoundedPath(row, RadiusMd))
                using (var brush = new SolidBrush(treeSelectionColor))
                    e.Graphics.FillPath(brush, path);
            }

            bool isFolder = e.Node.Tag is ImageFolder;
            int indent = isFolder ? 8 : 28;
            Rectangle iconRect = new Rectangle(row.X + indent, row.Y + 12, 24, 24);
            Color iconColor = selected ? Color.White : Color.FromArgb(215, 220, 224);

            if (isFolder)
                DrawFolderGlyph(e.Graphics, iconRect, iconColor);
            else
                DrawImageGlyph(e.Graphics, iconRect, iconColor);

            Rectangle textRect = new Rectangle(iconRect.Right + 10, row.Y,
                Math.Max(1, row.Right - iconRect.Right - 48), row.Height);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, treeView.Font, textRect,
                selected ? Color.White : lightText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (isFolder)
            {
                // 開閉の矢印。開き具合に合わせて回る（閉じている＝下向き、開いている＝上向き）。
                float open = treeView.OpennessOf(e.Node);
                PointF center = new PointF(row.Right - 20, row.Y + row.Height / 2f);
                var state = e.Graphics.Save();
                e.Graphics.TranslateTransform(center.X, center.Y);
                e.Graphics.RotateTransform(180f * open);
                using (var pen = new Pen(mutedText, 1.8f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                    e.Graphics.DrawLines(pen, new[] { new PointF(-5f, -2.5f), new PointF(0f, 2.5f), new PointF(5f, -2.5f) });
                e.Graphics.Restore(state);
            }
        }

        private static void DrawFolderGlyph(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, IconStroke))
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddLines(new[]
                {
                    new Point(r.X + 2, r.Y + 8), new Point(r.X + 7, r.Y + 8),
                    new Point(r.X + 10, r.Y + 5), new Point(r.X + 17, r.Y + 5),
                    new Point(r.X + 20, r.Y + 9), new Point(r.X + 22, r.Y + 9),
                    new Point(r.X + 19, r.Y + 19), new Point(r.X + 2, r.Y + 19),
                    new Point(r.X + 2, r.Y + 8)
                });
                g.DrawPath(pen, path);
            }
        }

        private static void DrawImageGlyph(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, IconStroke))
            using (var path = CreateRoundedPath(new Rectangle(r.X + 2, r.Y + 2, 20, 20), RadiusSm))
            {
                g.DrawPath(pen, path);
                g.DrawEllipse(pen, r.X + 15, r.Y + 6, 3, 3);
                g.DrawLines(pen, new[]
                {
                    new Point(r.X + 5, r.Y + 18), new Point(r.X + 10, r.Y + 12),
                    new Point(r.X + 13, r.Y + 15), new Point(r.X + 16, r.Y + 12),
                    new Point(r.X + 20, r.Y + 18)
                });
            }
        }

        private void TreeView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            FolderNode node = e.Item as FolderNode;
            if (node == null) return;
            internalDragNode = node;
            treeView.SelectedNode = node;
            treeView.DoDragDrop(node, DragDropEffects.Move);
            internalDragNode = null;
            HideDropIndicator();
        }

        private void TreeView_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(typeof(FolderNode)))
            {
                e.Effect = DragDropEffects.Move;
            }
            else
            {
                MainForm_DragEnter(sender, e);
            }
        }

        private void TreeView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(typeof(FolderNode)))
            {
                MainForm_DragEnter(sender, e);
                return;
            }

            Point clientPoint = treeView.PointToClient(new Point(e.X, e.Y));
            FolderNode target = treeView.GetNodeAt(clientPoint);
            e.Effect = CanDropNode(internalDragNode, target) ? DragDropEffects.Move : DragDropEffects.None;
            treeView.DragAutoScroll(clientPoint);   // 上下の端に近づくと、なめらかにスクロールする
            // 落とすと入る場所を、行と行の間の線で示す（選択は動かさない）。
            int lineY, lineX;
            if (target != null && TryGetDropLine(internalDragNode, target, clientPoint, out lineY, out lineX))
                ShowDropIndicator(lineY, lineX);
            else
                HideDropIndicator();
        }

        //--------------
        // ドラッグ中の「ここに入る」線
        //--------------
        private void ShowDropIndicator(int y, int x) { treeView.ShowDropLine(y, x); }

        private void HideDropIndicator() { treeView.HideDropLine(); }

        // フォルダの表示範囲（展開中なら中の画像まで）の下端。
        private static int BottomOfFolderBlock(FolderNode folderNode)
        {
            return folderNode.IsExpanded && folderNode.Nodes.Count > 0 ? folderNode.LastNode.Bounds.Bottom : folderNode.Bounds.Bottom;
        }

        // 落とす位置が、対象の下側か（フォルダを動かすときは、中の画像まで含めた範囲の下半分か）。
        private static bool DropAfter(FolderNode dragged, FolderNode target, Point client)
        {
            if (dragged != null && dragged.Tag is ImageFolder)
            {
                FolderNode folderNode = target.Tag is ImageFolder ? target : target.Parent;
                if (folderNode == null) return false;
                return client.Y > (folderNode.Bounds.Top + BottomOfFolderBlock(folderNode)) / 2;
            }
            return client.Y > target.Bounds.Top + target.Bounds.Height / 2;
        }

        // 落としたときに入る場所を、行と行の間の y と、線の開始位置 x で返す。落とせない場所なら false。
        private bool TryGetDropLine(FolderNode dragged, FolderNode target, Point client, out int y, out int x)
        {
            y = 0;
            x = 0;
            if (dragged == null || !CanDropNode(dragged, target)) return false;
            bool after = DropAfter(dragged, target, client);
            const int folderX = 8;
            const int imageX = 30;
            if (dragged.Tag is ImageFolder)
            {
                FolderNode folderNode = target.Tag is ImageFolder ? target : target.Parent;
                if (folderNode == null || ReferenceEquals(folderNode.Tag, dragged.Tag)) return false;
                y = after ? BottomOfFolderBlock(folderNode) : folderNode.Bounds.Top;
                x = folderX;
                return true;
            }
            if (target.Tag is ImageItem)
            {
                if (ReferenceEquals(dragged.Tag, target.Tag)) return false;
                y = after ? target.Bounds.Bottom : target.Bounds.Top;
                x = imageX;
                return true;
            }
            // フォルダの上に落とすと、そのフォルダの最後に入る。
            y = BottomOfFolderBlock(target);
            x = imageX;
            return true;
        }

        private void TreeView_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null) return;
            HideDropIndicator();
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                Point dropPoint = treeView.PointToClient(new Point(e.X, e.Y));
                FolderNode dropNode = treeView.GetNodeAt(dropPoint);
                ImageFolder targetFolder = null;
                if (dropNode != null)
                {
                    targetFolder = dropNode.Tag as ImageFolder;
                    if (targetFolder == null && dropNode.Parent != null) targetFolder = dropNode.Parent.Tag as ImageFolder;
                }
                // フォルダ（またはその中の画像）の上に直接落としたときは、新しいフォルダを作らず、そのフォルダへすべて入れる。
                AddDroppedPathsCore((string[])e.Data.GetData(DataFormats.FileDrop), targetFolder ?? GetSelectedTargetFolder(), targetFolder != null);
                return;
            }

            FolderNode sourceNode = e.Data.GetData(typeof(FolderNode)) as FolderNode;
            Point clientPoint = treeView.PointToClient(new Point(e.X, e.Y));
            FolderNode targetNode = treeView.GetNodeAt(clientPoint);
            if (!CanDropNode(sourceNode, targetNode)) return;

            MoveTreeNodes(sourceNode, targetNode, DropAfter(sourceNode, targetNode, clientPoint));
        }

        // つかんだもの（選択中のものをつかんだときは、選択中のものすべて。並び順は保つ）を、target の前後（フォルダの上ならその最後）へ動かす。
        // 移動元のフォルダが空になったら消す。動かしたものは選んだままにする。動かしたら true。
        internal bool MoveTreeNodes(FolderNode sourceNode, FolderNode targetNode, bool dropAfter)
        {
            if (!CanDropNode(sourceNode, targetNode)) return false;
            object movedObject = sourceNode.Tag;
            var removedEmpty = new List<string>();
            List<object> moved;
            Dictionary<ImageItem, int> cellsBefore = ComputeCellNumbers(folders);   // 割り当てを同じ画像に付け替えるため

            ImageFolder movedFolder = movedObject as ImageFolder;
            if (movedFolder != null)
            {
                ImageFolder targetFolder = targetNode.Tag as ImageFolder;
                if (targetFolder == null && targetNode.Parent != null) targetFolder = targetNode.Parent.Tag as ImageFolder;
                List<ImageFolder> group = selectedFolders.Contains(movedFolder) && selectedFolders.Count > 1
                    ? folders.Where(selectedFolders.Contains).ToList() : new List<ImageFolder> { movedFolder };
                if (targetFolder == null || group.Contains(targetFolder)) return false;
                foreach (ImageFolder folder in group) folders.Remove(folder);
                int newIndex = folders.IndexOf(targetFolder) + (dropAfter ? 1 : 0);
                folders.InsertRange(Math.Max(0, Math.Min(folders.Count, newIndex)), group);
                moved = group.Cast<object>().ToList();
            }
            else
            {
                ImageItem movedItem = movedObject as ImageItem;
                if (movedItem == null) return false;
                List<ImageItem> group = selectedImages.Contains(movedItem) && selectedImages.Count > 1
                    ? folders.SelectMany(f => f.Items).Where(selectedImages.Contains).ToList() : new List<ImageItem> { movedItem };
                ImageItem targetItem = targetNode.Tag as ImageItem;
                ImageFolder destinationFolder = targetItem != null
                    ? folders.FirstOrDefault(f => f.Items.Contains(targetItem))
                    : targetNode.Tag as ImageFolder;
                if (destinationFolder == null || (targetItem != null && group.Contains(targetItem))) return false;
                List<ImageFolder> sources = folders.Where(f => f.Items.Any(group.Contains)).ToList();
                foreach (ImageFolder folder in sources) folder.Items.RemoveAll(group.Contains);
                int destinationIndex = targetItem != null ? destinationFolder.Items.IndexOf(targetItem) + (dropAfter ? 1 : 0) : destinationFolder.Items.Count;
                destinationFolder.Items.InsertRange(Math.Max(0, Math.Min(destinationFolder.Items.Count, destinationIndex)), group);
                foreach (ImageFolder folder in sources.Where(f => f.Items.Count == 0 && !ReferenceEquals(f, destinationFolder)))
                {
                    folders.Remove(folder);
                    removedEmpty.Add(folder.Name);
                }
                moved = group.Cast<object>().ToList();
            }

            RemapCellAssignments(cellsBefore, ComputeCellNumbers(folders));
            UpdateTree();
            SelectTreeObject(movedObject);
            foreach (object item in moved)
            {
                if (item is ImageItem) selectedImages.Add((ImageItem)item);
                else if (item is ImageFolder) selectedFolders.Add((ImageFolder)item);
            }
            treeView.Invalidate();
            QueuePreviewUpdateCore(removedEmpty.Count == 0 ? null :
                Loc.T("status.emptyFolderRemoved", string.Join(", ", removedEmpty)), true);   // 並びだけの変更なので、読み込み中の表示は出さない
            CommitUndoableChange();
            return true;
        }

        private static bool CanDropNode(FolderNode source, FolderNode target)
        {
            if (source == null || target == null || ReferenceEquals(source, target)) return false;
            if (source.Tag is ImageFolder) return true;
            return source.Tag is ImageItem && (target.Tag is ImageFolder || target.Tag is ImageItem);
        }

        private void TreeView_NodeMouseClick(object sender, FolderNodeMouseEventArgs e)
        {
            ImageFolder folder = e.Node.Tag as ImageFolder;
            ImageItem image = e.Node.Tag as ImageItem;
            if (e.Button == MouseButtons.Left && folder != null &&
                e.X > treeView.ClientSize.Width - 44)
            {
                e.Node.Toggle();
                return;
            }

            if (folder != null)
            {
                selectedImages.Clear();
                imageSelectionAnchor = null;
                if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) == Keys.Shift && folderSelectionAnchor != null)
                {
                    SelectFolderRange(folderSelectionAnchor, folder);
                }
                else if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control)
                {
                    if (!selectedFolders.Add(folder)) selectedFolders.Remove(folder);
                    folderSelectionAnchor = folder;
                }
                else if (e.Button == MouseButtons.Right && selectedFolders.Contains(folder))
                {
                    // Preserve the current multi-selection when right-clicking one of its folders.
                }
                else
                {
                    selectedFolders.Clear();
                    selectedFolders.Add(folder);
                    folderSelectionAnchor = folder;
                }
            }
            else if (image != null)
            {
                selectedFolders.Clear();
                folderSelectionAnchor = null;
                if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) == Keys.Shift && imageSelectionAnchor != null)
                {
                    SelectImageRange(imageSelectionAnchor, image);
                }
                else if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control)
                {
                    if (!selectedImages.Add(image)) selectedImages.Remove(image);
                    imageSelectionAnchor = image;
                }
                else if (e.Button == MouseButtons.Right && selectedImages.Contains(image))
                {
                    // Explorer-like behavior: keep the current multi-selection for its context menu.
                }
                else
                {
                    selectedImages.Clear();
                    selectedImages.Add(image);
                    imageSelectionAnchor = image;
                }
            }
            else
            {
                selectedFolders.Clear();
                selectedImages.Clear();
                folderSelectionAnchor = null;
                imageSelectionAnchor = null;
            }

            treeView.SelectedNode = e.Node;
            treeView.Invalidate();
            if (e.Button == MouseButtons.Right)
            {
                ShowTreeContextMenu(e.Node, e.Location);
            }
        }

        private void SelectFolderRange(ImageFolder first, ImageFolder last)
        {
            int start = folders.IndexOf(first);
            int end = folders.IndexOf(last);
            if (start < 0 || end < 0) return;
            if (start > end) { int temp = start; start = end; end = temp; }
            selectedFolders.Clear();
            for (int index = start; index <= end; index++) selectedFolders.Add(folders[index]);
        }

        private void SelectImageRange(ImageItem first, ImageItem last)
        {
            List<ImageItem> ordered = folders.SelectMany(folder => folder.Items).ToList();
            int start = ordered.IndexOf(first);
            int end = ordered.IndexOf(last);
            if (start < 0 || end < 0) return;
            if (start > end) { int temp = start; start = end; end = temp; }
            selectedImages.Clear();
            for (int index = start; index <= end; index++) selectedImages.Add(ordered[index]);
        }

        private readonly List<ImageFolder> sortTargetFolders = new List<ImageFolder>();

        // フォルダの中の画像を、ファイル名の昇順（数字は数の大きさ順: 2 が 10 より前）に並べ直す。
        // 並びが変わるとセル番号が変わるので、状態への割り当ては同じ画像を指すよう付け替える。
        private void SortFoldersAscending()
        {
            List<ImageFolder> targets = sortTargetFolders.Where(folders.Contains).ToList();
            if (targets.Count == 0) return;
            Dictionary<ImageItem, int> cellsBefore = ComputeCellNumbers(folders);
            bool changed = false;
            foreach (ImageFolder folder in targets)
            {
                List<ImageItem> sorted = folder.Items
                    .OrderBy(item => Path.GetFileName(item.Path), NaturalStringComparer.Instance)
                    .ToList();
                if (sorted.SequenceEqual(folder.Items)) continue;
                folder.Items.Clear();
                folder.Items.AddRange(sorted);
                changed = true;
            }
            if (!changed) return;
            RemapCellAssignments(cellsBefore, ComputeCellNumbers(folders));
            UpdateTree();
            QueuePreviewUpdateCore(null, true);   // 並びだけの変更なので、読み込み中の表示は出さない
            CommitUndoableChange();
        }

        private void ShowTreeContextMenu(FolderNode node, Point location)
        {
            ImageFolder folder = node == null ? null : node.Tag as ImageFolder;
            ImageItem item = node == null ? null : node.Tag as ImageItem;
            int folderCount = selectedFolders.Count;
            int imageCount = selectedImages.Count;
            undoMenuItem.Enabled = undoManager.CanUndo;
            redoMenuItem.Enabled = undoManager.CanRedo;
            renameFolderMenuItem.Enabled = folder != null && folderCount == 1;
            // 「昇順に並べる」の対象: 右クリックしたフォルダ。選択中のフォルダの上で押したときは、選択中のすべてのフォルダ。
            sortTargetFolders.Clear();
            if (folder != null && selectedFolders.Contains(folder)) sortTargetFolders.AddRange(folders.Where(selectedFolders.Contains));
            else if (folder != null) sortTargetFolders.Add(folder);
            sortFolderMenuItem.Enabled = sortTargetFolders.Any(f => f.Items.Count > 1);
            deleteTreeMenuItem.Enabled = item != null || folder != null || folderCount > 0 || imageCount > 0;
            deleteTreeMenuItem.Text = Loc.T("button.delete");
            treeContextMenu.Show(treeView, location);
        }

        private void SelectTreeObject(object value)
        {
            selectedFolders.Clear();
            selectedImages.Clear();
            ImageFolder selectedFolder = value as ImageFolder;
            ImageItem selectedImage = value as ImageItem;
            if (selectedFolder != null)
            {
                selectedFolders.Add(selectedFolder);
                folderSelectionAnchor = selectedFolder;
                imageSelectionAnchor = null;
            }
            else if (selectedImage != null)
            {
                selectedImages.Add(selectedImage);
                imageSelectionAnchor = selectedImage;
                folderSelectionAnchor = null;
            }
            else
            {
                folderSelectionAnchor = null;
                imageSelectionAnchor = null;
            }

            foreach (FolderNode folderNode in treeView.Nodes)
            {
                if (ReferenceEquals(folderNode.Tag, value))
                {
                    treeView.SelectedNode = folderNode;
                    folderNode.EnsureVisible();
                    return;
                }

                foreach (FolderNode itemNode in folderNode.Nodes)
                {
                    if (!ReferenceEquals(itemNode.Tag, value)) continue;
                    treeView.SelectedNode = itemNode;
                    itemNode.EnsureVisible();
                    return;
                }
            }
        }

        private Bitmap CreateDeleteIcon()
        {
            var icon = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(icon))
            using (var pen = new Pen(mutedText, IconStroke))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(pen, 4, 5, 12, 5);
                g.DrawLine(pen, 6, 3, 10, 3);
                g.DrawRectangle(pen, 5, 6, 6, 7);
                g.DrawLine(pen, 7, 8, 7, 11);
                g.DrawLine(pen, 9, 8, 9, 11);
            }
            return icon;
        }

        private Bitmap CreateAddFileIcon()
        {
            var icon = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(icon))
            using (var pen = new Pen(mutedText, IconStroke))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(pen, new[]
                {
                    new PointF(4.5f, 2f), new PointF(9f, 2f), new PointF(12.5f, 6f),
                    new PointF(12.5f, 13f), new PointF(4.5f, 13f), new PointF(4.5f, 2f)
                });
                g.DrawLines(pen, new[] { new PointF(9f, 2f), new PointF(9f, 6f), new PointF(12.5f, 6f) });
                g.DrawLine(pen, 8f, 7.5f, 8f, 11f);
                g.DrawLine(pen, 6f, 9.25f, 10f, 9.25f);
            }
            return icon;
        }

        private Bitmap CreateAddFolderIcon()
        {
            var icon = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(icon))
            using (var pen = new Pen(mutedText, IconStroke))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(pen, new[]
                {
                    new PointF(2f, 4.5f), new PointF(3.5f, 3f), new PointF(6.5f, 3f),
                    new PointF(8f, 4.5f), new PointF(14f, 4.5f), new PointF(14f, 11.5f),
                    new PointF(2f, 11.5f), new PointF(2f, 4.5f)
                });
                g.DrawLine(pen, 8f, 6.5f, 8f, 10f);
                g.DrawLine(pen, 6f, 8.25f, 10f, 8.25f);
            }
            return icon;
        }

        private void StyleIconOnlyButton(Button button, string accessibleName)
        {
            button.Text = "";
            button.Size = new Size(30, 30);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.Transparent;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
            button.AccessibleName = accessibleName;
            toolTip.SetToolTip(button, accessibleName);

            bool hover = false;
            bool pressed = false;
            button.MouseEnter += (s, e) => { hover = true; button.Invalidate(); };
            button.MouseLeave += (s, e) => { hover = false; pressed = false; button.Invalidate(); };
            button.MouseDown += (s, e) => { pressed = true; button.Invalidate(); };
            button.MouseUp += (s, e) => { pressed = false; button.Invalidate(); };
            button.Paint += (s, e) =>
            {
                if (!hover && !pressed) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
                Color fill = pressed ? Color.FromArgb(57, 66, 74) : Color.FromArgb(43, 52, 59);
                using (var path = CreateRoundedPath(bounds, RadiusMd))
                using (var brush = new SolidBrush(fill))
                    e.Graphics.FillPath(brush, path);
                if (button.Image != null)
                {
                    int ix = (button.Width - button.Image.Width) / 2;
                    int iy = (button.Height - button.Image.Height) / 2;
                    e.Graphics.DrawImage(button.Image, ix, iy);
                }
            };
        }

        private void AddDroppedPaths(IEnumerable<string> paths, ImageFolder targetFolder)
        {
            AddDroppedPathsCore(paths, targetFolder, false);
        }

        private void AddDroppedPathsCore(IEnumerable<string> paths, ImageFolder targetFolder, bool directOnFolder)
        {
            BeginLoading();
            try
            {
                var filePaths = new List<string>();
                int imagesFound = 0;   // 対応形式の画像の数（すでに追加済みのものも数える）

                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        imagesFound += AddDirectoryFolder(path);
                    }
                    else if (File.Exists(path))
                    {
                        filePaths.Add(path);
                    }
                }

                imagesFound += filePaths.Count(IsSupportedImagePath);
                if (filePaths.Count > 0)
                {
                    AddAutoFolderCore(filePaths, true, targetFolder, directOnFolder);
                }
                if (imagesFound == 0) ShowNoImagesFoundWarning();

                UpdateTree();
                QueuePreviewUpdate();
                CommitUndoableChange();
            }
            finally
            {
                EndLoading();
                StartPlayerPreviewIfPossible();
            }
        }

        private static readonly string[] SupportedImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

        private static bool IsSupportedImagePath(string path)
        {
            return SupportedImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        // 追加できる画像（対応形式）が1枚も無かったとき。画像以外のファイルは知らせずに除外する。
        private void ShowNoImagesFoundWarning()
        {
            ShowDarkNotice(Loc.T("dialog.noImagesFoundTitle"), Loc.T("message.noImagesFound"));
        }

        // 戻り値は、フォルダ直下にあった対応形式の画像の数（すでに追加済みで今回入らなかったものも含む）。
        private int AddDirectoryFolder(string directoryPath)
        {
            List<string> images = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImagePath)
                .ToList();
            var files = images
                .Where(path => !ContainsPath(path))
                .OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance)
                .ToList();

            if (files.Count == 0)
            {
                return images.Count;
            }

            string sourceFolderName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var folder = new ImageFolder { Name = MakeUniqueFolderName(sourceFolderName) };

            foreach (var file in files)
            {
                folder.Items.Add(new ImageItem { Path = file });
            }

            folders.Add(folder);
            return images.Count;
        }

        private void AddAutoFolder(IEnumerable<string> files, bool sortThisFolder, ImageFolder targetFolder)
        {
            AddAutoFolderCore(files, sortThisFolder, targetFolder, false);
        }

        // intoTarget が true のときは、複数のファイルでも新しいフォルダを作らず、targetFolder へすべて入れる。
        private void AddAutoFolderCore(IEnumerable<string> files, bool sortThisFolder, ImageFolder targetFolder, bool intoTarget)
        {
            string[] allowed = { ".png", ".jpg", ".jpeg", ".bmp" };
            var valid = new List<string>();

            foreach (var path in files)
            {
                if (!File.Exists(path)) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!allowed.Contains(ext)) continue;
                if (ContainsPath(path)) continue;
                valid.Add(path);
            }

            if (sortThisFolder)
            {
                valid = valid.OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance).ToList();
            }

            if (valid.Count == 0) return;

            Dictionary<ImageItem, int> cellsBefore = ComputeCellNumbers(folders);
            bool createImportFolder = targetFolder == null || folders.Count == 0 ||
                (!intoTarget && valid.Count > 1 && targetFolder.Items.Count > 0);
            var folder = createImportFolder ? null : targetFolder;
            if (folder == null)
            {
                folder = new ImageFolder { Name = GenerateUniqueFolderName() };
                folders.Add(folder);
            }

            foreach (var path in valid)
            {
                folder.Items.Add(new ImageItem { Path = path });
            }

            RemapCellAssignments(cellsBefore, ComputeCellNumbers(folders));
            UpdateTree();
            SelectTreeObject(folder);
            QueuePreviewUpdate();
            CommitUndoableChange();
        }

        private ImageFolder GetSelectedTargetFolder()
        {
            if (selectedFolders.Count == 1) return selectedFolders.First();
            FolderNode node = treeView.SelectedNode;
            if (node == null) return null;
            ImageFolder folder = node.Tag as ImageFolder;
            if (folder != null) return folder;
            ImageItem item = node.Tag as ImageItem;
            return item == null ? null : folders.FirstOrDefault(candidate => candidate.Items.Contains(item));
        }

        private string GenerateUniqueFolderName()
        {
            string name;
            do
            {
                name = "Folder_" + nextFolderNumber.ToString("000");
                nextFolderNumber++;
            }
            while (folders.Any(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)));
            return name;
        }

        private string MakeUniqueFolderName(string preferredName)
        {
            string baseName = string.IsNullOrWhiteSpace(preferredName) ? "Folder" : preferredName.Trim();
            if (!folders.Any(folder => string.Equals(folder.Name, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;

            int suffix = 2;
            string candidate;
            do
            {
                candidate = baseName + "_" + suffix.ToString("000");
                suffix++;
            }
            while (folders.Any(folder => string.Equals(folder.Name, candidate, StringComparison.OrdinalIgnoreCase)));
            return candidate;
        }

        private void CreateEmptyFolder()
        {
            var folder = new ImageFolder { Name = GenerateUniqueFolderName() };
            folders.Add(folder);
            UpdateTree();
            SelectTreeObject(folder);
            CommitUndoableChange();
        }

        private void RenameSelectedFolder()
        {
            ImageFolder folder = selectedFolders.Count == 1
                ? selectedFolders.First()
                : treeView.SelectedNode == null ? null : treeView.SelectedNode.Tag as ImageFolder;
            if (folder == null) return;

            string newName;
            if (!ShowDarkTextPrompt(Loc.T("dialog.renameFolderTitle"), Loc.T("dialog.renameFolderPrompt"), folder.Name, out newName)) return;
            newName = (newName ?? "").Trim();
            if (newName.Length == 0)
            {
                ShowDarkNotice(Loc.T("dialog.cannotChange"), Loc.T("message.folderNameEmpty"));
                return;
            }
            if (folders.Any(candidate => !ReferenceEquals(candidate, folder) &&
                string.Equals(candidate.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowDarkNotice(Loc.T("dialog.cannotChange"), Loc.T("message.folderNameDuplicate"));
                return;
            }

            folder.Name = newName;
            UpdateTree();
            SelectTreeObject(folder);
            CommitUndoableChange();
        }

        private bool ContainsPath(string path)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { fullPath = path; }

            return folders.SelectMany(f => f.Items).Any(item =>
            {
                string existing;
                try { existing = Path.GetFullPath(item.Path); }
                catch { existing = item.Path; }
                return string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void TreeView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedNode();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Up)
            {
                MoveSelectedItem(-1);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Down)
            {
                MoveSelectedItem(1);
                e.Handled = true;
            }
        }

        private void RemoveSelectedNode()
        {
            FolderNode node = treeView.SelectedNode;
            var foldersToRemove = selectedFolders.Where(folder => folders.Contains(folder)).ToList();
            ImageItem item = node == null ? null : node.Tag as ImageItem;
            var imagesToRemove = selectedImages
                .Where(image => folders.Any(folder => folder.Items.Contains(image)))
                .ToList();
            Dictionary<ImageItem, int> cellsBefore = ComputeCellNumbers(folders);

            if (foldersToRemove.Count > 0)
            {
                string message = foldersToRemove.Count == 1
                    ? Loc.T("message.deleteFolder", foldersToRemove[0].Name)
                    : Loc.T("message.deleteFolders", foldersToRemove.Count);
                if (!ShowDarkConfirm(Loc.T("dialog.deleteTitle"), message, Loc.T("button.delete"), Cursor.Position)) return;
                foreach (ImageFolder folder in foldersToRemove) folders.Remove(folder);
            }
            else if (imagesToRemove.Count > 0 || item != null)
            {
                if (imagesToRemove.Count == 0) imagesToRemove.Add(item);
                string message = imagesToRemove.Count == 1
                    ? Path.GetFileName(imagesToRemove[0].Path)
                    : Loc.T("message.deleteImages", imagesToRemove.Count);
                if (!ShowDarkConfirm(Loc.T("dialog.deleteImagesTitle"), message, Loc.T("button.delete"))) return;
                foreach (ImageItem selectedImage in imagesToRemove)
                {
                    ImageFolder owner = folders.FirstOrDefault(f => f.Items.Contains(selectedImage));
                    if (owner != null) owner.Items.Remove(selectedImage);
                }
            }
            else return;

            animationTimer.Stop();
            selectedFolders.Clear();
            selectedImages.Clear();
            folderSelectionAnchor = null;
            imageSelectionAnchor = null;
            RemapCellAssignments(cellsBefore, ComputeCellNumbers(folders));
            UpdateTree();
            QueuePreviewUpdate();
            CommitUndoableChange();
        }

        private void ClearAllWithConfirm()
        {
            if (folders.Count == 0) return;
            if (!ShowDarkConfirm(Loc.T("dialog.clearAllTitle"),
                Loc.T("message.clearAll"), Loc.T("button.clearAll"))) return;
            folders.Clear();
            selectedFolders.Clear();
            selectedImages.Clear();
            folderSelectionAnchor = null;
            imageSelectionAnchor = null;
            animationTimer.Stop();
            playToggleButton.Invalidate();
            DisposeAnimationFrames();
            nextFolderNumber = 1;
            animationIndex = 0;
            UpdateTree();
            QueuePreviewUpdate();
            CommitUndoableChange();
        }

        private void MoveSelectedItem(int direction)
        {
            FolderNode node = treeView.SelectedNode;
            ImageItem item = node == null ? null : node.Tag as ImageItem;
            if (item == null) return;

            ImageFolder folder = folders.FirstOrDefault(f => f.Items.Contains(item));
            if (folder == null) return;

            int oldIndex = folder.Items.IndexOf(item);
            int newIndex = oldIndex + direction;
            if (newIndex < 0 || newIndex >= folder.Items.Count) return;

            Dictionary<ImageItem, int> cellsBefore = ComputeCellNumbers(folders);
            int firstNumber = folder.Items.Min(i => i.ImageNumber);
            folder.Items.RemoveAt(oldIndex);
            folder.Items.Insert(newIndex, item);
            RemapCellAssignments(cellsBefore, ComputeCellNumbers(folders));

            // ツリーは作り直さず、動かしたノードを入れ替えるだけにする（全体の描き直しでちらつくため）。
            FolderNode parent = node.Parent;
            bool sameStructure = parent != null && parent.Nodes.Count == folder.Items.Count && ReferenceEquals(parent.Tag, folder);
            if (sameStructure)
            {
                treeView.BeginUpdate();
                try
                {
                    parent.Nodes.RemoveAt(oldIndex);
                    parent.Nodes.Insert(newIndex, node);
                    for (int i = Math.Min(oldIndex, newIndex); i <= Math.Max(oldIndex, newIndex); i++)
                    {
                        folder.Items[i].ImageNumber = firstNumber + i;
                        parent.Nodes[i].Text = folder.Items[i].ImageNumber.ToString("000") + "  " + Path.GetFileName(folder.Items[i].Path);
                    }
                    treeView.SelectedNode = node;
                }
                finally
                {
                    treeView.EndUpdate();
                }
                node.EnsureVisible();
            }
            else
            {
                UpdateTree();
                foreach (FolderNode folderNode in treeView.Nodes)
                    foreach (FolderNode itemNode in folderNode.Nodes)
                        if (ReferenceEquals(itemNode.Tag, item))
                        {
                            treeView.SelectedNode = itemNode;
                            itemNode.EnsureVisible();
                        }
            }

            QueuePreviewUpdateCore(null, true);   // 並びだけの変更なので、読み込み中の表示は出さない
            CommitUndoableChange();
        }

        private void UpdateTree()
        {
            object primarySelection = treeView.SelectedNode == null ? null : treeView.SelectedNode.Tag;
            var existingFolders = new HashSet<ImageFolder>();
            var expandedFolders = new HashSet<ImageFolder>();
            foreach (FolderNode existingNode in treeView.Nodes)
            {
                ImageFolder existingFolder = existingNode.Tag as ImageFolder;
                if (existingFolder == null) continue;
                existingFolders.Add(existingFolder);
                if (existingNode.IsExpanded) expandedFolders.Add(existingFolder);
            }
            selectedFolders.RemoveWhere(folder => !folders.Contains(folder));
            selectedImages.RemoveWhere(image => !folders.Any(folder => folder.Items.Contains(image)));
            if (folderSelectionAnchor != null && !folders.Contains(folderSelectionAnchor)) folderSelectionAnchor = null;
            if (imageSelectionAnchor != null && !folders.Any(folder => folder.Items.Contains(imageSelectionAnchor)))
                imageSelectionAnchor = null;

            treeView.BeginUpdate();
            treeView.Nodes.Clear();
            FolderNode primaryNode = null;

            int globalImageNumber = 1;

            foreach (var folder in folders)
            {
                var folderNode = new FolderNode(folder.Name);
                folderNode.Tag = folder;
                folderNode.ToolTipText = folder.Name;
                if (ReferenceEquals(primarySelection, folder)) primaryNode = folderNode;

                for (int i = 0; i < folder.Items.Count; i++)
                {
                    folder.Items[i].ImageNumber = globalImageNumber;
                    var itemNode = new FolderNode(globalImageNumber.ToString("000") + "  " + Path.GetFileName(folder.Items[i].Path));
                    itemNode.Tag = folder.Items[i];
                    itemNode.ToolTipText = folder.Items[i].Path;
                    if (ReferenceEquals(primarySelection, folder.Items[i])) primaryNode = itemNode;
                    folderNode.Nodes.Add(itemNode);
                    globalImageNumber++;
                }

                if (!existingFolders.Contains(folder) || expandedFolders.Contains(folder))
                    folderNode.Expand();
                treeView.Nodes.Add(folderNode);
            }

            treeView.EndUpdate();
            if (primaryNode != null) treeView.SelectedNode = primaryNode;
            treeView.Invalidate();
            // 空のときは受け皿（emptyDropZone）だけを出す。
            bool isEmpty = treeView.Nodes.Count == 0;
            emptyDropZone.Visible = isEmpty;
            treeView.Visible = !isEmpty;
            fileCountRow.Visible = !isEmpty;

            int count = GetAllItems().Count;
            statusLabel.Text = count == 0 ? Loc.T("status.addImages") : Loc.T("status.imageCount", count);
            fileCountLabel.Text = Loc.T("label.fileCount", folders.Count, count);
        }

        private static void DrawGridNumbers(Graphics g, IEnumerable<LayoutCell> cells)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using (var textBrush = new SolidBrush(Color.Black))
            using (var bgBrush = new SolidBrush(Color.FromArgb(230, Color.White)))
            {
                foreach (var cell in cells)
                {
                    Rectangle r = cell.Rect;
                    if (r.Width <= 0 || r.Height <= 0) continue;

                    string text = cell.CellNumber.ToString();
                    float shortestSide = Math.Max(1, Math.Min(r.Width, r.Height));
                    float fontSize = Math.Max(12.0f, Math.Min(256.0f, shortestSide * 0.18f));
                    float padding = Math.Max(3.0f, fontSize * 0.24f);
                    float lineWidth = Math.Max(1.0f, Math.Min(12.0f, shortestSide * 0.006f));

                    using (var pen = new Pen(Color.White, lineWidth))
                    using (var font = CreateFittingNumberFont(g, text, fontSize, Math.Max(1, r.Width - padding * 2)))
                    {
                        g.DrawRectangle(pen, r.X + lineWidth / 2, r.Y + lineWidth / 2,
                            Math.Max(0, r.Width - lineWidth), Math.Max(0, r.Height - lineWidth));

                        SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
                        float boxWidth = Math.Min(r.Width, size.Width + padding * 2);
                        float boxHeight = Math.Min(r.Height, size.Height + padding * 1.4f);
                        g.FillRectangle(bgBrush, r.X, r.Y, boxWidth, boxHeight);
                        g.DrawString(text, font, textBrush,
                            r.X + Math.Max(1, padding), r.Y + Math.Max(0, padding * 0.45f),
                            StringFormat.GenericTypographic);
                    }
                }
            }
        }

        private static Font CreateFittingNumberFont(Graphics g, string text, float requestedSize, float maxWidth)
        {
            Font font = UiFont.Create(requestedSize, FontStyle.Bold, GraphicsUnit.Pixel);
            SizeF measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            if (measured.Width <= maxWidth || measured.Width <= 0) return font;

            float fittedSize = Math.Max(8.0f, requestedSize * maxWidth / measured.Width);
            font.Dispose();
            return UiFont.Create(fittedSize, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void UpdatePreviewSafe()
        {
            if (colorPreviewCancellation != null) colorPreviewCancellation.Cancel();
            colorPreviewGeneration++;
            BeginLoading();
            try
            {
                UpdateSheetPreview();
                UpdateAnimationPreview();
            }
            catch (Exception ex)
            {
                DisposeAnimationFrames();
                sheetCanvas.SetImage(null, null);
                cellItems.Clear(); SyncMapAssets();
                ResetSheetChips();
                animCanvas.SetImage(null, null);
                statusLabel.Text = Loc.T("status.previewError", ex.Message.Replace("\r", " ").Replace("\n", " "));
            }
            finally
            {
                EndLoading();
                StartPlayerPreviewIfPossible();
            }
        }

        // await Task.Run(...)後の継続は、Application.Run()によるメッセージループが
        // 無い状況（テストハーネスのDoEvents()連打など）ではSynchronizationContextの
        // 捕捉に失敗し、UIスレッド以外で再開することがある。コントロール操作の前に
        // 必ずこれを通し、Control.InvokeでUIスレッドへ明示的に戻す。
        private void RunOnUiThread(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        // 表示が出ている読み込み（quiet でないもの）の数。並び替えなどの短い更新は quiet にして、
        // 「ロード中」の表示・カーソル・ボタンの無効化を出さない（毎回の点滅を避けるため）。
        private int visibleLoadingDepth;

        private void BeginLoading()
        {
            BeginLoadingCore(false);
        }

        private void BeginLoadingCore(bool quiet)
        {
            loadingDepth++;
            if (quiet) return;
            visibleLoadingDepth++;
            if (visibleLoadingDepth != 1) return;
            statusBeforeLoading = statusLabel.Text;
            statusLabel.Text = Loc.T("status.loading");
            loadingIndicatorLabel.Visible = true;
            UseWaitCursor = true;
            exportPngButton.Enabled = false;
            exportTgaButton.Enabled = false;
            exportGifButton.Enabled = false;
            exportWebPButton.Enabled = false;
            loadingIndicatorLabel.Update();
            if (statusLabel.Owner != null) statusLabel.Owner.Update();
        }

        private void EndLoading()
        {
            EndLoadingCore(false);
        }

        private void EndLoadingCore(bool quiet)
        {
            if (loadingDepth <= 0) return;
            loadingDepth--;
            if (quiet) return;
            if (visibleLoadingDepth > 0) visibleLoadingDepth--;
            if (visibleLoadingDepth != 0) return;
            UseWaitCursor = false;
            loadingIndicatorLabel.Visible = false;
            bool canExport = GetAllItems().Count > 0 && !exportBusy;
            exportPngButton.Enabled = canExport;
            exportTgaButton.Enabled = canExport;
            exportGifButton.Enabled = canExport;
            exportWebPButton.Enabled = canExport;
            if (statusLabel.Text == Loc.T("status.loading")) statusLabel.Text = statusBeforeLoading;
        }

        private void EndBlackTransparencyCooldown(object sender, EventArgs e)
        {
            blackTransparencyCooldownTimer.Stop();
            if (!blackTransparencyCheckBox.IsDisposed) blackTransparencyCheckBox.Enabled = true;
        }

        private void ShowColorAdjustmentDialog()
        {
            SpriteColorBlendMode originalMode = colorBlendMode;
            Color originalColor = spriteAdjustmentColor;
            int originalStrength = spriteAdjustmentStrength;
            using (var dialog = new ColorAdjustmentDialog(colorBlendMode, spriteAdjustmentColor, spriteAdjustmentStrength))
            {
                DialogMotion.Attach(dialog, this);
                dialog.AdjustmentChanged += (s, e) =>
                {
                    colorBlendMode = dialog.BlendMode;
                    spriteAdjustmentColor = dialog.SelectedColor;
                    spriteAdjustmentStrength = dialog.Strength;
                    UpdateColorAdjustmentButton();
                    QueueColorPreviewUpdate();
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    colorBlendMode = dialog.BlendMode;
                    spriteAdjustmentColor = dialog.SelectedColor;
                    spriteAdjustmentStrength = dialog.Strength;
                    UpdateColorAdjustmentButton();
                    QueueColorPreviewUpdate();
                    CommitUndoableChange();
                }
                else
                {
                    colorBlendMode = originalMode;
                    spriteAdjustmentColor = originalColor;
                    spriteAdjustmentStrength = originalStrength;
                    UpdateColorAdjustmentButton();
                    QueueColorPreviewUpdate();
                }
            }
        }

        private void UpdateColorAdjustmentButton()
        {
            colorAdjustmentButton.AccessibleName = Loc.T(colorBlendMode == SpriteColorBlendMode.Multiply ? "blend.multiply" : "blend.add") +
                " #" + spriteAdjustmentColor.R.ToString("X2") + spriteAdjustmentColor.G.ToString("X2") +
                spriteAdjustmentColor.B.ToString("X2") + " " + spriteAdjustmentStrength + "%";
            colorAdjustmentButton.Invalidate();
            RefreshMapImages();
        }

        private void ColorAdjustmentButton_Paint(object sender, PaintEventArgs e)
        {
            int swatchSize = Math.Min(26, Math.Max(18, colorAdjustmentButton.ClientSize.Height - 14));
            Rectangle swatchRect = new Rectangle(10,
                (colorAdjustmentButton.ClientSize.Height - swatchSize) / 2, swatchSize, swatchSize);
            const int checker = 5;
            for (int y = 0; y < swatchRect.Height; y += checker)
            {
                for (int x = 0; x < swatchRect.Width; x += checker)
                {
                    bool light = ((x / checker) + (y / checker)) % 2 == 0;
                    using (var brush = new SolidBrush(light ? Color.FromArgb(92, 98, 104) : Color.FromArgb(52, 59, 65)))
                        e.Graphics.FillRectangle(brush, swatchRect.X + x, swatchRect.Y + y,
                            Math.Min(checker, swatchRect.Width - x), Math.Min(checker, swatchRect.Height - y));
                }
            }
            using (var colorBrush = new SolidBrush(spriteAdjustmentColor)) e.Graphics.FillRectangle(colorBrush, swatchRect);
            using (var border = new Pen(Color.FromArgb(170, 180, 188))) e.Graphics.DrawRectangle(border,
                swatchRect.X, swatchRect.Y, swatchRect.Width - 1, swatchRect.Height - 1);

            string mode = Loc.T(colorBlendMode == SpriteColorBlendMode.Multiply ? "blend.multiply" : "blend.add");
            Rectangle textRect = new Rectangle(swatchRect.Right + 10, 0,
                Math.Max(0, colorAdjustmentButton.ClientSize.Width - swatchRect.Right - 16),
                colorAdjustmentButton.ClientSize.Height);
            TextRenderer.DrawText(e.Graphics, mode, colorAdjustmentButton.Font, textRect, lightText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private async void QueueColorPreviewUpdate()
        {
            if (closing || IsDisposed) return;
            CancellationTokenSource previous = colorPreviewCancellation;
            colorPreviewCancellation = new CancellationTokenSource();
            if (previous != null)
            {
                previous.Cancel();
                previous.Dispose();
            }
            CancellationTokenSource current = colorPreviewCancellation;
            CancellationToken token = current.Token;
            int generation = ++colorPreviewGeneration;
            bool gateEntered = false;
            BeginLoading();
            try
            {
                // 1フレーム分だけまとめ、ドラッグ中は常に最後の入力を優先する。
                await Task.Delay(16, token);
                await colorPreviewGate.WaitAsync(token);
                gateEntered = true;
                token.ThrowIfCancellationRequested();
                if (GetAllItems().Count == 0) return;
                List<LoadFolderRequest> request = CaptureLoadRequest();
                int divisor = GetScaleDivisor();
                bool convertBlack = blackTransparencyCheckBox.Checked;
                SpriteColorBlendMode blendMode = colorBlendMode;
                Color adjustmentColor = spriteAdjustmentColor;
                int adjustmentStrength = spriteAdjustmentStrength;
                List<LoadedFolder> prepared = null;
                try
                {
                    prepared = await Task.Run(() => imagePipeline.Load(request, divisor, convertBlack,
                        blendMode, adjustmentColor, adjustmentStrength, token), token);
                    token.ThrowIfCancellationRequested();
                    if (closing || generation != colorPreviewGeneration) return;
                    List<LoadedFolder> applying = prepared;
                    prepared = null;
                    RunOnUiThread(() => RunAutomatic(() =>
                    {
                        UpdateSheetPreview(applying);
                        UpdateAnimationPreview();
                    }));
                }
                finally
                {
                    if (prepared != null) SpriteImagePipeline.DisposeLoaded(prepared);
                }
            }
            catch (OperationCanceledException)
            {
                // 高速操作で古くなった処理は正常に破棄する。
            }
            catch (Exception ex)
            {
                if (!closing) statusLabel.Text = Loc.T("status.colorError", ex.Message.Replace("\r", " ").Replace("\n", " "));
            }
            finally
            {
                // フォーム終了処理と競合すると、この継続がOnFormClosedでの
                // colorPreviewGate.Dispose()より後に走ることがある。セマフォの
                // 解放漏れは終了時なら無害なので、破棄済み例外は握りつぶす。
                if (gateEntered)
                {
                    try { colorPreviewGate.Release(); }
                    catch (ObjectDisposedException) { }
                }
                if (!closing) EndLoading();
            }
        }

        // ツリー編集（追加・削除・並べ替え・リネームなど）からのプレビュー更新を
        // UIスレッドを止めずに行う。QueueColorPreviewUpdateと同じキャンセル・世代
        // 管理を共有するため、色調整のライブプレビューと競合しても新しい方が勝つ。
        // Undoの復元やGIF書き出し前のフレーム確定など、直後にanimationFramesを
        // 同期的に読む必要がある箇所は従来どおりUpdatePreviewSafe()を使うこと。
        private void QueuePreviewUpdate(string statusNote = null)
        {
            QueuePreviewUpdateCore(statusNote, false);
        }

        private async void QueuePreviewUpdateCore(string statusNote, bool quiet)
        {
            if (statusNote != null) pendingPreviewNote = statusNote;
            if (closing || IsDisposed) return;
            CancellationTokenSource previous = colorPreviewCancellation;
            colorPreviewCancellation = new CancellationTokenSource();
            if (previous != null)
            {
                previous.Cancel();
                previous.Dispose();
            }
            CancellationTokenSource current = colorPreviewCancellation;
            CancellationToken token = current.Token;
            int generation = ++colorPreviewGeneration;
            bool gateEntered = false;
            BeginLoadingCore(quiet);
            try
            {
                await Task.Delay(16, token);
                await colorPreviewGate.WaitAsync(token);
                gateEntered = true;
                token.ThrowIfCancellationRequested();
                if (closing || IsDisposed || Disposing) return;
                if (GetAllItems().Count == 0)
                {
                    RunOnUiThread(() => RunAutomatic(() =>
                    {
                        DisposeAnimationFrames();
                        sheetCanvas.SetImage(null, null);
                cellItems.Clear(); SyncMapAssets();
                        ResetSheetChips();
                        imagePipeline.ClearCache();
                        UpdateAnimationPreview();
                        ApplyPendingPreviewNote();
                    }));
                    return;
                }
                List<LoadFolderRequest> request = CaptureLoadRequest();
                int divisor = GetScaleDivisor();
                bool convertBlack = blackTransparencyCheckBox.Checked;
                SpriteColorBlendMode blendMode = colorBlendMode;
                Color adjustmentColor = spriteAdjustmentColor;
                int adjustmentStrength = spriteAdjustmentStrength;
                List<LoadedFolder> prepared = null;
                try
                {
                    prepared = await Task.Run(() => imagePipeline.Load(request, divisor, convertBlack,
                        blendMode, adjustmentColor, adjustmentStrength, token), token);
                    token.ThrowIfCancellationRequested();
                    if (closing || generation != colorPreviewGeneration) return;
                    List<LoadedFolder> applying = prepared;
                    prepared = null;
                    RunOnUiThread(() => RunAutomatic(() =>
                    {
                        UpdateSheetPreview(applying);
                        UpdateAnimationPreview();
                        ApplyPendingPreviewNote();
                    }));
                }
                finally
                {
                    if (prepared != null) SpriteImagePipeline.DisposeLoaded(prepared);
                }
            }
            catch (OperationCanceledException)
            {
                // 高速操作で古くなった処理は正常に破棄する。
            }
            catch (Exception ex)
            {
                if (!closing) statusLabel.Text = Loc.T("status.previewError", ex.Message.Replace("\r", " ").Replace("\n", " "));
            }
            finally
            {
                // フォーム終了処理と競合すると、この継続がOnFormClosedでの
                // colorPreviewGate.Dispose()より後に走ることがある。セマフォの
                // 解放漏れは終了時なら無害なので、破棄済み例外は握りつぶす。
                if (gateEntered)
                {
                    try { colorPreviewGate.Release(); }
                    catch (ObjectDisposedException) { }
                }
                if (!closing)
                {
                    EndLoadingCore(quiet);
                    StartPlayerPreviewIfPossible();
                }
            }
        }

        private void ApplyPendingPreviewNote()
        {
            if (pendingPreviewNote == null) return;
            statusLabel.Text += "  |  " + pendingPreviewNote;
            pendingPreviewNote = null;
        }

        private void UpdateSheetPreview()
        {
            if (GetAllItems().Count == 0)
            {
                DisposeAnimationFrames();
                sheetCanvas.SetImage(null, null);
                cellItems.Clear(); SyncMapAssets();
                ResetSheetChips();
                imagePipeline.ClearCache();
                return;
            }

            UpdateSheetPreview(LoadBitmapsByFolder());
        }

        private const long PreviewSheetPixelBudget = 64L * 1000 * 1000;

        private static int ScaleUp(int value, int divisor)
        {
            return (int)(((long)value + divisor - 1) / divisor);
        }

        // シート全体を1枚のビットマップにするプレビューの縮小率。読み込み時の縮小率（shrink）以上で、
        // 画素数とGDI+の寸法の上限に収まる最小の整数。
        internal static int ChoosePreviewSheetScale(int width, int height, int shrink)
        {
            int scale = Math.Max(1, shrink);
            long pixels = (long)width * height;
            if (pixels > PreviewSheetPixelBudget)
                scale = Math.Max(scale, (int)Math.Ceiling(Math.Sqrt(pixels / (double)PreviewSheetPixelBudget)));
            scale = Math.Max(scale, ScaleUp(width, 32767));
            scale = Math.Max(scale, ScaleUp(height, 32767));
            while ((long)ScaleUp(width, scale) * ScaleUp(height, scale) > PreviewSheetPixelBudget) scale++;
            return scale;
        }

        private static Rectangle ScaleRectOutward(Rectangle rect, int divisor)
        {
            int left = rect.Left / divisor;
            int top = rect.Top / divisor;
            int right = ScaleUp(rect.Right, divisor);
            int bottom = ScaleUp(rect.Bottom, divisor);
            return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        private void ResetSheetChips()
        {
            sheetCellsChip.Text = "—";
            sheetSizeChipWanted = false;
            FitSheetHeader();
        }

        // シート見出しの左側（タイトル・セル数・サイズ）を、幅に収まる分だけ表示する。
        private void FitSheetHeader()
        {
            if (sheetHeaderLeft == null || sheetTitleLabel == null) return;
            Func<Control, int> width = c => c.Margin.Horizontal + c.PreferredSize.Width;
            // 幅が足りないとき（文言が長い言語・狭いペイン）は、見出しの文字を守るため、右端の倍率チップを先に隠す。
            int zoomChipWidth = width(sheetZoomChip);
            int total = sheetHeaderLeft.ClientSize.Width + (sheetZoomChip.Visible ? zoomChipWidth : 0);
            bool showZoom = total - zoomChipWidth >= width(sheetTitleLabel);
            if (sheetZoomChip.Visible != showZoom)
            {
                sheetZoomChip.Visible = showZoom;
                // 右側の枠は自動で幅が決まるが、この変更の途中では組み直されないので、あとで組み直してから測り直す。
                TryBeginInvoke(() =>
                {
                    if (sheetZoomChip.Parent == null) return;
                    sheetZoomChip.Parent.PerformLayout();
                    if (sheetZoomChip.Parent.Parent != null) sheetZoomChip.Parent.Parent.PerformLayout();
                    FitSheetHeader();
                RefreshMapHeaders();
                });
            }
            int available = total - (showZoom ? zoomChipWidth : 0);
            int used = width(sheetTitleLabel);
            bool showCells = used + width(sheetCellsChip) <= available;
            if (showCells) used += width(sheetCellsChip);
            bool showSize = sheetSizeChipWanted && showCells && used + width(sheetSizeChip) <= available;
            sheetCellsChip.Visible = showCells;
            sheetSizeChip.Visible = showSize;
        }

        private void UpdateSheetPreview(List<LoadedFolder> loaded)
        {
            try
            {
                SheetLayout layout = BuildLayout(loaded, Math.Max(1, (int)columnsBox.Value), align4CheckBox.Checked);
                // 大きすぎるときは、読み込みの段階でプレビュー用に縮小してある（Shrink）。座標や表示上の大きさは
                // 書き出しと同じ原寸のまま扱い、絵だけを縮小した画像から拡大して描く。
                int shrink = loaded.SelectMany(f => f.Items).Select(i => i.Shrink).DefaultIfEmpty(1).Max();
                int previewScale = ChoosePreviewSheetScale(layout.Width, layout.Height, shrink);
                Bitmap preview = new Bitmap(ScaleUp(layout.Width, previewScale), ScaleUp(layout.Height, previewScale),
                    PixelFormat.Format32bppArgb);

                using (Graphics g = Graphics.FromImage(preview))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                    foreach (var placement in layout.Placements)
                    {
                        if (placement.Bitmap == null) continue;
                        if (previewScale == 1)
                            g.DrawImageUnscaled(placement.Bitmap, placement.Rect.X, placement.Rect.Y);
                        else
                            g.DrawImage(placement.Bitmap, ScaleRectOutward(placement.Rect, previewScale));
                    }
                }

                var rects = layout.Cells.Select(c => new PreviewRect
                {
                    Rect = c.Rect,
                    Number = c.CellNumber,
                    BorderColor = GetCellAssignmentColor(c.CellNumber)
                }).ToList();

                RebuildCellMap(layout);
                sheetCanvas.SetImage(preview, rects, new Size(layout.Width, layout.Height));
                RebuildAnimationFrames(layout, previewScale);
                int itemCount = GetAllItems().Count;
                lastSheetSize = new Size(layout.Width, layout.Height);
                lastPreviewScale = previewScale;
                statusLabel.Text = Loc.T("status.sheetSummary", itemCount, layout.Width, layout.Height);
                if (previewScale > 1) statusLabel.Text += "  |  " + Loc.T("message.previewShrunk", previewScale);
                int sheetColumns = Math.Max(1, (int)columnsBox.Value);
                int sheetRows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)sheetColumns);
                sheetCellsChip.Text = itemCount == 0 ? "—" : Loc.T("chip.cells", sheetColumns, sheetRows);
                sheetSizeChip.Text = layout.Width + " × " + layout.Height + " px";
                UpdateFillEmptyCellsButton();
                sheetSizeChipWanted = itemCount > 0;
                FitSheetHeader();
                RefreshMapHeaders();
            }
            finally
            {
                SpriteImagePipeline.DisposeLoaded(loaded);
            }
        }

        private Size lastSheetSize;
        private int lastPreviewScale = 1;

        // 言語が変わったとき、すでに表示している状態欄・見出しのチップ・操作案内・プレビュー見出しを新しい言語で組み直す。
        private void RefreshLocalizedStatus()
        {
            if (closing || IsDisposed || loadingDepth > 0 || exportBusy) return;
            int itemCount = GetAllItems().Count;
            if (itemCount == 0)
            {
                statusLabel.Text = Loc.T("status.addImages");
            }
            else if (!lastSheetSize.IsEmpty)
            {
                statusLabel.Text = Loc.T("status.sheetSummary", itemCount, lastSheetSize.Width, lastSheetSize.Height);
                if (lastPreviewScale > 1) statusLabel.Text += "  |  " + Loc.T("message.previewShrunk", lastPreviewScale);
                int columns = Math.Max(1, (int)columnsBox.Value);
                sheetCellsChip.Text = Loc.T("chip.cells", columns, (int)Math.Ceiling(itemCount / (double)columns));
            }
            fileCountLabel.Text = Loc.T("label.fileCount", folders.Count, itemCount);
            UpdateZoomLabel();
            FitSheetHeader();
            UpdateAnimationPreview();
            FlushAnimInfo();
        }

        private void UpdateAnimationPreview()
        {
            if (previewTargetMode == PreviewTargetMode.Map) { if (mapCanvas != null) mapCanvas.Invalidate(); return; }
            sceneKeyValid = false;
            if (animationFrames.Count == 0)
            {
                animCanvas.SetImage(null, null);
                animInfoLabel.Text = Loc.T("anim.noImages");
                return;
            }

            if (previewTargetMode == PreviewTargetMode.Standard)
            {
                if (animationIndex >= animationFrames.Count) animationIndex = 0;
                bool frameInRange = IsFrameInRange(animationIndex);
                if (!frameInRange) animationIndex = GetFirstPlayableFrameIndex();
                AnimationFrameCache standardFrame = animationFrames[animationIndex];
                animCanvas.SetImageFromSource(EnsureFrameImage(standardFrame), new List<PreviewRect>
                {
                    new PreviewRect
                    {
                        Rect = new Rectangle(0, 0, standardFrame.NominalSize.Width, standardFrame.NominalSize.Height),
                        Number = standardFrame.CellNumber
                    }
                }, standardFrame.NominalSize);
                SetAnimInfo(Loc.T("anim.frame", animationIndex + 1, animationFrames.Count, standardFrame.CellNumber) +
                    (frameInRange ? "" : Loc.T("anim.outOfRange")));
                return;
            }

            int cell = previewTargetMode == PreviewTargetMode.Player
                ? playerAnimator.CurrentCell
                : effectCurrentCell;
            AnimationFrameCache cache = FindAnimationFrame(cell) ?? animationFrames[0];
            PointF position = previewTargetMode == PreviewTargetMode.Player
                ? playerState.Position
                : effectMotion.Position;
            RectangleF collider = RectangleF.Empty;
            if (previewTargetMode == PreviewTargetMode.Player && playerColliderVisibleCheckBox.Checked)
            {
                // コライダーは当たり判定の位置（接地オフセットで画像をずらす前）に、画像の中心に合わせて描く。
                Size display = SimulationDisplaySizeOf(cache.NominalSize);
                SizeF box = PlayerColliderSceneSize(cache.NominalSize);
                collider = new RectangleF(position.X + display.Width * 0.5f - box.Width * 0.5f,
                    position.Y + display.Height * 0.5f - box.Height * 0.5f, box.Width, box.Height);
            }
            animCanvas.SetSceneCollider(collider);
            if (previewTargetMode == PreviewTargetMode.Player)
                position.Y += GroundContact.ToSceneOffset((int)playerGroundOffsetBox.Value,
                    cache.NominalSize.Height, SimulationDisplaySizeOf(cache.NominalSize));
            bool mirror = previewTargetMode == PreviewTargetMode.Player &&
                mirrorMissingDirectionsCheckBox.Checked && playerAnimator.ShouldMirrorForFacing(playerState.FacingRight);
            animCanvas.ShowFloor = previewTargetMode == PreviewTargetMode.Player;   // エフェクトに足場は不要
            animCanvas.SetScene(EnsureFrameImage(cache), SimulationSceneSize, position,
                SimulationDisplaySizeOf(cache.NominalSize), accentColor, Terrain.GroundThicknessRatio, mirror);
            animCanvas.SetTerrain(previewTargetMode == PreviewTargetMode.Player ? playerState.Terrain : null);
            lastSceneKey = CaptureSceneKey();
            sceneKeyValid = true;

            if (previewTargetMode == PreviewTargetMode.Player)
            {
                string stateText = GetStateDisplayName(playerAnimator.RequestedState);
                if (playerAnimator.RequestedState != playerAnimator.PlayingState)
                    stateText += Loc.T("anim.stateAlt", GetStateDisplayName(playerAnimator.PlayingState));
                if (mirror) stateText += Loc.T("anim.mirrored");
                SetAnimInfo(Loc.T("anim.state", stateText, cache.CellNumber));
            }
            else
            {
                SetAnimInfo(Loc.T("anim.effect", cache.CellNumber,
                    effectDirectionXBox.Value.ToString("0.00"), effectDirectionYBox.Value.ToString("0.00")));
            }
        }

        // 再生中はコマが変わるたびに見出しのチップを作り直すと重いので、文字の更新は0.1秒に1回までにする。
        // 再生が止まる操作では FlushAnimInfo で最新の文字へそろえる。
        private bool tickDriven;
        private int lastInfoTick;
        private string pendingInfoText;

        private void SetAnimInfo(string text)
        {
            pendingInfoText = text;
            if (tickDriven && unchecked(Environment.TickCount - lastInfoTick) < 100) return;
            FlushAnimInfo();
        }

        private void FlushAnimInfo()
        {
            if (pendingInfoText != null && animInfoLabel.Text != pendingInfoText) animInfoLabel.Text = pendingInfoText;
            pendingInfoText = null;
            lastInfoTick = Environment.TickCount;
        }

        private void ResetSimulation()
        {
            previewInput.Clear();
            int maxCell = GetMaxCellNumber();
            playerAnimator.Reset(PlayerAnimationState.Idle, maxCell);
            AnimationFrameCache first = animationFrames.Count == 0 ? null : animationFrames[0];
            Size spriteSize = first == null ? new Size(64, 64) : SimulationDisplaySizeOf(first.NominalSize);
            playerState.Reset(SimulationSceneSize, spriteSize);
            effectMotion.Reset(SimulationSceneSize, spriteSize);
            ResetEffectPlayback();
            lastSimulationTickUtc = DateTime.UtcNow;
        }

        private void ResetEffectPlayback()
        {
            int maxCell = GetMaxCellNumber();
            effectCurrentCell = Math.Max(1, Math.Min(maxCell, effectClip.StartCell));
            effectFrameAccumulator = 0;
            AnimationFrameCache current = FindAnimationFrame(effectCurrentCell) ??
                (animationFrames.Count == 0 ? null : animationFrames[0]);
            effectMotion.Reset(SimulationSceneSize, current == null ? new Size(64, 64) : SimulationDisplaySizeOf(current.NominalSize));
        }

        private void AdvanceEffectAnimation(float seconds)
        {
            int maxCell = GetMaxCellNumber();
            int start = Math.Max(1, Math.Min(maxCell, effectClip.StartCell));
            int end = Math.Max(start, Math.Min(maxCell, effectClip.EndCell));
            effectFrameAccumulator += seconds;
            double frameDuration = 1.0 / Math.Max(1, effectClip.Fps);
            while (effectFrameAccumulator >= frameDuration)
            {
                effectFrameAccumulator -= frameDuration;
                effectCurrentCell++;
                if (effectCurrentCell < start || effectCurrentCell > end) effectCurrentCell = start;
            }
        }

        private int GetMaxCellNumber()
        {
            return animationFrames.Count == 0 ? 1 : animationFrames.Max(frame => frame.CellNumber);
        }

        private void UpdateSimulationRangeMax()
        {
            int availableMaxCell = GetMaxCellNumber();
            const int maxCell = 999;
            bool initialize = !simulationRangesInitialized && animationFrames.Count > 0;
            foreach (AnimationClipSettings clip in playerClips.Values)
            {
                clip.StartCell = Math.Max(1, Math.Min(999, clip.StartCell));
                clip.EndCell = initialize && clip.Enabled
                    ? Math.Min(999, availableMaxCell)
                    : Math.Max(clip.StartCell, Math.Min(999, clip.EndCell));
            }
            effectClip.StartCell = Math.Max(1, Math.Min(999, effectClip.StartCell));
            effectClip.EndCell = initialize
                ? Math.Min(999, availableMaxCell)
                : Math.Max(effectClip.StartCell, Math.Min(999, effectClip.EndCell));
            simulationRangesInitialized = simulationRangesInitialized || animationFrames.Count > 0;
            EnsureColliderDefaults();

            syncingStateEditor = true;
            stateStartBox.Maximum = maxCell;
            stateEndBox.Maximum = maxCell;
            effectStartBox.Maximum = maxCell;
            effectEndBox.Maximum = maxCell;
            syncingStateEditor = false;
            LoadSelectedStateEditor();
            LoadEffectEditor();
            RefreshStateTransitionEditors();
            UpdateSheetRangeColors();
        }

        private void UpdateSheetRangeColors()
        {
            sheetCanvas.SetRectColors(GetCellAssignmentColor);
        }

        private Color GetCellAssignmentColor(int cell)
        {
            if (previewTargetMode == PreviewTargetMode.Standard) return Color.Empty;
            if (previewTargetMode == PreviewTargetMode.Effect)
            {
                if (cell >= effectClip.StartCell && cell <= effectClip.EndCell)
                    return Color.FromArgb(196, 100, 255);
                return Color.Empty;
            }

            if (ClipContains(PlayerAnimationState.AttackRight1, cell) ||
                ClipContains(PlayerAnimationState.AttackRight2, cell) ||
                ClipContains(PlayerAnimationState.AttackLeft1, cell) ||
                ClipContains(PlayerAnimationState.AttackLeft2, cell)) return Color.FromArgb(70, 145, 255);
            if (ClipContains(PlayerAnimationState.Jump, cell) ||
                ClipContains(PlayerAnimationState.JumpStart, cell) ||
                ClipContains(PlayerAnimationState.JumpAir, cell) ||
                ClipContains(PlayerAnimationState.JumpLand, cell) ||
                ClipContains(PlayerAnimationState.JumpRightStart, cell) ||
                ClipContains(PlayerAnimationState.JumpRightAir, cell) ||
                ClipContains(PlayerAnimationState.JumpRightLand, cell) ||
                ClipContains(PlayerAnimationState.JumpLeftStart, cell) ||
                ClipContains(PlayerAnimationState.JumpLeftAir, cell) ||
                ClipContains(PlayerAnimationState.JumpLeftLand, cell)) return Color.FromArgb(255, 205, 64);
            if (ClipContains(PlayerAnimationState.MoveLeft, cell) ||
                ClipContains(PlayerAnimationState.MoveRight, cell) ||
                ClipContains(PlayerAnimationState.MoveUp, cell) ||
                ClipContains(PlayerAnimationState.MoveDown, cell) ||
                ClipContains(PlayerAnimationState.Move, cell)) return Color.FromArgb(255, 83, 91);
            if (ClipContains(PlayerAnimationState.Idle, cell)) return Color.FromArgb(83, 210, 135);
            return Color.Empty;
        }

        private bool ClipContains(PlayerAnimationState state, int cell)
        {
            AnimationClipSettings clip;
            return playerClips.TryGetValue(state, out clip) && clip.Enabled &&
                   cell >= clip.StartCell && cell <= clip.EndCell;
        }

        private AnimationFrameCache FindAnimationFrame(int cell)
        {
            return animationFrames.FirstOrDefault(frame => frame.CellNumber == cell);
        }

        private static Size GetSimulationDisplaySize(Bitmap sprite)
        {
            return sprite == null ? new Size(64, 64) : SimulationDisplaySizeOf(sprite.Size);
        }

        // コライダー表示がオフの間は、大きさを変えられないよう灰色にする。
        private void UpdateColliderInputsEnabled()
        {
            if (playerColliderWidthHost == null) return;
            bool enabled = playerColliderVisibleCheckBox.Checked;
            playerColliderWidthHost.Enabled = enabled;
            playerColliderHeightHost.Enabled = enabled;
            playerColliderWidthLabel.ForeColor = enabled ? lightText : disabledText;
            playerColliderHeightLabel.ForeColor = enabled ? lightText : disabledText;
        }

        // コライダーの大きさが未設定（0）なら、最初の画像に合わせた値（幅は半分・高さは同じ、画像ピクセル）を入れる。
        // 画像を読み込んだだけで未保存の印が付かないよう、変更前に保存済みと同じ状態だったなら基準も合わせて動かす。
        private void EnsureColliderDefaults()
        {
            if (animationFrames.Count == 0 || restoringState) return;
            if (playerColliderWidthBox.Value != 0 && playerColliderHeightBox.Value != 0) return;
            Size image = animationFrames[0].NominalSize;
            if (image.Width <= 0 || image.Height <= 0) return;
            bool wasClean = projectBaseline != null && !IsProjectDirty();
            bool wasSyncing = syncingStateEditor;
            syncingStateEditor = true;   // ここでの値の変更は、取り消しの1手として記録しない
            try
            {
                if (playerColliderWidthBox.Value == 0)
                    playerColliderWidthBox.Value = ClampDecimal(Math.Max(1, (int)Math.Round(image.Width * 0.5)), 1, playerColliderWidthBox.Maximum);
                if (playerColliderHeightBox.Value == 0)
                    playerColliderHeightBox.Value = ClampDecimal(image.Height, 1, playerColliderHeightBox.Maximum);
            }
            finally
            {
                syncingStateEditor = wasSyncing;
            }
            if (wasClean) projectBaseline = CaptureState();
        }

        private SizeF PlayerColliderSceneSize(Size imageSize)
        {
            return ColliderSize.ToScene((int)playerColliderWidthBox.Value, (int)playerColliderHeightBox.Value,
                imageSize, SimulationDisplaySizeOf(imageSize));
        }

        private static Size SimulationDisplaySizeOf(Size sprite)
        {
            if (sprite.Width <= 0 || sprite.Height <= 0) return new Size(64, 64);
            int targetHeight = Math.Max(1, (int)Math.Round(SimulationSceneSize.Height * 0.28f));
            float scale = targetHeight / (float)sprite.Height;
            int width = Math.Max(1, (int)Math.Round(sprite.Width * scale));
            int maxWidth = (int)Math.Round(SimulationSceneSize.Width * 0.42f);
            if (width > maxWidth)
            {
                scale = maxWidth / (float)sprite.Width;
                width = maxWidth;
                targetHeight = Math.Max(1, (int)Math.Round(sprite.Height * scale));
            }
            return new Size(width, targetHeight);
        }

        // 再生用のコマ画像を最初に全部作っておく大きさの上限（バイト）。これを超えるときは必要なときに作る。
        // テストで小さくして、必要なときに作る経路を確かめられるよう、インスタンスの値にしてある。
        private long eagerFrameBytes = 64L * 1024 * 1024;
        private const long LiveFrameBytes = 64L * 1024 * 1024;
        private readonly Queue<AnimationFrameCache> liveFrames = new Queue<AnimationFrameCache>();
        private long liveFrameTotal;

        private static long FrameBytes(AnimationFrameCache frame)
        {
            return (long)frame.SheetRect.Width * frame.SheetRect.Height * 4;
        }

        // コマの画像を返す。まだ無ければシート画像から切り出して作り、保持する合計が上限を超えたら古いものを捨てる。
        private Bitmap EnsureFrameImage(AnimationFrameCache frame)
        {
            if (frame.Image != null) return frame.Image;
            frame.Image = sheetCanvas.CloneImageRegion(frame.SheetRect);
            if (frame.Image == null) return null;
            liveFrames.Enqueue(frame);
            liveFrameTotal += FrameBytes(frame);
            while (liveFrameTotal > LiveFrameBytes && liveFrames.Count > 1)
            {
                AnimationFrameCache oldest = liveFrames.Dequeue();
                liveFrameTotal -= FrameBytes(oldest);
                if (oldest.Image != null)
                {
                    oldest.Image.Dispose();
                    oldest.Image = null;
                }
            }
            return frame.Image;
        }

        private void RebuildAnimationFrames(SheetLayout layout, int previewScale)
        {
            DisposeAnimationFrames();
            previewScale = Math.Max(1, previewScale);

            long totalBytes = 0;
            foreach (var placement in layout.Placements)
            {
                var frame = new AnimationFrameCache
                {
                    SheetRect = previewScale == 1 ? placement.CellRect : ScaleRectOutward(placement.CellRect, previewScale),
                    NominalSize = placement.CellRect.Size,
                    CellNumber = placement.CellNumber
                };
                totalBytes += FrameBytes(frame);
                animationFrames.Add(frame);
            }

            // シート画像と同じ絵を二重に持たないよう、大きいときはコマ画像を先に作らない。
            if (totalBytes <= eagerFrameBytes)
                foreach (AnimationFrameCache frame in animationFrames)
                    frame.Image = sheetCanvas.CloneImageRegion(frame.SheetRect);

            UpdateRangeBoxMax();

            if (animationIndex >= animationFrames.Count)
            {
                animationIndex = 0;
            }

            if (!IsFrameInRange(animationIndex))
            {
                animationIndex = GetFirstPlayableFrameIndex();
            }

            UpdateSimulationRangeMax();
            ResetSimulation();
        }

        private void DisposeAnimationFrames()
        {
            foreach (var frame in animationFrames)
            {
                if (frame.Image != null)
                {
                    frame.Image.Dispose();
                }
            }

            animationFrames.Clear();
            liveFrames.Clear();
            liveFrameTotal = 0;

            if (!closing && !startCellBox.IsDisposed && !endCellBox.IsDisposed)
            {
                startCellBox.Value = 1;
                endCellBox.Value = 1;
            }
        }

        private List<LoadedFolder> LoadBitmapsByFolder()
        {
            return imagePipeline.Load(CaptureLoadRequest(), GetScaleDivisor(), blackTransparencyCheckBox.Checked,
                colorBlendMode, spriteAdjustmentColor, spriteAdjustmentStrength, CancellationToken.None);
        }

        private List<LoadFolderRequest> CaptureLoadRequest()
        {
            var request = new List<LoadFolderRequest>();
            foreach (ImageFolder folder in folders)
            {
                var folderRequest = new LoadFolderRequest { Name = folder.Name };
                foreach (ImageItem item in folder.Items)
                    folderRequest.Items.Add(new LoadItemRequest { Source = item, Path = item.Path });
                request.Add(folderRequest);
            }
            return request;
        }

        //--------------
        // 空きセル（フォルダごとに新しい行から始まるので、各フォルダの最後の行に残る空きのマス）
        //--------------
        private List<int> FolderCounts() { return folders.Select(f => f.Items.Count).Where(n => n > 0).ToList(); }

        internal static int EmptyCells(IEnumerable<int> counts, int columns)
        {
            columns = Math.Max(1, columns);
            return counts.Sum(n => (n + columns - 1) / columns * columns - n);
        }

        // 空きセルが出ない横セル数（どのフォルダのコマ数でも割り切れる数）のうち、今の横セル数に一番近いもの。
        internal static int ColumnsWithoutEmptyCells(IList<int> counts, int current, int maxColumns = int.MaxValue)
        {
            if (counts.Count == 0) return current;
            int g = counts.Aggregate((a, b) => { while (b != 0) { int r = a % b; a = b; b = r; } return a; });
            int best = 1;
            // 横セル数の入力欄で設定できる数（上限 maxColumns）の中から選ぶ（Codex 監査 2026-10-06）。
            for (int d = 1; d <= Math.Min(g, maxColumns); d++)
                if (g % d == 0 && (Math.Abs(d - current) < Math.Abs(best - current) || (Math.Abs(d - current) == Math.Abs(best - current) && d > best))) best = d;
            return best;
        }

        private void UpdateFillEmptyCellsButton()
        {
            bool show = previewTargetMode != PreviewTargetMode.Map && EmptyCells(FolderCounts(), (int)columnsBox.Value) > 0;
            if (fillEmptyCellsButton.Visible != show) fillEmptyCellsButton.Visible = show;
        }

        private void FillEmptyCells()
        {
            int columns = ColumnsWithoutEmptyCells(FolderCounts(), (int)columnsBox.Value, (int)columnsBox.Maximum);
            columnsBox.Value = Math.Max(columnsBox.Minimum, Math.Min(columnsBox.Maximum, columns));
            statusLabel.Text = Loc.T("status.emptyCellsFilled", (int)columnsBox.Value);
        }

        private SheetLayout BuildLayout(List<LoadedFolder> loadedFolders, int columns, bool align4 = false)
        {
            int maxColumnsPerRow = Math.Max(1, columns);
            var rows = new List<LayoutRow>();

            foreach (var folder in loadedFolders)
            {
                var current = new LayoutRow();

                foreach (var item in folder.Items)
                {
                    if (current.Items.Count >= maxColumnsPerRow)
                    {
                        rows.Add(current);
                        current = new LayoutRow();
                    }

                    current.Items.Add(item);
                }

                if (current.Items.Count > 0)
                {
                    rows.Add(current);
                }
            }

            if (rows.Count == 0)
            {
                return new SheetLayout { Width = 1, Height = 1 };
            }

            int maxColumns = maxColumnsPerRow;
            int[] columnWidths = new int[maxColumns];

            foreach (var row in rows)
            {
                for (int i = 0; i < row.Items.Count; i++)
                {
                    if (row.Items[i].NominalSize.Width > columnWidths[i])
                    {
                        columnWidths[i] = row.Items[i].NominalSize.Width;
                    }
                }
            }

            if (columnWidths.Sum() <= 0)
            {
                columnWidths[0] = 1;
            }

            foreach (var row in rows)
            {
                row.Height = row.Items.Count == 0 ? 1 : row.Items.Max(i => i.NominalSize.Height);
            }

            long sheetWidthLong = columnWidths.Aggregate(0L, (sum, width) => sum + width);
            long sheetHeightLong = rows.Aggregate(0L, (sum, row) => sum + row.Height);

            // 書き出しは帯ごとに作るので、ここでは座標が int に収まることだけを確かめる。
            // 1枚のビットマップにする用途（テスト・プレビュー）は、呼び出し側で大きさを検証する。
            if (sheetWidthLong <= 0 || sheetHeightLong <= 0 ||
                sheetWidthLong > int.MaxValue / 8 || sheetHeightLong > int.MaxValue / 8)
            {
                throw new InvalidOperationException(
                    Loc.T("error.layoutTooLarge", sheetWidthLong, sheetHeightLong));
            }

            int sheetWidth = SheetSizeRule.Round((int)sheetWidthLong, align4);   // 4の倍数にそろえるときは右と下に透明な余白を足す
            int sheetHeight = SheetSizeRule.Round((int)sheetHeightLong, align4);

            var layout = new SheetLayout
            {
                Width = sheetWidth,
                Height = sheetHeight
            };

            int y = 0;
            int cellNumber = 1;

            foreach (var row in rows)
            {
                int x = 0;

                for (int col = 0; col < maxColumns; col++)
                {
                    int cellWidth = columnWidths[col];
                    Rectangle cellRect = new Rectangle(x, y, cellWidth, row.Height);

                    layout.Cells.Add(new LayoutCell
                    {
                        Rect = cellRect,
                        CellNumber = cellNumber
                    });

                    if (col < row.Items.Count)
                    {
                        var item = row.Items[col];

                        layout.Placements.Add(new FramePlacement
                        {
                            Source = item.Source,
                            Bitmap = item.Bitmap,
                            CellNumber = cellNumber,
                            Rect = new Rectangle(x, y, item.NominalSize.Width, item.NominalSize.Height),
                            CellRect = cellRect
                        });
                    }

                    x += cellWidth;
                    cellNumber++;
                }

                y += row.Height;
            }

            return layout;
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            tickDriven = true;
            try { AnimationTimerTickCore(); }
            finally { tickDriven = false; }
        }

        private void AnimationTimerTickCore()
        {
            if (previewTargetMode == PreviewTargetMode.Map) { animationTimer.Stop(); return; }
            if (animationFrames.Count == 0)
            {
                animationTimer.Stop();
                SetPlayAccessibleName(false);
                playToggleButton.Invalidate();
                return;
            }

            if (previewTargetMode == PreviewTargetMode.Standard)
            {
                animationIndex = GetNextPlayableFrameIndex(animationIndex);
                UpdateAnimationPreview();
                return;
            }

            DateTime now = DateTime.UtcNow;
            float seconds = lastSimulationTickUtc == default(DateTime)
                ? 0.016f
                : (float)(now - lastSimulationTickUtc).TotalSeconds;
            lastSimulationTickUtc = now;
            seconds = Math.Max(0.001f, Math.Min(0.1f, seconds));

            if (previewTargetMode == PreviewTargetMode.Player)
            {
                AnimationFrameCache current = FindAnimationFrame(playerAnimator.CurrentCell) ?? animationFrames[0];
                float movementSpeed = (float)playerMoveSpeedBox.Value * SimulationSceneSize.Width * 0.18f;
                float jumpForce = PlayerStateController.JumpForceFromSetting((float)playerJumpDistanceBox.Value, SimulationSceneSize.Height);
                float gravity = (float)playerGravityBox.Value * SimulationSceneSize.Height * 2.2f;
                playerState.Update(seconds, previewInput, SimulationSceneSize, SimulationDisplaySizeOf(current.NominalSize),
                    PlayerColliderSceneSize(current.NominalSize),
                    state => playerAnimator.GetDuration(state, GetMaxCellNumber()),
                    movementSpeed, jumpForce, gravity);
                playerAnimator.Update(seconds, playerState.State, GetMaxCellNumber(), playerState.MovementState);
                ThrottleWhenIdle();
            }
            else
            {
                if (animationTimer.Interval != ActiveTimerInterval) animationTimer.Interval = ActiveTimerInterval;
                AdvanceEffectAnimation(seconds);
                AnimationFrameCache current = FindAnimationFrame(effectCurrentCell) ?? animationFrames[0];
                float effectSpeed = (float)effectSpeedBox.Value * SimulationSceneSize.Width * 0.18f;
                effectMotion.Update(seconds,
                    new PointF((float)effectDirectionXBox.Value, (float)effectDirectionYBox.Value),
                    effectSpeed, SimulationSceneSize, SimulationDisplaySizeOf(current.NominalSize));
            }
            // 何も変わっていなければ再描画しない（立ち止まっている間の無駄な描画を省く）。
            if (sceneKeyValid && CaptureSceneKey().Equals(lastSceneKey)) return;
            UpdateAnimationPreview();
        }

        // 立ち止まって何も入力がない間は、次にコマが切り替わる瞬間までタイマーを待たせる（16ミリ秒ごとの判定をやめる）。
        // 間隔は 16〜100 ミリ秒。キーやマウスが押された瞬間に previewInput.Activity から通常の間隔へ戻すので、
        // 入力への反応は遅れない。
        private const int ActiveTimerInterval = 16;
        private const int MaxIdleTimerInterval = 100;
        private PointF lastIdleCheckPosition;

        private void ThrottleWhenIdle()
        {
            bool still = playerState.Position == lastIdleCheckPosition;
            lastIdleCheckPosition = playerState.Position;
            int target = ActiveTimerInterval;
            if (still && playerState.IsResting && !previewInput.AnyActive)
            {
                double untilNextFrame = playerAnimator.GetSecondsToNextFrame(GetMaxCellNumber());
                target = Math.Max(ActiveTimerInterval, Math.Min(MaxIdleTimerInterval, (int)Math.Ceiling(untilNextFrame * 1000.0)));
            }
            if (Math.Abs(animationTimer.Interval - target) >= 2) animationTimer.Interval = target;
        }

        private void WakeSimulationTimer()
        {
            if (animationTimer.Enabled && previewTargetMode != PreviewTargetMode.Standard &&
                animationTimer.Interval != ActiveTimerInterval)
                animationTimer.Interval = ActiveTimerInterval;
        }

        // プレビューの見た目を決める値。前回描いたときと同じなら描き直さなくてよい。
        private struct SceneKey : IEquatable<SceneKey>
        {
            public int Mode, Cell, RequestedState, PlayingState, GroundOffset, ColliderWidth, ColliderHeight;
            public float X, Y;
            public bool Facing, Terrain, ShowCollider;

            public bool Equals(SceneKey other)
            {
                return Mode == other.Mode && Cell == other.Cell && RequestedState == other.RequestedState &&
                    PlayingState == other.PlayingState && GroundOffset == other.GroundOffset &&
                    X == other.X && Y == other.Y && Facing == other.Facing && Terrain == other.Terrain &&
                    ColliderWidth == other.ColliderWidth && ColliderHeight == other.ColliderHeight && ShowCollider == other.ShowCollider;
            }
        }

        private bool sceneKeyValid;
        private SceneKey lastSceneKey;

        private SceneKey CaptureSceneKey()
        {
            if (previewTargetMode == PreviewTargetMode.Player)
                return new SceneKey
                {
                    Mode = 1, Cell = playerAnimator.CurrentCell,
                    RequestedState = (int)playerAnimator.RequestedState, PlayingState = (int)playerAnimator.PlayingState,
                    X = playerState.Position.X, Y = playerState.Position.Y, Facing = playerState.FacingRight,
                    GroundOffset = (int)playerGroundOffsetBox.Value, Terrain = playerState.Terrain != null,
                    ColliderWidth = (int)playerColliderWidthBox.Value, ColliderHeight = (int)playerColliderHeightBox.Value,
                    ShowCollider = playerColliderVisibleCheckBox.Checked
                };
            return new SceneKey { Mode = 2, Cell = effectCurrentCell, X = effectMotion.Position.X, Y = effectMotion.Position.Y };
        }

        private bool IsFrameInRange(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= animationFrames.Count)
            {
                return false;
            }

            int cell = animationFrames[frameIndex].CellNumber;
            return cell >= (int)startCellBox.Value && cell <= (int)endCellBox.Value;
        }

        private int GetFirstPlayableFrameIndex()
        {
            for (int i = 0; i < animationFrames.Count; i++)
            {
                if (IsFrameInRange(i))
                {
                    return i;
                }
            }

            return 0;
        }

        private int GetNextPlayableFrameIndex(int current)
        {
            if (animationFrames.Count == 0)
            {
                return 0;
            }

            for (int step = 1; step <= animationFrames.Count; step++)
            {
                int next = (current + step) % animationFrames.Count;
                if (IsFrameInRange(next))
                {
                    return next;
                }
            }

            return 0;
        }

        private void UpdateRangeBoxMax()
        {
            int maxCell = animationFrames.Count == 0 ? 1 : animationFrames.Max(f => f.CellNumber);

            int editableMax = Math.Max(1, Math.Min(999, maxCell));
            startCellBox.Maximum = editableMax;
            endCellBox.Maximum = editableMax;

            if (endCellBox.Value == 1 || endCellBox.Value > editableMax)
            {
                endCellBox.Value = editableMax;
            }

            if (startCellBox.Value > editableMax)
            {
                startCellBox.Value = 1;
            }

            ClampRangeBoxes();
        }

        private void ClampRangeBoxes()
        {
            if (startCellBox.Value > endCellBox.Value)
            {
                endCellBox.Value = startCellBox.Value;
            }
        }

        private void UpdateTimerInterval()
        {
            int fps = Math.Max(1, (int)fpsBox.Value);
            animationTimer.Interval = Math.Max(1, 1000 / fps);
        }

        private void UpdateZoomLabel()
        {
            if (sheetCanvas.IsFitMode)
            {
                zoomHintLabel.Text = Loc.T("hint.zoomFit");
            }
            else
            {
                zoomHintLabel.Text = Loc.T("hint.zoomPercent", Math.Round(sheetCanvas.ZoomPercent));
            }

            // チップは常に%表記にする（隣の「全体表示」ボタンと文言が重複しないように）。
            // 全体表示中の実際の倍率はPreviewCanvas側で非公開のため、100%を表示する。
            sheetZoomChip.Text = sheetCanvas.IsFitMode ? "100%" : Math.Round(sheetCanvas.ZoomPercent) + "%";
            animZoomChip.Text = animCanvas.IsFitMode ? "100%" : Math.Round(animCanvas.ZoomPercent) + "%";
            RefreshMapHeaders();
        }

        private List<ImageItem> GetAllItems()
        {
            return folders.SelectMany(f => f.Items).ToList();
        }

        // ウィンドウの縁でサイズを変えられる幅（px）。枠なしのウィンドウで、内側のコントロールが端まで覆うため、
        // この幅の余白をフォームに残し、そこをサイズ変更の当たりにする（以前は1pxで、つかみにくかった）。
        private const int ResizeGrip = 6;
        // 四隅は、縁に沿ってこの長さまで斜めのサイズ変更にする。
        private const int ResizeCorner = 20;

        protected override void WndProc(ref Message m)
        {
            const int WmNcHitTest = 0x0084;
            const int HtClient = 1;
            const int HtLeft = 10;
            const int HtRight = 11;
            const int HtTop = 12;
            const int HtTopLeft = 13;
            const int HtTopRight = 14;
            const int HtBottom = 15;
            const int HtBottomLeft = 16;
            const int HtBottomRight = 17;

            base.WndProc(ref m);
            if (m.Msg != WmNcHitTest || WindowState == FormWindowState.Maximized ||
                m.Result.ToInt32() != HtClient) return;

            // マウスの位置は、メッセージに入っている画面座標を使う（Cursor.Position は、このメッセージが処理される時点で動いていることがある）。
            long packed = m.LParam.ToInt64();
            Point point = PointToClient(new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF))));
            bool left = point.X <= ResizeGrip;
            bool right = point.X >= ClientSize.Width - ResizeGrip;
            bool top = point.Y <= ResizeGrip;
            bool bottom = point.Y >= ClientSize.Height - ResizeGrip;
            // 縁の四隅の近くは、斜めにサイズを変えられるようにする。
            bool nearLeft = point.X <= ResizeCorner;
            bool nearRight = point.X >= ClientSize.Width - ResizeCorner;
            bool nearTop = point.Y <= ResizeCorner;
            bool nearBottom = point.Y >= ClientSize.Height - ResizeCorner;

            if ((left && nearTop) || (top && nearLeft)) m.Result = new IntPtr(HtTopLeft);
            else if ((right && nearTop) || (top && nearRight)) m.Result = new IntPtr(HtTopRight);
            else if ((left && nearBottom) || (bottom && nearLeft)) m.Result = new IntPtr(HtBottomLeft);
            else if ((right && nearBottom) || (bottom && nearRight)) m.Result = new IntPtr(HtBottomRight);
            else if (left) m.Result = new IntPtr(HtLeft);
            else if (right) m.Result = new IntPtr(HtRight);
            else if (top) m.Result = new IntPtr(HtTop);
            else if (bottom) m.Result = new IntPtr(HtBottom);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(dividerColor))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (WindowState == FormWindowState.Maximized)
            {
                if (Padding.All != 0) Padding = Padding.Empty;
                Region old = Region;
                Region = null;
                if (old != null) old.Dispose();
            }
            else
            {
                if (Padding.All != ResizeGrip) Padding = new Padding(ResizeGrip);
                ApplyRoundedRegion(this, 10);
            }
        }

        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (PromptOnUnsavedChanges && e.CloseReason != CloseReason.WindowsShutDown && !ConfirmProjectDiscard())
            {
                e.Cancel = true;
                return;
            }
            closing = true;
            StopExportForClose();
            EndAssignMode();
            EndMemoEdit(true);
            if (colorPreviewCancellation != null)
            {
                colorPreviewCancellation.Cancel();
                colorPreviewCancellation.Dispose();
                colorPreviewCancellation = null;
            }
            previewInput.Clear();
            animationTimer.Stop();
            blackTransparencyCooldownTimer.Stop();
            CancelInlineBindingCapture();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Loc.LanguageChanged -= OnUiLanguageChanged;
            if (projectDirectoryLock != null) { projectDirectoryLock.Dispose(); projectDirectoryLock = null; }
            DeleteQuietly(projectExtractDir);
            ReleaseDirectoriesAfterExport();
            projectMenu.Dispose();
            if (clickTracker != null)
            {
                Application.RemoveMessageFilter(clickTracker);
                clickTracker = null;
            }
            animationTimer.Tick -= AnimationTimer_Tick;
            animationTimer.Dispose();
            blackTransparencyCooldownTimer.Dispose();
            DisposeAnimationFrames();
            treeContextMenu.Dispose();
            toolTip.Dispose();
            colorPreviewGate.Dispose();
            imagePipeline.ClearCache();
            base.OnFormClosed(e);
        }

        private sealed class AnimationFrameCache
        {
            // 再生用のコマ画像。小さな画像では全コマを最初に作る。大きな画像では、シート画像からの切り出しを
            // 必要になったときだけ作り（EnsureFrameImage）、古いものから捨てる（Image は null のことがある）。
            public Bitmap Image;
            public Rectangle SheetRect;
            // 書き出し時のコマの大きさ。プレビューを縮小して読み込んだときは Image より大きい。
            public Size NominalSize;
            public int CellNumber;
        }

        private sealed class StateRangeEditorControls
        {
            public CheckBox Enabled;
            public NumericUpDown Start;
            public NumericUpDown End;
            public Button Binding;
            public Panel Row;
        }

        private sealed class NextInputMessageFilter : IMessageFilter
        {
            private readonly Action<Keys> captured;

            public NextInputMessageFilter(Action<Keys> onCaptured)
            {
                captured = onCaptured;
            }

            public bool PreFilterMessage(ref Message m)
            {
                Keys key = Keys.None;
                switch (m.Msg)
                {
                    case 0x0100:
                    case 0x0104:
                        key = (Keys)(int)m.WParam & Keys.KeyCode;
                        break;
                    case 0x0201: key = Keys.LButton; break;
                    case 0x0204: key = Keys.RButton; break;
                    case 0x0207: key = Keys.MButton; break;
                    case 0x020B:
                        key = (((long)m.WParam >> 16) & 0xFFFF) == 2 ? Keys.XButton2 : Keys.XButton1;
                        break;
                }
                if (key == Keys.None) return false;
                captured(key);
                return true;
            }
        }

        private sealed class ImageFolder
        {
            public string Name;
            public readonly List<ImageItem> Items = new List<ImageItem>();
        }

        internal sealed class ImageItem
        {
            public string Path;
            public int ImageNumber;
        }

        private sealed class LayoutRow
        {
            public readonly List<LoadedItem> Items = new List<LoadedItem>();
            public int Height;
        }

        private sealed class SheetLayout
        {
            public int Width;
            public int Height;
            public readonly List<FramePlacement> Placements = new List<FramePlacement>();
            public readonly List<LayoutCell> Cells = new List<LayoutCell>();
        }

        private sealed class FramePlacement
        {
            public ImageItem Source;
            public Bitmap Bitmap;
            public int CellNumber;
            public Rectangle Rect;
            public Rectangle CellRect;
        }

        private sealed class LayoutCell
        {
            public Rectangle Rect;
            public int CellNumber;
        }

        private sealed class AppStateSnapshot
        {
            public int NextFolderNumber;
            public int Columns;
            public int ScaleIndex;
            public int Fps;
            public int StartCell;
            public int EndCell;
            public bool ExportNumbers;
            public bool ShowAxisNumbers;
            public bool UseTerrain = true;
            public List<int[]> SelectedItems = new List<int[]>();
            public List<MemoState> Memos = new List<MemoState>();
            public PreviewTargetMode PreviewMode;
            public string MapJson;
            public decimal PlayerMoveSpeed;
            public decimal PlayerJumpDistance;
            public decimal PlayerGravity;
            public decimal PlayerGroundOffset;
            public decimal PlayerColliderWidth;
            public decimal PlayerColliderHeight;
            public bool ShowCollider;
            public bool MirrorMissingDirections;
            public int BackgroundPalette;
            public bool BlackTransparency;
            public SpriteColorBlendMode ColorBlendMode;
            public Color AdjustmentColor = Color.White;
            public int AdjustmentStrength = 100;
            public decimal EffectDirectionX;
            public decimal EffectDirectionY;
            public decimal EffectSpeed;
            public bool SimulationRangesInitialized;
            public AnimationClipSettings EffectClip = new AnimationClipSettings();
            public readonly Dictionary<PlayerAnimationState, AnimationClipSettings> PlayerClips =
                new Dictionary<PlayerAnimationState, AnimationClipSettings>();
            public readonly Dictionary<PreviewAction, Keys> KeyBindings =
                new Dictionary<PreviewAction, Keys>();
            public readonly List<FolderSnapshot> Folders = new List<FolderSnapshot>();
        }

        private sealed class FolderSnapshot
        {
            public string Name;
            public readonly List<string> Paths = new List<string>();
        }

        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            private static readonly Color Background = Color.FromArgb(24, 32, 38);
            private static readonly Color Hover = Color.FromArgb(55, 48, 125);
            private static readonly Color Border = Color.FromArgb(70, 80, 88);
            public override Color ToolStripDropDownBackground { get { return Background; } }
            public override Color ImageMarginGradientBegin { get { return Background; } }
            public override Color ImageMarginGradientMiddle { get { return Background; } }
            public override Color ImageMarginGradientEnd { get { return Background; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemBorder { get { return Color.FromArgb(95, 82, 220); } }
            public override Color MenuBorder { get { return Border; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Background; } }
        }

        private sealed class NaturalStringComparer : IComparer<string>
        {
            public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

            public int Compare(string x, string y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                int ix = 0;
                int iy = 0;
                while (ix < x.Length && iy < y.Length)
                {
                    if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                    {
                        int sx = ix;
                        int sy = iy;
                        while (sx < x.Length && x[sx] == '0') sx++;
                        while (sy < y.Length && y[sy] == '0') sy++;

                        int ex = sx;
                        int ey = sy;
                        while (ex < x.Length && char.IsDigit(x[ex])) ex++;
                        while (ey < y.Length && char.IsDigit(y[ey])) ey++;

                        int lengthCompare = (ex - sx).CompareTo(ey - sy);
                        if (lengthCompare != 0) return lengthCompare;

                        for (int i = 0; i < ex - sx; i++)
                        {
                            int digitCompare = x[sx + i].CompareTo(y[sy + i]);
                            if (digitCompare != 0) return digitCompare;
                        }

                        int fullEx = ex;
                        int fullEy = ey;
                        while (fullEx < x.Length && char.IsDigit(x[fullEx])) fullEx++;
                        while (fullEy < y.Length && char.IsDigit(y[fullEy])) fullEy++;
                        int zeroCompare = (fullEx - ix).CompareTo(fullEy - iy);
                        if (zeroCompare != 0) return zeroCompare;
                        ix = fullEx;
                        iy = fullEy;
                        continue;
                    }

                    char cx = char.ToUpperInvariant(x[ix]);
                    char cy = char.ToUpperInvariant(y[iy]);
                    if (cx != cy) return cx.CompareTo(cy);
                    ix++;
                    iy++;
                }

                return (x.Length - ix).CompareTo(y.Length - iy);
            }
        }

        private enum ImageOutputFormat
        {
            Png,
            Tga,
            Gif,
            WebP
        }

        private enum PreviewWorkspacePage
        {
            Preview,
            StateTransitions,
            Parameters
        }
    }
}
