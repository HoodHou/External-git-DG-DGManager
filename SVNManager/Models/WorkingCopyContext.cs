namespace SVNManager;

internal sealed record WorkingCopyContext(
    string SelectedPath,
    string RootPath,
    string ScopeRelativePath,
    WorkingCopyInfo Info)
{
    public bool IsScopedToSubdirectory =>
        !string.IsNullOrWhiteSpace(ScopeRelativePath) &&
        !string.Equals(ScopeRelativePath, ".", StringComparison.Ordinal);
}
