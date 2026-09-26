using System.IO;
using System.Linq;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The composed campaign board off engine: how the authored 800x600 dialog meets an arbitrary
/// window, which rows become the original's own button plaques, and what focus and a press look
/// like on one. These are the rules a screenshot can only show one instance of.
/// </summary>
public class ComposedBoardTests
{
    // Ninety characters, which the stand-in measurer below wraps to three lines. The campaign's own
    // longest objective (MSG_BRF_NWM3_OBJ4) is 98.
    private const string Long =
        "4) Cripple the CCCP zeppelin, or set fire to the tanker to prevent them from pursuing it.x";


    /// <summary>The scaling decision, which every later screen inherits: one uniform scale on both
    /// axes, the board centred, the remainder letterboxed (docs/org/campaign-board.md).</summary>
    [Fact]
    public void TheBoardFitsUniformlyAndCentres()
    {
        var wide = BoardFit.For(1280f, 720f);

        Assert.Equal(1.2f, wide.Scale, 3);
        Assert.Equal(160f, wide.OriginX, 3);
        Assert.Equal(0f, wide.OriginY, 3);
        Assert.Equal(160f, wide.X(0f), 3);
        Assert.Equal(1120f, wide.X(800f), 3);
        Assert.Equal(720f, wide.Y(600f), 3);
    }

    /// <summary>A tall window letterboxes the other way round, and a viewport with no area at all
    /// falls back to 1:1 rather than a scale nothing can draw at.</summary>
    [Fact]
    public void TheBoardLetterboxesTheOtherAxisAndSurvivesAnEmptyViewport()
    {
        var tall = BoardFit.For(800f, 1200f);

        Assert.Equal(1f, tall.Scale, 3);
        Assert.Equal(0f, tall.OriginX, 3);
        Assert.Equal(300f, tall.OriginY, 3);
        Assert.Equal(1f, BoardFit.For(0f, 0f).Scale, 3);
    }

    /// <summary>The four-frame strips ink the state into the art; a one-frame plaque keeps frame 0
    /// and changes its label's face instead, which is the briefing's three-font triad.</summary>
    [Fact]
    public void APlaqueTakesItsStateFromItsStripOrItsLabel()
    {
        Assert.Equal(1, ComposedBoard.PlaqueFrame(4, focused: false, pressed: false));
        Assert.Equal(2, ComposedBoard.PlaqueFrame(4, focused: true, pressed: false));
        Assert.Equal(3, ComposedBoard.PlaqueFrame(4, focused: true, pressed: true));
        Assert.Equal(0, ComposedBoard.PlaqueFrame(1, focused: true, pressed: true));

        Assert.Equal(BoardInk.LabelNormal, ComposedBoard.PlaqueInk(focused: false, pressed: false));
        Assert.Equal(BoardInk.LabelRollover, ComposedBoard.PlaqueInk(focused: true, pressed: false));
        Assert.Equal(BoardInk.LabelActivate, ComposedBoard.PlaqueInk(focused: true, pressed: true));

        // The messagebox's buttons never take the screen's palette: white on the two dark frames,
        // black on the light depressed one.
        Assert.Equal(BoardInk.Dialog, ComposedBoard.DialogInk(pressed: false));
        Assert.Equal(BoardInk.DialogPressed, ComposedBoard.DialogInk(pressed: true));
    }

    /// <summary>Every cabin row is one of the screen's five authored buttons, at the layout's own
    /// coordinates, and none of them lands as a text row.</summary>
    [Fact]
    public void TheCabinIsFivePlaquesAndNoList()
    {
        var flow = Opened();

        var board = CampaignBoards.For(flow.Page, 0);

        Assert.Equal(5, board.Plaques.Count);
        Assert.Empty(board.Lines);
        var next = board.Plaques.Single(p => p.Row == 0);
        Assert.Equal(87f, next.X);
        Assert.Equal(504f, next.Y);
        Assert.Equal(593f, board.Plaques.Single(p => p.Row == 3).X);
        Assert.Equal(259f, board.Plaques.Single(p => p.Row == 4).X);
    }

