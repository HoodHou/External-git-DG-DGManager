using System.Text;

namespace SVNManager;

internal sealed record SpreadsheetDiffReport(IReadOnlyList<SpreadsheetDiffRow> Rows)
{
    public IReadOnlyList<SpreadsheetColumnChange> ColumnChanges { get; init; } = [];
    public int ChangedRowCount => Rows.Count;
    public int ModifiedRowCount => Rows.Count(row => row.ChangeKind == SpreadsheetDiffChangeKind.Modified);
    public int AddedRowCount => Rows.Count(row => row.ChangeKind == SpreadsheetDiffChangeKind.Added);
    public int DeletedRowCount => Rows.Count(row => row.ChangeKind == SpreadsheetDiffChangeKind.Deleted);
    public int WeakAlignedRowCount => Rows.Count(row => row.AlignmentKind == SpreadsheetDiffAlignmentKind.Weak);
    public int ChangedCellCount => Rows.Sum(row => row.ChangedCells.Count);
    public int RenamedColumnCount => ColumnChanges.Count(change => change.Kind == SpreadsheetColumnChangeKind.Renamed);
    public int AddedColumnCount => ColumnChanges.Count(change => change.Kind == SpreadsheetColumnChangeKind.Added);
    public int DeletedColumnCount => ColumnChanges.Count(change => change.Kind == SpreadsheetColumnChangeKind.Deleted);
    public string ColumnChangesSummary
    {
        get
        {
            if (ColumnChanges.Count == 0)
            {
                return "";
            }

            var samples = ColumnChanges
                .Take(3)
                .Select(change => change.Kind switch
                {
                    SpreadsheetColumnChangeKind.Renamed => $"{change.OldFieldName}→{change.NewFieldName}",
                    SpreadsheetColumnChangeKind.Added => $"+{change.NewFieldName}",
                    SpreadsheetColumnChangeKind.Deleted => $"-{change.OldFieldName}",
                    _ => "",
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            return string.Join("，", samples) + (ColumnChanges.Count > samples.Count ? $" 等 {ColumnChanges.Count} 项" : "");
        }
    }

    public string Summary => ChangedRowCount == 0
        ? ColumnChanges.Count == 0
            ? "没有发现表格差异"
            : $"发现 {ColumnChanges.Count} 个列结构变化：{ColumnChangesSummary}"
        : $"发现 {ChangedRowCount} 行 / {ChangedCellCount} 个字段差异" +
            (ColumnChanges.Count == 0 ? "" : $"，列变化 {ColumnChanges.Count}（{ColumnChangesSummary}）");

    public IReadOnlyList<ExcelCellDifference> ToLegacyDifferences()
    {
        var result = new List<ExcelCellDifference>();
        foreach (var row in Rows)
        {
            foreach (var cell in row.ChangedCells)
            {
                var source = row.NewRow > 0 ? row.NewRow : row.OldRow;
                var column = cell.ColumnIndex + 1;
                result.Add(new ExcelCellDifference(
                    row.Sheet,
                    source > 0 ? source + 1 : 0,
                    column,
                    ExcelDiffService.ToColumnName(cell.ColumnIndex),
                    cell.FieldName,
                    row.DisplayKey,
                    cell.OldValue,
                    cell.NewValue,
                    row.OldRowText,
                    row.NewRowText));
            }
        }

        return result;
    }

    public static SpreadsheetDiffReport FromLegacy(IReadOnlyList<ExcelCellDifference> differences)
    {
        var rows = differences
            .GroupBy(diff => $"{diff.Sheet}\u001f{diff.RowId}\u001f{diff.Row}")
            .Select(group =>
            {
                var first = group.First();
                var cells = group
                    .Select(diff => new SpreadsheetDiffCell(
                        diff.FieldName,
                        diff.Column - 1,
                        diff.ColumnName,
                        diff.OldValue,
                        diff.NewValue,
                        SpreadsheetDiffCellKindLabels.FromValues(diff.OldValue, diff.NewValue),
                        SpreadsheetStructuredValueDiff.Create(diff.OldValue, diff.NewValue)))
                    .ToList();
                return new SpreadsheetDiffRow(
                    first.Sheet,
                    string.IsNullOrWhiteSpace(first.RowId) ? $"第 {first.Row} 行" : first.RowId,
                    $"P|{first.Sheet}|{first.Row}",
                    first.Row - 1,
                    first.Row - 1,
                    SpreadsheetDiffCellKindLabels.FromCells(cells),
                    SpreadsheetDiffAlignmentKind.Physical,
                    BuildRowText(cells, oldSide: true),
                    BuildRowText(cells, oldSide: false),
                    cells);
            })
            .ToList();
        return new SpreadsheetDiffReport(rows);
    }

    private static string BuildRowText(IReadOnlyList<SpreadsheetDiffCell> cells, bool oldSide)
    {
        var builder = new StringBuilder();
        foreach (var cell in cells)
        {
            var value = oldSide ? cell.OldValue : cell.NewValue;
            builder.AppendLine($"{cell.FieldName} = {(string.IsNullOrEmpty(value) ? "(空)" : value)}");
        }

        return builder.ToString().TrimEnd();
    }
}

internal sealed record SpreadsheetColumnChange(
    string Sheet,
    SpreadsheetColumnChangeKind Kind,
    string OldFieldName,
    string NewFieldName,
    int OldColumn,
    int NewColumn);

internal enum SpreadsheetColumnChangeKind
{
    Renamed,
    Added,
    Deleted,
}

internal sealed record SpreadsheetDiffRow(
    string Sheet,
    string DisplayKey,
    string RowMergeKey,
    int OldRow,
    int NewRow,
    SpreadsheetDiffChangeKind ChangeKind,
    SpreadsheetDiffAlignmentKind AlignmentKind,
    string OldRowText,
    string NewRowText,
    IReadOnlyList<SpreadsheetDiffCell> Cells)
{
    public IReadOnlyList<SpreadsheetDiffCell> ChangedCells => Cells.Where(cell => cell.Kind != SpreadsheetDiffCellKind.Unchanged).ToList();
    public string OldAddress => OldRow >= 0 ? $"行 {OldRow + 1}" : "";
    public string NewAddress => NewRow >= 0 ? $"行 {NewRow + 1}" : "";
    public string AddressText => OldAddress == NewAddress ? OldAddress : $"{OldAddress} -> {NewAddress}".Trim(' ', '-', '>');
    public string CompactDisplayKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayKey))
            {
                return RowMergeKey;
            }

