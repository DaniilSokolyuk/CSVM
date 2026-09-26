using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>The decoded AI mode vocabulary: the nine states the original engine's debug
/// readout dispatches on, in its own naming (docs/formats/ai-rosters.md "AI modes, engine-side").
/// There is no flee and no inactive mode in that dispatch.</summary>
public enum AiMode
{
    /// <summary>Flying the assigned net or holding course.</summary>
    Patrol,

    /// <summary>Chasing the target; the gunner fires in this mode.</summary>
    Pursue,

    /// <summary>The rubber-band assist: a pursued AI lets its human pursuer catch up,
    /// course held, throttle eased by the decoded <c>sixth_sense_factor</c>, fire held.
    /// Disabled session-wide by <c>--no-assist</c> (<see cref="AiModeMachine.AssistEnabled"/>).</summary>
    LayOff,

    /// <summary>The evade flag is set and no library maneuver was eligible: the pilot flies its
    /// standing engagement, marked as evading, until the flag clears. The readout's name for the
    /// flag on an otherwise steering pilot, not a break-off (docs/org/aiControlLaw.md).</summary>
    Evade,

    /// <summary>Playing one maneuver-library program through <see cref="ManeuverExecutor"/>.</summary>
    EvasiveManeuver,

    /// <summary>Controls neutral until the stun expires: the original's state 4, entered by a
    /// failed sixth-sense test (<c>stun_recovery_interval</c>), a <c>SONIC</c>/<c>FLASH</c> hit
    /// (five times the intensity) and every frame inside a smoke screen
    /// (<c>smokescreen_stun_interval</c>), all through <see cref="AiModeMachine.Stun"/>.</summary>
    Stunned,

    /// <summary>Obstacle-closure override (terrain or another aircraft dead ahead): climb out,
    /// everything else waits.</summary>
    AvoidCrash,

    /// <summary>Flying at a <c>dzpathN</c> ribbon's entry point on the emergency table, the
    /// original's state 2. Entered off a reached net node's danger-zone tag or a hit pilot's
    /// proximity pick, and left inside 105 m for <see cref="NavigatingDangerZone"/>.</summary>
    ApproachingDangerZone,

    /// <summary>On rails along the ribbon: the pose is written off <see cref="Modes.DangerZoneRail"/>
    /// in place of the flight model (state 5) until the cursor leaves the far end, then patrol
    /// re-seats on the net. Nothing in this machine runs while it holds.</summary>
    NavigatingDangerZone,
}

/// <summary>The nine-mode AI state machine, owned by an <see cref="AiPilot"/> and stepped
/// once per sim tick from <see cref="AiPilot.Next"/>. The mode list and its vocabulary are
/// decoded (docs/formats/ai-rosters.md "AI modes, engine-side"), and each invented constant
/// below says so at its own declaration.
/// Config fields are plain and mutable BY DESIGN, mission scripts rewrite them at runtime
/// (<c>SET_AI_ATTACK_RADIUS</c> and friends). All randomness is this machine's own seeded
/// stream, so a fixed-seed run transitions identically. The obstacle probe is an injected
/// delegate and target state a snapshot, so the table unit-tests without a scene tree.
/// ⚠ The danger-zone modes are <see cref="AiPilot"/>'s to enter and none of them here: this
/// machine only rolls and stamps (docs/org/aiPilot.md "The danger-zone run").</summary>
public sealed class AiModeMachine
{
    /// <summary>The evade flag's clear threshold, decoded: the flag is dropped once the cosine
    /// between the pursuer's nose and the line to this aircraft falls under 0.85, about 31.8
    /// degrees off (<c>FUN_0041d9f0</c>, <c>0x0041deaf</c>). Nothing else times the flag out; the
    /// 8 s stamp the damage handler writes at <c>+0xbc</c> is read nowhere in the image.</summary>
    public const float EvadeClearAlignment = 0.85f;

    /// <summary>Obstacle-probe cadence floor, seconds, decoded: the original re-arms its per-plane
    /// timer to the game clock plus 0.5…1.0 s (<c>FUN_0041f810</c>), the fraction drawn as
    /// <c>rand()/32767</c>. Was an invented flat 0.25 s.</summary>
    public const float ProbeIntervalMinS = 0.5f;

    /// <summary>Obstacle-probe cadence ceiling, seconds (see <see cref="ProbeIntervalMinS"/>).</summary>
    public const float ProbeIntervalMaxS = 1f;

    /// <summary>The world-Y floor the climb-out arms below. A candidate maneuver whose predicted
    /// end point falls below it is vetoed. Decoded: <c>DAT_0071c3f0</c>, written 20.0 at level
    /// setup (<c>FUN_004735b0</c>, <c>0x0047415b</c>). ⚠ Flat world Y, not a terrain follow. It
    /// saves a plane over water and does nothing over a ridge, which is what the ray is for.</summary>
    public const float AltitudeFloorM = 20f;

    /// <summary>The world-Y ceiling above which no ray is cast and a running climb-out is released
    ///, decoded: <c>DAT_0071c3f4</c>, written 8000.0 alongside the floor above. A candidate
    /// maneuver predicted to end above it is taken with no obstacle test.</summary>
    public const float ProbeCeilingM = 8000f;

    /// <summary>Obstacle-probe lookahead, seconds of current velocity, decoded: the original's
    /// ray is the vehicle's own velocity scaled by 4.5 (<c>FUN_0041f810</c>), so 4.5 seconds of
    /// travel. Was an invented 2.5 until the reach was read out of the image.</summary>
    public const float ProbeLookaheadS = 4.5f;

    /// <summary>Invented: minimum probe length, metres (a slow plane still looks ahead).</summary>
    public const float ProbeMinLookaheadM = 120f;

    /// <summary>The hold a refused danger-zone look arms before the next one, seconds. Decoded:
    /// the original stamps <c>+0x8a0</c> with the clock plus 5 on every arm that finds no run
    /// (<c>FUN_004210e0</c>, <c>0x00421443</c>).</summary>
    public const float DangerZoneRetryS = 5f;

    /// <summary>The vehicle constructor's own <c>daredevil_chance</c> (<c>+0x954</c>, written
    /// 0.2 at <c>0x004b03c2</c>), carried by a pilot whose spawn resolved no rating.</summary>
    public const float DefaultDaredevilChance = 0.2f;

