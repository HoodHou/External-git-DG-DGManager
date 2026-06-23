using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal enum SpreadsheetMergeChangeKind
{
    AutoRemote,
    LocalOnly,
    SameBoth,
    Conflict,
}

internal enum SpreadsheetMergeResolution
{
    UseLocal,
    UseRemote,
}

internal enum SpreadsheetMergeOperation
{
    KeepTarget,
    WriteCell,
    AppendRow,
    InsertRow,
    DeleteRow,
}

internal enum SpreadsheetMergeWriteKind
{
    SetCell,
    InsertRow,
    DeleteRow,
}

internal sealed class SpreadsheetMergePlan
{
    public SpreadsheetMergePlan(
        IReadOnlyList<SpreadsheetMergeChange> autoRemoteChanges,
        IReadOnlyList<SpreadsheetMergeChange> localOnlyChanges,
        IReadOnlyList<SpreadsheetMergeChange> sameBothChanges,
        IReadOnlyList<SpreadsheetMergeChange> conflicts)
    {
        AutoRemoteChanges = autoRemoteChanges;
        LocalOnlyChanges = localOnlyChanges;
        SameBothChanges = sameBothChanges;
        Conflicts = conflicts;
    }

    public IReadOnlyList<SpreadsheetMergeChange> AutoRemoteChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> LocalOnlyChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> SameBothChanges { get; }
    public IReadOnlyList<SpreadsheetMergeChange> Conflicts { get; }
    public IReadOnlyList<SpreadsheetMergeChange> AllChanges => AutoRemoteChanges
        .Concat(LocalOnlyChanges)
        .Concat(SameBothChanges)
        .Concat(Conflicts)
        .ToList();
    public IReadOnlyList<SpreadsheetMergeChange> MergeWorkChanges => AutoRemoteChanges
        .Concat(Conflicts)
        .ToList();
    public int ResolvedConflictCount => Conflicts.Count(change => change.Resolution == SpreadsheetMergeResolution.UseRemote);
    public int PlannedWriteCount => AllChanges.Count(change =>
        change.Operation != SpreadsheetMergeOperation.KeepTarget &&
        !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal));
    public int RelevantChangeCount => MergeWorkChanges.Count;

    public IReadOnlyList<SpreadsheetMergeWrite> BuildWrites()
    {
        var selectedChanges = AllChanges
            .Where(change => change.Operation != SpreadsheetMergeOperation.KeepTarget)
            .Where(change => !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal))
            .ToList();
        var deletes = selectedChanges
            .Where(change => change.Operation == SpreadsheetMergeOperation.DeleteRow)
            .GroupBy(change => new ExcelRowKey(change.WriteCell.Sheet, change.WriteCell.Row))
            .Select(group => new SpreadsheetMergeWrite(new ExcelCellKey(group.Key.Sheet, group.Key.Row, 0), "", SpreadsheetMergeWriteKind.DeleteRow));
        var inserts = selectedChanges
            .Where(change => change.Operation == SpreadsheetMergeOperation.InsertRow)
            .GroupBy(change => new ExcelRowKey(change.WriteCell.Sheet, change.WriteCell.Row))
            .SelectMany(group => BuildSourceRowWrites(group, SpreadsheetMergeWriteKind.InsertRow));
        var appends = selectedChanges
            .Where(change => change.Operation == SpreadsheetMergeOperation.AppendRow)
            .GroupBy(change => new ExcelRowKey(change.WriteCell.Sheet, change.WriteCell.Row))
            .SelectMany(group => BuildSourceRowWrites(group, SpreadsheetMergeWriteKind.SetCell));
        var sets = selectedChanges
            .Where(change => change.Operation == SpreadsheetMergeOperation.WriteCell)
            .Select(change => new SpreadsheetMergeWrite(change.WriteCell, change.RemoteValue, SpreadsheetMergeWriteKind.SetCell));

        return deletes
            .Concat(inserts)
            .Concat(appends)
            .Concat(sets)
            .GroupBy(write => $"{write.Kind}|{write.Cell.Sheet}|{write.Cell.Row}|{write.Cell.Column}", StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    private static IEnumerable<SpreadsheetMergeWrite> BuildSourceRowWrites(
        IEnumerable<SpreadsheetMergeChange> changes,
        SpreadsheetMergeWriteKind kind)
    {
        var groupedChanges = changes.ToList();
        if (groupedChanges.Count == 0)
        {
            yield break;
        }

        var anchor = groupedChanges.First();
        var row = anchor.WriteCell.Row;
        var sheet = anchor.WriteCell.Sheet;
        var contextFields = anchor.RowContext.Fields
            .Where(field => field.ColumnIndex >= 0)
            .Where(field => !string.IsNullOrEmpty(field.RemoteValue))
            .OrderBy(field => field.ColumnIndex)
            .ToList();
        if (contextFields.Count == 0)
        {
            foreach (var change in groupedChanges)
            {
                yield return new SpreadsheetMergeWrite(change.WriteCell, change.RemoteValue, kind);
            }

            yield break;
        }

        foreach (var field in contextFields)
        {
            yield return new SpreadsheetMergeWrite(new ExcelCellKey(sheet, row, field.ColumnIndex), field.RemoteValue, kind);
        }
    }
}

internal sealed class SpreadsheetMergeChange
{
    public SpreadsheetMergeChange(
        SpreadsheetMergeChangeKind kind,
        ExcelCellKey targetCell,
        string fieldName,
        string rowId,
        string baseValue,
        string localValue,
        string remoteValue,
        bool targetCellExists = true,
        bool targetRowExists = true,
        bool sourceCellExists = true,
        string rowMergeKey = "",
        SpreadsheetMergeRowContext? rowContext = null)
    {
        Kind = kind;
        TargetCell = targetCell;
        WriteCell = targetCell;
        FieldName = string.IsNullOrWhiteSpace(fieldName) ? "(未命名字段)" : fieldName;
        RowId = string.IsNullOrWhiteSpace(rowId) ? "(无 ID)" : rowId;
        BaseValue = baseValue;
        LocalValue = localValue;
        RemoteValue = remoteValue;
        TargetCellExists = targetCellExists;
        TargetRowExists = targetRowExists;
        SourceCellExists = sourceCellExists;
        RowMergeKey = rowMergeKey;
        RowContext = rowContext ?? new SpreadsheetMergeRowContext([]);
        Resolution = kind == SpreadsheetMergeChangeKind.AutoRemote
            ? SpreadsheetMergeResolution.UseRemote
            : SpreadsheetMergeResolution.UseLocal;
        Operation = Resolution == SpreadsheetMergeResolution.UseRemote
            ? SpreadsheetMergeOperation.WriteCell
            : SpreadsheetMergeOperation.KeepTarget;
        if (Operation == SpreadsheetMergeOperation.WriteCell && SourceCellExists && !TargetRowExists)
        {
            Operation = SpreadsheetMergeOperation.KeepTarget;
            Resolution = SpreadsheetMergeResolution.UseLocal;
        }
    }

