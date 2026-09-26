using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The campaign flow's navigation contract: a screen stack rather than a fixed order, an
/// unregistered screen drawing a placeholder rather than crashing, returning to an open screen
/// instead of stacking a second copy of it, and backing out of the first screen ending the flow.
/// </summary>
public class CampaignFlowTests
{
    [Fact]
    public void OpensOnTheRoster()
    {
        var flow = NewFlow(out _);

        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        Assert.Equal(CampaignExit.None, flow.Exit);
        Assert.IsType<CampaignRosterPage>(flow.Page);
    }

    /// <summary>The wave's mount point: every screen the flow knows has a page of its own, so the
    /// placeholder (which edits nothing) is never drawn for a shipped screen.</summary>
    [Fact]
    public void EveryScreenHasAPage()
    {
        var screens = new[]
        {
            CampaignScreen.Roster, CampaignScreen.Cabin, CampaignScreen.PreviousMissions,
            CampaignScreen.Briefing, CampaignScreen.FlightCheck, CampaignScreen.Ammo,
        };
        foreach (var screen in screens)
        {
            Assert.True(CampaignFlow.HasPage(screen), screen.ToString());
        }

        var flow = NewFlow(out _);
        flow.GoTo(CampaignScreen.Ammo);

        Assert.IsNotType<CampaignPlaceholderPage>(flow.Page);
        Assert.Equal(CampaignScreen.Ammo, flow.Page.Screen);
    }

    [Fact]
    public void BackLeavesAScreenAndCancelsFromTheFirstOne()
    {
        var flow = NewFlow(out _);
        flow.GoTo(CampaignScreen.Cabin);
        flow.GoTo(CampaignScreen.Briefing);

        flow.Back();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        flow.Back();
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        Assert.Equal(CampaignExit.None, flow.Exit);

        flow.Back();
        Assert.Equal(CampaignExit.Cancelled, flow.Exit);
    }

    /// <summary>RETURN TO CABIN from a briefing must land on the cabin that opened it, not on a
    /// second one stacked over it, or every return would deepen the stack by one.</summary>
    [Fact]
    public void ReturningToAnOpenScreenPopsBackToIt()
    {
        var flow = NewFlow(out _);
        flow.GoTo(CampaignScreen.Cabin);
        flow.GoTo(CampaignScreen.Briefing);

        flow.GoTo(CampaignScreen.Cabin);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);

        flow.Back();
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
    }

    internal static CampaignFlow NewFlow(out string dir)
    {
        dir = Path.Combine(TestData.TempDir(), "Profiles");
        return new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
    }
}
