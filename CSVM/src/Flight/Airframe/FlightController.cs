using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Bindings;
using CSVM.Effects;
using CSVM.Flight.Ai;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Sticks;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>How far a seat's controls are held back while the world flies on around an ending the
/// player watches out. The two settings differ only in the stick. Both swallow the discrete
/// flight commands, and only <see cref="All"/> also takes the aeroplane's attitude away.
/// ⚠ Neither is a halt. Physics, the wreck's fall, weapons already in the air and the cameras
/// all carry on, which is the point.</summary>
public enum FlightControlHold
{
    /// <summary>Nothing is held; the seat answers everything.</summary>
    None,

    /// <summary>Every discrete flight command (the two triggers, the weapon selectors, respawn)
    /// is swallowed. No crash-cam timer brings the pilot back. The stick and the throttle still
    /// fly the aeroplane.</summary>
    CommandsOnly,

    /// <summary>The above, and the stick reads neutral over the lever the pilot left, so the
    /// aeroplane flies on as trimmed.</summary>
    All,
}

/// <summary>
/// The flying aircraft: polls keyboard + gamepad into a <see cref="FlightModel"/>, applies the
/// result to this node's transform, and drives the chase camera plus a minimal text HUD.
/// Controls: docs/controls.md. Collaborators and other constraints: this module's entry in
/// docs/architecture.md.
/// Hitting terrain or a building crashes the plane: explosion, airframe hidden, frozen at the
/// impact point until the seat's own Respawn control brings it back, which its crash prompt names.
/// ⚠ P (or gamepad Start) halts the WHOLE simulation, not just this plane; see
/// <see cref="PauseTogglePressed"/>.
/// </summary>
public partial class FlightController : Node3D
{
    /// <summary><c>weapons.gunAmmoCap</c> (config.json) default: 0 = off, full stock gun capacity.
    /// A positive value caps every firable gun group's load to that many rounds, a testing knob for
    /// the low-ammo cases (chiefly the on-empty group hand-off) that thousands of stock rounds make
    /// tedious to reach. Capped at each group's real capacity; the gauge reads full at the cap.</summary>
    public const int GunAmmoCapDefault = 0;

    /// <summary><c>weapons.ordnanceCap</c> (config.json) default: 0 = off, full stock pylon load,
    /// the same knob as <see cref="GunAmmoCapDefault"/>, for hardpoints instead of gun groups.</summary>
    public const int OrdnanceCapDefault = 0;

    /// <summary>Own-plane sound, if the sound archive was found (add as a child too).</summary>
    public FlightAudio? Audio;

    /// <summary>The positional twin of <see cref="Audio"/>, carried by an AI-flown aircraft instead
    /// of it: engine loops on 3D emitters, culled by distance. Never both, own-ship audio is
    /// non-positional by design, and a rig with a person in it takes <see cref="Audio"/>.</summary>
    public AiEngineAudio? EngineAudio;

    /// <summary>The weapon half of that positional pair, again carried by an AI-flown aircraft
    /// instead of <see cref="Audio"/>: the gun loop and the dry cue on 3D emitters, culled by each
    /// cue's own authored audible distance. Separate from <see cref="EngineAudio"/> because that
    /// component's contract is the engine slots alone.</summary>
    public AiWeaponAudio? WeaponAudio;

    /// <summary>The visible aircraft model (a child of this node); hidden while crashed.</summary>
    public Node3D? PlaneModel;

    /// <summary>The per-mode hiding of this pilot's OWN aircraft while a first-person view is on
    /// the screen (interior in, body out; Nose also drops markers/dontmove). Null on every rig that
    /// was not built an interior, AI planes and the labs, which then never hides anything.</summary>
    public CockpitVisibility? Cockpit;

    /// <summary>The authored instrument panel inside that interior: needles and the two warning
    /// lamps, driven off the same readings the screen-space cluster draws. Null wherever
    /// <see cref="Cockpit"/> is.</summary>
    public CockpitGauges? CockpitPanel;

    /// <summary>The <c>cockpit1</c> subtree itself, as the plane builder returned it. Null wherever
    /// <see cref="Cockpit"/> is.</summary>
    public Node3D? CockpitInterior;

    /// <summary>The interior's own render pass, which takes <see cref="CockpitInterior"/> out of
    /// the plane model and draws it at the origin. Null under <c>--no-cockpit-pass</c>, when the
    /// interior renders in the main world instead.</summary>
    public CockpitOverlay? CockpitPass;

    /// <summary>The wobble oscillators and the pivot they roll, the node the assembler hung
    /// <see cref="PlaneModel"/> under. Null when no rig assembly ran (parked lab planes).</summary>
    public PlaneShake? Shake;
    public Node3D? ShakePivot;

    /// <summary>Spins the plane's propeller/rotor blur discs; advanced each frame,
    /// throttle-scaled. Null if the model has no propeller nodes.</summary>
    public PropAnimator? Props;

    /// <summary>Flashes the plane's wingtip flares on the original's 1.5 s cycle; advanced
    /// each frame. Null if the model has no wing-flare nodes.</summary>
    public WingLightBlinker? WingLights;

    /// <summary>The exhaust smoke a commanded lever ahead of the live one streams; advanced on
    /// each sim step, the clock the lever slews on. Null if the model has no exhaust nodes.</summary>
    public ExhaustSmoke? ExhaustSmoke;

    /// <summary>The chapter-authored pale speed wisps spawned ahead of this aircraft; one private
    /// instance per rendered player view.</summary>
    public SpeedCue? SpeedCue;

    /// <summary>Deflects the plane's ailerons/elevators/rudders with stick input;
    /// advanced each frame. Null if the model has no control-surface nodes.</summary>
    public ControlSurfaceAnimator? Surfaces;

    /// <summary>The airframe collision boxes (fuselage/wings/tail), swept along each
    /// physics frame's motion so wingtips and tail collide with obstacles. Null falls
    /// back to the center-ray-only test.</summary>
    public PlaneCollider? Collider;

    /// <summary>The airframe's physics body on the aircraft collision layer, built in
    /// <c>_Ready</c> from <see cref="Collider"/>'s own boxes: what a projectile ray or another
    /// plane's sweep strikes. This plane's own queries exclude it (<see cref="AircraftBody.ExcludeSelf"/>).
    /// Null when no collider boxes could be derived, the plane is then unhittable.</summary>
    public AircraftBody? Body;

    /// <summary>Per-part hit points from the vehicle def's destroyable_parts.
    /// When set, collisions below the crash threshold damage the struck
    /// part and the plane flies on; null means any hit crashes.</summary>
    public PlaneDamage? Damage;

    /// <summary>The livery this aircraft's model was painted with, set once by whichever assembler
    /// built it. Read back by an airframe swap that needs to carry a captured rig's own scheme
    /// onto the rig it rebuilds (docs/formats/anim-definitions/cutscenes.md).</summary>
    public PaintScheme? Scheme;

    /// <summary>Whether this aircraft was built under the ShippedSkins reading. That is a roster
    /// spawn on a team other than the player's, which draws no default pattern of its own. Its own
    /// def's authored livery still reaches it. A swap has to carry this alongside
    /// <see cref="Scheme"/>. A captured rig resolved this way and painted nothing is not "no scheme
    /// carried". The rebuild must not fall back to this pilot's own default livery either.</summary>
    public bool ShippedSkins;

    /// <summary>The painter that composited <see cref="Scheme"/> onto this aircraft's skins, null
    /// when it flies the bare shipped textures. Exposed so a swap suite can read the actual
    /// painted output a rebuild carries, rather than comparing the scheme record alone.</summary>
    public PlanePainter? Painter;

    /// <summary>Applies a plane collision's health damage to the struck world node, returning true
    /// iff it was a <c>WeaponOrCollideHit</c> destructible (the 44 facades/windows/agyrobus), in
    /// which case the object breaks and the plane flies THROUGH it. EVERY destructible takes the
    /// damage; the return value is the plane's fate alone. Wired to
    /// <c>AnimRuntime.CollideDamageAt</c>; null (a viewer/static build with no world runtime)
    /// makes every collision solid and harmless.</summary>
    public System.Func<Node?, float, bool>? CollideDamageSink;


    /// <summary>Plays a named effect def at a world point through the session's world-effects
    /// runtime, the survivable graze's authored <c>touchdown_*</c> reaction. Same sink shape
    /// as <c>ProjectilePool.EffectSink</c>; null (no world, or a build with no effects runtime)
    /// leaves the scrape's sound without its sparks/dust/splash.</summary>
    public System.Action<string, Vector3>? GrazeEffectSink;

    /// <summary>The graze family's def vector, the session's ONE <c>touchdown_*</c> table
    /// (<c>WorldEffectsFactory.TouchdownDefs</c>), indexed by the scraped material's surface id by
    /// the same cascade <see cref="CrashDefs"/> uses. Null on a build with no world-effects runtime,
    /// which leaves a scrape with neither its def nor its bark, exactly as the original leaves it
    /// when the vector cannot answer, because the sound is authored inside the def.</summary>
    public SurfaceDefTable? TouchdownDefs;

    /// <summary>Visible damage: torn-skin pdpanel flips + the authored damage-stage anims
    /// (panel burns, fuel leak, heavy prop1 trail), driven from the data's injure_anims
    /// thresholds through the rig runtime. Optional.</summary>
    public DamageVisuals? Visuals;

    /// <summary>This plane's stock loadout bound to its model, the gun groups (with independent
    /// ammo counters) + hardpoints the firing code draws from. Null disables weapons.</summary>
    public Loadout? Loadout;

    /// <summary>The shared world's projectile/effect pool guns and hardpoints fire into. Null
    /// disables weapons.</summary>
    public ProjectilePool? Projectiles;

    /// <summary>The FLYOUT-model rockets mounted under the wings, one per loaded pylon, hidden as
    /// each pylon's ammo depletes. Rides the plane; null when nothing could be mounted (viewer, or a
    /// chapter gamez lacking the prototype roots).</summary>
    public PylonOrdnance? Ordnance;

    /// <summary>The world's smoke screens, which a <c>SMOKE_SCREEN</c> pylon lays into instead of
    /// spawning a round. Null in a session that runs no screens (the weapon lab, a suite): the
    /// weapon then still spends its ammo and lays nothing.</summary>
    public SmokeScreens? SmokeScreens;

    /// <summary>--infinite-ammo: guns/hardpoints fire without depleting (frictionless testing).</summary>
    public bool InfiniteAmmo;

    /// <summary>--ammo=N: overrides both <c>weapons.gunAmmoCap</c> and <c>weapons.ordnanceCap</c> at
    /// rig build, so the low-ammo start survives <c>--det</c> (which drops config.json entirely).
    /// Null when the flag was absent, falls through to the config.json knobs.</summary>
    public int? AmmoCapOverride;

    /// <summary>--fire: hold the gun trigger down (scripted screenshot / soak runs), as a scripted
    /// hold profile does for flight input.</summary>
    public bool AutoFire;

    /// <summary>--fire-rockets: hold the rocket trigger down (scripted screenshot / soak runs).
    /// Unlike a human pull (one rocket per press), this auto-repeats at the launch cooldown.</summary>
    public bool AutoFireRockets;

    /// <summary>Forces <see cref="AutoLandPressed"/> true, the way <see cref="AutoFire"/> forces the
    /// gun trigger: a suite's twin for the auto-land button, with no live key or pad to press.</summary>
    public bool AutoLand;

    /// <summary>Holds the nitro command down, the way <see cref="AutoLand"/> holds the auto-land
    /// button: a suite's twin for the N key, so a rig built the way a session builds one can reach
    /// the engage edge through the production step instead of calling the animation runtime itself.
    /// A held command is what the original's own arm reads, so holding it is not a shortcut past
    /// the state machine.</summary>
    public bool AutoNitro;

    /// <summary>Whether <c>LandingApproachRuntime</c>'s auto row currently passes for this
    /// aircraft, fed once a frame by the session that owns the trigger. Drives the HUD prompt.</summary>
    public bool AutoLandOffered;

    /// <summary>--gun-select=N: the gun selector's initial firable group (0-based; 0 = the first
    /// group, the default). Only one gun group fires at a time. A headless testing hook so a scripted
    /// run can fire one group in isolation; interactively the selector cycles with F3 / gamepad D-pad Right.</summary>
    public int InitialGunSelect;

    /// <summary>This airframe's DESTROY def, the anim slot the original starts the instant health
    /// reaches zero (<c>fury-fury</c>, <c>player-player</c>), where <see cref="CrashDefs"/> is the
    /// GROUND-IMPACT slot. Set alongside <see cref="CrashRuntime"/>; null leaves a kill with no
    /// wreck at all, which is what hiding the airframe on the death frame used to look like.</summary>
    public string? DestroyDef;

    /// <summary>Whether <see cref="DestroyDef"/> takes the hull over itself (an <c>ObjectMotion</c>
    /// on <c>MAIN_ROOT_NODE</c>, with its own bounce landing), true on all eleven airframe defs,
    /// false on <c>player</c>. It says who LANDS the wreck, not who flies it: every dead hull flies
    /// itself until the def's <c>Callback 15</c>. Derived by <c>EffectCatalogue.FliesOwnHull</c>.</summary>
    public bool DestroyDefFliesWreck;

    /// <summary>The stunt run, when flying --stunt: danger-zone sphere
    /// detection, tested against the plane each physics frame. Deliberately NOT reset on
    /// respawn, a mid-run crash keeps completed zones (the clock keeps running).
    /// Null in free flight.</summary>
    public StuntMission? Stunt;

    /// <summary>This pane's Danger Zone camera: one latched photograph per marker per run, stepped
    /// from <see cref="SimStep"/> beside the run itself. Null in free flight and on every rig with
    /// no pane of its own to photograph.</summary>
    public StuntCapture? StuntShots;

    /// <summary>The eye both Danger Zone writers photograph this pilot through, posed ahead of the
    /// aircraft rather than at the pilot's view. Null on every rig with no pane of its own.</summary>
    public DangerZonePhotograph? Photograph;

    /// <summary>The end-of-run results overlay: splits + total + best-time on
    /// AllComplete. Added to the HUD canvas last (drawn over the marker/dials); wakes itself on
    /// the run's RunCompleted. Null in free flight and in Instant Action, whose pilot flies on past
    /// the finish. A bare <see cref="Control"/> because the board is UI's own type, which the
    /// flight layer holds and attaches but never names.</summary>
    public Control? Scoreboard;

    /// <summary>The Dogfight per-pane HUD, <c>--vs</c> only: the match timer/K-D/leader line, the
    /// kill banner, and the opponent markers. Added to the HUD canvas; fed nothing per frame (it
    /// pulls VersusMatch's own live state) beyond the kill facts GameSession pushes through its
    /// OnKill.</summary>
    public VersusHud? VersusHud;

    /// <summary>The splitscreen stunt race this plane is one seat of, or null when
    /// flying solo and in Instant Action, whose pilots fly on through the ending. Set, clearing every zone parks this player at the finish while the others fly
    /// on, and R only becomes a rematch once the whole field is in, a rematch restarts every
    /// player, so it goes through <see cref="RestartRace"/> rather than this plane alone.</summary>
    public StuntRace? Race;

    /// <summary>Restarts the whole race (the session owns every player's plane, so it does the
    /// work). Invoked when a player presses R on the shared results board.</summary>
    public Action? RestartRace;

    /// <summary>The dogfight this plane is one seat of, or null outside <c>--vs</c>. Set, once
    /// <see cref="VersusMatch.Completed"/> the results board is up and any player's R there means
    /// "rematch" instead of "respawn me", checked before the crash branch, exactly the same
    /// R-ownership rule <see cref="Race"/>/<see cref="RestartRace"/> already follow (the board
    /// owns R only while it is visible, which mirrors <c>Completed</c> exactly).</summary>
    public VersusMatch? Match;

    /// <summary>Restarts the whole match (the session owns every player's plane, so it does the
    /// work): scores and clock reset, every plane respawns. Invoked when a player presses R on the
    /// dogfight results board.</summary>
    public Action? RestartMatch;

    /// <summary>Where this seat comes back, asked once per <see cref="Respawn"/>. It gives a world
    /// position and a point to aim the nose at, or null to keep the pose it has. Null itself, the
    /// default, leaves every respawn on the spawn <see cref="Setup"/> fixed, which is what every
    /// mode but the dogfight wants. Set by the session, which owns the field the choice is made
    /// against.</summary>
    public Func<(Vector3 Pos, Vector3 LookAt)?>? RespawnPlacement;

    /// <summary>Splitscreen pause bookkeeping, the SAME instance on every rig
    /// (assigned by <c>GameSession</c>, the same way <see cref="Match"/> is), so any player's
    /// Start/P here can pause everyone but only <see cref="PauseState.OwnerPlayerIndex"/> can
    /// resume. Null only where no session builds one (the suites' bare rigs), in which case
    /// <see cref="AllowPause"/> is always false there too, so <see cref="PauseTogglePressed"/>
    /// never fires and the null is never read.</summary>
    public PauseState? PauseState;

    /// <summary>0-based player index, this plane's seat in the race and its pane, and the
    /// identity a round it fired carries (<c>ProjectilePool.Spawn</c>'s shooter id).</summary>
    public int PlayerIndex;

    /// <summary>This pilot's target selection, or null for a seat that
    /// does no targeting (every AI rig, and the suites' bare rigs). Set by
    /// <c>HumanFlightAdapter</c> on each human pane; <see cref="StepTargeting"/> feeds it every
    /// frame. Read <c>Targeting.Current</c> for the selected target, that is the property the
    /// marker, Track Target's camera and any later AI-order consumer are meant to read.</summary>
    public TargetSelection? Targeting;

    /// <summary>Appends the zeppelin sub-parts to the targeting pool each frame,
    /// <c>ZeppelinRuntime.CollectTargetParts</c>, bound by <c>GameSession</c> once the zeppelins
    /// exist. Null in a session with none, and the pool takes them only under the torpedo gate
    /// (<see cref="TargetPool.Rebuild"/>). A delegate because the zeppelins are BUILT AFTER the
    /// rigs; the same sink shape <see cref="CollideDamageSink"/> already uses.</summary>
    public System.Action<List<AimCandidate>>? TargetSubParts;

    /// <summary>Appends this pilot's live objective sites to the targeting pool each frame: the
    /// campaign mission's (<c>ObjectiveSites.Collect</c>, bound by <c>GameSession</c>) or a stunt
    /// run's unflown Danger Zones (<see cref="StuntMission.CollectTargets"/>, bound per pane). Null
    /// in a session with neither. A channel of its own rather than <see cref="TargetSubParts"/>:
    /// a site carries the mission's own flag, onto the Enemy cycle or the Non-Aircraft one, where a
    /// sub-part is offered under a flag nothing authors for it.</summary>
    public System.Action<List<AimCandidate>>? TargetObjectives;

    /// <summary><c>--target=</c>'s spec, or null for an unscripted session. Applied ONCE, on
    /// the first frame <see cref="Targeting"/>'s pool has anything in it, and never consulted again,
    /// it sets the initial selection, it does not hold it, so an interactive session started with the
    /// flag still cycles normally.</summary>
    public string? InitialTarget;

    /// <summary>Whether a person is flying this plane. Gates the gun aim assist
    /// (B6): true runs <see cref="AimAssist"/> as normal, false takes the muzzle axis
    /// unassisted, the same fallback a barrel with no slot already uses. Defaults true; the AI
    /// spawner sets it false. It is the original's human-versus-AI split (`FUN_004b6530`'s
    /// else-branch), not "pane 1 only".</summary>
    public bool IsHumanPiloted = true;

    /// <summary>This plane's carried turret gunners: built by the rig assembler from the
    /// vehicle def's <c>turrets</c> block against <c>ai.zrd</c>, ticked from <see cref="SimStep"/>
    /// (so a crash silences them), and collected into every shooter's aim-assist candidate set
    /// through <see cref="ProjectilePool.CollectTurrets"/>. Empty on the six turretless airframes.</summary>
    public TurretController[] Turrets = Array.Empty<TurretController>();

    /// <summary>The non-player input source: set (with <see cref="IsHumanPiloted"/> false), it
    /// replaces the keyboard/pad read each sim step, the way a scripted hold profile does for
    /// scripted runs, collision, weapons, damage and crash downstream are byte-for-byte the
    /// player's path. ⚠ The FLIGHT MODEL is the one exception: construction selects the AI force
    /// path off this same split (<see cref="FlightModel.UsesAiForcePath"/>).</summary>
    public AiPilot? Pilot;

    /// <summary>The nitro boost lifecycle. <see cref="NitroSystem.Installed"/> is the build's
    /// (the hangar's nitrous engine pick, or the AI spawn's roster flag); the command arm, the
    /// AI maneuver arm, the tank and the animation edges run from <see cref="SimStep"/>.</summary>
    public NitroSystem Nitro = new();

    /// <summary>The flown tank, burned by <see cref="ReadKeyboard"/> and refilled at every spawn.
    /// Only the human lever path touches it, which is the original's player-only gate; a pilot-flown
    /// aircraft leaves it full. Nitro burns nothing here: the burn reads the lever, not the boost
    /// flag.</summary>
    public FuelTank Fuel = new();

    /// <summary>Where the human pilots are, as one snapshot per call, the seam the flight model's
    /// far-field plant is selected on (<see cref="FlightModel.FarFieldPlant"/>). The session binds
    /// the same snapshot every other "who is nearest" consumer reads. Null (every rig built without
    /// a session) leaves the distance at zero, which keeps that rig on near-field aerodynamics.
    /// ⚠ Set on AI rigs. A human rig's nearest human is itself, so the distance is always 0.</summary>
    public Func<IReadOnlyList<Vector3>>? HumanPositions;

    /// <summary>The world's destructibles, when this session has a world runtime, the aim assist's
    /// third candidate list (an approximation of the original's `targets.zrd`
    /// `MStructList`). Null in every build with no world (the weapon lab, the suites), which costs
    /// the scan nothing: that pass simply iterates an empty list.</summary>
    public DestructibleRegistry? Destructibles;

    /// <summary>The mission's surface vehicles, when this session built any, on the SAME candidate
    /// list the aircraft roster feeds (`VehicleList`, docs/org/aim-assist.md "The four lists": the
    /// decoded list holds "aircraft and AI ground/sea vehicles"), never the structure or turret one.
    /// Null in every build with no world or no hull spawned, which costs the scan nothing.</summary>
    public ISurfaceVehicles? SurfaceVehicles;

    /// <summary>Draw the collision probe, the swept ray plus the airframe boxes the
    /// crash test sweeps each physics frame, in green (red on the impact frame).</summary>
    public bool DebugCollision;

    /// <summary>The gamepad devices that fly THIS plane (splitscreen). Null, the
    /// single-player default, means every connected pad flies it (see <see cref="PadPressed"/>).
    /// In splitscreen each player is bound to its own device so P2's stick never moves P1.</summary>
    public int[]? PadDevices;

    /// <summary>Whether the keyboard flies this plane. Single player and splitscreen P1: true;
    /// P2–P4 are pad-only (there is one keyboard).</summary>
    public bool UseKeyboard = true;

    /// <summary>Where the HUD <see cref="CanvasLayer"/> is parented. Null (single player) keeps it
    /// a child of this node, i.e. the main viewport; splitscreen sets the player's SubViewport so
    /// the dials/compass draw in that player's pane only.</summary>
    public Node? HudParent;

    /// <summary>Whether P / gamepad-Start reads on this rig at all. True for every human rig,
    /// splitscreen included: the freeze halts the shared simulation for everyone
    /// regardless of who pressed it, and <see cref="PauseState"/> is what keeps a second player
    /// from stealing the resume. False for AI rigs and the suites' bare test rigs, which have no
    /// pause key to read.</summary>
    public bool AllowPause = true;

    /// <summary>Debug/testing (--debug-scoreboard): force-complete the stunt run on the first
    /// physics frame so the results scoreboard renders deterministically for a screenshot. No
    /// effect without a stunt run.</summary>
    public bool DebugCompleteStunt;

    /// <summary>The numpad snap direction (1–9, 5 unbound) held for the whole run, the scripted
    /// twin of holding the key, so a capture can frame a flank or the belly of a flying plane. 0
    /// (the default) leaves the head straight ahead, i.e. exactly today's behaviour. A key held at
    /// the controls wins over this while it is down, and the head SWINGS to the pinned direction
    /// rather than cutting to it, since it reaches the camera through the same head as a key does
    /// (<see cref="HeadLook"/>).</summary>
    public int PinnedView;

    /// <summary>The view mode this pilot starts in (<c>--view=cockpit</c>/<c>=nose</c>); Chase, the
    /// default, is exactly today's behaviour. The live value is the camera's
    /// (<see cref="CameraController.ViewMode"/>) once <see cref="Setup"/> has run, because the
    /// cycle key changes it; this field only seeds it.</summary>
    public PilotViewMode PinnedViewMode = PilotViewMode.Chase;

    /// <summary>The Auto Head Turn option as the options file has it, the original's GAME OPTIONS
    /// checkbox. True turns the head with the aircraft in the cockpit, false leaves it ahead. It is
    /// seeded at build and read every frame. A page accepted over the pause rewrites it
    /// (<c>UI.Screens.PausePreferences</c>). ⚠ Null, the default, is "never set". It leaves the
    /// <c>headLook.autohead</c> config key deciding, which ships OFF (<see cref="Utils.Config"/>),
    /// so a build with no options file answers to that key alone.</summary>
    public bool? AutoHeadTurn;

    /// <summary>The right-stick deflection held for the whole run (<c>--look=x,y</c>, +x right and
    /// +y up), the scripted twin of pushing the look stick. Zero (the default) is a centred stick,
    /// exactly today's behaviour; a live stick wins while deflected, the rule a held numpad key
    /// follows against <see cref="PinnedView"/>. Feeds the chase swing and the first-person head
    /// through the one reader, so a run can compare them.</summary>
    public Vector2 PinnedLook;

    /// <summary>The cursor offset the mouse-flying stick reads while set, +x right and +y down over
    /// the pane's half extent. A suite can then fly the scheme with no window and no cursor. Null,
    /// the default, reads this seat's real pane.</summary>
    public Vector2? MouseStickForTest;

    /// <summary>Whether this seat may take the desktop mouse while it flies, under either mouse
    /// scheme. Resolved once per session by <see cref="MouseCapture.Allowed"/>, whose ⚠ says why the
    /// answer is no on the hidden test desktop and in every <c>--det</c> run. False, the default,
    /// leaves <c>Input.MouseMode</c> untouched by this node from first frame to last.</summary>
    public bool MouseCaptureAllowed;

    /// <summary>Out of lives: this pilot stays crashed for the rest of
    /// the mission, neither R nor <see cref="AutoRespawnAfter"/>'s timer brings it back, while
    /// the session hands its pane to a <see cref="SpectatorCamera"/> and the others fly on. Set by
    /// the session's own lives ledger (<c>InstantActionRuntime.NotifyPilotDown</c>), never from
    /// here; this node holds no mission state and decides no rule.</summary>
    public bool Spectating;

    /// <summary>Whether R and pad Y respawn a LIVE aircraft. False wherever the mission counts: in
    /// a campaign mission and in Instant Action a respawn taken while flying is a free repair,
    /// restock and refuel, so those two pin it and the button is read only from
    /// <see cref="Crashed"/>. True in free flight, the stunt runs and the dogfight, where R means
    /// "put me back at the spawn". Pinned by the session's own director, never from here.</summary>
    public bool AllowLiveRespawn = true;

    /// <summary>How far this seat's controls are held back while the world flies on, see
    /// <see cref="FlightControlHold"/>. Set by the session's own director around an ending the
    /// player watches out, never from here; an aircraft nobody holds is unaffected.</summary>
    public FlightControlHold ControlHold;

    private const float ThrottleRate = 0.5f;    // full sweep in 2 s
    // Spawn throttle/speed come from the mission's PLAYER_INIT via Setup (docs/formats/spawns.md).
    // ⚠ These two are only the no-mission fallback (labs, tests, AI rigs), and they are the OLD
    // placeholder, deliberately: the original gives an AI aircraft min(plane_speed_max, fd_speed),
    // which is decoded but not landed, so moving them to the player's 18 m/s would be a third
    // invented answer rather than that rule (docs/org/flightModel.md, "What this changes" #13).
    private const float FallbackSpawnThrottle = 0.5f;
    private const float FallbackSpawnSpeed = 53.6f;
    private const float CarrierDropThrottle = 0.1f;

    // The speed a cutscene's re-placement flies out at, along the placed nose. The original writes
    // it straight into the vehicle's velocity at the re-placing callback, as the placed rotation's
    // own forward axis times this literal (docs/formats/anim-definitions/cutscenes.md).
    private const float ReplacedSpeed = 53.6448f;
    private const float UnderMapY = 0f;        // C1 terrain sits at y≈100+; below this we're lost
    // How often the under-map backstop may write a line. Not a tuned quantity: it exists only so a
    // stuck aircraft's per-frame resets read as a rate and a count rather than a flood of lines.
    private const float UnderMapReportInterval = 1f;
    private const float CollisionMargin = 6f;   // m of look-ahead past the nose (airframe half-length)
    // ANIM_STATE's RUNNING, the value AnimRuntime.AnimStateOf reports while an instance is live.
    private const int AnimRunning = 2;
    private const float DebugFinishStagger = 1.5f; // s between players' forced finishes (--debug-scoreboard in a race)

    private const float GrazeReactionInterval = 1.5f; // s between graze reactions, NOT a tuned value:
                                                      // the touchdown defs stop their own puffer at
                                                      // ANIMATION_OFFSET 1.5, so this is one whole authored
                                                      // reaction per scrape rather than a restart per frame
    private const int InitialTargetGrace = 300;    // frames --target= waits for the pool to fill
    private const float PropIdleSpin = 0.4f;    // blur discs still turn at zero throttle (windmilling)

    // Everything this pane draws for its pilot. Always present, so no site has to ask whether
    // there is a HUD: an aircraft with no readouts built simply has a module that draws nothing.
    private readonly FlightHud _pilotHud = new();
    // Which state this aircraft is in and what moves it between them, including the spawn timers.
    // Every transition below reports what it did and this node performs it (Decision 7).
    private readonly AircraftLifecycle _lifecycle = new();
    private readonly SweepCadence _sweep = new();    // the original's alternate-step sweep and its carried motion
    // The desktop mouse this seat holds while it flies, and the virtual cursor standing in for the
    // OS one for as long as it does. Idle on every seat that never takes it (MouseCaptureAllowed).
    private readonly MouseCapture _mouse = new();
    // The look stick as the CHASE swing reads it. Its own filter rather than the head's, because
    // that swing is rigid and instant and takes the stick on the frames the head is not given it.
    private readonly StickLookFilter _chaseLook = new();
    private readonly AimCandidateSet _aimCandidates = new(); // rebuilt once per fire call (B4/B5)
    private readonly AimCandidateSet _gunnerScan = new();    // the AI gunner's acquisition scan (D14)
    private readonly AimCandidateSet _rescoreScan = new();   // the aircraft-only walk the re-score's withdrawal reads
    private readonly List<RocketPylonView> _pylonViews = new();          // the AI rocketeer's pylon walk
    private readonly List<RankedTargetCandidate> _rankCandidates = new(); // the D12/D36 ranking snapshots
    // …and their sources, by index: a FlightController, a TurretController or a
    // DestructibleRegistry.Instance (the D36 widening, BL-363's decoded turret/structure pools).
    private readonly List<object?> _rankSources = new();
    // The names one non-aircraft candidate also answers to, refilled per candidate by
    // TargetPool.CollectOwners. A field, not a fresh list: the acquisition walks every pool.
    private readonly List<string> _biasOwners = new();
    private readonly RandomNumberGenerator _aimRng = Rng.Stream(Rng.Weapons); // the assist's 1° launch scatter
    private readonly AimCandidateSet _targetScan = new();   // the targeting pass's own scan, rebuilt per frame
    private readonly List<AimCandidate> _targetParts = new(); // this frame's selectable sub-parts
    private readonly List<AimCandidate> _targetSites = new(); // this frame's objective sites
    private readonly bool[] _targetKeyPrev = new bool[13];  // the eleven targeting keys, spyglass pair last
    private readonly bool[] _viewModeKeyPrev = new bool[5]; // F8/F2/F7 + pad view-selection edges
    // Decision 7: D-pad Up down longer than the shared threshold is a HOLD, not a tap. The two
    // weapon selectors' pad buttons split on the same number, inside FireControl.
    private readonly TapHoldButton _targetHold = new(TapHoldButton.PadHoldSeconds);
    // Swallows a discrete flight command's next read when a cutscene skip or a pause-sheet dismiss
    // hands input back while the control that confirmed it is still down.
    private readonly FlightReentryLatch _reentryLatch = new();

