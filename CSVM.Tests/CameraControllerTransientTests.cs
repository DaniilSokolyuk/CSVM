using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The chase camera's throttle transient, <c>dist_vary·(V − V̄)</c> with <c>V̄</c> a lagged copy
/// of speed easing at <c>dist_catch_up</c>. Both fields are authored per airframe, so what these
/// tests pin is the law rather than a tuned number: under steady acceleration the excess settles
/// at <c>dist_vary / dist_catch_up</c> metres per m/s², and once the acceleration stops it relaxes
/// at <c>dist_catch_up</c> per second. <see cref="CameraController.DistTransient"/> is pure, so
/// none of this needs a live camera.
/// </summary>
public class CameraControllerTransientTests
{
    // Fine enough that the discrete settle sits within 0.3% of the continuous one: the fixed
    // point is a·dt/(1 − e^(−rate·dt)), which exceeds a/rate by about rate·dt/2.
    private const float Dt = 1f / 240f;

    private const float Accel = 4f;   // m/s², a brisk throttle slam's along-path figure
    private const float SettleSeconds = 20f;

    /// <summary>The shipped default block: <c>dist_vary</c> 0.1 and <c>dist_catch_up</c> 1.0, the
    /// pair every airframe takes (no plane's own block overrides either).</summary>
    [Fact]
    public void TheShippedFieldsAreTheOnesTheLawIsQuotedAt()
    {
        var cam = new CamParams();
        Assert.Equal(0.1f, cam.DistVary, 1e-6f);
        Assert.Equal(1f, cam.DistCatchUp, 1e-6f);
    }

    /// <summary>Steady acceleration on the shipped fields settles at 0.1 m of excess distance per
    /// m/s², which is the ratio of the two fields and what the original's staircase clips measure
    /// as 0.105.</summary>
    [Fact]
    public void SteadyAccelerationSettlesAtATenthOfAMetrePerAccel()
    {
        var cam = new CamParams();
        float excess = SettleUnderAcceleration(cam.DistVary, cam.DistCatchUp, Accel, out _);
        Assert.Equal(0.1f * Accel, excess, 0.3f * 0.01f * Accel);
    }

    /// <summary>The settled excess is the RATIO of the two fields, not the gain alone: halving the
    /// relaxation rate doubles the distance the camera hangs back at the same acceleration.</summary>
    [Fact]
    public void HalvingTheCatchUpRateDoublesTheSettledExcess()
    {
        float fast = SettleUnderAcceleration(0.1f, 1f, Accel, out _);
        float slow = SettleUnderAcceleration(0.1f, 0.5f, Accel, out _);
        Assert.Equal(2f * fast, slow, 0.01f * fast);
    }

    /// <summary>Cut the acceleration and the excess decays at <c>dist_catch_up</c> per second: one
    /// second on the shipped 1.0 leaves 1/e of it, and the authored rate is a REAL-second rate,
    /// which is the clock this method is stepped on.</summary>
    [Fact]
    public void TheExcessRelaxesAtTheAuthoredRate()
    {
        var cam = new CamParams();
        float settled = SettleUnderAcceleration(cam.DistVary, cam.DistCatchUp, Accel,
            out float state);
        float speed = Accel * SettleSeconds;
        float excess = settled;
        for (int i = 0; i < (int)(1f / Dt); i++)
        {
            excess = CameraController.DistTransient(state, speed, cam.DistVary, cam.DistCatchUp,
                Dt, out state);
        }
        Assert.Equal(settled * 0.36788f, excess, 0.01f * settled);
    }

    /// <summary>A cruise at a held speed carries no transient at all, so the radius a settled shot
    /// frames is the plain speed law.</summary>
    [Fact]
    public void AHeldSpeedCarriesNoTransient()
    {
        var cam = new CamParams();
        float state = 60f; // enter the cruise still lagging 20 m/s behind, as a slam leaves it
        float excess = 0f;
        for (int i = 0; i < (int)(SettleSeconds / Dt); i++)
        {
            excess = CameraController.DistTransient(state, 80f, cam.DistVary, cam.DistCatchUp, Dt,
                out state);
        }
        // A tenth of a millimetre: float32 resolution at 80 m/s stops the lag a few ULPs short of
        // the real speed, which is what the residue is.
        Assert.Equal(0f, excess, 1e-3f);
    }

    /// <summary>A deceleration pulls the camera IN: the lagged copy sits above the real speed, so
    /// the term is negative, which is the cut-throttle half of what the clips show.</summary>
    [Fact]
    public void DecelerationGivesANegativeExcess()
    {
        var cam = new CamParams();
        float excess = SettleUnderAcceleration(cam.DistVary, cam.DistCatchUp, -Accel, out _);
        Assert.Equal(-0.1f * Accel, excess, 0.3f * 0.01f * Accel);
    }

    // Run the lag to its fixed point under a constant along-path acceleration, handing back the
    // last step's excess and the lagged-speed state it left, so a relaxation can continue from it.
    private static float SettleUnderAcceleration(float distVary, float distCatchUp, float accel,
        out float laggedSpeed)
    {
        laggedSpeed = 0f;
        float excess = 0f;
        int steps = (int)(SettleSeconds / Dt);
        for (int i = 1; i <= steps; i++)
        {
            excess = CameraController.DistTransient(laggedSpeed, accel * i * Dt, distVary,
                distCatchUp, Dt, out laggedSpeed);
        }
        return excess;
    }
}
