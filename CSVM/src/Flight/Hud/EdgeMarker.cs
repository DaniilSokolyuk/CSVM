using Godot;

namespace CSVM.Flight.Hud;

/// <summary>The off-screen edge marker's placement rules, the one home for what
/// VersusHud and TargetHud each used to carry privately (docs/architecture.md): a world target's
/// marker sits at its projected point while that point is on the pane, else clamps into the inset
/// screen edge with an outward arrow direction and a second point on the pane boundary for the
/// arrow to reach; the edge tag carries a clock-hour bearing. Engine-free, the caller projects
/// through its own camera and passes the result in, and each HUD keeps its own arrow, tag and
/// label styling. Decode: <see href="../../../../docs/org/spyglass.md">org/spyglass.md</see>.</summary>
public static class EdgeMarker
{
    /// <summary>The share of each pane axis the marker ANCHOR is held inside, the original's own
    /// per-axis 5 percent (docs/org/spyglass.md "Where the anchor is"). A fraction rather than a
    /// reference-pixel count, so nothing scales it by HudMetrics. ⚠ Do not answer the on-screen
    /// test from this rectangle; the original's off-screen flag is the whole viewport, so a target
    /// inside the inset band is on screen and draws no arrow, tag or disc.</summary>
    public const float InsetFraction = 0.05f;

    // The arrow's tip clamps to the pane less this at the far corner, the original's own 1.001
    // device pixels, so a tip never lands on the boundary its viewport rectangle excludes.
    private const float TipInset = 1.001f;

    /// <summary>Resolves a projected target to its marker placement. Off screen is the whole pane,
    /// the original's own flag; the inset rectangles only place what is then drawn.
    /// <paramref name="behind"/> is the caller's <c>Camera3D.IsPositionBehind</c> answer: such a
    /// projection is mirrored through centre, so it is forced off screen and its direction flipped
    /// back, and one landing on centre (dead ahead or astern) points down.
    /// <paramref name="anchorInset"/> holds the ANCHOR further in, the disc's half window.</summary>
    public static Placement Resolve(Vector2 projected, bool behind, Vector2 paneSize,
        float anchorInset = 0f)
    {
        if (!behind && new Rect2(Vector2.Zero, paneSize).HasPoint(projected))
        {
            return new Placement(true, projected, Vector2.Zero, projected);
        }

        var center = paneSize / 2f;
        var dir = projected - center;
        if (behind)
        {
            dir = -dir;
        }

        dir = dir.LengthSquared() < 1f ? Vector2.Down : dir.Normalized();
        var inset = paneSize * InsetFraction;
        var inner = new Rect2(inset, paneSize - 2f * inset);
        var pane = new Rect2(Vector2.Zero, paneSize - new Vector2(TipInset, TipInset));
        return new Placement(false, ToBoundary(Shrink(inner, anchorInset), center, dir), dir,
            ToBoundary(pane, center, dir));
    }

    /// <summary>Relative bearing of <paramref name="targetPos"/> from the pilot's own heading in
    /// clock hours (12 = ahead, 3 = right, 6 = behind, 9 = left), the original's "N o'clock"
    /// suffix. Heading and bearing convention: 0 = north (−Z), 90 = east (+X).</summary>
    public static int ClockHour(Vector3 ownPos, float headingDeg, Vector3 targetPos)
    {
        var d = targetPos - ownPos;
        float bearing = Mathf.RadToDeg(Mathf.Atan2(d.X, -d.Z));
        float rel = Mathf.PosMod(bearing - headingDeg, 360f);
        int h = Mathf.RoundToInt(rel / 30f) % 12;
        return h == 0 ? 12 : h;
    }

    // Pull every wall of `rect` in by `by`, never past its own centre: on a pane too small to
    // hold the disc the anchor collapses to the middle instead of inverting the rectangle.
    private static Rect2 Shrink(Rect2 rect, float by)
    {
        if (by <= 0f)
        {
            return rect;
        }

        float w = Mathf.Max(0f, rect.Size.X - (2f * by));
        float h = Mathf.Max(0f, rect.Size.Y - (2f * by));
        var size = new Vector2(w, h);
        return new Rect2(rect.GetCenter() - (size / 2f), size);
    }

    // Walk from `from` along `dir` to the first wall of `rect`. The original steps out from the
    // rectangle's centre rather than clamping each axis on its own, which is what keeps both
    // clamped points on the marker's own bearing.
    private static Vector2 ToBoundary(Rect2 rect, Vector2 from, Vector2 dir)
    {
        float t = float.MaxValue;
        if (Mathf.Abs(dir.X) > 1e-4f)
        {
            t = Mathf.Min(t, ((dir.X > 0f ? rect.End.X : rect.Position.X) - from.X) / dir.X);
        }

        if (Mathf.Abs(dir.Y) > 1e-4f)
        {
            t = Mathf.Min(t, ((dir.Y > 0f ? rect.End.Y : rect.Position.Y) - from.Y) / dir.Y);
        }

        return t == float.MaxValue ? from : from + dir * t;
    }

    /// <summary>Where a marker goes. On screen: <see cref="Anchor"/> and <see cref="Tip"/> are the
    /// projected point and <see cref="Dir"/> is zero. Off screen: <see cref="Anchor"/> sits on the
    /// inset boundary and carries the marker and its label, <see cref="Tip"/> is where the same
    /// bearing meets the pane's own edge and is where the arrow points, and <see cref="Dir"/> is
    /// the normalized outward direction from the pane's centre.</summary>
    public readonly record struct Placement(bool OnScreen, Vector2 Anchor, Vector2 Dir, Vector2 Tip);
}
