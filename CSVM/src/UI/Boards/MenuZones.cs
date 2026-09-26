using System;

namespace CSVM.UI.Boards;

/// <summary>
/// How the launchscreen's three bands divide a window: a header of fixed height at the top, a
/// footer of fixed height at the bottom, and the selection list in whatever is left between them.
/// The two fixed bands are what stops the screen jumping, since their heights are a function of
/// the metrics alone and not of how many rows or how much description the focused row happens to
/// have. One uniform scale drives all three, so a band keeps its share of the window at any
/// resolution; the middle absorbs the slack.
/// </summary>
public readonly record struct MenuZones(float Scale, float Header, float Middle, float Footer)
{
    /// <summary>The window height the band metrics are authored at. A taller window scales them
    /// up, a shorter one is served at 1:1 until the content stops fitting.</summary>
    public const float ReferenceHeight = 720f;

    /// <summary>The bands for a window of the given height, from the three reference heights the
    /// caller measures its own content at. A window with no height at all (one being restored)
    /// falls back to 1:1 rather than a zero scale nothing can draw at.</summary>
    public static MenuZones For(float viewportHeight, float headerRef, float middleRef, float footerRef)
    {
        float total = headerRef + middleRef + footerRef;
        float scale = Math.Max(1f, viewportHeight / ReferenceHeight);
        if (total > 0f)
        {
            scale = Math.Min(scale, viewportHeight / total);
        }

        if (!(scale > 0f))
        {
            scale = 1f;
        }

        float header = headerRef * scale;
        float footer = footerRef * scale;
        return new MenuZones(scale, header, Math.Max(0f, viewportHeight - header - footer), footer);
    }
}
