using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Session.World;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The cloud deck's two altitude regimes (<see cref="WeatherRig.DeckRegime"/>): a world-fixed
/// sheet at the tiles' own authored altitude, dimmed below the <c>CLOUD_COVER</c> band centre and
/// undimmed at or above it. Decode: docs/org/weather.md, docs/formats/weather.md.
/// ⚠ There is no below-band ceiling member and no <c>DeckCeilingHeight</c> TUNE; see
/// docs/formats/weather.md's retired-mechanism entry.
/// ⚠ Whether the ambient cloud populations render is the original's <c>zone_id</c> gate, not this
/// rule; see <c>ZoneGateTests</c>.
/// </summary>
public class DeckRegimeTests
{
    // C1/IA1's authored CLOUD_COVER: band 970-1124, opaque core 30 m deep centred on 1047.
    private const float C1Bottom = 970f;
    private const float C1Top = 1124f;
    private const float C1Thickness = 30f;
    private const float C1Centre = 1047f;
    // C1/C1C/C2B's deck tiles' own authored altitude (WorldBuilder.CloudDeckAltitude; see
    // docs/formats/fogvol.md), where the above-band floor sits, rather than at C1Centre. C4's
    // authored 1050 equals its own band centre, so a C4 fixture cannot tell the two rules apart;
    // C1's 87 m gap is why this suite pins C1.
    private const float C1AuthoredDeckY = 960f;

    [Fact]
    public void BelowTheBandTheDeckNoLongerMovesWithTheCamera()
    {
        // Two cameras below the band get the same deck Y; a camera-relative ceiling could not.
        (float low, bool lowDimmed) = WeatherRig.DeckRegime(300f, C1Centre, C1AuthoredDeckY);
        (float high, bool highDimmed) = WeatherRig.DeckRegime(900f, C1Centre, C1AuthoredDeckY);
        Assert.Equal(C1AuthoredDeckY, low, 3);
        Assert.Equal(C1AuthoredDeckY, high, 3);
        // ...and it is still the overcast's UNDERSIDE all the way up, so it keeps the mission's
        // SUNLIGHT dimming at every altitude below the band (that face measures 168.9 against the
        // original's 167.7), the lit variant is the only thing the crossing flips.
        Assert.True(lowDimmed);
        Assert.True(highDimmed);
    }

    [Fact]
    public void AtAndAboveTheBandCentreTheDeckIsAWorldFixedFloorAtTheAuthoredAltitude()
    {
        // The above-band floor sits at the tiles' OWN authored altitude, never re-pinned to the
        // band centre. C1's 960 is 87 m below its band centre of 1047, so an implementation that
        // returned bandCentre here fails this.
        Assert.Equal(C1AuthoredDeckY, WeatherRig.DeckRegime(C1Centre, C1Centre, C1AuthoredDeckY).DeckY, 3);
        Assert.Equal(C1AuthoredDeckY, WeatherRig.DeckRegime(1192f, C1Centre, C1AuthoredDeckY).DeckY, 3);
    }

    [Fact]
    public void C4sAuthoredAltitudeEqualsItsBandCentreSoItIsUnmovedByTheAuthoredFloor()
    {
        // C4's authored deck altitude (1050) happens to equal its own CLOUD_COVER centre, the
        // coincidence a band-centre pin rides on undetected (see docs/formats/fogvol.md). Pinned
        // here so a C4 golden staying byte-identical under either rule is expected, not a missed
        // regression.
        const float c4Centre = 1050f;
        const float c4AuthoredDeckY = 1050f;
        Assert.Equal(c4AuthoredDeckY, WeatherRig.DeckRegime(c4Centre, c4Centre, c4AuthoredDeckY).DeckY, 3);
        Assert.Equal(c4AuthoredDeckY, WeatherRig.DeckRegime(1400f, c4Centre, c4AuthoredDeckY).DeckY, 3);
    }

    [Fact]
    public void TheDeckKeepsSunlightBelowTheBandAndDropsItAbove()
    {
        // The band crossing flips SUNLIGHT dimming: the two regimes show different faces of the
        // overcast, measured against docs/org/weather.md's fog-colour bound.
        Assert.True(WeatherRig.DeckRegime(C1Centre - 0.5f, C1Centre, C1AuthoredDeckY).DeckDimmed);
        Assert.False(WeatherRig.DeckRegime(C1Centre + 0.5f, C1Centre, C1AuthoredDeckY).DeckDimmed);
        // It is the ONLY thing the crossing decides, the sheet stays world-fixed at its
        // authored altitude on both sides, so a camera 292 m under the band and one 145 m over it
        // differ in lit variant and in nothing else.
        var below = WeatherRig.DeckRegime(900f, C1Centre, C1AuthoredDeckY);
        var above = WeatherRig.DeckRegime(1192f, C1Centre, C1AuthoredDeckY);
        Assert.True(below.DeckDimmed);
        Assert.False(above.DeckDimmed);
        Assert.Equal(below.DeckY, above.DeckY, 3);
    }

