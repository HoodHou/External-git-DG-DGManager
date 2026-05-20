using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class TextPromptForm : Form
{
    private readonly TextBox _text = new();

    public TextPromptForm(string title, string label, string initialValue)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Width = 460;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _text.Dock = DockStyle.Fill;
        _text.Text = initialValue;
        root.Controls.Add(_text, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var cancelButton = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "确定", Width = 80 };
        okButton.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Shown += (_, _) =>
        {
            _text.SelectAll();
            _text.Focus();
        };
    }

    public string Value => _text.Text.Trim();

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            MessageBox.Show(this, "名称不能为空。", "缺少名称", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

