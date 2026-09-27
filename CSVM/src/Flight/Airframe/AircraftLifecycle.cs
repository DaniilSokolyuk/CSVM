using System;

namespace CSVM.Flight.Airframe;

/// <summary>What the GROUND-IMPACT transition asks the aircraft node to perform, as one value with
/// no optional parts: a caller that performs half of it leaves a crashed plane with no boom and no
/// camera cut, so the whole crash is one struct rather than four remembered statements.
/// <see cref="Occurred"/> false is the guard's answer, and every other field is then meaningless.
/// The wreck landing arm reports no death, because the death was reported at the kill.</summary>
public readonly record struct CrashOutcome
{
    /// <summary>Whether the transition happened at all. False when the guard refused it: this
    /// aircraft is already crashed and is not the falling wreck reaching the ground.</summary>
    public bool Occurred { get; init; }

    /// <summary>Whether this was the falling wreck's own landing rather than a fresh crash, which
    /// is the one re-entry the guard allows. It is the second of that death's two anim slots.</summary>
    public bool WreckLanding { get; init; }

    /// <summary>The def the struck surface selected off the crash table, or null when no crash rig
    /// is bound or the table has no playable slot for that surface.</summary>
    public string? CrashDef { get; init; }

    /// <summary>Whether the caller owes the once-per-death shutdown (gun loop, engine loops, the
    /// wind-down cue, the plume). False on a wreck landing, which shut them down at the kill.</summary>
    public bool EndFlightSystems { get; init; }

    /// <summary>Whether the caller owes the hard cut to the authored crash camera. False on a wreck
    /// landing: re-cutting would swing the camera off the fall it was framing.</summary>
    public bool CutCamera { get; init; }

    /// <summary>Whether the caller owes one <c>Downed</c> report. One death reports once, so a
    /// wreck landing raises nothing.</summary>
    public bool Downed { get; init; }

    /// <summary>The killer to name in that report, or null for terrain, a mid-air and every other
    /// unattributed cause.</summary>
    public int? Killer { get; init; }
}

/// <summary>What the DEATH transition asks the aircraft node to perform, on the same one-value terms
/// as <see cref="CrashOutcome"/>. The three owed effects are always due on a death that happened;
/// they are fields rather than an implied rule so both transitions perform in one shape.</summary>
public readonly record struct DestroyOutcome
{
    /// <summary>Whether the transition happened. False when the guard refused it, because this
    /// aircraft is already out of the fight and one death reports once.</summary>
    public bool Occurred { get; init; }

    /// <summary>The airframe's own destroy def to start over the visible wreck, or null when no
    /// crash rig is bound or the airframe has no such def, which hides the hull instead.</summary>
    public string? DestroyDef { get; init; }

    /// <summary>Whether the hull now flies itself down under the flight model, which is what a
    /// bound destroy def leaves behind until its own callback stops it.</summary>
    public bool WreckFalling { get; init; }

    /// <summary>Whether the caller owes the once-per-death shutdown. Always true on a death.</summary>
    public bool EndFlightSystems { get; init; }

    /// <summary>Whether the caller owes the cut to the crash camera. Always true on a death.</summary>
    public bool CutCamera { get; init; }

    /// <summary>Whether the caller owes one <c>Downed</c> report. Always true on a death.</summary>
    public bool Downed { get; init; }

    /// <summary>The killer to name in that report, or null for an unattributed kill.</summary>
    public int? Killer { get; init; }
}

/// <summary>The states one aircraft moves between and the rules that move it: in play, crashed,
/// destroyed with its wreck still flying, inert, and back to spawned. It owns those flags and the
/// spawn/respawn timers, and every transition RETURNS what happened rather than performing it, so
/// the rules are assertable with no engine in the process. The aircraft node keeps <c>InPlay</c> and
/// <c>Crashed</c> as forwards onto this, and performs what a transition reports.
/// ⚠ Crashed is one-way until <see cref="Respawn"/>: a crashed aircraft cannot crash again, and the
/// falling wreck reaching the ground is the single exception.</summary>
public sealed class AircraftLifecycle
{
    /// <summary>Seconds a crash sits on the crash cam before an unattended run respawns itself, and
    /// the fallback for a session that armed <see cref="AutoRespawnAfter"/> with nothing.</summary>
    public const float AutoRespawnDelay = 1.5f;

