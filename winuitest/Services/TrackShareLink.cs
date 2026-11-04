using System;
using System.Globalization;
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
        if (track.Id <= 0 || track.Id > 9007199254740991)
            throw new ArgumentOutOfRangeException(nameof(track), "歌曲 ID 无效。");
        var id = track.Id.ToString(CultureInfo.InvariantCulture);
        // Keep NetEase's stable ID-based entry, never a redirected, expiring CDN
        // URL or an authenticated stream URL. Availability still belongs to NetEase.
        return UsesPublicAudioEntry(track)
            ? $"https://music.163.com/song/media/outer/url?id={id}.mp3"
            : $"https://music.163.com/song?id={id}";
    }
}
