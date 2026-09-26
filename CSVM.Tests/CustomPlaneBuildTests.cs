using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// D32's join: a saved <see cref="CustomPlaneDef"/> onto the three things a spawn consumes. The
/// cases that matter are the ones the record cannot state on its own, which firepoints a twin
/// mount owns, which pylons a wing count hangs on, and what a count above the stock fit does,
/// plus the guarantee that the Ammo Selection layer still composes over the result.
/// </summary>
public class CustomPlaneBuildTests : IDisposable
{
    // The airframe id the hub-built cases fly, the same one the shipped Blue Streak record names.
    private const int Bloodhawk = 3;

    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public CustomPlaneBuildTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-custom-build-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
    }

    private static StockLoadouts Stock =>
        StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Calibre row c is caliber 30 + 10c, and the id stays unresolved: the whole point of
    /// leaving WeaponId null is that Loadout.Bind resolves calibre + ammo, so a later ammo pick
    /// still reaches the right member of the wep_30..73 matrix.</summary>
    [Fact]
    public void ACalibreRowBecomesItsCaliberAndNeverAResolvedWeaponId()
    {
        var def = Def();
        for (int row = 0; row <= CustomPlaneDef.MaxCalibre; row++)
        {
            def.Guns[0] = new GunChoice(row, Twin: false);
            var gun = Build(def, "pdevastator").Guns.Single(g => g.Slot == 1);

            Assert.Equal(30 + (10 * row), gun.Caliber);
            Assert.Null(gun.WeaponId);
            Assert.Equal("slug", gun.Ammo);
        }
    }

    /// <summary>Slot n owns firepoint(9-2n) and firepoint(10-2n): a twin is one gun over both, a
    /// single takes the low one. Never a second gun instance and never a second def.</summary>
    [Fact]
    public void ATwinTakesBothFirepointsAndASingleTheLowOne()
    {
        var def = Def();
        def.Guns[0] = new GunChoice(0, Twin: true);
        def.Guns[1] = new GunChoice(1, Twin: false);
        def.Guns[2] = new GunChoice(2, Twin: true);
        def.Guns[3] = new GunChoice(3, Twin: false);

        var built = Build(def, "pdevastator");

        Assert.Equal(new[] { "firepoint7", "firepoint8" }, built.Guns.Single(g => g.Slot == 1).Markers);
        Assert.Equal(new[] { "firepoint5" }, built.Guns.Single(g => g.Slot == 2).Markers);
        Assert.Equal(new[] { "firepoint3", "firepoint4" }, built.Guns.Single(g => g.Slot == 3).Markers);
        Assert.Equal(new[] { "firepoint1" }, built.Guns.Single(g => g.Slot == 4).Markers);
    }

    /// <summary>The Kestrel has no firepoint8 and its stock slot 1 says so by naming one marker.
    /// A twin pick there must take the one that exists: binding an absent marker is a loud throw,
    /// so this is the difference between a Kestrel that flies and one that spawns unarmed.</summary>
    [Fact]
    public void ASingleBarrelStockSlotNarrowsATwinPickToTheMarkerTheRigHas()
    {
        var def = Def();
        def.Guns[0] = new GunChoice(4, Twin: true);

        Assert.Equal(new[] { "firepoint7" }, Build(def, "pkestrel").Guns.Single(g => g.Slot == 1).Markers);
    }

    /// <summary>An empty slot (the dropdown's id 5) is an omitted slot, not a slot with no
    /// rounds: it never occupies a place in the gun cycle.</summary>
    [Fact]
    public void AnEmptySlotIsOmittedEntirely()
    {
        var def = Def();
        def.Guns[1] = new GunChoice(2, Twin: false);

        var built = Build(def, "pdevastator");

        Assert.Equal(new[] { 2 }, built.Guns.Select(g => g.Slot));
    }

    /// <summary>Turret is the stock slot's flag, never the record's: the record has no turret
    /// field, and the flag only selects the price column in the hangar.</summary>
    [Fact]
    public void TheTurretFlagComesFromTheStockSlot()
    {
        var def = Def();
        def.Guns[0] = new GunChoice(0, Twin: true);
        def.Guns[3] = new GunChoice(0, Twin: true);

        var built = Build(def, "pbalmoral");

        Assert.False(built.Guns.Single(g => g.Slot == 1).Turret);
        Assert.True(built.Guns.Single(g => g.Slot == 4).Turret);
        Assert.Equal("Rear Turret", built.Guns.Single(g => g.Slot == 4).Mount);
    }

    /// <summary>A wing's count hangs that wing's own pylons, outermost first. The halves
    /// interleave across the centreline, so two per wing is 1 and 3 to port with 2 and 4 to
    /// starboard, listed here in the array's fill order.</summary>
    [Fact]
    public void EachWingsCountHangsThatWingsOutermostPylons()
    {
        var def = Def();
        def.LeftHardpoints = 2;
        def.RightHardpoints = 2;

        Assert.Equal(new[] { 1, 2, 3, 4 }, PylonsOf(def, "pbalmoral"));
    }

    /// <summary>An asymmetric build hangs what each wing asks for and nothing on the other: three
    /// to port is 1, 3 and 5, all of them odd, with no starboard pylon at all.</summary>
    [Fact]
    public void TheTwoWingCountsAreIndependent()
    {
        var def = Def();
        def.LeftHardpoints = 3;
        def.RightHardpoints = 0;

        Assert.Equal(new[] { 1, 5, 3 }, PylonsOf(def, "pbalmoral"));
    }

    /// <summary>The record's counts alone open the pylons. The airframe's stock fit is the AI's
    /// and Instant Action's, and bounds nothing here: the Bloodhawk's three-pylon fit does not
    /// stop a 4/4 build flying eight.</summary>
    [Fact]
    public void ACountAboveTheStockFitStillHangsWhatWasBought()
    {
        var def = Def();
        def.LeftHardpoints = CustomPlaneDef.MaxHardpointsPerWing;
        def.RightHardpoints = CustomPlaneDef.MaxHardpointsPerWing;

        Assert.Equal(new[] { 1, 5, 2, 6, 3, 7, 4, 8 }, PylonsOf(def, "pbloodhawk"));
    }

    /// <summary>The reported case: a Devastator bought with three hardpoints a side flies six,
    /// not the four its stock fit authors.</summary>
    [Fact]
    public void ThreeAWingOnTheDevastatorHangsSix()
    {
        var def = Def();
        def.LeftHardpoints = 3;
        def.RightHardpoints = 3;

        Assert.Equal(new[] { 1, 5, 2, 6, 3, 4 }, PylonsOf(def, "pdevastator"));
    }

    /// <summary>No hardpoints bought is no hardpoint block at all, not eight empty ones.</summary>
    [Fact]
    public void NoHardpointsBoughtHangsNothing()
    {
        Assert.Null(Build(Def(), "pbalmoral").Hardpoints);
    }

    /// <summary>Every hung pylon carries high explosive, what the hangar's commit writes inside a
    /// wing's count. One a side is pylons 1 and 2, which sit at fill-order indices 0 and 2, so the
    /// unchosen index between them keeps the sentinel rather than sliding pylon 2 down.</summary>
    [Fact]
    public void EveryHungPylonCarriesHighExplosive()
    {
        var def = Def();
        def.LeftHardpoints = 1;
        def.RightHardpoints = 1;

        var hp = Build(def, "pwarhawk").Hardpoints!;

        Assert.Equal(new[] { "wep_06", LoadoutChoice.None, "wep_06" }, hp.Stock);
        Assert.Equal(3, hp.Count);
    }

    /// <summary>The Ammo Selection layer composes over the built def unchanged: the picks are
    /// keyed by slot identity, so they apply to a base they were never built against.</summary>
    [Fact]
    public void TheAmmoLayerComposesOverTheBuiltDef()
    {
        var def = Def();
        def.Guns[0] = new GunChoice(4, Twin: true);
        def.Guns[1] = new GunChoice(0, Twin: true);
        def.LeftHardpoints = 1;
        def.RightHardpoints = 1;
        var choice = new LoadoutChoice();
        choice.SetGunAmmo(1, "ap");
        choice.SetGunAmmo(2, LoadoutChoice.None);
        choice.SetPylon(2, "wep_14");

        var applied = choice.ApplyTo(Build(def, "pdevastator"));

        var gun = applied.Guns.Single();
        Assert.Equal(1, gun.Slot);
        Assert.Equal(70, gun.Caliber);
        Assert.Equal("wep_72", StockLoadouts.GunWeaponId(gun.Caliber, gun.Ammo));
        Assert.Equal(new[] { "wep_06", LoadoutChoice.None, "wep_14" }, applied.Hardpoints!.Stock);
    }

    /// <summary>The join the report is about: a pick for a pylon the airframe's stock fit never
    /// authors reaches it, because the rig the picks compose over is now as wide as the record's
    /// counts. The Devastator's stock fit stops at four; this lands a torpedo on the sixth.</summary>
    [Fact]
    public void APickReachesAPylonAboveTheStockFit()
    {
        var def = Def();
        def.LeftHardpoints = 3;
        def.RightHardpoints = 3;
        var choice = new LoadoutChoice();
        choice.SetPylon(6, "wep_14");

        var applied = choice.ApplyTo(Build(def, "pdevastator"));

        Assert.Equal("wep_14", applied.Hardpoints!.Stock[3]);
        Assert.Equal(6, Loadout.PylonFillOrder[3]);
    }

    /// <summary>A pick never reaches a pylon the build did not buy. One hardpoint a wing hangs 1
    /// and 2, which sit either side of the empty entry pylon 5 holds open, and the Ammo Selection
    /// screen offers a row for all three because the airframe's own fit is three wide. The empty
    /// entry stays empty, so the aeroplane flies the two hardpoints it was priced for.</summary>
    [Fact]
    public void APickNeverReachesAnEmptyCellOfAHubBuiltPlane()
    {
        var built = HubBuild("Blue Streak", left: 1, right: 1);
        var choice = new LoadoutChoice();
        foreach (int pylon in new[] { 1, 5, 2 })
        {
            choice.SetPylon(pylon, "wep_07");
        }

        var applied = choice.ApplyTo(Build(built, "pbloodhawk"));

        Assert.Equal(new[] { "wep_07", LoadoutChoice.None, "wep_07" }, applied.Hardpoints!.Stock);
    }

    /// <summary>The plane keeps its airframe's def and model (it flies that aircraft) and takes
    /// the pilot's own name for display.</summary>
    [Fact]
    public void TheBuiltDefKeepsTheAirframeAndTakesTheCustomName()
    {
        var built = Build(Def(), "pfury");

        Assert.Equal("pfury", built.Def);
        Assert.Equal("player_fury", built.Model);
        Assert.Equal("Blue Streak", built.Display);
    }

    /// <summary>Armour units land on the named zone's ARMOUR pool at five per unit, the scale the
    /// shipped pools are already on; the zone's hit points are the def's, untouched.</summary>
    [Fact]
    public void ArmourUnitsLandOnTheZonesArmourPoolAtFivePerUnit()
    {
        var def = Def();
        def.ArmourNose = 12;
        def.ArmourTail = 0;
        def.ArmourLeftWing = 4;
        def.ArmourRightWing = 7;

        var parts = CustomPlaneBuild.ArmouredParts(StockParts(), def).ToDictionary(p => p.Name);

        Assert.Equal(60f, parts["nose"].MaxArmor);
        Assert.Equal(0f, parts["tail"].MaxArmor);
        Assert.Equal(20f, parts["leftwing"].MaxArmor);
        Assert.Equal(35f, parts["rightwing"].MaxArmor);
        Assert.All(parts.Values, p => Assert.Equal(20f, p.MaxHp));
    }

    /// <summary>A zone the record does not name is carried across as it stands: no invented
    /// zones, and an airframe modelling some other part keeps its own pool.</summary>
    [Fact]
    public void AZoneTheRecordDoesNotNameIsUntouched()
    {
        var parts = StockParts();
        parts.Add(new DestroyablePart { Name = "hull", MaxHp = 30f, MaxArmor = 30f });

        var built = CustomPlaneBuild.ArmouredParts(parts, Def());

        Assert.Equal(30f, built.Single(p => p.Name == "hull").MaxArmor);
    }

    /// <summary>The stats' parts are cached and shared by every plane of that airframe, so the
    /// build must copy rather than overwrite: a custom plane must not repaint the stock one.</summary>
    [Fact]
    public void TheAirframesOwnPartsAreNeverMutated()
    {
        var parts = StockParts();
        var def = Def();
        def.ArmourNose = 12;

        CustomPlaneBuild.ArmouredParts(parts, def);

        Assert.Equal(20f, parts.Single(p => p.Name == "nose").MaxArmor);
    }

    /// <summary>Vehicle totals are the sum over zones, so buying armour raises the hull pool with
    /// them, the original recomputes the totals on every zone write and never keeps them
    /// independently.</summary>
    [Fact]
    public void TheVehicleArmourTotalIsTheSumOverTheBoughtZones()
    {
        var def = Def();
        def.ArmourNose = 12;
        def.ArmourTail = 12;
        def.ArmourLeftWing = 12;
        def.ArmourRightWing = 12;

        var damage = new PlaneDamage(CustomPlaneBuild.ArmouredParts(StockParts(), def));

        Assert.Equal(240f, damage.WholeArmorMax);
        Assert.Equal(80f, damage.WholeHealthMax);
    }

    /// <summary>The paint carries the pattern, the three resolved colours and the three decals,
    /// which are the same 0-49 index space vehicle.json's paint_decalN uses.</summary>
    [Fact]
    public void ThePaintCarriesThePatternColoursAndDecals()
    {
        var def = Def();
        def.LoadPatternDefaults(4); // fortune: red, darkest, white
        def.NoseDecal = 40;
        def.TailDecal = 8;
        def.WingDecal = 7;

        var scheme = CustomPlaneBuild.PaintFor(def, "hughes");

        Assert.Equal("hughes", scheme.Pattern);
        Assert.Equal(PaintScheme.FromBytes(223, 0, 41), scheme.Color1);
        Assert.Equal(PaintScheme.FromBytes(25, 25, 25), scheme.Color2);
        Assert.Equal(PaintScheme.FromBytes(255, 255, 255), scheme.Color3);
        Assert.Equal(40, scheme.NoseDecal);
        Assert.Equal(8, scheme.TailDecal);
        Assert.Equal(7, scheme.WingDecal);
    }

    /// <summary>A build that chose no decal keeps the shipped placeholder on every slot, since a
    /// fresh plane has no authored decal to inherit (the pattern defaults carry none).</summary>
    [Fact]
    public void AFreshBuildKeepsThePlaceholderDecals()
    {
        var scheme = CustomPlaneBuild.PaintFor(Def(), "fortune");

        Assert.Equal(-1, scheme.NoseDecal);
        Assert.Equal(-1, scheme.TailDecal);
        Assert.Equal(-1, scheme.WingDecal);
    }

    /// <summary>The engine pick resolves to its authored engines.json tier row: the Fury's Lvl-3
    /// (registry base 16 + tier 2 = row 18, 0.755), the same tier through a nitrous id, and the
    /// stock pick (id 6) yielding null so the airframe's own row stands. Nitrous itself changes
    /// nothing here; its flight effect is untraced.</summary>
    [ExtractedDataFact]
    public void TheEnginePickResolvesToItsAuthoredRegistryRow()
    {
        string zrdr = Path.Combine(TestData.ExtractedRoot!, "zrdr");
        var def = Def();
        def.Airframe = 7;

        def.Engine = 2;
        Assert.Equal(0.755f, CustomPlaneBuild.EnginePowerFor(zrdr, def)!.Value, 3);
        def.Engine = 5;
        Assert.Equal(0.755f, CustomPlaneBuild.EnginePowerFor(zrdr, def)!.Value, 3);
        def.Engine = 0;
        Assert.Equal(0.45f, CustomPlaneBuild.EnginePowerFor(zrdr, def)!.Value, 3);
        def.Engine = CustomPlaneDef.EngineNone;
        Assert.Null(CustomPlaneBuild.EnginePowerFor(zrdr, def));
    }

    /// <summary>Every airframe's registry base row exists in engines.json, so no pick can read a
    /// missing row (the map is FUN_00416e10's, base 0x0a stepping by 3).</summary>
    [ExtractedDataFact]
    public void EveryAirframesRegistryBaseRowExists()
    {
        string zrdr = Path.Combine(TestData.ExtractedRoot!, "zrdr");
        var def = Def();
        for (int airframe = 0; airframe <= CustomPlaneDef.MaxAirframe; airframe++)
        {
            def.Airframe = airframe;
            for (int engine = 0; engine < CustomPlaneDef.EngineNone; engine++)
            {
                def.Engine = engine;
                Assert.NotNull(CustomPlaneBuild.EnginePowerFor(zrdr, def));
            }
        }
    }

    // The airframe id itself is unread by the join: the base fit is handed in, so which airframe
    // this build sits on is whichever def the Build/PylonsOf helper names.
    private static CustomPlaneDef Def() => new() { Name = "Blue Streak" };

    private static LoadoutDef Build(CustomPlaneDef def, string airframeDef) =>
        CustomPlaneBuild.LoadoutFor(def, Stock.For(airframeDef)!);

    private static int[] PylonsOf(CustomPlaneDef def, string airframeDef)
    {
        var hp = Build(def, airframeDef).Hardpoints!;
        return Enumerable.Range(0, hp.Count)
            .Where(i => hp.Stock[i] != LoadoutChoice.None)
            .Select(i => Loadout.PylonFillOrder[i])
            .ToArray();
    }

    // The Bloodhawk's shipped zone allocation: four parts at 20 hp / 20 armour.
    private static List<DestroyablePart> StockParts() => new()
    {
        new DestroyablePart { Name = "nose", MaxHp = 20f, MaxArmor = 20f },
        new DestroyablePart { Name = "tail", MaxHp = 20f, MaxArmor = 20f },
        new DestroyablePart { Name = "leftwing", MaxHp = 20f, MaxArmor = 20f },
        new DestroyablePart { Name = "rightwing", MaxHp = 20f, MaxArmor = 20f },
    };

    // One plane built the way the Plane Construction hub builds one: a flow walked to the purchase
    // review and committed, read back out of the store the commit wrote it to. The airframe
    // defaults are declined, so the two counts are the only hardpoints the build pays for.
    private CustomPlaneDef HubBuild(string name, int left, int right)
    {
        var flow = new HangarFlow(_store, UiStrings.Empty);
        flow.Accept();
        flow.PickAirframe(Bloodhawk);
        flow.AnswerDefaultsAsk(false);
        flow.Scratch.Engine = 1;
        flow.Scratch.LeftHardpoints = left;
        flow.Scratch.RightHardpoints = right;
        flow.Scratch.Name = name;
        while (flow.Screen != HangarScreen.Purchase && flow.Advance())
        {
        }

        Assert.True(flow.Commit(), flow.Message);
        return Assert.Single(_store.List());
    }
}
