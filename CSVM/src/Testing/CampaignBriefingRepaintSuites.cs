using System.Diagnostics;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// BL-485: the briefing's reveal is an animation, so whether it reaches the screen is a question
/// about frames rather than about a moment. This drives a real <see cref="LaunchMenu"/> frame by
/// frame and compares what the board surface is holding against a board freshly composed from the
/// page, so a shell that advances the reveal without repainting is caught the frame it happens.
/// A shot taken at a named second cannot see this, which is why it took a pilot to notice it.
/// </summary>
internal static class CampaignBriefingRepaintSuites
{
    // One driven frame, and how many of them: 90 s covers the longest shipped narration, so the
    // run reaches a completed reveal rather than stopping part-way through one.
    private const double StepDt = 1.0 / 60.0;
    private const int Frames = 5400;

    // How many separate compositions the screen must go through. A reveal that only repainted
    // when an objective line was uncovered produced fewer than a dozen over a whole briefing.
    private const int MinDistinct = 100;

    private static readonly System.Globalization.CultureInfo Invariant =
        System.Globalization.CultureInfo.InvariantCulture;

    // BL-485: the shell advanced the reveal every frame and repainted the board only when a
    // discrete property moved, which a shot taken at a named second cannot see.
    [Suite("campaign-briefing-repaint",
        "the briefing reveal reaching the screen, driven frame by frame through a real "
        + "LaunchMenu: the composed board the surface holds is compared against a board "
        + "composed from the page on each of 5400 driven frames, the reveal must compose the "
        + "screen afresh on far more than the handful of frames an objective count moves on, "
        + "and one frame's board is counted so the cost of repainting it is on the record")]
    internal static void CampaignBriefingRepaint(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var menu = MenuSuiteHost.Menu(ctx);
        ctx.Host.AddChild(menu);
        try
        {
            Drive(ctx, menu);
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
        }
    }

    // The two picture layers as the surface is holding them, compared against a board composed
    // now. Text and plaques are left out on purpose: the reveal lives in the pictures and the
    // route line, and a row's focus ink moves for reasons that are the cursor's, not the clock's.
    private static bool Agrees(ComposedBoard? shown, ComposedBoard fresh)
    {
        if (shown == null || shown.Pictures.Count != fresh.Pictures.Count
            || shown.Strokes.Count != fresh.Strokes.Count)
        {
            return false;
        }

        for (int i = 0; i < shown.Pictures.Count; i++)
        {
            if (!shown.Pictures[i].Equals(fresh.Pictures[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < shown.Strokes.Count; i++)
        {
            if (!shown.Strokes[i].Equals(fresh.Strokes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Drive(TestContext ctx, LaunchMenu menu)
    {
        // Opened the way a pilot opens it before being walked to the briefing. Opening straight
        // onto a board would leave the shell's join-strip comparison unsettled, and the board
        // would then repaint every frame for a reason that is not the reveal's.
        menu.ShowMenu();
        menu.ShowMenu("campaign-briefing:0");
        menu.SetProcess(false);
        if (menu.Campaign is not { } flow || flow.Page is not CampaignBriefingPage page)
        {
            throw new SuiteSkippedException("the briefing aid did not reach the briefing");
        }

        ctx.Check(page.State != null && page.Reveal != null,
            $"the briefing loaded this profile's next mission, so there is a reveal to run");
        if (page.Reveal == null)
        {
            return;
        }

        int startPictures = menu.ShownBoard?.Pictures.Count ?? 0;
        int stale = 0;
        int distinct = 0;
        int peakPictures = startPictures;
        ComposedBoard? previous = menu.ShownBoard;
        var clock = new Stopwatch();
        for (int frame = 0; frame < Frames; frame++)
        {
            clock.Start();
            menu._Process(StepDt);
            clock.Stop();
            var shown = menu.ShownBoard;
            var fresh = CampaignBoards.For(page, flow.Row);
            if (!Agrees(shown, fresh))
            {
                stale++;
            }

            if (shown != null && !Agrees(previous, shown))
            {
                distinct++;
            }

            peakPictures = Mathf.Max(peakPictures, shown?.Pictures.Count ?? 0);
            previous = shown;
        }

        Report(ctx, menu, page, stale, distinct, startPictures, peakPictures, clock);
    }

    private static void Report(
        TestContext ctx, LaunchMenu menu, CampaignBriefingPage page,
        int stale, int distinct, int startPictures, int peakPictures, Stopwatch clock)
    {
        string cost = (clock.Elapsed.TotalMilliseconds / Frames).ToString("0.000", Invariant);
        var shown = menu.ShownBoard;
        int primitives = shown != null ? Counted(shown) : -1;
        bool complete = page.Reveal is { Complete: true };
        ctx.Note($"driven {Frames} frames: stale={stale}, distinct boards={distinct}, pictures {startPictures} to {peakPictures}, primitives={primitives}, complete={complete}, {cost} ms per driven frame");
        ctx.Check(stale == 0,
            $"the board on screen matched the page on every one of {Frames} driven frames (stale={stale})");
        ctx.Check(distinct >= MinDistinct,
            $"and the screen was composed afresh {distinct} times over the reveal, not the handful a per-objective change test produced (floor {MinDistinct})");
        ctx.Check(peakPictures > startPictures,
            $"the reveal really did put elements on the board it started without ({startPictures} pictures at the start, {peakPictures} at its fullest)");
        ctx.Check(complete,
            $"and the reveal ran to its end inside the {Frames}-frame window");
        ctx.Check(primitives is > 0 and < 200,
            $"one frame's board stays a small composition, so repainting it every frame is cheap ({primitives} drawn primitives at the reveal's fullest)");
    }

    // A note's entries count as the lines they draw as, so the primitive count still says what one
    // frame costs now that the objectives are a flowed list rather than one row each.
    private static int Counted(ComposedBoard board)
    {
        int notes = 0;
        foreach (var note in board.Notes)
        {
            notes += note.Entries.Count;
        }

        return board.Pictures.Count + board.Strokes.Count + board.Lines.Count
            + board.Plaques.Count + notes;
    }
}
