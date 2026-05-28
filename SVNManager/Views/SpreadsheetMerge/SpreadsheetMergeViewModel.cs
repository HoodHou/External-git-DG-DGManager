using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SVNManager.Views.SpreadsheetMerge;

internal sealed class SpreadsheetMergeViewModel : INotifyPropertyChanged
{
    private readonly MergeOperationLabels _labels;
    private readonly Stack<IReadOnlyList<MergeRowSnapshot>> _undoStack = new();
    private MergeRowViewModel? _selectedRow;
    private string _activeFilter = FilterAll;
    private string _searchText = "";
    private bool _syncingRows;
    private bool _restoring;

    private const string FilterAll = "All";
    private const string FilterAutoRemote = "AutoRemote";
    private const string FilterLocalOnly = "LocalOnly";
    private const string FilterSameBoth = "SameBoth";
    private const string FilterConflict = "Conflict";
    private const string FilterRisk = "Risk";

    public SpreadsheetMergeViewModel(
        string relativePath,
        SpreadsheetMergePlan plan,
        string titlePrefix = "内置表格三方合并",
        string targetLabel = "本地",
        string sourceLabel = "远端 HEAD",
        string applyButtonText = "写入工作副本")
    {
        RelativePath = relativePath;
        Plan = plan;
        TitlePrefix = titlePrefix;
        ApplyButtonText = applyButtonText;
        _labels = new MergeOperationLabels(targetLabel, sourceLabel);
        Rows = new ObservableCollection<MergeRowViewModel>(
            plan.MergeWorkChanges.Select(change => new MergeRowViewModel(change, _labels)));
        foreach (var row in Rows)
        {
            row.Edited += OnRowEdited;
        }

        Groups =
        [
            new MergeRowGroupViewModel("冲突", SpreadsheetMergeChangeKind.Conflict, isExpanded: true),
            new MergeRowGroupViewModel("自动应用", SpreadsheetMergeChangeKind.AutoRemote, isExpanded: false),
        ];

        SelectRowCommand = new RelayCommand(parameter => SelectRow(parameter as MergeRowViewModel));
        SetFilterCommand = new RelayCommand(parameter => SetFilter(parameter?.ToString() ?? FilterAll));
        MoveNextCommand = new RelayCommand(_ => MoveSelection(1), _ => Rows.Count > 0);
        MovePrevCommand = new RelayCommand(_ => MoveSelection(-1), _ => Rows.Count > 0);
        NextConflictCommand = new RelayCommand(_ => MoveConflict(1), _ => Rows.Any(row => row.Kind == SpreadsheetMergeChangeKind.Conflict));
        PrevConflictCommand = new RelayCommand(_ => MoveConflict(-1), _ => Rows.Any(row => row.Kind == SpreadsheetMergeChangeKind.Conflict));
        KeepLocalCommand = new RelayCommand(_ => KeepLocal(), _ => SelectedRow != null);
        UseRemoteCommand = new RelayCommand(_ => UseRemote(), _ => SelectedRow != null);
        ToggleDecisionCommand = new RelayCommand(_ => ToggleDecision(), _ => SelectedRow != null);
        AdoptSideCommand = new RelayCommand(AdoptSide);
        UndoLastDecisionCommand = new RelayCommand(_ => UndoLastDecision(), _ => _undoStack.Count > 0);
        AllLocalCommand = new RelayCommand(_ => SetAllLocal(), _ => Rows.Count > 0);
        AllRemoteCommand = new RelayCommand(_ => SetAllRemote(), _ => Rows.Count > 0);
        WriteCommand = new RelayCommand(_ => Write());
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
        ShowHelpCommand = new RelayCommand(_ => ShowKeyboardHelp());
        ClassicViewCommand = new RelayCommand(_ => RequestClassicView?.Invoke());

        RefreshGroups();
        SelectRow(Rows.FirstOrDefault(row => row.Kind == SpreadsheetMergeChangeKind.Conflict) ?? Rows.FirstOrDefault());
        RefreshDerivedState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool?>? RequestClose;
    public event Action<string, string>? RequestMessage;
    public event Func<string, string, bool>? RequestConfirmation;
    public event Action? RequestClassicView;

    public SpreadsheetMergePlan Plan { get; }
    public string RelativePath { get; }
    public string TitlePrefix { get; }
    public string WindowTitle => $"{TitlePrefix} - {RelativePath}";
    public string TargetLabel => _labels.TargetLabel;
    public string SourceLabel => _labels.SourceLabel;
    public string ApplyButtonText { get; }
    public ObservableCollection<MergeRowViewModel> Rows { get; }
    public ObservableCollection<MergeRowGroupViewModel> Groups { get; }
    public ObservableCollection<MergeRowViewModel> SelectedRelatedRows { get; } = [];
    public IReadOnlyList<string> OperationOptions => _labels.All;

    public ICommand SelectRowCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand MoveNextCommand { get; }
    public ICommand MovePrevCommand { get; }
    public ICommand NextConflictCommand { get; }
    public ICommand PrevConflictCommand { get; }
    public ICommand KeepLocalCommand { get; }
    public ICommand UseRemoteCommand { get; }
    public ICommand ToggleDecisionCommand { get; }
    public ICommand AdoptSideCommand { get; }
    public ICommand UndoLastDecisionCommand { get; }
    public ICommand AllLocalCommand { get; }
    public ICommand AllRemoteCommand { get; }
    public ICommand WriteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ShowHelpCommand { get; }
    public ICommand ClassicViewCommand { get; }

    public MergeRowViewModel? SelectedRow
    {
        get => _selectedRow;
        private set
        {
            if (ReferenceEquals(_selectedRow, value))
            {
                return;
            }

            if (_selectedRow != null)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = value;
            if (_selectedRow != null)
            {
                _selectedRow.IsSelected = true;
                ExpandGroupFor(_selectedRow);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedRow));
            OnPropertyChanged(nameof(SelectedHeaderText));
            OnPropertyChanged(nameof(SelectedOperationText));
            RefreshRelatedRows();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedRow => SelectedRow != null;

    public string? SelectedOperationText
    {
        get => SelectedRow?.OperationText;
        set
        {
            if (SelectedRow == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            ChangeRowOperation(SelectedRow, value);
        }
    }

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
            RefreshGroups();
        }
    }

    public int TotalCount => Rows.Count;
    public int DecidedCount => Rows.Count(row => !row.IsDecisionPending);
    public int RemainingConflictCount => Rows.Count(row => row.IsDecisionPending);
    public int RiskCount => Rows.Count(row => row.IsHighRisk);
    public int PlannedWriteCount => Rows.Sum(row => row.PlannedWriteCellCount);
    public int WriteSelectionCount => Rows.Count(row => row.IsRemoteOperation);
    public double ProgressMaximum => Math.Max(1, TotalCount);
    public string ProgressText => $"已决策 {DecidedCount} / {TotalCount}，剩余 {RemainingConflictCount} 个冲突";
    public string AutoFilterText => $"自动 {Plan.AutoRemoteChanges.Count}";
    public string LocalFilterText => $"{TargetLabel}独有 {Plan.LocalOnlyChanges.Count}";
    public string SameFilterText => $"相同 {Plan.SameBothChanges.Count}";
    public string ConflictFilterText => $"冲突 {Plan.Conflicts.Count}";
    public string RiskFilterText => $"风险 {RiskCount}";
    public string SelectedHeaderText => SelectedRow == null
        ? "未选择合并项目"
        : $"{SelectedRow.LocationText}   字段「{SelectedRow.FieldName}」   ID {SelectedRow.RowId}   {SelectedRow.KindText}";
    public string SummaryText =>
        $"当前选择写入/插入/删除 {WriteSelectionCount} 项、保留 {Rows.Count - WriteSelectionCount} 项；预计生成 {PlannedWriteCount} 个写入动作。";
    public string LinkHintText
    {
        get
        {
            if (SelectedRow == null || string.IsNullOrWhiteSpace(SelectedRow.RowMergeKey) || SelectedRelatedRows.Count <= 1)
            {
                return "当前项目没有可联动的同一行字段。";
            }

            var fields = SelectedRelatedRows
                .Where(row => !ReferenceEquals(row, SelectedRow))
                .Select(row => row.FieldName)
                .Take(6);
            return $"此行另有 {SelectedRelatedRows.Count - 1} 个字段会随行级操作联动：" + string.Join(" / ", fields);
        }
    }

    public bool IsAllFilterActive => _activeFilter == FilterAll;
    public bool IsAutoFilterActive => _activeFilter == FilterAutoRemote;
    public bool IsLocalFilterActive => _activeFilter == FilterLocalOnly;
    public bool IsSameFilterActive => _activeFilter == FilterSameBoth;
    public bool IsConflictFilterActive => _activeFilter == FilterConflict;
    public bool IsRiskFilterActive => _activeFilter == FilterRisk;

    internal bool TryWrite(out string title, out string message)
    {
        title = "";
        message = "";
        foreach (var row in Rows)
        {
            if (!row.TryApplyToChange(out var error))
            {
                title = "写入位置无效";
                message = error;
                return false;
            }
        }

        var duplicateTarget = Plan.AllChanges
            .Where(change => change.Operation is SpreadsheetMergeOperation.WriteCell or SpreadsheetMergeOperation.AppendRow or SpreadsheetMergeOperation.InsertRow)
            .Where(change => !string.Equals(change.LocalValue, change.RemoteValue, StringComparison.Ordinal))
            .GroupBy(change => change.WriteCell)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget != null)
        {
            var target = duplicateTarget.Key;
            var address = $"{target.Sheet}!{ExcelDiffService.ToColumnName(target.Column)}{target.Row + 1}";
            title = "写入位置重复";
            message =
                $"有多个合并项目会写入同一个目标单元格：{address}{Environment.NewLine}{Environment.NewLine}" +
                "请先手动调整其中一项的写入位置，避免覆盖顺序不明确。";
            return false;
        }

        return true;
    }

