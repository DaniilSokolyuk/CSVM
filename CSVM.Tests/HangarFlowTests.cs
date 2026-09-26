using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The hangar shell's own engine-free surface: the screen order, back/next navigation, the
/// scratch plane's lifetime, and the commit that ends a flow. `LaunchMenu` is engine-bound and
/// untestable directly, so these are the facts standing behind the screens it draws, the same
/// role LaunchMenuWizardTests plays for the Instant Action wizard.
/// </summary>
public class HangarFlowTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-hangar-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The original's screen order (docs/org/hangar.md), plane selection first and the
    /// purchase review last.</summary>
    [Fact]
    public void ScreenOrderIsTheOriginals() =>
        Assert.Equal(
            new[]
            {
                HangarScreen.PlaneSelection, HangarScreen.Airframe, HangarScreen.Engine,
                HangarScreen.Armour, HangarScreen.Guns, HangarScreen.Hardpoints,
                HangarScreen.Paint, HangarScreen.Name, HangarScreen.Purchase,
            },
            HangarFlow.Order);

    [Fact]
    public void AFlowOpensOnPlaneSelection()
    {
        var flow = Open();
        Assert.Equal(HangarScreen.PlaneSelection, flow.Screen);
        Assert.Equal(HangarExit.None, flow.Exit);
    }

    /// <summary>Picking an airframe whose availability mask excludes the standing pattern snaps
    /// the paint job to the airframe's first available pattern with that pattern's own defaults:
    /// a fresh scratch carries pattern 0 (blackhat), which a Fury may not wear, and the paint
    /// screen must never open on a paint job the plane cannot wear.</summary>
    [Fact]
    public void PickingAnAirframeSnapsAnUnwearablePatternToItsFirstAvailable()
    {
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Accept(); // New Plane, on to Airframe
        flow.PickAirframe(7); // Fury: blackhat's mask excludes it; blckswan (1) is its first

        Assert.Equal(1, flow.Scratch.PaintPattern);
        Assert.Equal(new[] { 24, 24, 24 }, flow.Scratch.PaintColours);
    }

    /// <summary>An airframe that may wear the standing pattern keeps it, colours untouched.</summary>
    [Fact]
    public void PickingAnAirframeKeepsAWearablePattern()
    {
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Accept();
        flow.PickAirframe(4); // Brigand: blackhat's mask includes it

        Assert.Equal(0, flow.Scratch.PaintPattern);
    }

    /// <summary>Every screen's heading comes from its own langui id, not a literal.</summary>
    [Fact]
    public void EveryScreenTitlesItselfFromLangui()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1004,\"text\":\"Airframe\",\"dll\":\"langui\"}," +
            "{\"id\":1010,\"text\":\"Purchase\",\"dll\":\"langui\"}]");
        var flow = new HangarFlow(_store, strings);
        flow.Accept(); // New Plane, on to Airframe
        Assert.Equal("Airframe", flow.Page.Title);
        Walk(flow, HangarScreen.Purchase);
        Assert.Equal("Purchase", flow.Page.Title);
    }

    /// <summary>Confirm walks forward one screen at a time and stops on the last one, which is
    /// the commit's own screen rather than a tenth.</summary>
    [Fact]
    public void ConfirmWalksTheOrderAndStopsOnPurchase()
    {
        var flow = Open();
        foreach (var screen in HangarFlow.Order)
        {
            Assert.Equal(screen, flow.Screen);
            if (screen == HangarScreen.Purchase)
            {
                break;
            }

            flow.Accept();
            if (screen == HangarScreen.Airframe)
            {
                // E49: the airframe screen's first confirm picks, and the next one advances.
                flow.Accept();
            }
        }

        flow.Accept(); // the purchase page handles the press itself, so nothing advances past it
        Assert.Equal(HangarScreen.Purchase, flow.Screen);
    }

    [Fact]
    public void BackWalksTheOrderInReverse()
    {
        var flow = Open();
        Walk(flow, HangarScreen.Guns);
        flow.Back();
        Assert.Equal(HangarScreen.Armour, flow.Screen);
        flow.Back();
        Assert.Equal(HangarScreen.Engine, flow.Screen);
    }

    /// <summary>Backing out of the first screen cancels the flow.</summary>
    [Fact]
    public void BackOffTheFirstScreenCancels()
    {
        var flow = Open();
        flow.Back();
        Assert.Equal(HangarExit.Cancelled, flow.Exit);
    }

    /// <summary>Cancelling from a screen deep in the flow leaves no residue: the scratch plane is
    /// only ever in memory until the commit, so the store is untouched.</summary>
    [Fact]
    public void CancellingMidFlowWritesNothing()
    {
        var flow = Open();
        Walk(flow, HangarScreen.Name);
        flow.Scratch.Airframe = 3;
        flow.Step(1); // give it a name, so only the missing commit stops a save
        while (flow.Exit == HangarExit.None)
        {
            flow.Back();
        }

        Assert.Equal(HangarExit.Cancelled, flow.Exit);
        Assert.Null(flow.BuiltPlaneName);
        Assert.Empty(_store.List());
    }

    /// <summary>A completed flow saves the scratch plane and hands its name back, which is what
    /// D31's auto-select will key off.</summary>
    [Fact]
    public void CompletingTheFlowSavesTheScratchPlaneAndNamesIt()
    {
        var flow = Open();
        flow.Accept(); // New Plane
        flow.Scratch.Airframe = 0;
        flow.Scratch.Engine = 1;
        flow.Scratch.Name = "Test Hoplite";
        Walk(flow, HangarScreen.Purchase);
        Assert.True(flow.Commit());

        Assert.Equal(HangarExit.Built, flow.Exit);
        Assert.Equal("Test Hoplite", flow.BuiltPlaneName);
        var saved = Assert.Single(_store.List());
        Assert.Equal("Test Hoplite", saved.Name);
        Assert.Equal(1, saved.Engine);
    }

    /// <summary>The gate is HangarEconomy's verdict, in the original's own words, and it blocks
    /// the commit rather than saving a plane that cannot fly.</summary>
    [Fact]
    public void AnEnginelessBuildIsRefusedWithTheOriginalsWords()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Name = "Engineless";
        Walk(flow, HangarScreen.Purchase);

        Assert.False(flow.Commit());
        Assert.Contains("No Engine Selected", flow.Message, StringComparison.Ordinal);
        Assert.Empty(_store.List());
    }

    /// <summary>An overweight build is refused, a Balmoral loaded past its 15760 lb
    /// capacity (the figures HangarEconomyTests pins).</summary>
    [Fact]
    public void AnOverweightBuildIsRefused()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Name = "Overweight";
        flow.Scratch.Airframe = 2;
        flow.Scratch.Engine = 5;
        flow.Scratch.ArmourNose = 12;
        flow.Scratch.ArmourTail = 12;
        flow.Scratch.ArmourLeftWing = 12;
        flow.Scratch.ArmourRightWing = 12;
        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            flow.Scratch.Guns[slot] = new GunChoice(4, true);
        }

        flow.Scratch.LeftHardpoints = 4;
        flow.Scratch.RightHardpoints = 4;
        Walk(flow, HangarScreen.Purchase);

        Assert.False(flow.Commit());
        Assert.Contains("OVERWEIGHT", flow.Message, StringComparison.Ordinal);
        Assert.Empty(_store.List());
    }

    /// <summary>A nameless build is refused before the economy is consulted at all: the name is
    /// the store's identity, and the original has its own string for exactly this. The name screen
    /// rolls one onto every plane that lacks one, so the way here is a pilot who took theirs back
    /// off again; the gate stands whatever route reached it.</summary>
    [Fact]
    public void ANamelessBuildIsRefused()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Engine = 0;
        Walk(flow, HangarScreen.Purchase);
        flow.Scratch.Name = "   ";

        Assert.False(flow.Commit());
        Assert.Contains("name", flow.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.List());
    }

    /// <summary>Editing a saved plane starts from a copy: abandoning the edit must leave what is
    /// on disk exactly as it was.</summary>
    [Fact]
    public void EditingASavedPlaneWorksOnACopy()
    {
        _store.Save(new CustomPlaneDef { Name = "Saved One", Airframe = 4, Engine = 2 });
        var flow = Open();

        Assert.Equal(3, flow.Page.RowCount); // New Plane + the one saved plane + the delete row
        Assert.Equal("Saved One", flow.Page.RowText(1));
        flow.Move(1);
        flow.Accept();

        Assert.Null(flow.DefaultsAsk); // the ask meets a new plane, never a saved one (E41)
        Assert.Equal(4, flow.Scratch.Airframe);
        flow.Scratch.Airframe = 9;
        Assert.Equal(4, _store.Load("Saved One")!.Airframe);
    }

    /// <summary>The plane-selection screen's New Plane row seats a fresh scratch plane, so a
    /// second pass through the flow never inherits the first one's picks.</summary>
    [Fact]
    public void NewPlaneSeatsAFreshScratch()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Airframe = 7;
        while (flow.Screen != HangarScreen.PlaneSelection)
        {
            flow.Back();
        }

        flow.Accept();
        Assert.Equal(0, flow.Scratch.Airframe);
        Assert.Equal(CustomPlaneDef.EngineNone, flow.Scratch.Engine);
    }

    /// <summary>With nothing saved there is nothing to delete, so the screen is the New Plane row
    /// alone and the delete stage cannot be reached at all.</summary>
    [Fact]
    public void AnEmptyHangarOffersNoDeleteRow()
    {
        var flow = Open();

        Assert.Equal(1, flow.Page.RowCount);
        Assert.Equal("New Plane", flow.Page.RowText(0));
    }

    /// <summary>The delete row opens a second list whose every row names the plane it removes, so
    /// the press that destroys a build says which build (E46). Deleting one leaves the list up
    /// while any remain.</summary>
    [Fact]
    public void TheDeleteRowOpensAListThatNamesEachPlane()
    {
        _store.Save(new CustomPlaneDef { Name = "Alpha" });
        _store.Save(new CustomPlaneDef { Name = "Beta" });
        var flow = Open();

        flow.Move(3); // New Plane, Alpha, Beta, then the delete row
        Assert.Equal("Delete a saved plane", flow.Page.RowText(3));
        Assert.True(flow.Accept());
        Assert.Equal(HangarScreen.PlaneSelection, flow.Screen); // its own stage, not the next screen

        Assert.Equal(3, flow.Page.RowCount); // Alpha, Beta, Cancel
        Assert.Equal("Delete Alpha", flow.Page.RowText(0));
        Assert.Equal("Delete Beta", flow.Page.RowText(1));
        Assert.Equal("Cancel", flow.Page.RowText(2));

        Assert.True(flow.Accept()); // delete Alpha
        Assert.Equal("Beta", Assert.Single(flow.Saved).Name);
        Assert.Equal(2, flow.Page.RowCount); // still deleting: Beta, Cancel
        Assert.Equal("Delete Beta", flow.Page.RowText(0));
        Assert.Null(_store.Load("Alpha"));
    }

    /// <summary>Deleting the last saved plane returns to the pick list rather than leaving a list
    /// of nothing, and so does Cancel.</summary>
    [Fact]
    public void TheDeleteListClosesWhenItEmptiesAndOnCancel()
    {
        _store.Save(new CustomPlaneDef { Name = "Only One" });
        var flow = Open();
        flow.Move(2);
        flow.Accept();  // into the delete list
        flow.Accept();  // delete Only One

        Assert.Empty(flow.Saved);
        Assert.Equal(1, flow.Page.RowCount);
        Assert.Equal("New Plane", flow.Page.RowText(0));
        Assert.Equal(0, flow.Row);

        _store.Save(new CustomPlaneDef { Name = "Second" });
        var again = Open();
        again.Move(2);
        again.Accept();
        again.Move(1); // Cancel
        Assert.True(again.Accept());

        Assert.Equal("Second", Assert.Single(again.Saved).Name);
        Assert.Equal("New Plane", again.Page.RowText(0));
    }

    /// <summary>Deleting under the cursor cannot leave it past the list's end: the flow clamps on
    /// every roster change, which is what keeps a shrunk list navigable.</summary>
    [Fact]
    public void DeletingClampsTheCursorIntoTheShorterList()
    {
        _store.Save(new CustomPlaneDef { Name = "Alpha" });
        _store.Save(new CustomPlaneDef { Name = "Beta" });
        var flow = Open();
        flow.Move(3);
        flow.Accept();
        flow.Move(1);   // Delete Beta, the last plane row
        flow.Accept();

        Assert.Equal("Alpha", Assert.Single(flow.Saved).Name);
        Assert.True(flow.Row < flow.Page.RowCount);
        Assert.Equal("Delete Alpha", flow.Page.RowText(flow.Row));
    }

    /// <summary>The screens C22-C26 have not landed yet edit nothing: a flow walked straight
    /// through them produces the plane the caller set up and no other.</summary>
    [Fact]
    public void ThePlaceholderScreensEditNothing()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Airframe = 5;
        flow.Scratch.Engine = 3;
        flow.Scratch.LeftHardpoints = 2;
        Walk(flow, HangarScreen.Name);

        Assert.Equal(5, flow.Scratch.Airframe);
        Assert.Equal(3, flow.Scratch.Engine);
        Assert.Equal(2, flow.Scratch.LeftHardpoints);
    }

    /// <summary>The persistent totals line (Decision 10) is the build's price and weight against
    /// the airframe's capacity, recomputed from the economy on demand.</summary>
    [Fact]
    public void TheTotalsLineCarriesPriceWeightAndCapacity()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Engine = 1;

        Assert.Equal("$7650   2400 / 4160 lbs.", flow.TotalsLine);
        Assert.False(flow.TotalsOverweight);
    }

    /// <summary>Over capacity, the totals line is visibly flagged with the original's word and
    /// the shell's colour flag reads true.</summary>
    [Fact]
    public void TheTotalsLineFlagsOverweight()
    {
        var flow = Open();
        flow.Accept();
        flow.Scratch.Airframe = 0;
        flow.Scratch.Engine = 5;
        flow.Scratch.LeftHardpoints = 4;
        flow.Scratch.RightHardpoints = 4;

        // Hoplite 1400 + engine 2000 + 8 hardpoints x 480 = 7240 lbs against 4160.
        Assert.Contains("7240 / 4160 lbs.", flow.TotalsLine, StringComparison.Ordinal);
        Assert.Contains("OVERWEIGHT", flow.TotalsLine, StringComparison.Ordinal);
        Assert.True(flow.TotalsOverweight);
    }

    /// <summary>E49: a new plane leaves the airframe screen only through a pick, so the flow
    /// cannot reach the engine screen on one press. A saved plane's airframe is already the pick,
    /// so it does leave on one.</summary>
    [Fact]
    public void ANewPlaneLeavesTheAirframeScreenOnlyThroughAPick()
    {
        var flow = Open();
        flow.Accept(); // New Plane, on to Airframe

        flow.Accept();
        Assert.Equal(HangarScreen.Airframe, flow.Screen); // the press became a pick, not an advance
        Assert.Null(flow.DefaultsAsk); // the airframe id did not move, so nothing is asked
        flow.Accept();
        Assert.Equal(HangarScreen.Engine, flow.Screen);

        _store.Save(new CustomPlaneDef { Name = "Saved", Airframe = 3 });
        var edit = Open();
        edit.Move(1);
        edit.Accept(); // load the saved plane, on to Airframe
        edit.Accept();
        Assert.Equal(HangarScreen.Engine, edit.Screen);
    }

    /// <summary>E50: on the plane-selection screen the totals row prices the saved plane under
    /// the cursor, not the scratch plane, which this screen has not started building yet.</summary>
    [Fact]
    public void TheTotalsRowPricesTheFocusedSavedPlane()
    {
        _store.Save(new CustomPlaneDef { Name = "Hoplite One", Airframe = 0, Engine = 1 });
        _store.Save(new CustomPlaneDef { Name = "Zeppelin", Airframe = 2, Engine = 1 });
        var flow = Open();

        flow.Move(1);
        Assert.Equal("$7650   2400 / 4160 lbs.", flow.TotalsLine);
        flow.Move(1);
        Assert.Equal("$4420   8460 / 15760 lbs.", flow.TotalsLine);
        Assert.False(flow.TotalsOverweight);
    }

    /// <summary>E50: every row here that is not a saved plane (New Plane, the delete row, the
    /// delete list, its Cancel) has no plane to price, so the totals row hides.</summary>
    [Fact]
    public void TheTotalsRowHidesWhereNoPlaneIsFocused()
    {
        _store.Save(new CustomPlaneDef { Name = "Only One", Airframe = 0, Engine = 1 });
        var flow = Open();

        Assert.Equal(string.Empty, flow.TotalsLine); // New Plane
        Assert.False(flow.TotalsOverweight);
        flow.Move(2); // the delete row
        Assert.Equal(string.Empty, flow.TotalsLine);

        flow.Accept(); // into the delete list
        Assert.Equal("Delete Only One", flow.Page.RowText(0));
        Assert.Equal(string.Empty, flow.TotalsLine);
        flow.Move(1); // Cancel
        Assert.Equal(string.Empty, flow.TotalsLine);
    }

    /// <summary>E50 leaves the build screens alone: from the airframe screen on, the totals row
    /// is the scratch plane's, as Decision 10 has it.</summary>
    [Fact]
    public void TheBuildScreensStillPriceTheScratchPlane()
    {
        _store.Save(new CustomPlaneDef { Name = "Zeppelin", Airframe = 2, Engine = 1 });
        var flow = Open();
        flow.Accept(); // New Plane, on to Airframe

        Assert.Equal("$6800   1400 / 4160 lbs.", flow.TotalsLine);
    }

    // Confirms forward until the flow is on `target`, so a test names the screen it cares about
    // rather than counting presses. The walk keeps the plane the caller set up: the airframe
    // screen picks on confirm, so the cursor is put on the plane's own airframe first, which is
    // a pick that moves no airframe id and therefore asks nothing.
    private static void Walk(HangarFlow flow, HangarScreen target)
    {
        for (int guard = 0; flow.Screen != target && guard < HangarFlow.Order.Length + 3; guard++)
        {
            if (flow.Screen == HangarScreen.Airframe)
            {
                flow.FocusRow(flow.Scratch.Airframe);
            }

            flow.Accept();
        }

        Assert.Equal(target, flow.Screen);
    }

    private HangarFlow Open() => new(_store, UiStrings.Empty);
}
