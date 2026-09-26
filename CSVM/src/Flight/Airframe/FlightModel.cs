using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>Pilot inputs, all in [-1, 1] except Throttle in [0, 1].
/// Pitch + = pull (nose up), Roll + = bank left, Yaw + = nose left.</summary>
public struct FlightInput
{
    public float Pitch, Roll, Yaw, Throttle;

    /// <summary>Ground blow's probe result, filled by the caller because only it has the world:
    /// the WORLD-frame surface normal of the nearest hit on a ray cast forward along the nose,
    /// and the distance to it in metres. <see cref="Vector3.Zero"/> means no hit, and it is the
    /// only "no hit" signal, matching the original, which leaves its zero-initialised output vector
    /// alone on a miss, a back-facing surface or a filtered emitter and so adds nothing.</summary>
    public Vector3 GroundBlowNormal;

    /// <inheritdoc cref="GroundBlowNormal"/>
    public float GroundBlowDistM;

    /// <summary>Multiplier for both AI ground-blow terms during the post-drop settling window.
    /// A negative value suppresses the term; zero keeps the normal factor, preserving callers
    /// that do not set it.</summary>
    public float AiGroundBlowScale;

    /// <summary>Squared HORIZONTAL distance in m² from this aircraft to the nearest human pilot,
    /// filled by the caller because only it knows where the humans are. It selects the original's
    /// far-field plant (<see cref="FlightModel.FarFieldPlant"/>); the vertical separation is
    /// deliberately excluded, as the original's own helper drops it.
    /// ⚠ Zero, the default, is "at a human", which keeps every caller that says nothing on the
    /// near-field aerodynamics.</summary>
    public float NearestHumanDistSqM;

    /// <summary>The nitro boost flag, filled by the caller from its <see cref="NitroSystem"/>.
    /// While set the thrust lever is REPLACED by <see cref="FlightModel.BoostLever"/> and the drag
    /// coefficient scaled by <see cref="FlightModel.BoostDragFactor"/>; the throttle itself is
    /// untouched, so the far-field cruise target and the lever slew never see it.</summary>
    public bool Boost;
}

/// <summary>
/// Arcade flight dynamics parameterised by the original's zrdr stats and decoded from its own
/// force path. Thrust, drag, gravity and lift integrate on the velocity VECTOR, so speed passes
/// through zero. Lift is a demanded load factor rather than a fraction of gravity, drag a
/// parabolic polar in MACH with no induced term, thrust a Mach curve times a linear lever.
/// Each axis carries the authored speed-authority curve, which scales the stick command only and
/// never the bank coupling or the weathervane.
/// Decode: docs/org/flightModel.md. The two force paths, the plumbing and the standing
/// decode-vs-footage gaps: this module's entry in docs/architecture.md.
/// ⚠ Do not retune these constants by feel. Every one is classified in docs/org/flightModel.md's
/// "The plant's constant inventory" and pinned by CSVM.Tests/FlightConstantInventoryTests.
/// </summary>
public sealed class FlightModel
{
    public Vector3 Position;
    public Basis Attitude = Basis.Identity;       // body→world; nose −Z, up +Y (Godot frame)
    public Vector3 BodyRates;                     // quaternion half-angle rad/s: x pitch, y yaw, z roll
    public Vector3 VelocityDir = Vector3.Forward;
    public float Speed;                           // m/s along VelocityDir
    public float Throttle;
    // The boost flag as of the last Step, for the instruments and the engine-audio pins that read it.
    public bool Boosting;
    // deg: angle(nose, VelocityDir) at frame start, i.e. before this step's forces move
    // VelocityDir, see Step()'s "α" comment. An emergent LAG behind the lift demand, not a
    // modelled aerodynamic incidence. Reported for instruments only, no force term reads it.
    public float Alpha;
    // The demanded load factor in G at this step, the length of the lift demand's body X/Y
    // projection over StandardG, reported BEFORE the ±5/9 clamp and the aerodynamic ceiling below
    // it. Instruments only; no force term reads it. Pre-clamp is deliberate: it is the most
    // generous reading of "the G this aircraft is pulling", which makes it the right quantity to
    // measure the authored highGs/lowGs control limiters against (ControlLimiterTests).
    public float LoadFactorDemand;

    // Thrust available: the parasite drag force the airframe would feel at a Mach-tracking
    // reference speed, divided by Mach, scaled by EnginePower × RefArea. Nothing here is fitted,
    // every number is the original's (docs/org/flightModel.md, "Thrust available").
    // ⚠ Read EnginePower from the plane's stock Lvl-2 engine row; the Lvl-1 row inflates it ~32 %.
    // ⚠ Do not remove the Mach floor. The 1/M is a real singularity at rest, and the floor is the
    // original's own guard rather than one invented here.
    private const float ThrustMachFloor = 0.1f;
    private const float ThrustVRefSlope = 0.84f;
    private const float ThrustVRefMach = 0.112f;
    private const float ThrustMachTrim = 1f / 60f;
    private const float ThrustPowMach = 1.41f;
    // Base of the Mach-dependent divisor: 1.33 × the atmosphere's own scale factor, the third output
    // of the band step beside density and the speed of sound.
    // ⚠ Take the band's factor, never the dense one everywhere. Above the edge the base falls below
    // 1, so the divisor shrinks with Mach where below it grows, and holding the dense value there
    // understates thin-band thrust. docs/org/flightModel.md, "Thrust available".
    private const float DenseThrustPowBase = 1.33f * 0.98842078f;
    private const float ThinThrustPowBase = 1.33f * 0.7348f;

    // Available thrust is then scaled by the nose's attitude, before the engine-power and
    // reference-area scaling: a climb LOSES thrust (0.6612× straight up) and a dive GAINS it
    // (1.24× straight down). Two immediates in the original's force accumulator, the second
    // one-sided, see AttitudeThrustScale. docs/org/flightModel.md, "Attitude thrust".
    private const float AttitudeThrustBoth = 0.24f;
    private const float AttitudeThrustUp = 0.13f;

    // The hard lift clamp, in G, a LOAD FACTOR, never an angle (docs/org/flightModel.md, "Step 3").
    // ⚠ Do not re-derive this as an incidence limit. It clamps the demanded acceleration, so an
    // angle form looks right at small inputs and diverges at the limits.
    // ⚠ Do not fold this together with the authored highGs/lowGs control limiters. Those are a
    // wider pair sitting at or past this clamp, unreachable on all eleven airframes and therefore
    // not implemented. The negative bound here is likewise unreachable, the demand being a length.
    private const float LiftGMin = -5f;
    private const float LiftGMax = 9f;

    // The aerodynamic ceiling on the delivered lift coefficient: C_L ≤ ClMaxStatic − ClMaxMach·Mach.
    // This, not the load clamp, is what makes a slow aircraft unable to carry its own weight, so
    // it is the term the stall speed falls out of, and it is the only place RefArea and air density
    // enter lift at all (below the ceiling lift is independent of both).
    private const float ClMaxStatic = 0.75f;
    private const float ClMaxMach = 0.15f;

