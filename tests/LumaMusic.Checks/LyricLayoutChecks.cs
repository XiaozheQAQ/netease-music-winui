using winuitest.Views.Controls;

internal static class LyricLayoutChecks
{
    internal static void Run()
    {
        var normal = LyricViewportLayout.CurrentLineAnchor(600, 140, 120, 24);
        Near(normal, 70, "The preceding phrase should be half clipped, not retained in full.");
        Near(normal - 140, -70, "The viewport must begin halfway through the preceding measured block.");
        Near(LyricViewportLayout.CurrentLineAnchor(600, 220, 180, 24), 110,
            "Enlarged subtitles must contribute to the measured preceding half-block.");
        Near(LyricViewportLayout.CurrentLineAnchor(600, 0, 120, 24), 24, "The first line needs a safe top inset.");
        Program.Check(LyricViewportLayout.CurrentLineAnchor(600, 800, 120, 24) < 600 * 0.27,
            "An unusually tall previous paragraph pushed the active line too far down.");
        Program.Check(LyricViewportLayout.CurrentLineAnchor(160, 120, 80, 16) + 80 <= 144,
            "A fitting active paragraph must remain fully visible in a short viewport.");

        var extremes = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d, double.Epsilon, 600d, double.MaxValue };
        foreach (var height in extremes)
        foreach (var previous in extremes)
        foreach (var current in extremes)
        foreach (var inset in extremes)
        {
            var anchor = LyricViewportLayout.CurrentLineAnchor(height, previous, current, inset);
            var finiteHeight = double.IsFinite(height) ? Math.Max(0, height) : 0;
            Program.Check(double.IsFinite(anchor) && anchor >= 0 && anchor <= finiteHeight,
                "Degenerate layout dimensions produced an invalid anchor.");
        }

        var random = new Random(1749);
        for (var index = 0; index < 1200; index++)
        {
            var height = random.NextDouble() * 1600;
            var inset = Math.Min(24, height / 2);
            var usable = height - inset * 2;
            var previous = random.NextDouble() * 1600;
            var current = random.NextDouble() * 1600;
            var anchor = LyricViewportLayout.CurrentLineAnchor(height, previous, current, inset);
            if (current <= usable)
                Program.Check(anchor + current <= height - inset + 0.000001, "The active block was clipped despite available room.");
            foreach (var threshold in new[] { usable, usable * 1.32 })
            {
                var before = LyricViewportLayout.CurrentLineAnchor(height, previous, Math.Max(0, threshold - 0.001), inset);
                var after = LyricViewportLayout.CurrentLineAnchor(height, previous, threshold + 0.001, inset);
                Program.Check(Math.Abs(after - before) <= 0.002001, "Resizing across the long-line threshold jumped the anchor.");
            }
        }

        VerifyScroll();
        Console.WriteLine("PASS lyric layout/motion: half-previous anchor, enlarged subtitle blocks, short/long viewports, finite extremes, continuous resizing, refresh-rate independence and interrupted travel");
    }

    private static void VerifyScroll()
    {
        var start = new LyricScrollState(0, LyricScrollMotion.RetargetVelocity(0, 0, 400, true));
        var quarterSecond = LyricScrollMotion.Advance(start, 400, 0.15);
        Program.Check(quarterSecond.Position > 190 && quarterSecond.Position < 250,
            "Natural phrase travel should lift decisively without teleporting.");
        var expected = LyricScrollMotion.Advance(start, 400, 1);
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var state = start;
            for (var frame = 0; frame < fps; frame++)
            {
                var next = LyricScrollMotion.Advance(state, 400, 1d / fps);
                Program.Check(next.Position >= state.Position && next.Position <= 400, "Normal phrase travel must not reverse or overshoot.");
                state = next;
            }
            Near(state.Position, expected.Position, "Refresh rate changed the scroll destination.");
            Near(state.Velocity, expected.Velocity, "Refresh rate changed the scroll velocity.");
        }
        var interrupted = quarterSecond with
        {
            Velocity = LyricScrollMotion.RetargetVelocity(quarterSecond.Position, quarterSecond.Velocity, 40, false)
        };
        Near(interrupted.Position, quarterSecond.Position, "Retargeting must preserve the current visible position.");
        var settled = LyricScrollMotion.Advance(interrupted, 40, 2);
        Program.Check(Math.Abs(settled.Position - 40) < 0.001 && Math.Abs(settled.Velocity) < 0.01,
            "An interrupted scroll did not settle at its new destination.");
        Program.Check(LyricScrollMotion.Advance(start, 400, double.NaN) == start &&
            LyricScrollMotion.Advance(start, 400, 0) == start, "An invalid or zero frame duration changed the scroll state.");
    }

    private static void Near(double actual, double expected, string message) =>
        Program.Check(Math.Abs(actual - expected) < 0.000001, message);
}