    // This seat's keymap, and several views of it. The halves of an attitude action are processed
    // differently here and then summed. The keys ramp through StickRamp, a pad bends through
    // StickCurve, a flight stick passes linearly. A single OR-ed read cannot express that, so the
    // halves stay separable while the bindings stay shared.
    private readonly BindingProfile _bindings;
    private readonly PlayerActions _actions;     // keyboard, mouse and pad together
    private readonly PlayerActions _keyActions;   // the keyboard and mouse half alone
    private readonly PlayerActions _padActions;   // the pad half alone, flight sticks included
    // The pad half's flight axes, split by device. A pad bends through the pad curve and a flight
    // stick flies linearly (AnalogAxes), so neither reads the other's bindings.
    private readonly PlayerActions _padAxes;
    private readonly PlayerActions _stickAxes;
    private readonly LeverTakeover _leverTakeover = new(); // when a bound throttle lever commands
    private readonly SeatDeviceState _seatState;
    private readonly SeatDeviceState _padMutedState;
    private readonly StickSplit _padsAlone;
    private readonly StickSplit _sticksAlone;
    // The pad haptics for this seat, routed to the same devices the bindings above read. A
    // splitscreen pane therefore rumbles its own pilot's controller and no other. The shot total is
    // what turns the carried gunners' running counters into a fired-this-tick edge.
    private readonly PadRumble _rumble;
    private int _turretShots;

    // The message table this seat's control prompts take their wording from, null on a rig built
    // with none (which then reads the prompt's own data-less stand-in).
    private Messages? _strings;

    private ulong _inputFrame = ulong.MaxValue;  // the rendered frame the three readers above hold
    private int _stickRevision = -1;             // the StickProfiles revision last merged into _bindings
    private bool _initialTargetDone;             // --target= has had its one chance
    private int _initialTargetWaits;             // …frames it has waited for a non-empty pool
    private FlightModel _model = null!;
    private CameraController? _cam;              // null on an AI rig, no view rides this plane
    private bool _deathCamera;                   // the pilot's own destruction holds the view; the
                                                 // one camera that keeps writing while crashed,
                                                 // since it re-aims at the falling wreck
    private Camera3D? _viewCamera;
    private CanvasLayer? _hudCanvas;             // the whole HUD layer; hidden while crashed (the
                                                 // original's crash camera shows no HUD, footage);
                                                 // never built on an AI rig
    private CanvasLayer? _messageCanvas;         // the message stack's own layer, which the crash
                                                 // hide above deliberately leaves up
    private Vector3 _spawnPos;
    private Basis _spawnAttitude;
    private float _spawnThrottle = FallbackSpawnThrottle;
    private float _spawnSpeed = FallbackSpawnSpeed;
    private float _throttle;                     // the live lever (+0x128), slewing toward the next
    private float _throttleSetting;              // the commanded lever (+0x124) the controls write
    private float _leverGap;                     // commanded minus live, read before this step's slew
    private float _keyPitch;                    // the three keyboard axes' own deflection, ramped
    private float _keyRoll;                      // by StickRamp; a gamepad's analogue axis adds on
    private float _keyYaw;                       // top and is never ramped
    private double _sinceTelemetry;
    private float _sinceUnderMapReport;          // s since the under-map backstop last reported
    private int _underMapResets;                 // its running count, which is how a loop reads
    private WarningShotCue? _warningShots;       // the incoming-fire shield's shipped accumulator
    private CanopyHoleCue? _canopyHoles;         // the canopy-glass cue's decoded cadence
    private System.Random? _canopyRng;           // built on the first round that lands, not at Setup
    private FlightInput _lastInput;              // this physics frame's stick input (drives the surfaces)
    // s until auto-rematch on a finished race (scripted hold runs only). Not the lifecycle's
    // respawn timer: this one belongs to a results board, which no aircraft state reaches.
    private float _autoRestartIn = AircraftLifecycle.AutoRespawnDelay;

    /// <summary>When set (from <see cref="FlightControllerBuild.HoldSegments"/>), replaces keyboard
    /// input for automated screenshot/demo runs: each segment holds its input for its duration
    /// (seconds of sim time), the last holds forever, and a respawn restarts the sequence
    /// (<see cref="ScriptedInputSource.Reset"/>). Such runs are unattended, so a crash
    /// auto-respawns after a short pause.</summary>
    private (FlightInput Input, float Duration)[]? _holdSegments;

    /// <summary>The <c>--lever=</c> schedule (from <see cref="FlightControllerBuild.LeverSteps"/>):
    /// commanded-lever presses at their own sim-seconds, the scripted twin of the digit row. It lets
    /// a headless capture slam the throttle. Null on every ordinary run.</summary>
    private IReadOnlyList<(float At, float Lever)>? _leverSteps;
    private float _leverElapsed;                 // sim-s the keyboard arm has read, for the schedule
    private int _leverNext;                      // the next scheduled press not yet made

    // A stick the caller supplied outright (FlightControllerBuild.InputSource), ahead of every
    // other arm.
    private IFlightInputSource? _suppliedInputSource;
    private bool _pausePrev;                     // previous frame's pause-key state (edge detection)
    private bool _haltPrev;                      // previous frame's clock-halt state (orbit seeding)
    private ImmediateMesh? _probe;               // debug collision-probe line
    // The rest pose of every node the crash def flings (the destroyed wreck's pieceN meshes) and
    // the BUILT visibility of every plane-model node, both captured before the first crash so
    // Respawn can undo what the def did: a RESET_STATE re-poses only the nodes it names and
    // restores only dontmove, so neither the flung pieces nor the built-hidden torn panels and
    // wingtip flares come back without these two snapshots. Bound with the rest of the crash rig.
    private IReadOnlyList<(Node3D Node, Transform3D RestPose)>? _crashRestPoses;
    private IReadOnlyList<(Node3D Node, bool Visible)>? _crashPlaneVisibility;
    private AnimRuntime? _crashRuntime;          // the bound rig; reached through CrashRuntime, which forces the build below
    private Node3D? _crashAnchor;                // the rig's `player` anchor, bound with it
    private Node3D? _viewCameraProxy;            // the rig-local `camera1`; see EnsureViewCameraProxy
    private Action? _pendingCrashRig;            // a rig armed but not built yet; see ArmPendingCrashRig
    private IWorldQuery? _worldQuery;             // the sweep/ray seam; bound in Bind, lazy for bare test rigs
    private AircraftContactResolver? _contacts;   // the contact rules; lazy, over the same seam
    private IFlightInputSource? _inputSource;     // which stick flies this aircraft; bound in Bind, lazy for bare test rigs
    private float _grazeReactionCooldown;        // s left before the next touchdown_* reaction
    private int _projectileHitsLogged;           // verification breadcrumb: the first few hits log
    private FireControl? _fire;                  // the fire-control state machine; built in _Ready with the loadout
    private GunGroup[] _firableGuns = Array.Empty<GunGroup>(); // the firable gun groups in _fire's slot order (muzzle nodes, live ammo)
    private GunAimSlot[][] _aimSlots = Array.Empty<GunAimSlot[]>(); // per _firableGuns group, one slot per muzzle, B2's assist state
    private bool _aimLoggedFirst;                // verification breadcrumb: the assist's first snap logs once
    private bool _aimListsLogged;                // verification breadcrumb: the candidate list sizes log once
    private bool _groundBlowLoggedFirst;         // verification breadcrumb: ground blow's first repelling hit
    private bool _gunnerLoggedTarget;            // verification breadcrumb: the AI gunner's first acquisition
    private int _gunnerRetargetsLogged;          // capped per shooter: every re-score switch would flood the log
    private bool _gunnerLoggedFire;              // verification breadcrumb: the AI gunner's first open fire
    private bool _rocketeerLoggedFire;           // verification breadcrumb: the AI's first ordnance launch
    private string _rocketeerLastVerdict = "";   // the last rocketeer verdict KEY logged, so a repeat is silent
    private int _rocketeerVerdictsLogged;        // capped per shooter: a flight of twelve must not flood the log
    private bool _gunLoopOn;                     // the firing loop sound is currently playing
    private bool _aiNitroArmed;                  // the current AI nitro maneuver already engaged
    private bool _propsStopped;                  // the stopped-prop presentation holds the slot
    private bool[] _gunLoggedFirst = Array.Empty<bool>(); // verification breadcrumb: each group logs its first live round once
    private int _rocketsLaunched;                // verification breadcrumb: the first few launches log their pylon
    private int? _team;                          // Team's backing field, null until overridden (B7)
    private bool _held;                          // Held's backing field, the airframe is pinned (weapon lab)
    private bool _cameraOwned;                   // CameraOwned's backing field, the lab's free camera has the view
    private bool _panelShown;                    // the cockpit interior is on the screen this frame
    private bool _orbitPrev;                     // edge detection for entering the orbit (halt or hold)
    private bool _reseedOrbit;                   // the free camera handed the view back; re-seed from where it left it
    private bool _heldPinned;                    // the pinned pose below is valid (captured on the first held step)
    private Vector3 _heldPos;                    // the pinned position, re-applied through the model every held step
    private Basis _heldAttitude;                 // the pinned attitude, ditto
    private Vector2 _mouseLookPrev;              // last frame's screen mouse position (head-look motion)

    // The sim advances on the 60 Hz physics tick while rendering runs at the display rate, so
    // drawing the raw sim pose stutters the plane against the smoothly-moving chase camera at
    // any render rate above 60 fps, in proportion to speed. SimStep records the last two sim
    // poses; _Process draws between them at the physics interpolation fraction. Realtime clock
    // only, a parent-driven (fixed-dt) clock draws the exact sim pose, keeping scripted
    // captures byte-identical.
    private Transform3D _simPrev = Transform3D.Identity;
    private Transform3D _simCurr = Transform3D.Identity;
    private Transform3D _renderPose = Transform3D.Identity; // the pose actually drawn this frame
    // Where the aircraft stood when a cutscene began staging it (StageAt), and the flag that it is
    // being staged at all. ResumeAt replaces the pose with the one the cutscene re-places the pilot
    // at, and raises the flag below so the hand-back moves the flight model too.
    private Transform3D? _stagedFrom;
    private bool _resumePlaced;
    // The model's own speed and throttle the instant staging began, captured alongside
    // _stagedFrom: Held pins both at zero for every step it holds (its own contract, shared with
    // the weapon lab), so a hand-back naming no re-placement has nothing else to resume them from.
    private float? _stagedSpeed;
    private float? _stagedThrottle;

    public FlightController()
    {
        _seatState = new SeatDeviceState(DefaultBindings.AnyPad, () => PadDevices, sticks: StickDeviceState.Live(() => PlayerIndex));
        _padMutedState = new SeatDeviceState(DefaultBindings.AnyPad, () => PadDevices, readsPads: false);
        _bindings = BindingProfile.Defaults(default, true);
        _rumble = new PadRumble(() => PadDevices, _bindings.Device);
        _actions = _bindings.Actions(InputContext.Flight);
        var map = _bindings.Map(InputContext.Flight);
        _keyActions = new PlayerActions(map, true);
        _padActions = new PlayerActions(map, false);
        _padAxes = new PlayerActions(map, false);
        _stickAxes = new PlayerActions(map, false);
        _padsAlone = StickSplit.WithoutSticks(_seatState);
        _sticksAlone = StickSplit.SticksOnly(_seatState);
    }

    /// <summary>Raised once per crash, at <see cref="Crash"/>: (victim <see cref="PlayerIndex"/>,
    /// killer shooter id). Null for terrain, mid-air, an unowned round or any other crash cause. A
    /// fact report, not a score, this node knows no match rules; the session subscribes and scores
    /// when a match exists. Respawn emits nothing.</summary>
    public event Action<int, int?>? Downed;

    /// <summary>Raised on every ground impact this aircraft performs, the fresh crash and a
    /// shot-down wreck's own landing alike. That is what the original's crash notice is posted off.
    /// The <see cref="Downed"/> event is the death report and fires once; this fires per
    /// impact.</summary>
    public event Action<FlightController>? GroundImpact;

    /// <summary>Raised whenever <see cref="Inert"/> flips, with this aircraft, the seam a
    /// session-level roster (the E16 voice dispatch's speaker list) mirrors the state into, since
    /// nothing outside this node polls it.</summary>
    public event Action<FlightController>? InertChanged;

    /// <summary>Raised on every projectile hit that moved this plane's damage state without
    /// destroying it (the destroying hit reports through <see cref="Downed"/> instead), carrying
    /// the round's shooter as <see cref="Downed"/> carries its killer, null for a round nobody
    /// owns. The E16 voice runtime reads the whole-vehicle summary off it for the DI distress
    /// tiers and the player's WA-HighDmg crossing, and the shooter for the ally distress line.
    /// Terrain grazes do not raise it; the decoded distress sites are the combat hit path's.</summary>
    public event Action<FlightController, int?>? DamageApplied;

    /// <summary>This aircraft's team, everywhere "is this hostile" is asked reads this instead of
    /// deriving a team from <see cref="PlayerIndex"/> (shooter ids are not team ids). Unset, it
    /// falls back to <see cref="AimAssist.TeamOfPilot"/>, so free flight and <c>--vs</c> keep every
    /// pane on its own team; a mission builder sets it to a fixed team explicitly.</summary>
    public int Team
    {
        get => _team ?? AimAssist.TeamOfPilot(PlayerIndex);
        set => _team = value;
    }

    /// <summary>The roster cohort this aircraft is counted in (the <c>aiv</c> block's <c>group</c>,
    /// the original's vehicle <c>+0x388</c>), or null for an aircraft no group counts: a free-flight
    /// or wave spawn, and a human rig until a 967 capture swap hands it the captured aircraft's.
    /// A mission's <c>DEDG</c> walk reads it off the human rig alone; the AI members are counted
    /// off their roster plans.</summary>
    public int? Group { get; set; }

    /// <summary>This aircraft carries the mission's objective marker (the roster block's own
    /// <c>aiv</c> slot 37, the original's entity <c>+0x4d</c>), so its ordinary candidate sorts
    /// ahead of every enemy on the Enemy cycle instead of a second synthetic one standing beside
    /// it. ⚠ Stamped at the spawn, never derived here: the aeroplane is the marker, which is what
    /// gives it the wake and death gate for free (docs/org/targeting.md).</summary>
    public bool ObjectiveTarget { get; set; }

    /// <summary>The resolved label half of that marker's line 1 (slot 38, "Bomber"), or null where
    /// the block authors none, which is every shipped block but one.</summary>
    public string? ObjectiveTypeLabel { get; set; }

    /// <summary>The resolved category half of that marker's line 1 (slot 39, "Follow"), or
    /// null.</summary>
    public string? ObjectiveCategory { get; set; }

    /// <summary>The weapon lab's hold: the airframe holds its pose while everything else in the
    /// session keeps running (props, guns, rounds, world sim). ⚠ NOT the P halt
    /// (<see cref="GameClock.Halted"/>), which stops the whole clock. Clearing it un-pins the
    /// airframe at zero speed; <see cref="PlaceHeld"/> moves the pin.</summary>
    public bool Held
    {
        get => _held;
        set
        {
            _held = value;
            if (!value)
            {
                _heldPinned = false;   // a later re-hold pins wherever the plane is then
            }
        }
    }

    /// <summary>Current throttle (0-1), the live flight model's own value, exposed so the rig
    /// assembler and the suites can read the lever, after <see cref="Setup"/> has already placed
    /// the plane at its spawn throttle.</summary>
    public float Throttle => _model.Throttle;

    /// <summary>Seconds left on this aircraft's engine-dead timer, zero when the engine runs. The
    /// choker's one observable, since nothing else on that path changes (see
    /// <see cref="TryChokeEngine"/>).</summary>
    public float EngineDeadRemainingS => _model.EngineDeadRemainingS;

    /// <summary>Whether the stopped-prop presentation holds this aircraft's prop slot, the still
    /// blade shown and the blur discs faded out. The engine-out edge and the death routine both put
    /// it there. The original's <c>+0x6cc</c> slot (docs/org/ordnanceTypes.md).</summary>
    public bool PropsStopped => _propsStopped;

    /// <summary>The view this pilot has selected, live. Falls back to <see cref="PinnedViewMode"/>
    /// before <see cref="Setup"/> has built a camera, and reads Chase on an AI rig, which has
    /// none.</summary>
    public PilotViewMode ViewMode => _cam?.ViewMode ?? PinnedViewMode;

    /// <summary>Whether this pilot is in one of the two first-person views, the session's feed for
    /// the anim data's <c>PLAYER_1ST_PERSON</c> condition (id 120).</summary>
    public bool FirstPersonView => PilotView.IsFirstPerson(ViewMode);

    /// <summary>Whether this pilot has the full Cockpit view (mode 6) selected, the half of
    /// <see cref="FirstPersonView"/> that draws an interior. Nose (mode 7) reads false. A held
    /// look-behind leaves a first-person selection where it is, so the effective view is the
    /// selected one (<see cref="PilotView.Effective"/>).</summary>
    public bool CockpitView => ViewMode == PilotViewMode.Cockpit;

    /// <summary>The one head every view of this pilot's aircraft is placed by, or null on an AI rig,
    /// which has no camera. Read by the suites: the angles it has settled at and the elevation floor
    /// the last placed frame handed it are what say which view's law ran.</summary>
    public HeadLook? Head => _cam?.Head;

    /// <summary>This airframe's stats, the flight model's own copy (jittered for an AI spawn, so it
    /// is the plane's data and not the cached def's). Read for the airframe's DISPLAY NAME
    /// (<c>PlaneRoster.PlaneDisplayName</c> → <c>Fury</c>) by the targeting pool's label pass. Null
    /// before <see cref="Setup"/> has bound a flight model, which is a bare suite rig; a caller
    /// that wants a name falls back to the node's.</summary>
    public PlaneStats? Stats => _model?.Stats;

    /// <summary>This pane's HUD message stack, where the session posts the kill line. Null until
    /// <c>_Ready</c> has built the HUD, and on every AI rig, which builds none.</summary>
    public HudMessages? MessageStack => _pilotHud.MessageStack;

    /// <summary>The data-driven crash: a per-player <see cref="AnimRuntime"/> bound to this plane's
    /// scoped crash subtree, advancing itself in its own <c>_Process</c>. Plays the compiled crash
    /// def the struck surface selects; see this module's entry in docs/architecture.md. Bound by
    /// <c>WorldEffectsFactory</c> through <see cref="BindCrashRig"/> for every flown plane; null
    /// only when the crash program/scene were unavailable, and the plane then just hides.</summary>
    public AnimRuntime? CrashRuntime
    {
        get
        {
            EnsureCrashRig();
            return _crashRuntime;
        }
    }

    /// <summary>The node the crash definition anchors to (its <c>player</c> anim-root), passed to
    /// <see cref="AnimRuntime.Play"/> on a crash. Bound with <see cref="CrashRuntime"/>.</summary>
    public Node3D? CrashAnchor
    {
        get
        {
            EnsureCrashRig();
            return _crashAnchor;
        }
    }

    /// <summary>Whether this aircraft's crash rig has been armed but not yet built, a spawn that
    /// handed the build to a frame-budgeted pump. Observation only: every reader of the rig itself
    /// forces the build, so nothing outside the pump has to ask.</summary>
    public bool CrashRigPending => _pendingCrashRig != null;

    /// <summary>The crash-def vector the struck surface id indexes, the original's own selection
    /// mechanism (see <see cref="SurfaceDefTable"/>). Built from the bound crash program, so it
    /// knows which slots name a def this install actually ships. The selection is
    /// <see cref="AircraftLifecycle"/>'s, which holds the table; null when no crash rig was
    /// built, and the plane then just hides on a crash.</summary>
    public SurfaceDefTable? CrashDefs
    {
        get
        {
            EnsureCrashRig();
            return _lifecycle.CrashDefs;
        }
    }

    /// <summary>The def the last <see cref="Crash"/> selected off <see cref="CrashDefs"/>,
    /// <c>player_crash_*</c> on a human rig, <c>ai_crash_*</c> on an AI plane, null before any
    /// crash or when no crash rig was built. The prefix names the family, so a suite (or a log
    /// reader, the CRASH line prints the same value as <c>def=</c>) can pin which family
    /// fired.</summary>
    public string? LastCrashDef => _lifecycle.LastCrashDef;

    /// <summary>Seconds a crash sits on the crash cam before this plane auto-respawns, or null,
    /// the default, for manual R only. The session arms it (Versus: 3 s, every rig) so a downed
    /// player rejoins the fight without touching a key; R still respawns early, and the timer is
    /// armed at <see cref="Crash"/>. Scripted HoldSegments runs auto-respawn regardless, on
    /// <see cref="AircraftLifecycle.AutoRespawnDelay"/> unless this says otherwise.</summary>
    public float? AutoRespawnAfter
    {
        get => _lifecycle.AutoRespawnAfter;
        set => _lifecycle.AutoRespawnAfter = value;
    }

    /// <summary>Whether this plane is crashed, frozen at the impact, airframe hidden, waiting
    /// for respawn. The fact the session (and the in-engine suites) read; only Respawn clears it.</summary>
    public bool Crashed => _lifecycle.Crashed;

    /// <summary>Whether this aircraft's hull is spent and its destroy def is playing, true from
    /// the kill, through the fall, and on past the ground impact until respawn. <see cref="Crashed"/>
    /// covers a live aircraft flown into terrain as well; this is the shot-down half alone.</summary>
    public bool Destroyed => _lifecycle.Destroyed;

    /// <summary>Whether the wreck is still falling under the flight model. True from the kill until
    /// the destroy def's <c>Callback 15</c> hands the hull to the anim, or until it lands and runs
    /// its ground-impact def, whichever the airframe authors.</summary>
    public bool WreckFalling => _lifecycle.WreckFalling;

    /// <summary>An aircraft that has been BUILT but held completely out of the session, not
    /// stepped, drawn, collidable, hittable, or a targeting candidate. The original's wave
    /// sequencer builds waves 2-4 this way (docs/formats/instant-action.md "The ace and the
    /// waves"); <see cref="Activate"/> is CSVM's inverse.
    /// ⚠ Do not park an inert aircraft far away instead of flagging it. It would still tick,
    /// collide and cost a frame; every consumer reads <see cref="InPlay"/> instead.</summary>
    public bool Inert
    {
        get => _lifecycle.Inert;
        set
        {
            if (!_lifecycle.SetInert(value))
                return;
            // Going live is flight's own resume/skip re-entry point: a cutscene's skip or its
            // natural handoff clears Inert on the same frame the button that ended it can still be
            // down, and a cutscene takes any key or pad button.
            if (!value)
                SwallowInputHeldThroughReentry();
            ApplyPresence();
            InertChanged?.Invoke(this);
        }
    }

    /// <summary>Inert under a cutscene's AI park (code 913) rather than deactivated: the aircraft is
    /// still in the mission, so an objective walk counts it. The cutscene host sets it beside
    /// <see cref="Inert"/> and clears it at the reveal, or when a swap hides the aircraft.</summary>
    public bool Parked
    {
        get => _lifecycle.Parked;
        set => _lifecycle.SetParked(value);
    }

    /// <summary>Out of the mission the way the original's dead byte reads: inert with no cutscene
    /// park behind it. ⚠ DEDG and TRAVELERS count by this, never by <see cref="Inert"/>, or the
    /// wing-walk capture wipes the parked bomber's group out mid-cutscene.</summary>
    public bool Deactivated => _lifecycle.Deactivated;

    /// <summary>Whether this aircraft is present in the session as a real object: neither crashed
    /// nor <see cref="Inert"/>. The single "is it there" test every roster reads, the aim assist's
    /// vehicle/turret candidate lists, the projectile pool's proximity fuse and blast pass, the D12
    /// ranking's standing-target check, the HUD hostile tracker and the E11 wave-clear walk. A
    /// consumer that tests <see cref="Crashed"/> alone silently sees inert aircraft.</summary>
    public bool InPlay => _lifecycle.InPlay;

    /// <summary>This pane is in photo mode: the session owns its camera and its HUD is hidden,
    /// and this node's pause key is silent so Escape means "leave photo mode" and nothing else.
    /// Driven by <c>GameSession</c> through <see cref="BeginPhotoMode"/>/<see cref="EndPhotoMode"/>;
    /// this node decides nothing about the mode itself.</summary>
    public bool InPhotoMode { get; private set; }

    /// <summary>The options leaf stands over this session's pause: this node's pause key is silent
    /// for the duration, so Escape means "leave the page" and nothing else. Driven by
    /// <c>GameSession</c> through <see cref="BeginPauseLeaf"/>/<see cref="EndPauseLeaf"/>, the same
    /// shape as <see cref="InPhotoMode"/>; this node decides nothing about the leaf itself.</summary>
    public bool InPauseLeaf { get; private set; }

    /// <summary>The flight model's world position, the plane as a SIM value, not a node transform
    /// (the node lags it by the render interpolation). What another plane's aim assist aims at.</summary>
    public Vector3 WorldPosition => _model.Position;

    /// <summary>The flight model's world velocity, m/s, the assist's intercept solve needs it, and
    /// so does the shooter's own subtraction to relative velocity.</summary>
    public Vector3 WorldVelocity => _model.VelocityDir * _model.Speed;

    /// <summary>The nose axis off the SIM attitude (the render half may hold an interpolated
    /// frame), what the AI gunner's quick-draw cones project fore and aft from.</summary>
    public Vector3 NoseDirection => -_model.Attitude.Z;

    /// <summary>The whole SIM attitude, body→world, nose −Z and up +Y. Roll is part of the answer:
    /// the landings trigger measures the aircraft against an approach node's own orientation, and
    /// a rolled aircraft is off that approach by the roll as much as by the heading.</summary>
    public Basis Attitude => _model.Attitude;

    /// <summary>Another vantage owns this pane's camera: while set, this controller writes NOTHING
    /// to it (no chase, no fixed view, no orbit, <see cref="SnapCamera"/> a no-op), and clearing it
    /// re-seeds the orbit from where that vantage left the eye so the hand-back does not jump.
    /// ⚠ It also silences the arm re-asserting the first-person visibility rules, so every caller
    /// owes this aircraft's visibility through <see cref="SetViewedFromOutside"/> at BOTH edges,
    /// beside this flag and never instead of it, or a cockpit seat keeps a hidden airframe.</summary>
    public bool CameraOwned
    {
        get => _cameraOwned;
        set
        {
            if (_cameraOwned && !value)
            {
                _reseedOrbit = true;
            }
            _cameraOwned = value;
        }
    }

    /// <summary>This pane's pilot HUD, for the one assembler that builds its readouts and the one
    /// debug mode that hides its instruments. Internal, not public: nothing outside this assembly
    /// draws on a live aircraft, and the per-frame feed is this node's alone.</summary>
    internal FlightHud PilotHud => _pilotHud;

    // The sweep/ray seam: set in Bind, and lazy here too so a bare test rig that never binds
    // still gets one (GetWorld3D() only needs tree membership, which Bind does not gate).
    private IWorldQuery World => _worldQuery ??= new GodotWorldQuery(this);

    // The decoded contact rules, over the same seam: what a contact costs, whether this plane
    // survives it, and how far out of the surface it has to be pushed.
    private AircraftContactResolver Contacts => _contacts ??= new AircraftContactResolver(World);

    // Both holds swallow the discrete commands, so every command read tests this rather than one
    // named setting. Only the stick asks which of the two it is.
    private bool CommandsHeld => ControlHold != FlightControlHold.None;

    // The solo scoreboard's end: it shows over the finish pose and takes R as a rerun. Keyed on the
    // board itself rather than a flag of its own. The board is what these rules belong to.
    // ⚠ Instant Action builds no scoreboard. Its pilot flies on through the ending's hold as the
    // original's does, so a finished run there must not stop the aircraft.
    private bool SoloBoardHolds => Stunt is { AllComplete: true } && Race == null && Scoreboard != null;

    // Which stick flies this aircraft: set in Bind, and lazy here too so a bare test rig that
    // never binds still gets one, off whichever of _holdSegments/Pilot it already set (Decision 8,
    // no suite mutates either after stepping starts.
#pragma warning disable SA1202 // kept beside World, its seam counterpart, ahead of the public method below
    private IFlightInputSource InputSource => _inputSource ??= ResolveInputSource();

    /// <summary>The raw lever/surface command last written into <see cref="_lastInput"/>, the value
    /// <see cref="StepWreckFall"/> replays unchanged for a dead hull: internal so a suite can
    /// assert it is bit-identical across the death handover rather than inferring the freeze from
    /// the wreck's retained speed alone.</summary>
    internal FlightInput LastCommand => _lastInput;

    private IFlightInputSource ResolveInputSource() =>
        _suppliedInputSource
        ?? (_holdSegments != null ? new ScriptedInputSource(_holdSegments)
        : Pilot != null ? new PilotInputSource(this)
        : new KeyboardInputSource(this));

    /// <summary>The direction an ordnance round leaves along, which the player and the AI decide
    /// differently in the original (docs/org/ordnanceTypes.md, "Who aims ordnance, and who does
    /// not"): a human's comes from the aircraft's own basis axis, negated, or taken as-is for a
    /// <c>REAR</c> weapon, with the mount giving the spawn position alone; an AI's is the mount's
    /// clamped aim in world space. Null keeps the mount's own axis, which is what an AI with no
    /// rocketeer to clamp an aim has. No ordnance round of either shooter is aim-assisted.</summary>
    public static Vector3? OrdnanceLaunchDir(bool humanPiloted, Basis planeBasis, bool rear,
        Vector3? mountAimWorld) =>
        humanPiloted ? (rear ? planeBasis.Z : -planeBasis.Z) : mountAimWorld;
#pragma warning restore SA1202

    /// <summary>Wires the flight model and (for a piloted view) the chase camera, then spawns.
    /// <paramref name="camera"/> is null on an AI rig: no camera write below runs.
    /// <paramref name="cockpitCameraOffset"/> is this rig's authored <c>cockpit_camera</c> marker
    /// (<see cref="Mech3.PlaneBuilder.CockpitCameraOffset"/>), origin for callers with no first
    /// person view. <paramref name="spawnThrottle"/>/<paramref name="spawnSpeed"/> are the
    /// mission's PLAYER_INIT values, defaulted for callers with none, and persist across respawns.</summary>
    public void Setup(FlightModel model, Camera3D? camera, CamParams camParams,
        Vector3 spawnPos, Vector3 spawnLookAt,
        float spawnThrottle = FallbackSpawnThrottle, float spawnSpeed = FallbackSpawnSpeed,
        Vector3 cockpitCameraOffset = default)
    {
        _model = model;
        _viewCamera = camera;
        _cam = camera != null
            ? new CameraController(camera, camParams, _actions.Held, PinnedView, PinnedViewMode, cockpitCameraOffset)
            : null;
        // C22: the idle branch of the shared head-look law, set once here, since Head lives for
        // the controller's whole life and _model (captured by the closure) is reassigned by every
        // respawn, not replaced.
        if (_cam != null)
        {
            _cam.Head.IdleAim = AutoheadTarget;
            _cam.Head.TargetOffset = PadlockOffset;
        }
        _spawnPos = spawnPos;
        _spawnAttitude = Basis.LookingAt((spawnLookAt - spawnPos).Normalized(), Vector3.Up);
        _spawnThrottle = spawnThrottle;
        _spawnSpeed = spawnSpeed;
        _warningShots = new WarningShotCue(model.Stats.WarningShotMax,
            model.Stats.WarningShotDissipation, model.Stats.WarningShotInterval);
        _canopyHoles = new CanopyHoleCue();
        Respawn();
    }