    // Atmosphere: the original's two-band step on the aircraft's own altitude, with no gradient
    // between them (FUN_0041aca0, threshold live at 0x0071bb3c). The intermediates run in imperial
    // (ft/s, slug/ft³, lb/ft²) and only the results come back to metric. Crossing into the thin band
    // divides lift by 16.73 and thrust by 22.0 at once, which is what ceilings a climb.
    // docs/org/flightModel.md, "The resting altitude cap is the atmosphere band edge".
    private const float BandThresholdFt = 6561.6796875f;
    private const float DenseDensitySlugPerFt3 = 2.2688e-3f;
    private const float DenseSoundFps = 1109.5f;
    private const float ThinDensitySlugPerFt3 = 1.3560e-4f;
    private const float ThinSoundFps = 968f;
    private const float FeetPerMetre = 3.28084f;
    private const float MetresPerFoot = 0.3048f;
    // The force/acceleration conversion the lift and gravity terms share: lift force is
    // (load factor × Weight) and acceleration is (force × StandardG / Weight), so a demanded load
    // factor of 1 is StandardG of acceleration. Gravity enters the demand as nom_gravity and leaves
    // it as nom_gravity, which is why level flight at zero incidence cancels weight identically.
    private const float StandardG = 9.82f;

    // The two stall cues sit on DIFFERENT quantities and neither is a TUNE. The nose-drop threshold
    // (isStalled, below) is the aircraft's own computed StallSpeed, an airspeed margin. The STALL
    // lamp is a LOAD-FACTOR margin: it lights below 2.35 g of AVAILABLE lift, decoded.
    // ⚠ Not a fraction of fd_speed. That reading came from footage and fits only because lift goes
    // as v², so it silently drops the AoA-cap condition.
    // docs/org/flightModel.md, "The two stall cues".
    private const float StallWarnLoadFactor = 2.35f;

    // Numerical backstop, NOT a terminal speed: it catches a loop energy pump or a dt spike.
    // ⚠ Do not tighten this until it binds. A cap that binds replaces a measured terminal with a
    // guess, and the aerodynamics already terminate the Bloodhawk's 71° dive at 1.11 × fd_speed.
    private const float MaxDiveSpeedFrac = 1.75f;

    // Ground blow's two constants that are NOT in player.json (the three that are live on
    // PlaneStats). Both are decoded, neither is a TUNE: the 0.05 is an immediate in the player
    // branch, and the 2.0 is a global whose only writer is the original's `gbc` debug console
    // command, so no data key can move it.
    private const float GroundBlowIntoFactor = 0.05f;  // what a command INTO the obstacle is met with
    private const float GroundBlowVelocitySteer = 2f;  // 1/s at contact: velocity steered onto the nose

    // Decoded ABSENT, held at 0: the original never rotates the velocity vector onto the nose
    // outside the ground-blow steer above. Its lag is spent once, as the clamped lift demand;
    // a second kinematic chase doubles the swing and hides the clamps. The config key is the
    // A/B seam (1 restores the pre-decode chase). docs/org/flightModel.md, "lift_accel_rate
    // is a lag toward a target velocity".
    private const float NoseChaseFactor = 0f;

    // The collision impulse's linear weight (see BounceNormalSpeed): a hardcoded literal with no
    // data origin, so no key in player.json can move it. It sets where the rebound/spin partition
    // sits and is NOT a TUNE.
    // ⚠ Do not raise it to reach the original's measured flat-ground rebound. That rebound comes
    // from the doubled contact-point term, which is not restitution and is unbounded.
    private const float BounceLeverScale = 2.25f;

    // The decoded contact placement (see Collide): a human-piloted contact rests 0.03 m off the
    // surface along the normal; anything else rests exactly at the sweep's stop. The angular
    // impulse's shared half-weight rides the same decode.
    // ⚠ The original has NO tangential, friction or vertical-speed term on contact; the filmed
    // losses are the placement plus repeated impulses, so do not re-add a speed-loss term here.
    // Decode and addresses: docs/org/flightModel.md, "Collision response".
    private const float ContactPushOut = 0.03f;    // m along the normal, human contacts only
    private const float BounceAngularHalf = 0.5f;  // the angular impulse's shared 0.5

    // Drag is a parabolic polar in MACH: C_D = DragPolarScale · (parasite + linear·M + quad·M²),
    // opposing the velocity in weight units. All four numbers are shared by every aircraft, which
    // differ only through the authored drag_factor and ref_area. docs/org/flightModel.md, "Drag".
    // ⚠ The variable is Mach, not the lift coefficient. This model has no induced-drag term at all,
    // and reading these as a C_L polar gives a drag floor that is speed-independent at fixed load.
    // ⚠ Do not refit them to close the recorded decode-vs-footage conflict; they are the binary's.
    private const float DragPolarScale = 0.73f;
    private const float DragPolarParasite = 0.12f;
    private const float DragPolarLinear = 0.8f;
    private const float DragPolarQuad = 0.5f;

    // Nitro: the boost flag substitutes a flat lever for the throttle (not a multiply, so boosting
    // at idle is boosting at full) and scales the drag coefficient. docs/org/flightModel.md, "Nitro".
    // ⚠ The lever substitution happens at the thrust read; the throttle and its slew are untouched.
    private const float BoostLever = 1.8f;
    private const float BoostDragFactor = 0.8f;

    // Per-axis control-rate calibration: steady rate = torque · recInertia · Tune / ang_momentum_damp
    // (× eff on yaw). All three are 1: the original has no such factor on any axis.
    // docs/org/flightModel.md, "The *Tune rates".
    // ⚠ Kept as named constants, not deleted, so the config keys stay live for an A/B at the
    // controls. Restoring a value needs a mechanism traced in the binary, never a rate timed off
    // footage, that is what all three replaced.
    private const float PitchTune = 1f;
    private const float YawTune = 1f;
    private const float RollTune = 1f;

    // Bank coupling, the original's coordinated-turn cheat and the only part of its rotation that no
    // airframe authors: banking yaws the nose the way the wings point and pulls it up, with a further
    // pull once the wings are past vertical. docs/org/flightModel.md, "Bank coupling".
    // ⚠ The inverted term reuses the YAW constant on the PITCH axis. It is not a third number, and it
    // peaks wings-level inverted where the bank term is exactly zero.
    // ⚠ Do not scale either term by the axis' *Tune; those calibrate STICK authority and are ours.
    private const float BankYawCoupling = 0.205f;
    private const float BankPitchCoupling = 0.165f;

    // The weathervane: the authored return_rate is a RESTORING TORQUE toward the velocity vector,
    // not extra damping, which makes the rotation a spring-damper rather than a first-order lag.
    // docs/org/flightModel.md, "Weathervane centring".
    // ⚠ The angle is halved, matching the original's quaternion-log conversion; reading it as the
    // full misalignment doubles the spring rate.
    // ⚠ It is PLAYER-only, and Step holds that gate, WeathervaneTorque itself is deliberately not.
    private const float WeathervaneHalfAngle = 0.5f;

    // The AI's forward-velocity floor: after integration, and on the AI path only, the velocity's
    // component along the NOSE is raised to at least 10 mph by adding along the nose, leaving the
    // perpendicular components untouched. One-sided; it only ever raises.
    // docs/org/flightModel.md, "The integrator".
    // ⚠ It is not a floor on Speed. A plane dropping at 20 m/s with its nose on the horizon has
    // ample speed and no forward velocity, and a Speed clamp would do nothing there.
    private const float AiNoseSpeedFloor = 4.4704f;

    // The reverse-authority factor's floor: max(yawAuthority, 0.2) above yaw_max, 1.0 at or below.
    // ⚠ It is NOT a force term, see ReverseAuthorityAt for where the trace leads.
    private const float ReverseAuthorityFloor = 0.2f;

