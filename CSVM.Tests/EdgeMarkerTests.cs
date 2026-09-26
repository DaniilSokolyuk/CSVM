using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The off-screen edge marker's placement rules (<see cref="EdgeMarker"/>), shared by
/// VersusHud and TargetHud: the whole-pane on-screen test, the behind-the-camera mirror, the
/// degenerate-direction fallback, the anchor's clamp to the 5 percent inset boundary, the tip's
/// clamp to the pane, and the clock-hour bearing. These pin the semantics the HUD goldens witness
/// end-to-end, including <c>Rect2.HasPoint</c>'s inclusive-at-position / exclusive-at-end
/// asymmetry, which is today's behaviour and not a bug to fix here.
/// </summary>
public class EdgeMarkerTests
{
    // The 5 percent of each axis Resolve insets the anchor by, on the pane below.
    private const float InsetX = 50f;
    private const float InsetY = 40f;

    private static readonly Vector2 Pane = new(1000f, 800f);

    [Fact]
    public void APointOnThePaneStaysOnScreenUntouched()
    {
        var placed = EdgeMarker.Resolve(new Vector2(300f, 500f), behind: false, Pane);
        Assert.True(placed.OnScreen);
        Assert.Equal(new Vector2(300f, 500f), placed.Anchor);
        Assert.Equal(new Vector2(300f, 500f), placed.Tip);
        Assert.Equal(Vector2.Zero, placed.Dir);
    }

    [Fact]
    public void APointInsideTheInsetBandIsStillOnScreen()
    {
        // The band between the inset boundary and the pane edge: the original's off-screen flag is
        // the whole viewport, so a target in there is on screen and gets no arrow, tag or disc.
        // Both axes, both ends, including the pixel inside each far edge.
        foreach (var band in new[]
        {
            new Vector2(InsetX - 1f, 400f), new Vector2(Pane.X - InsetX + 1f, 400f),
            new Vector2(500f, InsetY - 1f), new Vector2(500f, Pane.Y - InsetY + 1f),
            new Vector2(0f, 0f), new Vector2(Pane.X - 1f, Pane.Y - 1f),
        })
        {
            var placed = EdgeMarker.Resolve(band, behind: false, Pane);
            Assert.True(placed.OnScreen, $"{band} is inside the pane and must read on screen");
            Assert.Equal(band, placed.Anchor);
        }
    }

    [Fact]
    public void ThePaneBoundaryIsInclusiveAtTopLeftAndExclusiveAtBottomRight()
    {
        Assert.True(EdgeMarker.Resolve(Vector2.Zero, false, Pane).OnScreen);
        Assert.False(EdgeMarker.Resolve(new Vector2(Pane.X, 400f), false, Pane).OnScreen);
        Assert.False(EdgeMarker.Resolve(new Vector2(500f, Pane.Y), false, Pane).OnScreen);
        Assert.False(EdgeMarker.Resolve(new Vector2(-1f, 400f), false, Pane).OnScreen);
    }

    [Fact]
    public void BehindForcesOffScreenAndMirrorsTheDirectionThroughCentre()
    {
        // (700, 400) is comfortably on the pane, but a point behind the camera
        // unprojects mirrored, so the marker goes to the edge and the direction flips back:
        // the target reads as LEFT of centre, not right.
        var placed = EdgeMarker.Resolve(new Vector2(700f, 400f), behind: true, Pane);
        Assert.False(placed.OnScreen);
        Assert.Equal(new Vector2(-1f, 0f), placed.Dir);
        Assert.Equal(new Vector2(InsetX, 400f), placed.Anchor);
        Assert.Equal(new Vector2(0f, 400f), placed.Tip);
    }

    [Fact]
    public void AProjectionOnCentreFallsBackToPointingDown()
    {
        // Dead astern: the mirrored projection lands on centre, so there is no direction to
        // normalize, the fallback points down, and nothing is NaN.
        var placed = EdgeMarker.Resolve(Pane / 2f, behind: true, Pane);
        Assert.False(placed.OnScreen);
        Assert.Equal(Vector2.Down, placed.Dir);
        Assert.Equal(new Vector2(Pane.X / 2f, Pane.Y - InsetY), placed.Anchor);
        Assert.True(placed.Tip.IsEqualApprox(new Vector2(Pane.X / 2f, Pane.Y - 1.001f)),
            $"tip {placed.Tip} is not on the pane's own bottom edge");
    }

