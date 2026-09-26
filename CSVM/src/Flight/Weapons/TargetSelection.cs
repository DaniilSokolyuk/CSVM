using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>One pilot's target selection: the sticky choice, the three cycles, the nearest queries
/// and the lifecycle (decoded in docs/org/targeting.md). One instance per pane; it OWNS its
/// <see cref="TargetPool"/>. The shape follows the original's exactly: an action handler only
/// mutates state and steps the list that ALREADY exists, while <see cref="Resolve"/> is the
/// per-frame pass that re-sorts and re-finds. No node dependency and no engine call (the one
/// breadcrumb logs at DEBUG), so a plain unit test drives the whole lifecycle with no tree.
/// ⚠ Nothing but death, an explicit clear and the pilot's own input ever changes the selection.
/// There is no range, line-of-sight or field-of-view gate in the decoded path; adding one would be
/// a port invention.</summary>
public sealed class TargetSelection
{
    /// <summary>The nearest-crosshairs cone, <c>cos(15°)</c> (<c>FUN_00488ce0</c>'s
    /// <c>0.9659258</c>): a hard 15° half-angle about the NOSE.</summary>
    public const float CrosshairConeCos = 0.9659258f;

    /// <summary>Nearest-crosshairs' hard maximum range, metres, the running best starts at 2000.0,
    /// so nothing past it can ever win.</summary>
    public const float CrosshairMaxRange = 2000f;

    /// <summary>Rebuilds to wait before reporting an empty pool in the count breadcrumb, a few
    /// seconds at 60 Hz, long enough for the AI spawner, the zeppelins and a generator's first drop
    /// to have happened.</summary>
    private const int EmptyPoolReport = 300;

    private readonly List<TargetRef> _ordered = new();  // the active cycle, in cycle order
    private readonly List<object> _attackers = new();   // the queue 0x24 walks backwards
    private object? _selected;                          // the SOURCE object, never a TargetRef
    private bool _countsLogged;                         // verification breadcrumb: the cycle sizes log once
    private int _emptyRebuilds;                         // rebuilds seen with an empty pool, for that breadcrumb
    // What separates a LOST selection from never having had one, which is the whole discriminator
    // NearestAfterKill turns on: the class the last Resolve ran under, and the death hook's own
    // drop. A class change leaves the two classes different, so it reads as "never had one".
    private TargetClass? _resolvedClass;
    private bool _lostToDeath;

    /// <summary>The pool this selector cycles over, rebuilt by <see cref="Rebuild"/>.</summary>
    public TargetPool Pool { get; } = new();

    /// <summary>A remake setting, OFF by default: a target that dies is replaced by the NEAREST
    /// live member of the current cycle by distance instead of by the cycle's head.
    /// ⚠ The head is the decoded rule and stays the default (docs/org/targeting.md). The head of
    /// Enemy/Objective is the nearest objective whenever one exists, so with this off every kill in
    /// a defence mission sends the pilot back to the objective. The mission-start acquire and the
    /// class keys keep the decoded order whichever way this stands.</summary>
    public bool NearestAfterKill { get; set; }

    /// <summary>Which cycle Next/Previous currently step, or <b>null for cleared</b>. Null is not
    /// "no target yet", it is `Target Nothing`, and it is why the clear STAYS cleared: with every
    /// class flag zero the original skips the collection pass entirely, so the auto-acquire cannot
    /// fire again until a class action is pressed. Starts at <see cref="TargetClass.Enemy"/>, which
    /// is the mission-start state (<c>0x00474a6b</c>) and therefore the auto-acquire.</summary>
    public TargetClass? ActiveClass { get; private set; } = TargetClass.Enemy;

    /// <summary>The selected target as resolved by the last <see cref="Resolve"/>, or null. A plain
    /// readable property on purpose (decision 13): Track Target's camera and any later AI-order
    /// consumer read this and nothing else.</summary>
    public TargetRef? Current { get; private set; }

