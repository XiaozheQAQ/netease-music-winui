using System;
using System.Collections.Generic;
using System.Linq;
using winuitest.Models;

namespace winuitest.Services;

public readonly record struct LyricGap(long StartMs, long EndMs, int NextLineIndex);
public readonly record struct LyricGapProgress(LyricGap Gap, double Progress);

/// <summary>Source-timed vocal gaps. No estimated line endings, beats or wall clock.</summary>
public static class LyricGapTimeline
{
    public const long MinimumGapMs = 2500;

    /// <summary>NextLineIndex refers to the supplied list, including any empty entries.</summary>
    public static IReadOnlyList<LyricGap> Build(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ordered = lines.Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line is not null && item.Line.TimeMs >= 0)
            .OrderBy(item => item.Line.TimeMs).ToArray();
        var sung = ordered.Where(item => !string.IsNullOrWhiteSpace(item.Line.Text)).ToArray();
        if (sung.Length == 0 || sung.All(item => IsInstrumentalNotice(item.Line.Text))) return Array.Empty<LyricGap>();

        var result = new List<LyricGap>();
        LyricLine? previous = null;
        long? emptyStart = null;
        long knownEnd = 0;
        foreach (var (line, index) in ordered)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                if (previous is not null && line.TimeMs > previous.TimeMs) emptyStart ??= line.TimeMs;
                continue;
            }

            if (previous is null)
            {
                if (line.TimeMs >= MinimumGapMs) result.Add(new LyricGap(0, line.TimeMs, index));
            }
            else
            {
                var marker = line.GapStartMs is long sourceStart && sourceStart > previous.TimeMs && sourceStart < line.TimeMs
                    ? sourceStart : emptyStart;
                var start = ExplicitEnd(previous) ?? marker;
                if (start.HasValue)
                {
                    // An overlapping word/line from an earlier voice can still
                    // be sounding; never put a countdown over that known span.
                    var boundedStart = Math.Max(start.Value, knownEnd);
                    if (line.TimeMs >= boundedStart && line.TimeMs - boundedStart >= MinimumGapMs)
                        result.Add(new LyricGap(boundedStart, line.TimeMs, index));
                }
            }
            knownEnd = Math.Max(knownEnd, ExplicitEnd(line) ?? line.TimeMs);
            previous = line;
            emptyStart = null;
        }
        return result;
    }

    /// <summary>Returns the active [start,end) gap using the already calibrated lyric clock.</summary>
    public static LyricGapProgress? GetState(IReadOnlyList<LyricGap> gaps, TimeSpan lyricPosition)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var ticks = (Int128)lyricPosition.Ticks;
        var low = 0;
        var high = gaps.Count - 1;
        var index = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (ticks >= (Int128)gaps[middle].StartMs * TimeSpan.TicksPerMillisecond)
            {
                index = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        if (index < 0) return null;
        var gap = gaps[index];
        var startTicks = (Int128)gap.StartMs * TimeSpan.TicksPerMillisecond;
        var endTicks = (Int128)gap.EndMs * TimeSpan.TicksPerMillisecond;
        if (gap.StartMs < 0 || endTicks <= startTicks || ticks >= endTicks) return null;
        var progress = (double)(ticks - startTicks) / (double)(endTicks - startTicks);
        return new LyricGapProgress(gap, Math.Clamp(progress, 0, 1));
    }

    private static long? ExplicitEnd(LyricLine line)
    {
        long? end = line.DurationMs > 0 ? SaturatingEnd(line.TimeMs, line.DurationMs) : null;
        if (line.Words is not null)
            foreach (var word in line.Words)
            {
                if (word is null || word.StartMs < 0 || word.DurationMs <= 0) continue;
                var wordEnd = SaturatingEnd(word.StartMs, word.DurationMs);
                if (wordEnd > line.TimeMs) end = Math.Max(end ?? line.TimeMs, wordEnd);
            }
        return end;
    }

    private static long SaturatingEnd(long start, long duration) =>
        (long)Int128.Clamp((Int128)start + duration, 0, long.MaxValue);

    private static bool IsInstrumentalNotice(string text)
    {
        var normalized = text.Trim().TrimEnd('.', '。', '…', '!').Replace("，", ",").Replace(" ", "");
        return normalized is "纯音乐" or "纯音乐,请欣赏" || normalized.Equals("instrumental", StringComparison.OrdinalIgnoreCase);
    }
}
