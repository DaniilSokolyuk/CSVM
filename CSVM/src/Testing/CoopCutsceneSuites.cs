using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.Testing;

/// <summary>The window a cutscene plays in: an episode gives pane 1 the whole window and
/// takes the other panes and their listeners down, and both exits hand the rig back. Driven at
/// 2, 3 and 4 panes against the real <see cref="SplitScreen"/> and the real
/// <see cref="CutsceneController"/>, wired through the one seam the session wires. The listener
/// count is the check the rest of it exists for: the pinned model is one listener per pane, and a
/// collapse that leaves none takes every 3D emitter in the world silent with it
/// (docs/architecture.md, <c>src/UI/Boards/SplitScreen.cs</c>).</summary>
internal static class CoopCutsceneSuites
{
    // One of CutsceneController.IntroAnims, so the host answers for it with nothing registered,
    // and an intro is the episode a campaign session actually collapses the window for.
    private const string Anim = "generic_intro";

    [Suite("campaign-coop-cutscene-fullscreen",
        "the window a cutscene plays in at 2, 3 and 4 panes: an episode gives pane 1 the whole "
        + "window, takes the other panes down and leaves EXACTLY ONE listener-enabled viewport "
        + "rather than none, and both exits (the definition ending and a player's skip) lay "
        + "the panes back out with a listener each; an intro whose first code lands before the "
        + "rig exists collapses as the rigs bind, and a skip names the player in their colour")]
    internal static void CampaignCoopCutsceneFullscreen(TestContext ctx)
    {
        var window = ctx.Host.GetViewport().GetVisibleRect().Size;
        ctx.Check(window.X > 1f && window.Y > 1f,
            $"the test host has a real window ({window}) for the panes to be laid out over");
        var report = new StringBuilder();
        for (int players = 2; players <= SplitScreen.MaxPlayers; players++)
        {
            Fill(ctx, players, window, report);
        }

        BindsAfterTheCodeLands(ctx, window, report);
        NamesTheSkipper(ctx, report);
        ctx.WriteArtifact("test-campaign-coop-cutscene-fullscreen.txt", report.ToString());
        ctx.Note($"a cutscene fills the window at 2, 3 and 4 panes, and both exits give them back");
    }

    private static Panes Read(SplitScreen split)
    {
        var rects = new List<Rect2>();
        var visible = new List<bool>();
        int listeners = 0;
        int drawing = 0;
        for (int i = 0; i < split.Views.Count; i++)
        {
            var pane = (SubViewportContainer)split.Views[i].GetParent();
            rects.Add(new Rect2(pane.Position, pane.Size));
            visible.Add(pane.Visible);
            if (split.Views[i].AudioListenerEnable3D)
            {
                listeners++;
            }

            if (split.Views[i].RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled)
            {
                drawing++;
            }
        }

        return new Panes(rects, visible, listeners, drawing);
    }

    private static bool OnlyFirstDraws(Panes panes)
    {
        for (int i = 1; i < panes.Visible.Count; i++)
        {
            if (panes.Visible[i])
            {
                return false;
            }
        }

        return panes.Visible[0];
    }

