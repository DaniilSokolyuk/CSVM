using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The beeper tag list (<see cref="BeeperTags{TAircraft}"/>) and its aircraft-free rules
/// (<see cref="BeeperTagRule"/>), decoded from <c>FUN_004b89c0</c>, <c>FUN_004b8ad0</c>,
/// <c>FUN_004b8ce0</c> and <c>FUN_004b8b50</c>. Every expectation is worked by hand from the
/// routines' literals over a fake subject, since the registry reads only position, in-play and
/// team off an aircraft. The selection rule is the point most likely to be re-derived wrongly:
/// the alignment dot runs FROM the tag TOWARD the round, so a tag dead ahead scores -1 and LOWER
/// is better, and both distance ratios are of SQUARED distances. A test written with the
/// intuitive sign, or with plain distances, would pass against the wrong rule.
/// </summary>
public class BeeperTagsTests
{
    private const float Dt = 1f / 60f;

    // The shipped wep_10 TIME, restated so the tests read as arithmetic; asserted against the
    // extracted file in TheShippedBeeperTimeReadsOffWeaponsJson.
    private const float ShippedTimeS = 20f;

    private const int Shooter = 1;
    private const int Enemy = 2;

    // The round sits at the origin flying toward -Z; "ahead" is -Z.
    private static readonly Vector3 RoundPos = Vector3.Zero;
    private static readonly Vector3 Heading = Vector3.Forward;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>A tag ahead scores near -1, one behind near +1, one abeam 0: the dot is inverted
    /// from the intuitive sign because the vector runs from the tag toward the round.</summary>
    [Fact]
    public void TheAlignmentDotIsInverted()
    {
        Assert.Equal(-1f, DotOf(Heading * 500f), 5);
        Assert.Equal(1f, DotOf(-Heading * 500f), 5);
        Assert.Equal(0f, DotOf(Vector3.Right * 500f), 5);
        Assert.Equal(0f, DotOf(RoundPos));
    }

    /// <summary>A better-aligned candidate wins under a squared-distance ratio of 1.2 and loses
    /// at it: 1.19 in, 1.2 and 1.21 out.</summary>
    [Fact]
    public void ABetterAlignedTagMayBeUpToTwentyPercentFartherInSquaredDistance()
    {
        Assert.True(BeeperTagRule.Prefers(candDot: -1f, candDistSq: 1.19f, bestDot: -0.5f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: -1f, candDistSq: 1.2f, bestDot: -0.5f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: -1f, candDistSq: 1.21f, bestDot: -0.5f, bestDistSq: 1f));
        // Any margin of alignment counts, however small.
        Assert.True(BeeperTagRule.Prefers(candDot: -0.5001f, candDistSq: 1.19f, bestDot: -0.5f, bestDistSq: 1f));
    }

    /// <summary>A worse-or-equally-aligned candidate must be STRICTLY nearer: an equal squared
    /// distance loses even with the alignment margin met.</summary>
    [Fact]
    public void AWorseAlignedTagMustBeStrictlyNearer()
    {
        Assert.False(BeeperTagRule.Prefers(candDot: -0.5f, candDistSq: 1f, bestDot: -0.5f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: -0.5f, candDistSq: 1.01f, bestDot: -0.5f, bestDistSq: 1f));
        Assert.True(BeeperTagRule.Prefers(candDot: -0.5f, candDistSq: 0.99f, bestDot: -0.5f, bestDistSq: 1f));
    }

    /// <summary>The margin, which only a candidate above 0.7 reaches: with the best at 0.75, a
    /// nearer candidate at 0.84 is taken, one at 0.851 and 0.86 are not. Below 0.7 the margin
    /// never decides: a nearer -0.4 beats a -0.5 outright.</summary>
    [Fact]
    public void ANearerTagBehindTheRoundMayGiveUpLessThanATenthOfAlignment()
    {
        Assert.True(BeeperTagRule.Prefers(candDot: 0.84f, candDistSq: 0.5f, bestDot: 0.75f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: 0.851f, candDistSq: 0.5f, bestDot: 0.75f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: 0.86f, candDistSq: 0.5f, bestDot: 0.75f, bestDistSq: 1f));
        Assert.True(BeeperTagRule.Prefers(candDot: -0.4f, candDistSq: 0.5f, bestDot: -0.5f, bestDistSq: 1f));
    }

