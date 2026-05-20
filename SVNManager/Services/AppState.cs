namespace SVNManager;

internal sealed class AppState
{
    public WorkingCopyInfo WorkingCopyInfo { get; private set; } = WorkingCopyInfo.Empty;
    public SvnLogEntry? LatestRemoteLog { get; private set; }
    public IReadOnlyList<SvnChange> StatusChanges { get; private set; } = [];
    public IReadOnlyList<SvnLogEntry> HistoryRows { get; private set; } = [];
    public SvnLogEntry? SelectedHistoryLog { get; private set; }
    public IReadOnlyList<SvnLogEntry> SelectedHistoryLogs { get; private set; } = [];

    public event EventHandler<AppStateChangedEventArgs>? Changed;

    public void SetWorkingCopyInfo(WorkingCopyInfo info)
    {
        WorkingCopyInfo = info;
        OnChanged(AppStateChangeKind.WorkingCopy);
    }

    public void SetLatestRemoteLog(SvnLogEntry? log)
    {
        LatestRemoteLog = log;
        OnChanged(AppStateChangeKind.RemoteHistory);
    }

    public void SetStatusChanges(IReadOnlyList<SvnChange> changes)
    {
        StatusChanges = changes.ToList();
        OnChanged(AppStateChangeKind.Status);
    }

    public void SetHistoryRows(IReadOnlyList<SvnLogEntry> rows)
    {
        HistoryRows = rows.ToList();
        OnChanged(AppStateChangeKind.History);
    }

    public void SetSelectedHistory(IReadOnlyList<SvnLogEntry> logs)
    {
        SelectedHistoryLogs = logs.ToList();
        SelectedHistoryLog = SelectedHistoryLogs.Count == 1 ? SelectedHistoryLogs[0] : null;
        OnChanged(AppStateChangeKind.HistorySelection);
    }

    private void OnChanged(AppStateChangeKind kind)
    {
        Changed?.Invoke(this, new AppStateChangedEventArgs(kind));
    }
}

internal sealed record AppStateChangedEventArgs(AppStateChangeKind Kind);

internal enum AppStateChangeKind
{
    WorkingCopy,
    RemoteHistory,
    Status,
    History,
    HistorySelection,
}
