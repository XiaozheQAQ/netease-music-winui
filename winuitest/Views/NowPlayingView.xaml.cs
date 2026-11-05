using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using winuitest.Models;
using winuitest.Controls;
using winuitest.Views.Controls;
using winuitest.Services;

namespace winuitest.Views;

public sealed partial class NowPlayingView : UserControl
{
    private readonly List<Button> _lyricButtons = new();
    private readonly List<LineVisual> _lineVisuals = new();
    private readonly SolidColorBrush _mutedLyricBrush = new();
    private readonly SolidColorBrush _activeLineBrush = new();
    private readonly SolidColorBrush _subtitleBrush = new();
    private readonly SolidColorBrush _activeSubtitleBrush = new();
    private readonly UISettings _uiSettings = new();
    private bool _animationsEnabled;
    private bool _motionSubscribed;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private long _trackId = -1;
    private int _activeLine = -1;
    private bool _syncing;
    private bool _seeking;
    private bool _hasTrack;
    private bool _loading;
    private bool _isPreview;
    private double _position;
    private double _duration;
    private double _lyricOffset;
    private int _manualLyricOffsetMs;
    private double _lyricsClockMs;
    private double _lyricFontSize = 32;
    private bool _manualScrollHeld;
    private bool _lyricPointerDown;
    private bool _resumeAfterPointerRelease;
    private bool? _shuffleMode;
    private RepeatMode? _repeatMode;
    private long _lastFrame;
    private double _lastClock = double.NaN;
    private double _scrollPosition;
    private double _scrollVelocity;
    private double _scrollTarget;
    private bool _scrollMoving;
    private bool _seekPending;
    private bool _recenterQueued;
    private bool _fullScreen;
    private double _captionInset = 138;
    private readonly Dictionary<UIElement, long> _motionDeadlines = new();
    private readonly HashSet<UIElement> _translationReady = new();
    private bool _updatingLayout;
    private double _lyricTopInset = 24;

