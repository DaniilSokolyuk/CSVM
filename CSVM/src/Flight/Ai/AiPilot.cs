using System;
using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>The non-player <see cref="FlightModel"/> driver: standing orders in, one
/// <see cref="FlightInput"/> per sim step out. A <see cref="FlightController"/> with
/// <see cref="FlightController.Pilot"/> set reads this instead of the keyboard/pad, so an AI
/// aircraft flies the exact same flight model, collision sweep and damage path a player does.
/// Orders are plain mutable fields by design: the mission script retargets an AI at runtime
/// (<c>SET_AI_NET</c>, <c>ADD_OTHER_TARGET</c>, …), so nothing here is read once at spawn.
/// The steering law is the original's own,
/// <see cref="AiControlLaw"/> (docs/org/aiControlLaw.md); this class only drives it.</summary>
public sealed class AiPilot
{
    /// <summary>Invented: how fast lay off walks the throttle toward the ease-off speed, per
    /// second (the decoded constant is the speed factor, not a lever rate).</summary>
    public const float LayOffThrottleRatePerS = 0.4f;

    /// <summary>Invented: the throttle floor while laying off, the pilot slows down, it does
    /// not park in mid-air.</summary>
    public const float LayOffMinThrottle = 0.3f;

    /// <summary>Ordered heading, degrees, the mission-data convention
    /// (<c>SpawnPoint.HeadingDeg</c>): the nose (−Z) yawed about world up by this angle.</summary>
    public float TargetHeadingDeg;

    /// <summary>The patrol net being flown, or null for bare heading/altitude orders (assigning a
    /// different follower is <c>SET_AI_NET</c>'s seam; null returns to the last derived course).
    /// Each <see cref="Next"/> re-derives heading/altitude from the follower's current target node
    /// when set; branch choices draw from its own seeded rng, so a fixed-dt run stays deterministic.
    /// ⚠ Null never happens in the original and hunts in roll (no leg, so no <see cref="PatrolAim"/>
    /// damping); on a campaign AI it means an escort whose leader left play, and nothing else.</summary>
    public AiNetFollower? Patrol;

    /// <summary>The forward-gun gunnery, or null for an unarmed pilot. When its target is live and
    /// there is no <see cref="Machine"/>, each <see cref="Next"/> re-derives orders from the
    /// target's position, a plain pursuit through <see cref="AiControlLaw"/> that takes precedence
    /// over <see cref="Patrol"/>. The host <see cref="FlightController"/> decides fire; this only
    /// steers.</summary>
    public AiGunner? Gunner;

    /// <summary>The ordnance employment, or null for a pilot that launches nothing. Armed together
    /// with <see cref="Gunner"/> and useless without it: it holds no target of its own, so the host
    /// hands it the gunner's. Steering never reads it.</summary>
    public AiRocketeer? Rocketeer;

    /// <summary>The formation escort (<see cref="AiEscort"/>), or null for a pilot that flies no
    /// station. When it has a live leader it is the WHOLE dispatch, exactly as the original's
    /// <c>mode wingman</c> fork is: only the two AI states its own law short-circuits on, stunned
    /// and avoid crash, run instead of it. ⚠ Invented: the original dereferences its leader with no
    /// null check at all (docs/org/aiPilot.md, "The escort law"), so a leader out of play is CSVM's
    /// own case, and the host seats the pilot on that leader's net instead of leaving it here.</summary>
    public AiEscort? Escort;

    /// <summary>The nine-mode state machine, or null for the bare-orders pilot above.
    /// When set, each <see cref="Next"/> steps the machine and dispatches on its mode: patrol
    /// flies <see cref="Patrol"/>, pursue chases the gunner's target, lay off holds its entry
    /// course at eased throttle so the pursuer catches up, evade and avoid crash fly the
    /// machine's own orders, an evasive maneuver plays its <see cref="ManeuverExecutor"/> until
    /// done, and stunned holds the controls neutral. Mutable like every other order.</summary>
    public AiModeMachine? Machine;

    /// <summary>The mission's <c>dzpathN</c> ribbons, or null for a session with none: with a
    /// <see cref="Machine"/> and a <see cref="Patrol"/>, reaching a net node carrying the
    /// danger-zone tag starts a run on the ribbon the node names (docs/org/aiPilot.md "The
    /// danger-zone run"). Shared across pilots, since lanes are occupancy-counted.</summary>
    public DangerZoneRibbons? DangerZones;

