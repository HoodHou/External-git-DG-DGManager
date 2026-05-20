namespace SVNManager;

internal sealed class HistoryView : UserControl
{
    private const int HistoryListRowHeight = 66;
    private readonly SplitContainer _historySplit = new();
    private readonly SplitContainer _changedFilesSplit = new();
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
    private readonly Button _historyDiffMaximizeButton = new ModernButton();
    private readonly Label _historyDiffHeaderLabel = new();
    private readonly ImageList _historyListRowImages = new();
    private readonly ContextMenuStrip _historyListMenu = new();
    private readonly ContextMenuStrip _historyChangedFilesMenu = new();
    private ListViewItem? _hoveredHistoryItem;
    private bool _historyDiffMaximized;
    private List<SvnLogEntry> _historyRows = [];
    private IReadOnlyList<ChangedFileEntry> _historyChangedFilesAll = [];
    private string _historyChangedFilesRootText = "Changed files";
    private List<SvnLogEntry> _selectedLogs = [];

    public event EventHandler? RefreshRequested;
    public event EventHandler? DeepSearchRequested;
    public event EventHandler? LoadMoreRequested;
    public event EventHandler? SearchChanged;
    public event EventHandler? HistorySelectionChanged;
    public event EventHandler? HistoryDoubleClick;
    public event EventHandler? FocusChangedFileRequested;
    public event EventHandler? UpdateToRevisionRequested;
    public event EventHandler? CopyRevisionRequested;
    public event EventHandler? CopySummaryRequested;
    public event EventHandler<TreeViewEventArgs>? ChangedFileSelected;
    public event EventHandler<TreeViewEventArgs>? OpenChangedFileRequested;
    public event EventHandler? OpenChangedFileFolderRequested;
    public event EventHandler? ExternalCompareRequested;
    public event EventHandler? CompareAnotherTableRequested;
    public event EventHandler? FileHistoryRequested;
    public event EventHandler? CompareRemoteHeadRequested;
    public event EventHandler? CommitSpreadsheetMergeRequested;
    public event EventHandler? UpdateFileToRevisionRequested;
    public event EventHandler? ReverseMergeRequested;
    public event EventHandler? CopyChangedFilePathRequested;

    public HistoryView(ImageList treeImages, int initialLoadedLimit)
    {
        Dock = DockStyle.Fill;
        BackColor = ModernTheme.AppBackColor;
        LoadedLimit = initialLoadedLimit;
        Controls.Add(BuildLayout(treeImages));
    }

    public SplitContainer HistorySplit => _historySplit;
    public SplitContainer ChangedFilesSplit => _changedFilesSplit;
    public ListView HistoryList => _historyList;
    public TextBox SearchTextBox => _historySearchText;
    public Label SearchScopeLabel => _historySearchScopeLabel;
    public Button DeepSearchButton => _historyDeepSearchButton;
    public Button LoadMoreButton => _historyLoadMoreButton;
    public Button ClearSearchButton => _historyClearSearchButton;
    public TextBox DetailText => _historyDetailText;
    public TreeView ChangedFilesTree => _historyChangedFilesTree;
    public TextBox ChangedFilesSearchText => _historyChangedFilesSearchText;
    public ComboBox ChangedFilesFilterCombo => _historyChangedFilesFilterCombo;
    public Panel DiffPanel => _historyDiffPanel;
    public Button DiffMaximizeButton => _historyDiffMaximizeButton;
    public Label DiffHeaderLabel => _historyDiffHeaderLabel;
    public ImageList HistoryListRowImages => _historyListRowImages;
    public ContextMenuStrip HistoryListMenu => _historyListMenu;
    public ContextMenuStrip ChangedFilesMenu => _historyChangedFilesMenu;
    public ListViewItem? HoveredHistoryItem => _hoveredHistoryItem;

    public bool IsDiffMaximized
    {
        get => _historyDiffMaximized;
        set
        {
            _historyDiffMaximized = value;
            ApplyDiffMaximizedState();
        }
    }

    public SvnLogEntry? SelectedLog { get; set; }

