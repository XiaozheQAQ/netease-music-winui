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

    public StartupView()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(LogoMotion, true);
    }

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
        logo.StopAnimation("Translation");
        logo.StopAnimation("Opacity");
        logo.Scale = Vector3.One;
        logo.Properties.InsertVector3("Translation", Vector3.Zero);
        logo.Opacity = 1;
    }

    private void UpdateAnimation()
    {
        ResetVisuals();
        var animate = _loaded && !_stopped && _settings.AnimationsEnabled;
        LoadingProgress.IsIndeterminate = animate;
        LoadingProgress.Value = animate ? 0 : 34;
        if (!animate) return;

        var logo = ElementCompositionPreview.GetElementVisual(LogoMotion);
        logo.CenterPoint = new Vector3(52, 52, 0);
        var compositor = logo.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));

        using var breathing = compositor.CreateVector3KeyFrameAnimation();
        breathing.Duration = TimeSpan.FromMilliseconds(2600);
        breathing.IterationBehavior = AnimationIterationBehavior.Forever;
        breathing.InsertKeyFrame(0, Vector3.One);
        breathing.InsertKeyFrame(0.5f, new Vector3(1.022f, 1.022f, 1), easing);
        breathing.InsertKeyFrame(1, Vector3.One, easing);
        logo.StartAnimation("Scale", breathing);

        using var floatMotion = compositor.CreateVector3KeyFrameAnimation();
        floatMotion.Duration = breathing.Duration;
        floatMotion.IterationBehavior = AnimationIterationBehavior.Forever;
        floatMotion.InsertKeyFrame(0, Vector3.Zero);
        floatMotion.InsertKeyFrame(0.5f, new Vector3(0, -2, 0), easing);
        floatMotion.InsertKeyFrame(1, Vector3.Zero, easing);
        logo.StartAnimation("Translation", floatMotion);
    }
}
