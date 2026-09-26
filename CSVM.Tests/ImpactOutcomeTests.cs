using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="ImpactOutcome.Resolve"/>, the decision "what should happen when this weapon hits
/// this surface id", taken apart from performing it. Nothing here builds a scene: the rule cases run
/// on hand-built <see cref="WeaponDef"/>s, and the data cases read the shipped
/// <c>weapons.zrd.json</c> so the table lookup is checked against the real <c>IMPACT</c> shapes
/// (a row that is present but all-null, a row the reader skips, a row that binds only
/// <c>SURFACE_ANIMATION</c>, and the eight registry ids no weapon authors at all) rather than
/// against fixtures that agree with the reader by construction.
/// </summary>
public class ImpactOutcomeTests
{
    // The surface ids a round can actually strike in M3, a measurement rather than a choice:
    // analysis/surface-classification/FINDINGS.md's per-chapter area table plus `player`, which
    // `ProjectilePool.SurfaceIdOf` answers. A case for any other id would be invented coverage.
    private static readonly int[] ReachableSurfaceIds =
    {
        SurfaceRegistry.Default, SurfaceRegistry.Water, 5, 8, SurfaceRegistry.Player,
        SurfaceRegistry.Buildings, 12, 13,
    };

    private static string SharedZrdr =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    // ---- the stand-in ladder ------------------------------------------------------------------

    /// <summary>An instanced authored model beats every stand-in: when the effect name resolved to
    /// a real gamez node, the model IS the effect and nothing else draws.</summary>
    [Theory]
    [InlineData(SurfaceRegistry.Default)]
    [InlineData(SurfaceRegistry.Water)]
    [InlineData(SurfaceRegistry.Buildings)]
    public void AResolvedModelLeavesNoStandIn(int surfaceId)
    {
        var outcome = ImpactOutcome.Resolve(Gun(), surfaceId, modelResolved: true, hasEffectsRuntime: true);

        Assert.Equal(ImpactStandIn.None, outcome.StandIn);
    }

    /// <summary>Ground gets the single spark, gun or rocket alike, at every terrain id, it has no
    /// arm of its own since the chip burst was removed. The assertion that matters is that the
    /// answer is not <c>None</c>: <c>ProjectilePool</c> gates the world-effects sink (the
    /// <c>blacksmokepuffer</c>) on <c>StandIn != None</c>, so a terrain hit resolving to nothing
    /// would silently take the gunhit smoke with it.</summary>
    [Theory]
    [InlineData(SurfaceRegistry.Default)]
    [InlineData(13)]
    [InlineData(8)]
    public void ARoundOnGroundPicksTheSpark(int surfaceId)
    {
        Assert.Equal(ImpactStandIn.Spark,
            ImpactOutcome.Resolve(Gun(), surfaceId, false, hasEffectsRuntime: true).StandIn);
        Assert.Equal(ImpactStandIn.Spark,
            ImpactOutcome.Resolve(Rocket(), surfaceId, false, hasEffectsRuntime: true).StandIn);
    }

    /// <summary>A gun round on <c>buildings</c> whose row renders nowhere draws nothing, since the
    /// original leaves the unresolved slot empty; a bound name keeps the base spark, and a rocket
    /// on the same wall is untouched.</summary>
    [Fact]
    public void AGunOnABuildingWithAnUnboundRowDrawsNothing()
    {
        Assert.Equal(ImpactStandIn.None,
            ImpactOutcome.Resolve(Gun(), SurfaceRegistry.Buildings, false, hasEffectsRuntime: true).StandIn);
        Assert.Equal(ImpactStandIn.Spark,
            ImpactOutcome.Resolve(Gun(), SurfaceRegistry.Buildings, false, hasEffectsRuntime: true,
                effectBound: true).StandIn);
        Assert.Equal(ImpactStandIn.Spark,
            ImpactOutcome.Resolve(Rocket(), SurfaceRegistry.Buildings, false, hasEffectsRuntime: true).StandIn);
    }