    // The far-field plant's boundary: an aircraft further than this from the nearest human pilot,
    // measured horizontally, flies the simplified speed-hold plant instead of the aerodynamics.
    // docs/org/flightModel.md, "The far-field plant".
    // ⚠ The original compares the SQUARE against 1e6 m² and has no hysteresis, so an aircraft
    // sitting on the line alternates branches frame by frame. That is the decoded behaviour.
    private const float FarFieldRangeM = 1000f;

    // The speed a non-player aircraft holds ON TOP of throttle · fd_speed in the far-field plant.
    // Every aircraft CSVM can send far-field is a non-player one, so it is unconditional here.
    private const float FarFieldAiSpeedBonus = 5f;

    // How much of the opposing-command limiter's decoded AOA window is spent. 1 is the decode and
    // the default; 0 is the A/B seam that holds the window off. The window is continuous rather
    // than a threshold, so at 1 it binds on every stock airframe and the sustained pitch-rate row
    // reads 24.01 °/s against a filmed 33.00, a recorded conflict, not a tuning input.
    // docs/org/flightModel.md, "The α a full pull holds".
    // ⚠ Any value between is a blend with no original behind it.
    private const float AoaLimiterFactorDefault = 1f;

    // The delivered lift's component along the BODY UP axis, in G, as this step's force build left
    // it, the signed load factor the original's G limiter reads, which is why it is kept rather
    // than derived from LoadFactorDemand: that one is a length and cannot go negative, while this
    // one goes negative in an outside pull and is the only thing that can reach lowGs.
    private float _bodyUpLoadFactor;

    /// <param name="aiForcePath">Which of the original's two force paths this instance flows, see
    /// <see cref="UsesAiForcePath"/>. ⚠ Optional, and it defaults to the PLAYER path, so a
    /// production construction site added later gets the player plant silently. Two sites pass it
    /// today (<c>HumanFlightAdapter</c>, <c>FlightRoster</c>); a third one must pass it too.
    /// The default exists for the ~18 test sites that construct a plant with no session around
    /// them, not as a statement about what a new caller wants.</param>
    public FlightModel(PlaneStats stats, bool aiForcePath = false)
    {
        Stats = stats;
        UsesAiForcePath = aiForcePath;
        StallSpeed = ComputeStallSpeed(stats);
    }

    public PlaneStats Stats { get; }

    /// <summary>The body rates as physical angular velocity: <see cref="BodyRates"/> carries the
    /// original's quaternion half-angle, so an instrument reading rad/s wants twice it.</summary>
    public Vector3 PhysicalBodyRates => BodyRates * 2f;

    /// <summary>Whether this plant flows the original's AI force path rather than its player one,
    /// chosen once at construction from <c>IsHumanPiloted</c> because the original's own selection
    /// presumes a single player and this engine flies four. The four divergences it drives are all
    /// in <see cref="Step"/> and are listed in this module's docs/architecture.md entry.
    /// ⚠ Named for the PATH, not for the pilot; <see cref="FlightController.IsHumanPiloted"/>
    /// answers who is flying. ⚠ Immutable, a mid-flight switch breaks reproducibility.</summary>
    public bool UsesAiForcePath { get; }

    /// <summary>How much of the decoded AOA window this plant spends, before the config key
    /// <c>flightModel.aoaLimiterFactor</c> (which still wins). 1, the default, is the decode; 0
    /// replaces the window with 1 and is the A/B seam. See <see cref="AoaLimiterFactorDefault"/>.
    /// ⚠ Fixed at construction, like <see cref="UsesAiForcePath"/>: a mid-flight switch would make
    /// a flight unreproducible.</summary>
    public float AoaLimiterFactor { get; init; } = AoaLimiterFactorDefault;

    /// <summary>The delivered lift's body-up component in G, built from the attitude the last step
    /// entered with and read by that same step's limiter: the signed load factor the
    /// <c>highGs</c>/<c>lowGs</c> ramp is measured on, positive in an ordinary pull and negative in
    /// an outside one. Reported for instruments and for <c>ControlLimiterTests</c>.
    /// ⚠ Not interchangeable with <see cref="LoadFactorDemand"/>: this is post-clamp, signed and
    /// projected on one axis, that one is the pre-clamp demand's length.</summary>
    public float BodyUpLoadFactor => _bodyUpLoadFactor;

    /// <summary>The opposing-command limiter's scalar as the last step computed it: the smaller of
    /// the AOA and G terms, applied to whichever of the pitch and yaw commands swings the nose
    /// further off the flight path. 1 leaves both commands alone.
    /// ⚠ The G ramp is authored out of reach on every stock airframe; the AOA window is not, and
    /// it reads about 0.7 through a stock full pull. docs/org/flightModel.md, "Torques and the
    /// limiters".</summary>
    public float CommandLimit { get; private set; } = 1f;

    /// <summary>Which plant the last step flew: true is the original's far-field speed-hold branch,
    /// false its near-field aerodynamics. Only an AI-path plant can reach the far branch, and only
    /// while the nearest human pilot is beyond <c>FarFieldRangeM</c>. Reported so a suite can assert
    /// the SELECTION separately from the behaviour it produces.
    /// ⚠ Re-decided every step from the distance the input carries, unlike
    /// <see cref="UsesAiForcePath"/>: an aircraft crosses the boundary in both directions.</summary>
    public bool FarFieldPlant { get; private set; }

    /// <summary>Airspeed as a fraction of fd_speed, the airframe-independent speed scale the
    /// authored stats are quoted on. ⚠ NOT a stall cue: neither threshold reads it. The lamp gates
    /// on <see cref="AvailableLoadFactor"/> and the nose-drop on <see cref="StallSpeed"/>.</summary>
    public float StallFraction => Stats.FdSpeed > 0f ? Speed / Stats.FdSpeed : 0f;

    /// <summary>The speed (m/s) below which the wings' maximum available lift, the same aerodynamic
    /// ceiling <see cref="Step"/> caps lift with, can no longer equal the aircraft's weight: the
    /// solution of <c>clMax(V)·q(V)·RefArea = VehWeight</c> at a LOAD FACTOR OF EXACTLY 1.
    /// ⚠ Do not rescale it by <c>nom_gravity / StandardG</c> to close the Bloodhawk's computed
    /// 56.5 mph against its filmed ~76 mph nose-drop. The decode's worked example settles the 1 G
    /// read, and the gap stands recorded in docs/org/flightModel.md, "Stall".</summary>
    public float StallSpeed { get; }

    /// <summary>The original's stall flag, <c>1 − L(9°)/Weight</c>: how far the maximum available
    /// lift falls short of carrying the aircraft's weight, and the depth the nose-drop torque scales
    /// with. Positive where the lift cap is under 1, which in the dense band is the same condition as
    /// being under <see cref="StallSpeed"/>; the thin band moves this and not that speed.
    /// ⚠ It is NOT the speed ratio it replaced: this is quadratic in speed, so the drop deepens
    /// faster as speed bleeds. docs/org/flightModel.md, "Stall".</summary>
    public float StallFlag => 1f - AvailableLoadFactor;

    /// <summary>The wing's available lift at the current airspeed as a multiple of weight, the
    /// original's <c>n_avail</c>. The STALL lamp gates on this and its blink rate ramps over it,
    /// so the lamp reads a LOAD-FACTOR margin while the nose-drop reads an airspeed one.</summary>
    public float AvailableLoadFactor => LiftCapAt(Speed);

