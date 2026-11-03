using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace winuitest.Views;

public sealed partial class NowPlayingView
{
    private readonly Dictionary<UIElement, long> _lyricScaleDeadlines = new();

    private void AnimateLyricTravelScale()
    {
        if (!_animationsEnabled || _manualScrollHeld || !IsLoaded || Visibility != Visibility.Visible) return;
        var now = Stopwatch.GetTimestamp();
        // Only the phrases surrounding the destination participate. Scaling the
        // presenter leaves its capture subtree and the measured Button untouched.
        for (var index = Math.Max(0, _activeLine - 2); index < Math.Min(_lineVisuals.Count, _activeLine + 5); index++)
        {
            if (LineContent(_lineVisuals[index]) is not FrameworkElement element || !element.IsLoaded || element.ActualHeight <= 0) continue;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.CenterPoint = new Vector3(0, (float)element.ActualHeight * 0.35f, 0);
            using var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            using var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.25f, 1));
            scale.Duration = TimeSpan.FromMilliseconds(540);
            if (index == _activeLine)
            {
                // A newly focused phrase grows into place. An interruption uses
                // its current composed scale instead of restarting from 0.985.
                if (!_lyricScaleDeadlines.TryGetValue(element, out var deadline) || now >= deadline)
                    visual.Scale = new Vector3(0.985f, 0.985f, 1);
                scale.InsertExpressionKeyFrame(0, "this.StartingValue");
            }
            else
            {
                scale.InsertExpressionKeyFrame(0, "this.StartingValue");
                scale.InsertKeyFrame(0.18f, new Vector3(0.987f, 0.987f, 1), easing);
            }
            scale.InsertKeyFrame(1, Vector3.One, easing);
            visual.StartAnimation("Scale", scale);
            _lyricScaleDeadlines[element] = now + (long)(0.54 * Stopwatch.Frequency);
        }
    }

    private void ResetLyricScale(UIElement element)
    {
        if (!_lyricScaleDeadlines.Remove(element)) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        visual.CenterPoint = Vector3.Zero;
    }

    private void StopLyricTravelMotion()
    {
        // Direct manipulation cancels line movement/scale synchronously, without
        // touching the live word masks or their playback position.
        foreach (var element in _motionDeadlines.Keys.Where(element => element != ContentGrid).ToArray())
            StopMotion(element);
        foreach (var element in _lyricScaleDeadlines.Keys.ToArray()) ResetLyricScale(element);
    }
}