    private void SelectRow(MergeRowViewModel? row)
    {
        if (row == null)
        {
            SelectedRow = null;
            return;
        }

        if (!Rows.Contains(row))
        {
            return;
        }

        SelectedRow = row;
    }

    private void SetFilter(string filter)
    {
        _activeFilter = _activeFilter == filter ? FilterAll : filter;
        RefreshGroups();
        var firstVisible = GetFilteredRows().FirstOrDefault();
        if (firstVisible != null && (SelectedRow == null || !GetFilteredRows().Contains(SelectedRow)))
        {
            SelectRow(firstVisible);
        }

        RefreshFilterProperties();
    }

    private void MoveSelection(int delta)
    {
        var visible = GetFilteredRows().ToList();
        if (visible.Count == 0)
        {
            return;
        }

        var index = SelectedRow == null ? -1 : visible.IndexOf(SelectedRow);
        var next = Math.Clamp(index + delta, 0, visible.Count - 1);
        if (index < 0)
        {
            next = delta > 0 ? 0 : visible.Count - 1;
        }

        SelectRow(visible[next]);
    }

    private void MoveConflict(int delta)
    {
        var conflicts = Rows
            .Where(row => row.Kind == SpreadsheetMergeChangeKind.Conflict && row.IsDecisionPending)
            .ToList();
        if (conflicts.Count == 0)
        {
            conflicts = Rows.Where(row => row.Kind == SpreadsheetMergeChangeKind.Conflict).ToList();
        }

        if (conflicts.Count == 0)
        {
            return;
        }

        var index = SelectedRow == null ? -1 : conflicts.IndexOf(SelectedRow);
        var next = index < 0
            ? (delta > 0 ? 0 : conflicts.Count - 1)
            : (index + delta + conflicts.Count) % conflicts.Count;
        SelectRow(conflicts[next]);
    }

