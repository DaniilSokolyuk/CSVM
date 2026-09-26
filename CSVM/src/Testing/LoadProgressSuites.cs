using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over the load screen while a build holds the frame loop. The bar's fill and
/// the propeller's frame at each of the sixteen authored milestones, on a real board over a real
/// window. The pump draws them from inside the build.
/// Decode: docs/org/loading-screen.md.</summary>
internal static class LoadProgressSuites
{
    // The filmed mission, whose campaign sheet carries the propeller cycle the script authors.
    private const string FilmedChapter = "C3";
    private const string FilmedMission = "M01";

    // The strips the two families fill, at their extracted widths. Asserted rather than assumed:
    // the fill is a pixel clip against the bitmap's own width, so a different strip is a different
    // bar.
    private const int ChalkStripWidth = 236;
    private const int SheetStripWidth = 338;

    // The lamps the fill strip is drawn as (docs/org/loading-screen.md). The bar is a pixel clip,
    // not a count of lamps, so this is what a watcher counts rather than what the fill computes.
    private const int LampCount = 6;

    /// <summary>The load screen moving under a build. Both families find their fill strip and
    /// their propeller in the extraction. The fill at each authored milestone is the floored pixel
    /// clip of that strip's own width. The propeller names one of its six frames at its authored
    /// point. The still composition under the overlay is untouched at every step. The board
    /// installs the pump for its own tree lifetime alone, and a reported step puts a frame on
    /// screen from inside the build.</summary>
    [Suite("load-progress",
        "the load screen while a build holds the frame loop: the blackboard's prog_red and the "
        + "chart sheet's prog_redload both resolve at their authored width, the fill at each of "
        + "the sixteen authored milestones is floor(stripWidth * fraction) pixels and never "
        + "steps backwards, the propeller cycles the six extracted frames at its authored point "
        + "and rate, the campaign sheet's own content is unchanged under the overlay that moves, "
        + "the board owns the pump for exactly as long as it is in the tree so a CLI launch gains "
        + "no draw, every one of the sixteen steps reported back to back lights the strip a lamp "
        + "at a time on the window's own frames, and a reported step draws a frame from inside "
        + "the blocking build")]
    internal static void LoadScreenMoves(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var report = new StringBuilder();

        CheckFamily(ctx, report, campaign: false, sheet: null, stripWidth: ChalkStripWidth);
        var filmed = FilmedSheet(ctx);
        CheckFamily(ctx, report, campaign: true, sheet: filmed, stripWidth: SheetStripWidth);
        CheckSheetCycleIsTheScriptsOwn(ctx, report, filmed);
        CheckPumpOwnership(ctx, report, filmed);
        CheckEveryFractionReachesTheFrame(ctx, report);
        CheckPumpDuringAWorldBuild(ctx, report, filmed);

        ctx.WriteArtifact($"test-load-progress.txt", report.ToString());
    }