    public override void _Ready()
    {
        // Only a seat that may take the mouse is handed input events at all. The AI rigs sharing
        // this node, and every seat in a scripted run, then cost nothing per motion event.
        SetProcessInput(MouseCaptureAllowed);
        // No HUD on an AI rig: a CanvasLayer draws over the whole window wherever its Node3D
        // parent sits, so an AI plane building one would paint its telemetry over the player's view.
        if (IsHumanPiloted)
        {
            // Explicit rather than Godot's implicit default of 1: the sun wash draws just above this
            // (UI.Boards.HudLayers.SunWash), so the HUD's own layer is deliberate, not incidental.
            var canvas = new CanvasLayer { Layer = UI.Boards.HudLayers.Hud };
            _hudCanvas = canvas;
            canvas.Name = "hud";
            // ⚠ The message stack takes a layer of its own, never the HUD's. The crash camera hides
            // the HUD outright, and the original's stack is up over that cut, showing the crash
            // notice the impact posted.
            var messages = new CanvasLayer { Layer = UI.Boards.HudLayers.Hud, Name = "hud_messages" };
            _messageCanvas = messages;
            // The two board-adjacent readouts this node still owns take their z-order slots inside
            // the pilot HUD's own order, so they are handed to it rather than added around it.
            _pilotHud.Attach(canvas, messages, VersusHud, Scoreboard);
            // Splitscreen parents the HUD into this player's SubViewport so it draws in that pane
            // only (and scales off the pane's height); single player keeps it on this node.
            (HudParent ?? this).AddChild(canvas);
            (HudParent ?? this).AddChild(messages);
        }
        if (DebugCollision)
        {
            _probe = new ImmediateMesh();
            AddChild(new MeshInstance3D
            {
                Mesh = _probe,
                TopLevel = true, // vertices are in world space
                Name = "collision_probe",
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    NoDepthTest = true, // stays visible against/through terrain
                },
            });
        }
        // The airframe's physics body, riding this node's transform; own queries pass
        // Body.ExcludeSelf so it never collides with itself.
        if (Collider != null)
        {
            Body = new AircraftBody(this, Collider);
            AddChild(Body);
            // ⚠ Register even an INERT plane: ApplyPresence keeps it off the aircraft layer, not
            // exclusion from this roster (the pool's fuse/blast passes walk it and read InPlay).
            Projectiles?.RegisterAircraft(Body);
        }
        // Setup's Respawn ran before this node was in the tree, so Body did not exist to switch
        // off then: re-assert an inert airframe's presence now that it does.
        ApplyPresence();
        SnapCamera();