    private void KeepLocal()
    {
        if (SelectedRow != null)
        {
            ChangeRowOperation(SelectedRow, _labels.KeepTargetText);
        }
    }

    private void UseRemote()
    {
        if (SelectedRow != null)
        {
            UseRemote(SelectedRow);
        }
    }

    private void UseRemote(MergeRowViewModel row)
    {
        var operation = row.SourceCellExists
            ? row.RequiresWholeRowSource ? _labels.AppendRowText : _labels.WriteCellText
            : _labels.DeleteRowText;
        ChangeRowOperation(row, operation);
    }

    private void ToggleDecision()
    {
        if (SelectedRow == null)
        {
            return;
        }

        if (SelectedRow.IsRemoteOperation)
        {
            ChangeRowOperation(SelectedRow, _labels.KeepTargetText);
        }
        else
        {
            UseRemote(SelectedRow);
        }
    }

    private void AdoptSide(object? parameter)
    {
        if (parameter is not MergeSideViewModel side)
        {
            return;
        }

        SelectRow(side.Row);
        if (side.IsTarget)
        {
            ChangeRowOperation(side.Row, _labels.KeepTargetText);
        }
        else if (side.IsSource)
        {
            UseRemote(side.Row);
        }
    }

    private void SetAllLocal()
    {
        if (RequestConfirmation?.Invoke("全部选择目标", $"确认把全部 {Rows.Count} 项改为“{_labels.KeepTargetText}”？") == false)
        {
            return;
        }

        ChangeRowsWithUndo(Rows, () =>
        {
            foreach (var row in Rows)
            {
                row.SetOperationFromOwner(_labels.KeepTargetText, markDecisionTouched: true);
            }
        });
    }

