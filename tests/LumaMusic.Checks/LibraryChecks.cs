using winuitest.Models;
using winuitest.Services;

internal static class LibraryChecks
{
    internal static void Run()
    {
        // Explicitly isolated from the user's real local library.
        var directory = Path.Combine(Path.GetTempPath(), "LumaMusic-Checks-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "library.json");
        try
        {
            var library = new LibraryStore(directory);
            var first = new Track { Id = 1, Title = "测试", DurationMs = 125000 };
            Program.Check(library.PlaybackQuality == AudioQuality.Default, "New libraries must use the default quality.");
            Program.Check(library.AudioOutputDeviceId == string.Empty, "New libraries must follow the system audio output.");
            Program.Check(library.Theme == ThemePreference.System && library.LastSession.CurrentTrack is null && library.LastSession.Queue.Count == 0,
                "A new library must follow the system theme and have no restored song.");
            Program.Check(first.DisplayDuration == "2:05", "Duration formatting failed.");
            Program.Check(library.ToggleFavorite(first), "Adding a favorite failed.");
            Program.Check(library.LastSaveError is null,
                $"The isolated library could not be saved at {directory}: {library.LastSaveError}");
            library.Remember(first);
            library.Remember(first);
            library.Volume = 0.32;
            library.Shuffle = true;
            library.Repeat = RepeatMode.All;
            library.PlaybackQuality = "hires";
            library.AudioOutputDeviceId = "{0.0.0.00000000}.fixture-speakers";
            var reloaded = new LibraryStore(directory);
            Program.Check(reloaded.Favorites.Count == 1 && reloaded.IsFavorite(1),
                $"Favorite was not persisted at {directory}. Save: {library.LastSaveError ?? "succeeded"}; reload: {reloaded.LastSaveError ?? "succeeded"}.");
            Program.Check(reloaded.Recent.Count == 1, "History was not deduplicated.");
            Program.Check(reloaded.Volume == 0.32 && reloaded.Shuffle && reloaded.Repeat == RepeatMode.All, "Playback preferences were not persisted.");
            Program.Check(reloaded.PlaybackQuality == "hires", "Quality preference was not persisted.");
            Program.Check(reloaded.AudioOutputDeviceId == "{0.0.0.00000000}.fixture-speakers", "Audio routing preference was not persisted.");
            reloaded.AudioOutputDeviceId = string.Empty;
            Program.Check(new LibraryStore(directory).AudioOutputDeviceId == string.Empty, "System-default audio output was not persisted.");
            reloaded.PlaybackQuality = "invalid-quality";
            Program.Check(new LibraryStore(directory).PlaybackQuality == AudioQuality.Default, "Invalid quality was not normalized.");
            reloaded.Theme = ThemePreference.Dark;
            var second = new Track { Id = 2, Title = "下一首", Artist = "歌手", Album = "专辑", DurationMs = 240000 };
            var queue = new List<Track> { first, second, first };
            reloaded.SavePlaybackSession(second, queue, TimeSpan.FromMilliseconds(73450));
            queue.Clear();
            var sessionReloaded = new LibraryStore(directory);
            Program.Check(sessionReloaded.Theme == ThemePreference.Dark, "Explicit dark theme was not persisted.");
            var session = sessionReloaded.LastSession;
            Program.Check(session.CurrentTrack?.Id == 2 && session.CurrentTrack.Artist == "歌手" && session.PositionMs == 73450,
                "The last song metadata or position was not restored.");
            Program.Check(session.Queue.Select(track => track.Id).SequenceEqual(new long[] { 1, 2 }), "Saved queue order or deduplication failed.");
            session.Queue.Clear();
            session.CurrentTrack!.Title = "external mutation";
            Program.Check(sessionReloaded.LastSession.Queue.Count == 2 && sessionReloaded.LastSession.CurrentTrack?.Title == "下一首",
                "A read snapshot must not mutate the durable session.");
            sessionReloaded.Theme = ThemePreference.Light;
            Program.Check(new LibraryStore(directory).Theme == ThemePreference.Light, "Explicit light theme was not persisted.");
            sessionReloaded.Theme = ThemePreference.System;
            Program.Check(new LibraryStore(directory).Theme == ThemePreference.System, "Following the system theme was not persisted.");
            Program.Check(!reloaded.ToggleFavorite(first), "Removing a favorite failed.");
            Program.Check(new LibraryStore(directory).Favorites.Count == 0, "Favorite removal was not persisted.");
            VerifyBatchFavorites(reloaded, directory, first, second);
            for (var index = 1; index <= 110; index++) reloaded.Remember(new Track { Id = index, Title = "Song " + index });
            Program.Check(reloaded.Recent.Count == 100 && reloaded.Recent[0].Id == 110, "History cap or ordering failed.");
            reloaded.ClearRecent();
            Program.Check(new LibraryStore(directory).Recent.Count == 0, "Clearing history was not persisted.");
            File.WriteAllText(statePath, "{invalid");
            var broken = new LibraryStore(directory);
            Program.Check(broken.Favorites.Count == 0 && broken.LastSaveError is not null, "Corrupt state was not recovered.");
            Program.Check(broken.Theme == ThemePreference.System && broken.LastSession.CurrentTrack is null, "Corrupt session/theme defaults were not recovered.");
            File.WriteAllText(statePath, "{\"Favorites\":null,\"Recent\":null,\"Volume\":12,\"Repeat\":42,\"Theme\":42,\"LastSession\":null,\"AudioOutputDeviceId\":null}");
            var invalid = new LibraryStore(directory);
            Program.Check(invalid.Volume == 1 && invalid.Repeat == RepeatMode.Off && invalid.Recent.Count == 0, "Invalid persisted values were not normalized.");
            Program.Check(invalid.Theme == ThemePreference.System && invalid.LastSession.PositionMs == 0, "Invalid theme or null session was not normalized.");
            Program.Check(invalid.AudioOutputDeviceId == string.Empty, "Null audio output must fall back to the system default.");
            File.WriteAllText(statePath, "{\"LastSession\":{\"CurrentTrack\":{\"Id\":2,\"DurationMs\":10000},\"Queue\":[null,{\"Id\":-1},{\"Id\":3},{\"Id\":3}],\"PositionMs\":999999}}");
            var clamped = new LibraryStore(directory).LastSession;
            Program.Check(clamped.PositionMs == 10000 && clamped.Queue.Select(track => track.Id).SequenceEqual(new long[] { 2, 3 }),
                "Out-of-range session position or invalid queue entries were not normalized.");
            invalid.SavePlaybackSession(first, new[] { first }, TimeSpan.FromSeconds(-3));
            Program.Check(new LibraryStore(directory).LastSession.PositionMs == 0, "Negative session position was not clamped.");
            invalid.SavePlaybackSession(null, Array.Empty<Track>(), TimeSpan.Zero);
            Program.Check(new LibraryStore(directory).LastSession.CurrentTrack is null, "Clearing the saved session failed.");
            Console.WriteLine("PASS library: batch favorite order/deduplication/one notification/persistence, history, theme preferences, playback session metadata/queue/position, corrupt/null state recovery");
        }
        finally
        {
            // Delete only files created by these checks, without recursive directory operations.
            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void VerifyBatchFavorites(LibraryStore library, string directory, Track first, Track second)
    {
        library.SetFavorites(new[] { second }, true);
        var third = new Track { Id = 3, Title = "第三首", Artist = "另一位歌手" };
        var changed = 0;
        var persistedAtNotification = new List<long[]>();
        void OnChanged(object? sender, EventArgs args)
        {
            changed++;
            // Consumers must see a complete saved batch, never one notification
            // per song or an event raised before persistence has finished.
            persistedAtNotification.Add(new LibraryStore(directory).Favorites.Select(track => track.Id).ToArray());
        }
        library.Changed += OnChanged;
        try
        {
            library.SetFavorites(new[] { first, third, new Track { Id = 1, Title = "重复副本" }, second, new Track { Id = 0 } }, true);
            Program.Check(changed == 1, "A favorite batch must raise Changed once.");
            Program.Check(library.Favorites.Select(track => track.Id).SequenceEqual(new long[] { 1, 3, 2 }),
                "A favorite batch lost selected order, duplicated an existing song, or accepted an invalid ID.");
            Program.Check(library.LastSaveError is null && persistedAtNotification[0].SequenceEqual(new long[] { 1, 3, 2 }),
                "Changed was raised before the entire favorite batch was durably saved.");
            var saved = new LibraryStore(directory).Favorites;
            Program.Check(saved[0].Title == first.Title && saved[1].Artist == third.Artist && saved[2].Title == second.Title,
                "Batch favorite deduplication replaced original metadata or lost persisted song metadata.");

            library.SetFavorites(new[] { second, first, third, first }, true);
            library.SetFavorites(Array.Empty<Track>(), true);
            Program.Check(changed == 1 && library.Favorites.Select(track => track.Id).SequenceEqual(new long[] { 1, 3, 2 }),
                "An unchanged or empty batch reordered favorites or raised a redundant notification.");

            library.SetFavorites(new[] { third, third, new Track { Id = 99 } }, false);
            Program.Check(changed == 2 && persistedAtNotification[1].SequenceEqual(new long[] { 1, 2 }),
                "Batch removal did not preserve survivor order and persist before one Changed event.");
            library.SetFavorites(new[] { third, new Track { Id = 99 } }, false);
            Program.Check(changed == 2, "Removing absent favorites raised a redundant notification.");
            library.SetFavorites(new[] { second, first, second }, false);
            Program.Check(changed == 3 && persistedAtNotification[2].Length == 0 && new LibraryStore(directory).Favorites.Count == 0,
                "Removing the remaining favorite batch was not persisted once.");
        }
        finally { library.Changed -= OnChanged; }
    }
}