    /// <summary>The avoid-crash climb-out altitude gain, metres, decoded: the original's climb-out
    /// aim is the aeroplane's own position with Y + 1000 (<c>FUN_0041d1f0</c> case 3), which is the
    /// same 1000 m <see cref="AiPilot.ClimbOutAim"/> flies. Was an invented 250.</summary>
    public const float ClimbOutM = 1000f;

    /// <summary>Selection weight multiplier for a roster-authored signature maneuver, decoded
    /// (<c>FUN_004201a0</c>, <c>0x004205ca</c>). Was an invented 3.</summary>
    public const float SignatureWeight = 6f;

    /// <summary>Selection weight multiplier on the maneuver flown last, decoded
    /// (<c>FUN_004201a0</c>, <c>0x00420584</c>, off the <c>+0x9b4</c> slot). This is what keeps a
    /// chained evade from flying the same program twice running.</summary>
    public const float RepeatWeight = 0.1f;

    /// <summary>Selection weight multiplier on the maneuver flown before last, decoded
    /// (<c>FUN_004201a0</c>, <c>0x00420598</c>, off the <c>+0x9b8</c> slot).</summary>
    public const float SecondLastWeight = 0.2f;

    /// <summary>Selection weight floor, decoded: a weight that comes out negative is replaced by
    /// this, not clamped to zero (<c>FUN_004201a0</c>, <c>0x00420607</c>).</summary>
    public const float MinSelectionWeight = 0.1f;

    /// <summary>The base of the log that turns an authored <c>steady_hand_chance</c> into
    /// <see cref="SteadyHandExponent"/>. Decoded (<c>0x00608020</c>, read at <c>0x0047d00a</c>).
    /// ⚠ The authored 0.5-to-0.08 pair is this log's argument. It is never a probability a roll is
    /// compared against (docs/org/aiControlLaw.md, "The roll itself").</summary>
    public const float SteadyHandLogBase = 0.7f;

    /// <summary>The vehicle constructor's own steady-hand exponent (<c>0x004b03f4</c>), carried
    /// by a pilot whose spawn resolved no rating.</summary>
    public const float DefaultSteadyHandExponent = 1.03f;

    /// <summary>Invented: a pursuing human this far behind has "fallen behind", pursue eases
    /// into lay off past this gap. The mode and the intent are decoded; the distance is not.</summary>
    public const float LayOffEnterRangeM = 350f;

    /// <summary>Invented: the pursuer has "caught up" inside this gap, lay off returns to
    /// pursue (hysteresis against <see cref="LayOffEnterRangeM"/>).</summary>
    public const float LayOffCaughtUpRangeM = 250f;

    /// <summary>Invented: the pursued test's rear cone, the pursuer must sit within this
    /// half-angle of the AI's tail axis (its velocity, reversed).</summary>
    public const float LayOffRearConeDeg = 60f;

    /// <summary>Invented: the pursued test's chase cone, the pursuer's velocity must point
    /// within this half-angle of the line to the AI, i.e. it is actually chasing.</summary>
    public const float LayOffPursuerConeDeg = 30f;

    /// <summary>Invented: minimum lay-off dwell, seconds, an anti-chatter hold before any
    /// lay-off exit condition is honoured.</summary>
    public const float LayOffMinHoldS = 2f;

    /// <summary>Invented: the pursued geometry must hold continuously this long before pursue
    /// eases into lay off. Kills single-frame misfires in a turning fight, where the tail-axis
    /// test can pass for a moment mid-maneuver and the dwell would then latch it (user-reported
    /// 2026-08-14: an enemy behind the player appearing to slow down).</summary>
    public const float LayOffSustainS = 1.5f;

    /// <summary>The 20 s hold a promotion onto a turret or structure arms in place of
    /// <see cref="AttackDwellS"/> (<c>0x006035e4</c>, read at <c>0x0041f0ea</c>). The original
    /// takes it only for a <c>jet</c> or <c>wingman</c> pursuer, which every aeroplane def is.</summary>
    public const float NonVehicleAttackDwellS = 20f;

    /// <summary>The 15 s hold a pursuit revert off a turret or structure arms in place of
    /// <see cref="NotPursuitDwellS"/> (<c>0x00603560</c>, read at <c>0x0041e713</c>).</summary>
    public const float NonVehicleNotPursuitDwellS = 15f;

    /// <summary>Activation radius, metres, player.json's <c>min_ai_active_dist</c> (2000 shipped),
    /// the fallback for every roster whose own volume slots are unauthored. That is all of them.
    /// ⚠ Decoded, this volume gates whether the original SIMULATES a vehicle at all, measured to
    /// the player. It reaches neither target admission, the promotion nor the chase leash
    /// (docs/org/aiPilot.md "Every reader of the attack and activation triples"). Nothing in this
    /// machine reads it; it is carried for the spawn and <c>DEDG</c> writers that set it.</summary>
    public float ActivationRange = 2000f;

    /// <summary>Attack radius, metres, vehicle.json's <c>attack</c> (2000 shipped, on
    /// <c>basic_airplane</c>, inherited install-wide). The decoded admission volume. Both scorers
    /// refuse a candidate outside it, so it is what <see cref="AiTargetRanking"/> is handed.
    /// ⚠ Do not test it again in <see cref="Update"/>. The original's promotion reads no range, and
    /// a quarry the selection hands over is chased the tick the dwell allows it.</summary>
    public float AttackRange = 2000f;

    /// <summary>vehicle.json's <c>attack_dwell</c>, seconds (60 shipped): how long a promotion holds
    /// the chase before the pursuit reverts on its own.</summary>
    public float AttackDwellS = 60f;

    /// <summary>vehicle.json's <c>not_pursuit_dwell</c>, seconds (5 shipped): how long a revert or a
    /// lost target keeps the pilot on its net before it may promote again.</summary>
    public float NotPursuitDwellS = 5f;

