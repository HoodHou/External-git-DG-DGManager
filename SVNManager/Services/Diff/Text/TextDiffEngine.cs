namespace SVNManager;

internal static class TextDiffEngine
{
    public static IReadOnlyList<TextDiffRow> CompareLines(string[] oldLines, string[] newLines)
    {
        var trim = PrefixSuffixTrimmer.TrimCommonEdges(oldLines, newLines);
        var operations = new List<TextDiffOperation>(Math.Min(trim.PrefixLength, 4) + trim.OldMiddle.Length + trim.NewMiddle.Length + Math.Min(trim.SuffixLength, 4));

        for (var index = 0; index < trim.PrefixLength; index++)
        {
            operations.Add(TextDiffOperation.Context(index + 1, index + 1, oldLines[index]));
        }

        operations.AddRange(DiffPlexAdapter.BuildOperations(
            trim.OldMiddle,
            trim.NewMiddle,
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
}
