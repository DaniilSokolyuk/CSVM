using System;
using CSVM.Session.Campaign;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Danger Zone photograph's framing (<see cref="CampaignSnapshot.Window"/>): the written file
/// is the 640x480 every one of the original's is. A pane of another shape is cropped to a centred 4:3 window
/// before the scale, never squeezed into it. A wide pane keeps its full height and loses its
/// flanks; a narrow one keeps its full width. Neither answer letterboxes, because the print fills
/// the torn mount's window.
/// Slot: docs/formats/campaign-screens.md, "The danger-zone slot".
/// </summary>
public class CampaignSnapshotWindowTests
{
    // The pane shapes flown here, widest first: the reported 32:9 flight, a splitscreen quarter of
    // it, and the common desktop ratios. The last two are narrower than 4:3, a splitscreen half of
    // a 4:3 window and a portrait window.
    public static TheoryData<int, int> Panes => new()
    {
        { 5120, 1440 }, { 2560, 720 }, { 3440, 1440 }, { 2558, 1408 }, { 1920, 1080 },
        { 1280, 800 }, { 640, 480 }, { 1024, 768 }, { 640, 240 }, { 600, 800 },
    };

    [Fact]
    public void AWidePaneKeepsItsFullHeightAndLosesItsFlanks()
    {
        // The reported flight: 1920 of the 5120 columns, centred, at the whole 1440 rows.
        Assert.Equal(new Rect2I(1600, 0, 1920, 1440), CampaignSnapshot.Window(5120, 1440));
    }

    [Fact]
    public void AFourThreePaneIsFramedWhole()
    {
        Assert.Equal(new Rect2I(0, 0, 640, 480), CampaignSnapshot.Window(640, 480));
        Assert.Equal(new Rect2I(0, 0, 1024, 768), CampaignSnapshot.Window(1024, 768));
    }

    [Fact]
    public void APaneNarrowerThanFourThreeKeepsItsFullWidth()
    {
        Assert.Equal(new Rect2I(0, 175, 600, 450), CampaignSnapshot.Window(600, 800));
    }

    [Theory]
    [MemberData(nameof(Panes))]
    public void TheWindowIsFourThreeInsideThePaneAndCentred(int width, int height)
    {
        var window = CampaignSnapshot.Window(width, height);

        float aspect = (float)window.Size.X / window.Size.Y;
        Assert.Equal((float)CampaignSnapshot.Width / CampaignSnapshot.Height, aspect, 2);
        Assert.True(window.Size.X <= width && window.Size.Y <= height, $"{window} leaves the pane");
        Assert.True(Math.Abs(width - window.Size.X - (2 * window.Position.X)) <= 1,
            $"{window} is not centred across a {width} pane");
        Assert.True(Math.Abs(height - window.Size.Y - (2 * window.Position.Y)) <= 1,
            $"{window} is not centred down a {height} pane");
    }

    // The able-to-fail half: the whole pane is the window only where the pane is already 4:3. A
    // Window answering the pane back unchanged fails here, though it would pass the theory above.
    [Theory]
    [MemberData(nameof(Panes))]
    public void OnlyAFourThreePaneIsFramedWhole(int width, int height)
    {
        var window = CampaignSnapshot.Window(width, height);
        bool whole = window.Size.X == width && window.Size.Y == height;

        Assert.Equal(width * CampaignSnapshot.Height == height * CampaignSnapshot.Width, whole);
    }

    [Fact]
    public void APaneWithNoAreaAnswersAnEmptyWindow()
    {
        Assert.Equal(new Rect2I(0, 0, 0, 0), CampaignSnapshot.Window(0, 480));
        Assert.Equal(new Rect2I(0, 0, 0, 0), CampaignSnapshot.Window(640, 0));
        Assert.Equal(new Rect2I(0, 0, 0, 0), CampaignSnapshot.Window(-8, -8));
    }
}