    /// <summary>Chase leash, metres, vehicle.json's <c>return_range</c> (1200 shipped). Decoded as
    /// a CYLINDER about the pursuit anchor. The squared horizontal radius sits at <c>+0x334</c>,
    /// the vertical band <c>-r</c> to <c>+r</c> at <c>+0x338</c>/<c>+0x33c</c>. All three are
    /// filled from the one authored token. Leaving it reverts the task on its own, with no
    /// activation term (<c>FUN_0041d9f0</c>, <c>0x0041e69c</c>–<c>0x0041e6d5</c>).</summary>
    public float ReturnRange = 1200f;

    /// <summary>The steady-hand exponent, the original's <c>+0x968</c>. A hit passes with
    /// probability <c>(1 - bite/pool)</c> raised to this. The same round therefore evades far more
    /// often out of a worn-down pool than a fresh one. Spawn writes <see cref="ExponentFor"/> of
    /// the rating's <c>steady_hand_chance</c>, which RISES with the rating, so the better pilot
    /// evades more (docs/org/aiControlLaw.md, "The roll itself").</summary>
    public float SteadyHandExponent = DefaultSteadyHandExponent;

    /// <summary>Probability that the sixth-sense test PASSES (the pilot follows the target's
    /// maneuver), <c>sixth_sense_chance</c> (0.45 → 0.71 over the pair). A failure stuns.</summary>
    public float SixthSenseChance = 0.45f;

    /// <summary>How long a stun lasts, <c>stun_recovery_interval</c> (4.8 s → 0.6 s over the
    /// pair).</summary>
    public float StunRecoveryIntervalS = 4.8f;

    /// <summary>The decoded ease-off factor applied while being pursued,
    /// <c>sixth_sense_factor</c> (0.994 → 1.07 over the pair): the fraction of the
    /// pursuer's speed a laying-off pilot flies at, so a poor pilot lets the player close and
    /// an ace pulls away. The constant is decoded; the speed-matching application point is our
    /// reading (<see cref="AiPilot"/>).</summary>
    public float SixthSenseFactor = 0.994f;

    /// <summary>The rubber-band assist switch: false (<c>--no-assist</c>) means
    /// <see cref="AiMode.LayOff"/> is never entered by <see cref="Update"/>, pursue only, the
    /// original's assist off. Default true, the original's behaviour. External
    /// <see cref="Enter"/> overrides (script/tests) are deliberately not gated.</summary>
    public bool AssistEnabled = true;

    /// <summary>The pilot's 1–9 <c>natural_touch</c>, compared directly against maneuver
    /// difficulty (no interpolation table, by design). The proximity pick compares it against a
    /// ribbon's own difficulty the same way (<c>FUN_004210e0</c>, <c>+0x958</c> against
    /// <c>+0x44</c>).</summary>
    public int NaturalTouch = 1;

    /// <summary>The pilot's resolved <c>daredevil_chance</c> (<c>+0x954</c>, 0.35 → 0.99 over the
    /// rating): the probability one look for a danger zone gets past the roll. A spawn whose def
    /// is not a <c>jet</c> is given zero. That is the original's own <c>+0x67c</c> test on the
    /// arm that rolls (<c>FUN_0041d9f0</c>, <c>0x0041da07</c>).</summary>
    public float DaredevilChance = DefaultDaredevilChance;

    /// <summary>The maneuver library, or null for a maneuver-less pilot (a failed
    /// steady-hand test then evades plainly instead).</summary>
    public IReadOnlyList<Maneuver>? Library;

    /// <summary>The roster's signature-maneuver names (<see cref="Maneuvers.SignatureNames"/>),
    /// weighted up <see cref="SignatureWeight"/>× during selection; null/empty = none.</summary>
    public IReadOnlyCollection<string>? SignatureManeuvers;

    /// <summary>Line-of-sight probe for the avoid-crash test and the maneuver veto's path sweep.
    /// It hits static world plus other aircraft, never the caster's own body (docs/org/aiPilot.md
    /// "What the ray can hit"). Returns the struck body's name, or null for a clear line. The
    /// transition log can then say whether the override fired on terrain or on another aircraft.
    /// The host wires <c>FlightController.AvoidCrashBlocksLine</c>. A null delegate means no world
    /// data, so the mode is never entered and no candidate is vetoed for an obstacle.</summary>
    public Func<Vector3, Vector3, string?>? ProbeBlocked;

    /// <summary>Whether a nitro-flagged library entry may be drawn at all: the injector installed
    /// and the engine alive (<c>FUN_004201a0</c>, <c>0x004202d5</c>-<c>0x004202ea</c>). The host
    /// wires <c>FlightController</c>'s pair. ⚠ A null delegate is a pilot with no injector, so
    /// <c>nitro_evade</c> is culled. Difficulty 0 makes it eligible for everyone, and without the
    /// boost its single step is six seconds of wings-level flight.</summary>
    public Func<bool>? NitroUsable;

    // The log's argument is floored so a chance of zero cannot hand the exponent an infinity.
    // The shipped 0.5-to-0.08 pair never reaches it; an authored rating floors at 0.5.
    private const double MinSteadyHandChance = 1e-6;

    private readonly Random _rng;

    private AiMode _returnMode = AiMode.Patrol;
    private AiMode? _lastTargetMode;

    // The pursuit anchor: the PURSUER's own position where the promotion began the chase
    // (FUN_0041f040, 0x0041f0a6). It is never rewritten while the task stands, so a lay off, a
    // stun or a finished maneuver hands back to the same point. Null means the task is not pursue.
    private Vector3? _pursuitAnchor;

    // The one dwell stamp (the original's +0x300) against this machine's own clock, the sum of
    // every Update's dt. A promotion arms it to the chase's deadline. A revert or a lost target
    // arms it to the end of the wait before the next chase, so one field paces both ends.
    private double _clock;
    private double _dwellUntil;
    private double _dangerZoneRetryUntil;
    private bool _quarryIsVehicle = true;
    private bool _quarryIsPrimary;

    private Vector3 _lastPos;
    private Vector3 _lastVelocity;
    private Vector3? _nose;
    private Basis? _attitude;
    private string? _lastManeuver;
    private string? _secondLastManeuver;
    private float _layOffHold;
    private float _pursuedFor;
    private float _stunRemaining;
    private float _probeCooldown;

    public AiModeMachine(Random rng)
    {
        _rng = rng;
    }

    /// <summary>Every transition, with the modes and a short reason, the observability seam
    /// (the session logs these in the engine's own mode vocabulary).</summary>
    public event Action<AiMode, AiMode, string>? ModeChanged;

