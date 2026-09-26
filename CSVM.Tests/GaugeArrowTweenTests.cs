using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The weapon-gauge pointer sweeps at a single constant rate measured from original-game
/// footage, 168.7 °/sim-s, toward the selected belt slot, routed the shortest way round, and snaps
/// instead of sweeping in when it has no prior pose (NaN: gauge just appeared, or a respawn
/// cleared it via <c>GaugeCluster.Reset</c>). Every member here is a pure static with no engine
/// dependency.
/// </summary>
public class GaugeArrowTweenTests
{
    [Fact]
    public void TargetAngleStepsEvenlyAroundTheRing()
    {
        Assert.Equal(0f, GaugeCluster.TargetArrowAngle(4, 0));
        Assert.Equal(-90f, GaugeCluster.TargetArrowAngle(4, 1)); // 4 positions, 90° apart
        Assert.Equal(-45f, GaugeCluster.TargetArrowAngle(8, 1)); // 8 positions, 45° apart
        Assert.Equal(0f, GaugeCluster.TargetArrowAngle(0, 0)); // no positions targets 0°, never NaN/inf
    }

    [Fact]
    public void ANaNPoseSnapsToTargetInsteadOfSweepingIn()
    {
        Assert.Equal(-90f, GaugeCluster.TweenArrow(float.NaN, -90f, 0.5f));
    }

    /// <summary>One sim-step at the constant rate advances the arrow by exactly rate × dt, neither
    /// clamped early nor overshooting. This is a constant-rate tween with no easing; see
    /// <c>docs/formats/hud.md</c> for the ~97 ms ease this omits.</summary>
    [Fact]
    public void OneSimStepAdvancesByRateTimesDt()
    {
        float step = GaugeCluster.ArrowSweepDegPerSimS * (1f / 60f);
        float afterOneStep = GaugeCluster.TweenArrow(0f, 90f, 1f / 60f);
        Assert.Equal(step, afterOneStep, 3);
    }

    /// <summary>The decoded rate is 0.8 revolutions per second (288 °/s), so the gun gauge's
    /// 90°-per-position step takes 312 ms. ⚠ Supersedes a 533 ms span computed from the 168.7 °/s
    /// figure measured off footage.</summary>
    [Fact]
    public void ANinetyDegreeSweepSpansAboutThreeHundredTwelveMillisecondsSim()
    {
        float angle = 0f;
        int steps = 0;
        while (!Mathf.IsEqualApprox(angle, 90f) && steps < 200)
        {
            angle = GaugeCluster.TweenArrow(angle, 90f, 1f / 60f);
            steps++;
        }
        float simMs = steps * (1000f / 60f);
        Assert.Equal(90f, angle, 3);
        Assert.InRange(simMs, 300f, 330f);
    }

    /// <summary>Shortest-way wrap: 170° → -170° is only 20° apart going UP through the ±180° seam,
    /// 340° apart going down through 0°, the step must move toward 180°, not back toward 0°.</summary>
    [Fact]
    public void TheShortestWayWrapStepsUpThroughTheSeam()
    {
        float wrapped = GaugeCluster.TweenArrow(170f, -170f, 1f / 60f);
        Assert.True(wrapped > 170f, $"the shortest-way wrap steps up through the ±180° seam rather than back through 0°, got {wrapped:0.00}");
    }
}
