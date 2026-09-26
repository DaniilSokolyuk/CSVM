using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>The engine half of the cinema skip, the half a unit cannot build: a real
/// <c>InputEventJoypadButton</c> reads as a pad press and ends all three cinemas, a release ends
/// none, a pads-off run reads no button at all, and the keyboard and mouse events still read as
/// the presses their sets are written against.
/// ⚠ This pins the reading, not the device. Whether a physical pad reaches the window is a
/// question only the controls answer, since a scripted run has no pad.</summary>
internal static class CinemaSkipSuites
{
    [Suite("cinema-skip-pad",
        "A pad button skips a cinema: a synthetic InputEventJoypadButton reads as CinemaPress.PadButton "
        + "and ends the chapter, closing and boot sets alike, a button release ends none of them, the "
        + "same button read with pads off is no press, and Escape, Space, Return and the left mouse "
        + "still read as themselves so the authored sets keep their differences")]
    internal static void CinemaSkipPad(TestContext ctx)
    {
        var button = new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true };
        var press = CinemaSkips.PressOf(button, padsLive: true);
        ctx.Check(press == CinemaPress.PadButton, $"a pressed pad button reads as a pad press ({press})");
        ctx.Check(CinemaScreen.ChapterKeys.Skips(press), $"a pad button ends the chapter cinema ({press})");
        ctx.Check(CinemaScreen.ClosingKeys.Skips(press), $"a pad button ends the closing cinema ({press})");
        ctx.Check(CinemaScreen.BootKeys.Skips(press), $"a pad button ends a boot film or still ({press})");

        var off = CinemaSkips.PressOf(button, padsLive: false);
        ctx.Check(off == CinemaPress.None, $"the same button is no press where pad input does not count ({off})");

        var release = CinemaSkips.PressOf(
            new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = false }, padsLive: true);
        ctx.Check(release == CinemaPress.None, $"a button release is no press ({release})");

        var stick = CinemaSkips.PressOf(
            new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 1f }, padsLive: true);
        ctx.Check(stick == CinemaPress.None, $"a stick at its stop is no press, so a drift skips nothing ({stick})");

        var escape = CinemaSkips.PressOf(Key(Godot.Key.Escape), padsLive: true);
        var space = CinemaSkips.PressOf(Key(Godot.Key.Space), padsLive: true);
        var enter = CinemaSkips.PressOf(Key(Godot.Key.Enter), padsLive: true);
        var other = CinemaSkips.PressOf(Key(Godot.Key.F), padsLive: true);
        ctx.Check(
            escape == CinemaPress.Escape && space == CinemaPress.Space
            && enter == CinemaPress.Return && other == CinemaPress.OtherKey,
            $"the keys read as themselves ({escape}, {space}, {enter}, {other})");

        var click = CinemaSkips.PressOf(
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, padsLive: true);
        ctx.Check(click == CinemaPress.LeftMouse, $"a left click reads as the mouse press both scripts take ({click})");

        var echo = CinemaSkips.PressOf(
            new InputEventKey { Keycode = Godot.Key.Space, Pressed = true, Echo = true }, padsLive: true);
        ctx.Check(echo == CinemaPress.None, $"a key repeat is no press, so a held key ends one cinema only ({echo})");
    }

    private static InputEventKey Key(Key code) =>
        new() { Keycode = code, Pressed = true, Echo = false };
}
