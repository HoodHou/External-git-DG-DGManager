using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class FileTreeNodeSorter : System.Collections.IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not TreeNode left || y is not TreeNode right)
        {
            return 0;
        }

        var leftIsFile = IsFileNode(left);
        var rightIsFile = IsFileNode(right);
        if (leftIsFile != rightIsFile)
        {
            return leftIsFile ? 1 : -1;
        }

        return string.Compare(CleanNodeText(left.Text), CleanNodeText(right.Text), StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsFileNode(TreeNode node)
    {
        return node.Tag is ChangedFileEntry || node.Tag is FileTreeNodeInfo { IsFile: true };
    }

    private static string CleanNodeText(string text)
    {
        return text.Length > 2 && text[1] == ' ' && "NMAD?!CRI".Contains(text[0], StringComparison.Ordinal)
            ? text[2..]
            : text;
    }
}

internal sealed class ShellTabControl : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TcmAdjustRect && !DesignMode)
        {
            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (radius == 0)
        {
            graphics.FillRectangle(brush, bounds);
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (radius == 0)
        {
            graphics.DrawRectangle(pen, bounds);
            return;
        }

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}

internal sealed class ShellNavButton : Control
{
    private bool _active;
    private bool _hovered;

    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string TabText { get; init; } = "";

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            Invalidate();
        }
    }

    public ShellNavButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        ForeColor = Color.FromArgb(213, 220, 230);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var background = Active
            ? Color.FromArgb(39, 50, 67)
            : _hovered ? Color.FromArgb(32, 41, 55) : Color.FromArgb(24, 31, 42);
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRoundedRectangle(backgroundBrush, new Rectangle(0, 0, Width - 1, Height - 1), 8);

        if (Active)
        {
            using var accentBrush = new SolidBrush(Color.FromArgb(88, 166, 255));
            e.Graphics.FillRoundedRectangle(accentBrush, new Rectangle(0, 10, 4, Height - 20), 3);
        }

        var glyphColor = Active ? Color.White : Color.FromArgb(175, 186, 202);
        var titleColor = Active ? Color.White : Color.FromArgb(197, 206, 220);
        using var titleFont = new Font("Microsoft YaHei UI", 9F, Active ? FontStyle.Bold : FontStyle.Regular);
        DrawShellIcon(e.Graphics, new Rectangle((Width - 24) / 2, 7, 24, 22), glyphColor);
        TextRenderer.DrawText(
            e.Graphics,
            Title,
            titleFont,
            new Rectangle(8, 30, Width - 16, 22),
            titleColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private void DrawShellIcon(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.7F) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var brush = new SolidBrush(color);
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        switch (Glyph)
        {
            case "CFG":
                graphics.DrawEllipse(pen, center.X - 5, center.Y - 5, 10, 10);
                for (var index = 0; index < 8; index++)
                {
                    var angle = index * Math.PI / 4;
                    var x1 = center.X + (int)(Math.Cos(angle) * 8);
                    var y1 = center.Y + (int)(Math.Sin(angle) * 8);
                    var x2 = center.X + (int)(Math.Cos(angle) * 10);
                    var y2 = center.Y + (int)(Math.Sin(angle) * 10);
                    graphics.DrawLine(pen, x1, y1, x2, y2);
                }

                break;
            case "STS":
                for (var index = 0; index < 3; index++)
                {
                    var y = bounds.Top + 4 + index * 7;
                    graphics.FillEllipse(brush, bounds.Left + 2, y - 1, 4, 4);
                    graphics.DrawLine(pen, bounds.Left + 10, y + 1, bounds.Right - 2, y + 1);
                }

                break;
            case "CNF":
                var triangle = new[]
                {
                    new Point(center.X, bounds.Top + 2),
                    new Point(bounds.Right - 2, bounds.Bottom - 2),
                    new Point(bounds.Left + 2, bounds.Bottom - 2),
                };
                graphics.DrawPolygon(pen, triangle);
                graphics.DrawLine(pen, center.X, bounds.Top + 8, center.X, bounds.Bottom - 8);
                graphics.FillEllipse(brush, center.X - 1, bounds.Bottom - 5, 3, 3);
                break;
            case "ALL":
                graphics.DrawRectangle(pen, bounds.Left + 5, bounds.Top + 2, 13, 17);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 7, bounds.Right - 7, bounds.Top + 7);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 11, bounds.Right - 5, bounds.Top + 11);
                graphics.DrawLine(pen, bounds.Left + 8, bounds.Top + 15, bounds.Right - 8, bounds.Top + 15);
                break;
            case "HIS":
                graphics.DrawEllipse(pen, bounds.Left + 3, bounds.Top + 2, 18, 18);
                graphics.DrawLine(pen, center.X, center.Y, center.X, bounds.Top + 7);
                graphics.DrawLine(pen, center.X, center.Y, bounds.Right - 7, center.Y + 3);
                break;
            default:
                graphics.FillEllipse(brush, center.X - 4, center.Y - 4, 8, 8);
                break;
        }
    }
}

