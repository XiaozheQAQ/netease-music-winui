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
        var ownsTestInstance = false;
        var trayError = false;
        var playbackError = false;
        var success = false;
        EventHandler<string> onTrayError = (_, _) => trayError = true;
        EventHandler<string> onPlaybackError = (_, _) => playbackError = true;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));

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

            step = "native-tray-menu";
            _tray.ShowMenu();
            await Task.Delay(350, budget.Token);
            Assert(_tray.IsAvailable);
            AssertHealthy();
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
                        elapsedMs = clock.ElapsedMilliseconds
                    };
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { if (!_closed) Close(); }
            }
        }
    }
}
