using System.Collections.Generic;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>Which of the two decoded scorer implementations a shooter runs. The engine selects on
/// the SCORING vehicle's own <c>mode</c> at <c>0x0041fe75</c>, so this is a property of who is
/// looking and never of what is being looked at (docs/org/aiPilot.md "The scorer, and which one
/// runs").</summary>
public enum AiScorer
{
    /// <summary><c>FUN_00421ad0</c>, run by a <c>jet</c> or a <c>wingman</c>: the shared terms plus
    /// the three geometry ones (ahead/behind, altitude, closing).</summary>
    Jet,

    /// <summary><c>FUN_00421950</c>, run by everything else, a <c>ship</c>, a <c>plane</c>, a
    /// <c>heli</c>, a <c>tank</c>. ⚠ It has the shared terms and NOTHING else: a ground or sea AI
    /// scores on base weight and the two class terms alone. Do not let the three geometry terms
    /// reach it because they read like part of one formula.</summary>
    Other,
}

/// <summary>One target-selection candidate, as a snapshot. Built per acquisition by
/// whoever holds the live lists; the ranker reads nothing else, so the formula unit-tests
/// without a scene tree (the <see cref="AiModeMachine"/> pattern).</summary>
public struct RankedTargetCandidate
{
    /// <summary>World position.</summary>
    public Vector3 Position;

    /// <summary>The candidate's world velocity, m/s: the facing term dots it against the offset
    /// from the scorer, so a candidate flying AT the scorer is the unfavourable arm and a
    /// stationary one (a turret, a parked structure) the favourable one. Decoded as the
    /// candidate object's velocity virtual, not its nose (<c>FUN_00421ad0</c>).</summary>
    public Vector3 Velocity;

    /// <summary>Whether this candidate is a human-piloted aircraft, the 0.7 weight case.</summary>
    public bool IsPlayer;

    /// <summary>Whether this candidate is an aircraft flying in <c>wingman</c> mode (a netless
    /// formation escort), the +0.4 case: the engine de-prioritises enemy wingmen.</summary>
    public bool IsWingman;

    /// <summary>Whether this candidate is an aircraft. Read only by the aircraft-first preference
    /// (<see cref="AiTargetRanking.AircraftFirst"/>), never by the decoded arithmetic, which has no
    /// class priority at all.</summary>
    public bool IsAircraft;

    /// <summary>Whether this candidate came from the turret or the structure pool, the two classes
    /// the aircraft-first preference suppresses while an aircraft ranks. A surface hull is a
    /// vehicle candidate and carries neither this nor <see cref="IsAircraft"/>.</summary>
    public bool IsStructureClass;

    /// <summary>The class-dependent rank term, already in rank units. A vehicle candidate carries
    /// its own <c>target_bias</c> (<see cref="Airframe.PlaneStats.AiTargetBias"/>), a turret or
    /// structure candidate the SCORER's <c>struct_bias</c>
    /// (<see cref="Airframe.PlaneStats.AiStructBias"/>). ⚠ Shipped negative against a minimised
    /// rank, so it attracts.</summary>
    public float ClassBias;

    /// <summary>Whether this candidate is a zeppelin gasbag, the −0.5 case. ⚠ The caller admits
    /// a gasbag only to a pilot with live gasbag ordnance (docs/org/aiPilot.md "The gasbag gate is
    /// ordnance, checked at admission"); the scorer never sees one otherwise.</summary>
    public bool IsGasbag;

    /// <summary>The objectiveBias term, already in rank units
    /// (<see cref="AiTargetRanking.ObjectiveBiasFor"/>).</summary>
    public float ObjectiveBias;

    /// <summary>How many ALLIES already hold this candidate as their standing target, the
    /// deconfliction input. Zero whenever every pilot sits on its own default team (free flight,
    /// <c>--vs</c>); it counts real allies once a mission puts two AI on the same
    /// <see cref="Airframe.FlightController.Team"/>.</summary>
    public int AlliedAttackers;
}