    public List<SvnLogEntry> SelectedLogs
    {
        get => _selectedLogs;
        set => _selectedLogs = value ?? [];
    }

    public List<SvnLogEntry> HistoryRows
    {
        get => _historyRows;
        set => _historyRows = value ?? [];
    }

    public int LoadedLimit { get; set; }

    public IReadOnlyList<ChangedFileEntry> ChangedFilesAll
    {
        get => _historyChangedFilesAll;
        set => _historyChangedFilesAll = value ?? [];
    }

    public string ChangedFilesRootText
    {
        get => _historyChangedFilesRootText;
        set => _historyChangedFilesRootText = string.IsNullOrWhiteSpace(value) ? "Changed files" : value;
    }

    public void ClearForRepositoryChange(int initialLoadedLimit)
    {
        IsDiffMaximized = false;
        LoadedLimit = initialLoadedLimit;
        _historyRows.Clear();
        _historyList.Items.Clear();
        SelectedLog = null;
        SelectedLogs = [];
        ClearChangedFiles();
        if (!_historyDetailText.IsDisposed)
        {
            _historyDetailText.Clear();
        }
    }

    public void SetBusy(bool busy, bool canUseHistory)
    {
        _historyDeepSearchButton.Enabled = !busy && canUseHistory;
        _historyLoadMoreButton.Enabled = !busy && canUseHistory;
        _historyClearSearchButton.Enabled = !busy && !string.IsNullOrWhiteSpace(_historySearchText.Text);
        _historySearchText.Enabled = !busy;
    }

    public void ClearChangedFiles()
    {
        _historyChangedFilesAll = [];
        _historyChangedFilesRootText = "Changed files";
        _historyChangedFilesTree.Nodes.Clear();
    }

    public void SetChangedFiles(string rootText, IReadOnlyList<ChangedFileEntry> files)
    {
        _historyChangedFilesRootText = rootText;
        _historyChangedFilesAll = files
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyChangedFilesFilter();
    }

    public void ApplyChangedFilesFilter()
    {
        _historyChangedFilesTree.BeginUpdate();
        try
        {
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
        }
        finally
        {
            _historyChangedFilesTree.EndUpdate();
        }
    }

