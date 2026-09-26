using System.IO;
using System.Linq;
using CSVM;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The mission load screen off engine: which dialog a mission type reads, the four texts that
/// dialog places and where, and what a mode of ours takes instead. The authored side is
/// <c>Loading.zrd</c> and <c>messages.json</c>, so these read the extraction rather than restating
/// it (docs/org/loading-screen.md).
/// </summary>
public class LoadScreensTests
{
    /// <summary>The mission-type letter that completes the dialog name, the exe's own five-entry
    /// table. Free flight and dogfight are ours and resolve to none.</summary>
    [Fact]
    public void EachMissionTypeNamesItsOwnDialogAndOurModesNameNone()
    {
        Assert.Equal('a', LoadScreens.LetterFor("dogfight_ace"));
        Assert.Equal('d', LoadScreens.LetterFor("dogfight_squadron"));
        Assert.Equal('s', LoadScreens.LetterFor("stunt_flying"));
        Assert.Equal('z', LoadScreens.LetterFor("zeppelin_run"));
        Assert.Null(LoadScreens.LetterFor(null));
        Assert.Null(LoadScreens.LetterFor("free_flight"));
    }

    /// <summary>A mode of ours takes the heading alone at the mode heading's authored place: no
    /// shipped dialog describes free flight or a dogfight, and a borrowed blurb would state a win
    /// condition neither has.</summary>
    [Fact]
    public void AModeOfOursWritesItsHeadingAloneAtTheAuthoredPlace()
    {
        var lines = LoadScreens.Texts(null, "FREE FLIGHT", "no-zrdr", "no-messages");

        var only = Assert.Single(lines);
        Assert.Equal("FREE FLIGHT", only.Text);
        Assert.Equal((70f, 35f), (only.X, only.Y));
        Assert.Equal(0f, only.Width);
        Assert.False(only.Bold);
    }

    /// <summary>The stunt screen, the one the reference shot names: four texts at the dialog's own
    /// positions, the blurb and the win condition in the body face, and the mission type in the
    /// dialog's empty default face, which is the one weight the board draws twice.</summary>
    [ExtractedDataFact]
    public void TheStuntDialogPlacesItsFourTextsAtTheirAuthoredPositions()
    {
        var lines = Texts("stunt_flying");

        Assert.Equal(4, lines.Count);
        Assert.Equal(
            new[] { (70f, 35f), (325f, 35f), (360f, 135f), (360f, 215f) },
            lines.Select(l => (l.X, l.Y)));
        Assert.Equal(new[] { 0f, 400f, 400f, 0f }, lines.Select(l => l.Width));
        Assert.Equal("INSTANT ACTION", lines[0].Text);
        Assert.Equal("STUNT FLYING", lines[1].Text);
        Assert.StartsWith("Any knucklehead", lines[2].Text);
        Assert.Equal("Fly through all the Danger Zones to win!", lines[3].Text);
        Assert.Equal(new[] { false, true, false, false }, lines.Select(l => l.Bold));
        Assert.Equal(new[] { BoardInk.Heading, BoardInk.Heading, BoardInk.Row, BoardInk.Row },
            lines.Select(l => l.Ink));
    }

    /// <summary>The other three families write the same four widgets, and the ace duel's heading
    /// sits at its own 365 rather than the 325 every other family authors.</summary>
    [ExtractedDataFact]
    public void EveryFamilyWritesItsOwnHeadingBlurbAndWinCondition()
    {
        var squadron = Texts("dogfight_squadron");
        var zeppelin = Texts("zeppelin_run");
        var ace = Texts("dogfight_ace");

        Assert.Equal("SQUADRON", squadron[1].Text);
        Assert.Equal("Destroy all enemy fighters to win!", squadron[3].Text);
        Assert.Equal("ZEPPELIN RUN", zeppelin[1].Text);
        Assert.Equal("Destroy the zeppelin's engines to win!", zeppelin[3].Text);
        Assert.Equal("DOGFIGHT AN ACE.", ace[1].Text);
        Assert.Equal(365f, ace[1].X);
        Assert.Equal(325f, squadron[1].X);
        Assert.All(new[] { squadron, zeppelin, ace }, f => Assert.Equal("INSTANT ACTION", f[0].Text));
        Assert.All(new[] { squadron, zeppelin, ace }, f => Assert.False(f[1].Bold));
    }

    /// <summary>The blackboard carries the dialog's four texts and its three photographs; the
    /// campaign sheet writes no words of ours and takes its content from its own dialog.</summary>
    [ExtractedDataFact]
    public void TheBlackboardTakesItsDialogAndTheSheetTakesTheMissionsOwn()
    {
        var blackboard = LoadScreens.For(false, "unused", "zeppelin_run", Zrdr(), MessagesPath());
        var sheet = LoadScreens.For(true, "unused", null, Zrdr(), MessagesPath(), Filmed());

        Assert.Equal(4, blackboard.Lines.Count);
        Assert.Equal(6, blackboard.Pictures.Count);
        Assert.Empty(blackboard.Backdrop);
        Assert.Equal(new[] { "Objectives" }, sheet.Lines.Select(l => l.Text));
        Assert.Equal("loadframe", Assert.Single(sheet.Backdrop).Art.Name);
    }

