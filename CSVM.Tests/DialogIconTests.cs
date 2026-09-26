using System.Collections.Generic;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>Which of the messagebox sheet's three icons a dialog draws. <c>MESSAGEBOX.SCRIPT</c>
/// reads the frame off the raising screen's button mask in <c>@globals@OR.UR</c>: the <c>0x4</c>
/// and <c>0x8</c> masks take frame 0, every other mask frame 1, and a set <c>XR</c> frame 2. These
/// pin the frame each call site picks, which the composed pixels then follow.</summary>
public class DialogIconTests
{
    [Fact]
    public void TheThreeIconsStandInTheSheetsOwnStackingOrder()
    {
        Assert.Equal(0, (int)DialogIcon.Query);
        Assert.Equal(1, (int)DialogIcon.Warning);
        Assert.Equal(2, (int)DialogIcon.Death);
    }

    /// <summary>The reading this item was written against: a one-button box can take either frame
    /// and so can a two-button one, the original's <c>0x1</c> and <c>0x2</c> masks both drawing the
    /// warning while <c>0x4</c> and <c>0x8</c> both draw the query. So the composer takes the frame
    /// from its caller and never counts the buttons it was handed.</summary>
    [Fact]
    public void TheButtonCountDecidesNothingAboutTheIcon()
    {
        var one = CampaignBoards.Dialog("Notice.", Buttons(CampaignBoards.DialogCenterKey), DialogIcon.Query);
        var two = CampaignBoards.Dialog(
            "Confirm?",
            Buttons(CampaignBoards.DialogLeftKey, CampaignBoards.DialogRightKey),
            DialogIcon.Warning);

        Assert.Equal((int)DialogIcon.Query, IconFrame(one));
        Assert.Equal((int)DialogIcon.Warning, IconFrame(two));
    }

    [Fact]
    public void AModalCarriesItsIconThroughToThePicture()
    {
        foreach (var icon in new[] { DialogIcon.Query, DialogIcon.Warning, DialogIcon.Death })
        {
            var panel = CampaignBoards.Dialog(new CampaignModal("Message.", "OK", null, icon));

            Assert.Equal((int)icon, IconFrame(panel));
        }
    }

    /// <summary>Both boxes the flow raises are the plane screen's <c>0x1</c> masks, langui 710 and
    /// 702, so a modal takes the warning where the frameless composer drew the query.</summary>
    [Fact]
    public void AFlowRaisedModalTakesTheWarning()
    {
        var flow = CampaignFlowTests.NewFlow(out _);
        flow.RaiseModal("Pilot and Wingman must fly different planes.");

        Assert.Equal(DialogIcon.Warning, flow.Modal!.Icon);
        Assert.Equal((int)DialogIcon.Warning, IconFrame(CampaignBoards.Dialog(flow.Modal)));
    }

    // The panel composes its background, then its icon, then one picture per button, so picture 1
    // is the icon whatever art the layout names for the row.
    internal static int IconFrame(BoardPanel panel) => panel.Pictures[1].Frame;

    private static IReadOnlyList<CampaignBoards.DialogButton> Buttons(params string[] keys)
    {
        var buttons = new List<CampaignBoards.DialogButton>(keys.Length);
        foreach (string key in keys)
        {
            buttons.Add(new CampaignBoards.DialogButton(key, "OK", 0, ComposedBoard.DialogInk(pressed: false)));
        }

        return buttons;
    }
}
