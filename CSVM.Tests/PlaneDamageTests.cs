using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using Xunit;

namespace CSVM.Tests;

/// <summary>The decoded vehicle damage flow (docs/org/vehicleDamage.md, corrected 2026-08-14):
/// per-part pools spent armor-first, a dead or unknown zone redirected to a random surviving
/// zone, the whole-vehicle pair recomputed from the parts after every part spend, and the
/// unabsorbed leftover draining the whole pair directly through the wrapper loop. Values are
/// the stock Bloodhawk's (20 hp / 20 armor per zone) and the weapons data's 30-calibre ammo
/// matrix, AP `wep_32` 4.5 armor / 1.5 health, dum-dum `wep_31` 1.5 / 4.5.</summary>
[Trait("Tier", "Quick")]
public class PlaneDamageTests
{
    [Fact]
    public void APartStartsWithBothPoolsFullAndTheWholePairSeedsAsTheSumOverParts()
    {
        var damage = Bloodhawk();
        var nose = damage.Parts["nose"];
        Assert.Equal(20f, nose.Hp);
        Assert.Equal(20f, nose.Armor);
        Assert.Equal(1f, nose.Fraction, 4);
        Assert.Equal(1f, damage.WorstFraction, 4);
        Assert.Equal(40f, damage.WholeArmorMax);
        Assert.Equal(40f, damage.WholeHealthMax);
        Assert.Equal(40f, damage.WholeArmor);
        Assert.Equal(40f, damage.WholeHealth);
        Assert.False(damage.IsDestroyed);
        Assert.Equal("", damage.Summary());
    }

    /// <summary>The AI defs author a whole pair (docs/formats/vehicle.md: fighters 64/64…100/100)
    /// and it beats the sum over parts; player defs author none and fall back to the sum.</summary>
    [Fact]
    public void AnAuthoredWholePairBeatsTheSumOverParts()
    {
        var damage = new PlaneDamage(TwoZones(), wholeArmorMax: 64f, wholeHealthMax: 64f);
        Assert.Equal(64f, damage.WholeArmorMax);
        Assert.Equal(64f, damage.WholeHealthMax);
        Assert.Equal(64f, damage.WholeHealth);
    }

    [Fact]
    public void ArmorIsSpentBeforeHealthAndTheWholePairFollowsTheParts()
    {
        var damage = Bloodhawk();
        var nose = damage.Apply("nose", 4.5f, 4.5f)!;
        Assert.Equal(15.5f, nose.Armor, 4);
        Assert.Equal(20f, nose.Hp, 4);
        // The recompute (FUN_004b3bf0): whole current = parts' fraction × whole maxima.
        Assert.Equal(35.5f, damage.WholeArmor, 4);
        Assert.Equal(40f, damage.WholeHealth, 4);
    }

    [Fact]
    public void TheShareArmorCouldNotAbsorbCarriesIntoHealthAndTheLeftoverReEntersTheWholePair()
    {
        var damage = Bloodhawk();
        // Four AP rounds strip 18 of 20 armor; the fifth finds 2 left, so 2/4.5 of it is absorbed
        // and the remaining 5/9 of the round lands on health as 5/9 of its 1.5 health magnitude.
        for (int i = 0; i < 4; i++)
            damage.Apply("nose", 1.5f, 4.5f);
        var nose = damage.Parts["nose"];
        Assert.Equal(2f, nose.Armor, 4);
        Assert.Equal(20f, nose.Hp, 4);

        damage.Apply("nose", 1.5f, 4.5f);
        Assert.Equal(0f, nose.Armor, 4);
        Assert.Equal(20f - 1.5f * (2.5f / 4.5f), nose.Hp, 4);
        // The wrapper loop's leftover pair (2.5 armor, 5/9 × 1.5 health) re-entered zone-less:
        // the recomputed whole armor (20) covered the 2.5 outright, shielding the health share.
        Assert.Equal(17.5f, damage.WholeArmor, 4);
        Assert.Equal(20f - 1.5f * (2.5f / 4.5f) + 20f, damage.WholeHealth, 4);
    }