    public SpreadsheetMergeChangeKind Kind { get; }
    public ExcelCellKey TargetCell { get; }
    public ExcelCellKey WriteCell { get; set; }
    public string FieldName { get; }
    public string RowId { get; }
    public string BaseValue { get; }
    public string LocalValue { get; }
    public string RemoteValue { get; set; }
    public SpreadsheetMergeResolution Resolution { get; set; }
    public SpreadsheetMergeOperation Operation { get; set; }
    public bool TargetCellExists { get; set; }
    public bool TargetRowExists { get; set; }
    public bool SourceCellExists { get; set; }
    public string RowMergeKey { get; set; }
    public SpreadsheetMergeRowContext RowContext { get; }
    public string Sheet => TargetCell.Sheet;
    public string ColumnName => ExcelDiffService.ToColumnName(TargetCell.Column);
    public string Address => $"{ColumnName}{TargetCell.Row + 1}";
}

internal sealed record SpreadsheetMergeWrite(ExcelCellKey Cell, string Value, SpreadsheetMergeWriteKind Kind = SpreadsheetMergeWriteKind.SetCell);

internal sealed record SpreadsheetMergeRowContext(IReadOnlyList<SpreadsheetMergeRowContextField> Fields);

internal sealed record SpreadsheetMergeRowContextField(
    string FieldName,
    int ColumnIndex,
    string ColumnName,
    string BaseValue,
    string LocalValue,
    string RemoteValue,
    bool IsCurrentField);

internal sealed record SpreadsheetMergeRawCell(
    ExcelCellKey Cell,
    string Value,
    string FieldName,
    string RowId,
    string RowMergeKey,
    bool HasRowMergeKey,
    string SemanticKey,
    bool HasSemanticKey)
{
    public string PhysicalKey => SpreadsheetThreeWayMergeService.CreatePhysicalKey(Cell);
}

internal sealed record SpreadsheetMergeCell(
    string MergeKey,
    ExcelCellKey Cell,
    string Value,
    string FieldName,
    string RowId,
    string RowMergeKey,
    bool HasRowMergeKey);

internal sealed record SpreadsheetMergeResolvedTarget(ExcelCellKey Cell, bool TargetCellExists, bool TargetRowExists, string RowMergeKey);

internal sealed class SpreadsheetMergeSheetLayout
{
    public SpreadsheetMergeSheetLayout(string sheet, int fieldHeaderRow, Dictionary<int, string> headers, IReadOnlyList<int> keyColumns)
    {
        Sheet = sheet;
        FieldHeaderRow = fieldHeaderRow;
        Headers = headers;
        KeyColumns = keyColumns;
    }

    public string Sheet { get; }
    public int FieldHeaderRow { get; }
    public Dictionary<int, string> Headers { get; }
    public IReadOnlyList<int> KeyColumns { get; }
    public bool HasKey => KeyColumns.Count > 0;

    public string FieldName(int column)
    {
        return Headers.TryGetValue(column, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header
            : $"col_{column + 1}";
    }

    public string RowKey(Dictionary<ExcelCellKey, string> cells, int row)
    {
        if (KeyColumns.Count == 0)
        {
            return "";
        }

        var values = KeyColumns
            .Select(column => GetCellValue(cells, Sheet, row, column).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return values.Count == KeyColumns.Count ? string.Join("/", values) : "";
    }

    private static string GetCellValue(Dictionary<ExcelCellKey, string> cells, string sheet, int row, int column)
    {
        return cells.TryGetValue(new ExcelCellKey(sheet, row, column), out var value) ? value : "";
    }
}

internal static class SpreadsheetThreeWayMergeService
{
    private const long DefaultMaxSingleInputBytes = 120L * 1024 * 1024;
    private const long DefaultMaxTotalInputBytes = 240L * 1024 * 1024;
    private static readonly Regex FieldTokenRegex = new("^[a-zA-Z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex HeaderNonWordRegex = new(@"[^\w\u4e00-\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex HeaderUnderscoreRegex = new("_+", RegexOptions.Compiled);
    private static readonly Regex KeyFieldRegex = new(@"(?:^|_)(id|level|key|code|name|type)(?:$|_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSupportedPath(string filePath)
    {
        return DiffFileKindDetector.IsSpreadsheet(filePath);
    }

    public static void ValidateMergeInputs(params string[] filePaths)
    {
        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new FileInfo(path))
            .ToList();
        if (files.Any(file => !file.Exists))
        {
            var missing = files.First(file => !file.Exists).FullName;
            throw new FileNotFoundException("表格合并输入文件不存在。", missing);
        }

        var maxSingleBytes = GetConfiguredLimitBytes("SVNMANAGER_SPREADSHEET_MERGE_MAX_SINGLE_MB", DefaultMaxSingleInputBytes);
        var maxTotalBytes = GetConfiguredLimitBytes("SVNMANAGER_SPREADSHEET_MERGE_MAX_TOTAL_MB", DefaultMaxTotalInputBytes);
        var tooLargeFile = files.FirstOrDefault(file => file.Length > maxSingleBytes);
        if (tooLargeFile != null)
        {
            throw new InvalidOperationException(
                $"表格文件过大，已阻止在主进程内一次性加载三份表格：{tooLargeFile.Name} ({FormatBytes(tooLargeFile.Length)})。" +
                $"{Environment.NewLine}当前单文件上限：{FormatBytes(maxSingleBytes)}。可通过环境变量 SVNMANAGER_SPREADSHEET_MERGE_MAX_SINGLE_MB 调高，但更建议先拆分超大表。");
        }

        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes > maxTotalBytes)
        {
            throw new InvalidOperationException(
                $"三方合并输入总量过大，已阻止一次性加载：{FormatBytes(totalBytes)}。" +
                $"{Environment.NewLine}当前总上限：{FormatBytes(maxTotalBytes)}。可通过环境变量 SVNMANAGER_SPREADSHEET_MERGE_MAX_TOTAL_MB 调高。");
        }
    }

    public static SpreadsheetMergePlan BuildPlan(string baseFilePath, string localFilePath, string remoteFilePath)
    {
        ValidateMergeInputs(baseFilePath, localFilePath, remoteFilePath);
        var baseRaw = ReadRawCells(baseFilePath);
        var localRaw = ReadRawCells(localFilePath);
        var remoteRaw = ReadRawCells(remoteFilePath);
        var unsafeSemanticKeys = FindUnsafeSemanticKeys(baseRaw, localRaw, remoteRaw);
        var baseCells = MaterializeCells(baseRaw, unsafeSemanticKeys);
        var localCells = MaterializeCells(localRaw, unsafeSemanticKeys);
        var remoteCells = MaterializeCells(remoteRaw, unsafeSemanticKeys);
        var localRows = BuildRowLocationMap(localRaw);
        var nextAppendRowBySheet = BuildNextAppendRowMap(localRaw);
        var suggestedRows = new Dictionary<string, int>(StringComparer.Ordinal);

        var keys = baseCells.Keys
            .Union(localCells.Keys)
            .Union(remoteCells.Keys)
            .OrderBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Sheet)
            .ThenBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Row)
            .ThenBy(key => PickCell(key, localCells, baseCells, remoteCells)?.Cell.Column)
            .ToList();

        var autoRemote = new List<SpreadsheetMergeChange>();
        var localOnly = new List<SpreadsheetMergeChange>();
        var sameBoth = new List<SpreadsheetMergeChange>();
        var conflicts = new List<SpreadsheetMergeChange>();

        foreach (var key in keys)
        {
            baseCells.TryGetValue(key, out var baseCell);
            localCells.TryGetValue(key, out var localCell);
            remoteCells.TryGetValue(key, out var remoteCell);

            var baseValue = baseCell?.Value ?? "";
            var localValue = localCell?.Value ?? "";
            var remoteValue = remoteCell?.Value ?? "";
            var localChanged = !string.Equals(localValue, baseValue, StringComparison.Ordinal);
            var remoteChanged = !string.Equals(remoteValue, baseValue, StringComparison.Ordinal);
            if (!localChanged && !remoteChanged)
            {
                continue;
            }

            var target = ResolveTargetCell(localCell, baseCell, remoteCell, localRows, nextAppendRowBySheet, suggestedRows);
            var fieldName = FirstNonEmpty(localCell?.FieldName, remoteCell?.FieldName, baseCell?.FieldName);
            var rowId = FirstNonEmpty(localCell?.RowId, remoteCell?.RowId, baseCell?.RowId);
            var rowContext = BuildRowContext(baseRaw, localRaw, remoteRaw, baseCell, localCell, remoteCell, target, fieldName);

            if (remoteChanged && !localChanged)
            {
                autoRemote.Add(CreateChange(SpreadsheetMergeChangeKind.AutoRemote, target, fieldName, rowId, baseValue, localValue, remoteValue, remoteCell != null, rowContext));
                continue;
            }

            if (localChanged && !remoteChanged)
            {
                localOnly.Add(CreateChange(SpreadsheetMergeChangeKind.LocalOnly, target, fieldName, rowId, baseValue, localValue, remoteValue, remoteCell != null, rowContext));
                continue;
            }

            if (string.Equals(localValue, remoteValue, StringComparison.Ordinal))
            {
                sameBoth.Add(CreateChange(SpreadsheetMergeChangeKind.SameBoth, target, fieldName, rowId, baseValue, localValue, remoteValue, remoteCell != null, rowContext));
                continue;
            }

            conflicts.Add(CreateChange(SpreadsheetMergeChangeKind.Conflict, target, fieldName, rowId, baseValue, localValue, remoteValue, remoteCell != null, rowContext));
        }

        return new SpreadsheetMergePlan(autoRemote, localOnly, sameBoth, conflicts);
    }

    private static SpreadsheetMergeChange CreateChange(
        SpreadsheetMergeChangeKind kind,
        SpreadsheetMergeResolvedTarget target,
        string fieldName,
        string rowId,
        string baseValue,
        string localValue,
        string remoteValue,
        bool sourceCellExists,
        SpreadsheetMergeRowContext rowContext)
    {
        return new SpreadsheetMergeChange(
            kind,
            target.Cell,
            fieldName,
            rowId,
            baseValue,
            localValue,
            remoteValue,
            target.TargetCellExists,
            target.TargetRowExists,
            sourceCellExists,
            target.RowMergeKey,
            rowContext);
    }

    public static string CreateBackup(string localFilePath)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNManager",
            "merge-backups",
            DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileNameWithoutExtension(localFilePath);
        var extension = Path.GetExtension(localFilePath);
        var backupPath = Path.Combine(directory, $"{fileName}_{DateTime.Now:HHmmss_fff}{extension}");
        File.Copy(localFilePath, backupPath, overwrite: false);
        return backupPath;
    }

