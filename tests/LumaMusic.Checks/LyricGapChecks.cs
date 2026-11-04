using winuitest.Models;
using winuitest.Services;

internal static class LyricGapChecks
{
    internal static void Run()
    {
        static LyricLine Line(long start, long duration = 0, string text = "歌") =>
            new() { TimeMs = start, DurationMs = duration, Text = text };
        static void Gaps(IReadOnlyList<LyricLine> lines, params LyricGap[] expected) =>
            Program.Check(LyricGapTimeline.Build(lines).SequenceEqual(expected), "Source-timed lyric gaps differ from the explicit timeline.");

        Gaps(Array.Empty<LyricLine>());
        Gaps(new[] { Line(20000, text: "纯音乐，请欣赏") });
        Gaps(new[] { Line(0, text: ""), Line(10000, text: " ") });
        Gaps(new[] { Line(2499) });
        Gaps(new[] { Line(2500) }, new LyricGap(0, 2500, 0));
        // Only start times exist: the later line cannot tell us when the prior vocal ended.
        Gaps(new[] { Line(0), Line(30000) });
        Gaps(new[] { Line(0, 1000), Line(3499) });
        Gaps(new[] { Line(0, 1000), Line(3500) }, new LyricGap(1000, 3500, 1));
        Gaps(new[] { Line(0, 1000) }); // No next vocal, no fabricated outro countdown.
        Gaps(new[] { Line(0, 1000), Line(500) }); // Overlapping lines are not a gap.

        var wordLine = Line(0, 1000);
        wordLine.Words = new() { new() { StartMs = 500, DurationMs = 1500, Text = "歌" } };
        Gaps(new[] { wordLine, Line(4500) }, new LyricGap(2000, 4500, 1));
        wordLine.DurationMs = 3000;
        Gaps(new[] { wordLine, Line(5500) }, new LyricGap(3000, 5500, 1));
        wordLine.DurationMs = 0;
        Gaps(new[] { wordLine, Line(4500) }, new LyricGap(2000, 4500, 1));
        // A prior overlapping voice extends beyond the immediately preceding line.
        Gaps(new[] { Line(0, 9000), Line(1000, 1000), Line(8000) });
        Gaps(new[] { Line(0, 9000), Line(1000, 1000), Line(12000) }, new LyricGap(9000, 12000, 2));

        Gaps(new[] { Line(0), Line(2000, text: ""), Line(3000, text: " "), Line(8000) }, new LyricGap(2000, 8000, 3));
        Gaps(new[] { Line(0), new LyricLine { TimeMs = 8000, Text = "歌", GapStartMs = 2000 } }, new LyricGap(2000, 8000, 1));
        Gaps(new[] { Line(1000), new LyricLine { TimeMs = 8000, Text = "歌", GapStartMs = 0 } });
        Gaps(new[] { Line(1000), new LyricLine { TimeMs = 8000, Text = "歌", GapStartMs = 9000 } });
        // Input may be unsorted; indexes still point to the caller's original rows.
        Gaps(new[] { Line(8000), Line(0, 1000) }, new LyricGap(1000, 8000, 0));
        Gaps(new[] { Line(-1, 1000), Line(0, -100), Line(8000) });
        var overflowing = Line(0, long.MaxValue);
        overflowing.Words.Add(new LyricWord { StartMs = long.MaxValue - 1, DurationMs = long.MaxValue, Text = "歌" });
        Gaps(new[] { overflowing, Line(long.MaxValue) });

        var timeline = LyricGapTimeline.Build(new[] { Line(4000, 1000), Line(10000) });
        Program.Check(LyricGapTimeline.GetState(timeline, TimeSpan.FromMilliseconds(-1)) is null
            && LyricGapTimeline.GetState(timeline, TimeSpan.Zero) is { Progress: 0 }
            && LyricGapTimeline.GetState(timeline, TimeSpan.FromSeconds(2)) is { Progress: 0.5 }
            && LyricGapTimeline.GetState(timeline, TimeSpan.FromSeconds(4)) is null,
            "Intro countdown boundaries must use [start,end) on the lyric clock.");
        var atSeven = LyricGapTimeline.GetState(timeline, TimeSpan.FromSeconds(7));
        Program.Check(atSeven is { Progress: 0.4 } && atSeven.Value.Gap.NextLineIndex == 1
            && LyricGapTimeline.GetState(timeline, TimeSpan.FromSeconds(7)) == atSeven
            && LyricGapTimeline.GetState(timeline, TimeSpan.FromSeconds(6)) is { Progress: 0.2 },
            "Pause or seek changed the countdown independently of supplied lyric time.");
        var calibrated = LyricTiming.ToLyricPosition(TimeSpan.FromSeconds(7.5), 500);
        Program.Check(LyricGapTimeline.GetState(timeline, calibrated) == atSeven
            && LyricGapTimeline.GetState(timeline, TimeSpan.MaxValue) is null
            && LyricGapTimeline.GetState(timeline, TimeSpan.MinValue) is null,
            "Countdown applied calibration twice or overflowed the playback clock.");
        var extreme = LyricGapTimeline.GetState(LyricGapTimeline.Build(new[] { Line(long.MaxValue) }), TimeSpan.MaxValue);
        Program.Check(extreme.HasValue && double.IsFinite(extreme.Value.Progress) && extreme.Value.Progress is >= 0 and <= 1,
            "Extreme source timestamps produced invalid countdown progress.");
        Console.WriteLine("PASS lyric gaps: explicit intro/word/line/empty markers, overlaps, no guessed LRC end/outro, pause/seek/calibration, overflow bounds");
    }
}
