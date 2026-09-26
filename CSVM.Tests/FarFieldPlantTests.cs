using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's far-field plant: beyond 1 km from the nearest human pilot an AI aircraft flies a
/// speed-hold branch instead of the aerodynamics. Decode: docs/org/flightModel.md, "The far-field
/// plant". Each skipped term is asserted alone against a near-field control on the same airframe
/// and the same state (METHOD-7/METHOD-10), because an aggregate trajectory difference cannot say
/// WHICH of the five the branch dropped. The selection itself is pinned separately from the
/// behaviour, so a plant that reads the distance but spends the wrong terms fails distinctly.
/// </summary>
public class FarFieldPlantTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = PhysicsConstants.MphToMs;

    // The boundary in the units the plant is fed: squared metres, as the original compares them.
    private const float OnTheLine = 1000f * 1000f;

    /// <summary>The selection rule: an AI plant past 1000 m takes the far branch, one at or inside
    /// it does not. The at-the-line row is the strict `>` the original's FCOMP encodes; a `>=`
    /// would pass every other row here and fail only this one.</summary>
    [Theory]
    [InlineData(0f, false)]
    [InlineData(999f * 999f, false)]
    [InlineData(OnTheLine, false)]
    [InlineData(1001f * 1001f, true)]
    [InlineData(9000f * 9000f, true)]
    public void TheBranchIsSelectedAtOneKilometre(float distSq, bool expectFar)
    {
        var m = Flown(Bhawk(), aiPath: true, distSq, steps: 1);
        Assert.Equal(expectFar, m.FarFieldPlant);
    }

    /// <summary>A human's plant never takes it, at any range. The original's guard tests the player
    /// pointer before it measures anything, so a person flying 9 km from another person keeps the
    /// aerodynamics.</summary>
    [Fact]
    public void AHumanPlantIsNeverFarField()
    {
        var m = Flown(Bhawk(), aiPath: false, 9000f * 9000f, steps: 1);
        Assert.False(m.FarFieldPlant);
    }

    /// <summary>Crossing back inside restores the near-field plant on the very next step: the
    /// original re-tests the range every frame and carries no hysteresis and no timer.</summary>
    [Fact]
    public void TheBranchIsRedecidedEveryStep()
    {
        var m = new FlightModel(Bhawk(), aiForcePath: true);
        m.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        m.Step(Far(), Dt);
        Assert.True(m.FarFieldPlant);
        m.Step(Near(), Dt);
        Assert.False(m.FarFieldPlant);
        m.Step(Far(), Dt);
        Assert.True(m.FarFieldPlant);
    }

    /// <summary>Inside the boundary nothing changes at all. The same AI plant flown at a human's
    /// position and at 999 m from one lands on the identical state, which is the invariant the whole
    /// item promises and the control that stops the distance leaking into the near path.</summary>
    [Fact]
    public void InsideTheBoundaryTheNearFieldPlantIsUntouched()
    {
        var at = Flown(Bhawk(), aiPath: true, 0f, steps: 600, pitch: 0.4f, roll: -0.3f);
        var near = Flown(Bhawk(), aiPath: true, 999f * 999f, steps: 600, pitch: 0.4f, roll: -0.3f);

        Assert.Equal(at.Speed, near.Speed, 6);
        Assert.Equal(0f, (at.Position - near.Position).Length(), 5);
        Assert.Equal(0f, (at.BodyRates - near.BodyRates).Length(), 6);

        // Able to fail: the probe flew a real manoeuvre rather than sitting at its trim.
        Assert.True(at.Position.Length() > 100f);
        Assert.True(at.BodyRates.Length() > 0.1f);
    }

    /// <summary>The plant itself: the along-nose speed converges on throttle · fd_speed plus the
    /// AI's flat 5 m/s, from above and from below. Entering from both sides is what separates a
    /// speed-hold from a thrust term, which could only push one way.</summary>
    [Theory]
    [InlineData(1f, 60f)]
    [InlineData(1f, 260f)]
    [InlineData(0.5f, 60f)]
    [InlineData(0.5f, 260f)]
    [InlineData(0.25f, 200f)]
    public void TheFarPlantHoldsThrottleTimesFdSpeedAlongTheNose(float throttle, float entryMs)
    {
        var stats = Bhawk();
        var m = new FlightModel(stats, aiForcePath: true);
        m.Reset(Vector3.Zero, Basis.Identity, entryMs, throttle);
        for (int i = 0; i < 1800; i++)
            m.Step(Far(throttle), Dt);

        float expected = (stats.FdSpeed * throttle) + 5f;
        Assert.Equal(expected, m.Speed, 2);
        Assert.Equal(1f, (m.VelocityDir * m.Speed).Dot(-m.Attitude.Z) / m.Speed, 4);
    }

    /// <summary>The rate is 1/s, not the authored <c>lift_accel_rate</c>: the gap to the held speed
    /// decays by exactly one e-folding per second of sim. Flown on an airframe whose
    /// <c>lift_accel_rate</c> is 4, so a plant spending that rate here would close the gap in a
    /// quarter of the time and miss by a factor of e³.</summary>
    [Fact]
    public void TheFarPlantsRateIsOnePerSecond()
    {
        var stats = Bhawk();
        stats.LiftAccelRate = 4f;
        var m = new FlightModel(stats, aiForcePath: true);
        // Entered well above the AI's own 10 mph nose floor, which would otherwise seed the run.
        m.Reset(Vector3.Zero, Basis.Identity, 40f, 1f);
        float target = stats.FdSpeed + 5f;
        for (int i = 0; i < 60; i++)
            m.Step(Far(), Dt);

        // One second of a first-order lag at rate 1, stepped at 60 Hz: (1 − dt)^60, not exp(−1).
        float expected = target - ((target - 40f) * Mathf.Pow(1f - Dt, 60f));
        Assert.Equal(expected, m.Speed, 2);
    }

    /// <summary>Gravity is skipped. Flown hands-off and wings-level at 20 m/s, below the speed at
    /// which the wings can carry the weight, so the near-field control on the same airframe and
    /// state falls: at cruise it would hold altitude by construction and prove nothing.</summary>
    [Fact]
    public void TheFarPlantHasNoGravity()
    {
        var far = Flown(Bhawk(), aiPath: true, 4e6f, steps: 300, entryMs: 20f);
        var near = Flown(Bhawk(), aiPath: true, 0f, steps: 300, entryMs: 20f);

        Assert.Equal(0f, far.Position.Y, 4);
        Assert.Equal(0f, far.VelocityDir.Y, 5);
        Assert.True(near.Position.Y < -1f, $"the near-field control did not fall ({near.Position.Y} m)");
    }

    /// <summary>Bank coupling is skipped, which is the visible half of the item: a distant aircraft
    /// held in a 60° bank picks up no yaw at all, while the near-field control on the same bank
    /// picks up the decoded coupling. Rudder and stick are centred, so the coupling is the only
    /// thing that can write yaw.</summary>
    [Fact]
    public void TheFarPlantSkipsBankCoupling()
    {
        var stats = Bhawk();
        stats.ReturnRate = 0f;
        var banked = Basis.Identity.Rotated(Vector3.Forward, Mathf.DegToRad(60f));

        var far = new FlightModel(stats, aiForcePath: true);
        var near = new FlightModel(stats, aiForcePath: true);
        foreach (var m in new[] { far, near })
            m.Reset(Vector3.Zero, banked, 135f, 1f);

        far.Step(Far(), Dt);
        near.Step(Near(), Dt);

        Assert.Equal(0f, far.BodyRates.Length(), 7);
        Assert.True(Mathf.Abs(near.BodyRates.Y) > 1e-4f,
            $"the near-field control picked up no yaw ({near.BodyRates.Y}), the coupling is out of "
            + "reach in this attitude and the comparison says nothing");
    }

    /// <summary>The authority curves are skipped, all three forced to 1. Flown at 20 mph on an
    /// airframe whose low-speed ramp is well inside that: the far-field aircraft rolls at the full
    /// authored torque and the near-field control rolls slower, on the same stick.</summary>
    [Fact]
    public void TheFarPlantFliesAtFullControlAuthority()
    {
        var stats = Ramped();
        var far = new FlightModel(stats, aiForcePath: true);
        var near = new FlightModel(stats, aiForcePath: true);
        foreach (var m in new[] { far, near })
            m.Reset(Vector3.Zero, Basis.Identity, 20f * Mph, 0f);

        var far1 = Far();
        var near1 = Near();
        far1.Roll = 1f;
        near1.Roll = 1f;
        far.Step(far1, Dt);
        near.Step(near1, Dt);

        // The ramp reads 0.25 at 20 mph on this airframe, so the near control is a quarter of the
        // far one, a ratio, not merely "different", which a dropped multiply could also produce.
        Assert.Equal(stats.RollTorque * stats.RecInertia.Z * Dt, far.BodyRates.Z / Mathf.Exp(-Dt * stats.AngMomentumDamp), 4);
        Assert.Equal(0.25f, near.BodyRates.Z / far.BodyRates.Z, 3);
    }

    /// <summary>The opposing-command limiter is skipped: its scalar is forced to 1 rather than
    /// computed. Flown on the synthetic airframe whose G ramp is authored into reach, after a pull
    /// that stores a load factor inside it, so the near-field control reads a limit below 1 in the
    /// identical state.</summary>
    [Fact]
    public void TheFarPlantForcesTheCommandLimiterToOne()
    {
        var far = Loaded();
        var near = Loaded();
        far.Step(Far(), Dt);
        near.Step(Near(), Dt);

        Assert.Equal(1f, far.CommandLimit, 6);
        Assert.True(near.CommandLimit < 0.95f,
            $"the near-field control's limiter is not in reach ({near.CommandLimit}), the probe "
            + "no longer separates a forced 1 from a computed one");
    }

    /// <summary>The lift solve is skipped, so no load factor is delivered and none is measured. The
    /// two readouts keep the value the last near-field step left, which is the original's own
    /// behaviour: the far branch writes neither.</summary>
    [Fact]
    public void TheFarPlantComputesNoLift()
    {
        var m = Loaded();
        float demand = m.LoadFactorDemand;
        float bodyUp = m.BodyUpLoadFactor;
        Assert.True(demand > 0.5f, $"the probe never delivered a load factor ({demand})");

        for (int i = 0; i < 30; i++)
            m.Step(Far(), Dt);

        Assert.Equal(demand, m.LoadFactorDemand, 6);
        Assert.Equal(bodyUp, m.BodyUpLoadFactor, 6);
    }

    /// <summary>The ground blow is NOT skipped: the original calls it for every aircraft but a
    /// crashed player, outside the far-field guard. A far-field aircraft on a wall therefore still
    /// gets the AI arm's fixed response, which is the control that stops "far field" being read as
    /// "everything off".</summary>
    [Fact]
    public void TheFarPlantKeepsTheGroundBlow()
    {
        var stats = Bhawk();
        stats.GroundBlowElev = 60f;
        stats.GroundBlowMag = 1f;
        stats.AiGroundBlow = 5f;

        var m = new FlightModel(stats, aiForcePath: true);
        m.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        var input = Far();
        // Oblique on purpose: a dead-on normal leaves no escape axis and the original saves nobody.
        input.GroundBlowNormal = (Vector3.Back + (Vector3.Up * 0.5f)).Normalized();
        input.GroundBlowDistM = 10f;
        input.AiGroundBlowScale = 1f;
        m.Step(input, Dt);

        Assert.True(m.FarFieldPlant);
        Assert.True(m.BodyRates.Length() > 1e-3f,
            $"a far-field aircraft got no ground-blow response ({m.BodyRates.Length()} rad/s)");
    }

    // One AI plant with a load factor stored inside the synthetic G ramp and a nose/path separation
    // open, handed back with the stick released so the caller's own step is the only command the
    // limiter sees. The pull runs NEAR-field: the far branch delivers no lift to store.
    private static FlightModel Loaded()
    {
        var stats = Bhawk();
        stats.HighGStart = 1f;
        stats.HighGMax = 3f;
        stats.LowGStart = -1f;
        stats.LowGMax = -3f;
        var m = new FlightModel(stats, aiForcePath: true);
        m.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        var pull = Near();
        pull.Pitch = 1f;
        for (int i = 0; i < 120; i++)
            m.Step(pull, Dt);
        return m;
    }

    private static FlightModel Flown(PlaneStats stats, bool aiPath, float distSq, int steps,
        float pitch = 0f, float roll = 0f, float entryMs = 135f)
    {
        var m = new FlightModel(stats, aiForcePath: aiPath);
        m.Reset(Vector3.Zero, Basis.Identity, entryMs, 1f);
        var input = new FlightInput
        {
            Throttle = 1f,
            Pitch = pitch,
            Roll = roll,
            NearestHumanDistSqM = distSq,
        };
        for (int i = 0; i < steps; i++)
            m.Step(input, Dt);
        return m;
    }

    private static FlightInput Far(float throttle = 1f) =>
        new() { Throttle = throttle, NearestHumanDistSqM = 4e6f };

    private static FlightInput Near(float throttle = 1f) =>
        new() { Throttle = throttle, NearestHumanDistSqM = 0f };

    // The Bloodhawk's real dynamics, the fixture ForcePathSeamTests flies, with the G thresholds
    // left at the shipped values so nothing but a test that authors them can reach the limiter.
    private static PlaneStats Bhawk() => new()
    {
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 3f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1900f,
        RefArea = 330f,
        DragFactor = 0.37f,
        EnginePower = 0.62f,
    };

    // The same airframe with the roll ramp authored so 20 mph sits a quarter of the way up it.
    private static PlaneStats Ramped()
    {
        var stats = Bhawk();
        stats.ReturnRate = 0f;
        stats.TurnFadeIn = 10f * Mph;
        stats.TurnFadeOut = 50f * Mph;
        return stats;
    }
}
