using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace SVNManager.Views.SpreadsheetMerge;

public partial class SpreadsheetMergeWindow : Window
{
    private SpreadsheetMergeViewModel? _viewModel;

    public SpreadsheetMergeWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) => DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        DetachViewModel();
        if (args.NewValue is SpreadsheetMergeViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.RequestClose += OnRequestClose;
            _viewModel.RequestMessage += OnRequestMessage;
            _viewModel.RequestConfirmation += OnRequestConfirmation;
            _viewModel.RequestClassicView += OnRequestClassicView;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.RequestMessage -= OnRequestMessage;
        _viewModel.RequestConfirmation -= OnRequestConfirmation;
        _viewModel.RequestClassicView -= OnRequestClassicView;
        _viewModel = null;
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }

    private void OnRequestMessage(string title, string message)
    {
        WpfMessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool OnRequestConfirmation(string title, string message)
        => WpfMessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void OnRequestClassicView()
    {
        if (_viewModel == null)
        {
            return;
        }

        if (!_viewModel.TryWrite(out var title, out var message))
        {
            OnRequestMessage(title, message);
            return;
        }

        var ownerHandle = new WindowInteropHelper(this).Handle;
        using var form = new SpreadsheetMergeConflictForm(
            _viewModel.RelativePath,
            _viewModel.Plan,
            _viewModel.TitlePrefix,
            _viewModel.TargetLabel,
            _viewModel.SourceLabel,
            _viewModel.ApplyButtonText);
        if (form.ShowDialog(new WpfOwnerWindow(ownerHandle)) == WinForms.DialogResult.OK)
        {
            DialogResult = true;
            Close();
        }
    }

    private sealed class WpfOwnerWindow : WinForms.IWin32Window
    {
        public WpfOwnerWindow(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
