using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private bool _collectionHeaderCompact;
    private bool _resetCollectionScroll;
    private PlaylistDetail? _activePlaylist;
    private double _collectionWheelTarget = double.NaN;
    private long _collectionWheelTime;

    private void SetActivePlaylist(PlaylistDetail playlist) => _activePlaylist = playlist;

    private void ResetCollectionHeader()
    {
        _activePlaylist = null;
        _collectionHeaderCompact = false;
        _resetCollectionScroll = true;
        _collectionWheelTarget = double.NaN;
        _motion.Cancel(CompactCollectionHeader, false);
        TrackList.SelectedItems.Clear();
        _trackListScroller?.ChangeView(null, 0, null, true);
    }

    private void CollectionHeader_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateTrackHeadingWidth();
        UpdateCollectionHeader();
    }

    private void CompactCollectionHeader_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (args.Handled || _trackListScroller is null) return;
        var properties = args.GetCurrentPoint(CompactCollectionHeader).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0) return;
        // This fixed sibling does not bubble input through the ListView's scroller.
        // Respect the Windows wheel setting and accumulate rapid wheel ticks while
        // ChangeView is still animating toward the previous destination.
        if (!ReadCollectionWheelSettings(0x0068, 0, out var lines, 0)) lines = 3;
        var distance = lines == uint.MaxValue
            ? Math.Max(62, _trackListScroller.ViewportHeight - CompactCollectionHeader.ActualHeight)
            : Math.Min(lines, 100) * 16d;
        var now = Environment.TickCount64;
        var origin = double.IsFinite(_collectionWheelTarget) && now - _collectionWheelTime < 250
            ? _collectionWheelTarget : _trackListScroller.VerticalOffset;
        _collectionWheelTarget = Math.Clamp(origin - properties.MouseWheelDelta / 120d * distance,
            0, _trackListScroller.ScrollableHeight);
        _collectionWheelTime = now;
        _trackListScroller.ChangeView(null, _collectionWheelTarget, null, !_motion.AnimationsEnabled);
        args.Handled = true;
    }

    private void TrackItemsPanel_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        if (args.Handled || !_collectionHeaderCompact
            || !ReferenceEquals(FindTrackAncestor<ListView>(args.TargetElement), TrackList)) return;
        // The virtualizing panel receives this before ScrollViewer consumes it.
        // Extend the requested rectangle instead of starting a second scroll or
        // suppressing native keyboard focus/Shift-selection behavior.
        var padding = Math.Max(96, CompactCollectionHeader.ActualHeight);
        var rect = args.TargetRect;
        args.TargetRect = new Rect(rect.X, rect.Y - padding, rect.Width, rect.Height + padding);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadCollectionWheelSettings(uint action, uint parameter, out uint value, uint flags);

    private void UpdateCollectionHeader()
    {
        if (_trackListScroller is null || CollectionHeader.ActualHeight <= 0) return;
        if (_resetCollectionScroll)
        {
            _resetCollectionScroll = false;
            _trackListScroller.ChangeView(null, 0, null, true);
        }
        // The expanded heading is part of the virtualized ListView's header.
        // The fixed replacement overlays it without changing the viewport or
        // scroll extent, so entering/leaving compact mode never jumps the list.
        var threshold = Math.Max(24, CollectionHeader.ActualHeight
            + CollectionHeader.Margin.Top + CollectionHeader.Margin.Bottom - 56);
        var compact = _trackListScroller.VerticalOffset >= threshold;
        if (_collectionHeaderCompact == compact) return;
        _collectionHeaderCompact = compact;
        if (compact) _ = _motion.RevealAsync(CompactCollectionHeader, offset: -6, durationMs: 220);
        else _ = _motion.HideAsync(CompactCollectionHeader, offset: -6, durationMs: 180);
    }
}
