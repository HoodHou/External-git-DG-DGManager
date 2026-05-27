using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class ToolUpdateForm : Form
{
    private readonly string? _localDirectory;
    private readonly string _remoteUrl;
    public bool RunUpdateRequested { get; private set; }

    private ToolUpdateForm(
        string titleText,
        string infoText,
        string updateLog,
        string updateButtonText,
        bool updateEnabled,
        string? localDirectory,
        string remoteUrl)
    {
        _localDirectory = localDirectory;
        _remoteUrl = remoteUrl;
        Text = "工具更新";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 520;
        MinimumSize = new Size(620, 420);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = titleText,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        root.Controls.Add(title, 0, 0);

        var info = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            Text = infoText,
        };
        root.Controls.Add(info, 0, 1);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "更新内容",
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 2);

        root.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = string.IsNullOrWhiteSpace(updateLog) ? "暂无更新内容。" : updateLog,
        }, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        var closeButton = new Button { Text = "关闭", Width = 90, DialogResult = DialogResult.Cancel };
        var updateButton = new Button { Text = updateButtonText, Width = 120, Enabled = updateEnabled };
        var openLocalButton = new Button { Text = "打开本地目录", Width = 110, Enabled = !string.IsNullOrWhiteSpace(localDirectory) };
        var openGitHubButton = new Button { Text = "打开 GitHub", Width = 110, Enabled = !string.IsNullOrWhiteSpace(remoteUrl) };

        updateButton.Click += (_, _) =>
        {
            RunUpdateRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        openLocalButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_localDirectory) && Directory.Exists(_localDirectory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _localDirectory) { UseShellExecute = true });
            }
        };
        openGitHubButton.Click += (_, _) =>
        {
            var url = NormalizeRemoteUrl(_remoteUrl);
            if (!string.IsNullOrWhiteSpace(url))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        };

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(updateButton);
        buttons.Controls.Add(openLocalButton);
        buttons.Controls.Add(openGitHubButton);
        root.Controls.Add(buttons, 0, 4);

        AcceptButton = updateButton;
        CancelButton = closeButton;
        Controls.Add(root);
    }

    public static ToolUpdateForm FromRelease(ReleaseUpdateStatus status)
    {
        var title = status.State switch
        {
            ReleaseUpdateState.UpdateAvailable => "检测到工具新版本",
            ReleaseUpdateState.UpToDate => "工具已是最新版本",
            ReleaseUpdateState.Unavailable => "无法检查工具更新",
            _ => "工具更新状态未知",
        };
        var info =
            $"当前版本：{status.CurrentVersion}{Environment.NewLine}" +
            $"最新版本：{(string.IsNullOrWhiteSpace(status.LatestTag) ? "未知" : status.LatestTag)}{Environment.NewLine}" +
            $"更新通道：{status.Channel} / {status.Source}{Environment.NewLine}" +
            $"强制更新：{(status.Required ? "是" : "否")}{Environment.NewLine}" +
            $"下载文件：{(string.IsNullOrWhiteSpace(status.AssetName) ? "未找到" : status.AssetName)}{Environment.NewLine}" +
            $"SHA256：{(string.IsNullOrWhiteSpace(status.Sha256) ? "未提供" : status.Sha256)}{Environment.NewLine}" +
            $"安装目录：{AppContext.BaseDirectory}";
        var notes = status.State == ReleaseUpdateState.Unavailable
            ? status.Message
            : string.IsNullOrWhiteSpace(status.ReleaseNotes) ? "这个版本没有填写更新说明。" : status.ReleaseNotes;
        return new ToolUpdateForm(
            title,
            info,
            notes,
            "下载并更新",
            status.State == ReleaseUpdateState.UpdateAvailable && !string.IsNullOrWhiteSpace(status.AssetDownloadUrl),
            AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(status.ReleaseUrl) ? "https://github.com/HoodHou/External-git-DG-SVNManager/releases" : status.ReleaseUrl);
    }

    public static ToolUpdateForm FromGit(GitUpdateStatus? status, string? repositoryRoot, string remoteUrl, string updateLog)
    {
        return new ToolUpdateForm(
            BuildGitTitle(status, repositoryRoot),
            BuildGitInfo(status, repositoryRoot, remoteUrl),
            string.IsNullOrWhiteSpace(updateLog) ? "暂无更新内容。" : updateLog,
            "执行更新命令",
            !string.IsNullOrWhiteSpace(repositoryRoot),
            repositoryRoot,
            remoteUrl);
    }

    private static string BuildGitTitle(GitUpdateStatus? status, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return "当前程序没有找到 Git 仓库信息";
        }

        return status?.State switch
        {
            GitUpdateState.UpdateAvailable => "检测到工具新版本",
            GitUpdateState.UpToDate => "工具已是最新版本",
            GitUpdateState.RemoteUnavailable => "无法连接 GitHub 远端",
            _ => "工具更新状态未知",
        };
    }

    private static string BuildGitInfo(GitUpdateStatus? status, string? repositoryRoot, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return "当前运行目录向上没有找到 .git，无法比较 GitHub 版本。";
        }

        return
            $"本地目录：{repositoryRoot}{Environment.NewLine}" +
            $"GitHub：{remoteUrl}{Environment.NewLine}" +
            $"当前版本：{status?.LocalShortSha ?? "未知"}{Environment.NewLine}" +
            $"GitHub 最新：{status?.RemoteShortSha ?? "未知"}";
    }

    private static string NormalizeRemoteUrl(string remoteUrl)
    {
        if (remoteUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var path = remoteUrl["git@github.com:".Length..];
            return "https://github.com/" + (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path);
        }

        return remoteUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? remoteUrl[..^4]
            : remoteUrl;
    }
}