    /// <summary>Seconds left on the choker's engine-dead timer (<c>TANGLER</c>, the victim's
    /// <c>+0x2e0</c> behind the disabled-systems bit): while it runs the thrust term is zero and
    /// every other force is untouched, so the aircraft bleeds speed on drag rather than snapping to
    /// stall. The lever is left where the pilot put it, as the original leaves it, so the engine
    /// comes back at the throttle setting it died on. Set through <see cref="ChokeEngine"/>.</summary>
    public float EngineDeadRemainingS { get; private set; }

    /// <summary>Whether the engine is dead, the disabled-systems bit's one flight-side reader. No
    /// AI code reads it: a choked pilot is never told, and simply flies an aircraft with no
    /// thrust.</summary>
    public bool EngineDead => EngineDeadRemainingS > 0f;

    // Read per use off the altitude the step ENTERS with, as FUN_0048fc40 passes [obj+0x208]·3.28084
    // into the atmosphere before it composes any force.
    // ⚠ Nothing clamps altitude, here or in the original: what an aircraft reaches above the line is
    // bought with the vertical speed it crossed at, and no constant bounds it.
    private bool InDenseBand => Position.Y * FeetPerMetre <= BandThresholdFt;

    private float AirDensitySlugPerFt3 => InDenseBand ? DenseDensitySlugPerFt3 : ThinDensitySlugPerFt3;

    private float SpeedOfSoundFps => InDenseBand ? DenseSoundFps : ThinSoundFps;

    private float ThrustPowBase => InDenseBand ? DenseThrustPowBase : ThinThrustPowBase;

    /// <summary>How much of the available thrust the nose's attitude leaves: 1 wings-level, 0.6612
    /// pointing straight up, 1.24 pointing straight down. A climb is PENALISED and a dive rewarded.
    /// ⚠ The argument is the world-up component of the BODY Z AXIS and the nose points along −Z, so
    /// pass <c>Attitude.Z.Y</c>, which is negative in a climb. A dropped sign here merely swaps
    /// climb for dive and still looks plausible, which is why <c>AttitudeThrustTests</c> reads the
    /// term back out of the integrator rather than leaving it to review.</summary>
    public static float AttitudeThrustScale(float bodyZUp)
    {
        float a = Mathf.Clamp(bodyZUp, -1f, 1f);
        float scale = 1f + AttitudeThrustBoth * a;
        return a <= 0f ? scale * (1f + AttitudeThrustUp * a) : scale;
    }

    /// <summary>Thrust acceleration along the nose, m/s², at an airspeed and lever position, the
    /// original's thrust-available curve times the lever, LINEARLY. ⚠ The attitude scale is NOT
    /// included: <see cref="AttitudeThrustScale"/> is applied by the caller, so this stays the bare
    /// curve an instrument can sample. Exposed so an instrument can report the curve without
    /// re-deriving it; the step below calls the same method.</summary>
    public float ThrustAccelAt(float speed, float throttle)
    {
        float mach = Mathf.Max(ThrustMachFloor, speed / (SpeedOfSoundFps * MetresPerFoot));
        float vRefFps = (ThrustVRefSlope * mach + ThrustVRefMach) * SpeedOfSoundFps;
        float qRef = 0.5f * AirDensitySlugPerFt3 * vRefFps * vRefFps;
        float cRef = DragPolarScale * (DragPolarParasite - mach * ThrustMachTrim);
        float avail = qRef * cRef / (mach * Mathf.Pow(ThrustPowBase, ThrustPowMach * mach));
        return Stats.VehWeight > 1e-3f
            ? Stats.EnginePower * Stats.RefArea * avail * throttle * StandardG / Stats.VehWeight
            : 0f;
    }

    /// <summary>Rudder authority at an airspeed, the original's authored piecewise speed table: a
    /// low-speed floor, a linear ramp to full authority at <c>yaw_max</c>, then a linear decline to
    /// a high-speed floor. Exposed so an instrument can sample it; Step calls the same method.
    /// ⚠ Deliberately NOT monotone and NOT flat past the knee. The executable's compiled fallbacks
    /// are a different, wrong reading of this curve (docs/org/flightModel.md, "Authored values vs
    /// the executable's fallbacks"). ⚠ YAW ONLY; pitch and roll run their own curve.</summary>
    public float YawAuthorityAt(float speed)
    {
        var s = Stats;
        if (speed <= s.YawFadeIn)
            return s.YawLowSpeed;
        if (speed <= s.YawMax)
            return s.YawMax > s.YawFadeIn
                ? Mathf.Lerp(s.YawLowSpeed, 1f, (speed - s.YawFadeIn) / (s.YawMax - s.YawFadeIn))
                : 1f;
        if (speed <= s.YawFadeOut)
            return s.YawFadeOut > s.YawMax
                ? Mathf.Lerp(1f, s.YawHighSpeed, (speed - s.YawMax) / (s.YawFadeOut - s.YawMax))
                : s.YawHighSpeed;
        return s.YawHighSpeed;
    }

    /// <summary>Roll authority at an airspeed, and the base ramp pitch starts from, taken from
    /// AIRSPEED ALONE: 0 at or below <c>turn_fade_in</c>, linear to 1 at <c>turn_fade_out</c>, held
    /// at 1 above. docs/org/flightModel.md, "The low-speed ramp".
    /// ⚠ Roll never fades with high speed; pitch takes a second stage on top
    /// (<see cref="PitchAuthorityAt"/>) and the rudder runs its own curve, so extending this one to
    /// the rudder double-fades an axis that already has one.</summary>
    public float RollAuthorityAt(float speed)
    {
        var s = Stats;
        if (speed <= s.TurnFadeIn)
            return 0f;
        return speed < s.TurnFadeOut && s.TurnFadeOut > s.TurnFadeIn
            ? (speed - s.TurnFadeIn) / (s.TurnFadeOut - s.TurnFadeIn)
            : 1f;
    }

    /// <summary>Pitch authority at an airspeed: the base ramp above, then faded again by the
    /// authored <c>high_speed_pitch_fade</c> pair, full to its first speed, linear to zero at its
    /// second. docs/org/flightModel.md, "Control authority vs speed".
    /// ⚠ PITCH ONLY, and the two stages MULTIPLY rather than replacing one another.
    /// ⚠ This install authors the pair past any attainable speed, so the second stage returns 1 on
    /// every stock airframe and a stock envelope row that moves means the curve is wrong.</summary>
    public float PitchAuthorityAt(float speed)
    {
        var s = Stats;
        float ramp = RollAuthorityAt(speed);
        if (speed >= s.HighSpeedPitchFadeHi)
            return 0f;
        // The knees cannot cross here: reaching the divide needs lo < speed < hi.
        return speed > s.HighSpeedPitchFadeLo
            ? ramp * ((s.HighSpeedPitchFadeHi - speed) / (s.HighSpeedPitchFadeHi - s.HighSpeedPitchFadeLo))
            : ramp;
    }

    /// <summary>The reverse-authority factor: <c>max(yawAuthority, 0.2)</c> above <c>yaw_max</c> and
    /// 1.0 at or below it, so it engages across the whole of normal flight. Its one consumer here is
    /// <see cref="ControlSurfaceAnimator"/>. docs/org/flightModel.md, "Control authority vs speed".
    /// ⚠ Nothing in the force path may read this. It scales the VISIBLE rudder deflection and
    /// touches no torque; what softens opposing control inside the original's force function is a
    /// different, local quantity, and this method exists to record that misattribution.</summary>
    public float ReverseAuthorityAt(float speed) =>
        speed > Stats.YawMax ? Mathf.Max(YawAuthorityAt(speed), ReverseAuthorityFloor) : 1f;

