using System.Collections.Generic;
using CSVM;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Which weather/sky zone a chapter renders, the rule (<see
/// cref="WeatherState.PreferPopulatedHorizonZone"/>) and the census it reads (<see
/// cref="WorldBuilder.HorizonZonesOf"/>).
///
/// <para>Nothing on disk names the zone a mission flies (searched exhaustively,
/// docs/formats/weather.md), so the <c>zone2</c> request is a default, not a datum. What IS on
/// disk is whether a zone has a dome to build, and three chapters ship a <c>zone2</c> that has
/// none. These tests pin both halves: the rule's shape on hand-built censuses, and the real
/// census + resolved zone for all eight chapters, including the five the rule must leave
/// alone.</para>
/// </summary>
public class SkyZoneTests
{
    /// <summary>The horizon zone census of every chapter, and the zone the default <c>zone2</c>
    /// request resolves to against it alone (<see cref="WeatherState.ResolveZone(string)"/> has
    /// the mission's weather.json say separately). Rows are gamez child order, not zone-number
    /// order; see <c>docs/formats/weather.md</c>.</summary>
    public static TheoryData<string, string, string> ChapterHorizonZones => new()
    {
        { "C1", "zone1:2 zone2:4", "zone2" },
        { "C1B", "zone1:4 zone2:0", "zone1" },
        { "C1C", "zone2:4 zone1:1", "zone2" },
        { "C2", "zone2:0 zone1:3", "zone1" },
        { "C2B", "zone2:2 zone1:1", "zone2" },
        { "C3", "zone2:0 zone1:3", "zone1" },
        { "C4", "zone2:4 zone1:1", "zone2" },
        { "C5", "zone3:1 zone1:2", "zone2" },
    };

    [Fact]
    public void ARequestWithGeometryOfItsOwnIsKept()
    {
        // C1's shape: both zones build, and zone2 is what was asked for.
        Assert.Equal("zone2", WeatherState.PreferPopulatedHorizonZone("zone2", Zones(("zone1", 2), ("zone2", 4))));
    }

    [Fact]
    public void AnEmptyRequestYieldsToTheOneZoneThatBuilds()
    {
        // C1B/C2/C3: zone2 is a bare marker, so the sky and fog come from zone1 instead.
        Assert.Equal("zone1", WeatherState.PreferPopulatedHorizonZone("zone2", Zones(("zone1", 4), ("zone2", 0))));
    }

    [Fact]
    public void AZoneTheHorizonDoesNotHaveIsLeftToTheWeatherFile()
    {
        // C5 ships zone3/zone1 and no zone2 at all. The rule must NOT reach for the horizon's
        // first zone here: C5's weather.json lists ZONE1 first while its horizon lists zone3
        // first, so that would render zone3's sky under zone1's fog. ResolveZone owns this case.
        Assert.Equal("zone2", WeatherState.PreferPopulatedHorizonZone("zone2", Zones(("zone3", 1), ("zone1", 2))));
    }

    [Fact]
    public void TwoBuildableZonesLeaveTheRequestAlone()
    {
        Assert.Equal("zone2", WeatherState.PreferPopulatedHorizonZone("zone2", Zones(("zone1", 1), ("zone2", 4))));
    }

    [Fact]
    public void AHorizonWhereNothingBuildsLeavesTheRequestAlone()
    {
        Assert.Equal("zone2", WeatherState.PreferPopulatedHorizonZone("zone2", Zones(("zone1", 0), ("zone2", 0))));
        Assert.Equal("zone2", WeatherState.PreferPopulatedHorizonZone("zone2", Zones()));
    }

    [Fact]
    public void TheSwapIsCaseInsensitiveOnTheRequest()
    {
        // --sky-zone= is a raw CLI string; every other zone comparison in the engine is
        // OrdinalIgnoreCase, and a rule that silently stopped firing on "ZONE2" would look
        // exactly like the bug it fixes.
        Assert.Equal("zone1", WeatherState.PreferPopulatedHorizonZone("ZONE2", Zones(("zone1", 4), ("zone2", 0))));
    }

    [ExtractedDataTheory]
    [MemberData(nameof(ChapterHorizonZones))]
    public void EveryChapterHorizonCensusAndResolvedZoneAreWhatTheDataSays(
        string chapter, string expectedCensus, string expectedZone)
    {
        var gamez = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, chapter));
        var zones = WorldBuilder.HorizonZonesOf(gamez);

        var census = new List<string>();
        foreach (var z in zones)
        {
            census.Add($"{z.Name}:{z.MeshedNodes}");
        }

        Assert.Equal(expectedCensus, string.Join(" ", census));
        Assert.Equal(expectedZone, WeatherState.PreferPopulatedHorizonZone("zone2", zones));
    }

    private static IReadOnlyList<HorizonZone> Zones(params (string Name, int Meshed)[] zones)
    {
        var list = new List<HorizonZone>();
        foreach (var (name, meshed) in zones)
        {
            list.Add(new HorizonZone(name, meshed));
        }

        return list;
    }
}
