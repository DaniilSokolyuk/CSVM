using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>The one place the flight HUD decides how big it draws (docs/architecture.md). The
/// window height sets the base scale against a 1440p reference; a splitscreen pane's share of
/// that window is damped through a square root, confirmed at the controls. A
/// full-screen single-player view has <see cref="PaneFactor"/> exactly 1, so <see cref="Scale"/>
/// returns the plain height ratio unchanged. Damped sizes stay on screen only if positions anchor
/// to a pane EDGE, not the top-left, and a horizontal edge means
/// <see cref="ReadingBox(Vector2)"/>'s, not the pane's own, so a pane wider than the reference
/// frame does not push the left and right columns apart. A NARROWER pane has the opposite
/// problem, a column holding a 16:9 frame's margin in from a border that is nearer than 16:9,
/// which <see cref="ColumnOutdent"/> hands back.</summary>
public static class HudMetrics
{
    /// <summary>The reference viewport height every HUD metric was measured at (HUD.png).</summary>
    public const float ReferenceHeight = 1440f;

    /// <summary>The aspect the reference frame was measured at: HUD.png is 2556x1440, 16:9 to
    /// within its own bezel scans, and every edge-anchored constant is an offset from its edges.</summary>
    public const float ReferenceAspect = 16f / 9f;

    /// <summary>The border an edge-anchored gauge column keeps on a pane narrower than the
    /// reference frame, in reference pixels. The reference placement's own margins are a 16:9
    /// frame's worth of room that a 4:3 pane does not have, so there the column gives all but this
    /// back and sits at the border.</summary>
    public const float MinimumColumnMargin = 16f;

    /// <summary>The lowest allowed HUD text multiplier.</summary>
    public const float MinimumTextScale = 0.5f;

    /// <summary>The highest allowed HUD text multiplier.</summary>
    public const float MaximumTextScale = 2f;

    /// <summary>The user-selected multiplier for flight-status text.</summary>
    public static float StatusTextScale => TextScale("hud.statusTextScale");

    /// <summary>The user-selected multiplier for flight marker labels.</summary>
    public static float MarkerTextScale => TextScale("hud.markerTextScale");

    /// <summary>How much this control's viewport is shrunk by splitscreen: 1.0 for a full-screen
    /// single-player view (the pane IS the window), sqrt(½) ≈ 0.71 in a 2P pane, ½ in a 4P pane.
    /// Multiply any element that should shrink with the pane, but not vanish, by this.</summary>
    public static float PaneFactor(Control control)
    {
        float paneH = control.GetViewportRect().Size.Y;
        // The root Window's height is the real screen height even for a control living inside a
        // SubViewport (the panes are children of the same window). No stretch/content scaling is
        // configured in project.godot, so window pixels and viewport pixels are the same unit.
        float windowH = control.GetTree()?.Root?.Size.Y ?? 0f;
        if (windowH < 1f || paneH < 1f)
            return 1f;
        return Mathf.Sqrt(Mathf.Clamp(paneH / windowH, 0.01f, 1f));
    }

    /// <summary>The scale factor a HUD element should apply to its reference-space metrics:
    /// the window's height against the calibration reference, damped by the pane share.
    /// Identical to plain <c>viewportHeight / reference</c> whenever there is one full view.</summary>
    public static float Scale(Control control, float reference = ReferenceHeight)
    {
        float windowH = control.GetTree()?.Root?.Size.Y ?? 0f;
        if (windowH < 1f)
            windowH = control.GetViewportRect().Size.Y;
        return windowH / reference * PaneFactor(control);
    }

    /// <summary>The pane region an edge-anchored HUD element measures from: the reference frame's
    /// aspect at the pane's full height, centred. A pane 16:9 or narrower, to within a pixel, IS
    /// the box, so an ordinary screen sees nothing move; a wider one keeps the left-anchored and
    /// right-anchored columns a reading width apart rather than a screen width. ⚠ Not for an
    /// off-screen marker, which belongs to the true pane edge: that is the edge it left by.</summary>
    public static Rect2 ReadingBox(Vector2 paneSize)
    {
        // ⚠ A pane within a pixel of the reference aspect IS the box. The splitscreen gutter leaves
        // a nominally 16:9 pane a fraction wide (639x359 in a 4P grid at 720p), and shaving that
        // fraction off each side would move every dial half a pixel for nothing anyone can see.
        float width = paneSize.Y * ReferenceAspect;
        if (paneSize.X - width < 1f)
            return new Rect2(Vector2.Zero, paneSize);
        return new Rect2((paneSize.X - width) / 2f, 0f, width, paneSize.Y);
    }

    /// <summary>This control's reading box, in its own local space.</summary>
    public static Rect2 ReadingBox(Control control) => ReadingBox(control.GetViewportRect().Size);

    /// <summary>How far outward, in reference pixels, an edge-anchored column slides on a box this
    /// wide. <paramref name="referenceMargin"/> is the column's outer edge measured from the
    /// reference frame's own edge; on a box narrower than that frame it collapses to
    /// <see cref="MinimumColumnMargin"/>, which is what puts a column at the border of a 4:3
    /// screen. Zero on a box at the reference aspect, which is every pane 16:9 and wider, so no
    /// ordinary screen and no splitscreen pane moves.</summary>
    public static float ColumnOutdent(Vector2 boxSize, float referenceMargin)
    {
        // ⚠ The same one-pixel allowance ReadingBox makes, for the same reason: a 4P grid's
        // nominally 16:9 pane is a fraction narrow, and that must not read as a narrow screen.
        float width = boxSize.Y * ReferenceAspect;
        return width - boxSize.X >= 1f ? Mathf.Max(0f, referenceMargin - MinimumColumnMargin) : 0f;
    }

    /// <summary>Constrain a user-selected HUD text multiplier to its supported range.</summary>
    public static float ClampTextScale(float scale) => Mathf.Clamp(scale, MinimumTextScale, MaximumTextScale);

    private static float TextScale(string key) => ClampTextScale(Config.GetFloat(key, 1f));
}
