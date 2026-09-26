using System;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The difficulty setting's two effects, both built from one k at one gate: a hostile vehicle's
/// armour and health maxima scaled at spawn, and that pilot's nine skill ratings shifted before
/// they interpolate. Full decode: <c>docs/org/vehicleDamage.md</c> "The difficulty scale" and
/// <c>docs/org/aiControlLaw.md</c> "The skill scalar". Pins the three factors, the hostility gate,
/// the ace exemption, the 0-9 clamp, the two naming vocabularies, and the order against the
/// per-spawn jitter, which runs after this and bands around the scaled hull.
/// </summary>
public class EnemyDifficultyScaleTests
{
    /// <summary>The decoded factors. The low tier is 0.75 and not 0.875: the branch loads
    /// <c>k = -2</c>, so the spread is a symmetric two eighths either side of 1.0.</summary>
    [Fact]
    public void TheThreeTiersScaleByThreeQuartersOneAndFiveQuarters()
    {
        Assert.Equal(0.75f, Difficulty.EnemyDurabilityFactor(Difficulty.Normal));
        Assert.Equal(1f, Difficulty.EnemyDurabilityFactor(Difficulty.Hard));
        Assert.Equal(1.25f, Difficulty.EnemyDurabilityFactor(Difficulty.Hardest));
    }

    /// <summary>Anything off the three tiers lands on the middle one, the way the engine's own
    /// setter is fed (<c>0 -&gt; 0, 2 -&gt; 2, anything else -&gt; 1</c>).</summary>
    [Fact]
    public void AnyOtherValueIsTheMiddleTier()
    {
        Assert.Equal(1f, Difficulty.EnemyDurabilityFactor(-3));
        Assert.Equal(1f, Difficulty.EnemyDurabilityFactor(7));
        Assert.Equal(Difficulty.Hard, Difficulty.Clamp(99));
    }

    /// <summary>Two vocabularies name the same three tiers: the campaign selector's
    /// <c>IDS_DIFFICULTY</c> and Instant Action's <c>IDS_IA_DIFFICULTY</c>. Normal IS novice, which
    /// is the whole reason a wave's skill can stand in as the setting.</summary>
    [Fact]
    public void BothVocabulariesNameTheSameTiers()
    {
        Assert.Equal(Difficulty.Normal, Difficulty.Parse("normal"));
        Assert.Equal(Difficulty.Normal, Difficulty.Parse("novice"));
        Assert.Equal(Difficulty.Hard, Difficulty.Parse("Hard"));
        Assert.Equal(Difficulty.Hard, Difficulty.Parse("VETERAN"));
        Assert.Equal(Difficulty.Hardest, Difficulty.Parse(" hardest "));
        Assert.Equal(Difficulty.Hardest, Difficulty.Parse("ace"));
    }

    /// <summary>An unknown name is null rather than a tier, so a caller rejects it instead of
    /// silently flying a difficulty nobody asked for.</summary>
    [Fact]
    public void AnUnknownNameIsNotATier()
    {
        Assert.Null(Difficulty.Parse("brutal"));
        Assert.Null(Difficulty.Parse(""));
        Assert.Null(Difficulty.Parse(null));
    }

    /// <summary>The gate is the engine's, and it is hostility rather than inequality: the branch
    /// spares a side equal to the player's constant 1 AND a side of 0, so a neutral takes nothing.
    /// A spawn carrying no team is hostile here, because an AI rig's fallback team is its shooter
    /// id's band and never the player's.</summary>
    [Fact]
    public void ThePlayersOwnTeamAndTheNeutralsAreSpared()
    {
        Assert.Equal(1f, Difficulty.FactorForSpawn(AimAssist.PlayerTeam, null, Difficulty.Normal));
        Assert.Equal(1f, Difficulty.FactorForSpawn(AimAssist.NeutralTeam, null, Difficulty.Normal));
        Assert.Equal(0.75f, Difficulty.FactorForSpawn(null, null, Difficulty.Normal));
        Assert.Equal(0.75f, Difficulty.FactorForSpawn(2, null, Difficulty.Normal));
        Assert.Equal(0.75f, Difficulty.FactorForSpawn(3, null, Difficulty.Normal));
    }

