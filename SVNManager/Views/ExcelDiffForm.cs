using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class ExcelDiffForm : Form
{
    public ExcelDiffForm(string relativePath, DiffPreviewData data)
        : this(relativePath, data.SpreadsheetReport ?? SpreadsheetDiffReport.FromLegacy(data.ExcelDifferences ?? []))
    {
    }

    public ExcelDiffForm(string relativePath, SpreadsheetDiffReport report)
    {
        Text = $"表格差异 - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1080, 640);
        Size = new Size(1280, 760);
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
            Text = report.Summary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        root.Controls.Add(CreateExcelDiffView(report), 0, 1);
    }

    public ExcelDiffForm(string relativePath, IReadOnlyList<ExcelCellDifference> differences)
        : this(relativePath, SpreadsheetDiffReport.FromLegacy(differences))
    {
    }

    public static Control CreateExcelDiffView(SpreadsheetDiffReport report)
    {
        var rows = report.Rows.ToList();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 249, 250),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        var searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "搜索 工作表 / ID / 字段 / 新旧值", Margin = new Padding(0, 4, 8, 4) };
        var typeCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 4) };
        typeCombo.Items.AddRange(["全部类型", "只看修改", "只看新增行", "只看删除行", "只看弱对齐"]);
        typeCombo.SelectedIndex = 0;
        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var copyButton = new Button { Text = "复制摘要", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var countLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105) };
        toolbar.Controls.Add(searchBox, 0, 0);
        toolbar.Controls.Add(typeCombo, 1, 0);
        toolbar.Controls.Add(clearButton, 2, 0);
        toolbar.Controls.Add(copyButton, 3, 0);
        toolbar.Controls.Add(countLabel, 4, 0);
        root.Controls.Add(toolbar, 0, 0);

        var selectedSheet = "";
        var sheetTabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(248, 249, 250),
            Padding = new Padding(0, 3, 0, 3),
        };
        root.Controls.Add(sheetTabs, 0, 1);

        var grid = CreateSpreadsheetDiffGrid();
        root.Controls.Add(grid, 0, 2);

        var detailTabs = new TabControl { Dock = DockStyle.Fill };
        var fieldsPage = new TabPage("字段横向核对");
        var structuredPage = new TabPage("子表 / 长字段");
        var fieldGrid = CreateSpreadsheetFieldGrid();
        fieldsPage.Controls.Add(fieldGrid);
        structuredPage.Controls.Add(CreateStructuredDetailPanel(out var structuredSelector, out var structuredOldBox, out var structuredNewBox, out var structuredSegmentsGrid));
        detailTabs.TabPages.Add(fieldsPage);
        detailTabs.TabPages.Add(structuredPage);
        root.Controls.Add(detailTabs, 0, 3);

        var visibleRows = rows;
        SpreadsheetDiffRow? selectedRow = null;

        void UpdateDetails(SpreadsheetDiffRow? row)
        {
            selectedRow = row;
            UpdateSpreadsheetFieldGrid(fieldGrid, row);
            UpdateStructuredSelector(structuredSelector, structuredOldBox, structuredNewBox, structuredSegmentsGrid, row);
        }

        void StyleSheetTabs()
        {
            foreach (var button in sheetTabs.Controls.OfType<Button>())
            {
                var sheet = button.Tag as string ?? "";
                var selected = string.Equals(sheet, selectedSheet, StringComparison.OrdinalIgnoreCase);
                button.BackColor = selected ? Color.FromArgb(219, 234, 254) : Color.White;
                button.ForeColor = selected ? Color.FromArgb(29, 78, 216) : Color.FromArgb(71, 85, 105);
                button.FlatAppearance.BorderColor = selected ? Color.FromArgb(96, 165, 250) : Color.FromArgb(226, 232, 240);
                button.Font = new Font("Microsoft YaHei UI", 8.5F, selected ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        void AddSheetTab(string sheet, int count)
        {
            var text = string.IsNullOrEmpty(sheet) ? "全部" : sheet;
            var button = new Button
            {
                Text = $"{text} ({count})",
                Tag = sheet,
                AutoSize = true,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 6, 0),
                Padding = new Padding(10, 0, 10, 0),
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) =>
            {
                selectedSheet = sheet;
                ApplyFilter();
            };
            sheetTabs.Controls.Add(button);
        }

        AddSheetTab("", rows.Count);
        foreach (var group in rows.GroupBy(row => row.Sheet).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            AddSheetTab(group.Key, group.Count());
        }

        void ApplyFilter()
        {
            var keyword = searchBox.Text.Trim();
            var typeIndex = typeCombo.SelectedIndex;
            var filtered = rows.Where(row =>
                    (string.IsNullOrWhiteSpace(selectedSheet) || string.Equals(row.Sheet, selectedSheet, StringComparison.OrdinalIgnoreCase)) &&
                    MatchesSpreadsheetRowType(row, typeIndex) &&
                    (string.IsNullOrWhiteSpace(keyword) || MatchesSpreadsheetRowKeyword(row, keyword)))
                .ToList();
            visibleRows = filtered;
            BindSpreadsheetDiffGrid(grid, filtered);
            countLabel.Text = $"{filtered.Count} / {rows.Count} 行    修改 {report.ModifiedRowCount} | 新增 {report.AddedRowCount} | 删除 {report.DeletedRowCount} | 弱对齐 {report.WeakAlignedRowCount}";
            if (filtered.Count > 0 && grid.Rows.Count > 0)
            {
                grid.ClearSelection();
                grid.Rows[0].Selected = true;
                grid.CurrentCell = grid.Rows[0].Cells[0];
                UpdateDetails(filtered[0]);
            }
            else
            {
                UpdateDetails(null);
            }

            StyleSheetTabs();
        }

        grid.SelectionChanged += (_, _) =>
        {
            var row = GetSpreadsheetDiffGridRow(grid, grid.CurrentCell?.RowIndex ?? -1);
            if (row != null && !ReferenceEquals(row, selectedRow))
            {
                UpdateDetails(row);
            }
        };
        grid.CellContentClick += (_, args) =>
        {
            if (args.RowIndex >= 0 &&
                args.ColumnIndex >= 0 &&
                grid.Columns[args.ColumnIndex].Name == "Detail" &&
                GetSpreadsheetDiffGridRow(grid, args.RowIndex) is { } row)
            {
                ShowSpreadsheetRowDetail(grid.FindForm(), row);
            }
        };
        grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && GetSpreadsheetDiffGridRow(grid, args.RowIndex) is { } row)
            {
                ShowSpreadsheetRowDetail(grid.FindForm(), row);
            }
        };
        searchBox.TextChanged += (_, _) => ApplyFilter();
        typeCombo.SelectedIndexChanged += (_, _) => ApplyFilter();
        clearButton.Click += (_, _) =>
        {
            searchBox.Clear();
            typeCombo.SelectedIndex = 0;
        };
        copyButton.Click += (_, _) => CopySpreadsheetDiffSummary(root.FindForm(), visibleRows);
        structuredSelector.SelectedIndexChanged += (_, _) => UpdateStructuredDetail(structuredSelector, structuredOldBox, structuredNewBox, structuredSegmentsGrid);
        ApplyFilter();
        return root;
    }

    private static bool MatchesSpreadsheetRowType(SpreadsheetDiffRow row, int typeIndex)
    {
        return typeIndex switch
        {
            1 => row.ChangeKind == SpreadsheetDiffChangeKind.Modified,
            2 => row.ChangeKind == SpreadsheetDiffChangeKind.Added,
            3 => row.ChangeKind == SpreadsheetDiffChangeKind.Deleted,
            4 => row.AlignmentKind == SpreadsheetDiffAlignmentKind.Weak,
            _ => true,
        };
    }

    private static bool MatchesSpreadsheetRowKeyword(SpreadsheetDiffRow row, string keyword)
    {
        return row.Sheet.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.DisplayKey.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.AddressText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.ChangedFieldsSummary.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.OldRowText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.NewRowText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            row.Cells.Any(cell =>
                cell.FieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                cell.OldValue.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                cell.NewValue.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static DataGridView CreateSpreadsheetDiffGrid()
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
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.VirtualMode = true;
        grid.RowTemplate.Height = 42;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工作表", DataPropertyName = nameof(SpreadsheetDiffRow.Sheet), Width = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID / 行标识", DataPropertyName = nameof(SpreadsheetDiffRow.DisplayKey), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "位置", DataPropertyName = nameof(SpreadsheetDiffRow.AddressText), Width = 116 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "改动类型", Name = "ChangeKind", Width = 82 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "对齐", Name = "AlignmentKind", Width = 82 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "改动字段摘要",
            DataPropertyName = nameof(SpreadsheetDiffRow.ChangedFieldsSummary),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 38,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "旧整行",
            DataPropertyName = nameof(SpreadsheetDiffRow.OldRowText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 31,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "新整行",
            DataPropertyName = nameof(SpreadsheetDiffRow.NewRowText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 31,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Name = "Detail", Text = "详情", UseColumnTextForButtonValue = true, Width = 66 });
        grid.CellValueNeeded += ProvideSpreadsheetDiffGridValue;
        grid.CellFormatting += (_, args) =>
        {
            if (args.RowIndex < 0 || args.CellStyle == null || GetSpreadsheetDiffGridRow(grid, args.RowIndex) is not { } row)
            {
                return;
            }

            if (grid.Columns[args.ColumnIndex].Name == "ChangeKind")
            {
                args.Value = SpreadsheetDiffChangeKindLabels.Text(row.ChangeKind);
                args.FormattingApplied = true;
            }
            else if (grid.Columns[args.ColumnIndex].Name == "AlignmentKind")
            {
                args.Value = SpreadsheetDiffAlignmentKindLabels.Text(row.AlignmentKind);
                args.FormattingApplied = true;
            }

            args.CellStyle.BackColor = row.ChangeKind switch
            {
                SpreadsheetDiffChangeKind.Added => Color.FromArgb(236, 253, 245),
                SpreadsheetDiffChangeKind.Deleted => Color.FromArgb(255, 241, 242),
                _ => row.AlignmentKind == SpreadsheetDiffAlignmentKind.Weak ? Color.FromArgb(255, 251, 235) : Color.White,
            };
            args.CellStyle.ForeColor = Color.FromArgb(30, 41, 59);
        };
        return grid;
    }

    private static void BindSpreadsheetDiffGrid(DataGridView grid, IReadOnlyList<SpreadsheetDiffRow> rows)
    {
        grid.Tag = rows;
        grid.RowCount = rows.Count;
    }

    private static SpreadsheetDiffRow? GetSpreadsheetDiffGridRow(DataGridView grid, int rowIndex)
    {
        return rowIndex >= 0 &&
            grid.Tag is IReadOnlyList<SpreadsheetDiffRow> rows &&
            rowIndex < rows.Count
                ? rows[rowIndex]
                : null;
    }

    private static void ProvideSpreadsheetDiffGridValue(object? sender, DataGridViewCellValueEventArgs args)
    {
        if (sender is not DataGridView grid || GetSpreadsheetDiffGridRow(grid, args.RowIndex) is not { } row)
        {
            return;
        }

        args.Value = grid.Columns[args.ColumnIndex].Name switch
        {
            "ChangeKind" => SpreadsheetDiffChangeKindLabels.Text(row.ChangeKind),
            "AlignmentKind" => SpreadsheetDiffAlignmentKindLabels.Text(row.AlignmentKind),
            "Detail" => "详情",
            _ => grid.Columns[args.ColumnIndex].DataPropertyName switch
            {
                nameof(SpreadsheetDiffRow.Sheet) => row.Sheet,
                nameof(SpreadsheetDiffRow.DisplayKey) => row.DisplayKey,
                nameof(SpreadsheetDiffRow.AddressText) => row.AddressText,
                nameof(SpreadsheetDiffRow.ChangedFieldsSummary) => row.ChangedFieldsSummary,
                nameof(SpreadsheetDiffRow.OldRowText) => row.OldRowText,
                nameof(SpreadsheetDiffRow.NewRowText) => row.NewRowText,
                _ => "",
            },
        };
    }

    private static DataGridView CreateSpreadsheetFieldGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.Single,
            GridColor = Color.FromArgb(226, 232, 240),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.RowTemplate.Height = 42;
        grid.DefaultCellStyle.Font = new Font("Consolas", 9F);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(SpreadsheetDiffCell.FieldName), Width = 150, Frozen = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "列", DataPropertyName = nameof(SpreadsheetDiffCell.ColumnName), Width = 54 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Name = "CellKind", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "旧值", DataPropertyName = nameof(SpreadsheetDiffCell.OldValue), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50, MinimumWidth = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新值", DataPropertyName = nameof(SpreadsheetDiffCell.NewValue), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50, MinimumWidth = 220 });
        grid.CellFormatting += (_, args) =>
        {
            if (args.RowIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not SpreadsheetDiffCell cell)
            {
                return;
            }

            if (grid.Columns[args.ColumnIndex].Name == "CellKind")
            {
                args.Value = cell.Kind switch
                {
                    SpreadsheetDiffCellKind.Added => "新增",
                    SpreadsheetDiffCellKind.Deleted => "删除",
                    SpreadsheetDiffCellKind.Modified => "修改",
                    _ => "相同",
                };
                args.FormattingApplied = true;
            }
        };
        grid.DataBindingComplete += (_, _) => ApplySpreadsheetFieldStyles(grid);
        return grid;
    }

    private static void UpdateSpreadsheetFieldGrid(DataGridView grid, SpreadsheetDiffRow? row)
    {
        grid.DataSource = row?.Cells.ToList() ?? [];
    }

    private static void ApplySpreadsheetFieldStyles(DataGridView grid)
    {
        foreach (DataGridViewRow gridRow in grid.Rows)
        {
            if (gridRow.DataBoundItem is not SpreadsheetDiffCell cell)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = cell.Kind switch
            {
                SpreadsheetDiffCellKind.Added => Color.FromArgb(236, 253, 245),
                SpreadsheetDiffCellKind.Deleted => Color.FromArgb(255, 241, 242),
                SpreadsheetDiffCellKind.Modified => Color.White,
                _ => Color.FromArgb(248, 250, 252),
            };
            if (cell.Kind != SpreadsheetDiffCellKind.Unchanged)
            {
                gridRow.Cells[3].Style.BackColor = Color.FromArgb(254, 226, 226);
                gridRow.Cells[3].Style.ForeColor = Color.FromArgb(153, 27, 27);
                gridRow.Cells[4].Style.BackColor = Color.FromArgb(220, 252, 231);
                gridRow.Cells[4].Style.ForeColor = Color.FromArgb(22, 101, 52);
            }
        }
    }

    private static Control CreateStructuredDetailPanel(
        out ComboBox selector,
        out RichTextBox oldBox,
        out RichTextBox newBox,
        out DataGridView segmentsGrid)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selector = new ComboBox { Dock = DockStyle.Left, Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 4) };
        root.Controls.Add(selector, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        Form1.SetSplitterDistanceWhenReady(split, 130);
        var textSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
        Form1.SetSplitterDistanceWhenReady(textSplit, 520);
        oldBox = CreateRowPreviewBox(Color.FromArgb(153, 27, 27));
        newBox = CreateRowPreviewBox(Color.FromArgb(22, 101, 52));
        textSplit.Panel1.Controls.Add(oldBox);
        textSplit.Panel2.Controls.Add(newBox);
        split.Panel1.Controls.Add(textSplit);
        segmentsGrid = new DataGridView
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
        };
        segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "片段", DataPropertyName = nameof(SpreadsheetStructuredValueSegment.Key), Width = 150 });
        segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "旧片段", DataPropertyName = nameof(SpreadsheetStructuredValueSegment.OldText), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50 });
        segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新片段", DataPropertyName = nameof(SpreadsheetStructuredValueSegment.NewText), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50 });
        var gridForStyles = segmentsGrid;
        segmentsGrid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewRow row in gridForStyles.Rows)
            {
                if (row.DataBoundItem is not SpreadsheetStructuredValueSegment segment)
                {
                    continue;
                }

                row.DefaultCellStyle.BackColor = segment.Kind switch
                {
                    SpreadsheetDiffCellKind.Added => Color.FromArgb(236, 253, 245),
                    SpreadsheetDiffCellKind.Deleted => Color.FromArgb(255, 241, 242),
                    _ => Color.White,
                };
            }
        };
        split.Panel2.Controls.Add(segmentsGrid);
        root.Controls.Add(split, 0, 1);
        return root;
    }

    private static void UpdateStructuredSelector(ComboBox selector, RichTextBox oldBox, RichTextBox newBox, DataGridView segmentsGrid, SpreadsheetDiffRow? row)
    {
        selector.Items.Clear();
        if (row != null)
        {
            foreach (var cell in row.ChangedCells.Where(cell => cell.IsStructured || cell.OldValue.Length > 80 || cell.NewValue.Length > 80))
            {
                selector.Items.Add(new StructuredCellListItem(cell));
            }
        }

        if (selector.Items.Count > 0)
        {
            selector.SelectedIndex = 0;
        }
        else
        {
            oldBox.Text = "";
            newBox.Text = "";
            segmentsGrid.DataSource = Array.Empty<SpreadsheetStructuredValueSegment>();
        }
    }

    private static void UpdateStructuredDetail(ComboBox selector, RichTextBox oldBox, RichTextBox newBox, DataGridView segmentsGrid)
    {
        if (selector.SelectedItem is not StructuredCellListItem item)
        {
            return;
        }

        oldBox.Text = FormatDiffDetailValue(item.Cell.OldValue);
        newBox.Text = FormatDiffDetailValue(item.Cell.NewValue);
        segmentsGrid.DataSource = item.Cell.StructuredDiff.Segments.ToList();
    }

    private static void ShowSpreadsheetRowDetail(IWin32Window? owner, SpreadsheetDiffRow row)
    {
        using var form = new Form
        {
            Text = $"表格行差异 - {row.Sheet} / {row.DisplayKey}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(1080, 640),
            Size = new Size(1280, 760),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Text = $"{SpreadsheetDiffChangeKindLabels.Text(row.ChangeKind)}    {SpreadsheetDiffAlignmentKindLabels.Text(row.AlignmentKind)}    {row.Sheet} / {row.DisplayKey} / {row.AddressText}    {row.ChangedFieldsSummary}",
        }, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var fieldsPage = new TabPage("字段横向核对");
        var fieldGrid = CreateSpreadsheetFieldGrid();
        fieldsPage.Controls.Add(fieldGrid);
        UpdateSpreadsheetFieldGrid(fieldGrid, row);
        var structuredPage = new TabPage("子表 / 长字段");
        structuredPage.Controls.Add(CreateStructuredDetailPanel(out var selector, out var oldBox, out var newBox, out var segmentsGrid));
        selector.SelectedIndexChanged += (_, _) => UpdateStructuredDetail(selector, oldBox, newBox, segmentsGrid);
        UpdateStructuredSelector(selector, oldBox, newBox, segmentsGrid, row);
        tabs.TabPages.Add(fieldsPage);
        tabs.TabPages.Add(structuredPage);
        root.Controls.Add(tabs, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK });
        root.Controls.Add(buttons, 0, 2);
        form.Controls.Add(root);
        form.AcceptButton = buttons.Controls.OfType<Button>().First();
        form.ShowDialog(owner);
    }

    private static void CopySpreadsheetDiffSummary(IWin32Window? owner, IReadOnlyList<SpreadsheetDiffRow> rows)
    {
        if (rows.Count == 0)
        {
            MessageBox.Show(owner, "当前筛选结果为空，没有可复制的差异。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"表格差异摘要：{rows.Count} 行");
        builder.AppendLine("状态\t对齐\t工作表\tID/行标识\t位置\t字段摘要");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join('\t',
                SpreadsheetDiffChangeKindLabels.Text(row.ChangeKind),
                SpreadsheetDiffAlignmentKindLabels.Text(row.AlignmentKind),
                NormalizeClipboardCell(row.Sheet),
                NormalizeClipboardCell(row.DisplayKey),
                NormalizeClipboardCell(row.AddressText),
                NormalizeClipboardCell(row.ChangedFieldsSummary)));
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"复制失败：{ex.Message}", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(owner, $"已复制 {rows.Count} 行差异摘要。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private sealed class StructuredCellListItem
    {
        public StructuredCellListItem(SpreadsheetDiffCell cell)
        {
            Cell = cell;
        }

        public SpreadsheetDiffCell Cell { get; }

        public override string ToString()
        {
            var suffix = Cell.StructuredDiff.HasSegments ? $"{Cell.StructuredDiff.Segments.Count} 个片段" : "长文本";
            return $"{Cell.FieldName} - {suffix}";
        }
    }

    public ExcelDiffForm(string relativePath, IReadOnlyList<ExcelCellDifference> differences, bool useLegacyView)
    {
        Text = $"Excel 差异 - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1120, 680);
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
            Text = differences.Count == 0 ? "没有发现单元格差异" : $"发现 {differences.Count} 个单元格差异",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(DiffPreviewViewFactory.Create(DiffPreviewData.FromExcel(differences)), 0, 1);
    }

    public static Control CreateExcelDiffView(IReadOnlyList<ExcelCellDifference> differences)
    {
        var rows = differences.Select(ExcelUnifiedDiffRow.FromDifference).ToList();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.FromArgb(248, 249, 250),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        var searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "搜索 ID / 字段 / 新旧值 / 单元格", Margin = new Padding(0, 4, 8, 4) };
        var idBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "只看 ID", Margin = new Padding(0, 4, 8, 4) };
        var fieldBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "只看字段", Margin = new Padding(0, 4, 8, 4) };
        var clearButton = new Button { Text = "清空", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var copyButton = new Button { Text = "复制摘要", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
        var countLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        toolbar.Controls.Add(searchBox, 0, 0);
        toolbar.Controls.Add(idBox, 1, 0);
        toolbar.Controls.Add(fieldBox, 2, 0);
        toolbar.Controls.Add(clearButton, 3, 0);
        toolbar.Controls.Add(copyButton, 4, 0);
        toolbar.Controls.Add(countLabel, 5, 0);
        root.Controls.Add(toolbar, 0, 0);

        var selectedSheet = "";
        var sheetTabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(248, 249, 250),
            Padding = new Padding(0, 3, 0, 3),
        };
        root.Controls.Add(sheetTabs, 0, 1);

        var grid = CreateExcelDiffGrid();
        root.Controls.Add(grid, 0, 2);
        var rowPreview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(0, 6, 0, 0),
        };
        rowPreview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rowPreview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rowPreview.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        rowPreview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var oldRowPreview = CreateRowPreviewBox(Color.FromArgb(153, 27, 27));
        var newRowPreview = CreateRowPreviewBox(Color.FromArgb(22, 101, 52));
        rowPreview.Controls.Add(CreateRowPreviewLabel("旧整行内容", Color.FromArgb(153, 27, 27)), 0, 0);
        rowPreview.Controls.Add(CreateRowPreviewLabel("新整行内容", Color.FromArgb(22, 101, 52)), 1, 0);
        rowPreview.Controls.Add(oldRowPreview, 0, 1);
        rowPreview.Controls.Add(newRowPreview, 1, 1);
        root.Controls.Add(rowPreview, 0, 3);
        var visibleRows = rows;

        void UpdateRowPreview(ExcelUnifiedDiffRow? row)
        {
            oldRowPreview.Text = row?.OldRowText ?? "";
            newRowPreview.Text = row?.NewRowText ?? "";
        }

        void StyleSheetTabs()
        {
            foreach (var button in sheetTabs.Controls.OfType<Button>())
            {
                var sheet = button.Tag as string ?? "";
                var selected = string.Equals(sheet, selectedSheet, StringComparison.OrdinalIgnoreCase);
                button.BackColor = selected ? Color.FromArgb(219, 234, 254) : Color.White;
                button.ForeColor = selected ? Color.FromArgb(29, 78, 216) : Color.FromArgb(180, 83, 9);
                button.FlatAppearance.BorderColor = selected ? Color.FromArgb(96, 165, 250) : Color.FromArgb(226, 232, 240);
                button.Font = new Font("Microsoft YaHei UI", 8.5F, selected ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        void AddSheetTab(string sheet, int count)
        {
            var title = string.IsNullOrEmpty(sheet) ? "全部" : $"* {sheet}";
            var button = new Button
            {
                Text = $"{title} ({count})",
                Tag = sheet,
                AutoSize = true,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 6, 0),
                Padding = new Padding(10, 0, 10, 0),
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) =>
            {
                selectedSheet = sheet;
                ApplyFilter();
            };
            sheetTabs.Controls.Add(button);
        }

        AddSheetTab("", rows.Count);
        foreach (var group in rows.GroupBy(row => row.Sheet).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            AddSheetTab(group.Key, group.Count());
        }

        void ApplyFilter()
        {
            var keyword = searchBox.Text.Trim();
            var id = idBox.Text.Trim();
            var field = fieldBox.Text.Trim();
            var filtered = rows.Where(row =>
                    (string.IsNullOrWhiteSpace(selectedSheet) || string.Equals(row.Sheet, selectedSheet, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(keyword) ||
                        row.Sheet.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.Address.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.FieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.RowId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.OldValue.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.NewValue.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.OldRowText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        row.NewRowText.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(id) || row.RowId.Contains(id, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(field) || row.FieldName.Contains(field, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            visibleRows = filtered;
            grid.DataSource = filtered;
            countLabel.Text = $"{filtered.Count} / {rows.Count} 项";
            if (filtered.Count > 0 && grid.Rows.Count > 0)
            {
                grid.ClearSelection();
                grid.Rows[0].Selected = true;
                grid.CurrentCell = grid.Rows[0].Cells[0];
                UpdateRowPreview(filtered[0]);
            }
            else
            {
                UpdateRowPreview(null);
            }

            StyleSheetTabs();
        }

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.CurrentRow?.DataBoundItem is ExcelUnifiedDiffRow row)
            {
                UpdateRowPreview(row);
            }
        };
        searchBox.TextChanged += (_, _) => ApplyFilter();
        idBox.TextChanged += (_, _) => ApplyFilter();
        fieldBox.TextChanged += (_, _) => ApplyFilter();
        clearButton.Click += (_, _) =>
        {
            searchBox.Clear();
            idBox.Clear();
            fieldBox.Clear();
        };
        copyButton.Click += (_, _) => CopyExcelDiffSummary(root.FindForm(), visibleRows);
        ApplyFilter();
        return root;
    }

    private static Label CreateRowPreviewLabel(string text, Color color)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = color,
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
            Padding = new Padding(8, 0, 0, 0),
        };
    }

    private static RichTextBox CreateRowPreviewBox(Color color)
    {
        return new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = color,
            Font = new Font("Consolas", 9F),
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            DetectUrls = false,
        };
    }

    public static DataGridView CreateExcelDiffGrid()
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
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(226, 232, 240),
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.RowTemplate.Height = 38;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "工作表",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.Sheet),
            Width = 118,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "ID",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.RowId),
            Width = 118,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "字段",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.FieldName),
            Width = 148,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "旧值",
            Name = "OldValue",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.OldValue),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "新值",
            Name = "NewValue",
            DataPropertyName = nameof(ExcelUnifiedDiffRow.NewValue),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            MinimumWidth = 220,
        });
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "",
            Name = "Detail",
            Text = "详情",
            UseColumnTextForButtonValue = true,
            Width = 66,
        });
        grid.CellPainting += PaintExcelDiffCell;
        grid.DataBindingComplete += (_, _) => ApplyExcelRowStyles(grid);
        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not ExcelUnifiedDiffRow row)
            {
                return;
            }

            args.ToolTipText =
                $"工作表：{row.Sheet}{Environment.NewLine}" +
                $"单元格：{row.Address}{Environment.NewLine}" +
                $"字段：{row.FieldName}{Environment.NewLine}" +
                $"ID：{row.RowId}{Environment.NewLine}" +
                $"类型：{row.DetailKind}{Environment.NewLine}" +
                $"旧值：{row.OldValue}{Environment.NewLine}" +
                $"新值：{row.NewValue}{Environment.NewLine}{Environment.NewLine}" +
                $"旧整行：{NormalizeClipboardCell(row.OldRowText)}{Environment.NewLine}" +
                $"新整行：{NormalizeClipboardCell(row.NewRowText)}{Environment.NewLine}{Environment.NewLine}" +
                "双击或点击“详情”查看长文本 / 子表差异。";
        };
        grid.CellContentClick += (_, args) =>
        {
            if (args.RowIndex >= 0 &&
                args.ColumnIndex >= 0 &&
                grid.Columns[args.ColumnIndex].Name == "Detail" &&
                grid.Rows[args.RowIndex].DataBoundItem is ExcelUnifiedDiffRow row)
            {
                ShowExcelDiffDetail(grid.FindForm(), row);
            }
        };
        grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && grid.Rows[args.RowIndex].DataBoundItem is ExcelUnifiedDiffRow row)
            {
                ShowExcelDiffDetail(grid.FindForm(), row);
            }
        };
        return grid;
    }

    private static void ApplyExcelRowStyles(DataGridView grid)
    {
        foreach (DataGridViewRow gridRow in grid.Rows)
        {
            if (gridRow.DataBoundItem is not ExcelUnifiedDiffRow row)
            {
                continue;
            }

            gridRow.Height = row.HasStructuredValue ? 42 : 38;
            gridRow.DefaultCellStyle.BackColor = row.ChangeKind switch
            {
                "Added" => Color.FromArgb(235, 255, 239),
                "Deleted" => Color.FromArgb(255, 239, 241),
                _ => Color.White,
            };
            gridRow.DefaultCellStyle.ForeColor = Color.FromArgb(30, 41, 59);
        }
    }

    private static void PaintExcelDiffCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            args.Graphics == null ||
            grid.Rows[args.RowIndex].DataBoundItem is not ExcelUnifiedDiffRow row)
        {
            return;
        }

        var columnName = grid.Columns[args.ColumnIndex].Name;
        if (columnName is not "OldValue" and not "NewValue")
        {
            return;
        }

        args.Handled = true;
        args.Paint(args.CellBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var bounds = Rectangle.Inflate(args.CellBounds, -8, -5);
        var cellStyle = args.CellStyle ?? grid.DefaultCellStyle;
        var font = cellStyle.Font ?? grid.Font;
        var isSelected = grid.Rows[args.RowIndex].Selected;
        var backColor = isSelected ? cellStyle.SelectionBackColor : grid.Rows[args.RowIndex].DefaultCellStyle.BackColor;
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);

        DrawHorizontalDiffValue(args.Graphics, bounds, row, columnName == "OldValue", font);

        using var borderPen = new Pen(Color.FromArgb(226, 232, 240));
        args.Graphics.DrawLine(borderPen, args.CellBounds.Left, args.CellBounds.Bottom - 1, args.CellBounds.Right, args.CellBounds.Bottom - 1);
    }

    private static void DrawHorizontalDiffValue(Graphics graphics, Rectangle bounds, ExcelUnifiedDiffRow row, bool oldSide, Font font)
    {
        var value = oldSide ? row.OldValue : row.NewValue;
        var label = oldSide ? "旧" : "新";
        var textColor = oldSide ? Color.FromArgb(153, 27, 27) : Color.FromArgb(22, 101, 52);
        var fillColor = oldSide ? Color.FromArgb(255, 241, 242) : Color.FromArgb(240, 253, 244);
        var highlightColor = oldSide ? Color.FromArgb(254, 202, 202) : Color.FromArgb(187, 247, 208);
        var emptyText = row.ChangeKind switch
        {
            "Added" when oldSide => "（新增，无旧值）",
            "Deleted" when !oldSide => "（已删除，无新值）",
            _ => "（空）",
        };
        var displayValue = string.IsNullOrEmpty(value) ? emptyText : value;
        var highlights = row.ValueHighlights;
        var spans = oldSide ? highlights.OldSpans : highlights.NewSpans;
        var strikeout = oldSide && row.ChangeKind == "Deleted";
        using var fillBrush = new SolidBrush(fillColor);
        graphics.FillRectangle(fillBrush, bounds);
        using var borderPen = new Pen(oldSide ? Color.FromArgb(254, 205, 211) : Color.FromArgb(187, 247, 208));
        graphics.DrawRectangle(borderPen, bounds.Left, bounds.Top, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        DrawValueLine(graphics, Rectangle.Inflate(bounds, -6, 0), label, displayValue, spans, font, textColor, highlightColor, strikeout);
    }

    private static void DrawValueLine(
        Graphics graphics,
        Rectangle bounds,
        string marker,
        string value,
        int highlightStart,
        int highlightLength,
        Font font,
        Color textColor,
        Color highlightColor,
        bool strikeout)
    {
        var spans = highlightStart >= 0 && highlightLength > 0
            ? [new TextHighlightSpan(highlightStart, highlightLength)]
            : Array.Empty<TextHighlightSpan>();
        DrawValueLine(graphics, bounds, marker, value, spans, font, textColor, highlightColor, strikeout);
    }

    private static void DrawValueLine(
        Graphics graphics,
        Rectangle bounds,
        string marker,
        string value,
        IReadOnlyList<TextHighlightSpan> highlightSpans,
        Font font,
        Color textColor,
        Color highlightColor,
        bool strikeout)
    {
        var lineFont = strikeout ? new Font(font, font.Style | FontStyle.Strikeout) : font;
        try
        {
            var markerBounds = new Rectangle(bounds.Left, bounds.Top, 24, bounds.Height);
            TextRenderer.DrawText(graphics, marker, font, markerBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            var textBounds = new Rectangle(bounds.Left + 26, bounds.Top, Math.Max(0, bounds.Width - 26), bounds.Height);
            DrawSegmentedText(graphics, textBounds, value, highlightSpans, lineFont, textColor, highlightColor);
        }
        finally
        {
            if (!ReferenceEquals(lineFont, font))
            {
                lineFont.Dispose();
            }
        }
    }

    private static void DrawSegmentedText(Graphics graphics, Rectangle bounds, string value, int highlightStart, int highlightLength, Font font, Color textColor, Color highlightColor)
    {
        value ??= "";
        highlightStart = Math.Clamp(highlightStart, -1, value.Length);
        highlightLength = Math.Clamp(highlightLength, 0, Math.Max(0, value.Length - Math.Max(0, highlightStart)));
        if (highlightStart < 0 || highlightLength == 0)
        {
            TextRenderer.DrawText(graphics, value, font, bounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        var prefix = value[..highlightStart];
        var highlight = value.Substring(highlightStart, highlightLength);
        var suffix = value[(highlightStart + highlightLength)..];
        var x = bounds.Left;
        DrawTextPart(graphics, ref x, bounds, prefix, font, textColor, null);
        DrawTextPart(graphics, ref x, bounds, highlight, font, textColor, highlightColor);
        DrawTextPart(graphics, ref x, bounds, suffix, font, textColor, null);
    }

    private static void DrawSegmentedText(Graphics graphics, Rectangle bounds, string value, IReadOnlyList<TextHighlightSpan> highlights, Font font, Color textColor, Color highlightColor)
    {
        value ??= "";
        var ordered = highlights
            .Where(span => span.Length > 0 && span.Start < value.Length)
            .Select(span => new TextHighlightSpan(Math.Max(0, span.Start), Math.Min(span.Length, value.Length - Math.Max(0, span.Start))))
            .OrderBy(span => span.Start)
            .ToList();
        if (ordered.Count == 0)
        {
            TextRenderer.DrawText(graphics, value, font, bounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        var x = bounds.Left;
        var cursor = 0;
        foreach (var span in ordered)
        {
            if (span.Start > cursor)
            {
                DrawTextPart(graphics, ref x, bounds, value[cursor..span.Start], font, textColor, null);
            }

            DrawTextPart(graphics, ref x, bounds, value.Substring(span.Start, span.Length), font, textColor, highlightColor);
            cursor = span.Start + span.Length;
            if (x >= bounds.Right)
            {
                return;
            }
        }

        if (cursor < value.Length)
        {
            DrawTextPart(graphics, ref x, bounds, value[cursor..], font, textColor, null);
        }
    }

    private static void DrawTextPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var textSize = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(textSize.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top, width, bounds.Height);
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRectangle(brush, partBounds);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }

    private static void ShowExcelDiffDetail(IWin32Window? owner, ExcelUnifiedDiffRow row)
    {
        var oldDetail = FormatDiffDetailValue(row.OldValue);
        var newDetail = FormatDiffDetailValue(row.NewValue);
        var highlights = DiffHighlightSpans.Calculate(oldDetail, newDetail);
        using var form = new Form
        {
            Text = $"单元格差异 - {row.Sheet} {row.Address}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(980, 560),
            Size = new Size(1160, 740),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        form.Controls.Add(root);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Text = $"{row.Sheet} / {row.Address} / {row.FieldName} / ID: {row.RowId}    {row.DetailKind}    改动片段：旧 {highlights.OldSpans.Count} / 新 {highlights.NewSpans.Count}",
        }, 0, 0);
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.Controls.Add(CreateValueBox("旧值（红底为改动位置）", oldDetail, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), row.ChangeKind == "Deleted", highlights.OldSpans), 0, 0);
        split.Controls.Add(CreateValueBox("新值（绿底为改动位置）", newDetail, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), false, highlights.NewSpans), 1, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(CreateRowContextGrid(row), 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK });
        root.Controls.Add(buttons, 0, 3);
        form.AcceptButton = buttons.Controls.OfType<Button>().First();
        form.ShowDialog(owner);
    }

    private static Control CreateRowContextGrid(ExcelUnifiedDiffRow row)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = "整行横向单元格核对",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var cells = BuildRowContextCells(row);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.Single,
            GridColor = Color.FromArgb(226, 232, 240),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.RowTemplate.Height = 42;
        grid.DefaultCellStyle.Font = new Font("Consolas", 9F);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);

        var versionColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "版本",
            Name = "Version",
            Width = 72,
            Frozen = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        grid.Columns.Add(versionColumn);
        foreach (var cell in cells)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = cell.Field,
                Name = $"F{grid.Columns.Count}",
                Width = CalculateRowContextColumnWidth(cell),
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        grid.Rows.Add(BuildRowContextGridValues("旧", cells.Select(cell => cell.OldValue)));
        grid.Rows.Add(BuildRowContextGridValues("新", cells.Select(cell => cell.NewValue)));
        grid.Rows[0].DefaultCellStyle.ForeColor = Color.FromArgb(153, 27, 27);
        grid.Rows[1].DefaultCellStyle.ForeColor = Color.FromArgb(22, 101, 52);
        grid.Rows[0].Cells[0].Style.BackColor = Color.FromArgb(255, 241, 242);
        grid.Rows[1].Cells[0].Style.BackColor = Color.FromArgb(240, 253, 244);
        grid.Rows[0].Cells[0].Style.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.Rows[1].Cells[0].Style.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

        for (var index = 0; index < cells.Count; index++)
        {
            var columnIndex = index + 1;
            var cell = cells[index];
            if (cell.Changed)
            {
                grid.Rows[0].Cells[columnIndex].Style.BackColor = Color.FromArgb(254, 226, 226);
                grid.Rows[1].Cells[columnIndex].Style.BackColor = Color.FromArgb(220, 252, 231);
            }

            if (cell.IsCurrent)
            {
                grid.Columns[columnIndex].HeaderCell.Style.BackColor = Color.FromArgb(219, 234, 254);
                grid.Columns[columnIndex].HeaderCell.Style.ForeColor = Color.FromArgb(29, 78, 216);
                grid.Rows[0].Cells[columnIndex].Style.Font = new Font("Consolas", 9F, FontStyle.Bold);
                grid.Rows[1].Cells[columnIndex].Style.Font = new Font("Consolas", 9F, FontStyle.Bold);
            }
        }

        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.ColumnIndex <= 0 || args.ColumnIndex > cells.Count)
            {
                return;
            }

            var cell = cells[args.ColumnIndex - 1];
            args.ToolTipText =
                $"字段：{cell.Field}{Environment.NewLine}" +
                $"旧值：{cell.OldValue}{Environment.NewLine}" +
                $"新值：{cell.NewValue}";
        };
        panel.Controls.Add(grid, 0, 1);
        return panel;
    }

    private static Control CreateValueBox(string title, string value, Color color, Color highlightColor, bool strikeout, IReadOnlyList<TextHighlightSpan> highlights)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = color,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = true,
            Font = new Font("Consolas", 10F, strikeout ? FontStyle.Strikeout : FontStyle.Regular),
            ForeColor = color,
            BackColor = Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            DetectUrls = false,
            Text = value,
        };
        ApplyRichTextHighlights(box, color, highlightColor, strikeout, highlights);
        panel.Controls.Add(box, 0, 1);
        return panel;
    }

    private static IReadOnlyList<RowContextCell> BuildRowContextCells(ExcelUnifiedDiffRow row)
    {
        var oldCells = ParseRowContextCells(row.OldRowText);
        var newCells = ParseRowContextCells(row.NewRowText);
        var fieldOrder = oldCells.Select(cell => cell.Field)
            .Concat(newCells.Select(cell => cell.Field))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var oldByField = oldCells.GroupBy(cell => cell.Field, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var newByField = newCells.GroupBy(cell => cell.Field, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var currentColumnName = ColumnNameFromAddress(row.Address);
        return fieldOrder.Select(field =>
        {
            oldByField.TryGetValue(field, out var oldValue);
            newByField.TryGetValue(field, out var newValue);
            oldValue ??= "";
            newValue ??= "";
            var isCurrent =
                string.Equals(field, row.FieldName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field, currentColumnName, StringComparison.OrdinalIgnoreCase);
            return new RowContextCell(
                field,
                string.IsNullOrEmpty(oldValue) ? "(空)" : oldValue,
                string.IsNullOrEmpty(newValue) ? "(空)" : newValue,
                !string.Equals(oldValue, newValue, StringComparison.Ordinal),
                isCurrent);
        }).ToList();
    }

    private static IReadOnlyList<RowContextPair> ParseRowContextCells(string text)
    {
        return FormatRowContextValue(text)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseRowContextPair)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Field))
            .ToList();
    }

    private static RowContextPair ParseRowContextPair(string line)
    {
        var separator = line.IndexOf(" = ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            return new RowContextPair(line[..separator].Trim(), line[(separator + 3)..].Trim());
        }

        separator = line.IndexOf('=');
        return separator >= 0
            ? new RowContextPair(line[..separator].Trim(), line[(separator + 1)..].Trim())
            : new RowContextPair(line.Trim(), "");
    }

    private static string ColumnNameFromAddress(string address)
    {
        return new string((address ?? "").TakeWhile(char.IsLetter).ToArray());
    }

    private static object[] BuildRowContextGridValues(string version, IEnumerable<string> values)
    {
        return new[] { version }.Concat(values).Cast<object>().ToArray();
    }

    private static int CalculateRowContextColumnWidth(RowContextCell cell)
    {
        var contentLength = Math.Max(cell.Field.Length, Math.Max(cell.OldValue.Length, cell.NewValue.Length));
        return Math.Clamp(contentLength * 8 + 28, 86, 260);
    }

    private static void ApplyRichTextHighlights(RichTextBox box, Color textColor, Color highlightColor, bool strikeout, IReadOnlyList<TextHighlightSpan> highlights)
    {
        box.SelectAll();
        box.SelectionColor = textColor;
        box.SelectionBackColor = Color.White;
        box.SelectionFont = new Font(box.Font, strikeout ? box.Font.Style | FontStyle.Strikeout : box.Font.Style);

        foreach (var span in highlights)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start >= box.TextLength)
            {
                continue;
            }

            var length = Math.Min(span.Length, box.TextLength - span.Start);
            box.Select(span.Start, length);
            box.SelectionBackColor = highlightColor;
            box.SelectionFont = new Font(box.Font, box.Font.Style | FontStyle.Bold | (strikeout ? FontStyle.Strikeout : FontStyle.Regular));
        }

        box.Select(0, 0);
    }

    private static string FormatDiffDetailValue(string value)
    {
        value ??= "";
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return "";
        }

        if (TryFormatJson(normalized, out var json))
        {
            return json;
        }

        if (TryFormatXml(normalized, out var xml))
        {
            return xml;
        }

        if (ExcelUnifiedDiffRow.LooksStructuredValue(normalized))
        {
            var separator = normalized.Contains('|', StringComparison.Ordinal)
                ? '|'
                : normalized.Contains(';', StringComparison.Ordinal) ? ';' : ',';
            var parts = normalized
                .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.Length > 0)
                .ToList();
            if (parts.Count >= 2)
            {
                return string.Join(Environment.NewLine, parts);
            }
        }

        return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static string FormatRowContextValue(string value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static bool TryFormatJson(string value, out string formatted)
    {
        formatted = "";
        if (!value.StartsWith("{", StringComparison.Ordinal) && !value.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFormatXml(string value, out string formatted)
    {
        formatted = "";
        if (!value.StartsWith("<", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            formatted = XDocument.Parse(value).ToString(SaveOptions.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyExcelDiffSummary(IWin32Window? owner, IReadOnlyList<ExcelUnifiedDiffRow> rows)
    {
        if (rows.Count == 0)
        {
            MessageBox.Show(owner, "当前筛选结果为空，没有可复制的差异。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Excel/XML 差异摘要：{rows.Count} 项");
        builder.AppendLine("状态\t工作表\t单元格\tID\t字段\t旧值\t新值");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join('\t',
                TranslateExcelChangeKind(row.ChangeKind),
                NormalizeClipboardCell(row.Sheet),
                NormalizeClipboardCell(row.Address),
                NormalizeClipboardCell(row.RowId),
                NormalizeClipboardCell(row.FieldName),
                NormalizeClipboardCell(row.OldValue),
                NormalizeClipboardCell(row.NewValue)));
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"复制失败：{ex.Message}", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(owner, $"已复制 {rows.Count} 项差异摘要。", "复制摘要", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string TranslateExcelChangeKind(string changeKind)
    {
        return changeKind switch
        {
            "Added" => "新增",
            "Deleted" => "删除",
            _ => "修改",
        };
    }

    private static string NormalizeClipboardCell(string value)
    {
        return (value ?? "")
            .Replace('\t', ' ')
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}

internal sealed record ExcelUnifiedDiffRow(string Sheet, string Address, string FieldName, string RowId, string OldValue, string NewValue, string OldRowText, string NewRowText)
{
    private DiffHighlightSpans? _valueHighlights;

    public string DifferenceText => $"{OldValue} -> {NewValue}";

    public DiffHighlightSpans ValueHighlights => _valueHighlights ??= DiffHighlightSpans.Calculate(OldValue, NewValue);

    public bool HasStructuredValue => LooksStructuredValue(OldValue) || LooksStructuredValue(NewValue);

    public bool HasLongValue => (OldValue?.Length ?? 0) > 80 || (NewValue?.Length ?? 0) > 80;

    public string DetailKind => HasStructuredValue ? "子表/结构化字段" : HasLongValue ? "长文本" : "普通单元格";

    public string ChangeKind => string.IsNullOrEmpty(OldValue) && !string.IsNullOrEmpty(NewValue)
        ? "Added"
        : !string.IsNullOrEmpty(OldValue) && string.IsNullOrEmpty(NewValue)
            ? "Deleted"
            : "Modified";

    public static bool LooksStructuredValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('\n') ||
            trimmed.StartsWith("<", StringComparison.Ordinal) ||
            trimmed.StartsWith("{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.Count(character => character == '|') >= 1)
        {
            return true;
        }

        var commaCount = trimmed.Count(character => character == ',');
        var semicolonCount = trimmed.Count(character => character == ';');
        var equalsCount = trimmed.Count(character => character == '=');
        return equalsCount >= 2 || commaCount >= 4 || semicolonCount >= 2;
    }

    public static ExcelUnifiedDiffRow FromDifference(ExcelCellDifference difference)
    {
        return new ExcelUnifiedDiffRow(
            difference.Sheet,
            difference.Address,
            string.IsNullOrWhiteSpace(difference.FieldName) ? "(未命名字段)" : difference.FieldName,
            string.IsNullOrWhiteSpace(difference.RowId) ? "(无 ID)" : difference.RowId,
            difference.OldValue,
            difference.NewValue,
            difference.OldRowText,
            difference.NewRowText);
    }
}

internal sealed record RowContextPair(string Field, string Value);

internal sealed record RowContextCell(string Field, string OldValue, string NewValue, bool Changed, bool IsCurrent);

internal sealed record DiffSpan(int OldStart, int OldLength, int NewStart, int NewLength)
{
    public static DiffSpan Calculate(string oldValue, string newValue)
    {
        oldValue ??= "";
        newValue ??= "";
        var prefix = 0;
        while (prefix < oldValue.Length &&
            prefix < newValue.Length &&
            oldValue[prefix] == newValue[prefix])
        {
            prefix++;
        }

        var oldEnd = oldValue.Length - 1;
        var newEnd = newValue.Length - 1;
        while (oldEnd >= prefix &&
            newEnd >= prefix &&
            oldValue[oldEnd] == newValue[newEnd])
        {
            oldEnd--;
            newEnd--;
        }

        return new DiffSpan(
            prefix,
            Math.Max(0, oldEnd - prefix + 1),
            prefix,
            Math.Max(0, newEnd - prefix + 1));
    }
}

internal sealed record TextHighlightSpan(int Start, int Length);

internal sealed record DiffHighlightSpans(IReadOnlyList<TextHighlightSpan> OldSpans, IReadOnlyList<TextHighlightSpan> NewSpans)
{
    public static DiffHighlightSpans Calculate(string oldValue, string newValue)
    {
        oldValue ??= "";
        newValue ??= "";
        var tokenSpans = CalculateKeyValueTokenSpans(oldValue, newValue);
        if (tokenSpans.OldSpans.Count > 0 || tokenSpans.NewSpans.Count > 0)
        {
            return tokenSpans;
        }

        var span = DiffSpan.Calculate(oldValue, newValue);
        return new DiffHighlightSpans(
            span.OldLength > 0 ? [new TextHighlightSpan(span.OldStart, span.OldLength)] : [],
            span.NewLength > 0 ? [new TextHighlightSpan(span.NewStart, span.NewLength)] : []);
    }

    private static DiffHighlightSpans CalculateKeyValueTokenSpans(string oldValue, string newValue)
    {
        var oldTokens = ParseKeyValueTokens(oldValue);
        var newTokens = ParseKeyValueTokens(newValue);
        if (oldTokens.Count < 2 && newTokens.Count < 2)
        {
            return new DiffHighlightSpans([], []);
        }

        if (HasDuplicateKeys(oldTokens) || HasDuplicateKeys(newTokens))
        {
            return new DiffHighlightSpans([], []);
        }

        var oldByKey = oldTokens.ToDictionary(token => token.Key, StringComparer.Ordinal);
        var newByKey = newTokens.ToDictionary(token => token.Key, StringComparer.Ordinal);
        var oldSpans = new List<TextHighlightSpan>();
        var newSpans = new List<TextHighlightSpan>();

        foreach (var oldToken in oldTokens)
        {
            if (!newByKey.TryGetValue(oldToken.Key, out var newToken))
            {
                oldSpans.Add(new TextHighlightSpan(oldToken.TokenStart, oldToken.TokenLength));
                continue;
            }

            if (!string.Equals(oldToken.Value, newToken.Value, StringComparison.Ordinal))
            {
                oldSpans.Add(new TextHighlightSpan(oldToken.ValueStart, oldToken.ValueLength));
                newSpans.Add(new TextHighlightSpan(newToken.ValueStart, newToken.ValueLength));
            }
        }

        foreach (var newToken in newTokens)
        {
            if (!oldByKey.ContainsKey(newToken.Key))
            {
                newSpans.Add(new TextHighlightSpan(newToken.TokenStart, newToken.TokenLength));
            }
        }

        return new DiffHighlightSpans(CoalesceSpans(oldSpans), CoalesceSpans(newSpans));
    }

    private static IReadOnlyList<KeyValueTextToken> ParseKeyValueTokens(string value)
    {
        var tokens = new List<KeyValueTextToken>();
        var start = 0;
        while (start <= value.Length)
        {
            var end = start;
            while (end < value.Length && value[end] is not ',' and not ';' and not '\r' and not '\n')
            {
                end++;
            }

            AddKeyValueToken(value, start, end, tokens);
            if (end >= value.Length)
            {
                break;
            }

            start = end + 1;
        }

        return tokens;
    }

    private static void AddKeyValueToken(string value, int start, int end, List<KeyValueTextToken> tokens)
    {
        var tokenStart = start;
        var tokenEnd = end;
        while (tokenStart < tokenEnd && char.IsWhiteSpace(value[tokenStart]))
        {
            tokenStart++;
        }

        while (tokenEnd > tokenStart && char.IsWhiteSpace(value[tokenEnd - 1]))
        {
            tokenEnd--;
        }

        if (tokenEnd <= tokenStart)
        {
            return;
        }

        var equalsIndex = value.IndexOf('=', tokenStart, tokenEnd - tokenStart);
        if (equalsIndex <= tokenStart)
        {
            return;
        }

        var key = value[tokenStart..equalsIndex].Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var valueStart = equalsIndex + 1;
        var valueEnd = tokenEnd;
        while (valueStart < valueEnd && char.IsWhiteSpace(value[valueStart]))
        {
            valueStart++;
        }

        while (valueEnd > valueStart && char.IsWhiteSpace(value[valueEnd - 1]))
        {
            valueEnd--;
        }

        tokens.Add(new KeyValueTextToken(
            key,
            value[valueStart..valueEnd],
            tokenStart,
            tokenEnd - tokenStart,
            valueStart,
            valueEnd - valueStart));
    }

    private static bool HasDuplicateKeys(IReadOnlyList<KeyValueTextToken> tokens)
    {
        return tokens.Select(token => token.Key).Distinct(StringComparer.Ordinal).Count() != tokens.Count;
    }

    private static IReadOnlyList<TextHighlightSpan> CoalesceSpans(IReadOnlyList<TextHighlightSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var ordered = spans
            .Where(span => span.Length > 0)
            .OrderBy(span => span.Start)
            .ToList();
        if (ordered.Count <= 1)
        {
            return ordered;
        }

        var result = new List<TextHighlightSpan>();
        var current = ordered[0];
        foreach (var next in ordered.Skip(1))
        {
            var currentEnd = current.Start + current.Length;
            if (next.Start <= currentEnd)
            {
                current = current with { Length = Math.Max(currentEnd, next.Start + next.Length) - current.Start };
                continue;
            }

            result.Add(current);
            current = next;
        }

        result.Add(current);
        return result;
    }
}

internal sealed record KeyValueTextToken(string Key, string Value, int TokenStart, int TokenLength, int ValueStart, int ValueLength);

internal sealed record ExcelCellKey(string Sheet, int Row, int Column);
internal sealed record ExcelRowKey(string Sheet, int Row);

internal sealed record ExcelCellDifference(string Sheet, int Row, int Column, string ColumnName, string FieldName, string RowId, string OldValue, string NewValue, string OldRowText, string NewRowText)
{
    public string Address => $"{ColumnName}{Row}";
}

