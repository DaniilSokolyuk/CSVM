using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The roll command reaches the roll axis and nothing else. In the original the stick's roll slot
/// is read twice in the live torque path, once for the roll torque and once as a zero test, and
/// the pitch axis takes attitude terms only (docs/org/flightModel.md, "Roll to pitch"). These pins
/// hold the attitude wings-level so the bank coupling reads the same state every step, which
/// leaves the roll stick as the only quantity that could move the pitch accumulator.
/// </summary>
public class RollToPitchCouplingTests
{
    private const float Dt = 1f / 60f;
    private const int Steps = 120;
    private const float Speed = 60f;

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.35f)]
    public void APureRollStickWritesNothingToThePitchAccumulator(float roll)
    {
        var m = FlyLevel(new FlightInput { Roll = roll, Throttle = 1f });

        Assert.Equal(0f, m.BodyRates.X);
        Assert.True(Mathf.Abs(m.BodyRates.Z) > 1e-3f,
            $"the roll itself must be live for the pin to mean anything: {m.BodyRates.Z:0.000000} rad/s");
    }

    [Fact]
    public void ThePitchAccumulatorIsTheSameWithAndWithoutARollCommand()
    {
        // Held wings-level and on the flight path, the pitch axis is quiet with any stick; a term
        // of either sign keyed on the roll command would separate these three runs.
        var left = FlyLevel(new FlightInput { Roll = 1f, Throttle = 1f });
        var right = FlyLevel(new FlightInput { Roll = -1f, Throttle = 1f });
        var centred = FlyLevel(new FlightInput { Throttle = 1f });

        Assert.Equal(centred.BodyRates.X, left.BodyRates.X);
        Assert.Equal(centred.BodyRates.X, right.BodyRates.X);
    }

    [Fact]
    public void ADesignDocumentSizedNoseOverWouldBeVisibleToThatPin()
    {
        // METHOD-9 control. The GDD calls the effect "small but noticeable", so this injects one at
        // a fiftieth of the pitch torque and reads it back: a pass above measures the plant.
        var m = new FlightModel(Stats());
        m.Reset(Vector3.Zero, Basis.Identity, Speed, 1f);
        for (int i = 0; i < Steps; i++)
        {
            m.Step(new FlightInput { Roll = 1f, Throttle = 1f }, Dt);
            m.BodyRates -= new Vector3(0.02f * Stats().PitchTorque * Dt, 0f, 0f);
            Level(m);
        }

        Assert.NotEqual(0f, m.BodyRates.X);
        Assert.True(m.BodyRates.X < -1e-4f,
            $"the injected nose-over must reach the accumulator: {m.BodyRates.X:0.000000} rad/s");
    }

    [Fact]
    public void TheElevatorsCarryARollTermThatTheTorquePathDoesNotSee()
    {
        // The original mixes a differential roll term into both elevator slots for the surface
        // animation alone. The two must not be confused, so this pins the deflection and the quiet
        // pitch axis in the same state.
        var input = new FlightInput { Roll = 1f, Throttle = 1f };
        var mix = default(ControlSurfaceMix);
        mix.Advance(1f, input, 1f, animate: true);

        Assert.True(Mathf.Abs(mix[SurfaceSlot.ElevatorLeft]) > 0.05f,
            $"a roll stick deflects the elevators: {mix[SurfaceSlot.ElevatorLeft]:0.0000} rad");
        Assert.Equal(-mix[SurfaceSlot.ElevatorLeft], mix[SurfaceSlot.ElevatorRight]);
        Assert.Equal(0f, FlyLevel(input).BodyRates.X);
    }

    // Wings-level, on the flight path and at a fixed speed every step, so the bank coupling, the
    // weathervane and the authority curves read one unchanging state and cannot supply a pitch
    // rate that a roll-keyed term could hide behind.
    private static FlightModel FlyLevel(FlightInput input)
    {
        var m = new FlightModel(Stats());
        m.Reset(Vector3.Zero, Basis.Identity, Speed, input.Throttle);
        for (int i = 0; i < Steps; i++)
        {
            m.Step(input, Dt);
            Level(m);
        }

        return m;
    }

    private static void Level(FlightModel m)
    {
        m.Attitude = Basis.Identity;
        m.VelocityDir = Vector3.Forward;
        m.Speed = Speed;
    }

    // The Bloodhawk's dynamics, so the reciprocal inertias are the real, unequal numbers.
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