    /// <summary>Seconds after a carrier drop that an AI's ground-blow response runs at the 0.15
    /// multiplier: the aircraft is falling towards water on purpose and must not fight it.</summary>
    public const float CarrierDropGroundBlow = 2.5f;

    private float _autoRespawnIn;
    private float _collisionGrace;
    private float _postDropGroundBlow;
    private bool _crashed;
    private bool _destroyed;
    private bool _wreckFalling;
    private bool _inert;
    private bool _parked;

    /// <summary>The crash-def vector the struck surface id indexes, bound with the rest of the
    /// crash rig; null leaves every crash without an authored def.</summary>
    public SurfaceDefTable? CrashDefs { get; set; }

    /// <summary>The def the last crash selected off <see cref="CrashDefs"/>, null before any crash.
    /// The prefix names the family, so a suite can pin which one fired.</summary>
    public string? LastCrashDef { get; private set; }

    /// <summary>Seconds a crash waits before respawning itself, or null for manual respawn only.
    /// The session arms it per rig; a scripted run respawns on <see cref="AutoRespawnDelay"/>
    /// regardless.</summary>
    public float? AutoRespawnAfter { get; set; }

    /// <summary>Whether a crash waits for the pilot's Fire Guns press once
    /// <see cref="AutoRespawnAfter"/> has run. It is the lobby's Auto Respawn turned off
    /// (docs/org/multiplayer-scoring.md).</summary>
    public bool RespawnOnFire { get; set; }

    /// <summary>Frozen at the impact point, waiting for a respawn.</summary>
    public bool Crashed => _crashed;

    /// <summary>The hull is spent and its destroy def is playing: the shot-down half of
    /// <see cref="Crashed"/>, which also covers a live aircraft flown into the world.</summary>
    public bool Destroyed => _destroyed;

    /// <summary>That wreck is still falling under the flight model, on its way to its
    /// ground-impact def.</summary>
    public bool WreckFalling => _wreckFalling;

    /// <summary>Built but held completely out of the session: not stepped, drawn or collidable.</summary>
    public bool Inert => _inert;

    /// <summary>Held out of the session by a cutscene's AI park (code 913), the original's hold flag
    /// rather than its dead byte: the aircraft is still in the mission and an objective walk still
    /// counts it. Always set beside <see cref="Inert"/>, never on its own.</summary>
    public bool Parked => _parked;

    /// <summary>Out of the mission the way the original's dead byte reads: inert without a cutscene
    /// park behind it, which is a roster block shipped deactivated or a captured airframe hidden.
    /// ⚠ DEDG and TRAVELERS walks read this, never <see cref="Inert"/>: the wing-walk capture parks
    /// the last bomber for 19 s, and a walk that dropped it there wipes its group out mid-cutscene.</summary>
    public bool Deactivated => _inert && !_parked;

    /// <summary>Present in the session as a real object: neither crashed nor inert.</summary>
    public bool InPlay => !_crashed && !_inert;

    /// <summary>Whether the collision-free window is still open, which suppresses the whole sweep
    /// rather than filtering its result.</summary>
    public bool CollisionGraceActive => _collisionGrace > 0f;

    /// <summary>Whether the carrier drop's ground-blow window is still open.</summary>
    public bool PostDropGroundBlowActive => _postDropGroundBlow > 0f;

    /// <summary>Arms the spawn windows: the collision-free window every spawn gets, and the longer
    /// ground-blow damping a carrier drop needs on top of it.</summary>
    public void ArmSpawnTimers(bool carrierDrop)
    {
        _collisionGrace = CollisionDamage.SpawnGrace;
        _postDropGroundBlow = carrierDrop ? CarrierDropGroundBlow : 0f;
    }

    /// <summary>Arms the shorter collision-free window a resolved ram writes to both parties, so
    /// neither re-resolves the overlap they are still in.</summary>
    public void ArmCollisionGrace() => _collisionGrace = CollisionDamage.EntityGrace;

