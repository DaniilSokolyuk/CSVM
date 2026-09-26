using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The input script a campaign screenshot aid's <c>--menu=</c> colon argument spells: the
/// grammar's counted verbs and button words, the bare step count every older command still means,
/// and the replay itself, which is what lets an aid leave a drop-down standing open.</summary>
public class CampaignAidScriptTests
{
    [Fact]
    public void ABareNumberIsStillThatManyStepsDown()
    {
        var presses = CampaignAidScript.Parse("3");

        Assert.Single(presses);
        Assert.Equal('d', presses[0].Verb);
        Assert.Equal(3, presses[0].Count);
        Assert.Equal(BoardButton.None, presses[0].Button);
    }

    [Fact]
    public void ADecimalCountKeepsTheWholeNumberTheFloatReadGave()
    {
        var presses = CampaignAidScript.Parse("2.75");

        Assert.Single(presses);
        Assert.Equal('d', presses[0].Verb);
        Assert.Equal(2, presses[0].Count);
    }

    [Fact]
    public void CountsBindToTheVerbTheyPrecede()
    {
        var presses = CampaignAidScript.Parse("4da");

        Assert.Equal(2, presses.Count);
        Assert.Equal('d', presses[0].Verb);
        Assert.Equal(4, presses[0].Count);
        Assert.Equal('a', presses[1].Verb);
        Assert.Equal(1, presses[1].Count);
    }

    [Fact]
    public void ASeparatorJoinsSegmentsWithoutChangingThem()
    {
        Assert.Equal(CampaignAidScript.Parse("4da"), CampaignAidScript.Parse("4d-a"));
    }

    [Fact]
    public void ExportNamesTheExportPlanePress()
    {
        var presses = CampaignAidScript.Parse(CampaignAidProfiles.ExportArgument);

        Assert.Single(presses);
        Assert.Equal(BoardButton.ExportPlane, presses[0].Button);
    }

    [Fact]
    public void AButtonIsAlsoNamedByItsOwnMemberName()
    {
        var presses = CampaignAidScript.Parse("2d-changeammo");

        Assert.Equal(2, presses.Count);
        Assert.Equal('d', presses[0].Verb);
        Assert.Equal(BoardButton.ChangeAmmo, presses[1].Button);
    }

    [Fact]
    public void AnArgumentNoVerbOrButtonReadsSpellsNoPressAtAll()
    {
        Assert.Empty(CampaignAidScript.Parse(string.Empty));
        Assert.Empty(CampaignAidScript.Parse("seat-plane"));
        Assert.Empty(CampaignAidScript.Parse("2dz"));
    }

    [Fact]
    public void TheSecondaryVerbParsesWhicheverPresentationIsRunning()
    {
        var presses = CampaignAidScript.Parse("2d-x");

        Assert.Equal(2, presses.Count);
        Assert.Equal('d', presses[0].Verb);
        Assert.Equal('x', presses[1].Verb);
    }

    [Fact]
    public void APresentationWithoutTheSecondaryPressRefusesTheWholeScript()
    {
        Assert.Null(CampaignAidScript.Presses("2d-x", CampaignAidScript.VerbsWithoutSecondary, "Original's controls"));
    }

    [Fact]
    public void EveryOtherVerbStillReplaysWithoutASecondaryPress()
    {
        const string script = "3d1u-2l-r-a-b-export";

        Assert.Equal(
            CampaignAidScript.Parse(script),
            CampaignAidScript.Presses(script, CampaignAidScript.VerbsWithoutSecondary, "Original's controls"));
    }

    [Fact]
    public void APadWithASecondaryPressSpellsEveryVerbTheGrammarHas()
    {
        Assert.Equal(
            CampaignAidScript.Parse("2d-x"),
            CampaignAidScript.Presses("2d-x", CampaignAidScript.Verbs, "Built-in's pad"));
    }

    [Fact]
    public void TheSecondaryVerbPressesBuiltInsOwnShortcut()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Flown(0));
        CampaignProgression.Record(profile, Flown(1));
        var flow = PreviousMissions(profile);

        Assert.True(CampaignAidScript.Replay(flow, "2d-x"));

        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(1, flow.MissionSeq); // the row the cursor stood on, viewed without walking to the button
    }

    [Fact]
    public void ReplayStepsTheCursorTheWayTheStepCountDid()
    {
        var flow = Cabin();

        CampaignAidScript.Replay(flow, "3");

        Assert.Equal(3, flow.Row);
    }

    [Fact]
    public void ReplayReadsEveryCursorVerb()
    {
        var flow = Cabin();

        CampaignAidScript.Replay(flow, "3d1u");

        Assert.Equal(2, flow.Row);
    }

    [Fact]
    public void ReplayPressesANamedButtonWhereverItsRowIs()
    {
        var flow = Cabin();

        CampaignAidScript.Replay(flow, "previousmissions");

        Assert.Equal(CampaignScreen.PreviousMissions, flow.Screen);
    }

    [Fact]
    public void AScreenWithoutTheNamedButtonIsLeftAlone()
    {
        var flow = Cabin();

        CampaignAidScript.Replay(flow, CampaignAidProfiles.ExportArgument);

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal(0, flow.Row);
    }

    [Fact]
    public void AConfirmLeavesTheListUnderTheCursorStandingOpen()
    {
        var flow = Ammo();

        CampaignAidScript.Replay(flow, "a");

        Assert.NotNull(flow.OpenCombo);
    }

    [Fact]
    public void StepsAndAConfirmOpenAPylonsRocketList()
    {
        var flow = Ammo();

        CampaignAidScript.Replay(flow, "4da");

        Assert.Equal(4, flow.Row);
        Assert.NotNull(flow.OpenCombo);
    }

    [Fact]
    public void AConfirmOpensThePlaneSelectionScreensOwnList()
    {
        string root = TestData.TempDir();
        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        var flow = new CampaignFlow(store, UiStrings.Empty);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Blue Streak", Airframe = 3 });
        store.Save(profile);
        flow.SelectProfile(profile);
        flow.SetPlaneSlot(0);
        flow.GoTo(CampaignScreen.PlaneSelection);

        CampaignAidScript.Replay(flow, "a");

        Assert.NotNull(flow.OpenCombo);
    }

    // The ammo screen over a built plane with a gun and four hardpoints, so both the gun groups and
    // the pylons have a list with something to choose between.
    private static CampaignFlow Ammo()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var planes = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "Planes"));
        var built = new CustomPlaneDef { Name = "Built1", Airframe = 5, LeftHardpoints = 1, RightHardpoints = 3 };
        built.Guns[0] = new GunChoice(2, Twin: false);
        planes.Save(built);

        var stock = StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty, null, planes, stock);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Built1", Airframe = 5 });
        profile.SelectedPlane = 0;
        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);
        flow.GoTo(CampaignScreen.Ammo);
        return flow;
    }

    private static MissionAttempt Flown(int seq) =>
        new(seq, CompletedMask: 1, TimeMs: 40000, Shots: 10, Hits: 5, Airframe: 5, PlaneName: "Gypsy Magic");

    // The contents screen over a profile with flights in it, the one campaign screen whose secondary
    // press does something: it views the mission the cursor stands on.
    private static CampaignFlow PreviousMissions(CampaignProfileDef profile)
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.SelectProfile(profile);
        flow.GoTo(CampaignScreen.PreviousMissions);
        return flow;
    }

    private static CampaignFlow Cabin()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        return flow;
    }
}
