using CSVM.UI.Boards;
using Godot;
using Xunit;

namespace CSVM.Tests;

public class SplitScreenLayoutTests
{
    [Fact]
    public void TwoPlayersStandSideBySideOnlyWhereEachHalfStaysWiderThanItIsTall()
    {
        // Every shape a 16:9-ish machine has: the split stays stacked, as it has always been.
        Assert.False(SplitScreen.SideBySide(new Vector2(1280f, 720f)));
        Assert.False(SplitScreen.SideBySide(new Vector2(1920f, 1080f)));
        Assert.False(SplitScreen.SideBySide(new Vector2(1366f, 768f)));   // 1.7786, a hair over 16:9
        Assert.False(SplitScreen.SideBySide(new Vector2(1680f, 1050f)));  // 16:10
        Assert.False(SplitScreen.SideBySide(new Vector2(1024f, 768f)));   // 4:3

        // 2:1 and wider: the half is square or landscape, which is the shape the port is built for.
        Assert.True(SplitScreen.SideBySide(new Vector2(2560f, 1280f)));   // exactly 2:1
        Assert.True(SplitScreen.SideBySide(new Vector2(2560f, 1080f)));   // 21:9
        Assert.True(SplitScreen.SideBySide(new Vector2(5120f, 1440f)));   // 32:9
    }

    [Fact]
    public void ThePaneRectsFollowTheChosenAxisAndLeaveTheGridAlone()
    {
        // 2P stacked at 720p: unchanged, gutter included.
        var window = new Vector2(1280f, 720f);
        Assert.Equal(new Rect2(0f, 0f, 1280f, 359f), SplitScreen.PaneRect(0, 2, window, sideBySide: false));
        Assert.Equal(new Rect2(0f, 361f, 1280f, 359f), SplitScreen.PaneRect(1, 2, window, sideBySide: false));

        // 2P side by side on a 32:9 window: two panes of 2559x1440, which is 16:9 to the pixel.
        var wide = new Vector2(5120f, 1440f);
        Assert.Equal(new Rect2(0f, 0f, 2559f, 1440f), SplitScreen.PaneRect(0, 2, wide, sideBySide: true));
        Assert.Equal(new Rect2(2561f, 0f, 2559f, 1440f), SplitScreen.PaneRect(1, 2, wide, sideBySide: true));

        // 3P and 4P are the 2x2 grid at every aspect, side-by-side answer or not.
        foreach (bool side in new[] { false, true })
        {
            Assert.Equal(new Rect2(0f, 0f, 639f, 359f), SplitScreen.PaneRect(0, 4, window, side));
            Assert.Equal(new Rect2(641f, 361f, 639f, 359f), SplitScreen.PaneRect(3, 4, window, side));
            Assert.Equal(new Rect2(641f, 0f, 639f, 359f), SplitScreen.PaneRect(1, 3, window, side));
        }
    }

    [Fact]
    public void TheLaunchscreenPicksInThePaneItThenFliesIn()
    {
        // The plane select lays its panes into the window MINUS a bottom strip, so its own area is
        // proportionally wider than the flight's. Taking the axis from the window keeps the two
        // agreeing: on a 16:9 machine both stack, even though the shorter area is itself past 2:1.
        var window = new Vector2(1280f, 720f);
        var selectArea = new Vector2(1280f, 576f);
        Assert.True(SplitScreen.SideBySide(selectArea));
        Assert.False(SplitScreen.SideBySide(window));
        var picked = SplitScreen.PaneRect(1, 2, selectArea, SplitScreen.SideBySide(window));
        Assert.Equal(0f, picked.Position.X, 3);
        Assert.True(picked.Position.Y > 0f, "the second pane is the lower one, as it is in flight");
    }
}
