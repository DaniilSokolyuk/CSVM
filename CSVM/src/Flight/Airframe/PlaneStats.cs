using System;
using System.Collections.Generic;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>Clamped linear ramp between two (x, y) control points, the shape of
/// every throttle/speed→volume/pitch sound curve in player.json.</summary>
public readonly struct SoundCurve
{
    public readonly float MinX, MinY, MaxX, MaxY;

    public SoundCurve(float minX, float minY, float maxX, float maxY) =>
        (MinX, MinY, MaxX, MaxY) = (minX, minY, maxX, maxY);

    /// <summary>Where <paramref name="x"/> falls between the two control points, clamped to [0, 1].
    /// Split out from <see cref="Eval"/> because the engine slot's own terms are added to THIS
    /// parameter and not to the output (docs/formats/vehicle.md, "The engine slot's pitch and gain
    /// are not throttle alone").</summary>
    public float Frac(float x) =>
        MaxX <= MinX ? 1f : Mathf.Clamp((x - MinX) / (MaxX - MinX), 0f, 1f);

    /// <summary>The output a parameter maps to. ⚠ Deliberately does NOT clamp: a caller that added
    /// to <see cref="Frac"/>'s result is allowed past 1, which is the only way the original's engine
    /// note overshoots its curve's own top.</summary>
    public float Remap(float t) => MinY + (MaxY - MinY) * t;

    public float Eval(float x) => Remap(Frac(x));
}

/// <summary>One entry of a vehicle def's 'destroyable_parts' block:
/// a damageable airframe section, nose / tail / leftwing / rightwing for the
/// player planes, with its hit points, its armor pool, and state-change anims.
/// The pair is (hit points, armor), armor is spent first
/// (docs/formats/vehicle.md "The hp pair: armor + hit points"); a def with only one
/// float carries no armor (`MaxArmor` stays 0), it is not duplicated from MaxHp.
/// 'critical' means the plane is destroyed when this part's HP reaches 0; the tail
/// additionally carries 'engine' (power loss on destruction, flight-handling
/// penalties are not modeled yet, recorded only). InjureAnims maps descending
/// HP fractions to anim names: the *_damage_green/yellow/red cockpit-indicator
/// cycle plus the pdpanelN torn-skin panel flips (DamageVisuals wires the panels).</summary>
public sealed class DestroyablePart
{
    public string Name = "";
    public float MaxHp = 20f;
    public float MaxArmor;
    public bool Critical;
    public bool Engine;
    public string? GotHitAnim;
    public List<(float Frac, string Anim)> InjureAnims = new();
}

/// <summary>One entry of a vehicle def's <c>turrets</c> block: which <c>ai.zrd</c> gunner row
/// (<see cref="Title"/>, a <c>MSG_TUR_*</c> key) drives which turret-rig subtree
/// (<see cref="Node"/>, e.g. <c>kestrel_turret1</c>). The block is keyed by VIEWPOINT,
/// <c>firstp</c> is the cockpit-view rig (player defs only), <c>thirdp</c> the external one,
/// which is what the titles' <c>_G1</c>/<c>_G3</c> suffixes select (docs/formats/turrets.md).</summary>
public sealed class TurretMount
{
    public string Title = "";
    public string Node = "";
    public bool FirstPerson;
}

/// <summary>The damaged-engine re-arm timer's shared state: one instance per loaded airframe
/// DEFINITION, the original's own field, not the instance. <see cref="PlaneStats"/>'s per-spawn
/// clones carry this SAME reference forward rather than copying it. So every aircraft flying one
/// airframe def reads and writes the same counter and the same drawn threshold.
/// Decode: docs/formats/vehicle.md, "What makes an airframe damaged".</summary>
public sealed class DamagedEngineTimer
{
    public float Elapsed;
}

/// <summary>
/// Flight parameters for one player aircraft, pulled from the zrdr extraction:
/// vehicle.json (per-plane 'dynamics' block, resolved through the 'kind_of'
/// inheritance chain, e.g. pbloodhawk → player_airplane → basic_airplane),
/// engines.json (stock engine power factor), player.json (global constants).
/// Units are meters/seconds (fd_speed 135 ≈ 302 mph matches the Bloodhawk's
/// published top speed; flight_ceiling 2500 and nom_gravity 20 agree).
/// </summary>
public sealed class PlaneStats
{
    /// <summary>The per-spawn jitter's half-width (<see cref="WithAiSpawnJitter"/>): the original's
    /// own immediate, an independent uniform ±5 % per slot.</summary>
    public const float AiSpawnJitterSpread = 0.05f;

    public string DefName = "";          // vehicle.json def, e.g. "pbloodhawk"
    public string NodeName = "";         // GameZ node, e.g. "player_bhawk"

    /// <summary>The AI def's own nine-slot pilot skill vector and voice accent, resolved down the AI
    /// chain; every slot null on a player load. A roster block's own vector outranks these, which is
    /// the fallback the engine takes when a slot there is unset (<see cref="AiSkillVector"/>).</summary>
    public AiSkillVector AiPilotSkills;

    /// <summary>The AI def's <c>accentID</c>, a <c>voice.zrd</c> row; null when it authors none.</summary>
    public int? AiAccentId;

    /// <summary>The AI def's own armament, nearest <c>weapons</c> block in the AI chain, empty on a
    /// player load. Deliberately not read down the player chain: <c>player_airplane</c> authors a
    /// <c>weapons</c> block too, but that one is the 39-id buyable catalogue rather than a fit, and
    /// the player's own fit comes from <c>stock_loadouts.json</c>.</summary>
    public List<AiWeaponSlot> AiWeapons = new();

    /// <summary>The AI def the damage trio came from ("bloodhawk"), or null on a player load
    /// (<see cref="Load"/>). Set only by <see cref="LoadForAi"/>, and deliberately NOT used as
    /// <see cref="DefName"/>: that name keys the stock-loadout table, which holds the eleven
    /// player defs alone, so swapping it would leave every AI plane unarmed.</summary>
    public string? AiDefName;

    /// <summary>The def chain's own <c>mode</c> key ("jet", "wingman", "ship"), nearest wins: the AI
    /// chain on an AI load, the player chain otherwise. It is the vehicle class the original stores
    /// at <c>obj+0x67c</c>, and the only consumer here is <see cref="WithAiSpawnJitter"/>'s gate.
    /// Null where no def in the chain authors one.</summary>
    public string? VehicleMode;

