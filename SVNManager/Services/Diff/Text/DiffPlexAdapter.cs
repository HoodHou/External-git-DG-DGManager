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
        var operations = new List<TextDiffOperation>();
        if (oldLines.Length == 0 && newLines.Length == 0)
        {
            return operations;
        }

        var oldText = string.Join('\n', oldLines);
        var newText = string.Join('\n', newLines);
        var result = new Differ().CreateLineDiffs(oldText, newText, false, false);
        var oldCursor = 0;
        var newCursor = 0;

        foreach (var block in result.DiffBlocks)
        {
            while (oldCursor < block.DeleteStartA && newCursor < block.InsertStartB)
            {
                operations.Add(TextDiffOperation.Context(oldLineOffset + oldCursor + 1, newLineOffset + newCursor + 1, oldLines[oldCursor]));
                oldCursor++;
                newCursor++;
            }

            for (var index = 0; index < block.DeleteCountA; index++)
            {
                var lineIndex = block.DeleteStartA + index;
                if (lineIndex >= 0 && lineIndex < oldLines.Length)
                {
                    operations.Add(TextDiffOperation.Removed(oldLineOffset + lineIndex + 1, oldLines[lineIndex]));
                }
            }

            for (var index = 0; index < block.InsertCountB; index++)
            {
                var lineIndex = block.InsertStartB + index;
                if (lineIndex >= 0 && lineIndex < newLines.Length)
                {
                    operations.Add(TextDiffOperation.Added(newLineOffset + lineIndex + 1, newLines[lineIndex]));
                }
            }

            oldCursor = block.DeleteStartA + block.DeleteCountA;
            newCursor = block.InsertStartB + block.InsertCountB;
        }

        while (oldCursor < oldLines.Length && newCursor < newLines.Length)
        {
            operations.Add(TextDiffOperation.Context(oldLineOffset + oldCursor + 1, newLineOffset + newCursor + 1, oldLines[oldCursor]));
            oldCursor++;
            newCursor++;
        }

        while (oldCursor < oldLines.Length)
        {
            operations.Add(TextDiffOperation.Removed(oldLineOffset + oldCursor + 1, oldLines[oldCursor]));
            oldCursor++;
        }

        while (newCursor < newLines.Length)
        {
            operations.Add(TextDiffOperation.Added(newLineOffset + newCursor + 1, newLines[newCursor]));
            newCursor++;
        }

        return operations;
    }
}
