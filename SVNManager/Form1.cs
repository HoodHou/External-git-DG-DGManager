using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

public partial class Form1 : Form
{
    private readonly ComboBox _repositorySelector = new();
    private readonly TextBox _repoUrlText = new();
    private readonly TextBox _workingCopyText = new();
    private readonly TextBox _outputText = new();
    private readonly ListView _changesList = new();
    private readonly TextBox _statusSearchText = new();
    private readonly ComboBox _statusFilterCombo = new();
    private readonly CheckBox _statusCommitVisibleOnlyCheck = new();
    private readonly Label _statusFilterSummaryLabel = new();
    private readonly DataGridView _conflictGrid = new();
    private readonly Label _conflictSummaryLabel = new();
    private readonly TreeView _repositoryTree = new();
    private readonly TreeView _fileTree = new();
    private readonly TextBox _fileTreeSearchText = new();
    private readonly CheckBox _fileTreeChangedOnlyCheck = new();
    private readonly Button _fileTreeExpandButton = new();
    private readonly Button _fileTreeCollapseButton = new();
    private readonly Button _fileTreeRefreshButton = new();
    private readonly System.Windows.Forms.Timer _fileTreeLoadDebounceTimer = new();
    private readonly System.Windows.Forms.Timer _treeExpansionSaveTimer = new();
    private readonly ListView _historyList = new();
    private readonly TextBox _historySearchText = new();
    private readonly Label _historySearchScopeLabel = new();
    private readonly Button _historyDeepSearchButton = new();
    private readonly Button _historyLoadMoreButton = new();
    private readonly Button _historyClearSearchButton = new();
    private readonly TextBox _historyDetailText = new();
    private readonly TreeView _historyChangedFilesTree = new();
    private readonly TextBox _historyChangedFilesSearchText = new();
    private readonly ComboBox _historyChangedFilesFilterCombo = new();
    private readonly Panel _historyDiffPanel = new();
    private readonly Button _historyDiffMaximizeButton = new();
    private readonly Label _historyDiffHeaderLabel = new();
    private readonly ImageList _historyListRowImages = new();
    private readonly TabControl _mainTabs = new ShellTabControl();
    private readonly FlowLayoutPanel _shellNav = new();
    private readonly List<ShellNavButton> _shellNavButtons = [];
    private ListViewItem? _hoveredHistoryItem;
    private readonly TabPage _configPage = new("配置");
    private readonly TabPage _statusPage = new("File Status");
    private readonly TabPage _conflictPage = new("冲突");
    private readonly TabPage _historyPage = new("History");
    private readonly SplitContainer _workspaceSplit = new();
    private readonly SplitContainer _historySplit = new();
    private readonly SplitContainer _changedFilesSplit = new();
    private readonly ContextMenuStrip _changesListMenu = new();
    private readonly ContextMenuStrip _fileTreeMenu = new();
    private readonly ContextMenuStrip _historyListMenu = new();
    private readonly ContextMenuStrip _historyChangedFilesMenu = new();
    private readonly Button _checkoutButton = new();
    private readonly Button _updateButton = new();
    private readonly Button _statusButton = new();
    private readonly Button _commitButton = new();
    private readonly Button _diffButton = new();
    private readonly Button _externalMergeButton = new();
    private readonly Button _conflictWorkflowButton = new();
    private readonly Button _historyButton = new();
    private readonly Button _moreActionsButton = new();
    private readonly ContextMenuStrip _moreActionsMenu = new();
    private readonly ImageList _treeImages = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _localRevisionStatusLabel = new();
    private readonly ToolStripStatusLabel _toolUpdateStatusLabel = new();
    private readonly ToolStripStatusLabel _remoteStatusLabel = new();
    private readonly System.Windows.Forms.Timer _remoteCheckTimer = new();
    private readonly SvnClient _svn = new();
    private readonly AppSettings _settings;
    private bool _loadingRepository;
    private bool _loadingFileTree;
    private bool _loadingCurrentTab;
    private bool _updatingChangesList;
    private bool _checkingToolUpdate;
    private bool _checkingRemote;
    private bool _historyDiffMaximized;
    private WorkingCopyInfo _currentWorkingCopyInfo = WorkingCopyInfo.Empty;
    private SvnLogEntry? _latestRemoteLog;
    private GitUpdateStatus? _lastToolUpdateStatus;
    private string? _lastToolRepositoryRoot;
    private ReleaseUpdateStatus? _lastReleaseUpdateStatus;
    private readonly HashSet<string> _selectedFileTreePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _styledFileTreeSelectionPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _fileTreeSelectionAnchorPath;
    private CancellationTokenSource? _fileTreeLoadCts;
    private int _lastFileTreeFileCount;
    private IReadOnlyDictionary<string, SvnStatusKind> _fileTreeStatusMap = new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);
    private SvnLogEntry? _selectedHistoryLog;
    private List<SvnLogEntry> _selectedHistoryLogs = [];
    private List<SvnLogEntry> _historyRows = [];
    private int _historyLoadedLimit = InitialHistoryLimit;
    private List<SvnChange> _statusChangesAll = [];
    private readonly HashSet<string> _checkedStatusPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ChangedFileEntry> _historyChangedFilesAll = [];
    private string _historyChangedFilesRootText = "Changed files";
    private List<SvnChange> _currentConflicts = [];
    private readonly Dictionary<string, DiffPreviewData> _historyDiffPreviewCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _historyDiffPreviewCts;
    private const int InitialHistoryLimit = 80;
    private const int HistoryLoadMoreStep = 200;
    private const int HistoryDeepSearchLimit = 1000;
    private const int HistoryRevisionRangeLimit = 5000;
    private const int MaxDiffPreviewCacheEntries = 40;
    private const int MaxFileTreeDisplayFiles = 8000;
    private const int MaxFileTreeAutoExpandFiles = 1200;
    private const int MaxFileTreeExpandAllFiles = 2000;
    private const int FileTreeLoadDebounceMilliseconds = 350;

    public Form1()
    {
        InitializeComponent();
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        _settings = AppSettings.Load();
        _fileTreeLoadDebounceTimer.Interval = FileTreeLoadDebounceMilliseconds;
        _fileTreeLoadDebounceTimer.Tick += (_, _) =>
        {
            _fileTreeLoadDebounceTimer.Stop();
            LoadAllFiles();
        };
        _treeExpansionSaveTimer.Interval = 600;
        _treeExpansionSaveTimer.Tick += (_, _) =>
        {
            _treeExpansionSaveTimer.Stop();
            SaveTreeExpansionStateCore();
        };
        BuildUi();
        LoadSettingsIntoUi();
        Shown += async (_, _) =>
        {
            RestoreUiLayout();
            await RunStartupEnvironmentCheckAsync();
            if (ValidateWorkingCopyPathForBackground())
            {
                await LoadRepositoryHistoryAsync();
            }
            else
            {
                WriteOutput("请先在“配置”页导入已有工作副本，或检出一个新的 SVN 库。");
            }
            _remoteCheckTimer.Start();
            await CheckToolUpdatesAsync(showUpToDateMessage: false);
            await CheckRemoteChangesAsync(showUpToDateMessage: false);
        };
        FormClosing += (_, _) =>
        {
            _remoteCheckTimer.Stop();
            CancelFileTreeLoad();
            CancelHistoryDiffPreview();
            SaveUiLayout();
        };
    }

    private void BuildUi()
    {
        Text = "梦境 SVN 管理器";
        MinimumSize = new Size(1180, 760);
        Size = new Size(1480, 900);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 250);
        ConfigureTreeImages();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = Color.FromArgb(246, 248, 250),
        };
        root.Controls.Add(toolbar, 0, 0);

        _repositorySelector.Width = 250;
        _repositorySelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _repositorySelector.SelectedIndexChanged += (_, _) => SelectRepositoryFromList();
        toolbar.Controls.Add(_repositorySelector);

        var importRepositoryButton = new Button { Text = "导入/检出库", Width = 110 };
        importRepositoryButton.Click += (_, _) => SelectTab("配置");
        toolbar.Controls.Add(importRepositoryButton);

        var manageRepositoryButton = new Button { Text = "管理本地库", Width = 110 };
        manageRepositoryButton.Click += (_, _) => ShowRepositoryManagerDialog();
        toolbar.Controls.Add(manageRepositoryButton);

        var saveRepositoryButton = new Button { Text = "保存库", Width = 82 };
        saveRepositoryButton.Click += (_, _) => SaveCurrentRepository();
        toolbar.Controls.Add(saveRepositoryButton);

        var removeRepositoryButton = new Button { Text = "移除", Width = 70 };
        removeRepositoryButton.Click += (_, _) => RemoveCurrentRepository();
        toolbar.Controls.Add(removeRepositoryButton);

        _updateButton.Text = "拉取最新";
        _updateButton.Width = 110;
        _updateButton.Click += async (_, _) => await RunUpdateAsync();
        toolbar.Controls.Add(_updateButton);

        _statusButton.Text = "查看改动";
        _statusButton.Width = 110;
        _statusButton.Click += async (_, _) => await RefreshStatusAsync();
        toolbar.Controls.Add(_statusButton);

        _commitButton.Text = "提交选中文件";
        _commitButton.Width = 130;
        _commitButton.Click += async (_, _) => await RunCommitAsync();
        toolbar.Controls.Add(_commitButton);

        _diffButton.Text = "查看差异";
        _diffButton.Width = 130;
        _diffButton.Click += async (_, _) => await RunDiffAsync();

        _externalMergeButton.Text = "外部对比/合并";
        _externalMergeButton.Width = 130;
        _externalMergeButton.Click += async (_, _) => await RunExternalCompareOrMergeAsync();

        _conflictWorkflowButton.Text = "冲突处理";
        _conflictWorkflowButton.Width = 100;
        _conflictWorkflowButton.Click += async (_, _) => await RunConflictWorkflowAsync();

        _historyButton.Text = "文件历史";
        _historyButton.Width = 100;
        _historyButton.Click += async (_, _) => await RunFileHistoryAsync();

        var openFolderButton = new Button { Text = "打开目录", Width = 100 };
        openFolderButton.Click += (_, _) => OpenWorkingCopyFolder();
        toolbar.Controls.Add(openFolderButton);

        BuildMoreActionsMenu();
        _moreActionsButton.Text = "更多操作";
        _moreActionsButton.Width = 96;
        _moreActionsButton.Click += (_, _) => _moreActionsMenu.Show(_moreActionsButton, new Point(0, _moreActionsButton.Height));
        toolbar.Controls.Add(_moreActionsButton);

        _changesList.Dock = DockStyle.Fill;
        _changesList.View = View.Details;
        _changesList.FullRowSelect = true;
        _changesList.GridLines = true;
        _changesList.CheckBoxes = true;
        _changesList.HideSelection = false;
        WinFormsRendering.EnableDoubleBuffering(_changesList);
        _changesList.Columns.Add("状态", 90);
        _changesList.Columns.Add("文件", 650);
        _changesList.Columns.Add("说明", 260);
        _changesList.MouseDown += (_, args) => SelectChangeItemForContextMenu(args);
        _changesList.ItemChecked += (_, args) => TrackStatusItemChecked(args.Item);
        BuildChangesListMenu();
        _changesList.ContextMenuStrip = _changesListMenu;

        _workspaceSplit.Dock = DockStyle.Fill;
        _workspaceSplit.SplitterDistance = 170;
        _workspaceSplit.FixedPanel = FixedPanel.Panel1;
        root.Controls.Add(_workspaceSplit, 0, 1);

        var sidebarSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 170,
            FixedPanel = FixedPanel.Panel1,
        };
        _workspaceSplit.Panel1.Controls.Add(sidebarSplit);

        _repositoryTree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(_repositoryTree);
        _repositoryTree.ShowNodeToolTips = true;
        _repositoryTree.ImageList = _treeImages;
        _repositoryTree.AfterSelect += async (_, args) => await SelectSidebarRepositoryAsync(args.Node);
        sidebarSplit.Panel1.Controls.Add(CreateTitledPanel("本地库", _repositoryTree));
        RefreshRepositoryTree();

        _mainTabs.Dock = DockStyle.Fill;
        _configPage.Controls.Add(CreateConfigPanel());
        _mainTabs.TabPages.Add(_configPage);

        _statusPage.Controls.Add(CreateStatusPanel());
        _mainTabs.TabPages.Add(_statusPage);

        _conflictPage.Controls.Add(CreateConflictPanel());
        _mainTabs.TabPages.Add(_conflictPage);

        _fileTree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(_fileTree);
        _fileTree.ShowNodeToolTips = true;
        _fileTree.ImageList = _treeImages;
        _fileTree.TreeViewNodeSorter = new FileTreeNodeSorter();
        _fileTree.BeforeExpand += (_, args) =>
        {
            if (args.Node != null)
            {
                EnsureLazyFileTreeChildren(args.Node);
            }
        };
        _fileTree.NodeMouseDoubleClick += (_, args) =>
        {
            if (IsModernTreeArrowHit(_fileTree, args.Node, new Point(args.X, args.Y)))
            {
                return;
            }

            if (ToggleExpandableNode(args.Node))
            {
                return;
            }

            OpenTreeFile(args.Node);
        };
        _fileTree.AfterExpand += (_, _) => SaveTreeExpansionState();
        _fileTree.AfterCollapse += (_, _) => SaveTreeExpansionState();
        _fileTree.NodeMouseClick += (_, args) =>
        {
            HandleFileTreeNodeMouseClick(args.Node, args.Button);
        };
        BuildFileTreeMenu();
        _fileTree.ContextMenuStrip = _fileTreeMenu;
        var filesPage = new TabPage("全部文件");
        filesPage.Controls.Add(CreateAllFilesPanel());
        _mainTabs.TabPages.Add(filesPage);

        _historySplit.Dock = DockStyle.Fill;
        _historySplit.Orientation = Orientation.Horizontal;
        _historySplit.SplitterDistance = 240;
        var historyListPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        historyListPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        historyListPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var historySearchPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
        };
        historySearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historySearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        historySearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        historySearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        historySearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _historySearchText.Dock = DockStyle.Fill;
        _historySearchText.PlaceholderText = "搜索：file:文件 author:作者 rev:57100-57120 id:需求号 或普通关键词";
        _historySearchText.Margin = new Padding(0, 4, 6, 4);
        _historySearchText.TextChanged += (_, _) => ApplyHistoryFilter();
        _historySearchText.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
            {
                return;
            }

            args.SuppressKeyPress = true;
            await RunDeepHistorySearchAsync();
        };
        historySearchPanel.Controls.Add(_historySearchText, 0, 0);

        _historySearchScopeLabel.Dock = DockStyle.Fill;
        _historySearchScopeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _historySearchScopeLabel.ForeColor = Color.FromArgb(90, 100, 115);
        _historySearchScopeLabel.Text = "已加载 0 条";
        historySearchPanel.Controls.Add(_historySearchScopeLabel, 1, 0);

        ConfigureHistorySearchButton(_historyDeepSearchButton, "深度搜索");
        _historyDeepSearchButton.Click += async (_, _) => await RunDeepHistorySearchAsync();
        historySearchPanel.Controls.Add(_historyDeepSearchButton, 2, 0);

        ConfigureHistorySearchButton(_historyLoadMoreButton, "加载更多");
        _historyLoadMoreButton.Click += async (_, _) => await LoadMoreRepositoryHistoryAsync();
        historySearchPanel.Controls.Add(_historyLoadMoreButton, 3, 0);

        ConfigureHistorySearchButton(_historyClearSearchButton, "清空");
        _historyClearSearchButton.Click += (_, _) => _historySearchText.Clear();
        historySearchPanel.Controls.Add(_historyClearSearchButton, 4, 0);

        historyListPanel.Controls.Add(historySearchPanel, 0, 0);

        _historyList.Dock = DockStyle.Fill;
        _historyList.View = View.Details;
        _historyList.FullRowSelect = true;
        _historyList.GridLines = false;
        _historyList.HideSelection = false;
        _historyList.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _historyList.BackColor = Color.White;
        _historyList.OwnerDraw = true;
        WinFormsRendering.EnableDoubleBuffering(_historyList);
        _historyList.HeaderStyle = ColumnHeaderStyle.None;
        _historyListRowImages.ColorDepth = ColorDepth.Depth32Bit;
        _historyListRowImages.ImageSize = new Size(1, 58);
        _historyListRowImages.Images.Add("row-height", new Bitmap(1, 58));
        _historyList.SmallImageList = _historyListRowImages;
        _historyList.Columns.Add("Graph", 70);
        _historyList.Columns.Add("Description", 760);
        _historyList.Columns.Add("Date", 150);
        _historyList.Columns.Add("Author", 130);
        _historyList.Columns.Add("Commit", 90);
        _historyList.DrawColumnHeader += (_, args) => args.DrawDefault = false;
        _historyList.DrawSubItem += DrawHistoryListSubItem;
        _historyList.SelectedIndexChanged += (_, _) => ShowSelectedHistoryDetail();
        _historyList.DoubleClick += (_, _) => FocusFirstChangedFileInSelectedHistory();
        _historyList.MouseDown += (_, args) => SelectHistoryItemForContextMenu(args);
        _historyList.MouseMove += (_, args) =>
        {
            var previous = _hoveredHistoryItem;
            var item = _historyList.GetItemAt(args.X, args.Y);
            if (!ReferenceEquals(item, _hoveredHistoryItem))
            {
                _hoveredHistoryItem = item;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, item);
            }
        };
        _historyList.MouseLeave += (_, _) =>
        {
            if (_hoveredHistoryItem != null)
            {
                var previous = _hoveredHistoryItem;
                _hoveredHistoryItem = null;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, null);
            }
        };
        BuildHistoryListMenu();
        _historyList.ContextMenuStrip = _historyListMenu;
        historyListPanel.Controls.Add(_historyList, 0, 1);
        _historySplit.Panel1.Controls.Add(CreateHistoryTopPanel(historyListPanel));

        _changedFilesSplit.Dock = DockStyle.Fill;
        _changedFilesSplit.SplitterDistance = 430;
        _changedFilesSplit.FixedPanel = FixedPanel.Panel1;
        _historyChangedFilesTree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(_historyChangedFilesTree);
        _historyChangedFilesTree.ImageList = _treeImages;
        _historyChangedFilesTree.TreeViewNodeSorter = new FileTreeNodeSorter();
        _historyChangedFilesTree.NodeMouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
            {
                _historyChangedFilesTree.SelectedNode = args.Node;
            }
        };
        _historyChangedFilesTree.NodeMouseDoubleClick += async (_, args) =>
        {
            if (IsModernTreeArrowHit(_historyChangedFilesTree, args.Node, new Point(args.X, args.Y)))
            {
                return;
            }

            if (ToggleExpandableNode(args.Node))
            {
                return;
            }

            await OpenHistoryChangedFileAsync(args.Node);
        };
        _historyChangedFilesTree.AfterSelect += async (_, args) => await ShowSelectedHistoryFileDiffAsync(args.Node);
        BuildHistoryChangedFilesMenu();
        _historyChangedFilesTree.ContextMenuStrip = _historyChangedFilesMenu;
        _changedFilesSplit.Panel1.Controls.Add(CreateChangedFilesFilterPanel(
            "Changed files",
            _historyChangedFilesTree,
            _historyChangedFilesSearchText,
            _historyChangedFilesFilterCombo,
            ApplyHistoryChangedFilesFilter));

        _historyDiffPanel.Dock = DockStyle.Fill;
        _historyDiffPanel.BackColor = Color.White;
        _historyDetailText.Dock = DockStyle.Fill;
        _historyDetailText.Multiline = true;
        _historyDetailText.ReadOnly = true;
        _historyDetailText.ScrollBars = ScrollBars.Both;
        _historyDetailText.WordWrap = false;
        _historyDiffPanel.Controls.Add(_historyDetailText);
        _changedFilesSplit.Panel2.Controls.Add(CreateHistoryDiffPreviewPanel());
        _historySplit.Panel2.Controls.Add(_changedFilesSplit);
        _historyPage.Controls.Add(_historySplit);
        _mainTabs.TabPages.Add(_historyPage);
        _mainTabs.SelectedIndexChanged += async (_, _) =>
        {
            UpdateShellNavigationSelection();
            await LoadCurrentTabAsync();
        };
        _workspaceSplit.Panel2.Controls.Add(CreateShellHost());

        _outputText.Dock = DockStyle.Fill;
        _outputText.Multiline = true;
        _outputText.ReadOnly = true;
        _outputText.ScrollBars = ScrollBars.Both;
        _outputText.WordWrap = false;
        _outputText.BackColor = Color.FromArgb(250, 250, 250);
        sidebarSplit.Panel2.Controls.Add(CreateTitledPanel("终端输出", _outputText));

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        _localRevisionStatusLabel.Text = "本地：未检查";
        _localRevisionStatusLabel.IsLink = true;
        _localRevisionStatusLabel.Click += (_, _) => RefreshWorkingCopyRevisionStatus(showFailure: true);
        statusStrip.Items.Add(_localRevisionStatusLabel);
        _toolUpdateStatusLabel.Text = "工具：未检查";
        _toolUpdateStatusLabel.IsLink = true;
        _toolUpdateStatusLabel.Click += async (_, _) => await ShowToolUpdatePanelAsync();
        statusStrip.Items.Add(_toolUpdateStatusLabel);
        _remoteStatusLabel.Text = "远端：未检查";
        _remoteStatusLabel.IsLink = true;
        _remoteStatusLabel.Click += async (_, _) => await CheckRemoteChangesAsync(showUpToDateMessage: true);
        statusStrip.Items.Add(_remoteStatusLabel);
        _statusLabel.Text = "就绪";
        Controls.Add(statusStrip);
        _remoteCheckTimer.Interval = 180000;
        _remoteCheckTimer.Tick += async (_, _) => await CheckRemoteChangesAsync(showUpToDateMessage: false);
        ApplyControlStyle(this);
    }

    private Control CreateConfigPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        fields.Controls.Add(new Label { Text = "SVN 地址", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _repoUrlText.Dock = DockStyle.Fill;
        fields.Controls.Add(_repoUrlText, 1, 0);
        _checkoutButton.Text = "检出";
        _checkoutButton.Dock = DockStyle.Fill;
        _checkoutButton.Click += async (_, _) => await RunCheckoutAsync();
        fields.Controls.Add(_checkoutButton, 2, 0);

        fields.Controls.Add(new Label { Text = "本地目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        _workingCopyText.Dock = DockStyle.Fill;
        fields.Controls.Add(_workingCopyText, 1, 1);
        var chooseButton = new Button { Text = "选择", Dock = DockStyle.Fill };
        chooseButton.Click += (_, _) => ChooseWorkingCopy();
        fields.Controls.Add(chooseButton, 2, 1);
        root.Controls.Add(CreateTitledPanel("导入 / 检出 SVN 库", fields), 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 12, 0, 0),
        };
        actions.Controls.Add(CreateActionButton("导入已有工作副本", ChooseWorkingCopy, 132));
        actions.Controls.Add(CreateActionButton("保存当前库", SaveCurrentRepository, 104));
        actions.Controls.Add(CreateActionButton("移除当前库", RemoveCurrentRepository, 104));
        actions.Controls.Add(CreateActionButton("管理本地库", ShowRepositoryManagerDialog, 104));
        actions.Controls.Add(CreateActionButton("打开目录", OpenWorkingCopyFolder, 92));
        actions.Controls.Add(CreateActionButton("环境检测", async () => await ShowEnvironmentCheckAsync(), 92));
        actions.Controls.Add(CreateActionButton("设置", ShowSettingsDialog, 82));
        root.Controls.Add(actions, 0, 1);

        return root;
    }

    private Control CreateShellHost()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(245, 247, 250),
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _shellNav.Dock = DockStyle.Fill;
        _shellNav.FlowDirection = FlowDirection.TopDown;
        _shellNav.WrapContents = false;
        _shellNav.Padding = new Padding(8, 10, 8, 8);
        _shellNav.BackColor = Color.FromArgb(24, 31, 42);
        _shellNavButtons.Clear();
        _shellNav.Controls.Clear();

        AddShellNavButton("配置", "CFG", "配置");
        AddShellNavButton("改动", "STS", "File Status");
        AddShellNavButton("冲突", "CNF", "冲突");
        AddShellNavButton("文件", "ALL", "全部文件");
        AddShellNavButton("历史", "HIS", "History");
        UpdateShellNavigationSelection();

        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.Appearance = TabAppearance.FlatButtons;
        _mainTabs.ItemSize = new Size(0, 1);
        _mainTabs.SizeMode = TabSizeMode.Fixed;

        host.Controls.Add(_shellNav, 0, 0);
        host.Controls.Add(_mainTabs, 1, 0);
        return host;
    }

    private void AddShellNavButton(string title, string glyph, string tabText)
    {
        var button = new ShellNavButton
        {
            Title = title,
            Glyph = glyph,
            TabText = tabText,
            Width = 96,
            Height = 58,
            Margin = new Padding(0, 0, 0, 8),
        };
        button.Click += (_, _) => SelectTab(tabText);
        _shellNavButtons.Add(button);
        _shellNav.Controls.Add(button);
    }

    private void UpdateShellNavigationSelection()
    {
        var current = GetBaseTabText(_mainTabs.SelectedTab?.Text ?? "");
        foreach (var button in _shellNavButtons)
        {
            button.Active = string.Equals(button.TabText, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Button CreateActionButton(string text, Action action, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            Margin = new Padding(0, 0, 8, 8),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Control CreateTitledPanel(string title, Control content)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(241, 243, 245),
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.Controls.Add(content, 0, 1);
        return panel;
    }

    private Control CreateHistoryDiffPreviewPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(241, 243, 245),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        _historyDiffHeaderLabel.Text = "Diff preview";
        _historyDiffHeaderLabel.Dock = DockStyle.Fill;
        _historyDiffHeaderLabel.Padding = new Padding(8, 0, 0, 0);
        _historyDiffHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
        _historyDiffHeaderLabel.BackColor = Color.FromArgb(241, 243, 245);
        _historyDiffHeaderLabel.ForeColor = Color.FromArgb(55, 65, 81);
        _historyDiffHeaderLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        header.Controls.Add(_historyDiffHeaderLabel, 0, 0);
        _historyDiffMaximizeButton.Text = "最大化差异";
        _historyDiffMaximizeButton.Dock = DockStyle.Fill;
        _historyDiffMaximizeButton.Margin = new Padding(4, 2, 6, 2);
        _historyDiffMaximizeButton.Click += (_, _) => ToggleHistoryDiffMaximized();
        header.Controls.Add(_historyDiffMaximizeButton, 1, 0);

        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(_historyDiffPanel, 0, 1);
        return panel;
    }

    private void ToggleHistoryDiffMaximized()
    {
        _historyDiffMaximized = !_historyDiffMaximized;
        _historySplit.Panel1Collapsed = _historyDiffMaximized;
        _changedFilesSplit.Panel1Collapsed = _historyDiffMaximized;
        _historyDiffMaximizeButton.Text = _historyDiffMaximized ? "还原布局" : "最大化差异";
    }

    private void ConfigureTreeImages()
    {
        _treeImages.ColorDepth = ColorDepth.Depth32Bit;
        _treeImages.ImageSize = new Size(16, 16);
        _treeImages.Images.Clear();
        _treeImages.Images.Add("repo", CreateTreeIcon(Color.FromArgb(57, 99, 157), false));
        _treeImages.Images.Add("folder", CreateTreeIcon(Color.FromArgb(219, 164, 64), true));
        _treeImages.Images.Add("file", CreateTreeIcon(Color.FromArgb(118, 128, 140), false));
        _treeImages.Images.Add("xml", CreateTreeIcon(Color.FromArgb(39, 132, 85), false));
        _treeImages.Images.Add("lua", CreateTreeIcon(Color.FromArgb(72, 99, 180), false));
        _treeImages.Images.Add("changed", CreateTreeIcon(Color.FromArgb(209, 92, 56), false));
        _treeImages.Images.Add("action-added", CreateActionTreeIcon("A", Color.FromArgb(35, 134, 83)));
        _treeImages.Images.Add("action-modified", CreateActionTreeIcon("M", Color.FromArgb(184, 107, 25)));
        _treeImages.Images.Add("action-deleted", CreateActionTreeIcon("D", Color.FromArgb(184, 66, 66)));
        _treeImages.Images.Add("action-conflicted", CreateActionTreeIcon("C", Color.FromArgb(164, 62, 176)));
        _treeImages.Images.Add("action-replaced", CreateActionTreeIcon("R", Color.FromArgb(109, 85, 184)));
        _treeImages.Images.Add("action-unknown", CreateActionTreeIcon("?", Color.FromArgb(100, 116, 139)));
    }

    private static void ConfigureNavigationTree(TreeView tree)
    {
        WinFormsRendering.EnableDoubleBuffering(tree);
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.ShowLines = false;
        tree.ShowRootLines = false;
        tree.ShowPlusMinus = false;
        tree.HotTracking = false;
        tree.ItemHeight = 28;
        tree.BorderStyle = System.Windows.Forms.BorderStyle.None;
        tree.BackColor = Color.White;
        tree.ForeColor = Color.FromArgb(35, 43, 51);
        tree.LineColor = Color.FromArgb(226, 232, 240);
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.DrawNode -= DrawModernTreeNode;
        tree.DrawNode += DrawModernTreeNode;
        tree.MouseDown -= ToggleModernTreeNodeFromMouseDown;
        tree.MouseDown += ToggleModernTreeNodeFromMouseDown;
    }

    private static void ToggleModernTreeNodeFromMouseDown(object? sender, MouseEventArgs args)
    {
        if (sender is not TreeView tree || args.Button != MouseButtons.Left || args.Clicks > 1)
        {
            return;
        }

        var node = tree.GetNodeAt(args.Location);
        if (node == null || node.Nodes.Count == 0)
        {
            return;
        }

        if (!IsModernTreeArrowHit(tree, node, args.Location))
        {
            return;
        }

        tree.SelectedNode = node;
        ToggleExpandableNode(node);
    }

    private static bool ToggleExpandableNode(TreeNode? node)
    {
        if (node == null || node.Nodes.Count == 0)
        {
            return false;
        }

        if (node.IsExpanded)
        {
            node.Collapse();
        }
        else
        {
            node.Expand();
        }

        return true;
    }

    private static bool IsModernTreeArrowHit(TreeView tree, TreeNode? node, Point location)
    {
        return node != null &&
            node.Nodes.Count > 0 &&
            GetModernTreeNodeArrowBounds(tree, node).Contains(location);
    }

    private static Rectangle GetModernTreeNodeArrowBounds(TreeView tree, TreeNode node)
    {
        var top = node.Bounds.Top > 0 ? node.Bounds.Top : 0;
        return new Rectangle(8 + node.Level * 18, top + 2, 24, Math.Max(20, tree.ItemHeight - 2));
    }

    private static void DrawModernTreeNode(object? sender, DrawTreeNodeEventArgs args)
    {
        if (sender is not TreeView tree || args.Node == null)
        {
            return;
        }

        args.DrawDefault = false;
        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var fullBounds = new Rectangle(4, args.Bounds.Top + 2, Math.Max(1, tree.ClientSize.Width - 8), tree.ItemHeight - 4);
        var selected = (args.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var markedSelected = args.Node.BackColor != Color.Empty && args.Node.BackColor != tree.BackColor;
        var backgroundColor = selected
            ? Color.FromArgb(226, 241, 255)
            : markedSelected ? args.Node.BackColor : tree.BackColor;
        using var backgroundBrush = new SolidBrush(backgroundColor);
        graphics.FillRoundedRectangle(backgroundBrush, fullBounds, 7);

        var x = 10 + args.Node.Level * 18;
        var arrowRect = GetModernTreeNodeArrowBounds(tree, args.Node);
        if (args.Node.Nodes.Count > 0)
        {
            using var arrowFont = new Font("Segoe UI", 7F, FontStyle.Regular);
            TextRenderer.DrawText(
                graphics,
                args.Node.IsExpanded ? "▼" : "▶",
                arrowFont,
                arrowRect,
                Color.FromArgb(100, 116, 139),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        x += 18;
        var imageKey = args.Node.IsSelected ? args.Node.SelectedImageKey : args.Node.ImageKey;
        if (tree.ImageList != null && !string.IsNullOrWhiteSpace(imageKey) && tree.ImageList.Images.ContainsKey(imageKey))
        {
            var image = tree.ImageList.Images[imageKey];
            if (image != null)
            {
                graphics.DrawImage(image, new Rectangle(x, args.Bounds.Top + 6, 16, 16));
                x += 22;
            }
        }

        var font = args.Node.NodeFont ?? tree.Font;
        var color = selected ? Color.FromArgb(15, 76, 129) : args.Node.ForeColor == Color.Empty ? tree.ForeColor : args.Node.ForeColor;
        var textBounds = new Rectangle(x, args.Bounds.Top + 1, Math.Max(1, tree.ClientSize.Width - x - 8), tree.ItemHeight - 2);
        TextRenderer.DrawText(
            graphics,
            args.Node.Text,
            font,
            textBounds,
            color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawHistoryListSubItem(object? sender, DrawListViewSubItemEventArgs args)
    {
        args.DrawDefault = false;
        if (sender is not ListView list || args.Item?.Tag is not SvnLogEntry log || args.ColumnIndex != 0)
        {
            return;
        }

        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, args.Item.Bounds.Top + 5, Math.Max(1, list.ClientSize.Width - 12), args.Item.Bounds.Height - 8);
        var selected = args.Item.Selected;
        var hovered = ReferenceEquals(args.Item, _hoveredHistoryItem);
        var cardColor = selected ? Color.FromArgb(226, 241, 255) : Color.White;
        if (!selected && hovered)
        {
            cardColor = Color.FromArgb(248, 250, 252);
        }

        var borderColor = selected
            ? Color.FromArgb(147, 197, 253)
            : hovered ? Color.FromArgb(203, 213, 225) : Color.FromArgb(226, 232, 240);
        using var cardBrush = new SolidBrush(cardColor);
        using var borderPen = new Pen(borderColor);
        using var revisionFont = new Font("Consolas", 9F, FontStyle.Bold);
        using var authorFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        using var dateFont = new Font("Microsoft YaHei UI", 8F);
        using var messageFont = new Font("Microsoft YaHei UI", 9F);
        graphics.FillRoundedRectangle(cardBrush, bounds, 8);
        graphics.DrawRoundedRectangle(borderPen, bounds, 8);

        var actionColor = log.IsUncommitted
            ? Color.FromArgb(184, 107, 25)
            : log.IsWorkingCopyRevision ? Color.FromArgb(37, 99, 235) : Color.FromArgb(100, 116, 139);
        using var markerBrush = new SolidBrush(actionColor);
        graphics.FillEllipse(markerBrush, bounds.Left + 12, bounds.Top + 17, 10, 10);
        if (log.IsWorkingCopyRevision)
        {
            graphics.FillRectangle(markerBrush, bounds.Left + 16, bounds.Top + 27, 2, 12);
        }

        var revision = log.IsUncommitted ? "LOCAL" : $"r{log.Revision}";
        var revisionBounds = new Rectangle(bounds.Left + 34, bounds.Top + 8, 92, 18);
        TextRenderer.DrawText(graphics, revision, revisionFont, revisionBounds, actionColor, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var authorBounds = new Rectangle(bounds.Left + 130, bounds.Top + 8, 130, 18);
        TextRenderer.DrawText(graphics, log.Author, authorFont, authorBounds, Color.FromArgb(31, 41, 55), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var dateBounds = new Rectangle(bounds.Right - 150, bounds.Top + 8, 134, 18);
        TextRenderer.DrawText(graphics, log.LocalDateText, dateFont, dateBounds, Color.FromArgb(100, 116, 139), TextFormatFlags.Right | TextFormatFlags.NoPadding);

        if (log.ChangedFiles.Count > 0)
        {
            var countText = $"{log.ChangedFiles.Count} files";
            var countBounds = new Rectangle(bounds.Right - 84, bounds.Top + 29, 68, 18);
            using var countBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
            using var countPen = new Pen(Color.FromArgb(203, 213, 225));
            graphics.FillRoundedRectangle(countBrush, countBounds, 5);
            graphics.DrawRoundedRectangle(countPen, countBounds, 5);
            TextRenderer.DrawText(graphics, countText, dateFont, countBounds, Color.FromArgb(71, 85, 105), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var messageBounds = new Rectangle(bounds.Left + 34, bounds.Top + 28, Math.Max(1, bounds.Width - 128), 20);
        TextRenderer.DrawText(graphics, log.DescriptionText, messageFont, messageBounds, Color.FromArgb(51, 65, 85), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static Bitmap CreateTreeIcon(Color color, bool folder)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(ControlPaint.Dark(color), 1);
        if (folder)
        {
            graphics.FillRectangle(brush, 2, 5, 12, 8);
            graphics.FillRectangle(brush, 3, 3, 5, 3);
            graphics.DrawRectangle(pen, 2, 5, 12, 8);
        }
        else
        {
            graphics.FillRectangle(brush, 4, 2, 8, 12);
            graphics.DrawRectangle(pen, 4, 2, 8, 12);
            graphics.DrawLine(Pens.White, 6, 5, 10, 5);
            graphics.DrawLine(Pens.White, 6, 8, 10, 8);
        }

        return bitmap;
    }

    private static Bitmap CreateActionTreeIcon(string text, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 1, 2, 14, 12);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (16 - size.Width) / 2F, (16 - size.Height) / 2F - 0.5F);
        return bitmap;
    }

    private void BuildMoreActionsMenu()
    {
        _moreActionsMenu.Items.Clear();
        _moreActionsMenu.Items.Add("设置", null, (_, _) => ShowSettingsDialog());
        _moreActionsMenu.Items.Add("本地库管理", null, (_, _) => ShowRepositoryManagerDialog());
        _moreActionsMenu.Items.Add("环境检测", null, async (_, _) => await ShowEnvironmentCheckAsync());
        _moreActionsMenu.Items.Add(new ToolStripSeparator());
        _moreActionsMenu.Items.Add("查看改动", null, async (_, _) => await RefreshStatusAsync());
        _moreActionsMenu.Items.Add("查看差异", null, async (_, _) => await RunDiffAsync());
        _moreActionsMenu.Items.Add("内置表格三方合并", null, async (_, _) => await RunInternalSpreadsheetMergeAsync());
        _moreActionsMenu.Items.Add("跨库表格三方合并", null, async (_, _) => await RunCrossRepositorySpreadsheetMergeAsync());
        _moreActionsMenu.Items.Add("外部对比/合并", null, async (_, _) => await RunExternalCompareOrMergeAsync());
        _moreActionsMenu.Items.Add("冲突处理", null, async (_, _) => await RunConflictWorkflowAsync());
        _moreActionsMenu.Items.Add("文件历史", null, async (_, _) => await RunFileHistoryAsync());
        _moreActionsMenu.Items.Add(new ToolStripSeparator());
        _moreActionsMenu.Items.Add("SVN 清理工作副本", null, async (_, _) => await RunCleanupAsync());
        _moreActionsMenu.Items.Add("查看忽略清单", null, async (_, _) => await ShowIgnoreListAsync());
        _moreActionsMenu.Items.Add(new ToolStripSeparator());
        _moreActionsMenu.Items.Add("全部文件：刷新", null, (_, _) => LoadAllFiles());
        AddFavoritePathsMenuItems(_moreActionsMenu.Items);
        _moreActionsMenu.Items.Add(new ToolStripSeparator());
        _moreActionsMenu.Items.Add(_settings.UiLayout.LayoutLocked ? "解锁当前布局" : "锁定当前布局", null, (_, _) => ToggleLayoutLock());
        _moreActionsMenu.Items.Add("重置界面布局", null, (_, _) => ResetUiLayout());
        _moreActionsMenu.Items.Add("检查工具更新", null, async (_, _) => await ShowToolUpdatePanelAsync());
        _moreActionsMenu.Items.Add("最近操作时间线", null, (_, _) => ShowRecentOperations());
        _moreActionsMenu.Items.Add("打开操作日志", null, (_, _) => OpenOperationLog());
    }

    private void AddFavoritePathsMenuItems(ToolStripItemCollection items)
    {
        var favoritesMenu = new ToolStripMenuItem("常用目录");
        if (_settings.FavoriteFileTreePaths.Count == 0)
        {
            favoritesMenu.DropDownItems.Add("暂无收藏", null, (_, _) => { }).Enabled = false;
        }
        else
        {
            foreach (var path in _settings.FavoriteFileTreePaths.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                favoritesMenu.DropDownItems.Add(path, null, async (_, _) => await NavigateToFavoriteFileTreePathAsync(path));
            }
        }

        items.Add(favoritesMenu);
    }

    private void ShowSettingsDialog()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.ExternalMergeToolPath = form.ExternalMergeToolPath;
        _settings.Save();
        WriteOutput(string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath)
            ? "已清空分久必合路径。"
            : $"已保存分久必合路径：{_settings.ExternalMergeToolPath}");
    }

    private void ShowRepositoryManagerDialog()
    {
        using var form = new RepositoryManagerForm(_settings, _svn);
        form.ShowDialog(this);
        if (!form.Changed)
        {
            return;
        }

        _settings.Save();
        RefreshRepositorySelector();
        ApplyCurrentRepositoryToUi();
        WriteOutput("已更新本地库列表。");
    }

    private async Task ShowEnvironmentCheckAsync()
    {
        SetBusy(true, "正在执行环境检测...");
        try
        {
            var items = await BuildEnvironmentCheckItemsAsync();
            using var form = new EnvironmentCheckForm(items);
            form.ShowDialog(this);
            WriteEnvironmentCheckSummary(items);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunStartupEnvironmentCheckAsync()
    {
        try
        {
            var items = await BuildEnvironmentCheckItemsAsync();
            WriteEnvironmentCheckSummary(items, onlyWhenHasIssues: true);
        }
        catch (Exception ex)
        {
            WriteOutput("环境检测失败：" + ex.Message);
        }
    }

    private async Task<IReadOnlyList<EnvironmentCheckItem>> BuildEnvironmentCheckItemsAsync()
    {
        var items = new List<EnvironmentCheckItem>();
        await CheckSvnCommandAsync(items);
        CheckSavedRepository(items);
        await CheckWorkingCopyAsync(items);
        CheckExternalMergeTool(items);
        CheckInstallDirectoryWritable(items);
        CheckOperationLogWritable(items);
        return items;
    }

    private async Task CheckSvnCommandAsync(List<EnvironmentCheckItem> items)
    {
        try
        {
            var result = await _svn.VersionAsync();
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                items.Add(EnvironmentCheckItem.Ok("SVN 命令", $"svn {result.StandardOutput.Trim()}", "SVN 命令行可用。"));
                return;
            }

            items.Add(EnvironmentCheckItem.Error("SVN 命令", "svn 命令执行失败", result.CombinedOutput.Trim()));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Error(
                "SVN 命令",
                "找不到 svn 命令行",
                "请安装 TortoiseSVN 时勾选 command line tools，或安装 Apache Subversion，并确认 svn.exe 在 PATH 中。" + Environment.NewLine + ex.Message));
        }
    }

    private void CheckSavedRepository(List<EnvironmentCheckItem> items)
    {
        var repository = _settings.GetCurrentRepository();
        if (repository == null)
        {
            items.Add(EnvironmentCheckItem.Warning("本地库", "还没有保存本地库", "请在配置页导入已有 SVN 工作副本，或检出一个新库。"));
            return;
        }

        items.Add(EnvironmentCheckItem.Ok("本地库", repository.Name, repository.WorkingCopyPath));
    }

    private async Task CheckWorkingCopyAsync(List<EnvironmentCheckItem> items)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            items.Add(EnvironmentCheckItem.Warning("工作副本", "未选择本地目录", "请选择一个包含 .svn 的工作副本目录。"));
            return;
        }

        if (!Directory.Exists(workingCopy))
        {
            items.Add(EnvironmentCheckItem.Error("工作副本", "本地目录不存在", workingCopy));
            return;
        }

        if (!Directory.Exists(Path.Combine(workingCopy, ".svn")))
        {
            items.Add(EnvironmentCheckItem.Error("工作副本", "不是 SVN 工作副本", "目录存在，但没有找到 .svn：" + workingCopy));
            return;
        }

        try
        {
            var info = await Task.Run(() => _svn.GetWorkingCopyInfo(workingCopy));
            var detail = info == WorkingCopyInfo.Empty
                ? workingCopy
                : $"{info.DisplayRevisionText}  {info.Url}";
            items.Add(EnvironmentCheckItem.Ok("工作副本", "SVN 工作副本正常", detail));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Error("工作副本", "SVN 信息读取失败", ex.Message));
        }
    }

    private void CheckExternalMergeTool(List<EnvironmentCheckItem> items)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath))
        {
            items.Add(EnvironmentCheckItem.Warning("分久必合", "未配置外部合并工具", "XML 表格仍可看内置差异，但冲突合并建议在“更多操作 -> 设置”中配置分久必合.exe。"));
            return;
        }

        if (File.Exists(_settings.ExternalMergeToolPath))
        {
            items.Add(EnvironmentCheckItem.Ok("分久必合", "外部合并工具可用", _settings.ExternalMergeToolPath));
            return;
        }

        items.Add(EnvironmentCheckItem.Error("分久必合", "配置的路径不存在", _settings.ExternalMergeToolPath));
    }

    private static void CheckInstallDirectoryWritable(List<EnvironmentCheckItem> items)
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var testPath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            items.Add(EnvironmentCheckItem.Ok("安装目录", "可写", directory));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Warning("安装目录", "当前目录不可写，自动更新可能失败", $"{directory}{Environment.NewLine}{ex.Message}"));
        }
    }

    private static void CheckOperationLogWritable(List<EnvironmentCheckItem> items)
    {
        try
        {
            var logPath = OperationLogger.EnsureLogFile();
            items.Add(EnvironmentCheckItem.Ok("操作日志", "可写", logPath));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Warning("操作日志", "日志目录不可写", ex.Message));
        }
    }

    private void WriteEnvironmentCheckSummary(IReadOnlyList<EnvironmentCheckItem> items, bool onlyWhenHasIssues = false)
    {
        var errors = items.Count(item => item.Level == EnvironmentCheckLevel.Error);
        var warnings = items.Count(item => item.Level == EnvironmentCheckLevel.Warning);
        if (onlyWhenHasIssues && errors == 0 && warnings == 0)
        {
            return;
        }

        WriteOutput(errors == 0 && warnings == 0
            ? "环境检测通过。"
            : $"环境检测发现 {errors} 个错误、{warnings} 个提醒。请在“更多操作 -> 环境检测”查看详情。");
    }

    private void OpenOperationLog()
    {
        var logPath = OperationLogger.EnsureLogFile();
        Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
    }

    private void ShowRecentOperations()
    {
        try
        {
            var logPath = OperationLogger.EnsureLogFile();
            var lines = File.Exists(logPath)
                ? File.ReadLines(logPath).Reverse().Take(160).Reverse().ToList()
                : [];
            using var form = new Form
            {
                Text = "最近操作时间线",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(980, 620),
                MinimumSize = new Size(760, 420),
                Font = new Font("Microsoft YaHei UI", 9F),
            };
            form.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F),
                Text = lines.Count == 0 ? "暂无操作记录。" : string.Join(Environment.NewLine, lines),
            });
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private Control CreateStatusPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateStatusToolbar(), 0, 0);
        root.Controls.Add(CreateStatusFilterBar(), 0, 1);
        root.Controls.Add(_changesList, 0, 2);
        return root;
    }

    private Control CreateStatusToolbar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(241, 243, 245),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        panel.Controls.Add(new Label
        {
            Text = "本地改动",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.Controls.Add(CreateSmallToolbarButton("刷新改动", async () => await RefreshStatusAsync()), 1, 0);
        panel.Controls.Add(CreateSmallToolbarButton("全选", () => SetAllChecks(true)), 2, 0);
        panel.Controls.Add(CreateSmallToolbarButton("全不选", () => SetAllChecks(false)), 3, 0);
        return panel;
    }

    private Control CreateStatusFilterBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.White,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        _statusSearchText.Dock = DockStyle.Fill;
        _statusSearchText.Margin = new Padding(0, 4, 6, 4);
        _statusSearchText.PlaceholderText = "搜索文件名 / 路径";
        _statusSearchText.TextChanged += (_, _) => ApplyStatusFilter();
        panel.Controls.Add(_statusSearchText, 0, 0);

        _statusFilterCombo.Dock = DockStyle.Fill;
        _statusFilterCombo.Margin = new Padding(0, 4, 6, 4);
        _statusFilterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var text in ChangedFilesFilter.Options)
        {
            _statusFilterCombo.Items.Add(text);
        }

        _statusFilterCombo.SelectedIndex = 0;
        _statusFilterCombo.SelectedIndexChanged += (_, _) => ApplyStatusFilter();
        panel.Controls.Add(_statusFilterCombo, 1, 0);

        _statusCommitVisibleOnlyCheck.Dock = DockStyle.Fill;
        _statusCommitVisibleOnlyCheck.Margin = new Padding(0, 5, 8, 4);
        _statusCommitVisibleOnlyCheck.Text = "只提交当前筛选结果";
        _statusCommitVisibleOnlyCheck.TextAlign = ContentAlignment.MiddleLeft;
        _statusCommitVisibleOnlyCheck.CheckedChanged += (_, _) => UpdateStatusFilterSummary(_changesList.Items.Count);
        panel.Controls.Add(_statusCommitVisibleOnlyCheck, 2, 0);

        _statusFilterSummaryLabel.Dock = DockStyle.Fill;
        _statusFilterSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusFilterSummaryLabel.ForeColor = Color.FromArgb(90, 100, 115);
        _statusFilterSummaryLabel.Text = "显示 0/0，已勾选 0";
        panel.Controls.Add(_statusFilterSummaryLabel, 3, 0);
        return panel;
    }

    private void BuildChangesListMenu()
    {
        _changesListMenu.Items.Clear();
        _changesListMenu.Items.Add("查看差异", null, async (_, _) => await RunDiffAsync());
        _changesListMenu.Items.Add("和另一个表快速比对...", null, async (_, _) => await CompareSelectedTableWithAnotherAsync());
        _changesListMenu.Items.Add("内置表格三方合并", null, async (_, _) => await RunInternalSpreadsheetMergeAsync());
        _changesListMenu.Items.Add("跨库表格三方合并", null, async (_, _) => await RunCrossRepositorySpreadsheetMergeAsync());
        _changesListMenu.Items.Add("打开文件", null, (_, _) => OpenSelectedStatusFile());
        _changesListMenu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedStatusFileFolder());
        _changesListMenu.Items.Add(new ToolStripSeparator());
        _changesListMenu.Items.Add("锁定文件", null, async (_, _) => await LockSelectedFileAsync());
        _changesListMenu.Items.Add("解锁文件", null, async (_, _) => await UnlockSelectedFileAsync());
        _changesListMenu.Items.Add("查看锁信息", null, async (_, _) => await ShowSelectedFileLockInfoAsync());
        _changesListMenu.Items.Add(new ToolStripSeparator());
        _changesListMenu.Items.Add("还原到 SVN 最新版本...", null, async (_, _) => await RevertSelectedStatusChangesToLatestAsync());
        _changesListMenu.Items.Add("加入忽略清单", null, async (_, _) => await AddSelectedPathsToIgnoreAsync());
        _changesListMenu.Items.Add("移出忽略清单", null, async (_, _) => await RemoveSelectedPathsFromIgnoreAsync());
        _changesListMenu.Opening += (_, args) =>
        {
            var selected = GetSelectedStatusChanges();
            args.Cancel = selected.Count == 0;
            foreach (ToolStripItem item in _changesListMenu.Items)
            {
                if (item is ToolStripSeparator)
                {
                    continue;
                }

                item.Enabled = selected.Count > 0;
            }
        };
    }

    private void SelectChangeItemForContextMenu(MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right)
        {
            return;
        }

        var item = _changesList.GetItemAt(args.X, args.Y);
        if (item == null)
        {
            return;
        }

        if (!item.Selected)
        {
            _changesList.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
        }
    }

    private Control CreateHistoryTopPanel(Control content)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreatePanelToolbar("提交历史", "刷新历史", async () => await LoadRepositoryHistoryAsync()), 0, 0);
        root.Controls.Add(content, 0, 1);
        return root;
    }

    private Control CreateAllFilesPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Color.FromArgb(241, 243, 245),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));

        _fileTreeSearchText.Dock = DockStyle.Fill;
        _fileTreeSearchText.PlaceholderText = "搜索文件名 / 路径";
        _fileTreeSearchText.Margin = new Padding(0, 3, 8, 3);
        _fileTreeSearchText.TextChanged += (_, _) => ScheduleFileTreeLoad();
        toolbar.Controls.Add(_fileTreeSearchText, 0, 0);

        _fileTreeChangedOnlyCheck.Text = "仅改动";
        _fileTreeChangedOnlyCheck.Dock = DockStyle.Fill;
        _fileTreeChangedOnlyCheck.TextAlign = ContentAlignment.MiddleCenter;
        _fileTreeChangedOnlyCheck.CheckedChanged += (_, _) => ScheduleFileTreeLoad();
        toolbar.Controls.Add(_fileTreeChangedOnlyCheck, 1, 0);

        _fileTreeExpandButton.Text = "展开";
        _fileTreeExpandButton.Dock = DockStyle.Fill;
        _fileTreeExpandButton.Margin = new Padding(0, 3, 6, 3);
        _fileTreeExpandButton.Click += (_, _) => ExpandFileTreeSafely();
        toolbar.Controls.Add(_fileTreeExpandButton, 2, 0);

        _fileTreeCollapseButton.Text = "折叠";
        _fileTreeCollapseButton.Dock = DockStyle.Fill;
        _fileTreeCollapseButton.Margin = new Padding(0, 3, 6, 3);
        _fileTreeCollapseButton.Click += (_, _) => CollapseFileTreeToRoot();
        toolbar.Controls.Add(_fileTreeCollapseButton, 3, 0);

        _fileTreeRefreshButton.Text = "刷新";
        _fileTreeRefreshButton.Dock = DockStyle.Fill;
        _fileTreeRefreshButton.Margin = new Padding(0, 3, 6, 3);
        _fileTreeRefreshButton.Click += (_, _) =>
        {
            if (_fileTreeLoadCts != null)
            {
                CancelFileTreeLoad();
                return;
            }

            LoadAllFiles();
        };
        toolbar.Controls.Add(_fileTreeRefreshButton, 4, 0);

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(_fileTree, 0, 1);
        return root;
    }

    private static Button CreateSmallToolbarButton(string text, Action action)
    {
        var button = CreateToolbarButtonBase(text);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button CreateSmallToolbarButton(string text, Func<Task> action)
    {
        var button = CreateToolbarButtonBase(text);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Button CreateToolbarButtonBase(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3),
        };
    }

    private static void ConfigureHistorySearchButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 6, 4);
    }

    internal static Control CreateChangedFilesFilterPanel(
        string title,
        TreeView tree,
        TextBox searchText,
        ComboBox filterCombo,
        Action applyFilter)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(241, 243, 245),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));

        searchText.Dock = DockStyle.Fill;
        searchText.Margin = new Padding(0, 4, 6, 4);
        searchText.PlaceholderText = "搜索文件名 / 路径";
        searchText.TextChanged += (_, _) => applyFilter();
        filters.Controls.Add(searchText, 0, 0);

        filterCombo.Dock = DockStyle.Fill;
        filterCombo.Margin = new Padding(0, 4, 0, 4);
        filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        filterCombo.Items.Clear();
        foreach (var text in ChangedFilesFilter.Options)
        {
            filterCombo.Items.Add(text);
        }

        filterCombo.SelectedIndex = 0;
        filterCombo.SelectedIndexChanged += (_, _) => applyFilter();
        filters.Controls.Add(filterCombo, 1, 0);

        panel.Controls.Add(filters, 0, 1);
        panel.Controls.Add(tree, 0, 2);
        return panel;
    }

    private static Control CreatePanelToolbar(string title, string buttonText, Func<Task> refresh)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(241, 243, 245),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        var button = CreateSmallToolbarButton(buttonText, refresh);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    private Control CreateConflictPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreatePanelToolbar("冲突文件", "刷新冲突", async () => await RefreshStatusAsync()), 0, 0);

        _conflictSummaryLabel.Dock = DockStyle.Fill;
        _conflictSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _conflictSummaryLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _conflictSummaryLabel.Text = "当前没有读取冲突状态。";
        root.Controls.Add(_conflictSummaryLabel, 0, 1);

        _conflictGrid.Dock = DockStyle.Fill;
        _conflictGrid.AllowUserToAddRows = false;
        _conflictGrid.AllowUserToDeleteRows = false;
        _conflictGrid.AutoGenerateColumns = false;
        _conflictGrid.ReadOnly = true;
        _conflictGrid.RowHeadersVisible = false;
        _conflictGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _conflictGrid.MultiSelect = false;
        _conflictGrid.BackgroundColor = Color.White;
        _conflictGrid.CellContentClick += async (_, args) => await HandleConflictGridClickAsync(args);
        _conflictGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "冲突文件", DataPropertyName = nameof(ConflictGridRow.RelativePath), Width = 620 });
        _conflictGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", DataPropertyName = nameof(ConflictGridRow.Description), Width = 220 });
        _conflictGrid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "内置合并", Name = "InternalMerge", Text = "内置合并", UseColumnTextForButtonValue = true, Width = 110 });
        _conflictGrid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "外部合并", Name = "OpenMerge", Text = "外部合并", UseColumnTextForButtonValue = true, Width = 110 });
        _conflictGrid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "打开目录", Name = "OpenFolder", Text = "打开目录", UseColumnTextForButtonValue = true, Width = 110 });
        _conflictGrid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "标记解决", Name = "Resolve", Text = "标记解决", UseColumnTextForButtonValue = true, Width = 110 });
        root.Controls.Add(_conflictGrid, 0, 2);
        return root;
    }

    private static void ApplyControlStyle(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Button button)
            {
                button.Height = 28;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(180, 186, 194);
                button.BackColor = Color.FromArgb(248, 249, 250);
                button.ForeColor = Color.FromArgb(20, 31, 43);
            }
            else if (control is TabControl tabControl)
            {
                tabControl.BackColor = Color.White;
            }

            ApplyControlStyle(control);
        }
    }

    private void RestoreUiLayout()
    {
        var layout = _settings.UiLayout;
        if (layout.WindowWidth >= MinimumSize.Width && layout.WindowHeight >= MinimumSize.Height)
        {
            var bounds = new Rectangle(layout.WindowX, layout.WindowY, layout.WindowWidth, layout.WindowHeight);
            if (IsVisibleOnAnyScreen(bounds))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }

        SafeSetSplitterDistance(_workspaceSplit, layout.WorkspaceSplitterDistance);
        var historyDistance = layout.HistorySplitterDistance <= 0 ? 240 : Math.Min(layout.HistorySplitterDistance, 260);
        SafeSetSplitterDistance(_historySplit, historyDistance);
        SafeSetSplitterDistance(_changedFilesSplit, layout.ChangedFilesSplitterDistance);

        if (layout.IsMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private void SaveUiLayout()
    {
        if (_settings.UiLayout.LayoutLocked)
        {
            return;
        }

        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.UiLayout.WindowX = bounds.X;
        _settings.UiLayout.WindowY = bounds.Y;
        _settings.UiLayout.WindowWidth = bounds.Width;
        _settings.UiLayout.WindowHeight = bounds.Height;
        _settings.UiLayout.IsMaximized = WindowState == FormWindowState.Maximized;
        _settings.UiLayout.WorkspaceSplitterDistance = _workspaceSplit.SplitterDistance;
        _settings.UiLayout.HistorySplitterDistance = _historySplit.SplitterDistance;
        _settings.UiLayout.ChangedFilesSplitterDistance = _changedFilesSplit.SplitterDistance;
        _settings.UiLayout.SelectedTab = GetBaseTabText(_mainTabs.SelectedTab?.Text ?? "History");
        _settings.Save();
    }

    private void ToggleLayoutLock()
    {
        _settings.UiLayout.LayoutLocked = !_settings.UiLayout.LayoutLocked;
        if (!_settings.UiLayout.LayoutLocked)
        {
            SaveUiLayout();
        }
        else
        {
            _settings.Save();
        }

        BuildMoreActionsMenu();
        WriteOutput(_settings.UiLayout.LayoutLocked ? "已锁定当前界面布局。" : "已解锁界面布局。");
    }

    private void ResetUiLayout()
    {
        var result = MessageBox.Show(
            this,
            "确认重置窗口大小、分栏位置和当前页签？",
            "重置界面布局",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (result != DialogResult.OK)
        {
            return;
        }

        _settings.UiLayout = new UiLayoutSettings();
        _settings.Save();
        WindowState = FormWindowState.Normal;
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        SafeSetSplitterDistance(_workspaceSplit, 170);
        SafeSetSplitterDistance(_historySplit, 240);
        SafeSetSplitterDistance(_changedFilesSplit, 430);
        SelectTab("History");
        WriteOutput("界面布局已重置。");
    }

    internal static void SafeSetSplitterDistance(SplitContainer split, int distance)
    {
        if (split.IsDisposed || distance <= 0 || split.Width <= 0 || split.Height <= 0)
        {
            return;
        }

        var available = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        var max = available - split.SplitterWidth - split.Panel2MinSize;
        var min = split.Panel1MinSize;
        if (max <= min)
        {
            return;
        }

        try
        {
            split.SplitterDistance = Math.Max(min, Math.Min(distance, max));
        }
        catch (InvalidOperationException)
        {
            // WinForms can reject splitter updates during transient layout states.
        }
    }

    internal static void BindSafeSplitterDistance(SplitContainer split, int distance)
    {
        split.HandleCreated += (_, _) =>
        {
            if (!split.IsDisposed)
            {
                try
                {
                    split.BeginInvoke(new Action(() => SafeSetSplitterDistance(split, distance)));
                }
                catch (InvalidOperationException)
                {
                    SafeSetSplitterDistance(split, distance);
                }
            }
        };
        split.SizeChanged += (_, _) => SafeSetSplitterDistance(split, distance);
    }

    private static bool IsVisibleOnAnyScreen(Rectangle bounds)
    {
        return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }

    private void LoadSettingsIntoUi()
    {
        _settings.MigrateLegacySettings();
        RefreshRepositorySelector();
        var selected = _settings.GetCurrentRepository();
        _repoUrlText.Text = selected?.RepositoryUrl ?? "";
        _workingCopyText.Text = selected?.WorkingCopyPath ?? "";
        LoadAllFiles();
        SelectTab(string.IsNullOrWhiteSpace(_settings.UiLayout.SelectedTab) ? "History" : _settings.UiLayout.SelectedTab);
    }

    private async Task RunCheckoutAsync()
    {
        if (!ValidateRepositoryUrl() || !ValidateWorkingCopyPath(allowMissing: true))
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        if (Directory.Exists(workingCopy) && Directory.EnumerateFileSystemEntries(workingCopy).Any())
        {
            if (Directory.Exists(Path.Combine(workingCopy, ".svn")))
            {
                SaveCurrentRepository();
                WriteOutput($"已保存已有 SVN 工作副本：{workingCopy}");
                await RefreshStatusAsync();
                return;
            }

            MessageBox.Show("本地目录不是空目录。为了避免覆盖已有文件，请选择一个空目录或已有 SVN 工作副本。", "无法检出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunSvnOperationAsync("正在检出...", async () =>
        {
            Directory.CreateDirectory(workingCopy);
            var result = await _svn.CheckoutAsync(_repoUrlText.Text.Trim(), workingCopy);
            SaveCurrentRepository();
            return result;
        });
        await RefreshStatusAsync();
    }

    private async Task RunUpdateAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var preflightChanges = await _svn.GetStatusAsync(workingCopy);
        if (!ConfirmUpdateWithLocalChanges(preflightChanges))
        {
            OperationLogger.Log("UpdateCancelled", workingCopy, $"localChanges={preflightChanges.Count}");
            WriteOutput("已取消拉取最新：当前有未提交改动。");
            return;
        }

        OperationLogger.Log("UpdateStart", workingCopy, $"localChanges={preflightChanges.Count}");
        await RunSvnOperationAsync("正在拉取最新...", async () =>
        {
            SaveSettings();
            return await _svn.UpdateAsync(workingCopy);
        });
        OperationLogger.Log("UpdateFinish", workingCopy, "svn update finished");
        await RefreshStatusAsync();
        await CheckRemoteChangesAsync(showUpToDateMessage: false);
    }

    private bool ConfirmUpdateWithLocalChanges(IReadOnlyList<SvnChange> changes)
    {
        if (changes.Count == 0)
        {
            return true;
        }

        var conflicts = changes.Count(change => change.Status == SvnStatusKind.Conflicted);
        var unversioned = changes.Count(change => change.Status == SvnStatusKind.Unversioned);
        var message =
            $"当前有 {changes.Count} 个未提交改动。直接拉取最新可能产生冲突。{Environment.NewLine}{Environment.NewLine}" +
            $"冲突：{conflicts} 个{Environment.NewLine}" +
            $"未加入版本控制：{unversioned} 个{Environment.NewLine}{Environment.NewLine}" +
            "建议先查看改动、提交或备份，再拉取最新。仍然继续拉取？";
        var result = MessageBox.Show(
            message,
            "拉取最新前确认",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.OK;
    }

    private async Task RunCleanupAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        using var form = new CleanupOptionsForm(workingCopy);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var options = form.Options;
        if (options.HasDestructiveActions)
        {
            var destructive = new List<string>();
            if (options.DeleteUnversioned) destructive.Add("删除未加入版本控制的文件和文件夹");
            if (options.DeleteIgnored) destructive.Add("删除已忽略的文件和文件夹");
            if (options.RevertAllRecursive) destructive.Add("递归还原所有本地改动");
            var message =
                "你选择了会删除或丢弃本地内容的 Clean Up 选项：" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, destructive.Select(item => "- " + item)) +
                Environment.NewLine + Environment.NewLine +
                "这些操作不能在工具内撤销。确定继续？";
            if (MessageBox.Show(message, "危险操作确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                OperationLogger.Log("CleanupCancelled", workingCopy, options.ToLogText());
                return;
            }
        }

        OperationLogger.Log("CleanupStart", workingCopy, options.ToLogText());
        var result = await RunSvnOperationAsync("正在执行 SVN 清理...", async () => await _svn.CleanupAsync(workingCopy, options));
        OperationLogger.Log(result?.ExitCode == 0 ? "CleanupSuccess" : "CleanupFailed", workingCopy, "");
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ShowIgnoreListAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        SetBusy(true, "正在读取忽略清单...");
        try
        {
            var result = await _svn.GetIgnoreListAsync(workingCopy);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                WriteOutput(result.StandardOutput);
                return;
            }

            if (result.ExitCode == 0 || IsMissingIgnoreProperty(result))
            {
                WriteOutput("当前工作副本没有设置 svn:ignore。");
                return;
            }

            WriteOutput(result.CombinedOutput);
            MessageBox.Show("读取忽略清单失败，请查看终端输出。", "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task AddSelectedPathsToIgnoreAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var selectedPaths = GetSelectedIgnoreCandidatePaths();
        if (selectedPaths.Count == 0)
        {
            MessageBox.Show("请先在 File Status 或全部文件里选中要忽略的文件/文件夹。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var statusMap = (await _svn.GetStatusAsync(workingCopy))
            .ToDictionary(change => NormalizeRelativePath(change.RelativePath), change => change.Status, StringComparer.OrdinalIgnoreCase);
        var targetPairs = selectedPaths
            .Select(path => new { Selected = path, Target = FindUnversionedIgnoreTarget(path, statusMap) })
            .ToList();
        var unversionedPaths = targetPairs
            .Select(pair => pair.Target)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skippedPaths = targetPairs
            .Where(pair => string.IsNullOrWhiteSpace(pair.Target))
            .Select(pair => pair.Selected)
            .ToList();
        if (unversionedPaths.Count == 0)
        {
            MessageBox.Show("选中的路径都不是“未加入版本控制”状态，不能加入 svn:ignore。", "不能忽略", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"准备把 {unversionedPaths.Count} 个未加入版本控制的路径加入 svn:ignore。{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, unversionedPaths.Take(12)) +
            (unversionedPaths.Count > 12 ? Environment.NewLine + "..." : "");
        if (skippedPaths.Count > 0)
        {
            message += $"{Environment.NewLine}{Environment.NewLine}会跳过 {skippedPaths.Count} 个已受 SVN 管理或状态不明确的路径。";
        }

        if (MessageBox.Show(message, "加入忽略清单", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var result = await UpdateIgnorePropertiesAsync(workingCopy, unversionedPaths, add: true);
        OperationLogger.Log(result.ExitCode == 0 ? "AddIgnoreSuccess" : "AddIgnoreFailed", workingCopy, string.Join(" | ", unversionedPaths));
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task RemoveSelectedPathsFromIgnoreAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selectedPaths = GetSelectedIgnoreCandidatePaths();
        if (selectedPaths.Count == 0)
        {
            MessageBox.Show("请先在全部文件里选中要移出忽略清单的文件/文件夹。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var message =
            $"准备从 svn:ignore 里移除 {selectedPaths.Count} 个名称。{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, selectedPaths.Take(12)) +
            (selectedPaths.Count > 12 ? Environment.NewLine + "..." : "");
        if (MessageBox.Show(message, "移出忽略清单", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var result = await UpdateIgnorePropertiesAsync(workingCopy, selectedPaths, add: false);
        OperationLogger.Log(result.ExitCode == 0 ? "RemoveIgnoreSuccess" : "RemoveIgnoreFailed", workingCopy, string.Join(" | ", selectedPaths));
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task<ProcessResult> UpdateIgnorePropertiesAsync(string workingCopy, IReadOnlyList<string> relativePaths, bool add)
    {
        SetBusy(true, add ? "正在加入忽略清单..." : "正在移出忽略清单...");
        try
        {
            var output = new StringBuilder();
            var exitCode = 0;
            foreach (var group in BuildIgnoreGroups(relativePaths))
            {
                var current = await _svn.GetIgnoreAsync(workingCopy, group.ParentPath);
                if (current.ExitCode != 0 && !IsMissingIgnoreProperty(current))
                {
                    output.AppendLine(current.CombinedOutput);
                    exitCode = current.ExitCode;
                    continue;
                }

                var names = ParseIgnoreNames(current.StandardOutput);
                var changed = false;
                foreach (var name in group.Names)
                {
                    if (add)
                    {
                        changed |= names.Add(name);
                    }
                    else
                    {
                        changed |= names.Remove(name);
                    }
                }

                if (!changed)
                {
                    output.AppendLine($"{DisplayIgnoreParent(group.ParentPath)}：没有需要{(add ? "加入" : "移除")}的 ignore 项。");
                    continue;
                }

                var update = await _svn.SetIgnoreAsync(workingCopy, group.ParentPath, names);
                output.AppendLine(update.CombinedOutput);
                if (update.ExitCode != 0)
                {
                    exitCode = update.ExitCode;
                    continue;
                }

                output.AppendLine($"{DisplayIgnoreParent(group.ParentPath)}：已{(add ? "加入" : "移除")} {group.Names.Count} 项。");
            }

            return new ProcessResult(exitCode, output.ToString(), "");
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return new ProcessResult(-1, "", ex.Message);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private IReadOnlyList<string> GetSelectedIgnoreCandidatePaths()
    {
        var statusPaths = GetSelectedStatusChanges()
            .Select(change => SvnConflictArtifact.NormalizeToBasePath(change.RelativePath));
        var treePaths = GetSelectedFileTreeHistoryPaths();
        return statusPaths.Concat(treePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindUnversionedIgnoreTarget(string path, IReadOnlyDictionary<string, SvnStatusKind> statusMap)
    {
        var normalized = NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (statusMap.TryGetValue(normalized, out var status) && status == SvnStatusKind.Unversioned)
        {
            return normalized;
        }

        var current = normalized;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return null;
            }

            parent = NormalizeRelativePath(parent).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (statusMap.TryGetValue(parent, out status) && status == SvnStatusKind.Unversioned)
            {
                return parent;
            }

            current = parent;
        }

        return null;
    }

    private static IReadOnlyList<IgnoreGroup> BuildIgnoreGroups(IEnumerable<string> relativePaths)
    {
        return relativePaths
            .Select(path => NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(
                path => NormalizeIgnoreParent(Path.GetDirectoryName(path)),
                path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new IgnoreGroup(
                group.Key,
                group.Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(group => group.Names.Count > 0)
            .ToList();
    }

    private static HashSet<string> ParseIgnoreNames(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMissingIgnoreProperty(ProcessResult result)
    {
        var text = result.CombinedOutput;
        return text.Contains("W200017", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("不存在", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIgnoreParent(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? "."
            : NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string DisplayIgnoreParent(string parentPath)
    {
        return parentPath == "." ? "工作副本根目录" : parentPath;
    }

    private async Task RevertSelectedStatusChangesToLatestAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selected = GetSelectedStatusChanges()
            .DistinctBy(change => NormalizeRelativePath(change.RelativePath))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var unsupported = selected
            .Where(change => change.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added or SvnStatusKind.Conflicted)
            .ToList();
        var changes = selected.Except(unsupported).ToList();
        if (unsupported.Count > 0)
        {
            MessageBox.Show(
                "以下类型不会自动还原到最新：\r\n\r\n" +
                "- 未加入版本控制：需要手动删除或加入版本控制\r\n" +
                "- 新增文件：svn revert 后会变成未加入文件，容易误解\r\n" +
                "- 冲突文件：请走“冲突处理流程”\r\n\r\n" +
                "本次会跳过这些文件：\r\n" +
                string.Join(Environment.NewLine, unsupported.Take(8).Select(change => $"{change.DisplayStatus} {change.RelativePath}")) +
                (unsupported.Count > 8 ? Environment.NewLine + "..." : ""),
                "部分文件不能自动还原",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        if (changes.Count == 0)
        {
            return;
        }

        if (!ConfirmRevertToLatest(changes))
        {
            OperationLogger.Log("RevertToLatestCancelled", _workingCopyText.Text.Trim(), $"files={changes.Count}");
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        SetBusy(true, "正在还原到 SVN 最新版本...");
        try
        {
            var output = new StringBuilder();
            foreach (var change in changes)
            {
                var revert = await _svn.RevertAsync(workingCopy, change.RelativePath);
                output.AppendLine(revert.CombinedOutput);
                if (revert.ExitCode != 0)
                {
                    continue;
                }

                var update = await _svn.UpdatePathAsync(workingCopy, change.RelativePath);
                output.AppendLine(update.CombinedOutput);
            }

            OperationLogger.Log("RevertToLatest", workingCopy, string.Join(" | ", changes.Select(change => change.RelativePath)));
            WriteOutput(output.ToString());
            await RefreshStatusAsync();
            LoadAllFiles();
            await LoadRepositoryHistoryAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private bool ConfirmRevertToLatest(IReadOnlyList<SvnChange> changes)
    {
        var message =
            $"准备把 {changes.Count} 个文件还原到 SVN 最新版本。{Environment.NewLine}{Environment.NewLine}" +
            "这会丢弃这些文件的本地改动，然后执行 svn update 拉取最新内容。此操作不能在工具内撤销。" +
            Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, changes.Take(10).Select(change => $"{change.DisplayStatus} {change.RelativePath}")) +
            (changes.Count > 10 ? Environment.NewLine + "..." : "") +
            Environment.NewLine + Environment.NewLine +
            "确认继续？";
        return MessageBox.Show(
            message,
            "还原到 SVN 最新版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private async Task LockSelectedFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        OperationLogger.Log("LockFileStart", workingCopy, relativePath);
        var result = await RunSvnOperationAsync("正在锁定文件...", async () => await _svn.LockAsync(workingCopy, relativePath));
        OperationLogger.Log(result?.ExitCode == 0 ? "LockFileSuccess" : "LockFileFailed", workingCopy, relativePath);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task UnlockSelectedFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        OperationLogger.Log("UnlockFileStart", workingCopy, relativePath);
        var result = await RunSvnOperationAsync("正在解锁文件...", async () => await _svn.UnlockAsync(workingCopy, relativePath));
        OperationLogger.Log(result?.ExitCode == 0 ? "UnlockFileSuccess" : "UnlockFileFailed", workingCopy, relativePath);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ShowSelectedFileLockInfoAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取锁信息...");
        try
        {
            var result = await _svn.InfoAsync(_workingCopyText.Text.Trim(), relativePath);
            WriteOutput(result.CombinedOutput);
            MessageBox.Show(
                BuildLockInfoMessage(relativePath, result),
                "SVN 锁信息",
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private static string BuildLockInfoMessage(string relativePath, ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return $"读取锁信息失败：{relativePath}{Environment.NewLine}{Environment.NewLine}{result.CombinedOutput}";
        }

        var lines = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lockLines = lines
            .Where(line =>
                line.StartsWith("Lock Owner:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Created:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Comment", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Token:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return lockLines.Count == 0
            ? $"当前文件没有检测到 SVN 锁。{Environment.NewLine}{Environment.NewLine}{relativePath}"
            : $"当前文件锁信息：{relativePath}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, lockLines)}";
    }

    private async Task RefreshStatusAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        ClearHistoryDiffPreviewCache();
        SetBusy(true, "正在查看改动...");
        try
        {
            SaveSettings();
            var changes = (await _svn.GetStatusAsync(_workingCopyText.Text.Trim())).ToList();
            _statusChangesAll = changes.OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            _checkedStatusPaths.Clear();
            foreach (var change in _statusChangesAll.Where(change => change.CanCommit))
            {
                _checkedStatusPaths.Add(NormalizeRelativePath(change.RelativePath));
            }

            ApplyStatusFilter();
            var conflicts = changes.Where(change => change.Status == SvnStatusKind.Conflicted).ToList();
            RefreshConflictPanel(conflicts);
            UpdateStatusBadges(changes.Count, conflicts.Count);
            if (conflicts.Count > 0)
            {
                WriteOutput(
                    $"发现 {changes.Count} 个本地改动，其中 {conflicts.Count} 个冲突。\r\n\r\n" +
                    "SVN 冲突会在目录里生成 .mine、.r旧版本、.r新版本 文件，这是正常现象。" +
                    "请先处理冲突，再提交。冲突文件已经在列表里标红。");
            }
            else
            {
                WriteOutput(changes.Count == 0 ? "没有本地改动。" : $"发现 {changes.Count} 个本地改动。");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void RefreshConflictPanel(IReadOnlyList<SvnChange> conflicts)
    {
        _currentConflicts = conflicts.OrderBy(change => change.RelativePath).ToList();
        _conflictSummaryLabel.Text = _currentConflicts.Count == 0
            ? "当前没有冲突文件。"
            : $"当前有 {_currentConflicts.Count} 个冲突文件。请逐个打开合并，确认保存后再标记解决。";
        _conflictSummaryLabel.ForeColor = _currentConflicts.Count == 0 ? Color.FromArgb(45, 100, 65) : Color.DarkRed;
        _conflictGrid.DataSource = _currentConflicts
            .Select(change => new ConflictGridRow(change.RelativePath, change.Description))
            .ToList();
    }

    private void ApplyStatusFilter()
    {
        var selectedPath = GetSelectedChange()?.RelativePath;
        var filtered = ChangedFilesFilter.ApplyStatusChanges(
            _statusChangesAll,
            _statusSearchText.Text,
            ChangedFilesFilter.GetMode(_statusFilterCombo));

        _updatingChangesList = true;
        _changesList.BeginUpdate();
        _changesList.Items.Clear();
        try
        {
            ListViewItem? itemToSelect = null;
            foreach (var change in filtered)
            {
                var item = CreateStatusListItem(change);
                _changesList.Items.Add(item);
                if (selectedPath != null &&
                    string.Equals(NormalizeRelativePath(selectedPath), NormalizeRelativePath(change.RelativePath), StringComparison.OrdinalIgnoreCase))
                {
                    itemToSelect = item;
                }
            }

            if (itemToSelect != null)
            {
                itemToSelect.Selected = true;
                itemToSelect.Focused = true;
                itemToSelect.EnsureVisible();
            }
        }
        finally
        {
            _changesList.EndUpdate();
            _updatingChangesList = false;
        }

        UpdateStatusFilterSummary(filtered.Count);
    }

    private List<string> GetCommitSelectedPaths()
    {
        if (_statusCommitVisibleOnlyCheck.Checked)
        {
            return _changesList.Items
                .Cast<ListViewItem>()
                .Where(item => item.Checked && item.Tag is SvnChange)
                .Select(item => ((SvnChange)item.Tag!).RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _checkedStatusPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ListViewItem CreateStatusListItem(SvnChange change)
    {
        var item = new ListViewItem(change.DisplayStatus)
        {
            Tag = change,
            Checked = change.CanCommit && _checkedStatusPaths.Contains(NormalizeRelativePath(change.RelativePath)),
        };
        item.SubItems.Add(change.RelativePath);
        item.SubItems.Add(change.Description);
        if (change.Status == SvnStatusKind.Conflicted)
        {
            item.ForeColor = Color.DarkRed;
            item.Checked = false;
        }

        return item;
    }

    private void TrackStatusItemChecked(ListViewItem item)
    {
        if (_updatingChangesList || item.Tag is not SvnChange change)
        {
            return;
        }

        var key = NormalizeRelativePath(change.RelativePath);
        if (!change.CanCommit)
        {
            if (item.Checked)
            {
                item.Checked = false;
            }

            _checkedStatusPaths.Remove(key);
            UpdateStatusFilterSummary(_changesList.Items.Count);
            return;
        }

        if (item.Checked)
        {
            _checkedStatusPaths.Add(key);
        }
        else
        {
            _checkedStatusPaths.Remove(key);
        }

        UpdateStatusFilterSummary(_changesList.Items.Count);
    }

    private void UpdateStatusFilterSummary(int visibleCount)
    {
        var scopeText = _statusCommitVisibleOnlyCheck.Checked ? "提交当前" : "提交全部";
        _statusFilterSummaryLabel.Text = $"显示 {visibleCount}/{_statusChangesAll.Count}，已勾选 {_checkedStatusPaths.Count}，{scopeText}";
    }

    private void UpdateStatusBadges(int changeCount, int conflictCount)
    {
        _statusPage.Text = changeCount > 0 ? $"File Status({changeCount})" : "File Status";
        _conflictPage.Text = conflictCount > 0 ? $"冲突({conflictCount})" : "冲突";
    }

    private void UpdateHistoryBadge(int logCount)
    {
        _historyPage.Text = logCount > 0 ? $"History({logCount})" : "History";
    }

    private async Task CheckToolUpdatesAsync(bool showUpToDateMessage)
    {
        if (_checkingToolUpdate)
        {
            return;
        }

        _checkingToolUpdate = true;
        try
        {
            _lastReleaseUpdateStatus = await ReleaseUpdateChecker.CheckLatestAsync(AppInfo.Version);
            if (_lastReleaseUpdateStatus.State == ReleaseUpdateState.UpdateAvailable)
            {
                _toolUpdateStatusLabel.Text = $"工具有新版本 {_lastReleaseUpdateStatus.LatestTag}";
                _toolUpdateStatusLabel.ForeColor = Color.DarkRed;
                if (showUpToDateMessage)
                {
                    WriteOutput($"检测到工具新版本：当前 {AppInfo.VersionText}，最新 {_lastReleaseUpdateStatus.LatestTag}。点击状态栏可打开更新面板。");
                }

                return;
            }

            if (_lastReleaseUpdateStatus.State == ReleaseUpdateState.UpToDate)
            {
                _toolUpdateStatusLabel.Text = $"工具最新 {AppInfo.VersionText}";
                _toolUpdateStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                if (showUpToDateMessage)
                {
                    WriteOutput($"工具已是最新发布版：{AppInfo.VersionText}");
                }

                return;
            }

            var repositoryRoot = GitUpdateChecker.FindRepositoryRoot(AppContext.BaseDirectory);
            _lastToolRepositoryRoot = repositoryRoot;
            if (repositoryRoot == null)
            {
                _lastToolUpdateStatus = null;
                _toolUpdateStatusLabel.Text = "工具：检查失败";
                _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                if (showUpToDateMessage)
                {
                    WriteOutput("无法检查 GitHub Release：" + _lastReleaseUpdateStatus.Message);
                }

                return;
            }

            var status = await GitUpdateChecker.CheckAsync(repositoryRoot);
            _lastToolUpdateStatus = status;
            switch (status.State)
            {
                case GitUpdateState.RemoteUnavailable:
                    _toolUpdateStatusLabel.Text = "工具：远端不可用";
                    _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                    if (showUpToDateMessage)
                    {
                        WriteOutput(status.Message);
                    }

                    break;
                case GitUpdateState.UpdateAvailable:
                    _toolUpdateStatusLabel.Text = $"工具有新版本 {status.RemoteShortSha}";
                    _toolUpdateStatusLabel.ForeColor = Color.DarkRed;
                    WriteOutput($"GitHub 工具有新版本：本地 {status.LocalShortSha}，远端 {status.RemoteShortSha}。可在仓库目录执行 git pull 更新。");
                    break;
                case GitUpdateState.UpToDate:
                    _toolUpdateStatusLabel.Text = $"工具最新 {status.LocalShortSha}";
                    _toolUpdateStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                    if (showUpToDateMessage)
                    {
                        WriteOutput($"工具已是 GitHub 最新版本：{status.LocalShortSha}");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _toolUpdateStatusLabel.Text = "工具：检查失败";
            _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
            if (showUpToDateMessage)
            {
                WriteOutput("工具更新检查失败：" + ex.Message);
            }
        }
        finally
        {
            _checkingToolUpdate = false;
        }
    }

    private async Task ShowToolUpdatePanelAsync()
    {
        await CheckToolUpdatesAsync(showUpToDateMessage: false);

        if (_lastReleaseUpdateStatus != null &&
            (_lastReleaseUpdateStatus.State != ReleaseUpdateState.Unavailable || string.IsNullOrWhiteSpace(_lastToolRepositoryRoot)))
        {
            using var releaseForm = ToolUpdateForm.FromRelease(_lastReleaseUpdateStatus);
            if (releaseForm.ShowDialog(this) != DialogResult.OK || !releaseForm.RunUpdateRequested)
            {
                return;
            }

            await InstallReleaseUpdateAsync(_lastReleaseUpdateStatus);
            return;
        }

        var repositoryRoot = _lastToolRepositoryRoot;
        var status = _lastToolUpdateStatus;
        var remoteUrl = "";
        var updateLog = "";
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            remoteUrl = await GitUpdateChecker.GetRemoteUrlAsync(repositoryRoot);
            updateLog = await GitUpdateChecker.GetUpdateLogAsync(repositoryRoot, 30);
        }

        using var form = ToolUpdateForm.FromGit(status, repositoryRoot, remoteUrl, updateLog);
        if (form.ShowDialog(this) != DialogResult.OK || !form.RunUpdateRequested || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        SetBusy(true, "正在执行工具更新...");
        try
        {
            OperationLogger.Log("ToolUpdateStart", repositoryRoot, remoteUrl);
            var result = await GitUpdateChecker.PullAsync(repositoryRoot);
            WriteOutput(result.CombinedOutput);
            OperationLogger.Log(result.ExitCode == 0 ? "ToolUpdateSuccess" : "ToolUpdateFailed", repositoryRoot, result.CombinedOutput);
            MessageBox.Show(
                result.ExitCode == 0
                    ? "工具更新命令已执行完成。建议关闭并重新打开程序，使用最新构建。"
                    : "工具更新命令执行失败，请查看下方输出。",
                "工具更新",
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }

        await CheckToolUpdatesAsync(showUpToDateMessage: false);
    }

    private async Task InstallReleaseUpdateAsync(ReleaseUpdateStatus status)
    {
        if (status.State != ReleaseUpdateState.UpdateAvailable || string.IsNullOrWhiteSpace(status.AssetDownloadUrl))
        {
            MessageBox.Show("当前没有可安装的新版本。", "工具更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"准备下载并安装 {status.LatestTag}。{Environment.NewLine}{Environment.NewLine}" +
            "程序会在下载完成后自动关闭、覆盖当前目录并重新启动。继续？",
            "自动更新工具",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, "正在下载工具更新...");
        try
        {
            var zipPath = await ReleaseUpdateChecker.DownloadAssetAsync(status.AssetDownloadUrl, status.LatestTag);
            OperationLogger.Log("ToolReleaseDownloadSuccess", AppContext.BaseDirectory, zipPath);
            StartSelfUpdater(zipPath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private static void StartSelfUpdater(string zipPath)
    {
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updateRoot = Path.Combine(Path.GetTempPath(), "DreamSVNManagerUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
        var extractDirectory = Path.Combine(updateRoot, "extract");
        var exePath = Path.Combine(targetDirectory, "SVNManager.exe");
        var script = $@"
$ErrorActionPreference = 'Stop'
$processId = {Environment.ProcessId}
$zipPath = {PowerShellQuote(zipPath)}
$targetDirectory = {PowerShellQuote(targetDirectory)}
$extractDirectory = {PowerShellQuote(extractDirectory)}
$exePath = {PowerShellQuote(exePath)}
try {{
    Wait-Process -Id $processId -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Test-Path -LiteralPath $extractDirectory) {{
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }}
    New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory -Force
    Copy-Item -Path (Join-Path $extractDirectory '*') -Destination $targetDirectory -Recurse -Force
    Start-Process -FilePath $exePath
}} catch {{
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '工具更新失败')
}}
";
        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
            },
        });
    }

    private static string PowerShellQuote(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private WorkingCopyInfo RefreshWorkingCopyRevisionStatus(bool showFailure = false)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未选择", SystemColors.ControlText, "未选择工作副本。");
            return WorkingCopyInfo.Empty;
        }

        if (!Directory.Exists(workingCopy))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：目录不存在", Color.FromArgb(166, 103, 34), workingCopy);
            return WorkingCopyInfo.Empty;
        }

        if (!Directory.Exists(Path.Combine(workingCopy, ".svn")))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：非 SVN", Color.FromArgb(166, 103, 34), workingCopy);
            return WorkingCopyInfo.Empty;
        }

        try
        {
            var info = _svn.GetWorkingCopyInfo(workingCopy);
            if (info == WorkingCopyInfo.Empty)
            {
                SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未知", Color.FromArgb(166, 103, 34), "svn info 没有返回可用版本信息。");
                return WorkingCopyInfo.Empty;
            }

            var text = info.IsMixedRevision
                ? $"本地 {info.DisplayRevisionText}"
                : $"本地 r{info.CurrentContentRevision}";
            var color = info.IsMixedRevision
                ? Color.FromArgb(166, 103, 34)
                : Color.FromArgb(45, 100, 65);
            var detail = $"工作副本版本：{info.DisplayRevisionText}{Environment.NewLine}当前内容版本：r{info.CurrentContentRevision}{Environment.NewLine}{info.Url}";
            SetWorkingCopyRevisionStatus(info, text, color, detail);
            if (showFailure)
            {
                WriteOutput(detail.Replace(Environment.NewLine, "  ", StringComparison.Ordinal));
            }

            return info;
        }
        catch (Exception ex)
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：读取失败", Color.FromArgb(166, 103, 34), ex.Message);
            if (showFailure)
            {
                WriteOutput("本地工作副本版本读取失败：" + ex.Message);
            }

            return WorkingCopyInfo.Empty;
        }
    }

    private void SetWorkingCopyRevisionStatus(WorkingCopyInfo info, string text, Color color, string toolTip)
    {
        _currentWorkingCopyInfo = info;
        _localRevisionStatusLabel.Text = text;
        _localRevisionStatusLabel.ForeColor = color;
        _localRevisionStatusLabel.ToolTipText = toolTip;
    }

    private async Task CheckRemoteChangesAsync(bool showUpToDateMessage)
    {
        if (_checkingRemote || !ValidateWorkingCopyPathForBackground())
        {
            return;
        }

        _checkingRemote = true;
        try
        {
            var workingCopy = _workingCopyText.Text.Trim();
            var info = RefreshWorkingCopyRevisionStatus();
            var latest = await _svn.GetLatestRepositoryLogAsync(workingCopy);
            if (latest == null)
            {
                _remoteStatusLabel.Text = "远端：无历史";
                _remoteStatusLabel.ForeColor = SystemColors.ControlText;
                return;
            }

            var localRevision = info.CurrentContentRevision;
            if (localRevision <= 0)
            {
                _remoteStatusLabel.Text = $"远端 r{latest.Revision}，本地未知";
                _remoteStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                if (showUpToDateMessage)
                {
                    WriteOutput($"已读取远端最新 r{latest.Revision}，但本地工作副本版本未知，暂时无法判断是否落后。");
                }

                _latestRemoteLog = latest;
                return;
            }

            var hasRemoteUpdates = localRevision > 0 && latest.Revision > localRevision;
            if (hasRemoteUpdates)
            {
                _remoteStatusLabel.Text = $"远端有新提交 r{latest.Revision}";
                _remoteStatusLabel.ForeColor = Color.DarkRed;
                if (_latestRemoteLog == null || latest.Revision > _latestRemoteLog.Revision)
                {
                    WriteOutput($"远端有新提交：r{latest.Revision}  {latest.Author}  {latest.ShortMessage}");
                }
            }
            else
            {
                _remoteStatusLabel.Text = $"远端最新 r{latest.Revision}";
                _remoteStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                if (showUpToDateMessage)
                {
                    WriteOutput($"当前没有检测到需要拉取的新提交。远端最新：r{latest.Revision}");
                }
            }

            _latestRemoteLog = latest;
        }
        catch (Exception ex)
        {
            _remoteStatusLabel.Text = "远端：检查失败";
            _remoteStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
            if (showUpToDateMessage)
            {
                WriteOutput("远端检查失败：" + ex.Message);
            }
        }
        finally
        {
            _checkingRemote = false;
        }
    }

    private bool ValidateWorkingCopyPathForBackground()
    {
        var path = _workingCopyText.Text.Trim();
        return Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".svn"));
    }

    private async Task HandleConflictGridClickAsync(DataGridViewCellEventArgs args)
    {
        if (args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            _conflictGrid.Rows[args.RowIndex].DataBoundItem is not ConflictGridRow row)
        {
            return;
        }

            var columnName = _conflictGrid.Columns[args.ColumnIndex].Name;
        switch (columnName)
        {
            case "InternalMerge":
                await RunInternalSpreadsheetMergeAsync(row.RelativePath);
                break;
            case "OpenMerge":
                OpenConflictMerge(row.RelativePath);
                break;
            case "OpenFolder":
                OpenConflictFolderByPath(row.RelativePath);
                break;
            case "Resolve":
                await ResolveConflictPathAsync(row.RelativePath);
                break;
        }
    }

    private async Task RunDiffAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = GetComparableExtension(relativePath);
        var selectedChange = GetSelectedChange();
        if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
        {
            MessageBox.Show("这是新增文件，没有 SVN 基准版本可对比。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
        {
            MessageBox.Show("本地文件不存在，暂不支持查看 Excel 差异。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在生成差异...");
        var tempBaseFile = Path.Combine(Path.GetTempPath(), $"SVNManager_BASE_{Guid.NewGuid():N}{extension}");
        try
        {
            var workingCopy = _workingCopyText.Text.Trim();
            var localFile = Path.Combine(workingCopy, relativePath);
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict?.ServerPath != null)
            {
                ShowDiffWindow($"我的版本 -> 服务器版本：{relativePath}", conflict.MinePath, conflict.ServerPath);
                return;
            }

            await _svn.WriteBaseFileAsync(workingCopy, relativePath, tempBaseFile);
            ShowDiffWindow($"BASE -> 本地：{relativePath}", tempBaseFile, localFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task CompareSelectedFileWithRemoteHeadAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个本地文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await CompareLocalFileWithRemoteHeadAsync(relativePath);
    }

    private async Task CompareSelectedHistoryFileWithRemoteHeadAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        await CompareLocalFileWithRemoteHeadAsync(GetHistoryChangedWorkingCopyRelativePath(file));
    }

    private async Task CompareLocalFileWithRemoteHeadAsync(string relativePath)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        var localFile = Path.Combine(workingCopy, relativePath);
        if (!File.Exists(localFile))
        {
            MessageBox.Show("本地文件不存在，无法和远端 HEAD 对比。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var tempHeadFile = Path.Combine(Path.GetTempPath(), $"SVNManager_HEAD_{Guid.NewGuid():N}{GetComparableExtension(relativePath)}");
        SetBusy(true, "正在读取远端 HEAD 文件...");
        try
        {
            await _svn.WriteHeadFileAsync(workingCopy, relativePath, tempHeadFile);
            ShowDiffWindow($"当前本地 -> 远端 HEAD：{relativePath}", localFile, tempHeadFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempHeadFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task CompareSelectedTableWithAnotherAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个表格文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var firstFile = Path.Combine(_workingCopyText.Text.Trim(), relativePath);
        if (!File.Exists(firstFile))
        {
            MessageBox.Show("选中的本地文件不存在，无法快速比对。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(firstFile))
        {
            MessageBox.Show("快速表格比对只支持 .xls / .xlsx / .xlsm / SpreadsheetML XML。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var secondFile = PromptSpreadsheetFile("选择要比对的另一个表", firstFile);
        if (string.IsNullOrWhiteSpace(secondFile))
        {
            return;
        }

        ShowDiffWindow($"快速表格比对：{Path.GetFileName(firstFile)} -> {Path.GetFileName(secondFile)}", firstFile, secondFile);
        await Task.CompletedTask;
    }

    private async Task CompareSelectedHistoryFileWithAnotherTableAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var otherFile = PromptSpreadsheetFile("选择要比对的另一个表", GetHistoryChangedLocalPath(file));
        if (string.IsNullOrWhiteSpace(otherFile))
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var tempVersionFile = "";
        SetBusy(true, "正在准备历史表格版本...");
        try
        {
            string firstFile;
            string firstLabel;
            if (_selectedHistoryLog is { IsUncommitted: false, Revision: > 0 } log)
            {
                if (string.IsNullOrWhiteSpace(file.RepositoryPath))
                {
                    MessageBox.Show("这条历史记录没有仓库路径，无法读取提交版本。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var revision = file.Action == "D" ? log.Revision - 1 : log.Revision;
                if (revision <= 0)
                {
                    MessageBox.Show("无法确定这个文件可比对的历史版本。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                tempVersionFile = CreateHistoryOpenTempPath($"r{revision}", file.TreePath);
                await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, tempVersionFile);
                firstFile = tempVersionFile;
                firstLabel = $"r{revision} {file.TreePath}";
            }
            else
            {
                firstFile = GetHistoryChangedLocalPath(file);
                firstLabel = $"当前本地 {file.TreePath}";
            }

            if (!File.Exists(firstFile) || !SpreadsheetThreeWayMergeService.IsSupportedPath(firstFile))
            {
                MessageBox.Show("选中的历史文件不是可读取的表格文件。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ShowDiffWindow($"快速表格比对：{firstLabel} -> {Path.GetFileName(otherFile)}", firstFile, otherFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempVersionFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task RunInternalSpreadsheetMergeAsync(string? forcedRelativePath = null)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = string.IsNullOrWhiteSpace(forcedRelativePath)
            ? GetSelectedRelativePath()
            : SvnConflictArtifact.NormalizeToBasePath(forcedRelativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个 XML / Excel 表格文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var localFile = Path.Combine(workingCopy, relativePath);
        if (!File.Exists(localFile))
        {
            MessageBox.Show("本地文件不存在，无法执行内置三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(localFile))
        {
            MessageBox.Show("内置三方合并当前支持 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selectedChange = GetSelectedChange();
        if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
        {
            MessageBox.Show("这是新增文件，没有 SVN BASE 版本可做三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
        {
            MessageBox.Show("本地文件不存在或已删除，无法执行表格合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = GetComparableExtension(relativePath);
        var tempBaseFile = Path.Combine(Path.GetTempPath(), $"SVNManager_MERGE_BASE_{Guid.NewGuid():N}{extension}");
        var tempRemoteFile = Path.Combine(Path.GetTempPath(), $"SVNManager_MERGE_HEAD_{Guid.NewGuid():N}{extension}");
        var wasConflict = selectedChange?.Status == SvnStatusKind.Conflicted || ConflictFileSet.Find(workingCopy, relativePath) != null;

        SetBusy(true, "正在准备内置表格三方合并...");
        try
        {
            await _svn.WriteBaseFileAsync(workingCopy, relativePath, tempBaseFile);
            await _svn.WriteHeadFileAsync(workingCopy, relativePath, tempRemoteFile);
            var plan = await Task.Run(() => SpreadsheetThreeWayMergeService.BuildPlan(tempBaseFile, localFile, tempRemoteFile));
            if (plan.RelevantChangeCount == 0)
            {
                MessageBox.Show("BASE、本地和远端 HEAD 没有需要合并的表格差异。", "无需合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(false, "等待确认合并项目");
            using (var form = new SpreadsheetMergeConflictForm(
                relativePath,
                plan,
                "内置表格三方合并 - 合并项目预览",
                "本地",
                "远端 HEAD",
                "写入工作副本"))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消内置三方合并：{relativePath}");
                    return;
                }
            }

            SetBusy(true, "正在写入合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show("当前选择全部保留本地，没有需要写入工作副本的远端表格改动。", "无需写入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(localFile);
            await Task.Run(() => SpreadsheetThreeWayMergeService.ApplyWrites(localFile, writes));
            OperationLogger.Log("InternalSpreadsheetMergeSuccess", workingCopy, $"{relativePath}; writes={writes.Count}; backup={backupPath}");
            WriteOutput($"内置三方合并已写入 {writes.Count} 个单元格：{relativePath}\r\n备份：{backupPath}");
            await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
            await RefreshStatusAsync();
            LoadAllFiles();
            await LoadRepositoryHistoryAsync();
        }
        catch (Exception ex)
        {
            OperationLogger.Log("InternalSpreadsheetMergeFailed", workingCopy, $"{relativePath}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            TryDelete(tempRemoteFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task RunCrossRepositorySpreadsheetMergeAsync()
    {
        var defaultTargetFile = "";
        if (ValidateWorkingCopyPathForBackground())
        {
            var relativePath = GetSelectedRelativePath();
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                var candidate = Path.Combine(_workingCopyText.Text.Trim(), relativePath);
                if (File.Exists(candidate))
                {
                    defaultTargetFile = candidate;
                }
            }
        }

        using var picker = new CrossRepositorySpreadsheetMergeForm(defaultTargetFile);
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var baseFile = picker.BaseFilePath;
        var changedFile = picker.ChangedFilePath;
        var targetFile = picker.TargetFilePath;
        var displayName = Path.GetFileName(targetFile);

        SetBusy(true, "正在计算跨库表格三方合并...");
        try
        {
            var plan = await Task.Run(() => SpreadsheetThreeWayMergeService.BuildPlan(baseFile, targetFile, changedFile));
            if (plan.RelevantChangeCount == 0)
            {
                MessageBox.Show(
                    "A、B、C 三个文件没有需要合并的表格差异。",
                    "无需合并",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(false, "等待确认跨库合并项目");
            using (var conflictForm = new SpreadsheetMergeConflictForm(
                displayName,
                plan,
                "跨库表格三方合并 - 合并项目预览",
                "目标 C",
                "B 改动后",
                "写入目标 C"))
            {
                if (conflictForm.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消跨库表格三方合并：{targetFile}");
                    return;
                }
            }

            SetBusy(true, "正在写入跨库合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show(
                    "当前选择全部保留目标 C，没有需要写入的 A->B 改动。",
                    "无需写入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(targetFile);
            await Task.Run(() => SpreadsheetThreeWayMergeService.ApplyWrites(targetFile, writes));
            OperationLogger.Log("CrossRepositorySpreadsheetMergeSuccess", _workingCopyText.Text.Trim(), $"{targetFile}; writes={writes.Count}; backup={backupPath}");
            WriteOutput(
                $"跨库表格三方合并已写入 {writes.Count} 个单元格到目标 C：{targetFile}\r\n" +
                $"A 改动前：{baseFile}\r\n" +
                $"B 改动后：{changedFile}\r\n" +
                $"备份：{backupPath}");

            if (IsCurrentWorkingCopyFile(targetFile))
            {
                await RefreshStatusAsync();
                LoadAllFiles();
            }
        }
        catch (Exception ex)
        {
            OperationLogger.Log("CrossRepositorySpreadsheetMergeFailed", _workingCopyText.Text.Trim(), $"{targetFile}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunSelectedCommitSpreadsheetMergeAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var committedLogs = GetSelectedCommittedHistoryLogs();
        if (committedLogs.Count == 0)
        {
            return;
        }

        var firstRevision = committedLogs.First().Revision;
        var lastRevision = committedLogs.Last().Revision;
        var scopeLabel = firstRevision == lastRevision
            ? $"r{lastRevision}"
            : $"r{firstRevision}-r{lastRevision}";

        if (file.Action is "A" or "D")
        {
            MessageBox.Show(
                "新增或删除文件没有完整的“范围开始前 -> 范围结束后”两侧表格版本，暂不支持用这段提交做三方合并。",
                "无法三方合并",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            MessageBox.Show("这条历史记录没有仓库路径，无法读取提交前后的文件。", "无法三方合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var defaultTargetFile = GetHistoryChangedLocalPath(file);
        var targetFile = PromptSpreadsheetFile("选择要写入的目标表 C", defaultTargetFile);
        if (string.IsNullOrWhiteSpace(targetFile))
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var tempBaseFile = CreateHistoryOpenTempPath($"r{firstRevision - 1}_before", file.TreePath);
        var tempChangedFile = CreateHistoryOpenTempPath($"r{lastRevision}_after", file.TreePath);
        SetBusy(true, "正在准备所选提交范围的三方合并...");
        try
        {
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, firstRevision - 1, tempBaseFile);
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, lastRevision, tempChangedFile);

            if (!SpreadsheetThreeWayMergeService.IsSupportedPath(tempBaseFile) ||
                !SpreadsheetThreeWayMergeService.IsSupportedPath(tempChangedFile) ||
                !SpreadsheetThreeWayMergeService.IsSupportedPath(targetFile))
            {
                MessageBox.Show(
                    "所选提交/范围三方合并只支持 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格。",
                    "文件类型不适合",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var plan = await Task.Run(() => SpreadsheetThreeWayMergeService.BuildPlan(tempBaseFile, targetFile, tempChangedFile));
            if (plan.RelevantChangeCount == 0)
            {
                MessageBox.Show(
                    $"{scopeLabel} 对这个表没有可合并的单元格差异。",
                    "无需合并",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(false, "等待确认所选提交范围合并项目");
            using (var conflictForm = new SpreadsheetMergeConflictForm(
                file.TreePath,
                plan,
                $"用 {scopeLabel} 提交范围三方合并 - 合并项目预览",
                "目标 C",
                scopeLabel,
                "写入目标 C"))
            {
                if (conflictForm.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消用 {scopeLabel} 三方合并：{file.TreePath}");
                    return;
                }
            }

            SetBusy(true, "正在写入所选提交范围三方合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show(
                    "当前选择全部保留目标 C，没有需要写入的提交范围改动。",
                    "无需写入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(targetFile);
            await Task.Run(() => SpreadsheetThreeWayMergeService.ApplyWrites(targetFile, writes));
            OperationLogger.Log("CommitSpreadsheetMergeSuccess", workingCopy, $"range={firstRevision}-{lastRevision}; file={file.TreePath}; target={targetFile}; writes={writes.Count}; backup={backupPath}");
            WriteOutput(
                $"已把 {scopeLabel} 对表格的累计改动三方合并到目标 C，写入 {writes.Count} 个单元格。\r\n" +
                $"历史文件：{file.TreePath}\r\n" +
                $"目标 C：{targetFile}\r\n" +
                $"备份：{backupPath}");

            if (IsCurrentWorkingCopyFile(targetFile))
            {
                await RefreshStatusAsync();
                LoadAllFiles();
            }
        }
        catch (Exception ex)
        {
            OperationLogger.Log("CommitSpreadsheetMergeFailed", workingCopy, $"range={firstRevision}-{lastRevision}; file={file.TreePath}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            TryDelete(tempChangedFile);
            SetBusy(false, "就绪");
        }
    }

    private bool ConfirmCrossRepositorySpreadsheetMergeWrite(string baseFile, string changedFile, string targetFile, SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把 A->B 的表格改动写入目标 C。{Environment.NewLine}{Environment.NewLine}" +
            $"A 改动前：{baseFile}{Environment.NewLine}" +
            $"B 改动后：{changedFile}{Environment.NewLine}" +
            $"C 目标文件：{targetFile}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用 A->B 改动：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留目标 C 独有改动：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份目标 C。继续？";
        return MessageBox.Show(
            message,
            "跨库表格三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private bool ConfirmCommitSpreadsheetMergeWrite(
        ChangedFileEntry file,
        long firstRevision,
        long lastRevision,
        string scopeLabel,
        string targetFile,
        SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把 {scopeLabel} 对这个表的累计改动写入目标 C。{Environment.NewLine}{Environment.NewLine}" +
            $"历史文件：{file.TreePath}{Environment.NewLine}" +
            $"改动范围：r{firstRevision - 1} -> r{lastRevision}{Environment.NewLine}" +
            $"目标 C：{targetFile}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用提交范围改动：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留目标 C 独有改动：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份目标 C。继续？";
        return MessageBox.Show(
            message,
            "用所选提交/范围三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private IReadOnlyList<SvnLogEntry> GetSelectedCommittedHistoryLogs()
    {
        var logs = _selectedHistoryLogs.Count > 0
            ? _selectedHistoryLogs
            : _selectedHistoryLog != null ? [_selectedHistoryLog] : [];
        return logs
            .Where(log => !log.IsUncommitted && log.Revision > 0)
            .OrderBy(log => log.Revision)
            .ToList();
    }

    private string? PromptSpreadsheetFile(string title, string initialFile = "")
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "表格文件 (*.xml;*.xls;*.xlsx;*.xlsm)|*.xml;*.xls;*.xlsx;*.xlsm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialFile))
        {
            var initialDirectory = File.Exists(initialFile)
                ? Path.GetDirectoryName(initialFile)
                : Directory.Exists(initialFile) ? initialFile : Path.GetDirectoryName(initialFile);
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            if (File.Exists(initialFile))
            {
                dialog.FileName = Path.GetFileName(initialFile);
            }
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(dialog.FileName))
        {
            MessageBox.Show(
                "请选择 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格文件。",
                "文件类型不适合",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        return dialog.FileName;
    }

    private bool IsCurrentWorkingCopyFile(string filePath)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        if (string.IsNullOrWhiteSpace(workingCopy) || !Directory.Exists(workingCopy))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workingCopy)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(filePath);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool ConfirmSpreadsheetMergeWrite(string relativePath, SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把远端 HEAD 的非冲突表格改动写入工作副本：{relativePath}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用远端：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留本地：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份当前本地文件。继续？";
        return MessageBox.Show(
            message,
            "内置表格三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private async Task OfferResolveAfterInternalMergeAsync(string relativePath, bool wasConflict)
    {
        if (!wasConflict)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"表格合并结果已经保存到工作副本。是否现在执行 svn resolve --accept working？{Environment.NewLine}{Environment.NewLine}{relativePath}",
            "标记冲突已解决",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm == DialogResult.OK)
        {
            await ResolveConflictPathCoreAsync(relativePath);
        }
    }

    private async Task RunConflictViewerAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取冲突版本...");
        await Task.Yield();
        try
        {
            var workingCopy = _workingCopyText.Text.Trim();
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict == null)
            {
                MessageBox.Show("没有找到 .mine / .r版本号 冲突辅助文件。", "不是冲突文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var form = new ConflictViewerForm(conflict, conflictFile => LaunchExternalConflictCompare(conflictFile));
            form.ShowDialog(this);
            WriteOutput($"已打开冲突查看器：{conflict.RelativePath}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunConflictWorkflowAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备冲突处理...");
        try
        {
            var conflict = FindConflictOrWarn(relativePath);
            if (conflict == null || !LaunchExternalConflictCompare(conflict))
            {
                return;
            }

            OpenConflictFileFolder(conflict);
            SetBusy(false, "等待手动合并完成");
            var confirm = MessageBox.Show(
                "已经打开分久必合和当前文件目录。\r\n\r\n请在外部工具中完成合并，并把最终结果保存到当前冲突文件后，再点击“确定”。\r\n\r\n确定后会执行 svn resolve，并刷新状态。",
                "确认合并已保存",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.OK)
            {
                WriteOutput($"已打开冲突处理工具，尚未标记已解决：{conflict.RelativePath}");
                return;
            }

            SetBusy(true, "正在标记冲突已解决...");
            await ResolveConflictPathCoreAsync(conflict.RelativePath);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void OpenConflictMerge(string relativePath)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        SetBusy(true, "正在打开合并工具...");
        try
        {
            var conflict = FindConflictOrWarn(relativePath);
            if (conflict != null && LaunchExternalConflictCompare(conflict))
            {
                WriteOutput($"已打开合并工具：{conflict.RelativePath}");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void OpenConflictFolderByPath(string relativePath)
    {
        var conflict = FindConflictOrWarn(relativePath);
        if (conflict != null)
        {
            OpenConflictFileFolder(conflict);
            WriteOutput($"已打开冲突文件目录：{conflict.RelativePath}");
        }
    }

    private async Task ResolveConflictPathAsync(string relativePath)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var conflict = FindConflictOrWarn(relativePath);
        if (conflict == null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"确认已经把最终合并结果保存到当前文件，并标记冲突已解决？\r\n\r\n{conflict.RelativePath}",
            "标记冲突已解决",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, "正在标记冲突已解决...");
        try
        {
            await ResolveConflictPathCoreAsync(conflict.RelativePath);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task ResolveConflictPathCoreAsync(string relativePath)
    {
        var result = await _svn.ResolveAsync(_workingCopyText.Text.Trim(), relativePath);
        OperationLogger.Log(result.ExitCode == 0 ? "ResolveConflictSuccess" : "ResolveConflictFailed", _workingCopyText.Text.Trim(), relativePath);
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private ConflictFileSet? FindConflictOrWarn(string relativePath)
    {
        var conflict = ConflictFileSet.Find(_workingCopyText.Text.Trim(), relativePath);
        if (conflict == null)
        {
            MessageBox.Show("没有找到 .mine / .r版本号 冲突辅助文件。请确认选中的是 SVN 冲突文件。", "不是冲突文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return conflict;
    }

    private async Task RunExternalCompareOrMergeAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个 XML 表格文件或冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备外部对比...");
        try
        {
            var workingCopy = _workingCopyText.Text.Trim();
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict != null)
            {
                LaunchExternalConflictCompare(conflict);
                return;
            }

            if (!IsExternalTableToolSupported(relativePath))
            {
                MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。Lua 等文本文件请先使用内置差异查看。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedChange = GetSelectedChange();
            if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
            {
                MessageBox.Show("这是新增文件，没有 SVN 基准版本可对比。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
            {
                MessageBox.Show("本地文件不存在，无法交给外部工具。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var localFile = Path.Combine(workingCopy, relativePath);
            if (!File.Exists(localFile))
            {
                MessageBox.Show("本地没有找到这个文件。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var baseFile = CreateExternalTempPath("BASE", relativePath);
            await _svn.WriteBaseFileAsync(workingCopy, relativePath, baseFile);
            if (LaunchExternalMergeTool(baseFile, localFile))
            {
                WriteOutput($"已打开分久必合：BASE -> 本地：{relativePath}");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunSelectedHistoryChangedFileExternalCompareAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            (_selectedHistoryLog == null && _selectedHistoryLogs.Count <= 1))
        {
            return;
        }

        if (!IsExternalTableToolSupported(file.TreePath))
        {
            MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。Lua 等文本文件请先使用内置差异预览。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备外部对比...");
        try
        {
            if (_selectedHistoryLog?.IsUncommitted == true && file.Action == "C")
            {
                var conflict = ConflictFileSet.Find(_workingCopyText.Text.Trim(), file.RelativePath);
                if (conflict != null)
                {
                    LaunchExternalConflictCompare(conflict);
                    return;
                }
            }

            var oldTemp = CreateExternalTempPath("OLD", file.TreePath);
            var newTemp = CreateExternalTempPath("NEW", file.TreePath);
            var workingCopy = _workingCopyText.Text.Trim();
            if (_selectedHistoryLogs.Count > 1)
            {
                var committedLogs = _selectedHistoryLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
                if (committedLogs.Count == 0)
                {
                    MessageBox.Show("多选范围不支持只选择未提交改动。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                await PrepareRangeDiffFilesAsync(_svn, workingCopy, committedLogs.First().Revision, committedLogs.Last().Revision, file, oldTemp, newTemp);
            }
            else if (_selectedHistoryLog?.IsUncommitted == true)
            {
                await PrepareUncommittedDiffFilesAsync(workingCopy, file, oldTemp, newTemp);
            }
            else if (_selectedHistoryLog != null)
            {
                await PrepareCommittedDiffFilesAsync(_svn, workingCopy, _selectedHistoryLog.Revision, file, oldTemp, newTemp);
            }

            if (LaunchExternalMergeTool(oldTemp, newTemp))
            {
                WriteOutput($"已打开分久必合：{file.DisplayText}");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private bool LaunchExternalConflictCompare(ConflictFileSet conflict)
    {
        if (conflict.ServerPath == null)
        {
            MessageBox.Show("没有找到服务器版本文件，无法外部对比。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!IsExternalTableToolSupported(conflict.RelativePath))
        {
            MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var mineFile = CopyToExternalTempFile(conflict.MinePath, "MINE", conflict.RelativePath);
        var serverFile = CopyToExternalTempFile(conflict.ServerPath, "SERVER", conflict.RelativePath);
        if (LaunchExternalMergeTool(mineFile, serverFile))
        {
            WriteOutput($"已打开分久必合：我的版本 -> 服务器版本：{conflict.RelativePath}");
            return true;
        }

        return false;
    }

    private static void OpenConflictFileFolder(ConflictFileSet conflict)
    {
        var argument = File.Exists(conflict.CurrentPath)
            ? $"/select,\"{conflict.CurrentPath}\""
            : Path.GetDirectoryName(conflict.CurrentPath);
        if (!string.IsNullOrWhiteSpace(argument))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
    }

    private bool LaunchExternalMergeTool(params string[] filePaths)
    {
        var toolPath = ResolveExternalMergeToolPath();
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            MessageBox.Show("没有配置分久必合.exe。请在“更多操作 -> 设置”里选择本机的分久必合.exe。", "外部工具未配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var startInfo = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
        };
        foreach (var filePath in filePaths.Where(File.Exists))
        {
            startInfo.ArgumentList.Add(filePath);
        }

        Process.Start(startInfo);
        OperationLogger.Log("OpenExternalMergeTool", _workingCopyText.Text.Trim(), string.Join(" | ", filePaths));
        return true;
    }

    private string? ResolveExternalMergeToolPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath) && File.Exists(_settings.ExternalMergeToolPath))
        {
            return _settings.ExternalMergeToolPath;
        }

        if (!string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath) && !File.Exists(_settings.ExternalMergeToolPath))
        {
            MessageBox.Show(
                $"配置的分久必合路径不存在：{Environment.NewLine}{_settings.ExternalMergeToolPath}{Environment.NewLine}{Environment.NewLine}请在“更多操作 -> 设置”里重新选择。",
                "外部工具路径失效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        return null;
    }

    private static bool IsExternalTableToolSupported(string path)
    {
        var extension = GetComparableExtension(path);
        return extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private static string CopyToExternalTempFile(string sourcePath, string label, string relativePath)
    {
        var targetPath = CreateExternalTempPath(label, relativePath);
        File.Copy(sourcePath, targetPath, true);
        return targetPath;
    }

    private static string CreateExternalTempPath(string label, string relativePath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SVNManager", "ExternalCompare", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(directory);
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(relativePath);
        var extension = GetComparableExtension(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(comparablePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        return Path.Combine(directory, $"{SanitizeFileName(label)}_{SanitizeFileName(fileName)}{extension}");
    }

    private static string CreateHistoryOpenTempPath(string label, string relativePath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SVNManager", "HistoryOpen", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(directory);
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(relativePath);
        var extension = GetComparableExtension(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(comparablePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        return Path.Combine(directory, $"{SanitizeFileName(fileName)}_{SanitizeFileName(label)}{extension}");
    }

    private static string FormatPathListLabel(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return "";
        }

        if (relativePaths.Count == 1)
        {
            return relativePaths[0];
        }

        return $"{relativePaths.Count} 个路径：" + string.Join("、", relativePaths.Take(3)) + (relativePaths.Count > 3 ? "..." : "");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static string GetComparableExtension(string path)
    {
        return Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(path));
    }

    private static bool IsUnsafeCommitPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return SvnConflictArtifact.IsAuxiliaryPath(path) ||
            fileName.EndsWith("~", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".temp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".orig", StringComparison.OrdinalIgnoreCase);
    }

    private static string CommitBlockReason(SvnChange change)
    {
        if (IsUnsafeCommitPath(change.RelativePath))
        {
            return "SVN 冲突辅助文件、临时文件或备份文件，禁止提交";
        }

        return change.Status switch
        {
            SvnStatusKind.Conflicted => "文件仍有冲突，请先打开合并并标记解决",
            SvnStatusKind.Missing => "文件在本地缺失，暂不自动提交删除",
            _ => "",
        };
    }

    private async Task<CommitPreflightResult> BuildCommitPreflightAsync(string workingCopy, IReadOnlyList<SvnChange> selectedChanges, IReadOnlyList<SvnChange> conflicts)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (conflicts.Count > 0)
        {
            blockers.Add($"工作副本仍有 {conflicts.Count} 个冲突，必须先处理冲突。");
        }

        var unsafePaths = selectedChanges.Where(change => IsUnsafeCommitPath(change.RelativePath)).Select(change => change.RelativePath).ToList();
        if (unsafePaths.Count > 0)
        {
            blockers.Add($"选择了 {unsafePaths.Count} 个冲突辅助/临时/备份文件，禁止提交：" + FormatPathPreview(unsafePaths));
        }

        var missing = selectedChanges.Where(change => change.Status == SvnStatusKind.Missing).Select(change => change.RelativePath).ToList();
        if (missing.Count > 0)
        {
            blockers.Add($"选择了 {missing.Count} 个本地缺失文件，当前不自动提交删除：" + FormatPathPreview(missing));
        }

        var unversioned = selectedChanges.Where(change => change.Status == SvnStatusKind.Unversioned).Select(change => change.RelativePath).ToList();
        if (unversioned.Count > 0)
        {
            warnings.Add($"将自动 svn add {unversioned.Count} 个未加入版本控制的文件：" + FormatPathPreview(unversioned));
        }

        var info = WorkingCopyInfo.Empty;
        try
        {
            info = _svn.GetWorkingCopyInfo(workingCopy);
            if (info.MinRevision > 0 && info.MaxRevision > 0 && info.MinRevision != info.MaxRevision)
            {
                warnings.Add($"工作副本是混合版本：r{info.MinRevision}:r{info.MaxRevision}。提交前请确认这正是你想要的状态。");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("无法读取工作副本版本信息：" + ex.Message);
        }

        try
        {
            var latest = await _svn.GetLatestRepositoryLogAsync(workingCopy);
            if (latest is { Revision: > 0 } && info.MaxRevision > 0 && latest.Revision > info.MaxRevision)
            {
                warnings.Add($"远端最新是 r{latest.Revision}，当前本地最高内容版本是 r{info.MaxRevision}，本地落后 {latest.Revision - info.MaxRevision} 个版本。");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("无法检查远端最新版本：" + ex.Message);
        }

        return new CommitPreflightResult(blockers, warnings);
    }

    private bool ConfirmCommitPreflight(CommitPreflightResult result)
    {
        if (result.Blocked)
        {
            MessageBox.Show(
                result.FormatMessage("提交前检查未通过"),
                "提交被拦截",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (!result.HasWarnings)
        {
            return true;
        }

        return MessageBox.Show(
            result.FormatMessage("提交前检查发现风险"),
            "提交前检查",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private static string FormatPathPreview(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "";
        }

        var preview = string.Join("、", paths.Take(5));
        return paths.Count > 5 ? preview + $" 等 {paths.Count} 个" : preview;
    }

    private void ShowDiffWindow(string title, string oldFilePath, string newFilePath)
    {
        var data = CreateDiffPreviewData(oldFilePath, newFilePath);
        if (data.ExcelDifferences != null)
        {
            using var form = new ExcelDiffForm(title, data);
            form.ShowDialog(this);
            WriteOutput($"{data.Summary}：{title}");
            return;
        }

        using var textForm = new TextDiffForm(title, data);
        textForm.ShowDialog(this);
        WriteOutput($"{data.Summary}：{title}");
    }

    private async Task RunFileHistoryAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePaths = GetSelectedFileTreeHistoryPaths();
        if (relativePaths.Count == 0)
        {
            MessageBox.Show("请先选中或勾选一个文件/文件夹，再查看历史。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取文件历史...");
        try
        {
            await ShowFileHistoryWindowAsync(relativePaths);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task ShowFileHistoryWindowAsync(string relativePath)
    {
        await ShowFileHistoryWindowAsync([relativePath]);
    }

    private async Task ShowFileHistoryWindowAsync(IReadOnlyList<string> relativePaths)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        var logs = await _svn.GetLogAsync(workingCopy, relativePaths, 80);
        using var form = new FileHistoryForm(workingCopy, relativePaths, logs, _svn);
        form.ShowDialog(this);
        var label = FormatPathListLabel(relativePaths);
        WriteOutput(logs.Count == 0
            ? $"没有读取到历史：{label}"
            : $"已打开历史窗口：{label}（{logs.Count} 条）");
    }

    private async Task LoadRepositoryHistoryAsync(int? limit = null)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var requestedLimit = Math.Max(InitialHistoryLimit, limit ?? _historyLoadedLimit);
        _historyLoadedLimit = requestedLimit;
        ClearHistoryDiffPreviewCache();
        SetBusy(true, $"正在读取仓库历史（最近 {requestedLimit} 条）...");
        try
        {
            var logs = await _svn.GetRepositoryLogAsync(_workingCopyText.Text.Trim(), requestedLimit);
            _latestRemoteLog = logs.FirstOrDefault(log => !log.IsUncommitted);
            FillHistoryList(logs);
            UpdateHistoryBadge(logs.Count);
            WriteOutput(logs.Count == 0 ? "没有读取到仓库历史。" : $"已读取最近 {logs.Count} 条仓库历史。");
            await CheckRemoteChangesAsync(showUpToDateMessage: false);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task LoadMoreRepositoryHistoryAsync()
    {
        var nextLimit = _historyLoadedLimit + HistoryLoadMoreStep;
        await LoadRepositoryHistoryAsync(nextLimit);
    }

    private async Task RunDeepHistorySearchAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var filter = HistorySearchFilter.Parse(_historySearchText.Text);
        if (filter.HasRevisionRange)
        {
            await LoadRepositoryHistoryRevisionRangeAsync(filter);
            return;
        }

        var targetLimit = Math.Max(_historyLoadedLimit, HistoryDeepSearchLimit);
        await LoadRepositoryHistoryAsync(targetLimit);
        ApplyHistoryFilter();
        WriteOutput(string.IsNullOrWhiteSpace(_historySearchText.Text)
            ? $"深度搜索已读取最近 {_historyRows.Count(log => !log.IsUncommitted)} 条历史。"
            : $"深度搜索已读取最近 {_historyRows.Count(log => !log.IsUncommitted)} 条历史，当前匹配 {_historyList.Items.Count} 条。");
    }

    private async Task LoadRepositoryHistoryRevisionRangeAsync(HistorySearchFilter filter)
    {
        if (filter.RevisionStart == null && filter.RevisionEnd == null)
        {
            return;
        }

        var start = filter.RevisionStart ?? filter.RevisionEnd!.Value;
        var end = filter.RevisionEnd ?? filter.RevisionStart!.Value;
        var rangeLimit = (int)Math.Min(HistoryRevisionRangeLimit, Math.Max(1, Math.Abs(end - start) + 1));

        ClearHistoryDiffPreviewCache();
        SetBusy(true, $"正在读取版本范围 r{start}-r{end}...");
        try
        {
            var rangeLogs = await _svn.GetRepositoryLogRangeAsync(_workingCopyText.Text.Trim(), start, end, rangeLimit);
            var mergedLogs = _historyRows
                .Where(log => !log.IsUncommitted)
                .Concat(rangeLogs)
                .GroupBy(log => log.Revision)
                .Select(group => group.OrderByDescending(log => log.ChangedFiles.Count).First())
                .OrderByDescending(log => log.Revision)
                .ToList();
            FillHistoryList(mergedLogs);
            UpdateHistoryBadge(mergedLogs.Count);
            ApplyHistoryFilter();
            WriteOutput(
                $"深度搜索已读取版本范围 r{Math.Min(start, end)}-r{Math.Max(start, end)}，新增/合并 {rangeLogs.Count} 条历史，当前匹配 {_historyList.Items.Count} 条。" +
                (Math.Abs(end - start) + 1 > HistoryRevisionRangeLimit ? $" 当前最多读取 {HistoryRevisionRangeLimit} 条，请缩小版本范围。" : ""));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunCommitAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selectedPaths = GetCommitSelectedPaths();

        if (selectedPaths.Count == 0)
        {
            var noSelectionMessage = _statusCommitVisibleOnlyCheck.Checked && _checkedStatusPaths.Count > 0
                ? $"当前筛选结果里没有已勾选文件。{Environment.NewLine}{Environment.NewLine}当前共有 {_checkedStatusPaths.Count} 个隐藏或不在筛选结果里的已勾选文件。取消“只提交当前筛选结果”，或在当前筛选结果中勾选文件后再提交。"
                : "请先勾选要提交的文件。";
            MessageBox.Show(noSelectionMessage, "没有选择文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var latestChanges = (await _svn.GetStatusAsync(workingCopy)).ToList();
        var conflicts = latestChanges.Where(change => change.Status == SvnStatusKind.Conflicted).ToList();
        string? globalBlockReason = null;
        if (conflicts.Count > 0)
        {
            RefreshConflictPanel(conflicts);
            globalBlockReason = $"当前工作副本仍有 {conflicts.Count} 个冲突。请先在“冲突”页处理完，再提交。";
        }

        var latestMap = latestChanges.ToDictionary(change => NormalizeRelativePath(change.RelativePath), StringComparer.OrdinalIgnoreCase);
        var selectedChanges = selectedPaths
            .Select(path => latestMap.TryGetValue(NormalizeRelativePath(path), out var change) ? change : null)
            .Where(change => change != null)
            .Cast<SvnChange>()
            .ToList();

        if (selectedChanges.Count == 0)
        {
            MessageBox.Show("勾选的文件当前没有可提交改动，请先刷新状态。", "没有可提交改动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshStatusAsync();
            return;
        }

        var preflight = await BuildCommitPreflightAsync(workingCopy, selectedChanges, conflicts);
        if (!ConfirmCommitPreflight(preflight))
        {
            OperationLogger.Log("CommitPreflightCancelled", workingCopy, preflight.ToLogText());
            return;
        }

        var message = "";
        using (var preview = new CommitPreviewForm(_settings.LastCommitMessage, selectedChanges, CommitBlockReason, preflight.Blocked ? preflight.BlockMessage : globalBlockReason))
        {
            if (preview.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            message = preview.CommitMessage;
            selectedChanges = preview.SelectedChanges.ToList();
        }

        if (selectedChanges.Count == 0)
        {
            MessageBox.Show("请至少保留一个要提交的文件。", "没有提交文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (message.Length < 4)
        {
            var continueShortMessage = MessageBox.Show(
                "提交说明比较短，可能不方便之后查历史。\r\n\r\n仍然继续提交？",
                "提交说明偏短",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (continueShortMessage != DialogResult.OK)
            {
                return;
            }
        }

        var result = await RunSvnOperationAsync("正在提交...", async () =>
        {
            var unversioned = selectedChanges.Where(change => change.Status == SvnStatusKind.Unversioned).ToList();
            var output = "";
            foreach (var change in unversioned)
            {
                var addResult = await _svn.AddAsync(workingCopy, change.RelativePath);
                output += addResult.CombinedOutput + Environment.NewLine;
            }

            var commitResult = await _svn.CommitAsync(workingCopy, selectedChanges.Select(change => change.RelativePath), message);
            _settings.LastCommitMessage = message;
            SaveSettings();
            return new ProcessResult(commitResult.ExitCode, output + commitResult.StandardOutput, commitResult.StandardError);
        });
        OperationLogger.Log(
            result?.ExitCode == 0 ? "CommitSuccess" : "CommitFailed",
            workingCopy,
            $"files={selectedChanges.Count}; message={message}");
        await RefreshStatusAsync();
        if (result?.ExitCode == 0)
        {
            await LoadRepositoryHistoryAsync();
            SelectTab("History");
        }
    }

    private async Task<ProcessResult?> RunSvnOperationAsync(string busyText, Func<Task<ProcessResult>> operation)
    {
        SetBusy(true, busyText);
        try
        {
            var result = await operation();
            WriteOutput(result.CombinedOutput);
            if (result.ExitCode != 0)
            {
                MessageBox.Show("SVN 命令执行失败，请查看下方输出。", "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return result;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return null;
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void ChooseWorkingCopy()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 SVN 工作目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_workingCopyText.Text.Trim()) ? _workingCopyText.Text.Trim() : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workingCopyText.Text = dialog.SelectedPath;
            SaveCurrentRepository();
        }
    }

    private void OpenWorkingCopyFolder()
    {
        var path = _workingCopyText.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show("本地目录不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void LoadAllFiles()
    {
        _ = LoadAllFilesAsync();
    }

    private void ScheduleFileTreeLoad()
    {
        if (!IsTab(_mainTabs.SelectedTab, "全部文件"))
        {
            return;
        }

        _fileTreeLoadDebounceTimer.Stop();
        _fileTreeLoadDebounceTimer.Start();
    }

    private async Task LoadAllFilesAsync()
    {
        _fileTreeLoadDebounceTimer.Stop();
        var root = _workingCopyText.Text.Trim();
        var search = _fileTreeSearchText.Text.Trim();
        var changedOnly = _fileTreeChangedOnlyCheck.Checked;
        var isFiltering = changedOnly || !string.IsNullOrWhiteSpace(search);
        var expandedPaths = isFiltering ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : GetExpandedTreePaths();
        if (expandedPaths.Count == 0 && !string.IsNullOrWhiteSpace(root))
        {
            expandedPaths = _settings.GetExpandedPaths(root);
        }

        CancelFileTreeLoad();
        var loadCts = new CancellationTokenSource();
        _fileTreeLoadCts = loadCts;
        var token = loadCts.Token;
        var request = new FileTreeLoadRequest(root, search, changedOnly, isFiltering, expandedPaths);
        ShowFileTreeMessage(string.IsNullOrWhiteSpace(root) ? "请选择本地目录。" : "正在加载文件树...");
        _fileTreeRefreshButton.Enabled = true;
        _fileTreeRefreshButton.Text = "停止";
        _fileTreeSearchText.Enabled = false;
        _fileTreeChangedOnlyCheck.Enabled = false;
        _fileTreeExpandButton.Enabled = false;
        _fileTreeCollapseButton.Enabled = false;
        _statusLabel.Text = "正在加载全部文件...";
        try
        {
            var result = await Task.Run(() => BuildFileTree(request, token), token);
            if (!IsCurrentFileTreeLoad(loadCts) || token.IsCancellationRequested)
            {
                return;
            }

            ApplyFileTreeBuildResult(result);
            _statusLabel.Text = result.RootNode == null
                ? "就绪"
                : result.IsLazy
                    ? $"已加载根目录，展开文件夹时继续读取。SVN 状态 {result.StatusMap.Count} 项"
                    : result.IsTruncated
                    ? $"已显示前 {result.FileCount} 个匹配文件，建议搜索缩小范围"
                    : $"已加载 {result.FileCount} 个文件";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentFileTreeLoad(loadCts))
            {
                _statusLabel.Text = "已取消加载全部文件";
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && IsCurrentFileTreeLoad(loadCts))
            {
                ShowFileTreeMessage("文件树加载失败，请查看终端输出。");
                WriteOutput(ex.ToString());
                _statusLabel.Text = "就绪";
            }
        }
        finally
        {
            if (IsCurrentFileTreeLoad(loadCts))
            {
                _fileTreeLoadCts = null;
                _fileTreeRefreshButton.Enabled = true;
                _fileTreeRefreshButton.Text = "刷新";
                _fileTreeSearchText.Enabled = true;
                _fileTreeChangedOnlyCheck.Enabled = true;
                _fileTreeExpandButton.Enabled = true;
                _fileTreeCollapseButton.Enabled = true;
            }
        }
    }

    private void CancelFileTreeLoad()
    {
        try
        {
            _fileTreeLoadCts?.Cancel();
        }
        catch
        {
        }
    }

    private bool IsCurrentFileTreeLoad(CancellationTokenSource loadCts)
    {
        return ReferenceEquals(_fileTreeLoadCts, loadCts);
    }

    private static IEnumerable<string> EnumerateWorkingCopyFiles(string rootPath, CancellationToken token)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);
        while (pendingDirectories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var childDirectory in subDirectories.Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(childDirectory), ".svn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                yield return file;
            }
        }
    }

    private static IEnumerable<FileTreeFileEntry> EnumerateFilteredWorkingCopyFiles(string rootPath, string search, CancellationToken token)
    {
        foreach (var filePath in EnumerateWorkingCopyFiles(rootPath, token))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(rootPath, filePath);
            if (SvnConflictArtifact.IsAuxiliaryPath(relativePath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search) &&
                !relativePath.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(filePath).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new FileTreeFileEntry(relativePath, filePath, SvnStatusKind.None);
        }
    }

    private static IEnumerable<FileTreeFileEntry> EnumerateChangedStatusFiles(
        string rootPath,
        string search,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap,
        CancellationToken token)
    {
        foreach (var item in statusMap.OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (item.Value is SvnStatusKind.None or SvnStatusKind.Normal)
            {
                continue;
            }

            if (SvnConflictArtifact.IsAuxiliaryPath(item.Key))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search) &&
                !item.Key.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(item.Key).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new FileTreeFileEntry(item.Key, Path.Combine(rootPath, item.Key), item.Value);
        }
    }

    private static int PopulateLazyDirectoryNode(
        TreeNode directoryNode,
        string rootPath,
        string relativeDirectory,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directoryNode.Nodes.Clear();
        var fullDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? rootPath
            : Path.Combine(rootPath, relativeDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            return 0;
        }

        var added = 0;
        foreach (var directory in SafeEnumerateDirectories(fullDirectory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(directory), ".svn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, directory);
            if (IsReservedDevicePath(relativePath))
            {
                continue;
            }

            var node = CreateFileTreeFolderNode(relativePath, Path.GetFileName(directory));
            if (DirectoryMayHaveChildren(directory))
            {
                node.Nodes.Add(CreateLazyFileTreePlaceholder());
            }

            directoryNode.Nodes.Add(node);
            added++;
        }

        foreach (var file in SafeEnumerateFiles(fullDirectory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(rootPath, file);
            if (SvnConflictArtifact.IsAuxiliaryPath(relativePath) || IsReservedDevicePath(relativePath))
            {
                continue;
            }

            statusMap.TryGetValue(NormalizeRelativePath(relativePath), out var status);
            directoryNode.Nodes.Add(CreateFileTreeFileNode(relativePath, new FileInfo(file), status));
            added++;
        }

        return added;
    }

    private void EnsureLazyFileTreeChildren(TreeNode node)
    {
        if (node.Tag is not FileTreeNodeInfo { IsFile: false } info ||
            node.Nodes.Count != 1 ||
            node.Nodes[0].Tag is not LazyFileTreePlaceholder)
        {
            return;
        }

        var root = _workingCopyText.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        _loadingFileTree = true;
        WinFormsRendering.SetRedraw(_fileTree, false);
        _fileTree.BeginUpdate();
        try
        {
            PopulateLazyDirectoryNode(node, root, info.RelativePath, _fileTreeStatusMap, CancellationToken.None);
        }
        finally
        {
            _fileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_fileTree, true);
            _loadingFileTree = false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool DirectoryMayHaveChildren(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Any(path => !string.Equals(Path.GetFileName(path), ".svn", StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private FileTreeBuildResult BuildFileTree(FileTreeLoadRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            return new FileTreeBuildResult(null, "请选择本地目录。", 0, false, false, request.IsFiltering, request.ExpandedPaths, new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase));
        }

        if (!Directory.Exists(request.RootPath))
        {
            return new FileTreeBuildResult(null, "本地目录不存在。", 0, false, false, request.IsFiltering, request.ExpandedPaths, new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase));
        }

        var rootInfo = new DirectoryInfo(request.RootPath);
        var rootNode = new TreeNode(rootInfo.Name)
        {
            Tag = new FileTreeNodeInfo("", false),
            ToolTipText = request.RootPath,
            ImageKey = "folder",
            SelectedImageKey = "folder",
        };

        var statusMap = GetStatusMapForTree(request.RootPath);
        token.ThrowIfCancellationRequested();
        if (!request.IsFiltering)
        {
            var topLevelCount = PopulateLazyDirectoryNode(rootNode, request.RootPath, "", statusMap, token);
            return new FileTreeBuildResult(rootNode, "", topLevelCount, false, true, request.IsFiltering, request.ExpandedPaths, statusMap);
        }

        var files = new List<FileTreeFileEntry>();
        var isTruncated = false;
        var filteredFiles = request.ChangedOnly
            ? EnumerateChangedStatusFiles(request.RootPath, request.Search, statusMap, token)
            : EnumerateFilteredWorkingCopyFiles(request.RootPath, request.Search, token);
        foreach (var file in filteredFiles)
        {
            token.ThrowIfCancellationRequested();
            var normalized = NormalizeRelativePath(file.RelativePath);
            var hasStatus = statusMap.TryGetValue(normalized, out var status) && status != SvnStatusKind.None && status != SvnStatusKind.Normal;
            if (request.ChangedOnly && !hasStatus)
            {
                continue;
            }

            if (files.Count >= MaxFileTreeDisplayFiles)
            {
                isTruncated = true;
                break;
            }

            files.Add(file with { Status = status });
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.CurrentCultureIgnoreCase));
        if (isTruncated)
        {
            rootNode.Nodes.Add(new TreeNode($"只显示前 {MaxFileTreeDisplayFiles} 个匹配文件，请用搜索或“仅改动”缩小范围。"));
        }

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            AddFileNode(rootNode, file.RelativePath, new FileInfo(file.FullPath), file.Status);
        }

        return new FileTreeBuildResult(rootNode, "", files.Count, isTruncated, false, request.IsFiltering, request.ExpandedPaths, statusMap);
    }

    private void ApplyFileTreeBuildResult(FileTreeBuildResult result)
    {
        _loadingFileTree = true;
        _lastFileTreeFileCount = result.FileCount;
        _fileTreeStatusMap = result.StatusMap;
        _styledFileTreeSelectionPaths.Clear();
        WinFormsRendering.SetRedraw(_fileTree, false);
        _fileTree.BeginUpdate();
        _fileTree.Nodes.Clear();
        try
        {
            if (result.RootNode == null)
            {
                _fileTree.Nodes.Add(new TreeNode(result.Message));
                return;
            }

            _fileTree.Nodes.Add(result.RootNode);
            if (result.IsFiltering)
            {
                if (result.FileCount <= MaxFileTreeAutoExpandFiles)
                {
                    result.RootNode.ExpandAll();
                }
                else
                {
                    result.RootNode.Expand();
                }
            }
            else
            {
                RestoreExpandedTreePaths(result.ExpandedPaths);
            }

            PruneFileTreeSelection();
            ApplyFileTreeSelectionStyles();
        }
        finally
        {
            _fileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_fileTree, true);
            _fileTree.Invalidate();
            _loadingFileTree = false;
        }
    }

    private void ShowFileTreeMessage(string message)
    {
        _loadingFileTree = true;
        WinFormsRendering.SetRedraw(_fileTree, false);
        _fileTree.BeginUpdate();
        try
        {
            _fileTree.Nodes.Clear();
            _fileTree.Nodes.Add(new TreeNode(message));
        }
        finally
        {
            _fileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_fileTree, true);
            _fileTree.Invalidate();
            _loadingFileTree = false;
        }
    }

    private void CollapseFileTreeToRoot()
    {
        _fileTree.CollapseAll();
        if (_fileTree.Nodes.Count > 0)
        {
            _fileTree.Nodes[0].Expand();
        }
    }

    private void ExpandFileTreeSafely()
    {
        if (_lastFileTreeFileCount > MaxFileTreeExpandAllFiles)
        {
            MessageBox.Show(
                this,
                $"当前文件树有 {_lastFileTreeFileCount} 个文件，一次性全部展开会明显卡顿。\r\n\r\n请先使用搜索或“仅改动”，或者手动展开需要查看的目录。",
                "文件过多",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (_fileTree.Nodes.Count > 0)
            {
                _fileTree.Nodes[0].Expand();
            }

            return;
        }

        _fileTree.ExpandAll();
    }

    private void HandleFileTreeNodeMouseClick(TreeNode? node, MouseButtons button)
    {
        if (node == null)
        {
            return;
        }

        if (button == MouseButtons.Left)
        {
            SelectFileTreeNode(node, ModifierKeys);
            return;
        }

        if (button == MouseButtons.Right)
        {
            if (!IsFileTreeNodeSelected(node))
            {
                SelectFileTreeNode(node, Keys.None);
            }

            _fileTree.SelectedNode = node;
        }
    }

    private void SelectFileTreeNode(TreeNode node, Keys modifiers)
    {
        var path = GetFileTreeSelectionPath(node);
        if (path == null)
        {
            _selectedFileTreePaths.Clear();
            _fileTreeSelectionAnchorPath = null;
            _fileTree.SelectedNode = node;
            ApplyFileTreeSelectionStyles();
            return;
        }

        if (modifiers.HasFlag(Keys.Shift) && !string.IsNullOrWhiteSpace(_fileTreeSelectionAnchorPath))
        {
            SelectFileTreeRange(_fileTreeSelectionAnchorPath, path);
        }
        else if (modifiers.HasFlag(Keys.Control))
        {
            if (!_selectedFileTreePaths.Add(path))
            {
                _selectedFileTreePaths.Remove(path);
            }
        }
        else
        {
            _selectedFileTreePaths.Clear();
            _selectedFileTreePaths.Add(path);
        }

        _fileTreeSelectionAnchorPath = path;
        _fileTree.SelectedNode = node;
        ApplyFileTreeSelectionStyles();
    }

    private void SelectFileTreeRange(string anchorPath, string currentPath)
    {
        var visibleNodes = GetVisibleFileTreeNodes().ToList();
        var anchorIndex = visibleNodes.FindIndex(node => string.Equals(GetFileTreeSelectionPath(node), anchorPath, StringComparison.OrdinalIgnoreCase));
        var currentIndex = visibleNodes.FindIndex(node => string.Equals(GetFileTreeSelectionPath(node), currentPath, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0 || currentIndex < 0)
        {
            _selectedFileTreePaths.Clear();
            _selectedFileTreePaths.Add(currentPath);
            return;
        }

        _selectedFileTreePaths.Clear();
        var start = Math.Min(anchorIndex, currentIndex);
        var end = Math.Max(anchorIndex, currentIndex);
        for (var index = start; index <= end; index++)
        {
            var path = GetFileTreeSelectionPath(visibleNodes[index]);
            if (path != null)
            {
                _selectedFileTreePaths.Add(path);
            }
        }
    }

    private IEnumerable<TreeNode> GetVisibleFileTreeNodes()
    {
        foreach (TreeNode node in _fileTree.Nodes)
        {
            foreach (var child in EnumerateVisibleNodes(node))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<TreeNode> EnumerateVisibleNodes(TreeNode node)
    {
        yield return node;
        if (!node.IsExpanded)
        {
            yield break;
        }

        foreach (TreeNode child in node.Nodes)
        {
            foreach (var visibleChild in EnumerateVisibleNodes(child))
            {
                yield return visibleChild;
            }
        }
    }

    private bool IsFileTreeNodeSelected(TreeNode node)
    {
        var path = GetFileTreeSelectionPath(node);
        return path != null && _selectedFileTreePaths.Contains(path);
    }

    private static string? GetFileTreeSelectionPath(TreeNode? node)
    {
        if (node?.Tag is not FileTreeNodeInfo info || string.IsNullOrWhiteSpace(info.RelativePath))
        {
            return null;
        }

        return NormalizeRelativePath(SvnConflictArtifact.NormalizeToBasePath(info.RelativePath));
    }

    private void ApplyFileTreeSelectionStyles()
    {
        var pathsToRefresh = _styledFileTreeSelectionPaths
            .Concat(_selectedFileTreePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in pathsToRefresh)
        {
            var node = FindFileTreeNodeByPath(path);
            if (node != null)
            {
                ApplyFileTreeSelectionStyle(node);
                WinFormsRendering.InvalidateTreeNodeRow(_fileTree, node);
            }
        }

        _styledFileTreeSelectionPaths.Clear();
        foreach (var path in _selectedFileTreePaths)
        {
            _styledFileTreeSelectionPaths.Add(path);
        }
    }

    private void ApplyFileTreeSelectionStyle(TreeNode node)
    {
        var selected = IsFileTreeNodeSelected(node);
        if (selected)
        {
            node.BackColor = Color.FromArgb(226, 241, 255);
            node.ForeColor = Color.FromArgb(15, 76, 129);
        }
        else
        {
            node.BackColor = _fileTree.BackColor;
            node.ForeColor = GetFileTreeDefaultForeColor(node);
        }
    }

    private static Color GetFileTreeDefaultForeColor(TreeNode node)
    {
        if (node.Tag is FileTreeNodeInfo { IsFile: false })
        {
            return Color.FromArgb(55, 65, 81);
        }

        return StatusFromNodeText(node.Text) switch
        {
            SvnStatusKind.Modified => StatusColor(SvnStatusKind.Modified),
            SvnStatusKind.Added => StatusColor(SvnStatusKind.Added),
            SvnStatusKind.Deleted => StatusColor(SvnStatusKind.Deleted),
            SvnStatusKind.Unversioned => StatusColor(SvnStatusKind.Unversioned),
            SvnStatusKind.Missing => StatusColor(SvnStatusKind.Missing),
            SvnStatusKind.Conflicted => StatusColor(SvnStatusKind.Conflicted),
            SvnStatusKind.Replaced => StatusColor(SvnStatusKind.Replaced),
            _ => SystemColors.WindowText,
        };
    }

    private static SvnStatusKind StatusFromNodeText(string text)
    {
        if (text.Length < 2 || text[1] != ' ')
        {
            return SvnStatusKind.None;
        }

        return text[0] switch
        {
            'M' => SvnStatusKind.Modified,
            'A' => SvnStatusKind.Added,
            'D' => SvnStatusKind.Deleted,
            '?' => SvnStatusKind.Unversioned,
            '!' => SvnStatusKind.Missing,
            'C' => SvnStatusKind.Conflicted,
            'R' => SvnStatusKind.Replaced,
            _ => SvnStatusKind.None,
        };
    }

    private void PruneFileTreeSelection()
    {
        if (_selectedFileTreePaths.Count == 0)
        {
            return;
        }

        var existingPaths = GetAllFileTreeSelectablePaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedFileTreePaths.RemoveWhere(path => !existingPaths.Contains(path));
        if (_fileTreeSelectionAnchorPath != null && !existingPaths.Contains(_fileTreeSelectionAnchorPath))
        {
            _fileTreeSelectionAnchorPath = _selectedFileTreePaths.FirstOrDefault();
        }
    }

    private IEnumerable<string> GetAllFileTreeSelectablePaths()
    {
        foreach (TreeNode node in _fileTree.Nodes)
        {
            foreach (var path in GetAllFileTreeSelectablePaths(node))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> GetAllFileTreeSelectablePaths(TreeNode node)
    {
        var path = GetFileTreeSelectionPath(node);
        if (path != null)
        {
            yield return path;
        }

        foreach (TreeNode child in node.Nodes)
        {
            foreach (var childPath in GetAllFileTreeSelectablePaths(child))
            {
                yield return childPath;
            }
        }
    }

    private TreeNode? FindFileTreeNodeByPath(string relativePath)
    {
        var normalizedPath = NormalizeRelativePath(SvnConflictArtifact.NormalizeToBasePath(relativePath));
        foreach (TreeNode node in _fileTree.Nodes)
        {
            var found = FindFileTreeNodeByPath(node, normalizedPath);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static TreeNode? FindFileTreeNodeByPath(TreeNode node, string normalizedPath)
    {
        var nodePath = GetFileTreeSelectionPath(node);
        if (nodePath != null && string.Equals(nodePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (TreeNode child in node.Nodes)
        {
            var found = FindFileTreeNodeByPath(child, normalizedPath);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void AddFileNode(TreeNode rootNode, string relativePath, FileInfo file, SvnStatusKind status)
    {
        if (IsReservedDevicePath(relativePath))
        {
            return;
        }

        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = rootNode;
        var currentPath = "";
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            currentPath = string.IsNullOrEmpty(currentPath) ? part : Path.Combine(currentPath, part);
            var isFile = index == parts.Length - 1;
            var existing = current.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(CleanTreeNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                var tooltip = isFile
                    ? BuildFileTooltip(currentPath, file)
                    : currentPath;
                existing = new TreeNode(part)
                {
                    Tag = new FileTreeNodeInfo(currentPath, isFile),
                    ToolTipText = tooltip,
                    ImageKey = isFile ? FileImageKey(currentPath, status) : "folder",
                    SelectedImageKey = isFile ? FileImageKey(currentPath, status) : "folder",
                    ForeColor = isFile ? SystemColors.WindowText : Color.FromArgb(55, 65, 81),
                };
                if (!isFile)
                {
                    existing.NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                }

                current.Nodes.Add(existing);
            }

            if (isFile && status != SvnStatusKind.None && status != SvnStatusKind.Normal)
            {
                existing.Text = $"{StatusPrefix(status)} {part}";
                existing.ForeColor = StatusColor(status);
                existing.ToolTipText += $"\r\n状态：{StatusText(status)}";
                existing.ImageKey = "changed";
                existing.SelectedImageKey = "changed";
            }

            current = existing;
        }
    }

    private static TreeNode CreateFileTreeFolderNode(string relativePath, string name)
    {
        return new TreeNode(name)
        {
            Tag = new FileTreeNodeInfo(relativePath, false),
            ToolTipText = relativePath,
            ImageKey = "folder",
            SelectedImageKey = "folder",
            ForeColor = Color.FromArgb(55, 65, 81),
            NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        };
    }

    private static TreeNode CreateFileTreeFileNode(string relativePath, FileInfo file, SvnStatusKind status)
    {
        var name = Path.GetFileName(relativePath);
        var node = new TreeNode(name)
        {
            Tag = new FileTreeNodeInfo(relativePath, true),
            ToolTipText = BuildFileTooltip(relativePath, file),
            ImageKey = FileImageKey(relativePath, status),
            SelectedImageKey = FileImageKey(relativePath, status),
            ForeColor = SystemColors.WindowText,
        };
        if (status != SvnStatusKind.None && status != SvnStatusKind.Normal)
        {
            node.Text = $"{StatusPrefix(status)} {name}";
            node.ForeColor = StatusColor(status);
            node.ToolTipText += $"\r\n状态：{StatusText(status)}";
            node.ImageKey = "changed";
            node.SelectedImageKey = "changed";
        }

        return node;
    }

    private static TreeNode CreateLazyFileTreePlaceholder()
    {
        return new TreeNode("正在加载...")
        {
            Tag = LazyFileTreePlaceholder.Instance,
            ForeColor = Color.FromArgb(100, 116, 139),
        };
    }

    private static string BuildFileTooltip(string relativePath, FileInfo file)
    {
        try
        {
            return $"{relativePath}\r\n修改时间：{file.LastWriteTime:yyyy-MM-dd HH:mm}\r\n大小：{FormatBytes(file.Length)}";
        }
        catch (IOException)
        {
            return relativePath;
        }
        catch (UnauthorizedAccessException)
        {
            return relativePath;
        }
    }

    private static bool IsReservedDevicePath(string relativePath)
    {
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(IsReservedDeviceName);
    }

    private static bool IsReservedDeviceName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name).TrimEnd(' ');
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Length == 4 &&
            (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            baseName[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static string FileImageKey(string path, SvnStatusKind status)
    {
        if (status != SvnStatusKind.None && status != SvnStatusKind.Normal)
        {
            return "changed";
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return "xml";
        }

        return extension.Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "lua" : "file";
    }

    private static string CleanTreeNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "MAD?!CR".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }

    private Dictionary<string, SvnStatusKind> GetStatusMapForTree(string workingCopyPath)
    {
        try
        {
            return _svn.GetStatus(workingCopyPath)
                .ToDictionary(change => NormalizeRelativePath(change.RelativePath), change => change.Status, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string StatusPrefix(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Modified => "M",
            SvnStatusKind.Added => "A",
            SvnStatusKind.Deleted => "D",
            SvnStatusKind.Unversioned => "?",
            SvnStatusKind.Missing => "!",
            SvnStatusKind.Conflicted => "C",
            SvnStatusKind.Replaced => "R",
            _ => "",
        };
    }

    private static string StatusText(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Modified => "已修改",
            SvnStatusKind.Added => "已新增",
            SvnStatusKind.Deleted => "已删除",
            SvnStatusKind.Unversioned => "未加入版本控制",
            SvnStatusKind.Missing => "本地缺失",
            SvnStatusKind.Conflicted => "冲突",
            SvnStatusKind.Replaced => "已替换",
            _ => "",
        };
    }

    private static Color StatusColor(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Modified => Color.FromArgb(166, 103, 34),
            SvnStatusKind.Added => Color.FromArgb(38, 128, 72),
            SvnStatusKind.Deleted => Color.FromArgb(170, 67, 67),
            SvnStatusKind.Unversioned => Color.FromArgb(93, 88, 161),
            SvnStatusKind.Missing => Color.FromArgb(170, 67, 67),
            SvnStatusKind.Conflicted => Color.FromArgb(190, 50, 50),
            SvnStatusKind.Replaced => Color.FromArgb(128, 79, 160),
            _ => SystemColors.WindowText,
        };
    }

    private string? GetSelectedRelativePath()
    {
        var selectedChange = GetSelectedChange();
        if (selectedChange != null)
        {
            return SvnConflictArtifact.NormalizeToBasePath(selectedChange.RelativePath);
        }

        if (_conflictGrid.SelectedRows.Count == 1 &&
            _conflictGrid.SelectedRows[0].DataBoundItem is ConflictGridRow conflict)
        {
            return conflict.RelativePath;
        }

        if (_selectedFileTreePaths.Count == 1)
        {
            var selectedPath = _selectedFileTreePaths.First();
            if (FindFileTreeNodeByPath(selectedPath)?.Tag is FileTreeNodeInfo { IsFile: true })
            {
                return SvnConflictArtifact.NormalizeToBasePath(selectedPath);
            }
        }

        if (_fileTree.SelectedNode?.Tag is FileTreeNodeInfo { IsFile: true } fileNode && !string.IsNullOrWhiteSpace(fileNode.RelativePath))
        {
            return SvnConflictArtifact.NormalizeToBasePath(fileNode.RelativePath);
        }

        return null;
    }

    private IReadOnlyList<string> GetSelectedFileTreeHistoryPaths()
    {
        if (_selectedFileTreePaths.Count > 0)
        {
            return RemoveNestedPaths(_selectedFileTreePaths)
                .Select(SvnConflictArtifact.NormalizeToBasePath)
                .ToList();
        }

        if (_fileTree.SelectedNode?.Tag is FileTreeNodeInfo nodeInfo && !string.IsNullOrWhiteSpace(nodeInfo.RelativePath))
        {
            return [SvnConflictArtifact.NormalizeToBasePath(nodeInfo.RelativePath)];
        }

        var selectedChange = GetSelectedChange();
        if (selectedChange != null)
        {
            return [SvnConflictArtifact.NormalizeToBasePath(selectedChange.RelativePath)];
        }

        if (_conflictGrid.SelectedRows.Count == 1 &&
            _conflictGrid.SelectedRows[0].DataBoundItem is ConflictGridRow conflict)
        {
            return [conflict.RelativePath];
        }

        return [];
    }

    private static IReadOnlyList<string> RemoveNestedPaths(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<string>();
        foreach (var path in normalized)
        {
            if (result.Any(parent => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    private SvnChange? GetSelectedChange()
    {
        return _changesList.SelectedItems.Count == 1 && _changesList.SelectedItems[0].Tag is SvnChange change
            ? change
            : null;
    }

    private List<SvnChange> GetSelectedStatusChanges()
    {
        return _changesList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnChange)
            .Where(change => change != null)
            .Cast<SvnChange>()
            .ToList();
    }

    private void OpenSelectedStatusFile()
    {
        var change = GetSelectedChange();
        if (change == null)
        {
            return;
        }

        var path = Path.Combine(_workingCopyText.Text.Trim(), change.RelativePath);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("本地文件不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenSelectedStatusFileFolder()
    {
        var change = GetSelectedChange();
        if (change == null)
        {
            return;
        }

        var path = Path.Combine(_workingCopyText.Text.Trim(), change.RelativePath);
        var argument = File.Exists(path)
            ? $"/select,\"{path}\""
            : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(argument))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
    }

    private void AddSelectedFileTreeFavorite()
    {
        if (_fileTree.SelectedNode?.Tag is not FileTreeNodeInfo info)
        {
            MessageBox.Show("请先在全部文件里选中一个目录或文件。", "未选择目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var favoritePath = info.IsFile
            ? NormalizeRelativePath(Path.GetDirectoryName(info.RelativePath) ?? "")
            : NormalizeRelativePath(info.RelativePath);
        if (string.IsNullOrWhiteSpace(favoritePath))
        {
            favoritePath = ".";
        }

        if (!_settings.FavoriteFileTreePaths.Any(path => string.Equals(path, favoritePath, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.FavoriteFileTreePaths.Add(favoritePath);
            _settings.FavoriteFileTreePaths.Sort(StringComparer.CurrentCultureIgnoreCase);
            _settings.Save();
            BuildMoreActionsMenu();
        }

        WriteOutput($"已收藏目录：{favoritePath}");
    }

    private async Task NavigateToFavoriteFileTreePathAsync(string relativePath)
    {
        SelectTab("全部文件");
        if (_fileTree.Nodes.Count == 0 || _fileTree.Nodes[0].Tag is not FileTreeNodeInfo)
        {
            await LoadAllFilesAsync();
        }

        var node = FindOrLoadFileTreeNode(relativePath == "." ? "" : relativePath);
        if (node == null)
        {
            MessageBox.Show($"没有找到收藏目录：{relativePath}", "无法跳转", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        node.EnsureVisible();
        _fileTree.SelectedNode = node;
        SelectFileTreeNode(node, Keys.None);
    }

    private TreeNode? FindOrLoadFileTreeNode(string relativePath)
    {
        if (_fileTree.Nodes.Count == 0)
        {
            return null;
        }

        var current = _fileTree.Nodes[0];
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            current.Expand();
            return current;
        }

        foreach (var part in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            EnsureLazyFileTreeChildren(current);
            current.Expand();
            var next = current.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node => string.Equals(CleanTreeNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private string CurrentWorkingCopyKey()
    {
        return _workingCopyText.Text.Trim();
    }

    private HashSet<string> GetExpandedTreePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode node in _fileTree.Nodes)
        {
            CollectExpandedTreePaths(node, paths);
        }

        return paths;
    }

    private static void CollectExpandedTreePaths(TreeNode node, HashSet<string> paths)
    {
        if (node.IsExpanded && node.Tag is FileTreeNodeInfo { IsFile: false } info)
        {
            paths.Add(info.RelativePath);
        }

        foreach (TreeNode child in node.Nodes)
        {
            CollectExpandedTreePaths(child, paths);
        }
    }

    private void RestoreExpandedTreePaths(HashSet<string> paths)
    {
        if (_fileTree.Nodes.Count == 0)
        {
            return;
        }

        _fileTree.Nodes[0].Expand();
        foreach (TreeNode node in _fileTree.Nodes)
        {
            RestoreExpandedTreePaths(node, paths);
        }
    }

    private static void RestoreExpandedTreePaths(TreeNode node, HashSet<string> paths)
    {
        if (node.Tag is FileTreeNodeInfo { IsFile: false } info && paths.Contains(info.RelativePath))
        {
            node.Expand();
        }

        foreach (TreeNode child in node.Nodes)
        {
            RestoreExpandedTreePaths(child, paths);
        }
    }

    private void SaveTreeExpansionState()
    {
        if (_loadingFileTree || _lastFileTreeFileCount > MaxFileTreeExpandAllFiles)
        {
            return;
        }

        _treeExpansionSaveTimer.Stop();
        _treeExpansionSaveTimer.Start();
    }

    private void SaveTreeExpansionStateCore()
    {
        if (_loadingFileTree || _lastFileTreeFileCount > MaxFileTreeExpandAllFiles)
        {
            return;
        }

        _settings.SetExpandedPaths(CurrentWorkingCopyKey(), GetExpandedTreePaths());
        _settings.Save();
    }

    private void FillHistoryList(IReadOnlyList<SvnLogEntry> logs)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        var hasWorkingCopy = Directory.Exists(workingCopy) && Directory.Exists(Path.Combine(workingCopy, ".svn"));
        var info = hasWorkingCopy ? RefreshWorkingCopyRevisionStatus() : WorkingCopyInfo.Empty;
        var changes = hasWorkingCopy ? _svn.GetStatus(workingCopy) : [];
        var workingCopyRevision = info.CurrentContentRevision;
        var latestRemoteRevision = logs
            .Where(log => !log.IsUncommitted && log.Revision > 0)
            .Select(log => log.Revision)
            .DefaultIfEmpty(0)
            .Max();
        _historyRows = [];
        var summary = info == WorkingCopyInfo.Empty
            ? ""
            : $"当前工作副本版本：{info.DisplayRevisionText}{Environment.NewLine}当前文件内容最高版本：r{workingCopyRevision}{Environment.NewLine}{info.Url}";
        if (latestRemoteRevision > 0 && info != WorkingCopyInfo.Empty)
        {
            summary += $"{Environment.NewLine}远端最新历史版本：r{latestRemoteRevision}";
            if (workingCopyRevision > 0 && latestRemoteRevision > workingCopyRevision)
            {
                summary += $"（本地未更新，落后 {latestRemoteRevision - workingCopyRevision} 个版本）";
            }
        }

        ShowHistorySummary(summary);
        if (changes.Count > 0)
        {
            var uncommitted = new SvnLogEntry(0, "*", DateTimeOffset.Now, $"Uncommitted changes ({changes.Count} files)")
            {
                IsUncommitted = true,
                ChangedFiles = changes.Select(change => ChangedFileEntry.FromWorkingCopy(change.Status, change.RelativePath)).ToList(),
            };
            _historyRows.Add(uncommitted);
        }

        var hasWorkingCopyRevisionInLoadedLogs = workingCopyRevision > 0 && logs.Any(log => log.Revision == workingCopyRevision);
        if (info != WorkingCopyInfo.Empty && workingCopyRevision > 0 && !hasWorkingCopyRevisionInLoadedLogs)
        {
            _historyRows.Add(new SvnLogEntry(
                workingCopyRevision,
                "LOCAL",
                DateTimeOffset.MinValue,
                $"当前工作副本位于 r{workingCopyRevision}。这个版本不在当前已加载的最近 {logs.Count} 条历史中；可以点击“加载更多”“深度搜索”，或搜索 rev:{workingCopyRevision} 查看完整提交详情。")
            {
                IsWorkingCopyRevision = true,
            });
        }

        foreach (var log in logs.OrderByDescending(log => log.Revision))
        {
            _historyRows.Add(log with { IsWorkingCopyRevision = log.Revision == workingCopyRevision });
        }
        ApplyHistoryFilter(selectWorkingCopyRevision: true);
    }

    private void ApplyHistoryFilter(bool selectWorkingCopyRevision = false)
    {
        var filter = HistorySearchFilter.Parse(_historySearchText.Text);
        var rows = filter.IsEmpty
            ? _historyRows
            : _historyRows.Where(log => filter.Matches(log)).ToList();
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        ClearHistoryChangedFiles();
        foreach (var row in rows)
        {
            AddHistoryItem(row);
        }
        _historyList.EndUpdate();
        UpdateHistorySearchControls();

        if (_historyList.Items.Count == 0)
        {
            _selectedHistoryLog = null;
            var loadedCount = _historyRows.Count(log => !log.IsUncommitted);
            ShowHistorySummary(filter.IsEmpty
                ? ""
                : $"没有匹配的提交。当前只在已加载的 {loadedCount} 条历史里搜索；可以点击“深度搜索”读取更早提交。");
            return;
        }

        var itemToSelect = selectWorkingCopyRevision || filter.IsEmpty
            ? _historyList.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Tag is SvnLogEntry { IsWorkingCopyRevision: true })
            : null;
        itemToSelect ??= _historyList.Items[0];
        itemToSelect.Selected = true;
        itemToSelect.Focused = true;
        itemToSelect.EnsureVisible();
    }

    private void UpdateHistorySearchControls()
    {
        var filter = HistorySearchFilter.Parse(_historySearchText.Text);
        var loadedCount = _historyRows.Count(log => !log.IsUncommitted);
        var matchedCount = _historyList.Items.Count;
        _historySearchScopeLabel.Text = filter.IsEmpty
            ? $"已加载 {loadedCount} 条"
            : $"匹配 {matchedCount}/{loadedCount} 条";

        var canUseHistory = !UseWaitCursor && ValidateWorkingCopyPathForBackground();
        _historyLoadMoreButton.Enabled = canUseHistory;
        _historyDeepSearchButton.Enabled = canUseHistory;
        _historyClearSearchButton.Enabled = !string.IsNullOrWhiteSpace(_historySearchText.Text);
    }

    private void AddHistoryItem(SvnLogEntry log)
    {
        var item = new ListViewItem(log.GraphText) { Tag = log, ImageKey = "row-height" };
        item.SubItems.Add(log.DescriptionText);
        item.SubItems.Add(log.LocalDateText);
        item.SubItems.Add(log.Author);
        item.SubItems.Add(log.RevisionText);
        if (log.IsUncommitted)
        {
            item.Font = new Font(_historyList.Font, FontStyle.Bold);
            item.BackColor = Color.FromArgb(255, 250, 230);
        }
        else if (log.IsWorkingCopyRevision)
        {
            item.BackColor = Color.FromArgb(221, 235, 247);
            item.Font = new Font(_historyList.Font, FontStyle.Bold);
        }

        _historyList.Items.Add(item);
    }

    private void FocusFirstChangedFileInSelectedHistory()
    {
        if (_historyList.SelectedItems.Count != 1 || _historyList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        _selectedHistoryLog = log;
        PopulateHistoryChangedFiles(log);
        var filter = HistorySearchFilter.Parse(_historySearchText.Text);
        var firstFileNode = FindBestChangedFileNode(_historyChangedFilesTree.Nodes.Cast<TreeNode>(), filter);
        if (firstFileNode == null)
        {
            return;
        }

        firstFileNode.EnsureVisible();
        _historyChangedFilesTree.SelectedNode = firstFileNode;
        _historyChangedFilesTree.Focus();
    }

    private static TreeNode? FindBestChangedFileNode(IEnumerable<TreeNode> nodes, HistorySearchFilter filter)
    {
        if (!filter.IsEmpty)
        {
            var matched = FindChangedFileNode(nodes, file => filter.MatchesFileForNavigation(file));
            if (matched != null)
            {
                return matched;
            }
        }

        return FindFirstChangedFileNode(nodes);
    }

    private static TreeNode? FindChangedFileNode(IEnumerable<TreeNode> nodes, Func<ChangedFileEntry, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry file && predicate(file))
            {
                return node;
            }

            var child = FindChangedFileNode(node.Nodes.Cast<TreeNode>(), predicate);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private static TreeNode? FindFirstChangedFileNode(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry)
            {
                return node;
            }

            var child = FindFirstChangedFileNode(node.Nodes.Cast<TreeNode>());
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private void BuildHistoryListMenu()
    {
        _historyListMenu.Items.Clear();
        _historyListMenu.Items.Add("定位本次改动文件", null, (_, _) => FocusFirstChangedFileInSelectedHistory());
        _historyListMenu.Items.Add("回退工作副本到此版本...", null, async (_, _) => await RunUpdateWorkingCopyToSelectedHistoryRevisionAsync());
        _historyListMenu.Items.Add(new ToolStripSeparator());
        _historyListMenu.Items.Add("深度搜索当前条件", null, async (_, _) => await RunDeepHistorySearchAsync());
        _historyListMenu.Items.Add("加载更多历史", null, async (_, _) => await LoadMoreRepositoryHistoryAsync());
        _historyListMenu.Items.Add(new ToolStripSeparator());
        _historyListMenu.Items.Add("复制版本号", null, (_, _) => CopySelectedHistoryRevision());
        _historyListMenu.Items.Add("复制提交摘要", null, (_, _) => CopySelectedHistorySummary());
        _historyListMenu.Items.Add("刷新历史", null, async (_, _) => await LoadRepositoryHistoryAsync());
        _historyListMenu.Opening += (_, args) =>
        {
            var log = GetSingleSelectedHistoryLog();
            var hasCommittedRevision = log != null && !log.IsUncommitted && log.Revision > 0;
            foreach (ToolStripItem item in _historyListMenu.Items)
            {
                item.Enabled = hasCommittedRevision ||
                    item.Text == "刷新历史" ||
                    item.Text == "加载更多历史" ||
                    item.Text == "深度搜索当前条件";
            }
        };
    }

    private void SelectHistoryItemForContextMenu(MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right)
        {
            return;
        }

        var item = _historyList.GetItemAt(args.X, args.Y);
        if (item == null)
        {
            return;
        }

        if (!item.Selected)
        {
            _historyList.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
        }
    }

    private SvnLogEntry? GetSingleSelectedHistoryLog()
    {
        return _historyList.SelectedItems.Count == 1 && _historyList.SelectedItems[0].Tag is SvnLogEntry log
            ? log
            : null;
    }

    private void CopySelectedHistoryRevision()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null || log.IsUncommitted)
        {
            return;
        }

        Clipboard.SetText(log.Revision.ToString());
        WriteOutput($"已复制版本号：r{log.Revision}");
    }

    private void CopySelectedHistorySummary()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null)
        {
            return;
        }

        var text = log.IsUncommitted
            ? $"本地未提交改动：{log.ChangedFiles.Count} 个文件"
            : $"r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}{log.Message}{Environment.NewLine}{Environment.NewLine}Changed files ({log.ChangedFiles.Count}){Environment.NewLine}" +
              string.Join(Environment.NewLine, log.ChangedFiles.Select(file => $"{file.Action} {file.DisplayText}"));
        Clipboard.SetText(text);
        WriteOutput(log.IsUncommitted ? "已复制本地改动摘要。" : $"已复制提交摘要：r{log.Revision}");
    }

    private async Task RunUpdateWorkingCopyToSelectedHistoryRevisionAsync()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null || log.IsUncommitted || log.Revision <= 0 || !ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var changes = await _svn.GetStatusAsync(workingCopy);
        if (!ConfirmUpdateToRevision(log, changes))
        {
            OperationLogger.Log("UpdateToRevisionCancelled", workingCopy, $"revision={log.Revision}; localChanges={changes.Count}");
            return;
        }

        OperationLogger.Log("UpdateToRevisionStart", workingCopy, $"revision={log.Revision}; localChanges={changes.Count}");
        var result = await RunSvnOperationAsync($"正在回退到 r{log.Revision}...", async () => await _svn.UpdateToRevisionAsync(workingCopy, log.Revision));
        OperationLogger.Log(result?.ExitCode == 0 ? "UpdateToRevisionSuccess" : "UpdateToRevisionFailed", workingCopy, $"revision={log.Revision}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
        await CheckRemoteChangesAsync(showUpToDateMessage: false);
    }

    private bool ConfirmUpdateToRevision(SvnLogEntry log, IReadOnlyList<SvnChange> changes)
    {
        var localChangeText = changes.Count == 0
            ? "当前没有本地改动。"
            : $"当前有 {changes.Count} 个本地改动；继续回退可能产生冲突，建议先提交、备份或清理。";
        var message =
            $"准备把整个工作副本更新到历史版本 r{log.Revision}。{Environment.NewLine}{Environment.NewLine}" +
            $"{log.LocalDateText}  {log.Author}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"{localChangeText}{Environment.NewLine}{Environment.NewLine}" +
            "这个操作不会修改 SVN 服务器历史，也不会自动提交；只是把你的本地工作副本切到该历史版本。确认继续？";
        var result = MessageBox.Show(
            message,
            "回退工作副本到历史版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.OK;
    }

    private async Task SelectSidebarRepositoryAsync(TreeNode? node)
    {
        if (_loadingRepository)
        {
            return;
        }

        if (node?.Tag is not RepositoryEntry repository)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        _repoUrlText.Text = repository.RepositoryUrl;
        _workingCopyText.Text = repository.WorkingCopyPath;
        _settings.Save();
        _latestRemoteLog = null;
        _remoteStatusLabel.Text = "远端：未检查";
        _remoteStatusLabel.ForeColor = SystemColors.ControlText;
        ClearStatusChanges();
        RefreshConflictPanel([]);
        UpdateStatusBadges(0, 0);
        _historyLoadedLimit = InitialHistoryLimit;
        _historyList.Items.Clear();
        UpdateHistoryBadge(0);
        if (!_historyDetailText.IsDisposed)
        {
            _historyDetailText.Clear();
        }
        LoadAllFiles();
        await LoadCurrentTabAsync();
    }

    private void OpenTreeFile(TreeNode node)
    {
        if (node.Tag is not FileTreeNodeInfo { IsFile: true } fileNode || string.IsNullOrWhiteSpace(fileNode.RelativePath))
        {
            node.Toggle();
            return;
        }

        var filePath = Path.Combine(_workingCopyText.Text.Trim(), fileNode.RelativePath);
        if (File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
    }

    private void BuildFileTreeMenu()
    {
        _fileTreeMenu.Items.Clear();
        _fileTreeMenu.Items.Add("打开文件", null, (_, _) => OpenSelectedTreeFile());
        _fileTreeMenu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedTreeFileFolder());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("查看差异", null, async (_, _) => await RunDiffAsync());
        _fileTreeMenu.Items.Add("和另一个表快速比对...", null, async (_, _) => await CompareSelectedTableWithAnotherAsync());
        _fileTreeMenu.Items.Add("当前本地 vs 远端 HEAD", null, async (_, _) => await CompareSelectedFileWithRemoteHeadAsync());
        _fileTreeMenu.Items.Add("查看冲突", null, async (_, _) => await RunConflictViewerAsync());
        _fileTreeMenu.Items.Add("内置表格三方合并", null, async (_, _) => await RunInternalSpreadsheetMergeAsync());
        _fileTreeMenu.Items.Add("跨库表格三方合并", null, async (_, _) => await RunCrossRepositorySpreadsheetMergeAsync());
        _fileTreeMenu.Items.Add("用分久必合对比/合并", null, async (_, _) => await RunExternalCompareOrMergeAsync());
        _fileTreeMenu.Items.Add("冲突处理流程", null, async (_, _) => await RunConflictWorkflowAsync());
        _fileTreeMenu.Items.Add("文件/文件夹历史", null, async (_, _) => await RunFileHistoryAsync());
        _fileTreeMenu.Items.Add("清除选择", null, (_, _) => ClearFileTreeSelection());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("锁定文件", null, async (_, _) => await LockSelectedFileAsync());
        _fileTreeMenu.Items.Add("解锁文件", null, async (_, _) => await UnlockSelectedFileAsync());
        _fileTreeMenu.Items.Add("查看锁信息", null, async (_, _) => await ShowSelectedFileLockInfoAsync());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("加入版本控制", null, async (_, _) => await AddSelectedTreeFileAsync());
        _fileTreeMenu.Items.Add("加入忽略清单", null, async (_, _) => await AddSelectedPathsToIgnoreAsync());
        _fileTreeMenu.Items.Add("移出忽略清单", null, async (_, _) => await RemoveSelectedPathsFromIgnoreAsync());
        _fileTreeMenu.Items.Add("收藏此目录", null, (_, _) => AddSelectedFileTreeFavorite());
        _fileTreeMenu.Items.Add("标记冲突已解决", null, async (_, _) => await ResolveSelectedTreeFileAsync());
        _fileTreeMenu.Opening += (_, args) =>
        {
            var relativePath = GetSelectedRelativePath();
            var hasFile = !string.IsNullOrWhiteSpace(relativePath);
            var hasTreePath = GetSelectedFileTreeHistoryPaths().Count > 0;
            foreach (ToolStripItem item in _fileTreeMenu.Items)
            {
                item.Enabled = item is ToolStripSeparator ||
                    item.Text is "打开所在目录" && _fileTree.SelectedNode?.Tag is FileTreeNodeInfo ||
                    item.Text is "文件/文件夹历史" && hasTreePath ||
                    item.Text is "打开文件" && hasFile ||
                    item.Text is "查看差异" && hasFile ||
                    item.Text is "和另一个表快速比对..." && hasFile ||
                    item.Text is "内置表格三方合并" && hasFile ||
                    item.Text is "跨库表格三方合并" ||
                    item.Text is "查看冲突" && hasFile ||
                    item.Text is "用分久必合对比/合并" && hasFile ||
                    item.Text is "冲突处理流程" && hasFile ||
                    item.Text is "清除选择" && _selectedFileTreePaths.Count > 0 ||
                    item.Text is "锁定文件" && hasFile ||
                    item.Text is "解锁文件" && hasFile ||
                    item.Text is "查看锁信息" && hasFile ||
                    item.Text is "加入版本控制" && hasFile ||
                    item.Text is "加入忽略清单" && hasTreePath ||
                    item.Text is "移出忽略清单" && hasTreePath ||
                    item.Text is "标记冲突已解决" && hasFile;
            }
        };
    }

    private void BuildHistoryChangedFilesMenu()
    {
        _historyChangedFilesMenu.Items.Clear();
        _historyChangedFilesMenu.Items.Add("打开文件", null, async (_, _) => await OpenSelectedHistoryChangedFileAsync());
        _historyChangedFilesMenu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedHistoryChangedFileFolder());
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("用分久必合对比", null, async (_, _) => await RunSelectedHistoryChangedFileExternalCompareAsync());
        _historyChangedFilesMenu.Items.Add("和另一个表快速比对...", null, async (_, _) => await CompareSelectedHistoryFileWithAnotherTableAsync());
        _historyChangedFilesMenu.Items.Add("文件历史", null, async (_, _) => await RunSelectedHistoryChangedFileHistoryAsync());
        _historyChangedFilesMenu.Items.Add("当前本地 vs 远端 HEAD", null, async (_, _) => await CompareSelectedHistoryFileWithRemoteHeadAsync());
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("用所选提交/范围三方合并到目标表...", null, async (_, _) => await RunSelectedCommitSpreadsheetMergeAsync());
        _historyChangedFilesMenu.Items.Add("将此文件更新到本次提交版本...", null, async (_, _) => await UpdateSelectedHistoryFileToRevisionAsync());
        _historyChangedFilesMenu.Items.Add("撤销本次提交对这个文件的改动...", null, async (_, _) => await ReverseMergeSelectedHistoryFileAsync());
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("复制路径", null, (_, _) => CopySelectedHistoryChangedFilePath());
        _historyChangedFilesMenu.Opening += (_, args) =>
        {
            var hasFile = _historyChangedFilesTree.SelectedNode?.Tag is ChangedFileEntry;
            var hasSingleCommittedRevision = hasFile && _selectedHistoryLog is { IsUncommitted: false, Revision: > 0 };
            var hasCommittedSelection = hasFile && GetSelectedCommittedHistoryLogs().Count > 0;
            foreach (ToolStripItem item in _historyChangedFilesMenu.Items)
            {
                if (item is ToolStripSeparator)
                {
                    item.Enabled = true;
                    continue;
                }

                item.Enabled = item.Text is "用所选提交/范围三方合并到目标表..."
                    ? hasCommittedSelection
                    : item.Text is "将此文件更新到本次提交版本..." or "撤销本次提交对这个文件的改动..."
                    ? hasSingleCommittedRevision
                    : hasFile;
            }
        };
    }

    private void OpenSelectedTreeFile()
    {
        if (_fileTree.SelectedNode != null)
        {
            OpenTreeFile(_fileTree.SelectedNode);
        }
    }

    private void OpenSelectedTreeFileFolder()
    {
        if (_fileTree.SelectedNode?.Tag is not FileTreeNodeInfo nodeInfo)
        {
            return;
        }

        var path = Path.Combine(_workingCopyText.Text.Trim(), nodeInfo.RelativePath);
        var folder = nodeInfo.IsFile ? Path.GetDirectoryName(path) : path;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
    }

    private void ClearFileTreeSelection()
    {
        _selectedFileTreePaths.Clear();
        _fileTreeSelectionAnchorPath = null;
        ApplyFileTreeSelectionStyles();
    }

    private async Task OpenSelectedHistoryChangedFileAsync()
    {
        if (_historyChangedFilesTree.SelectedNode != null)
        {
            await OpenHistoryChangedFileAsync(_historyChangedFilesTree.SelectedNode);
        }
    }

    private async Task OpenHistoryChangedFileAsync(TreeNode? node)
    {
        if (node?.Tag is not ChangedFileEntry file)
        {
            node?.Toggle();
            return;
        }

        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        SetBusy(true, "正在打开历史版本文件...");
        try
        {
            if (await OpenHistoryChangedFileVersionAsync(file))
            {
                return;
            }

            MessageBox.Show("没有找到可打开的文件版本。可能是历史版本中的已删除文件，或当前工作副本没有该路径。", "无法打开文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task<bool> OpenHistoryChangedFileVersionAsync(ChangedFileEntry file)
    {
        if (_selectedHistoryLogs.Count > 1)
        {
            var committedLogs = _selectedHistoryLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
            if (committedLogs.Count > 0)
            {
                var lastRevision = committedLogs.Last().Revision;
                if (await TryOpenRepositoryFileVersionAsync(file, lastRevision, $"r{lastRevision}"))
                {
                    return true;
                }

                var beforeFirstRevision = committedLogs.First().Revision - 1;
                if (beforeFirstRevision > 0 && await TryOpenRepositoryFileVersionAsync(file, beforeFirstRevision, $"r{beforeFirstRevision}"))
                {
                    WriteOutput($"范围结束版本没有该文件，已打开范围开始前版本：{file.DisplayText}");
                    return true;
                }

                return false;
            }
        }

        if (_selectedHistoryLog is { IsUncommitted: false } selectedLog)
        {
            var revision = file.Action == "D" ? selectedLog.Revision - 1 : selectedLog.Revision;
            if (revision <= 0)
            {
                return false;
            }

            return await TryOpenRepositoryFileVersionAsync(file, revision, $"r{revision}");
        }

        var filePath = GetHistoryChangedLocalPath(file);
        if (File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            WriteOutput($"已打开本地文件：{file.RelativePath}");
            return true;
        }

        return false;
    }

    private async Task<bool> TryOpenRepositoryFileVersionAsync(ChangedFileEntry file, long revision, string label)
    {
        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            return false;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var tempPath = CreateHistoryOpenTempPath(label, file.TreePath);
        try
        {
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, tempPath);
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            WriteOutput($"已打开历史版本 {label}：{file.DisplayText}");
            return true;
        }
        catch
        {
            TryDelete(tempPath);
            return false;
        }
    }

    private void OpenSelectedHistoryChangedFileFolder()
    {
        if (_historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var filePath = GetHistoryChangedLocalPath(file);
        var folder = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
    }

    private async Task RunSelectedHistoryChangedFileHistoryAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        SetBusy(true, "正在读取文件历史...");
        try
        {
            await ShowFileHistoryWindowAsync(file.RelativePath);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task UpdateSelectedHistoryFileToRevisionAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            _selectedHistoryLog is not { IsUncommitted: false, Revision: > 0 } log)
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var relativePath = GetHistoryChangedWorkingCopyRelativePath(file);
        var localChanges = await _svn.GetStatusAsync(workingCopy);
        if (!ConfirmUpdateHistoryFileToRevision(file, relativePath, log, localChanges))
        {
            OperationLogger.Log("UpdateFileToRevisionCancelled", workingCopy, $"revision={log.Revision}; file={relativePath}");
            return;
        }

        OperationLogger.Log("UpdateFileToRevisionStart", workingCopy, $"revision={log.Revision}; file={relativePath}");
        var result = await RunSvnOperationAsync($"正在把文件更新到 r{log.Revision}...", async () => await _svn.UpdatePathToRevisionAsync(workingCopy, relativePath, log.Revision));
        OperationLogger.Log(result?.ExitCode == 0 ? "UpdateFileToRevisionSuccess" : "UpdateFileToRevisionFailed", workingCopy, $"revision={log.Revision}; file={relativePath}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private async Task ReverseMergeSelectedHistoryFileAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            _selectedHistoryLog is not { IsUncommitted: false, Revision: > 0 } log)
        {
            return;
        }

        var workingCopy = _workingCopyText.Text.Trim();
        var relativePath = GetHistoryChangedWorkingCopyRelativePath(file);
        SetBusy(true, "正在预览撤销改动...");
        ProcessResult preview;
        try
        {
            preview = await _svn.ReverseMergeRevisionForPathAsync(workingCopy, relativePath, log.Revision, dryRun: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }
        finally
        {
            SetBusy(false, "就绪");
        }

        if (!ConfirmReverseMergeHistoryFile(file, relativePath, log, preview))
        {
            OperationLogger.Log("ReverseMergeFileCancelled", workingCopy, $"revision={log.Revision}; file={relativePath}");
            return;
        }

        OperationLogger.Log("ReverseMergeFileStart", workingCopy, $"revision={log.Revision}; file={relativePath}");
        var result = await RunSvnOperationAsync($"正在撤销 r{log.Revision} 对文件的改动...", async () => await _svn.ReverseMergeRevisionForPathAsync(workingCopy, relativePath, log.Revision, dryRun: false));
        OperationLogger.Log(result?.ExitCode == 0 ? "ReverseMergeFileSuccess" : "ReverseMergeFileFailed", workingCopy, $"revision={log.Revision}; file={relativePath}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private bool ConfirmUpdateHistoryFileToRevision(ChangedFileEntry file, string relativePath, SvnLogEntry log, IReadOnlyList<SvnChange> localChanges)
    {
        var localStatus = localChanges.FirstOrDefault(change =>
            string.Equals(NormalizeRelativePath(change.RelativePath), NormalizeRelativePath(relativePath), StringComparison.OrdinalIgnoreCase));
        var localWarning = localStatus == null
            ? "这个文件当前没有本地改动。"
            : $"这个文件当前有本地状态：{localStatus.DisplayStatus}。继续可能覆盖本地修改或产生冲突。";
        var message =
            $"准备只把这个文件更新到 r{log.Revision} 的版本。{Environment.NewLine}{Environment.NewLine}" +
            $"文件：{relativePath}{Environment.NewLine}" +
            $"提交：r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"影响范围：只影响这个文件，不会提交到服务器。{Environment.NewLine}" +
            $"{localWarning}{Environment.NewLine}{Environment.NewLine}" +
            $"SVN 路径：{file.DisplayText}{Environment.NewLine}{Environment.NewLine}" +
            "确认继续？";
        return MessageBox.Show(
            message,
            "文件回退到历史版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private bool ConfirmReverseMergeHistoryFile(ChangedFileEntry file, string relativePath, SvnLogEntry log, ProcessResult preview)
    {
        if (preview.ExitCode != 0)
        {
            MessageBox.Show(
                $"SVN dry-run 预览失败，已取消撤销操作。{Environment.NewLine}{Environment.NewLine}{preview.CombinedOutput}",
                "无法撤销本次提交",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var previewText = string.IsNullOrWhiteSpace(preview.CombinedOutput)
            ? "SVN dry-run 没有输出；通常表示没有可撤销改动，或该文件路径不适合直接撤销。"
            : preview.CombinedOutput.Trim();
        var message =
            $"准备撤销 r{log.Revision} 对这个文件造成的改动。{Environment.NewLine}{Environment.NewLine}" +
            $"文件：{relativePath}{Environment.NewLine}" +
            $"提交：r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"影响范围：只对这个文件执行 reverse merge，不会自动提交。{Environment.NewLine}" +
            $"SVN dry-run 预览：{Environment.NewLine}{previewText}{Environment.NewLine}{Environment.NewLine}" +
            "确认继续？";
        return MessageBox.Show(
            message,
            "撤销单次提交对文件的改动",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private void CopySelectedHistoryChangedFilePath()
    {
        if (_historyChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        Clipboard.SetText(string.IsNullOrWhiteSpace(file.RepositoryPath) ? file.RelativePath : file.RepositoryPath);
        WriteOutput($"已复制路径：{(string.IsNullOrWhiteSpace(file.RepositoryPath) ? file.RelativePath : file.RepositoryPath)}");
    }

    private string GetHistoryChangedLocalPath(ChangedFileEntry file)
    {
        return Path.Combine(_workingCopyText.Text.Trim(), GetHistoryChangedWorkingCopyRelativePath(file));
    }

    private string GetHistoryChangedWorkingCopyRelativePath(ChangedFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            return file.RelativePath;
        }

        var repositoryPath = file.RepositoryPath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        var workingCopyUrl = _svn.GetWorkingCopyInfo(_workingCopyText.Text.Trim()).Url;
        var workingCopyRepositoryPath = ExtractWorkingCopyRepositoryPath(workingCopyUrl);
        if (!string.IsNullOrWhiteSpace(workingCopyRepositoryPath) &&
            repositoryPath.StartsWith(workingCopyRepositoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return repositoryPath[(workingCopyRepositoryPath.Length + 1)..];
        }

        var candidates = new[]
        {
            file.RelativePath,
            repositoryPath,
            StripFirstPathSegment(repositoryPath),
            StripFirstPathSegment(StripFirstPathSegment(repositoryPath)),
        };
        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (File.Exists(Path.Combine(_workingCopyText.Text.Trim(), candidate)) ||
             Directory.Exists(Path.Combine(_workingCopyText.Text.Trim(), candidate)))) ?? file.RelativePath;
    }

    private static string ExtractWorkingCopyRepositoryPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
        for (var start = 0; start < segments.Count; start++)
        {
            var suffix = string.Join(Path.DirectorySeparatorChar, segments.Skip(start));
            if (suffix.StartsWith("trunk", StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("branch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("branches" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("tags" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return suffix;
            }
        }

        return "";
    }

    private static string StripFirstPathSegment(string path)
    {
        var trimmed = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var index = trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return index < 0 ? "" : trimmed[(index + 1)..];
    }

    private async Task AddSelectedTreeFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        await RunSvnOperationAsync("正在加入版本控制...", async () => await _svn.AddAsync(_workingCopyText.Text.Trim(), relativePath));
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ResolveSelectedTreeFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        await ResolveConflictPathAsync(relativePath);
    }

    private void SelectTab(string text)
    {
        foreach (TabPage page in _mainTabs.TabPages)
        {
            if (IsTab(page, text))
            {
                _mainTabs.SelectedTab = page;
                return;
            }
        }
    }

    private static bool IsTab(TabPage? page, string text)
    {
        return page != null &&
            string.Equals(GetBaseTabText(page.Text), text, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBaseTabText(string text)
    {
        var index = text.IndexOf('(');
        return (index > 0 ? text[..index] : text).Trim();
    }

    private async Task LoadCurrentTabAsync()
    {
        if (_loadingCurrentTab)
        {
            return;
        }

        _loadingCurrentTab = true;
        try
        {
            if (IsTab(_mainTabs.SelectedTab, "全部文件"))
            {
                LoadAllFiles();
            }
            else if (IsTab(_mainTabs.SelectedTab, "File Status"))
            {
                if (ValidateWorkingCopyPathForBackground())
                {
                    await RefreshStatusAsync();
                }
            }
            else if (IsTab(_mainTabs.SelectedTab, "冲突"))
            {
                if (ValidateWorkingCopyPathForBackground())
                {
                    await RefreshStatusAsync();
                }
            }
            else if (IsTab(_mainTabs.SelectedTab, "History"))
            {
                if (ValidateWorkingCopyPathForBackground())
                {
                    await LoadRepositoryHistoryAsync();
                }
            }
        }
        finally
        {
            _loadingCurrentTab = false;
        }
    }

    private bool ValidateRepositoryUrl()
    {
        if (!string.IsNullOrWhiteSpace(_repoUrlText.Text))
        {
            return true;
        }

        MessageBox.Show("请填写 SVN 地址。", "缺少 SVN 地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private bool ValidateWorkingCopyPath(bool allowMissing = false)
    {
        var path = _workingCopyText.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请先选择本地目录。", "缺少本地目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!allowMissing && !Directory.Exists(path))
        {
            MessageBox.Show("本地目录不存在。", "目录错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void SaveSettings()
    {
        _settings.UpsertRepository(_repoUrlText.Text.Trim(), _workingCopyText.Text.Trim());
        _settings.Save();
        RefreshRepositorySelector();
    }

    private void SaveCurrentRepository()
    {
        if (!ValidateWorkingCopyPath(allowMissing: true))
        {
            return;
        }

        SaveSettings();
        WriteOutput($"已保存本地库：{_workingCopyText.Text.Trim()}");
    }

    private void RemoveCurrentRepository()
    {
        var repository = GetRepositorySelectedForRemoval();
        if (repository == null)
        {
            MessageBox.Show("请先在左侧本地库或顶部下拉框里选中要移除的库。", "没有选中本地库", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"确定从工具里移除这个本地库吗？{Environment.NewLine}{Environment.NewLine}" +
            $"{repository.Name}{Environment.NewLine}" +
            $"{repository.WorkingCopyPath}{Environment.NewLine}{Environment.NewLine}" +
            "这只会从工具列表移除，不会删除磁盘上的文件。";
        if (MessageBox.Show(message, "移除本地库", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _settings.RemoveRepository(repository);

        _settings.Save();
        RefreshRepositorySelector();
        ApplyCurrentRepositoryToUi();
        WriteOutput($"已从本地库列表移除：{repository.Name}");
    }

    private RepositoryEntry? GetRepositorySelectedForRemoval()
    {
        if (_repositoryTree.SelectedNode?.Tag is RepositoryEntry treeRepository)
        {
            return treeRepository;
        }

        if (_repositorySelector.SelectedItem is RepositoryEntry selectorRepository)
        {
            return selectorRepository;
        }

        return _settings.GetCurrentRepository();
    }

    private void RefreshRepositorySelector()
    {
        _loadingRepository = true;
        try
        {
            _repositorySelector.Items.Clear();
            foreach (var repository in _settings.Repositories)
            {
                _repositorySelector.Items.Add(repository);
            }

            var selected = _settings.GetCurrentRepository();
            if (selected != null)
            {
                _repositorySelector.SelectedItem = selected;
            }
            else if (_repositorySelector.Items.Count > 0)
            {
                _repositorySelector.SelectedIndex = 0;
            }
        }
        finally
        {
            _loadingRepository = false;
        }

        RefreshRepositoryTree();
    }

    private void RefreshRepositoryTree()
    {
        if (_repositoryTree.IsDisposed)
        {
            return;
        }

        var wasLoading = _loadingRepository;
        _loadingRepository = true;
        try
        {
            _repositoryTree.BeginUpdate();
            _repositoryTree.Nodes.Clear();
            var repositoriesNode = new TreeNode("本地库")
            {
                ImageKey = "folder",
                SelectedImageKey = "folder",
            };
            _repositoryTree.Nodes.Add(repositoriesNode);
            foreach (var repository in _settings.Repositories)
            {
                var node = new TreeNode(repository.Name)
                {
                    Tag = repository,
                    ToolTipText = repository.WorkingCopyPath,
                    ImageKey = "repo",
                    SelectedImageKey = "repo",
                    ForeColor = repository.Id == _settings.CurrentRepositoryId
                        ? Color.FromArgb(0, 92, 175)
                        : SystemColors.WindowText,
                    NodeFont = repository.Id == _settings.CurrentRepositoryId
                        ? new Font(_repositoryTree.Font, FontStyle.Bold)
                        : _repositoryTree.Font,
                };
                repositoriesNode.Nodes.Add(node);
                if (repository.Id == _settings.CurrentRepositoryId)
                {
                    _repositoryTree.SelectedNode = node;
                    node.EnsureVisible();
                }
            }

            repositoriesNode.Expand();
        }
        finally
        {
            _repositoryTree.EndUpdate();
            _loadingRepository = wasLoading;
        }
    }

    private void SelectRepositoryFromList()
    {
        if (_loadingRepository || _repositorySelector.SelectedItem is not RepositoryEntry repository)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        _settings.Save();
        RefreshRepositoryTree();
        ApplyCurrentRepositoryToUi();
    }

    private void ApplyCurrentRepositoryToUi()
    {
        var selected = _settings.GetCurrentRepository();
        _repoUrlText.Text = selected?.RepositoryUrl ?? "";
        _workingCopyText.Text = selected?.WorkingCopyPath ?? "";
        ClearStatusChanges();
        RefreshConflictPanel([]);
        UpdateStatusBadges(0, 0);
        _historyLoadedLimit = InitialHistoryLimit;
        _historyList.Items.Clear();
        _historyRows.Clear();
        UpdateHistoryBadge(0);
            if (!_historyDetailText.IsDisposed)
            {
                _historyDetailText.Clear();
            }
        ClearHistoryChangedFiles();
        ClearHistoryDiffPanel();
        SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未检查", SystemColors.ControlText, "尚未读取当前工作副本版本。");
        _remoteStatusLabel.Text = "远端：未检查";
        _remoteStatusLabel.ForeColor = SystemColors.ControlText;
        UpdateHistorySearchControls();
        LoadAllFiles();
    }

    private void ClearStatusChanges()
    {
        _statusChangesAll.Clear();
        _checkedStatusPaths.Clear();
        _changesList.Items.Clear();
        UpdateStatusFilterSummary(0);
    }

    private void SetAllChecks(bool isChecked)
    {
        foreach (ListViewItem item in _changesList.Items)
        {
            if (item.Tag is SvnChange { CanCommit: true })
            {
                item.Checked = isChecked;
            }
        }

        UpdateStatusFilterSummary(_changesList.Items.Count);
    }

    private void SetBusy(bool busy, string text)
    {
        _statusLabel.Text = text;
        _checkoutButton.Enabled = !busy;
        _updateButton.Enabled = !busy;
        _statusButton.Enabled = !busy;
        _commitButton.Enabled = !busy;
        _diffButton.Enabled = !busy;
        _externalMergeButton.Enabled = !busy;
        _conflictWorkflowButton.Enabled = !busy;
        _historyButton.Enabled = !busy;
        _historyDeepSearchButton.Enabled = !busy;
        _historyLoadMoreButton.Enabled = !busy;
        _historyClearSearchButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_historySearchText.Text);
        _statusCommitVisibleOnlyCheck.Enabled = !busy;
        UseWaitCursor = busy;
        if (!busy)
        {
            UpdateHistorySearchControls();
        }
    }

    private void WriteOutput(string output)
    {
        _outputText.Text = string.IsNullOrWhiteSpace(output) ? "命令没有输出。" : output.Trim();
    }

    private void ShowError(Exception ex)
    {
        WriteOutput(ex.ToString());
        MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowSelectedHistoryDetail()
    {
        var selectedLogs = _historyList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnLogEntry)
            .Where(log => log != null)
            .Cast<SvnLogEntry>()
            .OrderBy(log => log.Revision)
            .ToList();

        if (selectedLogs.Count == 0)
        {
            _selectedHistoryLog = null;
            _selectedHistoryLogs = [];
            ClearHistoryChangedFiles();
            ShowHistorySummary("");
            return;
        }

        if (selectedLogs.Count > 1)
        {
            _selectedHistoryLog = null;
            _selectedHistoryLogs = selectedLogs;
            ShowSelectedHistoryRangeDetail(selectedLogs);
            return;
        }

        var log = selectedLogs[0];
        _selectedHistoryLog = log;
        _selectedHistoryLogs = [log];
        PopulateHistoryChangedFiles(log);
        if (log.IsUncommitted)
        {
            ShowHistorySummary(HistorySummaryData.FromLog(log));
            return;
        }

        ShowHistorySummary(HistorySummaryData.FromLog(log));
    }

    private void ShowSelectedHistoryRangeDetail(IReadOnlyList<SvnLogEntry> logs)
    {
        var committedLogs = logs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
        if (committedLogs.Count == 0)
        {
            ShowHistorySummary("当前选择只包含未提交改动，请单选 Uncommitted changes 查看。");
            ClearHistoryChangedFiles();
            return;
        }

        var changedFiles = BuildRangeChangedFiles(committedLogs);
        PopulateHistoryChangedFiles($"Selected commits ({committedLogs.Count}) - Changed files ({changedFiles.Count})", changedFiles);
        var first = committedLogs.First();
        var last = committedLogs.Last();
        ShowHistorySummary(HistorySummaryData.FromRange(committedLogs, changedFiles));
    }

    private void ShowHistorySummary(string text)
    {
        ShowHistorySummary(HistorySummaryData.Plain(text));
    }

    private void ShowHistorySummary(HistorySummaryData data)
    {
        _historyDiffHeaderLabel.Text = "提交详情";
        _historyDiffMaximizeButton.Visible = false;

        CancelHistoryDiffPreview();
        ClearHistoryDiffPanel();
        var summaryControl = new HistorySummaryPanel(data);
        summaryControl.FileClicked += async summaryFile =>
        {
            var entry = _historyChangedFilesAll.FirstOrDefault(f => f.TreePath == summaryFile.TreePath);
            if (entry != null)
            {
                var node = FindNodeByTag(_historyChangedFilesTree.Nodes, entry);
                if (node != null)
                {
                    _historyChangedFilesTree.SelectedNode = node;
                }
                var tempNode = new TreeNode { Tag = entry };
                await OpenHistoryChangedFileAsync(node ?? tempNode);
            }
        };
        _historyDiffPanel.Controls.Add(summaryControl);
    }

    private TreeNode? FindNodeByTag(TreeNodeCollection nodes, object tag)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag == tag) return node;
            var found = FindNodeByTag(node.Nodes, tag);
            if (found != null) return found;
        }
        return null;
    }

    private void PopulateHistoryChangedFiles(SvnLogEntry log)
    {
        PopulateHistoryChangedFiles($"Changed files ({log.ChangedFiles.Count})", log.ChangedFiles);
    }

    private void ClearHistoryChangedFiles()
    {
        _historyChangedFilesAll = [];
        _historyChangedFilesRootText = "Changed files";
        _historyChangedFilesTree.Nodes.Clear();
    }

    private void PopulateHistoryChangedFiles(string rootText, IReadOnlyList<ChangedFileEntry> files)
    {
        _historyChangedFilesRootText = rootText;
        _historyChangedFilesAll = files
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyHistoryChangedFilesFilter();
    }

    private void ApplyHistoryChangedFilesFilter()
    {
        _historyChangedFilesTree.BeginUpdate();
        _historyChangedFilesTree.Nodes.Clear();
        var files = ChangedFilesFilter.Apply(
            _historyChangedFilesAll,
            _historyChangedFilesSearchText.Text,
            ChangedFilesFilter.GetMode(_historyChangedFilesFilterCombo));
        var rootText = files.Count == _historyChangedFilesAll.Count
            ? _historyChangedFilesRootText
            : $"{_historyChangedFilesRootText} - 显示 {files.Count}/{_historyChangedFilesAll.Count}";
        var root = new TreeNode(rootText)
        {
            ImageKey = "folder",
            SelectedImageKey = "folder",
        };
        _historyChangedFilesTree.Nodes.Add(root);
        foreach (var file in files)
        {
            AddChangedFileNode(root, file);
        }

        if (files.Count <= 50)
        {
            _historyChangedFilesTree.ExpandAll();
            if (_historyChangedFilesTree.Nodes.Count > 0)
            {
                _historyChangedFilesTree.Nodes[0].EnsureVisible();
            }
        }
        else
        {
            root.Expand();
        }
        _historyChangedFilesTree.EndUpdate();
    }

    private static IReadOnlyList<ChangedFileEntry> BuildRangeChangedFiles(IReadOnlyList<SvnLogEntry> logs)
    {
        return logs
            .SelectMany(log => log.ChangedFiles)
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .GroupBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = group.ToList();
                var action = files.All(file => file.Action == "A")
                    ? "A"
                    : files.All(file => file.Action == "D")
                        ? "D"
                        : "M";
                var repositoryPath = files.LastOrDefault(file => !string.IsNullOrWhiteSpace(file.RepositoryPath))?.RepositoryPath ?? "";
                var relativePath = files.LastOrDefault(file => !string.IsNullOrWhiteSpace(file.RelativePath))?.RelativePath ?? group.Key;
                return new ChangedFileEntry(action, repositoryPath, relativePath);
            })
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddChangedFileNode(TreeNode root, ChangedFileEntry file)
    {
        var path = file.TreePath;
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var isFile = index == parts.Length - 1;
            var text = isFile ? $"{file.Action} {parts[index]}" : parts[index];
            var existing = current.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(node.Text, text, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new TreeNode(text)
                {
                    Tag = isFile ? file : null,
                    ToolTipText = file.DisplayText,
                    ImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                    SelectedImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                };
                if (isFile)
                {
                    existing.ForeColor = file.Action switch
                    {
                        "A" => Color.FromArgb(38, 128, 72),
                        "D" => Color.FromArgb(170, 67, 67),
                        "M" => Color.FromArgb(166, 103, 34),
                        "C" => Color.FromArgb(144, 65, 170),
                        "R" => Color.FromArgb(109, 85, 184),
                        _ => SystemColors.WindowText,
                    };
                }
                else
                {
                    existing.ForeColor = Color.FromArgb(55, 65, 81);
                    existing.NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                }
                current.Nodes.Add(existing);
            }

            current = existing;
        }
        root.TreeView?.Sort();
    }

    private static string ChangedFileActionImageKey(string action)
    {
        return action switch
        {
            "A" => "action-added",
            "M" => "action-modified",
            "D" => "action-deleted",
            "C" => "action-conflicted",
            "R" => "action-replaced",
            _ => "action-unknown",
        };
    }

    private async Task ShowSelectedHistoryFileDiffAsync(TreeNode? node)
    {
        if (node?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var previewCts = BeginHistoryDiffPreview();
        var token = previewCts.Token;
        var extension = GetComparableExtension(file.TreePath);
        var oldTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_OLD_{Guid.NewGuid():N}{extension}");
        var newTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_NEW_{Guid.NewGuid():N}{extension}");
        ShowHistoryDiffLoading(file.TreePath, "正在准备文件版本...");
        SetBusy(true, "正在读取文件差异...");
        try
        {
            var workingCopy = _workingCopyText.Text.Trim();
            var title = "";
            var cacheKey = "";
            if (_selectedHistoryLogs.Count > 1)
            {
                var committedLogs = _selectedHistoryLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
                if (committedLogs.Count == 0)
                {
                    ShowHistorySummary("多选范围不支持只选择未提交改动。");
                    return;
                }

                var firstRevision = committedLogs.First().Revision;
                var lastRevision = committedLogs.Last().Revision;
                title = BuildDiffTitle(file.TreePath, $"r{firstRevision - 1}", $"r{lastRevision}", "选中提交范围");
                cacheKey = BuildHistoryDiffCacheKey("range", file, firstRevision - 1, lastRevision);
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareRangeDiffFilesAsync(_svn, workingCopy, firstRevision, lastRevision, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
                return;
            }

            if (_selectedHistoryLog == null)
            {
                return;
            }

            if (_selectedHistoryLog.IsUncommitted && file.Action == "C")
            {
                var conflict = ConflictFileSet.Find(workingCopy, file.RelativePath);
                if (conflict?.ServerPath != null)
                {
                    title = BuildDiffTitle(file.TreePath, "我的版本", "服务器版本", "SVN 冲突");
                    cacheKey = BuildHistoryDiffCacheKey("conflict", file, 0, 0, FileVersionStamp(conflict.MinePath), FileVersionStamp(conflict.ServerPath));
                    if (!TryRenderCachedHistoryDiff(title, cacheKey, token))
                    {
                        await ShowDiffPreviewAsync(title, conflict.MinePath, conflict.ServerPath, cacheKey, token);
                    }

                    return;
                }
            }

            if (_selectedHistoryLog.IsUncommitted)
            {
                var localPath = Path.Combine(workingCopy, file.RelativePath);
                title = BuildDiffTitle(file.TreePath, "SVN BASE", "本地工作副本", "未提交改动");
                cacheKey = BuildHistoryDiffCacheKey("uncommitted", file, 0, 0, FileVersionStamp(localPath));
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareUncommittedDiffFilesAsync(workingCopy, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
            }
            else
            {
                title = BuildDiffTitle(file.TreePath, $"r{_selectedHistoryLog.Revision - 1}", $"r{_selectedHistoryLog.Revision}", "单次提交");
                cacheKey = BuildHistoryDiffCacheKey("commit", file, _selectedHistoryLog.Revision - 1, _selectedHistoryLog.Revision);
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareCommittedDiffFilesAsync(_svn, workingCopy, _selectedHistoryLog.Revision, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowHistorySummary(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
            if (IsCurrentHistoryDiffPreview(previewCts))
            {
                SetBusy(false, "就绪");
            }
        }
    }

    private async Task PrepareUncommittedDiffFilesAsync(string workingCopy, ChangedFileEntry file, string oldTemp, string newTemp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localPath = Path.Combine(workingCopy, file.RelativePath);
        if (file.Action is "A" or "?")
        {
            File.WriteAllText(oldTemp, "");
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(localPath, newTemp, true);
            return;
        }

        if (file.Action is "D" or "!")
        {
            await _svn.WriteBaseFileAsync(workingCopy, file.RelativePath, oldTemp, cancellationToken);
            File.WriteAllText(newTemp, "");
            return;
        }

        await _svn.WriteBaseFileAsync(workingCopy, file.RelativePath, oldTemp, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(localPath, newTemp, true);
    }

    private CancellationTokenSource BeginHistoryDiffPreview()
    {
        CancelHistoryDiffPreview();
        _historyDiffPreviewCts = new CancellationTokenSource();
        return _historyDiffPreviewCts;
    }

    private void CancelHistoryDiffPreview()
    {
        try
        {
            _historyDiffPreviewCts?.Cancel();
        }
        catch
        {
        }
    }

    private void ClearHistoryDiffPreviewCache()
    {
        CancelHistoryDiffPreview();
        _historyDiffPreviewCache.Clear();
    }

    private bool IsCurrentHistoryDiffPreview(CancellationTokenSource previewCts)
    {
        return ReferenceEquals(_historyDiffPreviewCts, previewCts);
    }

    private bool TryRenderCachedHistoryDiff(string title, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_historyDiffPreviewCache.TryGetValue(cacheKey, out var data))
        {
            return false;
        }

        _historyDiffHeaderLabel.Text = "Diff preview";
        _historyDiffMaximizeButton.Visible = true;
        RenderDiffPreviewInPanel(_historyDiffPanel, null, title + "    [缓存]", data);
        return true;
    }

    private async Task ShowDiffPreviewAsync(string title, string oldFilePath, string newFilePath, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ShowHistoryDiffLoading(title, "正在计算差异...");
        var data = await Task.Run(() => CreateDiffPreviewData(oldFilePath, newFilePath), token);
        token.ThrowIfCancellationRequested();
        AddHistoryDiffPreviewCache(cacheKey, data);
        _historyDiffHeaderLabel.Text = "Diff preview";
        _historyDiffMaximizeButton.Visible = true;
        RenderDiffPreviewInPanel(_historyDiffPanel, null, title, data);
    }

    private void AddHistoryDiffPreviewCache(string cacheKey, DiffPreviewData data)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        if (_historyDiffPreviewCache.Count >= MaxDiffPreviewCacheEntries &&
            !_historyDiffPreviewCache.ContainsKey(cacheKey))
        {
            _historyDiffPreviewCache.Remove(_historyDiffPreviewCache.Keys.First());
        }

        _historyDiffPreviewCache[cacheKey] = data;
    }

    private void ShowHistoryDiffLoading(string title, string message)
    {
        _historyDiffHeaderLabel.Text = "Diff preview";
        _historyDiffMaximizeButton.Visible = true;

        ClearHistoryDiffPanel();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(85, 95, 105),
        }, 0, 1);
        _historyDiffPanel.Controls.Add(root);
    }

    private string BuildHistoryDiffCacheKey(string scope, ChangedFileEntry file, long oldRevision, long newRevision, params string[] stamps)
    {
        var workingCopy = _workingCopyText.Text.Trim();
        var repository = _repoUrlText.Text.Trim();
        return string.Join("|",
            "history",
            scope,
            repository,
            workingCopy,
            oldRevision.ToString(),
            newRevision.ToString(),
            file.Action,
            file.RepositoryPath,
            file.RelativePath,
            file.TreePath,
            string.Join(";", stamps));
    }

    private static string FileVersionStamp(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"{path}:missing";
            }

            var info = new FileInfo(path);
            return $"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"{path}:unknown";
        }
    }

    internal static async Task PrepareCommittedDiffFilesAsync(SvnClient svn, string workingCopy, long revision, ChangedFileEntry file, string oldTemp, string newTemp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Action == "A")
        {
            File.WriteAllText(oldTemp, "");
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, newTemp, cancellationToken);
            return;
        }

        if (file.Action == "D")
        {
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision - 1, oldTemp, cancellationToken);
            File.WriteAllText(newTemp, "");
            return;
        }

        await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision - 1, oldTemp, cancellationToken);
        await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, newTemp, cancellationToken);
    }

    internal static async Task PrepareRangeDiffFilesAsync(
        SvnClient svn,
        string workingCopy,
        long firstRevision,
        long lastRevision,
        ChangedFileEntry file,
        string oldTemp,
        string newTemp,
        CancellationToken cancellationToken = default)
    {
        await TryWriteRepositoryFileAtRevisionAsync(svn, workingCopy, file.RepositoryPath, firstRevision - 1, oldTemp, cancellationToken);
        await TryWriteRepositoryFileAtRevisionAsync(svn, workingCopy, file.RepositoryPath, lastRevision, newTemp, cancellationToken);
    }

    private static async Task TryWriteRepositoryFileAtRevisionAsync(SvnClient svn, string workingCopy, string repositoryPath, long revision, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, repositoryPath, revision, outputPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            File.WriteAllText(outputPath, "");
        }
    }

    internal static void ShowDiffPreviewInPanel(Panel panel, Label? headerLabel, string title, string oldFilePath, string newFilePath)
    {
        RenderDiffPreviewInPanel(panel, headerLabel, title, CreateDiffPreviewData(oldFilePath, newFilePath));
    }

    internal static DiffPreviewData CreateDiffPreviewData(string oldFilePath, string newFilePath)
    {
        if (DiffFileKindDetector.IsSpreadsheet(oldFilePath) && DiffFileKindDetector.IsSpreadsheet(newFilePath))
        {
            return DiffPreviewData.FromExcel(ExcelDiffService.Compare(oldFilePath, newFilePath));
        }

        return DiffPreviewData.FromText(TextDiffService.CreatePreview(oldFilePath, newFilePath));
    }

    internal static void RenderDiffPreviewInPanel(Panel panel, Label? headerLabel, string title, DiffPreviewData data)
    {
        ClearControlsDisposing(panel);
        var header = headerLabel ?? new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        };
        header.Text = title;
        panel.Controls.Add(header);

        var diffControl = data.CreateView();
        diffControl.Dock = DockStyle.Fill;
        panel.Controls.Add(diffControl);
        diffControl.BringToFront();
    }

    private void ClearHistoryDiffPanel()
    {
        ClearControlsDisposing(_historyDiffPanel, _historyDetailText);
    }

    internal static void ClearControlsDisposing(Control parent, params Control[] keepAlive)
    {
        var keep = keepAlive
            .Where(control => control != null)
            .ToHashSet();
        var oldControls = parent.Controls.Cast<Control>().ToList();
        parent.Controls.Clear();
        foreach (var control in oldControls)
        {
            if (keep.Contains(control))
            {
                continue;
            }

            control.Dispose();
        }
    }

    private void ShowDiffPreview(string title, string oldFilePath, string newFilePath)
    {
        ShowDiffPreviewInPanel(_historyDiffPanel, null, title, oldFilePath, newFilePath);
    }

    private static string BuildDiffTitle(string path, string oldLabel, string newLabel, string scope)
    {
        return $"{path}{Environment.NewLine}{scope}: {oldLabel} -> {newLabel}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp cleanup failure should not block normal use.
        }
    }
}

internal sealed class DiffPreviewData
{
    private DiffPreviewData(IReadOnlyList<ExcelCellDifference>? excelDifferences, TextDiffContent? textContent)
    {
        ExcelDifferences = excelDifferences;
        TextContent = textContent;
    }

    public IReadOnlyList<ExcelCellDifference>? ExcelDifferences { get; }
    public TextDiffContent? TextContent { get; }
    public IReadOnlyList<TextDiffRow>? TextDifferences => TextContent?.Differences;

    public string Summary
    {
        get
        {
            if (ExcelDifferences != null)
            {
                return ExcelDifferences.Count == 0 ? "没有发现单元格差异" : $"发现 {ExcelDifferences.Count} 个单元格差异";
            }

            var rows = TextContent?.Differences ?? [];
            return rows.Count == 0 ? "没有发现文本差异" : $"发现 {rows.Count} 行文本差异";
        }
    }

    public static DiffPreviewData FromExcel(IReadOnlyList<ExcelCellDifference> differences)
    {
        return new DiffPreviewData(differences.ToList(), null);
    }

    public static DiffPreviewData FromText(TextDiffContent content)
    {
        return new DiffPreviewData(null, content with { Differences = content.Differences.ToList() });
    }

    public static DiffPreviewData FromTextRows(IReadOnlyList<TextDiffRow> differences)
    {
        return new DiffPreviewData(
            null,
            new TextDiffContent("", "", "plaintext", "旧版本", "新版本", differences.ToList()));
    }

    public Control CreateView()
    {
        return ExcelDifferences != null
            ? ExcelDiffForm.CreateExcelDiffView(ExcelDifferences)
            : TextContent != null ? TextDiffForm.CreateTextDiffView(TextContent) : TextDiffForm.CreateTextDiffView(TextDifferences ?? []);
    }
}

internal enum GitUpdateState
{
    UpToDate,
    UpdateAvailable,
    RemoteUnavailable,
}

internal enum ReleaseUpdateState
{
    UpToDate,
    UpdateAvailable,
    Unavailable,
}

internal sealed record ReleaseUpdateStatus(
    ReleaseUpdateState State,
    string CurrentVersion,
    string LatestTag,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseUrl,
    string AssetName,
    string AssetDownloadUrl,
    string Message);

internal static class AppInfo
{
    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

internal sealed record GitUpdateStatus(GitUpdateState State, string LocalSha, string RemoteSha, string Message)
{
    public string LocalShortSha => ShortSha(LocalSha);
    public string RemoteShortSha => ShortSha(RemoteSha);

    private static string ShortSha(string sha)
    {
        return string.IsNullOrWhiteSpace(sha) ? "未知" : sha[..Math.Min(7, sha.Length)];
    }
}

internal static class ReleaseUpdateChecker
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/HoodHou/External-git-DG-SVNManager/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<ReleaseUpdateStatus> CheckLatestAsync(Version currentVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"GitHub Release 检查失败：HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var tag = ReadString(root, "tag_name");
            var releaseName = ReadString(root, "name");
            var notes = ReadString(root, "body");
            var url = ReadString(root, "html_url");
            var latestVersion = ParseVersion(tag);
            var asset = FindWindowsZipAsset(root);
            if (string.IsNullOrWhiteSpace(tag) || latestVersion == null)
            {
                return Unavailable("GitHub Release 没有有效版本号。");
            }

            var state = latestVersion > NormalizeVersion(currentVersion)
                ? ReleaseUpdateState.UpdateAvailable
                : ReleaseUpdateState.UpToDate;
            return new ReleaseUpdateStatus(
                state,
                AppInfo.VersionText,
                tag,
                releaseName,
                notes,
                url,
                asset.Name,
                asset.DownloadUrl,
                "");
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    public static async Task<string> DownloadAssetAsync(string assetDownloadUrl, string tag)
    {
        if (string.IsNullOrWhiteSpace(assetDownloadUrl))
        {
            throw new InvalidOperationException("GitHub Release 没有可下载的 Windows zip。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "DreamSVNManagerUpdate");
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, $"DreamSVNManager-{tag}-{Guid.NewGuid():N}.zip");
        using var response = await Http.GetAsync(assetDownloadUrl);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(zipPath);
        await input.CopyToAsync(output);
        return zipPath;
    }

    private static ReleaseUpdateStatus Unavailable(string message)
    {
        return new ReleaseUpdateStatus(
            ReleaseUpdateState.Unavailable,
            AppInfo.VersionText,
            "",
            "",
            "",
            "https://github.com/HoodHou/External-git-DG-SVNManager/releases",
            "",
            "",
            message);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DreamSVNManager/" + AppInfo.VersionText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static (string Name, string DownloadUrl) FindWindowsZipAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return ("", "");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            {
                return (name, ReadString(asset, "browser_download_url"));
            }
        }

        var firstZip = assets.EnumerateArray()
            .Select(asset => (Name: ReadString(asset, "name"), DownloadUrl: ReadString(asset, "browser_download_url")))
            .FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return firstZip;
    }

    private static Version? ParseVersion(string tag)
    {
        var normalized = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? NormalizeVersion(version) : null;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            version.Build < 0 ? 0 : version.Build);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }
}

internal static class GitUpdateChecker
{
    public static string? FindRepositoryRoot(string startPath)
    {
        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : Directory.GetParent(startPath);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static async Task<GitUpdateStatus> CheckAsync(string repositoryRoot)
    {
        var local = await RunGitAsync(repositoryRoot, "rev-parse", "HEAD");
        if (local.ExitCode != 0 || string.IsNullOrWhiteSpace(local.StandardOutput))
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, "", "", "无法读取本地 Git HEAD：" + local.CombinedOutput);
        }

        var remote = await RunGitAsync(repositoryRoot, "ls-remote", "origin", "refs/heads/main");
        if (remote.ExitCode != 0)
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, local.StandardOutput.Trim(), "", "无法连接 GitHub origin/main：" + remote.CombinedOutput);
        }

        var remoteSha = ParseLsRemoteSha(remote.StandardOutput);
        if (string.IsNullOrWhiteSpace(remoteSha))
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, local.StandardOutput.Trim(), "", "GitHub origin/main 暂无可比较版本。");
        }

        var localSha = local.StandardOutput.Trim();
        return string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase)
            ? new GitUpdateStatus(GitUpdateState.UpToDate, localSha, remoteSha, "")
            : new GitUpdateStatus(GitUpdateState.UpdateAvailable, localSha, remoteSha, "");
    }

    public static async Task<string> GetRemoteUrlAsync(string repositoryRoot)
    {
        var remote = await RunGitAsync(repositoryRoot, "remote", "get-url", "origin");
        return remote.ExitCode == 0 ? remote.StandardOutput.Trim() : "";
    }

    public static async Task<string> GetUpdateLogAsync(string repositoryRoot, int limit)
    {
        var fetch = await RunGitAsync(repositoryRoot, "fetch", "origin", "main", "--quiet");
        if (fetch.ExitCode != 0)
        {
            return "无法读取 GitHub 更新内容：" + fetch.CombinedOutput;
        }

        var log = await RunGitAsync(repositoryRoot, "log", $"--max-count={limit}", "--pretty=format:%h  %s", "HEAD..FETCH_HEAD");
        if (log.ExitCode != 0)
        {
            return "无法生成更新内容：" + log.CombinedOutput;
        }

        return string.IsNullOrWhiteSpace(log.StandardOutput)
            ? "当前没有检测到未拉取的提交。"
            : log.StandardOutput.Trim();
    }

    public static Task<ProcessResult> PullAsync(string repositoryRoot)
    {
        return RunGitAsync(repositoryRoot, "pull", "--ff-only", "origin", "main");
    }

    private static string ParseLsRemoteSha(string output)
    {
        var firstLine = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "";
        }

        return firstLine.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static async Task<ProcessResult> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 git 命令。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

internal sealed class ToolUpdateForm : Form
{
    private readonly string? _localDirectory;
    private readonly string _remoteUrl;
    public bool RunUpdateRequested { get; private set; }

    private ToolUpdateForm(
        string titleText,
        string infoText,
        string updateLog,
        string updateButtonText,
        bool updateEnabled,
        string? localDirectory,
        string remoteUrl)
    {
        _localDirectory = localDirectory;
        _remoteUrl = remoteUrl;
        Text = "工具更新";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 520;
        MinimumSize = new Size(620, 420);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = titleText,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        root.Controls.Add(title, 0, 0);

        var info = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            Text = infoText,
        };
        root.Controls.Add(info, 0, 1);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "更新内容",
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);

        root.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = string.IsNullOrWhiteSpace(updateLog) ? "暂无更新内容。" : updateLog,
        }, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        var closeButton = new Button { Text = "关闭", Width = 90, DialogResult = DialogResult.Cancel };
        var updateButton = new Button { Text = updateButtonText, Width = 120, Enabled = updateEnabled };
        var openLocalButton = new Button { Text = "打开本地目录", Width = 110, Enabled = !string.IsNullOrWhiteSpace(localDirectory) };
        var openGitHubButton = new Button { Text = "打开 GitHub", Width = 110, Enabled = !string.IsNullOrWhiteSpace(remoteUrl) };

        updateButton.Click += (_, _) =>
        {
            RunUpdateRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        openLocalButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_localDirectory) && Directory.Exists(_localDirectory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _localDirectory) { UseShellExecute = true });
            }
        };
        openGitHubButton.Click += (_, _) =>
        {
            var url = NormalizeRemoteUrl(_remoteUrl);
            if (!string.IsNullOrWhiteSpace(url))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        };

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(updateButton);
        buttons.Controls.Add(openLocalButton);
        buttons.Controls.Add(openGitHubButton);
        root.Controls.Add(buttons, 0, 4);

        AcceptButton = updateButton;
        CancelButton = closeButton;
        Controls.Add(root);
    }

    public static ToolUpdateForm FromRelease(ReleaseUpdateStatus status)
    {
        var title = status.State switch
        {
            ReleaseUpdateState.UpdateAvailable => "检测到工具新版本",
            ReleaseUpdateState.UpToDate => "工具已是最新版本",
            ReleaseUpdateState.Unavailable => "无法检查工具更新",
            _ => "工具更新状态未知",
        };
        var info =
            $"当前版本：{status.CurrentVersion}{Environment.NewLine}" +
            $"GitHub 最新：{(string.IsNullOrWhiteSpace(status.LatestTag) ? "未知" : status.LatestTag)}{Environment.NewLine}" +
            $"下载文件：{(string.IsNullOrWhiteSpace(status.AssetName) ? "未找到" : status.AssetName)}{Environment.NewLine}" +
            $"安装目录：{AppContext.BaseDirectory}";
        var notes = status.State == ReleaseUpdateState.Unavailable
            ? status.Message
            : string.IsNullOrWhiteSpace(status.ReleaseNotes) ? "这个版本没有填写更新说明。" : status.ReleaseNotes;
        return new ToolUpdateForm(
            title,
            info,
            notes,
            "下载并更新",
            status.State == ReleaseUpdateState.UpdateAvailable && !string.IsNullOrWhiteSpace(status.AssetDownloadUrl),
            AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(status.ReleaseUrl) ? "https://github.com/HoodHou/External-git-DG-SVNManager/releases" : status.ReleaseUrl);
    }

    public static ToolUpdateForm FromGit(GitUpdateStatus? status, string? repositoryRoot, string remoteUrl, string updateLog)
    {
        return new ToolUpdateForm(
            BuildGitTitle(status, repositoryRoot),
            BuildGitInfo(status, repositoryRoot, remoteUrl),
            string.IsNullOrWhiteSpace(updateLog) ? "暂无更新内容。" : updateLog,
            "执行更新命令",
            !string.IsNullOrWhiteSpace(repositoryRoot),
            repositoryRoot,
            remoteUrl);
    }

    private static string BuildGitTitle(GitUpdateStatus? status, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return "当前程序没有找到 Git 仓库信息";
        }

        return status?.State switch
        {
            GitUpdateState.UpdateAvailable => "检测到工具新版本",
            GitUpdateState.UpToDate => "工具已是最新版本",
            GitUpdateState.RemoteUnavailable => "无法连接 GitHub 远端",
            _ => "工具更新状态未知",
        };
    }

    private static string BuildGitInfo(GitUpdateStatus? status, string? repositoryRoot, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return "当前运行目录向上没有找到 .git，无法比较 GitHub 版本。";
        }

        return
            $"本地目录：{repositoryRoot}{Environment.NewLine}" +
            $"GitHub：{remoteUrl}{Environment.NewLine}" +
            $"当前版本：{status?.LocalShortSha ?? "未知"}{Environment.NewLine}" +
            $"GitHub 最新：{status?.RemoteShortSha ?? "未知"}";
    }

    private static string NormalizeRemoteUrl(string remoteUrl)
    {
        if (remoteUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var path = remoteUrl["git@github.com:".Length..];
            return "https://github.com/" + (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path);
        }

        return remoteUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? remoteUrl[..^4]
            : remoteUrl;
    }
}

internal sealed class SvnClient
{
    public Task<ProcessResult> VersionAsync()
    {
        return RunTextAsync(null, "--version", "--quiet");
    }

    public Task<ProcessResult> CheckoutAsync(string repositoryUrl, string workingCopyPath)
    {
        return RunAsync(null, "checkout", repositoryUrl, workingCopyPath);
    }

    public Task<ProcessResult> UpdateAsync(string workingCopyPath)
    {
        return RunAsync(workingCopyPath, "update");
    }

    public async Task<ProcessResult> CleanupAsync(string workingCopyPath, CleanupOptions options)
    {
        var output = new StringBuilder();
        var exitCode = 0;

        if (options.CleanWorkingCopyStatus || options.BreakWriteLocks || options.FixTimeStamps)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals);
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.VacuumPristineCopies)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--vacuum-pristines");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.DeleteUnversioned)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--remove-unversioned");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.DeleteIgnored)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--remove-ignored");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.RevertAllRecursive)
        {
            var result = await RunAsync(workingCopyPath, "revert", "-R", ".");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.RefreshShellOverlays)
        {
            output.AppendLine("刷新资源管理器图标覆盖是 TortoiseSVN 的外壳刷新项；本工具已在操作后刷新自身状态。");
        }

        if (output.Length == 0)
        {
            output.AppendLine("没有选择任何 Clean Up 操作。");
        }

        return new ProcessResult(exitCode, output.ToString(), "");
    }

    private static Task<ProcessResult> RunCleanupCommandAsync(string workingCopyPath, bool includeExternals, params string[] options)
    {
        var args = new List<string> { "cleanup" };
        args.AddRange(options);
        if (includeExternals)
        {
            args.Add("--include-externals");
        }

        return RunAsync(workingCopyPath, args.ToArray());
    }

    private static int MergeExitCode(int current, int next)
    {
        return current != 0 ? current : next;
    }

    public Task<ProcessResult> UpdateToRevisionAsync(string workingCopyPath, long revision)
    {
        return RunAsync(workingCopyPath, "update", "-r", revision.ToString());
    }

    public Task<ProcessResult> UpdatePathAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "update", relativePath);
    }

    public Task<ProcessResult> UpdatePathToRevisionAsync(string workingCopyPath, string relativePath, long revision)
    {
        return RunAsync(workingCopyPath, "update", "-r", revision.ToString(), relativePath);
    }

    public Task<ProcessResult> RevertAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "revert", relativePath);
    }

    public Task<ProcessResult> ReverseMergeRevisionForPathAsync(string workingCopyPath, string relativePath, long revision, bool dryRun)
    {
        var args = new List<string> { "merge", "-c", "-" + revision, relativePath };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        return RunAsync(workingCopyPath, args.ToArray());
    }

    public Task<ProcessResult> LockAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "lock", relativePath);
    }

    public Task<ProcessResult> UnlockAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "unlock", relativePath);
    }

    public Task<ProcessResult> InfoAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "info", relativePath);
    }

    public Task<ProcessResult> AddAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "add", relativePath);
    }

    public Task<ProcessResult> GetIgnoreListAsync(string workingCopyPath)
    {
        return RunTextAsync(workingCopyPath, "propget", "svn:ignore", "-R", ".");
    }

    public Task<ProcessResult> GetIgnoreAsync(string workingCopyPath, string parentPath)
    {
        return RunTextAsync(workingCopyPath, "propget", "svn:ignore", parentPath);
    }

    public Task<ProcessResult> SetIgnoreAsync(string workingCopyPath, string parentPath, IEnumerable<string> names)
    {
        var value = string.Join(Environment.NewLine, names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(value)
            ? RunTextAsync(workingCopyPath, "propdel", "svn:ignore", parentPath)
            : RunTextAsync(workingCopyPath, "propset", "svn:ignore", value, parentPath);
    }

    public Task<ProcessResult> ResolveAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "resolve", "--accept", "working", relativePath);
    }

    public Task<ProcessResult> CommitAsync(string workingCopyPath, IEnumerable<string> relativePaths, string message)
    {
        var args = new List<string> { "commit", "-m", message };
        args.AddRange(relativePaths);
        return RunAsync(workingCopyPath, args.ToArray());
    }

    public async Task WriteBaseFileAsync(string workingCopyPath, string relativePath, string outputPath, CancellationToken cancellationToken = default)
    {
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", "BASE", relativePath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task WriteHeadFileAsync(string workingCopyPath, string relativePath, string outputPath, CancellationToken cancellationToken = default)
    {
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", "HEAD", relativePath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task WriteRepositoryFileAtRevisionAsync(string workingCopyPath, string repositoryPath, long revision, string outputPath, CancellationToken cancellationToken = default)
    {
        var path = repositoryPath.StartsWith("^", StringComparison.Ordinal) ? repositoryPath : "^" + repositoryPath;
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", revision.ToString(), path);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task<IReadOnlyList<SvnChange>> GetStatusAsync(string workingCopyPath)
    {
        return await Task.Run(() => GetStatus(workingCopyPath));
    }

    public IReadOnlyList<SvnChange> GetStatus(string workingCopyPath)
    {
        var result = RunText(workingCopyPath, "status");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnChange>();
        }

        return result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseStatusLine)
            .Where(change => change != null)
            .Cast<SvnChange>()
            .Where(change => !SvnConflictArtifact.IsAuxiliaryPath(change.RelativePath))
            .OrderBy(change => change.RelativePath)
            .ToList();
    }

    public WorkingCopyInfo GetWorkingCopyInfo(string workingCopyPath)
    {
        var result = RunText(workingCopyPath, "info");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return WorkingCopyInfo.Empty;
        }

        var values = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        var revision = values.TryGetValue("Revision", out var revisionText) && long.TryParse(revisionText, out var rev) ? rev : 0;
        var lastChangedRevision = values.TryGetValue("Last Changed Rev", out var lastChangedText) && long.TryParse(lastChangedText, out var lastChangedRev)
            ? lastChangedRev
            : revision;
        var url = values.TryGetValue("URL", out var urlText) ? urlText : "";
        var maxRevision = Math.Max(revision, lastChangedRevision);
        var minRevision = revision;
        try
        {
            var versionResult = RunToolText("svnversion", null, workingCopyPath);
            if (versionResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(versionResult.StandardOutput))
            {
                var version = ParseSvnVersion(versionResult.StandardOutput);
                if (version.MaxRevision > 0)
                {
                    minRevision = version.MinRevision;
                    maxRevision = version.MaxRevision;
                }
            }
        }
        catch
        {
            // svnversion is optional; root svn info is still usable as a fallback.
        }

        return new WorkingCopyInfo(revision, lastChangedRevision, minRevision, maxRevision, url);
    }

    private static SvnChange? ParseStatusLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var status = line[0];
        if (MapTextStatus(status) == SvnStatusKind.None || !LooksLikeStatusLine(line))
        {
            return null;
        }

        var path = line.Length > 8 ? line[8..].Trim() : line[1..].Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new SvnChange(path, MapTextStatus(status));
    }

    private static bool LooksLikeStatusLine(string line)
    {
        if (line.Length < 2)
        {
            return false;
        }

        return line.Length > 7 && char.IsWhiteSpace(line[7]) || line.Length > 1 && char.IsWhiteSpace(line[1]);
    }

    private static (long MinRevision, long MaxRevision) ParseSvnVersion(string text)
    {
        var revisions = System.Text.RegularExpressions.Regex.Matches(text, @"\d+")
            .Select(match => long.TryParse(match.Value, out var revision) ? revision : 0)
            .Where(revision => revision > 0)
            .ToList();
        if (revisions.Count == 0)
        {
            return (0, 0);
        }

        return (revisions.Min(), revisions.Max());
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string workingCopyPath, string relativePath, int limit)
    {
        return await GetLogAsync(workingCopyPath, [relativePath], limit);
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string workingCopyPath, IReadOnlyList<string> relativePaths, int limit)
    {
        var targets = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return Array.Empty<SvnLogEntry>();
        }

        if (targets.Count == 1)
        {
            return await GetLogForSingleTargetAsync(workingCopyPath, targets[0], limit);
        }

        var allLogs = new List<SvnLogEntry>();
        foreach (var target in targets)
        {
            allLogs.AddRange(await GetLogForSingleTargetAsync(workingCopyPath, target, limit));
        }

        return MergeLogEntries(allLogs)
            .OrderByDescending(log => log.Revision)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogRangeAsync(string workingCopyPath, IReadOnlyList<string> relativePaths, long revisionStart, long revisionEnd, int limit)
    {
        var targets = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return Array.Empty<SvnLogEntry>();
        }

        if (targets.Count == 1)
        {
            return await GetLogForSingleTargetRangeAsync(workingCopyPath, targets[0], revisionStart, revisionEnd, limit);
        }

        var allLogs = new List<SvnLogEntry>();
        foreach (var target in targets)
        {
            allLogs.AddRange(await GetLogForSingleTargetRangeAsync(workingCopyPath, target, revisionStart, revisionEnd, limit));
        }

        return MergeLogEntries(allLogs)
            .OrderByDescending(log => log.Revision)
            .Take(limit)
            .ToList();
    }

    private async Task<IReadOnlyList<SvnLogEntry>> GetLogForSingleTargetAsync(string workingCopyPath, string relativePath, int limit)
    {
        var args = new List<string> { "log", "-v", "-r", "HEAD:1", "--limit", limit.ToString() };
        args.Add(relativePath);
        var result = await RunTextAsync(workingCopyPath, args.ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        return ParseTextLogEntries(result.StandardOutput);
    }

    private async Task<IReadOnlyList<SvnLogEntry>> GetLogForSingleTargetRangeAsync(string workingCopyPath, string relativePath, long revisionStart, long revisionEnd, int limit)
    {
        var start = Math.Min(revisionStart, revisionEnd);
        var end = Math.Max(revisionStart, revisionEnd);
        var args = new List<string> { "log", "-v", "-r", $"{end}:{start}", "--limit", limit.ToString() };
        args.Add(relativePath);
        var result = await RunTextAsync(workingCopyPath, args.ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        return ParseTextLogEntries(result.StandardOutput);
    }

    private static IReadOnlyList<SvnLogEntry> MergeLogEntries(IEnumerable<SvnLogEntry> logs)
    {
        return logs
            .GroupBy(log => log.Revision)
            .Select(group =>
            {
                var first = group.OrderByDescending(log => log.ChangedFiles.Count).First();
                var changedFiles = group
                    .SelectMany(log => log.ChangedFiles)
                    .GroupBy(file => $"{file.Action}|{file.RepositoryPath}|{file.RelativePath}", StringComparer.OrdinalIgnoreCase)
                    .Select(fileGroup => fileGroup.First())
                    .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return first with { ChangedFiles = changedFiles };
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetRepositoryLogAsync(string workingCopyPath, int limit)
    {
        var result = await RunTextAsync(workingCopyPath, "log", "-v", "-r", "HEAD:1", "--limit", limit.ToString());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        return ParseTextLogEntries(result.StandardOutput);
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetRepositoryLogRangeAsync(string workingCopyPath, long revisionStart, long revisionEnd, int limit)
    {
        var start = Math.Min(revisionStart, revisionEnd);
        var end = Math.Max(revisionStart, revisionEnd);
        var result = await RunTextAsync(workingCopyPath, "log", "-v", "-r", $"{end}:{start}", "--limit", limit.ToString());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        return ParseTextLogEntries(result.StandardOutput);
    }

    public async Task<SvnLogEntry?> GetLatestRepositoryLogAsync(string workingCopyPath)
    {
        var logs = await GetRepositoryLogAsync(workingCopyPath, 1);
        return logs.FirstOrDefault();
    }

    private static IReadOnlyList<SvnLogEntry> ParseTextLogEntries(string text)
    {
        var entries = new List<SvnLogEntry>();
        var blocks = text.Split("------------------------------------------------------------------------", StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawBlock in blocks)
        {
            var lines = rawBlock
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd())
                .ToList();
            var headerIndex = lines.FindIndex(line => line.StartsWith("r", StringComparison.Ordinal) && line.Contains('|'));
            if (headerIndex < 0)
            {
                continue;
            }

            var parts = lines[headerIndex].Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length < 3)
            {
                continue;
            }

            var revision = long.TryParse(parts[0].TrimStart('r'), out var revisionValue) ? revisionValue : 0;
            var author = parts[1];
            var date = ParseSvnTextDate(parts[2]);
            var changedFiles = new List<ChangedFileEntry>();
            var cursor = headerIndex + 1;
            while (cursor < lines.Count && string.IsNullOrWhiteSpace(lines[cursor]))
            {
                cursor++;
            }

            if (cursor < lines.Count && string.Equals(lines[cursor].Trim(), "Changed paths:", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                while (cursor < lines.Count && !string.IsNullOrWhiteSpace(lines[cursor]))
                {
                    changedFiles.Add(ChangedFileEntry.ParseRepositoryPath(lines[cursor].Trim()));
                    cursor++;
                }
            }

            var messageLines = lines.Skip(cursor).SkipWhile(string.IsNullOrWhiteSpace).ToList();
            entries.Add(new SvnLogEntry(revision, author, date, string.Join(Environment.NewLine, messageLines).Trim())
            {
                ChangedFiles = changedFiles,
            });
        }

        return entries;
    }

    private static DateTimeOffset ParseSvnTextDate(string text)
    {
        var datePart = text.Split('(')[0].Trim();
        return DateTimeOffset.TryParse(datePart, out var date) ? date : DateTimeOffset.MinValue;
    }

    private static SvnStatusKind MapStatus(string status)
    {
        return status switch
        {
            "modified" => SvnStatusKind.Modified,
            "added" => SvnStatusKind.Added,
            "deleted" => SvnStatusKind.Deleted,
            "unversioned" => SvnStatusKind.Unversioned,
            "missing" => SvnStatusKind.Missing,
            "conflicted" => SvnStatusKind.Conflicted,
            "replaced" => SvnStatusKind.Replaced,
            "normal" => SvnStatusKind.Normal,
            _ => SvnStatusKind.None,
        };
    }

    private static SvnStatusKind MapTextStatus(char status)
    {
        return status switch
        {
            'M' => SvnStatusKind.Modified,
            'A' => SvnStatusKind.Added,
            'D' => SvnStatusKind.Deleted,
            '?' => SvnStatusKind.Unversioned,
            '!' => SvnStatusKind.Missing,
            'C' => SvnStatusKind.Conflicted,
            'R' => SvnStatusKind.Replaced,
            _ => SvnStatusKind.None,
        };
    }

    private static async Task<ProcessResult> RunAsync(string? workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "svn",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ProcessResult> RunTextAsync(string? workingDirectory, params string[] arguments)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var textEncoding = Encoding.GetEncoding("GB18030");
        var startInfo = new ProcessStartInfo
        {
            FileName = "svn",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = textEncoding,
            StandardErrorEncoding = textEncoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunText(string? workingDirectory, params string[] arguments)
    {
        return RunToolText("svn", workingDirectory, arguments);
    }

    private static ProcessResult RunToolText(string fileName, string? workingDirectory, params string[] arguments)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var textEncoding = Encoding.GetEncoding("GB18030");
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = textEncoding,
            StandardErrorEncoding = textEncoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ProcessResult> RunBinaryToFileAsync(string? workingDirectory, string outputPath, CancellationToken cancellationToken = default, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "svn",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
        await using var output = File.Create(outputPath);
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await copyTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, "", stderr);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Canceling stale diff previews should not surface process cleanup errors.
        }
    }
}

internal static class ExcelDiffService
{
    public static IReadOnlyList<ExcelCellDifference> Compare(string oldFilePath, string newFilePath)
    {
        var oldCells = ReadCellValues(oldFilePath);
        var newCells = ReadCellValues(newFilePath);
        var keys = oldCells.Keys
            .Union(newCells.Keys)
            .OrderBy(key => key.Sheet)
            .ThenBy(key => key.Row)
            .ThenBy(key => key.Column);
        var differences = new List<ExcelCellDifference>();

        foreach (var key in keys)
        {
            oldCells.TryGetValue(key, out var oldValue);
            newCells.TryGetValue(key, out var newValue);
            oldValue ??= "";
            newValue ??= "";
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                differences.Add(CreateDifference(key, oldCells, newCells, oldValue, newValue));
            }
        }

        return differences;
    }

    public static Dictionary<ExcelCellKey, string> ReadCellValues(string filePath)
    {
        if (IsXmlSpreadsheet(filePath))
        {
            return ReadXmlSpreadsheetCells(filePath);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.OpenRead(filePath);
        var workbook = WorkbookFactory.Create(stream);
        return ReadCells(workbook);
    }

    public static bool IsXmlSpreadsheetFile(string filePath)
    {
        return IsXmlSpreadsheet(filePath);
    }

    private static ExcelCellDifference CreateDifference(
        ExcelCellKey key,
        Dictionary<ExcelCellKey, string> oldCells,
        Dictionary<ExcelCellKey, string> newCells,
        string oldValue,
        string newValue)
    {
        var fieldName = FirstNonEmpty(
            GetCellValue(newCells, key.Sheet, 1, key.Column),
            GetCellValue(oldCells, key.Sheet, 1, key.Column));
        var rowId = FirstNonEmpty(
            GetCellValue(newCells, key.Sheet, key.Row, 0),
            GetCellValue(oldCells, key.Sheet, key.Row, 0));
        return new ExcelCellDifference(
            key.Sheet,
            key.Row + 1,
            key.Column + 1,
            ToColumnName(key.Column),
            fieldName,
            rowId,
            oldValue,
            newValue);
    }

    private static string GetCellValue(Dictionary<ExcelCellKey, string> cells, string sheet, int row, int column)
    {
        return cells.TryGetValue(new ExcelCellKey(sheet, row, column), out var value) ? value : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static bool IsXmlSpreadsheet(string filePath)
    {
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(filePath);
        if (!string.Equals(Path.GetExtension(comparablePath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var document = XDocument.Load(stream, LoadOptions.None);
            return document.Root?.Name.LocalName == "Workbook" &&
                document.Root.Name.NamespaceName == "urn:schemas-microsoft-com:office:spreadsheet";
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<ExcelCellKey, string> ReadXmlSpreadsheetCells(string filePath)
    {
        var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
        XNamespace spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
        var cells = new Dictionary<ExcelCellKey, string>();

        foreach (var worksheet in document.Root?.Elements(spreadsheet + "Worksheet") ?? Enumerable.Empty<XElement>())
        {
            var sheetName = worksheet.Attribute(spreadsheet + "Name")?.Value ?? "Sheet";
            var table = worksheet.Element(spreadsheet + "Table");
            if (table == null)
            {
                continue;
            }

            var rowIndex = 0;
            foreach (var row in table.Elements(spreadsheet + "Row"))
            {
                var explicitRowIndex = GetSpreadsheetIndex(row, spreadsheet);
                if (explicitRowIndex.HasValue)
                {
                    rowIndex = explicitRowIndex.Value - 1;
                }

                var columnIndex = 0;
                foreach (var cell in row.Elements(spreadsheet + "Cell"))
                {
                    var explicitColumnIndex = GetSpreadsheetIndex(cell, spreadsheet);
                    if (explicitColumnIndex.HasValue)
                    {
                        columnIndex = explicitColumnIndex.Value - 1;
                    }

                    var data = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "Data");
                    var value = NormalizeCellText(data?.Value ?? "");
                    if (!string.IsNullOrEmpty(value))
                    {
                        cells[new ExcelCellKey(sheetName, rowIndex, columnIndex)] = value;
                    }

                    columnIndex++;
                }

                rowIndex++;
            }
        }

        return cells;
    }

    private static int? GetSpreadsheetIndex(XElement element, XNamespace spreadsheet)
    {
        var value = element.Attribute(spreadsheet + "Index")?.Value;
        return int.TryParse(value, out var index) ? index : null;
    }

    private static string NormalizeCellText(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static Dictionary<ExcelCellKey, string> ReadCells(IWorkbook workbook)
    {
        var formatter = new DataFormatter();
        var cells = new Dictionary<ExcelCellKey, string>();
        for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            if (sheet == null)
            {
                continue;
            }

            for (var rowIndex = sheet.FirstRowNum; rowIndex <= sheet.LastRowNum; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row == null)
                {
                    continue;
                }

                for (var columnIndex = row.FirstCellNum; columnIndex < row.LastCellNum; columnIndex++)
                {
                    if (columnIndex < 0)
                    {
                        continue;
                    }

                    var cell = row.GetCell(columnIndex);
                    var value = cell == null ? "" : formatter.FormatCellValue(cell).Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        cells[new ExcelCellKey(sheet.SheetName, rowIndex, columnIndex)] = value;
                    }
                }
            }
        }

        return cells;
    }

    public static string ToColumnName(int zeroBasedColumn)
    {
        var column = zeroBasedColumn + 1;
        var name = "";
        while (column > 0)
        {
            var modulo = (column - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            column = (column - modulo) / 26;
        }

        return name;
    }
}

internal enum SpreadsheetMergeChangeKind
{
    AutoRemote,
    LocalOnly,
    SameBoth,
    Conflict,
}

internal enum SpreadsheetMergeResolution
{
    UseLocal,
    UseRemote,
}

internal sealed class SpreadsheetMergePlan
{
    public SpreadsheetMergePlan(
        IReadOnlyList<SpreadsheetMergeChange> autoRemoteChanges,
        IReadOnlyList<SpreadsheetMergeChange> localOnlyChanges,
        IReadOnlyList<SpreadsheetMergeChange> sameBothChanges,
        IReadOnlyList<SpreadsheetMergeChange> conflicts)
    {
        AutoRemoteChanges = autoRemoteChanges;
        LocalOnlyChanges = localOnlyChanges;
        SameBothChanges = sameBothChanges;
        Conflicts = conflicts;
    }

    public IReadOnlyList<SpreadsheetMergeChange> AutoRemoteChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> LocalOnlyChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> SameBothChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> Conflicts { get; }
    public IReadOnlyList<SpreadsheetMergeChange> AllChanges => AutoRemoteChanges
        .Concat(LocalOnlyChanges)
        .Concat(SameBothChanges)
        .Concat(Conflicts)
        .ToList();
    public int ResolvedConflictCount => Conflicts.Count(change => change.Resolution == SpreadsheetMergeResolution.UseRemote);
    public int PlannedWriteCount => AllChanges.Count(change =>
        change.Resolution == SpreadsheetMergeResolution.UseRemote &&
        !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal));
    public int RelevantChangeCount => AutoRemoteChanges.Count + LocalOnlyChanges.Count + SameBothChanges.Count + Conflicts.Count;

    public IReadOnlyList<SpreadsheetMergeWrite> BuildWrites()
    {
        return AllChanges
            .Where(change => change.Resolution == SpreadsheetMergeResolution.UseRemote)
            .Where(change => !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal))
            .Select(change => new SpreadsheetMergeWrite(change.WriteCell, change.RemoteValue))
            .GroupBy(write => write.Cell)
            .Select(group => group.Last())
            .ToList();
    }
}

internal sealed class SpreadsheetMergeChange
{
    public SpreadsheetMergeChange(
        SpreadsheetMergeChangeKind kind,
        ExcelCellKey targetCell,
        string fieldName,
        string rowId,
        string baseValue,
        string localValue,
        string remoteValue)
    {
        Kind = kind;
        TargetCell = targetCell;
        WriteCell = targetCell;
        FieldName = string.IsNullOrWhiteSpace(fieldName) ? "(未命名字段)" : fieldName;
        RowId = string.IsNullOrWhiteSpace(rowId) ? "(无 ID)" : rowId;
        BaseValue = baseValue;
        LocalValue = localValue;
        RemoteValue = remoteValue;
        Resolution = kind == SpreadsheetMergeChangeKind.AutoRemote
            ? SpreadsheetMergeResolution.UseRemote
            : SpreadsheetMergeResolution.UseLocal;
    }

    public SpreadsheetMergeChangeKind Kind { get; }
    public ExcelCellKey TargetCell { get; }
    public ExcelCellKey WriteCell { get; set; }
    public string FieldName { get; }
    public string RowId { get; }
    public string BaseValue { get; }
    public string LocalValue { get; }
    public string RemoteValue { get; }
    public SpreadsheetMergeResolution Resolution { get; set; }
    public string Sheet => TargetCell.Sheet;
    public string ColumnName => ExcelDiffService.ToColumnName(TargetCell.Column);
    public string Address => $"{ColumnName}{TargetCell.Row + 1}";
}

internal sealed record SpreadsheetMergeWrite(ExcelCellKey Cell, string Value);

internal sealed record SpreadsheetMergeRawCell(
    ExcelCellKey Cell,
    string Value,
    string FieldName,
    string RowId,
    string SemanticKey,
    bool HasSemanticKey)
{
    public string PhysicalKey => SpreadsheetThreeWayMergeService.CreatePhysicalKey(Cell);
}

internal sealed record SpreadsheetMergeCell(
    string MergeKey,
    ExcelCellKey Cell,
    string Value,
    string FieldName,
    string RowId);

internal sealed class SpreadsheetMergeSheetLayout
{
    public SpreadsheetMergeSheetLayout(string sheet, int fieldHeaderRow, Dictionary<int, string> headers, IReadOnlyList<int> keyColumns)
    {
        Sheet = sheet;
        FieldHeaderRow = fieldHeaderRow;
        Headers = headers;
        KeyColumns = keyColumns;
    }

    public string Sheet { get; }
    public int FieldHeaderRow { get; }
    public Dictionary<int, string> Headers { get; }
    public IReadOnlyList<int> KeyColumns { get; }
    public bool HasKey => KeyColumns.Count > 0;

    public string FieldName(int column)
    {
        return Headers.TryGetValue(column, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header
            : $"col_{column + 1}";
    }

    public string RowKey(Dictionary<ExcelCellKey, string> cells, int row)
    {
        if (KeyColumns.Count == 0)
        {
            return "";
        }

        var values = KeyColumns
            .Select(column => GetCellValue(cells, Sheet, row, column).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return values.Count == KeyColumns.Count ? string.Join("/", values) : "";
    }

    private static string GetCellValue(Dictionary<ExcelCellKey, string> cells, string sheet, int row, int column)
    {
        return cells.TryGetValue(new ExcelCellKey(sheet, row, column), out var value) ? value : "";
    }
}

internal static class SpreadsheetThreeWayMergeService
{
    private static readonly Regex FieldTokenRegex = new("^[a-zA-Z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex HeaderNonWordRegex = new(@"[^\w\u4e00-\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex HeaderUnderscoreRegex = new("_+", RegexOptions.Compiled);
    private static readonly Regex KeyFieldRegex = new(@"(?:^|_)(id|level|key|code|name|type)(?:$|_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSupportedPath(string filePath)
    {
        return DiffFileKindDetector.IsSpreadsheet(filePath);
    }

    public static SpreadsheetMergePlan BuildPlan(string baseFilePath, string localFilePath, string remoteFilePath)
    {
        var baseRaw = ReadRawCells(baseFilePath);
        var localRaw = ReadRawCells(localFilePath);
        var remoteRaw = ReadRawCells(remoteFilePath);
        var unsafeSemanticKeys = FindUnsafeSemanticKeys(baseRaw, localRaw, remoteRaw);
        var baseCells = MaterializeCells(baseRaw, unsafeSemanticKeys);
        var localCells = MaterializeCells(localRaw, unsafeSemanticKeys);
        var remoteCells = MaterializeCells(remoteRaw, unsafeSemanticKeys);

        var keys = baseCells.Keys
            .Union(localCells.Keys)
            .Union(remoteCells.Keys)
            .OrderBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Sheet)
            .ThenBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Row)
            .ThenBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Column)
            .ToList();

        var autoRemote = new List<SpreadsheetMergeChange>();
        var localOnly = new List<SpreadsheetMergeChange>();
        var sameBoth = new List<SpreadsheetMergeChange>();
        var conflicts = new List<SpreadsheetMergeChange>();

        foreach (var key in keys)
        {
            baseCells.TryGetValue(key, out var baseCell);
            localCells.TryGetValue(key, out var localCell);
            remoteCells.TryGetValue(key, out var remoteCell);

            var baseValue = baseCell?.Value ?? "";
            var localValue = localCell?.Value ?? "";
            var remoteValue = remoteCell?.Value ?? "";
            var localChanged = !string.Equals(localValue, baseValue, StringComparison.Ordinal);
            var remoteChanged = !string.Equals(remoteValue, baseValue, StringComparison.Ordinal);
            if (!localChanged && !remoteChanged)
            {
                continue;
            }

            var target = PickCell(key, localCells, baseCells, remoteCells) ?? throw new InvalidOperationException("无法定位合并单元格。");
            var fieldName = FirstNonEmpty(localCell?.FieldName, remoteCell?.FieldName, baseCell?.FieldName);
            var rowId = FirstNonEmpty(localCell?.RowId, remoteCell?.RowId, baseCell?.RowId);

            if (remoteChanged && !localChanged)
            {
                autoRemote.Add(new SpreadsheetMergeChange(SpreadsheetMergeChangeKind.AutoRemote, target.Cell, fieldName, rowId, baseValue, localValue, remoteValue));
                continue;
            }

            if (localChanged && !remoteChanged)
            {
                localOnly.Add(new SpreadsheetMergeChange(SpreadsheetMergeChangeKind.LocalOnly, target.Cell, fieldName, rowId, baseValue, localValue, remoteValue));
                continue;
            }

            if (string.Equals(localValue, remoteValue, StringComparison.Ordinal))
            {
                sameBoth.Add(new SpreadsheetMergeChange(SpreadsheetMergeChangeKind.SameBoth, target.Cell, fieldName, rowId, baseValue, localValue, remoteValue));
                continue;
            }

            conflicts.Add(new SpreadsheetMergeChange(SpreadsheetMergeChangeKind.Conflict, target.Cell, fieldName, rowId, baseValue, localValue, remoteValue));
        }

        return new SpreadsheetMergePlan(autoRemote, localOnly, sameBoth, conflicts);
    }

    public static string CreateBackup(string localFilePath)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNManager",
            "merge-backups",
            DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileNameWithoutExtension(localFilePath);
        var extension = Path.GetExtension(localFilePath);
        var backupPath = Path.Combine(directory, $"{fileName}_{DateTime.Now:HHmmss_fff}{extension}");
        File.Copy(localFilePath, backupPath, overwrite: false);
        return backupPath;
    }

    public static void ApplyWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        if (writes.Count == 0)
        {
            return;
        }

        if (ExcelDiffService.IsXmlSpreadsheetFile(localFilePath))
        {
            ApplyXmlWrites(localFilePath, writes);
            return;
        }

        ApplyWorkbookWrites(localFilePath, writes);
    }

    public static string CreatePhysicalKey(ExcelCellKey cell)
    {
        return $"P|{cell.Sheet}|{cell.Row}|{cell.Column}";
    }

    private static IReadOnlyList<SpreadsheetMergeRawCell> ReadRawCells(string filePath)
    {
        var cells = ExcelDiffService.ReadCellValues(filePath);
        var layouts = InferSheetLayouts(cells);
        return cells
            .Select(pair =>
            {
                var cell = pair.Key;
                var layout = layouts.TryGetValue(cell.Sheet, out var inferredLayout)
                    ? inferredLayout
                    : new SpreadsheetMergeSheetLayout(cell.Sheet, 1, [], []);
                var fieldName = layout.FieldName(cell.Column);
                var rowId = layout.RowKey(cells, cell.Row);
                var hasSemanticKey = layout.HasKey &&
                    cell.Row > layout.FieldHeaderRow &&
                    !string.IsNullOrWhiteSpace(rowId) &&
                    !string.IsNullOrWhiteSpace(fieldName);
                var semanticKey = hasSemanticKey
                    ? $"S|{cell.Sheet}|{rowId.Trim()}|{NormalizeHeader(fieldName)}"
                    : "";
                return new SpreadsheetMergeRawCell(cell, pair.Value, fieldName, rowId, semanticKey, hasSemanticKey);
            })
            .ToList();
    }

    private static Dictionary<string, SpreadsheetMergeSheetLayout> InferSheetLayouts(Dictionary<ExcelCellKey, string> cells)
    {
        return cells.Keys
            .Select(key => key.Sheet)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                sheet => sheet,
                sheet => InferSheetLayout(sheet, cells),
                StringComparer.Ordinal);
    }

    private static SpreadsheetMergeSheetLayout InferSheetLayout(string sheet, Dictionary<ExcelCellKey, string> cells)
    {
        var sheetCells = cells
            .Where(pair => string.Equals(pair.Key.Sheet, sheet, StringComparison.Ordinal))
            .ToList();
        if (sheetCells.Count == 0)
        {
            return new SpreadsheetMergeSheetLayout(sheet, 1, [], []);
        }

        var maxRow = sheetCells.Max(pair => pair.Key.Row);
        var maxColumn = sheetCells.Max(pair => pair.Key.Column);
        var fieldHeaderRow = InferFieldHeaderRow(sheet, cells, maxRow);
        var headers = Enumerable.Range(0, maxColumn + 1)
            .ToDictionary(column => column, column =>
            {
                var value = GetCellValue(cells, sheet, fieldHeaderRow, column).Trim();
                return string.IsNullOrWhiteSpace(value) ? $"col_{column + 1}" : value;
            });
        var keyColumns = InferKeyColumns(sheet, cells, headers, fieldHeaderRow, maxRow);
        return new SpreadsheetMergeSheetLayout(sheet, fieldHeaderRow, headers, keyColumns);
    }

    private static int InferFieldHeaderRow(string sheet, Dictionary<ExcelCellKey, string> cells, int maxRow)
    {
        var bestRow = 0;
        var bestScore = -1.0;
        var limit = Math.Min(maxRow, 11);
        for (var row = 0; row <= limit; row++)
        {
            var values = RowValues(cells, sheet, row)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var tokenCount = values.Count(value => FieldTokenRegex.IsMatch(value));
            var score = (double)tokenCount / values.Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = row;
            }
        }

        return bestRow;
    }

    private static IReadOnlyList<int> InferKeyColumns(
        string sheet,
        Dictionary<ExcelCellKey, string> cells,
        Dictionary<int, string> headers,
        int fieldHeaderRow,
        int maxRow)
    {
        var dataRows = Enumerable.Range(fieldHeaderRow + 1, Math.Max(0, maxRow - fieldHeaderRow))
            .Where(row => RowNonEmptyCount(cells, sheet, row) >= 2)
            .ToList();
        if (dataRows.Count == 0)
        {
            return [];
        }

        foreach (var exactIdColumn in headers
            .Where(pair => string.Equals(NormalizeHeader(pair.Value), "id", StringComparison.Ordinal))
            .Select(pair => pair.Key))
        {
            var keys = dataRows
                .Select(row => GetCellValue(cells, sheet, row, exactIdColumn).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (keys.Count > 0 && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count)
            {
                return [exactIdColumn];
            }
        }

        var candidates = headers
            .Where(pair => KeyFieldRegex.IsMatch(NormalizeHeader(pair.Value)))
            .Select(pair => pair.Key)
            .Take(6)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        IReadOnlyList<int> bestColumns = [];
        var bestScore = -1.0;
        for (var width = 1; width <= Math.Min(3, candidates.Count); width++)
        {
            foreach (var columns in Combinations(candidates, width))
            {
                var keys = dataRows
                    .Select(row => columns.Select(column => GetCellValue(cells, sheet, row, column).Trim()).ToArray())
                    .Where(values => values.All(value => !string.IsNullOrWhiteSpace(value)))
                    .Select(values => string.Join("\u001f", values))
                    .ToList();
                if (keys.Count == 0 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                {
                    continue;
                }

                var coverage = (double)keys.Count / dataRows.Count;
                var idBonus = columns.Count(column => NormalizeHeader(headers[column]).Contains("id", StringComparison.Ordinal)) * 0.15;
                var score = coverage + idBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestColumns = columns.ToList();
                }
            }
        }

        return bestScore >= 0.8 ? bestColumns : [];
    }

    private static IEnumerable<IReadOnlyList<int>> Combinations(IReadOnlyList<int> values, int width)
    {
        var selected = new int[width];
        return Build(0, 0);

        IEnumerable<IReadOnlyList<int>> Build(int start, int depth)
        {
            if (depth == width)
            {
                yield return selected.ToArray();
                yield break;
            }

            for (var index = start; index <= values.Count - (width - depth); index++)
            {
                selected[depth] = values[index];
                foreach (var item in Build(index + 1, depth + 1))
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<string> RowValues(Dictionary<ExcelCellKey, string> cells, string sheet, int row)
    {
        return cells
            .Where(pair => string.Equals(pair.Key.Sheet, sheet, StringComparison.Ordinal) && pair.Key.Row == row)
            .OrderBy(pair => pair.Key.Column)
            .Select(pair => pair.Value);
    }

    private static int RowNonEmptyCount(Dictionary<ExcelCellKey, string> cells, string sheet, int row)
    {
        return RowValues(cells, sheet, row).Count(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeHeader(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        text = HeaderNonWordRegex.Replace(text, "_");
        text = HeaderUnderscoreRegex.Replace(text, "_").Trim('_');
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }

    private static HashSet<string> FindUnsafeSemanticKeys(params IReadOnlyList<SpreadsheetMergeRawCell>[] versions)
    {
        var unsafeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            foreach (var group in version.Where(cell => cell.HasSemanticKey).GroupBy(cell => cell.SemanticKey))
            {
                if (group.Count() > 1)
                {
                    unsafeKeys.Add(group.Key);
                }
            }
        }

        return unsafeKeys;
    }

    private static Dictionary<string, SpreadsheetMergeCell> MaterializeCells(
        IReadOnlyList<SpreadsheetMergeRawCell> rawCells,
        HashSet<string> unsafeSemanticKeys)
    {
        return rawCells
            .Select(raw =>
            {
                var key = raw.HasSemanticKey && !unsafeSemanticKeys.Contains(raw.SemanticKey)
                    ? raw.SemanticKey
                    : raw.PhysicalKey;
                return new SpreadsheetMergeCell(key, raw.Cell, raw.Value, raw.FieldName, raw.RowId);
            })
            .GroupBy(cell => cell.MergeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    private static SpreadsheetMergeCell? PickCell(
        string key,
        Dictionary<string, SpreadsheetMergeCell> localCells,
        Dictionary<string, SpreadsheetMergeCell> baseCells,
        Dictionary<string, SpreadsheetMergeCell> remoteCells)
    {
        if (localCells.TryGetValue(key, out var localCell))
        {
            return localCell;
        }

        if (baseCells.TryGetValue(key, out var baseCell))
        {
            return baseCell;
        }

        return remoteCells.TryGetValue(key, out var remoteCell) ? remoteCell : null;
    }

    private static string GetCellValue(Dictionary<ExcelCellKey, string> cells, string sheet, int row, int column)
    {
        return cells.TryGetValue(new ExcelCellKey(sheet, row, column), out var value) ? value : "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static void ApplyWorkbookWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        IWorkbook workbook;
        using (var input = File.OpenRead(localFilePath))
        {
            workbook = WorkbookFactory.Create(input);
        }

        foreach (var write in writes)
        {
            var sheet = workbook.GetSheet(write.Cell.Sheet) ?? workbook.CreateSheet(write.Cell.Sheet);
            var row = sheet.GetRow(write.Cell.Row) ?? sheet.CreateRow(write.Cell.Row);
            var cell = row.GetCell(write.Cell.Column) ?? row.CreateCell(write.Cell.Column);
            if (string.IsNullOrEmpty(write.Value))
            {
                cell.SetCellType(CellType.Blank);
            }
            else
            {
                cell.SetCellValue(write.Value);
            }
        }

        using var output = File.Create(localFilePath);
        workbook.Write(output);
        workbook.Close();
    }

    private static void ApplyXmlWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        var document = XDocument.Load(localFilePath, LoadOptions.PreserveWhitespace);
        XNamespace spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
        var root = document.Root ?? throw new InvalidOperationException("XML 表格缺少 Workbook 根节点。");

        foreach (var write in writes)
        {
            var worksheet = GetOrCreateWorksheet(root, spreadsheet, write.Cell.Sheet);
            var table = worksheet.Element(spreadsheet + "Table");
            if (table == null)
            {
                table = new XElement(spreadsheet + "Table");
                worksheet.Add(table);
            }

            var row = GetOrCreateXmlRow(table, spreadsheet, write.Cell.Row);
            var cell = GetOrCreateXmlCell(row, spreadsheet, write.Cell.Column);
            RemoveXmlFormulaAttributes(cell, spreadsheet);
            var data = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "Data");
            if (data == null)
            {
                data = new XElement(spreadsheet + "Data", new XAttribute(spreadsheet + "Type", "String"));
                cell.Add(data);
            }

            data.Value = NormalizeXmlCellText(write.Value);
            if (string.IsNullOrWhiteSpace(data.Attribute(spreadsheet + "Type")?.Value))
            {
                data.SetAttributeValue(spreadsheet + "Type", GuessXmlCellType(write.Value));
            }
        }

        foreach (var table in root.Descendants(spreadsheet + "Table"))
        {
            UpdateXmlTableExtents(table, spreadsheet);
        }

        document.Save(localFilePath);
    }

    private static void RemoveXmlFormulaAttributes(XElement cell, XNamespace spreadsheet)
    {
        cell.Attribute(spreadsheet + "Formula")?.Remove();
        cell.Attribute(spreadsheet + "ArrayRange")?.Remove();
    }

    private static void UpdateXmlTableExtents(XElement table, XNamespace spreadsheet)
    {
        var maxRow = 0;
        var maxColumn = 0;
        var rowIndex = 0;
        foreach (var row in table.Elements(spreadsheet + "Row"))
        {
            var explicitRowIndex = ReadSpreadsheetIndex(row, spreadsheet);
            if (explicitRowIndex.HasValue)
            {
                rowIndex = explicitRowIndex.Value - 1;
            }

            maxRow = Math.Max(maxRow, rowIndex + 1);
            var columnIndex = 0;
            foreach (var cell in row.Elements(spreadsheet + "Cell"))
            {
                var explicitColumnIndex = ReadSpreadsheetIndex(cell, spreadsheet);
                if (explicitColumnIndex.HasValue)
                {
                    columnIndex = explicitColumnIndex.Value - 1;
                }

                maxColumn = Math.Max(maxColumn, columnIndex + 1);
                columnIndex++;
            }

            rowIndex++;
        }

        if (maxRow > 0)
        {
            table.SetAttributeValue(spreadsheet + "ExpandedRowCount", maxRow.ToString());
        }

        if (maxColumn > 0)
        {
            table.SetAttributeValue(spreadsheet + "ExpandedColumnCount", maxColumn.ToString());
        }
    }

    private static XElement GetOrCreateWorksheet(XElement root, XNamespace spreadsheet, string sheetName)
    {
        var worksheet = root
            .Elements(spreadsheet + "Worksheet")
            .FirstOrDefault(element => string.Equals(element.Attribute(spreadsheet + "Name")?.Value, sheetName, StringComparison.Ordinal));
        if (worksheet != null)
        {
            return worksheet;
        }

        worksheet = new XElement(spreadsheet + "Worksheet", new XAttribute(spreadsheet + "Name", sheetName));
        root.Add(worksheet);
        return worksheet;
    }

    private static XElement GetOrCreateXmlRow(XElement table, XNamespace spreadsheet, int zeroBasedRow)
    {
        var rowIndex = 0;
        foreach (var row in table.Elements(spreadsheet + "Row"))
        {
            var explicitIndex = ReadSpreadsheetIndex(row, spreadsheet);
            if (explicitIndex.HasValue)
            {
                rowIndex = explicitIndex.Value - 1;
            }

            if (rowIndex == zeroBasedRow)
            {
                return row;
            }

            if (rowIndex > zeroBasedRow)
            {
                var created = new XElement(spreadsheet + "Row", new XAttribute(spreadsheet + "Index", zeroBasedRow + 1));
                row.AddBeforeSelf(created);
                return created;
            }

            rowIndex++;
        }

        var appended = new XElement(spreadsheet + "Row", new XAttribute(spreadsheet + "Index", zeroBasedRow + 1));
        table.Add(appended);
        return appended;
    }

    private static XElement GetOrCreateXmlCell(XElement row, XNamespace spreadsheet, int zeroBasedColumn)
    {
        var columnIndex = 0;
        foreach (var cell in row.Elements(spreadsheet + "Cell"))
        {
            var explicitIndex = ReadSpreadsheetIndex(cell, spreadsheet);
            if (explicitIndex.HasValue)
            {
                columnIndex = explicitIndex.Value - 1;
            }

            if (columnIndex == zeroBasedColumn)
            {
                return cell;
            }

            if (columnIndex > zeroBasedColumn)
            {
                var created = new XElement(spreadsheet + "Cell", new XAttribute(spreadsheet + "Index", zeroBasedColumn + 1));
                cell.AddBeforeSelf(created);
                return created;
            }

            columnIndex++;
        }

        var appended = new XElement(spreadsheet + "Cell", new XAttribute(spreadsheet + "Index", zeroBasedColumn + 1));
        row.Add(appended);
        return appended;
    }

    private static int? ReadSpreadsheetIndex(XElement element, XNamespace spreadsheet)
    {
        var value = element.Attribute(spreadsheet + "Index")?.Value;
        return int.TryParse(value, out var index) ? index : null;
    }

    private static string NormalizeXmlCellText(string value)
    {
        return (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string GuessXmlCellType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "String";
        }

        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? "Number"
            : "String";
    }
}

internal sealed class CrossRepositorySpreadsheetMergeForm : Form
{
    private readonly TextBox _baseFileText = new();
    private readonly TextBox _changedFileText = new();
    private readonly TextBox _targetFileText = new();

    public CrossRepositorySpreadsheetMergeForm(string defaultTargetFile)
    {
        Text = "跨库表格三方合并";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(780, 300);
        Size = new Size(900, 340);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(45, 55, 72),
            Text = "以 A 为基线，计算 A -> B 的改动，再合并到目标 C。C 中相对 A 独立修改过的单元格会保留；双方改同一格且结果不同会进入冲突选择表。",
        }, 0, 0);

        root.Controls.Add(CreatePathRow("A 改动前", _baseFileText, "选择 A"), 0, 1);
        root.Controls.Add(CreatePathRow("B 改动后", _changedFileText, "选择 B"), 0, 2);
        root.Controls.Add(CreatePathRow("C 目标文件", _targetFileText, "选择 C"), 0, 3);
        _targetFileText.Text = defaultTargetFile;

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = "支持 .xls / .xlsx / .xlsm / SpreadsheetML XML。开始后会先显示合并项目清单，确认写入前会自动备份 C。",
        };
        root.Controls.Add(hint, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var startButton = new Button { Text = "开始合并", Width = 96, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "取消", Width = 86, DialogResult = DialogResult.Cancel };
        startButton.Click += (_, args) =>
        {
            if (!ValidateInputPaths(out var message))
            {
                MessageBox.Show(this, message, "无法开始合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };
        buttons.Controls.Add(startButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 5);
        AcceptButton = startButton;
        CancelButton = cancelButton;
    }

    public string BaseFilePath => _baseFileText.Text.Trim();

    public string ChangedFilePath => _changedFileText.Text.Trim();

    public string TargetFilePath => _targetFileText.Text.Trim();

    private static Control CreatePathRow(string labelText, TextBox textBox, string buttonText)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 4, 8, 4);
        row.Controls.Add(textBox, 1, 0);

        var browseButton = new Button { Text = buttonText, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        browseButton.Click += (_, _) => BrowseSpreadsheetFile(textBox);
        row.Controls.Add(browseButton, 2, 0);
        return row;
    }

    private static void BrowseSpreadsheetFile(TextBox textBox)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "表格文件 (*.xml;*.xls;*.xlsx;*.xlsm)|*.xml;*.xls;*.xlsx;*.xlsm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        var current = textBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            textBox.Text = dialog.FileName;
        }
    }

    private bool ValidateInputPaths(out string message)
    {
        var paths = new[]
        {
            ("A 改动前", BaseFilePath),
            ("B 改动后", ChangedFilePath),
            ("C 目标文件", TargetFilePath),
        };

        foreach (var (name, path) in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                message = $"请选择 {name}。";
                return false;
            }

            if (!File.Exists(path))
            {
                message = $"{name} 不存在：{path}";
                return false;
            }

            if (!SpreadsheetThreeWayMergeService.IsSupportedPath(path))
            {
                message = $"{name} 不是支持的表格文件：{path}";
                return false;
            }
        }

        message = "";
        return true;
    }
}

internal sealed class SpreadsheetMergeConflictForm : Form
{
    private readonly SpreadsheetMergePlan _plan;
    private readonly List<SpreadsheetMergeConflictGridRow> _rows;
    private readonly BindingSource _source = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _detailTitleLabel = new();
    private readonly RichTextBox _baseDetailBox = new();
    private readonly RichTextBox _targetDetailBox = new();
    private readonly RichTextBox _sourceDetailBox = new();
    private readonly string _targetLabel;
    private readonly string _sourceLabel;
    private readonly string _localResolutionText;
    private readonly string _remoteResolutionText;

    public SpreadsheetMergeConflictForm(
        string relativePath,
        SpreadsheetMergePlan plan,
        string titlePrefix = "内置表格三方合并",
        string targetLabel = "本地",
        string sourceLabel = "远端 HEAD",
        string applyButtonText = "写入工作副本")
    {
        _plan = plan;
        _targetLabel = targetLabel;
        _sourceLabel = sourceLabel;
        _localResolutionText = string.Equals(targetLabel, "本地", StringComparison.Ordinal)
            ? "保留本地"
            : $"保留{targetLabel}";
        _remoteResolutionText = string.Equals(sourceLabel, "远端 HEAD", StringComparison.Ordinal)
            ? "使用远端"
            : $"使用{sourceLabel}";
        _rows = plan.AllChanges.Select(change => new SpreadsheetMergeConflictGridRow(change, _localResolutionText, _remoteResolutionText)).ToList();
        Text = $"{titlePrefix} - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1180, 700);
        Size = new Size(1400, 840);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.ForeColor = Color.FromArgb(45, 55, 72);
        root.Controls.Add(_summaryLabel, 0, 0);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(CreateDetailPanel(), 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var applyButton = new Button { Text = applyButtonText, Width = 118 };
        var cancelButton = new Button { Text = "取消", Width = 86, DialogResult = DialogResult.Cancel };
        var allRemoteButton = new Button { Text = $"全部选{sourceLabel}", Width = 120 };
        var allLocalButton = new Button { Text = $"全部选{targetLabel}", Width = 120 };
        applyButton.Click += (_, _) =>
        {
            if (ApplyRowsToPlan())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        allRemoteButton.Click += (_, _) => SetAll(_remoteResolutionText);
        allLocalButton.Click += (_, _) => SetAll(_localResolutionText);
        buttons.Controls.Add(applyButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(allRemoteButton);
        buttons.Controls.Add(allLocalButton);
        root.Controls.Add(buttons, 0, 3);
        AcceptButton = applyButton;
        CancelButton = cancelButton;
        UpdateSummary();
        UpdateMergeDetail();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _grid.GridColor = Color.FromArgb(226, 232, 240);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        _grid.RowTemplate.Height = 86;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.DataBindingComplete += (_, _) => ApplyRowStyles();
        _grid.SelectionChanged += (_, _) => UpdateMergeDetail();
        _grid.CellPainting += PaintMergeComparisonCell;
        _grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && _grid.Rows[args.RowIndex].DataBoundItem is SpreadsheetMergeConflictGridRow row)
            {
                ShowMergeCellDetail(row);
            }
        };
        _grid.CellValueChanged += (_, args) =>
        {
            if (args.RowIndex >= 0)
            {
                UpdateSummary();
                ApplyRowStyles();
                UpdateMergeDetail();
            }
        };
        _grid.DataError += (_, args) => args.ThrowException = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= _rows.Count)
            {
                return;
            }

            var row = _rows[args.RowIndex];
            args.ToolTipText =
                $"BASE：{row.BaseValue}{Environment.NewLine}" +
                $"{_targetLabel}：{row.LocalValue}{Environment.NewLine}" +
                $"{_sourceLabel}：{row.RemoteValue}";
        };

        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "选择",
            DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.Resolution),
            DataSource = new[] { _localResolutionText, _remoteResolutionText },
            Width = 108,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.KindText), Width = 108, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "默认位置", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.DefaultLocation), Width = 150, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入工作表", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.WriteSheet), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入单元格", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.WriteAddress), Width = 88 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.RowId), Width = 126, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.FieldName), Width = 144, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "合并对比（双击看完整内容）",
            Name = "MergeComparison",
            DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.ComparisonText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 420,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "BASE", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.BaseValue), Visible = false, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = _targetLabel, DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.LocalValue), Visible = false, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = _sourceLabel, DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.RemoteValue), Visible = false, ReadOnly = true });

        _source.DataSource = _rows;
        _grid.DataSource = _source;
    }

    private Control CreateDetailPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _detailTitleLabel.Dock = DockStyle.Fill;
        _detailTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _detailTitleLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _detailTitleLabel.ForeColor = Color.FromArgb(30, 41, 59);
        panel.Controls.Add(_detailTitleLabel, 0, 0);

        var boxes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.White,
        };
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        boxes.Controls.Add(CreateMergeDetailBox("BASE", _baseDetailBox, Color.FromArgb(71, 85, 105)), 0, 0);
        boxes.Controls.Add(CreateMergeDetailBox(_targetLabel, _targetDetailBox, Color.FromArgb(153, 27, 27)), 1, 0);
        boxes.Controls.Add(CreateMergeDetailBox(_sourceLabel, _sourceDetailBox, Color.FromArgb(22, 101, 52)), 2, 0);
        panel.Controls.Add(boxes, 0, 1);
        return panel;
    }

    private static Control CreateMergeDetailBox(string title, RichTextBox box, Color titleColor)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 8, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = title,
            ForeColor = titleColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        box.Dock = DockStyle.Fill;
        box.ReadOnly = true;
        box.WordWrap = true;
        box.ScrollBars = RichTextBoxScrollBars.Both;
        box.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
        box.BackColor = Color.White;
        box.Font = new Font("Consolas", 9F);
        box.DetectUrls = false;
        panel.Controls.Add(box, 0, 1);
        return panel;
    }

    private void UpdateMergeDetail()
    {
        var row = _grid.CurrentRow?.DataBoundItem as SpreadsheetMergeConflictGridRow ??
            _rows.FirstOrDefault();
        if (row == null)
        {
            _detailTitleLabel.Text = "未选择合并项目";
            SetMergeDetailText(_baseDetailBox, "", Color.FromArgb(71, 85, 105), Color.White, []);
            SetMergeDetailText(_targetDetailBox, "", Color.FromArgb(153, 27, 27), Color.White, []);
            SetMergeDetailText(_sourceDetailBox, "", Color.FromArgb(22, 101, 52), Color.White, []);
            return;
        }

        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        _detailTitleLabel.Text = $"{row.KindText}    {row.Sheet}!{row.Address}    写入 {row.WriteSheet}!{row.WriteAddress}    ID: {row.RowId}    字段: {row.FieldName}";
        SetMergeDetailText(_baseDetailBox, row.BaseValue, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240), []);
        SetMergeDetailText(_targetDetailBox, row.LocalValue, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), highlights.OldSpans);
        SetMergeDetailText(_sourceDetailBox, row.RemoteValue, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), highlights.NewSpans);
    }

    private static void SetMergeDetailText(RichTextBox box, string value, Color textColor, Color highlightColor, IReadOnlyList<TextHighlightSpan> highlights)
    {
        box.SuspendLayout();
        box.Text = value ?? "";
        box.SelectAll();
        box.SelectionColor = textColor;
        box.SelectionBackColor = Color.White;
        box.SelectionFont = new Font(box.Font, FontStyle.Regular);
        foreach (var span in highlights)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start >= box.TextLength)
            {
                continue;
            }

            var length = Math.Min(span.Length, box.TextLength - span.Start);
            box.Select(span.Start, length);
            box.SelectionBackColor = highlightColor;
            box.SelectionFont = new Font(box.Font, FontStyle.Bold);
        }

        box.Select(0, 0);
        box.ResumeLayout();
    }

    private void PaintMergeComparisonCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            args.Graphics == null ||
            grid.Columns[args.ColumnIndex].Name != "MergeComparison" ||
            grid.Rows[args.RowIndex].DataBoundItem is not SpreadsheetMergeConflictGridRow row)
        {
            return;
        }

        args.Handled = true;
        var cellStyle = args.CellStyle ?? grid.DefaultCellStyle;
        var selected = grid.Rows[args.RowIndex].Selected;
        var backColor = selected
            ? cellStyle.SelectionBackColor
            : grid.Rows[args.RowIndex].DefaultCellStyle.BackColor;
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);

        var bounds = Rectangle.Inflate(args.CellBounds, -8, -5);
        var lineHeight = Math.Max(21, bounds.Height / 3);
        var baseBounds = new Rectangle(bounds.Left, bounds.Top, bounds.Width, lineHeight);
        var targetBounds = new Rectangle(bounds.Left, bounds.Top + lineHeight, bounds.Width, lineHeight);
        var sourceBounds = new Rectangle(bounds.Left, bounds.Top + lineHeight * 2, bounds.Width, bounds.Height - lineHeight * 2);
        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        var basePreview = BuildFocusedPreview(row.BaseValue, [], 160);
        var targetPreview = BuildFocusedPreview(row.LocalValue, highlights.OldSpans, 170);
        var sourcePreview = BuildFocusedPreview(row.RemoteValue, highlights.NewSpans, 170);

        DrawMergeValueLine(args.Graphics, baseBounds, "BASE", basePreview.Text, basePreview.Spans, grid.Font, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240));
        DrawMergeValueLine(args.Graphics, targetBounds, _targetLabel, targetPreview.Text, targetPreview.Spans, grid.Font, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202));
        DrawMergeValueLine(args.Graphics, sourceBounds, _sourceLabel, sourcePreview.Text, sourcePreview.Spans, grid.Font, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208));

        using var borderPen = new Pen(Color.FromArgb(203, 213, 225));
        args.Graphics.DrawLine(borderPen, args.CellBounds.Left, args.CellBounds.Bottom - 1, args.CellBounds.Right, args.CellBounds.Bottom - 1);
    }

    private static (string Text, IReadOnlyList<TextHighlightSpan> Spans) BuildFocusedPreview(string value, IReadOnlyList<TextHighlightSpan> spans, int maxLength)
    {
        value ??= "";
        if (value.Length <= maxLength || spans.Count == 0)
        {
            return (value, spans);
        }

        var firstStart = spans.Min(span => Math.Max(0, span.Start));
        var lastEnd = spans.Max(span => Math.Min(value.Length, span.Start + span.Length));
        var context = Math.Max(24, (maxLength - Math.Max(8, lastEnd - firstStart)) / 2);
        var start = Math.Max(0, firstStart - context);
        var end = Math.Min(value.Length, lastEnd + context);
        if (end - start > maxLength)
        {
            end = Math.Min(value.Length, start + maxLength);
        }

        var prefix = start > 0 ? "... " : "";
        var suffix = end < value.Length ? " ..." : "";
        var text = prefix + value[start..end] + suffix;
        var adjusted = spans
            .Select(span =>
            {
                var spanStart = Math.Max(start, span.Start);
                var spanEnd = Math.Min(end, span.Start + span.Length);
                return spanEnd > spanStart
                    ? new TextHighlightSpan(prefix.Length + spanStart - start, spanEnd - spanStart)
                    : new TextHighlightSpan(-1, 0);
            })
            .Where(span => span.Start >= 0 && span.Length > 0)
            .ToList();
        return (text, adjusted);
    }

    private static void DrawMergeValueLine(
        Graphics graphics,
        Rectangle bounds,
        string label,
        string value,
        IReadOnlyList<TextHighlightSpan> highlights,
        Font font,
        Color textColor,
        Color highlightColor)
    {
        var labelBounds = new Rectangle(bounds.Left, bounds.Top + 2, 58, Math.Max(18, bounds.Height - 4));
        using var labelBrush = new SolidBrush(Color.FromArgb(28, textColor));
        using var labelPen = new Pen(Color.FromArgb(80, textColor));
        graphics.FillRoundedRectangle(labelBrush, labelBounds, 4);
        graphics.DrawRoundedRectangle(labelPen, labelBounds, 4);
        TextRenderer.DrawText(
            graphics,
            label,
            font,
            labelBounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var textBounds = new Rectangle(labelBounds.Right + 8, bounds.Top, Math.Max(1, bounds.Right - labelBounds.Right - 8), bounds.Height);
        DrawHighlightedMergeText(graphics, textBounds, value, highlights, font, textColor, highlightColor);
    }

    private static void DrawHighlightedMergeText(
        Graphics graphics,
        Rectangle bounds,
        string value,
        IReadOnlyList<TextHighlightSpan> highlights,
        Font font,
        Color textColor,
        Color highlightColor)
    {
        value ??= "";
        if (string.IsNullOrEmpty(value))
        {
            TextRenderer.DrawText(graphics, "(空)", font, bounds, Color.FromArgb(148, 163, 184), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var x = bounds.Left;
        var cursor = 0;
        foreach (var span in highlights.OrderBy(span => span.Start))
        {
            if (span.Start > cursor)
            {
                DrawMergeTextPart(graphics, ref x, bounds, value[cursor..span.Start], font, textColor, null);
            }

            var safeLength = Math.Min(span.Length, value.Length - span.Start);
            if (safeLength > 0)
            {
                DrawMergeTextPart(graphics, ref x, bounds, value.Substring(span.Start, safeLength), font, textColor, highlightColor);
            }

            cursor = Math.Max(cursor, span.Start + Math.Max(0, safeLength));
            if (x >= bounds.Right)
            {
                return;
            }
        }

        if (cursor < value.Length)
        {
            DrawMergeTextPart(graphics, ref x, bounds, value[cursor..], font, textColor, null);
        }
    }

    private static void DrawMergeTextPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var textSize = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(textSize.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top + 1, width, Math.Max(1, bounds.Height - 2));
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRoundedRectangle(brush, partBounds, 3);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }

    private void ShowMergeCellDetail(SpreadsheetMergeConflictGridRow row)
    {
        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        using var form = new Form
        {
            Text = $"合并项目详情 - {row.Sheet}!{row.Address}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(920, 560),
            Size = new Size(1100, 680),
            Font = Font,
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        form.Controls.Add(root);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Text = $"{row.KindText}    默认 {row.Sheet}!{row.Address}    写入 {row.WriteSheet}!{row.WriteAddress}    ID: {row.RowId}    字段: {row.FieldName}",
        }, 0, 0);
        root.Controls.Add(CreatePopupMergeValueBox("BASE", row.BaseValue, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240), []), 0, 1);
        root.Controls.Add(CreatePopupMergeValueBox(_targetLabel + "（红底为与来源不同的位置）", row.LocalValue, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), highlights.OldSpans), 0, 2);
        root.Controls.Add(CreatePopupMergeValueBox(_sourceLabel + "（绿底为与目标不同的位置）", row.RemoteValue, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), highlights.NewSpans), 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK });
        root.Controls.Add(buttons, 0, 4);
        form.AcceptButton = buttons.Controls.OfType<Button>().First();
        form.ShowDialog(this);
    }

    private static Control CreatePopupMergeValueBox(string title, string value, Color textColor, Color highlightColor, IReadOnlyList<TextHighlightSpan> highlights)
    {
        var box = new RichTextBox();
        var panel = (TableLayoutPanel)CreateMergeDetailBox(title, box, textColor);
        panel.Margin = new Padding(0, 0, 0, 8);
        SetMergeDetailText(box, value, textColor, highlightColor, highlights);
        return panel;
    }

    private void ApplyRowStyles()
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is not SpreadsheetMergeConflictGridRow row)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = row.Kind switch
            {
                SpreadsheetMergeChangeKind.AutoRemote => Color.FromArgb(235, 255, 239),
                SpreadsheetMergeChangeKind.LocalOnly => Color.FromArgb(239, 246, 255),
                SpreadsheetMergeChangeKind.SameBoth => Color.FromArgb(248, 250, 252),
                SpreadsheetMergeChangeKind.Conflict => Color.FromArgb(255, 247, 237),
                _ => Color.White,
            };
            gridRow.DefaultCellStyle.ForeColor = row.Kind == SpreadsheetMergeChangeKind.Conflict
                ? Color.FromArgb(124, 45, 18)
                : Color.FromArgb(30, 41, 59);
        }
    }

    private void SetAll(string resolution)
    {
        foreach (var row in _rows)
        {
            row.Resolution = resolution;
        }

        _source.ResetBindings(false);
        UpdateSummary();
        ApplyRowStyles();
        UpdateMergeDetail();
    }

    private bool ApplyRowsToPlan()
    {
        _grid.EndEdit();
        foreach (var row in _rows)
        {
            if (!row.TryApplyToChange(out var error))
            {
                MessageBox.Show(this, error, "写入位置无效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }

        var duplicateTarget = _plan.AllChanges
            .Where(change => change.Resolution == SpreadsheetMergeResolution.UseRemote)
            .Where(change => !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal))
            .GroupBy(change => change.WriteCell)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget != null)
        {
            var target = duplicateTarget.Key;
            var address = $"{target.Sheet}!{ExcelDiffService.ToColumnName(target.Column)}{target.Row + 1}";
            MessageBox.Show(
                this,
                $"有多个合并项目会写入同一个目标单元格：{address}{Environment.NewLine}{Environment.NewLine}请先手动调整其中一项的写入位置，避免覆盖顺序不明确。",
                "写入位置重复",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private void UpdateSummary()
    {
        var remoteCount = _rows.Count(row => row.Resolution == _remoteResolutionText);
        var plannedWrites = _rows.Count(row =>
            row.Resolution == _remoteResolutionText &&
            !string.Equals(row.LocalValue, row.RemoteValue, StringComparison.Ordinal));
        _summaryLabel.Text =
            $"可应用{_sourceLabel} {_plan.AutoRemoteChanges.Count} 项；{_targetLabel}独有 {_plan.LocalOnlyChanges.Count} 项；两边相同 {_plan.SameBothChanges.Count} 项；冲突 {_plan.Conflicts.Count} 项。{Environment.NewLine}" +
            $"当前选择{_sourceLabel} {remoteCount} 项、{_targetLabel} {_rows.Count - remoteCount} 项；预计写入 {plannedWrites} 个单元格。";
    }
}

internal sealed class SpreadsheetMergeConflictGridRow
{
    private readonly SpreadsheetMergeChange _change;
    private readonly string _remoteResolutionText;

    public SpreadsheetMergeConflictGridRow(SpreadsheetMergeChange change, string localResolutionText, string remoteResolutionText)
    {
        _change = change;
        _remoteResolutionText = remoteResolutionText;
        Resolution = change.Resolution == SpreadsheetMergeResolution.UseRemote
            ? remoteResolutionText
            : localResolutionText;
        WriteSheet = change.WriteCell.Sheet;
        WriteAddress = $"{ExcelDiffService.ToColumnName(change.WriteCell.Column)}{change.WriteCell.Row + 1}";
    }

    public string Resolution { get; set; }
    public string WriteSheet { get; set; }
    public string WriteAddress { get; set; }
    public SpreadsheetMergeChangeKind Kind => _change.Kind;
    public string KindText => _change.Kind switch
    {
        SpreadsheetMergeChangeKind.AutoRemote => "可合并改动",
        SpreadsheetMergeChangeKind.LocalOnly => "目标独有",
        SpreadsheetMergeChangeKind.SameBoth => "双方相同",
        SpreadsheetMergeChangeKind.Conflict => "冲突",
        _ => "未知",
    };
    public string Sheet => _change.Sheet;
    public string Address => _change.Address;
    public string RowId => _change.RowId;
    public string FieldName => _change.FieldName;
    public string BaseValue => _change.BaseValue;
    public string LocalValue => _change.LocalValue;
    public string RemoteValue => _change.RemoteValue;
    public string DefaultLocation => $"{Sheet}!{Address}";
    public string ComparisonText => $"BASE {BaseValue}{Environment.NewLine}{LocalValue}{Environment.NewLine}{RemoteValue}";

    public bool TryApplyToChange(out string error)
    {
        error = "";
        if (!TryParseCellAddress(WriteAddress, out var row, out var column))
        {
            error = $"写入单元格格式无效：{WriteAddress}{Environment.NewLine}{Environment.NewLine}请使用 A1、B23 这种格式。";
            return false;
        }

        var sheet = WriteSheet.Trim();
        if (string.IsNullOrWhiteSpace(sheet))
        {
            error = "写入工作表不能为空。";
            return false;
        }

        _change.Resolution = Resolution == _remoteResolutionText
            ? SpreadsheetMergeResolution.UseRemote
            : SpreadsheetMergeResolution.UseLocal;
        _change.WriteCell = new ExcelCellKey(sheet, row, column);
        return true;
    }

    private static bool TryParseCellAddress(string address, out int row, out int column)
    {
        row = -1;
        column = -1;
        var text = (address ?? "").Trim();
        var match = Regex.Match(text, @"^([A-Za-z]+)([1-9]\d*)$");
        if (!match.Success)
        {
            return false;
        }

        var columnText = match.Groups[1].Value.ToUpperInvariant();
        var value = 0;
        foreach (var character in columnText)
        {
            value = value * 26 + character - 'A' + 1;
        }

        if (!int.TryParse(match.Groups[2].Value, out var oneBasedRow))
        {
            return false;
        }

        row = oneBasedRow - 1;
        column = value - 1;
        return row >= 0 && column >= 0;
    }
}

internal sealed class ExcelDiffForm : Form
{
    public ExcelDiffForm(string relativePath, DiffPreviewData data)
        : this(relativePath, data.ExcelDifferences ?? [])
    {
    }

    public ExcelDiffForm(string relativePath, IReadOnlyList<ExcelCellDifference> differences)
    {
        Text = $"Excel 差异 - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1120, 680);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = differences.Count == 0 ? "没有发现单元格差异" : $"发现 {differences.Count} 个单元格差异",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(DiffPreviewData.FromExcel(differences).CreateView(), 0, 1);
    }

    public static Control CreateExcelDiffView(IReadOnlyList<ExcelCellDifference> differences)
    {
        var rows = differences.Select(ExcelUnifiedDiffRow.FromDifference).ToList();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 249, 250),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        var searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "搜索 ID / 字段 / 新旧值 / 单元格", Margin = new Padding(0, 4, 8, 4) };
        var idBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "只看 ID", Margin = new Padding(0, 4, 8, 4) };
        var fieldBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "只看字段", Margin = new Padding(0, 4, 8, 4) };
        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var copyButton = new Button { Text = "复制摘要", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var countLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        toolbar.Controls.Add(searchBox, 0, 0);
        toolbar.Controls.Add(idBox, 1, 0);
        toolbar.Controls.Add(fieldBox, 2, 0);
        toolbar.Controls.Add(clearButton, 3, 0);
        toolbar.Controls.Add(copyButton, 4, 0);
        toolbar.Controls.Add(countLabel, 5, 0);
        root.Controls.Add(toolbar, 0, 0);

        var grid = CreateExcelDiffGrid();
        root.Controls.Add(grid, 0, 1);
        var visibleRows = rows;

        void ApplyFilter()
        {
            var keyword = searchBox.Text.Trim();
            var id = idBox.Text.Trim();
            var field = fieldBox.Text.Trim();
            var filtered = rows.Where(row =>
                    (string.IsNullOrWhiteSpace(keyword) ||
                        row.Sheet.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.Address.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.FieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.RowId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.OldValue.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.NewValue.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(id) || row.RowId.Contains(id, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(field) || row.FieldName.Contains(field, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            visibleRows = filtered;
            grid.DataSource = filtered;
            countLabel.Text = $"{filtered.Count} / {rows.Count} 项";
        }

        searchBox.TextChanged += (_, _) => ApplyFilter();
        idBox.TextChanged += (_, _) => ApplyFilter();
        fieldBox.TextChanged += (_, _) => ApplyFilter();
        clearButton.Click += (_, _) =>
        {
            searchBox.Clear();
            idBox.Clear();
            fieldBox.Clear();
        };
        copyButton.Click += (_, _) => CopyExcelDiffSummary(root.FindForm(), visibleRows);
        ApplyFilter();
        return root;
    }

    public static DataGridView CreateExcelDiffGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.RowTemplate.Height = 58;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工作表", DataPropertyName = nameof(ExcelUnifiedDiffRow.Sheet), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = nameof(ExcelUnifiedDiffRow.RowId), Width = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(ExcelUnifiedDiffRow.FieldName), Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "差异内容",
            Name = "Difference",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.DifferenceText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 360,
        });
        grid.CellPainting += PaintExcelDiffCell;
        grid.DataBindingComplete += (_, _) => ApplyExcelRowStyles(grid);
        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not ExcelUnifiedDiffRow row)
            {
                return;
            }

            args.ToolTipText =
                $"工作表：{row.Sheet}{Environment.NewLine}" +
                $"单元格：{row.Address}{Environment.NewLine}" +
                $"字段：{row.FieldName}{Environment.NewLine}" +
                $"ID：{row.RowId}{Environment.NewLine}" +
                $"旧值：{row.OldValue}{Environment.NewLine}" +
                $"新值：{row.NewValue}";
        };
        grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && grid.Rows[args.RowIndex].DataBoundItem is ExcelUnifiedDiffRow row)
            {
                ShowExcelDiffDetail(grid.FindForm(), row);
            }
        };
        return grid;
    }

    private static void ApplyExcelRowStyles(DataGridView grid)
    {
        foreach (DataGridViewRow gridRow in grid.Rows)
        {
            if (gridRow.DataBoundItem is not ExcelUnifiedDiffRow row)
            {
                continue;
            }

            gridRow.Height = 58;
            gridRow.DefaultCellStyle.BackColor = row.ChangeKind switch
            {
                "Added" => Color.FromArgb(235, 255, 239),
                "Deleted" => Color.FromArgb(255, 239, 241),
                _ => Color.White,
            };
            gridRow.DefaultCellStyle.ForeColor = Color.FromArgb(30, 41, 59);
        }
    }

    private static void PaintExcelDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.Graphics == null ||
            grid.Columns[args.ColumnIndex].Name != "Difference" ||
            grid.Rows[args.RowIndex].DataBoundItem is not ExcelUnifiedDiffRow row)
        {
            return;
        }

        args.Handled = true;
        args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var bounds = Rectangle.Inflate(args.CellBounds, -8, -5);
        var cellStyle = args.CellStyle ?? grid.DefaultCellStyle;
        var font = cellStyle.Font ?? grid.Font;
        var isSelected = grid.Rows[args.RowIndex].Selected;
        var backColor = isSelected ? cellStyle.SelectionBackColor : grid.Rows[args.RowIndex].DefaultCellStyle.BackColor;
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);

        if (row.ChangeKind == "Added")
        {
            DrawValueLine(args.Graphics, bounds, "+", row.NewValue, -1, 0, font, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), strikeout: false);
        }
        else if (row.ChangeKind == "Deleted")
        {
            DrawValueLine(args.Graphics, bounds, "-", row.OldValue, -1, 0, font, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), strikeout: true);
        }
        else
        {
            var spans = DiffSpan.Calculate(row.OldValue, row.NewValue);
            var oldBounds = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height / 2);
            var newBounds = new Rectangle(bounds.Left, bounds.Top + bounds.Height / 2, bounds.Width, bounds.Height - bounds.Height / 2);
            DrawValueLine(args.Graphics, oldBounds, "-", row.OldValue, spans.OldStart, spans.OldLength, font, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), strikeout: true);
            DrawValueLine(args.Graphics, newBounds, "+", row.NewValue, spans.NewStart, spans.NewLength, font, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), strikeout: false);
        }

        using var borderPen = new Pen(Color.FromArgb(226, 232, 240));
        args.Graphics.DrawLine(borderPen, args.CellBounds.Left, args.CellBounds.Bottom - 1, args.CellBounds.Right, args.CellBounds.Bottom - 1);
    }

    private static void DrawValueLine(
        Graphics graphics,
        Rectangle bounds,
        string marker,
        string value,
        int highlightStart,
        int highlightLength,
        Font font,
        Color textColor,
        Color highlightColor,
        bool strikeout)
    {
        var lineFont = strikeout ? new Font(font, font.Style | FontStyle.Strikeout) : font;
        try
        {
            var markerBounds = new Rectangle(bounds.Left, bounds.Top, 22, bounds.Height);
            TextRenderer.DrawText(graphics, marker, font, markerBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var textBounds = new Rectangle(bounds.Left + 22, bounds.Top, bounds.Width - 22, bounds.Height);
            DrawSegmentedText(graphics, textBounds, value, highlightStart, highlightLength, lineFont, textColor, highlightColor);
        }
        finally
        {
            if (!ReferenceEquals(lineFont, font))
            {
                lineFont.Dispose();
            }
        }
    }

    private static void DrawSegmentedText(Graphics graphics, Rectangle bounds, string value, int highlightStart, int highlightLength, Font font, Color textColor, Color highlightColor)
    {
        value ??= "";
        highlightStart = Math.Clamp(highlightStart, -1, value.Length);
        highlightLength = Math.Clamp(highlightLength, 0, Math.Max(0, value.Length - Math.Max(0, highlightStart)));
        if (highlightStart < 0 || highlightLength == 0)
        {
            TextRenderer.DrawText(graphics, value, font, bounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        var prefix = value[..highlightStart];
        var highlight = value.Substring(highlightStart, highlightLength);
        var suffix = value[(highlightStart + highlightLength)..];
        var x = bounds.Left;
        DrawTextPart(graphics, ref x, bounds, prefix, font, textColor, null);
        DrawTextPart(graphics, ref x, bounds, highlight, font, textColor, highlightColor);
        DrawTextPart(graphics, ref x, bounds, suffix, font, textColor, null);
    }

    private static void DrawTextPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var textSize = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(textSize.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top, width, bounds.Height);
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRectangle(brush, partBounds);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }

    private static void ShowExcelDiffDetail(IWin32Window? owner, ExcelUnifiedDiffRow row)
    {
        var highlights = DiffHighlightSpans.Calculate(row.OldValue, row.NewValue);
        using var form = new Form
        {
            Text = $"单元格差异 - {row.Sheet} {row.Address}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(760, 460),
            Size = new Size(920, 560),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        form.Controls.Add(root);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Text = $"{row.Sheet} / {row.Address} / {row.FieldName} / ID: {row.RowId}    改动片段：旧 {highlights.OldSpans.Count} / 新 {highlights.NewSpans.Count}",
        }, 0, 0);
        root.Controls.Add(CreateValueBox("旧值（红底为改动位置）", row.OldValue, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), true, highlights.OldSpans), 0, 1);
        root.Controls.Add(CreateValueBox("新值（绿底为改动位置）", row.NewValue, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), false, highlights.NewSpans), 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK });
        root.Controls.Add(buttons, 0, 3);
        form.AcceptButton = buttons.Controls.OfType<Button>().First();
        form.ShowDialog(owner);
    }

    private static Control CreateValueBox(string title, string value, Color color, Color highlightColor, bool strikeout, IReadOnlyList<TextHighlightSpan> highlights)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = color,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = true,
            Font = new Font("Consolas", 10F, strikeout ? FontStyle.Strikeout : FontStyle.Regular),
            ForeColor = color,
            BackColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            DetectUrls = false,
            Text = value,
        };
        ApplyRichTextHighlights(box, color, highlightColor, strikeout, highlights);
        panel.Controls.Add(box, 0, 1);
        return panel;
    }

    private static void ApplyRichTextHighlights(RichTextBox box, Color textColor, Color highlightColor, bool strikeout, IReadOnlyList<TextHighlightSpan> highlights)
    {
        box.SelectAll();
        box.SelectionColor = textColor;
        box.SelectionBackColor = Color.White;
        box.SelectionFont = new Font(box.Font, strikeout ? box.Font.Style | FontStyle.Strikeout : box.Font.Style);

        foreach (var span in highlights)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start >= box.TextLength)
            {
                continue;
            }

            var length = Math.Min(span.Length, box.TextLength - span.Start);
            box.Select(span.Start, length);
            box.SelectionBackColor = highlightColor;
            box.SelectionFont = new Font(box.Font, box.Font.Style | FontStyle.Bold | (strikeout ? FontStyle.Strikeout : FontStyle.Regular));
        }

        box.Select(0, 0);
    }

    private static void CopyExcelDiffSummary(IWin32Window? owner, IReadOnlyList<ExcelUnifiedDiffRow> rows)
    {
        if (rows.Count == 0)
        {
            MessageBox.Show(owner, "当前筛选结果为空，没有可复制的差异。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Excel/XML 差异摘要：{rows.Count} 项");
        builder.AppendLine("状态\t工作表\t单元格\tID\t字段\t旧值\t新值");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join('\t',
                TranslateExcelChangeKind(row.ChangeKind),
                NormalizeClipboardCell(row.Sheet),
                NormalizeClipboardCell(row.Address),
                NormalizeClipboardCell(row.RowId),
                NormalizeClipboardCell(row.FieldName),
                NormalizeClipboardCell(row.OldValue),
                NormalizeClipboardCell(row.NewValue)));
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"复制失败：{ex.Message}", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(owner, $"已复制 {rows.Count} 项差异摘要。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string TranslateExcelChangeKind(string changeKind)
    {
        return changeKind switch
        {
            "Added" => "新增",
            "Deleted" => "删除",
            _ => "修改",
        };
    }

    private static string NormalizeClipboardCell(string value)
    {
        return (value ?? "")
            .Replace('\t', ' ')
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}

internal sealed record ExcelUnifiedDiffRow(string Sheet, string Address, string FieldName, string RowId, string OldValue, string NewValue)
{
    public string DifferenceText => $"{OldValue} -> {NewValue}";

    public string ChangeKind => string.IsNullOrEmpty(OldValue) && !string.IsNullOrEmpty(NewValue)
        ? "Added"
        : !string.IsNullOrEmpty(OldValue) && string.IsNullOrEmpty(NewValue)
            ? "Deleted"
            : "Modified";

    public static ExcelUnifiedDiffRow FromDifference(ExcelCellDifference difference)
    {
        return new ExcelUnifiedDiffRow(
            difference.Sheet,
            difference.Address,
            string.IsNullOrWhiteSpace(difference.FieldName) ? "(未命名字段)" : difference.FieldName,
            string.IsNullOrWhiteSpace(difference.RowId) ? "(无 ID)" : difference.RowId,
            difference.OldValue,
            difference.NewValue);
    }
}

internal sealed record DiffSpan(int OldStart, int OldLength, int NewStart, int NewLength)
{
    public static DiffSpan Calculate(string oldValue, string newValue)
    {
        oldValue ??= "";
        newValue ??= "";
        var prefix = 0;
        while (prefix < oldValue.Length &&
            prefix < newValue.Length &&
            oldValue[prefix] == newValue[prefix])
        {
            prefix++;
        }

        var oldEnd = oldValue.Length - 1;
        var newEnd = newValue.Length - 1;
        while (oldEnd >= prefix &&
            newEnd >= prefix &&
            oldValue[oldEnd] == newValue[newEnd])
        {
            oldEnd--;
            newEnd--;
        }

        return new DiffSpan(
            prefix,
            Math.Max(0, oldEnd - prefix + 1),
            prefix,
            Math.Max(0, newEnd - prefix + 1));
    }
}

internal sealed record TextHighlightSpan(int Start, int Length);

internal sealed record DiffHighlightSpans(IReadOnlyList<TextHighlightSpan> OldSpans, IReadOnlyList<TextHighlightSpan> NewSpans)
{
    public static DiffHighlightSpans Calculate(string oldValue, string newValue)
    {
        oldValue ??= "";
        newValue ??= "";
        var tokenSpans = CalculateKeyValueTokenSpans(oldValue, newValue);
        if (tokenSpans.OldSpans.Count > 0 || tokenSpans.NewSpans.Count > 0)
        {
            return tokenSpans;
        }

        var span = DiffSpan.Calculate(oldValue, newValue);
        return new DiffHighlightSpans(
            span.OldLength > 0 ? [new TextHighlightSpan(span.OldStart, span.OldLength)] : [],
            span.NewLength > 0 ? [new TextHighlightSpan(span.NewStart, span.NewLength)] : []);
    }

    private static DiffHighlightSpans CalculateKeyValueTokenSpans(string oldValue, string newValue)
    {
        var oldTokens = ParseKeyValueTokens(oldValue);
        var newTokens = ParseKeyValueTokens(newValue);
        if (oldTokens.Count < 2 && newTokens.Count < 2)
        {
            return new DiffHighlightSpans([], []);
        }

        if (HasDuplicateKeys(oldTokens) || HasDuplicateKeys(newTokens))
        {
            return new DiffHighlightSpans([], []);
        }

        var oldByKey = oldTokens.ToDictionary(token => token.Key, StringComparer.Ordinal);
        var newByKey = newTokens.ToDictionary(token => token.Key, StringComparer.Ordinal);
        var oldSpans = new List<TextHighlightSpan>();
        var newSpans = new List<TextHighlightSpan>();

        foreach (var oldToken in oldTokens)
        {
            if (!newByKey.TryGetValue(oldToken.Key, out var newToken))
            {
                oldSpans.Add(new TextHighlightSpan(oldToken.TokenStart, oldToken.TokenLength));
                continue;
            }

            if (!string.Equals(oldToken.Value, newToken.Value, StringComparison.Ordinal))
            {
                oldSpans.Add(new TextHighlightSpan(oldToken.ValueStart, oldToken.ValueLength));
                newSpans.Add(new TextHighlightSpan(newToken.ValueStart, newToken.ValueLength));
            }
        }

        foreach (var newToken in newTokens)
        {
            if (!oldByKey.ContainsKey(newToken.Key))
            {
                newSpans.Add(new TextHighlightSpan(newToken.TokenStart, newToken.TokenLength));
            }
        }

        return new DiffHighlightSpans(CoalesceSpans(oldSpans), CoalesceSpans(newSpans));
    }

    private static IReadOnlyList<KeyValueTextToken> ParseKeyValueTokens(string value)
    {
        var tokens = new List<KeyValueTextToken>();
        var start = 0;
        while (start <= value.Length)
        {
            var end = start;
            while (end < value.Length && value[end] is not ',' and not ';' and not '\r' and not '\n')
            {
                end++;
            }

            AddKeyValueToken(value, start, end, tokens);
            if (end >= value.Length)
            {
                break;
            }

            start = end + 1;
        }

        return tokens;
    }

    private static void AddKeyValueToken(string value, int start, int end, List<KeyValueTextToken> tokens)
    {
        var tokenStart = start;
        var tokenEnd = end;
        while (tokenStart < tokenEnd && char.IsWhiteSpace(value[tokenStart]))
        {
            tokenStart++;
        }

        while (tokenEnd > tokenStart && char.IsWhiteSpace(value[tokenEnd - 1]))
        {
            tokenEnd--;
        }

        if (tokenEnd <= tokenStart)
        {
            return;
        }

        var equalsIndex = value.IndexOf('=', tokenStart, tokenEnd - tokenStart);
        if (equalsIndex <= tokenStart)
        {
            return;
        }

        var key = value[tokenStart..equalsIndex].Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var valueStart = equalsIndex + 1;
        var valueEnd = tokenEnd;
        while (valueStart < valueEnd && char.IsWhiteSpace(value[valueStart]))
        {
            valueStart++;
        }

        while (valueEnd > valueStart && char.IsWhiteSpace(value[valueEnd - 1]))
        {
            valueEnd--;
        }

        tokens.Add(new KeyValueTextToken(
            key,
            value[valueStart..valueEnd],
            tokenStart,
            tokenEnd - tokenStart,
            valueStart,
            valueEnd - valueStart));
    }

    private static bool HasDuplicateKeys(IReadOnlyList<KeyValueTextToken> tokens)
    {
        return tokens.Select(token => token.Key).Distinct(StringComparer.Ordinal).Count() != tokens.Count;
    }

    private static IReadOnlyList<TextHighlightSpan> CoalesceSpans(IReadOnlyList<TextHighlightSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var ordered = spans
            .Where(span => span.Length > 0)
            .OrderBy(span => span.Start)
            .ToList();
        if (ordered.Count <= 1)
        {
            return ordered;
        }

        var result = new List<TextHighlightSpan>();
        var current = ordered[0];
        foreach (var next in ordered.Skip(1))
        {
            var currentEnd = current.Start + current.Length;
            if (next.Start <= currentEnd)
            {
                current = current with { Length = Math.Max(currentEnd, next.Start + next.Length) - current.Start };
                continue;
            }

            result.Add(current);
            current = next;
        }

        result.Add(current);
        return result;
    }
}

internal sealed record KeyValueTextToken(string Key, string Value, int TokenStart, int TokenLength, int ValueStart, int ValueLength);

internal sealed record ExcelCellKey(string Sheet, int Row, int Column);

internal sealed record ExcelCellDifference(string Sheet, int Row, int Column, string ColumnName, string FieldName, string RowId, string OldValue, string NewValue)
{
    public string Address => $"{ColumnName}{Row}";
}

internal static class SvnConflictArtifact
{
    public static bool IsAuxiliaryPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".mine", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string NormalizeToBasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.EndsWith(".mine", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^5];
        }

        var fileName = Path.GetFileName(path);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\.r\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? path[..^match.Value.Length] : path;
    }
}

internal static class DiffFileKindDetector
{
    public static bool IsSpreadsheet(string filePath)
    {
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(filePath);
        var extension = Path.GetExtension(comparablePath);
        if (string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var document = XDocument.Load(stream, LoadOptions.None);
            return document.Root?.Name.LocalName == "Workbook" &&
                document.Root.Name.NamespaceName == "urn:schemas-microsoft-com:office:spreadsheet";
        }
        catch
        {
            return false;
        }
    }
}

internal static class TextDiffService
{
    public static IReadOnlyList<TextDiffRow> Compare(string oldFilePath, string newFilePath)
    {
        return CreatePreview(oldFilePath, newFilePath).Differences;
    }

    public static TextDiffContent CreatePreview(string oldFilePath, string newFilePath)
    {
        var oldLines = ReadTextLines(oldFilePath);
        var newLines = ReadTextLines(newFilePath);
        var oldText = string.Join('\n', oldLines);
        var newText = string.Join('\n', newLines);
        return new TextDiffContent(
            oldText,
            newText,
            DetectLanguage(oldFilePath, newFilePath),
            "旧版本",
            "新版本",
            CompareLines(oldLines, newLines));
    }

    private static IReadOnlyList<TextDiffRow> CompareLines(string[] oldLines, string[] newLines)
    {
        var operations = (long)oldLines.Length * newLines.Length <= 2_000_000
            ? BuildAlignedOperations(oldLines, newLines)
            : BuildPositionalOperations(oldLines, newLines);
        return BuildHunks(operations);
    }

    private static List<TextDiffOperation> BuildAlignedOperations(string[] oldLines, string[] newLines)
    {
        var table = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var operations = new List<TextDiffOperation>();
        var oldLine = 0;
        var newLine = 0;
        while (oldLine < oldLines.Length && newLine < newLines.Length)
        {
            if (string.Equals(oldLines[oldLine], newLines[newLine], StringComparison.Ordinal))
            {
                operations.Add(TextDiffOperation.Context(oldLine + 1, newLine + 1, oldLines[oldLine]));
                oldLine++;
                newLine++;
            }
            else if (table[oldLine + 1, newLine] >= table[oldLine, newLine + 1])
            {
                operations.Add(TextDiffOperation.Removed(oldLine + 1, oldLines[oldLine]));
                oldLine++;
            }
            else
            {
                operations.Add(TextDiffOperation.Added(newLine + 1, newLines[newLine]));
                newLine++;
            }
        }

        while (oldLine < oldLines.Length)
        {
            operations.Add(TextDiffOperation.Removed(oldLine + 1, oldLines[oldLine]));
            oldLine++;
        }

        while (newLine < newLines.Length)
        {
            operations.Add(TextDiffOperation.Added(newLine + 1, newLines[newLine]));
            newLine++;
        }

        return operations;
    }

    private static List<TextDiffOperation> BuildPositionalOperations(string[] oldLines, string[] newLines)
    {
        var max = Math.Max(oldLines.Length, newLines.Length);
        var operations = new List<TextDiffOperation>();
        for (var index = 0; index < max; index++)
        {
            var hasOld = index < oldLines.Length;
            var hasNew = index < newLines.Length;
            var oldValue = hasOld ? oldLines[index] : "";
            var newValue = hasNew ? newLines[index] : "";
            if (hasOld && hasNew && string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                operations.Add(TextDiffOperation.Context(index + 1, index + 1, oldValue));
                continue;
            }

            if (hasOld)
            {
                operations.Add(TextDiffOperation.Removed(index + 1, oldValue));
            }

            if (hasNew)
            {
                operations.Add(TextDiffOperation.Added(index + 1, newValue));
            }
        }

        return operations;
    }

    private static IReadOnlyList<TextDiffRow> BuildHunks(IReadOnlyList<TextDiffOperation> operations)
    {
        var rows = new List<TextDiffRow>();
        const int contextLines = 2;
        var index = 0;
        while (index < operations.Count)
        {
            while (index < operations.Count && operations[index].Kind == "Context")
            {
                index++;
            }

            if (index >= operations.Count)
            {
                break;
            }

            var hunkStart = Math.Max(0, index - contextLines);
            var hunkEnd = index;
            var trailingContext = 0;
            for (var scan = index; scan < operations.Count; scan++)
            {
                if (operations[scan].Kind == "Context")
                {
                    trailingContext++;
                    if (trailingContext > contextLines)
                    {
                        hunkEnd = scan - trailingContext;
                        break;
                    }
                }
                else
                {
                    trailingContext = 0;
                    hunkEnd = scan;
                }
            }

            hunkEnd = Math.Min(operations.Count - 1, hunkEnd + contextLines);
            var firstLine = operations[hunkStart].OldLine ?? operations[hunkStart].NewLine ?? 1;
            rows.Add(TextDiffRow.Hunk(firstLine));
            for (var rowIndex = hunkStart; rowIndex <= hunkEnd; rowIndex++)
            {
                var operation = operations[rowIndex];
                switch (operation.Kind)
                {
                    case "Context":
                        rows.Add(TextDiffRow.Context(operation.OldLine ?? operation.NewLine ?? 0, operation.Content));
                        break;
                    case "Removed":
                        rows.Add(TextDiffRow.Removed(operation.OldLine ?? 0, operation.Content));
                        break;
                    case "Added":
                        rows.Add(TextDiffRow.Added(operation.NewLine ?? 0, operation.Content));
                        break;
                }
            }

            index = hunkEnd + 1;
        }

        ApplyInlineHighlights(rows);
        return rows;
    }

    private static void ApplyInlineHighlights(List<TextDiffRow> rows)
    {
        var index = 0;
        while (index < rows.Count)
        {
            if (rows[index].Kind != "Removed")
            {
                index++;
                continue;
            }

            var removedStart = index;
            while (index < rows.Count && rows[index].Kind == "Removed")
            {
                index++;
            }

            var addedStart = index;
            while (index < rows.Count && rows[index].Kind == "Added")
            {
                index++;
            }

            var pairCount = Math.Min(addedStart - removedStart, index - addedStart);
            for (var offset = 0; offset < pairCount; offset++)
            {
                var removedIndex = removedStart + offset;
                var addedIndex = addedStart + offset;
                var oldValue = StripDiffPrefix(rows[removedIndex].Content);
                var newValue = StripDiffPrefix(rows[addedIndex].Content);
                var span = DiffSpan.Calculate(oldValue, newValue);
                rows[removedIndex] = rows[removedIndex] with
                {
                    HighlightStart = 2 + span.OldStart,
                    HighlightLength = span.OldLength,
                };
                rows[addedIndex] = rows[addedIndex] with
                {
                    HighlightStart = 2 + span.NewStart,
                    HighlightLength = span.NewLength,
                };
            }
        }
    }

    private static string StripDiffPrefix(string content)
    {
        return content.Length >= 2 && (content[0] == '-' || content[0] == '+') && content[1] == ' '
            ? content[2..]
            : content;
    }

    public static string ReadText(string filePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(filePath);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private static string[] ReadTextLines(string filePath)
    {
        return ReadText(filePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string DetectLanguage(string oldFilePath, string newFilePath)
    {
        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(newFilePath));
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(oldFilePath));
        }

        return extension.ToLowerInvariant() switch
        {
            ".lua" => "lua",
            ".xml" => "xml",
            ".json" => "json",
            ".cs" => "csharp",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".css" => "css",
            ".html" or ".htm" => "html",
            ".md" => "markdown",
            ".sql" => "sql",
            ".txt" => "plaintext",
            _ => "plaintext",
        };
    }
}

internal sealed record TextDiffOperation(string Kind, int? OldLine, int? NewLine, string Content)
{
    public static TextDiffOperation Context(int oldLine, int newLine, string content) => new("Context", oldLine, newLine, content);

    public static TextDiffOperation Removed(int oldLine, string content) => new("Removed", oldLine, null, content);

    public static TextDiffOperation Added(int newLine, string content) => new("Added", null, newLine, content);
}

internal sealed record TextDiffContent(
    string OldText,
    string NewText,
    string Language,
    string OldLabel,
    string NewLabel,
    IReadOnlyList<TextDiffRow> Differences);

internal sealed record TextDiffRow(string Kind, string LineNumber, string Content)
{
    public int HighlightStart { get; init; } = -1;

    public int HighlightLength { get; init; }

    public string KindText => Kind switch
    {
        "Added" => "新增",
        "Removed" => "删除",
        "Context" => "上下文",
        "Hunk" => "变更块",
        _ => Kind,
    };

    public static TextDiffRow Hunk(int lineNumber) => new("Hunk", $"@@ line {lineNumber} @@", $"变更块：约第 {lineNumber} 行");

    public static TextDiffRow Context(int lineNumber, string content) => new("Context", lineNumber.ToString(), "  " + content);

    public static TextDiffRow Removed(int lineNumber, string content) => new("Removed", lineNumber.ToString(), "- " + content);

    public static TextDiffRow Added(int lineNumber, string content) => new("Added", lineNumber.ToString(), "+ " + content);
}

internal sealed record TextSideBySideRow(TextDiffRow? OldRow, TextDiffRow? NewRow)
{
    public string OldLine => OldRow?.Kind == "Hunk" ? "" : OldRow?.LineNumber ?? "";

    public string NewLine => NewRow?.Kind == "Hunk" ? "" : NewRow?.LineNumber ?? "";

    public string OldContent => OldRow?.Content ?? "";

    public string NewContent => NewRow?.Content ?? "";

    public bool IsHunk => OldRow?.Kind == "Hunk" || NewRow?.Kind == "Hunk";
}

internal sealed class TextDiffForm : Form
{
    public TextDiffForm(string title, DiffPreviewData data)
    {
        BuildContent(title, data.Summary, data.CreateView());
    }

    public TextDiffForm(string title, IReadOnlyList<TextDiffRow> differences)
    {
        BuildContent(
            title,
            differences.Count == 0 ? "没有发现文本差异" : $"发现 {differences.Count} 行文本差异",
            CreateTextDiffView(differences));
    }

    private void BuildContent(string title, string summary, Control content)
    {
        Text = $"文本差异 - {title}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1160, 720);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = summary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(content, 0, 1);
    }

    public static Control CreateTextDiffView(IReadOnlyList<TextDiffRow> differences)
    {
        var rows = differences.ToList();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 249, 250),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        var searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "搜索行号 / 内容", Margin = new Padding(0, 4, 8, 4) };
        var modeBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 4) };
        modeBox.Items.AddRange(new object[] { "全部", "只看改动", "只看新增", "只看删除" });
        modeBox.SelectedIndex = 0;
        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var countLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        toolbar.Controls.Add(searchBox, 0, 0);
        toolbar.Controls.Add(modeBox, 1, 0);
        toolbar.Controls.Add(clearButton, 2, 0);
        toolbar.Controls.Add(countLabel, 3, 0);
        root.Controls.Add(toolbar, 0, 0);

        var grid = CreateTextDiffGrid();
        root.Controls.Add(grid, 0, 1);

        void ApplyFilter()
        {
            var keyword = searchBox.Text.Trim();
            var mode = modeBox.SelectedItem?.ToString() ?? "全部";
            var filtered = rows.Where(row =>
                    MatchesTextMode(row, mode) &&
                    (string.IsNullOrWhiteSpace(keyword) ||
                        row.LineNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.KindText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            grid.DataSource = filtered;
            countLabel.Text = $"{filtered.Count} / {rows.Count} 行";
        }

        searchBox.TextChanged += (_, _) => ApplyFilter();
        modeBox.SelectedIndexChanged += (_, _) => ApplyFilter();
        clearButton.Click += (_, _) =>
        {
            searchBox.Clear();
            modeBox.SelectedIndex = 0;
        };
        ApplyFilter();
        return root;
    }

    public static Control CreateTextDiffView(TextDiffContent content)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(0, 2, 0, 2),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var unifiedButton = CreateDiffModeButton("统一视图");
        var splitButton = CreateDiffModeButton("双栏对比");
        var summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = $"{content.OldLabel}  ->  {content.NewLabel}",
        };
        var languageLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = $"语言：{content.Language}",
        };
        toolbar.Controls.Add(unifiedButton, 0, 0);
        toolbar.Controls.Add(splitButton, 1, 0);
        toolbar.Controls.Add(summaryLabel, 2, 0);
        toolbar.Controls.Add(languageLabel, 3, 0);
        root.Controls.Add(toolbar, 0, 0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        root.Controls.Add(host, 0, 1);

        void Show(Control control, Button activeButton)
        {
            Form1.ClearControlsDisposing(host);
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
            unifiedButton.Font = new Font(unifiedButton.Font, activeButton == unifiedButton ? FontStyle.Bold : FontStyle.Regular);
            splitButton.Font = new Font(splitButton.Font, activeButton == splitButton ? FontStyle.Bold : FontStyle.Regular);
            unifiedButton.BackColor = activeButton == unifiedButton ? Color.FromArgb(219, 234, 254) : Color.White;
            splitButton.BackColor = activeButton == splitButton ? Color.FromArgb(219, 234, 254) : Color.White;
        }

        unifiedButton.Click += (_, _) => Show(CreateTextDiffView(content.Differences), unifiedButton);
        splitButton.Click += (_, _) => Show(CreateSideBySideTextDiffView(content), splitButton);
        Show(CreateSideBySideTextDiffView(content), splitButton);
        return root;
    }

    private static Button CreateDiffModeButton(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
        };
    }

    private static Control CreateSideBySideTextDiffView(TextDiffContent content)
    {
        var rows = BuildSideBySideRows(content.Differences);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 241, 255);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = content.OldLabel,
            Name = "OldLine",
            DataPropertyName = nameof(TextSideBySideRow.OldLine),
            Width = 72,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "旧内容",
            Name = "OldContent",
            DataPropertyName = nameof(TextSideBySideRow.OldContent),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 260,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = content.NewLabel,
            Name = "NewLine",
            DataPropertyName = nameof(TextSideBySideRow.NewLine),
            Width = 72,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "新内容",
            Name = "NewContent",
            DataPropertyName = nameof(TextSideBySideRow.NewContent),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 260,
        });
        grid.DataSource = rows;
        grid.DataBindingComplete += (_, _) => ApplySideBySideRowStyles(grid);
        grid.CellPainting += PaintSideBySideTextDiffCell;
        return grid;
    }

    private static List<TextSideBySideRow> BuildSideBySideRows(IReadOnlyList<TextDiffRow> rows)
    {
        var result = new List<TextSideBySideRow>();
        var index = 0;
        while (index < rows.Count)
        {
            var row = rows[index];
            if (row.Kind == "Hunk")
            {
                result.Add(new TextSideBySideRow(row, row));
                index++;
                continue;
            }

            if (row.Kind == "Removed")
            {
                var removed = new List<TextDiffRow>();
                while (index < rows.Count && rows[index].Kind == "Removed")
                {
                    removed.Add(rows[index++]);
                }

                var added = new List<TextDiffRow>();
                while (index < rows.Count && rows[index].Kind == "Added")
                {
                    added.Add(rows[index++]);
                }

                var pairCount = Math.Max(removed.Count, added.Count);
                for (var pairIndex = 0; pairIndex < pairCount; pairIndex++)
                {
                    result.Add(new TextSideBySideRow(
                        pairIndex < removed.Count ? removed[pairIndex] : null,
                        pairIndex < added.Count ? added[pairIndex] : null));
                }

                continue;
            }

            if (row.Kind == "Added")
            {
                result.Add(new TextSideBySideRow(null, row));
                index++;
                continue;
            }

            result.Add(new TextSideBySideRow(row, row));
            index++;
        }

        return result;
    }

    private static string NormalizeSideBySideContent(TextDiffRow row)
    {
        return row.Content.Length >= 2 && (row.Content[0] == '-' || row.Content[0] == '+' || row.Content[0] == ' ') && row.Content[1] == ' '
            ? row.Content[2..]
            : row.Content;
    }

    private static void ApplySideBySideRowStyles(DataGridView grid)
    {
        foreach (DataGridViewRow gridRow in grid.Rows)
        {
            if (gridRow.DataBoundItem is not TextSideBySideRow row)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = row.IsHunk ? Color.FromArgb(241, 245, 249) : Color.White;
            gridRow.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            gridRow.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        }
    }

    private static void PaintSideBySideTextDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.Graphics == null ||
            grid.Rows[args.RowIndex].DataBoundItem is not TextSideBySideRow row)
        {
            return;
        }

        var columnName = grid.Columns[args.ColumnIndex].Name;
        var diffRow = columnName.StartsWith("Old", StringComparison.Ordinal) ? row.OldRow : row.NewRow;
        if (diffRow == null)
        {
            args.Handled = true;
            args.PaintBackground(args.ClipBounds, true);
            using var brush = new SolidBrush(Color.FromArgb(248, 250, 252));
            args.Graphics.FillRectangle(brush, args.CellBounds);
            return;
        }

        if (columnName is "OldLine" or "NewLine")
        {
            args.Handled = true;
            args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            var lineText = columnName == "OldLine" ? row.OldLine : row.NewLine;
            using var lineFont = new Font("Consolas", 9F);
            TextRenderer.DrawText(
                args.Graphics,
                lineText,
                lineFont,
                Rectangle.Inflate(args.CellBounds, -6, -1),
                Color.FromArgb(100, 116, 139),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        if (columnName is not "OldContent" and not "NewContent")
        {
            return;
        }

        args.Handled = true;
        var backColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(235, 255, 239),
            "Removed" => Color.FromArgb(255, 239, 241),
            "Hunk" => Color.FromArgb(241, 245, 249),
            _ => Color.White,
        };
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);
        args.Paint(args.CellBounds, (args.PaintParts & ~DataGridViewPaintParts.Background) & ~DataGridViewPaintParts.ContentForeground);
        var textColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(22, 101, 52),
            "Removed" => Color.FromArgb(153, 27, 27),
            "Hunk" => Color.FromArgb(71, 85, 105),
            _ => Color.FromArgb(30, 41, 59),
        };
        var highlightColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(187, 247, 208),
            "Removed" => Color.FromArgb(254, 202, 202),
            _ => Color.FromArgb(226, 232, 240),
        };
        using var font = new Font("Consolas", 9F, diffRow.Kind == "Removed" ? FontStyle.Strikeout : FontStyle.Regular);
        DrawTextDiffSegments(
            args.Graphics,
            Rectangle.Inflate(args.CellBounds, -8, -2),
            NormalizeSideBySideContent(diffRow),
            Math.Max(-1, diffRow.HighlightStart - 2),
            diffRow.HighlightLength,
            font,
            textColor,
            highlightColor);
    }

    private static bool MatchesTextMode(TextDiffRow row, string mode)
    {
        return mode switch
        {
            "只看新增" => row.Kind is "Hunk" or "Added",
            "只看删除" => row.Kind is "Hunk" or "Removed",
            "只看改动" => row.Kind is "Hunk" or "Added" or "Removed",
            _ => true,
        };
    }

    public static DataGridView CreateTextDiffGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = nameof(TextDiffRow.KindText), Width = 82 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "行号",
            DataPropertyName = nameof(TextDiffRow.LineNumber),
            Width = 110,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "内容",
            Name = "Content",
            DataPropertyName = nameof(TextDiffRow.Content),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 520,
        });
        grid.DataBindingComplete += (_, _) => ApplyTextRowStyles(grid);
        grid.CellPainting += PaintTextDiffCell;
        return grid;
    }

    private static void ApplyTextRowStyles(DataGridView grid)
    {
        foreach (DataGridViewRow gridRow in grid.Rows)
        {
            if (gridRow.DataBoundItem is not TextDiffRow row)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = row.Kind switch
            {
                "Added" => Color.FromArgb(235, 255, 239),
                "Removed" => Color.FromArgb(255, 239, 241),
                "Hunk" => Color.FromArgb(241, 245, 249),
                "Context" => Color.White,
                _ => Color.White,
            };
            gridRow.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            gridRow.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
            if (row.Kind == "Hunk")
            {
                gridRow.DefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
            }
        }
    }

    private static void PaintTextDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.Graphics == null ||
            grid.Columns[args.ColumnIndex].Name != "Content" ||
            grid.Rows[args.RowIndex].DataBoundItem is not TextDiffRow row)
        {
            return;
        }

        args.Handled = true;
        args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var bounds = Rectangle.Inflate(args.CellBounds, -8, -2);
        var font = new Font("Consolas", 9F, row.Kind == "Removed" ? FontStyle.Strikeout : FontStyle.Regular);
        try
        {
            var textColor = row.Kind switch
            {
                "Added" => Color.FromArgb(22, 101, 52),
                "Removed" => Color.FromArgb(153, 27, 27),
                "Hunk" => Color.FromArgb(71, 85, 105),
                _ => Color.FromArgb(30, 41, 59),
            };
            var highlightColor = row.Kind switch
            {
                "Added" => Color.FromArgb(187, 247, 208),
                "Removed" => Color.FromArgb(254, 202, 202),
                _ => Color.FromArgb(226, 232, 240),
            };
            DrawTextDiffSegments(args.Graphics, bounds, row.Content, row.HighlightStart, row.HighlightLength, font, textColor, highlightColor);
        }
        finally
        {
            font.Dispose();
        }
    }

    private static void DrawTextDiffSegments(Graphics graphics, Rectangle bounds, string value, int highlightStart, int highlightLength, Font font, Color textColor, Color highlightColor)
    {
        value ??= "";
        if (highlightStart < 0 || highlightLength <= 0 || highlightStart >= value.Length)
        {
            TextRenderer.DrawText(graphics, value, font, bounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        highlightLength = Math.Min(highlightLength, value.Length - highlightStart);
        var x = bounds.Left;
        DrawTextDiffPart(graphics, ref x, bounds, value[..highlightStart], font, textColor, null);
        DrawTextDiffPart(graphics, ref x, bounds, value.Substring(highlightStart, highlightLength), font, textColor, highlightColor);
        DrawTextDiffPart(graphics, ref x, bounds, value[(highlightStart + highlightLength)..], font, textColor, null);
    }

    private static void DrawTextDiffPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var size = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(size.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top, width, bounds.Height);
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRectangle(brush, partBounds);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }
}

internal sealed class ConflictFileSet
{
    public required string RelativePath { get; init; }
    public required string CurrentPath { get; init; }
    public required string MinePath { get; init; }
    public required string? BasePath { get; init; }
    public required string? ServerPath { get; init; }

    public static ConflictFileSet? Find(string workingCopyPath, string selectedRelativePath)
    {
        var baseRelativePath = NormalizeSelectedConflictPath(selectedRelativePath);
        var currentPath = Path.Combine(workingCopyPath, baseRelativePath);
        var directory = Path.GetDirectoryName(currentPath);
        var fileName = Path.GetFileName(currentPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var minePath = currentPath + ".mine";
        var revisionFiles = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, fileName + ".r*")
                .Select(path => new RevisionConflictFile(path, ParseRevisionSuffix(path)))
                .Where(file => file.Revision > 0)
                .OrderBy(file => file.Revision)
                .ToList()
            : [];

        if (!File.Exists(minePath) || revisionFiles.Count == 0)
        {
            return null;
        }

        var basePath = revisionFiles.Count >= 2 ? revisionFiles[0].Path : null;
        var serverPath = revisionFiles[^1].Path;
        return new ConflictFileSet
        {
            RelativePath = baseRelativePath,
            CurrentPath = currentPath,
            MinePath = minePath,
            BasePath = basePath,
            ServerPath = serverPath,
        };
    }

    private static string NormalizeSelectedConflictPath(string path)
    {
        var result = path;
        if (result.EndsWith(".mine", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^5];
        }
        else
        {
            var fileName = Path.GetFileName(result);
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\.r\d+$");
            if (match.Success)
            {
                result = result[..^match.Value.Length];
            }
        }

        return result;
    }

    private static long ParseRevisionSuffix(string path)
    {
        var fileName = Path.GetFileName(path);
        var marker = fileName.LastIndexOf(".r", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && long.TryParse(fileName[(marker + 2)..], out var revision) ? revision : 0;
    }

    private sealed record RevisionConflictFile(string Path, long Revision);
}

internal sealed class ConflictViewerForm : Form
{
    public ConflictViewerForm(ConflictFileSet conflict, Action<ConflictFileSet>? openExternalTool = null)
    {
        Text = $"冲突查看 - {conflict.RelativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1120, 680);
        Size = new Size(1280, 820);
        Font = new Font("Microsoft YaHei UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        tabs.TabPages.Add(CreateSummaryPage(conflict, openExternalTool));

        if (conflict.ServerPath != null)
        {
            tabs.TabPages.Add(CreateDiffPage("我的版本 vs 服务器版本", conflict.MinePath, conflict.ServerPath));
            if (File.Exists(conflict.CurrentPath))
            {
                tabs.TabPages.Add(CreateDiffPage("当前工作文件 vs 服务器版本", conflict.CurrentPath, conflict.ServerPath));
            }
        }

        if (conflict.BasePath != null && conflict.ServerPath != null)
        {
            tabs.TabPages.Add(CreateDiffPage("旧基础版本 vs 服务器版本", conflict.BasePath, conflict.ServerPath));
        }
    }

    private static TabPage CreateSummaryPage(ConflictFileSet conflict, Action<ConflictFileSet>? openExternalTool)
    {
        var page = new TabPage("版本文件");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = openExternalTool == null ? 1 : 2,
            Padding = new Padding(8),
        };
        if (openExternalTool != null)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        }

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        if (openExternalTool != null)
        {
            var button = new Button
            {
                Text = "用分久必合打开我的版本 vs 服务器版本",
                Dock = DockStyle.Left,
                Width = 260,
            };
            button.Click += (_, _) => openExternalTool(conflict);
            root.Controls.Add(button, 0, 0);
        }

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text =
                $"冲突文件：{conflict.RelativePath}{Environment.NewLine}{Environment.NewLine}" +
                $"当前工作文件：{conflict.CurrentPath}{Environment.NewLine}" +
                $"我的版本(.mine)：{conflict.MinePath}{Environment.NewLine}" +
                $"旧基础版本：{conflict.BasePath ?? "未找到"}{Environment.NewLine}" +
                $"服务器版本：{conflict.ServerPath ?? "未找到"}{Environment.NewLine}{Environment.NewLine}" +
                "这里只负责查看，不会修改或自动合并文件。你可以用外部合并工具处理后，再回到主界面标记冲突已解决。",
        };
        root.Controls.Add(text, 0, openExternalTool == null ? 0 : 1);
        return page;
    }

    private static TabPage CreateDiffPage(string title, string oldFilePath, string newFilePath)
    {
        var page = new TabPage(title);
        var diffControl = Form1.CreateDiffPreviewData(oldFilePath, newFilePath).CreateView();
        diffControl.Dock = DockStyle.Fill;
        page.Controls.Add(diffControl);
        return page;
    }

    public static DataGridView CreateExcelDiffGrid(IReadOnlyList<ExcelCellDifference> differences)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工作表", DataPropertyName = nameof(ExcelCellDifference.Sheet), Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单元格", DataPropertyName = nameof(ExcelCellDifference.Address), Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(ExcelCellDifference.FieldName), Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = nameof(ExcelCellDifference.RowId), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "旧值", DataPropertyName = nameof(ExcelCellDifference.OldValue), Width = 360 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新值", DataPropertyName = nameof(ExcelCellDifference.NewValue), Width = 360 });
        grid.DataSource = differences.ToList();
        return grid;
    }
}

internal sealed record WorkingCopyInfo(long Revision, long LastChangedRevision, long MinRevision, long MaxRevision, string Url)
{
    public static WorkingCopyInfo Empty { get; } = new(0, 0, 0, 0, "");

    public long CurrentContentRevision => MaxRevision > 0
        ? MaxRevision
        : Math.Max(Revision, LastChangedRevision);

    public bool IsMixedRevision => MinRevision > 0 && MaxRevision > 0 && MinRevision != MaxRevision;

    public string DisplayRevisionText => MinRevision > 0 && MaxRevision > 0 && MinRevision != MaxRevision
        ? $"r{MinRevision}:r{MaxRevision}（混合版本）"
        : $"r{CurrentContentRevision}";
}

internal sealed record ChangedFileEntry(string Action, string RepositoryPath, string RelativePath)
{
    public string DisplayText => string.IsNullOrWhiteSpace(RepositoryPath)
        ? $"{Action} {RelativePath}"
        : $"{Action} {RepositoryPath}";

    public string TreePath => !string.IsNullOrWhiteSpace(RelativePath)
        ? RelativePath
        : RepositoryPath.TrimStart('/');

    public static ChangedFileEntry FromWorkingCopy(SvnStatusKind status, string relativePath)
    {
        return new ChangedFileEntry(StatusAction(status), "", relativePath);
    }

    public static ChangedFileEntry ParseRepositoryPath(string line)
    {
        var trimmed = line.Trim();
        var action = trimmed.Length > 0 ? trimmed[0].ToString() : "?";
        var path = trimmed.Length > 1 ? trimmed[1..].Trim() : "";
        return new ChangedFileEntry(action, path, ToRelativePath(path));
    }

    private static string ToRelativePath(string repositoryPath)
    {
        var path = repositoryPath.TrimStart('/');
        return path.StartsWith("trunk/", StringComparison.OrdinalIgnoreCase)
            ? path["trunk/".Length..]
            : path;
    }

    private static string StatusAction(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Modified => "M",
            SvnStatusKind.Added => "A",
            SvnStatusKind.Deleted => "D",
            SvnStatusKind.Unversioned => "?",
            SvnStatusKind.Missing => "!",
            SvnStatusKind.Conflicted => "C",
            SvnStatusKind.Replaced => "R",
            _ => "?",
        };
    }
}

internal enum ChangedFilesFilterMode
{
    All,
    Xml,
    Lua,
    Conflict,
    Added,
    Deleted,
    Modified,
}

internal static class ChangedFilesFilter
{
    public static IReadOnlyList<string> Options { get; } =
    [
        "全部",
        "只看 XML",
        "只看 Lua",
        "只看冲突",
        "只看新增",
        "只看删除",
        "只看修改",
    ];

    public static ChangedFilesFilterMode GetMode(ComboBox combo)
    {
        return combo.SelectedIndex switch
        {
            1 => ChangedFilesFilterMode.Xml,
            2 => ChangedFilesFilterMode.Lua,
            3 => ChangedFilesFilterMode.Conflict,
            4 => ChangedFilesFilterMode.Added,
            5 => ChangedFilesFilterMode.Deleted,
            6 => ChangedFilesFilterMode.Modified,
            _ => ChangedFilesFilterMode.All,
        };
    }

    public static IReadOnlyList<ChangedFileEntry> Apply(
        IEnumerable<ChangedFileEntry> files,
        string searchText,
        ChangedFilesFilterMode mode)
    {
        var keyword = (searchText ?? "").Trim();
        return files
            .Where(file => MatchesMode(file, mode))
            .Where(file => string.IsNullOrWhiteSpace(keyword) || MatchesText(file, keyword))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SvnChange> ApplyStatusChanges(
        IEnumerable<SvnChange> changes,
        string searchText,
        ChangedFilesFilterMode mode)
    {
        var keyword = (searchText ?? "").Trim();
        return changes
            .Where(change => MatchesStatusMode(change, mode))
            .Where(change => string.IsNullOrWhiteSpace(keyword) || MatchesStatusText(change, keyword))
            .OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesMode(ChangedFileEntry file, ChangedFilesFilterMode mode)
    {
        return mode switch
        {
            ChangedFilesFilterMode.Xml => HasExtension(file, ".xml"),
            ChangedFilesFilterMode.Lua => HasExtension(file, ".lua"),
            ChangedFilesFilterMode.Conflict => string.Equals(file.Action, "C", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Added => string.Equals(file.Action, "A", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Deleted => string.Equals(file.Action, "D", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Modified => string.Equals(file.Action, "M", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static bool MatchesStatusMode(SvnChange change, ChangedFilesFilterMode mode)
    {
        return mode switch
        {
            ChangedFilesFilterMode.Xml => HasExtension(change.RelativePath, ".xml"),
            ChangedFilesFilterMode.Lua => HasExtension(change.RelativePath, ".lua"),
            ChangedFilesFilterMode.Conflict => change.Status == SvnStatusKind.Conflicted,
            ChangedFilesFilterMode.Added => change.Status is SvnStatusKind.Added or SvnStatusKind.Unversioned,
            ChangedFilesFilterMode.Deleted => change.Status is SvnStatusKind.Deleted or SvnStatusKind.Missing,
            ChangedFilesFilterMode.Modified => change.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced,
            _ => true,
        };
    }

    private static bool HasExtension(ChangedFileEntry file, string extension)
    {
        return HasExtension(file.TreePath, extension) ||
            HasExtension(file.RelativePath, extension) ||
            HasExtension(file.RepositoryPath, extension);
    }

    private static bool HasExtension(string path, string extension)
    {
        return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesText(ChangedFileEntry file, string keyword)
    {
        return file.DisplayText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.TreePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.RepositoryPath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStatusText(SvnChange change, string keyword)
    {
        return change.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            change.DisplayStatus.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            change.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HistorySearchFilter
{
    private HistorySearchFilter()
    {
    }

    public string Keyword { get; private init; } = "";
    public string FileKeyword { get; private init; } = "";
    public string Author { get; private init; } = "";
    public string IssueId { get; private init; } = "";
    public long? RevisionStart { get; private init; }
    public long? RevisionEnd { get; private init; }
    public bool HasRevisionRange => RevisionStart != null || RevisionEnd != null;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Keyword) &&
        string.IsNullOrWhiteSpace(FileKeyword) &&
        string.IsNullOrWhiteSpace(Author) &&
        string.IsNullOrWhiteSpace(IssueId) &&
        RevisionStart == null &&
        RevisionEnd == null;

    public static HistorySearchFilter Parse(string text)
    {
        var keywordParts = new List<string>();
        var file = "";
        var author = "";
        var issue = "";
        long? revisionStart = null;
        long? revisionEnd = null;

        foreach (var token in SplitTokens(text))
        {
            var parts = token.Split(':', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts[1].Trim();
                switch (key)
                {
                    case "file":
                    case "path":
                    case "文件":
                    case "路径":
                        file = value;
                        continue;
                    case "author":
                    case "作者":
                        author = value;
                        continue;
                    case "id":
                    case "需求":
                    case "需求号":
                    case "story":
                        issue = value.Trim('[', ']');
                        continue;
                    case "rev":
                    case "r":
                    case "版本":
                        ParseRevisionRange(value, out revisionStart, out revisionEnd);
                        continue;
                }
            }

            if (token.StartsWith("r", StringComparison.OrdinalIgnoreCase) && token.Length > 1 && char.IsDigit(token[1]))
            {
                ParseRevisionRange(token[1..], out revisionStart, out revisionEnd);
                continue;
            }

            keywordParts.Add(token);
        }

        return new HistorySearchFilter
        {
            Keyword = string.Join(' ', keywordParts).Trim(),
            FileKeyword = file,
            Author = author,
            IssueId = issue,
            RevisionStart = revisionStart,
            RevisionEnd = revisionEnd,
        };
    }

    public bool Matches(SvnLogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(Author) &&
            !log.Author.Contains(Author, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RevisionStart != null && log.Revision < RevisionStart.Value)
        {
            return false;
        }

        if (RevisionEnd != null && log.Revision > RevisionEnd.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(FileKeyword) &&
            !log.ChangedFiles.Any(MatchesFile))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(IssueId) &&
            !log.Message.Contains(IssueId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Keyword))
        {
            return true;
        }

        return log.Message.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.Author.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.RevisionText.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.LocalDateText.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.ChangedFiles.Any(file => file.DisplayText.Contains(Keyword, StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesFile(ChangedFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(FileKeyword))
        {
            return false;
        }

        return MatchesFileText(file, FileKeyword);
    }

    public bool MatchesFileForNavigation(ChangedFileEntry file)
    {
        if (!string.IsNullOrWhiteSpace(FileKeyword))
        {
            return MatchesFileText(file, FileKeyword);
        }

        return !string.IsNullOrWhiteSpace(Keyword) && MatchesFileText(file, Keyword);
    }

    private static bool MatchesFileText(ChangedFileEntry file, string text)
    {
        return file.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.TreePath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.RepositoryPath.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitTokens(string text)
    {
        return (text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void ParseRevisionRange(string value, out long? start, out long? end)
    {
        start = null;
        end = null;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            if (long.TryParse(parts[0].TrimStart('r', 'R'), out var revision))
            {
                start = revision;
                end = revision;
            }

            return;
        }

        if (long.TryParse(parts[0].TrimStart('r', 'R'), out var left))
        {
            start = left;
        }

        if (long.TryParse(parts[1].TrimStart('r', 'R'), out var right))
        {
            end = right;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }
    }
}

internal static class OperationLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNManager",
        "operation.log");

    public static void Log(string action, string workingCopy, string detail)
    {
        try
        {
            EnsureLogDirectory();
            var line = string.Join("\t",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                Environment.UserName,
                action,
                workingCopy,
                detail.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never block the SVN workflow.
        }
    }

    public static string EnsureLogFile()
    {
        EnsureLogDirectory();
        if (!File.Exists(LogPath))
        {
            File.WriteAllText(LogPath, "", Encoding.UTF8);
        }

        return LogPath;
    }

    private static void EnsureLogDirectory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }
}

internal sealed record SvnLogEntry(long Revision, string Author, DateTimeOffset Date, string Message)
{
    public bool IsUncommitted { get; init; }
    public bool IsWorkingCopyRevision { get; init; }
    public IReadOnlyList<ChangedFileEntry> ChangedFiles { get; init; } = [];

    public string GraphText => IsUncommitted ? "●" : IsWorkingCopyRevision ? "● ←" : "●";

    public string RevisionText => IsUncommitted ? "*" : Revision.ToString();

    public string DescriptionText
    {
        get
        {
            if (IsUncommitted)
            {
                return Message;
            }

            var marker = IsWorkingCopyRevision ? "[当前工作副本] " : "";
            return marker + ShortMessage;
        }
    }

    public string LocalDateText => Date == DateTimeOffset.MinValue
        ? ""
        : Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ShortMessage
    {
        get
        {
            var firstLine = Message
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
            return firstLine.Length <= 90 ? firstLine : firstLine[..90] + "...";
        }
    }
}

internal sealed record SvnChange(string RelativePath, SvnStatusKind Status)
{
    public bool CanCommit => Status is not SvnStatusKind.Conflicted and not SvnStatusKind.Missing;

    public string DisplayStatus => Status switch
    {
        SvnStatusKind.Modified => "已修改",
        SvnStatusKind.Added => "已新增",
        SvnStatusKind.Deleted => "已删除",
        SvnStatusKind.Unversioned => "未加入",
        SvnStatusKind.Missing => "本地缺失",
        SvnStatusKind.Conflicted => "冲突",
        SvnStatusKind.Replaced => "已替换",
        _ => "未知",
    };

    public string Description => Status switch
    {
        SvnStatusKind.Unversioned => "提交时会先执行 svn add",
        SvnStatusKind.Missing => "文件在本地缺失，暂不自动提交",
        SvnStatusKind.Conflicted => "需要先解决冲突",
        _ => "",
    };
}

internal enum SvnStatusKind
{
    None,
    Normal,
    Modified,
    Added,
    Deleted,
    Unversioned,
    Missing,
    Conflicted,
    Replaced,
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

internal sealed record CommitPreflightResult(IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool Blocked => Blockers.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public string BlockMessage => Blocked
        ? string.Join(Environment.NewLine, Blockers)
        : "";

    public string FormatMessage(string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine();
        if (Blockers.Count > 0)
        {
            builder.AppendLine("必须处理：");
            foreach (var blocker in Blockers)
            {
                builder.AppendLine("- " + blocker);
            }

            builder.AppendLine();
        }

        if (Warnings.Count > 0)
        {
            builder.AppendLine("需要确认：");
            foreach (var warning in Warnings)
            {
                builder.AppendLine("- " + warning);
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string ToLogText()
    {
        return $"blockers={Blockers.Count}; warnings={Warnings.Count}; {string.Join(" | ", Blockers.Concat(Warnings))}";
    }
}

internal sealed record ConflictGridRow(string RelativePath, string Description);

internal sealed record FileTreeNodeInfo(string RelativePath, bool IsFile);

internal sealed record FileTreeLoadRequest(
    string RootPath,
    string Search,
    bool ChangedOnly,
    bool IsFiltering,
    HashSet<string> ExpandedPaths);

internal sealed record FileTreeBuildResult(
    TreeNode? RootNode,
    string Message,
    int FileCount,
    bool IsTruncated,
    bool IsLazy,
    bool IsFiltering,
    HashSet<string> ExpandedPaths,
    IReadOnlyDictionary<string, SvnStatusKind> StatusMap);

internal sealed record FileTreeFileEntry(string RelativePath, string FullPath, SvnStatusKind Status);

internal sealed class LazyFileTreePlaceholder
{
    public static LazyFileTreePlaceholder Instance { get; } = new();

    private LazyFileTreePlaceholder()
    {
    }
}

internal static class WinFormsRendering
{
    private const int WmSetRedraw = 0x000B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void EnableDoubleBuffering(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(control, true);
    }

    public static void SetRedraw(Control control, bool enabled)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        SendMessage(control.Handle, WmSetRedraw, enabled ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        if (enabled)
        {
            control.Invalidate(true);
        }
    }

    public static void InvalidateTreeNodeRow(TreeView tree, TreeNode node)
    {
        if (!tree.IsHandleCreated)
        {
            return;
        }

        var bounds = node.Bounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        tree.Invalidate(new Rectangle(0, Math.Max(0, bounds.Top - 2), tree.ClientSize.Width, Math.Max(tree.ItemHeight + 4, bounds.Height + 4)));
    }

    public static void InvalidateListViewItems(ListView list, params ListViewItem?[] items)
    {
        if (!list.IsHandleCreated)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var bounds = item.Bounds;
            if (!bounds.IsEmpty)
            {
                list.Invalidate(new Rectangle(0, Math.Max(0, bounds.Top - 2), list.ClientSize.Width, bounds.Height + 4));
            }
        }
    }
}

internal sealed record IgnoreGroup(string ParentPath, IReadOnlyList<string> Names);

internal sealed record CleanupOptions(
    bool CleanWorkingCopyStatus,
    bool BreakWriteLocks,
    bool FixTimeStamps,
    bool VacuumPristineCopies,
    bool RefreshShellOverlays,
    bool IncludeExternals,
    bool DeleteUnversioned,
    bool DeleteIgnored,
    bool RevertAllRecursive)
{
    public bool HasDestructiveActions => DeleteUnversioned || DeleteIgnored || RevertAllRecursive;

    public string ToLogText()
    {
        return string.Join("; ", new[]
        {
            $"cleanStatus={CleanWorkingCopyStatus}",
            $"breakLocks={BreakWriteLocks}",
            $"fixTimeStamps={FixTimeStamps}",
            $"vacuumPristines={VacuumPristineCopies}",
            $"refreshOverlays={RefreshShellOverlays}",
            $"includeExternals={IncludeExternals}",
            $"deleteUnversioned={DeleteUnversioned}",
            $"deleteIgnored={DeleteIgnored}",
            $"revertAll={RevertAllRecursive}",
        });
    }
}

internal sealed class CleanupOptionsForm : Form
{
    private readonly CheckBox _cleanStatus = new() { Text = "清理工作副本状态", Checked = true, AutoSize = true };
    private readonly CheckBox _breakLocks = new() { Text = "解除写入锁", Checked = true, AutoSize = true };
    private readonly CheckBox _fixTimeStamps = new() { Text = "修复文件时间戳", Checked = true, AutoSize = true };
    private readonly CheckBox _vacuumPristines = new() { Text = "清理 .svn 内未使用的原始副本", Checked = true, AutoSize = true };
    private readonly CheckBox _refreshOverlays = new() { Text = "刷新资源管理器图标覆盖", Checked = true, AutoSize = true };
    private readonly CheckBox _includeExternals = new() { Text = "包含 externals 外部目录", Checked = true, AutoSize = true };
    private readonly CheckBox _deleteUnversioned = new() { Text = "删除未加入版本控制的文件和文件夹", AutoSize = true };
    private readonly CheckBox _deleteIgnored = new() { Text = "删除已忽略的文件和文件夹", AutoSize = true };
    private readonly CheckBox _revertAll = new() { Text = "递归还原所有本地改动", AutoSize = true };

    public CleanupOptions Options => new(
        _cleanStatus.Checked,
        _breakLocks.Checked,
        _fixTimeStamps.Checked,
        _vacuumPristines.Checked,
        _refreshOverlays.Checked,
        _includeExternals.Checked,
        _deleteUnversioned.Checked,
        _deleteIgnored.Checked,
        _revertAll.Checked);

    public CleanupOptionsForm(string workingCopyPath)
    {
        Text = "SVN 清理工作副本";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 560;
        Height = 410;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 14, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = workingCopyPath,
            AutoEllipsis = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        foreach (var checkBox in new[]
        {
            _cleanStatus,
            _breakLocks,
            _fixTimeStamps,
            _vacuumPristines,
            _refreshOverlays,
            _includeExternals,
            _deleteUnversioned,
            _deleteIgnored,
            _revertAll,
        })
        {
            checkBox.Margin = new Padding(0, 0, 0, 8);
            options.Controls.Add(checkBox);
        }

        root.Controls.Add(options, 0, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(110, 70, 20),
            Text = "删除和递归还原类选项默认关闭，执行前会再次确认；“时间戳/图标覆盖”属于 TortoiseSVN 体验项，本工具会尽量刷新自身状态。",
        };
        root.Controls.Add(hint, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var ok = new Button { Text = "确定", Width = 88, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 88, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}

internal sealed class RepositoryManagerForm : Form
{
    private readonly AppSettings _settings;
    private readonly SvnClient _svn;
    private readonly ListView _list = new();
    private readonly Label _summaryLabel = new();

    public RepositoryManagerForm(AppSettings settings, SvnClient svn)
    {
        _settings = settings;
        _svn = svn;
        Text = "本地库管理";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 460);
        Size = new Size(1080, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font(Font, FontStyle.Bold);
        root.Controls.Add(_summaryLabel, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("当前", 58);
        _list.Columns.Add("名称", 140);
        _list.Columns.Add("状态", 150);
        _list.Columns.Add("版本", 150);
        _list.Columns.Add("本地路径", 330);
        _list.Columns.Add("SVN 地址", 360);
        _list.DoubleClick += (_, _) => SetSelectedAsCurrent();
        root.Controls.Add(_list, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var closeButton = new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK };
        var refreshButton = new Button { Text = "刷新", Width = 86 };
        var openButton = new Button { Text = "打开目录", Width = 96 };
        var removeButton = new Button { Text = "移除", Width = 86 };
        var renameButton = new Button { Text = "重命名", Width = 86 };
        var currentButton = new Button { Text = "设为当前", Width = 96 };
        refreshButton.Click += (_, _) => RefreshRows();
        openButton.Click += (_, _) => OpenSelectedFolder();
        removeButton.Click += (_, _) => RemoveSelected();
        renameButton.Click += (_, _) => RenameSelected();
        currentButton.Click += (_, _) => SetSelectedAsCurrent();
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(openButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(renameButton);
        buttons.Controls.Add(currentButton);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = closeButton;
        RefreshRows();
    }

    public bool Changed { get; private set; }

    private RepositoryEntry? SelectedRepository
    {
        get
        {
            if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not string id)
            {
                return null;
            }

            return _settings.Repositories.FirstOrDefault(repository => repository.Id == id);
        }
    }

    private void RefreshRows()
    {
        var selectedId = SelectedRepository?.Id ?? _settings.CurrentRepositoryId;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var repository in _settings.Repositories.OrderBy(repository => repository.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var status = GetRepositoryStatus(repository, out var version);
                var item = new ListViewItem(repository.Id == _settings.CurrentRepositoryId ? "√" : "")
                {
                    Tag = repository.Id,
                    Font = repository.Id == _settings.CurrentRepositoryId ? new Font(_list.Font, FontStyle.Bold) : _list.Font,
                    ForeColor = status.StartsWith("正常", StringComparison.Ordinal)
                        ? SystemColors.WindowText
                        : Color.FromArgb(170, 65, 45),
                };
                item.SubItems.Add(repository.Name);
                item.SubItems.Add(status);
                item.SubItems.Add(version);
                item.SubItems.Add(repository.WorkingCopyPath);
                item.SubItems.Add(repository.RepositoryUrl);
                _list.Items.Add(item);
                if (repository.Id == selectedId)
                {
                    item.Selected = true;
                    item.Focused = true;
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _summaryLabel.Text = $"本地库管理    共 {_settings.Repositories.Count} 个库";
    }

    private string GetRepositoryStatus(RepositoryEntry repository, out string version)
    {
        version = "-";
        if (string.IsNullOrWhiteSpace(repository.WorkingCopyPath))
        {
            return "缺少本地路径";
        }

        if (!Directory.Exists(repository.WorkingCopyPath))
        {
            return "目录不存在";
        }

        if (!Directory.Exists(Path.Combine(repository.WorkingCopyPath, ".svn")))
        {
            return "不是 SVN 工作副本";
        }

        try
        {
            var info = _svn.GetWorkingCopyInfo(repository.WorkingCopyPath);
            version = info == WorkingCopyInfo.Empty ? "未知" : info.DisplayRevisionText;
            return "正常 SVN 工作副本";
        }
        catch
        {
            version = "读取失败";
            return "SVN 信息读取失败";
        }
    }

    private void SetSelectedAsCurrent()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        Changed = true;
        RefreshRows();
    }

    private void RenameSelected()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        using var prompt = new TextPromptForm("重命名本地库", "名称", repository.Name);
        if (prompt.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        repository.Name = prompt.Value;
        Changed = true;
        RefreshRows();
    }

    private void RemoveSelected()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        var message =
            $"确定从工具里移除这个本地库吗？{Environment.NewLine}{Environment.NewLine}" +
            $"{repository.Name}{Environment.NewLine}" +
            $"{repository.WorkingCopyPath}{Environment.NewLine}{Environment.NewLine}" +
            "这只会从工具列表移除，不会删除磁盘上的文件。";
        if (MessageBox.Show(this, message, "移除本地库", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _settings.RemoveRepository(repository);

        Changed = true;
        RefreshRows();
    }

    private void OpenSelectedFolder()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        if (!Directory.Exists(repository.WorkingCopyPath))
        {
            MessageBox.Show(this, "本地目录不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", repository.WorkingCopyPath) { UseShellExecute = true });
    }
}

internal sealed class EnvironmentCheckForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly IReadOnlyList<EnvironmentCheckItem> _items;

    public EnvironmentCheckForm(IReadOnlyList<EnvironmentCheckItem> items)
    {
        _items = items;
        Text = "环境检测";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 460);
        Size = new Size(980, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        var errors = items.Count(item => item.Level == EnvironmentCheckLevel.Error);
        var warnings = items.Count(item => item.Level == EnvironmentCheckLevel.Warning);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = errors > 0 ? Color.DarkRed : warnings > 0 ? Color.FromArgb(166, 103, 34) : Color.FromArgb(45, 100, 65),
            Text = errors == 0 && warnings == 0
                ? "环境检测通过"
                : $"环境检测发现 {errors} 个错误、{warnings} 个提醒",
        }, 0, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目", DataPropertyName = nameof(EnvironmentCheckItem.Name), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = nameof(EnvironmentCheckItem.Status), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", DataPropertyName = nameof(EnvironmentCheckItem.Detail), Width = 360, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "建议", DataPropertyName = nameof(EnvironmentCheckItem.Suggestion), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        _grid.DataSource = items.ToList();
        _grid.DataBindingComplete += (_, _) => ApplyRowStyles();
        root.Controls.Add(_grid, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var closeButton = new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK };
        buttons.Controls.Add(closeButton);
        root.Controls.Add(buttons, 0, 2);
        AcceptButton = closeButton;
    }

    private void ApplyRowStyles()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Index < 0 || row.Index >= _items.Count)
            {
                continue;
            }

            var item = _items[row.Index];
            row.DefaultCellStyle.ForeColor = item.Level switch
            {
                EnvironmentCheckLevel.Error => Color.DarkRed,
                EnvironmentCheckLevel.Warning => Color.FromArgb(166, 103, 34),
                _ => Color.FromArgb(45, 100, 65),
            };
        }
    }
}

internal sealed record EnvironmentCheckItem(string Name, string Status, string Detail, string Suggestion, EnvironmentCheckLevel Level)
{
    public static EnvironmentCheckItem Ok(string name, string status, string detail)
    {
        return new EnvironmentCheckItem(name, status, detail, "", EnvironmentCheckLevel.Ok);
    }

    public static EnvironmentCheckItem Warning(string name, string status, string suggestion)
    {
        return new EnvironmentCheckItem(name, status, "", suggestion, EnvironmentCheckLevel.Warning);
    }

    public static EnvironmentCheckItem Error(string name, string status, string suggestion)
    {
        return new EnvironmentCheckItem(name, status, "", suggestion, EnvironmentCheckLevel.Error);
    }
}

internal enum EnvironmentCheckLevel
{
    Ok,
    Warning,
    Error,
}

internal sealed class TextPromptForm : Form
{
    private readonly TextBox _text = new();

    public TextPromptForm(string title, string label, string initialValue)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Width = 460;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _text.Dock = DockStyle.Fill;
        _text.Text = initialValue;
        root.Controls.Add(_text, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var cancelButton = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "确定", Width = 80 };
        okButton.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Shown += (_, _) =>
        {
            _text.SelectAll();
            _text.Focus();
        };
    }

    public string Value => _text.Text.Trim();

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            MessageBox.Show(this, "名称不能为空。", "缺少名称", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class SettingsForm : Form
{
    private readonly TextBox _externalMergeToolText = new();

    public SettingsForm(AppSettings settings)
    {
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 220;
        MinimumSize = new Size(600, 200);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "分久必合软件位置",
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        _externalMergeToolText.Dock = DockStyle.Fill;
        _externalMergeToolText.Text = settings.ExternalMergeToolPath;
        pathRow.Controls.Add(_externalMergeToolText, 0, 0);

        var browseButton = new Button { Text = "选择", Dock = DockStyle.Fill };
        browseButton.Click += (_, _) => BrowseExternalMergeTool();
        pathRow.Controls.Add(browseButton, 1, 0);

        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill };
        clearButton.Click += (_, _) => _externalMergeToolText.Clear();
        pathRow.Controls.Add(clearButton, 2, 0);
        root.Controls.Add(pathRow, 0, 1);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottom.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "用于 XML / Excel 表格外部对比与合并。发布包不再内置该工具，需要每台电脑自行配置一次。",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.TopLeft,
        }, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var cancelButton = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "保存", Width = 80 };
        okButton.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        bottom.Controls.Add(buttons, 1, 0);
        root.Controls.Add(bottom, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    public string ExternalMergeToolPath => _externalMergeToolText.Text.Trim();

    private void BrowseExternalMergeTool()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择分久必合.exe",
            Filter = "分久必合.exe|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(_externalMergeToolText.Text) && File.Exists(_externalMergeToolText.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_externalMergeToolText.Text);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _externalMergeToolText.Text = dialog.FileName;
        }
    }

    private void SaveAndClose()
    {
        if (!string.IsNullOrWhiteSpace(ExternalMergeToolPath) && !File.Exists(ExternalMergeToolPath))
        {
            MessageBox.Show("分久必合路径不存在，请重新选择或清空。", "路径错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class FileTreeNodeSorter : System.Collections.IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not TreeNode left || y is not TreeNode right)
        {
            return 0;
        }

        var leftIsFile = IsFileNode(left);
        var rightIsFile = IsFileNode(right);
        if (leftIsFile != rightIsFile)
        {
            return leftIsFile ? 1 : -1;
        }

        return string.Compare(CleanNodeText(left.Text), CleanNodeText(right.Text), StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsFileNode(TreeNode node)
    {
        return node.Tag is ChangedFileEntry || node.Tag is FileTreeNodeInfo { IsFile: true };
    }

    private static string CleanNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "MAD?!CR".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }
}

internal sealed class ShellTabControl : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TcmAdjustRect && !DesignMode)
        {
            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (radius == 0)
        {
            graphics.FillRectangle(brush, bounds);
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (radius == 0)
        {
            graphics.DrawRectangle(pen, bounds);
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}

internal sealed class ShellNavButton : Control
{
    private bool _active;
    private bool _hovered;

    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string TabText { get; init; } = "";

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            Invalidate();
        }
    }

    public ShellNavButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        ForeColor = Color.FromArgb(213, 220, 230);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var background = Active
            ? Color.FromArgb(39, 50, 67)
            : _hovered ? Color.FromArgb(32, 41, 55) : Color.FromArgb(24, 31, 42);
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRoundedRectangle(backgroundBrush, new Rectangle(0, 0, Width - 1, Height - 1), 8);

        if (Active)
        {
            using var accentBrush = new SolidBrush(Color.FromArgb(88, 166, 255));
            e.Graphics.FillRoundedRectangle(accentBrush, new Rectangle(0, 10, 4, Height - 20), 3);
        }

        var glyphColor = Active ? Color.White : Color.FromArgb(175, 186, 202);
        var titleColor = Active ? Color.White : Color.FromArgb(197, 206, 220);
        using var titleFont = new Font("Microsoft YaHei UI", 9F, Active ? FontStyle.Bold : FontStyle.Regular);
        DrawShellIcon(e.Graphics, new Rectangle((Width - 24) / 2, 7, 24, 22), glyphColor);
        TextRenderer.DrawText(
            e.Graphics,
            Title,
            titleFont,
            new Rectangle(8, 30, Width - 16, 22),
            titleColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private void DrawShellIcon(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.7F) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var brush = new SolidBrush(color);
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        switch (Glyph)
        {
            case "CFG":
                graphics.DrawEllipse(pen, center.X - 5, center.Y - 5, 10, 10);
                for (var index = 0; index < 8; index++)
                {
                    var angle = index * Math.PI / 4;
                    var x1 = center.X + (int)(Math.Cos(angle) * 8);
                    var y1 = center.Y + (int)(Math.Sin(angle) * 8);
                    var x2 = center.X + (int)(Math.Cos(angle) * 10);
                    var y2 = center.Y + (int)(Math.Sin(angle) * 10);
                    graphics.DrawLine(pen, x1, y1, x2, y2);
                }

                break;
            case "STS":
                for (var index = 0; index < 3; index++)
                {
                    var y = bounds.Top + 4 + index * 7;
                    graphics.FillEllipse(brush, bounds.Left + 2, y - 1, 4, 4);
                    graphics.DrawLine(pen, bounds.Left + 10, y + 1, bounds.Right - 2, y + 1);
                }

                break;
            case "CNF":
                var triangle = new[]
                {
                    new Point(center.X, bounds.Top + 2),
                    new Point(bounds.Right - 2, bounds.Bottom - 2),
                    new Point(bounds.Left + 2, bounds.Bottom - 2),
                };
                graphics.DrawPolygon(pen, triangle);
                graphics.DrawLine(pen, center.X, bounds.Top + 8, center.X, bounds.Bottom - 8);
                graphics.FillEllipse(brush, center.X - 1, bounds.Bottom - 5, 3, 3);
                break;
            case "ALL":
                graphics.DrawRectangle(pen, bounds.Left + 5, bounds.Top + 2, 13, 17);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 7, bounds.Right - 7, bounds.Top + 7);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 11, bounds.Right - 5, bounds.Top + 11);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 15, bounds.Right - 8, bounds.Top + 15);
                break;
            case "HIS":
                graphics.DrawEllipse(pen, bounds.Left + 3, bounds.Top + 2, 18, 18);
                graphics.DrawLine(pen, center.X, center.Y, center.X, bounds.Top + 7);
                graphics.DrawLine(pen, center.X, center.Y, bounds.Right - 7, center.Y + 3);
                break;
            default:
                graphics.FillEllipse(brush, center.X - 4, center.Y - 4, 8, 8);
                break;
        }
    }
}

internal sealed class CommitPreviewForm : Form
{
    private readonly TextBox _messageBox = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _statusFilterBox = new();
    private readonly TreeView _commitTree = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _blockLabel = new();
    private readonly Label _messageStatsLabel = new();
    private readonly List<CommitPreviewRow> _rows;
    private readonly string? _globalBlockReason;
    private bool _syncingCommitTreeChecks;

    public string CommitMessage => _messageBox.Text.Trim();

    public IReadOnlyList<SvnChange> SelectedChanges => _rows
        .Where(row => row.Include && row.CanSubmit)
        .Select(row => row.Change)
        .ToList();

    public CommitPreviewForm(string message, IReadOnlyList<SvnChange> changes, Func<SvnChange, string> blockReasonFactory, string? globalBlockReason = null)
    {
        _globalBlockReason = globalBlockReason;
        _rows = changes
            .Select(change => new CommitPreviewRow(change, blockReasonFactory(change)))
            .ToList();

        Text = "准备提交";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 560);
        Size = new Size(1120, 680);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, string.IsNullOrWhiteSpace(globalBlockReason) ? 0 : 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _summaryLabel.ForeColor = Color.FromArgb(15, 23, 42);
        root.Controls.Add(_summaryLabel, 0, 0);

        _blockLabel.Dock = DockStyle.Fill;
        _blockLabel.TextAlign = ContentAlignment.MiddleLeft;
        _blockLabel.ForeColor = Color.FromArgb(185, 28, 28);
        _blockLabel.Font = new Font(Font, FontStyle.Bold);
        _blockLabel.Text = globalBlockReason ?? "";
        root.Controls.Add(_blockLabel, 0, 1);

        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
        };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var messagePanel = CreateCommitMessagePanel(message);
        messagePanel.Margin = new Padding(0, 0, 8, 0);
        var filesPanel = CreateCommitFilesPanel();
        filesPanel.Margin = new Padding(8, 0, 0, 0);
        contentLayout.Controls.Add(messagePanel, 0, 0);
        contentLayout.Controls.Add(filesPanel, 1, 0);
        root.Controls.Add(contentLayout, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = BackColor,
        };
        var okButton = new Button
        {
            Text = "确认提交",
            Width = 112,
            Height = 30,
            FlatStyle = FlatStyle.System,
        };
        var cancelButton = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.System,
        };
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                MessageBox.Show(this, "请先填写提交说明。", "缺少提交说明", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_globalBlockReason))
            {
                MessageBox.Show(this, _globalBlockReason, "提交被拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (SelectedChanges.Count == 0)
            {
                MessageBox.Show(this, "请至少保留一个要提交的文件。", "没有提交文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 3);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        ApplyFilter();
        UpdateCommitMessageStats();
        _messageBox.SelectAll();
    }

    private Control CreateCommitMessagePanel(string message)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "提交说明",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
        };
        panel.Controls.Add(title, 0, 0);

        _messageBox.Dock = DockStyle.Fill;
        _messageBox.Multiline = true;
        _messageBox.ReadOnly = false;
        _messageBox.ScrollBars = ScrollBars.Vertical;
        _messageBox.Text = message;
        _messageBox.BackColor = Color.White;
        _messageBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
        _messageBox.Font = new Font("Consolas", 10F);
        _messageBox.Margin = new Padding(0, 0, 0, 8);
        _messageBox.TextChanged += (_, _) => UpdateCommitMessageStats();
        panel.Controls.Add(_messageBox, 0, 1);

        _messageStatsLabel.Dock = DockStyle.Fill;
        _messageStatsLabel.TextAlign = ContentAlignment.MiddleLeft;
        _messageStatsLabel.ForeColor = Color.FromArgb(100, 116, 139);
        panel.Controls.Add(_messageStatsLabel, 0, 2);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "建议第一行写清本次改动目的；右侧可快速筛选、取消勾选不需要提交的文件。",
            ForeColor = Color.FromArgb(71, 85, 105),
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(10, 8, 10, 8),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(hint, 0, 3);
        return panel;
    }

    private Control CreateCommitFilesPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "待提交文件",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
        };
        panel.Controls.Add(title, 0, 0);

        var filterPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.PlaceholderText = "搜索文件 / 状态 / 说明";
        _searchBox.Margin = new Padding(0, 3, 8, 3);
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_searchBox, 0, 0);
        _statusFilterBox.Dock = DockStyle.Fill;
        _statusFilterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _statusFilterBox.Margin = new Padding(0, 3, 8, 3);
        _statusFilterBox.Items.AddRange(new object[] { "全部状态", "已修改", "已新增", "已删除", "未加入", "冲突", "不可提交" });
        _statusFilterBox.SelectedIndex = 0;
        _statusFilterBox.SelectedIndexChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_statusFilterBox, 1, 0);
        var selectAllButton = new Button { Text = "全选", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3), FlatStyle = FlatStyle.System };
        selectAllButton.Click += (_, _) => SetVisibleRowsIncluded(true);
        filterPanel.Controls.Add(selectAllButton, 2, 0);
        var selectNoneButton = new Button { Text = "全不选", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3), FlatStyle = FlatStyle.System };
        selectNoneButton.Click += (_, _) => SetVisibleRowsIncluded(false);
        filterPanel.Controls.Add(selectNoneButton, 3, 0);
        var clearButton = new Button { Text = "清空搜索", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), FlatStyle = FlatStyle.System };
        clearButton.Click += (_, _) =>
        {
            _searchBox.Clear();
            _statusFilterBox.SelectedIndex = 0;
        };
        filterPanel.Controls.Add(clearButton, 4, 0);
        panel.Controls.Add(filterPanel, 0, 1);

        _commitTree.Dock = DockStyle.Fill;
        _commitTree.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _commitTree.BackColor = Color.White;
        _commitTree.ForeColor = Color.FromArgb(30, 41, 59);
        _commitTree.CheckBoxes = true;
        _commitTree.HideSelection = false;
        _commitTree.HotTracking = false;
        _commitTree.ShowNodeToolTips = true;
        _commitTree.ShowLines = false;
        _commitTree.ShowPlusMinus = false;
        _commitTree.ShowRootLines = false;
        _commitTree.ItemHeight = 30;
        _commitTree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        WinFormsRendering.EnableDoubleBuffering(_commitTree);
        _commitTree.AfterCheck += CommitTreeAfterCheck;
        _commitTree.DrawNode += DrawCommitTreeNode;
        _commitTree.NodeMouseDoubleClick += (_, args) => args.Node.Toggle();
        panel.Controls.Add(_commitTree, 0, 2);
        return panel;
    }

    private void UpdateCommitMessageStats()
    {
        var trimmedLength = _messageBox.Text.Trim().Length;
        var lineCount = string.IsNullOrEmpty(_messageBox.Text) ? 0 : _messageBox.Lines.Length;
        _messageStatsLabel.Text = $"提交说明 {trimmedLength} 字 / {lineCount} 行";
        _messageStatsLabel.ForeColor = trimmedLength == 0
            ? Color.FromArgb(185, 28, 28)
            : Color.FromArgb(100, 116, 139);
    }

    private void ApplyFilter()
    {
        var keyword = _searchBox.Text.Trim();
        var statusFilter = _statusFilterBox.SelectedItem?.ToString() ?? "全部状态";
        var visibleRows = string.IsNullOrWhiteSpace(keyword)
            ? _rows
            : _rows.Where(row =>
                row.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Status.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.BlockReason.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        visibleRows = visibleRows.Where(row => MatchesCommitStatusFilter(row, statusFilter)).ToList();
        PopulateCommitTree(visibleRows);
        UpdateSummary();
    }

    private static bool MatchesCommitStatusFilter(CommitPreviewRow row, string statusFilter)
    {
        return statusFilter switch
        {
            "已修改" => row.Status == "已修改",
            "已新增" => row.Status == "已新增",
            "已删除" => row.Status == "已删除",
            "未加入" => row.Status == "未加入",
            "冲突" => row.Status == "冲突",
            "不可提交" => !row.CanSubmit,
            _ => true,
        };
    }

    private void SetVisibleRowsIncluded(bool include)
    {
        _syncingCommitTreeChecks = true;
        foreach (TreeNode node in FlattenCommitNodes(_commitTree.Nodes))
        {
            if (node.Tag is CommitPreviewRow row)
            {
                row.Include = include && row.CanSubmit && string.IsNullOrWhiteSpace(_globalBlockReason);
                node.Checked = row.Include;
            }
        }

        UpdateCommitFolderChecks(_commitTree.Nodes);
        _syncingCommitTreeChecks = false;
        _commitTree.Invalidate();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selectedCount = _rows.Count(row => row.Include);
        var blockedCount = _rows.Count(row => !row.CanSubmit);
        _summaryLabel.Text = blockedCount == 0
            ? $"准备提交 {selectedCount} / {_rows.Count} 个文件"
            : $"准备提交 {selectedCount} / {_rows.Count} 个文件，{blockedCount} 个文件不可提交";
    }

    private void PopulateCommitTree(IReadOnlyList<CommitPreviewRow> rows)
    {
        _syncingCommitTreeChecks = true;
        _commitTree.BeginUpdate();
        _commitTree.Nodes.Clear();

        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = row.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var collection = _commitTree.Nodes;
            var prefix = "";
            for (var index = 0; index < Math.Max(0, parts.Length - 1); index++)
            {
                prefix = string.IsNullOrEmpty(prefix) ? parts[index] : prefix + "/" + parts[index];
                if (!folders.TryGetValue(prefix, out var folderNode))
                {
                    folderNode = new TreeNode(parts[index])
                    {
                        Name = prefix,
                        Tag = CommitPreviewFolderNode.Instance,
                    };
                    collection.Add(folderNode);
                    folders[prefix] = folderNode;
                }

                collection = folderNode.Nodes;
            }

            var fileName = parts.Length == 0 ? row.RelativePath : parts[^1];
            var fileNode = new TreeNode(fileName)
            {
                Tag = row,
                Checked = row.Include,
                ToolTipText = string.IsNullOrWhiteSpace(row.BlockReason)
                    ? $"{row.Status}  {row.RelativePath}"
                    : $"{row.Status}  {row.RelativePath}\r\n{row.BlockReason}",
            };
            collection.Add(fileNode);
        }

        UpdateCommitFolderChecks(_commitTree.Nodes);
        ExpandCommitTree(_commitTree.Nodes, rows.Count <= 160);
        _commitTree.EndUpdate();
        _syncingCommitTreeChecks = false;
    }

    private void CommitTreeAfterCheck(object? sender, TreeViewEventArgs args)
    {
        if (_syncingCommitTreeChecks || args.Node == null)
        {
            return;
        }

        _syncingCommitTreeChecks = true;
        SetCommitNodeChecked(args.Node, args.Node.Checked);
        UpdateCommitFolderChecks(_commitTree.Nodes);
        _syncingCommitTreeChecks = false;
        _commitTree.Invalidate();
        UpdateSummary();
    }

    private void SetCommitNodeChecked(TreeNode node, bool include)
    {
        if (node.Tag is CommitPreviewRow row)
        {
            row.Include = include && row.CanSubmit && string.IsNullOrWhiteSpace(_globalBlockReason);
            node.Checked = row.Include;
            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            SetCommitNodeChecked(child, include);
        }
    }

    private static void UpdateCommitFolderChecks(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is CommitPreviewFolderNode)
            {
                UpdateCommitFolderChecks(node.Nodes);
                node.Checked = node.Nodes.Cast<TreeNode>().Any(child => child.Checked);
            }
        }
    }

    private static void ExpandCommitTree(TreeNodeCollection nodes, bool expandAll)
    {
        foreach (TreeNode node in nodes)
        {
            if (expandAll || node.Level < 1)
            {
                node.Expand();
            }

            ExpandCommitTree(node.Nodes, expandAll);
        }
    }

    private static IEnumerable<TreeNode> FlattenCommitNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in FlattenCommitNodes(node.Nodes))
            {
                yield return child;
            }
        }
    }

    private static void DrawCommitTreeNode(object? sender, DrawTreeNodeEventArgs args)
    {
        if (sender is not TreeView tree || args.Node == null)
        {
            return;
        }

        args.DrawDefault = false;
        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var selected = (args.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var hot = (args.State & TreeNodeStates.Hot) == TreeNodeStates.Hot;
        var fullBounds = new Rectangle(4, args.Bounds.Top + 2, Math.Max(1, tree.ClientSize.Width - 8), tree.ItemHeight - 4);
        var backColor = selected
            ? Color.FromArgb(226, 241, 255)
            : hot ? Color.FromArgb(248, 250, 252) : Color.White;
        using var backBrush = new SolidBrush(backColor);
        graphics.FillRoundedRectangle(backBrush, fullBounds, 7);

        var textBounds = args.Bounds;
        var x = textBounds.Left;
        if (args.Node.Nodes.Count > 0)
        {
            using var arrowFont = new Font("Segoe UI", 7F);
            TextRenderer.DrawText(
                graphics,
                args.Node.IsExpanded ? "▼" : "▶",
                arrowFont,
                new Rectangle(x, args.Bounds.Top + 6, 16, 16),
                Color.FromArgb(100, 116, 139),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 18;
        }

        if (args.Node.Tag is CommitPreviewRow row)
        {
            var statusColor = row.CanSubmit && string.IsNullOrWhiteSpace(row.BlockReason)
                ? row.Status switch
                {
                    "已新增" => Color.FromArgb(22, 163, 74),
                    "已删除" => Color.FromArgb(220, 38, 38),
                    "已修改" => Color.FromArgb(202, 138, 4),
                    "冲突" => Color.FromArgb(185, 28, 28),
                    _ => Color.FromArgb(71, 85, 105),
                }
                : Color.FromArgb(185, 28, 28);
            using var statusBrush = new SolidBrush(Color.FromArgb(24, statusColor));
            using var statusPen = new Pen(Color.FromArgb(80, statusColor));
            var badgeBounds = new Rectangle(x, args.Bounds.Top + 6, 44, 18);
            graphics.FillRoundedRectangle(statusBrush, badgeBounds, 5);
            graphics.DrawRoundedRectangle(statusPen, badgeBounds, 5);
            TextRenderer.DrawText(
                graphics,
                row.Status,
                tree.Font,
                badgeBounds,
                statusColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 52;

            var pathText = args.Node.Level == 0 ? row.RelativePath : args.Node.Text;
            using var fileFont = new Font(tree.Font, row.CanSubmit ? FontStyle.Regular : FontStyle.Strikeout);
            TextRenderer.DrawText(
                graphics,
                pathText,
                fileFont,
                new Rectangle(x, args.Bounds.Top + 3, Math.Max(1, tree.ClientSize.Width - x - 8), 22),
                row.CanSubmit ? Color.FromArgb(30, 41, 59) : Color.FromArgb(153, 27, 27),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        using var folderFont = new Font(tree.Font, FontStyle.Bold);
        TextRenderer.DrawText(
            graphics,
            args.Node.Text,
            folderFont,
            new Rectangle(x, args.Bounds.Top + 3, Math.Max(1, tree.ClientSize.Width - x - 8), 22),
            Color.FromArgb(51, 65, 85),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class CommitPreviewRow
{
    public CommitPreviewRow(SvnChange change, string blockReason)
    {
        Change = change;
        Status = change.DisplayStatus;
        RelativePath = change.RelativePath;
        Description = change.Description;
        BlockReason = blockReason;
        CanSubmit = string.IsNullOrWhiteSpace(blockReason);
        Include = CanSubmit;
    }

    public bool Include { get; set; }
    public bool CanSubmit { get; }
    public string Status { get; }
    public string RelativePath { get; }
    public string Description { get; }
    public string BlockReason { get; }
    public SvnChange Change { get; }
}

internal sealed class CommitPreviewFolderNode
{
    public static CommitPreviewFolderNode Instance { get; } = new();

    private CommitPreviewFolderNode()
    {
    }
}

internal sealed class FileHistoryForm : Form
{
    private readonly string _workingCopy;
    private readonly string _relativePath;
    private readonly IReadOnlyList<string> _relativePaths;
    private readonly string _displayPath;
    private readonly bool _enableFileDiffPreview;
    private readonly SvnClient _svn;
    private readonly Label _summaryLabel = new();
    private readonly TextBox _searchText = new();
    private readonly Label _searchScopeLabel = new();
    private readonly Button _deepSearchButton = new();
    private readonly Button _loadMoreButton = new();
    private readonly Button _clearSearchButton = new();
    private readonly ListView _historyList = new();
    private readonly ImageList _historyListRowImages = new();
    private readonly TextBox _detailText = new();
    private readonly Panel _diffPanel = new();
    private readonly TreeView _changedFilesTree = new();
    private readonly TextBox _changedFilesSearchText = new();
    private readonly ComboBox _changedFilesFilterCombo = new();
    private readonly ImageList _treeImages = new();
    private readonly Dictionary<string, DiffPreviewData> _diffPreviewCache = new(StringComparer.Ordinal);
    private List<SvnLogEntry> _logs;
    private IReadOnlyList<ChangedFileEntry> _changedFilesAll = [];
    private string _changedFilesRootText = "Changed files";
    private CancellationTokenSource? _diffPreviewCts;
    private int _loadedLimit = InitialHistoryLimit;
    private bool _loadingHistory;
    private ListViewItem? _hoveredHistoryItem;
    private const int InitialHistoryLimit = 80;
    private const int HistoryLoadMoreStep = 200;
    private const int HistoryDeepSearchLimit = 1000;
    private const int HistoryRevisionRangeLimit = 5000;
    private const int MaxFileHistoryDiffPreviewCacheEntries = 40;

    public FileHistoryForm(string workingCopy, string relativePath, IReadOnlyList<SvnLogEntry> logs, SvnClient svn)
        : this(workingCopy, [relativePath], logs, svn)
    {
    }

    public FileHistoryForm(string workingCopy, IReadOnlyList<string> relativePaths, IReadOnlyList<SvnLogEntry> logs, SvnClient svn)
    {
        _workingCopy = workingCopy;
        _relativePaths = relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        _relativePath = _relativePaths.Count == 1 ? _relativePaths[0] : "";
        _displayPath = FormatPathListLabel(_relativePaths);
        _enableFileDiffPreview = _relativePaths.Count == 1 && File.Exists(Path.Combine(workingCopy, _relativePath));
        _logs = logs.OrderByDescending(log => log.Revision).ToList();
        _loadedLimit = Math.Max(InitialHistoryLimit, _logs.Count);
        _svn = svn;
        Text = _enableFileDiffPreview ? $"文件历史 - {_displayPath}" : $"路径历史 - {_displayPath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1380, 880);
        Font = new Font("Microsoft YaHei UI", 9F);
        FormClosing += (_, _) => CancelDiffPreview();
        ConfigureTreeImages();

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        Controls.Add(root);
        Form1.BindSafeSplitterDistance(root, 250);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font(Font, FontStyle.Bold);
        top.Controls.Add(_summaryLabel, 0, 0);

        top.Controls.Add(CreateSearchPanel(), 0, 1);

        _historyList.Dock = DockStyle.Fill;
        _historyList.View = View.Details;
        _historyList.FullRowSelect = true;
        _historyList.GridLines = false;
        _historyList.HideSelection = false;
        _historyList.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _historyList.BackColor = Color.White;
        _historyList.OwnerDraw = true;
        WinFormsRendering.EnableDoubleBuffering(_historyList);
        _historyList.HeaderStyle = ColumnHeaderStyle.None;
        _historyListRowImages.ColorDepth = ColorDepth.Depth32Bit;
        _historyListRowImages.ImageSize = new Size(1, 54);
        _historyListRowImages.Images.Add("row-height", new Bitmap(1, 54));

        _historyList.SmallImageList = _historyListRowImages;
        _historyList.Columns.Add("Description", 620);
        _historyList.Columns.Add("Date", 150);
        _historyList.Columns.Add("Author", 120);
        _historyList.Columns.Add("Commit", 90);
        _historyList.DrawColumnHeader += (_, args) => args.DrawDefault = false;
        _historyList.DrawSubItem += DrawHistoryCardSubItem;
        _historyList.MouseMove += (_, args) =>
        {
            var previous = _hoveredHistoryItem;
            var item = _historyList.GetItemAt(args.X, args.Y);
            if (!ReferenceEquals(item, _hoveredHistoryItem))
            {
                _hoveredHistoryItem = item;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, item);
            }
        };
        _historyList.MouseLeave += (_, _) =>
        {
            if (_hoveredHistoryItem != null)
            {
                var previous = _hoveredHistoryItem;
                _hoveredHistoryItem = null;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, null);
            }
        };
        _historyList.SelectedIndexChanged += async (_, _) => await ShowSelectedFileRevisionAsync();
        _historyList.DoubleClick += (_, _) => FocusBestChangedFileInSelectedLog();
        top.Controls.Add(_historyList, 0, 2);
        root.Panel1.Controls.Add(top);

        var bottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        Form1.BindSafeSplitterDistance(bottom, 300);
        _detailText.Dock = DockStyle.Fill;
        _detailText.Multiline = true;
        _detailText.ReadOnly = true;
        _detailText.ScrollBars = ScrollBars.Both;
        _detailText.WordWrap = false;
        bottom.Panel1.Controls.Add(_detailText);

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        Form1.BindSafeSplitterDistance(right, 120);
        _changedFilesTree.Dock = DockStyle.Fill;
        _changedFilesTree.HideSelection = false;
        _changedFilesTree.FullRowSelect = true;
        _changedFilesTree.ShowLines = false;
        _changedFilesTree.ShowRootLines = false;
        _changedFilesTree.ItemHeight = 24;
        _changedFilesTree.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _changedFilesTree.ImageList = _treeImages;
        _changedFilesTree.TreeViewNodeSorter = new FileTreeNodeSorter();
        _changedFilesTree.AfterSelect += async (_, _) => await ShowSelectedChangedFileDiffAsync();
        right.Panel1.Controls.Add(Form1.CreateChangedFilesFilterPanel(
            "Changed files",
            _changedFilesTree,
            _changedFilesSearchText,
            _changedFilesFilterCombo,
            ApplyChangedFilesFilter));

        _diffPanel.Dock = DockStyle.Fill;
        right.Panel2.Controls.Add(CreateTitledPanel("Diff preview", _diffPanel));
        bottom.Panel2.Controls.Add(right);
        root.Panel2.Controls.Add(bottom);

        LoadHistoryRows();
    }

    private Control CreateSearchPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        _searchText.Dock = DockStyle.Fill;
        _searchText.Margin = new Padding(0, 4, 6, 4);
        _searchText.PlaceholderText = "搜索：author:作者 rev:57100-57120 id:需求号 或普通关键词";
        _searchText.TextChanged += (_, _) => ApplyHistoryFilter(selectFirst: false);
        _searchText.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
            {
                return;
            }

            args.SuppressKeyPress = true;
            await RunDeepHistorySearchAsync();
        };
        panel.Controls.Add(_searchText, 0, 0);

        _searchScopeLabel.Dock = DockStyle.Fill;
        _searchScopeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _searchScopeLabel.ForeColor = Color.FromArgb(90, 100, 115);
        panel.Controls.Add(_searchScopeLabel, 1, 0);

        ConfigureSearchButton(_deepSearchButton, "深度搜索");
        _deepSearchButton.Click += async (_, _) => await RunDeepHistorySearchAsync();
        panel.Controls.Add(_deepSearchButton, 2, 0);

        ConfigureSearchButton(_loadMoreButton, "加载更多");
        _loadMoreButton.Click += async (_, _) => await LoadMoreHistoryAsync();
        panel.Controls.Add(_loadMoreButton, 3, 0);

        ConfigureSearchButton(_clearSearchButton, "清空");
        _clearSearchButton.Click += (_, _) => _searchText.Clear();
        panel.Controls.Add(_clearSearchButton, 4, 0);

        return panel;
    }

    private static void ConfigureSearchButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 6, 4);
    }

    private void LoadHistoryRows()
    {
        UpdateSummaryLabel();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter(bool selectFirst = true)
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        var rows = filter.IsEmpty
            ? _logs
            : _logs.Where(log => filter.Matches(log)).ToList();
        var selectedRevisions = _historyList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnLogEntry)
            .Where(log => log != null)
            .Cast<SvnLogEntry>()
            .Select(log => log.Revision)
            .ToHashSet();
        ListViewItem? itemToSelect = null;
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var log in rows)
        {
            var item = new ListViewItem(log.ShortMessage) { Tag = log, ImageKey = "row-height" };
            item.SubItems.Add(log.LocalDateText);
            item.SubItems.Add(log.Author);
            item.SubItems.Add(log.RevisionText);
            _historyList.Items.Add(item);
            if (!selectFirst && selectedRevisions.Contains(log.Revision))
            {
                itemToSelect ??= item;
            }
        }
        _historyList.EndUpdate();

        UpdateSearchControls();
        if (_historyList.Items.Count == 0)
        {
            _detailText.Text = filter.IsEmpty
                ? ""
                : $"没有匹配的提交。当前只在已加载的 {_logs.Count} 条历史里搜索；可以点击“深度搜索”读取更早提交。";
            ClearChangedFiles();
            ShowDiffMessage(_detailText.Text);
            return;
        }

        itemToSelect ??= _historyList.Items[0];

        itemToSelect.Selected = true;
        itemToSelect.Focused = true;
        itemToSelect.EnsureVisible();
    }

    private void DrawHistoryCardSubItem(object? sender, DrawListViewSubItemEventArgs args)
    {
        args.DrawDefault = false;
        if (sender is not ListView list || args.Item?.Tag is not SvnLogEntry log || args.ColumnIndex != 0)
        {
            return;
        }

        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, args.Item.Bounds.Top + 5, Math.Max(1, list.ClientSize.Width - 12), args.Item.Bounds.Height - 8);
        var selected = args.Item.Selected;
        var hovered = ReferenceEquals(args.Item, _hoveredHistoryItem);
        var cardColor = selected
            ? Color.FromArgb(226, 241, 255)
            : hovered ? Color.FromArgb(248, 250, 252) : Color.White;
        var borderColor = selected
            ? Color.FromArgb(147, 197, 253)
            : hovered ? Color.FromArgb(203, 213, 225) : Color.FromArgb(226, 232, 240);
        using var cardBrush = new SolidBrush(cardColor);
        using var borderPen = new Pen(borderColor);
        using var revisionFont = new Font("Consolas", 9F, FontStyle.Bold);
        using var authorFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        using var metaFont = new Font("Microsoft YaHei UI", 8F);
        using var messageFont = new Font("Microsoft YaHei UI", 9F);
        graphics.FillRoundedRectangle(cardBrush, bounds, 8);
        graphics.DrawRoundedRectangle(borderPen, bounds, 8);

        using var accentBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        graphics.FillRoundedRectangle(accentBrush, new Rectangle(bounds.Left, bounds.Top + 9, 4, bounds.Height - 18), 3);

        TextRenderer.DrawText(
            graphics,
            log.RevisionText,
            revisionFont,
            new Rectangle(bounds.Left + 16, bounds.Top + 8, 82, 18),
            Color.FromArgb(37, 99, 235),
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.Author,
            authorFont,
            new Rectangle(bounds.Left + 104, bounds.Top + 8, 130, 18),
            Color.FromArgb(31, 41, 55),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.LocalDateText,
            metaFont,
            new Rectangle(bounds.Right - 150, bounds.Top + 8, 134, 18),
            Color.FromArgb(100, 116, 139),
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.ShortMessage,
            messageFont,
            new Rectangle(bounds.Left + 16, bounds.Top + 27, Math.Max(1, bounds.Width - 32), 20),
            Color.FromArgb(51, 65, 85),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void UpdateSummaryLabel()
    {
        _summaryLabel.Text = $"{_displayPath}    已加载 {_logs.Count} 条历史";
    }

    private void UpdateSearchControls()
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        _searchScopeLabel.Text = filter.IsEmpty
            ? $"已加载 {_logs.Count} 条"
            : $"匹配 {_historyList.Items.Count}/{_logs.Count} 条";
        _deepSearchButton.Enabled = !_loadingHistory;
        _loadMoreButton.Enabled = !_loadingHistory;
        _clearSearchButton.Enabled = !_loadingHistory && !string.IsNullOrWhiteSpace(_searchText.Text);
    }

    private async Task LoadMoreHistoryAsync()
    {
        await ReloadHistoryAsync(_loadedLimit + HistoryLoadMoreStep, "正在加载更多文件历史...");
    }

    private async Task RunDeepHistorySearchAsync()
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        if (filter.HasRevisionRange)
        {
            await LoadHistoryRevisionRangeAsync(filter);
            return;
        }

        await ReloadHistoryAsync(Math.Max(_loadedLimit, HistoryDeepSearchLimit), "正在深度搜索文件历史...");
    }

    private async Task ReloadHistoryAsync(int limit, string busyText)
    {
        SetHistoryBusy(true, busyText);
        try
        {
            var logs = await _svn.GetLogAsync(_workingCopy, _relativePaths, limit);
            _logs = logs.OrderByDescending(log => log.Revision).ToList();
            _loadedLimit = Math.Max(InitialHistoryLimit, limit);
            ClearDiffPreviewCache();
            LoadHistoryRows();
        }
        catch (Exception ex)
        {
            ShowDiffMessage(ex.Message);
            MessageBox.Show(ex.Message, "文件历史读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetHistoryBusy(false, "就绪");
        }
    }

    private async Task LoadHistoryRevisionRangeAsync(HistorySearchFilter filter)
    {
        if (filter.RevisionStart == null && filter.RevisionEnd == null)
        {
            return;
        }

        var start = filter.RevisionStart ?? filter.RevisionEnd!.Value;
        var end = filter.RevisionEnd ?? filter.RevisionStart!.Value;
        var rangeLimit = (int)Math.Min(HistoryRevisionRangeLimit, Math.Max(1, Math.Abs(end - start) + 1));
        SetHistoryBusy(true, $"正在读取版本范围 r{start}-r{end}...");
        try
        {
            var rangeLogs = await _svn.GetLogRangeAsync(_workingCopy, _relativePaths, start, end, rangeLimit);
            _logs = _logs
                .Concat(rangeLogs)
                .GroupBy(log => log.Revision)
                .Select(group => group.OrderByDescending(log => log.ChangedFiles.Count).First())
                .OrderByDescending(log => log.Revision)
                .ToList();
            ClearDiffPreviewCache();
            LoadHistoryRows();
            if (Math.Abs(end - start) + 1 > HistoryRevisionRangeLimit)
            {
                ShowDiffMessage($"版本范围过大，当前最多读取 {HistoryRevisionRangeLimit} 条。请缩小版本范围后再查。");
            }
        }
        catch (Exception ex)
        {
            ShowDiffMessage(ex.Message);
            MessageBox.Show(ex.Message, "文件历史读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetHistoryBusy(false, "就绪");
        }
    }

    private void SetHistoryBusy(bool busy, string message)
    {
        _loadingHistory = busy;
        UseWaitCursor = busy;
        _summaryLabel.Text = busy ? $"{_displayPath}    {message}" : $"{_displayPath}    已加载 {_logs.Count} 条历史";
        UpdateSearchControls();
    }

    private void ClearDiffPreviewCache()
    {
        CancelDiffPreview();
        _diffPreviewCache.Clear();
    }

    private async Task ShowSelectedFileRevisionAsync()
    {
        if (_historyList.SelectedItems.Count != 1 || _historyList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        _detailText.Text =
            $"版本：r{log.Revision}{Environment.NewLine}" +
            $"作者：{log.Author}{Environment.NewLine}" +
            $"时间：{log.LocalDateText}{Environment.NewLine}{Environment.NewLine}" +
            log.Message +
            Environment.NewLine + Environment.NewLine +
            $"路径：{_displayPath}{Environment.NewLine}{Environment.NewLine}" +
            $"Changed files ({log.ChangedFiles.Count}){Environment.NewLine}" +
            string.Join(Environment.NewLine, log.ChangedFiles.Select(file => file.DisplayText));

        PopulateChangedFilesTree(log);
        if (!_enableFileDiffPreview)
        {
            ShowChangedFilesHint();
            return;
        }

        var file = log.ChangedFiles.FirstOrDefault(change =>
            PathMatches(change.RelativePath, _relativePath) ||
            PathMatches(change.RepositoryPath.TrimStart('/'), _relativePath)) ??
            new ChangedFileEntry("M", "/trunk/" + _relativePath.Replace('\\', '/'), _relativePath);

        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(_relativePath));
        var oldTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_FILE_OLD_{Guid.NewGuid():N}{extension}");
        var newTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_FILE_NEW_{Guid.NewGuid():N}{extension}");
        var previewCts = BeginDiffPreview();
        var token = previewCts.Token;
        var title = $"r{log.Revision}  {_relativePath}";
        var cacheKey = BuildFileHistoryDiffCacheKey("file", log.Revision, file);
        if (TryRenderCachedDiff(title, cacheKey, token))
        {
            return;
        }

        ShowDiffLoading(title, "正在准备文件版本...");
        try
        {
            await Form1.PrepareCommittedDiffFilesAsync(_svn, _workingCopy, log.Revision, file, oldTemp, newTemp, token);
            await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowDiffMessage(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
        }
    }

    private void PopulateChangedFilesTree(SvnLogEntry log)
    {
        _changedFilesRootText = $"Changed files ({log.ChangedFiles.Count})";
        _changedFilesAll = log.ChangedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyChangedFilesFilter();
    }

    private void ClearChangedFiles()
    {
        _changedFilesAll = [];
        _changedFilesRootText = "Changed files";
        _changedFilesTree.Nodes.Clear();
    }

    private void ApplyChangedFilesFilter()
    {
        _changedFilesTree.BeginUpdate();
        _changedFilesTree.Nodes.Clear();
        try
        {
            var files = ChangedFilesFilter.Apply(
                _changedFilesAll,
                _changedFilesSearchText.Text,
                ChangedFilesFilter.GetMode(_changedFilesFilterCombo));
            var rootText = files.Count == _changedFilesAll.Count
                ? _changedFilesRootText
                : $"{_changedFilesRootText} - 显示 {files.Count}/{_changedFilesAll.Count}";
            var root = new TreeNode(rootText)
            {
                ImageKey = "folder",
                SelectedImageKey = "folder",
            };
            _changedFilesTree.Nodes.Add(root);
            foreach (var file in files)
            {
                AddChangedFileNode(root, file);
            }

            root.Expand();
            _changedFilesTree.Sort();
        }
        finally
        {
            _changedFilesTree.EndUpdate();
        }
    }

    private void FocusBestChangedFileInSelectedLog()
    {
        if (_historyList.SelectedItems.Count != 1 || _historyList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        if (_changedFilesTree.Nodes.Count == 0)
        {
            PopulateChangedFilesTree(log);
        }

        var filter = HistorySearchFilter.Parse(_searchText.Text);
        var target = FindBestChangedFileNode(_changedFilesTree.Nodes.Cast<TreeNode>(), filter);
        if (target == null)
        {
            return;
        }

        target.EnsureVisible();
        _changedFilesTree.SelectedNode = target;
        _changedFilesTree.Focus();
    }

    private static TreeNode? FindBestChangedFileNode(IEnumerable<TreeNode> nodes, HistorySearchFilter filter)
    {
        if (!filter.IsEmpty)
        {
            var matched = FindChangedFileNode(nodes, file => filter.MatchesFileForNavigation(file));
            if (matched != null)
            {
                return matched;
            }
        }

        return FindFirstChangedFileNode(nodes);
    }

    private static TreeNode? FindChangedFileNode(IEnumerable<TreeNode> nodes, Func<ChangedFileEntry, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry file && predicate(file))
            {
                return node;
            }

            var child = FindChangedFileNode(node.Nodes.Cast<TreeNode>(), predicate);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private static TreeNode? FindFirstChangedFileNode(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry)
            {
                return node;
            }

            var child = FindFirstChangedFileNode(node.Nodes.Cast<TreeNode>());
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private async Task ShowSelectedChangedFileDiffAsync()
    {
        if (_historyList.SelectedItems.Count != 1 ||
            _historyList.SelectedItems[0].Tag is not SvnLogEntry log ||
            _changedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(file.TreePath));
        var oldTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_PATH_OLD_{Guid.NewGuid():N}{extension}");
        var newTemp = Path.Combine(Path.GetTempPath(), $"SVNManager_PATH_NEW_{Guid.NewGuid():N}{extension}");
        var previewCts = BeginDiffPreview();
        var token = previewCts.Token;
        var title = $"r{log.Revision}  {file.DisplayText}";
        var cacheKey = BuildFileHistoryDiffCacheKey("changed-file", log.Revision, file);
        if (TryRenderCachedDiff(title, cacheKey, token))
        {
            return;
        }

        ShowDiffLoading(title, "正在准备文件版本...");
        try
        {
            await Form1.PrepareCommittedDiffFilesAsync(_svn, _workingCopy, log.Revision, file, oldTemp, newTemp, token);
            await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowDiffMessage(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
        }
    }

    private CancellationTokenSource BeginDiffPreview()
    {
        CancelDiffPreview();
        _diffPreviewCts = new CancellationTokenSource();
        return _diffPreviewCts;
    }

    private void CancelDiffPreview()
    {
        try
        {
            _diffPreviewCts?.Cancel();
        }
        catch
        {
        }
    }

    private bool TryRenderCachedDiff(string title, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_diffPreviewCache.TryGetValue(cacheKey, out var data))
        {
            return false;
        }

        Form1.RenderDiffPreviewInPanel(_diffPanel, null, title + "    [缓存]", data);
        return true;
    }

    private async Task ShowDiffPreviewAsync(string title, string oldFilePath, string newFilePath, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ShowDiffLoading(title, "正在计算差异...");
        var data = await Task.Run(() => Form1.CreateDiffPreviewData(oldFilePath, newFilePath), token);
        token.ThrowIfCancellationRequested();
        AddDiffPreviewCache(cacheKey, data);
        Form1.RenderDiffPreviewInPanel(_diffPanel, null, title, data);
    }

    private void AddDiffPreviewCache(string cacheKey, DiffPreviewData data)
    {
        if (_diffPreviewCache.Count >= MaxFileHistoryDiffPreviewCacheEntries &&
            !_diffPreviewCache.ContainsKey(cacheKey))
        {
            _diffPreviewCache.Remove(_diffPreviewCache.Keys.First());
        }

        _diffPreviewCache[cacheKey] = data;
    }

    private void ShowDiffLoading(string title, string message)
    {
        Form1.ClearControlsDisposing(_diffPanel);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(85, 95, 105),
        }, 0, 1);
        _diffPanel.Controls.Add(root);
    }

    private string BuildFileHistoryDiffCacheKey(string scope, long revision, ChangedFileEntry file)
    {
        return string.Join("|", scope, _workingCopy, revision.ToString(), file.Action, file.RepositoryPath, file.RelativePath, file.TreePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void ShowChangedFilesHint()
    {
        ShowDiffMessage("请选择右侧 Changed files 中的具体文件查看本次提交的内容差异。");
    }

    private void ShowDiffMessage(string message)
    {
        Form1.ClearControlsDisposing(_diffPanel);
        _diffPanel.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Text = message,
        });
    }

    private static Control CreateTitledPanel(string title, Control content)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(241, 243, 245),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.Controls.Add(content, 0, 1);
        return panel;
    }

    private void ConfigureTreeImages()
    {
        _treeImages.ColorDepth = ColorDepth.Depth32Bit;
        _treeImages.ImageSize = new Size(16, 16);
        _treeImages.Images.Clear();
        _treeImages.Images.Add("folder", CreateTreeIcon(Color.FromArgb(219, 164, 64), true));
        _treeImages.Images.Add("file", CreateTreeIcon(Color.FromArgb(118, 128, 140), false));
        _treeImages.Images.Add("xml", CreateTreeIcon(Color.FromArgb(39, 132, 85), false));
        _treeImages.Images.Add("lua", CreateTreeIcon(Color.FromArgb(72, 99, 180), false));
        _treeImages.Images.Add("changed", CreateTreeIcon(Color.FromArgb(209, 92, 56), false));
        _treeImages.Images.Add("action-added", CreateActionTreeIcon("A", Color.FromArgb(35, 134, 83)));
        _treeImages.Images.Add("action-modified", CreateActionTreeIcon("M", Color.FromArgb(184, 107, 25)));
        _treeImages.Images.Add("action-deleted", CreateActionTreeIcon("D", Color.FromArgb(184, 66, 66)));
        _treeImages.Images.Add("action-conflicted", CreateActionTreeIcon("C", Color.FromArgb(164, 62, 176)));
        _treeImages.Images.Add("action-replaced", CreateActionTreeIcon("R", Color.FromArgb(109, 85, 184)));
        _treeImages.Images.Add("action-unknown", CreateActionTreeIcon("?", Color.FromArgb(100, 116, 139)));
    }

    private static Bitmap CreateTreeIcon(Color color, bool folder)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(ControlPaint.Dark(color), 1);
        if (folder)
        {
            graphics.FillRectangle(brush, 2, 5, 12, 8);
            graphics.FillRectangle(brush, 3, 3, 5, 3);
            graphics.DrawRectangle(pen, 2, 5, 12, 8);
        }
        else
        {
            graphics.FillRectangle(brush, 4, 2, 8, 12);
            graphics.DrawRectangle(pen, 4, 2, 8, 12);
            graphics.DrawLine(Pens.White, 6, 5, 10, 5);
            graphics.DrawLine(Pens.White, 6, 8, 10, 8);
        }

        return bitmap;
    }

    private static Bitmap CreateActionTreeIcon(string text, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 1, 2, 14, 12);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (16 - size.Width) / 2F, (16 - size.Height) / 2F - 0.5F);
        return bitmap;
    }

    private static void AddChangedFileNode(TreeNode root, ChangedFileEntry file)
    {
        var path = file.TreePath;
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var isFile = index == parts.Length - 1;
            var existing = current.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(CleanNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new TreeNode(isFile ? $"{file.Action} {part}" : part)
                {
                    Tag = isFile ? file : null,
                    ToolTipText = file.DisplayText,
                    ImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                    SelectedImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                    ForeColor = isFile ? ActionColor(file.Action) : Color.FromArgb(55, 65, 81),
                };
                if (!isFile)
                {
                    existing.NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                }

                current.Nodes.Add(existing);
            }

            current = existing;
        }
    }

    private static string ChangedFileActionImageKey(string action)
    {
        return action switch
        {
            "A" => "action-added",
            "M" => "action-modified",
            "D" => "action-deleted",
            "C" => "action-conflicted",
            "R" => "action-replaced",
            _ => "action-unknown",
        };
    }

    private static string FileImageKey(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return "xml";
        }

        return extension.Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "lua" : "file";
    }

    private static Color ActionColor(string action)
    {
        return action switch
        {
            "A" => Color.FromArgb(38, 128, 72),
            "D" => Color.FromArgb(170, 67, 67),
            "M" => Color.FromArgb(166, 103, 34),
            "C" => Color.FromArgb(144, 65, 170),
            "R" => Color.FromArgb(128, 79, 160),
            _ => SystemColors.WindowText,
        };
    }

    private static string CleanNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "MAD?!CR".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }

    private static bool PathMatches(string candidate, string expected)
    {
        var normalizedCandidate = candidate.Replace('\\', '/').Trim('/');
        var normalizedExpected = expected.Replace('\\', '/').Trim('/');
        return string.Equals(normalizedCandidate, normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.EndsWith("/" + normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPathListLabel(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return "";
        }

        if (relativePaths.Count == 1)
        {
            return relativePaths[0];
        }

        return $"{relativePaths.Count} 个路径：" + string.Join("、", relativePaths.Take(3)) + (relativePaths.Count > 3 ? "..." : "");
    }
}

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNManager",
        "settings.json");

    public string RepositoryUrl { get; set; } = "";
    public string WorkingCopyPath { get; set; } = "";
    public string LastCommitMessage { get; set; } = "【策划配置】";
    public string ExternalMergeToolPath { get; set; } = "";
    public string? CurrentRepositoryId { get; set; }
    public List<RepositoryEntry> Repositories { get; set; } = [];
    public List<string> IgnoredWorkingCopyPaths { get; set; } = [];
    public List<string> FavoriteFileTreePaths { get; set; } = [];
    public Dictionary<string, List<string>> ExpandedFileTreePaths { get; set; } = [];
    public UiLayoutSettings UiLayout { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.IgnoredWorkingCopyPaths ??= [];
            settings.FavoriteFileTreePaths ??= [];
            settings.ExpandedFileTreePaths ??= [];
            settings.UiLayout ??= new UiLayoutSettings();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void MigrateLegacySettings()
    {
        Repositories.RemoveAll(repository => IsIgnoredWorkingCopy(repository.WorkingCopyPath));

        if (!string.IsNullOrWhiteSpace(WorkingCopyPath) &&
            !IsIgnoredWorkingCopy(WorkingCopyPath) &&
            Repositories.All(repository => !PathEquals(repository.WorkingCopyPath, WorkingCopyPath)))
        {
            var entry = RepositoryEntry.Create(RepositoryUrl, WorkingCopyPath);
            Repositories.Add(entry);
            CurrentRepositoryId ??= entry.Id;
        }
        else if (!string.IsNullOrWhiteSpace(WorkingCopyPath) && IsIgnoredWorkingCopy(WorkingCopyPath))
        {
            WorkingCopyPath = "";
            RepositoryUrl = "";
        }

        if (CurrentRepositoryId != null &&
            Repositories.All(repository => !string.Equals(repository.Id, CurrentRepositoryId, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentRepositoryId = Repositories.FirstOrDefault()?.Id;
        }

        if (CurrentRepositoryId == null && Repositories.Count > 0)
        {
            CurrentRepositoryId = Repositories[0].Id;
        }

        Save();
    }

    public void AddKnownWorkingCopyIfExists(string name, string repositoryUrl, string workingCopyPath)
    {
        if (IsIgnoredWorkingCopy(workingCopyPath) ||
            !Directory.Exists(Path.Combine(workingCopyPath, ".svn")) ||
            Repositories.Any(repository => PathEquals(repository.WorkingCopyPath, workingCopyPath)))
        {
            return;
        }

        var entry = RepositoryEntry.Create(repositoryUrl, workingCopyPath);
        entry.Name = name;
        Repositories.Add(entry);
        CurrentRepositoryId ??= entry.Id;
        Save();
    }

    public RepositoryEntry? GetCurrentRepository()
    {
        return Repositories.FirstOrDefault(repository => repository.Id == CurrentRepositoryId) ??
            Repositories.FirstOrDefault();
    }

    public void UpsertRepository(string repositoryUrl, string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        UnignoreWorkingCopy(workingCopyPath);
        var existing = Repositories.FirstOrDefault(repository => PathEquals(repository.WorkingCopyPath, workingCopyPath));
        if (existing == null)
        {
            existing = RepositoryEntry.Create(repositoryUrl, workingCopyPath);
            Repositories.Add(existing);
        }
        else
        {
            existing.RepositoryUrl = repositoryUrl;
            existing.WorkingCopyPath = workingCopyPath;
            existing.Name = RepositoryEntry.BuildName(repositoryUrl, workingCopyPath);
        }

        CurrentRepositoryId = existing.Id;
    }

    public void RemoveRepository(RepositoryEntry repository)
    {
        IgnoreWorkingCopy(repository.WorkingCopyPath);
        Repositories.RemoveAll(item =>
            string.Equals(item.Id, repository.Id, StringComparison.OrdinalIgnoreCase) ||
            PathEquals(item.WorkingCopyPath, repository.WorkingCopyPath));

        if (PathEquals(WorkingCopyPath, repository.WorkingCopyPath))
        {
            WorkingCopyPath = "";
            RepositoryUrl = "";
        }

        ExpandedFileTreePaths.Remove(NormalizeKey(repository.WorkingCopyPath));

        if (CurrentRepositoryId != null &&
            Repositories.All(item => !string.Equals(item.Id, CurrentRepositoryId, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentRepositoryId = Repositories.FirstOrDefault()?.Id;
        }

        if (Repositories.Count == 0)
        {
            CurrentRepositoryId = null;
        }
    }

    public void IgnoreWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        var key = NormalizeKey(workingCopyPath);
        if (IgnoredWorkingCopyPaths.Any(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IgnoredWorkingCopyPaths.Add(key);
    }

    public void UnignoreWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        var key = NormalizeKey(workingCopyPath);
        IgnoredWorkingCopyPaths.RemoveAll(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsIgnoredWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return false;
        }

        var key = NormalizeKey(workingCopyPath);
        return IgnoredWorkingCopyPaths.Any(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase));
    }

    public HashSet<string> GetExpandedPaths(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ExpandedFileTreePaths.TryGetValue(NormalizeKey(workingCopyPath), out var paths)
            ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetExpandedPaths(string workingCopyPath, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        ExpandedFileTreePaths[NormalizeKey(workingCopyPath)] = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path)
            .ToList();
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}

internal sealed class RepositoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string WorkingCopyPath { get; set; } = "";

    public static RepositoryEntry Create(string repositoryUrl, string workingCopyPath)
    {
        return new RepositoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = BuildName(repositoryUrl, workingCopyPath),
            RepositoryUrl = repositoryUrl,
            WorkingCopyPath = workingCopyPath,
        };
    }

    public static string BuildName(string repositoryUrl, string workingCopyPath)
    {
        var folderName = Path.GetFileName(workingCopyPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            return folderName;
        }

        return string.IsNullOrWhiteSpace(repositoryUrl) ? workingCopyPath : repositoryUrl;
    }

    public override string ToString()
    {
        return $"{Name}  ({WorkingCopyPath})";
    }
}

internal sealed class UiLayoutSettings
{
    public bool LayoutLocked { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool IsMaximized { get; set; }
    public int WorkspaceSplitterDistance { get; set; } = 170;
    public int HistorySplitterDistance { get; set; } = 240;
    public int ChangedFilesSplitterDistance { get; set; } = 430;
    public string SelectedTab { get; set; } = "History";
}