    public List<SvnLogEntry> GetSelectedLogsFromList()
    {
        return _historyList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnLogEntry)
            .Where(log => log != null)
            .Cast<SvnLogEntry>()
            .OrderBy(log => log.Revision)
            .ToList();
    }

    public SvnLogEntry? GetSingleSelectedLog()
    {
        return _historyList.SelectedItems.Count == 1 && _historyList.SelectedItems[0].Tag is SvnLogEntry log
            ? log
            : null;
    }

    public ChangedFileEntry? GetSelectedChangedFile()
    {
        return _historyChangedFilesTree.SelectedNode?.Tag as ChangedFileEntry;
    }

    public TreeNode? FindBestChangedFileNode(HistorySearchFilter filter)
    {
        return FindBestChangedFileNode(_historyChangedFilesTree.Nodes.Cast<TreeNode>(), filter);
    }

    private Control BuildLayout(ImageList treeImages)
    {
        _historySplit.Dock = DockStyle.Fill;
        _historySplit.Orientation = Orientation.Vertical;
        _historySplit.FixedPanel = FixedPanel.Panel1;
        _historySplit.BackColor = ModernTheme.AppBackColor;
        _historySplit.SplitterWidth = 8;
        _historySplit.Panel1.Padding = new Padding(0, 8, 4, 8);
        _historySplit.Panel2.Padding = new Padding(4, 8, 0, 8);
        Form1.SetSplitterDistanceWhenReady(_historySplit, 640);

        var historyListPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        historyListPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        historyListPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        historyListPanel.Controls.Add(BuildHistorySearchPanel(), 0, 0);
        historyListPanel.Controls.Add(BuildHistoryList(), 0, 1);
        _historySplit.Panel1.Controls.Add(CreateCard(BuildHistoryTopPanel(historyListPanel)));

        _changedFilesSplit.Dock = DockStyle.Fill;
        _changedFilesSplit.Orientation = Orientation.Horizontal;
        _changedFilesSplit.FixedPanel = FixedPanel.Panel1;
        _changedFilesSplit.BackColor = ModernTheme.AppBackColor;
        _changedFilesSplit.SplitterWidth = 8;
        _changedFilesSplit.Panel1.Padding = new Padding(0, 0, 0, 4);
        _changedFilesSplit.Panel2.Padding = new Padding(0, 4, 0, 0);
        Form1.SetSplitterDistanceWhenReady(_changedFilesSplit, 360);
        _changedFilesSplit.Panel1.Controls.Add(CreateCard(BuildHistoryDiffPreviewPanel()));
        _changedFilesSplit.Panel2.Controls.Add(CreateCard(BuildChangedFilesPanel(treeImages)));
        _historySplit.Panel2.Controls.Add(_changedFilesSplit);

        return _historySplit;
    }

    private Control BuildHistorySearchPanel()
    {
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
        _historySearchText.Margin = new Padding(0, 3, 6, 3);
        _historySearchText.TextChanged += (_, _) => SearchChanged?.Invoke(this, EventArgs.Empty);
        _historySearchText.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
            {
                return;
            }

            args.SuppressKeyPress = true;
            DeepSearchRequested?.Invoke(this, EventArgs.Empty);
        };
        historySearchPanel.Controls.Add(_historySearchText, 0, 0);

        _historySearchScopeLabel.Dock = DockStyle.Fill;
        _historySearchScopeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _historySearchScopeLabel.ForeColor = Color.FromArgb(90, 100, 115);
        _historySearchScopeLabel.Text = "已加载 0 条";
        historySearchPanel.Controls.Add(_historySearchScopeLabel, 1, 0);

        ConfigureHistorySearchButton(_historyDeepSearchButton, "深度搜索");
        _historyDeepSearchButton.Click += (_, _) => DeepSearchRequested?.Invoke(this, EventArgs.Empty);
        historySearchPanel.Controls.Add(_historyDeepSearchButton, 2, 0);

        ConfigureHistorySearchButton(_historyLoadMoreButton, "加载更多");
        _historyLoadMoreButton.Click += (_, _) => LoadMoreRequested?.Invoke(this, EventArgs.Empty);
        historySearchPanel.Controls.Add(_historyLoadMoreButton, 3, 0);

        ConfigureHistorySearchButton(_historyClearSearchButton, "清空");
        _historyClearSearchButton.Click += (_, _) => _historySearchText.Clear();
        historySearchPanel.Controls.Add(_historyClearSearchButton, 4, 0);
        return historySearchPanel;
    }

    private Control BuildHistoryList()
    {
        _historyList.Dock = DockStyle.Fill;
        _historyList.View = View.Details;
        _historyList.FullRowSelect = true;
        _historyList.GridLines = false;
        _historyList.HideSelection = false;
        _historyList.BorderStyle = BorderStyle.None;
        _historyList.BackColor = ModernTheme.SurfaceColor;
        _historyList.OwnerDraw = true;
        WinFormsRendering.EnableDoubleBuffering(_historyList);
        _historyList.HeaderStyle = ColumnHeaderStyle.None;
        _historyListRowImages.ColorDepth = ColorDepth.Depth32Bit;
        _historyListRowImages.ImageSize = new Size(1, HistoryListRowHeight);
        _historyListRowImages.Images.Add("row-height", new Bitmap(1, HistoryListRowHeight));
        _historyList.SmallImageList = _historyListRowImages;
        _historyList.Columns.Add("History", 630);
        _historyList.SizeChanged += (sender, args) =>
        {
            if (_historyList.Columns.Count > 0)
            {
                _historyList.Columns[0].Width = Math.Max(100, _historyList.ClientSize.Width - 4);
            }
        };
        _historyList.DrawColumnHeader += (_, args) => args.DrawDefault = false;
        _historyList.DrawSubItem += DrawHistoryListSubItem;
        _historyList.SelectedIndexChanged += (_, _) => HistorySelectionChanged?.Invoke(this, EventArgs.Empty);
        _historyList.DoubleClick += (_, _) => HistoryDoubleClick?.Invoke(this, EventArgs.Empty);
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
        return _historyList;
    }

    private Control BuildChangedFilesPanel(ImageList treeImages)
    {
        _historyChangedFilesTree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(_historyChangedFilesTree);
        _historyChangedFilesTree.ImageList = treeImages;
        _historyChangedFilesTree.TreeViewNodeSorter = new FileTreeNodeSorter();
        _historyChangedFilesTree.NodeMouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
            {
                _historyChangedFilesTree.SelectedNode = args.Node;
            }
        };
        _historyChangedFilesTree.NodeMouseDoubleClick += (_, args) =>
        {
            if (IsModernTreeArrowHit(_historyChangedFilesTree, args.Node, new Point(args.X, args.Y)))
            {
                return;
            }

            if (ToggleExpandableNode(args.Node))
            {
                return;
            }

            OpenChangedFileRequested?.Invoke(this, new TreeViewEventArgs(args.Node));
        };
        _historyChangedFilesTree.AfterSelect += (_, args) => ChangedFileSelected?.Invoke(this, args);
        BuildHistoryChangedFilesMenu();
        _historyChangedFilesTree.ContextMenuStrip = _historyChangedFilesMenu;
        return BuildChangedFilesFilterPanel();
    }

    private Control BuildHistoryDiffPreviewPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.SurfaceColor,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ModernTheme.SurfaceColor,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        _historyDiffHeaderLabel.Text = "提交详情";
        _historyDiffHeaderLabel.Dock = DockStyle.Fill;
        _historyDiffHeaderLabel.Padding = new Padding(8, 0, 0, 0);
        _historyDiffHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
        _historyDiffHeaderLabel.BackColor = ModernTheme.SurfaceColor;
        _historyDiffHeaderLabel.ForeColor = ModernTheme.TextColor;
        _historyDiffHeaderLabel.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        header.Controls.Add(_historyDiffHeaderLabel, 0, 0);
        _historyDiffMaximizeButton.Text = "最大化差异";
        _historyDiffMaximizeButton.Dock = DockStyle.Fill;
        _historyDiffMaximizeButton.Margin = new Padding(4, 3, 6, 3);
        _historyDiffMaximizeButton.Click += (_, _) =>
        {
            _historyDiffMaximized = !_historyDiffMaximized;
            ApplyDiffMaximizedState();
        };
        header.Controls.Add(_historyDiffMaximizeButton, 1, 0);

        _historyDiffPanel.Dock = DockStyle.Fill;
        _historyDiffPanel.BackColor = ModernTheme.SurfaceColor;
        _historyDetailText.Dock = DockStyle.Fill;
        _historyDetailText.Multiline = true;
        _historyDetailText.ReadOnly = true;
        _historyDetailText.ScrollBars = ScrollBars.Both;
        _historyDetailText.WordWrap = false;
        _historyDiffPanel.Controls.Add(_historyDetailText);

        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(_historyDiffPanel, 0, 1);
        return panel;
    }

    private void BuildHistoryListMenu()
    {
        _historyListMenu.Items.Clear();
        _historyListMenu.Items.Add("定位本次改动文件", null, (_, _) => FocusChangedFileRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add("回退工作副本到此版本...", null, (_, _) => UpdateToRevisionRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add(new ToolStripSeparator());
        _historyListMenu.Items.Add("深度搜索当前条件", null, (_, _) => DeepSearchRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add("加载更多历史", null, (_, _) => LoadMoreRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add(new ToolStripSeparator());
        _historyListMenu.Items.Add("复制版本号", null, (_, _) => CopyRevisionRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add("复制提交摘要", null, (_, _) => CopySummaryRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Items.Add("刷新历史", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _historyListMenu.Opening += (_, _) =>
        {
            var log = GetSingleSelectedLog();
            var hasCommittedRevision = log != null && !log.IsUncommitted && log.Revision > 0;
            foreach (ToolStripItem item in _historyListMenu.Items)
            {
                if (item is ToolStripSeparator)
                {
                    continue;
                }

                item.Enabled = hasCommittedRevision ||
                    item.Text == "刷新历史" ||
                    item.Text == "加载更多历史" ||
                    item.Text == "深度搜索当前条件";
            }
        };
    }

    private void BuildHistoryChangedFilesMenu()
    {
        _historyChangedFilesMenu.Items.Clear();
        _historyChangedFilesMenu.Items.Add("打开文件", null, (_, _) =>
        {
            if (_historyChangedFilesTree.SelectedNode != null)
            {
                OpenChangedFileRequested?.Invoke(this, new TreeViewEventArgs(_historyChangedFilesTree.SelectedNode));
            }
        });
        _historyChangedFilesMenu.Items.Add("打开所在目录", null, (_, _) => OpenChangedFileFolderRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("用分久必合对比", null, (_, _) => ExternalCompareRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add("和另一个表快速比对...", null, (_, _) => CompareAnotherTableRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add("文件历史", null, (_, _) => FileHistoryRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add("当前本地 vs 远端 HEAD", null, (_, _) => CompareRemoteHeadRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("用所选提交/范围三方合并到目标表...", null, (_, _) => CommitSpreadsheetMergeRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add("将此文件更新到本次提交版本...", null, (_, _) => UpdateFileToRevisionRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add("撤销本次提交对这个文件的改动...", null, (_, _) => ReverseMergeRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Items.Add(new ToolStripSeparator());
        _historyChangedFilesMenu.Items.Add("复制路径", null, (_, _) => CopyChangedFilePathRequested?.Invoke(this, EventArgs.Empty));
        _historyChangedFilesMenu.Opening += (_, _) =>
        {
            var hasFile = GetSelectedChangedFile() != null;
            var hasSingleCommittedRevision = hasFile && SelectedLog is { IsUncommitted: false, Revision: > 0 };
            foreach (ToolStripItem item in _historyChangedFilesMenu.Items)
            {
                if (item is ToolStripSeparator)
                {
                    continue;
                }

                item.Enabled = hasFile;
                var text = item.Text ?? "";
                if (text.StartsWith("将此文件更新", StringComparison.Ordinal) ||
                    text.StartsWith("撤销本次提交", StringComparison.Ordinal))
                {
                    item.Enabled = hasSingleCommittedRevision;
                }
            }
        };
    }

    private Control BuildHistoryTopPanel(Control content)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.SurfaceColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreatePanelToolbar("提交历史", "刷新历史"), 0, 0);
        root.Controls.Add(content, 0, 1);
        return root;
    }

    private Control BuildChangedFilesFilterPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ModernTheme.SurfaceColor,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = "Changed files",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = ModernTheme.SurfaceColor,
            ForeColor = ModernTheme.TextColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ModernTheme.SurfaceColor,
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));

        _historyChangedFilesSearchText.Dock = DockStyle.Fill;
        _historyChangedFilesSearchText.Margin = new Padding(0, 4, 6, 4);
        _historyChangedFilesSearchText.PlaceholderText = "搜索文件名 / 路径";
        _historyChangedFilesSearchText.TextChanged += (_, _) => ApplyChangedFilesFilter();
        filters.Controls.Add(_historyChangedFilesSearchText, 0, 0);

        _historyChangedFilesFilterCombo.Dock = DockStyle.Fill;
        _historyChangedFilesFilterCombo.Margin = new Padding(0, 4, 0, 4);
        _historyChangedFilesFilterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyChangedFilesFilterCombo.Items.Clear();
        foreach (var text in ChangedFilesFilter.Options)
        {
            _historyChangedFilesFilterCombo.Items.Add(text);
        }

        _historyChangedFilesFilterCombo.SelectedIndex = 0;
        _historyChangedFilesFilterCombo.SelectedIndexChanged += (_, _) => ApplyChangedFilesFilter();
        filters.Controls.Add(_historyChangedFilesFilterCombo, 1, 0);

        panel.Controls.Add(filters, 0, 1);
        panel.Controls.Add(_historyChangedFilesTree, 0, 2);
        return panel;
    }

    private Control CreatePanelToolbar(string title, string buttonText)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ModernTheme.SurfaceColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.TextColor,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        }, 0, 0);
        var button = new ModernButton
        {
            Text = buttonText,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3),
        };
        button.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    private static void ConfigureHistorySearchButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 3, 6, 3);
    }

    private static Control CreateCard(Control content)
    {
        var card = new ModernCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ShowShadow = true,
        };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    private void ApplyDiffMaximizedState()
    {
        _historySplit.Panel1Collapsed = _historyDiffMaximized;
        _changedFilesSplit.Panel2Collapsed = _historyDiffMaximized;
        _historyDiffMaximizeButton.Text = _historyDiffMaximized ? "还原布局" : "最大化差异";
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

    private void DrawHistoryListSubItem(object? sender, DrawListViewSubItemEventArgs args)
    {
        args.DrawDefault = false;
        if (sender is not ListView list || args.Item?.Tag is not SvnLogEntry log || args.ColumnIndex != 0)
        {
            return;
        }

        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, args.Item.Bounds.Top + 6, Math.Max(1, list.ClientSize.Width - 12), args.Item.Bounds.Height - 12);
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
        graphics.FillEllipse(markerBrush, bounds.Left + 12, bounds.Top + 19, 10, 10);
        if (log.IsWorkingCopyRevision)
        {
            graphics.FillRectangle(markerBrush, bounds.Left + 16, bounds.Top + 29, 2, 16);
        }

        var contentLeft = bounds.Left + 34;
        var contentRight = bounds.Right - 14;
        var compact = bounds.Width < 560;
        var revision = log.IsUncommitted ? "LOCAL" : $"r{log.Revision}";
        var revisionWidth = compact ? 78 : 92;
        var dateWidth = compact ? 104 : 134;
        var dateLeft = Math.Max(contentLeft + revisionWidth + 8, contentRight - dateWidth);
        var revisionBounds = new Rectangle(contentLeft, bounds.Top + 10, revisionWidth, 18);
        TextRenderer.DrawText(graphics, revision, revisionFont, revisionBounds, actionColor, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var authorLeft = revisionBounds.Right + 8;
        var authorWidth = Math.Max(1, dateLeft - authorLeft - 6);
        var authorBounds = new Rectangle(authorLeft, bounds.Top + 10, authorWidth, 18);
        TextRenderer.DrawText(graphics, log.Author, authorFont, authorBounds, Color.FromArgb(31, 41, 55), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var dateBounds = new Rectangle(dateLeft, bounds.Top + 10, Math.Max(1, contentRight - dateLeft), 18);
        TextRenderer.DrawText(graphics, log.LocalDateText, dateFont, dateBounds, Color.FromArgb(100, 116, 139), TextFormatFlags.Right | TextFormatFlags.NoPadding);

        var reservedBadgeWidth = 0;
        if (log.ChangedFiles.Count > 0)
        {
            var countText = $"{log.ChangedFiles.Count} files";
            reservedBadgeWidth = compact ? 0 : 78;
            var countBounds = new Rectangle(bounds.Right - 84, bounds.Top + 34, 68, 18);
            using var countBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
            using var countPen = new Pen(Color.FromArgb(203, 213, 225));
            if (!compact)
            {
                graphics.FillRoundedRectangle(countBrush, countBounds, 5);
                graphics.DrawRoundedRectangle(countPen, countBounds, 5);
                TextRenderer.DrawText(graphics, countText, dateFont, countBounds, Color.FromArgb(71, 85, 105), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        var messageBounds = new Rectangle(contentLeft, bounds.Top + 34, Math.Max(1, contentRight - contentLeft - reservedBadgeWidth), 20);
        TextRenderer.DrawText(graphics, log.DescriptionText, messageFont, messageBounds, Color.FromArgb(51, 65, 85), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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

    private static void ConfigureNavigationTree(TreeView tree)
    {
        ModernTreeViewRenderer.Configure(tree);
    }

    private static bool ToggleExpandableNode(TreeNode? node)
    {
        return ModernTreeViewRenderer.ToggleNode(node);
    }

    private static bool IsModernTreeArrowHit(TreeView tree, TreeNode? node, Point location)
    {
        return ModernTreeViewRenderer.IsArrowHit(tree, node, location);
    }
}
