using System.Collections.Generic;
using CSVM.Flight.Hud;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>The stunt run's own readouts, one per pane: the run-status line (clock + zones
/// cleared), the one-shot intro banner, the zone-cleared flash and the all-complete banner, which
/// in a race becomes this pilot's placing and who they are still waiting on. A viewport-filling
/// Control fed nothing per frame; it reads the run.
/// ⚠ The Danger Zone marker itself is NOT here. A zone is an objective-flagged entry on the
/// pilot's own target cycle (<see cref="StuntMission.CollectTargets"/>) and
/// <see cref="TargetHud"/> draws it like every other objective, so there is one marker style and
/// one cycle.</summary>
public sealed partial class StuntRunHud : Control
{
    /// <summary>The splitscreen race this pilot is flying in, or null in a solo run.
    /// Set, the all-zones-cleared banner becomes their placing + finish time and says who they are
    /// still waiting on; the shared ranked board (<c>StuntRaceBoard</c>) takes over from
    /// there. Paired with <see cref="PlayerIndex"/>.</summary>
    public StuntRace? Race;

    /// <summary>Which player's pane this HUD draws in (0-based), picks their row out of
    /// <see cref="Race"/>.</summary>
    public int PlayerIndex;

    // 1440p reference metrics (scaled by viewport height).
    private const int RefStatusFont = 19;    // the run-status line
    private const int RefBannerFont = 26;    // intro / all-complete banners
    private const float RefStatusY = 100f;   // run-status baseline y, just under the compass tape
    private const float IntroDuration = 5f;  // s the run-start line shows (fades the last second)
    private const float FlashDuration = 1.6f;// s a zone-cleared flash shows

    private static readonly Color HudBlue = MarkerDraw.HudBlue;
    private static readonly Color HudGreen = new(0.60f, 1f, 0.70f); // completion feedback

    private StuntMission _mission = null!;
    private float _flash;         // s left on the zone-cleared flash
    private string _flashText = "";

    /// <summary>Binds the run and subscribes to the completion flash. Add to the HUD canvas;
    /// nothing needs feeding per frame.</summary>
    public static StuntRunHud Build(StuntMission mission)
    {
        var hud = new StuntRunHud
        {
            _mission = mission,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
        };
        mission.ZoneCompleted += hud.OnZoneCompleted;
        return hud;
    }

    public override void _ExitTree() => _mission.ZoneCompleted -= OnZoneCompleted;

    public override void _Process(double delta)
    {
        // Track the viewport (resizable window) and repaint, the clock advances every frame.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        if (_flash > 0f)
            _flash -= (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // A draw can land before _Process has sized us; Godot's font cache errors on zero size.
        float s = Size.Y <= 0f ? 0f : HudMetrics.Scale(this);
        if (s <= 0f)
            return;
        var font = GetThemeDefaultFont();
        // Never round a scaled font down to 0, a quarter-height 4P pane scales hard.
        int statusFont = Mathf.Max(1, Mathf.RoundToInt(RefStatusFont * s * HudMetrics.StatusTextScale));
        int bannerFont = Mathf.Max(1, Mathf.RoundToInt(RefBannerFont * s));
        float cx = Size.X / 2f;

        // Run-status: elapsed time + zones done, top-centre under the compass tape.
        DrawLines(font, new Vector2(cx, RefStatusY * s),
            new[] { $"{StuntMission.FormatTime(_mission.Elapsed)}    ZONES {_mission.CompletedCount}/{_mission.TotalCount}" },
            statusFont, HudBlue, topAnchored: true);

        // One-shot intro banner (fades over the last second of its window).
        if (_mission.Elapsed < IntroDuration && !_mission.AllComplete && _mission.IntroLine.Length > 0)
        {
            float a = Mathf.Clamp(IntroDuration - _mission.Elapsed, 0f, 1f);
            DrawLines(font, new Vector2(cx, Size.Y * 0.26f), new[] { _mission.IntroLine },
                bannerFont, new Color(HudBlue, a));
        }

        // Brief zone-cleared flash (fades over its last 0.4 s).
        if (_flash > 0f)
            DrawLines(font, new Vector2(cx, Size.Y * 0.34f), new[] { _flashText }, bannerFont,
                new Color(HudGreen, Mathf.Clamp(_flash / 0.4f, 0f, 1f)));

        if (_mission.AllComplete)
            DrawLines(font, new Vector2(cx, Size.Y * 0.26f), CompleteBanner(), bannerFont, HudGreen);
    }

    private void OnZoneCompleted(StuntZone z)
    {
        _flash = FlashDuration;
        _flashText = z.Description.Length > 0 ? $"{z.Description} CLEARED" : "DANGER ZONE CLEARED";
    }

    // The banner shown in this player's pane once they have cleared every zone. Solo: the
    // run is simply over (the results board is coming up in the same pane). In a race:
    // their placing + finish time, held while the rest of the field still flies, the shared
    // ranked board only appears when the last pilot is in.
    private string[] CompleteBanner()
    {
        if (Race?.Of(PlayerIndex) is not { } me)
            return new[] { "ALL DANGER ZONES CLEARED", StuntMission.FormatTime(_mission.Elapsed) };
        var lines = new List<string>(3)
        {
            $"FINISHED {StuntRace.Ordinal(me.Rank)}",
            StuntMission.FormatTime(me.FinishTime),
        };
        if (!Race.AllFinished)
        {
            int waiting = Race.Racers.Count - Race.FinishedCount;
            lines.Add(waiting == 1 ? "waiting for 1 pilot…" : $"waiting for {waiting} pilots…");
        }
        return lines.ToArray();
    }

    private void DrawLines(Font font, Vector2 anchor, string[] lines, int fontSize, Color color,
        bool topAnchored = false) =>
        MarkerDraw.Lines(this, font, anchor, lines, fontSize, color, topAnchored);
}