    /// <summary>Maximum available lift as a multiple of weight, clMax(V)·q(V)·RefArea / Weight: the
    /// load factor ceiling, and the quantity ComputeStallSpeed inverts. Weight is the BARE authored
    /// veh_weight (a load factor of exactly 1), which is what the original compares against. Public
    /// so the pull instrument (<c>Probes.PullToLimit</c>) can print the ceiling beside the delivered G.</summary>
    public float LiftCapAt(float speed)
    {
        var s = Stats;
        if (s.VehWeight <= 1e-3f)
            return 0f;
        float speedFps = speed * FeetPerMetre;
        float dynPressure = 0.5f * AirDensitySlugPerFt3 * speedFps * speedFps;
        float mach = speed / (SpeedOfSoundFps * MetresPerFoot);
        float clMax = Mathf.Max(0f, ClMaxStatic - ClMaxMach * mach);
        return clMax * dynPressure * s.RefArea / s.VehWeight;
    }

    /// <summary>The opposing-command limiter's scalar: the smaller of an alignment window on α and a
    /// ramp on the delivered body-up load factor. Pure in its arguments so an instrument can sample
    /// it at a state the model is not in. docs/org/flightModel.md, "Torques and the limiters".
    /// ⚠ It multiplies ONLY a pitch or yaw command that swings the nose FURTHER off the flight path.
    /// Applied to every command it reads as sluggish controls and is backwards.
    /// ⚠ Zero is its neutral value at a standstill, and the G ramp is not floored at zero.</summary>
    public float OpposingCommandLimitAt(float speed, float cosAlpha, float bodyUpLoadFactor,
        float aoaLimiterFactor)
    {
        if (speed <= 0f)
            return 0f;
        var s = Stats;
        float window = cosAlpha > s.MaxAoaCos ? (cosAlpha - s.MaxAoaCos) / (1f - s.MaxAoaCos) : 0f;
        float limit = window * aoaLimiterFactor + (1f - aoaLimiterFactor);
        // The band between the two authored starts leaves the AOA term standing on its own; the
        // original jumps the comparison there rather than taking a min against 1.
        if (bodyUpLoadFactor > s.HighGStart)
            limit = Mathf.Min(limit, (s.HighGMax - bodyUpLoadFactor) / (s.HighGMax - s.HighGStart));
        else if (bodyUpLoadFactor < s.LowGStart)
            limit = Mathf.Min(limit, (s.LowGMax - bodyUpLoadFactor) / (s.LowGMax - s.LowGStart));
        return limit;
    }

    /// <summary>The weathervane's restoring torque for the current attitude and flight path, in BODY
    /// axes and in the same units as the stick command, <c>return_rate · (α/2)</c> about the axis
    /// that swings the nose onto the velocity vector. Zero when the two are aligned, and its roll
    /// component is zero always. Ungated, so an instrument or a test can read it on either path;
    /// Step calls the same method and applies it on the player path only. Decode and traps:
    /// <see cref="WeathervaneHalfAngle"/>.</summary>
    public Vector3 WeathervaneTorque()
    {
        var nose = -Attitude.Z;
        var axis = nose.Cross(VelocityDir);
        float sin = axis.Length();
        if (sin < 1e-6f)
            return Vector3.Zero;
        // atan2 of the cross/dot pair, so the angle stays exact out to a fully reversed flight path
        // where an Acos of the dot alone loses precision and a small-angle read is simply wrong.
        float angle = Mathf.Atan2(sin, nose.Dot(VelocityDir));
        var world = axis * (Stats.ReturnRate * WeathervaneHalfAngle * angle / sin);
        return Attitude.Transposed() * world;
    }

    public void Reset(Vector3 position, Basis attitude, float speed, float throttle)
    {
        Position = position;
        Attitude = attitude.Orthonormalized();
        BodyRates = Vector3.Zero;
        VelocityDir = -Attitude.Z;
        Speed = speed;
        Throttle = throttle;
        _bodyUpLoadFactor = 0f;
        CommandLimit = 1f;
    }

    /// <summary>Kills the engine for <paramref name="seconds"/>, the choker's effect
    /// (<c>FUN_004b1690</c>). The timer only ever EXTENDS: a shorter choke landing on a running one
    /// changes nothing, which is what makes a stream of hits build rather than reset. Non-positive
    /// seconds are ignored; the victim guards live in <c>FlightController.TryChokeEngine</c>.</summary>
    public void ChokeEngine(float seconds)
    {
        if (seconds > EngineDeadRemainingS)
            EngineDeadRemainingS = seconds;
    }

    /// <summary>Restarts a dead engine outright: the respawn reset, so a fresh airframe never flies
    /// with the last one's choke still running.</summary>
    public void ClearChoke() => EngineDeadRemainingS = 0f;

    /// <summary>Seeds the complete world velocity after a reset. A carrier launch inherits its
    /// host's motion, so its direction must not be reconstructed from the launch attitude.</summary>
    public void SetVelocity(Vector3 velocity)
    {
        Speed = velocity.Length();
        VelocityDir = Speed > 1e-6f ? velocity / Speed : -Attitude.Z;
    }

