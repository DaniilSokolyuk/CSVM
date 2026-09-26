using System.IO;
using System.Linq;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Ammo Selection screen's pure core: the two dropdown rosters as the original orders them,
/// and the choice record's apply step. The record is keyed by slot identity, so the interesting
/// cases are the ones where the base does not have the shape the choice was built against, a
/// custom plane's fit, a stale pick for a slot that is gone.
/// </summary>
public class LoadoutChoiceTests
{
    private static string ConfigPath =>
        Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json");

    /// <summary>The gun dropdown, in the screenshot's order. "Explosive" is the magnesium round's
    /// screen name; the id stays the one StockLoadouts.GunWeaponId resolves.</summary>
    [Fact]
    public void TheGunDropdownIsTheOriginalsFiveRowsInOrder()
    {
        var options = StockLoadouts.Load(ConfigPath).Options;
        Assert.Equal(
            new[] { "slug", "dumdum", "ap", "magnesium", "none" },
            options.GunAmmo.Select(o => o.Id));
        Assert.Equal("Explosive", options.GunAmmo.Single(o => o.Id == "magnesium").Label);
    }

    /// <summary>The rocket dropdown's eleven weapons plus None, in the screen's own order, which
    /// is neither id order nor the wep_04–15 tier. The incendiary wep_04 is a named weapon the
    /// original does not offer, so its absence is the assertion that matters most here.</summary>
    [Fact]
    public void TheRocketDropdownIsElevenWeaponsPlusNoneAndExcludesTheIncendiary()
    {
        var options = StockLoadouts.Load(ConfigPath).Options;
        Assert.Equal(
            new[]
            {
                "wep_05", "wep_06", "wep_07", "wep_08", "wep_09", "wep_15",
                "wep_13", "wep_12", "wep_10", "wep_11", "wep_14", "none",
            },
            options.PylonOrdnance.Select(o => o.Id));
        Assert.DoesNotContain(options.PylonOrdnance, o => o.Id == "wep_04");
    }

    /// <summary>No pick at all leaves the base's own fit standing.</summary>
    [Fact]
    public void AnEmptyChoiceAppliesAsTheBase()
    {
        var choice = new LoadoutChoice();
        var applied = choice.ApplyTo(Devastator());

        Assert.True(choice.IsStock);
        Assert.Equal(new[] { "slug", "slug", "slug" }, applied.Guns.Select(g => g.Ammo));
        Assert.Equal(new[] { "wep_06", "wep_06", "wep_06", "wep_06" }, applied.Hardpoints!.Stock);
    }

    /// <summary>Applying never writes through to the base, which the menu keeps handing back.</summary>
    [Fact]
    public void ApplyingDoesNotMutateTheBase()
    {
        var stock = Devastator();
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(1, "ap");
        choice.SetPylon(1, "wep_14");
        choice.ApplyTo(stock);

        Assert.Equal("slug", stock.Guns[0].Ammo);
        Assert.Equal("wep_06", stock.Hardpoints!.Stock[0]);
    }

    /// <summary>A pylon pick is keyed by the physical pylon number, so it lands on the fill-order
    /// entry that binds to that pylon, index 1 is pylon 5, not pylon 2.</summary>
    [Fact]
    public void APylonPickLandsOnItsPhysicalPylonNotItsArrayIndex()
    {
        var choice = new LoadoutChoice();
        choice.SetPylon(5, "wep_14");
        var applied = choice.ApplyTo(Devastator());

        Assert.Equal(new[] { "wep_06", "wep_14", "wep_06", "wep_06" }, applied.Hardpoints!.Stock);
    }

    /// <summary>A saved record's wing cell lands on the pylon that wing actually hangs: the
    /// Devastator's four-pylon fit puts 1 and 5 to port, so the left wing's second cell is pylon 5.
    /// Reading the cell as the wing array's second entry named pylon 3, which this fit does not
    /// hang, and the pick was dropped.</summary>
    [Fact]
    public void AWingCellLandsOnThePylonThatWingHangs()
    {
        var choice = new LoadoutChoice();
        choice.SetWingCell(1, "wep_14");
        var applied = choice.ApplyTo(Devastator());

        Assert.Equal(new[] { "wep_06", "wep_14", "wep_06", "wep_06" }, applied.Hardpoints!.Stock);
    }

    /// <summary>A cell for a wing that hangs no such pylon is dropped, the way a pylon pick for a
    /// mount the base lacks is: the two-pylon fit's pair is 1 and 5, both to port, so its right
    /// wing has no first pylon to carry the fifth cell.</summary>
    [Fact]
    public void AWingCellTheFitHasNoPylonForIsDropped()
    {
        var stock = Devastator();
        stock.Hardpoints = new HardpointSpec { Count = 2, Stock = new[] { "wep_06", "wep_06" } };
        var choice = new LoadoutChoice();
        choice.SetWingCell(1, "wep_14");
        choice.SetWingCell(4, "wep_07");

        Assert.Equal(new[] { "wep_06", "wep_14" }, choice.ApplyTo(stock).Hardpoints!.Stock);
    }

    /// <summary>A pylon pick names its mount outright, so it wins over the cell that resolves onto
    /// the same pylon.</summary>
    [Fact]
    public void APylonPickWinsOverAWingCellOnTheSamePylon()
    {
        var choice = new LoadoutChoice();
        choice.SetWingCell(1, "wep_14");
        choice.SetPylon(5, "wep_07");

        Assert.Equal("wep_07", choice.ApplyTo(Devastator()).Hardpoints!.Stock[1]);
    }

