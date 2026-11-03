using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using winuitest.Views.Controls;

namespace winuitest.Views;

public sealed partial class NowPlayingView
{
    private CompositionEffectFactory? _lyricBlurFactory;
    private readonly HashSet<int> _blurredLines = new();
    private readonly List<(double Top, double Bottom)> _lyricBlurBounds = new();
    private bool _lyricBlurLayoutDirty = true;
    private bool _lyricBlurStateDirty = true;
    private bool _lyricBlurUnavailable;
    private double _lastBlurOffset = double.NaN;
    private long _lastBlurUpdate;

    private void InvalidateLyricBlurLayout() => _lyricBlurLayoutDirty = true;

    private void ShowAllLyricsClearly()
    {
        // No captures, rendering, dispatcher work or completion callbacks are
        // required to interrupt a blur. Native text returns in this input event.
        foreach (var index in _blurredLines)
            if (index < _lineVisuals.Count) _lineVisuals[index].BlurPresenter?.ShowClearImmediately();
        _lyricBlurStateDirty = true;
    }

    private void UpdateLyricBlur(bool force = false)
    {
        if (!IsLoaded || Visibility != Visibility.Visible || _manualScrollHeld || _lyricBlurUnavailable) return;
        var height = LyricsScroller.ViewportHeight;
        if (height <= 0 || _lineVisuals.Count == 0) return;
        var now = Stopwatch.GetTimestamp();
        var offset = LyricsScroller.VerticalOffset;
        if (!force && !_lyricBlurLayoutDirty && !_lyricBlurStateDirty)
        {
            if (Math.Abs(offset - _lastBlurOffset) < 12) return;
            if ((now - _lastBlurUpdate) / (double)Stopwatch.Frequency < 0.065) return;
        }

        try
        {
            if (_lyricBlurLayoutDirty || _lyricBlurBounds.Count != _lineVisuals.Count)
            {
                _lyricBlurBounds.Clear();
                foreach (var line in _lineVisuals)
                {
                    var presenter = line.BlurPresenter!;
                    var point = presenter.TransformToVisual(LyricsStack).TransformPoint(new Point());
                    var top = point.Y + LyricsStack.Margin.Top;
                    _lyricBlurBounds.Add((top, top + presenter.ActualHeight));
                }
                _lyricBlurLayoutDirty = false;
            }
            _lyricBlurFactory ??= CreateLyricBlurFactory();

            // Only viewport neighbours have live effect surfaces. Binary search
            // avoids walking the complete lyric list on each scrolling update.
            const double overscan = 160;
            var nearTop = offset - overscan;
            var nearBottom = offset + height + overscan;
            var low = 0;
            var high = _lyricBlurBounds.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_lyricBlurBounds[middle].Bottom < nearTop) low = middle + 1;
                else high = middle;
            }
            var retained = new HashSet<int>();
            for (var index = low; index < _lyricBlurBounds.Count && _lyricBlurBounds[index].Top <= nearBottom; index++)
            {
                var bounds = _lyricBlurBounds[index];
                if (bounds.Bottom <= bounds.Top) continue;
                var presenter = _lineVisuals[index].BlurPresenter!;
                // A modest blur keeps phrase shapes legible without competing
                // with the active text. All subtitles are in the same capture.
                var radius = index == _activeLine ? 0f : 1.8f;
                presenter.UpdateBlur(_lyricBlurFactory, radius, _animationsEnabled,
                    Math.Max(0, nearTop - bounds.Top), Math.Min(presenter.ActualHeight, nearBottom - bounds.Top));
                if (presenter.HasResources) retained.Add(index);
            }
            foreach (var index in _blurredLines)
                if (!retained.Contains(index)) _lineVisuals[index].BlurPresenter?.ReleaseResources();
            _blurredLines.Clear();
            foreach (var index in retained) _blurredLines.Add(index);
            _lastBlurOffset = offset;
            _lastBlurUpdate = now;
            _lyricBlurStateDirty = false;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            // Device/effect failures leave the real text visible, never a blank
            // or fabricated translucent substitute. Retry on the next opening.
            Debug.WriteLine($"Lyric blur unavailable: {exception.Message}");
            ReleaseLyricBlur();
            _lyricBlurUnavailable = true;
        }
    }

    private CompositionEffectFactory CreateLyricBlurFactory()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(LyricsStack).Compositor;
        using var effect = new GaussianBlurEffect
        {
            Name = "LyricBlur",
            BlurAmount = 0,
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Balanced,
            Source = new CompositionEffectSourceParameter("Text")
        };
        return compositor.CreateEffectFactory(effect, new[] { "LyricBlur.BlurAmount" });
    }

    private void ReleaseLyricBlur()
    {
        // Also cover a presenter whose resource creation failed before it was
        // added to the retained set, so its hidden source cannot remain hidden.
        foreach (var line in _lineVisuals) line.BlurPresenter?.ReleaseResources();
        _blurredLines.Clear();
        _lyricBlurBounds.Clear();
        _lyricBlurFactory?.Dispose();
        _lyricBlurFactory = null;
        _lyricBlurLayoutDirty = true;
        _lyricBlurStateDirty = true;
        _lyricBlurUnavailable = false;
        _lastBlurOffset = double.NaN;
        _lastBlurUpdate = 0;
    }
}
