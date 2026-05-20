namespace SVNManager;

internal static class WorkbookReaderCache
{
    private const int DefaultMaxCellCount = 5_000_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Entries = new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Lru = new();
    private static int _currentCellCount;

    public static int MaxCellCount { get; set; } = DefaultMaxCellCount;

    public static Dictionary<ExcelCellKey, string> GetOrRead(
        string filePath,
        Func<string, Dictionary<ExcelCellKey, string>> reader)
    {
        var key = BuildKey(filePath);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var cached))
            {
                Lru.Remove(cached);
                Lru.AddFirst(cached);
                return Clone(cached.Value.Cells);
            }
        }

        var cells = reader(filePath);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                _currentCellCount -= existing.Value.Cells.Count;
                Lru.Remove(existing);
                Entries.Remove(key);
            }

            var copied = Clone(cells);
            var entry = new Entry(key, copied);
            var node = new LinkedListNode<Entry>(entry);
            Lru.AddFirst(node);
            Entries[key] = node;
            _currentCellCount += copied.Count;
            Trim();
        }

        return cells;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Lru.Clear();
            _currentCellCount = 0;
        }
    }

    private static void Trim()
    {
        while (_currentCellCount > MaxCellCount && Lru.Last != null)
        {
            var last = Lru.Last;
            Lru.RemoveLast();
            Entries.Remove(last.Value.Key);
            _currentCellCount -= last.Value.Cells.Count;
        }
    }

    private static string BuildKey(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        try
        {
            var info = new FileInfo(fullPath);
            return string.Join("|", fullPath, info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString());
        }
        catch
        {
            return fullPath;
        }
    }

    private static Dictionary<ExcelCellKey, string> Clone(Dictionary<ExcelCellKey, string> cells)
    {
        return new Dictionary<ExcelCellKey, string>(cells);
    }

    private sealed record Entry(string Key, Dictionary<ExcelCellKey, string> Cells);
}
