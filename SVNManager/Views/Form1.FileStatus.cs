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
    private Control CreateStatusPanel()
    {
        _fileStatusView.RefreshRequested += async (_, _) => await RefreshStatusAsync();
        _fileStatusView.DiffRequested += async (_, _) => await RunDiffAsync();
        _fileStatusView.CompareTableRequested += async (_, _) => await CompareSelectedTableWithAnotherAsync();
        _fileStatusView.InternalMergeRequested += async (_, _) => await RunInternalSpreadsheetMergeAsync();
        _fileStatusView.CrossMergeRequested += async (_, _) => await RunCrossRepositorySpreadsheetMergeAsync();
        _fileStatusView.OpenFileRequested += (_, _) => OpenSelectedStatusFile();
        _fileStatusView.OpenFolderRequested += (_, _) => OpenSelectedStatusFileFolder();
        _fileStatusView.LockRequested += async (_, _) => await LockSelectedFileAsync();
        _fileStatusView.UnlockRequested += async (_, _) => await UnlockSelectedFileAsync();
        _fileStatusView.LockInfoRequested += async (_, _) => await ShowSelectedFileLockInfoAsync();
        _fileStatusView.RevertToLatestRequested += async (_, _) => await RevertSelectedStatusChangesToLatestAsync();
        _fileStatusView.AddIgnoreRequested += async (_, _) => await AddSelectedPathsToIgnoreAsync();
        _fileStatusView.RemoveIgnoreRequested += async (_, _) => await RemoveSelectedPathsFromIgnoreAsync();
        return _fileStatusView;
    }


    private bool ConfirmUpdateWithLocalChanges(IReadOnlyList<SvnChange> changes)
    {
        if (changes.Count == 0)
        {
            return true;
        }

        var conflicts = changes.Count(change => change.Status == SvnStatusKind.Conflicted);
        var unversioned = changes.Count(change => change.Status == SvnStatusKind.Unversioned);
        var message =
            $"当前有 {changes.Count} 个未提交改动。直接拉取最新可能产生冲突。{Environment.NewLine}{Environment.NewLine}" +
            $"冲突：{conflicts} 个{Environment.NewLine}" +
            $"未加入版本控制：{unversioned} 个{Environment.NewLine}{Environment.NewLine}" +
            "建议先查看改动、提交或备份，再拉取最新。仍然继续拉取？";
        var result = MessageBox.Show(
            message,
            "拉取最新前确认",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.OK;
    }

    private async Task RunCleanupAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        using var form = new CleanupOptionsForm(workingCopy);
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var options = form.Options;
        if (options.HasDestructiveActions)
        {
            var destructive = new List<string>();
            if (options.DeleteUnversioned) destructive.Add("删除未加入版本控制的文件和文件夹");
            if (options.DeleteIgnored) destructive.Add("删除已忽略的文件和文件夹");
            if (options.RevertAllRecursive) destructive.Add("递归还原所有本地改动");
            var message =
                "你选择了会删除或丢弃本地内容的 Clean Up 选项：" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, destructive.Select(item => "- " + item)) +
                Environment.NewLine + Environment.NewLine +
                "这些操作不能在工具内撤销。确定继续？";
            if (MessageBox.Show(message, "危险操作确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                OperationLogger.Log("CleanupCancelled", workingCopy, options.ToLogText());
                return;
            }
        }

        OperationLogger.Log("CleanupStart", workingCopy, options.ToLogText());
        var result = await RunSvnOperationAsync("正在执行 SVN 清理...", async () => await _svn.CleanupAsync(workingCopy, options));
        OperationLogger.Log(result?.ExitCode == 0 ? "CleanupSuccess" : "CleanupFailed", workingCopy, "");
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ShowIgnoreListAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        SetBusy(true, "正在读取忽略清单...");
        try
        {
            var result = await _svn.GetIgnoreListAsync(workingCopy);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                WriteOutput(result.StandardOutput);
                return;
            }

            if (result.ExitCode == 0 || IsMissingIgnoreProperty(result))
            {
                WriteOutput("当前工作副本没有设置 svn:ignore。");
                return;
            }

            WriteOutput(result.CombinedOutput);
            MessageBox.Show("读取忽略清单失败，请查看终端输出。", "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task AddSelectedPathsToIgnoreAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var selectedPaths = GetSelectedIgnoreCandidatePaths();
        if (selectedPaths.Count == 0)
        {
            MessageBox.Show("请先在 File Status 或全部文件里选中要忽略的文件/文件夹。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var statusMap = (await _svn.GetStatusAsync(workingCopy))
            .ToDictionary(change => NormalizeRelativePath(change.RelativePath), change => change.Status, StringComparer.OrdinalIgnoreCase);
        var targetPairs = selectedPaths
            .Select(path => new { Selected = path, Target = FindUnversionedIgnoreTarget(path, statusMap) })
            .ToList();
        var unversionedPaths = targetPairs
            .Select(pair => pair.Target)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skippedPaths = targetPairs
            .Where(pair => string.IsNullOrWhiteSpace(pair.Target))
            .Select(pair => pair.Selected)
            .ToList();
        if (unversionedPaths.Count == 0)
        {
            MessageBox.Show("选中的路径都不是“未加入版本控制”状态，不能加入 svn:ignore。", "不能忽略", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"准备把 {unversionedPaths.Count} 个未加入版本控制的路径加入 svn:ignore。{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, unversionedPaths.Take(12)) +
            (unversionedPaths.Count > 12 ? Environment.NewLine + "..." : "");
        if (skippedPaths.Count > 0)
        {
            message += $"{Environment.NewLine}{Environment.NewLine}会跳过 {skippedPaths.Count} 个已受 SVN 管理或状态不明确的路径。";
        }

        if (MessageBox.Show(message, "加入忽略清单", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var result = await UpdateIgnorePropertiesAsync(workingCopy, unversionedPaths, add: true);
        OperationLogger.Log(result.ExitCode == 0 ? "AddIgnoreSuccess" : "AddIgnoreFailed", workingCopy, string.Join(" | ", unversionedPaths));
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task RemoveSelectedPathsFromIgnoreAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selectedPaths = GetSelectedIgnoreCandidatePaths();
        if (selectedPaths.Count == 0)
        {
            MessageBox.Show("请先在全部文件里选中要移出忽略清单的文件/文件夹。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var message =
            $"准备从 svn:ignore 里移除 {selectedPaths.Count} 个名称。{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, selectedPaths.Take(12)) +
            (selectedPaths.Count > 12 ? Environment.NewLine + "..." : "");
        if (MessageBox.Show(message, "移出忽略清单", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var result = await UpdateIgnorePropertiesAsync(workingCopy, selectedPaths, add: false);
        OperationLogger.Log(result.ExitCode == 0 ? "RemoveIgnoreSuccess" : "RemoveIgnoreFailed", workingCopy, string.Join(" | ", selectedPaths));
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task<ProcessResult> UpdateIgnorePropertiesAsync(string workingCopy, IReadOnlyList<string> relativePaths, bool add)
    {
        SetBusy(true, add ? "正在加入忽略清单..." : "正在移出忽略清单...");
        try
        {
            var output = new StringBuilder();
            var exitCode = 0;
            foreach (var group in BuildIgnoreGroups(relativePaths))
            {
                var current = await _svn.GetIgnoreAsync(workingCopy, group.ParentPath);
                if (current.ExitCode != 0 && !IsMissingIgnoreProperty(current))
                {
                    output.AppendLine(current.CombinedOutput);
                    exitCode = current.ExitCode;
                    continue;
                }

                var names = ParseIgnoreNames(current.StandardOutput);
                var changed = false;
                foreach (var name in group.Names)
                {
                    if (add)
                    {
                        changed |= names.Add(name);
                    }
                    else
                    {
                        changed |= names.Remove(name);
                    }
                }

                if (!changed)
                {
                    output.AppendLine($"{DisplayIgnoreParent(group.ParentPath)}：没有需要{(add ? "加入" : "移除")}的 ignore 项。");
                    continue;
                }

                var update = await _svn.SetIgnoreAsync(workingCopy, group.ParentPath, names);
                output.AppendLine(update.CombinedOutput);
                if (update.ExitCode != 0)
                {
                    exitCode = update.ExitCode;
                    continue;
                }

                output.AppendLine($"{DisplayIgnoreParent(group.ParentPath)}：已{(add ? "加入" : "移除")} {group.Names.Count} 项。");
            }

            return new ProcessResult(exitCode, output.ToString(), "");
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return new ProcessResult(-1, "", ex.Message);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private IReadOnlyList<string> GetSelectedIgnoreCandidatePaths()
    {
        var statusPaths = GetSelectedStatusChanges()
            .Select(change => SvnConflictArtifact.NormalizeToBasePath(change.RelativePath));
        var treePaths = GetSelectedFileTreeHistoryPaths();
        return statusPaths.Concat(treePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindUnversionedIgnoreTarget(string path, IReadOnlyDictionary<string, SvnStatusKind> statusMap)
    {
        var normalized = NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (statusMap.TryGetValue(normalized, out var status) && status == SvnStatusKind.Unversioned)
        {
            return normalized;
        }

        var current = normalized;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return null;
            }

            parent = NormalizeRelativePath(parent).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (statusMap.TryGetValue(parent, out status) && status == SvnStatusKind.Unversioned)
            {
                return parent;
            }

            current = parent;
        }

        return null;
    }

    private static IReadOnlyList<IgnoreGroup> BuildIgnoreGroups(IEnumerable<string> relativePaths)
    {
        return relativePaths
            .Select(path => NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(
                path => NormalizeIgnoreParent(Path.GetDirectoryName(path)),
                path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new IgnoreGroup(
                group.Key,
                group.Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(group => group.Names.Count > 0)
            .ToList();
    }

    private static HashSet<string> ParseIgnoreNames(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMissingIgnoreProperty(ProcessResult result)
    {
        var text = result.CombinedOutput;
        return text.Contains("W200017", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("不存在", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIgnoreParent(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? "."
            : NormalizeRelativePath(path).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string DisplayIgnoreParent(string parentPath)
    {
        return parentPath == "." ? "工作副本根目录" : parentPath;
    }

    private async Task RevertSelectedStatusChangesToLatestAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selected = GetSelectedStatusChanges()
            .DistinctBy(change => NormalizeRelativePath(change.RelativePath))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var unsupported = selected
            .Where(change => change.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added or SvnStatusKind.Conflicted)
            .ToList();
        var changes = selected.Except(unsupported).ToList();
        if (unsupported.Count > 0)
        {
            MessageBox.Show(
                "以下类型不会自动还原到最新：\r\n\r\n" +
                "- 未加入版本控制：需要手动删除或加入版本控制\r\n" +
                "- 新增文件：svn revert 后会变成未加入文件，容易误解\r\n" +
                "- 冲突文件：请走“冲突处理流程”\r\n\r\n" +
                "本次会跳过这些文件：\r\n" +
                string.Join(Environment.NewLine, unsupported.Take(8).Select(change => $"{change.DisplayStatus} {change.RelativePath}")) +
                (unsupported.Count > 8 ? Environment.NewLine + "..." : ""),
                "部分文件不能自动还原",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        if (changes.Count == 0)
        {
            return;
        }

        if (!ConfirmRevertToLatest(changes))
        {
            OperationLogger.Log("RevertToLatestCancelled", _configView.WorkingCopyPath.Trim(), $"files={changes.Count}");
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        SetBusy(true, "正在还原到 SVN 最新版本...");
        try
        {
            var output = new StringBuilder();
            foreach (var change in changes)
            {
                var revert = await _svn.RevertAsync(workingCopy, change.RelativePath);
                output.AppendLine(revert.CombinedOutput);
                if (revert.ExitCode != 0)
                {
                    continue;
                }

                var update = await _svn.UpdatePathAsync(workingCopy, change.RelativePath);
                output.AppendLine(update.CombinedOutput);
            }

            OperationLogger.Log("RevertToLatest", workingCopy, string.Join(" | ", changes.Select(change => change.RelativePath)));
            WriteOutput(output.ToString());
            await RefreshStatusAsync();
            LoadAllFiles();
            await LoadRepositoryHistoryAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private bool ConfirmRevertToLatest(IReadOnlyList<SvnChange> changes)
    {
        var message =
            $"准备把 {changes.Count} 个文件还原到 SVN 最新版本。{Environment.NewLine}{Environment.NewLine}" +
            "这会丢弃这些文件的本地改动，然后执行 svn update 拉取最新内容。此操作不能在工具内撤销。" +
            Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, changes.Take(10).Select(change => $"{change.DisplayStatus} {change.RelativePath}")) +
            (changes.Count > 10 ? Environment.NewLine + "..." : "") +
            Environment.NewLine + Environment.NewLine +
            "确认继续？";
        return MessageBox.Show(
            message,
            "还原到 SVN 最新版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private async Task LockSelectedFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        OperationLogger.Log("LockFileStart", workingCopy, relativePath);
        var result = await RunSvnOperationAsync("正在锁定文件...", async () => await _svn.LockAsync(workingCopy, relativePath));
        OperationLogger.Log(result?.ExitCode == 0 ? "LockFileSuccess" : "LockFileFailed", workingCopy, relativePath);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task UnlockSelectedFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        OperationLogger.Log("UnlockFileStart", workingCopy, relativePath);
        var result = await RunSvnOperationAsync("正在解锁文件...", async () => await _svn.UnlockAsync(workingCopy, relativePath));
        OperationLogger.Log(result?.ExitCode == 0 ? "UnlockFileSuccess" : "UnlockFileFailed", workingCopy, relativePath);
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ShowSelectedFileLockInfoAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (!ValidateWorkingCopyPath() || string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取锁信息...");
        try
        {
            var result = await _svn.InfoAsync(_configView.WorkingCopyPath.Trim(), relativePath);
            WriteOutput(result.CombinedOutput);
            MessageBox.Show(
                BuildLockInfoMessage(relativePath, result),
                "SVN 锁信息",
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }


    private async Task RefreshStatusAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        ClearHistoryDiffPreviewCache();
        SetBusy(true, "正在查看改动...");
        try
        {
            SaveSettings();
            var changes = (await _svn.GetStatusAsync(_configView.WorkingCopyPath.Trim())).ToList();
            var orderedChanges = changes.OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            _fileStatusView.SetChanges(orderedChanges);
            _state.SetStatusChanges(orderedChanges);
            var conflicts = changes.Where(change => change.Status == SvnStatusKind.Conflicted).ToList();
            RefreshConflictPanel(conflicts);
            UpdateStatusBadges(changes.Count, conflicts.Count);
            if (conflicts.Count > 0)
            {
                WriteOutput(
                    $"发现 {changes.Count} 个本地改动，其中 {conflicts.Count} 个冲突。\r\n\r\n" +
                    "SVN 冲突会在目录里生成 .mine、.r旧版本、.r新版本 文件，这是正常现象。" +
                    "请先处理冲突，再提交。冲突文件已经在列表里标红。");
            }
            else
            {
                WriteOutput(changes.Count == 0 ? "没有本地改动。" : $"发现 {changes.Count} 个本地改动。");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }


    private List<string> GetCommitSelectedPaths()
    {
        return _fileStatusView.GetCommitSelectedPaths();
    }


    private void UpdateStatusBadges(int changeCount, int conflictCount)
    {
        _statusPage.Text = changeCount > 0 ? $"File Status({changeCount})" : "File Status";
        _conflictPage.Text = conflictCount > 0 ? $"冲突({conflictCount})" : "冲突";
    }


    private static bool IsUnsafeCommitPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return SvnConflictArtifact.IsAuxiliaryPath(path) ||
            fileName.EndsWith("~", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".temp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".orig", StringComparison.OrdinalIgnoreCase);
    }

    private static string CommitBlockReason(SvnChange change)
    {
        if (IsUnsafeCommitPath(change.RelativePath))
        {
            return "SVN 冲突辅助文件、临时文件或备份文件，禁止提交";
        }

        return change.Status switch
        {
            SvnStatusKind.Conflicted => "文件仍有冲突，请先打开合并并标记解决",
            SvnStatusKind.Missing => "文件在本地缺失，暂不自动提交删除",
            _ => "",
        };
    }

    private async Task<CommitPreflightResult> BuildCommitPreflightAsync(string workingCopy, IReadOnlyList<SvnChange> selectedChanges, IReadOnlyList<SvnChange> conflicts)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (conflicts.Count > 0)
        {
            blockers.Add($"工作副本仍有 {conflicts.Count} 个冲突，必须先处理冲突。");
        }

        var unsafePaths = selectedChanges.Where(change => IsUnsafeCommitPath(change.RelativePath)).Select(change => change.RelativePath).ToList();
        if (unsafePaths.Count > 0)
        {
            blockers.Add($"选择了 {unsafePaths.Count} 个冲突辅助/临时/备份文件，禁止提交：" + FormatPathPreview(unsafePaths));
        }

        var missing = selectedChanges.Where(change => change.Status == SvnStatusKind.Missing).Select(change => change.RelativePath).ToList();
        if (missing.Count > 0)
        {
            blockers.Add($"选择了 {missing.Count} 个本地缺失文件，当前不自动提交删除：" + FormatPathPreview(missing));
        }

        var unversioned = selectedChanges.Where(change => change.Status == SvnStatusKind.Unversioned).Select(change => change.RelativePath).ToList();
        if (unversioned.Count > 0)
        {
            warnings.Add($"将自动 svn add {unversioned.Count} 个未加入版本控制的文件：" + FormatPathPreview(unversioned));
        }

        var info = WorkingCopyInfo.Empty;
        try
        {
            info = _svn.GetWorkingCopyInfo(workingCopy);
            if (info.MinRevision > 0 && info.MaxRevision > 0 && info.MinRevision != info.MaxRevision)
            {
                warnings.Add($"工作副本是混合版本：r{info.MinRevision}:r{info.MaxRevision}。提交前请确认这正是你想要的状态。");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("无法读取工作副本版本信息：" + ex.Message);
        }

        try
        {
            var latest = await _svn.GetLatestRepositoryLogAsync(workingCopy);
            if (latest is { Revision: > 0 } && info.MaxRevision > 0 && latest.Revision > info.MaxRevision)
            {
                warnings.Add($"远端最新是 r{latest.Revision}，当前本地最高内容版本是 r{info.MaxRevision}，本地落后 {latest.Revision - info.MaxRevision} 个版本。");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("无法检查远端最新版本：" + ex.Message);
        }

        return new CommitPreflightResult(blockers, warnings);
    }

    private bool ConfirmCommitPreflight(CommitPreflightResult result)
    {
        if (result.Blocked)
        {
            MessageBox.Show(
                result.FormatMessage("提交前检查未通过"),
                "提交被拦截",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (!result.HasWarnings)
        {
            return true;
        }

        return MessageBox.Show(
            result.FormatMessage("提交前检查发现风险"),
            "提交前检查",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private static string FormatPathPreview(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "";
        }

        var preview = string.Join("、", paths.Take(5));
        return paths.Count > 5 ? preview + $" 等 {paths.Count} 个" : preview;
    }


    private async Task RunCommitAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var selectedPaths = GetCommitSelectedPaths();

        if (selectedPaths.Count == 0)
        {
            var noSelectionMessage = _fileStatusView.CommitVisibleOnly && _fileStatusView.CheckedCount > 0
                ? $"当前筛选结果里没有已勾选文件。{Environment.NewLine}{Environment.NewLine}当前共有 {_fileStatusView.CheckedCount} 个隐藏或不在筛选结果里的已勾选文件。取消“只提交当前筛选结果”，或在当前筛选结果中勾选文件后再提交。"
                : "请先勾选要提交的文件。";
            MessageBox.Show(noSelectionMessage, "没有选择文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var latestChanges = (await _svn.GetStatusAsync(workingCopy)).ToList();
        var conflicts = latestChanges.Where(change => change.Status == SvnStatusKind.Conflicted).ToList();
        string? globalBlockReason = null;
        if (conflicts.Count > 0)
        {
            RefreshConflictPanel(conflicts);
            globalBlockReason = $"当前工作副本仍有 {conflicts.Count} 个冲突。请先在“冲突”页处理完，再提交。";
        }

        var latestMap = latestChanges.ToDictionary(change => NormalizeRelativePath(change.RelativePath), StringComparer.OrdinalIgnoreCase);
        var selectedChanges = selectedPaths
            .Select(path => latestMap.TryGetValue(NormalizeRelativePath(path), out var change) ? change : null)
            .Where(change => change != null)
            .Cast<SvnChange>()
            .ToList();

        if (selectedChanges.Count == 0)
        {
            MessageBox.Show("勾选的文件当前没有可提交改动，请先刷新状态。", "没有可提交改动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshStatusAsync();
            return;
        }

        var preflight = await BuildCommitPreflightAsync(workingCopy, selectedChanges, conflicts);
        if (!ConfirmCommitPreflight(preflight))
        {
            OperationLogger.Log("CommitPreflightCancelled", workingCopy, preflight.ToLogText());
            return;
        }

        var message = "";
        using (var preview = new CommitPreviewForm(_settings.LastCommitMessage, selectedChanges, CommitBlockReason, preflight.Blocked ? preflight.BlockMessage : globalBlockReason))
        {
            if (preview.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            message = preview.CommitMessage;
            selectedChanges = preview.SelectedChanges.ToList();
        }

        if (selectedChanges.Count == 0)
        {
            MessageBox.Show("请至少保留一个要提交的文件。", "没有提交文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (message.Length < 4)
        {
            var continueShortMessage = MessageBox.Show(
                "提交说明比较短，可能不方便之后查历史。\r\n\r\n仍然继续提交？",
                "提交说明偏短",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (continueShortMessage != DialogResult.OK)
            {
                return;
            }
        }

        var result = await RunSvnOperationAsync("正在提交...", async () =>
        {
            var unversioned = selectedChanges.Where(change => change.Status == SvnStatusKind.Unversioned).ToList();
            var output = "";
            foreach (var change in unversioned)
            {
                var addResult = await _svn.AddAsync(workingCopy, change.RelativePath);
                output += addResult.CombinedOutput + Environment.NewLine;
            }

            var commitResult = await _svn.CommitAsync(workingCopy, selectedChanges.Select(change => change.RelativePath), message);
            _settings.LastCommitMessage = message;
            SaveSettings();
            return new ProcessResult(commitResult.ExitCode, output + commitResult.StandardOutput, commitResult.StandardError);
        });
        OperationLogger.Log(
            result?.ExitCode == 0 ? "CommitSuccess" : "CommitFailed",
            workingCopy,
            $"files={selectedChanges.Count}; message={message}");
        await RefreshStatusAsync();
        if (result?.ExitCode == 0)
        {
            await LoadRepositoryHistoryAsync();
            SelectTab("History");
        }
    }

    private async Task<ProcessResult?> RunSvnOperationAsync(string busyText, Func<Task<ProcessResult>> operation)
    {
        SetBusy(true, busyText);
        try
        {
            var result = await operation();
            WriteOutput(result.CombinedOutput);
            if (result.ExitCode != 0)
            {
                MessageBox.Show("SVN 命令执行失败，请查看下方输出。", "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return result;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return null;
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

}


