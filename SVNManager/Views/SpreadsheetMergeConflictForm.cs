using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class SpreadsheetMergeConflictForm : Form
{
    private readonly SpreadsheetMergePlan _plan;
    private readonly List<SpreadsheetMergeConflictGridRow> _rows;
    private readonly BindingSource _source = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _detailTitleLabel = new();
    private readonly RichTextBox _baseDetailBox = new();
    private readonly RichTextBox _targetDetailBox = new();
    private readonly RichTextBox _sourceDetailBox = new();
    private readonly string _targetLabel;
    private readonly string _sourceLabel;
    private readonly string _localResolutionText;
    private readonly string _remoteResolutionText;
    private readonly string _keepTargetText;
    private readonly string _writeCellText;
    private readonly string _appendRowText;
    private readonly string _insertRowText;
    private readonly string _deleteRowText;
    private readonly string[] _operationTexts;
    private bool _syncingRowOperation;

    public SpreadsheetMergeConflictForm(
        string relativePath,
        SpreadsheetMergePlan plan,
        string titlePrefix = "内置表格三方合并",
        string targetLabel = "本地",
        string sourceLabel = "远端 HEAD",
        string applyButtonText = "写入工作副本")
    {
        _plan = plan;
        _targetLabel = targetLabel;
        _sourceLabel = sourceLabel;
        _localResolutionText = string.Equals(targetLabel, "本地", StringComparison.Ordinal)
            ? "保留本地"
            : $"保留{targetLabel}";
        _remoteResolutionText = string.Equals(sourceLabel, "远端 HEAD", StringComparison.Ordinal)
            ? "使用远端"
            : $"使用{sourceLabel}";
        _keepTargetText = _localResolutionText;
        _writeCellText = $"写入{sourceLabel}单元格";
        _appendRowText = "新增行到末尾";
        _insertRowText = "插入新行";
        _deleteRowText = $"删除{targetLabel}行";
        _operationTexts = [_keepTargetText, _writeCellText, _appendRowText, _insertRowText, _deleteRowText];
        _rows = plan.AllChanges
            .Select(change => new SpreadsheetMergeConflictGridRow(change, _keepTargetText, _writeCellText, _appendRowText, _insertRowText, _deleteRowText))
            .ToList();
        Text = $"{titlePrefix} - {relativePath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1180, 700);
        Size = new Size(1400, 840);
        Font = new Font("Microsoft YaHei UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.ForeColor = Color.FromArgb(45, 55, 72);
        root.Controls.Add(_summaryLabel, 0, 0);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(CreateDetailPanel(), 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var applyButton = new Button { Text = applyButtonText, Width = 118 };
        var cancelButton = new Button { Text = "取消", Width = 86, DialogResult = DialogResult.Cancel };
        var allRemoteButton = new Button { Text = $"全部选{sourceLabel}", Width = 120 };
        var allLocalButton = new Button { Text = $"全部选{targetLabel}", Width = 120 };
        applyButton.Click += (_, _) =>
        {
            if (ApplyRowsToPlan())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        allRemoteButton.Click += (_, _) => SetAll(_writeCellText);
        allLocalButton.Click += (_, _) => SetAll(_keepTargetText);
        buttons.Controls.Add(applyButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(allRemoteButton);
        buttons.Controls.Add(allLocalButton);
        root.Controls.Add(buttons, 0, 3);
        AcceptButton = applyButton;
        CancelButton = cancelButton;
        UpdateSummary();
        UpdateMergeDetail();
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _grid.GridColor = Color.FromArgb(226, 232, 240);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 55, 72);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);
        _grid.RowTemplate.Height = 86;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.DataBindingComplete += (_, _) => ApplyRowStyles();
        _grid.SelectionChanged += (_, _) => UpdateMergeDetail();
        _grid.CellPainting += PaintMergeComparisonCell;
        _grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && _grid.Rows[args.RowIndex].DataBoundItem is SpreadsheetMergeConflictGridRow row)
            {
                ShowMergeCellDetail(row);
            }
        };
        _grid.CellValueChanged += (_, args) =>
        {
            if (args.RowIndex >= 0)
            {
                SynchronizeRowOperation(args.RowIndex, args.ColumnIndex);
                UpdateSummary();
                ApplyRowStyles();
                UpdateMergeDetail();
            }
        };
        _grid.DataError += (_, args) => args.ThrowException = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= _rows.Count)
            {
                return;
            }

            var row = _rows[args.RowIndex];
            args.ToolTipText =
                $"BASE：{row.BaseValue}{Environment.NewLine}" +
                $"{_targetLabel}：{row.LocalValue}{Environment.NewLine}" +
                $"{_sourceLabel}：{row.RemoteValue}";
        };

        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "操作",
            Name = "Operation",
            DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.OperationText),
            DataSource = _operationTexts,
            Width = 132,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.KindText), Width = 108, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "对齐状态", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.AlignmentText), Width = 170, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "默认位置", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.DefaultLocation), Width = 128, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入工作表", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.WriteSheet), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "写入单元格", Name = "WriteAddress", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.WriteAddress), Width = 88 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.RowId), Width = 126, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "字段", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.FieldName), Width = 144, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "合并对比（双击看完整内容）",
            Name = "MergeComparison",
            DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.ComparisonText),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 420,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "BASE", DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.BaseValue), Visible = false, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = _targetLabel, DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.LocalValue), Visible = false, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = _sourceLabel, DataPropertyName = nameof(SpreadsheetMergeConflictGridRow.RemoteValue), Visible = false, ReadOnly = true });

        _source.DataSource = _rows;
        _grid.DataSource = _source;
    }

    private Control CreateDetailPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _detailTitleLabel.Dock = DockStyle.Fill;
        _detailTitleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _detailTitleLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _detailTitleLabel.ForeColor = Color.FromArgb(30, 41, 59);
        panel.Controls.Add(_detailTitleLabel, 0, 0);

        var boxes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.White,
        };
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        boxes.Controls.Add(CreateMergeDetailBox("BASE", _baseDetailBox, Color.FromArgb(71, 85, 105)), 0, 0);
        boxes.Controls.Add(CreateMergeDetailBox(_targetLabel, _targetDetailBox, Color.FromArgb(153, 27, 27)), 1, 0);
        boxes.Controls.Add(CreateMergeDetailBox(_sourceLabel, _sourceDetailBox, Color.FromArgb(22, 101, 52)), 2, 0);
        panel.Controls.Add(boxes, 0, 1);
        return panel;
    }

    private static Control CreateMergeDetailBox(string title, RichTextBox box, Color titleColor)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 8, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = title,
            ForeColor = titleColor,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        box.Dock = DockStyle.Fill;
        box.ReadOnly = true;
        box.WordWrap = true;
        box.ScrollBars = RichTextBoxScrollBars.Both;
        box.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
        box.BackColor = Color.White;
        box.Font = new Font("Consolas", 9F);
        box.DetectUrls = false;
        panel.Controls.Add(box, 0, 1);
        return panel;
    }

    private void UpdateMergeDetail()
    {
        var row = _grid.CurrentRow?.DataBoundItem as SpreadsheetMergeConflictGridRow ??
            _rows.FirstOrDefault();
        if (row == null)
        {
            _detailTitleLabel.Text = "未选择合并项目";
            SetMergeDetailText(_baseDetailBox, "", Color.FromArgb(71, 85, 105), Color.White, []);
            SetMergeDetailText(_targetDetailBox, "", Color.FromArgb(153, 27, 27), Color.White, []);
            SetMergeDetailText(_sourceDetailBox, "", Color.FromArgb(22, 101, 52), Color.White, []);
            return;
        }

        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        _detailTitleLabel.Text = $"{row.KindText}    {row.Sheet}!{row.Address}    写入 {row.WriteSheet}!{row.WriteAddress}    ID: {row.RowId}    字段: {row.FieldName}";
        SetMergeDetailText(_baseDetailBox, row.BaseValue, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240), []);
        SetMergeDetailText(_targetDetailBox, row.LocalValue, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), highlights.OldSpans);
        SetMergeDetailText(_sourceDetailBox, row.RemoteValue, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), highlights.NewSpans);
    }

    private static void SetMergeDetailText(RichTextBox box, string value, Color textColor, Color highlightColor, IReadOnlyList<TextHighlightSpan> highlights)
    {
        box.SuspendLayout();
        box.Text = value ?? "";
        box.SelectAll();
        box.SelectionColor = textColor;
        box.SelectionBackColor = Color.White;
        box.SelectionFont = new Font(box.Font, FontStyle.Regular);
        foreach (var span in highlights)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start >= box.TextLength)
            {
                continue;
            }

            var length = Math.Min(span.Length, box.TextLength - span.Start);
            box.Select(span.Start, length);
            box.SelectionBackColor = highlightColor;
            box.SelectionFont = new Font(box.Font, FontStyle.Bold);
        }

        box.Select(0, 0);
        box.ResumeLayout();
    }

    private void PaintMergeComparisonCell(object? sender, DataGridViewCellPaintingEventArgs args)
    {
        if (sender is not DataGridView grid ||
            args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            args.Graphics == null ||
            grid.Columns[args.ColumnIndex].Name != "MergeComparison" ||
            grid.Rows[args.RowIndex].DataBoundItem is not SpreadsheetMergeConflictGridRow row)
        {
            return;
        }

        args.Handled = true;
        var cellStyle = args.CellStyle ?? grid.DefaultCellStyle;
        var selected = grid.Rows[args.RowIndex].Selected;
        var backColor = selected
            ? cellStyle.SelectionBackColor
            : grid.Rows[args.RowIndex].DefaultCellStyle.BackColor;
        using var backBrush = new SolidBrush(backColor);
        args.Graphics.FillRectangle(backBrush, args.CellBounds);

        var bounds = Rectangle.Inflate(args.CellBounds, -8, -5);
        var lineHeight = Math.Max(21, bounds.Height / 3);
        var baseBounds = new Rectangle(bounds.Left, bounds.Top, bounds.Width, lineHeight);
        var targetBounds = new Rectangle(bounds.Left, bounds.Top + lineHeight, bounds.Width, lineHeight);
        var sourceBounds = new Rectangle(bounds.Left, bounds.Top + lineHeight * 2, bounds.Width, bounds.Height - lineHeight * 2);
        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        var basePreview = BuildFocusedPreview(row.BaseValue, [], 160);
        var targetPreview = BuildFocusedPreview(row.LocalValue, highlights.OldSpans, 170);
        var sourcePreview = BuildFocusedPreview(row.RemoteValue, highlights.NewSpans, 170);

        DrawMergeValueLine(args.Graphics, baseBounds, "BASE", basePreview.Text, basePreview.Spans, grid.Font, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240));
        DrawMergeValueLine(args.Graphics, targetBounds, _targetLabel, targetPreview.Text, targetPreview.Spans, grid.Font, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202));
        DrawMergeValueLine(args.Graphics, sourceBounds, _sourceLabel, sourcePreview.Text, sourcePreview.Spans, grid.Font, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208));

        using var borderPen = new Pen(Color.FromArgb(203, 213, 225));
        args.Graphics.DrawLine(borderPen, args.CellBounds.Left, args.CellBounds.Bottom - 1, args.CellBounds.Right, args.CellBounds.Bottom - 1);
    }

    private static (string Text, IReadOnlyList<TextHighlightSpan> Spans) BuildFocusedPreview(string value, IReadOnlyList<TextHighlightSpan> spans, int maxLength)
    {
        value ??= "";
        if (value.Length <= maxLength || spans.Count == 0)
        {
            return (value, spans);
        }

        var firstStart = spans.Min(span => Math.Max(0, span.Start));
        var lastEnd = spans.Max(span => Math.Min(value.Length, span.Start + span.Length));
        var context = Math.Max(24, (maxLength - Math.Max(8, lastEnd - firstStart)) / 2);
        var start = Math.Max(0, firstStart - context);
        var end = Math.Min(value.Length, lastEnd + context);
        if (end - start > maxLength)
        {
            end = Math.Min(value.Length, start + maxLength);
        }

        var prefix = start > 0 ? "... " : "";
        var suffix = end < value.Length ? " ..." : "";
        var text = prefix + value[start..end] + suffix;
        var adjusted = spans
            .Select(span =>
            {
                var spanStart = Math.Max(start, span.Start);
                var spanEnd = Math.Min(end, span.Start + span.Length);
                return spanEnd > spanStart
                    ? new TextHighlightSpan(prefix.Length + spanStart - start, spanEnd - spanStart)
                    : new TextHighlightSpan(-1, 0);
            })
            .Where(span => span.Start >= 0 && span.Length > 0)
            .ToList();
        return (text, adjusted);
    }

    private static void DrawMergeValueLine(
        Graphics graphics,
        Rectangle bounds,
        string label,
        string value,
        IReadOnlyList<TextHighlightSpan> highlights,
        Font font,
        Color textColor,
        Color highlightColor)
    {
        var labelBounds = new Rectangle(bounds.Left, bounds.Top + 2, 58, Math.Max(18, bounds.Height - 4));
        using var labelBrush = new SolidBrush(Color.FromArgb(28, textColor));
        using var labelPen = new Pen(Color.FromArgb(80, textColor));
        graphics.FillRoundedRectangle(labelBrush, labelBounds, 4);
        graphics.DrawRoundedRectangle(labelPen, labelBounds, 4);
        TextRenderer.DrawText(
            graphics,
            label,
            font,
            labelBounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var textBounds = new Rectangle(labelBounds.Right + 8, bounds.Top, Math.Max(1, bounds.Right - labelBounds.Right - 8), bounds.Height);
        DrawHighlightedMergeText(graphics, textBounds, value, highlights, font, textColor, highlightColor);
    }

    private static void DrawHighlightedMergeText(
        Graphics graphics,
        Rectangle bounds,
        string value,
        IReadOnlyList<TextHighlightSpan> highlights,
        Font font,
        Color textColor,
        Color highlightColor)
    {
        value ??= "";
        if (string.IsNullOrEmpty(value))
        {
            TextRenderer.DrawText(graphics, "(空)", font, bounds, Color.FromArgb(148, 163, 184), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var x = bounds.Left;
        var cursor = 0;
        foreach (var span in highlights.OrderBy(span => span.Start))
        {
            if (span.Start > cursor)
            {
                DrawMergeTextPart(graphics, ref x, bounds, value[cursor..span.Start], font, textColor, null);
            }

            var safeLength = Math.Min(span.Length, value.Length - span.Start);
            if (safeLength > 0)
            {
                DrawMergeTextPart(graphics, ref x, bounds, value.Substring(span.Start, safeLength), font, textColor, highlightColor);
            }

            cursor = Math.Max(cursor, span.Start + Math.Max(0, safeLength));
            if (x >= bounds.Right)
            {
                return;
            }
        }

        if (cursor < value.Length)
        {
            DrawMergeTextPart(graphics, ref x, bounds, value[cursor..], font, textColor, null);
        }
    }

    private static void DrawMergeTextPart(Graphics graphics, ref int x, Rectangle bounds, string text, Font font, Color textColor, Color? backColor)
    {
        if (string.IsNullOrEmpty(text) || x >= bounds.Right)
        {
            return;
        }

        var textSize = TextRenderer.MeasureText(graphics, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Min(textSize.Width, bounds.Right - x);
        var partBounds = new Rectangle(x, bounds.Top + 1, width, Math.Max(1, bounds.Height - 2));
        if (backColor != null)
        {
            using var brush = new SolidBrush(backColor.Value);
            graphics.FillRoundedRectangle(brush, partBounds, 3);
        }

        TextRenderer.DrawText(graphics, text, font, partBounds, textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        x += width;
    }

    private void ShowMergeCellDetail(SpreadsheetMergeConflictGridRow row)
    {
        var highlights = DiffHighlightSpans.Calculate(row.LocalValue, row.RemoteValue);
        using var form = new Form
        {
            Text = $"合并项目详情 - {row.Sheet}!{row.Address}",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(920, 560),
            Size = new Size(1100, 680),
            Font = Font,
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        form.Controls.Add(root);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Text = $"{row.KindText}    默认 {row.Sheet}!{row.Address}    写入 {row.WriteSheet}!{row.WriteAddress}    ID: {row.RowId}    字段: {row.FieldName}",
        }, 0, 0);
        root.Controls.Add(CreatePopupMergeValueBox("BASE", row.BaseValue, Color.FromArgb(71, 85, 105), Color.FromArgb(226, 232, 240), []), 0, 1);
        root.Controls.Add(CreatePopupMergeValueBox(_targetLabel + "（红底为与来源不同的位置）", row.LocalValue, Color.FromArgb(153, 27, 27), Color.FromArgb(254, 202, 202), highlights.OldSpans), 0, 2);
        root.Controls.Add(CreatePopupMergeValueBox(_sourceLabel + "（绿底为与目标不同的位置）", row.RemoteValue, Color.FromArgb(22, 101, 52), Color.FromArgb(187, 247, 208), highlights.NewSpans), 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "关闭", Width = 86, DialogResult = DialogResult.OK });
        root.Controls.Add(buttons, 0, 4);
        form.AcceptButton = buttons.Controls.OfType<Button>().First();
        form.ShowDialog(this);
    }

    private static Control CreatePopupMergeValueBox(string title, string value, Color textColor, Color highlightColor, IReadOnlyList<TextHighlightSpan> highlights)
    {
        var box = new RichTextBox();
        var panel = (TableLayoutPanel)CreateMergeDetailBox(title, box, textColor);
        panel.Margin = new Padding(0, 0, 0, 8);
        SetMergeDetailText(box, value, textColor, highlightColor, highlights);
        return panel;
    }

    private void ApplyRowStyles()
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is not SpreadsheetMergeConflictGridRow row)
            {
                continue;
            }

            gridRow.DefaultCellStyle.BackColor = row.Kind switch
            {
                SpreadsheetMergeChangeKind.AutoRemote => Color.FromArgb(235, 255, 239),
                SpreadsheetMergeChangeKind.LocalOnly => Color.FromArgb(239, 246, 255),
                SpreadsheetMergeChangeKind.SameBoth => Color.FromArgb(248, 250, 252),
                SpreadsheetMergeChangeKind.Conflict => Color.FromArgb(255, 247, 237),
                _ => Color.White,
            };
            if (!row.TargetRowExists && row.SourceCellExists)
            {
                gridRow.DefaultCellStyle.BackColor = Color.FromArgb(255, 251, 235);
            }
            gridRow.DefaultCellStyle.ForeColor = row.Kind == SpreadsheetMergeChangeKind.Conflict
                ? Color.FromArgb(124, 45, 18)
                : Color.FromArgb(30, 41, 59);
        }
    }

    private void SetAll(string resolution)
    {
        foreach (var row in _rows)
        {
            row.OperationText = resolution;
        }

        _source.ResetBindings(false);
        UpdateSummary();
        ApplyRowStyles();
        UpdateMergeDetail();
    }

    private void SynchronizeRowOperation(int rowIndex, int columnIndex)
    {
        if (_syncingRowOperation ||
            rowIndex < 0 ||
            rowIndex >= _grid.Rows.Count ||
            _grid.Rows[rowIndex].DataBoundItem is not SpreadsheetMergeConflictGridRow changedRow)
        {
            return;
        }

        var columnName = columnIndex >= 0 && columnIndex < _grid.Columns.Count
            ? _grid.Columns[columnIndex].Name
            : "";
        var shouldSync = columnName is "Operation" or "WriteAddress" ||
            changedRow.OperationText == _appendRowText ||
            changedRow.OperationText == _insertRowText ||
            changedRow.OperationText == _deleteRowText;
        if (!shouldSync ||
            string.IsNullOrWhiteSpace(changedRow.RowMergeKey))
        {
            return;
        }

        if (!SpreadsheetMergeConflictGridRow.TryParseCellAddress(changedRow.WriteAddress, out var targetRow, out _))
        {
            return;
        }

        _syncingRowOperation = true;
        try
        {
            foreach (var row in _rows.Where(row => string.Equals(row.RowMergeKey, changedRow.RowMergeKey, StringComparison.Ordinal)))
            {
                if (ReferenceEquals(row, changedRow))
                {
                    continue;
                }

                row.OperationText = changedRow.OperationText;
                if (changedRow.OperationText is var operation &&
                    (operation == _appendRowText || operation == _insertRowText || operation == _deleteRowText))
                {
                    row.WriteSheet = changedRow.WriteSheet;
                    row.WriteAddress = $"{row.WriteColumnName}{targetRow + 1}";
                }
            }

            _source.ResetBindings(false);
        }
        finally
        {
            _syncingRowOperation = false;
        }
    }

    private bool ApplyRowsToPlan()
    {
        _grid.EndEdit();
        foreach (var row in _rows)
        {
            if (!row.TryApplyToChange(out var error))
            {
                MessageBox.Show(this, error, "写入位置无效", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }

        var duplicateTarget = _plan.AllChanges
            .Where(change => change.Operation is SpreadsheetMergeOperation.WriteCell or SpreadsheetMergeOperation.AppendRow or SpreadsheetMergeOperation.InsertRow)
            .Where(change => !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal))
            .GroupBy(change => change.WriteCell)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget != null)
        {
            var target = duplicateTarget.Key;
            var address = $"{target.Sheet}!{ExcelDiffService.ToColumnName(target.Column)}{target.Row + 1}";
            MessageBox.Show(
                this,
                $"有多个合并项目会写入同一个目标单元格：{address}{Environment.NewLine}{Environment.NewLine}请先手动调整其中一项的写入位置，避免覆盖顺序不明确。",
                "写入位置重复",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private void UpdateSummary()
    {
        var writeCount = _rows.Count(row => row.OperationText != _keepTargetText);
        var missingTargetRows = _rows
            .Where(row => !row.TargetRowExists && row.SourceCellExists)
            .Select(row => row.RowMergeKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var plannedWrites = _rows.Count(row =>
            row.OperationText != _keepTargetText &&
            !string.Equals(row.LocalValue, row.RemoteValue, StringComparison.Ordinal));
        _summaryLabel.Text =
            $"可应用{_sourceLabel} {_plan.AutoRemoteChanges.Count} 项；{_targetLabel}独有 {_plan.LocalOnlyChanges.Count} 项；两边相同 {_plan.SameBothChanges.Count} 项；冲突 {_plan.Conflicts.Count} 项。{Environment.NewLine}" +
            $"当前选择写入/插入/删除 {writeCount} 项、保留 {_rows.Count - writeCount} 项；目标缺行 {missingTargetRows} 行；预计生成 {plannedWrites} 个写入动作。";
    }
}

internal sealed class SpreadsheetMergeConflictGridRow
{
    private readonly SpreadsheetMergeChange _change;
    private readonly string _keepTargetText;
    private readonly string _writeCellText;
    private readonly string _appendRowText;
    private readonly string _insertRowText;
    private readonly string _deleteRowText;

    public SpreadsheetMergeConflictGridRow(
        SpreadsheetMergeChange change,
        string keepTargetText,
        string writeCellText,
        string appendRowText,
        string insertRowText,
        string deleteRowText)
    {
        _change = change;
        _keepTargetText = keepTargetText;
        _writeCellText = writeCellText;
        _appendRowText = appendRowText;
        _insertRowText = insertRowText;
        _deleteRowText = deleteRowText;
        OperationText = OperationToText(change.Operation);
        WriteSheet = change.WriteCell.Sheet;
        WriteAddress = $"{ExcelDiffService.ToColumnName(change.WriteCell.Column)}{change.WriteCell.Row + 1}";
    }

    public string OperationText { get; set; }
    public string WriteSheet { get; set; }
    public string WriteAddress { get; set; }
    public SpreadsheetMergeChangeKind Kind => _change.Kind;
    public string KindText => _change.Kind switch
    {
        SpreadsheetMergeChangeKind.AutoRemote when !TargetRowExists && SourceCellExists => "来源新增行",
        SpreadsheetMergeChangeKind.AutoRemote when !SourceCellExists => "来源删除",
        SpreadsheetMergeChangeKind.AutoRemote => "可合并改动",
        SpreadsheetMergeChangeKind.LocalOnly => "目标独有",
        SpreadsheetMergeChangeKind.SameBoth => "双方相同",
        SpreadsheetMergeChangeKind.Conflict => "冲突",
        _ => "未知",
    };
    public string Sheet => _change.Sheet;
    public string Address => _change.Address;
    public string RowId => _change.RowId;
    public string FieldName => _change.FieldName;
    public string BaseValue => _change.BaseValue;
    public string LocalValue => _change.LocalValue;
    public string RemoteValue => _change.RemoteValue;
    public bool TargetCellExists => _change.TargetCellExists;
    public bool TargetRowExists => _change.TargetRowExists;
    public bool SourceCellExists => _change.SourceCellExists;
    public string RowMergeKey => _change.RowMergeKey;
    public string WriteColumnName => ExcelDiffService.ToColumnName(_change.WriteCell.Column);
    public string DefaultLocation => $"{Sheet}!{Address}";
    public string AlignmentText
    {
        get
        {
            if (!TargetRowExists && SourceCellExists)
            {
                return "目标缺行：先选新增/插入";
            }

            if (TargetRowExists && !TargetCellExists && SourceCellExists)
            {
                return "目标行存在：字段为空";
            }

            if (!SourceCellExists)
            {
                return "来源为空/删除";
            }

            return "已按 ID/字段对齐";
        }
    }
    public string ComparisonText => $"BASE {BaseValue}{Environment.NewLine}{LocalValue}{Environment.NewLine}{RemoteValue}";

    public bool TryApplyToChange(out string error)
    {
        error = "";
        var operation = TextToOperation(OperationText);
        if (operation == SpreadsheetMergeOperation.KeepTarget)
        {
            _change.Operation = SpreadsheetMergeOperation.KeepTarget;
            _change.Resolution = SpreadsheetMergeResolution.UseLocal;
            return true;
        }

        if (!TryParseCellAddress(WriteAddress, out var row, out var column))
        {
            error = $"写入单元格格式无效：{WriteAddress}{Environment.NewLine}{Environment.NewLine}请使用 A1、B23 这种格式。";
            return false;
        }

        var sheet = WriteSheet.Trim();
        if (string.IsNullOrWhiteSpace(sheet))
        {
            error = "写入工作表不能为空。";
            return false;
        }

        if ((operation is SpreadsheetMergeOperation.WriteCell or SpreadsheetMergeOperation.AppendRow or SpreadsheetMergeOperation.InsertRow) &&
            !SourceCellExists)
        {
            error = "来源内容为空，不能写入/新增/插入。请改选保留目标或删除目标行。";
            return false;
        }

        _change.Operation = operation;
        _change.Resolution = operation == SpreadsheetMergeOperation.KeepTarget
            ? SpreadsheetMergeResolution.UseLocal
            : SpreadsheetMergeResolution.UseRemote;
        _change.WriteCell = new ExcelCellKey(sheet, row, column);
        return true;
    }

    private string OperationToText(SpreadsheetMergeOperation operation)
    {
        return operation switch
        {
            SpreadsheetMergeOperation.WriteCell => _writeCellText,
            SpreadsheetMergeOperation.AppendRow => _appendRowText,
            SpreadsheetMergeOperation.InsertRow => _insertRowText,
            SpreadsheetMergeOperation.DeleteRow => _deleteRowText,
            _ => _keepTargetText,
        };
    }

    private SpreadsheetMergeOperation TextToOperation(string text)
    {
        if (text == _writeCellText)
        {
            return SpreadsheetMergeOperation.WriteCell;
        }

        if (text == _appendRowText)
        {
            return SpreadsheetMergeOperation.AppendRow;
        }

        if (text == _insertRowText)
        {
            return SpreadsheetMergeOperation.InsertRow;
        }

        if (text == _deleteRowText)
        {
            return SpreadsheetMergeOperation.DeleteRow;
        }

        return SpreadsheetMergeOperation.KeepTarget;
    }

    public static bool TryParseCellAddress(string address, out int row, out int column)
    {
        row = -1;
        column = -1;
        var text = (address ?? "").Trim();
        var match = Regex.Match(text, @"^([A-Za-z]+)([1-9]\d*)$");
        if (!match.Success)
        {
            return false;
        }

        var columnText = match.Groups[1].Value.ToUpperInvariant();
        var value = 0;
        foreach (var character in columnText)
        {
            value = value * 26 + character - 'A' + 1;
        }

        if (!int.TryParse(match.Groups[2].Value, out var oneBasedRow))
        {
            return false;
        }

        row = oneBasedRow - 1;
        column = value - 1;
        return row >= 0 && column >= 0;
    }
}

