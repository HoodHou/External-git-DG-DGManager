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
    private void WireHistoryViewEvents()
    {
        _historyView.RefreshRequested += async (_, _) => await LoadRepositoryHistoryAsync();
        _historyView.DeepSearchRequested += async (_, _) => await RunDeepHistorySearchAsync();
        _historyView.LoadMoreRequested += async (_, _) => await LoadMoreRepositoryHistoryAsync();
        _historyView.SearchChanged += (_, _) => ApplyHistoryFilter();
        _historyView.HistorySelectionChanged += (_, _) => ShowSelectedHistoryDetail();
        _historyView.HistoryDoubleClick += (_, _) => FocusFirstChangedFileInSelectedHistory();
        _historyView.FocusChangedFileRequested += (_, _) => FocusFirstChangedFileInSelectedHistory();
        _historyView.UpdateToRevisionRequested += async (_, _) => await RunUpdateWorkingCopyToSelectedHistoryRevisionAsync();
        _historyView.CopyRevisionRequested += (_, _) => CopySelectedHistoryRevision();
        _historyView.CopySummaryRequested += (_, _) => CopySelectedHistorySummary();
        _historyView.ChangedFileSelected += async (_, args) => await ShowSelectedHistoryFileDiffAsync(args.Node);
        _historyView.OpenChangedFileRequested += async (_, args) => await OpenHistoryChangedFileAsync(args.Node);
        _historyView.OpenChangedFileFolderRequested += (_, _) => OpenSelectedHistoryChangedFileFolder();
        _historyView.ExternalCompareRequested += async (_, _) => await RunSelectedHistoryChangedFileExternalCompareAsync();
        _historyView.CompareAnotherTableRequested += async (_, _) => await CompareSelectedHistoryFileWithAnotherTableAsync();
        _historyView.FileHistoryRequested += async (_, _) => await RunSelectedHistoryChangedFileHistoryAsync();
        _historyView.CompareRemoteHeadRequested += async (_, _) => await CompareSelectedHistoryFileWithRemoteHeadAsync();
        _historyView.CommitSpreadsheetMergeRequested += async (_, _) => await RunSelectedCommitSpreadsheetMergeAsync();
        _historyView.UpdateFileToRevisionRequested += async (_, _) => await UpdateSelectedHistoryFileToRevisionAsync();
        _historyView.ReverseMergeRequested += async (_, _) => await ReverseMergeSelectedHistoryFileAsync();
        _historyView.CopyChangedFilePathRequested += (_, _) => CopySelectedHistoryChangedFilePath();
    }

    private void UpdateHistoryBadge(int logCount)
    {
        _historyPage.Text = logCount > 0 ? $"History({logCount})" : "History";
    }


    private async Task RunFileHistoryAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePaths = GetSelectedFileTreeHistoryPaths();
        if (relativePaths.Count == 0)
        {
            MessageBox.Show("请先选中或勾选一个文件/文件夹，再查看历史。", "未选择路径", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取文件历史...");
        try
        {
            await ShowFileHistoryWindowAsync(relativePaths);
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

    private async Task ShowFileHistoryWindowAsync(string relativePath)
    {
        await ShowFileHistoryWindowAsync([relativePath]);
    }

    private async Task ShowFileHistoryWindowAsync(IReadOnlyList<string> relativePaths)
    {
        var workingCopy = _configView.WorkingCopyPath.Trim();
        var logs = await _svn.GetLogAsync(workingCopy, relativePaths, 80);
        using var form = new FileHistoryForm(workingCopy, relativePaths, logs, _svn);
        form.ShowDialog(this);
        var label = FormatPathListLabel(relativePaths);
        WriteOutput(logs.Count == 0
            ? $"没有读取到历史：{label}"
            : $"已打开历史窗口：{label}（{logs.Count} 条）");
    }

    private async Task LoadRepositoryHistoryAsync(int? limit = null)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var requestedLimit = Math.Max(InitialHistoryLimit, limit ?? _historyView.LoadedLimit);
        _historyView.LoadedLimit = requestedLimit;
        ClearHistoryDiffPreviewCache();
        SetBusy(true, $"正在读取仓库历史（最近 {requestedLimit} 条）...");
        try
        {
            var logs = await _svn.GetRepositoryLogAsync(_configView.WorkingCopyPath.Trim(), requestedLimit);
            _latestRemoteLog = logs.FirstOrDefault(log => !log.IsUncommitted);
            FillHistoryList(logs);
            UpdateHistoryBadge(logs.Count);
            WriteOutput(logs.Count == 0 ? "没有读取到仓库历史。" : $"已读取最近 {logs.Count} 条仓库历史。");
            await CheckRemoteChangesAsync(showUpToDateMessage: false);
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

    private async Task LoadMoreRepositoryHistoryAsync()
    {
        var nextLimit = _historyView.LoadedLimit + HistoryLoadMoreStep;
        await LoadRepositoryHistoryAsync(nextLimit);
    }

    private async Task RunDeepHistorySearchAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var filter = HistorySearchFilter.Parse(_historyView.SearchTextBox.Text);
        if (filter.HasRevisionRange)
        {
            await LoadRepositoryHistoryRevisionRangeAsync(filter);
            return;
        }

        var targetLimit = Math.Max(_historyView.LoadedLimit, HistoryDeepSearchLimit);
        await LoadRepositoryHistoryAsync(targetLimit);
        ApplyHistoryFilter();
        WriteOutput(string.IsNullOrWhiteSpace(_historyView.SearchTextBox.Text)
            ? $"深度搜索已读取最近 {_historyView.HistoryRows.Count(log => !log.IsUncommitted)} 条历史。"
            : $"深度搜索已读取最近 {_historyView.HistoryRows.Count(log => !log.IsUncommitted)} 条历史，当前匹配 {_historyView.HistoryList.Items.Count} 条。");
    }

    private async Task LoadRepositoryHistoryRevisionRangeAsync(HistorySearchFilter filter)
    {
        if (filter.RevisionStart == null && filter.RevisionEnd == null)
        {
            return;
        }

        var start = filter.RevisionStart ?? filter.RevisionEnd!.Value;
        var end = filter.RevisionEnd ?? filter.RevisionStart!.Value;
        var rangeLimit = (int)Math.Min(HistoryRevisionRangeLimit, Math.Max(1, Math.Abs(end - start) + 1));

        ClearHistoryDiffPreviewCache();
        SetBusy(true, $"正在读取版本范围 r{start}-r{end}...");
        try
        {
            var rangeLogs = await _svn.GetRepositoryLogRangeAsync(_configView.WorkingCopyPath.Trim(), start, end, rangeLimit);
            var mergedLogs = _historyView.HistoryRows
                .Where(log => !log.IsUncommitted)
                .Concat(rangeLogs)
                .GroupBy(log => log.Revision)
                .Select(group => group.OrderByDescending(log => log.ChangedFiles.Count).First())
                .OrderByDescending(log => log.Revision)
                .ToList();
            FillHistoryList(mergedLogs);
            UpdateHistoryBadge(mergedLogs.Count);
            ApplyHistoryFilter();
            WriteOutput(
                $"深度搜索已读取版本范围 r{Math.Min(start, end)}-r{Math.Max(start, end)}，新增/合并 {rangeLogs.Count} 条历史，当前匹配 {_historyView.HistoryList.Items.Count} 条。" +
                (Math.Abs(end - start) + 1 > HistoryRevisionRangeLimit ? $" 当前最多读取 {HistoryRevisionRangeLimit} 条，请缩小版本范围。" : ""));
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


    private void FillHistoryList(IReadOnlyList<SvnLogEntry> logs)
    {
        var workingCopy = _configView.WorkingCopyPath.Trim();
        var hasWorkingCopy = Directory.Exists(workingCopy) && Directory.Exists(Path.Combine(workingCopy, ".svn"));
        var info = hasWorkingCopy ? RefreshWorkingCopyRevisionStatus() : WorkingCopyInfo.Empty;
        var changes = hasWorkingCopy ? _svn.GetStatus(workingCopy) : [];
        var workingCopyRevision = info.CurrentContentRevision;
        var latestRemoteRevision = logs
            .Where(log => !log.IsUncommitted && log.Revision > 0)
            .Select(log => log.Revision)
            .DefaultIfEmpty(0)
            .Max();
        _historyView.HistoryRows = [];
        var summary = info == WorkingCopyInfo.Empty
            ? ""
            : $"当前内容版本：r{info.LastContentRevision}{Environment.NewLine}工作副本已更新到：{info.DisplayRevisionText}{Environment.NewLine}{info.Url}";
        if (latestRemoteRevision > 0 && info != WorkingCopyInfo.Empty)
        {
            summary += $"{Environment.NewLine}远端最新历史版本：r{latestRemoteRevision}";
            if (workingCopyRevision > 0 && latestRemoteRevision > workingCopyRevision)
            {
                summary += $"（本地未更新，落后 {latestRemoteRevision - workingCopyRevision} 个版本）";
            }
        }

        ShowHistorySummary(summary);
        if (changes.Count > 0)
        {
            var uncommitted = new SvnLogEntry(0, "*", DateTimeOffset.Now, $"Uncommitted changes ({changes.Count} files)")
            {
                IsUncommitted = true,
                ChangedFiles = changes.Select(change => ChangedFileEntry.FromWorkingCopy(change.Status, change.RelativePath)).ToList(),
            };
            _historyView.HistoryRows.Add(uncommitted);
        }

        var hasWorkingCopyRevisionInLoadedLogs = workingCopyRevision > 0 && logs.Any(log => log.Revision == workingCopyRevision);
        if (info != WorkingCopyInfo.Empty && workingCopyRevision > 0 && !hasWorkingCopyRevisionInLoadedLogs)
        {
            _historyView.HistoryRows.Add(new SvnLogEntry(
                workingCopyRevision,
                "LOCAL",
                DateTimeOffset.MinValue,
                $"当前工作副本位于 r{workingCopyRevision}。这个版本不在当前已加载的最近 {logs.Count} 条历史中；可以点击“加载更多”“深度搜索”，或搜索 rev:{workingCopyRevision} 查看完整提交详情。")
            {
                IsWorkingCopyRevision = true,
            });
        }

        foreach (var log in logs.OrderByDescending(log => log.Revision))
        {
            _historyView.HistoryRows.Add(log with { IsWorkingCopyRevision = log.Revision == workingCopyRevision });
        }
        _state.SetHistoryRows(_historyView.HistoryRows);
        ApplyHistoryFilter(selectWorkingCopyRevision: true);
    }

    private void ApplyHistoryFilter(bool selectWorkingCopyRevision = false)
    {
        var filter = HistorySearchFilter.Parse(_historyView.SearchTextBox.Text);
        var rows = filter.IsEmpty
            ? _historyView.HistoryRows
            : _historyView.HistoryRows.Where(log => filter.Matches(log)).ToList();
        _historyView.HistoryList.BeginUpdate();
        _historyView.HistoryList.Items.Clear();
        ClearHistoryChangedFiles();
        foreach (var row in rows)
        {
            AddHistoryItem(row);
        }
        _historyView.HistoryList.EndUpdate();
        UpdateHistorySearchControls();

        if (_historyView.HistoryList.Items.Count == 0)
        {
            _historyView.SelectedLog = null;
            _historyView.SelectedLogs = [];
            _state.SetSelectedHistory([]);
            var loadedCount = _historyView.HistoryRows.Count(log => !log.IsUncommitted);
            ShowHistorySummary(filter.IsEmpty
                ? ""
                : $"没有匹配的提交。当前只在已加载的 {loadedCount} 条历史里搜索；可以点击“深度搜索”读取更早提交。");
            return;
        }

        var itemToSelect = selectWorkingCopyRevision || filter.IsEmpty
            ? _historyView.HistoryList.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Tag is SvnLogEntry { IsWorkingCopyRevision: true })
            : null;
        itemToSelect ??= _historyView.HistoryList.Items[0];
        itemToSelect.Selected = true;
        itemToSelect.Focused = true;
        itemToSelect.EnsureVisible();
    }

    private void UpdateHistorySearchControls()
    {
        var filter = HistorySearchFilter.Parse(_historyView.SearchTextBox.Text);
        var loadedCount = _historyView.HistoryRows.Count(log => !log.IsUncommitted);
        var matchedCount = _historyView.HistoryList.Items.Count;
        _historyView.SearchScopeLabel.Text = filter.IsEmpty
            ? $"已加载 {loadedCount} 条"
            : $"匹配 {matchedCount}/{loadedCount} 条";

        var canUseHistory = !UseWaitCursor && ValidateWorkingCopyPathForBackground();
        _historyView.LoadMoreButton.Enabled = canUseHistory;
        _historyView.DeepSearchButton.Enabled = canUseHistory;
        _historyView.ClearSearchButton.Enabled = !string.IsNullOrWhiteSpace(_historyView.SearchTextBox.Text);
    }

    private void AddHistoryItem(SvnLogEntry log)
    {
        var item = new ListViewItem(log.GraphText) { Tag = log, ImageIndex = 0 };
        item.SubItems.Add(log.DescriptionText);
        item.SubItems.Add(log.LocalDateText);
        item.SubItems.Add(log.Author);
        item.SubItems.Add(log.RevisionText);
        if (log.IsUncommitted)
        {
            item.Font = new Font(_historyView.HistoryList.Font, FontStyle.Bold);
            item.BackColor = Color.FromArgb(255, 250, 230);
        }
        else if (log.IsWorkingCopyRevision)
        {
            item.BackColor = Color.FromArgb(221, 235, 247);
            item.Font = new Font(_historyView.HistoryList.Font, FontStyle.Bold);
        }

        _historyView.HistoryList.Items.Add(item);
    }

    private void FocusFirstChangedFileInSelectedHistory()
    {
        if (_historyView.HistoryList.SelectedItems.Count != 1 || _historyView.HistoryList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        _historyView.SelectedLog = log;
        _historyView.SelectedLogs = [log];
        _state.SetSelectedHistory(_historyView.SelectedLogs);
        PopulateHistoryChangedFiles(log);
        var filter = HistorySearchFilter.Parse(_historyView.SearchTextBox.Text);
        var firstFileNode = _historyView.FindBestChangedFileNode(filter);
        if (firstFileNode == null)
        {
            return;
        }

        firstFileNode.EnsureVisible();
        _historyView.ChangedFilesTree.SelectedNode = firstFileNode;
        _historyView.ChangedFilesTree.Focus();
    }

    private SvnLogEntry? GetSingleSelectedHistoryLog()
    {
        return _historyView.GetSingleSelectedLog();
    }

    private void CopySelectedHistoryRevision()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null || log.IsUncommitted)
        {
            return;
        }

        Clipboard.SetText(log.Revision.ToString());
        WriteOutput($"已复制版本号：r{log.Revision}");
    }

    private void CopySelectedHistorySummary()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null)
        {
            return;
        }

        var text = log.IsUncommitted
            ? $"本地未提交改动：{log.ChangedFiles.Count} 个文件"
            : $"r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}{log.Message}{Environment.NewLine}{Environment.NewLine}Changed files ({log.ChangedFiles.Count}){Environment.NewLine}" +
              string.Join(Environment.NewLine, log.ChangedFiles.Select(file => $"{file.Action} {file.DisplayText}"));
        Clipboard.SetText(text);
        WriteOutput(log.IsUncommitted ? "已复制本地改动摘要。" : $"已复制提交摘要：r{log.Revision}");
    }

    private async Task RunUpdateWorkingCopyToSelectedHistoryRevisionAsync()
    {
        var log = GetSingleSelectedHistoryLog();
        if (log == null || log.IsUncommitted || log.Revision <= 0 || !ValidateWorkingCopyPath())
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var changes = await _svn.GetStatusAsync(workingCopy);
        if (!ConfirmUpdateToRevision(log, changes))
        {
            OperationLogger.Log("UpdateToRevisionCancelled", workingCopy, $"revision={log.Revision}; localChanges={changes.Count}");
            return;
        }

        OperationLogger.Log("UpdateToRevisionStart", workingCopy, $"revision={log.Revision}; localChanges={changes.Count}");
        var result = await RunSvnOperationAsync($"正在回退到 r{log.Revision}...", async () => await _svn.UpdateToRevisionAsync(workingCopy, log.Revision));
        OperationLogger.Log(result?.ExitCode == 0 ? "UpdateToRevisionSuccess" : "UpdateToRevisionFailed", workingCopy, $"revision={log.Revision}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
        await CheckRemoteChangesAsync(showUpToDateMessage: false);
    }

    private bool ConfirmUpdateToRevision(SvnLogEntry log, IReadOnlyList<SvnChange> changes)
    {
        var localChangeText = changes.Count == 0
            ? "当前没有本地改动。"
            : $"当前有 {changes.Count} 个本地改动；继续回退可能产生冲突，建议先提交、备份或清理。";
        var message =
            $"准备把整个工作副本更新到历史版本 r{log.Revision}。{Environment.NewLine}{Environment.NewLine}" +
            $"{log.LocalDateText}  {log.Author}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"{localChangeText}{Environment.NewLine}{Environment.NewLine}" +
            "这个操作不会修改 SVN 服务器历史，也不会自动提交；只是把你的本地工作副本切到该历史版本。确认继续？";
        var result = MessageBox.Show(
            message,
            "回退工作副本到历史版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.OK;
    }


    private async Task OpenHistoryChangedFileAsync(TreeNode? node)
    {
        if (node?.Tag is not ChangedFileEntry file)
        {
            node?.Toggle();
            return;
        }

        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        SetBusy(true, "正在打开历史版本文件...");
        try
        {
            if (await OpenHistoryChangedFileVersionAsync(file))
            {
                return;
            }

            MessageBox.Show("没有找到可打开的文件版本。可能是历史版本中的已删除文件，或当前工作副本没有该路径。", "无法打开文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private async Task<bool> OpenHistoryChangedFileVersionAsync(ChangedFileEntry file)
    {
        if (_historyView.SelectedLogs.Count > 1)
        {
            var committedLogs = _historyView.SelectedLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
            if (committedLogs.Count > 0)
            {
                var lastRevision = committedLogs.Last().Revision;
                if (await TryOpenRepositoryFileVersionAsync(file, lastRevision, $"r{lastRevision}"))
                {
                    return true;
                }

                var beforeFirstRevision = committedLogs.First().Revision - 1;
                if (beforeFirstRevision > 0 && await TryOpenRepositoryFileVersionAsync(file, beforeFirstRevision, $"r{beforeFirstRevision}"))
                {
                    WriteOutput($"范围结束版本没有该文件，已打开范围开始前版本：{file.DisplayText}");
                    return true;
                }

                return false;
            }
        }

        if (_historyView.SelectedLog is { IsUncommitted: false } selectedLog)
        {
            var revision = file.Action == "D" ? selectedLog.Revision - 1 : selectedLog.Revision;
            if (revision <= 0)
            {
                return false;
            }

            return await TryOpenRepositoryFileVersionAsync(file, revision, $"r{revision}");
        }

        var filePath = GetHistoryChangedLocalPath(file);
        if (File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            WriteOutput($"已打开本地文件：{file.RelativePath}");
            return true;
        }

        return false;
    }

    private async Task<bool> TryOpenRepositoryFileVersionAsync(ChangedFileEntry file, long revision, string label)
    {
        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            return false;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var tempPath = CreateHistoryOpenTempPath(label, file.TreePath);
        try
        {
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, tempPath);
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            WriteOutput($"已打开历史版本 {label}：{file.DisplayText}");
            return true;
        }
        catch
        {
            TryDelete(tempPath);
            return false;
        }
    }

    private void OpenSelectedHistoryChangedFileFolder()
    {
        if (_historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var filePath = GetHistoryChangedLocalPath(file);
        var folder = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
    }

    private async Task RunSelectedHistoryChangedFileHistoryAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        SetBusy(true, "正在读取文件历史...");
        try
        {
            await ShowFileHistoryWindowAsync(file.RelativePath);
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

    private async Task UpdateSelectedHistoryFileToRevisionAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            _historyView.SelectedLog is not { IsUncommitted: false, Revision: > 0 } log)
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var relativePath = GetHistoryChangedWorkingCopyRelativePath(file);
        var localChanges = await _svn.GetStatusAsync(workingCopy);
        if (!ConfirmUpdateHistoryFileToRevision(file, relativePath, log, localChanges))
        {
            OperationLogger.Log("UpdateFileToRevisionCancelled", workingCopy, $"revision={log.Revision}; file={relativePath}");
            return;
        }

        OperationLogger.Log("UpdateFileToRevisionStart", workingCopy, $"revision={log.Revision}; file={relativePath}");
        var result = await RunSvnOperationAsync($"正在把文件更新到 r{log.Revision}...", async () => await _svn.UpdatePathToRevisionAsync(workingCopy, relativePath, log.Revision));
        OperationLogger.Log(result?.ExitCode == 0 ? "UpdateFileToRevisionSuccess" : "UpdateFileToRevisionFailed", workingCopy, $"revision={log.Revision}; file={relativePath}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private async Task ReverseMergeSelectedHistoryFileAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            _historyView.SelectedLog is not { IsUncommitted: false, Revision: > 0 } log)
        {
            return;
        }

        var workingCopy = _configView.WorkingCopyPath.Trim();
        var relativePath = GetHistoryChangedWorkingCopyRelativePath(file);
        SetBusy(true, "正在预览撤销改动...");
        ProcessResult preview;
        try
        {
            preview = await _svn.ReverseMergeRevisionForPathAsync(workingCopy, relativePath, log.Revision, dryRun: true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }
        finally
        {
            SetBusy(false, "就绪");
        }

        if (!ConfirmReverseMergeHistoryFile(file, relativePath, log, preview))
        {
            OperationLogger.Log("ReverseMergeFileCancelled", workingCopy, $"revision={log.Revision}; file={relativePath}");
            return;
        }

        OperationLogger.Log("ReverseMergeFileStart", workingCopy, $"revision={log.Revision}; file={relativePath}");
        var result = await RunSvnOperationAsync($"正在撤销 r{log.Revision} 对文件的改动...", async () => await _svn.ReverseMergeRevisionForPathAsync(workingCopy, relativePath, log.Revision, dryRun: false));
        OperationLogger.Log(result?.ExitCode == 0 ? "ReverseMergeFileSuccess" : "ReverseMergeFileFailed", workingCopy, $"revision={log.Revision}; file={relativePath}");
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private bool ConfirmUpdateHistoryFileToRevision(ChangedFileEntry file, string relativePath, SvnLogEntry log, IReadOnlyList<SvnChange> localChanges)
    {
        var localStatus = localChanges.FirstOrDefault(change =>
            string.Equals(NormalizeRelativePath(change.RelativePath), NormalizeRelativePath(relativePath), StringComparison.OrdinalIgnoreCase));
        var localWarning = localStatus == null
            ? "这个文件当前没有本地改动。"
            : $"这个文件当前有本地状态：{localStatus.DisplayStatus}。继续可能覆盖本地修改或产生冲突。";
        var message =
            $"准备只把这个文件更新到 r{log.Revision} 的版本。{Environment.NewLine}{Environment.NewLine}" +
            $"文件：{relativePath}{Environment.NewLine}" +
            $"提交：r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"影响范围：只影响这个文件，不会提交到服务器。{Environment.NewLine}" +
            $"{localWarning}{Environment.NewLine}{Environment.NewLine}" +
            $"SVN 路径：{file.DisplayText}{Environment.NewLine}{Environment.NewLine}" +
            "确认继续？";
        return MessageBox.Show(
            message,
            "文件回退到历史版本",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private bool ConfirmReverseMergeHistoryFile(ChangedFileEntry file, string relativePath, SvnLogEntry log, ProcessResult preview)
    {
        if (preview.ExitCode != 0)
        {
            MessageBox.Show(
                $"SVN dry-run 预览失败，已取消撤销操作。{Environment.NewLine}{Environment.NewLine}{preview.CombinedOutput}",
                "无法撤销本次提交",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var previewText = string.IsNullOrWhiteSpace(preview.CombinedOutput)
            ? "SVN dry-run 没有输出；通常表示没有可撤销改动，或该文件路径不适合直接撤销。"
            : preview.CombinedOutput.Trim();
        var message =
            $"准备撤销 r{log.Revision} 对这个文件造成的改动。{Environment.NewLine}{Environment.NewLine}" +
            $"文件：{relativePath}{Environment.NewLine}" +
            $"提交：r{log.Revision}  {log.Author}  {log.LocalDateText}{Environment.NewLine}" +
            $"{log.ShortMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"影响范围：只对这个文件执行 reverse merge，不会自动提交。{Environment.NewLine}" +
            $"SVN dry-run 预览：{Environment.NewLine}{previewText}{Environment.NewLine}{Environment.NewLine}" +
            "确认继续？";
        return MessageBox.Show(
            message,
            "撤销单次提交对文件的改动",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private void CopySelectedHistoryChangedFilePath()
    {
        if (_historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        Clipboard.SetText(string.IsNullOrWhiteSpace(file.RepositoryPath) ? file.RelativePath : file.RepositoryPath);
        WriteOutput($"已复制路径：{(string.IsNullOrWhiteSpace(file.RepositoryPath) ? file.RelativePath : file.RepositoryPath)}");
    }

    private string GetHistoryChangedLocalPath(ChangedFileEntry file)
    {
        return Path.Combine(_configView.WorkingCopyPath.Trim(), GetHistoryChangedWorkingCopyRelativePath(file));
    }

    private string GetHistoryChangedWorkingCopyRelativePath(ChangedFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            return file.RelativePath;
        }

        var repositoryPath = file.RepositoryPath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        var workingCopyUrl = _svn.GetWorkingCopyInfo(_configView.WorkingCopyPath.Trim()).Url;
        var workingCopyRepositoryPath = ExtractWorkingCopyRepositoryPath(workingCopyUrl);
        if (!string.IsNullOrWhiteSpace(workingCopyRepositoryPath) &&
            repositoryPath.StartsWith(workingCopyRepositoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return repositoryPath[(workingCopyRepositoryPath.Length + 1)..];
        }

        var candidates = new[]
        {
            file.RelativePath,
            repositoryPath,
            StripFirstPathSegment(repositoryPath),
            StripFirstPathSegment(StripFirstPathSegment(repositoryPath)),
        };
        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (File.Exists(Path.Combine(_configView.WorkingCopyPath.Trim(), candidate)) ||
             Directory.Exists(Path.Combine(_configView.WorkingCopyPath.Trim(), candidate)))) ?? file.RelativePath;
    }

    private static string ExtractWorkingCopyRepositoryPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
        for (var start = 0; start < segments.Count; start++)
        {
            var suffix = string.Join(Path.DirectorySeparatorChar, segments.Skip(start));
            if (suffix.StartsWith("trunk", StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("branch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("branches" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith("tags" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return suffix;
            }
        }

        return "";
    }

    private static string StripFirstPathSegment(string path)
    {
        var trimmed = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var index = trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return index < 0 ? "" : trimmed[(index + 1)..];
    }

    private async Task AddSelectedTreeFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        await RunSvnOperationAsync("正在加入版本控制...", async () => await _svn.AddAsync(_configView.WorkingCopyPath.Trim(), relativePath));
        await RefreshStatusAsync();
        LoadAllFiles();
    }

    private async Task ResolveSelectedTreeFileAsync()
    {
        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        await ResolveConflictPathAsync(relativePath);
    }


    private void ShowSelectedHistoryDetail()
    {
        var selectedLogs = _historyView.GetSelectedLogsFromList();

        if (selectedLogs.Count == 0)
        {
            _historyView.SelectedLog = null;
            _historyView.SelectedLogs = [];
            _state.SetSelectedHistory([]);
            ClearHistoryChangedFiles();
            ShowHistorySummary("");
            return;
        }

        if (selectedLogs.Count > 1)
        {
            _historyView.SelectedLog = null;
            _historyView.SelectedLogs = selectedLogs;
            _state.SetSelectedHistory(_historyView.SelectedLogs);
            ShowSelectedHistoryRangeDetail(selectedLogs);
            return;
        }

        var log = selectedLogs[0];
        _historyView.SelectedLog = log;
        _historyView.SelectedLogs = [log];
        _state.SetSelectedHistory(_historyView.SelectedLogs);
        PopulateHistoryChangedFiles(log);
        if (log.IsUncommitted)
        {
            ShowHistorySummary(HistorySummaryData.FromLog(log));
            return;
        }

        ShowHistorySummary(HistorySummaryData.FromLog(log));
    }

    private void ShowSelectedHistoryRangeDetail(IReadOnlyList<SvnLogEntry> logs)
    {
        var committedLogs = logs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
        if (committedLogs.Count == 0)
        {
            ShowHistorySummary("当前选择只包含未提交改动，请单选 Uncommitted changes 查看。");
            ClearHistoryChangedFiles();
            return;
        }

        var changedFiles = BuildRangeChangedFiles(committedLogs);
        PopulateHistoryChangedFiles($"Selected commits ({committedLogs.Count}) - Changed files ({changedFiles.Count})", changedFiles);
        var first = committedLogs.First();
        var last = committedLogs.Last();
        ShowHistorySummary(HistorySummaryData.FromRange(committedLogs, changedFiles));
    }

    private void ShowHistorySummary(string text)
    {
        ShowHistorySummary(HistorySummaryData.Plain(text));
    }

    private void ShowHistorySummary(HistorySummaryData data)
    {
        _historyView.IsDiffMaximized = false;
        _historyView.DiffHeaderLabel.Text = "提交详情";
        _historyView.DiffMaximizeButton.Visible = false;

        CancelHistoryDiffPreview();
        ClearHistoryDiffPanel();
        var summaryControl = new HistorySummaryPanel(data);
        summaryControl.FileClicked += async summaryFile =>
        {
            var entry = _historyView.ChangedFilesAll.FirstOrDefault(f => f.TreePath == summaryFile.TreePath);
            if (entry != null)
            {
                var node = FindNodeByTag(_historyView.ChangedFilesTree.Nodes, entry);
                if (node != null)
                {
                    _historyView.ChangedFilesTree.SelectedNode = node;
                }
                var tempNode = new TreeNode { Tag = entry };
                await OpenHistoryChangedFileAsync(node ?? tempNode);
            }
        };
        _historyView.DiffPanel.Controls.Add(summaryControl);
    }

    private TreeNode? FindNodeByTag(TreeNodeCollection nodes, object tag)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag == tag) return node;
            var found = FindNodeByTag(node.Nodes, tag);
            if (found != null) return found;
        }
        return null;
    }

    private void PopulateHistoryChangedFiles(SvnLogEntry log)
    {
        PopulateHistoryChangedFiles($"Changed files ({log.ChangedFiles.Count})", log.ChangedFiles);
    }

    private void ClearHistoryChangedFiles()
    {
        _historyView.ClearChangedFiles();
    }

    private void PopulateHistoryChangedFiles(string rootText, IReadOnlyList<ChangedFileEntry> files)
    {
        _historyView.SetChangedFiles(rootText, files);
    }

    private static IReadOnlyList<ChangedFileEntry> BuildRangeChangedFiles(IReadOnlyList<SvnLogEntry> logs)
    {
        return logs
            .SelectMany(log => log.ChangedFiles)
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .GroupBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = group.ToList();
                var action = files.All(file => file.Action == "A")
                    ? "A"
                    : files.All(file => file.Action == "D")
                        ? "D"
                        : "M";
                var repositoryPath = files.LastOrDefault(file => !string.IsNullOrWhiteSpace(file.RepositoryPath))?.RepositoryPath ?? "";
                var relativePath = files.LastOrDefault(file => !string.IsNullOrWhiteSpace(file.RelativePath))?.RelativePath ?? group.Key;
                return new ChangedFileEntry(action, repositoryPath, relativePath);
            })
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}

