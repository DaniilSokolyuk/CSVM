using CSVM.Flight.Airframe;
using CSVM.Session.World;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Both mappings from a zone's authored SUNLIGHT to Godot energies
/// (<see cref="WeatherRig.EnhancedEnergies"/>, <see cref="WeatherRig.FaithfulEnergies"/>), and the
/// parse that carries the two authored scalars through uncollapsed beside the faithful path's
/// <c>WorldLight</c> scalar.
///
/// The mappings are pinned here rather than at the controls because their failure mode is silent:
/// a factor that drifts still renders, just at the wrong level in every mission at once.
/// </summary>
public class SunlightEnergyTests
{
    // The anchor the two factors were chosen on: the install's modal day zone must land on the
    // energies the faithful path hardcodes (Launcher.SetupLighting: 1.6 sun, 0.9 ambient), so
    // enhanced mode leaves a day mission at the level it already had.
    [Fact]
    public void TheModalDayZoneLandsOnTheHardcodedEnergies()
    {
        (float sun, float ambient) = WeatherRig.EnhancedEnergies(Zone(diffuse: 1.5f, ambient: 0.5f));
        Assert.Equal(1.6f, sun, 1);
        Assert.Equal(0.9f, ambient, 2);
    }

    // The point of the item: a night zone's authored values must resolve dimmer than a day one's
    // from the data alone, with no per-chapter special case.
    [Fact]
    public void ANightZoneResolvesDimmerThanADayZone()
    {
        (float nightSun, float nightAmbient) = WeatherRig.EnhancedEnergies(Zone(0.6f, 0.15f));
        (float daySun, float dayAmbient) = WeatherRig.EnhancedEnergies(Zone(1.5f, 0.5f));
        Assert.True(nightSun < daySun * 0.5f);
        Assert.True(nightAmbient < dayAmbient * 0.5f);
        Assert.Equal(0.64f, nightSun, 2);
        Assert.Equal(0.27f, nightAmbient, 2);
    }

    // C5 is the case the night cap exists for: a black FOG_COLOR over the install's modal DAY
    // SUNLIGHT pair, which without the cap lights a night city at noon level.
    [Fact]
    public void ADayLevelPairUnderABlackSkyCapsToTheNightPair()
    {
        var night = Zone(diffuse: 1.5f, ambient: 0.5f, fog: Colors.Black);
        Assert.True(WeatherRig.IsNightZone(night));
        (float sun, float ambient) = WeatherRig.EnhancedEnergies(night);
        Assert.Equal(0.64f, sun, 2);
        Assert.Equal(0.27f, ambient, 2);
    }

    // The cap is a ceiling, not a replacement: a zone already authored dimmer than the night pair
    // keeps its own values, so C1B's 0.6 / 0.15 renders exactly as before.
    [Fact]
    public void AZoneAuthoredDimmerThanTheCapKeepsItsOwnValues()
    {
        (float sun, float ambient) = WeatherRig.EnhancedEnergies(Zone(0.6f, 0.15f, Colors.Black));
        Assert.Equal(0.64f, sun, 2);
        Assert.Equal(0.27f, ambient, 2);
    }

    // The separator has to hold on the install's own two populations, not on invented colours:
    // C5's night zone authors a black fog and C4's day zone a 0.75 grey one.
    [ExtractedDataFact]
    public void TheInstallsNightAndDayFogColoursFallOnOppositeSidesOfTheRule()
    {
        var c5 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C5", "IA1"));
        var c4 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C4", "IA1"));
        Assert.NotNull(c5);
        Assert.NotNull(c4);
        Assert.True(WeatherRig.IsNightZone(c5!.Zone("zone1")));
        Assert.True(WeatherRig.IsNightZone(c5!.Zone("zone3")));
        Assert.False(WeatherRig.IsNightZone(c4!.Zone("zone1")));
        // And the cap is what C5 gets out of it, from the same day-level pair C4 keeps.
        Assert.Equal(0.64f, WeatherRig.EnhancedEnergies(c5!.Zone("zone1")).Sun, 2);
        Assert.Equal(1.6f, WeatherRig.EnhancedEnergies(c4!.Zone("zone1")).Sun, 1);
    }

