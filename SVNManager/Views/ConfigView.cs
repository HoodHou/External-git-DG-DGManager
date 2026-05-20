namespace SVNManager;

internal sealed class ConfigView : UserControl
{
    private readonly TextBox _repositoryUrlText = new();
    private readonly TextBox _workingCopyText = new();
    private readonly Button _checkoutButton = new ModernButton();

    public event EventHandler? CheckoutRequested;
    public event EventHandler? ChooseWorkingCopyRequested;
    public event EventHandler? SaveRepositoryRequested;
    public event EventHandler? RemoveRepositoryRequested;
    public event EventHandler? ManageRepositoriesRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? EnvironmentCheckRequested;
    public event EventHandler? SettingsRequested;

    public ConfigView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Controls.Add(BuildLayout());
    }

    public string RepositoryUrl
    {
        get => _repositoryUrlText.Text;
        set => _repositoryUrlText.Text = value ?? "";
    }

    public string WorkingCopyPath
    {
        get => _workingCopyText.Text;
        set => _workingCopyText.Text = value ?? "";
    }

    public void SetBusy(bool busy)
    {
        _repositoryUrlText.Enabled = !busy;
        _workingCopyText.Enabled = !busy;
        _checkoutButton.Enabled = !busy;
    }

    private Control BuildLayout()
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
        _repositoryUrlText.Dock = DockStyle.Fill;
        fields.Controls.Add(_repositoryUrlText, 1, 0);
        _checkoutButton.Text = "检出";
        _checkoutButton.Dock = DockStyle.Fill;
        _checkoutButton.Click += (_, _) => CheckoutRequested?.Invoke(this, EventArgs.Empty);
        fields.Controls.Add(_checkoutButton, 2, 0);

        fields.Controls.Add(new Label { Text = "本地目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        _workingCopyText.Dock = DockStyle.Fill;
        fields.Controls.Add(_workingCopyText, 1, 1);
        var chooseButton = new ModernButton { Text = "选择", Dock = DockStyle.Fill };
        chooseButton.Click += (_, _) => ChooseWorkingCopyRequested?.Invoke(this, EventArgs.Empty);
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
        actions.Controls.Add(CreateActionButton("导入已有工作副本", () => ChooseWorkingCopyRequested?.Invoke(this, EventArgs.Empty), 132));
        actions.Controls.Add(CreateActionButton("保存当前库", () => SaveRepositoryRequested?.Invoke(this, EventArgs.Empty), 104));
        actions.Controls.Add(CreateActionButton("移除当前库", () => RemoveRepositoryRequested?.Invoke(this, EventArgs.Empty), 104));
        actions.Controls.Add(CreateActionButton("管理本地库", () => ManageRepositoriesRequested?.Invoke(this, EventArgs.Empty), 104));
        actions.Controls.Add(CreateActionButton("打开目录", () => OpenFolderRequested?.Invoke(this, EventArgs.Empty), 92));
        actions.Controls.Add(CreateActionButton("环境检测", () => EnvironmentCheckRequested?.Invoke(this, EventArgs.Empty), 92));
        actions.Controls.Add(CreateActionButton("设置", () => SettingsRequested?.Invoke(this, EventArgs.Empty), 82));
        root.Controls.Add(actions, 0, 1);

        return root;
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
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            BackColor = Color.FromArgb(241, 243, 245),
            Padding = new Padding(8, 0, 0, 0),
        }, 0, 0);
        content.Dock = DockStyle.Fill;
        panel.Controls.Add(content, 0, 1);
        return panel;
    }

    private static Button CreateActionButton(string text, Action action, int width)
    {
        var button = new ModernButton
        {
            Text = text,
            Width = width,
            Height = 30,
            Margin = new Padding(0, 0, 8, 8),
        };
        button.Click += (_, _) => action();
        return button;
    }
}