    /// <summary>Ordered altitude, metres (world Y).</summary>
    public float TargetAltitude = 400f;

    /// <summary>The commanded throttle lever, 0–1, the lever's CURRENT state, walked toward
    /// <see cref="AiControlLaw"/>'s own desired speed each step, not a standing order the pilot
    /// holds (the original's lever, <c>obj+0x124</c>, works the same way). ⚠ Do not reintroduce an
    /// altitude-leash throttle rule here: the real law's wings-level rule and elevator deadband
    /// replaced that placeholder. Presetting still seeds the walk; <see cref="AiMode.LayOff"/> is
    /// the one mode that overrides the law's answer (<see cref="FlyLayOff"/>).</summary>
    public float Throttle = 0.85f;

    // How far ahead an ordered heading is projected to make the aim point the law wants. A PORT
    // ARTIFACT, not a game value: the original never carries heading/altitude orders, only points
    // (a net node, a target, or its own position plus a climb-out offset), so this distance exists
    // only to convert our order representation. It sets the vertical angle of an altitude capture
    // and nothing else; 1000 m matches the one climb-out offset the original does carry.
    private const float OrderAimRangeM = 1000f;

    // The original's own avoid-crash aim point: straight up from the aircraft by this much.
    private const float ClimbOutAimM = 1000f;

    // INVENTED: how far right of its own ground track a NETTED pilot's climb-out is displaced,
    // making the 1000 m pull-up a 45° break to the right rather than a vertical one. An escort
    // flies the decoded vertical climb instead. See ClimbOutAim.
    private const float ClimbOutBreakM = 1000f;

    // The patrol aim's two decoded constants (FUN_0041d1f0 case 0, the aim built at 0x0041d30b-
    // 0x0041d4e0): the fraction of its own cross-track error the aim point carries (0x0060355c)
    // and the cap on that displacement (0x00603550, tested squared against 40000 at 0x00603558).
    private const float PatrolCrossTrackCarry = 0.9f;
    private const float PatrolCrossTrackCapM = 200f;

    // The merge rule's five decoded constants (FUN_0041d9f0 at 0x0041e130): the range it arms
    // inside, the fraction of each party's own speed its closure test wants along the line of
    // sight, the flat speed the aim velocity collapses to, and the vertical-bias endpoints.
    private const float MergeRangeM = 400f;
    private const float MergeClosureFraction = 0.8f;
    private const float MergeSpeedMps = 31.292799f;
    private const float MergeDiveSpeedMps = 22.352f;
    private const float MergeClimbSpeedMps = 40.2336f;
    private const float MergeVerticalBiasM = 0.3f;
    private const float MergeVerticalBiasPerMps = 0.033554047f;

    // The stun countdown of a pilot WITHOUT a mode machine, seconds; a pilot with one keeps its
    // stun in the machine's Stunned mode instead (one timer per pilot, never two).
    private float _bareStunRemainingS;

    // The rail integrator of the run in progress, built at the lock and dropped at the exit.
    private DangerZoneRail? _rail;

    // The net leg being flown when the run started, kept so the exit's re-seat can refuse it.
    private int _zoneEntryLegFrom = -1;
    private int _zoneEntryLegTo = -1;

    /// <summary>The danger-zone run in progress, from the approach through the rail to the exit;
    /// null between runs.</summary>
    public DangerZoneRun? ZoneRun { get; private set; }

    /// <summary>The pose the last <see cref="Next"/> wrote off the ribbon while
    /// <see cref="AiMode.NavigatingDangerZone"/> held, or null when the flight model flies. The
    /// host applies it in place of the model step, the original's physics bypass for state 5.</summary>
    public Transform3D? RailPose { get; private set; }

    /// <summary>The speed along the nose that goes with <see cref="RailPose"/>.</summary>
    public float RailSpeed { get; private set; }

    /// <summary>Whether the LAST <see cref="Next"/> actually steered to <see cref="Patrol"/>'s
    /// node, as opposed to pursuing, evading or holding the bare orders. Reported rather than
    /// re-derived from the mode: the dispatch in <see cref="Next"/> is the only thing that
    /// decides it, so an observer (the debug overlay's leashes) that asked the mode machine
    /// instead could drift from it.</summary>
    public bool SteeringPatrol { get; private set; }

