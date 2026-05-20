using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

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

