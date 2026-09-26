using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's weathervane: <c>return_rate</c> as a restoring torque that swings the nose onto
/// the velocity vector, not extra per-axis damping. Decode: docs/org/flightModel.md, "Weathervane
/// centring, resolved". Reads <see cref="FlightModel.WeathervaneTorque"/> directly, or the body
/// rates after one step from rest where the arithmetic is closed-form, so a sign flip or a missing
/// halving fails exactly rather than being absorbed by the integrator.
/// </summary>
public class WeathervaneTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void VanishesWhenTheNoseIsOnTheFlightPath()
    {
        var m = Model();
        m.Reset(Vector3.Zero, Basis.Identity, 120f, 1f);

        // Reset puts the velocity down the nose, so the misalignment is identically zero and the
        // torque must be exactly zero, not small. This is the property that keeps every
        // wings-level, zero-α scenario in the pinned envelope untouched.
        Assert.Equal(Vector3.Zero, m.WeathervaneTorque());
    }

    [Fact]
    public void PullsTheNoseBackDownWhenItLeadsThePathUpward()
    {
        // Nose pitched 20° above a level flight path: the torque must be nose-DOWN (negative pitch,
        // since BodyRates.X is + nose-up), and nothing else. A reversed sign here is the failure
        // that would make the model diverge instead of centre.
        var m = Model();
        m.Reset(Vector3.Zero, Pitched(20f), 120f, 1f);
        m.VelocityDir = Vector3.Forward;
        var torque = m.WeathervaneTorque();

        Assert.True(torque.X < 0f, $"nose above the path must pitch DOWN, got {torque.X:0.0000}");
        Assert.True(Mathf.Abs(torque.Y) < 1e-6f, $"a pure pitch offset must not yaw ({torque.Y:0.0000})");
    }

    [Fact]
    public void PullsTheNoseBackTowardThePathWhenItLeadsInYaw()
    {
        // Mirror case on the other axis: nose 20° left of the path yaws RIGHT (negative), and the
        // magnitude matches the pitch case exactly, the torque is isotropic about the nose.
        var m = Model();
        m.Reset(Vector3.Zero, Yawed(20f), 120f, 1f);
        m.VelocityDir = Vector3.Forward;
        var torque = m.WeathervaneTorque();

        Assert.True(torque.Y < 0f, $"nose left of the path must yaw RIGHT, got {torque.Y:0.0000}");
        Assert.True(Mathf.Abs(torque.X) < 1e-6f, $"a pure yaw offset must not pitch ({torque.X:0.0000})");

        var pitchCase = Model();
        pitchCase.Reset(Vector3.Zero, Pitched(20f), 120f, 1f);
        pitchCase.VelocityDir = Vector3.Forward;
        Assert.True(Mathf.IsEqualApprox(torque.Length(), pitchCase.WeathervaneTorque().Length(), 1e-6f),
            "the same misalignment must give the same torque on either axis");
    }

    [Fact]
    public void IsReturnRateTimesHalfTheMisalignmentAngle()
    {
        // return_rate × HALF the misalignment angle (docs/org/flightModel.md); reading the full
        // angle doubles the spring rate.
        var m = Model();
        m.Reset(Vector3.Zero, Pitched(20f), 120f, 1f);
        m.VelocityDir = Vector3.Forward;

        float expected = Stats().ReturnRate * 0.5f * Mathf.DegToRad(20f);
        Assert.True(Mathf.IsEqualApprox(m.WeathervaneTorque().Length(), expected, 1e-5f),
            $"|torque| {m.WeathervaneTorque().Length():0.000000} vs return_rate·(α/2) {expected:0.000000} "
            + $"(the full angle would give {expected * 2f:0.000000})");
    }

    [Fact]
    public void NeverReachesTheRollAxis()
    {
        // The torque's axis is nose × v̂, which is perpendicular to the nose at every attitude, so
        // its roll component is identically zero, a weathervane cannot bank an aeroplane. This is
        // why roll-360 is untouched by this item on a measurement as well as on a test.
        foreach (float bank in new[] { 0f, 30f, 90f, 150f, 180f })
        {
            var m = Model();
            m.Reset(Vector3.Zero, BankedLeft(bank) * Pitched(15f), 120f, 1f);
            m.VelocityDir = Vector3.Forward;
            Assert.True(Mathf.Abs(m.WeathervaneTorque().Z) < 1e-6f,
                $"bank {bank:0}°: roll component {m.WeathervaneTorque().Z:0.000000} must be zero");
        }
    }

    [Fact]
    public void EntersTheSameAccumulatorAsTheStickAndCarriesRecInertia()
    {
        // One step from rest: BodyRates = cmd·dt·exp(-dt·damp), torque scaled by RecInertia then
        // this tick's own decay, since the original damps the accumulated total.
        var stats = Stats();
        var m = Model();
        m.Reset(Vector3.Zero, Pitched(20f), 120f, 1f);
        m.VelocityDir = Vector3.Forward;
        var torque = m.WeathervaneTorque();
        m.Step(default, Dt);

        float decay = Mathf.Exp(-Dt * stats.AngMomentumDamp);
        float expected = torque.X * stats.RecInertia.X * Dt * decay;
        Assert.True(Mathf.IsEqualApprox(m.BodyRates.X, expected, 1e-6f),
            $"pitch rate {m.BodyRates.X:0.000000} vs torque·recInertia.x·dt·exp(-dt·damp) "
            + $"{expected:0.000000} (undamped would give {torque.X * stats.RecInertia.X * Dt:0.000000})");
    }

    [Fact]
    public void DampingNoLongerDependsOnWhetherAStickIsHeld()
    {
        // return_rate is out of the damping coefficient: a spinning aircraft must decay at
        // ang_momentum_damp alone, whether or not the stick is centred. The alternative this
        // discriminates against damps a released axis at damp + return_rate, a first-order lag.
        var stats = Stats();
        var m = Model();
        m.Reset(Vector3.Zero, Basis.Identity, 120f, 1f);
        m.BodyRates = new Vector3(0f, 0f, 1f);
        m.Step(default, Dt);

        // Roll is the axis the weathervane provably cannot reach, so this isolates the damping
        // term; the decay is exponential, not the linear (1 - damp·dt) form.
        float expected = Mathf.Exp(-stats.AngMomentumDamp * Dt);
        Assert.True(Mathf.IsEqualApprox(m.BodyRates.Z, expected, 1e-6f),
            $"roll decayed to {m.BodyRates.Z:0.000000}, expected exp(-ang_momentum_damp·dt) "
            + $"{expected:0.000000} (the linear form would give {1f - (stats.AngMomentumDamp * Dt):0.000000}, "
            + $"damp + return_rate folded in would give {1f - ((stats.AngMomentumDamp + stats.ReturnRate) * Dt):0.000000})");
    }

    // The Bloodhawk's real dynamics, the torque is scaled by `rec_moments_inertia`,
    // so the placeholder defaults would hide a wrong axis behind near-equal components.
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

    private static FlightModel Model() => new(Stats());

    // Nose up by `deg`, a rotation about the body starboard axis.
    private static Basis Pitched(float deg) =>
        Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(deg));

    // Nose left by `deg`.
    private static Basis Yawed(float deg) =>
        Basis.Identity.Rotated(Vector3.Up, Mathf.DegToRad(deg));

    private static Basis BankedLeft(float deg) =>
        Basis.Identity.Rotated(Vector3.Back, Mathf.DegToRad(deg));
}
