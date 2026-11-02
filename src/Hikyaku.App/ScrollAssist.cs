using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Hikyaku.App;

/// <summary>Lets a nested list/editor hand wheel scrolling to its containing page at either boundary.</summary>
public static class ScrollAssist
{
    public static readonly DependencyProperty BubbleAtBoundaryProperty = DependencyProperty.RegisterAttached(
        "BubbleAtBoundary", typeof(bool), typeof(ScrollAssist), new PropertyMetadata(false, OnChanged));

    public static bool GetBubbleAtBoundary(DependencyObject element) => (bool)element.GetValue(BubbleAtBoundaryProperty);
    public static void SetBubbleAtBoundary(DependencyObject element, bool value) => element.SetValue(BubbleAtBoundaryProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ScrollViewer viewer) return;
        if ((bool)e.OldValue) viewer.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var viewer = (ScrollViewer)sender;
        // Only the innermost viewer participates. Value controls retain their normal wheel semantics.
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node))
            if (node is ScrollViewer or ComboBox or Slider or ScrollBar) return;
        if (CanScroll(viewer, e.Delta)) return;
        for (var node = Parent(viewer); node is not null; node = Parent(node))
        {
            if (node is not ScrollViewer outer || !CanScroll(outer, e.Delta)) continue;
            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            var notches = Math.Max(1, Math.Abs(e.Delta) / 120.0);
            if (lines < 0)
            {
                if (e.Delta < 0) outer.PageDown(); else outer.PageUp();
            }
            else
            {
                // Line commands also preserve logical item scrolling in virtualized lists.
                for (var i = 0; i < Math.Ceiling(lines * notches); i++)
                    if (e.Delta < 0) outer.LineDown(); else outer.LineUp();
            }
            e.Handled = true;
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) => viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
        viewer.ScrollableHeight > 0 && (delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight : viewer.VerticalOffset > 0);

    private static DependencyObject? Parent(DependencyObject node) => node is Visual || node is System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
