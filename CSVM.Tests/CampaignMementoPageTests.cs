using System.IO;
using System.Linq;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The cabin's memento chooser: what a profile may choose between, the arrows stepping and
/// wrapping through it, ACCEPT writing the picture into the profile and CANCEL writing nothing.</summary>
public class CampaignMementoPageTests
{
    [Fact]
    public void AFreshProfileHoldsTheSevenTheCampaignStartsWith()
    {
        var page = Chooser(out _, out _);

        Assert.Equal(7, page.Choices);
        Assert.Equal(CampaignMementos.Seeded, page.Showing);
    }

    /// <summary>A mission's picture is held once that mission's merged mask carries the won bit and
    /// the row's own objective bit, so a win alone is not enough for one that names an objective.</summary>
    [Fact]
    public void AMissionsPictureIsHeldOnlyOnceItsObjectiveBitIsRecorded()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(1, 1));
        Assert.Equal(7, CampaignMementos.Awarded(profile).Count);

        CampaignProgression.Record(profile, Attempt(1, 1 | 2));

        Assert.Contains("MS_P_02_01_hawaiianPrinces2.jpg", CampaignMementos.Awarded(profile));
    }

    [Fact]
    public void TheArrowsStepThroughTheHeldPicturesAndWrapAtBothEnds()
    {
        var page = Chooser(out _, out _);
        var held = CampaignMementos.Awarded(CampaignProfileDef.NewProfile("Zachary")).ToArray();

        page.Accept(2);
        Assert.Equal(held[1], page.Showing);

        page.Accept(3);
        page.Accept(3);
        Assert.Equal(held[^1], page.Showing);

        page.Accept(2);
        Assert.Equal(held[0], page.Showing);
    }

    [Fact]
    public void AcceptWritesTheShownPictureIntoTheProfileAndReturnsToTheCabin()
    {
        var page = Chooser(out var flow, out var store);
        string second = CampaignMementos.Awarded(flow.Profile!)[1];

        page.Accept(2);
        page.Accept(0);

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal(second, flow.Profile!.Memento);
        Assert.Equal(second, store.Load("Zachary")!.Memento);
    }

    [Fact]
    public void CancelWritesNothingAndTheChooserOpensAgainOnTheCabinsOwnPicture()
    {
        var page = Chooser(out var flow, out var store);

        page.Accept(2);
        page.Accept(1);

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal(string.Empty, flow.Profile!.Memento);
        Assert.Equal(string.Empty, store.Load("Zachary")!.Memento);

        flow.GoTo(CampaignScreen.MementoSelection);

        Assert.Equal(CampaignMementos.Seeded, ((CampaignMementoPage)flow.Page).Showing);
    }

    /// <summary>A name the award table does not carry (an edited file) draws as the seeded pin-up
    /// rather than as a missing bitmap, which is the executable's own answer for an unknown name.</summary>
    [Fact]
    public void AnUnknownStoredNameFallsBackToTheSeededPinup()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Memento = "MS_P_NotAPicture.jpg";

        Assert.Equal(CampaignMementos.Seeded, CampaignMementos.Current(profile));
        Assert.Equal("ms_p_initialpinup1", CampaignMementos.Bitmap(CampaignMementos.Current(profile)));
    }

    /// <summary>A picture the profile has not been awarded is not written, so a chooser that never
    /// listed it cannot hang it either.</summary>
    [Fact]
    public void CommitRefusesAPictureTheProfileHasNotBeenAwarded()
    {
        Chooser(out var flow, out _);

        flow.Feature.CommitMemento("MS_P_24_01_theFinalKiss.jpg");

        Assert.Equal(string.Empty, flow.Profile!.Memento);
    }

    private static MissionAttempt Attempt(int seq, int mask) =>
        new(seq, mask, TimeMs: 40000, Shots: 10, Hits: 5, Airframe: 5, PlaneName: "Gypsy Magic");

    private static CampaignMementoPage Chooser(out CampaignFlow flow, out CampaignProfileStore store)
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        store = new CampaignProfileStore(dir);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        store.Save(profile);
        flow = new CampaignFlow(store, UiStrings.Empty);
        flow.SelectProfile(profile);
        flow.GoTo(CampaignScreen.MementoSelection);
        return (CampaignMementoPage)flow.Page;
    }
}
