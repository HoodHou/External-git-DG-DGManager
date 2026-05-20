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
    private async Task CheckToolUpdatesAsync(bool showUpToDateMessage)
    {
        if (_checkingToolUpdate)
        {
            return;
        }

        _checkingToolUpdate = true;
        try
        {
            _lastReleaseUpdateStatus = await ReleaseUpdateChecker.CheckLatestAsync(AppInfo.Version);
            if (_lastReleaseUpdateStatus.State == ReleaseUpdateState.UpdateAvailable)
            {
                _toolUpdateStatusLabel.Text = $"工具有新版本 {_lastReleaseUpdateStatus.LatestTag}";
                _toolUpdateStatusLabel.ForeColor = Color.DarkRed;
                if (showUpToDateMessage)
                {
                    WriteOutput($"检测到工具新版本：当前 {AppInfo.VersionText}，最新 {_lastReleaseUpdateStatus.LatestTag}。点击状态栏可打开更新面板。");
                }

                return;
            }

            if (_lastReleaseUpdateStatus.State == ReleaseUpdateState.UpToDate)
            {
                _toolUpdateStatusLabel.Text = $"工具最新 {AppInfo.VersionText}";
                _toolUpdateStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                if (showUpToDateMessage)
                {
                    WriteOutput($"工具已是最新发布版：{AppInfo.VersionText}");
                }

                return;
            }

            var repositoryRoot = GitUpdateChecker.FindRepositoryRoot(AppContext.BaseDirectory);
            _lastToolRepositoryRoot = repositoryRoot;
            if (repositoryRoot == null)
            {
                _lastToolUpdateStatus = null;
                _toolUpdateStatusLabel.Text = "工具：检查失败";
                _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                if (showUpToDateMessage)
                {
                    WriteOutput("无法检查 GitHub Release：" + _lastReleaseUpdateStatus.Message);
                }

                return;
            }

            var status = await GitUpdateChecker.CheckAsync(repositoryRoot);
            _lastToolUpdateStatus = status;
            switch (status.State)
            {
                case GitUpdateState.RemoteUnavailable:
                    _toolUpdateStatusLabel.Text = "工具：远端不可用";
                    _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                    if (showUpToDateMessage)
                    {
                        WriteOutput(status.Message);
                    }

                    break;
                case GitUpdateState.UpdateAvailable:
                    _toolUpdateStatusLabel.Text = $"工具有新版本 {status.RemoteShortSha}";
                    _toolUpdateStatusLabel.ForeColor = Color.DarkRed;
                    WriteOutput($"GitHub 工具有新版本：本地 {status.LocalShortSha}，远端 {status.RemoteShortSha}。可在仓库目录执行 git pull 更新。");
                    break;
                case GitUpdateState.UpToDate:
                    _toolUpdateStatusLabel.Text = $"工具最新 {status.LocalShortSha}";
                    _toolUpdateStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                    if (showUpToDateMessage)
                    {
                        WriteOutput($"工具已是 GitHub 最新版本：{status.LocalShortSha}");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _toolUpdateStatusLabel.Text = "工具：检查失败";
            _toolUpdateStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
            if (showUpToDateMessage)
            {
                WriteOutput("工具更新检查失败：" + ex.Message);
            }
        }
        finally
        {
            _checkingToolUpdate = false;
        }
    }

    private async Task ShowToolUpdatePanelAsync()
    {
        await CheckToolUpdatesAsync(showUpToDateMessage: false);

        if (_lastReleaseUpdateStatus != null &&
            (_lastReleaseUpdateStatus.State != ReleaseUpdateState.Unavailable || string.IsNullOrWhiteSpace(_lastToolRepositoryRoot)))
        {
            using var releaseForm = ToolUpdateForm.FromRelease(_lastReleaseUpdateStatus);
            if (releaseForm.ShowDialog(this) != DialogResult.OK || !releaseForm.RunUpdateRequested)
            {
                return;
            }

            await InstallReleaseUpdateAsync(_lastReleaseUpdateStatus);
            return;
        }

        var repositoryRoot = _lastToolRepositoryRoot;
        var status = _lastToolUpdateStatus;
        var remoteUrl = "";
        var updateLog = "";
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            remoteUrl = await GitUpdateChecker.GetRemoteUrlAsync(repositoryRoot);
            updateLog = await GitUpdateChecker.GetUpdateLogAsync(repositoryRoot, 30);
        }

        using var form = ToolUpdateForm.FromGit(status, repositoryRoot, remoteUrl, updateLog);
        if (form.ShowDialog(this) != DialogResult.OK || !form.RunUpdateRequested || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        SetBusy(true, "正在执行工具更新...");
        try
        {
            OperationLogger.Log("ToolUpdateStart", repositoryRoot, remoteUrl);
            var result = await GitUpdateChecker.PullAsync(repositoryRoot);
            WriteOutput(result.CombinedOutput);
            OperationLogger.Log(result.ExitCode == 0 ? "ToolUpdateSuccess" : "ToolUpdateFailed", repositoryRoot, result.CombinedOutput);
            MessageBox.Show(
                result.ExitCode == 0
                    ? "工具更新命令已执行完成。建议关闭并重新打开程序，使用最新构建。"
                    : "工具更新命令执行失败，请查看下方输出。",
                "工具更新",
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

        await CheckToolUpdatesAsync(showUpToDateMessage: false);
    }

    private async Task InstallReleaseUpdateAsync(ReleaseUpdateStatus status)
    {
        if (status.State != ReleaseUpdateState.UpdateAvailable || string.IsNullOrWhiteSpace(status.AssetDownloadUrl))
        {
            MessageBox.Show("当前没有可安装的新版本。", "工具更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"准备下载并安装 {status.LatestTag}。{Environment.NewLine}{Environment.NewLine}" +
            "程序会在下载完成后自动关闭、覆盖当前目录并重新启动。继续？",
            "自动更新工具",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, "正在下载工具更新...");
        try
        {
            var zipPath = await ReleaseUpdateChecker.DownloadAssetAsync(status.AssetDownloadUrl, status.LatestTag);
            OperationLogger.Log("ToolReleaseDownloadSuccess", AppContext.BaseDirectory, zipPath);
            StartSelfUpdater(zipPath);
            Application.Exit();
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

    private static void StartSelfUpdater(string zipPath)
    {
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updateRoot = Path.Combine(Path.GetTempPath(), "DreamSVNManagerUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
        var extractDirectory = Path.Combine(updateRoot, "extract");
        var exePath = Path.Combine(targetDirectory, "SVNManager.exe");
        var script = $@"
$ErrorActionPreference = 'Stop'
$processId = {Environment.ProcessId}
$zipPath = {PowerShellQuote(zipPath)}
$targetDirectory = {PowerShellQuote(targetDirectory)}
$extractDirectory = {PowerShellQuote(extractDirectory)}
$exePath = {PowerShellQuote(exePath)}
try {{
    Wait-Process -Id $processId -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Test-Path -LiteralPath $extractDirectory) {{
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }}
    New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory -Force
    Copy-Item -Path (Join-Path $extractDirectory '*') -Destination $targetDirectory -Recurse -Force
    Start-Process -FilePath $exePath
}} catch {{
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '工具更新失败')
}}
";
        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
            },
        });
    }

    private static string PowerShellQuote(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private WorkingCopyInfo RefreshWorkingCopyRevisionStatus(bool showFailure = false)
    {
        var workingCopy = _configView.WorkingCopyPath.Trim();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未选择", SystemColors.ControlText, "未选择工作副本。");
            return WorkingCopyInfo.Empty;
        }

        if (!Directory.Exists(workingCopy))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：目录不存在", Color.FromArgb(166, 103, 34), workingCopy);
            return WorkingCopyInfo.Empty;
        }

        if (!Directory.Exists(Path.Combine(workingCopy, ".svn")))
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：非 SVN", Color.FromArgb(166, 103, 34), workingCopy);
            return WorkingCopyInfo.Empty;
        }

        try
        {
            var info = _svn.GetWorkingCopyInfo(workingCopy);
            if (info == WorkingCopyInfo.Empty)
            {
                SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：未知", Color.FromArgb(166, 103, 34), "svn info 没有返回可用版本信息。");
                return WorkingCopyInfo.Empty;
            }

            var text = $"本地 {info.DisplayContentRevisionText}";
            var color = info.IsMixedRevision
                ? Color.FromArgb(166, 103, 34)
                : Color.FromArgb(45, 100, 65);
            var detail = info.RevisionDetailText;
            SetWorkingCopyRevisionStatus(info, text, color, detail);
            if (showFailure)
            {
                WriteOutput(detail.Replace(Environment.NewLine, "  ", StringComparison.Ordinal));
            }

            return info;
        }
        catch (Exception ex)
        {
            SetWorkingCopyRevisionStatus(WorkingCopyInfo.Empty, "本地：读取失败", Color.FromArgb(166, 103, 34), ex.Message);
            if (showFailure)
            {
                WriteOutput("本地工作副本版本读取失败：" + ex.Message);
            }

            return WorkingCopyInfo.Empty;
        }
    }

    private void SetWorkingCopyRevisionStatus(WorkingCopyInfo info, string text, Color color, string toolTip)
    {
        _currentWorkingCopyInfo = info;
        _state.SetWorkingCopyInfo(info);
        _localRevisionStatusLabel.Text = text;
        _localRevisionStatusLabel.ForeColor = color;
        _localRevisionStatusLabel.ToolTipText = toolTip;
    }

    private async Task CheckRemoteChangesAsync(bool showUpToDateMessage)
    {
        if (_checkingRemote || !ValidateWorkingCopyPathForBackground())
        {
            return;
        }

        _checkingRemote = true;
        try
        {
            var workingCopy = _configView.WorkingCopyPath.Trim();
            var info = RefreshWorkingCopyRevisionStatus();
            var latest = await _svn.GetLatestRepositoryLogAsync(workingCopy);
            if (latest == null)
            {
                _remoteStatusLabel.Text = "远端：无历史";
                _remoteStatusLabel.ForeColor = SystemColors.ControlText;
                return;
            }

            var localRevision = info.CurrentContentRevision;
            if (localRevision <= 0)
            {
                _remoteStatusLabel.Text = $"远端 r{latest.Revision}，本地未知";
                _remoteStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
                if (showUpToDateMessage)
                {
                    WriteOutput($"已读取远端最新 r{latest.Revision}，但本地工作副本版本未知，暂时无法判断是否落后。");
                }

                _latestRemoteLog = latest;
                _state.SetLatestRemoteLog(latest);
                return;
            }

            var hasRemoteUpdates = localRevision > 0 && latest.Revision > localRevision;
            if (hasRemoteUpdates)
            {
                _remoteStatusLabel.Text = $"远端有新提交 r{latest.Revision}";
                _remoteStatusLabel.ForeColor = Color.DarkRed;
                if (_latestRemoteLog == null || latest.Revision > _latestRemoteLog.Revision)
                {
                    WriteOutput($"远端有新提交：r{latest.Revision}  {latest.Author}  {latest.ShortMessage}");
                }
            }
            else
            {
                _remoteStatusLabel.Text = $"远端最新 r{latest.Revision}";
                _remoteStatusLabel.ForeColor = Color.FromArgb(45, 100, 65);
                if (showUpToDateMessage)
                {
                    WriteOutput($"当前没有检测到需要拉取的新提交。远端最新：r{latest.Revision}");
                }
            }

            _latestRemoteLog = latest;
            _state.SetLatestRemoteLog(latest);
        }
        catch (Exception ex)
        {
            _remoteStatusLabel.Text = "远端：检查失败";
            _remoteStatusLabel.ForeColor = Color.FromArgb(166, 103, 34);
            if (showUpToDateMessage)
            {
                WriteOutput("远端检查失败：" + ex.Message);
            }
        }
        finally
        {
            _checkingRemote = false;
        }
    }

    private bool ValidateWorkingCopyPathForBackground()
    {
        var path = _configView.WorkingCopyPath.Trim();
        return Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".svn"));
    }

}

