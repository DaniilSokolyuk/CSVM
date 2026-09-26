using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The chase camera's head swing, <see cref="CameraController.ChaseSwing"/>: elevation about the
/// plane's right axis, then azimuth about its up axis, turning the chase rig's plane-frame offset.
/// Pinned against the nine numpad positions measured off the original's own scripted re-take, so
/// the snap table and this rotation together have to reproduce the layout that footage shows
/// (docs/org/cameraViews.md). Pure, so none of it needs a live camera.
/// </summary>
public class CameraControllerChaseSwingTests
{
    private const float Tol = 1e-5f;

    // Two rigs behind and above the aeroplane, the shipped shape and a level one, so every claim
    // below rests on the rotation rather than on one authored base elevation.
    private static readonly Vector3[] Rigs =
    {
        new Vector3(0f, 4.5f, 16f).Normalized(),
        new Vector3(0f, 0f, 16f).Normalized(),
    };

    // The four corners, as the original's own stills read them: all four are BELOW the aircraft
    // (it has no above view at all), the bottom row of the pad is the forward hemisphere and the
    // top row is aft, and a pilot looking left is what carries the camera to starboard.
    [Theory]
    [InlineData(-1f, -1f, 1, -1)]   // Kp1 ahead + starboard, below
    [InlineData(1f, -1f, -1, -1)]   // Kp3 ahead + port, below
    [InlineData(-1f, 1f, 1, 1)]     // Kp7 astern + starboard, below
    [InlineData(1f, 1f, -1, 1)]     // Kp9 astern + port, below
    public void TheFourCornersLandInTheMeasuredQuadrants(float x, float y, int side, int foreAft)
    {
        foreach (var rig in Rigs)
        {
            var at = Where(x, y, rig);
            Assert.True(at.X * side > 0.1f, $"side, rig {rig}, at {at}");
            Assert.True(at.Z * foreAft > 0.1f, $"fore/aft, rig {rig}, at {at}");
            Assert.True(at.Y < -0.1f, $"below, rig {rig}, at {at}");
        }
    }

    // The four cardinals: the flanks stay at the rig's own elevation and lose the fore/aft term
    // entirely, Kp2 is the nose-on view rather than the belly, and Kp8 is the belly plan view.
    [Fact]
    public void TheFourCardinalsLandOnTheMeasuredAxis()
    {
        foreach (var rig in Rigs)
        {
            var port = Where(1f, 0f, rig);
            var starboard = Where(-1f, 0f, rig);
            Assert.True(starboard.X > 0.9f && Mathf.Abs(starboard.Z) < Tol, $"Kp4 at {starboard}");
            Assert.True(port.X < -0.9f && Mathf.Abs(port.Z) < Tol, $"Kp6 at {port}");
            // A swing about the up axis cannot change the height, so both flanks are as level as
            // the rig they came from, which is what the footage's level side views show.
            Assert.Equal(rig.Y, starboard.Y, Tol);
            Assert.Equal(rig.Y, port.Y, Tol);

            var ahead = Where(0f, -1f, rig);
            Assert.True(ahead.Z < -0.9f && Mathf.Abs(ahead.X) < Tol, $"Kp2 at {ahead}");
            Assert.Equal(rig.Y, ahead.Y, Tol);

            var below = Where(0f, 1f, rig);
            Assert.True(below.Y < -0.9f, $"Kp8 at {below}");
            Assert.True(Mathf.Abs(below.Y) > Mathf.Abs(below.Z), $"Kp8 plan form, at {below}");
        }
    }

    // The goldens' guarantee: a head at rest returns the identity exactly, not merely nearly, so a
    // chase shot taken with no look input places the camera on the same bits it always did.
    [Fact]
    public void ASettledHeadIsTheExactIdentity()
    {
        var swing = CameraController.ChaseSwing(0f, 0f);
        Assert.Equal(Basis.Identity, swing);
        foreach (var rig in Rigs)
        {
            var turned = swing * rig;
            Assert.Equal(rig.X, turned.X);
            Assert.Equal(rig.Y, turned.Y);
            Assert.Equal(rig.Z, turned.Z);
        }
    }

    // The two rotations do not commute, so the order is part of the law: elevation is taken in the
    // plane's frame and the azimuth then carries the tilted offset around the aircraft.
    [Fact]
    public void ElevationIsTakenBeforeAzimuth()
    {
        var law = CameraController.ChaseSwing(0.4f, 0.7f);
        var reversed = new Basis(Vector3.Right, 0.4f) * new Basis(Vector3.Up, 0.7f);
        Assert.False(law.IsEqualApprox(reversed));
        var expected = new Basis(Vector3.Up, 0.7f) * new Basis(Vector3.Right, 0.4f);
        Assert.True(law.IsEqualApprox(expected));
    }

    // Where the snap cluster's composed direction puts the camera: the head's own target angles,
    // then the swing, applied to the rig. The whole chain the chase placement runs.
    private static Vector3 Where(float x, float y, Vector3 rig)
    {
        var snap = HeadLook.SnapTargets(x, y);
        Assert.NotNull(snap);
        return CameraController.ChaseSwing(snap!.Value.Elevation, snap.Value.Azimuth) * rig;
    }
}
