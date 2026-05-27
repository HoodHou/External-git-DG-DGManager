using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SVNManager;

public partial class Form1
{
    private const int TerminalDrawerCollapsedHeight = 48;
    private const int TerminalDrawerExpandedHeight = 210;
    private const int DefaultWindowWidth = 1420;
    private const int DefaultWindowHeight = 840;
    private const int DefaultWorkspaceSplitterDistance = 240;
    private const int MinWorkspaceSplitterDistance = 160;
    private const int MaxWorkspaceSplitterDistance = 260;
    private const int DefaultHistorySplitterDistance = 640;
    private const int MinHistorySplitterDistance = 480;
    private const int MaxHistorySplitterDistance = 700;
    private const int PreferredHistoryDetailWidth = 560;
    private const int MinHistoryDetailWidth = 320;
    private const int DefaultChangedFilesSplitterDistance = 360;
    private const int MinChangedFilesSplitterDistance = 220;
    private const int MaxChangedFilesSplitterDistance = 520;
    private readonly Button _terminalToggleButton = new ModernToolbarButton
    {
        Text = "展开",
        Icon = ModernToolbarIcon.More,
        Kind = ModernToolbarButtonKind.Ghost,
    };
    private readonly Label _terminalPreviewLabel = new();
    private RowStyle? _terminalDrawerRow;
    private bool _terminalDrawerExpanded;

    private void BuildUi()
    {
        Text = "梦境 SVN 管理器";
        MinimumSize = new Size(1120, 720);
        Size = new Size(DefaultWindowWidth, DefaultWindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = ModernTheme.AppBackColor;
        ConfigureTreeImages();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 12, 14, 8),
            BackColor = ModernTheme.AppBackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _terminalDrawerRow = new RowStyle(SizeType.Absolute, TerminalDrawerCollapsedHeight);
        root.RowStyles.Add(_terminalDrawerRow);
        Controls.Add(root);

        root.Controls.Add(BuildTopToolbar(), 0, 0);

        _workspaceSplit.Dock = DockStyle.Fill;
        _workspaceSplit.FixedPanel = FixedPanel.Panel1;
        root.Controls.Add(_workspaceSplit, 0, 1);
        root.Controls.Add(BuildTerminalDrawer(), 0, 2);

        BuildSidebar();
        BuildMainTabs();
        BuildStatusStrip();
        BindShellLayoutGuards();

        _remoteCheckTimer.Interval = 180000;
        _remoteCheckTimer.Tick += async (_, _) => await CheckRemoteChangesAsync(showUpToDateMessage: false);
    }