    /// <summary>The AI def's <c>title</c> message KEY ("MSG_VEH_MEDUSA_KESTREL"), nearest in the AI
    /// chain, null on a player load. Resolve it through the string table for the name the targeting
    /// readout prints; unresolved it is a key, not a name.</summary>
    public string? AiTitleKey;

    /// <summary>That title resolved ("Medusa Kestrel"), or null where nobody has resolved it, a
    /// suite rig with no string table, or a player load. <c>PlaneRoster.PlaneDisplayName</c> prefers
    /// it over the def-name derivation, which is how a marker reads the militia's name.</summary>
    public string? AiTitle;

    // dynamics block
    public float PitchTorque = 2.4f;
    public float RollTorque = 6f;
    public float RudderTorque = 1.4f;
    public float ReturnRate = 3f;        // extra rate damping while the stick is centered
    public float AngMomentumDamp = 5f;
    public Vector3 RecInertia = new(0.8f, 0.6f, 1.3f); // reciprocal moments: x=pitch, y=yaw, z=roll
    public float FdSpeed = 113f;         // reference/max level-flight speed, m/s
    public float DragFactor = 1f;
    public float VehWeight = 3500f;
    public float RefArea = 335f;

    public float EnginePower = 1f;       // engines.json factor for the plane's stock engine
    public float FlightCeiling = 2500f;

    // vehicle.json def-level 'fuel', a full tank in burn units (FuelTank.BurnRate per second at a
    // fully open lever). One def authors it, player_airplane at 54926, and every player_* airframe
    // inherits that; no AI def does, and the compiled default is 0 (docs/formats/vehicle.md).
    public float FuelCapacity;

    // The AI mode machine's range gates: vehicle.json 'attack' / 'return_range', both
    // authored once on basic_airplane and inherited install-wide (2000 / 1200).
    public float AiAttackRange = 2000f;
    public float AiReturnRange = 1200f;

    // The pursuit dwell pair, vehicle.json 'attack_dwell' / 'not_pursuit_dwell' (def +0x5c/+0x60),
    // seconds: basic_airplane authors 60 / 5 and every aeroplane inherits them. The fallbacks are
    // the def record's compiled defaults (FUN_00478a00, 0x00478a93 and 0x00478a9a).
    public float AiAttackDwell = 60f;
    public float AiNotPursuitDwell = 3f;

    // The AI control law's per-axis output stage (docs/org/aiControlLaw.md): roll, pitch and yaw
    // commands are multiplied by these scales, then clamped to these limits. Fallbacks are the
    // def initialiser's own compiled defaults, which is what every airframe in this install flies on.
    // ⚠ A scale of 3.5 against a limit of 1.0 saturates near a 0.29 aim error, so this stage is
    // near-bang-bang, not proportional; that is decoded behaviour, not a bug.
    // ⚠ Do not mirror ai_emerg_input_*: its compiled defaults equal these and no roster authors it.
    public float AiInputScaleRoll = 3.5f;
    public float AiInputScalePitch = 3.5f;
    public float AiInputScaleYaw = 3.5f;
    public float AiInputLimitRoll = 1f;
    public float AiInputLimitPitch = 1f;
    public float AiInputLimitYaw = 1f;

    // The AI acquisition's two class-dependent rank terms, in raw rank units (the same units as
    // metres of distance, never scaled by the weight scale). `target_bias` is spent when this
    // vehicle is the CANDIDATE and only on the vehicle arm; `struct_bias` when it is the SCORER,
    // added to every turret and structure candidate. ⚠ Both ship NEGATIVE against a MINIMISED
    // rank, so both ATTRACT; do not flip a sign to make structures unattractive. An unauthored def
    // spends 0, which is what the vehicle constructor leaves (docs/formats/vehicle.md).
    public float AiTargetBias;
    public float AiStructBias;

    // rudder_tol: how much horizontal aim error justifies banking rather than ruddering
    // (docs/org/aiControlLaw.md). ⚠ A HIGHER value means MORE rudder, not less, clearing the
    // threshold is what selects the bank branch. At the 0.2 default any target ahead is banked
    // toward and the rudder is reserved for targets nearly dead astern; `autogyro` and `balmoral`
    // author 1.0, the ceiling the compared quantity can never exceed, which puts a lateral-dominant
    // error on the rudder even dead ahead. Only `balmoral` reaches this law (the autogyro is class 1).
    public float RudderTol = 0.2f;

    // is_autogyro: the authored bare flag, the vehicle record's byte +0x21c. It reaches no torque,
    // authority curve or stall (docs/org/flightModel.md). Two arms read it: the AI maneuver chooser,
    // and the mouse-flying stick, where it exchanges the roll and yaw sources so an autogyro yaws
    // with sideways mouse motion where an aeroplane banks.
    public bool IsAutogyro;

    // player.json globals
    public float Gravity = PhysicsConstants.NomGravity; // nom_gravity, the game's arcade gravity, m/s²
    public float StallMag = 1.25f;

    // player.json flight globals, plumbed here instead of hardcoded (docs/org/flightModel.md).
    // Converted exactly as the original does: speeds × 0.44704, angles cosined where the original
    // cosines them, raw G otherwise. Fallbacks are the executable's compiled defaults; this
    // install's authored values differ, see the corrections table there.
    // ⚠ Every one of these now reaches the plant, so do not read an authored value as proof that a
    // term is inert; the reachability measurements are in that dossier's limiter sections.
    public float LiftAccelRate = 1.2f;      // lift_accel_rate, 1/s, NOT converted (a rate, not a speed)
    public float LiftAoaCosLo = 0.98f;      // cos(liftAOAs[0]), liftAOAs is degrees, cosined at load
    public float LiftAoaCosHi = 0.96f;      // cos(liftAOAs[1])
    public float MaxAoaCos = 0.85f;         // cos(maxAOA), maxAOA is degrees, cosined at load
    public float HighGStart = 5f;           // highGs[0], plain G, NOT converted
    public float HighGMax = 9f;             // highGs[1], plain G
    public float LowGStart = -5f;           // lowGs[0], plain G
    public float LowGMax = -9f;             // lowGs[1], plain G
    public float TurnFadeIn = 10f * PhysicsConstants.MphToMs;   // turn_fade_in, m/s
    public float TurnFadeOut = 40f * PhysicsConstants.MphToMs;  // turn_fade_out, m/s
    public float YawLowSpeed = 0.05f;       // yaw_low_speed, dimensionless authority, NOT converted
    public float YawHighSpeed = 0.1f;       // yaw_high_speed, dimensionless authority
    public float YawFadeIn = 10f * PhysicsConstants.MphToMs;    // yaw_fade_in, m/s
    public float YawMax = 22.5f * PhysicsConstants.MphToMs;     // yaw_max, m/s
    public float YawFadeOut = 45f * PhysicsConstants.MphToMs;   // yaw_fade_out, m/s
    public float HighSpeedPitchFadeLo = 500f * PhysicsConstants.MphToMs;  // high_speed_pitch_fade[0], m/s
    public float HighSpeedPitchFadeHi = 600f * PhysicsConstants.MphToMs; // high_speed_pitch_fade[1], m/s
    // drag_fade_speed's own compiled fallback is undocumented in docs/org/flightModel.md (the decode
    // covers control authority's turn_*/yaw_* fades but not this key's mechanism); 40 mph mirrors the
    // unchanged turn_fade_out/yaw_fade_in pattern, not a read fallback, flag if this proves wrong.
    public float DragFadeSpeed = 40f * PhysicsConstants.MphToMs; // drag_fade_speed, m/s

