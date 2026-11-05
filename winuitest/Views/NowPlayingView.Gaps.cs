using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        HideActiveLyricGap();
        _lyricGaps = LyricGapTimeline.Build(_lines);
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
            var row = new Border
            {
                Name = "LyricGapRow" + gap.NextLineIndex,
                Height = 0,
                Visibility = Visibility.Collapsed,
                Clip = new RectangleGeometry(),
                Padding = new Thickness(18, 0, 0, 0),
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

    private void AddLyricButton(int lineIndex, Button button)
    {
        if (!_gapRows.TryGetValue(lineIndex, out var gap))
        {
            LyricsStack.Children.Add(button);
            return;
        }
        // One outer StackPanel child per sung phrase keeps normal line spacing
        // intact even at the very first/last pixel of the countdown transition.
        var phrase = new StackPanel();
        phrase.Children.Add(gap.Element);
        phrase.Children.Add(button);
        LyricsStack.Children.Add(phrase);
    }

    private bool HideActiveLyricGap()
    {
        if (_activeGapRow is null) return false;
        _activeGapRow.Element.Opacity = 0;
        _activeGapRow.Element.Height = 0;
        _activeGapRow.Element.Visibility = Visibility.Collapsed;
        _activeGapRow = null;
        InvalidateLyricBlurLayout();
        return true;
    }

    private bool UpdateLyricGap(double timestampMs, out bool resized)
    {
        resized = false;
        LyricGapProgress? state = null;
        if (_lyricGaps.Count > 0 && IsLoaded && Visibility == Visibility.Visible && double.IsFinite(timestampMs))
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
            resized = HideActiveLyricGap();
            _activeGapRow = next;
            _lyricBlurStateDirty = true;
        }
        if (next is null || state is null) return changed;

        var progress = state.Value.Progress;
        var duration = (double)next.Gap.EndMs - next.Gap.StartMs;
        var distanceFromEdge = Math.Min(progress, 1 - progress) * duration;
        // Start easing the space closed while the last dots fade. At the next
        // phrase's timestamp its actual position already excludes the countdown.
        // Both directions follow source time, including during scrubbing or pause.
        var fraction = _animationsEnabled ? Math.Clamp(distanceFromEdge / 320, 0, 1) : 1;
        var height = 48 * fraction * fraction * (3 - 2 * fraction);
        if (next.Element.Height != height)
        {
            next.Element.Height = height;
            ((RectangleGeometry)next.Element.Clip).Rect = new Rect(0, 0, 96, height);
            next.Element.Visibility = height > 0 ? Visibility.Visible : Visibility.Collapsed;
            resized = true;
            if (height == 48 || changed) InvalidateLyricBlurLayout();
        }
        next.Element.Opacity = Math.Clamp(distanceFromEdge / 180, 0, 1);
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
        if (_activeGapRow is not { } gap) return false;
        var next = _lyricButtons[gap.Gap.NextLineIndex];
        if (next.ActualHeight <= 0) return false;
        // The row itself can be collapsed on the exact starting frame. Use
        // the adjacent measured button as a stable anchor at zero height too.
        var top = next.TransformToVisual(LyricsStack).TransformPoint(new Point()).Y - gap.Element.Height;
        var previousIndex = gap.Gap.NextLineIndex - 1;
        var precedingSpan = 0d;
        if (previousIndex >= 0 && previousIndex < _lyricButtons.Count)
        {
            var previous = _lyricButtons[previousIndex];
            var previousPoint = previous.TransformToVisual(LyricsStack).TransformPoint(new Point());
            precedingSpan = Math.Max(previous.ActualHeight, top - previousPoint.Y);
        }
        var anchor = LyricViewportLayout.CurrentLineAnchor(LyricsScroller.ViewportHeight,
            precedingSpan, gap.Element.Height, _lyricTopInset);
        target = top + LyricsStack.Margin.Top - anchor;
        return true;
    }

    private sealed record GapRow(LyricGap Gap, Border Element, Ellipse[] Dots);
}