        // One firing-state slot per firable gun group (turrets excluded, built inert).
        if (Loadout != null)
        {
            var firable = new List<GunGroup>();
            foreach (var g in Loadout.FirableGuns)
            {
                firable.Add(g);
            }
            _firableGuns = firable.ToArray();
            int n = _firableGuns.Length;
            // --ammo=N wins over weapons.gunAmmoCap so the knob survives --det, which drops config.json.
            int gunCap = AmmoCapOverride ?? Config.GetInt("weapons.gunAmmoCap", GunAmmoCapDefault);
            if (gunCap > 0)
            {
                foreach (var g in _firableGuns)
                {
                    g.Capacity = Mathf.Min(g.Capacity, gunCap);
                    g.Ammo = g.Capacity;
                }
            }
            // weapons.ordnanceCap / --ammo=N: the pylon equivalent of the gun cap above, same pattern.
            int ordnanceCap = AmmoCapOverride ?? Config.GetInt("weapons.ordnanceCap", OrdnanceCapDefault);
            if (ordnanceCap > 0)
            {
                foreach (var h in Loadout.Hardpoints)
                {
                    h.Capacity = Mathf.Min(h.Capacity, ordnanceCap);
                    h.Ammo = h.Capacity;
                }
            }
            // The state machine sees the loadout through its node-free slot views; --gun-select
            // (0-based group index, clamped) seeds its cursor.
            _fire = new FireControl(_firableGuns, Loadout.Hardpoints,
                AutoFireRockets, InfiniteAmmo, InitialGunSelect, Loadout.PylonStepOrder);
            _gunLoggedFirst = new bool[n];

            // One aim-assist slot per muzzle, seeded to "no assist" (both directions local
            // forward), the original's slots start unused and the forget pass alone would reach
            // the same state, this just skips the wait.
            double aimNow = GameClock.Current?.Time ?? 0.0;
            _aimSlots = new GunAimSlot[n][];
            for (int gi = 0; gi < n; gi++)
            {
                var slots = new GunAimSlot[_firableGuns[gi].MuzzleCount];
                for (int mi = 0; mi < slots.Length; mi++)
                {
                    slots[mi] = new GunAimSlot
                    {
                        Active = true,
                        Smoothed = AimAssist.LocalForward,
                        Target = AimAssist.LocalForward,
                        LastUpdate = aimNow,
                    };
                }
                _aimSlots[gi] = slots;
            }

            _pilotHud.BindWeaponGauges(n, Loadout.Hardpoints.Count);
        }
    }

    /// <summary>Rerun this plane's own run: fresh clock and every zone incomplete, then the
    /// respawn below. A plane with no stunt run is simply respawned, which is all a free flight's
    /// rerun amounts to.</summary>
    public void Rerun()
    {
        Stunt?.Reset();
        StuntShots?.Reset();
        Respawn();
    }

    /// <summary>Back to the spawn pose at half throttle with a healthy, repaired airframe: the
    /// crash respawn (R), and the session's per-plane reset for a race rematch. Leaves the
    /// stunt run alone, a mid-run crash deliberately keeps its zones and clock.</summary>
    public void Respawn()
    {
        // Asked before anything reads the spawn pose, so the whole reset below lands on the new
        // point. The dogfight rotates a downed seat away from the one it was camped at.
        if (RespawnPlacement?.Invoke() is { } placement)
        {
            _spawnPos = placement.Pos;
            var aim = placement.LookAt - placement.Pos;
            if (aim.LengthSquared() > 1e-6f)
                _spawnAttitude = Basis.LookingAt(aim.Normalized(), Vector3.Up);
        }
        _lifecycle.Respawn();
        (_inputSource as ScriptedInputSource)?.Reset(); // scripted hold sequences restart from the spawn
        _lastInput = default;
        Pilot?.ClearStun();  // a fresh airframe never wakes up with its pilot's hands still off
        _model.ClearChoke(); // nor with the last airframe's engine still choked
        WingLights?.Reset(); // flares off; the cycle restarts from this spawn
        Surfaces?.Reset();   // control surfaces back to neutral
        Damage?.Reset();     // every part back to full HP
        _canopyHoles?.Reset(); // pristine glass, which is what reset_bulletholes does at spawn
        _warningShots?.Reset(); // and a fresh airframe's shield, which the constructor arms
        _pilotHud.Reset();   // damage-dial blink timers cleared, no impact line pending
        Visuals?.Reset();    // torn panels off, healthy twins back, smoke trail cleared
        RefillWeapons();     // full ammo, dry warnings re-armed, any live tracers cleared
        // ⚠ The field, not the forcing property: a rig that is still armed has never played
        // anything, so there is nothing here to undo, and asking for it would build the whole rig
        // on the frame an aeroplane is placed, which is the frame the deferral exists to spare.
        if (_crashRuntime != null)
        {
            // Hard-stop the played def, re-hide the wreck (its RESET_STATE), and re-home the flung
            // pieces below (no reset event re-poses them; without this respawn leaves just the prop).
            _crashRuntime.ResetToBaseState();
            if (_crashRestPoses != null)
                foreach (var (node, rest) in _crashRestPoses)
                {
                    node.Transform = rest;
                }
            if (_crashPlaneVisibility != null)
                foreach (var (node, vis) in _crashPlaneVisibility)
                {
                    node.Visible = vis;
                }
        }
        _sweep.Reset();
        _grazeReactionCooldown = 0f;
        if (_hudCanvas != null)
            _hudCanvas.Visible = true;  // the crash camera hid it (footage); flying again
        // ⚠ An INERT airframe must stay off-screen and off the aircraft layer through a respawn too.
        // Setup() calls Respawn before _Ready builds the body, so this is also that first assertion.
        ApplyPresence();
        SetLever(_spawnThrottle);
        _keyPitch = _keyRoll = _keyYaw = 0f;  // a fresh airframe spawns with the stick centred
        // The original tops the tank up where it places the aircraft, from the def-derived capacity.
        Fuel.Capacity = Stats?.FuelCapacity ?? 0f;
        Fuel.Fill();
        // ⚠ Off the slot before the spawn choreography goes on it. A hull that went down with its
        // propellers stopped would otherwise fly again with the stop definition still running. That
        // definition fades staticpropN in under the start one fading it back out.
        _crashRuntime?.Stop("stopprops");

        // ⚠ The backing field here, never CrashRuntime: a still-armed rig has played nothing, so
        // there is nothing to replay, and asking would build the whole rig on the placement frame.
        // First setup precedes adapter construction, so the adapter replays startprops after attachment.
        _crashRuntime?.Play("startprops", PlaneModel, applyReset: false);
        _propsStopped = false;  // a fresh airframe's discs turn, whatever the last hull ended on
        // A fresh engine has no in-flight plume, and no charge left over from the last airframe.
        ExhaustSmoke?.Reset();
        Nitro.Reset();
        _aiNitroArmed = false;
        SpeedCue?.Reset();
        _model.Reset(_spawnPos, _spawnAttitude, _spawnSpeed, _throttle);
        _simPrev = _simCurr = _renderPose = new Transform3D(_model.Attitude, _model.Position);
        GlobalTransform = _simCurr;
        if (_cam != null && IsInsideTree())
            SnapCamera();
    }

    /// <summary>Opens this spawn's collision-free window, and with
    /// <paramref name="carrierDrop"/> the longer ground-blow damping a dropped aircraft needs so it
    /// does not fight the fall it was launched into. The rules are
    /// <see cref="AircraftLifecycle.ArmSpawnTimers"/>'s.</summary>
    public void ArmSpawnTimers(bool carrierDrop = false) => _lifecycle.ArmSpawnTimers(carrierDrop);

    /// <summary>Hands this aircraft a crash rig that is not built yet, to be completed by the
    /// caller's pump or by the first reader of the rig, whichever comes first. Null disarms it.
    /// ⚠ The delegate must be the whole remaining build: a reader gets no second chance, and an
    /// aeroplane whose damage model is reachable while its rig is not would take a kill with no
    /// wreck (docs/org/vehicleDamage.md).</summary>
    public void ArmPendingCrashRig(Action? finish) => _pendingCrashRig = finish;

    /// <summary>Builds an armed crash rig now, if one is still pending. Called by every accessor of
    /// the rig and at the head of both damage intakes and the ground contact, so an aircraft is
    /// never shot at, crashed or destroyed in a state where its rig cannot be reached. Clears the
    /// arming BEFORE running the build, because the build itself reads those accessors.</summary>
    public void EnsureCrashRig()
    {
        if (_pendingCrashRig is not { } finish)
        {
            return;
        }
        _pendingCrashRig = null;
        finish();
    }

    /// <summary>Binds this plane's crash rig: the runtime the crash and destroy defs play on, the
    /// def vector the struck surface indexes, the anchor they play against, and the two snapshots a
    /// respawn restores. One call rather than six assignments, so a rig cannot be half-bound.</summary>
    public void BindCrashRig(AnimRuntime runtime, SurfaceDefTable? defs, Node3D? anchor,
        IReadOnlyList<(Node3D Node, Transform3D RestPose)>? restPoses,
        IReadOnlyList<(Node3D Node, bool Visible)>? planeVisibility)
    {
        _crashRuntime = runtime;
        _crashAnchor = anchor;
        _lifecycle.CrashDefs = defs;
        _crashRestPoses = restPoses;
        _crashPlaneVisibility = planeVisibility;
    }

    /// <summary>Builds this rig's own <c>camera1</c>, the node the canopy-hole overlay poses
    /// AT_NODE, and hands it back. The answer is null on an AI rig, which draws no canopy, and a
    /// second call is free. The proxy follows the pilot's view camera, so each splitscreen pane's
    /// overlay sits in front of its eye. ⚠ Must exist before the rig binds. A crash runtime
    /// resolves a name over its bind scope alone, so the world's <c>camera1</c> is invisible to it.
    /// <c>PoseChannel.AtNode</c> answers a miss by posing at the origin without a word.</summary>
    public Node3D? EnsureViewCameraProxy()
    {
        if (_viewCameraProxy != null || !IsHumanPiloted)
            return _viewCameraProxy;
        var proxy = new Node3D { Name = CanopyHoleCue.ViewCameraNode };
        proxy.SetMeta(AnimRuntime.NameMeta, CanopyHoleCue.ViewCameraNode);
        AddChild(proxy);
        _viewCameraProxy = proxy;
        return proxy;
    }

    /// <summary>Runs one of the five <c>cockpit_bulletholes</c> defs through this plane's rig and
    /// answers how many instances it started (docs/org/weaponFire.md for what the two sequences
    /// draw). Public for the suites, which open a hole without a gun.
    /// ⚠ The def's own <c>Sound</c> events are silent on a human rig. Its runtime carries
    /// <c>SoundHandledElsewhere</c>, so <c>FlightAudio.OnWindowHit</c> stays the one source and the
    /// cue is not doubled.</summary>
    public int OpenCanopyHole(int hole)
    {
        if (PlaneModel is not { } model || CrashRuntime is not { } rig)
            return 0;
        // applyReset: false, the same rule every rig play follows. A RESET_STATE here would put the
        // already-open holes back behind the glass, which is reset_bulletholes' job at spawn.
        return rig.Play($"bullet{hole}", model, applyReset: false).Count;
    }

    /// <summary><c>--canopy-holes=</c>: opens the first <paramref name="count"/> glass holes as the
    /// aircraft spawns. It goes through the cue's own ledger and the same defs a landed round opens
    /// them with, so a scripted shot catches struck glass. Answers the anim instances
    /// started.</summary>
    public int PresetCanopyHoles(int count)
    {
        if (!IsHumanPiloted || _canopyHoles == null)
            return 0;
        int drew = 0;
        for (int hole = 1; hole <= count && hole <= CanopyHoleCue.HoleCount; hole++)
            if (_canopyHoles.ForceOpen(hole))
                drew += OpenCanopyHole(hole);
        Log.Info("weapons", $"canopy holes preset P{PlayerIndex + 1} open={count} closed={_canopyHoles.ClosedCount} drew={drew}");
        return drew;
    }

    /// <summary>The inverse of building inert: re-home this aircraft at <paramref name="pos"/>
    /// with its nose on <paramref name="lookAt"/>, put it back in play and respawn it there, the
    /// original's teleport-then-reactivate, in one call. <see cref="Respawn"/> does the rest of the
    /// work it always does (spawn speed and throttle, a healthy repaired airframe, full ammo, the
    /// start choreography), so a wave arrives flying rather than parked. Calling this on an
    /// aircraft already in play is simply that teleport-and-reset.</summary>
    public void Activate(Vector3 pos, Vector3 lookAt, Vector3? launchVelocity = null,
        bool carrierDrop = false, float? launchThrottle = null)
    {
        var dir = lookAt - pos;
        _spawnPos = pos;
        // A zero-length aim keeps the attitude it has, the same guard PlaceHeld makes.
        if (dir.LengthSquared() > 1e-6f)
            _spawnAttitude = Basis.LookingAt(dir.Normalized(), Vector3.Up);
        Inert = false;
        ArmSpawnTimers(carrierDrop);
        // The original snaps an activated vehicle to its net's nearest node (FUN_004b0f40 →
        // FUN_00432010), which is what makes a teleported wave patrol where it ARRIVED rather
        // than fly back to wherever it was parked.
        Pilot?.Patrol?.Reseat();
        Respawn();
        if (launchVelocity is { } velocity)
            _model.SetVelocity(velocity);
        // A launch dictates the lever the aircraft leaves on: a carrier drop nearly closes it,
        // a take-off run opens it. Written past the smoothing so the first step runs at it.
        float? lever = carrierDrop ? CarrierDropThrottle : launchThrottle;
        if (lever is { } opened)
        {
            SetLever(opened);
            _model.Throttle = opened;
            if (Pilot != null)
                Pilot.Throttle = opened;
            ExhaustSmoke?.Reset();
        }
    }

    /// <summary>`WARP_VEHICLE`'s plain waypoint (<c>FUN_00493fb0</c>): put this aeroplane at
    /// <paramref name="pos"/> yawed to <paramref name="headingDeg"/>, flying at
    /// <paramref name="speed"/> along the placed nose.
    /// ⚠ Not <see cref="Activate"/>: the warp writes a pose and a velocity and nothing else, so
    /// damage, ammo, fuel and the INERT bit all survive it. C4/M02 warps Blacke while he is still
    /// deactivated, and activating him here would put the boss up at mission start.</summary>
    public void WarpTo(Vector3 pos, float headingDeg, float speed)
    {
        // The roster block's own yaw convention (CampaignRoster's Forward), which is the one the
        // authored angle is written in: the shipped points sit within a few degrees of the aiv
        // spawn yaw of the aircraft they move.
        var attitude = new Basis(Vector3.Up, Mathf.DegToRad(headingDeg));
        _spawnPos = pos;
        _spawnAttitude = attitude;
        _model.Reset(pos, attitude, speed, _model.Throttle);
        _simPrev = _simCurr = _renderPose = new Transform3D(_model.Attitude, _model.Position);
        GlobalTransform = _simCurr;
        _sweep.Reset(); // a warp is a teleport too; the carried origin must not span it
        if (_cam != null && IsInsideTree())
            SnapCamera();
    }

    /// <summary>Hands a held aircraft back to the flight model where it stands, moving at
    /// <paramref name="velocity"/> with the lever at <paramref name="throttle"/>: the
    /// scripted-path follower's handoff, which the original makes by clearing the path flag and
    /// nothing else (<c>FUN_0048a110</c>), so no respawn and no spawn grace, and the collision
    /// sweep and ground blow run from the first flown step. The patrol net reseats where the
    /// aircraft is, since the run placed it and not the net.</summary>
    public void ReleaseHeld(Vector3 velocity, float throttle)
    {
        Held = false;
        _model.SetVelocity(velocity);
        SetLever(throttle);
        _model.Throttle = throttle;
        if (Pilot != null)
            Pilot.Throttle = throttle;
        ExhaustSmoke?.Reset();
        Pilot?.Patrol?.Reseat();
    }

    /// <summary>Weapon lab: pin the held airframe at <paramref name="pos"/> with its nose on
    /// <paramref name="lookAt"/>, at zero speed, click-to-place and <c>--weapon-target=</c>. Goes
    /// through the same <see cref="FlightModel.Reset"/> + <see cref="SnapCamera"/> pair
    /// <see cref="Respawn"/> uses. Sets the pin whether or not <see cref="Held"/> is on.</summary>
    public void PlaceHeld(Vector3 pos, Vector3 lookAt)
    {
        var dir = lookAt - pos;
        // A zero-length aim (clicked the plane's own position) would make LookingAt throw, keep
        // the attitude the plane already has rather than fail the placement.
        _heldAttitude = dir.LengthSquared() > 1e-6f
            ? Basis.LookingAt(dir.Normalized(), Vector3.Up)
            : _model.Attitude;
        _heldPos = pos;
        _heldPinned = true;
        _model.Reset(_heldPos, _heldAttitude, 0f, 0f);
        SetLever(0f);
        _simPrev = _simCurr = _renderPose = new Transform3D(_model.Attitude, _model.Position);
        GlobalTransform = _simCurr;
        // A held airframe is re-placed rather than flown, so the pair above is collapsed and the
        // frame callback skips its own interpolation for it. Hand the drawing to the session's
        // pose book instead, which is what carries an aeroplane down a scripted taxi path.
        RenderPoses.Record(this);
        if (_cam != null && IsInsideTree())
            SnapCamera();
    }

    /// <summary>An intro cutscene's own aircraft motion: draw this airframe at
    /// <paramref name="pose"/> while it is out of flight, or null to hand it back the pose it held
    /// when the staging began. The animation runtime owns the pose, so this writes it rather than
    /// deriving one; <see cref="Inert"/> keeps the aircraft out of play throughout, and the model's
    /// visibility is re-asserted on every call because a respawn resets it.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public void StageAt(Transform3D? pose)
    {
        if (pose is { } staged)
        {
            // The model itself, before the first held step pins it at zero: captured once, the
            // same way _stagedFrom is, so a hand-back naming no re-placement still resumes on the
            // airspeed and power the aircraft actually held rather than on stall.
            if (_stagedFrom == null)
            {
                _stagedSpeed = _model.Speed;
                _stagedThrottle = _model.Throttle;
            }

            _stagedFrom ??= GlobalTransform;
            GlobalTransform = staged;
            if (ShakePivot != null)
            {
                ShakePivot.Visible = true;
            }

            if (PlaneModel != null)
            {
                PlaneModel.Visible = true;
            }

            return;
        }

        if (_stagedFrom is not { } home)
        {
            return;
        }

        _stagedFrom = null;
        GlobalTransform = home;
        _simPrev = _simCurr = _renderPose = home;
        // The sweep's carried origin is where the last flown step entered, before the staging: a
        // drop that hands back 250 m on would otherwise sweep from the trigger through the drop
        // site and put the aeroplane back there on the contact (CM18's Devil's Horn).
        _sweep.Reset();
        // A re-placement moves the flight model as well as the drawn pose: the pin the held steps
        // re-assert is what the aeroplane flies out of once the hold clears.
        if (_resumePlaced)
        {
            _resumePlaced = false;
            _heldPos = home.Origin;
            _heldAttitude = home.Basis;
            _heldPinned = true;
            _model.Reset(_heldPos, _heldAttitude, ReplacedSpeed, _model.Throttle);
        }
        else if (_stagedSpeed is { } speed)
        {
            // No definition named a re-placement, so the model resumes on the airspeed and power
            // it held before staging began rather than on the zero Held pinned it at.
            _heldPos = home.Origin;
            _heldAttitude = home.Basis;
            _heldPinned = true;
            _model.Reset(_heldPos, _heldAttitude, speed, _stagedThrottle ?? 0f);
            _stagedSpeed = null;
            _stagedThrottle = null;
        }

        ApplyPresence();
    }

    /// <summary>The re-placement a cutscene's own callback authors: the aeroplane flies out of
    /// <paramref name="pose"/> rather than out of where <see cref="StageAt"/> found it, at the
    /// original's own release speed along the placed nose. Applied at the hand-back, because the
    /// hold zeroes the model's speed on every step it runs.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public void ResumeAt(Transform3D pose)
    {
        _stagedFrom = pose.Orthonormalized();
        _resumePlaced = true;
        // A definition that raises its handoff BEFORE this code (CM06's unhook) has already had
        // the aeroplane handed back, so there is no later hand-back to defer to: place it now, the
        // way the original writes the pose the instant the code lands.
        if (!Held)
        {
            StageAt(null);
        }
    }

    /// <summary>Hand this aircraft's own visibility to an outside vantage, or take it back. An
    /// episode owns the camera for its whole length, which silences the per-frame arm that
    /// re-asserts the first-person rules, so both edges are written here instead: presenting draws
    /// the airframe and takes the interior pass off the screen, and the hand-back puts back the
    /// rules for the view this pilot still has selected.</summary>
    public void SetViewedFromOutside(bool on)
    {
        if (on)
        {
            LeaveFirstPerson();
            return;
        }

        // ⚠ A crashed pilot keeps the crash cut's own exit from first person: _Process writes
        // nothing to the camera while crashed, so a restore here would hold the interior over the
        // crash camera until the respawn.
        if (Crashed || _cam == null)
        {
            return;
        }

        // The selection, not a frame's pose: a held numpad key or a look-behind is not something a
        // hand-back can read, and the pilot's own view is what the episode owes them back.
        Cockpit?.Apply(_cam.ViewMode, _cam.FirstPerson);
        ShowPanel(CockpitVisibility.Rules(_cam.ViewMode, _cam.FirstPerson).Interior);
        CockpitPass?.Sync(_renderPose.Basis, _cam, Shake?.Roll ?? 0f, Projectiles?.ActiveMuzzleLights());
    }

    /// <summary>Weapon lab: point the gun selector at a firable gun group (0-based, clamped),
    /// the programmatic twin of F3 / D-pad Right, which only cycles. Interactively that cycle still
    /// wins the next time it is pressed; <see cref="InitialGunSelect"/> is the _Ready-time
    /// equivalent and cannot be re-applied once the rig is built.</summary>
    public void SelectGunGroup(int index) => _fire?.SelectGunGroup(index);

    /// <summary>Weapon lab: point the hardpoint selector at a pylon (0-based, clamped), the
    /// programmatic twin of F5. Unlike F5 this lands on an EMPTY pylon too (the lab picks a mount to
    /// look at, not a mount to fire); the firing path's own armed scan still advances off it when
    /// the trigger is pulled.</summary>
    public void SelectPylon(int index) => _fire?.SelectPylon(index);

    /// <summary>Enter photo mode: the pause key goes silent for the duration.</summary>
    public void BeginPhotoMode() => InPhotoMode = true;

    /// <summary>Leave photo mode, seeding the pause key's edge flag from the CURRENT device state.
    /// ⚠ Clearing the flag alone is a bug, and was one: <see cref="PauseTogglePressed"/> answered
    /// false for the whole mode, so <c>_pausePrev</c> is false, and the Escape still under the
    /// player's finger the instant the gate lifts then reads as a fresh press and unpauses the
    /// session that photo mode was opened from. Same hazard as priming a menu reader, one edge
    /// flag further down.</summary>
    public void EndPhotoMode()
    {
        InPhotoMode = false;
        // The bare action, not PauseTogglePressed: that still reads the gates, and the point
        // is to record what the hands are doing regardless of them.
        PollInput();
        _pausePrev = _actions.Held(InputAction.Pause);
    }

    /// <summary>Enter the options leaf over the pause: the pause key goes silent for the duration.</summary>
    public void BeginPauseLeaf() => InPauseLeaf = true;

    /// <summary>Leave the options leaf, seeding the pause key's edge flag from the CURRENT device
    /// state for the reason <see cref="EndPhotoMode"/> does: the page is left with Escape, and that
    /// same Escape still under the player's finger would otherwise read as a fresh press and resume
    /// the mission the leaf was opened from.</summary>
    public void EndPauseLeaf()
    {
        InPauseLeaf = false;
        PollInput();
        _pausePrev = _actions.Held(InputAction.Pause);
    }

    /// <summary>Photo mode's forward onto <see cref="FlightHud.SetVisible"/>: the session drives it
    /// per rig and holds no HUD of its own.</summary>
    public void SetPilotHudVisible(bool visible) => _pilotHud.SetVisible(visible);

    /// <summary>The decoded bracket gate for the targeting marker (<c>FUN_004574d0</c>): whether the
    /// SELECTED gun group could reach an intercept inside the weapon's authored <c>RANGE</c>. That,
    /// not a HUD distance constant, is the original's threshold, so the marker is weapon-dependent.
    /// With no gun group resolvable the original never rejects, and neither does this.
    /// <paramref name="margin"/> is the hysteresis: zero to turn the brackets on,
    /// <see cref="TargetHud.BracketHysteresis"/> to keep them on.</summary>
    public bool GunReachesTarget(Vector3 targetPos, Vector3 targetVel, float margin = 0f)
    {
        if (SelectedGun() is not { } sel)
        {
            return true;
        }

        float speed = sel.Weapon.Velocity ?? ProjectilePool.DefaultVelocity;
        float range = sel.Weapon.Range ?? 0f;
        return TargetHud.GunReaches(MuzzleMidpoint(sel), _model.VelocityDir * _model.Speed, speed,
            range + margin, targetPos, targetVel);
    }

    /// <summary>--crash[=frame]: forces this player's crash outside any live collision. No struck
    /// body by default, so the selection cascade takes its null-material arm and resolves slot 0
    /// (<c>player_crash_default</c>) however the plane is posed. <paramref name="killer"/> lets
    /// <c>--debug-scoreboard --vs</c> show a real, attributed kill. <paramref name="struckBody"/>
    /// lets a suite pass a real <see cref="SceneBuilder.SurfaceIdMeta"/> stamp instead.</summary>
    public void DebugForceCrash(int? killer = null, Node? struckBody = null)
    {
        // An INERT wave-parked plane is not in the fight; --crash sweeps every spawned AI plane.
        if (InPlay)
            Crash(_model.Position, "debug-crash", "test", struckBody, killer);
    }

    /// <summary>One projectile hit on this plane: maps it to the data part and spends the weapon's
    /// ARMOR_DAMAGE/HEALTH_DAMAGE through <see cref="PlaneDamage.Apply"/>, the decoded flow, see
    /// docs/org/vehicleDamage.md. Exhausted whole-vehicle health downs the plane through
    /// <see cref="Crash"/>, as a fatal contact does. No cooldown: every round counts.
    /// <paramref name="shooter"/> carries into <see cref="Downed"/> as the killer.
    /// <paramref name="damageScale"/>: 1 for a direct round, the blast falloff share otherwise.</summary>
    public void TakeProjectileHit(WeaponDef weapon, Vector3 impact, string colliderPart, int shooter,
        float damageScale = 1f)
    {
        if (!InPlay)
            return;
        // An armed rig is built before the first round lands, not on the kill: the damage stages
        // this hit may cross play out of it, and its wreck is what the hull becomes.
        EnsureCrashRig();
        // The attacker queue (FUN_004b9770): whoever just shot this pilot goes to the END of the
        // queue `Next Enemy/Objective` walks backwards. The gate is a shooter on a DIFFERENT,
        // non-zero team, so a friendly-fire round records nothing.
        if (Targeting != null && Projectiles?.RigOfShooter(shooter) is { } attacker
            && attacker.Team != Team && attacker.Team != AimAssist.NeutralTeam
            && Team != AimAssist.NeutralTeam)
        {
            Targeting.RecordAttacker(attacker);
        }
        // The incoming-fire cues, gated as the original gates them: a CANNON round (0x004b9e7e) on
        // the pilot's own aeroplane. ⚠ Never dispatch these from the contact path, where a scrape
        // is "I hit something" rather than "I was shot" and would make both readings wrong.
        bool absorbed = weapon.IsCannon && IsHumanPiloted && OnCannonHit();
        // Runs even with no damage data, so a plane nothing tracks HP for still visibly takes fire.
        // ⚠ Ahead of the absorb return on purpose: the original's own shake kick (0x004b9d26) sits
        // ahead of the arm, so an absorbed round still rocks the airframe.
        if (weapon.Caliber is { } shakeCal)
            Shake?.BulletHit(shakeCal);
        else if (damageScale < 1f)
            Shake?.ExplosionAt((weapon.ArmorDamage ?? 0f) * damageScale);
        else
            Shake?.MissileHit(weapon.ArmorDamage ?? 0f, weapon.HighExplosive);
        // The pad's half of the same event. Two effects, not one: the original splits a gun round
        // from everything else and gives each its own damage edge (docs/org/input.md).
        if (IsHumanPiloted)
        {
            float took = (weapon.ArmorDamage ?? 0f) * damageScale;
            _rumble.Play(weapon.Caliber.HasValue
                ? PadRumble.CannonHit(took)
                : PadRumble.OrdnanceHit(took));
        }
        // The shield took it: the damage pair is discarded where the original zeroes it
        // (0x004b9ea4) and returns, so nothing below this line runs, no armour spend, no hit
        // marker, no kill test.
        if (absorbed)
            return;
        if (Damage == null)
            return;
        // The sim pose, not GlobalTransform: the render half may hold an interpolated frame.
        var pose = new Transform3D(_model.Attitude, _model.Position);
        var localImpact = pose.AffineInverse() * impact;
        string dataPart = PlaneDamage.MapStruckPart(colliderPart, localImpact);
        // The steady-hand roll reads the pools the hit arrives at, so they are taken before the
        // spend moves them (docs/org/aiControlLaw.md, "The roll itself").
        float poolArmorBefore = Damage.WholeArmor, poolHealthBefore = Damage.WholeHealth;
        // Apply's answer is the struck zone, not the geometric guess: it may redirect a dead-zone
        // hit to a survivor (docs/org/vehicleDamage.md).
        var state = Damage.Apply(dataPart,
            (weapon.HealthDamage ?? 0f) * damageScale, (weapon.ArmorDamage ?? 0f) * damageScale);
        string struckPart = state?.Def.Name ?? dataPart;
        if (state != null)
        {
            Visuals?.OnPartDamage(struckPart, state.HealthFraction);
            _pilotHud.OnPartDamage(struckPart); // damage dial: hit zone blinks 5 s
        }

        // the def-level stages run off the hull pool even when the round went zone-less
        Visuals?.OnHullDamage(Damage.SummaryHealthFraction);

        if (_projectileHitsLogged < 6)
        {
            _projectileHitsLogged++;
            string zone = state != null
                ? Log.Format($"armor={state.Armor:0.0}/{state.Def.MaxArmor:0} hp={state.Hp:0.0}/{state.Def.MaxHp:0} ")
                : string.Empty;
            Log.Info("weapons", $"shot hit P{PlayerIndex + 1} ({colliderPart}→{struckPart}): {weapon.Id} {zone}hull={Damage.WholeHealth:0.0}/{Damage.WholeHealthMax:0}");
        }

        // The decoded kill rule: whole-vehicle health current at or below zero (never a part
        // flag), reachable through the zone-less overflow, so it is tested on every hit.
        if (Damage.IsDestroyed)
        {
            Log.Info("weapons", $"vehicle health exhausted ({struckPart} last), shot down by {weapon.Id}");
            Destroy(impact, $"gunfire ({weapon.Id})", colliderPart,
                killer: shooter != ProjectilePool.NoShooter ? shooter : null);
            return;
        }
        DamageApplied?.Invoke(this, shooter != ProjectilePool.NoShooter ? shooter : null);
        if (!IsHumanPiloted && Pilot?.Machine is { } machine)
        {
            machine.NotifyDamage((weapon.ArmorDamage ?? 0f) * damageScale,
                (weapon.HealthDamage ?? 0f) * damageScale, poolArmorBefore, poolHealthBefore);
        }
        _pilotHud.Flash(state != null
            ? $"⚠ HIT {struckPart.ToUpperInvariant()} {state.Fraction * 100f:0}%"
            : $"⚠ HIT HULL {Damage.SummaryHealthFraction * 100f:0}%");
    }

    /// <summary>The AI stun's entry for a struck aircraft (decoded: <c>FUN_004200d0</c>; a
    /// <c>SONIC</c>/<c>FLASH</c> burst passes <see cref="DisablingIntensity"/>'s stun seconds, the
    /// smoke screen <c>smokescreen_stun_interval</c>). The original's victim guards live here: never
    /// a human (their half is the screen wash, no control is touched), never a dead or inert
    /// airframe, never an aircraft with no AI pilot; the <c>+0xf8</c> byte is not modelled on our
    /// side. Returns whether the pilot was stunned. Re-entrant: see <see cref="AiPilot.Stun"/>.</summary>
    public bool TryStunPilot(float seconds)
    {
        if (IsHumanPiloted || !InPlay || Pilot is not { } pilot || seconds <= 0f)
            return false;
        pilot.Stun(seconds);
        return true;
    }

    /// <summary>The choker's entry for a struck aircraft (decoded: <c>FUN_004b1690</c> under
    /// <c>FUN_004b9bc0</c>'s <c>TANGLER</c> branch; the seconds come from
    /// <see cref="TanglerChoke.Duration"/>). Unlike the stun this hits a human exactly as it hits an
    /// AI, because the original's choker branch has no player guard: the engine is a mechanical
    /// system and nobody is told about it, least of all the AI. Returns whether the engine is now
    /// dead. Re-entrant, and extend-only: see <see cref="FlightModel.ChokeEngine"/>.</summary>
    public bool TryChokeEngine(float seconds)
    {
        if (!InPlay || seconds <= 0f)
            return false;
        bool wasDead = _model.EngineDead;
        _model.ChokeEngine(seconds);
        if (!wasDead && _model.EngineDead)
            Log.Info("weapons", $"engine choked on P{PlayerIndex + 1} for {seconds:0.0} s");
        // On the choking frame, as the original's edge sits inside the mask setter itself rather
        // than on the next step.
        SyncPropSlot();
        return _model.EngineDead;
    }

    /// <summary>The receiving half of a plane-versus-plane ram: the striker's decoded
    /// pair spent through this plane's own ledger, the flow a projectile hit uses.
    /// ⚠ Do not suppress the struck plane hitting back. Each aircraft sweeps itself, so both
    /// resolve the contact; that is the original's behaviour, not a double-count.</summary>
    public void TakeCollisionHit(float armorDamage, float healthDamage, Vector3 impact, int striker)
    {
        if (!InPlay || Damage == null)
            return;
        EnsureCrashRig();
        var pose = new Transform3D(_model.Attitude, _model.Position);
        string dataPart = PlaneDamage.MapStruckPart("center", pose.AffineInverse() * impact);
        var state = Damage.Apply(dataPart, healthDamage, armorDamage);
        string struckPart = state?.Def.Name ?? dataPart;
        if (state != null)
        {
            Visuals?.OnPartDamage(struckPart, state.HealthFraction);
            _pilotHud.OnPartDamage(struckPart);
        }

        Visuals?.OnHullDamage(Damage.SummaryHealthFraction);
        Log.Info("flight",
            $"rammed P{PlayerIndex + 1} by P{striker + 1} ({struckPart}): a={armorDamage:0.0} h={healthDamage:0.0} hull={Damage.WholeHealth:0.0}/{Damage.WholeHealthMax:0}");
        if (Damage.IsDestroyed)
        {
            Log.Info("flight", $"vehicle health exhausted ({struckPart} last), rammed by P{striker + 1}");
            Destroy(impact, "collision", "center", null);
        }
    }

    /// <summary>Arms this plane's own collision-free window, the other half of a ram's grace pair:
    /// the caller arms its own side and calls this on the struck rig so neither re-resolves the
    /// overlap they are still in. Kept off <see cref="TakeCollisionHit"/> itself because that
    /// method's test callers exercise the receiving half alone, with no ram and no grace to arm.</summary>
    public void ArmCollisionGrace() => _lifecycle.ArmCollisionGrace();

    /// <summary>One session-simulation flight step: input, flight model, collision, weapons and
    /// the stunt clock.</summary>
    public void SimStep(float dt)
    {
        // An inert airframe takes no step at all, no stunt clock, no input, no flight
        // model, no collision sweep, no weapons. Guarded here rather than in the session's loop so
        // every caller (the session simulation or a focused suite) honours it in one place.
        if (Inert)
            return;

        PollInput();

        // Advance the stunt clock every physics frame, including through the crash freeze so the
        // clock never stops (a deliberate rule); it stops only at AllComplete (inside Tick). A
        // halted GameClock stops the calls entirely, so the timer freezes with the rest of the sim.
        Stunt?.Tick(dt);

        // The incoming-fire block's own tick, one interval feeding both cues: the shield charges or
        // drains on the sim clock, and an interval that closed with a hit is what may open a hole.
        TickIncomingFire(dt);

        // Race players finish STAGGERED by index, exercising the real one-finishes-while-others-fly
        // path instead of four identical totals landing on frame one.
        if (DebugCompleteStunt && Stunt is { AllComplete: false }
            && (Race == null || Stunt.Elapsed >= PlayerIndex * DebugFinishStagger))
            Stunt.DebugCompleteAll(Race != null ? PlayerIndex * 2f : 0f);

        // ⚠ The rematch shortcuts are NOT read here; a results board halts the clock, so this step
        // never runs while one is up. PollResultsShortcuts reads them off the rendered frame.
        if (Match is { Completed: true })
            return;

        if (Race is { AllFinished: true } && Stunt is { AllComplete: true })
            return;

        if (SoloBoardHolds)
        {
            _simPrev = _simCurr;   // hold the finish pose, no stale pair left to interpolate
            return;
        }
        _autoRestartIn = AircraftLifecycle.AutoRespawnDelay; // re-armed while the run is live

        if (Crashed)
        {
            // ⚠ Out of lives, or held through an ending, neither R nor AutoRespawnAfter's timer
            // may bring the pilot back. It is checked here rather than by clearing
            // AutoRespawnAfter, which R overrides. A hull that dies inside a hold keeps falling.
            if (Spectating || CommandsHeld)
            {
                StepWreckFall(dt);
                return;
            }
            if (RespawnPressed() || _lifecycle.TickAutoRespawn(dt, _holdSegments != null))
            {
                Respawn();
                return;
            }
            // The one thing a dead aircraft still does: fall. No input, no weapons, no stunt
            // clock, only the hull, on its way to its ground-impact def.
            StepWreckFall(dt);
            return;
        }

        // Only a step that reads the controls measures a gap; a held or wholly held seat reads none.
        _leverGap = 0f;

        // Weapon lab: a HELD airframe skips input/model/collision and re-asserts its pinned pose
        // instead; weapons/gauges/telemetry below run exactly as in flight, through _model.Reset.
        if (_held)
        {
            if (!_heldPinned)
            {
                _heldPos = _model.Position;
                _heldAttitude = _model.Attitude;
                _heldPinned = true;
            }
            _model.Reset(_heldPos, _heldAttitude, 0f, 0f);
            SetLever(0f);        // the throttle ramp is input-driven and no input is read while held
            _lastInput = default; // control surfaces sit neutral
        }
        else
        {
            // A wholly held seat commands nothing. The stick centres over the lever it was left on,
            // so the aeroplane flies on as trimmed, not held still or cut to idle. The narrower
            // hold leaves the stick and the throttle exactly as the pilot works them.
            var input = ControlHold == FlightControlHold.All
                ? new FlightInput { Throttle = _throttle }
                : InputSource.Read(dt);
            // Read AFTER the input: R respawns inside it, and a sweep from the pose before that
            // respawn would run the whole way to the spawn point and strike whatever lies between.
            var entered = _model.Position;       // the position this step enters with
            // The response is absent for 1.5 s, then AI terms run at 15% for 1 s.
            // Keep the probe off too, so the log records response rather than an inert hit.
            bool groundBlowReady = IsHumanPiloted || !_lifecycle.CollisionGraceActive;
            if (groundBlowReady)
                ProbeGroundBlow(ref input);  // reads the pose this step ENTERED with, as the original does
            input.AiGroundBlowScale = !groundBlowReady ? -1f
                : _lifecycle.PostDropGroundBlowActive ? 0.15f : 1f;
            input.NearestHumanDistSqM = NearestHumanDistSqM();
            AdvanceNitro(dt);
            input.Boost = Nitro.Boosting;
            _lastInput = input;
            _grazeReactionCooldown -= dt;
            // The original sweeps on every other step and carries the skipped step's motion into
            // the next sweep, so the sweep from `prev` covers two steps of motion after a skipped
            // one. Every contact it resolves spends the pair; nothing else gates the spend.
            bool onSweepStep = _sweep.Advance(entered, out var prev);
            _lifecycle.TickTimers(dt);
            // A danger-zone run writes the pose off its ribbon in place of the physics, the
            // original's state-5 bypass (FUN_004897c0); the sweep below still runs, as its does.
            if (!IsHumanPiloted && Pilot is { RailPose: { } rail } onRails)
                _model.Reset(rail.Origin, rail.Basis, onRails.RailSpeed, _throttle);
            else
                _model.Step(input, dt);

            // The engine-out timer ran down inside that step, so the restart edge is read here.
            // ⚠ Never on a spent hull. The death routine put the still blade there to stay, and
            // its timer expiring must not spin a wreck's discs back up.
            if (!Destroyed)
                SyncPropSlot();

            // The airframe boxes sweep along the carried motion; the center ray stays as an
            // anti-tunnelling backstop. Only the shapeless fallback keeps a nose margin on it.
            var to = _model.Position;
            var step = to - prev;
            float len = step.Length();
            float margin = Collider == null ? CollisionMargin : 0f;
            var probeEnd = len > 1e-4f ? to + step / len * margin : to;
            // ⚠ The grace window suppresses the SWEEP, not just the damage: while it is live this
            // plane has no collision at all (obj+0xAC, docs/org/flightModel.md).
            bool sweeping = onSweepStep && !_lifecycle.CollisionGraceActive;
            ContactReport contact = default;
            Node? hitBody = null;
            // A human rig sweeps the airframe hulls; an AI rig sweeps its def's collision probes,
            // the original's shape (see SweepProbes): one origin point on every AI def, so its
            // wings clip through a slot the hull cannot pass, the CM13 racers' dzpath2 arch first.
            bool hit = sweeping && (IsHumanPiloted
                ? SweepAirframe(prev, step, out contact, out hitBody)
                : SweepProbes(prev, step, out contact, out hitBody));
            if (!hit && sweeping)
                hit = CenterRayContact(prev, probeEnd, step, len, out contact, out hitBody);
            // No contact means the boxes cleared the whole motion, which is where they are drawn.
            if (_probe != null)
                DrawProbe(prev, probeEnd, prev + step * (hit ? contact.StopFraction : 1f), hit);
            // The decoded contact: the resolver decides it whole (both parties' damage off one
            // severity cosine, then this plane's fate), and what comes back is performed here.
            if (hit)
            {
                var outcome = Contacts.Resolve(in contact, Striking(),
                    new ContactEffects(this, hitBody, in contact, prev, step));
                PerformContact(in outcome, in contact, hitBody);
                if (outcome.Fate == ContactFate.Crash)
                {
                    Crash(contact.Impact, contact.ColliderName, contact.Part, hitBody);
                    return;
                }
            }
        }

        _simPrev = _simCurr;
        _simCurr = _renderPose = new Transform3D(_model.Attitude, _model.Position);
        GlobalTransform = _simCurr;

        // The dynamic chase radius advances on the sim step, not the render frame: the transient's
        // speed lag must see one cadence, and the authored relaxation rate is per real second,
        // which this clock is. A crash or halt stops the calls, holding the radius where it stands.
        _cam?.UpdateDynamics(dt, _model.Speed);

        // ⚠ The smoke belongs on this clock, never on the rendered frame. The gap it charges from
        // closes only inside this step, so a frame carrying none would charge it twice. Past the
        // pose write above, so the plume emits at this step's pose.
        ExhaustSmoke?.Update(dt, _leverGap);

        // The AI gunner: acquire/hold the target and decide this tick's trigger and lead
        // BEFORE the fire step reads them. Runs for AI pilots only; a gunner-less AI keeps the
        // released trigger it always had.
        if (!IsHumanPiloted && Pilot?.Gunner is { } aiGunner)
        {
            DriveAiGunner(aiGunner);
            // The ordnance half runs off the gunner's target, never its own acquisition, so the
            // two weapon classes cannot chase different aircraft the way the original never does.
            if (Pilot?.Rocketeer is { } aiRocketeer)
                DriveAiRocketeer(aiRocketeer, aiGunner, dt);
        }

        // Weapons: poll the raw held controls, let FireControl decide (selector edges, fire
        // clocks, ammo draw-down, cues), then perform its outcome against the pool and audio.
        if (_fire != null)
        {
            // The weapon lab flips these on the public fields at runtime (bank switch, toggles;
            // GameSession also sets InfiniteAmmo post-_Ready), mirror them into the machine.
            _fire.AutoFireRockets = AutoFireRockets;
            _fire.InfiniteAmmo = InfiniteAmmo;
            var fireInputs = ReadSelectorInputs();
            // A null pool could spawn nothing, feed the triggers as released so no ammo is
            // decided away on rounds that could never fire. An AI pilot's gunner IS its
            // trigger; without one the raw controls (--fire's AutoFire included) decide.
            fireInputs.FireHeld = Projectiles != null
                && (!IsHumanPiloted && Pilot?.Gunner is { } g ? g.WantsFire : FirePressed());
            fireInputs.RocketHeld = Projectiles != null
                && (!IsHumanPiloted && Pilot?.Rocketeer is { } r ? r.WantsFire : RocketFirePressed());
            // The assist's forget + catch-up pass (docs/org/aim-assist.md "Per frame"), ticked on
            // the PRE-shot state; must run before a round out this frame restamps last-update.
            if (IsHumanPiloted)
            {
                double aimNow = GameClock.Current?.Time ?? 0.0;
                for (int gi = 0; gi < _aimSlots.Length; gi++)
                {
                    AimAssist.Tick(_aimSlots[gi], aimNow, dt,
                        _model.Stats.StickyBulletForgetInterval, _model.Stats.StickyBulletCatchupRate);
                }
            }
            ApplyFireOutcome(_fire.Step(dt, fireInputs));
        }
        Ordnance?.Update(InfiniteAmmo);   // hide a pylon's mounted rocket the moment it fired its last

        // The carried turret gunners: each tracks and fires on its own, into the same
        // shared pool, under this pilot's shooter id. The crash branch above already returned,
        // so a downed host's gunners take no further ticks.
        int turretShots = 0;
        foreach (var turret in Turrets)
        {
            turret.SimStep(dt);
            turretShots += turret.ShotsFired;
        }

        // The original rumbles for the pilot's OWN gunner alone. Its turret effect is gated on the
        // firing turret belonging to the player's aeroplane, and every round restarts the loop.
        if (turretShots != _turretShots)
        {
            _turretShots = turretShots;
            if (IsHumanPiloted)
                _rumble.Play(RumbleEvent.TurretFire);
        }

        // The plane wobble: overspeed drive plus this tick's fire/hit kicks, written as
        // visual-only roll to the pivot the model hangs under. Physics, aim and the camera
        // read this node's transform, which the pivot sits below, never the wobble.
        float speedRatio = _model.Speed / Mathf.Max(1f, _model.Stats.FdSpeed);
        if (Shake != null)
        {
            Shake.SetSpeedRatio(speedRatio);
            Shake.Advance(dt);
            if (ShakePivot != null)
                ShakePivot.Rotation = new Vector3(0f, 0f, Shake.Roll);
        }

        // The pad's overspeed rattle rides the same gate the wobble's authored min_speed puts on
        // it, past the plane's rated maximum. It stops on its own once the dive ends.
        if (IsHumanPiloted)
            _rumble.Overspeed(speedRatio > 1f, GameClock.Current?.Time ?? 0.0);

        // Stunt run: flew-through-a-danger-zone test against this frame's committed position.
        Stunt?.Update(_model.Position);

        // The camera latch reads the same committed position, after the run's own test, so a
        // photograph and the zone it completes land on one frame.
        StuntShots?.Update(_model.Position);

        // height over ground for the altimeter's LOW ALT warning: one ray straight
        // down per physics frame (world + map-edge extension colliders)
        _pilotHud.StepAgl(World, _model.Position, Body?.ExcludeSelf);

        // Backstop if the swept ray ever misses. A HELD plane is exempt: it is exactly where the lab
        // parked it (below the map is a legal place to hold), and a respawn would fling it away from
        // the target it was aimed at.
        _sinceUnderMapReport += dt;
        if (!_held && _model.Position.Y < UnderMapY)
        {
            // ⚠ Rate-limited rather than one line per reset. An aircraft stuck under the map trips
            // this every physics frame, so an ungated print is one console write per frame per
            // plane (docs/verification.md PERF-23); the running count is what says it is a loop.
            _underMapResets++;
            if (_sinceUnderMapReport >= UnderMapReportInterval)
            {
                _sinceUnderMapReport = 0f;
                var under = _model.Position;
                Log.Warn("flight", $"under-map backstop: {Name} (P{PlayerIndex + 1}) below y={UnderMapY:0} at ({under.X:0},{under.Y:0},{under.Z:0}), respawned to its spawn; {_underMapResets} reset(s) so far");
            }
            Respawn();
        }

        _sinceTelemetry += dt;
        if (_sinceTelemetry >= 1.0)
        {
            _sinceTelemetry = 0;
            // ⚠ Ask the filter before formatting or emitting. Every live aircraft crosses this
            // boundary on the same sim step, so an unasked line puts one console write per
            // aircraft inside a single physics tick (docs/verification.md PERF-23).
            if (Log.ConsoleShows("flight", Log.Level.Debug))
            {
                var p = _model.Position;
                var rates = _model.PhysicalBodyRates;
                // path = climb/dive angle of the flight path; nose = the attitude's pitch;
                // wv = wing verticality |up·Y| (1 level/inverted, 0 knife-edge), the nose-chase factor
                float path = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(_model.VelocityDir.Y, -1f, 1f)));
                float nose = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-_model.Attitude.Z.Y, -1f, 1f)));
                float wv = Mathf.Abs(_model.Attitude.Y.Dot(Vector3.Up));
                string agl = _pilotHud.AglMeters < float.MaxValue
                    ? Log.Format($" agl={_pilotHud.AglMeters:0}")
                    : "";
                Log.Debug("flight", $"telemetry pos=({p.X:0},{p.Y:0},{p.Z:0}) spd={_model.Speed:0.0} m/s thr={_model.Throttle:0.00} rates=({rates.X:0.00},{rates.Y:0.00},{rates.Z:0.00}) path={path:0}° nose={nose:0}° wv={wv:0.00}{agl}");
            }
        }
    }

    /// <summary>Relative mouse travel, the only reading a captured pointer still gives. Banked for
    /// the stick's virtual cursor and for head-look alike, and only while this seat holds the
    /// mouse. A session that never takes the mouse banks nothing here.</summary>
    public override void _Input(InputEvent @event)
    {
        if (_mouse.Holding && @event is InputEventMouseMotion motion)
            _mouse.Moved(motion.Relative);
    }

    /// <summary>Gives the desktop mouse back on the way out. ⚠ Do not drop this. A session left
    /// through the pause board's EXIT tears the seat down without a halt of its own. The menu
    /// behind it would then come up with a pointer nothing hands back.</summary>
    public override void _ExitTree() => ReleaseMouseCapture();

    public override void _Process(double delta)
    {
        using var _ = ProcessSiteCost.Enter(ProcessSite.Flight);
        PollInput();
        var clock = GameClock.Current;
        if (!Inert)
            PollResultsShortcuts((float)delta);
        // ⚠ Ahead of the inert return, for a rig that HAS a pause key. --debug-spectate flags
        // every human rig inert (GameSession.ApplyDebugSpectate), so swallowing the key here
        // leaves a spectated session with no way to halt the picture at all.
        bool halted = false;
        if (AllowPause || !Inert)
            halted = PollPauseAndHalt(clock);
        // ⚠ Ahead of the inert return as well. A seat flagged inert mid-session must give the
        // pointer back, and this is the only frame that would notice.
        StepMouseCapture(halted);
        // Nothing left to draw, animate, interpolate or point a camera at while inert.
        if (Inert)
            return;

        // ⚠ A halt does NOT orbit: a board's cursor keys ARE the orbit keys, so the free look
        // lives behind its Photo Mode row instead (BL-429, docs/architecture.md). The weapon lab's
        // hold keeps its own orbit, seeded on the edge so entering it never jumps.
        bool orbiting = Held;
        if ((orbiting && !_orbitPrev) || _reseedOrbit)
        {
            _reseedOrbit = false;
            _cam?.SeedOrbit(_model.Position);
        }
        _orbitPrev = orbiting;
        // Plane state, animators, audio ramps, is sim time, so it freezes and scales with it.
        float simDt = clock?.FrameDt ?? (float)delta;
        if (!orbiting)
        {
            // Draw the plane between its last two sim poses (see the _simPrev/_simCurr fields).
            // Skipped while crashed: the sim pair is stale and the wreck owns the visuals.
            // ⚠ The fraction is the session-wide one, never the engine's raw reading.
            if ((clock == null || !clock.ParentDriven) && !Crashed)
            {
                _renderPose = _simPrev.InterpolateWith(_simCurr, RenderPoses.Fraction);
                GlobalTransform = _renderPose;
            }
        }
        if (CameraOwned || _cam == null)
        {
            // The lab's free camera has the view, every camera write here would fight it,
            // and an AI rig has no camera at all.
        }
        else if (orbiting)
        {
            // The orbit camera runs on wall time on purpose: a held airframe is a stopped subject
            // with the world still running, and the point is to look around it.
            var (yawIn, pitchIn, zoomIn) = OrbitInput();
            _cam.Orbit((float)delta, _model.Position, yawIn, pitchIn, zoomIn);
        }
        else if (halted)
        {
            // ⚠ Never write the camera on a halted frame: a board's menu cursor reads the same
            // WASD/arrows/left stick OrbitInput does, so choosing a menu row would swing the view.
            // Writing nothing leaves the menu over the still frame the board appeared on.
        }
        else if (Crashed)
        {
            // The authored crash camera holds the pose Crash() cut to, the original's camera does
            // not move after the cut (footage). The DEATH camera is the exception: its spot is
            // fixed but its aim is not, so it is stepped to keep the falling wreck framed.
            if (_deathCamera)
            {
                StepDeathView();
            }
        }
        else
        {
            PollViewModeKeys();
            // The zoom pair (BL-433), never while orbiting: the weapon lab's held orbit reads the
            // same two actions for its dolly (OrbitInput).
            _cam.UpdateZoom(simDt);
            // Default to the external FOV global; the FirstPerson arm below overrides it, so a
            // look-behind while SELECTED Cockpit/Nose gets the first-person FOV back on release.
            _cam.RestoreExternalFov();
            bool firstPersonPose = false;
            var logged = CameraView.Chase;
            if (_cam.FlybyActive)
            {
                // Above every held pose on purpose: the flyby is a camera the aeroplane was put
                // into, not a pose held over the pilot's selection, so nothing overrides it while
                // it runs. Sim time, so the pass survives a frame-rate change and a halt freezes it.
                _cam.FlybyView((float)(clock?.Time ?? 0.0), _renderPose, _model.Speed,
                    World, Body?.ExcludeSelf);
                logged = CameraView.Flyby;
            }
            else if (_cam.FirstPerson)
            {
                // Rigid at cockpit_camera (wobble inherited), the mode's own FOV, aimed by the
                // head. Look-back stays IN the cockpit, the head snapped to dead astern while held,
                // as the original does. This arm therefore sits above the look-behind cut below.
                _cam.StepHead(simDt, _cam.BackActive()
                    ? new HeadLookInput(0f, -1f, 0f, 0f, false, ForceSnap: true)
                    : HeadLookRead(), HeadLook.FirstPersonElevationFloor);
                _cam.FirstPersonView(_renderPose);
                _cam.ApplyFirstPersonFov();
                firstPersonPose = true;
                logged = _cam.ViewMode == PilotViewMode.Nose ? CameraView.Nose : CameraView.Cockpit;
            }
            else if (_cam.BackActive())
            {
                _cam.BackView(_renderPose);
                logged = CameraView.Back;
            }
            else
            {
                // The one head, on the chase camera's own floor, so the snap cluster, the centre
                // key and the mouse swing this view exactly as they aim the cockpit. The pad is
                // left out: it keeps the absolute PadLook path below, in both views.
                _cam.StepHead(simDt, HeadLookRead(includePad: false), HeadLook.ChaseElevationFloor);
                var (lookX, lookY) = PadLookInput();
                _chaseLook.Step(simDt, lookX, lookY);
                if (_chaseLook.Active)
                {
                    // E42: the right stick swings the view around the plane instead of
                    // the usual chase pose, see CameraController.PadLook.
                    _cam.PadLook(_renderPose, _chaseLook.X, _chaseLook.Y);
                    logged = CameraView.PadLook;
                }
                else
                {
                    // Fed simDt, not wall time, so a scripted flight capture stays frame-rate
                    // independent; fed the DRAWN pose, same rule as the rigid views above.
                    _cam.Chase(simDt, _renderPose.Origin, _renderPose.Basis);
                    logged = _cam.Head.Settled ? CameraView.Chase : CameraView.Look;
                }
            }
            // Keyed to the pose this frame actually took, not to the selection, a look-behind
            // puts the camera outside the aircraft and must bring its body back while held.
            Cockpit?.Apply(_cam.ViewMode, firstPersonPose);
            // Same rule, so the panel is driven exactly on the frames it is on the screen.
            ShowPanel(CockpitVisibility.Rules(_cam.ViewMode, firstPersonPose).Interior);
            // After the hide, so the pass shows exactly the frames the interior itself does.
            CockpitPass?.Sync(_renderPose.Basis, _cam, Shake?.Roll ?? 0f, Projectiles?.ActiveMuzzleLights());
            _cam.LogView(logged, _model.Position, _model.Attitude);
        }

        SyncViewCameraProxy();

        // heading of the nose: 0 = north (−Z), 90 = east (+X), shared by the compass and the marker
        var nose = -_model.Attitude.Z;
        float headingDeg = CompassTape.ReadingDeg(nose);
        // Player target selection: rebuild-then-input, the original's own order, the
        // per-frame candidate pass runs first and a handler then steps the list it just built.
        if (Targeting != null && IsHumanPiloted)
            StepTargeting(simDt);
        // Dogfight opponent / AI hostile markers: this pane's own pose, so each HUD can compute
        // its own target's clock bearing off it (the same feed the pilot HUD's markers get).
        if (VersusHud != null)
        {
            VersusHud.PlanePos = _model.Position;
            VersusHud.HeadingDeg = headingDeg;
        }
        _pilotHud.Draw(BuildHudState((float)delta, simDt, halted, headingDeg));
        // After the HUD feed, so the authored needles show THIS frame's readings rather than
        // trailing the screen-space dials by one.
        if (_panelShown && CockpitPanel != null && _pilotHud.Gauges is { } panelSource)
        {
            CockpitPanel.Apply(panelSource);
        }
        if (!halted && !Crashed)
        {
            float speedFrac = _model.Speed / _model.Stats.FdSpeed;
            // Zones where the airframe resolves them, the hull pair where it does not: the two
            // arms the decoded damage-state test itself has, so nothing here picks between them.
            float healthFrac = Damage?.WorstHealthFraction ?? 1f;
            // One drive for both paths: the original runs ONE per-frame routine for the player and
            // every AI vehicle, so the two must never read the airframe differently.
            var engineDrive = EngineAudioCurves.DriveFrom(_model, _model.Boosting);
            // Keyed to the SELECTED view (D31), not the per-frame pose the camera actually took,
            // the original's swap is a camera-mode gate, and a held numpad key or look-behind is a
            // pose, not a mode change (⚠ table row 2 traces the analogous head-look case).
            Audio?.Update(simDt, engineDrive, speedFrac, healthFrac, _model.EngineDead,
                ViewMode == PilotViewMode.Cockpit);
            EngineAudio?.Update(simDt, engineDrive, speedFrac, healthFrac, _model.EngineDead);
            if (SpeedCue != null && _viewCamera != null)
            {
                var cameraPos = _viewCamera.GlobalPosition;
                SpeedCue.Update(simDt, _model.Position, _model.Attitude, cameraPos.Y,
                    HeightAboveWorldGround(cameraPos), ViewportAspect(_viewCamera));
            }
        }

        // Spin the propeller/rotor blur discs: they keep turning even at idle (windmilling)
        // and speed up with throttle. Frozen while crashed or paused (a still disc reads
        // the same at any angle, and freezing it keeps screenshots deterministic).
        Props?.Advance(simDt, Crashed || halted ? 0f : PropIdleSpin + (1f - PropIdleSpin) * _model.Throttle);

        // Frozen while paused (so a screenshot catches a fixed state) and while crashed.
        if (!Crashed && !halted)
        {
            WingLights?.Advance(simDt);
            Surfaces?.Advance(simDt, _lastInput, IsHumanPiloted,
                _model.ReverseAuthorityAt(_model.Speed));
            // damage-stage trails need no per-frame feed: the rig runtime's emitters follow
            // their pdpN/prop1 host nodes themselves
        }
    }

    internal void DetachRosterBindings(ProjectilePool pool)
    {
        if (Body != null)
            pool.UnregisterAircraft(Body);
        if (_hudCanvas != null && GodotObject.IsInstanceValid(_hudCanvas))
        {
            _hudCanvas.GetParent()?.RemoveChild(_hudCanvas);
            _hudCanvas.QueueFree();
            _hudCanvas = null;
        }
        if (_messageCanvas != null && GodotObject.IsInstanceValid(_messageCanvas))
        {
            _messageCanvas.GetParent()?.RemoveChild(_messageCanvas);
            _messageCanvas.QueueFree();
            _messageCanvas = null;
        }
        SpeedCue?.Dispose();
        SpeedCue = null;
        Race?.Remove(PlayerIndex);
        Race = null;
        SmokeScreens = null;
        PauseState = null;
        TargetSubParts = null;
        TargetObjectives = null;
    }

    /// <summary>Whether static world geometry blocks the segment, the turret gunners' cached
    /// line-of-sight test. World layer only: another aircraft in the way is not cover, which is
    /// also why <see cref="HitWorld"/> (world + aircraft) is not reused here.</summary>
    internal bool WorldBlocksLine(Vector3 from, Vector3 to) =>
        World.Ray(from, to, CollisionLayers.World, null, out _);

    /// <summary>What blocks the AI's avoid-crash lookahead along the segment, the static world or
    /// another aircraft, never this plane's own body, as the struck body's name, or null for a
    /// clear path. Not <see cref="WorldBlocksLine"/>; decode: docs/org/aiPilot.md, "What the ray
    /// can hit".
    /// ⚠ Do not widen this to a swept sphere. Tried and reverted 2026-08-16: it detects far more
    /// but changes no outcome, and detection was never the bottleneck (same doc).</summary>
    internal string? AvoidCrashBlocksLine(Vector3 from, Vector3 to)
    {
        if (!World.Ray(from, to, CollisionLayers.WorldAndAircraft, Body?.ExcludeSelf, out var report))
            return null;
        // The name is the diagnostic: a block on "a5/col" is terrain, one on
        // "ai6_player_bhawk/airframe" is the aircraft case the decode says this ray also covers.
        return report.Collider is { } body
            ? $"{body.GetParent()?.Name}/{body.Name}"
            : "unnamed";
    }

    // Degrees between two vectors, 180 when either is degenerate (an unmeasurable
    // aspect reads as the worst case rather than as zero).
    private static float AngleBetweenDeg(Vector3 a, Vector3 b) =>
        a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f
            ? 180f
            : Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f)));

    // The struck body's numeric surface id (SceneBuilder.SurfaceIdMeta,
    // stamped on every collider), or null when there is no struck body, the headless
    // `--crash` force, which is the original's null-material arm and so resolves slot 0.
    // The weapon IMPACT table is read at this same id (ProjectilePool.SurfaceIdOf),
    // which answers `0` where this answers null, because that path has no null to carry.
    private static int? SurfaceIdOf(Node? hitBody) =>
        hitBody != null && hitBody.HasMeta(SceneBuilder.SurfaceIdMeta)
            ? hitBody.GetMeta(SceneBuilder.SurfaceIdMeta).AsInt32()
            : null;

    // Deadzone + squared response for fine control around center. A flight stick's attitude axes bypass it.
    private static float StickCurve(float v) => AnalogAxes.PadCurve(v);

    // N / gamepad X, the nitro command (the original's MSG_CMD_NITROUS "Use Nitro-Booster").
    // A level read: the command arm engages on pressed-or-held and ignores it otherwise.
    private bool NitroPressed() => AutoNitro || ReadLatched(InputAction.Nitro);

    // The nitro lifecycle for this step, in the original's order: the command arm (human key,
    // or the AI's nitro-flagged maneuver), then the tank, then the edges the flag produced.
    // Every human pilot takes the player's arms (shake, loop, animation): plan Decision 3.
    private void AdvanceNitro(float dt)
    {
        bool engineOut = _model.EngineDead;
        // The step opens by clearing last step's edges, so the arms below can raise this step's
        // and the reads at the bottom still see them.
        Nitro.BeginStep();
        if (IsHumanPiloted)
            Nitro.HumanCommand(NitroPressed(), engineOut, dt);
        else if (Pilot?.Machine?.Executor is { Maneuver.Nitro: true })
        {
            // One engage per maneuver, the way the maneuver starter fires SetNitro(1) once.
            if (!_aiNitroArmed)
                Nitro.AiSet(true, engineOut, dt);
            _aiNitroArmed = true;
        }
        else
        {
            _aiNitroArmed = false;
            Nitro.AiSet(false, engineOut, dt);
        }
        Nitro.Advance(dt, engineOut);

        if (Nitro.EngagedThisTick)
        {
            if (IsHumanPiloted)
            {
                Shake?.NitroEngaged();
                _rumble.Play(RumbleEvent.NitroStart);
            }
            else
            {
                PlayAiShake();
            }
            // Play, not PlayWithin: the def's anchor NAME ("warhawk") never resolves in this
            // per-plane index, same as startprops/stopprops above, Play's fallback to
            // PlaneModel is what makes those work; PlayWithin has no such fallback.
            if (PlaneModel != null)
                CrashRuntime?.Play("nitro_boost", PlaneModel, applyReset: false);
            Log.Debug("flight", $"nitro engaged charge={Nitro.Charge:0.0}");
        }
        if (Nitro.ReleasedThisTick && PlaneModel != null && CrashRuntime != null)
        {
            // Unscoped Stop is safe here: this runtime is bound to this one aircraft only
            // (WorldEffectsFactory.BuildFlightCrashRuntime), so every live instance it holds is
            // already this plane's own.
            CrashRuntime.Stop("nitro_boost");
            CrashRuntime.Play("nitro_decay", PlaneModel, applyReset: false);
            Log.Debug("flight", $"nitro released charge={Nitro.Charge:0.0}");
        }
        // The re-engage stays refused until the decay INSTANCE ends, which is what the original's
        // completion callback clears its handle on, so the lockout is the def's own length whatever
        // the def says. Never on the releasing tick: nothing completes before it has started.
        else if (Nitro.DecayAnimPlaying
                 && (CrashRuntime is not { } decayRig
                     || decayRig.AnimStateOf("nitro_decay") != AnimRunning))
        {
            Nitro.DecayFinished();
        }

        if (IsHumanPiloted && Audio != null)
        {
            if (Nitro.BoostAnimAlive)
                Audio.StartNitroLoop();
            else
                Audio.StopNitroLoop();
        }
        // An AI's loop is positional and keyed, not a sustain: the state machine refreshes it only
        // on the ticks something calls it, which is the engage and the releases after the maneuver.
        else if (!IsHumanPiloted)
            EngineAudio?.RefreshNitroLoop(Nitro.LoopRefreshedThisTick, dt);
    }

    // The AI half of the engage's shake: the original plays the middle of the three `*_aishake`
    // defs on the aircraft's own node, one at a time per vehicle, where a person at the controls
    // gets the camera shake instead. The runtime's own ANIM_STATE is that one-at-a-time handle.
    private void PlayAiShake()
    {
        if (PlaneModel == null || CrashRuntime is not { } rig
            || rig.AnimStateOf(EffectCatalogue.AiShakeAnim) == AnimRunning)
        {
            return;
        }
        rig.Play(EffectCatalogue.AiShakeAnim, PlaneModel, applyReset: false);
    }

    // Both bit-2 edges of the original's disabled-systems mask. They are read off the model's own
    // engine-out state, so the choke, its extension and its expiry all reach the same two calls.
    private void SyncPropSlot()
    {
        if (_model.EngineDead)
            PlayStopProps();
        else
            PlaySpinProps();
    }

    // The wind-down half of the original's two prop anim slots. ⚠ Refuse while the stopped
    // presentation already holds the slot. The original's own routine starts nothing when its
    // stop handle is occupied. That keeps a choked aircraft's crash from replaying the fade and
    // re-firing snd_propstop (docs/org/ordnanceTypes.md).
    private void PlayStopProps()
    {
        if (_propsStopped || PlaneModel == null || CrashRuntime is not { } rig)
            return;
        rig.Stop("spinprops");
        rig.Play("stopprops", PlaneModel, applyReset: false);
        _propsStopped = true;
    }

    // The restart half: silent and instant, because the original's falling edge runs `spinprops`
    // and never `startprops`, whose snd_propstart the original plays nowhere.
    private void PlaySpinProps()
    {
        if (!_propsStopped || PlaneModel == null || CrashRuntime is not { } rig)
            return;
        rig.Stop("stopprops");
        // ⚠ Suppressed, or the def's endless XYZ_ROTATION becomes a second writer on the same
        // disc transforms PropAnimator turns at those very rates.
        rig.SuppressedMotionAnims.Add("spinprops");
        rig.Play("spinprops", PlaneModel, applyReset: false);
        RestoreDiscOpacity(rig, PlaneModel);
        _propsStopped = false;
    }

    // `spinprops` re-activates the blur discs and writes no opacity. The wind-down it reverses
    // faded those same discs to zero. The restart therefore puts the alpha back itself, or the
    // aeroplane comes out of a choke with its propellers turning invisibly.
    private void RestoreDiscOpacity(AnimRuntime rig, Node node)
    {
        if (node is Node3D n3d && PropParts.Spin(PropParts.Classify(AnimRuntime.NameOf(n3d)), out _, out _))
            rig.SetSubtreeOpacity(n3d, 1f);
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
            RestoreDiscOpacity(rig, node.GetChild(i));
    }

    // Space / gamepad B, the gun trigger (caller drives the fire-rate clock);
    // `--fire` holds it down for unattended runs, and stays outside the latch because no re-entry
    // press put it down. Gamepad B is also the menu's Back, so a pause-sheet dismiss reaches here.
    private bool FirePressed() => AutoFire || ReadLatched(InputAction.FireGuns);

    // F / gamepad A, the rocket trigger. One discrete pull launches one rocket (holding
    // does NOT auto-repeat; only the 1.0 s cooldown gates it), and `--fire-rockets` auto-repeats
    // for unattended runs. ⚠ Gamepad A must not also respawn: PadPressed is a level read, so a
    // button still held when the plane goes live fires a rocket on the spawn frame, the same
    // shape the latch below also catches, since it arms wherever flight regains input.
    private bool RocketFirePressed() => ReadLatched(InputAction.FireRockets);

    // ⚠ The latch is read even while the seat is held. Skipping it would leave a control that went
    // down during the hold reading as a fresh press the moment the hold ends. Every discrete flight
    // command reads through here, so one still-down control is swallowed once for whichever
    // commands it is bound to (FlightReentryLatch.Latched names them).
    private bool ReadLatched(InputAction action)
    {
        bool down = _reentryLatch.Read(action, _actions.Held(action));
        return down && !CommandsHeld;
    }

    // ⚠ One latch read per action per frame. The latch disarms on the first reading that says "up".
    // A second read of the same action in the same frame would clear it early. This is the same
    // read over the keyboard and mouse half alone. The action's pad half is dispatched by a
    // tap/hold slot of its own.
    private bool ReadLatchedKeys(InputAction action)
    {
        bool down = _reentryLatch.Read(action, _keyActions.Held(action));
        return down && !CommandsHeld;
    }

    // Called at flight's own resume/skip re-entry points (the Inert setter above, and
    // PollPauseAndHalt's halt-clearing edge) so a press that just confirmed a cutscene skip or a
    // pause-sheet dismiss cannot also read as a flight command. ⚠ Pass the latch no button reading
    // from here: a skip is handled inside an input handler, and this frame's snapshot predates the
    // press that caused it (FlightReentryLatch.Arm).
    private void SwallowInputHeldThroughReentry() => _reentryLatch.Arm();