    /// <summary>Water and a struck aircraft fall to the single spark. This is the existing
    /// else-branch, not a case authored for <c>player</c>/<c>enemy</c>.</summary>
    [Theory]
    [InlineData(SurfaceRegistry.Water)]
    [InlineData(SurfaceRegistry.Player)]
    [InlineData(SurfaceRegistry.Enemy)]
    public void EverySurfaceWithNoLookOfItsOwnFallsToTheSpark(int surfaceId)
    {
        Assert.Equal(ImpactStandIn.Spark,
            ImpactOutcome.Resolve(Gun(), surfaceId, false, hasEffectsRuntime: true).StandIn);
    }

    /// <summary>With no world-effects runtime to build its real fireball, a hardpoint weapon shows
    /// the explosion stand-in, and it outranks the surface's own look, so the blast is visible in a
    /// scene-less pool whatever it hit. A gun never takes that branch.</summary>
    [Theory]
    [InlineData(SurfaceRegistry.Default)]
    [InlineData(SurfaceRegistry.Water)]
    [InlineData(SurfaceRegistry.Buildings)]
    public void AHardpointWeaponWithNoEffectsRuntimeExplodesInstead(int surfaceId)
    {
        Assert.Equal(ImpactStandIn.Explosion,
            ImpactOutcome.Resolve(Rocket(), surfaceId, false, hasEffectsRuntime: false).StandIn);
        Assert.NotEqual(ImpactStandIn.Explosion,
            ImpactOutcome.Resolve(Gun(), surfaceId, false, hasEffectsRuntime: false).StandIn);
    }

    // ---- the damage numbers ------------------------------------------------------------------

    /// <summary>An authored radius is a damage blast only when the weapon also carries positive
    /// health damage; the flash/flare specials carry a large radius and no damage at all.</summary>
    [Fact]
    public void ARadiusIsABlastOnlyAlongsidePositiveDamage()
    {
        var rocket = new WeaponDef { Id = "r", IsRocket = true, HealthDamage = 60f, ImpactProximity = 15f };
        var flash = new WeaponDef { Id = "f", IsRocket = true, ImpactProximity = 450f };
        var contact = new WeaponDef { Id = "c", IsRocket = true, HealthDamage = 60f };

        Assert.True(ImpactOutcome.Resolve(rocket, SurfaceRegistry.Default, false, true).HasBlastDamage);
        Assert.False(ImpactOutcome.Resolve(flash, SurfaceRegistry.Default, false, true).HasBlastDamage);
        Assert.False(ImpactOutcome.Resolve(contact, SurfaceRegistry.Default, false, true).HasBlastDamage);
        Assert.Equal(450f, ImpactOutcome.Resolve(flash, SurfaceRegistry.Default, false, true).BlastRadius);
        Assert.Equal(0f, ImpactOutcome.Resolve(contact, SurfaceRegistry.Default, false, true).BlastRadius);
    }

    /// <summary>A weapon with no damage keys at all resolves to zeroes rather than to nulls a
    /// caller has to re-default.</summary>
    [Fact]
    public void AWeaponWithNoDamageKeysResolvesToZeroes()
    {
        var outcome = ImpactOutcome.Resolve(Gun(), SurfaceRegistry.Default, false, true);

        Assert.Equal(0f, outcome.Damage);
        Assert.Equal(0f, outcome.BlastRadius);
        Assert.False(outcome.HasBlastDamage);
    }

    // ---- the IMPACT table lookup, against the shipped data ------------------------------------

    /// <summary>The 30 cal slug's own table: <c>default</c>(0) and <c>water</c>(1) each bind a name
    /// and a sound, and <c>buildings</c>(11) binds a name with no <c>SOUND</c>, a row is taken
    /// whole, so the building hit is silent rather than borrowing the default's sound.</summary>
    [ExtractedDataFact]
    public void TheSlugsTableIsReadPerSurfaceIdAndNotMerged()
    {
        var slug = WeaponDefs.Load(SharedZrdr).Get("wep_00")!;

        var ground = ImpactOutcome.Resolve(slug, SurfaceRegistry.Default, false, true);
        Assert.Equal("3040slug_gunhit", ground.EffectName);
        Assert.Equal("snd_grnd_bullet", ground.Sound);

        var water = ImpactOutcome.Resolve(slug, SurfaceRegistry.Water, false, true);
        Assert.Equal("splash1.flt", water.EffectName);
        Assert.Equal("snd_water_bullet", water.Sound);

        var wall = ImpactOutcome.Resolve(slug, SurfaceRegistry.Buildings, false, true);
        Assert.Equal("bld_damage.flt", wall.EffectName);
        Assert.Null(wall.Sound);
    }

