using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>Which side of a seat's hardware a control prompt names: the keyboard and the mouse
/// together, or the seat's pads. The two are one side because the platform reports a single keyboard
/// and a single mouse for the person at the desk, the reasoning <see cref="DeviceId"/> gives them a
/// singleton identity.</summary>
public enum DeviceSide
{
    Keyboard,
    Pad,
}

/// <summary>Which side produced a seat's last real input, so a control prompt names the control the
/// player is holding rather than the first entry in a binding list. One side at a time: a prompt
/// reads <see cref="Side"/> and recomposes on the tick <see cref="Observe"/> reports a handover.
/// ⚠ A press hands the prompt over and a held control does not. A stick short of
/// <see cref="PressTravel"/> is drift rather than input, and a stick the player leans on must not
/// take the prompt back off a key pressed after it.</summary>
public sealed class ActiveDevice
{
    /// <summary>How far a control has to be deflected before it counts as input here. A button
    /// reads fully on, so this gates the sticks alone: the flight axes carry no deadzone of their
    /// own (they want their travel raw from zero), and an idle stick drifts. TUNE, the same
    /// judgement <c>DefaultBindings</c>'s menu stick deadzone makes about a deliberate push.
    /// </summary>
    public const float PressTravel = 0.5f;

    // InputAction is contiguous from zero, its own contract, so the count is the loop bound over
    // either side's snapshot.
    private static readonly int ActionCount = Enum.GetValues<InputAction>().Length;

    private int _keyboardActive;   // actions each side held entering this tick, which is what makes
    private int _padActive;        // a press visible: the count rises on one and not on the other

    /// <summary>The side a prompt names. Keyboard until a press moves it, which is what a seat
    /// nobody plugs a pad into reads for a whole session.</summary>
    public DeviceSide Side { get; private set; }

    /// <summary>Which of an action's bindings a prompt on <paramref name="side"/> names: that side's
    /// first one, the other side's when this one holds none, and nothing for an unbound action. A key
    /// beats a mouse button on the keyboard side.
    /// ⚠ A false <paramref name="readsKeyboard"/> skips the keyboard and the mouse: a pad-only
    /// splitscreen seat must not be told to press a key that does nothing for it.</summary>
    public static Binding? PromptBinding(IReadOnlyList<Binding> bindings, DeviceSide side, bool readsKeyboard)
    {
        Binding? key = null;
        Binding? mouse = null;
        Binding? pad = null;
        for (int i = 0; i < (bindings?.Count ?? 0); i++)
        {
            var binding = bindings![i];
            switch (binding.Control.Kind)
            {
                case ControlKind.Key when readsKeyboard:
                    key ??= binding;
                    break;
                case ControlKind.Mouse when readsKeyboard:
                    mouse ??= binding;
                    break;
                case ControlKind.Button:
                case ControlKind.Axis:
                case ControlKind.FullAxis:
                case ControlKind.Hat:
                    pad ??= binding;
                    break;
                default:
                    break;
            }
        }

        var keyboard = key ?? mouse;
        return side == DeviceSide.Pad ? pad ?? keyboard : keyboard ?? pad;
    }

    /// <summary>Takes this tick's two resolved halves and answers whether the side moved, which is a
    /// prompt's cue to recompose. Call it once a tick, after the seat has polled. A false
    /// <paramref name="readsKeyboard"/> pins the pad, since such a seat reads no key at all.
    /// </summary>
    public bool Observe(ActionSnapshot keyboardSide, ActionSnapshot padSide, bool readsKeyboard)
    {
        int keyboard = readsKeyboard ? ActiveCount(keyboardSide) : 0;
        int pad = ActiveCount(padSide);
        var was = Side;
        if (!readsKeyboard)
        {
            Side = DeviceSide.Pad;
        }
        else if (Side == DeviceSide.Keyboard
            ? Claims(pad, _padActive, keyboard)
            : Claims(keyboard, _keyboardActive, pad))
        {
            Side = Side == DeviceSide.Keyboard ? DeviceSide.Pad : DeviceSide.Keyboard;
        }

        _keyboardActive = keyboard;
        _padActive = pad;
        return Side != was;
    }

    // How many of the seat's actions one side is deflecting past PressTravel. A count rather than a
    // flag, so a press registers even while something else on the same side stays down.
    private static int ActiveCount(ActionSnapshot snapshot)
    {
        int active = 0;
        for (int i = 0; i < ActionCount; i++)
        {
            if (snapshot.Value((InputAction)i) >= PressTravel)
                active++;
        }

        return active;
    }

    // The other side takes the prompt on a press of its own, or when this side falls quiet while the
    // other is still held. Anything looser flickers the line while both are touched at once.
    private static bool Claims(int other, int otherBefore, int mine) =>
        other > otherBefore || (other > 0 && mine == 0);
}
