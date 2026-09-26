using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>Suites over the campaign load screen: every campaign mission's chart sheet composed
/// from its own <c>Loading.zrd</c> dialog, with every bitmap it names checked against the
/// extraction. Decode: docs/org/loading-screen.md.</summary>
internal static class LoadSheetSuites
{
    // The filmed mission, whose sheet the CM01 reference still pins exactly.
    private const string FilmedChapter = "C3";
    private const string FilmedMission = "M01";

    // The picture the profile this suite seats has chosen: a mission 0 award, so every profile
    // holds it, and not the seeded pin-up, so a screen that read no profile would draw a different
    // name here rather than the right one by accident.
    private const string ChosenMemento = "MS_P_Mom.jpg";

    // The shadow the script centres under the memento.
    private const string Shadow = "momento_shad";

    /// <summary>The campaign load screens end to end: every dialog resolves, the parchment lists
    /// the mission's own objectives, every bitmap the composition draws is extracted, and a real
    /// board hangs the whole thing over a window.</summary>
    [Suite("load-sheet",
        "the campaign load screen against the 24 Loading.zrd campaign dialogs: each mission's "
        + "sheet resolves its map, memento, parchment and propeller, every authored flag pin "
        + "reaches the chart it belongs to past the waits 19 of the dialogs place theirs behind, "
        + "every bitmap the composition names exists in the extraction, the map is drawn at its "
        + "authored source crop rather than scaled, the parchment lists that mission's objectives "
        + "unmarked and slanted in the measure the pause sheet's rows take, with every row of every "
        + "sheet held inside the parchment's solid paper by a face that shrinks rather than by "
        + "losing a row, the memento the "
        + "seated profile chose sits over the shadow at their authored points while a session that "
        + "seats no profile hangs the seeded pin-up instead, every picture comes "
        + "from the dialog so no world icon is placed, an unreadable sheet "
        + "still leaves the frame and the bar standing, and C3/M01's own sheet matches the "
        + "reference still flag for flag")]
    internal static void LoadSheetScreen(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");

        // Every campaign mission, not the run's own chapter: no world is built here, so the whole
        // sequence is affordable, and the filmed mission has to be in it whatever chapter is up.
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (missions.Count == 0)
        {
            throw new SuiteSkippedException($"cm_sequence names no campaign mission");
        }

        // The screen hangs the seated profile's own picture, so every sheet below is composed over
        // one that chose a picture rather than over the name a fresh profile is seeded with.
        string memento = Session.Campaign.CampaignMementos.BitmapFor(SeatedProfile(ctx));
        var report = new StringBuilder();
        var sheets = new List<(CampaignMission Mission, LoadSheet Sheet)>();
        foreach (var mission in missions)
        {
            string key = EscapeDialog.CampaignKey(mission.Campaign, mission.Mission);
            var sheet = LoadSheet.Load(
                ctx.ZrdrPath, ctx.MessagesPath,
                SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder),
                key, memento);
            if (sheet == null)
            {
                ctx.Check(false, $"{mission.ChapterFolder}/{mission.MissionFolder} resolves {key}");
                continue;
            }

            sheets.Add((mission, sheet));
            report.AppendLine(
                $"{mission.ChapterFolder}/{mission.MissionFolder} {key} map={sheet.State.Map?.Bitmap ?? "-"} "
                + $"pins={CountPins(sheet)} objectives={sheet.Objectives.Count} steps={sheet.State.Steps.Count}");
        }

        ctx.Same(missions.Count, sheets.Count, $"every campaign mission resolves its own loading dialog");
        CheckEveryPinPlaced(ctx, sheets, report);
        CheckArtExists(ctx, sheets, report);
        CheckComposition(ctx, sheets, memento, report);
        CheckRowsOnPaper(ctx, sheets, report);
        CheckSeatedMemento(ctx, sheets, memento, report);
        CheckBareSheet(ctx);
        CheckFilmedSheet(ctx, sheets, report);
        DriveBoard(ctx, sheets);