    [Fact]
    public void TwoCamerasOnOppositeSidesOfTheBandGetOppositeRegimes()
    {
        // The splitscreen requirement, asserted on the rule itself: the answer must be a function
        // of one camera's own altitude and nothing else, because each pane holds its own deck
        // copy. P1 below, P2 above.
        var p1 = WeatherRig.DeckRegime(900f, C1Centre, C1AuthoredDeckY);
        var p2 = WeatherRig.DeckRegime(1192f, C1Centre, C1AuthoredDeckY);
        // The sheet's brightness: P1 sees the dimmed underside while P2 sees the undimmed top, at
        // the same instant, of one world's deck, which is why the swap is a per-instance mesh
        // assignment on each rig's own copy and never a shared material.
        Assert.True(p1.DeckDimmed);
        Assert.False(p2.DeckDimmed);
        // Both panes' decks sit at the one authored altitude, P1 under it, P2 over it.
        Assert.Equal(C1AuthoredDeckY, p1.DeckY, 3);
        Assert.Equal(C1AuthoredDeckY, p2.DeckY, 3);
    }

    [ExtractedDataTheory]
    [InlineData("C1", 960f, 970.00f)]
    [InlineData("C1C", 960f, 970.73f)]
    [InlineData("C2B", 960f, 970.00f)]
    [InlineData("C4", 1050f, 1060.00f)]
    public void TheDeckMeshStaysAboutTenMetresBelowTheFvolSlabFloor(
        string chapter, float expectedDeckAltitude, float expectedSlabFloor)
    {
        // The authored deck-to-slab relationship (docs/formats/fogvol.md), read off the data
        // rather than re-typed as a constant, so a change to either reader fails this.
        var gamez = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, chapter));

        float? deckAltitude = WorldBuilder.CloudDeckAltitudeOf(gamez);
        Assert.Equal(expectedDeckAltitude, deckAltitude);

        // The map-spanning slab's own floor: every top-anchored box volume (the slab pieces, not
        // C1C's build-up frusta) shares one bottom Y, asserted, not assumed.
        float? slabFloor = null;
        foreach (var volume in FogVolumeSpec.VolumesOf(gamez))
        {
            if (!volume.IsAxisAlignedBox())
                continue;
            float y = volume.Box.Position.Y;
            if (slabFloor is { } got)
                Assert.Equal(got, y, 2);
            else
                slabFloor = y;
        }
        Assert.NotNull(slabFloor);
        Assert.Equal(expectedSlabFloor, slabFloor!.Value, 2);
        Assert.True(slabFloor > deckAltitude, "the mesh must sit below the slab, not inside it");
    }

    [ExtractedDataFact]
    public void TheRegimeFlipHappensInsideTheFullyOpaqueWhiteoutCore()
    {
        // ⚠ Asserted against the authored band, not a hand-typed one: a mismatch is a finding
        // about the whiteout band, never a licence to move the flip.
        var weather = WeatherState.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "IA1"));
        Assert.NotNull(weather);
        Assert.Equal(C1Bottom, weather!.CloudBottom, 3);
        Assert.Equal(C1Top, weather.CloudTop, 3);
        Assert.Equal(C1Thickness, weather.CloudThickness, 3);
        Assert.Equal(C1Centre, weather.CloudBandCentre, 3);
        // The deck flips exactly here, and here the pane is solid whiteout.
        Assert.Equal(1f, weather.WhiteoutAmount(weather.CloudBandCentre), 3);
        Assert.Equal(1f, weather.WhiteoutAmount(weather.CloudBandCentre - 1f), 3);
        Assert.Equal(1f, weather.WhiteoutAmount(weather.CloudBandCentre + 1f), 3);
        // ...and the core really does end, so the three assertions above can fail.
        Assert.True(weather.WhiteoutAmount(weather.CloudBandCentre + (C1Thickness * 0.5f) + 20f) < 1f);
    }
}