    /// <summary>Every steady-hand and sixth-sense roll's outcome, phrased in the engine's own
    /// vocabulary. Pass and fail alike, so a quiet run is distinguishable from a lucky one. Every
    /// hit that took no roll at all is logged too, so the line count is the hit count.</summary>
    public event Action<string>? RollLogged;

    /// <summary>The current mode. Transitions go through the machine; <see cref="Enter"/> is the
    /// external override (mission script, tests).</summary>
    public AiMode Mode { get; private set; } = AiMode.Patrol;

    /// <summary>The executor playing the current evasive maneuver; non-null exactly while
    /// <see cref="Mode"/> is <see cref="AiMode.EvasiveManeuver"/>.</summary>
    public ManeuverExecutor? Executor { get; private set; }

    /// <summary>Seconds of stun left, zero outside <see cref="AiMode.Stunned"/>: the original's
    /// expiry at <c>+0xc0</c> minus the clock.</summary>
    public float StunRemainingS => Mode == AiMode.Stunned ? Mathf.Max(0f, _stunRemaining) : 0f;

    /// <summary>The evade flag (the original's <c>+0xBA</c>): set by a failed steady-hand test and
    /// held until <see cref="EvadeClearAlignment"/> clears it. While it stands, lay off cannot be
    /// entered, a finished maneuver chains into another one, and no second steady-hand roll is
    /// taken. It is not a mode: the pilot keeps flying its engagement.
    /// ⚠ Do not set it on entry into a mode. The image's one setter is inside the damage routine,
    /// so an ordered evade must leave the next hit its roll.</summary>
    public bool Evading { get; private set; }

    /// <summary>Where the standing pursuit began, the point <see cref="ReturnRange"/>'s cylinder is
    /// measured from; null while the task is not pursue. Written once per promotion, so it is where
    /// the chase started rather than where the aircraft spawned.</summary>
    public Vector3? PursuitAnchor => _pursuitAnchor;

    /// <summary>Seconds until the dwell stamp passes, zero once it has. While pursuing, this is
    /// what is left of the chase. Otherwise it is what is left of the wait before a promotion is
    /// allowed.</summary>
    public float DwellRemainingS => (float)Math.Max(0.0, _dwellUntil - _clock);

    /// <summary>Avoid-crash's climb-out altitude order (entry altitude + <see cref="ClimbOutM"/>).</summary>
    public float ClimbOutAltitude { get; private set; }

    /// <summary>Lay off's course order, degrees: the heading flown at entry, held so the pilot
    /// stays ahead of the pursuer instead of turning back into a head-on.</summary>
    public float LayOffHeadingDeg { get; private set; }

    /// <summary>Lay off's altitude order, metres: the entry altitude.</summary>
    public float LayOffAltitude { get; private set; }

    /// <summary>The spawn-side conversion, decoded (<c>FUN_0047c210</c>, <c>0x0047d00a</c>):
    /// <c>e = ln(chance) / ln(0.7)</c> over the rating's resolved <c>steady_hand_chance</c>.
    /// Falling chances give rising exponents, which is where "a better pilot evades more" comes
    /// from.</summary>
    public static float ExponentFor(float chance) => (float)(
        Math.Log(Math.Max(chance, MinSteadyHandChance)) / Math.Log(SteadyHandLogBase));

    /// <summary>The decoded pass probability for one hit (<c>FUN_004b1160</c>). It is the
    /// complement of the fraction this hit bites out of the victim's pre-hit pool, raised to
    /// <paramref name="exponent"/>. A bite covering the pool returns 0, which never passes.</summary>
    public static double PassChance(float bite, float pool, float exponent) =>
        pool <= 0f || bite >= pool ? 0d : Math.Pow(1d - (bite / (double)pool), exponent);

    /// <summary>The armour-then-health slice this hit takes of the pre-hit pair
    /// (<c>FUN_004b1160</c>, <c>+0x2c8</c> and <c>+0x2d0</c>). It is the armour damage while the
    /// armour pool covers it, else that pool plus the health damage, else the whole pair.</summary>
    public static float BiteOf(float armorDamage, float healthDamage, float armorPool, float healthPool) =>
        armorDamage < armorPool ? armorDamage
        : healthDamage < healthPool ? armorPool + healthDamage
        : armorPool + healthPool;

    /// <summary>The engine's own name for a mode, the debug-readout vocabulary, verbatim.</summary>
    public static string NameOf(AiMode mode) => mode switch
    {
        AiMode.Patrol => "patrol",
        AiMode.Pursue => "pursue",
        AiMode.LayOff => "lay off",
        AiMode.Evade => "evade",
        AiMode.EvasiveManeuver => "evasive maneuver",
        AiMode.Stunned => "stunned",
        AiMode.AvoidCrash => "avoid crash",
        AiMode.ApproachingDangerZone => "approaching danger zone",
        AiMode.NavigatingDangerZone => "navigating danger zone",
        _ => mode.ToString(),
    };

    /// <summary>External mode override, the mission-script / test seam. Resets the overridden
    /// mode's own timers so a forced state behaves as if entered normally.</summary>
    public void Enter(AiMode mode, string reason = "ordered")
    {
        // An ordered engagement anchors where it was ordered, as a promotion does. Without this a
        // scripted rejoin would be leashed to a point the aircraft left long ago.
        if (mode is AiMode.Pursue or AiMode.LayOff)
        {
            _pursuitAnchor = _lastPos;
            ArmChaseDeadline(); // or the stale stamp would revert the ordered chase next tick
        }
        Transition(mode, reason);
    }