#pragma warning disable SA1202
    // The two triggers' own readings, for the suite that drives the cutscene-to-flight and
    // pause-to-flight boundaries: a headless run holds no button down, so the suite supplies the
    // reading and the production read is what answers it. Kept beside the button reads they belong
    // with rather than hoisted for SA1202's sake, the same trade made elsewhere here.
    internal bool RocketTriggerReadsForTest(bool buttonDown)
    {
        HoldActionForTest(InputAction.FireRockets, buttonDown);
        return RocketFirePressed();
    }

    internal bool GunTriggerReadsForTest(bool buttonDown)
    {
        HoldActionForTest(InputAction.FireGuns, buttonDown);
        return FirePressed();
    }

    // The --lever= schedule on a rig a suite assembled itself rather than through the roster. That
    // is the only way to reach the flag's own path, since a headless run holds no digit down. The times
    // run from this call, not from the rig's first step, so a suite need not count what it has
    // already stepped.
    internal void ScheduleLeverForTest(IReadOnlyList<(float At, float Lever)> steps)
    {
        _leverSteps = steps;
        _leverElapsed = 0f;
        _leverNext = 0;
    }

    // This tick's reading for one action, written over the three readers after a real poll, so the
    // next SimStep in the same rendered frame reuses it rather than polling over it. The suites'
    // stand-in for hardware nothing headless can hold down.
    internal void HoldActionForTest(InputAction action, bool held)
    {
        PollInput();
        var read = ControlValue.Digital(held);
        _actions.Current.Store(action, read);
        _keyActions.Current.Store(action, read);
        _padActions.Current.Store(action, read);
        _padAxes.Current.Store(action, read);
    }

    // The tick's two halves as a suite supplies them, so the seat's device memory has something to
    // follow: a headless run holds down no key and no stick. Each state answers for one side alone,
    // the split a live poll gets from its own pad-muted reader. The whole-seat reader gets both at
    // once, since a site reading through it must see what the suite pressed rather than the empty
    // hardware underneath.
    internal void ObserveDeviceForTest(IDeviceState keyboardSide, IDeviceState padSide)
    {
        PollInput();
        _actions.Poll(new BothSides(keyboardSide, padSide));
        _keyActions.Poll(keyboardSide);
        _padActions.Poll(padSide);
        _padAxes.Poll(StickSplit.WithoutSticks(padSide));
        _stickAxes.Poll(StickSplit.SticksOnly(padSide));
        if (_bindings.ObserveDevice(_keyActions.Current, _padActions.Current, _stickAxes.Current))
            ComposeControlPrompts();
    }

    // One event through this seat's own rumble. A suite reaches the gate the shipped event sites
    // share without staging a shot landing, a launch or a contact.
    internal void RumbleForTest(RumbleEvent ev) => _rumble.Play(ev);
#pragma warning restore SA1202

    // F3 ("Cycle guns clockwise"), the KEY half alone: cycles the gun selector forward through the
    // firable groups (1 → 2 → … → 1). Only ONE group fires at a time; the gun trigger fires the
    // selected one. Caller edge-detects.
    // ⚠ Do not widen this to the seat's whole reading. The pad half of the same action carries both
    // directions through its own tap/hold slot. A combined read would step forward on the press and
    // back again on the release of one long hold.
    private bool GunSelectPressed() => ReadLatchedKeys(InputAction.SelectGunGroup);

    // F4 ("Cycle guns counterclockwise"), the same walk the other way. It is its own bound action
    // because the original's keybind page carries one per direction per weapon class. Read whole
    // rather than key-only: nothing ships on the pad here. A pad control on this row is one a
    // player bound themselves and means exactly one step back.
    private bool GunSelectBackPressed() => ReadLatched(InputAction.SelectGunGroupPrev);

    // A / gamepad left-stick click, the auto-land button, read live by
    // LandingApproachRuntime.Tick() so a press lands in the same frame it happens. Kept beside the
    // other button reads rather than hoisted for SA1202's sake, the same trade made elsewhere here.
#pragma warning disable SA1202
    internal bool AutoLandPressed()
    {
        PollInput();    // read from outside this node's own tick, so it resolves its own frame
        return AutoLand || ReadLatched(InputAction.AutoLand);
    }
#pragma warning restore SA1202

    // F5 ("Cycle rockets clockwise"), the KEY half alone for the same reason as the gun row above.
    // It moves the hardpoint selector forward to the next pylon that still carries ordnance. Each
    // pylon is its own selectable slot, whatever it loads, even on a plane with one uniform
    // ordnance type. The rocket trigger then launches from the selected pylon. Caller edge-detects.
    private bool RocketSelectPressed() => ReadLatchedKeys(InputAction.SelectOrdnance);

    // F6 ("Cycle rockets counterclockwise"), the hardpoint walk the other way, over the same
    // physical mount order and skipping the same empties. A press each way from one pylon returns
    // to it. Caller edge-detects.
    private bool RocketSelectBackPressed() => ReadLatched(InputAction.SelectOrdnancePrev);

    // ⚠ The D-pad side follows the cockpit dial it drives. The GUNS gauge sits in the right column
    // (above the speedometer) and ROCKETS in the left. Pressing away from the dial reads as a
    // mis-binding at the controls. This is the pad half of a forward selector, as a LEVEL.
    // FireControl splits it into a tap that steps forward and a hold that steps back, so one button
    // serves a class both ways.
    private bool PadSelectorHeld(InputAction action) =>
        !CommandsHeld && _padActions.Held(action);

    // This tick's four selector readings, each side on its own field. One call per tick: the two
    // key-half reads go through the re-entry latch, which answers once.
    private FireInputs ReadSelectorInputs() => new()
    {
        GunSelectHeld = GunSelectPressed(),
        RocketSelectHeld = RocketSelectPressed(),
        GunSelectBackHeld = GunSelectBackPressed(),
        RocketSelectBackHeld = RocketSelectBackPressed(),
        GunSelectPadHeld = PadSelectorHeld(InputAction.SelectGunGroup),
        RocketSelectPadHeld = PadSelectorHeld(InputAction.SelectOrdnance),
    };

#pragma warning disable SA1202
    // The selector readings for the suite that drives the shipped keys and the pad's tap/hold
    // button against a real seat. A headless run holds nothing down, so the suite supplies the
    // tick's two device sides and the production reads above answer them. Kept beside those reads
    // rather than hoisted for SA1202's sake, the same trade made elsewhere here.
    internal FireInputs SelectorInputsForTest() => ReadSelectorInputs();

    // The other discrete buttons' readings, for the suite that holds a stick button on each row. They
    // are the targeting row's own read, the tap/hold splitter's, the flyby view's and the throttle's.
    internal bool TargetControlDownForTest(InputAction action) => TargetControlDown(action);

    internal bool TargetSplitterDownForTest() => TargetSplitterDown();

    internal bool FlybyDownForTest() => FlybyDown();

    internal float? RequestedThrottleForTest() => RequestedThrottle();
