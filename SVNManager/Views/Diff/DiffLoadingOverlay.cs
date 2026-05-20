namespace SVNManager;

internal sealed class DiffLoadingOverlay : Form
{
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _cancelButton = new();

    public DiffLoadingOverlay(string title, Action cancel)
    {
        Text = "正在生成差异";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Size = new Size(420, 150);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        _statusLabel.Text = $"正在计算差异：{title}";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        root.Controls.Add(_statusLabel, 0, 0);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Style = ProgressBarStyle.Marquee;
        root.Controls.Add(_progressBar, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        _cancelButton.Text = "取消";
        _cancelButton.Width = 86;
        _cancelButton.Click += (_, _) =>
        {
            _statusLabel.Text = "正在取消...";
            _cancelButton.Enabled = false;
            cancel();
        };
        buttons.Controls.Add(_cancelButton);
        root.Controls.Add(buttons, 0, 2);
    }

    public void SetStatus(string status)
    {
        if (!IsDisposed)
        {
            _statusLabel.Text = status;
        }
    }

    public void SetProgress(DiffProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusLabel.Text = progress.Message;
        if (progress.Percent < 0)
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            return;
        }

        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = Math.Clamp(progress.Percent, _progressBar.Minimum, _progressBar.Maximum);
    }
}
