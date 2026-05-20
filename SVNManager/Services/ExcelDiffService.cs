using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal static class ExcelDiffService
{
    public static SpreadsheetDiffReport Compare(string oldFilePath, string newFilePath)
    {
        return Compare(ReadCellValues(oldFilePath), ReadCellValues(newFilePath));
    }

    public static IReadOnlyList<ExcelCellDifference> CompareCells(string oldFilePath, string newFilePath)
    {
        return Compare(oldFilePath, newFilePath).ToLegacyDifferences();
    }

    public static SpreadsheetDiffReport Compare(Dictionary<ExcelCellKey, string> oldCells, Dictionary<ExcelCellKey, string> newCells)
    {
        var oldRawCells = SpreadsheetThreeWayMergeService.ReadRawCells(oldCells);
        var newRawCells = SpreadsheetThreeWayMergeService.ReadRawCells(newCells);
        var oldRows = BuildRows(oldRawCells);
        var newRows = BuildRows(newRawCells);
        var pairs = AlignRows(oldRows, newRows);
        var rows = pairs
            .Select(pair => CreateDiffRow(pair.OldRow, pair.NewRow, pair.AlignmentKind))
            .Where(row => row != null)
            .Cast<SpreadsheetDiffRow>()
            .OrderBy(row => row.Sheet, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => Math.Min(row.OldRow < 0 ? int.MaxValue : row.OldRow, row.NewRow < 0 ? int.MaxValue : row.NewRow))
            .ThenBy(row => row.DisplayKey, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SpreadsheetDiffReport(rows);
    }

    public static Dictionary<ExcelCellKey, string> ReadCellValues(string filePath)
    {
        return WorkbookReaderCache.GetOrRead(filePath, ReadCellValuesUncached);
    }

    private static Dictionary<ExcelCellKey, string> ReadCellValuesUncached(string filePath)
    {
        if (IsXmlSpreadsheet(filePath))
        {
            return ReadXmlSpreadsheetCells(filePath);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.OpenRead(filePath);
        var workbook = WorkbookFactory.Create(stream);
        return ReadCells(workbook);
    }

    public static bool IsXmlSpreadsheetFile(string filePath)
    {
        return IsXmlSpreadsheet(filePath);
    }

    private static IReadOnlyList<SpreadsheetRowPair> AlignRows(
        IReadOnlyList<SpreadsheetDiffSourceRow> oldRows,
        IReadOnlyList<SpreadsheetDiffSourceRow> newRows)
    {
        var result = new List<SpreadsheetRowPair>();
        var usedOld = new HashSet<SpreadsheetDiffSourceRow>();
        var usedNew = new HashSet<SpreadsheetDiffSourceRow>();

        var oldSemantic = oldRows
            .Where(row => row.HasReliableKey)
            .GroupBy(row => row.RowMergeKey, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var newSemantic = newRows
            .Where(row => row.HasReliableKey)
            .GroupBy(row => row.RowMergeKey, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var key in oldSemantic.Keys.Union(newSemantic.Keys, StringComparer.Ordinal))
        {
            oldSemantic.TryGetValue(key, out var oldRow);
            newSemantic.TryGetValue(key, out var newRow);
            if (oldRow != null)
            {
                usedOld.Add(oldRow);
            }

            if (newRow != null)
            {
                usedNew.Add(newRow);
            }

            result.Add(new SpreadsheetRowPair(oldRow, newRow, SpreadsheetDiffAlignmentKind.Semantic));
        }

        foreach (var sheet in oldRows.Select(row => row.Sheet).Union(newRows.Select(row => row.Sheet), StringComparer.Ordinal))
        {
            var oldPhysical = oldRows
                .Where(row => !usedOld.Contains(row) && string.Equals(row.Sheet, sheet, StringComparison.Ordinal))
                .OrderBy(row => row.Row)
                .ToList();
            var newPhysical = newRows
                .Where(row => !usedNew.Contains(row) && string.Equals(row.Sheet, sheet, StringComparison.Ordinal))
                .OrderBy(row => row.Row)
                .ToList();
            foreach (var pair in AlignPhysicalRows(oldPhysical, newPhysical))
            {
                if (pair.OldRow != null)
                {
                    usedOld.Add(pair.OldRow);
                }

                if (pair.NewRow != null)
                {
                    usedNew.Add(pair.NewRow);
                }

                result.Add(pair);
            }
        }

        foreach (var oldRow in oldRows.Where(row => !usedOld.Contains(row)))
        {
            result.Add(new SpreadsheetRowPair(oldRow, null, SpreadsheetDiffAlignmentKind.Physical));
        }

        foreach (var newRow in newRows.Where(row => !usedNew.Contains(row)))
        {
            result.Add(new SpreadsheetRowPair(null, newRow, SpreadsheetDiffAlignmentKind.Physical));
        }

        return result;
    }

    private static IReadOnlyList<SpreadsheetRowPair> AlignPhysicalRows(
        IReadOnlyList<SpreadsheetDiffSourceRow> oldRows,
        IReadOnlyList<SpreadsheetDiffSourceRow> newRows)
    {
        var result = new List<SpreadsheetRowPair>();
        var oldAvailable = oldRows.ToHashSet();
        var newAvailable = newRows.ToHashSet();

        foreach (var pair in AlignExactRows(oldRows, newRows))
        {
            if (pair.OldRow == null || pair.NewRow == null || !oldAvailable.Contains(pair.OldRow) || !newAvailable.Contains(pair.NewRow))
            {
                continue;
            }

            oldAvailable.Remove(pair.OldRow);
            newAvailable.Remove(pair.NewRow);
            result.Add(pair);
        }

        var weakPairs = BuildWeakRowCandidates(oldAvailable, newAvailable);

        foreach (var pair in weakPairs.OrderByDescending(pair => pair.Score).ThenBy(pair => Math.Abs(pair.OldRow.Row - pair.NewRow.Row)))
        {
            if (!oldAvailable.Contains(pair.OldRow) || !newAvailable.Contains(pair.NewRow))
            {
                continue;
            }

            oldAvailable.Remove(pair.OldRow);
            newAvailable.Remove(pair.NewRow);
            result.Add(new SpreadsheetRowPair(pair.OldRow, pair.NewRow, SpreadsheetDiffAlignmentKind.Weak));
        }

        result.AddRange(oldAvailable.Select(row => new SpreadsheetRowPair(row, null, SpreadsheetDiffAlignmentKind.Physical)));
        result.AddRange(newAvailable.Select(row => new SpreadsheetRowPair(null, row, SpreadsheetDiffAlignmentKind.Physical)));
        return result;
    }

    private static List<(SpreadsheetDiffSourceRow OldRow, SpreadsheetDiffSourceRow NewRow, double Score)> BuildWeakRowCandidates(
        IReadOnlyCollection<SpreadsheetDiffSourceRow> oldAvailable,
        IReadOnlyCollection<SpreadsheetDiffSourceRow> newAvailable)
    {
        const long fullScanLimit = 1_000_000;
        var weakPairs = new List<(SpreadsheetDiffSourceRow OldRow, SpreadsheetDiffSourceRow NewRow, double Score)>();
        if (oldAvailable.Count == 0 || newAvailable.Count == 0)
        {
            return weakPairs;
        }

        var newByAnchor = newAvailable
            .Where(row => !string.IsNullOrWhiteSpace(row.AnchorKey))
            .GroupBy(row => row.AnchorKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var fullScanAllowed = (long)oldAvailable.Count * newAvailable.Count <= fullScanLimit;
        foreach (var oldRow in oldAvailable)
        {
            IEnumerable<SpreadsheetDiffSourceRow> candidates;
            if (!string.IsNullOrWhiteSpace(oldRow.AnchorKey) && newByAnchor.TryGetValue(oldRow.AnchorKey, out var anchored))
            {
                candidates = anchored;
            }
            else if (fullScanAllowed)
            {
                candidates = newAvailable;
            }
            else
            {
                candidates = newAvailable.Where(row => Math.Abs(row.Row - oldRow.Row) <= 50);
            }

            foreach (var newRow in candidates)
            {
                var score = RowSimilarity(oldRow, newRow);
                if (score >= 0.58)
                {
                    weakPairs.Add((oldRow, newRow, score));
                }
            }
        }

        return weakPairs;
    }

    private static IReadOnlyList<SpreadsheetRowPair> AlignExactRows(
        IReadOnlyList<SpreadsheetDiffSourceRow> oldRows,
        IReadOnlyList<SpreadsheetDiffSourceRow> newRows)
    {
        if (oldRows.Count == 0 || newRows.Count == 0)
        {
            return [];
        }

        if ((long)oldRows.Count * newRows.Count > 250000)
        {
            return AlignUniqueSignatures(oldRows, newRows);
        }

        var lengths = new int[oldRows.Count + 1, newRows.Count + 1];
        for (var oldIndex = oldRows.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newRows.Count - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = oldRows[oldIndex].Signature == newRows[newIndex].Signature
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        var pairs = new List<SpreadsheetRowPair>();
        var i = 0;
        var j = 0;
        while (i < oldRows.Count && j < newRows.Count)
        {
            if (oldRows[i].Signature == newRows[j].Signature)
            {
                pairs.Add(new SpreadsheetRowPair(oldRows[i], newRows[j], SpreadsheetDiffAlignmentKind.Weak));
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return pairs;
    }

    private static IReadOnlyList<SpreadsheetRowPair> AlignUniqueSignatures(
        IReadOnlyList<SpreadsheetDiffSourceRow> oldRows,
        IReadOnlyList<SpreadsheetDiffSourceRow> newRows)
    {
        var oldUnique = oldRows
            .GroupBy(row => row.Signature, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var newUnique = newRows
            .GroupBy(row => row.Signature, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return oldUnique.Keys
            .Intersect(newUnique.Keys, StringComparer.Ordinal)
            .Select(key => new SpreadsheetRowPair(oldUnique[key], newUnique[key], SpreadsheetDiffAlignmentKind.Weak))
            .ToList();
    }

    private static SpreadsheetDiffRow? CreateDiffRow(
        SpreadsheetDiffSourceRow? oldRow,
        SpreadsheetDiffSourceRow? newRow,
        SpreadsheetDiffAlignmentKind alignmentKind)
    {
        var source = newRow ?? oldRow ?? throw new InvalidOperationException("无法创建空表格差异行。");
        var fields = (oldRow?.Cells ?? [])
            .Concat(newRow?.Cells ?? [])
            .GroupBy(cell => cell.FieldKey, StringComparer.Ordinal)
            .OrderBy(group => group.Min(cell => cell.Cell.Column))
            .ThenBy(group => group.First().FieldName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var cells = new List<SpreadsheetDiffCell>();
        foreach (var field in fields)
        {
            var oldCell = oldRow?.Cells.LastOrDefault(cell => string.Equals(cell.FieldKey, field.Key, StringComparison.Ordinal));
            var newCell = newRow?.Cells.LastOrDefault(cell => string.Equals(cell.FieldKey, field.Key, StringComparison.Ordinal));
            var oldValue = oldCell?.Value ?? "";
            var newValue = newCell?.Value ?? "";
            var kind = SpreadsheetDiffCellKindLabels.FromValues(oldValue, newValue);
            var representative = newCell ?? oldCell ?? field.First();
            cells.Add(new SpreadsheetDiffCell(
                representative.FieldName,
                representative.Cell.Column,
                ToColumnName(representative.Cell.Column),
                oldValue,
                newValue,
                kind,
                SpreadsheetStructuredValueDiff.Create(oldValue, newValue)));
        }

        if (cells.All(cell => cell.Kind == SpreadsheetDiffCellKind.Unchanged))
        {
            return null;
        }

        var changeKind = oldRow == null
            ? SpreadsheetDiffChangeKind.Added
            : newRow == null ? SpreadsheetDiffChangeKind.Deleted : SpreadsheetDiffCellKindLabels.FromCells(cells);
        return new SpreadsheetDiffRow(
            source.Sheet,
            FirstNonEmpty(newRow?.DisplayKey, oldRow?.DisplayKey, source.Row >= 0 ? $"第 {source.Row + 1} 行" : "(无 ID)"),
            FirstNonEmpty(newRow?.RowMergeKey, oldRow?.RowMergeKey, ""),
            oldRow?.Row ?? -1,
            newRow?.Row ?? -1,
            changeKind,
            alignmentKind,
            oldRow?.RowText ?? "",
            newRow?.RowText ?? "",
            cells);
    }

    private static IReadOnlyList<SpreadsheetDiffSourceRow> BuildRows(IReadOnlyList<SpreadsheetMergeRawCell> rawCells)
    {
        return rawCells
            .GroupBy(cell => cell.RowMergeKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var cells = group
                    .OrderBy(cell => cell.Cell.Column)
                    .Select(cell => new SpreadsheetDiffSourceCell(cell, NormalizeFieldKey(cell.FieldName, cell.Cell.Column)))
                    .ToList();
                var first = cells.First().Source;
                var rowIds = group.Select(cell => cell.RowId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
                var physicalRows = group.Select(cell => cell.Cell.Row).Distinct().ToList();
                var hasReliableKey = group.All(cell => cell.HasRowMergeKey) && rowIds.Count == 1 && physicalRows.Count == 1;
                var displayKey = hasReliableKey ? rowIds[0] : $"第 {first.Cell.Row + 1} 行";
                var rowText = BuildRowText(cells);
                var valuesByField = BuildValuesByField(cells);
                return new SpreadsheetDiffSourceRow(
                    first.Cell.Sheet,
                    first.Cell.Row,
                    group.Key,
                    displayKey,
                    hasReliableKey,
                    rowText,
                    BuildRowSignature(cells),
                    BuildAnchorKey(valuesByField),
                    valuesByField,
                    cells);
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> BuildValuesByField(IReadOnlyList<SpreadsheetDiffSourceCell> cells)
    {
        return cells
            .GroupBy(cell => cell.FieldKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
    }

    private static string BuildAnchorKey(IReadOnlyDictionary<string, string> valuesByField)
    {
        var values = valuesByField
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => NormalizeComparableValue(pair.Value))
            .Where(value => value.Length > 0)
            .Take(2)
            .ToList();
        return values.Count == 0 ? "" : string.Join("\u001e", values);
    }

    private static string BuildRowText(IReadOnlyList<SpreadsheetDiffSourceCell> cells)
    {
        return string.Join(
            Environment.NewLine,
            cells.OrderBy(cell => cell.Cell.Column)
                .Select(cell => $"{cell.FieldName} = {(string.IsNullOrEmpty(cell.Value) ? "(空)" : cell.Value)}"));
    }

    private static string BuildRowSignature(IReadOnlyList<SpreadsheetDiffSourceCell> cells)
    {
        return string.Join(
            "\u001f",
            cells.OrderBy(cell => cell.Cell.Column)
                .Select(cell => $"{NormalizeFieldKey(cell.FieldName, cell.Cell.Column)}={NormalizeComparableValue(cell.Value)}"));
    }

    private static double RowSimilarity(SpreadsheetDiffSourceRow oldRow, SpreadsheetDiffSourceRow newRow)
    {
        var oldByField = oldRow.ValuesByField;
        var newByField = newRow.ValuesByField;
        var keys = oldByField.Keys.Union(newByField.Keys, StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            return 0;
        }

        var score = 0.0;
        foreach (var key in keys)
        {
            oldByField.TryGetValue(key, out var oldValue);
            newByField.TryGetValue(key, out var newValue);
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                score += 1.0;
            }
            else if (!string.IsNullOrWhiteSpace(oldValue) &&
                !string.IsNullOrWhiteSpace(newValue) &&
                TokenSimilarity(oldValue, newValue) >= 0.75)
            {
                score += 0.5;
            }
        }

        return score / keys.Count;
    }

    private static double TokenSimilarity(string oldValue, string newValue)
    {
        var oldTokens = TokenizeComparableValue(oldValue);
        var newTokens = TokenizeComparableValue(newValue);
        if (oldTokens.Count == 0 || newTokens.Count == 0)
        {
            return 0;
        }

        var same = oldTokens.Intersect(newTokens, StringComparer.Ordinal).Count();
        return (double)same / Math.Max(oldTokens.Count, newTokens.Count);
    }

    private static HashSet<string> TokenizeComparableValue(string value)
    {
        return Regex.Split(NormalizeComparableValue(value), @"[,\s;|=]+")
            .Where(token => token.Length > 0)
            .Take(64)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeComparableValue(string value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string NormalizeFieldKey(string fieldName, int column)
    {
        var normalized = Regex.Replace((fieldName ?? "").Trim().ToLowerInvariant(), @"[^\w\u4e00-\u9fff]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? $"col_{column + 1}" : normalized;
    }

    private static ExcelCellDifference CreateDifference(
        SpreadsheetMergeRawCell? oldCell,
        SpreadsheetMergeRawCell? newCell,
        IReadOnlyDictionary<ExcelCellKey, SpreadsheetMergeRawCell> oldCells,
        IReadOnlyDictionary<ExcelCellKey, SpreadsheetMergeRawCell> newCells,
        Dictionary<ExcelRowKey, string> oldRowTextCache,
        Dictionary<ExcelRowKey, string> newRowTextCache,
        string oldValue,
        string newValue)
    {
        var key = newCell?.Cell ?? oldCell?.Cell ?? throw new InvalidOperationException("无法定位差异单元格。");
        var fieldName = FirstNonEmpty(
            newCell?.FieldName,
            oldCell?.FieldName,
            ToColumnName(key.Column));
        var rowId = FirstNonEmpty(
            newCell?.RowId,
            oldCell?.RowId);
        return new ExcelCellDifference(
            key.Sheet,
            key.Row + 1,
            key.Column + 1,
            ToColumnName(key.Column),
            fieldName,
            rowId,
            oldValue,
            newValue,
            oldCell == null ? "" : GetCachedRowText(oldCell.Cell, oldCells, oldRowTextCache),
            newCell == null ? "" : GetCachedRowText(newCell.Cell, newCells, newRowTextCache));
    }

    private static HashSet<string> FindUnsafeSemanticKeys(
        IReadOnlyList<SpreadsheetMergeRawCell> oldCells,
        IReadOnlyList<SpreadsheetMergeRawCell> newCells)
    {
        var unsafeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cells in new[] { oldCells, newCells })
        {
            foreach (var group in cells.Where(cell => cell.HasSemanticKey).GroupBy(cell => cell.SemanticKey, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    unsafeKeys.Add(group.Key);
                }
            }
        }

        return unsafeKeys;
    }

    private static Dictionary<string, SpreadsheetMergeRawCell> MaterializeCells(
        IReadOnlyList<SpreadsheetMergeRawCell> rawCells,
        HashSet<string> unsafeSemanticKeys)
    {
        return rawCells
            .Select(cell =>
            {
                var key = cell.HasSemanticKey && !unsafeSemanticKeys.Contains(cell.SemanticKey)
                    ? cell.SemanticKey
                    : cell.PhysicalKey;
                return (Key: key, Cell: cell);
            })
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Cell, StringComparer.Ordinal);
    }

    private static SpreadsheetMergeRawCell? PickCell(
        string key,
        Dictionary<string, SpreadsheetMergeRawCell> oldCells,
        Dictionary<string, SpreadsheetMergeRawCell> newCells)
    {
        if (newCells.TryGetValue(key, out var newCell))
        {
            return newCell;
        }

        return oldCells.TryGetValue(key, out var oldCell) ? oldCell : null;
    }

    private static string GetCachedRowText(
        ExcelCellKey cell,
        IReadOnlyDictionary<ExcelCellKey, SpreadsheetMergeRawCell> cells,
        Dictionary<ExcelRowKey, string> cache)
    {
        var rowKey = new ExcelRowKey(cell.Sheet, cell.Row);
        if (cache.TryGetValue(rowKey, out var cached))
        {
            return cached;
        }

        var text = BuildRowText(rowKey, cells);
        cache[rowKey] = text;
        return text;
    }

    private static string BuildRowText(
        ExcelRowKey rowKey,
        IReadOnlyDictionary<ExcelCellKey, SpreadsheetMergeRawCell> cells)
    {
        var lines = cells.Values
            .Where(cell => string.Equals(cell.Cell.Sheet, rowKey.Sheet, StringComparison.Ordinal) && cell.Cell.Row == rowKey.Row)
            .OrderBy(cell => cell.Cell.Column)
            .Select(cell => $"{cell.FieldName} = {(string.IsNullOrEmpty(cell.Value) ? "(空)" : cell.Value)}")
            .ToList();
        return string.Join(Environment.NewLine, lines);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static bool IsXmlSpreadsheet(string filePath)
    {
        var comparablePath = SvnConflictArtifact.NormalizeToBasePath(filePath);
        if (!string.Equals(Path.GetExtension(comparablePath), ".xml", StringComparison.OrdinalIgnoreCase))
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

    private static Dictionary<ExcelCellKey, string> ReadXmlSpreadsheetCells(string filePath)
    {
        var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
        XNamespace spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
        var cells = new Dictionary<ExcelCellKey, string>();

        foreach (var worksheet in document.Root?.Elements(spreadsheet + "Worksheet") ?? Enumerable.Empty<XElement>())
        {
            var sheetName = worksheet.Attribute(spreadsheet + "Name")?.Value ?? "Sheet";
            var table = worksheet.Element(spreadsheet + "Table");
            if (table == null)
            {
                continue;
            }

            var rowIndex = 0;
            foreach (var row in table.Elements(spreadsheet + "Row"))
            {
                var explicitRowIndex = GetSpreadsheetIndex(row, spreadsheet);
                if (explicitRowIndex.HasValue)
                {
                    rowIndex = explicitRowIndex.Value - 1;
                }

                var columnIndex = 0;
                foreach (var cell in row.Elements(spreadsheet + "Cell"))
                {
                    var explicitColumnIndex = GetSpreadsheetIndex(cell, spreadsheet);
                    if (explicitColumnIndex.HasValue)
                    {
                        columnIndex = explicitColumnIndex.Value - 1;
                    }

                    var data = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "Data");
                    var value = NormalizeCellText(data?.Value ?? "");
                    if (!string.IsNullOrEmpty(value))
                    {
                        cells[new ExcelCellKey(sheetName, rowIndex, columnIndex)] = value;
                    }

                    columnIndex++;
                }

                rowIndex++;
            }
        }

        return cells;
    }

    private static int? GetSpreadsheetIndex(XElement element, XNamespace spreadsheet)
    {
        var value = element.Attribute(spreadsheet + "Index")?.Value;
        return int.TryParse(value, out var index) ? index : null;
    }

    private static string NormalizeCellText(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static Dictionary<ExcelCellKey, string> ReadCells(IWorkbook workbook)
    {
        var formatter = new DataFormatter();
        var cells = new Dictionary<ExcelCellKey, string>();
        for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            if (sheet == null)
            {
                continue;
            }

            for (var rowIndex = sheet.FirstRowNum; rowIndex <= sheet.LastRowNum; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row == null)
                {
                    continue;
                }

                for (var columnIndex = row.FirstCellNum; columnIndex < row.LastCellNum; columnIndex++)
                {
                    if (columnIndex < 0)
                    {
                        continue;
                    }

                    var cell = row.GetCell(columnIndex);
                    var value = cell == null ? "" : formatter.FormatCellValue(cell).Trim();
                    if (!string.IsNullOrEmpty(value))
                    {
                        cells[new ExcelCellKey(sheet.SheetName, rowIndex, columnIndex)] = value;
                    }
                }
            }
        }

        return cells;
    }

    public static string ToColumnName(int zeroBasedColumn)
    {
        var column = zeroBasedColumn + 1;
        var name = "";
        while (column > 0)
        {
            var modulo = (column - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            column = (column - modulo) / 26;
        }

        return name;
    }
}

internal sealed record SpreadsheetRowPair(
    SpreadsheetDiffSourceRow? OldRow,
    SpreadsheetDiffSourceRow? NewRow,
    SpreadsheetDiffAlignmentKind AlignmentKind);

internal sealed record SpreadsheetDiffSourceRow(
    string Sheet,
    int Row,
    string RowMergeKey,
    string DisplayKey,
    bool HasReliableKey,
    string RowText,
    string Signature,
    string AnchorKey,
    IReadOnlyDictionary<string, string> ValuesByField,
    IReadOnlyList<SpreadsheetDiffSourceCell> Cells);

internal sealed record SpreadsheetDiffSourceCell(SpreadsheetMergeRawCell Source, string FieldKey)
{
    public ExcelCellKey Cell => Source.Cell;
    public string FieldName => Source.FieldName;
    public string Value => Source.Value;
}

