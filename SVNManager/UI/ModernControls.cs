namespace SVNManager;

internal static class ModernTheme
{
    public static Color AppBackColor => Color.FromArgb(245, 247, 250);
    public static Color SurfaceColor => Color.White;
    public static Color SubtleSurfaceColor => Color.FromArgb(248, 250, 252);
    public static Color ToolbarColor => Color.FromArgb(241, 243, 245);
    public static Color BorderColor => Color.FromArgb(190, 200, 214);
    public static Color BorderSubtleColor => Color.FromArgb(226, 232, 240);
    public static Color TextColor => Color.FromArgb(15, 23, 42);
    public static Color MutedTextColor => Color.FromArgb(100, 116, 139);
    public static Color AccentColor => Color.FromArgb(37, 99, 235);
    public static Color AccentHoverColor => Color.FromArgb(232, 240, 254);
    public static Color AccentPressedColor => Color.FromArgb(219, 234, 254);
    public static Color PrimaryTextOnAccent => Color.White;
    public static Color SidebarColor => Color.FromArgb(17, 24, 39);
    public static Color ShadowColor => Color.FromArgb(28, 15, 23, 42);
}

internal class ModernButton : Button
{
    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = ModernTheme.SubtleSurfaceColor;
        ForeColor = ModernTheme.TextColor;
        FlatAppearance.BorderColor = ModernTheme.BorderColor;
        FlatAppearance.MouseOverBackColor = ModernTheme.AccentHoverColor;
        FlatAppearance.MouseDownBackColor = ModernTheme.AccentPressedColor;
        Font = new Font("Microsoft YaHei UI", 9F);
        UseVisualStyleBackColor = false;
    }
}

internal enum ModernToolbarButtonKind
{
    Primary,
    Ghost,
    Subtle,
}

internal enum ModernToolbarIcon
{
    None,
    Import,
    Manage,
    Save,
    Remove,
    Update,
    Status,
    Commit,
    Folder,
    More,
    Settings,
    History,
}

