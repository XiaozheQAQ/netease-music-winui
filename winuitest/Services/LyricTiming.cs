using System;

namespace winuitest.Services;

/// <summary>Manual lyric calibration, separate from the audio clock and preview start.</summary>
public static class LyricTiming
{
    public const int StepMs = 500;
    public const int MinimumOffsetMs = -30000;
    public const int MaximumOffsetMs = 30000;

    /// <summary>Positive values make lyrics appear later; calibration uses half-second steps.</summary>
    public static int NormalizeOffsetMs(int offsetMs)
    {
        var bounded = Math.Clamp(offsetMs, MinimumOffsetMs, MaximumOffsetMs);
        return (int)Math.Round(bounded / (double)StepMs, MidpointRounding.AwayFromZero) * StepMs;
    }

    /// <summary>
    /// Convert the original-song clock (including any preview start) to the lyric clock.
    /// Preserve negative positions so a delayed first line does not activate at audio time zero.
    /// </summary>
    public static TimeSpan ToLyricPosition(TimeSpan songPosition, int offsetMs)
    {
        var ticks = (Int128)songPosition.Ticks - OffsetTicks(offsetMs);
        return TimeSpan.FromTicks((long)Int128.Clamp(ticks, long.MinValue, long.MaxValue));
    }

    /// <summary>
    /// Convert a lyric timestamp to the playable stream's clock when a lyric is clicked.
    /// This reverses calibration, then removes the upstream preview start exactly once.
    /// </summary>
    public static TimeSpan ToPlaybackPosition(TimeSpan lyricPosition, TimeSpan previewStart, int offsetMs,
        TimeSpan? playbackDuration = null)
    {
        var ticks = (Int128)lyricPosition.Ticks + OffsetTicks(offsetMs) - Math.Max(0, previewStart.Ticks);
        var maximum = playbackDuration.HasValue ? Math.Max(0, playbackDuration.Value.Ticks) : long.MaxValue;
        return TimeSpan.FromTicks((long)Int128.Clamp(ticks, 0, maximum));
    }

    private static long OffsetTicks(int offsetMs) => NormalizeOffsetMs(offsetMs) * TimeSpan.TicksPerMillisecond;
}