    /// <summary>An id the weapon names no block for resolves the <c>default</c> row whole, effect
    /// name and sound alike, <c>FUN_005ad630</c>'s miss arm copies row 0 over it at parse time
    /// (`0x005ae268`). <c>dirt</c>(13) is the case with consequences: no shipped weapon names it,
    /// and it is up to 10.2 % of a chapter's collidable ground, so without the copy the four ids
    /// below go silent on ground that visibly is not special.</summary>
    [ExtractedDataFact]
    public void AnIdTheWeaponNeverNamesResolvesTheDefaultRow()
    {
        var slug = WeaponDefs.Load(SharedZrdr).Get("wep_00")!;

        var ground = ImpactOutcome.Resolve(slug, SurfaceRegistry.Default, false, true);
        Assert.Equal("3040slug_gunhit", ground.EffectName);

        // fire(5), airstrip(8), dzone(12), dirt(13): the reachable ids no weapon names.
        foreach (var id in new[] { 5, 8, 12, 13 })
        {
            var outcome = ImpactOutcome.Resolve(slug, id, false, true);
            Assert.Equal(ground.EffectName, outcome.EffectName);
            Assert.Equal(ground.Sound, outcome.Sound);
        }
    }

    /// <summary>The rocket half of the same rule, which is what a player sees: the HE rocket names
    /// only <c>default</c>/<c>water</c>/<c>buildings</c>, so its ground burst is what fires on a
    /// dirt tile and on a struck aircraft, reported at the controls as "rockets do not explode on
    /// dirt or on other planes", and the reason the copy is not a detail.</summary>
    [ExtractedDataFact]
    public void TheHeRocketsBurstReachesDirtAndAStruckAircraft()
    {
        var boom = WeaponDefs.Load(SharedZrdr).Get("wep_06")!;

        foreach (var id in new[] { SurfaceRegistry.Default, 13, SurfaceRegistry.Player })
        {
            var outcome = ImpactOutcome.Resolve(boom, id, false, true);
            Assert.Equal("he_ground_effect", outcome.EffectName);
            Assert.Equal("snd_missile_explode", outcome.Sound);
        }
    }

    /// <summary>Naming an id and binding nothing on it is not the same as never naming it, and the
    /// 30 cal slug is the case that separates them: it names <c>player</c>(6) with every slot empty
    /// and <c>enemy</c>(7) with the data's "no effect on that surface" null, so both stay silent
    /// while its unnamed ids take the <c>default</c> row. A slug round on a plane therefore draws
    /// nothing authored even though a rocket's does. The flak rocket binds <c>player</c> outright,
    /// and that row is what a struck plane selects.</summary>
    [ExtractedDataFact]
    public void ANamedButEmptyAircraftRowStaysSilentWhileUnnamedIdsInherit()
    {
        var weapons = WeaponDefs.Load(SharedZrdr);
        var slug = weapons.Get("wep_00")!;

        Assert.Null(slug.ImpactFor(SurfaceRegistry.Player));
        Assert.Null(slug.ImpactFor(SurfaceRegistry.Enemy));
        // ...against an id it simply never names, which inherits.
        Assert.Equal("3040slug_gunhit", slug.ImpactFor(13)!.Animation);
        Assert.Null(ImpactOutcome.Resolve(slug, SurfaceRegistry.Player, false, true).EffectName);
        Assert.Null(ImpactOutcome.Resolve(slug, SurfaceRegistry.Enemy, false, true).EffectName);

        var flak = weapons.Get("wep_07")!;
        Assert.Equal("flak_effect", ImpactOutcome.Resolve(flak, SurfaceRegistry.Default, false, true).EffectName);
        Assert.Equal("flak_effectplayer", ImpactOutcome.Resolve(flak, SurfaceRegistry.Player, false, true).EffectName);
    }

