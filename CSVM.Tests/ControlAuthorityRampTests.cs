using System.IO;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's low-speed control-authority ramp. Decode:
/// docs/org/flightModel.md, "The low-speed ramp". Saturated at 1 across the whole speed band the
/// flight-envelope suite measures, so a green suite there says nothing here (verification.md
/// METHOD-10); this asserts the curve's shape and the rotation it produces where it bites.
/// The reverse-authority factor is asserted as a curve only, per
/// <see cref="FlightModel.ReverseAuthorityAt"/>'s own note.
/// </summary>
public class ControlAuthorityRampTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = PhysicsConstants.MphToMs;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The curve's three regions and both knees, on the authored 10/50 mph pair. The
    /// midpoint is the one that separates a linear ramp from a step at either end.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(10f, 0f)]      // AT turn_fade_in: still zero, as the original's `>` test gives
    [InlineData(20f, 0.25f)]
    [InlineData(30f, 0.50f)]
    [InlineData(40f, 0.75f)]
    [InlineData(50f, 1f)]      // AT turn_fade_out: full
    [InlineData(300f, 1f)]     // and held, roll and pitch have no high-speed fade
    public void TheRampIsLinearBetweenTheAuthoredKnees(float speedMph, float expected)
    {
        var m = new FlightModel(Authored());
        Assert.Equal(expected, m.RollAuthorityAt(speedMph * Mph), 4);
    }

    /// <summary>Read off the SHIPPED data rather than a fixture, which is what says the ramp runs on
    /// the authored 50 mph knee and not on the executable's compiled fallback of 40. At 45 mph the
    /// two readings are 0.875 and 1.0, the fallback has already saturated.</summary>
    [ExtractedDataFact]
    public void TheKneesComeFromTheAuthoredDataNotTheCompiledFallback()
    {
        var m = new FlightModel(PlaneStats.Load(ZrdrPath, "player_bhawk"));
        Assert.Equal(0.875f, m.RollAuthorityAt(45f * Mph), 3);
        Assert.NotEqual(1f, m.RollAuthorityAt(45f * Mph), 3);
    }

    /// <summary>The ramp reaches rotation, on BOTH axes, and in proportion. Two plants fly the same
    /// full-stick input from the same attitude, one at 30 mph (authority 0.5) and one at 60 (1.0),
    /// with the stall block and the weathervane taken out of the way so the stick command is the
    /// only thing that can move the body rates. One step, so no second-order term has a chance to
    /// accumulate: the rate ratio is then the authority ratio exactly.</summary>
    [Fact]
    public void TheRampScalesRollAndPitchRateAndNotYaw()
    {
        var slow = Hover();
        var fast = Hover();
        slow.Reset(Vector3.Zero, Basis.Identity, 30f * Mph, 0f);
        fast.Reset(Vector3.Zero, Basis.Identity, 60f * Mph, 0f);

        var input = new FlightInput { Pitch = 1f, Roll = 1f, Yaw = 1f };
        slow.Step(input, Dt);
        fast.Step(input, Dt);

        // Able to fail: the fast plant is at full authority on every axis, so all three rates are
        // non-trivial and a ratio read against them means something.
        Assert.True(Mathf.Abs(fast.BodyRates.X) > 1e-3f);
        Assert.True(Mathf.Abs(fast.BodyRates.Z) > 1e-3f);
        Assert.True(Mathf.Abs(fast.BodyRates.Y) > 1e-3f);

        Assert.Equal(0.5f, slow.BodyRates.X / fast.BodyRates.X, 3);
        Assert.Equal(0.5f, slow.BodyRates.Z / fast.BodyRates.Z, 3);

        // Yaw is on its own curve and must NOT pick up the ramp: over 30 → 60 mph the authored yaw
        // table is still climbing toward its 50 mph peak, so the ratio is neither 0.5 nor 1.
        float yawRatio = slow.BodyRates.Y / fast.BodyRates.Y;
        Assert.NotEqual(0.5f, yawRatio, 2);
        Assert.Equal(slow.YawAuthorityAt(30f * Mph) / slow.YawAuthorityAt(60f * Mph), yawRatio, 3);
    }

    /// <summary>At <c>turn_fade_in</c> and below there is no roll or pitch left at all, the end of
    /// the ramp described as "at 10 mph roll and pitch are gone entirely", and the one a
    /// clamped-to-a-floor implementation would quietly miss. The rudder still works there: its own
    /// curve bottoms out at <c>yaw_low_speed</c>, not at zero.</summary>
    [Fact]
    public void BelowTurnFadeInRollAndPitchAreGoneButTheRudderIsNot()
    {
        var m = Hover();
        m.Reset(Vector3.Zero, Basis.Identity, 8f * Mph, 0f);
        m.Step(new FlightInput { Pitch = 1f, Roll = 1f, Yaw = 1f }, Dt);

        Assert.Equal(0f, m.BodyRates.X, 6);
        Assert.Equal(0f, m.BodyRates.Z, 6);
        Assert.True(Mathf.Abs(m.BodyRates.Y) > 1e-4f, $"yaw rate {m.BodyRates.Y} rad/s");
    }

    /// <summary>The ramp scales the STICK only. Hands-off in a 45° bank at 8 mph, where roll and
    /// pitch authority are identically zero, the bank coupling must still drive the accumulator, so
    /// a slow aeroplane keeps the original's coordinated-turn cheat while having no controls. An
    /// implementation that scaled the whole accumulator instead of the command would read zero on
    /// every axis here.</summary>
    [Fact]
    public void TheBankCouplingIsNotFadedByTheRamp()
    {
        var m = Hover();
        m.Reset(Vector3.Zero, new Basis(Vector3.Forward, Mathf.DegToRad(45f)), 8f * Mph, 0f);
        m.Step(default, Dt);

        Assert.Equal(0f, m.RollAuthorityAt(8f * Mph), 6);
        Assert.True(Mathf.Abs(m.BodyRates.X) > 1e-4f, $"pitch rate {m.BodyRates.X} rad/s");
        Assert.True(Mathf.Abs(m.BodyRates.Y) > 1e-4f, $"yaw rate {m.BodyRates.Y} rad/s");
    }

    /// <summary>The reverse-authority factor's curve: 1.0 at and below <c>yaw_max</c>, then the yaw
    /// curve itself, floored at 0.2. Asserted against <see cref="FlightModel.YawAuthorityAt"/> rather
    /// than against copied numbers, because "it tracks the declining yaw curve" IS the decode; the
    /// floor and the discontinuity at yaw_max are the parts a re-derivation would get wrong.</summary>
    [ExtractedDataFact]
    public void TheReverseAuthorityFactorTracksTheYawCurveDownToItsFloor()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var m = new FlightModel(stats);

        // At or below yaw_max (authored 50 mph) it is identically 1, including where the yaw curve
        // itself is far below 1, the two are NOT the same function down there.
        Assert.Equal(1f, m.ReverseAuthorityAt(0f), 5);
        Assert.Equal(1f, m.ReverseAuthorityAt(20f * Mph), 5);
        Assert.True(m.YawAuthorityAt(20f * Mph) < 0.8f);
        Assert.Equal(1f, m.ReverseAuthorityAt(stats.YawMax), 5);

        // Above it, the yaw curve, declining, and still above the floor at cruise.
        foreach (float mph in new[] { 60f, 150f, 302f })
        {
            float v = mph * Mph;
            Assert.Equal(m.YawAuthorityAt(v), m.ReverseAuthorityAt(v), 5);
        }

        Assert.InRange(m.ReverseAuthorityAt(302f * Mph), 0.35f, 0.45f);   // ≈0.40 at cruise
        Assert.Equal(0.2f, m.ReverseAuthorityAt(500f * Mph), 5);          // floored past ≈345 mph
        Assert.True(m.YawAuthorityAt(500f * Mph) < 0.2f, "the floor must be able to bite");
    }

    /// <summary>Nothing in the force path reads the factor. This is not an
    /// omission, the assertion that breaks the day someone "restores" it as a torque scale. Full
    /// rudder at 400 mph against the same at 100: the achieved yaw-rate ratio is the YAW TABLE's
    /// ratio (0.193), and folding the factor in as well would put it at 0.044, so the two readings
    /// are four times apart rather than a rounding away.</summary>
    [Fact]
    public void TheReverseAuthorityFactorReachesNoForceTerm()
    {
        var fast = Hover();
        var slow = Hover();
        fast.Reset(Vector3.Zero, Basis.Identity, 400f * Mph, 1f);
        slow.Reset(Vector3.Zero, Basis.Identity, 100f * Mph, 1f);

        // Able to fail: the factor is well away from 1 on both plants and differs between them.
        Assert.Equal(0.2f, fast.ReverseAuthorityAt(400f * Mph), 5);
        Assert.InRange(slow.ReverseAuthorityAt(100f * Mph), 0.8f, 0.95f);

        var input = new FlightInput { Yaw = 1f, Throttle = 1f };
        fast.Step(input, Dt);
        slow.Step(input, Dt);

        float expected = fast.YawAuthorityAt(400f * Mph) / fast.YawAuthorityAt(100f * Mph);
        Assert.True(Mathf.Abs(slow.BodyRates.Y) > 1e-4f);
        Assert.Equal(expected, fast.BodyRates.Y / slow.BodyRates.Y, 4);
    }

    // The authored globals, as the shipped player.json carries them (verified against
    // `extracted/zrdr/player.zrd.json` and pinned by PlaneStatsFlightGlobalsTests).
    // Written out here so the curve tests do not need the extracted tree.
    private static PlaneStats Authored() => new()
    {
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
    };

    // A plant that can be flown slowly without the stall block or the weathervane taking
    // the nose: `return_rate` zero, and a weight/area pair whose computed stall speed is below
    // the whole ramp, so nothing but the stick moves the body rates.
    private static FlightModel Hover() => new(new PlaneStats
    {
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 0f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1f,
        RefArea = 330f,
        DragFactor = 0.37f,
        EnginePower = 0.62f,
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
        YawLowSpeed = 0.0625f,
        YawHighSpeed = 0.17f,
        YawFadeIn = 10f * Mph,
        YawMax = 50f * Mph,
        YawFadeOut = 400f * Mph,
    });
}
