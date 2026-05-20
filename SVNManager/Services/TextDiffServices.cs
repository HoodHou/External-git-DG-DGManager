using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal static class SvnConflictArtifact
{
    public static bool IsAuxiliaryPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".mine", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string NormalizeToBasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.EndsWith(".mine", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^5];
        }

        var fileName = Path.GetFileName(path);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\.r\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? path[..^match.Value.Length] : path;
    }
}

internal static class DiffFileKindDetector
{
    public static bool IsSpreadsheet(string filePath)
    {
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(filePath);
        var extension = Path.GetExtension(comparablePath);
        if (string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var document = XDocument.Load(stream, LoadOptions.None);
            return document.Root?.Name.LocalName == "Workbook" &&
                document.Root.Name.NamespaceName == "urn:schemas-microsoft-com:office:spreadsheet";
        }
        catch
        {
            return false;
        }
    }
}

internal static class TextDiffService
{
    public static IReadOnlyList<TextDiffRow> Compare(string oldFilePath, string newFilePath)
    {
        return CreatePreview(oldFilePath, newFilePath).Differences;
    }

    public static TextDiffContent CreatePreview(string oldFilePath, string newFilePath)
    {
        var oldLines = ReadTextLines(oldFilePath);
        var newLines = ReadTextLines(newFilePath);
        var oldText = string.Join('\n', oldLines);
        var newText = string.Join('\n', newLines);
        return new TextDiffContent(
            oldText,
            newText,
            DetectLanguage(oldFilePath, newFilePath),
            "旧版本",
            "新版本",
            CompareLines(oldLines, newLines));
    }

    private static IReadOnlyList<TextDiffRow> CompareLines(string[] oldLines, string[] newLines)
    {
        return TextDiffEngine.CompareLines(oldLines, newLines);
    }

    private static List<TextDiffOperation> BuildAlignedOperations(string[] oldLines, string[] newLines)
    {
        var table = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var operations = new List<TextDiffOperation>();
        var oldLine = 0;
        var newLine = 0;
        while (oldLine < oldLines.Length && newLine < newLines.Length)
        {
            if (string.Equals(oldLines[oldLine], newLines[newLine], StringComparison.Ordinal))
            {
                operations.Add(TextDiffOperation.Context(oldLine + 1, newLine + 1, oldLines[oldLine]));
                oldLine++;
                newLine++;
            }
            else if (table[oldLine + 1, newLine] >= table[oldLine, newLine + 1])
            {
                operations.Add(TextDiffOperation.Removed(oldLine + 1, oldLines[oldLine]));
                oldLine++;
            }
            else
            {
                operations.Add(TextDiffOperation.Added(newLine + 1, newLines[newLine]));
                newLine++;
            }
        }

        while (oldLine < oldLines.Length)
        {
            operations.Add(TextDiffOperation.Removed(oldLine + 1, oldLines[oldLine]));
            oldLine++;
        }

        while (newLine < newLines.Length)
        {
            operations.Add(TextDiffOperation.Added(newLine + 1, newLines[newLine]));
            newLine++;
        }

        return operations;
    }

    private static List<TextDiffOperation> BuildPositionalOperations(string[] oldLines, string[] newLines)
    {
        var max = Math.Max(oldLines.Length, newLines.Length);
        var operations = new List<TextDiffOperation>();
        for (var index = 0; index < max; index++)
        {
            var hasOld = index < oldLines.Length;
            var hasNew = index < newLines.Length;
            var oldValue = hasOld ? oldLines[index] : "";
            var newValue = hasNew ? newLines[index] : "";
            if (hasOld && hasNew && string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                operations.Add(TextDiffOperation.Context(index + 1, index + 1, oldValue));
                continue;
            }

            if (hasOld)
            {
                operations.Add(TextDiffOperation.Removed(index + 1, oldValue));
            }

            if (hasNew)
            {
                operations.Add(TextDiffOperation.Added(index + 1, newValue));
            }
        }

        return operations;
    }

