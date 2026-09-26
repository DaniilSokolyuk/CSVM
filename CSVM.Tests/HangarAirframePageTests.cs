using System;
using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The AIRFRAME screen: all 11 airframes offered, confirm writing
/// the scratch plane's airframe and nothing else (E49: the pick is on Enter, the stepper is
/// inert), the detail line carrying the stat table's figures and the economy's own star ratings,
/// and the focused airframe's blueprint through the page-art seam. E41's airframe-defaults ask
/// (string 206) meets every pick of an airframe that is not already the pick: OK loads the
/// airframe's defaults, Cancel keeps every current pick.
/// </summary>
public class HangarAirframePageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarAirframePageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-airframe-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Decision 9: every one of the 11 airframes is a row; the availability threshold
    /// gates nothing. Names come from langui 3000+id.</summary>
    [Fact]
    public void OffersAllElevenAirframes_NamedFromLangui()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":3000,\"text\":\"Ford Hoplite\",\"dll\":\"langui\"}," +
            "{\"id\":3002,\"text\":\"Blackflag Balmoral\",\"dll\":\"langui\"}]");
        var flow = OpenOnAirframe(strings);

        Assert.Equal(HangarEconomy.Airframes.Length, flow.Page.RowCount);
        Assert.Equal(11, flow.Page.RowCount);
        Assert.StartsWith("Ford Hoplite", flow.Page.RowText(0), StringComparison.Ordinal);
        Assert.StartsWith("Blackflag Balmoral", flow.Page.RowText(2), StringComparison.Ordinal);
    }

    /// <summary>E49: confirm makes the focused row the pick (raising the defaults ask, here
    /// declined) and ticks it; the ←→ stepper does nothing at all on this screen, so a pick can
    /// only be made deliberately.</summary>
    [Fact]
    public void ConfirmSelectsTheFocusedAirframe_AndTheStepperIsInert()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);
        flow.Move(1);
        flow.Move(1);

        Assert.False(flow.Step(1));
        Assert.False(flow.Step(-1));
        Assert.False(flow.AirframeChosen);

        Assert.True(flow.Accept());
        Assert.Equal(2, flow.Scratch.Airframe);
        Assert.EndsWith("✓", flow.Page.RowText(2), StringComparison.Ordinal);
        Assert.False(flow.Page.RowText(0).EndsWith("✓", StringComparison.Ordinal));
    }

    /// <summary>E49, the user's finding: a new plane arrives with nothing committed. No row is
    /// ticked, no question is asked, and the model's own default airframe stays silent until the
    /// pilot picks one.</summary>
    [Fact]
    public void ANewPlaneArrivesWithNothingChosenAndNothingAsked()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);

        Assert.Null(flow.DefaultsAsk);
        Assert.False(flow.AirframeChosen);
        Assert.Equal(11, flow.Page.RowCount);
        Assert.All(
            new[] { 0, 1, 5, 10 },
            row => Assert.False(flow.Page.RowText(row).EndsWith("✓", StringComparison.Ordinal)));
    }

    /// <summary>E49: picking row 0 is still an explicit pick, so it ticks the row, but the airframe
    /// id it writes is the one the model was already carrying, so nothing is asked.</summary>
    [Fact]
    public void PickingTheOpeningRowStillCountsAsAPick()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);

        Assert.True(flow.Accept());
        Assert.Equal(HangarScreen.Airframe, flow.Screen);
        Assert.Null(flow.DefaultsAsk);
        Assert.True(flow.AirframeChosen);
        Assert.EndsWith("✓", flow.Page.RowText(0), StringComparison.Ordinal);
    }

    /// <summary>The ask's Cancel row is the one answer that preserves every other pick, which is
    /// what string 206 offers it for: it puts the airframe back too, so the build is exactly what
    /// the swap found.</summary>
    [Fact]
    public void TheAsksCancelPreservesTheOtherPicksAndPutsTheAirframeBack()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);
        flow.Scratch.Engine = 3;
        flow.Scratch.Guns[3] = new GunChoice(4, true);
        flow.Scratch.LeftHardpoints = 4;
        flow.Scratch.ArmourTail = 7;

        flow.Move(1);
        Assert.True(flow.Accept());

        Assert.Equal(1, flow.DefaultsAsk); // the switch itself already stands; the ask offers defaults
        Assert.Equal(1, flow.Scratch.Airframe);
        Assert.Equal(3, flow.Page.RowCount);
        flow.Move(1);
        flow.Move(1); // onto Cancel, the third answer
        flow.Accept();

        Assert.Null(flow.DefaultsAsk);
        Assert.Equal(0, flow.Scratch.Airframe);
        Assert.Equal(3, flow.Scratch.Engine);
        Assert.Equal(new GunChoice(4, true), flow.Scratch.Guns[3]);
        Assert.Equal(4, flow.Scratch.LeftHardpoints);
        Assert.Equal(7, flow.Scratch.ArmourTail);
    }

    /// <summary>The ask's Yes and No rows both rebuild from the new airframe's stock template, Yes
    /// taking it whole and No stripping it back to a bare airframe.</summary>
    [Fact]
    public void TheAsksYesTakesTheStockBuildAndItsNoTakesABareAirframe()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);
        flow.Scratch.Engine = 3;
        flow.Move(1);
        Assert.True(flow.Accept());
        Assert.Equal(0, flow.Row); // the cursor moves onto the answers

        flow.Accept(); // Yes
        Assert.Equal(1, flow.Scratch.Airframe);
        Assert.Equal(1, flow.Scratch.Engine);

        flow.Scratch.Engine = 3;
        flow.FocusRow(2);
        Assert.True(flow.Accept());
        flow.Move(1);
        flow.Accept(); // No

        Assert.Equal(2, flow.Scratch.Airframe);
        Assert.Equal(CustomPlaneDef.EngineNone, flow.Scratch.Engine);
        Assert.All(flow.Scratch.Guns, gun => Assert.True(gun.IsEmpty));
    }

    /// <summary>E49's double-enter idiom: the confirm that picks an airframe stays on the screen,
    /// and the next confirm, now on the picked row, advances to the Engine screen without touching
    /// anything.</summary>
    [Fact]
    public void ConfirmingThePickAdvancesWithoutEditing()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);
        flow.FocusRow(5);

        Assert.True(flow.Accept()); // picks the Fury
        Assert.Equal(HangarScreen.Airframe, flow.Screen);
        Assert.Equal(5, flow.Row); // the cursor stays on the pick

        Assert.True(flow.Accept());
        Assert.Equal(HangarScreen.Engine, flow.Screen);
        Assert.Equal(5, flow.Scratch.Airframe);
    }

    /// <summary>Every row's detail line shows its own decoded cost, weight and capacity
    /// (docs/org/hangar.md's stat table, pinned by HangarEconomyTests).</summary>
    [Fact]
    public void DetailShowsTheDecodedFigures()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);

        string hoplite = flow.Page.Detail(0);
        Assert.Contains("$6800", hoplite, StringComparison.Ordinal);
        Assert.Contains("1400 lbs.", hoplite, StringComparison.Ordinal);
        Assert.Contains("Capacity 4160 lbs.", hoplite, StringComparison.Ordinal);

        string balmoral = flow.Page.Detail(2);
        Assert.Contains("$1870", balmoral, StringComparison.Ordinal);
        Assert.Contains("5460 lbs.", balmoral, StringComparison.Ordinal);
        Assert.Contains("Capacity 15760 lbs.", balmoral, StringComparison.Ordinal);
    }

    /// <summary>The stars are HangarEconomy's own: Hoplite tops agility at 4, the Balmoral sits
    /// at 0 (the plan's hand-checked pair); armour stars track the scratch plane's armour units,
    /// exactly as the decoded formula reads the record.</summary>
    [Fact]
    public void StarRatingsMatchTheEconomy()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);

        Assert.Contains("Agility ★★★★", flow.Page.Detail(0), StringComparison.Ordinal);
        Assert.Contains("Armor ☆☆☆☆", flow.Page.Detail(0), StringComparison.Ordinal);
        Assert.Contains("Agility ☆☆☆☆", flow.Page.Detail(2), StringComparison.Ordinal);
        Assert.Contains("Armor ★☆☆☆", flow.Page.Detail(2), StringComparison.Ordinal);

        flow.Scratch.ArmourNose = 12;
        flow.Scratch.ArmourTail = 12;
        flow.Scratch.ArmourLeftWing = 12;
        flow.Scratch.ArmourRightWing = 12;
        Assert.Contains("Armor ★★★★", flow.Page.Detail(0), StringComparison.Ordinal);
    }

    /// <summary>Reading a detail line never edits the scratch plane, even though the ratings are
    /// priced for the focused row's airframe rather than the chosen one.</summary>
    [Fact]
    public void DetailLeavesTheScratchUntouched()
    {
        var flow = OpenOnAirframe(UiStrings.Empty);
        flow.Scratch.Airframe = 7;
        flow.Page.Detail(2);

        Assert.Equal(7, flow.Scratch.Airframe);
    }

    /// <summary>A flow opened without a data root has no art to offer, and the screen still
    /// works: art is optional by design.</summary>
    [Fact]
    public void ArtIsNullWithoutADataRoot() =>
        Assert.Null(OpenOnAirframe(UiStrings.Empty).Page.Art);

    /// <summary>With the extraction present, the page's art is the focused airframe's blueprint
    /// TGA at its catalogued 358x335, captioned with that airframe's name, and it follows the
    /// cursor rather than the pick.</summary>
    [ExtractedDataFact]
    public void ArtShowsTheFocusedAirframesBlueprint()
    {
        var flow = new HangarFlow(_store, UiStrings.Empty, TestData.DataRoot);
        flow.Accept(); // New Plane, on to Airframe

        var art = flow.Page.Art;
        Assert.NotNull(art);
        Assert.Equal(358, art!.Image.Width);
        Assert.Equal(335, art.Image.Height);
        Assert.Equal("Airframe 0", art.Caption);

        flow.Move(1);
        Assert.Equal("Airframe 1", flow.Page.Art!.Caption);
    }

    /// <summary>E41 through E49: an airframe swap over an edited build raises the defaults ask as
    /// the inline confirm, carrying the original's own three answers.</summary>
    [Fact]
    public void AnEditedBuildsSwapRaisesTheAskWithThreeAnswers()
    {
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Accept(); // New Plane, on to Airframe
        flow.Feature.SetEngine(1); // the edit the question is about
        flow.Move(1);
        flow.Accept(); // swap onto the Hellhound

        Assert.Equal(HangarScreen.Airframe, flow.Screen);
        Assert.Equal(1, flow.DefaultsAsk);
        Assert.Equal(3, flow.Page.RowCount);
        Assert.Equal("Yes", flow.Page.RowText(0));
        Assert.Equal("No", flow.Page.RowText(1));
        Assert.Equal("Cancel", flow.Page.RowText(2));
        Assert.Contains("default armor, engine, and guns", flow.Page.Detail(0), StringComparison.Ordinal);
    }

    /// <summary>E41: editing a saved plane never asks; the plane already is what its builder
    /// chose, so the screen opens on that airframe's row, ticked (E49).</summary>
    [Fact]
    public void EditingASavedPlaneDoesNotAsk()
    {
        _store.Save(new CustomPlaneDef { Name = "Kept", Airframe = 4 });
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Move(1);
        flow.Accept();

        Assert.Equal(HangarScreen.Airframe, flow.Screen);
        Assert.Null(flow.DefaultsAsk);
        Assert.Equal(11, flow.Page.RowCount);
        Assert.True(flow.AirframeChosen);
        Assert.Equal(4, flow.Row);
        Assert.EndsWith("✓", flow.Page.RowText(4), StringComparison.Ordinal);
    }

    /// <summary>E49: a saved plane's own airframe is already the pick, so confirming it asks
    /// nothing and simply advances. Only a pick of a DIFFERENT airframe asks.</summary>
    [Fact]
    public void ConfirmingASavedPlanesAirframeAdvancesWithoutAsking()
    {
        _store.Save(new CustomPlaneDef { Name = "Kept", Airframe = 4, Engine = 2 });
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Move(1);
        flow.Accept(); // load the saved plane, on to Airframe

        Assert.True(flow.Accept());
        Assert.Null(flow.DefaultsAsk);
        Assert.Equal(HangarScreen.Engine, flow.Screen);
        Assert.Equal(4, flow.Scratch.Airframe);
        Assert.Equal(2, flow.Scratch.Engine);
    }

    /// <summary>E41: the ask speaks string 206 over two short airframe names (langui 3020 + id),
    /// the one just picked and the one the build was opened on. The plane's own typed name is not
    /// one of its arguments.</summary>
    [Fact]
    public void TheAskSpeaksString206WithBothShortAirframeNames()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":206,\"text\":\"Defaults for %1!s!? You were building the %2!s!.\",\"dll\":\"langui\"}," +
            "{\"id\":3020,\"text\":\"Hoplite\",\"dll\":\"langui\"}," +
            "{\"id\":3021,\"text\":\"Hellhound\",\"dll\":\"langui\"}]");
        var flow = OpenOnAirframe(strings);
        flow.Scratch.Name = "My Crate";
        flow.Feature.SetEngine(1);

        flow.Move(1);
        Assert.True(flow.Accept());
        Assert.Equal("Defaults for Hellhound? You were building the Hoplite.", flow.Page.Detail(0));

        flow.AnswerDefaultsAsk(false);
        flow.Feature.SetEngine(1);
        flow.Move(-1); // back onto the Hoplite row
        Assert.True(flow.Accept());
        Assert.Equal("Defaults for Hoplite? You were building the Hellhound.", flow.Page.Detail(0));
    }

    /// <summary>E41's accept arm, the worked example: the Balmoral's defaults are its stock fit
    /// read back through the A3 mapping (four twin mounts: two fifty-cals, two thirty-cal
    /// turrets), its eight authored pylons as 4/4 wing counts, and the stock Lvl-2 engine
    /// (id 1). Armour stays 0 here: no zrdr scope was handed in.</summary>
    [Fact]
    public void AcceptingLoadsTheAirframeDefaults()
    {
        var stock = StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));
        var flow = OpenOnAirframe(UiStrings.Empty, stock);
        flow.Scratch.ArmourNose = 9; // a pick the defaults overwrite

        flow.Move(1);
        flow.Move(1); // onto the Balmoral
        Assert.True(flow.Accept()); // picks it, raising the ask over the armour edit
        flow.Accept(); // the cursor sits on Yes when the ask is raised

        Assert.Null(flow.DefaultsAsk);
        Assert.Equal(2, flow.Scratch.Airframe);
        Assert.Equal(1, flow.Scratch.Engine);
        Assert.Equal(new GunChoice(2, true), flow.Scratch.Guns[0]);
        Assert.Equal(new GunChoice(2, true), flow.Scratch.Guns[1]);
        Assert.Equal(new GunChoice(0, true), flow.Scratch.Guns[2]);
        Assert.Equal(new GunChoice(0, true), flow.Scratch.Guns[3]);
        Assert.Equal(4, flow.Scratch.LeftHardpoints);
        Assert.Equal(4, flow.Scratch.RightHardpoints);
        Assert.Equal(0, flow.Scratch.ArmourNose);
    }

    /// <summary>E41: the Hoplite's stock fit authors one twin thirty-cal and two pylons, whose
    /// fill order (1, 5) hangs both to port, since the rig pairs odd pylons there; the Kestrel's
    /// slot 1 is the one single-barrel stock mount, so its default is NOT a twin.</summary>
    [Fact]
    public void DefaultsReadTheStockFitPerAirframe()
    {
        var stock = StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));
        var flow = OpenOnAirframe(UiStrings.Empty, stock);

        flow.LoadAirframeDefaults(0); // Hoplite
        Assert.Equal(new GunChoice(0, true), flow.Scratch.Guns[0]);
        Assert.True(flow.Scratch.Guns[1].IsEmpty);
        Assert.Equal((2, 0), (flow.Scratch.LeftHardpoints, flow.Scratch.RightHardpoints));

        flow.LoadAirframeDefaults(8); // Kestrel
        Assert.Equal(new GunChoice(3, false), flow.Scratch.Guns[0]);
        Assert.Equal(new GunChoice(2, true), flow.Scratch.Guns[1]);
        Assert.True(flow.Scratch.Guns[2].IsEmpty);
        Assert.Equal(new GunChoice(1, true), flow.Scratch.Guns[3]);
        Assert.Equal((3, 2), (flow.Scratch.LeftHardpoints, flow.Scratch.RightHardpoints));
    }

    /// <summary>E41: a pick on arrival keeps the brand-new plane's empty state; loading the
    /// defaults without a stock table still sets the engine default and leaves the rest empty.</summary>
    [Fact]
    public void PickingOnArrivalKeepsTheEmptyState_AndNoStockTableDegradesQuietly()
    {
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Accept(); // New Plane
        flow.Accept(); // pick the focused airframe, which moves no airframe id
        Assert.Equal(CustomPlaneDef.EngineNone, flow.Scratch.Engine);
        Assert.All(flow.Scratch.Guns, gun => Assert.True(gun.IsEmpty));
        Assert.Equal(0, flow.Scratch.LeftHardpoints);

        flow.LoadAirframeDefaults(0); // no StockFits handed in
        Assert.Equal(1, flow.Scratch.Engine);
        Assert.All(flow.Scratch.Guns, gun => Assert.True(gun.IsEmpty));
        Assert.Equal(0, flow.Scratch.LeftHardpoints);
        Assert.Equal(0, flow.Scratch.ArmourNose);
    }

    /// <summary>E41, with the real extraction: accepting the ask loads the airframe's stock
    /// zone allocations, the destroyable_parts armour pools at five per unit
    /// (docs/formats/vehicle.md), read back through PlaneStats' own vehicle defs.</summary>
    [ExtractedDataFact]
    public void TheDefaultsCarryTheStockArmourAllocations()
    {
        string zrdr = SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));
        var flow = new HangarFlow(_store, UiStrings.Empty, null, null, zrdr);
        flow.Accept(); // New Plane, on to Airframe
        flow.Move(1);
        flow.Accept(); // swap onto the Hellhound
        flow.Feature.SetEngine(1); // the edit the swap back asks about
        flow.Move(-1);
        flow.Accept(); // swap back onto the Hoplite, raising its defaults ask
        flow.Accept(); // Yes

        var stats = PlaneStats.Load(zrdr, "player_autogyro");
        foreach (var part in stats.DestroyableParts)
        {
            int? units = part.Name.ToLowerInvariant() switch
            {
                "nose" => flow.Scratch.ArmourNose,
                "tail" => flow.Scratch.ArmourTail,
                "leftwing" => flow.Scratch.ArmourLeftWing,
                "rightwing" => flow.Scratch.ArmourRightWing,
                _ => null,
            };
            if (units is { } bought)
            {
                Assert.True(bought > 0, $"{part.Name}: stock armour should buy units");
                Assert.Equal((int)Math.Round(part.MaxArmor / 5f), bought);
            }
        }
    }

    // A flow standing on the AIRFRAME screen with a fresh scratch plane: nothing picked, nothing
    // asked, which is what a new build now arrives as (E49).
    private HangarFlow OpenOnAirframe(UiStrings strings, StockLoadouts? stockFits = null)
    {
        var flow = new HangarFlow(_store, strings, null, stockFits);
        flow.Accept(); // New Plane, on to Airframe
        Assert.Equal(HangarScreen.Airframe, flow.Screen);
        Assert.Null(flow.DefaultsAsk);
        return flow;
    }
}
