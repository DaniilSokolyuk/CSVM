using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Session.World;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's <c>zone_id</c> visibility gate (<see cref="ZoneGate"/>): a node draws iff its
/// gamez <c>zone_id</c> is −1, or is in the camera's armed set <c>{0, camera weather state}</c>.
/// Pins both the rule and the per-chapter census it is pointed at; the census is what catches a
/// regression, since the gate itself reads correct against any of them.
/// ⚠ C2B breaks the pattern (its fog volumes are <c>zone_id −1</c>, not 2 like the other deck
/// chapters); see docs/formats/weather.md's C2B divergence note.
/// </summary>
public class ZoneGateTests
{
    /// <summary>Per chapter: the <c>fvol*</c> volumes' zone (−1 when the chapter ships none), and
    /// the horizon's zone children as <c>name:zone_id</c> in gamez child order. Surveyed off
    /// <c>extracted/*/gamez/nodes.json</c>; see docs/formats/weather.md.</summary>
    public static TheoryData<string, int, string> ChapterZoneCensus => new()
    {
        { "C1", 2, "zone1:1 zone2:2" },
        { "C1B", -1, "zone1:1 zone2:2" },   // no fvol* at all
        { "C1C", 2, "zone2:2 zone1:1" },
        { "C2", -1, "zone2:2 zone1:1" },    // no fvol* at all
        { "C2B", -1, "zone2:2 zone1:1" },   // ⚠ ships NINE fvol* volumes, all zone_id −1
        { "C3", -1, "zone2:2 zone1:1" },    // no fvol* at all
        { "C4", 2, "zone2:2 zone1:1" },
        { "C5", 1, "zone3:3 zone1:1" },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ZoneMinusOneAndZoneZeroDrawAtEveryState(int state)
    {
        // The armed set is {0, state}: -1 short-circuits the test outright, 0 is a member of every
        // set the engine ever arms. Both are "always", and neither may ever be gated.
        Assert.True(ZoneGate.Draws(-1, state));
        Assert.True(ZoneGate.Draws(0, state));
    }

    [Fact]
    public void AZonedNodeDrawsOnlyInItsOwnState()
    {
        for (int zone = 1; zone <= ZoneGate.MaxZoneId; zone++)
        {
            for (int state = 1; state <= ZoneGate.MaxZoneId; state++)
            {
                Assert.Equal(zone == state, ZoneGate.Draws(zone, state));
            }
        }
    }

    [Fact]
    public void AStateFlipTogglesExactlyTheTwoZonesInvolved()
    {
        // The deck chapters' whole behaviour in one assertion: below the deck (state 1) the
        // ground world draws and the cloud population does not; above it (state 2), the inverse.
        Assert.True(ZoneGate.Draws(1, 1));
        Assert.False(ZoneGate.Draws(2, 1));
        Assert.False(ZoneGate.Draws(1, 2));
        Assert.True(ZoneGate.Draws(2, 2));
        Assert.True(ZoneGate.Draws(-1, 1));
        Assert.True(ZoneGate.Draws(-1, 2));
    }

    [Fact]
    public void EachGatedZoneGetsItsOwnLayerAndTheRestGetNone()
    {
        var seen = new HashSet<uint>();
        for (int zone = 1; zone <= ZoneGate.MaxZoneId; zone++)
        {
            uint layer = ZoneGate.LayerFor(zone);
            Assert.NotEqual(0u, layer);
            Assert.True(seen.Add(layer), $"zone {zone} shares a layer with another zone");
            Assert.Equal(layer, layer & ZoneGate.LayerBand);
        }

        // An ungated id must never claim a bit, or a camera narrowing the band would cull content
        // the original always draws.
        Assert.Equal(0u, ZoneGate.LayerFor(-1));
        Assert.Equal(0u, ZoneGate.LayerFor(0));
        Assert.Equal(0u, ZoneGate.LayerFor(ZoneGate.MaxZoneId + 1));
    }

    [Fact]
    public void TheCullMaskNarrowsTheBandToOneStateAndTouchesNothingElse()
    {
        const uint everything = 0xFFFFF;                 // Godot's 20 visual layers
        const uint outside = everything & ~ZoneGate.LayerBand;

        for (int state = 1; state <= ZoneGate.MaxZoneId; state++)
        {
            uint mask = ZoneGate.CullMask(everything, state);
            Assert.Equal(ZoneGate.LayerFor(state), mask & ZoneGate.LayerBand);
            // Every bit outside the band, the default layer 1 and the per-player band, survives
            // untouched, which is what lets the gate compose with splitscreen instead of fighting it.
            Assert.Equal(outside, mask & ~ZoneGate.LayerBand);
        }

        // Applying it twice in a row (Tick writes it every frame) is idempotent, and a state
        // change is reversible, the band is rebuilt from scratch, never OR-ed into.
        uint two = ZoneGate.CullMask(ZoneGate.CullMask(everything, 2), 2);
        Assert.Equal(ZoneGate.CullMask(everything, 2), two);
        Assert.Equal(ZoneGate.CullMask(everything, 1), ZoneGate.CullMask(two, 1));
    }

    [Fact]
    public void TheEscapeHatchPutsTheWholeBandBack()
    {
        // --no-zone-cull, and the launcher camera's per-session reset: OpenCullMask must undo any
        // narrowing a previous session left behind, from any starting point.
        uint narrowed = ZoneGate.CullMask(0xFFFFF, 2);
        Assert.NotEqual(0xFFFFFu, narrowed);             // the control: it really did narrow
        Assert.Equal(0xFFFFFu, ZoneGate.OpenCullMask(narrowed));
        Assert.Equal(ZoneGate.LayerBand,
            ZoneGate.OpenCullMask(0u) & ZoneGate.LayerBand);
    }

    [Fact]
    public void SkyZoneNamesResolveToTheStateTheyForce()
    {
        // An explicit --sky-zone drives the gate as well as the fog, or an inspection
        // pose renders one zone's sky over another zone's content.
        Assert.Equal(1, WeatherRig.ZoneNumberOf("zone1"));
        Assert.Equal(2, WeatherRig.ZoneNumberOf("zone2"));
        Assert.Equal(3, WeatherRig.ZoneNumberOf("zone3"));
        Assert.Equal(2, WeatherRig.ZoneNumberOf("ZONE2"));
        // Anything that is not a zone number leaves the state machine's own answer standing,
        // rather than silently forcing state 0 and culling every zoned node in the world.
        Assert.Null(WeatherRig.ZoneNumberOf(string.Empty));
        Assert.Null(WeatherRig.ZoneNumberOf("zone9"));
        Assert.Null(WeatherRig.ZoneNumberOf("horizon"));
    }

    [ExtractedDataTheory]
    [MemberData(nameof(ChapterZoneCensus))]
    public void EveryChapterFvolAndHorizonZoneCensusIsWhatTheDataSays(
        string chapter, int expectedFvolZone, string expectedHorizon)
    {
        var gamez = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, chapter));

