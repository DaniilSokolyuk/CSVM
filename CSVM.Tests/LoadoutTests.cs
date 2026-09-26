using System;
using System.IO;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The stock-loadout config (<c>docs/formats/loadouts.md</c>), committed engine data under
/// <c>CSVM/data/</c>, so these run without an extraction, plus the caliber+ammo weapon-id rule.
/// Binding a loadout to a plane needs live <c>Node3D</c>s and stays with the in-engine suites.
/// </summary>
[Trait("Tier", "Quick")]
public class LoadoutTests
{
    private static string ConfigPath =>
        Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json");

    [Theory]
    [InlineData(30, "slug", "wep_30")]
    [InlineData(30, "dumdum", "wep_31")]
    [InlineData(30, "ap", "wep_32")]
    [InlineData(30, "magnesium", "wep_33")]
    [InlineData(70, "slug", "wep_70")]
    public void AGunsWeaponIdIsItsCaliberPlusTheAmmoOffset(int caliber, string ammo, string expected)
    {
        // Each caliber's four ammo types sit at four consecutive wep ids.
        Assert.Equal(expected, StockLoadouts.GunWeaponId(caliber, ammo));
    }

    [Fact]
    public void AnUnknownAmmoNameFallsBackToTheStockSlugRatherThanShiftingCaliber()
    {
        Assert.Equal("wep_50", StockLoadouts.GunWeaponId(50, "not_an_ammo_type"));
        Assert.Equal("wep_50", StockLoadouts.GunWeaponId(50, "SLUG"));
    }

    [Fact]
    public void TheCommittedConfigCarriesTheElevenPlayerAirframes()
    {
        var loadouts = Load();
        Assert.Equal(11, loadouts.All.Count);
        Assert.NotNull(loadouts.For("pbloodhawk"));
        Assert.Null(loadouts.For("not_a_plane"));
    }

    [Fact]
    public void EveryLoadoutNamesAModelAndAtLeastOneGunGroup()
    {
        foreach (var (def, loadout) in Load().All)
        {
            Assert.Equal(def, loadout.Def);
            Assert.NotEqual("", loadout.Model);
            Assert.NotEqual("", loadout.Display);
            Assert.NotEmpty(loadout.Guns);
        }
    }

    [Fact]
    public void EveryGunSlotHasACaliberAndTheMarkersItFiresFrom()
    {
        foreach (var (def, loadout) in Load().All)
        {
            foreach (var gun in loadout.Guns)
            {
                Assert.InRange(gun.Slot, 1, 4);           // the W1-W4 mount slots
                Assert.NotEqual("", gun.Mount);
                Assert.True(gun.Caliber > 0, $"{def} slot {gun.Slot}: no caliber");
                Assert.NotEmpty(gun.Markers);
            }
        }
    }

    [Fact]
    public void PylonsAreDeclaredWithTheirStockOrdnance()
    {
        int withPylons = 0;
        foreach (var (_, loadout) in Load().All)
        {
            if (loadout.Hardpoints is not { } hp)
            {
                continue;
            }
            withPylons++;
            Assert.InRange(hp.Count, 1, 8);              // pylon1..pylonN, max 8 on the rig
            Assert.Equal(hp.Count, hp.Stock.Length);
            Assert.All(hp.Stock, stock => Assert.StartsWith("wep_", stock));
        }
        Assert.Equal(11, withPylons);
    }

    [Fact]
    public void MarkerNamesAreOnesTheRigCanClassify()
    {
        // A loadout naming a marker the model does not carry throws at bind time; the names
        // must at least be shaped like firepoints.
        foreach (var (def, loadout) in Load().All)
        {
            foreach (var gun in loadout.Guns)
            {
                foreach (var marker in gun.Markers)
                {
                    Assert.True(CSVM.Mech3.MarkerRig.Classify(marker, out var kind, out _),
                        $"{def} slot {gun.Slot}: '{marker}' is not a marker name");
                    Assert.Equal(CSVM.Mech3.MarkerRig.MarkerKind.Firepoint, kind);
                }
            }
        }
    }

    [Fact]
    public void PylonFillOrderAlternatesWings()
    {
        // The original fills hardpoints 1,5,2,6,3,7,4,8, both wings alternately,
        // not sequential 1..N. A partial stock fit (hp.Count < 8) takes this sequence's prefix.
        Assert.Equal(new[] { 1, 5, 2, 6, 3, 7, 4, 8 }, Loadout.PylonFillOrder);
    }