    private static bool AllDraw(Panes panes)
    {
        foreach (bool drawn in panes.Visible)
        {
            if (!drawn)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LaidOutAsPanes(Panes panes, Vector2 window)
    {
        for (int i = 0; i < panes.Rects.Count; i++)
        {
            if (!panes.Rects[i].IsEqualApprox(
                SplitScreen.PaneRect(i, panes.Rects.Count, window, SplitScreen.SideBySide(window))))
            {
                return false;
            }
        }

        return true;
    }

    // One pane count, driven twice: an episode ended by its definition and an episode a player
    // skips. Both exits go through the controller's own restore, and the rig is read either side
    // of each of them.
    private static void Fill(TestContext ctx, int players, Vector2 window, StringBuilder report)
    {
        var split = SplitScreen.Build(players, ctx.Host.GetViewport());
        ctx.Host.AddChild(split);
        var world = new Node3D { Name = "world" };
        ctx.Host.AddChild(world);
        var runtime = new AnimRuntime();
        ctx.Host.AddChild(runtime);
        // An empty program, bound: the host asks the runtime whether its definition is still
        // running, and a program carrying none answers "ended", which is the exit under test.
        runtime.Bind(world, new AnimProgram());
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            // The session's own line, verbatim: a suite that collapsed the rig itself would pin
            // the layout and nothing about who asks for it.
            cutscene.FillsWindow = fills => split.Fill(fills);
            cutscene.BindWorld(runtime);

            var before = Read(split);
            report.AppendLine($"{players}P before: {before.Line}");
            ctx.Check(LaidOutAsPanes(before, window) && AllDraw(before)
                && before.Listeners == players && before.Drawing == players,
                $"{players}P starts as {players} laid-out panes, each of them drawing and a listener: {before.Line}");

            cutscene.Host(CutsceneController.CodeHoldsWorld, Anim);
            var filled = Read(split);
            report.AppendLine($"{players}P during: {filled.Line}");
            ctx.Check(split.Filled && filled.Rects[0].IsEqualApprox(new Rect2(Vector2.Zero, window)),
                $"{players}P: an episode gives pane 1 the whole {window} window: {filled.Line}");
            ctx.Check(OnlyFirstDraws(filled) && filled.Drawing == 1,
                $"…and takes the other {players - 1} pane(s) down, target and all, rather than paying for {players - 1} more copies of the same shot: {filled.Line}");
            ctx.Check(filled.Listeners == 1,
                $"…leaving EXACTLY ONE listener-enabled viewport, never none: {filled.Line}");

            // Nothing is playing in this runtime's program, so the tick that asks finds the
            // definition ended, the ordinary exit, driven through the ordinary path.
            cutscene.Tick();
            var ended = Read(split);
            report.AppendLine($"{players}P after the definition ended: {ended.Line}");
            ctx.Check(!cutscene.Playing && !split.Filled && LaidOutAsPanes(ended, window),
                $"{players}P: the definition ending lays the panes back out: {ended.Line}");
            ctx.Check(AllDraw(ended) && ended.Listeners == players && ended.Drawing == players,
                $"…drawing again, with every pane a listener as the pinned model has it: {ended.Line}");

            cutscene.Host(CutsceneController.CodeHoldsWorld, Anim);
            ctx.Check(split.Filled && Read(split).Listeners == 1,
                $"{players}P: a second episode fills the window again, so the first exit restored state rather than a picture");
            ctx.Check(cutscene.Skip(players - 1),
                $"…and the last player's skip is taken");
            var skipped = Read(split);
            report.AppendLine($"{players}P after a skip: {skipped.Line}");
            ctx.Check(!split.Filled && LaidOutAsPanes(skipped, window) && AllDraw(skipped)
                && skipped.Listeners == players && skipped.Drawing == players,
                $"{players}P: a skip is the same exit and gives back the same rig: {skipped.Line}");
        }
        finally
        {
            cutscene.Free();
            runtime.Free();
            world.Free();
            split.Free();
        }
    }

    // The intro's own order: its first code lands in the animation bootstrap, before the pane rig
    // exists, so the seam is null when the episode takes the session and the collapse has to be
    // re-raised as the rigs bind.
    private static void BindsAfterTheCodeLands(TestContext ctx, Vector2 window, StringBuilder report)
    {
        SplitScreen? split = null;
        var world = new Node3D { Name = "world" };
        ctx.Host.AddChild(world);
        var runtime = new AnimRuntime();
        ctx.Host.AddChild(runtime);
        // An empty program, bound: the host asks the runtime whether its definition is still
        // running, and a program carrying none answers "ended", which is the exit under test.
        runtime.Bind(world, new AnimProgram());
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            cutscene.FillsWindow = fills => split?.Fill(fills);
            cutscene.BindWorld(runtime);
            cutscene.Host(CutsceneController.CodeHoldsWorld, Anim);
            ctx.Check(cutscene.Playing,
                $"the episode took the session with no pane rig built yet, the way a mission intro does");

            split = SplitScreen.Build(2, ctx.Host.GetViewport());
            ctx.Host.AddChild(split);
            cutscene.BindRigs(Array.Empty<PlayerRig>(), () => Array.Empty<FlightController>());
            var panes = Read(split);
            report.AppendLine($"bound after the code: {panes.Line}");
            ctx.Check(split.Filled && panes.Rects[0].IsEqualApprox(new Rect2(Vector2.Zero, window))
                && panes.Listeners == 1,
                $"and binding the rigs collapses the rig it now has, rather than leaving the intro in two panes: {panes.Line}");
        }
        finally
        {
            cutscene.Free();
            runtime.Free();
            world.Free();
            split?.Free();
        }
    }

    // Who skipped, in that player's own colour: with a field of humans the picture ending is one
    // player's decision, and the line is what tells the others whose.
    private static void NamesTheSkipper(TestContext ctx, StringBuilder report)
    {
        var split = SplitScreen.Build(3, ctx.Host.GetViewport());
        ctx.Host.AddChild(split);
        try
        {
            ctx.Check(split.SkipNotice == null,
                $"a session nobody has skipped anything in carries no skip line at all");
            split.NoteSkip(2);
            var notice = split.SkipNotice;
            report.AppendLine($"skip notice: '{notice?.Text}' in {notice?.Modulate}");
            ctx.Check(notice != null && notice.Text.Contains(SplitScreen.PlayerTag(2)),
                $"a skip by player 3 names that player on screen: '{notice?.Text}'");
            ctx.Check(notice != null && notice.Modulate.IsEqualApprox(SplitScreen.PlayerColor(2)),
                $"…in that player's own identity colour, the one their pane and their menu cursor use: {notice?.Modulate}");
        }
        finally
        {
            split.Free();
        }
    }

    // The state one reading of the rig found: what each pane covers, which of them draw, and how
    // many of them are 3D audio listeners.
    private readonly record struct Panes(
        IReadOnlyList<Rect2> Rects, IReadOnlyList<bool> Visible, int Listeners, int Drawing)
    {
        internal string Line =>
            $"rects [{string.Join(" ", Rects)}] visible [{string.Join(",", Visible)}] "
            + $"listeners {Listeners} drawing {Drawing}";
    }
}
