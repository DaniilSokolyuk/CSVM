using System.Linq;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The typed <c>BALLISTICS</c> reader (<c>docs/formats/weapons.md</c>): scalar typing, the
/// key-paired-with-null class flags, the nested <c>FIRE</c>/<c>FLYOUT</c>/<c>IMPACT</c>
/// bindings, and the unhandled-key tripwire. Input is <c>fixtures/zrdr/weapons.json</c>.
/// </summary>
[Trait("Tier", "Quick")]
public class WeaponDefsTests
{
    [Fact]
    public void EveryBallisticsEntryIsIndexedByIdAndKeptInFileOrder()
    {
        var defs = Load();
        Assert.Equal(3, defs.All.Count);
        Assert.Equal("wep_probe_gun", defs.All[0].Id);
        Assert.Equal("wep_probe_seeker", defs.All[2].Id);
        Assert.NotNull(defs.Get("WEP_PROBE_GUN")); // id lookup is case-insensitive
        Assert.Null(defs.Get("wep_absent"));
    }

    [Fact]
    public void TheEmptyClipSoundComesFromTheFileRootNotTheDefault()
    {
        Assert.Equal("snd_probe_emptyclip", Load().EmptyClipSound);
    }

    [Fact]
    public void ScalarsAreTypedAndCountsNarrowedToInt()
    {
        var gun = Load().Get("wep_probe_gun")!;
        Assert.Equal(30, gun.Caliber);
        Assert.Equal(200, gun.ClusterSize);
        Assert.Equal(400, gun.AmmoLimit);
        Assert.Equal(8.5f, gun.FireRate);
        Assert.Equal(610f, gun.Velocity);
        Assert.Equal(1.5f, gun.CannonSpread);
        Assert.Equal(0f, gun.Gravity); // present-and-zero, not absent
        Assert.Null(gun.Acceleration); // absent stays null
    }

    [Fact]
    public void AKeyPairedWithNullReadsAsASetFlag()
    {
        var defs = Load();
        var gun = defs.Get("wep_probe_gun")!;
        Assert.True(gun.IsCannon);
        Assert.False(gun.IsRocket);

        var seeker = defs.Get("wep_probe_seeker")!;
        Assert.True(seeker.IsRocket);
        Assert.True(seeker.Targetable);
        Assert.True(seeker.Torpedo);
        Assert.True(seeker.Rear);
        Assert.True(seeker.Sonic);
        Assert.True(seeker.Flash);
        Assert.True(seeker.BeeperSeeker);
        Assert.True(seeker.DamagesZeppelin);
        Assert.False(seeker.Crater);
    }

    [Fact]
    public void GunClassificationFollowsCannonOrCaliber()
    {
        var defs = Load();
        Assert.True(defs.Get("wep_probe_gun")!.IsGun);
        Assert.False(defs.Get("wep_probe_rocket")!.IsGun);
    }

    [Fact]
    public void GuidanceIsReadFromTurnRateNotFromLockOn()
    {
        // The dumbfire sentinel is 0.001 and a steering rocket is ~1.25; LOCK_ON is present on
        // both kinds, so it cannot be the discriminator.
        var defs = Load();
        var dumbfire = defs.Get("wep_probe_rocket")!;
        Assert.Equal(0.001f, dumbfire.TurnRate);
        Assert.NotNull(dumbfire.LockOn);
        Assert.False(dumbfire.IsGuided);
        Assert.True(defs.Get("wep_probe_seeker")!.IsGuided);
    }

    [Fact]
    public void LockOnLeadIsReadAsAPair()
    {
        var rocket = Load().Get("wep_probe_rocket")!;
        Assert.Equal((0.2f, 0.8f), rocket.LockOnLead);
    }

    [Fact]
    public void FireAndFlyoutBindingsAreTypedAndPartialSlotsStayNull()
    {
        var defs = Load();
        var gun = defs.Get("wep_probe_gun")!;
        Assert.Equal("probe_muzzle", gun.Fire!.Animation);
        Assert.Equal("snd_probe_fire", gun.Fire.Sound);
        Assert.Null(gun.Fire.Effect);
        Assert.Null(gun.Flyout);

        var rocket = defs.Get("wep_probe_rocket")!;
        Assert.Equal("probe_rocket.flt", rocket.Flyout!.Model);
        Assert.Equal("probe_rocket_trail", rocket.Flyout.ModelAnimation);
        Assert.Equal("snd_probe_flyout", rocket.Flyout.Sound);
    }

