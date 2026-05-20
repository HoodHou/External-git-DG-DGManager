namespace SVNManager;

internal static class DiffTempFileTracker
{
    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
    private static bool _registered;

    public static void Initialize()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupRegistered();
        SweepLeftovers();
    }

    public static string NewTempFile(string prefix, string extension)
    {
        Initialize();
        extension = string.IsNullOrWhiteSpace(extension)
            ? ".tmp"
            : extension.StartsWith('.') ? extension : "." + extension;
        var path = Path.Combine(Path.GetTempPath(), $"{NormalizePrefix(prefix)}_{Guid.NewGuid():N}{extension}");
        Register(path);
        return path;
    }

    public static string NewTempDirectory(string label)
    {
        Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "SVNManager", NormalizePrefix(label), DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(directory);
        Register(directory);
        return directory;
    }

    public static void Register(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SyncRoot)
        {
            Paths.Add(path);
        }
    }

    public static void CleanupRegistered()
    {
        List<string> paths;
        lock (SyncRoot)
        {
            paths = Paths.ToList();
            Paths.Clear();
        }

        foreach (var path in paths)
        {
            TryDelete(path);
        }
    }

    public static void SweepLeftovers()
    {
        var threshold = DateTime.Now.AddHours(-24);
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "SVNManager_*", SearchOption.TopDirectoryOnly))
        {
            TryDeleteIfOld(file, threshold);
        }

        var root = Path.Combine(Path.GetTempPath(), "SVNManager");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            TryDeleteIfOld(entry, threshold);
        }
    }

    private static void TryDeleteIfOld(string path, DateTime threshold)
    {
        try
        {
            var lastWrite = Directory.Exists(path)
                ? Directory.GetLastWriteTime(path)
                : File.GetLastWriteTime(path);
            if (lastWrite <= threshold)
            {
                TryDelete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        var text = string.IsNullOrWhiteSpace(prefix) ? "SVNManager" : prefix.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        return text.StartsWith("SVNManager", StringComparison.OrdinalIgnoreCase)
            ? text
            : "SVNManager_" + text;
    }
}
