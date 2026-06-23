using System.Windows;
using System.Windows.Input;

namespace SVNManager.Views.SpreadsheetMerge;

public partial class MergeCellControl : System.Windows.Controls.UserControl
{
    // DP 类型用 object 以避免公有控件暴露 internal 的 MergeCellViewModel(可见性冲突)。
    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(
        nameof(Cell),
        typeof(object),
        typeof(MergeCellControl),
        new PropertyMetadata(null, OnCellChanged));

    public MergeCellControl()
    {
        InitializeComponent();
    }

    public object? Cell
    {
        get => GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    private MergeCellViewModel? CellModel => Cell as MergeCellViewModel;

    private static void OnCellChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MergeCellControl control)
        {
            control.LayoutRoot.DataContext = e.NewValue;
        }
    }

    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (CellModel is { IsClickable: true })
        {
            DecisionPopup.IsOpen = true;
        }
    }

    private void OnTakeLocal(object sender, RoutedEventArgs e)
    {
        CellModel?.TakeLocal();
        DecisionPopup.IsOpen = false;
    }

    private void OnTakeRemote(object sender, RoutedEventArgs e)
    {
        CellModel?.TakeRemote();
        DecisionPopup.IsOpen = false;
    }

    private void OnApplyManual(object sender, RoutedEventArgs e)
    {
        CellModel?.ApplyManual(ManualBox.Text);
        DecisionPopup.IsOpen = false;
    }
}
