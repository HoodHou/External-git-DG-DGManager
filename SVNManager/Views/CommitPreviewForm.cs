using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class CommitPreviewForm : Form
{
    private readonly TextBox _messageBox = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _statusFilterBox = new();
    private readonly TreeView _commitTree = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _blockLabel = new();
    private readonly Label _messageStatsLabel = new();
    private readonly List<CommitPreviewRow> _rows;
    private readonly string? _globalBlockReason;
    private bool _syncingCommitTreeChecks;

    public string CommitMessage => _messageBox.Text.Trim();

    public IReadOnlyList<SvnChange> SelectedChanges => _rows
        .Where(row => row.Include && row.CanSubmit)
        .Select(row => row.Change)
        .ToList();

    public CommitPreviewForm(string message, IReadOnlyList<SvnChange> changes, Func<SvnChange, string> blockReasonFactory, string? globalBlockReason = null)
    {
        _globalBlockReason = globalBlockReason;
        _rows = changes
            .Select(change => new CommitPreviewRow(change, blockReasonFactory(change)))
            .ToList();

        Text = "准备提交";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 560);
        Size = new Size(1120, 680);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, string.IsNullOrWhiteSpace(globalBlockReason) ? 0 : 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _summaryLabel.ForeColor = Color.FromArgb(15, 23, 42);
        root.Controls.Add(_summaryLabel, 0, 0);

        _blockLabel.Dock = DockStyle.Fill;
        _blockLabel.TextAlign = ContentAlignment.MiddleLeft;
        _blockLabel.ForeColor = Color.FromArgb(185, 28, 28);
        _blockLabel.Font = new Font(Font, FontStyle.Bold);
        _blockLabel.Text = globalBlockReason ?? "";
        root.Controls.Add(_blockLabel, 0, 1);

        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
        };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var messagePanel = CreateCommitMessagePanel(message);
        messagePanel.Margin = new Padding(0, 0, 8, 0);
        var filesPanel = CreateCommitFilesPanel();
        filesPanel.Margin = new Padding(8, 0, 0, 0);
        contentLayout.Controls.Add(messagePanel, 0, 0);
        contentLayout.Controls.Add(filesPanel, 1, 0);
        root.Controls.Add(contentLayout, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = BackColor,
        };
        var okButton = new Button
        {
            Text = "确认提交",
            Width = 112,
            Height = 30,
            FlatStyle = FlatStyle.System,
        };
        var cancelButton = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.System,
        };
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                MessageBox.Show(this, "请先填写提交说明。", "缺少提交说明", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_globalBlockReason))
            {
                MessageBox.Show(this, _globalBlockReason, "提交被拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (SelectedChanges.Count == 0)
            {
                MessageBox.Show(this, "请至少保留一个要提交的文件。", "没有提交文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 3);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        ApplyFilter();
        UpdateCommitMessageStats();
        _messageBox.SelectAll();
    }

    private Control CreateCommitMessagePanel(string message)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "提交说明",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
        };
        panel.Controls.Add(title, 0, 0);

        _messageBox.Dock = DockStyle.Fill;
        _messageBox.Multiline = true;
        _messageBox.ReadOnly = false;
        _messageBox.ScrollBars = ScrollBars.Vertical;
        _messageBox.Text = message;
        _messageBox.BackColor = Color.White;
        _messageBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
        _messageBox.Font = new Font("Consolas", 10F);
        _messageBox.Margin = new Padding(0, 0, 0, 8);
        _messageBox.TextChanged += (_, _) => UpdateCommitMessageStats();
        panel.Controls.Add(_messageBox, 0, 1);

        _messageStatsLabel.Dock = DockStyle.Fill;
        _messageStatsLabel.TextAlign = ContentAlignment.MiddleLeft;
        _messageStatsLabel.ForeColor = Color.FromArgb(100, 116, 139);
        panel.Controls.Add(_messageStatsLabel, 0, 2);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "建议第一行写清本次改动目的；右侧可快速筛选、取消勾选不需要提交的文件。",
            ForeColor = Color.FromArgb(71, 85, 105),
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(10, 8, 10, 8),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(hint, 0, 3);
        return panel;
    }

    private Control CreateCommitFilesPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Color.White,
            Margin = Padding.Empty,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "待提交文件",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
        };
        panel.Controls.Add(title, 0, 0);

        var filterPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.PlaceholderText = "搜索文件 / 状态 / 说明";
        _searchBox.Margin = new Padding(0, 3, 8, 3);
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_searchBox, 0, 0);
        _statusFilterBox.Dock = DockStyle.Fill;
        _statusFilterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _statusFilterBox.Margin = new Padding(0, 3, 8, 3);
        _statusFilterBox.Items.AddRange(new object[] { "全部状态", "已修改", "已新增", "已删除", "未加入", "冲突", "不可提交" });
        _statusFilterBox.SelectedIndex = 0;
        _statusFilterBox.SelectedIndexChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_statusFilterBox, 1, 0);
        var selectAllButton = new Button { Text = "全选", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3), FlatStyle = FlatStyle.System };
        selectAllButton.Click += (_, _) => SetVisibleRowsIncluded(true);
        filterPanel.Controls.Add(selectAllButton, 2, 0);
        var selectNoneButton = new Button { Text = "全不选", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3), FlatStyle = FlatStyle.System };
        selectNoneButton.Click += (_, _) => SetVisibleRowsIncluded(false);
        filterPanel.Controls.Add(selectNoneButton, 3, 0);
        var clearButton = new Button { Text = "清空搜索", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), FlatStyle = FlatStyle.System };
        clearButton.Click += (_, _) =>
        {
            _searchBox.Clear();
            _statusFilterBox.SelectedIndex = 0;
        };
        filterPanel.Controls.Add(clearButton, 4, 0);
        panel.Controls.Add(filterPanel, 0, 1);

        _commitTree.Dock = DockStyle.Fill;
        _commitTree.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _commitTree.BackColor = Color.White;
        _commitTree.ForeColor = Color.FromArgb(30, 41, 59);
        _commitTree.CheckBoxes = true;
        _commitTree.HideSelection = false;
        _commitTree.HotTracking = false;
        _commitTree.ShowNodeToolTips = true;
        _commitTree.ShowLines = false;
        _commitTree.ShowPlusMinus = false;
        _commitTree.ShowRootLines = false;
        _commitTree.ItemHeight = 30;
        _commitTree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        WinFormsRendering.EnableDoubleBuffering(_commitTree);
        _commitTree.AfterCheck += CommitTreeAfterCheck;
        _commitTree.DrawNode += DrawCommitTreeNode;
        _commitTree.NodeMouseDoubleClick += (_, args) => args.Node.Toggle();
        panel.Controls.Add(_commitTree, 0, 2);
        return panel;
    }

    private void UpdateCommitMessageStats()
    {
        var trimmedLength = _messageBox.Text.Trim().Length;
        var lineCount = string.IsNullOrEmpty(_messageBox.Text) ? 0 : _messageBox.Lines.Length;
        _messageStatsLabel.Text = $"提交说明 {trimmedLength} 字 / {lineCount} 行";
        _messageStatsLabel.ForeColor = trimmedLength == 0
            ? Color.FromArgb(185, 28, 28)
            : Color.FromArgb(100, 116, 139);
    }

    private void ApplyFilter()
    {
        var keyword = _searchBox.Text.Trim();
        var statusFilter = _statusFilterBox.SelectedItem?.ToString() ?? "全部状态";
        var visibleRows = string.IsNullOrWhiteSpace(keyword)
            ? _rows
            : _rows.Where(row =>
                row.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Status.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.BlockReason.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        visibleRows = visibleRows.Where(row => MatchesCommitStatusFilter(row, statusFilter)).ToList();
        PopulateCommitTree(visibleRows);
        UpdateSummary();
    }

    private static bool MatchesCommitStatusFilter(CommitPreviewRow row, string statusFilter)
    {
        return statusFilter switch
        {
            "已修改" => row.Status == "已修改",
            "已新增" => row.Status == "已新增",
            "已删除" => row.Status == "已删除",
            "未加入" => row.Status == "未加入",
            "冲突" => row.Status == "冲突",
            "不可提交" => !row.CanSubmit,
            _ => true,
        };
    }

    private void SetVisibleRowsIncluded(bool include)
    {
        _syncingCommitTreeChecks = true;
        foreach (TreeNode node in FlattenCommitNodes(_commitTree.Nodes))
        {
            if (node.Tag is CommitPreviewRow row)
            {
                row.Include = include && row.CanSubmit && string.IsNullOrWhiteSpace(_globalBlockReason);
                node.Checked = row.Include;
            }
        }

        UpdateCommitFolderChecks(_commitTree.Nodes);
        _syncingCommitTreeChecks = false;
        _commitTree.Invalidate();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selectedCount = _rows.Count(row => row.Include);
        var blockedCount = _rows.Count(row => !row.CanSubmit);
        _summaryLabel.Text = blockedCount == 0
            ? $"准备提交 {selectedCount} / {_rows.Count} 个文件"
            : $"准备提交 {selectedCount} / {_rows.Count} 个文件，{blockedCount} 个文件不可提交";
    }

    private void PopulateCommitTree(IReadOnlyList<CommitPreviewRow> rows)
    {
        _syncingCommitTreeChecks = true;
        _commitTree.BeginUpdate();
        _commitTree.Nodes.Clear();

        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = row.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var collection = _commitTree.Nodes;
            var prefix = "";
            for (var index = 0; index < Math.Max(0, parts.Length - 1); index++)
            {
                prefix = string.IsNullOrEmpty(prefix) ? parts[index] : prefix + "/" + parts[index];
                if (!folders.TryGetValue(prefix, out var folderNode))
                {
                    folderNode = new TreeNode(parts[index])
                    {
                        Name = prefix,
                        Tag = CommitPreviewFolderNode.Instance,
                    };
                    collection.Add(folderNode);
                    folders[prefix] = folderNode;
                }

                collection = folderNode.Nodes;
            }

            var fileName = parts.Length == 0 ? row.RelativePath : parts[^1];
            var fileNode = new TreeNode(fileName)
            {
                Tag = row,
                Checked = row.Include,
                ToolTipText = string.IsNullOrWhiteSpace(row.BlockReason)
                    ? $"{row.Status}  {row.RelativePath}"
                    : $"{row.Status}  {row.RelativePath}\r\n{row.BlockReason}",
            };
            collection.Add(fileNode);
        }

        UpdateCommitFolderChecks(_commitTree.Nodes);
        ExpandCommitTree(_commitTree.Nodes, rows.Count <= 160);
        _commitTree.EndUpdate();
        _syncingCommitTreeChecks = false;
    }

    private void CommitTreeAfterCheck(object? sender, TreeViewEventArgs args)
    {
        if (_syncingCommitTreeChecks || args.Node == null)
        {
            return;
        }

        _syncingCommitTreeChecks = true;
        SetCommitNodeChecked(args.Node, args.Node.Checked);
        UpdateCommitFolderChecks(_commitTree.Nodes);
        _syncingCommitTreeChecks = false;
        _commitTree.Invalidate();
        UpdateSummary();
    }

    private void SetCommitNodeChecked(TreeNode node, bool include)
    {
        if (node.Tag is CommitPreviewRow row)
        {
            row.Include = include && row.CanSubmit && string.IsNullOrWhiteSpace(_globalBlockReason);
            node.Checked = row.Include;
            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            SetCommitNodeChecked(child, include);
        }
    }

    private static void UpdateCommitFolderChecks(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is CommitPreviewFolderNode)
            {
                UpdateCommitFolderChecks(node.Nodes);
                node.Checked = node.Nodes.Cast<TreeNode>().Any(child => child.Checked);
            }
        }
    }

    private static void ExpandCommitTree(TreeNodeCollection nodes, bool expandAll)
    {
        foreach (TreeNode node in nodes)
        {
            if (expandAll || node.Level < 1)
            {
                node.Expand();
            }

            ExpandCommitTree(node.Nodes, expandAll);
        }
    }

    private static IEnumerable<TreeNode> FlattenCommitNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in FlattenCommitNodes(node.Nodes))
            {
                yield return child;
            }
        }
    }

    private static void DrawCommitTreeNode(object? sender, DrawTreeNodeEventArgs args)
    {
        if (sender is not TreeView tree || args.Node == null)
        {
            return;
        }

        args.DrawDefault = false;
        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var selected = (args.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var hot = (args.State & TreeNodeStates.Hot) == TreeNodeStates.Hot;
        var fullBounds = new Rectangle(4, args.Bounds.Top + 2, Math.Max(1, tree.ClientSize.Width - 8), tree.ItemHeight - 4);
        var backColor = selected
            ? Color.FromArgb(226, 241, 255)
            : hot ? Color.FromArgb(248, 250, 252) : Color.White;
        using var backBrush = new SolidBrush(backColor);
        graphics.FillRoundedRectangle(backBrush, fullBounds, 7);

        var textBounds = args.Bounds;
        var x = textBounds.Left;
        if (args.Node.Nodes.Count > 0)
        {
            using var arrowFont = new Font("Segoe UI", 7F);
            TextRenderer.DrawText(
                graphics,
                args.Node.IsExpanded ? "▼" : "▶",
                arrowFont,
                new Rectangle(x, args.Bounds.Top + 6, 16, 16),
                Color.FromArgb(100, 116, 139),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 18;
        }

        if (args.Node.Tag is CommitPreviewRow row)
        {
            var statusColor = row.CanSubmit && string.IsNullOrWhiteSpace(row.BlockReason)
                ? row.Status switch
                {
                    "已新增" => Color.FromArgb(22, 163, 74),
                    "已删除" => Color.FromArgb(220, 38, 38),
                    "已修改" => Color.FromArgb(202, 138, 4),
                    "冲突" => Color.FromArgb(185, 28, 28),
                    _ => Color.FromArgb(71, 85, 105),
                }
                : Color.FromArgb(185, 28, 28);
            using var statusBrush = new SolidBrush(Color.FromArgb(24, statusColor));
            using var statusPen = new Pen(Color.FromArgb(80, statusColor));
            var badgeBounds = new Rectangle(x, args.Bounds.Top + 6, 44, 18);
            graphics.FillRoundedRectangle(statusBrush, badgeBounds, 5);
            graphics.DrawRoundedRectangle(statusPen, badgeBounds, 5);
            TextRenderer.DrawText(
                graphics,
                row.Status,
                tree.Font,
                badgeBounds,
                statusColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 52;

            var pathText = args.Node.Level == 0 ? row.RelativePath : args.Node.Text;
            using var fileFont = new Font(tree.Font, row.CanSubmit ? FontStyle.Regular : FontStyle.Strikeout);
            TextRenderer.DrawText(
                graphics,
                pathText,
                fileFont,
                new Rectangle(x, args.Bounds.Top + 3, Math.Max(1, tree.ClientSize.Width - x - 8), 22),
                row.CanSubmit ? Color.FromArgb(30, 41, 59) : Color.FromArgb(153, 27, 27),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        using var folderFont = new Font(tree.Font, FontStyle.Bold);
        TextRenderer.DrawText(
            graphics,
            args.Node.Text,
            folderFont,
            new Rectangle(x, args.Bounds.Top + 3, Math.Max(1, tree.ClientSize.Width - x - 8), 22),
            Color.FromArgb(51, 65, 85),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class CommitPreviewRow
{
    public CommitPreviewRow(SvnChange change, string blockReason)
    {
        Change = change;
        Status = change.DisplayStatus;
        RelativePath = change.RelativePath;
        Description = change.Description;
        BlockReason = blockReason;
        CanSubmit = string.IsNullOrWhiteSpace(blockReason);
        Include = CanSubmit;
    }

    public bool Include { get; set; }
    public bool CanSubmit { get; }
    public string Status { get; }
    public string RelativePath { get; }
    public string Description { get; }
    public string BlockReason { get; }
    public SvnChange Change { get; }
}

internal sealed class CommitPreviewFolderNode
{
    public static CommitPreviewFolderNode Instance { get; } = new();

    private CommitPreviewFolderNode()
    {
    }
}

