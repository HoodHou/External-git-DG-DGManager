namespace SVNManager;

internal sealed class XmlMergeConflictForm : Form
{
    private static readonly Font BaseFont = new("Microsoft YaHei UI", 9F);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);

    private readonly XmlMergePlan _plan;
    private readonly List<XmlMergeGridRow> _rows;
    private readonly BindingSource _source = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summaryLabel = new();
    private readonly TextBox _detailBox = new();
    private readonly string _keepText;
    private readonly string _useRemoteText;

    public XmlMergeConflictForm(
        string relativePath,
        XmlMergePlan plan,
        string targetLabel = "本地",
        string sourceLabel = "远端 HEAD",
        string applyButtonText = "写入工作副本")
    {
        _plan = plan;
        _keepText = $"保留{targetLabel}";
        _useRemoteText = $"使用{sourceLabel}";
        _rows = plan.AllChanges
            .Select(change => new XmlMergeGridRow(change, _keepText, _useRemoteText))
            .ToList();

        Text = $"普通 XML 三方合并 - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1050, 660);
        Size = new Size(1260, 780);
        Font = BaseFont;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = TitleFont;
        root.Controls.Add(_summaryLabel, 0, 0);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);

        _detailBox.Dock = DockStyle.Fill;
        _detailBox.Multiline = true;
        _detailBox.ReadOnly = true;
        _detailBox.ScrollBars = ScrollBars.Both;
        _detailBox.Font = new Font("Consolas", 9F);
        root.Controls.Add(_detailBox, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var applyButton = new Button { Text = applyButtonText, Width = 118 };
        var cancelButton = new Button { Text = "取消", Width = 86, DialogResult = DialogResult.Cancel };
        var allRemoteButton = new Button { Text = $"全部{sourceLabel}", Width = 118 };
        var allLocalButton = new Button { Text = $"全部{targetLabel}", Width = 118 };
        applyButton.Click += (_, _) =>
        {
            _grid.EndEdit();
            ApplyRowsToPlan();
            DialogResult = DialogResult.OK;
            Close();
        };
        allRemoteButton.Click += (_, _) => SetAll(_useRemoteText);
        allLocalButton.Click += (_, _) => SetAll(_keepText);
        buttons.Controls.Add(applyButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(allRemoteButton);
        buttons.Controls.Add(allLocalButton);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = applyButton;
        CancelButton = cancelButton;
        UpdateSummary();
        UpdateDetail();
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
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Color.FromArgb(226, 232, 240);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        _grid.ColumnHeadersDefaultCellStyle.Font = TitleFont;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        _grid.RowTemplate.Height = 68;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "处理",
            DataPropertyName = nameof(XmlMergeGridRow.OperationText),
            Width = 120,
            DataSource = new[] { _keepText, _useRemoteText },
            FlatStyle = FlatStyle.Flat,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = nameof(XmlMergeGridRow.KindText), Width = 96, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "动作", DataPropertyName = nameof(XmlMergeGridRow.ActionText), Width = 116, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(XmlMergeGridRow.DisplayName), Width = 120, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "XML 路径", DataPropertyName = nameof(XmlMergeGridRow.Path), Width = 360, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "差异",
            DataPropertyName = nameof(XmlMergeGridRow.ComparisonText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
            DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
        });
        _grid.SelectionChanged += (_, _) => UpdateDetail();
        _grid.CellValueChanged += (_, _) => UpdateSummary();
        _grid.DataBindingComplete += (_, _) => ApplyRowStyles();

        _source.DataSource = _rows;
        _grid.DataSource = _source;
    }

    private void SetAll(string operationText)
    {
        foreach (var row in _rows)
        {
            row.OperationText = operationText;
        }

        _source.ResetBindings(false);
        UpdateSummary();
        ApplyRowStyles();
        UpdateDetail();
    }

    private void ApplyRowsToPlan()
    {
        foreach (var row in _rows)
        {
            row.Apply();
        }
    }

    private void UpdateSummary()
    {
        var selectedRemote = _rows.Count(row => row.OperationText == _useRemoteText);
        _summaryLabel.Text =
            $"自动应用 {_plan.AutoRemoteChanges.Count}，本地保留 {_plan.LocalOnlyChanges.Count}，双方相同 {_plan.SameBothChanges.Count}，冲突 {_plan.Conflicts.Count}；" +
            $"当前将写入 {selectedRemote} 项远端 XML 改动";
    }

    private void UpdateDetail()
    {
        if (_grid.CurrentRow?.DataBoundItem is not XmlMergeGridRow row)
        {
            _detailBox.Text = "";
            return;
        }

        _detailBox.Text =
            $"路径: {row.Path}{Environment.NewLine}" +
            $"动作: {row.ActionText}{Environment.NewLine}" +
            $"BASE: {row.BaseValue}{Environment.NewLine}" +
            $"本地: {row.LocalValue}{Environment.NewLine}" +
            $"远端: {row.RemoteValue}";
    }

    private void ApplyRowStyles()
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is not XmlMergeGridRow row)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = row.Kind switch
            {
                XmlMergeChangeKind.AutoRemote => Color.FromArgb(235, 255, 239),
                XmlMergeChangeKind.LocalOnly => Color.FromArgb(239, 246, 255),
                XmlMergeChangeKind.SameBoth => Color.FromArgb(248, 250, 252),
                XmlMergeChangeKind.Conflict => Color.FromArgb(255, 247, 237),
                _ => Color.White,
            };
        }
    }

    private sealed class XmlMergeGridRow
    {
        private readonly XmlMergeChange _change;
        private readonly string _keepText;
        private readonly string _useRemoteText;

        public XmlMergeGridRow(XmlMergeChange change, string keepText, string useRemoteText)
        {
            _change = change;
            _keepText = keepText;
            _useRemoteText = useRemoteText;
            OperationText = change.Resolution == XmlMergeResolution.UseRemote ? _useRemoteText : _keepText;
        }

        public XmlMergeChangeKind Kind => _change.Kind;
        public string KindText => _change.Kind switch
        {
            XmlMergeChangeKind.AutoRemote => "可合并",
            XmlMergeChangeKind.LocalOnly => "本地独有",
            XmlMergeChangeKind.SameBoth => "双方相同",
            XmlMergeChangeKind.Conflict => "冲突",
            _ => "",
        };
        public string ActionText => _change.ActionKind switch
        {
            XmlMergeActionKind.SetAttribute => "设置属性",
            XmlMergeActionKind.RemoveAttribute => "删除属性",
            XmlMergeActionKind.SetText => "设置文本",
            XmlMergeActionKind.AddElement => "新增节点",
            XmlMergeActionKind.DeleteElement => "删除节点",
            _ => "",
        };
        public string DisplayName => _change.DisplayName;
        public string Path => _change.Path;
        public string BaseValue => Shorten(_change.BaseValue);
        public string LocalValue => Shorten(_change.LocalValue);
        public string RemoteValue => Shorten(_change.RemoteValue);
        public string ComparisonText => $"本地: {LocalValue}{Environment.NewLine}远端: {RemoteValue}";
        public string OperationText { get; set; }

        public void Apply()
        {
            _change.Resolution = OperationText == _useRemoteText
                ? XmlMergeResolution.UseRemote
                : XmlMergeResolution.KeepTarget;
        }

        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var text = value.Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            return text.Length <= 260 ? text : text[..260] + "...";
        }
    }
}
