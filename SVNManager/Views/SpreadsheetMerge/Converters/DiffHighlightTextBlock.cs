using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace SVNManager.Views.SpreadsheetMerge.Converters;

public sealed class DiffHighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty TextValueProperty = DependencyProperty.Register(
        nameof(TextValue),
        typeof(string),
        typeof(DiffHighlightTextBlock),
        new PropertyMetadata("", OnHighlightInputChanged));

    public static readonly DependencyProperty OldValueProperty = DependencyProperty.Register(
        nameof(OldValue),
        typeof(string),
        typeof(DiffHighlightTextBlock),
        new PropertyMetadata("", OnHighlightInputChanged));

    public static readonly DependencyProperty NewValueProperty = DependencyProperty.Register(
        nameof(NewValue),
        typeof(string),
        typeof(DiffHighlightTextBlock),
        new PropertyMetadata("", OnHighlightInputChanged));

    public static readonly DependencyProperty HighlightRoleProperty = DependencyProperty.Register(
        nameof(HighlightRole),
        typeof(string),
        typeof(DiffHighlightTextBlock),
        new PropertyMetadata("None", OnHighlightInputChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush),
        typeof(MediaBrush),
        typeof(DiffHighlightTextBlock),
        new PropertyMetadata(MediaBrushes.Transparent, OnHighlightInputChanged));

    public string TextValue
    {
        get => (string)GetValue(TextValueProperty);
        set => SetValue(TextValueProperty, value);
    }

    public string OldValue
    {
        get => (string)GetValue(OldValueProperty);
        set => SetValue(OldValueProperty, value);
    }

    public string NewValue
    {
        get => (string)GetValue(NewValueProperty);
        set => SetValue(NewValueProperty, value);
    }

    public string HighlightRole
    {
        get => (string)GetValue(HighlightRoleProperty);
        set => SetValue(HighlightRoleProperty, value);
    }

    public MediaBrush HighlightBrush
    {
        get => (MediaBrush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static void OnHighlightInputChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is DiffHighlightTextBlock textBlock)
        {
            textBlock.RenderHighlightedText();
        }
    }

    private void RenderHighlightedText()
    {
        Inlines.Clear();
        var value = TextValue ?? "";
        if (value.Length == 0)
        {
            Inlines.Add(new Run("(空)") { Foreground = MediaBrushes.SlateGray });
            return;
        }

        var spans = GetSpans(value);
        if (spans.Count == 0)
        {
            Inlines.Add(new Run(value));
            return;
        }

        var cursor = 0;
        foreach (var span in spans.OrderBy(span => span.Start))
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start >= value.Length)
            {
                continue;
            }

            if (span.Start > cursor)
            {
                Inlines.Add(new Run(value[cursor..span.Start]));
            }

            var safeLength = Math.Min(span.Length, value.Length - span.Start);
            if (safeLength > 0)
            {
                Inlines.Add(new Run(value.Substring(span.Start, safeLength))
                {
                    Background = HighlightBrush,
                    FontWeight = FontWeights.SemiBold,
                });
            }

            cursor = Math.Max(cursor, span.Start + safeLength);
        }

        if (cursor < value.Length)
        {
            Inlines.Add(new Run(value[cursor..]));
        }
    }

    private IReadOnlyList<TextHighlightSpan> GetSpans(string value)
    {
        if (string.Equals(HighlightRole, "Old", StringComparison.OrdinalIgnoreCase))
        {
            return DiffHighlightSpans.Calculate(value, NewValue ?? "").OldSpans;
        }

        if (string.Equals(HighlightRole, "New", StringComparison.OrdinalIgnoreCase))
        {
            return DiffHighlightSpans.Calculate(OldValue ?? "", value).NewSpans;
        }

        return [];
    }
}
