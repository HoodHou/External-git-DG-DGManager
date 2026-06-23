using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SVNManager.Views.SpreadsheetMerge;

/// <summary>用户对一个可决议单元格/记录的取舍状态。</summary>
internal enum MergeDecision
{
    Pending,
    KeepLocal,
    TakeRemote,
    Manual,
}

/// <summary>
/// 新表格直显窗口绑定的总模型。表格 VM 与 XML VM 都实现它,
/// 这样同一个 <c>MergeReviewWindow</c> 可服务两套引擎。
/// </summary>
internal interface IMergeReviewModel : INotifyPropertyChanged
{
    string WindowTitle { get; }
    string RelativePath { get; }
    string ApplyButtonText { get; }
    string TargetLabel { get; }
    string SourceLabel { get; }

    string SummaryText { get; }
    string ProgressText { get; }
    double ProgressMaximum { get; }
    int DecidedCount { get; }
    string AutoFilterText { get; }
    string ConflictFilterText { get; }
    string RiskFilterText { get; }

    string SearchText { get; set; }
    ObservableCollection<MergeSheetViewModel> Sheets { get; }

    ICommand SetFilterCommand { get; }
    ICommand NextConflictCommand { get; }
    ICommand PrevConflictCommand { get; }
    ICommand AllConflictsLocalCommand { get; }
    ICommand AllConflictsRemoteCommand { get; }
    ICommand AllLocalCommand { get; }
    ICommand AllRemoteCommand { get; }
    ICommand UndoLastDecisionCommand { get; }
    ICommand WriteCommand { get; }
    ICommand CancelCommand { get; }
    ICommand ShowHelpCommand { get; }

    /// <summary>仅表格侧提供「经典视图」回退;XML 侧为 null,按钮隐藏。</summary>
    ICommand? ClassicViewCommand { get; }

    event Action<bool?>? RequestClose;
    event Action<string, string>? RequestMessage;
    event Func<string, string, bool>? RequestConfirmation;

    /// <summary>请求把某个单元格滚动进视野并高亮(跳到下一冲突时)。</summary>
    event Action<MergeCellViewModel>? RequestRevealCell;
}

/// <summary>
/// 单条可决议改动的引擎无关视图。<see cref="MergeRowViewModel"/>(表格)与
/// <see cref="XmlMergeRow"/>(XML)都实现它,投影层据此摆进 记录×字段 表格。
/// </summary>
internal interface IMergeReviewRow : INotifyPropertyChanged
{
    SpreadsheetMergeChangeKind Kind { get; }

    string SheetName { get; }
    string RecordKey { get; }
    string RecordTitle { get; }
    int ColumnOrder { get; }
    string ColumnHeader { get; }

    string BaseValue { get; }
    string LocalValue { get; }
    string RemoteValue { get; }
    string EffectiveValue { get; }

    MergeDecision Decision { get; }
    bool IsConflict { get; }
    bool IsRowLevel { get; }
    bool IsHighRisk { get; }
    bool SupportsManual { get; }
    string RowLevelKindText { get; }
    string ManualValue { get; set; }

    void TakeLocal();
    void TakeRemote();
    void ApplyManual(string value);
}

/// <summary>表格一列(合成安全键 + 显示名 + 排序)。</summary>
internal sealed record MergeColumn(string Key, string Header, int Order);

/// <summary>一个工作表 / XML 顶层记录类型,对应窗口里的一个 Tab。</summary>
internal sealed class MergeSheetViewModel
{
    public MergeSheetViewModel(string name, IReadOnlyList<MergeColumn> columns, IReadOnlyList<MergeRecordViewModel> records)
    {
        Name = name;
        Columns = columns;
        Records = new ObservableCollection<MergeRecordViewModel>(records);
    }

    public string Name { get; }
    public IReadOnlyList<MergeColumn> Columns { get; }
    public ObservableCollection<MergeRecordViewModel> Records { get; }
    public string TabHeader => $"{Name}（{Records.Count}）";

    /// <summary>按谓词筛选本 Tab 的记录(过滤 chip / 搜索框驱动)。</summary>
    public void ApplyFilter(Predicate<MergeRecordViewModel> predicate)
    {
        var view = CollectionViewSource.GetDefaultView(Records);
        if (view == null)
        {
            return;
        }

        view.Filter = item => item is MergeRecordViewModel record && predicate(record);
        view.Refresh();
    }
}