    /// <summary>Whether the controls are masked to neutral this step: the original's AI state 4.
    /// True while <see cref="Machine"/> sits in <see cref="AiMode.Stunned"/>, or, for a pilot
    /// without a machine, while its own countdown runs. Read by the host for the gates the
    /// original keys on state 4 (the ground-blow probe) and by the debug HUD.</summary>
    public bool IsStunned =>
        Machine is { } machine ? machine.Mode == AiMode.Stunned : _bareStunRemainingS > 0f;

    /// <summary>Seconds of stun left, zero when not stunned.</summary>
    public float StunRemainingS =>
        Machine is { } machine ? machine.StunRemainingS : Mathf.Max(0f, _bareStunRemainingS);

    /// <summary>Aims the standing orders at holding the given spawn pose: heading from the
    /// pos→look-at pair, altitude from the position, what a freshly spawned patrol-less AI
    /// flies until something retargets it.</summary>
    public static AiPilot HoldingCourse(Vector3 pos, Vector3 lookAt)
    {
        var dir = lookAt - pos;
        return new AiPilot
        {
            TargetHeadingDeg = HeadingDegOf(dir),
            TargetAltitude = pos.Y,
        };
    }

    /// <summary>The heading (degrees, mission-data convention) whose forward vector is
    /// <paramref name="dir"/>'s horizontal projection.
    /// ⚠ Not what the pilot's compass reads; <see cref="Hud.CompassTape.ReadingDeg"/> runs the other
    /// way round, and the two agree only at north and south.</summary>
    public static float HeadingDegOf(Vector3 dir) =>
        Mathf.RadToDeg(Mathf.Atan2(-dir.X, -dir.Z));

    /// <summary>The decoded aspect test (<c>FUN_0041d9f0</c> at <c>0x0041dd49</c>): whether the
    /// pursuer sits on its victim's own axis, within the pilot's <c>quick_draw_angle</c> of it.
    /// Both cones count, since the original compares the magnitude of the dot against the victim's
    /// backward axis (docs/org/aiPilot.md); only the reversed arm this port omits ever tells the
    /// two apart.</summary>
    public static bool IsOnGunAxis(Vector3 toQuarry, Vector3 quarryNose, float quickDrawAngleDeg)
    {
        if (toQuarry.LengthSquared() < 1f || quarryNose.LengthSquared() < 1e-6f)
            return false;
        float cos = toQuarry.Normalized().Dot(quarryNose.Normalized());
        return Mathf.Abs(cos) > Mathf.Cos(Mathf.DegToRad(quickDrawAngleDeg));
    }

    /// <summary>The decoded avoid-crash aim point, both laws' own: the aeroplane's position with
    /// <see cref="ClimbOutAimM"/> added to Y and its X and Z untouched (the escort law
    /// <c>FUN_0041e760</c> at <c>0x0041e7c8</c>, the net follower <c>FUN_0041d1f0</c> at
    /// <c>0x0041d2b4</c>; docs/org/aiPilot.md). What an escort flies, since its measured lateral
    /// break below was never taken on one.</summary>
    public static Vector3 ClimbOutAim(Vector3 pos) => pos + (Vector3.Up * ClimbOutAimM);

    /// <summary>Avoid crash's aim point. ⚠ Invented: the original climbs out at its own position
    /// plus 1000 m of altitude and nothing else. This adds a <see cref="ClimbOutBreakM"/>
    /// displacement to the RIGHT of the aeroplane's own ground track, the aviation right-of-way
    /// convention, measured to cut merges (docs/org/aiPilot.md). Off the track, not the airframe's
    /// right axis, so a rolled or inverted aeroplane breaks the same way as a level one.</summary>
    public static Vector3 ClimbOutAim(Vector3 pos, Vector3 velocity)
    {
        var track = new Vector3(velocity.X, 0f, velocity.Z);
        var right = track.LengthSquared() > 1e-4f
            ? new Vector3(-track.Z, 0f, track.X).Normalized()
            : Vector3.Right;
        return pos + (Vector3.Up * ClimbOutAimM) + (right * ClimbOutBreakM);
    }

