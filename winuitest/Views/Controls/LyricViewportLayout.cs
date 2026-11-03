using System;

namespace winuitest.Views.Controls;

/// <summary>Chooses an anchor from measured lyric blocks, including their subtitles.</summary>
internal static class LyricViewportLayout
{
    public static double CurrentLineAnchor(double viewportHeight, double precedingSpan, double currentHeight, double inset)
    {
        viewportHeight = FiniteNonNegative(viewportHeight);
        precedingSpan = FiniteNonNegative(precedingSpan);
        currentHeight = FiniteNonNegative(currentHeight);
        inset = Math.Min(FiniteNonNegative(inset), viewportHeight / 2);
        var usableHeight = Math.Max(0, viewportHeight - inset * 2);

        // Keep a fitting current block entirely visible. As it outgrows the
        // viewport, gradually restore preceding context, without a jump at
        // the fit threshold during resizing or subtitle changes.
        var currentReservation = currentHeight <= usableHeight
            ? currentHeight
            : usableHeight - Math.Min(usableHeight * 0.32, currentHeight - usableHeight);
        if (precedingSpan == 0) return inset;

        // Place the viewport edge halfway through the preceding measured block
        // (including its subtitles), instead of keeping that entire block above
        // the active one. Long preceding paragraphs cannot push it below the
        // upper quarter; short viewports still prioritise the active text.
        var desiredAnchor = Math.Max(inset, Math.Min(precedingSpan * 0.5, inset + usableHeight * 0.24));
        var availableAnchor = inset + Math.Max(0, usableHeight - currentReservation);
        return Math.Min(desiredAnchor, availableAnchor);
    }

    private static double FiniteNonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
