using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;

namespace winuitest;

public sealed partial class MainWindow
{
    private ScrollViewer? _trackListScroller;
    private bool _searchHasMore;
    private bool _searchPagingFailed;
    private bool _searchViewportCheckPending;
    private bool _trackPagingAttached;

    private bool CanAppendSearchResults => !_closed && !_inspectorOpen && _page == "search" && _query.Length > 0
        && _pageRequest is { IsCancellationRequested: false } && !_loadingMore
        && _searchHasMore && !_searchPagingFailed;

    private bool IsCurrentSearch(CancellationToken token, string query) => !_closed
        && !token.IsCancellationRequested && _page == "search" && _pageRequest?.Token == token
        && string.Equals(_query, query, StringComparison.Ordinal);

    private void ResetSearchPaging()
    {
        _searchHasMore = false;
        _searchPagingFailed = false;
        _searchViewportCheckPending = false;
        _loadingMore = false;
        _nextSearchOffset = 0;
        _searchTotal = 0;
        SetSearchAppendLoading(false);
    }

    private void SetSearchAppendLoading(bool loading)
    {
        SearchAppendProgress.IsIndeterminate = loading;
        SearchAppendProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrackList_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_trackPagingAttached)
        {
            TrackList.LayoutUpdated += TrackList_LayoutUpdated;
            _trackPagingAttached = true;
        }
        AttachTrackListScroller();
        UpdateTrackHeadingWidth();
        QueueSearchViewportCheck();
    }

    private void TrackList_Unloaded(object sender, RoutedEventArgs e)
    {
        TrackList.LayoutUpdated -= TrackList_LayoutUpdated;
        _trackPagingAttached = false;
        _searchViewportCheckPending = false;
        if (_trackListScroller is not null)
        {
            _trackListScroller.ViewChanged -= TrackList_ViewChanged;
            _trackListScroller = null;
        }
    }

    private void TrackList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTrackHeadingWidth();
        QueueSearchViewportCheck();
    }

    private void AttachTrackListScroller()
    {
        if (_trackListScroller is not null) return;
        _trackListScroller = FindTrackListScroller(TrackList);
        if (_trackListScroller is not null) _trackListScroller.ViewChanged += TrackList_ViewChanged;
    }

    private static ScrollViewer? FindTrackListScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindTrackListScroller(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void TrackList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Once append changes the extent, wait for layout rather than making a
        // second decision from the previous page's scroll metrics.
        if (!_searchViewportCheckPending) TryAppendSearchResults();
    }

    private void QueueSearchViewportCheck()
    {
        if (!CanAppendSearchResults) return;
        _searchViewportCheckPending = true;
        if (TrackList.IsLoaded) TrackList.InvalidateMeasure();
    }

    private void TrackList_LayoutUpdated(object? sender, object e)
    {
        if (!_searchViewportCheckPending) return;
        if (!CanAppendSearchResults)
        {
            _searchViewportCheckPending = false;
            return;
        }
        // The first search page is still behind the skeleton's exit animation.
        // Keep the pending check until the list has a real, visible viewport.
        if (_pageLoading || TracksView.Visibility != Visibility.Visible || TrackList.ActualHeight <= 1) return;
        AttachTrackListScroller();
        if (_trackListScroller is null || _trackListScroller.ViewportHeight <= 1) return;
        _searchViewportCheckPending = false;
        TryAppendSearchResults();
    }

    private void TryAppendSearchResults()
    {
        if (!CanAppendSearchResults || _pageLoading || !TrackList.IsLoaded
            || TracksView.Visibility != Visibility.Visible || _trackListScroller is null) return;
        var viewport = _trackListScroller.ViewportHeight;
        var remaining = _trackListScroller.ScrollableHeight - _trackListScroller.VerticalOffset;
        if (!double.IsFinite(viewport) || viewport <= 1 || !double.IsFinite(remaining)) return;
        // A small prefetch margin fills short pages, but stops as soon as there
        // is enough content below the viewport. It never walks the full result set.
        var threshold = Math.Clamp(viewport * 0.45, 96, 240);
        if (remaining <= threshold) _ = SearchAsync(_query, append: true);
    }
}
