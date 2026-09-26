using System.Text;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over the cover a session starts under: what it stands over on a real window,
/// that it holds the whole frame opaque in a dark tone until the session reports its first real
/// frame, that the fade then comes up over its own second, and that a <c>--det</c> run carries no
/// cover at all, so the first frame a pinned golden hashes is still the world.
/// ⚠ The drawn frame itself is not checked here. A suite runs inside one <c>_Ready</c> and never
/// yields, so a canvas item built in it issues no draw commands and photographs as an empty frame
/// however many times <c>RenderingServer.ForceDraw</c> is called; the covered frame is proved out
/// of process by a <c>--no-det --screenshot</c> render instead (see GoldenShot's own note).
/// </summary>
internal static class SessionStartCoverSuites
{
    /// <summary>The start cover on a real window: its canvas layer draws over the HUD and the sun
    /// wash and under the board the load screen uses, its rect covers the whole window opaque in
    /// the dark start tone for as long as the session has not reported its first real frame, the
    /// frame the session first reads ready is still opaque, the fade then reaches nothing over its
    /// own second and the cover reports itself finished, and a deterministic run builds no cover
    /// at all.</summary>
    [Suite("session-start-cover",
        "the cover a session starts under: its canvas layer sits over the HUD and the sun wash "
        + "and under the board the load screen draws on, its rect covers the whole window in the "
        + "dark start tone for as long as the session reports no first frame, the frame it first "
        + "reads ready still draws opaque, the fade reaches nothing after its own second and the "
        + "cover then reports finished, and --det builds no cover at all, so a pinned golden's "
        + "first frame is still the world")]
    internal static void StartCoverHoldsThenFades(TestContext ctx)
    {
        var report = new StringBuilder();

        CheckDeterministicRunIsUncovered(ctx, report);
        CheckHoldThenFade(ctx, report);

        ctx.WriteArtifact($"test-session-start-cover.txt", report.ToString());
    }

    // The det gate builds nothing, so nothing can paint over the world a golden hashes or the frame
    // --frames=N counts to.
    private static void CheckDeterministicRunIsUncovered(TestContext ctx, StringBuilder report)
    {
        var none = SessionStartFade.Build(det: true, () => false);
        ctx.Check(none == null, $"a --det session builds no start cover at all");
        report.AppendLine($"det: cover={(none == null ? "none" : "BUILT")}");
        none?.QueueFree();
    }

    // The hold and the fade on a live window, driven a frame at a time: a suite runs inside one
    // _Ready and never yields, so the cover's own tick is called rather than waited for.
    private static void CheckHoldThenFade(TestContext ctx, StringBuilder report)
    {
        bool ready = false;
        var cover = SessionStartFade.Build(det: false, () => ready);
        if (cover == null)
        {
            ctx.Check(false, $"a session outside --det builds a start cover");
            return;
        }

        ctx.Host.AddChild(cover);
        try
        {
            CheckWhatItStandsOver(ctx, report, cover);
            CheckHold(ctx, report, cover);

            // The frame the session first answers ready is still fully covered, so no frame of the
            // world escapes before the fade starts.
            ready = true;
            cover.Tick(1f / 60f);
            ctx.Check(cover.Alpha >= 1f, $"the frame the session reads ready still draws opaque");

            CheckFade(ctx, report, cover);
        }
        finally
        {
            ctx.Host.RemoveChild(cover);
            cover.QueueFree();
        }
    }

    // Where the cover sits in the canvas order, which is what decides whether anything of the
    // world can reach the frame around it.
    private static void CheckWhatItStandsOver(TestContext ctx, StringBuilder report, SessionStartFade cover)
    {
        if (cover.Layer is not { } layer)
        {
            ctx.Check(false, $"the cover in the tree owns a canvas layer");
            return;
        }

        ctx.Check(
            layer.Layer > HudLayers.Hud && layer.Layer > HudLayers.SunWash,
            $"the cover's layer {layer.Layer} draws over the HUD and the sun wash");
        ctx.Check(
            layer.Layer < HudLayers.Board,
            $"the cover's layer {layer.Layer} draws under the load screen's board layer {HudLayers.Board}");
        report.AppendLine($"layer={layer.Layer} hud={HudLayers.Hud} board={HudLayers.Board}");
    }

    // Opaque, in the dark tone, over the whole window, for as long as the session says nothing.
    private static void CheckHold(TestContext ctx, StringBuilder report, SessionStartFade cover)
    {
        for (int frame = 0; frame < 120; frame++)
        {
            cover.Tick(1f / 60f);
        }

        ctx.Check(cover.Alpha >= 1f, $"two seconds of frames with no session answer leave the cover opaque");
        ctx.Check(!cover.Finished, $"a holding cover is not finished, so the launcher keeps it");
        if (Rect(cover) is not { } rect)
        {
            ctx.Check(false, $"the cover owns a full-screen rect");
            return;
        }

        var window = ctx.Host.GetViewport().GetVisibleRect().Size;
        ctx.Check(
            rect.Size.X >= window.X && rect.Size.Y >= window.Y,
            $"the rect covers the whole {window.X}x{window.Y} window ({rect.Size.X}x{rect.Size.Y})");
        ctx.Check(
            rect.Color.A >= 1f && IsStartTone(rect.Color),
            $"the held cover paints the start tone at full alpha ({rect.Color})");
        ctx.Check(
            rect.Color.R > 0f || rect.Color.G > 0f || rect.Color.B > 0f,
            $"the start tone is dark and not black ({rect.Color.R},{rect.Color.G},{rect.Color.B})");
        report.AppendLine(
            $"held: alpha={cover.Alpha} colour={rect.Color} rect={rect.Size.X}x{rect.Size.Y} window={window.X}x{window.Y}");
    }

    // The ramp itself, frame by frame: down over the fade's own second and gone at its end.
    private static void CheckFade(TestContext ctx, StringBuilder report, SessionStartFade cover)
    {
        float opened = cover.Alpha;
        for (int frame = 0; frame < 30; frame++)
        {
            cover.Tick(1f / 60f);
        }

        float half = cover.Alpha;
        ctx.Check(
            half < opened && half > 0f,
            $"half a second into the fade the cover is part way up ({half})");
        for (int frame = 0; frame < 45; frame++)
        {
            cover.Tick(1f / 60f);
        }

        ctx.Check(cover.Alpha <= 0f, $"the cover is gone one second after the session was ready");
        ctx.Check(cover.Finished, $"the spent cover reports finished, so the launcher drops it");
        if (Rect(cover) is { } rect)
        {
            ctx.Check(rect.Color.A <= 0f, $"the spent rect paints nothing ({rect.Color.A})");
        }

        if (cover.Layer is { } layer)
        {
            ctx.Check(!layer.Visible, $"the spent cover's layer is hidden rather than drawn clear");
        }

        report.AppendLine($"fade: opened={opened} half={half} end={cover.Alpha}");
    }

    // The cover's own rect, or null where the node never built one.
    private static ColorRect? Rect(SessionStartFade cover) =>
        cover.Layer is { } layer && layer.GetChildCount() > 0 ? layer.GetChild(0) as ColorRect : null;

    // Is this the tone the cover fades up from? Painted from the same constants, so an exact match.
    private static bool IsStartTone(Color colour) =>
        colour.R == StartCover.ToneR && colour.G == StartCover.ToneG && colour.B == StartCover.ToneB;
}