    private Control BuildTopToolbar()
    {
        var card = new ModernCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8),
            ShowShadow = true,
        };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            BackColor = ModernTheme.SurfaceColor,
        };
        card.Controls.Add(toolbar);

        toolbar.Controls.Add(new Label
        {
            Text = "梦境 SVN",
            AutoSize = false,
            Width = 88,
            Height = 38,
            Margin = new Padding(0, 0, 10, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = ModernTheme.TextColor,
        });

        _repositorySelector.Width = 220;
        _repositorySelector.Height = 38;
        _repositorySelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _repositorySelector.Margin = new Padding(0, 5, 12, 0);
        _repositorySelector.SelectedIndexChanged += (_, _) => SelectRepositoryFromList();
        toolbar.Controls.Add(_repositorySelector);

        toolbar.Controls.Add(CreateTopToolbarButton("导入/检出", 108, ModernToolbarIcon.Import, ModernToolbarButtonKind.Ghost, () => SelectTab("配置")));
        toolbar.Controls.Add(CreateTopToolbarButton("管理", 86, ModernToolbarIcon.Manage, ModernToolbarButtonKind.Ghost, ShowRepositoryManagerDialog));
        toolbar.Controls.Add(CreateToolbarSeparator());

        ConfigureTopToolbarButton(_updateButton, "拉取最新", 108, ModernToolbarIcon.Update, ModernToolbarButtonKind.Subtle);
        _updateButton.Click += async (_, _) => await RunUpdateAsync();
        toolbar.Controls.Add(_updateButton);

        ConfigureTopToolbarButton(_statusButton, "查看改动", 108, ModernToolbarIcon.Status, ModernToolbarButtonKind.Subtle);
        _statusButton.Click += async (_, _) => await RefreshStatusAsync();
        toolbar.Controls.Add(_statusButton);

        ConfigureTopToolbarButton(_commitButton, "提交", 94, ModernToolbarIcon.Commit, ModernToolbarButtonKind.Primary);
        _commitButton.Click += async (_, _) => await RunCommitAsync();
        toolbar.Controls.Add(_commitButton);

        ConfigureTopToolbarButton(_diffButton, "查看差异", 100, ModernToolbarIcon.Status, ModernToolbarButtonKind.Ghost);
        _diffButton.Click += async (_, _) => await RunDiffAsync();

        ConfigureTopToolbarButton(_externalMergeButton, "外部合并", 100, ModernToolbarIcon.Manage, ModernToolbarButtonKind.Ghost);
        _externalMergeButton.Click += async (_, _) => await RunExternalCompareOrMergeAsync();

        ConfigureTopToolbarButton(_conflictWorkflowButton, "冲突处理", 100, ModernToolbarIcon.Status, ModernToolbarButtonKind.Ghost);
        _conflictWorkflowButton.Click += async (_, _) => await RunConflictWorkflowAsync();

        ConfigureTopToolbarButton(_historyButton, "文件历史", 100, ModernToolbarIcon.History, ModernToolbarButtonKind.Ghost);
        _historyButton.Click += async (_, _) => await RunFileHistoryAsync();

        toolbar.Controls.Add(CreateToolbarSeparator());
        toolbar.Controls.Add(CreateTopToolbarButton("打开目录", 98, ModernToolbarIcon.Folder, ModernToolbarButtonKind.Ghost, OpenWorkingCopyFolder));

        BuildMoreActionsMenu();
        ConfigureTopToolbarButton(_moreActionsButton, "更多", 82, ModernToolbarIcon.More, ModernToolbarButtonKind.Ghost);
        _moreActionsButton.Click += (_, _) => _moreActionsMenu.Show(_moreActionsButton, new Point(0, _moreActionsButton.Height));
        toolbar.Controls.Add(_moreActionsButton);

        return card;
    }

    private void BuildSidebar()
    {
        _repositoryTree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(_repositoryTree);
        _repositoryTree.ShowNodeToolTips = true;
        _repositoryTree.ImageList = _treeImages;
        _repositoryTree.AfterSelect += async (_, args) => await SelectSidebarRepositoryAsync(args.Node);
        _workspaceSplit.Panel1.Padding = new Padding(0, 8, 8, 8);
        _workspaceSplit.Panel1.Controls.Add(CreateTitledPanel("本地库", _repositoryTree));
    }

    private Control BuildTerminalDrawer()
    {
        var card = new ModernCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 4, 10, 4),
            ShowShadow = false,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.SurfaceColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = ModernTheme.SurfaceColor,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        header.Controls.Add(new Label
        {
            Text = "终端输出",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernTheme.TextColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        _terminalPreviewLabel.Dock = DockStyle.Fill;
        _terminalPreviewLabel.Text = "暂无输出。";
        _terminalPreviewLabel.TextAlign = ContentAlignment.MiddleLeft;
        _terminalPreviewLabel.ForeColor = ModernTheme.MutedTextColor;
        _terminalPreviewLabel.AutoEllipsis = true;
        header.Controls.Add(_terminalPreviewLabel, 1, 0);
        ConfigureTopToolbarButton(_terminalToggleButton, "展开", 86, ModernToolbarIcon.More, ModernToolbarButtonKind.Ghost);
        _terminalToggleButton.Dock = DockStyle.Fill;
        _terminalToggleButton.Margin = new Padding(4, 0, 0, 0);
        _terminalToggleButton.Click += (_, _) => ToggleTerminalDrawer();
        header.Controls.Add(_terminalToggleButton, 2, 0);
        layout.Controls.Add(header, 0, 0);

        _outputText.Dock = DockStyle.Fill;
        _outputText.Multiline = true;
        _outputText.ReadOnly = true;
        _outputText.ScrollBars = ScrollBars.Both;
        _outputText.WordWrap = false;
        _outputText.BorderStyle = BorderStyle.None;
        _outputText.BackColor = Color.FromArgb(15, 23, 42);
        _outputText.ForeColor = Color.FromArgb(226, 232, 240);
        _outputText.Font = new Font("Consolas", 9F);
        _outputText.Visible = false;
        layout.Controls.Add(_outputText, 0, 1);
        return card;
    }

    private void ToggleTerminalDrawer()
    {
        _terminalDrawerExpanded = !_terminalDrawerExpanded;
        if (_terminalDrawerRow != null)
        {
            _terminalDrawerRow.Height = _terminalDrawerExpanded ? TerminalDrawerExpandedHeight : TerminalDrawerCollapsedHeight;
        }

        _outputText.Visible = _terminalDrawerExpanded;
        _terminalToggleButton.Text = _terminalDrawerExpanded ? "收起" : "展开";
        PerformLayout();
    }

    private void BuildMainTabs()
    {
        _mainTabs.Dock = DockStyle.Fill;
        _mainTabs.Appearance = TabAppearance.FlatButtons;
        _mainTabs.ItemSize = new Size(0, 1);
        _mainTabs.SizeMode = TabSizeMode.Fixed;

        _configPage.Controls.Add(CreateConfigPanel());
        _mainTabs.TabPages.Add(_configPage);

        _statusPage.Controls.Add(CreateStatusPanel());
        _mainTabs.TabPages.Add(_statusPage);

        _conflictPage.Controls.Add(CreateConflictPanel());
        _mainTabs.TabPages.Add(_conflictPage);

        ConfigureAllFilesTree();
        var filesPage = new TabPage("全部文件");
        filesPage.Controls.Add(CreateAllFilesPanel());
        _mainTabs.TabPages.Add(filesPage);

        WireHistoryViewEvents();
        _historyPage.Controls.Add(_historyView);
        _mainTabs.TabPages.Add(_historyPage);

        _mainTabs.SelectedIndexChanged += async (_, _) =>
        {
            UpdateShellNavigationSelection();
            await LoadCurrentTabAsync();
        };

        _workspaceSplit.Panel2.Controls.Add(CreateShellHost());
    }

    private void ConfigureAllFilesTree()
    {
        var tree = _allFilesView.FileTree;
        tree.Dock = DockStyle.Fill;
        ConfigureNavigationTree(tree);
        tree.ShowNodeToolTips = true;
        tree.ImageList = _treeImages;
        tree.TreeViewNodeSorter = new FileTreeNodeSorter();
        tree.BeforeExpand += (_, args) =>
        {
            if (args.Node != null)
            {
                EnsureLazyFileTreeChildren(args.Node);
            }
        };
        tree.NodeMouseDoubleClick += (_, args) =>
        {
            if (IsModernTreeArrowHit(tree, args.Node, new Point(args.X, args.Y)))
            {
                return;
            }

            if (ToggleExpandableNode(args.Node))
            {
                return;
            }

            OpenTreeFile(args.Node);
        };
        tree.AfterExpand += (_, _) => SaveTreeExpansionState();
        tree.AfterCollapse += (_, _) => SaveTreeExpansionState();
        tree.NodeMouseClick += (_, args) => HandleFileTreeNodeMouseClick(args.Node, args.Button);
        BuildFileTreeMenu();
        tree.ContextMenuStrip = _fileTreeMenu;
    }

    private void BuildStatusStrip()
    {
        var statusStrip = new StatusStrip
        {
            SizingGrip = true,
            BackColor = ModernTheme.SubtleSurfaceColor,
        };
        _statusLabel.Text = "就绪";
        statusStrip.Items.Add(_statusLabel);

        _toolUpdateDownloadProgressBar.Minimum = 0;
        _toolUpdateDownloadProgressBar.Maximum = 100;
        _toolUpdateDownloadProgressBar.Width = 160;
        _toolUpdateDownloadProgressBar.Style = ProgressBarStyle.Marquee;
        _toolUpdateDownloadProgressBar.Visible = false;
        statusStrip.Items.Add(_toolUpdateDownloadProgressBar);

        _toolUpdateDownloadProgressLabel.Visible = false;
        statusStrip.Items.Add(_toolUpdateDownloadProgressLabel);

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

        Controls.Add(statusStrip);
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

    private void BuildMoreActionsMenu()
    {
        _moreActionsMenu.Items.Clear();
        _moreActionsMenu.Items.Add("设置", null, (_, _) => ShowSettingsDialog());
        _moreActionsMenu.Items.Add("本地库管理", null, (_, _) => ShowRepositoryManagerDialog());
        _moreActionsMenu.Items.Add("保存当前库", null, (_, _) => SaveCurrentRepository());
        _moreActionsMenu.Items.Add("移除当前库", null, (_, _) => RemoveCurrentRepository());
        _moreActionsMenu.Items.Add("打开目录", null, (_, _) => OpenWorkingCopyFolder());
        _moreActionsMenu.Items.Add("环境检测", null, async (_, _) => await ShowEnvironmentCheckAsync());
        _moreActionsMenu.Items.Add(new ToolStripSeparator());
        _moreActionsMenu.Items.Add("查看改动", null, async (_, _) => await RefreshStatusAsync());
        _moreActionsMenu.Items.Add("查看差异", null, async (_, _) => await RunDiffAsync());
        _moreActionsMenu.Items.Add("内置三方合并", null, async (_, _) => await RunInternalSpreadsheetMergeAsync());
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
            favoritesMenu.DropDownItems.Add("暂无收藏").Enabled = false;
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

    private void SelectTab(string text)
    {
        foreach (TabPage page in _mainTabs.TabPages)
        {
            if (!IsTab(page, text))
            {
                continue;
            }

            _mainTabs.SelectedTab = page;
            UpdateShellNavigationSelection();
            return;
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

        if (layout.IsMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        ApplySavedSplitterLayout(layout);
        BeginInvoke(new Action(() => ApplySavedSplitterLayout(layout)));
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
        NormalizeCurrentSplitterLayout();
        _settings.UiLayout.WorkspaceSplitterDistance = NormalizeWorkspaceSplitterDistance(_workspaceSplit, _workspaceSplit.SplitterDistance);
        _settings.UiLayout.HistorySplitterDistance = NormalizeHistorySplitterDistance(_historyView.HistorySplit, _historyView.HistorySplit.SplitterDistance);
        _settings.UiLayout.ChangedFilesSplitterDistance = NormalizeChangedFilesSplitterDistance(_historyView.ChangedFilesSplit, _historyView.ChangedFilesSplit.SplitterDistance);
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

        _settings.UiLayout = new UiLayoutSettings
        {
            WorkspaceSplitterDistance = DefaultWorkspaceSplitterDistance,
            HistorySplitterDistance = DefaultHistorySplitterDistance,
            ChangedFilesSplitterDistance = DefaultChangedFilesSplitterDistance,
            SelectedTab = "History",
        };
        _settings.Save();
        WindowState = FormWindowState.Normal;
        Size = new Size(DefaultWindowWidth, DefaultWindowHeight);
        CenterToScreen();
        ApplySavedSplitterLayout(_settings.UiLayout);
        SelectTab("History");
        BuildMoreActionsMenu();
        WriteOutput("界面布局已重置。");
    }

    private void BindShellLayoutGuards()
    {
        _workspaceSplit.SizeChanged += (_, _) => NormalizeCurrentSplitterLayout();
        _historyView.HistorySplit.SizeChanged += (_, _) => NormalizeCurrentSplitterLayout();
        _historyView.ChangedFilesSplit.SizeChanged += (_, _) => NormalizeCurrentSplitterLayout();
    }

    private void NormalizeCurrentSplitterLayout()
    {
        if (IsDisposed)
        {
            return;
        }

        SafeSetSplitterDistance(_workspaceSplit, NormalizeWorkspaceSplitterDistance(_workspaceSplit, _workspaceSplit.SplitterDistance));
        SafeSetSplitterDistance(_historyView.HistorySplit, NormalizeHistorySplitterDistance(_historyView.HistorySplit, _historyView.HistorySplit.SplitterDistance));
        SafeSetSplitterDistance(_historyView.ChangedFilesSplit, NormalizeChangedFilesSplitterDistance(_historyView.ChangedFilesSplit, _historyView.ChangedFilesSplit.SplitterDistance));
    }

    private void ApplySavedSplitterLayout(UiLayoutSettings layout)
    {
        SafeSetSplitterDistance(_workspaceSplit, NormalizeWorkspaceSplitterDistance(_workspaceSplit, layout.WorkspaceSplitterDistance));
        SafeSetSplitterDistance(_historyView.HistorySplit, NormalizeHistorySplitterDistance(_historyView.HistorySplit, layout.HistorySplitterDistance));
        SafeSetSplitterDistance(_historyView.ChangedFilesSplit, NormalizeChangedFilesSplitterDistance(_historyView.ChangedFilesSplit, layout.ChangedFilesSplitterDistance));
    }

    private static int NormalizeWorkspaceSplitterDistance(SplitContainer split, int requested)
    {
        var distance = requested <= 0 ? DefaultWorkspaceSplitterDistance : requested;
        var max = MaxWorkspaceSplitterDistance;
        if (split.Width > 0)
        {
            max = Math.Min(max, Math.Max(MinWorkspaceSplitterDistance, split.Width - split.SplitterWidth - 520));
        }

        return Math.Clamp(distance, MinWorkspaceSplitterDistance, max);
    }

    private static int NormalizeHistorySplitterDistance(SplitContainer split, int requested)
    {
        var distance = requested <= 0 ? DefaultHistorySplitterDistance : requested;
        var min = MinHistorySplitterDistance;
        var max = MaxHistorySplitterDistance;
        var available = split.Width;
        if (available > 0)
        {
            var rightMin = available >= 1200
                ? PreferredHistoryDetailWidth
                : Math.Max(MinHistoryDetailWidth, available / 3);
            var maxByRight = available - split.SplitterWidth - rightMin;
            max = Math.Min(max, Math.Max(180, maxByRight));
            min = Math.Min(min, max);
        }

        return Math.Clamp(distance, min, max);
    }

    private static int NormalizeChangedFilesSplitterDistance(SplitContainer split, int requested)
    {
        var distance = requested <= 0 ? DefaultChangedFilesSplitterDistance : requested;
        var min = MinChangedFilesSplitterDistance;
        var max = MaxChangedFilesSplitterDistance;
        var available = split.Height;
        if (available > 0)
        {
            var bottomMin = available >= 680 ? 260 : 200;
            var maxByBottom = available - split.SplitterWidth - bottomMin;
            max = Math.Min(max, Math.Max(140, maxByBottom));
            min = Math.Min(min, max);
        }

        return Math.Clamp(distance, min, max);
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
            // WinForms can reject splitter updates while a parent split is resizing.
        }
        catch (ArgumentException)
        {
            // WinForms can reject splitter updates while a parent split is resizing.
        }
    }

    internal static void BindSafeSplitterDistance(SplitContainer split, int distance)
    {
        split.HandleCreated += (_, _) =>
        {
            if (split.IsDisposed)
            {
                return;
            }

            try
            {
                split.BeginInvoke(new Action(() => SafeSetSplitterDistance(split, distance)));
            }
            catch (InvalidOperationException)
            {
                SafeSetSplitterDistance(split, distance);
            }
        };
        split.SizeChanged += (_, _) =>
        {
            if (split.SplitterDistance <= split.Panel1MinSize)
            {
                SafeSetSplitterDistance(split, distance);
            }
        };
    }

    internal static void SetSplitterDistanceWhenReady(SplitContainer split, int distance)
    {
        void Apply()
        {
            if (!split.IsDisposed)
            {
                SafeSetSplitterDistance(split, distance);
            }
        }

        split.HandleCreated += (_, _) =>
        {
            try
            {
                split.BeginInvoke(new Action(Apply));
            }
            catch (InvalidOperationException)
            {
                Apply();
            }
        };
        split.SizeChanged += (_, _) =>
        {
            if (split.SplitterDistance <= split.Panel1MinSize)
            {
                Apply();
            }
        };
    }

    private static bool IsVisibleOnAnyScreen(Rectangle bounds)
    {
        return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }

    private static Button CreateTopToolbarButton(
        string text,
        int width,
        ModernToolbarIcon icon,
        ModernToolbarButtonKind kind,
        Action action)
    {
        var button = new ModernToolbarButton();
        ConfigureTopToolbarButton(button, text, width, icon, kind);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button CreateTopToolbarButton(
        string text,
        int width,
        ModernToolbarIcon icon,
        ModernToolbarButtonKind kind,
        Func<Task> action)
    {
        var button = new ModernToolbarButton();
        ConfigureTopToolbarButton(button, text, width, icon, kind);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void ConfigureTopToolbarButton(
        Button button,
        string text,
        int width,
        ModernToolbarIcon icon,
        ModernToolbarButtonKind kind)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 38;
        button.Margin = new Padding(0, 0, 8, 0);
        if (button is ModernToolbarButton toolbarButton)
        {
            toolbarButton.Icon = icon;
            toolbarButton.Kind = kind;
        }
    }

    private static Control CreateToolbarSeparator()
    {
        return new Panel
        {
            Width = 1,
            Height = 30,
            Margin = new Padding(2, 4, 10, 4),
            BackColor = ModernTheme.BorderSubtleColor,
        };
    }

    private static Control CreateTitledPanel(string title, Control content)
    {
        var card = new ModernCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ShowShadow = true,
        };
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ModernTheme.SurfaceColor,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = ModernTheme.ToolbarColor,
            ForeColor = ModernTheme.TextColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.Controls.Add(content, 0, 1);
        card.Controls.Add(panel);
        return card;
    }

    private void ClearStatusChanges()
    {
        _fileStatusView.Clear();
        _state.SetStatusChanges([]);
    }

    private void SetAllChecks(bool isChecked)
    {
        _fileStatusView.SetAllChecks(isChecked);
    }

    private void SetBusy(bool busy, string text)
    {
        _statusLabel.Text = text;
        _configView.SetBusy(busy);
        _updateButton.Enabled = !busy;
        _statusButton.Enabled = !busy;
        _commitButton.Enabled = !busy;
        _diffButton.Enabled = !busy;
        _externalMergeButton.Enabled = !busy;
        _conflictWorkflowButton.Enabled = !busy;
        _historyButton.Enabled = !busy;
        _historyView.SetBusy(busy, ValidateWorkingCopyPathForBackground());
        _fileStatusView.SetBusy(busy);
        UseWaitCursor = busy;

        if (!busy)
        {
            UpdateHistorySearchControls();
        }
    }

    private void WriteOutput(string output)
    {
        var text = string.IsNullOrWhiteSpace(output) ? "命令没有输出。" : output.Trim();
        _outputText.Text = text;
        _terminalPreviewLabel.Text = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
    }

    private void ShowError(Exception ex)
    {
        OperationLogger.Log("Error", GetWorkingCopyRootPath(), ex.ToString());
        WriteOutput(ex.ToString());
        MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
