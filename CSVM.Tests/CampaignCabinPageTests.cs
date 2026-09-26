using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The cabin hub: NEXT MISSION routing to the profile's own next mission and refusing once
/// the campaign is finished, PREVIOUS MISSIONS and PLANE CONSTRUCTION handing the shell a screen or
/// a job, CHANGE MEMENTO opening the chooser over the picture the profile hangs, and RETURN TO MAIN
/// MENU cancelling the flow.</summary>
public class CampaignCabinPageTests
{
    [Fact]
    public void FiveButtonsInTheOriginalsOwnCreationOrder()
    {
        var flow = Opened(out _);

        Assert.Equal(5, flow.Page.RowCount);
        Assert.Equal("Next Mission", flow.Page.RowText(0));
        Assert.Equal("Previous Missions", flow.Page.RowText(1));
        Assert.Equal("Plane Construction", flow.Page.RowText(2));
        Assert.Equal("Return to Main Menu", flow.Page.RowText(3));
        Assert.Equal("Change Memento", flow.Page.RowText(4));
    }

    [Fact]
    public void ChangeMementoOpensTheChooserAndTheWallDrawsTheProfilesOwnPicture()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Memento = "MS_P_Mom.jpg";
        var flow = Opened(out _, profile);

        Assert.Contains(flow.Page.Pictures, p => p.Art.Name == "ms_p_mom");

        flow.FocusRow(4);
        flow.Accept();

        Assert.Equal(CampaignScreen.MementoSelection, flow.Screen);
    }

    [Fact]
    public void NextMissionSetsTheFreshProfilesFirstMissionAndOpensTheBriefing()
    {
        var flow = Opened(out _);

        flow.FocusRow(0);
        flow.Accept();

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(0, flow.MissionSeq);
    }

    [Fact]
    public void NextMissionAfterProgressOpensTheNextSeqNotTheFirst()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0));
        CampaignProgression.Record(profile, Attempt(1));
        var flow = Opened(out _, profile);

        flow.FocusRow(0);
        flow.Accept();

        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.Equal(2, flow.MissionSeq);
    }

    [Fact]
    public void NextMissionRefusesOnceAllTwentyFourAreComplete()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int seq = 0; seq < 24; seq++)
        {
            CampaignProgression.Record(profile, Attempt(seq));
        }

        var flow = Opened(out _, profile);
        flow.FocusRow(0);
        flow.Accept();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.NotEqual(string.Empty, flow.Message);
    }

    [Fact]
    public void PreviousMissionsOpensTheListScreen()
    {
        var flow = Opened(out _);

        flow.FocusRow(1);
        flow.Accept();

        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);
    }

    [Fact]
    public void PlaneConstructionRequestsTheHangarWithoutLeavingTheScreen()
    {
        var flow = Opened(out _);

        flow.FocusRow(2);
        flow.Accept();

        Assert.Equal(CampaignExit.OpenHangar, flow.Exit);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    [Fact]
    public void ReturnToMainMenuCancelsTheFlow()
    {
        var flow = Opened(out _);

        flow.FocusRow(3);
        flow.Accept();

        Assert.Equal(CampaignExit.Cancelled, flow.Exit);
    }

    /// <summary>The pin count is the story chapter of the current position: 1 for a fresh profile,
    /// rising every five missions, and 5 once the campaign is finished (act 5 is only four missions
    /// long, so seq 23's chapter is still 5 under integer division).</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(15, 4)]
    [InlineData(20, 5)]
    [InlineData(24, 5)]
    public void MapPinCountIsTheStoryChapterOfTheCurrentPosition(int missionsCompleted, int pins)
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.MissionsCompleted = missionsCompleted;

        Assert.Equal(pins, CampaignCabinPage.MapPinCount(profile));
    }

    private static MissionAttempt Attempt(int seq) =>
        new(seq, CompletedMask: 1, TimeMs: 40000, Shots: 10, Hits: 5,
            Airframe: 5, PlaneName: "Gypsy Magic");

    private static CampaignFlow Opened(out string dir, CampaignProfileDef? profile = null)
    {
        dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.SelectProfile(profile ?? CampaignProfileDef.NewProfile("Zachary"));
        return flow;
    }
}
