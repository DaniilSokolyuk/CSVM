using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The target-ranking formula (FUN_00421ad0, docs/org/aiPilot.md "Target acquisition"):
/// rank = weight × 1200 + distance + objectiveBias, minimised, player base weight 0.7, +0.4 on a
/// wingman, ±0.2 ahead-behind/altitude/closing terms on the decoded half-metre deadband, −0.5 on
/// a gasbag, 1e21 beyond the ATTACK radius, which is the volume both decoded scorers admit on.
/// Ahead and above are the UNFAVOURABLE arms. Plus
/// the roster accessors for slots 6 (primary_target) and 33 (rating_biases), with install-wide
/// goldens.
/// </summary>
[Trait("Tier", "Quick")]
public class AiTargetRankingTests
{
    private const float Attack = 2000f;

    private static readonly Vector3 OwnPos = new(0f, 500f, 0f);
    private static readonly Vector3 OwnFwd = new(0f, 0f, -1f);

    // ---- the formula -------------------------------------------------------------------------

    [Fact]
    public void PlayerRanksAtPointSevenWorkedComparison()
    {
        // Equal geometry, 800 m each: the player's 0.7 base weight is worth 0.3 × 1200 = 360
        // rank units, so the player out-ranks (rank is minimised) the AI target exactly by it.
        var player = Ahead(800f, isPlayer: true, xOffset: 100f);
        var ai = Ahead(800f, xOffset: -100f);
        var sPlayer = AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, player);
        var sAi = AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, ai);
        Assert.Equal(0.7f + 0.2f - 0.2f - 0.2f, sPlayer.Weight, 3); // ahead +, level −, away −
        Assert.Equal(1.0f + 0.2f - 0.2f - 0.2f, sAi.Weight, 3);
        Assert.Equal(360f, sAi.Rank - sPlayer.Rank, 1);

        int pick = AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { ai, player }, out var best);
        Assert.Equal(1, pick);
        Assert.Equal(sPlayer.Rank, best.Rank, 2);
    }

    [Fact]
    public void TheWeightScaleDominatesDistanceAt1200()
    {
        // The 0.3 player-weight delta equals 360 m: an AI target 500 m closer wins, one only
        // 300 m closer still loses.
        var player = Ahead(900f, isPlayer: true, xOffset: 100f);
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { Ahead(400f, xOffset: -100f), player }, out _));
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { Ahead(600f, xOffset: -100f), player }, out _));
        // One ±0.2 term flip is 480 rank units: a target ahead loses to one 400 m farther
        // astern (ahead/behind is the only differing term; the astern one flies away too, so
        // closing stays on the away arm). The engine prefers the target on its tail.
        var behind = Ahead(800f);
        behind.Position = OwnPos + new Vector3(0f, 0f, 800f);
        behind.Velocity = new Vector3(0f, 0f, 60f);
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { Ahead(400f), behind }, out _));
    }

    [Fact]
    public void DistanceBreaksTies()
    {
        var near = Ahead(500f);
        var far = Ahead(900f);
        Assert.Equal(400f, RankOf(far) - RankOf(near), 1);
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { near, far }, out _));
    }

    [Fact]
    public void BeyondTheAttackRadiusScoresTheEnginesOwn1e21AndIsNeverPicked()
    {
        var outside = Ahead(Attack + 1f);
        Assert.Equal(AiTargetRanking.NotRanked, RankOf(outside));
        // Even alone, a target outside the attack cylinder is never selected.
        Assert.Equal(-1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { outside }, out _));
        // At the radius it still ranks (the cutoff is strictly beyond).
        Assert.True(RankOf(Ahead(Attack)) < AiTargetRanking.NotRanked);
    }

    [Fact]
    public void TheThreeTermsMoveTheWeightByPointTwoEach()
    {
        var baseline = Ahead(500f);
        float b = RankOf(baseline);

        var behind = baseline;
        behind.Position = OwnPos + new Vector3(0f, 0f, 500f); // astern: ahead/behind flips…
        behind.Velocity = new Vector3(0f, 0f, 60f);            // …and only that term
        Assert.Equal(-0.4f * AiTargetRanking.WeightScale, RankOf(behind) - b, 1);

        var above = baseline;
        above.Position = baseline.Position + Vector3.Up * 100f; // above: altitude flips
        float distAbove = OwnPos.DistanceTo(above.Position);
        Assert.Equal(0.4f * AiTargetRanking.WeightScale + (distAbove - 500f),
            RankOf(above) - b, 1);

        var closing = baseline;
        closing.Velocity = new Vector3(0f, 0f, 60f); // flying at the shooter: closing flips
        Assert.Equal(0.4f * AiTargetRanking.WeightScale, RankOf(closing) - b, 1);

        // A stationary candidate (a turret, a parked structure) reads as the favourable arm.
        var parked = baseline;
        parked.Velocity = Vector3.Zero;
        Assert.Equal(b, RankOf(parked), 1);
    }

    [Fact]
    public void TheAheadTermHasAHalfMetreDeadbandOnTheRawOffset()
    {
        // The dot is taken on the un-normalised offset, so a candidate 0.4 m ahead of the
        // scorer's plane adds nothing, 0.6 m ahead adds the term, 0.6 m behind subtracts it.
        var abeam = Ahead(500f);
        abeam.Position = OwnPos + new Vector3(500f, 0f, -0.4f);
        abeam.Velocity = new Vector3(60f, 0f, 0f); // flying away abeam, so closing holds still
        var justAhead = abeam;
        justAhead.Position = OwnPos + new Vector3(500f, 0f, -0.6f);
        var justBehind = abeam;
        justBehind.Position = OwnPos + new Vector3(500f, 0f, 0.6f);
        float w = AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, abeam).Weight;
        Assert.Equal(w + 0.2f, AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, justAhead).Weight, 3);
        Assert.Equal(w - 0.2f, AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, justBehind).Weight, 3);
    }

    [Fact]
    public void AWingmanCarriesPointFourAgainstItAndAGasbagPointFiveForIt()
    {
        var plain = Ahead(500f);
        var wingman = plain;
        wingman.IsWingman = true;
        Assert.Equal(AiTargetRanking.WingmanWeight * AiTargetRanking.WeightScale,
            RankOf(wingman) - RankOf(plain), 1);
        var gasbag = plain;
        gasbag.IsGasbag = true;
        Assert.Equal(-AiTargetRanking.GasbagWeight * AiTargetRanking.WeightScale,
            RankOf(gasbag) - RankOf(plain), 1);
        // 600 m in the gasbag's favour: it beats an equal aircraft 500 m nearer.
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { Ahead(500f), Ahead(1000f, xOffset: 50f) with { IsGasbag = true } }, out _));
    }

    // ---- rating_biases -----------------------------------------------------------------------

    [Fact]
    public void BiasWildcardsMatchAsAuthored()
    {
        var star = new AiRatingBias("fuel_truck*", -1f, 0f);
        Assert.True(star.Matches("fuel_truck"));
        Assert.True(star.Matches("fuel_truck_02"));
        Assert.True(star.Matches("FUEL_TRUCK_02")); // case-insensitive
        Assert.False(star.Matches("truck"));
        Assert.False(star.Matches("a_fuel_truck")); // no implicit leading wildcard

        var doubleStar = new AiRatingBias("aagun**", -1f, null);
        Assert.True(doubleStar.Matches("aagun03"));

        var exact = new AiRatingBias("bloodhawk_2", -1f, null);
        Assert.True(exact.Matches("bloodhawk_2"));
        Assert.False(exact.Matches("bloodhawk_21"));

        var infix = new AiRatingBias("hk_*p", -1f, null);
        Assert.True(infix.Matches("hk_zep"));
        Assert.False(infix.Matches("hk_zeps"));
    }

    [Fact]
    public void ObjectiveBiasSaturatesAtBothEndsAndFirstMatchWins()
    {
        var biases = new List<AiRatingBias>
        {
            new("bloodhawk_*", -1f, 0f),
            new("bloodhawk_2", -0.5f, 0f), // shadowed: first match wins (authored order)
        };
        // −1.0 is the exclusion, not a penalty, the dominant shipped value.
        Assert.Equal(AiTargetRanking.NotRanked,
            AiTargetRanking.ObjectiveBiasFor("bloodhawk_2", biases), 1);
        Assert.Equal(0f, AiTargetRanking.ObjectiveBiasFor("piratezep", biases), 1);
        Assert.Equal(0f, AiTargetRanking.ObjectiveBiasFor("bloodhawk_2", null), 1);

        // Between the ends it scales to rank units directly and negated, so a positive bias
        // attracts: 0.4 is worth 300 rank units in the candidate's favour.
        Assert.Equal(-300f,
            AiTargetRanking.ObjectiveBiasFor("x", new List<AiRatingBias> { new("x", 0.4f, null) }), 1);
        // And 1.0 or more is always-target.
        Assert.Equal(AiTargetRanking.AlwaysTarget,
            AiTargetRanking.ObjectiveBiasFor("x", new List<AiRatingBias> { new("x", 1f, null) }), 1);
    }

    [Fact]
    public void AStructurePartAnswersToEveryNameAboveIt()
    {
        // C4/M03's shape: the Black Swan escort excludes the cargo zeppelin by name, and the
        // Black Hat Warhawks make it their always-target. Neither pattern names a part, because
        // the parts are damage pools called leng31 and rtur1 and the mission names the airship.
        var escort = new List<AiRatingBias> { new("bhatbrigand_5", -1f, null), new("cargozep1", -1f, null) };
        var warhawk = new List<AiRatingBias> { new("bswingman_1", -1f, null), new("cargozep1", 1f, null) };
        var chain = new[] { "cargozep1", "world" };
        Assert.Equal(AiTargetRanking.NotRanked,
            AiTargetRanking.ObjectiveBiasFor("leng31", chain, escort), 1);
        Assert.Equal(AiTargetRanking.AlwaysTarget,
            AiTargetRanking.ObjectiveBiasFor("leng31", chain, warhawk), 1);
        // The part alone, with nothing above it, is what the pools used to be offered as.
        Assert.Equal(0f, AiTargetRanking.ObjectiveBiasFor("leng31", null, escort), 1);
        // And a part of something the list does not name is unaffected either way.
        Assert.Equal(0f,
            AiTargetRanking.ObjectiveBiasFor("leng31", new[] { "piratezep" }, escort), 1);
    }

    [Fact]
    public void TheBiasListIsWalkedOncePerNameNotOncePerEntry()
    {
        // The decoded lookup restarts the whole list at each level of the chain, so the
        // candidate's own name beats an owner's entry standing earlier in authored order.
        var biases = new List<AiRatingBias> { new("cargozep1", -1f, null), new("leng31", 0.4f, null) };
        Assert.Equal(-300f, AiTargetRanking.ObjectiveBiasFor("leng31", new[] { "cargozep1" }, biases), 1);
        // …and the nearer owner beats a further one for the same reason.
        var nested = new List<AiRatingBias> { new("world", 1f, null), new("cargozep1", -1f, null) };
        Assert.Equal(AiTargetRanking.NotRanked,
            AiTargetRanking.ObjectiveBiasFor("leng31", new[] { "cargozep1", "world" }, nested), 1);
    }

    [Fact]
    public void TurretCandidateCarriesTheFlatHandicapOnEveryArm()
    {
        // D36 (BL-363): a turret's decoded +37.5 rides on top of every arm, including no match.
        Assert.Equal(AiTargetRanking.TurretBiasFlat,
            AiTargetRanking.ObjectiveBiasFor("aagun1", null, isTurret: true), 1);
        Assert.Equal(-300f + AiTargetRanking.TurretBiasFlat,
            AiTargetRanking.ObjectiveBiasFor("x", new List<AiRatingBias> { new("x", 0.4f, null) },
                isTurret: true), 1);
        Assert.Equal(AiTargetRanking.AlwaysTarget + AiTargetRanking.TurretBiasFlat,
            AiTargetRanking.ObjectiveBiasFor("x", new List<AiRatingBias> { new("x", 1f, null) },
                isTurret: true), 1);
        Assert.Equal(0f, AiTargetRanking.ObjectiveBiasFor("aagun1", null), 1);
    }

    [Fact]
    public void AnExcludedTargetIsNeverPickedEvenWhenItIsTheOnlyCandidate()
    {
        // The whole point of the exclusion: a −1.0 target is not merely deprioritised.
        var excluded = Ahead(500f, bias: AiTargetRanking.NotRanked);
        Assert.Equal(-1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { excluded }, out _));

        // …and a plain target beats it however much farther away it is, inside the radius.
        var plain = Ahead(1900f, xOffset: 50f);
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { excluded, plain }, out _));
    }

    // ---- deconfliction -----------------------------------------------------------------------

    [Fact]
    public void AHeldTargetIsDroppedForTheBestFreeOne()
    {
        // The better-ranked candidate is already held by an ally: the free peer wins.
        var held = Ahead(500f, attackers: 1);
        var free = Ahead(900f, xOffset: 50f);
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { held, free }, out var best));
        Assert.Equal(900f, best.Distance, 1);
    }

    [Fact]
    public void AnExhaustedPoolFallsBackToTheBestOverall()
    {
        // Every candidate is held: the design's "exhausting the pool returns to the top",
        // the best-ranked candidate is taken anyway.
        var a = Ahead(500f, attackers: 1);
        var b = Ahead(900f, attackers: 2, xOffset: 50f);
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet, aircraftFirst: false,
            new[] { a, b }, out var best));
        Assert.Equal(500f, best.Distance, 1);
    }

    // ---- the roster accessors ------------------------------------------------------------------

    [Fact]
    public void RosterPrimaryTargetReadsSlotSix()
    {
        var fields = new List<object?>();
        for (int i = 0; i < 10; i++)
            fields.Add(0f);
        fields[6] = "piratezep";
        Assert.Equal("piratezep", AiSkills.RosterPrimaryTarget(fields));
        fields[6] = string.Empty; // the 346 unset blocks author ""
        Assert.Null(AiSkills.RosterPrimaryTarget(fields));
        Assert.Null(AiSkills.RosterPrimaryTarget(new List<object?> { 0f, 0f }));
    }

    [Fact]
    public void RosterRatingBiasesParseTriplesAndPreserveTheThirdElement()
    {
        var fields = new List<object?>();
        for (int i = 0; i < 34; i++)
            fields.Add(0f);
        fields[33] = new List<object?>
        {
            new List<object?> { "tcargun*", -1f, 0f },     // triple: the exe comment's shape
            new List<object?> { "bloodhawk_2", -1f },      // pair: what this install authors
            new List<object?> { 42f, -1f, 0f },            // malformed: skipped
        };
        var biases = AiSkills.RosterRatingBiases(fields);
        Assert.Equal(2, biases.Count);
        Assert.Equal("tcargun*", biases[0].Pattern);
        Assert.Equal(-1f, biases[0].Bias, 3);
        Assert.Equal(0f, biases[0].Third);
        Assert.Null(biases[1].Third);

        fields[33] = null; // the 93 null blocks
        Assert.Empty(AiSkills.RosterRatingBiases(fields));
        Assert.Empty(AiSkills.RosterRatingBiases(new List<object?> { 0f }));
    }

    // ---- the class biases and the aircraft-first preference -------------------------------------

    [Fact]
    public void TheClassBiasGoesInRawBesideTheObjectiveOneAndAttracts()
    {
        // Rank units, not metres scaled by anything: the shipped -200 moves a structure exactly
        // 200 rank units in its favour, and it adds to an authored objective bias rather than
        // replacing it.
        var plain = Ahead(800f);
        var biased = Ahead(800f) with { ClassBias = -200f };
        Assert.Equal(-200f, RankOf(biased) - RankOf(plain), 1);
        var both = Ahead(800f, bias: -300f) with { ClassBias = -200f };
        Assert.Equal(-500f, both.ObjectiveBias + both.ClassBias, 1);
        Assert.Equal(-500f, RankOf(both) - RankOf(plain), 1);
        // The readout reproduces the rank: weight x 1200 + distance + bias, with both terms in it.
        var s = AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, both);
        Assert.Equal(s.Rank, s.Weight * AiTargetRanking.WeightScale + s.Distance + s.Bias, 1);
    }

    [Fact]
    public void TheShippedBiasesRankAnEquidistantStructureAheadOfAnEquidistantAeroplane()
    {
        // The decoded order, and the reason the departure exists: a wingman authoring both
        // (-100 target_bias, -200 struct_bias) puts an equidistant structure 100 rank units ahead
        // of an equidistant enemy aeroplane.
        var aeroplane = Ahead(800f) with { IsAircraft = true, ClassBias = -100f };
        var structure = Ahead(800f, xOffset: 50f) with { IsStructureClass = true, ClassBias = -200f };
        Assert.Equal(100f, RankOf(aeroplane) - RankOf(structure), 1);
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: false, new[] { aeroplane, structure }, out _));
    }

    [Fact]
    public void TheAircraftFirstPreferenceWithdrawsStructuresWhileAnAircraftRanks()
    {
        // The same pool under the preference: the far aeroplane wins over the near structure,
        // however the biases fall.
        var aeroplane = Ahead(1900f) with { IsAircraft = true, ClassBias = -100f };
        var structure = Ahead(300f, xOffset: 50f) with { IsStructureClass = true, ClassBias = -200f };
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: true, new[] { aeroplane, structure }, out _));
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: false, new[] { aeroplane, structure }, out _));

        // With no aircraft in reach the structure is fought: the preference is a priority, never
        // a filter that leaves a gunner with nothing.
        var outOfReach = Ahead(Attack + 1f) with { IsAircraft = true };
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: true, new[] { outOfReach, structure }, out _));
        // An excluded aeroplane is not in reach either: a -1.0 target must not shield a structure.
        var excluded = Ahead(300f, bias: AiTargetRanking.NotRanked) with { IsAircraft = true };
        Assert.Equal(1, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: true, new[] { excluded, structure }, out _));
    }

    [Fact]
    public void AHullIsNeitherClassAndKeepsItsRankedPlaceUnderThePreference()
    {
        // A surface hull is a vehicle candidate that is not an aircraft, so the preference neither
        // promotes nor withdraws it: it competes on rank with the aeroplane beside it.
        var hull = Ahead(400f) with { Velocity = Vector3.Zero };
        var aeroplane = Ahead(1200f, xOffset: 50f) with { IsAircraft = true };
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: true, new[] { hull, aeroplane }, out _));
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftFirst: false, new[] { hull, aeroplane }, out _));
    }

    [ExtractedDataFact]
    public void TheShippedDefsAuthorTheBiasesTheDecodeMeasured()
    {
        // Read off vehicle.json, never typed in: a wingman def carries -100 target_bias and -200
        // struct_bias, a bare enemy AI def carries neither, and the player chain's own
        // player_airplane carries the -300 that makes a human the most attractive candidate.
        string zrdr = SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));
        var wingman = PlaneStats.LoadForAi(zrdr, "player_bhawk", "wbloodhawk");
        Assert.Equal(-100f, wingman.AiTargetBias, 1);
        Assert.Equal(-200f, wingman.AiStructBias, 1);

        var enemy = PlaneStats.LoadForAi(zrdr, "player_bhawk");
        Assert.Equal(0f, enemy.AiTargetBias, 1);
        Assert.Equal(0f, enemy.AiStructBias, 1);

        var human = PlaneStats.Load(zrdr, "player_bhawk");
        Assert.Equal(-300f, human.AiTargetBias, 1);
        Assert.Equal(0f, human.AiStructBias, 1);
    }

    // ---- goldens against the shipped extraction ------------------------------------------------

    [ExtractedDataFact]
    public void TheShippedSlotSixAndThirtyThreeCensusesHold()
    {
        // The measured counts: 414 blocks;
        // primary_target authored on 68 (27 of them "player"); rating_biases a list on 321.
        int blocks = 0, primaries = 0, playerPrimaries = 0, biased = 0, thirds = 0;
        int entries = 0, minusOnes = 0;
        foreach (var roster in AllRosters())
        {
            foreach (var (_, fields) in roster)
            {
                blocks++;
                if (AiSkills.RosterPrimaryTarget(fields) is { } primary)
                {
                    primaries++;
                    if (primary == "player")
                        playerPrimaries++;
                }

                var biases = AiSkills.RosterRatingBiases(fields);
                if (biases.Count > 0)
                    biased++;
                entries += biases.Count;
                minusOnes += biases.Count(b => b.Bias == -1f);
                thirds += biases.Count(b => b.Third != null);
            }
        }

        Assert.Equal(414, blocks);
        Assert.Equal(68, primaries);
        Assert.Equal(27, playerPrimaries);
        Assert.Equal(321, biased);
        Assert.Equal(697, entries);
        Assert.Equal(389, minusOnes); // biases run -1.0…1.0, both signs, -1.0 dominant
        // Measured 2026-08-13: every shipped entry is a [pattern, bias] PAIR, the exe
        // comment's third element is never authored in this install (0 triples across all 53
        // files). The reader still preserves one defensively when present.
        Assert.Equal(0, thirds);
    }

    // ---- the non-jet scorer, FUN_00421950 ------------------------------------------------------

    [Fact]
    public void TheNonJetScorerHasNoneOfTheThreeGeometryTerms()
    {
        // The same candidate scored both ways. The jet scorer's three ±0.2 terms are all on this
        // one (ahead +, level −, away −, netting −0.2); the ship/tank/plane/heli scorer has none
        // of them at all, so its weight is the bare 1.0 base.
        var c = Ahead(800f);
        Assert.Equal(1.0f + 0.2f - 0.2f - 0.2f, ScoreAs(AiScorer.Jet, c).Weight, 3);
        Assert.Equal(1.0f, ScoreAs(AiScorer.Other, c).Weight, 3);
    }

    [Fact]
    public void TheNonJetScorerKeepsTheThreeSharedTerms()
    {
        // What FUN_00421950 does have: the player's lower base weight, the wingman penalty and
        // the gasbag credit. Those are the shared half of the formula, not the jet's own.
        Assert.Equal(0.7f, ScoreAs(AiScorer.Other, Ahead(800f, isPlayer: true)).Weight, 3);
        var wingman = Ahead(800f);
        wingman.IsWingman = true;
        Assert.Equal(1.4f, ScoreAs(AiScorer.Other, wingman).Weight, 3);
        var gasbag = Ahead(800f);
        gasbag.IsGasbag = true;
        Assert.Equal(0.5f, ScoreAs(AiScorer.Other, gasbag).Weight, 3);
    }

    [Fact]
    public void TheNonJetScorerIgnoresTheShootersFacingEntirely()
    {
        // A hull's own heading changes nothing about what it picks: with no ahead/behind term
        // there is nothing left that reads ownForward. Two candidates equidistant fore and aft
        // therefore tie, where the jet scorer prefers the one astern by 480 rank units.
        var ahead = Ahead(800f);
        var astern = Ahead(800f);
        astern.Position = OwnPos + new Vector3(0f, 0f, 800f);
        astern.Velocity = new Vector3(0f, 0f, 60f); // flying away, so only the ahead term differs
        Assert.Equal(ScoreAs(AiScorer.Other, ahead).Rank, ScoreAs(AiScorer.Other, astern).Rank, 2);
        Assert.True(ScoreAs(AiScorer.Jet, astern).Rank < ScoreAs(AiScorer.Jet, ahead).Rank);
        // And reversing the hull's own nose leaves its ranking untouched.
        Assert.Equal(
            AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Other, ahead).Rank,
            AiTargetRanking.Score(OwnPos, -OwnFwd, Attack, AiScorer.Other, ahead).Rank, 2);
    }

    [Fact]
    public void TheNonJetScorerStillRespectsTheAttackRadiusAndDistance()
    {
        Assert.Equal(AiTargetRanking.NotRanked,
            ScoreAs(AiScorer.Other, Ahead(Attack + 1f)).Rank);
        Assert.Equal(400f,
            ScoreAs(AiScorer.Other, Ahead(900f)).Rank - ScoreAs(AiScorer.Other, Ahead(500f)).Rank, 1);
        Assert.Equal(0, AiTargetRanking.SelectBest(OwnPos, OwnFwd, Attack, AiScorer.Other,
            aircraftFirst: false, new[] { Ahead(500f), Ahead(900f) }, out _));
    }

    [Fact]
    public void AStandingTargetIsKeptWhileItStillScoresValid()
    {
        var camp = Ahead(1000f);
        camp.IsStructureClass = true;
        Assert.True(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: false, camp));

        // Out of the attack radius the re-score is NotRanked, which is the decoded drop.
        var gone = Ahead(Attack + 1f);
        gone.IsStructureClass = true;
        Assert.False(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: false, gone));

        // And an authored hard exclusion drops it where it stands.
        var excluded = Ahead(1000f, bias: AiTargetRanking.NotRanked);
        excluded.IsStructureClass = true;
        Assert.False(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: false, excluded));
    }

    [Fact]
    public void AnAircraftInReachWithdrawsAStandingStructureAndNothingElse()
    {
        var camp = Ahead(1000f);
        camp.IsStructureClass = true;
        Assert.False(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: true, camp));

        var enemy = Ahead(1000f);
        enemy.IsAircraft = true;
        Assert.True(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: true, enemy));

        // A surface hull is neither class, so the preference leaves it alone here exactly as
        // SelectBest's own withdrawal does.
        Assert.True(AiTargetRanking.KeepsStandingTarget(OwnPos, OwnFwd, Attack, AiScorer.Jet,
            aircraftWithdraws: true, Ahead(1000f)));
    }

    private static TargetScore ScoreAs(AiScorer scorer, in RankedTargetCandidate c) =>
        AiTargetRanking.Score(OwnPos, OwnFwd, Attack, scorer, c);

    // A candidate ahead of the shooter with every ±0.2 term on the same arm as its
    // peers: ahead (unfavourable), level with the shooter (level counts as below, favourable),
    // flying away (favourable).
    private static RankedTargetCandidate Ahead(float distance, bool isPlayer = false,
        float bias = 0f, int attackers = 0, float xOffset = 0f) => new()
        {
            Position = OwnPos + new Vector3(xOffset, 0f, -Mathf.Sqrt(distance * distance - xOffset * xOffset)),
            Velocity = new Vector3(0f, 0f, -60f),
            IsPlayer = isPlayer,
            ObjectiveBias = bias,
            AlliedAttackers = attackers,
        };

    private static float RankOf(in RankedTargetCandidate c) =>
        AiTargetRanking.Score(OwnPos, OwnFwd, Attack, AiScorer.Jet, c).Rank;

    private static IEnumerable<List<(string Name, List<object?> Fields)>> AllRosters()
    {
        string root = TestData.DataRoot!;
        foreach (string chapter in new[] { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" })
        {
            string dir = Path.Combine(root, "extracted", chapter);
            if (!Directory.Exists(dir))
                continue;
            foreach (string missionDir in Directory.EnumerateDirectories(dir))
            {
                // Chapter-scope dirs (anim/texture/zrdr/…) are not missions and have no
                // <dir>/zrdr scope; a mission dir without an aiv also just contributes nothing.
                string zrdr = SessionPaths.MissionZrdr(root, chapter, Path.GetFileName(missionDir));
                List<(string Name, List<object?> Fields)> roster;
                try
                {
                    roster = AiSkills.LoadRoster(zrdr);
                }
                catch (IOException)
                {
                    continue;
                }

                if (roster.Count > 0)
                    yield return roster;
            }
        }
    }
}