    /// <summary>The 0.7 gate: a nearer tag with a dot at or under 0.7 is taken however much
    /// alignment it gives up, and one above 0.7 falls back to the margin.</summary>
    [Fact]
    public void ANearerTagAtOrUnderPointSevenIsTakenOutright()
    {
        // The best is dead ahead; the candidate is far off it but nearer.
        Assert.True(BeeperTagRule.Prefers(candDot: 0.7f, candDistSq: 0.5f, bestDot: -1f, bestDistSq: 1f));
        Assert.True(BeeperTagRule.Prefers(candDot: 0.69f, candDistSq: 0.5f, bestDot: -1f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: 0.71f, candDistSq: 0.5f, bestDot: -1f, bestDistSq: 1f));
        // Above 0.7 the margin still applies: a best at 0.75 is beaten by a nearer 0.8.
        Assert.True(BeeperTagRule.Prefers(candDot: 0.8f, candDistSq: 0.5f, bestDot: 0.75f, bestDistSq: 1f));
        Assert.False(BeeperTagRule.Prefers(candDot: 0.86f, candDistSq: 0.5f, bestDot: 0.75f, bestDistSq: 1f));
    }

    /// <summary>The list-level pick is a running best in tag order, so it is order-dependent in
    /// one window: a near tag 20° off at 500 m and a dead-ahead one at 545 m (squared ratio 1.19)
    /// each replace the other, and the LATER tag wins. At 550 m (ratio 1.21) the far one no
    /// longer steals, so the near one wins in either order.</summary>
    [Fact]
    public void TwoPaintedAircraftPickByTheRuleInTagOrder()
    {
        var nearOff = OffNose(20f, 500f);
        var farAhead = OffNose(0f, 500f * Mathf.Sqrt(1.19f));
        Assert.Same(farAhead, Painted(nearOff, farAhead).PickTarget(RoundPos, Heading));
        Assert.Same(nearOff, Painted(farAhead, nearOff).PickTarget(RoundPos, Heading));

        var tooFarAhead = OffNose(0f, 500f * Mathf.Sqrt(1.21f));
        Assert.Same(nearOff, Painted(nearOff, tooFarAhead).PickTarget(RoundPos, Heading));
        Assert.Same(nearOff, Painted(tooFarAhead, nearOff).PickTarget(RoundPos, Heading));
    }

    /// <summary>The 0.7 gate at list level: a nearer tag steals outright unless it sits more than
    /// about 134° off the nose (dot above 0.7). Against a far tag dead ahead tagged first, a nearer
    /// one at 140° cannot steal it, while one at 130° wins whichever was tagged first (tagged first
    /// it holds, since the far one is twice as far and cannot steal back).</summary>
    [Fact]
    public void ANearerTagBehindTheRoundCannotStealAFartherOneAhead()
    {
        var farAhead = OffNose(0f, 1000f);
        var nearBehind = OffNose(140f, 500f);
        Assert.Same(farAhead, Painted(farAhead, nearBehind).PickTarget(RoundPos, Heading));

        var nearAft = OffNose(130f, 500f);
        Assert.Same(nearAft, Painted(farAhead, nearAft).PickTarget(RoundPos, Heading));
        Assert.Same(nearAft, Painted(nearAft, farAhead).PickTarget(RoundPos, Heading));
    }

    /// <summary>The 0.1 margin at list level: with the best itself behind the round (140°, dot
    /// 0.766), a nearer tag at 145° (dot 0.819) gives up under 0.1 and wins; one at 155° (dot
    /// 0.906) gives up more and loses.</summary>
    [Fact]
    public void ANearerTagFurtherBehindWinsOnlyWithinTheMargin()
    {
        var best = OffNose(140f, 1000f);
        Assert.Same(best, Painted(best, OffNose(155f, 500f)).PickTarget(RoundPos, Heading));
        var within = OffNose(145f, 500f);
        Assert.Same(within, Painted(best, within).PickTarget(RoundPos, Heading));
    }

    /// <summary>An aircraft with no tag is never returned, and an empty list returns null.</summary>
    [Fact]
    public void AnUntaggedAircraftIsNeverPicked()
    {
        var tagged = At(Heading * 800f + Vector3.Right * 300f);
        var untagged = At(Heading * 200f);
        var tags = Painted(tagged);
        Assert.Same(tagged, tags.PickTarget(RoundPos, Heading));
        Assert.False(tags.IsPainted(untagged));
        Assert.Null(new BeeperTags<Subject>().PickTarget(RoundPos, Heading));
    }

