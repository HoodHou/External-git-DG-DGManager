using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

public partial class Form1
{
    private Control CreateConfigPanel()
    {
        _configView.CheckoutRequested += async (_, _) => await RunCheckoutAsync();
        _configView.ChooseWorkingCopyRequested += (_, _) => ChooseWorkingCopy();
        _configView.SaveRepositoryRequested += (_, _) => SaveCurrentRepository();
        _configView.RemoveRepositoryRequested += (_, _) => RemoveCurrentRepository();
        _configView.ManageRepositoriesRequested += (_, _) => ShowRepositoryManagerDialog();
        _configView.OpenFolderRequested += (_, _) => OpenWorkingCopyFolder();
        _configView.EnvironmentCheckRequested += async (_, _) => await ShowEnvironmentCheckAsync();
        _configView.SettingsRequested += (_, _) => ShowSettingsDialog();
        return _configView;
    }


    private void ShowSettingsDialog()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.ExternalMergeToolPath = form.ExternalMergeToolPath;
        _settings.UpdateChannel = form.UpdateChannel;
        _settings.Save();
        WriteOutput(string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath)
            ? $"已清空分久必合路径；工具更新通道：{_settings.UpdateChannel}。"
            : $"已保存分久必合路径：{_settings.ExternalMergeToolPath}；工具更新通道：{_settings.UpdateChannel}。");
    }

    private void ShowRepositoryManagerDialog()
    {
        using var form = new RepositoryManagerForm(_settings, _svn);
        form.ShowDialog(this);
        if (!form.Changed)
        {
            return;
        }

        _settings.Save();
        RefreshRepositorySelector();
        ApplyCurrentRepositoryToUi();
        WriteOutput("已更新本地库列表。");
    }


    private void LoadSettingsIntoUi()
    {
        _settings.MigrateLegacySettings();
        RefreshRepositorySelector();
        var selected = _settings.GetCurrentRepository();
        _configView.RepositoryUrl = selected?.RepositoryUrl ?? "";
        _configView.WorkingCopyPath = selected?.WorkingCopyPath ?? "";
        LoadAllFiles();
        SelectTab(string.IsNullOrWhiteSpace(_settings.UiLayout.SelectedTab) ? "History" : _settings.UiLayout.SelectedTab);
    }

    private async Task RunCheckoutAsync()
    {
        if (!ValidateRepositoryUrl() || !ValidateWorkingCopyPath(allowMissing: true))
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        if (Directory.Exists(workingCopy) && Directory.EnumerateFileSystemEntries(workingCopy).Any())
        {
            if (TryGetWorkingCopyContext() != null)
            {
                SaveCurrentRepository();
                WriteOutput($"已保存已有 SVN 工作副本：{workingCopy}");
                await RefreshStatusAsync();
                return;
            }

            MessageBox.Show("本地目录不是空目录。为了避免覆盖已有文件，请选择一个空目录或已有 SVN 工作副本。", "无法检出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunSvnOperationAsync("正在检出...", async () =>
        {
            Directory.CreateDirectory(workingCopy);
            var result = await _svn.CheckoutAsync(_configView.RepositoryUrl.Trim(), workingCopy);
            SaveCurrentRepository();
            return result;
        });
        await RefreshStatusAsync();
    }

    private async Task RunUpdateAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var context = GetRequiredWorkingCopyContext();
        var workingCopy = context.SelectedPath;
        var preflightChanges = FilterChangesForCurrentScope(await _svn.GetStatusAsync(context.RootPath));
        if (!ConfirmUpdateWithLocalChanges(preflightChanges))
        {
            OperationLogger.Log("UpdateCancelled", workingCopy, $"localChanges={preflightChanges.Count}");
            WriteOutput("已取消拉取最新：当前有未提交改动。");
            return;
        }

        OperationLogger.Log("UpdateStart", workingCopy, $"localChanges={preflightChanges.Count}");
        await RunSvnOperationAsync("正在拉取最新...", async () =>
        {
            SaveSettings();
            return await _svn.UpdateAsync(workingCopy);
        });
        OperationLogger.Log("UpdateFinish", workingCopy, "svn update finished");
        await RefreshStatusAsync();
        await CheckRemoteChangesAsync(showUpToDateMessage: false);
    }


    private void ChooseWorkingCopy()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 SVN 工作目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_configView.WorkingCopyPath.Trim()) ? _configView.WorkingCopyPath.Trim() : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _configView.WorkingCopyPath = dialog.SelectedPath;
            SaveCurrentRepository();
        }
    }

    private void OpenWorkingCopyFolder()
    {
        var path = _configView.WorkingCopyPath.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show("本地目录不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }


    private bool ValidateRepositoryUrl()
    {
        if (!string.IsNullOrWhiteSpace(_configView.RepositoryUrl))
        {
            return true;
        }

        MessageBox.Show("请填写 SVN 地址。", "缺少 SVN 地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void SaveSettings()
    {
        _settings.UpsertRepository(_configView.RepositoryUrl.Trim(), _configView.WorkingCopyPath.Trim());
        _settings.Save();
        RefreshRepositorySelector();
    }

    private void SaveCurrentRepository()
    {
        if (!ValidateWorkingCopyPath(allowMissing: true))
        {
            return;
        }

        SaveSettings();
        WriteOutput($"已保存本地库：{_configView.WorkingCopyPath.Trim()}");
    }

    private void RemoveCurrentRepository()
    {
        var repository = GetRepositorySelectedForRemoval();
        if (repository == null)
        {
            MessageBox.Show("请先在左侧本地库或顶部下拉框里选中要移除的库。", "没有选中本地库", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"确定从工具里移除这个本地库吗？{Environment.NewLine}{Environment.NewLine}" +
            $"{repository.Name}{Environment.NewLine}" +
            $"{repository.WorkingCopyPath}{Environment.NewLine}{Environment.NewLine}" +
            "这只会从工具列表移除，不会删除磁盘上的文件。";
        if (MessageBox.Show(message, "移除本地库", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _settings.RemoveRepository(repository);

        _settings.Save();
        RefreshRepositorySelector();
        ApplyCurrentRepositoryToUi();
        WriteOutput($"已从本地库列表移除：{repository.Name}");
    }

    private RepositoryEntry? GetRepositorySelectedForRemoval()
    {
        if (_repositoryTree.SelectedNode?.Tag is RepositoryEntry treeRepository)
        {
            return treeRepository;
        }

        if (_repositorySelector.SelectedItem is RepositoryEntry selectorRepository)
        {
            return selectorRepository;
        }

        return _settings.GetCurrentRepository();
    }

    private void RefreshRepositorySelector()
    {
        _loadingRepository = true;
        try
        {
            _repositorySelector.Items.Clear();
            foreach (var repository in _settings.Repositories)
            {
                _repositorySelector.Items.Add(repository);
            }

            var selected = _settings.GetCurrentRepository();
            if (selected != null)
            {
                _repositorySelector.SelectedItem = selected;
            }
            else if (_repositorySelector.Items.Count > 0)
            {
                _repositorySelector.SelectedIndex = 0;
            }
        }
        finally
        {
            _loadingRepository = false;
        }

        RefreshRepositoryTree();
    }

    private void RefreshRepositoryTree()
    {
        if (_repositoryTree.IsDisposed)
        {
            return;
        }

        var wasLoading = _loadingRepository;
        _loadingRepository = true;
        try
        {
            _repositoryTree.BeginUpdate();
            _repositoryTree.Nodes.Clear();
            var repositoriesNode = new TreeNode("本地库")
            {
                ImageKey = "folder",
                SelectedImageKey = "folder",
            };
            _repositoryTree.Nodes.Add(repositoriesNode);
            foreach (var repository in _settings.Repositories)
            {
                var node = new TreeNode(repository.Name)
                {
                    Tag = repository,
                    ToolTipText = repository.WorkingCopyPath,
                    ImageKey = "repo",
                    SelectedImageKey = "repo",
                    ForeColor = repository.Id == _settings.CurrentRepositoryId
                        ? Color.FromArgb(0, 92, 175)
                        : SystemColors.WindowText,
                    NodeFont = repository.Id == _settings.CurrentRepositoryId
                        ? new Font(_repositoryTree.Font, FontStyle.Bold)
                        : _repositoryTree.Font,
                };
                repositoriesNode.Nodes.Add(node);
                if (repository.Id == _settings.CurrentRepositoryId)
                {
                    _repositoryTree.SelectedNode = node;
                    node.EnsureVisible();
                }
            }

            repositoriesNode.Expand();
        }
        finally
        {
            _repositoryTree.EndUpdate();
            _loadingRepository = wasLoading;
        }
    }

    private void SelectRepositoryFromList()
    {
        if (_loadingRepository || _repositorySelector.SelectedItem is not RepositoryEntry repository)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        _settings.Save();
        RefreshRepositoryTree();
        ApplyCurrentRepositoryToUi();
    }

    private void ApplyCurrentRepositoryToUi()
    {
        var selected = _settings.GetCurrentRepository();
        _configView.RepositoryUrl = selected?.RepositoryUrl ?? "";
        _configView.WorkingCopyPath = selected?.WorkingCopyPath ?? "";
        ClearStatusChanges();
        RefreshConflictPanel([]);
        UpdateStatusBadges(0, 0);
        _historyView.ClearForRepositoryChange(InitialHistoryLimit);
        UpdateHistoryBadge(0);
        ClearHistoryDiffPanel();
        SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未检查", SystemColors.ControlText, "尚未读取当前工作副本版本。");
        _remoteStatusLabel.Text = "远端：未检查";
        _remoteStatusLabel.ForeColor = SystemColors.ControlText;
        UpdateHistorySearchControls();
        LoadAllFiles();
    }

}