    internal static IReadOnlyList<TextDiffRow> BuildHunks(IReadOnlyList<TextDiffOperation> operations)
    {
        var rows = new List<TextDiffRow>();
        const int contextLines = 2;
        var index = 0;
        while (index < operations.Count)
        {
            while (index < operations.Count && operations[index].Kind == "Context")
            {
                index++;
            }

            if (index >= operations.Count)
            {
                break;
            }

            var hunkStart = Math.Max(0, index - contextLines);
            var hunkEnd = index;
            var trailingContext = 0;
            for (var scan = index; scan < operations.Count; scan++)
            {
                if (operations[scan].Kind == "Context")
                {
                    trailingContext++;
                    if (trailingContext > contextLines)
                    {
                        hunkEnd = scan - trailingContext;
                        break;
                    }
                }
                else
                {
                    trailingContext = 0;
                    hunkEnd = scan;
                }
            }

            hunkEnd = Math.Min(operations.Count - 1, hunkEnd + contextLines);
            var firstLine = operations[hunkStart].OldLine ?? operations[hunkStart].NewLine ?? 1;
            rows.Add(TextDiffRow.Hunk(firstLine));
            for (var rowIndex = hunkStart; rowIndex <= hunkEnd; rowIndex++)
            {
                var operation = operations[rowIndex];
                switch (operation.Kind)
                {
                    case "Context":
                        rows.Add(TextDiffRow.Context(operation.OldLine ?? operation.NewLine ?? 0, operation.Content));
                        break;
                    case "Removed":
                        rows.Add(TextDiffRow.Removed(operation.OldLine ?? 0, operation.Content));
                        break;
                    case "Added":
                        rows.Add(TextDiffRow.Added(operation.NewLine ?? 0, operation.Content));
                        break;
                }
            }

            index = hunkEnd + 1;
        }

        ApplyInlineHighlights(rows);
        return rows;
    }

    private static void ApplyInlineHighlights(List<TextDiffRow> rows)
    {
        var index = 0;
        while (index < rows.Count)
        {
            if (rows[index].Kind != "Removed")
            {
                index++;
                continue;
            }

            var removedStart = index;
            while (index < rows.Count && rows[index].Kind == "Removed")
            {
                index++;
            }

            var addedStart = index;
            while (index < rows.Count && rows[index].Kind == "Added")
            {
                index++;
            }

            foreach (var (removedIndex, addedIndex) in MatchInlinePairs(rows, removedStart, addedStart, index))
            {
                var oldValue = StripDiffPrefix(rows[removedIndex].Content);
                var newValue = StripDiffPrefix(rows[addedIndex].Content);
                var span = DiffSpan.Calculate(oldValue, newValue);
                rows[removedIndex] = rows[removedIndex] with
                {
                    HighlightStart = 2 + span.OldStart,
                    HighlightLength = span.OldLength,
                };
                rows[addedIndex] = rows[addedIndex] with
                {
                    HighlightStart = 2 + span.NewStart,
                    HighlightLength = span.NewLength,
                };
            }
        }
    }

    private static IReadOnlyList<(int RemovedIndex, int AddedIndex)> MatchInlinePairs(
        IReadOnlyList<TextDiffRow> rows,
        int removedStart,
        int addedStart,
        int blockEnd)
    {
        var removedCount = addedStart - removedStart;
        var addedCount = blockEnd - addedStart;
        if (removedCount <= 0 || addedCount <= 0)
        {
            return [];
        }

        if ((long)removedCount * addedCount > 10000)
        {
            return Enumerable.Range(0, Math.Min(removedCount, addedCount))
                .Select(offset => (removedStart + offset, addedStart + offset))
                .ToList();
        }

        var candidates = new List<(int RemovedIndex, int AddedIndex, double Score)>();
        for (var removedOffset = 0; removedOffset < removedCount; removedOffset++)
        {
            var oldValue = StripDiffPrefix(rows[removedStart + removedOffset].Content);
            for (var addedOffset = 0; addedOffset < addedCount; addedOffset++)
            {
                var newValue = StripDiffPrefix(rows[addedStart + addedOffset].Content);
                var score = TextSimilarity(oldValue, newValue);
                if (score >= 0.40)
                {
                    candidates.Add((removedStart + removedOffset, addedStart + addedOffset, score));
                }
            }
        }

        var usedRemoved = new HashSet<int>();
        var usedAdded = new HashSet<int>();
        var pairs = new List<(int RemovedIndex, int AddedIndex)>();
        foreach (var candidate in candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs((candidate.RemovedIndex - removedStart) - (candidate.AddedIndex - addedStart))))
        {
            if (!usedRemoved.Add(candidate.RemovedIndex) || !usedAdded.Add(candidate.AddedIndex))
            {
                continue;
            }

            pairs.Add((candidate.RemovedIndex, candidate.AddedIndex));
        }