    // The plumbing: SUNLIGHT_DIFFUSE and SUNLIGHT_AMBIENT reach the record uncollapsed. Before
    // this they existed only inside WorldLightFactor's clamp, which is why the launcher's
    // hardcoded energies could not be replaced by data.
    [ExtractedDataFact]
    public void C1BsNightZoneCarriesItsAuthoredDiffuseAndAmbient()
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1B", "IA1"));
        Assert.NotNull(weather);
        var zone = weather!.Zone("zone1");
        Assert.Equal(0.6f, zone.SunDiffuse, 2);
        Assert.Equal(0.15f, zone.SunAmbient, 2);
        // The faithful scalar is untouched by the new fields: ambient + diffuse * 0.46, clamped.
        Assert.Equal(0.426f, zone.WorldLight, 3);
    }

    // C4 is the one chapter whose SUNLIGHT colours are authored away from white (warm sun, cold
    // ambient), so the colour half of the block has a case that can actually fail.
    [ExtractedDataFact]
    public void C4sZoneCarriesItsAuthoredSunlightColours()
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C4", "IA1"));
        Assert.NotNull(weather);
        var zone = weather!.Zone("zone1");
        Assert.Equal(1.0f, zone.SunColorDiffuse.R, 2);
        Assert.Equal(0.8f, zone.SunColorDiffuse.G, 2);
        Assert.Equal(0.7f, zone.SunColorDiffuse.B, 2);
        Assert.Equal(0.7f, zone.SunColorAmbient.R, 2);
        Assert.Equal(0.9f, zone.SunColorAmbient.G, 2);
        Assert.Equal(1.0f, zone.SunColorAmbient.B, 2);
    }

    // The faithful path's anchor: the modal day zone keeps the energies the launcher builds with,
    // so the missions that author it render exactly as they did before the mapping existed.
    [Fact]
    public void TheModalDayZoneKeepsTheLauncherEnergiesInTheFaithfulPath()
    {
        (float sun, float ambient) = WeatherRig.FaithfulEnergies(Zone(diffuse: 1.5f, ambient: 0.5f));
        Assert.Equal(WeatherRig.DefaultEnergies.Sun, sun, 3);
        Assert.Equal(WeatherRig.DefaultEnergies.Ambient, ambient, 3);
    }

    // The point of the item, on the faithful path this time: a night zone's authored values must
    // light the aircraft dimmer than a day zone's, from the data alone.
    [Fact]
    public void ANightZoneLightsThePlaneDimmerThanADayZoneInTheFaithfulPath()
    {
        (float nightSun, float nightAmbient) = WeatherRig.FaithfulEnergies(Zone(0.6f, 0.15f));
        (float daySun, float dayAmbient) = WeatherRig.FaithfulEnergies(Zone(1.5f, 0.5f));
        Assert.True(nightSun < daySun * 0.5f);
        Assert.True(nightAmbient < dayAmbient * 0.5f);
        Assert.Equal(0.64f, nightSun, 2);
        Assert.Equal(0.27f, nightAmbient, 2);
    }

    // The cap, which is where the faithful mapping parts from the enhanced one: this pass has no
    // tonemap, so a zone brighter than the day pair must not push the plane past white.
    [Fact]
    public void AZoneBrighterThanTheDayPairIsCappedAtTheDayLevel()
    {
        (float sun, float ambient) = WeatherRig.FaithfulEnergies(Zone(2.0f, 0.6f));
        Assert.Equal(WeatherRig.DefaultEnergies.Sun, sun, 3);
        Assert.Equal(WeatherRig.DefaultEnergies.Ambient, ambient, 3);
    }

    // The other parting: no night gate on the faithful path. C5's fullbright world takes its own
    // day-level SUNLIGHT, so capping the plane there would sink it below its own terrain.
    [Fact]
    public void ADayLevelPairUnderABlackSkyIsNotCappedInTheFaithfulPath()
    {
        var night = Zone(diffuse: 1.5f, ambient: 0.5f, fog: Colors.Black);
        Assert.True(WeatherRig.IsNightZone(night));
        (float sun, float ambient) = WeatherRig.FaithfulEnergies(night);
        Assert.Equal(WeatherRig.DefaultEnergies.Sun, sun, 3);
        Assert.Equal(WeatherRig.DefaultEnergies.Ambient, ambient, 3);
    }

    // The install's own night/day pair, off the shipped data rather than invented scalars: C1B's
    // night mission against C1C's daylight, which is the A/B this mapping exists to produce.
    [ExtractedDataFact]
    public void C1BsNightMissionLightsThePlaneDimmerThanC1CsDayMission()
    {
        var night = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1B", "IA1"));
        var day = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1C", "M01"));
        Assert.NotNull(night);
        Assert.NotNull(day);
        (float nightSun, float nightAmbient) = WeatherRig.FaithfulEnergies(night!.Zone("zone1"));
        (float daySun, float dayAmbient) = WeatherRig.FaithfulEnergies(day!.Zone("zone1"));
        Assert.True(nightSun < daySun * 0.5f);
        Assert.True(nightAmbient < dayAmbient * 0.5f);
        Assert.Equal(0.64f, nightSun, 2);
        Assert.Equal(1.6f, daySun, 2);
    }

    // The bicolored bit decides which colour the ambient half wears. Clear, the original reads only
    // the diffuse colour for both halves, so an authored ambient colour must not reach the plane.
    [Fact]
    public void AnUnbicoloredZoneLightsBothHalvesWithTheDiffuseColour()
    {
        var zone = Zone(1.2f, 0.25f) with
        {
            SunColorDiffuse = new Color(1f, 0.8f, 0.7f),
            SunColorAmbient = new Color(0.7f, 0.9f, 1f),
        };
        (Vector3 ambient, Vector3 diffuse) = WeatherRig.SunVertexLight(zone);
        Assert.Equal(0.25f, ambient.X, 3);
        Assert.Equal(0.2f, ambient.Y, 3);
        Assert.Equal(0.175f, ambient.Z, 3);
        Assert.Equal(1.2f, diffuse.X, 3);
        Assert.Equal(0.96f, diffuse.Y, 3);
    }

    [Fact]
    public void ABicoloredZoneLightsTheAmbientHalfWithItsOwnColour()
    {
        var zone = Zone(1.5f, 0.5f) with
        {
            SunColorDiffuse = new Color(1f, 0.8f, 0.7f),
            SunColorAmbient = new Color(0.7f, 0.9f, 1f),
            SunBicolored = true,
        };
        (Vector3 ambient, Vector3 diffuse) = WeatherRig.SunVertexLight(zone);
        Assert.Equal(0.35f, ambient.X, 3);
        Assert.Equal(0.45f, ambient.Y, 3);
        Assert.Equal(0.5f, ambient.Z, 3);
        Assert.Equal(1.05f, diffuse.Z, 3);
    }

    // The bit off the shipped data: C5's night zone authors it set, C1's day zones clear.
    [ExtractedDataFact]
    public void TheBicoloredBitParsesOffTheInstallsZones()
    {
        var c5 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C5", "IA1"));
        var c1 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(c5);
        Assert.NotNull(c1);
        Assert.True(c5!.Zone("zone1").SunBicolored);
        Assert.False(c1!.Zone("zone1").SunBicolored);
    }

    // The photograph's fill: the byte FUN_004a0220 stores, ftol((A * 1.5 + 0.1) * 255 + 0.5)
    // clamped to 255, read back through the 1/255 the draw multiplies it by.
    [Theory]
    [InlineData(0.25f, 121)]
    [InlineData(0.5f, 217)]
    [InlineData(0f, 26)]
    [InlineData(0.6f, 255)]
    [InlineData(1.5f, 255)]
    public void ThePhotographFillIsTheStoredByte(float ambient, int stored)
        => Assert.Equal(stored * 0.003921569f, WeatherRig.PhotographFillAmbient(ambient), 6);

    // The fill replaces the ambient scalar alone, so it keeps the colour the ambient half is
    // lit with, bicoloured or not.
    [Fact]
    public void ThePhotographFillKeepsTheAmbientHalfsColour()
    {
        var zone = Zone(1.5f, 0.25f) with
        {
            SunColorDiffuse = new Color(1f, 0.8f, 0.7f),
            SunColorAmbient = new Color(0.7f, 0.9f, 1f),
        };
        float fill = 121 * 0.003921569f;
        Assert.Equal(new Vector3(1f, 0.8f, 0.7f) * fill, WeatherRig.PhotographFill(zone));
        Assert.Equal(new Vector3(0.7f, 0.9f, 1f) * fill,
            WeatherRig.PhotographFill(zone with { SunBicolored = true }));
    }

    // The default fog is the install's modal DAY colour, so a case that says nothing about the
    // sky gets the day arm of the night rule.
    private static WeatherState.ZoneWeather Zone(float diffuse, float ambient, Color? fog = null)
        => new(fog ?? Colors.Gray, 1000f, 2000f, 0f, 1f, 2050f, 1f, Vector3.Zero,
            diffuse, ambient, Colors.White, Colors.White);
}
