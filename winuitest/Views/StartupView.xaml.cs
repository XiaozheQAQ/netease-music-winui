using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace winuitest.Views;

/// <summary>A first-frame brand surface; startup readiness belongs to the window.</summary>
public sealed partial class StartupView : UserControl
{
    private readonly UISettings _settings = new();
    private bool _loaded;
    private bool _subscribed;
    private bool _stopped;

    public StartupView() => InitializeComponent();

    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        if (_stopped) return;
        _loaded = true;
        if (!_subscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged += AnimationsEnabledChanged;
            _subscribed = true;
        }
        UpdateAnimation();
    }

    private void View_Unloaded(object sender, RoutedEventArgs args) => Deactivate();

    /// <summary>Stops the startup animation and releases preference subscriptions.</summary>
    public void Stop()
    {
        _stopped = true;
        Deactivate();
    }

    private void Deactivate()
    {
        _loaded = false;
        if (_subscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
            _subscribed = false;
        }
        ResetVisuals();
        LoadingProgress.IsIndeterminate = false;
    }

    private void AnimationsEnabledChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_loaded && !_stopped) UpdateAnimation();
    });

    private void ResetVisuals()
    {
        var logo = ElementCompositionPreview.GetElementVisual(LogoMotion);
        logo.StopAnimation("Scale");
        logo.Scale = Vector3.One;
        var halo = ElementCompositionPreview.GetElementVisual(LogoHalo);
        halo.StopAnimation("Opacity");
        halo.StopAnimation("Scale");
        halo.Scale = Vector3.One;
        halo.Opacity = 0.08f;
    }

    private void UpdateAnimation()
    {
        ResetVisuals();
        var animate = _loaded && !_stopped && _settings.AnimationsEnabled;
        LoadingProgress.IsIndeterminate = animate;
        LoadingProgress.Value = animate ? 0 : 34;
        if (!animate) return;

        var logo = ElementCompositionPreview.GetElementVisual(LogoMotion);
        var halo = ElementCompositionPreview.GetElementVisual(LogoHalo);
        logo.CenterPoint = new Vector3(52, 52, 0);
        halo.CenterPoint = new Vector3(74, 74, 0);
        var compositor = logo.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));

        using var breathing = compositor.CreateVector3KeyFrameAnimation();
        breathing.Duration = TimeSpan.FromMilliseconds(2800);
        breathing.IterationBehavior = AnimationIterationBehavior.Forever;
        breathing.InsertKeyFrame(0, Vector3.One);
        breathing.InsertKeyFrame(0.5f, new Vector3(1.035f, 1.035f, 1), easing);
        breathing.InsertKeyFrame(1, Vector3.One, easing);
        logo.StartAnimation("Scale", breathing);

        using var glow = compositor.CreateScalarKeyFrameAnimation();
        glow.Duration = breathing.Duration;
        glow.IterationBehavior = AnimationIterationBehavior.Forever;
        glow.InsertKeyFrame(0, 0.08f);
        glow.InsertKeyFrame(0.5f, 0.16f, easing);
        glow.InsertKeyFrame(1, 0.08f, easing);
        halo.StartAnimation("Opacity", glow);

        using var expansion = compositor.CreateVector3KeyFrameAnimation();
        expansion.Duration = breathing.Duration;
        expansion.IterationBehavior = AnimationIterationBehavior.Forever;
        expansion.InsertKeyFrame(0, Vector3.One);
        expansion.InsertKeyFrame(0.5f, new Vector3(1.07f, 1.07f, 1), easing);
        expansion.InsertKeyFrame(1, Vector3.One, easing);
        halo.StartAnimation("Scale", expansion);
    }
}