    // The filmed mission's own sheet, or null where the extraction cannot answer for it.
    private static LoadSheet? FilmedSheet(TestContext ctx)
    {
        foreach (var mission in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (!mission.ChapterFolder.Equals(FilmedChapter, StringComparison.OrdinalIgnoreCase)
                || !mission.MissionFolder.Equals(FilmedMission, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return LoadSheet.Load(
                ctx.ZrdrPath, ctx.MessagesPath,
                SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder),
                EscapeDialog.CampaignKey(mission.Campaign, mission.Mission),
                Session.Campaign.CampaignMementos.BitmapFor(null));
        }

        return null;
    }

    // One family's whole milestone walk, on a real board over the window so the strip's width is
    // the extraction's rather than a number restated here.
    private static void CheckFamily(
        TestContext ctx, StringBuilder report, bool campaign, LoadSheet? sheet, int stripWidth)
    {
        string family = campaign ? "chart sheet" : "blackboard";
        var board = LoadBoard.Build(
            ctx.DataRoot, ctx.ZrdrPath, ctx.MessagesPath, campaign, "FREE FLIGHT", null, sheet);
        ctx.Host.AddChild(board);
        try
        {
            if (board.GetChild(0) is not ComposedBoardView view)
            {
                ctx.Check(false, $"{family}: the board on a window builds its own view");
                return;
            }

            var still = view.Board ?? LoadScreens.Empty;
            var motion = LoadScreens.MotionFor(campaign, sheet);
            var art = view.ArtSize(new BoardArt(BoardArtLibrary.Rimage, motion.FillArt));
            ctx.Same(stripWidth, (int)art.X, $"{family}: {motion.FillArt} is the extracted strip");
            report.AppendLine(
                $"{family} fill={motion.FillArt} {(int)art.X}x{(int)art.Y} at ({motion.FillX},{motion.FillY}) "
                + $"propeller={motion.Propeller.Count} frame(s) at ({motion.PropellerX},{motion.PropellerY})");
            WalkMilestones(ctx, report, family, still, motion, art, stripWidth);
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
        }
    }

    // Every milestone in order: the fill is the floored clip, and it never steps back. The
    // propeller draws one of its own frames, and nothing under the overlay moves.
    private static void WalkMilestones(
        TestContext ctx, StringBuilder report, string family, ComposedBoard still,
        LoadMotion motion, Vector2 art, int stripWidth)
    {
        int previous = -1;
        var frames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Enum.GetValues<LoadStep>())
        {
            float fraction = LoadProgress.Milestones[(int)step];
            int frame = (int)step % LoadProgress.PropellerFrames;
            var painted = LoadScreens.Painted(still, motion, art.X, art.Y, fraction, frame);
            int lit = FillWidth(painted);
            int want = LoadProgress.FillPixels(stripWidth, fraction);
            ctx.Same(want, lit, $"{family}: {step} fills {want} px of {stripWidth} at {fraction}");
            ctx.Check(lit >= previous, $"{family}: {step}'s fill never steps back from {previous}");
            previous = lit;
            if (Propeller(painted, motion) is { } named)
            {
                frames.Add(named);
            }

            ctx.Same(
                still.Pictures.Count, painted.Pictures.Count,
                $"{family}: {step} leaves the still composition's {still.Pictures.Count} picture(s) alone");
            ctx.Same(
                still.Notes.Count, painted.Notes.Count,
                $"{family}: {step} leaves the parchment alone");
            report.AppendLine($"{family} {step} {fraction} fill={lit}px frame={frame}");
        }

        // Sixteen steps over a six-frame cycle: every frame is named, so nothing draws one still
        // picture for the whole build.
        ctx.Same(
            motion.Propeller.Count, frames.Count,
            $"{family}: the walk names every one of the cycle's frames");
    }

    // The chart sheet's propeller is the sheet's own Cycle beat, at the point and through the
    // frames its script authors, never the blackboard's placement.
    private static void CheckSheetCycleIsTheScriptsOwn(
        TestContext ctx, StringBuilder report, LoadSheet? sheet)
    {
        if (sheet == null)
        {
            ctx.Check(false, $"{FilmedChapter}/{FilmedMission} resolves its own loading sheet");
            return;
        }

        var motion = LoadScreens.MotionFor(true, sheet);
        ctx.Same(
            LoadProgress.PropellerFrames, motion.Propeller.Count,
            $"the sheet's script authors the whole six-frame cycle");
        ctx.Check(
            motion.Propeller.Count > 0 && motion.Propeller[0] == LoadScreens.Propeller[0],
            $"the sheet's cycle starts on {LoadScreens.Propeller[0]}, the frame its still draws");
        ctx.Check(
            motion.PropellerX != 0f || motion.PropellerY != 0f,
            $"the sheet's cycle sits where its script put it ({motion.PropellerX},{motion.PropellerY})");
        report.AppendLine(
            $"sheet cycle at ({motion.PropellerX},{motion.PropellerY}) frames="
            + string.Join(",", motion.Propeller));
    }

