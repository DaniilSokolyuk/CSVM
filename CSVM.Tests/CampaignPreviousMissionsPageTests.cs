using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The scrapbook's table of contents: the career row, then one 80-pixel row per completed
/// mission in <c>seq</c> order with its icon and three lines, a four-row window with a scrollbar
/// past that, VIEW SELECTED and the secondary press both opening the book, the forward tab turning
/// into it, REPLAY MISSION opening the briefing without advancing the campaign, and RETURN TO CABIN
/// never stacking a second cabin.</summary>
public class CampaignPreviousMissionsPageTests
{
    /// <summary>The list is the campaign's own position: <c>uiData</c> 2409 counts its rows off
    /// that alone, so the missions below it are listed in story order whatever the profile's
    /// records hold.</summary>
    [Fact]
    public void ListsEveryMissionBelowTheCampaignPositionInSeqOrder()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        CampaignProgression.Record(profile, Attempt(2, mask: 4)); // no primary: the position holds
        var flow = OpenedOnPreviousMissions(profile);

        // The career row and two missions below the position, then VIEW SELECTED, REPLAY MISSION,
        // the forward page tab, the CURRENT MISSION bookmark and RETURN TO CABIN.
        Assert.Equal(2, profile.MissionsCompleted);
        Assert.Equal(8, flow.Page.RowCount);
        Assert.Equal(string.Empty, flow.Page.RowText(0)); // a list row draws its own three lines
        Assert.Equal("VIEW SELECTED", flow.Page.RowText(3));
        Assert.Equal("Starting My Career", flow.Page.Detail(0)); // langui 1217, the career row
        Assert.Equal("Mission 1", flow.Page.Detail(1)); // seq 0 (1-based ordinal 1) comes first
        Assert.Equal("Mission 2", flow.Page.Detail(2)); // seq 1 comes second
    }

    /// <summary>The original's ordinal 0 is <c>SCRAPBOOK.CSV</c> slot 0, the not-yet-started
    /// career: it stands above the missions from a brand-new profile onwards, carries the card fan
    /// past the eleven airframes and names no mission, so REPLAY MISSION is not offered on it and a
    /// second confirm opens the book there instead.</summary>
    [Fact]
    public void AFreshProfileListsTheCareerRowAlone()
    {
        var flow = OpenedOnPreviousMissions(CampaignProfileDef.NewProfile("Zachary"));

        // The career row, then VIEW SELECTED, the forward tab, the bookmark and RETURN TO CABIN.
        Assert.Equal(5, flow.Page.RowCount);
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));
        Assert.Equal(11, flow.Page.Pictures[0].Frame); // the card fan, past the eleven airframes
        Assert.Equal(
            new[] { "Starting My Career", "Above the clouds", "Gypsy Magic" },
            new[] { flow.Page.Captions[2].Text, flow.Page.Captions[3].Text, flow.Page.Captions[4].Text });

        flow.Accept(); // picks it
        Assert.Equal("Selected. Confirm again to open it", flow.Page.Detail(0));
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission)); // never on the career row

        flow.Accept(); // and a second confirm opens the book there rather than replaying anything
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(-1, flow.MissionSeq); // slot 0, the page with no mission behind it
    }

    /// <summary>The career row is picked like any other, and picking it takes REPLAY MISSION away
    /// even on a profile whose missions offer it.</summary>
    [Fact]
    public void PickingTheCareerRowWithdrawsReplayMission()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnPreviousMissions(profile);

        Assert.NotEqual(-1, RowOf(flow.Page, BoardButton.ReplayMission)); // the flown mission's

        flow.FocusRow(0);
        flow.Accept();
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));
    }

    /// <summary>A position reached without flying still lists its missions, which is what the
    /// original's row count does: the rows draw with no record behind them, REPLAY MISSION is not
    /// offered on one whose record holds no time, and the book still opens there.</summary>
    [Fact]
    public void APositionReachedWithoutFlyingStillListsItsMissions()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.MissionsCompleted = 3;
        var flow = OpenedOnPreviousMissions(profile);

        // The career row, three mission rows, and the four buttons a profile with no timed record
        // carries.
        Assert.Equal(8, flow.Page.RowCount);
        Assert.Equal(-1, RowOf(flow.Page, BoardButton.ReplayMission));
        Assert.Equal("Mission 3", flow.Page.Detail(3));
        Assert.Equal(string.Empty, flow.Page.Captions[7].Text); // the plane line of a mission never flown
        Assert.Equal(0, flow.Page.Pictures[1].Frame); // and its icon, the record's zeroed airframe

        flow.FocusRow(1);
        Assert.True(flow.Secondary());
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
    }

    /// <summary>A row is the icon at the listbox's own X, then three lines 20 apart from 10 down
    /// the 80-pixel row: the mission's short name, its area and the plane that flew it.</summary>
    [Fact]
    public void ARowIsAnIconAndThreeLinesAtTheListboxsOwnGeometry()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);

        var icons = flow.Page.Pictures;
        Assert.Equal(3, icons.Count); // the career row and the two missions
        Assert.EndsWith("FC_PlaneIcons.png", icons[1].Art.Name);
        Assert.Equal(5, icons[1].Frame); // the airframe the recorded run flew
        Assert.Equal(422f, icons[1].X);
        Assert.Equal(220f, icons[1].Y); // one 80-pixel row below the career row's own 140
        Assert.Equal(300f, icons[2].Y);

        // The two header widgets, then three lines per row.
        var rows = flow.Page.Captions;
        Assert.Equal(11, rows.Count);
        Assert.Equal(520f, rows[5].X);
        Assert.Equal(new[] { 230f, 250f, 270f }, new[] { rows[5].Y, rows[6].Y, rows[7].Y });
        Assert.Equal("Gypsy Magic", rows[7].Text); // the plane, the row's third line
        Assert.Equal(310f, rows[8].Y); // the next row's first line
    }

    /// <summary>The picked row washes and outlines, the focused one takes the same outline over a
    /// fainter wash, and both are the list's own colours.</summary>
    [Fact]
    public void ThePickedAndFocusedRowsAreWashedAndOutlined()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);

        // Row 0 is focused and nothing is picked yet: one wash and one outline.
        var focused = flow.Page.Fills;
        Assert.Equal(2, focused.Count);
        Assert.Equal(140f, focused[0].Y);
        Assert.Equal(0x40 / 255f, focused[0].Opacity);
        Assert.True(focused[1].Border);
        Assert.Equal(0xdd, focused[1].R);

        flow.Accept(); // pick row 0
        Assert.Equal(0x80 / 255f, flow.Page.Fills[0].Opacity);
    }

    /// <summary>Past four rows the window scrolls to keep the cursor's row on screen and the
    /// scrollbar appears, its thumb landing flush at the bottom on the last row.</summary>
    [Fact]
    public void TheWindowScrollsAndTheScrollbarAppearsPastFourRows()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int seq = 0; seq < 6; seq++)
        {
            CampaignProgression.Record(profile, Attempt(seq));
        }

        var flow = OpenedOnPreviousMissions(profile);

        // Four icons plus the two arrows and the thumb, never more than the window holds.
        Assert.Equal(7, flow.Page.Pictures.Count);
        Assert.Equal("Starting My Career", flow.Page.Captions[2].Text); // still at the top of the list

        flow.FocusRow(6); // the last mission row
        Assert.Equal("Mission 3", flow.Page.Captions[2].Text); // seq 2 heads the window now
        Assert.Equal(150f, flow.Page.Captions[2].Y); // the window's first slot, wherever it stands

        var thumb = flow.Page.Pictures[6];
        Assert.Equal(730f, thumb.X);
        Assert.Equal(16f, thumb.Width);
        Assert.Equal(449f, thumb.Y + thumb.Height); // flush against the lower arrow
    }

    /// <summary>The list as the pointer's wheel and thumb see it, and what a scroll writes back:
    /// the window moves and the cursor comes with it only where it would otherwise leave.</summary>
    [Fact]
    public void ThePointerWindowScrollsTheListAndDragsTheCursorInsideIt()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int seq = 0; seq < 6; seq++)
        {
            CampaignProgression.Record(profile, Attempt(seq));
        }

        var flow = OpenedOnPreviousMissions(profile);
        var page = (CampaignPreviousMissionsPage)flow.Page;
        var window = page.PointerWindow;
        Assert.NotNull(window);
        Assert.Equal((7, 4, 0), (window!.Value.Count, window.Value.Rows, window.Value.Top));
        Assert.True(window.Value.Contains(window.Value.X + 1f, window.Value.Y + 1f));
        Assert.True(window.Value.OnThumb(window.Value.ThumbX + 1f, window.Value.ThumbY + 1f));

        page.ScrollTo(window.Value.TopAfterWheel(1));
        Assert.Equal(1, page.PointerWindow!.Value.Top);
        Assert.Equal("Mission 1", flow.Page.Captions[2].Text); // seq 0 heads the window now
        Assert.Equal(1, flow.Row); // row 0 left the window, so the cursor came with it

        page.ScrollTo(500);
        Assert.Equal(3, page.PointerWindow!.Value.Top); // the last window of four over seven rows
        Assert.Equal(3, flow.Row);
    }

    [Fact]
    public void AListThatFitsItsWindowShowsThePointerNoScrollbar()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnPreviousMissions(profile);

        Assert.Null(((CampaignPreviousMissionsPage)flow.Page).PointerWindow);
    }

    /// <summary>A second confirm on the row already picked is REPLAY MISSION's own press, so a
    /// mission is replayed without walking down to the button.</summary>
    [Fact]
    public void ASecondConfirmOnThePickedRowReplaysIt()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);

        flow.FocusRow(2); // the seq-1 row, past the career row
        flow.Accept(); // picks it
        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);

        flow.Accept(); // replays it
        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(1, flow.MissionSeq);
    }

    [Fact]
    public void ReplayMissionOpensTheBriefingOnTheSelectedSeqWithoutAdvancing()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);
        int before = profile.MissionsCompleted;

        flow.FocusRow(2); // the seq-1 row (second finished mission, under the career row)
        flow.Accept();    // select it
        flow.FocusRow(4); // REPLAY MISSION (3 list rows + VIEW SELECTED before it)
        flow.Accept();

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(1, flow.MissionSeq);
        Assert.Equal(before, profile.MissionsCompleted);
    }

    [Fact]
    public void ReplayMissionWithNothingPickedFallsBackToTheFirstFinishedMission()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnPreviousMissions(profile);

        flow.FocusRow(3); // REPLAY MISSION (2 list rows + VIEW SELECTED before it)
        flow.Accept();

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(0, flow.MissionSeq);
    }

    /// <summary>The forward page tab turns out of the contents and into the book at its first
    /// page, the career page, which is the page the book's own back arrow falls off to reach this
    /// screen.</summary>
    [Fact]
    public void TheForwardTabTurnsIntoTheBookAtItsFirstPage()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(2));
        var flow = OpenedOnPreviousMissions(profile);

        Press(flow, BoardButton.ScrapbookNext);

        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(-1, flow.MissionSeq); // the front of the book, not the finished mission
    }

    /// <summary>The secondary press is VIEW SELECTED without walking down to the button: on a
    /// mission row it picks that row and opens the book there.</summary>
    [Fact]
    public void TheSecondaryPressViewsTheMissionTheCursorStandsOn()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);

        flow.FocusRow(2); // the seq-1 row, never confirmed
        Assert.True(flow.Secondary());

        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(1, flow.MissionSeq);
    }

    /// <summary>With nobody seated the list has no rows at all, so the press is unhandled rather
    /// than opening the book on a page the list does not carry.</summary>
    [Fact]
    public void TheSecondaryPressIsNothingWithNoProfileSeated()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.GoTo(CampaignScreen.PreviousMissions);

        Assert.False(flow.Secondary());
        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);
    }

    /// <summary>VIEW SELECTED opens the book at the picked mission's first spread, which is the
    /// original's <c>uiData</c> 2405 mode 1 and the only way the table of contents reaches it.</summary>
    [Fact]
    public void ViewSelectedOpensTheBookAtThePickedMission()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = OpenedOnPreviousMissions(profile);

        flow.FocusRow(2); // the seq-1 row
        flow.Accept();    // pick it
        Press(flow, BoardButton.ViewMission);

        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(1, flow.MissionSeq);
    }

    /// <summary>The CURRENT MISSION bookmark opens the book at the campaign's own next mission,
    /// whatever the list's cursor is on.</summary>
    [Fact]
    public void TheBookmarkOpensTheBookAtTheCampaignsCurrentMission()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        var flow = OpenedOnPreviousMissions(profile);

        Press(flow, BoardButton.CurrentMission);

        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(1, flow.MissionSeq); // the win advanced the campaign past seq 0
    }

    /// <summary>RETURN TO CABIN lands on the cabin already on the stack (from
    /// <see cref="CampaignFlow.SelectProfile"/>) rather than pushing a second copy: backing out of
    /// it once more reaches the roster, the flow's own first screen, not a second cabin.</summary>
    [Fact]
    public void ReturnToCabinGoesBackWithoutStackingASecondCabin()
    {
        var flow = OpenedOnPreviousMissions(CampaignProfileDef.NewProfile("Zachary"));

        flow.FocusRow(flow.Page.RowCount - 1); // RETURN TO CABIN
        flow.Accept();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        flow.Back();
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
    }

    // The button rows shift as Replay comes and goes, so a test presses one by name.
    private static void Press(CampaignFlow flow, BoardButton button)
    {
        int row = RowOf(flow.Page, button);
        Assert.NotEqual(-1, row);
        flow.FocusRow(row);
        flow.Accept();
    }

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

    private static MissionAttempt Attempt(int seq, int mask = 1) =>
        new(seq, CompletedMask: mask, TimeMs: 40000, Shots: 10, Hits: 5,
            Airframe: 5, PlaneName: "Gypsy Magic");

    private static CampaignFlow OpenedOnPreviousMissions(CampaignProfileDef profile)
    {
        var dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.SelectProfile(profile);
        flow.GoTo(CampaignScreen.PreviousMissions);
        return flow;
    }
}
