using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Overlays;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Original presentation's pause screen off engine: the map's world window, the flag states the
/// dialog authors, and what the sheet composes for the filmed mission. The authored side is
/// <c>escape.zrd</c>, <c>ia_escape.zrd</c> and <c>messages.json</c>, so these read the extraction
/// rather than restating it (docs/org/pause-screen.md).
/// </summary>
public class PauseScreensTests
{
    // CM01 (C3/M01), the mission the reference stills were filmed on, and its own dialog key.
    private const int FilmedCampaign = 6;
    private const int FilmedMission = 1;

    /// <summary>The clip is a rectangle in the bitmap, not on the screen: the crop's top left
    /// lands on the map's authored position, so the visible map runs from there to there plus the
    /// crop's size, and that is the rectangle the world window maps onto.</summary>
    [Fact]
    public void TheClipIsASourceCropAndSetsTheMapsScreenRectangle()
    {
        var map = Map(new EscapeRect(211, 51, 784, 551), new EscapeRect(0, 0, 100, 100));

        Assert.Equal(573, map.Clip.Width);
        Assert.Equal(500, map.Clip.Height);
        Assert.Equal((16f, 19f), (map.ScreenX0, map.ScreenY0));
        Assert.Equal((589f, 519f), (map.ScreenX1, map.ScreenY1));
    }

    /// <summary>The window maps world X across and negated world Z down, both ends inclusive, and
    /// altitude is not read at all: the chart is a plan view.</summary>
    [Fact]
    public void TheWorldWindowMapsXAcrossAndNegatedZDown()
    {
        var map = Map(new EscapeRect(211, 51, 784, 551), new EscapeRect(-1000, 400, 1000, -400));

        Assert.True(map.TryProject(-1000f, -400f, out var topLeft));
        Assert.Equal(new BriefingPoint(16f, 19f), topLeft);
        Assert.True(map.TryProject(1000f, 400f, out var bottomRight));
        Assert.Equal(new BriefingPoint(589f, 519f), bottomRight);
        Assert.True(map.TryProject(0f, 0f, out var middle));
        Assert.Equal(new BriefingPoint(302f, 269f), middle);

        // Altitude moves nothing, which is why the same X and Z answer the same pixel.
        Assert.True(map.TryProject(0f, 0f, out var again));
        Assert.Equal(middle, again);
    }

    /// <summary>Off the window answers false, which is what turns the icon off rather than clamping
    /// it to an edge.</summary>
    [Fact]
    public void APositionOffTheWindowIsNotOnTheMap()
    {
        var map = Map(new EscapeRect(211, 51, 784, 551), new EscapeRect(-1000, 400, 1000, -400));

        Assert.False(map.TryProject(-1001f, 0f, out _));
        Assert.False(map.TryProject(0f, -401f, out _));
        Assert.False(map.TryProject(1001f, 0f, out _));
        Assert.False(map.TryProject(0f, 401f, out _));
    }

    /// <summary>The icon's turn: the chart puts world +X right and world -Z up and the art points
    /// up, so a nose at -Z is no turn and east is a quarter turn clockwise.</summary>
    [Fact]
    public void AnIconsTurnIsMeasuredFromANoseAtNegativeZ()
    {
        Assert.Equal(0f, MissionMap.Heading(0f, -1f), 4);
        Assert.Equal(0.25f, MissionMap.Heading(1f, 0f), 4);
        Assert.Equal(0.5f, System.Math.Abs(MissionMap.Heading(0f, 1f)), 4);
        Assert.Equal(-0.25f, MissionMap.Heading(-1f, 0f), 4);
    }

    /// <summary>Every <c>WORLD</c> and <c>CLIP</c> bound is truncated toward zero on the way in,
    /// which is the conversion the original's reader applies to an authored float.</summary>
    [ExtractedDataFact]
    public void EveryWorldBoundIsTruncatedTowardZero()
    {
        var map = Dialog().Find(EscapeDialog.CampaignKey(FilmedCampaign, FilmedMission))!.Map!;

        Assert.Equal("HA-m1MAP", map.Bitmap);
        Assert.Equal(new EscapeRect(211, 51, 784, 551), map.Clip);
        Assert.Equal(new EscapeRect(-10818, 9155, -2204, 540), map.World);
        Assert.Equal(new BriefingPoint(16f, 19f), map.Position);
    }

