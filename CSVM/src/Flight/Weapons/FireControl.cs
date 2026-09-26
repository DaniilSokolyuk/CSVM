using System;
using System.Collections.Generic;
using CSVM.Flight.Audio;
using CSVM.Flight.Hud;
using CSVM.Utils;

namespace CSVM.Flight.Weapons;

/// <summary>The ammo counter every weapon slot carries, shared by the gun and pylon faces so
/// <see cref="AmmoSlots.Armed"/> can answer for either.</summary>
public interface IAmmoSlot
{
    int Ammo { get; set; }

    int Capacity { get; }
}

/// <summary>A firable gun group as <see cref="FireControl"/> sees it: the weapon (fire rate, loop
/// sound), a mutable per-group ammo counter, and how many muzzles the group alternates across.
/// <see cref="GunGroup"/> implements it; tests hand in fakes.</summary>
public interface IGunSlot : IAmmoSlot
{
    WeaponDef Weapon { get; }

    int MuzzleCount { get; }
}

/// <summary>One rocket pylon as <see cref="FireControl"/> sees it: the ordnance weapon and a
/// mutable per-pylon ammo counter. <see cref="Hardpoint"/> implements it; tests hand in fakes.</summary>
public interface IPylonSlot : IAmmoSlot
{
    WeaponDef Weapon { get; }
}

/// <summary>One sim tick's raw control state, polled by the caller. Held levels, not edges, all
/// edge detection (first-shot-on-press, one-rocket-per-pull, selector steps) lives inside
/// <see cref="FireControl"/>, where it is testable.</summary>
public struct FireInputs
{
    public bool FireHeld;          // gun trigger (Space / pad B; --fire ORs in adapter-side)
    public bool RocketHeld;        // rocket trigger (F / pad A)
    public bool GunSelectHeld;         // gun-group selector, forward (F3), key half only
    public bool RocketSelectHeld;      // hardpoint selector, forward (F5), key half only
    public bool GunSelectBackHeld;     // gun-group selector, backward (F4)
    public bool RocketSelectBackHeld;  // hardpoint selector, backward (F6)

    // The pad half of the two forward selectors (D-pad Right and Left), kept apart from the key
    // half because it carries BOTH directions: a tap steps forward and a hold steps back. A pad
    // has no free control for a second pair, and the original gives its joystick one direction per
    // class too (docs/org/input.md).
    public bool GunSelectPadHeld;
    public bool RocketSelectPadHeld;
}

/// <summary>The one spelling of "is this weapon slot armed?".</summary>
public static class AmmoSlots
{
    /// <summary>Whether the slot can fire: it holds rounds, or <c>--infinite-ammo</c> makes every
    /// slot count as armed. Every caller passes its live flag, so whether a site honours
    /// <c>InfiniteAmmo</c> is visible in the argument rather than in a private respelling.</summary>
    public static bool Armed(this IAmmoSlot slot, bool infinite) => infinite || slot.Ammo > 0;
}

/// <summary>What one <see cref="FireControl.Step"/> decided, for the caller to perform against the
/// engine. A single persistent instance is reused every tick (cleared on entry) so the 60 Hz path
/// allocates nothing, read it before the next Step.</summary>
public sealed class FireOutcome
{
    /// <summary>Rounds to spawn this tick, in firing order: gun-slot index + the muzzle index the
    /// group's rotation picked. Usually 0–1 entries; more when a slow frame spans several fire
    /// intervals. The caller resolves the muzzle node's transform and spawns.</summary>
    public List<(int Group, int Muzzle)> GunShots { get; } = new();

    /// <summary>The pylon index to launch a rocket from this tick, or -1 for none.</summary>
    public int RocketPylon { get; set; } = -1;

    /// <summary>The firing loop should be sounding this tick (the selected group is firing with
    /// ammo). Distinct from <see cref="GunLoopSound"/> because a gun without a looped sound still
    /// wants the loop state on, the caller's start call is then a no-op, exactly as before the
    /// extraction.</summary>
    public bool GunLoopWanted { get; set; }

    /// <summary>The looped sound the firing group carries, when <see cref="GunLoopWanted"/>.</summary>
    public string? GunLoopSound { get; set; }

    /// <summary>Sound the empty-clip cue: the selected gun ran dry with every group spent (once,
    /// re-armed by a real shot).</summary>
    public bool GunDryCue { get; set; }

