using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>Suites over the Original presentation's pause screen: the real board over a real
/// <see cref="Flight.Modes.PauseState"/>, composed from the chapter's own <c>escape.zrd</c> dialogs, with
/// every bitmap it names checked against the extraction. A second suite does the same for the
/// Instant Action blackboard the sortie's <c>ia_escape.zrd</c> dialog carries.
/// Decode: docs/org/pause-screen.md.</summary>
internal static class PauseSheetSuites
{
    // The filmed mission, whose composition the four pause-screen stills pin exactly.
    private const string FilmedChapter = "C3";
    private const string FilmedMission = "M01";

    // The mission whose note rows are numbered against their priorities: its priority 1 row is
    // OBJECTIVE15, while OBJECTIVE1 is a conditionless two-second wake objective. A parchment that
    // asked the runtime by priority would check that row two seconds into the mission.
    private const string NumberedChapter = "C3";
    private const string NumberedMission = "M04";

    // The picture the profile this suite seats has chosen: a mission 0 award, so every profile
    // holds it. It is not the seeded pin-up, so a sheet that read no profile would draw a
    // different name here by accident.
    private const string ChosenMemento = "MS_P_Mom.jpg";
    // The mission whose objective block authors two IDENTITY entries, one keyed and one not. It is
    // the only block in the shipped data that does, and the note keeps the keyed entry alone.
    private const string DoubledChapter = "C4";
    private const string DoubledMission = "M05";
    private const int DoubledBlock = 23;

    // The sortie the Instant Action pause still films, as its chapter code and mission type.
    private const string FilmedEnvironment = "C1";
    private const string FilmedType = "stunt_flying";

    // The ace dialog whose second head stands 40 px left of every other environment's, which is the
    // one place the environment digit changes what an Instant Action pause draws.
    private const string OddAceKey = "loading_i6a";
    private const float OddAceHeadX = 325f;
    private const float AceHeadX = 365f;

    // How many objectives across the whole sequence are composed away rather than drawn. The
    // original's list walks its whole row vector and stops at no height, so every row reaches
    // both screens. A list too deep for the paper shrinks its face instead of losing a row.
    private const int OverrunRows = 0;

    // Where the parchment's solid paper ends in authored pixels, off the art's own position. No
    // row may cross either edge: the bitmap's torn band is not paper, and words drawn over it read
    // as ink spilled off the sheet. The sweep measures the band again rather than trusting these.
    private const float PaperRight = 770f;
    private const float PaperBottom = 290f;

    // How many points over its own face the deepest list is put at to exercise the fit. Every
    // campaign list fits the paper at the size the screen writes it in, so nothing on the disc
    // drives the shrink. A rule no data reaches is a rule nothing checks.
    private const float Swollen = 6f;

    // The alpha a pixel counts as paper at. The torn edge fades out over a few pixels rather than
    // ending. The solid rectangle is read at very nearly opaque rather than at any coverage.
    private const float SolidAlpha = 250f / 255f;

    // How many lines each of the filmed mission's four objectives takes on the parchment, read off
    // the reference crop of the original's own sheet. The face is ours, so the count is what a row
    // can be held to rather than the glyphs.
    private static readonly int[] FilmedRowLines = { 1, 2, 2, 1 };

    // And the numbered mission's two, both shorter than the filmed sheet's longest single line.
    // The original fits each on one line in the same box. No still films this one.
    private static readonly int[] NumberedRowLines = { 1, 1 };

    // The world-folder digits Instant Action's own environment list offers. C1C (3) is the chapter
    // it omits, so ia_escape.zrd carries no loading_i3 dialog at all
    // (docs/formats/instant-action.md).
    private static readonly int[] Environments = { 1, 2, 4, 5, 6, 7, 8 };

    // The profile a campaign pause is taken over here: a seated one that chose a picture. The
    // flown session's own director hands that picture to the readout.
    private static readonly Session.Campaign.CampaignProfileDef Seated = SeatProfile();

    // The four mission types, in the order the exe's jump table letters them.
    private static readonly string[] MissionTypes =
    {
        "dogfight_ace", "dogfight_squadron", "stunt_flying", "zeppelin_run",
    };

    // The three photographs every Instant Action dialog's script places, each centred on its own
    // authored point. They are the load screen's own stills for the family, the same three whatever
    // the environment and the mission type, and no mission still reaches this screen.
    private static readonly (string Bitmap, float X, float Y)[] Photographs =
    {
        ("MP-shotdown", 197f, 157f),
        ("MP-crash", 197f, 307f),
        ("mp-dangerzone2", 197f, 457f),
    };

    // Where the Instant Action sheet's strips stand, in the order a cursor walks them. The block
    // authors three across and one below the third. The remake's photo strip takes the free cell
    // under RESTART, since this block has no channel between columns.
    private static readonly (float X, float Y)[] Strips =
    {
        (352f, 510f), (497f, 550f), (497f, 510f), (642f, 510f), (642f, 550f),
    };

    // And where the campaign sheet's stand: two columns of two. The photo strip goes in their own
    // channel, four pixels narrower than the plate it takes.
    private static readonly (float X, float Y)[] CampaignStrips =
    {
        (107f, 528f), (237f, 528f), (127f, 559f), (367f, 528f), (347f, 559f),
    };

    /// <summary>The chapter's campaign pause sheets end to end: every dialog resolves, every
    /// bitmap it draws is extracted, and the board follows the pause state it was given.</summary>
    [Suite("pause-sheet",
        "the Original presentation's pause screen against the chapter's own escape.zrd dialogs: "
        + "each campaign mission's sheet resolves its map, parchment and authored strips, the "
        + "memento slot takes the picture the seated profile chose, which the flown mission's own "
        + "director reports, at the slot's authored point, while a pause with no profile behind it "
        + "takes the seeded pin-up, "
        + "every bitmap the composition names exists in the extraction (a lowercasing or naming "
        + "slip draws nothing and is otherwise silent), a real OriginalPauseBoard follows "
        + "PauseState.Changed with its cursor resting on RESUME, the five strips stand at the "
        + "points the block's own geometry gives them with PHOTO MODE in the channel between the "
        + "two columns, that strip opens photo mode and leaves the halt and the sheet standing, "
        + "the map is drawn at its authored "
        + "source crop rather than scaled, an OWNSHIP icon placed by world position lands inside "
        + "the map's own screen rectangle and one off the window draws nothing, the parchment's "
        + "marks follow the completed rows across the four filmed poses, a mark follows the row's "
        + "own OBJECTIVEn number rather than its briefing priority (C3/M04's priority 1 row is "
        + "OBJECTIVE15, and its two-second wake objective must not check it), the one block that "
        + "authors two IDENTITY entries gives C4/M05's note one row and one mark, C3/M01's own "
        + "sheet matches the reference stills flag for flag, every parchment sets its rows in the "
        + "slanted face the original's ObjList authors and breaks C3/M01's four where the reference "
        + "crop breaks them, every objective is drawn rather than composed away at a height the "
        + "original's own list stops at nowhere, with the whole sequence's rows held inside the "
        + "parchment's solid paper (measured off the bitmap's own torn-edge band) by a face that "
        + "shrinks where a list would leave it, and a pointer over a strip moves the "
        + "shared cursor onto it and fires it on the release while a press let go elsewhere fires "
        + "nothing")]
    internal static void PauseSheetScreen(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");

        // Every campaign mission, not the run's own chapter: no world is built here, so the whole
        // sequence is affordable, and the filmed mission has to be in it whatever chapter is up.
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (missions.Count == 0)
        {
            throw new SuiteSkippedException($"cm_sequence names no campaign mission");
        }

        var report = new StringBuilder();
        var sheets = new List<(CampaignMission Mission, PauseSheet Sheet)>();
        foreach (var mission in missions)
        {
            string key = EscapeDialog.CampaignKey(mission.Campaign, mission.Mission);
            var sheet = PauseSheet.Load(ctx.ZrdrPath, ctx.MessagesPath, key, instantAction: false);
            if (sheet == null)
            {
                ctx.Check(false, $"{mission.ChapterFolder}/{mission.MissionFolder} resolves {key}");
                continue;
            }

            sheets.Add((mission, sheet));
            report.AppendLine(
                $"{mission.ChapterFolder}/{mission.MissionFolder} {key} map={sheet.State.Map?.Bitmap ?? "-"} "
                + $"pins={CountPins(sheet)} steps={sheet.State.Steps.Count}");
        }

        ctx.Same(missions.Count, sheets.Count, $"every campaign mission resolves its own pause dialog");
        CheckEveryPinPlaced(ctx, sheets, report);
        CheckArtExists(ctx, sheets, report);
        int filmed = Filmed(sheets);
        ctx.Check(filmed >= 0, $"{FilmedChapter}/{FilmedMission}, the filmed mission, is in the sequence");
        DriveBoard(ctx, sheets[filmed < 0 ? 0 : filmed], report);
        CheckFilmedPoses(ctx, sheets, filmed, report);
        CheckSeatedMemento(ctx, sheets, filmed, report);
        CheckNumberedMarks(ctx, sheets, report);
        CheckRowWrap(ctx, sheets, report);
        CheckDoubledIdentity(ctx, sheets, report);

        ctx.WriteArtifact($"test-pause-sheet.txt", report.ToString());
        ctx.Note($"composed {sheets.Count} pause sheets and drove one over a live pause state");
    }

