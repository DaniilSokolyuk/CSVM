using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>The bindings one action holds, resolved together. Any one of them firing fires the
/// action, which is the original's four-slots-ORed rule (`FUN_00537530`, `docs/org/input.md`) kept
/// on purpose and freed of its four fixed typed slots. A list, not slots: a player may bind a key,
/// two buttons on different pads and a trigger to the same action, and the count is theirs.
/// ⚠ This type owns no action name and no device lookup. The action naming is the map's
/// (`ActionMap`), and resolving an identity to live hardware is the device registry's.</summary>
public sealed class BindingSet
{
    private readonly List<Binding> _bindings;

    public BindingSet() => _bindings = new List<Binding>();

    public BindingSet(IEnumerable<Binding> bindings)
    {
        _bindings = new List<Binding>();
        foreach (var binding in bindings)
            Add(binding);
    }

    public int Count => _bindings.Count;

    /// <summary>The bindings in the order they were added, which is the order a screen lists them.
    /// </summary>
    public IReadOnlyList<Binding> Bindings => _bindings;

    /// <summary>Adds a binding unless the set already holds it. Duplicates are dropped rather than
    /// rejected: the same control twice on one action is a no-op, not an error a rebinding screen
    /// has to report.</summary>
    public bool Add(Binding binding)
    {
        if (_bindings.Contains(binding))
            return false;
        _bindings.Add(binding);
        return true;
    }

    public bool Contains(Binding binding) => _bindings.Contains(binding);

    /// <summary>Drops one binding. This is the half of the steal rule that runs on the action
    /// losing the control; putting it on the new owner is the map's job.</summary>
    public bool Remove(Binding binding) => _bindings.Remove(binding);

    public void Clear() => _bindings.Clear();

    /// <summary>An independent copy, for an editing screen that may be cancelled.</summary>
    public BindingSet Clone() => new(_bindings);

    /// <summary>What the action reads this tick: held if any binding is held, and the deepest
    /// deflection any of them reports. Deepest rather than first, so a half-pressed trigger cannot
    /// beat a fully held button that is bound to the same action.</summary>
    public ControlValue Resolve(IDeviceState state) => Resolve(state, default);

    /// <summary>The same read against a tick's <see cref="ModifierGate"/>, which is how a key
    /// binding learns what its map holds under a modifier.</summary>
    public ControlValue Resolve(IDeviceState state, ModifierGate gate) => Resolve(state, gate, 0);

    /// <summary>The same read for the action on <paramref name="side"/> of an axis pair, which is
    /// the side a full-axis binding in this set feeds (<see cref="AxisPairs.SideOf"/>).</summary>
    public ControlValue Resolve(IDeviceState state, ModifierGate gate, int side)
    {
        bool pressed = false;
        float value = 0f;
        foreach (var binding in _bindings)
        {
            var read = binding.Resolve(state, gate, side);
            pressed |= read.Pressed;
            if (read.Value > value)
                value = read.Value;
        }

        return new ControlValue(pressed, value);
    }

    /// <summary>The same read for an absolute row, each binding read through
    /// <see cref="Binding.ResolveAbsolute"/>, the furthest position winning.</summary>
    public ControlValue ResolveAbsolute(IDeviceState state, ModifierGate gate)
    {
        bool pressed = false;
        float value = 0f;
        foreach (var binding in _bindings)
        {
            var read = binding.ResolveAbsolute(state, gate);
            pressed |= read.Pressed;
            if (read.Value > value)
                value = read.Value;
        }

        return new ControlValue(pressed, value);
    }
}