    /// <summary>Sound the empty-clip cue: a rocket pull with every pylon empty (once, re-armed by
    /// a launch or a refill).</summary>
    public bool RocketDryCue { get; set; }

    internal void Clear()
    {
        GunShots.Clear();
        RocketPylon = -1;
        GunLoopWanted = false;
        GunLoopSound = null;
        GunDryCue = false;
        RocketDryCue = false;
    }
}

/// <summary>The fire-control state machine behind <see cref="Airframe.FlightController"/>:
/// trigger edges, per-group fire-rate accumulators, muzzle rotation, ammo draw-down, the two weapon
/// selectors with their on-empty auto-advance, the rocket launch gate and both dry-clip cues, as a
/// plain engine-free class. <see cref="Step"/> consumes raw held inputs and returns spawn/sound
/// <b>decisions</b> (<see cref="FireOutcome"/>); the caller performs them (muzzle transforms,
/// <see cref="ProjectilePool"/>, <see cref="FlightAudio"/>). Ammo mutates through the slot views so
/// a decision and its state can never diverge mid-tick, the gauges keep reading the same
/// <see cref="Loadout"/> counters. <see cref="WeaponCursor"/> is this module's internal index math.
/// Proven in <c>CSVM.Tests/FireControlTests</c>; the in-engine <c>weapons-fire</c> suite covers only
/// the mounting census.</summary>
public sealed class FireControl
{
    private readonly IReadOnlyList<IGunSlot> _guns;
    private readonly IReadOnlyList<IPylonSlot> _pylons;
    private readonly GunState[] _states;      // per gun slot, in _guns order
    private readonly int[] _pylonStep;        // pylon list positions, in the order the cursor walks
    private readonly int[] _pylonRank;        // inverse of _pylonStep: list position -> its step place
    private readonly Func<int, int> _stepAmmo; // step place -> that pylon's ammo, for WeaponCursor
    private readonly Func<int, int> _gunAmmo;  // gun slot -> its ammo, for WeaponCursor
    private readonly FireOutcome _outcome = new();
    // The pad's two selector buttons, each carrying both directions of its class: tap forward,
    // hold back. The threshold is the one every dual-purpose pad control here splits on.
    private readonly TapHoldButton _gunSelPad = new(TapHoldButton.PadHoldSeconds);
    private readonly TapHoldButton _rocketSelPad = new(TapHoldButton.PadHoldSeconds);

    private bool _firePrev;          // previous tick's gun trigger (immediate first shot on press)
    private bool _gunSelPrev;        // edge detection for the forward gun-selector button
    private bool _gunSelBackPrev;    // edge detection for the backward gun-selector button
    private bool _rocketSelPrev;     // edge detection for the forward hardpoint-selector button
    private bool _rocketSelBackPrev; // edge detection for the backward hardpoint-selector button
    private bool _rocketFirePrev;    // previous tick's rocket trigger (one rocket per discrete pull)
    private bool _rocketDryWarned;   // the all-pylons-empty cue has already sounded
    private float _rocketCooldown;   // s until the next rocket may launch (FIRE_RATE gate)
    private int _gunSel;             // gun selector: 0-based slot that fires (only ONE at a time)
    private int _selectedPylon;      // the pylon the hardpoint selector points at, and rockets fire from