    // Ground blow (docs/org/flightModel.md, "Ground blow"): the nose-forward probe that biases the
    // player's control response away from what it hits. Both are RAW SCALARS, groundblow_elev is a
    // length in METRES and needs no conversion, and it is the ray's length AND the falloff's
    // denominator, so it is not a trigger range. Fallbacks are the executable's compiled defaults;
    // this install authors 400 and 10.
    public float GroundBlowElev = 100f;     // groundblow_elev, m, ray length and falloff denominator
    public float GroundBlowMag = 1.5f;      // groundblow_mag, dimensionless
    // ⚠ C23: the AI path is a DIFFERENT law from the player term, not that term scaled by this,
    // a fixed push, linear in proximity, not dt-scaled (docs/org/flightModel.md "Ground blow").
    // FlightModel.GroundBlowTerm reads AiGroundBlow · GroundBlowMag as that factor (5.0 authored).
    // Carrier drops suppress it for 1.5 s, then cut it ×0.15 for 1 s; stunned AI skips it too.
    public float AiGroundBlow = 0.9f;       // ai_groundblow, dimensionless

    // The collision restitution ceiling (player.json's `crash` block, docs/org/flightModel.md's
    // "Collision response and bounce_factor"): a RAW SCALAR, and the ceiling on effective normal
    // restitution rather than the restitution itself, what a contact actually rebounds at is
    // f_lin · this, with f_lin the lever arm's rebound/spin partition (FlightModel's
    // BounceNormalSpeed). The fallback is the executable's compiled default, pre-set before the
    // block is looked up, so an absent `crash` block leaves it standing; this install authors 0.6.
    public float BounceFactor = 0.8f;       // bounce_factor, dimensionless

    // The collision damage pair's authored ranges (player.json's `crash` block, decode in
    // docs/org/flightModel.md's "Collision damage"): element 0 is the FLOOR and element 1 the
    // SCALE of max(scale · s³, floor), with s the impact cosine. Fallbacks are the executable's
    // compiled defaults, which this install's authored [50, 300] always replaces.
    public float CollideArmorFloor = 15f;
    public float CollideArmorScale = 200f;
    public float CollideHealthFloor = 15f;
    public float CollideHealthScale = 200f;

    // The incoming-fire shield's shipped accumulator (warning_shot_*, WarningShotCue). The sound is
    // a SOUND_GROUPS name (bullet_warning_sg → snd_bulletpass1-3), not a sounds.json def, so it
    // resolves through the group table like every other one. Fallbacks are the executable's own
    // compiled defaults (0x00474661, 0x006076b8, 0x004746d7), which this install's data replaces
    // with the identical figures.
    public float WarningShotMax = 2f;           // s of sustained fire before rounds tell
    public float WarningShotDissipation = 0.5f; // intensity shed per second of quiet
    public float WarningShotInterval = 1f;      // s the hit count accrues over
    public string WarningShotSound = "bullet_warning_sg";

    // The ricochet a gun round striking your own airframe rings (bullet_hit_sound → bullet_hit_sg →
    // snd_ricochet1-4). A SOUND_GROUPS name like the one above, and the fallback is the shipped
    // value, since the original leaves the slot null with the key absent and then plays nothing.
    public string BulletHitSound = "bullet_hit_sg";

    // The gun aim assist (sticky_bullet_*, docs/org/aim-assist.md), CatchupRate/ForgetInterval
    // feed B2's per-frame slot update, DistFactor B4's candidate scoring, Inaccuracy B5's launch
    // scatter. Fallbacks are the executable's own compiled defaults, not the shipped player.json
    // values, DistFactor's shipped 0.0 deletes the scan's distance term outright, where the
    // compiled fallback below does not.
    public float StickyBulletCatchupRate = 1f;      // sticky_bullet_catchup_rate, 1/s
    public float StickyBulletForgetInterval = 0.5f; // sticky_bullet_forget_interval, s
    public float StickyBulletDistFactor = 2.5e-4f;  // sticky_bullet_dist_factor, per metre
    // Held in RADIANS, as the original stores it: the parser multiplies the file's degrees by
    // pi/180 on the way in. The shipped 1.0 is a 1-degree cone.
    public float StickyBulletInaccuracy = Mathf.Pi / 180f;

    // The autohead lead (docs/formats/vehicle/player-globals.md): the idle-frame head aim along
    // the plane's own turn that HeadLook.IdleAim drives in Cockpit only. All three
    // fallbacks are the executable's own compiled defaults, not this install's authored values,
    // ⚠ turn_max's asymmetry is the trap: the AUTHORED path converts degrees to radians and then
    // DOUBLES the result (the loader's own arithmetic), where the compiled DEFAULT is already the
    // doubled radian value stored directly, with no further doubling applied to it.
    public float AutoheadTurnTime = 0.75f;          // autohead_turn_time, s, no conversion
    public float AutoheadTurnMax = 0.1f;            // autohead_turn_max fallback, RADIANS already
    public float AutoheadTurnMinPitch = -0.05235988f; // autohead_turn_min_pitch fallback, RADIANS already

    // sound: the three engine-slot def names are vehicle.json keys, the curves are player.json
    // blocks every airframe indexes. Engine curves run on throttle [0..1]; the whine ('prop_sound')
    // curves and the rattle's gate run on speed/fd_speed. Slot assignment: docs/formats/vehicle.md.
    public string EngineSound = "snd_devastatorengine"; // basic_airplane default
    public SoundCurve EngineVolume = new(0.1f, 1f, 1f, 1f);
    public SoundCurve EnginePitch = new(0.1f, 0.6f, 1f, 1f);

