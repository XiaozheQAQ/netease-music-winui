using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace winuitest.Controls;

public enum SkeletonKind
{
    Tracks,
    Discovery,
    Playlists
}

/// <summary>Text-free native placeholders. The parent owns loading and content visibility.</summary>
public sealed partial class LoadingSkeleton : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(SkeletonKind), typeof(LoadingSkeleton),
        new PropertyMetadata(SkeletonKind.Tracks, PresentationChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingSkeleton),
        new PropertyMetadata(false, PresentationChanged));

    public static readonly DependencyProperty ContentInsetProperty = DependencyProperty.Register(
        nameof(ContentInset), typeof(Thickness), typeof(LoadingSkeleton),
        new PropertyMetadata(new Thickness(0, 0, 0, 24)));

    private readonly UISettings _settings = new();
    private readonly List<(UIElement Element, long Token)> _visibilityObservers = new();
    private readonly Dictionary<Grid, List<CardShape>> _cards = new();
    private readonly Dictionary<Grid, double> _cardWidths = new();
    private bool _ready;
    private bool _running;
    private bool _settingsSubscribed;

    public LoadingSkeleton()
    {
        InitializeComponent();
        BuildTrackRows();
        BuildCards(DiscoveryCards, 6);
        BuildCards(PlaylistCards, 8);
        _ready = true;
        UpdatePresentation();
    }

    public SkeletonKind Kind
    {
        get => (SkeletonKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Thickness ContentInset
    {
        get => (Thickness)GetValue(ContentInsetProperty);
        set => SetValue(ContentInsetProperty, value);
    }

    private static void PresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LoadingSkeleton { _ready: true } skeleton) skeleton.UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        TracksShape.Visibility = Kind == SkeletonKind.Tracks ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryShape.Visibility = Kind == SkeletonKind.Discovery ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsShape.Visibility = Kind == SkeletonKind.Playlists ? Visibility.Visible : Visibility.Collapsed;
        SynchronizeAnimation();
    }

    private Border Block(double height, double? width = null, double radius = 5)
    {
        var block = new Border
        {
            Style = (Style)Resources["SkeletonBlock"],
            Height = height,
            CornerRadius = new CornerRadius(radius)
        };
        if (width.HasValue)
        {
            block.Width = width.Value;
            block.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return block;
    }

    private void BuildTrackRows()
    {
        for (var index = 0; index < 7; index++)
        {
            var row = new TrackColumnsGrid { Height = 72 };
            var cover = Block(44, radius: 8);
            cover.VerticalAlignment = VerticalAlignment.Center;
            TrackColumnsGrid.SetField(cover, TrackField.Cover);
            row.Children.Add(cover);
            var titleGroup = new StackPanel { Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
            var title = Block(12);
            title.Margin = new Thickness(0, 0, 20 + index % 3 * 22, 0);
            titleGroup.Children.Add(title);
            var subtitle = Block(9);
            subtitle.Margin = new Thickness(0, 0, 70 + index % 2 * 18, 0);
            titleGroup.Children.Add(subtitle);
            TrackColumnsGrid.SetField(titleGroup, TrackField.Title);
            row.Children.Add(titleGroup);
            var album = Block(10);
            album.VerticalAlignment = VerticalAlignment.Center;
            album.Margin = new Thickness(0, 0, 24, 0);
            TrackColumnsGrid.SetField(album, TrackField.Album);
            row.Children.Add(album);
            var duration = Block(9, 32);
            duration.VerticalAlignment = VerticalAlignment.Center;
            duration.HorizontalAlignment = HorizontalAlignment.Right;
            TrackColumnsGrid.SetField(duration, TrackField.Duration);
            row.Children.Add(duration);
            TrackRows.Children.Add(row);
        }
    }

    private void BuildCards(Grid host, int count)
    {
        var cards = new List<CardShape>();
        for (var index = 0; index < count; index++)
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(14, 14, 14, 22) };
            var artwork = Block(144, radius: 14);
            var title = Block(12);
            var subtitle = Block(9);
            panel.Children.Add(artwork);
            panel.Children.Add(title);
            if (ReferenceEquals(host, PlaylistCards)) panel.Children.Add(subtitle);
            cards.Add(new CardShape(panel, artwork, title, subtitle));
            host.Children.Add(panel);
        }
        _cards[host] = cards;
    }

    private void Cards_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid host || !_cards.TryGetValue(host, out var cards) || args.NewSize.Width <= 0) return;
        var width = args.NewSize.Width;
        if (_cardWidths.TryGetValue(host, out var previous) && Math.Abs(width - previous) < 0.5) return;
        _cardWidths[host] = width;
        // Mirror the native card layout's cell width, content inset and display
        // rounding so placeholders stay aligned when real artwork arrives.
        var columns = Math.Clamp((int)(width / 198), 1, 6);
        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var cellWidth = Math.Max(1, Math.Floor(Math.Floor(width * scale) / columns) - 1) / scale;
        var cardWidth = Math.Max(1, cellWidth - 28);
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++) host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });
        for (var index = 0; index < (cards.Count + columns - 1) / columns; index++)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            Grid.SetColumn(card.Panel, index % columns);
            Grid.SetRow(card.Panel, index / columns);
            card.Artwork.Height = cardWidth;
            card.Title.Margin = new Thickness(0, 0, cardWidth * 0.08, 0);
            card.Subtitle.Margin = new Thickness(0, 0, cardWidth * 0.4, 0);
        }
    }

    private void Skeleton_Loaded(object sender, RoutedEventArgs args)
    {
        ObserveVisibility();
        if (!_settingsSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged += MotionPreferenceChanged;
            _settingsSubscribed = true;
        }
        SynchronizeAnimation();
    }

    private void Skeleton_Unloaded(object sender, RoutedEventArgs args)
    {
        StopAnimation();
        ClearVisibilityObservers();
        if (_settingsSubscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _settings.AnimationsEnabledChanged -= MotionPreferenceChanged;
        _settingsSubscribed = false;
    }

    private void ObserveVisibility()
    {
        ClearVisibilityObservers();
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element)
            {
                var token = element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SynchronizeAnimation());
                _visibilityObservers.Add((element, token));
            }
        }
    }

    private void ClearVisibilityObservers()
    {
        foreach (var subscription in _visibilityObservers)
            subscription.Element.UnregisterPropertyChangedCallback(VisibilityProperty, subscription.Token);
        _visibilityObservers.Clear();
    }

    private bool IsEffectivelyVisible()
    {
        if (Visibility != Visibility.Visible) return false;
        foreach (var subscription in _visibilityObservers)
            if (subscription.Element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private void SynchronizeAnimation()
    {
        if (!_ready || !IsLoaded) return;
        var shouldAnimate = IsActive && IsEffectivelyVisible() && _settings.AnimationsEnabled;
        if (!shouldAnimate) { StopAnimation(); return; }
        if (_running) return;
        var visual = ElementCompositionPreview.GetElementVisual(SkeletonCanvas);
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(2400);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));
        animation.InsertKeyFrame(0, 0.65f);
        animation.InsertKeyFrame(0.5f, 1, easing);
        animation.InsertKeyFrame(1, 0.65f, easing);
        visual.StartAnimation("Opacity", animation);
        _running = true;
    }

    private void StopAnimation()
    {
        if (!_ready) return;
        var visual = ElementCompositionPreview.GetElementVisual(SkeletonCanvas);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0.85f;
        _running = false;
    }

    private void MotionPreferenceChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(SynchronizeAnimation);

    private sealed record CardShape(StackPanel Panel, Border Artwork, Border Title, Border Subtitle);
}