    [Fact]
    public void AStrippedZoneTakesTheFullHealthMagnitude()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 0f, 20f);          // strip the armor exactly
        var nose = damage.Parts["nose"];
        Assert.Equal(0f, nose.Armor, 4);
        Assert.Equal(20f, nose.Hp, 4);

        damage.Apply("nose", 4.5f, 1.5f);       // dum-dum on bare airframe
        Assert.Equal(15.5f, nose.Hp, 4);
        damage.Apply("nose", 1.5f, 4.5f);       // AP on bare airframe: little damage
        Assert.Equal(14f, nose.Hp, 4);
    }

    [Fact]
    public void AZoneWithNoArmorPoolTakesFullHealthDamageFromTheFirstShot()
    {
        var damage = Unarmored();
        var nose = damage.Apply("nose", 4.5f, 4.5f)!;
        Assert.Equal(0f, nose.Armor, 4);
        Assert.Equal(15.5f, nose.Hp, 4);
        Assert.Equal(15.5f / 20f, nose.Fraction, 4);
    }

    [Fact]
    public void ASingleMagnitudeSpendsAcrossBothPoolsArmorEndFirst()
    {
        // A collision: player.json's crash block gives armor and health the same range.
        var damage = Bloodhawk();
        var nose = damage.Apply("nose", 30f)!;
        Assert.Equal(0f, nose.Armor, 4);
        Assert.Equal(10f, nose.Hp, 4);
        Assert.Equal(10f / 40f, nose.Fraction, 4);
        // Leftover pair (10, 20) re-entered; the recomputed whole armor (20) covered it.
        Assert.Equal(10f, damage.WholeArmor, 4);
        Assert.Equal(30f, damage.WholeHealth, 4);
    }

    /// <summary>Which quotient the injure staging must read (BL-384 items 1 and 2, from BL-297's
    /// decode). FUN_004b3d70 divides health only, and while a zone's armor stands its health
    /// cannot move, so no per-part threshold is crossed. The combined progression crosses them
    /// early: a Bloodhawk zone stripped of armor sits at combined 0.5, which is already the
    /// shipped leftwing pdpanel5 threshold, with the airframe untouched. DamageVisuals.OnPartDamage
    /// takes HealthFraction for exactly this reason; feeding it Fraction tears panels early.</summary>
    [Fact]
    public void StrippingAZonesArmorCrossesAShippedPanelThresholdOnTheCombinedScaleButNotOnHealth()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 0f, 20f);          // strip the armor exactly, health untouched
        var nose = damage.Parts["nose"];
        Assert.Equal(0.5f, nose.Fraction, 4);   // ≤ 0.5: pdpanel5's shipped threshold
        Assert.Equal(1f, nose.HealthFraction, 4);
        Assert.Equal(1f, damage.SummaryHealthFraction, 4);
    }

    /// <summary>The def-level list is on the hull pool, not any one zone (FUN_004b3800). One zone
    /// gutted leaves the hull well above player_smoketrail's 0.10 entry, which is why
    /// DamageVisuals.OnHullDamage exists apart from OnPartDamage.</summary>
    [Fact]
    public void OneGuttedZoneLeavesTheHullFractionFarAboveTheHeavyTrailThreshold()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 0f, 20f);          // armor off
        damage.Apply("nose", 40f, 0f);          // and the zone's health with it
        Assert.Equal(0f, damage.Parts["nose"].HealthFraction, 4);
        Assert.True(damage.SummaryHealthFraction > 0.10f,
            $"hull at {damage.SummaryHealthFraction:0.00}, above the 0.10 heavy-trail entry");
        Assert.False(damage.IsDestroyed);
    }

    /// <summary>The decoded quirk kept on purpose (FUN_004b7f80's opening branch): a hit with
    /// health damage but NO armor damage is nulled outright while the zone's armor stands.</summary>
    [Fact]
    public void APureHealthHitOnAnArmoredZoneIsShieldedOutright()
    {
        var damage = Bloodhawk();
        var nose = damage.Apply("nose", 10f, 0f)!;
        Assert.Equal(20f, nose.Armor, 4);
        Assert.Equal(20f, nose.Hp, 4);
        Assert.Equal(40f, damage.WholeHealth, 4);
    }

    /// <summary>THE porting gap this file pins (the 9-rocket sponge): an overkill hit's
    /// leftover drains the whole pair through the wrapper loop, so one massive hit on one
    /// zone kills while the other zone is untouched.</summary>
    [Fact]
    public void AMassiveHitOnOneZoneKillsThroughTheOverflow()
    {
        var damage = Bloodhawk();
        var nose = damage.Apply("nose", 500f, 500f)!;
        Assert.Equal(0f, nose.Armor);
        Assert.Equal(0f, nose.Hp);
        var tail = damage.Parts["tail"];
        Assert.Equal(20f, tail.Hp);              // pristine
        Assert.Equal(20f, tail.Armor);
        Assert.Equal(0f, damage.WholeHealth);
        Assert.True(damage.IsDestroyed);
    }

    /// <summary>Death at whole ≤ 0 with three zones still healthy, the decoded kill needs the
    /// whole pool empty, not the zones.</summary>
    [Fact]
    public void TheOverflowKillLeavesThreeZonesHealthyOnAFourZoneAirframe()
    {
        var damage = FourZones();
        damage.Apply("nose", 200f, 200f);
        Assert.True(damage.IsDestroyed);
        Assert.Equal(0f, damage.WholeHealth);
        Assert.Equal(3, damage.Parts.Values.Count(p => p.Hp >= p.Def.MaxHp));
    }

    [Fact]
    public void StrippingArmorDoesNotEmptyHealth()
    {
        var damage = Bloodhawk();
        var nose = damage.Apply("nose", 1.5f, 100f)!;
        Assert.Equal(0f, nose.Armor);
        Assert.True(nose.Hp > 0f);
        Assert.False(damage.IsDestroyed);
    }

    /// <summary>The decoded kill rule: whole-vehicle health at zero. A dead critical part
    /// alone leaves the recomputed whole pool at the surviving parts' fraction; exact spends
    /// (no overflow) walk it to zero only when every zone's health is gone.</summary>
    [Fact]
    public void OneDeadCriticalPartIsNotDestroyedButAllHealthGoneIs()
    {
        var damage = Bloodhawk();
        KillZoneExactly(damage, "nose"); // armor stripped then health spent, no leftover
        Assert.False(damage.IsDestroyed);
        Assert.Equal(0.5f, damage.SummaryHealthFraction, 4);
        KillZoneExactly(damage, "tail");
        Assert.True(damage.IsDestroyed);
        Assert.Equal(0f, damage.SummaryHealthFraction, 4);
        damage.Reset();
        Assert.False(damage.IsDestroyed);
        Assert.Equal(1f, damage.SummaryHealthFraction, 4);
        Assert.Equal(40f, damage.WholeArmor);
        Assert.Equal(40f, damage.WholeHealth);
    }

    /// <summary>The resolver rule (FUN_004b3950): a dead zone is never struck, the hit
    /// redirects to a surviving zone, so a dead zone absorbs nothing and soaks nothing.</summary>
    [Fact]
    public void AHitOnADeadZoneRedirectsToASurvivor()
    {
        var damage = Bloodhawk();
        KillZoneExactly(damage, "nose");
        var struck = damage.Apply("nose", 4.5f, 4.5f);
        Assert.NotNull(struck);
        Assert.Equal("tail", struck!.Def.Name); // the only survivor
        Assert.Equal(15.5f, struck.Armor, 4);
        Assert.Equal(0f, damage.Parts["nose"].Hp); // the dead zone did not move
    }

    /// <summary>A zone name the def does not carry behaves like the resolver's geometric miss:
    /// the hit lands on a surviving zone rather than vanishing.</summary>
    [Fact]
    public void AnUnknownPartRedirectsToASurvivor()
    {
        var damage = Bloodhawk();
        var struck = damage.Apply("leftwing", 4.5f, 4.5f);
        Assert.NotNull(struck);
        float total = damage.Parts.Values.Sum(p => p.Hp + p.Armor);
        Assert.Equal(80f - 4.5f, total, 4);
    }

    /// <summary>The decoded recompute quirk, reproduced faithfully: a later part spend
    /// recomputes the whole pair from the parts and OVERWRITES an earlier overflow dent,
    /// partially healing it. The engine's own arithmetic does this.</summary>
    [Fact]
    public void ALaterPartSpendOverwritesAnEarlierOverflowDent()
    {
        var damage = FourZones();
        foreach (var name in new[] { "nose", "tail", "leftwing", "rightwing" })
            damage.Apply(name, 0f, 20f); // strip every zone's armor: whole armor recomputes to 0
        Assert.Equal(0f, damage.WholeArmor, 4);
        Assert.Equal(80f, damage.WholeHealth, 4);

        // Overkill the nose: 20 absorbed at the part, leftover 10 drains the whole pool
        // directly (no recompute on the zone-less pass).
        damage.Apply("nose", 30f, 5f);
        Assert.Equal(0f, damage.Parts["nose"].Hp, 4);
        Assert.Equal(50f, damage.WholeHealth, 4); // recomputed 60, then dented by the leftover 10

        // A small later part spend recomputes the whole pool from the parts: the dent heals
        // back onto the parts' fraction (59 of 80), the decoded quirk.
        damage.Apply("tail", 1f, 0f); // tail armor is stripped, so the health point lands
        Assert.Equal(19f, damage.Parts["tail"].Hp, 4);
        Assert.Equal(59f, damage.WholeHealth, 4);
    }

    /// <summary>Concentrated fire on ONE zone kills: the resolver redirects once the zone dies
    /// and the recompute walks the whole pool down with the surviving zones.</summary>
    [Fact]
    public void HammeringOneZoneWithGunRoundsKillsWithinTheTotalPoolBudget()
    {
        var damage = FourZones();
        int budget = (int)((80f + 80f) / 4.5f) * 3;
        int rounds = 0;
        while (!damage.IsDestroyed && rounds < budget)
        {
            rounds++;
            damage.Apply("nose", 4.5f, 4.5f);
        }

        Assert.True(damage.IsDestroyed);
        Assert.True(rounds < budget);
    }

    [Fact]
    public void TheFractionIsTheCombinedProgressionThroughArmorThenHealth()
    {
        var damage = Bloodhawk();
        var nose = damage.Parts["nose"];
        damage.Apply("nose", 10f);              // half the armor
        Assert.Equal(0.75f, nose.Fraction, 4);
        Assert.Equal(0.5f, nose.ArmorFraction, 4);
        Assert.Equal(1f, nose.HealthFraction, 4);

        damage.Apply("nose", 10f);              // the rest of it
        Assert.Equal(0.5f, nose.Fraction, 4);
        Assert.Equal(0f, nose.ArmorFraction, 4);
        Assert.Equal(1f, nose.HealthFraction, 4);

        damage.Apply("nose", 10f);              // now into the airframe
        Assert.Equal(0.25f, nose.Fraction, 4);
        Assert.Equal(0.5f, nose.HealthFraction, 4);
    }

    [Fact]
    public void WorstFractionIsTheLowestPartAcrossBothPools()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 10f);
        damage.Apply("tail", 30f);
        Assert.Equal(0.25f, damage.WorstFraction, 4);
    }

    [Fact]
    public void ResetRefillsThePartsAndTheWholePair()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 35f);
        damage.Reset();
        var nose = damage.Parts["nose"];
        Assert.Equal(20f, nose.Armor);
        Assert.Equal(20f, nose.Hp);
        Assert.Equal(40f, damage.WholeArmor);
        Assert.Equal(40f, damage.WholeHealth);
        Assert.Equal("", damage.Summary());
    }

    [Fact]
    public void SummaryLeadsWithTheWholePairThenTheHurtParts()
    {
        var damage = Bloodhawk();
        damage.Apply("nose", 10f);
        Assert.Equal("hull a75% h100% · nose a50%", damage.Summary());
        damage.Apply("nose", 10f);
        Assert.Equal("hull a50% h100% · nose a0%", damage.Summary());
        damage.Apply("nose", 10f);
        Assert.Equal("hull a50% h75% · nose a0% h50%", damage.Summary());
    }

    // Kills a zone with exact spends (armor stripped, then its health spent with no
    // armor damage on the bare zone) so no leftover reaches the whole pair, the suites'
    // scaffolding pattern.
    private static void KillZoneExactly(PlaneDamage damage, string name)
    {
        var part = damage.Parts[name];
        damage.Apply(name, 0f, part.Def.MaxArmor);
        damage.Apply(name, part.Def.MaxHp, 0f);
        Assert.Equal(0f, part.Hp);
    }

    private static List<DestroyablePart> TwoZones() => new()
    {
        new() { Name = "nose", MaxHp = 20f, MaxArmor = 20f, Critical = true },
        new() { Name = "tail", MaxHp = 20f, MaxArmor = 20f, Critical = true, Engine = true },
    };

    private static PlaneDamage Bloodhawk() => new(TwoZones());

    private static PlaneDamage FourZones() => new(new List<DestroyablePart>
    {
        new() { Name = "nose", MaxHp = 20f, MaxArmor = 20f, Critical = true },
        new() { Name = "tail", MaxHp = 20f, MaxArmor = 20f, Critical = true, Engine = true },
        new() { Name = "leftwing", MaxHp = 20f, MaxArmor = 20f, Critical = true },
        new() { Name = "rightwing", MaxHp = 20f, MaxArmor = 20f, Critical = true },
    });

    private static PlaneDamage Unarmored() => new(new List<DestroyablePart>
    {
        new() { Name = "nose", MaxHp = 20f, MaxArmor = 0f, Critical = true },
    });
}