    /// <summary>One sim tick's transitions. The <paramref name="targetMode"/> argument is an AI
    /// target's own mode for the sixth-sense trigger, null for a human, the roll being undecoded
    /// against one. The <paramref name="targetVelocity"/> and <paramref name="targetIsHuman"/>
    /// arguments feed the lay-off pursued test, <paramref name="targetNose"/> the evade clear and
    /// <paramref name="attitude"/> the veto's frame. A non-vehicle quarry takes the dwell's
    /// 20/15 s holds; the primary lifts the dwell.</summary>
    public AiMode Update(Vector3 pos, Vector3 velocity, Vector3? targetPos, AiMode? targetMode,
        float dt, Vector3? targetVelocity = null, bool targetIsHuman = false, Vector3? nose = null,
        Vector3? targetNose = null, Basis? attitude = null, bool quarryIsVehicle = true,
        bool quarryIsPrimary = false)
    {
        _clock += dt;
        _quarryIsVehicle = quarryIsVehicle;
        _quarryIsPrimary = targetPos is not null && quarryIsPrimary;
        _lastPos = pos;
        _lastVelocity = velocity;
        _nose = nose;
        _attitude = attitude;
        if (Mode == AiMode.Stunned)
        {
            // Nothing interrupts a stun: the pilot has no controls to react with.
            _stunRemaining -= dt;
            _lastTargetMode = targetMode;
            if (_stunRemaining <= 0f)
                Transition(_returnMode, "stun recovered");
            return Mode;
        }

        // The sixth-sense trigger (decoded vocabulary: "AI has been evaded"): the pursued
        // target broke into an evasive state since last tick. Rolled before the terrain
        // override so the roll is not masked by an avoid-crash frame.
        if (Mode is AiMode.Pursue or AiMode.LayOff
            && targetMode is AiMode.Evade or AiMode.EvasiveManeuver
            && _lastTargetMode is not (AiMode.Evade or AiMode.EvasiveManeuver))
        {
            NotifyTargetEvaded();
        }
        _lastTargetMode = targetMode;
        if (Mode == AiMode.Stunned)
            return Mode; // the roll above just stunned us; timers start next tick

        // State 5 suppresses the crash check (FUN_004897c0 gates it on state < 4) and the pose is
        // written off the ribbon, so nothing below could steer; the pilot ends the run.
        if (Mode == AiMode.NavigatingDangerZone)
            return Mode;

        UpdateAvoidCrash(pos, velocity, dt);
        UpdateEvadeFlag(pos, targetPos, targetNose);

        switch (Mode)
        {
            // The promotion (FUN_0041f040) reads no range, team or net. Whatever the selection
            // handed over is chased once the dwell stamp has passed, at once for the assigned target.
            case AiMode.Patrol:
                if (targetPos is { } t && (_quarryIsPrimary || _clock > _dwellUntil))
                {
                    Transition(AiMode.Pursue, FormattableString.Invariant($"target at {pos.DistanceTo(t):0} m"));
                }
                break;

            // Evade joins the engagement cases: the flag runs the same driver, so the target
            // loss, the dwell and the leash still apply while it stands. The assigned target skips
            // the dwell and the leash alike (FUN_0041d9f0, 0x0041e5a6).
            case AiMode.Pursue:
            case AiMode.LayOff:
            case AiMode.Evade:
                if (targetPos is not { } tp)
                {
                    RevertTask("target lost", NotPursuitDwellS);
                }
                else if (!_quarryIsPrimary && _pursuitAnchor is not null && _clock > _dwellUntil)
                {
                    RevertTask("attack dwell ran out", RevertWaitS());
                }
                else if (!_quarryIsPrimary && _pursuitAnchor is { } anchor && OutsideReturnCylinder(pos, anchor))
                {
                    RevertTask(FormattableString.Invariant(
                        $"{pos.DistanceTo(anchor):0} m from the pursuit anchor, beyond return range"), RevertWaitS());
                }
                else
                {
                    UpdateLayOff(pos, velocity, tp, targetVelocity, targetIsHuman, dt);
                }
                break;

            case AiMode.EvasiveManeuver:
                if (Executor is not { Done: false })
                {
                    Executor = null;
                    Evading = Evading && PursuerStillOn(pos, targetPos, targetNose);
                    if (Evading)
                        StartReaction("program exhausted");
                    else
                        ReturnFromReaction(pos, targetPos);
                }
                break;

            case AiMode.ApproachingDangerZone:
                // The run record outranks a target: the lock drops the standing target
                // (FUN_00421500 nulls +0x948), so no activation into pursue from here.
                break;
        }
        return Mode;
    }

    /// <summary>The hit path's entry (decoded: <c>FUN_004b9bc0</c>, <c>0x004b9f1e</c> onward). Both
    /// halves of the hit are taken against the victim's PRE-hit pools, and the steady-hand test is
    /// rolled here. A FAILED test sets the evade flag and picks a library maneuver whatever mode
    /// was running. A pilot with nothing eligible does not break off; it keeps its engagement with
    /// the flag set. Leftover damage re-enters as the wrapper loop's later passes do, so one impact
    /// can take several rolls against a shrinking pool. No second roll while the flag stands.</summary>
    public void NotifyDamage(float armorDamage, float healthDamage, float armorPool, float healthPool)
    {
        if (Evading)
        {
            // the original restamps here and rolls nothing
            LogNoRoll(armorDamage + healthDamage, "already evading");
            return;
        }
        float dmgA = armorDamage, dmgH = healthDamage, poolA = armorPool, poolH = healthPool;
        while (true)
        {
            float bite = BiteOf(dmgA, dmgH, poolA, poolH);
            bool failed = _rng.NextDouble() >= PassChance(bite, poolA + poolH, SteadyHandExponent);
            RollLogged?.Invoke(FormattableString.Invariant(
                $"absorbed {dmgA + dmgH:0.0} damage; steady hand test ")
                + (failed ? "failed. Evading." : "passed. Not evading."));
            if (failed)
            {
                Evading = true;
                RememberReturnMode();
                StartReaction("steady hand test failed");
                return;
            }
            // The wrapper's own loop condition (FUN_004b9b30): both halves still unspent and the
            // victim alive. The zone's share of the first pass is not modelled here, so this pool
            // is the whole-vehicle pair throughout.
            PlaneDamage.Spend(ref dmgA, ref dmgH, ref poolA, ref poolH);
            if (dmgA <= 0f || dmgH <= 0f || poolH <= 0f)
                return;
        }
    }