/// <summary>One candidate's rank and the inputs that made it, the observability shape the
/// first-acquisition breadcrumb logs.</summary>
public readonly struct TargetScore
{
    public TargetScore(float weight, float distance, float bias, float rank)
    {
        Weight = weight;
        Distance = distance;
        Bias = bias;
        Rank = rank;
    }

    /// <summary>The final weight after the ±0.2 terms (0.7/1.0 base).</summary>
    public float Weight { get; }

    /// <summary>Straight-line distance, metres.</summary>
    public float Distance { get; }

    /// <summary>The bias terms in rank units, the roster's objective bias plus the def's class
    /// bias, so the rank below is reproducible from this readout alone.</summary>
    public float Bias { get; }

    /// <summary>weight × 1200 + distance + objectiveBias, minimised; <see
    /// cref="AiTargetRanking.NotRanked"/> beyond the attack radius.</summary>
    public float Rank { get; }
}

/// <summary>The decoded target-ranking formula (<c>FUN_00421ad0</c>, docs/org/aiPilot.md "Target
/// acquisition"): <c>rank = weight × 1200 + distance + objectiveBias</c>, MINIMISED, player base
/// weight 0.7, others 1.0, +0.4 on a wingman, ±0.2 terms for ahead/behind, altitude sign and
/// closing, −0.5 on a gasbag, <c>1e21</c> beyond the attack radius. <see cref="SelectBest"/>
/// prefers a candidate no ally already holds (<see cref="RankedTargetCandidate.AlliedAttackers"/>),
/// falling back to the overall best when the pool is exhausted, the design's deconfliction, a
/// minimum reading. The class biases go in raw beside the objective one
/// (<see cref="RankedTargetCandidate.ClassBias"/>). ⚠ Unported: the attack volume is the
/// engine's cylinder (horizontal radius and an altitude band); this scores a sphere.</summary>
public static class AiTargetRanking
{
    /// <summary>The decoded weight scale: one weight unit is worth 1200 m of distance.</summary>
    public const float WeightScale = 1200f;

    /// <summary>The player's base weight, the decoded hard constant.
    /// ⚠ Under minimisation this ranks the player AHEAD of an equal-distance AI target, by about
    /// 360 m of distance equivalence. That is the decoded arithmetic; do not "fix" it to rank the
    /// player last.</summary>
    public const float PlayerWeight = 0.7f;

    /// <summary>Every other target's base weight.</summary>
    public const float BaseWeight = 1f;

    /// <summary>Magnitude of the ahead-behind / altitude-sign / closing weight terms.</summary>
    public const float TermWeight = 0.2f;

    /// <summary>The ahead/behind term's deadband, in METRES of the un-normalised offset dotted
    /// with the scorer's forward: past +0.5 ahead adds the term, past −0.5 behind subtracts it,
    /// and the half-metre between adds nothing. Not a cone.</summary>
    public const float AheadDeadbandM = 0.5f;

    /// <summary>The weight added against a candidate in <c>wingman</c> mode, 480 m under
    /// minimisation.</summary>
    public const float WingmanWeight = 0.4f;

    /// <summary>The weight taken off a zeppelin gasbag, 600 m in its favour.</summary>
    public const float GasbagWeight = 0.5f;

    /// <summary>The engine's own out-of-activation score: never picked.</summary>
    public const float NotRanked = 1e21f;

    /// <summary>The name a human-piloted candidate answers to in a roster's <c>primary_target</c>
    /// and <c>rating_biases</c>. A role, not a node name (C22): the roster's own player block is
    /// called this and the human rigs are <c>player1</c>/<c>player2</c>, so matching the node name
    /// alone would leave 157 of the install's 697 authored bias entries dead.</summary>
    public const string PlayerRole = "player";

    /// <summary>An authored <c>rating_biases</c> weight resolves to rank units directly and
    /// NEGATED, so a positive bias attracts under minimisation (docs/formats/ai-rosters.md).
    /// ⚠ Not the weight scale: a bias is not in metres and is not multiplied by 1200.</summary>
    public const float BiasScale = -750f;

    /// <summary>The rank a bias of 1.0 or more collapses to: always target.</summary>
    public const float AlwaysTarget = -100000f;

