using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The tracer pixel floor's screen-space math (<see cref="ScreenSize.MinWorldSizeForPixels"/>) and
/// the splitscreen rule built on it: flooring against the nearest viewer, not player 1's pane, per
/// docs/org/tracers.md's splitscreen-bug section. Every expectation is computed by hand from the
/// projection, never captured from the implementation. The round-trip cases assert the inverse
/// actually inverts.
/// </summary>
public class TracerScreenSizeTests
{
    // A 70 degree vertical FOV and a 720-line viewport; tan(35 deg) = 0.70020753...
    private const float Fov = 70f;
    private const float FullHeight = 720f;
    private const float Tan35 = 0.7002075f;

    /// <summary>The inverse inverts: the size it returns, pushed back through the forward
    /// projection, lands on exactly the pixel target that was asked for.</summary>
    [Theory]
    [InlineData(2f, 50f)]
    [InlineData(2f, 1000f)]
    [InlineData(8f, 250f)]
    public void TheReturnedSizeProjectsBackToExactlyThePixelsAskedFor(float pixels, float distance)
    {
        float size = ScreenSize.MinWorldSizeForPixels(pixels, distance, Fov, FullHeight);
        float projectedPx = size * FullHeight / (2f * distance * Tan35);
        Assert.Equal(pixels, projectedPx, 3);
    }

    /// <summary>The floor grows linearly with distance, the property that makes a single shared
    /// mesh unable to satisfy two cameras at once, and so the reason the nearest one must win.</summary>
    [Fact]
    public void TheFloorIsLinearInDistance()
    {
        float near = ScreenSize.MinWorldSizeForPixels(2f, 100f, Fov, FullHeight);
        float far = ScreenSize.MinWorldSizeForPixels(2f, 1000f, Fov, FullHeight);
        Assert.Equal(10f, far / near, 4);
        // And by hand: 2 * 2 * 1000 * tan(35) / 720.
        Assert.Equal(2f * 2f * 1000f * Tan35 / 720f, far, 5);
    }

    /// <summary>A splitscreen pane is shorter than the window, so a camera measured against the
    /// WINDOW height is under-floored by the pane factor, the second half of the same bug. Halving
    /// the viewport height doubles the world size needed to cover the same pixels.</summary>
    [Fact]
    public void APaneHalfTheWindowHeightNeedsTwiceTheWorldSizeForTheSamePixels()
    {
        float window = ScreenSize.MinWorldSizeForPixels(2f, 500f, Fov, FullHeight);
        float pane = ScreenSize.MinWorldSizeForPixels(2f, 500f, Fov, FullHeight / 2f);
        Assert.Equal(2f, pane / window, 4);
    }

    /// <summary>The splitscreen rule: with several viewers the floor is the SMALLEST of their
    /// individual floors, i.e. the nearest camera's. The worked case: a round 1000 m from P1 and
    /// 100 m from P2. Sizing against P1, the alternative this discriminates against, makes the
    /// mesh ~10x larger than P2's own pane needs, which reads as "P2's tracers are huge"; taking the
    /// minimum sizes it for P2 and leaves it merely under-floored in P1's distant view.</summary>
    [Fact]
    public void NearerViewerAlwaysWinsSoNoPaneIsEverInflated()
    {
        var viewers = new[]
        {
            new ScreenSize.ViewerSample(1000f, Fov, FullHeight), // P1, far from the round
            new ScreenSize.ViewerSample(100f, Fov, FullHeight),  // P2, close to it
        };
        float chosen = ScreenSize.NearestFloor(2f, viewers);
        Assert.Equal(ScreenSize.MinWorldSizeForPixels(2f, 100f, Fov, FullHeight), chosen, 6);
        // Order must not matter, the rule is "nearest", not "first" or "last".
        Assert.Equal(chosen, ScreenSize.NearestFloor(2f, new[] { viewers[1], viewers[0] }), 6);
        // The inflation a farthest-viewer rule produces, stated as a number so a regression is legible:
        // sized for P1, the mesh covers ten times its 2 px target in P2's pane.
        float forP1 = ScreenSize.MinWorldSizeForPixels(2f, 1000f, Fov, FullHeight);
        float pxInP2sPaneIfSizedForP1 = forP1 * FullHeight / (2f * 100f * Tan35);
        Assert.Equal(20f, pxInP2sPaneIfSizedForP1, 3);
        // ...and the chosen size does meet, not exceed, the target in the pane it was sized for.
        Assert.Equal(2f, chosen * FullHeight / (2f * 100f * Tan35), 3);
    }

    /// <summary>A viewer the projection cannot use, sitting exactly on the round, or with no
    /// viewport, is skipped rather than collapsing the floor to zero for everyone. With one live
    /// viewer left, that one decides.</summary>
    [Fact]
    public void DegenerateViewersAreSkippedNotTreatedAsTheNearest()
    {
        var viewers = new[]
        {
            new ScreenSize.ViewerSample(0f, Fov, FullHeight),   // camera on top of the round
            new ScreenSize.ViewerSample(400f, Fov, 0f),         // pane with no height (headless)
            new ScreenSize.ViewerSample(250f, Fov, FullHeight), // the only usable one
        };
        Assert.Equal(ScreenSize.MinWorldSizeForPixels(2f, 250f, Fov, FullHeight), ScreenSize.NearestFloor(2f, viewers), 6);
    }

    /// <summary>No viewers bound at all (the weapon lab, the headless dumps) means no floor,
    /// the pool must fall through to the authored sizes, not to zero-size or infinite geometry.</summary>
    [Fact]
    public void NoViewersMeansNoFloor()
    {
        Assert.Equal(0f, ScreenSize.NearestFloor(2f, System.Array.Empty<ScreenSize.ViewerSample>()));
    }

    /// <summary>Each viewer is measured with its OWN pane height: a nearer camera in a short pane
    /// can legitimately need a larger world size than a farther one in a tall pane, and the rule
    /// must follow the arithmetic rather than the raw distance ordering.</summary>
    [Fact]
    public void ThePaneHeightIsPerViewerNotShared()
    {
        var viewers = new[]
        {
            new ScreenSize.ViewerSample(300f, Fov, FullHeight),      // far, tall pane  -> smaller floor
            new ScreenSize.ViewerSample(200f, Fov, FullHeight / 4f), // near, short pane -> larger floor
        };
        // 300/720 = 0.4167 vs 200/180 = 1.111 per unit: the FARTHER viewer wins on size here.
        Assert.Equal(ScreenSize.MinWorldSizeForPixels(2f, 300f, Fov, FullHeight), ScreenSize.NearestFloor(2f, viewers), 6);
    }

    /// <summary>Degenerate inputs read as "no floor", not as a size: a camera exactly on the round,
    /// a zero pixel target (the documented way to disable the floor) and a viewport with no height
    /// (a headless probe) each return 0 rather than an infinity or a NaN.</summary>
    [Theory]
    [InlineData(2f, 0f, FullHeight)]
    [InlineData(0f, 500f, FullHeight)]
    [InlineData(2f, 500f, 0f)]
    public void DegenerateInputsReturnNoFloorRatherThanASize(float pixels, float distance, float height)
    {
        Assert.Equal(0f, ScreenSize.MinWorldSizeForPixels(pixels, distance, Fov, height));
    }
}
