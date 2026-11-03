using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

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
        var width = rowWidth ?? (_trackListScroller is { ViewportWidth: > 1 }
            ? _trackListScroller.ViewportWidth : TrackList.ActualWidth);
        if (double.IsFinite(width) && width > 1 &&
            (double.IsNaN(TrackHeader.Width) || Math.Abs(TrackHeader.Width - width) > 0.1))
            TrackHeader.Width = width;
    }
}
