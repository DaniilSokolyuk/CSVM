using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>Pins the one member that decides what skips what: the authored asymmetry between the
/// chapter and closing sets, the boot sequence taking any press whatsoever, and a pad button
/// ending all three, since a player holding one has no other press to offer.</summary>
[Trait("Tier", "Quick")]
public class CinemaSkipTests
{
    [Theory]
    [InlineData(CinemaPress.Escape, true)]
    [InlineData(CinemaPress.Space, true)]
    [InlineData(CinemaPress.Return, true)]
    [InlineData(CinemaPress.LeftMouse, true)]
    [InlineData(CinemaPress.PadButton, true)]
    [InlineData(CinemaPress.OtherKey, false)]
    [InlineData(CinemaPress.None, false)]
    public void TheChapterCinemaTakesItsFourPressesAndAPadButton(CinemaPress press, bool skips)
    {
        Assert.Equal(skips, CinemaScreen.ChapterKeys.Skips(press));
    }

    [Theory]
    [InlineData(CinemaPress.Escape, true)]
    [InlineData(CinemaPress.LeftMouse, true)]
    [InlineData(CinemaPress.PadButton, true)]
    [InlineData(CinemaPress.Space, false)]
    [InlineData(CinemaPress.Return, false)]
    [InlineData(CinemaPress.OtherKey, false)]
    [InlineData(CinemaPress.None, false)]
    public void TheClosingCinemaStillRefusesSpaceAndReturn(CinemaPress press, bool skips)
    {
        Assert.Equal(skips, CinemaScreen.ClosingKeys.Skips(press));
    }

    [Theory]
    [InlineData(CinemaPress.Escape)]
    [InlineData(CinemaPress.Space)]
    [InlineData(CinemaPress.Return)]
    [InlineData(CinemaPress.LeftMouse)]
    [InlineData(CinemaPress.OtherKey)]
    [InlineData(CinemaPress.PadButton)]
    public void TheBootSequenceTakesAnyPressThereIs(CinemaPress press)
    {
        Assert.True(CinemaScreen.BootKeys.Skips(press));
    }

    [Fact]
    public void NothingAtAllIsNoPress()
    {
        Assert.False(CinemaScreen.BootKeys.Skips(CinemaPress.None));
        Assert.False(CinemaSkip.None.Skips(CinemaPress.PadButton));
        Assert.False(CinemaSkip.None.Skips(CinemaPress.Escape));
    }

    // A set that names a key and not the pad leaves the pad out, which is what keeps the three
    // authored sets in charge of what they take rather than the predicate.
    [Fact]
    public void APadButtonJoinsASetRatherThanBypassingIt()
    {
        Assert.False(CinemaSkip.Escape.Skips(CinemaPress.PadButton));
        Assert.True((CinemaSkip.Escape | CinemaSkip.PadButton).Skips(CinemaPress.PadButton));
    }
}
