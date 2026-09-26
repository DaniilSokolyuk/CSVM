using Godot;

namespace CSVM.UI.Screens;

/// <summary>What a press is, as a skip set reads it. A screen reads the device event it was handed
/// into one of these and asks <see cref="CinemaSkips"/>, so the films and the boot stills answer
/// one rule instead of a predicate each. ⚠ Do not fold this into <see cref="CinemaSkip"/>; the two
/// are not one list. A press is the single thing that arrived where a set is any number of flags,
/// <see cref="OtherKey"/> is a press no set names on its own, and <see cref="CinemaSkip.AnyPress"/>
/// is a set no press answers to.</summary>
public enum CinemaPress
{
    /// <summary>Nothing any set takes: a release, a key repeat, a stick, a pad that does not
    /// count.</summary>
    None,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>Space.</summary>
    Space,

    /// <summary>Return, the main one or the keypad's.</summary>
    Return,

    /// <summary>A left mouse press.</summary>
    LeftMouse,

    /// <summary>A key none of the named ones, which only an any-press set takes.</summary>
    OtherKey,

    /// <summary>A gamepad button.</summary>
    PadButton,
}

/// <summary>The one member that decides what skips what, and the reading of a device event that
/// feeds it. <see cref="Skips"/> holds the whole rule and takes no engine, so every set is pinned
/// off engine; <see cref="PressOf"/> is the engine's half and holds no policy of its own.</summary>
public static class CinemaSkips
{
    /// <summary>Whether <paramref name="press"/> ends a cinema whose set is <paramref name="skip"/>.
    /// <see cref="CinemaSkip.AnyPress"/> takes every press there is, a pad button included, because
    /// the screens carrying it have taught the player no key yet. Even that set ends nothing on
    /// <see cref="CinemaPress.None"/>, which is what a device event no set reads answers.</summary>
    public static bool Skips(this CinemaSkip skip, CinemaPress press) =>
        press != CinemaPress.None
        && (skip.HasFlag(CinemaSkip.AnyPress) || (skip & NamedFlag(press)) != CinemaSkip.None);

    /// <summary>What a device event is to a skip set. <paramref name="padsLive"/> is whether pad
    /// input counts at all: a button arriving while it does not is no press, since a pad reports
    /// its first button pressed as it connects and <c>--no-pads</c> would otherwise still see it.
    /// </summary>
    public static CinemaPress PressOf(InputEvent @event, bool padsLive) => @event switch
    {
        InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } => CinemaPress.LeftMouse,
        InputEventJoypadButton { Pressed: true } => padsLive ? CinemaPress.PadButton : CinemaPress.None,
        InputEventKey { Pressed: true, Echo: false } key => key.Keycode switch
        {
            Key.Escape => CinemaPress.Escape,
            Key.Space => CinemaPress.Space,
            Key.Enter or Key.KpEnter => CinemaPress.Return,
            _ => CinemaPress.OtherKey,
        },
        _ => CinemaPress.None,
    };

    // The flag a set names this press by, and CinemaSkip.None for a press no set names on its own.
    // ⚠ Test the answer with a bit test, never HasFlag: HasFlag(None) is true for every set, so an
    // unnamed press would end every cinema.
    private static CinemaSkip NamedFlag(CinemaPress press) => press switch
    {
        CinemaPress.Escape => CinemaSkip.Escape,
        CinemaPress.Space => CinemaSkip.Space,
        CinemaPress.Return => CinemaSkip.Return,
        CinemaPress.LeftMouse => CinemaSkip.LeftMouse,
        CinemaPress.PadButton => CinemaSkip.PadButton,
        _ => CinemaSkip.None,
    };
}
