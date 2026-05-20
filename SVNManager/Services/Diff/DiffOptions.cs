namespace SVNManager;

internal sealed class DiffOptions
{
    public bool IgnoreWhitespace { get; set; }
    public bool IgnoreCase { get; set; }
    public bool IgnoreLineEndings { get; set; } = true;
    public bool ShowInlineHighlight { get; set; } = true;
    public int ContextLines { get; set; } = 2;

    public DiffOptions Clone()
    {
        return new DiffOptions
        {
            IgnoreWhitespace = IgnoreWhitespace,
            IgnoreCase = IgnoreCase,
            IgnoreLineEndings = IgnoreLineEndings,
            ShowInlineHighlight = ShowInlineHighlight,
            ContextLines = ContextLines,
        };
    }
}
