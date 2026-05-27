using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _externalMergeToolText = new();
    private readonly ComboBox _updateChannelBox = new();

    public SettingsForm(AppSettings settings)
    {
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 290;
        MinimumSize = new Size(600, 260);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
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

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "工具更新通道",
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);

        var updateRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _updateChannelBox.Dock = DockStyle.Fill;
        _updateChannelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _updateChannelBox.Items.AddRange(new object[] { "stable", "beta" });
        _updateChannelBox.SelectedItem = string.Equals(settings.UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
        updateRow.Controls.Add(_updateChannelBox, 0, 0);
        updateRow.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "stable 给普通用户使用；beta 用来先试用新包。",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 1, 0);
        root.Controls.Add(updateRow, 0, 3);

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
        root.Controls.Add(bottom, 0, 4);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    public string ExternalMergeToolPath => _externalMergeToolText.Text.Trim();
    public string UpdateChannel => _updateChannelBox.SelectedItem?.ToString() == "beta" ? "beta" : "stable";

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

