using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class AppSettings
{
    public string RepositoryUrl { get; set; } = "";
    public string WorkingCopyPath { get; set; } = "";
    public string LastCommitMessage { get; set; } = "【策划配置】";
    public string ExternalMergeToolPath { get; set; } = "";
    public string? CurrentRepositoryId { get; set; }
    public List<RepositoryEntry> Repositories { get; set; } = [];
    public List<string> IgnoredWorkingCopyPaths { get; set; } = [];
    public List<string> FavoriteFileTreePaths { get; set; } = [];
    public Dictionary<string, List<string>> ExpandedFileTreePaths { get; set; } = [];
    public UiLayoutSettings UiLayout { get; set; } = new();
    public int DiffCacheCapacity { get; set; } = 40;
    public int DiffCacheMaxMB { get; set; } = 256;
    public DiffOptions DiffOptions { get; set; } = new();

    public static AppSettings Load()
    {
        return AppSettingsStore.Load();
    }

    public void Save()
    {
        AppSettingsStore.Save(this);
    }

    public void MigrateLegacySettings()
    {
        Repositories.RemoveAll(repository => IsIgnoredWorkingCopy(repository.WorkingCopyPath));

        if (!string.IsNullOrWhiteSpace(WorkingCopyPath) &&
            !IsIgnoredWorkingCopy(WorkingCopyPath) &&
            Repositories.All(repository => !PathEquals(repository.WorkingCopyPath, WorkingCopyPath)))
        {
            var entry = RepositoryEntry.Create(RepositoryUrl, WorkingCopyPath);
            Repositories.Add(entry);
            CurrentRepositoryId ??= entry.Id;
        }
        else if (!string.IsNullOrWhiteSpace(WorkingCopyPath) && IsIgnoredWorkingCopy(WorkingCopyPath))
        {
            WorkingCopyPath = "";
            RepositoryUrl = "";
        }

        if (CurrentRepositoryId != null &&
            Repositories.All(repository => !string.Equals(repository.Id, CurrentRepositoryId, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentRepositoryId = Repositories.FirstOrDefault()?.Id;
        }

        if (CurrentRepositoryId == null && Repositories.Count > 0)
        {
            CurrentRepositoryId = Repositories[0].Id;
        }

        Save();
    }

    public void AddKnownWorkingCopyIfExists(string name, string repositoryUrl, string workingCopyPath)
    {
        if (IsIgnoredWorkingCopy(workingCopyPath) ||
            !Directory.Exists(Path.Combine(workingCopyPath, ".svn")) ||
            Repositories.Any(repository => PathEquals(repository.WorkingCopyPath, workingCopyPath)))
        {
            return;
        }

        var entry = RepositoryEntry.Create(repositoryUrl, workingCopyPath);
        entry.Name = name;
        Repositories.Add(entry);
        CurrentRepositoryId ??= entry.Id;
        Save();
    }

    public RepositoryEntry? GetCurrentRepository()
    {
        return Repositories.FirstOrDefault(repository => repository.Id == CurrentRepositoryId) ??
            Repositories.FirstOrDefault();
    }

    public void UpsertRepository(string repositoryUrl, string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        UnignoreWorkingCopy(workingCopyPath);
        var existing = Repositories.FirstOrDefault(repository => PathEquals(repository.WorkingCopyPath, workingCopyPath));
        if (existing == null)
        {
            existing = RepositoryEntry.Create(repositoryUrl, workingCopyPath);
            Repositories.Add(existing);
        }
        else
        {
            existing.RepositoryUrl = repositoryUrl;
            existing.WorkingCopyPath = workingCopyPath;
            existing.Name = RepositoryEntry.BuildName(repositoryUrl, workingCopyPath);
        }

        CurrentRepositoryId = existing.Id;
    }

    public void RemoveRepository(RepositoryEntry repository)
    {
        IgnoreWorkingCopy(repository.WorkingCopyPath);
        Repositories.RemoveAll(item =>
            string.Equals(item.Id, repository.Id, StringComparison.OrdinalIgnoreCase) ||
            PathEquals(item.WorkingCopyPath, repository.WorkingCopyPath));

        if (PathEquals(WorkingCopyPath, repository.WorkingCopyPath))
        {
            WorkingCopyPath = "";
            RepositoryUrl = "";
        }

        ExpandedFileTreePaths.Remove(NormalizeKey(repository.WorkingCopyPath));

        if (CurrentRepositoryId != null &&
            Repositories.All(item => !string.Equals(item.Id, CurrentRepositoryId, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentRepositoryId = Repositories.FirstOrDefault()?.Id;
        }

        if (Repositories.Count == 0)
        {
            CurrentRepositoryId = null;
        }
    }

    public void IgnoreWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        var key = NormalizeKey(workingCopyPath);
        if (IgnoredWorkingCopyPaths.Any(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IgnoredWorkingCopyPaths.Add(key);
    }

    public void UnignoreWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        var key = NormalizeKey(workingCopyPath);
        IgnoredWorkingCopyPaths.RemoveAll(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsIgnoredWorkingCopy(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return false;
        }

        var key = NormalizeKey(workingCopyPath);
        return IgnoredWorkingCopyPaths.Any(path => string.Equals(path, key, StringComparison.OrdinalIgnoreCase));
    }

    public HashSet<string> GetExpandedPaths(string workingCopyPath)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ExpandedFileTreePaths.TryGetValue(NormalizeKey(workingCopyPath), out var paths)
            ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetExpandedPaths(string workingCopyPath, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            return;
        }

        ExpandedFileTreePaths[NormalizeKey(workingCopyPath)] = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path)
            .ToList();
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}

internal sealed class RepositoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string WorkingCopyPath { get; set; } = "";

    public static RepositoryEntry Create(string repositoryUrl, string workingCopyPath)
    {
        return new RepositoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = BuildName(repositoryUrl, workingCopyPath),
            RepositoryUrl = repositoryUrl,
            WorkingCopyPath = workingCopyPath,
        };
    }

    public static string BuildName(string repositoryUrl, string workingCopyPath)
    {
        var folderName = Path.GetFileName(workingCopyPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            return folderName;
        }

        return string.IsNullOrWhiteSpace(repositoryUrl) ? workingCopyPath : repositoryUrl;
    }

    public override string ToString()
    {
        return $"{Name}  ({WorkingCopyPath})";
    }
}

internal sealed class UiLayoutSettings
{
    public bool LayoutLocked { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool IsMaximized { get; set; }
    public int WorkspaceSplitterDistance { get; set; } = 240;
    public int HistorySplitterDistance { get; set; } = 640;
    public int ChangedFilesSplitterDistance { get; set; } = 360;
    public string SelectedTab { get; set; } = "History";
}