    /// <summary>The filmed mission's sheet: its own chart at the crop the dialog authors, the
    /// parchment with the mission's four objectives unmarked, the memento over the shadow the
    /// script centres under it, the propeller's first frame and the unlit bar.</summary>
    [ExtractedDataFact]
    public void TheCampaignSheetDrawsTheMissionsChartParchmentMementoAndBar()
    {
        var board = LoadScreens.For(true, "unused", null, Zrdr(), MessagesPath(), Filmed());

        var chart = Picture(board, "HA-m1MAP");
        Assert.Equal(new BoardCrop(211, 51, 573, 500), chart.Crop);
        Assert.Equal((16f, 19f), (chart.X, chart.Y));
        var note = Assert.Single(board.Notes);
        Assert.Equal(4, note.Entries.Count);
        Assert.StartsWith("1)", note.Entries[0]);
        Assert.Null(note.Marked);
        Assert.Equal((580f, 50f), (note.X, note.Y));
        Assert.Equal((533f, 326f), At(board, "ms_p_initialpinup1"));
        Assert.Equal((661f, 454f), At(board, "momento_shad"));
        Assert.Equal((435f, 535f), At(board, "prp0"));
        Assert.Equal((90f, 548f), At(board, "prog_blkload"));
    }

    /// <summary>The pins the filmed mission's script places: the reference still's three
    /// question marks and one numbered four, each centred on its authored point, and the Pandora
    /// icon the loading script authors where the pause screen's own does not.</summary>
    [ExtractedDataFact]
    public void TheCampaignSheetPlacesEveryFlagAndTheMissionsDeviceIcon()
    {
        var board = LoadScreens.For(true, "unused", null, Zrdr(), MessagesPath(), Filmed());

        var flags = board.Pictures
            .Where(p => p.Art.Name.StartsWith("pin", System.StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            new[] { ("pin6", 506f, 149f), ("pin6", 396f, 254f), ("pin6", 181f, 244f), ("pin4", 106f, 319f) },
            flags.Select(p => (p.Art.Name, p.X, p.Y)));
        Assert.All(flags, p => Assert.True(p.Centered));
        Assert.Equal((106f, 419f), At(board, "NW-m1pda_icon"));
    }

    /// <summary>The load screen places neither world icon: its own constructor binds no
    /// <c>OWNSHIP</c> and no <c>MYZEP</c>, its script turns neither on, and the pause screen's
    /// constructor is the only one that does.</summary>
    [ExtractedDataFact]
    public void TheCampaignSheetPlacesNeitherOwnshipNorZeppelinIcon()
    {
        var sheet = Filmed();
        var board = LoadScreens.For(true, "unused", null, Zrdr(), MessagesPath(), sheet);

        Assert.Equal("singledev", sheet.Shared.OwnShip);
        Assert.Equal("NW-m1wv_icon", sheet.Shared.MyZep);
        Assert.Null(sheet.Reveal.Element("OWNSHIP"));
        Assert.Null(sheet.Reveal.Element("MYZEP"));
        Assert.DoesNotContain(sheet.Shared.OwnShip, board.Pictures.Select(p => p.Art.Name));
    }

    /// <summary>An extraction the sheet could not be read out of leaves the frame and the bar
    /// standing rather than throwing out of a screen shown while everything else loads.</summary>
    [Fact]
    public void AnUnreadableSheetKeepsTheFrameAndTheBar()
    {
        Assert.Null(LoadSheet.Load("no-zrdr", "no-messages", "no-mission", "loading_c61", "none"));

        var board = LoadScreens.For(true, "unused", null, "no-zrdr", "no-messages");

        Assert.Equal("loadframe", Assert.Single(board.Backdrop).Art.Name);
        Assert.Equal("prog_blkload", Assert.Single(board.Pictures).Art.Name);
        Assert.Empty(board.Lines);
        Assert.Empty(board.Notes);
    }

    /// <summary>An absent extraction leaves the board standing with no words rather than throwing
    /// out of a screen that exists to be shown while everything else is still loading.</summary>
    [Fact]
    public void AMissingExtractionWritesNothingRatherThanThrowing()
    {
        Assert.Empty(LoadScreens.Texts("stunt_flying", "unused", "no-zrdr", "no-messages"));
    }

    private static System.Collections.Generic.IReadOnlyList<BoardLine> Texts(string missionType) =>
        LoadScreens.Texts(missionType, "unused", Zrdr(), MessagesPath());

    // C3/M01, the mission the reference still frames, whose dialog key is loading_c61.
    private static LoadSheet Filmed() =>
        LoadSheet.Load(
            Zrdr(), MessagesPath(),
            SessionPaths.MissionZrdr(TestData.DataRoot!, "C3", "M01"),
            "loading_c61", "ms_p_initialpinup1")!;

    private static BoardPicture Picture(ComposedBoard board, string name) =>
        Assert.Single(board.Pictures.Where(p => p.Art.Name == name));

    private static (float X, float Y) At(ComposedBoard board, string name)
    {
        var picture = Picture(board, name);
        return (picture.X, picture.Y);
    }

    private static string Zrdr() =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string MessagesPath() =>
        Path.Combine(TestData.ExtractedRoot!, "messages.json");
}