#pragma warning restore SA1202

    // This frame's pilot-HUD feed. The pipper's inputs are resolved HERE and only where there is a
    // reticle to draw: a muzzle midpoint reads one world transform per barrel, which every aircraft
    // drawing no reticle would otherwise pay every rendered frame.
    private FlightHudState BuildHudState(float wallDt, float simDt, bool halted, float headingDeg)
    {
        GunGroup? reticleGun = null;
        Vector3 reticleOrigin = default;
        Vector3 reticleNose = default;
        Vector3 inheritedVelocity = default;
        if (_pilotHud.DrawsReticle && !Crashed && Loadout != null && _fire != null
            && SelectedGun() is { } sel)
        {
            reticleGun = sel;
            reticleOrigin = MuzzleMidpoint(sel);
            reticleNose = -GlobalTransform.Basis.Orthonormalized().Z;
            inheritedVelocity = _model.VelocityDir * _model.Speed;
        }
        return new FlightHudState
        {
            Position = _model.Position,
            HeadingDeg = headingDeg,
            SpeedMps = _model.Speed,
            Throttle = _model.Throttle,
            Crashed = Crashed,
            Held = _held,
            Halted = halted,
            StallWarned = _model.IsStallWarned(),
            AvailableLoadFactor = _model.AvailableLoadFactor,
            Stalled = _model.isStalled(),
            AutoLandOffered = AutoLandOffered,
            RespawnOffered = !Spectating && !CommandsHeld,
            WallDt = wallDt,
            SimDt = simDt,
            DamageSummary = _pilotHud.DrawsTextBlock ? Damage?.Summary() : null,
            StuntStatusLine = Stunt != null && _pilotHud.NeedsStuntStatusLine ? Stunt.StatusLine() : null,
            Loadout = Loadout,
            GunSelect = _fire?.GunSel ?? 0,
            PylonSelect = _fire?.SelectedPylon ?? 0,
            NitroInstalled = Nitro.Installed,
            NitroBoosting = Nitro.Boosting,
            NitroChargeFrac = Nitro.ChargeFraction,
            ReticleGun = reticleGun,
            ReticleOrigin = reticleOrigin,
            ReticleNose = reticleNose,
            InheritedVelocity = inheritedVelocity,
            Attitude = Attitude,
            RenderPose = _renderPose,
        };
    }

    // Pushes InPlay into the two facts the engine can only hold as state:
    // whether the airframe is drawn, and whether its body sits on the aircraft collision layer
    // (so a ray, a sweep or a hit test can find it). Everything else consults the flag. Called by
    // Respawn and by _Ready, the two points where the pieces that
    // carry the state come (back) into existence.
    private void ApplyPresence()
    {
        // ⚠ Hide the pivot, never the model root: that root is the airframe's own archive node and
        // its visibility is the ACTIVE bit a hookup definition reads.
        // Decode: docs/formats/anim-definitions/cutscenes.md.
        if (ShakePivot != null)
            ShakePivot.Visible = InPlay;
        // The crash paths hide the airframe itself, which is a death state rather than presence;
        // coming back into play is what undoes it.
        if (PlaneModel != null && InPlay)
            PlaneModel.Visible = true;
        Body?.SetHittable(InPlay);
    }

    /// <summary>The selected firable gun group, the one the trigger fires, or null when there is
    /// no loadout, no fire control, no group at the selected index, or the group has no muzzle to
    /// fire from. Shared by the pipper and the targeting marker's bracket gate so both read the
    /// same "which gun is selected" answer.</summary>
    private GunGroup? SelectedGun()
    {
        if (Loadout == null || _fire == null)
        {
            return null;
        }
        int gi = 0;
        foreach (var g in Loadout.FirableGuns)
        {
            if (gi == _fire.GunSel)
            {
                return g.Muzzles.Count > 0 ? g : null;
            }
            gi++;
        }
        return null;
    }

    // The ordnance the hardpoint selector points at, or null with no fit, no fire control or no
    // pylon. What the Non-Aircraft cycle's torpedo gate reads (TargetPool.Rebuild), on every
    // per-frame rebuild. Switching off the torpedo therefore drops the zeppelin parts from that
    // cycle on the next pass. The re-resolve moves a selected part to the head rather than holding
    // it.
    // The gun selector can never open the gate: no gun in this install authors LOCK_ON.
    private WeaponDef? SelectedOrdnance()
    {
        if (Loadout is not { Hardpoints.Count: > 0 } fit || _fire == null)
        {
            return null;
        }

        int pylon = _fire.SelectedPylon;
        return pylon >= 0 && pylon < fit.Hardpoints.Count ? fit.Hardpoints[pylon].Weapon : null;
    }

    /// <summary>A gun group's muzzle MIDPOINT: the original averages that group's live barrel
    /// attachments, which is where that group's fire converges.</summary>
    private Vector3 MuzzleMidpoint(GunGroup group)
    {
        var origin = Vector3.Zero;
        foreach (var m in group.Muzzles)
        {
            origin += m.GlobalPosition;
        }
        return origin / group.Muzzles.Count;
    }

    // One gun round's launch direction: the per-muzzle aim assist through
    // AimAssist.FireDirection, which restamps this barrel's slot so the forget timer
    // runs from the last SHOT. ⚠ Gated on IsHumanPiloted, not pane 1, every CSVM
    // pane is a human pilot, so every pane is assisted. An AI plane's round leaves along the
    // gunner's lead instead; a gunner-less AI and a barrel with no slot fall back to the muzzle
    // axis.
    private Vector3 AssistedGunDirection(WeaponDef weapon, int gi, int mi, Node3D muzzle,
        Basis planeBasis, Vector3 inheritVel, double now)
    {
        var muzzleXf = muzzle.GlobalTransform;
        if (!IsHumanPiloted)
        {
            return Pilot?.Gunner is { WantsFire: true } gunner
                ? gunner.ShotDirection(muzzleXf.Origin)
                : -muzzleXf.Basis.Z.Normalized();
        }
        var slots = gi >= 0 && gi < _aimSlots.Length ? _aimSlots[gi] : null;
        if (slots == null || mi < 0 || mi >= slots.Length)
        {
            return -muzzleXf.Basis.Z.Normalized();
        }
        float range = weapon.Range ?? 0f;
        var scan = new AimScan
        {
            MuzzlePosition = muzzleXf.Origin,
            ShooterVelocity = inheritVel,
            // Alignment is measured against the PLANE's nose, not this muzzle's axis, the engine
            // scores every candidate against the airframe's own forward row.
            Forward = -planeBasis.Z,
            Team = Team,
            Speed = weapon.Velocity ?? ProjectilePool.DefaultVelocity,
            RangeSquared = range * range,
            ConeCos = AimAssist.WeaponConeCos(weapon),
            DistFactor = _model.Stats.StickyBulletDistFactor,
            Self = this,
        };
        var dir = AimAssist.FireDirection(ref slots[mi], scan, _aimCandidates, planeBasis, now,
            _model.Stats.StickyBulletInaccuracy, _aimRng, out var found);
        if (found.Found && !_aimLoggedFirst)
        {
            _aimLoggedFirst = true; // verification breadcrumb: the assist found something, once
            Log.Info("weapons", $"gun aim assist: P{PlayerIndex + 1} snapped onto {found.Kind} (score {found.Score:0.000}, {found.TimeOfFlight * (weapon.Velocity ?? ProjectilePool.DefaultVelocity):0} m out)");
        }
        return dir;
    }

    // Performs one FireControl.Step's decisions against the engine: spawns
    // the commanded rounds from their live muzzle nodes (with the plane's inherited velocity),
    // launches the commanded rocket, mirrors the loop-sound state onto FlightAudio
    // (started every wanted frame, StartGunLoop only rebuilds when the name changes, which is how
    // a mid-burst group switch swaps the loop), and sounds the dry cues. The first-round-per-group
    // and first-launches breadcrumbs log here, print throttles, not fire-control state.
    private void ApplyFireOutcome(FireOutcome outcome)
    {
        var inheritVel = _model.VelocityDir * _model.Speed;
        // Built ONCE for this tick's rounds, not per barrel: the same four lists feed every muzzle.
        if (outcome.GunShots.Count > 0 && Projectiles != null)
        {
            _aimCandidates.Clear();
            Projectiles.CollectAircraft(_aimCandidates);
            SurfaceVehicles?.CollectVehicles(_aimCandidates);
            Projectiles.CollectTurrets(_aimCandidates);
            Projectiles.CollectFusedOrdnance(_aimCandidates);
            if (Destructibles != null)
            {
                _aimCandidates.AddStructures(Destructibles);
            }
            if (!_aimListsLogged)
            {
                _aimListsLogged = true; // verification breadcrumb: WHICH lists this build actually feeds
                // The nearest structure's range comes with the count on purpose: a registry whose
                // anchors carried no world transform would report its full count and sit at the
                // world origin, i.e. be silently untargetable, and the count alone would not show it.
                float nearest = float.MaxValue;
                foreach (var s in _aimCandidates.Structures)
                {
                    nearest = Mathf.Min(nearest, s.Position.DistanceTo(_model.Position));
                }
                string nearestPart = _aimCandidates.Structures.Count > 0
                    ? Log.Format($", nearest structure {nearest:0} m")
                    : string.Empty;
                Log.Info("weapons", $"gun aim assist: candidates vehicles={_aimCandidates.Vehicles.Count} turrets={_aimCandidates.Turrets.Count} structures={_aimCandidates.Structures.Count} ordnance={_aimCandidates.Ordnance.Count}{nearestPart}");
            }
        }
        var planeBasis = GlobalTransform.Basis.Orthonormalized();
        double aimNow = GameClock.Current?.Time ?? 0.0;
        foreach ((int gi, int mi) in outcome.GunShots)
        {
            var g = _firableGuns[gi];
            var muzzle = g.Muzzles[mi];
            var aimDir = AssistedGunDirection(g.Weapon, gi, mi, muzzle, planeBasis, inheritVel, aimNow);
            Projectiles!.Spawn(g.Weapon, muzzle.GlobalTransform, inheritVel, PlayerIndex, muzzle, aimDir, Team);
            Shake?.FireBullet(g.Weapon.Caliber ?? 0f); // the firing buzz: factor × caliber (measured)
            // One of three effects by calibre, each restarted per round. The original's loop is
            // infinite and its own timer stops it 0.3 s after the last shot.
            if (IsHumanPiloted)
                _rumble.Play(PadRumble.GunFire(g.Weapon.Caliber ?? 0f));
            if (!_gunLoggedFirst[gi])
            {
                _gunLoggedFirst[gi] = true;   // verification breadcrumb: which groups actually fire
                Log.Info("weapons", $"gun group {gi + 1} ({g.Mount}, {g.Weapon.Caliber ?? 0}-cal {g.Weapon.Id}) firing on {Name}");
            }
        }
        if (outcome.RocketPylon >= 0)
        {
            var hp = Loadout!.Hardpoints[outcome.RocketPylon];
            var rocketAim = OrdnanceLaunchDir(IsHumanPiloted, planeBasis, hp.Weapon.Rear,
                !IsHumanPiloted && Pilot?.Rocketeer is { } launcher ? launcher.LaunchDirWorld : null);
            // The round's target, the second half of `ProjectilePool.SteeringStepRuns`'s gate: a
            // human's own selection, an AI's gunner quarry. A LOCK_ON round holding none never
            // sheds its inherited launch velocity.
            object? launchTarget = IsHumanPiloted ? Targeting?.Current?.Source : Pilot?.Gunner?.Target;
            // A SMOKE_SCREEN weapon spawns no round: the original's launch branch builds a world
            // object carrying the weapon's TIME instead, and skips the spawn that would have
            // played the FIRE row's sound and animation. Ammo and the fire clock are already spent.
            if (hp.Weapon.SmokeScreenTime is { } screenTime)
            {
                SmokeScreens?.Lay(this, screenTime);
            }
            else
            {
                Projectiles!.Spawn(hp.Weapon, hp.Pylon.GlobalTransform, inheritVel, PlayerIndex, hp.Pylon,
                    rocketAim, Team, launchTarget);
            }
            // After the spawn branch, so a SMOKE_SCREEN launch rumbles too. The cue hangs on the
            // pylon firing, where the original hangs it, not on a round appearing.
            if (IsHumanPiloted)
                _rumble.Play(PadRumble.Launch(hp.Weapon.Torpedo, hp.Weapon.Rear));
            if (_rocketsLaunched < 12)
            {
                _rocketsLaunched++;
                Log.Info("weapons", $"rocket: {Name} launched {hp.Weapon.Id} ({hp.Weapon.Name}) from pylon{hp.Index}, {(InfiniteAmmo ? "∞" : hp.Ammo.ToString(CultureInfo.InvariantCulture))} left on that pylon");
            }
        }
        // Exactly one of the two is built per rig, so both lines run and the null one no-ops: a pilot
        // hears their own guns flat, and every other aircraft's come from where it is.
        if (outcome.GunLoopWanted)
        {
            _gunLoopOn = true;
            Audio?.StartGunLoop(outcome.GunLoopSound);
            WeaponAudio?.StartGunLoop(outcome.GunLoopSound);
        }
        else if (_gunLoopOn)
        {
            _gunLoopOn = false;
            Audio?.StopGunLoop();
            WeaponAudio?.StopGunLoop();
        }
        if (outcome.GunDryCue)
        {
            Audio?.PlayEmptyClip();
            WeaponAudio?.PlayEmptyClip();
        }
        if (outcome.RocketDryCue)
        {
            Audio?.PlayEmptyClip();
            WeaponAudio?.PlayEmptyClip();
            Log.Info("weapons", $"rocket: {Name} dry pull, all pylons empty, empty-clip cue");
        }
    }

    // Refills every gun group to its full load and re-arms the dry warnings (respawn).
    private void RefillWeapons()
    {
        if (Loadout == null)
        {
            return;
        }
        // The loadout-wide top-up covers the inert turret groups too, and runs even before _Ready
        // has built the state machine (Setup's pre-tree Respawn). FireControl.Refill re-tops its
        // own slots with the same values and resets the machine (clocks, cursors, dry warnings).
        foreach (var g in Loadout.Guns)
        {
            g.Ammo = g.Capacity;
        }
        foreach (var h in Loadout.Hardpoints)
        {
            h.Ammo = h.Capacity;
        }
        _fire?.Refill();
        if (_gunLoopOn)
        {
            _gunLoopOn = false;
            Audio?.StopGunLoop();
            WeaponAudio?.StopGunLoop();
        }
        Projectiles?.Clear();
    }

    // Full stunt restart from the results scoreboard (R): fresh clock + every
    // zone incomplete, then the normal respawn (spawn pose / throttle / cleared damage). The
    // scoreboard hides itself once AllComplete clears; the marker HUD replays its intro line.

    // True if the segment crosses any solid collider, the static world, or another
    // aircraft's body (never this plane's own, excluded by RID); on a hit,
    // `point` is the impact position (else the segment end) and
    // `hitName` names the collider (parent/body, e.g. a terrain
    // tile's "g27889/col", or a clutter city block's "world1/clutter_bld_3_7").
    private bool HitWorld(Vector3 from, Vector3 to, out Vector3 point, out string hitName, out Node? hitBody)
    {
        point = to;
        hitName = "";
        hitBody = null;
        if (!World.Ray(from, to, CollisionLayers.WorldAndAircraft, Body?.ExcludeSelf, out var report))
            return false;
        point = report.Position;
        if (report.Collider is { } body)
        {
            hitBody = body;
            hitName = $"{body.GetParent()?.Name}/{body.Name}";
        }
        return true;
    }

    // Ground blow's probe (docs/org/flightModel.md "Ground blow"): a ray along the nose
    // whose nearest hit FlightModel turns into a control-response bias.
    // ⚠ The mask is CollisionLayers.World, the decoded emitter rule, do not add an
    // aircraft mask. ⚠ Skipped while the AI pilot is stunned; do not lift either gate.
    private void ProbeGroundBlow(ref FlightInput input)
    {
        float elev = _model.Stats.GroundBlowElev;
        if (elev <= 0f)
            return;
        if (!IsHumanPiloted && (!_model.UsesAiForcePath || Pilot?.IsStunned == true))
            return;
        var from = _model.Position;
        if (!World.Ray(from, from - _model.Attitude.Z * elev, CollisionLayers.World, null, out var report))
            return;
        input.GroundBlowNormal = report.Normal;
        input.GroundBlowDistM = from.DistanceTo(report.Position);
        if (!_groundBlowLoggedFirst && _model.Attitude.Z.Dot(input.GroundBlowNormal) > 0f)
        {
            _groundBlowLoggedFirst = true;   // verification breadcrumb: the probe reaches real geometry
            // The facing test comes with it: a hit that fails it is inert and must not read as "working".
            string what = report.Collider is { } body
                ? $"{body.GetParent()?.Name}/{body.Name}" : "?";
            Log.Info("flight", $"ground blow: first repelling hit on {what} at {input.GroundBlowDistM:0} m of {elev:0} (facing {_model.Attitude.Z.Dot(input.GroundBlowNormal):0.00})");
        }
    }

    // Squared HORIZONTAL range to the nearest human pilot, the quantity the flight model's
    // far-field branch is selected on. The original measures Δx² + Δz² against its single player;
    // this reads every human, so flight targets always use the nearest human pilot.
    // ⚠ No seam bound means no human is known, and 0 keeps the aircraft near-field.
    private float NearestHumanDistSqM()
    {
        if (HumanPositions?.Invoke() is not { Count: > 0 } humans)
            return 0f;
        var here = _model.Position;
        float best = float.MaxValue;
        for (int i = 0; i < humans.Count; i++)
        {
            float dx = humans[i].X - here.X;
            float dz = humans[i].Z - here.Z;
            float d = (dx * dx) + (dz * dz);
            if (d < best)
                best = d;
        }

        return best;
    }

    // Vertical clearance over static world collision only. Unlike HitWorld,
    // another aircraft below the camera is not ground for speed_cue's NODE_NEAR_GROUND gate.
    private float HeightAboveWorldGround(Vector3 from)
    {
        var to = from + Vector3.Down * 1000f;
        return World.Ray(from, to, CollisionLayers.World, null, out var report)
            ? from.Y - report.Position.Y
            : float.MaxValue;
    }

    // The shape of the pane this pilot actually sees. A splitscreen pane spreads the speed cue by
    // its own width, not the whole window's. A camera out of tree, or one whose viewport
    // has no height yet, reads the authored 4:3, which spreads nothing.
    private float ViewportAspect(Camera3D camera)
    {
        var size = camera.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
        return size.Y > 0f ? size.X / size.Y : SpeedCue.AuthoredAspect;
    }

    // The explosion boom the chosen crash def authors, which FlightAudio
    // plays because the crash runtime renders effects and never sound: `player_crash_dirt`
    // Sounds `snd_exp_ground_a` itself, `player_crash_water`'s `snd_exp_water_a`
    // sits one level down in the `plane_big_splash` it calls, and the fallback
    // `player_crash_default` Sounds only `plane_destroy_sg`, already played by
    // FlightAudio.OnCrash, so it layers no surface boom at all.
    private void PlayCrashBoom(string? crashDef)
    {
        switch (crashDef)
        {
            // The AI family authors the same surface booms (ai_crash_dirt Sounds snd_exp_ground_a
            // itself; ai_crash_water's snd_exp_water_a rides the plane_big_splash it calls), so
            // both prefixes take the same arm.
            case EffectCatalogue.CrashDefPrefix + "water":
            case EffectCatalogue.AiCrashDefPrefix + "water":
                Audio?.OnWaterExplosion();
                break;
            case EffectCatalogue.CrashDefPrefix + "dirt":
            case EffectCatalogue.AiCrashDefPrefix + "dirt":
                Audio?.OnGroundExplosion();
                break;
        }
    }

    /// <summary>The DEATH event: whole-vehicle health has reached zero. Starts the airframe's own
    /// <see cref="DestroyDef"/> and leaves the wreck VISIBLE, the surface-indexed
    /// <see cref="CrashDefs"/> family belongs to the wreck's ground contact, and playing it here is
    /// what deleted the fall, the burning wreck and the parachute (org/vehicleDamage.md).</summary>
    private void Destroy(Vector3 at, string hitName, string part, int? killer)
    {
        // The transition decides; everything below performs what it reported.
        var death = _lifecycle.Destroy(CrashRuntime != null ? DestroyDef : null, killer);
        if (!death.Occurred)
            return;
        Body?.SetHittable(false);       // a dead plane soaks no rounds and blocks no sweep
        if (death.EndFlightSystems)
            EndFlightSystems();
        string? destroyDef = death.DestroyDef;
        if (destroyDef != null)
        {
            // The dead hull flies itself from here; the def's own Callback 15 stops it, at 3.0 s on
            // the ten AI airframes and at once on `player`. ⚠ Wire all three seams before Play, or
            // the untimed player def raises them into nothing (docs/org/vehicleDamage.md).
            CrashRuntime!.WreckVelocity = () => _model.VelocityDir * _model.Speed;
            CrashRuntime.StopWreckFlying = () => _lifecycle.StopWreckFall();
            // Code 15's other half: the stages the hull was wearing end with it. Both AI stage anims
            // are LOOP -1 with NO authored exit, so nothing else can reach them and a survivor emits
            // forever at the node the wreck left behind.
            CrashRuntime.StopDamageStages = () => Visuals?.DamageEffectStop?.Invoke();
            // Code 3, the first event of either destroy arm: the pilot loses the view they chose.
            // Bound on every rig and raised by none but the human def, so the aeroplane whose
            // camera this is is always the one dying.
            CrashRuntime.ResetPilotView = () => _cam?.ResetToChase();
            // The airburst, the pilot's chute and (on the eleven airframe defs) the wreck's own
            // launch: this plane's parts detaching, not a world destructible's.
            using (PerfSample.Scope(PerfSite.PartDetach))
            {
                CrashRuntime.Play(destroyDef, CrashAnchor, applyReset: false);
            }
        }
        else if (PlaneModel != null)
        {
            // No destroy def bound: nothing authored can end this airframe, so hide it outright
            // rather than leave a pristine hull hanging in the air.
            PlaneModel.Visible = false;
        }
        if (death.CutCamera)
            CutToDeathView();
        Log.Info("flight",
            $"DESTROYED by {hitName} ({part}) def={destroyDef ?? "-"} wreck={(death.WreckFalling ? "falling (flight model)" : "handed over on the kill frame")} lands={(DestroyDefFliesWreck ? "anim (bounce sequence)" : "ground-impact def")} pos=({_model.Position.X:0},{_model.Position.Y:0},{_model.Position.Z:0}) spd={_model.Speed:0} m/s");
        if (death.Downed)
            Downed?.Invoke(PlayerIndex, death.Killer);
    }

    // Everything an aircraft stops doing the moment it is out of the fight, whether it died in the
    // air or struck the world. Runs exactly once per death: the gun loop would otherwise keep
    // playing under the wreck, since nothing else calls StopGunLoop while the plane is crashed.
    private void EndFlightSystems()
    {
        if (_gunLoopOn)
        {
            _gunLoopOn = false;
            Audio?.StopGunLoop();
            WeaponAudio?.StopGunLoop();
        }
        Audio?.StopNitroLoop();
        Audio?.OnCrash();
        // An AI aircraft's loops end here and stay ended: the animation's own authored sound
        // events are what is audible from now on, and no wreck respawns to restart them.
        EngineAudio?.Stop();
        WeaponAudio?.Stop();
        // The engine wind-down cue layers over the explosion, replacing the loops' abrupt cut with
        // snd_propstop.
        Audio?.OnEngineStop();
        // ⚠ Keep the visual wind-down beside the cue. They are two halves of one event, and a
        // caller raising one alone would spin a dead aeroplane's propeller over snd_propstop. The
        // original's death routine runs it here too (docs/org/ordnanceTypes.md).
        PlayStopProps();
        // No plume survives a dead engine.
        ExhaustSmoke?.Reset();
        SpeedCue?.Reset();
    }

    // The authored crash camera: hard-cut to the static elevated vantage and hide the HUD, both
    // straight off the original's crash footage. Written once, and _Process writes nothing to the
    // camera while crashed, so the pose holds until respawn.
    private void CutToCrashView(Vector3 at)
    {
        if (!CameraOwned)
            _cam?.CrashView(at, _model.VelocityDir, World, Body?.ExcludeSelf);
        HideHudForStaticCamera();
    }

    // The DEATH camera, the destruction's own framing rather than the ground impact's: a spot
    // chosen once out of the death_* fields and held while the wreck falls past it. Unlike the
    // crash cut this one is stepped every frame, because it re-aims at a moving wreck.
    private void CutToDeathView()
    {
        if (CameraOwned || _cam == null)
            return;
        _deathCamera = true;
        _cam.EnterDeathView();
        StepDeathView();
        HideHudForStaticCamera();
    }

    // One stepped frame of the death camera, off the drawn pose so it tracks the wreck exactly the
    // way every other per-frame camera write does.
    private void StepDeathView()
    {
        _cam?.DeathView(_renderPose, _model.Speed, World, Body?.ExcludeSelf);
        _cam?.LogView(CameraView.Death, _model.Position, _model.Attitude);
    }

    // What every static camera owes the screen: no HUD (the original's crash and death footage
    // shows none) and no first-person dressing, since all of them are outside vantages.
    private void HideHudForStaticCamera()
    {
        if (_hudCanvas != null)
            _hudCanvas.Visible = false;
        LeaveFirstPerson();
    }

    // The crash camera is an external vantage, so the first-person hiding comes off with the cut.
    // ⚠ Not something _Process can do for us: it writes nothing to the camera while crashed, so
    // the last flying frame's state would stand for the whole crash. The interior lives OUTSIDE
    // PlaneModel under the overlay pass, which is why hiding the airframe does not take it with it.
    private void LeaveFirstPerson()
    {
        Cockpit?.Apply(_cam?.ViewMode ?? PilotViewMode.Chase, firstPerson: false);
        CockpitPass?.Deactivate();
        ShowPanel(false);
    }

    // The cockpit interior's on-screen frames drive two things together: the authored panel
    // needles, and the screen-space overlay coming off so the panel is not doubled by it.
    private void ShowPanel(bool shown)
    {
        _panelShown = shown;
        _pilotHud.SetCockpitView(shown);
    }

    // The dead hull flying itself, on the same model it flew alive: the original gates nothing in
    // the integrator on death (docs/org/flightModel.md, "A destroyed hull flies the same model").
    // ⚠ The commands FREEZE rather than neutralise: death stops the AI think, so nothing writes the
    // command vector and the integrator reads its last value. A default input here would be ours.
    // Runs until Callback 15 takes the hull over, so a wreck reaching the ground first still gets
    // its crash def, as the original's does.
    private void StepWreckFall(float dt)
    {
        if (!WreckFalling)
            return;
        var prev = _model.Position;
        // The stick freezes, the range does not: the original re-tests the far-field boundary every
        // step regardless of what is flying the aircraft, so a wreck drifting past it switches too.
        var falling = _lastInput;
        falling.NearestHumanDistSqM = NearestHumanDistSqM();
        _model.Step(falling, dt);
        if (_model.Position.Y < UnderMapY)
        {
            _lifecycle.StopWreckFall();   // lost under the map; nothing left to strike
            return;
        }
        if (HitWorld(prev, _model.Position, out var impact, out var hitName, out var hitBody))
        {
            Crash(impact, hitName, "center", hitBody);
            return;
        }
        _simPrev = _simCurr;
        _simCurr = _renderPose = new Transform3D(_model.Attitude, _model.Position);
        GlobalTransform = _simCurr;
    }

    /// <summary>The GROUND-IMPACT event: a live aircraft flown into the world, or a
    /// <see cref="Destroy"/>ed wreck reaching it. Plays the surface-indexed
    /// <see cref="CrashDefs"/> slot, which is where that family belongs.</summary>
    private void Crash(Vector3 impact, string hitName, string part, Node? hitBody, int? killer = null)
    {
        // A ground contact can be the first thing that ever happens to an aeroplane, so the rig is
        // forced here too rather than only on the damage intakes.
        EnsureCrashRig();
        // The original's selection: index by the struck material's surface id, falling back to
        // slot 0 (player_crash_default) for most of the ground (id 0 is ~98% of every chapter).
        int? surfaceId = SurfaceIdOf(hitBody);
        // The transition decides, including the guard and which of the four effects below are due;
        // everything after this line performs what it reported.
        var landing = _lifecycle.Crash(surfaceId, killer);
        if (!landing.Occurred)
            return;
        // The original has no crash effect of its own. A ground impact runs the same contact effect
        // every other contact does, and a fatal one is always past its heavy edge.
        if (IsHumanPiloted)
            _rumble.Play(RumbleEvent.ContactHeavy);
        string? crashDef = landing.CrashDef;
        if (PlaneModel != null)
            PlaneModel.Visible = false; // the airframe is gone; HUD prompts for respawn
        Body?.SetHittable(false);       // a crashed plane soaks no rounds and blocks no sweep
        if (landing.EndFlightSystems)
            EndFlightSystems();
        PlayCrashBoom(crashDef);
        if (CrashRuntime != null && crashDef != null)
        {
            // Plays the compiled def; see this module's entry in docs/architecture.md.
            // ⚠ Never seed an inherited velocity here: the `player_crash_*` defs this path plays
            // inherit none in the original, and only a `Callback 16` can arm one anyway.
            using (PerfSample.Scope(PerfSite.PartDetach))
            {
                CrashRuntime.Play(crashDef, CrashAnchor, applyReset: false);
            }
        }
        // ⚠ Not on a wreck landing: the cut and the report both belong to the kill, seconds
        // earlier, and re-cutting here would swing the camera off the fall it was framing.
        if (landing.CutCamera)
            CutToCrashView(impact);
        string surface = surfaceId is { } sid
            ? $"{sid}/{SurfaceRegistry.NameForId(sid) ?? "?"}"
            : "none";
        // A mid-air's ASPECT (diagnostic): which AI rule should have prevented it depends entirely
        // on whether the two met head-on, overtaking or side-on, and no crash line carries that.
        if (hitBody is AircraftBody struckAir)
        {
            var mine = _model.VelocityDir;
            var theirs = struckAir.Rig.WorldVelocity;
            var los = struckAir.Rig.WorldPosition - _model.Position;
            Log.Info("flight",
                $"midair aspect: into {hitName}, tracks {AngleBetweenDeg(mine, theirs):0}° apart (0 = same heading, 180 = head-on), line of sight {AngleBetweenDeg(mine, los):0}° off own track, spd mine={_model.Speed:0} theirs={theirs.Length():0} m/s");
        }
        Log.Info("flight",
            $"CRASH into {hitName} ({part}) surface={surface} def={crashDef ?? "-"} wreck={landing.WreckLanding} impact=({impact.X:0},{impact.Y:0},{impact.Z:0}) pos=({_model.Position.X:0},{_model.Position.Y:0},{_model.Position.Z:0}) spd={_model.Speed:0} m/s, waiting for respawn");
        if (landing.Downed)
            Downed?.Invoke(PlayerIndex, landing.Killer);
        // After the death report, so the crash notice reads above the kill line that death posted.
        // That is the order the original's two routines run in.
        GroundImpact?.Invoke(this);
    }

    // Resolves this seat's named actions, at most once per rendered frame. Godot's input state does
    // not move inside a frame, so one resolve answers every read a frame makes, the sim step, the
    // draw pass and the landing runtime alike, exactly as a direct hardware read did. Whichever of
    // them asks first pays for it, which is why there is no fixed call site.
    private void PollInput()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _inputFrame)
            return;
        _inputFrame = frame;
        // Splitscreen P2-P4 are pad-only and the field can change after construction, so the gate is
        // re-read rather than captured. The pad-half reader is never given the keyboard.
        _bindings.ReadsKeyboard = UseKeyboard;
        _keyActions.ReadsKeyboard = UseKeyboard;
        // A stick plugged or unplugged mid-flight moves seat 1's active profiles; the merge edits the
        // maps every resolver here reads.
        if (IsHumanPiloted && PlayerIndex == StickDeviceState.OwningSeat
            && StickProfiles.MergeIfChanged(_bindings, ref _stickRevision))
            ComposeControlPrompts();
        _seatState.Refresh();
        _padMutedState.Refresh();
        _actions.Poll(_seatState);
        _keyActions.Poll(_padMutedState);
        _padActions.Poll(_seatState);
        _padAxes.Poll(_padsAlone);
        _stickAxes.Poll(_sticksAlone);
        // A prompt names the device the seat last took input from, so a handover recomposes it.
        if (_bindings.ObserveDevice(_keyActions.Current, _padActions.Current, _stickAxes.Current))
        {
            if (IsHumanPiloted)
                LogHandover();
            ComposeControlPrompts();
        }
    }

    // Every control prompt this pane draws, over the bindings of the side the seat is reading. Run
    // at construction and again on each handover, rather than per frame: the wording is a string
    // build over the message table, and it only moves when a binding or the side does.
    private void ComposeControlPrompts()
    {
        var side = _bindings.Device.Side;
        bool onStick = _bindings.Device.OnStick;
        _pilotHud.AutoLandPrompt = FlightHud.ComposeAutoLandPrompt(
            _strings, FlightKeymap.Bindings(InputAction.AutoLand), UseKeyboard, side, onStick);
        _pilotHud.RespawnPrompt = FlightHud.ComposeRespawnPrompt(
            FlightKeymap.Bindings(InputAction.Respawn), UseKeyboard, side, onStick);
    }

    // Which actions each half held on the tick the prompt side moved. An unwanted handover is
    // then traced to the control that claimed it.
    private void LogHandover()
    {
        var device = _bindings.Device;
        string stick = device.OnStick ? " (stick)" : string.Empty;
        string keys = Held(_keyActions.Current);
        string pad = Held(_padActions.Current);
        string sticks = Held(_stickAxes.Current);
        Log.Info("core", $"prompt device P{PlayerIndex + 1}: {device.Side}{stick} keys=[{keys}] pad=[{pad}] sticks=[{sticks}]");

        static string Held(ActionSnapshot snapshot)
        {
            var held = new List<string>();
            foreach (var action in Enum.GetValues<InputAction>())
            {
                if (snapshot.Value(action) >= ActiveDevice.PressTravel)
                    held.Add(action.ToString());
            }

            return string.Join(",", held);
        }
    }

    // The largest-magnitude value of the axis across this player's gamepads (0 when
    // none), idle phantom devices read ~0 and never mask the real stick.
    private float PadAxis(JoyAxis axis)
    {
        float v = 0f;
        foreach (int pad in Pads.For(PadDevices))
        {
            float a = Input.GetJoyAxis(pad, axis);
            if (Mathf.Abs(a) > Mathf.Abs(v))
                v = a;
        }
        return v;
    }

    // A key, but only for a player the keyboard flies (splitscreen P2–P4 are pad-only).
    private bool KeyDown(Key key) => UseKeyboard && Input.IsKeyPressed(key);

    // A +/- key pair as an axis, honoring UseKeyboard.
    private float KeyAxis(Key positive, Key negative) =>
        (KeyDown(positive) ? 1f : 0f) - (KeyDown(negative) ? 1f : 0f);

    // R and pad Y while a results board is up: the direct route to the board's Restart item, and
    // the hold harness's automatic one. Read off the rendered frame on wall time, since the board
    // halts the clock and a sim-dt timer under a halt would never fire.
    private void PollResultsShortcuts(float delta)
    {
        if (Match is { Completed: true })
        {
            if (RespawnPressed())
                RestartMatch?.Invoke();
            return;
        }
        if (Race is { AllFinished: true } && Stunt is { AllComplete: true })
        {
            bool autoRematch = _holdSegments != null && PlayerIndex == 0
                && (_autoRestartIn -= delta) <= 0f;
            if (RespawnPressed() || autoRematch)
                RestartRace?.Invoke();
            return;
        }
        if (SoloBoardHolds && RespawnPressed())
            Rerun();   // the solo board takes R as a fresh run, distinct from a mid-run respawn
    }

    private bool RespawnPressed() => ReadLatched(InputAction.Respawn);

    // P, Esc or gamepad Start, edge-detected so one press toggles once, gated on AllowPause
    // (false for AI rigs and the suites' bare test rigs). Esc opens the pause board rather than
    // leaving the flight; the board's Exit item is what leaves, and a pad can reach it.
    // ⚠ Silent in photo mode and under the pause's options leaf: Escape is what LEAVES both, and
    // this reads Escape too, so one press would both close the screen and unpause the session
    // behind it.
    private bool PauseTogglePressed() =>
        AllowPause && !InPhotoMode && !InPauseLeaf && _actions.Held(InputAction.Pause);

    // One frame of the pause key, and the halt it mirrors into the shared clock. Polled from
    // _Process, not the sim step: a halted sim takes no steps and could never resume itself.
    // With a shared PauseState only the player who paused may resume it.
    private bool PollPauseAndHalt(GameClock? clock)
    {
        bool pausePressed = PauseTogglePressed();
        if (pausePressed && !_pausePrev)
        {
            if (PauseState != null)
                PauseState.TryToggle(PlayerIndex);
            else if (clock != null)
                clock.Halted = !clock.Halted;
        }
        _pausePrev = pausePressed;
        bool halted = PauseState?.Halted ?? (clock?.Halted ?? false);
        if (clock != null)
            clock.Halted = halted;
        if (halted != _haltPrev)
        {
            _haltPrev = halted;
            // The engine/whine/rattle loops hold their sample position through the freeze; the
            // one-shots already in flight are left to play out.
            Audio?.SetPaused(halted);
            // Clearing is the pause board's own re-entry point: the B or Enter that dismissed the
            // sheet can still be down on this very frame.
            if (!halted)
                SwallowInputHeldThroughReentry();
        }
        return halted;
    }

#pragma warning disable SA1202
    // One frame of the pause poll for the suite that drives the pause-to-flight boundary: a
    // headless rig renders no frame, so nothing calls _Process and the halt-clearing edge would
    // never run. Kept beside the poll it drives rather than hoisted for SA1202's sake.
    internal bool PollPauseForTest(GameClock? clock) => PollPauseAndHalt(clock);
#pragma warning restore SA1202

    /// <summary>One frame of player targeting: rebuild the pool and re-resolve, prune the
    /// attacker queue, then dispatch this frame's input. That order is the original's, its
    /// per-frame candidate pass runs in the sim step and a handler steps the list it just built,
    /// which is why a class change reads one frame late and self-heals.</summary>
    private void StepTargeting(float dt)
    {
        var sel = Targeting!;
        if (Projectiles != null && sel.ActiveClass != null)
        {
            // Skipped entirely with the selection cleared: `Target Nothing` zeroes the class flags
            // and the original then skips its whole collection pass, which is the mechanism that
            // keeps the clear cleared rather than an optimisation.
            _targetScan.Clear();
            Projectiles.CollectAircraft(_targetScan);
            SurfaceVehicles?.CollectVehicles(_targetScan);
            // No turret pass. A gun reaches the pilot's cycle only where a targets.zrd record
            // names its node. It then arrives on the site feed below as any structure does.
            // No shipped table names one, so collecting them would be unread work.

            // The fourth pool (E19): a TARGETABLE round in flight is selectable, which is why a
            // torpedo can be locked and shot at. The pool itself reads the admission byte.
            Projectiles.CollectFusedOrdnance(_targetScan);
            _targetParts.Clear();
            TargetSubParts?.Invoke(_targetParts);
            // The mission's objective sites, rebuilt from their live sources every frame, so a
            // site under a moving node is marked where it now is rather than where it was.
            _targetSites.Clear();
            TargetObjectives?.Invoke(_targetSites);
        }
        // The team is read off the FIELD. Deriving it from PlayerIndex is right for P1 by
        // coincidence and wrong for every other pane the moment a mission sets teams, the
        // wingman-in-the-marker bug (see TargetHud.OwnTeam).
        sel.Rebuild(_targetScan, _targetParts, Team, this, _model.Position, _model.Attitude,
            _targetSites, SelectedOrdnance());

        // The death prune (FUN_004a64e0). There is no session-wide Downed broadcast outside --vs,
        // so this pane prunes its own queue: a shot-down attacker must not be offered again.
        for (int i = sel.Attackers.Count - 1; i >= 0; i--)
        {
            if (sel.Attackers[i] is FlightController { InPlay: false } dead)
                sel.ForgetTarget(dead);
        }

        // --target=, before the input dispatch and before the InPlay gate: a --det run pins its
        // selection without a pilot who can press anything, and a real keypress on the same frame
        // should win over the scripted one rather than be overwritten by it.
        if (InitialTarget != null && !_initialTargetDone)
        {
            ApplyInitialTarget(sel);
        }

        // ⚠ Gate the INPUT on InPlay, not the rebuild. The freecam a downed pilot watches from binds
        // `U` among its own keys, so reading targeting from a spectator both re-targets a plane that
        // is not there and fights the camera. The selection keeps re-resolving regardless.
        if (!InPlay)
        {
            _targetHold.Step(false, dt);   // let a button held through the crash resolve as nothing
            return;
        }

        // D-pad Up, the pad's one targeting button (decision 6/7): tap steps the enemy cycle, hold
        // selects the target nearest the crosshair. TapHoldButton owns the timing and the
        // resolve-on-release rule; this reads the pad and acts on the verdict.
        switch (_targetHold.Step(TargetSplitterDown(), dt))
        {
            case TapHold.Hold:
                sel.NearestCrosshairs(_model.Position, _model.Attitude);
                break;
            case TapHold.Tap:
                sel.NextEnemy();
                break;
        }

        // The keyboard set: every one of the original's eleven targeting keys collides with our
        // flight scheme, so the shipped keys are this port's own. All eleven actions are here, one
        // per class per direction plus the two class-less ones, and each is rebindable.
        DispatchTargetKey(0, InputAction.TargetNextEnemy, () => sel.NextEnemy());
        DispatchTargetKey(1, InputAction.TargetNextAlly, () => sel.Next(TargetClass.Ally));
        DispatchTargetKey(2, InputAction.TargetNextNonAircraft, () => sel.Next(TargetClass.NonAircraft));
        DispatchTargetKey(3, InputAction.TargetNearest, () => sel.NearestCrosshairs(_model.Position, _model.Attitude));
        DispatchTargetKey(4, InputAction.TargetClear, () => sel.Clear());
        DispatchTargetKey(5, InputAction.TargetPreviousEnemy, () => sel.Previous(TargetClass.Enemy));
        DispatchTargetKey(6, InputAction.TargetPreviousAlly, () => sel.Previous(TargetClass.Ally));
        DispatchTargetKey(7, InputAction.TargetPreviousNonAircraft, () => sel.Previous(TargetClass.NonAircraft));
        DispatchTargetKey(8, InputAction.TargetNearestEnemy, () => sel.Nearest(TargetClass.Enemy));
        DispatchTargetKey(9, InputAction.TargetNearestAlly, () => sel.Nearest(TargetClass.Ally));
        DispatchTargetKey(10, InputAction.TargetNearestNonAircraft, () => sel.Nearest(TargetClass.NonAircraft));

        // The spyglass toggle, both halves on their own slots: unlike the keys above, the pad
        // control here is a button of its own rather than the tap/hold splitter, so a pad-only
        // pilot reaches it without dispatching the action twice.
        DispatchEdge(11, _keyActions.Held(InputAction.ToggleSpyglass), ToggleSpyglass);
        DispatchEdge(12, _padActions.Held(InputAction.ToggleSpyglass), ToggleSpyglass);
    }

    // The pilot's spyglass arm/disarm. Every other gate (off screen, the range band) is re-answered
    // every frame regardless, so this flips one flag and logs the transition.
    private void ToggleSpyglass()
    {
        if (_pilotHud.TargetHud is not { } hud)
        {
            return;
        }

        hud.SpyglassOn = !hud.SpyglassOn;
        Log.Info("flight",
            $"targeting hud: P{PlayerIndex + 1} spyglass {(hud.SpyglassOn ? "on" : "off")}");
    }

    // DispatchTargetKey's edge rule for a control the CALLER reads, so one action can take a slot
    // per device half rather than the keyboard alone.
    private void DispatchEdge(int slot, bool down, System.Action act)
    {
        if (down && !_targetKeyPrev[slot])
        {
            act();
        }

        _targetKeyPrev[slot] = down;
    }

    /// <summary>Spends <c>--target=</c>'s one application. Waits for a non-empty pool first:
    /// the things it can name (AI spawns, the zeppelins, a generator's first drop) are all built
    /// after the rigs are, so applying on frame one would match nothing in every session.
    /// <c>none</c> needs no pool and does not wait.</summary>
    private void ApplyInitialTarget(TargetSelection sel)
    {
        bool needsPool = !string.Equals(InitialTarget, "none", System.StringComparison.OrdinalIgnoreCase);
        if (needsPool && sel.Pool.Count == 0 && ++_initialTargetWaits < InitialTargetGrace)
        {
            return;
        }

        _initialTargetDone = true;      // spent whether or not it matched: one chance, then hands off
        if (sel.ApplyInitial(InitialTarget!, _model.Position, _model.Attitude))
        {
            string picked = sel.Current is { } t && t.Name.Length > 0 ? t.Name : "nothing";
            Log.Info("flight", $"--target={InitialTarget}: {picked} (class={sel.ActiveClass?.ToString() ?? "cleared"})");
            return;
        }

        // Naming what IS selectable is the whole diagnosis for a mistyped node name, and it is why
        // the flag needs no separate listing mode.
        var names = new List<string>();
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            foreach (var t in sel.Pool.Of(cls))
            {
                if (t.Name.Length > 0 && !names.Contains(t.Name))
                {
                    names.Add(t.Name);
                }
            }
        }

        names.Sort(System.StringComparer.OrdinalIgnoreCase);
        const int MaxNamed = 24;    // a C1 session offers ~100; enough to recognise a typo, not a wall
        int extra = names.Count - MaxNamed;
        if (extra > 0)
        {
            names.RemoveRange(MaxNamed, extra);
        }

        string listed = names.Count == 0 ? "(nothing)"
            : string.Join(", ", names) + (extra > 0 ? $", +{extra} more" : "");
        Log.Warn("core", $"--target={InitialTarget}: no match, selectable now: {listed}");
    }

    /// <summary>The view-selection inputs, edge-detected: F8 or D-pad Down advances the original's
    /// three-stop Cockpit → Nose → Chase cycle; F2 or the pad's Back/Select selects Chase directly.
    /// The keyboard bindings and pad slots are this port's choices; the original's binding menu
    /// also places its cycle action on a joystick button.
    /// The pad half makes the views reachable for a pad-only pilot (P2–P4), who has no keyboard.</summary>
    private void PollViewModeKeys()
    {
        DispatchViewModeKey(0, _keyActions.Held(InputAction.CycleCockpitViews), () => _cam!.CycleCockpitViews());
        DispatchViewModeKey(1, _keyActions.Held(InputAction.SelectChaseView), () => _cam!.SelectChase());
        DispatchViewModeKey(2, _padActions.Held(InputAction.CycleCockpitViews), () => _cam!.CycleCockpitViews());
        DispatchViewModeKey(3, _padActions.Held(InputAction.SelectChaseView), () => _cam!.SelectChase());
        DispatchViewModeKey(4, FlybyDown(), () => _cam!.EnterFlyby());
    }

    // The whole seat, since nothing ships on the pad for it: a pad or stick control here is one the
    // player bound themselves.
    private bool FlybyDown() => _actions.Held(InputAction.FlybyView);

    // Same one-action-per-press rule as DispatchTargetKey, against its own slots.
    private void DispatchViewModeKey(int slot, bool down, System.Action act)
    {
        if (down && !_viewModeKeyPrev[slot])
            act();
        _viewModeKeyPrev[slot] = down;
    }

    /// <summary>Edge-detects one targeting key against its own slot and runs its action once per
    /// press.</summary>
    private void DispatchTargetKey(int slot, InputAction action, System.Action act)
    {
        bool down = TargetControlDown(action);
        if (down && !_targetKeyPrev[slot])
            act();
        _targetKeyPrev[slot] = down;
    }

    // ⚠ The next-enemy row reads the keyboard half alone, since its pad and stick controls reach the
    // tap/hold splitter, which would otherwise dispatch it twice. Every other row reads the whole
    // seat, or a stick button bound to it would do nothing.
    private bool TargetControlDown(InputAction action) =>
        action == InputAction.TargetNextEnemy ? _keyActions.Held(action) : _actions.Held(action);

    // The next-enemy row's pad and stick half, which the tap/hold splitter reads.
    private bool TargetSplitterDown() => _padActions.Held(InputAction.TargetNextEnemy);

    // Internal rather than private: IFlightInputSource.cs's PilotInputSource calls this to keep
    // the body where it always lived, rather than hoisting it for SA1202's sake. The scripted hold
    // sequence's own body lives on ScriptedInputSource now, which needs no such wrapper.