    /// <summary>A turret candidate's flat objectiveBias addition, on top of every arm including the
    /// no-match case (docs/formats/ai-rosters.md, `FUN_0041ae40`). Independent of
    /// <c>rating_biases</c>: it applies whether or not the roster names the turret at all.</summary>
    public const float TurretBiasFlat = 37.5f;

    /// <summary>Whether a picker ranks live enemy aircraft ahead of every turret and structure
    /// candidate, fighting a structure only when no aircraft is in reach. A deliberate departure
    /// from the decoded picker, which has no class priority; <c>--ai-targeting=decoded</c> turns it
    /// off and leaves the biases alone, so the original's arithmetic can be flown against this one.
    /// It settles once per launch and no consumer chooses it, which is why it is a static and not a
    /// per-pilot value.</summary>
    public static bool AircraftFirst { get; set; } = true;

    /// <summary>One candidate's rank and its inputs, the decoded arithmetic term for term.
    /// <paramref name="ownForward"/> must be unit-length (a basis column), and is unread under
    /// <see cref="AiScorer.Other"/>. ⚠ Ahead is the UNFAVOURABLE arm and so is being above the
    /// scorer: do not "fix" either sign to the design document's front-arc reading.
    /// ⚠ <paramref name="attackRange"/> is the SCORER's ATTACK volume, never its activation one
    /// (docs/org/aiPilot.md). <paramref name="scorer"/> has no default: the wrong one is silent.</summary>
    public static TargetScore Score(Vector3 ownPos, Vector3 ownForward, float attackRange,
        AiScorer scorer, in RankedTargetCandidate c)
    {
        var to = c.Position - ownPos;
        float dist = to.Length();
        float bias = c.ObjectiveBias + c.ClassBias;
        if (dist > attackRange)
            return new TargetScore(0f, dist, bias, NotRanked);
        float weight = c.IsPlayer ? PlayerWeight : BaseWeight;
        if (c.IsWingman)
            weight += WingmanWeight;
        if (scorer == AiScorer.Jet)
        {
            // Ahead/behind on the raw offset: a half-metre deadband, then ±0.2 either side.
            float ahead = to.Dot(ownForward);
            if (ahead > AheadDeadbandM)
                weight += TermWeight;
            else if (ahead < -AheadDeadbandM)
                weight -= TermWeight;
            // Altitude sign: strictly above the scorer is the unfavourable arm, level counts as below.
            weight += to.Y > 0f ? TermWeight : -TermWeight;
            // Closing: a candidate whose velocity points back at the scorer is the unfavourable arm;
            // a stationary one, or one flying away, the favourable.
            weight += c.Velocity.Dot(to) < 0f ? TermWeight : -TermWeight;
        }

        if (c.IsGasbag)
            weight -= GasbagWeight;
        return new TargetScore(weight, dist, bias, weight * WeightScale + dist + bias);
    }

    /// <summary>The pick: the minimal-rank candidate no ally already holds; when every ranked
    /// candidate is held, the minimal-rank candidate outright (the design's exhausted-pool
    /// fallback). Returns the candidate's index and its score, or −1 when nothing ranks (all
    /// beyond the attack radius, or the list is empty). <paramref name="aircraftFirst"/> engages the
    /// preference (<see cref="AircraftFirst"/>); it has no default because a picker that silently
    /// took the wrong one would disagree with the pickers beside it.</summary>
    public static int SelectBest(Vector3 ownPos, Vector3 ownForward, float attackRange,
        AiScorer scorer, bool aircraftFirst, IReadOnlyList<RankedTargetCandidate> candidates,
        out TargetScore best)
    {
        bool dropStructures = aircraftFirst
            && AnyAircraftRanks(ownPos, ownForward, attackRange, scorer, candidates);
        int bestAny = -1, bestFree = -1;
        TargetScore scoreAny = default, scoreFree = default;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (dropStructures && candidates[i].IsStructureClass)
                continue;
            var s = Score(ownPos, ownForward, attackRange, scorer, candidates[i]);
            if (s.Rank >= NotRanked)
                continue;
            if (bestAny < 0 || s.Rank < scoreAny.Rank)
            {
                bestAny = i;
                scoreAny = s;
            }

            if (candidates[i].AlliedAttackers == 0 && (bestFree < 0 || s.Rank < scoreFree.Rank))
            {
                bestFree = i;
                scoreFree = s;
            }
        }