    /// <summary>A row that binds only <c>SURFACE_ANIMATION</c>, the armour-piercing rocket's
    /// ground effect, resolves through it, since <c>ANIMATION</c> is preferred but optional.</summary>
    [ExtractedDataFact]
    public void ASurfaceAnimationStandsInForAMissingAnimation()
    {
        var armour = WeaponDefs.Load(SharedZrdr).Get("wep_05")!;

        var outcome = ImpactOutcome.Resolve(armour, SurfaceRegistry.Default, false, true);
        Assert.Equal("ap_ground_effect", outcome.EffectName);
        Assert.True(outcome.SurfaceOriented);
        Assert.Equal("snd_missile_pierce", outcome.Sound);
        Assert.Equal(40f, outcome.Damage);
        Assert.Equal(15f, outcome.BlastRadius);
    }

    /// <summary>The slot the name came from is carried out, because the original orients only the
    /// <c>SURFACE_ANIMATION</c> spawn to the struck normal: the choker's <c>scatter_effect</c> is a
    /// plain <c>ANIMATION</c> and reads unoriented; the HE rocket's ground effect is the other slot.</summary>
    [ExtractedDataFact]
    public void OnlyTheSurfaceAnimationSlotIsSurfaceOriented()
    {
        var weapons = WeaponDefs.Load(SharedZrdr);

        var choker = ImpactOutcome.Resolve(weapons.Get("wep_12")!, SurfaceRegistry.Default, false, true);
        Assert.Equal("scatter_effect", choker.EffectName);
        Assert.False(choker.SurfaceOriented);

        var he = ImpactOutcome.Resolve(weapons.Get("wep_06")!, SurfaceRegistry.Default, false, true);
        Assert.Equal("he_ground_effect", he.EffectName);
        Assert.True(he.SurfaceOriented);
    }

    /// <summary>The impact hook's mask strips the row piece by piece: <c>Sound</c> silences the
    /// sound (the choker's hook, the one the binary installs), <c>Animation</c> drops the
    /// <c>ANIMATION</c> slot and leaves a <c>SURFACE_ANIMATION</c> to stand, and the damage
    /// figures are under no bit.</summary>
    [ExtractedDataFact]
    public void TheHookMaskSilencesTheSoundAndDropsOnlyTheAnimationSlot()
    {
        var weapons = WeaponDefs.Load(SharedZrdr);
        var choker = weapons.Get("wep_12")!;
        Assert.Equal(ImpactHook.Tangler, choker.ImpactHook);
        Assert.All(weapons.All.Where(w => w.Id != "wep_12"), w => Assert.Equal(ImpactHook.None, w.ImpactHook));

        var silenced = ImpactOutcome.Resolve(choker, SurfaceRegistry.Default, false, true, ImpactSuppression.Sound);
        Assert.Null(silenced.Sound);
        Assert.Equal("scatter_effect", silenced.EffectName);

        var noAnimation = ImpactOutcome.Resolve(choker, SurfaceRegistry.Default, false, true, ImpactSuppression.Animation);
        Assert.Null(noAnimation.EffectName);
        Assert.Equal("snd_missile_choker", noAnimation.Sound);

        var slug = weapons.Get("wep_01")!; // its default row binds SURFACE_ANIMATION alone
        var kept = ImpactOutcome.Resolve(slug, SurfaceRegistry.Default, false, true, ImpactSuppression.Animation);
        Assert.Equal("3040slug_gunhit", kept.EffectName);
        Assert.True(kept.SurfaceOriented);

        var he = weapons.Get("wep_06")!;
        var all = ImpactOutcome.Resolve(he, SurfaceRegistry.Default, false, true,
            ImpactSuppression.Sound | ImpactSuppression.Effects | ImpactSuppression.Animation);
        Assert.Equal(he.HealthDamage, all.Damage);
        Assert.Equal(he.ImpactProximity, all.BlastRadius);
    }

    /// <summary>The one shipped weapon with no <c>IMPACT</c> block at all resolves to nothing
    /// bound, without the lookup faulting on the empty table.</summary>
    [ExtractedDataFact]
    public void TheWeaponWithNoImpactBlockResolvesToNothingBound()
    {
        var fake = WeaponDefs.Load(SharedZrdr).Get("wep_26")!;
        Assert.All(fake.Impact, row => Assert.Null(row));

        var outcome = ImpactOutcome.Resolve(fake, SurfaceRegistry.Default, false, true);
        Assert.Null(outcome.EffectName);
        Assert.Null(outcome.Sound);
        Assert.Equal(ImpactStandIn.Spark, outcome.StandIn);
    }

