using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The pure first-person placement law:
/// <c>camera_world = plane_pos + plane_rotation × cockpit_camera_offset</c>, plus the fixed
/// −4.70° head-pitch offset. <see cref="CameraController.FirstPersonPose"/> is engine-free, so
/// these tests need no live <see cref="Camera3D"/>.
/// </summary>
public class CameraControllerFirstPersonTests
{
    // −4.70° = −0.08203 rad (bit pattern 0xbda7ff58, docs/org/cameraViews.md,
    // "Fixed head-pitch offset").
    private const float HeadPitchOffsetRad = -0.08203f;

    [Fact]
    public void LevelFlightPlacesTheCameraAtThePlanePositionPlusTheOffset()
    {
        var planePos = new Vector3(10f, 20f, 30f);
        var offset = new Vector3(0f, 0.75f, -0.2f); // player_pfighter's cockpit_camera marker
        var (position, _) = CameraController.FirstPersonPose(planePos, Basis.Identity, offset);
        Assert.True(position.IsEqualApprox(planePos + offset));
    }

    [Fact]
    public void AbsentMarkerDataFallsBackToThePlaneOrigin()
    {
        var planePos = new Vector3(5f, 5f, 5f);
        var (position, _) = CameraController.FirstPersonPose(planePos, Basis.Identity, Vector3.Zero);
        Assert.True(position.IsEqualApprox(planePos));
    }

    [Fact]
    public void ThePitchOffsetTiltsTheViewDownByTheFixedAngleInLevelFlight()
    {
        var (_, basis) = CameraController.FirstPersonPose(Vector3.Zero, Basis.Identity, Vector3.Zero);
        var forward = -basis.Z; // the camera's own forward axis
        var expected = new Vector3(0f, Mathf.Sin(HeadPitchOffsetRad), -Mathf.Cos(HeadPitchOffsetRad));
        Assert.True(forward.IsEqualApprox(expected));
        Assert.True(forward.Y < 0f); // −4.70° tilts down, revealing the plane's own nose
    }

    [Fact]
    public void ThePlanesAttitudeCarriesBothThePositionOffsetAndTheAim()
    {
        // A 90° yaw about the plane's own up axis: the nose (local −Z) now points along world −X.
        var attitude = new Basis(Vector3.Up, Mathf.Pi / 2f);
        var offset = new Vector3(0f, 0f, 1f); // a marker offset along the plane's local +Z
        var (position, basis) = CameraController.FirstPersonPose(Vector3.Zero, attitude, offset);
        // attitude rotates local +Z to world +X, so the offset lands there too, placement is not
        // a bare add of the local offset, it rides the plane's own rotation (the plan's formula).
        Assert.True(position.IsEqualApprox(new Vector3(1f, 0f, 0f)));
        var forward = -basis.Z;
        Assert.True(forward.X < -0.99f); // aim still follows the plane's own nose direction
    }

    [Fact]
    public void ZeroPlaneRotationAndZeroOffsetIsTheIdentityPlacement()
    {
        var (position, basis) = CameraController.FirstPersonPose(Vector3.Zero, Basis.Identity, Vector3.Zero);
        Assert.True(position.IsEqualApprox(Vector3.Zero));
        // Only the fixed pitch tilt moves the basis off identity, no yaw, no roll.
        Assert.True(basis.X.IsEqualApprox(Vector3.Right));
    }
}
