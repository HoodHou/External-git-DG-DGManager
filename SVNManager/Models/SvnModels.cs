using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed record WorkingCopyInfo(
    long Revision,
    long LastChangedRevision,
    long MinRevision,
    long MaxRevision,
    string Url,
    string RepositoryRootUrl,
    string WorkingCopyRootPath)
{
    public static WorkingCopyInfo Empty { get; } = new(0, 0, 0, 0, "", "", "");

    public long CheckedOutRevision => MaxRevision > 0
        ? MaxRevision
        : Math.Max(Revision, LastChangedRevision);

    public long LastContentRevision => LastChangedRevision > 0
        ? LastChangedRevision
        : CheckedOutRevision;

    public long CurrentContentRevision => LastContentRevision;

    public bool IsMixedRevision => MinRevision > 0 && MaxRevision > 0 && MinRevision != MaxRevision;

    public string DisplayRevisionText => MinRevision > 0 && MaxRevision > 0 && MinRevision != MaxRevision
        ? $"r{MinRevision}:r{MaxRevision}（混合版本）"
        : $"r{CheckedOutRevision}";

    public string DisplayContentRevisionText => LastContentRevision > 0 && CheckedOutRevision > 0 && LastContentRevision != CheckedOutRevision
        ? $"内容 r{LastContentRevision} / 已更新到 r{CheckedOutRevision}"
        : $"内容 r{LastContentRevision}";

    public string RevisionDetailText =>
        $"内容最后变更：r{LastContentRevision}{Environment.NewLine}" +
        $"工作副本已更新到：{DisplayRevisionText}{Environment.NewLine}" +
        (string.IsNullOrWhiteSpace(WorkingCopyRootPath) ? "" : $"工作副本根目录：{WorkingCopyRootPath}{Environment.NewLine}") +
        Url;
}

internal sealed record ChangedFileEntry(string Action, string RepositoryPath, string RelativePath)
{
    public string DisplayText => string.IsNullOrWhiteSpace(RepositoryPath)
        ? $"{Action} {RelativePath}"
        : $"{Action} {RepositoryPath}";

    public string TreePath => !string.IsNullOrWhiteSpace(RelativePath)
        ? RelativePath
        : RepositoryPath.TrimStart('/');

    public static ChangedFileEntry FromWorkingCopy(SvnStatusKind status, string relativePath)
    {
        return new ChangedFileEntry(StatusAction(status), "", relativePath);
    }

    public static ChangedFileEntry ParseRepositoryPath(string line)
    {
        var trimmed = line.Trim();
        var action = trimmed.Length > 0 ? trimmed[0].ToString() : "?";
        var path = trimmed.Length > 1 ? trimmed[1..].Trim() : "";
        return FromRepositoryPath(action, path);
    }

    public static ChangedFileEntry FromRepositoryPath(string action, string repositoryPath)
    {
        return new ChangedFileEntry(action, repositoryPath, ToRelativePath(repositoryPath));
    }

    private static string ToRelativePath(string repositoryPath)
    {
        var path = repositoryPath.TrimStart('/');
        return path.StartsWith("trunk/", StringComparison.OrdinalIgnoreCase)
            ? path["trunk/".Length..]
            : path;
    }

    private static string StatusAction(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Modified => "M",
            SvnStatusKind.Added => "A",
            SvnStatusKind.Deleted => "D",
            SvnStatusKind.Unversioned => "?",
            SvnStatusKind.Missing => "!",
            SvnStatusKind.Conflicted => "C",
            SvnStatusKind.Replaced => "R",
            SvnStatusKind.Ignored => "I",
            _ => "?",
        };
    }
}

internal enum ChangedFilesFilterMode
{
    All,
    Xml,
    Lua,
    Conflict,
    Added,
    Deleted,
    Modified,
    Ignored,
}

internal static class ChangedFilesFilter
{
    public static IReadOnlyList<string> Options { get; } =
    [
        "全部",
        "只看 XML",
        "只看 Lua",
        "只看冲突",
        "只看新增",
        "只看删除",
        "只看修改",
        "只看忽略",
    ];

