using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

public partial class Form1
{
    private void ConfigureTreeImages()
    {
        _treeImages.ColorDepth = ColorDepth.Depth32Bit;
        _treeImages.ImageSize = new Size(16, 16);
        _treeImages.Images.Clear();
        _treeImages.Images.Add("repo", CreateTreeIcon(Color.FromArgb(57, 99, 157), false));
        _treeImages.Images.Add("folder", CreateTreeIcon(Color.FromArgb(219, 164, 64), true));
        _treeImages.Images.Add("file", CreateTreeIcon(Color.FromArgb(118, 128, 140), false));
        _treeImages.Images.Add("xml", CreateTreeIcon(Color.FromArgb(39, 132, 85), false));
        _treeImages.Images.Add("lua", CreateTreeIcon(Color.FromArgb(72, 99, 180), false));
        _treeImages.Images.Add("changed", CreateTreeIcon(Color.FromArgb(209, 92, 56), false));
        _treeImages.Images.Add("ignored", CreateActionTreeIcon("I", Color.FromArgb(100, 116, 139)));
        _treeImages.Images.Add("action-added", CreateActionTreeIcon("A", Color.FromArgb(35, 134, 83)));
        _treeImages.Images.Add("action-modified", CreateActionTreeIcon("M", Color.FromArgb(184, 107, 25)));
        _treeImages.Images.Add("action-deleted", CreateActionTreeIcon("D", Color.FromArgb(184, 66, 66)));
        _treeImages.Images.Add("action-conflicted", CreateActionTreeIcon("C", Color.FromArgb(164, 62, 176)));
        _treeImages.Images.Add("action-replaced", CreateActionTreeIcon("R", Color.FromArgb(109, 85, 184)));
        _treeImages.Images.Add("action-unknown", CreateActionTreeIcon("?", Color.FromArgb(100, 116, 139)));
    }

    private static void ConfigureNavigationTree(TreeView tree)
    {
        ModernTreeViewRenderer.Configure(tree);
    }

    private static bool ToggleExpandableNode(TreeNode? node)
    {
        return ModernTreeViewRenderer.ToggleNode(node);
    }

    private static bool IsModernTreeArrowHit(TreeView tree, TreeNode? node, Point location)
    {
        return ModernTreeViewRenderer.IsArrowHit(tree, node, location);
    }


    private static Bitmap CreateTreeIcon(Color color, bool folder)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(ControlPaint.Dark(color), 1);
        if (folder)
        {
            graphics.FillRectangle(brush, 2, 5, 12, 8);
            graphics.FillRectangle(brush, 3, 3, 5, 3);
            graphics.DrawRectangle(pen, 2, 5, 12, 8);
        }
        else
        {
            graphics.FillRectangle(brush, 4, 2, 8, 12);
            graphics.DrawRectangle(pen, 4, 2, 8, 12);
            graphics.DrawLine(Pens.White, 6, 5, 10, 5);
            graphics.DrawLine(Pens.White, 6, 8, 10, 8);
        }

        return bitmap;
    }

    private static Bitmap CreateActionTreeIcon(string text, Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        graphics.FillRectangle(brush, 1, 2, 14, 12);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (16 - size.Width) / 2F, (16 - size.Height) / 2F - 0.5F);
        return bitmap;
    }


    private static Button CreateSmallToolbarButton(string text, Action action)
    {
        var button = CreateToolbarButtonBase(text);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button CreateSmallToolbarButton(string text, Func<Task> action)
    {
        var button = CreateToolbarButtonBase(text);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Button CreateToolbarButtonBase(string text)
    {
        return new ModernButton
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 6, 3),
        };
    }

    private static void ConfigureHistorySearchButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 6, 4);
    }

    internal static Control CreateChangedFilesFilterPanel(
        string title,
        TreeView tree,
        TextBox searchText,
        ComboBox filterCombo,
        Action applyFilter)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.White,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(241, 243, 245),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));

        searchText.Dock = DockStyle.Fill;
        searchText.Margin = new Padding(0, 4, 6, 4);
        searchText.PlaceholderText = "搜索文件名 / 路径";
        searchText.TextChanged += (_, _) => applyFilter();
        filters.Controls.Add(searchText, 0, 0);

        filterCombo.Dock = DockStyle.Fill;
        filterCombo.Margin = new Padding(0, 4, 0, 4);
        filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        filterCombo.Items.Clear();
        foreach (var text in ChangedFilesFilter.Options)
        {
            filterCombo.Items.Add(text);
        }

        filterCombo.SelectedIndex = 0;
        filterCombo.SelectedIndexChanged += (_, _) => applyFilter();
        filters.Controls.Add(filterCombo, 1, 0);

        panel.Controls.Add(filters, 0, 1);
        panel.Controls.Add(tree, 0, 2);
        return panel;
    }

    private static Control CreatePanelToolbar(string title, string buttonText, Func<Task> refresh)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(241, 243, 245),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 65, 81),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        }, 0, 0);
        var button = CreateSmallToolbarButton(buttonText, refresh);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

}

