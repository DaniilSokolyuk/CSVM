using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>One player's whole keymap: which controls fire which named action. Two players' maps are
/// independent, so one control means whatever each map says it means. Resolution goes through
/// <see cref="ResolveInto"/> once per tick, so two consumers asking in one tick cannot disagree.
/// ⚠ A control belongs to at most one action. Assigning one takes it off its previous owner and
/// names the loser (`FUN_005371d0`, `docs/org/input.md`). ⚠ A full axis sits on both actions of its pair or on neither, so
/// unbinding it from either row clears both.</summary>
public sealed class ActionMap
{
    private readonly Dictionary<InputAction, BindingSet> _sets = new();

    // Which modifiers this map holds each key under, rebuilt when the map changes. A bare binding
    // reads it to know which modifier it must stand down under, so E and Shift+E can be two actions
    // without making every context's bare keys die under a held modifier.
    private readonly Dictionary<int, KeyModifiers> _contested = new();
    private bool _contestedStale = true;

    /// <summary>The actions that currently hold at least one binding, in no particular order. An
    /// action absent here is unbound and resolves to nothing.</summary>
    public IEnumerable<InputAction> BoundActions => _sets.Keys;

    /// <summary>Whether two bindings name the same physical control, ignoring an axis deadzone.
    /// Deadzone is tuning, not identity, so rebinding an axis already on the action adjusts it
    /// instead of stacking a copy. A full axis covers both halves of its travel. It is therefore
    /// the same control as either half-axis binding on that axis of that device, and as any full
    /// axis there whatever its invert.</summary>
    public static bool SameControl(Binding left, Binding right)
    {
        if (left.Device != right.Device || left.Control.Index != right.Control.Index)
            return false;
        var leftKind = left.Control.Kind;
        var rightKind = right.Control.Kind;
        if ((leftKind == ControlKind.FullAxis && rightKind is ControlKind.Axis or ControlKind.FullAxis)
            || (rightKind == ControlKind.FullAxis && leftKind == ControlKind.Axis))
            return true;
        if (leftKind != rightKind)
            return false;
        return left.Control.Kind switch
        {
            ControlKind.Axis => left.Control.Sign == right.Control.Sign,
            ControlKind.Hat => left.Control.Direction == right.Control.Direction,
            ControlKind.Key => left.Control.Modifiers == right.Control.Modifiers,
            _ => true,
        };
    }

    /// <summary>Whether two actions hold one control without either taking it from the other, which
    /// the steal rule and a screen's steal prompt both skip. True when either is
    /// <see cref="InputAction.SkipCutscene"/>, which is read only while a cutscene or cinema plays.
    /// Its stick default shares the trigger with menu accept, and a rebind must not undo that.</summary>
    public static bool Shares(InputAction left, InputAction right) =>
        left != right && (left == InputAction.SkipCutscene || right == InputAction.SkipCutscene);

    /// <summary>Which modifiers this map holds that key under, which is what a bare binding on it
    /// must stand down under. Empty for a key no action names with a modifier.</summary>
    public KeyModifiers ContestedFor(int keyCode)
    {
        RebuildContested();
        return _contested.TryGetValue(keyCode, out var modifiers) ? modifiers : KeyModifiers.None;
    }