    /// <summary>vehicle.json <c>cockpit_engine_sound</c>, the engine def the original swaps onto
    /// the engine slot while the pilot's SELECTED view is the full Cockpit (mode 6), not the Nose
    /// view, confirmed at the controls of the original, and back on leaving it. Selected by
    /// <see cref="Audio.EngineAudioCurves.EngineDefFor"/> and driven by <c>FlightAudio</c>
    /// (<c>BL-161</c>, closed by D31); a held numpad key or look-behind is a per-frame pose and does
    /// not retrigger the swap, only a change of selection does.</summary>
    public string? CockpitEngineSound;

    /// <summary>vehicle.json <c>prop_sound</c>, the overspeed dive whine's def. ⚠ Stays null
    /// install-wide: no shipped vehicle def authors the key and the slot has no compiled default,
    /// so the original plays no whine at all. The player.json <c>prop_sound</c> CURVE block below is
    /// a different key of the same name and IS authored.</summary>
    public string? WhineSound;
    public SoundCurve WhineVolume = new(1f, 0f, 1.1f, 0.5f);
    public SoundCurve WhinePitch = new(1f, 0.65f, 1.2f, 1.25f);
    public string RattleSound = "snd_planeshake";

    /// <summary>player.json <c>rattle.speed_range[0]</c>, the speed as a fraction of fd_speed the
    /// rattle loop starts at, and the whole of the original's law for it. ⚠ Do not add the block's
    /// <c>volume_range</c> back as a ramp: the original parses that pair and its second speed into
    /// globals no instruction reads, so the loop is a hard on/off at full gain.
    /// Decode: docs/org/shakes.md.</summary>
    public float RattleSpeedGate = 1f;

    /// <summary>vehicle.json <c>damaged_engine_sound</c>, the looped def swapped ONTO the engine
    /// slot while the airframe is damaged, not a second loop blended over it. One entry install-wide
    /// (<c>snd_damagedengine</c>), inherited from basic_airplane by every plane.</summary>
    public string? DamagedEngineSound;

    /// <summary>The pitch multiplier drawn once per swap, uniform over this range, applied to the
    /// engine pitch curve. Read only when <see cref="DamagedEnginePitchRandom"/> is set, which the
    /// entry's own third element decides; the shipped entry authors it with the range 0.0 to 1.0, so
    /// a damaged engine can drop to the bottom of the mixer's frequency floor.</summary>
    public float DamagedEnginePitchLo;

    public float DamagedEnginePitchHi = 1f;

    public bool DamagedEnginePitchRandom;

    /// <summary>The shared re-arm timer this airframe definition's damaged loop waits out
    /// (<see cref="DamagedEngineTimer"/>). Set once here so every clone below carries the SAME
    /// instance forward; do not reassign it in a <c>With*</c> method.</summary>
    public DamagedEngineTimer DamagedTimer = new();

    /// <summary>The plane's damageable sections ('destroyable_parts', nearest def in
    /// the kind_of chain). Empty when the def has none (damage model disabled).</summary>
    public List<DestroyablePart> DestroyableParts = new();

    /// <summary>The def-authored whole-vehicle pair ('armor'/'health', nearest def in the
    /// damage chain), the AI base defs carry one (docs/formats/vehicle.md, fighters
    /// 64/64…100/100) and an AI load resolves it alongside an empty
    /// <see cref="DestroyableParts"/>, so the pair IS the whole model. No player def resolves
    /// either, so both stay null on a player load and <see cref="PlaneDamage"/> seeds the whole
    /// pair as the sum over parts instead.</summary>
    public float? VehicleArmor;

    public float? VehicleHealth;

    /// <summary>The def-level 'injure_anims': descending HP-fraction thresholds → whole-plane
    /// effect anims (docs/formats/vehicle.md). An AI load resolves the AI def's own ladder
    /// instead of the player's two-entry one below.
    /// ⚠ Read as "any part's fraction crosses the threshold", the exact original trigger is
    /// undecoded.</summary>
    public List<(float Frac, string Anim)> VehicleInjureAnims = new();

    /// <summary>The def's <c>collision</c> probes, plane-frame metres, nearest def in the damage
    /// chain (docs/formats/vehicle.md "Collision probes"): the six-point airframe on a player
    /// def, and basic_airplane's single origin probe on every AI def, since no AI def authors
    /// its own. The original's contact test carries exactly these points, so an AI aircraft's
    /// wings never touch anything.</summary>
    public List<Vector3> CollisionProbes = new();

    /// <summary>The def's <c>turrets</c> block, the host→gunner link the carried half of
    /// <c>ai.zrd</c> is looked up through (empty on the six non-turret airframes). Both viewpoint
    /// rigs are parsed; carried AI gunner construction consumes only the <c>thirdp</c> entries.</summary>
    public List<TurretMount> TurretMounts = new();

    /// <summary>The player airframe as flown by a person: every property, including the damage
    /// model, resolves down the <c>player_airplane</c> chain.</summary>
    public static PlaneStats Load(string zrdrPath, string planeNodeName) =>
        LoadCore(zrdrPath, planeNodeName, forAi: false);

    /// <summary>The same airframe as flown by the AI: the damage model and the armament resolve
    /// down the AI def's own chain (an <c>armor</c>/<c>health</c> pair, no
    /// <c>destroyable_parts</c>, the <c>weapons</c> tuples), everything else down the player chain,
    /// where <see cref="DefName"/>, <see cref="TurretMounts"/> and the built model must stay.
    /// <paramref name="aiDefName"/> names a militia variant and must derive from the base def.
    /// ⚠ The <c>r*</c> family carries parts but is the remote-player family; no roster spawns one.</summary>
    public static PlaneStats LoadForAi(string zrdrPath, string planeNodeName, string? aiDefName = null) =>
        LoadCore(zrdrPath, planeNodeName, forAi: true, aiDefName);

