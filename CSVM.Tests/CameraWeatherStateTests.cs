using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="WeatherState.CameraWeatherState"/>: the binary's per-frame camera weather state
/// (1/2/3). Ships dark, nothing consumes it yet, so these tests are the only thing pinning the
/// state machine until a consumer is wired in.
///
/// <para>The fixture's <c>CLOUD_COVER</c> (<c>fixtures/zrdr/weather.json</c>) is invented, not
/// copied from an extraction (per <c>fixtures/README.md</c>): TOP 2000/BOTTOM 1000/THICKNESS 200
/// ⇒ band centre 1500, opaque-core bottom (<see cref="WeatherState.CloudCoreBottom"/>) 1400, the
/// state-2 threshold, which is NOT the band centre and NOT <c>BOTTOM</c>.</para>
/// </summary>
public class CameraWeatherStateTests
{
    private const float CoreBottom = 1400f; // 1500 (centre) - 200/2 (THICKNESS/2)

    private static readonly IReadOnlyList<FogVolumeBox> NoVolumes = System.Array.Empty<FogVolumeBox>();

    [Fact]
    public void JustBelowTheCoreBottomIsState1()
    {
        var weather = Load();
        Assert.Equal(1, weather.CameraWeatherState(At(CoreBottom - 0.5f), fogZoneArmed: false, NoVolumes));
    }

    [Fact]
    public void AtTheCoreBottomIsState2()
    {
        var weather = Load();
        Assert.Equal(2, weather.CameraWeatherState(At(CoreBottom), fogZoneArmed: false, NoVolumes));
    }

    [Fact]
    public void JustAboveTheCoreBottomIsState2()
    {
        var weather = Load();
        Assert.Equal(2, weather.CameraWeatherState(At(CoreBottom + 0.5f), fogZoneArmed: false, NoVolumes));
    }

    [Fact]
    public void ThresholdIsTheCoreBottomNotTheBandCentreOrTheVisualFloor()
    {
        var weather = Load();
        Assert.Equal(1500f, weather.CloudBandCentre, 3);
        Assert.Equal(1000f, weather.CloudBottom, 3);
        Assert.Equal(CoreBottom, weather.CloudCoreBottom, 3);
        // 1499/1001 straddle CloudBandCentre/CloudBottom but not CloudCoreBottom, so a threshold
        // using the wrong field would answer differently here.
        Assert.Equal(2, weather.CameraWeatherState(At(1499f), fogZoneArmed: false, NoVolumes));
        Assert.Equal(1, weather.CameraWeatherState(At(1001f), fogZoneArmed: false, NoVolumes));
    }

    [Fact]
    public void AMissionWithNoCloudCoverIsAlwaysState1()
    {
        // A mission whose weather.json carries no CLOUD_COVER block at all (HasCloudBand false)
        // must never reach state 2, at any altitude, the binary's gate sits inside the
        // CLOUD_COVER-exists check.
        var weather = WeatherState.Load(TestData.Fixture("weather-no-cloud"));
        Assert.NotNull(weather);
        Assert.False(weather!.HasCloudBand);
        Assert.Equal(1, weather.CameraWeatherState(At(-1000f), fogZoneArmed: false, NoVolumes));
        Assert.Equal(1, weather.CameraWeatherState(At(0f), fogZoneArmed: false, NoVolumes));
        Assert.Equal(1, weather.CameraWeatherState(At(1_000_000f), fogZoneArmed: false, NoVolumes));
    }

    [Fact]
    public void FogZoneArmedAndInsideAVolumeIsState3()
    {
        // C5-style: fog_zone armed (FogVolumeSpec.FogZoneArmed), camera inside one of the
        // chapter's fvol volumes. Uses the no-cloud-band fixture so the result isolates the
        // state-3 test from the altitude threshold above.
        var weather = WeatherState.Load(TestData.Fixture("weather-no-cloud"));
        Assert.NotNull(weather);
        var volumes = new[] { BoxVolume("fvol1", new Vector3(-10, -10, -10), new Vector3(10, 10, 10)) };

        Assert.Equal(3, weather!.CameraWeatherState(Vector3.Zero, fogZoneArmed: true, volumes));
    }

    [Fact]
    public void FogZoneArmedButOutsideEveryVolumeIsNotState3()
    {
        var weather = WeatherState.Load(TestData.Fixture("weather-no-cloud"));
        Assert.NotNull(weather);
        var volumes = new[] { BoxVolume("fvol1", new Vector3(-10, -10, -10), new Vector3(10, 10, 10)) };

        Assert.Equal(1, weather!.CameraWeatherState(new Vector3(1000, 0, 0), fogZoneArmed: true, volumes));
    }

    [Fact]
    public void FogZoneDisarmedInsideAVolumeIsNotState3()
    {
        // C1-style: the chapter's fvol volumes exist but fogvol.zrd's fog_zone is 0 (present,
        // disarmed), FogVolumeSpec.FogZoneArmed is false, and a camera inside a volume must not
        // pick up state 3 from geometry alone.
        var weather = WeatherState.Load(TestData.Fixture("weather-no-cloud"));
        Assert.NotNull(weather);
        var volumes = new[] { BoxVolume("fvol1", new Vector3(-10, -10, -10), new Vector3(10, 10, 10)) };

        Assert.Equal(1, weather!.CameraWeatherState(Vector3.Zero, fogZoneArmed: false, volumes));
    }

    [Fact]
    public void State3TakesPrecedenceOverState2()
    {
        // The binary assigns state 3 after state 2: a camera above the core bottom
        // AND inside an armed volume is state 3, not 2, even though shipped data never actually
        // exercises this overlap (C5's fog_zone-armed band sits far above any C5 volume).
        var weather = Load();
        var volumes = new[] { BoxVolume("fvol1", new Vector3(-10, 1900, -10), new Vector3(10, 1910, 10)) };

        Assert.Equal(3, weather.CameraWeatherState(new Vector3(0, 1905, 0), fogZoneArmed: true, volumes));
    }

    private static Vector3 At(float altitude) => new(0f, altitude, 0f);

    private static WeatherState Load()
    {
        var weather = WeatherState.Load(TestData.Fixture("zrdr"));
        Assert.NotNull(weather);
        return weather!;
    }

    // A hand-built axis-aligned FogVolumeBox, the six outward-facing unit-normal planes of
    // [min, max], mirroring FogVolumeTests.BoxVolume (not shared across test files by design;
    // each pins its own minimal fixture shape).
    private static FogVolumeBox BoxVolume(string name, Vector3 min, Vector3 max) => new(
        name,
        new Aabb(min, max - min),
        new[]
        {
            new Plane(new Vector3(1, 0, 0), max.X),
            new Plane(new Vector3(-1, 0, 0), -min.X),
            new Plane(new Vector3(0, 1, 0), max.Y),
            new Plane(new Vector3(0, -1, 0), -min.Y),
            new Plane(new Vector3(0, 0, 1), max.Z),
            new Plane(new Vector3(0, 0, -1), -min.Z),
        });
}
