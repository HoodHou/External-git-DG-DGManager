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
    private Control CreateConflictPanel()
    {
        _conflictView.RefreshRequested += async (_, _) => await RefreshStatusAsync();
        _conflictView.ActionRequested += async (_, args) => await HandleConflictActionAsync(args);
        return _conflictView;
    }


    private void RefreshConflictPanel(IReadOnlyList<SvnChange> conflicts)
    {
        _conflictView.SetConflicts(conflicts);
    }


    private async Task HandleConflictActionAsync(ConflictActionRequestedEventArgs args)
    {
        switch (args.ColumnName)
        {
            case "InternalMerge":
                await RunInternalSpreadsheetMergeAsync(args.RelativePath);
                break;
            case "OpenMerge":
                OpenConflictMerge(args.RelativePath);
                break;
            case "OpenFolder":
                OpenConflictFolderByPath(args.RelativePath);
                break;
            case "Resolve":
                await ResolveConflictPathAsync(args.RelativePath);
                break;
        }
    }


    private async Task RunConflictViewerAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在读取冲突版本...");
        await Task.Yield();
        try
        {
            var workingCopy = GetWorkingCopyRootPath();
            var conflict = ConflictFileSet.Find(workingCopy, relativePath);
            if (conflict == null)
            {
                MessageBox.Show("没有找到 .mine / .r版本号 冲突辅助文件。", "不是冲突文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var form = new ConflictViewerForm(conflict, conflictFile => LaunchExternalConflictCompare(conflictFile));
            form.ShowDialog(this);
            WriteOutput($"已打开冲突查看器：{conflict.RelativePath}");
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

    private async Task RunConflictWorkflowAsync()
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var relativePath = GetSelectedRelativePath();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            MessageBox.Show("请先选中一个冲突文件。", "未选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, "正在准备冲突处理...");
        try
        {
            var conflict = FindConflictOrWarn(relativePath);
            if (conflict == null || !LaunchExternalConflictCompare(conflict))
            {
                return;
            }

            OpenConflictFileFolder(conflict);
            SetBusy(false, "等待手动合并完成");
            var confirm = MessageBox.Show(
                "已经打开分久必合和当前文件目录。\r\n\r\n请在外部工具中完成合并，并把最终结果保存到当前冲突文件后，再点击“确定”。\r\n\r\n确定后会执行 svn resolve，并刷新状态。",
                "确认合并已保存",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.OK)
            {
                WriteOutput($"已打开冲突处理工具，尚未标记已解决：{conflict.RelativePath}");
                return;
            }

            SetBusy(true, "正在标记冲突已解决...");
            await ResolveConflictPathCoreAsync(conflict.RelativePath);
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

    private void OpenConflictMerge(string relativePath)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        SetBusy(true, "正在打开合并工具...");
        try
        {
            var conflict = FindConflictOrWarn(relativePath);
            if (conflict != null && LaunchExternalConflictCompare(conflict))
            {
                WriteOutput($"已打开合并工具：{conflict.RelativePath}");
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

    private void OpenConflictFolderByPath(string relativePath)
    {
        var conflict = FindConflictOrWarn(relativePath);
        if (conflict != null)
        {
            OpenConflictFileFolder(conflict);
            WriteOutput($"已打开冲突文件目录：{conflict.RelativePath}");
        }
    }

    private async Task ResolveConflictPathAsync(string relativePath)
    {
        if (!ValidateWorkingCopyPath())
        {
            return;
        }

        var conflict = FindConflictOrWarn(relativePath);
        if (conflict == null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"确认已经把最终合并结果保存到当前文件，并标记冲突已解决？\r\n\r\n{conflict.RelativePath}",
            "标记冲突已解决",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, "正在标记冲突已解决...");
        try
        {
            await ResolveConflictPathCoreAsync(conflict.RelativePath);
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

    private async Task ResolveConflictPathCoreAsync(string relativePath)
    {
        var workingCopy = GetWorkingCopyRootPath();
        var result = await _svn.ResolveAsync(workingCopy, relativePath);
        OperationLogger.Log(result.ExitCode == 0 ? "ResolveConflictSuccess" : "ResolveConflictFailed", workingCopy, relativePath);
        WriteOutput(result.CombinedOutput);
        await RefreshStatusAsync();
        LoadAllFiles();
        await LoadRepositoryHistoryAsync();
    }

    private ConflictFileSet? FindConflictOrWarn(string relativePath)
    {
        var conflict = ConflictFileSet.Find(GetWorkingCopyRootPath(), relativePath);
        if (conflict == null)
        {
            MessageBox.Show("没有找到 .mine / .r版本号 冲突辅助文件。请确认选中的是 SVN 冲突文件。", "不是冲突文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return conflict;
    }

}