        Assert.Equal(expectedFvolZone, WorldBuilder.FogVolumeZoneIdOf(gamez));

        var census = new List<string>();
        foreach (var z in WorldBuilder.HorizonZonesOf(gamez))
        {
            census.Add($"{z.Name}:{z.ZoneId}");
        }

        Assert.Equal(expectedHorizon, string.Join(" ", census));
    }

    [ExtractedDataFact]
    public void C2BIsTheChapterWhoseFogVolumesAreNeverCulled()
    {
        // ⚠ Pinned separately: an implementation that reads "deck chapter => fvol zone 2" passes
        // every other assertion here. See docs/formats/weather.md's C2B divergence note.
        var c2b = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, "C2B"));
        int volumes = 0;
        foreach (var n in c2b.Nodes)
        {
            if (n.Name.StartsWith("fvol", System.StringComparison.OrdinalIgnoreCase))
            {
                volumes++;
                Assert.Equal(-1, n.ZoneId);
            }
        }

        Assert.Equal(9, volumes);   // the population really is there to be gated
        Assert.Equal(-1, WorldBuilder.FogVolumeZoneIdOf(c2b));
        Assert.True(ZoneGate.Draws(WorldBuilder.FogVolumeZoneIdOf(c2b), 1));
        Assert.True(ZoneGate.Draws(WorldBuilder.FogVolumeZoneIdOf(c2b), 2));

        // The able-to-fail half: C1 ships the same nine volumes at zone_id 2, and they ARE culled
        // below its deck. Same code, same call, opposite answer, decided by the data alone.
        var c1 = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, "C1"));
        Assert.Equal(2, WorldBuilder.FogVolumeZoneIdOf(c1));
        Assert.False(ZoneGate.Draws(WorldBuilder.FogVolumeZoneIdOf(c1), 1));
        Assert.True(ZoneGate.Draws(WorldBuilder.FogVolumeZoneIdOf(c1), 2));
    }

    [ExtractedDataFact]
    public void TheGateIsPerNodeAndNotInheritedDownASubtree()
    {
        // C1's flaglite1/flaglite2 are zone_id −1 children of a zone-1 parent, the data's own
        // counter-example to "stamp the subtree"; a subtree-inherited gate would hide them.
        var c1 = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, "C1"));
        int found = 0;
        for (int i = 0; i < c1.Nodes.Count; i++)
        {
            foreach (int child in c1.Nodes[i].Children)
            {
                if (child < 0 || child >= c1.Nodes.Count)
                {
                    continue;
                }

                if (c1.Nodes[i].ZoneId > 0 && c1.Nodes[child].ZoneId == -1)
                {
                    found++;
                }
            }
        }

        Assert.Equal(2, found);
    }
}
