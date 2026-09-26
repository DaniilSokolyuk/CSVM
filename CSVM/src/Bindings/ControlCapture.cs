using System.Collections.Generic;
using Godot;

namespace CSVM.Bindings;

/// <summary>The controls a rebinding screen can capture, and the scan that turns a press into a
/// <see cref="Binding"/>. It reads the seat's own <see cref="IDeviceState"/>, so a captured pad
/// control carries the placeholder identity the seat's map is authored on, never a hardware GUID.
/// A GUID beside a placeholder row (<see cref="DefaultBindings.AnyPad"/>) would be two controls to
/// <see cref="ActionMap.SameControl"/> and one to the player.
/// ⚠ A pad's hat is not scanned: a d-pad direction arrives as a button and is captured as that.
/// A pad axis is scanned rest-then-move (<see cref="MoveThreshold"/>), since a resting stick drifts.
/// Sticks, hats included, are scanned by <see cref="StickCapture"/>.</summary>
public sealed class ControlCapture
{
    /// <summary>The key that cancels a capture instead of being captured. A screen with no way out
    /// is worse than an unbindable Escape, and Escape keeps its shipped bindings either way.
    /// </summary>
    public const Key CancelKey = Key.Escape;

    /// <summary>The pad button that cancels, on the same reasoning as <see cref="CancelKey"/>. It
    /// is the seat's own Back, so the gesture is the one every other screen uses.</summary>
    public const JoyButton CancelButton = JoyButton.B;

    /// <summary>How near centre an axis must sit before the capture will accept a move on it, so a
    /// stick that never returns to rest can never be latched. TUNE (`BL-693`).</summary>
    public const float RestBand = 0.25f;

    /// <summary>How far an axis must travel from rest to count as the player's choice. Well past
    /// <see cref="RestBand"/>, so drift and a sloppy centre cannot reach it. TUNE (`BL-693`).
    /// </summary>
    public const float MoveThreshold = 0.6f;

    /// <summary>The deadzone stamped on a captured axis, which is also its digital threshold
    /// (<see cref="BindingControl.Deadzone"/>). It is a constant rather than the travel the capture
    /// saw, because the value a stick crosses is not the value it settles at. TUNE (`BL-693`).
    /// </summary>
    public const float CapturedDeadzone = 0.5f;

    // Every key a player may bind: the letters and digits, F1-F12, the numpad, the arrows, the
    // editing and modifier keys, and the punctuation the shipped defaults already use. F13 upward
    // is left out on purpose, because that block is the debug overlays' (docs/controls.md) and a
    // player action landing there would fight a diagnostic.
    private static readonly Key[] Keys = BuildKeys();

    // Godot's SDL button range, which is every button it can report. Guide is included: a pad that
    // exposes it can bind it, and one that does not never reports it.
    private static readonly JoyButton[] Buttons = BuildButtons();

    // The mouse buttons past the pointer's own. Left is left out because it is how a pointer-driven
    // presentation confirms the very row being rebound.
    private static readonly MouseButton[] MouseButtons =
    {
        MouseButton.Right, MouseButton.Middle,
    };

    // Godot's SDL axis range: the two sticks and the two triggers, which is every axis it reports
    // against a stable index. Anything past SdlMax is device-specific and has no name to print.
    private static readonly JoyAxis[] Axes = BuildAxes();

    // The modifier keys, which are captured differently from every other key: held, they qualify the
    // key pressed after them, so one alone can only be meant once it is let go with nothing else
    // pressed. Without that a player reaching for Shift+E would latch Shift the moment it went down.
    private static readonly Key[] ModifierKeys = { Key.Shift, Key.Ctrl, Key.Alt };

    private readonly HashSet<int> _maskedKeys = new();
    private readonly HashSet<int> _maskedButtons = new();
    private readonly HashSet<int> _maskedMouse = new();

    // An axis stays here until it has been seen inside the rest band, which is the release-first
    // mask of a button written for a control that has no release. An axis already deflected when
    // the capture armed is masked, and drift never leaves the band, so neither can be captured.
    private readonly HashSet<int> _maskedAxes = new();