    // ---- the 48 weapons x the reachable surfaces ---------------------------------------------

    /// <summary>The rule, not a snapshot: every shipped weapon at every reachable surface id
    /// resolves an outcome coherent by three checks, never a table of expected per-row values that
    /// breaks on the next weapon-polish change without catching anything.
    /// ⚠ Also the producer-range guard for the <c>*_gunhit</c> family (docs/formats/weapon-effects.md):
    /// every such name must resolve inside <see cref="EffectCatalogue.EffectAnimNames"/>, unlike an
    /// <see cref="ImpactOutcome.EffectName"/> that names a gamez mesh instead.</summary>
    [ExtractedDataFact]
    public void Every48WeaponsResolvesACoherentOutcomeAtEveryReachableSurfaceId()
    {
        var weapons = WeaponDefs.Load(SharedZrdr);
        Assert.Equal(48, weapons.All.Count);

        var sounds = SoundDefs.Load(SharedZrdr);
        var groups = SoundDefs.LoadGroups(SharedZrdr);
        var violations = new List<string>();

        foreach (var weapon in weapons.All)
        {
            // HasBlastDamage's rule, re-derived from the raw fields rather than read back off the
            // outcome it is meant to check, a positive-damage weapon with an authored radius owes
            // a blast; a zero-damage flash/flare special's radius is an effect radius only.
            var expectedBlast = weapon.HealthDamage is > 0f && weapon.ImpactProximity is > 0f;

            foreach (var surfaceId in ReachableSurfaceIds)
            {
                var outcome = ImpactOutcome.Resolve(weapon, surfaceId, modelResolved: false, hasEffectsRuntime: true);
                var where = $"{weapon.Id} ({weapon.Name}) / {surfaceId}/{SurfaceRegistry.NameForId(surfaceId)}";

                if (outcome.EffectName == null && outcome.StandIn == ImpactStandIn.None)
                    violations.Add($"{where}: neither an effect name nor a stand-in");

                // A SOUND token names either a SETS def or a SOUND_GROUPS entry; the coherent
                // check is the union of both tables.
                if (outcome.Sound != null && !sounds.ContainsKey(outcome.Sound) && !groups.ContainsKey(outcome.Sound))
                    violations.Add($"{where}: sound '{outcome.Sound}' is neither a SoundDefs entry nor a SOUND_GROUPS name");

                if (outcome.HasBlastDamage != expectedBlast)
                    violations.Add($"{where}: HasBlastDamage was {outcome.HasBlastDamage}, expected {expectedBlast}");

                // The gun-effect range this loop also guards (see the method doc): a *_gunhit
                // name must resolve in the catalogue.
                if (outcome.EffectName is { } gunhit && gunhit.EndsWith("_gunhit", StringComparison.Ordinal)
                    && !EffectCatalogue.EffectAnimNames.Contains(gunhit))
                    violations.Add($"{where}: gun effect '{gunhit}' is not in EffectCatalogue.EffectAnimNames");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>The third hand-picked case the review named: a rocket's authored splash model at
    /// water beats the stand-in ladder entirely, same as the synthetic case in
    /// <see cref="AResolvedModelLeavesNoStandIn"/> but against the shipped incendiary rocket's own
    /// table row rather than a hand-built weapon.</summary>
    [ExtractedDataFact]
    public void ARocketOnWaterResolvesTheGamezSplashModel()
    {
        var incendiary = WeaponDefs.Load(SharedZrdr).Get("wep_04")!;

        var outcome = ImpactOutcome.Resolve(incendiary, SurfaceRegistry.Water, modelResolved: true, hasEffectsRuntime: true);

        Assert.Equal("bsplsh.flt", outcome.EffectName);
        Assert.Equal("snd_bsplash", outcome.Sound);
        Assert.Equal(ImpactStandIn.None, outcome.StandIn);
        Assert.True(outcome.HasBlastDamage);
    }

    private static WeaponDef Gun() => new() { Id = "test_gun", Caliber = 30, IsCannon = true };

    private static WeaponDef Rocket() => new() { Id = "test_rocket", IsRocket = true };
}