    [Fact]
    public void ImpactIsIndexedBySurfaceIdAndAnUnnamedIdInheritsTheDefaultRow()
    {
        var gun = Load().Get("wep_probe_gun")!;
        // One row per registry slot, in slot order, the original's own array shape.
        Assert.Equal(SurfaceRegistry.Names.Count, gun.Impact.Length);
        Assert.Equal("probe_spark", gun.Impact[SurfaceRegistry.Default]!.Effect);
        Assert.Equal("probe_splash", gun.Impact[SurfaceRegistry.Water]!.SurfaceAnimation);
        // "enemy" is present in the data with a null body: a named id that binds nothing, which is
        // the one case the parse-time copy does NOT reach.
        Assert.Null(gun.ImpactFor(SurfaceRegistry.Enemy));
        // An id the fixture never names inherits the `default` row whole, sound included,
        // FUN_005ad630's miss arm. `dirt`(13) is the case with consequences in the shipped data.
        Assert.Equal("probe_spark", gun.ImpactFor(13)!.Effect);
        Assert.Equal("snd_probe_hit", gun.ImpactFor(13)!.Sound);
        Assert.Equal("probe_spark", gun.ImpactFor(SurfaceRegistry.Buildings)!.Effect);
        // So every slot but the named-and-empty one is filled.
        Assert.Equal(SurfaceRegistry.Names.Count - 1, gun.Impact.Count(row => row != null));
        // Out of range answers null rather than faulting: a bounds test of ours, since the original
        // indexes its row array unchecked and is never handed an id off the registry.
        Assert.Null(gun.ImpactFor(-1));
        Assert.Null(gun.ImpactFor(SurfaceRegistry.Names.Count));
    }

    /// <summary>A weapon that names only <c>default</c> answers that row at every id, the rocket
    /// fixture's shape, and the shipped HE/AP rockets' shape too (they name
    /// <c>default</c>/<c>water</c>/<c>buildings</c> and nothing else, so their burst is what a
    /// struck aircraft and a dirt tile both select).</summary>
    [Fact]
    public void AWeaponNamingOnlyDefaultAnswersThatRowEverywhere()
    {
        var rocket = Load().Get("wep_probe_rocket")!;

        Assert.All(rocket.Impact, row => Assert.Equal("probe_boom", row!.Effect));
    }

    [Fact]
    public void NestedSpecialStructsAreParsed()
    {
        var seeker = Load().Get("wep_probe_seeker")!;
        Assert.Equal(4f, seeker.BeeperTime);
        Assert.Equal(7f, seeker.SmokeScreenTime);
        Assert.Equal(2.5f, seeker.Tangler!.Time);
        Assert.Equal(18f, seeker.Tangler.Radius);
        Assert.Equal((3f, 9f), seeker.Tangler.EngineDead);
        Assert.Equal(12, seeker.FlyoutHealth);
        Assert.Equal("probe_seeker_die", seeker.DestroyAnimation);
    }

    [Fact]
    public void AnUnknownKeyIsReportedRatherThanSilentlyDropped()
    {
        var defs = Load();
        Assert.Equal(new[] { "PROBE_FUTURE_KEY" }, defs.Get("wep_probe_gun")!.UnhandledKeys);
        Assert.Empty(defs.Get("wep_probe_rocket")!.UnhandledKeys);
        Assert.Empty(defs.Get("wep_probe_seeker")!.UnhandledKeys);
    }

    [Fact]
    public void AnUnresolvedDescKeyStaysVisibleAsTheKeyItself()
    {
        // Load without a message table: the display name must not go blank.
        Assert.Equal("MSG_PROBE_GUN", Load().Get("wep_probe_gun")!.DisplayName);
    }

    private static WeaponDefs Load() => WeaponDefs.Load(TestData.Fixture("zrdr"));
}