    /// <summary>The countdown runs down by the step; the paint lasts TIME, and the frame that
    /// takes it to or below zero ends it: the aircraft is no longer picked, but the tag stays in
    /// the list.</summary>
    [Fact]
    public void TheCountdownRunsForTimeThenStopsPainting()
    {
        var a = At(Heading * 300f);
        var tags = new BeeperTags<Subject>();
        Assert.True(tags.TryTag(Shooter, a, 1f));
        Assert.Equal(1f, tags.RemainingFor(a));
        for (int i = 0; i < 59; i++)
        {
            tags.SimStep(Dt);
            Assert.Same(a, tags.PickTarget(RoundPos, Heading));
        }
        Assert.Equal(1f - 59f * Dt, tags.RemainingFor(a)!.Value, 4);
        tags.SimStep(Dt * 1.5f);
        Assert.Null(tags.PickTarget(RoundPos, Heading));
        Assert.False(tags.IsPainted(a));
        Assert.Null(tags.RemainingFor(a));
        Assert.Equal(1, tags.Count);
        Assert.Equal(0, tags.PaintingCount);
    }

    /// <summary>An expired tag is deleted only once its countdown sits at or below -5: five seconds
    /// of tail after expiry.</summary>
    [Fact]
    public void ATagIsDeletedFiveSecondsAfterExpiry()
    {
        var a = At(Heading * 300f);
        var tags = new BeeperTags<Subject>();
        tags.TryTag(Shooter, a, 1f);
        tags.SimStep(1f);         // exactly zero: paint ends, tag stays
        Assert.Equal(1, tags.Count);
        tags.SimStep(4.98f);      // -4.98: still in the tail
        Assert.Equal(1, tags.Count);
        tags.SimStep(0.03f);      // -5.01: gone
        Assert.Equal(0, tags.Count);
    }

    /// <summary>An aircraft leaving play slams its painting tag to -1 before that step's decrement,
    /// so it stops painting on the spot and is deleted four seconds later, not five.</summary>
    [Fact]
    public void ADeadAircraftCollapsesItsTagToMinusOne()
    {
        var a = At(Heading * 300f);
        var tags = new BeeperTags<Subject>();
        tags.TryTag(Shooter, a, ShippedTimeS);
        tags.SimStep(Dt);
        a.InPlay = false;
        tags.SimStep(0.5f);
        Assert.Null(tags.PickTarget(RoundPos, Heading));
        Assert.Equal(1, tags.Count);
        tags.SimStep(3.48f);      // -1 - 0.5 - 3.48 = -4.98
        Assert.Equal(1, tags.Count);
        tags.SimStep(0.03f);      // -5.01
        Assert.Equal(0, tags.Count);
    }

    /// <summary>The slam applies only while the tag paints: an aircraft dying during the tail
    /// does not restart the tail at -1.</summary>
    [Fact]
    public void ADeathDuringTheTailDoesNotRestartIt()
    {
        var a = At(Heading * 300f);
        var tags = new BeeperTags<Subject>();
        tags.TryTag(Shooter, a, 1f);
        tags.SimStep(3f);         // -2, in the tail
        a.InPlay = false;
        tags.SimStep(2.98f);      // -4.98, not -3.98
        Assert.Equal(1, tags.Count);
        tags.SimStep(0.03f);      // -5.01: gone, where a restarted tail would still hold it
        Assert.Equal(0, tags.Count);
    }

    /// <summary>The step's own arithmetic, in isolation.</summary>
    [Fact]
    public void TheStepSlamsThenDecrementsThenEndsThePaint()
    {
        float remaining = 3f;
        bool painting = true;
        Assert.Equal(3f - Dt, BeeperTagRule.Step(ref remaining, ref painting, Dt, subjectGone: false), 5);
        Assert.True(painting);
        Assert.Equal(-1f - Dt, BeeperTagRule.Step(ref remaining, ref painting, Dt, subjectGone: true), 5);
        Assert.False(painting);
        Assert.Equal(-1f - 2f * Dt, BeeperTagRule.Step(ref remaining, ref painting, Dt, subjectGone: true), 5);
    }

