using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Session.World;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The camera weather state driving which zone's fog the flight wears,
/// <see cref="WeatherState.ZoneForState"/> (state n → <c>ZONE&lt;n&gt;</c>, through
/// <see cref="WeatherState.ResolveZone(string)"/>'s file fallback) and
/// <see cref="WeatherRig.FogStateTrigger"/> (apply on the EDGE, never per frame).
///
/// <para>The two halves are tested apart because they fail apart: a wrong mapping renders the
/// wrong zone's fog, a wrong trigger renders the right zone's fog over and over, and the second
/// is invisible in a screenshot while being exactly what would make the rim annulus shimmer
/// at the boundary (the ⚠ trap).</para>
/// </summary>
public class FogZoneStateTests
{
    [Fact]
    public void AStateWithNoAuthoredZoneFallsBackToTheFilesFirstZone()
    {
        var weather = OneZoneFixture();
        Assert.Equal(new[] { "zone1" }, weather.ZoneNames);
        Assert.Equal("zone1", weather.ZoneForState(1));
        // Without the fallback this would answer "zone2", whose Fog() lookup misses and returns
        // NoFog, no fog at all and world light 1.0 (fullbright), the failure the fallback exists
        // to stop. Same for the C5-shaped state 3.
        Assert.Equal("zone1", weather.ZoneForState(2));
        Assert.Equal("zone1", weather.ZoneForState(3));
    }

    [ExtractedDataFact]
    public void ADeckChaptersStatesMapToItsTwoAuthoredZones()
    {
        // C1/IA1: below the deck (state 1) it must wear ZONE1's 1000-1750 m fog, above it
        // (state 2) ZONE2's 1000-4000 m, a 2.3x below-deck fog error if the state is ignored.
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(weather);
        Assert.Equal("zone1", weather!.ZoneForState(1));
        Assert.Equal("zone2", weather.ZoneForState(2));
        Assert.Equal(1750f, weather.Zone(weather.ZoneForState(1)).FogFar, 1);
        Assert.Equal(4000f, weather.Zone(weather.ZoneForState(2)).FogFar, 1);
        // ...and the state-2 fog is what a state-blind implementation renders EVERYWHERE, so the
        // above-deck half is an invariant and only the below-deck half moves.
        Assert.Equal(970f, weather.Zone(weather.ZoneForState(1)).FogLow, 1);
        Assert.Equal(4000f, weather.Zone(weather.ZoneForState(2)).FogLow, 1);
    }

    [ExtractedDataFact]
    public void C5sStatesResolveToItsZone1AndZone3()
    {
        // C5 ships ZONE1 + ZONE3 (no ZONE2 anywhere in the chapter), so its states exercise both
        // the direct hit (3 → zone3) and the fallback (2 → zone1).
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C5", "IA1"));
        Assert.NotNull(weather);
        Assert.Equal("zone1", weather!.ZoneForState(1));
        Assert.Equal("zone1", weather.ZoneForState(2));
        Assert.Equal("zone3", weather.ZoneForState(3));
    }