    /// <summary>The flag state is authored, not computed: the filmed mission's script names
    /// <c>pin6</c>, the question-mark flag, at three of its four points and <c>pin4</c> at the
    /// fourth. Nothing in the run can change which bitmap stands where.</summary>
    [ExtractedDataFact]
    public void TheFilmedMissionAuthorsThreeQuestionFlagsAndOneNumberedFour()
    {
        var reveal = new BriefingReveal(
            Dialog().Find(EscapeDialog.CampaignKey(FilmedCampaign, FilmedMission))!.Steps,
            System.Array.Empty<double>());

        var pins = reveal.Elements.Where(e => e.Id.StartsWith("OBJPIN")).ToList();
        Assert.Equal(4, pins.Count);
        Assert.Equal(new[] { "pin6", "pin6", "pin6", "pin4" }, pins.Select(p => p.Bitmap));
        Assert.Equal(
            new[] { (506f, 149f), (396f, 254f), (181f, 244f), (106f, 319f) },
            pins.Select(p => (p.At.X, p.At.Y)));
        Assert.All(pins, p => Assert.True(p.Center && p.Visible));
    }

    /// <summary>The shared block: the four strips at their authored positions, each with its three
    /// bitmaps, and the two world-placed icons carrying a bitmap and no position.</summary>
    [ExtractedDataFact]
    public void TheSharedBlockCarriesFourStripsAndTheTwoWorldPlacedIcons()
    {
        var shared = Dialog().Shared!;

        Assert.Equal(4, shared.Buttons.Count);
        Assert.Equal(PauseScreens.ButtonKeys, shared.Buttons.Select(b => b.Key));
        Assert.Equal(
            new[] { (107f, 528f), (127f, 559f), (367f, 528f), (347f, 559f) },
            shared.Buttons.Select(b => (b.At.X, b.At.Y)));
        Assert.All(shared.Buttons, b => Assert.Equal(
            ("escape_button1", "escape_button2", "escape_button3"), (b.Normal, b.Rollover, b.Activate)));
        Assert.All(shared.Buttons, b => Assert.Equal(new BriefingPoint(66f, 7f), b.LabelOffset));
        Assert.Equal("singledev", shared.OwnShip);
        Assert.Equal("NW-m1wv_icon", shared.MyZep);
    }

    /// <summary>The parchment's own geometry, shared with the loading dialog, and its mark.</summary>
    [ExtractedDataFact]
    public void TheParchmentIsTheSharedObjectivesListAtItsAuthoredPlace()
    {
        var list = Dialog().Shared!.Objectives!;

        Assert.Equal("parchment", list.Background);
        Assert.Equal(new BriefingPoint(555f, 6f), list.BackgroundAt);
        Assert.Equal(new BriefingPoint(595f, 25f), list.TitleAt);
        Assert.Equal(new BriefingPoint(580f, 50f), list.ListAt);
        Assert.Equal((190f, 255f, 5f), (list.WrapWidth, list.WrapHeight, list.Spacing));
        Assert.Equal("obj_check1", list.CheckMark);
    }

