using System;

namespace CSVM.UI.Boards;

/// <summary>
/// How far a one-line caption wider than its box has scrolled, the renderer's marquee for a
/// <see cref="BoardLine.Marquee"/> line. A caption that fits never moves. One that overflows rests
/// at its start, scrolls until its end shows, rests there and scrolls back, then repeats. The
/// clock is the caller's, so the phase is a function of the steps it was handed. Engine-free, so
/// the timing is tested without a font.
/// </summary>
public static class BoardMarquee
{
    /// <summary>How long a caption rests at each end, in seconds. TUNE: long enough to read a
    /// half-panel caption before it moves; no reference fixes it.</summary>
    public const double HoldSeconds = 1.5;

    /// <summary>The scroll speed in authored pixels a second, about three characters of the KEYS
    /// page's row face. TUNE: calm enough to read while it moves; no reference fixes it.</summary>
    public const float PixelsPerSecond = 20f;

    /// <summary>How far past its box a caption may run, in authored pixels, and still be drawn whole
    /// and still. The renderer clips whole glyphs, so a caption a pixel too wide would otherwise
    /// scroll one pixel and trade its first glyph for its last. TUNE: inside the gap between the
    /// KEYS page's columns.</summary>
    public const float SlackPixels = 4f;

    /// <summary>The clock every marquee reads while set, in seconds, or null to let the view's own
    /// clock run. ⚠ A deterministic run must set it. A running marquee puts a capture's pixels on
    /// the frame count, so <c>--det</c> pins the start and <c>--debug-marquee=</c> any phase.
    /// </summary>
    public static double? PinnedSeconds { get; set; }

    /// <summary>One whole rest, scroll, rest and return, in seconds, or 0 for a caption that fits.
    /// </summary>
    public static double Cycle(float overflow) =>
        overflow <= 0f ? 0d : 2d * (HoldSeconds + (overflow / PixelsPerSecond));

    /// <summary>How far the caption is shifted left at <paramref name="seconds"/>, in authored
    /// pixels, between 0 and <paramref name="overflow"/> (how much wider than its box it is).
    /// </summary>
    public static float Offset(float overflow, double seconds)
    {
        if (overflow <= 0f || seconds <= 0d)
        {
            return 0f;
        }

        double travel = overflow / PixelsPerSecond;
        double t = seconds % Cycle(overflow);
        if (t < HoldSeconds)
        {
            return 0f;
        }

        t -= HoldSeconds;
        if (t < travel)
        {
            return (float)(t * PixelsPerSecond);
        }

        t -= travel;
        if (t < HoldSeconds)
        {
            return overflow;
        }

        t -= HoldSeconds;
        return (float)Math.Max(0d, overflow - (t * PixelsPerSecond));
    }
}
