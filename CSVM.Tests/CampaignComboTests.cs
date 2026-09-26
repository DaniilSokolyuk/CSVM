using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The campaign screens' drop-down list: opening and closing, the cursor wrapping inside
/// an open list, the window scrolling only at its edges, and the rule that the widget never moves
/// its own pick, which is what lets a screen refuse one.</summary>
public class CampaignComboTests
{
    [Fact]
    public void AFreshComboShowsItsSelectedEntryAndIsClosed()
    {
        var combo = Loaded(4, selected: 2);

        Assert.False(combo.Open);
        Assert.Equal("Plane 2", combo.Text);
        Assert.Equal(2, combo.Highlight);
    }

    [Fact]
    public void ExpandingOpensOnThePickAndCollapsingLeavesItAlone()
    {
        var combo = Loaded(4, selected: 2);

        Assert.True(combo.Expand());
        Assert.True(combo.Open);
        Assert.Equal(2, combo.Highlight);

        combo.Move(1);
        Assert.True(combo.Collapse());
        Assert.False(combo.Open);
        Assert.Equal(2, combo.Selected);
        Assert.Equal(2, combo.Highlight);
    }

    [Fact]
    public void AListWithNothingToChooseBetweenDoesNotOpen()
    {
        Assert.False(Loaded(1, selected: 0).Expand());
        Assert.False(Loaded(0, selected: 0).Expand());
    }

    [Fact]
    public void TheCursorWrapsInsideAnOpenList()
    {
        var combo = Loaded(3, selected: 0);
        combo.Expand();

        combo.Move(-1);
        Assert.Equal(2, combo.Highlight);
        combo.Move(1);
        Assert.Equal(0, combo.Highlight);
    }

    [Fact]
    public void AClosedListIgnoresTheCursor()
    {
        var combo = Loaded(3, selected: 0);

        Assert.False(combo.Move(1));
        Assert.Equal(0, combo.Highlight);
    }

    [Fact]
    public void ConfirmHandsBackTheHighlightWithoutMovingThePick()
    {
        var combo = Loaded(4, selected: 0);
        combo.Expand();
        combo.Move(1);

        Assert.Equal(1, combo.Confirm());
        Assert.False(combo.Open);
        Assert.Equal(0, combo.Selected);
        Assert.Equal("Plane 0", combo.Text);
    }

    [Fact]
    public void ConfirmOnAClosedListIsNothing() => Assert.Null(Loaded(4, selected: 0).Confirm());

    [Fact]
    public void SelectIsTheOneThingThatMovesThePick()
    {
        var combo = Loaded(4, selected: 0);

        combo.Select(3);
        Assert.Equal(3, combo.Selected);
        Assert.Equal(3, combo.Highlight);
        Assert.Equal("Plane 3", combo.Text);
    }

    [Fact]
    public void NextIsACandidateAndWrapsBothWays()
    {
        var combo = Loaded(3, selected: 0);

        Assert.Equal(1, combo.Next(1));
        Assert.Equal(2, combo.Next(-1));
        Assert.Equal(0, combo.Selected);
    }

    [Fact]
    public void AShortListNeitherScrollsNorWindows()
    {
        var combo = Loaded(3, selected: 0, rowsDisplayed: 5);
        combo.Expand();

        Assert.False(combo.Scrolls);
        Assert.Equal(3, combo.Visible);
        Assert.Equal(0, combo.First);
    }

    [Fact]
    public void TheWindowMovesOnlyWhenTheCursorLeavesIt()
    {
        var combo = Loaded(10, selected: 0, rowsDisplayed: 3);
        combo.Expand();

        Assert.True(combo.Scrolls);
        Assert.Equal(3, combo.Visible);

        combo.Move(1);
        combo.Move(1);
        Assert.Equal(0, combo.First); // the cursor is still on the last visible row

        combo.Move(1);
        Assert.Equal(1, combo.First); // it left, so the window followed by one
    }

    [Fact]
    public void WrappingToTheEndPullsTheWindowToTheBottom()
    {
        var combo = Loaded(10, selected: 0, rowsDisplayed: 3);
        combo.Expand();

        combo.Move(-1);
        Assert.Equal(9, combo.Highlight);
        Assert.Equal(7, combo.First);
    }

    /// <summary>The pointer's wheel and thumb move the window rather than the cursor, and drag the
    /// highlight along only where it would otherwise leave the window.</summary>
    [Fact]
    public void ScrollingMovesTheWindowAndPullsTheHighlightInsideIt()
    {
        var combo = Loaded(10, selected: 0, rowsDisplayed: 3);
        combo.Expand();

        Assert.True(combo.ScrollTo(1));
        Assert.Equal(1, combo.First);
        Assert.Equal(1, combo.Highlight); // row 0 left the window, so the highlight came with it

        Assert.True(combo.ScrollTo(2));
        Assert.Equal(2, combo.First);
        Assert.Equal(2, combo.Highlight);

        Assert.True(combo.ScrollTo(500)); // clamped to the last window
        Assert.Equal(7, combo.First);
        Assert.Equal(7, combo.Highlight);

        Assert.False(combo.ScrollTo(7)); // already there
        Assert.Equal(0, combo.Selected); // and none of it moved the pick
    }

    [Fact]
    public void AClosedListAndOneThatFitsItsWindowIgnoreTheWheel()
    {
        var closed = Loaded(10, selected: 0, rowsDisplayed: 3);
        Assert.False(closed.ScrollTo(2));
        Assert.Equal(0, closed.First);

        var shortList = Loaded(3, selected: 0, rowsDisplayed: 5);
        shortList.Expand();
        Assert.False(shortList.ScrollTo(1));
        Assert.Equal(0, shortList.First);
    }

    [Fact]
    public void LoadingFreshEntriesClosesTheListAndClampsThePick()
    {
        var combo = Loaded(10, selected: 9, rowsDisplayed: 3);
        combo.Expand();

        combo.Load(new[] { "One", "Two" }, 5);

        Assert.False(combo.Open);
        Assert.Equal(1, combo.Selected);
        Assert.Equal(0, combo.First);
    }

    private static CampaignCombo Loaded(int entries, int selected, int rowsDisplayed = 13)
    {
        var combo = new CampaignCombo(138f, 132f, 271f, 15f, rowsDisplayed);
        var names = new string[entries];
        for (int i = 0; i < entries; i++)
        {
            names[i] = $"Plane {i}";
        }

        combo.Load(names, selected);
        return combo;
    }
}
