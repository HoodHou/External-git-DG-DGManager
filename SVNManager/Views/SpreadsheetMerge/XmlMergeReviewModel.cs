using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SVNManager.Views.SpreadsheetMerge;

/// <summary>把一个 <see cref="XmlMergeChange"/> 适配成表格直显里的一格可决议改动。</summary>
internal sealed class XmlMergeRow : INotifyPropertyChanged, IMergeReviewRow
{
    private readonly XmlMergeChange _change;
    private bool _touched;
    private bool _isManual;
    private string? _manualDraft;

    public XmlMergeRow(XmlMergeChange change)
    {
        _change = change;
        _touched = change.Kind != XmlMergeChangeKind.Conflict;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>由模型注入:让 取舍 走 撤销 通道。</summary>
    internal Action<Action>? RunWithUndo { get; set; }

    public SpreadsheetMergeChangeKind Kind => MapKind(_change.Kind);
    public string SheetName => GroupOf(_change);
    public string RecordKey => RecordKeyOf(_change);
    public string RecordTitle => RecordTitleOf(_change);
    public int ColumnOrder => 0;
    public string ColumnHeader => _change.DisplayName;
    public string BaseValue => _change.BaseValue;
    public string LocalValue => _change.LocalValue;
    public string RemoteValue => _change.RemoteValue;
    public string EffectiveValue => IsRemoteOperation ? RemoteValue : LocalValue;
    public bool IsConflict => _change.Kind == XmlMergeChangeKind.Conflict;
    public bool IsRowLevel => _change.ActionKind is XmlMergeActionKind.AddElement or XmlMergeActionKind.DeleteElement;
    public bool IsHighRisk => IsConflict;
    public bool SupportsManual => _change.ActionKind is XmlMergeActionKind.SetText or XmlMergeActionKind.SetAttribute;
    public string RowLevelKindText => _change.ActionKind switch
    {
        XmlMergeActionKind.AddElement => "新增节点",
        XmlMergeActionKind.DeleteElement => "删除节点",
        _ => "",
    };

    private bool IsRemoteOperation => _change.Resolution == XmlMergeResolution.UseRemote;

    public MergeDecision Decision
    {
        get
        {
            if (IsConflict && !_touched)
            {
                return MergeDecision.Pending;
            }

            if (!IsRemoteOperation)
            {
                return MergeDecision.KeepLocal;
            }

            return _isManual ? MergeDecision.Manual : MergeDecision.TakeRemote;
        }
    }

    public string ManualValue
    {
        get => _manualDraft ?? RemoteValue;
        set
        {
            if (_manualDraft == value)
            {
                return;
            }

            _manualDraft = value;
            OnPropertyChanged();
        }
    }

    public void TakeLocal() => Run(() => SetCore(XmlMergeResolution.KeepTarget, manual: false, manualValue: null));

    public void TakeRemote() => Run(() => SetCore(XmlMergeResolution.UseRemote, manual: false, manualValue: null));

    public void ApplyManual(string value)
    {
        if (!SupportsManual)
        {
            return;
        }

        Run(() => SetCore(XmlMergeResolution.UseRemote, manual: true, manualValue: value));
    }

    /// <summary>纯变更(不经撤销),供批量使用。</summary>
    internal void SetCore(XmlMergeResolution resolution, bool manual, string? manualValue)
    {
        if (manual && SupportsManual)
        {
            _change.RemoteValue = manualValue ?? "";
            _manualDraft = manualValue ?? "";
        }

        _isManual = manual;
        _change.Resolution = resolution;
        _touched = true;
        RaiseAll();
    }

    internal XmlMergeRowSnapshot Capture() => new(this, _change.Resolution, _change.RemoteValue, _touched, _isManual);

    internal void Restore(XmlMergeRowSnapshot snapshot)
    {
        _change.Resolution = snapshot.Resolution;
        _change.RemoteValue = snapshot.RemoteValue;
        _touched = snapshot.Touched;
        _isManual = snapshot.IsManual;
        _manualDraft = snapshot.IsManual ? snapshot.RemoteValue : null;
        RaiseAll();
    }

    private void Run(Action action)
    {
        if (RunWithUndo != null)
        {
            RunWithUndo(action);
        }
        else
        {
            action();
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Decision));
        OnPropertyChanged(nameof(EffectiveValue));
        OnPropertyChanged(nameof(RemoteValue));
        OnPropertyChanged(nameof(ManualValue));
    }

    internal static SpreadsheetMergeChangeKind MapKind(XmlMergeChangeKind kind) => kind switch
    {
        XmlMergeChangeKind.AutoRemote => SpreadsheetMergeChangeKind.AutoRemote,
        XmlMergeChangeKind.LocalOnly => SpreadsheetMergeChangeKind.LocalOnly,
        XmlMergeChangeKind.SameBoth => SpreadsheetMergeChangeKind.SameBoth,
        _ => SpreadsheetMergeChangeKind.Conflict,
    };