internal sealed class ModernToolbarButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public ModernToolbarIcon Icon { get; set; }
    public ModernToolbarButtonKind Kind { get; set; } = ModernToolbarButtonKind.Ghost;

    public ModernToolbarButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Height = 38;
        UseVisualStyleBackColor = false;
        TabStop = false;
    }

    protected override bool ShowFocusCues => false;

    public override void NotifyDefault(bool value)
    {
        base.NotifyDefault(false);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor ?? ModernTheme.SurfaceColor);
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
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? ModernTheme.SurfaceColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        var backColor = GetBackColor();
        var borderColor = GetBorderColor();
        var textColor = Kind switch
        {
            ModernToolbarButtonKind.Primary => ModernTheme.PrimaryTextOnAccent,
            ModernToolbarButtonKind.Subtle => ModernTheme.AccentColor,
            _ => ModernTheme.TextColor,
        };
        var iconColor = textColor;

        using var backBrush = new SolidBrush(backColor);
        using var borderPen = new Pen(borderColor);
        e.Graphics.FillRoundedRectangle(backBrush, bounds, 8);
        if (Kind != ModernToolbarButtonKind.Primary || _hovered)
        {
            e.Graphics.DrawRoundedRectangle(borderPen, bounds, 8);
        }

        var iconBounds = new Rectangle(bounds.Left + 10, bounds.Top + (bounds.Height - 18) / 2, 18, 18);
        DrawToolbarIcon(e.Graphics, iconBounds, iconColor);
        var textBounds = new Rectangle(iconBounds.Right + 7, bounds.Top, Math.Max(1, bounds.Right - iconBounds.Right - 12), bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private Color GetBackColor()
    {
        if (Kind == ModernToolbarButtonKind.Primary)
        {
            return _pressed ? Color.FromArgb(29, 78, 216) : _hovered ? Color.FromArgb(30, 89, 220) : ModernTheme.AccentColor;
        }

        if (Kind == ModernToolbarButtonKind.Subtle)
        {
            return _pressed ? ModernTheme.AccentPressedColor : _hovered ? ModernTheme.AccentHoverColor : ModernTheme.SubtleSurfaceColor;
        }

        return _pressed ? ModernTheme.AccentPressedColor : _hovered ? ModernTheme.AccentHoverColor : ModernTheme.SurfaceColor;
    }

    private Color GetBorderColor()
    {
        if (Kind == ModernToolbarButtonKind.Primary)
        {
            return _hovered ? Color.FromArgb(29, 78, 216) : ModernTheme.AccentColor;
        }

        return _hovered ? Color.FromArgb(147, 197, 253) : ModernTheme.BorderSubtleColor;
    }

    private void DrawToolbarIcon(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.8F)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        using var brush = new SolidBrush(color);
        var cx = bounds.Left + bounds.Width / 2;
        var cy = bounds.Top + bounds.Height / 2;
        switch (Icon)
        {
            case ModernToolbarIcon.Import:
                graphics.DrawRectangle(pen, bounds.Left + 3, bounds.Top + 4, bounds.Width - 6, bounds.Height - 6);
                graphics.DrawLine(pen, cx, bounds.Top + 1, cx, bounds.Top + 11);
                graphics.DrawLine(pen, cx, bounds.Top + 11, cx - 4, bounds.Top + 7);
                graphics.DrawLine(pen, cx, bounds.Top + 11, cx + 4, bounds.Top + 7);
                break;
            case ModernToolbarIcon.Manage:
                graphics.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 4, bounds.Width - 4, bounds.Height - 8);
                graphics.DrawLine(pen, bounds.Left + 5, bounds.Top + 8, bounds.Right - 5, bounds.Top + 8);
                graphics.DrawLine(pen, bounds.Left + 5, bounds.Top + 12, bounds.Right - 5, bounds.Top + 12);
                break;
            case ModernToolbarIcon.Save:
                graphics.DrawRectangle(pen, bounds.Left + 3, bounds.Top + 2, bounds.Width - 6, bounds.Height - 4);
                graphics.DrawLine(pen, bounds.Left + 6, bounds.Top + 2, bounds.Left + 6, bounds.Top + 8);
                graphics.DrawRectangle(pen, bounds.Left + 6, bounds.Bottom - 7, bounds.Width - 12, 5);
                break;
            case ModernToolbarIcon.Remove:
                graphics.DrawLine(pen, bounds.Left + 5, bounds.Top + 5, bounds.Right - 5, bounds.Bottom - 5);
                graphics.DrawLine(pen, bounds.Right - 5, bounds.Top + 5, bounds.Left + 5, bounds.Bottom - 5);
                break;
            case ModernToolbarIcon.Update:
                graphics.DrawLine(pen, cx, bounds.Top + 2, cx, bounds.Bottom - 6);
                graphics.DrawLine(pen, cx, bounds.Bottom - 6, cx - 5, bounds.Bottom - 11);
                graphics.DrawLine(pen, cx, bounds.Bottom - 6, cx + 5, bounds.Bottom - 11);
                graphics.DrawLine(pen, bounds.Left + 4, bounds.Bottom - 3, bounds.Right - 4, bounds.Bottom - 3);
                break;
            case ModernToolbarIcon.Status:
                for (var index = 0; index < 3; index++)
                {
                    var y = bounds.Top + 4 + index * 5;
                    graphics.FillEllipse(brush, bounds.Left + 2, y, 3, 3);
                    graphics.DrawLine(pen, bounds.Left + 8, y + 1, bounds.Right - 2, y + 1);
                }
                break;
            case ModernToolbarIcon.Commit:
                graphics.DrawLine(pen, cx, bounds.Bottom - 3, cx, bounds.Top + 5);
                graphics.DrawLine(pen, cx, bounds.Top + 5, cx - 5, bounds.Top + 10);
                graphics.DrawLine(pen, cx, bounds.Top + 5, cx + 5, bounds.Top + 10);
                graphics.DrawLine(pen, bounds.Left + 4, bounds.Bottom - 3, bounds.Right - 4, bounds.Bottom - 3);
                break;
            case ModernToolbarIcon.Folder:
                graphics.DrawLine(pen, bounds.Left + 2, bounds.Top + 7, bounds.Left + 6, bounds.Top + 4);
                graphics.DrawLine(pen, bounds.Left + 6, bounds.Top + 4, bounds.Left + 10, bounds.Top + 4);
                graphics.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 7, bounds.Width - 4, bounds.Height - 8);
                break;
            case ModernToolbarIcon.More:
                graphics.FillEllipse(brush, cx - 6, cy - 1, 3, 3);
                graphics.FillEllipse(brush, cx - 1, cy - 1, 3, 3);
                graphics.FillEllipse(brush, cx + 4, cy - 1, 3, 3);
                break;
            case ModernToolbarIcon.Settings:
                graphics.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                graphics.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                break;
            case ModernToolbarIcon.History:
                graphics.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                graphics.DrawLine(pen, cx, cy, cx, cy - 5);
                graphics.DrawLine(pen, cx, cy, cx + 4, cy + 3);
                break;
        }
    }
}

internal sealed class ModernCardPanel : Panel
{
    public int CornerRadius { get; set; } = 8;
    public bool ShowShadow { get; set; } = true;

    public ModernCardPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Padding = new Padding(10);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? ModernTheme.AppBackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        bounds.Inflate(-2, -2);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (ShowShadow)
        {
            using var shadowBrush = new SolidBrush(ModernTheme.ShadowColor);
            var shadowBounds = bounds;
            shadowBounds.Offset(0, 2);
            e.Graphics.FillRoundedRectangle(shadowBrush, shadowBounds, CornerRadius);
        }

        using var surfaceBrush = new SolidBrush(ModernTheme.SurfaceColor);
        using var borderPen = new Pen(ModernTheme.BorderSubtleColor);
        e.Graphics.FillRoundedRectangle(surfaceBrush, bounds, CornerRadius);
        e.Graphics.DrawRoundedRectangle(borderPen, bounds, CornerRadius);
    }
}

internal sealed class ModernBadge : Control
{
    private string _badgeText = "";

    public string BadgeText
    {
        get => _badgeText;
        set
        {
            _badgeText = value ?? "";
            Invalidate();
        }
    }

    public Color BadgeBackColor { get; set; } = ModernTheme.AccentColor;
    public Color BadgeForeColor { get; set; } = ModernTheme.SurfaceColor;

    public ModernBadge()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        MinimumSize = new Size(24, 20);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(BadgeBackColor);
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        e.Graphics.FillRoundedRectangle(brush, bounds, Math.Min(bounds.Height / 2, 8));
        TextRenderer.DrawText(
            e.Graphics,
            BadgeText,
            Font,
            bounds,
            BadgeForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
