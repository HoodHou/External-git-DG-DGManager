namespace SVNManager;

internal static class ModernTreeViewRenderer
{
    public static void Configure(TreeView tree)
    {
        WinFormsRendering.EnableDoubleBuffering(tree);
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.ShowLines = false;
        tree.ShowRootLines = false;
        tree.ShowPlusMinus = false;
        tree.HotTracking = false;
        tree.ItemHeight = 28;
        tree.BorderStyle = BorderStyle.None;
        tree.BackColor = ModernTheme.SurfaceColor;
        tree.ForeColor = Color.FromArgb(35, 43, 51);
        tree.LineColor = ModernTheme.BorderSubtleColor;
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.DrawNode -= DrawNode;
        tree.DrawNode += DrawNode;
        tree.MouseDown -= ToggleNodeFromMouseDown;
        tree.MouseDown += ToggleNodeFromMouseDown;
        tree.AfterExpand -= InvalidateNodeRow;
        tree.AfterExpand += InvalidateNodeRow;
        tree.AfterCollapse -= InvalidateNodeRow;
        tree.AfterCollapse += InvalidateNodeRow;
    }

    public static bool ToggleNode(TreeNode? node)
    {
        if (node == null || node.Nodes.Count == 0)
        {
            return false;
        }

        if (node.IsExpanded)
        {
            node.Collapse();
        }
        else
        {
            node.Expand();
        }

        if (node.TreeView != null)
        {
            WinFormsRendering.InvalidateTreeNodeRow(node.TreeView, node);
        }

        return true;
    }

    public static bool IsArrowHit(TreeView tree, TreeNode? node, Point location)
    {
        return node != null &&
            node.Nodes.Count > 0 &&
            GetArrowBounds(tree, node).Contains(location);
    }

    private static void ToggleNodeFromMouseDown(object? sender, MouseEventArgs args)
    {
        if (sender is not TreeView tree || args.Button != MouseButtons.Left || args.Clicks > 1)
        {
            return;
        }

        var node = tree.GetNodeAt(args.Location);
        if (!IsArrowHit(tree, node, args.Location))
        {
            return;
        }

        tree.SelectedNode = node;
        ToggleNode(node);
    }

    private static void InvalidateNodeRow(object? sender, TreeViewEventArgs args)
    {
        if (sender is TreeView tree && args.Node != null)
        {
            WinFormsRendering.InvalidateTreeNodeRow(tree, args.Node);
        }
    }

    private static Rectangle GetArrowBounds(TreeView tree, TreeNode node)
    {
        var top = node.Bounds.Top > 0 ? node.Bounds.Top : 0;
        var left = Math.Max(2, 8 + node.Level * 18);
        return new Rectangle(left, top, 32, Math.Max(22, tree.ItemHeight));
    }

    private static void DrawNode(object? sender, DrawTreeNodeEventArgs args)
    {
        if (sender is not TreeView tree || args.Node == null)
        {
            return;
        }

        args.DrawDefault = false;
        var graphics = args.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var fullBounds = new Rectangle(4, args.Bounds.Top + 2, Math.Max(1, tree.ClientSize.Width - 8), tree.ItemHeight - 4);
        var selected = (args.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var markedSelected = args.Node.BackColor != Color.Empty && args.Node.BackColor != tree.BackColor;
        var backgroundColor = selected
            ? ModernTheme.AccentPressedColor
            : markedSelected ? args.Node.BackColor : tree.BackColor;
        using var backgroundBrush = new SolidBrush(backgroundColor);
        graphics.FillRoundedRectangle(backgroundBrush, fullBounds, 7);

        var x = 10 + args.Node.Level * 18;
        var arrowRect = GetArrowBounds(tree, args.Node);
        if (args.Node.Nodes.Count > 0)
        {
            using var arrowFont = new Font("Segoe UI", 7F, FontStyle.Regular);
            TextRenderer.DrawText(
                graphics,
                args.Node.IsExpanded ? "▼" : "▶",
                arrowFont,
                arrowRect,
                ModernTheme.MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        x += 18;
        var imageKey = args.Node.IsSelected ? args.Node.SelectedImageKey : args.Node.ImageKey;
        if (tree.ImageList != null && !string.IsNullOrWhiteSpace(imageKey) && tree.ImageList.Images.ContainsKey(imageKey))
        {
            var image = tree.ImageList.Images[imageKey];
            if (image != null)
            {
                graphics.DrawImage(image, new Rectangle(x, args.Bounds.Top + 6, 16, 16));
                x += 22;
            }
        }

        var font = args.Node.NodeFont ?? tree.Font;
        var color = selected ? Color.FromArgb(15, 76, 129) : args.Node.ForeColor == Color.Empty ? tree.ForeColor : args.Node.ForeColor;
        var textBounds = new Rectangle(x, args.Bounds.Top + 1, Math.Max(1, tree.ClientSize.Width - x - 8), tree.ItemHeight - 2);
        TextRenderer.DrawText(
            graphics,
            args.Node.Text,
            font,
            textBounds,
            color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}
