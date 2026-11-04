using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using winuitest.Controls;
using winuitest.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using winuitest.Services;

namespace winuitest;

// Linked only by -p:LumaNativeChecks=true. The normal app has no test implementation.
public sealed partial class MainWindow
{
    async partial void RunTrayDiagnostics()
    {
        var reportPath = Environment.GetEnvironmentVariable("LUMA_NATIVE_CHECK_REPORT");
        var dataDirectory = App.DataDirectory;
        if (string.IsNullOrWhiteSpace(reportPath) || string.IsNullOrWhiteSpace(dataDirectory)) return;

        var clock = Stopwatch.StartNew();
        var passed = new List<string>();
        var step = "isolated-profile";
        var failureType = string.Empty;
        var outputCount = 0;
        var screenshots = new List<string>();
        var alignmentDiagnostics = new List<object>();
        var nativeMenuDiagnostics = new List<object>();
        var trayErrorMessages = new List<string>();
        var ownsTestInstance = false;
        var trayError = false;
        var playbackError = false;
        var success = false;
        EventHandler<string> onTrayError = (_, message) =>
        {
            trayError = true;
            // Preserve our own focus diagnostic, never endpoint/account errors.
            trayErrorMessages.Add(message.StartsWith("Windows 暂未允许打开托盘菜单", StringComparison.Ordinal)
                ? message : "Native tray operation failed (external details omitted).");
        };
        EventHandler<string> onPlaybackError = (_, _) => playbackError = true;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        void Assert(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Native integration assertion failed.");
        }

        void AssertHealthy()
        {
            Assert(!_closed && !trayError && !playbackError);
            Assert(!Notice.IsOpen || !Notice.Title.Contains("托盘", StringComparison.Ordinal));
        }

        async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            var waiting = Stopwatch.StartNew();
            while (!predicate())
            {
                AssertHealthy();
                if (waiting.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException();
                await Task.Delay(25, budget.Token);
            }
            AssertHealthy();
        }

        T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindVisual<T>(VisualTreeHelper.GetChild(parent, index)) is { } descendant) return descendant;
            return null;
        }

        async Task CaptureAsync(string name)
        {
            Root.UpdateLayout();
            await Task.Delay(120, budget.Token);
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

        void AssertColumnAlignment(Track sample, bool compact)
        {
            Root.UpdateLayout();
            var container = TrackList.ContainerFromItem(sample) as ListViewItem;
            Assert(container is not null);
            var row = FindVisual<TrackColumnsGrid>(container!);
            var heading = compact ? StickyTrackHeader : TrackHeader;
            Assert(row is not null);
            var aligned = Math.Abs(row!.ActualWidth - heading.ActualWidth) < 1.5;
            var fields = new List<object>();
            foreach (var field in new[] { TrackField.Title, TrackField.Album, TrackField.Duration })
            {
                var cell = row.Children.OfType<FrameworkElement>().First(child => TrackColumnsGrid.GetField(child) == field);
                var label = heading.Children.OfType<FrameworkElement>().First(child => TrackColumnsGrid.GetField(child) == field);
                aligned &= cell.Visibility == label.Visibility;
                var cellX = cell.TransformToVisual(TracksView).TransformPoint(new Point()).X;
                var labelX = label.TransformToVisual(TracksView).TransformPoint(new Point()).X;
                var cellEdge = cellX + (field == TrackField.Duration ? cell.ActualWidth : 0);
                var labelEdge = labelX + (field == TrackField.Duration ? label.ActualWidth : 0);
                fields.Add(new { field = field.ToString(), cellX, labelX, cellWidth = cell.ActualWidth,
                    labelWidth = label.ActualWidth, cellEdge, labelEdge, difference = cellEdge - labelEdge,
                    cellVisibility = cell.Visibility.ToString(), labelVisibility = label.Visibility.ToString() });
                if (cell.Visibility == Visibility.Visible) aligned &= Math.Abs(cellEdge - labelEdge) < 1.5;
            }
            alignmentDiagnostics.Add(new
            {
                step, compact, rootWidth = Root.ActualWidth, listWidth = TrackList.ActualWidth,
                viewportWidth = _trackListScroller?.ViewportWidth,
                rowWidth = row.ActualWidth, headerWidth = heading.ActualWidth,
                rowX = row.TransformToVisual(TracksView).TransformPoint(new Point()).X,
                headerX = heading.TransformToVisual(TracksView).TransformPoint(new Point()).X,
                itemWidth = container!.ActualWidth, itemPadding = container.Padding.ToString(),
                itemBorder = container.BorderThickness.ToString(), fields
            });
            Assert(aligned);
        }

        async Task CheckNativeMenuAsync(bool deactivate)
        {
            Assert(_tray is not null && !_tray.IsMenuOpen);
            var observed = false;
            var actionSent = false;
            var fallback = false;
            var timerError = false;
            var ownerWasForeground = false;
            var elapsed = Stopwatch.StartNew();
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(60);
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (_tray!.IsMenuOpen)
                    {
                        observed = true;
                        ownerWasForeground |= TrayNative.GetForegroundWindow() == _tray.MenuOwnerHandle;
                        if (!actionSent && elapsed.ElapsedMilliseconds >= 180)
                        {
                            actionSent = true;
                            if (deactivate) Activate();
                            else _tray.DismissMenu();
                        }
                        if (elapsed.ElapsedMilliseconds > 1800)
                        {
                            fallback = true;
                            _tray.DismissMenu();
                        }
                    }
                    else if (observed || elapsed.ElapsedMilliseconds > 2500)
                    {
                        timer.Stop();
                        completed.TrySetResult(observed && actionSent && !fallback);
                    }
                }
                catch
                {
                    timerError = true;
                    _tray?.DismissMenu();
                    timer.Stop();
                    completed.TrySetResult(false);
                }
            };
            timer.Start();
            try
            {
                _tray!.ShowMenu();
                Assert(await completed.Task.WaitAsync(budget.Token) && !timerError && !_tray.IsMenuOpen);
                AssertHealthy();
            }
            finally
            {
                timer.Stop();
                nativeMenuDiagnostics.Add(new
                {
                    step, deactivate, observed, actionSent, fallback, timerError, ownerWasForeground,
                    elapsedMs = elapsed.ElapsedMilliseconds,
                    stage = _tray?.LastMenuStage, foregroundAccepted = _tray?.LastMenuForegroundAccepted,
                    ownerVisible = _tray?.LastMenuOwnerVisible, trayError
                });
                _tray?.DismissMenu();
            }
        }

        try
        {
            Assert(Path.IsPathFullyQualified(dataDirectory) && Path.IsPathFullyQualified(reportPath));
            var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            var realProfile = Path.TrimEndingDirectorySeparator(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaMusic"));
            Assert(!string.Equals(profile, realProfile, StringComparison.OrdinalIgnoreCase));
            Assert(!profile.StartsWith(realProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert(!realProfile.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var testDirectory = Directory.GetParent(profile)?.FullName;
            Assert(testDirectory is not null && Path.GetFullPath(reportPath).StartsWith(
                testDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert(Directory.Exists(profile) && !File.Exists(Path.Combine(profile, "account.dat")));
            // A reparse-point profile could alias the real listener's directory.
            for (var directory = new DirectoryInfo(profile); directory is not null; directory = directory.Parent)
                Assert((directory.Attributes & FileAttributes.ReparsePoint) == 0);
            ownsTestInstance = true;
            passed.Add(step);

            // Let InitializeTray and the final startup layout return before exercising commands.
            await Task.Yield();
            step = "tray-registered";
            Assert(_tray is { IsAvailable: true });
            _tray!.Error += onTrayError;
            _player.Error += onPlaybackError;
            passed.Add(step);

            step = "fixture-ready";
            await WaitUntilAsync(() => _startupReady && !_player.IsLoading, 3000);
            Assert(_account is null && !_loginDialogOpen && !_player.IsPlaying);
            var queue = _player.Queue.Select(track => track.Id).ToArray();
            Assert(queue.Length == 2 && queue[0] != queue[1] && _player.CurrentTrack?.Id == queue[0]);
            Assert(!_player.Shuffle);
            var initialTrackId = _player.CurrentTrack!.Id;
            var initialPosition = _player.GetLyricPosition();
            passed.Add(step);

            step = "device-enumeration";
            var outputs = await _player.GetAudioOutputsAsync(budget.Token);
            outputCount = outputs.Count;
            Assert(outputs.Count > 0 && outputs.All(output => output.Id.Length > 0));
            passed.Add(step);

            step = "device-selection-preserves-playback";
            await _player.SelectAudioOutputAsync(outputs[0].Id, budget.Token);
            Assert(string.Equals(_player.SelectedAudioOutputId, outputs[0].Id, StringComparison.OrdinalIgnoreCase));
            Assert(_player.CurrentTrack?.Id == initialTrackId && !_player.IsPlaying);
            Assert(Math.Abs((_player.GetLyricPosition() - initialPosition).TotalMilliseconds) <= 250);
            Assert(_player.Queue.Select(track => track.Id).SequenceEqual(queue));
            AssertHealthy();
            passed.Add(step);

            step = "system-default-output";
            await _player.SelectAudioOutputAsync(null, budget.Token);
            Assert(_player.SelectedAudioOutputId.Length == 0 && !_player.IsPlaying);
            Assert(_player.CurrentTrack?.Id == initialTrackId);
            Assert(Math.Abs((_player.GetLyricPosition() - initialPosition).TotalMilliseconds) <= 250);
            passed.Add(step);

            step = "playback-start";
            _player.TogglePlayPause();
            await WaitUntilAsync(() => _player.IsPlaying);
            passed.Add(step);

            step = "next-track";
            await ExecuteTrayCommandAsync(TrayCommand.Next).WaitAsync(budget.Token);
            await WaitUntilAsync(() => _player.CurrentTrack?.Id == queue[1] && _player.IsPlaying && !_player.IsLoading);
            passed.Add(step);

            step = "previous-track";
            // Previous seeks to zero after three seconds. Explicitly exercise queue navigation.
            _player.Seek(0);
            await ExecuteTrayCommandAsync(TrayCommand.Previous).WaitAsync(budget.Token);
            await WaitUntilAsync(() => _player.CurrentTrack?.Id == initialTrackId && _player.IsPlaying && !_player.IsLoading);
            passed.Add(step);

            step = "playback-paused";
            await ExecuteTrayCommandAsync(TrayCommand.PlayPause);
            await WaitUntilAsync(() => !_player.IsPlaying && !_player.IsLoading);
            passed.Add(step);

            foreach (var (command, page, check) in new[]
            {
                (TrayCommand.Settings, "settings", "settings-page"),
                (TrayCommand.Favorites, "favorites", "favorites-page"),
                (TrayCommand.Playlists, "cloud", "playlists-page")
            })
            {
                step = check;
                await ExecuteTrayCommandAsync(command).WaitAsync(budget.Token);
                Assert(_page == page && !_immersiveOpen && !_loginDialogOpen);
                AssertHealthy();
                passed.Add(step);
            }

            step = "lyrics-page";
            await ExecuteTrayCommandAsync(TrayCommand.NowPlaying);
            Assert(_immersiveOpen && !_loginDialogOpen && _player.CurrentTrack?.Id == initialTrackId);
            AssertHealthy();
            passed.Add(step);

            step = "lyric-seek-resumes-playback";
            SeekLyricAndPlay(30);
            await WaitUntilAsync(() => _player.IsPlaying && _player.GetLyricPosition().TotalSeconds >= 29.5);
            _player.TogglePlayPause();
            await WaitUntilAsync(() => !_player.IsPlaying);
            passed.Add(step);

            step = "slider-seek-preserves-pause";
            SeekTo(45);
            await Task.Delay(180, budget.Token);
            Assert(!_player.IsPlaying && Math.Abs(_player.GetLyricPosition().TotalSeconds - 45) < .5);
            passed.Add(step);

            step = "lyrics-close-completes";
            CloseNowPlaying();
            await WaitUntilAsync(() => NowPlaying.Visibility == Visibility.Collapsed, 1500);
            passed.Add(step);
            // Render only this isolated app's XAML tree. An opaque test underlay
            // stands in for Mica, which RenderTargetBitmap cannot capture.
            Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 13, 20, 35) : Windows.UI.Color.FromArgb(255, 237, 237, 242));
            var testArtwork = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Luma.png")).AbsoluteUri;
            var sampleTracks = Enumerable.Range(0, 60).Select(index => new Track
            {
                Id = 20000 + index,
                Title = index % 3 == 0 ? $"ルカルカ★ナイトフィーバー · {index + 1:00}" : $"夜空中的旋律 · {index + 1:00}",
                Artist = index % 2 == 0 ? "巡音ルカ / Luma Music" : "Luma Music",
                Album = "Luma · Native UI Collection", DurationMs = 180000, ArtworkUrl = testArtwork
            }).ToArray();
            step = "list-fixture-layout";
            BeginPage("playlist");
            _playlistId = 789;
            ShowTracks("我喜欢的音乐", "60 首歌曲", sampleTracks);
            SetActivePlaylist(new PlaylistDetail { Id = 789, Title = "我喜欢的音乐", Tracks = sampleTracks.ToList() });
            SetImage(CollectionCover, testArtwork);
            CollectionCoverBorder.Visibility = Visibility.Visible;
            await Task.Delay(550, budget.Token);
            AttachTrackListScroller();
            await CaptureAsync("list-fixture");
            await WaitUntilAsync(() => _trackListScroller is { ScrollableHeight: > 500 });
            passed.Add(step);

            step = "list-single-selection-does-not-play";
            Assert(TrackList.SelectionMode == ListViewSelectionMode.Extended && !TrackList.IsItemClickEnabled);
            TrackList.SelectedItem = sampleTracks[3];
            await Task.Delay(100, budget.Token);
            Assert(!_player.IsPlaying && _player.CurrentTrack?.Id == initialTrackId);
            passed.Add(step);

            step = "list-playing-state-preserves-multiselection";
            TrackList.SelectedItems.Add(sampleTracks[5]);
            var beforeSelection = TrackList.SelectedItems.Cast<Track>().Select(track => track.Id).Order().ToArray();
            await _player.PlayAsync(sampleTracks[0], sampleTracks).WaitAsync(budget.Token);
            await WaitUntilAsync(() => _player.CurrentTrack?.Id == sampleTracks[0].Id && _player.IsPlaying);
            Assert(TrackList.SelectedItems.Cast<Track>().Select(track => track.Id).Order().SequenceEqual(beforeSelection));
            _player.TogglePlayPause();
            await WaitUntilAsync(() => !_player.IsPlaying);
            passed.Add(step);

            step = "list-context-selection";
            Assert(CaptureTrackSelection(TrackList, sampleTracks[5]).Select(track => track.Id)
                .SequenceEqual(new[] { sampleTracks[3].Id, sampleTracks[5].Id }));
            Assert(CaptureTrackSelection(TrackList, sampleTracks[8]).Select(track => track.Id)
                .SequenceEqual(new[] { sampleTracks[8].Id }));
            Assert(TrackList.SelectedItems.Count == 1);
            passed.Add(step);

            step = "list-double-action-plays";
            await PlayTrackFromListAsync(sampleTracks[2], TrackList).WaitAsync(budget.Token);
            await WaitUntilAsync(() => _player.CurrentTrack?.Id == sampleTracks[2].Id && _player.IsPlaying);
            Assert(_player.Queue.Count == sampleTracks.Length);
            _player.TogglePlayPause();
            await WaitUntilAsync(() => !_player.IsPlaying);
            passed.Add(step);

            var scale = Root.XamlRoot.RasterizationScale;
            step = "list-wide-column-alignment";
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1120 * scale), (int)(740 * scale)));
            TrackList.SelectedItems.Clear();
            _trackListScroller!.ChangeView(null, 0, null, true);
            await Task.Delay(450, budget.Token);
            UpdateTrackHeadingWidth();
            await CaptureAsync("list-expanded");
            AssertColumnAlignment(sampleTracks[0], compact: false);
            Assert(!_collectionHeaderCompact);
            passed.Add(step);

            step = "list-sticky-header-stable-extent";
            var extent = _trackListScroller.ExtentHeight;
            _trackListScroller.ChangeView(null, 310, null, true);
            await WaitUntilAsync(() => _collectionHeaderCompact && _trackListScroller.VerticalOffset >= 309);
            await Task.Delay(320, budget.Token);
            Assert(Math.Abs(_trackListScroller.ExtentHeight - extent) < 1.5);
            Assert(CompactCollectionHeader.Visibility == Visibility.Visible && CompactCollectionHeader.IsHitTestVisible);
            await CaptureAsync("list-compact");
            AssertColumnAlignment(sampleTracks[5], compact: true);
            passed.Add(step);

            step = "list-sticky-header-restores-expanded";
            _trackListScroller.ChangeView(null, 0, null, true);
            await WaitUntilAsync(() => !_collectionHeaderCompact && CompactCollectionHeader.Visibility == Visibility.Collapsed);
            Assert(Math.Abs(_trackListScroller.ExtentHeight - extent) < 1.5);
            passed.Add(step);

            step = "list-narrow-column-alignment";
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(940 * scale), (int)(660 * scale)));
            await Task.Delay(450, budget.Token);
            UpdateTrackHeadingWidth();
            await CaptureAsync("list-narrow");
            AssertColumnAlignment(sampleTracks[0], compact: false);
            Assert(PlayerInfoColumn.ActualWidth > 0 && PlayerTransportColumn.ActualWidth > 0);
            passed.Add(step);

            step = "native-tray-menu";
            await CheckNativeMenuAsync(deactivate: false);
            passed.Add(step);
            step = "native-tray-menu-reopens";
            await CheckNativeMenuAsync(deactivate: false);
            passed.Add(step);
            step = "native-tray-dismisses-on-deactivation";
            await CheckNativeMenuAsync(deactivate: true);
            passed.Add(step);
            success = true;
        }
        catch (Exception error)
        {
            // The report never includes native exception messages, device identities or account data.
            failureType = error.GetType().Name;
        }
        finally
        {
            if (ownsTestInstance)
            {
                _player.Error -= onPlaybackError;
                if (_tray is not null) _tray.Error -= onTrayError;
                try
                {
                    _tray?.Dispose();
                    Assert(_tray is null || !_tray.IsAvailable);
                    passed.Add("tray-disposed");
                }
                catch (Exception error) { success = false; step = "tray-disposed"; failureType = error.GetType().Name; }
                try
                {
                    var report = new
                    {
                        result = success ? "Passed" : "Failed", checks = passed, failedStep = success ? null : step,
                        errorType = success ? null : failureType, audioOutputCount = outputCount,
                        screenshots, alignmentDiagnostics, nativeMenuDiagnostics, trayErrorMessages, elapsedMs = clock.ElapsedMilliseconds
                    };
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { if (!_closed) Close(); }
            }
        }
    }
}
