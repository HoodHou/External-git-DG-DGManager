using System.Globalization;
using System.Windows.Data;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfBinding = System.Windows.Data.Binding;

namespace SVNManager.Views.SpreadsheetMerge.Converters;

public sealed class MergeKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SpreadsheetMergeChangeKind kind)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(248, 250, 252));
        }

        return kind switch
        {
            SpreadsheetMergeChangeKind.AutoRemote => new MediaSolidColorBrush(MediaColor.FromRgb(235, 255, 239)),
            SpreadsheetMergeChangeKind.LocalOnly => new MediaSolidColorBrush(MediaColor.FromRgb(239, 246, 255)),
            SpreadsheetMergeChangeKind.SameBoth => new MediaSolidColorBrush(MediaColor.FromRgb(248, 250, 252)),
            SpreadsheetMergeChangeKind.Conflict => new MediaSolidColorBrush(MediaColor.FromRgb(255, 247, 237)),
            _ => new MediaSolidColorBrush(MediaColor.FromRgb(255, 255, 255)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}