    /// <summary>改动所属元素路径(去掉结尾 /@attr 或 /text())。</summary>
    internal static string ElementPathOf(XmlMergeChange change)
        => change.ActionKind is XmlMergeActionKind.AddElement or XmlMergeActionKind.DeleteElement
            ? change.Path
            : string.IsNullOrEmpty(change.ParentPath) ? change.Path : change.ParentPath;

    internal static string RecordKeyOf(XmlMergeChange change) => ElementPathOf(change);

    internal static string RecordTitleOf(XmlMergeChange change)
    {
        var path = ElementPathOf(change);
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < path.Length - 1 ? path[(lastSlash + 1)..] : path;
    }

    /// <summary>分组键 = 根的直接子元素名(去掉谓词),作为一个 Tab。</summary>
    internal static string GroupOf(XmlMergeChange change)
    {
        var path = ElementPathOf(change);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var target = segments.Length >= 2
            ? segments[1]
            : segments.Length == 1 ? segments[0] : "(根)";
        var bracket = target.IndexOf('[');
        return bracket > 0 ? target[..bracket] : target;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record XmlMergeRowSnapshot(XmlMergeRow Row, XmlMergeResolution Resolution, string RemoteValue, bool Touched, bool IsManual);

/// <summary>XML 三方合并的表格直显模型(走同一个 <c>MergeReviewWindow</c>)。</summary>
internal sealed class XmlMergeReviewModel : INotifyPropertyChanged, IMergeReviewModel
{
    private const string FilterAll = "All";
    private const string FilterAutoRemote = "AutoRemote";
    private const string FilterConflict = "Conflict";
    private const string FilterRisk = "Risk";

    private readonly List<XmlMergeRow> _rows;
    private readonly Stack<IReadOnlyList<XmlMergeRowSnapshot>> _undoStack = new();
    private readonly Dictionary<IMergeReviewRow, MergeCellViewModel> _cellByRow = new();
    private string _activeFilter = FilterAll;
    private string _searchText = "";
    private XmlMergeRow? _currentConflict;

    public XmlMergeReviewModel(
        string relativePath,
        XmlMergePlan plan,
        string titlePrefix = "内置 XML 三方合并",
        string applyButtonText = "写入工作副本")
    {
        RelativePath = relativePath;
        Plan = plan;
        TitlePrefix = titlePrefix;
        ApplyButtonText = applyButtonText;
        _rows = plan.AutoRemoteChanges.Concat(plan.Conflicts).Select(change => new XmlMergeRow(change)).ToList();
        foreach (var row in _rows)
        {
            var captured = row;
            row.RunWithUndo = action => RunWithUndo(captured, action);
        }

        SetFilterCommand = new RelayCommand(parameter => SetFilter(parameter?.ToString() ?? FilterAll));
        NextConflictCommand = new RelayCommand(_ => MoveConflict(1), _ => _rows.Any(row => row.IsConflict));
        PrevConflictCommand = new RelayCommand(_ => MoveConflict(-1), _ => _rows.Any(row => row.IsConflict));
        AllConflictsLocalCommand = new RelayCommand(_ => SetAllConflicts(useRemote: false), _ => Plan.Conflicts.Count > 0);
        AllConflictsRemoteCommand = new RelayCommand(_ => SetAllConflicts(useRemote: true), _ => Plan.Conflicts.Count > 0);
        AllLocalCommand = new RelayCommand(_ => SetAll(useRemote: false), _ => _rows.Count > 0);
        AllRemoteCommand = new RelayCommand(_ => SetAll(useRemote: true), _ => _rows.Count > 0);
        UndoLastDecisionCommand = new RelayCommand(_ => Undo(), _ => _undoStack.Count > 0);
        WriteCommand = new RelayCommand(_ => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
        ShowHelpCommand = new RelayCommand(_ => RequestMessage?.Invoke(
            "快捷键",
            "N：下一未决冲突\nShift+N：上一冲突\nCtrl+Z：撤销上一步\nCtrl+Enter：写入\nEsc：取消"));

        BuildSheets();
        RefreshSheetFilters();
        RefreshDerived();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool?>? RequestClose;
    public event Action<string, string>? RequestMessage;
    public event Func<string, string, bool>? RequestConfirmation;
    public event Action<MergeCellViewModel>? RequestRevealCell;

    public XmlMergePlan Plan { get; }
    public string TitlePrefix { get; }
    public string RelativePath { get; }
    public string ApplyButtonText { get; }
    public string WindowTitle => $"{TitlePrefix} - {RelativePath}";
    public string TargetLabel => "本地";
    public string SourceLabel => "远端 HEAD";
    public ObservableCollection<MergeSheetViewModel> Sheets { get; } = [];

    public ICommand SetFilterCommand { get; }
    public ICommand NextConflictCommand { get; }
    public ICommand PrevConflictCommand { get; }
    public ICommand AllConflictsLocalCommand { get; }
    public ICommand AllConflictsRemoteCommand { get; }
    public ICommand AllLocalCommand { get; }
    public ICommand AllRemoteCommand { get; }
    public ICommand UndoLastDecisionCommand { get; }
    public ICommand WriteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ShowHelpCommand { get; }
    public ICommand? ClassicViewCommand => null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value ?? "";
            OnPropertyChanged();
            RefreshSheetFilters();
        }
    }

    public int TotalCount => _rows.Count;
    public int DecidedCount => _rows.Count(row => row.Decision != MergeDecision.Pending);
    public int RemainingConflictCount => _rows.Count(row => row.Decision == MergeDecision.Pending);
    public int RiskCount => _rows.Count(row => row.IsHighRisk);
    public int RemoteSelectionCount => _rows.Count(row => row.Decision is MergeDecision.TakeRemote or MergeDecision.Manual);
    public double ProgressMaximum => Math.Max(1, TotalCount);
    public string ProgressText => $"已决策 {DecidedCount} / {TotalCount}，剩余 {RemainingConflictCount} 个冲突";
    public string SummaryText => $"自动应用 {Plan.AutoRemoteChanges.Count} 项、冲突 {Plan.Conflicts.Count} 项；当前将写入远端 {RemoteSelectionCount} 项。";
    public string AutoFilterText => $"自动 {Plan.AutoRemoteChanges.Count}";
    public string ConflictFilterText => $"冲突 {Plan.Conflicts.Count}";
    public string RiskFilterText => $"风险 {RiskCount}";

    private void BuildSheets()
    {
        Sheets.Clear();
        _cellByRow.Clear();
        var contextChanges = Plan.LocalOnlyChanges.Concat(Plan.SameBothChanges).ToList();

        var groups = _rows.Select(row => row.SheetName)
            .Concat(contextChanges.Select(XmlMergeRow.GroupOf))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupRows = _rows.Where(row => string.Equals(row.SheetName, group, StringComparison.Ordinal)).ToList();
            var groupContext = contextChanges.Where(change => string.Equals(XmlMergeRow.GroupOf(change), group, StringComparison.Ordinal)).ToList();

            var headers = new List<string>();
            void AddHeader(string header)
            {
                if (!string.IsNullOrEmpty(header) && !headers.Contains(header))
                {
                    headers.Add(header);
                }
            }

            foreach (var row in groupRows.Where(row => !row.IsRowLevel))
            {
                AddHeader(row.ColumnHeader);
            }

            foreach (var change in groupContext)
            {
                AddHeader(change.DisplayName);
            }

            var columns = new List<MergeColumn>();
            var sequence = 0;
            foreach (var header in headers)
            {
                columns.Add(new MergeColumn($"c{sequence}", header, sequence));
                sequence++;
            }

            var recordKeys = groupRows.Select(row => row.RecordKey)
                .Concat(groupContext.Select(XmlMergeRow.RecordKeyOf))
                .Distinct(StringComparer.Ordinal);

            var records = new List<MergeRecordViewModel>();
            foreach (var recordKey in recordKeys)
            {
                var recordRows = groupRows.Where(row => string.Equals(row.RecordKey, recordKey, StringComparison.Ordinal)).ToList();
                var recordContext = groupContext.Where(change => string.Equals(XmlMergeRow.RecordKeyOf(change), recordKey, StringComparison.Ordinal)).ToList();
                var elementRow = recordRows.FirstOrDefault(row => row.IsRowLevel);

                var cellsByKey = new Dictionary<string, MergeCellViewModel>(StringComparer.Ordinal);
                var cellsInOrder = new List<MergeCellViewModel>();
                foreach (var column in columns)
                {
                    MergeCellViewModel cell;
                    var interactive = recordRows.FirstOrDefault(row => !row.IsRowLevel && string.Equals(row.ColumnHeader, column.Header, StringComparison.Ordinal));
                    if (interactive != null)
                    {
                        cell = new MergeCellViewModel(interactive);
                        _cellByRow[interactive] = cell;
                    }
                    else
                    {
                        var contextField = recordContext.FirstOrDefault(change => string.Equals(change.DisplayName, column.Header, StringComparison.Ordinal));
                        cell = contextField != null
                            ? new MergeCellViewModel(contextField.LocalValue, XmlMergeRow.MapKind(contextField.Kind))
                            : MergeCellViewModel.Blank;
                    }

                    cellsByKey[column.Key] = cell;
                    cellsInOrder.Add(cell);
                }

                var isRowLevel = elementRow != null;
                var title = isRowLevel
                    ? $"{elementRow!.RowLevelKindText} {ShortPath(recordKey)}"
                    : ShortPath(recordKey);
                var kind = recordRows.Any(row => row.IsConflict)
                    ? SpreadsheetMergeChangeKind.Conflict
                    : recordRows.Any(row => row.Kind == SpreadsheetMergeChangeKind.AutoRemote)
                        ? SpreadsheetMergeChangeKind.AutoRemote
                        : recordContext.Count > 0 ? XmlMergeRow.MapKind(recordContext[0].Kind) : SpreadsheetMergeChangeKind.AutoRemote;

                records.Add(new MergeRecordViewModel(
                    recordKey,
                    title,
                    kind,
                    isRowLevel,
                    isRowLevel ? elementRow!.RowLevelKindText : "",
                    cellsInOrder,
                    cellsByKey,
                    isRowLevel ? elementRow : null));
            }

            Sheets.Add(new MergeSheetViewModel(group, columns, records));
        }
    }

    private static string ShortPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < path.Length - 1 ? path[(lastSlash + 1)..] : path;
    }

