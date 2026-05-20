using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

internal sealed class LazyFileTreePlaceholder
{
    public static LazyFileTreePlaceholder Instance { get; } = new();

    private LazyFileTreePlaceholder()
    {
    }
}

internal static class WinFormsRendering
{
    private const int WmSetRedraw = 0x000B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void EnableDoubleBuffering(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(control, true);
    }

    public static void SetRedraw(Control control, bool enabled)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        SendMessage(control.Handle, WmSetRedraw, enabled ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        if (enabled)
        {
            control.Invalidate(true);
        }
    }

    public static void InvalidateTreeNodeRow(TreeView tree, TreeNode node)
    {
        if (!tree.IsHandleCreated)
        {
            return;
        }

        var bounds = node.Bounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        tree.Invalidate(new Rectangle(0, Math.Max(0, bounds.Top - 2), tree.ClientSize.Width, Math.Max(tree.ItemHeight + 4, bounds.Height + 4)));
    }

    public static void InvalidateListViewItems(ListView list, params ListViewItem?[] items)
    {
        if (!list.IsHandleCreated)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var bounds = item.Bounds;
            if (!bounds.IsEmpty)
            {
                list.Invalidate(new Rectangle(0, Math.Max(0, bounds.Top - 2), list.ClientSize.Width, bounds.Height + 4));
            }
        }
    }
}