    private void SetAllRemote()
    {
        if (RequestConfirmation?.Invoke("全部选择来源", $"确认把全部 {Rows.Count} 项改为“{_labels.WriteCellText} / 删除行”？") == false)
        {
            return;
        }

        ChangeRowsWithUndo(Rows, () =>
        {
            foreach (var row in Rows)
            {
                row.SetOperationFromOwner(GetRemoteOperationText(row), markDecisionTouched: true);
            }
        });
    }

    private void ChangeRowOperation(MergeRowViewModel row, string operationText)
    {
        operationText = NormalizeOperationForRow(row, operationText);
        var scope = GetSynchronizationScope(row).ToList();
        ChangeRowsWithUndo(scope, () =>
        {
            row.SetOperationFromOwner(operationText, markDecisionTouched: true);
            SynchronizeRowOperation(row);
        });
    }

    private void ChangeRowsWithUndo(IEnumerable<MergeRowViewModel> scope, Action change)
    {
        var before = scope.Distinct().Select(row => row.Capture()).ToList();
        change();
        if (before.Any(HasSnapshotChanged))
        {
            _undoStack.Push(before);
        }

        RefreshDerivedState();
    }

    private string GetRemoteOperationText(MergeRowViewModel row)
    {
        if (!row.SourceCellExists)
        {
            return _labels.DeleteRowText;
        }

        return row.RequiresWholeRowSource ? _labels.AppendRowText : _labels.WriteCellText;
    }

    private string NormalizeOperationForRow(MergeRowViewModel row, string operationText)
    {
        return row.RequiresWholeRowSource && operationText == _labels.WriteCellText
            ? _labels.AppendRowText
            : operationText;
    }

    private bool HasSnapshotChanged(MergeRowSnapshot snapshot)
        => snapshot.OperationText != snapshot.Row.OperationText ||
            snapshot.WriteSheet != snapshot.Row.WriteSheet ||
            snapshot.WriteAddress != snapshot.Row.WriteAddress ||
            snapshot.IsDecisionTouched != snapshot.Row.IsDecisionTouched;

    private void UndoLastDecision()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _restoring = true;
        try
        {
            foreach (var snapshot in _undoStack.Pop())
            {
                snapshot.Row.Restore(snapshot);
            }
        }
        finally
        {
            _restoring = false;
        }

