using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The shared hangar feature off engine: the scratch plane's three starts, the airframe pick and
/// its defaults ask, the purchase gate in the original's words, the commit into a store, the sale
/// through a wallet and the deletion without one, the name-collision rule over the whole build
/// directory, the paint rules, and the discard that drops everything but what was saved. The
/// same rules Built-in's flow walks, so a case here binds both presentations.
/// </summary>
public class HangarFeatureTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarFeatureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-hangar-feature-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void OpenReadsTheRosterAndStartsBare()
    {
        _store.Save(new CustomPlaneDef { Name = "Kept", Airframe = 3, Engine = 1 });
        var feature = Feature();

        Assert.False(feature.IsOpen);
        feature.Open(_store);

        Assert.True(feature.IsOpen);
        Assert.Equal(new[] { "Kept" }, feature.Saved.Select(p => p.Name));
        Assert.False(feature.AirframeChosen);
        Assert.Equal(CustomPlaneDef.EngineNone, feature.Scratch.Engine);
        Assert.Null(feature.EditingName);
        Assert.True(feature.IsNameTaken("kept"));
        Assert.False(feature.IsNameTaken("Other"));
    }

    [Fact]
    public void TheDefaultConfigurationIsTheDevastatorWithItsStockEngineAlreadyChosen()
    {
        var feature = Feature();
        feature.Open(_store);

        feature.StartDefaultPlane();

        Assert.True(feature.AirframeChosen);
        Assert.Equal(HangarFeature.DefaultAirframe, feature.Scratch.Airframe);
        Assert.Equal(1, feature.Scratch.Engine);
        Assert.Null(feature.DefaultsAsk);
        Assert.True(HangarPaintTables.Default.Available(feature.Scratch.PaintPattern, HangarFeature.DefaultAirframe));
    }

    [Fact]
    public void StartingFromASavedPlaneEditsACopyAndNeverTheFile()
    {
        var saved = new CustomPlaneDef { Name = "Kept", Airframe = 3, Engine = 2 };
        _store.Save(saved);
        var feature = Feature();
        feature.Open(_store);

        feature.StartFromSaved(feature.Saved[0]);
        feature.Scratch.Engine = 4;

        Assert.Equal("Kept", feature.EditingName);
        Assert.True(feature.AirframeChosen);
        Assert.Equal(2, _store.Load("Kept")!.Engine);
        Assert.False(feature.Overwrites());
    }

    [Fact]
    public void AnUneditedSwapTakesTheBoxsOwnAnswerAndAnEditedOneAsks()
    {
        var feature = Feature();
        feature.Open(_store);

        // Nothing has been edited away from the opened build, so no question is raised and the
        // name screen's cleared box leaves a bare airframe.
        Assert.True(feature.PickAirframe(4));
        Assert.Null(feature.DefaultsAsk);
        Assert.Equal(CustomPlaneDef.EngineNone, feature.Scratch.Engine);
        Assert.False(feature.PickAirframe(4));

        Assert.True(feature.SetEngine(3));
        Assert.True(feature.EditedSinceOpened);
        Assert.True(feature.PickAirframe(7));
        Assert.Equal(7, feature.DefaultsAsk);
        Assert.Contains("Airframe 7", feature.DefaultsAskText);
        Assert.Contains("Airframe 4", feature.DefaultsAskText);

        // No takes the bare airframe, which is why 206 offers Cancel to anyone keeping an edit.
        feature.AnswerDefaultsAsk(false);
        Assert.Null(feature.DefaultsAsk);
        Assert.Equal(CustomPlaneDef.EngineNone, feature.Scratch.Engine);
        Assert.Equal(7, feature.Scratch.Airframe);
        Assert.True(HangarPaintTables.Default.Available(feature.Scratch.PaintPattern, 7));
    }

    [Fact]
    public void TheAsksYesTakesTheStockBuildAndItsCancelPutsTheAirframeBack()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.StartDefaultPlane(4);
        Assert.True(feature.SetArmour(0, 7));

        Assert.True(feature.PickAirframe(7));
        Assert.Equal(7, feature.DefaultsAsk);
        feature.CancelDefaultsAsk();
        Assert.Null(feature.DefaultsAsk);
        Assert.Equal(4, feature.Scratch.Airframe);
        Assert.Equal(7, feature.ArmourUnits(0));

        Assert.True(feature.PickAirframe(7));
        feature.AnswerDefaultsAsk(true);
        Assert.Equal(7, feature.Scratch.Airframe);
        Assert.Equal(1, feature.Scratch.Engine);
        Assert.NotEqual(7, feature.ArmourUnits(0));
    }

    [Fact]
    public void TheGateRefusesInTheOriginalsWordsThenCommitsIntoTheStore()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.PickAirframe(4);

        Assert.Equal("You must enter a name for your new plane.", feature.Refusal());
        feature.Scratch.Name = "Ace";
        Assert.Equal("CAN'T PURCHASE: No Engine Selected", feature.Refusal());
        Assert.False(feature.Commit());
        Assert.Equal("CAN'T PURCHASE: No Engine Selected", feature.Message);
        Assert.Null(_store.Load("Ace"));

        Assert.True(feature.SetEngine(1));
        Assert.True(feature.CanCommit);
        Assert.True(feature.Commit());
        Assert.Equal("Ace", feature.BuiltPlaneName);
        Assert.Equal(string.Empty, feature.Message);
        Assert.Equal(4, _store.Load("Ace")!.Airframe);
    }

    [Fact]
    public void AnOverweightBuildIsRefusedWithTheOriginalsWord()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.StartDefaultPlane();
        feature.Scratch.Name = "Brick";
        for (int zone = 0; zone < 4; zone++)
        {
            feature.SetArmour(zone, CustomPlaneDef.MaxArmourUnits);
        }

        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            feature.SetGun(slot, 9);
        }

        feature.SetHardpoints(0, 4);
        feature.SetHardpoints(1, 4);

        Assert.Equal(PurchaseVerdict.Overweight, feature.Bill.Verdict);
        Assert.Equal("CAN'T PURCHASE: OVERWEIGHT", feature.Refusal());
        Assert.Contains("OVERWEIGHT", feature.TotalsLine(feature.Scratch));
    }

    /// <summary>The armour setter behind Original's four combo boxes couples the wings: a pick on
    /// either wing writes both, its cost preview prices the pair, and a pick that only levels a
    /// loaded plane's disagreeing wings still reports a change so the other box redraws.</summary>
    [Fact]
    public void AWingPickSetsBothWingsAndIsPricedAsAPair()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.StartDefaultPlane();
        feature.SetArmour(0, 0);
        feature.SetArmour(1, 0);

        Assert.True(feature.SetArmour(2, 3));
        Assert.Equal(3, feature.ArmourUnits(2));
        Assert.Equal(3, feature.ArmourUnits(3));
        Assert.Equal(feature.Bill.Total.Cost + (2 * HangarEconomy.ArmourStepCost), feature.CostWithArmour(3, 4));
        Assert.Equal(feature.Bill.Total.Cost + HangarEconomy.ArmourStepCost, feature.CostWithArmour(0, 1));

        feature.Scratch.ArmourLeftWing = 2;
        feature.Scratch.ArmourRightWing = 9;
        Assert.True(feature.SetArmour(2, 2));
        Assert.Equal(2, feature.ArmourUnits(3));
        Assert.False(feature.SetArmour(2, 2));
    }

    [Fact]
    public void AWalletGatesAvailabilityAndFundsAndIsPaidOnCommit()
    {
        var wallet = new FakeWallet { FundsValue = 100, AvailableFrom = 5 };
        var feature = Feature();
        feature.Open(_store, wallet);
        feature.StartDefaultPlane();
        feature.Scratch.Name = "Bought";

        Assert.Equal("CAN'T PURCHASE: INSUFFICIENT FUNDS", feature.Refusal());
        wallet.FundsValue = 100_000;
        Assert.Null(feature.Refusal());
        wallet.AvailableFrom = 6;
        Assert.Equal("CAN'T PURCHASE: That airframe is not available yet.", feature.Refusal());
        wallet.AvailableFrom = 0;

        Assert.True(feature.Commit());
        Assert.Equal(("Bought", HangarFeature.DefaultAirframe, feature.Bill.Total.Cost), wallet.Purchased);
        Assert.NotNull(_store.Load("Bought"));
    }

    [Fact]
    public void TheWalletsRosterIsOwnershipWhileTakenNamesSpanTheWholeStore()
    {
        _store.Save(new CustomPlaneDef { Name = "Instant", Airframe = 1, Engine = 1 });
        var wallet = new FakeWallet();
        wallet.Owned.Add(new CustomPlaneDef { Name = "Owned", Airframe = 5, Engine = 1 });
        var feature = Feature();
        feature.Open(_store, wallet);

        Assert.Equal(new[] { "Owned" }, feature.Saved.Select(p => p.Name));
        Assert.True(feature.IsNameTaken("Instant"));
        Assert.True(feature.IsNameTaken("Owned"));
    }

    [Fact]
    public void DeletingWithoutAWalletRemovesTheFileAndWithOneSellsOrRefuses()
    {
        _store.Save(new CustomPlaneDef { Name = "Gone", Airframe = 1, Engine = 1 });
        var feature = Feature();
        feature.Open(_store);
        Assert.True(feature.DeleteSaved("Gone"));
        Assert.Empty(feature.Saved);
        Assert.Null(_store.Load("Gone"));

        var wallet = new FakeWallet();
        wallet.Owned.Add(new CustomPlaneDef { Name = "Prize", Airframe = 2, Engine = 1 });
        wallet.Owned.Add(new CustomPlaneDef { Name = "Spare", Airframe = 3, Engine = 1 });
        wallet.Special.Add("Prize");
        feature.Open(_store, wallet);

        Assert.False(feature.DeleteSaved("Prize"));
        Assert.Equal("This Airframe 2, Prize, cannot be sold.", feature.Message);
        Assert.False(feature.DeleteSaved("Spare"));
        Assert.Equal("You must keep at least two planes in your hangar.", feature.Message);
        wallet.Sellable = true;
        Assert.True(feature.DeleteSaved("Spare"));
        Assert.Equal(new[] { "Spare" }, wallet.Sold);
        Assert.Equal(string.Empty, feature.Message);
    }

    [Fact]
    public void ThePaintRulesFollowTheOriginals()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.StartDefaultPlane();

        var wearable = feature.WearablePatterns();
        Assert.Contains(feature.Scratch.PaintPattern, wearable);
        Assert.All(wearable, p => Assert.True(HangarPaintTables.Default.Available(p, HangarFeature.DefaultAirframe)));

        Assert.True(feature.SetColour(0, 3));
        Assert.Equal(3, feature.Scratch.PaintColours[0]);
        Assert.Equal(HangarPaintTables.Default.DefaultShadeFor(3), feature.Scratch.PaintShades[0]);
        Assert.True(feature.SetShade(0, 1));
        Assert.Equal(1, feature.Scratch.PaintShades[0]);
        Assert.True(feature.SetDecal(2, 40));
        Assert.Equal(40, feature.Scratch.WingDecal);
        Assert.False(feature.SetDecal(2, 40));
    }

    [Fact]
    public void TheDropdownRulesReadAsTheOriginalNamesThem()
    {
        var feature = Feature();

        Assert.Equal("None", feature.ArmourLabel(0));
        Assert.Equal("15 units", feature.ArmourLabel(3));
        Assert.Equal("None", feature.HardpointsLabel(0));
        Assert.Equal("1 Hardpoint", feature.HardpointsLabel(1));
        Assert.Equal("3 Hardpoints", feature.HardpointsLabel(3));
        Assert.Equal("No Gun", feature.GunCycleName(HangarFeature.GunCycleRows - 1));
        Assert.Equal("(2) .50-cal.", feature.GunCycleName(7));
        Assert.Equal(new GunChoice(2, true), HangarFeature.GunOfCycle(7));
        Assert.Equal(7, HangarFeature.GunCycleIndex(new GunChoice(2, true)));
        Assert.True(HangarFeature.AcceptsNameChar('a'));
        Assert.True(HangarFeature.AcceptsNameChar('\''));
        Assert.False(HangarFeature.AcceptsNameChar('/'));
    }

    [Fact]
    public void DiscardDropsTheScratchPlaneAndKeepsWhatWasSaved()
    {
        var feature = Feature();
        feature.Open(_store);
        feature.StartDefaultPlane();
        feature.Scratch.Name = "Saved";
        Assert.True(feature.Commit());
        feature.Scratch.Name = "Unsaved";

        feature.Discard();

        Assert.False(feature.IsOpen);
        Assert.Equal(string.Empty, feature.Scratch.Name);
        Assert.Empty(feature.Saved);
        Assert.Null(feature.BuiltPlaneName);
        Assert.NotNull(_store.Load("Saved"));
        Assert.Null(_store.Load("Unsaved"));
        Assert.False(feature.Commit());
    }

    private static HangarFeature Feature() =>
        new(UiStrings.Empty, PlanePickerRoster.AirframeNode);

    private sealed class FakeWallet : IHangarWallet
    {
        public int FundsValue { get; set; } = 1_000_000;

        public int AvailableFrom { get; set; }

        public bool Sellable { get; set; }

        public bool RoomForOneMore { get; set; } = true;

        public List<CustomPlaneDef> Owned { get; } = new();

        public HashSet<string> Special { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Sold { get; } = new();

        public (string Name, int Airframe, int Cost)? Purchased { get; private set; }

        public int Funds => FundsValue;

        public bool HasFreeSlot => RoomForOneMore;

        public bool CanAfford(int cost) => FundsValue >= cost;

        public bool IsAirframeAvailable(int airframe) => airframe >= AvailableFrom;

        public bool IsSpecial(string planeName) => Special.Contains(planeName);

        public bool CanSell(string planeName) => Sellable && !IsSpecial(planeName);

        public int? OwnedAirframe(string planeName) =>
            Owned.FirstOrDefault(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase))?.Airframe;

        public IReadOnlyList<CustomPlaneDef> OwnedBuilds() => Owned.ToList();

        public void Purchase(string planeName, int airframe, int cost) => Purchased = (planeName, airframe, cost);

        public bool Sell(string planeName)
        {
            Sold.Add(planeName);
            Owned.RemoveAll(p => p.Name == planeName);
            return true;
        }
    }
}