    [Theory]
    [InlineData(1, 1, 0)]   // pylon 1
    [InlineData(2, 2, 0)]   // pylons 1 and 5, both port
    [InlineData(3, 2, 1)]   // 1, 5 | 2
    [InlineData(4, 2, 2)]   // 1, 5 | 2, 6
    [InlineData(5, 3, 2)]   // 1, 3, 5 | 2, 6
    [InlineData(6, 4, 2)]   // 1, 3, 5, 7 | 2, 6
    [InlineData(7, 4, 3)]   // 1, 3, 5, 7 | 2, 4, 6
    [InlineData(8, 4, 4)]
    public void WingCountsSplitAFitOddToPortAndEvenToStarboard(int count, int left, int right)
    {
        // The rig pairs pylons across the centreline, so a fit's per-wing counts come off the
        // numbers it hangs, not off the fill order's halves, which would say 1/1 at count 2.
        var fit = new HardpointSpec { Count = count, Stock = Filled(count) };
        Assert.Equal((left, right), Loadout.WingCounts(fit));
    }

    [Fact]
    public void EveryWingCellNamesAPylonTheFitHangs()
    {
        // The counts and the cell join are one walk: every cell inside a wing's count resolves to
        // a pylon on that wing, and the first cell past the count resolves to none.
        for (int count = 1; count <= Loadout.PylonFillOrder.Length; count++)
        {
            var fit = new HardpointSpec { Count = count, Stock = Filled(count) };
            var (left, right) = Loadout.WingCounts(fit);
            for (int cell = 0; cell < left; cell++)
            {
                Assert.Contains(Loadout.PylonForCell(cell, fit), Loadout.LeftWingPylons);
            }
            for (int cell = 0; cell < right; cell++)
            {
                Assert.Contains(Loadout.PylonForCell(4 + cell, fit), Loadout.RightWingPylons);
            }
            if (left < Loadout.LeftWingPylons.Length)
            {
                Assert.Equal(0, Loadout.PylonForCell(left, fit));
            }
            Assert.Equal(0, Loadout.PylonForCell(4 + right, fit));
        }
    }

    [Fact]
    public void TheHopliteHangsBothPylonsToPortAndTheFirebrandFourToTwo()
    {
        // The two airframes whose stock count the fill-order halves are likeliest to split wrongly.
        // The totals, and so the prices, are the authored counts either way.
        var loadouts = Load();
        var hoplite = loadouts.For("pautogyro")!.Hardpoints;
        var firebrand = loadouts.For("pfirebrand")!.Hardpoints;

        Assert.Equal((2, 0), Loadout.WingCounts(hoplite));
        Assert.Equal((4, 2), Loadout.WingCounts(firebrand));
        Assert.Equal(hoplite!.Count, 2 + 0);
        Assert.Equal(firebrand!.Count, 4 + 2);
    }

    [Fact]
    public void EveryStockFitsWingCountsAddUpToWhatItAuthors()
    {
        foreach (var (def, loadout) in Load().All)
        {
            var (left, right) = Loadout.WingCounts(loadout.Hardpoints);
            Assert.True(left + right == loadout.Hardpoints!.Count,
                $"{def}: {left}+{right} against {loadout.Hardpoints.Count} authored pylons");
            Assert.InRange(left, 0, Loadout.LeftWingPylons.Length);
            Assert.InRange(right, 0, Loadout.RightWingPylons.Length);
        }
    }

    [Fact]
    public void ArmedIsTheOneSpellingAndHonoursInfiniteAmmo()
    {
        // Both slot faces answer through AmmoSlots.Armed: drained means not armed, unless
        // --infinite-ammo makes every slot count as armed; a loaded slot is armed either way.
        var gun = new GunGroup { Ammo = 0 };
        var pylon = new Hardpoint { Ammo = 0 };

        Assert.False(gun.Armed(infinite: false));
        Assert.False(pylon.Armed(infinite: false));
        Assert.True(gun.Armed(infinite: true));
        Assert.True(pylon.Armed(infinite: true));

        gun.Ammo = 1;
        pylon.Ammo = 1;
        Assert.True(gun.Armed(infinite: false));
        Assert.True(pylon.Armed(infinite: false));
    }

    private static StockLoadouts Load() => StockLoadouts.Load(ConfigPath);

    // A fit of N pylons, every one carrying the stock high explosive the hangar writes.
    private static string[] Filled(int count)
    {
        var stock = new string[count];
        Array.Fill(stock, Loadout.StockOrdnance);
        return stock;
    }
}
