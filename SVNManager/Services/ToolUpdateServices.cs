using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal enum GitUpdateState
{
    UpToDate,
    UpdateAvailable,
    RemoteUnavailable,
}

internal enum ReleaseUpdateState
{
    UpToDate,
    UpdateAvailable,
    Unavailable,
}

internal sealed record ReleaseUpdateStatus(
    ReleaseUpdateState State,
    string CurrentVersion,
    string LatestTag,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseUrl,
    string AssetName,
    string AssetDownloadUrl,
    string Message);

internal static class AppInfo
{
    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

internal sealed record GitUpdateStatus(GitUpdateState State, string LocalSha, string RemoteSha, string Message)
{
    public string LocalShortSha => ShortSha(LocalSha);
    public string RemoteShortSha => ShortSha(RemoteSha);

    private static string ShortSha(string sha)
    {
        return string.IsNullOrWhiteSpace(sha) ? "未知" : sha[..Math.Min(7, sha.Length)];
    }
}

internal static class ReleaseUpdateChecker
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/HoodHou/External-git-DG-SVNManager/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<ReleaseUpdateStatus> CheckLatestAsync(Version currentVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"GitHub Release 检查失败：HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var tag = ReadString(root, "tag_name");
            var releaseName = ReadString(root, "name");
            var notes = ReadString(root, "body");
            var url = ReadString(root, "html_url");
            var latestVersion = ParseVersion(tag);
            var asset = FindWindowsZipAsset(root);
            if (string.IsNullOrWhiteSpace(tag) || latestVersion == null)
            {
                return Unavailable("GitHub Release 没有有效版本号。");
            }

            var state = latestVersion > NormalizeVersion(currentVersion)
                ? ReleaseUpdateState.UpdateAvailable
                : ReleaseUpdateState.UpToDate;
            return new ReleaseUpdateStatus(
                state,
                AppInfo.VersionText,
                tag,
                releaseName,
                notes,
                url,
                asset.Name,
                asset.DownloadUrl,
                "");
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    public static async Task<string> DownloadAssetAsync(string assetDownloadUrl, string tag)
    {
        if (string.IsNullOrWhiteSpace(assetDownloadUrl))
        {
            throw new InvalidOperationException("GitHub Release 没有可下载的 Windows zip。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "DreamSVNManagerUpdate");
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, $"DreamSVNManager-{tag}-{Guid.NewGuid():N}.zip");
        using var response = await Http.GetAsync(assetDownloadUrl);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(zipPath);
        await input.CopyToAsync(output);
        return zipPath;
    }

    private static ReleaseUpdateStatus Unavailable(string message)
    {
        return new ReleaseUpdateStatus(
            ReleaseUpdateState.Unavailable,
            AppInfo.VersionText,
            "",
            "",
            "",
            "https://github.com/HoodHou/External-git-DG-SVNManager/releases",
            "",
            "",
            message);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DreamSVNManager/" + AppInfo.VersionText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static (string Name, string DownloadUrl) FindWindowsZipAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return ("", "");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            {
                return (name, ReadString(asset, "browser_download_url"));
            }
        }

        var firstZip = assets.EnumerateArray()
            .Select(asset => (Name: ReadString(asset, "name"), DownloadUrl: ReadString(asset, "browser_download_url")))
            .FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return firstZip;
    }

    private static Version? ParseVersion(string tag)
    {
        var normalized = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? NormalizeVersion(version) : null;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            version.Build < 0 ? 0 : version.Build);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }
}

internal static class GitUpdateChecker
{
    public static string? FindRepositoryRoot(string startPath)
    {
        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : Directory.GetParent(startPath);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static async Task<GitUpdateStatus> CheckAsync(string repositoryRoot)
    {
        var local = await RunGitAsync(repositoryRoot, "rev-parse", "HEAD");
        if (local.ExitCode != 0 || string.IsNullOrWhiteSpace(local.StandardOutput))
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, "", "", "无法读取本地 Git HEAD：" + local.CombinedOutput);
        }

        var remote = await RunGitAsync(repositoryRoot, "ls-remote", "origin", "refs/heads/main");
        if (remote.ExitCode != 0)
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, local.StandardOutput.Trim(), "", "无法连接 GitHub origin/main：" + remote.CombinedOutput);
        }

        var remoteSha = ParseLsRemoteSha(remote.StandardOutput);
        if (string.IsNullOrWhiteSpace(remoteSha))
        {
            return new GitUpdateStatus(GitUpdateState.RemoteUnavailable, local.StandardOutput.Trim(), "", "GitHub origin/main 暂无可比较版本。");
        }

        var localSha = local.StandardOutput.Trim();
        return string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase)
            ? new GitUpdateStatus(GitUpdateState.UpToDate, localSha, remoteSha, "")
            : new GitUpdateStatus(GitUpdateState.UpdateAvailable, localSha, remoteSha, "");
    }

    public static async Task<string> GetRemoteUrlAsync(string repositoryRoot)
    {
        var remote = await RunGitAsync(repositoryRoot, "remote", "get-url", "origin");
        return remote.ExitCode == 0 ? remote.StandardOutput.Trim() : "";
    }

    public static async Task<string> GetUpdateLogAsync(string repositoryRoot, int limit)
    {
        var fetch = await RunGitAsync(repositoryRoot, "fetch", "origin", "main", "--quiet");
        if (fetch.ExitCode != 0)
        {
            return "无法读取 GitHub 更新内容：" + fetch.CombinedOutput;
        }

        var log = await RunGitAsync(repositoryRoot, "log", $"--max-count={limit}", "--pretty=format:%h  %s", "HEAD..FETCH_HEAD");
        if (log.ExitCode != 0)
        {
            return "无法生成更新内容：" + log.CombinedOutput;
        }

        return string.IsNullOrWhiteSpace(log.StandardOutput)
            ? "当前没有检测到未拉取的提交。"
            : log.StandardOutput.Trim();
    }

    public static Task<ProcessResult> PullAsync(string repositoryRoot)
    {
        return RunGitAsync(repositoryRoot, "pull", "--ff-only", "origin", "main");
    }

    private static string ParseLsRemoteSha(string output)
    {
        var firstLine = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "";
        }

        return firstLine.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static async Task<ProcessResult> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 git 命令。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