    /// <summary>The whole sheet for the filmed mission: the frame behind it, the map at its crop,
    /// the parchment, the four pins, the memento and the five strips, each label resolved.</summary>
    [ExtractedDataFact]
    public void TheFilmedMissionsSheetComposesTheChartTheParchmentAndTheFiveStrips()
    {
        var sheet = Sheet();
        var board = PauseScreens.For(sheet, Readout(sheet, marked: 0), PauseScreens.ResumeRow, false);

        Assert.Equal(
            new[] { "Resume", "Photo Mode", "Restart", "Preferences", "Quit" }, sheet.ButtonLabels);
        Assert.Equal("Objectives", sheet.ObjectivesTitle);
        var frame = Assert.Single(board.Backdrop);
        Assert.Equal("loadframe", frame.Art.Name);

        var chart = board.Pictures[0];
        Assert.Equal("HA-m1MAP", chart.Art.Name);
        Assert.Equal(new BoardCrop(211f, 51f, 573f, 500f), chart.Crop);
        Assert.Equal((16f, 19f), (chart.X, chart.Y));
        Assert.Equal("parchment", board.Pictures[1].Art.Name);
        Assert.Contains(board.Pictures, p => p.Art.Name == "momento_shad");
        Assert.Contains(board.Pictures, p => p.Art.Name == "ms_p_initialpinup1");
        Assert.Equal(
            new[] { "pin6", "pin6", "pin6", "pin4" },
            board.Pictures.Where(p => p.Art.Name.StartsWith("pin")).Select(p => p.Art.Name));

        Assert.Equal(5, board.Plaques.Count);
        Assert.Equal(new[] { "Resume", "Photo Mode", "Restart", "Preferences", "Quit" },
            board.Plaques.Select(p => p.Label));
        Assert.Equal(
            new[] { (107f, 528f), (237f, 528f), (127f, 559f), (367f, 528f), (347f, 559f) },
            board.Plaques.Select(p => (p.X, p.Y)));
        Assert.Equal("escape_button2", board.Plaques[PauseScreens.ResumeRow].Art.Name);
        Assert.Equal("escape_button1", board.Plaques[PauseScreens.QuitRow].Art.Name);
        Assert.Equal(BoardInk.LabelRollover, board.Plaques[PauseScreens.ResumeRow].Ink);
        Assert.Equal(BoardInk.LabelNormal, board.Plaques[PauseScreens.QuitRow].Ink);
    }

    /// <summary>A held strip takes the third bitmap and the activate ink, which is the one state a
    /// cursor never composes for itself.</summary>
    [ExtractedDataFact]
    public void AHeldStripTakesTheThirdBitmap()
    {
        var sheet = Sheet();
        var board = PauseScreens.For(sheet, PauseReadout.Empty, PauseScreens.RestartRow, true);

        Assert.Equal("escape_button3", board.Plaques[PauseScreens.RestartRow].Art.Name);
        Assert.Equal(BoardInk.LabelActivate, board.Plaques[PauseScreens.RestartRow].Ink);
    }

    /// <summary>The five strips are the screen's only widgets, each a 132x28 plate on its own
    /// corner, and a point on none of them answers -1 rather than the nearest row.</summary>
    [ExtractedDataFact]
    public void EachStripIsHitOnItsOwnPlate()
    {
        var sheet = Sheet();

        Assert.Equal(PauseScreens.ResumeRow, PauseScreens.RowAt(sheet, 107f, 528f));
        Assert.Equal(PauseScreens.PhotoRow, PauseScreens.RowAt(sheet, 303f, 542f));
        Assert.Equal(PauseScreens.RestartRow, PauseScreens.RowAt(sheet, 193f, 573f));
        Assert.Equal(PauseScreens.PreferencesRow, PauseScreens.RowAt(sheet, 433f, 542f));
        Assert.Equal(PauseScreens.QuitRow, PauseScreens.RowAt(sheet, 413f, 573f));

        // The channel is four pixels narrower than the plate it holds, so two columns belong to
        // both the photo strip and a neighbour, and the earlier row in walk order owns them.
        Assert.Equal(PauseScreens.ResumeRow, PauseScreens.RowAt(sheet, 238f, 555f));
        Assert.Equal(PauseScreens.PhotoRow, PauseScreens.RowAt(sheet, 366f, 528f));

        // Off the group's far edges, and the clear board above the strips.
        Assert.Equal(-1, PauseScreens.RowAt(sheet, 106f, 528f));
        Assert.Equal(-1, PauseScreens.RowAt(sheet, 499f, 528f));
        Assert.Equal(-1, PauseScreens.RowAt(sheet, 107f, 556f));
        Assert.Equal(-1, PauseScreens.RowAt(sheet, 400f, 300f));
    }

