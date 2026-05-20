namespace SVNManager;

internal readonly record struct LineTrimResult(int PrefixLength, int SuffixLength, string[] OldMiddle, string[] NewMiddle);

internal static class PrefixSuffixTrimmer
{
    public static LineTrimResult TrimCommonEdges(string[] oldLines, string[] newLines)
    {
        var oldHashes = PrecomputeHashes(oldLines);
        var newHashes = PrecomputeHashes(newLines);
        var prefix = 0;
        while (prefix < oldLines.Length &&
            prefix < newLines.Length &&
            oldHashes[prefix] == newHashes[prefix] &&
            string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var oldTail = oldLines.Length - 1;
        var newTail = newLines.Length - 1;
        var suffix = 0;
        while (oldTail >= prefix &&
            newTail >= prefix &&
            oldHashes[oldTail] == newHashes[newTail] &&
            string.Equals(oldLines[oldTail], newLines[newTail], StringComparison.Ordinal))
        {
            suffix++;
            oldTail--;
            newTail--;
        }

        var oldMiddleLength = Math.Max(0, oldLines.Length - prefix - suffix);
        var newMiddleLength = Math.Max(0, newLines.Length - prefix - suffix);
        return new LineTrimResult(
            prefix,
            suffix,
            oldLines.Skip(prefix).Take(oldMiddleLength).ToArray(),
            newLines.Skip(prefix).Take(newMiddleLength).ToArray());
    }

    private static int[] PrecomputeHashes(string[] lines)
    {
        var hashes = new int[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            hashes[index] = string.GetHashCode(lines[index] ?? "", StringComparison.Ordinal);
        }

        return hashes;
    }
}
