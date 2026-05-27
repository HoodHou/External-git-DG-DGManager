using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

public partial class Form1
{
    private Control CreateAllFilesPanel()
    {
        _allFilesView.FilterChanged += (_, _) => ScheduleFileTreeLoad();
        _allFilesView.ExpandRequested += (_, _) => ExpandFileTreeSafely();
        _allFilesView.CollapseRequested += (_, _) => CollapseFileTreeToRoot();
        _allFilesView.RefreshRequested += (_, _) =>
        {
            if (_fileTreeLoadCts != null)
            {
                CancelFileTreeLoad();
                return;
            }

            LoadAllFiles();
        };
        return _allFilesView;
    }

    private void LoadAllFiles()
    {
        _ = LoadAllFilesAsync();
    }

    private void ScheduleFileTreeLoad()
    {
        if (!IsTab(_mainTabs.SelectedTab, "全部文件"))
        {
            return;
        }

        _fileTreeLoadDebounceTimer.Stop();
        _fileTreeLoadDebounceTimer.Start();
    }

    private async Task LoadAllFilesAsync()
    {
        _fileTreeLoadDebounceTimer.Stop();
        var context = TryGetWorkingCopyContext();
        var root = context?.SelectedPath ?? _configView.WorkingCopyPath.Trim();
        var svnRoot = context?.RootPath ?? root;
        var scopeRelativePath = context?.ScopeRelativePath ?? "";
        var search = _allFilesView.SearchTextBox.Text.Trim();
        var changedOnly = _allFilesView.ChangedOnlyCheck.Checked;
        var isFiltering = changedOnly || !string.IsNullOrWhiteSpace(search);
        var expandedPaths = isFiltering ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : GetExpandedTreePaths();
        if (expandedPaths.Count == 0 && !string.IsNullOrWhiteSpace(root))
        {
            expandedPaths = _settings.GetExpandedPaths(root);
        }

        CancelFileTreeLoad();
        var loadCts = new CancellationTokenSource();
        _fileTreeLoadCts = loadCts;
        var token = loadCts.Token;
        var request = new FileTreeLoadRequest(root, svnRoot, scopeRelativePath, search, changedOnly, isFiltering, expandedPaths);
        ShowFileTreeMessage(string.IsNullOrWhiteSpace(root) ? "请选择本地目录。" : "正在加载文件树...");
        _allFilesView.SetLoadingControls(loading: true);
        _statusLabel.Text = "正在加载全部文件...";
        try
        {
            var result = await Task.Run(() => BuildFileTree(request, token), token);
            if (!IsCurrentFileTreeLoad(loadCts) || token.IsCancellationRequested)
            {
                return;
            }

            ApplyFileTreeBuildResult(result);
            _statusLabel.Text = result.RootNode == null
                ? "就绪"
                : result.IsLazy
                    ? $"已加载根目录，展开文件夹时继续读取。SVN 状态 {result.StatusMap.Count} 项"
                    : result.IsTruncated
                    ? $"已显示前 {result.FileCount} 个匹配文件，建议搜索缩小范围"
                    : $"已加载 {result.FileCount} 个文件";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentFileTreeLoad(loadCts))
            {
                _statusLabel.Text = "已取消加载全部文件";
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && IsCurrentFileTreeLoad(loadCts))
            {
                ShowFileTreeMessage("文件树加载失败，请查看终端输出。");
                WriteOutput(ex.ToString());
                _statusLabel.Text = "就绪";
            }
        }
        finally
        {
            if (IsCurrentFileTreeLoad(loadCts))
            {
                _fileTreeLoadCts = null;
                _allFilesView.SetLoadingControls(loading: false);
            }
        }
    }

    private void CancelFileTreeLoad()
    {
        try
        {
            _fileTreeLoadCts?.Cancel();
        }
        catch
        {
        }
    }

    private bool IsCurrentFileTreeLoad(CancellationTokenSource loadCts)
    {
        return ReferenceEquals(_fileTreeLoadCts, loadCts);
    }

    private static IEnumerable<string> EnumerateWorkingCopyFiles(string rootPath, CancellationToken token)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);
        while (pendingDirectories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var childDirectory in subDirectories.Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(childDirectory), ".svn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                yield return file;
            }
        }
    }

    private static IEnumerable<FileTreeFileEntry> EnumerateFilteredWorkingCopyFiles(
        string searchRootPath,
        string svnRootPath,
        string search,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap,
        CancellationToken token)
    {
        foreach (var filePath in EnumerateWorkingCopyFiles(searchRootPath, token))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(svnRootPath, filePath);
            if (SvnConflictArtifact.IsAuxiliaryPath(relativePath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search) &&
                !relativePath.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(filePath).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new FileTreeFileEntry(relativePath, filePath, ResolveFileTreeStatus(relativePath, statusMap));
        }
    }

    private static IEnumerable<FileTreeFileEntry> EnumerateChangedStatusFiles(
        string svnRootPath,
        string scopeRelativePath,
        string search,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap,
        CancellationToken token)
    {
        foreach (var item in statusMap.OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (item.Value is SvnStatusKind.None or SvnStatusKind.Normal)
            {
                continue;
            }

            if (SvnConflictArtifact.IsAuxiliaryPath(item.Key))
            {
                continue;
            }

            if (!IsPathInsideScope(item.Key, scopeRelativePath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search) &&
                !item.Key.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(item.Key).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new FileTreeFileEntry(item.Key, Path.Combine(svnRootPath, item.Key), item.Value);
        }
    }

    private static int PopulateLazyDirectoryNode(
        TreeNode directoryNode,
        string svnRootPath,
        string relativeDirectory,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directoryNode.Nodes.Clear();
        var fullDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? svnRootPath
            : Path.Combine(svnRootPath, relativeDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            return 0;
        }

        var added = 0;
        foreach (var directory in SafeEnumerateDirectories(fullDirectory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(directory), ".svn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(svnRootPath, directory);
            if (IsReservedDevicePath(relativePath))
            {
                continue;
            }

            var status = ResolveFileTreeStatus(relativePath, statusMap);
            var node = CreateFileTreeFolderNode(relativePath, Path.GetFileName(directory), status);
            if (DirectoryMayHaveChildren(directory))
            {
                node.Nodes.Add(CreateLazyFileTreePlaceholder());
            }

            directoryNode.Nodes.Add(node);
            added++;
        }

        foreach (var file in SafeEnumerateFiles(fullDirectory).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(svnRootPath, file);
            if (SvnConflictArtifact.IsAuxiliaryPath(relativePath) || IsReservedDevicePath(relativePath))
            {
                continue;
            }

            var status = ResolveFileTreeStatus(relativePath, statusMap);
            directoryNode.Nodes.Add(CreateFileTreeFileNode(relativePath, new FileInfo(file), status));
            added++;
        }

        return added;
    }

    private void EnsureLazyFileTreeChildren(TreeNode node)
    {
        if (node.Tag is not FileTreeNodeInfo { IsFile: false } info ||
            node.Nodes.Count != 1 ||
            node.Nodes[0].Tag is not LazyFileTreePlaceholder)
        {
            return;
        }

        var context = TryGetWorkingCopyContext();
        var root = context?.RootPath ?? _configView.WorkingCopyPath.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        _allFilesView.IsLoadingFileTree = true;
        WinFormsRendering.SetRedraw(_allFilesView.FileTree, false);
        _allFilesView.FileTree.BeginUpdate();
        try
        {
            PopulateLazyDirectoryNode(node, root, info.RelativePath, _allFilesView.StatusMap, CancellationToken.None);
        }
        finally
        {
            _allFilesView.FileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_allFilesView.FileTree, true);
            _allFilesView.IsLoadingFileTree = false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool DirectoryMayHaveChildren(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Any(path => !string.Equals(Path.GetFileName(path), ".svn", StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private FileTreeBuildResult BuildFileTree(FileTreeLoadRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            return new FileTreeBuildResult(null, "请选择本地目录。", 0, false, false, request.IsFiltering, request.ExpandedPaths, new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase));
        }

        if (!Directory.Exists(request.RootPath))
        {
            return new FileTreeBuildResult(null, "本地目录不存在。", 0, false, false, request.IsFiltering, request.ExpandedPaths, new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase));
        }

        var rootInfo = new DirectoryInfo(request.RootPath);
        var rootNode = new TreeNode(rootInfo.Name)
        {
            Tag = new FileTreeNodeInfo(request.ScopeRelativePath, false),
            ToolTipText = request.RootPath,
            ImageKey = "folder",
            SelectedImageKey = "folder",
        };

        var statusMap = GetStatusMapForTree(request.SvnRootPath);
        var rootStatus = ResolveFileTreeStatus(request.ScopeRelativePath, statusMap);
        if (ShouldDisplayFileTreeStatus(rootStatus))
        {
            StyleFileTreeNodeForStatus(rootNode, rootStatus, rootInfo.Name, isFile: false);
        }

        token.ThrowIfCancellationRequested();
        if (!request.IsFiltering)
        {
            var topLevelCount = PopulateLazyDirectoryNode(rootNode, request.SvnRootPath, request.ScopeRelativePath, statusMap, token);
            return new FileTreeBuildResult(rootNode, "", topLevelCount, false, true, request.IsFiltering, request.ExpandedPaths, statusMap);
        }

        var files = new List<FileTreeFileEntry>();
        var isTruncated = false;
        var filteredFiles = request.ChangedOnly
            ? EnumerateChangedStatusFiles(request.SvnRootPath, request.ScopeRelativePath, request.Search, statusMap, token)
            : EnumerateFilteredWorkingCopyFiles(request.RootPath, request.SvnRootPath, request.Search, statusMap, token);
        foreach (var file in filteredFiles)
        {
            token.ThrowIfCancellationRequested();
            var normalized = NormalizeRelativePath(file.RelativePath);
            var hasStatus = statusMap.TryGetValue(normalized, out var status) && status != SvnStatusKind.None && status != SvnStatusKind.Normal;
            if (request.ChangedOnly && !hasStatus)
            {
                continue;
            }

            if (files.Count >= MaxFileTreeDisplayFiles)
            {
                isTruncated = true;
                break;
            }

            files.Add(file with { Status = status });
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.CurrentCultureIgnoreCase));
        if (isTruncated)
        {
            rootNode.Nodes.Add(new TreeNode($"只显示前 {MaxFileTreeDisplayFiles} 个匹配文件，请用搜索或“仅改动”缩小范围。"));
        }

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            AddFileNode(rootNode, file.RelativePath, new FileInfo(file.FullPath), statusMap);
        }

        return new FileTreeBuildResult(rootNode, "", files.Count, isTruncated, false, request.IsFiltering, request.ExpandedPaths, statusMap);
    }

    private void ApplyFileTreeBuildResult(FileTreeBuildResult result)
    {
        _allFilesView.IsLoadingFileTree = true;
        _allFilesView.LastFileCount = result.FileCount;
        _allFilesView.StatusMap = result.StatusMap;
        _allFilesView.StyledSelectionPaths.Clear();
        WinFormsRendering.SetRedraw(_allFilesView.FileTree, false);
        _allFilesView.FileTree.BeginUpdate();
        _allFilesView.FileTree.Nodes.Clear();
        try
        {
            if (result.RootNode == null)
            {
                _allFilesView.FileTree.Nodes.Add(new TreeNode(result.Message));
                return;
            }

            _allFilesView.FileTree.Nodes.Add(result.RootNode);
            if (result.IsFiltering)
            {
                if (result.FileCount <= MaxFileTreeAutoExpandFiles)
                {
                    result.RootNode.ExpandAll();
                }
                else
                {
                    result.RootNode.Expand();
                }
            }
            else
            {
                RestoreExpandedTreePaths(result.ExpandedPaths);
            }

            PruneFileTreeSelection();
            ApplyFileTreeSelectionStyles();
        }
        finally
        {
            _allFilesView.FileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_allFilesView.FileTree, true);
            _allFilesView.FileTree.Invalidate();
            _allFilesView.IsLoadingFileTree = false;
        }
    }

    private void ShowFileTreeMessage(string message)
    {
        _allFilesView.IsLoadingFileTree = true;
        WinFormsRendering.SetRedraw(_allFilesView.FileTree, false);
        _allFilesView.FileTree.BeginUpdate();
        try
        {
            _allFilesView.FileTree.Nodes.Clear();
            _allFilesView.FileTree.Nodes.Add(new TreeNode(message));
        }
        finally
        {
            _allFilesView.FileTree.EndUpdate();
            WinFormsRendering.SetRedraw(_allFilesView.FileTree, true);
            _allFilesView.FileTree.Invalidate();
            _allFilesView.IsLoadingFileTree = false;
        }
    }

    private void CollapseFileTreeToRoot()
    {
        _allFilesView.FileTree.CollapseAll();
        if (_allFilesView.FileTree.Nodes.Count > 0)
        {
            _allFilesView.FileTree.Nodes[0].Expand();
        }
    }

    private void ExpandFileTreeSafely()
    {
        if (_allFilesView.LastFileCount > MaxFileTreeExpandAllFiles)
        {
            MessageBox.Show(
                this,
                $"当前文件树有 {_allFilesView.LastFileCount} 个文件，一次性全部展开会明显卡顿。\r\n\r\n请先使用搜索或“仅改动”，或者手动展开需要查看的目录。",
                "文件过多",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (_allFilesView.FileTree.Nodes.Count > 0)
            {
                _allFilesView.FileTree.Nodes[0].Expand();
            }

            return;
        }

        _allFilesView.FileTree.ExpandAll();
    }

    private void HandleFileTreeNodeMouseClick(TreeNode? node, MouseButtons button)
    {
        if (node == null)
        {
            return;
        }

        if (button == MouseButtons.Left)
        {
            SelectFileTreeNode(node, ModifierKeys);
            return;
        }

        if (button == MouseButtons.Right)
        {
            if (!IsFileTreeNodeSelected(node))
            {
                SelectFileTreeNode(node, Keys.None);
            }

            _allFilesView.FileTree.SelectedNode = node;
        }
    }

    private void SelectFileTreeNode(TreeNode node, Keys modifiers)
    {
        var path = GetFileTreeSelectionPath(node);
        if (path == null)
        {
            _allFilesView.SelectedPaths.Clear();
            _allFilesView.SelectionAnchorPath = null;
            _allFilesView.FileTree.SelectedNode = node;
            ApplyFileTreeSelectionStyles();
            return;
        }

        if (modifiers.HasFlag(Keys.Shift) && !string.IsNullOrWhiteSpace(_allFilesView.SelectionAnchorPath))
        {
            SelectFileTreeRange(_allFilesView.SelectionAnchorPath, path);
        }
        else if (modifiers.HasFlag(Keys.Control))
        {
            if (!_allFilesView.SelectedPaths.Add(path))
            {
                _allFilesView.SelectedPaths.Remove(path);
            }
        }
        else
        {
            _allFilesView.SelectedPaths.Clear();
            _allFilesView.SelectedPaths.Add(path);
        }

        _allFilesView.SelectionAnchorPath = path;
        _allFilesView.FileTree.SelectedNode = node;
        ApplyFileTreeSelectionStyles();
    }

    private void SelectFileTreeRange(string anchorPath, string currentPath)
    {
        var visibleNodes = GetVisibleFileTreeNodes().ToList();
        var anchorIndex = visibleNodes.FindIndex(node => string.Equals(GetFileTreeSelectionPath(node), anchorPath, StringComparison.OrdinalIgnoreCase));
        var currentIndex = visibleNodes.FindIndex(node => string.Equals(GetFileTreeSelectionPath(node), currentPath, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0 || currentIndex < 0)
        {
            _allFilesView.SelectedPaths.Clear();
            _allFilesView.SelectedPaths.Add(currentPath);
            return;
        }

        _allFilesView.SelectedPaths.Clear();
        var start = Math.Min(anchorIndex, currentIndex);
        var end = Math.Max(anchorIndex, currentIndex);
        for (var index = start; index <= end; index++)
        {
            var path = GetFileTreeSelectionPath(visibleNodes[index]);
            if (path != null)
            {
                _allFilesView.SelectedPaths.Add(path);
            }
        }
    }

    private IEnumerable<TreeNode> GetVisibleFileTreeNodes()
    {
        foreach (TreeNode node in _allFilesView.FileTree.Nodes)
        {
            foreach (var child in EnumerateVisibleNodes(node))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<TreeNode> EnumerateVisibleNodes(TreeNode node)
    {
        yield return node;
        if (!node.IsExpanded)
        {
            yield break;
        }

        foreach (TreeNode child in node.Nodes)
        {
            foreach (var visibleChild in EnumerateVisibleNodes(child))
            {
                yield return visibleChild;
            }
        }
    }

    private bool IsFileTreeNodeSelected(TreeNode node)
    {
        var path = GetFileTreeSelectionPath(node);
        return path != null && _allFilesView.SelectedPaths.Contains(path);
    }

    private static string? GetFileTreeSelectionPath(TreeNode? node)
    {
        if (node?.Tag is not FileTreeNodeInfo info || string.IsNullOrWhiteSpace(info.RelativePath))
        {
            return null;
        }

        return NormalizeRelativePath(SvnConflictArtifact.NormalizeToBasePath(info.RelativePath));
    }

    private void ApplyFileTreeSelectionStyles()
    {
        var pathsToRefresh = _allFilesView.StyledSelectionPaths
            .Concat(_allFilesView.SelectedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in pathsToRefresh)
        {
            var node = FindFileTreeNodeByPath(path);
            if (node != null)
            {
                ApplyFileTreeSelectionStyle(node);
                WinFormsRendering.InvalidateTreeNodeRow(_allFilesView.FileTree, node);
            }
        }

        _allFilesView.StyledSelectionPaths.Clear();
        foreach (var path in _allFilesView.SelectedPaths)
        {
            _allFilesView.StyledSelectionPaths.Add(path);
        }
    }

    private void ApplyFileTreeSelectionStyle(TreeNode node)
    {
        var selected = IsFileTreeNodeSelected(node);
        if (selected)
        {
            node.BackColor = Color.FromArgb(226, 241, 255);
            node.ForeColor = Color.FromArgb(15, 76, 129);
        }
        else
        {
            node.BackColor = _allFilesView.FileTree.BackColor;
            node.ForeColor = GetFileTreeDefaultForeColor(node);
        }
    }

    private static Color GetFileTreeDefaultForeColor(TreeNode node)
    {
        var status = StatusFromNodeText(node.Text);
        if (status != SvnStatusKind.None)
        {
            return StatusColor(status);
        }

        if (node.Tag is FileTreeNodeInfo { IsFile: false })
        {
            return Color.FromArgb(55, 65, 81);
        }

        return SystemColors.WindowText;
    }

    private static SvnStatusKind StatusFromNodeText(string text)
    {
        if (text.Length < 2 || text[1] != ' ')
        {
            return SvnStatusKind.None;
        }

        return text[0] switch
        {
            'N' => SvnStatusKind.Normal,
            'M' => SvnStatusKind.Modified,
            'A' => SvnStatusKind.Added,
            'D' => SvnStatusKind.Deleted,
            '?' => SvnStatusKind.Unversioned,
            '!' => SvnStatusKind.Missing,
            'C' => SvnStatusKind.Conflicted,
            'R' => SvnStatusKind.Replaced,
            'I' => SvnStatusKind.Ignored,
            _ => SvnStatusKind.None,
        };
    }

    private void PruneFileTreeSelection()
    {
        if (_allFilesView.SelectedPaths.Count == 0)
        {
            return;
        }

        var existingPaths = GetAllFileTreeSelectablePaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allFilesView.SelectedPaths.RemoveWhere(path => !existingPaths.Contains(path));
        if (_allFilesView.SelectionAnchorPath != null && !existingPaths.Contains(_allFilesView.SelectionAnchorPath))
        {
            _allFilesView.SelectionAnchorPath = _allFilesView.SelectedPaths.FirstOrDefault();
        }
    }

    private IEnumerable<string> GetAllFileTreeSelectablePaths()
    {
        foreach (TreeNode node in _allFilesView.FileTree.Nodes)
        {
            foreach (var path in GetAllFileTreeSelectablePaths(node))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> GetAllFileTreeSelectablePaths(TreeNode node)
    {
        var path = GetFileTreeSelectionPath(node);
        if (path != null)
        {
            yield return path;
        }

        foreach (TreeNode child in node.Nodes)
        {
            foreach (var childPath in GetAllFileTreeSelectablePaths(child))
            {
                yield return childPath;
            }
        }
    }

    private TreeNode? FindFileTreeNodeByPath(string relativePath)
    {
        var normalizedPath = NormalizeRelativePath(SvnConflictArtifact.NormalizeToBasePath(relativePath));
        foreach (TreeNode node in _allFilesView.FileTree.Nodes)
        {
            var found = FindFileTreeNodeByPath(node, normalizedPath);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static TreeNode? FindFileTreeNodeByPath(TreeNode node, string normalizedPath)
    {
        var nodePath = GetFileTreeSelectionPath(node);
        if (nodePath != null && string.Equals(nodePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (TreeNode child in node.Nodes)
        {
            var found = FindFileTreeNodeByPath(child, normalizedPath);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void AddFileNode(
        TreeNode rootNode,
        string relativePath,
        FileInfo file,
        IReadOnlyDictionary<string, SvnStatusKind> statusMap)
    {
        if (IsReservedDevicePath(relativePath))
        {
            return;
        }

        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = rootNode;
        var currentPath = "";
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            currentPath = string.IsNullOrEmpty(currentPath) ? part : Path.Combine(currentPath, part);
            var isFile = index == parts.Length - 1;
            var status = ResolveFileTreeStatus(currentPath, statusMap);
            var existing = current.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals(CleanTreeNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                var tooltip = isFile
                    ? BuildFileTooltip(currentPath, file)
                    : currentPath;
                existing = new TreeNode(part)
                {
                    Tag = new FileTreeNodeInfo(currentPath, isFile),
                    ToolTipText = tooltip,
                    ImageKey = isFile ? FileImageKey(currentPath, status) : "folder",
                    SelectedImageKey = isFile ? FileImageKey(currentPath, status) : "folder",
                    ForeColor = isFile ? SystemColors.WindowText : Color.FromArgb(55, 65, 81),
                };
                if (!isFile)
                {
                    existing.NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                }

                current.Nodes.Add(existing);
            }

            if (ShouldDisplayFileTreeStatus(status))
            {
                StyleFileTreeNodeForStatus(existing, status, part, isFile);
            }

            current = existing;
        }
    }

    private static TreeNode CreateFileTreeFolderNode(string relativePath, string name, SvnStatusKind status = SvnStatusKind.None)
    {
        var node = new TreeNode(name)
        {
            Tag = new FileTreeNodeInfo(relativePath, false),
            ToolTipText = relativePath,
            ImageKey = "folder",
            SelectedImageKey = "folder",
            ForeColor = Color.FromArgb(55, 65, 81),
            NodeFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        };
        if (ShouldDisplayFileTreeStatus(status))
        {
            StyleFileTreeNodeForStatus(node, status, name, isFile: false);
        }

        return node;
    }

    private static TreeNode CreateFileTreeFileNode(string relativePath, FileInfo file, SvnStatusKind status)
    {
        var name = Path.GetFileName(relativePath);
        var node = new TreeNode(name)
        {
            Tag = new FileTreeNodeInfo(relativePath, true),
            ToolTipText = BuildFileTooltip(relativePath, file),
            ImageKey = FileImageKey(relativePath, status),
            SelectedImageKey = FileImageKey(relativePath, status),
            ForeColor = SystemColors.WindowText,
        };
        if (ShouldDisplayFileTreeStatus(status))
        {
            StyleFileTreeNodeForStatus(node, status, name, isFile: true);
        }

        return node;
    }

    private static void StyleFileTreeNodeForStatus(TreeNode node, SvnStatusKind status, string displayName, bool isFile)
    {
        node.Text = $"{StatusPrefix(status)} {displayName}";
        node.ForeColor = StatusColor(status);
        if (!node.ToolTipText.Contains("状态：", StringComparison.Ordinal))
        {
            node.ToolTipText += $"\r\n状态：{StatusText(status)}";
        }

        if (status == SvnStatusKind.Ignored)
        {
            node.ImageKey = "ignored";
            node.SelectedImageKey = "ignored";
        }
        else if (status != SvnStatusKind.Normal)
        {
            var imageKey = isFile ? "changed" : "folder";
            node.ImageKey = imageKey;
            node.SelectedImageKey = imageKey;
        }
    }

    private static bool ShouldDisplayFileTreeStatus(SvnStatusKind status)
    {
        return status != SvnStatusKind.None;
    }

    private static TreeNode CreateLazyFileTreePlaceholder()
    {
        return new TreeNode("正在加载...")
        {
            Tag = LazyFileTreePlaceholder.Instance,
            ForeColor = Color.FromArgb(100, 116, 139),
        };
    }

    private static string BuildFileTooltip(string relativePath, FileInfo file)
    {
        try
        {
            return $"{relativePath}\r\n修改时间：{file.LastWriteTime:yyyy-MM-dd HH:mm}\r\n大小：{FormatBytes(file.Length)}";
        }
        catch (IOException)
        {
            return relativePath;
        }
        catch (UnauthorizedAccessException)
        {
            return relativePath;
        }
    }

    private static bool IsReservedDevicePath(string relativePath)
    {
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(IsReservedDeviceName);
    }

    private static bool IsReservedDeviceName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name).TrimEnd(' ');
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Length == 4 &&
            (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            baseName[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static string FileImageKey(string path, SvnStatusKind status)
    {
        if (status == SvnStatusKind.Ignored)
        {
            return "ignored";
        }

        if (status != SvnStatusKind.None && status != SvnStatusKind.Normal)
        {
            return "changed";
        }

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

    private static string CleanTreeNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "NMAD?!CRI".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.##} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    }

    private Dictionary<string, SvnStatusKind> GetStatusMapForTree(string workingCopyPath)
    {
        try
        {
            var result = new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in _svn.GetStatus(workingCopyPath, includeIgnored: true, includeNormal: true))
            {
                result[NormalizeRelativePath(change.RelativePath)] = change.Status;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, SvnStatusKind>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static SvnStatusKind ResolveFileTreeStatus(string relativePath, IReadOnlyDictionary<string, SvnStatusKind> statusMap)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (statusMap.TryGetValue(normalized, out var directStatus))
        {
            return directStatus;
        }

        var parent = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrWhiteSpace(parent))
        {
            if (statusMap.TryGetValue(parent, out var parentStatus) &&
                parentStatus is SvnStatusKind.Ignored or SvnStatusKind.Unversioned)
            {
                return parentStatus;
            }

            parent = Path.GetDirectoryName(parent);
        }

        return SvnStatusKind.None;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string StatusPrefix(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Normal => "N",
            SvnStatusKind.Modified => "M",
            SvnStatusKind.Added => "A",
            SvnStatusKind.Deleted => "D",
            SvnStatusKind.Unversioned => "?",
            SvnStatusKind.Missing => "!",
            SvnStatusKind.Conflicted => "C",
            SvnStatusKind.Replaced => "R",
            SvnStatusKind.Ignored => "I",
            _ => "",
        };
    }

    private static string StatusText(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Normal => "正常",
            SvnStatusKind.Modified => "已修改",
            SvnStatusKind.Added => "已新增",
            SvnStatusKind.Deleted => "已删除",
            SvnStatusKind.Unversioned => "未加入版本控制",
            SvnStatusKind.Missing => "本地缺失",
            SvnStatusKind.Conflicted => "冲突",
            SvnStatusKind.Replaced => "已替换",
            SvnStatusKind.Ignored => "已忽略",
            _ => "",
        };
    }

    private static Color StatusColor(SvnStatusKind status)
    {
        return status switch
        {
            SvnStatusKind.Normal => Color.FromArgb(71, 85, 105),
            SvnStatusKind.Modified => Color.FromArgb(166, 103, 34),
            SvnStatusKind.Added => Color.FromArgb(38, 128, 72),
            SvnStatusKind.Deleted => Color.FromArgb(170, 67, 67),
            SvnStatusKind.Unversioned => Color.FromArgb(93, 88, 161),
            SvnStatusKind.Missing => Color.FromArgb(170, 67, 67),
            SvnStatusKind.Conflicted => Color.FromArgb(190, 50, 50),
            SvnStatusKind.Replaced => Color.FromArgb(128, 79, 160),
            SvnStatusKind.Ignored => Color.FromArgb(100, 116, 139),
            _ => SystemColors.WindowText,
        };
    }

    private string? GetSelectedRelativePath()
    {
        var selectedChange = GetSelectedChange();
        if (selectedChange != null)
        {
            return SvnConflictArtifact.NormalizeToBasePath(selectedChange.RelativePath);
        }

        var selectedConflictPath = _conflictView.GetSelectedConflictPath();
        if (!string.IsNullOrWhiteSpace(selectedConflictPath))
        {
            return selectedConflictPath;
        }

        if (_allFilesView.SelectedPaths.Count == 1)
        {
            var selectedPath = _allFilesView.SelectedPaths.First();
            if (FindFileTreeNodeByPath(selectedPath)?.Tag is FileTreeNodeInfo { IsFile: true })
            {
                return SvnConflictArtifact.NormalizeToBasePath(selectedPath);
            }
        }

        if (_allFilesView.FileTree.SelectedNode?.Tag is FileTreeNodeInfo { IsFile: true } fileNode && !string.IsNullOrWhiteSpace(fileNode.RelativePath))
        {
            return SvnConflictArtifact.NormalizeToBasePath(fileNode.RelativePath);
        }

        return null;
    }

    private IReadOnlyList<string> GetSelectedFileTreeHistoryPaths()
    {
        if (_allFilesView.SelectedPaths.Count > 0)
        {
            return RemoveNestedPaths(_allFilesView.SelectedPaths)
                .Select(SvnConflictArtifact.NormalizeToBasePath)
                .ToList();
        }

        if (_allFilesView.FileTree.SelectedNode?.Tag is FileTreeNodeInfo nodeInfo && !string.IsNullOrWhiteSpace(nodeInfo.RelativePath))
        {
            return [SvnConflictArtifact.NormalizeToBasePath(nodeInfo.RelativePath)];
        }

        var selectedChange = GetSelectedChange();
        if (selectedChange != null)
        {
            return [SvnConflictArtifact.NormalizeToBasePath(selectedChange.RelativePath)];
        }

        var selectedConflictPath = _conflictView.GetSelectedConflictPath();
        if (!string.IsNullOrWhiteSpace(selectedConflictPath))
        {
            return [selectedConflictPath];
        }

        return [];
    }

    private static IReadOnlyList<string> RemoveNestedPaths(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<string>();
        foreach (var path in normalized)
        {
            if (result.Any(parent => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    private SvnChange? GetSelectedChange()
    {
        return _fileStatusView.GetSelectedChange();
    }

    private List<SvnChange> GetSelectedStatusChanges()
    {
        return _fileStatusView.GetSelectedChanges();
    }

    private void OpenSelectedStatusFile()
    {
        var change = GetSelectedChange();
        if (change == null)
        {
            return;
        }

        var path = Path.Combine(GetWorkingCopyRootPath(), change.RelativePath);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("本地文件不存在。", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenSelectedStatusFileFolder()
    {
        var change = GetSelectedChange();
        if (change == null)
        {
            return;
        }

        var path = Path.Combine(GetWorkingCopyRootPath(), change.RelativePath);
        var argument = File.Exists(path)
            ? $"/select,\"{path}\""
            : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(argument))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
    }

    private void AddSelectedFileTreeFavorite()
    {
        if (_allFilesView.FileTree.SelectedNode?.Tag is not FileTreeNodeInfo info)
        {
            MessageBox.Show("请先在全部文件里选中一个目录或文件。", "未选择目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var favoritePath = info.IsFile
            ? NormalizeRelativePath(Path.GetDirectoryName(info.RelativePath) ?? "")
            : NormalizeRelativePath(info.RelativePath);
        if (string.IsNullOrWhiteSpace(favoritePath))
        {
            favoritePath = ".";
        }

        if (!_settings.FavoriteFileTreePaths.Any(path => string.Equals(path, favoritePath, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.FavoriteFileTreePaths.Add(favoritePath);
            _settings.FavoriteFileTreePaths.Sort(StringComparer.CurrentCultureIgnoreCase);
            _settings.Save();
            BuildMoreActionsMenu();
        }

        WriteOutput($"已收藏目录：{favoritePath}");
    }

    private async Task NavigateToFavoriteFileTreePathAsync(string relativePath)
    {
        SelectTab("全部文件");
        if (_allFilesView.FileTree.Nodes.Count == 0 || _allFilesView.FileTree.Nodes[0].Tag is not FileTreeNodeInfo)
        {
            await LoadAllFilesAsync();
        }

        var node = FindOrLoadFileTreeNode(relativePath == "." ? "" : relativePath);
        if (node == null)
        {
            MessageBox.Show($"没有找到收藏目录：{relativePath}", "无法跳转", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        node.EnsureVisible();
        _allFilesView.FileTree.SelectedNode = node;
        SelectFileTreeNode(node, Keys.None);
    }

    private TreeNode? FindOrLoadFileTreeNode(string relativePath)
    {
        if (_allFilesView.FileTree.Nodes.Count == 0)
        {
            return null;
        }

        var current = _allFilesView.FileTree.Nodes[0];
        var pathToFind = NormalizeRelativePath(relativePath == "." ? "" : relativePath);
        if (current.Tag is FileTreeNodeInfo { IsFile: false } rootInfo &&
            !string.IsNullOrWhiteSpace(rootInfo.RelativePath) &&
            IsPathInsideScope(pathToFind, rootInfo.RelativePath))
        {
            var scope = NormalizeRelativePath(rootInfo.RelativePath).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            pathToFind = string.Equals(pathToFind, scope, StringComparison.OrdinalIgnoreCase)
                ? ""
                : pathToFind[(scope.Length + 1)..];
        }

        if (string.IsNullOrWhiteSpace(pathToFind))
        {
            current.Expand();
            return current;
        }

        foreach (var part in pathToFind.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            EnsureLazyFileTreeChildren(current);
            current.Expand();
            var next = current.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(node => string.Equals(CleanTreeNodeText(node.Text), part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private string CurrentWorkingCopyKey()
    {
        return _configView.WorkingCopyPath.Trim();
    }

    private HashSet<string> GetExpandedTreePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode node in _allFilesView.FileTree.Nodes)
        {
            CollectExpandedTreePaths(node, paths);
        }

        return paths;
    }

    private static void CollectExpandedTreePaths(TreeNode node, HashSet<string> paths)
    {
        if (node.IsExpanded && node.Tag is FileTreeNodeInfo { IsFile: false } info)
        {
            paths.Add(info.RelativePath);
        }

        foreach (TreeNode child in node.Nodes)
        {
            CollectExpandedTreePaths(child, paths);
        }
    }

    private void RestoreExpandedTreePaths(HashSet<string> paths)
    {
        if (_allFilesView.FileTree.Nodes.Count == 0)
        {
            return;
        }

        _allFilesView.FileTree.Nodes[0].Expand();
        foreach (TreeNode node in _allFilesView.FileTree.Nodes)
        {
            RestoreExpandedTreePaths(node, paths);
        }
    }

    private static void RestoreExpandedTreePaths(TreeNode node, HashSet<string> paths)
    {
        if (node.Tag is FileTreeNodeInfo { IsFile: false } info && paths.Contains(info.RelativePath))
        {
            node.Expand();
        }

        foreach (TreeNode child in node.Nodes)
        {
            RestoreExpandedTreePaths(child, paths);
        }
    }

    private void SaveTreeExpansionState()
    {
        if (_allFilesView.IsLoadingFileTree || _allFilesView.LastFileCount > MaxFileTreeExpandAllFiles)
        {
            return;
        }

        _treeExpansionSaveTimer.Stop();
        _treeExpansionSaveTimer.Start();
    }

    private void SaveTreeExpansionStateCore()
    {
        if (_allFilesView.IsLoadingFileTree || _allFilesView.LastFileCount > MaxFileTreeExpandAllFiles)
        {
            return;
        }

        _settings.SetExpandedPaths(CurrentWorkingCopyKey(), GetExpandedTreePaths());
        _settings.Save();
    }


    private async Task SelectSidebarRepositoryAsync(TreeNode? node)
    {
        if (_loadingRepository)
        {
            return;
        }

        if (node?.Tag is not RepositoryEntry repository)
        {
            return;
        }

        _settings.CurrentRepositoryId = repository.Id;
        _configView.RepositoryUrl = repository.RepositoryUrl;
        _configView.WorkingCopyPath = repository.WorkingCopyPath;
        _settings.Save();
        _latestRemoteLog = null;
        _remoteStatusLabel.Text = "远端：未检查";
        _remoteStatusLabel.ForeColor = SystemColors.ControlText;
        ClearStatusChanges();
        RefreshConflictPanel([]);
        UpdateStatusBadges(0, 0);
        _historyView.ClearForRepositoryChange(InitialHistoryLimit);
        UpdateHistoryBadge(0);
        LoadAllFiles();
        await LoadCurrentTabAsync();
    }

    private void OpenTreeFile(TreeNode node)
    {
        if (node.Tag is not FileTreeNodeInfo { IsFile: true } fileNode || string.IsNullOrWhiteSpace(fileNode.RelativePath))
        {
            node.Toggle();
            return;
        }

        var filePath = Path.Combine(GetWorkingCopyRootPath(), fileNode.RelativePath);
        if (File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
    }

    private void BuildFileTreeMenu()
    {
        _fileTreeMenu.Items.Clear();
        _fileTreeMenu.Items.Add("打开文件", null, (_, _) => OpenSelectedTreeFile());
        _fileTreeMenu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedTreeFileFolder());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("查看差异", null, async (_, _) => await RunDiffAsync());
        _fileTreeMenu.Items.Add("和另一个表快速比对...", null, async (_, _) => await CompareSelectedTableWithAnotherAsync());
        _fileTreeMenu.Items.Add("当前本地 vs 远端 HEAD", null, async (_, _) => await CompareSelectedFileWithRemoteHeadAsync());
        _fileTreeMenu.Items.Add("查看冲突", null, async (_, _) => await RunConflictViewerAsync());
        _fileTreeMenu.Items.Add("内置三方合并", null, async (_, _) => await RunInternalSpreadsheetMergeAsync());
        _fileTreeMenu.Items.Add("跨库表格三方合并", null, async (_, _) => await RunCrossRepositorySpreadsheetMergeAsync());
        _fileTreeMenu.Items.Add("用分久必合对比/合并", null, async (_, _) => await RunExternalCompareOrMergeAsync());
        _fileTreeMenu.Items.Add("冲突处理流程", null, async (_, _) => await RunConflictWorkflowAsync());
        _fileTreeMenu.Items.Add("文件/文件夹历史", null, async (_, _) => await RunFileHistoryAsync());
        _fileTreeMenu.Items.Add("清除选择", null, (_, _) => ClearFileTreeSelection());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("锁定文件", null, async (_, _) => await LockSelectedFileAsync());
        _fileTreeMenu.Items.Add("解锁文件", null, async (_, _) => await UnlockSelectedFileAsync());
        _fileTreeMenu.Items.Add("查看锁信息", null, async (_, _) => await ShowSelectedFileLockInfoAsync());
        _fileTreeMenu.Items.Add(new ToolStripSeparator());
        _fileTreeMenu.Items.Add("加入版本控制", null, async (_, _) => await AddSelectedTreeFileAsync());
        _fileTreeMenu.Items.Add("加入忽略清单", null, async (_, _) => await AddSelectedPathsToIgnoreAsync());
        _fileTreeMenu.Items.Add("移出忽略清单", null, async (_, _) => await RemoveSelectedPathsFromIgnoreAsync());
        _fileTreeMenu.Items.Add("收藏此目录", null, (_, _) => AddSelectedFileTreeFavorite());
        _fileTreeMenu.Items.Add("标记冲突已解决", null, async (_, _) => await ResolveSelectedTreeFileAsync());
        _fileTreeMenu.Opening += (_, args) =>
        {
            var relativePath = GetSelectedRelativePath();
            var hasFile = !string.IsNullOrWhiteSpace(relativePath);
            var hasTreePath = GetSelectedFileTreeHistoryPaths().Count > 0;
            foreach (ToolStripItem item in _fileTreeMenu.Items)
            {
                item.Enabled = item is ToolStripSeparator ||
                    item.Text is "打开所在目录" && _allFilesView.FileTree.SelectedNode?.Tag is FileTreeNodeInfo ||
                    item.Text is "文件/文件夹历史" && hasTreePath ||
                    item.Text is "打开文件" && hasFile ||
                    item.Text is "查看差异" && hasFile ||
                    item.Text is "和另一个表快速比对..." && hasFile ||
                    item.Text is "内置三方合并" && hasFile ||
                    item.Text is "跨库表格三方合并" ||
                    item.Text is "查看冲突" && hasFile ||
                    item.Text is "用分久必合对比/合并" && hasFile ||
                    item.Text is "冲突处理流程" && hasFile ||
                    item.Text is "清除选择" && _allFilesView.SelectedPaths.Count > 0 ||
                    item.Text is "锁定文件" && hasFile ||
                    item.Text is "解锁文件" && hasFile ||
                    item.Text is "查看锁信息" && hasFile ||
                    item.Text is "加入版本控制" && hasFile ||
                    item.Text is "加入忽略清单" && hasTreePath ||
                    item.Text is "移出忽略清单" && hasTreePath ||
                    item.Text is "标记冲突已解决" && hasFile;
            }
        };
    }

    private void OpenSelectedTreeFile()
    {
        if (_allFilesView.FileTree.SelectedNode != null)
        {
            OpenTreeFile(_allFilesView.FileTree.SelectedNode);
        }
    }

    private void OpenSelectedTreeFileFolder()
    {
        if (_allFilesView.FileTree.SelectedNode?.Tag is not FileTreeNodeInfo nodeInfo)
        {
            return;
        }

        var path = Path.Combine(GetWorkingCopyRootPath(), nodeInfo.RelativePath);
        var folder = nodeInfo.IsFile ? Path.GetDirectoryName(path) : path;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
    }

    private void ClearFileTreeSelection()
    {
        _allFilesView.SelectedPaths.Clear();
        _allFilesView.SelectionAnchorPath = null;
        ApplyFileTreeSelectionStyles();
    }

    private async Task OpenSelectedHistoryChangedFileAsync()
    {
        if (_historyView.ChangedFilesTree.SelectedNode != null)
        {
            await OpenHistoryChangedFileAsync(_historyView.ChangedFilesTree.SelectedNode);
        }
    }

}

