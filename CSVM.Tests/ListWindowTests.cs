using CSVM.UI.Boards;
using Xunit;

namespace CSVM.Tests;

/// <summary>A scrolled list as the pointer sees it: what its box and its thumb contain, where a
/// wheel step and a thumb drag put the window, and where a thumb stands for a given window. Every
/// list widget builds one of these, so the arithmetic is tested once here rather than per screen.
/// </summary>
public class ListWindowTests
{
    [Fact]
    public void AListShorterThanItsWindowNeitherScrollsNorCarriesAThumb()
    {
        var window = Window(count: 3, rows: 5, top: 0);

        Assert.False(window.Scrolls);
        Assert.Equal(0, window.LastTop);
        Assert.False(window.OnThumb(304f, 105f)); // on the thumb's box, but there is no thumb
    }

    [Fact]
    public void TheBoxAndTheThumbAnswerForTheirOwnHalfOpenRectangles()
    {
        var window = Window(count: 20, rows: 5, top: 0);

        Assert.True(window.Contains(100f, 100f));
        Assert.True(window.Contains(299f, 199f));
        Assert.False(window.Contains(300f, 100f)); // the right edge is past the box
        Assert.False(window.Contains(100f, 99f));
        Assert.True(window.OnThumb(304f, 115f));
        Assert.False(window.OnThumb(304f, 126f)); // one past the thumb's foot
    }

    [Fact]
    public void AWheelStepMovesTheWindowByRowsAndClampsAtBothEnds()
    {
        var window = Window(count: 20, rows: 5, top: 4);

        Assert.Equal(5, window.TopAfterWheel(1));
        Assert.Equal(1, window.TopAfterWheel(-3));
        Assert.Equal(0, window.TopAfterWheel(-40));
        Assert.Equal(15, window.TopAfterWheel(40)); // the last top shows the last row at the foot
    }

    [Fact]
    public void ADragMapsTheThumbsFreeRunOntoTheRowsTheWindowCanMove()
    {
        // A 100-pixel track under a 20-pixel thumb leaves 80 pixels of run for 15 rows.
        var window = Window(count: 20, rows: 5, top: 0);

        Assert.Equal(0, window.TopAfterDrag(0, 0f));
        Assert.Equal(8, window.TopAfterDrag(0, 40f)); // half the run, rounded to the nearer row
        Assert.Equal(15, window.TopAfterDrag(0, 80f));
        Assert.Equal(15, window.TopAfterDrag(0, 500f));
        Assert.Equal(2, window.TopAfterDrag(10, -40f)); // a drag reads from where it took hold
        Assert.Equal(0, window.TopAfterDrag(10, -500f));
    }

    [Fact]
    public void ADragOnAListWithNoRunToGiveLeavesTheWindowWhereItWas()
    {
        var stuck = new ListWindow(
            0f, 0f, 100f, 100f,
            0f, 0f, 10f, 100f,
            0f, 100f,
            20, 5, 3);

        Assert.Equal(3, stuck.TopAfterDrag(3, 50f)); // the thumb fills its whole track
        Assert.Equal(0, Window(count: 5, rows: 5, top: 0).TopAfterDrag(0, 50f));
    }

    [Fact]
    public void AThumbIsAsLongAsTheShareOfTheListItsWindowShows()
    {
        // Instant Action's contents window: 14 of 19 presets on a 230-pixel track, which is the
        // length the reference frame draws, its foot 169.5 pixels below the track's head.
        Assert.Equal(230f * 14f / 19f, ListWindow.ThumbHeightFor(230f, 14, 19, 11f), 3);
        Assert.Equal(115f, ListWindow.ThumbHeightFor(230f, 1, 2, 11f));
        Assert.Equal(11f, ListWindow.ThumbHeightFor(230f, 1, 500, 11f)); // the floor holds a grip
        Assert.Equal(230f, ListWindow.ThumbHeightFor(230f, 19, 19, 11f)); // nothing to scroll
        Assert.Equal(230f, ListWindow.ThumbHeightFor(230f, 40, 19, 11f)); // a window past the list
        Assert.Equal(8f, ListWindow.ThumbHeightFor(8f, 1, 500, 11f)); // a track shorter than the art
        Assert.Equal(11f, ListWindow.ThumbHeightFor(230f, 5, 0, 11f)); // a list with no rows to share
    }

    [Fact]
    public void AThumbStandsAtTheHeadUnscrolledAndFlushAtTheFootOnTheLastRow()
    {
        Assert.Equal(100f, ListWindow.ThumbYFor(100f, 100f, 20f, 0, 15));
        Assert.Equal(180f, ListWindow.ThumbYFor(100f, 100f, 20f, 15, 15));
        Assert.Equal(140f, ListWindow.ThumbYFor(100f, 100f, 20f, 7, 14));
        Assert.Equal(100f, ListWindow.ThumbYFor(100f, 100f, 20f, 0, 0)); // nothing to scroll
    }

    // A 200x100 box at (100, 100) with a 20-pixel thumb on a 100-pixel track down its right edge.
    private static ListWindow Window(int count, int rows, int top) =>
        new(
            100f, 100f, 200f, 100f,
            300f, ListWindow.ThumbYFor(100f, 100f, 20f, top, System.Math.Max(0, count - rows)), 10f, 20f,
            100f, 100f,
            count, rows, top);
}