    /// <summary>The patrol aim point (<c>FUN_0041d1f0</c> case 0): <paramref name="target"/>
    /// displaced sideways by 0.9 of the aeroplane's own cross-track error from its leg, capped at
    /// 200 m, so the commanded course lies nearly parallel to the leg (docs/org/aiPilot.md).
    /// ⚠ Never aim at the node itself. <see cref="AiControlLaw"/>'s roll is a relay on the SIGN of
    /// the lateral error, so a full-strength convergence error banks hard each way and never
    /// settles (`BL-387`); this displacement is the only damping the original has.</summary>
    public static Vector3 PatrolAim(Vector3 pos, Vector3 legStart, Vector3 target)
    {
        var leg = target - legStart;
        if (leg.LengthSquared() < 1e-6f)
            return target;
        leg = leg.Normalized();
        var fromStart = pos - legStart;
        var off = (fromStart - (leg * fromStart.Dot(leg))) * PatrolCrossTrackCarry;
        float offSq = off.LengthSquared();
        if (offSq > PatrolCrossTrackCapM * PatrolCrossTrackCapM)
            off *= PatrolCrossTrackCapM / Mathf.Sqrt(offSq);
        return target + off;
    }

    /// <summary>The decoded merge test (<c>FUN_0041d9f0</c> at <c>0x0041e130</c>): the victim is
    /// inside <see cref="MergeRangeM"/> and the two are flying AT each other, each party's own
    /// velocity lies within <see cref="MergeClosureFraction"/> of its speed along the line of
    /// sight, which is about 37° of it. The original also tests that the victim is a
    /// <c>jet</c>/<c>wingman</c> and never arms this against a ground target;
    /// <see cref="FlyPursuit"/> keeps that gate on <see cref="PursuitQuarry.IsAircraft"/>.</summary>
    public static bool IsMerging(Vector3 toQuarry, Vector3 ownVelocity, Vector3 quarryVelocity)
    {
        float range = toQuarry.Length();
        float ownSpeed = ownVelocity.Length();
        float quarrySpeed = quarryVelocity.Length();
        if (range >= MergeRangeM || range < 1e-3f || ownSpeed < 1e-3f || quarrySpeed < 1e-3f)
            return false;
        var u = toQuarry / range;
        return ownVelocity.Dot(u) > MergeClosureFraction * ownSpeed
            && quarryVelocity.Dot(u) < -MergeClosureFraction * quarrySpeed;
    }

    /// <summary>The merge's vertical aim bias per unit of the line of sight's horizontal
    /// magnitude, metres: a dive below <see cref="MergeDiveSpeedMps"/>, a climb above
    /// <see cref="MergeClimbSpeedMps"/>, linear between. ⚠ Worth at most <see
    /// cref="MergeVerticalBiasM"/>, 0.3 m (docs/org/aiPilot.md); ported anyway so a decoded term
    /// left out does not get re-derived later as a bug.</summary>
    public static float MergeVerticalBias(float ownSpeed) => ownSpeed switch
    {
        <= MergeDiveSpeedMps => -MergeVerticalBiasM,
        >= MergeClimbSpeedMps => MergeVerticalBiasM,
        _ => (ownSpeed - MergeSpeedMps) * MergeVerticalBiasPerMps,
    };

    /// <summary>The stun handler's entry (decoded: <c>FUN_004200d0</c>): hands off the stick for
    /// <paramref name="seconds"/>, then back to what it was doing, the aircraft staying on the
    /// flight model with the lever where it was. Re-entrant: a second call OVERWRITES the remaining
    /// time (clock + seconds, never a max), which the smoke screen relies on every frame. The victim
    /// guards (dead, human, <c>+0xf8</c>) are the caller's: go through
    /// <c>FlightController.TryStunPilot</c>.</summary>
    public void Stun(float seconds)
    {
        if (seconds <= 0f)
            return;
        if (Machine is { } machine)
            machine.Stun(seconds, FormattableString.Invariant($"stunned for {seconds:0.0} s"));
        else
            _bareStunRemainingS = seconds;
    }

    /// <summary>Clears a running stun outright: the respawn reset, so a pilot never wakes up
    /// stunned in a fresh airframe.</summary>
    public void ClearStun()
    {
        _bareStunRemainingS = 0f;
        if (Machine is { Mode: AiMode.Stunned } machine)
            machine.Enter(AiMode.Patrol, "respawned");
    }