    /// <summary>Drains both spawn windows by one physics step. They run down past zero rather than
    /// clamping, which is what the readers above test for.</summary>
    public void TickTimers(float dt)
    {
        _collisionGrace -= dt;
        _postDropGroundBlow -= dt;
    }

    /// <summary>Drains the crash's respawn timer by one step and answers whether it is due. An
    /// unattended run respawns with no session timer armed, which is what
    /// <paramref name="scriptedRun"/> says. A run with neither never counts down at all. Under
    /// <see cref="RespawnOnFire"/> a run-down timer waits on <paramref name="firePressed"/>. That
    /// press is read only once the timer has run down.</summary>
    public bool TickAutoRespawn(float dt, bool scriptedRun, Func<bool>? firePressed = null)
    {
        if (!scriptedRun && AutoRespawnAfter == null)
            return false;
        _autoRespawnIn -= dt;
        if (_autoRespawnIn > 0f)
            return false;
        return scriptedRun || !RespawnOnFire || firePressed?.Invoke() == true;
    }

    /// <summary>The DEATH transition: whole-vehicle health has reached zero.
    /// <paramref name="destroyDef"/> is the airframe's own def when there is a rig to play it on,
    /// and its presence is what decides whether the wreck flies itself down.</summary>
    public DestroyOutcome Destroy(string? destroyDef, int? killer)
    {
        if (_crashed)
            return default;   // one death, one report, and nothing may double-fire it
        _crashed = true;
        _destroyed = true;
        _autoRespawnIn = AutoRespawnAfter ?? AutoRespawnDelay;
        _wreckFalling = destroyDef != null;
        return new DestroyOutcome
        {
            Occurred = true,
            DestroyDef = destroyDef,
            WreckFalling = _wreckFalling,
            EndFlightSystems = true,
            CutCamera = true,
            Downed = true,
            Killer = killer,
        };
    }

    /// <summary>The GROUND-IMPACT transition: a live aircraft flown into the world, or a destroyed
    /// wreck reaching it. <paramref name="surfaceId"/> is the struck material's id, which selects
    /// the def; null is the original's null-material arm and resolves the table's slot 0.</summary>
    public CrashOutcome Crash(int? surfaceId, int? killer)
    {
        // The falling wreck's own landing is the one re-entry allowed: it is already crashed and
        // already reported, and this call is the second of its two anim slots.
        bool wreckLanding = _wreckFalling;
        if (_crashed && !wreckLanding)
            return default;
        _wreckFalling = false;
        _crashed = true;
        if (!wreckLanding)
            _autoRespawnIn = AutoRespawnAfter ?? AutoRespawnDelay;
        LastCrashDef = CrashDefs?.DefForSurfaceId(surfaceId);
        return new CrashOutcome
        {
            Occurred = true,
            WreckLanding = wreckLanding,
            CrashDef = LastCrashDef,
            EndFlightSystems = !wreckLanding,
            CutCamera = !wreckLanding,
            Downed = !wreckLanding,
            Killer = killer,
        };
    }

    /// <summary>The wreck stops flying itself: the destroy def's own callback taking the hull over,
    /// or a hull lost under the map with nothing left to strike.</summary>
    public void StopWreckFall() => _wreckFalling = false;

    /// <summary>Back in play: the three out-of-play flags clear together. The spawn windows are
    /// deliberately not re-armed here, since only a spawn point arms those.</summary>
    public void Respawn()
    {
        _crashed = false;
        _destroyed = false;
        _wreckFalling = false;
    }

    /// <summary>Flips the inert flag, answering whether it moved. The caller owes the presence
    /// write and the change notification only when it did.</summary>
    public bool SetInert(bool value)
    {
        if (_inert == value)
            return false;
        _inert = value;
        return true;
    }

    /// <summary>Flips the cutscene-park flag. No presence write follows: the park is a reading on
    /// top of <see cref="Inert"/>, which the caller sets in the same breath.</summary>
    public void SetParked(bool value) => _parked = value;
}
