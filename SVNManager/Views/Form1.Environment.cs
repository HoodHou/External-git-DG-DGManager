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
    private async Task ShowEnvironmentCheckAsync()
    {
        SetBusy(true, "正在执行环境检测...");
        try
        {
            var items = await BuildEnvironmentCheckItemsAsync();
            using var form = new EnvironmentCheckForm(items);
            form.ShowDialog(this);
            WriteEnvironmentCheckSummary(items);
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

    private async Task RunStartupEnvironmentCheckAsync()
    {
        try
        {
            var items = await BuildEnvironmentCheckItemsAsync();
            WriteEnvironmentCheckSummary(items, onlyWhenHasIssues: true);
        }
        catch (Exception ex)
        {
            WriteOutput("环境检测失败：" + ex.Message);
        }
    }

    private async Task<IReadOnlyList<EnvironmentCheckItem>> BuildEnvironmentCheckItemsAsync()
    {
        var items = new List<EnvironmentCheckItem>();
        await CheckSvnCommandAsync(items);
        CheckSavedRepository(items);
        await CheckWorkingCopyAsync(items);
        CheckExternalMergeTool(items);
        CheckInstallDirectoryWritable(items);
        CheckOperationLogWritable(items);
        return items;
    }

    private async Task CheckSvnCommandAsync(List<EnvironmentCheckItem> items)
    {
        try
        {
            var result = await _svn.VersionAsync();
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                items.Add(EnvironmentCheckItem.Ok("SVN 命令", $"svn {result.StandardOutput.Trim()}", "SVN 命令行可用。"));
                return;
            }

            items.Add(EnvironmentCheckItem.Error("SVN 命令", "svn 命令执行失败", result.CombinedOutput.Trim()));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Error(
                "SVN 命令",
                "找不到 svn 命令行",
                "请安装 TortoiseSVN 时勾选 command line tools，或安装 Apache Subversion，并确认 svn.exe 在 PATH 中。" + Environment.NewLine + ex.Message));
        }
    }

    private void CheckSavedRepository(List<EnvironmentCheckItem> items)
    {
        var repository = _settings.GetCurrentRepository();
        if (repository == null)
        {
            items.Add(EnvironmentCheckItem.Warning("本地库", "还没有保存本地库", "请在配置页导入已有 SVN 工作副本，或检出一个新库。"));
            return;
        }

        items.Add(EnvironmentCheckItem.Ok("本地库", repository.Name, repository.WorkingCopyPath));
    }

    private async Task CheckWorkingCopyAsync(List<EnvironmentCheckItem> items)
    {
        var workingCopy = _configView.WorkingCopyPath.Trim();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            items.Add(EnvironmentCheckItem.Warning("工作副本", "未选择本地目录", "请选择一个包含 .svn 的工作副本目录。"));
            return;
        }

        if (!Directory.Exists(workingCopy))
        {
            items.Add(EnvironmentCheckItem.Error("工作副本", "本地目录不存在", workingCopy));
            return;
        }

        try
        {
            var selectedInfo = await Task.Run(() => _svn.GetWorkingCopyInfo(workingCopy));
            if (selectedInfo == WorkingCopyInfo.Empty)
            {
                items.Add(EnvironmentCheckItem.Error("工作副本", "不是 SVN 工作副本", "请选择 SVN 工作副本根目录或其子目录：" + workingCopy));
                return;
            }

            var rootPath = ResolveWorkingCopyRootPath(workingCopy, selectedInfo);
            var info = await Task.Run(() => _svn.GetWorkingCopyInfo(rootPath));
            var scopeRelativePath = NormalizeRelativePath(GetRelativePathOrEmpty(rootPath, workingCopy));
            var detail = info == WorkingCopyInfo.Empty
                ? workingCopy
                : $"{info.DisplayContentRevisionText}  {info.Url}" +
                  (!string.IsNullOrWhiteSpace(scopeRelativePath) ? $"{Environment.NewLine}当前范围：{scopeRelativePath}" : "") +
                  $"{Environment.NewLine}根目录：{rootPath}";
            items.Add(EnvironmentCheckItem.Ok("工作副本", "SVN 工作副本正常", detail));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Error("工作副本", "SVN 信息读取失败", ex.Message));
        }
    }

    private void CheckExternalMergeTool(List<EnvironmentCheckItem> items)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExternalMergeToolPath))
        {
            items.Add(EnvironmentCheckItem.Warning("分久必合", "未配置外部合并工具", "XML 表格仍可看内置差异，但冲突合并建议在“更多操作 -> 设置”中配置分久必合.exe。"));
            return;
        }

        if (File.Exists(_settings.ExternalMergeToolPath))
        {
            items.Add(EnvironmentCheckItem.Ok("分久必合", "外部合并工具可用", _settings.ExternalMergeToolPath));
            return;
        }

        items.Add(EnvironmentCheckItem.Error("分久必合", "配置的路径不存在", _settings.ExternalMergeToolPath));
    }

    private static void CheckInstallDirectoryWritable(List<EnvironmentCheckItem> items)
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var testPath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            items.Add(EnvironmentCheckItem.Ok("安装目录", "可写", directory));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Warning("安装目录", "当前目录不可写，自动更新可能失败", $"{directory}{Environment.NewLine}{ex.Message}"));
        }
    }

    private static void CheckOperationLogWritable(List<EnvironmentCheckItem> items)
    {
        try
        {
            var logPath = OperationLogger.EnsureLogFile();
            items.Add(EnvironmentCheckItem.Ok("操作日志", "可写", logPath));
        }
        catch (Exception ex)
        {
            items.Add(EnvironmentCheckItem.Warning("操作日志", "日志目录不可写", ex.Message));
        }
    }

    private void WriteEnvironmentCheckSummary(IReadOnlyList<EnvironmentCheckItem> items, bool onlyWhenHasIssues = false)
    {
        var errors = items.Count(item => item.Level == EnvironmentCheckLevel.Error);
        var warnings = items.Count(item => item.Level == EnvironmentCheckLevel.Warning);
        if (onlyWhenHasIssues && errors == 0 && warnings == 0)
        {
            return;
        }

        WriteOutput(errors == 0 && warnings == 0
            ? "环境检测通过。"
            : $"环境检测发现 {errors} 个错误、{warnings} 个提醒。请在“更多操作 -> 环境检测”查看详情。");
    }

    private void OpenOperationLog()
    {
        var logPath = OperationLogger.EnsureLogFile();
        Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
    }

    private void ShowRecentOperations()
    {
        try
        {
            var logPath = OperationLogger.EnsureLogFile();
            var lines = File.Exists(logPath)
                ? File.ReadLines(logPath).Reverse().Take(160).Reverse().ToList()
                : [];
            using var form = new Form
            {
                Text = "最近操作时间线",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(980, 620),
                MinimumSize = new Size(760, 420),
                Font = new Font("Microsoft YaHei UI", 9F),
            };
            form.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F),
                Text = lines.Count == 0 ? "暂无操作记录。" : string.Join(Environment.NewLine, lines),
            });
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

}

