using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The ported AI steering law (<see cref="AiControlLaw"/>, plan E41) against the decode in
/// docs/org/aiControlLaw.md. Engine-free and data-free: every case builds its own
/// <see cref="PlaneStats"/> so the arithmetic is pinned against the DECODED numbers rather than
/// against whatever the install happens to author.
///
/// <para>What these exist to catch is which branch a given geometry takes, which way
/// <c>rudder_tol</c> points, and above all the SIGN of <c>bz</c>: the renormalisation is the
/// ASTERN case, and the wings-level rule the straight-ahead one.</para>
/// </summary>
public class AiControlLawTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void TheFourParameterTablesCarryTheValuesReadOutOfTheImage()
    {
        Assert.Equal(0.3f, AiLawParams.Engaged.ThrottleMin);
        Assert.Equal(1.3f, AiLawParams.Engaged.SpeedCap);
        Assert.Equal(0.08f, AiLawParams.Engaged.CrossDeadband);
        Assert.Equal(0.01f, AiLawParams.Engaged.LevelDeadband);
        Assert.Equal(0.2f, AiLawParams.Engaged.LeadAlong);
        Assert.Equal(0.9f, AiLawParams.Engaged.FlipSpeed);

        Assert.Equal(0.8f, AiLawParams.Cruise.ThrottleMin);
        Assert.Equal(1.1f, AiLawParams.Cruise.SpeedCap);
        Assert.Equal(0.6f, AiLawParams.AvoidCrash.ThrottleMin);
        Assert.Equal(1.3f, AiLawParams.AvoidCrash.SpeedCap);
        Assert.Equal(0.4f, AiLawParams.Wingman.ThrottleMin);
        Assert.Equal(1.5f, AiLawParams.Wingman.SpeedCap);

        // Three of the four put the throttle ceiling ABOVE full; that is the original's range.
        Assert.True(AiLawParams.Engaged.SpeedCap > 1f);
        Assert.True(AiLawParams.AvoidCrash.SpeedCap > 1f);
        Assert.True(AiLawParams.Wingman.SpeedCap > 1f);

        // The cross-range lead coefficient is the one slot all four share.
        Assert.Equal(0.025f, AiLawParams.Engaged.LeadCross);
        Assert.Equal(0.025f, AiLawParams.Cruise.LeadCross);
        Assert.Equal(0.025f, AiLawParams.AvoidCrash.LeadCross);
        Assert.Equal(0.025f, AiLawParams.Wingman.LeadCross);
    }

    [Fact]
    public void TheLeadOffsetRampsBetweenItsTwoEndpointsAndFlattensOutside()
    {
        Assert.Equal(106.68f, AiControlLaw.LeadOffsetFor(0f));
        Assert.Equal(106.68f, AiControlLaw.LeadOffsetFor(20.576f));
        Assert.Equal(259.08, AiControlLaw.LeadOffsetFor(102.880005f), 2);
        Assert.Equal(259.08, AiControlLaw.LeadOffsetFor(400f), 2);

        // Linear between them: halfway in speed is halfway in offset.
        Assert.Equal((106.68 + 259.08) / 2.0,
            AiControlLaw.LeadOffsetFor((20.576f + 102.880005f) / 2f), 2);
    }

    [Fact]
    public void AnAimPointAheadAndRightBanksRightAndNeverTouchesTheRudder()
    {
        var input = AiControlLaw.Steer(Model(), new Vector3(100f, 400f, -100f), Vector3.Zero,
            AiLawParams.Cruise, 0.85f, Dt);

        // Roll + is bank LEFT, so a target on the right is a negative command, saturated because
        // the output stage multiplies by 3.5 against a limit of 1.
        Assert.Equal(-1f, input.Roll, 4);
        Assert.Equal(0f, input.Yaw);
        // The elevator only joins once the bank command is inside the deadband, and it is not.
        Assert.Equal(0f, input.Pitch);
    }

    [Fact]
    public void RudderTolPointsTheOtherWayRoundAndDecidesBankVersusRudder()
    {
        var aim = new Vector3(100f, 400f, -100f); // ahead and to the right

        // Stock 0.2: 45° off the nose is 0.707 of true horizontal error, which clears the
        // threshold on its own magnitude. The aircraft banks.
        var banked = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.Cruise, 0.85f, Dt);
        Assert.Equal(-1f, banked.Roll, 4);
        Assert.Equal(0f, banked.Yaw);

        // balmoral's authored 1.0 is a ceiling h can never EXCEED, so the threshold never selects
        // the bank branch and the same lateral-dominant error goes on the rudder instead. A higher
        // rudder_tol means MORE rudder, which is the reading D31 had inverted.
        var ruddered = AiControlLaw.Steer(Model(rudderTol: 1f), aim, Vector3.Zero,
            AiLawParams.Cruise, 0.85f, Dt);
        Assert.Equal(-1f, ruddered.Yaw, 4);
        Assert.Equal(0f, ruddered.Roll);
    }

    /// <summary>⚠ The renormalisation is the ASTERN case. `bz` is the aim point's component along
    /// the BACKWARD axis (row 2 of the basis at `+0x180`, negated at `0x00476339` to build the
    /// forward vector), so it is positive behind. Throwing the error's magnitude away and keeping
    /// its direction is how the original commits to a reversal; getting this backwards is what
    /// made every AI saw at full stick on a straight leg (`BL-387`).</summary>
    [Fact]
    public void AnAimPointAsternIsRenormalisedAndCommitsToFullBank()
    {
        // 0.19 of lateral against 0.98 of BEHIND. The true horizontal error is only 0.19, but
        // astern it is renormalised to 1, which clears rudder_tol and takes the bank branch.
        var input = AiControlLaw.Steer(Model(), new Vector3(190f, 400f, 980f), Vector3.Zero,
            AiLawParams.Cruise, 0.85f, Dt);

        Assert.Equal(-1f, input.Roll, 4);
        Assert.Equal(0f, input.Yaw);    // renormalised, so nothing falls through to the rudder
        Assert.Equal(0f, input.Pitch);  // and the bank command is far outside the 0.06 deadband
    }

    /// <summary>The same geometry AHEAD keeps its true magnitude, and 0.19 of lateral does not
    /// clear the stock 0.2. That is the branch the original's own AI was sampled in while flying
    /// a leg, and the reason a tracking aeroplane holds its course instead of wallowing.</summary>
    [Fact]
    public void TheSameErrorAheadKeepsItsMagnitudeAndGoesOnTheRudder()
    {
        var input = AiControlLaw.Steer(Model(), new Vector3(190f, 400f, -980f), Vector3.Zero,
            AiLawParams.Cruise, 0.85f, Dt);

        Assert.Equal(-0.666, input.Yaw, 2);  // -0.19033 x the 3.5 scale
        Assert.Equal(0f, input.Roll);        // the vertical component drives roll here, and it is 0
        Assert.Equal(0f, input.Pitch);
    }

    [Fact]
    public void TheWingsLevelRuleIsTheStraightAheadCaseAndRollsOutOfBank()
    {
        // Banked 30 degrees right, with the aim point straight AHEAD and level: both body
        // components are 0, which only survives unrenormalised, so it is reachable only in front.
        var banked = Basis.Identity.Rotated(Vector3.Forward, Mathf.DegToRad(30f));
        var input = AiControlLaw.Steer(Model(attitude: banked), new Vector3(0f, 400f, -1000f),
            Vector3.Zero, AiLawParams.Cruise, 0.85f, Dt);

        // 0.2 authority x sin(30) = 0.1, then the 3.5 scale, under the limit of 1.
        Assert.Equal(0.35, input.Roll, 3);
        Assert.True(input.Roll > 0f, "a right bank is levelled with a LEFT roll");
        Assert.Equal(0f, input.Pitch);
        Assert.Equal(0f, input.Yaw);
    }

    /// <summary>The plainest statement of `BL-387`: an aim point dead ahead and level needs no
    /// turn, so every channel must be still. Under the inverted `bz` this commanded full roll.</summary>
    [Fact]
    public void AnAimPointDeadAheadAndLevelCommandsNothingAtAll()
    {
        var input = AiControlLaw.Steer(Model(), new Vector3(0f, 400f, -8000f), Vector3.Zero,
            AiLawParams.Cruise, 0.85f, Dt);

        Assert.Equal(0f, input.Roll);
        Assert.Equal(0f, input.Pitch);
        Assert.Equal(0f, input.Yaw);
    }

    [Fact]
    public void EngagedRaisesEveryScaleAndLimitAndTheSkillFactorEasesThemAll()
    {
        var aim = new Vector3(100f, 400f, -100f);

        var idle = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.Engaged, 0.85f, Dt);
        Assert.Equal(-1f, idle.Roll, 4); // 3.5 scale clamped to the 1.0 limit

        // Engaged: +0.5 on the scale, +0.25 on the limit, +0.08 on the skill factor.
        var engaged = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.Engaged, 0.85f, Dt,
            engaged: true);
        Assert.Equal(-1.25 * 1.08, engaged.Roll, 4);

        // The skill factor multiplies whatever survived the limit.
        var eased = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.Engaged, 0.85f, Dt,
            skillFactor: 0.5f);
        Assert.Equal(-0.5, eased.Roll, 4);
    }

    [Fact]
    public void TheCrashRecoveryArmDropsTheSkillFactorAndFliesItsOwnSlowTarget()
    {
        var aim = new Vector3(100f, 400f, -100f);

        var normal = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.AvoidCrash, 0.85f,
            Dt, skillFactor: 0.5f);
        Assert.Equal(-0.5, normal.Roll, 4);

        // Emergency: the same geometry, the same skill factor, and the factor is simply not applied.
        var emergency = AiControlLaw.Steer(Model(), aim, Vector3.Zero, AiLawParams.AvoidCrash, 0.85f,
            Dt, emergency: true, skillFactor: 0.5f);
        Assert.Equal(-1f, emergency.Roll, 4);
    }

    [Fact]
    public void TheThrottleWalksTowardTheDesiredSpeedAndStopsAtTheTableFloor()
    {
        var aim = new Vector3(0f, 400f, -1000f);

        // Below the desired speed, the lever climbs by exactly 0.35/s.
        var up = AiControlLaw.Steer(Model(speed: 40f), aim, Vector3.Zero, AiLawParams.Cruise, 0.85f, Dt);
        Assert.Equal(0.85 + (0.35 * Dt), up.Throttle, 5);

        // Above it, the lever falls the same way, and the cruise table's 0.8 floor catches it.
        var down = AiControlLaw.Steer(Model(speed: 110f), aim, Vector3.Zero, AiLawParams.Cruise,
            0.802f, Dt);
        Assert.Equal(0.8, down.Throttle, 5);
    }

    [Fact]
    public void TheDesiredSpeedIsCappedAt250MphNoMatterHowFastTheAirframeIs()
    {
        // A Bloodhawk-class fd_speed chasing a 150 m/s target 1 km ahead, flying at 120 m/s. The
        // table would allow fd x 1.1 = 148.5 m/s and the lead terms ask for more still, but the
        // def's own AI clamp is a flat 111.76 m/s (250 mph) with no parser token behind it.
        var law = AiControlLaw.Steer(Model(fdSpeed: 135f, speed: 120f), new Vector3(0f, 400f, -1000f),
            new Vector3(0f, 0f, -150f), AiLawParams.Cruise, 0.85f, Dt);

        // The clamp is observable through the lever's direction alone: only a want UNDER the
        // current 120 m/s walks the throttle down. The airframe cap's 148.5 would walk it up.
        Assert.Equal(0.85 - (0.35 * Dt), law.Throttle, 5);
    }

    [Fact]
    public void TheAimAltitudeBandOpensOnlyForTheCallerThatAsksForIt()
    {
        // The danger-zone approach is the original's one caller that opens the band, around its own
        // solve alone. A point under the floor is flown to; closed, it is pulled up to the floor,
        // which commands less nose-down. Shallow aim points keep the elevator proportional.
        var under = new Vector3(0f, 5f, -12000f);
        Assert.True(Pitch(under, open: true) < Pitch(under, open: false));

        // The ceiling half of the same clamp, and the same exemption.
        var over = new Vector3(0f, 3000f, -12000f);
        Assert.True(Pitch(over, open: true) > Pitch(over, open: false));

        // An aim point already inside the band reads the same either way.
        var inside = new Vector3(0f, 420f, -12000f);
        Assert.Equal(Pitch(inside, open: false), Pitch(inside, open: true), 6);
    }

    // The elevator command for one aim point on the approach's own table, with the emergency arm
    // set so the roll branch is the same on both sides of the comparison.
    private static float Pitch(Vector3 aimPoint, bool open) =>
        AiControlLaw.Steer(Model(), aimPoint, Vector3.Zero, AiLawParams.AvoidCrash, 0.85f, Dt,
            emergency: true, openAltitudeBand: open).Pitch;

    private static FlightModel Model(float fdSpeed = 113f, float rudderTol = 0.2f,
        Basis? attitude = null, float speed = 80f)
    {
        var stats = new PlaneStats { FdSpeed = fdSpeed, RudderTol = rudderTol };
        var model = new FlightModel(stats);
        model.Reset(new Vector3(0f, 400f, 0f), attitude ?? Basis.Identity, speed, 0.85f);
        return model;
    }
}