    /// <summary>One sim step's stick and throttle for the current orders. Pure over the model's
    /// state and this instance's fields (no clocks, and the only randomness is
    /// <see cref="Patrol"/>'s own seeded branch draw), so a fixed-dt run is deterministic. The
    /// one node read is a turret or structure quarry's position, which lives on its node alone.</summary>
    public FlightInput Next(FlightModel model, float dt)
    {
        var quarry = PursuitQuarry.Of(Gunner?.Target, Gunner);
        SteeringPatrol = false;   // SteerPatrol sets it when it actually flies the net
        RailPose = null;          // set again below only while the rail writes the pose

        // The mode machine, when present, decides which input source flies this step;
        // without one the bare priority stands (gunner target, then patrol, then orders).
        if (Machine is { } machine)
        {
            var mode = machine.Update(model.Position, model.VelocityDir * model.Speed,
                quarry?.Position, quarry?.Mode, dt,
                quarry?.Velocity, quarry?.IsHumanPiloted ?? false,
                nose: -model.Attitude.Z, targetNose: quarry?.Nose, attitude: model.Attitude,
                quarryIsVehicle: quarry?.IsVehicle ?? true,
                quarryIsPrimary: quarry?.IsPrimaryTarget ?? false);
            // The two states the escort law itself short-circuits on come first, then the escort,
            // which is the whole dispatch for a wingman, a maneuver included, since the original
            // never reaches its maneuver arm from the mode-4 fork.
            if (mode == AiMode.Stunned)
                return StunnedInput();
            if (mode == AiMode.AvoidCrash)
                return FlyClimbOut(model, dt, machine);
            if (Escort is { Leader.InPlay: true } escorting)
                return FlyEscort(model, dt, escorting, quarry);

            // The dynamic entry into a danger-zone run, on the original's own gate: a combat
            // state (its state 0) with the evade flag up. That is a hit pilot being chased, and
            // a maneuver, state 1, never reaches it.
            if (machine.Evading && mode is AiMode.Pursue or AiMode.LayOff or AiMode.Evade)
            {
                TryDaredevilDangerZone(model, machine);
                mode = machine.Mode;
            }

            switch (mode)
            {
                case AiMode.ApproachingDangerZone when ZoneRun != null:
                    return FlyDangerZoneApproach(model, dt, machine);

                case AiMode.NavigatingDangerZone when _rail != null:
                    return FlyRail(model, dt, machine);

                case AiMode.ApproachingDangerZone:
                case AiMode.NavigatingDangerZone:
                    machine.Enter(AiMode.Patrol, "no danger-zone run to fly");
                    return FlyPatrol(model, dt);

                case AiMode.EvasiveManeuver when machine.Executor is { } executor:
                    return executor.Next(model, dt);

                // The evade flag with nothing eligible to fly: the engagement stands, marked.
                case AiMode.Evade when quarry is { } marked:
                    return FlyPursuit(model, dt, marked);

                case AiMode.Pursue when quarry is { } prey:
                    return FlyPursuit(model, dt, prey);

                case AiMode.LayOff when quarry is { } pursuer:
                    return FlyLayOff(model, dt, machine, pursuer);

                default: // patrol, and any mode whose quarry went away
                    var input = FlyPatrol(model, dt);
                    if (mode == AiMode.Patrol && Patrol is { ArrivedNode: { EntersDangerZone: true } node })
                        TryStartDangerZone(node.DangerZonePath, model, machine);
                    return input;
            }
        }

        // A pilot with no machine keeps its own countdown; the mask is the same either way.
        if (_bareStunRemainingS > 0f)
        {
            _bareStunRemainingS -= dt;
            return StunnedInput();
        }

        if (Escort is { Leader.InPlay: true } escort)
            return FlyEscort(model, dt, escort, quarry);

        return quarry is { } bare ? FlyPursuit(model, dt, bare) : FlyPatrol(model, dt);
    }

    // The stun mask: stick and rudder neutral, the throttle lever left where it was. The
    // original zeroes exactly the three channels raw and copied (+0x100/+0x108/+0x10c and
    // +0x114/+0x11c/+0x120, docs/org/aiControlLaw.md "The channels") and never the lever at
    // +0x124, so a stunned aircraft coasts under power rather than falling out of the sky.
    private FlightInput StunnedInput() =>
        new() { Throttle = Mathf.Clamp(Throttle, 0f, 1f) };

