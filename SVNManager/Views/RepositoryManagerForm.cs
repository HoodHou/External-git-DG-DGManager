using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class RepositoryManagerForm : Form
{
    private readonly AppSettings _settings;
    private readonly SvnClient _svn;
    private readonly ListView _list = new();
    private readonly Label _summaryLabel = new();

    public RepositoryManagerForm(AppSettings settings, SvnClient svn)
    {
        _settings = settings;
        _svn = svn;
        Text = "本地库管理";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 460);
        Size = new Size(1080, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font(Font, FontStyle.Bold);
        root.Controls.Add(_summaryLabel, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("当前", 58);
        _list.Columns.Add("名称", 140);
        _list.Columns.Add("状态", 150);
        _list.Columns.Add("版本", 150);
        _list.Columns.Add("本地路径", 330);
        _list.Columns.Add("SVN 地址", 360);
        _list.DoubleClick += (_, _) => SetSelectedAsCurrent();
        root.Controls.Add(_list, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var closeButton = new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK };
        var refreshButton = new Button { Text = "刷新", Width = 86 };
        var openButton = new Button { Text = "打开目录", Width = 96 };
        var removeButton = new Button { Text = "移除", Width = 86 };
        var renameButton = new Button { Text = "重命名", Width = 86 };
        var currentButton = new Button { Text = "设为当前", Width = 96 };
        refreshButton.Click += (_, _) => RefreshRows();
        openButton.Click += (_, _) => OpenSelectedFolder();
        removeButton.Click += (_, _) => RemoveSelected();
        renameButton.Click += (_, _) => RenameSelected();
        currentButton.Click += (_, _) => SetSelectedAsCurrent();
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(openButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(renameButton);
        buttons.Controls.Add(currentButton);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = closeButton;
        RefreshRows();
    }

    public bool Changed { get; private set; }

    private RepositoryEntry? SelectedRepository
    {
        get
        {
            if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not string id)
            {
                return null;
            }

            return _settings.Repositories.FirstOrDefault(repository => repository.Id == id);
        }
    }

    private void RefreshRows()
    {
        var selectedId = SelectedRepository?.Id ?? _settings.CurrentRepositoryId;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var repository in _settings.Repositories.OrderBy(repository => repository.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var status = GetRepositoryStatus(repository, out var version);
                var item = new ListViewItem(repository.Id == _settings.CurrentRepositoryId ? "√" : "")
                {
                    Tag = repository.Id,
                    Font = repository.Id == _settings.CurrentRepositoryId ? new Font(_list.Font, FontStyle.Bold) : _list.Font,
                    ForeColor = status.StartsWith("正常", StringComparison.Ordinal)
                        ? SystemColors.WindowText
                        : Color.FromArgb(170, 65, 45),
                };
                item.SubItems.Add(repository.Name);
                item.SubItems.Add(status);
                item.SubItems.Add(version);
                item.SubItems.Add(repository.WorkingCopyPath);
                item.SubItems.Add(repository.RepositoryUrl);
                _list.Items.Add(item);
                if (repository.Id == selectedId)
                {
                    item.Selected = true;
                    item.Focused = true;
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _summaryLabel.Text = $"本地库管理    共 {_settings.Repositories.Count} 个库";
    }

    private string GetRepositoryStatus(RepositoryEntry repository, out string version)
    {
        version = "-";
        if (string.IsNullOrWhiteSpace(repository.WorkingCopyPath))
        {
            return "缺少本地路径";
        }

        if (!Directory.Exists(repository.WorkingCopyPath))
        {
            return "目录不存在";
        }

        if (!Directory.Exists(Path.Combine(repository.WorkingCopyPath, ".svn")))
        {
            return "不是 SVN 工作副本";
        }

        try
        {
            var info = _svn.GetWorkingCopyInfo(repository.WorkingCopyPath);
            version = info == WorkingCopyInfo.Empty ? "未知" : info.DisplayContentRevisionText;
            return "正常 SVN 工作副本";
        }
        catch
        {
            version = "读取失败";
            return "SVN 信息读取失败";
        }
    }

    private void SetSelectedAsCurrent()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        Changed = true;
        RefreshRows();
    }

    private void RenameSelected()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        using var prompt = new TextPromptForm("重命名本地库", "名称", repository.Name);
        if (prompt.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        repository.Name = prompt.Value;
        Changed = true;
        RefreshRows();
    }

    private void RemoveSelected()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        var message =
            $"确定从工具里移除这个本地库吗？{Environment.NewLine}{Environment.NewLine}" +
            $"{repository.Name}{Environment.NewLine}" +
            $"{repository.WorkingCopyPath}{Environment.NewLine}{Environment.NewLine}" +
            "这只会从工具列表移除，不会删除磁盘上的文件。";
        if (MessageBox.Show(this, message, "移除本地库", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _settings.RemoveRepository(repository);

        Changed = true;
        RefreshRows();
    }

    private void OpenSelectedFolder()
    {
        var repository = SelectedRepository;
        if (repository == null)
        {
            return;
        }

        if (!Directory.Exists(repository.WorkingCopyPath))
        {
            MessageBox.Show(this, "本地目录不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", repository.WorkingCopyPath) { UseShellExecute = true });
    }
}

