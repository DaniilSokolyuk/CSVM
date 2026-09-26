using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The numpad +/- zoom axis and the distance it trims: the target moves at 2/s on the two keys,
/// clamped [0, 1] with 0 the rest pose, and the shown trim chases it at 1.5/s through
/// <see cref="HeadLook.Approach"/>. <see cref="CameraController.ExternalRadius"/> then holds the
/// speed-driven radius inside the authored bounds and carries the axis outward from the near one.
/// Both are engine-free, so none of this needs a live camera.
/// </summary>
public class CameraControllerZoomTests
{
    private const float Tol = 1e-4f;
    private const float SmoothRate = 1.5f; // the decoded shown-trim catch-up rate

    // The Bloodhawk's own camparam block, where the base distance IS the near bound, and the
    // shipped default block, whose base distance sits below its own near bound.
    private const float HawkDist = 18.5f, HawkMin = 18.5f, HawkMax = 25f;
    private const float DefaultDist = 13f, DefaultMin = 15.7f, DefaultMax = 25f;

    private const float ZoomSpan = 10f; // the decoded metres a fully held axis adds

    [Fact]
    public void HoldingZoomOutMovesTheTargetTowardOneAtTwoPerSecond()
    {
        float target = CameraController.ZoomTarget(0f, zoomIn: false, zoomOut: true, dt: 0.1f);
        Assert.Equal(0.2f, target, Tol);
    }

    [Fact]
    public void HoldingZoomInMovesTheTargetTowardZeroAtTwoPerSecond()
    {
        float target = CameraController.ZoomTarget(0.5f, zoomIn: true, zoomOut: false, dt: 0.1f);
        Assert.Equal(0.3f, target, Tol);
    }

    /// <summary>The pose the view opens on is the near bound itself, for an airframe whose block
    /// puts its base distance there and for one taking a default that sits below it.</summary>
    [Fact]
    public void TheRestPoseSitsOnTheNearBound()
    {
        Assert.Equal(HawkMin, CameraController.ExternalRadius(HawkDist, HawkMin, HawkMax, 0f), Tol);
        Assert.Equal(
            DefaultMin, CameraController.ExternalRadius(DefaultDist, DefaultMin, DefaultMax, 0f), Tol);
    }

    /// <summary>Zoom in is the axis's dead direction at rest: the target cannot go below 0, so the
    /// radius the camera reads never drops under the near bound.</summary>
    [Fact]
    public void ZoomingInFromTheRestPoseIsANoOp()
    {
        float target = CameraController.ZoomTarget(0f, zoomIn: true, zoomOut: false, dt: 0.5f);
        Assert.Equal(0f, target, Tol);
        Assert.Equal(
            HawkMin, CameraController.ExternalRadius(HawkDist, HawkMin, HawkMax, target), Tol);
    }

    /// <summary>The only travel available is outward, and a fully held axis reaches the decoded
    /// flat span past wherever the speed-driven radius had settled.</summary>
    [Fact]
    public void ZoomingOutCarriesTheCameraTheDecodedSpanPastTheRestPose()
    {
        float target = CameraController.ZoomTarget(0f, zoomIn: false, zoomOut: true, dt: 1f);
        Assert.Equal(1f, target, Tol);
        Assert.Equal(
            HawkMin + ZoomSpan, CameraController.ExternalRadius(HawkDist, HawkMin, HawkMax, target), Tol);
    }

    /// <summary>The bounds hold the SPEED-driven radius, before the zoom: a fast enough aircraft
    /// stops at the far bound, and the axis still adds its span on top of that.</summary>
    [Fact]
    public void TheSpeedDrivenRadiusStopsAtTheFarBound()
    {
        Assert.Equal(HawkMax, CameraController.ExternalRadius(40f, HawkMin, HawkMax, 0f), Tol);
        Assert.Equal(HawkMax + ZoomSpan, CameraController.ExternalRadius(40f, HawkMin, HawkMax, 1f), Tol);
    }

    [Fact]
    public void NeitherKeyLeavesTheTargetWhereItWas()
    {
        float target = CameraController.ZoomTarget(0.4f, zoomIn: false, zoomOut: false, dt: 0.25f);
        Assert.Equal(0.4f, target, Tol);
    }

    [Fact]
    public void HoldingBothKeysCancelsLikeAPlainAxis()
    {
        float target = CameraController.ZoomTarget(0.4f, zoomIn: true, zoomOut: true, dt: 0.25f);
        Assert.Equal(0.4f, target, Tol);
    }

    [Fact]
    public void TheTargetClampsAtOneRatherThanOvershooting()
    {
        // A full second at 2/s would reach 2 unclamped; the decoded axis stops at 1.
        float target = CameraController.ZoomTarget(0f, zoomIn: false, zoomOut: true, dt: 1f);
        Assert.Equal(1f, target, Tol);
    }

    [Fact]
    public void TheTargetClampsAtZeroRatherThanGoingNegative()
    {
        float target = CameraController.ZoomTarget(1f, zoomIn: true, zoomOut: false, dt: 1f);
        Assert.Equal(0f, target, Tol);
    }

    [Fact]
    public void TheShownTrimChasesTheClampedTargetAtOnePointFivePerSecond()
    {
        // Isolate the smoothing law from the target axis's own ramp: a huge dt saturates
        // ZoomTarget at its clamp in one call, so the target is already settled at 1.
        float target = CameraController.ZoomTarget(0f, zoomIn: false, zoomOut: true, dt: 10f);
        Assert.Equal(1f, target, Tol);

        // HeadLook.Approach is exact for any dt (it is the closed-form exponential, not an
        // Euler step), so one second-long call reads the same as sixty 1/60 s frames would.
        // 1 - e^(-1.5) ≈ 0.7769.
        float shown = HeadLook.Approach(0f, target, SmoothRate, 1f);
        Assert.InRange(shown, 0.7755f, 0.7785f);
    }

    [Fact]
    public void TheShownTrimNeverOvershootsAFrozenTarget()
    {
        float shown = 0f;
        for (int i = 0; i < 5; i++)
        {
            float next = HeadLook.Approach(shown, 1f, SmoothRate, 0.1f);
            Assert.True(next > shown && next <= 1f); // strictly closer, never past the target
            shown = next;
        }
    }
}