    // Runs the original's law for this step's aim point and keeps the lever it walked.
    // The skill factor is the machine's `sixth_sense_factor`, which the decode shows
    // multiplies all three channels every step; a pilot with no machine gets a neutral 1.
    private FlightInput Fly(FlightModel model, float dt, Vector3 aimPoint, Vector3 aimVelocity,
        in AiLawParams p, bool emergency = false, bool engaged = false, bool gunLead = false,
        bool stationKeeping = false, bool openAltitudeBand = false)
    {
        var input = AiControlLaw.Steer(model, aimPoint, aimVelocity, p, Throttle, dt,
            emergency, engaged, gunLead, Machine?.SixthSenseFactor ?? 1f, stationKeeping,
            openAltitudeBand);
        Throttle = input.Throttle;
        return input;
    }

    // Pursuit: aim at the decoded lead offset ahead of the victim's facing, flown on the engaged
    // table; on the victim's own axis (IsOnGunAxis) aim at it directly and let the law solve the
    // firing problem. A turret or structure has no axis, so the original takes that arm for it
    // every frame: the aim point is the part itself, with the gun lead on, and the merge rule
    // never arms (FUN_0041d9f0's TargetVehicle cast). A merge (IsMerging) replaces the aim
    // velocity with a flat MergeSpeedMps along the line of sight (docs/org/aiPilot.md).
    private FlightInput FlyPursuit(FlightModel model, float dt, in PursuitQuarry quarry)
    {
        var toQuarry = quarry.Position - model.Position;
        if (new Vector2(toQuarry.X, toQuarry.Z).LengthSquared() > 1f)
            TargetHeadingDeg = HeadingDegOf(toQuarry);
        TargetAltitude = quarry.Position.Y;

        var nose = quarry.Nose;
        var vel = quarry.Velocity;
        bool onAxis = !quarry.IsAircraft
            || (Gunner is { } g && IsOnGunAxis(toQuarry, nose, g.QuickDrawAngleDeg));
        var aim = onAxis
            ? quarry.Position
            : quarry.Position + (nose * AiControlLaw.LeadOffsetFor(vel.Length()));
        var aimVelocity = vel;
        if (quarry.IsAircraft && IsMerging(toQuarry, model.VelocityDir * model.Speed, vel))
        {
            var u = toQuarry.Normalized();
            aimVelocity = u * MergeSpeedMps;
            aim.Y += new Vector2(u.X, u.Z).Length() * MergeVerticalBias(model.Speed);
        }
        return Fly(model, dt, aim, aimVelocity, AiLawParams.Engaged, engaged: true, gunLead: onAxis);
    }

    // The climb-out both laws share: 1000 m above the aeroplane itself, on the emergency arm, with
    // no aim velocity. An escorting pilot flies the DECODED vertical climb on the wingman table,
    // which is the pair the escort law passes where the net follower passes its own; every other
    // pilot keeps the invented lateral break, whose merge measurement was taken on netted aircraft.
    // The heading order only follows an aim that has a horizontal leg, so a vertical one holds it.
    private FlightInput FlyClimbOut(FlightModel model, float dt, AiModeMachine machine)
    {
        bool escorting = Escort is { Leader.InPlay: true };
        var climbOut = escorting
            ? ClimbOutAim(model.Position)
            : ClimbOutAim(model.Position, model.VelocityDir * model.Speed);
        var toClimbOut = climbOut - model.Position;
        if (new Vector2(toClimbOut.X, toClimbOut.Z).LengthSquared() > 1f)
            TargetHeadingDeg = HeadingDegOf(toClimbOut);
        TargetAltitude = machine.ClimbOutAltitude;
        var table = escorting ? AiLawParams.Wingman : AiLawParams.AvoidCrash;
        return Fly(model, dt, climbOut, Vector3.Zero, table, emergency: true);
    }