        if (bestFree >= 0)
        {
            best = scoreFree;
            return bestFree;
        }

        best = scoreAny;
        return bestAny;
    }

    /// <summary>Whether a standing target survives this tick's re-score, the decoded hold's own
    /// test: kept while it still scores under <see cref="NotRanked"/>, dropped the moment it does
    /// not, whereupon the caller sweeps the pool again. <paramref name="aircraftWithdraws"/> is
    /// CSVM's layer over that decode and not the original's: an enemy aeroplane in reach takes a
    /// turret or structure target away at once rather than at the hold's end.</summary>
    public static bool KeepsStandingTarget(Vector3 ownPos, Vector3 ownForward, float attackRange,
        AiScorer scorer, bool aircraftWithdraws, in RankedTargetCandidate standing) =>
        !(aircraftWithdraws && standing.IsStructureClass)
        && Score(ownPos, ownForward, attackRange, scorer, standing).Rank < NotRanked;

    /// <summary>The objectiveBias term for a named candidate, from the FIRST matching
    /// <c>rating_biases</c> entry; 0 with no list or no match. The ends saturate rather than scale:
    /// 1.0 or more is always-target, −1.0 or less a hard exclusion, an authored −1.0 means NEVER
    /// target, not a penalty, and is the dominant shipped value. <paramref name="isTurret"/> adds
    /// <see cref="TurretBiasFlat"/> on top of every arm above, including the no-match case, the
    /// decoded flat term a turret candidate always carries.</summary>
    public static float ObjectiveBiasFor(string name, IReadOnlyList<AiRatingBias>? biases,
        bool isTurret = false) => ObjectiveBiasFor(name, null, biases, isTurret);

    /// <summary>The same term for a candidate that is PART of larger named entities:
    /// <paramref name="owners"/> is the chain of names it ALSO answers to, nearest first (a
    /// zeppelin zone's airship, then the tree above a mission structure's damage pool).
    /// ⚠ The list is walked ONCE PER NAME, not once with every name tried per entry: the decode
    /// re-walks it whole at each level, so a candidate's OWN name beats an owner's entry standing
    /// earlier in authored order (docs/org/aiPilot.md).</summary>
    public static float ObjectiveBiasFor(string name, IReadOnlyList<string>? owners,
        IReadOnlyList<AiRatingBias>? biases, bool isTurret = false)
    {
        float flat = isTurret ? TurretBiasFlat : 0f;
        if (biases == null)
            return flat;
        if (MatchedBias(name, biases, flat) is { } own)
            return own;
        if (owners == null)
            return flat;
        foreach (var owner in owners)
        {
            if (owner is { Length: > 0 } && MatchedBias(owner, biases, flat) is { } above)
                return above;
        }

        return flat;
    }

    // Whether one live aircraft is in reach, which is what withdraws the turret and structure
    // candidates under the preference. Ranking is the reach test: it carries the attack radius
    // and an authored hard exclusion alike, so an aircraft nobody may shoot does not shield a
    // structure from being picked.
    private static bool AnyAircraftRanks(Vector3 ownPos, Vector3 ownForward, float attackRange,
        AiScorer scorer, IReadOnlyList<RankedTargetCandidate> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].IsAircraft
                && Score(ownPos, ownForward, attackRange, scorer, candidates[i]).Rank < NotRanked)
                return true;
        }

        return false;
    }

    // One level of the chain: the first entry this one name matches, in rank units, or null when
    // the whole list passes the name over and the next name up takes its own pass.
    private static float? MatchedBias(string name, IReadOnlyList<AiRatingBias> biases, float flat)
    {
        foreach (var b in biases)
        {
            if (!b.Matches(name))
                continue;
            if (b.Bias >= 1f)
                return AlwaysTarget + flat;
            if (b.Bias <= -1f)
                return NotRanked + flat;
            return b.Bias * BiasScale + flat;
        }

        return null;
    }
}
