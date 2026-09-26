using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Where the flight HUD's two dial columns land, the one placement that differs by pane shape. A
/// pane at the reference aspect or wider holds them exactly where HUD.png measured them, off the
/// reading box's edges; a narrower one, a 4:3 screen or a side-by-side splitscreen pane, has no
/// room for those margins and brings both columns to the border. Pure statics over a reading box
/// and a scale, so the arithmetic is asserted without a live Control.
/// </summary>
public class GaugeColumnPlacementTests
{
    [Fact]
    public void A4By3PaneBringsBothColumnsToTheBorder()
    {
        // 1024x768 is the picker's 4:3 size. The reference margins scale to 182 and 179 px of a
        // 1024-wide screen, which reads as pulled inward; all but the minimum is given back.
        var (leftEdge, rightEdge) = ColumnEdges(new Vector2(1024f, 768f));
        float minimum = HudMetrics.MinimumColumnMargin * (768f / HudMetrics.ReferenceHeight);
        Assert.Equal(8.533f, leftEdge, 3);
        Assert.Equal(1024f - 8.533f, rightEdge, 3);
        Assert.Equal(minimum, leftEdge, 3);
        Assert.Equal(1024f - minimum, rightEdge, 3);

        // A 2-player side-by-side pane is far narrower still, and the same rule holds it on the
        // pane's own border rather than a third of the way across it.
        var (paneLeft, paneRight) = ColumnEdges(new Vector2(640f, 720f));
        Assert.Equal(8f, paneLeft, 3);
        Assert.Equal(632f, paneRight, 3);
    }

    [Fact]
    public void SixteenNineAndWiderPanesKeepTheMeasuredPlacement()
    {
        // The golden size: the dials stand where HUD.png put them, 340.5 and 335 reference pixels
        // in, so no pinned shot moves.
        var (leftEdge, rightEdge) = ColumnEdges(new Vector2(1280f, 720f));
        Assert.Equal(170.25f, leftEdge, 3);
        Assert.Equal(1280f - 167.5f, rightEdge, 3);

        // The 4P grid's own pane, and the fraction-narrow one its 2 px gutters leave: a nominally
        // 16:9 pane is not a narrow screen, so a splitscreen shot does not move either.
        Assert.Equal(0f, HudMetrics.ColumnOutdent(new Vector2(640f, 360f), GaugeCluster.LeftColumnMargin), 3);
        Assert.Equal(0f, HudMetrics.ColumnOutdent(new Vector2(639f, 359f), GaugeCluster.RightColumnMargin), 3);

        // An ultrawide pane: the reading box is the reference aspect itself, so the columns keep
        // their margins off ITS edges and stay a reading width apart.
        var box = HudMetrics.ReadingBox(new Vector2(5120f, 1440f));
        var (left, right) = GaugeCluster.ColumnAnchors(box, 1f);
        Assert.Equal(box.Position.X, left, 3);
        Assert.Equal(box.End.X, right, 3);
    }

    [Fact]
    public void AColumnIsNeverPulledInwardAndAnUnsizedPaneDoesNotMoveOne()
    {
        // A margin already at or under the minimum has nothing to give back: the outdent floors at
        // zero rather than going negative and walking that column off the border inward.
        var narrow = new Vector2(1024f, 768f);
        Assert.Equal(0f, HudMetrics.ColumnOutdent(narrow, HudMetrics.MinimumColumnMargin), 3);
        Assert.Equal(0f, HudMetrics.ColumnOutdent(narrow, 0f), 3);
        Assert.Equal(4f, HudMetrics.ColumnOutdent(narrow, HudMetrics.MinimumColumnMargin + 4f), 3);

        // A pane the tree has not sized yet: no movement, matching ReadingBox's own zero case.
        Assert.Equal(0f, HudMetrics.ColumnOutdent(Vector2.Zero, GaugeCluster.LeftColumnMargin), 3);
    }

    // The screen x of each column's outer edge on a pane of this size: what "hugs the border"
    // means, measured from the pane's own left edge and right edge.
    private static (float Left, float Right) ColumnEdges(Vector2 paneSize)
    {
        var box = HudMetrics.ReadingBox(paneSize);
        float scale = paneSize.Y / HudMetrics.ReferenceHeight;
        var (left, right) = GaugeCluster.ColumnAnchors(box, scale);
        return (left + (GaugeCluster.LeftColumnMargin * scale),
            right - (GaugeCluster.RightColumnMargin * scale));
    }
}