        RefreshDerivedState();
        OnPropertyChanged(nameof(SelectedOperationText));
    }

    private void Write()
    {
        if (!TryWrite(out var title, out var message))
        {
            RequestMessage?.Invoke(title, message);
            return;
        }

        RequestClose?.Invoke(true);
    }

    private void ShowKeyboardHelp()
    {
        RequestMessage?.Invoke(
            "快捷键",
            "J / ↓：下一项\nK / ↑：上一项\nH：保留目标\nL：使用来源\nN：下一未决冲突\nShift+N：上一冲突\nSpace：切换保留/使用\nCtrl+Z：撤销上一步\nCtrl+Enter：写入\nEsc：取消");
    }

    private void OnRowEdited(object? sender, MergeRowEditedEventArgs args)
    {
        if (_restoring)
        {
            return;
        }

        if (args.PropertyName is nameof(MergeRowViewModel.WriteAddress) or nameof(MergeRowViewModel.WriteSheet))
        {
            SynchronizeRowOperation(args.Row);
        }

        RefreshDerivedState();
    }

    private void SynchronizeRowOperation(MergeRowViewModel changedRow)
    {
        if (_syncingRows ||
            string.IsNullOrWhiteSpace(changedRow.RowMergeKey) ||
            !MergeRowViewModel.TryParseCellAddress(changedRow.WriteAddress, out var targetRow, out _))
        {
            return;
        }

        _syncingRows = true;
        try
        {
            var operation = changedRow.OperationText;
            var syncLocation = operation == _labels.AppendRowText ||
                operation == _labels.InsertRowText ||
                operation == _labels.DeleteRowText;
            foreach (var row in Rows.Where(row => string.Equals(row.RowMergeKey, changedRow.RowMergeKey, StringComparison.Ordinal)))
            {
                if (ReferenceEquals(row, changedRow))
                {
                    continue;
                }

                row.SetOperationFromOwner(changedRow.OperationText, changedRow.IsDecisionTouched);
                if (syncLocation)
                {
                    row.SetWriteLocationFromOwner(changedRow.WriteSheet, $"{row.WriteColumnName}{targetRow + 1}");
                }
            }
        }
        finally
        {
            _syncingRows = false;
        }
    }

    private IEnumerable<MergeRowViewModel> GetSynchronizationScope(MergeRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.RowMergeKey))
        {
            yield return row;
            yield break;
        }

        foreach (var scopedRow in Rows.Where(candidate => string.Equals(candidate.RowMergeKey, row.RowMergeKey, StringComparison.Ordinal)))
        {
            yield return scopedRow;
        }
    }

    private IEnumerable<MergeRowViewModel> GetFilteredRows()
        => Rows.Where(MatchesFilterAndSearch);

    private bool MatchesFilterAndSearch(MergeRowViewModel row)
    {
        var filterMatch = _activeFilter switch
        {
            FilterAutoRemote => row.Kind == SpreadsheetMergeChangeKind.AutoRemote,
            FilterLocalOnly => row.Kind == SpreadsheetMergeChangeKind.LocalOnly,
            FilterSameBoth => row.Kind == SpreadsheetMergeChangeKind.SameBoth,
            FilterConflict => row.Kind == SpreadsheetMergeChangeKind.Conflict,
            FilterRisk => row.IsHighRisk,
            _ => true,
        };
        if (!filterMatch)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var needle = SearchText.Trim();
        return row.Sheet.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.Address.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.RowId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.FieldName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.BaseValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.LocalValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.RemoteValue.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshGroups()
    {
        foreach (var group in Groups)
        {
            var allInGroup = Rows.Where(row => row.Kind == group.Kind).ToList();
            var visible = allInGroup.Where(MatchesFilterAndSearch);
            group.ReplaceRows(visible, allInGroup.Count);
        }
    }

    private void RefreshRelatedRows()
    {
        SelectedRelatedRows.Clear();
        if (SelectedRow == null)
        {
            OnPropertyChanged(nameof(LinkHintText));
            return;
        }

        var rows = string.IsNullOrWhiteSpace(SelectedRow.RowMergeKey)
            ? [SelectedRow]
            : Rows.Where(row => string.Equals(row.RowMergeKey, SelectedRow.RowMergeKey, StringComparison.Ordinal)).ToList();
        foreach (var row in rows)
        {
            SelectedRelatedRows.Add(row);
        }

        OnPropertyChanged(nameof(LinkHintText));
    }

    private void ExpandGroupFor(MergeRowViewModel row)
    {
        var group = Groups.FirstOrDefault(group => group.Kind == row.Kind);
        if (group != null)
        {
            group.IsExpanded = true;
        }
    }

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(DecidedCount));
        OnPropertyChanged(nameof(RemainingConflictCount));
        OnPropertyChanged(nameof(RiskCount));
        OnPropertyChanged(nameof(PlannedWriteCount));
        OnPropertyChanged(nameof(WriteSelectionCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RiskFilterText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectedOperationText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshFilterProperties()
    {
        OnPropertyChanged(nameof(IsAllFilterActive));
        OnPropertyChanged(nameof(IsAutoFilterActive));
        OnPropertyChanged(nameof(IsLocalFilterActive));
        OnPropertyChanged(nameof(IsSameFilterActive));
        OnPropertyChanged(nameof(IsConflictFilterActive));
        OnPropertyChanged(nameof(IsRiskFilterActive));
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
        => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
        => _execute(parameter);
}