    private readonly DeviceId _pad;
    private readonly bool _readsKeyboard;
    private readonly bool _sticksOnly;
    private readonly StickCapture _sticks;
    private int _pendingModifier = -1;

    /// <summary>A capture for one seat, whose pad bindings sit on <paramref name="pad"/>. A pad-only
    /// splitscreen seat passes <paramref name="readsKeyboard"/> false, so it never binds the shared
    /// keyboard. The action being bound, <paramref name="row"/>, decides whether a stick axis becomes
    /// a full axis. With <paramref name="sticksOnly"/> it captures nothing but stick controls, and
    /// Escape and the pad's Back still cancel.</summary>
    public ControlCapture(DeviceId pad, bool readsKeyboard, InputAction? row = null, bool sticksOnly = false)
    {
        _pad = pad;
        _readsKeyboard = readsKeyboard;
        _sticksOnly = sticksOnly;
        _sticks = new StickCapture(row);
    }

    /// <summary>Masks everything currently held, so a control still down from the press that opened
    /// the capture is not read as the player's choice. A masked control has to be released before
    /// it can be captured, and a masked axis has to be seen at rest.</summary>
    public void Arm(IDeviceState state)
    {
        _maskedKeys.Clear();
        _maskedButtons.Clear();
        _maskedMouse.Clear();
        _maskedAxes.Clear();
        _pendingModifier = -1;
        _sticks.Arm(state);
        foreach (var axis in Axes)
        {
            if (!AtRest(state, axis))
                _maskedAxes.Add((int)axis);
        }

        foreach (var key in Keys)
        {
            if (KeyDown(state, key))
                _maskedKeys.Add((int)key);
        }

        foreach (var button in Buttons)
        {
            if (state.IsButtonDown(_pad, (int)button))
                _maskedButtons.Add((int)button);
        }

        foreach (var button in MouseButtons)
        {
            if (MouseDown(state, button))
                _maskedMouse.Add((int)button);
        }
    }

    /// <summary>Whether the player is asking to abandon the capture rather than to bind something.
    /// Checked once a frame before <see cref="Poll"/>, since neither cancel control is ever
    /// captured and both are masked by <see cref="Arm"/> like any other.</summary>
    public bool Cancelled(IDeviceState state)
    {
        bool key = Fresh(_maskedKeys, (int)CancelKey, KeyDown(state, CancelKey));
        bool button = Fresh(_maskedButtons, (int)CancelButton, state.IsButtonDown(_pad, (int)CancelButton));
        return key || button;
    }

    /// <summary>The control the player pressed since <see cref="Arm"/>, or null while none has
    /// been. The order is keys, pad buttons, stick buttons and hats, the mouse, pad axes, stick axes.
    /// A frame holding several answers the same way twice, and a button beats an axis a hand rests on.
    /// A key pressed under Shift, Ctrl or Alt carries them; a modifier alone is captured on release.
    /// </summary>
    public Binding? Poll(IDeviceState state)
    {
        if (_sticksOnly)
            return _sticks.PollPresses(state) ?? _sticks.PollAxes(state);

        var modifiers = HeldModifiers(state);
        foreach (var key in Keys)
        {
            if (key != CancelKey && BindingControl.ModifierOf((int)key) == KeyModifiers.None
                && Fresh(_maskedKeys, (int)key, KeyDown(state, key)))
                return new Binding(DeviceId.Keyboard, BindingControl.Key((int)key, modifiers));
        }

        if (ModifierAlone(state) is { } alone)
            return alone;

        foreach (var button in Buttons)
        {
            if (button != CancelButton
                && Fresh(_maskedButtons, (int)button, state.IsButtonDown(_pad, (int)button)))
                return new Binding(_pad, BindingControl.Button((int)button));
        }

        if (_sticks.PollPresses(state) is { } press)
            return press;

        foreach (var button in MouseButtons)
        {
            if (Fresh(_maskedMouse, (int)button, MouseDown(state, button)))
                return new Binding(DeviceId.Mouse, BindingControl.Mouse((int)button));
        }

        foreach (var axis in Axes)
        {
            if (Moved(state, axis) is not { } sign)
                continue;
            return new Binding(_pad, BindingControl.Axis((int)axis, sign, CapturedDeadzone));
        }

        return _sticks.PollAxes(state);
    }