    /// <summary>A None pylon keeps its entry as the sentinel rather than shortening the array:
    /// dropping it would slide every later pylon onto the other wing.</summary>
    [Fact]
    public void ANonePylonKeepsItsEntrySoTheLaterPylonsDoNotMove()
    {
        var choice = new LoadoutChoice();
        choice.SetPylon(1, LoadoutChoice.None);
        var applied = choice.ApplyTo(Devastator());

        Assert.Equal(4, applied.Hardpoints!.Count);
        Assert.Equal(new[] { "none", "wep_06", "wep_06", "wep_06" }, applied.Hardpoints.Stock);
    }

    /// <summary>A None gun group is omitted outright, so it never occupies a slot in the gun
    /// cycle the way a zero-ammo group would.</summary>
    [Fact]
    public void ANoneGunGroupIsOmittedFromTheFit()
    {
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(2, LoadoutChoice.None);
        var applied = choice.ApplyTo(Devastator());

        Assert.Equal(new[] { 1, 3 }, applied.Guns.Select(g => g.Slot));
    }

    /// <summary>Everything set to None is a legal fit: the original offers it, so an unarmed
    /// take-off is a choice rather than a bug.</summary>
    [Fact]
    public void EverythingNoneLeavesAnUnarmedFit()
    {
        var choice = new LoadoutChoice();
        for (int slot = 1; slot <= LoadoutChoice.MaxGunSlot; slot++)
        {
            choice.SetGunAmmo(slot, LoadoutChoice.None);
        }

        for (int pylon = 1; pylon <= LoadoutChoice.MaxPylon; pylon++)
        {
            choice.SetPylon(pylon, LoadoutChoice.None);
        }

        var applied = choice.ApplyTo(Devastator());
        Assert.Empty(applied.Guns);
        Assert.All(applied.Hardpoints!.Stock, s => Assert.Equal("none", s));
    }

    /// <summary>A pick for a slot the base does not carry is dropped, not an error. This is the
    /// case a BL-354 custom plane produces: a fit built against a four-gun airframe applied to a
    /// three-gun one.</summary>
    [Fact]
    public void APickForASlotTheBaseLacksIsDropped()
    {
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(4, "ap");
        choice.SetPylon(8, "wep_14");
        var applied = choice.ApplyTo(Devastator());

        Assert.Equal(3, applied.Guns.Count);
        Assert.Equal(4, applied.Hardpoints!.Stock.Length);
        Assert.DoesNotContain("wep_14", applied.Hardpoints.Stock);
    }

    /// <summary>A turret is not on the screen, so a stale pick for its slot leaves it alone.</summary>
    [Fact]
    public void ATurretSlotIgnoresAPick()
    {
        var stock = Devastator();
        stock.Guns.Add(new GunSpec { Slot = 4, Mount = "Rear Turret", Caliber = 30, Ammo = "slug", Turret = true });
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(4, LoadoutChoice.None);
        var applied = choice.ApplyTo(stock);

        var turret = Assert.Single(applied.Guns, g => g.Turret);
        Assert.Equal("slug", turret.Ammo);
    }

    /// <summary>A picked ammo resolves through caliber + ammo, so an inherited explicit weapon id
    /// is cleared, Loadout.Bind prefers WeaponId and would otherwise ignore the pick.</summary>
    [Fact]
    public void APickedAmmoClearsAnInheritedExplicitWeaponId()
    {
        var stock = Devastator();
        stock.Guns[0].WeaponId = "wep_130";
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(1, "ap");

        Assert.Null(choice.ApplyTo(stock).Guns[0].WeaponId);

        // With no pick for that slot the explicit id survives, which is what an AI def needs.
        Assert.Equal("wep_130", new LoadoutChoice().ApplyTo(stock).Guns[0].WeaponId);
    }

    /// <summary>Reset is a clear, not a rebuild, which is why null means "as the base authored
    /// it" rather than a value of its own.</summary>
    [Fact]
    public void ResetToStockClearsEveryPick()
    {
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(1, "ap");
        choice.SetPylon(5, LoadoutChoice.None);
        Assert.False(choice.IsStock);

        choice.ResetToStock();

        Assert.True(choice.IsStock);
        Assert.Null(choice.GunAmmoFor(1));
        Assert.Null(choice.PylonFor(5));

        // A record's cells are picks too, so a fit carrying only those is not stock either.
        choice.SetWingCell(0, "wep_07");
        Assert.False(choice.IsStock);
        choice.ResetToStock();
        Assert.True(choice.IsStock);
        Assert.Null(choice.WingCellFor(0));
    }

    /// <summary>The Devastator as stock_loadouts.json authors it: three gun slots and four
    /// pylons, which is exactly what the original's screen draws for it.</summary>
    private static LoadoutDef Devastator() => new()
    {
        Def = "pdevastator",
        Model = "player_pfighter",
        Display = "Devastator",
        Guns =
        {
            new GunSpec { Slot = 1, Mount = "Low Inner Wing Guns", Caliber = 50, Ammo = "slug", Markers = { "firepoint7", "firepoint8" } },
            new GunSpec { Slot = 2, Mount = "Low Outer Wing Guns", Caliber = 40, Ammo = "slug", Markers = { "firepoint5", "firepoint6" } },
            new GunSpec { Slot = 3, Mount = "Upper Inner Wing Guns", Caliber = 30, Ammo = "slug", Markers = { "firepoint3", "firepoint4" } },
        },
        Hardpoints = new HardpointSpec { Count = 4, Stock = new[] { "wep_06", "wep_06", "wep_06", "wep_06" } },
    };
}
