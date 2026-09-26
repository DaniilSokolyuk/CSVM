using System.Linq;
using CSVM.Flight.Modes;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>The Original presentation's Instant Action wrap-up page, read off one ended mission's
/// snapshot. It checks the heading, the four decoded rows against <c>[@IA_WrapUp@]</c>'s own
/// geometry, and the further lines the built-in board carries
/// (<c>docs/formats/instant-action/wrap-up.md</c>).</summary>
public class InstantActionWrapupPageTests
{
    // A won stunt flight: 3:06 flown, four kills, three zones, 27% of the rounds on target.
    private static readonly IaWrapupSnapshot Won =
        new(true, "C1   .   Stunt Flying", 186f, 4, 3, 27, new[] { "1.  Pier    12.4    12.4", "TOTAL   12.4" });

    private static readonly IaWrapupSnapshot Lost = new(false, "C3   .   Dogfight", 74.5f, 1, 0, 8);

    [Fact]
    public void RowsCarryTheHeadingAndTheFourDecodedValues()
    {
        var rows = InstantActionWrapupPage.Rows(Won);

        Assert.Equal(9, rows.Count);
        Assert.Equal("Instant Action", rows[0].Text);
        Assert.Contains(rows, l => l.Text == "Time to Complete Mission");
        Assert.Contains(rows, l => l.Text == "03:06");
        Assert.Contains(rows, l => l.Text == "Enemies Shot Down");
        Assert.Contains(rows, l => l.Text == "4");
        Assert.Contains(rows, l => l.Text == "Danger Zones Completed");
        Assert.Contains(rows, l => l.Text == "3");
        Assert.Contains(rows, l => l.Text == "Shot %");
        Assert.Contains(rows, l => l.Text == "27%");
    }

    [Fact]
    public void RowsStandAtTheAuthoredColumns()
    {
        var rows = InstantActionWrapupPage.Rows(Won);

        Assert.Equal((467f, 98f), (rows[0].X, rows[0].Y));
        Assert.Equal((480f, 154f), (rows[1].X, rows[1].Y));
        Assert.Equal((533f, 185f), (rows[2].X, rows[2].Y));
        Assert.Equal((533f, 369f), (rows[8].X, rows[8].Y));
    }

    [Fact]
    public void RowsAreNeverRecomputedFromTheLiveRun()
    {
        // A losing run's numbers are the snapshot's own, down to the truncated clock and a shot
        // percentage the page never divides for itself.
        var rows = InstantActionWrapupPage.Rows(Lost);

        Assert.Contains(rows, l => l.Text == "01:14");
        Assert.Contains(rows, l => l.Text == "1");
        Assert.Contains(rows, l => l.Text == "0");
        Assert.Contains(rows, l => l.Text == "8%");
    }

    [Fact]
    public void ExtraLinesCarryTheContextAndTheSplitsAndNoSecondTime()
    {
        var won = InstantActionWrapupPage.ExtraLines(Won);
        var lost = InstantActionWrapupPage.ExtraLines(Lost);

        // The splits' total is the time row's figure again, and the outcome is the tick box's.
        Assert.Equal(new[] { "C1   .   Stunt Flying", "1.  Pier    12.4    12.4" }, won);
        Assert.Equal(new[] { "C3   .   Dogfight" }, lost);
        Assert.DoesNotContain(won, l => l.Contains("Time to Complete") || l.Contains("03:06"));
        Assert.DoesNotContain(won, l => l.StartsWith("TOTAL", System.StringComparison.Ordinal));
        Assert.DoesNotContain(won, l => l.Contains("MISSION"));
    }