    /// <summary>The remake's own strip has no authored point, so it takes the one the block leaves:
    /// the campaign block's two columns leave the channel between them, while the Instant Action
    /// block stands three across and leaves the cell under RESTART. Both wear RESUME's own plates
    /// and label offset, which is what makes the fifth strip read as one of the four.</summary>
    [ExtractedDataFact]
    public void ThePhotoStripTakesThePlaceItsOwnBlockLeavesFree()
    {
        var campaign = Sheet();
        var instantAction = PauseSheet.Load(
            Zrdr(), MessagesPath(), EscapeDialog.InstantActionKey(1, 'a'), instantAction: true)!;

        var onChart = campaign.Strips[PauseScreens.PhotoRow]!;
        var onBoard = instantAction.Strips[PauseScreens.PhotoRow]!;
        Assert.Equal(new BriefingPoint(237f, 528f), onChart.At);
        Assert.Equal(new BriefingPoint(497f, 550f), onBoard.At);
        Assert.Equal(
            ("escape_button1", "escape_button2", "escape_button3"),
            (onChart.Normal, onChart.Rollover, onChart.Activate));
        Assert.Equal(new BriefingPoint(66f, 7f), onChart.LabelOffset);
        Assert.Equal(PauseScreens.PhotoLabel, campaign.ButtonLabels[PauseScreens.PhotoRow]);

        // The authored block is untouched: the fifth strip is the sheet's, not the file's.
        Assert.Equal(4, campaign.Shared.Buttons.Count);
        Assert.Equal(5, campaign.Strips.Count);
    }

    /// <summary>Every campaign dialog authors the pointer the screen is driven with, and the
    /// composition draws it over everything else: the rollover bitmap on a strip, the plain one
    /// off it, and nothing at all where nobody is pointing.</summary>
    [ExtractedDataFact]
    public void TheDialogsOwnCursorFollowsThePointerAndWearsItsRolloverOnAStrip()
    {
        var sheet = Sheet();
        Assert.Equal(new EscapeCursor("daglove", "dafinger", false), sheet.State.Cursor);

        var away = PauseScreens.For(sheet, PauseReadout.Empty, 0, false, (400f, 300f));
        var onStrip = PauseScreens.For(sheet, PauseReadout.Empty, 0, false, (413f, 573f));
        var none = PauseScreens.For(sheet, PauseReadout.Empty, 0, false);

        var plain = Assert.Single(Assert.Single(away.Overlays).Pictures);
        Assert.Equal(("daglove", 400f, 300f, false), (plain.Art.Name, plain.X, plain.Y, plain.Centered));
        Assert.Equal("dafinger", Assert.Single(Assert.Single(onStrip.Overlays).Pictures).Art.Name);
        Assert.Empty(none.Overlays);
    }

    /// <summary>The parchment's rows are the objectives the script reveals, in reveal order, and a
    /// completed row takes the mark with its colour unchanged.</summary>
    [ExtractedDataFact]
    public void TheParchmentMarksTheCompletedRowAndLeavesItsWordsAlone()
    {
        var sheet = Sheet();
        var board = PauseScreens.For(sheet, Readout(sheet, marked: 0), 0, false);

        var note = Assert.Single(board.Notes);
        Assert.Equal(4, note.Entries.Count);
        Assert.StartsWith("1)", note.Entries[0]);

        // The box is the parchment's own solid paper from the list's corner, across and down, rather
        // than the authored WORDWRAP: the original stacks rows off the artwork, and a remake that
        // did would spill words over the bitmap's torn edges. A deeper list shrinks its face.
        Assert.Equal((580f, 50f, 190f, 240f, 5f), (note.X, note.Y, note.Width, note.Height, note.Spacing));
        Assert.True(note.Italic);
        Assert.True(note.Shrink);
        Assert.Equal("obj_check1", note.Mark!.Name);
        Assert.Equal(new[] { true, false, false, false }, note.Marked);

        // The mark rides the row's own origin, so the first row's mark sits on the list position.
        var marks = note.Marks((_, _) => 16f);
        var only = Assert.Single(marks);
        Assert.Equal((580f, 50f, true), (only.X, only.Y, only.Centered));
    }

    /// <summary>The two icons the chart places by world position, and what happens to one whose
    /// position is off the window.</summary>
    [ExtractedDataFact]
    public void AWorldPlacedIconDrawsOnlyWhereTheChartReaches()
    {
        var sheet = Sheet();
        var map = sheet.State.Map!;
        var inside = new PauseWorldIcon("singledev", (map.World.X0 + map.World.X1) / 2f, -5000f, 0f);
        var outside = new PauseWorldIcon("singledev", 50000f, -5000f, 0f);

        var shown = PauseScreens.For(
            sheet, new PauseReadout(System.Array.Empty<PauseObjective>(), string.Empty, new[] { inside }),
            0, false);
        var hidden = PauseScreens.For(
            sheet, new PauseReadout(System.Array.Empty<PauseObjective>(), string.Empty, new[] { outside }),
            0, false);

        Assert.Contains(shown.Pictures, p => p.Art.Name == "singledev" && p.Centered);
        Assert.DoesNotContain(hidden.Pictures, p => p.Art.Name == "singledev");
    }

