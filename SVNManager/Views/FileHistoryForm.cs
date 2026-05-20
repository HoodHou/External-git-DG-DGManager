using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class FileHistoryForm : Form
{
    private readonly string _workingCopy;
    private readonly string _relativePath;
    private readonly IReadOnlyList<string> _relativePaths;
    private readonly string _displayPath;
    private readonly bool _enableFileDiffPreview;
    private readonly SvnClient _svn;
    private readonly Label _summaryLabel = new();
    private readonly TextBox _searchText = new();
    private readonly Label _searchScopeLabel = new();
    private readonly Button _deepSearchButton = new();
    private readonly Button _loadMoreButton = new();
    private readonly Button _clearSearchButton = new();
    private readonly ListView _historyList = new();
    private readonly ImageList _historyListRowImages = new();
    private readonly TextBox _detailText = new();
    private readonly Panel _diffPanel = new();
    private readonly TreeView _changedFilesTree = new();
    private readonly TextBox _changedFilesSearchText = new();
    private readonly ComboBox _changedFilesFilterCombo = new();
    private readonly ImageList _treeImages = new();
    private readonly Dictionary<string, DiffPreviewData> _diffPreviewCache = new(StringComparer.Ordinal);
    private List<SvnLogEntry> _logs;
    private IReadOnlyList<ChangedFileEntry> _changedFilesAll = [];
    private string _changedFilesRootText = "Changed files";
    private CancellationTokenSource? _diffPreviewCts;
    private int _loadedLimit = InitialHistoryLimit;
    private bool _loadingHistory;
    private ListViewItem? _hoveredHistoryItem;
    private const int InitialHistoryLimit = 80;
    private const int HistoryLoadMoreStep = 200;
    private const int HistoryDeepSearchLimit = 1000;
    private const int HistoryRevisionRangeLimit = 5000;
    private const int MaxFileHistoryDiffPreviewCacheEntries = 40;

    public FileHistoryForm(string workingCopy, string relativePath, IReadOnlyList<SvnLogEntry> logs, SvnClient svn)
        : this(workingCopy, [relativePath], logs, svn)
    {
    }

    public FileHistoryForm(string workingCopy, IReadOnlyList<string> relativePaths, IReadOnlyList<SvnLogEntry> logs, SvnClient svn)
    {
        _workingCopy = workingCopy;
        _relativePaths = relativePaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        _relativePath = _relativePaths.Count == 1 ? _relativePaths[0] : "";
        _displayPath = FormatPathListLabel(_relativePaths);
        _enableFileDiffPreview = _relativePaths.Count == 1 && File.Exists(Path.Combine(workingCopy, _relativePath));
        _logs = logs.OrderByDescending(log => log.Revision).ToList();
        _loadedLimit = Math.Max(InitialHistoryLimit, _logs.Count);
        _svn = svn;
        Text = _enableFileDiffPreview ? $"文件历史 - {_displayPath}" : $"路径历史 - {_displayPath}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1380, 880);
        Font = new Font("Microsoft YaHei UI", 9F);
        FormClosing += (_, _) => CancelDiffPreview();
        ConfigureTreeImages();

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        Controls.Add(root);
        Form1.BindSafeSplitterDistance(root, 250);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font(Font, FontStyle.Bold);
        top.Controls.Add(_summaryLabel, 0, 0);

        top.Controls.Add(CreateSearchPanel(), 0, 1);

        _historyList.Dock = DockStyle.Fill;
        _historyList.View = View.Details;
        _historyList.FullRowSelect = true;
        _historyList.GridLines = false;
        _historyList.HideSelection = false;
        _historyList.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _historyList.BackColor = Color.White;
        _historyList.OwnerDraw = true;
        WinFormsRendering.EnableDoubleBuffering(_historyList);
        _historyList.HeaderStyle = ColumnHeaderStyle.None;
        _historyListRowImages.ColorDepth = ColorDepth.Depth32Bit;
        _historyListRowImages.ImageSize = new Size(1, 54);
        _historyListRowImages.Images.Add("row-height", new Bitmap(1, 54));

        _historyList.SmallImageList = _historyListRowImages;
        _historyList.Columns.Add("Description", 620);
        _historyList.Columns.Add("Date", 150);
        _historyList.Columns.Add("Author", 120);
        _historyList.Columns.Add("Commit", 90);
        _historyList.DrawColumnHeader += (_, args) => args.DrawDefault = false;
        _historyList.DrawSubItem += DrawHistoryCardSubItem;
        _historyList.MouseMove += (_, args) =>
        {
            var previous = _hoveredHistoryItem;
            var item = _historyList.GetItemAt(args.X, args.Y);
            if (!ReferenceEquals(item, _hoveredHistoryItem))
            {
                _hoveredHistoryItem = item;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, item);
            }
        };
        _historyList.MouseLeave += (_, _) =>
        {
            if (_hoveredHistoryItem != null)
            {
                var previous = _hoveredHistoryItem;
                _hoveredHistoryItem = null;
                WinFormsRendering.InvalidateListViewItems(_historyList, previous, null);
            }
        };
        _historyList.SelectedIndexChanged += async (_, _) => await ShowSelectedFileRevisionAsync();
        _historyList.DoubleClick += (_, _) => FocusBestChangedFileInSelectedLog();
        top.Controls.Add(_historyList, 0, 2);
        root.Panel1.Controls.Add(top);

        var bottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        Form1.BindSafeSplitterDistance(bottom, 300);
        _detailText.Dock = DockStyle.Fill;
        _detailText.Multiline = true;
        _detailText.ReadOnly = true;
        _detailText.ScrollBars = ScrollBars.Both;
        _detailText.WordWrap = false;
        bottom.Panel1.Controls.Add(_detailText);

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        Form1.BindSafeSplitterDistance(right, 120);
        _changedFilesTree.Dock = DockStyle.Fill;
        _changedFilesTree.HideSelection = false;
        _changedFilesTree.FullRowSelect = true;
        _changedFilesTree.ShowLines = false;
        _changedFilesTree.ShowRootLines = false;
        _changedFilesTree.ItemHeight = 24;
        _changedFilesTree.BorderStyle = System.Windows.Forms.BorderStyle.None;
        _changedFilesTree.ImageList = _treeImages;
        _changedFilesTree.TreeViewNodeSorter = new FileTreeNodeSorter();
        _changedFilesTree.AfterSelect += async (_, _) => await ShowSelectedChangedFileDiffAsync();
        right.Panel1.Controls.Add(Form1.CreateChangedFilesFilterPanel(
            "Changed files",
            _changedFilesTree,
            _changedFilesSearchText,
            _changedFilesFilterCombo,
            ApplyChangedFilesFilter));

        _diffPanel.Dock = DockStyle.Fill;
        right.Panel2.Controls.Add(CreateTitledPanel("Diff preview", _diffPanel));
        bottom.Panel2.Controls.Add(right);
        root.Panel2.Controls.Add(bottom);

        LoadHistoryRows();
    }

    private Control CreateSearchPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        _searchText.Dock = DockStyle.Fill;
        _searchText.Margin = new Padding(0, 4, 6, 4);
        _searchText.PlaceholderText = "搜索：author:作者 rev:57100-57120 id:需求号 或普通关键词";
        _searchText.TextChanged += (_, _) => ApplyHistoryFilter(selectFirst: false);
        _searchText.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter)
            {
                return;
            }

            args.SuppressKeyPress = true;
            await RunDeepHistorySearchAsync();
        };
        panel.Controls.Add(_searchText, 0, 0);

        _searchScopeLabel.Dock = DockStyle.Fill;
        _searchScopeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _searchScopeLabel.ForeColor = Color.FromArgb(90, 100, 115);
        panel.Controls.Add(_searchScopeLabel, 1, 0);

        ConfigureSearchButton(_deepSearchButton, "深度搜索");
        _deepSearchButton.Click += async (_, _) => await RunDeepHistorySearchAsync();
        panel.Controls.Add(_deepSearchButton, 2, 0);

        ConfigureSearchButton(_loadMoreButton, "加载更多");
        _loadMoreButton.Click += async (_, _) => await LoadMoreHistoryAsync();
        panel.Controls.Add(_loadMoreButton, 3, 0);

        ConfigureSearchButton(_clearSearchButton, "清空");
        _clearSearchButton.Click += (_, _) => _searchText.Clear();
        panel.Controls.Add(_clearSearchButton, 4, 0);

        return panel;
    }

    private static void ConfigureSearchButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 6, 4);
    }

    private void LoadHistoryRows()
    {
        UpdateSummaryLabel();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter(bool selectFirst = true)
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        var rows = filter.IsEmpty
            ? _logs
            : _logs.Where(log => filter.Matches(log)).ToList();
        var selectedRevisions = _historyList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as SvnLogEntry)
            .Where(log => log != null)
            .Cast<SvnLogEntry>()
            .Select(log => log.Revision)
            .ToHashSet();
        ListViewItem? itemToSelect = null;
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var log in rows)
        {
            var item = new ListViewItem(log.ShortMessage) { Tag = log, ImageKey = "row-height" };
            item.SubItems.Add(log.LocalDateText);
            item.SubItems.Add(log.Author);
            item.SubItems.Add(log.RevisionText);
            _historyList.Items.Add(item);
            if (!selectFirst && selectedRevisions.Contains(log.Revision))
            {
                itemToSelect ??= item;
            }
        }
        _historyList.EndUpdate();

        UpdateSearchControls();
        if (_historyList.Items.Count == 0)
        {
            _detailText.Text = filter.IsEmpty
                ? ""
                : $"没有匹配的提交。当前只在已加载的 {_logs.Count} 条历史里搜索；可以点击“深度搜索”读取更早提交。";
            ClearChangedFiles();
            ShowDiffMessage(_detailText.Text);
            return;
        }

        itemToSelect ??= _historyList.Items[0];

        itemToSelect.Selected = true;
        itemToSelect.Focused = true;
        itemToSelect.EnsureVisible();
    }

    private void DrawHistoryCardSubItem(object? sender, DrawListViewSubItemEventArgs args)
    {
        args.DrawDefault = false;
        if (sender is not ListView list || args.Item?.Tag is not SvnLogEntry log || args.ColumnIndex != 0)
        {
            return;
        }

        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, args.Item.Bounds.Top + 5, Math.Max(1, list.ClientSize.Width - 12), args.Item.Bounds.Height - 8);
        var selected = args.Item.Selected;
        var hovered = ReferenceEquals(args.Item, _hoveredHistoryItem);
        var cardColor = selected
            ? Color.FromArgb(226, 241, 255)
            : hovered ? Color.FromArgb(248, 250, 252) : Color.White;
        var borderColor = selected
            ? Color.FromArgb(147, 197, 253)
            : hovered ? Color.FromArgb(203, 213, 225) : Color.FromArgb(226, 232, 240);
        using var cardBrush = new SolidBrush(cardColor);
        using var borderPen = new Pen(borderColor);
        using var revisionFont = new Font("Consolas", 9F, FontStyle.Bold);
        using var authorFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        using var metaFont = new Font("Microsoft YaHei UI", 8F);
        using var messageFont = new Font("Microsoft YaHei UI", 9F);
        graphics.FillRoundedRectangle(cardBrush, bounds, 8);
        graphics.DrawRoundedRectangle(borderPen, bounds, 8);

        using var accentBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        graphics.FillRoundedRectangle(accentBrush, new Rectangle(bounds.Left, bounds.Top + 9, 4, bounds.Height - 18), 3);

        TextRenderer.DrawText(
            graphics,
            log.RevisionText,
            revisionFont,
            new Rectangle(bounds.Left + 16, bounds.Top + 8, 82, 18),
            Color.FromArgb(37, 99, 235),
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.Author,
            authorFont,
            new Rectangle(bounds.Left + 104, bounds.Top + 8, 130, 18),
            Color.FromArgb(31, 41, 55),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.LocalDateText,
            metaFont,
            new Rectangle(bounds.Right - 150, bounds.Top + 8, 134, 18),
            Color.FromArgb(100, 116, 139),
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            log.ShortMessage,
            messageFont,
            new Rectangle(bounds.Left + 16, bounds.Top + 27, Math.Max(1, bounds.Width - 32), 20),
            Color.FromArgb(51, 65, 85),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void UpdateSummaryLabel()
    {
        _summaryLabel.Text = $"{_displayPath}    已加载 {_logs.Count} 条历史";
    }

    private void UpdateSearchControls()
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        _searchScopeLabel.Text = filter.IsEmpty
            ? $"已加载 {_logs.Count} 条"
            : $"匹配 {_historyList.Items.Count}/{_logs.Count} 条";
        _deepSearchButton.Enabled = !_loadingHistory;
        _loadMoreButton.Enabled = !_loadingHistory;
        _clearSearchButton.Enabled = !_loadingHistory && !string.IsNullOrWhiteSpace(_searchText.Text);
    }

    private async Task LoadMoreHistoryAsync()
    {
        await ReloadHistoryAsync(_loadedLimit + HistoryLoadMoreStep, "正在加载更多文件历史...");
    }

    private async Task RunDeepHistorySearchAsync()
    {
        var filter = HistorySearchFilter.Parse(_searchText.Text);
        if (filter.HasRevisionRange)
        {
            await LoadHistoryRevisionRangeAsync(filter);
            return;
        }

        await ReloadHistoryAsync(Math.Max(_loadedLimit, HistoryDeepSearchLimit), "正在深度搜索文件历史...");
    }

    private async Task ReloadHistoryAsync(int limit, string busyText)
    {
        SetHistoryBusy(true, busyText);
        try
        {
            var logs = await _svn.GetLogAsync(_workingCopy, _relativePaths, limit);
            _logs = logs.OrderByDescending(log => log.Revision).ToList();
            _loadedLimit = Math.Max(InitialHistoryLimit, limit);
            ClearDiffPreviewCache();
            LoadHistoryRows();
        }
        catch (Exception ex)
        {
            ShowDiffMessage(ex.Message);
            MessageBox.Show(ex.Message, "文件历史读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetHistoryBusy(false, "就绪");
        }
    }

    private async Task LoadHistoryRevisionRangeAsync(HistorySearchFilter filter)
    {
        if (filter.RevisionStart == null && filter.RevisionEnd == null)
        {
            return;
        }

        var start = filter.RevisionStart ?? filter.RevisionEnd!.Value;
        var end = filter.RevisionEnd ?? filter.RevisionStart!.Value;
        var rangeLimit = (int)Math.Min(HistoryRevisionRangeLimit, Math.Max(1, Math.Abs(end - start) + 1));
        SetHistoryBusy(true, $"正在读取版本范围 r{start}-r{end}...");
        try
        {
            var rangeLogs = await _svn.GetLogRangeAsync(_workingCopy, _relativePaths, start, end, rangeLimit);
            _logs = _logs
                .Concat(rangeLogs)
                .GroupBy(log => log.Revision)
                .Select(group => group.OrderByDescending(log => log.ChangedFiles.Count).First())
                .OrderByDescending(log => log.Revision)
                .ToList();
            ClearDiffPreviewCache();
            LoadHistoryRows();
            if (Math.Abs(end - start) + 1 > HistoryRevisionRangeLimit)
            {
                ShowDiffMessage($"版本范围过大，当前最多读取 {HistoryRevisionRangeLimit} 条。请缩小版本范围后再查。");
            }
        }
        catch (Exception ex)
        {
            ShowDiffMessage(ex.Message);
            MessageBox.Show(ex.Message, "文件历史读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetHistoryBusy(false, "就绪");
        }
    }

    private void SetHistoryBusy(bool busy, string message)
    {
        _loadingHistory = busy;
        UseWaitCursor = busy;
        _summaryLabel.Text = busy ? $"{_displayPath}    {message}" : $"{_displayPath}    已加载 {_logs.Count} 条历史";
        UpdateSearchControls();
    }

    private void ClearDiffPreviewCache()
    {
        CancelDiffPreview();
        _diffPreviewCache.Clear();
    }

    private async Task ShowSelectedFileRevisionAsync()
    {
        if (_historyList.SelectedItems.Count != 1 || _historyList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        _detailText.Text =
            $"版本：r{log.Revision}{Environment.NewLine}" +
            $"作者：{log.Author}{Environment.NewLine}" +
            $"时间：{log.LocalDateText}{Environment.NewLine}{Environment.NewLine}" +
            log.Message +
            Environment.NewLine + Environment.NewLine +
            $"路径：{_displayPath}{Environment.NewLine}{Environment.NewLine}" +
            $"Changed files ({log.ChangedFiles.Count}){Environment.NewLine}" +
            string.Join(Environment.NewLine, log.ChangedFiles.Select(file => file.DisplayText));

        PopulateChangedFilesTree(log);
        if (!_enableFileDiffPreview)
        {
            ShowChangedFilesHint();
            return;
        }

        var file = log.ChangedFiles.FirstOrDefault(change =>
            PathMatches(change.RelativePath, _relativePath) ||
            PathMatches(change.RepositoryPath.TrimStart('/'), _relativePath)) ??
            new ChangedFileEntry("M", "/trunk/" + _relativePath.Replace('\\', '/'), _relativePath);

        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(_relativePath));
        var oldTemp = DiffTempFileTracker.NewTempFile("SVNManager_FILE_OLD", extension);
        var newTemp = DiffTempFileTracker.NewTempFile("SVNManager_FILE_NEW", extension);
        var previewCts = BeginDiffPreview();
        var token = previewCts.Token;
        var title = $"r{log.Revision}  {_relativePath}";
        var cacheKey = BuildFileHistoryDiffCacheKey("file", log.Revision, file);
        if (TryRenderCachedDiff(title, cacheKey, token))
        {
            return;
        }

        ShowDiffLoading(title, "正在准备文件版本...");
        try
        {
            await Form1.PrepareCommittedDiffFilesAsync(_svn, _workingCopy, log.Revision, file, oldTemp, newTemp, token);
            await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowDiffMessage(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
        }
    }

    private void PopulateChangedFilesTree(SvnLogEntry log)
    {
        _changedFilesRootText = $"Changed files ({log.ChangedFiles.Count})";
        _changedFilesAll = log.ChangedFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.TreePath))
            .OrderBy(file => file.TreePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyChangedFilesFilter();
    }

    private void ClearChangedFiles()
    {
        _changedFilesAll = [];
        _changedFilesRootText = "Changed files";
        _changedFilesTree.Nodes.Clear();
    }

    private void ApplyChangedFilesFilter()
    {
        _changedFilesTree.BeginUpdate();
        _changedFilesTree.Nodes.Clear();
        try
        {
            var files = ChangedFilesFilter.Apply(
                _changedFilesAll,
                _changedFilesSearchText.Text,
                ChangedFilesFilter.GetMode(_changedFilesFilterCombo));
            var rootText = files.Count == _changedFilesAll.Count
                ? _changedFilesRootText
                : $"{_changedFilesRootText} - 显示 {files.Count}/{_changedFilesAll.Count}";
            var root = new TreeNode(rootText)
            {
                ImageKey = "folder",
                SelectedImageKey = "folder",
            };
            _changedFilesTree.Nodes.Add(root);
            foreach (var file in files)
            {
                AddChangedFileNode(root, file);
            }

            root.Expand();
            _changedFilesTree.Sort();
        }
        finally
        {
            _changedFilesTree.EndUpdate();
        }
    }

    private void FocusBestChangedFileInSelectedLog()
    {
        if (_historyList.SelectedItems.Count != 1 || _historyList.SelectedItems[0].Tag is not SvnLogEntry log)
        {
            return;
        }

        if (_changedFilesTree.Nodes.Count == 0)
        {
            PopulateChangedFilesTree(log);
        }

        var filter = HistorySearchFilter.Parse(_searchText.Text);
        var target = FindBestChangedFileNode(_changedFilesTree.Nodes.Cast<TreeNode>(), filter);
        if (target == null)
        {
            return;
        }

        target.EnsureVisible();
        _changedFilesTree.SelectedNode = target;
        _changedFilesTree.Focus();
    }

    private static TreeNode? FindBestChangedFileNode(IEnumerable<TreeNode> nodes, HistorySearchFilter filter)
    {
        if (!filter.IsEmpty)
        {
            var matched = FindChangedFileNode(nodes, file => filter.MatchesFileForNavigation(file));
            if (matched != null)
            {
                return matched;
            }
        }

        return FindFirstChangedFileNode(nodes);
    }

    private static TreeNode? FindChangedFileNode(IEnumerable<TreeNode> nodes, Func<ChangedFileEntry, bool> predicate)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry file && predicate(file))
            {
                return node;
            }

            var child = FindChangedFileNode(node.Nodes.Cast<TreeNode>(), predicate);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private static TreeNode? FindFirstChangedFileNode(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Tag is ChangedFileEntry)
            {
                return node;
            }

            var child = FindFirstChangedFileNode(node.Nodes.Cast<TreeNode>());
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private async Task ShowSelectedChangedFileDiffAsync()
    {
        if (_historyList.SelectedItems.Count != 1 ||
            _historyList.SelectedItems[0].Tag is not SvnLogEntry log ||
            _changedFilesTree.SelectedNode?.Tag is not ChangedFileEntry file)
        {
            return;
        }

        var extension = Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(file.TreePath));
        var oldTemp = DiffTempFileTracker.NewTempFile("SVNManager_PATH_OLD", extension);
        var newTemp = DiffTempFileTracker.NewTempFile("SVNManager_PATH_NEW", extension);
        var previewCts = BeginDiffPreview();
        var token = previewCts.Token;
        var title = $"r{log.Revision}  {file.DisplayText}";
        var cacheKey = BuildFileHistoryDiffCacheKey("changed-file", log.Revision, file);
        if (TryRenderCachedDiff(title, cacheKey, token))
        {
            return;
        }

        ShowDiffLoading(title, "正在准备文件版本...");
        try
        {
            await Form1.PrepareCommittedDiffFilesAsync(_svn, _workingCopy, log.Revision, file, oldTemp, newTemp, token);
            await ShowDiffPreviewAsync(title, oldTemp, newTemp, cacheKey, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowDiffMessage(ex.Message);
            }
        }
        finally
        {
            TryDelete(oldTemp);
            TryDelete(newTemp);
        }
    }

    private CancellationTokenSource BeginDiffPreview()
    {
        CancelDiffPreview();
        _diffPreviewCts = new CancellationTokenSource();
        return _diffPreviewCts;
    }

    private void CancelDiffPreview()
    {
        try
        {
            _diffPreviewCts?.Cancel();
        }
        catch
        {
        }
    }

    private bool TryRenderCachedDiff(string title, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_diffPreviewCache.TryGetValue(cacheKey, out var data))
        {
            return false;
        }

        Form1.RenderDiffPreviewInPanel(_diffPanel, null, title + "    [缓存]", data);
        return true;
    }

    private async Task ShowDiffPreviewAsync(string title, string oldFilePath, string newFilePath, string cacheKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ShowDiffLoading(title, "正在计算差异...");
        var data = await Task.Run(() => Form1.CreateDiffPreviewData(oldFilePath, newFilePath), token);
        token.ThrowIfCancellationRequested();
        AddDiffPreviewCache(cacheKey, data);
        Form1.RenderDiffPreviewInPanel(_diffPanel, null, title, data);
    }

    private void AddDiffPreviewCache(string cacheKey, DiffPreviewData data)
    {
        if (_diffPreviewCache.Count >= MaxFileHistoryDiffPreviewCacheEntries &&
            !_diffPreviewCache.ContainsKey(cacheKey))
        {
            _diffPreviewCache.Remove(_diffPreviewCache.Keys.First());
        }

        _diffPreviewCache[cacheKey] = data;
    }

    private void ShowDiffLoading(string title, string message)
    {
        Form1.ClearControlsDisposing(_diffPanel);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, title.Contains(Environment.NewLine, StringComparison.Ordinal) ? 46 : 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(248, 249, 250),
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(85, 95, 105),
        }, 0, 1);
        _diffPanel.Controls.Add(root);
    }

    private string BuildFileHistoryDiffCacheKey(string scope, long revision, ChangedFileEntry file)
    {
        return string.Join("|", scope, _workingCopy, revision.ToString(), file.Action, file.RepositoryPath, file.RelativePath, file.TreePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void ShowChangedFilesHint()
    {
        ShowDiffMessage("请选择右侧 Changed files 中的具体文件查看本次提交的内容差异。");
    }

    private void ShowDiffMessage(string message)
    {
        Form1.ClearControlsDisposing(_diffPanel);
        _diffPanel.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Text = message,
        });
    }

    private static Control CreateTitledPanel(string title, Control content)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(241, 243, 245),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        panel.Controls.Add(content, 0, 1);
        return panel;
    }

    private void ConfigureTreeImages()
    {
        _treeImages.ColorDepth = ColorDepth.Depth32Bit;
        _treeImages.ImageSize = new Size(16, 16);
        _treeImages.Images.Clear();
        _treeImages.Images.Add("folder", CreateTreeIcon(Color.FromArgb(219, 164, 64), true));
        _treeImages.Images.Add("file", CreateTreeIcon(Color.FromArgb(118, 128, 140), false));
        _treeImages.Images.Add("xml", CreateTreeIcon(Color.FromArgb(39, 132, 85), false));
        _treeImages.Images.Add("lua", CreateTreeIcon(Color.FromArgb(72, 99, 180), false));
        _treeImages.Images.Add("changed", CreateTreeIcon(Color.FromArgb(209, 92, 56), false));
        _treeImages.Images.Add("action-added", CreateActionTreeIcon("A", Color.FromArgb(35, 134, 83)));
        _treeImages.Images.Add("action-modified", CreateActionTreeIcon("M", Color.FromArgb(184, 107, 25)));
        _treeImages.Images.Add("action-deleted", CreateActionTreeIcon("D", Color.FromArgb(184, 66, 66)));
        _treeImages.Images.Add("action-conflicted", CreateActionTreeIcon("C", Color.FromArgb(164, 62, 176)));
        _treeImages.Images.Add("action-replaced", CreateActionTreeIcon("R", Color.FromArgb(109, 85, 184)));
        _treeImages.Images.Add("action-unknown", CreateActionTreeIcon("?", Color.FromArgb(100, 116, 139)));
    }

    private static Bitmap CreateTreeIcon(Color color, bool folder)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(ControlPaint.Dark(color), 1);
        if (folder)
        {
            graphics.FillRectangle(brush, 2, 5, 12, 8);
            graphics.FillRectangle(brush, 3, 3, 5, 3);
            graphics.DrawRectangle(pen, 2, 5, 12, 8);
        }
        else
        {
            graphics.FillRectangle(brush, 4, 2, 8, 12);
            graphics.DrawRectangle(pen, 4, 2, 8, 12);
            graphics.DrawLine(Pens.White, 6, 5, 10, 5);
            graphics.DrawLine(Pens.White, 6, 8, 10, 8);
        }

        return bitmap;
    }

    private static Bitmap CreateActionTreeIcon(string text, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 1, 2, 14, 12);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (16 - size.Width) / 2F, (16 - size.Height) / 2F - 0.5F);
        return bitmap;
    }

    private static void AddChangedFileNode(TreeNode root, ChangedFileEntry file)
    {
        var path = file.TreePath;
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var isFile = index == parts.Length - 1;
            var existing = current.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(CleanNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new TreeNode(isFile ? $"{file.Action} {part}" : part)
                {
                    Tag = isFile ? file : null,
                    ToolTipText = file.DisplayText,
                    ImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                    SelectedImageKey = isFile ? ChangedFileActionImageKey(file.Action) : "folder",
                    ForeColor = isFile ? ActionColor(file.Action) : Color.FromArgb(55, 65, 81),
                };
                if (!isFile)
                {
                    existing.NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                }

                current.Nodes.Add(existing);
            }

            current = existing;
        }
    }

    private static string ChangedFileActionImageKey(string action)
    {
        return action switch
        {
            "A" => "action-added",
            "M" => "action-modified",
            "D" => "action-deleted",
            "C" => "action-conflicted",
            "R" => "action-replaced",
            _ => "action-unknown",
        };
    }

    private static string FileImageKey(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return "xml";
        }

        return extension.Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "lua" : "file";
    }

    private static Color ActionColor(string action)
    {
        return action switch
        {
            "A" => Color.FromArgb(38, 128, 72),
            "D" => Color.FromArgb(170, 67, 67),
            "M" => Color.FromArgb(166, 103, 34),
            "C" => Color.FromArgb(144, 65, 170),
            "R" => Color.FromArgb(128, 79, 160),
            _ => SystemColors.WindowText,
        };
    }

    private static string CleanNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "MAD?!CR".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }

    private static bool PathMatches(string candidate, string expected)
    {
        var normalizedCandidate = candidate.Replace('\\', '/').Trim('/');
        var normalizedExpected = expected.Replace('\\', '/').Trim('/');
        return string.Equals(normalizedCandidate, normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.EndsWith("/" + normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPathListLabel(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return "";
        }

        if (relativePaths.Count == 1)
        {
            return relativePaths[0];
        }

        return $"{relativePaths.Count} 个路径：" + string.Join("、", relativePaths.Take(3)) + (relativePaths.Count > 3 ? "..." : "");
    }
}

