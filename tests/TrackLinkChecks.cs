using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private async Task RunTrackLinkHitChecksAsync()
    {
        var playing = _player.CurrentTrack ?? throw new InvalidOperationException("Track hit checks require the fixture song.");
        var queueIds = _player.Queue.Select(track => track.Id).ToArray();
        var rowTrack = new Track { Id = playing.Id, Title = "短标题", Artist = "林", Album = "月", AlbumId = 8001 };
        BeginPage("search");
        ShowTracks("搜索结果", "1 首歌曲", new[] { rowTrack });
        await WaitForTrackHitAsync(() => TrackList.ContainerFromItem(rowTrack) is ListViewItem { ActualHeight: > 0 });
        await Task.Delay(400); // Let the normal page transition finish before sampling actual hit targets.
        Root.UpdateLayout();
        var row = (ListViewItem)TrackList.ContainerFromItem(rowTrack);
        foreach (var name in new[] { "TrackArtistLink", "TrackAlbumLink" })
        {
            var link = FindTrackHitChild<Button>(row, element => element.Name == name)
                ?? throw new InvalidOperationException($"Missing realized {name}.");
            var label = link.Content as TextBlock ?? throw new InvalidOperationException($"Missing text in {name}.");
            var labelStart = label.TransformToVisual(Root).TransformPoint(new Point(0, label.ActualHeight / 2));
            var probe = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = probe.DesiredSize.Width;
            RequireTrackHit(textWidth > 0 && link.ActualWidth <= textWidth + 6, $"{name} width {link.ActualWidth} for {textWidth} DIP text");
            RequireTrackHit(ButtonAt(new Point(labelStart.X + textWidth / 2, labelStart.Y)) == link, $"{name} text hit");
            var afterText = new Point(labelStart.X + textWidth + 12, labelStart.Y);
            RequireTrackHit(ButtonAt(afterText) is null && VisualTreeHelper.FindElementsInHostCoordinates(afterText, Root)
                .Any(element => FindTrackAncestor<ListViewItem>(element) == row), $"{name} trailing space belongs to the row");
        }

        await WaitForTrackHitAsync(() => LocateCurrentButton.Visibility == Visibility.Visible && LocateCurrentButton.ActualWidth > 0);
        AssertTrackLocatorGeometry(LocateCurrentButton);
        RequireTrackHit(FindTrackAncestor<Button>(VisualTreeHelper.GetParent(PlayerArtistButton)) is null
            && FindTrackHitChild<Button>(PlayerInfoButton, button => button != PlayerInfoButton) is null, "separate bottom-bar buttons");
        var artistBounds = TrackHitBounds(PlayerArtistButton);
        var infoBounds = TrackHitBounds(PlayerInfoButton);
        var artistPoint = new Point(artistBounds.X + artistBounds.Width / 2, artistBounds.Y + artistBounds.Height / 2);
        var trailingPoint = new Point(artistBounds.Right + 8, artistPoint.Y);
        RequireTrackHit(infoBounds.Contains(trailingPoint) && ButtonAt(trailingPoint) == PlayerInfoButton, "bottom artist trailing space opens lyrics");
        RequireTrackHit(ButtonAt(TrackHitCenter(PlayerCoverBorder)) == PlayerInfoButton, "bottom cover opens lyrics");
        RequireTrackHit(ButtonAt(TrackHitCenter(PlayerTitle)) == PlayerInfoButton, "bottom title opens lyrics");
        var artistTarget = ButtonAt(artistPoint);
        RequireTrackHit(artistTarget == PlayerArtistButton, "bottom artist text wins hit testing over the full metadata button");
        InvokeTrackHit(artistTarget!);
        await WaitForTrackHitAsync(() => _page == "artist" && !_pageLoading && ArtistView.Visibility == Visibility.Visible);
        RequireTrackHit(_artistId == 7001 && !_immersiveOpen, "bottom artist opens only the artist page");
        await WaitForTrackHitAsync(() => ArtistLocateCurrentButton.Visibility == Visibility.Visible && ArtistLocateCurrentButton.ActualWidth > 0);
        AssertTrackLocatorGeometry(ArtistLocateCurrentButton);

        var lyricsTarget = ButtonAt(trailingPoint);
        RequireTrackHit(lyricsTarget == PlayerInfoButton, "remaining metadata area retains its target on the artist page");
        InvokeTrackHit(lyricsTarget!);
        await WaitForTrackHitAsync(() => _immersiveOpen && NowPlaying.Visibility == Visibility.Visible);
        RequireTrackHit(_page == "artist" && _player.CurrentTrack?.Id == playing.Id && !_player.IsPlaying
            && _player.Queue.Select(track => track.Id).SequenceEqual(queueIds), "lyrics open without changing the page, track, or queue");
        CloseNowPlaying();
        await WaitForTrackHitAsync(() => NowPlaying.Visibility == Visibility.Collapsed);
    }

    private Button? ButtonAt(Point point) => FindTrackAncestor<Button>(
        VisualTreeHelper.FindElementsInHostCoordinates(point, Root).FirstOrDefault());

    private Rect TrackHitBounds(FrameworkElement element) => element.TransformToVisual(Root)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private Point TrackHitCenter(FrameworkElement element)
    {
        var bounds = TrackHitBounds(element);
        return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    private void AssertTrackLocatorGeometry(Button button)
    {
        var size = button.ActualWidth;
        var corners = button.CornerRadius;
        RequireTrackHit(size >= 28 && size <= 56 && Math.Abs(size - button.ActualHeight) <= 1
            && new[] { corners.TopLeft, corners.TopRight, corners.BottomLeft, corners.BottomRight }.All(radius => radius >= size / 2 - 1),
            $"{button.Name} circular bounds {size} × {button.ActualHeight}");
        var icon = FindTrackHitChild<IconElement>(button, _ => true);
        RequireTrackHit(icon is not null && button.Content is not string && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)),
            $"{button.Name} icon with an accessible name");
        var center = TrackHitCenter(button);
        var iconCenter = TrackHitCenter(icon!);
        RequireTrackHit(Math.Abs(center.X - iconCenter.X) <= 2 && Math.Abs(center.Y - iconCenter.Y) <= 2,
            $"{button.Name} centered icon");
    }

    private static T? FindTrackHitChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindTrackHitChild(VisualTreeHelper.GetChild(root, index), predicate) is { } child) return child;
        return null;
    }

    private static void InvokeTrackHit(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var provider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
        RequireTrackHit(provider is not null && button.IsEnabled, $"{button.Name} invoke pattern");
        provider!.Invoke();
    }

    private static async Task WaitForTrackHitAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > 2500) throw new TimeoutException("Track hit-test layout or route timed out.");
            await Task.Delay(25);
        }
    }

    private static void RequireTrackHit(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("Track hit-test failed: " + detail);
    }
}
