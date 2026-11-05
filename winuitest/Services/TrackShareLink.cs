using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using winuitest.Models;

namespace winuitest.Services;

public static class TrackShareLink
{
    public static bool UsesPublicAudioEntry(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.Fee is 0 or 8 && !track.IsCloudTrack && !track.IsUnavailable;
    }

    public static string Create(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidateId(track);
        if (NeedsMetadata(track)) throw Unconfirmed(track);
        var id = track.Id.ToString(CultureInfo.InvariantCulture);
        // Keep NetEase's stable ID-based entry, never a redirected, expiring CDN
        // URL or an authenticated stream URL. Availability still belongs to NetEase.
        return UsesPublicAudioEntry(track)
            ? $"https://music.163.com/song/media/outer/url?id={id}.mp3"
            : $"https://music.163.com/song?id={id}";
    }

    public static async Task<IReadOnlyList<(string Address, bool IsAudio)>> ResolveAsync(IEnumerable<Track> tracks,
        Func<IEnumerable<long>, CancellationToken, Task<List<Track>>> getDetails, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selection = tracks.DistinctBy(track => track.Id).ToArray();
        foreach (var track in selection) ValidateId(track);
        var details = new Dictionary<long, Track>();
        foreach (var batch in selection.Where(NeedsMetadata).Select(track => track.Id).Chunk(500))
        {
            var result = await getDetails(batch, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var track in result) details[track.Id] = track;
        }

        var addresses = new List<(string, bool)>(selection.Length);
        foreach (var original in selection)
        {
            var track = original;
            if (NeedsMetadata(original))
            {
                if (!details.TryGetValue(original.Id, out var confirmed) || NeedsMetadata(confirmed)) throw Unconfirmed(original);
                track = confirmed;
            }
            addresses.Add((Create(track), UsesPublicAudioEntry(track)));
        }
        token.ThrowIfCancellationRequested();
        return addresses;
    }

    private static bool NeedsMetadata(Track track) => !track.IsMetadataLoaded || track.Fee is not (0 or 1 or 4 or 8);

    private static void ValidateId(Track track)
    {
        if (track.Id <= 0 || track.Id > 9007199254740991)
            throw new ArgumentOutOfRangeException(nameof(track), "歌曲 ID 无效。");
    }

    private static InvalidOperationException Unconfirmed(Track track)
    {
        var name = string.IsNullOrWhiteSpace(track.Title) ? track.Id.ToString(CultureInfo.InvariantCulture) : track.Title;
        return new InvalidOperationException($"网易云未返回「{name}」的有效播放权限，请稍后重试。");
    }
}
