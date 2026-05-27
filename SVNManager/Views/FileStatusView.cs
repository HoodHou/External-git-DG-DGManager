namespace SVNManager;

internal sealed class FileStatusView : UserControl
{
    private readonly ListView _changesList = new();
    private readonly TextBox _searchText = new();
    private readonly ComboBox _filterCombo = new();
    private readonly CheckBox _commitVisibleOnlyCheck = new();
    private readonly Label _summaryLabel = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly HashSet<string> _checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private List<SvnChange> _changesAll = [];
    private bool _updatingList;

    public event EventHandler? RefreshRequested;
    public event EventHandler? DiffRequested;
    public event EventHandler? CompareTableRequested;
    public event EventHandler? InternalMergeRequested;
    public event EventHandler? CrossMergeRequested;
    public event EventHandler? OpenFileRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? UnlockRequested;
    public event EventHandler? LockInfoRequested;
    public event EventHandler? RevertToLatestRequested;
    public event EventHandler? AddIgnoreRequested;
    public event EventHandler? RemoveIgnoreRequested;

    public FileStatusView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Controls.Add(BuildLayout());
    }

    public IReadOnlyList<SvnChange> AllChanges => _changesAll;
    public int CheckedCount => _checkedPaths.Count;
    public bool CommitVisibleOnly => _commitVisibleOnlyCheck.Checked;

    public void SetChanges(IReadOnlyList<SvnChange> changes)
    {
        _changesAll = changes.OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        _checkedPaths.Clear();
        foreach (var change in _changesAll.Where(change => change.CanCommit))
        {
            _checkedPaths.Add(NormalizeRelativePath(change.RelativePath));
        }

        ApplyFilter();
    }

    public void Clear()
    {
        _changesAll = [];
        _checkedPaths.Clear();
        _changesList.Items.Clear();
        UpdateSummary(0);
    }

    public void SetBusy(bool busy)
    {
        _commitVisibleOnlyCheck.Enabled = !busy;
        _searchText.Enabled = !busy;
        _filterCombo.Enabled = !busy;
    }

    public void SetAllChecks(bool isChecked)
    {
        foreach (ListViewItem item in _changesList.Items)
        {
            if (item.Tag is SvnChange { CanCommit: true })
            {
                item.Checked = isChecked;
            }
        }

        UpdateSummary(_changesList.Items.Count);
    }

    public SvnChange? GetSelectedChange()
    {
        return _changesList.SelectedItems.Count == 1 && _changesList.SelectedItems[0].Tag is SvnChange change
            ? change
            : null;
    }

    public List<SvnChange> GetSelectedChanges()
    {
        return _changesList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnChange)
            .Where(change => change != null)
            .Cast<SvnChange>()
            .ToList();
    }

    public List<string> GetCommitSelectedPaths()
    {
        if (_commitVisibleOnlyCheck.Checked)
        {
            return _changesList.Items
                .Cast<ListViewItem>()
                .Where(item => item.Checked && item.Tag is SvnChange)
                .Select(item => ((SvnChange)item.Tag!).RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _checkedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Control BuildLayout()
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
        root.Controls.Add(CreateToolbar(), 0, 0);
        root.Controls.Add(CreateFilterBar(), 0, 1);
        ConfigureChangesList();
        root.Controls.Add(_changesList, 0, 2);
        return root;
    }

    private Control CreateToolbar()
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
        panel.Controls.Add(CreateToolbarButton("刷新改动", () => RefreshRequested?.Invoke(this, EventArgs.Empty)), 1, 0);
        panel.Controls.Add(CreateToolbarButton("全选", () => SetAllChecks(true)), 2, 0);
        panel.Controls.Add(CreateToolbarButton("全不选", () => SetAllChecks(false)), 3, 0);
        return panel;
    }

    private Control CreateFilterBar()
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

        _searchText.Dock = DockStyle.Fill;
        _searchText.Margin = new Padding(0, 4, 6, 4);
        _searchText.PlaceholderText = "搜索文件名 / 路径";
        _searchText.TextChanged += (_, _) => ApplyFilter();
        panel.Controls.Add(_searchText, 0, 0);

        _filterCombo.Dock = DockStyle.Fill;
        _filterCombo.Margin = new Padding(0, 4, 6, 4);
        _filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var text in ChangedFilesFilter.Options)
        {
            _filterCombo.Items.Add(text);
        }

        _filterCombo.SelectedIndex = 0;
        _filterCombo.SelectedIndexChanged += (_, _) => ApplyFilter();
        panel.Controls.Add(_filterCombo, 1, 0);

        _commitVisibleOnlyCheck.Dock = DockStyle.Fill;
        _commitVisibleOnlyCheck.Margin = new Padding(0, 5, 8, 4);
        _commitVisibleOnlyCheck.Text = "只提交当前筛选结果";
        _commitVisibleOnlyCheck.TextAlign = ContentAlignment.MiddleLeft;
        _commitVisibleOnlyCheck.CheckedChanged += (_, _) => UpdateSummary(_changesList.Items.Count);
        panel.Controls.Add(_commitVisibleOnlyCheck, 2, 0);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.ForeColor = Color.FromArgb(90, 100, 115);
        _summaryLabel.Text = "显示 0/0，已勾选 0";
        panel.Controls.Add(_summaryLabel, 3, 0);
        return panel;
    }

    private void ConfigureChangesList()
    {
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
        _changesList.ItemChecked += (_, args) => TrackItemChecked(args.Item);
        BuildContextMenu();
        _changesList.ContextMenuStrip = _menu;
    }

    private void BuildContextMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("查看差异", null, (_, _) => DiffRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("和另一个表快速比对...", null, (_, _) => CompareTableRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("内置三方合并", null, (_, _) => InternalMergeRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("跨库表格三方合并", null, (_, _) => CrossMergeRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("打开文件", null, (_, _) => OpenFileRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("打开所在目录", null, (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("锁定文件", null, (_, _) => LockRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("解锁文件", null, (_, _) => UnlockRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("查看锁信息", null, (_, _) => LockInfoRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("还原到 SVN 最新版本...", null, (_, _) => RevertToLatestRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("加入忽略清单", null, (_, _) => AddIgnoreRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("移出忽略清单", null, (_, _) => RemoveIgnoreRequested?.Invoke(this, EventArgs.Empty));
        _menu.Opening += (_, args) =>
        {
            var selected = GetSelectedChanges();
            args.Cancel = selected.Count == 0;
            foreach (ToolStripItem item in _menu.Items)
            {
                if (item is not ToolStripSeparator)
                {
                    item.Enabled = selected.Count > 0;
                }
            }
        };
    }

    private void ApplyFilter()
    {
        var selectedPath = GetSelectedChange()?.RelativePath;
        var filtered = ChangedFilesFilter.ApplyStatusChanges(
            _changesAll,
            _searchText.Text,
            ChangedFilesFilter.GetMode(_filterCombo));

        _updatingList = true;
        _changesList.BeginUpdate();
        _changesList.Items.Clear();
        try
        {
            ListViewItem? itemToSelect = null;
            foreach (var change in filtered)
            {
                var item = CreateItem(change);
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
            _updatingList = false;
        }

        UpdateSummary(filtered.Count);
    }

    private ListViewItem CreateItem(SvnChange change)
    {
        var item = new ListViewItem(change.DisplayStatus)
        {
            Tag = change,
            Checked = change.CanCommit && _checkedPaths.Contains(NormalizeRelativePath(change.RelativePath)),
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

    private void TrackItemChecked(ListViewItem item)
    {
        if (_updatingList || item.Tag is not SvnChange change)
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

            _checkedPaths.Remove(key);
            UpdateSummary(_changesList.Items.Count);
            return;
        }

        if (item.Checked)
        {
            _checkedPaths.Add(key);
        }
        else
        {
            _checkedPaths.Remove(key);
        }

        UpdateSummary(_changesList.Items.Count);
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

    private void UpdateSummary(int visibleCount)
    {
        var scopeText = _commitVisibleOnlyCheck.Checked ? "提交当前" : "提交全部";
        _summaryLabel.Text = $"显示 {visibleCount}/{_changesAll.Count}，已勾选 {_checkedPaths.Count}，{scopeText}";
    }

    private static Button CreateToolbarButton(string text, Action action)
    {
        var button = new ModernButton
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
