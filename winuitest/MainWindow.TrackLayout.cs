using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private void TrackRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not FrameworkElement row || row.ActualWidth <= 1) return;
        for (var parent = VisualTreeHelper.GetParent(row); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ListView list) continue;
            if (ReferenceEquals(list, TrackList)) UpdateTrackHeadingWidth(row.ActualWidth);
            break;
        }
    }

    private void UpdateTrackHeadingWidth(double? rowWidth = null)
    {
        if (TrackHeader is null || TrackList is null) return;
        // Native item presenters reserve a small inner border. Use a realized
        // row's content width so a later resize/layout pass cannot replace it
        // with the slightly larger ScrollViewer viewport and drift the columns.
        var width = rowWidth ?? GetRealizedTrackRowWidth(TrackList.ItemsPanelRoot)
            ?? (_trackListScroller is { ViewportWidth: > 1 }
            ? _trackListScroller.ViewportWidth : TrackList.ActualWidth);
        if (double.IsFinite(width) && width > 1 &&
            (double.IsNaN(TrackHeader.Width) || Math.Abs(TrackHeader.Width - width) > 0.1))
        {
            TrackHeader.Width = width;
            StickyTrackHeader.Width = width;
            CompactCollectionHeader.Width = width;
            CollectionScrollHeader.Width = width;
            CompactCollectionHeader.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private static double? GetRealizedTrackRowWidth(DependencyObject? parent)
    {
        if (parent is null) return null;
        if (parent is TrackColumnsGrid { Tag: Track, ActualWidth: > 1 } row) return row.ActualWidth;
        // Walk only the small realized visual subtree, never the Items source.
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (GetRealizedTrackRowWidth(VisualTreeHelper.GetChild(parent, index)) is { } width) return width;
        return null;
    }
}