#pragma warning disable SA1202
    // The AI pilot's throttle is the desired lever (+0x124); like keyboard input, its live
    // flight-model lever (+0x128) must traverse at 0.5/s. Carrier launch seeds both at 0.1,
    // then the AI may immediately request full power without erasing the visible settling ramp.
    internal FlightInput NextPilotInput(float dt)
    {
        // The mode machine's two host seams, the obstacle probe (D11 avoid crash) and the nitro
        // cull, are this node's own state. They are wired lazily, so a machine assigned after spawn
        // still gets them, and never overwrite what a test injected.
        if (Pilot!.Machine is { } machine && IsInsideTree())
        {
            machine.ProbeBlocked ??= AvoidCrashBlocksLine;
            // The maneuver picker's injector cull: a nitro-flagged entry is drawable only on an
            // aircraft that can actually boost.
            machine.NitroUsable ??= () => Nitro.Installed && !_model.EngineDead;
        }
        var input = Pilot!.Next(_model, dt);
        _throttleSetting = input.Throttle;
        _leverGap = _throttleSetting - _throttle;
        _throttle = Mathf.MoveToward(_throttle, _throttleSetting, ThrottleRate * dt);
        input.Throttle = _throttle;
        return input;
    }
#pragma warning restore SA1202

    // Four small static helpers for the D36 gunner-target widening (BL-363), kept beside the
    // instance methods that are their only real callers rather than hoisted for SA1204's sake,
    // the same trade the SA1202 blocks elsewhere in this file already make.
#pragma warning disable SA1204
    // A standing gunner target's current geometry and liveness, read off whichever source type it
    // actually is: a FlightController, a SurfaceVehicle, a TurretController or a
    // DestructibleRegistry.Instance. No non-aircraft source carries a facing axis (forward comes
    // back zero), which is what collapses the aspect test and the lead offset to "aim at the
    // position". Internal: TargetingOverlay's F15 debug line reuses this same lookup.
    internal static bool TryTargetGeometry(object? target, out Vector3 position, out Vector3 velocity,
        out Vector3 forward, out bool live)
    {
        switch (target)
        {
            case FlightController fc:
                position = fc.WorldPosition;
                velocity = fc.WorldVelocity;
                forward = fc.NoseDirection;
                live = fc.InPlay && fc.IsInsideTree();
                return true;
            case SurfaceVehicle hull:
                // A hull that has not been woken is present but not live, the same shape an inert
                // pilot takes: it is not a target until its WAKEUP_ENEMIES clause runs.
                live = !hull.Inert && !hull.IsDestroyed && GodotObject.IsInstanceValid(hull.Body)
                    && hull.Body.IsInsideTree();
                position = live ? hull.Position : Vector3.Zero;
                velocity = hull.Velocity;
                forward = Vector3.Zero;
                return true;
            case TurretController t:
                position = t.WorldPosition;
                velocity = t.PlatformVelocity;
                forward = Vector3.Zero;
                live = t.Alive;
                return true;
            case DestructibleRegistry.Instance inst:
                live = inst.Status != DestructibleRegistry.State.Destroyed
                    && GodotObject.IsInstanceValid(inst.Anchor) && inst.Anchor.IsInsideTree();
                position = live ? inst.Anchor.GlobalPosition : Vector3.Zero;
                velocity = Vector3.Zero;
                forward = Vector3.Zero;
                return true;
            default:
                position = velocity = forward = Vector3.Zero;
                live = false;
                return false;
        }
    }

    // Where a target source is DRAWN this frame, as opposed to where the gun solves to. An
    // aircraft's WorldPosition is the last physics pose; its node sits on _renderPose, the pose
    // interpolated between sim steps that the chase camera follows too. A marker projected from
    // the physics pose through that camera stalls between ticks and jumps on each, a shake that
    // grows with angular rate. Turret and structure positions are node positions already.
    // False on a freed, out-of-tree or unknown source: the caller keeps its physics snapshot.
    internal static bool TryRenderPosition(object? source, out Vector3 position)
    {
        switch (source)
        {
            case FlightController fc when GodotObject.IsInstanceValid(fc) && fc.IsInsideTree():
                position = fc.GlobalPosition;
                return true;
            case TurretController t:
                position = t.WorldPosition;
                return true;
            case DestructibleRegistry.Instance inst when GodotObject.IsInstanceValid(inst.Anchor)
                && inst.Anchor.IsInsideTree():
                position = inst.Anchor.GlobalPosition;
                return true;
            default:
                position = Vector3.Zero;
                return false;
        }
    }

    // The breadcrumb label for a standing target: "P{n}" for a human-readable aircraft slot, the
    // node/label name (TargetPool.NameOf) for a turret or structure.
    // How a target is named in a log line and in the F15 overlay's roll-call. Internal so the
    // overlay reuses it: a second naming rule there would drift from what the flight log says,
    // and the two get read side by side when a run is being explained.
    internal static string TargetLabel(object? source) =>
        source is FlightController fc ? $"P{fc.PlayerIndex + 1}" : TargetPool.NameOf(source);

    // The gunner's one standing target of any class, which AiPilot reads as its pursuit quarry.
    private static object? StandingTarget(AiGunner gunner) => gunner.Target;

    // Whether this pilot may be offered a gasbag at all: the decoded admission gate walks the
    // weapon list for a DAMAGES_ZEPPELIN slot with ammo whose two launch timers have run out
    // (FUN_00420070, docs/org/aiPilot.md). A pilot that launches nothing has no such slot.
    private bool HasGasbagOrdnanceReady() => GasbagOrdnanceState() == "ready";

    // The gate's verdict as a word, for the acquisition breadcrumb: which of its four conditions
    // withheld the gasbags is what a flight log has to say when the torpedoes never come.
    private string GasbagOrdnanceState()
    {
        if (Loadout is not { Hardpoints.Count: > 0 })
            return "no pylons";
        if (Pilot?.Rocketeer is not { } rocketeer)
            return "no rocketeer";
        string state = "no gasbag pylon";
        for (int i = 0; i < Loadout.Hardpoints.Count; i++)
        {
            var hp = Loadout.Hardpoints[i];
            if (!hp.Weapon.DamagesZeppelin)
                continue;
            if (!hp.Armed(InfiniteAmmo))
                state = "gasbag pylon empty";
            else if (!rocketeer.SlotReady(i))
                state = "gasbag pylon locked";
            else
                return "ready";
        }
        return state;
    }