    /// <summary>The Instant Action pause sheets end to end: every environment and mission type
    /// resolves its own blackboard dialog, and the composition is the load screen's board under the
    /// pause screen's four strips.</summary>
    [Suite("pause-sheet-ia",
        "the Original presentation's Instant Action pause screen against ia_escape.zrd's 28 "
        + "loading_i dialogs: each of the seven environments Instant Action offers resolves its own "
        + "dialog for all four mission types rather than falling back on the file's default, the "
        + "composition is the load screen's blackboard with its three centred photographs and the "
        + "dialog's own four texts, no chart, parchment, memento or world icon reaches it, every "
        + "bitmap it names exists in the extraction, C1's stunt sheet matches the reference still "
        + "word for word at its authored points, the remake's photo strip takes the free cell under "
        + "RESTART on a block that stands three across, the environment digit is read rather than assumed "
        + "(loading_i6a puts its second head 40 px left of every other ace dialog's) and "
        + "CampaignSequence.ChapterNumber inverts every campaign chapter's own folder, and a real "
        + "OriginalPauseBoard follows PauseState.Changed with a pointer that walks all five strips "
        + "and fires the one it was pressed and released on")]
    internal static void InstantActionPauseSheet(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");

        var report = new StringBuilder();
        var sheets = new List<(string Key, PauseSheet Sheet)>();
        foreach (int environment in Environments)
        {
            foreach (string type in MissionTypes)
            {
                if (LoadScreens.LetterFor(type) is not { } letter)
                {
                    ctx.Check(false, $"'{type}' letters an Instant Action dialog");
                    continue;
                }

                string key = EscapeDialog.InstantActionKey(environment, letter);
                var sheet = PauseSheet.Load(ctx.ZrdrPath, ctx.MessagesPath, key, instantAction: true);
                if (sheet == null)
                {
                    ctx.Check(false, $"{key} resolves an ia_escape.zrd dialog");
                    continue;
                }

                sheets.Add((key, sheet));
                report.AppendLine(
                    $"{key} -> {sheet.State.Key} background={sheet.State.Background} "
                    + $"texts={sheet.Texts.Count} steps={sheet.State.Steps.Count}");
            }
        }

        ctx.Same(
            Environments.Length * MissionTypes.Length, sheets.Count,
            $"every environment the game offers resolves a sheet for all four mission types");
        CheckOwnDialog(ctx, sheets);
        CheckBlackboard(ctx, sheets, report);
        CheckBlackboardArt(ctx, sheets, report);
        CheckFilmedBlackboard(ctx, report);
        CheckEnvironmentIsRead(ctx, report);
        DriveBlackboardBoard(ctx, report);

        ctx.WriteArtifact($"test-pause-sheet-ia.txt", report.ToString());
        ctx.Note($"composed {sheets.Count} Instant Action pause sheets and drove one over a live pause state");
    }

    // The parchment's solid paper in authored pixels, measured off the extraction rather than
    // assumed. The largest fully opaque rectangle in the art is the paper inside the torn edges,
    // at the point the sheet hangs the art. An empty rectangle where the bitmap is missing.
    internal static Godot.Rect2 Paper(TestContext ctx, EscapeObjectivesList? list)
    {
        if (list == null)
        {
            return default;
        }

        string path = Path.Combine(
            ctx.DataRoot, "extracted", "rimage", list.Background.ToLowerInvariant() + ".png");
        if (!File.Exists(path) || Godot.Image.LoadFromFile(path) is not { } art || art.IsEmpty())
        {
            return default;
        }

        var solid = Solid(art);
        return new Godot.Rect2(
            list.BackgroundAt.X + solid.Position.X, list.BackgroundAt.Y + solid.Position.Y,
            solid.Size.X, solid.Size.Y);
    }

    // The far corner of a flowed block in authored pixels: the widest line's own right edge and the
    // last line's bottom. A word too long to break pushes a line's box past the measure it wrapped
    // to. The drawn width is asked for rather than taken from the note.
    internal static Godot.Vector2 Corner(
        System.Func<string, float, Godot.Vector2> box, IReadOnlyList<BoardLine> placed)
    {
        var corner = Godot.Vector2.Zero;
        foreach (var line in placed)
        {
            var drawn = box(line.Text, line.Width);
            corner = new Godot.Vector2(
                Godot.Mathf.Max(corner.X, line.X + drawn.X), Godot.Mathf.Max(corner.Y, line.Y + drawn.Y));
        }

        return corner;
    }

