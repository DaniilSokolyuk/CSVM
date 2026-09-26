using System;
using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>
/// The weapon-fire subsystem for a shared world: a pool of projectiles integrated with the data's
/// own ballistics, plus their visuals and impacts, tracer streaks, muzzle flashes, per-surface
/// impact sound, and the named IMPACT effect. Guns and hardpoints feed it through
/// <see cref="Spawn"/>; it runs itself each physics frame, and one pool serves every player.
/// Hit detection is a per-step raycast over world and aircraft, with the shooter's own body
/// excluded per shot so a pilot's rounds never hit their own launcher. World surfaces select their
/// IMPACT row by the struck collider's surface id (<see cref="SurfaceIdOf"/>); an aircraft hit
/// routes its damage to the struck plane's own part model, never the destructible pipeline. A
/// round that misses may still fuse, see <see cref="ProximityFuseTriggered"/> and
/// <see cref="ApplyDamage"/>.
/// </summary>
public sealed partial class ProjectilePool : Node3D
{
    /// <summary>The <c>shooterId</c> of a round nobody owns, the weapon lab's, and the default.
    /// It matches no player, so such a round can still warn every aircraft it passes.</summary>
    public const int NoShooter = -1;

    /// <summary>Launch speed for a def carrying no <c>VELOCITY</c>, m/s. Shared with the aim
    /// assist's scan so the lead it solves is solved for the speed the round actually leaves at.</summary>
    public const float DefaultVelocity = 500f;

    /// <summary>Path length a def carrying no <c>RANGE</c> flies before it ends, m, the value
    /// <c>FUN_005ad630</c> writes into weapon <c>+0x1c</c> before it reads the key. Only the smoke
    /// screen and the rear-arc flare leave it unauthored, and the flare's 2.0 s
    /// <c>DETONATION_TIME</c> ends it long before.</summary>
    public const float DefaultRange = 500f;

    /// <summary>The impact burst's upper ring's disc normal in its own node's frame. The
    /// <c>he_ringer1</c> model's box is 8.48 m square in X and Y and 1 m deep in Z. Its disc
    /// therefore stands in the XY plane and faces along Z, where the ground ring's lies in
    /// XZ.</summary>
    public static readonly Vector3 UpperRingDiscNormal = Vector3.Back;

    /// <summary>Where a hit's damage goes: given the struck collider and the weapon's
    /// <c>HEALTH_DAMAGE</c>, apply it to the destructible that collider belongs to. Wired to
    /// <c>AnimRuntime.DamageAt</c> in flight; null when there is no destructible system (the static
    /// viewer, a chapter with no anim runtime), where impacts stay purely cosmetic.</summary>
    public System.Func<Node?, float, bool>? DamageSink;

    /// <summary>The zeppelin routing gate (M4 F18): asked before weapon damage reaches
    /// <see cref="DamageSink"/> for a struck body, with the firing weapon. False refuses the
    /// DAMAGE only, the impact effect and sound still play. Wired to
    /// <c>ZeppelinRuntime.GateWeaponDamage</c> (a weapon without <c>DAMAGES_ZEPPELIN</c> cannot
    /// hurt a gasbag); null gates nothing.</summary>
    public System.Func<Node?, WeaponDef, bool>? WorldDamageGate;

    /// <summary>Where a ground strike that passed <see cref="CraterGate"/> goes: given the impact
    /// point and the struck collider, carve the bowl and flatten the decorations in it, returning
    /// whether the carve landed. The faithful rule asks only for a collider whose node carries
    /// <see cref="SceneBuilder.CanModifyMeta"/>, which no shipped node does; the remake's own
    /// option asks for any rocket. Wired to <c>CraterField.TryCarve</c> in a collidable flight.</summary>
    public System.Func<Vector3, Node?, bool>? CraterSink;

    /// <summary>Plays a named IMPACT effect (its puffer half) at a hit point through the world-effects
    /// runtime. These are the gun/rocket smoke and fireballs whose <c>ANIMATION</c> is an ON_CALL
    /// effect def rather than a gamez model. Null in views with no anim runtime. After the point come the
    /// template's own basis (<see cref="EffectOrient"/>) and the basis the burst's upper ring takes
    /// (<see cref="UpperRingOrient"/>, null leaving its authored axis). Last is the time bound in
    /// seconds, 0 for the runtime's own and <see cref="GunEffectTtl"/> for guns.</summary>
    public System.Action<string, Vector3, Basis, Basis?, float>? EffectSink;

    /// <summary>Whether the world-effects runtime binds a def of this name (<c>AnimRuntime.Handles</c>).
    /// Asked before the gamez-model impact spawn: an IMPACT name can be BOTH a gamez root and an
    /// ON_CALL def anchored on it (the seeker's <c>ballflare.flt</c>), and the original plays the
    /// def, so a static instance of its template must not stand in for it. Null carries nothing.</summary>
    public System.Func<string, bool>? EffectHandles;

    /// <summary>The disabling wash's route to the struck human's pane, <c>ScreenFlash.PlayBlend</c>'s
    /// shape (player index, colour, weight, duration, start delay). Assigned by the session; null (a
    /// lab, a headless view) washes nobody while the AI stun beside it still runs.</summary>
    public System.Action<int, Color, float, float, float>? WashSink;

    /// <summary>The <c>ENGINE_DEAD</c> pair every choker cloud reads, resolved once by
    /// <see cref="TanglerChoke.EngineDeadBounds"/> over the catalogue; the static image's pair
    /// until the session assigns it.</summary>
    public (float Min, float Max) EngineDeadBounds =
        (TanglerChoke.ImageEngineDeadMin, TanglerChoke.ImageEngineDeadMax);

    /// <summary>The world's beeper tags: a <c>BEEPER</c> hit on an aircraft calls its
    /// <c>TryTag</c>, a <c>BEEPER_SEEKER</c> round asks its <c>PickTarget</c> each frame. Assigned by
    /// the session beside the sinks above; null (a lab, a headless view) tags and seeks nothing.</summary>
    public BeeperTags<FlightController>? BeeperTags;

    internal const float WorldGravity = PhysicsConstants.NomGravity; // the sprite debris (casings,
                                                                     // sparks) falls at it; a round
                                                                     // does not, a weapon's own
                                                                     // GRAVITY is already m/s²
    internal const float RocketSpeedScale = 1f; // rocket launch speed/accel scale (weapons.rocketSpeedScale);
                                                // 1 = neutral. Scales both together, pending playtest A/B.
                                                // ⚠ The four tracer constants below are measured off the original's `rabbit_blur` geometry, not
                                                // tuned by eye (docs/org/tracers.md). Retune only with that decode open.
    internal const float TracerLength = 4.5f;   // streak length, m, the authored quad spans z -4.5..0
    internal const float TracerWidth = 0.2f;    // m, both crossed quads are 0.2 m wide
    // The streak's geometry runs FORWARD from the round's simulated position: the position is the
    // streak's TAIL, and the bright head sits at the far end. (The engine attaches the model with its
    // origin at the round and its geometry along -Z, the flight direction.)
    internal const float TracerTipOffset = 4.5647f; // m ahead of the round, the tip node's own transform
    internal const float TracerTipSize = 0.2891f;   // m across, the tip disc, radius 0.1445 doubled
    // The authored streak carries white vertex colours and its texture unmodified, so neutral is the
    // authored value: the separate tip disc (the actual bright head) and the second crossed quad
    // supply the brightness an overbright multiplier would otherwise fake. The config key stays so a
    // bloom-less display can still be pushed.
    internal const float TracerBrightness = 1.0f; // default; user tunes via weapons.tracerBrightness
    // Distance-visibility floor: the minimum screen footprint (px) a tracer's drawn width/length
    // are allowed to shrink below at range, so a round many hundred metres out still reads as a
    // fleck instead of vanishing into sub-pixel geometry (the original screenshots show distant fire
    // as visible streaks). 0 disables the floor outright. Off the default screenshots/goldens (none
    // fire a weapon) so this never moves a golden hash, magnitude is TUNE, owed the
    // cockpit A/B via weapons.tracerMinPixels.
    internal const float TracerMinPixels = 2.0f;

    // How long a gun hit's `<caliber><ammo>_gunhit` instance may run: the family's longest authored
    // stop (mag's +0.3 s), bounding the slug defs that ship no stop at all. See
    // docs/formats/weapon-effects.md for the per-ammo emission windows.
    internal const float GunEffectTtl = 0.3f;

    // Minimum sim seconds between two plays of one gun's impact effect. The effect templates are
    // shared and relocated, not copied, so two plays inside one emission window only move
    // a single emitter, below this the extra plays buy nothing and only restart sequences. Sits at
    // the ap/dum emission window (0.1 s) and just under the fastest gun's FIRE_RATE (10.5/s), so a
    // single group still gets its smoke on essentially every round.
    private const float GunEffectInterval = 0.1f;

    // The raw candidate ceiling of the sphere query, well above anything a chapter packs into
    // one blast radius; the behavioural limit is MaxBlastTargets below.
    private const int MaxBlastBodies = 4096;
    // The original's splash gather (FUN_004cb420) writes into a 32-entry hit buffer and logs
    // "Database intersections array is full" for every candidate past it, so a burst damages at
    // most 32 objects. Which 32 is grid-walk order there; here it is the nearest 32, so the cap
    // drops the farthest and weakest hits. ⚠ Never discard a capped blast silently: the log line
    // names the weapon, the burst and how many candidates were dropped.
    private const int MaxBlastTargets = 32;
    // The cover ray starts this far off the struck surface along its normal, so a burst sitting on
    // a wall's face does not read that wall as cover for its own side while a target behind the
    // wall still finds it in the way.
    private const float CoverRayLift = 0.1f;

    // A fuse candidate whose closest approach sits at the very end of the swept step is still
    // closing, hold the fuse: the next step detonates closer, or the hit ray lands a direct hit.
    private const float StillClosingFraction = 0.999f;

    // The steering step's literals (FUN_005af960, docs/org/ordnanceTypes.md "Guidance"). The turn
    // scalar is DAT_00a1e1b8, a per-shot global the weapons init writes as 1.0 and the spawn
    // (FUN_005aef40) resets to 1.0 after every round it makes, so it is 1 on every steering frame
    // in this binary. The ramp is TURN_SUSPEND_TIME's, unauthored throughout, so it is 1.
    private const float TurnPenaltyFloor = 0.8f;
    private const float TurnPenaltyCosWeight = 0.2f;
    private const float TurnRateScale = 1f;
    private const float TurnRampUnsuspended = 1f;

    // The disabling wash's start delay (FUN_004b9bc0's SONIC/FLASH branch passes 1.0 s to
    // FUN_0042e9d0 as its first argument), during which nothing paints; the colours sit below.
    private const float DisablingWashStartDelay = 1f;
    // A desired direction this close to dead astern leaves the heading where it is: the axis to
    // swing about is undefined there.
    private const float SteerOppositeDot = 0.99999f;

    private const int MaxProjectiles = 1024;
    private const int MaxFlashes = 128;
    // ⚠ Keep this above every flight rig's priority (they draw at the default 0). Each anchored
    // effect is placed on the pose its anchor NODE holds when this pool's frame callback runs.
    // An aeroplane writes its interpolated drawn pose inside its own callback. Drawing first would
    // read the previous frame's pose, putting the flash and the shot light a frame astern of the
    // gun. A suite cannot see this: a hand-stepped harness poses the anchor before it draws.
    private const int DrawPriority = 100;
    // ⚠ No rocket streak constants here any more. `he_rocket`/`ap_rocket` and every other ordnance
    // FLYOUT prototype are LOD-wrapped missile BODIES, no `rabbit_blur` streak child, no tip disc
    // (measured, docs/org/tracers.md). An ordnance round's visible trail is its MODEL_ANIMATION
    // puffer smoke, which its def instance drives. The old RocketStreakScale/RocketExhaustScale
    // streaks had no counterpart in the data and are deleted; a chapter missing the prototype now
    // shows the smoke trail alone rather than a stand-in streak.
    private const float MuzzleSize = 0.5f;    // m
    private const float MuzzleLife = 0.05f;   // s
    // The flash triad: three quads 120 degrees apart on one continuous per-shot roll. The
    // pick-one reading was implemented and rejected at the controls; see
    // docs/formats/weapon-effects.md "Engine wiring" for the evidence.
    private const int MuzzleFlashCount = 3;
    private const float ImpactSize = 3.0f;    // m
    private const float ImpactLife = 0.14f;   // s
    private const float ImpactModelLife = 0.4f; // s the instanced IMPACT model shows before it is freed
    // The stand-in explosion burst a hardpoint weapon shows when no world-effects runtime is present
    // to render its real fireball (a scene-less pool: the weapon lab, a chapter with no world scene).
    private const int ExplosionSprites = 7;
    private const float ExplosionSize = 12f;  // m
    private const float ExplosionLife = 0.5f; // s
    private const float ExplosionSpread = 6f; // m, the cluster radius

    // The authored muzzle smoke (muzzle_burst's `muzzlepuffer`): smoke101–103, aft 20 m/s in the
    // muzzle frame, size 0.3–0.6 m, life 0.1–0.2 s, ±0.8 m/s random velocity, 5 cm deviation.
    // The data emits every 0.05 s over a 0.3 s window from the moving muzzle node; the per-shot
    // puff count here is the gloss of that window (TUNE), the authored ranges are verbatim.
    private const int MuzzleSmokePuffs = 6;            // 0.3 s window / 0.05 s TIME_INTERVAL
    private const float MuzzleSmokeAftSpeed = 20f;     // m/s, local_velocity z
    private const float MuzzleSmokeDeviation = 0.05f;  // m, deviation_distance
    private const float MuzzleSmokeRandVel = 0.8f;     // m/s, min/max_random_velocity
    private const float MuzzleSmokeSizeMin = 0.3f, MuzzleSmokeSizeMax = 0.6f;   // m
    private const float MuzzleSmokeLifeMin = 0.1f, MuzzleSmokeLifeMax = 0.2f;   // s

    private const int MaxSmoke = 256;   // cap on live muzzlepuffer sprites across every gun (a cap, not a tuned size)

    // The ejected shell casing (gunshell): one pooled chapter-gamez instance per shot, flying the
    // def's own OBJECT_MOTION. Per-shot instances so sustained fire never drops an ejection, a
    // shared anchor under AnimRuntime's already-live gate would swallow all but one per RUN_TIME.
    private const int MaxCasings = 128;

    // The muzzle light flash (muzzle_burst's `testfp`) as pooled OmniLight3Ds: the third-person
    // variants, and the first-person pair in enhanced mode or with no point term bound. The data
    // deactivates them on the next event tick, so a flash lives ~2 frames here.
    private const int MaxMuzzleLights = 8;
    private const float MuzzleLightLife = 0.03f;   // s
    private const float MuzzleLightEnergy = 2.5f;  // TUNE: no def carries energy; Godot's omni needs one

    // The authored water-splash playback, verbatim from splash1.flt/bsplsh.flt (identical shapes).
    // See docs/formats/weapon-effects/ordnance.md "Water splash playback" for the source events.
    private const float SplashRunTime = 2.0f;      // s, the def's sequence length
    private const float SplashBaseGrowTime = 0.2f; // s, base 1→2
    private const float SplashBaseEaseStart = 1.0f; // s (0.2 + EVENT_OFFSET 0.8), 2→1.8 over 1 s
    private const float SplashBaseMax = 2.0f, SplashBaseEnd = 1.8f;
    private const float SplashColumnScale = 100f;  // the column's authored initial Y scale
    private const float SplashFadeInTime = 0.05f;   // s
    private const float SplashFadeOutStart = 1.0f;  // s (0.05 + EVENT_OFFSET 0.95)
    private const float SplashFadeOutTime = 1.0f;   // s
    // The column's authored flipbook rate; docs/formats/weapon-effects/ordnance.md as above.
    private const float SplashFlipbookFps = 4f;
    // TUNE, source-visible for the A/B: 8x the authored literal width, judged at the controls.
    // docs/formats/weapon-effects/ordnance.md as above. Not a `const` so a run can set it to 1.
    private static readonly float SplashColumnWidthScale = 8f;
    // The column's authored flipbook frames, reusing TextureCycler's frame-swap machinery: not
    // reached by SceneBuilder's automatic registration, since splash1_splash's polygon binds a
    // sibling material with no cycle block, so EnsureSplashFlipbook registers it lazily instead.
    private static readonly string[] SplashFlipbookTextures = { "splash01", "splash02", "splash03" };

    private static readonly Color MuzzleSmokeTint = new(0.85f, 0.85f, 0.85f);

    // muzzle_burst's 1stperson_lts pair, bigmuzzle_lt then muzzle_lt, verbatim: the AT_NODE offset
    // on the effects root, the near/far range and the colour. Neither authors ambient or diffuse, so
    // each adds its colour at weight 1 (docs/org/vertexLighting.md, "Point lights").
    private static readonly (Vector3 At, float Near, float Far, Color Color)[] FirstPersonLights =
    {
        (new Vector3(11f, -1f, -5f), 13.5f, 21.25f, new Color(0.88f, 0.78f, 0.36f)),
        (new Vector3(-11f, -1f, -5f), 12.9f, 18.25f, new Color(0.93f, 0.78f, 0.36f)),
    };

    // Where a gun shot's three secondaries sit, in the firing muzzle node's own frame, -Z forward.
    // The casing, the muzzlepuffer smoke and the muzzle lights hang off the muzzleburst_effects
    // root, which muzzle_burst_slug's CallAnimation places at AT_NODE (0, -0.2, -1.0). The
    // first-person lights' own (+-11, -1, -5) composes on top of it. The flash quads carry no
    // displacement and stay at the node. docs/formats/weapon-effects.md, "Muzzle flashes".
    private static readonly Vector3 MuzzleSecondaryOffset = new(0f, -0.2f, -1.0f);

    // Muzzle-flash sprite tint, unrelated to the tracer tint below, which is a separate,
    // uniform overbright multiplier so each ammo's own tracer texture colour shows through unshifted.
    private static readonly Color SlugTint = new(1.0f, 0.85f, 0.35f);   // warm yellow
    private static readonly Color RocketTint = new(1.0f, 0.6f, 0.25f);  // orange exhaust

    // The muzzle-flash ammo-type axis (weapon-effects.md "Muzzle & tracer textures"): each
    // chapter's texture archive carries a `{slug,dum,ap,mag}_muzzle1` per ammo type. The index into
    // this array is resolved once per weapon from its FIRE ANIMATION binding (MuzzleAmmoIndex),
    // `muzzle_burst_slug`/`_dum`/`_ap`/`_mag` name the type directly; the base `muzzle_burst` /
    // heavy-mount `muzzle_burst2` carry no ammo suffix and default to slug, the common case.
    private static readonly string[] MuzzleAmmoTextures = { "slug_muzzle1", "dum_muzzle1", "ap_muzzle1", "mag_muzzle1" };

    // The tracer ammo-type axis (weapon-effects.md "Muzzle & tracer textures"): each chapter's
    // texture archive also carries a per-ammo tracer streak (`tracer_slug`/`_dumdum`/`_armorpierce`/
    // `_magnesium`), same four-way axis as the muzzle flash, TracerIdx reuses MuzzleAmmoIndex.
    // ⚠ There is deliberately no fifth entry for the generic `tracer1`: no gun prototype binds it,
    // and ordnance draws no streak at all (see the rocket note above), so the array is exactly the
    // four the data binds.
    private static readonly string[] TracerTextures =
        { "tracer_slug", "tracer_dumdum", "tracer_armorpierce", "tracer_magnesium" };

    // The tip disc's texture axis, index-parallel to TracerTextures (docs/org/tracers.md).
    // ⚠ Keep the spelling as shipped: the AP entry is `armourpiercetip` (British) while its
    // streak is `tracer_armorpierce` (American).
    private static readonly string[] TipTextures =
        { "slugtip", "dumdumtip", "armourpiercetip", "magnesiumtip" };

    // The disabling wash's colours (docs/org/ordnanceTypes.md "SONIC and FLASH"): red for SONIC,
    // white for FLASH.
    private static readonly Color SonicWashColour = new(1f, 0f, 0f);
    private static readonly Color FlashWashColour = new(1f, 1f, 1f);
    // The reset value for _ray.Exclude between shots, shared and never mutated.
    private static readonly Godot.Collections.Array<Rid> NoExclude = new();
    // Nearest-first order for the splash gather, so the 32 cap drops the farthest hits.
    private static readonly Comparison<BlastCandidate> ByDistance =
        (a, b) => a.DistanceSq.CompareTo(b.DistanceSq);

    private readonly Proj[] _proj = new Proj[MaxProjectiles];
    // One sprite list per muzzle-flash ammo texture (MuzzleAmmoTextures), a separate MultiMesh per
    // texture, since a MultiMesh's material (and so its texture) is shared across every instance.
    private readonly List<Sprite>[] _muzzle = { new(), new(), new(), new() };
    private readonly List<Sprite> _impact = new();
    private readonly List<Sprite> _smoke = new();   // muzzlepuffer smoke (alpha-blended)

    private readonly TextureArchive _textures;
    private readonly SoundArchive? _sounds;
    private readonly IReadOnlyDictionary<string, SoundDef>? _soundDefs;
    private readonly IReadOnlyDictionary<string, SoundGroup>? _soundGroups;
    // PlaySound resolves a SOUND_GROUPS name through this, same subsystem as _rng but its own
    // System.Random stream, SoundGroup.Pick's signature (docs/formats/sounds.md).
    private readonly System.Random _soundGroupRng = Rng.NewSystemRandom(Rng.Weapons);

    // The FLYOUT MODEL body: rockets fly the original's own projectile mesh, instanced from a
    // chapter-gamez prototype root (`he_rocket`, `ap_rocket`, …) via the world's SceneBuilder, the
    // roots exist once per chapter and their geometry is nose-along-(-Z). Only rockets get a body:
    // guns fire ≤~10 rounds/s that live ~1 s each (dozens alive) and stay on the cheap MultiMesh
    // tracer quad, while a rocket lives ~0.8 s at 1/s (≤1 alive per player), so a full mesh per
    // rocket is cheap. Null archives (no world, or a chapter lacking the root) ⇒ streak-only fallback.
    private readonly GameZ? _flyoutGamez;
    private readonly SceneBuilder? _flyoutScene;
    private readonly Dictionary<string, GameZNode?> _flyoutNodes = new(); // model name → prototype (cached)

