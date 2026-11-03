using System;

namespace winuitest.Views.Controls;

internal readonly record struct LyricScrollState(double Position, double Velocity);

/// <summary>Continuous scroll motion, independent of display refresh rate.</summary>
internal static class LyricScrollMotion
{
    private const double Frequency = 10.3;

    public static double RetargetVelocity(double position, double velocity, double target, bool phraseAdvance)
    {
        var distance = target - position;
        // Keep forward momentum; quickly shed it when the destination reverses.
        if (Math.Sign(distance) != Math.Sign(velocity)) velocity *= 0.25;
        if (phraseAdvance && distance > 24)
            velocity = Math.Max(velocity, Math.Min(distance * 1.9, 1300));
        return velocity;
    }

    public static LyricScrollState Advance(LyricScrollState state, double target, double elapsed)
    {
        if (!double.IsFinite(elapsed) || elapsed <= 0) return state;
        var displacement = state.Position - target;
        var decay = Math.Exp(-Frequency * elapsed);
        var term = (state.Velocity + Frequency * displacement) * elapsed;
        return new LyricScrollState(target + (displacement + term) * decay,
            (state.Velocity - Frequency * term) * decay);
    }
}
