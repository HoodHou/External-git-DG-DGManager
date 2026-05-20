namespace SVNManager;

internal static class TextDiffEngine
{
    public static IReadOnlyList<TextDiffRow> CompareLines(string[] oldLines, string[] newLines, DiffOptions? options = null)
    {
        var effectiveOptions = options ?? new DiffOptions();
        var oldCompareLines = NormalizeLines(oldLines, effectiveOptions);
        var newCompareLines = NormalizeLines(newLines, effectiveOptions);
        var trim = PrefixSuffixTrimmer.TrimCommonEdges(oldCompareLines, newCompareLines);
        var operations = new List<TextDiffOperation>(Math.Min(trim.PrefixLength, 4) + trim.OldMiddle.Length + trim.NewMiddle.Length + Math.Min(trim.SuffixLength, 4));

        for (var index = 0; index < trim.PrefixLength; index++)
        {
            operations.Add(TextDiffOperation.Context(index + 1, index + 1, oldLines[index]));
        }

        operations.AddRange(DiffPlexAdapter.BuildOperations(
            trim.OldMiddle,
            trim.NewMiddle,
            Slice(oldLines, trim.PrefixLength, trim.OldMiddle.Length),
            Slice(newLines, trim.PrefixLength, trim.NewMiddle.Length),
            trim.PrefixLength,
            trim.PrefixLength));

        var oldSuffixStart = oldLines.Length - trim.SuffixLength;
        var newSuffixStart = newLines.Length - trim.SuffixLength;
        for (var index = 0; index < trim.SuffixLength; index++)
        {
            operations.Add(TextDiffOperation.Context(oldSuffixStart + index + 1, newSuffixStart + index + 1, oldLines[oldSuffixStart + index]));
        }

        return TextDiffService.BuildHunks(operations);
    }

    private static string[] NormalizeLines(string[] lines, DiffOptions options)
    {
        var normalized = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (options.IgnoreWhitespace)
            {
                line = string.Concat(line.Where(ch => !char.IsWhiteSpace(ch)));
            }

            if (options.IgnoreCase)
            {
                line = line.ToUpperInvariant();
            }

            normalized[index] = line;
        }

        return normalized;
    }

    private static string[] Slice(string[] lines, int start, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var result = new string[count];
        Array.Copy(lines, start, result, 0, count);
        return result;
    }
}
