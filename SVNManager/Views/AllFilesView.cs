namespace SVNManager;

internal sealed class AllFilesView : UserControl
{
    private readonly TreeView _fileTree = new();
    private readonly TextBox _searchText = new();
    private readonly CheckBox _changedOnlyCheck = new();
    private readonly Button _expandButton = new ModernButton();
    private readonly Button _collapseButton = new ModernButton();
    private readonly Button _refreshButton = new ModernButton();
    private IReadOnlyDictionary<string, SvnStatusKind> _statusMap = new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? FilterChanged;
    public event EventHandler? ExpandRequested;
    public event EventHandler? CollapseRequested;
    public event EventHandler? RefreshRequested;

    public AllFilesView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Controls.Add(BuildLayout());
    }

    public TreeView FileTree => _fileTree;
    public TextBox SearchTextBox => _searchText;
    public CheckBox ChangedOnlyCheck => _changedOnlyCheck;
    public Button ExpandButton => _expandButton;
    public Button CollapseButton => _collapseButton;
    public Button RefreshButton => _refreshButton;
    public HashSet<string> SelectedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StyledSelectionPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectionAnchorPath { get; set; }
    public bool IsLoadingFileTree { get; set; }
    public int LastFileCount { get; set; }

    public IReadOnlyDictionary<string, SvnStatusKind> StatusMap
    {
        get => _statusMap;
        set => _statusMap = value ?? new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetLoadingControls(bool loading)
    {
        _refreshButton.Enabled = true;
        _refreshButton.Text = loading ? "停止" : "刷新";
        _searchText.Enabled = !loading;
        _changedOnlyCheck.Enabled = !loading;
        _expandButton.Enabled = !loading;
        _collapseButton.Enabled = !loading;
    }

    private Control BuildLayout()
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
        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(_fileTree, 0, 1);
        return root;
    }

    private Control BuildToolbar()
    {
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

        _searchText.Dock = DockStyle.Fill;
        _searchText.PlaceholderText = "搜索文件名 / 路径";
        _searchText.Margin = new Padding(0, 3, 8, 3);
        _searchText.TextChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(_searchText, 0, 0);

        _changedOnlyCheck.Text = "仅改动";
        _changedOnlyCheck.Dock = DockStyle.Fill;
        _changedOnlyCheck.TextAlign = ContentAlignment.MiddleCenter;
        _changedOnlyCheck.CheckedChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(_changedOnlyCheck, 1, 0);

        _expandButton.Text = "展开";
        _expandButton.Dock = DockStyle.Fill;
        _expandButton.Margin = new Padding(0, 3, 6, 3);
        _expandButton.Click += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(_expandButton, 2, 0);

        _collapseButton.Text = "折叠";
        _collapseButton.Dock = DockStyle.Fill;
        _collapseButton.Margin = new Padding(0, 3, 6, 3);
        _collapseButton.Click += (_, _) => CollapseRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(_collapseButton, 3, 0);

        _refreshButton.Text = "刷新";
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Margin = new Padding(0, 3, 6, 3);
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(_refreshButton, 4, 0);
        return toolbar;
    }
}
