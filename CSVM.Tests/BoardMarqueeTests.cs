using System.Collections.Generic;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Boards;
using CSVM.UI.Menu.Original;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The marquee a caption too wide for its cell scrolls by. Also the one-line text a KEYS AND
/// BUTTONS cell holds when several bindings share it.
/// </summary>
public class BoardMarqueeTests
{
    private const float Overflow = 40f;

    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    private static double Travel => Overflow / BoardMarquee.PixelsPerSecond;

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(7.25d)]
    [InlineData(1000d)]
    public void ACaptionThatFitsNeverMoves(double seconds)
    {
        Assert.Equal(0f, BoardMarquee.Offset(0f, seconds));
        Assert.Equal(0f, BoardMarquee.Offset(-3f, seconds));
        Assert.Equal(0d, BoardMarquee.Cycle(0f));
    }

    /// <summary>The start is where a clock that never ran, a pinned deterministic run, stands.
    /// </summary>
    [Fact]
    public void AnOverflowingCaptionRestsAtItsStartFirst()
    {
        Assert.Equal(0f, BoardMarquee.Offset(Overflow, 0d));
        Assert.Equal(0f, BoardMarquee.Offset(Overflow, BoardMarquee.HoldSeconds * 0.99));
    }

    [Fact]
    public void ItScrollsAtTheSetSpeedUntilItsEndShows()
    {
        double halfway = BoardMarquee.HoldSeconds + (Travel / 2d);
        Assert.Equal(Overflow / 2f, BoardMarquee.Offset(Overflow, halfway), 3);
        Assert.Equal(BoardMarquee.PixelsPerSecond, BoardMarquee.Offset(Overflow, BoardMarquee.HoldSeconds + 1d), 3);
    }

    [Fact]
    public void ItRestsAtTheEndThenScrollsBackAndRepeats()
    {
        double end = BoardMarquee.HoldSeconds + Travel;
        Assert.Equal(Overflow, BoardMarquee.Offset(Overflow, end + 0.01));
        Assert.Equal(Overflow, BoardMarquee.Offset(Overflow, end + BoardMarquee.HoldSeconds - 0.01));
        double back = end + BoardMarquee.HoldSeconds + (Travel / 2d);
        Assert.Equal(Overflow / 2f, BoardMarquee.Offset(Overflow, back), 3);
        double cycle = BoardMarquee.Cycle(Overflow);
        Assert.Equal(2d * (BoardMarquee.HoldSeconds + Travel), cycle, 6);
        Assert.Equal(0f, BoardMarquee.Offset(Overflow, cycle + 0.01));
        double quarter = cycle / 4d;
        Assert.Equal(BoardMarquee.Offset(Overflow, quarter), BoardMarquee.Offset(Overflow, cycle + quarter), 3);
    }

    [Fact]
    public void TheOffsetNeverLeavesTheOverflow()
    {
        for (double t = 0d; t < 3d * BoardMarquee.Cycle(Overflow); t += 0.05)
        {
            float offset = BoardMarquee.Offset(Overflow, t);
            Assert.InRange(offset, 0f, Overflow);
        }
    }

    [Fact]
    public void SeveralStickBindingsShareOneLineInTheRowsOrder()
    {
        var sticks = new List<Binding>
        {
            new(VkbR.Device, BindingControl.Button(3)),
            new(VkbL.Device, BindingControl.FullAxis(5, true, StickCapture.FlightDeadzone)),
            new(VkbL.Device, BindingControl.Hat(0, HatDirection.Up)),
        };

        // No profile set is live here, so both sticks are unnamed and print the control alone.
        Assert.Equal("Button 4 / Axis 6 inverted / Hat Up", KeysStickColumn.Text(sticks));
        Assert.Equal("Button 4", KeysStickColumn.Text(sticks.Take(1).ToList()));
        Assert.Equal(string.Empty, KeysStickColumn.Text(new List<Binding>()));
    }

    /// <summary>The clear gesture drops the Stick cell's first stick binding, which is the caption
    /// the line starts with.</summary>
    [Fact]
    public void TheClearedSlotIsTheFirstCaptionListed()
    {
        var bindings = new List<Binding>
        {
            new(DeviceId.Keyboard, BindingControl.Key((int)Godot.Key.W)),
            new(VkbL.Device, BindingControl.Button(7)),
            new(VkbR.Device, BindingControl.Button(1)),
        };
        var (_, sticks) = KeysStickColumn.Split(bindings);
        int slot = KeysStickColumn.SlotOfStick(bindings);

        Assert.StartsWith(StickLabels.Column(bindings[slot]) + KeysStickColumn.Separator, KeysStickColumn.Text(sticks));
        Assert.Equal(1, slot);
    }

    [Fact]
    public void JoinedUsesTheSeparatorAndTheGivenCaption()
    {
        var keys = new[]
        {
            new Binding(DeviceId.Keyboard, BindingControl.Key((int)Godot.Key.N)),
            new Binding(DeviceId.Keyboard, BindingControl.Key((int)Godot.Key.B)),
        };

        Assert.Equal("N / B", KeysStickColumn.Joined(keys, BindingLabels.Describe));
        Assert.Equal(" / ", KeysStickColumn.Separator);
    }
}
