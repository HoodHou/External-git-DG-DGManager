using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class SvnClient
{
    private static readonly SemaphoreSlim CommandGate = new(3, 3);
    private readonly SvnQueryCache _queryCache = new();

    public Task<ProcessResult> VersionAsync()
    {
        return RunTextAsync(null, "--version", "--quiet");
    }

    public Task<ProcessResult> CheckoutAsync(string repositoryUrl, string workingCopyPath)
    {
        return RunAndInvalidateAsync(workingCopyPath, null, "checkout", repositoryUrl, workingCopyPath);
    }

    public Task<ProcessResult> UpdateAsync(string workingCopyPath)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "update");
    }

    public async Task<ProcessResult> CleanupAsync(string workingCopyPath, CleanupOptions options)
    {
        var output = new StringBuilder();
        var exitCode = 0;

        if (options.CleanWorkingCopyStatus || options.BreakWriteLocks || options.FixTimeStamps)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals);
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.VacuumPristineCopies)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--vacuum-pristines");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.DeleteUnversioned)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--remove-unversioned");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.DeleteIgnored)
        {
            var result = await RunCleanupCommandAsync(workingCopyPath, options.IncludeExternals, "--remove-ignored");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.RevertAllRecursive)
        {
            var result = await RunAsync(workingCopyPath, "revert", "-R", ".");
            output.AppendLine(result.CombinedOutput);
            exitCode = MergeExitCode(exitCode, result.ExitCode);
        }

        if (options.RefreshShellOverlays)
        {
            output.AppendLine("刷新资源管理器图标覆盖是 TortoiseSVN 的外壳刷新项；本工具已在操作后刷新自身状态。");
        }

        if (output.Length == 0)
        {
            output.AppendLine("没有选择任何 Clean Up 操作。");
        }

        if (exitCode == 0)
        {
            _queryCache.InvalidateWorkingCopy(workingCopyPath);
        }

        return new ProcessResult(exitCode, output.ToString(), "");
    }

    private static Task<ProcessResult> RunCleanupCommandAsync(string workingCopyPath, bool includeExternals, params string[] options)
    {
        var args = new List<string> { "cleanup" };
        args.AddRange(options);
        if (includeExternals)
        {
            args.Add("--include-externals");
        }

        return RunAsync(workingCopyPath, args.ToArray());
    }

    private static int MergeExitCode(int current, int next)
    {
        return current != 0 ? current : next;
    }

    public Task<ProcessResult> UpdateToRevisionAsync(string workingCopyPath, long revision)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "update", "-r", revision.ToString());
    }

    public Task<ProcessResult> UpdatePathAsync(string workingCopyPath, string relativePath)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "update", relativePath);
    }

    public Task<ProcessResult> UpdatePathToRevisionAsync(string workingCopyPath, string relativePath, long revision)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "update", "-r", revision.ToString(), relativePath);
    }

    public Task<ProcessResult> RevertAsync(string workingCopyPath, string relativePath)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "revert", relativePath);
    }

    public Task<ProcessResult> ReverseMergeRevisionForPathAsync(string workingCopyPath, string relativePath, long revision, bool dryRun)
    {
        var args = new List<string> { "merge", "-c", "-" + revision, relativePath };
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        return RunAsync(workingCopyPath, args.ToArray());
    }

    public Task<ProcessResult> LockAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "lock", relativePath);
    }

    public Task<ProcessResult> UnlockAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "unlock", relativePath);
    }

    public Task<ProcessResult> InfoAsync(string workingCopyPath, string relativePath)
    {
        return RunAsync(workingCopyPath, "info", relativePath);
    }

    public Task<ProcessResult> AddAsync(string workingCopyPath, string relativePath)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "add", relativePath);
    }

    public Task<ProcessResult> GetIgnoreListAsync(string workingCopyPath)
    {
        return RunTextAsync(workingCopyPath, "propget", "svn:ignore", "-R", ".");
    }

    public Task<ProcessResult> GetIgnoreAsync(string workingCopyPath, string parentPath)
    {
        return RunTextAsync(workingCopyPath, "propget", "svn:ignore", parentPath);
    }

    public Task<ProcessResult> SetIgnoreAsync(string workingCopyPath, string parentPath, IEnumerable<string> names)
    {
        var value = string.Join(Environment.NewLine, names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(value)
            ? RunAndInvalidateTextAsync(workingCopyPath, "propdel", "svn:ignore", parentPath)
            : RunAndInvalidateTextAsync(workingCopyPath, "propset", "svn:ignore", value, parentPath);
    }

    public Task<ProcessResult> ResolveAsync(string workingCopyPath, string relativePath)
    {
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, "resolve", "--accept", "working", relativePath);
    }

    public Task<ProcessResult> CommitAsync(string workingCopyPath, IEnumerable<string> relativePaths, string message)
    {
        var args = new List<string> { "commit", "-m", message };
        args.AddRange(relativePaths);
        return RunAndInvalidateAsync(workingCopyPath, workingCopyPath, args.ToArray());
    }

    public async Task WriteBaseFileAsync(string workingCopyPath, string relativePath, string outputPath, CancellationToken cancellationToken = default)
    {
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", "BASE", relativePath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task WriteHeadFileAsync(string workingCopyPath, string relativePath, string outputPath, CancellationToken cancellationToken = default)
    {
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", "HEAD", relativePath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task WriteRepositoryFileAtRevisionAsync(string workingCopyPath, string repositoryPath, long revision, string outputPath, CancellationToken cancellationToken = default)
    {
        var path = repositoryPath.StartsWith("^", StringComparison.Ordinal) ? repositoryPath : "^" + repositoryPath;
        var result = await RunBinaryToFileAsync(workingCopyPath, outputPath, cancellationToken, "cat", "-r", revision.ToString(), path);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }
    }

    public async Task<IReadOnlyList<SvnChange>> GetStatusAsync(string workingCopyPath)
    {
        return await Task.Run(() => GetStatus(workingCopyPath));
    }

    public IReadOnlyList<SvnChange> GetStatus(string workingCopyPath, bool includeIgnored = false, bool includeNormal = false)
    {
        var cacheKey = SvnQueryCache.BuildKey(workingCopyPath, "status", includeIgnored, includeNormal);
        if (_queryCache.TryGet<IReadOnlyList<SvnChange>>(cacheKey, out var cached))
        {
            return cached;
        }

        var args = new List<string> { "status", "--xml" };
        if (includeIgnored)
        {
            args.Add("--no-ignore");
        }

        if (includeNormal)
        {
            args.Add("-v");
        }

        var result = RunXml(workingCopyPath, args.ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnChange>();
        }

        var changes = ParseStatusXml(result.StandardOutput, includeNormal)
            .Where(change => !SvnConflictArtifact.IsAuxiliaryPath(change.RelativePath))
            .OrderBy(change => change.RelativePath)
            .ToList();
        _queryCache.Set(cacheKey, changes);
        return changes;
    }

    public WorkingCopyInfo GetWorkingCopyInfo(string workingCopyPath)
    {
        var result = RunXml(workingCopyPath, "info", "--xml");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return WorkingCopyInfo.Empty;
        }

        var info = ParseWorkingCopyInfoXml(result.StandardOutput);
        var revision = info.Revision;
        var lastChangedRevision = info.LastChangedRevision;
        var url = info.Url;
        var maxRevision = Math.Max(revision, lastChangedRevision);
        var minRevision = revision;
        try
        {
            var versionResult = RunToolText("svnversion", null, workingCopyPath);
            if (versionResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(versionResult.StandardOutput))
            {
                var version = ParseSvnVersion(versionResult.StandardOutput);
                if (version.MaxRevision > 0)
                {
                    minRevision = version.MinRevision;
                    maxRevision = version.MaxRevision;
                }
            }
        }
        catch
        {
            // svnversion is optional; root svn info is still usable as a fallback.
        }

        return new WorkingCopyInfo(revision, lastChangedRevision, minRevision, maxRevision, url);
    }

    private static IReadOnlyList<SvnChange> ParseStatusXml(string xml, bool includeNormal)
    {
        return ReadStatusXmlEntries(xml)
            .Select(entry => new SvnChange(NormalizeStatusPath(entry.Path), MapStatus(entry.Item)))
            .Where(change => includeNormal || !string.IsNullOrWhiteSpace(change.RelativePath))
            .Where(change => change.Status != SvnStatusKind.None)
            .Where(change => includeNormal || change.Status != SvnStatusKind.Normal)
            .ToList();
    }

    private static string NormalizeStatusPath(string path)
    {
        return string.Equals(path, ".", StringComparison.Ordinal) ? "" : path;
    }

    private static SvnInfoXmlEntry ParseWorkingCopyInfoXml(string xml)
    {
        return ReadInfoXmlEntry(xml) ?? new SvnInfoXmlEntry(0, 0, "");
    }

    private static IReadOnlyList<SvnStatusXmlEntry> ReadStatusXmlEntries(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        return document
            .Descendants("entry")
            .Select(entry => new SvnStatusXmlEntry(
                entry.Attribute("path")?.Value?.Trim() ?? "",
                entry.Element("wc-status")?.Attribute("item")?.Value?.Trim() ?? ""))
            .ToList();
    }

    private static SvnInfoXmlEntry? ReadInfoXmlEntry(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var entry = document.Descendants("entry").FirstOrDefault();
        if (entry == null)
        {
            return null;
        }

        var revision = TryParseLong(entry.Attribute("revision")?.Value);
        var lastChangedRevision = TryParseLong(entry.Element("commit")?.Attribute("revision")?.Value);
        if (lastChangedRevision <= 0)
        {
            lastChangedRevision = revision;
        }

        return new SvnInfoXmlEntry(
            revision,
            lastChangedRevision,
            entry.Element("url")?.Value?.Trim() ?? "");
    }

    private static (long MinRevision, long MaxRevision) ParseSvnVersion(string text)
    {
        var revisions = System.Text.RegularExpressions.Regex.Matches(text, @"\d+")
            .Select(match => long.TryParse(match.Value, out var revision) ? revision : 0)
            .Where(revision => revision > 0)
            .ToList();
        if (revisions.Count == 0)
        {
            return (0, 0);
        }

        return (revisions.Min(), revisions.Max());
    }

    private static long TryParseLong(string? text)
    {
        return long.TryParse(text, out var value) ? value : 0;
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string workingCopyPath, string relativePath, int limit)
    {
        return await GetLogAsync(workingCopyPath, [relativePath], limit);
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string workingCopyPath, IReadOnlyList<string> relativePaths, int limit)
    {
        var targets = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return Array.Empty<SvnLogEntry>();
        }

        if (targets.Count == 1)
        {
            return await GetLogForSingleTargetAsync(workingCopyPath, targets[0], limit);
        }

        var allLogs = new List<SvnLogEntry>();
        foreach (var target in targets)
        {
            allLogs.AddRange(await GetLogForSingleTargetAsync(workingCopyPath, target, limit));
        }

        return MergeLogEntries(allLogs)
            .OrderByDescending(log => log.Revision)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetLogRangeAsync(string workingCopyPath, IReadOnlyList<string> relativePaths, long revisionStart, long revisionEnd, int limit)
    {
        var targets = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return Array.Empty<SvnLogEntry>();
        }

        if (targets.Count == 1)
        {
            return await GetLogForSingleTargetRangeAsync(workingCopyPath, targets[0], revisionStart, revisionEnd, limit);
        }

        var allLogs = new List<SvnLogEntry>();
        foreach (var target in targets)
        {
            allLogs.AddRange(await GetLogForSingleTargetRangeAsync(workingCopyPath, target, revisionStart, revisionEnd, limit));
        }

        return MergeLogEntries(allLogs)
            .OrderByDescending(log => log.Revision)
            .Take(limit)
            .ToList();
    }

    private async Task<IReadOnlyList<SvnLogEntry>> GetLogForSingleTargetAsync(string workingCopyPath, string relativePath, int limit)
    {
        var cacheKey = SvnQueryCache.BuildKey(workingCopyPath, "log", relativePath, limit);
        if (_queryCache.TryGet<IReadOnlyList<SvnLogEntry>>(cacheKey, out var cached))
        {
            return cached;
        }

        var args = new List<string> { "log", "--xml", "-v", "-r", "HEAD:1", "--limit", limit.ToString() };
        args.Add(relativePath);
        var result = await RunXmlAsync(workingCopyPath, args.ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        var logs = ParseLogXmlEntries(result.StandardOutput);
        _queryCache.Set(cacheKey, logs);
        return logs;
    }

    private async Task<IReadOnlyList<SvnLogEntry>> GetLogForSingleTargetRangeAsync(string workingCopyPath, string relativePath, long revisionStart, long revisionEnd, int limit)
    {
        var start = Math.Min(revisionStart, revisionEnd);
        var end = Math.Max(revisionStart, revisionEnd);
        var cacheKey = SvnQueryCache.BuildKey(workingCopyPath, "logRange", relativePath, start, end, limit);
        if (_queryCache.TryGet<IReadOnlyList<SvnLogEntry>>(cacheKey, out var cached))
        {
            return cached;
        }

        var args = new List<string> { "log", "--xml", "-v", "-r", $"{end}:{start}", "--limit", limit.ToString() };
        args.Add(relativePath);
        var result = await RunXmlAsync(workingCopyPath, args.ToArray());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        var logs = ParseLogXmlEntries(result.StandardOutput);
        _queryCache.Set(cacheKey, logs);
        return logs;
    }

    private static IReadOnlyList<SvnLogEntry> MergeLogEntries(IEnumerable<SvnLogEntry> logs)
    {
        return logs
            .GroupBy(log => log.Revision)
            .Select(group =>
            {
                var first = group.OrderByDescending(log => log.ChangedFiles.Count).First();
                var changedFiles = group
                    .SelectMany(log => log.ChangedFiles)
                    .GroupBy(file => $"{file.Action}|{file.RepositoryPath}|{file.RelativePath}", StringComparer.OrdinalIgnoreCase)
                    .Select(fileGroup => fileGroup.First())
                    .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return first with { ChangedFiles = changedFiles };
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetRepositoryLogAsync(string workingCopyPath, int limit)
    {
        var cacheKey = SvnQueryCache.BuildKey(workingCopyPath, "repoLog", limit);
        if (_queryCache.TryGet<IReadOnlyList<SvnLogEntry>>(cacheKey, out var cached))
        {
            return cached;
        }

        var result = await RunXmlAsync(workingCopyPath, "log", "--xml", "-v", "-r", "HEAD:1", "--limit", limit.ToString());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        var logs = ParseLogXmlEntries(result.StandardOutput);
        _queryCache.Set(cacheKey, logs);
        return logs;
    }

    public async Task<IReadOnlyList<SvnLogEntry>> GetRepositoryLogRangeAsync(string workingCopyPath, long revisionStart, long revisionEnd, int limit)
    {
        var start = Math.Min(revisionStart, revisionEnd);
        var end = Math.Max(revisionStart, revisionEnd);
        var cacheKey = SvnQueryCache.BuildKey(workingCopyPath, "repoLogRange", start, end, limit);
        if (_queryCache.TryGet<IReadOnlyList<SvnLogEntry>>(cacheKey, out var cached))
        {
            return cached;
        }

        var result = await RunXmlAsync(workingCopyPath, "log", "--xml", "-v", "-r", $"{end}:{start}", "--limit", limit.ToString());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<SvnLogEntry>();
        }

        var logs = ParseLogXmlEntries(result.StandardOutput);
        _queryCache.Set(cacheKey, logs);
        return logs;
    }

    public async Task<SvnLogEntry?> GetLatestRepositoryLogAsync(string workingCopyPath)
    {
        var logs = await GetRepositoryLogAsync(workingCopyPath, 1);
        return logs.FirstOrDefault();
    }

    private static IReadOnlyList<SvnLogEntry> ParseLogXmlEntries(string xml)
    {
        return ReadLogXmlEntries(xml)
            .Select(entry =>
            {
                var changedFiles = entry.ChangedPaths
                    .Select(path => ChangedFileEntry.FromRepositoryPath(path.Action, path.RepositoryPath))
                    .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
                    .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new SvnLogEntry(entry.Revision, entry.Author, entry.Date, entry.Message)
                {
                    ChangedFiles = changedFiles,
                };
            })
            .Where(log => log.Revision > 0)
            .ToList();
    }

    private static IReadOnlyList<SvnLogXmlEntry> ReadLogXmlEntries(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document
            .Descendants("logentry")
            .Select(entry => new SvnLogXmlEntry(
                TryParseLong(entry.Attribute("revision")?.Value),
                entry.Element("author")?.Value?.Trim() ?? "",
                ParseSvnXmlDate(entry.Element("date")?.Value),
                entry.Element("msg")?.Value ?? "",
                entry
                    .Descendants("path")
                    .Select(path => new SvnLogXmlPath(
                        path.Attribute("action")?.Value?.Trim() ?? "?",
                        path.Value.Trim()))
                    .ToList()))
            .ToList();
    }

    private static DateTimeOffset ParseSvnXmlDate(string? text)
    {
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : DateTimeOffset.MinValue;
    }

    private static SvnStatusKind MapStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "modified" => SvnStatusKind.Modified,
            "added" => SvnStatusKind.Added,
            "deleted" => SvnStatusKind.Deleted,
            "unversioned" => SvnStatusKind.Unversioned,
            "missing" => SvnStatusKind.Missing,
            "conflicted" => SvnStatusKind.Conflicted,
            "replaced" => SvnStatusKind.Replaced,
            "ignored" => SvnStatusKind.Ignored,
            "normal" => SvnStatusKind.Normal,
            _ => SvnStatusKind.None,
        };
    }

    private static async Task<ProcessResult> RunAsync(string? workingDirectory, params string[] arguments)
    {
        return await RunToolTextAsync("svn", workingDirectory, CreateSvnTextEncoding(), arguments);
    }

    private async Task<ProcessResult> RunAndInvalidateAsync(string workingCopyPath, string? workingDirectory, params string[] arguments)
    {
        var result = await RunAsync(workingDirectory, arguments);
        if (result.ExitCode == 0)
        {
            _queryCache.InvalidateWorkingCopy(workingCopyPath);
        }

        return result;
    }

    private async Task<ProcessResult> RunAndInvalidateTextAsync(string workingCopyPath, params string[] arguments)
    {
        var result = await RunTextAsync(workingCopyPath, arguments);
        if (result.ExitCode == 0)
        {
            _queryCache.InvalidateWorkingCopy(workingCopyPath);
        }

        return result;
    }

    private static Task<ProcessResult> RunXmlAsync(string? workingDirectory, params string[] arguments)
    {
        return RunToolTextAsync("svn", workingDirectory, Encoding.UTF8, arguments);
    }

    private static ProcessResult RunXml(string? workingDirectory, params string[] arguments)
    {
        return RunToolText("svn", workingDirectory, Encoding.UTF8, arguments);
    }

    private static async Task<ProcessResult> RunTextAsync(string? workingDirectory, params string[] arguments)
    {
        return await RunToolTextAsync("svn", workingDirectory, CreateSvnTextEncoding(), arguments);
    }

    private static async Task<ProcessResult> RunToolTextAsync(string fileName, string? workingDirectory, Encoding encoding, params string[] arguments)
    {
        await CommandGate.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            CommandGate.Release();
        }
    }

    private static ProcessResult RunToolText(string fileName, string? workingDirectory, params string[] arguments)
    {
        return RunToolText(fileName, workingDirectory, CreateSvnTextEncoding(), arguments);
    }

    private static ProcessResult RunToolText(string fileName, string? workingDirectory, Encoding encoding, params string[] arguments)
    {
        CommandGate.Wait();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            CommandGate.Release();
        }
    }

    private static Encoding CreateSvnTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Console.OutputEncoding ?? Encoding.UTF8;
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static async Task<ProcessResult> RunBinaryToFileAsync(string? workingDirectory, string outputPath, CancellationToken cancellationToken = default, params string[] arguments)
    {
        await CommandGate.WaitAsync(cancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "svn",
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = CreateSvnTextEncoding(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 svn 命令。");
            await using var output = File.Create(outputPath);
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await copyTask;
                var stderr = await stderrTask;
                return new ProcessResult(process.ExitCode, "", stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }
        }
        finally
        {
            CommandGate.Release();
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Canceling stale diff previews should not surface process cleanup errors.
        }
    }
}