    // The formation escort: the station AiEscort computes, flown on the wingman table. The
    // leader's frame comes off its published transform, the sim pose between sim steps.
    // ⚠ The one call that lifts the desired-speed ceiling (AiControlLaw.StationCeiling): a leader
    // cruising above 250 mph is faster than anything the decoded ceiling lets an escort ask for,
    // so under it the escort throttles back while behind and the station is never regained.
    private FlightInput FlyEscort(FlightModel model, float dt, AiEscort escort,
        PursuitQuarry? quarry)
    {
        var leader = escort.Leader!;
        var station = escort.Next(
            model.Position,
            model.Speed,
            new EscortLeader
            {
                Position = leader.WorldPosition,
                Attitude = leader.GlobalTransform.Basis,
                Velocity = leader.WorldVelocity,
                IsPlayer = leader.IsHumanPiloted,
            },
            quarry is not { } prey
                ? null
                : new EscortQuarry
                {
                    Position = prey.Position,
                    Backward = -prey.Nose,
                    Velocity = prey.Velocity,
                },
            out var aimVelocity);

        var toStation = station - model.Position;
        if (new Vector2(toStation.X, toStation.Z).LengthSquared() > 1f)
            TargetHeadingDeg = HeadingDegOf(toStation);
        TargetAltitude = station.Y;
        return Fly(model, dt, station, aimVelocity, AiLawParams.Wingman, stationKeeping: true);
    }

    // Lay off (the rubber-band assist): steers the course captured at mode entry on the cruise
    // table, then OVERRIDES the law's lever with a walk toward SixthSenseFactor × the pursuer's
    // speed. ⚠ That override is a remake-only assist, not the original's lay-off, which flies the
    // same table with a flat 0.8 lever floor and no speed match, keep it, landed and playtested.
    private FlightInput FlyLayOff(FlightModel model, float dt, AiModeMachine machine,
        in PursuitQuarry pursuer)
    {
        TargetHeadingDeg = machine.LayOffHeadingDeg;
        TargetAltitude = machine.LayOffAltitude;
        var input = Fly(model, dt, OrderAim(model), Vector3.Zero, AiLawParams.Cruise);
        float desired = machine.SixthSenseFactor * pursuer.Velocity.Length();
        float step = LayOffThrottleRatePerS * dt;
        Throttle = model.Speed > desired
            ? Mathf.Max(LayOffMinThrottle, Throttle - step)
            : Mathf.Min(1f, Throttle + step);
        input.Throttle = Throttle;
        return input;
    }

    // Patrol: the node is a point with no velocity, which is the shape the law wants, but it is
    // NOT the aim point, PatrolAim displaces it off the aeroplane's own cross-track error, and
    // flying at the node itself is what BL-387 was. The nose seats the walk on a real edge from
    // the first step, so there is a leg to be off straight away. Without a net the standing
    // heading/altitude orders are projected into a point. All of it on the original's cruise table.
    private FlightInput FlyPatrol(FlightModel model, float dt)
    {
        if (Patrol is not { } patrol)
            return Fly(model, dt, OrderAim(model), Vector3.Zero, AiLawParams.Cruise);

        SteeringPatrol = true;
        patrol.Update(model.Position, -model.Attitude.Z);
        var node = patrol.CurrentTarget;
        var toNode = node - model.Position;
        if (new Vector2(toNode.X, toNode.Z).LengthSquared() > 1f)
            TargetHeadingDeg = HeadingDegOf(toNode);
        TargetAltitude = node.Y;
        var aim = patrol.LegStart is { } legStart
            ? PatrolAim(model.Position, legStart, node)
            : node;
        return Fly(model, dt, aim, Vector3.Zero, AiLawParams.Cruise);
    }

    // The node-tag entry (FUN_0041d1f0 after the walk step): a numbered tag resolves dzpath<N>
    // and the run enters from whichever end is nearer; a negative one takes the nearest end of
    // any active ribbon. No roll, no range and no difficulty test on this arm.
    private void TryStartDangerZone(int pathIndex, FlightModel model, AiModeMachine machine)
    {
        if (DangerZones is not { } zones)
            return;
        DangerZoneRibbon ribbon;
        bool farEnd;
        var pos = model.Position;
        if (pathIndex >= 0)
        {
            if (zones.ByIndex(pathIndex) is not { Active: true } named)
                return;
            ribbon = named;
            farEnd = pos.DistanceTo(ribbon.End(true)) < pos.DistanceTo(ribbon.End(false));
        }
        else if (zones.NearestEnd(pos) is { } nearest)
        {
            (ribbon, farEnd) = nearest;
        }
        else
        {
            return;
        }
        StartDangerZoneRun(ribbon, farEnd, model, machine, "tagged node");
    }

