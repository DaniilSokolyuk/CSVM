using CSVM.UI.Boards;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// How the launchscreen's three bands divide a window (<see cref="MenuZones"/>): the one scale all
/// three share, the fixed header and footer, and the middle taking whatever is left. The property
/// the layout rests on is the last one here: a screen that grows in the middle leaves the other two
/// bands where they were, which is what keeps the title and the controls line still.
/// </summary>
public class MenuZonesTests
{
    // A window and a set of band metrics that fit inside it at 1:1, with room to spare.
    private const float Window = 720f;
    private const float HeaderRef = 120f;
    private const float MiddleRef = 300f;
    private const float FooterRef = 90f;

    [Fact]
    public void ContentThatFitsIsServedAtTheReferenceScale()
    {
        var zones = MenuZones.For(Window, HeaderRef, MiddleRef, FooterRef);
        Assert.Equal(1f, zones.Scale);
        Assert.Equal(HeaderRef, zones.Header);
        Assert.Equal(FooterRef, zones.Footer);
    }

    [Fact]
    public void TheMiddleTakesWhateverTheTwoFixedBandsLeave()
    {
        var zones = MenuZones.For(Window, HeaderRef, MiddleRef, FooterRef);
        Assert.Equal(Window, zones.Header + zones.Middle + zones.Footer);
        Assert.True(zones.Middle > MiddleRef);
    }

    [Fact]
    public void ATallerWindowScalesEveryBandTogether()
    {
        var zones = MenuZones.For(2 * Window, HeaderRef, MiddleRef, FooterRef);
        Assert.Equal(2f, zones.Scale);
        Assert.Equal(2 * HeaderRef, zones.Header);
        Assert.Equal(2 * FooterRef, zones.Footer);
    }

    [Fact]
    public void ContentTallerThanTheWindowShrinksToFitItExactly()
    {
        var zones = MenuZones.For(Window, HeaderRef, 4 * Window, FooterRef);
        Assert.True(zones.Scale < 1f);
        Assert.Equal(Window, zones.Header + zones.Middle + zones.Footer, 3);
        Assert.Equal(HeaderRef * zones.Scale, zones.Header, 3);
    }

    [Fact]
    public void AWindowWithNoHeightFallsBackToOneToOne()
    {
        Assert.Equal(1f, MenuZones.For(0f, HeaderRef, MiddleRef, FooterRef).Scale);
        Assert.Equal(1f, MenuZones.For(Window, 0f, 0f, 0f).Scale);
    }

    [Fact]
    public void GrowingTheMiddleLeavesTheOtherTwoBandsWhereTheyWere()
    {
        var few = MenuZones.For(Window, HeaderRef, MiddleRef, FooterRef);
        var many = MenuZones.For(Window, HeaderRef, 1.5f * MiddleRef, FooterRef);
        Assert.Equal(few.Scale, many.Scale);
        Assert.Equal(few.Header, many.Header);
        Assert.Equal(few.Footer, many.Footer);
    }
}