    // The pump belongs to the board and to nothing else. It is absent before one is up, the
    // board's own while it is, and gone again once it leaves. That keeps every CLI launch from
    // gaining a draw.
    private static void CheckPumpOwnership(TestContext ctx, StringBuilder report, LoadSheet? sheet)
    {
        var held = LoadProgress.Current;
        LoadProgress.Current = null;
        var board = LoadBoard.Build(
            ctx.DataRoot, ctx.ZrdrPath, ctx.MessagesPath, campaign: true, string.Empty, null, sheet);
        ctx.Check(LoadProgress.Current == null, $"no board up, so a build reports to nobody");
        ctx.Host.AddChild(board);
        try
        {
            ctx.Check(LoadProgress.Current != null, $"the board in the tree owns the pump");
            var view = board.GetChild(0) as ComposedBoardView;

            // The real path, pump and forced frame included: a build reports a step from inside
            // the block that owns the loop. The screen has to be repainted there or nowhere.
            LoadProgress.Report(LoadStep.RenderState);
            int first = view is { } opening ? FillWidth(opening.Moving) : -1;
            ctx.Check(first > 0, $"the first step lights {first} px from inside the build");

            // Reported at once, inside the pump's own 0.1 s window. A step the bar moved on is
            // drawn whatever the clock says. A fast build would otherwise show its first fraction
            // and nothing after it.
            LoadProgress.Report(LoadStep.Finished);
            int last = view is { } closing ? FillWidth(closing.Moving) : -1;
            ctx.Check(last > first, $"the last step lights {last} px, over the first's {first}");
            report.AppendLine($"pumped fill: first={first}px last={last}px");
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
        }

        ctx.Check(LoadProgress.Current == null, $"the board off the tree hands the pump back");
        LoadProgress.Current = held;
    }

    // Every authored fraction on the glass, counted off the rendered frame rather than off the
    // pump's own call count. The steps are reported back to back, which is the fast build a
    // throttle would swallow. Each one's frame is read back and its lamps counted.
    private static void CheckEveryFractionReachesTheFrame(TestContext ctx, StringBuilder report)
    {
        var held = LoadProgress.Current;
        LoadProgress.Current = null;
        var board = LoadBoard.Build(
            ctx.DataRoot, ctx.ZrdrPath, ctx.MessagesPath, campaign: false, "FREE FLIGHT", null);
        ctx.Host.AddChild(board);
        try
        {
            if (LoadProgress.Current is not { } progress
                || board.GetChild(0) is not ComposedBoardView view)
            {
                ctx.Check(false, $"the board in the tree owns the pump and its view");
                return;
            }

            var motion = LoadScreens.MotionFor(false, null);
            var art = view.ArtSize(new BoardArt(BoardArtLibrary.Rimage, motion.FillArt));
            var window = ctx.Host.GetViewport().GetVisibleRect().Size;
            var fit = BoardFit.For(window.X, window.Y);
            var seen = new List<int>();
            int rises = 0;
            int previous = 0;
            foreach (var step in Enum.GetValues<LoadStep>())
            {
                LoadProgress.Report(step);
                if (Lamps(ctx, fit, motion, art) is not { } lit)
                {
                    continue;
                }

                ctx.Check(
                    lit >= previous,
                    $"{step}: the frame on the glass shows {lit} lamp(s), never fewer than {previous}");
                rises += lit > previous ? 1 : 0;
                previous = lit;
                seen.Add(lit);
                report.AppendLine($"presented {step} {progress.Fraction} lamps={lit}");
            }

            if (seen.Count == 0)
            {
                ctx.Note($"the window returned no image, so nothing on the glass was counted");
                return;
            }

            // The point of the item, read off the glass. A build crossing every boundary inside one
            // throttle window still lights the strip a lamp at a time. A screen drawn once at the
            // end would rise from nothing to six in a single frame.
            ctx.Same(
                LoadProgress.Milestones.Count, seen.Count,
                $"the sixteen steps were each read back off the window");
            ctx.Same(LampCount, rises, $"the strip lit one lamp at a time: {string.Join(",", seen)}");
            ctx.Same(LampCount, previous, $"the last milestone lights the whole strip");
            report.AppendLine(
                $"presented lamps {string.Join(",", seen)} over {progress.Draws} draw(s)");
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
            LoadProgress.Current = held;
        }
    }

