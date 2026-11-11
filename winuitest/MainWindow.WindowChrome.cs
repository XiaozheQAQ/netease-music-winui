using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;

namespace winuitest;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _windowChromeTimer;
    private XamlRoot? _windowChromeRoot;
    private UIElement? _windowChromeDragRegion;
    private bool _windowChromeAttached;
    private bool _windowChromeRefreshing;
    private bool? _windowChromeFullScreen;
    private double _windowedCaptionInset = 138;
    private double _appliedCaptionInset = double.NaN;

    private void UpdateCaptionTheme()
    {
        if (_closed) return;
        var titleBar = AppWindow.TitleBar;
        if (IsAccentHighContrast)
        {
            // Let Windows use the user's contrast palette for every native state.
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            return;
        }

        var immersive = NowPlaying.Visibility == Visibility.Visible;
        var darkSurface = immersive || Root.ActualTheme == ElementTheme.Dark;
        var foreground = immersive ? Colors.White : ((SolidColorBrush)ThemeBrush("LumaTextPrimaryBrush")).Color;
        var mutedForeground = immersive ? Windows.UI.Color.FromArgb(255, 190, 190, 190)
            : ((SolidColorBrush)ThemeBrush("LumaTextTertiaryBrush")).Color;
        var feedbackChannel = darkSurface ? (byte)255 : (byte)0;
        // Extended native caption buttons support background alpha. Tint the
        // actual shell/artwork underneath, with a stronger response on press.
        // Only idle states are fully transparent; native hit testing and Snap stay.
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(
            immersive ? (byte)40 : darkSurface ? (byte)32 : (byte)20,
            feedbackChannel, feedbackChannel, feedbackChannel);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(
            immersive ? (byte)64 : darkSurface ? (byte)48 : (byte)36,
            feedbackChannel, feedbackChannel, feedbackChannel);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = mutedForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        // Windows deliberately retains its red Close-button hover/press states.
    }

    private void InitializeWindowChrome()
    {
        if (_windowChromeAttached) return;
        _windowChromeAttached = true;
        _windowChromeTimer = DispatcherQueue.CreateTimer();
        _windowChromeTimer.IsRepeating = false;
        _windowChromeTimer.Interval = TimeSpan.FromMilliseconds(60);
        _windowChromeTimer.Tick += WindowChrome_TimerTick;
        AppWindow.Changed += WindowChrome_AppWindowChanged;
        Activated += WindowChrome_Activated;
        Root.Loaded += WindowChrome_RootLoaded;
        AttachWindowChromeRoot();
        RefreshWindowChrome();
    }

    private void WindowChrome_RootLoaded(object sender, RoutedEventArgs args)
    {
        AttachWindowChromeRoot();
        RefreshWindowChrome();
    }

    private void AttachWindowChromeRoot()
    {
        if (ReferenceEquals(_windowChromeRoot, Root.XamlRoot)) return;
        if (_windowChromeRoot is not null) _windowChromeRoot.Changed -= WindowChrome_XamlRootChanged;
        _windowChromeRoot = Root.XamlRoot;
        if (_windowChromeRoot is not null) _windowChromeRoot.Changed += WindowChrome_XamlRootChanged;
    }

    private void WindowChrome_XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshWindowChrome();

    private void WindowChrome_AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Restore by dragging, Snap and DPI changes do not necessarily change
        // the presenter kind. Position and size changes matter as well.
        if (args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange)
            RefreshWindowChrome();
        if (args.DidPresenterChange) UpdateCaptionTheme();
    }

    private void WindowChrome_Activated(object sender, WindowActivatedEventArgs args) => RefreshWindowChrome();

    private void WindowChrome_TimerTick(DispatcherQueueTimer sender, object args) => ApplyWindowChrome();

    private void RefreshWindowChrome()
    {
        if (_closed || !_windowChromeAttached) return;
        ApplyWindowChrome();
        // AppWindow.Changed can precede the new native title-bar metrics.
        // Keep a safe inset immediately, then read the settled metrics once.
        _windowChromeTimer?.Stop();
        _windowChromeTimer?.Start();
    }

    private void ApplyWindowChrome()
    {
        if (_closed || _windowChromeRefreshing) return;
        _windowChromeRefreshing = true;
        try
        {
            var fullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            _lyricsFullScreen = fullScreen;
            if (_windowChromeFullScreen != fullScreen)
            {
                _windowChromeFullScreen = fullScreen;
                NowPlaying.SetFullScreen(fullScreen);
            }

            UIElement? dragRegion = fullScreen ? null : !_startupReady ? StartupDragRegion
                : _immersiveOpen ? NowPlaying.TitleBarRegion : DragRegion;
            if (!ReferenceEquals(_windowChromeDragRegion, dragRegion))
            {
                _windowChromeDragRegion = dragRegion;
                SetTitleBar(dragRegion);
            }

            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var scale = dpi > 0 ? dpi / 96d : Root.XamlRoot?.RasterizationScale ?? 1;
            var reportedInset = AppWindow.TitleBar.RightInset / Math.Max(1, scale);
            if (!fullScreen && double.IsFinite(reportedInset) && reportedInset >= 120)
                _windowedCaptionInset = reportedInset;
            // The three native caption buttons must retain space while Windows
            // briefly reports zero during maximize/drag-restore/fullscreen exit.
            // Only a real FullScreen presenter removes that reservation.
            var inset = fullScreen ? 0 : Math.Max(138, _windowedCaptionInset);
            if (!double.IsFinite(_appliedCaptionInset) || Math.Abs(_appliedCaptionInset - inset) > .1)
            {
                _appliedCaptionInset = inset;
                NowPlaying.SetCaptionInset(inset);
            }
        }
        finally { _windowChromeRefreshing = false; }
    }

    private void DisposeWindowChrome()
    {
        if (!_windowChromeAttached) return;
        _windowChromeAttached = false;
        AppWindow.Changed -= WindowChrome_AppWindowChanged;
        Activated -= WindowChrome_Activated;
        Root.Loaded -= WindowChrome_RootLoaded;
        if (_windowChromeRoot is not null) _windowChromeRoot.Changed -= WindowChrome_XamlRootChanged;
        _windowChromeRoot = null;
        if (_windowChromeTimer is not null)
        {
            _windowChromeTimer.Stop();
            _windowChromeTimer.Tick -= WindowChrome_TimerTick;
            _windowChromeTimer = null;
        }
    }
}