    /// <param name="pylonStepOrder">Pylon list positions in the order the hardpoint cursor walks
    /// them, or null for the list's own order. The caller supplies it because the pylon list is
    /// built in whatever order the fit was assembled, which need not be the order the mounts sit
    /// along the wing; stepping the list itself then sends the gauge arrow back and forth across
    /// the belt instead of along it. Must be a permutation of 0..<c>pylons.Count</c>-1.</param>
    public FireControl(IReadOnlyList<IGunSlot> guns, IReadOnlyList<IPylonSlot> pylons,
        bool autoFireRockets, bool infiniteAmmo, int initialGunSelect = 0,
        IReadOnlyList<int>? pylonStepOrder = null)
    {
        _guns = guns;
        _pylons = pylons;
        AutoFireRockets = autoFireRockets;
        InfiniteAmmo = infiniteAmmo;
        _states = new GunState[guns.Count];
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i] = new GunState();
        }
        _gunSel = guns.Count > 0 ? Math.Clamp(initialGunSelect, 0, guns.Count - 1) : 0;
        _pylonStep = BuildStepOrder(pylons.Count, pylonStepOrder);
        _pylonRank = new int[pylons.Count];
        for (int k = 0; k < _pylonStep.Length; k++)
        {
            _pylonRank[_pylonStep[k]] = k;
        }
        _stepAmmo = k => _pylons[_pylonStep[k]].Ammo;
        _gunAmmo = i => _guns[i].Ammo;
        _selectedPylon = FirstPylon();
    }

    /// <summary>The firing gun slot, what the trigger fires, the gauge arrow marks and the
    /// reticle aims for.</summary>
    public int GunSel => _gunSel;

    /// <summary>The selected pylon, what the hardpoint selector points at and the rocket trigger
    /// launches from.</summary>
    public int SelectedPylon => _selectedPylon;

    /// <summary>--fire-rockets / the weapon lab's auto toggle: a held rocket trigger auto-repeats
    /// at the cooldown cap instead of one launch per discrete pull. Runtime-mutable, the lab
    /// flips it live when switching banks, so the adapter mirrors it in every Step.</summary>
    public bool AutoFireRockets { get; set; }

    /// <summary>--infinite-ammo / the lab's toggle: every slot always counts as armed and no
    /// counter depletes. Runtime-mutable, mirrored like <see cref="AutoFireRockets"/>.</summary>
    public bool InfiniteAmmo { get; set; }

    /// <summary>Point the gun selector at a slot (0-based, clamped), the weapon lab's programmatic
    /// twin of F3, which only cycles.</summary>
    public void SelectGunGroup(int index) =>
        _gunSel = _guns.Count > 0 ? Math.Clamp(index, 0, _guns.Count - 1) : 0;

    /// <summary>Point the hardpoint selector at a pylon (0-based, clamped). Unlike H this lands on
    /// an EMPTY pylon too (the lab picks a mount to look at, not a mount to fire); the firing
    /// path's own armed scan still advances off it on the next pull.</summary>
    public void SelectPylon(int index) =>
        _selectedPylon = _pylons.Count > 0 ? Math.Clamp(index, 0, _pylons.Count - 1) : 0;

    /// <summary>One sim tick: selector edges, then the gun fire clocks, then the rocket gate, the
    /// order the flight sim always ran them in. Mutates slot ammo; returns the reused
    /// <see cref="FireOutcome"/> with everything the caller must perform.</summary>
    public FireOutcome Step(float dt, in FireInputs input)
    {
        _outcome.Clear();
        StepSelectors(dt, input);
        StepGuns(dt, input.FireHeld);
        StepRockets(dt, input.RocketHeld);
        return _outcome;
    }

    /// <summary>Full reload (respawn): every owned slot back to capacity, fire clocks and muzzle
    /// rotations reset, dry warnings re-armed, the pylon cursor back to the first pylon in step
    /// order (it doubles as the firing cursor). The gun pick deliberately persists across a
    /// respawn.</summary>
    public void Refill()
    {
        foreach (var g in _guns)
        {
            g.Ammo = g.Capacity;
        }
        foreach (var st in _states)
        {
            st.Accum = 0f;
            st.Warned = false;
            st.NextMuzzle = 0;
        }
        foreach (var p in _pylons)
        {
            p.Ammo = p.Capacity;
        }
        _rocketCooldown = 0f;
        _selectedPylon = FirstPylon();
        _rocketFirePrev = false;
        _rocketDryWarned = false;
    }

    // Validates the caller's walk order, or lays down the list's own order when none was given.
    // A malformed permutation is a loud error: silently falling back would leave the cursor
    // stepping an order nobody chose, which is the defect this parameter exists to fix.
    private static int[] BuildStepOrder(int count, IReadOnlyList<int>? order)
    {
        var step = new int[count];
        if (order == null)
        {
            for (int i = 0; i < count; i++)
            {
                step[i] = i;
            }
            return step;
        }
        if (order.Count != count)
        {
            throw new ArgumentException(
                $"pylon step order has {order.Count} entries for {count} pylons", nameof(order));
        }
        var seen = new bool[count];
        for (int k = 0; k < count; k++)
        {
            int idx = order[k];
            if (idx < 0 || idx >= count || seen[idx])
            {
                throw new ArgumentException(
                    $"pylon step order is not a permutation of 0..{count - 1} (entry {k} is {idx})",
                    nameof(order));
            }
            seen[idx] = true;
            step[k] = idx;
        }
        return step;
    }

    // Moves each weapon selector on the rising edge of one of its two keys to the adjacent armed
    // slot in that direction (one active at a time, skipping empties). Each direction has its own
    // edge, so holding both steps one way and straight back and the cursor stays put. The pad's
    // single button per class carries both directions instead, split by how long it is held. Both
    // cursors also auto-advance on their own when the selected slot empties (in the gun/rocket
    // steps).
    private void StepSelectors(float dt, in FireInputs input)
    {
        bool gunSel = input.GunSelectHeld;
        bool gunSelBack = input.GunSelectBackHeld;
        if (gunSel && !_gunSelPrev)
        {
            MoveGunSelector(true);
        }
        if (gunSelBack && !_gunSelBackPrev)
        {
            MoveGunSelector(false);
        }
        _gunSelPrev = gunSel;
        _gunSelBackPrev = gunSelBack;

        bool rocketSel = input.RocketSelectHeld;
        bool rocketSelBack = input.RocketSelectBackHeld;
        if (rocketSel && !_rocketSelPrev)
        {
            MovePylonSelector(true);
        }
        if (rocketSelBack && !_rocketSelBackPrev)
        {
            MovePylonSelector(false);
        }
        _rocketSelPrev = rocketSel;
        _rocketSelBackPrev = rocketSelBack;

        // The pad last, so a frame carrying a key edge as well reads in the order a key pair
        // would. A hold is spent on the frame it fires and does not also tap on release
        // (TapHoldButton), which is what keeps one long press from stepping forward and back.
        switch (_gunSelPad.Step(input.GunSelectPadHeld, dt))
        {
            case TapHold.Tap:
                MoveGunSelector(true);
                break;
            case TapHold.Hold:
                MoveGunSelector(false);
                break;
        }
        switch (_rocketSelPad.Step(input.RocketSelectPadHeld, dt))
        {
            case TapHold.Tap:
                MovePylonSelector(true);
                break;
            case TapHold.Hold:
                MovePylonSelector(false);
                break;
        }
    }

    // One step of the gun selector, forward or back, over the armed slots. Fewer than two slots
    // means there is nothing to step to, which is the gate every caller shares.
    private void MoveGunSelector(bool forward)
    {
        if (_guns.Count > 1)
        {
            _gunSel = forward
                ? WeaponCursor.NextSelectable(_guns.Count, _gunAmmo, _gunSel, InfiniteAmmo)
                : WeaponCursor.PrevSelectable(_guns.Count, _gunAmmo, _gunSel, InfiniteAmmo);
        }
    }

    // The same for the hardpoint cursor, over the step order rather than the list order.
    private void MovePylonSelector(bool forward)
    {
        if (_pylons.Count > 1)
        {
            int rank = _pylonRank[_selectedPylon];
            _selectedPylon = _pylonStep[forward
                ? WeaponCursor.NextSelectable(_pylonStep.Length, _stepAmmo, rank, InfiniteAmmo)
                : WeaponCursor.PrevSelectable(_pylonStep.Length, _stepAmmo, rank, InfiniteAmmo)];
        }
    }

    // Advances every gun slot's fire clock: while the trigger is held, the selected slot
    // emits shots at its `FIRE_RATE` (alternating muzzles so the slot's total rate equals it),
    // drawing from its own ammo counter. When the selected slot runs dry the selection
    // auto-advances to the next slot with ammo (the moment it empties); the empty-clip cue sounds
    // only once every slot is spent.
    private void StepGuns(float dt, bool fire)
    {
        bool wantLoop = false;
        string? loopSound = null;
        for (int gi = 0; gi < _guns.Count; gi++)
        {
            var g = _guns[gi];
            var st = _states[gi];
            // Only the selected gun slot fires, one at a time (the original's behaviour).
            bool selected = gi == _gunSel;
            if (!fire || !selected || g.Weapon.FireRate <= 0f || g.MuzzleCount == 0)
            {
                st.Accum = 0f;
                continue;
            }
            float interval = 1f / g.Weapon.FireRate;
            if (!_firePrev)
            {
                st.Accum = interval; // the first shot leaves the barrel the instant the trigger goes down
            }
            st.Accum += dt;
            if (g.Armed(InfiniteAmmo))
            {
                wantLoop = true;
                loopSound ??= g.Weapon.LoopedSoundName;
            }
            while (st.Accum >= interval)
            {
                st.Accum -= interval;
                if (g.Armed(InfiniteAmmo))
                {
                    _outcome.GunShots.Add((gi, st.NextMuzzle % g.MuzzleCount));
                    st.NextMuzzle++;
                    if (!InfiniteAmmo)
                    {
                        g.Ammo--;
                    }
                    st.Warned = false; // it fired a real round, re-arm the dry warning
                }
                else
                {
                    // The selected slot just ran dry, switch to the next slot that still has ammo
                    // the moment it empties, not on the next trigger pull. Only when no slot has
                    // ammo left does the dry cue sound.
                    int next = WeaponCursor.NextArmed(_guns.Count, _gunAmmo, _gunSel, InfiniteAmmo);
                    if (next >= 0)
                    {
                        _gunSel = next;
                    }
                    else if (!st.Warned)
                    {
                        st.Warned = true;
                        _outcome.GunDryCue = true;
                    }
                    st.Accum = 0f;
                    break;
                }
            }
        }
        _outcome.GunLoopWanted = wantLoop;
        _outcome.GunLoopSound = wantLoop ? loopSound : null;
        _firePrev = fire;
    }

    // The rocket gate: one launch per discrete trigger pull, drawn from the selected
    // pylon (it drains fully, then the cursor auto-advances to the next armed pylon the instant it
    // empties), gated by the weapon's `FIRE_RATE` cooldown. A pull with every pylon empty
    // sounds the dry cue once, the cue check runs BEFORE the cooldown gate, so a dry pull is
    // never swallowed by a hot cooldown.
    private void StepRockets(float dt, bool held)
    {
        if (_pylons.Count == 0)
        {
            return;
        }
        if (_rocketCooldown > 0f)
        {
            _rocketCooldown -= dt;
        }
        bool fire = AutoFireRockets || held;
        // A human pull fires one rocket; holding does not auto-repeat. Only --fire-rockets (soak
        // runs) auto-repeats, and either way the FIRE_RATE cooldown caps the launch rate.
        bool pull = AutoFireRockets ? fire : (fire && !_rocketFirePrev);
        _rocketFirePrev = fire;
        if (!pull)
        {
            return;
        }
        // The selected pylon while it still has ordnance, else the next armed pylon scanning from
        // it and wrapping, the cursor self-heals if it was somehow left on a spent pylon.
        int idx = NextArmedPylon(_selectedPylon, InfiniteAmmo);
        if (idx < 0)
        {
            if (!_rocketDryWarned)
            {
                _rocketDryWarned = true;
                _outcome.RocketDryCue = true;
            }
            return;
        }
        _selectedPylon = idx;
        if (_rocketCooldown > 0f)
        {
            return;
        }
        _rocketDryWarned = false;
        var hp = _pylons[idx];
        _outcome.RocketPylon = idx;
        if (!InfiniteAmmo)
        {
            hp.Ammo--;
            // Advance the moment the selected pylon empties, not on the next trigger pull, so
            // the gauge arrow leaves the spent pylon straight away; -1 (all empty) leaves it put
            // so the next pull sounds the dry cue.
            int next = NextArmedPylon(_selectedPylon, false);
            if (next >= 0)
            {
                _selectedPylon = next;
            }
        }
        _rocketCooldown = hp.Weapon.FireRate > 0f ? 1f / hp.Weapon.FireRate : 1f;
    }

    // The pylon the cursor starts and returns to: first in step order, not first in the list.
    private int FirstPylon() => _pylonStep.Length > 0 ? _pylonStep[0] : 0;

    // NextArmed over the step order, in and out in list positions. -1 when every pylon is spent.
    private int NextArmedPylon(int from, bool infinite)
    {
        int k = WeaponCursor.NextArmed(_pylonStep.Length, _stepAmmo, _pylonRank[from], infinite);
        return k < 0 ? -1 : _pylonStep[k];
    }

    private sealed class GunState
    {
        public float Accum;      // fire-clock accumulator, s
        public int NextMuzzle;   // muzzle-rotation cursor (monotonic; modulo picks the muzzle)
        public bool Warned;      // the all-slots-spent cue has sounded for this slot's dry burst
    }
}
