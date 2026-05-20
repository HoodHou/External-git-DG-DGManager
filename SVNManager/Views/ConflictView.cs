namespace SVNManager;

internal sealed class ConflictView : UserControl
{
    private readonly Label _summaryLabel = new();
    private readonly DataGridView _grid = new();
    private List<SvnChange> _conflicts = [];

    public event EventHandler? RefreshRequested;
    public event EventHandler<ConflictActionRequestedEventArgs>? ActionRequested;

    public ConflictView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Controls.Add(BuildLayout());
    }

    public IReadOnlyList<SvnChange> Conflicts => _conflicts;

    public string? GetSelectedConflictPath()
    {
        return _grid.SelectedRows.Count == 1 &&
            _grid.SelectedRows[0].DataBoundItem is ConflictGridRow conflict
                ? conflict.RelativePath
                : null;
    }

    public void SetConflicts(IReadOnlyList<SvnChange> conflicts)
    {
        _conflicts = conflicts.OrderBy(change => change.RelativePath).ToList();
        _summaryLabel.Text = _conflicts.Count == 0
            ? "当前没有冲突文件。"
            : $"当前有 {_conflicts.Count} 个冲突文件。请逐个打开合并，确认保存后再标记解决。";
        _summaryLabel.ForeColor = _conflicts.Count == 0 ? Color.FromArgb(45, 100, 65) : Color.DarkRed;
        _grid.DataSource = _conflicts
            .Select(change => new ConflictGridRow(change.RelativePath, change.Description))
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreatePanelToolbar(), 0, 0);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _summaryLabel.Text = "当前没有读取冲突状态。";
        root.Controls.Add(_summaryLabel, 0, 1);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = Color.White;
        _grid.CellContentClick += (_, args) => HandleGridClick(args);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "冲突文件", DataPropertyName = nameof(ConflictGridRow.RelativePath), Width = 620 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", DataPropertyName = nameof(ConflictGridRow.Description), Width = 220 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "内置合并", Name = "InternalMerge", Text = "内置合并", UseColumnTextForButtonValue = true, Width = 110 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "外部合并", Name = "OpenMerge", Text = "外部合并", UseColumnTextForButtonValue = true, Width = 110 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "打开目录", Name = "OpenFolder", Text = "打开目录", UseColumnTextForButtonValue = true, Width = 110 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "标记解决", Name = "Resolve", Text = "标记解决", UseColumnTextForButtonValue = true, Width = 110 });
        root.Controls.Add(_grid, 0, 2);
        return root;
    }

    private Control CreatePanelToolbar()
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
            Text = "冲突文件",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        var button = new ModernButton
        {
            Text = "刷新冲突",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3),
        };
        button.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    private void HandleGridClick(DataGridViewCellEventArgs args)
    {
        if (args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            _grid.Rows[args.RowIndex].DataBoundItem is not ConflictGridRow row)
        {
            return;
        }

        var columnName = _grid.Columns[args.ColumnIndex].Name;
        ActionRequested?.Invoke(this, new ConflictActionRequestedEventArgs(row.RelativePath, columnName));
    }
}

internal sealed record ConflictActionRequestedEventArgs(string RelativePath, string ColumnName);
