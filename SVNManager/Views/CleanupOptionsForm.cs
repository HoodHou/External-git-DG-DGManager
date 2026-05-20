using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed record IgnoreGroup(string ParentPath, IReadOnlyList<string> Names);

internal sealed record CleanupOptions(
    bool CleanWorkingCopyStatus,
    bool BreakWriteLocks,
    bool FixTimeStamps,
    bool VacuumPristineCopies,
    bool RefreshShellOverlays,
    bool IncludeExternals,
    bool DeleteUnversioned,
    bool DeleteIgnored,
    bool RevertAllRecursive)
{
    public bool HasDestructiveActions => DeleteUnversioned || DeleteIgnored || RevertAllRecursive;

    public string ToLogText()
    {
        return string.Join("; ", new[]
        {
            $"cleanStatus={CleanWorkingCopyStatus}",
            $"breakLocks={BreakWriteLocks}",
            $"fixTimeStamps={FixTimeStamps}",
            $"vacuumPristines={VacuumPristineCopies}",
            $"refreshOverlays={RefreshShellOverlays}",
            $"includeExternals={IncludeExternals}",
            $"deleteUnversioned={DeleteUnversioned}",
            $"deleteIgnored={DeleteIgnored}",
            $"revertAll={RevertAllRecursive}",
        });
    }
}

internal sealed class CleanupOptionsForm : Form
{
    private readonly CheckBox _cleanStatus = new() { Text = "清理工作副本状态", Checked = true, AutoSize = true };
    private readonly CheckBox _breakLocks = new() { Text = "解除写入锁", Checked = true, AutoSize = true };
    private readonly CheckBox _fixTimeStamps = new() { Text = "修复文件时间戳", Checked = true, AutoSize = true };
    private readonly CheckBox _vacuumPristines = new() { Text = "清理 .svn 内未使用的原始副本", Checked = true, AutoSize = true };
    private readonly CheckBox _refreshOverlays = new() { Text = "刷新资源管理器图标覆盖", Checked = true, AutoSize = true };
    private readonly CheckBox _includeExternals = new() { Text = "包含 externals 外部目录", Checked = true, AutoSize = true };
    private readonly CheckBox _deleteUnversioned = new() { Text = "删除未加入版本控制的文件和文件夹", AutoSize = true };
    private readonly CheckBox _deleteIgnored = new() { Text = "删除已忽略的文件和文件夹", AutoSize = true };
    private readonly CheckBox _revertAll = new() { Text = "递归还原所有本地改动", AutoSize = true };

    public CleanupOptions Options => new(
        _cleanStatus.Checked,
        _breakLocks.Checked,
        _fixTimeStamps.Checked,
        _vacuumPristines.Checked,
        _refreshOverlays.Checked,
        _includeExternals.Checked,
        _deleteUnversioned.Checked,
        _deleteIgnored.Checked,
        _revertAll.Checked);

    public CleanupOptionsForm(string workingCopyPath)
    {
        Text = "SVN 清理工作副本";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 560;
        Height = 410;
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 14, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = workingCopyPath,
            AutoEllipsis = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        foreach (var checkBox in new[]
        {
            _cleanStatus,
            _breakLocks,
            _fixTimeStamps,
            _vacuumPristines,
            _refreshOverlays,
            _includeExternals,
            _deleteUnversioned,
            _deleteIgnored,
            _revertAll,
        })
        {
            checkBox.Margin = new Padding(0, 0, 0, 8);
            options.Controls.Add(checkBox);
        }

        root.Controls.Add(options, 0, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(110, 70, 20),
            Text = "删除和递归还原类选项默认关闭，执行前会再次确认；“时间戳/图标覆盖”属于 TortoiseSVN 体验项，本工具会尽量刷新自身状态。",
        };
        root.Controls.Add(hint, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var ok = new Button { Text = "确定", Width = 88, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 88, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}

