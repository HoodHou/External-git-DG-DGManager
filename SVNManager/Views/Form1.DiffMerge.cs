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
    private async Task RunDiffAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = GetComparableExtension(relativePath);
        var selectedChange = GetSelectedChange();
        if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
        {
            MessageBox.Show("这是新增文件，没有 SVN 基准版本可对比。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
        {
            MessageBox.Show("本地文件不存在，暂不支持查看 Excel 差异。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在生成差异...");
        var tempBaseFile = DiffTempFileTracker.NewTempFile("SVNManager_BASE", extension);
        try
        {
            var workingCopy = GetWorkingCopyRootPath();
            var localFile = Path.Combine(workingCopy, relativePath);
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict?.ServerPath != null)
            {
                await ShowDiffWindowAsync($"我的版本 -> 服务器版本：{relativePath}", conflict.MinePath, conflict.ServerPath);
                return;
            }

            await _svn.WriteBaseFileAsync(workingCopy, relativePath, tempBaseFile);
            await ShowDiffWindowAsync($"BASE -> 本地：{relativePath}", tempBaseFile, localFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task CompareSelectedFileWithRemoteHeadAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个本地文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await CompareLocalFileWithRemoteHeadAsync(relativePath);
    }

    private async Task CompareSelectedHistoryFileWithRemoteHeadAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        await CompareLocalFileWithRemoteHeadAsync(GetHistoryChangedWorkingCopyRelativePath(file));
    }

    private async Task CompareLocalFileWithRemoteHeadAsync(string relativePath)
    {
        var workingCopy = GetWorkingCopyRootPath();
        var localFile = Path.Combine(workingCopy, relativePath);
        if (!File.Exists(localFile))
        {
            MessageBox.Show("本地文件不存在，无法和远端 HEAD 对比。", "无法对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var tempHeadFile = DiffTempFileTracker.NewTempFile("SVNManager_HEAD", GetComparableExtension(relativePath));
        SetBusy(true, "正在读取远端 HEAD 文件...");
        try
        {
            await _svn.WriteHeadFileAsync(workingCopy, relativePath, tempHeadFile);
            await ShowDiffWindowAsync($"当前本地 -> 远端 HEAD：{relativePath}", localFile, tempHeadFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempHeadFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task CompareSelectedTableWithAnotherAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个表格文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var firstFile = Path.Combine(GetWorkingCopyRootPath(), relativePath);
        if (!File.Exists(firstFile))
        {
            MessageBox.Show("选中的本地文件不存在，无法快速比对。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(firstFile))
        {
            MessageBox.Show("快速表格比对只支持 .xls / .xlsx / .xlsm / SpreadsheetML XML。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var secondFile = PromptSpreadsheetFile("选择要比对的另一个表", firstFile);
        if (string.IsNullOrWhiteSpace(secondFile))
        {
            return;
        }

        await ShowDiffWindowAsync($"快速表格比对：{Path.GetFileName(firstFile)} -> {Path.GetFileName(secondFile)}", firstFile, secondFile);
    }

    private async Task CompareSelectedHistoryFileWithAnotherTableAsync()
    {
        if (!ValidateWorkingCopyPath() || _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var otherFile = PromptSpreadsheetFile("选择要比对的另一个表", GetHistoryChangedLocalPath(file));
        if (string.IsNullOrWhiteSpace(otherFile))
        {
            return;
        }

        var workingCopy = GetWorkingCopyRootPath();
        var tempVersionFile = "";
        SetBusy(true, "正在准备历史表格版本...");
        try
        {
            string firstFile;
            string firstLabel;
            if (_historyView.SelectedLog is { IsUncommitted: false, Revision: > 0 } log)
            {
                if (string.IsNullOrWhiteSpace(file.RepositoryPath))
                {
                    MessageBox.Show("这条历史记录没有仓库路径，无法读取提交版本。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var revision = file.Action == "D" ? log.Revision - 1 : log.Revision;
                if (revision <= 0)
                {
                    MessageBox.Show("无法确定这个文件可比对的历史版本。", "无法比对", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                tempVersionFile = CreateHistoryOpenTempPath($"r{revision}", file.TreePath);
                await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, tempVersionFile);
                firstFile = tempVersionFile;
                firstLabel = $"r{revision} {file.TreePath}";
            }
            else
            {
                firstFile = GetHistoryChangedLocalPath(file);
                firstLabel = $"当前本地 {file.TreePath}";
            }

            if (!File.Exists(firstFile) || !SpreadsheetThreeWayMergeService.IsSupportedPath(firstFile))
            {
                MessageBox.Show("选中的历史文件不是可读取的表格文件。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await ShowDiffWindowAsync($"快速表格比对：{firstLabel} -> {Path.GetFileName(otherFile)}", firstFile, otherFile);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempVersionFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task RunInternalSpreadsheetMergeAsync(string? forcedRelativePath = null)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = string.IsNullOrWhiteSpace(forcedRelativePath)
            ? GetSelectedRelativePath()
            : SvnConflictArtifact.NormalizeToBasePath(forcedRelativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个 XML / Excel 文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var workingCopy = GetWorkingCopyRootPath();
        var localFile = Path.Combine(workingCopy, relativePath);
        if (!File.Exists(localFile))
        {
            MessageBox.Show("本地文件不存在，无法执行内置三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selectedChange = GetSelectedChange();
        var conflictForSupport = ConflictFileSet.Find(workingCopy, relativePath);
        var supportProbeFile = conflictForSupport?.MinePath ?? localFile;
        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(supportProbeFile))
        {
            if (XmlThreeWayMergeService.IsSupportedPath(supportProbeFile))
            {
                await RunInternalXmlMergeAsync(relativePath, selectedChange);
                return;
            }

            MessageBox.Show("内置三方合并当前支持 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格，以及普通业务 XML。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
        {
            MessageBox.Show("这是新增文件，没有 SVN BASE 版本可做三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
        {
            MessageBox.Show("本地文件不存在或已删除，无法执行三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = GetComparableExtension(relativePath);
        var tempBaseFile = DiffTempFileTracker.NewTempFile("SVNManager_MERGE_BASE", extension);
        var tempRemoteFile = DiffTempFileTracker.NewTempFile("SVNManager_MERGE_HEAD", extension);
        var conflict = conflictForSupport;
        var wasConflict = selectedChange?.Status == SvnStatusKind.Conflicted || conflict != null;
        var localMergeInput = conflict?.MinePath ?? localFile;

        SetBusy(true, "正在准备内置表格三方合并...");
        try
        {
            if (conflict?.BasePath != null && conflict.ServerPath != null)
            {
                File.Copy(conflict.BasePath, tempBaseFile, true);
                File.Copy(conflict.ServerPath, tempRemoteFile, true);
            }
            else
            {
                await _svn.WriteBaseFileAsync(workingCopy, relativePath, tempBaseFile);
                await _svn.WriteHeadFileAsync(workingCopy, relativePath, tempRemoteFile);
            }

            var plan = await SpreadsheetMergeWorker.BuildPlanAsync(tempBaseFile, localMergeInput, tempRemoteFile);
            if (plan.RelevantChangeCount == 0)
            {
                if (conflict != null)
                {
                    var restoredBackupPath = RestoreConflictMineToWorkingFile(localFile, localMergeInput);
                    WriteOutput($"内置三方合并未发现表格差异，已用本地 .mine 恢复工作副本：{relativePath}\r\n备份：{restoredBackupPath}");
                    await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
                }
                else
                {
                    MessageBox.Show("BASE、本地和远端 HEAD 没有需要合并的表格差异。", "无需合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return;
            }

            SetBusy(false, "等待确认合并项目");
            using (var form = new SpreadsheetMergeConflictForm(
                relativePath,
                plan,
                "内置表格三方合并 - 合并项目预览",
                "本地",
                "远端 HEAD",
                "写入工作副本"))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消内置三方合并：{relativePath}");
                    return;
                }
            }

            SetBusy(true, "正在写入合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show("当前选择全部保留本地，没有需要写入工作副本的远端表格改动。", "无需写入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (conflict != null)
                {
                    var restoredBackupPath = RestoreConflictMineToWorkingFile(localFile, localMergeInput);
                    WriteOutput($"已保留本地 .mine 作为合并结果：{relativePath}\r\n备份：{restoredBackupPath}");
                }

                await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(localFile);
            if (conflict != null)
            {
                File.Copy(localMergeInput, localFile, true);
            }

            await SpreadsheetMergeWorker.ApplyWritesAsync(localFile, writes);
            OperationLogger.Log("InternalSpreadsheetMergeSuccess", workingCopy, $"{relativePath}; writes={writes.Count}; backup={backupPath}");
            WriteOutput($"内置三方合并已写入 {writes.Count} 个单元格：{relativePath}\r\n备份：{backupPath}");
            await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
            await RefreshStatusAsync();
            LoadAllFiles();
            await LoadRepositoryHistoryAsync();
        }
        catch (Exception ex)
        {
            OperationLogger.Log("InternalSpreadsheetMergeFailed", workingCopy, $"{relativePath}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            TryDelete(tempRemoteFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task RunInternalXmlMergeAsync(string relativePath, SvnChange? selectedChange)
    {
        var workingCopy = GetWorkingCopyRootPath();
        var localFile = Path.Combine(workingCopy, relativePath);
        if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
        {
            MessageBox.Show("这是新增文件，没有 SVN BASE 版本可做三方合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
        {
            MessageBox.Show("本地文件不存在或已删除，无法执行 XML 合并。", "无法合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extension = GetComparableExtension(relativePath);
        var tempBaseFile = DiffTempFileTracker.NewTempFile("SVNManager_XML_MERGE_BASE", extension);
        var tempRemoteFile = DiffTempFileTracker.NewTempFile("SVNManager_XML_MERGE_HEAD", extension);
        var conflict = ConflictFileSet.Find(workingCopy, relativePath);
        var wasConflict = selectedChange?.Status == SvnStatusKind.Conflicted || conflict != null;
        var localMergeInput = conflict?.MinePath ?? localFile;

        SetBusy(true, "正在准备普通 XML 三方合并...");
        try
        {
            if (conflict?.BasePath != null && conflict.ServerPath != null)
            {
                File.Copy(conflict.BasePath, tempBaseFile, true);
                File.Copy(conflict.ServerPath, tempRemoteFile, true);
            }
            else
            {
                await _svn.WriteBaseFileAsync(workingCopy, relativePath, tempBaseFile);
                await _svn.WriteHeadFileAsync(workingCopy, relativePath, tempRemoteFile);
            }

            var plan = await Task.Run(() => XmlThreeWayMergeService.BuildPlan(tempBaseFile, localMergeInput, tempRemoteFile));
            if (plan.RelevantChangeCount == 0)
            {
                if (conflict != null)
                {
                    var restoredBackupPath = RestoreConflictMineToWorkingFile(localFile, localMergeInput);
                    WriteOutput($"普通 XML 三方合并未发现结构差异，已用本地 .mine 恢复工作副本：{relativePath}\r\n备份：{restoredBackupPath}");
                    await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
                }
                else
                {
                    MessageBox.Show("BASE、本地和远端 HEAD 没有需要合并的 XML 结构差异。", "无需合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return;
            }

            SetBusy(false, "等待确认 XML 合并项目");
            using (var form = new XmlMergeConflictForm(relativePath, plan, "本地", "远端 HEAD", "写入工作副本"))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消普通 XML 三方合并：{relativePath}");
                    return;
                }
            }

            SetBusy(true, "正在写入 XML 合并结果...");
            var actions = plan.BuildActions();
            if (actions.Count == 0)
            {
                MessageBox.Show("当前选择全部保留本地，没有需要写入工作副本的远端 XML 改动。", "无需写入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (conflict != null)
                {
                    var restoredBackupPath = RestoreConflictMineToWorkingFile(localFile, localMergeInput);
                    WriteOutput($"已保留本地 .mine 作为 XML 合并结果：{relativePath}\r\n备份：{restoredBackupPath}");
                }

                await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(localFile);
            if (conflict != null)
            {
                File.Copy(localMergeInput, localFile, true);
            }

            await Task.Run(() => XmlThreeWayMergeService.ApplyActions(localFile, actions));
            OperationLogger.Log("InternalXmlMergeSuccess", workingCopy, $"{relativePath}; actions={actions.Count}; backup={backupPath}");
            WriteOutput($"普通 XML 三方合并已写入 {actions.Count} 项改动：{relativePath}\r\n备份：{backupPath}");
            await OfferResolveAfterInternalMergeAsync(relativePath, wasConflict);
            await RefreshStatusAsync();
            LoadAllFiles();
            await LoadRepositoryHistoryAsync();
        }
        catch (Exception ex)
        {
            OperationLogger.Log("InternalXmlMergeFailed", workingCopy, $"{relativePath}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            TryDelete(tempRemoteFile);
            SetBusy(false, "就绪");
        }
    }

    private async Task RunCrossRepositorySpreadsheetMergeAsync()
    {
        var defaultTargetFile = "";
        if (ValidateWorkingCopyPathForBackground())
        {
            var relativePath = GetSelectedRelativePath();
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                var candidate = Path.Combine(GetWorkingCopyRootPath(), relativePath);
                if (File.Exists(candidate))
                {
                    defaultTargetFile = candidate;
                }
            }
        }

        using var picker = new CrossRepositorySpreadsheetMergeForm(defaultTargetFile);
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var baseFile = picker.BaseFilePath;
        var changedFile = picker.ChangedFilePath;
        var targetFile = picker.TargetFilePath;
        var displayName = Path.GetFileName(targetFile);

        SetBusy(true, "正在计算跨库表格三方合并...");
        try
        {
            var plan = await SpreadsheetMergeWorker.BuildPlanAsync(baseFile, targetFile, changedFile);
            if (plan.RelevantChangeCount == 0)
            {
                MessageBox.Show(
                    "A、B、C 三个文件没有需要合并的表格差异。",
                    "无需合并",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(false, "等待确认跨库合并项目");
            using (var conflictForm = new SpreadsheetMergeConflictForm(
                displayName,
                plan,
                "跨库表格三方合并 - 合并项目预览",
                "目标 C",
                "B 改动后",
                "写入目标 C"))
            {
                if (conflictForm.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消跨库表格三方合并：{targetFile}");
                    return;
                }
            }

            SetBusy(true, "正在写入跨库合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show(
                    "当前选择全部保留目标 C，没有需要写入的 A->B 改动。",
                    "无需写入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(targetFile);
            await SpreadsheetMergeWorker.ApplyWritesAsync(targetFile, writes);
            OperationLogger.Log("CrossRepositorySpreadsheetMergeSuccess", GetWorkingCopyRootPath(), $"{targetFile}; writes={writes.Count}; backup={backupPath}");
            WriteOutput(
                $"跨库表格三方合并已写入 {writes.Count} 个单元格到目标 C：{targetFile}\r\n" +
                $"A 改动前：{baseFile}\r\n" +
                $"B 改动后：{changedFile}\r\n" +
                $"备份：{backupPath}");

            if (IsCurrentWorkingCopyFile(targetFile))
            {
                await RefreshStatusAsync();
                LoadAllFiles();
            }
        }
        catch (Exception ex)
        {
            OperationLogger.Log("CrossRepositorySpreadsheetMergeFailed", GetWorkingCopyRootPath(), $"{targetFile}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunSelectedCommitSpreadsheetMergeAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var committedLogs = GetSelectedCommittedHistoryLogs();
        if (committedLogs.Count == 0)
        {
            return;
        }

        var firstRevision = committedLogs.First().Revision;
        var lastRevision = committedLogs.Last().Revision;
        var scopeLabel = firstRevision == lastRevision
            ? $"r{lastRevision}"
            : $"r{firstRevision}-r{lastRevision}";

        if (file.Action is "A" or "D")
        {
            MessageBox.Show(
                "新增或删除文件没有完整的“范围开始前 -> 范围结束后”两侧表格版本，暂不支持用这段提交做三方合并。",
                "无法三方合并",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(file.RepositoryPath))
        {
            MessageBox.Show("这条历史记录没有仓库路径，无法读取提交前后的文件。", "无法三方合并", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var defaultTargetFile = GetHistoryChangedLocalPath(file);
        var targetFile = PromptSpreadsheetFile("选择要写入的目标表 C", defaultTargetFile);
        if (string.IsNullOrWhiteSpace(targetFile))
        {
            return;
        }

        var workingCopy = GetWorkingCopyRootPath();
        var tempBaseFile = CreateHistoryOpenTempPath($"r{firstRevision - 1}_before", file.TreePath);
        var tempChangedFile = CreateHistoryOpenTempPath($"r{lastRevision}_after", file.TreePath);
        SetBusy(true, "正在准备所选提交范围的三方合并...");
        try
        {
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, firstRevision - 1, tempBaseFile);
            await _svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, lastRevision, tempChangedFile);

            if (!SpreadsheetThreeWayMergeService.IsSupportedPath(tempBaseFile) ||
                !SpreadsheetThreeWayMergeService.IsSupportedPath(tempChangedFile) ||
                !SpreadsheetThreeWayMergeService.IsSupportedPath(targetFile))
            {
                MessageBox.Show(
                    "所选提交/范围三方合并只支持 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格。",
                    "文件类型不适合",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var plan = await SpreadsheetMergeWorker.BuildPlanAsync(tempBaseFile, targetFile, tempChangedFile);
            if (plan.RelevantChangeCount == 0)
            {
                MessageBox.Show(
                    $"{scopeLabel} 对这个表没有可合并的单元格差异。",
                    "无需合并",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            SetBusy(false, "等待确认所选提交范围合并项目");
            using (var conflictForm = new SpreadsheetMergeConflictForm(
                file.TreePath,
                plan,
                $"用 {scopeLabel} 提交范围三方合并 - 合并项目预览",
                "目标 C",
                scopeLabel,
                "写入目标 C"))
            {
                if (conflictForm.ShowDialog(this) != DialogResult.OK)
                {
                    WriteOutput($"已取消用 {scopeLabel} 三方合并：{file.TreePath}");
                    return;
                }
            }

            SetBusy(true, "正在写入所选提交范围三方合并结果...");
            var writes = plan.BuildWrites();
            if (writes.Count == 0)
            {
                MessageBox.Show(
                    "当前选择全部保留目标 C，没有需要写入的提交范围改动。",
                    "无需写入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(targetFile);
            await SpreadsheetMergeWorker.ApplyWritesAsync(targetFile, writes);
            OperationLogger.Log("CommitSpreadsheetMergeSuccess", workingCopy, $"range={firstRevision}-{lastRevision}; file={file.TreePath}; target={targetFile}; writes={writes.Count}; backup={backupPath}");
            WriteOutput(
                $"已把 {scopeLabel} 对表格的累计改动三方合并到目标 C，写入 {writes.Count} 个单元格。\r\n" +
                $"历史文件：{file.TreePath}\r\n" +
                $"目标 C：{targetFile}\r\n" +
                $"备份：{backupPath}");

            if (IsCurrentWorkingCopyFile(targetFile))
            {
                await RefreshStatusAsync();
                LoadAllFiles();
            }
        }
        catch (Exception ex)
        {
            OperationLogger.Log("CommitSpreadsheetMergeFailed", workingCopy, $"range={firstRevision}-{lastRevision}; file={file.TreePath}; {ex.Message}");
            ShowError(ex);
        }
        finally
        {
            TryDelete(tempBaseFile);
            TryDelete(tempChangedFile);
            SetBusy(false, "就绪");
        }
    }

    private bool ConfirmCrossRepositorySpreadsheetMergeWrite(string baseFile, string changedFile, string targetFile, SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把 A->B 的表格改动写入目标 C。{Environment.NewLine}{Environment.NewLine}" +
            $"A 改动前：{baseFile}{Environment.NewLine}" +
            $"B 改动后：{changedFile}{Environment.NewLine}" +
            $"C 目标文件：{targetFile}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用 A->B 改动：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留目标 C 独有改动：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份目标 C。继续？";
        return MessageBox.Show(
            message,
            "跨库表格三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private bool ConfirmCommitSpreadsheetMergeWrite(
        ChangedFileEntry file,
        long firstRevision,
        long lastRevision,
        string scopeLabel,
        string targetFile,
        SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把 {scopeLabel} 对这个表的累计改动写入目标 C。{Environment.NewLine}{Environment.NewLine}" +
            $"历史文件：{file.TreePath}{Environment.NewLine}" +
            $"改动范围：r{firstRevision - 1} -> r{lastRevision}{Environment.NewLine}" +
            $"目标 C：{targetFile}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用提交范围改动：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留目标 C 独有改动：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份目标 C。继续？";
        return MessageBox.Show(
            message,
            "用所选提交/范围三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private IReadOnlyList<SvnLogEntry> GetSelectedCommittedHistoryLogs()
    {
        var logs = _historyView.SelectedLogs.Count > 0
            ? _historyView.SelectedLogs
            : _historyView.SelectedLog != null ? [_historyView.SelectedLog] : [];
        return logs
            .Where(log => !log.IsUncommitted && log.Revision > 0)
            .OrderBy(log => log.Revision)
            .ToList();
    }

    private string? PromptSpreadsheetFile(string title, string initialFile = "")
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "表格文件 (*.xml;*.xls;*.xlsx;*.xlsm)|*.xml;*.xls;*.xlsx;*.xlsm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialFile))
        {
            var initialDirectory = File.Exists(initialFile)
                ? Path.GetDirectoryName(initialFile)
                : Directory.Exists(initialFile) ? initialFile : Path.GetDirectoryName(initialFile);
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            if (File.Exists(initialFile))
            {
                dialog.FileName = Path.GetFileName(initialFile);
            }
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        if (!SpreadsheetThreeWayMergeService.IsSupportedPath(dialog.FileName))
        {
            MessageBox.Show(
                "请选择 .xls / .xlsx / .xlsm / SpreadsheetML XML 表格文件。",
                "文件类型不适合",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        return dialog.FileName;
    }

    private bool IsCurrentWorkingCopyFile(string filePath)
    {
        var workingCopy = GetWorkingCopyRootPath();
        if (string.IsNullOrWhiteSpace(workingCopy) || !Directory.Exists(workingCopy))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workingCopy)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(filePath);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool ConfirmSpreadsheetMergeWrite(string relativePath, SpreadsheetMergePlan plan)
    {
        var message =
            $"准备把远端 HEAD 的非冲突表格改动写入工作副本：{relativePath}{Environment.NewLine}{Environment.NewLine}" +
            $"自动应用远端：{plan.AutoRemoteChanges.Count} 项{Environment.NewLine}" +
            $"保留本地：{plan.LocalOnlyChanges.Count} 项{Environment.NewLine}" +
            $"两边相同：{plan.SameBothChanges.Count} 项{Environment.NewLine}" +
            $"冲突：0 项{Environment.NewLine}{Environment.NewLine}" +
            "写入前会自动备份当前本地文件。继续？";
        return MessageBox.Show(
            message,
            "内置表格三方合并",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private async Task OfferResolveAfterInternalMergeAsync(string relativePath, bool wasConflict)
    {
        if (!wasConflict)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"合并结果已经保存到工作副本。是否现在执行 svn resolve --accept working？{Environment.NewLine}{Environment.NewLine}{relativePath}",
            "标记冲突已解决",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm == DialogResult.OK)
        {
            await ResolveConflictPathCoreAsync(relativePath);
        }
    }

    private static string RestoreConflictMineToWorkingFile(string localFile, string mineFile)
    {
        var backupPath = SpreadsheetThreeWayMergeService.CreateBackup(localFile);
        File.Copy(mineFile, localFile, true);
        return backupPath;
    }


    private async Task RunExternalCompareOrMergeAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个 XML 表格文件或冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备外部对比...");
        try
        {
            var workingCopy = GetWorkingCopyRootPath();
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict != null)
            {
                LaunchExternalConflictCompare(conflict);
                return;
            }

            if (!IsExternalTableToolSupported(relativePath))
            {
                MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。Lua 等文本文件请先使用内置差异查看。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedChange = GetSelectedChange();
            if (selectedChange?.Status is SvnStatusKind.Unversioned or SvnStatusKind.Added)
            {
                MessageBox.Show("这是新增文件，没有 SVN 基准版本可对比。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (selectedChange?.Status is SvnStatusKind.Missing or SvnStatusKind.Deleted)
            {
                MessageBox.Show("本地文件不存在，无法交给外部工具。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var localFile = Path.Combine(workingCopy, relativePath);
            if (!File.Exists(localFile))
            {
                MessageBox.Show("本地没有找到这个文件。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var baseFile = CreateExternalTempPath("BASE", relativePath);
            await _svn.WriteBaseFileAsync(workingCopy, relativePath, baseFile);
            if (LaunchExternalMergeTool(baseFile, localFile))
            {
                WriteOutput($"已打开分久必合：BASE -> 本地：{relativePath}");
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

    private async Task RunSelectedHistoryChangedFileExternalCompareAsync()
    {
        if (!ValidateWorkingCopyPath() ||
            _historyView.ChangedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file ||
            (_historyView.SelectedLog == null && _historyView.SelectedLogs.Count <= 1))
        {
            return;
        }

        if (!IsExternalTableToolSupported(file.TreePath))
        {
            MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。Lua 等文本文件请先使用内置差异预览。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备外部对比...");
        try
        {
            if (_historyView.SelectedLog?.IsUncommitted == true && file.Action == "C")
            {
                var conflict = ConflictFileSet.Find(GetWorkingCopyRootPath(), file.RelativePath);
                if (conflict != null)
                {
                    LaunchExternalConflictCompare(conflict);
                    return;
                }
            }

            var oldTemp = CreateExternalTempPath("OLD", file.TreePath);
            var newTemp = CreateExternalTempPath("NEW", file.TreePath);
            var workingCopy = GetWorkingCopyRootPath();
            if (_historyView.SelectedLogs.Count > 1)
            {
                var committedLogs = _historyView.SelectedLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
                if (committedLogs.Count == 0)
                {
                    MessageBox.Show("多选范围不支持只选择未提交改动。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                await PrepareRangeDiffFilesAsync(_svn, workingCopy, committedLogs.First().Revision, committedLogs.Last().Revision, file, oldTemp, newTemp);
            }
            else if (_historyView.SelectedLog?.IsUncommitted == true)
            {
                await PrepareUncommittedDiffFilesAsync(workingCopy, file, oldTemp, newTemp);
            }
            else if (_historyView.SelectedLog != null)
            {
                await PrepareCommittedDiffFilesAsync(_svn, workingCopy, _historyView.SelectedLog.Revision, file, oldTemp, newTemp);
            }

            if (LaunchExternalMergeTool(oldTemp, newTemp))
            {
                WriteOutput($"已打开分久必合：{file.DisplayText}");
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

    private bool LaunchExternalConflictCompare(ConflictFileSet conflict)
    {
        if (conflict.ServerPath == null)
        {
            MessageBox.Show("没有找到服务器版本文件，无法外部对比。", "无法外部对比", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!IsExternalTableToolSupported(conflict.RelativePath))
        {
            MessageBox.Show("分久必合当前主要用于 XML / XLSX / CSV 表格。", "文件类型不适合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var mineFile = CopyToExternalTempFile(conflict.MinePath, "MINE", conflict.RelativePath);
        var serverFile = CopyToExternalTempFile(conflict.ServerPath, "SERVER", conflict.RelativePath);
        if (LaunchExternalMergeTool(mineFile, serverFile))
        {
            WriteOutput($"已打开分久必合：我的版本 -> 服务器版本：{conflict.RelativePath}");
            return true;
        }

        return false;
    }

    private static void OpenConflictFileFolder(ConflictFileSet conflict)
    {
        var argument = File.Exists(conflict.CurrentPath)
            ? $"/select,\"{conflict.CurrentPath}\""
            : Path.GetDirectoryName(conflict.CurrentPath);
        if (!string.IsNullOrWhiteSpace(argument))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
    }

    private bool LaunchExternalMergeTool(params string[] filePaths)
    {
        var toolPath = ResolveExternalMergeToolPath();
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            MessageBox.Show("没有配置分久必合.exe。请在“更多操作 -> 设置”里选择本机的分久必合.exe。", "外部工具未配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var startInfo = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
        };
        foreach (var filePath in filePaths.Where(File.Exists))
        {
            startInfo.ArgumentList.Add(filePath);
        }

        Process.Start(startInfo);
        OperationLogger.Log("OpenExternalMergeTool", GetWorkingCopyRootPath(), string.Join(" | ", filePaths));
        return true;
    }

    private string? ResolveExternalMergeToolPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath) && File.Exists(_settings.ExternalMergeToolPath))
        {
            return _settings.ExternalMergeToolPath;
        }

        if (!string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath) && !File.Exists(_settings.ExternalMergeToolPath))
        {
            MessageBox.Show(
                $"配置的分久必合路径不存在：{Environment.NewLine}{_settings.ExternalMergeToolPath}{Environment.NewLine}{Environment.NewLine}请在“更多操作 -> 设置”里重新选择。",
                "外部工具路径失效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        return null;
    }

    private static bool IsExternalTableToolSupported(string path)
    {
        var extension = GetComparableExtension(path);
        return extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private static string CopyToExternalTempFile(string sourcePath, string label, string relativePath)
    {
        var targetPath = CreateExternalTempPath(label, relativePath);
        File.Copy(sourcePath, targetPath, true);
        return targetPath;
    }

    private static string CreateExternalTempPath(string label, string relativePath)
    {
        var directory = DiffTempFileTracker.NewTempDirectory("ExternalCompare");
        Directory.CreateDirectory(directory);
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(relativePath);
        var extension = GetComparableExtension(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(comparablePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        return Path.Combine(directory, $"{SanitizeFileName(label)}_{SanitizeFileName(fileName)}{extension}");
    }

    private static string CreateHistoryOpenTempPath(string label, string relativePath)
    {
        var directory = DiffTempFileTracker.NewTempDirectory("HistoryOpen");
        Directory.CreateDirectory(directory);
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(relativePath);
        var extension = GetComparableExtension(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(comparablePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        return Path.Combine(directory, $"{SanitizeFileName(fileName)}_{SanitizeFileName(label)}{extension}");
    }

    private static string FormatPathListLabel(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return "";
        }

        if (relativePaths.Count == 1)
        {
            return relativePaths[0];
        }

        return $"{relativePaths.Count} 个路径：" + string.Join("、", relativePaths.Take(3)) + (relativePaths.Count > 3 ? "..." : "");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static string GetComparableExtension(string path)
    {
        return Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(path));
    }


    private async Task ShowDiffWindowAsync(string title, string oldFilePath, string newFilePath)
    {
        using var cts = new CancellationTokenSource();
        using var overlay = new DiffLoadingOverlay(title, cts.Cancel);
        overlay.Show(this);
        DiffPreviewData data;
        try
        {
            var progress = new Progress<DiffProgress>(overlay.SetProgress);
            data = await new DiffPreviewService().ComputeAsync(
                oldFilePath,
                newFilePath,
                _settings.DiffOptions,
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            WriteOutput($"已取消差异计算：{title}");
            return;
        }
        finally
        {
            if (!overlay.IsDisposed)
            {
                overlay.Close();
            }
        }

        if (data.ExcelDifferences != null)
        {
            using var form = new ExcelDiffForm(title, data);
            form.ShowDialog(this);
            WriteOutput($"{data.Summary}：{title}");
            return;
        }

        using var textForm = new TextDiffForm(title, data);
        textForm.ShowDialog(this);
        WriteOutput($"{data.Summary}：{title}");
    }


    private async Task ShowSelectedHistoryFileDiffAsync(TreeNode? node)
    {
        if (node?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var previewCts = BeginHistoryDiffPreview();
        var token = previewCts.Token;
        var extension = GetComparableExtension(file.TreePath);
        var oldTemp = DiffTempFileTracker.NewTempFile("SVNManager_OLD", extension);
        var newTemp = DiffTempFileTracker.NewTempFile("SVNManager_NEW", extension);
        ShowHistoryDiffLoading(file.TreePath, "正在准备文件版本...");
        SetBusy(true, "正在读取文件差异...");
        try
        {
            var workingCopy = GetWorkingCopyRootPath();
            var title = "";
            var cacheKey = "";
            if (_historyView.SelectedLogs.Count > 1)
            {
                var committedLogs = _historyView.SelectedLogs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
                if (committedLogs.Count == 0)
                {
                    ShowHistorySummary("多选范围不支持只选择未提交改动。");
                    return;
                }

                var firstRevision = committedLogs.First().Revision;
                var lastRevision = committedLogs.Last().Revision;
                title = BuildDiffTitle(file.TreePath, $"r{firstRevision - 1}", $"r{lastRevision}", "选中提交范围");
                cacheKey = BuildHistoryDiffCacheKey("range", file, firstRevision - 1, lastRevision);
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareRangeDiffFilesAsync(_svn, workingCopy, firstRevision, lastRevision, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
                return;
            }

            if (_historyView.SelectedLog == null)
            {
                return;
            }

            if (_historyView.SelectedLog.IsUncommitted && file.Action == "C")
            {
                var conflict = ConflictFileSet.Find(workingCopy, file.RelativePath);
                if (conflict?.ServerPath != null)
                {
                    title = BuildDiffTitle(file.TreePath, "我的版本", "服务器版本", "SVN 冲突");
                    cacheKey = BuildHistoryDiffCacheKey("conflict", file, 0, 0, FileVersionStamp(conflict.MinePath), FileVersionStamp(conflict.ServerPath));
                    if (!TryRenderCachedHistoryDiff(title, cacheKey, token))
                    {
                        await ShowDiffPreviewAsync(title, conflict.MinePath, conflict.ServerPath, cacheKey, token);
                    }

                    return;
                }
            }

            if (_historyView.SelectedLog.IsUncommitted)
            {
                var localPath = Path.Combine(workingCopy, file.RelativePath);
                title = BuildDiffTitle(file.TreePath, "SVN BASE", "本地工作副本", "未提交改动");
                cacheKey = BuildHistoryDiffCacheKey("uncommitted", file, 0, 0, FileVersionStamp(localPath));
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareUncommittedDiffFilesAsync(workingCopy, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
            }
            else
            {
                title = BuildDiffTitle(file.TreePath, $"r{_historyView.SelectedLog.Revision - 1}", $"r{_historyView.SelectedLog.Revision}", "单次提交");
                cacheKey = BuildHistoryDiffCacheKey("commit", file, _historyView.SelectedLog.Revision - 1, _historyView.SelectedLog.Revision);
                if (TryRenderCachedHistoryDiff(title, cacheKey, token))
                {
                    return;
                }

                await PrepareCommittedDiffFilesAsync(_svn, workingCopy, _historyView.SelectedLog.Revision, file, oldTemp, newTemp, token);
                await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowHistorySummary(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
            if (IsCurrentHistoryDiffPreview(previewCts))
            {
                SetBusy(false, "就绪");
            }
        }
    }

    private async Task PrepareUncommittedDiffFilesAsync(string workingCopy, ChangedFileEntry file, string oldTemp, string newTemp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localPath = Path.Combine(workingCopy, file.RelativePath);
        if (file.Action is "A" or "?")
        {
            File.WriteAllText(oldTemp, "");
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(localPath, newTemp, true);
            return;
        }

        if (file.Action is "D" or "!")
        {
            await _svn.WriteBaseFileAsync(workingCopy, file.RelativePath, oldTemp, cancellationToken);
            File.WriteAllText(newTemp, "");
            return;
        }

        await _svn.WriteBaseFileAsync(workingCopy, file.RelativePath, oldTemp, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(localPath, newTemp, true);
    }

    private CancellationTokenSource BeginHistoryDiffPreview()
    {
        CancelHistoryDiffPreview();
        _historyDiffPreviewCts = new CancellationTokenSource();
        return _historyDiffPreviewCts;
    }

    private void CancelHistoryDiffPreview()
    {
        try
        {
            _historyDiffPreviewCts?.Cancel();
        }
        catch
        {
        }
    }

    private void ClearHistoryDiffPreviewCache()
    {
        CancelHistoryDiffPreview();
        _historyDiffPreviewCache.Clear();
    }

    private bool IsCurrentHistoryDiffPreview(CancellationTokenSource previewCts)
    {
        return ReferenceEquals(_historyDiffPreviewCts, previewCts);
    }

    private bool TryRenderCachedHistoryDiff(string title, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_historyDiffPreviewCache.TryGet(cacheKey, out var data))
        {
            return false;
        }

        _historyView.DiffHeaderLabel.Text = "Diff preview";
        _historyView.DiffMaximizeButton.Visible = true;
        RenderDiffPreviewInPanel(_historyView.DiffPanel, null, title + "    [缓存]", data);
        return true;
    }

    private async Task ShowDiffPreviewAsync(string title, string oldFilePath, string newFilePath, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ShowHistoryDiffLoading(title, "正在计算差异...");
        var data = await new DiffPreviewService().ComputeAsync(oldFilePath, newFilePath, _settings.DiffOptions, null, token);
        token.ThrowIfCancellationRequested();
        AddHistoryDiffPreviewCache(cacheKey, data);
        _historyView.DiffHeaderLabel.Text = "Diff preview";
        _historyView.DiffMaximizeButton.Visible = true;
        RenderDiffPreviewInPanel(_historyView.DiffPanel, null, title, data);
    }

    private void AddHistoryDiffPreviewCache(string cacheKey, DiffPreviewData data)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        _historyDiffPreviewCache.Set(cacheKey, data);
    }

    private void ShowHistoryDiffLoading(string title, string message)
    {
        _historyView.DiffHeaderLabel.Text = "Diff preview";
        _historyView.DiffMaximizeButton.Visible = true;

        ClearHistoryDiffPanel();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(85, 95, 105),
        }, 0, 1);
        _historyView.DiffPanel.Controls.Add(root);
    }

    private string BuildHistoryDiffCacheKey(string scope, ChangedFileEntry file, long oldRevision, long newRevision, params string[] stamps)
    {
        var workingCopy = GetWorkingCopyRootPath();
        var repository = _configView.RepositoryUrl.Trim();
        return string.Join("|",
            "history",
            scope,
            repository,
            workingCopy,
            oldRevision.ToString(),
            newRevision.ToString(),
            file.Action,
            file.RepositoryPath,
            file.RelativePath,
            file.TreePath,
            string.Join(";", stamps));
    }

    private static string FileVersionStamp(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"{path}:missing";
            }

            var info = new FileInfo(path);
            return $"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"{path}:unknown";
        }
    }

    internal static async Task PrepareCommittedDiffFilesAsync(SvnClient svn, string workingCopy, long revision, ChangedFileEntry file, string oldTemp, string newTemp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Action == "A")
        {
            File.WriteAllText(oldTemp, "");
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, newTemp, cancellationToken);
            return;
        }

        if (file.Action == "D")
        {
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision - 1, oldTemp, cancellationToken);
            File.WriteAllText(newTemp, "");
            return;
        }

        await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision - 1, oldTemp, cancellationToken);
        await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, file.RepositoryPath, revision, newTemp, cancellationToken);
    }

    internal static async Task PrepareRangeDiffFilesAsync(
        SvnClient svn,
        string workingCopy,
        long firstRevision,
        long lastRevision,
        ChangedFileEntry file,
        string oldTemp,
        string newTemp,
        CancellationToken cancellationToken = default)
    {
        await TryWriteRepositoryFileAtRevisionAsync(svn, workingCopy, file.RepositoryPath, firstRevision - 1, oldTemp, cancellationToken);
        await TryWriteRepositoryFileAtRevisionAsync(svn, workingCopy, file.RepositoryPath, lastRevision, newTemp, cancellationToken);
    }

    private static async Task TryWriteRepositoryFileAtRevisionAsync(SvnClient svn, string workingCopy, string repositoryPath, long revision, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            await svn.WriteRepositoryFileAtRevisionAsync(workingCopy, repositoryPath, revision, outputPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            File.WriteAllText(outputPath, "");
        }
    }

    internal static void ShowDiffPreviewInPanel(Panel panel, Label? headerLabel, string title, string oldFilePath, string newFilePath)
    {
        RenderDiffPreviewInPanel(panel, headerLabel, title, CreateDiffPreviewData(oldFilePath, newFilePath));
    }

    internal static DiffPreviewData CreateDiffPreviewData(string oldFilePath, string newFilePath)
    {
        return new DiffPreviewService().Compute(oldFilePath, newFilePath);
    }

    internal static void RenderDiffPreviewInPanel(Panel panel, Label? headerLabel, string title, DiffPreviewData data)
    {
        ClearControlsDisposing(panel);
        var header = headerLabel ?? new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        };
        header.Text = title;
        panel.Controls.Add(header);

        var diffControl = DiffPreviewViewFactory.Create(data);
        diffControl.Dock = DockStyle.Fill;
        panel.Controls.Add(diffControl);
        diffControl.BringToFront();
    }

    private void ClearHistoryDiffPanel()
    {
        ClearControlsDisposing(_historyView.DiffPanel, _historyView.DetailText);
    }

    internal static void ClearControlsDisposing(Control parent, params Control[] keepAlive)
    {
        var keep = keepAlive
            .Where(control => control != null)
            .ToHashSet();
        var oldControls = parent.Controls.Cast<Control>().ToList();
        parent.Controls.Clear();
        foreach (var control in oldControls)
        {
            if (keep.Contains(control))
            {
                continue;
            }

            control.Dispose();
        }
    }

    private void ShowDiffPreview(string title, string oldFilePath, string newFilePath)
    {
        ShowDiffPreviewInPanel(_historyView.DiffPanel, null, title, oldFilePath, newFilePath);
    }

    private static string BuildDiffTitle(string path, string oldLabel, string newLabel, string scope)
    {
        return $"{path}{Environment.NewLine}{scope}: {oldLabel} -> {newLabel}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp cleanup failure should not block normal use.
        }
    }
}