        ctx.WriteArtifact($"test-load-sheet.txt", report.ToString());
        ctx.Note($"composed {sheets.Count} campaign load sheets and hung one over a window");
    }

    // Every pin the dialog's script authors has to be placed and visible. 19 of the 24 dialogs put
    // their pins past an authored Wait, which a reveal nobody advances never releases, so a sheet
    // built and left alone draws those charts with no flags on them at all.
    private static void CheckEveryPinPlaced(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, StringBuilder report)
    {
        int authored = 0, placed = 0, waits = 0, bare = 0;
        foreach (var (mission, sheet) in sheets)
        {
            int scripted = 0;
            foreach (var step in sheet.State.Steps)
            {
                scripted += step.Op == BriefingOp.Pict
                    && step.Id.StartsWith("OBJPIN", System.StringComparison.Ordinal) ? 1 : 0;
                waits += step.Op == BriefingOp.Wait ? 1 : 0;
            }

            int live = CountPins(sheet);
            authored += scripted;
            placed += live;
            bare += scripted > 0 && live == 0 ? 1 : 0;
            if (scripted != live)
            {
                report.AppendLine($"  {mission.MissionFolder}: {scripted} pins authored, {live} placed");
            }
        }

        report.AppendLine($"{authored} pins authored across the sequence, {placed} placed, {waits} waits");
        ctx.Same(authored, placed, $"every authored flag pin reaches the sheet it belongs to");
        ctx.Same(0, bare, $"no dialog that authors pins composes a chart with none on it");
        ctx.Check(waits > 0, $"the sequence does author waits, so the settle is doing work ({waits})");
    }

    // Every bitmap the composition names, against the extraction's own rimage folder. A name the
    // extraction does not carry draws nothing and logs nothing, so it is invisible without this.
    private static void CheckArtExists(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, StringBuilder report)
    {
        string rimage = Path.Combine(ctx.DataRoot, "extracted", "rimage");
        var missing = new List<string>();
        int named = 0;
        foreach (var (mission, sheet) in sheets)
        {
            foreach (string name in ArtNames(Compose(sheet)))
            {
                named++;
                if (!File.Exists(Path.Combine(rimage, name.ToLowerInvariant() + ".png")))
                {
                    missing.Add($"{mission.MissionFolder}:{name}");
                }
            }
        }

        report.AppendLine($"{named} bitmap references, {missing.Count} missing");
        foreach (string gap in missing)
        {
            report.AppendLine($"  missing {gap}");
        }

        ctx.Same(0, missing.Count, $"every bitmap the campaign load sheets name is extracted ({named} named)");
    }

    // What every sheet composes: the chart at its source crop, the parchment listing that mission's
    // own objectives with no mark on any row, the memento over its shadow, the propeller's first
    // frame and the unlit bar, and neither world icon.
    private static void CheckComposition(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, string memento,
        StringBuilder report)
    {
        int charts = 0, parchments = 0, mementos = 0, propellers = 0, bars = 0, marks = 0, icons = 0;
        foreach (var (mission, sheet) in sheets)
        {
            var board = Compose(sheet);
            if (sheet.State.Map is { } map && FindArt(board, map.Bitmap) is { } chart)
            {
                charts += chart.Crop == new BoardCrop(map.Clip.X0, map.Clip.Y0, map.Clip.Width, map.Clip.Height)
                    && chart.X == map.Position.X && chart.Y == map.Position.Y
                    && chart.Width == 0f && chart.Height == 0f ? 1 : 0;
            }

            var note = board.Notes.Count == 1 ? board.Notes[0] : null;
            var list = sheet.Shared.Objectives;
            if (note != null && list != null)
            {
                parchments += note.Entries.Count == ObjectiveBeats(sheet)
                    && note.X == list.ListAt.X && note.Y == list.ListAt.Y
                    && note.Width == list.RowWrap && note.Height == list.RowBox
                    && note.Italic && note.Shrink && Lists(note.Entries, sheet.Objectives) ? 1 : 0;
                marks += note.Marked == null && note.Mark == null ? 0 : 1;
            }

            mementos += FindArt(board, memento) is { } photo
                && photo.X == sheet.State.MementoAt.X && photo.Y == sheet.State.MementoAt.Y
                && !photo.Centered
                && FindArt(board, Shadow) is { Centered: true } ? 1 : 0;
            propellers += Propeller(sheet) is { } cycle && FindArt(board, cycle.Bitmap) is { } blade
                && blade.X == cycle.At.X && blade.Y == cycle.At.Y ? 1 : 0;
            bars += FindArt(board, "prog_blkload") is { X: 90f, Y: 548f } ? 1 : 0;
            icons += Accounted(board, sheet) ? 0 : 1;
            if (sheet.State.Map is null)
            {
                report.AppendLine($"  {mission.MissionFolder}: no map primitive");
            }
        }

        int all = sheets.Count;
        ctx.Same(all, charts, $"every sheet draws its chart at its authored source crop and position");
        ctx.Same(
            all, parchments,
            $"every parchment lists that mission's own objectives, all of them, in the paper's measure");
        ctx.Same(0, marks, $"no row is marked, the screen standing before the mission it lists has run");
        ctx.Same(all, mementos, $"every memento sits at its authored point over its centred shadow");
        ctx.Same(all, propellers, $"every sheet draws the propeller cycle's first frame at its authored point");
        ctx.Same(all, bars, $"every sheet draws the unlit bar at the PROGRESS entry's own position");
        ctx.Same(0, icons, $"every picture on a sheet comes from its dialog, so no world icon is placed");
    }

    // Every row of every sheet stays on the parchment's solid paper, the rectangle measured off the
    // bitmap's own torn-edge band. The renderer's font is the only thing that knows how a wrapped
    // entry drew, so the sheet is fitted the way the view fits it and then measured.
    private static void CheckRowsOnPaper(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, StringBuilder report)
    {
        if (sheets.Count == 0)
        {
            return;
        }

        var probe = new Godot.Control();
        ctx.Host.AddChild(probe);
        try
        {
            if (probe.GetThemeDefaultFont() is not { } font)
            {
                throw new SuiteSkippedException("the default theme carries no font to measure with");
            }

            SweepRowsOnPaper(ctx, sheets, font, report);
        }
        finally
        {
            ctx.Host.RemoveChild(probe);
            probe.QueueFree();
        }
    }

    private static void SweepRowsOnPaper(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, Godot.Font font,
        StringBuilder report)
    {
        var fit = BoardFit.For(BoardFit.AuthoredWidth, BoardFit.AuthoredHeight);
        var paper = PauseSheetSuites.Paper(ctx, sheets[0].Sheet.Shared.Objectives);
        int shrunk = 0, dropped = 0;
        float over = float.NegativeInfinity, past = float.NegativeInfinity;
        string widest = "-", deepest = "-";
        foreach (var (mission, sheet) in sheets)
        {
            var board = Compose(sheet);
            if (board.Notes.Count == 0)
            {
                continue;
            }

            var note = ComposedBoardView.Fitted(fit, font, board.Notes[0]);
            shrunk += note.Size < board.Notes[0].Size ? 1 : 0;
            var placed = note.Flow(ComposedBoardView.Measure(fit, font, note));
            dropped += note.Entries.Count - placed.Count;
            var corner = PauseSheetSuites.Corner(
                ComposedBoardView.MeasureBox(fit, font, note.Size), placed);
            string named = $"{mission.ChapterFolder}/{mission.MissionFolder}";
            if (corner.X - paper.End.X > over)
            {
                over = corner.X - paper.End.X;
                widest = named;
            }

            if (corner.Y - paper.End.Y > past)
            {
                past = corner.Y - paper.End.Y;
                deepest = named;
            }
        }

        ctx.Same(0, dropped, $"every objective the load screen lists is drawn ({dropped} lost)");
        ctx.Check(
            over <= 0f && past <= 0f,
            $"and no row leaves the paper ({widest} widest, {-over:0.0} px in; {deepest} deepest, {-past:0.0} px up)");
        report.AppendLine(
            $"paper: ends at {paper.End.X:0.0}/{paper.End.Y:0.0}, {shrunk} sheets shrunk, "
            + $"widest {widest} at {paper.End.X + over:0.0}, deepest {deepest} at {paper.End.Y + past:0.0}");
    }

    // The picture is the seated profile's own, read back off a store the way the launcher reads
    // one, and a session with nobody seated still draws the seeded pin-up. A screen that asked no
    // profile would draw the seeded name in both cases and pass every other check here.
    private static void CheckSeatedMemento(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, string memento,
        StringBuilder report)
    {
        string seeded = Session.Campaign.CampaignMementos.BitmapFor(null);
        ctx.Check(
            memento == Session.Campaign.CampaignMementos.Bitmap(ChosenMemento) && memento != seeded,
            $"the seated profile hangs {memento} where a session with no profile hangs {seeded}");
        if (sheets.Count == 0)
        {
            return;
        }

        var mission = sheets[0].Mission;
        var bare = LoadSheet.Load(
            ctx.ZrdrPath, ctx.MessagesPath,
            SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder),
            EscapeDialog.CampaignKey(mission.Campaign, mission.Mission), seeded);
        var board = bare == null ? null : Compose(bare);
        ctx.Check(
            board != null && FindArt(board, seeded) != null && FindArt(board, memento) == null,
            $"and a sheet with no profile behind it hangs the seeded pin-up alone");
        ctx.Check(
            FindArt(Compose(sheets[0].Sheet), memento) != null,
            $"while the seated sheet hangs the chosen picture ({memento})");
        report.AppendLine($"memento: seated {memento}, no profile {seeded}");
    }

    // A profile that has chosen a picture, written and read back through a store of its own under
    // the scratch root: the file round trip the launcher's own read makes, over nobody's real
    // profile.
    private static Session.Campaign.CampaignProfileDef SeatedProfile(TestContext ctx)
    {
        var store = new Session.Campaign.CampaignProfileStore(
            Path.Combine(ctx.ScratchDir, "load-sheet-memento"));
        var def = Session.Campaign.CampaignProfileDef.NewProfile("Load Sheet");
        def.Memento = ChosenMemento;
        store.Save(def);
        return store.Load(def.Name) ?? def;
    }

    // A sheet the extraction could not answer for still leaves a screen: the frame and the bar,
    // which is the whole of what this screen can promise while everything else is still loading.
    private static void CheckBareSheet(TestContext ctx)
    {
        var bare = LoadScreens.For(true, "unused", null, "no-zrdr", "no-messages");
        ctx.Same(1, bare.Backdrop.Count, $"an unreadable sheet keeps the frame behind the screen");
        ctx.Check(
            bare.Pictures.Count == 1 && bare.Pictures[0].Art.Name == "prog_blkload"
            && bare.Lines.Count == 0 && bare.Notes.Count == 0,
            $"an unreadable sheet draws the bar and no words of ours");
    }

    // C3/M01, the filmed mission: three question-mark flags and one numbered four, the Pandora
    // icon the loading script places and the pause screen's own does not, and four objectives.
    private static void CheckFilmedSheet(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets, StringBuilder report)
    {
        int at = sheets.FindIndex(s =>
            s.Mission.ChapterFolder.Equals(FilmedChapter, System.StringComparison.OrdinalIgnoreCase)
            && s.Mission.MissionFolder.Equals(FilmedMission, System.StringComparison.OrdinalIgnoreCase));
        ctx.Check(at >= 0, $"{FilmedChapter}/{FilmedMission}, the filmed mission, is in the sequence");
        if (at < 0)
        {
            return;
        }

        var sheet = sheets[at].Sheet;
        var pins = new List<string>();
        foreach (var element in sheet.Reveal.Elements)
        {
            if (element.Id.StartsWith("OBJPIN", System.StringComparison.Ordinal))
            {
                pins.Add(element.Bitmap);
            }
        }

        ctx.Check(
            pins.Count == 4 && pins[0] == "pin6" && pins[1] == "pin6" && pins[2] == "pin6"
            && pins[3] == "pin4",
            $"the filmed mission's flags are three question marks and one numbered four ({string.Join(",", pins)})");
        var board = Compose(sheet);
        ctx.Check(
            FindArt(board, "NW-m1pda_icon") is { Centered: true },
            $"the filmed sheet places the Pandora icon its loading script authors");
        ctx.Same(4, board.Notes.Count == 1 ? board.Notes[0].Entries.Count : 0,
            $"the filmed parchment lists the mission's four objectives");
        report.AppendLine(
            $"filmed sheet: pins {string.Join(",", pins)}, {board.Pictures.Count} pictures, "
            + $"{board.Backdrop.Count} backdrop");
    }

    // The real node over a real window: the composition reaches the authored-pixel surface and the
    // board tracks the viewport, which is the whole of what LoadBoard owns.
    private static void DriveBoard(
        TestContext ctx, List<(CampaignMission Mission, LoadSheet Sheet)> sheets)
    {
        if (sheets.Count == 0)
        {
            return;
        }

        var mission = sheets[0].Mission;
        var board = LoadBoard.Build(
            ctx.DataRoot, ctx.ZrdrPath, ctx.MessagesPath, campaign: true, string.Empty, null,
            sheets[0].Sheet);
        ctx.Host.AddChild(board);
        try
        {
            ctx.Same(1, board.GetChildCount(), $"the board on a window builds its own view");
            board._Process(0.0);
            ctx.Check(
                board.Size == board.GetViewportRect().Size,
                $"{mission.MissionFolder}'s board covers the window ({board.Size})");
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
        }
    }

    // Every picture the board draws has to come from the dialog: its map, the shared parchment,
    // the memento and the bar, plus what its own script placed. An ownship or zeppelin icon would
    // be one more, and neither the load dialog's constructor nor its script names either. The
    // zeppelin icon's own bitmap is not the test: 9 of the loading scripts place that art
    // themselves, at a point they author rather than through the map's window.
    private static bool Accounted(ComposedBoard board, LoadSheet sheet)
    {
        int scripted = 0;
        foreach (var element in sheet.Reveal.Elements)
        {
            scripted += element.Visible && element.Opacity > 0f && element.Bitmap.Length > 0 ? 1 : 0;
        }

        int expected = scripted + 2
            + (sheet.State.Map is { Bitmap.Length: > 0 } ? 1 : 0)
            + (sheet.Shared.Objectives is { Background.Length: > 0 } ? 1 : 0);
        return board.Pictures.Count == expected
            && sheet.Reveal.Element("OWNSHIP") == null && sheet.Reveal.Element("MYZEP") == null;
    }

    private static ComposedBoard Compose(LoadSheet sheet) =>
        LoadScreens.For(true, string.Empty, null, "unused", "unused", sheet);

    // How many objectives the dialog's script binds, which is how many rows the parchment lists.
    private static int ObjectiveBeats(LoadSheet sheet)
    {
        int bound = 0;
        foreach (int index in sheet.Reveal.RevealedObjectives)
        {
            bound += index >= 0 && index < sheet.Objectives.Count ? 1 : 0;
        }

        return bound;
    }

    private static BriefingStep? Propeller(LoadSheet sheet)
    {
        foreach (var step in sheet.State.Steps)
        {
            if (step.Op == BriefingOp.Cycle && step.Bitmap.Length > 0)
            {
                return step;
            }
        }

        return null;
    }

    // Whether the parchment's rows are the mission's own objective texts, in order.
    private static bool Lists(IReadOnlyList<string> rows, IReadOnlyList<string> objectives)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= objectives.Count || rows[i] != objectives[i])
            {
                return false;
            }
        }

        return rows.Count > 0;
    }

    private static int CountPins(LoadSheet sheet)
    {
        int pins = 0;
        foreach (var element in sheet.Reveal.Elements)
        {
            pins += element.Id.StartsWith("OBJPIN", System.StringComparison.Ordinal) ? 1 : 0;
        }

        return pins;
    }

    private static BoardPicture? FindArt(ComposedBoard board, string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        foreach (var picture in board.Pictures)
        {
            if (picture.Art.Name == name)
            {
                return picture;
            }
        }

        return null;
    }

    private static IEnumerable<string> ArtNames(ComposedBoard board)
    {
        foreach (var picture in board.Backdrop)
        {
            yield return picture.Art.Name;
        }

        foreach (var picture in board.Pictures)
        {
            yield return picture.Art.Name;
        }
    }
}
