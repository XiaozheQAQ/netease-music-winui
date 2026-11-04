using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using winuitest.Services;
using winuitest.Views.Controls;

namespace winuitest.Views;

public sealed partial class NowPlayingView
{
    private IReadOnlyList<LyricGap> _lyricGaps = Array.Empty<LyricGap>();
    private readonly Dictionary<int, GapRow> _gapRows = new();
    private GapRow? _activeGapRow;

    private void PrepareLyricGaps()
    {
        _lyricGaps = LyricGapTimeline.Build(_lines);
        _activeGapRow = null;
        _gapRows.Clear();
        foreach (var gap in _lyricGaps)
        {
            var dots = new Ellipse[3];
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            for (var index = 0; index < dots.Length; index++)
            {
                dots[index] = new Ellipse { Width = 12, Height = 12, Fill = _activeLineBrush, Opacity = 0 };
                group.Children.Add(dots[index]);
            }
            // The row always occupies the same space, even before/after its gap.
            // Only opacity changes during playback, so pause and seeking do not
            // remeasure the lyrics or move scroll anchors underneath the reader.
            var row = new Border
            {
                Name = "LyricGapRow" + gap.NextLineIndex,
                Height = 48,
                Padding = new Thickness(18, 8, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = group,
                Opacity = 0,
                IsHitTestVisible = false,
                Tag = gap
            };
            AutomationProperties.SetAccessibilityView(row, AccessibilityView.Raw);
            _gapRows.Add(gap.NextLineIndex, new GapRow(gap, row, dots));
        }
    }

    private void InsertLyricGapBefore(int lineIndex)
    {
        if (_gapRows.TryGetValue(lineIndex, out var gap)) LyricsStack.Children.Add(gap.Element);
    }

    private bool UpdateLyricGap(double timestampMs)
    {
        LyricGapProgress? state = null;
        if (double.IsFinite(timestampMs))
        {
            var ticks = Math.Clamp(timestampMs * TimeSpan.TicksPerMillisecond, long.MinValue, long.MaxValue);
            // The upper double bound rounds to 2^63; saturate before converting.
            var position = TimeSpan.FromTicks(ticks >= long.MaxValue ? long.MaxValue : ticks <= long.MinValue ? long.MinValue : (long)ticks);
            state = LyricGapTimeline.GetState(_lyricGaps, position);
        }
        var next = state.HasValue && _gapRows.TryGetValue(state.Value.Gap.NextLineIndex, out var found) ? found : null;
        var changed = !ReferenceEquals(next, _activeGapRow);
        if (changed)
        {
            if (_activeGapRow is not null) _activeGapRow.Element.Opacity = 0;
            _activeGapRow = next;
            _lyricBlurStateDirty = true;
        }
        if (next is null || state is null) return changed;

        var progress = state.Value.Progress;
        var duration = (double)next.Gap.EndMs - next.Gap.StartMs;
        // These fades also use source time. A paused clock freezes the exact
        // state; a seek updates all three dots directly without a queued animation.
        next.Element.Opacity = Math.Min(Math.Clamp(progress * duration / 180, 0, 1), Math.Clamp((1 - progress) * duration / 180, 0, 1));
        for (var index = 0; index < next.Dots.Length; index++)
        {
            var phase = Math.Clamp(progress * 3 - index, 0, 1);
            phase = phase * phase * (3 - 2 * phase);
            next.Dots[index].Opacity = 0.18 + phase * 0.82;
        }
        return changed;
    }

    private bool TryGetGapScrollTarget(out double target)
    {
        target = 0;
        if (_activeGapRow is not { } gap || gap.Element.ActualHeight <= 0) return false;
        var point = gap.Element.TransformToVisual(LyricsStack).TransformPoint(new Point());
        var previousIndex = gap.Gap.NextLineIndex - 1;
        var precedingSpan = 0d;
        if (previousIndex >= 0 && previousIndex < _lyricButtons.Count)
        {
            var previous = _lyricButtons[previousIndex];
            var previousPoint = previous.TransformToVisual(LyricsStack).TransformPoint(new Point());
            precedingSpan = Math.Max(previous.ActualHeight, point.Y - previousPoint.Y);
        }
        var anchor = LyricViewportLayout.CurrentLineAnchor(LyricsScroller.ViewportHeight,
            precedingSpan, gap.Element.ActualHeight, _lyricTopInset);
        target = point.Y + LyricsStack.Margin.Top - anchor;
        return true;
    }

    private sealed record GapRow(LyricGap Gap, Border Element, Ellipse[] Dots);
}
