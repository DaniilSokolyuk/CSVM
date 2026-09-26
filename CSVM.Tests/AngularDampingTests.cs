using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's angular damping: an exponential decay of <c>dt · ang_momentum_damp</c>, applied
/// to the whole body rate after this tick's torque is accumulated onto it, so the decay carries
/// this tick's own torque and not just the carried-over rate. Decode: docs/org/flightModel.md.
/// The two forms (exponential vs. linear) agree to first order in dt, so a small timestep cannot
/// discriminate them; <see cref="ADeliberatelyLargeTimestepStaysBoundedWhereExplicitEulerWouldFlipSignAndGrow"/>
/// is the case that can.
/// </summary>
public class AngularDampingTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void DecaysAPreExistingRateByExpOfDtTimesDamp()
    {
        // Stick centred, wings level, velocity down the nose: cmd is identically zero on every
        // axis, so this is a pure decay of whatever rate was already there.
        var stats = Stats();
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 120f, 1f);
        m.BodyRates = new Vector3(0f, 0f, 1f);
        m.Step(default, Dt);

        float expected = Mathf.Exp(-Dt * stats.AngMomentumDamp);
        Assert.True(Mathf.IsEqualApprox(m.BodyRates.Z, expected, 1e-6f),
            $"roll decayed to {m.BodyRates.Z:0.000000}, expected exp(-dt*damp) {expected:0.000000} "
            + $"(the linear form would give {1f - stats.AngMomentumDamp * Dt:0.000000})");
    }

    [Fact]
    public void ThisTicksOwnTorqueIsDampedTooNotJustTheCarriedOverRate()
    {
        // Full roll stick from rest. The linear form gives BodyRates = cmd·dt exactly here, this
        // tick's torque completely undamped; the original damps the accumulated total, so the
        // right answer is cmd·dt·exp(-dt·damp), strictly smaller.
        var stats = Stats();
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 120f, 1f);
        m.Step(new FlightInput { Roll = 1f }, Dt);

        // No roll calibration factor: the binary builds the roll term from the authored torque and
        // reciprocal inertia alone (FlightModel.RollTune).
        float cmdZ = stats.RollTorque * stats.RecInertia.Z;
        float undamped = cmdZ * Dt;
        float expected = undamped * Mathf.Exp(-Dt * stats.AngMomentumDamp);
        Assert.True(Mathf.IsEqualApprox(m.BodyRates.Z, expected, 1e-6f),
            $"roll rate {m.BodyRates.Z:0.000000} vs cmd·dt·exp(-dt·damp) {expected:0.000000} "
            + $"(undamped cmd·dt alone, the decay-then-add ordering bug, would give {undamped:0.000000})");
    }

    [Fact]
    public void ADeliberatelyLargeTimestepStaysBoundedWhereExplicitEulerWouldFlipSignAndGrow()
    {
        // dt·damp = 5·1 = 5, past the linear form's stability edge at dt·damp = 2. Roll, stick
        // centred, wings level, on-path: cmd is zero, so this is pure decay of a pre-existing rate,
        // the case that actually separates the two forms.
        var stats = Stats();
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 120f, 1f);
        m.BodyRates = new Vector3(0f, 0f, 1f);
        const float largeDt = 1f;
        m.Step(default, largeDt);

        float linearFactor = 1f - stats.AngMomentumDamp * largeDt; // -4
        Assert.True(linearFactor < -1f, "the case must actually sit past the linear form's stability edge");

        float expDecay = Mathf.Exp(-stats.AngMomentumDamp * largeDt);
        Assert.True(Mathf.IsEqualApprox(m.BodyRates.Z, expDecay, 1e-6f),
            $"roll rate {m.BodyRates.Z:0.000000} vs exp(-damp·dt) {expDecay:0.000000}");
        // The point: bounded, positive, strictly decayed at any dt, never the sign flip
        // and 4x growth the linear form would have produced here (linearFactor · 1 = -4).
        Assert.True(m.BodyRates.Z is > 0f and < 1f,
            $"roll rate {m.BodyRates.Z:0.000000} must stay in (0, 1), positive and decayed, unlike "
            + $"the linear form's {linearFactor:0.000000}");
    }

    // The Bloodhawk's real dynamics, see WeathervaneTests/BankCouplingTests for why the
    // placeholder `PlaneStats()` defaults would hide a wrong axis behind near-equal
    // components.
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
