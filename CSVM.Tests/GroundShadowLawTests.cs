using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The aircraft ground shadow's placement law (<see cref="GroundShadowLaw"/>): the projection
/// direction with the player's forward skew, both fades, the footprint scale, the derived
/// colour, and the spread the coverage ramp is indexed by. Decode: docs/org/shadows.md.
/// ⚠ Every number here is the original's; a failing case means the port drifted, not that the
/// expectation needs adjusting.
/// </summary>
public class GroundShadowLawTests
{
    // A height that sits inside the altitude ramp for every case below, and one that clears it.
    private const float MidAltitude = 155f;
    private const float HighAltitude = 400f;

    // The install's modal day SUNLIGHT pair, which is what 53 of 53 weather files author.
    private static readonly Vector3 DayDiffuse = Vector3.One * 1.5f;
    private static readonly Vector3 DayAmbient = Vector3.One * 0.5f;

    [Fact]
    public void EveryOtherAircraftProjectsStraightDown()
    {
        var dir = GroundShadowLaw.Direction(isPlayer: false, new Vector3(0f, 0f, -1f));
        Assert.Equal(Vector3.Down, dir);
        // Straight down puts the shadow exactly under the aircraft, whatever its altitude.
        var at = new Vector3(120f, 240f, -75f);
        var landed = GroundShadowLaw.Project(at, 40f, dir);
        Assert.Equal(at.X, landed.X, 3);
        Assert.Equal(at.Z, landed.Z, 3);
        Assert.Equal(40f, landed.Y, 3);
    }

    [Fact]
    public void ThePlayersOwnShadowRunsAheadByOneAndAHalfTimesItsAltitude()
    {
        // Nose along -Z, Godot's forward, so the lead must land in -Z and nowhere else.
        var dir = GroundShadowLaw.Direction(isPlayer: true, new Vector3(0f, 0f, -1f));
        var at = new Vector3(10f, 130f, 20f);
        float ground = 30f;
        var landed = GroundShadowLaw.Project(at, ground, dir);
        Assert.Equal(at.X, landed.X, 3);
        Assert.Equal(at.Z - (GroundShadowLaw.PlayerSkew * (at.Y - ground)), landed.Z, 2);
        // The skew is horizontal: a nose pitched up or down shortens it by the cosine and never
        // reverses it.
        var pitched = GroundShadowLaw.Direction(isPlayer: true, new Vector3(0f, 0.6f, -0.8f).Normalized());
        var pitchedLand = GroundShadowLaw.Project(at, ground, pitched);
        Assert.Equal(at.Z - (GroundShadowLaw.PlayerSkew * 0.8f * (at.Y - ground)), pitchedLand.Z, 2);
    }

    [Fact]
    public void TheDistanceFadeIsHorizontalAndLinearInSquaredDistance()
    {
        var player = new Vector3(0f, 500f, 0f);
        Assert.Equal(1f, GroundShadowLaw.DistanceFactor(player, player), 4);
        Assert.Equal(0f, GroundShadowLaw.DistanceFactor(new Vector3(500f, 0f, 0f), player), 4);
        // Halfway out in distance is three quarters of the way down in strength, the fade being
        // linear in the SQUARE of it.
        float half = GroundShadowLaw.DistanceFactor(new Vector3(100f, 0f, 0f), player);
        Assert.Equal(0.75f, half, 2);
        // Altitude is not part of it: the same ground track 1 km below reads the same.
        Assert.Equal(half, GroundShadowLaw.DistanceFactor(new Vector3(100f, -1000f, 0f), player), 4);
    }

    [Fact]
    public void TheAltitudeRampIsFullBelowSixtyAndGoneAtTwoFifty()
    {
        Assert.Equal(1f, GroundShadowLaw.AltitudeFactor(0f), 4);
        Assert.Equal(1f, GroundShadowLaw.AltitudeFactor(GroundShadowLaw.FullShadowAltitude), 4);
        Assert.Equal(0f, GroundShadowLaw.AltitudeFactor(GroundShadowLaw.CutoffAltitude), 4);
        Assert.Equal(0f, GroundShadowLaw.AltitudeFactor(HighAltitude), 4);
        Assert.Equal(0.5f, GroundShadowLaw.AltitudeFactor(MidAltitude), 2);
    }

    [Fact]
    public void OnlyThePlayersOwnFootprintGrowsWithAltitude()
    {
        Assert.Equal(1f, GroundShadowLaw.FootprintScale(isPlayer: false, 0f), 4);
        Assert.Equal(1f, GroundShadowLaw.FootprintScale(isPlayer: false, 1f), 4);
        Assert.Equal(1f, GroundShadowLaw.FootprintScale(isPlayer: true, 1f), 4);
        Assert.Equal(GroundShadowLaw.PlayerMaxScale,
            GroundShadowLaw.FootprintScale(isPlayer: true, 0f), 4);
        Assert.Equal(2f, GroundShadowLaw.FootprintScale(isPlayer: true, 0.5f), 4);
    }