    /// <summary>The original's per-spawn dynamics jitter (docs/org/flightModel.md "The per-spawn
    /// jitter"), applied only to the two vehicle classes it reaches. Always returns a COPY: the
    /// caller's object is the shared per-airframe cache and callers mutate what they get back.
    /// ⚠ Only the whole-vehicle pair scales, never per-part pools, <see cref="LoadForAi"/>'s
    /// zone-less pair takes the full effect; a player airframe's resolved sum is written out
    /// explicitly so <see cref="PlaneDamage"/> sees the scaled hull.</summary>
    public PlaneStats WithAiSpawnJitter(Random rng)
    {
        // Shallow: DestroyableParts / TurretMounts / VehicleInjureAnims / AiWeapons are read-only after Load and
        // nothing below touches them, so the copy shares them with the cached original on purpose.
        var jittered = (PlaneStats)MemberwiseClone();
        // ⚠ Do not widen this to the wingman class. A downward draw leaves an escort slower than the
        // leader it shares an airframe with, and the station is unreachable for the rest of the run.
        bool takes = VehicleMode == null
            || VehicleMode.Equals(Mech3.VehicleDefs.JetMode, StringComparison.OrdinalIgnoreCase)
            || VehicleMode.Equals("heli", StringComparison.OrdinalIgnoreCase);
        jittered.VehicleHealth = (VehicleHealth ?? SumParts(static p => p.MaxHp)) * (takes ? Factor(rng) : 1f);
        jittered.VehicleArmor = (VehicleArmor ?? SumParts(static p => p.MaxArmor)) * (takes ? Factor(rng) : 1f);
        if (!takes)
            return jittered;
        jittered.FdSpeed *= Factor(rng);
        jittered.EnginePower *= Factor(rng);
        jittered.DragFactor *= Factor(rng);
        jittered.PitchTorque *= Factor(rng);
        jittered.RollTorque *= Factor(rng);
        return jittered;
    }

    /// <summary>The roster block's own durability override (docs/org/vehicleDamage.md "Where the
    /// numbers come from at spawn", step 2), applied before the difficulty scale and the jitter.
    /// Each argument replaces the whole-vehicle pool outright when given; the caller has already
    /// applied the two gates, so null always means "no override", never a zero to invent. Neither
    /// argument resolves a parts-only pair's sum; a null pool stays null for the later steps.</summary>
    public PlaneStats WithRosterDurability(float? initHealth, float? armor)
    {
        if (initHealth is null && armor is null)
        {
            return this;
        }
        var overridden = (PlaneStats)MemberwiseClone();
        if (initHealth is { } health)
        {
            overridden.VehicleHealth = health;
        }
        if (armor is { } a)
        {
            overridden.VehicleArmor = a;
        }
        return overridden;
    }

    /// <summary>The difficulty scale on an enemy vehicle's whole-vehicle pair
    /// (docs/org/vehicleDamage.md "The difficulty scale"), which the engine applies at spawn BEFORE
    /// the per-spawn jitter. Resolves a parts-only airframe's pair to its sum on the way, the same
    /// derivation <see cref="WithAiSpawnJitter"/> makes, so the jitter after it sees the scaled hull.
    /// ⚠ The player's own aircraft is never scaled, and neither is a vehicle on the player's team;
    /// the caller owns that test. An unscaled tier returns THIS, uncopied.</summary>
    public PlaneStats WithEnemyDurability(float factor)
    {
        if (Mathf.IsEqualApprox(factor, 1f))
        {
            return this;
        }
        var scaled = (PlaneStats)MemberwiseClone();
        scaled.VehicleHealth = (VehicleHealth ?? SumParts(static p => p.MaxHp)) * factor;
        scaled.VehicleArmor = (VehicleArmor ?? SumParts(static p => p.MaxArmor)) * factor;
        return scaled;
    }

    /// <summary>A shallow copy carrying a different engine power, for the hangar's engine pick
    /// (the original's registry override onto <c>veh+0x66c</c>). The caller's object
    /// is the shared per-airframe cache and is never mutated; the copy shares the read-only
    /// lists the same way <see cref="WithAiSpawnJitter"/>'s does.</summary>
    public PlaneStats WithEnginePower(float power)
    {
        var copy = (PlaneStats)MemberwiseClone();
        copy.EnginePower = power;
        return copy;
    }

    private static PlaneStats LoadCore(string zrdrPath, string planeNodeName, bool forAi,
        string? aiDefName = null)
    {
        var vehicleRoot = Zrdr.LoadFile(zrdrPath, "vehicle.json")[0] as List<object?>
            ?? throw new InvalidOperationException("vehicle.json: unexpected root shape");

        // defs alternate: name, property-list, name, property-list, ...
        var defs = new Dictionary<string, ZrdrDict>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (int i = 0; i + 1 < vehicleRoot.Count; i += 2)
            if (vehicleRoot[i] is string name && vehicleRoot[i + 1] is List<object?> props)
            {
                defs[name] = ZrdrDict.FromAlternating(props);
                order.Add(name);
            }

        List<ZrdrDict> Chain(string defName)
        {
            var chain = new List<ZrdrDict>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var cur = defName; cur != null && seen.Add(cur) && defs.TryGetValue(cur, out var d);)
            {
                chain.Add(d);
                cur = d.Str("kind_of")!;
            }
            return chain; // derived first, base last
        }