    // How many lamps of the fill strip are lit on the frame just presented, or null where the
    // window hands back no image. Counted as runs of red across the strip's own middle row, which
    // is what a watcher sees. The propeller sits outside the band on both families.
    private static int? Lamps(TestContext ctx, BoardFit fit, LoadMotion motion, Vector2 art)
    {
        if (art.X <= 0f || art.Y <= 0f
            || ctx.Host.GetViewport()?.GetTexture()?.GetImage() is not { } shot || shot.IsEmpty())
        {
            return null;
        }

        int row = Mathf.Clamp(
            (int)(fit.Y(motion.FillY) + (fit.Length(art.Y) / 2f)), 0, shot.GetHeight() - 1);
        int from = Mathf.Clamp((int)fit.X(motion.FillX), 0, shot.GetWidth() - 1);
        int to = Mathf.Clamp((int)(fit.X(motion.FillX) + fit.Length(art.X)), 0, shot.GetWidth());
        int runs = 0;
        bool inRun = false;
        for (int x = from; x < to; x++)
        {
            var pixel = shot.GetPixel(x, row);
            bool red = pixel.R > 0.35f && pixel.R > pixel.G * 2f && pixel.R > pixel.B * 2f;
            if (red && !inRun)
            {
                runs++;
            }

            inRun = red;
        }

        return runs;
    }

    // The whole point of the item, driven for real. A world build holds the frame loop. The screen
    // standing over it has to be repainted from inside that block or not at all. The world
    // is half-built at every forced draw, which is the case no composed-board check can reach.
    private static void CheckPumpDuringAWorldBuild(
        TestContext ctx, StringBuilder report, LoadSheet? sheet)
    {
        var held = LoadProgress.Current;
        LoadProgress.Current = null;
        var board = LoadBoard.Build(
            ctx.DataRoot, ctx.ZrdrPath, ctx.MessagesPath, campaign: true, string.Empty, null, sheet);
        ctx.Host.AddChild(board);
        try
        {
            if (LoadProgress.Current is not { } progress)
            {
                ctx.Check(false, $"the board in the tree owns the pump for the world build");
                return;
            }

            int draws = 0;
            var repaint = progress.Repaint;
            progress.Repaint = () =>
            {
                draws++;
                repaint?.Invoke();
            };
            // Private, so the build really runs rather than the cache handing a world back with no
            // phase crossed at all.
            ctx.WithPrivateWorld(ctx.Chapter, false, _ => { });
            ctx.Check(
                draws > 0, $"the build's own phases drew the screen {draws} time(s) from inside it");
            ctx.Check(
                progress.Fraction >= LoadProgress.Milestones[(int)LoadStep.MissionFiles],
                $"the world build carried the bar to {progress.Fraction}");
            report.AppendLine(
                $"world build: {draws} forced draw(s), bar at {progress.Fraction}");
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
            LoadProgress.Current = held;
        }
    }

    // The lit pixels the painted board draws, which is its overlay's cropped fill picture, or 0
    // where nothing is lit yet.
    private static int FillWidth(ComposedBoard board)
    {
        foreach (var panel in board.Overlays)
        {
            if (FillWidth(panel.Pictures) is > 0 and var lit)
            {
                return lit;
            }
        }

        return 0;
    }

    // The same, over the moving layer's own pictures.
    private static int FillWidth(IReadOnlyList<BoardPicture> pictures)
    {
        foreach (var picture in pictures)
        {
            if (picture.Crop is { } crop)
            {
                return (int)crop.Width;
            }
        }

        return 0;
    }

    // The propeller frame the painted board draws, or null where the screen carries no propeller.
    private static string? Propeller(ComposedBoard board, LoadMotion motion)
    {
        foreach (var panel in board.Overlays)
        {
            foreach (var picture in panel.Pictures)
            {
                if (picture.Crop == null
                    && picture.X == motion.PropellerX && picture.Y == motion.PropellerY)
                {
                    return picture.Art.Name;
                }
            }
        }

        return null;
    }
}