    public void Step(FlightInput input, float dt)
    {
        Throttle = Mathf.Clamp(input.Throttle, 0f, 1f);
        Boosting = input.Boost;
        var s = Stats;

        // Spent before the forces below read it, so the frame the timer runs out already has thrust.
        if (EngineDeadRemainingS > 0f)
            EngineDeadRemainingS = Mathf.Max(0f, EngineDeadRemainingS - dt);

        // Read through Config so config.json can override them without a recompile, and read
        // unconditionally once per step so every key registers even on a frame that never enters the
        // stall or knife-edge branches, --dump-config's template and the orphan check need that.
        float pitchTune = Config.GetFloat("flightModel.pitchTune", PitchTune);
        float yawTune = Config.GetFloat("flightModel.yawTune", YawTune);
        float rollTune = Config.GetFloat("flightModel.rollTune", RollTune);
        // Read here as well as at its own site (IsStallWarned, which Step never calls) purely so the
        // key registers on a launch that never flies, --dump-config's template and the orphan check.
        _ = Config.GetFloat("flightModel.stallWarnLoadFactor", StallWarnLoadFactor);
        float liftGMin = Config.GetFloat("flightModel.liftGMin", LiftGMin);
        float liftGMax = Config.GetFloat("flightModel.liftGMax", LiftGMax);
        float noseChaseFactor = Config.GetFloat("flightModel.noseChaseFactor", NoseChaseFactor);
        float aoaLimiter = Config.GetFloat("flightModel.aoaLimiterFactor", AoaLimiterFactor);

        // The far-field plant (see FarFieldRangeM): the original's level-of-detail branch, taken by
        // an aircraft nobody is close enough to watch. It keeps the stick torques and the ground
        // blow and drops everything else the near field spends.
        FarFieldPlant = UsesAiForcePath
                        && input.NearestHumanDistSqM > FarFieldRangeM * FarFieldRangeM;

        // --- forces, built from the attitude and velocity direction this step ENTERS with, which is
        // the original's order. ⚠ Moving this back under the rotated attitude puts a one-step lag
        // into the G ramp's read below and moves every airframe's envelope.
        var nose = -Attitude.Z;

        // α off the entering state, so everything keyed on the pull reads one value for the frame.
        // ⚠ An emergent LAG, not modelled incidence; the clamped Acos keeps it defined at α → 0,
        // where the Slerp below has no axis.
        Alpha = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(nose.Dot(VelocityDir), -1f, 1f)));

        var velocity = VelocityDir * Speed;
        Vector3 accel;
        if (FarFieldPlant)
        {
            // The far-field plant: hold throttle · fd_speed (plus the AI's flat bonus) along the
            // nose, spending the gap as acceleration at a rate of 1/s. No lift, no drag, no thrust
            // and no gravity are computed, so the whole aerodynamic build is skipped.
            accel = (nose * ((s.FdSpeed * Throttle) + FarFieldAiSpeedBonus)) - velocity;

            // ⚠ LoadFactorDemand and _bodyUpLoadFactor are deliberately left where the last
            // near-field step put them: the original writes neither here, so an aircraft returning
            // inside the boundary spends one step on the reading it left with.
        }
        else
        {
            // --- lift: a DEMANDED acceleration, not a fraction of gravity. Step 1 fakes the airflow
            // toward the nose across the authored liftAOAs window, blending on cos α because the
            // authored degrees are cosined at load. ⚠ Player-only, the AI wind is always nose-aligned.
            float cosAlpha = Mathf.Clamp(nose.Dot(VelocityDir), -1f, 1f);
            float cosSpan = s.LiftAoaCosLo - s.LiftAoaCosHi;
            float windBlend = UsesAiForcePath
                ? 1f
                : cosSpan > 1e-6f
                    ? Mathf.Clamp((s.LiftAoaCosLo - cosAlpha) / cosSpan, 0f, 1f)
                    : (cosAlpha < s.LiftAoaCosLo ? 1f : 0f);
            var relativeWind = velocity.Lerp(nose * Speed, windBlend);

            // Step 2, the demand: swing the velocity onto that airflow at the authored rate and carry
            // weight on top, projected onto the body X/Y plane because only that part acts through the
            // wings. ⚠ The projection's length is a load factor in G, never an incidence angle.
            var demand = (relativeWind - velocity) * s.LiftAccelRate;
            demand.Y += s.Gravity;
            var noseAxis = Attitude.Z;
            var liftDir = demand - noseAxis * demand.Dot(noseAxis);
            LoadFactorDemand = liftDir.Length() / StandardG;
            float loadFactor = Mathf.Clamp(LoadFactorDemand, liftGMin, liftGMax);

            // Step 3, cap the delivered force at the aerodynamic ceiling, C_L·q·RefArea in weight
            // units, which is the same as capping the load factor at C_L·q·RefArea / Weight. This is
            // where a slow aircraft stops being able to carry itself.
            float speedFps = Speed * FeetPerMetre;
            float dynPressure = 0.5f * AirDensitySlugPerFt3 * speedFps * speedFps;
            float mach = Speed / (SpeedOfSoundFps * MetresPerFoot);
            // q·RefArea, in weight units (lb/ft² × ft²), the scale a coefficient converts to a force,
            // and the divisor the delivered C_L (drag, below) comes back out through.
            float qRefArea = dynPressure * s.RefArea;
            float loadCap = LiftCapAt(Speed);
            loadFactor = Mathf.Clamp(loadFactor, -loadCap, loadCap);
            var liftAccel = liftDir.LengthSquared() > 1e-12f
                ? liftDir.Normalized() * (loadFactor * StandardG)
                : Vector3.Zero;
            // What the opposing-command limiter reads a few lines below, in the same step and before
            // any torque accumulates, which is where the original reads it.
            _bodyUpLoadFactor = liftAccel.Dot(Attitude.Y) / StandardG;

            // Thrust pulls along the nose and drag opposes the motion; the along-path shares fall out of
            // the vector sum. ⚠ Gravity acts at full strength in every attitude and carries no
            // climb-retention scale, the lift demand above already carries weight.
            float cd = DragPolarScale
                       * (DragPolarParasite + DragPolarLinear * mach + DragPolarQuad * mach * mach);
            // Force (weight units) → acceleration is × StandardG / Weight, the same conversion lift uses.
            if (Boosting)
                cd *= BoostDragFactor;
            float dragAccel = s.VehWeight > 1e-3f
                ? qRefArea * s.DragFactor * cd * StandardG / s.VehWeight
                : 0f;
            // A choked engine contributes no thrust and nothing else: no drag term, no lift term and no
            // airspeed clamp are touched (docs/org/ordnanceTypes.md, "The choker, settled").
            float lever = Boosting ? BoostLever : Throttle;
            float thrustAccel = EngineDead ? 0f : ThrustAccelAt(Speed, lever) * AttitudeThrustScale(Attitude.Z.Y);
            accel = nose * thrustAccel
                    - VelocityDir * dragAccel
                    + Vector3.Down * s.Gravity
                    + liftAccel;
        }

        // --- rotation: torque·recInertia against momentum damping, plus the bank coupling and the
        // weathervane into the same accumulator. Each axis carries its own authored authority curve
        // from airspeed alone, unless the far branch forces all three to 1 as the original does.
        float yawEff = FarFieldPlant ? 1f : YawAuthorityAt(Speed);
        float rollEff = FarFieldPlant ? 1f : RollAuthorityAt(Speed);
        float pitchEff = FarFieldPlant ? 1f : PitchAuthorityAt(Speed);

        // The axis that closes the nose onto the flight path, in body coordinates and read off the
        // attitude this step ENTERS with, as the original reads it. A command projecting negatively
        // on it swings the nose further off the path, and only that kind is softened below.
        var separationAxis = nose.Cross(VelocityDir);
        float separationSin = separationAxis.Length();
        var closingAxis = separationSin > 1e-6f
            ? Attitude.Transposed() * (separationAxis / separationSin)
            : Vector3.Zero;
        // 1 on the far branch, which is the original forcing the limiter's own scalar rather than
        // computing it: no AOA window and no G ramp reaches a distant aircraft's commands.
        CommandLimit = FarFieldPlant
            ? 1f
            : OpposingCommandLimitAt(Speed, nose.Dot(VelocityDir), _bodyUpLoadFactor, aoaLimiter);

        // ⚠ The authority scalars multiply the STICK COMMAND only. The bank coupling, the
        // weathervane and the ground blow are summed in below carrying none, which is the original's
        // arrangement: a slow aircraft keeps the full coupling and the full restoring torque.
        var cmd = new Vector3(
            Mathf.Clamp(input.Pitch, -1f, 1f) * s.PitchTorque * s.RecInertia.X * pitchTune * pitchEff,
            Mathf.Clamp(input.Yaw, -1f, 1f) * s.RudderTorque * s.RecInertia.Y * yawTune * yawEff,
            Mathf.Clamp(input.Roll, -1f, 1f) * s.RollTorque * s.RecInertia.Z * rollTune * rollEff);

        // ⚠ Pitch and yaw only, and the stick command only. Roll carries no such test in the
        // original, and neither does anything else summed into the accumulator below.
        cmd.X = SoftenIfOpposing(cmd.X, closingAxis.X, CommandLimit);
        cmd.Y = SoftenIfOpposing(cmd.Y, closingAxis.Y, CommandLimit);

        // Bank coupling (see BankYawCoupling), read off the entering attitude, before anything
        // rotates it, and skipped whole on the far branch: that is why a distant aircraft's bank
        // stops turning its nose while its roll command still rolls it.
        if (!FarFieldPlant)
        {
            float bankComponent = Attitude.X.Dot(Vector3.Up);
            float bodyUpComponent = Attitude.Y.Dot(Vector3.Up);
            cmd.Y += BankYawCoupling * bankComponent * s.RecInertia.Y;
            cmd.X += (BankPitchCoupling * Mathf.Abs(bankComponent)
                      + (bodyUpComponent < 0f ? -BankYawCoupling * bodyUpComponent : 0f))
                     * s.RecInertia.X;
        }

        // Weathervane (see WeathervaneHalfAngle), read off the same entering attitude and velocity
        // direction, the original's ordering again. The AI path skips the whole block, so an AI nose
        // is never pulled back onto its flight path and its rotation stays a first-order lag.
        if (!UsesAiForcePath)
            cmd += WeathervaneTorque() * s.RecInertia;

        // The AI branch writes its fixed response into persistent angular velocity, not this tick's torque.
        // It is per-tick, not dt-scaled; ordinary torque weakens recovery by the simulation rate.
        // The player branch remains a command bias.
        Vector3 groundBlow = GroundBlowTerm(input, cmd, out float groundBlowSteer);

        // The stall nose-drop, the last block of the original's accumulation and player-only.
        // docs/org/flightModel.md, "The nose-drop's rate is stall_mag".
        if (!UsesAiForcePath)
            cmd = StallNoseTorque(cmd);

        // The original's order: accumulate this tick's torque first, then decay the whole result,
        // the fresh torque included. ⚠ Keep the decay EXPONENTIAL. A linear subtraction agrees only
        // to first order and flips BodyRates' sign every tick once dt·damp exceeds 2.
        BodyRates += (cmd + (UsesAiForcePath ? Vector3.Zero : groundBlow)) * dt;
        if (UsesAiForcePath)
            BodyRates += groundBlow;
        BodyRates *= Mathf.Exp(-dt * s.AngMomentumDamp);

        var omegaWorld = Attitude * BodyRates;
        float omega = omegaWorld.Length();
        if (omega > 1e-6f)
            Attitude = Attitude.Rotated(omegaWorld / omega, 2f * omega * dt).Orthonormalized();

        // ⚠ Do not add a knife-edge nose-sag term here. The decoded bank→yaw coupling already does
        // that job and gives the original's own shape, and a second term keyed on wing verticality
        // double-counts it and fights every wings-level pull too. docs/org/flightModel.md.

        // --- translation: the forces built above integrate on the velocity VECTOR, so speed can
        // pass through zero and a vertical zoom tail-slides out downward instead of freezing mid-air
        // at a clamped 0.
        var vel = velocity + accel * dt;

        // The nose AFTER this step's rotation. Everything below reads the flown attitude, which is
        // the side of the integrator these three steps sit on in the original; the force build above
        // is the side that reads the entering one.
        var flownNose = -Attitude.Z;

        // The AI's nose-axis floor (see AiNoseSpeedFloor) sits here because the original applies it
        // here: after the velocity integration and before |v| is recomputed and the position steps.
        if (UsesAiForcePath)
        {
            float alongNose = vel.Dot(flownNose);
            if (alongNose < AiNoseSpeedFloor)
                vel += flownNose * (AiNoseSpeedFloor - alongNose);
        }

        Speed = Mathf.Min(vel.Length(), MaxDiveSpeedFrac * s.FdSpeed);
        if (vel.LengthSquared() > 1e-8f)
            VelocityDir = vel.Normalized();

        // ⚠ Do not add lift_accel_rate here: the demand above already spends it, and the original's
        // only velocity-direction rotation is the ground-blow steer (docs/org/flightModel.md,
        // "lift_accel_rate is a lag toward a target velocity"). NoseChaseFactor is the A/B seam.
        float align = s.LiftAccelRate * noseChaseFactor + groundBlowSteer;
        // ⚠ Keep the near-parallel lerp branch, which is the normal cruise state. Slerp builds its
        // axis from a cross product whose float error swamps a sub-degree angle, and Godot then
        // throws "Argument is not normalized", aborting the physics frame: the plane stops flying.
        float pathDot = flownNose.Dot(VelocityDir);
        if (align > 0f && pathDot > -0.999f)
        {
            float t = 1f - Mathf.Exp(-align * dt);
            VelocityDir = (pathDot > 0.999f
                ? VelocityDir + (flownNose - VelocityDir) * t
                : VelocityDir.Slerp(flownNose, t)).Normalized();
        }

        Position += VelocityDir * Speed * dt;
    }

    /// <summary>Below the airframe's own computed <see cref="StallSpeed"/>, the aerodynamic stall
    /// the flight model flies. NOT the cue the STALL lamp shows: that one lights earlier (a fixed
    /// fraction of fd_speed), see IsStallWarned.</summary>
    public bool isStalled() => StallFlag > 0f;

    /// <summary>Below the STALL lamp's warning threshold: the wing has less than 2.35 g of
    /// available lift left. Leads the nose-drop, which needs the ceiling under 1 g.</summary>
    public bool IsStallWarned() =>
        AvailableLoadFactor < Config.GetFloat("flightModel.stallWarnLoadFactor", StallWarnLoadFactor);

    /// <summary>The decoded collision restitution: the velocity's component along the contact normal
    /// AFTER the original's impulse (docs/org/flightModel.md, "Collision response").
    /// ⚠ Nothing here depends on the struck surface, no verticality test, no material lookup, no
    /// friction, so a flat-versus-vertical split must not be implemented as one.</summary>
    /// <param name="contactArm">NOT normalised: its length sets the rebound/spin partition, and a
    /// LONG arm rebounds harder than a short one.</param>
    public float BounceNormalSpeed(Vector3 velocity, Vector3 normal, Vector3 contactArm) =>
        BounceImpulse(velocity, normal, contactArm).NormalSpeed;

    /// <summary>The decoded angular half of the same impulse: what the contact adds to the body
    /// rates, net of the original's accumulator round-trip (the inertia weighting cancels between
    /// the deposit and the next frame's integration, so the applied kick is the raw
    /// <c>(r × J) / |r|²</c> scaled by the partition's angular share and the shared 0.5).</summary>
    public Vector3 BounceRateKick(Vector3 velocity, Vector3 normal, Vector3 contactArm) =>
        BounceImpulse(velocity, normal, contactArm).RateKick;

    /// <summary>The decoded collision response: the placement at the sweep's stop and, for a human
    /// pilot, the normal-only impulse on velocity and body rates. Nothing else: the original edits
    /// no tangential speed, no friction and no vertical component on contact, so a sustained scrape
    /// bleeds speed only through repeated impulses as the plant steers back into the surface.
    /// ⚠ The impulse is PLAYER-only, as the original is; an AI gets the position correction alone,
    /// resting exactly at the stop with no push-out.</summary>
    public void Collide(Vector3 prev, Vector3 step, float stopFrac, Vector3 impact, Vector3 normal,
        bool humanPiloted)
    {
        Position = prev + step * stopFrac + (humanPiloted ? normal * ContactPushOut : Vector3.Zero);
        if (!humanPiloted)
            return;

        var vel = VelocityDir * Speed;
        var (rebound, rateKick) = BounceImpulse(vel, normal, impact - Position);
        var bounced = vel - normal * vel.Dot(normal) + normal * rebound;
        float bouncedLen = bounced.Length();
        Speed = bouncedLen;
        if (bouncedLen > 1e-4f)
            VelocityDir = bounced / bouncedLen;
        BodyRates += rateKick;
    }

    // The sign test the opposing-command limiter gates on, on the raw sign bits as the original
    // tests them, so a negative zero on either side reads as negative rather than as agreement.
    private static float SoftenIfOpposing(float command, float closingComponent, float limit) =>
        (System.BitConverter.SingleToInt32Bits(command)
         ^ System.BitConverter.SingleToInt32Bits(closingComponent)) < 0
            ? command * limit
            : command;

    // Solves clMax(V)·q(V)·RefArea = VehWeight for V at a load factor of 1 (see StallSpeed's doc for
    // why 1, not nom_gravity/StandardG). clMax's Mach term makes this implicit; a handful of
    // fixed-point passes converge to float precision because stall speeds sit well below the speed
    // of sound, so the correction off the Mach-free start is only a percent or two. Computed once per
    // instance, VehWeight/RefArea never change after construction.
    // ⚠ Pinned to the DENSE band, so it is the airframe's figure for the whole flyable envelope.
    private static float ComputeStallSpeed(PlaneStats stats)
    {
        if (stats.VehWeight <= 1e-3f || stats.RefArea <= 1e-3f)
            return 0f;
        float vFps = Mathf.Sqrt(2f * stats.VehWeight / (ClMaxStatic * DenseDensitySlugPerFt3 * stats.RefArea));
        for (int i = 0; i < 5; i++)
        {
            float mach = vFps / DenseSoundFps;
            float clMax = Mathf.Max(0.05f, ClMaxStatic - ClMaxMach * mach);
            vFps = Mathf.Sqrt(2f * stats.VehWeight / (clMax * DenseDensitySlugPerFt3 * stats.RefArea));
        }
        return vFps * MetresPerFoot;
    }

    // Both halves of the decoded impulse from one computation, since the partition is shared.
    // The angular share's weight is the INERTIA-multiplied (momentum-like) vector: the original
    // divides (r × J)/|r|² by the authored reciprocal moments before measuring it, so a stiff axis
    // weighs heavier, not lighter. Reading that divide as ×I⁻¹ is the error this replaced.
    private (float NormalSpeed, Vector3 RateKick) BounceImpulse(
        Vector3 velocity, Vector3 normal, Vector3 contactArm)
    {
        float vn = normal.Dot(velocity);
        var omegaWorld = Attitude * BodyRates;
        float vpn = vn + 2f * normal.Dot(omegaWorld.Cross(contactArm));
        var j = -vpn * normal;
        float r2 = contactArm.LengthSquared();
        var u = r2 > 1e-12f ? contactArm.Cross(j) / r2 : Vector3.Zero;
        float l = BounceLeverScale * j.Length();
        float a = (Attitude.Transposed() * u / Stats.RecInertia).Length();
        float sum = l + a;
        float fLin = sum > 0f ? l / sum : 0f;   // L == 0 → 0, the original's own degenerate arm
        float fAng = sum > 0f ? a / sum : 0f;
        var kick = Attitude.Transposed() * u * ((1f + fAng * Stats.BounceFactor) * BounceAngularHalf);
        return (vn - vpn * (1f + fLin * Stats.BounceFactor), kick);
    }

    // The stall nose-drop as the original builds it, returning the accumulator with it folded in.
    // ⚠ The CANCELLATION is what makes the drop decisive, not its magnitude, which would lose a
    // straight contest with the elevator. Do not add a floor, a minimum rate or an over-the-horizon
    // cap on top of it. docs/org/flightModel.md, "The nose-drop's rate is stall_mag".
    private Vector3 StallNoseTorque(Vector3 cmd)
    {
        float flag = StallFlag;
        if (flag <= 0f)
            return cmd;
        // Cross order set by THIS engine's rotation convention, not the decode's; the length is the
        // same either way and is what fades the drop as the nose leaves horizontal. Body frame.
        var axis = Attitude.Inverse() * Vector3.Up.Cross(-Attitude.Z);
        float axisSq = axis.LengthSquared();
        if (axisSq <= 1e-8f)
            return cmd;
        // The reciprocal inertia comes out and goes back because the original cancels on the
        // PRE-inertia accumulator; a projection is rotation-invariant, so no frame change is needed.
        var pre = cmd / Stats.RecInertia;
        // Only torque OPPOSING the axis is cancelled, the sign test is the original's own.
        float along = pre.Dot(axis);
        if (along < 0f)
            pre -= axis * (along / axisSq);
        return (pre + axis * (Stats.StallMag * flag)) * Stats.RecInertia;
    }



    // Ground blow: the original's bias of control response away from anything large the nose is
    // closing on (docs/org/flightModel.md, "Ground blow"). The caller owns the probe. Two laws share
    // it, the player's biases the STICK, the AI's is a fixed per-tick push. The caller applies
    // dt only to the player command bias; the AI return goes straight to persistent angular speed.
    // ⚠ The AI factor is ai_groundblow · groundblow_mag, never ai_groundblow alone. The caller gates
    // carrier drops for 1.5 s, then applies their ×0.15 final-second response.
    private Vector3 GroundBlowTerm(in FlightInput input, Vector3 cmd, out float velocitySteerRate)
    {
        velocitySteerRate = 0f;
        var n = input.GroundBlowNormal;
        float elev = Stats.GroundBlowElev;
        if (n.LengthSquared() < 1e-12f || elev <= 0f)
            return Vector3.Zero;
        var b = Attitude.Z;                        // backward body axis, in world coordinates
        float c = b.Dot(n);
        if (c <= 0f)
            return Vector3.Zero;
        float proximity = Mathf.Sqrt(c) * (elev - input.GroundBlowDistM) / elev;
        if (proximity <= 0f)
            return Vector3.Zero;
        var axis = n.Cross(b);
        // ⚠ A dead-on approach must get nothing on either path; the original never saves a head-on.
        if (axis.LengthSquared() < 1e-12f)
            return Vector3.Zero;
        // ⚠ Convert to the body frame here, once: the escape axis is built from a WORLD normal, and
        // skipping this gives a term that is right wings-level and wrong at every other attitude.
        // Used twice on the player path, which is where the second power of proximity comes from.
        var v = Attitude.Transposed() * (axis.Normalized() * proximity);

        if (UsesAiForcePath)
        {
            if (input.AiGroundBlowScale < 0f)
                return Vector3.Zero;
            // AI law (0x0048c317): a fixed push, independent of the AI's own command, never
            // suppressed by command direction, unlike the player law below.
            float scale = input.AiGroundBlowScale == 0f ? 1f : input.AiGroundBlowScale;
            velocitySteerRate = GroundBlowVelocitySteer * proximity * scale;
            return v * (Stats.AiGroundBlow * Stats.GroundBlowMag * scale);
        }

        float p = cmd.Dot(v);
        velocitySteerRate = p < 0f ? 0f : GroundBlowVelocitySteer * proximity;
        // Both branches push along +v, away from the surface: commanding away is amplified by
        // 1 + mag·S², commanding into is cut to 1 − 0.05·mag·S² (halved at contact with the
        // authored 10) and never reversed.
        return v * ((p >= 0f ? p : GroundBlowIntoFactor * -p) * Stats.GroundBlowMag);
    }


}