    public static ChangedFilesFilterMode GetMode(ComboBox combo)
    {
        return combo.SelectedIndex switch
        {
            1 => ChangedFilesFilterMode.Xml,
            2 => ChangedFilesFilterMode.Lua,
            3 => ChangedFilesFilterMode.Conflict,
            4 => ChangedFilesFilterMode.Added,
            5 => ChangedFilesFilterMode.Deleted,
            6 => ChangedFilesFilterMode.Modified,
            7 => ChangedFilesFilterMode.Ignored,
            _ => ChangedFilesFilterMode.All,
        };
    }

    public static IReadOnlyList<ChangedFileEntry> Apply(
        IEnumerable<ChangedFileEntry> files,
        string searchText,
        ChangedFilesFilterMode mode)
    {
        var keyword = (searchText ?? "").Trim();
        return files
            .Where(file => MatchesMode(file, mode))
            .Where(file => string.IsNullOrWhiteSpace(keyword) || MatchesText(file, keyword))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SvnChange> ApplyStatusChanges(
        IEnumerable<SvnChange> changes,
        string searchText,
        ChangedFilesFilterMode mode)
    {
        var keyword = (searchText ?? "").Trim();
        return changes
            .Where(change => MatchesStatusMode(change, mode))
            .Where(change => string.IsNullOrWhiteSpace(keyword) || MatchesStatusText(change, keyword))
            .OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesMode(ChangedFileEntry file, ChangedFilesFilterMode mode)
    {
        return mode switch
        {
            ChangedFilesFilterMode.Xml => HasExtension(file, ".xml"),
            ChangedFilesFilterMode.Lua => HasExtension(file, ".lua"),
            ChangedFilesFilterMode.Conflict => string.Equals(file.Action, "C", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Added => string.Equals(file.Action, "A", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Deleted => string.Equals(file.Action, "D", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Modified => string.Equals(file.Action, "M", StringComparison.OrdinalIgnoreCase),
            ChangedFilesFilterMode.Ignored => string.Equals(file.Action, "I", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static bool MatchesStatusMode(SvnChange change, ChangedFilesFilterMode mode)
    {
        return mode switch
        {
            ChangedFilesFilterMode.Xml => HasExtension(change.RelativePath, ".xml"),
            ChangedFilesFilterMode.Lua => HasExtension(change.RelativePath, ".lua"),
            ChangedFilesFilterMode.Conflict => change.Status == SvnStatusKind.Conflicted,
            ChangedFilesFilterMode.Added => change.Status is SvnStatusKind.Added or SvnStatusKind.Unversioned,
            ChangedFilesFilterMode.Deleted => change.Status is SvnStatusKind.Deleted or SvnStatusKind.Missing,
            ChangedFilesFilterMode.Modified => change.Status is SvnStatusKind.Modified or SvnStatusKind.Replaced,
            ChangedFilesFilterMode.Ignored => change.Status == SvnStatusKind.Ignored,
            _ => true,
        };
    }

    private static bool HasExtension(ChangedFileEntry file, string extension)
    {
        return HasExtension(file.TreePath, extension) ||
            HasExtension(file.RelativePath, extension) ||
            HasExtension(file.RepositoryPath, extension);
    }

    private static bool HasExtension(string path, string extension)
    {
        return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesText(ChangedFileEntry file, string keyword)
    {
        return file.DisplayText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.TreePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            file.RepositoryPath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStatusText(SvnChange change, string keyword)
    {
        return change.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            change.DisplayStatus.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            change.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HistorySearchFilter
{
    private HistorySearchFilter()
    {
    }

    public string Keyword { get; private init; } = "";
    public string FileKeyword { get; private init; } = "";
    public string Author { get; private init; } = "";
    public string IssueId { get; private init; } = "";
    public long? RevisionStart { get; private init; }
    public long? RevisionEnd { get; private init; }
    public bool HasRevisionRange => RevisionStart != null || RevisionEnd != null;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Keyword) &&
        string.IsNullOrWhiteSpace(FileKeyword) &&
        string.IsNullOrWhiteSpace(Author) &&
        string.IsNullOrWhiteSpace(IssueId) &&
        RevisionStart == null &&
        RevisionEnd == null;

    public static HistorySearchFilter Parse(string text)
    {
        var keywordParts = new List<string>();
        var file = "";
        var author = "";
        var issue = "";
        long? revisionStart = null;
        long? revisionEnd = null;

        foreach (var token in SplitTokens(text))
        {
            var parts = token.Split(':', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts[1].Trim();
                switch (key)
                {
                    case "file":
                    case "path":
                    case "文件":
                    case "路径":
                        file = value;
                        continue;
                    case "author":
                    case "作者":
                        author = value;
                        continue;
                    case "id":
                    case "需求":
                    case "需求号":
                    case "story":
                        issue = value.Trim('[', ']');
                        continue;
                    case "rev":
                    case "r":
                    case "版本":
                        ParseRevisionRange(value, out revisionStart, out revisionEnd);
                        continue;
                }
            }

            if (token.StartsWith("r", StringComparison.OrdinalIgnoreCase) && token.Length > 1 && char.IsDigit(token[1]))
            {
                ParseRevisionRange(token[1..], out revisionStart, out revisionEnd);
                continue;
            }

            keywordParts.Add(token);
        }

        return new HistorySearchFilter
        {
            Keyword = string.Join(' ', keywordParts).Trim(),
            FileKeyword = file,
            Author = author,
            IssueId = issue,
            RevisionStart = revisionStart,
            RevisionEnd = revisionEnd,
        };
    }

    public bool Matches(SvnLogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(Author) &&
            !log.Author.Contains(Author, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RevisionStart != null && log.Revision < RevisionStart.Value)
        {
            return false;
        }

        if (RevisionEnd != null && log.Revision > RevisionEnd.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(FileKeyword) &&
            !log.ChangedFiles.Any(MatchesFile))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(IssueId) &&
            !log.Message.Contains(IssueId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Keyword))
        {
            return true;
        }

        return log.Message.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.Author.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.RevisionText.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.LocalDateText.Contains(Keyword, StringComparison.OrdinalIgnoreCase) ||
            log.ChangedFiles.Any(file => file.DisplayText.Contains(Keyword, StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesFile(ChangedFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(FileKeyword))
        {
            return false;
        }

        return MatchesFileText(file, FileKeyword);
    }

    public bool MatchesFileForNavigation(ChangedFileEntry file)
    {
        if (!string.IsNullOrWhiteSpace(FileKeyword))
        {
            return MatchesFileText(file, FileKeyword);
        }

        return !string.IsNullOrWhiteSpace(Keyword) && MatchesFileText(file, Keyword);
    }

    private static bool MatchesFileText(ChangedFileEntry file, string text)
    {
        return file.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.TreePath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            file.RepositoryPath.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitTokens(string text)
    {
        return (text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void ParseRevisionRange(string value, out long? start, out long? end)
    {
        start = null;
        end = null;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            if (long.TryParse(parts[0].TrimStart('r', 'R'), out var revision))
            {
                start = revision;
                end = revision;
            }

            return;
        }

        if (long.TryParse(parts[0].TrimStart('r', 'R'), out var left))
        {
            start = left;
        }

        if (long.TryParse(parts[1].TrimStart('r', 'R'), out var right))
        {
            end = right;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }
    }
}

internal static class OperationLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNManager",
        "operation.log");

    public static void Log(string action, string workingCopy, string detail)
    {
        try
        {
            EnsureLogDirectory();
            var line = string.Join("\t",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                Environment.UserName,
                action,
                workingCopy,
                detail.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never block the SVN workflow.
        }
    }

    public static string EnsureLogFile()
    {
        EnsureLogDirectory();
        if (!File.Exists(LogPath))
        {
            File.WriteAllText(LogPath, "", Encoding.UTF8);
        }

        return LogPath;
    }

    private static void EnsureLogDirectory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }
}

internal sealed record SvnLogEntry(long Revision, string Author, DateTimeOffset Date, string Message)
{
    public bool IsUncommitted { get; init; }
    public bool IsWorkingCopyRevision { get; init; }
    public IReadOnlyList<ChangedFileEntry> ChangedFiles { get; init; } = [];

    public string GraphText => IsUncommitted ? "●" : IsWorkingCopyRevision ? "● ←" : "●";

    public string RevisionText => IsUncommitted ? "*" : Revision.ToString();

    public string DescriptionText
    {
        get
        {
            if (IsUncommitted)
            {
                return Message;
            }

            var marker = IsWorkingCopyRevision ? "[当前工作副本] " : "";
            return marker + ShortMessage;
        }
    }

    public string LocalDateText => Date == DateTimeOffset.MinValue
        ? ""
        : Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ShortMessage
    {
        get
        {
            var firstLine = Message
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
            return firstLine.Length <= 90 ? firstLine : firstLine[..90] + "...";
        }
    }
}

internal sealed record SvnChange(string RelativePath, SvnStatusKind Status)
{
    public bool CanCommit => Status is not SvnStatusKind.Conflicted and not SvnStatusKind.Missing and not SvnStatusKind.Ignored;

    public string DisplayStatus => Status switch
    {
        SvnStatusKind.Modified => "已修改",
        SvnStatusKind.Added => "已新增",
        SvnStatusKind.Deleted => "已删除",
        SvnStatusKind.Unversioned => "未加入",
        SvnStatusKind.Missing => "本地缺失",
        SvnStatusKind.Conflicted => "冲突",
        SvnStatusKind.Replaced => "已替换",
        SvnStatusKind.Ignored => "已忽略",
        _ => "未知",
    };

    public string Description => Status switch
    {
        SvnStatusKind.Unversioned => "提交时会先执行 svn add",
        SvnStatusKind.Missing => "文件在本地缺失，暂不自动提交",
        SvnStatusKind.Conflicted => "需要先解决冲突",
        SvnStatusKind.Ignored => "被 svn:ignore 忽略，不会参与提交",
        _ => "",
    };
}

internal enum SvnStatusKind
{
    None,
    Normal,
    Modified,
    Added,
    Deleted,
    Unversioned,
    Missing,
    Conflicted,
    Replaced,
    Ignored,
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

internal sealed record CommitPreflightResult(IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool Blocked => Blockers.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public string BlockMessage => Blocked
        ? string.Join(Environment.NewLine, Blockers)
        : "";

    public string FormatMessage(string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine();
        if (Blockers.Count > 0)
        {
            builder.AppendLine("必须处理：");
            foreach (var blocker in Blockers)
            {
                builder.AppendLine("- " + blocker);
            }

            builder.AppendLine();
        }

        if (Warnings.Count > 0)
        {
            builder.AppendLine("需要确认：");
            foreach (var warning in Warnings)
            {
                builder.AppendLine("- " + warning);
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string ToLogText()
    {
        return $"blockers={Blockers.Count}; warnings={Warnings.Count}; {string.Join(" | ", Blockers.Concat(Warnings))}";
    }
}

internal sealed record ConflictGridRow(string RelativePath, string Description);

internal sealed record FileTreeNodeInfo(string RelativePath, bool IsFile);

internal sealed record FileTreeLoadRequest(
    string RootPath,
    string SvnRootPath,
    string ScopeRelativePath,
    string Search,
    bool ChangedOnly,
    bool IsFiltering,
    HashSet<string> ExpandedPaths);

internal sealed record FileTreeBuildResult(
    TreeNode? RootNode,
    string Message,
    int FileCount,
    bool IsTruncated,
    bool IsLazy,
    bool IsFiltering,
    HashSet<string> ExpandedPaths,
    IReadOnlyDictionary<string, SvnStatusKind> StatusMap);

internal sealed record FileTreeFileEntry(string RelativePath, string FullPath, SvnStatusKind Status);

