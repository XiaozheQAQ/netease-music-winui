using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var success = false;
        var ownsInstance = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));

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

        TextBlock TrackTitleFor(Track track)
        {
            var container = TrackList.ContainerFromItem(track) as ListViewItem;
            Assert(container is not null);
            var title = Find<TextBlock>(container!, text => Equals(text.Tag, "TrackTitle"));
            Assert(title is not null);
            return title!;
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
                Album = "原生界面检查", DurationMs = 180000,
                ArtworkUrl = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Luma.png")).AbsoluteUri
            })).ToArray();
            BeginPage("new");
            ShowTracks("本地", "60 首歌曲", sampleTracks);
            await WaitAsync(() => TrackList.ContainerFromItem(sampleTracks[1]) is ListViewItem);
            await Task.Delay(400, budget.Token);
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
                Root.Background = new SolidColorBrush(theme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(255, 13, 20, 35) : Windows.UI.Color.FromArgb(255, 237, 237, 242));
                if (name != "theme-dark-restored") await CaptureAsync(name);
                checks.Add(step);
            }

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

            OpenNowPlaying();
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
            CloseNowPlaying();
            await WaitAsync(() => NowPlaying.Visibility == Visibility.Collapsed);

            step = "artist-link-resolves-legacy-track";
            await OpenCurrentTrackArtistAsync(PlayerArtistButton);
            await WaitAsync(() => _page == "artist" && !_pageLoading && ArtistView.Visibility == Visibility.Visible);
            Assert(_artistId == 7001 && _tracks.Count == 2 && ArtistPageTitle.Text == "Luma Artist");
            Assert(!_player.IsPlaying);
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
            checks.Add(step);

            step = "lyrics-artist-route";
            OpenNowPlaying();
            await OpenCurrentTrackArtistAsync(NowPlaying);
            await WaitAsync(() => _page == "artist" && !_immersiveOpen);
            Assert(_artistId == 7001);
            checks.Add(step);

            success = true;
        }
        catch (Exception error) { errorType = error.GetType().Name; }
        finally
        {
            if (ownsInstance)
            {
                try
                {
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                    {
                        result = success ? "Passed" : "Failed", checks, failedStep = success ? null : step,
                        errorType = success ? null : errorType, screenshots, elapsedMs = clock.ElapsedMilliseconds
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { Close(); }
            }
        }
    }
}
