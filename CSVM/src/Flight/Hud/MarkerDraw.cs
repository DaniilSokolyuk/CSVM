using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>The world-marker's drawing primitives: the target reticle, the off-screen arrow, and
/// the centred text block with its drop shadow and its clamped variant. <see cref="EdgeMarker"/>
/// decides WHERE a marker goes; this decides what it looks like once placed, so every HUD that
/// draws one reproduces the look instead of copying it. The caller owns the colour and the scaled
/// sizes, which is why none of them are baked in here.</summary>
public static class MarkerDraw
{
    /// <summary>1440p reference arrowhead length; scale by <see cref="HudMetrics"/> first.</summary>
    public const float RefArrowLen = 20f;

    /// <summary>1440p reference arrowhead half-width.</summary>
    public const float RefArrowHalf = 9f;

    /// <summary>1440p reference gap from the projected point (or the arrow tail) to the text.</summary>
    public const float RefTextGap = 10f;

    /// <summary>1440p reference radius of the on-screen target reticle.</summary>
    public const float RefReticleR = 9f;

    /// <summary>The marker's own blue (<c>HudBlue</c>), the colour the original draws a
    /// non-destructive objective and a danger zone in.</summary>
    public static readonly Color HudBlue = new(0.55f, 0.78f, 1f);

    /// <summary>The 1 px drop shadow every marker element carries, so the text stays readable
    /// over bright sky.</summary>
    public static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);

    /// <summary>The on-screen target reticle: a shadowed ring at the projected point.</summary>
    public static void Reticle(CanvasItem into, Vector2 p, float r, Color color)
    {
        into.DrawArc(p, r + 1f, 0f, Mathf.Tau, 20, Shadow, 2.5f);
        into.DrawArc(p, r, 0f, Mathf.Tau, 20, color, 1.5f);
    }

    /// <summary>The off-screen edge arrow: a shadowed head at <paramref name="tip"/> pointing along
    /// <paramref name="dir"/>, with the short tail stroke behind it.</summary>
    public static void Arrow(CanvasItem into, Vector2 tip, Vector2 dir, float len, float half,
        float s, Color color)
    {
        var perp = new Vector2(-dir.Y, dir.X);
        var b1 = tip - dir * len + perp * half;
        var b2 = tip - dir * len - perp * half;
        var off = new Vector2(1.5f, 1.5f) * s;
        into.DrawColoredPolygon(new[] { tip + off, b1 + off, b2 + off }, Shadow);
        into.DrawColoredPolygon(new[] { tip, b1, b2 }, color);
        into.DrawLine(tip - dir * len, tip - dir * (len + 8f * s), color, 2f * s);
    }

    /// <summary>Draws each line horizontally centred at <paramref name="anchor"/>.X with a 1 px drop
    /// shadow. Vertically the block is centred on <paramref name="anchor"/>.Y unless
    /// <paramref name="topAnchored"/>, in which case that is its top.</summary>
    public static void Lines(CanvasItem into, Font font, Vector2 anchor, IReadOnlyList<string> lines,
        int fontSize, Color color, bool topAnchored = false)
    {
        float lineH = font.GetHeight(fontSize);
        float ascent = font.GetAscent(fontSize);
        float y = topAnchored ? anchor.Y + ascent : anchor.Y - lines.Count * lineH / 2f + ascent;
        foreach (var line in lines)
        {
            float w = font.GetStringSize(line, HorizontalAlignment.Left, -1f, fontSize).X;
            var p = new Vector2(anchor.X - w / 2f, y);
            into.DrawString(font, p + Vector2.One, line, HorizontalAlignment.Left, -1f, fontSize, Shadow);
            into.DrawString(font, p, line, HorizontalAlignment.Left, -1f, fontSize, color);
            y += lineH;
        }
    }

    /// <summary>Like <see cref="Lines"/> (vertically centred) but keeps the whole block inside
    /// <paramref name="paneSize"/> less <paramref name="margin"/>, so an edge marker never spills
    /// off a corner.</summary>
    public static void LinesClamped(CanvasItem into, Font font, Vector2 center,
        IReadOnlyList<string> lines, int fontSize, Color color, Vector2 paneSize, float margin)
    {
        float lineH = font.GetHeight(fontSize);
        float totalH = lines.Count * lineH;
        float maxW = 0f;
        foreach (var line in lines)
        {
            maxW = Mathf.Max(maxW, font.GetStringSize(line, HorizontalAlignment.Left, -1f, fontSize).X);
        }

        center.X = Mathf.Clamp(center.X, margin + maxW / 2f, paneSize.X - margin - maxW / 2f);
        center.Y = Mathf.Clamp(center.Y, margin + totalH / 2f, paneSize.Y - margin - totalH / 2f);
        Lines(into, font, center, lines, fontSize, color);
    }
}
