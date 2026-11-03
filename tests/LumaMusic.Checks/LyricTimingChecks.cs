using System.Text.Json;
using winuitest.Models;
using winuitest.Services;

internal static class LyricTimingChecks
{
    internal static void Run()
    {
        // A 30-second preview starts at 1:00 in the original song. At clip time
        // 0:16, delayed lyrics read 1:15; advancing them by 0.5s reads 1:16.5.
        var previewStart = TimeSpan.FromSeconds(60);
        var clipPosition = TimeSpan.FromSeconds(16);
        var songPosition = previewStart + clipPosition;
        var delayed = LyricTiming.ToLyricPosition(songPosition, 1000);
        var advanced = LyricTiming.ToLyricPosition(songPosition, -500);
        Program.Check(delayed == TimeSpan.FromSeconds(75) && advanced == TimeSpan.FromSeconds(76.5),
            "Calibration sign or preview origin is incorrect.");
        Program.Check(LyricTiming.ToPlaybackPosition(delayed, previewStart, 1000) == clipPosition &&
            LyricTiming.ToPlaybackPosition(advanced, previewStart, -500) == clipPosition,
            "Clicking a calibrated preview lyric must seek back to its original audible moment.");
        Program.Check(LyricTiming.ToLyricPosition(TimeSpan.Zero, 500) == TimeSpan.FromMilliseconds(-500),
            "Delaying lyrics must not prematurely activate the first line at audio time zero.");
        Program.Check(LyricTiming.ToPlaybackPosition(TimeSpan.FromSeconds(10), TimeSpan.Zero, -500) == TimeSpan.FromSeconds(9.5),
            "Full-track lyric seeking must apply negative calibration without a preview origin.");
        Program.Check(LyricTiming.ToPlaybackPosition(TimeSpan.FromSeconds(59), previewStart, -500) == TimeSpan.Zero &&
            LyricTiming.ToPlaybackPosition(TimeSpan.FromSeconds(89.5), previewStart, 1000, TimeSpan.FromSeconds(30)) == TimeSpan.FromSeconds(30),
            "Lyrics outside the available preview must seek to the nearest playable boundary.");
        Program.Check(LyricTiming.ToLyricPosition(TimeSpan.MaxValue, -30000) == TimeSpan.MaxValue &&
            LyricTiming.ToPlaybackPosition(TimeSpan.MaxValue, TimeSpan.Zero, 30000) == TimeSpan.MaxValue,
            "Extreme timestamps must saturate instead of overflowing.");

        VerifyPersistence(previewStart, clipPosition);
        Console.WriteLine("PASS lyric calibration: signed preview/full-track clocks, click-to-seek, clip boundaries, per-song restart/reset, legacy and normalized storage");
    }

    private static void VerifyPersistence(TimeSpan previewStart, TimeSpan clipPosition)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LumaMusic-LyricChecks-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "library.json");
        try
        {
            var library = new LibraryStore(directory);
            var track = new Track { Id = 41, Title = "Preview calibration", DurationMs = 180000 };
            Program.Check(library.GetLyricOffsetMs(track.Id) == 0, "New tracks must start uncalibrated.");
            library.SetLyricOffsetMs(41, 1000);
            library.SetLyricOffsetMs(42, -1500);
            library.SavePlaybackSession(track, new[] { track }, clipPosition);
            var restarted = new LibraryStore(directory);
            Program.Check(restarted.GetLyricOffsetMs(41) == 1000 && restarted.GetLyricOffsetMs(42) == -1500 &&
                restarted.GetLyricOffsetMs(43) == 0,
                "Restarting must preserve each song's calibration independently.");
            Program.Check(restarted.LastSession.PositionMs == 16000 &&
                LyricTiming.ToLyricPosition(TimeSpan.FromMilliseconds(restarted.LastSession.PositionMs) + previewStart,
                    restarted.GetLyricOffsetMs(41)) == TimeSpan.FromSeconds(75),
                "Calibration must not contaminate the saved audio position or preview origin.");
            restarted.ResetLyricOffset(41);
            var reset = new LibraryStore(directory);
            Program.Check(reset.GetLyricOffsetMs(41) == 0 && reset.GetLyricOffsetMs(42) == -1500,
                "Resetting one song must persist without clearing another song's calibration.");
            using (var document = JsonDocument.Parse(File.ReadAllText(statePath)))
                Program.Check(!document.RootElement.GetProperty("LyricOffsetsMs").TryGetProperty("41", out _),
                    "A reset must remove the redundant calibration entry from disk.");

            // Legacy libraries have no calibration member, and null is recoverable.
            File.WriteAllText(statePath, "{\"Volume\":0.32}");
            var legacy = new LibraryStore(directory);
            Program.Check(legacy.GetLyricOffsetMs(41) == 0 && legacy.Volume == 0.32,
                "Adding calibration must preserve libraries written by older versions.");
            File.WriteAllText(statePath, "{\"LyricOffsetsMs\":null}");
            Program.Check(new LibraryStore(directory).GetLyricOffsetMs(41) == 0,
                "A null persisted calibration map must be recovered.");
            File.WriteAllText(statePath, "{\"LyricOffsetsMs\":{\"41\":2147483647,\"42\":-2147483648,\"43\":1249,\"44\":0,\"-1\":1000}}");
            var normalized = new LibraryStore(directory);
            Program.Check(normalized.GetLyricOffsetMs(41) == 30000 && normalized.GetLyricOffsetMs(42) == -30000 &&
                normalized.GetLyricOffsetMs(43) == 1000 && normalized.GetLyricOffsetMs(-1) == 0,
                "Persisted calibration must clamp its range, align to half-seconds, and reject invalid track IDs.");
            Program.Check(normalized.SetLyricOffsetMs(43, -1250) == -1500 && normalized.SetLyricOffsetMs(0, 500) == 0,
                "Calibration setters must report their normalized value and ignore invalid track IDs.");
            Program.Check(new LibraryStore(directory).GetLyricOffsetMs(43) == -1500,
                "A normalized calibration change must survive a subsequent restart.");
        }
        finally
        {
            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