    /// <summary>Gives a control to an action, taking it off every action that held it. Returns those
    /// in enum order, so a screen can name each loss. A full axis goes onto both actions of the pair,
    /// the partner is not reported, and an action that takes no full axis throws. ⚠ Every owner, not
    /// the first: <see cref="Add"/> puts one control on two actions (a numpad snap-look diagonal).
    /// Stopping at the first leaves it on the other, which the original forbids (`FUN_005371d0`).
    /// </summary>
    public IReadOnlyList<InputAction> Assign(InputAction action, Binding binding)
    {
        InputAction? partner = null;
        if (binding.Control.Kind == ControlKind.FullAxis)
        {
            if (!AxisPairs.TakesFullAxis(action))
                throw new ArgumentException($"{action} is in no axis pair, so it cannot hold a full axis.", nameof(binding));
            partner = AxisPairs.PartnerOf(action);
        }

        var stolenFrom = new List<InputAction>();
        foreach (var pair in _sets)
        {
            if (pair.Key != action && pair.Key != partner && !Shares(action, pair.Key)
                && RemoveMatching(pair.Value, binding))
                stolenFrom.Add(pair.Key);
        }

        stolenFrom.Sort();
        Put(SetFor(action), binding);
        if (partner is { } other)
            Put(SetFor(other), binding);
        _contestedStale = true;
        return stolenFrom;
    }

    /// <summary>Gives a control to an action without taking it off anyone. The shipped defaults and
    /// a loaded file need this, since a numpad snap-look diagonal is deliberately on two actions. A
    /// full axis goes onto both actions of the pair, replacing a copy with another invert or
    /// deadzone. On the lever row it goes onto that row alone, and on any other it is refused.
    /// ⚠ Not for a rebinding screen; <see cref="Assign"/> is the only path that keeps the steal rule.</summary>
    public bool Add(InputAction action, Binding binding)
    {
        _contestedStale = true;
        if (binding.Control.Kind != ControlKind.FullAxis)
            return SetFor(action).Add(binding);
        if (!AxisPairs.TakesFullAxis(action))
            return false;

        bool added = !SetFor(action).Contains(binding);
        Put(SetFor(action), binding);
        if (AxisPairs.PartnerOf(action) is { } partner)
            Put(SetFor(partner), binding);
        return added;
    }

    /// <summary>Drops one control from one action, leaving every other action alone. A full axis
    /// leaves the pair's other action too, being one binding on both. This is the unbind a screen
    /// performs; it is not part of the steal rule.</summary>
    public bool Unassign(InputAction action, Binding binding)
    {
        _contestedStale = true;
        if (!_sets.TryGetValue(action, out var set))
            return false;

        bool removed = false;
        for (int i = set.Bindings.Count - 1; i >= 0; i--)
        {
            var held = set.Bindings[i];
            if (!SameControl(held, binding))
                continue;
            removed |= set.Remove(held);
            DropFromPartner(action, held);
        }

        return removed;
    }

    /// <summary>Unbinds every control on that action. A full axis it held leaves the pair's other
    /// action as well, as <see cref="Unassign"/> does.</summary>
    public void Clear(InputAction action)
    {
        _contestedStale = true;
        if (!_sets.TryGetValue(action, out var set))
            return;
        foreach (var held in set.Bindings)
            DropFromPartner(action, held);
        _sets.Remove(action);
    }

    public void Clear()
    {
        _contestedStale = true;
        _sets.Clear();
    }

    /// <summary>The controls bound to that action, in the order they were added, which is the order
    /// a screen lists them. Empty for an unbound action.</summary>
    public IReadOnlyList<Binding> Bindings(InputAction action) =>
        _sets.TryGetValue(action, out var set) ? set.Bindings : System.Array.Empty<Binding>();

    /// <summary>Every action that currently holds that control, in enum order. A screen calls this
    /// before assigning so it can name the losers ahead of committing the steal.
    /// ⚠ A list rather than one action, and there is no single-owner form on purpose: two actions
    /// deliberately share several shipped controls, and a caller that took the first owner would
    /// report one loss and perform two.</summary>
    public IReadOnlyList<InputAction> OwnersOf(Binding binding)
    {
        var owners = new List<InputAction>();
        foreach (var pair in _sets)
        {
            foreach (var held in pair.Value.Bindings)
            {
                if (SameControl(held, binding))
                {
                    owners.Add(pair.Key);
                    break;
                }
            }
        }

        owners.Sort();
        return owners;
    }