    /// <summary>A second beeper hit on an aircraft carrying a live tag makes no second tag and
    /// does NOT refresh the first; once the first has expired a new tag is taken, so the aircraft
    /// can carry a tail tag and a live one at once.</summary>
    [Fact]
    public void ALiveTagIsNeitherDoubledNorRefreshed()
    {
        var a = At(Heading * 300f);
        var tags = new BeeperTags<Subject>();
        Assert.True(tags.TryTag(Shooter, a, 2f));
        tags.SimStep(1f);
        Assert.False(tags.TryTag(Shooter, a, ShippedTimeS));
        Assert.Equal(1f, tags.RemainingFor(a)!.Value, 5);
        Assert.Equal(1, tags.Count);
        tags.SimStep(1f);         // expired, in the tail
        Assert.True(tags.TryTag(Shooter, a, ShippedTimeS));
        Assert.Equal(2, tags.Count);
        Assert.Equal(1, tags.PaintingCount);
        Assert.Same(a, tags.PickTarget(RoundPos, Heading));
    }

    /// <summary>The tagging gate: a shooter on the victim's team, or either side neutral, tags
    /// nothing; an aircraft out of play is not tagged; and only one tag is made per sim step.</summary>
    [Fact]
    public void TheTaggingGateRefusesFriendsNeutralsTheDeadAndASecondTagThisStep()
    {
        var tags = new BeeperTags<Subject>();
        Assert.False(tags.TryTag(Enemy, At(Heading * 300f), ShippedTimeS));
        Assert.False(tags.TryTag(AimAssist.NeutralTeam, At(Heading * 300f), ShippedTimeS));
        Assert.False(tags.TryTag(Shooter, At(Heading * 300f, team: AimAssist.NeutralTeam), ShippedTimeS));
        var dead = At(Heading * 300f);
        dead.InPlay = false;
        Assert.False(tags.TryTag(Shooter, dead, ShippedTimeS));
        Assert.Equal(0, tags.Count);

        var first = At(Heading * 300f);
        var second = At(Heading * 400f);
        Assert.True(tags.TryTag(Shooter, first, ShippedTimeS));
        Assert.False(tags.TryTag(Shooter, second, ShippedTimeS));
        tags.SimStep(Dt);
        Assert.True(tags.TryTag(Shooter, second, ShippedTimeS));
        Assert.Equal(2, tags.PaintingCount);
    }

    /// <summary>The four constants, as read out of the image.</summary>
    [Fact]
    public void TheConstantsAreTheRoutines()
    {
        Assert.Equal(1.2f, BeeperTagRule.BetterAlignedRatioSq);
        Assert.Equal(1.0f, BeeperTagRule.NearerRatioSq);
        Assert.Equal(0.7f, BeeperTagRule.NearerTakenAtOrBelowDot);
        Assert.Equal(0.1f, BeeperTagRule.NearerMarginDot);
        Assert.Equal(-1f, BeeperTagRule.DeadSlamS);
        Assert.Equal(-5f, BeeperTagRule.DeleteAtOrBelowS);
    }

    /// <summary>The shipped paint time, read out of the extracted weapons file rather than
    /// restated: wep_10 authors TIME 20, and it is the only BEEPER.</summary>
    [ExtractedDataFact]
    public void TheShippedBeeperTimeReadsOffWeaponsJson()
    {
        var defs = WeaponDefs.Load(ZrdrPath);
        Assert.Equal(ShippedTimeS, defs.Get("wep_10")!.BeeperTime);
        int beepers = 0;
        foreach (var d in defs.All)
        {
            if (d.BeeperTime != null)
                beepers++;
        }
        Assert.Equal(1, beepers);
    }

    private static Subject At(Vector3 pos, int team = Enemy) => new() { WorldPosition = pos, InPlay = true, Team = team };

    // A subject at deg off the round's nose (0 ahead, 180 behind) at range.
    private static Subject OffNose(float deg, float range) =>
        At((Heading * Mathf.Cos(Mathf.DegToRad(deg)) + Vector3.Right * Mathf.Sin(Mathf.DegToRad(deg))) * range);

    // A tag's dot for a subject at pos, the query's own arithmetic.
    private static float DotOf(Vector3 pos) => BeeperTagRule.AlignmentDot(RoundPos, Heading, pos);

    private static BeeperTags<Subject> Painted(params Subject[] subjects)
    {
        var tags = new BeeperTags<Subject>();
        foreach (var s in subjects)
        {
            Assert.True(tags.TryTag(Shooter, s, ShippedTimeS));
            tags.SimStep(Dt); // re-arm the one-per-step gate; the tags stay live
        }
        return tags;
    }

    private sealed class Subject : IBeeperSubject
    {
        public Vector3 WorldPosition { get; set; }

        public bool InPlay { get; set; }

        public int Team { get; set; }
    }
}
