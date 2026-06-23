using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace SVNManager.Views.SpreadsheetMerge;

public partial class MergeReviewWindow : Window
{
    private IMergeReviewModel? _model;
    private SpreadsheetMergeViewModel? _spreadsheetModel;
    private readonly Dictionary<MergeSheetViewModel, DataGrid> _gridBySheet = new();

    public MergeReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is IMergeReviewModel model)
        {
            _model = model;
            _model.RequestClose += OnRequestClose;
            _model.RequestMessage += OnRequestMessage;
            _model.RequestConfirmation += OnRequestConfirmation;
            _model.RequestRevealCell += OnRequestRevealCell;
            if (model is SpreadsheetMergeViewModel spreadsheet)
            {
                _spreadsheetModel = spreadsheet;
                _spreadsheetModel.RequestClassicView += OnRequestClassicView;
            }
        }
    }

    private void Detach()
    {
        if (_model != null)
        {
            _model.RequestClose -= OnRequestClose;
            _model.RequestMessage -= OnRequestMessage;
            _model.RequestConfirmation -= OnRequestConfirmation;
            _model.RequestRevealCell -= OnRequestRevealCell;
            _model = null;
        }

        if (_spreadsheetModel != null)
        {
            _spreadsheetModel.RequestClassicView -= OnRequestClassicView;
            _spreadsheetModel = null;
        }
    }

    private void OnRequestClose(bool? result)
    {
        DialogResult = result;
        Close();
    }

    private void OnRequestMessage(string title, string message)
        => WpfMessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private bool OnRequestConfirmation(string title, string message)
        => WpfMessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void OnSheetGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        // DataContext 可能在 Loaded 时还未就绪(TabControl + ContentTemplate 时序),
        // 所以同时挂 DataContextChanged 兜底建列。
        BuildColumnsIfNeeded(grid);
        grid.DataContextChanged -= OnSheetGridDataContextChanged;
        grid.DataContextChanged += OnSheetGridDataContextChanged;
    }

    private void OnSheetGridDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            BuildColumnsIfNeeded(grid);
        }
    }

    private void BuildColumnsIfNeeded(DataGrid grid)
    {
        if (grid.DataContext is not MergeSheetViewModel sheet)
        {
            return;
        }

        _gridBySheet[sheet] = grid;
        if (grid.Columns.Count > 0)
        {
            return;
        }

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "记录",
            CellTemplate = (DataTemplate)Resources["RecordCellTemplate"],
            Width = new DataGridLength(240),
        });

        foreach (var column in sheet.Columns)
        {
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                Width = new DataGridLength(170),
                CellTemplate = BuildCellTemplate(column.Key),
            });
        }
    }

    private static DataTemplate BuildCellTemplate(string columnKey)
    {
        var assembly = typeof(MergeReviewWindow).Assembly.GetName().Name;
        var xaml =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            $"xmlns:m=\"clr-namespace:SVNManager.Views.SpreadsheetMerge;assembly={assembly}\">" +
            $"<m:MergeCellControl Cell=\"{{Binding [{columnKey}]}}\" /></DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private void OnRequestRevealCell(MergeCellViewModel cell)
    {
        if (_model == null)
        {
            return;
        }

        foreach (var sheet in _model.Sheets)
        {
            foreach (var record in sheet.Records)
            {
                if (!record.CellsInOrder.Contains(cell))
                {
                    continue;
                }

                SheetTabs.SelectedItem = sheet;
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (_gridBySheet.TryGetValue(sheet, out var grid))
                        {
                            grid.SelectedItem = record;
                            grid.ScrollIntoView(record);
                        }
                    }),
                    DispatcherPriority.Background);
                return;
            }
        }
    }

    private void OnRequestClassicView()
    {
        if (_spreadsheetModel == null)
        {
            return;
        }

        if (!_spreadsheetModel.TryWrite(out var title, out var message))
        {
            OnRequestMessage(title, message);
            return;
        }

        var ownerHandle = new WindowInteropHelper(this).Handle;
        using var form = new SpreadsheetMergeConflictForm(
            _spreadsheetModel.RelativePath,
            _spreadsheetModel.Plan,
            _spreadsheetModel.TitlePrefix,
            _spreadsheetModel.TargetLabel,
            _spreadsheetModel.SourceLabel,
            _spreadsheetModel.ApplyButtonText);
        if (form.ShowDialog(new WpfOwnerWindow(ownerHandle)) == WinForms.DialogResult.OK)
        {
            DialogResult = true;
            Close();
        }
    }

    private sealed class WpfOwnerWindow : WinForms.IWin32Window
    {
        public WpfOwnerWindow(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}
