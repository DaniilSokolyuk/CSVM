using CSVM.Flight.Hud;
using Godot;
using Xunit;

namespace CSVM.Tests;

public class HudMetricsTests
{
    [Fact]
    public void TextScalesDefaultToOneAndStayWithinTheAccessibleRange()
    {
        Assert.Equal(1f, HudMetrics.StatusTextScale, 3);
        Assert.Equal(1f, HudMetrics.MarkerTextScale, 3);
        Assert.Equal(0.5f, HudMetrics.ClampTextScale(0f), 3);
        Assert.Equal(0.5f, HudMetrics.ClampTextScale(0.49f), 3);
        Assert.Equal(1.25f, HudMetrics.ClampTextScale(1.25f), 3);
        Assert.Equal(2f, HudMetrics.ClampTextScale(2.01f), 3);
    }

    [Fact]
    public void TheReadingBoxIsThePaneUntilThePaneIsWiderThanTheReferenceFrame()
    {
        // 16:9 and anything narrower is its own box, so no ordinary screen and no 4P pane moves.
        Check(new Vector2(1280f, 720f), 0f, 1280f, 720f);
        Check(new Vector2(640f, 360f), 0f, 640f, 360f);
        Check(new Vector2(1920f, 1440f), 0f, 1920f, 1440f);

        // The 4P grid's own pane at 720p, a fraction over 16:9 because of the 2 px gutters: still
        // its own box, so the dials do not move by half a pixel on an ordinary splitscreen.
        Check(new Vector2(639f, 359f), 0f, 639f, 359f);

        // 32:9: half the width, centred, so the columns stand a 16:9 reading width apart.
        Check(new Vector2(5120f, 1440f), 1280f, 2560f, 1440f);

        // A 2-player pane stacked on a 720p window is 1280x360, which IS 32:9, the same law with
        // no aspect branch, which is why the ultrawide symptom showed up in splitscreen first.
        Check(new Vector2(1280f, 360f), 320f, 640f, 360f);

        // A pane the tree has not sized yet: no divide, no NaN placement.
        Check(Vector2.Zero, 0f, 0f, 0f);
    }

    private static void Check(Vector2 paneSize, float x, float width, float height)
    {
        var box = HudMetrics.ReadingBox(paneSize);
        Assert.Equal(x, box.Position.X, 3);
        Assert.Equal(0f, box.Position.Y, 3);
        Assert.Equal(width, box.Size.X, 3);
        Assert.Equal(height, box.Size.Y, 3);
    }
}
