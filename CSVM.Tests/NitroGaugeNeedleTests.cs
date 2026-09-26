using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>The nitro dial's two needles (FUN_004568c0): the original's shared exponential at
/// 3/s (boost) and 1.5/s (charge) over a 216° sweep, so a step lands 95 % of the way in one
/// second at 3/s and in two at 1.5/s, and a linear sweep of any rate fails the shape.</summary>
public class NitroGaugeNeedleTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void TheBoostNeedleChasesFullSweepExponentiallyAtThreePerSecond()
    {
        var needle = new GaugeCluster.NitroNeedle(GaugeCluster.NitroBoostNeedleRate);
        float target = -GaugeCluster.NitroNeedleSweepDeg;
        for (int i = 0; i < 60; i++)
            needle.Advance(target, Dt);
        float expected = target * (1f - Mathf.Exp(-3f));
        Assert.InRange(needle.Angle, expected - 0.5f, expected + 0.5f);
        // A linear 216°/s sweep would have arrived exactly; the exponential is 5 % short.
        Assert.True(needle.Angle > target + 5f);
    }

    [Fact]
    public void TheChargeNeedleIsHalfAsQuick()
    {
        var fast = new GaugeCluster.NitroNeedle(GaugeCluster.NitroBoostNeedleRate);
        var slow = new GaugeCluster.NitroNeedle(GaugeCluster.NitroChargeNeedleRate);
        for (int i = 0; i < 60; i++)
        {
            fast.Advance(100f, Dt);
            slow.Advance(100f, Dt);
        }
        for (int i = 0; i < 60; i++)
            slow.Advance(100f, Dt);
        Assert.InRange(slow.Angle, fast.Angle - 0.5f, fast.Angle + 0.5f);
    }
}
