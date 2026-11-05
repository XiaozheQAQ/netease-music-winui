using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using winuitest.Models;

namespace winuitest;

// Linked only in native-check builds. This path deliberately never registers a
// notification icon, so it can run without replacing the listener's live icon.
public sealed partial class MainWindow
{
    partial void TryStartUiChecks(ref bool started)
    {
        var reportPath = Environment.GetEnvironmentVariable("LUMA_UI_CHECK_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        started = true;
        RunUiChecksAsync(reportPath);
    }

    private async void RunUiChecksAsync(string reportPath)
    {
        var clock = Stopwatch.StartNew();
        var checks = new List<string>();
        var screenshots = new List<string>();
        var step = "isolated-profile";
        var errorType = "";
        var errorMessage = "";
        object? failureDetails = null;
        var success = false;
        var ownsInstance = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        void Assert(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Native UI assertion failed.");
        }

        async Task WaitAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            var waiting = Stopwatch.StartNew();
            while (!predicate())
            {
                if (_closed || waiting.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException();
                await Task.Delay(25, budget.Token);
            }
        }

        T? Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
        {
            if (root is T match && predicate(match)) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                if (Find(VisualTreeHelper.GetChild(root, index), predicate) is { } child) return child;
            return null;
        }

        bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (ReferenceEquals(parent, ancestor)) return true;
            return false;
        }

        void InvokeButton(Button button)
        {
            // Exercise the real XAML Click binding, without sending input to
            // another process or depending on the user's pointer position.
            var peer = new ButtonAutomationPeer(button);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Assert(invoke is not null && button.IsEnabled && button.Visibility == Visibility.Visible);
            invoke!.Invoke();
        }

        void AssertNoSidebarSelection()
        {
            Assert(MainNavigation.SelectedItem is null);
            foreach (var item in MainNavigation.MenuItems.Concat(MainNavigation.FooterMenuItems).OfType<NavigationViewItem>())
                Assert(!item.IsSelected);
            if (MainNavigation.SettingsItem is NavigationViewItem settings) Assert(!settings.IsSelected);
            Assert(Find<NavigationViewItem>(MainNavigation, item => item.IsSelected) is null);
        }

        void AssertCaptionHover()
        {
            var titleBar = AppWindow.TitleBar;
            Assert(titleBar.ButtonHoverBackgroundColor is { } hover
                && (hover.A == 0 || hover.R + hover.G + hover.B > 32));
            Assert(titleBar.ButtonPressedBackgroundColor is { } pressed
                && (pressed.A == 0 || pressed.R + pressed.G + pressed.B > 32));
            Assert(titleBar.ButtonHoverForegroundColor is { A: > 0 });
        }

        TextBlock TrackTitleFor(Track track)
        {
            var container = TrackList.ContainerFromItem(track) as ListViewItem;
            Assert(container is not null);
            var title = Find<TextBlock>(container!, text => Equals(text.Tag, "TrackTitle"));
            Assert(title is not null);
            return title!;
        }

        Button TrackLinkFor(Track track, string name)
        {
            var container = TrackList.ContainerFromItem(track) as ListViewItem;
            Assert(container is not null);
            var link = Find<Button>(container!, button => button.Name == name && ReferenceEquals(button.Tag, track));
            Assert(link is not null);
            return link!;
        }

        void AssertTrackColor(Track track, string brushKey)
        {
            Assert(TrackTitleFor(track).Foreground is SolidColorBrush actual
                && ThemeBrush(brushKey) is SolidColorBrush expected && actual.Color == expected.Color);
        }

        void AssertWindowedCaption()
        {
            Assert(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen && !_lyricsFullScreen);
            var header = Find<FrameworkElement>(NowPlaying, element => element.Name == "HeaderControls");
            var button = Find<Button>(NowPlaying, element => element.Name == "FullScreenButton");
            Assert(header is not null && button is not null && header.Margin.Right >= 146);
            var right = button!.TransformToVisual(NowPlaying).TransformPoint(new Point(button.ActualWidth, 0)).X;
            Assert(NowPlaying.ActualWidth - right >= 138);
            AssertCaptionHover();
        }

        void AssertCompactHeadingDoesNotCoverScrollBar()
        {
            var scrollbar = Find<ScrollBar>(TrackList, bar => bar.Orientation == Orientation.Vertical);
            var presenter = Find<ScrollContentPresenter>(TrackList, _ => true);
            Assert(scrollbar is { ActualWidth: > 0 } && presenter is not null);
            Assert(CompactCollectionHeader.Background is SolidColorBrush { Color.A: 0 });
            Assert(CompactCollectionHeader.Clip is RectangleGeometry && presenter!.Clip is RectangleGeometry);
            var overlayClip = ((RectangleGeometry)CompactCollectionHeader.Clip).Rect;
            var contentClip = ((RectangleGeometry)presenter!.Clip).Rect;
            var overlayRight = CompactCollectionHeader.TransformToVisual(TrackList)
                .TransformPoint(new Point(overlayClip.Right, 0)).X;
            var scrollbarLeft = scrollbar!.TransformToVisual(TrackList).TransformPoint(new Point()).X;
            Assert(overlayRight <= scrollbarLeft + .5);
            Assert(contentClip.Top >= CompactCollectionHeader.ActualHeight - .5
                && Math.Abs(contentClip.Bottom - presenter.ActualHeight) <= .5);
            Assert(GetRealizedTrackRowWidth(TrackList.ItemsPanelRoot) is { } rowWidth
                && Math.Abs(TrackHeader.Width - rowWidth) <= .5
                && Math.Abs(StickyTrackHeader.Width - rowWidth) <= .5);
            var arrowPoint = scrollbar.TransformToVisual(Root).TransformPoint(new Point(scrollbar.ActualWidth / 2, 8));
            Assert(!VisualTreeHelper.FindElementsInHostCoordinates(arrowPoint, Root)
                .Any(element => IsDescendantOf(element, CompactCollectionHeader)));
        }

        async Task CaptureAsync(string name)
        {
            Root.UpdateLayout();
            await Task.Delay(100, budget.Token);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Root);
            Assert(bitmap.PixelWidth > 100 && bitmap.PixelHeight > 100);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[checked((int)buffer.Length)];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var image = new byte[checked((int)stream.Size)];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)image.Length);
                reader.ReadBytes(image);
            }
            var filename = name + ".png";
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(reportPath)!, filename), image, budget.Token);
            screenshots.Add(filename);
        }

        try
        {
            var profile = App.DataDirectory;
            Assert(!string.IsNullOrWhiteSpace(profile) && Path.IsPathFullyQualified(profile!) && Path.IsPathFullyQualified(reportPath));
            profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile!));
            var realProfile = Path.TrimEndingDirectorySeparator(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaMusic"));
            Assert(!profile.Equals(realProfile, StringComparison.OrdinalIgnoreCase)
                && !profile.StartsWith(realProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !realProfile.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var testDirectory = Directory.GetParent(profile)?.FullName;
            Assert(testDirectory is not null && Path.GetFullPath(reportPath).StartsWith(testDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert(Directory.Exists(profile) && !File.Exists(Path.Combine(profile, "account.dat")));
            for (var directory = new DirectoryInfo(profile); directory is not null; directory = directory.Parent)
                Assert((directory.Attributes & FileAttributes.ReparsePoint) == 0);
            ownsInstance = true;
            checks.Add(step);
            await Task.Yield();

            step = "startup-without-tray";
            await WaitAsync(() => _startupReady && !_player.IsLoading && _player.CurrentTrack is not null, 3000);
            Assert(_tray is null && _account is null && !_player.IsPlaying);
            checks.Add(step);

            var current = _player.CurrentTrack!;
            var sampleTracks = new[] { current }.Concat(Enumerable.Range(1, 59).Select(index => new Track
            {
                Id = 30000 + index, Title = $"夜空中的旋律 · {index + 1:00}", Artist = "Luma Music",
                Album = "原生界面检查", DurationMs = 180000, Fee = index == 1 ? 1 : index == 2 ? 4 : 0,
                ArtworkUrl = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Luma.png")).AbsoluteUri
            })).ToArray();
            BeginPage("new");
            ShowTracks("本地", "60 首歌曲", sampleTracks);
            await WaitAsync(() => TrackList.ContainerFromItem(sampleTracks[1]) is ListViewItem);
            await Task.Delay(400, budget.Token);
            step = "track-access-badges";
            foreach (var (track, expectedLabel) in new[] { (sampleTracks[1], "VIP"), (sampleTracks[2], "付费") })
            {
                var row = (ListViewItem)TrackList.ContainerFromItem(track);
                var badge = Find<Border>(row, element => element.Name == "TrackAccessBadge");
                Assert(badge is { Visibility: Visibility.Visible, Child: TextBlock label } && label.Text == expectedLabel);
            }
            checks.Add(step);
            TrackList.SelectedItems.Add(sampleTracks[1]);
            TrackList.SelectedItems.Add(sampleTracks[2]);
            var selected = TrackList.SelectedItems.Cast<Track>().Select(track => track.Id).ToArray();

            foreach (var (theme, name) in new[]
            {
                (ElementTheme.Dark, "theme-dark"), (ElementTheme.Light, "theme-light"), (ElementTheme.Dark, "theme-dark-restored")
            })
            {
                step = name;
                Root.RequestedTheme = theme;
                await WaitAsync(() => Root.ActualTheme == theme);
                await Task.Delay(100, budget.Token);
                AssertTrackColor(current, "LumaAccentTextBrush");
                AssertTrackColor(sampleTracks[1], "LumaTextPrimaryBrush");
                Assert(TrackList.SelectedItems.Cast<Track>().Select(track => track.Id).SequenceEqual(selected));
                Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
                AssertCaptionHover();
                Root.Background = new SolidColorBrush(theme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(255, 13, 20, 35) : Windows.UI.Color.FromArgb(255, 237, 237, 242));
                if (name != "theme-dark-restored") await CaptureAsync(name);
                checks.Add(step);
            }

            step = "accent-settings";
            await RunAccentUiChecksAsync(checks, budget.Token);

            step = "compact-heading-preserves-scrollbar-and-surface";
            var originalSize = AppWindow.Size;
            var originalContentClip = Find<ScrollContentPresenter>(TrackList, _ => true)?.Clip;
            var initialScale = Root.XamlRoot.RasterizationScale;
            foreach (var width in new[] { 1000, 1280 })
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * initialScale), (int)(720 * initialScale)));
                await WaitAsync(() => _trackListScroller is { ScrollableHeight: > 600 });
                await Task.Delay(180, budget.Token);
                _trackListScroller!.ChangeView(null, 600, null, true);
                await WaitAsync(() => _collectionHeaderCompact && CompactCollectionHeader.Visibility == Visibility.Visible);
                await Task.Delay(250, budget.Token);
                AssertCompactHeadingDoesNotCoverScrollBar();
            }
            Root.RequestedTheme = ElementTheme.Light;
            await WaitAsync(() => Root.ActualTheme == ElementTheme.Light);
            AssertCompactHeadingDoesNotCoverScrollBar();
            Root.RequestedTheme = ElementTheme.Dark;
            await WaitAsync(() => Root.ActualTheme == ElementTheme.Dark);
            await CaptureAsync("compact-heading-scrollbar");
            checks.Add(step);

            step = "compact-heading-interruption-and-page-reset";
            _trackListScroller!.ChangeView(null, 0, null, true);
            await WaitAsync(() => !_collectionHeaderCompact);
            _trackListScroller.ChangeView(null, 600, null, true);
            await WaitAsync(() => _collectionHeaderCompact);
            await Task.Delay(250, budget.Token);
            AssertCompactHeadingDoesNotCoverScrollBar();
            BeginPage("recent");
            ShowTracks("最近播放", "2 首歌曲", sampleTracks.Take(2));
            AppWindow.Resize(originalSize);
            await WaitAsync(() => !_collectionHeaderCompact && CompactCollectionHeader.Visibility == Visibility.Collapsed);
            await Task.Delay(250, budget.Token);
            var resetPresenter = Find<ScrollContentPresenter>(TrackList, _ => true);
            Assert(resetPresenter is not null && ReferenceEquals(resetPresenter.Clip, originalContentClip));
            Assert(_trackListScroller is { VerticalOffset: < 1 });
            checks.Add(step);

            step = "playback-mode-cycle";
            _player.Shuffle = false;
            _player.Repeat = RepeatMode.Off;
            Repeat_Click(RepeatButton, new RoutedEventArgs());
            Assert(!_player.Shuffle && _player.Repeat == RepeatMode.All);
            Repeat_Click(RepeatButton, new RoutedEventArgs());
            Assert(!_player.Shuffle && _player.Repeat == RepeatMode.One);
            Repeat_Click(RepeatButton, new RoutedEventArgs());
            Assert(_player.Shuffle);
            Repeat_Click(RepeatButton, new RoutedEventArgs());
            Assert(!_player.Shuffle && _player.Repeat == RepeatMode.Off);
            checks.Add(step);

            step = "bottom-metadata-opens-lyrics";
            Assert(IsDescendantOf(PlayerTitle, PlayerInfoButton)
                && IsDescendantOf(PlayerArtist, PlayerArtistButton)
                && IsDescendantOf(PlayerCoverBorder, PlayerInfoButton));
            Assert(Find<Button>(PlayerInfoButton, button => !ReferenceEquals(button, PlayerInfoButton)) is null);
            Assert(!IsDescendantOf(PlayerArtistButton, PlayerInfoButton));
            BeginPage("discover");
            Assert(ReferenceEquals(MainNavigation.SelectedItem, DiscoverNav) && DiscoverNav.IsSelected);
            InvokeButton(PlayerInfoButton);
            await WaitAsync(() => _immersiveOpen && NowPlaying.Visibility == Visibility.Visible);
            Assert(_page == "discover" && ReferenceEquals(MainNavigation.SelectedItem, DiscoverNav));
            Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            checks.Add(step);
            await Task.Delay(650, budget.Token);
            step = "windowed-caption-reservation";
            AssertWindowedCaption();
            checks.Add(step);

            step = "maximize-restore-caption";
            Assert(AppWindow.Presenter is OverlappedPresenter);
            ((OverlappedPresenter)AppWindow.Presenter).Maximize();
            await Task.Delay(180, budget.Token);
            AssertWindowedCaption();
            ((OverlappedPresenter)AppWindow.Presenter).Restore();
            await Task.Delay(180, budget.Token);
            AssertWindowedCaption();
            checks.Add(step);

            step = "fullscreen-presenter-synchronization";
            SetLyricsFullScreen(true);
            await WaitAsync(() => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen && _lyricsFullScreen);
            checks.Add(step);

            step = "external-presenter-restore-caption";
            // Simulate a native restore initiated outside SetLyricsFullScreen.
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            await WaitAsync(() => !_lyricsFullScreen);
            await Task.Delay(180, budget.Token);
            AssertWindowedCaption();
            checks.Add(step);

            step = "restored-window-resize-caption";
            var scale = Root.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(680 * scale)));
            await Task.Delay(180, budget.Token);
            AssertWindowedCaption();
            await CaptureAsync("window-restored");
            checks.Add(step);
            step = "artist-link-resolves-legacy-track";
            var artistButton = Find<Button>(NowPlaying, button => button.Name == "TrackArtistButton");
            Assert(artistButton is not null && current.Artists.Count == 0);
            InvokeButton(artistButton!);
            await WaitAsync(() => _page == "artist" && !_pageLoading && ArtistView.Visibility == Visibility.Visible
                && NowPlaying.Visibility == Visibility.Collapsed);
            Assert(_artistId == 7001 && _tracks.Count == 2 && ArtistPageTitle.Text == "Luma Artist");
            Assert(!_player.IsPlaying);
            AssertNoSidebarSelection();
            await CaptureAsync("artist-songs");
            checks.Add(step);

            step = "artist-albums-and-album-tracks";
            await SelectArtistAlbumsAsync();
            await WaitAsync(() => _artistAlbumsLoaded && !_artistPaging);
            Assert(_artistAlbums.Count == 1 && _artistAlbums[0].Id == 8001);
            await CaptureAsync("artist-albums");
            await OpenAlbumAsync(8001);
            Assert(_page == "album" && _tracks.Count == 2 && PageTitle.Text == "Luma Album");
            Assert(AlbumArtistBackButton.Visibility == Visibility.Visible);
            AssertNoSidebarSelection();
            checks.Add(step);

            step = "lyrics-artist-route";
            BeginPage("recent");
            ShowTracks("最近播放", "2 首歌曲", sampleTracks.Take(2));
            Assert(ReferenceEquals(MainNavigation.SelectedItem, RecentNav) && RecentNav.IsSelected);
            OpenNowPlaying();
            await WaitAsync(() => NowPlaying.Visibility == Visibility.Visible);
            InvokeButton(artistButton!);
            await WaitAsync(() => _page == "artist" && !_pageLoading && !_immersiveOpen);
            Assert(_artistId == 7001);
            AssertNoSidebarSelection();
            checks.Add(step);

            step = "list-artist-link-does-not-play";
            await WaitAsync(() => NowPlaying.Visibility == Visibility.Collapsed);
            var linkedTrack = new Track { Id = 12346, Title = "列表链接测试", Artist = "Luma Artist", Album = "Luma Album", DurationMs = 180000 };
            var unidentifiedArtistTrack = new Track { Id = 12348, Title = "未署名作品", Artist = "", Album = "Luma Album", AlbumId = 8001, DurationMs = 180000 };
            var queueBeforeLinks = _player.Queue.Select(track => track.Id).ToArray();
            BeginPage("playlist");
            ShowTracks("歌单链接测试", "2 首歌曲", new[] { linkedTrack, unidentifiedArtistTrack });
            await WaitAsync(() => TrackList.ContainerFromItem(linkedTrack) is ListViewItem);
            InvokeButton(TrackLinkFor(linkedTrack, "TrackArtistLink"));
            await WaitAsync(() => _page == "artist" && !_pageLoading);
            Assert(_artistId == 7001 && linkedTrack.Artists.FirstOrDefault()?.Id == 7001);
            Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            Assert(_player.Queue.Select(track => track.Id).SequenceEqual(queueBeforeLinks));
            AssertNoSidebarSelection();
            checks.Add(step);

            step = "list-album-link-resolves-legacy-metadata";
            BeginPage("search");
            ShowTracks("搜索结果", "2 首歌曲", new[] { linkedTrack, unidentifiedArtistTrack });
            await WaitAsync(() => TrackList.ContainerFromItem(linkedTrack) is ListViewItem);
            InvokeButton(TrackLinkFor(linkedTrack, "TrackAlbumLink"));
            await WaitAsync(() => _page == "album" && !_pageLoading);
            Assert(linkedTrack.AlbumId == 8001 && _artistAlbumId == 8001 && _albumOriginArtistId == 7001);
            Assert(AlbumArtistBackButton.Visibility == Visibility.Visible);
            Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            Assert(_player.Queue.Select(track => track.Id).SequenceEqual(queueBeforeLinks));
            AssertNoSidebarSelection();
            checks.Add(step);

            step = "list-album-does-not-inherit-unrelated-artist";
            BeginPage("search");
            ShowTracks("搜索结果", "1 首歌曲", new[] { unidentifiedArtistTrack });
            await WaitAsync(() => TrackList.ContainerFromItem(unidentifiedArtistTrack) is ListViewItem);
            InvokeButton(TrackLinkFor(unidentifiedArtistTrack, "TrackAlbumLink"));
            await WaitAsync(() => _page == "album" && !_pageLoading);
            Assert(_albumOriginArtistId == 0 && AlbumArtistBackButton.Visibility == Visibility.Collapsed);
            Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            checks.Add(step);

            step = "list-link-cancels-after-navigation";
            var pendingAlbum = OpenTrackAlbumAsync(new Track { Id = 12347, Album = "Luma Album" });
            BeginPage("recent");
            await pendingAlbum;
            Assert(_page == "recent" && _player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            checks.Add(step);

            step = "text-only-track-links-and-circular-locator";
            await RunTrackLinkHitChecksAsync();
            checks.Add(step);

            step = "playlist-first-page-only";
            await OpenPlaylistAsync(888);
            await WaitAsync(() => !_pageLoading && TracksView.Visibility == Visibility.Visible && _tracks.Count == 240);
            Assert(_tracks.Count(track => track.IsMetadataLoaded) == 60);
            Assert(_tracks[185].Id == current.Id && _tracks[185].IsMetadataLoading);
            await WaitAsync(() => LocateCurrentButton.Visibility == Visibility.Visible);
            checks.Add(step);

            step = "playlist-locate-loads-target-page-only";
            InvokeButton(LocateCurrentButton);
            await WaitAsync(() => _tracks[185].IsMetadataLoaded && !_locatingCurrent
                && TrackList.ContainerFromItem(_tracks[185]) is ListViewItem && _trackListScroller!.VerticalOffset > 5000);
            await Task.Delay(350, budget.Token);
            Assert(_tracks.Skip(60).Take(120).All(track => track.IsMetadataLoading));
            var locatedRow = (ListViewItem)TrackList.ContainerFromItem(_tracks[185]);
            var locatedY = locatedRow.TransformToVisual(_trackListScroller).TransformPoint(new Point()).Y;
            Assert(locatedY >= CompactCollectionHeader.ActualHeight - 1 && locatedY < _trackListScroller!.ViewportHeight);
            Assert(_player.CurrentTrack?.Id == current.Id && !_player.IsPlaying);
            checks.Add(step);

            step = "playlist-scroll-lazyloads-visible-page";
            TrackList.ScrollIntoView(_tracks[75], ScrollIntoViewAlignment.Leading);
            await WaitAsync(() => _tracks[75].IsMetadataLoaded);
            Assert(_tracks[140].IsMetadataLoading);
            checks.Add(step);

            step = "single-track-starts-before-remote-queue-and-keeps-audio";
            var expandingPlayback = PlayTrackFromListAsync(_tracks[2], TrackList);
            await WaitAsync(() => _player.CurrentTrack?.Id == 40002);
            Assert(_tracks[140].IsMetadataLoading && _player.Queue.Count < 240 && !expandingPlayback.IsCompleted);
            var nativePlayerField = _player.GetType().GetField("_player", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert(nativePlayerField is not null);
            var nativePlayerBeforeExpansion = nativePlayerField!.GetValue(_player);
            BeginPage("recent");
            ShowTracks("最近播放", "1 首歌曲", new[] { linkedTrack });
            await expandingPlayback;
            Assert(_player.Queue.Count == 240 && _player.CurrentTrack?.Id == 40002
                && ReferenceEquals(nativePlayerBeforeExpansion, nativePlayerField.GetValue(_player)));
            checks.Add(step);

            step = "background-queue-expansion-preserves-user-edits";
            await OpenPlaylistAsync(888);
            var editedExpansion = PlayTrackFromListAsync(_tracks[3], TrackList);
            await WaitAsync(() => _player.CurrentTrack?.Id == 40003);
            _player.AddToQueue(new Track { Id = 77777, Title = "用户手动加入的歌曲", Artist = "Luma Music" });
            var editedQueueIds = _player.Queue.Select(track => track.Id).ToArray();
            await editedExpansion;
            Assert(_player.CurrentTrack?.Id == 40003 && _player.Queue.Select(track => track.Id).SequenceEqual(editedQueueIds));
            checks.Add(step);

            step = "new-play-intent-cancels-old-playlist-hydration";
            await OpenPlaylistAsync(888);
            var olderPlayback = PlayTracksWithIntentAsync(_tracks[0], _tracks);
            Assert(_playIntentPreparing && !PlayAllButton.IsEnabled);
            await PlayTracksWithIntentAsync(_tracks[1], new[] { _tracks[1] });
            await olderPlayback;
            await Task.Delay(450, budget.Token);
            Assert(_player.CurrentTrack?.Id == 40001 && _player.Queue.Count == 1);
            Assert(!_playIntentPreparing && PlayAllButton.IsEnabled);
            if (_player.IsPlaying) _player.TogglePlayPause();
            await WaitAsync(() => !_player.IsPlaying);
            checks.Add(step);

            step = "playlist-play-all-keeps-entire-queue";
            _trackListScroller!.ChangeView(null, 0, null, true);
            await WaitAsync(() => !_collectionHeaderCompact);
            InvokeButton(PlayAllButton);
            await WaitAsync(() => _player.Queue.Count == 240 && !_player.IsLoading && _player.CurrentTrack?.Id == 40000, 3000);
            Assert(_player.Queue.All(track => track.IsMetadataLoaded && !string.IsNullOrWhiteSpace(track.Title)));
            Assert(_player.Queue[185].Id == current.Id);
            if (_player.IsPlaying) _player.TogglePlayPause();
            await WaitAsync(() => !_player.IsPlaying);
            BeginPage("search");
            ShowTracks("搜索结果", "1 首歌曲", new[] { linkedTrack });
            await WaitAsync(() => LocateCurrentButton.Visibility == Visibility.Collapsed);
            checks.Add(step);

            step = "lyric-presentation-and-controls";
            await RunLyricPresentationChecksAsync();
            checks.Add(step);
            success = true;
        }
        catch (Exception error)
        {
            errorType = error.GetType().Name;
            errorMessage = error.Message;
            var target = _tracks.FirstOrDefault(track => track.Id == _player.CurrentTrack?.Id);
            var container = target is null ? null : TrackList.ContainerFromItem(target) as ListViewItem;
            var panel = TrackList.ItemsPanelRoot as ItemsStackPanel;
            failureDetails = new
            {
                page = _page, pageLoading = _pageLoading, currentId = _player.CurrentTrack?.Id,
                locating = _locatingCurrent, locatorVisible = LocateCurrentButton.Visibility.ToString(), locatorEnabled = LocateCurrentButton.IsEnabled,
                tracks = _tracks.Count, loadedTracks = _tracks.Count(track => track.IsMetadataLoaded),
                targetIndex = target is null ? -1 : _tracks.IndexOf(target), targetLoaded = target?.IsMetadataLoaded,
                targetRealized = container is not null, targetHeight = container?.ActualHeight,
                targetY = container is null || _trackListScroller is null ? (double?)null : container.TransformToVisual(_trackListScroller).TransformPoint(new Point()).Y,
                offset = _trackListScroller?.VerticalOffset, scrollable = _trackListScroller?.ScrollableHeight,
                viewport = _trackListScroller?.ViewportHeight, firstVisible = panel?.FirstVisibleIndex, lastVisible = panel?.LastVisibleIndex,
                compactHeader = _collectionHeaderCompact, resetScroll = _resetCollectionScroll,
                playlistScrolling = _playlistScrollInProgress, playlistSettling = _playlistViewportSettling,
                noticeTitle = Notice.Title, noticeMessage = Notice.Message
            };
        }
        finally
        {
            if (ownsInstance)
            {
                try
                {
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                    {
                        result = success ? "Passed" : "Failed", checks, failedStep = success ? null : step,
                        errorType = success ? null : errorType, errorMessage = success ? null : errorMessage,
                        failureDetails, screenshots, elapsedMs = clock.ElapsedMilliseconds
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { Close(); }
            }
        }
    }
}
