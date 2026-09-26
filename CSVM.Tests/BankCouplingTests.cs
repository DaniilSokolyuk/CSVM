using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's bank→yaw / bank→pitch coupling: two constants compiled into <c>crimson.exe</c>
/// (0.205 and 0.165, its `fall_off` / `bank_off` console variables) that turn bank straight into
/// angular rate. Everything here is asserted on <see cref="FlightModel.BodyRates"/> after ONE step
/// from rest with the stick centred, where the model's own arithmetic is a closed form
/// (<c>BodyRates = cmd · dt</c>), so a sign flip, a missing axis or a wrong constant fails
/// exactly, rather than being absorbed by the integrator a hundred frames later.
///
/// <para>The inverted case is the one a plausible-looking implementation gets wrong: the extra
/// contribution reuses the YAW constant on the PITCH axis, so it is 0.205 and not 0.165, and it
/// peaks wings-level inverted where the bank term is identically zero.</para>
/// </summary>
public class BankCouplingTests
{
    private const float Dt = 1f / 60f;
    private const float YawCoef = 0.205f;
    private const float PitchCoef = 0.165f;

    [Fact]
    public void WingsLevelUprightCouplesNothing()
    {
        // Both terms vanish by construction at zero bank, so cruise is untouched, the same
        // property the knife-edge sag has, and the reason this change cannot reach a level-flight
        // measurement.
        var rates = OneStepFrom(Basis.Identity);
        Assert.True(rates.Length() < 1e-7f, $"level, stick centred: body rates {rates} must be zero");
    }

    [Fact]
    public void BankSignsTheYawAndNotThePitch()
    {
        var left = OneStepFrom(BankedLeft(90f));
        var right = OneStepFrom(BankedLeft(-90f));

        // Yaw follows the bank: left bank yaws left (+), right bank yaws right (−), equal size.
        Assert.True(left.Y > 0f && right.Y < 0f,
            $"bank must yaw the way the wings point: left {left.Y:0.0000}, right {right.Y:0.0000}");
        Assert.True(Mathf.Abs(left.Y + right.Y) < 1e-6f, "the yaw coupling must be symmetric in bank");

        // Pitch does not: |bank| is unsigned, so both banks pull the nose UP by the same amount.
        Assert.True(left.X > 0f && Mathf.Abs(left.X - right.X) < 1e-6f,
            $"bank must pull up whichever way it points: left {left.X:0.0000}, right {right.X:0.0000}");

        // Nothing reaches roll, the original couples bank into two axes, not three.
        Assert.True(Mathf.Abs(left.Z) < 1e-7f, $"the coupling must not touch roll (got {left.Z:0.0000})");
    }

    [Fact]
    public void TheCoefficientsAreTheBinarysAtNinetyDegrees()
    {
        var stats = Bhawk();
        var rates = OneStepFrom(BankedLeft(90f));
        // One step from rest: the accumulated total (this tick's torque, nothing carried over) is
        // itself subject to the tick's own exponential decay, see AngularDampingTests for the
        // ordering this factor pins.
        float decay = Mathf.Exp(-Dt * stats.AngMomentumDamp);

        // At exactly 90° the wings are vertical: |starboard·up| = 1 and bodyUp·up = 0, so each axis
        // reads its own constant undiluted, scaled only by that axis' reciprocal inertia (and this
        // tick's decay).
        Assert.True(Mathf.IsEqualApprox(rates.Y, YawCoef * stats.RecInertia.Y * Dt * decay, 1e-6f),
            $"yaw {rates.Y:0.000000} vs 0.205·recInertia.y·dt·exp(-dt·damp) "
            + $"{YawCoef * stats.RecInertia.Y * Dt * decay:0.000000}");
        Assert.True(Mathf.IsEqualApprox(rates.X, PitchCoef * stats.RecInertia.X * Dt * decay, 1e-6f),
            $"pitch {rates.X:0.000000} vs 0.165·recInertia.x·dt·exp(-dt·damp) "
            + $"{PitchCoef * stats.RecInertia.X * Dt * decay:0.000000}");
    }

    [Fact]
    public void InvertedPullsOnTheYawConstantThroughThePitchAxis()
    {
        var stats = Bhawk();
        var rates = OneStepFrom(BankedLeft(180f));
        float decay = Mathf.Exp(-Dt * stats.AngMomentumDamp);

        // Wings-level inverted, the bank term is zero, but the inverted pull is the same 0.205
        // constant applied to the pitch axis, not a yaw contribution; see docs/org/flightModel.md.
        Assert.True(Mathf.Abs(rates.Y) < 1e-6f,
            $"inverted and wings level, nothing couples into yaw (got {rates.Y:0.000000})");
        Assert.True(Mathf.IsEqualApprox(rates.X, YawCoef * stats.RecInertia.X * Dt * decay, 1e-6f),
            $"inverted pitch {rates.X:0.000000} vs 0.205·recInertia.x·dt·exp(-dt·damp) "
            + $"{YawCoef * stats.RecInertia.X * Dt * decay:0.000000} (0.165 would give "
            + $"{PitchCoef * stats.RecInertia.X * Dt * decay:0.000000})");
    }

    [Fact]
    public void TheInvertedTermSwitchesOnExactlyAtTheHorizon()
    {
        // The gate is the sign of bodyUp·up, so it opens the instant the wings pass vertical and is
        // continuous across it: both sides of 90° carry the same bank term, and only past 90° does
        // the inverted term add anything at all.
        var upright = OneStepFrom(BankedLeft(89f));
        var past = OneStepFrom(BankedLeft(91f));
        var stats = Bhawk();
        float decay = Mathf.Exp(-Dt * stats.AngMomentumDamp);
        float bankTerm = PitchCoef * Mathf.Sin(Mathf.DegToRad(89f)) * stats.RecInertia.X * Dt * decay;

        Assert.True(Mathf.IsEqualApprox(upright.X, bankTerm, 1e-6f),
            $"just short of vertical the bank term is alone: {upright.X:0.000000} vs {bankTerm:0.000000}");
        Assert.True(past.X > upright.X,
            $"past vertical the inverted term adds: {past.X:0.000000} must exceed {upright.X:0.000000}");
        float invertedExtra = YawCoef * Mathf.Cos(Mathf.DegToRad(89f)) * stats.RecInertia.X * Dt * decay;
        Assert.True(Mathf.IsEqualApprox(past.X - bankTerm, invertedExtra, 1e-6f),
            $"the extra past vertical is 0.205·|bodyUp·up|: {past.X - bankTerm:0.000000} vs "
            + $"{invertedExtra:0.000000}");
    }

    // The Bloodhawk's real dynamics, the coupling is scaled by `rec_moments_inertia`,
    // so the placeholder defaults would hide a wrong axis behind near-equal components.
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
    };

    // Bank about the nose, positive = LEFT (right wing up), matching
    // FlightInput.Roll's sign.
    private static Basis BankedLeft(float deg) =>
        Basis.Identity.Rotated(Vector3.Back, Mathf.DegToRad(deg));

    private static Vector3 OneStepFrom(Basis attitude)
    {
        var m = new FlightModel(Bhawk());
        m.Reset(Vector3.Zero, attitude, 120f, 1f);
        m.Step(new FlightInput { Throttle = 1f }, Dt);
        return m.BodyRates;
    }
}