            var slashIndex = DisplayKey.IndexOf('/');
            return slashIndex > 0 ? DisplayKey[..slashIndex] : DisplayKey;
        }
    }

    public string ChangedFieldsSummary
    {
        get
        {
            var changed = ChangedCells;
            if (changed.Count == 0)
            {
                return "无字段差异";
            }

            var names = changed
                .Take(5)
                .Select(cell => $"{cell.FieldName}{SpreadsheetDiffCellKindLabels.Short(cell.Kind)}")
                .ToList();
            return string.Join("，", names) + (changed.Count > 5 ? $" 等 {changed.Count} 项" : "");
        }
    }

    public string CardSummary
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(CompactDisplayKey) ? "(无 ID)" : CompactDisplayKey;
            var firstLine = $"{key} · {AddressText} · {SpreadsheetDiffAlignmentKindLabels.Text(AlignmentKind)} · {SpreadsheetDiffChangeKindLabels.Text(ChangeKind)}";
            var secondLine = ChangedFieldsSummary;
            return string.IsNullOrWhiteSpace(Sheet) ? $"{firstLine}{Environment.NewLine}{secondLine}" : $"{firstLine} · {Sheet}{Environment.NewLine}{secondLine}";
        }
    }
}

internal sealed record SpreadsheetDiffCell(
    string FieldName,
    int ColumnIndex,
    string ColumnName,
    string OldValue,
    string NewValue,
    SpreadsheetDiffCellKind Kind,
    SpreadsheetStructuredValueDiff StructuredDiff)
{
    public DiffHighlightSpans Highlights => DiffHighlightSpans.Calculate(OldValue, NewValue);
    public bool IsStructured => StructuredDiff.HasSegments;
}

internal sealed record SpreadsheetStructuredValueDiff(IReadOnlyList<SpreadsheetStructuredValueSegment> Segments)
{
    public bool HasSegments => Segments.Count > 0;

    public static SpreadsheetStructuredValueDiff Create(string oldValue, string newValue)
    {
        var oldSegments = ParseSegments(oldValue);
        var newSegments = ParseSegments(newValue);
        if (oldSegments.Count < 2 && newSegments.Count < 2)
        {
            return new SpreadsheetStructuredValueDiff([]);
        }

        var keys = oldSegments.Keys
            .Union(newSegments.Keys, StringComparer.Ordinal)
            .OrderBy(key => FirstIndex(key, oldSegments, newSegments))
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();
        var segments = new List<SpreadsheetStructuredValueSegment>();
        foreach (var key in keys)
        {
            oldSegments.TryGetValue(key, out var oldSegment);
            newSegments.TryGetValue(key, out var newSegment);
            var oldText = oldSegment?.Text ?? "";
            var newText = newSegment?.Text ?? "";
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                continue;
            }

            var kind = string.IsNullOrEmpty(oldText)
                ? SpreadsheetDiffCellKind.Added
                : string.IsNullOrEmpty(newText) ? SpreadsheetDiffCellKind.Deleted : SpreadsheetDiffCellKind.Modified;
            segments.Add(new SpreadsheetStructuredValueSegment(key, oldText, newText, kind));
        }