    /// <summary>Replaces this map's contents with <paramref name="source"/>'s, in place, so every
    /// reader already holding this object reads the new keymap without being rebuilt. A polling site
    /// hands the same map to two or three <see cref="PlayerActions"/>, and swapping the reference
    /// would leave those readers on the map the seat was constructed with.
    /// ⚠ <see cref="Add"/> rather than <see cref="Assign"/>: the shipped set deliberately puts one
    /// control on two actions, and a steal on the way in would silently undo the second.</summary>
    public void Fill(ActionMap source)
    {
        System.ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
            return;
        Clear();
        foreach (var pair in source._sets)
        {
            foreach (var binding in pair.Value.Bindings)
                Add(pair.Key, binding);
        }
    }

    /// <summary>An independent copy, for a screen whose edits may be cancelled.</summary>
    public ActionMap Clone()
    {
        var copy = new ActionMap();
        foreach (var pair in _sets)
            copy._sets[pair.Key] = pair.Value.Clone();
        return copy;
    }

    /// <summary>Reads every bound action out of this tick's hardware into the snapshot, reusing it
    /// rather than allocating one per tick. Unbound actions read as nothing. An absolute row reads
    /// its position, so an idle lever and an unbound one both read zero.</summary>
    public void ResolveInto(ActionSnapshot snapshot, IDeviceState state)
    {
        snapshot.Reset();
        RebuildContested();
        var gate = ModifierGate.Read(state, _contested);
        foreach (var pair in _sets)
        {
            snapshot.Store(pair.Key, AxisPairs.IsAbsolute(pair.Key)
                ? pair.Value.ResolveAbsolute(state, gate)
                : pair.Value.Resolve(state, gate, AxisPairs.SideOf(pair.Key)));
        }
    }

    /// <summary>A fresh snapshot of this tick, for a caller that keeps no snapshot of its own.
    /// </summary>
    public ActionSnapshot Resolve(IDeviceState state)
    {
        var snapshot = new ActionSnapshot();
        ResolveInto(snapshot, state);
        return snapshot;
    }

    private static bool RemoveMatching(BindingSet set, Binding binding)
    {
        bool removed = false;
        for (int i = set.Bindings.Count - 1; i >= 0; i--)
        {
            var held = set.Bindings[i];
            if (SameControl(held, binding))
                removed |= set.Remove(held);
        }

        return removed;
    }

    // A copy already on the set under another sign, invert or deadzone goes first. It is the same
    // control, and two of them would resolve the axis twice.
    private static void Put(BindingSet set, Binding binding)
    {
        if (set.Contains(binding))
            return;
        RemoveMatching(set, binding);
        set.Add(binding);
    }

    private void DropFromPartner(InputAction action, Binding held)
    {
        if (held.Control.Kind != ControlKind.FullAxis
            || AxisPairs.PartnerOf(action) is not { } partner
            || !_sets.TryGetValue(partner, out var other))
            return;
        for (int i = other.Bindings.Count - 1; i >= 0; i--)
        {
            if (other.Bindings[i].Control.Kind == ControlKind.FullAxis && SameControl(other.Bindings[i], held))
                other.Remove(other.Bindings[i]);
        }
    }

    private void RebuildContested()
    {
        if (!_contestedStale)
            return;

        _contestedStale = false;
        _contested.Clear();
        foreach (var pair in _sets)
        {
            foreach (var binding in pair.Value.Bindings)
            {
                if (binding.Control.Kind != ControlKind.Key || binding.Control.Modifiers == KeyModifiers.None)
                    continue;
                int key = binding.Control.Index;
                _contested[key] = (_contested.TryGetValue(key, out var held) ? held : KeyModifiers.None)
                    | binding.Control.Modifiers;
            }
        }
    }

    private BindingSet SetFor(InputAction action)
    {
        if (!_sets.TryGetValue(action, out var set))
        {
            set = new BindingSet();
            _sets[action] = set;
        }

        return set;
    }
}