    /// <summary>The position-update entry (decoded vocabulary: "AI has been evaded"): rolls the
    /// sixth-sense test; a FAILED test stuns for <see cref="StunRecoveryIntervalS"/>. Called by
    /// <see cref="Update"/> when a pursued AI target enters an evasive state; public because
    /// the human-target trigger is undecoded and scripts/tests fire it directly.</summary>
    public void NotifyTargetEvaded()
    {
        if (Mode is not (AiMode.Pursue or AiMode.LayOff))
            return;
        bool passed = _rng.NextDouble() < SixthSenseChance;
        RollLogged?.Invoke("AI has been evaded. Sixth sense test " +
            (passed ? "passed." : "failed; AI now stunned."));
        if (passed)
            return;
        Stun(StunRecoveryIntervalS, FormattableString.Invariant($"for {StunRecoveryIntervalS:0.0} s"));
    }

    /// <summary>The daredevil roll that opens the proximity pick (<c>FUN_004210e0</c>'s unforced
    /// arm): true means the pilot may look for a zone this step. The 5 s stamp is read first, so
    /// a refused look costs one roll per <see cref="DangerZoneRetryS"/>, not one per frame. A
    /// failed roll arms the stamp again. ⚠ This rolls only, and <see cref="AiPilot"/> owns both
    /// entries into the run.</summary>
    public bool RollDaredevil()
    {
        if (DaredevilChance <= 0f || _clock < _dangerZoneRetryUntil)
            return false;
        bool passed = _rng.NextDouble() < DaredevilChance;
        RollLogged?.Invoke("Dare devil test " + (passed
            ? "passed. Looking for danger zones."
            : "failed. Not looking for danger zones."));
        if (!passed)
            StampDangerZoneRetry();
        return passed;
    }

    /// <summary>Arms the retry hold after a look that started no run. The original stamps it on
    /// every arm of the pick that falls through. Those arms are a difficulty over the pilot's
    /// natural touch, an occupied lane, and no end in range.</summary>
    public void StampDangerZoneRetry() => _dangerZoneRetryUntil = _clock + DangerZoneRetryS;

    /// <summary>The stun handler (decoded: <c>FUN_004200d0</c>, shared by the failed sixth-sense
    /// test, a <c>SONIC</c>/<c>FLASH</c> hit and the smoke screen): controls neutral for
    /// <paramref name="seconds"/>, then back to the interrupted mode. Pre-empts every mode (the
    /// original writes state 4 without reading it), and a stun on a stunned pilot OVERWRITES the
    /// expiry (clock + seconds, no max) BY DESIGN: the smoke screen refreshes it every frame. The
    /// victim guards are the caller's (<c>FlightController.TryStunPilot</c>).</summary>
    public void Stun(float seconds, string reason = "stunned")
    {
        if (seconds <= 0f)
            return;
        RememberReturnMode();
        _stunRemaining = seconds;
        Transition(AiMode.Stunned, reason);
    }

    // A hit that reached the pilot but took no roll. Without it the trace cannot separate an AI
    // that is never hit from one that is hit constantly while a reaction is already running.
    private void LogNoRoll(float absorbed, string why) =>
        RollLogged?.Invoke(FormattableString.Invariant(
            $"absorbed {absorbed:0.0} damage; no steady hand test ({why})"));

    // Where an interruption hands back to. A run in progress resumes as an approach to the
    // cursor's current point: the original keeps the run record (+0x9bc) through a stun or a
    // climb-out and re-arms state 2 off it every frame (FUN_004897c0).
    private void RememberReturnMode()
    {
        _returnMode = Mode switch
        {
            AiMode.Pursue or AiMode.LayOff or AiMode.Patrol => Mode,
            AiMode.ApproachingDangerZone or AiMode.NavigatingDangerZone => AiMode.ApproachingDangerZone,
            _ => _returnMode,
        };
    }

    // One turn of the flag's reaction: a fresh maneuver when the library offers one, and the
    // marked engagement otherwise. The original re-enters the picker from the combat driver
    // every frame the flag stands and the step deadline has passed, so a program that ends
    // while the flag is still set runs straight into the next one.
    private void StartReaction(string why)
    {
        if (PickManeuver() is { } maneuver)
        {
            _secondLastManeuver = _lastManeuver;
            _lastManeuver = maneuver.Name;
            Executor = new ManeuverExecutor(maneuver);
            Transition(AiMode.EvasiveManeuver,
                $"'{maneuver.Name}' natural touch {maneuver.Difficulty}/{NaturalTouch}, {why}");
        }
        else
        {
            Transition(AiMode.Evade, $"{why}, no maneuver eligible");
        }
    }

    // ⚠ Do not test the flag while a maneuver is playing. The clear sits inside the steering
    // driver's own branch, which a pilot on a program never reaches, so a running program is
    // never cut short by it; the moment one ends is where the chain re-tests it.
    private void UpdateEvadeFlag(Vector3 pos, Vector3? targetPos, Vector3? targetNose)
    {
        if (!Evading || Mode == AiMode.EvasiveManeuver
            || PursuerStillOn(pos, targetPos, targetNose))
            return;
        Evading = false;
        if (Mode == AiMode.Evade)
            ReturnFromReaction(pos, targetPos);
    }

    // The flag's hold condition: the pursuer's nose still on this aircraft. The original measures
    // the human player's forward axis against the line to its own target, which the damage
    // handler has just pointed at the player. No target means no driver to test it at all, which
    // the port reads as a clear rather than a latch.
    private bool PursuerStillOn(Vector3 pos, Vector3? targetPos, Vector3? targetNose) =>
        targetPos is { } tp && targetNose is { } tn && tn.LengthSquared() > 1e-4f
            && (pos - tp).LengthSquared() > 1e-4f
            && (pos - tp).Normalized().Dot(tn.Normalized()) >= EvadeClearAlignment;

    // Where a finished reaction goes back to: the engagement while the task still stands, patrol
    // otherwise. ⚠ Do not re-anchor here. The original's state (+0x358) and task (+0x2f0) are
    // separate, so a reaction leaves the anchor alone and a re-anchor walks the leash across the map.
    private void ReturnFromReaction(Vector3 pos, Vector3? targetPos)
    {
        bool engaged = targetPos is not null && _pursuitAnchor is { } anchor
            && (_quarryIsPrimary || (_clock <= _dwellUntil && !OutsideReturnCylinder(pos, anchor)));
        var back = _returnMode is AiMode.Pursue or AiMode.LayOff && engaged
            ? _returnMode
            : _returnMode == AiMode.ApproachingDangerZone ? _returnMode
            : AiMode.Patrol;
        // A reaction that hands a pursuit back to the net is the task revert, deferred to here.
        if (back == AiMode.Patrol && _pursuitAnchor is not null)
            _dwellUntil = _clock + (targetPos is null ? NotPursuitDwellS : RevertWaitS());
        Transition(back, "reaction complete");
    }