        return new SpreadsheetStructuredValueDiff(segments);
    }

    private static int FirstIndex(
        string key,
        IReadOnlyDictionary<string, StructuredToken> oldSegments,
        IReadOnlyDictionary<string, StructuredToken> newSegments)
    {
        var oldIndex = oldSegments.TryGetValue(key, out var oldSegment) ? oldSegment.Index : int.MaxValue;
        var newIndex = newSegments.TryGetValue(key, out var newSegment) ? newSegment.Index : int.MaxValue;
        return Math.Min(oldIndex, newIndex);
    }

    private static Dictionary<string, StructuredToken> ParseSegments(string value)
    {
        value ??= "";
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        char[] separators = normalized.Contains('\n')
            ? ['\n']
            : normalized.Contains('|', StringComparison.Ordinal)
                ? ['|']
                : normalized.Contains(';', StringComparison.Ordinal) ? [';'] : [','];
        var parts = normalized
            .Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (parts.Count < 2)
        {
            return [];
        }

        var result = new Dictionary<string, StructuredToken>(StringComparer.Ordinal);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var key = BuildSegmentKey(part, index);
            if (!result.ContainsKey(key))
            {
                result[key] = new StructuredToken(index, part);
            }
        }

        return result;
    }

    private static string BuildSegmentKey(string part, int index)
    {
        var equals = part.IndexOf('=');
        if (equals > 0)
        {
            var key = part[..equals].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                return $"K|{key}";
            }
        }

        var compact = new string(part.Where(character => !char.IsWhiteSpace(character)).Take(24).ToArray());
        return string.IsNullOrWhiteSpace(compact) ? $"I|{index}" : $"V|{compact}";
    }
}

internal sealed record SpreadsheetStructuredValueSegment(string Key, string OldText, string NewText, SpreadsheetDiffCellKind Kind);

internal sealed record StructuredToken(int Index, string Text);

internal enum SpreadsheetDiffChangeKind
{
    Modified,
    Added,
    Deleted,
}

internal static class SpreadsheetDiffChangeKindLabels
{
    public static string Text(SpreadsheetDiffChangeKind kind)
    {
        return kind switch
        {
            SpreadsheetDiffChangeKind.Added => "新增行",
            SpreadsheetDiffChangeKind.Deleted => "删除行",
            _ => "修改",
        };
    }
}

internal enum SpreadsheetDiffAlignmentKind
{
    Semantic,
    Weak,
    Physical,
}

internal static class SpreadsheetDiffAlignmentKindLabels
{
    public static string Text(SpreadsheetDiffAlignmentKind kind)
    {
        return kind switch
        {
            SpreadsheetDiffAlignmentKind.Semantic => "ID 对齐",
            SpreadsheetDiffAlignmentKind.Weak => "弱对齐",
            _ => "物理行",
        };
    }
}

internal enum SpreadsheetDiffCellKind
{
    Unchanged,
    Modified,
    Added,
    Deleted,
}

internal static class SpreadsheetDiffCellKindLabels
{
    public static SpreadsheetDiffCellKind FromValues(string oldValue, string newValue)
    {
        oldValue ??= "";
        newValue ??= "";
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return SpreadsheetDiffCellKind.Unchanged;
        }

        return string.IsNullOrEmpty(oldValue)
            ? SpreadsheetDiffCellKind.Added
            : string.IsNullOrEmpty(newValue) ? SpreadsheetDiffCellKind.Deleted : SpreadsheetDiffCellKind.Modified;
    }

    public static SpreadsheetDiffChangeKind FromCells(IReadOnlyList<SpreadsheetDiffCell> cells)
    {
        var changed = cells.Where(cell => cell.Kind != SpreadsheetDiffCellKind.Unchanged).ToList();
        if (changed.Count > 0 && changed.All(cell => cell.Kind == SpreadsheetDiffCellKind.Added))
        {
            return SpreadsheetDiffChangeKind.Added;
        }

        if (changed.Count > 0 && changed.All(cell => cell.Kind == SpreadsheetDiffCellKind.Deleted))
        {
            return SpreadsheetDiffChangeKind.Deleted;
        }

        return SpreadsheetDiffChangeKind.Modified;
    }

    public static string Short(SpreadsheetDiffCellKind kind)
    {
        return kind switch
        {
            SpreadsheetDiffCellKind.Added => "+",
            SpreadsheetDiffCellKind.Deleted => "-",
            SpreadsheetDiffCellKind.Modified => "*",
            _ => "",
        };
    }
}
