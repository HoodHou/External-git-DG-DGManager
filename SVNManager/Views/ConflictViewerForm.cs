using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class ConflictFileSet
{
    public required string RelativePath { get; init; }
    public required string CurrentPath { get; init; }
    public required string MinePath { get; init; }
    public required string? BasePath { get; init; }
    public required string? ServerPath { get; init; }

    public static ConflictFileSet? Find(string workingCopyPath, string selectedRelativePath)
    {
        var baseRelativePath = NormalizeSelectedConflictPath(selectedRelativePath);
        var currentPath = Path.Combine(workingCopyPath, baseRelativePath);
        var directory = Path.GetDirectoryName(currentPath);
        var fileName = Path.GetFileName(currentPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var minePath = currentPath + ".mine";
        var revisionFiles = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, fileName + ".r*")
                .Select(path => new RevisionConflictFile(path, ParseRevisionSuffix(path)))
                .Where(file => file.Revision > 0)
                .OrderBy(file => file.Revision)
                .ToList()
            : [];

        if (!File.Exists(minePath) || revisionFiles.Count == 0)
        {
            return null;
        }

        var basePath = revisionFiles.Count >= 2 ? revisionFiles[0].Path : null;
        var serverPath = revisionFiles[^1].Path;
        return new ConflictFileSet
        {
            RelativePath = baseRelativePath,
            CurrentPath = currentPath,
            MinePath = minePath,
            BasePath = basePath,
            ServerPath = serverPath,
        };
    }

    private static string NormalizeSelectedConflictPath(string path)
    {
        var result = path;
        if (result.EndsWith(".mine", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^5];
        }
        else
        {
            var fileName = Path.GetFileName(result);
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\.r\d+$");
            if (match.Success)
            {
                result = result[..^match.Value.Length];
            }
        }

        return result;
    }

    private static long ParseRevisionSuffix(string path)
    {
        var fileName = Path.GetFileName(path);
        var marker = fileName.LastIndexOf(".r", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && long.TryParse(fileName[(marker + 2)..], out var revision) ? revision : 0;
    }

    private sealed record RevisionConflictFile(string Path, long Revision);
}

internal sealed class ConflictViewerForm : Form
{
    public ConflictViewerForm(ConflictFileSet conflict, Action<ConflictFileSet>? openExternalTool = null)
    {
        Text = $"冲突查看 - {conflict.RelativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1120, 680);
        Size = new Size(1280, 820);
        Font = new Font("Microsoft YaHei UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        tabs.TabPages.Add(CreateSummaryPage(conflict, openExternalTool));

        if (conflict.ServerPath != null)
        {
            tabs.TabPages.Add(CreateDiffPage("我的版本 vs 服务器版本", conflict.MinePath, conflict.ServerPath));
            if (File.Exists(conflict.CurrentPath))
            {
                tabs.TabPages.Add(CreateDiffPage("当前工作文件 vs 服务器版本", conflict.CurrentPath, conflict.ServerPath));
            }
        }

        if (conflict.BasePath != null && conflict.ServerPath != null)
        {
            tabs.TabPages.Add(CreateDiffPage("旧基础版本 vs 服务器版本", conflict.BasePath, conflict.ServerPath));
        }
    }

    private static TabPage CreateSummaryPage(ConflictFileSet conflict, Action<ConflictFileSet>? openExternalTool)
    {
        var page = new TabPage("版本文件");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = openExternalTool == null ? 1 : 2,
            Padding = new Padding(8),
        };
        if (openExternalTool != null)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        }

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        if (openExternalTool != null)
        {
            var button = new Button
            {
                Text = "用分久必合打开我的版本 vs 服务器版本",
                Dock = DockStyle.Left,
                Width = 260,
            };
            button.Click += (_, _) => openExternalTool(conflict);
            root.Controls.Add(button, 0, 0);
        }

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text =
                $"冲突文件：{conflict.RelativePath}{Environment.NewLine}{Environment.NewLine}" +
                $"当前工作文件：{conflict.CurrentPath}{Environment.NewLine}" +
                $"我的版本(.mine)：{conflict.MinePath}{Environment.NewLine}" +
                $"旧基础版本：{conflict.BasePath ?? "未找到"}{Environment.NewLine}" +
                $"服务器版本：{conflict.ServerPath ?? "未找到"}{Environment.NewLine}{Environment.NewLine}" +
                "这里只负责查看，不会修改或自动合并文件。你可以用外部合并工具处理后，再回到主界面标记冲突已解决。",
        };
        root.Controls.Add(text, 0, openExternalTool == null ? 0 : 1);
        return page;
    }

    private static TabPage CreateDiffPage(string title, string oldFilePath, string newFilePath)
    {
        var page = new TabPage(title);
        var diffControl = DiffPreviewViewFactory.Create(Form1.CreateDiffPreviewData(oldFilePath, newFilePath));
        diffControl.Dock = DockStyle.Fill;
        page.Controls.Add(diffControl);
        return page;
    }

    public static DataGridView CreateExcelDiffGrid(IReadOnlyList<ExcelCellDifference> differences)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工作表", DataPropertyName = nameof(ExcelCellDifference.Sheet), Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单元格", DataPropertyName = nameof(ExcelCellDifference.Address), Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(ExcelCellDifference.FieldName), Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = nameof(ExcelCellDifference.RowId), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "旧值", DataPropertyName = nameof(ExcelCellDifference.OldValue), Width = 360 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新值", DataPropertyName = nameof(ExcelCellDifference.NewValue), Width = 360 });
        grid.DataSource = differences.ToList();
        return grid;
    }
}

