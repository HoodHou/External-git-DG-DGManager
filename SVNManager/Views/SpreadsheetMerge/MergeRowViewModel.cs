using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace SVNManager.Views.SpreadsheetMerge;

internal sealed class MergeOperationLabels
{
    public MergeOperationLabels(string targetLabel, string sourceLabel)
    {
        TargetLabel = targetLabel;
        SourceLabel = sourceLabel;
        KeepTargetText = string.Equals(targetLabel, "本地", StringComparison.Ordinal)
            ? "保留本地"
            : $"保留{targetLabel}";
        WriteCellText = string.Equals(sourceLabel, "远端 HEAD", StringComparison.Ordinal)
            ? "使用远端"
            : $"使用{sourceLabel}";
        AppendRowText = "新增行到末尾";
        InsertRowText = "插入新行";
        DeleteRowText = $"删除{targetLabel}行";
        All = [KeepTargetText, WriteCellText, AppendRowText, InsertRowText, DeleteRowText];
    }

    public string TargetLabel { get; }
    public string SourceLabel { get; }
    public string KeepTargetText { get; }
    public string WriteCellText { get; }
    public string AppendRowText { get; }
    public string InsertRowText { get; }
    public string DeleteRowText { get; }
    public IReadOnlyList<string> All { get; }
}

internal sealed class MergeRowViewModel : INotifyPropertyChanged, IMergeReviewRow
{
    private readonly SpreadsheetMergeChange _change;
    private readonly MergeOperationLabels _labels;
    private string _operationText;
    private string _writeSheet;
    private string _writeAddress;
    private bool _isDecisionTouched;
    private bool _isSelected;
    private bool _suppressEditEvent;
    private bool _isManual;
    private string? _manualDraft;

    /// <summary>由拥有者(VM)注入,使决议改动走 撤销 + 行级联动 的统一通道。</summary>
    internal Action<string>? OperationApplier { get; set; }

