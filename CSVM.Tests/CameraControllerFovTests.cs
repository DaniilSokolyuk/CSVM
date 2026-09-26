using System;
using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The horizontal→vertical FOV conversion (docs/org/cameraViews.md, "FOV constants and aspect
/// correction"): <c>vertical = atan(tan(H/2) / assumedAspect)</c>, doubled for the full angle
/// Godot's <see cref="Godot.Camera3D.Fov"/> expects. The result does not depend on the viewport,
/// so a wider one shows more world at the sides rather than less above and below.
/// <see cref="CameraController.HorizontalToVerticalFovDeg"/> is engine-free, so these tests need
/// no live <see cref="Godot.Camera3D"/> or <see cref="Godot.Viewport"/>.
/// </summary>
public class CameraControllerFovTests
{
    private const float Aspect4By3 = 4f / 3f;
    private const float Aspect16By9 = 16f / 9f;
    private const float Aspect32By9 = 32f / 9f;

    [Fact]
    public void NosesSixtyHorizontalPortsToFortySixPointEightVertical()
    {
        float vertical = CameraController.HorizontalToVerticalFovDeg(60f);
        Assert.InRange(vertical, 46.7f, 46.9f);
    }

    [Fact]
    public void CockpitsEightyHorizontalPortsToSixtyFourPointFourVertical()
    {
        float vertical = CameraController.HorizontalToVerticalFovDeg(80f);
        Assert.InRange(vertical, 64.3f, 64.5f);
    }

    [Fact]
    public void TheDerivedVerticalGivesBackTheStoredHorizontalAtTheAspectItWasStatedAt()
    {
        // The bases are the original's horizontal angles at the 4:3 it ran, so a 4:3 viewport
        // showing this vertical shows exactly those angles across, the check that the stored
        // number and the derived one describe one frustum rather than two.
        Assert.InRange(HorizontalAt(CameraController.HorizontalToVerticalFovDeg(80f), Aspect4By3), 79.9f, 80.1f);
        Assert.InRange(HorizontalAt(CameraController.HorizontalToVerticalFovDeg(60f), Aspect4By3), 59.9f, 60.1f);
    }

    [Fact]
    public void AWiderViewportKeepsTheVerticalAndWidensTheHorizontal()
    {
        // What an ultrawide screen and a stacked 2-player pane both need: the vertical is the
        // angle held, so the picture gains world at the sides instead of losing the canopy rails
        // and the panel off the top and bottom.
        float vertical = CameraController.HorizontalToVerticalFovDeg(80f);
        Assert.InRange(HorizontalAt(vertical, Aspect16By9), 96.3f, 96.5f);
        Assert.InRange(HorizontalAt(vertical, Aspect32By9), 131.7f, 131.9f);
    }

    [Fact]
    public void EveryViewportShapeTakesTheSameVertical()
    {
        // The 4:3 fullscreen, the 16:9 window, the 32:9 ultrawide and the stacked pane a 1280x720
        // window cuts are one FOV, so there is no aspect-conditional branch to get wrong.
        float cockpit = CameraController.HorizontalToVerticalFovDeg(80f);
        Assert.Equal(cockpit, CameraController.FirstPersonFovDeg(PilotViewMode.Cockpit), 3);
        Assert.Equal(CameraController.HorizontalToVerticalFovDeg(60f),
            CameraController.FirstPersonFovDeg(PilotViewMode.Nose), 3);
    }

    [Fact]
    public void EveryViewOutsideTheCockpitInteriorTakesTheOneDecodedBase()
    {
        // The chase, the nine fixed numpad poses, look-behind, the pad look-around, the crash and
        // death cuts and the flyby are all camera modes the original hands the 60° constant, so they
        // share one number with the Nose view rather than carrying an assumption of their own.
        Assert.InRange(CameraController.ExternalFovDeg, 46.7f, 46.9f);
        Assert.Equal(CameraController.FirstPersonFovDeg(PilotViewMode.Nose),
            CameraController.ExternalFovDeg, 3);
        Assert.NotEqual(CameraController.FirstPersonFovDeg(PilotViewMode.Cockpit),
            CameraController.ExternalFovDeg, 3);
    }

    // The horizontal angle a frustum of this vertical spans on a viewport of this aspect, in
    // degrees. The inverse of the conversion under test, so it is written out here rather than
    // taken from the class it is checking.
    private static float HorizontalAt(float verticalDeg, float aspect)
    {
        double halfV = verticalDeg * Math.PI / 360.0;
        return (float)(Math.Atan(Math.Tan(halfV) * aspect) * 360.0 / Math.PI);
    }
}
