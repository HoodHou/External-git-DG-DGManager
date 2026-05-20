using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class EnvironmentCheckForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly IReadOnlyList<EnvironmentCheckItem> _items;

    public EnvironmentCheckForm(IReadOnlyList<EnvironmentCheckItem> items)
    {
        _items = items;
        Text = "环境检测";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 460);
        Size = new Size(980, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        var errors = items.Count(item => item.Level == EnvironmentCheckLevel.Error);
        var warnings = items.Count(item => item.Level == EnvironmentCheckLevel.Warning);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = errors > 0 ? Color.DarkRed : warnings > 0 ? Color.FromArgb(166, 103, 34) : Color.FromArgb(45, 100, 65),
            Text = errors == 0 && warnings == 0
                ? "环境检测通过"
                : $"环境检测发现 {errors} 个错误、{warnings} 个提醒",
        }, 0, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目", DataPropertyName = nameof(EnvironmentCheckItem.Name), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = nameof(EnvironmentCheckItem.Status), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", DataPropertyName = nameof(EnvironmentCheckItem.Detail), Width = 360, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "建议", DataPropertyName = nameof(EnvironmentCheckItem.Suggestion), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        _grid.DataSource = items.ToList();
        _grid.DataBindingComplete += (_, _) => ApplyRowStyles();
        root.Controls.Add(_grid, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var closeButton = new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK };
        buttons.Controls.Add(closeButton);
        root.Controls.Add(buttons, 0, 2);
        AcceptButton = closeButton;
    }

    private void ApplyRowStyles()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Index < 0 || row.Index >= _items.Count)
            {
                continue;
            }

            var item = _items[row.Index];
            row.DefaultCellStyle.ForeColor = item.Level switch
            {
                EnvironmentCheckLevel.Error => Color.DarkRed,
                EnvironmentCheckLevel.Warning => Color.FromArgb(166, 103, 34),
                _ => Color.FromArgb(45, 100, 65),
            };
        }
    }
}

internal sealed record EnvironmentCheckItem(string Name, string Status, string Detail, string Suggestion, EnvironmentCheckLevel Level)
{
    public static EnvironmentCheckItem Ok(string name, string status, string detail)
    {
        return new EnvironmentCheckItem(name, status, detail, "", EnvironmentCheckLevel.Ok);
    }

    public static EnvironmentCheckItem Warning(string name, string status, string suggestion)
    {
        return new EnvironmentCheckItem(name, status, "", suggestion, EnvironmentCheckLevel.Warning);
    }

    public static EnvironmentCheckItem Error(string name, string status, string suggestion)
    {
        return new EnvironmentCheckItem(name, status, "", suggestion, EnvironmentCheckLevel.Error);
    }
}

internal enum EnvironmentCheckLevel
{
    Ok,
    Warning,
    Error,
}