#pragma warning restore SA1204

    // Whether this tick keeps the standing target. The decoded hold: a picked target is re-scored
    // every tick and kept while it scores valid. Once the hold runs out the pool is swept whole
    // (docs/org/aiPilot.md). A target written straight onto AiGunner.Target, a mission order or an
    // airframe swap's replacement, carries no rank snapshot. It keeps to the simpler rule that
    // alive is enough. An assigned primary_target is kept while it is inside the attack radius.
    private bool HoldsStandingTarget(AiGunner gunner, out Vector3 pos, out Vector3 vel, out Vector3 fwd)
    {
        if (!TryTargetGeometry(StandingTarget(gunner), out pos, out vel, out fwd, out bool live)
            || !live)
            return false;
        float attack = Pilot?.Machine?.AttackRange ?? 2000f;
        // The assigned target is re-scored every tick in the original (FUN_0041fe10's primary arm),
        // so it stays only inside the attack volume. This is the range the promotion does not read.
        if (gunner.AutoTarget && gunner.IsPrimaryTarget(gunner.Target))
            return WorldPosition.DistanceSquaredTo(pos) <= attack * attack;
        if (!gunner.AutoTarget || !ReferenceEquals(gunner.TargetRankFor, gunner.Target))
            return true;
        if ((GameClock.Current?.Time ?? 0.0) >= gunner.TargetHoldUntil)
            return false;
        var rank = gunner.TargetRank;
        rank.Position = pos;   // the re-score runs on the live geometry; the bias terms stand
        rank.Velocity = vel;
        // ⚠ The withdrawal argument is CSVM's layer, not the decode. Evaluated last, and only for a
        // structure target, so the aircraft walk costs nothing on the ordinary aeroplane duel.
        return AiTargetRanking.KeepsStandingTarget(WorldPosition, NoseDirection,
            attack, AiScorer.Jet,
            AiTargetRanking.AircraftFirst && rank.IsStructureClass && EnemyAircraftRanks(gunner),
            rank);
    }

    // Whether one live enemy aeroplane is in reach, the withdrawal's test, over the aircraft roster
    // alone: a hull is neither class the preference suppresses. Ranking is the reach test in
    // AiTargetRanking.SelectBest. This asks the same two things it does, the attack radius and an
    // authored hard exclusion, rather than a second reading of "in reach".
    private bool EnemyAircraftRanks(AiGunner gunner)
    {
        if (Projectiles == null)
            return false;
        float attack = Pilot?.Machine?.AttackRange ?? 2000f;
        var ownPos = WorldPosition;
        _rescoreScan.Clear();
        Projectiles.CollectAircraft(_rescoreScan);
        foreach (var c in _rescoreScan.Vehicles)
        {
            if (!c.Live || c.Source is not FlightController fc || ReferenceEquals(fc, this))
                continue;
            if (!AimAssist.Hostile(Team, c.Team)
                || ownPos.DistanceSquaredTo(c.Position) > attack * attack)
                continue;
            float bias = AiTargetRanking.ObjectiveBiasFor(
                fc.IsHumanPiloted ? AiTargetRanking.PlayerRole : TargetPool.NameOf(c.Source),
                gunner.RatingBiases);
            if (bias < AiTargetRanking.NotRanked)
                return true;
        }

        return false;
    }

    // One AI-gunner tick: keep the standing target while the hold above holds it (re-acquiring
    // through the D12/D36 ranking when it is gone and AiGunner.AutoTarget allows), then
    // hand the gunner this tick's fire geometry, the SELECTED gun group's weapon and muzzle
    // midpoint, the sim pose (never the render pose), and the target's state, so
    // AiGunner.WantsFire is current when the fire step reads it.
    private void DriveAiGunner(AiGunner gunner)
    {
        gunner.HoldFire();
        if (_fire == null || Projectiles == null)
            return;
        if (!HoldsStandingTarget(gunner, out var targetPos, out var targetVel, out var targetFwd))
        {
            object? left = StandingTarget(gunner);
            TargetScore score = default;
            string how = "ranked";
            gunner.Target = gunner.AutoTarget
                ? SelectRankedTarget(gunner, out score, out how)
                : null;
            if (!TryTargetGeometry(StandingTarget(gunner), out targetPos, out targetVel, out targetFwd,
                    out bool targetLive) || !targetLive)
                return;
            if (!_gunnerLoggedTarget)
            {
                _gunnerLoggedTarget = true; // verification breadcrumb: who the gunner went after
                Log.Info("flight",
                    $"ai gunner: shooter {PlayerIndex} targets {TargetLabel(StandingTarget(gunner))} at {score.Distance:0} m ({how}: weight {score.Weight:0.0#} bias {score.Bias:0} rank {score.Rank:0}; gasbag ordnance {GasbagOrdnanceState()}, {_gunnerScan.Structures.Count} structure(s) in the scan)");
            }
            else if (_gunnerRetargetsLogged < 8 && !ReferenceEquals(left, StandingTarget(gunner)))
            {
                // Capped per shooter: the switch is the thing the re-score exists for, and a
                // flight of twelve re-scoring all mission must not flood the log.
                _gunnerRetargetsLogged++;
                Log.Info("flight",
                    $"ai gunner: shooter {PlayerIndex} leaves {TargetLabel(left)} for {TargetLabel(StandingTarget(gunner))} at {score.Distance:0} m ({how}: rank {score.Rank:0}; {_gunnerScan.Structures.Count} structure(s) in the scan)");
            }
        }
        // ⚠ Only Pursue shoots. Lay off holds fire deliberately (the rubber-band assist) even
        // though the target stays acquired in every mode.
        if (Pilot?.Machine is { } modes && modes.Mode != AiMode.Pursue)
            return;
        GunGroup? group = _fire.GunSel >= 0 && _fire.GunSel < _firableGuns.Length
            ? _firableGuns[_fire.GunSel]
            : null;
        if (group == null || group.Muzzles.Count == 0)
            return;
        // The muzzle midpoint of the selected group, the same convergence point the reticle
        // and the original's own barrel averaging use.
        var muzzlePos = Vector3.Zero;
        foreach (var m in group.Muzzles)
            muzzlePos += m.GlobalPosition;
        muzzlePos /= group.Muzzles.Count;
        // The engagement window is a property of the weapon slot, not of the pilot, so a group whose
        // AI def authored one flies that one; a stock fit authors none and keeps the gunner's own.
        if (group.MinRangeM > 0f && group.MaxRangeM > 0f)
        {
            gunner.MinRangeM = group.MinRangeM;
            gunner.MaxRangeM = group.MaxRangeM;
        }
        gunner.Solve(muzzlePos, WorldVelocity, _model.Attitude,
            targetPos, targetVel, targetFwd,
            group.Weapon.Velocity ?? ProjectilePool.DefaultVelocity);
        if (gunner.WantsFire && !_gunnerLoggedFire)
        {
            _gunnerLoggedFire = true; // verification breadcrumb: the gates first opened
            Log.Info("flight",
                $"ai gunner: shooter {PlayerIndex} opens fire on {TargetLabel(StandingTarget(gunner))} at {WorldPosition.DistanceTo(targetPos):0} m ({group.Weapon.Id})");
        }
    }

    // One AI-rocketeer tick: age the lockout unconditionally (it is a vehicle timer, not one that
    // stops when the AI loses its target), then walk the pylons against the GUNNER's target and let
    // the decision name the pylon it chose, so a DAMAGES_ZEPPELIN round cannot be launched by a
    // cursor that disagrees with the gates that cleared it.
    private void DriveAiRocketeer(AiRocketeer rocketeer, AiGunner gunner, float dt)
    {
        rocketeer.Tick(dt);
        if (_fire == null || Projectiles == null || Loadout is not { Hardpoints.Count: > 0 })
            return;
        if (!TryTargetGeometry(StandingTarget(gunner), out var targetPos, out var targetVel,
                out var targetFwd, out bool targetLive) || !targetLive)
            return;
        // ⚠ Only Pursue shoots, the same deliberate hold the guns take. The original restricts
        // neither, so revisiting this is one change for both classes, not two.
        if (Pilot?.Machine is { } modes && modes.Mode != AiMode.Pursue)
            return;
        _pylonViews.Clear();
        for (int i = 0; i < Loadout.Hardpoints.Count; i++)
        {
            var hp = Loadout.Hardpoints[i];
            _pylonViews.Add(new RocketPylonView
            {
                Index = i,   // the list position FireControl selects by, not the 1-based pylon number
                DamagesZeppelin = hp.Weapon.DamagesZeppelin,
                Armed = hp.Armed(InfiniteAmmo),
                MountPos = hp.Pylon.GlobalPosition,
                RoundSpeed = hp.Weapon.Velocity ?? ProjectilePool.DefaultVelocity,
                RoundAccel = hp.Weapon.Acceleration ?? 0f,
                MinRangeM = hp.MinRangeM,
                MaxRangeM = hp.MaxRangeM,
                RefireSeconds = hp.RefireSeconds,
            });
        }
        rocketeer.Solve(WorldPosition, WorldVelocity, _model.Attitude,
            targetPos, targetVel, targetFwd,
            targetIsGasbag: gunner.Target is DestructibleRegistry.Instance { Gasbag: true },
            _pylonViews);
        if (rocketeer.SelectedPylon >= 0)
            _fire.SelectPylon(rocketeer.SelectedPylon);
        // Each CHANGE of verdict, a few per shooter: the user's C4/M03 flights had the Warhawks on
        // a gasbag with the gate open and nothing launched, and the log could not say which gate.
        if (_rocketeerVerdictsLogged < 12 && rocketeer.LastVerdictKey.Length > 0
            && rocketeer.LastVerdictKey != _rocketeerLastVerdict)
        {
            _rocketeerLastVerdict = rocketeer.LastVerdictKey;
            _rocketeerVerdictsLogged++;
            Log.Info("flight",
                $"ai rocketeer: shooter {PlayerIndex} on {TargetLabel(StandingTarget(gunner))} at {WorldPosition.DistanceTo(targetPos):0} m: {rocketeer.LastVerdict}");
        }
        if (rocketeer.WantsFire && !_rocketeerLoggedFire)
        {
            _rocketeerLoggedFire = true; // verification breadcrumb: the ordnance gates first opened
            // pylon{Index}, never the list position the selection runs on: the launch line the
            // fire step logs names the hardpoint's OWN number, and two numbers for one pylon in
            // adjacent lines is how a reader concludes the wrong pylon fired.
            var hp = Loadout.Hardpoints[rocketeer.SelectedPylon];
            Log.Info("flight",
                $"ai rocketeer: shooter {PlayerIndex} launches at {TargetLabel(StandingTarget(gunner))} at {WorldPosition.DistanceTo(targetPos):0} m ({hp.Weapon.Id}, pylon{hp.Index})");
        }
    }

    // The acquisition: the decoded ranking formula over the whole VehicleList, the turrets and the
    // structures (TargetVehicle/TargetTurret/TargetStruct), swept for one global minimum, same
    // roster and team gate as the aim assist. A live PrimaryTargetName is picked outright; its
    // "player" token resolves to the nearest human (C22), which only an aeroplane can be.
    // Decode: docs/org/aiPilot.md.
    // ⚠ Deconfliction (AiTargetRanking) stays zero outside a mission.
    private object? SelectRankedTarget(AiGunner gunner, out TargetScore score, out string how)
    {
        score = default;
        how = "ranked";
        if (Projectiles == null)
            return null;
        _gunnerScan.Clear();
        // ⚠ The whole VehicleList, not its aircraft half: the decoded sweep walks the one list that
        // holds the AI ground and sea vehicles beside the aircraft, so a boat or a turret truck is a
        // candidate as the vessel it is (docs/org/targeting.md). Never by being registered aircraft.
        Projectiles.CollectVehicleList(_gunnerScan);
        Projectiles.CollectTurrets(_gunnerScan);
        if (Destructibles != null)
        {
            _gunnerScan.AddMissionStructures(Destructibles);
        }
        int ownTeam = Team;
        // ⚠ The attack volume, not the activation one. Both decoded scorers admit on the attack
        // cylinder, and the activation volume is the original's awake test alone. A DEDG widening
        // therefore never reaches acquisition (docs/org/aiPilot.md).
        float attack = Pilot?.Machine?.AttackRange ?? 2000f;
        var ownPos = WorldPosition;
        var ownFwd = NoseDirection;
        _rankCandidates.Clear();
        _rankSources.Clear();
        object? primary = null;
        FlightController? nearestHuman = null;
        float nearestHumanDistSq = float.MaxValue;
        foreach (var c in _gunnerScan.Vehicles)
        {
            if (!c.Live || c.Source == null || ReferenceEquals(c.Source, this))
                continue;
            if (c.Team == AimAssist.NeutralTeam || ownTeam == AimAssist.NeutralTeam || c.Team == ownTeam)
                continue;
            // ⚠ Read the source's TYPE, never gate on it: a hull is on this list and carries no
            // FlightController, so every term below has a source-typed reading and the ones that
            // are properties of an aeroplane simply do not apply to it.
            var fc = c.Source as FlightController;
            if (gunner.PrimaryTargetName is { Length: > 0 } wanted
                && ownPos.DistanceSquaredTo(c.Position) <= attack * attack)
            {
                if (primary == null
                    && string.Equals(TargetPool.NameOf(c.Source), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    primary = c.Source; // a by-NAME assignment names one entry: first match is it
                }
                else if (fc is { IsHumanPiloted: true }
                    && wanted.Equals(AiTargetRanking.PlayerRole, StringComparison.OrdinalIgnoreCase))
                {
                    // "player" is a role, not a name (C22); resolved ONCE per acquisition.
                    float d = ownPos.DistanceSquaredTo(c.Position);
                    if (d < nearestHumanDistSq)
                    {
                        nearestHumanDistSq = d;
                        nearestHuman = fc;
                    }
                }
            }

            // Allied gunners already on this candidate (the deconfliction input).
            int attackers = 0;
            foreach (var a in _gunnerScan.Vehicles)
            {
                if (a.Team == ownTeam && a.Source is FlightController ally
                    && !ReferenceEquals(ally, this)
                    && ReferenceEquals(ally.Pilot?.Gunner?.Target, c.Source))
                    attackers++;
            }

            // Wingman mode is the netless escort: a net demotes the mode to jet at spawn, and
            // CSVM's session build makes the same fork (docs/org/aiPilot.md "Net assignment"). A
            // hull flies neither, so both flags read false for it off the null cast.
            bool human = fc is { IsHumanPiloted: true };
            _rankCandidates.Add(new RankedTargetCandidate
            {
                Position = c.Position,
                Velocity = c.Velocity,
                IsPlayer = human,
                IsAircraft = fc != null,
                IsWingman = fc?.Pilot?.Escort != null,
                // target_bias is the CANDIDATE's own field on the vehicle arm. A hull carries no def
                // of its own here and spends nothing, as the shipped hull defs do.
                ClassBias = fc?.Stats?.AiTargetBias ?? 0f,
                ObjectiveBias = AiTargetRanking.ObjectiveBiasFor(
                    human ? AiTargetRanking.PlayerRole : TargetPool.NameOf(c.Source),
                    gunner.RatingBiases),
                AlliedAttackers = attackers,
            });
            _rankSources.Add(c.Source);
        }

        // Turrets and structures: the other two pools, neither with a primary_target term. A
        // structure reaches the gate on its pool's authored team, so an unauthored one is neutral
        // and never ranked; a gasbag reaches it only past the ordnance gate (docs/org/aiPilot.md).
        bool gasbagsAdmitted = HasGasbagOrdnanceReady();
        AddRankedNonAircraft(_gunnerScan.Turrets, isTurret: true, ownTeam, gunner, gasbagsAdmitted);
        AddRankedNonAircraft(_gunnerScan.Structures, isTurret: false, ownTeam, gunner, gasbagsAdmitted);

        bool byRole = primary == null && nearestHuman != null;
        primary ??= nearestHuman;
        if (primary != null)
        {
            // Log the assigned pick with its own rank inputs (informational, rank not consulted).
            int idx = _rankSources.IndexOf(primary);
            if (idx >= 0)
                score = AiTargetRanking.Score(ownPos, ownFwd, attack, AiScorer.Jet,
                    _rankCandidates[idx]);
            how = byRole ? "primary target: nearest human" : "primary target";
            return primary;
        }

        // ⚠ Jet is asserted, not derived: the engine picks the scorer off the SHOOTER's own mode,
        // so a mode plane or heli aeroplane should take Other. Deriving it here would change what
        // those aircraft target, which is a behaviour claim wanting its own evidence.
        int best = AiTargetRanking.SelectBest(ownPos, ownFwd, attack, AiScorer.Jet,
            AiTargetRanking.AircraftFirst, _rankCandidates, out score);
        if (best < 0)
            return null;
        // The take stamps the hold and the rank the re-score re-runs. Only the ranked arm does:
        // an assigned primary_target wins outright at every acquisition in the original, so it
        // never reaches the hold at all.
        gunner.TakeTarget(_rankSources[best], _rankCandidates[best], GameClock.Current?.Time ?? 0.0);
        return _rankSources[best];
    }

    // Files one turret or structure candidate into the shared rank pool, mirroring the vehicle
    // loop's team gate and allied-attacker count above (TargetTurret/TargetStruct). A gasbag is
    // dropped at admission unless the pilot's gasbag ordnance is live, the original's
    // FUN_0041f9c0 third argument.
    private void AddRankedNonAircraft(List<AimCandidate> pool, bool isTurret, int ownTeam,
        AiGunner gunner, bool gasbagsAdmitted)
    {
        foreach (var c in pool)
        {
            if (!c.Live || c.Source == null || ReferenceEquals(c.Source, this))
                continue;
            if (c.Team == AimAssist.NeutralTeam || ownTeam == AimAssist.NeutralTeam
                || c.Team == ownTeam)
                continue;
            // A carried turret's host is already ranked as a vehicle above; offering it again
            // here would put two entries on one silhouette. Mirrors TargetPool.Offer's guard.
            if (isTurret && !TargetPool.IsEmplacement(c.Source))
                continue;
            bool gasbag = c.Source is DestructibleRegistry.Instance { Gasbag: true };
            if (gasbag && !gasbagsAdmitted)
                continue;
            // A part answers to every name above it, which is what carries a roster's exclusion
            // naming a mission structure down onto the structure's own engines and guns. Skipped
            // outright with no list to match against, since the walk is then unread work.
            _biasOwners.Clear();
            if (gunner.RatingBiases is { Count: > 0 })
                TargetPool.CollectOwners(c.Source, _biasOwners);

            int attackers = 0;
            foreach (var a in _gunnerScan.Vehicles)
            {
                if (a.Team == ownTeam && a.Source is FlightController ally
                    && !ReferenceEquals(ally, this)
                    && ReferenceEquals(ally.Pilot?.Gunner?.Target, c.Source))
                    attackers++;
            }

            _rankCandidates.Add(new RankedTargetCandidate
            {
                Position = c.Position,
                Velocity = c.Velocity,
                IsPlayer = false,
                IsStructureClass = true,
                IsGasbag = gasbag,
                // struct_bias is the SCORER's own field, spent on every turret and structure
                // candidate alike. It is read off this aeroplane and never off the candidate.
                ClassBias = Stats?.AiStructBias ?? 0f,
                ObjectiveBias = AiTargetRanking.ObjectiveBiasFor(
                    TargetPool.NameOf(c.Source), _biasOwners,
                    gunner.RatingBiases, isTurret),
                AlliedAttackers = attackers,
            });
            _rankSources.Add(c.Source);
        }
    }

#pragma warning disable SA1202
    // Internal rather than private: the carried-turret dedup suite asserts on the ranked pool's
    // shape directly (one entry per silhouette) rather than inferring it from which one thing
    // ranking picks, which a same-position duplicate could still pass by accident.
    internal IReadOnlyList<object?> RankedPoolSourcesForTest(AiGunner gunner)
    {
        SelectRankedTarget(gunner, out _, out _);
        return _rankSources;
    }

    // Internal rather than private: the admission suite asserts on the scan's own membership. A
    // pool the team gate would drop downstream is indistinguishable from one never admitted. The
    // decoded list is the narrower one, so only the scan itself can show which happened.
    internal int ScannedStructureCountForTest() => _gunnerScan.Structures.Count;

    // Internal rather than private: PilotInputSource/KeyboardInputSource (IFlightInputSource.cs)
    // call this and its sibling above to keep each body exactly where it always lived among the
    // other sim-step helpers, rather than hoisting it for SA1202's sake.
    internal FlightInput ReadKeyboard(float dt)
    {
        // this player's gamepads and flight sticks fly the plane, each through its own reader.
        // Arcade-flight standard: stick back (+Y) = nose up, stick right = bank right.
        var pad = AnalogAxes.Pad(_padAxes.Current);
        var stick = AnalogAxes.Stick(_stickAxes.Current);
        // The live respawn, read only where the mission does not count (AllowLiveRespawn): this
        // body runs on a flying aircraft alone, since SimStep leaves through its crashed branch,
        // and that branch's own read is what brings a crashed pilot back.
        if (AllowLiveRespawn && _padActions.Held(InputAction.Respawn))
            Respawn();

        if (AllowLiveRespawn && _keyActions.Held(InputAction.Respawn))
            Respawn();

        // The commanded lever, as FUN_00487460 writes it. The up and down keys move it at 0.5/s,
        // and a digit puts it on its eighth. It stays there once the key is up. The handler never
        // reads the tank, so a dry engine still takes the command.
        float rate = _keyActions.Axis(InputAction.ThrottleUp, InputAction.ThrottleDown) + pad.ThrottleRate + stick.ThrottleRate;
        _throttleSetting = Mathf.Clamp(_throttleSetting + (rate * ThrottleRate * dt), 0f, 1f);
        float? scheduled = ScheduledThrottle(dt);
        float? requested = RequestedThrottle();
        // An absolute lever writes over the rate step and under the schedule and the digits. That is
        // the rule a live digit beating the schedule already follows. Any of those three hands it back.
        if (LeverSetting(Mathf.Abs(rate) > LeverTakeover.Epsilon || scheduled != null || requested != null) is { } lever)
            _throttleSetting = lever;
        if (scheduled is { } step)
            _throttleSetting = step;
        if (requested is { } digit)
            _throttleSetting = digit;

        // ⚠ Slew every tick, not only while a key is down. Stepping it inside the digit test leaves
        // a tapped setting barely moved. A dry tank skips the slew, so the lever holds rather than
        // closing; a crashed airframe still moves it (the original's crashed-flag arm).
        _leverGap = _throttleSetting - _throttle; // ahead of the fuel test, as FUN_0048e580 reads it
        if (Crashed || Fuel.Step(dt, _throttle))
            _throttle = Mathf.MoveToward(_throttle, _throttleSetting, ThrottleRate * dt);

        // pull = S/Down, push = W/Up; bank/yaw left = A/Left/Q
        _keyPitch = StickRamp.Step(
            _keyPitch, Mathf.Sign(_keyActions.Axis(InputAction.PitchUp, InputAction.PitchDown)), dt);
        _keyRoll = StickRamp.Step(
            _keyRoll, Mathf.Sign(_keyActions.Axis(InputAction.RollLeft, InputAction.RollRight)), dt);
        _keyYaw = StickRamp.Step(
            _keyYaw, _keyActions.Axis(InputAction.YawLeft, InputAction.YawRight), dt);

        var mouse = MouseFlightRead();
        return new FlightInput
        {
            Pitch = Mathf.Clamp(_keyPitch + pad.Pitch + stick.Pitch + mouse.Pitch, -1f, 1f),
            Roll = Mathf.Clamp(_keyRoll + pad.Roll + stick.Roll + mouse.Roll, -1f, 1f),
            Yaw = Mathf.Clamp(_keyYaw + pad.Yaw + stick.Yaw + mouse.Yaw, -1f, 1f),
            Throttle = _throttle,
        };
    }

    // What the mouse adds to this tick's stick. It adds nothing at all unless this seat flies with
    // the mouse and the free-look control is up. That is the original's own guard: the mouse
    // control bit of DAT_0071c2a0 set and DAT_00654120 clear. The contribution SUMS into the
    // keyboard and pad deflections rather than replacing them, as the original's arm sums into the
    // same slots. An autogyro pilot therefore still banks with the roll keys while the mouse yaws.
    internal FlightInput MouseFlightRead()
    {
        if (!MouseFlying || FreeLookHeld())
            return default;
        var stick = MouseStick();
        // The wheel is the original's third mouse axis and this port reads two, so it passes zero.
        // An aeroplane takes no yaw from the mouse and an autogyro no roll (docs/controls.md).
        return MouseFlight.Read(stick.X, stick.Y, 0f, Stats?.IsAutogyro ?? false);
    }

    // Whether the mouse is head-look's this frame rather than the stick's. For a suite that drives
    // the posture through the free-look control instead of asserting on the camera behind it.
    internal bool FreeLookActiveForTest() => FreeLookHeld();

    // Whether this seat would take the mouse on this frame. For a suite that reads the decision on
    // a desktop no session is allowed to capture on.
    internal bool WantsMouseCaptureForTest(bool halted) => WantsMouseCapture(halted);

    // Whether this seat holds the mouse right now, the other half of that reading.
    internal bool HoldsMouseForTest() => _mouse.Holding;

    // One frame of the capture decision. For a suite that reads what the mouse mode is left at
    // without rendering a frame for _Process to run on.
    internal void StepMouseCaptureForTest(bool halted) => StepMouseCapture(halted);

    // Where the cursor stands in this seat's pane, or the suite's stand-in while one is set. A seat
    // outside the tree has no pane to measure, and reads centred rather than guessing one.
    private Vector2 MouseStick()
    {
        if (MouseStickForTest is { } pinned)
            return new Vector2(Mathf.Clamp(pinned.X, -1f, 1f), Mathf.Clamp(pinned.Y, -1f, 1f));
        if (!IsInsideTree() || GetViewport() is not { } viewport)
            return Vector2.Zero;
        var half = viewport.GetVisibleRect().Size * 0.5f;
        // A captured pointer reports one fixed position, so the virtual cursor stands in for it,
        // with its own wider centre band. Off capture this is the pane's own cursor and decoded law.
        if (_mouse.Holding)
            return MouseCapture.Centred(MouseFlight.Offset(_mouse.Cursor, half, half));
        return MouseFlight.Offset(viewport.GetMousePosition(), half, half);
    }

    // The seat's hold on the desktop mouse, re-decided every frame. A board that draws its own
    // pointer halts the session. The halt is what hands the pointer back to the pause sheet, the
    // preferences page and the wrap-up boards alike. The resume takes it again.
    private void StepMouseCapture(bool halted)
    {
        bool wanted = WantsMouseCapture(halted);
        if (wanted && !_mouse.Holding)
        {
            _mouse.Take(PaneCursor());
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (!wanted)
        {
            ReleaseMouseCapture();
        }

        if (_mouse.Holding)
            _mouse.StepCursor(PaneSize(), MouseSensitivity);
    }

    // Whether this seat should be holding the mouse. One physical mouse, so only the seat the
    // keyboard flies asks for it. A watcher's pane belongs to its SpectatorCamera, which reads the
    // right button itself. A halt belongs to whichever board went up.
    private bool WantsMouseCapture(bool halted) =>
        MouseCaptureAllowed && IsHumanPiloted && UseKeyboard && IsInsideTree()
        && !Inert && !Spectating && !halted && !InPhotoMode && !InPauseLeaf;

    // Puts back only what this seat took. A board that has already swapped the mode for its own
    // drawn cursor is left alone. The release then cannot show the OS pointer over a pause sheet.
    private void ReleaseMouseCapture()
    {
        if (!_mouse.Holding)
            return;
        _mouse.Release();
        if (Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // This seat's pane and the real cursor in it, both zero outside the tree.
    private Vector2 PaneSize() =>
        IsInsideTree() && GetViewport() is { } viewport ? viewport.GetVisibleRect().Size : Vector2.Zero;

    private Vector2 PaneCursor() =>
        IsInsideTree() && GetViewport() is { } viewport ? viewport.GetMousePosition() : Vector2.Zero;

    // Whether the free-look control is down, this port's reading of DAT_00654120. It decides which
    // of two consumers gets the mouse, and it is a HOLD under both mouse schemes. ⚠ Do not make it
    // a toggle. One tap would leave the mouse on the head for the rest of the sortie. The original
    // holds the look only while the button is down.
    private bool FreeLookHeld() => _actions.Held(InputAction.FreeLook);

    // Which eighth the digit row is asking for, or null while none of the nine is held. The highest
    // held wins, so two digits at once open the lever rather than fighting over it. The whole seat
    // is read, so a stick button bound to an eighth sets it too.
    private float? RequestedThrottle()
    {
        float? requested = null;
        for (int eighths = 0; eighths <= 8; eighths++)
        {
            if (_actions.Held(InputAction.ThrottleSet0 + eighths))
                requested = eighths / 8f;
        }

        return requested;
    }

    // What a bound Throttle (lever) commands this tick, or null while the other controls hold the
    // throttle. A rate under the takeover epsilon is a resting trigger's noise, not a command. While
    // input is blocked every axis reads centred, so the lever is forgotten rather than read as half.
    // An unplugged stick's lever reads centred the same way, and is forgotten the same way.
    private float? LeverSetting(bool otherCommand)
    {
        if (CSVM.Pads.InputBlocked)
        {
            _leverTakeover.Release();
            return null;
        }

        var sticks = PlayerIndex == StickDeviceState.OwningSeat ? StickPump.Roster : null;
        float? position = AnalogAxes.LeverPosition(
            _padActions.Map.Bindings(InputAction.ThrottleLever),
            _padAxes.Value(InputAction.ThrottleLever),
            _stickAxes.Value(InputAction.ThrottleLever),
            sticks);
        return AnalogAxes.StepLever(_leverTakeover, position, otherCommand);
    }

    // Which lever the --lever= schedule presses on this step, or null while none is due. A step is
    // one press: the commanded lever jumps there and stays. The live one then slews to it at its own
    // rate, so the gap the exhaust smoke charges from is a real one. A live digit beats it, since
    // the caller who added the schedule is not the one at the keyboard.
    private float? ScheduledThrottle(float dt)
    {
        if (_leverSteps == null)
            return null;
        _leverElapsed += dt;
        float? pressed = null;
        while (_leverNext < _leverSteps.Count && _leverSteps[_leverNext].At <= _leverElapsed)
            pressed = _leverSteps[_leverNext++].Lever;
        return pressed;
    }

    // Both levers on one value, for the writers that place an aircraft rather than fly it. The
    // original's spawn and launch writers set +0x124 and +0x128 together. A stale command left
    // behind would slew a respawned lever away from where it was placed. A bound throttle lever is
    // released for the same reason; it takes over again when it moves.
    private void SetLever(float lever)
    {
        _throttle = _throttleSetting = lever;
        _leverTakeover.Release();
    }
#pragma warning restore SA1202

    // This plane's state entering a contact, as the resolver's per-call half.
    private ContactConditions Striking() => new()
    {
        IsHumanPiloted = IsHumanPiloted,
        VelocityDir = _model.VelocityDir,
        Speed = _model.Speed,
        Pose = GlobalTransform,
        Stats = Stats,
        Ledger = Damage,
        Parts = Collider?.Parts,
        ExcludeSelf = Body?.ExcludeSelf,
    };

    // The outcome's instructions, which only this side can perform: the struck aeroplane's share
    // of the pair, the pilot's impact line, and the push out of whatever the graze embedded this
    // airframe in. One value, so forgetting it is forgetting one call rather than four.
    private void PerformContact(in ContactOutcome outcome, in ContactReport contact, Node? hitBody)
    {
        // The node the report deliberately does not carry, read here for the applying alone.
        if (outcome.DamageStruckAircraft && (hitBody as AircraftBody)?.Rig is { } struckRig)
        {
            struckRig.TakeCollisionHit(outcome.ArmorDamage, outcome.HealthDamage, contact.Impact, PlayerIndex);
            // Both parties go collision-free, so neither re-resolves the overlap they are still in.
            _lifecycle.ArmCollisionGrace();
            struckRig.ArmCollisionGrace();
        }

        // The block-5 kick. A positive severity on a human pilot's own airframe is the whole gate:
        // 0x48d3aa's second guard is the fd developer switch, which no gameplay event sets.
        if (outcome.ShakeMagnitude > 0f)
            Shake?.ContactHit(outcome.ShakeMagnitude);
        // The pad takes the larger of the damage pair, the quantity the original's contact path
        // hands its own effect. The 50.5 edge picks the heavy effect over the light one.
        if (IsHumanPiloted)
            _rumble.Play(PadRumble.Contact(outcome.ArmorDamage, outcome.HealthDamage));
        if (outcome.DamageFlashText is { } flash)
            _pilotHud.Flash(flash);
        _model.Position += outcome.PushOut;
    }

    // One gun round landed on this pilot. It always counts toward the block's interval, and the
    // shield decides which of the two cues answers it: while the shield is armed the round is
    // absorbed and the pass cue rings, otherwise the ricochet does and the damage goes on to be
    // spent. The draw stream is built here rather than at Setup so a pilot nobody shoots at
    // consumes none of it. Answers whether the round was absorbed.
    private bool OnCannonHit()
    {
        _canopyRng ??= Rng.NewSystemRandom(Rng.Weapons);
        _warningShots?.RegisterHit();
        // ⚠ A Dogfight pane carries no shield, which is the original's own networked-session rule:
        // one that ate the other pilot's gunfire would decide the fight. Co-op keeps it on every
        // human pane, the AI's warning shots being what it exists for. docs/org/weaponFire.md.
        if (Match == null && _warningShots is { Absorbs: true })
        {
            string? pass = Audio?.OnWarningShot();
            // The breadcrumb the cue otherwise leaves only in the speakers: which pilot, how full
            // the shield is, and which of the three pass samples drew.
            Log.Info("weapons", $"warning shot P{PlayerIndex + 1} intensity={_warningShots.Intensity:0.00} snd={pass ?? "none"}");
            return true;
        }
        if (Audio?.OnBulletHit() is { } variant)
            Log.Info("weapons", $"bullet hit P{PlayerIndex + 1} snd={variant}");
        return false;
    }

    // The incoming-fire block's tick (FUN_004b1340), which the original runs for the PLAYER's own
    // aeroplane alone (0x0048985f). The shield charges on an interval that closed with a hit and
    // drains on a quiet one; the same closed interval may open one of the five canopy hole defs,
    // which draws the hole and sounds the glass. The health fraction is the whole-vehicle pool.
    private void TickIncomingFire(float dt)
    {
        if (!IsHumanPiloted || Crashed)
            return;
        int hits = _warningShots?.Tick(dt) ?? 0;
        if (hits == 0 || _canopyHoles == null || _canopyRng == null || Damage == null)
            return;
        if (_canopyHoles.TryOpenHole(Damage.SummaryHealthFraction, _canopyRng) is not { } hole)
            return;
        string? variant = Audio?.OnWindowHit();
        int drew = OpenCanopyHole(hole);
        // The breadcrumb the cue otherwise leaves only in the speakers and on the glass. It names
        // which pilot, which hole of the five and which of the three glass samples drew. It also
        // gives how many anim instances the def left running.
        Log.Info("weapons", $"canopy hole P{PlayerIndex + 1} bullet{hole} closed={_canopyHoles.ClosedCount} snd={variant ?? "none"} drew={drew}");
    }

    // The rig-local `camera1` follows the eye. The exterior overlay's AT_NODE pose then lands in
    // front of the view this frame, not at the world origin. Written every frame the pilot's own
    // camera holds the view: the lab's free camera and an AI rig leave it where it stands.
    private void SyncViewCameraProxy()
    {
        if (_viewCameraProxy is not { } proxy || CameraOwned || _cam == null)
            return;
        proxy.GlobalTransform = _cam.EyePose;
    }

    // The survivable scrape's authored per-surface reaction, selected exactly as
    // Crash selects its own, indexing TouchdownDefs by surface id. A
    // null def plays nothing. One reaction per GrazeReactionInterval, not per frame.
    // `graze.siteAtContact` (default true) picks contact point over aircraft.
    // ⚠ Effects keep ONE live instance per def session-wide, as every PlayEffectAt caller does.
    private void GrazeReaction(Vector3 impact, string hitName, Node? hitBody)
    {
        if (_grazeReactionCooldown > 0f)
            return;
        _grazeReactionCooldown = GrazeReactionInterval;
        int? surfaceId = SurfaceIdOf(hitBody);
        string? effect = TouchdownDefs?.DefForSurfaceId(surfaceId);
        var site = Config.GetBool("graze.siteAtContact", true) ? impact : _model.Position;
        if (effect != null)
        {
            GrazeEffectSink?.Invoke(effect, site);
            // The bark belongs to the def, exactly as the crash boom does: touchdown_water Sounds
            // snd_exp_water_b, the other two snd_exp_ground_b. No def, no sound.
            Audio?.OnGraze(effect == EffectCatalogue.TouchdownDefPrefix + "water");
        }
        string surface = surfaceId is { } sid
            ? $"{sid}/{SurfaceRegistry.NameForId(sid) ?? "?"}"
            : "none";
        Log.Info("flight", $"graze reaction effect={effect ?? "-"} surface={surface} into={hitName} contact=({impact.X:0},{impact.Y:0},{impact.Z:0}) site=({site.X:0},{site.Y:0},{site.Z:0}) rendered={(GrazeEffectSink != null ? 1 : 0)}");
    }

    // Sweeps each airframe box along this frame's motion against every solid collider,
    // world plus other aircraft's bodies (a mid-air resolves through the contact rules like any
    // other hit), own body excluded by RID. Fills the earliest hit's report. False when
    // uncollidable or nothing is in the way. `hitBody` is the caller's, not the report's: the
    // struck node stays out of the value the decision side reads.
    private bool SweepAirframe(Vector3 from, Vector3 motion, out ContactReport contact,
        out Node? hitBody)
    {
        contact = default;
        hitBody = null;
        if (Collider == null)
            return false;
        var baseXf = new Transform3D(_model.Attitude, from);
        if (!World.Sweep(Collider.Parts, baseXf, motion, CollisionLayers.WorldAndAircraft,
            Body?.ExcludeSelf, out var report))
            return false;
        hitBody = report.Collider;
        contact = new ContactReport
        {
            Impact = report.Contact,
            Normal = report.Normal,
            Part = report.Part,
            ColliderName = report.ColliderName,
            StopFraction = report.StopFraction,
            StruckIsAircraft = (report.Collider as AircraftBody)?.Rig != null,
        };
        return true;
    }

    // The original's contact test (FUN_0048d7f0): each of the def's collision probes is carried
    // from the pose the sweep runs from to this frame's pose and the earliest strike along the
    // motion wins (docs/formats/vehicle.md "Collision probes": the player defs author six, every
    // AI def resolves basic_airplane's single origin probe, which is what lets the CM13 racers
    // thread the 9.7 m dbase arch on dzpath2). A def with no probe list reports nothing, and the
    // centre ray behind it stands.
    private bool SweepProbes(Vector3 from, Vector3 motion, out ContactReport contact, out Node? hitBody)
    {
        contact = default;
        hitBody = null;
        if (Stats?.CollisionProbes is not { Count: > 0 } probes)
            return false;
        float len = motion.Length();
        if (len < 1e-4f)
            return false;
        float best = float.MaxValue;
        foreach (var probe in probes)
        {
            var offset = _model.Attitude * probe;
            var start = from + offset;
            if (!World.Ray(start, start + motion, CollisionLayers.WorldAndAircraft, Body?.ExcludeSelf, out var report))
                continue;
            float fraction = start.DistanceTo(report.Position) / len;
            if (fraction >= best)
                continue;
            best = fraction;
            hitBody = report.Collider;
            contact = new ContactReport
            {
                Impact = report.Position,
                Normal = report.Normal.LengthSquared() > 1e-6f ? report.Normal : -motion / len,
                Part = Mathf.Abs(probe.X) > 1f ? "wing" : "center",
                ColliderName = report.Collider is { } body ? $"{body.GetParent()?.Name}/{body.Name}" : "",
                StopFraction = fraction,
                StruckIsAircraft = (report.Collider as AircraftBody)?.Rig != null,
            };
        }
        return best < float.MaxValue;
    }

    // The anti-tunnelling backstop, filling the same report off the centre ray alone: no box
    // reached the obstacle but the swept centre did. It has no struck box and no surface normal
    // of its own, so the part reads `center` and the normal is the reversed motion, which makes
    // the contact head-on; the stop fraction stays 1 because a ray reports where it hit, not
    // where the airframe would have come to rest.
    private bool CenterRayContact(Vector3 from, Vector3 to, Vector3 motion, float motionLen,
        out ContactReport contact, out Node? hitBody)
    {
        contact = default;
        if (!HitWorld(from, to, out var point, out var hitName, out hitBody))
            return false;
        contact = new ContactReport
        {
            Impact = point,
            Normal = motionLen > 1e-4f ? -motion / motionLen : Vector3.Up,
            Part = "center",
            ColliderName = hitName,
            StopFraction = 1f,
            StruckIsAircraft = (hitBody as AircraftBody)?.Rig != null,
        };
        return true;
    }

    // Debug view of the collision test: the swept center ray with a cross at
    // its tip, plus the airframe hulls drawn at where this frame's sweep stopped.
    // Freezes red at the impact pose while crashed.
    private void DrawProbe(Vector3 from, Vector3 end, Vector3 shapePos, bool hit)
    {
        var color = hit ? new Color(1f, 0.15f, 0.1f) : new Color(0.2f, 1f, 0.3f);
        _probe!.ClearSurfaces();
        _probe.SurfaceBegin(Mesh.PrimitiveType.Lines);
        _probe.SurfaceSetColor(color);
        _probe.SurfaceAddVertex(from);
        _probe.SurfaceAddVertex(end);
        const float s = 1.5f;
        foreach (var axis in stackalloc[] { Vector3.Right, Vector3.Up, Vector3.Back })
        {
            _probe.SurfaceAddVertex(end - axis * s);
            _probe.SurfaceAddVertex(end + axis * s);
        }
        if (Collider != null)
        {
            var baseXf = new Transform3D(_model.Attitude, shapePos);
            foreach (var p in Collider.Parts)
            {
                var xf = baseXf * p.Local;
                foreach (var (a, b) in p.Hull.Edges)
                {
                    _probe.SurfaceAddVertex(xf * p.Hull.Points[a]);
                    _probe.SurfaceAddVertex(xf * p.Hull.Points[b]);
                }
            }
        }
        _probe.SurfaceEnd();
    }

    /// <summary>Places the camera at its settled pose immediately (spawn, respawn, the weapon
    /// lab's re-park), there is nothing to interpolate from at those moments.</summary>
    // Silent no-op while the lab's free camera owns the view: a respawn or a lab re-park must
    // not yank the eye back onto the plane the tester just flew away from.
    private void SnapCamera()
    {
        // The death camera ends where the aeroplane it was framing does. Cleared even under the
        // lab's free camera, so a re-park cannot leave a dead pilot's framing armed for the next
        // life.
        _deathCamera = false;
        if (!CameraOwned)
        {
            _cam?.Snap(_model.Position, _model.Attitude, _model.Speed, _renderPose);
        }
    }

    // The weapon lab's orbit camera, mixed from this player's keyboard and pads. Read here rather
    // than in CameraController so the camera never learns about pad devices, window focus or the
    // stick response curve.
    // ⚠ The swing is not a named-action read, since these are lab controls. Its key pairs SUM
    // rather than OR, so W and Up swing at double rate. The dolly is the player's zoom pair.
    private (float Yaw, float Pitch, float Zoom) OrbitInput()
    {
        float padYaw = StickCurve(PadAxis(JoyAxis.LeftX));
        float padPitch = -StickCurve(PadAxis(JoyAxis.LeftY)); // stick up = camera up
        float padZoom = PadAxis(JoyAxis.TriggerRight)
                      - PadAxis(JoyAxis.TriggerLeft);         // RT out, LT in
        return (KeyAxis(Key.D, Key.A) + KeyAxis(Key.Right, Key.Left) + padYaw,
                KeyAxis(Key.W, Key.S) + KeyAxis(Key.Up, Key.Down) + padPitch,
                _keyActions.Axis(InputAction.ZoomOut, InputAction.ZoomIn) + padZoom); // RT out, LT in
    }

    // The pad look-around stick: this player's right stick, curved the same way OrbitInput's is.
    // Read here, not in CameraController, for the same reason `OrbitInput` is: the camera never
    // learns about pad devices or the stick response curve. Both components read exactly 0 inside
    // the deadzone, which is what tells the caller the look-around is inactive. ONE reader for
    // both views, so the chase swing and the first-person head cannot take different sticks.
    private (float X, float Y) PadLookInput()
    {
        float x = StickCurve(_padActions.Axis(InputAction.LookAimRight, InputAction.LookAimLeft));
        float y = StickCurve(_padActions.Axis(InputAction.LookAimDown, InputAction.LookAimUp));
        // A live stick beats the scripted pin, the rule a held numpad key follows against --view=.
        // PinnedLook's Y is +up, this pair's is the stick's own +down, so it is negated back here
        // and every caller keeps reading one convention.
        return x != 0f || y != 0f ? (x, y) : (PinnedLook.X, -PinnedLook.Y);
    }

    // One frame of head-look input, in HeadLook's own conventions. Read here for the same reason
    // the look-around stick is: the camera never learns about pads, mice or key layouts. The
    // chase camera clears `includePad`, since the stick places that view itself (PadLook).
    private HeadLookInput HeadLookRead(bool includePad = true)
    {
        var (snapX, snapY) = SnapLookInput();
        var (freeRight, freeUp) = FreeLookRead();
        var (lookX, lookY) = includePad ? PadLookInput() : (0f, 0f);
        // The pad aims absolutely in first person, matching the chase view's own stick; the mouse
        // stays on the decoded relative path. `lookY` is the stick's +down, HeadLook wants +up. The
        // held control rides along so a still mouse holds the look instead of reading as idle.
        return new HeadLookInput(snapX, snapY, freeRight, freeUp, _actions.Held(InputAction.LookCenter),
            lookX, -lookY, FreeLookHeld(),
            _actions.Held(InputAction.SnapLookMode), _actions.Held(InputAction.SmoothLookMode),
            _actions.Held(InputAction.TrackTarget));
    }

    // HeadLook's TargetOffset: where this pilot's selection sits in the plane's own frame, the one
    // thing the padlock state reads. Null with nothing selected, which is that state's no-target
    // frame. Taken off the SIM pose, the pose the original's own padlock differences against.
    private Vector3? PadlockOffset()
    {
        if (Targeting?.Current is not { } target)
        {
            return null;
        }

        return _model.Attitude.Inverse() * (target.Position - _model.Position);
    }

    // C22's IdleAim delegate: HeadLook.Step calls this only on a frame with no look input at all.
    // Gated on ViewMode (Cockpit only, the original's option byte AND mode ≠ 7) and the options
    // toggle here, mirroring the original's engine option byte. The body rates are already in the
    // plane's own frame, the frame the original rotates its world rates into before the lead.
    private (float Elevation, float Azimuth)? AutoheadTarget()
    {
        if (_cam == null || _cam.ViewMode != PilotViewMode.Cockpit
            || !(AutoHeadTurn ?? Config.GetBool("headLook.autohead", false)))
        {
            return null;
        }
        return HeadLook.AutoheadTarget(_model.BodyRates, _model.Stats.AutoheadTurnTime,
            _model.Stats.AutoheadTurnMax, _model.Stats.AutoheadTurnMinPitch);
    }

    // The snap cluster as a composed direction, the original's own numpad bindings: Kp8 Look Up,
    // Kp4/Kp6 the flanks, Kp2 Look Back, the corners the four diagonals. Read in every view the
    // head places, first person and the chase camera alike, since they share the one head.
    private (float X, float Y) SnapLookInput()
    {
        float x = _actions.Axis(InputAction.LookRight, InputAction.LookLeft);
        float y = _actions.Axis(InputAction.LookUp, InputAction.LookDown);
        if (x != 0f || y != 0f || PinnedView < 1 || PinnedView > 9)
        {
            return (x, y);
        }
        // A scripted --view=<digit> holds that direction for the run, off the pad's own layout:
        // the digit's column is the left/right component and its row the up/down one. Live keys
        // beat the pin, the rule PadLookInput follows against PinnedLook.
        return (((PinnedView - 1) % 3) - 1, ((PinnedView - 1) / 3) - 1);
    }

    // Free-look direction: the mouse while the FreeLook action is held (the middle button by
    // default). Only the DIRECTION is read, at the decoded fixed pan rate. The pad
    // deliberately does NOT feed this path: its stick aims absolutely (HeadLook.PadAimTargets),
    // which is what makes the cockpit read like the chase view. A mouse has no absolute position
    // to map, so it keeps the original's relative law.
    private (float Right, float Up) FreeLookRead()
    {
        var mouse = MouseLookDelta();
        return (mouse.X, -mouse.Y);                        // screen Y grows downward
    }

    // How far the mouse moved since the last read, or zero unless free-look has the mouse. Polled
    // rather than event-driven, like every other control here; the previous position is refreshed on
    // every call, so an idle mouse reads exactly zero. A still mouse under a held control is NOT the
    // released case: HeadLookInput.Looking is what tells the head those apart.
    private Vector2 MouseLookDelta()
    {
        var pos = (Vector2)DisplayServer.MouseGetPosition();
        var absolute = pos - _mouseLookPrev;
        // Refreshed whether or not it is the reading used. Otherwise the frame capture ends on
        // would hand head-look the whole span the pointer stood still for as one delta.
        _mouseLookPrev = pos;
        var delta = _mouse.Holding ? _mouse.TakeLook() : absolute;
        return FreeLookHeld() && delta.LengthSquared() > 1f ? delta : Vector2.Zero;
    }

    // The two supplied sides of a suite's tick as ONE reading, for the seat's whole-seat reader.
    // Each side answers for its own devices, so the union is what a live poll of both would give.
    private sealed class BothSides : IDeviceState
    {
        private readonly IDeviceState _keyboard;
        private readonly IDeviceState _pad;

        public BothSides(IDeviceState keyboard, IDeviceState pad)
        {
            _keyboard = keyboard;
            _pad = pad;
        }

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            _keyboard.IsKeyDown(device, keyCode) || _pad.IsKeyDown(device, keyCode);

        public bool IsButtonDown(DeviceId device, int button) =>
            _keyboard.IsButtonDown(device, button) || _pad.IsButtonDown(device, button);

        public bool IsMouseButtonDown(DeviceId device, int button) =>
            _keyboard.IsMouseButtonDown(device, button) || _pad.IsMouseButtonDown(device, button);

        public float AxisValue(DeviceId device, int axis)
        {
            float key = _keyboard.AxisValue(device, axis);
            float pad = _pad.AxisValue(device, axis);
            return Mathf.Abs(pad) > Mathf.Abs(key) ? pad : key;
        }

        public HatDirection HatState(DeviceId device, int hat)
        {
            var hats = _keyboard.HatState(device, hat);
            return hats == HatDirection.None ? _pad.HatState(device, hat) : hats;
        }
    }

    // This plane's half of a contact the resolver is deciding: the engine effects it has to
    // interleave with, plus the struck Node the report deliberately does not carry. One instance
    // per contact, so nothing about a contact outlives the call that decided it.
    private sealed class ContactEffects : IContactEffects
    {
        private readonly FlightController _rig;
        private readonly Node? _struck;
        private readonly ContactReport _contact;
        private readonly Vector3 _from;
        private readonly Vector3 _motion;

        public ContactEffects(FlightController rig, Node? struck, in ContactReport contact,
            Vector3 from, Vector3 motion)
        {
            _rig = rig;
            _struck = struck;
            _contact = contact;
            _from = from;
            _motion = motion;
        }

        public bool ShatterStruck(float healthDamage) =>
            _rig.CollideDamageSink != null && _rig.CollideDamageSink(_struck, healthDamage);

        public void PlayGrazeReaction() =>
            _rig.GrazeReaction(_contact.Impact, _contact.ColliderName, _struck);

        public PlaneDamage.PartState? SpendDamage(string zone, float healthDamage, float armorDamage)
        {
            var state = _rig.Damage!.Apply(zone, healthDamage, armorDamage);
            string struckPart = state?.Def.Name ?? zone;
            if (state != null)
            {
                _rig.Visuals?.OnPartDamage(struckPart, state.HealthFraction);
                _rig._pilotHud.OnPartDamage(struckPart); // damage dial: hit zone blinks 5 s
            }

            // the def-level stages run off the hull pool even when the graze went zone-less
            _rig.Visuals?.OnHullDamage(_rig.Damage.SummaryHealthFraction);
            return state;
        }

        // Placement and the decoded impulse, on the plant whose fields they write. An airframe
        // already crashed is off the player path, so its gate rides the impulse's argument.
        public ContactResponse ApplyResponse()
        {
            _rig._model.Collide(_from, _motion, _contact.StopFraction, _contact.Impact, _contact.Normal,
                _rig.IsHumanPiloted && !_rig.Crashed);
            return new ContactResponse(new Transform3D(_rig._model.Attitude, _rig._model.Position));
        }
    }
}
