using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class TextDiffForm : Form
{
    public TextDiffForm(string title, DiffPreviewData data)
    {
        BuildContent(title, data.Summary, DiffPreviewViewFactory.Create(data));
    }

    public TextDiffForm(string title, IReadOnlyList<TextDiffRow> differences)
    {
        BuildContent(
            title,
            differences.Count == 0 ? "没有发现文本差异" : $"发现 {differences.Count} 行文本差异",
            CreateTextDiffView(differences));
    }

    private void BuildContent(string title, string summary, Control content)
    {
        Text = $"文本差异 - {title}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1160, 720);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = summary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(content, 0, 1);
    }

    public static Control CreateTextDiffView(IReadOnlyList<TextDiffRow> differences)
    {
        var rows = differences.ToList();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 249, 250),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        var searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "搜索行号 / 内容", Margin = new Padding(0, 4, 8, 4) };
        var modeBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 4) };
        modeBox.Items.AddRange(new object[] { "全部", "只看改动", "只看新增", "只看删除" });
        modeBox.SelectedIndex = 0;
        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var countLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        toolbar.Controls.Add(searchBox, 0, 0);
        toolbar.Controls.Add(modeBox, 1, 0);
        toolbar.Controls.Add(clearButton, 2, 0);
        toolbar.Controls.Add(countLabel, 3, 0);
        root.Controls.Add(toolbar, 0, 0);

        var grid = CreateTextDiffGrid();
        root.Controls.Add(grid, 0, 1);

        void ApplyFilter()
        {
            var keyword = searchBox.Text.Trim();
            var mode = modeBox.SelectedItem?.ToString() ?? "全部";
            var filtered = rows.Where(row =>
                    MatchesTextMode(row, mode) &&
                    (string.IsNullOrWhiteSpace(keyword) ||
                        row.LineNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.KindText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            BindTextDiffGrid(grid, filtered);
            countLabel.Text = $"{filtered.Count} / {rows.Count} 行";
        }

        searchBox.TextChanged += (_, _) => ApplyFilter();
        modeBox.SelectedIndexChanged += (_, _) => ApplyFilter();
        clearButton.Click += (_, _) =>
        {
            searchBox.Clear();
            modeBox.SelectedIndex = 0;
        };
        ApplyFilter();
        return root;
    }

    public static Control CreateTextDiffView(TextDiffContent content)
    {
        var currentContent = content;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Color.White,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(0, 2, 0, 2),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var unifiedButton = CreateDiffModeButton("统一视图");
        var splitButton = CreateDiffModeButton("双栏对比");
        var ignoreWhitespaceBox = CreateDiffOptionCheckBox("忽略空白", currentContent.Options.IgnoreWhitespace);
        var ignoreCaseBox = CreateDiffOptionCheckBox("忽略大小写", currentContent.Options.IgnoreCase);
        var ignoreLineEndingsBox = CreateDiffOptionCheckBox("忽略换行", currentContent.Options.IgnoreLineEndings);
        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 0, 8, 0),
        };
        optionsPanel.Controls.Add(ignoreWhitespaceBox);
        optionsPanel.Controls.Add(ignoreCaseBox);
        optionsPanel.Controls.Add(ignoreLineEndingsBox);
        var applyButton = new Button
        {
            Text = "应用",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
        };
        applyButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        var summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = $"{currentContent.OldLabel}  ->  {currentContent.NewLabel}",
        };
        var languageLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = $"语言：{currentContent.Language}",
        };
        toolbar.Controls.Add(unifiedButton, 0, 0);
        toolbar.Controls.Add(splitButton, 1, 0);
        toolbar.Controls.Add(optionsPanel, 2, 0);
        toolbar.Controls.Add(applyButton, 3, 0);
        toolbar.Controls.Add(summaryLabel, 4, 0);
        toolbar.Controls.Add(languageLabel, 5, 0);
        root.Controls.Add(toolbar, 0, 0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        root.Controls.Add(host, 0, 1);
        var activeMode = splitButton;

        void Show(Control control, Button activeButton)
        {
            activeMode = activeButton;
            Form1.ClearControlsDisposing(host);
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
            unifiedButton.Font = new Font(unifiedButton.Font, activeButton == unifiedButton ? FontStyle.Bold : FontStyle.Regular);
            splitButton.Font = new Font(splitButton.Font, activeButton == splitButton ? FontStyle.Bold : FontStyle.Regular);
            unifiedButton.BackColor = activeButton == unifiedButton ? Color.FromArgb(219, 234, 254) : Color.White;
            splitButton.BackColor = activeButton == splitButton ? Color.FromArgb(219, 234, 254) : Color.White;
        }

        DiffOptions BuildOptions()
        {
            return new DiffOptions
            {
                IgnoreWhitespace = ignoreWhitespaceBox.Checked,
                IgnoreCase = ignoreCaseBox.Checked,
                IgnoreLineEndings = ignoreLineEndingsBox.Checked,
                ShowInlineHighlight = currentContent.Options.ShowInlineHighlight,
                ContextLines = currentContent.Options.ContextLines,
            };
        }

        void ShowCurrent(Button modeButton)
        {
            if (modeButton == unifiedButton)
            {
                Show(CreateTextDiffView(currentContent.Differences), unifiedButton);
                return;
            }

            Show(CreateSideBySideTextDiffView(currentContent), splitButton);
        }

        unifiedButton.Click += (_, _) => ShowCurrent(unifiedButton);
        splitButton.Click += (_, _) => ShowCurrent(splitButton);
        applyButton.Click += (_, _) =>
        {
            var options = BuildOptions();
            currentContent = TextDiffService.CreatePreviewFromText(
                currentContent.OldText,
                currentContent.NewText,
                currentContent.Language,
                currentContent.OldLabel,
                currentContent.NewLabel,
                options);
            summaryLabel.Text = $"{currentContent.OldLabel}  ->  {currentContent.NewLabel}  ·  {currentContent.Differences.Count} 行";
            ShowCurrent(activeMode);
        };

        ShowCurrent(splitButton);
        return root;
    }

    private static CheckBox CreateDiffOptionCheckBox(string text, bool isChecked)
    {
        return new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Margin = new Padding(0, 8, 12, 0),
            ForeColor = Color.FromArgb(51, 65, 85),
        };
    }

    private static Button CreateDiffModeButton(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
        };
    }

    private static Control CreateSideBySideTextDiffView(TextDiffContent content)
    {
        var rows = BuildSideBySideRows(content.Differences);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 241, 255);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = content.OldLabel,
            Name = "OldLine",
            DataPropertyName = nameof(TextSideBySideRow.OldLine),
            Width = 72,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "旧内容",
            Name = "OldContent",
            DataPropertyName = nameof(TextSideBySideRow.OldContent),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 260,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = content.NewLabel,
            Name = "NewLine",
            DataPropertyName = nameof(TextSideBySideRow.NewLine),
            Width = 72,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "新内容",
            Name = "NewContent",
            DataPropertyName = nameof(TextSideBySideRow.NewContent),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 260,
        });
        BindSideBySideGrid(grid, rows);
        grid.CellPainting += PaintSideBySideTextDiffCell;
        grid.CellFormatting += FormatSideBySideRow;
        grid.CellValueNeeded += ProvideSideBySideCellValue;
        return grid;
    }

    private static List<TextSideBySideRow> BuildSideBySideRows(IReadOnlyList<TextDiffRow> rows)
    {
        var result = new List<TextSideBySideRow>();
        var index = 0;
        while (index < rows.Count)
        {
            var row = rows[index];
            if (row.Kind == "Hunk")
            {
                result.Add(new TextSideBySideRow(row, row));
                index++;
                continue;
            }

            if (row.Kind == "Removed")
            {
                var removed = new List<TextDiffRow>();
                while (index < rows.Count && rows[index].Kind == "Removed")
                {
                    removed.Add(rows[index++]);
                }

                var added = new List<TextDiffRow>();
                while (index < rows.Count && rows[index].Kind == "Added")
                {
                    added.Add(rows[index++]);
                }

                var pairCount = Math.Max(removed.Count, added.Count);
                for (var pairIndex = 0; pairIndex < pairCount; pairIndex++)
                {
                    result.Add(new TextSideBySideRow(
                        pairIndex < removed.Count ? removed[pairIndex] : null,
                        pairIndex < added.Count ? added[pairIndex] : null));
                }

                continue;
            }

            if (row.Kind == "Added")
            {
                result.Add(new TextSideBySideRow(null, row));
                index++;
                continue;
            }

            result.Add(new TextSideBySideRow(row, row));
            index++;
        }

        return result;
    }

    private static string NormalizeSideBySideContent(TextDiffRow row)
    {
        return row.Content.Length >= 2 && (row.Content[0] == '-' || row.Content[0] == '+' || row.Content[0] == ' ') && row.Content[1] == ' '
            ? row.Content[2..]
            : row.Content;
    }

    private static void BindSideBySideGrid(DataGridView grid, IReadOnlyList<TextSideBySideRow> rows)
    {
        grid.Tag = rows;
        grid.VirtualMode = true;
        grid.RowCount = rows.Count;
    }

    private static TextSideBySideRow? GetSideBySideRow(DataGridView grid, int rowIndex)
    {
        return rowIndex >= 0 &&
            grid.Tag is IReadOnlyList<TextSideBySideRow> rows &&
            rowIndex < rows.Count
                ? rows[rowIndex]
                : null;
    }

    private static void FormatSideBySideRow(object? sender, DataGridViewCellFormattingEventArgs args)
    {
        if (sender is not DataGridView grid || args.CellStyle == null)
        {
            return;
        }

        var row = GetSideBySideRow(grid, args.RowIndex);
        if (row == null)
        {
            return;
        }

        args.CellStyle.BackColor = row.IsHunk ? Color.FromArgb(241, 245, 249) : Color.White;
        args.CellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        args.CellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
    }

    private static void ProvideSideBySideCellValue(object? sender, DataGridViewCellValueEventArgs args)
    {
        if (sender is not DataGridView grid || GetSideBySideRow(grid, args.RowIndex) is not { } row)
        {
            return;
        }

        args.Value = grid.Columns[args.ColumnIndex].Name switch
        {
            "OldLine" => row.OldLine,
            "OldContent" => row.OldContent,
            "NewLine" => row.NewLine,
            "NewContent" => row.NewContent,
            _ => "",
        };
    }

    private static void PaintSideBySideTextDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.Graphics == null)
        {
            return;
        }

        var row = GetSideBySideRow(grid, args.RowIndex);
        if (row == null)
        {
            return;
        }

        var columnName = grid.Columns[args.ColumnIndex].Name;
        var diffRow = columnName.StartsWith("Old", StringComparison.Ordinal) ? row.OldRow : row.NewRow;
        if (diffRow == null)
        {
            args.Handled = true;
            args.PaintBackground(args.ClipBounds, true);
            using var brush = new SolidBrush(Color.FromArgb(248, 250, 252));
            args.Graphics.FillRectangle(brush, args.CellBounds);
            return;
        }

        if (columnName is "OldLine" or "NewLine")
        {
            args.Handled = true;
            args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            var lineText = columnName == "OldLine" ? row.OldLine : row.NewLine;
            using var lineFont = new Font("Consolas", 9F);
            TextRenderer.DrawText(
                args.Graphics,
                lineText,
                lineFont,
                Rectangle.Inflate(args.CellBounds, -6, -1),
                Color.FromArgb(100, 116, 139),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        if (columnName is not "OldContent" and not "NewContent")
        {
            return;
        }

        args.Handled = true;
        var backColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(235, 255, 239),
            "Removed" => Color.FromArgb(255, 239, 241),
            "Hunk" => Color.FromArgb(241, 245, 249),
            _ => Color.White,
        };
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);
        args.Paint(args.CellBounds, (args.PaintParts & ~DataGridViewPaintParts.Background) & ~DataGridViewPaintParts.ContentForeground);
        var textColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(22, 101, 52),
            "Removed" => Color.FromArgb(153, 27, 27),
            "Hunk" => Color.FromArgb(71, 85, 105),
            _ => Color.FromArgb(30, 41, 59),
        };
        var highlightColor = diffRow.Kind switch
        {
            "Added" => Color.FromArgb(187, 247, 208),
            "Removed" => Color.FromArgb(254, 202, 202),
            _ => Color.FromArgb(226, 232, 240),
        };
        using var font = new Font("Consolas", 9F, diffRow.Kind == "Removed" ? FontStyle.Strikeout : FontStyle.Regular);
        DrawTextDiffSegments(
            args.Graphics,
            Rectangle.Inflate(args.CellBounds, -8, -2),
            NormalizeSideBySideContent(diffRow),
            Math.Max(-1, diffRow.HighlightStart - 2),
            diffRow.HighlightLength,
            font,
            textColor,
            highlightColor);
    }

    private static bool MatchesTextMode(TextDiffRow row, string mode)
    {
        return mode switch
        {
            "只看新增" => row.Kind is "Hunk" or "Added",
            "只看删除" => row.Kind is "Hunk" or "Removed",
            "只看改动" => row.Kind is "Hunk" or "Added" or "Removed",
            _ => true,
        };
    }

    public static DataGridView CreateTextDiffGrid()
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
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
        };
        grid.EnableHeadersVisualStyles = false;
        grid.VirtualMode = true;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = nameof(TextDiffRow.KindText), Width = 82 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "行号",
            DataPropertyName = nameof(TextDiffRow.LineNumber),
            Width = 110,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9F), Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "内容",
            Name = "Content",
            DataPropertyName = nameof(TextDiffRow.Content),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 520,
        });
        grid.CellPainting += PaintTextDiffCell;
        grid.CellValueNeeded += ProvideTextDiffCellValue;
        grid.CellFormatting += FormatTextDiffRow;
        return grid;
    }

    private static void BindTextDiffGrid(DataGridView grid, IReadOnlyList<TextDiffRow> rows)
    {
        grid.Tag = rows;
        grid.RowCount = rows.Count;
    }

    private static TextDiffRow? GetTextDiffRow(DataGridView grid, int rowIndex)
    {
        return rowIndex >= 0 &&
            grid.Tag is IReadOnlyList<TextDiffRow> rows &&
            rowIndex < rows.Count
                ? rows[rowIndex]
                : null;
    }

    private static void ProvideTextDiffCellValue(object? sender, DataGridViewCellValueEventArgs args)
    {
        if (sender is not DataGridView grid || GetTextDiffRow(grid, args.RowIndex) is not { } row)
        {
            return;
        }

        args.Value = grid.Columns[args.ColumnIndex].DataPropertyName switch
        {
            nameof(TextDiffRow.KindText) => row.KindText,
            nameof(TextDiffRow.LineNumber) => row.LineNumber,
            nameof(TextDiffRow.Content) => row.Content,
            _ => "",
        };
    }

    private static void FormatTextDiffRow(object? sender, DataGridViewCellFormattingEventArgs args)
    {
        if (sender is not DataGridView grid || args.CellStyle == null || GetTextDiffRow(grid, args.RowIndex) is not { } row)
        {
            return;
        }

        args.CellStyle.BackColor = row.Kind switch
        {
            "Added" => Color.FromArgb(235, 255, 239),
            "Removed" => Color.FromArgb(255, 239, 241),
            "Hunk" => Color.FromArgb(241, 245, 249),
            "Context" => Color.White,
            _ => Color.White,
        };
        args.CellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        args.CellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        args.CellStyle.Font = grid.Font;
    }

    private static void PaintTextDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.Graphics == null ||
            grid.Columns[args.ColumnIndex].Name != "Content")
        {
            return;
        }

        var row = GetTextDiffRow(grid, args.RowIndex);
        if (row == null)
        {
            return;
        }

        args.Handled = true;
        args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var bounds = Rectangle.Inflate(args.CellBounds, -8, -2);
        var font = new Font("Consolas", 9F, row.Kind == "Removed" ? FontStyle.Strikeout : FontStyle.Regular);
        try
        {
            var textColor = row.Kind switch
            {
                "Added" => Color.FromArgb(22, 101, 52),
                "Removed" => Color.FromArgb(153, 27, 27),
                "Hunk" => Color.FromArgb(71, 85, 105),
                _ => Color.FromArgb(30, 41, 59),
            };
            var highlightColor = row.Kind switch
            {
                "Added" => Color.FromArgb(187, 247, 208),
                "Removed" => Color.FromArgb(254, 202, 202),
                _ => Color.FromArgb(226, 232, 240),
            };
            DrawTextDiffSegments(args.Graphics, bounds, row.Content, row.HighlightStart, row.HighlightLength, font, textColor, highlightColor);
        }
        finally
        {
            font.Dispose();
        }
    }

    private static void DrawTextDiffSegments(Graphics graphics, Rectangle bounds, string value, int highlightStart, int highlightLength, Font font, Color textColor, Color highlightColor)
    {
        value ??= "";
        if (highlightStart < 0 || highlightLength <= 0 || highlightStart >= value.Length)
        {
            TextRenderer.DrawText(graphics, value, font, bounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        highlightLength = Math.Min(highlightLength, value.Length - highlightStart);
        var x = bounds.Left;
        DrawTextDiffPart(graphics, ref x, bounds, value[..highlightStart], font, textColor, null);
        DrawTextDiffPart(graphics, ref x, bounds, value.Substring(highlightStart, highlightLength), font, textColor, highlightColor);
        DrawTextDiffPart(graphics, ref x, bounds, value[(highlightStart + highlightLength)..], font, textColor, null);
    }

    private static void DrawTextDiffPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var size = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(size.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top, width, bounds.Height);
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRectangle(brush, partBounds);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }
}