    // The proximity entry (FUN_0041d9f0's first statement, 0x0041da3e, into FUN_004210e0's
    // unforced arm): a hit pilot rolls daredevil. A passed roll takes a zone end within 500 m
    // that leads away. It is an evasion, which is why the evade flag is its gate. A look that
    // finds nothing arms the 5 s retry hold.
    private void TryDaredevilDangerZone(FlightModel model, AiModeMachine machine)
    {
        if (DangerZones is not { } zones || ZoneRun != null || !machine.RollDaredevil())
            return;
        if (zones.ProximityPick(model.Position, machine.NaturalTouch) is not { } pick)
        {
            machine.StampDangerZoneRetry();
            return;
        }
        StartDangerZoneRun(pick.Ribbon, pick.FarEnd, model, machine, "dare devil test passed");
    }

    // What both entries do once a ribbon and an end are chosen: the run record, the leg the exit
    // re-seats off, and state 2. The machine's own transition drops the standing target
    // (FUN_00421500 nulls +0x948).
    private void StartDangerZoneRun(DangerZoneRibbon ribbon, bool farEnd, FlightModel model,
        AiModeMachine machine, string why)
    {
        ZoneRun = new DangerZoneRun(ribbon, farEnd);
        _rail = null;
        _zoneEntryLegFrom = Patrol?.LegStartIndex ?? -1;
        _zoneEntryLegTo = Patrol?.CurrentIndex ?? -1;
        machine.Enter(AiMode.ApproachingDangerZone,
            $"'{ribbon.Name}' from its {(farEnd ? "far" : "near")} end, {model.Position.DistanceTo(ZoneRun.Point):0} m out ({why})");
    }

    // The approach (FUN_004216e0): the run's current point on the emergency table, no aim
    // velocity, and inside LockRangeM of it the rail takes the pose. The altitude band is opened
    // for this solve alone, so a ribbon entry below the AI floor or above the airframe's ceiling
    // is flown to rather than clamped away from.
    private FlightInput FlyDangerZoneApproach(FlightModel model, float dt, AiModeMachine machine)
    {
        var run = ZoneRun!;
        var aim = run.Point;
        var toAim = aim - model.Position;
        if (new Vector2(toAim.X, toAim.Z).LengthSquared() > 1f)
            TargetHeadingDeg = HeadingDegOf(toAim);
        TargetAltitude = aim.Y;
        var input = Fly(model, dt, aim, Vector3.Zero, AiLawParams.AvoidCrash, openAltitudeBand: true);
        float range = toAim.Length();
        if (range < DangerZoneRibbon.LockRangeM)
        {
            _rail = new DangerZoneRail(run, model.Position, model.Attitude,
                model.VelocityDir * model.Speed, model.Speed);
            machine.Enter(AiMode.NavigatingDangerZone,
                FormattableString.Invariant($"'{run.Ribbon.Name}' locked at {range:0} m"));
        }
        return input;
    }

    // The rail (FUN_00490590): the pose comes off the integrator and the controls sit neutral;
    // running off the ribbon hands the walk back to the net, re-seated where the run left it and
    // refused the leg it was flying at the entry, so the exit continues the course.
    private FlightInput FlyRail(FlightModel model, float dt, AiModeMachine machine)
    {
        var rail = _rail!;
        bool flying = rail.Step(dt);
        RailPose = new Transform3D(rail.Attitude, rail.Position);
        RailSpeed = rail.Speed;
        TargetHeadingDeg = HeadingDegOf(-rail.Attitude.Z);
        TargetAltitude = rail.Position.Y;
        if (!flying)
        {
            string name = rail.Run.Ribbon.Name;
            rail.Run.Release();
            ZoneRun = null;
            _rail = null;
            Patrol?.Reseat(_zoneEntryLegFrom, _zoneEntryLegTo);
            _zoneEntryLegFrom = -1;
            _zoneEntryLegTo = -1;
            machine.Enter(AiMode.Patrol, $"'{name}' flown, back to the net");
        }
        return new FlightInput { Throttle = Mathf.Clamp(Throttle, 0f, 1f) };
    }

    // The aim point a bare heading/altitude order becomes (see
    // OrderAimRangeM for why the distance is a port artifact).
    private Vector3 OrderAim(FlightModel model)
    {
        var dir = new Basis(Vector3.Up, Mathf.DegToRad(TargetHeadingDeg)) * Vector3.Forward;
        var aim = model.Position + (dir * OrderAimRangeM);
        aim.Y = TargetAltitude;
        return aim;
    }
}
