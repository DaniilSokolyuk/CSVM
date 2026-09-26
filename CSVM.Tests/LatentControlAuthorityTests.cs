using System.IO;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The two control-authority terms the original carries and this install's data leaves at their
/// neutral values: the pitch-only high-speed fade and the opposing-command AOA/G limiter. Decode:
/// docs/org/flightModel.md, "Control authority vs speed" and "Torques and the limiters".
/// Neither can be exercised on a stock airframe, so every binding case here flies a SYNTHETIC
/// airframe that authors the term into reach, and each has a stock-data control beside it showing
/// the same instrument reading neutral (METHOD-9/METHOD-10). The stock envelope's own proof is the
/// eleven-airframe dump.
/// </summary>
public class LatentControlAuthorityTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = PhysicsConstants.MphToMs;

    private static readonly string[] AllPlanes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The fade's three regions on an airframe that authors it into reach: full authority
    /// up to the first speed, linear to zero at the second, zero beyond. The midpoint is what
    /// separates the ramp from a step at either end.</summary>
    [Theory]
    [InlineData(100f, 1f)]
    [InlineData(150f, 1f)]      // AT the knee: still full, the original's `>` test
    [InlineData(200f, 0.5f)]
    [InlineData(225f, 0.25f)]
    [InlineData(250f, 0f)]      // AT the top: gone
    [InlineData(400f, 0f)]
    public void ThePitchFadeIsLinearBetweenTheAuthoredKnees(float speedMph, float expected)
    {
        var m = new FlightModel(Faded());
        Assert.Equal(expected, m.PitchAuthorityAt(speedMph * Mph), 4);
    }

    /// <summary>Roll keeps everything the fade takes off pitch. Above <c>turn_fade_out</c> roll is
    /// pinned at 1 across the whole band where pitch falls to zero, which is the asymmetry the two
    /// axes sharing one curve hid.</summary>
    [Fact]
    public void TheFadeIsPitchOnlyAndRollKeepsFullAuthority()
    {
        var m = new FlightModel(Faded());
        foreach (float mph in new[] { 150f, 200f, 250f, 400f })
        {
            Assert.Equal(1f, m.RollAuthorityAt(mph * Mph), 5);
        }

        // Able to fail: pitch really is away from 1 over the same band.
        Assert.True(m.PitchAuthorityAt(200f * Mph) < 0.75f);
    }

    /// <summary>The two stages MULTIPLY. At 30 mph the base ramp is 0.5, and an airframe whose fade
    /// window also starts at 20 mph carries both, which a curve that replaced the ramp rather than
    /// scaling it would miss.</summary>
    [Fact]
    public void TheTwoStagesMultiplyWhereTheyOverlap()
    {
        var stats = Faded();
        stats.HighSpeedPitchFadeLo = 20f * Mph;
        stats.HighSpeedPitchFadeHi = 60f * Mph;
        var m = new FlightModel(stats);

        Assert.Equal(0.5f, m.RollAuthorityAt(30f * Mph), 4);          // base ramp, 10 → 50 mph
        Assert.Equal(0.5f * 0.75f, m.PitchAuthorityAt(30f * Mph), 4); // × (60 − 30) / (60 − 20)
    }

    /// <summary>The fade reaches rotation, not just the curve. Two plants fly full back stick from
    /// the same attitude, one inside the fade and one below it: the pitch rates differ by the fade
    /// and the roll rates do not differ at all.</summary>
    [Fact]
    public void TheFadeScalesPitchRateAndNotRollRate()
    {
        var inside = new FlightModel(Faded());
        var below = new FlightModel(Faded());
        inside.Reset(Vector3.Zero, Basis.Identity, 200f * Mph, 0f);
        below.Reset(Vector3.Zero, Basis.Identity, 100f * Mph, 0f);

        var input = new FlightInput { Pitch = 1f, Roll = 1f };
        inside.Step(input, Dt);
        below.Step(input, Dt);

        // Able to fail: the unfaded plant has real rotation on both axes.
        Assert.True(Mathf.Abs(below.BodyRates.X) > 1e-3f);
        Assert.True(Mathf.Abs(below.BodyRates.Z) > 1e-3f);

        Assert.Equal(0.5f, inside.BodyRates.X / below.BodyRates.X, 3);
        Assert.Equal(1f, inside.BodyRates.Z / below.BodyRates.Z, 3);
    }

    /// <summary>The stock control: on every shipped airframe the fade returns 1 at every speed the
    /// model can reach, so pitch and roll authority are the same number and the term is inert. The
    /// sweep runs to <c>MaxDiveSpeedFrac</c> × <c>fd_speed</c>, which is the model's own hard
    /// ceiling and so the most generous test available (METHOD-23).</summary>
    [ExtractedDataFact]
    public void TheFadeIsOutOfReachOnEveryStockAirframe()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            var m = new FlightModel(stats);
            float ceiling = 1.75f * stats.FdSpeed;
            for (float v = 0f; v <= ceiling; v += 2f * Mph)
            {
                Assert.True(m.PitchAuthorityAt(v) == m.RollAuthorityAt(v),
                    $"{plane}: at {v / Mph:0.0} mph pitch authority {m.PitchAuthorityAt(v):0.0000} is "
                    + $"away from roll's {m.RollAuthorityAt(v):0.0000}, high_speed_pitch_fade has "
                    + "come into reach, and a stock envelope row moves with it");
            }
        }
    }

    /// <summary>The AOA window's shape: 1 with the nose on the flight path, falling to zero at the
    /// authored <c>maxAOA</c> and staying there beyond. It is NOT a threshold, it is below 1 at
    /// every non-zero α, which is why it binds on stock data and the G half beside it does not.
    /// </summary>
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(20f, 0.8024f)]
    [InlineData(30f, 0.5612f)]
    [InlineData(45f, 0.0408f)]
    [InlineData(46f, 0f)]
    [InlineData(60f, 0f)]
    public void TheAoaWindowFallsToZeroAtMaxAoa(float alphaDeg, float expected)
    {
        var m = new FlightModel(Limited());
        float cos = Mathf.Cos(Mathf.DegToRad(alphaDeg));
        Assert.Equal(expected, m.OpposingCommandLimitAt(100f, cos, 0f, 1f), 3);
    }

    /// <summary>At the seam's default the window is replaced by 1, which is the recorded divergence
    /// and the reason the stock envelope does not move. The able-to-fail half is the same α at
    /// factor 1, which is far from 1.</summary>
    [Fact]
    public void TheAoaWindowIsHeldOffByTheSeam()
    {
        var m = new FlightModel(Limited());
        float cos = Mathf.Cos(Mathf.DegToRad(30f));
        Assert.Equal(1f, m.OpposingCommandLimitAt(100f, cos, 0f, 0f), 5);
        Assert.True(m.OpposingCommandLimitAt(100f, cos, 0f, 1f) < 0.6f);
    }

    /// <summary>The G ramp: 1 at <c>highGs[0]</c>, zero at <c>highGs[1]</c>, mirrored on the
    /// <c>lowGs</c> pair, and neutral in the band between the two starts. The negative side reads a
    /// SIGNED load factor, so it is reachable in an outside pull rather than unreachable by
    /// construction.</summary>
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(2f, 1f)]
    [InlineData(3f, 0.5f)]
    [InlineData(4f, 0f)]
    [InlineData(-2f, 1f)]
    [InlineData(-3f, 0.5f)]
    [InlineData(-4f, 0f)]
    public void TheGRampIsMirroredAboutTheAuthoredBand(float bodyUpG, float expected)
    {
        var m = new FlightModel(Limited());
        Assert.Equal(expected, m.OpposingCommandLimitAt(100f, 1f, bodyUpG, 1f), 4);
    }

    /// <summary>The ramp reads the load factor THIS step delivers, not the one the step before left
    /// behind: the original builds its forces and reads the ramp in one pass, before any torque
    /// accumulates and before its integrator rotates anything. Flown on the synthetic airframe whose
    /// ramp is in reach, and checked every step against the scalar rebuilt from the state that step
    /// entered with and the load factor it delivered.</summary>
    [Fact]
    public void TheGRampReadsTheSameStepsDeliveredLift()
    {
        var m = new FlightModel(Limited());
        m.Reset(Vector3.Zero, Basis.Identity, 300f * Mph, 1f);
        var s = m.Stats;
        int inRamp = 0, lateReadWouldDiffer = 0;
        for (int i = 0; i < 120; i++)
        {
            float entrySpeed = m.Speed;
            float lastStepsLoadFactor = m.BodyUpLoadFactor;
            m.Step(new FlightInput { Pitch = 1f, Throttle = 1f }, Dt);

            // Alpha and BodyUpLoadFactor both report the step just flown: α off the attitude it
            // entered with, the load factor off the lift that same entering attitude produced.
            float cosAlpha = Mathf.Cos(Mathf.DegToRad(m.Alpha));
            Assert.Equal(m.OpposingCommandLimitAt(entrySpeed, cosAlpha, m.BodyUpLoadFactor, 1f),
                         m.CommandLimit, 4);

            if (m.BodyUpLoadFactor > s.HighGStart || m.BodyUpLoadFactor < s.LowGStart)
                inRamp++;
            if (Mathf.Abs(m.OpposingCommandLimitAt(entrySpeed, cosAlpha, lastStepsLoadFactor, 1f)
                          - m.CommandLimit) > 1e-3f)
                lateReadWouldDiffer++;
        }

        // Able to fail: the ramp really is in reach over this pull, and a limiter handed the
        // PREVIOUS step's load factor would have applied a visibly different scalar, so the
        // comparison above is measuring the alignment rather than agreeing with itself.
        Assert.True(inRamp > 10, $"the G ramp was in reach on only {inRamp} of 120 steps");
        Assert.True(lateReadWouldDiffer > 10,
            $"a one-step-late read would have differed on only {lateReadWouldDiffer} of 120 steps");
    }

    /// <summary>The smaller of the two is what applies. At 30° α (window 0.561) against 3 G (ramp
    /// 0.5) the ramp wins; at 40° α (window 0.234) against 2.5 G (ramp 0.75) the window does. A max,
    /// or either term alone, reads right on one of the two and wrong on the other.</summary>
    [Fact]
    public void TheSmallerOfTheTwoTermsWins()
    {
        var m = new FlightModel(Limited());
        float cos30 = Mathf.Cos(Mathf.DegToRad(30f));
        float cos40 = Mathf.Cos(Mathf.DegToRad(40f));
        Assert.Equal(0.5f, m.OpposingCommandLimitAt(100f, cos30, 3f, 1f), 3);      // the G ramp
        Assert.Equal(0.2338f, m.OpposingCommandLimitAt(100f, cos40, 2.5f, 1f), 3); // the AOA window
    }

    /// <summary>A standstill removes a separating command outright: the original starts the scalar
    /// at zero and only leaves it there when there is no flight path to measure against.</summary>
    [Fact]
    public void AStandstillLeavesNoSeparatingAuthorityAtAll()
    {
        var m = new FlightModel(Limited());
        Assert.Equal(0f, m.OpposingCommandLimitAt(0f, 1f, 0f, 1f), 6);
    }

    /// <summary>The asymmetry, flown: at a load factor inside the authored <c>highGs</c> ramp, the
    /// pitch command that swings the nose further off the flight path is softened and the one that
    /// brings it back is not. Both plants enter with the same nose/path separation and the same
    /// stored load factor, so the only difference is the stick's direction.</summary>
    [Fact]
    public void OnlyTheSeparatingPitchCommandIsSoftened()
    {
        float away = SingleStepPitchRate(pitch: 1f);
        float back = SingleStepPitchRate(pitch: -1f);

        // Able to fail: both commands produce real rotation, in opposite directions.
        Assert.True(away > 1e-4f);
        Assert.True(back < -1e-4f);

        // The nose already leads the path, so +pitch separates and −pitch closes. The separating
        // one carries the limiter's scalar and the closing one is untouched, which is the whole
        // asymmetry: entry into a departure is damped, recovery from one is free.
        Assert.True(away < Mathf.Abs(back) * 0.75f,
            $"separating {away:0.00000} rad/s against closing {Mathf.Abs(back):0.00000}, the two "
            + "are within a quarter of each other, so the sign test is not selecting");
    }

    /// <summary>Roll carries no such test at all, in either direction. Read against the closed form
    /// for one step of roll command rather than against a second plant, because a plant flown
    /// without the limiter arrives at this entry with a different speed and so a different roll
    /// authority, the comparison that looks obvious is not a controlled one.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void RollIsNeverSoftened(float roll)
    {
        var m = FlyToLoadFactor(Limited());

        // Able to fail: the limiter is well away from 1 at this entry, so an axis that took it
        // would read visibly short of the closed form below.
        Assert.InRange(m.CommandLimit, 0.01f, 0.9f);

        var s = m.Stats;
        float expected = roll * s.RollTorque * s.RecInertia.Z * m.RollAuthorityAt(m.Speed) * Dt
                         * Mathf.Exp(-Dt * s.AngMomentumDamp);
        m.BodyRates = Vector3.Zero;
        m.Step(new FlightInput { Roll = roll, Throttle = 1f }, Dt);
        Assert.Equal(expected, m.BodyRates.Z, 6);
    }

    /// <summary>The limiter reaches the yaw axis on the same shared scalar and with the same sign
    /// rule. Flown in a sideslip, where the nose/path separation is about the YAW axis, so the two
    /// rudder directions are the separating and the closing one. The AOA window supplies the scalar
    /// here, which is also this suite's flown proof that the seam at 1 is the decoded term.</summary>
    [Fact]
    public void TheSeparatingYawCommandIsSoftenedAndTheClosingOneIsNot()
    {
        float away = SingleStepYawRate(yaw: 1f);
        float back = SingleStepYawRate(yaw: -1f);

        Assert.True(away > 1e-6f);
        Assert.True(back < -1e-6f);
        Assert.True(away < Mathf.Abs(back) * 0.75f,
            $"separating {away:0.0000000} rad/s against closing {Mathf.Abs(back):0.0000000}, the "
            + "rudder's two directions are within a quarter of each other, so the sign test is not "
            + "selecting on the yaw axis");
    }

    /// <summary>The seam's own control, flown: two plants identical but for
    /// <see cref="FlightModel.AoaLimiterFactor"/> fly the same sustained pull, and the one that
    /// spends the window ends up pitching visibly slower. The shipped default is 1, the decode, so
    /// the held-off plant is the one constructed explicitly here.</summary>
    [Fact]
    public void TheAoaSeamChangesTheFlownPitchRate()
    {
        var off = new FlightModel(Bare()) { AoaLimiterFactor = 0f };
        var on = new FlightModel(Bare());
        foreach (var m in new[] { off, on })
        {
            m.Reset(Vector3.Zero, Basis.Identity, 300f * Mph, 1f);
            for (int i = 0; i < 120; i++)
                m.Step(new FlightInput { Pitch = 1f, Throttle = 1f }, Dt);
        }

        Assert.True(off.BodyRates.X > 1e-3f);
        Assert.Equal(1f, off.CommandLimit, 5);
        Assert.True(on.CommandLimit < 0.9f);
        Assert.True(on.BodyRates.X < off.BodyRates.X * 0.9f,
            $"with the window spent the sustained pitch rate is {on.BodyRates.X:0.0000} rad/s "
            + $"against {off.BodyRates.X:0.0000} without it, the seam is not reaching the command");
    }

    // A synthetic airframe whose high_speed_pitch_fade window sits inside the flyable band
    // (150 → 250 mph) instead of past every attainable speed as the shipped data authors it.
    // Everything else is the ramp tests' hover plant, so nothing but the stick moves the rates.
    private static PlaneStats Faded()
    {
        var stats = Bare();
        stats.HighSpeedPitchFadeLo = 150f * Mph;
        stats.HighSpeedPitchFadeHi = 250f * Mph;
        return stats;
    }

    // A synthetic airframe whose limiter thresholds sit inside what the plant can produce: a 46°
    // maxAOA as shipped, but highGs/lowGs at ±2 → ±4 G against the shipped ±9 / −6.
    private static PlaneStats Limited()
    {
        var stats = Bare();
        stats.HighGStart = 2f;
        stats.HighGMax = 4f;
        stats.LowGStart = -2f;
        stats.LowGMax = -4f;
        return stats;
    }

    // The base synthetic airframe: the ramp tests' hover plant, with both G thresholds authored far
    // out of reach so the G half is neutral unless a test brings it in. PlaneStats' own fallbacks
    // are ±5/±9, which a hard pull at 300 mph does cross, so leaving them would put an uncontrolled
    // limiter inside every fade test here.
    private static PlaneStats Bare() => new()
    {
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 0f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 3500f,
        RefArea = 335f,
        DragFactor = 0.37f,
        EnginePower = 0.62f,
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
        YawLowSpeed = 0.0625f,
        YawHighSpeed = 0.17f,
        YawFadeIn = 10f * Mph,
        YawMax = 50f * Mph,
        YawFadeOut = 400f * Mph,
        MaxAoaCos = Mathf.Cos(Mathf.DegToRad(46f)),
        HighGStart = 900f,
        HighGMax = 1500f,
        LowGStart = -900f,
        LowGMax = -1500f,
    };

    // Fly a hard pull long enough to store a load factor inside the synthetic highGs ramp and to
    // open a nose/path separation, then hand the plant back with the stick released, so the caller's
    // single step is the only command the limiter ever sees.
    private static FlightModel FlyToLoadFactor(PlaneStats stats)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 300f * Mph, 1f);
        for (int i = 0; i < 19; i++)
            m.Step(new FlightInput { Pitch = 1f, Throttle = 1f }, Dt);
        return m;
    }

    // One further step's pitch rate from that entry, from a plant whose body rates are cleared so
    // the reading is this step's command and not what the entry left spinning.
    private static float SingleStepPitchRate(float pitch)
    {
        var m = FlyToLoadFactor(Limited());
        m.BodyRates = Vector3.Zero;
        m.Step(new FlightInput { Pitch = pitch, Throttle = 1f }, Dt);
        return m.BodyRates.X;
    }

    // One step of rudder from a SIDESLIP: the velocity is seeded a long way off the nose about the
    // yaw axis, so the closing axis lies on body Y and the two rudder directions separate.
    private static float SingleStepYawRate(float yaw)
    {
        var m = new FlightModel(Bare()) { AoaLimiterFactor = 1f };
        m.Reset(Vector3.Zero, Basis.Identity, 300f * Mph, 1f);
        m.SetVelocity(new Basis(Vector3.Up, Mathf.DegToRad(-30f)) * (Vector3.Forward * (300f * Mph)));
        m.Step(new FlightInput { Throttle = 1f }, Dt);
        m.BodyRates = Vector3.Zero;
        m.Step(new FlightInput { Yaw = yaw, Throttle = 1f }, Dt);
        return m.BodyRates.Y;
    }
}
