using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The stall nose-drop is a TORQUE about an unnormalised nose × worldUp, not a rotation of the
/// attitude toward world-down: decoded in docs/org/flightModel.md's "The nose-drop's rate is
/// stall_mag". These assert the mechanism, the reciprocal inertia, the momentum damping and the
/// lift-versus-weight flag all being in the path, and never a figure measured off video.
/// </summary>
public class StallNoseDropTests
{
    private const float Dt = 1f / 60f;

    /// <summary>One step from a trimmed, wings-level, stick-centred stall is the stall torque and
    /// nothing else, the weathervane is zero with the path on the nose, and the bank couplings are
    /// zero wings-level. So the resulting body pitch rate must be the decoded product exactly:
    /// stall_mag × flag × rec_moments_inertia.x × dt, decayed by the tick's own exp(−dt·damp).
    /// A term applied to the attitude instead, or one that skipped the inertia or the damping,
    /// lands somewhere else entirely.</summary>
    [Fact]
    public void TheDropIsATorqueScaledByInertiaAndDampedLikeAnyOther()
    {
        var stats = Bhawk();
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 0.9f * m.StallSpeed, 0f);
        Assert.True(m.isStalled(), "the fixture must start stalled");

        float flag = m.StallFlag;
        float noseYBefore = (-m.Attitude.Z).Y;
        m.Step(new FlightInput(), Dt);

        float expected = stats.StallMag * flag * stats.RecInertia.X * Dt
                         * Mathf.Exp(-Dt * stats.AngMomentumDamp);
        Assert.True(Mathf.Abs(Mathf.Abs(m.BodyRates.X) - expected) < 1e-6f,
            $"body pitch rate {Mathf.Abs(m.BodyRates.X):0.000000} after one stalled step against a "
            + $"decoded {expected:0.000000} (stall_mag {stats.StallMag}, flag {flag:0.000}, "
            + $"recInertia.x {stats.RecInertia.X}, damp {stats.AngMomentumDamp})");
        Assert.True((-m.Attitude.Z).Y < noseYBefore, "the drop must push the nose DOWN");
    }

    /// <summary>The drop settles rather than chasing. The axis is unnormalised, so the torque
    /// carries cos(nose elevation) and fades as the nose leaves the horizontal; a stalled aircraft
    /// left alone therefore parks at an equilibrium instead of rotating on toward straight down,
    /// which is what the retired attitude-chase did.</summary>
    [Fact]
    public void TheNoseSettlesInsteadOfChasingWorldDown()
    {
        var m = new FlightModel(Bhawk());
        m.Reset(Vector3.Zero, Basis.Identity, 0.9f * m.StallSpeed, 0f);

        for (float t = 0f; t < 30f; t += Dt)
            m.Step(new FlightInput(), Dt);

        float noseDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));
        Assert.True(noseDeg > -75f,
            $"the nose reached {noseDeg:0.0}° after 30 s of stall, that is a chase toward "
            + "world-down, not the decoded torque finding its equilibrium");
        Assert.True(noseDeg < 0f, $"the nose must end up below the horizon, not at {noseDeg:0.0}°");
    }

    /// <summary>Full back stick cannot raise the nose in a stall, by CANCELLATION rather than by
    /// magnitude: the pull lies along the drop axis and opposes it, so the sign test removes it
    /// before the drop is added. ⚠ This is the decoded replacement for the remake's hand-rolled
    /// over-the-horizon cap; if it fails, the cancellation is gone, do not re-add a cap.</summary>
    [Fact]
    public void FullBackStickCannotRaiseTheNoseInAStall()
    {
        var m = new FlightModel(Bhawk());
        m.Reset(Vector3.Zero, Basis.Identity, 0.9f * m.StallSpeed, 0f);
        float noseYBefore = (-m.Attitude.Z).Y;

        for (float t = 0f; t < 0.5f; t += Dt)
            m.Step(new FlightInput { Pitch = 1f }, Dt);

        Assert.True(m.isStalled(), "the aircraft must still be stalled through the pull");
        Assert.True((-m.Attitude.Z).Y <= noseYBefore,
            "full back stick in a stall must not raise the nose: the pull is cancelled along the "
            + "drop axis before the drop is added");
    }

    // The Bloodhawk's own weight and area, which put its stall well clear of the warn threshold.
    private static PlaneStats Bhawk() =>
        new() { VehWeight = 1900f, RefArea = 330f, FdSpeed = 135f };
}