    /// <summary>The same k the durability factor is built from is ADDED to every one of a hostile
    /// pilot's nine skill ratings before they interpolate: -2 at Normal, +2 at Hardest.</summary>
    [Fact]
    public void TheSameKShiftsAHostilePilotsSkillRatings()
    {
        Assert.Equal(3, Difficulty.SkillRatingForSpawn(5, 2, false, null, Difficulty.Normal));
        Assert.Equal(5, Difficulty.SkillRatingForSpawn(5, 2, false, null, Difficulty.Hard));
        Assert.Equal(7, Difficulty.SkillRatingForSpawn(5, 2, false, null, Difficulty.Hardest));
    }

    /// <summary>The ace flag (roster slot 67) zeroes the offset and nothing else: the read at
    /// 0x0047cde2 sits between the armour block and the skill block, so a flagged pilot flies its
    /// authored ratings at every tier while its hull still takes the scale.</summary>
    [Fact]
    public void AnAceKeepsItsAuthoredRatingsAtEveryTier()
    {
        Assert.Equal(9, Difficulty.SkillRatingForSpawn(9, 2, true, null, Difficulty.Normal));
        Assert.Equal(9, Difficulty.SkillRatingForSpawn(9, 2, true, null, Difficulty.Hardest));
        Assert.Equal(6, Difficulty.SkillRatingForSpawn(6, 2, true, null, Difficulty.Normal));
        Assert.Equal(0.75f, Difficulty.FactorForSpawn(2, null, Difficulty.Normal));
    }

    /// <summary>The player's own side and a neutral take no offset either, off the one gate both
    /// effects share. An ace flag on the player's side changes nothing, since there is nothing to
    /// exempt from.</summary>
    [Fact]
    public void ASparedSideTakesNoSkillOffset()
    {
        Assert.Equal(5, Difficulty.SkillRatingForSpawn(5, AimAssist.PlayerTeam, false, null, Difficulty.Normal));
        Assert.Equal(5, Difficulty.SkillRatingForSpawn(5, AimAssist.NeutralTeam, false, null, Difficulty.Hardest));
        Assert.Equal(3, Difficulty.SkillRatingForSpawn(5, null, false, null, Difficulty.Normal));
    }

    /// <summary>The engine clamps the shifted rating to 0-9, not to 1-9: a rating of 1 at Normal
    /// floors at 0 rather than staying 1, and a 9 at Hardest cannot climb past the table's top.
    /// Rating 0 is a real point on the curve, which is why <c>AiSkills.At</c> reaches it.</summary>
    [Fact]
    public void TheShiftedRatingClampsToZeroAndNine()
    {
        Assert.Equal(0, Difficulty.SkillRatingForSpawn(1, 2, false, null, Difficulty.Normal));
        Assert.Equal(0, Difficulty.SkillRatingForSpawn(2, 2, false, null, Difficulty.Normal));
        Assert.Equal(9, Difficulty.SkillRatingForSpawn(9, 2, false, null, Difficulty.Hardest));
        Assert.Equal(9, Difficulty.SkillRatingForSpawn(8, 2, false, null, Difficulty.Hardest));
    }

    /// <summary>A per-spawn setting outranks the session's for the ratings as well as the hull,
    /// which is what an Instant Action wave's skill is.</summary>
    [Fact]
    public void APerSpawnSettingOutranksTheSessionsForRatingsToo()
    {
        Assert.Equal(7, Difficulty.SkillRatingForSpawn(5, 2, false, Difficulty.Hardest, Difficulty.Normal));
        Assert.Equal(3, Difficulty.SkillRatingForSpawn(5, 2, false, Difficulty.Normal, Difficulty.Hardest));
    }

