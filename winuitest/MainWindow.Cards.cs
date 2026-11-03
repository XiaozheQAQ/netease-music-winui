using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly Dictionary<GridView, PlaylistCardViewport> _playlistCardViewports = new();
    private bool _playlistCardLifecycleAttached;

    private sealed record PlaylistCardViewport(ItemsWrapGrid Panel, ScrollViewer? Scroller, long ViewportCallbackToken);

    private void PlaylistCards_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not GridView grid) return;
        ResizePlaylistCards(grid);
        // A GridView template can finish its first measure after Loaded.
        grid.DispatcherQueue.TryEnqueue(() => { if (!_closed && grid.IsLoaded) ResizePlaylistCards(grid); });
    }

    private void PlaylistCards_SizeChanged(object sender, SizeChangedEventArgs args) => ResizePlaylistCards(sender as GridView);

    private void ResizePlaylistCards(GridView? grid)
    {
        if (_closed || grid?.ItemsPanelRoot is not ItemsWrapGrid wrap || grid.ActualWidth <= 1) return;
        var scroller = CardAncestor<ScrollViewer>(wrap);
        AttachPlaylistCardViewport(grid, wrap, scroller);

        // The GridView's outer size can exceed its internal scrolling viewport,
        // especially with alignment margins and fractional display scaling.
        var width = scroller is { ViewportWidth: > 1 } ? scroller.ViewportWidth
            : scroller is { ActualWidth: > 1 } ? scroller.ActualWidth
            : grid.ActualWidth - grid.BorderThickness.Left - grid.BorderThickness.Right;
        var padding = CardAncestor<ItemsPresenter>(wrap)?.Padding ?? grid.Padding;
        width -= padding.Left + padding.Right;
        if (!double.IsFinite(width) || width <= 1) return;

        var columns = Math.Clamp((int)(width / 198), 1, 6);
        var scale = grid.XamlRoot?.RasterizationScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        // A DIP-floor can still round UP to a physical pixel for every cell,
        // pushing the final card onto the next row. Allocate whole physical
        // pixels and leave one physical pixel per cell for native rounding.
        var cellPixels = Math.Max(1, Math.Floor(Math.Floor(width * scale) / columns) - 1);
        var cellWidth = cellPixels / scale;
        if (wrap.MaximumRowsOrColumns != columns) wrap.MaximumRowsOrColumns = columns;
        if (!double.IsFinite(wrap.ItemWidth) || Math.Abs(wrap.ItemWidth - cellWidth) > 0.01) wrap.ItemWidth = cellWidth;
    }

    private static T? CardAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private void AttachPlaylistCardViewport(GridView grid, ItemsWrapGrid wrap, ScrollViewer? scroller)
    {
        if (_playlistCardViewports.TryGetValue(grid, out var previous))
        {
            if (ReferenceEquals(previous.Panel, wrap) && ReferenceEquals(previous.Scroller, scroller)) return;
            DetachPlaylistCardViewport(grid);
        }
        var token = scroller?.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty,
            (_, _) => { if (!_closed && grid.IsLoaded) ResizePlaylistCards(grid); }) ?? 0;
        _playlistCardViewports.Add(grid, new PlaylistCardViewport(wrap, scroller, token));
        grid.Unloaded += PlaylistCards_Unloaded;
        wrap.SizeChanged += PlaylistCardViewport_SizeChanged;
        if (scroller is not null) scroller.SizeChanged += PlaylistCardViewport_SizeChanged;
        if (!_playlistCardLifecycleAttached)
        {
            Closed += PlaylistCards_WindowClosed;
            _playlistCardLifecycleAttached = true;
        }
    }

    private void PlaylistCardViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        foreach (var pair in new List<KeyValuePair<GridView, PlaylistCardViewport>>(_playlistCardViewports))
            if (ReferenceEquals(sender, pair.Value.Panel) || ReferenceEquals(sender, pair.Value.Scroller)) ResizePlaylistCards(pair.Key);
    }

    private void PlaylistCards_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is GridView grid) DetachPlaylistCardViewport(grid);
    }

    private void DetachPlaylistCardViewport(GridView grid)
    {
        if (!_playlistCardViewports.Remove(grid, out var viewport)) return;
        grid.Unloaded -= PlaylistCards_Unloaded;
        viewport.Panel.SizeChanged -= PlaylistCardViewport_SizeChanged;
        if (viewport.Scroller is not null)
        {
            viewport.Scroller.SizeChanged -= PlaylistCardViewport_SizeChanged;
            viewport.Scroller.UnregisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, viewport.ViewportCallbackToken);
        }
    }

    private void PlaylistCards_WindowClosed(object sender, WindowEventArgs args)
    {
        foreach (var grid in new List<GridView>(_playlistCardViewports.Keys)) DetachPlaylistCardViewport(grid);
        Closed -= PlaylistCards_WindowClosed;
        _playlistCardLifecycleAttached = false;
    }
}