    public static void ApplyWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        if (writes.Count == 0)
        {
            return;
        }

        if (ExcelDiffService.IsXmlSpreadsheetFile(localFilePath))
        {
            ApplyXmlWrites(localFilePath, writes);
            return;
        }

        ApplyWorkbookWrites(localFilePath, writes);
    }

    public static string CreatePhysicalKey(ExcelCellKey cell)
    {
        return $"P|{cell.Sheet}|{cell.Row}|{cell.Column}";
    }

    private static IReadOnlyList<SpreadsheetMergeRawCell> ReadRawCells(string filePath)
    {
        return ReadRawCells(ExcelDiffService.ReadCellValues(filePath));
    }

    internal static IReadOnlyList<SpreadsheetMergeRawCell> ReadRawCells(Dictionary<ExcelCellKey, string> cells)
    {
        var layouts = InferSheetLayouts(cells);
        return cells
            .Select(pair =>
            {
                var cell = pair.Key;
                var layout = layouts.TryGetValue(cell.Sheet, out var inferredLayout)
                    ? inferredLayout
                    : new SpreadsheetMergeSheetLayout(cell.Sheet, 1, [], []);
                var fieldName = layout.FieldName(cell.Column);
                var rowId = layout.RowKey(cells, cell.Row);
                var hasSemanticKey = layout.HasKey &&
                    cell.Row > layout.FieldHeaderRow &&
                    !string.IsNullOrWhiteSpace(rowId) &&
                    !string.IsNullOrWhiteSpace(fieldName);
                var hasRowMergeKey = layout.HasKey &&
                    cell.Row > layout.FieldHeaderRow &&
                    !string.IsNullOrWhiteSpace(rowId);
                var rowMergeKey = hasRowMergeKey
                    ? $"R|{cell.Sheet}|{rowId.Trim()}"
                    : $"P|{cell.Sheet}|{cell.Row}";
                var semanticKey = hasSemanticKey
                    ? $"S|{cell.Sheet}|{rowId.Trim()}|{NormalizeHeader(fieldName)}"
                    : "";
                return new SpreadsheetMergeRawCell(cell, pair.Value, fieldName, rowId, rowMergeKey, hasRowMergeKey, semanticKey, hasSemanticKey);
            })
            .ToList();
    }

    private static Dictionary<string, SpreadsheetMergeSheetLayout> InferSheetLayouts(Dictionary<ExcelCellKey, string> cells)
    {
        return cells.Keys
            .Select(key => key.Sheet)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                sheet => sheet,
                sheet => InferSheetLayout(sheet, cells),
                StringComparer.Ordinal);
    }

    private static SpreadsheetMergeSheetLayout InferSheetLayout(string sheet, Dictionary<ExcelCellKey, string> cells)
    {
        var sheetCells = cells
            .Where(pair => string.Equals(pair.Key.Sheet, sheet, StringComparison.Ordinal))
            .ToList();
        if (sheetCells.Count == 0)
        {
            return new SpreadsheetMergeSheetLayout(sheet, 1, [], []);
        }

        var maxRow = sheetCells.Max(pair => pair.Key.Row);
        var maxColumn = sheetCells.Max(pair => pair.Key.Column);
        var fieldHeaderRow = InferFieldHeaderRow(sheet, cells, maxRow);
        var headers = Enumerable.Range(0, maxColumn + 1)
            .ToDictionary(column => column, column =>
            {
                var value = GetCellValue(cells, sheet, fieldHeaderRow, column).Trim();
                return string.IsNullOrWhiteSpace(value) ? $"col_{column + 1}" : value;
            });
        var keyColumns = InferKeyColumns(sheet, cells, headers, fieldHeaderRow, maxRow);
        return new SpreadsheetMergeSheetLayout(sheet, fieldHeaderRow, headers, keyColumns);
    }

    private static int InferFieldHeaderRow(string sheet, Dictionary<ExcelCellKey, string> cells, int maxRow)
    {
        var bestRow = 0;
        var bestScore = -1.0;
        var limit = Math.Min(maxRow, 11);
        for (var row = 0; row <= limit; row++)
        {
            var values = RowValues(cells, sheet, row)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var tokenCount = values.Count(value => FieldTokenRegex.IsMatch(value));
            var score = (double)tokenCount / values.Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = row;
            }
        }

        return bestRow;
    }

    private static IReadOnlyList<int> InferKeyColumns(
        string sheet,
        Dictionary<ExcelCellKey, string> cells,
        Dictionary<int, string> headers,
        int fieldHeaderRow,
        int maxRow)
    {
        var dataRows = Enumerable.Range(fieldHeaderRow + 1, Math.Max(0, maxRow - fieldHeaderRow))
            .Where(row => RowNonEmptyCount(cells, sheet, row) >= 2)
            .ToList();
        if (dataRows.Count == 0)
        {
            return [];
        }

        foreach (var exactIdColumn in headers
            .Where(pair => string.Equals(NormalizeHeader(pair.Value), "id", StringComparison.Ordinal))
            .Select(pair => pair.Key))
        {
            var keys = dataRows
                .Select(row => GetCellValue(cells, sheet, row, exactIdColumn).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (keys.Count > 0 && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count)
            {
                return [exactIdColumn];
            }
        }

        var candidates = headers
            .Where(pair => KeyFieldRegex.IsMatch(NormalizeHeader(pair.Value)))
            .Select(pair => pair.Key)
            .Take(6)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        IReadOnlyList<int> bestColumns = [];
        var bestScore = -1.0;
        for (var width = 1; width <= Math.Min(3, candidates.Count); width++)
        {
            foreach (var columns in Combinations(candidates, width))
            {
                var keys = dataRows
                    .Select(row => columns.Select(column => GetCellValue(cells, sheet, row, column).Trim()).ToArray())
                    .Where(values => values.All(value => !string.IsNullOrWhiteSpace(value)))
                    .Select(values => string.Join("\u001f", values))
                    .ToList();
                if (keys.Count == 0 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                {
                    continue;
                }

                var coverage = (double)keys.Count / dataRows.Count;
                var idBonus = columns.Count(column => NormalizeHeader(headers[column]).Contains("id", StringComparison.Ordinal)) * 0.15;
                var score = coverage + idBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestColumns = columns.ToList();
                }
            }
        }

        return bestScore >= 0.8 ? bestColumns : [];
    }

    private static IEnumerable<IReadOnlyList<int>> Combinations(IReadOnlyList<int> values, int width)
    {
        var selected = new int[width];
        return Build(0, 0);

        IEnumerable<IReadOnlyList<int>> Build(int start, int depth)
        {
            if (depth == width)
            {
                yield return selected.ToArray();
                yield break;
            }

            for (var index = start; index <= values.Count - (width - depth); index++)
            {
                selected[depth] = values[index];
                foreach (var item in Build(index + 1, depth + 1))
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<string> RowValues(Dictionary<ExcelCellKey, string> cells, string sheet, int row)
    {
        return cells
            .Where(pair => string.Equals(pair.Key.Sheet, sheet, StringComparison.Ordinal) && pair.Key.Row == row)
            .OrderBy(pair => pair.Key.Column)
            .Select(pair => pair.Value);
    }

    private static int RowNonEmptyCount(Dictionary<ExcelCellKey, string> cells, string sheet, int row)
    {
        return RowValues(cells, sheet, row).Count(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeHeader(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        text = HeaderNonWordRegex.Replace(text, "_");
        text = HeaderUnderscoreRegex.Replace(text, "_").Trim('_');
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }

    private static HashSet<string> FindUnsafeSemanticKeys(params IReadOnlyList<SpreadsheetMergeRawCell>[] versions)
    {
        var unsafeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            foreach (var group in version.Where(cell => cell.HasSemanticKey).GroupBy(cell => cell.SemanticKey))
            {
                if (group.Count() > 1)
                {
                    unsafeKeys.Add(group.Key);
                }
            }
        }

        return unsafeKeys;
    }

    private static Dictionary<string, SpreadsheetMergeCell> MaterializeCells(
        IReadOnlyList<SpreadsheetMergeRawCell> rawCells,
        HashSet<string> unsafeSemanticKeys)
    {
        return rawCells
            .Select(raw =>
            {
                var key = raw.HasSemanticKey && !unsafeSemanticKeys.Contains(raw.SemanticKey)
                    ? raw.SemanticKey
                    : raw.PhysicalKey;
                return new SpreadsheetMergeCell(key, raw.Cell, raw.Value, raw.FieldName, raw.RowId, raw.RowMergeKey, raw.HasRowMergeKey);
            })
            .GroupBy(cell => cell.MergeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    private static Dictionary<string, ExcelCellKey> BuildRowLocationMap(IReadOnlyList<SpreadsheetMergeRawCell> rawCells)
    {
        return rawCells
            .Where(cell => cell.HasRowMergeKey)
            .GroupBy(cell => cell.RowMergeKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(cell => cell.Cell.Column).First().Cell,
                StringComparer.Ordinal);
    }

    private static Dictionary<string, int> BuildNextAppendRowMap(IReadOnlyList<SpreadsheetMergeRawCell> rawCells)
    {
        return rawCells
            .GroupBy(cell => cell.Cell.Sheet, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(cell => cell.Cell.Row) + 1,
                StringComparer.Ordinal);
    }

    private static SpreadsheetMergeRowContext BuildRowContext(
        IReadOnlyList<SpreadsheetMergeRawCell> baseRaw,
        IReadOnlyList<SpreadsheetMergeRawCell> localRaw,
        IReadOnlyList<SpreadsheetMergeRawCell> remoteRaw,
        SpreadsheetMergeCell? baseCell,
        SpreadsheetMergeCell? localCell,
        SpreadsheetMergeCell? remoteCell,
        SpreadsheetMergeResolvedTarget target,
        string currentFieldName)
    {
        var rowMergeKey = FirstNonEmpty(localCell?.RowMergeKey, remoteCell?.RowMergeKey, baseCell?.RowMergeKey, target.RowMergeKey);
        var baseRow = GetRowContextCells(baseRaw, rowMergeKey, baseCell?.Cell);
        var localRow = GetRowContextCells(localRaw, rowMergeKey, localCell?.Cell ?? target.Cell);
        var remoteRow = GetRowContextCells(remoteRaw, rowMergeKey, remoteCell?.Cell);
        var currentFieldKey = NormalizeHeader(currentFieldName);

        var fieldKeys = baseRow
            .Concat(localRow)
            .Concat(remoteRow)
            .Select(cell => NormalizeHeader(cell.FieldName))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Append(currentFieldKey)
            .Distinct(StringComparer.Ordinal)
            .Select(key => new
            {
                Key = key,
                Column = MinColumnForField(key, baseRow, localRow, remoteRow),
                FieldName = FirstFieldNameForKey(key, currentFieldName, baseRow, localRow, remoteRow),
            })
            .OrderBy(field => field.Column)
            .ThenBy(field => field.FieldName, StringComparer.Ordinal)
            .ToList();

        var fields = fieldKeys
            .Select(field => new SpreadsheetMergeRowContextField(
                field.FieldName,
                field.Column,
                field.Column >= 0 ? ExcelDiffService.ToColumnName(field.Column) : "",
                RowContextValue(baseRow, field.Key),
                RowContextValue(localRow, field.Key),
                RowContextValue(remoteRow, field.Key),
                string.Equals(field.Key, currentFieldKey, StringComparison.Ordinal)))
            .ToList();
        return new SpreadsheetMergeRowContext(fields);
    }

    private static IReadOnlyList<SpreadsheetMergeRawCell> GetRowContextCells(
        IReadOnlyList<SpreadsheetMergeRawCell> rawCells,
        string rowMergeKey,
        ExcelCellKey? fallbackCell)
    {
        if (!string.IsNullOrWhiteSpace(rowMergeKey))
        {
            var semanticRow = rawCells
                .Where(cell => string.Equals(cell.RowMergeKey, rowMergeKey, StringComparison.Ordinal))
                .OrderBy(cell => cell.Cell.Column)
                .ToList();
            if (semanticRow.Count > 0)
            {
                return semanticRow;
            }
        }

        if (fallbackCell == null)
        {
            return [];
        }

        return rawCells
            .Where(cell => string.Equals(cell.Cell.Sheet, fallbackCell.Sheet, StringComparison.Ordinal) && cell.Cell.Row == fallbackCell.Row)
            .OrderBy(cell => cell.Cell.Column)
            .ToList();
    }

    private static int MinColumnForField(
        string fieldKey,
        params IReadOnlyList<SpreadsheetMergeRawCell>[] rows)
    {
        return rows
            .SelectMany(row => row)
            .Where(cell => string.Equals(NormalizeHeader(cell.FieldName), fieldKey, StringComparison.Ordinal))
            .Select(cell => cell.Cell.Column)
            .DefaultIfEmpty(-1)
            .Min();
    }

    private static string FirstFieldNameForKey(
        string fieldKey,
        string currentFieldName,
        params IReadOnlyList<SpreadsheetMergeRawCell>[] rows)
    {
        if (string.Equals(fieldKey, NormalizeHeader(currentFieldName), StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(currentFieldName))
        {
            return currentFieldName;
        }

        return rows
            .SelectMany(row => row)
            .Where(cell => string.Equals(NormalizeHeader(cell.FieldName), fieldKey, StringComparison.Ordinal))
            .Select(cell => cell.FieldName)
            .FirstOrDefault(field => !string.IsNullOrWhiteSpace(field)) ?? fieldKey;
    }

    private static string RowContextValue(IReadOnlyList<SpreadsheetMergeRawCell> row, string fieldKey)
    {
        var values = row
            .Where(cell => string.Equals(NormalizeHeader(cell.FieldName), fieldKey, StringComparison.Ordinal))
            .OrderBy(cell => cell.Cell.Column)
            .Select(cell => cell.Value)
            .ToList();
        if (values.Count <= 1)
        {
            return values.FirstOrDefault() ?? "";
        }

        return string.Join(" | ", values);
    }

    private static SpreadsheetMergeResolvedTarget ResolveTargetCell(
        SpreadsheetMergeCell? localCell,
        SpreadsheetMergeCell? baseCell,
        SpreadsheetMergeCell? remoteCell,
        Dictionary<string, ExcelCellKey> localRows,
        Dictionary<string, int> nextAppendRowBySheet,
        Dictionary<string, int> suggestedRows)
    {
        if (localCell != null)
        {
            return new SpreadsheetMergeResolvedTarget(localCell.Cell, true, true, localCell.RowMergeKey);
        }

        var template = remoteCell ?? baseCell;
        if (template == null)
        {
            throw new InvalidOperationException("无法定位合并单元格。");
        }

        var rowMergeKey = FirstNonEmpty(template.RowMergeKey, baseCell?.RowMergeKey, remoteCell?.RowMergeKey);
        if (!string.IsNullOrWhiteSpace(rowMergeKey) &&
            localRows.TryGetValue(rowMergeKey, out var localRow))
        {
            return new SpreadsheetMergeResolvedTarget(
                new ExcelCellKey(localRow.Sheet, localRow.Row, template.Cell.Column),
                false,
                true,
                rowMergeKey);
        }

        var sheet = template.Cell.Sheet;
        if (!nextAppendRowBySheet.TryGetValue(sheet, out var nextRow))
        {
            nextRow = 0;
        }

        var appendKey = string.IsNullOrWhiteSpace(rowMergeKey)
            ? $"P|{template.Cell.Sheet}|{template.Cell.Row}"
            : rowMergeKey;
        if (!suggestedRows.TryGetValue(appendKey, out var appendRow))
        {
            appendRow = nextRow;
            suggestedRows[appendKey] = appendRow;
            nextAppendRowBySheet[sheet] = appendRow + 1;
        }

        return new SpreadsheetMergeResolvedTarget(
            new ExcelCellKey(sheet, appendRow, template.Cell.Column),
            false,
            false,
            appendKey);
    }

    private static SpreadsheetMergeCell? PickCell(
        string key,
        Dictionary<string, SpreadsheetMergeCell> localCells,
        Dictionary<string, SpreadsheetMergeCell> baseCells,
        Dictionary<string, SpreadsheetMergeCell> remoteCells)
    {
        if (localCells.TryGetValue(key, out var localCell))
        {
            return localCell;
        }

        if (baseCells.TryGetValue(key, out var baseCell))
        {
            return baseCell;
        }

        return remoteCells.TryGetValue(key, out var remoteCell) ? remoteCell : null;
    }

    private static string GetCellValue(Dictionary<ExcelCellKey, string> cells, string sheet, int row, int column)
    {
        return cells.TryGetValue(new ExcelCellKey(sheet, row, column), out var value) ? value : "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static void ApplyWorkbookWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        ValidateMergeInputs(localFilePath);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        IWorkbook workbook;
        using (var input = File.OpenRead(localFilePath))
        {
            workbook = WorkbookFactory.Create(input);
        }

        foreach (var delete in writes
            .Where(write => write.Kind == SpreadsheetMergeWriteKind.DeleteRow)
            .GroupBy(write => new ExcelRowKey(write.Cell.Sheet, write.Cell.Row))
            .OrderByDescending(group => group.Key.Sheet)
            .ThenByDescending(group => group.Key.Row))
        {
            var sheet = workbook.GetSheet(delete.Key.Sheet);
            if (sheet != null)
            {
                DeleteWorkbookRow(sheet, delete.Key.Row);
            }
        }

        foreach (var insert in writes
            .Where(write => write.Kind == SpreadsheetMergeWriteKind.InsertRow)
            .GroupBy(write => new ExcelRowKey(write.Cell.Sheet, write.Cell.Row))
            .OrderByDescending(group => group.Key.Sheet)
            .ThenByDescending(group => group.Key.Row))
        {
            var sheet = workbook.GetSheet(insert.Key.Sheet) ?? workbook.CreateSheet(insert.Key.Sheet);
            InsertWorkbookRow(sheet, insert.Key.Row);
            foreach (var write in insert)
            {
                SetWorkbookCell(sheet, write.Cell.Row, write.Cell.Column, write.Value);
            }
        }

        foreach (var write in writes.Where(write => write.Kind == SpreadsheetMergeWriteKind.SetCell))
        {
            var sheet = workbook.GetSheet(write.Cell.Sheet) ?? workbook.CreateSheet(write.Cell.Sheet);
            SetWorkbookCell(sheet, write.Cell.Row, write.Cell.Column, write.Value);
        }

        using var output = File.Create(localFilePath);
        workbook.Write(output);
        workbook.Close();
    }

    private static void SetWorkbookCell(ISheet sheet, int rowIndex, int columnIndex, string value)
    {
        var row = GetOrCreateWorkbookRow(sheet, rowIndex, preferNextTemplate: false);
        var cell = row.GetCell(columnIndex);
        if (cell == null)
        {
            cell = row.CreateCell(columnIndex);
            CopyWorkbookCellStyle(FindWorkbookCellTemplate(sheet, rowIndex, columnIndex), cell);
        }

        var valueTemplate = FindWorkbookValueTemplate(sheet, rowIndex, columnIndex) ?? cell;
        SetWorkbookCellValue(cell, value, valueTemplate);
    }

    private static void InsertWorkbookRow(ISheet sheet, int rowIndex)
    {
        if (rowIndex <= sheet.LastRowNum)
        {
            sheet.ShiftRows(rowIndex, sheet.LastRowNum, 1, true, false);
        }

        CreateWorkbookRowFromTemplate(sheet, rowIndex, preferNextTemplate: true);
    }

    private static void DeleteWorkbookRow(ISheet sheet, int rowIndex)
    {
        var row = sheet.GetRow(rowIndex);
        if (row != null)
        {
            sheet.RemoveRow(row);
        }

        if (rowIndex < sheet.LastRowNum)
        {
            sheet.ShiftRows(rowIndex + 1, sheet.LastRowNum, -1, true, false);
        }
    }

    private static IRow GetOrCreateWorkbookRow(ISheet sheet, int rowIndex, bool preferNextTemplate)
    {
        return sheet.GetRow(rowIndex) ?? CreateWorkbookRowFromTemplate(sheet, rowIndex, preferNextTemplate);
    }

    private static IRow CreateWorkbookRowFromTemplate(ISheet sheet, int rowIndex, bool preferNextTemplate)
    {
        var row = sheet.CreateRow(rowIndex);
        var template = FindWorkbookRowTemplate(sheet, rowIndex, preferNextTemplate);
        if (template == null)
        {
            return row;
        }

        row.Height = template.Height;
        row.ZeroHeight = template.ZeroHeight;
        if (template.RowStyle != null)
        {
            row.RowStyle = template.RowStyle;
        }

        if (template.FirstCellNum < 0 || template.LastCellNum < 0)
        {
            return row;
        }

        for (var column = template.FirstCellNum; column < template.LastCellNum; column++)
        {
            var templateCell = template.GetCell(column);
            if (templateCell == null)
            {
                continue;
            }

            var cell = row.CreateCell(column, CellType.Blank);
            CopyWorkbookCellStyle(templateCell, cell);
        }

        return row;
    }

    private static IRow? FindWorkbookRowTemplate(ISheet sheet, int rowIndex, bool preferNextTemplate)
    {
        var next = rowIndex + 1;
        var previous = rowIndex - 1;
        if (preferNextTemplate && next <= sheet.LastRowNum)
        {
            var nextRow = sheet.GetRow(next);
            if (nextRow != null)
            {
                return nextRow;
            }
        }

        if (previous >= sheet.FirstRowNum)
        {
            var previousRow = sheet.GetRow(previous);
            if (previousRow != null)
            {
                return previousRow;
            }
        }

        if (!preferNextTemplate && next <= sheet.LastRowNum)
        {
            var nextRow = sheet.GetRow(next);
            if (nextRow != null)
            {
                return nextRow;
            }
        }

        return null;
    }

    private static ICell? FindWorkbookCellTemplate(ISheet sheet, int rowIndex, int columnIndex)
    {
        return FindWorkbookValueTemplate(sheet, rowIndex, columnIndex) ??
            sheet.GetRow(rowIndex - 1)?.GetCell(columnIndex) ??
            sheet.GetRow(rowIndex + 1)?.GetCell(columnIndex);
    }

    private static ICell? FindWorkbookValueTemplate(ISheet sheet, int rowIndex, int columnIndex)
    {
        var current = sheet.GetRow(rowIndex)?.GetCell(columnIndex);
        if (current != null && current.CellType != CellType.Blank)
        {
            return current;
        }

        for (var row = rowIndex - 1; row >= sheet.FirstRowNum; row--)
        {
            var cell = sheet.GetRow(row)?.GetCell(columnIndex);
            if (cell != null && cell.CellType != CellType.Blank)
            {
                return cell;
            }
        }

        for (var row = rowIndex + 1; row <= sheet.LastRowNum; row++)
        {
            var cell = sheet.GetRow(row)?.GetCell(columnIndex);
            if (cell != null && cell.CellType != CellType.Blank)
            {
                return cell;
            }
        }

        return current;
    }

    private static void CopyWorkbookCellStyle(ICell? source, ICell target)
    {
        if (source?.CellStyle != null)
        {
            target.CellStyle = source.CellStyle;
        }
    }

    private static void SetWorkbookCellValue(ICell cell, string value, ICell? template)
    {
        if (string.IsNullOrEmpty(value))
        {
            cell.SetCellType(CellType.Blank);
            return;
        }

        switch (GetWorkbookValueKind(template))
        {
            case WorkbookValueKind.Number:
                if (TryParseDouble(value, out var number))
                {
                    cell.SetCellValue(number);
                    return;
                }

                break;
            case WorkbookValueKind.Boolean:
                if (TryParseBoolean(value, out var boolean))
                {
                    cell.SetCellValue(boolean);
                    return;
                }

                break;
            case WorkbookValueKind.Date:
                if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date) ||
                    DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    cell.SetCellValue(date);
                    return;
                }

                break;
        }

        cell.SetCellValue(value);
    }

    private static WorkbookValueKind GetWorkbookValueKind(ICell? cell)
    {
        if (cell == null)
        {
            return WorkbookValueKind.String;
        }

        if (DateUtil.IsCellDateFormatted(cell))
        {
            return WorkbookValueKind.Date;
        }

        var cellType = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
        return cellType switch
        {
            CellType.Numeric => WorkbookValueKind.Number,
            CellType.Boolean => WorkbookValueKind.Boolean,
            _ => WorkbookValueKind.String,
        };
    }

    private static bool TryParseDouble(string value, out double number)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseBoolean(string value, out bool boolean)
    {
        if (bool.TryParse(value, out boolean))
        {
            return true;
        }

        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            boolean = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            boolean = false;
            return true;
        }

        return false;
    }

    private enum WorkbookValueKind
    {
        String,
        Number,
        Boolean,
        Date,
    }

    private static long GetConfiguredLimitBytes(string variableName, long defaultBytes)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (long.TryParse(value, out var megabytes) && megabytes > 0)
        {
            return megabytes * 1024 * 1024;
        }

        return defaultBytes;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }

    private static void ApplyXmlWrites(string localFilePath, IReadOnlyList<SpreadsheetMergeWrite> writes)
    {
        var document = XDocument.Load(localFilePath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("XML 表格缺少 Workbook 根节点。");
        if (!SpreadsheetXmlFormat.IsSpreadsheetWorkbook(root))
        {
            throw new InvalidOperationException("XML 文件不是可识别的 SpreadsheetML 表格。");
        }

        foreach (var delete in writes
            .Where(write => write.Kind == SpreadsheetMergeWriteKind.DeleteRow)
            .GroupBy(write => new ExcelRowKey(write.Cell.Sheet, write.Cell.Row))
            .OrderByDescending(group => group.Key.Sheet)
            .ThenByDescending(group => group.Key.Row))
        {
            var worksheet = GetOrCreateWorksheet(root, delete.Key.Sheet);
            var table = SpreadsheetXmlFormat.Element(worksheet, "Table");
            if (table != null)
            {
                DeleteXmlRow(table, delete.Key.Row);
            }
        }

        foreach (var insert in writes
            .Where(write => write.Kind == SpreadsheetMergeWriteKind.InsertRow)
            .GroupBy(write => new ExcelRowKey(write.Cell.Sheet, write.Cell.Row))
            .OrderByDescending(group => group.Key.Sheet)
            .ThenByDescending(group => group.Key.Row))
        {
            var worksheet = GetOrCreateWorksheet(root, insert.Key.Sheet);
            var table = SpreadsheetXmlFormat.Element(worksheet, "Table");
            if (table == null)
            {
                table = SpreadsheetXmlFormat.CreateChild(worksheet, "Table");
                worksheet.Add(table);
            }

            InsertXmlRow(table, insert.Key.Row);
            foreach (var write in insert)
            {
                SetXmlCell(table, write.Cell, write.Value);
            }
        }

        foreach (var write in writes.Where(write => write.Kind == SpreadsheetMergeWriteKind.SetCell))
        {
            var worksheet = GetOrCreateWorksheet(root, write.Cell.Sheet);
            var table = SpreadsheetXmlFormat.Element(worksheet, "Table");
            if (table == null)
            {
                table = SpreadsheetXmlFormat.CreateChild(worksheet, "Table");
                worksheet.Add(table);
            }

            SetXmlCell(table, write.Cell, write.Value);
        }

        foreach (var table in SpreadsheetXmlFormat.Descendants(root, "Table"))
        {
            UpdateXmlTableExtents(table);
        }

        document.Save(localFilePath, SaveOptions.DisableFormatting);
    }

    private static void SetXmlCell(XElement table, ExcelCellKey cellKey, string value)
    {
        var row = GetOrCreateXmlRow(table, cellKey.Row);
        var cell = GetOrCreateXmlCell(row, cellKey.Column);
        RemoveXmlFormulaAttributes(cell);
        var data = SpreadsheetXmlFormat.Element(cell, "Data");
        var templateData = SpreadsheetXmlFormat.Element(FindXmlValueTemplate(table, cellKey.Row, cellKey.Column) ?? cell, "Data");
        var normalizedValue = NormalizeXmlCellText(value);
        if (string.IsNullOrEmpty(normalizedValue))
        {
            data?.Remove();
            return;
        }

        if (data == null)
        {
            data = SpreadsheetXmlFormat.CreateChild(cell, "Data");
            CopyXmlAttributes(templateData, data);
            cell.Add(data);
        }

        data.Value = normalizedValue;
        var type = SpreadsheetXmlFormat.AttributeValue(data, "Type");
        var templateType = templateData == null ? "" : SpreadsheetXmlFormat.AttributeValue(templateData, "Type");
        var guessedType = GuessXmlCellType(normalizedValue);
        if (string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(templateType))
        {
            SpreadsheetXmlFormat.SetAttributeValue(data, "Type", templateType);
            type = templateType;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            SpreadsheetXmlFormat.SetAttributeValue(data, "Type", guessedType);
        }
        else if (string.Equals(type, "Number", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(guessedType, "Number", StringComparison.OrdinalIgnoreCase))
        {
            SpreadsheetXmlFormat.SetAttributeValue(data, "Type", guessedType);
        }
    }

    private static void InsertXmlRow(XElement table, int zeroBasedRow)
    {
        var existing = GetXmlRowAt(table, zeroBasedRow);
        var template = existing ?? GetXmlRowAt(table, zeroBasedRow - 1);
        var created = CreateXmlRowFromTemplate(table, zeroBasedRow, template);
        SpreadsheetXmlFormat.SetAttributeValue(created, "Index", (zeroBasedRow + 1).ToString());
        if (existing != null)
        {
            existing.AddBeforeSelf(created);
        }
        else
        {
            table.Add(created);
        }

        NormalizeXmlRowIndexes(table);
    }

    private static void DeleteXmlRow(XElement table, int zeroBasedRow)
    {
        GetXmlRowAt(table, zeroBasedRow)?.Remove();
        NormalizeXmlRowIndexes(table);
    }

    private static XElement? GetXmlRowAt(XElement table, int zeroBasedRow)
    {
        var rowIndex = 0;
        foreach (var row in SpreadsheetXmlFormat.Elements(table, "Row"))
        {
            var explicitIndex = ReadSpreadsheetIndex(row);
            if (explicitIndex.HasValue)
            {
                rowIndex = explicitIndex.Value - 1;
            }

            if (rowIndex == zeroBasedRow)
            {
                return row;
            }

            if (rowIndex > zeroBasedRow)
            {
                return null;
            }

            rowIndex++;
        }

        return null;
    }

    private static void NormalizeXmlRowIndexes(XElement table)
    {
        var rowIndex = 1;
        foreach (var row in SpreadsheetXmlFormat.Elements(table, "Row"))
        {
            SpreadsheetXmlFormat.SetAttributeValue(row, "Index", rowIndex.ToString());
            rowIndex++;
        }
    }

    private static void RemoveXmlFormulaAttributes(XElement cell)
    {
        foreach (var attribute in cell.Attributes()
            .Where(attribute => attribute.Name.LocalName is "Formula" or "ArrayRange")
            .ToList())
        {
            attribute.Remove();
        }
    }

    private static void UpdateXmlTableExtents(XElement table)
    {
        var maxRow = 0;
        var maxColumn = 0;
        var rowIndex = 0;
        foreach (var row in SpreadsheetXmlFormat.Elements(table, "Row"))
        {
            var explicitRowIndex = ReadSpreadsheetIndex(row);
            if (explicitRowIndex.HasValue)
            {
                rowIndex = explicitRowIndex.Value - 1;
            }

            maxRow = Math.Max(maxRow, rowIndex + 1);
            var columnIndex = 0;
            foreach (var cell in SpreadsheetXmlFormat.Elements(row, "Cell"))
            {
                var explicitColumnIndex = ReadSpreadsheetIndex(cell);
                if (explicitColumnIndex.HasValue)
                {
                    columnIndex = explicitColumnIndex.Value - 1;
                }

                maxColumn = Math.Max(maxColumn, columnIndex + 1);
                columnIndex++;
            }

            rowIndex++;
        }

        if (maxRow > 0)
        {
            SpreadsheetXmlFormat.SetAttributeValue(table, "ExpandedRowCount", maxRow.ToString());
        }

        if (maxColumn > 0)
        {
            SpreadsheetXmlFormat.SetAttributeValue(table, "ExpandedColumnCount", maxColumn.ToString());
        }
    }

    private static XElement GetOrCreateWorksheet(XElement root, string sheetName)
    {
        var worksheet = root
            .Elements()
            .Where(element => element.Name.LocalName == "Worksheet")
            .FirstOrDefault(element => string.Equals(SpreadsheetXmlFormat.AttributeValue(element, "Name"), sheetName, StringComparison.Ordinal));
        if (worksheet != null)
        {
            return worksheet;
        }

        worksheet = SpreadsheetXmlFormat.CreateChild(root, "Worksheet");
        SpreadsheetXmlFormat.SetAttributeValue(worksheet, "Name", sheetName);
        root.Add(worksheet);
        return worksheet;
    }

    private static XElement GetOrCreateXmlRow(XElement table, int zeroBasedRow)
    {
        var rowIndex = 0;
        foreach (var row in SpreadsheetXmlFormat.Elements(table, "Row"))
        {
            var explicitIndex = ReadSpreadsheetIndex(row);
            if (explicitIndex.HasValue)
            {
                rowIndex = explicitIndex.Value - 1;
            }

            if (rowIndex == zeroBasedRow)
            {
                return row;
            }

            if (rowIndex > zeroBasedRow)
            {
                var template = GetXmlRowAt(table, zeroBasedRow - 1) ?? row;
                var created = CreateXmlRowFromTemplate(table, zeroBasedRow, template);
                SpreadsheetXmlFormat.SetAttributeValue(created, "Index", (zeroBasedRow + 1).ToString());
                row.AddBeforeSelf(created);
                return created;
            }

            rowIndex++;
        }

        var appended = CreateXmlRowFromTemplate(table, zeroBasedRow, GetXmlRowAt(table, zeroBasedRow - 1));
        SpreadsheetXmlFormat.SetAttributeValue(appended, "Index", (zeroBasedRow + 1).ToString());
        table.Add(appended);
        return appended;
    }

    private static XElement GetOrCreateXmlCell(XElement row, int zeroBasedColumn)
    {
        var columnIndex = 0;
        foreach (var cell in SpreadsheetXmlFormat.Elements(row, "Cell"))
        {
            var explicitIndex = ReadSpreadsheetIndex(cell);
            if (explicitIndex.HasValue)
            {
                columnIndex = explicitIndex.Value - 1;
            }

            if (columnIndex == zeroBasedColumn)
            {
                return cell;
            }

            if (columnIndex > zeroBasedColumn)
            {
                var template = FindXmlCellTemplate(row, zeroBasedColumn);
                var created = CreateXmlCellFromTemplate(row, template);
                SpreadsheetXmlFormat.SetAttributeValue(created, "Index", (zeroBasedColumn + 1).ToString());
                cell.AddBeforeSelf(created);
                return created;
            }

            columnIndex++;
        }

        var appended = CreateXmlCellFromTemplate(row, FindXmlCellTemplate(row, zeroBasedColumn));
        SpreadsheetXmlFormat.SetAttributeValue(appended, "Index", (zeroBasedColumn + 1).ToString());
        row.Add(appended);
        return appended;
    }

    private static XElement CreateXmlRowFromTemplate(XElement table, int zeroBasedRow, XElement? template)
    {
        var row = SpreadsheetXmlFormat.CreateChild(table, "Row");
        CopyXmlAttributes(template, row, "Index");

        if (template == null)
        {
            return row;
        }

        foreach (var templateCell in SpreadsheetXmlFormat.Elements(template, "Cell"))
        {
            var cell = CreateXmlCellFromTemplate(row, templateCell);
            var explicitIndex = ReadSpreadsheetIndex(templateCell);
            if (explicitIndex.HasValue)
            {
                SpreadsheetXmlFormat.SetAttributeValue(cell, "Index", explicitIndex.Value.ToString(CultureInfo.InvariantCulture));
            }

            row.Add(cell);
        }

        return row;
    }

    private static XElement CreateXmlCellFromTemplate(XElement row, XElement? template)
    {
        var cell = SpreadsheetXmlFormat.CreateChild(row, "Cell");
        CopyXmlAttributes(template, cell, "Formula", "ArrayRange", "Index");
        return cell;
    }

    private static XElement? FindXmlCellTemplate(XElement row, int zeroBasedColumn)
    {
        return GetXmlCellAt(row, zeroBasedColumn) ??
            FindXmlValueTemplate(row.Parent, GetXmlRowPosition(row), zeroBasedColumn);
    }

    private static XElement? FindXmlValueTemplate(XElement? table, int zeroBasedRow, int zeroBasedColumn)
    {
        if (table == null)
        {
            return null;
        }

        var current = GetXmlCellAt(GetXmlRowAt(table, zeroBasedRow), zeroBasedColumn);
        if (current != null && SpreadsheetXmlFormat.Element(current, "Data") != null)
        {
            return current;
        }

        for (var row = zeroBasedRow - 1; row >= 0; row--)
        {
            var cell = GetXmlCellAt(GetXmlRowAt(table, row), zeroBasedColumn);
            if (cell != null && SpreadsheetXmlFormat.Element(cell, "Data") != null)
            {
                return cell;
            }
        }

        var maxRow = SpreadsheetXmlFormat.Elements(table, "Row").Count() + Math.Max(0, zeroBasedRow + 2);
        for (var row = zeroBasedRow + 1; row <= maxRow; row++)
        {
            var cell = GetXmlCellAt(GetXmlRowAt(table, row), zeroBasedColumn);
            if (cell != null && SpreadsheetXmlFormat.Element(cell, "Data") != null)
            {
                return cell;
            }
        }

        return current;
    }

    private static XElement? GetXmlCellAt(XElement? row, int zeroBasedColumn)
    {
        if (row == null)
        {
            return null;
        }

        var columnIndex = 0;
        foreach (var cell in SpreadsheetXmlFormat.Elements(row, "Cell"))
        {
            var explicitIndex = ReadSpreadsheetIndex(cell);
            if (explicitIndex.HasValue)
            {
                columnIndex = explicitIndex.Value - 1;
            }

            if (columnIndex == zeroBasedColumn)
            {
                return cell;
            }

            if (columnIndex > zeroBasedColumn)
            {
                return null;
            }

            columnIndex++;
        }

        return null;
    }

    private static int GetXmlRowPosition(XElement row)
    {
        var table = row.Parent;
        if (table == null)
        {
            return -1;
        }

        var rowIndex = 0;
        foreach (var current in SpreadsheetXmlFormat.Elements(table, "Row"))
        {
            var explicitIndex = ReadSpreadsheetIndex(current);
            if (explicitIndex.HasValue)
            {
                rowIndex = explicitIndex.Value - 1;
            }

            if (ReferenceEquals(current, row))
            {
                return rowIndex;
            }

            rowIndex++;
        }

        return -1;
    }

    private static void CopyXmlAttributes(XElement? source, XElement target, params string[] skippedLocalNames)
    {
        if (source == null)
        {
            return;
        }

        var skipped = skippedLocalNames.Length == 0
            ? null
            : new HashSet<string>(skippedLocalNames, StringComparer.Ordinal);
        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration ||
                skipped?.Contains(attribute.Name.LocalName) == true)
            {
                continue;
            }

            target.SetAttributeValue(attribute.Name, attribute.Value);
        }
    }

    private static int? ReadSpreadsheetIndex(XElement element)
    {
        var value = SpreadsheetXmlFormat.AttributeValue(element, "Index");
        return int.TryParse(value, out var index) ? index : null;
    }

    private static string NormalizeXmlCellText(string value)
    {
        return SpreadsheetXmlFormat.NormalizeCellText(value);
    }

    private static string GuessXmlCellType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "String";
        }

        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? "Number"
            : "String";
    }
}

