using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>Per-pane Dogfight HUD, <c>--vs</c> only (docs/architecture.md): a status line (time,
/// this pane's own K/D, the leader) and an edge-arrow + clock-bearing marker per living opponent in
/// that opponent's identity colour, the splitscreen answer to the original's radar, in the marker
/// HUDs' visual language. A kill goes to <see cref="HudMessages"/>, the one message element the
/// original has, rather than to a banner of this HUD's own.
/// The single-target marker this shape came from is <see cref="TargetHud"/>'s, which draws in every
/// flight session rather than only <c>--vs</c>.
/// ⚠ Read opponent positions off <see cref="Rigs"/> and project through THIS pane's camera. Never
/// <c>AnimRuntime.PlayerPosition</c>: it is a P1-only singleton.</summary>
public sealed partial class VersusHud : Control
{
    /// <summary>Which pane this draws in (0-based), this pane's own K/D, and the identity every
    /// opponent marker excludes.</summary>
    public int PlayerIndex;

    /// <summary>Every rig in the session (this player's own included, skipped when drawing
    /// markers), set once the whole field is built. Null in a solo <c>--vs --players=1</c>
    /// session, nothing to mark.</summary>
    public IReadOnlyList<PlayerRig>? Rigs;

    // 1440p reference metrics (scaled by HudMetrics, matches TargetHud's calibration).
    private const int RefStatusFont = 19;
    private const int RefMarkerFont = 14;
    private const float RefStatusY = 100f;    // same slot as StuntRunHud's run-status line
    private const float RefArrowLen = 18f;
    private const float RefArrowHalf = 8f;
    private const float RefTextGap = 8f;
    private const float RefOnScreenLift = 22f; // gap above a plane's own projected point

    private static readonly Color HudBlue = new(0.55f, 0.78f, 1f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);

    private VersusMatch? _match;
    private Camera3D _camera = null!;

    /// <summary>This pane's own world pose, fed every frame by FlightController, opponent clock
    /// bearings read off it, exactly like TargetHud's PlanePos/HeadingDeg.</summary>
    public Vector3 PlanePos { get; set; }

    /// <summary>This pane's own nose heading, 0 = north (−Z), see <see cref="PlanePos"/>.</summary>
    public float HeadingDeg { get; set; }

    /// <summary>Binds the match + this pane's own camera (opponent markers project through it).
    /// Add to the HUD canvas; <see cref="Rigs"/> is attached once the whole field is built, and
    /// <see cref="PlanePos"/>/<see cref="HeadingDeg"/> every frame, nothing else needs feeding,
    /// the match's own state is always current.</summary>
    public static VersusHud Build(VersusMatch match, int playerIndex, Camera3D camera)
    {
        return new VersusHud
        {
            _match = match,
            PlayerIndex = playerIndex,
            _camera = camera,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
        };
    }

    /// <summary>The line a Downed report anywhere in the match posts to the message stack: killer
    /// named when it was a weapon kill, a plain "DOWN" otherwise, which is a hull spent by a ram or
    /// by anything else with no seat to credit. Static, since the stack it lands in belongs to the
    /// reading pane, not to this one.</summary>
    public static string KillLine(int? killer, int victim) =>
        killer is int k
            ? $"{SplitScreen.PlayerTag(k)} DOWNED {SplitScreen.PlayerTag(victim)}"
            : $"{SplitScreen.PlayerTag(victim)} DOWN";

    public override void _Process(double delta)
    {
        // Track the pane (resizable window / splitscreen layout) and repaint every frame, the
        // clock needs it.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Same zero-size guard as TargetHud: a draw can land before _Process has sized this pane.
        float s = Size.Y <= 0f ? 0f : HudMetrics.Scale(this);
        if (s <= 0f)
            return;
        var font = GetThemeDefaultFont();
        int statusFont = Mathf.Max(1, Mathf.RoundToInt(RefStatusFont * s * HudMetrics.StatusTextScale));
        float cx = Size.X / 2f;

        if (_match is { } match)
            DrawCentered(font, new Vector2(cx, RefStatusY * s), StatusLine(match), statusFont, HudBlue);

        if (Rigs == null)
            return;
        int markerFont = Mathf.Max(1, Mathf.RoundToInt(RefMarkerFont * s * HudMetrics.MarkerTextScale));
        foreach (var opp in Rigs)
        {
            if (opp.Index == PlayerIndex || opp.Controller is not { Crashed: false } c)
                continue; // this pane's own seat, or an opponent out of the fight for now
            DrawOpponent(font, c.GlobalPosition, SplitScreen.PlayerColor(opp.Index),
                SplitScreen.PlayerTag(opp.Index), s, markerFont);
        }
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    private string StatusLine(VersusMatch match)
    {
        string time = match.TimeLimit > 0f ? $"{FormatTime(match.TimeRemaining)}   " : "";
        string kd = $"K/D {match.KillsOf(PlayerIndex)}/{match.DeathsOf(PlayerIndex)}";
        return $"{time}{kd}   {LeaderText(match)}";
    }

    // The sole rank-1 player's tag, or TIED while nobody leads (including 0-0 before the
    // first kill).
    private string LeaderText(VersusMatch match)
    {
        var leaders = match.Standings().Where(st => st.Rank == 1).ToList();
        return leaders.Count == 1 ? $"LEADER {SplitScreen.PlayerTag(leaders[0].PlayerIndex)}" : "LEADER TIED";
    }

    // One opponent's marker: on screen, their tag floats just above the projected
    // point; off screen (or behind), an edge arrow + "N o'clock" bearing, EdgeMarker's placement,
    // one instance per opponent instead of one stunt zone.
    private void DrawOpponent(Font font, Vector3 pos, Color color, string tag, float s, int fontSize)
    {
        bool behind = _camera.IsPositionBehind(pos);
        Vector2 sp = _camera.UnprojectPosition(pos);
        var placed = EdgeMarker.Resolve(sp, behind, Size);
        if (placed.OnScreen)
        {
            DrawTag(font, sp + new Vector2(0f, -RefOnScreenLift * s), tag, color, fontSize);
            return;
        }
        DrawArrow(placed.Anchor, placed.Dir, RefArrowLen * s, RefArrowHalf * s, s, color);
        DrawTag(font, placed.Anchor - placed.Dir * (RefArrowLen + RefTextGap) * s,
            $"{tag}  {EdgeMarker.ClockHour(PlanePos, HeadingDeg, pos)} o'clock", color, fontSize);
    }

    private void DrawArrow(Vector2 tip, Vector2 dir, float len, float half, float s, Color color)
    {
        var perp = new Vector2(-dir.Y, dir.X);
        var b1 = tip - dir * len + perp * half;
        var b2 = tip - dir * len - perp * half;
        var off = new Vector2(1.5f, 1.5f) * s;
        DrawColoredPolygon(new[] { tip + off, b1 + off, b2 + off }, Shadow);
        DrawColoredPolygon(new[] { tip, b1, b2 }, color);
    }

    private void DrawTag(Font font, Vector2 center, string text, Color color, int fontSize)
    {
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        float h = font.GetHeight(fontSize);
        var p = new Vector2(center.X - w / 2f, center.Y - h / 2f + font.GetAscent(fontSize));
        DrawString(font, p + Vector2.One, text, HorizontalAlignment.Left, -1f, fontSize, Shadow);
        DrawString(font, p, text, HorizontalAlignment.Left, -1f, fontSize, color);
    }

    // Draws one horizontally-centred line at `anchor`.X, top-anchored
    // at .Y, with a 1 px drop shadow, MarkerDraw's Lines, single-line.
    private void DrawCentered(Font font, Vector2 anchor, string text, int fontSize, Color color)
    {
        float ascent = font.GetAscent(fontSize);
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        var p = new Vector2(anchor.X - w / 2f, anchor.Y + ascent);
        DrawString(font, p + Vector2.One, text, HorizontalAlignment.Left, -1f, fontSize, Shadow);
        DrawString(font, p, text, HorizontalAlignment.Left, -1f, fontSize, color);
    }
}
