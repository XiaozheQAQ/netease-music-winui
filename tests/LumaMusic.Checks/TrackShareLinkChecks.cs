using System.Globalization;
using System.Text.Json;
using winuitest.Models;
using winuitest.Services;

internal static class TrackShareLinkChecks
{
    internal static void Run()
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
        foreach (var fee in new int?[] { 1, 4, null, -1, 16 })
            Program.Check(TrackShareLink.Create(new Track { Id = 42, Fee = fee }) == songPage,
                "Paid or unknown metadata must use the song page without resolving an authenticated stream.");

        var legacy = JsonSerializer.Deserialize<Track>("{\"Id\":42,\"Title\":\"旧歌曲\"}")!;
        Program.Check(legacy.Fee is null && legacy.AlbumId == 0 && TrackShareLink.Create(legacy) == songPage,
            "A legacy record silently became free or acquired a guessed album ID.");
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
        Console.WriteLine("PASS track access/share: fee badges, legacy unknown, public ID entry, paid/cloud/unavailable fallback, metadata persistence");
    }
}
