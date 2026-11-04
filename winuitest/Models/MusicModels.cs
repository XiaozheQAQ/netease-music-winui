using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace winuitest.Models;

public sealed class Track
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public long DurationMs { get; set; }

    [JsonIgnore]
    public string DisplayDuration
    {
        get
        {
            var duration = TimeSpan.FromTicks(Math.Clamp(DurationMs, 0, long.MaxValue / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
            return $"{duration.Ticks / TimeSpan.TicksPerMinute}:{duration.Seconds:00}";
        }
    }
}

public sealed class MusicPlaylist
{
    public long Id { get; set; }
    public long CreatorId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public long PlayCount { get; set; }
    [JsonIgnore]
    public string PlayCountLabel => PlayCount >= 100000000 ? $"{PlayCount / 100000000d:0.#}亿" : PlayCount >= 10000 ? $"{PlayCount / 10000d:0.#}万" : PlayCount.ToString("N0");

    public bool IsOwnedBy(long userId) => userId > 0 && CreatorId == userId;
}

public sealed class HomeFeed
{
    public List<MusicPlaylist> Playlists { get; set; } = new();
    public List<Track> NewSongs { get; set; } = new();
}

public sealed class SearchResult
{
    public List<Track> Tracks { get; set; } = new();
    public int Total { get; set; }
}

public sealed class PlaylistDetail
{
    public long Id { get; set; }
    public long CreatorId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public List<Track> Tracks { get; set; } = new();

    public bool IsOwnedBy(long userId) => userId > 0 && CreatorId == userId;
}

public sealed class StreamInfo
{
    public string Url { get; set; } = string.Empty;
    public bool IsPreview { get; set; }
    public long StartOffsetMs { get; set; }
    public string Quality { get; set; } = string.Empty;
}

public sealed class LyricLine
{
    public long TimeMs { get; set; }
    public long DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public string Romanization { get; set; } = string.Empty;
    public List<LyricWord> Words { get; set; } = new();
}

public sealed class LyricWord
{
    public long StartMs { get; set; }
    public long DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public enum RepeatMode
{
    Off,
    All,
    One
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class PlaybackSessionSnapshot
{
    public Track? CurrentTrack { get; set; }
    public List<Track> Queue { get; set; } = new();
    /// <summary>Position within the playable stream, including for a preview clip.</summary>
    public long PositionMs { get; set; }
}

public sealed class AccountProfile
{
    public long Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public int VipType { get; set; }

    [JsonIgnore]
    public string MembershipLabel => VipType switch
    {
        11 => "黑胶 VIP",
        10 => "音乐包",
        _ => "网易云音乐"
    };
}

public sealed class AccountStatus
{
    public AccountProfile? Profile { get; set; }
}

public sealed class QrLoginSession
{
    public string Key { get; set; } = string.Empty;
    public string QrImage { get; set; } = string.Empty;
    public string QrUrl { get; set; } = string.Empty;
}

public sealed class QrLoginStatus
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
}

public sealed class UserPlaylistPage
{
    public List<MusicPlaylist> Playlists { get; set; } = new();
    public bool More { get; set; }
}

public static class AudioQuality
{
    public const string Default = "lossless";
    public static string Normalize(string? value) => value switch
    {
        "standard" or "higher" or "exhigh" or "lossless" or "hires" => value,
        _ => Default
    };
}