    /// <summary>The active cycle in cycle order, as the last <see cref="Resolve"/> sorted it. What
    /// the HUD would page through and what the suite asserts an order against.</summary>
    public IReadOnlyList<TargetRef> Ordered => _ordered;

    /// <summary>The attacker queue, oldest first, see <see cref="NextEnemy"/>.</summary>
    public IReadOnlyList<object> Attackers => _attackers;

    /// <summary>The cycle's sort key for one candidate (<c>FUN_004bbd60</c>): −1 for a
    /// <see cref="TargetRef.SortsFirst"/> candidate, otherwise the 90° sector it sits in, measured
    /// against the plane's own basis. Godot's convention makes this a direct port (the X column IS
    /// the engine's <c>row0</c>, the Z column its <c>row2</c>). The quadrant is taken after a π/4
    /// rotation and has its 0 and 3 swapped, which puts <b>ahead</b> first and <b>right</b> last.</summary>
    /// <returns>−1 ahead of every sector, 0 ahead, 1 behind, 2 left, 3 right.</returns>
    public static int SectorKey(Vector3 toTarget, Basis basis, bool sortsFirst)
    {
        if (sortsFirst)
        {
            return -1;
        }

        float theta = Mathf.Atan2(toTarget.Dot(basis.Z), toTarget.Dot(basis.X)) + (Mathf.Pi / 4f);
        int q = (int)(Mathf.PosMod(theta, Mathf.Tau) * (2f / Mathf.Pi));
        return q == 0 ? 3 : q == 3 ? 0 : q;
    }

    /// <summary>Fills <see cref="Pool"/> and re-resolves, the whole per-frame pass in one call
    /// (<c>FUN_004b5fb0</c>). With the selection cleared the pool is left EMPTY rather than built and
    /// discarded, which is the original's own short-circuit and the mechanism behind the sticky
    /// clear. <paramref name="objectives"/> is the mission's flagged sites and
    /// <paramref name="selectedWeapon"/> the pilot's selected ordnance, which gates
    /// <paramref name="subParts"/>; both rules are <see cref="TargetPool.Rebuild"/>'s.</summary>
    public void Rebuild(AimCandidateSet scan, IReadOnlyList<AimCandidate>? subParts, int ownTeam,
        object? self, Vector3 position, Basis basis,
        IReadOnlyList<AimCandidate>? objectives = null, WeaponDef? selectedWeapon = null)
    {
        if (ActiveClass == null)
        {
            Pool.Clear();
            Resolve(position, basis);
            return;
        }

        Pool.Rebuild(scan, subParts, ownTeam, self, objectives, selectedWeapon);
        Resolve(position, basis);
        // Verification breadcrumb, once per selector: which cycles this session has anything in.
        // It waits for the first NON-EMPTY pool because the things that fill it (AI spawns, the
        // zeppelins, a generator drop) are built after the rigs are.
        if (!_countsLogged && (Pool.Count > 0 || ++_emptyRebuilds >= EmptyPoolReport))
        {
            _countsLogged = true;
            string acquired = Current is { } t && t.Name.Length > 0 ? t.Name : "-";
            Log.Debug("flight", $"target pool: enemy={Pool.Enemy.Count} ally={Pool.Ally.Count} nonAircraft={Pool.NonAircraft.Count} class={ActiveClass} acquired={acquired}");
        }
    }