    /// <summary>What a pad press changes: the plaque under the cursor is the only one in its
    /// rollover frame, and the confirm the shell holds for a few frames depresses that one.</summary>
    [Fact]
    public void FocusAndAPressMoveBetweenPlaques()
    {
        var flow = Opened();

        var opening = CampaignBoards.For(flow.Page, flow.Row);
        flow.Move(1);
        var moved = CampaignBoards.For(flow.Page, flow.Row);
        var held = CampaignBoards.For(flow.Page, flow.Row, pressed: true);

        Assert.Equal(2, opening.Plaques.Single(p => p.Row == 0).Frame);
        Assert.Equal(1, opening.Plaques.Single(p => p.Row == 1).Frame);
        Assert.Equal(1, moved.Plaques.Single(p => p.Row == 0).Frame);
        Assert.Equal(2, moved.Plaques.Single(p => p.Row == 1).Frame);
        Assert.Equal(3, held.Plaques.Single(p => p.Row == 1).Frame);
    }

    /// <summary>The briefing's three plaques are the dialog's own BUTTONS entries, sharing one
    /// bitmap at 197/397/597 with the row's words drawn over it.</summary>
    [Fact]
    public void TheBriefingCarriesTheDialogsThreeLabelledPlaques()
    {
        var flow = Opened();
        flow.GoTo(CampaignScreen.Briefing);

        var board = CampaignBoards.For(flow.Page, 0);

        Assert.Equal(3, board.Plaques.Count);
        Assert.Equal(new[] { 197f, 397f, 597f }, board.Plaques.Select(p => p.X));
        Assert.All(board.Plaques, p => Assert.Equal(560f, p.Y));
        Assert.All(board.Plaques, p => Assert.NotEqual(string.Empty, p.Label));
        Assert.All(board.Plaques, p => Assert.Equal(0, p.Frame));
    }

    /// <summary>The list rows walk each screen's own text widgets: the flight check's two crew
    /// headings, at the PILOT and WINGMAN slots.</summary>
    [Fact]
    public void TheFlightCheckListsDownItsAuthoredWidgets()
    {
        Assert.Equal((138f, 102f, 400f), CampaignBoards.TextSlot(CampaignScreen.FlightCheck, 0));
        Assert.Equal((138f, 320f, 400f), CampaignBoards.TextSlot(CampaignScreen.FlightCheck, 1));
    }

    /// <summary>⚠ The flow rule BL-490 is about: a wrapped entry pushes the next one down by what
    /// it actually drew, so two long entries cannot land on the same authored slot. The measurer
    /// stands in for the renderer's font, one line per 30 characters at 13 px a line.</summary>
    [Fact]
    public void AWrappedNoteEntryPushesTheOneBelowItDown()
    {
        var note = CampaignBoards.ObjectivesNote(new[] { Long, "2) Short.", Long });

        var lines = note.Flow(Measured);

        Assert.Equal(new[] { 335f, 335f + 39f + 5f, 335f + 39f + 5f + 13f + 5f }, lines.Select(l => l.Y));
        Assert.All(lines, l => Assert.Equal(35f, l.X));
        Assert.All(lines, l => Assert.Equal(185f, l.Width));
    }

    /// <summary>The widget's authored 240 px is a box, not a suggestion: an entry that would draw
    /// past the parchment's bottom is dropped rather than written over the buttons.</summary>
    [Fact]
    public void ANoteStopsAtItsAuthoredHeight()
    {
        var entries = Enumerable.Repeat(Long, 8).ToArray();

        var lines = CampaignBoards.ObjectivesNote(entries).Flow(Measured);

        Assert.Equal(5, lines.Count);
        Assert.True(lines[^1].Y + 39f <= 335f + 240f);
    }

    // Thirty characters is one line, thirteen pixels tall, which is the shape of the real face in
    // the 185-wide column without needing one.
    private static float Measured(string text, float width) =>
        13f * ((text.Length + 29) / 30);

    private static CampaignFlow Opened()
    {
        var dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        return flow;
    }
}