    [Fact]
    public void ExtraLinesKeepEverySplitAndTheBestLine()
    {
        var run = InstantActionWrapupPage.LongSample();
        var lines = InstantActionWrapupPage.ExtraLines(run);

        // The context, seventeen splits and the best line; only the total drops out.
        Assert.Equal(19, lines.Count);
        Assert.Equal(17, lines.Count(l => char.IsDigit(l[0])));
        Assert.StartsWith("NEW BEST", lines[^1], System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(19, 3)]
    [InlineData(200, 3)]
    public void PostItsTakeOnePerCapacityUpToThePageEdge(int lines, int expected)
    {
        Assert.Equal(9, InstantActionWrapupPage.PostItCapacity);
        Assert.Equal(expected, InstantActionWrapupPage.PostItCount(lines));
    }

    [Fact]
    public void TheLongestStuntRunSharesItsLinesOverThreePostIts()
    {
        var run = InstantActionWrapupPage.LongSample();
        var postIts = InstantActionWrapupPage.PostIts(run);
        var rows = InstantActionWrapupPage.Rows(run);
        var (plaqueX, _) = InstantActionWrapupPage.ContinueAt();

        Assert.Equal(3, postIts.Count);
        Assert.Equal(new[] { 7, 7, 5 }, postIts.Select(p => p.Lines.Count));
        Assert.Equal(InstantActionWrapupPage.ExtraLines(run).Count, postIts.Sum(p => p.Lines.Count));
        Assert.All(postIts, p => Assert.True(p.Height <= 168f && p.X >= 0f, $"{p}"));
        Assert.True(postIts[0].Y > rows[8].Y, "the first post-it opens below the last value row");
        Assert.True(postIts[0].X + postIts[0].Width <= plaqueX, "and stops clear of the CONTINUE plaque");
        Assert.True(postIts[1].X + postIts[1].Width <= postIts[0].X, "further post-its stand to its left");
        Assert.StartsWith("1.  Police Tower  ", postIts[0].Lines[1], System.StringComparison.Ordinal);
    }

    [Fact]
    public void APostItIsOnlyAsTallAsWhatItHolds()
    {
        var one = InstantActionWrapupPage.PostIts(Lost).Single();
        var seven = InstantActionWrapupPage.PostIts(InstantActionWrapupPage.LongSample())[0];

        Assert.Single(one.Lines);
        Assert.Equal(7, seven.Lines.Count);
        Assert.True(one.Height < seven.Height, $"{one.Height} against {seven.Height}");
        Assert.True(one.Height >= one.Width / 2f, "a single line still reads as a note, not a strip");
        Assert.Equal(3, InstantActionWrapupPage.PostItPaper(one).Count);
        Assert.True(InstantActionWrapupPage.PostItNote(seven).Shrink);
    }

    [Fact]
    public void TheTickBoxStandsAboveContinueAndIsTickedOnlyOnAWin()
    {
        var (plaqueX, plaqueY) = InstantActionWrapupPage.ContinueAt();
        var (x, y, side) = InstantActionWrapupPage.TickBox();
        var won = InstantActionWrapupPage.TickStrokes(won: true);
        var lost = InstantActionWrapupPage.TickStrokes(won: false);

        Assert.True(y + side < plaqueY, "the box stands above the plaque");
        Assert.True(x > plaqueX && x + side < plaqueX + 112f, "over the plaque's own width");
        Assert.Equal(8, lost.Count);
        Assert.True(won.Count > lost.Count, "a win adds the tick");
        Assert.Equal(lost, won.Take(lost.Count));
    }

    [Fact]
    public void PicturesAreTheSpreadAndTheFourBrushstrokes()
    {
        var art = InstantActionWrapupPage.Pictures();

        Assert.Equal(5, art.Count);
        Assert.Equal("IA_StatScreenBackground.jpg", art[0].Art.Name);
        Assert.All(art.Skip(1), p => Assert.Equal("IA_StatScreen_Brushstroke.png", p.Art.Name));
        Assert.Equal(new[] { 173f, 226f, 297f, 359f }, art.Skip(1).Select(p => p.Y));
    }

    [Fact]
    public void ARunWithNoPhotographsDrawsNoPrints()
    {
        Assert.Empty(InstantActionWrapupPage.Prints(Won));
        Assert.Empty(InstantActionWrapupPage.Prints(Won with { Shots = System.Array.Empty<StuntShot>() }));
    }

    [Fact]
    public void PrintsStandInMarkerOrderBesideThePostItsUnderTheRows()
    {
        var run = Won with { Shots = Shots(3) };
        var prints = InstantActionWrapupPage.Prints(run);
        var postIt = InstantActionWrapupPage.PostIts(run).Single();
        var rows = InstantActionWrapupPage.Rows(run);

        Assert.Equal(new[] { "dz1", "dz2", "dz3" }, prints.Select(p => p.Shot.DzName));
        Assert.All(prints, p =>
        {
            Assert.True(p.X >= 8f && p.X + p.Width < postIt.X, $"beside the post-it, on the page: {p}");
            Assert.True(p.Y > rows[8].Y && p.Y + p.Height <= 592f, $"in the band under the rows: {p}");
        });
        Assert.Equal(prints.Select(p => p.X).OrderBy(x => x), prints.Select(p => p.X));
        Assert.All(prints, p => Assert.True(p.Width - 6f <= StuntCapture.ThumbWidth, $"{p.Width}"));

        // Three shots in a band this wide read as one strip, level with the post-it's top.
        Assert.All(prints, p => Assert.Equal(postIt.Y, p.Y));
    }

    [Fact]
    public void ALongRunsPrintsShrinkIntoTheRoomLeftOfItsLastPostIt()
    {
        var run = InstantActionWrapupPage.LongSample() with { Shots = Shots(17) };
        var prints = InstantActionWrapupPage.Prints(run);
        var last = InstantActionWrapupPage.PostIts(run)[^1];

        Assert.Equal(17, prints.Count);
        Assert.All(prints, p => Assert.True(p.X >= 8f && p.X + p.Width < last.X && p.Y + p.Height <= 592f, $"{p}"));
        Assert.Equal(Enumerable.Range(1, 17).Select(i => $"dz{i}"), prints.Select(p => p.Shot.DzName));
    }

    [Fact]
    public void APendingShotKeepsAnEmptyPrintAndAFailedOneIsLeftOut()
    {
        var shots = Shots(3);
        shots[1].Landed = true;  // landed with no thumbnail: the pane never arrived
        var prints = InstantActionWrapupPage.Prints(Won with { Shots = shots });

        Assert.Equal(new[] { "dz1", "dz3" }, prints.Select(p => p.Shot.DzName));
        Assert.All(prints, p => Assert.Null(InstantActionWrapupPage.PrintPicture(p)));
        Assert.Equal(2, InstantActionWrapupPage.PrintPaper(prints[0]).Count);
    }

    [Fact]
    public void TheDecodedLayoutMovesEveryRowWithIt()
    {
        // The fixture section authors its own positions, so a page that read them rather than the
        // built-in fallbacks answers the fixture's plaque corner.
        var layout = CampaignLayout.Over(MenuLayoutReaderTests.OriginalLayout());

        var rows = InstantActionWrapupPage.Rows(Won, layout);
        var (x, y) = InstantActionWrapupPage.ContinueAt(layout);

        Assert.Equal("Instant Action", rows[0].Text);
        Assert.Equal((600f, 449f), (x, y));
        Assert.Equal("PM_B_Continue.png", InstantActionWrapupPage.ContinueArt(layout).Name);
        var first = InstantActionWrapupPage.PostIts(Won, layout)[0];
        Assert.True(first.X + first.Width <= x, "the first post-it still clears the moved plaque");
        Assert.True(InstantActionWrapupPage.TickBox(layout).X > x, "and the tick box follows it");
    }

    // Shots still on their way, which is all a test without the engine can make: a thumbnail is an
    // engine image.
    private static StuntShot[] Shots(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new StuntShot { DzName = $"dz{i}", At = i, Path = $"C1_dz{i}.png" })
            .ToArray();
}
