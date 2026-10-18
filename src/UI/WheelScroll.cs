using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ACRLiveTiming.UI;

/// <summary>
/// The mouse wheel over a text input that cannot scroll that way (a single-line
/// box, or a multi-line one already at its end) scrolls the panel around it. WPF's
/// own behaviour is to swallow the wheel in the input's inner scroll viewer, so
/// the panel stops scrolling whenever the pointer crosses a field.
/// </summary>
internal static class WheelScroll
{
    private const double PixelsPerNotch = 48;   // three 16 px lines, like a ScrollViewer

    public static void Attach(UIElement root)
        => root.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source) return;
        ScrollViewer? inner = null;
        for (var d = source; d != null; d = Parent(d))
        {
            if (d is not ScrollViewer viewer) continue;
            if (inner == null)
            {
                // the first viewer up the tree takes the wheel: leave it alone unless
                // it is a text input's own viewer that cannot move this way
                if (viewer.TemplatedParent is not (TextBoxBase or PasswordBox) || CanScroll(viewer, e.Delta)) return;
                inner = viewer;
                continue;
            }
            if (!CanScroll(viewer, e.Delta)) continue;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120.0 * PixelsPerNotch);
            e.Handled = true;
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
        => delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;

    private static DependencyObject? Parent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