    // ZONE3 is present in all 8 C5 missions, asserted from the files rather than from one. IA1
    // above only pins the mission every golden flies; this walks the other 7 so a mission whose
    // author dropped ZONE3 (or renamed it) cannot hide behind IA1 passing.
    [ExtractedDataTheory]
    [InlineData("IA1")]
    [InlineData("M01")]
    [InlineData("M02")]
    [InlineData("M03")]
    [InlineData("M04")]
    [InlineData("MP1")]
    [InlineData("MP2")]
    [InlineData("MP3")]
    public void EveryC5MissionAuthorsZone1AndZone3(string mission)
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C5", mission));
        Assert.NotNull(weather);
        Assert.Contains("zone1", weather!.ZoneNames);
        Assert.Contains("zone3", weather.ZoneNames);
        Assert.Equal("zone3", weather.ZoneForState(3));
    }

    // State 3 wears ZONE3's fog (50-250 m, FOG_COLOR [16,16,16]) through the same per-state
    // machinery, no clip-range plumbing (the ⚠ trap: ZONE3's CLIP_RANGES far of 300 is NOT
    // applied; the remake fogs instead of clipping), no extra smoothing (the whiteout curtain
    // hides the hard switch). Leaving the volume restores
    // ZONE1 exactly, proving the trigger is symmetric rather than a one-way latch.
    [ExtractedDataFact]
    public void StateThreeFlipAppliesZone3FogAndExitRestoresZone1()
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C5", "IA1"));
        Assert.NotNull(weather);
        // C5's own default resolution (ResolveZone falling back off "zone2") already lands on
        // zone1, mirroring what WeatherRig.Build hands the trigger as its starting buildZone.
        var trigger = new WeatherRig.FogStateTrigger(stateDriven: true, buildZone: "zone1");

        var into = trigger.Next(3, weather!);
        Assert.NotNull(into);
        Assert.True(into!.Value.Applied);
        Assert.False(into.Value.FellBack);
        Assert.Equal("zone3", into.Value.Zone);
        var zone3Fog = weather!.Zone(into.Value.Zone);
        Assert.Equal(50f, zone3Fog.FogNear, 1);
        Assert.Equal(250f, zone3Fog.FogFar, 1);
        Assert.Equal(16f / 255f, zone3Fog.FogColor.R, 3);
        Assert.Equal(16f / 255f, zone3Fog.FogColor.G, 3);
        Assert.Equal(16f / 255f, zone3Fog.FogColor.B, 3);
        // ZONE3's own SUNLIGHT block (diffuse 1.5 / ambient 0.5) rode along with the fog, the
        // same ApplyZone call writes csky_world_light from this same ZoneWeather record.
        Assert.Equal(1f, zone3Fog.WorldLight, 3);

        var outOf = trigger.Next(1, weather!);
        Assert.NotNull(outOf);
        Assert.True(outOf!.Value.Applied);
        Assert.Equal("zone1", outOf.Value.Zone);
        var zone1Fog = weather!.Zone(outOf.Value.Zone);
        Assert.Equal(1500f, zone1Fog.FogNear, 1);
        Assert.Equal(2250f, zone1Fog.FogFar, 1);
        Assert.Equal(2, trigger.Applications);
    }

    // Bypasses the arming gate on purpose, feeding state 3 straight to a fog_zone-0 chapter's
    // weather (C1, no ZONE3 at all), to show ZoneForState's file fallback holds as a second
    // layer: it lands state 3 back on the chapter's first zone, never on a zone3 that does not
    // exist.
    [ExtractedDataFact]
    public void AFogZoneZeroChapterNeverAppliesZone3EvenIfStateThreeWereRequested()
    {
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(weather);
        Assert.DoesNotContain("zone3", weather!.ZoneNames);
        var trigger = new WeatherRig.FogStateTrigger(stateDriven: true, buildZone: "zone1");

        var change = trigger.Next(3, weather);
        Assert.NotNull(change);
        Assert.Equal("zone1", change!.Value.Zone);   // falls back, never "zone3"
        Assert.True(change.Value.FellBack);
        Assert.False(change.Value.Applied);           // already live on zone1, nothing written
        Assert.Equal(0, trigger.Applications);
    }

    [Fact]
    public void TheTriggerAppliesOnTheEdgeAndNeverAgainWhileTheStateHolds()
    {
        // The ⚠ trap: the original applies the fog on a state CHANGE. Re-writing the fog globals
        // every frame would render identically at a static pose and shimmer the rim annulus in motion, so
        // the count is the only thing that can catch it.
        var weather = OneZoneFixture();
        var trigger = new WeatherRig.FogStateTrigger(stateDriven: true, buildZone: "zone2");

        var first = trigger.Next(1, weather);
        Assert.NotNull(first);
        Assert.True(first!.Value.Applied);
        Assert.Equal("zone1", first.Value.Zone);
        Assert.Equal(1, trigger.Applications);

        // Sixty more frames at the same altitude: nothing to say, nothing written.
        for (int frame = 0; frame < 60; frame++)
            Assert.Null(trigger.Next(1, weather));
        Assert.Equal(1, trigger.Applications);
        Assert.Equal("zone1", trigger.Zone);
    }

    [Fact]
    public void AStateChangeThatResolvesToTheLiveZoneWritesNothing()
    {
        // State 1 -> 2 is a real edge, but both resolve to zone1, so globals must stay untouched
        // even though the fallback is still reported once.
        var weather = OneZoneFixture();
        var trigger = new WeatherRig.FogStateTrigger(stateDriven: true, buildZone: "zone1");

        var up = trigger.Next(2, weather);
        Assert.NotNull(up);
        Assert.False(up!.Value.Applied);
        Assert.True(up.Value.FellBack);
        Assert.Equal("zone1", up.Value.Zone);
        Assert.Equal(0, trigger.Applications);

        // Down and up again: the fallback line is not repeated.
        Assert.False(trigger.Next(1, weather)!.Value.FellBack);
        Assert.False(trigger.Next(2, weather)!.Value.FellBack);
        Assert.Equal(0, trigger.Applications);
    }

    [Fact]
    public void AnExplicitSkyZoneKeepsTheFogStatic()
    {
        // --sky-zone is a state OVERRIDE. An inspection pose that silently swapped
        // zone with altitude would not be reproducible, and analysis/ repro poses depend on it.
        var weather = OneZoneFixture();
        var trigger = new WeatherRig.FogStateTrigger(stateDriven: false, buildZone: "zone2");

        Assert.Null(trigger.Next(1, weather));
        Assert.Null(trigger.Next(2, weather));
        Assert.Null(trigger.Next(3, weather));
        Assert.Equal(0, trigger.Applications);
        Assert.Equal("zone2", trigger.Zone);
    }

    [ExtractedDataFact]
    public void TheDeckChaptersSwitchBothFogAndWorldLightWhileTheOtherChaptersCannot()
    {
        // C1/IA1 is one of the 18 of 24 deck-chapter missions where fog and world light agree,
        // so the below-deck difference is fog-only in practice for the mission every golden flies.
        var ia1 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(ia1);
        Assert.Equal(
            ia1!.Zone(ia1.ZoneForState(1)).WorldLight,
            ia1.Zone(ia1.ZoneForState(2)).WorldLight,
            3);

        var m02 = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "M02"));
        Assert.NotNull(m02);
        // ZONE1 ambient 0.20 / diffuse 1.5 vs ZONE2 0.25 / 1.2, a real per-state brightness the
        // static resolution could never render.
        Assert.NotEqual(
            m02!.Zone(m02.ZoneForState(1)).WorldLight,
            m02.Zone(m02.ZoneForState(2)).WorldLight,
            3);
    }

    // The zrdr fixture authors ZONE1 only (fixtures/zrdr/weather.json), which is precisely the
    // shape the fallback exists for, a state-2 flip there has no ZONE2 to land on.
    private static WeatherState OneZoneFixture()
    {
        var weather = WeatherState.Load(TestData.Fixture("zrdr"));
        Assert.NotNull(weather);
        return weather!;
    }
}