    /// <summary>Re-sorts the active cycle against the plane's pose and re-finds the selection by
    /// ENTITY (<c>FUN_004b6490(current, 0)</c>): the matching entry, or the list HEAD when it is
    /// gone. That single rule is the whole lifecycle. A dead target, a target that left the class,
    /// and a class change all drop to the head of the current cycle, not to the dead entry's
    /// neighbour, because in every one of those cases the re-find simply fails.</summary>
    public void Resolve(Vector3 position, Basis basis)
    {
        _ordered.Clear();
        if (ActiveClass is not { } cls)
        {
            _selected = null;
            Current = null;
            _resolvedClass = null;
            _lostToDeath = false;
            return;
        }

        var live = Pool.Of(cls);
        for (int i = 0; i < live.Count; i++)
        {
            _ordered.Add(live[i]);
        }

        Sort(position, basis);
        bool lost = LostSelection(cls);
        int idx = IndexOfSelected();
        if (idx < 0 && _ordered.Count > 0)
        {
            // The re-resolve's head fallback: this is the auto-acquire AND the death switch. The
            // setting replaces it with the nearest entry only on a LOST selection, which leaves the
            // acquire and every class change on the decoded head.
            idx = NearestAfterKill && lost ? NearestIndex(position) : 0;
        }

        Current = idx >= 0 ? _ordered[idx] : null;
        _selected = Current?.Source;
        _resolvedClass = cls;
        _lostToDeath = false;
    }

    /// <summary>Next Enemy/Objective (<c>0x24</c>), the one action that consults the attacker
    /// queue first, walking it BACKWARDS from the end so the most recent shooter comes first. The
    /// queue's first entry, and an empty queue, fall through to an ordinary <c>+1</c> step.
    /// ⚠ CSVM ships every class action SILENT. <c>sg_switchtarget</c> is in the executable but in no
    /// shipped asset, so wiring <c>snd_select</c> to it would be an invention.</summary>
    public void NextEnemy()
    {
        if (ActiveClass != TargetClass.Enemy)
        {
            ActiveClass = TargetClass.Enemy;
            _selected = null;
            Step(1);
            return;
        }

        if (_attackers.Count > 0)
        {
            int idx = _selected == null ? -1 : _attackers.LastIndexOf(_selected);
            if (idx < 0)
            {
                _selected = _attackers[_attackers.Count - 1];
                return;
            }

            if (idx > 0)
            {
                _selected = _attackers[idx - 1];
                return;
            }
        }

        Step(1);
    }

    /// <summary>Next in <paramref name="cls"/> (<c>0x27</c>/<c>0x2a</c>, and <c>0x24</c>'s
    /// fall-through). Changing class clears the selection first, so the step lands on the head.</summary>
    public void Next(TargetClass cls) => StepIn(cls, 1);

    /// <summary>Previous in <paramref name="cls"/> (<c>0x25</c>/<c>0x28</c>/<c>0x2b</c>).</summary>
    public void Previous(TargetClass cls) => StepIn(cls, -1);

    /// <summary>Nearest in <paramref name="cls"/> (<c>0x26</c>/<c>0x29</c>/<c>0x2c</c>). Unlike
    /// Next/Previous it ALWAYS re-asserts its class and restarts the cycle.
    /// ⚠ "Nearest" is the HEAD OF THE CYCLE, not the nearest thing in space: nearest objective, else
    /// nearest ahead, else behind, else left, else right. A target 200 m off the left wing loses to
    /// one 900 m ahead.</summary>
    public void Nearest(TargetClass cls)
    {
        ActiveClass = cls;
        _selected = null;
        Step(0);
    }

    /// <summary>Select Target Nearest Crosshairs (<c>0x2d</c>, <c>FUN_00488db0</c>): its own scan
    /// over every class, ignoring the cycle entirely, capped at <see cref="CrosshairMaxRange"/>.
    /// Friendlies are included, which is how one keypress reaches an ally; the class is written back
    /// from what it found. ⚠ It scores against the NOSE, not the gun pipper, which is a separate
    /// velocity-derived point nothing in this path reads.</summary>
    /// <returns>False when nothing is in the cone, in which case the selection is left alone.</returns>
    public bool NearestCrosshairs(Vector3 position, Basis basis)
    {
        float best = CrosshairMaxRange;
        TargetRef? winner = null;
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            foreach (var t in Pool.Of(cls))
            {
                var v = t.Position - position;
                float d = v.Length();
                if (d <= 0f || v.Dot(basis.Z) > d * -CrosshairConeCos || d >= best)
                {
                    continue;
                }

                best = d;
                winner = t;
            }
        }

