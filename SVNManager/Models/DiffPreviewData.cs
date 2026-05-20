using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class DiffPreviewData
{
    private DiffPreviewData(
        SpreadsheetDiffReport? spreadsheetReport,
        IReadOnlyList<ExcelCellDifference>? excelDifferences,
        TextDiffContent? textContent,
        BinaryDiffStatus? binaryStatus)
    {
        SpreadsheetReport = spreadsheetReport;
        ExcelDifferences = excelDifferences;
        TextContent = textContent;
        BinaryStatus = binaryStatus;
    }

    public SpreadsheetDiffReport? SpreadsheetReport { get; }
    public IReadOnlyList<ExcelCellDifference>? ExcelDifferences { get; }
    public TextDiffContent? TextContent { get; }
    public BinaryDiffStatus? BinaryStatus { get; }
    public IReadOnlyList<TextDiffRow>? TextDifferences => TextContent?.Differences;
    public long EstimatedBytes => EstimateBytes();

    public string Summary
    {
        get
        {
            if (BinaryStatus != null)
            {
                return BinaryStatus.Summary;
            }

            if (SpreadsheetReport != null)
            {
                return SpreadsheetReport.Summary;
            }

            if (ExcelDifferences != null)
            {
                return ExcelDifferences.Count == 0 ? "没有发现单元格差异" : $"发现 {ExcelDifferences.Count} 个单元格差异";
            }

            var rows = TextContent?.Differences ?? [];
            return rows.Count == 0 ? "没有发现文本差异" : $"发现 {rows.Count} 行文本差异";
        }
    }

    public static DiffPreviewData FromExcel(IReadOnlyList<ExcelCellDifference> differences)
    {
        var copied = differences.ToList();
        return new DiffPreviewData(SpreadsheetDiffReport.FromLegacy(copied), copied, null, null);
    }

    public static DiffPreviewData FromExcel(SpreadsheetDiffReport report)
    {
        return new DiffPreviewData(report, report.ToLegacyDifferences(), null, null);
    }

    public static DiffPreviewData FromText(TextDiffContent content)
    {
        return new DiffPreviewData(null, null, content with { Differences = content.Differences.ToList() }, null);
    }

    public static DiffPreviewData FromTextRows(IReadOnlyList<TextDiffRow> differences)
    {
        return new DiffPreviewData(
            null,
            null,
            new TextDiffContent("", "", "plaintext", "旧版本", "新版本", differences.ToList()),
            null);
    }

    public static DiffPreviewData FromBinary(BinaryDiffStatus status)
    {
        return new DiffPreviewData(null, null, null, status);
    }

    private long EstimateBytes()
    {
        if (BinaryStatus != null)
        {
            return 2048;
        }

        if (TextContent != null)
        {
            return EstimateString(TextContent.OldText) +
                EstimateString(TextContent.NewText) +
                TextContent.Differences.Sum(row => 128L + EstimateString(row.Content));
        }

        if (SpreadsheetReport != null)
        {
            return SpreadsheetReport.Rows.Sum(row =>
                512L +
                EstimateString(row.Sheet) +
                EstimateString(row.DisplayKey) +
                EstimateString(row.OldRowText) +
                EstimateString(row.NewRowText) +
                row.Cells.Sum(cell =>
                    192L +
                    EstimateString(cell.FieldName) +
                    EstimateString(cell.OldValue) +
                    EstimateString(cell.NewValue)));
        }

        return ExcelDifferences?.Sum(diff =>
            256L +
            EstimateString(diff.Sheet) +
            EstimateString(diff.FieldName) +
            EstimateString(diff.RowId) +
            EstimateString(diff.OldValue) +
            EstimateString(diff.NewValue) +
            EstimateString(diff.OldRowText) +
            EstimateString(diff.NewRowText)) ?? 0;
    }

    private static long EstimateString(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : value.Length * 2L;
    }
}

internal sealed record BinaryDiffStatus(
    string OldPath,
    string NewPath,
    long OldSize,
    long NewSize,
    bool OldIsBinary,
    bool NewIsBinary)
{
    public string Summary =>
        $"二进制文件不支持文本对比：旧 {FormatBytes(OldSize)} / 新 {FormatBytes(NewSize)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

