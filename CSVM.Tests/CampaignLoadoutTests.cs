using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight;
using CSVM.Session;
using Xunit;

namespace CSVM.Tests;

/// <summary>The profile's stored picks turned into a flying fit: the ammunition index's four
/// names and its no-gun marker, the ordnance cell's table index and its left/right wing split,
/// and what an unset or table-less pick leaves alone.</summary>
public class CampaignLoadoutTests
{
    private static readonly StockLoadouts Stock =
        StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));

    [Fact]
    public void EachAmmoIndexBindsItsOwnAmmunitionName()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ammo = new[] { 0, 1, 2, 3 } };

        var fit = CampaignLoadout.For(plane, Stock);

        Assert.Equal("slug", fit.GunAmmoFor(1));
        Assert.Equal("dumdum", fit.GunAmmoFor(2));
        Assert.Equal("ap", fit.GunAmmoFor(3));
        Assert.Equal("magnesium", fit.GunAmmoFor(4));
    }

    [Fact]
    public void TheNoGunMarkerBindsAnEmptyMountRatherThanAnAmmunition()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ammo = new[] { 4, 0, 0, 0 } };

        var fit = CampaignLoadout.For(plane, Stock);

        Assert.Equal(LoadoutChoice.None, fit.GunAmmoFor(1));
        Assert.Equal("slug", fit.GunAmmoFor(2));
    }

    [Fact]
    public void AnOrdnanceCellCarriesItsTableRowsWeaponOnItsOwnWingsCell()
    {
        // Cell 0 is the left wing's first pylon and cell 4 the right wing's first; a cell travels as
        // a cell, since which pylon it is depends on the fit. The stored value is the row plus one.
        var ordnance = new int[8];
        ordnance[0] = 3;
        ordnance[1] = 2;
        ordnance[4] = 11;
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ordnance = ordnance };

        var fit = CampaignLoadout.For(plane, Stock);

        Assert.Equal(Stock.Options.PylonOrdnance[2].Id, fit.WingCellFor(0));
        Assert.Equal(Stock.Options.PylonOrdnance[1].Id, fit.WingCellFor(1));
        Assert.Equal(Stock.Options.PylonOrdnance[10].Id, fit.WingCellFor(4));
        Assert.Null(fit.WingCellFor(5));
    }

    /// <summary>The reported case: a campaign Devastator with all four of its pylons set to flak
    /// carries flak on all four in the air. A four-pylon stock fit hangs pylons 1, 5, 2 and 6, so
    /// the left wing's second cell is pylon 5, and resolving it as pylon 3 instead dropped the pick
    /// and left that pylon on the base fit's high explosive.</summary>
    [Fact]
    public void EveryPylonAFourPylonStockFitHangsCarriesItsOwnCellsOrdnance()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ordnance = new[] { 3, 3, 0, 0, 3, 3, 0, 0 } };
        var stockFit = Stock.For("pdevastator")!;

        var applied = CampaignLoadout.For(plane, Stock).ApplyTo(stockFit);

        Assert.Equal(new[] { 1, 5, 2, 6 }, Pylons(stockFit));
        Assert.Equal(new[] { "wep_07", "wep_07", "wep_07", "wep_07" }, applied.Hardpoints!.Stock);
    }

    /// <summary>The same four picks on the same airframe built two pylons a wing in the hangar,
    /// which hangs 1 and 3 to port with 2 and 4 to starboard: a different pylon set, the same four
    /// cells, and every one of them carried.</summary>
    [Fact]
    public void ATwoAWingHangarBuildCarriesTheSameFourCells()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ordnance = new[] { 3, 3, 0, 0, 3, 3, 0, 0 } };
        var build = new CustomPlaneDef { Name = "Gypsy Magic", LeftHardpoints = 2, RightHardpoints = 2 };
        var built = CustomPlaneBuild.LoadoutFor(build, Stock.For("pdevastator")!);

        var applied = CampaignLoadout.For(plane, Stock).ApplyTo(built);

        Assert.Equal(new[] { 1, 2, 3, 4 }, Pylons(built));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Pylons(applied));
        Assert.All(Carried(applied), id => Assert.Equal("wep_07", id));
    }

    /// <summary>A cell the fit has no pylon for is dropped rather than landing on another wing's
    /// pylon: a Fury hangs three, so its right wing's second cell names nothing.</summary>
    [Fact]
    public void ACellBeyondWhatTheWingHangsIsDropped()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ordnance = new[] { 3, 3, 0, 0, 3, 3, 0, 0 } };
        var stockFit = Stock.For("pfury")!;

        var applied = CampaignLoadout.For(plane, Stock).ApplyTo(stockFit);

        // pylon1 and pylon5 are the port pair and pylon2 the lone starboard one.
        Assert.Equal(new[] { 1, 5, 2 }, Pylons(stockFit));
        Assert.Equal(new[] { "wep_07", "wep_07", "wep_07" }, applied.Hardpoints!.Stock);
    }

    [Fact]
    public void AnUnsetCellLeavesThePylonAtItsBaseFit()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic" };
        var stockFit = Stock.For("pdevastator")!;

        var fit = CampaignLoadout.For(plane, Stock);

        for (int cell = 0; cell < LoadoutChoice.OrdnanceCells; cell++)
        {
            Assert.Null(fit.WingCellFor(cell));
        }

        Assert.Equal(stockFit.Hardpoints!.Stock, fit.ApplyTo(stockFit).Hardpoints!.Stock);
    }

    [Fact]
    public void WithNoStockTableThePylonsAreLeftAloneAndTheGunsAreNot()
    {
        var ordnance = new int[8];
        ordnance[0] = 3;
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ammo = new[] { 2, 2, 2, 2 }, Ordnance = ordnance };

        var fit = CampaignLoadout.For(plane, null);

        Assert.Null(fit.WingCellFor(0));
        Assert.Equal("ap", fit.GunAmmoFor(1));
    }

    [Fact]
    public void AFreshProfilesStarterBindsTheStockFitUnchanged()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var fit = CampaignLoadout.For(profile.Planes[profile.SelectedPlane], Stock);

        // Every gun slot reads slug, the stock ammunition, and no pylon is picked at all, so
        // laying this over an airframe's own fit changes nothing about it.
        Assert.Equal("slug", fit.GunAmmoFor(1));
        Assert.Null(fit.WingCellFor(0));
    }

    /// <summary>An exported plane's own stored picks read the same way the profile record's do, so
    /// Instant Action flies what the campaign fitted.</summary>
    [Fact]
    public void AnExportedPlanesStoredPicksReadLikeTheProfileRecords()
    {
        var ordnance = new int[] { 3, 0, 0, 0, 11, 0, 0, 0 };
        var exported = new CustomPlaneDef { Name = "Gypsy Magic" };
        exported.SetLoadout(new[] { 2, 4, 0, 1 }, ordnance);

        var fit = CampaignLoadout.For(exported, Stock);

        Assert.Equal("ap", fit.GunAmmoFor(1));
        Assert.Equal(LoadoutChoice.None, fit.GunAmmoFor(2));
        Assert.Equal(Stock.Options.PylonOrdnance[2].Id, fit.WingCellFor(0));
        Assert.Equal(Stock.Options.PylonOrdnance[10].Id, fit.WingCellFor(4));
    }

    /// <summary>A plane the campaign never exported picks nothing at all, so laying its fit over an
    /// airframe leaves the airframe's own.</summary>
    [Fact]
    public void APlaneWithNoExportedLoadoutLeavesEveryGunAndPylonAlone()
    {
        var fit = CampaignLoadout.For(new CustomPlaneDef { Name = "Blue Streak" }, Stock);

        Assert.True(fit.IsStock);
    }

    /// <summary>A co-op seat's fit crosses the wire as the stored values. It binds on the far
    /// machine exactly as the pilot's own record binds at home. The launch's fit encodes back to
    /// the same values, which is what a host sends for its own seats.</summary>
    [Fact]
    public void ACoopFitBindsOnTheFarMachineAsThePilotsOwnRecordBindsAtHome()
    {
        var plane = new OwnedPlane { Name = "Gypsy Magic", Ammo = new[] { 3, 4, 1, 0 }, Ordnance = new[] { 3, 3, 0, 0, 11, 3, 0, 0 } };
        var wire = Net.CoopFit.Of(plane.Ammo, plane.Ordnance);
        var stockFit = Stock.For("pdevastator")!;

        var home = CampaignLoadout.For(plane, Stock).ApplyTo(stockFit);
        var far = CampaignLoadout.For(wire, Stock)!.ApplyTo(stockFit);

        Assert.Equal(home.Guns.Select(g => g.Ammo), far.Guns.Select(g => g.Ammo));
        Assert.Equal(home.Hardpoints!.Stock, far.Hardpoints!.Stock);
        Assert.Equal("magnesium", far.Guns[0].Ammo);
        Assert.Equal(wire, CampaignLoadout.FitOf(CampaignLoadout.For(plane, Stock), Stock));

        // ABLE-TO-FAIL CONTROL: a stock fit on the wire binds nothing, so the seat flies its base.
        Assert.Null(CampaignLoadout.For(default(Net.CoopFit), Stock));
        Assert.NotEqual(stockFit.Guns.Select(g => g.Ammo), far.Guns.Select(g => g.Ammo));
    }

    [Fact]
    public void AFreshProfileFliesTheWingmanInTheSecondStarter()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        Assert.Equal(0, profile.SelectedPlane);
        Assert.Equal(1, profile.WingmanPlane);
        Assert.Equal("Gypsy Magic", profile.Planes[profile.SelectedPlane].Name);
        Assert.Equal("The Knave", profile.Planes[profile.WingmanPlane].Name);
    }

    // The physical pylons a fit hangs, in the array's own fill order: an entry left empty holds its
    // index open and hangs nothing, which is what keeps the later entries on their own wing.
    private static int[] Pylons(LoadoutDef def) =>
        Occupied(def).Select(i => Loadout.PylonFillOrder[i]).ToArray();

    private static string[] Carried(LoadoutDef def) =>
        Occupied(def).Select(i => def.Hardpoints!.Stock[i]).ToArray();

    private static IEnumerable<int> Occupied(LoadoutDef def) =>
        Enumerable.Range(0, def.Hardpoints?.Count ?? 0)
            .Where(i => def.Hardpoints!.Stock[i] != LoadoutChoice.None);
}