    /// <summary>A per-spawn setting outranks the session's, which is the whole of what an Instant
    /// Action wave's skill does: it stands in as the global setting for that one spawn.</summary>
    [Fact]
    public void APerSpawnSettingOutranksTheSessions()
    {
        Assert.Equal(1.25f, Difficulty.FactorForSpawn(2, Difficulty.Hardest, Difficulty.Normal));
        Assert.Equal(0.75f, Difficulty.FactorForSpawn(2, Difficulty.Normal, Difficulty.Hardest));
        Assert.Equal(1.25f, Difficulty.FactorForSpawn(2, null, Difficulty.Hardest));
    }

    /// <summary>The whole-vehicle pair scales and the per-part pools do not, the same split the
    /// jitter makes. A parts-only airframe has its pair resolved to the sum on the way out, or
    /// <see cref="PlaneDamage"/> would re-derive the unscaled hull and the scale would vanish.</summary>
    [Fact]
    public void TheHullPairScalesAndThePartPoolsDoNot()
    {
        var authored = Authored();
        Assert.Null(authored.VehicleHealth);

        var enemy = authored.WithEnemyDurability(0.75f);

        Assert.Equal(30f, enemy.VehicleHealth!.Value, 3);
        Assert.Equal(30f, enemy.VehicleArmor!.Value, 3);
        foreach (var part in enemy.DestroyableParts)
        {
            Assert.Equal(20f, part.MaxHp);
            Assert.Equal(20f, part.MaxArmor);
        }
    }

    /// <summary>An authored pair (the AI base defs carry one) is scaled where it stands.</summary>
    [Fact]
    public void AnAuthoredHullPairIsScaledInPlace()
    {
        var authored = Authored();
        authored.VehicleHealth = 72f;
        authored.VehicleArmor = 72f;

        var enemy = authored.WithEnemyDurability(1.25f);

        Assert.Equal(90f, enemy.VehicleHealth!.Value, 3);
        Assert.Equal(90f, enemy.VehicleArmor!.Value, 3);
    }

    /// <summary>The session hands every aircraft off one airframe the same cached
    /// <see cref="PlaneStats"/>, so scaling must copy rather than perturb in place.</summary>
    [Fact]
    public void TheSharedAirframeStatsAreNotScaledInPlace()
    {
        var shared = Authored();
        shared.VehicleHealth = 72f;

        var enemy = shared.WithEnemyDurability(0.75f);

        Assert.Equal(72f, shared.VehicleHealth!.Value);
        Assert.Equal(54f, enemy.VehicleHealth!.Value, 3);
    }

    /// <summary>The unscaled tier costs nothing and changes nothing: the caller's own object comes
    /// back, so a player-team spawn and a middle-tier session take no copy at all.</summary>
    [Fact]
    public void TheUnscaledTierReturnsTheSameObject()
    {
        var authored = Authored();

        Assert.Same(authored, authored.WithEnemyDurability(1f));
    }

    /// <summary>The engine's spawn order: the scale lands on the authored pools and the jitter bands
    /// around the SCALED hull, never the reverse. At Normal a 40-point hull is 30 ± 5 %, which the
    /// unscaled band around 40 cannot reach.</summary>
    [Fact]
    public void TheJitterBandsAroundTheScaledHull()
    {
        var authored = Authored();

        var spawned = authored
            .WithEnemyDurability(Difficulty.EnemyDurabilityFactor(Difficulty.Normal))
            .WithAiSpawnJitter(new Random(7));

        float health = spawned.VehicleHealth!.Value;
        Assert.True(Math.Abs(health - 30f) <= 30f * PlaneStats.AiSpawnJitterSpread + 1e-4f,
            $"hull health {health} is outside 30 ± 5 %");
    }

    // Two parts and no authored pair, so the hull sum is 40 and the arithmetic is watchable.
    private static PlaneStats Authored()
    {
        var stats = new PlaneStats { FdSpeed = 100f, EnginePower = 0.5f };
        stats.DestroyableParts.Add(new DestroyablePart { Name = "nose", MaxHp = 20f, MaxArmor = 20f });
        stats.DestroyableParts.Add(new DestroyablePart { Name = "tail", MaxHp = 20f, MaxArmor = 20f });
        return stats;
    }
}
