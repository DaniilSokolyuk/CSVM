using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Why the autogyro's low-speed nose-down reads softer than another airframe's, term by term: it is
/// the authored wing loading moving the stall speed, not a weaker drop. Every term the drop passes
/// through is decoded (docs/org/flightModel.md, "The nose-drop's rate is stall_mag" and "Why the
/// autogyro's low-speed nose-down is softer"), and the two the airframe could differ on,
/// rec_moments_inertia.x and ang_momentum_damp, are authored identical to the Bloodhawk's.
/// These also separate the plant's own authority from the AI law's low-speed recovery arm, which
/// commands the nose down only when it is UP and slow and never reaches a player-flown aeroplane.
/// </summary>
public class AutogyroStallNoseDownTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = 0.44704f;

    /// <summary>The nose-drop is stall_mag (one global) × flag × rec_moments_inertia.x, damped by
    /// ang_momentum_damp, and the autogyro authors the Bloodhawk's 1.18 and 5.0. So at the same
    /// depth of its own stall the two airframes drop at the same rate to the last digit. The control
    /// is the same autogyro carrying the Balmoral's authored 0.4 inertia, which does drop softer:
    /// if that passes as equal, this test is not measuring the term it claims to.</summary>
    [Fact]
    public void AtEqualStallDepthTheAutogyroDropsExactlyAsHardAsTheBloodhawk()
    {
        float gyro = OneStalledStepRate(Autogyro(), out float gyroFlag);
        float hawk = OneStalledStepRate(Bhawk(), out float hawkFlag);
        Assert.True(Mathf.Abs(gyroFlag - hawkFlag) < 0.01f,
            $"the fixtures must sit at the same stall depth: {gyroFlag:0.000} against {hawkFlag:0.000}");
        // Within a percent, not to the bit: the lift cap carries a Mach term, so two airframes at
        // nine tenths of their own stall sit at slightly different flags.
        Assert.True(Mathf.Abs(gyro - hawk) < 0.01f * Mathf.Abs(hawk),
            $"autogyro {gyro:0.0000} °/s against Bloodhawk {hawk:0.0000} °/s at the same stall depth");

        var soft = Autogyro();
        soft.RecInertia = new Vector3(0.4f, soft.RecInertia.Y, soft.RecInertia.Z);
        float softer = OneStalledStepRate(soft, out _);
        Assert.True(Mathf.Abs(softer - hawk) > 0.1f * Mathf.Abs(hawk),
            "the control must separate: an airframe authoring a smaller rec_moments_inertia.x drops "
            + $"softer, and this one read {softer:0.0000} °/s against {hawk:0.0000}");
    }

    /// <summary>Where the softness comes from: 500 lb over 800 ft² is a ninth of the Bloodhawk's
    /// wing loading, which puts the autogyro's stall at 18.5 mph against 56.5. At 40 mph, a speed a
    /// pilot calls low, the Bloodhawk has broken and the autogyro has no nose-drop term at all,
    /// because its flag is still negative.</summary>
    [Fact]
    public void TheAutogyroCarriesNoNoseDropWhereTheBloodhawkHasAlreadyBroken()
    {
        var gyro = new FlightModel(Autogyro());
        var hawk = new FlightModel(Bhawk());
        Assert.True(Mathf.Abs((gyro.StallSpeed / Mph) - 18.5f) < 0.2f,
            $"the autogyro's computed stall is {gyro.StallSpeed / Mph:0.0} mph");
        Assert.True(Mathf.Abs((hawk.StallSpeed / Mph) - 56.5f) < 0.2f,
            $"the Bloodhawk's computed stall is {hawk.StallSpeed / Mph:0.0} mph");

        gyro.Reset(Vector3.Zero, Basis.Identity, 40f * Mph, 0f);
        hawk.Reset(Vector3.Zero, Basis.Identity, 40f * Mph, 0f);
        Assert.False(gyro.isStalled(), $"the autogyro must not be stalled at 40 mph (flag {gyro.StallFlag:0.000})");
        Assert.True(hawk.isStalled(), $"the Bloodhawk must be stalled at 40 mph (flag {hawk.StallFlag:0.000})");

        gyro.Step(new FlightInput(), Dt);
        hawk.Step(new FlightInput(), Dt);
        Assert.Equal(0f, gyro.BodyRates.X);
        Assert.True(hawk.BodyRates.X < -1e-3f,
            $"the Bloodhawk's nose must be pushed down at 40 mph, and it read {hawk.BodyRates.X:0.0000} rad/s");
    }

    /// <summary>The authored low-speed ramp (turn_fade_in 10 / turn_fade_out 50 mph) is the other
    /// candidate the report named, and it scales the STICK only. Deep in the autogyro's stall the
    /// ramp has taken most of the elevator, and the drop still arrives at its full decoded size; the
    /// control is the same figure scaled by the ramp, which the measurement must not match.</summary>
    [Fact]
    public void TheLowSpeedRampScalesTheStickAndNotTheDrop()
    {
        var stats = Autogyro();
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 0.9f * m.StallSpeed, 0f);
        float ramp = m.PitchAuthorityAt(m.Speed);
        Assert.True(ramp < 0.2f, $"the fixture must sit deep inside the authored ramp, and it read {ramp:0.000}");

        float flag = m.StallFlag;
        m.Step(new FlightInput(), Dt);
        float decoded = stats.StallMag * flag * stats.RecInertia.X * Dt
                        * Mathf.Exp(-Dt * stats.AngMomentumDamp);
        Assert.True(Mathf.Abs(Mathf.Abs(m.BodyRates.X) - decoded) < 1e-6f,
            $"the drop read {Mathf.Abs(m.BodyRates.X):0.000000} against a decoded {decoded:0.000000}");
        Assert.True(Mathf.Abs(decoded - (decoded * ramp)) > 1e-5f,
            "the control must separate: a drop scaled by the ramp is a different number");
    }

    /// <summary>The AI law's low-speed recovery arm keys on the BACKWARD axis's Y, so it fires with
    /// the nose UP and slow and commands the nose down there; in a dive it never arms, whatever the
    /// speed. It cannot be the softness in a dive, and a player-flown aeroplane never runs it at
    /// all.</summary>
    [Fact]
    public void TheRecoveryArmFiresNoseHighAndSlowAndNeverInADive()
    {
        var high = new FlightModel(Autogyro());
        high.Reset(Vector3.Zero, Pitched(40f), 20f * Mph, 0f);
        var low = new FlightModel(Autogyro());
        low.Reset(Vector3.Zero, Pitched(-40f), 20f * Mph, 0f);

        Assert.True(high.Attitude.Z.Y < AiControlLaw.RecoveryNoseY, "nose-high must arm the recovery");
        Assert.False(low.Attitude.Z.Y < AiControlLaw.RecoveryNoseY, "a dive must not arm the recovery");
        Assert.True(high.Speed < AiControlLaw.RecoverySpeed, "the fixture must be below the recovery speed");
        Assert.Equal(-1f, Steer(high).Pitch);
        Assert.NotEqual(-1f, Steer(low).Pitch);
    }

    /// <summary>The plant's own low-speed nose-down is PLAYER-ONLY: the drop and the weathervane both
    /// sit behind that guard, so an AI-path autogyro holds its nose-high attitude through a stall
    /// with a centred stick and every degree of nose-down it flies is the recovery arm's command.
    /// The player-path run of the same script is the control.</summary>
    [Fact]
    public void AnAiPathAutogyroHasNoPlantNoseDownToSoften()
    {
        var ai = new FlightModel(Autogyro(), aiForcePath: true);
        var player = new FlightModel(Autogyro());
        ai.Reset(Vector3.Zero, Pitched(30f), 70f * Mph, 0f);
        player.Reset(Vector3.Zero, Pitched(30f), 70f * Mph, 0f);
        for (float t = 0f; t < 3f; t += Dt)
        {
            ai.Step(new FlightInput(), Dt);
            player.Step(new FlightInput(), Dt);
        }

        Assert.Equal(0f, ai.BodyRates.X);
        Assert.True(NoseDeg(ai) > 29.9f, $"the AI-path nose must stay put, and it read {NoseDeg(ai):0.0}°");
        Assert.True(NoseDeg(player) < 20f,
            $"the player-path control must have dropped its nose, and it read {NoseDeg(player):0.0}°");
    }

    private static FlightInput Steer(FlightModel m) => AiControlLaw.Steer(
        m, m.Position + new Vector3(0f, 0f, -1000f), Vector3.Zero, AiLawParams.Cruise, 0f, Dt);

    private static Basis Pitched(float deg) => Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(deg));

    private static float NoseDeg(FlightModel m) =>
        Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));

    // One step from a trimmed, wings-level, stick-centred stall at nine tenths of the airframe's own
    // stall speed, which is the same depth on any airframe: the resulting pitch rate in °/s.
    private static float OneStalledStepRate(PlaneStats stats, out float flag)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 0.9f * m.StallSpeed, 0f);
        flag = m.StallFlag;
        m.Step(new FlightInput(), Dt);
        return Mathf.RadToDeg(m.BodyRates.X);
    }

    // The autogyro's authored dynamics block (extracted/zrdr/vehicle.zrd.json, `pautogyro`).
    private static PlaneStats Autogyro() => new()
    {
        VehWeight = 500f,
        RefArea = 800f,
        FdSpeed = 102f,
        DragFactor = 0.5f,
        PitchTorque = 3.4f,
        RollTorque = 6.2f,
        RudderTorque = 10f,
        ReturnRate = 3f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 0.3f, 0.5f),
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
    };

    // The Bloodhawk's, the airframe the envelope is measured on.
    private static PlaneStats Bhawk() => new()
    {
        VehWeight = 1900f,
        RefArea = 330f,
        FdSpeed = 135f,
        DragFactor = 0.37f,
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 3f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        TurnFadeIn = 10f * Mph,
        TurnFadeOut = 50f * Mph,
    };
}
