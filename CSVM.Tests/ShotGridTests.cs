using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>The Danger Zone photographs' grid rule and the cursor that walks the grid, shared by
/// the built-in boards' strip and the Original wrap-up page's prints.</summary>
public class ShotGridTests
{
    // The built-in strip's room at 720p: 640 by 300, a 6 gap, a 20 caption under each picture.
    private const float Width = 640f;
    private const float Height = 300f;
    private const float Gap = 6f;
    private const float Caption = 20f;
    private const float Thumb = 164f;
    private const float Wide = 9f / 16f;

    [Fact]
    public void AFewShotsStayOneRowAtTheThumbnailWidth()
    {
        var (columns, picture) = ShotGrid.Fit(3, Width, Height, Wide, Gap, 0f, Caption, Thumb);

        Assert.Equal(3, columns);
        Assert.Equal(Thumb, picture);
    }

    [Fact]
    public void ALongCourseWrapsIntoRowsRatherThanShrinkingOneStrip()
    {
        var (columns, picture) = ShotGrid.Fit(14, Width, Height, Wide, Gap, 0f, Caption, Thumb);

        // Five across in three rows, (640 - 4 * 6) / 5 = 123.2 wide; one strip would be 40.
        Assert.Equal(5, columns);
        Assert.Equal(123.2f, picture, 0.01f);
    }

    [Fact]
    public void FewerRowsWinOnlyWithinTheSlack()
    {
        // Seven across in two rows is 86.3 wide, under 0.85 of the five-column 123.2, so the
        // extra row is taken.
        var (twoRows, _) = ShotGrid.Fit(14, Width, Height, Wide, Gap, 0f, Caption, 400f);
        Assert.NotEqual(7, twoRows);

        // Among grids of the same rows the wider picture wins: five and six columns both need
        // three rows for fourteen.
        var (columns, _) = ShotGrid.Fit(14, Width, Height, Wide, Gap, 0f, Caption, Thumb);
        Assert.Equal(5, columns);
    }

    [Fact]
    public void NoShotsOrNoRoomAnswersOneEmptyColumn()
    {
        Assert.Equal((1, 0f), ShotGrid.Fit(0, Width, Height, Wide, Gap, 0f, Caption, Thumb));
        Assert.Equal((1, 0f), ShotGrid.Fit(4, Width, Height, 0f, Gap, 0f, Caption, Thumb));
        Assert.Equal(0f, ShotGrid.Fit(4, 10f, 10f, Wide, Gap, 6f, Caption, Thumb).Picture);
    }

    [Fact]
    public void EnteringFromBelowTakesTheLastRowsLeftmostCell()
    {
        var cursor = Grid(out _);

        Assert.False(cursor.OnGrid);
        Assert.True(cursor.Enter());
        Assert.Equal(6, cursor.Cell);
    }

    [Fact]
    public void APendingCellRefusesTheCursorAndIsSteppedOver()
    {
        var cursor = Grid(out _);

        Assert.False(cursor.MoveTo(5));
        Assert.False(cursor.OnGrid);
        Assert.True(cursor.MoveTo(4));
        Assert.Equal(ShotGridStep.Moved, cursor.Step(1, 0));
        Assert.Equal(6, cursor.Cell);
    }

    [Fact]
    public void VerticalStepsTakeTheNearestCellByColumn()
    {
        var cursor = Grid(out _);
        cursor.MoveTo(4);

        // Row two holds only cell 6, in column 0.
        Assert.Equal(ShotGridStep.Moved, cursor.Step(0, 1));
        Assert.Equal(6, cursor.Cell);
        Assert.Equal(ShotGridStep.Moved, cursor.Step(0, -1));
        Assert.Equal(3, cursor.Cell);
        Assert.Equal(ShotGridStep.Moved, cursor.Step(0, -1));
        Assert.Equal(0, cursor.Cell);
    }

    [Fact]
    public void DownPastTheLastRowLeavesTheGridAndUpPastTheTopStays()
    {
        var cursor = Grid(out _);
        cursor.MoveTo(1);
        Assert.Equal(ShotGridStep.Stayed, cursor.Step(0, -1));
        Assert.Equal(1, cursor.Cell);
        Assert.Equal(ShotGridStep.Moved, cursor.Step(-1, 0));
        Assert.Equal(0, cursor.Cell);
        Assert.Equal(ShotGridStep.Stayed, cursor.Step(-1, 0));
        Assert.Equal(0, cursor.Cell);

        cursor.MoveTo(6);
        Assert.Equal(ShotGridStep.Left, cursor.Step(0, 1));
        Assert.Equal(-1, cursor.Cell);
    }

    [Fact]
    public void ALayoutThatNoLongerTakesTheCellDropsTheCursor()
    {
        var cursor = Grid(out var selectable);
        cursor.MoveTo(3);
        selectable[3] = false;
        cursor.Layout(7, 3, i => selectable[i]);

        Assert.False(cursor.OnGrid);
    }

    [Fact]
    public void AGridWithNothingLandedCannotBeEntered()
    {
        var cursor = new ShotGridCursor();
        cursor.Layout(4, 4, _ => false);

        Assert.False(cursor.AnySelectable);
        Assert.False(cursor.Enter());
        Assert.False(cursor.OnGrid);
    }

    // Seven cells three across, the sixth (row 1, column 2) still pending.
    private static ShotGridCursor Grid(out bool[] selectable)
    {
        var cells = new[] { true, true, true, true, true, false, true };
        selectable = cells;
        var cursor = new ShotGridCursor();
        cursor.Layout(7, 3, i => cells[i]);
        return cursor;
    }
}