    // The lookup falls back on the file's own default dialog, which resolves for any key at all, so
    // a sheet that came back is not yet evidence its environment and letter were understood.
    private static void CheckOwnDialog(TestContext ctx, List<(string Key, PauseSheet Sheet)> sheets)
    {
        int own = 0;
        foreach (var (key, sheet) in sheets)
        {
            own += sheet.State.Key.Equals(key, System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        ctx.Same(sheets.Count, own, $"every sheet is its own dialog rather than the file's default");
    }

    // What every Instant Action sheet composes: the blackboard behind it, the three photographs its
    // script places, the four texts it writes, the four shared strips, and nothing else. The chart,
    // the parchment and the memento are the campaign dialog's, and none of them may appear here.
    private static void CheckBlackboard(
        TestContext ctx, List<(string Key, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        int frames = 0, photographs = 0, texts = 0, strips = 0, bare = 0, cursors = 0;
        foreach (var (key, sheet) in sheets)
        {
            var board = PauseScreens.For(sheet, PauseReadout.Empty, 0, false);
            frames += board.Backdrop.Count == 1
                && board.Backdrop[0].Art.Name == "loadframempt2"
                && board.Backdrop[0].X == 0f && board.Backdrop[0].Y == 0f ? 1 : 0;
            photographs += Placed(board, report, key) ? 1 : 0;
            texts += board.Lines.Count == 4 && sheet.Texts.Count == 4 ? 1 : 0;
            strips += Striped(board) ? 1 : 0;
            bare += board.Notes.Count == 0 && board.Strokes.Count == 0
                && sheet.State.Map == null && sheet.State.MementoBitmap.Length == 0 ? 1 : 0;
            cursors += sheet.State.Cursor is { Bitmap: "daglove", Rollover: "dafinger" } ? 1 : 0;
        }

        int all = sheets.Count;
        ctx.Same(all, frames, $"every sheet stands on the blackboard the dialog names");
        ctx.Same(all, photographs, $"every sheet centres the load screen's three photographs on their own points");
        ctx.Same(all, texts, $"every sheet writes the four texts its dialog authors and no more");
        ctx.Same(all, strips, $"every sheet carries the five strips at the points its block gives them");
        ctx.Same(all, bare, $"no sheet draws a chart, a parchment, a connector or a memento");
        ctx.Same(all, cursors, $"every sheet authors the glove pointer and its pointing-finger rollover");
    }

    // The three photographs, in script order, each centred and nothing else in the picture list: a
    // world icon or a memento would be one more entry.
    private static bool Placed(ComposedBoard board, StringBuilder report, string key)
    {
        if (board.Pictures.Count != Photographs.Length)
        {
            report.AppendLine($"  {key}: {board.Pictures.Count} pictures, expected {Photographs.Length}");
            return false;
        }

        for (int i = 0; i < Photographs.Length; i++)
        {
            var picture = board.Pictures[i];
            if (picture.Art.Name != Photographs[i].Bitmap || picture.X != Photographs[i].X
                || picture.Y != Photographs[i].Y || !picture.Centered)
            {
                report.AppendLine($"  {key}: picture {i} is {picture.Art.Name} at ({picture.X}, {picture.Y})");
                return false;
            }
        }

        return true;
    }

    private static bool Striped(ComposedBoard board)
    {
        if (board.Plaques.Count != Strips.Length)
        {
            return false;
        }

        for (int row = 0; row < Strips.Length; row++)
        {
            if (board.Plaques[row].X != Strips[row].X || board.Plaques[row].Y != Strips[row].Y)
            {
                return false;
            }
        }

        return true;
    }

    // Every bitmap the blackboard names, against the extraction's own rimage folder. A name the
    // extraction does not carry draws nothing and logs nothing, so it is invisible without this.
    private static void CheckBlackboardArt(
        TestContext ctx, List<(string Key, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        string rimage = Path.Combine(ctx.DataRoot, "extracted", "rimage");
        var missing = new List<string>();
        int named = 0;
        foreach (var (key, sheet) in sheets)
        {
            var board = PauseScreens.For(sheet, PauseReadout.Empty, 0, false);
            foreach (string name in ArtNames(board))
            {
                named++;
                if (!File.Exists(Path.Combine(rimage, name.ToLowerInvariant() + ".png")))
                {
                    missing.Add($"{key}:{name}");
                }
            }

            foreach (string name in Cursors(sheet))
            {
                named++;
                if (!File.Exists(Path.Combine(rimage, name.ToLowerInvariant() + ".png")))
                {
                    missing.Add($"{key}:{name}");
                }
            }
        }

        report.AppendLine($"{named} bitmap references, {missing.Count} missing");
        foreach (string gap in missing)
        {
            report.AppendLine($"  missing {gap}");
        }

        ctx.Same(0, missing.Count, $"every bitmap the Instant Action pause sheets name is extracted ({named} named)");
    }

    // C1's stunt blackboard, which the reference still films: the two heads, the blurb and the win
    // line at their authored points and faces, and the four strips' own labels.
    private static void CheckFilmedBlackboard(TestContext ctx, StringBuilder report)
    {
        if (Blackboard(ctx, FilmedEnvironment, FilmedType) is not { } sheet)
        {
            ctx.Check(false, $"{FilmedEnvironment}'s {FilmedType} sheet resolves");
            return;
        }

        var board = PauseScreens.For(sheet, PauseReadout.Empty, 0, false);
        ctx.Same(4, board.Lines.Count, $"the filmed sheet writes four texts");
        if (board.Lines.Count != 4)
        {
            return;
        }

        var head1 = board.Lines[0];
        var head2 = board.Lines[1];
        var blurb = board.Lines[2];
        var win = board.Lines[3];
        ctx.Check(
            head1.Text == "INSTANT ACTION" && head1.X == 70f && head1.Y == 35f && head1.Size == 17f
            && head1.Ink == BoardInk.Heading && !head1.Bold,
            $"the first head is INSTANT ACTION at the title face's own point ({head1.X}, {head1.Y})");
        ctx.Check(
            head2.Text == "STUNT FLYING" && head2.X == 325f && head2.Y == 35f && head2.Bold
            && head2.Width == 400f,
            $"the second head is STUNT FLYING in the dialog's default face ({head2.X}, {head2.Y})");
        ctx.Check(
            blurb.Text.StartsWith("Any knucklehead", System.StringComparison.Ordinal)
            && blurb.Text.EndsWith("real tight spots.", System.StringComparison.Ordinal)
            && blurb.X == 360f && blurb.Y == 135f && blurb.Width == 400f && blurb.Size == 13f
            && blurb.Ink == BoardInk.Row,
            $"the blurb is the stunt run's own, wrapped in the box it authors ({blurb.Width} wide)");
        ctx.Check(
            win.Text == "Fly through all the Danger Zones to win!" && win.X == 360f && win.Y == 215f
            && win.Size == 13f,
            $"the win line stands under the blurb at its own point ({win.X}, {win.Y})");
        ctx.Check(
            sheet.ButtonLabels.Count == 5 && sheet.ButtonLabels[PauseScreens.ResumeRow] == "Resume"
            && sheet.ButtonLabels[PauseScreens.PhotoRow] == PauseScreens.PhotoLabel
            && sheet.ButtonLabels[PauseScreens.RestartRow] == "Restart"
            && sheet.ButtonLabels[PauseScreens.PreferencesRow] == "Preferences"
            && sheet.ButtonLabels[PauseScreens.QuitRow] == "Quit",
            $"the five strips read {string.Join("/", sheet.ButtonLabels)}");
        ctx.Check(
            board.Notes.Count == 0 && board.Lines.Count == sheet.Texts.Count,
            $"and the sheet writes no objectives title over a parchment it does not draw");
        report.AppendLine(
            $"filmed: '{head1.Text}' at ({head1.X}, {head1.Y}), '{head2.Text}' at ({head2.X}, "
            + $"{head2.Y}), blurb at ({blurb.X}, {blurb.Y}) wrap {blurb.Width}, '{win.Text}' at "
            + $"({win.X}, {win.Y}), strips {string.Join("/", sheet.ButtonLabels)}");
    }

    // The environment digit is the only part of the key a sortie's chapter decides, and it changes
    // exactly one thing on the shipped data: C3's ace dialog puts its second head 40 px left of
    // every other environment's. A sheet keyed on a fixed environment writes that head in the wrong
    // place for a C3 ace sortie, which is what this pins.
    private static void CheckEnvironmentIsRead(TestContext ctx, StringBuilder report)
    {
        var odd = Blackboard(ctx, "C3", "dogfight_ace");
        var rest = Blackboard(ctx, "C1", "dogfight_ace");
        if (odd == null || rest == null)
        {
            ctx.Check(false, $"both ace sheets resolve");
            return;
        }

        ctx.Check(
            odd.State.Key.Equals(OddAceKey, System.StringComparison.OrdinalIgnoreCase),
            $"C3's ace sortie resolves the environment 6 dialog ({odd.State.Key})");
        ctx.Check(
            odd.Texts.Count == 4 && rest.Texts.Count == 4
            && odd.Texts[1].Text == rest.Texts[1].Text,
            $"the two ace dialogs write the same second head");
        ctx.Check(
            odd.Texts.Count == 4 && odd.Texts[1].X == OddAceHeadX,
            $"C3's ace head stands at x={OddAceHeadX} ({(odd.Texts.Count == 4 ? odd.Texts[1].X : -1f)})");
        ctx.Check(
            rest.Texts.Count == 4 && rest.Texts[1].X == AceHeadX,
            $"and every other ace head at x={AceHeadX} ({(rest.Texts.Count == 4 ? rest.Texts[1].X : -1f)})");

        // The inverse the session keys on: a chapter code back to its world-folder digit.
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        int inverted = 0;
        foreach (var mission in missions)
        {
            inverted += CampaignSequence.ChapterNumber(mission.ChapterFolder) == mission.Campaign ? 1 : 0;
        }

        ctx.Same(missions.Count, inverted, $"ChapterNumber inverts every campaign mission's own chapter folder");
        ctx.Same(0, CampaignSequence.ChapterNumber("c9"), $"and answers 0 for a code no chapter world carries");
        report.AppendLine(
            $"environment: {odd.State.Key} head2 x={odd.Texts[1].X}, {rest.State.Key} x={rest.Texts[1].X}");
    }

    // The real board over a real pause state, on the blackboard sheet: hidden until the pause, the
    // cursor on RESUME, and the pointer walking all four strips before firing one.
    private static void DriveBlackboardBoard(TestContext ctx, StringBuilder report)
    {
        if (Blackboard(ctx, FilmedEnvironment, FilmedType) is not { } sheet)
        {
            return;
        }

        var pause = new Flight.Modes.PauseState();
        var board = OriginalPauseBoard.Build(
            pause,
            _ => new MenuInput { Keyboard = false, Pads = System.Array.Empty<int>() },
            ctx.DataRoot,
            sheet,
            () => PauseReadout.Empty);
        ctx.Host.AddChild(board);
        try
        {
            ctx.Check(!board.Visible && board.Shown == null, $"the board is hidden and composes nothing before a pause");
            pause.TryToggle(0);
            ctx.Check(board.Visible, $"the pause raises the blackboard");
            ctx.Same(PauseScreens.ResumeRow, board.FocusedRow, $"the cursor rests on RESUME");
            ctx.Same(5, board.Shown?.Plaques.Count ?? 0, $"the raised sheet carries the five strips");
            WalkStrips(ctx, board, sheet, pause, report);
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    // The hit test over this dialog's own strips: the pointer moves the shared cursor onto each of
    // the five in turn. A press released on PREFERENCES fires that strip and leaves the sheet
    // standing. It and the photo strip are the only two rows that do.
    private static void WalkStrips(
        TestContext ctx,
        OriginalPauseBoard board,
        PauseSheet sheet,
        Flight.Modes.PauseState pause,
        StringBuilder report)
    {
        (float X, float Y, bool Pressed)? pointer = null;
        board.PointerSource = () => pointer;
        int walked = 0, hit = 0;
        for (int row = 0; row < Strips.Length; row++)
        {
            float onX = Strips[row].X + (PauseScreens.StripWidth / 2f);
            float onY = Strips[row].Y + (PauseScreens.StripHeight / 2f);
            hit += PauseScreens.RowAt(sheet, onX, onY) == row ? 1 : 0;
            pointer = (onX, onY, false);
            board._Process(0.0);
            walked += board.FocusedRow == row ? 1 : 0;
        }

        ctx.Same(Strips.Length, hit, $"the hit test answers each of the five strips for its own middle");
        ctx.Same(Strips.Length, walked, $"and the pointer moves the shared cursor onto each in turn");
        ctx.Same(-1, PauseScreens.RowAt(sheet, 400f, 300f), $"a point on the blackboard itself is on no strip");
        FirePhotoStrip(ctx, board, sheet, pause, at => pointer = at, report);

        int fired = 0;
        board.Preferences = () => fired++;
        float atX = Strips[PauseScreens.PreferencesRow].X + (PauseScreens.StripWidth / 2f);
        float atY = Strips[PauseScreens.PreferencesRow].Y + (PauseScreens.StripHeight / 2f);
        pointer = (atX, atY, false);
        board._Process(0.0);
        ctx.Check(CursorArt(board) == sheet.State.Cursor?.Rollover,
            $"the dialog's own cursor wears its rollover over a strip ({CursorArt(board)})");
        pointer = (atX, atY, true);
        board._Process(0.0);
        ctx.Check(
            StripArt(board, PauseScreens.PreferencesRow)
            == sheet.Strips[PauseScreens.PreferencesRow]?.Activate && fired == 0,
            $"the press holds PREFERENCES on its activate bitmap and fires nothing while it is down");
        pointer = (atX, atY, false);
        board._Process(0.0);
        ctx.Same(1, fired, $"the release on the strip opens the preferences leaf");
        ctx.Check(board.Visible, $"and the sheet stands, the leaf being the one action that comes back to it");

        pause.ForceResume();
        ctx.Check(!board.Visible, $"the resume takes the blackboard away");
        report.AppendLine($"pointer: walked {walked} strips, fired PREFERENCES {fired} time(s)");
    }

    // The remake's own strip on either sheet: pressed and released on its own plate, it opens photo
    // mode. The halt and the sheet stand exactly as they were. The mode is a still frame over the
    // halted world rather than a resume, which is what returns a player to the sheet.
    private static void FirePhotoStrip(
        TestContext ctx,
        OriginalPauseBoard board,
        PauseSheet sheet,
        Flight.Modes.PauseState pause,
        System.Action<(float X, float Y, bool Pressed)?> point,
        StringBuilder report)
    {
        if (sheet.Strips[PauseScreens.PhotoRow] is not { } strip)
        {
            ctx.Check(false, $"the sheet carries the remake's own photo strip");
            return;
        }

        int photos = 0;
        board.PhotoMode = () => photos++;
        float onX = strip.At.X + (PauseScreens.StripWidth / 2f);
        float onY = strip.At.Y + (PauseScreens.StripHeight / 2f);
        point((onX, onY, false));
        board._Process(0.0);
        ctx.Same(
            PauseScreens.PhotoRow, board.FocusedRow,
            $"the pointer reaches PHOTO MODE on its own plate ({onX}, {onY})");
        point((onX, onY, true));
        board._Process(0.0);
        ctx.Same(0, photos, $"and fires nothing while the button is down");
        point((onX, onY, false));
        board._Process(0.0);
        ctx.Same(1, photos, $"the release on it opens photo mode");
        ctx.Check(
            board.Visible && pause.Paused,
            $"and the halt and the sheet both stand, so the mode returns to this screen");
        report.AppendLine($"photo strip at ({strip.At.X}, {strip.At.Y}), fired {photos} time(s)");
    }

    // One Instant Action pause sheet by chapter code and mission type, keyed the way a session keys
    // it: the chapter's world-folder digit and the mission type's own letter.
    private static PauseSheet? Blackboard(TestContext ctx, string chapter, string missionType)
    {
        if (LoadScreens.LetterFor(missionType) is not { } letter)
        {
            return null;
        }

        return PauseSheet.Load(
            ctx.ZrdrPath,
            ctx.MessagesPath,
            EscapeDialog.InstantActionKey(CampaignSequence.ChapterNumber(chapter), letter),
            instantAction: true);
    }

    // Every pin the dialog's script authors has to be placed and visible. Seven of the 24 dialogs
    // put their pins past an authored Wait, which a reveal nobody advances never releases, so a
    // sheet built and left alone draws those maps with no flags on them at all.
    private static void CheckEveryPinPlaced(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, StringBuilder report)
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
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        string rimage = Path.Combine(ctx.DataRoot, "extracted", "rimage");
        var missing = new List<string>();
        int named = 0;
        foreach (var (mission, sheet) in sheets)
        {
            var board = PauseScreens.For(sheet, Readout(ctx, mission, sheet, 0), 0, false);
            foreach (string name in ArtNames(board))
            {
                named++;
                if (!File.Exists(Path.Combine(rimage, name.ToLowerInvariant() + ".png")))
                {
                    missing.Add($"{mission.MissionFolder}:{name}");
                }
            }

            // The dialog's own pointer is drawn only under a live pointer, so it reaches no
            // composition here; both its bitmaps still have to be in the extraction.
            foreach (string name in Cursors(sheet))
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

        ctx.Same(0, missing.Count, $"every bitmap the chapter's pause sheets name is extracted ({named} named)");
    }

    // The real board over a real pause state: hidden until the pause, composing on it, cursor on
    // RESUME, and the map drawn at its source crop rather than stretched.
    private static void DriveBoard(
        TestContext ctx, (CampaignMission Mission, PauseSheet Sheet) entry, StringBuilder report)
    {
        var pause = new Flight.Modes.PauseState();
        var board = OriginalPauseBoard.Build(
            pause,
            _ => new MenuInput { Keyboard = false, Pads = System.Array.Empty<int>() },
            ctx.DataRoot,
            entry.Sheet,
            () => Readout(ctx, entry.Mission, entry.Sheet, 0));
        ctx.Host.AddChild(board);
        try
        {
            ctx.Check(!board.Visible && board.Shown == null, $"the board is hidden and composes nothing before a pause");
            pause.TryToggle(0);
            ctx.Check(board.Visible, $"the pause raises the board");
            ctx.Same(PauseScreens.ResumeRow, board.FocusedRow, $"the cursor rests on RESUME");

            var shown = board.Shown;
            ctx.Check(shown != null, $"the raised board composes a sheet");
            if (shown != null && entry.Sheet.State.Map is { } map)
            {
                var chart = shown.Pictures[0];
                ctx.Check(
                    chart.Crop == new BoardCrop(map.Clip.X0, map.Clip.Y0, map.Clip.Width, map.Clip.Height)
                    && chart.X == map.Position.X && chart.Y == map.Position.Y,
                    $"the chart is drawn at its authored source crop ({chart.Crop}) and position");
                ctx.Check(chart.Width == 0f && chart.Height == 0f,
                    $"the chart is cropped rather than stretched to a size of its own");
                CheckWorldIcons(ctx, entry.Sheet, map, report);
            }

            CheckStripPlaces(ctx, entry.Sheet, shown, report);
            board._Process(0.0);
            DrivePointer(ctx, board, entry.Sheet, pause, report);
            pause.ForceResume();
            ctx.Check(!board.Visible, $"the resume takes the board away");
        }
        finally
        {
            ctx.Host.RemoveChild(board);
            board.QueueFree();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    // The five strips on the campaign sheet, drawn in walk order. The block authors four at their
    // own points, and the photo strip stands in the channel between the two columns. A plate that
    // moved would land on its neighbour rather than beside it, which no other check would see.
    private static void CheckStripPlaces(
        TestContext ctx, PauseSheet sheet, ComposedBoard? shown, StringBuilder report)
    {
        if (shown == null)
        {
            return;
        }

        ctx.Same(CampaignStrips.Length, shown.Plaques.Count, $"the sheet carries the five strips");
        int placed = 0;
        var places = new List<string>();
        for (int row = 0; row < CampaignStrips.Length && row < shown.Plaques.Count; row++)
        {
            var plaque = shown.Plaques[row];
            placed += plaque.X == CampaignStrips[row].X && plaque.Y == CampaignStrips[row].Y ? 1 : 0;
            places.Add($"{sheet.ButtonLabels[row]}({plaque.X}, {plaque.Y})");
        }

        ctx.Same(CampaignStrips.Length, placed, $"each stands at its own point: {string.Join(" ", places)}");
        ctx.Same(
            PauseScreens.PhotoRow,
            PauseScreens.RowAt(
                sheet, CampaignStrips[PauseScreens.PhotoRow].X + (PauseScreens.StripWidth / 2f),
                CampaignStrips[PauseScreens.PhotoRow].Y + (PauseScreens.StripHeight / 2f)),
            $"and the hit test answers PHOTO MODE for the middle of its own plate");
        report.AppendLine($"strips: {string.Join(" ", places)}");
    }

    // The pointer over the raised board: PREFERENCES, which leaves the board standing when it
    // fires, and the photo strip, the other row that does. Both are driven through the same frame
    // the pad's own step runs in.
    private static void DrivePointer(
        TestContext ctx, OriginalPauseBoard board, PauseSheet sheet, Flight.Modes.PauseState pause,
        StringBuilder report)
    {
        var strip = sheet.Strips[PauseScreens.PreferencesRow];
        if (strip == null)
        {
            ctx.Check(false, $"the shared block authors the PREFERENCES strip");
            return;
        }

        (float X, float Y, bool Pressed)? pointer = null;
        int fired = 0;
        board.PointerSource = () => pointer;
        board.Preferences = () => fired++;
        float onX = strip.At.X + (PauseScreens.StripWidth / 2f);
        float onY = strip.At.Y + (PauseScreens.StripHeight / 2f);
        int before = board.FocusedRow;

        pointer = (onX, onY, false);
        board._Process(0.0);
        ctx.Same(PauseScreens.PreferencesRow, board.FocusedRow,
            $"a pointer on the third strip moves the shared cursor onto it (from {before})");
        ctx.Check(StripArt(board, PauseScreens.PreferencesRow) == strip.Rollover,
            $"and that strip wears the rollover bitmap ({StripArt(board, PauseScreens.PreferencesRow)})");
        ctx.Check(CursorArt(board) == sheet.State.Cursor?.Rollover,
            $"and the dialog's own cursor wears its rollover ({CursorArt(board)})");

        pointer = (onX, onY, true);
        board._Process(0.0);
        ctx.Check(StripArt(board, PauseScreens.PreferencesRow) == strip.Activate && fired == 0,
            $"the press holds the strip on its activate bitmap and fires nothing while it is down");

        pointer = (onX, onY, false);
        board._Process(0.0);
        ctx.Same(1, fired, $"the release on the strip fires that strip's action");

        pointer = (400f, 300f, false);
        board._Process(0.0);
        ctx.Same(PauseScreens.PreferencesRow, board.FocusedRow,
            $"a pointer on no strip leaves the cursor where it stands");
        ctx.Check(CursorArt(board) == sheet.State.Cursor?.Bitmap,
            $"and the cursor goes back to its plain bitmap ({CursorArt(board)})");

        pointer = (onX, onY, true);
        board._Process(0.0);
        pointer = (400f, 300f, true);
        board._Process(0.0);
        pointer = (400f, 300f, false);
        board._Process(0.0);
        ctx.Same(1, fired, $"a press released off the strip it took hold of fires nothing");

        // The pad's own path with no pointer at all: the cursor stays put and no strip is held.
        pointer = null;
        board._Process(0.0);
        ctx.Same(PauseScreens.PreferencesRow, board.FocusedRow,
            $"the cursor survives the pointer leaving the screen");
        ctx.Check(StripArt(board, PauseScreens.PreferencesRow) == strip.Rollover,
            $"and the focused strip is back on its rollover bitmap with none held");

        FirePhotoStrip(ctx, board, sheet, pause, at => pointer = at, report);
        report.AppendLine(
            $"pointer: strip {PauseScreens.PreferencesRow} at ({onX}, {onY}) focused, "
            + $"fired {fired} time(s), cursor {sheet.State.Cursor?.Bitmap ?? "-"}/"
            + $"{sheet.State.Cursor?.Rollover ?? "-"}");
    }

    private static string? StripArt(OriginalPauseBoard board, int row) =>
        board.Shown is { } shown && row < shown.Plaques.Count ? shown.Plaques[row].Art.Name : null;

    private static string? CursorArt(OriginalPauseBoard board)
    {
        if (board.Shown is not { } shown)
        {
            return null;
        }

        foreach (var panel in shown.Overlays)
        {
            foreach (var picture in panel.Pictures)
            {
                return picture.Art.Name;
            }
        }

        return null;
    }

    private static IEnumerable<string> Cursors(PauseSheet sheet)
    {
        if (sheet.State.Cursor is not { } cursor)
        {
            yield break;
        }

        yield return cursor.Bitmap;
        if (cursor.Rollover != cursor.Bitmap)
        {
            yield return cursor.Rollover;
        }
    }

    // An icon inside the window lands on the map's own screen rectangle; one outside it draws
    // nothing, which is the original's answer rather than an icon clamped to an edge.
    private static void CheckWorldIcons(
        TestContext ctx, PauseSheet sheet, EscapeMap map, StringBuilder report)
    {
        float midX = (map.World.X0 + map.World.X1) / 2f;
        float midZ = -((map.World.Y0 + map.World.Y1) / 2f);
        var inside = new PauseWorldIcon(sheet.Shared.OwnShip, midX, midZ, 0f);
        var shown = PauseScreens.For(
            sheet, new PauseReadout(System.Array.Empty<PauseObjective>(), string.Empty, new[] { inside }),
            0, false);
        var placed = FindArt(shown, sheet.Shared.OwnShip);
        ctx.Check(
            placed != null && placed.Centered
            && placed.X >= map.ScreenX0 && placed.X <= map.ScreenX1
            && placed.Y >= map.ScreenY0 && placed.Y <= map.ScreenY1,
            $"the window's middle places OWNSHIP inside the map's screen rectangle ({placed?.X}, {placed?.Y})");
        report.AppendLine(
            $"ownship at world ({midX}, {midZ}) -> ({placed?.X}, {placed?.Y}) "
            + $"in [{map.ScreenX0}..{map.ScreenX1}] x [{map.ScreenY0}..{map.ScreenY1}]");

        var offMap = new PauseWorldIcon(sheet.Shared.OwnShip, midX + 1e6f, midZ, 0f);
        var hidden = PauseScreens.For(
            sheet, new PauseReadout(System.Array.Empty<PauseObjective>(), string.Empty, new[] { offMap }),
            0, false);
        ctx.Check(FindArt(hidden, sheet.Shared.OwnShip) == null, $"a position off the window draws no icon");
    }

    // The four filmed poses: rows 1 to 3 completing in turn, which must move the marks and nothing
    // else, and C3/M01's own flags, which the stills pin exactly.
    private static void CheckFilmedPoses(
        TestContext ctx,
        List<(CampaignMission Mission, PauseSheet Sheet)> sheets,
        int at,
        StringBuilder report)
    {
        if (at < 0)
        {
            report.AppendLine($"{FilmedChapter}/{FilmedMission} is absent; poses not checked");
            return;
        }

        var filmed = sheets[at];

        var pins = new List<string>();
        foreach (var element in filmed.Sheet.Reveal.Elements)
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

        int lastMarked = -1;
        for (int done = 0; done < 4; done++)
        {
            var board = PauseScreens.For(
                filmed.Sheet, Readout(ctx, filmed.Mission, filmed.Sheet, done), 0, false);
            int marked = 0;
            foreach (var note in board.Notes)
            {
                foreach (bool flag in note.Marked ?? System.Array.Empty<bool>())
                {
                    marked += flag ? 1 : 0;
                }
            }

            report.AppendLine($"pose {done}: {marked} marked row(s), {board.Pictures.Count} pictures");
            ctx.Same(done, marked, $"pose {done} marks exactly the rows that completed");
            ctx.Check(marked > lastMarked, $"pose {done} marks more rows than the pose before it");
            lastMarked = marked;
        }
    }

    // The marks against the live objectives runtime, on the mission where the note's row order and
    // the script's numbering disagree: the rows are OBJECTIVE15 then OBJECTIVE14, so a mark taken
    // from a row's priority would check row one when OBJECTIVE1's two-second wake completes.
    private static void CheckNumberedMarks(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        int at = sheets.FindIndex(s =>
            s.Mission.ChapterFolder.Equals(NumberedChapter, System.StringComparison.OrdinalIgnoreCase)
            && s.Mission.MissionFolder.Equals(NumberedMission, System.StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            ctx.Check(false, $"{NumberedChapter}/{NumberedMission} is in the sequence");
            return;
        }

        var entry = sheets[at];
        var reader = Zrdr.LoadFile(
            SessionPaths.MissionZrdr(
                ctx.DataRoot, entry.Mission.ChapterFolder, entry.Mission.MissionFolder),
            "objectives.json");
        var note = BriefingObjectives.Load(reader, Messages.Load(ctx.MessagesPath));
        ctx.Check(
            note.Count == 2 && note[0].Priority == 1 && note[0].Number == 15
            && note[1].Priority == 2 && note[1].Number == 14,
            $"the note's two rows are OBJECTIVE15 at priority 1 and OBJECTIVE14 at priority 2");

        int rowOne = 0;
        foreach (int index in entry.Sheet.Reveal.RevealedObjectives)
        {
            rowOne += index == 0 ? 1 : 0;
        }

        ctx.Check(rowOne > 0, $"the sheet's own script reveals the priority 1 row ({rowOne} time(s))");

        var graph = new Session.Objectives.ObjectiveGraph(Session.Objectives.ObjectiveScript.Parse(reader), new NoWorld());
        for (float t = 0f; t < 3f; t += 0.1f)
        {
            graph.Step(0.1f);
        }

        var open = PauseReadout.Rows(note, graph.CompletedOf);
        float wakeAt = graph.Elapsed;
        ctx.Check(graph.CompletedOf(1), $"OBJECTIVE1, the conditionless two-second wake, has completed");
        ctx.Check(!graph.CompletedOf(15), $"OBJECTIVE15, which the priority 1 row stands for, has not");
        ctx.Check(!open[0].Completed && !open[1].Completed, $"so neither parchment row reads done");
        ctx.Same(0, Marks(entry.Sheet, open), $"and the composed parchment carries no mark");

        graph.Wake(15);
        graph.Step(0.1f);
        var done = PauseReadout.Rows(note, graph.CompletedOf);
        ctx.Check(graph.CompletedOf(15), $"a wake completes the conditionless OBJECTIVE15");
        ctx.Check(done[0].Completed && !done[1].Completed, $"which marks the priority 1 row and only it");
        ctx.Same(rowOne, Marks(entry.Sheet, done), $"and the composed parchment marks that row");
        report.AppendLine(
            $"{NumberedChapter}/{NumberedMission} rows: "
            + $"O{note[0].Number}/p{note[0].Priority}, O{note[1].Number}/p{note[1].Priority}; "
            + $"at {wakeAt:0.0} s O1 alone is done and the parchment marks {Marks(entry.Sheet, open)}, "
            + $"with O15 done it marks {Marks(entry.Sheet, done)}");
    }

    // The parchment's rows in the face the original sets them in, and how many lines each takes in
    // the measure the widget authors. Only the renderer's font knows how tall a wrapped entry drew.
    // The count is measured with the face the screen writes it in rather than composed.
    private static void CheckRowWrap(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        var probe = new Godot.Control();
        ctx.Host.AddChild(probe);
        try
        {
            if (probe.GetThemeDefaultFont() is not { } font)
            {
                throw new SuiteSkippedException("the default theme carries no font to measure with");
            }

            SweepRowWrap(ctx, sheets, font, report);
        }
        finally
        {
            ctx.Host.RemoveChild(probe);
            probe.QueueFree();
        }
    }

    private static void SweepRowWrap(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, Godot.Font font,
        StringBuilder report)
    {
        var fit = BoardFit.For(BoardFit.AuthoredWidth, BoardFit.AuthoredHeight);
        var paper = Paper(ctx, sheets[0].Sheet.Shared.Objectives);
        int slanted = 0, tall = 0, rows = 0, dropped = 0, shrunk = 0;
        float past = float.NegativeInfinity, over = float.NegativeInfinity;
        string deepest = "-", widest = "-";
        BoardNote? deep = null;
        var filmed = System.Array.Empty<int>();
        var numbered = System.Array.Empty<int>();
        foreach (var (mission, sheet) in sheets)
        {
            var board = PauseScreens.For(sheet, Readout(ctx, mission, sheet, 0), 0, false);
            if (board.Notes.Count == 0)
            {
                continue;
            }

            var written = board.Notes[0];
            var note = ComposedBoardView.Fitted(fit, font, written);
            slanted += note.Italic ? 1 : 0;
            shrunk += note.Size < written.Size ? 1 : 0;
            var counts = RowLines(fit, font, note);
            rows += counts.Length;
            var height = ComposedBoardView.Measure(fit, font, note);
            var placed = note.Flow(height);
            int lost = note.Entries.Count - placed.Count;
            dropped += lost;
            foreach (int lines in counts)
            {
                tall += lines > 2 ? 1 : 0;
            }

            var corner = Corner(ComposedBoardView.MeasureBox(fit, font, note.Size), placed);
            string named = $"{mission.ChapterFolder}/{mission.MissionFolder}";
            if (corner.Y - paper.End.Y > past)
            {
                past = corner.Y - paper.End.Y;
                deepest = named;
                deep = written;
            }

            if (corner.X - paper.End.X > over)
            {
                over = corner.X - paper.End.X;
                widest = named;
            }

            if (Named(mission, FilmedChapter, FilmedMission))
            {
                filmed = counts;
            }
            else if (Named(mission, NumberedChapter, NumberedMission))
            {
                numbered = counts;
            }

            report.AppendLine(
                $"{named} rows {string.Join("/", counts)} at {note.Size:0} pt, dropped {lost}, "
                + $"corner {corner.X:0.0}/{corner.Y:0.0}");
        }

        ctx.Same(sheets.Count, slanted, $"every campaign parchment sets its rows in the slanted face");
        string drew = string.Join("/", filmed), want = string.Join("/", FilmedRowLines);
        string second = string.Join("/", numbered), wantSecond = string.Join("/", NumberedRowLines);
        ctx.Check(
            System.Linq.Enumerable.SequenceEqual(FilmedRowLines, filmed),
            $"{FilmedChapter}/{FilmedMission}'s rows take {drew} lines, the crop's {want}");
        ctx.Check(
            System.Linq.Enumerable.SequenceEqual(NumberedRowLines, numbered),
            $"{NumberedChapter}/{NumberedMission}'s take {second}, the crop's {wantSecond}");
        ctx.Same(
            OverrunRows, dropped,
            $"every objective in the sequence is drawn rather than composed away ({dropped} lost)");
        ctx.Check(
            paper.End.X == PaperRight && paper.End.Y == PaperBottom,
            $"the bitmap's own paper ends at {paper.End.X:0}/{paper.End.Y:0}, where the rows are held");
        ctx.Check(
            over <= 0f,
            $"no row reaches the torn right edge, {widest}'s widest stopping {-over:0.0} px inside it");
        ctx.Check(
            past <= 0f,
            $"and none the torn bottom, {deepest}'s last ending {-past:0.0} px above it");
        CheckSwollenListShrinks(ctx, fit, font, deep, paper, report);
        report.AppendLine(
            $"wrap: {rows} rows, {tall} of them on three lines or more, {dropped} dropped, "
            + $"{shrunk} sheets shrunk; paper ends at {paper.End.X:0.0}/{paper.End.Y:0.0}, "
            + $"deepest {deepest}, widest {widest}");
    }

    // The largest rectangle of paper pixels in a bitmap, by the histogram walk. Each row carries
    // how far the solid run above every column reaches. The widest bar-chart rectangle standing on
    // that row is a candidate. A profile of per-row runs cannot do this. A few rows of the torn
    // edge are almost all fringe and would shrink the answer to a sliver.
    private static Godot.Rect2I Solid(Godot.Image art)
    {
        int wide = art.GetWidth(), high = art.GetHeight();
        var run = new int[wide];
        var best = default(Godot.Rect2I);
        for (int y = 0; y < high; y++)
        {
            for (int x = 0; x < wide; x++)
            {
                run[x] = art.GetPixel(x, y).A >= SolidAlpha ? run[x] + 1 : 0;
            }

            for (int x = 0; x < wide; x++)
            {
                int deep = run[x];
                for (int span = x; span < wide && deep > 0; span++)
                {
                    deep = Godot.Mathf.Min(deep, run[span]);
                    if (deep * (span - x + 1) > best.Size.X * best.Size.Y)
                    {
                        best = new Godot.Rect2I(x, y - deep + 1, span - x + 1, deep);
                    }
                }
            }
        }

        return best;
    }

    // The deepest list in a face too large for the paper, which the fit has to bring back onto it
    // without losing a row. This is the shrink's only exercise: every campaign list as written fits.
    private static void CheckSwollenListShrinks(
        TestContext ctx, BoardFit fit, Godot.Font font, BoardNote? deep, Godot.Rect2 paper,
        StringBuilder report)
    {
        if (deep == null)
        {
            return;
        }

        var swollen = ComposedBoardView.Fitted(fit, font, deep with { Size = deep.Size + Swollen });
        var placed = swollen.Flow(ComposedBoardView.Measure(fit, font, swollen));
        var corner = Corner(ComposedBoardView.MeasureBox(fit, font, swollen.Size), placed);
        ctx.Check(
            placed.Count == deep.Entries.Count && corner.X <= paper.End.X && corner.Y <= paper.End.Y,
            $"a list {Swollen:0} pt too large is fitted onto the paper at {swollen.Size:0} pt, {placed.Count} rows kept");
        report.AppendLine($"fit: the deepest list at {deep.Size + Swollen:0} pt comes back at {swollen.Size:0} pt");
    }

    // One note's entries as line counts. A short word measured in the same box is one line's worth.
    // An entry's own measured height divided by it is how many lines it wrapped to.
    private static int[] RowLines(BoardFit fit, Godot.Font font, BoardNote note)
    {
        var height = ComposedBoardView.Measure(fit, font, note);
        float one = height("X", note.Width);
        var counts = new int[note.Entries.Count];
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = one > 0f ? Godot.Mathf.RoundToInt(height(note.Entries[i], note.Width) / one) : 0;
        }

        return counts;
    }

    private static bool Named(CampaignMission mission, string chapter, string folder) =>
        mission.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
        && mission.MissionFolder.Equals(folder, System.StringComparison.OrdinalIgnoreCase);

    // The one mission whose objective block carries two IDENTITY entries. Only the keyed one is a
    // note line, so the block owns a single row and a single mark. A reader that took every entry
    // as a line would put an empty row on the parchment and mark two rows on one completion.
    private static void CheckDoubledIdentity(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, StringBuilder report)
    {
        int at = sheets.FindIndex(s =>
            s.Mission.ChapterFolder.Equals(DoubledChapter, System.StringComparison.OrdinalIgnoreCase)
            && s.Mission.MissionFolder.Equals(DoubledMission, System.StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            ctx.Check(false, $"{DoubledChapter}/{DoubledMission} is in the sequence");
            return;
        }

        var entry = sheets[at];
        var note = BriefingObjectives.Load(
            Zrdr.LoadFile(
                SessionPaths.MissionZrdr(
                    ctx.DataRoot, entry.Mission.ChapterFolder, entry.Mission.MissionFolder),
                "objectives.json"),
            Messages.Load(ctx.MessagesPath));
        int fromBlock = 0;
        int priority = 0;
        foreach (var line in note)
        {
            if (line.Number == DoubledBlock)
            {
                fromBlock++;
                priority = line.Priority;
            }
        }

        ctx.Same(4, note.Count, $"{DoubledChapter}/{DoubledMission}'s note has four rows, none of them empty");
        ctx.Same(1, fromBlock, $"OBJECTIVE{DoubledBlock} gives it one row, its keyless SECONDARY entry none");
        ctx.Same(3, priority, $"and that row is the keyed PRIMARY entry's own priority");

        var rows = PauseReadout.Rows(note, number => number == DoubledBlock);
        int done = 0;
        foreach (var row in rows)
        {
            done += row.Completed ? 1 : 0;
        }

        ctx.Same(1, done, $"so OBJECTIVE{DoubledBlock}'s completion reads done on one row");
        ctx.Same(1, Marks(entry.Sheet, rows), $"and the composed parchment carries one mark");
        report.AppendLine(
            $"{DoubledChapter}/{DoubledMission} rows: {note.Count}, "
            + $"{fromBlock} from OBJECTIVE{DoubledBlock} at priority {priority}, "
            + $"marks on its completion: {Marks(entry.Sheet, rows)}");
    }

    private static int Marks(PauseSheet sheet, IReadOnlyList<PauseObjective> rows)
    {
        var board = PauseScreens.For(
            sheet,
            new PauseReadout(rows, string.Empty, System.Array.Empty<PauseWorldIcon>()),
            0,
            false);
        int marked = 0;
        foreach (var note in board.Notes)
        {
            foreach (bool flag in note.Marked ?? System.Array.Empty<bool>())
            {
                marked += flag ? 1 : 0;
            }
        }

        return marked;
    }

    // The mission's own objectives in the order its dialog's script indexes them, with the first
    // few marked so the parchment's marks are exercised.
    private static PauseReadout Readout(
        TestContext ctx, CampaignMission mission, PauseSheet sheet, int completed)
    {
        var objectives = BriefingObjectives.Load(
            Zrdr.LoadFile(
                SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder),
                "objectives.json"),
            Messages.Load(ctx.MessagesPath));
        var rows = new List<PauseObjective>(objectives.Count);
        for (int i = 0; i < objectives.Count; i++)
        {
            rows.Add(new PauseObjective(objectives[i].Text, i < completed));
        }

        return new PauseReadout(
            rows, Session.Campaign.CampaignMementos.BitmapFor(Seated), System.Array.Empty<PauseWorldIcon>());
    }

    private static Session.Campaign.CampaignProfileDef SeatProfile()
    {
        var def = Session.Campaign.CampaignProfileDef.NewProfile("Pause Sheet");
        def.Memento = ChosenMemento;
        return def;
    }

    // The memento slot takes the seated profile's own picture, and a session with nobody seated
    // still takes the seeded pin-up. A sheet that asked no profile would draw the seeded name in
    // both cases and pass every other check here.
    private static void CheckSeatedMemento(
        TestContext ctx, List<(CampaignMission Mission, PauseSheet Sheet)> sheets, int at,
        StringBuilder report)
    {
        string chosen = Session.Campaign.CampaignMementos.BitmapFor(Seated);
        string seeded = Session.Campaign.CampaignMementos.BitmapFor(null);
        ctx.Check(
            chosen == Session.Campaign.CampaignMementos.Bitmap(ChosenMemento) && chosen != seeded,
            $"the seated profile hangs {chosen} where a session with no profile hangs {seeded}");
        if (at < 0)
        {
            return;
        }

        var entry = sheets[at];
        string missionZrdr = SessionPaths.MissionZrdr(
            ctx.DataRoot, entry.Mission.ChapterFolder, entry.Mission.MissionFolder);
        var director = Session.Campaign.CampaignDirector.Create(
            Session.Objectives.ObjectiveScript.Load(missionZrdr), entry.Mission, Seated, null);
        ctx.Check(
            director.Memento == chosen,
            $"the flown mission's own director hands that picture to the readout ({director.Memento})");
        var board = PauseScreens.For(entry.Sheet, Readout(ctx, entry.Mission, entry.Sheet, 0), 0, false);
        var photo = FindArt(board, chosen);
        ctx.Check(
            photo != null && photo.X == entry.Sheet.State.MementoAt.X
            && photo.Y == entry.Sheet.State.MementoAt.Y,
            $"the sheet hangs the chosen picture at the slot's authored point ({photo?.X}, {photo?.Y})");
        var bare = PauseScreens.For(
            entry.Sheet,
            new PauseReadout(
                System.Array.Empty<PauseObjective>(), seeded, System.Array.Empty<PauseWorldIcon>()),
            0,
            false);
        ctx.Check(
            FindArt(bare, seeded) != null && FindArt(bare, chosen) == null,
            $"and a pause with no profile behind it hangs the seeded pin-up alone");
        report.AppendLine($"memento: seated {chosen} at ({photo?.X}, {photo?.Y}), no profile {seeded}");
    }

    private static int Filmed(List<(CampaignMission Mission, PauseSheet Sheet)> sheets) =>
        sheets.FindIndex(s =>
            s.Mission.ChapterFolder.Equals(FilmedChapter, System.StringComparison.OrdinalIgnoreCase)
            && s.Mission.MissionFolder.Equals(FilmedMission, System.StringComparison.OrdinalIgnoreCase));

    private static int CountPins(PauseSheet sheet)
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

        foreach (var plaque in board.Plaques)
        {
            yield return plaque.Art.Name;
        }

        foreach (var note in board.Notes)
        {
            if (note.Mark is { } mark)
            {
                yield return mark.Name;
            }
        }
    }

    // The objectives runtime's world seam with no world behind it: the families a session cannot
    // answer report null, exactly as the live adapter does, and every world-touching action is a
    // no-op. Enough to run a mission's own script for its timing and its chaining.
    private sealed class NoWorld : Session.Objectives.IObjectiveWorld
    {
        public bool? NodeInactive(IReadOnlyList<string> path) => null;

        public int AnimState(string anim) => 0;

        public int? GroupLiveCount(int group, string? generator) => null;

        public void WidenGroupEngagement(int group)
        {
        }

        public bool? TravelersMet(Session.Objectives.TravelersSpec spec) => null;

        public void WakeupEnemies(IReadOnlyList<string> names)
        {
        }

        public void WakeupTurrets(IReadOnlyList<string> patterns)
        {
        }

        public void WakeupZepTurrets(IReadOnlyList<string> nodes)
        {
        }

        public void WakeupGenerator(string name, int count)
        {
        }

        public void WakeAnim(string anim, string? node)
        {
        }

        public void PlaySoundGroup(string group)
        {
        }

        public void StopQueuedSounds(IReadOnlyList<string> names)
        {
        }

        public void WarpVehicle(string vehicle, IReadOnlyList<Session.Objectives.WarpPoint> points)
        {
        }

        public void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries)
        {
        }

        public void SetAiNet(IReadOnlyList<(string Name, string Net)> entries)
        {
        }

        public void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries)
        {
        }

        public void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries)
        {
        }

        public void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries)
        {
        }

        public void StartTaxi(IReadOnlyList<string> names)
        {
        }
    }
}
