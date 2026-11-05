using System.Globalization;
using System.Text.Json;
using winuitest.Models;
using winuitest.Services;

internal static class TrackShareLinkChecks
{
    internal static async Task RunAsync()
    {
        const string publicEntry = "https://music.163.com/song/media/outer/url?id=42.mp3";
        const string songPage = "https://music.163.com/song?id=42";
        foreach (var fee in new int?[] { 0, 8 })
        {
            var track = new Track { Id = 42, Fee = fee };
            Program.Check(TrackShareLink.Create(track) == publicEntry, "Known free standard audio did not use the stable public entry.");
            track.IsCloudTrack = true;
            Program.Check(TrackShareLink.Create(track) == songPage, "A private cloud track was advertised as public audio.");
            track.IsCloudTrack = false;
            track.IsUnavailable = true;
            Program.Check(TrackShareLink.Create(track) == songPage, "Unavailable regional/copyright audio was advertised as public audio.");
        }
        foreach (var fee in new int?[] { 1, 4 })
            Program.Check(TrackShareLink.Create(new Track { Id = 42, Fee = fee }) == songPage,
                "Paid metadata must use the song page without resolving an authenticated stream.");

        var legacy = JsonSerializer.Deserialize<Track>("{\"Id\":42,\"Title\":\"旧歌曲\"}")!;
        Program.Check(legacy.Fee is null && legacy.AlbumId == 0,
            "A legacy record silently became free or acquired a guessed album ID.");
        List<long> queried = new();
        var links = await TrackShareLink.ResolveAsync(new[] { legacy, new Track { Id = 45, Fee = 4 },
            new Track { Id = 46, Fee = 8, IsMetadataLoaded = false }, legacy }, (ids, token) =>
        {
            queried.AddRange(ids);
            return Task.FromResult(new List<Track> { new() { Id = 46, Fee = 1 }, new() { Id = 42, Fee = 0 } });
        }, CancellationToken.None);
        Program.Check(queried.SequenceEqual(new long[] { 42, 46 }) && links.SequenceEqual(new[] { (publicEntry, true),
            ("https://music.163.com/song?id=45", false), ("https://music.163.com/song?id=46", false) }),
            "Legacy/placeholder metadata resolution lost exact IDs, selection order, deduplication or known paid status.");
        foreach (var returned in new[] { new Track { Id = 43, Fee = 0 }, new Track { Id = 42 }, new Track { Id = 42, Fee = 16 } })
        {
            var failed = false;
            try
            {
                await TrackShareLink.ResolveAsync(new[] { new Track { Id = 41, Fee = 0 }, legacy },
                    (_, _) => Task.FromResult(new List<Track> { returned }), CancellationToken.None);
            }
            catch (InvalidOperationException) { failed = true; }
            Program.Check(failed, "Missing, unrelated or unknown fees produced a partial or misleading copy result.");
        }
        using (var superseded = new CancellationTokenSource())
        {
            var canceled = false;
            try
            {
                await TrackShareLink.ResolveAsync(new[] { legacy }, (_, _) =>
                {
                    superseded.Cancel();
                    return Task.FromResult(new List<Track> { new() { Id = 42, Fee = 0 } });
                }, superseded.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Program.Check(canceled, "A superseded request returned links after its metadata call completed.");
        }
        var vip = new Track { Id = 42, Fee = 1, IsUnavailable = true };
        var restored = JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(vip))!;
        Program.Check(restored.Fee == 1 && restored.IsUnavailable && restored.AccessLabel == "VIP", "Library roundtrip lost access metadata.");
        Program.Check(new Track { Fee = 4 }.AccessLabel == "付费" && new Track { Fee = 8 }.AccessLabel == "VIP 音质"
            && new Track { Fee = 0 }.AccessLabel == "" && legacy.AccessLabel == "", "Song badges conflate free audio with VIP-only audio.");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            Program.Check(TrackShareLink.Create(new Track { Id = 42, Fee = 0 }) == publicEntry, "Share IDs changed with display culture.");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        foreach (var id in new long[] { 0, -1, long.MaxValue })
        {
            var rejected = false;
            try { TrackShareLink.Create(new Track { Id = id, Fee = 0 }); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Program.Check(rejected, "Invalid song IDs were copied into share links.");
        }
        Console.WriteLine("PASS track access/share: rights lookup by ID, mixed selections, atomic failure, cancellation and public audio/page links");
    }
}