    // The promotion's stamp (0x0041f0ea-0x0041f106): the chase's own deadline.
    private void ArmChaseDeadline() =>
        _dwellUntil = _clock + (_quarryIsVehicle ? AttackDwellS : NonVehicleAttackDwellS);

    // The revert's re-arm (0x0041e6d7-0x0041e737): the wait before the next promotion.
    private float RevertWaitS() => _quarryIsVehicle ? NotPursuitDwellS : NonVehicleNotPursuitDwellS;

    // Only a pursue task re-arms (0x0041c299 tests task 1). An ordered evade off the net that
    // loses its target leaves the promotion's wait where it stood.
    private void RevertTask(string reason, float waitS)
    {
        if (_pursuitAnchor is not null)
            _dwellUntil = _clock + waitS;
        Transition(AiMode.Patrol, reason);
    }

    // The task revert's geometry (FUN_0041d9f0, 0x0041e69c-0x0041e6d5): a cylinder about the
    // anchor, ReturnRange wide and the same distance up and down. ⚠ Not a sphere. The corner
    // between radius and band is inside it, and a sphere would recall a climbing pursuer early.
    private bool OutsideReturnCylinder(Vector3 pos, Vector3 anchor)
    {
        var d = pos - anchor;
        return (d.X * d.X) + (d.Z * d.Z) > ReturnRange * ReturnRange
            || d.Y < -ReturnRange || d.Y > ReturnRange;
    }

    // The rubber-band assist's transitions (decoded: the mode, its "let the player catch up"
    // intent, and sixth_sense_factor; the geometry is invented, named on the constants above).
    // Pursue eases into lay off when a chasing human target has fallen behind; lay off returns
    // when the pursuer catches up or stops chasing. AssistEnabled false never enters.
    // Splitscreen extension: the assist follows whichever human the AI is engaging, not a fixed
    // player one.
    private void UpdateLayOff(Vector3 pos, Vector3 velocity, Vector3 targetPos,
        Vector3? targetVelocity, bool targetIsHuman, float dt)
    {
        if (Evading)
        {
            // The flag makes the break-off branch unreachable, so a laying-off pilot that takes
            // a hit goes straight back to the engagement.
            if (Mode == AiMode.LayOff)
                Transition(AiMode.Pursue, "evading");
            return;
        }
        _layOffHold -= dt;
        float gap = pos.DistanceTo(targetPos);
        bool pursued = AssistEnabled && targetIsHuman
            && IsPursuedBy(pos, velocity, targetPos, targetVelocity);
        if (Mode == AiMode.Pursue)
        {
            // Entry needs the geometry SUSTAINED, not one passing frame: a turning fight can
            // satisfy the tail-axis test momentarily, and the dwell would latch the misfire.
            _pursuedFor = pursued && gap > LayOffEnterRangeM ? _pursuedFor + dt : 0f;
            if (_pursuedFor >= LayOffSustainS)
            {
                Transition(AiMode.LayOff, FormattableString.Invariant(
                    $"pursuer {gap:0} m behind for {_pursuedFor:0.0} s; easing off x{SixthSenseFactor:0.00}"));
            }
        }
        else if (!AssistEnabled)
        {
            Transition(AiMode.Pursue, "assist off");
        }
        else if (_layOffHold <= 0f)
        {
            if (gap < LayOffCaughtUpRangeM)
                Transition(AiMode.Pursue, FormattableString.Invariant($"pursuer caught up at {gap:0} m"));
            else if (!pursued)
                Transition(AiMode.Pursue, "no longer pursued");
        }
    }

    // The pursued test (invented geometry): the target sits within LayOffRearConeDeg of the tail
    // axis and its velocity points within LayOffPursuerConeDeg of the AI, i.e. it is chasing.
    // The tail axis is the NOSE when supplied, velocity only as fallback, mid-maneuver the two
    // diverge and velocity alone let the test pass for a frame with the enemy behind the player.
    private bool IsPursuedBy(Vector3 pos, Vector3 velocity, Vector3 targetPos,
        Vector3? targetVelocity)
    {
        var axis = _nose is { } n && n.LengthSquared() > 1e-4f ? n : velocity;
        if (targetVelocity is not { } tv || tv.LengthSquared() < 1e-4f
            || axis.LengthSquared() < 1e-4f)
            return false;
        var toTarget = targetPos - pos;
        if (toTarget.LengthSquared() < 1e-4f)
            return false;
        float behindCos = (-axis.Normalized()).Dot(toTarget.Normalized());
        if (behindCos < Mathf.Cos(Mathf.DegToRad(LayOffRearConeDeg)))
            return false;
        float chaseCos = tv.Normalized().Dot((-toTarget).Normalized());
        return chaseCos >= Mathf.Cos(Mathf.DegToRad(LayOffPursuerConeDeg));
    }

    // The obstacle-closure override, in the original's three altitude bands (FUN_0041f810): below
    // AltitudeFloorM it arms outright, above ProbeCeilingM nothing is cast and a running one is
    // released, and between them a due probe decides. ⚠ Do not add a second, deck-slanted ray:
    // the original casts one along its own velocity, and the extra one broke a campaign wingman
    // off its station over water the decoded ray was clear of (docs/org/aiPilot.md). Blocked →
    // avoid crash, dropping a running maneuver, the original's own precedence (FUN_004897c0).
    private void UpdateAvoidCrash(Vector3 pos, Vector3 velocity, float dt)
    {
        // Stunned never reaches here (Update returns above), matching the caller's state < 4 gate.
        if (pos.Y < AltitudeFloorM)
        {
            // No ray and no cadence on this arm: the original stores the state every call.
            if (Mode != AiMode.AvoidCrash)
                EnterAvoidCrash(pos, FormattableString.Invariant($"below the {AltitudeFloorM:0} m floor"));
            return;
        }

        if (pos.Y >= ProbeCeilingM)
        {
            if (Mode == AiMode.AvoidCrash)
                Transition(_returnMode, "above the probe ceiling");
            return;
        }

        if (ProbeBlocked is not { } probe)
            return;
        _probeCooldown -= dt;
        if (_probeCooldown > 0f)
            return; // not due: the state stands until a probe changes it, as the original's does
        _probeCooldown = ProbeIntervalMinS
            + ((float)_rng.NextDouble() * (ProbeIntervalMaxS - ProbeIntervalMinS));

        float speed = velocity.Length();
        var dir = speed > 1e-3f ? velocity / speed : Vector3.Forward;
        float reach = Mathf.Max(speed * ProbeLookaheadS, ProbeMinLookaheadM);
        var ahead = pos + dir * reach;
        string? struck = probe(pos, ahead);

        if (struck is null)
        {
            // One clear ray releases, in the same call. The original holds no clear streak.
            if (Mode == AiMode.AvoidCrash)
                Transition(_returnMode, "clear of obstacles");
        }
        else if (Mode != AiMode.AvoidCrash)
        {
            EnterAvoidCrash(pos, FormattableString.Invariant($"obstacle inside {reach:0} m ({struck})"));
        }
    }

