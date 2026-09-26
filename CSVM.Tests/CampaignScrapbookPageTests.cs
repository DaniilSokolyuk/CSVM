using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The scrapbook's results page: opened on the mission a finished mission just flew,
/// drawing the results block and kill stamps off it, with the page/mission arrows and the Current
/// Mission bookmark browsing the rest of the book and REPLAY MISSION acting on whichever mission
/// is currently shown rather than a fixed one. Slot 0, the career page at the front of the book,
/// carries scraps alone.</summary>
public class CampaignScrapbookPageTests
{
    [Fact]
    public void DrawsTheFlownMissionsResultsAndStamps()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(
            0, CompletedMask: 1, TimeMs: 215000, Shots: 25, Hits: 4,
            Airframe: 5, PlaneName: "Gypsy Magic", Kills: KillsOf((8, 3))));
        var flow = OpenedOnScrapbook(profile, seq: 0);

        Assert.Contains(flow.Page.Captions, l => l.Text == "Mission Completed");
        Assert.Contains(flow.Page.Captions, l => l.Text == "03:35");
        Assert.Contains(flow.Page.Captions, l => l.Text == "16%");
        Assert.Contains(flow.Page.Captions, l => l.Text == "3"); // the one kill-stamp count
        Assert.Contains(flow.Page.Captions, l => l.Text == "Zachary - Mission 1"); // SB_T_NAMEANDAREA

        // The unselected tab under the card, the card itself, then the stamp.
        Assert.Equal(3, flow.Page.Pictures.Count);
        Assert.Equal("SB_B_Statcardtab.png", flow.Page.Pictures[0].Art.Name);
        Assert.Equal("SB_P_Card.png", flow.Page.Pictures[1].Art.Name);
        Assert.Equal(1, flow.Page.Pictures[1].Frame); // Most Recent's own card frame
        Assert.Equal(8, flow.Page.Pictures[2].Frame); // Kestrel, plain
    }

    /// <summary>The two tabs are the whole of the selection: the one showing is a plaque over the
    /// card, the other a picture under it, and switching moves the card's own frame with them. The
    /// merged half this completed mission carries reads Mission Completed under either tab.</summary>
    [Fact]
    public void TheTabsSwitchWhichHalfTheResultsBlockReads()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(
            0, CompletedMask: 1, TimeMs: 215000, Shots: 25, Hits: 4,
            Airframe: 5, PlaneName: "Gypsy Magic", Kills: KillsOf((8, 3))));
        var flow = OpenedOnScrapbook(profile, seq: 0);

        Assert.Equal(-1, RowOf(flow.Page, BoardButton.BestTab)); // unselected: a picture, not a plaque
        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.MostTab));
        Assert.Contains(flow.Page.Captions, l => l.Text == "Mission Completed");

        Press(flow, BoardButton.MostTab); // pressing the tab already showing changes nothing
        Assert.Contains(flow.Page.Captions, l => l.Text == "Mission Completed");

        flow.FocusRow(RowOf(flow.Page, BoardButton.MostTab) - 1); // the Best tab's own row
        flow.Accept();

        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.BestTab)); // now it is the plaque
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.MostTab));
        Assert.Equal(0, flow.Page.Pictures[1].Frame); // the card's Best to Date frame
        Assert.Contains(flow.Page.Captions, l => l.Text == "Mission Completed");
    }

    /// <summary>Every row the page offers names one thing a pointer can be aimed at: an authored
    /// button, the art the row draws itself with, or a scrap. The unselected tab is the only row of
    /// the middle kind, and the two tabs swap kinds when the selection moves, so no row on the page
    /// is left without a rectangle for a presentation to hit-test.</summary>
    [Fact]
    public void EveryRowNamesAButtonOrArtOrAScrapAndTheTabsSwapWhichKindTheyAre()
    {
        string root = ScrapbookCompositionFixture.WriteMinimalOpenableScrap(TestData.TempDir(), mission: 1);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);
        var page = (CampaignScrapbookPage)flow.Page;

        for (int row = 0; row < page.RowCount; row++)
        {
            int named = (page.Button(row).Button != BoardButton.None ? 1 : 0)
                + (page.ArtOf(row) != null ? 1 : 0) + (page.ScrapOf(row) != null ? 1 : 0);
            Assert.Equal(1, named);
        }

        int best = RowOf(page, BoardButton.MostTab) - 1; // Best to Date, the tab under the card
        Assert.Equal("SB_B_Statcardtab.png", page.ArtOf(best)?.Art.Name);
        Assert.Null(page.ArtOf(best + 1));

        flow.FocusRow(best);
        flow.Accept();

        Assert.Null(page.ArtOf(best));
        Assert.Equal("SB_B_Statcardtab.png", page.ArtOf(best + 1)?.Art.Name);
    }

    /// <summary>Replay Mission is offered only where <c>uiData</c> 2411 offers it: a mission whose
    /// record holds a time, and only on the results page.</summary>
    [Fact]
    public void ReplayMissionIsOfferedOnlyOnAFlownMissionsResultsPage()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.ReplayMission));

        StepNext(flow); // (1,1) -> (1,2), a story page: no results, no Replay
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));

        StepNext(flow); // (1,2) -> (2,1), a results page for a mission never flown
        Assert.Contains(flow.Page.Captions, l => l.Text == "Not yet flown");
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));
    }

    /// <summary>A lost attempt still counts: the gate is a recorded time, not a completion bit, so
    /// a mission failed once is replayable from its own page.</summary>
    [Fact]
    public void ALostAttemptStillOffersReplayMission()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(
            0, CompletedMask: 0, TimeMs: 40000, Shots: 10, Hits: 5,
            Airframe: 5, PlaneName: "Gypsy Magic"));
        var flow = OpenedOnScrapbook(profile, seq: 0);

        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.ReplayMission));
    }

    /// <summary><c>SCRAPBOOK.CSV</c> slot 0 is the career page: its scraps draw, the results card,
    /// its two tabs and REPLAY MISSION do not, and the title is langui 1216 over the player's name
    /// alone, there being no mission to name.</summary>
    [Fact]
    public void TheCareerPageDrawsItsScrapsWithNoResultsCard()
    {
        string root = ScrapbookCompositionFixture.WriteCareerBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: -1, dataRoot: root);

        Assert.Single(flow.Page.Captions); // the title alone: no outcome line, block or stamp count
        Assert.Equal("Zachary - Scrapbook", flow.Page.Captions[0].Text);
        Assert.Equal(2, flow.Page.Pictures.Count); // the spread's two scraps, no tab and no card
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.BestTab));
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.MostTab));
        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.CurrentMission)); // never the current one
    }

    /// <summary>The career page is the front of the book: the back arrow off mission 1's results
    /// page lands on it, the forward arrow climbs out of it, and the back arrow there falls into
    /// the mission overview.</summary>
    [Fact]
    public void TheBackArrowOffMissionOneLandsOnTheCareerPage()
    {
        string root = ScrapbookCompositionFixture.WriteCareerBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepPrev(flow); // (1,1) -> (0,1)
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal("Zachary - Scrapbook", flow.Page.Captions[0].Text);

        StepNext(flow); // (0,1) -> (1,1), climbing out of the career page
        Assert.Equal("Zachary - Mission 1", flow.Page.Captions[0].Text);

        StepPrev(flow); // back onto it, where nothing stands before it
        StepPrev(flow);
        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);
    }

    /// <summary>VIEW ALL MISSIONS jumps to the mission overview, and so does the back arrow at the
    /// front of the book, which is where the original's own <c>sb_b_prev</c> falls.</summary>
    [Fact]
    public void ViewAllMissionsAndTheFrontOfTheBookBothReachTheMissionOverview()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0);

        Press(flow, BoardButton.ViewAllMissions);
        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);

        flow.OpenScrapbook(0);
        Press(flow, BoardButton.ScrapbookPrev); // mission 1, spread 1: nowhere back to turn
        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);
    }

    /// <summary>C17's own trap: after a win the campaign position advances past the flown mission,
    /// so REPLAY MISSION must not derive its target from the profile's progress.</summary>
    [Fact]
    public void ReplayMissionReEntersTheFlownMissionNotTheCurrentPosition()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0);
        Assert.Equal(1, CampaignProgression.NextMissionSeq(profile)); // the win advanced it

        Press(flow, BoardButton.ReplayMission);

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(0, flow.MissionSeq); // the mission just flown, not seq 1
    }

    [Fact]
    public void ReturnToCabinLandsOnTheCabinAlreadyOnTheStack()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0);

        Press(flow, BoardButton.ReturnToCabin);

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        flow.Back();
        Assert.Equal(CampaignScreen.Roster, flow.Screen); // no second cabin stacked underneath
    }

    /// <summary>A never-attempted mission draws no art (nothing to compose without a data root) but
    /// still names itself "Not yet flown" (D20) rather than drawing an empty results block.</summary>
    [Fact]
    public void ANeverAttemptedMissionShowsNotYetFlownRatherThanThrowing()
    {
        var flow = OpenedOnScrapbook(CampaignProfileDef.NewProfile("Zachary"), seq: 5);

        Assert.Contains(flow.Page.Captions, l => l.Text == "Not yet flown");
        Assert.Equal(2, flow.Page.Pictures.Count); // the unselected tab and the card, no stamps
    }

    /// <summary>An openable scrap on spread 1 gets its own row after the two buttons, and
    /// confirming it opens the detail view naming exactly that scrap.</summary>
    [Fact]
    public void AnOpenableScrapGetsARowThatOpensItsDetailView()
    {
        string root = ScrapbookCompositionFixture.WriteMinimalOpenableScrap(TestData.TempDir(), mission: 1);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        int scrapRow = flow.Page.RowCount - 1; // the one openable scrap, after every button
        flow.FocusRow(scrapRow);
        // No RESRC1.H in this fixture, so the row's symbol resolves to nothing and the hint band
        // says what a confirm does rather than printing the symbol or the image's own name.
        Assert.Equal("Look closer", flow.Page.Detail(scrapRow));
        flow.Accept();

        Assert.Equal(CampaignScreen.ScrapbookZoom, flow.Screen);
        Assert.Equal((1, 1, 1), flow.ZoomTarget);
    }

    /// <summary>A scrap row draws no list text of its own: its picture already stands at its
    /// authored position.</summary>
    [Fact]
    public void AScrapRowDrawsNoRowTextOfItsOwn()
    {
        string root = ScrapbookCompositionFixture.WriteMinimalOpenableScrap(TestData.TempDir(), mission: 1);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.Equal(string.Empty, flow.Page.RowText(flow.Page.RowCount - 1));
    }

    /// <summary>A scrap row draws no plaque and no list text, so the original's own hover is the
    /// whole of its focus state: the scrap under the cursor grows and lifts, and steps back the
    /// moment the cursor moves off it.</summary>
    [Fact]
    public void TheScrapUnderTheCursorGrowsAndSettlesBackWhenTheCursorLeaves()
    {
        string root = ScrapbookCompositionFixture.WriteMinimalOpenableScrap(TestData.TempDir(), mission: 1);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.Equal(1f, Scraps(flow.Page)[0].Scale); // the cursor opens on a button

        flow.FocusRow(flow.Page.RowCount - 1); // the one openable scrap
        Assert.Equal(1.02f, Scraps(flow.Page)[0].Scale);

        flow.FocusRow(0);
        Assert.Equal(1f, Scraps(flow.Page)[0].Scale);
    }

    /// <summary>The forward arrow steps within mission 1's two spreads, then rolls to mission 2's
    /// and mission 3's own spread 1, and offers no further forward step past the book's last
    /// page.</summary>
    [Fact]
    public void ArrowsStepForwardThenRollToTheNextMission()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2)
        Assert.EndsWith("01_02_a.PNG", flow.Page.Pictures[0].Art.Name);

        StepNext(flow); // (1,2) -> (2,1)
        Assert.EndsWith("02_01_a.PNG", flow.Page.Pictures[0].Art.Name);

        StepNext(flow); // (2,1) -> (3,1)
        Assert.EndsWith("03_01_a.PNG", flow.Page.Pictures[0].Art.Name);

        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ScrapbookNext)); // no page past the book's end
    }

    /// <summary>The back arrow steps within a mission's own spreads, then rolls into the previous
    /// mission's own last spread rather than its first, and offers no further back step at the
    /// front of the book.</summary>
    [Fact]
    public void ArrowsStepBackAndRollToThePreviousMissionsLastSpread()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2)
        StepNext(flow); // (1,2) -> (2,1)

        StepPrev(flow); // (2,1) -> (1,2), the previous mission's own last spread, not its first
        Assert.EndsWith("01_02_a.PNG", flow.Page.Pictures[0].Art.Name);
    }

    /// <summary>The book opens with the cursor already on RETURN TO CABIN: a mission-end scrapbook is
    /// read rather than operated, so the row waiting under the cursor is the way out.</summary>
    [Fact]
    public void OpensWithTheCursorOnReturnToCabin()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.Equal(BoardButton.ReturnToCabin, flow.Page.Button(flow.Row).Button);

        flow.Accept();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    /// <summary>Turning a page leaves the cursor on the arrow that turned it, not on the row index
    /// that arrow used to hold: a spread-1 page drops the Replay row and both tabs above the arrows,
    /// which slides an unmoved cursor down onto RETURN TO CABIN.</summary>
    [Fact]
    public void TurningAPageKeepsTheCursorOnTheArrowThatTurnedIt()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2), where the three spread-1 rows are gone
        Assert.Equal(BoardButton.ScrapbookNext, flow.Page.Button(flow.Row).Button);

        StepPrev(flow); // (1,2) -> (1,1), where they are back
        Assert.Equal(BoardButton.ScrapbookPrev, flow.Page.Button(flow.Row).Button);
    }

    /// <summary>The last page of the book offers no forward arrow to stay on, so the cursor lands on
    /// the back arrow beside it rather than on whatever row the index now names.</summary>
    [Fact]
    public void TurningToTheLastPageLeavesTheCursorOnTheBackArrow()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2)
        StepNext(flow); // (1,2) -> (2,1)
        StepNext(flow); // (2,1) -> (3,1), the book's last page

        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ScrapbookNext));
        Assert.Equal(BoardButton.ScrapbookPrev, flow.Page.Button(flow.Row).Button);
    }

    /// <summary>The Current Mission bookmark shows only while the browsed mission differs from the
    /// campaign's own current one, and jumps back to that mission's spread 1.</summary>
    [Fact]
    public void BookmarkAppearsOnlyAwayFromTheCurrentMissionAndJumpsBackToIt()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.Equal(-1, RowOf(flow.Page, BoardButton.CurrentMission)); // opened on the current mission

        StepNext(flow); // (1,1) -> (1,2), still mission 1
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.CurrentMission));

        StepNext(flow); // (1,2) -> (2,1), no longer the current mission
        int bookmarkRow = RowOf(flow.Page, BoardButton.CurrentMission);
        Assert.NotEqual(-1, bookmarkRow);

        flow.FocusRow(bookmarkRow);
        flow.Accept();
        Assert.EndsWith("01_01_a.PNG", flow.Page.Pictures[0].Art.Name); // back to mission 1, spread 1
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.CurrentMission));
    }

    /// <summary>REPLAY MISSION acts on whichever mission is browsed, not
    /// <see cref="CampaignFlow.MissionSeq"/>: pressing it after stepping away from the flown mission
    /// re-enters the mission the page is now showing.</summary>
    [Fact]
    public void ReplayMissionActsOnTheBrowsedMissionRatherThanMissionSeq()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1)); // so mission 2's own page offers Replay
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2)
        StepNext(flow); // (1,2) -> (2,1), mission slot 2 -- seq 1

        Press(flow, BoardButton.ReplayMission);

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(1, flow.MissionSeq); // the browsed mission, not the one the book opened on
    }

    /// <summary>A spread-1 view of a mission with no recorded attempt shows the "Not yet flown"
    /// placeholder rather than a results block or nothing at all.</summary>
    [Fact]
    public void AnUnflownMissionsResultsPageShowsNotYetFlown()
    {
        string root = ScrapbookCompositionFixture.WriteBook(TestData.TempDir());
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0)); // only mission 1 (seq 0) is flown
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        StepNext(flow); // (1,1) -> (1,2)
        StepNext(flow); // (1,2) -> (2,1), never flown

        Assert.Contains(flow.Page.Captions, l => l.Text == "Not yet flown");
    }

    /// <summary>The danger-zone slot (D21): the photo-corner mount always draws, but the capture it
    /// frames is skipped until the profile directory actually carries the file
    /// (<c>CampaignScrapbookPage.CaptureExists</c>, resolved against
    /// <c>CampaignProfileStore.DirFor</c>) -- exactly the gate <c>ScrapbookComposition</c> already
    /// applies, exercised here end to end through the page rather than the composition alone.</summary>
    [Fact]
    public void TheDangerZoneCaptureDrawsOnlyOnceItsFileExistsInTheProfileDirectory()
    {
        string root = ScrapbookCompositionFixture.WriteDangerZoneSpread(TestData.TempDir(), mission: 1);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnScrapbook(profile, seq: 0, dataRoot: root);

        Assert.Single(Scraps(flow.Page)); // just the corner mount, no capture on disk yet

        File.WriteAllBytes(Path.Combine(flow.Store.DirFor(profile.Name), "Snap_1_18.PNG"), new byte[] { 0 });
        var scraps = Scraps(flow.Page);
        Assert.Equal(3, scraps.Count); // the capture, its grime, then the mount painted over both
        Assert.EndsWith("Snap_1_18.PNG", scraps[0].Art.Name);
        Assert.Equal(BoardArtLibrary.Loose, scraps[0].Art.Library); // the profile directory, not the assets
        Assert.Equal("SB_P_Grime.Png", scraps[1].Art.Name);
        Assert.EndsWith("DZ_generic_corners.PNG", scraps[2].Art.Name);
    }

    // Presses whichever row currently carries the named arrow/bookmark button, failing loudly if
    // the layout ever stops offering it where a test expects one.
    private static void StepNext(CampaignFlow flow) => Press(flow, BoardButton.ScrapbookNext);

    private static void StepPrev(CampaignFlow flow) => Press(flow, BoardButton.ScrapbookPrev);

    private static void Press(CampaignFlow flow, BoardButton button)
    {
        int row = RowOf(flow.Page, button);
        Assert.NotEqual(-1, row);
        flow.FocusRow(row);
        flow.Accept();
    }

    // The spread's own scraps: on a results page the unselected tab and the card always follow
    // them in the picture list, so the first of those two ends the scraps.
    private static List<BoardPicture> Scraps(ICampaignPage page)
    {
        var scraps = new List<BoardPicture>();
        foreach (var picture in page.Pictures)
        {
            if (picture.Art.Name is "SB_B_Statcardtab.png" or "SB_P_Card.png")
            {
                break;
            }

            scraps.Add(picture);
        }

        return scraps;
    }

    // The row layout shifts as arrows/bookmark come and go, so tests locate a button by scanning
    // rather than assuming a fixed index.
    private static int RowOf(ICampaignPage page, BoardButton button)
    {
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.Button(row).Button == button)
            {
                return row;
            }
        }

        return -1;
    }

    private static MissionAttempt Attempt(int seq) =>
        new(seq, CompletedMask: 1, TimeMs: 40000, Shots: 10, Hits: 5,
            Airframe: 5, PlaneName: "Gypsy Magic");

    private static int[] KillsOf(params (int Index, int Count)[] kills)
    {
        var array = new int[CampaignProgression.AirframeCount];
        foreach (var (index, count) in kills)
        {
            array[index] = count;
        }

        return array;
    }

    private static CampaignFlow OpenedOnScrapbook(CampaignProfileDef profile, int seq, string? dataRoot = null)
    {
        var dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var store = new CampaignProfileStore(dir);
        store.Save(profile);
        var flow = new CampaignFlow(store, UiStrings.Empty, dataRoot);
        flow.SelectProfile(store.Load(profile.Name)!);
        flow.SetMission(seq);
        flow.GoTo(CampaignScreen.Scrapbook);
        return flow;
    }
}