    // The FLYOUT MODEL_ANIMATION defs (`he_rocket`, `sonic`, `torpedo_trail`, …, compiled in
    // cam_anim): each live round runs its own instance of its weapon's def, which is where its
    // trail puffers, its launch look and its sounds come from (ProjectileFlyoutAnim.cs). The
    // emitters those instances switch on are pooled here and reused once their smoke has decayed.
    private readonly AnimProgram? _flyoutAnims;
    private readonly List<TrailEmitter> _trailEmitters = new();          // reusable emitters, all states

    // Casing ejection: each gun shot ejects the authored `gunshell` casing, the chapter-gamez
    // mesh (its child `g1` carries model 60, the shell1/shell2-textured shell) flying the gunshell
    // def's own OBJECT_MOTION verbatim (LOCAL gravity, ranged ballistic launch, forward-rotation
    // tumble over RUN_TIME 2 s). Instances are pooled and reused once a casing expires; the spec is
    // read once from the anim program the way the rocket trails are (see BuildCasingSpec).
    private readonly List<CasingSlot> _casings = new();

    // Pooled muzzle-light flashes: real OmniLight3Ds, reused round-robin.
    private readonly List<LightFlash> _lights = new();

    // The first-person pair's lights waiting for, or inside, their one drawn frame of the point
    // term (SubmitPointFlashes). Empty unless BindPointLights was called.
    private readonly List<PointFlash> _pointFlashes = new();

    // The session's wind, read by the rocket-trail puffers this pool builds. Rocket trails
    // carry FRICTION and no WIND_FACTOR, so they take the engine default of 1, fully carried.
    private readonly Effects.EffectAmbience _ambience = Effects.EffectAmbience.Still;
    // A per-surface IMPACT `ANIMATION` naming a chapter gamez node (the water splash prototypes)
    // is instanced at the hit point via the flyout GameZ/SceneBuilder. Everything else, a
    // reader/control def or an unresolved name, stays on the stand-in spark instead.
    private readonly Dictionary<string, GameZNode?> _impactNodes = new(); // impact anim name → prototype (cached)
    private readonly HashSet<string> _impactFxLogged = new();
    private readonly List<ImpactFx> _impactFx = new();
    // The splash fade's per-instance translucent twin, cached per SOURCE material,
    // installed as a surface override on every splash instance that shares it (never edited in
    // place, the same rule AnimRuntime's own fade-twin cache follows), each instance then driven
    // independently through its own SetInstanceShaderParameter. Null once cached means the source
    // shader had no alpha path to twin (logged, not swallowed, see EnsureSplashFade).
    private readonly Dictionary<ShaderMaterial, ShaderMaterial?> _splashFadeTwins = new();
    // The splash column's flipbook material, once registered with the shared TextureCycler, a
    // set rather than a bool since the gun and HE splash defs build different underlying
    // materials (see SplashFlipbookTextures).
    private readonly HashSet<Material> _splashFlipbookRegistered = new();
    // Gun-impact effect throttle: effect name → the sim time it last played. Keyed by name,
    // which is exactly "per firing group", a group's rounds all carry one weapon and one
    // `<caliber><ammo>_gunhit`. Advanced by SimStep, so it follows the sim clock like everything
    // else here and a `--det` run throttles identically.
    private readonly Dictionary<string, float> _gunEffectAt = new();

    // Reused each step (no per-round alloc). Sees world AND aircraft bodies; the shooter's own
    // body is excluded per shot (ExcludeFor), set AND reset around every query, a leaked Exclude
    // would silently shield the next round's target.
    private readonly PhysicsRayQueryParameters3D _ray = new() { CollisionMask = CollisionLayers.WorldAndAircraft };
    // The flying aircraft bodies rounds can strike, one per rig (FlightController._Ready
    // registers), how a round's shooter id resolves to the one body its hit ray must exclude.
    private readonly List<AircraftBody> _aircraft = new();
    private readonly List<TurretController> _worldTurrets = new();
    private readonly SphereShape3D _proximitySphere = new();
    // The destructible blast sphere stays world-masked on purpose: planes never enter the
    // destructible DamageSink. The aircraft halves of fuse and blast run off the registered
    // `_aircraft` list instead (ProximityFuseTriggered / GatherAircraftCandidates), never this query.
    private readonly PhysicsShapeQueryParameters3D _proximityQuery = new() { CollisionMask = CollisionLayers.World };
    // The splash occlusion ray (C11): world geometry is cover, aircraft are not. The original casts
    // through its whole intersect database; whether aircraft nodes are in it was not settled, and
    // our hulls live on their own query layer, so a plane between a burst and its victim shields
    // nothing here.
    private readonly PhysicsRayQueryParameters3D _coverRay = new() { CollisionMask = CollisionLayers.World };
    // The cover ray's one-entry exclusion, reused across casts rather than built per candidate.
    private readonly Godot.Collections.Array<Rid> _coverExclude = new();
    private readonly List<BlastCandidate> _blastCandidates = new();
    // The world objects one burst has already dealt a share to, by instance id (Godot object
    // identity is a native pointer, so the id is the safe key). Reset per burst.
    private readonly HashSet<ulong> _blastGroups = new();
    // Local bounds per collision shape (BlastCentre), read once off the physics server: a chapter
    // trimesh's face array is large and shared across every instance of its mesh.
    private readonly Dictionary<Rid, Aabb> _shapeBounds = new();
    // The choker clouds the TANGLER hook leaves at each burst (FUN_004b94e0's list, DAT_0071db9c),
    // each choking every aircraft inside its radius for its TIME. Stepped by SimStep after the rounds.
    private readonly List<TanglerCloud> _tanglerClouds = new();
    private readonly List<AudioStreamPlayer> _sfxPool = new();
    // The stand-in fireball's sprite scatter and the muzzle-flash roll (gun dispersion itself
    // was removed). Held rather than resolved per draw.
    private readonly RandomNumberGenerator _rng = Rng.Stream(Rng.Weapons);
    private readonly HashSet<string> _flyoutLogged = new();
    // One MultiMesh per muzzle-flash ammo texture (MuzzleAmmoTextures), built in _Ready.
    private readonly MultiMesh[] _muzzleMm = new MultiMesh[MuzzleAmmoTextures.Length];
    // One MultiMesh per tracer texture (TracerTextures), built in _Ready; one shared per-mesh
    // instance-count scratch array, cleared and refilled every frame in RenderTracers.
    private readonly MultiMesh[] _tracerMm = new MultiMesh[TracerTextures.Length];
    private readonly int[] _tracerCounts = new int[TracerTextures.Length];
    // The tip discs, index-parallel to the streak pools above: one instance per round, so one
    // shared count array serves both and _tracerCounts is reused for the tip pools too.
    private readonly MultiMesh[] _tipMm = new MultiMesh[TipTextures.Length];
    // The per-round working set behind the TracerMinPixels floor: one sample per bound viewer,
    // cleared and refilled per round (distance is per round), so the floor allocates nothing
    // after the first frame.
    private readonly List<ScreenSize.ViewerSample> _viewerScratch = new();

    private WorldLights? _pointLights;         // the session's point set, null until BindPointLights
    private int _projHigh;                     // highest slot ever used (bounds the scan)
    private Node3D _flyoutModels = null!;   // container for the live rocket-body instances
    private Node3D _impactFxModels = null!;  // container for the short-lived impact-effect instances
    private Node3D _casingModels = null!;    // container for the pooled shell-casing instances
    private MultiMesh _impactMm = null!;
    private MultiMesh _smokeMm = null!;
    private int _sfxNext;
    private bool _flyoutPoseLogged;
    private int _muzzleBasisLogs;
    private int _muzzleLightLogs;
    private int _impactsLogged;
    private int _soundGainsLogged;               // D31 one-shot gain breadcrumb, first 8
    private int _decaysLogged;                   // launch-velocity decay breadcrumb, first 4
    private int _armedLogged;                    // RANGE_MINIMUM intersect-bit breadcrumb, first 2
    private int _flyoutDestroysLogged;           // shot-down flyout breadcrumb, first 2
    private int _disablingLogged;                // sonic/flash victim breadcrumb, first 8
    private float _simClock;                   // sim seconds since the pool started (the gun-effect throttle)
    private CasingSpec? _casingSpec;           // the gunshell OBJECT_MOTION, resolved once
    private bool _casingSpecResolved;
    private GameZNode? _casingProto;           // the gunshell gamez prototype, resolved once
    private bool _casingProtoResolved;
    private bool _casingLogged;

    public ProjectilePool(TextureArchive textures, SoundArchive? sounds,
        IReadOnlyDictionary<string, SoundDef>? soundDefs,
        GameZ? flyoutGamez = null, SceneBuilder? flyoutScene = null, AnimProgram? flyoutAnims = null,
        IReadOnlyDictionary<string, SoundGroup>? soundGroups = null,
        Effects.EffectAmbience? ambience = null)
    {
        _ambience = ambience ?? Effects.EffectAmbience.Still;
        _textures = textures;
        _sounds = sounds;
        _soundDefs = soundDefs;
        _soundGroups = soundGroups;
        _flyoutGamez = flyoutGamez;
        _flyoutScene = flyoutScene;
        _flyoutAnims = flyoutAnims;
        Name = "projectiles";
        ProcessPriority = DrawPriority;
    }

    /// <summary>One of this session's gunners, carried or emplaced, has acquired a human player
    /// and has line of sight to it, once per acquisition episode
    /// (<see cref="TurretController"/>). The pool is the seam every gunner already holds and the
    /// one list both turret families reach, so a session-wide listener subscribes once here
    /// instead of per gunner as each is built; <c>Session.Roster.AiVoiceRuntime</c> broadcasts
    /// <c>WA-Turret</c> on it.</summary>
    public event Action<TurretController, FlightController>? TurretAcquiredPlayer;

    /// <summary>Every camera that can see this pool's tracers, feeding the
    /// <see cref="TracerMinPixels"/> distance floor, a screen-space rule applied to one shared
    /// world-space mesh, so it takes the NEAREST bound viewer. Unbound (weapon bench, suite
    /// labs) means no floor.
    /// ⚠ Bind every pane, never player 1 alone: that floored every round against P1's distance and
    /// inflated it in every other pane too. See <see cref="TracerFloor"/>.</summary>
    public ViewerSet Viewers { get; set; } = new();

    /// <summary>Splitscreen's overall gain for this pool's one-shots, the same equal-power figure
    /// (1 for 1P, 1/√N for N, <c>GameSession.mixGain</c>) <see cref="Audio.FlightAudio.MixGain"/> already
    /// applies to a plane's own-ship loops, so N simultaneous firefights don't sum to a wall of
    /// noise either.</summary>
    public float MixGain { get; set; } = 1f;

    /// <summary>The nearest-human seam (<c>GameSession.PlayerPositionsSnapshot</c>, C21's
    /// <c>PLAYER_RANGE</c> seam), read here so a gun/rocket one-shot's distance term
    /// answers "how far is this from the nearest pilot", not player 1's alone. Null
    /// outside a real session (the weapon bench, suite labs), where the distance term is
    /// skipped entirely rather than guessing a listener.</summary>
    public Func<IReadOnlyList<Vector3>>? PlayerPositions { get; set; }

    /// <summary>The anim data's <c>PLAYER_1ST_PERSON</c> condition (<c>GameSession.AnyPilotFirstPerson</c>,
    /// the same closure <c>AnimRuntime.FirstPersonView</c> takes), read per shot because the view
    /// cycle key changes it mid-burst. It picks <c>muzzle_burst</c>'s <c>testfp</c> branch: the two
    /// big first-person lights that light the cockpit, or the small third-person one. Null outside a
    /// session (the weapon bench, suite labs), where the third-person light is the answer.</summary>
    public Func<bool>? FirstPersonView { get; set; }

    /// <summary>Whether the pilot with this shooter id has the full Cockpit view (mode 6) on
    /// screen. That view draws no muzzle flash quads for that pilot's own guns. Per shooter, not
    /// per session: an aeroplane ahead still flashes while the player sits in the canopy. Nose
    /// (mode 7) is not covered, and a null closure (the weapon bench, the suite labs) draws every
    /// flash.</summary>
    public Func<int, bool>? CockpitViewOfPilot { get; set; }

    /// <summary>The impact sprites alive this step (spark, explosion stand-in), for a suite that
    /// asserts a hit drew none of its own.</summary>
    public int ImpactSpriteCount => _impact.Count;

    /// <summary>The session's surface hulls, so <see cref="CollectVehicleList"/> can offer the
    /// whole of the engine's <c>VehicleList</c> rather than its aircraft half. Null in every build
    /// with no hulls (the weapon lab, the suite labs, a bare stage), where the vehicle list is the
    /// aircraft roster and nothing else.</summary>
    public ISurfaceVehicles? SurfaceVehicles { get; set; }

    /// <summary>The session's destructibles, CSVM's stand-in for the engine's mission-structure
    /// pool, so a scan that wants that pool asks here rather than carrying its own reference.
    /// Null in a build with no world (the weapon lab, the suite labs).</summary>
    public Mech3.DestructibleRegistry? Structures { get; set; }

    /// <summary>Instant Action's wrap-up "Shot %" (docs/formats/instant-action.md "What the four
    /// numbers count") counts a cannon round fired/hit only for <c>the local player</c>; a shooter
    /// id here is that filter generalised to every human pilot for splitscreen, empty outside
    /// Instant Action. <see cref="CannonRoundsFired"/>/<see cref="CannonHits"/> are the two
    /// counters, both filtered on <see cref="WeaponDef.IsCannon"/>.</summary>
    public HashSet<int> ScoredShooters { get; } = new();

    /// <summary>Cannon rounds a scored shooter fired that actually created a round (the pool was not
    /// full), the decode's denominator, <c>FUN_004b6820</c>'s per-station fire loop.</summary>
    public int CannonRoundsFired { get; private set; }

    /// <summary>Cannon rounds a scored shooter hit something with, the decode's numerator, summed
    /// over its three hit sites (<see cref="Impact"/> is CSVM's single choke point for all three: a
    /// cannon round never reaches the rocket-only fuse/range-expiry arms below).</summary>
    public int CannonHits { get; private set; }

    /// <summary>Whether an authored effect radius is also a positive-health damage blast, the
    /// weapon-level spelling of <see cref="ImpactOutcome.HasBlastDamage"/>, where the rule lives.
    /// The surface never changes the answer, so any resolves it.</summary>
    public static bool HasBlastDamage(WeaponDef weapon) =>
        ImpactOutcome.Resolve(weapon, SurfaceRegistry.Default, modelResolved: false, hasEffectsRuntime: true)
            .HasBlastDamage;

    /// <summary>Whether the weapon authors <c>LOCK_ON</c> (weapon <c>+0x74</c> bit <c>0x8000</c>).
    /// The key does three jobs and lock acquisition is none of them
    /// (docs/org/ordnanceTypes.md): it decides whether a MOTOR round inherits its launcher's
    /// velocity as a vector, it is the window that inheritance decays over, and it is the guidance
    /// ramp's denominator. No <c>CANNON</c> in this install authors it; ten of the twelve ordnance
    /// types do.</summary>
    public static bool CarriesLockOn(WeaponDef weapon) => weapon.LockOn is > 0f;

    /// <summary>The steering step's gate (<c>FUN_005af720</c> → <c>FUN_005af960</c>): the weapon
    /// carries <c>LOCK_ON</c> AND the round holds a target. A round failing either is never turned
    /// (<see cref="Steer"/>), whatever its <c>TURN_RATE</c> says: the gate is on the flag, and the
    /// 0.001 sentinel every dumbfire type authors only makes a gated round turn imperceptibly.
    /// ⚠ The inherited-velocity decay is deliberately NOT hung on this predicate here, although
    /// the original runs it inside the same step: <see cref="InheritedFraction"/>.</summary>
    public static bool SteeringStepRuns(WeaponDef weapon, bool hasTarget) =>
        CarriesLockOn(weapon) && hasTarget;

    /// <summary>The steering step's per-frame speed penalty (<c>FUN_005af960</c>): the round's own
    /// speed times <c>0.8 + 0.2·cos(turned)</c>, where <paramref name="turnedRad"/> is the angle the
    /// heading actually swung THIS frame (the clamp when the turn was clamped, the whole angle when
    /// it snapped). Applied on every steering frame the angle was above zero, never once per turn,
    /// so the loss over a whole turn scales with the frame's turn authority: at 60 fps the seeker's
    /// 1.25 rad/s costs about 0.3% over a 90° turn, and a coarser step costs more.</summary>
    public static float TurnPenaltyFactor(float turnedRad) =>
        TurnPenaltyFloor + TurnPenaltyCosWeight * Mathf.Cos(turnedRad);

    /// <summary>The heading's turn authority for one frame, radians:
    /// <c>TURN_RATE × dt × ramp</c>, times the engine's per-shot turn scalar. The ramp is
    /// <c>TURN_SUSPEND_TIME</c>'s: zero until that age, then rising over <c>LOCK_ON</c>; no shipped
    /// entry authors the key, so the term is <see cref="TurnRampUnsuspended"/> from the first
    /// frame and is kept as a term so the formula reads as the routine's.</summary>
    public static float MaxTurnRad(WeaponDef weapon, float dt) =>
        TurnRateScale * (weapon.TurnRate ?? 0f) * dt * TurnRampUnsuspended;

    /// <summary>Whether a round that reaches <c>RANGE</c> detonates on its way out or simply
    /// vanishes (<c>FUN_005afd50</c>, the branch at <c>LAB_005b02ba</c> against
    /// <c>LAB_005b0318</c>). The original's rule is <c>LOCK_ON</c> and not <c>EXPIRES</c>, or
    /// <c>DETONATE_AT_RANGE</c>; this install authors neither of those two keys, so carrying
    /// <c>LOCK_ON</c> is the whole rule and the choker, the cannonball and the fake weapon expire
    /// silently.</summary>
    public static bool DetonatesAtRange(WeaponDef weapon) => CarriesLockOn(weapon);

    /// <summary>Whether the round leaves with its intersect bit clear, so nothing can strike it
    /// until it has flown <c>RANGE_MINIMUM</c> metres, <c>FLYOUT_HEALTH</c> and
    /// <c>RANGE_MINIMUM</c> both present, which the torpedo alone is (<c>FUN_005aef40</c> clears
    /// node flag <c>0x10</c>, <c>FUN_005afd50</c> at <c>0x005b01c4</c> sets it). The round is drawn
    /// from its first frame; the key hides nothing and arms nothing.</summary>
    public static bool FlyoutUnhittableAtLaunch(WeaponDef weapon) =>
        weapon.FlyoutHealth is > 0 && weapon.RangeMinimum is > 0f;

    /// <summary>Seeds one round's shootable-flyout state, or null for a round that is neither
    /// shootable nor targetable (every gun round, and every ordnance type but the torpedo), which
    /// is what keeps this allocation off the hot path. <paramref name="slot"/> only names the
    /// round for <c>--target=</c>.</summary>
    public static Flyout? SeedFlyout(WeaponDef weapon, int slot) =>
        weapon.FlyoutHealth is > 0 || weapon.Targetable ? new Flyout(weapon, slot) : null;

    /// <summary>The splash share at a squared surface distance: <c>1 − d² / IMPACT_PROXIMITY²</c>
    /// (<c>FUN_005acac0</c>, docs/org/ordnanceTypes.md "Half two, the splash"), applied to both
    /// damage pools. Quadratic in distance, so 0.75 at half the radius where a linear curve gives
    /// 0.5. <paramref name="radiusSq"/> is <see cref="WeaponDef.ImpactProximitySqM"/>, the square
    /// the engine stores at weapon <c>+0x40</c>; a burst that engulfs its target passes 0.</summary>
    public static float BlastFalloff(float distanceSq, float radiusSq) =>
        radiusSq > 0f ? Mathf.Clamp(1f - distanceSq / radiusSq, 0f, 1f) : 0f;

    /// <summary>The same curve in plain metres, for a caller holding a distance rather than its
    /// square: full at the surface, zero at the authored radius.</summary>
    public static float BlastDamage(float fullDamage, float radius, float distance) =>
        fullDamage * BlastFalloff(distance * distance, radius * radius);

    /// <summary>Whether this weapon is one of the four types whose hit-side branch zeroes the damage
    /// pair for an aircraft victim (<c>SONIC</c>, <c>FLASH</c>, <c>BEEPER</c>, <c>TANGLER</c>): its
    /// authored figures never reach a plane's ledger, whatever they say. World bodies are not under
    /// that branch and still take the (authored-zero) pair.</summary>
    public static bool AircraftDamageDiscarded(WeaponDef weapon) =>
        weapon.Sonic || weapon.Flash || weapon.BeeperTime != null || weapon.Tangler != null;

    /// <summary>The <c>SURFACE_ANIMATION</c> orientation: the shortest rotation taking world up onto
    /// the struck surface's normal, which is what <c>FUN_005ac7a0</c> builds from the hit record
    /// (<c>FUN_0053fd40</c> from <c>(0,1,0)</c>) before spawning that slot. Identity on flat ground,
    /// so the rule shows only on a slope, and identity for a burst with no surface (a fuse, the range
    /// expiry). A normal straight down turns half a turn about X.</summary>
    public static Basis SurfaceUpBasis(Vector3 normal) => ShortestArc(Vector3.Up, normal);

    /// <summary>Enhanced Graphics only: the basis the impact burst's upper ring is placed with.
    /// The ring's own disc normal (<see cref="UpperRingDiscNormal"/>, not world up) is rotated onto
    /// the reverse of the round's flight direction. The ring then faces back up the path the rocket
    /// came down. Null in the faithful presentation and for a round with no usable velocity. It
    /// leaves the ring on the fixed axis the original spawns it on (docs/org/ordnanceTypes.md,
    /// "The upper ring in Enhanced Graphics").</summary>
    public static Basis? UpperRingOrient(Vector3 velocity) =>
        GraphicsMode.Enhanced && velocity.LengthSquared() > 1e-6f
            ? ShortestArc(UpperRingDiscNormal, -velocity.Normalized()) : null;