        return pairs.OrderBy(pair => pair.RemovedIndex).ToList();
    }

    private static double TextSimilarity(string oldValue, string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(oldValue) || string.IsNullOrWhiteSpace(newValue))
        {
            return 0;
        }

        var oldTokens = BuildSimilarityTokens(oldValue);
        var newTokens = BuildSimilarityTokens(newValue);
        if (oldTokens.Count == 0 || newTokens.Count == 0)
        {
            return 0;
        }

        var shared = oldTokens.Intersect(newTokens, StringComparer.Ordinal).Count();
        return (double)shared / Math.Max(oldTokens.Count, newTokens.Count);
    }

    private static HashSet<string> BuildSimilarityTokens(string value)
    {
        var normalized = value.Trim();
        var words = Regex.Split(normalized, @"[^\w\u4e00-\u9fff]+")
            .Where(word => word.Length > 0)
            .Take(80)
            .ToHashSet(StringComparer.Ordinal);
        if (words.Count >= 2)
        {
            return words;
        }

        var compact = new string(normalized.Where(character => !char.IsWhiteSpace(character)).Take(256).ToArray());
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < compact.Length - 1; index++)
        {
            grams.Add(compact.Substring(index, 2));
        }

        if (compact.Length == 1)
        {
            grams.Add(compact);
        }

        return grams;
    }

    private static string StripDiffPrefix(string content)
    {
        return content.Length >= 2 && (content[0] == '-' || content[0] == '+') && content[1] == ' '
            ? content[2..]
            : content;
    }

    public static string ReadText(string filePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(filePath);
        foreach (var encoding in GetTextDecodeCandidates(bytes))
        {
            try
            {
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static IEnumerable<Encoding> GetTextDecodeCandidates(byte[] bytes)
    {
        var encodings = new List<Encoding>();
        var bomEncoding = DetectBomEncoding(bytes);
        if (bomEncoding != null)
        {
            encodings.Add(bomEncoding);
        }

        encodings.Add(new UTF8Encoding(false, true));
        encodings.Add(CreateStrictEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage));
        encodings.Add(CreateStrictEncoding("GB18030"));

        return encodings
            .Where(encoding => encoding != null)
            .Cast<Encoding>()
            .GroupBy(encoding => encoding.WebName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static Encoding? DetectBomEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE &&
            bytes[2] == 0x00 &&
            bytes[3] == 0x00)
        {
            return new UTF32Encoding(false, true, true);
        }

        if (bytes.Length >= 4 &&
            bytes[0] == 0x00 &&
            bytes[1] == 0x00 &&
            bytes[2] == 0xFE &&
            bytes[3] == 0xFF)
        {
            return new UTF32Encoding(true, true, true);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(true, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new UnicodeEncoding(false, true, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new UnicodeEncoding(true, true, true);
        }

        return null;
    }

    private static Encoding CreateStrictEncoding(int codePage)
    {
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static Encoding CreateStrictEncoding(string name)
    {
        return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static string[] ReadTextLines(string filePath)
    {
        return ReadText(filePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string DetectLanguage(string oldFilePath, string newFilePath)
    {
        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(newFilePath));
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(oldFilePath));
        }

        return extension.ToLowerInvariant() switch
        {
            ".lua" => "lua",
            ".xml" => "xml",
            ".json" => "json",
            ".cs" => "csharp",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".css" => "css",
            ".html" or ".htm" => "html",
            ".md" => "markdown",
            ".sql" => "sql",
            ".txt" => "plaintext",
            _ => "plaintext",
        };
    }
}

internal sealed record TextDiffOperation(string Kind, int? OldLine, int? NewLine, string Content)
{
    public static TextDiffOperation Context(int oldLine, int newLine, string content) => new("Context", oldLine, newLine, content);

    public static TextDiffOperation Removed(int oldLine, string content) => new("Removed", oldLine, null, content);

    public static TextDiffOperation Added(int newLine, string content) => new("Added", null, newLine, content);
}

internal sealed record TextDiffContent(
    string OldText,
    string NewText,
    string Language,
    string OldLabel,
    string NewLabel,
    IReadOnlyList<TextDiffRow> Differences);

internal sealed record TextDiffRow(string Kind, string LineNumber, string Content)
{
    public int HighlightStart { get; init; } = -1;

    public int HighlightLength { get; init; }

    public string KindText => Kind switch
    {
        "Added" => "新增",
        "Removed" => "删除",
        "Context" => "上下文",
        "Hunk" => "变更块",
        _ => Kind,
    };

    public static TextDiffRow Hunk(int lineNumber) => new("Hunk", $"@@ line {lineNumber} @@", $"变更块：约第 {lineNumber} 行");

    public static TextDiffRow Context(int lineNumber, string content) => new("Context", lineNumber.ToString(), "  " + content);

    public static TextDiffRow Removed(int lineNumber, string content) => new("Removed", lineNumber.ToString(), "- " + content);

    public static TextDiffRow Added(int lineNumber, string content) => new("Added", lineNumber.ToString(), "+ " + content);
}

internal sealed record TextSideBySideRow(TextDiffRow? OldRow, TextDiffRow? NewRow)
{
    public string OldLine => OldRow?.Kind == "Hunk" ? "" : OldRow?.LineNumber ?? "";

    public string NewLine => NewRow?.Kind == "Hunk" ? "" : NewRow?.LineNumber ?? "";

    public string OldContent => OldRow?.Content ?? "";

    public string NewContent => NewRow?.Content ?? "";

    public bool IsHunk => OldRow?.Kind == "Hunk" || NewRow?.Kind == "Hunk";
}

