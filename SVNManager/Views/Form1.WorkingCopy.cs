namespace SVNManager;

public partial class Form1
{
    private WorkingCopyContext? TryGetWorkingCopyContext(bool showMessage = false, bool allowMissing = false)
    {
        var selectedPath = _configView.WorkingCopyPath.Trim();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            if (showMessage)
            {
                MessageBox.Show("请先选择本地目录。", "缺少本地目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return null;
        }

        if (!Directory.Exists(selectedPath))
        {
            if (allowMissing)
            {
                return new WorkingCopyContext(selectedPath, selectedPath, "", WorkingCopyInfo.Empty);
            }

            if (showMessage)
            {
                MessageBox.Show("本地目录不存在。", "目录错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return null;
        }

        try
        {
            var fullSelectedPath = Path.GetFullPath(selectedPath);
            if (!allowMissing &&
                _workingCopyContextCache != null &&
                string.Equals(_workingCopyContextCachePath, fullSelectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return _workingCopyContextCache;
            }

            var info = _svn.GetWorkingCopyInfo(selectedPath);
            if (info == WorkingCopyInfo.Empty)
            {
                if (allowMissing)
                {
                    return new WorkingCopyContext(selectedPath, selectedPath, "", WorkingCopyInfo.Empty);
                }

                if (showMessage)
                {
                    MessageBox.Show("选择的目录不是 SVN 工作副本的一部分。请选择 SVN 工作副本根目录或其子目录。", "不是 SVN 工作副本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return null;
            }

            var rootPath = ResolveWorkingCopyRootPath(selectedPath, info);
            var rootInfo = PathsEqual(rootPath, selectedPath)
                ? info
                : _svn.GetWorkingCopyInfo(rootPath);
            var scopeRelativePath = GetRelativePathOrEmpty(rootPath, selectedPath);
            var context = new WorkingCopyContext(
                Path.GetFullPath(selectedPath),
                rootPath,
                NormalizeRelativePath(scopeRelativePath),
                rootInfo with { WorkingCopyRootPath = rootPath });
            if (!allowMissing)
            {
                _workingCopyContextCache = context;
                _workingCopyContextCachePath = fullSelectedPath;
            }

            return context;
        }
        catch (Exception ex)
        {
            if (showMessage)
            {
                MessageBox.Show("SVN 工作副本信息读取失败：" + ex.Message, "工作副本错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return null;
        }
    }

    private bool ValidateWorkingCopyPath(bool allowMissing = false)
    {
        return TryGetWorkingCopyContext(showMessage: true, allowMissing: allowMissing) != null;
    }

    private bool ValidateWorkingCopyPathForBackground()
    {
        return TryGetWorkingCopyContext() != null;
    }

    private string GetWorkingCopyRootPath()
    {
        return TryGetWorkingCopyContext()?.RootPath ?? _configView.WorkingCopyPath.Trim();
    }

    private WorkingCopyContext GetRequiredWorkingCopyContext()
    {
        return TryGetWorkingCopyContext(showMessage: true) ??
            throw new InvalidOperationException("没有可用的 SVN 工作副本。");
    }

    private IReadOnlyList<SvnChange> FilterChangesForCurrentScope(IEnumerable<SvnChange> changes)
    {
        var context = TryGetWorkingCopyContext();
        if (context == null || !context.IsScopedToSubdirectory)
        {
            return changes.ToList();
        }

        return changes
            .Where(change => IsPathInsideScope(change.RelativePath, context.ScopeRelativePath))
            .ToList();
    }

    private static bool IsPathInsideScope(string relativePath, string scopeRelativePath)
    {
        var path = NormalizeRelativePath(relativePath).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scope = NormalizeRelativePath(scopeRelativePath).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(scope) ||
            string.Equals(path, scope, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWorkingCopyRootPath(string selectedPath, WorkingCopyInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.WorkingCopyRootPath) && Directory.Exists(info.WorkingCopyRootPath))
        {
            return Path.GetFullPath(info.WorkingCopyRootPath);
        }

        var current = new DirectoryInfo(selectedPath);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".svn")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(selectedPath);
    }

    private static string GetRelativePathOrEmpty(string rootPath, string path)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        return string.Equals(relativePath, ".", StringComparison.Ordinal) ? "" : relativePath;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