    private void MoveConflict(int delta)
    {
        var conflicts = _rows.Where(row => row.IsConflict && row.Decision == MergeDecision.Pending).ToList();
        if (conflicts.Count == 0)
        {
            conflicts = _rows.Where(row => row.IsConflict).ToList();
        }

        if (conflicts.Count == 0)
        {
            return;
        }

        var index = _currentConflict == null ? -1 : conflicts.IndexOf(_currentConflict);
        var next = index < 0
            ? (delta > 0 ? 0 : conflicts.Count - 1)
            : (index + delta + conflicts.Count) % conflicts.Count;
        _currentConflict = conflicts[next];
        if (_cellByRow.TryGetValue(_currentConflict, out var cell))
        {
            RequestRevealCell?.Invoke(cell);
        }
    }

    private void SetAll(bool useRemote)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var target = useRemote ? SourceLabel : TargetLabel;
        if (RequestConfirmation?.Invoke("批量处理", $"确认把全部 {_rows.Count} 项改为“使用{target}”？") == false)
        {
            return;
        }

        BatchWithUndo(_rows, () =>
        {
            foreach (var row in _rows)
            {
                row.SetCore(useRemote ? XmlMergeResolution.UseRemote : XmlMergeResolution.KeepTarget, manual: false, manualValue: null);
            }
        });
    }

    private void SetAllConflicts(bool useRemote)
    {
        var conflicts = _rows.Where(row => row.IsConflict).ToList();
        if (conflicts.Count == 0)
        {
            return;
        }

        var target = useRemote ? SourceLabel : TargetLabel;
        if (RequestConfirmation?.Invoke("批量处理冲突", $"确认把全部 {conflicts.Count} 个冲突改为“使用{target}”？") == false)
        {
            return;
        }

        BatchWithUndo(conflicts, () =>
        {
            foreach (var row in conflicts)
            {
                row.SetCore(useRemote ? XmlMergeResolution.UseRemote : XmlMergeResolution.KeepTarget, manual: false, manualValue: null);
            }
        });
    }

    private void RunWithUndo(XmlMergeRow row, Action action)
    {
        var snapshot = row.Capture();
        action();
        _undoStack.Push([snapshot]);
        RefreshDerived();
    }

    private void BatchWithUndo(IReadOnlyList<XmlMergeRow> rows, Action change)
    {
        var snapshots = rows.Select(row => row.Capture()).ToList();
        change();
        _undoStack.Push(snapshots);
        RefreshDerived();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        foreach (var snapshot in _undoStack.Pop())
        {
            snapshot.Row.Restore(snapshot);
        }

        RefreshDerived();
    }

    private void SetFilter(string filter)
    {
        _activeFilter = _activeFilter == filter ? FilterAll : filter;
        RefreshSheetFilters();
    }

    private void RefreshSheetFilters()
    {
        foreach (var sheet in Sheets)
        {
            sheet.ApplyFilter(RecordMatches);
        }
    }

    private bool RecordMatches(MergeRecordViewModel record)
    {
        var filterMatch = _activeFilter switch
        {
            FilterAutoRemote => record.HasAuto,
            FilterConflict => record.HasConflict,
            FilterRisk => record.HasRisk,
            _ => true,
        };
        if (!filterMatch)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText) || record.MatchesSearch(SearchText.Trim());
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(DecidedCount));
        OnPropertyChanged(nameof(RemainingConflictCount));
        OnPropertyChanged(nameof(RiskCount));
        OnPropertyChanged(nameof(RemoteSelectionCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(RiskFilterText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
