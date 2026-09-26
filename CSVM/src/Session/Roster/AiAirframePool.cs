using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>The aeroplanes a mission's waves are going to need, built before the mission starts
/// and kept out of the tree. A launch then costs the bind, the loadout and the tree insert instead
/// of the painted model, its collision hulls and its animators. One slot per airframe and livery,
/// ordered off the roster the waves launch from; a claim empties a slot and the owner refills it
/// on a quiet frame. A detached model has no body in the space and no node the anim runtime walks,
/// so a full pool costs nothing per frame.
/// ⚠ Holds models and hulls, never spawn indices. The spawn jitter draws its index at the launch,
/// and an index allocated ahead moves every stream behind it.</summary>
internal sealed class AiAirframePool
{
    private readonly Func<string, PaintScheme?, Prepared> _build;
    private readonly List<Slot> _slots = new();
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.Ordinal);

    public AiAirframePool(Func<string, PaintScheme?, Prepared> build) => _build = build;

    /// <summary>Aeroplanes ordered and not built yet. The load screen builds until this is
    /// zero, and a quiet frame in play takes one off it.</summary>
    public int Owed
    {
        get
        {
            int owed = 0;
            foreach (var slot in _slots)
            {
                owed += Math.Max(0, slot.Target - slot.Ready.Count);
            }

            return owed;
        }
    }

    /// <summary>Aeroplanes built and waiting to be claimed.</summary>
    public int Ready
    {
        get
        {
            int ready = 0;
            foreach (var slot in _slots)
            {
                ready += slot.Ready.Count;
            }

            return ready;
        }
    }

    /// <summary>Launches that took a prepared aeroplane.</summary>
    public int Claims { get; private set; }

    /// <summary>Launches that found an ordered slot empty and built in place.</summary>
    public int Misses { get; private set; }

    /// <summary>The slot an airframe in a livery belongs to. Built from the livery's own text, so
    /// two blocks of one airframe in different paint never claim each other's aeroplanes.</summary>
    public static string KeyFor(string planeName, PaintScheme? scheme) =>
        $"{planeName}|{scheme?.ToString() ?? "-"}";

    /// <summary>Adds <paramref name="depth"/> aeroplanes to what this airframe and livery holds,
    /// up to <paramref name="cap"/> in total. Called once per roster block a wave launches from,
    /// so two generators drawing the same block each get their own depth.</summary>
    public void Order(string planeName, PaintScheme? scheme, int depth, int cap)
    {
        if (depth <= 0)
        {
            return;
        }

        string key = KeyFor(planeName, scheme);
        if (!_byKey.TryGetValue(key, out var slot))
        {
            slot = new Slot(key, planeName, scheme);
            _byKey.Add(key, slot);
            _slots.Add(slot);
        }

        slot.Target = Math.Min(cap, slot.Target + depth);
    }

    /// <summary>Builds one owed aeroplane and answers whether it built anything. Slots are served
    /// in order order, so what a mission launches first is ready first.</summary>
    public bool BuildOne()
    {
        foreach (var slot in _slots)
        {
            if (slot.Ready.Count >= slot.Target)
            {
                continue;
            }

            slot.Ready.Add(_build(slot.PlaneName, slot.Scheme));
            return true;
        }

        return false;
    }

    /// <summary>Takes a finished aeroplane for this airframe and livery, or null where the slot is
    /// empty or was never ordered. The caller then builds in place, which is what every spawn did
    /// before this pool existed.</summary>
    public Prepared? Claim(string planeName, PaintScheme? scheme)
    {
        if (!_byKey.TryGetValue(KeyFor(planeName, scheme), out var slot))
        {
            return null;
        }

        if (slot.Ready.Count == 0)
        {
            Misses++;
            return null;
        }

        var taken = slot.Ready[0];
        slot.Ready.RemoveAt(0);
        Claims++;
        return taken;
    }

    /// <summary>Frees every unclaimed aeroplane and forgets every order. The models are outside
    /// the tree, so nothing else frees them and the session teardown never sees them.</summary>
    public void Discard()
    {
        foreach (var slot in _slots)
        {
            foreach (var prepared in slot.Ready)
            {
                prepared.Model.Free();
            }

            slot.Ready.Clear();
        }

        _slots.Clear();
        _byKey.Clear();
    }

    /// <summary>One aeroplane's spawn-independent parts. <see cref="Builder"/> comes along because
    /// the livery, the wing flares, the wreck and the damage panels are all read back off it after
    /// the launch, and a second builder would paint a second time.</summary>
    internal sealed record Prepared(
        PlaneBuilder Builder, Node3D Model, PropAnimator? Props, WingLightBlinker? WingLights,
        ControlSurfaceAnimator? Surfaces, PlaneCollider? Collider);

    private sealed class Slot
    {
        public Slot(string key, string planeName, PaintScheme? scheme)
        {
            Key = key;
            PlaneName = planeName;
            Scheme = scheme;
        }

        public string Key { get; }

        public string PlaneName { get; }

        public PaintScheme? Scheme { get; }

        public int Target { get; set; }

        public List<Prepared> Ready { get; } = new();
    }
}