        if (winner is not { } pick)
        {
            return false;
        }

        ActiveClass = pick.Class;
        _selected = pick.Source;
        return true;
    }

    /// <summary>Target Nothing (<c>0x2e</c>): nulls the target AND every class flag. The second half
    /// is the point, it is what stops the auto-acquire firing again, so the clear STAYS cleared
    /// until a class action. Auto-acquire at start and the drop-to-head on death are the only two
    /// automatic transitions there are; a third ("nothing selected, so pick one") would silently
    /// break this action.</summary>
    public void Clear()
    {
        ActiveClass = null;
        _selected = null;
        Current = null;
        _ordered.Clear();
        _resolvedClass = null;
        _lostToDeath = false;
    }

    /// <summary>Point the selection at a named pool entry, matching <see cref="TargetRef.Name"/>
    /// case-insensitively and writing the class back from what it found (without that write-back the
    /// next <see cref="Resolve"/> drops a cross-class pin). This is <c>--target=&lt;name&gt;</c>'s
    /// seam; the original has no scripted input at all. Cycles are searched Enemy, Ally,
    /// Non-Aircraft, in collector order, so a duplicated name resolves the same way every run.</summary>
    /// <returns>False when no entry carries that name, in which case the selection is left alone.</returns>
    public bool Select(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            foreach (var t in Pool.Of(cls))
            {
                if (!string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ActiveClass = t.Class;
                _selected = t.Source;
                return true;
            }
        }

        return false;
    }

    /// <summary>Apply one <c>--target=</c> spec: <c>nearest</c>, <c>crosshair</c>, <c>next</c>,
    /// <c>none</c>, or a target's name for <see cref="Select"/>. The four words run the ordinary
    /// actions, so <c>nearest</c> means the HEAD of the cycle (see <see cref="Nearest"/>).
    /// ⚠ This sets the INITIAL selection and nothing more; calling it once is the caller's job. A
    /// flag that re-asserted itself per frame would freeze targeting in an interactive session.</summary>
    /// <returns>False when the spec selected nothing (an empty crosshair cone, an unknown name).</returns>
    public bool ApplyInitial(string spec, Vector3 position, Basis basis)
    {
        bool ok;
        switch (spec.ToLowerInvariant())
        {
            case "none":
                Clear();
                ok = true;
                break;
            case "nearest":
                Nearest(TargetClass.Enemy);
                ok = true;
                break;
            case "next":
                NextEnemy();
                ok = true;
                break;
            case "crosshair":
                ok = NearestCrosshairs(position, basis);
                break;
            default:
                ok = Select(spec);
                break;
        }

        // Re-resolve in the SAME frame rather than leaving the one-frame lag a keypress has: the
        // caller logs what was picked, and a --frames=N --screenshot run should not need one more
        // frame than it asked for to have the flag's target on screen.
        if (ok && ActiveClass != null)
        {
            Resolve(position, basis);
        }

        return ok;
    }

    /// <summary>Records a shooter that just hit this pilot, for <see cref="NextEnemy"/>'s queue
    /// (<c>FUN_004b9770</c>: an end insert, only for a shooter on a different, non-zero team). The
    /// queue survives the whole mission and is pruned only by death (<see cref="ForgetTarget"/>).
    /// ⚠ The de-duplication is an INFERENCE, not decode. <c>FUN_004bc1e0</c> before the insert is
    /// probably a remove-if-present, but that was not traced.</summary>
    public void RecordAttacker(object shooter)
    {
        _attackers.Remove(shooter);
        _attackers.Add(shooter);
    }

    /// <summary>The death/despawn hook (<c>FUN_004a64e0</c>): drops <paramref name="entity"/> from
    /// the attacker queue, and from the selection if it was the target. The next
    /// <see cref="Resolve"/> then picks the head of the current cycle, which is the original's
    /// switch-on-death. Calling this is optional for the switch itself, a dead candidate leaves the
    /// pool and the re-find fails anyway, but the queue prune is not.</summary>
    public void ForgetTarget(object entity)
    {
        _attackers.Remove(entity);
        if (ReferenceEquals(_selected, entity))
        {
            _selected = null;
            Current = null;
            _lostToDeath = true;
        }
    }

    // Whether this pass LOST a selection rather than never having had one: the entity it held is
    // gone from the cycle, or the death hook dropped it, under the same class the last pass ran.
    // A class change and the mission-start acquire both read as "never had one".
    private bool LostSelection(TargetClass cls) =>
        (_selected != null || _lostToDeath) && cls == _resolvedClass;

    // The nearest entry of the sorted cycle by distance from the plane, the one thing
    // NearestAfterKill reads. A tie takes the earlier entry, so the cycle's own order breaks it.
    private int NearestIndex(Vector3 position)
    {
        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < _ordered.Count; i++)
        {
            float d = (_ordered[i].Position - position).LengthSquared();
            if (d < bestSq)
            {
                bestSq = d;
                best = i;
            }
        }

        return best;
    }

    private void StepIn(TargetClass cls, int dir)
    {
        if (ActiveClass != cls)
        {
            // Clearing on a real class change makes the step land on a head. ⚠ The list this steps
            // is still LAST frame's, built under the old class; that one-frame lag is the original's
            // and self-heals on the next Resolve. Do not "fix" it with a synchronous rebuild.
            ActiveClass = cls;
            _selected = null;
        }

        Step(dir);
    }

    /// <summary><c>FUN_004b6490</c>: find the selection by entity, then step. Not found (or nothing
    /// selected) returns the FIRST entry whatever the direction; found wraps at both ends.</summary>
    private void Step(int dir)
    {
        if (_ordered.Count == 0)
        {
            _selected = null;
            return;
        }

        int idx = IndexOfSelected();
        if (idx < 0)
        {
            _selected = _ordered[0].Source;
            return;
        }

        if (dir == 0)
        {
            return;
        }

        idx = dir > 0 ? (idx + 1) % _ordered.Count
            : ((idx - 1) + _ordered.Count) % _ordered.Count;
        _selected = _ordered[idx].Source;
    }

    private int IndexOfSelected()
    {
        if (_selected == null)
        {
            return -1;
        }

        for (int i = 0; i < _ordered.Count; i++)
        {
            if (ReferenceEquals(_ordered[i].Source, _selected))
            {
                return i;
            }
        }

        return -1;
    }

    private void Sort(Vector3 position, Basis basis)
    {
        int n = _ordered.Count;
        if (n < 2)
        {
            return;
        }

        var keys = new (int Sector, float DistSq, int Index)[n];
        for (int i = 0; i < n; i++)
        {
            var v = _ordered[i].Position - position;
            keys[i] = (SectorKey(v, basis, _ordered[i].SortsFirst), v.LengthSquared(), i);
        }

        var byKey = new TargetRef[n];
        for (int i = 0; i < n; i++)
        {
            byKey[i] = _ordered[i];
        }

        // Sector first, then nearest inside it. The third key is the pre-sort index: a total order
        // keeps a tie (two parts of one zeppelin at the same range) from reordering between frames
        // and walking the cycle under the pilot.
        System.Array.Sort(keys, byKey, Comparer<(int Sector, float DistSq, int Index)>.Create(
            (x, y) => x.Sector != y.Sector ? x.Sector.CompareTo(y.Sector)
                : x.DistSq != y.DistSq ? x.DistSq.CompareTo(y.DistSq)
                : x.Index.CompareTo(y.Index)));
        _ordered.Clear();
        for (int i = 0; i < n; i++)
        {
            _ordered.Add(byKey[i]);
        }
    }
}
