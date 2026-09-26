using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's angular accumulator carries no engine torque: every write is a product of
/// state-derived vectors, and the throttle reaches none of them (docs/org/flightModel.md,
/// "Engine torque"). The rotational plant is therefore mirror-symmetric and throttle-blind, and
/// these pins fail on any one-sided term or any throttle-scaled term added to it.
/// </summary>
public class EngineTorqueAbsenceTests
{
    private const float Dt = 1f / 60f;
    private const int Steps = 60;
    private const float Tol = 1e-5f;

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void FullRollIsTheSameRateInBothDirections(float throttle)
    {
        var left = Fly(new FlightInput { Roll = 1f, Throttle = throttle });
        var right = Fly(new FlightInput { Roll = -1f, Throttle = throttle });

        Assert.True(Mathf.IsEqualApprox(left.BodyRates.Z, -right.BodyRates.Z, Tol),
            $"roll left {left.BodyRates.Z:0.000000} vs roll right {right.BodyRates.Z:0.000000} rad/s at throttle {throttle}");
        Assert.True(Mathf.IsEqualApprox(left.BodyRates.Y, -right.BodyRates.Y, Tol),
            $"the bank coupling's yaw must mirror too: {left.BodyRates.Y:0.000000} vs {right.BodyRates.Y:0.000000}");
        Assert.True(Mathf.IsEqualApprox(left.BodyRates.X, right.BodyRates.X, Tol),
            $"the bank coupling's nose-up pull is even in bank: {left.BodyRates.X:0.000000} vs {right.BodyRates.X:0.000000}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void RudderTurnIsTheSameRateInBothDirections(float throttle)
    {
        var left = Fly(new FlightInput { Yaw = 1f, Throttle = throttle });
        var right = Fly(new FlightInput { Yaw = -1f, Throttle = throttle });

        Assert.True(Mathf.IsEqualApprox(left.BodyRates.Y, -right.BodyRates.Y, Tol),
            $"yaw left {left.BodyRates.Y:0.000000} vs yaw right {right.BodyRates.Y:0.000000} rad/s at throttle {throttle}");
        Assert.True(Mathf.IsEqualApprox(left.BodyRates.Z, -right.BodyRates.Z, Tol),
            $"any roll induced by the turn must mirror: {left.BodyRates.Z:0.000000} vs {right.BodyRates.Z:0.000000}");
    }

    [Fact]
    public void StraightAndLevelStaysStillAtEveryThrottle()
    {
        // The GDD's "no effect in straight-and-level flight" is not a special case of the shipped
        // plant; nothing turns the aircraft without a stick, a bank or a misaligned path.
        foreach (float throttle in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var m = Fly(new FlightInput { Throttle = throttle });
            Assert.True(m.BodyRates.Length() < Tol,
                $"throttle {throttle}: body rates {m.BodyRates} with a centred stick");
        }
    }

    [Fact]
    public void RollAndRudderRatesDoNotReadTheThrottle()
    {
        // The lever scales thrust alone. Speed and the flight path are pinned per step, because
        // thrust moves the velocity vector and the weathervane and the limiter read the nose-to-path
        // angle; with both held, the only remaining route from the lever to a rate is a torque.
        var idle = Fly(new FlightInput { Roll = 1f, Yaw = 1f, Throttle = 0f }, holdSpeed: true);
        var full = Fly(new FlightInput { Roll = 1f, Yaw = 1f, Throttle = 1f }, holdSpeed: true);
        Assert.True(idle.BodyRates.IsEqualApprox(full.BodyRates),
            $"rates at idle {idle.BodyRates} vs full throttle {full.BodyRates}");
    }

    [Fact]
    public void TheMirrorPinSeesAOneSidedTermOfTheSizeTheBacklogQuoted()
    {
        // METHOD-9 control: the disputed footage pair differed by 28 % in roll rate. A one-sided
        // assist a hundredth of that size fails the roll pin, so a pass is a measurement.
        var left = Fly(new FlightInput { Roll = 1f, Throttle = 1f });
        var right = Fly(new FlightInput { Roll = -1f, Throttle = 1f });
        float assisted = left.BodyRates.Z * 1.0028f;
        Assert.False(Mathf.IsEqualApprox(assisted, -right.BodyRates.Z, Tol),
            "a 0.28 % one-sided assist must be visible to the roll pin");
    }

    private static FlightModel Fly(FlightInput input, bool holdSpeed = false)
    {
        var m = new FlightModel(Stats());
        m.Reset(Vector3.Zero, Basis.Identity, 60f, input.Throttle);
        for (int i = 0; i < Steps; i++)
        {
            m.Step(input, Dt);
            if (holdSpeed)
            {
                m.Speed = 60f;
                m.VelocityDir = m.Attitude * Vector3.Forward;
            }
        }

        return m;
    }

    // The Bloodhawk's dynamics, so the bank coupling and the reciprocal inertias are the real,
    // unequal numbers rather than placeholders that would make a wrong axis look symmetric.
    private static PlaneStats Stats() => new()
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
}
