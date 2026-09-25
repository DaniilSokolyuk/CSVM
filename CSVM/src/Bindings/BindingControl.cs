using System;

namespace CSVM.Bindings;

/// <summary>Which control shape a <see cref="BindingControl"/> carries. The tag is the
/// discriminator: the numeric members mean different things per kind and only the ones the kind
/// names are meaningful.</summary>
public enum ControlKind
{
    Key,
    Button,
    Axis,
    Hat,
    Mouse,

    /// <summary>A whole axis driving an action pair (<see cref="AxisPairs"/>), one side per action.
    /// </summary>
    FullAxis,
}

/// <summary>The modifier keys a keyboard binding names, as flags, which is the original's own
/// three bits over a scancode (`0x100` Alt, `0x200` Ctrl, `0x400` Shift, `docs/org/input.md`). Left
/// and right variants collapse onto one flag there and here, since Godot reports one keycode for
/// each pair.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>The four directions of one hat, as flags so a device can report several at once (a
/// diagonal is Up and Right together). A binding names exactly one of them.</summary>
[Flags]
public enum HatDirection
{
    None = 0,
    Up = 1,
    Right = 2,
    Down = 4,
    Left = 8,
}

/// <summary>One control on a device: a key, a button, a mouse button, a half axis, a full axis or
/// a hat direction. The tagged shape is deliberate. Four typed slots
/// is what the original ships (`docs/org/input.md`), and it is why an axis cannot be bound there.
/// ⚠ Build one through the six factories, never through the default value. They alone enforce the
/// per-kind invariants: a sign of exactly ±1, a deadzone in range, exactly one hat direction.
/// </summary>
public readonly record struct BindingControl
{
    /// <summary>The widest deadzone a full axis honours. Past it the rescaled side would be a switch
    /// with a few percent of travel, which no stick setting means; the value itself is TUNE.</summary>
    public const float MaxFullAxisDeadzone = 0.95f;

    private BindingControl(
        ControlKind kind, int index, int sign, float deadzone, HatDirection direction, KeyModifiers modifiers)
    {
        Kind = kind;
        Index = index;
        Sign = sign;
        Deadzone = deadzone;
        Direction = direction;
        Modifiers = modifiers;
    }

    public ControlKind Kind { get; }

    /// <summary>The key code, button index, axis index or hat index, per <see cref="Kind"/>. A key
    /// code is the engine's own key constant, not a DirectInput scancode.</summary>
    public int Index { get; }

    /// <summary>Which way the axis has to move, +1 or -1. On a full axis, +1 feeds raw positive travel
    /// to the pair's positive action and -1 is the inverted binding. Zero for every other kind, since
    /// a binding on a key or a button has no direction to choose.</summary>
    public int Sign { get; }

    /// <summary>How far past centre the axis has to travel before it resolves at all. A half axis
    /// takes [0, 1), a full one [0, <see cref="MaxFullAxisDeadzone"/>]. It is both the noise gate and
    /// the digital threshold; the model carries no second number for the two jobs.</summary>
    public float Deadzone { get; }

    /// <summary>Whether a full axis feeds its raw positive travel to the pair's negative action.
    /// False for every other kind.</summary>
    public bool Inverted => Kind == ControlKind.FullAxis && Sign < 0;

    /// <summary>The one hat direction this binding watches. <c>None</c> for every other kind.
    /// </summary>
    public HatDirection Direction { get; }

    /// <summary>Which modifiers a key binding names, part of its identity: E and Shift+E are two
    /// controls and drive two actions. <c>None</c> for every other kind.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>One key, optionally under modifiers. A bare key is blocked while a modifier another
    /// action holds on the same key is down, which is what keeps E off Shift+E's press
    /// (<see cref="ActionMap.ContestedFor"/>).</summary>
    public static BindingControl Key(int keyCode, KeyModifiers modifiers = KeyModifiers.None) =>
        new(ControlKind.Key, NonNegative(keyCode, nameof(keyCode)), 0, 0f, HatDirection.None, modifiers);

    public static BindingControl Button(int index) =>
        new(ControlKind.Button, NonNegative(index, nameof(index)), 0, 0f, HatDirection.None, KeyModifiers.None);

    /// <summary>The modifier that key IS, so a binding on Shift itself is not asked to prove Shift
    /// is up. Godot reports one keycode per pair, as the original collapses its two scancodes.
    /// </summary>
    public static KeyModifiers ModifierOf(int keyCode) => keyCode switch
    {
        (int)Godot.Key.Shift => KeyModifiers.Shift,
        (int)Godot.Key.Ctrl => KeyModifiers.Ctrl,
        (int)Godot.Key.Alt => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };

    /// <summary>The key that names one modifier flag, for a reader asking whether it is held.
    /// </summary>
    public static int KeyCodeOf(KeyModifiers modifier) => modifier switch
    {
        KeyModifiers.Shift => (int)Godot.Key.Shift,
        KeyModifiers.Ctrl => (int)Godot.Key.Ctrl,
        _ => (int)Godot.Key.Alt,
    };

    /// <summary>One mouse button, held. The original carries one in bits 26-27 of every command word
    /// (`FUN_00537150`, `docs/org/input.md`), the one input this model dropped until this factory
    /// existed.</summary>
    public static BindingControl Mouse(int index) =>
        new(ControlKind.Mouse, NonNegative(index, nameof(index)), 0, 0f, HatDirection.None, KeyModifiers.None);

    /// <summary>One direction of one axis. The sign picks the half of the travel that fires, so a
    /// stick's two ends are two bindings and can drive two different actions.</summary>
    public static BindingControl Axis(int index, int sign, float deadzone)
    {
        if (sign != 1 && sign != -1)
            throw new ArgumentOutOfRangeException(nameof(sign), sign, "An axis binding needs a sign of +1 or -1.");
        if (!(deadzone >= 0f) || deadzone >= 1f)
            throw new ArgumentOutOfRangeException(nameof(deadzone), deadzone, "A deadzone lies in [0, 1).");
        return new BindingControl(
            ControlKind.Axis, NonNegative(index, nameof(index)), sign, deadzone, HatDirection.None, KeyModifiers.None);
    }

    /// <summary>A whole axis over an action pair, each side reading 0 at the deadzone edge and 1 at
    /// full travel. A flight stick needs one binding where a pad needs two. Which pair it drives is
    /// the action that holds it (<see cref="AxisPairs"/>), not the control.</summary>
    public static BindingControl FullAxis(int index, bool inverted, float deadzone)
    {
        if (!(deadzone >= 0f) || deadzone > MaxFullAxisDeadzone)
            throw new ArgumentOutOfRangeException(nameof(deadzone), deadzone, "A full-axis deadzone lies in [0, 0.95].");
        return new BindingControl(
            ControlKind.FullAxis, NonNegative(index, nameof(index)), inverted ? -1 : 1, deadzone, HatDirection.None, KeyModifiers.None);
    }

    /// <summary>One direction of one hat. Exactly one direction, because a binding on a diagonal
    /// would be a second, hidden combining rule beside the one the binding list already has.
    /// ⚠ Do not author one on a pad. Godot reports a d-pad as four buttons, so a hat binding there
    /// aliases a button binding that <see cref="ActionMap.SameControl"/> reads as different. A
    /// stick's hat has no button form, so there it is the only binding its directions have.</summary>
    public static BindingControl Hat(int index, HatDirection direction)
    {
        if (direction is not (HatDirection.Up or HatDirection.Right or HatDirection.Down or HatDirection.Left))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "A hat binding names exactly one direction.");
        return new BindingControl(
            ControlKind.Hat, NonNegative(index, nameof(index)), 0, 0f, direction, KeyModifiers.None);
    }

    /// <summary>The modifier prefix a key's name carries, in a fixed order so one control spells one
    /// way: <c>Ctrl+Alt+Shift+</c>, and empty for a bare key.</summary>
    public static string Prefix(KeyModifiers modifiers)
    {
        string text = string.Empty;
        if ((modifiers & KeyModifiers.Ctrl) != 0)
            text += "Ctrl+";
        if ((modifiers & KeyModifiers.Alt) != 0)
            text += "Alt+";
        if ((modifiers & KeyModifiers.Shift) != 0)
            text += "Shift+";
        return text;
    }

    public override string ToString() => Kind switch
    {
        ControlKind.Key => $"key:{Prefix(Modifiers)}{Index}",
        ControlKind.Button => $"button:{Index}",
        ControlKind.Axis => $"axis:{Index}{(Sign < 0 ? "-" : "+")}@{Deadzone:0.###}",
        ControlKind.Mouse => $"mouse:{Index}",
        ControlKind.FullAxis => $"fullaxis:{Index}{(Sign < 0 ? "-" : "+")}@{Deadzone:0.####}",
        _ => $"hat:{Index}:{Direction}",
    };

    private static int NonNegative(int value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "A control index is never negative.");
}