/// <summary>一条记录(表格的一行 / 一个 XML 元素)。列按合成键索引。</summary>
internal sealed class MergeRecordViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<string, MergeCellViewModel> _cellsByKey;
    private readonly IMergeReviewRow? _rowLevelAnchor;

    public MergeRecordViewModel(
        string recordKey,
        string title,
        SpreadsheetMergeChangeKind kind,
        bool isRowLevel,
        string rowLevelKindText,
        IReadOnlyList<MergeCellViewModel> cellsInOrder,
        IReadOnlyDictionary<string, MergeCellViewModel> cellsByKey,
        IMergeReviewRow? rowLevelAnchor)
    {
        RecordKey = recordKey;
        Title = title;
        Kind = kind;
        IsRowLevel = isRowLevel;
        RowLevelKindText = rowLevelKindText;
        CellsInOrder = cellsInOrder;
        _cellsByKey = cellsByKey;
        _rowLevelAnchor = rowLevelAnchor;
        HasConflict = cellsInOrder.Any(cell => cell.IsConflict);
        HasAuto = cellsInOrder.Any(cell => cell.Row?.Kind == SpreadsheetMergeChangeKind.AutoRemote);
        HasRisk = cellsInOrder.Any(cell => cell.Row?.IsHighRisk == true);
        if (_rowLevelAnchor != null)
        {
            _rowLevelAnchor.PropertyChanged += (_, _) => RaiseRowLevelState();
        }

        AdoptRowCommand = new RelayCommand(_ => _rowLevelAnchor?.TakeRemote(), _ => _rowLevelAnchor != null);
        IgnoreRowCommand = new RelayCommand(_ => _rowLevelAnchor?.TakeLocal(), _ => _rowLevelAnchor != null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RecordKey { get; }
    public string Title { get; }
    public SpreadsheetMergeChangeKind Kind { get; }
    public bool IsRowLevel { get; }
    public string RowLevelKindText { get; }
    public IReadOnlyList<MergeCellViewModel> CellsInOrder { get; }

    public ICommand AdoptRowCommand { get; }
    public ICommand IgnoreRowCommand { get; }

    public bool HasConflict { get; }
    public bool HasAuto { get; }
    public bool HasRisk { get; }

    /// <summary>搜索匹配:标题或任一格的值命中。</summary>
    public bool MatchesSearch(string needle)
    {
        if (Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CellsInOrder.Any(cell => cell.IsPresent && (
            cell.TextValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            cell.BaseValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            cell.LocalValue.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            cell.RemoteValue.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    public bool IsRowLevelAdopted => _rowLevelAnchor?.Decision is MergeDecision.TakeRemote or MergeDecision.Manual;
    public string RowLevelStatusText => !IsRowLevel
        ? ""
        : IsRowLevelAdopted ? $"✓ 已采纳{RowLevelKindText}" : $"待决定：{RowLevelKindText}";

    /// <summary>WPF 列绑定 <c>{Binding [c3]}</c> 走这个索引器(合成安全键)。</summary>
    public MergeCellViewModel this[string columnKey]
        => _cellsByKey.TryGetValue(columnKey, out var cell) ? cell : MergeCellViewModel.Blank;

    private void RaiseRowLevelState()
    {
        OnPropertyChanged(nameof(IsRowLevelAdopted));
        OnPropertyChanged(nameof(RowLevelStatusText));
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>表格里的一格。可交互格包一个 <see cref="IMergeReviewRow"/>;否则是只读上下文/空白。</summary>
internal sealed class MergeCellViewModel : INotifyPropertyChanged
{
    public static readonly MergeCellViewModel Blank = new();

    private readonly IMergeReviewRow? _row;
    private readonly string _contextValue;
    private readonly SpreadsheetMergeChangeKind _contextKind;
    private readonly bool _present;

    private MergeCellViewModel()
    {
        _contextValue = "";
        _present = false;
    }

    /// <summary>只读上下文格(该字段无 可合并/冲突 改动)。</summary>
    public MergeCellViewModel(string contextValue, SpreadsheetMergeChangeKind contextKind)
    {
        _contextValue = contextValue ?? "";
        _contextKind = contextKind;
        _present = true;
    }

    /// <summary>可交互格(包一条 AutoRemote / Conflict 改动)。</summary>
    public MergeCellViewModel(IMergeReviewRow row)
    {
        _row = row;
        _contextValue = "";
        _present = true;
        _row.PropertyChanged += (_, _) => Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IMergeReviewRow? Row => _row;
    public bool IsPresent => _present;
    public bool IsInteractive => _row != null;
    public bool IsConflict => _row?.IsConflict ?? false;
    public bool IsPending => _row?.Decision == MergeDecision.Pending;
    public bool IsClickable => _row != null && !_row.IsRowLevel;
    public bool SupportsManual => _row?.SupportsManual ?? false;

    public string TextValue
    {
        get
        {
            if (_row == null)
            {
                return _contextValue;
            }

            // 行级新增/删除:预览将新增的远端内容 / 将删除的本地内容,而非随决议变空。
            if (_row.IsRowLevel)
            {
                return string.IsNullOrEmpty(_row.RemoteValue) ? _row.LocalValue : _row.RemoteValue;
            }

            return _row.EffectiveValue;
        }
    }
    public string OldValue => _row?.LocalValue ?? "";
    public string NewValue => _row?.RemoteValue ?? "";
    public string BaseValue => _row?.BaseValue ?? "";
    public string LocalValue => _row?.LocalValue ?? _contextValue;
    public string RemoteValue => _row?.RemoteValue ?? "";

    public string HighlightRole => _row == null
        ? "None"
        : _row.Decision == MergeDecision.KeepLocal ? "Old" : "New";

    public MediaBrush HighlightBrush => _row == null
        ? MediaBrushes.Transparent
        : _row.Decision == MergeDecision.KeepLocal ? Palette.TargetHighlight : Palette.SourceHighlight;

    public string DecisionGlyph
    {
        get
        {
            if (_row == null)
            {
                return "";
            }

            return _row.Decision switch
            {
                MergeDecision.Pending => "⚠",
                MergeDecision.KeepLocal => "◀",
                _ => "✓",
            };
        }
    }

    public string ManualValue
    {
        get => _row?.ManualValue ?? "";
        set
        {
            if (_row != null)
            {
                _row.ManualValue = value;
                OnPropertyChanged();
            }
        }
    }

    public MediaBrush CellBrush
    {
        get
        {
            if (!_present)
            {
                return Palette.Blank;
            }

            if (_row == null)
            {
                return _contextKind == SpreadsheetMergeChangeKind.LocalOnly ? Palette.Local : Palette.Neutral;
            }

            if (_row.Kind == SpreadsheetMergeChangeKind.Conflict)
            {
                return _row.Decision switch
                {
                    MergeDecision.Pending => Palette.ConflictPending,
                    MergeDecision.KeepLocal => Palette.Local,
                    _ => Palette.Remote,
                };
            }

            // AutoRemote(可交互):默认取远端→绿;用户翻成保留→蓝
            return _row.Decision == MergeDecision.KeepLocal ? Palette.Local : Palette.Remote;
        }
    }

    public MediaBrush BorderBrush => IsPending ? Palette.ConflictBorder : Palette.CellBorder;

    public void TakeLocal() => _row?.TakeLocal();
    public void TakeRemote() => _row?.TakeRemote();
    public void ApplyManual(string value) => _row?.ApplyManual(value);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(OldValue));
        OnPropertyChanged(nameof(NewValue));
        OnPropertyChanged(nameof(RemoteValue));
        OnPropertyChanged(nameof(HighlightRole));
        OnPropertyChanged(nameof(HighlightBrush));
        OnPropertyChanged(nameof(DecisionGlyph));
        OnPropertyChanged(nameof(CellBrush));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(IsConflict));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(ManualValue));
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>表格配色(与 <c>MergeKindBrushConverter</c> / 三卡高亮一致),冻结复用。</summary>
internal static class Palette
{
    public static readonly MediaBrush Remote = Frozen(235, 255, 239);   // 绿:取远端
    public static readonly MediaBrush Local = Frozen(239, 246, 255);    // 蓝:保留本地
    public static readonly MediaBrush Neutral = Frozen(248, 250, 252);  // 中性:未改/双方相同
    public static readonly MediaBrush Blank = Frozen(252, 252, 253);    // 该记录无此列
    public static readonly MediaBrush ConflictPending = Frozen(255, 237, 213); // 橙:待决议冲突
    public static readonly MediaBrush TargetHighlight = Frozen(219, 234, 254);
    public static readonly MediaBrush SourceHighlight = Frozen(187, 247, 208);
    public static readonly MediaBrush CellBorder = Frozen(226, 232, 240);
    public static readonly MediaBrush ConflictBorder = Frozen(249, 115, 22);

    private static MediaBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new MediaSolidColorBrush(MediaColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
