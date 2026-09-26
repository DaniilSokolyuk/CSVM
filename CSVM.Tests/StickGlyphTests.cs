using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Boards;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// A flight stick's control as a prompt glyph. The stick's name rides on the key, so a stick button
/// never draws the pad button of the same index. A button draws as the flight stick and its number
/// in filled digits. A hat draws as the stick and a d-pad arm, and an axis keeps its words.
/// </summary>
public sealed class StickGlyphTests
{
    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly DeviceId Pad = DeviceId.Joypad("test-pad");
    private static readonly PromptFontGlyphs Set = new();

    [Fact]
    public void AStickButtonCarriesItsStickAndIsNotThePadButtonOfTheSameIndex()
    {
        var stick = GlyphKey.Of(new Binding(VkbR.Device, BindingControl.Button(0)), Name);
        var pad = GlyphKey.Of(new Binding(Pad, BindingControl.Button((int)JoyButton.A)), Name);

        Assert.Equal("R", stick.Stick);
        Assert.Null(pad.Stick);
        Assert.NotEqual(pad, stick);
        Assert.Equal(ControlGlyphs.PadA, pad);
    }

    [Theory]
    [InlineData(0, "⓵")]
    [InlineData(8, "⓽")]
    [InlineData(9, "⓵⓿")]
    [InlineData(27, "⓶⓼")]
    public void AStickButtonIsItsOneBasedNumberInFilledDigits(int index, string mark)
    {
        var key = new GlyphKey(ControlKind.Button, index, 0, Stick: "R");

        Assert.Equal(mark, PromptFontGlyphs.StickMark(key));
        Assert.True(Set.Draws(key));
    }

    [Fact]
    public void AStickHatIsADpadArmAndAStickAxisKeepsItsWords()
    {
        var hat = GlyphKey.Of(new Binding(VkbR.Device, BindingControl.Hat(0, HatDirection.Left)), Name);
        var axis = GlyphKey.Of(new Binding(VkbR.Device, BindingControl.FullAxis(5, false, 0.02f)), Name);
        var half = new GlyphKey(ControlKind.Axis, 3, 1, Stick: "R");

        Assert.Equal("↞", PromptFontGlyphs.StickMark(hat));
        Assert.True(Set.Draws(hat));
        Assert.False(Set.Draws(axis));
        Assert.False(Set.Draws(half));
    }

    private static string? Name(DeviceId device) => device == VkbR.Device ? "R" : null;
}
