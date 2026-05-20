namespace SVNManager;

internal static class DiffPreviewViewFactory
{
    public static Control Create(DiffPreviewData data)
    {
        if (data.BinaryStatus != null)
        {
            return CreateBinaryView(data.BinaryStatus);
        }

        if (data.SpreadsheetReport != null)
        {
            return ExcelDiffForm.CreateExcelDiffView(data.SpreadsheetReport);
        }

        if (data.ExcelDifferences != null)
        {
            return ExcelDiffForm.CreateExcelDiffView(data.ExcelDifferences);
        }

        return data.TextContent != null
            ? TextDiffForm.CreateTextDiffView(data.TextContent)
            : TextDiffForm.CreateTextDiffView(data.TextDifferences ?? []);
    }

    private static Control CreateBinaryView(BinaryDiffStatus status)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24),
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label
        {
            Text = "二进制文件不支持文本差异预览",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = $"旧文件：{status.OldPath}  ({FormatBytes(status.OldSize)})",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
        }, 0, 1);
        root.Controls.Add(new Label
        {
            Text = $"新文件：{status.NewPath}  ({FormatBytes(status.NewSize)})",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
        }, 0, 2);
        root.Controls.Add(new Label
        {
            Text = "已跳过文本解码和行级 diff，避免图片、DLL、压缩包等文件导致乱码或长时间卡顿。",
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(100, 116, 139),
        }, 0, 3);
        return root;
    }

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