    private static bool Fresh(HashSet<int> masked, int index, bool down)
    {
        if (!down)
        {
            masked.Remove(index);
            return false;
        }

        return !masked.Contains(index);
    }

    private static Key[] BuildKeys()
    {
        var keys = new List<Key>();
        for (var k = Key.A; k <= Key.Z; k++)
            keys.Add(k);
        for (var k = Key.Key0; k <= Key.Key9; k++)
            keys.Add(k);
        for (var k = Key.F1; k <= Key.F12; k++)
            keys.Add(k);
        for (var k = Key.Kp0; k <= Key.Kp9; k++)
            keys.Add(k);
        keys.AddRange(new[]
        {
            Key.KpAdd, Key.KpSubtract, Key.KpMultiply, Key.KpDivide, Key.KpPeriod, Key.KpEnter,
            Key.Up, Key.Down, Key.Left, Key.Right,
            Key.Space, Key.Enter, Key.Tab, Key.Backspace,
            Key.Shift, Key.Ctrl, Key.Alt, Key.Escape,
            Key.Comma, Key.Period, Key.Slash, Key.Backslash, Key.Semicolon, Key.Apostrophe,
            Key.Bracketleft, Key.Bracketright, Key.Minus, Key.Equal, Key.Quoteleft,
        });
        return keys.ToArray();
    }

    private static JoyButton[] BuildButtons()
    {
        var buttons = new List<JoyButton>();
        for (int i = 0; i < (int)JoyButton.SdlMax; i++)
            buttons.Add((JoyButton)i);
        return buttons.ToArray();
    }

    private static JoyAxis[] BuildAxes()
    {
        var axes = new List<JoyAxis>();
        for (int i = 0; i < (int)JoyAxis.SdlMax; i++)
            axes.Add((JoyAxis)i);
        return axes.ToArray();
    }

    private static bool InBand(float travel) => travel > -RestBand && travel < RestBand;

    // The sign of a decisive move on an axis that has been seen at rest since the capture armed, or
    // null. An axis inside the rest band is unmasked here, which is the only place a mask is
    // dropped, so an axis that never rests is never a candidate.
    private int? Moved(IDeviceState state, JoyAxis axis)
    {
        float travel = state.AxisValue(_pad, (int)axis);
        if (InBand(travel))
        {
            _maskedAxes.Remove((int)axis);
            return null;
        }

        if (_maskedAxes.Contains((int)axis))
            return null;
        if (travel >= MoveThreshold)
            return 1;
        return travel <= -MoveThreshold ? -1 : null;
    }

    private bool AtRest(IDeviceState state, JoyAxis axis) => InBand(state.AxisValue(_pad, (int)axis));

    private KeyModifiers HeldModifiers(IDeviceState state)
    {
        var held = KeyModifiers.None;
        foreach (var key in ModifierKeys)
        {
            if (KeyDown(state, key))
                held |= BindingControl.ModifierOf((int)key);
        }

        return held;
    }

    // A modifier bound on its own, which only resolves on release: while one is down it may still be
    // qualifying a key to come. Pressing a second modifier hands the pending one over, so the last
    // one released is never the answer to a chord the player abandoned.
    private Binding? ModifierAlone(IDeviceState state)
    {
        foreach (var key in ModifierKeys)
        {
            bool down = KeyDown(state, key);
            if (Fresh(_maskedKeys, (int)key, down))
            {
                _pendingModifier = (int)key;
            }
            else if (!down && _pendingModifier == (int)key)
            {
                _pendingModifier = -1;
                return new Binding(DeviceId.Keyboard, BindingControl.Key((int)key));
            }
        }

        return null;
    }

    private bool KeyDown(IDeviceState state, Key key) =>
        _readsKeyboard && state.IsKeyDown(DeviceId.Keyboard, (int)key);

    private bool MouseDown(IDeviceState state, MouseButton button) =>
        _readsKeyboard && state.IsMouseButtonDown(DeviceId.Mouse, (int)button);
}
