namespace SVNManager;

internal sealed class DiffPreviewCache
{
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    public DiffPreviewCache(int capacity, long maxBytes = 256L * 1024 * 1024)
    {
        Capacity = Math.Max(1, capacity);
        MaxBytes = Math.Max(1024 * 1024, maxBytes);
    }

    public int Capacity { get; set; }
    public long MaxBytes { get; set; }

    public int Count => _entries.Count;
    public long CurrentBytes { get; private set; }

    public void Clear()
    {
        _entries.Clear();
        _lru.Clear();
        CurrentBytes = 0;
    }

    public bool TryGet(string key, out DiffPreviewData data)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            data = node.Value.Data;
            return true;
        }

        data = null!;
        return false;
    }

    public void Set(string key, DiffPreviewData data)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_entries.TryGetValue(key, out var existing))
        {
            CurrentBytes -= existing.Value.EstimatedBytes;
            existing.Value = existing.Value with { Data = data, EstimatedBytes = EstimateBytes(data) };
            CurrentBytes += existing.Value.EstimatedBytes;
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            Trim();
            return;
        }

        var entry = new CacheEntry(key, data, EstimateBytes(data));
        var node = new LinkedListNode<CacheEntry>(entry);
        _lru.AddFirst(node);
        _entries[key] = node;
        CurrentBytes += entry.EstimatedBytes;
        Trim();
    }

    private void Trim()
    {
        while ((_entries.Count > Capacity || CurrentBytes > MaxBytes) && _lru.Last != null)
        {
            var last = _lru.Last;
            _lru.RemoveLast();
            _entries.Remove(last.Value.Key);
            CurrentBytes -= last.Value.EstimatedBytes;
        }
    }

    private static long EstimateBytes(DiffPreviewData data)
    {
        return Math.Max(1024, data.EstimatedBytes);
    }

    private sealed record CacheEntry(string Key, DiffPreviewData Data, long EstimatedBytes);
}