    [Fact]
    public void TheColourIsTheLightTheAircraftBlocks()
    {
        // Day SUNLIGHT straight down: ambient 0.5 against ambient plus diffuse, times the fixed
        // 0.8, so a full-strength shadow multiplies the ground by 0.2.
        var full = GroundShadowLaw.Colour(DayDiffuse, DayAmbient, Vector3.Down, 1f);
        Assert.Equal(0.2f, full.R, 3);
        Assert.Equal(full.R, full.G, 5);
        Assert.Equal(full.R, full.B, 5);
        // No strength is no shadow at all, which in a modulate map is white.
        var none = GroundShadowLaw.Colour(DayDiffuse, DayAmbient, Vector3.Down, 0f);
        Assert.Equal(1f, none.R, 4);
        // A brighter ambient lightens it, the per-mission behaviour the zone's SUNLIGHT pair drives.
        var hazy = GroundShadowLaw.Colour(DayDiffuse, Vector3.One * 1.5f, Vector3.Down, 1f);
        Assert.True(hazy.R > full.R);
        // ⚠ No ambient at all falls back to the raw value rather than to a ratio: the darkest
        // shadow that channel can take, not the lightest.
        var lightless = GroundShadowLaw.Colour(DayDiffuse, Vector3.Zero, Vector3.Down, 1f);
        Assert.Equal(0f, lightless.R, 4);
    }

    [Fact]
    public void TheSpreadCarriesOneCoveredTexelIntoItsEightNeighbours()
    {
        const int size = 5;
        var covered = new bool[size * size];
        covered[(2 * size) + 2] = true;
        var ramp = GroundShadowLaw.Spread(covered, size, size);
        Assert.Equal(2, ramp[(2 * size) + 2]);
        Assert.Equal(1, ramp[(1 * size) + 1]);
        Assert.Equal(1, ramp[(3 * size) + 3]);
        Assert.Equal(0, ramp[(0 * size) + 0]);
        // A solid silhouette saturates the ramp in its interior, where a lone texel reached 2.
        for (int i = 0; i < covered.Length; i++)
            covered[i] = true;
        var solid = GroundShadowLaw.Spread(covered, size, size);
        Assert.Equal(GroundShadowLaw.RampSteps - 1, solid[(2 * size) + 2]);
        // A one-texel hole inside that silhouette is filled in by its neighbours alone, dark but
        // not saturated, which is the softening the spread exists for.
        for (int i = 0; i < covered.Length; i++)
            covered[i] = true;
        covered[(2 * size) + 2] = false;
        var hole = GroundShadowLaw.Spread(covered, size, size);
        Assert.Equal(8, hole[(2 * size) + 2]);
    }

    [Fact]
    public void TheBorderRingIsNeverScanned()
    {
        const int size = 5;
        var covered = new bool[size * size];
        covered[0] = true;
        var ramp = GroundShadowLaw.Spread(covered, size, size);
        // Marked but unscanned: it keeps its own cover mark and spreads into nothing.
        Assert.Equal(0, ramp[0]);
        Assert.Equal(0, ramp[(1 * size) + 1]);
    }

    [Fact]
    public void TheSpreadIntoACallersBufferIsTheSameSpread()
    {
        // The live raster reuses one buffer per aircraft rather than allocating a ramp per frame,
        // so the two overloads must not be able to drift apart.
        const int size = 6;
        var covered = new bool[size * size];
        covered[(2 * size) + 3] = true;
        covered[(3 * size) + 3] = true;
        var expected = GroundShadowLaw.Spread(covered, size, size);
        var into = new int[size * size];
        GroundShadowLaw.Spread(covered, size, size, into);
        Assert.Equal(expected, into);
    }

    [Fact]
    public void TheSofteningKeepsItsWidthOnTheGroundAtTheLiveStep()
    {
        // ⚠ The live texture is finer than the original's 32, so the spread is measured in world
        // terms: the edge must soften over the same fraction of the footprint at either step.
        float original = SofteningWidth(GroundShadowLaw.OriginalTextureSize, 1f);
        float live = SofteningWidth(GroundShadowLaw.TextureSize, GroundShadowLaw.TexelScale);
        Assert.Equal(original, live, 1f / GroundShadowLaw.OriginalTextureSize);
        // And the scale is what does it: the same spread taken in texels shrinks with the step.
        Assert.True(SofteningWidth(GroundShadowLaw.TextureSize, 1f) < original);
    }

    [Fact]
    public void TheRampRunsFromWhiteToTheShadowColour()
    {
        Assert.Equal(0f, GroundShadowLaw.Coverage(0), 4);
        Assert.Equal(1f, GroundShadowLaw.Coverage(GroundShadowLaw.RampSteps - 1), 4);
        Assert.Equal(0.5f, GroundShadowLaw.Coverage(5), 4);
    }

    // How much of a buffer's own edge-to-edge extent the spread takes to run a straight covered
    // edge from saturated to white, which is the softening's width as the ground sees it.
    private static float SofteningWidth(int size, float texelScale)
    {
        var covered = new bool[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size / 2; x++)
                covered[(y * size) + x] = true;
        }

        var ramp = GroundShadowLaw.Spread(covered, size, size, texelScale);
        int row = size / 2;
        int lastFull = -1;
        int firstEmpty = size;
        for (int x = 0; x < size; x++)
        {
            int step = ramp[(row * size) + x];
            if (step == GroundShadowLaw.RampSteps - 1)
                lastFull = x;
            else if (lastFull >= 0 && step == 0 && x < firstEmpty)
                firstEmpty = x;
        }

        return (firstEmpty - lastFull) / (float)size;
    }
}
