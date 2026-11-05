using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Foundation;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private ScrollBar? _trackVerticalScrollBar;
    private ScrollContentPresenter? _trackContentPresenter;
    private RectangleGeometry? _trackContentOriginalClip;
    private readonly RectangleGeometry _collectionOverlayClip = new();
    private readonly RectangleGeometry _collectionContentClip = new();

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
        UpdateCollectionHeaderClipping();
    }

    private void UpdateCollectionHeaderClipping()
    {
        if (_closed || _trackListScroller is null || TrackList.ActualWidth <= 1) return;
        if (_trackVerticalScrollBar is not { IsLoaded: true })
            _trackVerticalScrollBar = FindCollectionScrollPart<ScrollBar>(_trackListScroller,
                bar => bar.Orientation == Orientation.Vertical);
        if (_trackContentPresenter is not { IsLoaded: true })
        {
            RestoreCollectionContentClip();
            _trackContentPresenter = FindCollectionScrollPart<ScrollContentPresenter>(_trackListScroller, _ => true);
            _trackContentOriginalClip = _trackContentPresenter?.Clip;
        }

        var width = CompactCollectionHeader.ActualWidth > 1
            ? CompactCollectionHeader.ActualWidth : CompactCollectionHeader.Width;
        if (!double.IsFinite(width) || width <= 1) return;
        var height = Math.Max(96, CompactCollectionHeader.ActualHeight);
        // Keep the shared row/heading widths intact. The overlay alone stops at
        // the native scrollbar, including its full mouse/arrow hit-test area.
        // TransformToVisual already reports DIPs, including across DPI changes.
        var gutterLeft = TrackList.TransformToVisual(CompactCollectionHeader)
            .TransformPoint(new Point(Math.Max(0, TrackList.ActualWidth - 16), 0)).X;
        if (_trackVerticalScrollBar is { ActualWidth: > 0, ActualHeight: > 0 } bar)
            gutterLeft = bar.TransformToVisual(CompactCollectionHeader).TransformPoint(new Point()).X;
        var overlayWidth = Math.Clamp(gutterLeft, 0, width);
        SetCollectionClipRect(_collectionOverlayClip, new Rect(0, 0, overlayWidth, height));
        if (!ReferenceEquals(CompactCollectionHeader.Clip, _collectionOverlayClip))
            CompactCollectionHeader.Clip = _collectionOverlayClip;

        if (!_collectionHeaderOccludesContent)
        {
            RestoreCollectionContentClip();
            return;
        }
        if (_trackContentPresenter is not { ActualWidth: > 1, ActualHeight: > 1 } presenter) return;
        // Cut only the scrolling content, not the ScrollViewer or its sibling
        // scrollbar. A transparent fixed header now reveals exactly the same
        // Mica/surface as the page, with no guessed active/inactive fallback color.
        var slot = LayoutInformation.GetLayoutSlot(CompactCollectionHeader);
        var contentTop = presenter.TransformToVisual(TracksView).TransformPoint(new Point()).Y;
        var top = Math.Clamp(slot.Y + CompactCollectionHeader.Margin.Top + height - contentTop, 0, presenter.ActualHeight)
            * _collectionHeaderReveal;
        var contentRect = new Rect(0, top, presenter.ActualWidth, Math.Max(0, presenter.ActualHeight - top));
        if (_trackContentOriginalClip is { } original) contentRect.Intersect(original.Rect);
        SetCollectionClipRect(_collectionContentClip, contentRect);
        if (!ReferenceEquals(presenter.Clip, _collectionContentClip)) presenter.Clip = _collectionContentClip;
    }

    private static void SetCollectionClipRect(RectangleGeometry geometry, Rect rect)
    {
        if (rect.IsEmpty) rect = new Rect();
        var previous = geometry.Rect;
        if (Math.Abs(previous.X - rect.X) > .1 || Math.Abs(previous.Y - rect.Y) > .1
            || Math.Abs(previous.Width - rect.Width) > .1 || Math.Abs(previous.Height - rect.Height) > .1)
            geometry.Rect = rect;
    }

    private void RestoreCollectionContentClip()
    {
        if (_trackContentPresenter is { } presenter && ReferenceEquals(presenter.Clip, _collectionContentClip))
            presenter.Clip = _trackContentOriginalClip;
    }

    private void ReleaseCollectionScrollParts()
    {
        RestoreCollectionContentClip();
        _trackContentPresenter = null;
        _trackContentOriginalClip = null;
        _trackVerticalScrollBar = null;
        if (ReferenceEquals(CompactCollectionHeader.Clip, _collectionOverlayClip)) CompactCollectionHeader.Clip = null;
    }

    private static T? FindCollectionScrollPart<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match)) return match;
            if (FindCollectionScrollPart(child, predicate) is { } found) return found;
        }
        return null;
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
