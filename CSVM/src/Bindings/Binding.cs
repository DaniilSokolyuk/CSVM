using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>What a control reads this tick, as both answers at once: held, and how far. A button
/// resolves 1 and an axis its raw travel once past the deadzone, so an axis can drive an action
/// written as digital and a button can drive one written as analogue. The original cannot express
/// either direction (`docs/org/input.md`), which is the reason the pair is here.
/// <see cref="Pressed"/> holds exactly when <see cref="Value"/> is above zero.</summary>
public readonly record struct ControlValue(bool Pressed, float Value)
{
    /// <summary>Nothing held, nothing deflected: what an unbound or absent control reads.</summary>
    public static ControlValue None => default;

    /// <summary>A key, a button or a hat direction: on is fully on.</summary>
    public static ControlValue Digital(bool pressed) => new(pressed, pressed ? 1f : 0f);

    /// <summary>An axis past its deadzone. A half axis passes the raw travel, deliberately not
    /// rescaled. No polling site this seam replaced rescaled, so a pad stick just past a deadzone
    /// reads what it moved. A full axis passes its rescaled side.</summary>
    public static ControlValue Analogue(float value) => new(value > 0f, value);
}

/// <summary>This tick's held modifiers, and which of them each key is contested at: a key another
/// action holds under Shift is contested at Shift, so its bare binding stands down while Shift is
/// held. Scoped to one keymap rather than global, because a context that binds Shift alone (the free
/// camera's boost) must keep firing its bare keys under it.
/// ⚠ Read the held set once per tick through <see cref="Read"/>. The default value knows nothing and
/// makes every key binding re-read the three modifier keys.</summary>
public readonly struct ModifierGate
{
    private static readonly KeyModifiers[] All = { KeyModifiers.Shift, KeyModifiers.Ctrl, KeyModifiers.Alt };

    private readonly IReadOnlyDictionary<int, KeyModifiers>? _contested;
    private readonly bool _known;

    private ModifierGate(KeyModifiers held, IReadOnlyDictionary<int, KeyModifiers>? contested)
    {
        Held = held;
        _contested = contested;
        _known = true;
    }

    /// <summary>The modifiers held entering this tick, meaningless until <see cref="Read"/> has
    /// filled it.</summary>
    public KeyModifiers Held { get; }

    /// <summary>The tick's gate over one map's contested table.</summary>
    public static ModifierGate Read(IDeviceState state, IReadOnlyDictionary<int, KeyModifiers>? contested)
    {
        ArgumentNullException.ThrowIfNull(state);
        var held = KeyModifiers.None;
        foreach (var modifier in All)
        {
            if (state.IsKeyDown(DeviceId.Keyboard, BindingControl.KeyCodeOf(modifier)))
                held |= modifier;
        }

        return new ModifierGate(held, contested);
    }

    /// <summary>The same gate with the held set taken now, for a caller resolving one binding
    /// outside a map's tick.</summary>
    public ModifierGate Fill(IDeviceState state) => _known ? this : Read(state, _contested);

    /// <summary>Which modifiers another action holds that key under.</summary>
    public KeyModifiers ContestedFor(int keyCode) =>
        _contested is { } table && table.TryGetValue(keyCode, out var modifiers) ? modifiers : KeyModifiers.None;
}

/// <summary>One control on one named device: everything needed to read a binding, and nothing
/// about which action holds it. Device identity is part of the value rather than a nullable field
/// beside it, so two pads with the same button layout are two different bindings.</summary>
public readonly record struct Binding(DeviceId Device, BindingControl Control)
{
    /// <summary>What this binding reads from that tick's hardware state, reading the modifier keys
    /// itself. <see cref="Resolve(IDeviceState, ModifierGate)"/> is the per-tick form.</summary>
    public ControlValue Resolve(IDeviceState state) => Resolve(state, default);

    /// <summary>What this binding reads from that tick's hardware state. An axis fires strictly
    /// past its deadzone, only on the half its sign names, and reports that travel raw. A hat
    /// direction reads only its own flag, independent of the hat's other three. A full axis reads
    /// nothing here, having no side to feed.</summary>
    public ControlValue Resolve(IDeviceState state, ModifierGate gate) => Resolve(state, gate, 0);

    /// <summary>The same read for the action on <paramref name="side"/> of an axis pair
    /// (<see cref="AxisPairs.SideOf"/>). Only a full axis uses the side. It fires strictly past its
    /// deadzone on that side's half, rescaled to read 0 at the deadzone edge and 1 at full travel.
    /// </summary>
    public ControlValue Resolve(IDeviceState state, ModifierGate gate, int side)
    {
        switch (Control.Kind)
        {
            case ControlKind.Key:
                return ControlValue.Digital(KeyHeld(state, gate));
            case ControlKind.Button:
                return ControlValue.Digital(state.IsButtonDown(Device, Control.Index));
            case ControlKind.Mouse:
                return ControlValue.Digital(state.IsMouseButtonDown(Device, Control.Index));
            case ControlKind.Hat:
                return ControlValue.Digital((state.HatState(Device, Control.Index) & Control.Direction) != 0);
            case ControlKind.FullAxis:
                return FullAxisSide(state.AxisValue(Device, Control.Index) * Control.Sign * side);
            default:
                float travel = state.AxisValue(Device, Control.Index) * Control.Sign;
                if (travel <= Control.Deadzone)
                    return ControlValue.None;
                return ControlValue.Analogue(travel > 1f ? 1f : travel);
        }
    }

    public override string ToString() => $"{Device}/{Control}";

    // Rescaled rather than raw, unlike a half axis. A flight stick past a small deadzone flies
    // linearly from zero, so the deadzone edge is the stick's centre.
    private ControlValue FullAxisSide(float travel)
    {
        if (travel <= Control.Deadzone)
            return ControlValue.None;
        float value = (travel - Control.Deadzone) / (1f - Control.Deadzone);
        return ControlValue.Analogue(value > 1f ? 1f : value);
    }

    // The modifier rule. A binding that NAMES modifiers wants exactly those held, so Shift+E stands
    // down under Ctrl+Shift; a bare one stands down only under a modifier another action holds the
    // same key under. Its own flag is excluded either way, or a binding on Shift could never fire.
    private bool KeyHeld(IDeviceState state, ModifierGate gate)
    {
        if (!state.IsKeyDown(Device, Control.Index))
            return false;

        var self = BindingControl.ModifierOf(Control.Index);
        var named = Control.Modifiers & ~self;
        var held = gate.Fill(state).Held & ~self;
        return named != KeyModifiers.None
            ? held == named
            : (held & gate.ContestedFor(Control.Index)) == KeyModifiers.None;
    }
}
