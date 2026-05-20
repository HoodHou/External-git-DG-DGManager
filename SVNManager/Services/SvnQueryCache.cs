namespace SVNManager;

internal sealed class SvnQueryCache
{
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SvnQueryCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(30);
    }

    public bool TryGet<T>(string key, out T value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CreatedUtc <= _ttl && entry.Value is T typed)
            {
                value = typed;
                return true;
            }

            _entries.Remove(key);
        }

        value = default!;
        return false;
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _entries[key] = new Entry(DateTime.UtcNow, value);
        }
    }

    public void InvalidateWorkingCopy(string workingCopyPath)
    {
        var prefix = Normalize(workingCopyPath) + "|";
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    public static string BuildKey(string workingCopyPath, params object[] parts)
    {
        return Normalize(workingCopyPath) + "|" + string.Join("|", parts.Select(part => part?.ToString() ?? ""));
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    private sealed record Entry(DateTime CreatedUtc, object? Value);
}