    /// <summary>The shortest rotation taking unit <paramref name="from"/> onto <paramref name="to"/>;
    /// a zero <paramref name="to"/> is identity, and an opposite one is half a turn about an axis
    /// perpendicular to <paramref name="from"/>.</summary>
    public static Basis ShortestArc(Vector3 from, Vector3 to)
    {
        if (to.LengthSquared() < 1e-6f)
            return Basis.Identity;
        to = to.Normalized();
        float dot = from.Dot(to);
        if (dot > 1f - 1e-6f)
            return Basis.Identity;
        if (dot < -1f + 1e-6f)
            return new Basis(Mathf.Abs(from.X) < 0.9f ? Vector3.Right : Vector3.Up, Mathf.Pi);
        var axis = from.Cross(to).Normalized();
        return new Basis(axis, Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)));
    }

    /// <summary>The <c>DETONATION_DOT_PRODUCT</c> gate: unit(round - candidate) dotted with the
    /// candidate's backward axis must reach the threshold. A positive threshold therefore admits a
    /// round behind the candidate, whatever way the round flies. A zero offset stays zero and dots
    /// to 0, as the original's normalise leaves it (docs/org/ordnanceTypes.md "The proximity
    /// fuse").</summary>
    public static bool FuseDotAllows(float? minimumDot, Vector3 candidateBackward, Vector3 roundFromCandidate)
    {
        if (minimumDot is null)
            return true;
        var dir = roundFromCandidate.LengthSquared() > 0f ? roundFromCandidate.Normalized() : Vector3.Zero;
        return dir.Dot(candidateBackward) >= minimumDot.Value;
    }

    /// <summary>A fuse candidate's <c>+0x198</c> row, m[2], which is minus the nose: Godot's +Z
    /// basis column, unit length.</summary>
    public static Vector3 BackwardAxis(Node3D body) => body.GlobalBasis.Z.Normalized();
    /// <summary>Distance from point <paramref name="p"/> to the segment <paramref name="a"/>→
    /// <paramref name="b"/>. The fuse's cheap reject wants a round's whole step, not its endpoints:
    /// a gun round covers ~8 m per 60 Hz frame.</summary>
    public static float SegmentPointDistance(Vector3 a, Vector3 b, Vector3 p) =>
        (p - (a + (b - a) * SegmentClosestFraction(a, b, p))).Length();

    /// <summary>The fraction along <paramref name="a"/>→<paramref name="b"/> of the segment point
    /// nearest <paramref name="p"/>, clamped to the segment; 0 for a degenerate one.</summary>
    public static float SegmentClosestFraction(Vector3 a, Vector3 b, Vector3 p)
    {
        var seg = b - a;
        float lenSq = seg.LengthSquared();
        return lenSq <= 1e-12f ? 0f : Mathf.Clamp((p - a).Dot(seg) / lenSq, 0f, 1f);
    }

    /// <summary>The struck collider's numeric surface id, the index the weapon's <c>IMPACT</c>
    /// table is read at (<see cref="ImpactOutcome.Resolve"/>), off the same
    /// <see cref="SceneBuilder.SurfaceIdMeta"/> tag the crash and graze cascades read. An
    /// untagged collider answers <c>default</c>(0); a struck aircraft answers <c>player</c>(6).
    /// See docs/org/weaponImpact.md for the decode behind both arms.</summary>
    public static int SurfaceIdOf(Node? collider)
    {
        if (collider is AircraftBody)
            return SurfaceRegistry.Player;
        return collider != null && collider.HasMeta(SceneBuilder.SurfaceIdMeta)
            ? collider.GetMeta(SceneBuilder.SurfaceIdMeta).AsInt32()
            : SurfaceRegistry.Default;
    }

    /// <summary>Whether a struck collider is water, <c>water</c>(1) and nothing else. Its own read
    /// of the surface id rather than a table lookup, which is what the original does on the impact
    /// path (<c>FUN_005ad330</c> tests <c>*(material + 0x20) == 1</c> beside the IMPACT row it
    /// already resolved). The world runtime's <c>SurfaceIsWater</c> hook and the spark tint are
    /// bound to this, so a landing piece, a round and a wingtip cannot disagree about the sea.</summary>
    public static bool SurfaceIsWater(Node? collider) => SurfaceIdOf(collider) == SurfaceRegistry.Water;

    /// <summary>A one-shot's distance term: linear falloff from 1 at <paramref name="rangeMin"/>
    /// to 0 at <paramref name="rangeMax"/>, the same [full-volume, audible] pair
    /// <see cref="WorldSounds"/> reads for its positional emitters. <paramref name="distance"/> is
    /// measured to the NEAREST human, not the nearest pane camera. TUNE: linear rather than
    /// Godot's inverse-distance curve, since these stay plain <c>AudioStreamPlayer</c>s. Static so
    /// <c>FlightAudio</c>'s one-shots reuse this term too.</summary>
    public static float DistanceGain(float distance, float rangeMin, float rangeMax)
    {
        if (distance >= float.MaxValue)
            return 1f;
        if (rangeMax <= rangeMin)
            return distance <= rangeMax ? 1f : 0f;
        return Mathf.Clamp(1f - (distance - rangeMin) / (rangeMax - rangeMin), 0f, 1f);
    }

    /// <summary>Registers a flying aircraft's body as a strikeable target: rounds from every
    /// OTHER identity can hit it, and this plane's own rounds exclude it per shot (the body's
    /// <see cref="AircraftBody.PlayerIndex"/> is matched against each round's shooter id).</summary>
    public void RegisterAircraft(AircraftBody body) => _aircraft.Add(body);

    /// <summary>The registered aircraft that fired a round carrying <paramref name="shooterId"/>, or
    /// null for an unowned round (<see cref="NoShooter"/>) or a plane no longer registered. Shooter
    /// ids ARE unique across a session (a human's is its pane index, an AI's is
    /// <c>FlightRoster.ShooterIdBase + n</c>), so this resolves one plane, not a class of
    /// them.</summary>
    public FlightController? RigOfShooter(int shooterId)
    {
        if (shooterId < 0)
        {
            return null;
        }

        foreach (var body in _aircraft)
        {
            if (body.Rig.PlayerIndex == shooterId)
            {
                return body.Rig;
            }
        }

        return null;
    }

    /// <summary>Appends this pool's live rounds to the engine's fourth candidate list: a round is on
    /// it when its def carries a fuse longer than <see cref="AimAssist.MinFuseDistance"/> OR
    /// <c>TARGETABLE</c>, which are <c>FUN_00441830</c>'s two independent reasons to wrap a round.
    /// Reads the round's own <c>Team</c>, stamped once at <see cref="Spawn"/>; a round nobody owns
    /// lands on <see cref="AimAssist.NeutralTeam"/> and is rejected by the scorer's team gate.</summary>
    public void CollectFusedOrdnance(AimCandidateSet into)
    {
        for (int i = 0; i < _proj.Length; i++)
        {
            ref var p = ref _proj[i];
            if (!p.Alive || (p.Weapon.DetonationDistance is not > AimAssist.MinFuseDistance
                             && !p.Weapon.Targetable))
            {
                continue;
            }
            // The SOURCE is the round's flyout state, present only for a TARGETABLE round: it is
            // the identity the player's target registry holds a selection by, and a merely fused
            // round hands null, which is the admission byte clear (FUN_00441830).
            into.AddOrdnance(p.Pos, WorldVelocity(in p), p.Team,
                source: p.Shootable is { Targetable: true } ? p.Shootable : null);
        }
    }

    /// <summary>Appends every live round's flyout state, in slot order, skipping a round carrying
    /// neither <c>FLYOUT_HEALTH</c> nor <c>TARGETABLE</c>. The seam a scripted run reads the health
    /// pair and the admission byte through, since both are otherwise invisible from outside the
    /// round.</summary>
    public void CollectFlyouts(List<Flyout> into)
    {
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (p.Alive && p.Shootable is { } f)
            {
                into.Add(f);
            }
        }
    }

    /// <summary>Appends every live round's position and world velocity, in slot order. The seam a
    /// scripted run samples a round's speed through, so an assertion about how fast a round is
    /// flying reads the same number the integrator moved it by rather than a second derivation of
    /// it. Adds nothing when no round is alive.</summary>
    public void CollectLiveRounds(List<(Vector3 Pos, Vector3 Velocity)> into)
    {
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (p.Alive)
            {
                into.Add((p.Pos, WorldVelocity(in p)));
            }
        }
    }

    /// <summary>Appends every live round's path length so far and whether the
    /// <c>RANGE_MINIMUM</c> gate is still holding its intersect bit clear, in slot order. The seam
    /// a scripted run reads the gate through without firing at the round.</summary>
    public void CollectFlyoutIntersect(List<(float Travelled, bool Unhittable)> into)
    {
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (p.Alive)
            {
                into.Add((p.Travelled, p.IntersectOff));
            }
        }
    }

    /// <summary>Appends every live round's <c>FLYOUT</c> body, in slot order, skipping rounds
    /// flying without one. The seam a scripted run reads the launch look through: which of the
    /// body's nodes the running <c>MODEL_ANIMATION</c> has shown or hidden.</summary>
    public void CollectFlyoutBodies(List<Node3D> into)
    {
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (p.Alive && p.Model != null)
            {
                into.Add(p.Model);
            }
        }
    }

    /// <summary>Appends every live round's held target, in slot order (null for a round holding
    /// none). The seam a scripted run reads a seeker's per-frame pick through, since the slot is
    /// otherwise invisible from outside the round.</summary>
    public void CollectHeldTargets(List<object?> into)
    {
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (p.Alive)
            {
                into.Add(p.Target);
            }
        }
    }

    /// <summary>Appends the live choker clouds as (centre, seconds left), in spawn order: the seam a
    /// scripted run reads the choker's cloud through, since the pool holds it privately.</summary>
    public void CollectTanglerClouds(List<(Vector3 Centre, float Remaining)> into)
    {
        foreach (var cloud in _tanglerClouds)
            into.Add((cloud.Centre, cloud.Remaining));
    }

    /// <summary>Appends every registered aircraft to the assist's candidate set, reading the one
    /// live roster this pool already keeps for the hit ray and the fuse rather than a second list
    /// that could drift. Reads each rig's own <see cref="FlightController.Team"/>. A crashed or
    /// INERT pilot (<see cref="FlightController.InPlay"/>) is present but not live; the shooter
    /// excludes itself through <see cref="AimScan.Self"/>.</summary>
    public void CollectAircraft(AimCandidateSet into)
    {
        foreach (var body in _aircraft)
        {
            var rig = body.Rig;
            into.AddVehicle(rig.WorldPosition, rig.WorldVelocity, rig.Team, rig.InPlay, rig);
        }
    }

    /// <summary>Appends the whole of the engine's first candidate pool: the aircraft roster and,
    /// where a session has one, its surface hulls. The engine keeps one <c>VehicleList</c> holding
    /// "aircraft and AI ground/sea vehicles" (docs/org/aim-assist.md "The four lists") and its
    /// shared target picker walks that one list, so a scan that wants the pool rather than the
    /// aircraft half asks here and cannot drift from the half the player's own scan reads.</summary>
    public void CollectVehicleList(AimCandidateSet into)
    {
        CollectAircraft(into);
        SurfaceVehicles?.CollectVehicles(into);
    }

    /// <summary>Appends the engine's third candidate pool, the mission structures, where the
    /// session has any. The pool is offered whole and the team gate on the far side decides: a
    /// structure the data leaves unowned is neutral and nobody's target, exactly as it reaches the
    /// player's own scan (docs/org/targeting.md "What a turret's candidate set holds").</summary>
    public void CollectMissionStructures(AimCandidateSet into)
    {
        if (Structures != null)
        {
            into.AddStructures(Structures);
        }
    }

    /// <summary>Appends every registered aircraft's carried turrets and every world emplacement to
    /// the assist's candidate set. A carried turret rides its host's velocity and team; the host's
    /// own scan rejects it through that team gate, never through Self. An emplacement stays listed
    /// while dormant, a sleeping AA gun is still lockable; only its death delists it.</summary>
    public void CollectTurrets(AimCandidateSet into)
    {
        foreach (var body in _aircraft)
        {
            var rig = body.Rig;
            foreach (var turret in rig.Turrets)
            {
                into.AddTurret(turret.WorldPosition, rig.WorldVelocity, rig.Team, turret.Alive, turret);
            }
        }
        foreach (var turret in _worldTurrets)
        {
            into.AddTurret(turret.WorldPosition, turret.PlatformVelocity,
                turret.Team, turret.Alive, turret);
        }
    }

    /// <summary>Registers the session's world emplacements for
    /// <see cref="CollectTurrets"/>, the same one-live-roster rule as the aircraft list.</summary>
    public void RegisterWorldTurrets(IReadOnlyList<TurretController> turrets) =>
        _worldTurrets.AddRange(turrets);

    /// <summary>The muzzle flashes lit this frame, as world position, range, colour and energy.
    /// A second world drawing the cockpit interior (<see cref="CockpitOverlay"/>) has no view of
    /// these nodes and mirrors them from this list instead. The position is resolved off the firing
    /// muzzle at the moment of the call, never read back off the pooled light. The caller is a
    /// pilot's own frame callback, which runs before this pool places them.</summary>
    public IEnumerable<(Vector3 Position, float Range, Color Color, float Energy)> ActiveMuzzleLights()
    {
        foreach (var l in _lights)
        {
            if (l.InUse && l.Light.Visible)
            {
                var at = MuzzleFrame(l, out var world) ? world : l.Light.GlobalPosition;
                yield return (at, l.Light.OmniRange, l.Light.LightColor, l.Light.LightEnergy);
            }
        }
    }

    /// <summary>Routes the first-person muzzle pair into <paramref name="lights"/>, the session's
    /// point-light set, in original mode. The world and the aircraft, the cockpit interior
    /// included, then take it as the per-vertex point term rather than an
    /// <see cref="OmniLight3D"/>.</summary>
    public void BindPointLights(WorldLights lights)
    {
        _pointLights = lights;
        lights.AddSource(SubmitPointFlashes);
    }

    /// <summary>The first-person muzzle lights the point term holds now: world position, near and
    /// far range and colour. Diagnostics for the suite; the shaders read the packed set.</summary>
    public IEnumerable<(Vector3 Position, float Near, float Far, Color Color)> PendingPointFlashes()
    {
        foreach (var f in _pointFlashes)
            yield return (AnchorPoint(f.Anchor, f.Local, out var at) ? at : f.At, f.Near, f.Far, f.Color);
    }

    public override void _Ready()
    {
        // Tracers are velocity-aligned streaks, not billboarded, or a long streak would collapse
        // into a screen-vertical bar. One MultiMesh per texture; the streak mesh is the authored
        // crossed pair (docs/org/tracers.md), so it reads solid without consulting the camera.
        for (int i = 0; i < TracerTextures.Length; i++)
            _tracerMm[i] = AddMultiMesh(TracerTextures[i], MaxProjectiles, additive: true, billboard: false, out _, CrossedStreakMesh());
        // The tip discs. Carried on a plain quad rather than the authored octagon: the texture is a
        // radial disc with its own alpha, so the carrier's corners only matter if the art's corners
        // are opaque, swap in a real 8-gon here if they turn out to be.
        for (int i = 0; i < TipTextures.Length; i++)
            _tipMm[i] = AddMultiMesh(TipTextures[i], MaxProjectiles, additive: true, billboard: false, out _);
        for (int i = 0; i < MuzzleAmmoTextures.Length; i++)
            _muzzleMm[i] = AddMultiMesh(MuzzleAmmoTextures[i], MaxFlashes, additive: true, billboard: false, out _);
        _impactMm = AddMultiMesh("slug_muzzle2", MaxFlashes, additive: true, billboard: false, out _);
        // Smoke (the muzzlepuffer puffs): the authored puffer textures (smoke101), alpha-blended
        // rather than additive so the puffs read as smoke.
        _smokeMm = AddMultiMesh("smoke101", MaxSmoke, additive: false, billboard: false, out _);
        _flyoutModels = new Node3D { Name = "flyout" };
        AddChild(_flyoutModels);
        _impactFxModels = new Node3D { Name = "impact_fx" };
        AddChild(_impactFxModels);
        _casingModels = new Node3D { Name = "casings" };
        AddChild(_casingModels);
        for (int i = 0; i < 8; i++)
        {
            // The bus is set here and never per shot: the pool is built once and every player in it
            // is handed back and reused, so a per-shot write would be the same value every time.
            var p = new AudioStreamPlayer { Bus = AudioBuses.Effects };
            AddChild(p);
            _sfxPool.Add(p);
        }
    }

    /// <summary>Fires one round of <paramref name="weapon"/> from the muzzle transform, along
    /// <paramref name="aimDir"/> or the muzzle axis, carrying what <see cref="InheritedAtLaunch"/>
    /// allows of <paramref name="inheritVel"/>. Drops the round silently if the pool is full.
    /// <paramref name="shooterId"/> is the identity the hit ray excludes, <paramref name="team"/>
    /// stamps the round once, <paramref name="target"/> is what it holds, and <paramref name="ownerBodies"/>
    /// is a world gunner's own mount, which its rounds neither strike nor splash (org/ordnanceTypes.md).</summary>
    public void Spawn(WeaponDef weapon, Transform3D muzzle, Vector3 inheritVel, int shooterId = NoShooter,
        Node3D? muzzleAnchor = null, Vector3? aimDir = null, int? team = null, object? target = null,
        Godot.Collections.Array<Rid>? ownerBodies = null)
    {
        // The launch bark: only rockets/ordnance carry a FIRE.SOUND, every cannon's is
        // null in the data (LOOPED_SOUND_NAME covers continuous gunfire instead), so this is a
        // one-shot with no double-up risk.
        if (weapon.Fire?.Sound is { } fireSnd)
            PlaySound(fireSnd, muzzle.Origin);
        // ⚠ aimDir is a value the CALLER computed (the gun path's aim-assist vector). Never
        // re-derive it here, so a networking milestone can feed a received vector and get the
        // shooter's own answer rather than a locally re-run scan that would diverge.
        var forward = aimDir is { } aim && aim.LengthSquared() > 1e-12f
            ? aim.Normalized()
            : -muzzle.Basis.Z.Normalized();
        float speed = weapon.Velocity ?? DefaultVelocity;
        float accel = weapon.Acceleration ?? 0f;
        // Rockets only: scale launch velocity and acceleration by the same dev factor. Guns stay
        // byte-identical (the impact reticle reads weapon.Velocity separately), and default 1.0
        // leaves rocket flight unchanged.
        if (weapon.IsRocket)
        {
            float scale = Config.GetFloat("weapons.rocketSpeedScale", RocketSpeedScale);
            speed *= scale;
            accel *= scale;
        }
        // A motor round leaves at the LAUNCHER's speed and climbs to VELOCITY above it; one without
        // ACCELERATION is seeded at its cap and stays there (Ballistics.LaunchSpeed). The launcher
        // term is the raw vector, not InheritedAtLaunch: the original reads it before the LOCK_ON gate.
        float cap;
        (speed, cap) = Ballistics.LaunchSpeed(speed, accel, inheritVel.Length());
        var tint = weapon.IsRocket ? RocketTint : SlugTint;
        // TracerTextures/TipTextures index, reusing MuzzleAmmoIndex; unused on a rocket, since
        // RenderTracers draws no streak for ordnance.
        int tracerIdx = MuzzleAmmoIndex(weapon);
        // Brightness is baked into the tint at spawn (weapons.tracerBrightness) rather than read
        // per frame, a round's tint stands for its whole life, same as everything else in Proj.
        float brightness = Config.GetFloat("weapons.tracerBrightness", TracerBrightness);
        var tracerTint = new Color(brightness, brightness, brightness);

        int slot = -1;
        for (int i = 0; i < MaxProjectiles; i++)
        {
            if (!_proj[i].Alive)
            {
                slot = i;
                break;
            }
        }
        if (slot >= 0)
        {
            // G14's fired-side counter: once per round actually created (this arm), never per
            // trigger pull, matching the decode's "only when a round is actually created".
            if (weapon.IsCannon && ScoredShooters.Contains(shooterId))
                CannonRoundsFired++;
            var inherited = InheritedAtLaunch(weapon, inheritVel);
            var vel = forward * speed;
            var model = weapon.IsRocket ? BuildFlyoutModel(weapon) : null;
            if (model != null)
            {
                // A motor round's first-frame speed is ~zero, so pose the body down the launch
                // direction rather than a velocity too short to normalise.
                var poseVel = vel + inherited;
                model.GlobalTransform = FlyoutPose(muzzle.Origin,
                    poseVel.LengthSquared() > 1e-6f ? poseVel : forward);
            }
            // The shootable half. The hittable box comes off the model because the engine's
            // hittable thing is the round's own scene node: a round flying streak-only (no FLYOUT
            // MODEL in this chapter's gamez) has no node to strike, so it gets no box either.
            var shootable = SeedFlyout(weapon, slot);
            var hitBox = shootable is { HealthMax: > 0f } && model != null
                ? ModelAabb(model) : new Aabb();
            // Team is stamped once here, never re-derived from shooterId on a later scan, so a
            // caller with a real FlightController.Team is answered faithfully for the round's
            // whole flight.
            _proj[slot] = new Proj
            {
                Alive = true,
                Pos = muzzle.Origin,
                PrevPos = muzzle.Origin,
                Vel = vel,
                Range = weapon.Range ?? DefaultRange,
                IntersectOff = FlyoutUnhittableAtLaunch(weapon),
                Accel = accel,
                Cap = cap,
                Grav = weapon.Gravity ?? 0f,
                Weapon = weapon,
                Tint = tracerTint,
                TracerIdx = tracerIdx,
                Model = model,
                Shootable = shootable,
                HitCentre = hitBox.GetCenter(),
                HitHalf = hitBox.Size * 0.5f,
                Shooter = shooterId,
                Team = team ?? AimAssist.TeamOfPilot(shooterId),
                Owner = ownerBodies is { Count: > 0 } ? ownerBodies : null,
                Inherited = inherited,
                Target = target,
            };
            if (slot >= _projHigh)
                _projHigh = slot + 1;
            // The def runs from the spawn frame on every ordnance round: its RESET_STATE is the
            // launch look and its t=0 events start the trail at the muzzle.
            if (weapon.IsRocket)
                StartFlyoutAnim(slot, muzzle.Origin, muzzle.Basis);
        }

        int ammoIdx = MuzzleAmmoIndex(weapon);
        var muzzleSprites = _muzzle[ammoIdx];
        // The original draws no muzzle flash on the pilot's own guns while the Cockpit view is on
        // the screen. The quads are skipped rather than drawn behind the canopy. The shot light
        // still fires: that is what lights the interior.
        bool ownCockpit = CockpitViewOfPilot?.Invoke(shooterId) == true;
        if (!ownCockpit && muzzleSprites.Count + MuzzleFlashCount <= MaxFlashes)
        {
            // The triad's base orientation is the muzzle's world basis, so it rolls with the
            // aircraft rather than a fixed world plane (a world-fixed flash was flown through at
            // speed, user-reported at the controls).
            var planeBasis = muzzle.Basis.Orthonormalized();
            bool anchored = muzzleAnchor != null;
            var invBasis = anchored ? planeBasis.Inverse() : Basis.Identity;
            float baseAngle = _rng.Randf() * Mathf.Tau;
            for (int i = 0; i < MuzzleFlashCount; i++)
            {
                float angle = baseAngle + i * (Mathf.Tau / MuzzleFlashCount);
                var orient = RollAroundNormal(planeBasis, angle);
                muzzleSprites.Add(new Sprite
                {
                    Pos = anchored ? Vector3.Zero : muzzle.Origin,
                    Orient = anchored ? invBasis * orient : orient,
                    Anchor = muzzleAnchor,
                    Life = MuzzleLife,
                    Size = MuzzleSize,
                    Tint = tint,
                    AnchorLeft = true,
                });
            }
            // Two low-volume breadcrumbs confirming the stored sprite basis matches the aircraft's,
            // at spawn and again once the plane has had a second to maneuver.
            double t = GameClock.Current?.Time ?? 0.0;
            if (_muzzleBasisLogs < 2 && (_muzzleBasisLogs == 0 || t >= 1.0))
            {
                _muzzleBasisLogs++;
                // Anchored sprites store Orient local to the muzzle node, resolve to world for
                // the comparison so the breadcrumb keeps meaning the same thing in both modes.
                var stored = anchored ? planeBasis * muzzleSprites[^1].Orient : muzzleSprites[^1].Orient;
                float match = stored.Z.Dot(planeBasis.Z);
                Log.Info("weapons", $"muzzle flash basis: z=({stored.Z.X:0.00},{stored.Z.Y:0.00},{stored.Z.Z:0.00}) stored.Z==aircraft.Z match={match:0.000} ammo={ammoIdx} anchored={anchored} t={t:0.00}s");
            }
        }

        // The gun shot's authored secondaries: the ejected casing, the muzzlepuffer smoke,
        // and the dynamic muzzle-light flash. Guns only, the muzzle_burst def is bound by the
        // guns; rockets carry their own FIRE effects.
        if (weapon.IsGun)
        {
            SpawnCasing(muzzle);
            SpawnMuzzleSmoke(muzzle);
            FlashMuzzleLight(muzzle, muzzleAnchor);
        }
    }

    /// <summary>Instances a weapon's <c>FLYOUT</c> <c>MODEL</c> body, its <c>.flt</c> prototype root
    /// in the chapter gamez, as a fresh collision-exempt <see cref="Node3D"/>, returned unparented
    /// for the caller to place. Shared by the in-flight rocket body and
    /// <see cref="PylonOrdnance"/>'s mounted round, the same asset either way. Null when no world
    /// scene is bound, the weapon carries no <c>FLYOUT</c> <c>MODEL</c>, or the gamez lacks the
    /// prototype root.</summary>
    public Node3D? BuildFlyoutBody(WeaponDef weapon)
    {
        if (_flyoutScene == null || _flyoutGamez == null || weapon.Flyout?.Model is not { } modelName)
            return null;
        if (!_flyoutNodes.TryGetValue(modelName, out var node))
        {
            node = _flyoutGamez.FindByName(modelName);
            _flyoutNodes[modelName] = node;
            if (node == null)
                Log.Info("weapons", $"flyout model '{modelName}' ({weapon.Id}) absent from this chapter gamez, rocket flies streak-only");
        }
        if (node == null)
            return null;
        // Collision-exempt: a rocket carries no collider (it raycasts for its own hits and must not
        // obstruct another round or the world hit-test; mounted ordnance must not be shootable either),
        // and the world builder would otherwise attach one.
        var inst = _flyoutScene.BuildSubtree(node, skip: null, collisionSkip: _ => true);
        // Verification breadcrumb (once per model name): confirms the named prototype resolved and
        // instanced real geometry, without needing a lucky screenshot; then it goes quiet.
        if (inst != null && _flyoutLogged.Add(modelName))
            Log.Info("weapons", $"flyout model '{modelName}' ({weapon.Id}) instanced: {CountMeshes(inst)} mesh(es)");
        return inst;
    }

    /// <summary>One ballistics step: integrate every live round and apply its first end condition.
    /// Surviving rounds raycast their swept segment; muzzle and impact sprites age here too.</summary>
    public void SimStep(float dt)
    {
        _simClock += dt;
        var space = GetWorld3D()?.DirectSpaceState;
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (!p.Alive)
                continue;
            // Integrate (fixed physics step, frame-rate independent).
            var prev = p.Pos;
            var next = p.Pos;
            // The inherited launch velocity rides on top of the round's own, at whatever the decay
            // has left of it; ACCELERATION and GRAVITY act on the round's own alone.
            float carried = InheritedFraction(in p);
            // Verification breadcrumb (first few): the shed is invisible from outside the round, so
            // report the pair the decode predicts as each one settles onto its authored VELOCITY.
            // Clearing the spent vector is what keeps this to one line per round.
            if (carried <= 0f && p.Inherited != Vector3.Zero)
            {
                if (_decaysLogged < 4)
                {
                    _decaysLogged++;
                    Log.Info("weapons", $"launch-velocity decay: {p.Weapon.Id} shed {p.Inherited.Length():0.#} m/s of launcher over LOCK_ON {p.Weapon.LockOn ?? 0f:0.##} s, now {p.Vel.Length():0.#} m/s");
                }
                p.Inherited = Vector3.Zero;
            }
            // The original's per-round order (FUN_005af720): the retarget callback, then the
            // steering step on what it left in the target slot, then the motion step. So a seeker's
            // turn this frame is toward this frame's pick, and the motor acts on the penalised speed.
            RetargetSeeker(ref p, BeeperTags);
            // The health test runs here, after the retarget callback and BEFORE the guidance gate
            // and the motion step, which is FUN_005af720's own order. A shot-down round therefore
            // neither steers nor moves on the frame it dies.
            if (p.Shootable is { Destroyed: true })
            {
                DestroyFlyout(ref p);
                continue;
            }

            Steer(ref p, dt);
            Ballistics.Step(ref next, ref p.Vel, p.Accel, p.Grav, p.Cap, dt);
            next += p.Inherited * (carried * dt);
            var vel = p.Vel + p.Inherited * carried;
            float stepLen = (next - prev).Length();
            p.Age += dt;
            p.Travelled += stepLen;
            ArmFlyoutIntersect(ref p);

            // ⚠ Resolve the three end conditions BEFORE the swept step, never after: the original
            // ends a round inside its motion step and moves and collides only what survives
            // (FUN_005afd50 returning to FUN_005af720's alive test).
            if (EndConditionMet(in p, out bool detonates, out bool targetFused))
            {
                EndRound(ref p, prev, vel, detonates, targetFused ? RegisteredBodyOf(p.Target) : null);
                continue;
            }

            if (stepLen > 1e-5f)
            {
                // A live flyout is in the same intersect database as the world, so the two answers
                // compete on distance and the nearer wins (FUN_005b03f0). The round excludes its
                // OWN box, never another's: a torpedo must not strike itself.
                var struckFlyout = FlyoutStruck(i, prev, next, out var flyoutPoint);
                float flyoutDistSq = struckFlyout != null
                    ? prev.DistanceSquaredTo(flyoutPoint) : float.PositiveInfinity;
                Vector3 hitPoint = default, hitNormal = default;
                Node? hitCollider = null;
                int hitShape = -1;
                float hitDistSq = float.PositiveInfinity;
                if (space != null)
                {
                    _ray.From = prev;
                    _ray.To = next;
                    // Per-shot owner exclusion on the SHARED query object: set for this round's
                    // shooter, reset right after, a leaked Exclude shields the next round's target.
                    _ray.Exclude = ExcludeFor(p.Shooter, p.Owner);
                    // Disposed, never dropped, as every engine collection below is. A hit dictionary
                    // left to the finalizer is one more finalizable object per round per tick, and
                    // that count sets the collection pause (docs/verification.md PERF-20).
                    using var hit = space.IntersectRay(_ray);
                    _ray.Exclude = NoExclude;
                    if (hit.Count > 0)
                    {
                        hitPoint = (Vector3)hit["position"];
                        hitCollider = hit["collider"].Obj as Node;
                        hitNormal = (Vector3)hit["normal"];
                        hitShape = hit["shape"].AsInt32();
                        hitDistSq = prev.DistanceSquaredTo(hitPoint);
                    }
                }

                // A struck flyout is FUN_005abcf0's first branch: it spends the pair against the
                // round the node points back to and nothing else of the impact happens, no
                // per-surface effect, no sound, no splash. The frame check above acts on the result.
                if (struckFlyout != null && flyoutDistSq <= hitDistSq)
                {
                    struckFlyout.Spend(p.Weapon.ArmorDamage ?? 0f, p.Weapon.HealthDamage ?? 0f);
                    RetireRound(ref p);
                    continue;
                }

                if (hitDistSq < float.PositiveInfinity)
                {
                    Impact(p.Weapon, hitPoint, hitCollider, hitNormal, vel, hitShape, p.Shooter, p.Team, p.Owner);
                    RetireRound(ref p);
                    continue;
                }
                // Checked AFTER the ray, so a round that would strike its target keeps the direct
                // hit. Damage arrives through the blast's aircraft pass, not this branch (shapeIdx
                // -1 keeps it out of the direct-hit path and off the aircraft's IMPACT row).
                if (ProximityFuseTriggered(p.Weapon, p.Shooter, p.Team, prev, next,
                        out var fusePoint, out var fused, out var towardHull))
                {
                    var fuseNormal = towardHull.LengthSquared() > 1e-8f
                        ? towardHull.Normalized() : Vector3.Zero;
                    Impact(p.Weapon, fusePoint, fused, fuseNormal, vel, -1, p.Shooter, p.Team, p.Owner);
                    RetireRound(ref p);
                    continue;
                }
            }
            p.PrevPos = p.Pos;
            p.Pos = next;
            // The round's def ticks on the moved round: its trail puffs are laid along this step
            // and anything it switches on now homes at the new pose.
            if (p.Rig != null)
                AdvanceFlyoutAnim(i, ref p, dt, next, FlyoutPose(next, vel).Basis);
        }

        StepTanglerClouds(dt);
        foreach (var m in _muzzle)
            AgeSprites(m, dt);
        AgeSprites(_impact, dt);
        AgeSprites(_smoke, dt);
        AgeCasings(dt);
        AgeLights(dt);
        for (int i = _impactFx.Count - 1; i >= 0; i--)
        {
            var f = _impactFx[i];
            f.Age += dt;
            if (f.Age >= f.Life)
            {
                f.Model.QueueFree();
                _impactFx.RemoveAt(i);
            }
            else
            {
                AdvanceSplash(f);
                _impactFx[i] = f;
            }
        }
    }

    public override void _Process(double delta)
    {
        using var _ = ProcessSiteCost.Enter(ProcessSite.Projectiles);
        RenderTracers();
        for (int i = 0; i < _muzzle.Length; i++)
            RenderSprites(_muzzleMm[i], _muzzle[i]);
        RenderSprites(_impactMm, _impact);
        RenderSprites(_smokeMm, _smoke);
        PlaceAnchoredLights();
    }

    /// <summary>Deactivates every live round (R / respawn: no tracers hang in the air).</summary>
    public void Clear()
    {
        for (int i = 0; i < _projHigh; i++)
        {
            KillModel(ref _proj[i]);
            ReleaseRig(ref _proj[i]);
            ReleaseFlyout(ref _proj[i]);
            _proj[i].Alive = false;
        }
        _projHigh = 0;
        _tanglerClouds.Clear();
        foreach (var m in _muzzle)
            m.Clear();
        _impact.Clear();
        _smoke.Clear();
        foreach (var c in _casings)
        {
            c.InUse = false;
            c.Node.Visible = false;
        }
        foreach (var l in _lights)
        {
            l.InUse = false;
            l.Light.Visible = false;
            l.Anchor = null;
        }
        _pointFlashes.Clear();
        foreach (var f in _impactFx)
            f.Model.QueueFree();
        _impactFx.Clear();
    }

    /// <summary>Raises <see cref="TurretAcquiredPlayer"/>. Internal, and called from
    /// <see cref="TurretController"/> alone: the gunner owns the episode rule (once per
    /// acquisition, re-armed by losing the target), this only carries the report.</summary>
    internal void ReportTurretAcquisition(TurretController turret, FlightController player) =>
        TurretAcquiredPlayer?.Invoke(turret, player);

    internal void UnregisterAircraft(AircraftBody body) => _aircraft.Remove(body);

    /// <summary>Where each muzzle-flash quad drawn this frame is pinned, in world space: the
    /// texture's left edge, which <see cref="RenderSprites"/> keeps on the muzzle as the quad
    /// shrinks. The instrument behind the flash-to-muzzle gap suite.</summary>
    internal IEnumerable<Vector3> DrawnMuzzleFlashAnchors()
    {
        var toWorld = GlobalTransform;
        foreach (var mm in _muzzleMm)
        {
            for (int i = 0; i < mm.VisibleInstanceCount; i++)
            {
                var xf = toWorld * mm.GetInstanceTransform(i);
                yield return xf.Origin - (xf.Basis.X * 0.5f);
            }
        }
    }

    /// <summary>The splash cover test itself, naming what it met so an instrument reports the
    /// occluder rather than a bare verdict (INSTR-3: the census and the damage path share one
    /// predicate). Its decode sits on <see cref="BlastCovered"/>, the damage path's entry.
    /// <paramref name="cover"/> can be null on a hit whose collider is not a node.</summary>
    internal bool BlastCoverBetween(PhysicsDirectSpaceState3D space, Vector3 point, Vector3 normal,
        Vector3 centre, Rid candidate, out Node? cover)
    {
        cover = null;
        var from = point + normal * CoverRayLift;
        if (from.DistanceSquaredTo(centre) <= 1e-6f)
            return false;
        _coverRay.From = from;
        _coverRay.To = centre;
        _coverExclude.Clear();
        _coverExclude.Add(candidate);
        _coverRay.Exclude = _coverExclude;
        using var hit = space.IntersectRay(_coverRay);
        _coverRay.Exclude = NoExclude;
        if (hit.Count == 0)
            return false;
        cover = hit["collider"].Obj as Node;
        return true;
    }

    /// <summary>The production splash gather and the production cover ray over one burst, dealing
    /// no damage: what a burst at <paramref name="point"/> reaches and what stops the rest. The
    /// aircraft half is deliberately absent, since a census over world geometry has no roster.
    /// </summary>
    internal void BlastCoverCensus(PhysicsDirectSpaceState3D space, Vector3 point, Vector3 normal,
        float radius, List<BlastCoverRow> into)
    {
        _blastCandidates.Clear();
        GatherWorldCandidates(space, point, radius, struck: null);
        _blastCandidates.Sort(ByDistance);
        foreach (var c in _blastCandidates)
        {
            bool covered = BlastCoverBetween(space, point, normal, c.Centre, c.Rid, out var cover);
            into.Add(new BlastCoverRow(c.Body, c.Centre, Mathf.Sqrt(c.DistanceSq),
                covered ? cover : null, covered));
        }

        _blastCandidates.Clear();
    }

    private static int CountMeshes(Node n)
    {
        int c = n is MeshInstance3D ? 1 : 0;
        for (int i = 0, count = n.GetChildCount(); i < count; i++)
            c += CountMeshes(n.GetChild(i));
        return c;
    }

    // Poses one live splash instance at its age along the authored curves (see the Splash*
    // constants): the base disc's xz ramp and the column's collapsing Y scale. Same scale
    // application as PoseChannel.PoseScale (rest basis orthonormalized, then scaled). A model
    // with neither child (not a splash) has nothing to drive and its Life stayed the short
    // static one.
    private static void AdvanceSplash(ImpactFx f)
    {
        float t = f.Age;
        if (f.Base != null)
        {
            float s = t < SplashBaseGrowTime
                ? Mathf.Lerp(1f, SplashBaseMax, t / SplashBaseGrowTime)
                : t < SplashBaseEaseStart
                    ? SplashBaseMax
                    : Mathf.Lerp(SplashBaseMax, SplashBaseEnd,
                        Mathf.Min((t - SplashBaseEaseStart) / (SplashRunTime - SplashBaseEaseStart), 1f));
            f.Base.Transform = new Transform3D(
                f.BaseRest.Basis.Orthonormalized().Scaled(new Vector3(s, 1f, s)), f.BaseRest.Origin);
        }
        if (f.Splash != null)
        {
            // OBJECT_MOTION SCALE (1,100,1) + (0,-100,0)·u over the 2 s run: the column pops to
            // its full authored height and collapses to nothing.
            float y = Mathf.Max(SplashColumnScale * (1f - t / SplashRunTime), 0.001f);
            f.Splash.Transform = new Transform3D(
                f.SplashRest.Basis.Orthonormalized().Scaled(
                    new Vector3(SplashColumnWidthScale, y, SplashColumnWidthScale)), f.SplashRest.Origin);
        }
        // Applied to base and column through separate per-instance shader params, so concurrent
        // splashes fade independently even when sharing one twinned material (EnsureSplashFade).
        float alpha = SplashOpacity(t);
        f.BaseMesh?.SetInstanceShaderParameter(SceneBuilder.OpacityParam, alpha);
        f.SplashMesh?.SetInstanceShaderParameter(SceneBuilder.OpacityParam, alpha);
    }

    private static float SplashOpacity(float t) =>
        t < SplashFadeInTime ? Mathf.Lerp(0f, 1f, t / SplashFadeInTime)
        : t < SplashFadeOutStart ? 1f
        : Mathf.Lerp(1f, 0f, Mathf.Min((t - SplashFadeOutStart) / SplashFadeOutTime, 1f));

    // The built subtree's nodes carry their gamez cs_name as NameMeta (Godot renames duplicate
    // siblings), resolve the splash children by that, never by Godot node name.
    private static Node3D? FindChildByMetaSuffix(Node node, string suffix)
    {
        if (node is Node3D n3d && node.HasMeta(AnimRuntime.NameMeta)
            && node.GetMeta(AnimRuntime.NameMeta).AsString().EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
            return n3d;
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            if (FindChildByMetaSuffix(node.GetChild(i), suffix) is { } found)
                return found;
        }
        return null;
    }

    // The flyout body's world pose: its geometry is authored nose-along-(-Z) (uniform across all 15
    // ROCKET models), so LookingAt(velDir), which aims local -Z down the argument, points the nose
    // along the round's flight direction. `pos` is the tail (the model origin sits at the exhaust end).
    private static Transform3D FlyoutPose(Vector3 pos, Vector3 vel)
    {
        var dir = vel.LengthSquared() > 1e-6f ? vel.Normalized() : Vector3.Forward;
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        return new Transform3D(Basis.LookingAt(dir, up), pos);
    }

    // The impact sprite's orientation: the quad faces the struck surface (local Z = the surface
    // normal), so a spark sits against the wall/ground it hit rather than in a fixed world plane.
    // A degenerate normal, a mid-air range-expiry detonation, which has no surface, falls back to
    // the original world-facing quad (Right/Up/Back), leaving that case byte-identical.
    private static Basis SurfaceBasis(Vector3 normal)
    {
        if (normal.LengthSquared() < 1e-6f)
            return new Basis(Vector3.Right, Vector3.Up, Vector3.Back);
        var z = normal.Normalized();
        var up = Mathf.Abs(z.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = up.Cross(z).Normalized();
        var y = z.Cross(x).Normalized();
        return new Basis(x, y, z);
    }

    // The template basis an IMPACT effect is placed with. The SURFACE_ANIMATION slot takes the
    // struck normal's rotation, the ANIMATION slot the fixed world axis.
    // ⚠ Keep the decoded rule in both presentations. The enhanced remake-only rule is
    // UpperRingOrient's alone.
    private static Basis EffectOrient(in ImpactOutcome outcome, Vector3 normal) =>
        outcome.SurfaceOriented ? SurfaceUpBasis(normal) : Basis.Identity;

    // The launcher's velocity a round actually carries away. ⚠ The discriminator is ACCELERATION,
    // never the weapon class: a round with no motor is seeded launcherVel + VELOCITY x direction
    // and never rebuilt, so it inherits whatever it was fired from, guns included. A motor round is
    // rebuilt every frame off a base vector the original fills only under LOCK_ON, so without that
    // key it keeps the launcher's SPEED alone, along its own heading (Ballistics.LaunchSpeed).
    private static Vector3 InheritedAtLaunch(WeaponDef weapon, Vector3 launcherVel) =>
        (weapon.Acceleration ?? 0f) == 0f || CarriesLockOn(weapon) ? launcherVel : Vector3.Zero;

    // What is left of the inherited launch velocity this frame: 1 at launch, falling linearly to 0
    // at LOCK_ON seconds, for EVERY round whose weapon carries LOCK_ON, target or none; a gun's
    // rounds hold theirs for their whole flight. ⚠ Deliberately NOT gated on the target as the
    // original's is (docs/architecture.md, Projectile.cs): the original always hands a LOCK_ON
    // round a target, ours may hold none, and gating the decay on it would fly a torpedo fired
    // with nothing selected at launcher speed plus 60 m/s forever. Only the turn stays gated.
    private static float InheritedFraction(in Proj p)
    {
        float window = p.Weapon.LockOn ?? 0f;
        return CarriesLockOn(p.Weapon) && window > 0f
            ? Mathf.Clamp((window - p.Age) / window, 0f, 1f)
            : 1f;
    }

    // The seeker's per-frame retarget (FUN_00441780, installed at spawn by FUN_00441830 on the
    // BEEPER_SEEKER flag): the tag list is asked with the round's position and unit heading and the
    // answer REPLACES the held target every frame, null included, exactly as the callback writes
    // zeros into +0x70/+0x74 when nothing is painted. It runs before the steering gate is read, so
    // whatever target the shooter handed a seeker at spawn is overwritten on its first frame and a
    // seeker only ever steers at a painted aircraft.
    private static void RetargetSeeker(ref Proj p, BeeperTags<FlightController>? tags)
    {
        if (!p.Weapon.BeeperSeeker)
            return;
        var heading = p.Vel.LengthSquared() > 1e-12f ? p.Vel.Normalized() : Vector3.Zero;
        p.Target = tags?.PickTarget(p.Pos, heading);
    }

    // The steering step proper (FUN_005af960), on a round SteeringStepRuns admits and whose target
    // has a position: the desired direction (the bearing, or B7's lead blend), the turn clamp, and
    // the speed penalty, rewriting the round's own velocity as heading × speed. The launcher's
    // share is not touched here; WorldVelocity blends it back on top with the decay's fraction,
    // which is the routine's own `heading × speed + fraction × inherited` rebuild.
    private static void Steer(ref Proj p, float dt)
    {
        if (!SteeringStepRuns(p.Weapon, p.Target != null) || TargetPosition(p.Target) is not { } at)
            return;
        float speed = p.Vel.Length();
        if (speed <= 1e-6f)
            return;
        var heading = p.Vel / speed;
        var toTarget = at - p.Pos;
        if (toTarget.LengthSquared() <= 1e-6f)
            return;
        var desired = toTarget.Normalized();
        desired = LeadDesired(in p, speed, at, desired);

        float angle = Mathf.Acos(Mathf.Clamp(heading.Dot(desired), -1f, 1f));
        if (!(angle > 0f))
            return;
        float maxTurn = MaxTurnRad(p.Weapon, dt);
        float turned;
        if (angle > maxTurn)
        {
            heading = SlerpDirection(heading, desired, maxTurn / angle);
            turned = maxTurn;
        }
        else
        {
            heading = desired;
            turned = angle;
        }
        p.Vel = heading * (speed * TurnPenaltyFactor(turned));
    }

    // LOCK_ON_LEAD's blend (FUN_005af960's first block): from element 0 of age, on a target with a
    // velocity, the intercept solve replaces the bearing, slerped in from the bearing at element 0
    // to the full solve at element 1 (the parse at 0x005ade43 stores 1/(el1 - el0) at weapon
    // +0x84 and clamps el0 to at most el1). The solve is FUN_0053e56d, the same constant-velocity
    // intercept AimAssist.TryIntercept is, on the round's OWN speed and the target's velocity; no
    // answer leaves the bearing. No shipped carrier reaches element 0 before its RANGE.
    private static Vector3 LeadDesired(in Proj p, float speed, Vector3 targetPos, Vector3 bearing)
    {
        if (p.Weapon.LockOnLead is not var (onset, full) || p.Age < onset
            || TargetVelocity(p.Target) is not { } targetVel
            || !AimAssist.TryIntercept(p.Pos, speed, targetPos, targetVel, out var lead, out _))
        {
            return bearing;
        }
        if (!(p.Age < full))
            return lead;
        return SlerpDirection(bearing, lead, (p.Age - onset) / (full - onset));
    }

    // Slerp between two unit directions, renormalised. Near opposite keeps `from`: there is no
    // axis to swing about, and a lerp through the origin would hand back nothing to normalise.
    private static Vector3 SlerpDirection(Vector3 from, Vector3 to, float weight)
    {
        if (from.Dot(to) < -SteerOppositeDot)
            return from;
        var result = from.Slerp(to, weight);
        return result.LengthSquared() > 1e-12f ? result.Normalized() : from;
    }

    // A round's velocity through the world. Proj.Vel alone is its OWN velocity (the original's
    // heading × speed, which is what ACCELERATION raises), so every reader asking which way and how
    // fast a round is travelling comes here for the inherited component on top.
    private static Vector3 WorldVelocity(in Proj p) => p.Vel + p.Inherited * InheritedFraction(in p);

    // The three ways a round ends itself, in the original's own order (FUN_005afd50 from the RANGE
    // compare to LAB_005b0318). Range wins outright. `detonates` is false only for the quiet RANGE
    // expiry; `targetFused` marks the own-target fuse, whose aircraft rides along to the burst.
    private static bool EndConditionMet(in Proj p, out bool detonates, out bool targetFused)
    {
        targetFused = false;
        if (p.Travelled >= p.Range)
        {
            detonates = DetonatesAtRange(p.Weapon);
            return true;
        }
        detonates = true;
        if (TargetFuseTriggered(in p))
        {
            targetFused = true;
            return true;
        }
        return p.Weapon.DetonationTime is { } fuse && fuse > 0f && p.Age > fuse;
    }

    // The per-round fuse on the round's OWN target, the second fuse path beside the aircraft sweep
    // in ProximityFuseTriggered. Squared throughout, as the original is: FUN_00538880 returns a
    // squared distance and weapon +0x44 stores DETONATION_DISTANCE squared.
    private static bool TargetFuseTriggered(in Proj p)
    {
        if (!CarriesLockOn(p.Weapon) || p.Weapon.DetonationDistanceSqM is not > 0f)
            return false;
        return TargetPosition(p.Target) is { } at
            && p.Pos.DistanceSquaredTo(at) <= p.Weapon.DetonationDistanceSqM.Value;
    }

    // Where a round's held target is. Typed loosely because the slot takes an aircraft, an
    // emplacement or a zeppelin sub-part alike; anything with no position in the world answers
    // null, fuses nothing and steers nothing.
    private static Vector3? TargetPosition(object? target) => target switch
    {
        FlightController rig => rig.WorldPosition,
        TurretController turret => turret.WorldPosition,
        Node3D node => node.GlobalPosition,
        _ => null,
    };

    // The held target's velocity, the second of the two fields the original's target carries
    // (round +0x74, the pointer FUN_0053e56d reads); null for anything that has none, which is
    // what leaves LOCK_ON_LEAD on the plain bearing.
    private static Vector3? TargetVelocity(object? target) => target switch
    {
        FlightController rig => rig.WorldVelocity,
        TurretController turret => turret.PlatformVelocity,
        _ => null,
    };

    private static void KillModel(ref Proj p)
    {
        if (p.Model != null)
        {
            p.Model.QueueFree();
            p.Model = null;
        }
    }

    private static void AgeSprites(List<Sprite> sprites, float dt)
    {
        for (int i = sprites.Count - 1; i >= 0; i--)
        {
            var s = sprites[i];
            s.Age += dt;
            if (s.Age >= s.Life)
            {
                sprites.RemoveAt(i);
            }
            else
            {
                // Smoke puffs carry velocity; every other sprite has it zeroed
                // and is unaffected, the position/orientation set at spawn stands for its whole life.
                if (s.Vel != Vector3.Zero)
                {
                    s.Pos += s.Vel * dt;
                    if (!s.NoGravity)
                        s.Vel.Y -= WorldGravity * dt;
                }
                sprites[i] = s;
            }
        }
    }

    // The muzzle-flash ammo-type index (into MuzzleAmmoTextures) resolved from the weapon's FIRE
    // ANIMATION binding: muzzle_burst_slug/_dum/_ap/_mag name the ammo type directly; the base
    // muzzle_burst and heavy-mount muzzle_burst2 carry no ammo suffix and read as slug.
    private static int MuzzleAmmoIndex(WeaponDef weapon)
    {
        var anim = weapon.Fire?.Animation;
        if (anim == null)
            return 0;
        if (anim.EndsWith("_dum", System.StringComparison.OrdinalIgnoreCase))
            return 1;
        if (anim.EndsWith("_ap", System.StringComparison.OrdinalIgnoreCase))
            return 2;
        if (anim.EndsWith("_mag", System.StringComparison.OrdinalIgnoreCase))
            return 3;
        return 0;
    }

    // Rolls a quad's basis about its own facing normal (Z), the in-plane rotation the flash triad
    // uses to vary its look per shot without disturbing which way the quad faces.
    private static Basis RollAroundNormal(Basis b, float radians)
    {
        float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
        var x = b.X * c + b.Y * s;
        var y = b.Y * c - b.X * s;
        return new Basis(x, y, b.Z);
    }

    // Where a flash's anchor puts it right now. False when it carries no anchor, or when the node
    // that fired it has been freed or unparented mid-flash. The light then stays where it last
    // stood for its remaining frames, rather than snapping to the world origin.
    private static bool MuzzleFrame(LightFlash flash, out Vector3 world) =>
        AnchorPoint(flash.Anchor, flash.Local, out world);

    private static bool AnchorPoint(Node3D? anchor, Vector3 local, out Vector3 world)
    {
        world = Vector3.Zero;
        if (anchor is null || !GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree())
            return false;
        var xf = anchor.GlobalTransform;
        world = xf.Origin + (xf.Basis.Orthonormalized() * local);
        return true;
    }

    private static void RenderSprites(MultiMesh mm, List<Sprite> sprites)
    {
        int n = 0;
        foreach (var s in sprites)
        {
            float k = 1f - s.Age / s.Life;            // shrink + fade over life
            float size = s.Size * (0.6f + 0.4f * k);
            var orient = s.Orient;
            var origin = s.Pos;
            if (s.Anchor is { } anchor)
            {
                // Pos/Orient are LOCAL to the anchor node, so the flash rides the plane. An anchor
                // freed or unparented mid-flash skips the draw for the sprite's last few ms.
                if (!GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree())
                    continue;
                var xf = anchor.GlobalTransform;
                var b = xf.Basis.Orthonormalized();
                orient = b * orient;
                origin = xf.Origin + (b * origin);
            }
            var basis = new Basis(orient.X * size, orient.Y * size, orient.Z * size);
            // QuadMesh's default UV puts U=0 (the texture's left edge) at local X=-0.5, anchoring
            // there keeps it pinned to Pos as the quad shrinks over its life.
            var pos = s.AnchorLeft ? origin + orient.X * (size * 0.5f) : origin;
            mm.SetInstanceTransform(n, new Transform3D(basis, pos));
            var c = s.Tint;
            c.A = k;
            mm.SetInstanceColor(n, c);
            n++;
        }
        mm.VisibleInstanceCount = n;
    }

    // A shape's local bounds from the server's own data (the same read ColliderOverlay makes for
    // the clutter bodies). A kind this does not read (a heightmap, a world boundary) reports a
    // zero box at the shape origin, which aims the cover ray at the shape's placement rather than
    // the body's, and never errors.
    private static Aabb ShapeBounds(Rid shapeRid)
    {
        var data = PhysicsServer3D.ShapeGetData(shapeRid);
        switch (PhysicsServer3D.ShapeGetType(shapeRid))
        {
            case PhysicsServer3D.ShapeType.Box:
                var half = data.AsVector3();
                return new Aabb(-half, half * 2f);
            case PhysicsServer3D.ShapeType.Sphere:
                float r = data.AsSingle();
                return new Aabb(new Vector3(-r, -r, -r), new Vector3(2f * r, 2f * r, 2f * r));
            case PhysicsServer3D.ShapeType.Capsule:
            case PhysicsServer3D.ShapeType.Cylinder:
                var dims = data.AsGodotDictionary();
                float rad = dims["radius"].AsSingle();
                float halfH = dims["height"].AsSingle() * 0.5f;
                return new Aabb(new Vector3(-rad, -halfH, -rad), new Vector3(2f * rad, 2f * halfH, 2f * rad));
            case PhysicsServer3D.ShapeType.ConvexPolygon:
                return PointBounds(data.AsVector3Array());
            case PhysicsServer3D.ShapeType.ConcavePolygon:
                return PointBounds(data.AsGodotDictionary()["faces"].AsVector3Array());
            default:
                return new Aabb();
        }
    }

    private static Aabb PointBounds(Vector3[] points)
    {
        if (points.Length == 0)
            return new Aabb();
        var box = new Aabb(points[0], Vector3.Zero);
        for (int i = 1; i < points.Length; i++)
            box = box.Expand(points[i]);
        return box;
    }

    // Builds one pooled sprite/streak pool: a MultiMesh of `cap`
    // instances over a single shared material. `crossed` supplies a mesh other
    // than the default unit quad, the tracer pools pass CrossedStreakMesh; everything
    // else takes the quad.
    private MultiMesh AddMultiMesh(string texture, int cap, bool additive, bool billboard, out MultiMeshInstance3D mmi, ArrayMesh? crossed = null)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = _textures.Find(texture),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            BillboardMode = billboard ? BaseMaterial3D.BillboardModeEnum.Enabled : BaseMaterial3D.BillboardModeEnum.Disabled,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            // ⚠ Do not add a `Uv1Scale.x=-1` mirror here. Without a matching `Uv1Offset` it clamps
            // every sample to one texture column, flattening every sprite this pool draws.
            TextureRepeat = false,
        };
        Mesh drawn;
        if (crossed != null)
        {
            crossed.SurfaceSetMaterial(0, mat);
            drawn = crossed;
        }
        else
        {
            drawn = new QuadMesh { Size = Vector2.One, Material = mat };
        }
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = drawn,
            InstanceCount = cap,
            VisibleInstanceCount = 0,
        };
        mmi = new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mmi);
        return mm;
    }

    // The authored tracer streak's shape: two perpendicular quads sharing the length axis,
    // in one mesh (the original's `rabbit_blur` is one model with two polys, so one instance draws
    // both). Unit-sized, local +Y is the FRONT and spans [-0.5, +0.5]; X and Z are the two width
    // axes, so RenderTracers scaling X and Z by the width and Y by the length yields
    // two width x length quads. U runs along the length, 0 at the front to 1 at the tail: the
    // authored UV convention, measured off the model (u tracks length, v tracks width).
    private ArrayMesh CrossedStreakMesh()
    {
        var verts = new Vector3[]
        {
            // quad A, the XY plane
            new(-0.5f, 0.5f, 0f), new(0.5f, 0.5f, 0f), new(0.5f, -0.5f, 0f), new(-0.5f, -0.5f, 0f),
            // quad B, the ZY plane, perpendicular to A
            new(0f, 0.5f, -0.5f), new(0f, 0.5f, 0.5f), new(0f, -0.5f, 0.5f), new(0f, -0.5f, -0.5f),
        };
        var uvs = new Vector2[]
        {
            new(0f, 0f), new(0f, 1f), new(1f, 1f), new(1f, 0f),
            new(0f, 0f), new(0f, 1f), new(1f, 1f), new(1f, 0f),
        };
        // Winding is irrelevant here and deliberately not fussed over: the shared material runs
        // CullMode.Disabled, which is the authored `show_backface` on both polys.
        var indices = new int[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        // No normals: the material is Unshaded, so nothing consumes them.
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // The in-flight rocket body: a BuildFlyoutBody instance parented under the
    // pool's own container (the caller poses it down the round's velocity each frame). Null falls back
    // to the exhaust streak.
    private Node3D? BuildFlyoutModel(WeaponDef weapon)
    {
        var inst = BuildFlyoutBody(weapon);
        if (inst != null)
            _flyoutModels.AddChild(inst);
        return inst;
    }

    /// <summary>Instances a named IMPACT effect's gamez MODEL prototype at the hit point (the water
    /// splash <c>splash1.flt</c>/<c>bsplsh.flt</c>), collision-exempt, tracked for a short life and
    /// freed. Returns false, leaving the stand-in spark to show, when there is no world scene, the
    /// name resolves to nothing, or the node built no mesh.</summary>
    // Installs this instance's own translucent twin as a surface override, so its own
    // per-instance uniform drives the fade without editing the shared cached material every other
    // splash's mesh also points at. Cached per source material (SceneBuilder.FadeShaderFor).
    // A source with no alpha path is counted, not swallowed: the splash plays its scale curves
    // without a fade rather than throwing.
    private void EnsureSplashFade(MeshInstance3D mi)
    {
        if (mi.Mesh is not { } mesh || mesh.GetSurfaceCount() == 0)
            return;
        if (mesh.SurfaceGetMaterial(0) is not ShaderMaterial { Shader: { } sh } sm)
            return;
        if (!_splashFadeTwins.TryGetValue(sm, out var twin))
        {
            var fadeShader = SceneBuilder.FadeShaderFor(sh);
            twin = fadeShader != null ? (ShaderMaterial)sm.Duplicate() : null;
            if (twin != null)
                twin.Shader = fadeShader;
            _splashFadeTwins[sm] = twin;
            if (twin == null)
                Log.Info("weapons", $"splash fade: source shader has no alpha path, fade skipped, curves unaffected");
        }
        if (twin != null)
            mi.SetSurfaceOverrideMaterial(0, twin);
    }

    // Registers the column's built material with the shared TextureCycler the first time it is
    // seen (see SplashFlipbookTextures above).
    // ⚠ Register the RENDERED material, the fade twin from EnsureSplashFade, not the mesh's source:
    // the cycler swaps albedo_tex on exactly the material it is handed.
    private void EnsureSplashFlipbook(MeshInstance3D mi)
    {
        if (_flyoutScene?.Cycler is not { } cycler)
            return;
        if (mi.Mesh is not { } mesh || mesh.GetSurfaceCount() == 0)
            return;
        var rendered = mi.GetSurfaceOverrideMaterial(0) ?? mesh.SurfaceGetMaterial(0);
        if (rendered is not ShaderMaterial mat || !_splashFlipbookRegistered.Add(mat))
            return;
        var frames = new List<ImageTexture>(SplashFlipbookTextures.Length);
        foreach (var name in SplashFlipbookTextures)
        {
            if (_textures.Find(name) is not { } frame)
                return; // an incomplete flipbook would strobe a hole; leave it static (TextureCycler's own rule)
            frames.Add(frame);
        }
        cycler.Add(mat, frames, SplashFlipbookFps, looping: true, "splash01");
    }

    private bool SpawnImpactModel(string animName, Vector3 point, Basis orient)
    {
        if (_flyoutScene == null || _flyoutGamez == null)
            return false;
        if (!_impactNodes.TryGetValue(animName, out var node))
        {
            node = _flyoutGamez.FindByName(animName);
            _impactNodes[animName] = node;
        }
        if (node == null)
            return false;
        var inst = _flyoutScene.BuildSubtree(node, skip: null, collisionSkip: _ => true);
        if (inst == null)
            return false;
        int meshes = CountMeshes(inst);
        if (meshes == 0)
        {
            // A geometry-less host (e.g. the `gunhit` puffer root): nothing would render, drop it
            // and keep the spark. Logged once so the data fact is visible, not silently swallowed.
            if (_impactFxLogged.Add(animName))
                Log.Info("weapons", $"impact effect '{animName}' is a geometry-less node, spark stands in");
            inst.QueueFree();
            return false;
        }
        _impactFxModels.AddChild(inst);
        // Identity for an ANIMATION row (the splash geometry stands upright), the surface rotation
        // for a SURFACE_ANIMATION one.
        inst.GlobalTransform = new Transform3D(orient, point);
        // A splash prototype (splash1.flt / bsplsh.flt) carries a `*_base` disc and a `*_splash`
        // column child; when either resolves, the instance plays the authored 2 s scale curves
        // (AdvanceSplash) instead of standing statically for the short stand-in life.
        var baseNode = FindChildByMetaSuffix(inst, "_base");
        var splashNode = FindChildByMetaSuffix(inst, "_splash");
        bool animated = baseNode != null || splashNode != null;
        // Authored `lighting: false`/`fog: false`; SceneBuilder honours both, so no hand-rolled
        // unshaded override is needed here.
        var baseMesh = baseNode?.GetNodeOrNull<MeshInstance3D>("mesh");
        var splashMesh = splashNode?.GetNodeOrNull<MeshInstance3D>("mesh");
        // The fade targets base AND column; the flipbook only the column, per the defs.
        if (baseMesh != null)
            EnsureSplashFade(baseMesh);
        if (splashMesh != null)
        {
            EnsureSplashFade(splashMesh);
            EnsureSplashFlipbook(splashMesh);
        }
        var fx = new ImpactFx
        {
            Model = inst,
            Age = 0f,
            Life = animated ? SplashRunTime : ImpactModelLife,
            Base = baseNode,
            Splash = splashNode,
            BaseRest = baseNode?.Transform ?? Transform3D.Identity,
            SplashRest = splashNode?.Transform ?? Transform3D.Identity,
            BaseMesh = baseMesh,
            SplashMesh = splashMesh,
        };
        if (animated)
            AdvanceSplash(fx); // pose t=0 (the column at full authored scale) before the first tick
        _impactFx.Add(fx);
        if (_impactFxLogged.Add(animName))
            Log.Info("weapons", $"impact effect '{animName}' instanced: {meshes} mesh(es){(animated ? $", splash curves driven (base {(baseNode != null ? "✓" : "–")}, column {(splashNode != null ? "✓" : "–")})" : "")}");
        return true;
    }

    private void Impact(WeaponDef weapon, Vector3 point, Node? collider, Vector3 normal, Vector3 velocity,
        int shapeIdx = -1, int shooter = NoShooter, int team = AimAssist.NeutralTeam,
        Godot.Collections.Array<Rid>? owner = null)
    {
        // A cannon round only ever reaches Impact through the direct-hit ray, so this one guard
        // covers the decode's three hit sites without distinguishing them.
        if (weapon.IsCannon && ScoredShooters.Contains(shooter))
            CannonHits++;
        // A ray hit reads the struck material; every self-ended round reads `default`, since
        // FUN_005ac3a0's hit record carries surface id 0 and never the fused aircraft (org/
        // ordnanceTypes.md "Which row a burst reads"). ⚠ Flak's `player` row draws nothing.
        int surface = shapeIdx >= 0 ? SurfaceIdOf(collider) : SurfaceRegistry.Default;
        bool hasEffectsRuntime = EffectSink != null;
        // The weapon's own hook runs before anything else (FUN_005ac7a0's first act) and its mask
        // feeds the resolve, so Apply performs a row already stripped of what the hook silenced.
        var suppression = RunImpactHook(weapon, point);
        // The terrain carve runs between the hook and the row's own bindings, under the hook's
        // Effects bit. ⚠ Do not answer the gate here: CraterGate holds both rules, and the
        // faithful one refuses every played round as the original does (docs/org/craters.md).
        var ask = shapeIdx >= 0 && collider is not AircraftBody
            && (suppression & ImpactSuppression.Effects) == 0
            ? CraterGate.For(weapon, SceneBuilder.CanModify(collider), CraterGate.Enabled)
            : CraterAsk.None;
        bool carved = ask != CraterAsk.None && (CraterSink?.Invoke(point, collider) ?? false);
        // ⚠ Only the faithful carve suppresses the burst. The option is remake-only chrome and adds
        // a bowl under a burst that still plays, where the original's AND drops both slots.
        bool cratered = carved && ask == CraterAsk.Faithful;
        // The decision, taken once and read twice. `modelResolved` cannot be known before the
        // attempt, so the first resolve is only for the effect NAME to attempt; the second carries
        // the answer. Everything after this line obeys `outcome`, Impact itself decides nothing.
        var outcome = ImpactOutcome.Resolve(weapon, surface, modelResolved: false, hasEffectsRuntime, suppression, cratered);
        // A chapter gamez node name instances at the hit point and skips the spark; a reader-def
        // or unresolved name leaves the spark to stand in. A name the effects runtime binds plays
        // there instead (Apply's sink), even when a same-named gamez template exists (ballflare.flt).
        bool effectBound = outcome.EffectName is { } bound && (EffectHandles?.Invoke(bound) ?? false);
        bool modelled = !effectBound && outcome.EffectName is { } fxName
            && SpawnImpactModel(fxName, point, EffectOrient(outcome, normal));
        // The second resolve now also carries whether the runtime renders the row's own name. That
        // is what separates `wep_02`'s bound `buildings` row from the guns' unbound one.
        if (modelled || effectBound)
            outcome = ImpactOutcome.Resolve(weapon, surface, modelled, hasEffectsRuntime, suppression, cratered, effectBound);

        // Verification breadcrumb: the first few impacts confirm hit detection and surface
        // classification without needing a lucky screenshot; then it goes quiet.
        if (_impactsLogged < 8)
        {
            _impactsLogged++;
            Log.Info("weapons", $"impact: {weapon.Id} ({weapon.Name}) -> {surface}/{SurfaceRegistry.NameForId(surface) ?? "?"} at ({point.X:0},{point.Y:0},{point.Z:0}) on {collider?.GetParent()?.Name}/{collider?.Name} fx={outcome.EffectName ?? "-"} snd={outcome.Sound ?? "-"} standin={outcome.StandIn}");
        }
        Apply(weapon, surface, outcome, point, collider, normal, velocity, shapeIdx, shooter, team, owner);
    }

    // Perform a resolved impact: the effect, the stand-in burst, the sound and the damage.
    // Decides nothing, every branch here is keyed on `outcome`. It still takes the
    // weapon for the gun-effect rate limit (a stateful throttle, not a decision) and the surface for
    // the spark's tint (a `Color`, which the engine-free ImpactOutcome cannot
    // carry). `team` is the round's own stamp, which the beeper's tag gate tests the victim against.
    private void Apply(WeaponDef weapon, int surface, in ImpactOutcome outcome, Vector3 point,
        Node? collider, Vector3 normal, Vector3 velocity, int shapeIdx = -1, int shooter = NoShooter,
        int team = AimAssist.NeutralTeam, Godot.Collections.Array<Rid>? owner = null)
    {
        // The impact sprites face the struck surface (SurfaceBasis(normal)) rather than a fixed world
        // plane, a supplier distinct from the muzzle flash's plane basis (both feed Sprite.Orient).
        var orient = SurfaceBasis(normal);
        // The puffer half, when a stand-in is owed: hand the name to the world-effects runtime,
        // which no-ops on a name it does not carry. Gun hits are throttled (GunEffectInterval/Ttl).
        if (outcome.StandIn != ImpactStandIn.None && outcome.EffectName is { } fxName
            && (!weapon.IsGun || GunEffectDue(fxName)))
            EffectSink?.Invoke(fxName, point, EffectOrient(outcome, normal), UpperRingOrient(velocity),
                weapon.IsGun ? GunEffectTtl : 0f);
        switch (outcome.StandIn)
        {
            case ImpactStandIn.Explosion:
                SpawnExplosion(point, orient);
                break;
            case ImpactStandIn.Spark when _impact.Count < MaxFlashes:
                // The water case is its own read of the struck id, as it is in the original
                // (FUN_005ad330, see SurfaceIsWater), not a branch the table could carry.
                var tint = surface == SurfaceRegistry.Water ? new Color(0.8f, 0.9f, 1.0f) : new Color(1f, 0.9f, 0.5f);
                _impact.Add(new Sprite { Pos = point, Life = ImpactLife, Size = ImpactSize, Tint = tint, Orient = orient });
                break;
        }
        // Per-surface IMPACT sound (landed): the struck surface's SOUND, else the default's.
        if (outcome.Sound is { } snd)
            PlaySound(snd, point);
        // A SONIC or FLASH burst disables every aircraft in its radius instead of hurting any
        // (FUN_004b9bc0's first branch, reached for each aircraft the splash gather finds).
        if (weapon.Sonic || weapon.Flash)
            ApplyDisabling(weapon, point, normal, shooter);
        // A BEEPER reaching an aircraft, struck or fused, paints it and deals nothing (FUN_004b9bc0
        // zeroes both figures after the tag): the pair is discarded structurally rather than passed
        // as zero, so an authored pair would still spend none of it. The gate is TryTag's own.
        if (weapon.BeeperTime is { } tagSeconds && collider is AircraftBody painted)
            BeeperTags?.TryTag(team, painted.Rig, tagSeconds);
        // The four no-damage types never reach an aircraft's ledger: each of FUN_004b9bc0's branches
        // zeroes the pair before the damage path, so a struck plane takes nothing here and
        // ApplyDamage's aircraft gather is skipped for them. The choker's own effect is its cloud.
        if (collider is AircraftBody && AircraftDamageDiscarded(weapon))
            return;
        // A ray-struck aircraft takes damage through its own part model, never the destructible
        // pipeline. A fuse detonation (shapeIdx -1) leaves its plane to the blast's aircraft pass.
        if (collider is AircraftBody plane && shapeIdx >= 0)
            plane.TakeProjectileHit(weapon, point, shapeIdx, shooter);
        else
            ApplyDamage(weapon, outcome, point, normal, collider is AircraftBody ? null : collider, shooter, owner);
    }

    // The nearest live flyout the segment from→to passes through, or null. Kept in the pool rather
    // than on a Godot body: the engine has ONE intersect database and its swept query finds a
    // flyout node in it exactly as it finds a wall, so the two answers compete on distance
    // (FUN_005b03f0 takes the nearer). It also makes the test independent of a physics frame.
    private Flyout? FlyoutStruck(int selfSlot, Vector3 from, Vector3 to, out Vector3 point)
    {
        point = to;
        Flyout? best = null;
        float bestT = float.PositiveInfinity;
        for (int i = 0; i < _projHigh; i++)
        {
            ref var q = ref _proj[i];
            // The intersect bit: clear from launch until RANGE_MINIMUM on the torpedo
            // (FUN_005aef40 clears it, FUN_005afd50 sets it once the accumulator passes 300 m).
            if (i == selfSlot || !q.Alive || q.IntersectOff || q.HitHalf == Vector3.Zero
                || q.Shootable is not { HealthMax: > 0f } flyout)
            {
                continue;
            }

            var pose = FlyoutPose(q.Pos, WorldVelocity(in q));
            var inv = new Transform3D(pose.Basis, pose * q.HitCentre).AffineInverse();
            if (!SegmentHitsBox(inv * from, inv * to, q.HitHalf, out float t) || t >= bestT)
            {
                continue;
            }

            bestT = t;
            best = flyout;
            point = from.Lerp(to, t);
        }
        return best;
    }

    // Segment against an origin-centred axis-aligned box, both in the box's own frame: the slab
    // test, returning the entry fraction along the segment (0 when it starts inside).
    private bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 half, out float t)
    {
        t = 0f;
        float lo = 0f, hi = 1f;
        var d = b - a;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = a[axis], dir = d[axis], h = half[axis];
            if (Mathf.Abs(dir) < 1e-9f)
            {
                if (Mathf.Abs(o) > h)
                    return false;
                continue;
            }

            float t0 = (-h - o) / dir, t1 = (h - o) / dir;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            lo = Mathf.Max(lo, t0);
            hi = Mathf.Min(hi, t1);
            if (lo > hi)
                return false;
        }
        t = lo;
        return true;
    }

    // A built subtree's extent in the subtree root's own frame: the union of every
    // MeshInstance3D's AABB, each carried up through its local transform.
    private Aabb ModelAabb(Node3D model)
    {
        var box = new Aabb();
        bool any = false;
        void Walk(Node node, Transform3D xf)
        {
            if (node is Node3D n3d && node != model)
                xf *= n3d.Transform;
            if (node is MeshInstance3D { Mesh: not null } mi)
            {
                var local = xf * mi.Mesh.GetAabb();
                box = any ? box.Merge(local) : local;
                any = true;
            }
            foreach (var child in node.GetChildren())
                Walk(child, xf);
        }

        Walk(model, Transform3D.Identity);
        return box;
    }

    // The impact hook's dispatch (weapon +0x20c, FUN_005ac7a0's first call): the TANGLER parse is
    // the binary's one installer, its hook (0x004ba660) pushes a choker cloud at the burst onto the
    // world list and returns 1, so a choker's IMPACT row plays no sound. Every other weapon has none.
    private ImpactSuppression RunImpactHook(WeaponDef weapon, Vector3 point)
    {
        switch (weapon.ImpactHook)
        {
            case ImpactHook.Tangler:
                SpawnTanglerCloud(weapon, point);
                return ImpactSuppression.Sound;
            default:
                return ImpactSuppression.None;
        }
    }

    // The SONIC/FLASH hit against every aircraft the burst reaches. The gather, cover test and 32
    // cap are the blast's. The original hands each splash entry to FUN_004b9bc0, whose branch runs
    // FUN_0042e840 on the entry's squared surface distance. The victim's kind then decides: a
    // human's pane gets the wash, an AI pilot the stun, and neither takes a point of damage. The
    // shooter is a candidate here because the original's self-hit guard stands below the no-damage
    // arms. A pilot is flashed and deafened by their own burst, and only the damage pair is exempt.
    private void ApplyDisabling(WeaponDef weapon, Vector3 point, Vector3 normal, int shooter)
    {
        float radiusSq = weapon.ImpactProximitySqM ?? 0f;
        if (radiusSq <= 0f)
            return;
        var space = GetWorld3D()?.DirectSpaceState;
        _blastCandidates.Clear();
        GatherAircraftCandidates(point, radiusSq, shooter, includeShooter: true);
        _blastCandidates.Sort(ByDistance);
        int accepted = 0;
        for (int i = 0; i < _blastCandidates.Count && accepted < MaxBlastTargets; i++)
        {
            var c = _blastCandidates[i];
            if (space != null && BlastCovered(space, point, normal, c))
                continue;
            accepted++;
            var rig = c.Plane!.Rig;
            float facing = DisablingIntensity.FacingDot(rig.NoseDirection, rig.WorldPosition, point);
            if (!DisablingIntensity.TryResolve(c.DistanceSq, radiusSq, weapon.Flash, facing,
                    out float intensity, out float stunSeconds))
                continue;
            bool applied;
            if (rig.IsHumanPiloted)
            {
                WashSink?.Invoke(rig.PlayerIndex, weapon.Sonic ? SonicWashColour : FlashWashColour,
                    intensity, stunSeconds, DisablingWashStartDelay);
                applied = WashSink != null;
            }
            else
            {
                applied = rig.TryStunPilot(stunSeconds);
            }
            if (_disablingLogged < 8)
            {
                _disablingLogged++;
                Log.Info("weapons", $"disabling hit: {weapon.Id} on P{rig.PlayerIndex + 1} at {Mathf.Sqrt(c.DistanceSq):0.#} m intensity {intensity:0.00} for {stunSeconds:0.0} s ({(rig.IsHumanPiloted ? "wash" : "stun")}{(applied ? "" : ", no taker")})");
            }
        }
        _blastCandidates.Clear();
    }

    // The choker cloud the TANGLER hook leaves at the burst: the weapon's RADIUS squared as the catch
    // test (FUN_004b94e0 squares it once) and the shared TIME as its life. Nothing excludes the
    // shooter, so a pilot flying into their own cloud is choked like anyone else.
    private void SpawnTanglerCloud(WeaponDef weapon, Vector3 point)
    {
        float radius = weapon.Tangler?.Radius ?? TanglerData.DefaultRadius;
        _tanglerClouds.Add(new TanglerCloud
        {
            Centre = point,
            RadiusRaw = radius,
            RadiusSq = radius * radius,
            Remaining = weapon.Tangler?.Time ?? TanglerData.DefaultTime,
        });
    }

    // One step of every cloud (FUN_004b96d0 over FUN_004b9590): its life runs down first, and while
    // any is left every in-play aircraft whose ORIGIN sits inside the radius takes the choke for
    // TanglerChoke.Duration of its squared origin distance. The distance is to the vehicle
    // position, not its hull, which is what leaves the 13 s zone a few metres wide.
    private void StepTanglerClouds(float dt)
    {
        for (int i = _tanglerClouds.Count - 1; i >= 0; i--)
        {
            var cloud = _tanglerClouds[i];
            cloud.Remaining -= dt;
            if (cloud.Remaining <= 0f)
            {
                _tanglerClouds.RemoveAt(i);
                continue;
            }
            foreach (var plane in _aircraft)
            {
                var rig = plane.Rig;
                if (!rig.InPlay)
                    continue;
                float distanceSq = rig.WorldPosition.DistanceSquaredTo(cloud.Centre);
                if (distanceSq >= cloud.RadiusSq)
                    continue;
                float seconds = TanglerChoke.Duration(distanceSq, cloud.RadiusRaw,
                    EngineDeadBounds.Min, EngineDeadBounds.Max);
                rig.TryChokeEngine(seconds);
            }
        }
    }


    // ⚠ Do not re-add a world fuse: it detonates rockets short and locks hardpoint weapons out of
    // their per-surface IMPACT entry. Candidates are VehicleList's aircraft and hulls, never world
    // bodies or zeppelins. Both walks skip the round's whole side by plain id equality
    // (docs/org/ordnanceTypes.md "The proximity fuse"). Detonation is at CLOSEST APPROACH within
    // the swept step, not first entry. Otherwise most rockets burst where the blast is zero.
    // ⚠ These walks are rosters, not a physics query, so each needs a liveness test.
    private bool ProximityFuseTriggered(WeaponDef weapon, int shooter, int team, Vector3 from,
        Vector3 to, out Vector3 detonationPoint, out AircraftBody? fused, out Vector3 towardHull)
    {
        detonationPoint = to;
        fused = null;
        towardHull = Vector3.Zero;
        if (weapon.DetonationDistance is not { } fuseRange || fuseRange <= 0f)
            return false;
        float best = float.PositiveInfinity;
        foreach (var plane in _aircraft)
        {
            if (plane.PlayerIndex == shooter || plane.Rig.Team == team || !plane.Rig.InPlay)
                continue;
            // Cheap reject: the segment cannot come within fuse range of any box while it stays
            // outside the plane's bounding sphere by more than that range.
            if (SegmentPointDistance(from, to, plane.GlobalPosition)
                > fuseRange + plane.BoundRadius)
                continue;
            float d = plane.SegmentDistance(from, to, out float t, out var hull);
            if (d > fuseRange || d >= best || t >= StillClosingFraction)
                continue;
            var candidate = from + (to - from) * t;
            if (!FuseDotAllows(weapon.DetonationDotProduct, BackwardAxis(plane),
                    candidate - plane.GlobalPosition))
                continue;
            best = d;
            detonationPoint = candidate;
            fused = plane;
            towardHull = hull - candidate;
        }
        if (SurfaceVehicles == null)
            return fused != null;
        // A hull is measured to its origin, the decoded point-to-point distance. It has no airframe
        // hull set, and its colliders are world bodies the fuse must never query.
        foreach (var vessel in SurfaceVehicles.Vessels)
        {
            if (!GodotObject.IsInstanceValid(vessel.Body) || vessel.Inert || vessel.IsDestroyed
                || (vessel.Team ?? AimAssist.NeutralTeam) == team)
                continue;
            var origin = vessel.Position;
            float t = SegmentClosestFraction(from, to, origin);
            var candidate = from + (to - from) * t;
            float d = candidate.DistanceTo(origin);
            if (d > fuseRange || d >= best || t >= StillClosingFraction)
                continue;
            if (!FuseDotAllows(weapon.DetonationDotProduct, BackwardAxis(vessel.Body),
                    candidate - origin))
                continue;
            best = d;
            detonationPoint = candidate;
            fused = null;
            towardHull = origin - candidate;
        }
        return best < float.PositiveInfinity;
    }

    // The one exit every self-ended round takes, so the range expiry and both fuses share the
    // effect, sound and splash paths a struck surface gets, all of them off the `default` row
    // (FUN_005ac3a0). The own-target fuse hands its aircraft along for what the sweep fuse's
    // aircraft already gets, the beeper's tag and the blast's anchor, not for the row; every other
    // end strikes nothing and the impact sprite falls back to the world-facing quad. The shooter
    // rides along so the blast's aircraft pass attributes its kills.
    private void EndRound(ref Proj p, Vector3 at, Vector3 velocity, bool detonate, AircraftBody? fused = null)
    {
        if (detonate)
        {
            var toHull = fused != null ? fused.GlobalPosition - at : Vector3.Zero;
            var normal = toHull.LengthSquared() > 1e-8f ? toHull.Normalized() : Vector3.Zero;
            Impact(p.Weapon, at, fused, normal, velocity, shooter: p.Shooter, team: p.Team, owner: p.Owner);
        }
        RetireRound(ref p);
    }

    // The registered body of a round's own held target, so the own-target fuse can hand Impact the
    // aircraft it burst on. Null for a non-aircraft target (a zeppelin part, a bare lab mark).
    private AircraftBody? RegisteredBodyOf(object? target)
    {
        if (target is AircraftBody body)
            return body;
        if (target is not FlightController rig)
            return null;
        foreach (var b in _aircraft)
            if (b.Rig == rig)
                return b;
        return null;
    }

    // Takes a round out of the pool and releases what it was carrying. The live smoke of a released
    // trail decays where it was left rather than vanishing with the round.
    private void RetireRound(ref Proj p)
    {
        p.Alive = false;
        KillModel(ref p);
        ReleaseRig(ref p);
        ReleaseFlyout(ref p);
    }

    // A round shot out of the air (FUN_005af720's health branch): it plays its DESTROY_ANIMATION
    // where it was and dies WITHOUT detonating, the warhead is not set off. Only a shootable round
    // authoring no DESTROY_ANIMATION falls through to the ordinary detonation, which no entry in
    // this install does, so the torpedo never splashes what shot it down.
    private void DestroyFlyout(ref Proj p)
    {
        if (p.Weapon.DestroyAnimation is { } anim)
        {
            EffectSink?.Invoke(anim, p.Pos, Basis.Identity, null, 0f);
            if (_flyoutDestroysLogged < 2)
            {
                _flyoutDestroysLogged++;
                Log.Info("weapons", $"flyout destroyed: {p.Weapon.Id} shot down, playing '{anim}'");
            }
            RetireRound(ref p);
            return;
        }

        EndRound(ref p, p.Pos, p.Vel, detonate: true);
    }

    // The admission byte and the health pair leave with the round: a selection held on a dead
    // flyout has to drop, and the box must stop being hittable the frame the round ends.
    private void ReleaseFlyout(ref Proj p)
    {
        if (p.Shootable != null)
        {
            p.Shootable.Live = false;
            p.Shootable = null;
        }
        p.HitHalf = Vector3.Zero;
    }

    // The RANGE_MINIMUM gate (FUN_005afd50 at 0x005b01c4): once the accumulator passes the authored
    // distance the round's intersect bit is set and FlyoutStruck can find it. Nothing visible
    // changes here; the body and its def have been running since the spawn. The original also
    // requires RANGE_MINIMUM's second element to be zero, which the sole carrier authors.
    private void ArmFlyoutIntersect(ref Proj p)
    {
        if (!p.IntersectOff || p.Travelled <= (p.Weapon.RangeMinimum ?? 0f))
            return;
        p.IntersectOff = false;
        if (_armedLogged < 2)
        {
            _armedLogged++;
            Log.Info("weapons", $"flyout intersect: {p.Weapon.Id} hittable after {p.Travelled:0.#} m of its RANGE_MINIMUM {p.Weapon.RangeMinimum ?? 0f:0.#} m");
        }
    }

    private void ConfigureSphereQuery(float radius, Vector3 point)
    {
        _proximitySphere.Radius = radius;
        _proximityQuery.Shape = _proximitySphere;
        _proximityQuery.Transform = new Transform3D(Basis.Identity, point);
        _proximityQuery.Motion = Vector3.Zero;
    }

    // ⚠ The centre the cover ray aims at is the struck shape's BOUNDS centre, read back off the
    // physics server, never a node origin: a chapter mesh node's origin sits at ground level (a ray
    // to it ends ON the terrain, so every building reads as covered) or at the world origin, and a
    // clutter region body's shapes have no CollisionShape3D owner at all
    // (Clutter.BuildSolidCollision), so ShapeFindOwner/ShapeOwnerGetTransform on it error and
    // return identity. The server knows every shape and its body-local placement either way.
    private Vector3 BlastCentre(Node3D body, Rid rid, int shapeIndex, Vector3 fallback)
    {
        if (shapeIndex < 0 || shapeIndex >= PhysicsServer3D.BodyGetShapeCount(rid))
            return fallback;
        var shapeRid = PhysicsServer3D.BodyGetShape(rid, shapeIndex);
        if (!_shapeBounds.TryGetValue(shapeRid, out var bounds))
            _shapeBounds[shapeRid] = bounds = ShapeBounds(shapeRid);
        var placement = body.GlobalTransform * PhysicsServer3D.BodyGetShapeTransform(rid, shapeIndex);
        return placement * bounds.GetCenter();
    }

    // ⚠ Measure splash falloff to the nearest point on the neighbour's OWN collision shape, never
    // its transform origin. A large body (a zeppelin gasbag, a long building mesh) otherwise soaks
    // less splash than a small one, or none at all if its origin sits outside the sweep sphere.
    // Falls back to the shape's bounds centre only as a defensive case; IntersectShape already
    // found an overlap.
    private Vector3 NearestBlastPoint(PhysicsDirectSpaceState3D space, Vector3 center, float radius,
        Node3D body, Rid bodyRid, Godot.Collections.Array<Godot.Collections.Dictionary> hits, int shapeIndex)
    {
        var exclude = new Godot.Collections.Array<Rid>();
        foreach (var hit in hits)
        {
            var rid = (Rid)hit["rid"];
            if (rid != bodyRid)
                exclude.Add(rid);
        }
        ConfigureSphereQuery(radius, center);
        _proximityQuery.Exclude = exclude;
        var rest = space.GetRestInfo(_proximityQuery);
        _proximityQuery.Exclude = new Godot.Collections.Array<Rid>();
        return rest.Count > 0 ? (Vector3)rest["point"] : BlastCentre(body, bodyRid, shapeIndex, body.GlobalPosition);
    }

    // The detonation's damage: the struck body takes the full figure, then every candidate inside
    // IMPACT_PROXIMITY takes the falloff share of it, nearest first, cover-tested, at most 32 of
    // them (FUN_005aca30 then FUN_005acac0). Planes and destructibles share one gather and one cap
    // because the original's hit buffer holds both; planes are struck through their own part
    // model, destructibles through DamageSink, and neither is ever handed to the other's path.
    private void ApplyDamage(WeaponDef weapon, in ImpactOutcome outcome, Vector3 point, Vector3 normal,
        Node? struck, int shooter, Godot.Collections.Array<Rid>? owner = null)
    {
        float fullDamage = outcome.Damage;
        float radius = outcome.BlastRadius;

        // The F18 zeppelin gate, per struck body: a refused body takes no damage while the
        // impact effect/sound above played normally.
        bool Gated(Node? body) => WorldDamageGate != null && !WorldDamageGate(body, weapon);

        if (!outcome.HasBlastDamage)
        {
            if (DamageSink != null && fullDamage > 0f && !Gated(struck))
                DamageSink(struck, fullDamage);
            return;
        }

        // The ray contact is the detonation centre even when the collider's transform origin is far
        // away (large chapter meshes), so preserve full direct-hit damage and exclude it below.
        if (DamageSink != null && struck != null && !Gated(struck))
            DamageSink(struck, fullDamage);

        // The falloff denominator is the engine's stored square (weapon +0x40), never a root of
        // the authored radius taken here.
        float radiusSq = weapon.ImpactProximitySqM ?? radius * radius;
        var space = GetWorld3D()?.DirectSpaceState;
        _blastCandidates.Clear();
        // A no-damage type's aircraft half is its own branch (ApplyDisabling, the tag, the cloud),
        // never a scaled zero through the ledger, which would still flash "HIT" and wake the AI.
        if (!AircraftDamageDiscarded(weapon))
            GatherAircraftCandidates(point, radiusSq, shooter);
        if (space != null && DamageSink != null)
            GatherWorldCandidates(space, point, radius, struck, owner);
        _blastCandidates.Sort(ByDistance);

        // One share per world OBJECT, not per collider body: the original's hit buffer holds one
        // entry per node and SceneBuilder splits a node into a body per surface class and soil. The struck
        // node took the full figure already, so its siblings are spent with it.
        _blastGroups.Clear();
        if (struck != null)
            _blastGroups.Add(Mech3.WorldCollision.OwnerOf(struck).GetInstanceId());

        int accepted = 0;
        for (int i = 0; i < _blastCandidates.Count; i++)
        {
            if (accepted == MaxBlastTargets)
            {
                Log.Info("weapons", $"blast limit: {weapon.Id} burst at ({point.X:0},{point.Y:0},{point.Z:0}) had {_blastCandidates.Count} targets inside {radius:0.#} m; the nearest {MaxBlastTargets} took damage and {_blastCandidates.Count - i} were dropped (the original's 32-entry hit buffer)");
                break;
            }
            var c = _blastCandidates[i];
            // Nearest-first order makes the kept share the nearest body's, and the object is
            // decided here: a covered nearest body drops it rather than deferring to a sibling.
            if (c.Plane == null && c.Body != null
                && !_blastGroups.Add(Mech3.WorldCollision.OwnerOf(c.Body).GetInstanceId()))
                continue;
            if (space != null && BlastCovered(space, point, normal, c))
                continue;
            float share = BlastFalloff(c.DistanceSq, radiusSq);
            if (c.Plane != null)
                c.Plane.TakeProjectileHit(weapon, c.NearPoint, c.ShapeIdx, shooter, damageScale: share);
            else if (share > 0f && !Gated(c.Body))
                DamageSink!(c.Body, fullDamage * share);
            accepted++;
        }
        _blastCandidates.Clear();
    }

    // Every registered flying plane inside the radius, never one out of play, and never the
    // shooter's own on the damage pass. The fuse's roster-walk caveat applies. Each is measured to
    // the nearest point on its collision boxes (0 inside, the engulf clamp). The hit lands at that
    // box, so part mapping and kill attribution run the direct-hit path. ⚠ Keep the damage pass's
    // shooter exemption here. The original guards it one level lower, BELOW the no-damage arms,
    // which is why the disabling pass asks for the shooter back (docs/org/ordnanceTypes.md).
    private void GatherAircraftCandidates(Vector3 point, float radiusSq, int shooter, bool includeShooter = false)
    {
        foreach (var plane in _aircraft)
        {
            if ((plane.PlayerIndex == shooter && !includeShooter) || !plane.Rig.InPlay)
                continue;
            int shapeIdx = plane.NearestShape(point, out float distance, out var nearPoint);
            if (shapeIdx < 0 || distance * distance >= radiusSq)
                continue;
            _blastCandidates.Add(new BlastCandidate
            {
                Plane = plane,
                Rid = plane.GetRid(),
                ShapeIdx = shapeIdx,
                NearPoint = nearPoint,
                Centre = plane.GlobalPosition,
                DistanceSq = distance * distance,
            });
        }
    }

    // Every world body the blast sphere overlaps except the struck one and the round's own owner
    // bodies, scored from the nearest point on its own collision shape (NearestBlastPoint). The
    // struck body's contact point is the burst, so its own share is the full figure already dealt;
    // the owner is the original's shooter node with its intersect bit cleared for the gather, so an
    // emplacement's flak bursting beside it never splashes the gun that fired it.
    private void GatherWorldCandidates(PhysicsDirectSpaceState3D space, Vector3 point, float radius,
        Node? struck, Godot.Collections.Array<Rid>? owner = null)
    {
        ConfigureSphereQuery(radius, point);
        var hits = space.IntersectShape(_proximityQuery, MaxBlastBodies);
        // The typed array is not disposable itself; its untyped core is the finalizable wrapper.
        using var hitsCore = (Godot.Collections.Array)hits;
        if (hits.Count == MaxBlastBodies)
            GD.PushWarning($"blast query reached {MaxBlastBodies} bodies at radius {radius:0.##} m");
        foreach (var hit in hits)
        {
            var body = hit["collider"].Obj as Node;
            if (body == struck || body is not Node3D body3D)
                continue;
            var rid = (Rid)hit["rid"];
            if (owner != null && owner.Contains(rid))
                continue;
            int shapeIndex = hit["shape"].AsInt32();
            var nearPoint = NearestBlastPoint(space, point, radius, body3D, rid, hits, shapeIndex);
            _blastCandidates.Add(new BlastCandidate
            {
                Body = body,
                Rid = rid,
                ShapeIdx = shapeIndex,
                NearPoint = nearPoint,
                Centre = BlastCentre(body3D, rid, shapeIndex, nearPoint),
                DistanceSq = nearPoint.DistanceSquaredTo(point),
            });
        }
    }

    // The occlusion test (FUN_004cb420 under FUN_005aca30's occlusion flag): a ray from the burst
    // to the candidate's centre, the candidate itself excluded, and anything it meets on the way
    // is cover. The origin is lifted off the struck surface along its normal (CoverRayLift) so the
    // wall the round hit is cover for what stands behind it and transparent to its own side; a
    // fuse or range burst in the air has no normal and no lift.
    private bool BlastCovered(PhysicsDirectSpaceState3D space, Vector3 point, Vector3 normal,
        in BlastCandidate c) =>
        BlastCoverBetween(space, point, normal, c.Centre, c.Rid, out _);

    // Whether this gun's impact effect may play again now, stamping the time when it may.
    // The throttle is per effect name = per firing group (see _gunEffectAt).
    private bool GunEffectDue(string fxName)
    {
        if (_gunEffectAt.TryGetValue(fxName, out float last) && _simClock - last < GunEffectInterval)
            return false;
        _gunEffectAt[fxName] = _simClock;
        return true;
    }

    // A stand-in fireball: a cluster of large, bright, additive sprites at the hit point,
    // varied in size/life/tint, so a hardpoint impact reads as an explosion where the real puffer
    // effect cannot be built (no world-effects runtime).
    private void SpawnExplosion(Vector3 point, Basis orient)
    {
        for (int i = 0; i < ExplosionSprites && _impact.Count < MaxFlashes; i++)
        {
            var off = new Vector3(_rng.Randf() - 0.5f, _rng.Randf() - 0.5f, _rng.Randf() - 0.5f) * ExplosionSpread;
            float t = _rng.Randf();
            _impact.Add(new Sprite
            {
                Pos = point + off,
                Life = ExplosionLife * (0.6f + 0.6f * t),
                Size = ExplosionSize * (0.7f + 0.6f * t),
                Tint = new Color(1f, 0.45f + 0.4f * t, 0.12f * t), // deep orange → yellow core
                Orient = orient,
            });
        }
    }

    // Ejects one shell casing: a pooled instance of the `gunshell` prototype, launched with its
    // def's own OBJECT_MOTION (MotionRuntime semantics). Each casing rides its own transient node,
    // so sustained fire ejects at gun rate. No-op without a world scene/anim program.
    private void SpawnCasing(Transform3D muzzle)
    {
        var spec = CasingSpecResolve();
        if (spec == null)
            return;
        var slot = AcquireCasing();
        if (slot != null)
        {
            // Direction is drawn in the MUZZLE's frame, not the world's, so a rolling plane throws
            // its brass out sideways rather than at the ground.
            slot.Basis = muzzle.Basis.Orthonormalized();
            slot.Start = muzzle.Origin + (slot.Basis * MuzzleSecondaryOffset);
            var dir = Mech3.Anim.MotionRuntime.RangeLaunchDirection(
                RandRange(spec.XzMin, spec.XzMax), RandRange(spec.YMin, spec.YMax));
            // The tumble turns about THIS draw's horizontal perpendicular, in the same muzzle frame
            // the direction was drawn in, MotionRuntime.TumbleAxis, so the two cannot drift.
            slot.TumbleAxis = Mech3.Anim.MotionRuntime.TumbleAxis(dir);
            slot.V0 = slot.Basis * (dir * RandRange(spec.SpeedMin, spec.SpeedMax));
            slot.Age = 0f;
            slot.InUse = true;
            slot.Node.Visible = true;
            slot.Node.GlobalTransform = new Transform3D(slot.Basis, slot.Start);
        }
    }

    // The authored muzzlepuffer smoke. A few short-lived puffs sit at the effects root ahead of the
    // muzzle, with the def's aft velocity, size, lifetime and deviation. The aircraft flies out of
    // them, so they read as the smoke the shot leaves behind.
    private void SpawnMuzzleSmoke(Transform3D muzzle)
    {
        var aft = muzzle.Basis.Z.Normalized(); // Godot forward is -Z; the puffer drifts aft
        var orient = muzzle.Basis.Orthonormalized();
        var origin = muzzle.Origin + (orient * MuzzleSecondaryOffset);
        for (int i = 0; i < MuzzleSmokePuffs && _smoke.Count < MaxSmoke; i++)
        {
            var dev = new Vector3(_rng.Randf() - 0.5f, _rng.Randf() - 0.5f, _rng.Randf() - 0.5f)
                      * (2f * MuzzleSmokeDeviation);
            var vel = aft * MuzzleSmokeAftSpeed + new Vector3(
                _rng.Randf() - 0.5f, _rng.Randf() - 0.5f, _rng.Randf() - 0.5f) * (2f * MuzzleSmokeRandVel);
            _smoke.Add(new Sprite
            {
                Pos = origin + dev,
                Life = RandRange(MuzzleSmokeLifeMin, MuzzleSmokeLifeMax),
                Size = RandRange(MuzzleSmokeSizeMin, MuzzleSmokeSizeMax),
                Tint = MuzzleSmokeTint,
                Orient = orient,
                Vel = vel,
                NoGravity = true,
            });
        }
    }

    // The dynamic muzzle-light flash, the muzzle_burst def's testfp branch. A first-person view
    // takes the two big 1stperson_lts lights, otherwise one of the three small 3rdperson_lts
    // variants. Both branches sit at MuzzleSecondaryOffset, the displaced effects root.
    // ⚠ A LIGHT_STATE RANGE is a falloff PAIR, not a band to roll in (WorldLights.Add). The outer
    // value is where the pool reaches zero, the OmniLight3D range. A rolled range pulsed per shot.
    private void FlashMuzzleLight(Transform3D muzzle, Node3D? anchor)
    {
        // A pre-tree volley (the weapon lab engages while still building) has no world transform
        // to place a light in, skip the flash rather than set GlobalPosition out of tree.
        if (!IsInsideTree())
            return;
        if (FirstPersonView?.Invoke() == true)
        {
            // In original mode the pair is the point term, which is what lights the unshaded
            // cockpit interior; an omni reaches nothing there (docs/formats/weapon-effects.md).
            bool pointTerm = _pointLights != null && !GraphicsMode.Enhanced;
            foreach (var (at, near, far, tint) in FirstPersonLights)
            {
                if (pointTerm)
                    QueuePointFlash(muzzle, anchor, MuzzleSecondaryOffset + at, near, far, tint);
                else
                    EmitLight(muzzle, anchor, MuzzleSecondaryOffset + at, far, tint);
            }
            return;
        }
        // The def's 3rdperson_lts variants: RANDOM_WEIGHT 0.333 / 0.333 / else, each a range band
        // and a colour; the range within the band is a random pick.
        float roll = _rng.Randf();
        float range;
        Color color;
        if (roll < 0.333f)
        {
            range = RandRange(1.0f, 2.0f);
            color = new Color(0.88f, 0.78f, 0.36f);
        }
        else if (roll < 0.667f)
        {
            range = RandRange(1.25f, 3.25f);
            color = new Color(0.93f, 0.78f, 0.36f);
        }
        else
        {
            range = RandRange(2.0f, 3.75f);
            color = new Color(0.93f, 0.78f, 0.36f);
        }
        EmitLight(muzzle, anchor, MuzzleSecondaryOffset, range, color);
    }

    // Lights one pooled OmniLight3D at the given offset in the muzzle frame for MuzzleLightLife.
    // An anchor node keeps it there while the flash lives; without one the light stays where it
    // was lit. Silently drops the flash when the pool is at cap, which is what a volley past
    // MaxMuzzleLights costs.
    private void EmitLight(Transform3D muzzle, Node3D? anchor, Vector3 local, float range, Color color)
    {
        LightFlash? slot = null;
        foreach (var l in _lights)
        {
            if (!l.InUse)
            {
                slot = l;
                break;
            }
        }
        if (slot == null)
        {
            if (_lights.Count >= MaxMuzzleLights)
                return;
            slot = new LightFlash
            {
                Light = new OmniLight3D
                {
                    ShadowEnabled = false,
                    LightEnergy = MuzzleLightEnergy,
                    Visible = false,
                },
            };
            AddChild(slot.Light);
            _lights.Add(slot);
        }
        slot.Light.OmniRange = range;
        slot.Light.LightColor = color;
        slot.Light.GlobalPosition = muzzle.Origin + (muzzle.Basis.Orthonormalized() * local);
        slot.Light.Visible = true;
        slot.Age = 0f;
        slot.InUse = true;
        slot.Anchor = anchor;
        slot.Local = local;
        slot.LitAt = slot.Light.GlobalPosition;
        slot.Frames = 0;
    }

    // Holds one first-person light for the point term. Drops it when the set is already full,
    // which is more than the shader can draw in one frame anyway.
    private void QueuePointFlash(Transform3D muzzle, Node3D? anchor, Vector3 local, float near, float far, Color color)
    {
        if (_pointFlashes.Count >= WorldLights.MaxActive)
            return;
        _pointFlashes.Add(new PointFlash
        {
            Anchor = anchor,
            Local = local,
            At = muzzle.Origin + (muzzle.Basis.Orthonormalized() * local),
            Near = near,
            Far = far,
            Color = color,
        });
    }

    // The point set's per-commit call. Each light is submitted through the one rendered frame of
    // its first commit, then dropped. The def's INACTIVE on the next event tick leaves it lit for
    // one drawn frame. It rides its firing node the way the omni flashes do.
    private void SubmitPointFlashes(WorldLights lights)
    {
        ulong frame = Engine.GetProcessFrames();
        for (int i = _pointFlashes.Count - 1; i >= 0; i--)
        {
            var f = _pointFlashes[i];
            if (f.Frame is { } lit && lit != frame)
            {
                _pointFlashes.RemoveAt(i);
                continue;
            }
            f.Frame = frame;
            lights.Add(AnchorPoint(f.Anchor, f.Local, out var at) ? at : f.At, f.Color, f.Near, f.Far);
        }
    }

    private void AgeCasings(float dt)
    {
        var spec = _casingSpec;
        if (spec == null)
            return;
        foreach (var c in _casings)
        {
            if (!c.InUse)
                continue;
            c.Age += dt;
            if (c.Age >= spec.RunTime)
            {
                c.InUse = false;
                c.Node.Visible = false;
                continue;
            }
            // Closed-form ballistic + tumble, exactly MotionRuntime's Seek: origin = start + v0·t
            // + ½·g·t², and the euler tumble composed onto the muzzle frame the launch was drawn
            // in, about that draw's own horizontal perpendicular.
            float t = c.Age;
            var origin = c.Start + c.V0 * t + new Vector3(0f, 0.5f * spec.Gravity * t * t, 0f);
            var basis = c.Basis * Basis.FromEuler(
                c.TumbleAxis * Mech3.Anim.MotionRuntime.TumbleAngle(spec.TumbleRate, spec.TumbleAccel, t),
                EulerOrder.Yxz);
            c.Node.GlobalTransform = new Transform3D(basis, origin);
        }
    }

    private void AgeLights(float dt)
    {
        foreach (var l in _lights)
        {
            if (!l.InUse)
                continue;
            l.Age += dt;
            if (l.Age >= MuzzleLightLife)
            {
                l.InUse = false;
                l.Light.Visible = false;
                // ⚠ Never difference the light against the muzzle here. This runs on the simulation
                // step and the light was placed on the drawn pose (docs/verification.md). Two
                // breadcrumbs per session give the metres the expiring light rode with the muzzle.
                if (_muzzleLightLogs < 2 && l.Frames > 0 && (GameClock.Current?.Time ?? 0.0) >= 2.0
                    && MuzzleFrame(l, out var at))
                {
                    _muzzleLightLogs++;
                    Log.Info("weapons", $"muzzle light: {l.Frames} drawn frame(s), rode the muzzle over {l.LitAt.DistanceTo(at):0.00} m of flight");
                }
                l.Anchor = null;
            }
        }
    }

    // Re-places every anchored muzzle light on the DRAWN pose of the node that fired it, the same
    // per-frame resolve RenderSprites makes for the flash quads. A light left where it was lit
    // hangs in world space, and at 100 m/s the aeroplane is metres past it before it goes out.
    private void PlaceAnchoredLights()
    {
        foreach (var l in _lights)
        {
            if (!l.InUse || !MuzzleFrame(l, out var want))
                continue;
            l.Frames++;
            l.Light.GlobalPosition = want;
        }
    }

    // A free (or freshly built) pooled casing instance, or null when the pool is at cap
    // or the chapter gamez lacks the `gunshell` prototype.
    private CasingSlot? AcquireCasing()
    {
        foreach (var c in _casings)
        {
            if (!c.InUse)
                return c;
        }
        if (_casings.Count >= MaxCasings)
            return null;
        if (!_casingProtoResolved)
        {
            _casingProtoResolved = true;
            _casingProto = _flyoutGamez!.FindByName("gunshell");
            if (_casingProto == null)
                Log.Info("weapons", $"gun casing 'gunshell' absent from this chapter gamez, no ejection");
        }
        if (_casingProto == null)
            return null;
        var inst = _flyoutScene!.BuildSubtree(_casingProto, skip: null, collisionSkip: _ => true);
        if (inst == null)
            return null;
        _casingModels.AddChild(inst);
        inst.Visible = false;
        // Verification breadcrumb (once): confirms the prototype's child mesh actually instanced,
        // the root itself is meshless (model_index -1); the shell rides one level below.
        if (!_casingLogged)
        {
            _casingLogged = true;
            Log.Info("weapons", $"gun casing 'gunshell' instanced: {CountMeshes(inst)} mesh(es)");
        }
        var slot = new CasingSlot { Node = inst };
        _casings.Add(slot);
        // Verification breadcrumb (once): sustained fire keeps this many casings alive at once,
        // per-shot pooled anchors, so nothing is dropped by a shared-anchor already-live gate.
        if (_casings.Count == 12)
            Log.Info("weapons", $"gun casings: 12 live simultaneously (gun-rate ejection, no shared-anchor gate)");
        return slot;
    }

    // Resolves the gunshell def's OBJECT_MOTION out of the anim program, once, gravity,
    // the ranged launch, the tumble (its `Time` value is a RATE, the MotionRuntime decode:
    // 20.94 rad/s about the launch's own perpendicular, whose length at gunshell's −75…−85° of
    // elevation is 0.06–0.17, so the casing turns at 1.3–3.6 rad/s) and the run time. Null without a program
    // or when the def is absent; the miss is cached and logged once.
    private CasingSpec? CasingSpecResolve()
    {
        if (_casingSpecResolved)
            return _casingSpec;
        _casingSpecResolved = true;
        if (_flyoutAnims == null || _flyoutGamez == null || _flyoutScene == null)
            return null;
        foreach (var def in _flyoutAnims.ByAnimName("gunshell"))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind != "ObjectMotion" || ev.Data.Obj("translation_range") is not { } range)
                        continue;
                    float runTime = ev.Data.Num("run_time") ?? 2f;
                    var fwd = ev.Data.Obj("forward_rotation")?.Obj("Time");
                    _casingSpec = new CasingSpec
                    {
                        Gravity = ev.Data.Obj("gravity")?.Num("value") ?? 0f,
                        XzMin = range.Obj("xz")?.Num("min") ?? 0f,
                        XzMax = range.Obj("xz")?.Num("max") ?? 0f,
                        YMin = range.Obj("y")?.Num("min") ?? 0f,
                        YMax = range.Obj("y")?.Num("max") ?? 0f,
                        SpeedMin = range.Obj("initial")?.Num("min") ?? 0f,
                        SpeedMax = range.Obj("initial")?.Num("max") ?? 0f,
                        RunTime = runTime,
                        TumbleRate = fwd?.Num("initial") ?? 0f,
                        TumbleAccel = fwd?.Num("delta") ?? 0f,
                    };
                    Log.Info("weapons", $"gun casing spec: gravity {_casingSpec.Gravity}, azimuth [{_casingSpec.XzMin},{_casingSpec.XzMax}]deg, elevation [{_casingSpec.YMin},{_casingSpec.YMax}]deg, speed [{_casingSpec.SpeedMin},{_casingSpec.SpeedMax}] m/s, tumble {_casingSpec.TumbleRate:0.##} rad/s (+{_casingSpec.TumbleAccel:0.##} rad/s²) over {runTime} s");
                    return _casingSpec;
                }
            }
        }
        Log.Info("weapons", $"gun casing 'gunshell' def not in the anim program, no ejection");
        return null;
    }

    // The exclusion list a round's hit ray carries: its shooter's own registered body,
    // so identity, not weapon, is what keeps a pilot's rounds off their own airframe, or a
    // world gunner's own mount bodies. An unowned round
    // (NoShooter, no owner) excludes nothing and can hit any plane.
    private Godot.Collections.Array<Rid> ExcludeFor(int shooter, Godot.Collections.Array<Rid>? owner)
    {
        if (shooter != NoShooter)
        {
            foreach (var a in _aircraft)
            {
                if (a.PlayerIndex == shooter)
                    return a.ExcludeSelf;
            }
        }
        return owner ?? NoExclude;
    }

    private float RandRange(float a, float b) => a + _rng.Randf() * (b - a);

    // A weapon's SOUND binding may name a SOUND_GROUPS entry rather than a plain sounds.json SETS
    // def; resolve it through the group first, same as WorldSounds.PlayOneShot, or the lookup
    // below misses. These players are non-positional, so splitscreen gain (MixGain, plus a
    // distance term against the nearest human) is applied by hand. The 0.2f factor is the
    // pre-existing tuned balance, carried rather than re-tuned.
    private void PlaySound(string sndName, Vector3 worldPos)
    {
        if (_sounds == null || _soundDefs == null)
            return;
        string resolved = _soundGroups != null && _soundGroups.TryGetValue(sndName, out var group)
            ? group.Pick(_soundGroupRng) ?? sndName
            : sndName;
        if (!_soundDefs.TryGetValue(resolved, out var def))
            return;
        var stream = _sounds.Find(def.WavName, looped: false);
        if (stream == null)
            return;
        var player = _sfxPool[_sfxNext];
        _sfxNext = (_sfxNext + 1) % _sfxPool.Count;
        player.Stream = stream;
        float nearest = NearestPlayerDistance(worldPos);
        float distanceGain = DistanceGain(nearest, def.RangeMin, def.RangeMax);
        float gain = def.Volume * 0.2f * MixGain * distanceGain;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.002f, gain));
        player.Play();
        // Verification breadcrumb: the first few one-shots confirm the computed
        // gain without needing a lucky --volume=0 listen, same convention as the impact fx/snd
        // breadcrumb above.
        if (_soundGainsLogged < 8)
        {
            _soundGainsLogged++;
            Log.Info("sound", $"sound gain: {resolved} MixGain={MixGain:0.00} dist={(nearest >= float.MaxValue ? "n/a" : Log.Format($"{nearest:0}m"))} range=[{def.RangeMin:0}-{def.RangeMax:0}]m distGain={distanceGain:0.00} vol={gain:0.000}");
        }
    }

    // The NEAREST PlayerPositions entry to `worldPos`,
    // D31's "nearest human" reading, C21's `PLAYER_RANGE` seam reused for audio.
    // float.MaxValue with nobody wired (the weapon bench, suite labs),
    // which DistanceGain reads as "skip the term".
    private float NearestPlayerDistance(Vector3 worldPos)
    {
        var positions = PlayerPositions?.Invoke();
        if (positions == null || positions.Count == 0)
            return float.MaxValue;
        float nearest = float.MaxValue;
        foreach (var p in positions)
        {
            float d = (p - worldPos).Length();
            if (d < nearest)
                nearest = d;
        }
        return nearest;
    }

    // The floor for one round across every bound viewer: the SMALLEST size that satisfies
    // `pixels` for any one of them, i.e. the nearest viewer's. One world-space
    // mesh is drawn in every pane, so no single size can satisfy them all; taking the minimum means
    // a round is never INFLATED for a pane whose camera is closer than the one it was sized
    // against, the splitscreen failure this avoids. Each viewer is measured with its own
    // pane height and FOV. 0 with no viewers bound (the weapon lab, the headless dumps).
    private float TracerFloor(Vector3 worldPos, float pixels)
    {
        _viewerScratch.Clear();
        foreach (var cam in Viewers.Cameras)
        {
            if (cam == null || !GodotObject.IsInstanceValid(cam))
                continue;
            float height = cam.GetViewport()?.GetVisibleRect().Size.Y ?? 0f;
            _viewerScratch.Add(new ScreenSize.ViewerSample((cam.GlobalPosition - worldPos).Length(), cam.Fov, height));
        }
        return ScreenSize.NearestFloor(pixels, _viewerScratch);
    }

    private void RenderTracers()
    {
        // The authored tracer (docs/org/tracers.md): a crossed pair, not billboarded or
        // camera-aligned. Config-driven look, read once per frame, a session-wide setting.
        float cfgLength = Config.GetFloat("weapons.tracerLength", TracerLength);
        float cfgWidth = Config.GetFloat("weapons.tracerWidth", TracerWidth);
        float minPixels = Config.GetFloat("weapons.tracerMinPixels", TracerMinPixels);
        System.Array.Clear(_tracerCounts, 0, _tracerCounts.Length);
        // Rounds step at 60 Hz and this draws every frame, so a raw sim position is held then
        // jumped while the camera moves smoothly. Draw part way through the current step instead
        // (CSVM.Utils.RenderPoses). The fraction is 1 outside a realtime clock.
        float tween = RenderPoses.Fraction;
        for (int i = 0; i < _projHigh; i++)
        {
            ref var p = ref _proj[i];
            if (!p.Alive)
                continue;
            // ⚠ Not Lerp at 1: a + (b - a) is not bit-identical to b in float, and a scripted
            // capture must reproduce the simulation position exactly.
            var drawPos = tween >= 1f ? p.Pos : p.PrevPos.Lerp(p.Pos, tween);
            // Carry the rocket body along with the round, nose down its velocity.
            var worldVel = WorldVelocity(in p);
            if (p.Model != null)
            {
                var pose = FlyoutPose(drawPos, worldVel);
                // The def's spinner (the sonic's ObjectMotion XYZ_ROTATION, 8.73 rad/s): a steady
                // roll about the round's own nose axis, pure roll, so the nose stays on velocity.
                if (p.RollRate != 0f)
                    pose = new Transform3D(pose.Basis * new Basis(Vector3.Back, p.RollRate * p.Age), pose.Origin);
                p.Model.GlobalTransform = pose;
                // Verification breadcrumb (once): read the APPLIED world basis back, through the
                // prototype's own parent chain, and confirm the body's nose (local -Z) actually
                // aligns with the round's flight direction. dot≈1 ⇒ nose-forward.
                if (!_flyoutPoseLogged)
                {
                    _flyoutPoseLogged = true;
                    var nose = -p.Model.GlobalTransform.Basis.Z.Normalized();
                    var vdir = worldVel.Normalized();
                    Log.Info("weapons", $"flyout orientation: nose·velocity = {nose.Dot(vdir):0.000} (1.000 = nose-forward)");
                }
            }
            // Ordnance draws no streak and no tip: its FLYOUT prototype is a missile body, and its
            // trail is the MODEL_ANIMATION puffer smoke its def instance runs.
            if (p.Weapon.IsRocket)
                continue;
            var yAxis = worldVel.Normalized();
            // The two width axes. Any pair ⟂ to the flight direction will do, the crossed mesh
            // reads the same from every angle, which is exactly why the original consults no camera
            // here and why nothing in this basis depends on the eye any more.
            var zAxis = yAxis.Cross(Mathf.Abs(yAxis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up).Normalized();
            var xAxis = yAxis.Cross(zAxis).Normalized();
            // ⚠ This is the one place this pool knowingly contradicts the decode: the authored LOD
            // stops drawing past 600 m, while this floor keeps inflating it. See docs/org/tracers.md.
            float floorSize = TracerFloor(drawPos, minPixels);
            float width = Mathf.Max(cfgWidth, floorSize);
            float len = Mathf.Max(cfgLength, floorSize);
            // The mesh's local +Y is its front; the round's position is the streak's TAIL, hence
            // the +half-length origin offset below.
            var basis = new Basis(xAxis * width, yAxis * len, zAxis * width);
            var mm = _tracerMm[p.TracerIdx];
            int n = _tracerCounts[p.TracerIdx]++;
            mm.SetInstanceTransform(n, new Transform3D(basis, drawPos + yAxis * (len * 0.5f)));
            mm.SetInstanceColor(n, p.Tint);
            // The tip disc: perpendicular to flight (its quad spans the two width axes, its normal
            // is the flight direction), TracerTipOffset ahead of the round. Same instance index as
            // the streak, so one count array serves both pools.
            var tip = _tipMm[p.TracerIdx];
            tip.SetInstanceTransform(
                n,
                new Transform3D(new Basis(xAxis * TracerTipSize, zAxis * TracerTipSize, yAxis), drawPos + yAxis * TracerTipOffset));
            tip.SetInstanceColor(n, p.Tint);
        }
        for (int i = 0; i < _tracerMm.Length; i++)
        {
            _tracerMm[i].VisibleInstanceCount = _tracerCounts[i];
            _tipMm[i].VisibleInstanceCount = _tracerCounts[i];
        }
    }

    // One splash candidate: a registered plane (Plane set, its nearest hull box) or a world body
    // the blast sphere overlaps (Body set). DistanceSq is the squared distance from the burst to
    // the candidate's own surface, and Centre is where the cover ray aims: a plane's position, a
    // world body's struck-shape bounds centre (BlastCentre).
    private struct BlastCandidate
    {
        public AircraftBody? Plane;
        public Node? Body;
        public Rid Rid;
        public int ShapeIdx;
        public Vector3 NearPoint;
        public Vector3 Centre;
        public float DistanceSq;
    }

    /// <summary>One row of <see cref="BlastCoverCensus"/>: a world body the burst sphere overlapped,
    /// the point the cover ray aimed at, its surface distance from the burst, and the node that
    /// stopped the ray (null when the body is clear, or when the cover has no node).</summary>
    internal readonly record struct BlastCoverRow(Node? Body, Vector3 Centre, float Distance,
        Node? Cover, bool Covered);

    private struct Proj
    {
        public bool Alive;
        public Vector3 Pos;
        public Vector3 PrevPos;  // where the round stood one sim step ago. The render pass draws
                                 // between the two: rounds step at 60 Hz while the camera moves
                                 // every frame, so a torpedo drawn raw steps against it.
        public Vector3 Vel;      // m/s, world, the round's OWN velocity (heading × speed), which
                                 // ACCELERATION raises; the launcher's share rides in Inherited
        public Vector3 Inherited; // the launcher's velocity copied at spawn (FUN_005aef40's
                                  // +0x30..+0x38); zero for ordnance carrying no LOCK_ON
        public object? Target;   // what this round holds as its target, the second half of the
                                 // steering step's gate, filled at spawn and, on a seeker, replaced
                                 // every frame by RetargetSeeker. Typed loosely because the
                                 // original's slot takes an aircraft, an emplacement or a
                                 // zeppelin sub-part alike (TargetPosition/TargetVelocity read it).
        public float Travelled;  // m of path flown so far, the original's accumulator at round
                                 // +0x664, read by both the RANGE end condition and the reveal gate
        public float Range;      // m of path this round may fly (RANGE, or DefaultRange unauthored)
        public bool IntersectOff; // node flag 0x10 clear: the RANGE_MINIMUM gate has not passed yet
        public float Accel;      // ACCELERATION along the velocity direction, m/s²
        public float Cap;        // the own speed ACCELERATION climbs to and stops at, m/s, seeded
                                 // with Vel at spawn (Ballistics.LaunchSpeed)
        public float Grav;       // GRAVITY, m/s² of vertical acceleration on the round's own
                                 // velocity (0 throughout this install, so inert as shipped)
        public WeaponDef Weapon;
        public Color Tint;       // tracer brightness multiplier (uniform, TracerTint)
        public int TracerIdx;    // which TracerTextures entry/MultiMesh this round's streak draws into
        public Flyout? Shootable; // the FLYOUT_HEALTH pair and the TARGETABLE admission byte; null
                                  // for a round carrying neither key (SeedFlyout)
        public Vector3 HitCentre; // the hittable box, in the round's own flyout pose: the FLYOUT
        public Vector3 HitHalf;   // MODEL's AABB. Zero when nothing is hittable (no body to strike)
        public Node3D? Model;    // the FLYOUT MODEL body (rockets only; null for gun tracers)
        public FlyoutRig? Rig;   // the running FLYOUT MODEL_ANIMATION instance and what it drives
        public float RollRate;   // rad/s about the nose axis (the sonic spinner); 0 = no roll
        public float Age;        // s since launch, drives the roll angle
        public int Shooter;      // who fired it (PlayerIndex); NoShooter when nobody owns it
        public int Team;         // stamped at spawn from the shooter's own Team (B7), not re-derived
        public Godot.Collections.Array<Rid>? Owner; // the world bodies that fired it (an emplacement's
                                                    // own mount): out of its hit ray and its splash
    }

    private struct Sprite
    {
        public Vector3 Pos;
        public float Age;
        public float Life;
        public float Size;
        public Color Tint;
        public Basis Orient;   // unit quad orientation: X width, Y height, Z the facing normal.
                               // Muzzle flashes roll in the firing plane's basis; impact sprites
                               // face the struck surface normal, a fixed world plane for neither.
        public Vector3 Vel;    // m/s, world; zero for every sprite but smoke
        public bool NoGravity; // smoke puffs drift on their spawn velocity; sparks arc (false)
        public bool AnchorLeft; // Pos is the texture's left edge (UV x=0), not the quad centre,
                                // the muzzle flash triad; the centre is derived in RenderSprites
                                // from the *current* (shrinking) size so the anchor doesn't drift.
        public Node3D? Anchor;  // when set, Pos/Orient are LOCAL to this node and resolve to world
                                // per frame, the muzzle flash rides the firing plane; null keeps
                                // the sprite world-fixed (impacts, smoke, debris)
    }

    // A named IMPACT effect that resolved to a chapter-gamez MODEL prototype (the authored water
    // splash `splash1.flt`/`bsplsh.flt`), instanced at the hit point. A splash model's
    // `*_base`/`*_splash` children are driven along the authored scale curves for the def's 2 s
    // run; a model with neither child just shows briefly.
    private struct ImpactFx
    {
        public Node3D Model;
        public float Age;
        public float Life;
        public Node3D? Base;        // the `*_base` disc child (null: not a splash model)
        public Node3D? Splash;      // the `*_splash` column child
        public Transform3D BaseRest;
        public Transform3D SplashRest;
        // The children's own mesh instances, resolved once at spawn so the fade
        // drives SetInstanceShaderParameter directly each tick instead of re-walking the tree.
        // Null exactly when the corresponding Base/Splash is null, or its fade twin failed.
        public MeshInstance3D? BaseMesh;
        public MeshInstance3D? SplashMesh;
    }

    /// <summary>One round's shootable-flyout state: the armour/health pair the spawn seeds at round
    /// <c>+0x670</c>/<c>+0x674</c>, and the admission byte <c>TARGETABLE</c> sets
    /// (<c>FUN_00441830</c>). It is a CLASS because the player's target registry re-finds a
    /// selection by source object every frame and a pooled round is a slot in a struct array with
    /// no identity of its own; a round carrying neither key gets none of this
    /// (<see cref="SeedFlyout"/>).</summary>
    public sealed class Flyout
    {
        /// <summary>The not-shootable sentinel both pools take without <c>FLYOUT_HEALTH</c>. Kept
        /// as −1.0 rather than a flag because it is what makes <see cref="Destroyed"/>'s equality
        /// against zero safe on a round that was never shootable.</summary>
        public const float NotShootable = -1f;

        internal Flyout(WeaponDef weapon, int slot)
        {
            Weapon = weapon;
            Targetable = weapon.Targetable;
            Name = $"{weapon.Id}#{slot}";
            // FUN_005ad630 at 0x005ae164: the armour pool is the literal 0 the parser writes, with
            // no key feeding it, so the first hit spends health directly. Implemented as a pool
            // rather than dropped, because the spend ORDER below is what the decode fixes.
            HealthMax = weapon.FlyoutHealth is { } hp and > 0 ? hp : NotShootable;
            Health = HealthMax;
            Armour = weapon.FlyoutHealth is > 0 ? 0f : NotShootable;
        }

        /// <summary>The weapon this round flies.</summary>
        public WeaponDef Weapon { get; }

        /// <summary>The admission byte at the target wrapper's <c>+0x6c</c>: a <c>TARGETABLE</c>
        /// round is selectable, a merely fused one is on the same list with the byte clear.</summary>
        public bool Targetable { get; }

        /// <summary>This round's identity string, what <c>--target=</c> matches.</summary>
        public string Name { get; }

        /// <summary>The armour pool (round <c>+0x670</c>), always 0 as shipped.</summary>
        public float Armour { get; private set; }

        /// <summary>The health pool (round <c>+0x674</c>).</summary>
        public float Health { get; private set; }

        /// <summary><c>FLYOUT_HEALTH</c>, or <see cref="NotShootable"/>.</summary>
        public float HealthMax { get; }

        /// <summary>False once the round has left the pool, so a stale selection drops.</summary>
        public bool Live { get; internal set; } = true;

        /// <summary>Whether the per-frame test <c>FUN_005af720</c> runs before anything else has
        /// tripped: health exactly zero. A sentinel round never satisfies it.</summary>
        public bool Destroyed => Health == 0f;

        /// <summary>Spends one hit's pair, armour then health (<c>FUN_005abcf0</c>): each pool is
        /// clamped at zero, and health is only touched once armour is empty, which, with the
        /// armour pool shipped at 0, is the same call.
        /// ⚠ Never merge this with <c>PlaneDamage.Spend</c>. That is <c>FUN_004b7f80</c>, a richer
        /// routine with an armour-shielded share; the flyout branch is the plain clamp-and-subtract
        /// pair and the engine keeps the two apart.</summary>
        public void Spend(float armourDamage, float healthDamage)
        {
            if (HealthMax <= 0f)
            {
                return;     // the −1.0 sentinel: this round is not shootable at all
            }

            Armour = Mathf.Max(0f, Armour - armourDamage);
            if (Armour == 0f)
            {
                Health = Mathf.Max(0f, Health - healthDamage);
            }
        }
    }

    // One reusable trail emitter: a Puffer built for one authored PUFFER_STATE, owned by the pool
    // and lent to one live round at a time (see AcquireEmitter).
    private sealed class TrailEmitter
    {
        public Puffer Puffer = null!;
        public PufferState State = null!;
        public bool InUse;
    }

    // One choker cloud (FUN_004b94e0's 0x1c-byte object): the burst it was left at, its RADIUS both
    // raw (the duration's denominator) and squared (the catch test), and its remaining TIME.
    private sealed class TanglerCloud
    {
        public Vector3 Centre;
        public float RadiusRaw;
        public float RadiusSq;
        public float Remaining;
    }

    // The gunshell def's OBJECT_MOTION, read once from the anim program: the authored
    // ranged ballistic launch + tumble every ejected casing flies.
    private sealed class CasingSpec
    {
        public float Gravity;     // m/s², negative (LOCAL -3.0)
        public float XzMin, XzMax; // launch AZIMUTH range, degrees (translation_range)
        public float YMin, YMax;   // launch ELEVATION range, degrees (negative = downward)
        public float SpeedMin, SpeedMax; // launch speed range, m/s (translation_range.initial)
        public float RunTime;     // s the casing lives
        public float TumbleRate, TumbleAccel;  // forward_rotation.Time, rad/s and rad/s²
    }

    // One pooled casing: a gunshell subtree instance lent to one ejection at a time.
    private sealed class CasingSlot
    {
        public Node3D Node = null!;
        public Vector3 Start;   // launch position (world)
        public Vector3 V0;      // launch velocity (world), m/s
        public Basis Basis;     // launch orientation (the muzzle frame the direction was drawn in)
        public Vector3 TumbleAxis;  // this draw's own perpendicular, MotionRuntime.TumbleAxis
        public float Age;
        public bool InUse;
    }

    // One pooled muzzle-light flash: a real OmniLight3D shown for a couple of frames per shot.
    private sealed class LightFlash
    {
        public OmniLight3D Light = null!;
        public float Age;
        public bool InUse;
        public Node3D? Anchor;  // the node that fired: Local resolves against it every drawn frame,
                                // so the flash rides the aeroplane. Null leaves the light in world
                                // space, which is right only for a muzzle with no node behind it
        public Vector3 Local;   // the light's placement in the anchor's own frame
        public Vector3 LitAt;   // where the muzzle stood when it was lit, for the placement breadcrumb
        public int Frames;      // drawn frames this flash has lived, for the placement breadcrumb
    }

    // One first-person muzzle light held for the point term (SubmitPointFlashes).
    private sealed class PointFlash
    {
        public Node3D? Anchor;  // the node that fired, resolved at each commit like LightFlash's
        public Vector3 Local;   // the light's placement in the anchor's own frame
        public Vector3 At;      // where it was lit, used when the anchor is gone
        public float Near, Far; // the authored range pair, weight 1 inside Near and 0 past Far
        public Color Color;
        public ulong? Frame;    // the process frame of its first commit; null until then
    }
}