    [Fact]
    public void TheAnchorLandsOnTheInsetBoundaryAlongTheDirection()
    {
        // Cardinal: far off the right edge clamps to the inset boundary at the same height, and
        // the tip goes on out to the pane's own edge, which is what the shaft spans.
        var right = EdgeMarker.Resolve(new Vector2(5000f, 400f), false, Pane);
        Assert.Equal(new Vector2(Pane.X - InsetX, 400f), right.Anchor);
        Assert.Equal(new Vector2(1f, 0f), right.Dir);
        Assert.True(right.Tip.IsEqualApprox(new Vector2(Pane.X - 1.001f, 400f)),
            $"tip {right.Tip} is not on the pane's own right edge");

        // Diagonal on a square pane: both half-extents bind at once, so the min(tx, ty) rule
        // puts the anchor exactly on the inset corner and the direction stays unit length.
        var pane = new Vector2(800f, 800f);
        var corner = EdgeMarker.Resolve(new Vector2(1200f, 1200f), false, pane);
        Assert.False(corner.OnScreen);
        Assert.True(corner.Anchor.IsEqualApprox(new Vector2(760f, 760f)),
            $"anchor {corner.Anchor} is not the inset corner (760, 760)");
        Assert.True(Mathf.IsEqualApprox(corner.Dir.Length(), 1f));
    }

    [Fact]
    public void ATargetJustPastTheEdgeAlreadySpansTheWholeInsetBand()
    {
        // The first pixel outside the pane is off screen, and its shaft already spans the band,
        // anchor on the inset boundary and tip on the pane's own edge. A target further out moves
        // neither end, so the shaft's length is the bearing's and not the range's.
        var justOut = EdgeMarker.Resolve(new Vector2(Pane.X, 400f), false, Pane);
        var farOut = EdgeMarker.Resolve(new Vector2(5000f, 400f), false, Pane);
        Assert.False(justOut.OnScreen);
        Assert.Equal(new Vector2(Pane.X - InsetX, 400f), justOut.Anchor);
        Assert.True(justOut.Tip.IsEqualApprox(new Vector2(Pane.X - 1.001f, 400f)),
            $"tip {justOut.Tip} is not on the pane's own right edge");
        Assert.Equal(farOut.Anchor, justOut.Anchor);
        Assert.Equal(farOut.Tip, justOut.Tip);
    }

    [Fact]
    public void TheAnchorInsetMovesTheAnchorWithoutMovingTheOnScreenTest()
    {
        // The spyglass's half window: the anchor comes in by that much on the bearing, while the
        // tip and the direction are untouched, and a point the bare rule calls on screen is still
        // on screen (the off-screen flag is the viewport, neither inset rectangle).
        var bare = EdgeMarker.Resolve(new Vector2(5000f, 400f), false, Pane);
        var inset = EdgeMarker.Resolve(new Vector2(5000f, 400f), false, Pane, 48f);
        Assert.Equal(new Vector2(bare.Anchor.X - 48f, 400f), inset.Anchor);
        Assert.Equal(bare.Tip, inset.Tip);
        Assert.Equal(bare.Dir, inset.Dir);
        Assert.True(EdgeMarker.Resolve(new Vector2(0.06f * Pane.X, 400f), false, Pane, 48f).OnScreen);
    }

    [Fact]
    public void AnAnchorInsetWiderThanThePaneCollapsesToTheCentre()
    {
        // A disc bigger than the pane it is drawn on must not invert the anchor rectangle; it
        // collapses to the middle, so the picture stays on screen instead of flipping sides.
        var placed = EdgeMarker.Resolve(new Vector2(5000f, 5000f), false, Pane, 5000f);
        Assert.False(placed.OnScreen);
        Assert.True(placed.Anchor.IsEqualApprox(Pane / 2f),
            $"anchor {placed.Anchor} is not the pane centre");
    }

    [Fact]
    public void ClockHourReadsTheFourCardinalBearings()
    {
        var own = Vector3.Zero;
        Assert.Equal(12, EdgeMarker.ClockHour(own, 0f, new Vector3(0f, 0f, -100f)));  // north, ahead
        Assert.Equal(3, EdgeMarker.ClockHour(own, 0f, new Vector3(100f, 0f, 0f)));    // east, right
        Assert.Equal(6, EdgeMarker.ClockHour(own, 0f, new Vector3(0f, 0f, 100f)));    // south, behind
        Assert.Equal(9, EdgeMarker.ClockHour(own, 0f, new Vector3(-100f, 0f, 0f)));   // west, left
    }

    [Fact]
    public void ClockHourIsRelativeToTheHeadingAndWrapsBackTo12()
    {
        var own = Vector3.Zero;
        var north = new Vector3(0f, 0f, -100f);
        // Facing east, a target due north sits at 9 o'clock.
        Assert.Equal(9, EdgeMarker.ClockHour(own, 90f, north));
        // 16° right of the nose rounds up to 1 o'clock; 14° rounds back down to dead ahead,
        // and a bearing just LEFT of the nose (rel 350°) wraps through h == 0 to 12, never 0.
        Assert.Equal(1, EdgeMarker.ClockHour(own, -16f, north));
        Assert.Equal(12, EdgeMarker.ClockHour(own, -14f, north));
        Assert.Equal(12, EdgeMarker.ClockHour(own, 10f, north));
    }
}