    public MergeRowViewModel(SpreadsheetMergeChange change, MergeOperationLabels labels)
    {
        _change = change;
        _labels = labels;
        _operationText = OperationToText(change.Operation);
        _writeSheet = change.WriteCell.Sheet;
        _writeAddress = $"{ExcelDiffService.ToColumnName(change.WriteCell.Column)}{change.WriteCell.Row + 1}";
        _isDecisionTouched = change.Kind != SpreadsheetMergeChangeKind.Conflict ||
            change.Operation != SpreadsheetMergeOperation.KeepTarget;
        Sides =
        [
            new MergeSideViewModel(this, MergeSideRole.Base, "BASE"),
            new MergeSideViewModel(this, MergeSideRole.Target, labels.TargetLabel),
            new MergeSideViewModel(this, MergeSideRole.Source, labels.SourceLabel),
        ];
        RowContextFields = new ObservableCollection<MergeRowContextFieldViewModel>(
            change.RowContext.Fields.Select(field => new MergeRowContextFieldViewModel(field)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler<MergeRowEditedEventArgs>? Edited;

    public ObservableCollection<MergeSideViewModel> Sides { get; }
    public ObservableCollection<MergeRowContextFieldViewModel> RowContextFields { get; }
    public IReadOnlyList<string> OperationOptions => _labels.All;
    public SpreadsheetMergeChangeKind Kind => _change.Kind;
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
    public bool IsDecisionTouched => _isDecisionTouched;
    public bool IsDecisionPending => Kind == SpreadsheetMergeChangeKind.Conflict && !_isDecisionTouched;
    public bool IsRemoteOperation => TextToOperation(OperationText) != SpreadsheetMergeOperation.KeepTarget;
    public bool IsHighRisk => !TargetRowExists && SourceCellExists || Kind == SpreadsheetMergeChangeKind.Conflict;
    public bool RequiresWholeRowSource => !TargetRowExists && SourceCellExists;
    public string TargetLabel => _labels.TargetLabel;
    public string SourceLabel => _labels.SourceLabel;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string OperationText
    {
        get => _operationText;
        set => SetOperationText(value, markDecisionTouched: true, raiseEditEvent: true);
    }

    public string WriteSheet
    {
        get => _writeSheet;
        set
        {
            if (SetField(ref _writeSheet, value?.Trim() ?? ""))
            {
                RaiseEdited(nameof(WriteSheet));
            }
        }
    }

    public string WriteAddress
    {
        get => _writeAddress;
        set
        {
            if (SetField(ref _writeAddress, (value ?? "").Trim().ToUpperInvariant()))
            {
                RaiseEdited(nameof(WriteAddress));
            }
        }
    }

    public string KindText => Kind switch
    {
        SpreadsheetMergeChangeKind.AutoRemote when !TargetRowExists && SourceCellExists => "来源新增行",
        SpreadsheetMergeChangeKind.AutoRemote when !SourceCellExists => "来源删除",
        SpreadsheetMergeChangeKind.AutoRemote => "可合并改动",
        SpreadsheetMergeChangeKind.LocalOnly => "目标独有",
        SpreadsheetMergeChangeKind.SameBoth => "双方相同",
        SpreadsheetMergeChangeKind.Conflict => "冲突",
        _ => "未知",
    };

    public string GroupText => Kind switch
    {
        SpreadsheetMergeChangeKind.AutoRemote => "自动应用",
        SpreadsheetMergeChangeKind.LocalOnly => $"{_labels.TargetLabel}独有",
        SpreadsheetMergeChangeKind.SameBoth => "双方相同",
        SpreadsheetMergeChangeKind.Conflict => "冲突",
        _ => "其它",
    };

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

    public string RiskReason
    {
        get
        {
            if (!TargetRowExists && SourceCellExists)
            {
                return "目标行缺失但来源单元格将写入，建议确认新增/插入位置。";
            }

            if (Kind == SpreadsheetMergeChangeKind.Conflict)
            {
                return "目标与来源同时改动，需要人工确认取舍。";
            }

            return "无高风险。";
        }
    }

    public string LocationText => $"{Sheet}!{Address}";
    public string ListTitle => $"{LocationText}  {FieldName}";
    public string ListSubtitle => $"ID {RowId} · {KindText} · {OperationText}";
    public string DecisionStatusText => IsDecisionPending ? "待决策" : OperationText;
    public int PlannedWriteCellCount
    {
        get
        {
            if (!IsRemoteOperation || string.Equals(LocalValue, RemoteValue, StringComparison.Ordinal))
            {
                return 0;
            }

            var operation = TextToOperation(OperationText);
            if (operation is SpreadsheetMergeOperation.AppendRow or SpreadsheetMergeOperation.InsertRow)
            {
                return Math.Max(1, RowContextFields.Count(field => !string.IsNullOrEmpty(field.RemoteValue)));
            }

            return 1;
        }
    }
    public string RowContextSummary => RowContextFields.Count == 0
        ? "当前项目没有整行上下文。"
        : $"整行字段 {RowContextFields.Count} 个，当前字段已高亮。";

    // ===== IMergeReviewRow:表格直显投影 + 单元格决议 =====
    public string SheetName => Sheet;
    public string RecordKey => string.IsNullOrEmpty(RowMergeKey)
        ? $"{Sheet}|P|{_change.TargetCell.Row}"
        : RowMergeKey;
    public string RecordTitle => RowId;
    public int ColumnOrder => _change.TargetCell.Column;
    public string ColumnHeader => string.IsNullOrWhiteSpace(FieldName) ? WriteColumnName : FieldName;
    public string EffectiveValue => IsRemoteOperation ? RemoteValue : LocalValue;
    public bool IsConflict => Kind == SpreadsheetMergeChangeKind.Conflict;
    public bool IsRowLevel => !TargetRowExists || !SourceCellExists;
    public bool SupportsManual => !IsRowLevel;
    public string RowLevelKindText => !TargetRowExists && SourceCellExists
        ? "整行新增"
        : !SourceCellExists ? "整行删除" : "";

    public MergeDecision Decision
    {
        get
        {
            if (IsDecisionPending)
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

    public void TakeLocal()
    {
        _isManual = false;
        Apply(_labels.KeepTargetText);
    }

    public void TakeRemote()
    {
        _isManual = false;
        var operation = SourceCellExists
            ? RequiresWholeRowSource ? _labels.AppendRowText : _labels.WriteCellText
            : _labels.DeleteRowText;
        Apply(operation);
    }

    public void ApplyManual(string value)
    {
        if (!SupportsManual)
        {
            return;
        }

        _change.RemoteValue = value ?? "";
        _manualDraft = value ?? "";
        _isManual = true;
        Apply(_labels.WriteCellText);
        OnPropertyChanged(nameof(RemoteValue));
        OnPropertyChanged(nameof(ManualValue));
        OnPropertyChanged(nameof(EffectiveValue));
    }

    /// <summary>
    /// 投影构建时调用。远端删除行的引擎初值是 WriteCell(对"删除"无效,旧 UI 要求用户手动改),
    /// 这里归一为"保留本地",保证默认可写,由用户显式采纳删除。
    /// </summary>
    internal void NormalizeInitialState()
    {
        if (!SourceCellExists && IsRemoteOperation)
        {
            SetOperationText(_labels.KeepTargetText, markDecisionTouched: false, raiseEditEvent: false);
        }
    }

    private void Apply(string operationText)
    {
        if (OperationApplier != null)
        {
            OperationApplier(operationText);
        }
        else
        {
            OperationText = operationText;
        }
    }

    internal MergeRowSnapshot Capture()
        => new(this, OperationText, WriteSheet, WriteAddress, IsDecisionTouched, RemoteValue, _isManual);

    internal void Restore(MergeRowSnapshot snapshot)
    {
        _suppressEditEvent = true;
        try
        {
            _operationText = snapshot.OperationText;
            _writeSheet = snapshot.WriteSheet;
            _writeAddress = snapshot.WriteAddress;
            _isDecisionTouched = snapshot.IsDecisionTouched;
            _change.RemoteValue = snapshot.RemoteValue;
            _isManual = snapshot.IsManual;
            _manualDraft = snapshot.IsManual ? snapshot.RemoteValue : null;
            RaiseAllMutableProperties();
            OnPropertyChanged(nameof(RemoteValue));
            OnPropertyChanged(nameof(ManualValue));
        }
        finally
        {
            _suppressEditEvent = false;
        }
    }

    internal bool SetOperationFromOwner(string operationText, bool markDecisionTouched)
        => SetOperationText(operationText, markDecisionTouched, raiseEditEvent: false);

    internal bool SetWriteLocationFromOwner(string sheet, string address)
    {
        var changed = false;
        _suppressEditEvent = true;
        try
        {
            changed |= SetField(ref _writeSheet, sheet?.Trim() ?? "", nameof(WriteSheet));
            changed |= SetField(ref _writeAddress, (address ?? "").Trim().ToUpperInvariant(), nameof(WriteAddress));
        }
        finally
        {
            _suppressEditEvent = false;
        }

        return changed;
    }

    internal bool TryApplyToChange(out string error)
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

        if (operation == SpreadsheetMergeOperation.WriteCell && !TargetRowExists && SourceCellExists)
        {
            operation = SpreadsheetMergeOperation.AppendRow;
        }

        _change.Operation = operation;
        _change.Resolution = operation == SpreadsheetMergeOperation.KeepTarget
            ? SpreadsheetMergeResolution.UseLocal
            : SpreadsheetMergeResolution.UseRemote;
        _change.WriteCell = new ExcelCellKey(sheet, row, column);
        return true;
    }

    internal static bool TryParseCellAddress(string address, out int row, out int column)
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

    internal SpreadsheetMergeOperation TextToOperation(string text)
    {
        if (text == _labels.WriteCellText)
        {
            return SpreadsheetMergeOperation.WriteCell;
        }

        if (text == _labels.AppendRowText)
        {
            return SpreadsheetMergeOperation.AppendRow;
        }

        if (text == _labels.InsertRowText)
        {
            return SpreadsheetMergeOperation.InsertRow;
        }

        if (text == _labels.DeleteRowText)
        {
            return SpreadsheetMergeOperation.DeleteRow;
        }

        return SpreadsheetMergeOperation.KeepTarget;
    }

    private bool SetOperationText(string value, bool markDecisionTouched, bool raiseEditEvent)
    {
        var next = string.IsNullOrWhiteSpace(value) ? _labels.KeepTargetText : value;
        if (next == _operationText && (!markDecisionTouched || _isDecisionTouched))
        {
            return false;
        }

        _operationText = next;
        if (markDecisionTouched)
        {
            _isDecisionTouched = true;
        }

        RaiseAllDecisionProperties();
        if (raiseEditEvent)
        {
            RaiseEdited(nameof(OperationText));
        }

        return true;
    }

    private string OperationToText(SpreadsheetMergeOperation operation)
    {
        return operation switch
        {
            SpreadsheetMergeOperation.WriteCell => _labels.WriteCellText,
            SpreadsheetMergeOperation.AppendRow => _labels.AppendRowText,
            SpreadsheetMergeOperation.InsertRow => _labels.InsertRowText,
            SpreadsheetMergeOperation.DeleteRow => _labels.DeleteRowText,
            _ => _labels.KeepTargetText,
        };
    }

    private void RaiseEdited(string propertyName)
    {
        if (!_suppressEditEvent)
        {
            Edited?.Invoke(this, new MergeRowEditedEventArgs(this, propertyName));
        }
    }

    private void RaiseAllMutableProperties()
    {
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(WriteSheet));
        OnPropertyChanged(nameof(WriteAddress));
        RaiseAllDecisionProperties();
    }

    private void RaiseAllDecisionProperties()
    {
        OnPropertyChanged(nameof(OperationText));
        OnPropertyChanged(nameof(IsDecisionTouched));
        OnPropertyChanged(nameof(IsDecisionPending));
        OnPropertyChanged(nameof(IsRemoteOperation));
        OnPropertyChanged(nameof(PlannedWriteCellCount));
        OnPropertyChanged(nameof(ListSubtitle));
        OnPropertyChanged(nameof(DecisionStatusText));
        OnPropertyChanged(nameof(EffectiveValue));
        OnPropertyChanged(nameof(Decision));
        foreach (var side in Sides)
        {
            side.Refresh();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal enum MergeSideRole
{
    Base,
    Target,
    Source,
}

internal sealed class MergeSideViewModel : INotifyPropertyChanged
{
    private readonly MergeSideRole _role;
    private readonly string _title;

    public MergeSideViewModel(MergeRowViewModel row, MergeSideRole role, string title)
    {
        Row = row;
        _role = role;
        _title = title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MergeRowViewModel Row { get; }
    public string RoleKey => _role.ToString();
    public string Title => _title;
    public string Value => _role switch
    {
        MergeSideRole.Base => Row.BaseValue,
        MergeSideRole.Target => Row.LocalValue,
        MergeSideRole.Source => Row.RemoteValue,
        _ => "",
    };
    public string DisplayValue => string.IsNullOrEmpty(Value) ? "(空)" : Value;
    public string HighlightRole => _role switch
    {
        MergeSideRole.Target => "Old",
        MergeSideRole.Source => "New",
        _ => "None",
    };
    public bool CanAdopt => _role != MergeSideRole.Base && !IsSelected;
    public bool ShowsAdoptButton => _role != MergeSideRole.Base;
    public bool IsSelected => _role switch
    {
        MergeSideRole.Target => !Row.IsRemoteOperation,
        MergeSideRole.Source => Row.IsRemoteOperation,
        _ => false,
    };
    public string BadgeText => IsSelected ? "已采用" : "";
    public string ButtonText => IsSelected ? "已采用" : "采用";
    public string AccentBrush => _role switch
    {
        MergeSideRole.Target => "#1E40AF",
        MergeSideRole.Source => "#166534",
        _ => "#475569",
    };
    public string CardBackground => _role switch
    {
        MergeSideRole.Target => "#EFF6FF",
        MergeSideRole.Source => "#ECFDF5",
        _ => "#F8FAFC",
    };
    public string HighlightBrush => _role switch
    {
        MergeSideRole.Target => "#DBEAFE",
        MergeSideRole.Source => "#BBF7D0",
        _ => "#E2E8F0",
    };
    public string CardBorderBrush => IsSelected ? AccentBrush : "#CBD5E1";
    public Thickness CardBorderThickness => IsSelected ? new Thickness(2) : new Thickness(1);

    internal bool IsTarget => _role == MergeSideRole.Target;
    internal bool IsSource => _role == MergeSideRole.Source;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanAdopt));
        OnPropertyChanged(nameof(ShowsAdoptButton));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class MergeRowContextFieldViewModel
{
    public MergeRowContextFieldViewModel(SpreadsheetMergeRowContextField field)
    {
        FieldName = field.FieldName;
        ColumnName = field.ColumnName;
        ColumnIndex = field.ColumnIndex;
        BaseValue = field.BaseValue;
        LocalValue = field.LocalValue;
        RemoteValue = field.RemoteValue;
        IsCurrentField = field.IsCurrentField;
    }

    public string FieldName { get; }
    public string ColumnName { get; }
    public int ColumnIndex { get; }
    public string FieldLabel => string.IsNullOrWhiteSpace(ColumnName) ? FieldName : $"{ColumnName}  {FieldName}";
    public string BaseValue { get; }
    public string LocalValue { get; }
    public string RemoteValue { get; }
    public bool IsCurrentField { get; }
}

internal sealed class MergeRowGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string _countText = "";

    public MergeRowGroupViewModel(string header, SpreadsheetMergeChangeKind kind, bool isExpanded)
    {
        Header = header;
        Kind = kind;
        _isExpanded = isExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Header { get; }
    public SpreadsheetMergeChangeKind Kind { get; }
    public ObservableCollection<MergeRowViewModel> VisibleRows { get; } = [];
    public bool HasVisibleRows => VisibleRows.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public string CountText
    {
        get => _countText;
        private set
        {
            if (_countText == value)
            {
                return;
            }

            _countText = value;
            OnPropertyChanged();
        }
    }

    internal void ReplaceRows(IEnumerable<MergeRowViewModel> rows, int totalCount)
    {
        VisibleRows.Clear();
        foreach (var row in rows)
        {
            VisibleRows.Add(row);
        }

        CountText = VisibleRows.Count == totalCount
            ? totalCount.ToString()
            : $"{VisibleRows.Count}/{totalCount}";
        OnPropertyChanged(nameof(HasVisibleRows));
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record MergeRowSnapshot(
    MergeRowViewModel Row,
    string OperationText,
    string WriteSheet,
    string WriteAddress,
    bool IsDecisionTouched,
    string RemoteValue,
    bool IsManual);

internal sealed record MergeRowEditedEventArgs(MergeRowViewModel Row, string PropertyName);
