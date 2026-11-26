using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Sbzilla.Views;

/// <summary>
/// Puts a ListView's scroll bars outside its rows (as in sqlZilla's results grid): the built-in ones are hidden and
/// full-size <see cref="ScrollBar"/>s placed beside / below the list drive its ScrollViewer, so they never cover the last row or column.
/// </summary>
public static class ExternalScrollBars
{
    /// <param name="header">Optional column header kept in step with horizontal scrolling.</param>
    public static void Attach(ListView list, ScrollBar vertical, ScrollBar? horizontal = null, ScrollViewer? header = null)
    {
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        if (horizontal != null)
        {
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        }
        foreach (var bar in new[] { vertical, horizontal })
        {
            if (bar == null) continue;
            bar.Visibility = Visibility.Collapsed;
            bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            bar.IsTabStop = false;
            // Always the full-size bar, not the thin indicator that only widens on hover.
            void Expand() => VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false);
            bar.Loaded += (_, _) => Expand();
            bar.PointerExited += (_, _) => Expand();
            bar.PointerCaptureLost += (_, _) => Expand();
        }

        ScrollViewer? scroll = null;
        var syncing = false;
        void Sync()
        {
            if (scroll == null) return;
            syncing = true;
            Configure(vertical, scroll.ScrollableHeight, scroll.ViewportHeight, scroll.VerticalOffset);
            if (horizontal != null) Configure(horizontal, scroll.ScrollableWidth, scroll.ViewportWidth, scroll.HorizontalOffset);
            syncing = false;
        }

        list.Loaded += (_, _) =>
        {
            if (scroll != null || FindScrollViewer(list) is not { } found) return;
            scroll = found;
            scroll.ViewChanging += (_, e) => header?.ChangeView(e.NextView.HorizontalOffset, null, null, true);
            scroll.ViewChanged += (_, _) =>
            {
                header?.ChangeView(scroll.HorizontalOffset, null, null, true);
                Sync();
            };
            foreach (var dp in new[] { ScrollViewer.ScrollableWidthProperty, ScrollViewer.ScrollableHeightProperty, ScrollViewer.ViewportWidthProperty, ScrollViewer.ViewportHeightProperty })
                scroll.RegisterPropertyChangedCallback(dp, (_, _) => Sync());
            vertical.ValueChanged += (_, e) => { if (!syncing) scroll.ChangeView(null, e.NewValue, null, true); };
            if (horizontal != null) horizontal.ValueChanged += (_, e) => { if (!syncing) scroll.ChangeView(e.NewValue, null, null, true); };
            Sync();
        };
    }

    private static void Configure(ScrollBar bar, double scrollable, double viewport, double offset)
    {
        bar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        bar.Maximum = Math.Max(0, scrollable);
        bar.ViewportSize = viewport;
        bar.LargeChange = Math.Max(1, viewport * 0.9);
        bar.SmallChange = 30;
        bar.Value = offset;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