    public NowPlayingView()
    {
        InitializeComponent();
        UpdateSheetClip();
        ApplyThemePalette();
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        RegisterPropertyChangedCallback(VisibilityProperty, View_VisibilityChanged);
        SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => _seeking = false), true);
        SeekBar.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => _seeking = false), true);
        LyricsScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => HoldAutoScroll()), true);
        LyricsScroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => BeginLyricPointerInteraction()), true);
        LyricsScroller.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndLyricPointerInteraction()), true);
        LyricsScroller.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => EndLyricPointerInteraction()), true);
        LyricsScroller.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndLyricPointerInteraction()), true);
    }

    public event EventHandler? PlayPauseRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? FavoriteRequested;
    public event EventHandler? CopyTrackLinkRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? FullScreenRequested;
    public event EventHandler? QueueRequested;
    public event EventHandler<FrameworkElement>? ArtistRequested;
    public event EventHandler? RepeatRequested;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler<double>? LyricSeekRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler<int>? LyricCorrectionRequested;
    public FrameworkElement TitleBarRegion => HeaderDragArea;

    public void UpdatePlayback(Track? track, bool isPlaying, bool isLoading, TimeSpan position, TimeSpan duration,
        bool isPreview, bool isFavorite, double volume, double lyricOffsetSeconds = 0, int lyricCorrectionMs = 0)
    {
        var previewChanged = _isPreview != isPreview;
        var trackChanged = _trackId != (track?.Id ?? 0);
        _hasTrack = track is not null;
        CopyTrackLinkButton.IsEnabled = _hasTrack;
        _loading = isLoading;
        _isPreview = isPreview;
        _position = Math.Max(0, position.TotalSeconds);
        _duration = Math.Max(0, duration.TotalSeconds);
        _lyricOffset = Math.Max(0, lyricOffsetSeconds);
        var correctionChanged = _manualLyricOffsetMs != LyricTiming.NormalizeOffsetMs(lyricCorrectionMs);
        _manualLyricOffsetMs = LyricTiming.NormalizeOffsetMs(lyricCorrectionMs);
        if (correctionChanged)
        {
            NotifySeek();
            _lastClock = double.NaN;
        }
        UpdateCorrectionControls();
        // The visible view is driven by the native clock on every render frame.
        // Do not replace it with a 400 ms playback-state sample between frames.
        if (Visibility != Visibility.Visible || !IsLoaded || trackChanged || correctionChanged)
            _lyricsClockMs = LyricTiming.ToLyricPosition(TimeSpan.FromSeconds(_position + _lyricOffset), _manualLyricOffsetMs).TotalMilliseconds;
        if (trackChanged)
        {
            HideActiveLyricGap();
            _lyricGaps = Array.Empty<LyricGap>();
            ReleaseLyricBlur();
            _manualScrollHeld = false;
            _lyricPointerDown = false;
            _resumeAfterPointerRelease = false;
            _lastClock = double.NaN;
            _seekPending = true;
            _scrollMoving = false;
            _scrollVelocity = 0;
            _trackId = track?.Id ?? 0;
            TrackTitle.Text = track?.Title ?? "未播放";
            TrackArtist.Text = track?.Artist ?? string.Empty;
            ToolTipService.SetToolTip(TrackTitle, track?.Title ?? string.Empty);
            ToolTipService.SetToolTip(TrackArtist, track?.Artist ?? string.Empty);
            MessageBar.IsOpen = false;
            Artwork.Source = null;
            AmbientArtwork.Source = null;
            Artwork.Visibility = Visibility.Collapsed;
            if (Uri.TryCreate(track?.ArtworkUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                Artwork.Source = new BitmapImage(uri);
                AmbientArtwork.Source = new BitmapImage { DecodePixelWidth = 12, DecodePixelHeight = 12, UriSource = uri };
                Artwork.Visibility = Visibility.Visible;
            }
        }
        PlayIcon.Glyph = isLoading ? FilledIcons.Loading : isPlaying ? FilledIcons.Pause : FilledIcons.Play;
        PlayButton.IsEnabled = _hasTrack && !isLoading;
        PreviousButton.IsEnabled = _hasTrack;
        NextButton.IsEnabled = _hasTrack;
        FavoriteButton.IsEnabled = _hasTrack;
        TrackArtistButton.IsEnabled = _hasTrack && !string.IsNullOrWhiteSpace(track?.Artist);
        FavoriteIcon.Opacity = isFavorite ? 1 : 0.55;
        ToolTipService.SetToolTip(FavoriteButton, isFavorite ? "从喜欢的歌曲移除" : "添加到喜欢的歌曲");
        SourceCaption.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
        if (previewChanged && IsLoaded) UpdateResponsiveLayout();
        _syncing = true;
        SeekBar.Maximum = Math.Max(1, _duration);
        if (!_seeking) SeekBar.Value = Math.Clamp(_position, 0, SeekBar.Maximum);
        SeekBar.IsEnabled = _hasTrack && !isLoading && _duration > 0;
        VolumeBar.Value = Math.Round((double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0.75) * 100);
        VolumeValue.Text = VolumeBar.Value <= 0 ? "静音" : $"{VolumeBar.Value:0}%";
        _syncing = false;
        ElapsedText.Text = FormatTime(position);
        DurationText.Text = "−" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, _duration - _position)));
        HighlightCurrentLine();
        UpdateSeekAvailability();
    }

    public void UpdatePlaybackModes(bool shuffle, RepeatMode repeat)
    {
        if (_shuffleMode == shuffle && _repeatMode == repeat) return;
        _shuffleMode = shuffle;
        _repeatMode = repeat;
        RepeatIcon.Opacity = !shuffle && repeat == RepeatMode.Off ? 0.45 : 1;
        RepeatIcon.Glyph = shuffle ? FilledIcons.Shuffle : repeat == RepeatMode.One ? FilledIcons.RepeatOne : FilledIcons.Repeat;
        RepeatButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var label = shuffle ? "随机播放" : repeat switch { RepeatMode.All => "列表循环", RepeatMode.One => "单曲循环", _ => "顺序播放" };
        ToolTipService.SetToolTip(RepeatButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RepeatButton, label + "，点击切换播放模式");
    }

    /// <summary>Accepts the native original-song clock, including any preview offset.</summary>
    public void UpdateLyricsPosition(TimeSpan originalPosition)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrame == 0 ? 1d / 60 : Math.Clamp((now - _lastFrame) / (double)Stopwatch.Frequency, 0.001, 0.05);
        _lastFrame = now;
        var timestamp = LyricTiming.ToLyricPosition(originalPosition, _manualLyricOffsetMs).TotalMilliseconds;
        if (double.IsFinite(_lastClock) && (timestamp < _lastClock - 120 || timestamp - _lastClock > Math.Max(700, elapsed * 2000)))
        {
            _seekPending = true;
            _scrollVelocity = 0;
            _manualScrollHeld = false;
        }
        _lastClock = timestamp;
        _lyricsClockMs = timestamp;
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        HighlightCurrentLine();
        AdvanceScroll(elapsed);
        UpdateLyricBlur();
    }

    public void NotifySeek()
    {
        _seekPending = true;
        _scrollVelocity = 0;
        _manualScrollHeld = false;
        _lyricPointerDown = false;
        _resumeAfterPointerRelease = false;
        _lyricBlurStateDirty = true;
    }

    public void SetLyrics(IReadOnlyList<LyricLine> lines, string emptyMessage = "暂无歌词")
    {
        StopLyricTravelMotion();
        ReleaseLyricBlur();
        var visibleLines = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).OrderBy(line => line.TimeMs).ToArray();
        var instrumental = visibleLines.Length > 0 && visibleLines.All(line => IsInstrumentalMarker(line.Text));
        _lines = instrumental ? Array.Empty<LyricLine>() : visibleLines;
        _activeLine = -1;
        _manualScrollHeld = false;
        _lyricPointerDown = false;
        _resumeAfterPointerRelease = false;
        _scrollMoving = false;
        _scrollVelocity = 0;
        _seekPending = true;
        _motionDeadlines.Clear();
        _translationReady.Clear();
        _lyricButtons.Clear();
        _lineVisuals.Clear();
        LyricsStack.Children.Clear();
        PrepareLyricGaps();
        EmptyLyricsText.Text = instrumental ? "纯音乐，请欣赏" : emptyMessage;
        EmptyLyricsPanel.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var line in _lines)
        {
            var visual = new LineVisual();
            FrameworkElement content;
            if (line.Words.Count > 0)
            {
                visual.TimedText = new KaraokeText(line.Words, _lyricFontSize);
                content = visual.TimedText;
            }
            else
            {
                var text = new TextBlock
                {
                    Text = line.Text,
                    FontSize = _lyricFontSize,
                    FontFamily = LyricTypography.PrimaryFamily,
                    FontWeight = Microsoft.UI.Text.FontWeights.Black,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _mutedLyricBrush
                };
                visual.PlainText = text;
                content = text;
            }
            var group = new StackPanel { Spacing = 7 };
            group.Children.Add(content);
            visual.Container = group;
            if (!string.IsNullOrWhiteSpace(line.Romanization))
            {
                visual.Romanization = new TextBlock
                {
                    Text = line.Romanization, FontSize = Math.Clamp(_lyricFontSize * 0.65, 18, 26),
                    FontFamily = LyricTypography.SecondaryFamily,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap, Foreground = _subtitleBrush,
                    Visibility = RomanizationSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed
                };
                group.Children.Add(visual.Romanization);
            }
            if (!string.IsNullOrWhiteSpace(line.Translation))
            {
                visual.Translation = new TextBlock
                {
                    Text = line.Translation, FontSize = Math.Clamp(_lyricFontSize * 0.64, 18, 26),
                    FontFamily = LyricTypography.SecondaryFamily,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap, Foreground = _subtitleBrush,
                    Visibility = TranslationSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed
                };
                group.Children.Add(visual.Translation);
            }
            var blurPresenter = new LyricBlurPresenter(group);
            blurPresenter.SizeChanged += (_, _) => InvalidateLyricBlurLayout();
            visual.BlurPresenter = blurPresenter;
            var button = new Button
            {
                Style = (Style)Resources["LyricButton"],
                Content = blurPresenter,
                Margin = new Thickness(0),
                Tag = line.TimeMs
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, line.Text);
            button.Click += (_, _) =>
            {
                var target = (line.TimeMs + _manualLyricOffsetMs) / 1000d - _lyricOffset;
                if (!_hasTrack || _loading || target < 0 || (_isPreview && target > _duration)) return;
                NotifySeek();
                LyricSeekRequested?.Invoke(this, target);
            };
            _lyricButtons.Add(button);
            _lineVisuals.Add(visual);
            AddLyricButton(_lyricButtons.Count - 1, button);
        }
        HighlightCurrentLine(forceCenter: true);
        UpdateSeekAvailability();
        QueueRecenter();
    }

    public void SetFullScreen(bool fullScreen)
    {
        _fullScreen = fullScreen;
        FullScreenIcon.Glyph = fullScreen ? FilledIcons.ExitFullScreen : FilledIcons.FullScreen;
        HeaderControls.Margin = new Thickness(12, 0, fullScreen ? 12 : _captionInset + 8, 0);
        ToolTipService.SetToolTip(FullScreenButton, fullScreen ? "退出全屏" : "全屏");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FullScreenButton, fullScreen ? "退出全屏播放界面" : "全屏播放界面");
        QueueRecenter();
    }

    public void SetCaptionInset(double inset)
    {
        _captionInset = Math.Max(0, inset);
        HeaderControls.Margin = new Thickness(12, 0, _fullScreen ? 12 : _captionInset + 8, 0);
    }

    public void ShowMessage(string message)
    {
        MessageBar.Message = message;
        MessageBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private void HighlightCurrentLine(bool forceCenter = false, bool animateActive = true)
    {
        var timestamp = _lyricsClockMs;
        var wasGapActive = _activeGapRow is not null;
        if (UpdateLyricGap(timestamp, out var gapResized)) forceCenter = true;
        if (gapResized && IsLoaded && Visibility == Visibility.Visible)
        {
            LyricsStack.UpdateLayout();
            // The ScrollViewer may preserve its own anchor while rows reflow.
            // Continue the spring from that actual offset and retain velocity.
            if (_scrollMoving && !_manualScrollHeld) _scrollPosition = LyricsScroller.VerticalOffset;
        }
        var gapActive = _activeGapRow is not null;
        var index = -1;
        if (!gapActive)
            for (var i = 0; i < _lines.Count && _lines[i].TimeMs <= timestamp; i++) index = i;
        var phraseAdvance = index > _activeLine && (_activeLine >= 0 || wasGapActive);
        if (index != _activeLine)
        {
            // Manual browsing lasts for the current phrase only. The next timed
            // phrase resumes following immediately, even if the song was paused.
            if (!gapActive)
            {
                if (_lyricPointerDown) _resumeAfterPointerRelease = true;
                else _manualScrollHeld = false;
            }
            _lyricBlurStateDirty = true;
            if (_activeLine >= 0 && _activeLine < _lineVisuals.Count) ResetLine(_lineVisuals[_activeLine]);
            _activeLine = index;
            if (index >= 0 && _lineVisuals[index].PlainText is TextBlock current)
                current.Foreground = _activeLineBrush;
            if (index >= 0)
            {
                if (_lineVisuals[index].Romanization is TextBlock romanization) romanization.Foreground = _activeSubtitleBrush;
                if (_lineVisuals[index].Translation is TextBlock translation) translation.Foreground = _activeSubtitleBrush;
            }
            if (index >= 0 && animateActive) AnimateActiveLine(_lineVisuals[index]);
            forceCenter = true;
        }
        // Word timing advances even while the containing lyric line stays the same.
        if (index >= 0) _lineVisuals[index].TimedText?.Update(timestamp, true, _animationsEnabled);
        var recenter = forceCenter || _seekPending;
        if ((recenter || gapResized) && !_manualScrollHeld)
        {
            AlignCurrentLine(phraseAdvance);
            if (recenter) UpdateLyricBlur(force: true);
        }
    }

    private void ResetLine(LineVisual line)
    {
        if (LineContent(line) is UIElement content) SettleLine(content);
        if (line.PlainText is not null) line.PlainText.Foreground = _mutedLyricBrush;
        if (line.Romanization is not null) line.Romanization.Foreground = _subtitleBrush;
        if (line.Translation is not null) line.Translation.Foreground = _subtitleBrush;
        line.TimedText?.Update(0, false, _animationsEnabled);
    }

    private void UpdateSeekAvailability()
    {
        for (var index = 0; index < _lyricButtons.Count; index++)
        {
            var target = (_lines[index].TimeMs + _manualLyricOffsetMs) / 1000d - _lyricOffset;
            var enabled = _hasTrack && !_loading && _duration > 0 && target >= 0 && (!_isPreview || target <= _duration);
            _lyricButtons[index].IsEnabled = enabled;
            // Keep all lyrics readable even when the account only permits a short preview.
            _lyricButtons[index].Opacity = _isPreview && (target < 0 || target > _duration) ? 0.55 : 1;
        }
    }

    private void AlignCurrentLine(bool phraseAdvance = false)
    {
        if (!IsLoaded || ActualWidth <= 0 || LyricsScroller.ViewportHeight <= 0) return;
        var target = 0d;
        if (TryGetGapScrollTarget(out var gapTarget)) target = gapTarget;
        else if (_activeLine >= 0 && _activeLine < _lyricButtons.Count)
        {
            var line = _lyricButtons[_activeLine];
            if (line.ActualHeight <= 0) return;
            var point = line.TransformToVisual(LyricsStack).TransformPoint(new Point(0, 0));
            var precedingSpan = 0d;
            if (_activeLine > 0)
            {
                var previous = _lyricButtons[_activeLine - 1];
                var previousPoint = previous.TransformToVisual(LyricsStack).TransformPoint(new Point(0, 0));
                // The measured distance includes the complete previous original,
                // translation, romanization and the gap between the two phrases.
                precedingSpan = Math.Max(previous.ActualHeight, point.Y - previousPoint.Y);
            }
            var anchor = LyricViewportLayout.CurrentLineAnchor(LyricsScroller.ViewportHeight,
                precedingSpan, line.ActualHeight, _lyricTopInset);
            // Keep the latter half of the preceding block above the active one.
            // Buttons remain unscaled, so visual lift/zoom cannot move the anchor.
            target = point.Y + LyricsStack.Margin.Top - anchor;
        }
        target = Math.Clamp(target, 0, LyricsScroller.ScrollableHeight);
        var current = LyricsScroller.VerticalOffset;
        if (!_animationsEnabled || (_seekPending && Math.Abs(target - current) > LyricsScroller.ViewportHeight * 1.25))
        {
            StopLyricTravelMotion();
            _scrollMoving = false;
            _scrollVelocity = 0;
            _scrollPosition = _scrollTarget = target;
            LyricsScroller.ChangeView(null, target, null, true);
        }
        else
        {
            if (!_scrollMoving) { _scrollPosition = current; _scrollVelocity = 0; }
            _scrollVelocity = LyricScrollMotion.RetargetVelocity(_scrollPosition, _scrollVelocity, target,
                phraseAdvance && !_seekPending);
            _scrollTarget = target;
            _scrollMoving = Math.Abs(target - _scrollPosition) > 0.25;
            if (_scrollMoving && phraseAdvance && !_seekPending) AnimateLyricTravelScale();
        }
        _seekPending = false;
    }

    private void AdvanceScroll(double elapsed)
    {
        if (!_scrollMoving || _manualScrollHeld) return;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, LyricsScroller.ScrollableHeight);
        // A restrained forward impulse gives the next phrase a decisive lift;
        // the analytic damping naturally slows it without stacked animations.
        var motion = LyricScrollMotion.Advance(new LyricScrollState(_scrollPosition, _scrollVelocity), _scrollTarget, elapsed);
        _scrollPosition = motion.Position;
        _scrollVelocity = motion.Velocity;
        if (Math.Abs(_scrollPosition - _scrollTarget) < 0.25 && Math.Abs(_scrollVelocity) < 2)
        {
            _scrollPosition = _scrollTarget;
            _scrollVelocity = 0;
            _scrollMoving = false;
        }
        LyricsScroller.ChangeView(null, Math.Clamp(_scrollPosition, 0, LyricsScroller.ScrollableHeight), null, true);
    }

    private void QueueRecenter()
    {
        InvalidateLyricBlurLayout();
        if (_recenterQueued) return;
        _recenterQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _recenterQueued = false;
            if (!IsLoaded || Visibility != Visibility.Visible) return;
            LyricsStack.UpdateLayout();
            HighlightCurrentLine(forceCenter: true);
        });
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSheetClip();
        ApplyThemePalette();
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        if (!_motionSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged += AnimationsEnabled_Changed;
            _motionSubscribed = true;
        }
        UpdateResponsiveLayout();
        QueueRecenter();
        AnimateEntrance();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_motionSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= AnimationsEnabled_Changed;
            _motionSubscribed = false;
        }
        HideActiveLyricGap();
        StopAllMotion();
        ReleaseLyricBlur();
        _scrollMoving = false;
        _lastFrame = 0;
    }

    private void ApplyThemePalette()
    {
        // The immersive surface keeps a neutral, high-contrast palette over every album.
        _mutedLyricBrush.Color = Windows.UI.Color.FromArgb(100, 255, 255, 255);
        _activeLineBrush.Color = Windows.UI.Color.FromArgb(250, 255, 255, 255);
        _subtitleBrush.Color = Windows.UI.Color.FromArgb(86, 255, 255, 255);
        _activeSubtitleBrush.Color = Windows.UI.Color.FromArgb(180, 255, 255, 255);
        foreach (var line in _lineVisuals)
        {
            ResetLine(line);
        }
        _activeLine = -1;
        HighlightCurrentLine(animateActive: false);
    }

    private void AnimationsEnabled_Changed(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        if (!_animationsEnabled) { StopAllMotion(); if (!_manualScrollHeld) AlignCurrentLine(); }
        UpdateLyricBlur(force: true);
    });

    private void View_VisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        // This must precede an opening or reversed animation. The clip belongs to
        // the stationary child root, so the whole bounded sheet moves as one unit.
        UpdateSheetClip();
        _lastFrame = 0;
        _lastClock = double.NaN;
        if (Visibility == Visibility.Visible && IsLoaded)
        {
            _manualScrollHeld = false;
            _lyricPointerDown = false;
            _resumeAfterPointerRelease = false;
            _lyricBlurStateDirty = true;
            _seekPending = true;
            AnimateEntrance();
            QueueRecenter();
        }
        else if (IsLoaded)
        {
            HideActiveLyricGap();
            StopAllMotion();
            ReleaseLyricBlur();
            _scrollMoving = false;
            _scrollVelocity = 0;
        }
    }

    private void AnimateEntrance()
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        AnimateContent(ContentGrid, new Vector3(0, 8, 0), 0.7f, 360);
    }

    private void AnimateActiveLine(LineVisual line)
    {
        if (!_manualScrollHeld && LineContent(line) is UIElement content && IsLoaded && Visibility == Visibility.Visible)
            AnimateContent(content, new Vector3(0, 4, 0), 1, 520);
    }

    private void AnimateContent(UIElement element, Vector3 start, float opacity, int milliseconds)
    {
        if (!_animationsEnabled) { StopMotion(element); return; }
        if (!_translationReady.Contains(element)) StopMotion(element);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var now = Stopwatch.GetTimestamp();
        if (!_motionDeadlines.TryGetValue(element, out var deadline) || now >= deadline)
        {
            visual.Properties.InsertVector3("Translation", start);
            visual.Opacity = opacity;
        }
        var movement = visual.Compositor.CreateVector3KeyFrameAnimation();
        movement.Duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.25f, 1));
        movement.InsertExpressionKeyFrame(0, "this.StartingValue");
        movement.InsertKeyFrame(0.7f, new Vector3(0, -0.6f, 0), easing);
        movement.InsertKeyFrame(1, Vector3.Zero, easing);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = movement.Duration;
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, 1, easing);
        visual.StartAnimation("Translation", movement);
        visual.StartAnimation("Opacity", fade);
        _motionDeadlines[element] = now + (long)(milliseconds / 1000d * Stopwatch.Frequency);
    }

    private void SettleLine(UIElement element)
    {
        if (!_animationsEnabled || !IsLoaded || Visibility != Visibility.Visible) { StopMotion(element); return; }
        if (!_translationReady.Contains(element)) StopMotion(element);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var settle = visual.Compositor.CreateVector3KeyFrameAnimation();
        settle.Duration = TimeSpan.FromMilliseconds(280);
        settle.InsertExpressionKeyFrame(0, "this.StartingValue");
        settle.InsertKeyFrame(1, Vector3.Zero);
        visual.StartAnimation("Translation", settle);
        _motionDeadlines[element] = Stopwatch.GetTimestamp() + (long)(0.28 * Stopwatch.Frequency);
    }

    private void StopAllMotion()
    {
        StopMotion(ContentGrid);
        foreach (var line in _lineVisuals)
        {
            if (LineContent(line) is UIElement content) StopMotion(content);
            line.TimedText?.StopMotion();
        }
    }

    private void StopMotion(UIElement element)
    {
        // Translation is a XAML interop property, not a built-in Visual property.
        // A first-show reset (or a lyric that has never animated) must enable and
        // initialise it before StopAnimation can resolve the property path.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        ResetLyricScale(element);
        _motionDeadlines.Remove(element);
        _translationReady.Add(element);
    }

    private static UIElement? LineContent(LineVisual line) => (UIElement?)line.BlurPresenter ?? line.Container;

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSheetClip(e.NewSize);
        UpdateResponsiveLayout();
        QueueRecenter();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSheetClip(e.NewSize);

    private void UpdateSheetClip(Size? size = null)
    {
        if (LayoutRoot is null || SheetBoundsClip is null) return;
        var localSize = size ?? new Size(LayoutRoot.ActualWidth, LayoutRoot.ActualHeight);
        var width = double.IsFinite(localSize.Width) ? Math.Max(0, localSize.Width) : 0;
        var height = double.IsFinite(localSize.Height) ? Math.Max(0, localSize.Height) : 0;
        var bounds = new Rect(0, 0, width, height);
        if (!SheetBoundsClip.Rect.Equals(bounds)) SheetBoundsClip.Rect = bounds;
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
        QueueRecenter();
    }

    private void UpdateResponsiveLayout()
    {
        if (_updatingLayout || ContentGrid is null || ActualWidth <= 0) return;
        _updatingLayout = true;
        try
        {
            var stacked = ActualWidth < 820;
            var compact = ActualWidth < 1180;
            var shortWindow = ActualHeight < 780;
            var horizontal = stacked ? 20 : ActualWidth < 1050 ? 28 : ActualWidth < 1400 ? 44 : 72;
            ContentGrid.Margin = new Thickness(horizontal, shortWindow ? 4 : 12, horizontal, shortWindow ? 16 : 28);
            ContentGrid.ColumnSpacing = stacked ? 0 : Math.Clamp(ActualWidth * 0.045, 28, 72);
            ContentGrid.RowSpacing = stacked ? 14 : 0;
            var availableWidth = Math.Max(1, ActualWidth - ContentGrid.Margin.Left - ContentGrid.Margin.Right);
            var availableHeight = ContentGrid.ActualHeight > 0 ? ContentGrid.ActualHeight :
                Math.Max(1, ActualHeight - 58 - ContentGrid.Margin.Top - ContentGrid.Margin.Bottom);
            var columnWidth = stacked ? availableWidth - 16 :
                (availableWidth - ContentGrid.ColumnSpacing) * 0.92 / 2.1 - 16;
            columnWidth = Math.Max(1, columnWidth);

            // On narrow windows use a compact cover/control row above the lyrics.
            // At normal widths, preserve the familiar cover/lyrics columns.
            Grid.SetRow(AlbumScroller, 0);
            Grid.SetColumn(AlbumScroller, 0);
            Grid.SetRowSpan(AlbumScroller, stacked ? 1 : 2);
            Grid.SetColumnSpan(AlbumScroller, stacked ? 2 : 1);
            Grid.SetRow(LyricsPane, stacked ? 1 : 0);
            Grid.SetColumn(LyricsPane, stacked ? 0 : 1);
            Grid.SetRowSpan(LyricsPane, stacked ? 1 : 2);
            Grid.SetColumnSpan(LyricsPane, stacked ? 2 : 1);
            AlbumScroller.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
            AlbumScroller.MaxHeight = stacked ? Math.Min(230, availableHeight * 0.44) : double.PositiveInfinity;
            AlbumColumn.MaxWidth = stacked ? double.PositiveInfinity : 540;
            AlbumColumn.ColumnSpacing = stacked ? 16 : 0;
            AlbumColumn.Margin = new Thickness(8, stacked ? 4 : 12, 8, stacked ? 4 : 16);
            Grid.SetRow(PlaybackDetails, stacked ? 0 : 1);
            Grid.SetColumn(PlaybackDetails, stacked ? 1 : 0);
            ArtworkRow.Height = GridLength.Auto;
            PlaybackRow.Height = stacked ? new GridLength(0) : GridLength.Auto;
            var coverSide = stacked ? Math.Clamp(availableWidth * 0.22, 96, 148) :
                Math.Max(Math.Min(128, columnWidth), Math.Min(columnWidth, availableHeight - (shortWindow ? 258 : 308) - (_isPreview ? 24 : 0)));
            coverSide = Math.Min(coverSide, stacked ? columnWidth : Math.Min(448, columnWidth * 0.9));
            // Share one width for the cover, metadata and progress track. Keep
            // the height-constrained cover size instead of enlarging short views.
            AlbumColumn.Width = stacked ? columnWidth : coverSide;
            ArtworkColumn.Width = stacked ? new GridLength(coverSide) : new GridLength(1, GridUnitType.Star);
            PlaybackColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ArtworkFrame.Width = ArtworkFrame.Height = coverSide;
            ArtworkFrame.VerticalAlignment = VerticalAlignment.Center;
            var controlsWidth = stacked ? Math.Max(1, columnWidth - coverSide - 16) : coverSide;
            var smallControls = controlsWidth < 300;
            var tinyControls = controlsWidth < 220;
            var sideButtonSize = smallControls ? 32d : 40d;
            var transportSide = tinyControls ? Math.Clamp((controlsWidth - 60) / 2, 28, 40) : smallControls ? 44d : 52d;
            var transportMain = tinyControls ? 44d : smallControls || stacked ? 56d : shortWindow ? 64d : 72d;
            ShuffleButton.Width = RepeatButton.Width = sideButtonSize;
            ShuffleButton.Height = RepeatButton.Height = sideButtonSize;
            PreviousButton.Width = NextButton.Width = transportSide;
            PreviousButton.Height = NextButton.Height = transportSide;
            PreviousButton.CornerRadius = NextButton.CornerRadius = new CornerRadius(transportSide / 2);
            PlayButton.Width = PlayButton.Height = transportMain;
            PlayButton.CornerRadius = new CornerRadius(transportMain / 2);
            PlayIcon.FontSize = tinyControls ? 26 : smallControls || stacked ? 30 : 36;
            PreviousIcon.FontSize = NextIcon.FontSize = tinyControls ? 24 : 34;
            TransportControls.ColumnSpacing = Math.Clamp((controlsWidth - sideButtonSize * 2 - transportSide * 2 - transportMain) / 4, 4, 12);
            ShuffleButton.Visibility = RepeatButton.Visibility = controlsWidth < 260 ? Visibility.Collapsed : Visibility.Visible;
            TrackTitle.FontSize = tinyControls || stacked ? 18 : compact || shortWindow ? 21 : 23;
            TrackArtist.FontSize = tinyControls ? 14 : stacked ? 15 : compact || shortWindow ? 17 : 18;
            TrackInfoGrid.Margin = new Thickness(0, stacked ? 0 : shortWindow ? 16 : 24, 0, 0);
            TrackInfoGrid.ColumnSpacing = smallControls ? 4 : 8;
            var actionSize = tinyControls ? 28d : smallControls || stacked ? 32d : 40d;
            TrackInfoGrid.ColumnDefinitions[1].Width = TrackInfoGrid.ColumnDefinitions[2].Width = new GridLength(actionSize);
            FavoriteButton.Width = OptionsButton.Width = actionSize;
            FavoriteButton.Height = OptionsButton.Height = actionSize;
            SeekBar.Margin = new Thickness(0, shortWindow || stacked ? 6 : 16, 0, 0);
            TransportControls.Margin = new Thickness(0, shortWindow || stacked ? 6 : 14, 0, 0);
            SourceCaption.Margin = new Thickness(0, shortWindow || stacked ? 4 : 10, 0, 0);
            var lyricWidth = stacked ? availableWidth : (availableWidth - ContentGrid.ColumnSpacing) * 1.18 / 2.1;
            _lyricFontSize = stacked ? Math.Clamp(lyricWidth / 22, 26, 32) : Math.Clamp((lyricWidth - 24) / 16, 26, 40);
            LyricsStack.Spacing = Math.Clamp(_lyricFontSize * 0.5, 14, 20);
            EmptyLyricsText.FontSize = Math.Min(36, _lyricFontSize);
            foreach (var line in _lineVisuals)
            {
                if (line.PlainText is not null) line.PlainText.FontSize = _lyricFontSize;
                if (line.TimedText is not null) line.TimedText.FontSize = _lyricFontSize;
                if (line.Container is not null) line.Container.Spacing = Math.Clamp(_lyricFontSize * 0.21, 6, 8);
                if (line.Romanization is not null) line.Romanization.FontSize = Math.Clamp(_lyricFontSize * 0.65, 18, 26);
                if (line.Translation is not null) line.Translation.FontSize = Math.Clamp(_lyricFontSize * 0.64, 18, 26);
            }
            UpdateLyricInsets();
        }
        finally { _updatingLayout = false; }
    }

    private void UpdateLyricInsets()
    {
        var height = LyricsScroller.ViewportHeight;
        if (height <= 0) return;
        _lyricTopInset = Math.Clamp(height * 0.035, 16, 24);
        // Tail room lets the final phrase retain its preceding block above it;
        // a short last phrase must not be pinned to the bottom of the viewport.
        LyricsStack.Margin = new Thickness(0, _lyricTopInset, 6, Math.Max(0, height - _lyricTopInset));
    }

    private void LyricsScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLyricInsets();
        QueueRecenter();
    }
    private void HoldAutoScroll()
    {
        _scrollMoving = false;
        _scrollVelocity = 0;
        _manualScrollHeld = true;
        StopLyricTravelMotion();
        ShowAllLyricsClearly();
    }

    private void BeginLyricPointerInteraction()
    {
        _lyricPointerDown = true;
        _resumeAfterPointerRelease = false;
        HoldAutoScroll();
    }

    private void EndLyricPointerInteraction()
    {
        _lyricPointerDown = false;
        if (!_resumeAfterPointerRelease) return;
        // A phrase can change during a touch/scrollbar drag. Finish that direct
        // manipulation first, then follow without fighting the user's pointer.
        _resumeAfterPointerRelease = false;
        _manualScrollHeld = false;
        _lyricBlurStateDirty = true;
        QueueRecenter();
    }

    private void SubtitleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (TranslationSwitch is null || RomanizationSwitch is null) return;
        foreach (var line in _lineVisuals)
        {
            if (line.Romanization is not null) line.Romanization.Visibility = RomanizationSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (line.Translation is not null) line.Translation.Visibility = TranslationSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }
        QueueRecenter();
    }

    private void SeekBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncing && _hasTrack && !_loading) { NotifySeek(); SeekRequested?.Invoke(this, e.NewValue); }
    }

    private void VolumeBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VolumeValue is not null) VolumeValue.Text = e.NewValue <= 0 ? "静音" : $"{e.NewValue:0}%";
        if (!_syncing) VolumeRequested?.Invoke(this, Math.Clamp(e.NewValue / 100, 0, 1));
    }

    private void Artwork_ImageFailed(object sender, ExceptionRoutedEventArgs e) => Artwork.Visibility = Visibility.Collapsed;
    private static bool IsInstrumentalMarker(string text)
    {
        var label = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        return label is "纯音乐" or "纯音乐请欣赏" or "此歌曲为没有填词的纯音乐请您欣赏" or "此歌曲为纯音乐请欣赏"
            || label.Equals("instrumental", StringComparison.OrdinalIgnoreCase);
    }
    private void CopyTrackLink_Click(object sender, RoutedEventArgs e) => CopyTrackLinkRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void FullScreen_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke(this, EventArgs.Empty);
    private void Favorite_Click(object sender, RoutedEventArgs e) => FavoriteRequested?.Invoke(this, EventArgs.Empty);
    private void Previous_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    private void Queue_Click(object sender, RoutedEventArgs e) => QueueRequested?.Invoke(this, EventArgs.Empty);
    private void Artist_Click(object sender, RoutedEventArgs e) => ArtistRequested?.Invoke(this, TrackArtistButton);
    private void Repeat_Click(object sender, RoutedEventArgs e) => RepeatRequested?.Invoke(this, EventArgs.Empty);
    private static string FormatTime(TimeSpan value) => $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";

    private sealed class LineVisual
    {
        public LyricBlurPresenter? BlurPresenter { get; set; }
        public StackPanel? Container { get; set; }
        public TextBlock? PlainText { get; set; }
        public KaraokeText? TimedText { get; set; }
        public TextBlock? Romanization { get; set; }
        public TextBlock? Translation { get; set; }
    }
}
