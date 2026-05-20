using DiffPlex;

namespace SVNManager;

internal static class DiffPlexAdapter
{
    public static List<TextDiffOperation> BuildOperations(
        string[] oldLines,
        string[] newLines,
        int oldLineOffset,
        int newLineOffset)
    {
        return BuildOperations(oldLines, newLines, oldLines, newLines, oldLineOffset, newLineOffset);
    }

    public static List<TextDiffOperation> BuildOperations(
        string[] oldCompareLines,
        string[] newCompareLines,
        string[] oldDisplayLines,
        string[] newDisplayLines,
        int oldLineOffset,
        int newLineOffset)
    {
        var operations = new List<TextDiffOperation>();
        if (oldCompareLines.Length == 0 && newCompareLines.Length == 0)
        {
            return operations;
        }

        var oldText = string.Join('\n', oldCompareLines);
        var newText = string.Join('\n', newCompareLines);
        var result = new Differ().CreateLineDiffs(oldText, newText, false, false);
        var oldCursor = 0;
        var newCursor = 0;

        foreach (var block in result.DiffBlocks)
        {
            while (oldCursor < block.DeleteStartA && newCursor < block.InsertStartB)
            {
                operations.Add(TextDiffOperation.Context(oldLineOffset + oldCursor + 1, newLineOffset + newCursor + 1, oldDisplayLines[oldCursor]));
                oldCursor++;
                newCursor++;
            }

            for (var index = 0; index < block.DeleteCountA; index++)
            {
                var lineIndex = block.DeleteStartA + index;
                if (lineIndex >= 0 && lineIndex < oldDisplayLines.Length)
                {
                    operations.Add(TextDiffOperation.Removed(oldLineOffset + lineIndex + 1, oldDisplayLines[lineIndex]));
                }
            }

            for (var index = 0; index < block.InsertCountB; index++)
            {
                var lineIndex = block.InsertStartB + index;
                if (lineIndex >= 0 && lineIndex < newDisplayLines.Length)
                {
                    operations.Add(TextDiffOperation.Added(newLineOffset + lineIndex + 1, newDisplayLines[lineIndex]));
                }
            }

            oldCursor = block.DeleteStartA + block.DeleteCountA;
            newCursor = block.InsertStartB + block.InsertCountB;
        }

        while (oldCursor < oldDisplayLines.Length && newCursor < newDisplayLines.Length)
        {
            operations.Add(TextDiffOperation.Context(oldLineOffset + oldCursor + 1, newLineOffset + newCursor + 1, oldDisplayLines[oldCursor]));
            oldCursor++;
            newCursor++;
        }

        while (oldCursor < oldDisplayLines.Length)
        {
            operations.Add(TextDiffOperation.Removed(oldLineOffset + oldCursor + 1, oldDisplayLines[oldCursor]));
            oldCursor++;
        }

        while (newCursor < newDisplayLines.Length)
        {
            operations.Add(TextDiffOperation.Added(newLineOffset + newCursor + 1, newDisplayLines[newCursor]));
            newCursor++;
        }

        return operations;
    }
}