    /// <summary>An Instant Action pause reads its own definition file, which carries the load
    /// screen's blackboard: three photographs, no map, no memento and no parchment.</summary>
    [ExtractedDataFact]
    public void AnInstantActionPauseDrawsTheBlackboardRatherThanAChart()
    {
        var sheet = PauseSheet.Load(
            Zrdr(), MessagesPath(), EscapeDialog.InstantActionKey(1, 'a'), instantAction: true)!;
        var board = PauseScreens.For(sheet, PauseReadout.Empty, 0, false);

        Assert.Null(sheet.State.Map);
        Assert.Equal(string.Empty, sheet.State.MementoBitmap);
        Assert.Equal("loadframempt2", sheet.State.Background);
        Assert.Contains(board.Pictures, p => p.Art.Name == "MP-shotdown");
        Assert.DoesNotContain(board.Pictures, p => p.Art.Name.EndsWith("MAP"));
        Assert.Empty(board.Notes);
        Assert.Equal(5, board.Plaques.Count);
    }

    /// <summary>A sheet is run out rather than played: `CM02`'s script places its four pins past an
    /// authored <c>Wait</c>, which a reveal nobody advances never releases, so a sheet left unsettled
    /// would draw that chart with no flags on it at all.</summary>
    [ExtractedDataFact]
    public void ASheetIsSettledPastEveryAuthoredWait()
    {
        var waited = PauseSheet.Load(
            Zrdr(), MessagesPath(), EscapeDialog.CampaignKey(6, 5), instantAction: false)!;

        Assert.Contains(waited.State.Steps, s => s.Op == BriefingOp.Wait);
        var pins = waited.Reveal.Elements.Where(e => e.Id.StartsWith("OBJPIN")).ToList();
        Assert.Equal(4, pins.Count);
        Assert.All(pins, p => Assert.True(p.Visible && p.Bitmap.Length > 0));
        Assert.True(waited.Reveal.Complete);
    }

    /// <summary>An unreadable extraction leaves the sheet unbuilt rather than throwing out of a
    /// board that has to appear over a running mission.</summary>
    [Fact]
    public void AMissingExtractionBuildsNoSheetRatherThanThrowing()
    {
        Assert.Null(PauseSheet.Load("no-zrdr", "no-messages", "loading_c61", instantAction: false));
    }

    private static EscapeMap Map(EscapeRect clip, EscapeRect world) =>
        new("HA-m1MAP", new BriefingPoint(16f, 19f), clip, world);

    private static EscapeDialog Dialog() => EscapeDialog.Load(Zrdr(), EscapeDialog.CampaignFile);

    private static PauseSheet Sheet() => PauseSheet.Load(
        Zrdr(), MessagesPath(),
        EscapeDialog.CampaignKey(FilmedCampaign, FilmedMission), instantAction: false)!;

    // The mission's own objectives in the order the dialog's script indexes them, with one row
    // marked so the check's placement is exercised.
    private static PauseReadout Readout(PauseSheet sheet, int marked)
    {
        var objectives = BriefingObjectives.Load(
            Mech3.Zrdr.LoadFile(
                SessionPaths.MissionZrdr(TestData.DataRoot!, "C3", "M01"), "objectives.json"),
            Mech3.Messages.Load(MessagesPath()));
        var rows = new List<PauseObjective>();
        for (int i = 0; i < objectives.Count; i++)
        {
            rows.Add(new PauseObjective(objectives[i].Text, i == marked));
        }

        return new PauseReadout(rows, "ms_p_initialpinup1", System.Array.Empty<PauseWorldIcon>());
    }

    private static string Zrdr() =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string MessagesPath() =>
        Path.Combine(TestData.ExtractedRoot!, "messages.json");
}
