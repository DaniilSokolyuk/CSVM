using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The dialog a campaign screen raises: it takes every press while it stands, the screen
/// under it is untouched, both answers run what was to follow, and the board draws it over
/// everything else.</summary>
public class CampaignModalTests
{
    [Fact]
    public void AScreenWithNoDialogHasNone()
    {
        var flow = CampaignFlowTests.NewFlow(out _);

        Assert.Null(flow.Modal);
    }

    [Fact]
    public void ARaisedDialogSwallowsEveryPressButTheAnswer()
    {
        var flow = Raised(out _, out int row);

        Assert.False(flow.Move(1));
        Assert.False(flow.Step(1));
        Assert.False(flow.Secondary());
        Assert.Equal(row, flow.Row);
        Assert.NotNull(flow.Modal);
    }

    [Fact]
    public void TheConfirmAnswersItAndRunsWhatFollows()
    {
        int ran = 0;
        var flow = CampaignFlowTests.NewFlow(out _);
        flow.RaiseModal("Pilot and Wingman must fly different planes.", confirmed: () => ran++);

        Assert.True(flow.Accept());
        Assert.Null(flow.Modal);
        Assert.Equal(1, ran);
    }

    /// <summary>Back answers a one-button dialog too, and takes the same door: a callback that ran
    /// on one press and not the other would make what happens next depend on which was used.</summary>
    [Fact]
    public void BackAnswersItTheSameWay()
    {
        int ran = 0;
        var flow = CampaignFlowTests.NewFlow(out _);
        var screen = flow.Screen;
        flow.RaiseModal("Exported.", confirmed: () => ran++);

        Assert.True(flow.Back());
        Assert.Null(flow.Modal);
        Assert.Equal(1, ran);
        Assert.Equal(screen, flow.Screen); // and it did not also leave the screen
    }

    [Fact]
    public void ASecondRaiseReplacesTheFirstRatherThanStacking()
    {
        var flow = CampaignFlowTests.NewFlow(out _);
        flow.RaiseModal("First");
        flow.RaiseModal("Second");

        Assert.Equal("Second", flow.Modal?.Message);
        flow.Accept();
        Assert.Null(flow.Modal);
    }

    [Fact]
    public void TheBoardDrawsItOverEverythingTheScreenCarries()
    {
        var flow = Raised(out var modal, out _);

        var board = CampaignBoards.For(flow.Page, flow.Row, modal: flow.Modal);

        var panel = Assert.Single(board.Overlays);
        Assert.Contains(panel.Lines, line => line.Text == modal.Message);
        Assert.Contains(panel.Lines, line => line.Text == modal.Button);
        Assert.Contains(panel.Pictures, art => art.Art.Name == "MB_Background.png");
    }

    [Fact]
    public void AScreenWithNoDialogComposesNoOverlay()
    {
        var flow = CampaignFlowTests.NewFlow(out _);

        Assert.Empty(CampaignBoards.For(flow.Page, flow.Row).Overlays);
    }

    // A flow on the cabin, one row along, with a dialog standing over it: the screen is incidental,
    // since the dialog is the flow's rather than any one page's.
    private static CampaignFlow Raised(out CampaignModal modal, out int row)
    {
        var flow = CampaignFlowTests.NewFlow(out _);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        flow.Move(1);
        row = flow.Row;
        flow.RaiseModal("Pilot and Wingman must fly different planes.");
        modal = flow.Modal!;
        return flow;
    }
}