        bool DerivesFrom(string defName, string baseName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var cur = defName; cur != null && seen.Add(cur) && defs.TryGetValue(cur, out var d);)
            {
                if (string.Equals(cur, baseName, StringComparison.OrdinalIgnoreCase))
                    return true;
                cur = d.Str("kind_of")!;
            }
            return false;
        }

        // Requiring player_airplane in the chain is what makes the nodename match unique, the
        // AI and wingman variants share it. Runs for both flavours.
        string? found = null;
        List<ZrdrDict>? chain = null;
        foreach (var name in order)
        {
            var c = Chain(name);
            bool isPlayer = false;
            foreach (var d in c)
                if (string.Equals(d.Str("kind_of"), "player_airplane", StringComparison.OrdinalIgnoreCase))
                    isPlayer = true;
            if (!isPlayer)
                continue;
            foreach (var d in c)
                if (d.Has("nodename"))
                {
                    if (string.Equals(d.Str("nodename"), planeNodeName, StringComparison.OrdinalIgnoreCase))
                        found = name;
                    break; // nearest nodename in the chain decides
                }
            if (found != null) { chain = c; break; }
        }
        if (found == null || chain == null)
            throw new ArgumentException($"no player vehicle def with nodename '{planeNodeName}' in vehicle.json");

        // pfury -> fury, or the named militia variant (secfury, bhatwarhawk) that derives from it.
        // Neither can be found by nodename, since bare AI defs author none of their own. Fails loud
        // rather than silently falling back to the player chain.
        string? aiName = null;
        List<ZrdrDict>? aiChain = null;
        if (forAi)
        {
            string? baseName = found.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? found[1..] : null;
            aiName = aiDefName ?? baseName;
            if (aiName == null || !defs.ContainsKey(aiName))
                throw new ArgumentException(
                    $"no AI vehicle def for player def '{found}' (looked for '{aiName ?? found}') in vehicle.json");
            aiChain = Chain(aiName);
            if (baseName != null && !DerivesFrom(aiName, baseName))
                throw new ArgumentException(
                    $"AI def '{aiName}' does not derive from '{baseName}': it is not a variant of the " +
                    $"airframe '{planeNodeName}' was built from");
        }

        // Where the damage model comes from: the AI chain on an AI load, the player chain otherwise.
        var damageChain = aiChain ?? chain;

        // property lookup: nearest def in the chain wins
        float Prop(string key, float fallback)
        {
            foreach (var d in chain)
                if (d.TryFloat(key, out var f))
                    return f;
            return fallback;
        }
        float Dyn(string key, float fallback, int index = 0)
        {
            foreach (var d in chain)
                if (d.Dict("dynamics") is { } dyn && dyn.TryFloat(key, out var f, index))
                    return f;
            return fallback;
        }
        bool Flag(string key)
        {
            foreach (var d in chain)
                if (d.Has(key))
                    return true;
            return false;
        }
        string PropStr(string key, string fallback)
        {
            foreach (var d in chain)
                if (d.Str(key) is { } s)
                    return s;
            return fallback;
        }
        string? PropStrOpt(string key)
        {
            foreach (var d in chain)
                if (d.Str(key) is { } s)
                    return s;
            return null;
        }

        string? ChainMode()
        {
            foreach (var d in damageChain)
                if (d.Str("mode") is { Length: > 0 } m)
                    return m;
            return null;
        }

        var stats = new PlaneStats
        {
            DefName = found,
            NodeName = planeNodeName,
            AiDefName = aiName,
            VehicleMode = ChainMode(),
            PitchTorque = Dyn("pitch_torque", 2.4f),
            RollTorque = Dyn("roll_torque", 6f),
            RudderTorque = Dyn("rudder_torque", 1.4f),
            ReturnRate = Dyn("return_rate", 3f),
            AngMomentumDamp = Dyn("ang_momentum_damp", 5f),
            RecInertia = new Vector3(
                Dyn("rec_moments_inertia", 0.8f, 0),
                Dyn("rec_moments_inertia", 0.6f, 1),
                Dyn("rec_moments_inertia", 1.3f, 2)),
            FdSpeed = Dyn("fd_speed", 113f),
            DragFactor = Dyn("drag_factor", 1f),
            VehWeight = Dyn("veh_weight", 3500f),
            RefArea = Dyn("ref_area", 335f),
            FlightCeiling = Prop("flight_ceiling", 2500f),
            AiAttackRange = Prop("attack", 2000f),
            AiReturnRange = Prop("return_range", 1200f),
            AiAttackDwell = Prop("attack_dwell", 60f),
            AiNotPursuitDwell = Prop("not_pursuit_dwell", 3f),
            // Fallbacks are the def initialiser's compiled defaults, not guesses, see the fields.
            AiInputScaleRoll = Prop("ai_input_scale_roll", 3.5f),
            AiInputScalePitch = Prop("ai_input_scale_pitch", 3.5f),
            AiInputScaleYaw = Prop("ai_input_scale_yaw", 3.5f),
            AiInputLimitRoll = Prop("ai_input_limit_roll", 1f),
            AiInputLimitPitch = Prop("ai_input_limit_pitch", 1f),
            AiInputLimitYaw = Prop("ai_input_limit_yaw", 1f),
            RudderTol = Prop("rudder_tol", 0.2f),
            IsAutogyro = Flag("is_autogyro"),
            FuelCapacity = Prop("fuel", 0f),
        };
        stats.EngineSound = PropStr("engine_sound", stats.EngineSound);
        stats.CockpitEngineSound = PropStrOpt("cockpit_engine_sound");
        // Absent from every shipped def, which is the finding, not a parse gap, see WhineSound.
        stats.WhineSound = PropStrOpt("prop_sound");

        // The whole-vehicle pair, only when the chain actually authors it (AI defs do; player
        // chains carry neither key, and Prop's fallback would invent a pool).
        float? PropOpt(string key)
        {
            foreach (var d in damageChain)
                if (d.TryFloat(key, out var f))
                    return f;
            return null;
        }

        stats.VehicleArmor = PropOpt("armor");
        stats.VehicleHealth = PropOpt("health");

        // Off the damage chain, because the def the vehicle SPAWNS as is what the original copies
        // these off: an AI variant's own chain for an AI aeroplane, and the player chain, where
        // player_airplane authors the -300, for a flown one.
        stats.AiTargetBias = PropOpt("target_bias") ?? 0f;
        stats.AiStructBias = PropOpt("struct_bias") ?? 0f;

        // def-level injure_anims: [frac, animName] pairs (smoke trail / fuel leak). The player's
        // two-stage ladder, or the AI defs' own seven (eight on the balmoral).
        foreach (var d in damageChain)
        {
            if (d.List("injure_anims") is not { } injureList)
                continue;
            foreach (var item in injureList)
                if (item is List<object?> { Count: >= 2 } entry
                    && entry[0] is float frac && entry[1] is string anim)
                    stats.VehicleInjureAnims.Add((frac, anim));
            break;
        }

        foreach (var d in damageChain)
        {
            if (d.List("collision") is not { } probeList)
                continue;
            foreach (var item in probeList)
                if (item is List<object?> { Count: >= 3 } p
                    && p[0] is float px && p[1] is float py && p[2] is float pz)
                    stats.CollisionProbes.Add(new Vector3(px, py, pz));
            break;
        }

        // The pilot the def flies with: nine skill slots and a voice accent, each resolved on its
        // own down the AI chain, since a militia variant overrides some and inherits the rest.
        if (aiChain != null)
        {
            int? Skill(string key)
            {
                foreach (var d in aiChain)
                    if (d.TryFloat(key, out var f))
                        return (int)f;
                return null;
            }
            stats.AiPilotSkills = new AiSkillVector
            {
                DareDevil = Skill("dare_devil"),
                NaturalTouch = Skill("natural_touch"),
                SixthSense = Skill("sixth_sense"),
                DeadEye = Skill("dead_eye"),
                QuickDraw = Skill("quick_draw"),
                SteadyHand = Skill("steady_hand"),
                StunRecovery = Skill("stun_recovery"),
                Talker = Skill("talker"),
                Constitution = Skill("constitution"),
            };
            stats.AiAccentId = Skill("accentID");
            // The name the targeting readout prints: a militia def authors its own
            // ("MSG_VEH_MEDUSA_KESTREL"), and one that does not inherits the airframe's.
            foreach (var d in aiChain)
            {
                if (d.Str("title") is { } title)
                {
                    stats.AiTitleKey = title;
                    break;
                }
            }
        }

        // weapons: the AI chain's own armament, 5-tuples in list order. Guarded on aiChain rather
        // than damageChain because the player chain's block is the buyable catalogue, not a fit.
        foreach (var d in aiChain ?? new List<ZrdrDict>())
        {
            if (d.List("weapons") is not { } weaponList)
                continue;
            foreach (var item in weaponList)
                if (item is List<object?> { Count: >= 5 } w && w[0] is string weaponId
                    && w[1] is float rounds && w[2] is float refire
                    && w[3] is float minRange && w[4] is float maxRange)
                    stats.AiWeapons.Add(new AiWeaponSlot
                    {
                        WeaponId = weaponId,
                        Rounds = (int)rounds,
                        RefireSeconds = refire,
                        MinRangeM = minRange,
                        MaxRangeM = maxRange,
                    });
            break;
        }

        // damaged_engine_sound: an ARRAY of [soundName, pitchLo, pitchHi] entries, one swap candidate
        // each. Every shipped def inherits basic_airplane's single entry, so the random draw over the
        // array collapses to it; the two floats are the pitch range, present only on the longer form.
        foreach (var d in chain)
        {
            if (d.List("damaged_engine_sound") is not { Count: > 0 } dmgList)
                continue;
            if (dmgList[0] is List<object?> { Count: >= 1 } entry && entry[0] is string dmgName)
            {
                stats.DamagedEngineSound = dmgName;
                if (entry.Count >= 3 && entry[1] is float pitchLo && entry[2] is float pitchHi)
                {
                    stats.DamagedEnginePitchLo = pitchLo;
                    stats.DamagedEnginePitchHi = pitchHi;
                    stats.DamagedEnginePitchRandom = true;
                }
            }
            break;
        }

        // turrets: nearest def in the chain that has the block (docs/formats/vehicle.md), the AI
        // chain on an AI load as the damage model is. The pair is authored twice over and the two
        // are different guns: the AI Balmoral's rear mount sees 900 m where the player rig's sees 350.
        foreach (var d in damageChain)
        {
            if (d.Dict("turrets") is not { } turrets)
                continue;
            foreach (var (viewKey, firstPerson) in new[] { ("firstp", true), ("thirdp", false) })
            {
                if (turrets.List(viewKey) is not { } mounts)
                    continue;
                foreach (var item in mounts)
                {
                    if (item is not List<object?> entry)
                        continue;
                    var mount = ZrdrDict.FromAlternating(entry);
                    if (mount.Str("title") is { } title && mount.Str("node") is { } node)
                        stats.TurretMounts.Add(new TurretMount
                        {
                            Title = title,
                            Node = node,
                            FirstPerson = firstPerson,
                        });
                }
            }
            break;
        }

        // destroyable_parts schema: docs/formats/vehicle.md. An AI load resolves none, no
        // roster-named chain authors the block, so an AI aircraft is zone-less.
        foreach (var d in damageChain)
        {
            if (d.List("destroyable_parts") is not { } partsList)
                continue;
            foreach (var item in partsList)
            {
                if (item is not List<object?> p || p.Count == 0 || p[0] is not string partName)
                    continue;
                var part = new DestroyablePart { Name = partName };
                bool hpSet = false, armorSet = false;
                for (int i = 1; i < p.Count; i++)
                {
                    switch (p[i])
                    {
                        case float hp when !hpSet:
                            part.MaxHp = hp;
                            hpSet = true;
                            break;
                        case float armor when !armorSet:
                            part.MaxArmor = armor;
                            armorSet = true;
                            break;
                        case "critical":
                            part.Critical = true;
                            break;
                        case "engine":
                            part.Engine = true;
                            break;
                        case "got_hit_anim" when i + 1 < p.Count && p[i + 1] is List<object?> hit:
                            part.GotHitAnim = hit.Count > 0 ? hit[0] as string : null;
                            i++;
                            break;
                        case "injure_anims" when i + 1 < p.Count && p[i + 1] is List<object?> anims:
                            foreach (var a in anims)
                                if (a is List<object?> { Count: >= 2 } entry
                                    && entry[0] is float frac && entry[1] is string anim)
                                    part.InjureAnims.Add((frac, anim));
                            i++;
                            break;
                    }
                }
                stats.DestroyableParts.Add(part);
            }
            break;
        }

        // stock engine power factor: 'engine' prop → engines.json row [id, name, power]
        int engineId = (int)Prop("engine", 0f);
        foreach (var row in Zrdr.LoadFile(zrdrPath, "engines.json"))
            if (row is List<object?> { Count: >= 3 } r && r[0] is float id && (int)id == engineId && r[2] is float power)
                stats.EnginePower = power;

        // global flight constants + sound curves
        if (Zrdr.LoadFile(zrdrPath, "player.json")[0] is List<object?> playerList)
        {
            var player = ZrdrDict.FromAlternating(playerList);
            stats.Gravity = player.Float("nom_gravity", stats.Gravity);
            stats.StallMag = player.Float("stall_mag", stats.StallMag);
            stats.WarningShotMax = player.Float("warning_shot_max", stats.WarningShotMax);
            // Authored as a divisor and stored as its RECIPROCAL, which is the original's own
            // parse-time conversion (0x0047469f through 0x004746ad divides 1.0 by what it read),
            // so the shipped 2.0 drains half a second of charge per second of quiet.
            float dissipation = player.Float("warning_shot_dissipation", 1f / stats.WarningShotDissipation);
            stats.WarningShotDissipation = dissipation != 0f ? 1f / dissipation : 0f;
            stats.WarningShotInterval = player.Float("warning_shot_interval", stats.WarningShotInterval);
            stats.WarningShotSound = player.Str("warning_shot_sound") ?? stats.WarningShotSound;
            stats.BulletHitSound = player.Str("bullet_hit_sound") ?? stats.BulletHitSound;
            stats.StickyBulletCatchupRate = player.Float("sticky_bullet_catchup_rate", stats.StickyBulletCatchupRate);
            stats.StickyBulletForgetInterval = player.Float("sticky_bullet_forget_interval", stats.StickyBulletForgetInterval);
            stats.StickyBulletDistFactor = player.Float("sticky_bullet_dist_factor", stats.StickyBulletDistFactor);
            // Degrees in the file, radians in the field, the original's own parse-time conversion.
            // ⚠ Do NOT reproduce the executable's missing-key bug here (its absent-inaccuracy branch
            // writes catchup_rate's global); the shipped player.json always carries the key.
            stats.StickyBulletInaccuracy = Mathf.DegToRad(
                player.Float("sticky_bullet_inaccuracy", Mathf.RadToDeg(stats.StickyBulletInaccuracy)));

            // autohead_* (docs/formats/vehicle/player-globals.md): turn_max's authored
            // degrees are converted THEN DOUBLED, unlike its already-doubled compiled default.
            stats.AutoheadTurnTime = player.Float("autohead_turn_time", stats.AutoheadTurnTime);
            stats.AutoheadTurnMax = player.TryFloat("autohead_turn_max", out var turnMaxDeg)
                ? Mathf.DegToRad(turnMaxDeg) * 2f
                : stats.AutoheadTurnMax;
            stats.AutoheadTurnMinPitch = player.TryFloat("autohead_turn_min_pitch", out var minPitchDeg)
                ? Mathf.DegToRad(minPitchDeg)
                : stats.AutoheadTurnMinPitch;

            // Flight globals for the decoded model, see the field comments above for units and
            // fallback provenance. Not yet read by FlightModel.cs.
            const float mph = PhysicsConstants.MphToMs;
            stats.LiftAccelRate = player.Float("lift_accel_rate", stats.LiftAccelRate);
            stats.LiftAoaCosLo = player.TryFloat("liftAOAs", out var liftAoaLoDeg, 0)
                ? Mathf.Cos(Mathf.DegToRad(liftAoaLoDeg)) : stats.LiftAoaCosLo;
            stats.LiftAoaCosHi = player.TryFloat("liftAOAs", out var liftAoaHiDeg, 1)
                ? Mathf.Cos(Mathf.DegToRad(liftAoaHiDeg)) : stats.LiftAoaCosHi;
            stats.MaxAoaCos = player.TryFloat("maxAOA", out var maxAoaDeg, 0)
                ? Mathf.Cos(Mathf.DegToRad(maxAoaDeg)) : stats.MaxAoaCos;
            stats.HighGStart = player.Float("highGs", stats.HighGStart, 0);
            stats.HighGMax = player.Float("highGs", stats.HighGMax, 1);
            stats.LowGStart = player.Float("lowGs", stats.LowGStart, 0);
            stats.LowGMax = player.Float("lowGs", stats.LowGMax, 1);
            stats.TurnFadeIn = player.Float("turn_fade_in", stats.TurnFadeIn / mph) * mph;
            stats.TurnFadeOut = player.Float("turn_fade_out", stats.TurnFadeOut / mph) * mph;
            stats.YawLowSpeed = player.Float("yaw_low_speed", stats.YawLowSpeed);
            stats.YawHighSpeed = player.Float("yaw_high_speed", stats.YawHighSpeed);
            stats.YawFadeIn = player.Float("yaw_fade_in", stats.YawFadeIn / mph) * mph;
            stats.YawMax = player.Float("yaw_max", stats.YawMax / mph) * mph;
            stats.YawFadeOut = player.Float("yaw_fade_out", stats.YawFadeOut / mph) * mph;
            stats.HighSpeedPitchFadeLo = player.Float("high_speed_pitch_fade", stats.HighSpeedPitchFadeLo / mph, 0) * mph;
            stats.HighSpeedPitchFadeHi = player.Float("high_speed_pitch_fade", stats.HighSpeedPitchFadeHi / mph, 1) * mph;
            stats.DragFadeSpeed = player.Float("drag_fade_speed", stats.DragFadeSpeed / mph) * mph;
            // Raw scalars, metres already, no MPH conversion on any of the three.
            stats.GroundBlowElev = player.Float("groundblow_elev", stats.GroundBlowElev);
            stats.GroundBlowMag = player.Float("groundblow_mag", stats.GroundBlowMag);
            stats.AiGroundBlow = player.Float("ai_groundblow", stats.AiGroundBlow);
            // bounce_factor sits inside the `crash` block, beside the two damage ranges; a missing
            // block keeps the compiled fallback, which is the original's own parse order.
            if (player.Dict("crash") is { } crash)
            {
                stats.BounceFactor = crash.Float("bounce_factor", stats.BounceFactor);
                // Both ranges are [floor, scale] in that order, the original's own element order.
                stats.CollideArmorFloor = crash.Float("armor_damage_range", stats.CollideArmorFloor);
                stats.CollideArmorScale = crash.Float("armor_damage_range", stats.CollideArmorScale, 1);
                stats.CollideHealthFloor = crash.Float("health_damage_range", stats.CollideHealthFloor);
                stats.CollideHealthScale = crash.Float("health_damage_range", stats.CollideHealthScale, 1);
            }

            // curve blocks hold (x, y) pairs: min_* = ramp start, max_* = ramp end
            static SoundCurve Curve(ZrdrDict d, string minKey, string maxKey, SoundCurve fb) =>
                d.Has(minKey) && d.Has(maxKey)
                    ? new SoundCurve(d.Float(minKey, fb.MinX), d.Float(minKey, fb.MinY, 1),
                                     d.Float(maxKey, fb.MaxX), d.Float(maxKey, fb.MaxY, 1))
                    : fb;
            if (player.Dict("engine_sound") is { } eng)
            {
                stats.EngineVolume = Curve(eng, "min_throttle_volume", "max_throttle_volume", stats.EngineVolume);
                stats.EnginePitch = Curve(eng, "min_throttle_pitch", "max_throttle_pitch", stats.EnginePitch);
            }
            if (player.Dict("prop_sound") is { } prop)
            {
                stats.WhineVolume = Curve(prop, "min_speed_volume", "max_speed_volume", stats.WhineVolume);
                stats.WhinePitch = Curve(prop, "min_speed_pitch", "max_speed_pitch", stats.WhinePitch);
            }
            if (player.Dict("rattle") is { } rattle)
            {
                stats.RattleSound = rattle.Str("sound") ?? stats.RattleSound;
                stats.RattleSpeedGate = rattle.Float("speed_range", stats.RattleSpeedGate);
            }
        }
        return stats;
    }

    private static float Factor(Random rng) =>
        (float)((rng.NextDouble() * 2.0 - 1.0) * AiSpawnJitterSpread + 1.0);

    private float SumParts(Func<DestroyablePart, float> of)
    {
        float total = 0f;
        foreach (var part in DestroyableParts)
            total += of(part);
        return total;
    }
}
