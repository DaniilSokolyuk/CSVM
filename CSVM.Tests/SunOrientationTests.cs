using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The sun's bearing is the flown zone's authored <c>SUNLIGHT_ORIENTATION</c>. Full decode:
/// <c>docs/org/weather.md</c>. Tests the parse and the gamez-euler-to-Godot-Rotation mapping
/// apart, since they fail apart, pinning the mapping against the original's own helper.
/// Deliberately asserts no opinion about the bearing "looking right", the authored value is
/// adopted with no TUNE, so that would be a tuned expectation wearing a test's clothes.
/// </summary>
public class SunOrientationTests
{
    [Theory]
    // The three anchors that pin all three axes independently: identity, pitch alone, yaw alone.
    [InlineData(0f, 0f)]
    [InlineData(-90f, 0f)]     // SHADOW_ANGLES' install-wide value, straight down
    [InlineData(0f, 90f)]
    // ...and every distinct bearing the install actually authors, so a chapter cannot drift.
    [InlineData(-25f, 90f)]    // C1
    [InlineData(-65f, 90f)]    // C1B/C1C/C2/C2B
    [InlineData(-25f, 135f)]   // C3
    [InlineData(-45f, 135f)]   // C4
    [InlineData(-25f, -135f)]  // C5
    public void GodotEulerReproducesTheOriginalsSunDirectionWithNoConversion(float pitchDeg, float yawDeg)
    {
        float p = Mathf.DegToRad(pitchDeg);
        float y = Mathf.DegToRad(yawDeg);

        // What WeatherRig.ApplyZone assigns, and what Godot then shines along: a DirectionalLight3D
        // emits down its own local -Z, and Node3D's default euler order is YXZ, the same order
        // GameZ.ParseTransform already reads every gamez node rotation in.
        var basis = Basis.FromEuler(new Vector3(p, y, 0f), EulerOrder.Yxz);
        var godot = -basis.Z;

        var original = OriginalDirection(p, y);
        Assert.Equal(original.X, godot.X, 5);
        Assert.Equal(original.Y, godot.Y, 5);
        Assert.Equal(original.Z, godot.Z, 5);
    }

    [Fact]
    public void PitchMinus90PointsStraightDown()
    {
        // Not redundant with the theory above, which only proves we AGREE with the binary, if both
        // sides shared a sign error it would still pass. This one names the physical answer: the
        // engine's default sun (seeded at pitch -pi/2) shines straight down, -Y in Godot.
        var dir = -Basis.FromEuler(new Vector3(Mathf.DegToRad(-90f), 0f, 0f), EulerOrder.Yxz).Z;
        Assert.Equal(0f, dir.X, 5);
        Assert.Equal(-1f, dir.Y, 5);
        Assert.Equal(0f, dir.Z, 5);
    }

    [ExtractedDataFact]
    public void EachChapterParsesItsOwnAuthoredBearing()
    {
        // The install's census, as a pin. Two things it catches that nothing else does: degrees
        // arriving unconverted (every value would be ~57x too large), and the chapters collapsing
        // to one bearing, which would otherwise look exactly like success.
        AssertBearing("C1", "IA1", -25f, 90f);
        AssertBearing("C1B", "IA1", -65f, 90f);
        AssertBearing("C2", "IA1", -65f, 90f);
        AssertBearing("C3", "IA1", -25f, 135f);
        AssertBearing("C4", "IA1", -45f, 135f);
        AssertBearing("C5", "IA1", -25f, -135f);
    }

    [ExtractedDataFact]
    public void AZoneChangeCarriesTheSunWithIt()
    {
        // The record is what WeatherRig.ApplyZone reads, so "the sun rides the zone" is a property
        // of the LOOKUP, not of the rig: whatever zone the state trigger names, its bearing comes
        // out of the same record as its fog. C1/IA1 authors both zones, so both states resolve.
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(weather);
        var below = weather!.Zone(weather.ZoneForState(1));
        var above = weather.Zone(weather.ZoneForState(2));

        // C1's two zones happen to author the SAME bearing (as does every chapter but C2), so this
        // asserts the pair is READ, not that it differs, a state change
        // must not silently return a default for one of them.
        Assert.Equal(Mathf.DegToRad(-25f), below.SunOrientation.X, 4);
        Assert.Equal(Mathf.DegToRad(-25f), above.SunOrientation.X, 4);
        Assert.Equal(Mathf.DegToRad(90f), below.SunOrientation.Y, 4);
        Assert.Equal(Mathf.DegToRad(90f), above.SunOrientation.Y, 4);
    }

    [Fact]
    public void AMissionWithNoWeatherKeepsTheLaunchersDefaultBearing()
    {
        // A missing zone lands on the launcher's hand-picked (-45, 150), not the binary's
        // straight-down; with no mission there is no authored answer.
        var weather = WeatherState.Load(TestData.Fixture("zrdr"));
        Assert.NotNull(weather);
        var absent = weather!.Zone("zone9");
        Assert.Equal(Mathf.DegToRad(-45f), absent.SunOrientation.X, 4);
        Assert.Equal(Mathf.DegToRad(150f), absent.SunOrientation.Y, 4);
    }

    // The original's euler→direction helper, transcribed: it converts SHADOW_ANGLES
    // the same way the light pipeline converts a node rotation, so it is the binary's own
    // statement of what a (pitch, yaw) pair MEANS as a direction.
    //   out.y = sin(pitch);  out.x = -cos(pitch)*sin(yaw);  out.z = -cos(pitch)*cos(yaw)
    private static Vector3 OriginalDirection(float pitchRad, float yawRad)
        => new(
            -Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
            Mathf.Sin(pitchRad),
            -Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));

    private static void AssertBearing(string chapter, string mission, float pitchDeg, float yawDeg)
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission));
        Assert.NotNull(weather);
        var zone = weather!.Zone(weather.ZoneForState(1));
        Assert.Equal(Mathf.DegToRad(pitchDeg), zone.SunOrientation.X, 4);
        Assert.Equal(Mathf.DegToRad(yawDeg), zone.SunOrientation.Y, 4);
        Assert.Equal(0f, zone.SunOrientation.Z, 4);
    }
}