    private void EnterAvoidCrash(Vector3 pos, string reason)
    {
        RememberReturnMode();
        Executor = null; // a running maneuver is abandoned to the override
        ClimbOutAltitude = pos.Y + ClimbOutM;
        Transition(AiMode.AvoidCrash, reason);
    }

    // One weighted seeded draw over the entries that pass the natural-touch, injector and altitude
    // culls; null when no library is set or nothing passes. The weight is the entry's own bias plus
    // one, penalised for the last two flown and multiplied up for a signature entry, floored last.
    // The original's remaining term wants a roster value this machine does not carry. It is a point
    // for a program whose predicted end direction carries the aircraft toward its preferred
    // altitude.
    private Maneuver? PickManeuver()
    {
        if (Library is not { Count: > 0 } library)
            return null;
        var pool = new List<(Maneuver Maneuver, float Weight)>();
        float total = 0f;
        foreach (var m in library)
        {
            if (!m.EligibleFor(NaturalTouch))
                continue;
            if (m.Nitro && NitroUsable?.Invoke() != true)
                continue;
            if (!EndsSafely(m))
                continue;
            float weight = m.Bias + 1f;
            if (m.Name == _lastManeuver)
                weight *= RepeatWeight;
            if (m.Name == _secondLastManeuver)
                weight *= SecondLastWeight;
            if (SignatureManeuvers != null && SignatureManeuvers.Contains(m.Name))
                weight *= SignatureWeight;
            if (weight < 0f)
                weight = MinSelectionWeight;
            pool.Add((m, weight));
            total += weight;
        }
        if (pool.Count == 0)
            return null;
        float roll = (float)_rng.NextDouble() * total;
        foreach (var (maneuver, weight) in pool)
        {
            roll -= weight;
            if (roll <= 0f)
                return maneuver;
        }
        return pool[^1].Maneuver;
    }

    // The altitude veto, run before a program can be drawn: the candidate's PREDICTED end point
    // decides, never the aeroplane's own altitude. Below AltitudeFloorM the program is dropped. At
    // or above ProbeCeilingM it is kept with no obstacle test. Between them the predicted path is
    // swept segment by segment, and any blocked segment drops it.
    // ⚠ Do not fold this into the reactive arm. That one fires on an aeroplane already below the
    // floor; this one keeps a doomed program from ever starting.
    private bool EndsSafely(Maneuver maneuver)
    {
        var path = ManeuverExecutor.PredictedPath(maneuver, _lastPos, PredictionFrame());
        float endY = path[^1].Y;
        if (endY < AltitudeFloorM)
            return false;
        if (endY >= ProbeCeilingM || ProbeBlocked is not { } probe)
            return true;
        var from = _lastPos;
        foreach (var point in path)
        {
            if (probe(from, point) is not null)
                return false;
            from = point;
        }
        return true;
    }

    // The frame a candidate program is predicted in. It is the aeroplane's own attitude when the
    // caller supplies one, else the level frame of its nose. A non-relative program composes onto
    // that frame in any case.
    private Basis PredictionFrame() =>
        _attitude ?? (_nose is { } n && n.LengthSquared() > 1e-4f
            ? new Basis(Vector3.Up, Mathf.DegToRad(AiPilot.HeadingDegOf(n)))
            : Basis.Identity);

    private void Transition(AiMode to, string reason)
    {
        if (to == Mode)
            return;
        var from = Mode;
        Mode = to;
        // The task's own life: patrol and the danger-zone run drop it (FUN_00421500 nulls the
        // target). A promotion into an engagement takes the anchor the leash is measured from.
        if (to is AiMode.Patrol or AiMode.ApproachingDangerZone or AiMode.NavigatingDangerZone)
            _pursuitAnchor = null;
        else if ((to is AiMode.Pursue or AiMode.LayOff) && _pursuitAnchor is null)
        {
            // A new task, whichever path took it, is a promotion and takes the chase's deadline.
            _pursuitAnchor = _lastPos;
            ArmChaseDeadline();
        }
        if (to != AiMode.EvasiveManeuver)
            Executor = null;
        if (from == AiMode.Stunned)
            _stunRemaining = 0f; // an override out of the stun leaves no stale expiry behind
        if (to == AiMode.Stunned && _stunRemaining <= 0f)
            _stunRemaining = StunRecoveryIntervalS;
        if (to == AiMode.AvoidCrash && ClimbOutAltitude <= 0f)
            ClimbOutAltitude = _lastPos.Y + ClimbOutM;
        if (to == AiMode.LayOff)
        {
            // The lay-off course: straight on from the entry velocity, at the entry altitude,
            // stay ahead of the pursuer rather than turning back into a head-on.
            _layOffHold = LayOffMinHoldS;
            if (new Vector2(_lastVelocity.X, _lastVelocity.Z).LengthSquared() > 1e-4f)
                LayOffHeadingDeg = AiPilot.HeadingDegOf(_lastVelocity);
            LayOffAltitude = _lastPos.Y;
        }
        _pursuedFor = 0f; // any transition restarts the sustained-pursuit clock
        ModeChanged?.Invoke(from, to, reason);
    }
}
