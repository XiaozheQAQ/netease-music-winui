using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;

namespace winuitest;

public sealed partial class MainWindow
{
    private double _collectionHeaderReveal;
    private double _collectionExpandedOpacity = 1;
    private double _collectionRevealStart;
    private double _collectionRevealTarget;
    private long _collectionRevealClock;
    private bool _collectionRevealRendering;
    private bool _collectionMotionClosedAttached;
    private float _collectionAppliedOpacity = float.NaN;

    private void AnimateCollectionHeaderReveal(double target)
    {
        if (!_collectionMotionClosedAttached)
        {
            _collectionMotionClosedAttached = true;
            Closed += (_, _) => StopCollectionHeaderMotion();
        }
        _collectionRevealStart = _collectionHeaderReveal;
        _collectionRevealTarget = target;
        _collectionRevealClock = Stopwatch.GetTimestamp();
        if (!_motion.AnimationsEnabled || Math.Abs(target - _collectionRevealStart) < .001)
        {
            StopCollectionHeaderMotion();
            _collectionHeaderReveal = target;
            ApplyCollectionHeaderOpacity();
            UpdateCollectionHeaderClipping();
            return;
        }
        if (_collectionRevealRendering) return;
        _collectionRevealRendering = true;
        CompositionTarget.Rendering += CollectionHeader_Rendering;
    }

    private void CollectionHeader_Rendering(object? sender, object args)
    {
        if (_closed) { StopCollectionHeaderMotion(); return; }
        var progress = _motion.AnimationsEnabled
            ? Math.Clamp((Stopwatch.GetTimestamp() - _collectionRevealClock) / (Stopwatch.Frequency * .32), 0, 1) : 1;
        var eased = progress * progress * (3 - 2 * progress);
        _collectionHeaderReveal = _collectionRevealStart + (_collectionRevealTarget - _collectionRevealStart) * eased;
        ApplyCollectionHeaderOpacity();
        UpdateCollectionHeaderClipping();
        if (progress >= 1) StopCollectionHeaderMotion();
    }

    private void ApplyCollectionHeaderOpacity()
    {
        if (CollectionHeader is null) return;
        var opacity = (float)Math.Clamp(Math.Min(_collectionExpandedOpacity, 1 - _collectionHeaderReveal), 0, 1);
        if (Math.Abs(_collectionAppliedOpacity - opacity) < .001f) return;
        _collectionAppliedOpacity = opacity;
        ElementCompositionPreview.GetElementVisual(CollectionHeader).Opacity = opacity;
    }

    private void StopCollectionHeaderMotion()
    {
        if (!_collectionRevealRendering) return;
        CompositionTarget.Rendering -= CollectionHeader_Rendering;
        _collectionRevealRendering = false;
    }
}
