using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3.Anim;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// The animation engine: binds an <see cref="AnimProgram"/> to a built world and executes it.
/// Bootstrap runs its passes (mission setup, anchored RESET_STATEs, ON_STARTUP, the mission's
/// startanims, a safety net hiding still-visible `destroyed` subtrees), then events play through
/// the dispatch table. An event kind with no case is counted and reported, never fatal.
/// Passes 2 and 3 run rather than posing at the end state, so a hangar door swings open over its
/// authored run time. Module notes: docs/architecture.md.
/// ⚠ "Inactive" means hidden AND non-collidable. An invisible solid zeppelin is worse than a
/// visible one that should not be there.
/// ⚠ Resolve a definition to a node by its original gamez name (the <c>cs_name</c> meta), never by
/// <c>Node.Name</c>; Godot mangles duplicate sibling names.
/// </summary>
public sealed partial class AnimRuntime : Node, ISequenceHost
{
    /// <summary>The reader's <c>ANIMATION_LOD HIGH</c>, see <see cref="QualityLod"/>.</summary>
    public const int HighLod = 2;

    /// <summary>Original-name metadata key SceneBuilder stamps on every built Node3D.
    /// ⚠ Every meta key is a <see cref="StringName"/>, never a <c>const string</c>. A string handed
    /// to HasMeta/GetMeta converts to a fresh finalizable StringName per call, and these keys are
    /// read on the frame path (docs/verification.md PERF-20).</summary>
    public static readonly StringName NameMeta = "cs_name";

    /// <summary>Flat gamez node-index metadata key SceneBuilder stamps on every built
    /// Node3D, the exact binding compiled definitions reference (see
    /// <see cref="AnimDefinition.NodeRefs"/>).</summary>
    public static readonly StringName IndexMeta = "cs_index";

    /// <summary>Pool-slot metadata key the effect-template pool stamps on each slot container
    /// (<c>WorldEffectsFactory.BuildWorldEffectsRuntime</c>): every staged template copy lives
    /// under exactly one of them, and the slot is what keeps one call's copy of a template apart
    /// from another call's (see <see cref="TemplateStage{TNode}.Pooled"/>). Never on a template node itself,
    /// so name resolution is blind to it.</summary>
    public static readonly StringName PoolSlotMeta = "cs_pool_slot";

    /// <summary>Event kinds <c>Dispatch</c> acts on. Keep in step with its cases: a kind absent
    /// here is one the runtime counts as unhandled and does nothing for, which is what the node
    /// lab's coverage column reports.</summary>
    public static readonly IReadOnlyCollection<string> HandledEventKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "ObjectActiveState", "ObjectTranslateState", "ObjectRotateState", "ObjectScaleState",
        "ObjectMotionFromTo", "ObjectOpacityState", "ObjectOpacityFromTo", "ObjectMotion",
        "ObjectMotionSiScript", AnimDefinition.AllNamesKind, "Loop", "If", "Elseif", "Else", "Endif", "CallSequence",
        "StopSequence", "CallAnimation", "StopAnimation", "InvalidateAnimation", "ResetAnimation",
        "PufferState",
        "LightState", "LightAnimation", "SoundNode", "Sound", "ObjectAddChild", "ObjectDeleteChild",
        "Callback", "FogState",
    };

    /// <summary>Kinds with a handler that covers only part of what the event does, reported
    /// apart from the unhandled ones, since "acted on" and "acted on fully" are different answers.
    /// The two child events take their sound-emitter and node-reparent forms and count the rest as
    /// unhandled, and <c>Callback</c> acts on the two vehicle-death codes and counts every other
    /// one.</summary>
    public static readonly IReadOnlyCollection<string> PartialEventKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "ObjectAddChild", "ObjectDeleteChild", "Callback",
    };

    /// <summary>Refuse the <c>ANIMATION_ROOT_NAME</c> anchor lift, however few matches it finds.
    /// ⚠ Set this only from a caller that built part of the world. The resolver's
    /// <c>MaxRootLift</c> cap assumes a whole-world node population, so a single-subtree stage
    /// drops under it and unrelated definitions anchor onto whatever generic child the subtree
    /// owns. Suppressed lifts are counted and reported, never silently dropped.</summary>
    public bool SuppressRootLift;

    /// <summary>Refuses a COLLISION's damage on a struck destructible while the contact itself
    /// stands. Wired to <c>ZeppelinRuntime.GateCollisionDamage</c> so a rammed gasbag takes
    /// nothing; null gates nothing. It sits on the sink every rig shares, so AI aircraft need no
    /// second wiring.
    /// ⚠ The original needs no equivalent: it delivers a ram as a <c>wep_24</c> weapon hit, so its
    /// one gasbag gate catches a ram for free.</summary>
    public Func<DestructibleRegistry.Instance, bool>? CollideDamageGate;

    /// <summary>Every player's position, for the EXECUTION_BY_RANGE proximity gate and
    /// every <c>PLAYER_RANGE</c> condition, both measure from the nearest human, not
    /// one camera. In flight this is the aircraft themselves, the chase camera trails far
    /// enough behind the plane to eat most of a 50 m radius. Null → both fall back to
    /// <see cref="PlayerPosition"/> alone, keeping a runtime built without this seam (a lab, a
    /// test) on the single-camera behaviour.</summary>
    public Func<IReadOnlyList<Vector3>>? PlayerPositions;

    // ---- observability (the animation debugger's timeline; null = zero cost in the game) ----
    /// <summary>Raised as each sequence event fires at runtime. Null by default → zero cost in the
    /// game; the debugger sets it to feed its timeline's fired marks straight from the runtime,
    /// rather than parsing --debug-anim log text. The interpreter reads it through the get-only
    /// <see cref="ISequenceHost.OnEventDispatched"/> seam member.</summary>
    public Action<EventDispatch>? OnEventDispatched;

    /// <summary>Raised when a definition becomes a live instance and when that instance finishes,
    /// each carrying the (def, anchor) identity. Null by default → zero cost in the game; the
    /// debugger uses the pair to place a CALL_ANIMATION child def's timeline lane group at the
    /// playhead time it began, and to drop it when it ends.</summary>
    public Action<AnimDefinition, Node3D?>? OnInstanceStarted;

    public Action<AnimDefinition, Node3D?>? OnInstanceFinished;

    /// <summary>The collision mask a <c>do_intersections</c> body sweeps against; 0, the default,
    /// means this session does no ground contact (the labs, the goldens, the headless suites).
    /// ⚠ Set it to <c>CollisionLayers.World</c> only; debris must not be solid to aircraft. Handed
    /// in rather than read from <c>CollisionLayers</c> because that constant lives in the flight
    /// layer and the session is where the two meet.</summary>
    public uint ContactMask;

    /// <summary>Whether a struck collider is water, the only distinction a <c>BOUNCE_SEQUENCE</c>
    /// draws (docs/org/objectMotion.md). Null, the default, answers "not water" and every
    /// contact takes the <c>default</c> branch.
    /// ⚠ Bind this to <c>ProjectilePool.SurfaceIsWater</c>, never to a second surface read; a
    /// round, a wingtip graze and a landing piece must not disagree about what they hit.</summary>
    public Func<GodotObject?, bool>? SurfaceIsWater;

    // ---- live execution ----
    /// <summary>True when something else owns the clock (the animation debugger, which feeds
    /// <see cref="Advance"/> in fixed 1/60 s steps): <see cref="_Process"/> stops advancing.
    /// ⚠ Use this flag, not <c>SetProcess(false)</c>. Godot re-enables processing at READY for any
    /// node overriding <c>_Process</c>, and this node enters the tree after the lab is assembled,
    /// so an earlier SetProcess call is silently undone and the world runs at double speed.</summary>
    public bool ManualAdvance;

    /// <summary>Named CALL_ANIMATION callees whose placed root levels to world axes instead of the
    /// inherited parent rotation (<see cref="TemplateStage{TNode}.PlaceOn"/>), keyed by
    /// <c>AnimName ?? Name</c>. Set once by the crash rig, for the crash def's surface-hugging
    /// sub-effects and the parachute. ⚠ Never make it blanket: leveling `fly_trail1-5` strips the
    /// co-rotation their debris scatter is authored in. The lists are
    /// <c>EffectCatalogue.CrashSurfaceLevelAnimNames</c> + <c>BailoutAnimNames</c>, docs/architecture.md.</summary>
    public HashSet<string>? LevelPlacedTemplateNames;

    /// <summary>Named CALL_ANIMATION callees whose relocated copy takes the basis a
    /// <see cref="PlayEffectAt"/> hands in, instead of the axis it stands on, keyed by
    /// <c>AnimName ?? Name</c>. Set once by the world-effects rig, from
    /// <c>EffectCatalogue.ImpactUpperRingAnimNames</c>. ⚠ Never make it blanket. A callee outside
    /// this set is authored against a fixed axis, and re-basing it moves choreography.</summary>
    public HashSet<string>? OrientedCallAnimNames;

    /// <summary>Callers whose unresolvable CALL_ANIMATION target is worth one warning each, keyed by
    /// <c>AnimName ?? Name</c>, and the airframe that warning names. Injected like
    /// <see cref="LevelPlacedTemplateNames"/> above: the per-plane crash rig sets both to the damage
    /// stages and its own plane, and every other runtime leaves them null and stays quiet. The
    /// shipped case is the Bloodhawk's elevators (docs/org/vehicleDamage.md); a stage that misses
    /// its anchor still plays, at the airframe root, which no screenshot distinguishes.</summary>
    public HashSet<string>? AnchorWarnAnimNames;

    /// <summary>The airframe <see cref="AnchorWarnAnimNames"/>'s warnings name.</summary>
    public string? AnchorWarnLabel;

    /// <summary>Hands a named effect to the world-effects runtime instead of starting it locally,
    /// passing the call-site world point, the resolved call-site node (the callee's INPUT_NODE) and
    /// whether the effect rides that node (true for every call that resolved a site, so a carried
    /// site's death effects move with the hull, docs/org/sequences.md). Returns true when it took the
    /// effect, so the local Start is skipped. Set on the WORLD runtime, whose puffer factory is gone
    /// after the build; null everywhere else, where CALL_ANIMATION starts the callee locally.</summary>
    public Func<string, Vector3, Node3D?, bool, bool>? ExternalEffect;

    /// <summary>Stops a named effect on the external runtime <see cref="ExternalEffect"/> routes to
    ///, the reverse channel, for undoing a routed effect the data has no stop event for:
    /// <see cref="ResetDestructible"/> heals an object whose damage-stage sputter loops for as long
    /// as its host stays active, so the reset itself must end it.</summary>
    public Action<string>? ExternalEffectStop;

    /// <summary>Washes the picture from one RGBA to another, driven by <c>FBFX_COLOR_FROM_TO</c>.
    /// The arguments are <c>(from, to, run_time, origin, radius²)</c>; the radius is metres SQUARED,
    /// the compiled <c>PLAYER_RANGE</c> convention, and 0 for a def that gates on nothing.
    /// ⚠ Keep this a sink, never an overlay node owned here. The overlay is screen-space and
    /// session-scoped, and this runtime is instanced per effect pool and per player crash rig.
    /// Which panes the wash reaches is the sink's decision, not this runtime's.</summary>
    public Action<Color, Color, float, Vector3, float>? ScreenFlash;

    /// <summary>The mission-script host a <c>CALLBACK</c> code is offered to first, with the
    /// raising definition's animation name and its ROOT node name: the cutscene vocabulary belongs
    /// to the session, and the root name is how the airframe swap reaches the vehicle its
    /// definition belongs to. Returning false leaves the code to the two vehicle-death seams below
    /// and to the census, which is what every runtime with no host wired reports.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public Func<int, string?, string?, bool>? CallbackHost;

    /// <summary>Told the name of every definition a mission trigger starts, before it starts, so
    /// the session can book the episode that definition raises to the definition itself rather
    /// than to whichever callee raised its first code. This is the original's own trigger slot,
    /// which holds the started instance for as long as it runs, and it is the SAME slot on every
    /// path a trigger takes: an approach row, the objective script's <c>WAKE_ANIM</c>, the ladder
    /// switch. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public Action<string>? MissionTriggerOwner;

    /// <summary>The rate one cutscene episode's definitions run at while the player holds a key
    /// through a scene that offers no skip, or null on a runtime nobody fast-forwards (every one
    /// but the world's). Set by <c>CutsceneController</c>, which owns both the scope and the key.
    /// ⚠ It multiplies the DEFINITION's dt here, never the session's clock: the world and the
    /// flight models around the episode keep real time.</summary>
    public CutsceneFastForward? FastForward;

    /// <summary>Animation names whose <c>EXECUTION_BY_RANGE</c> is an ARMING gate, not a LOD one:
    /// a <c>CALL_ANIMATION</c> only arms them and the player reaching the band is what runs them.
    /// Bind the mission's own cutscene definitions; left empty, every call starts its callee at
    /// once, which is what the ambient props and light loops carrying the same key want.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public HashSet<string> RangeGatedCalls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Animation names whose <c>OBJECT_MOTION</c> events this runtime drops, so it never
    /// fights another writer over the same node transforms. Everything else in the definition runs
    /// normally.
    /// ⚠ A definition held open only by a looping motion ENDS once its motion is dropped. It then
    /// reports EXECUTED rather than RUNNING through <see cref="AnimStateOf"/> (docs/verification.md,
    /// INSTR-74). Track such a definition's slot at the call site, not here.</summary>
    public HashSet<string> SuppressedMotionAnims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a <c>FOG_STATE</c> event's inline fog goes: the session's weather rig, which
    /// owns the fog globals. Null counts the event. ⚠ Raised under a RESET_STATE as well as in a
    /// sequence: the original's handler (dispatch slot 28) writes the fog record from either
    /// walker, and the one shipped use sits in a reset block (docs/formats/anim-definitions.md).</summary>
    public Action<FogStateChange>? FogStateSink;

    /// <summary>Raised from <see cref="DamageAt"/> when a live kill takes a destructible's own
    /// <c>healthy</c>-role node down, carrying that node's authored name (the submarine's
    /// <c>subhealthy</c>). A fixed installation's death has no other signal, unlike a zeppelin's
    /// own destroyed flag; <c>GameSession</c> feeds this into <c>AiGeneratorRuntime.NotifyHostDied</c>
    /// beside <c>ZeppelinRuntime.ZeppelinKilled</c>. Null while a def authors no healthy-role node
    /// of its own.</summary>
    public Action<string>? DestructibleKilled;

    /// <summary>Raised when a spend moves a destructible to a new damage stage or kills it, after
    /// the stage and the death have run. A network host sends these, and the chip hits between
    /// stages change nothing another machine draws.</summary>
    public Action<DestructibleRegistry.Instance>? DestructibleDamaged;

    /// <summary>While set, <see cref="DamageAt"/> still reports a hit as landed but spends nothing.
    /// A network guest's world runs this way, and its pools move only through
    /// <see cref="ApplyReplicatedHealth"/>.</summary>
    public bool DamageReplicated;

    /// <summary>The world velocity a <c>Callback 16</c> hands the running instance, which is how a
    /// wreck inherits the aircraft's motion (docs/org/vehicleDamage.md). Supplied by the rig,
    /// because only the rig knows which vehicle is dying and how fast; null leaves the code
    /// counted and <see cref="InheritedWorldVelocity"/> untouched. ⚠ Return the vehicle's whole
    /// velocity. The original scales it nowhere on this path, so a fraction here is an invention.
    /// </summary>
    public Func<Vector3>? WreckVelocity;

    /// <summary>What a <c>Callback 15</c> stops: the damage-stage anims and start_anims, which is
    /// where an injure-ladder smoke trail ends (docs/org/vehicleDamage.md). Bind it to the rig's
    /// <c>DamageVisuals.DamageEffectStop</c>; null leaves the code counted and stops nothing.</summary>
    public Action? StopDamageStages;

    /// <summary>The other half of <c>Callback 15</c>: the dying vehicle stops flying ITSELF,
    /// because the destroy def takes the hull over from this event on. Until it fires, a dead
    /// aircraft is still stepped by its own flight model, which is the burning fall the reference
    /// recordings show before the parachute (docs/org/vehicleDamage.md). Bind it to the rig; null
    /// leaves the hull on the flight model and the two systems fly it at once.</summary>
    public Action? StopWreckFlying;

    /// <summary>What a <c>Callback 3</c> does: the pilot's SELECTED view goes back to the chase
    /// camera and the head-look angles are zeroed, which is how the original leaves first person as
    /// the pilot's own aeroplane comes apart (docs/formats/anim-definitions/cutscenes.md). Bound by
    /// the rig, because only the rig knows whose camera this is; null leaves the code counted and
    /// the view alone. ⚠ Only the human player's destroy def authors this code, and only a human
    /// rig ever plays that def, so an AI death cannot reach a person's camera through it.</summary>
    public Action? ResetPilotView;

    /// <summary>This runtime does not own audio, its SOUND / SOUND_NODE events are no-ops, not
    /// late-failure reports. Set on the world-effects runtime: it renders an effect def's
    /// puffers, but the same effect's impact/death SOUND is already played by the projectile pool
    /// or the world runtime, so playing it here too would double it, and with no audio
    /// session it would only spam "silent for the session" warnings.</summary>
    public bool SoundHandledElsewhere;

    // ---- SOUND_NODE (+ the sound half of OBJECT_ADD_CHILD) ----
    /// <summary>The world's ambient 3D emitters. Null in a muted or soundless session, in which
    /// case SOUND_NODE is tracked and reported but nothing is built.</summary>
    public WorldSounds? Sounds;

    /// <summary>Collect a per-definition census of how the bind RESOLVED, and log it through
    /// <see cref="ResolutionLines"/>. Set before <see cref="Bind"/> by a caller that built only
    /// part of the world (the <c>--node=</c> stage), where "this def did nothing" is the normal
    /// case and needs to be told apart from a defect. Default false: a full-world session collects
    /// nothing, so this is inert when nobody asks for it. Copied into the resolver, which owns
    /// the census, when <see cref="Bind"/> runs, like the two flags below.</summary>
    internal bool ReportResolution;

    /// <summary>--debug-anim: log every live motion's target and pose once a second, so a
    /// headless run can verify that (say) the train actually drives its loop.</summary>
    internal bool DebugMotions;

    /// <summary>Our answer to the data's <c>ANIMATION_LOD</c> condition, a project quality setting
    /// rather than a fact about the world. Every LOD-gated branch in this install asks for
    /// <c>HIGH</c>, which compiles to 2, so the default passes them all. <c>--anim-lod=N</c> lowers
    /// it for A/B comparison.</summary>
    internal int QualityLod = HighLod;

    /// <summary>Whether a cutscene has the pilots out of flight and is posing their aeroplanes
    /// itself, in which case a range gate reads the last pose they flew rather than the one the
    /// film is putting them through. Set by the cutscene host as it takes and returns flight; a
    /// session with no host leaves it false and reads live, as it always has.</summary>
    internal bool PlayerRangeHeld;

    /// <summary>Where the player is, for a <c>PLAYER_RANGE</c> condition with no
    /// <see cref="PlayerPositions"/> wired (a lab, a unit test). Supplied by the session (the
    /// flown aircraft, or the spectator camera); absent → the viewport camera, and failing
    /// that the world origin. During the bootstrap passes there is no camera yet, which is
    /// harmless: every PLAYER_RANGE definition in this install re-polls from a <c>Loop{-1}</c>,
    /// so a bootstrap-time miss corrects on the next frame.</summary>
    internal Func<Vector3>? PlayerPosition;

    /// <summary>Every pane's camera position, for budgeting <see cref="Lights"/>:
    /// a world light must not fade or lose its slot just because player 1 is far from it. This is
    /// the draw-rule seam (<c>ViewerSet.Positions</c>), not <see cref="PlayerPositions"/> (the
    /// gameplay one), null or empty falls back to <see cref="PlayerPos"/> alone, keeping a
    /// runtime built without a viewer seam (a lab, a test) on today's single-camera behaviour.</summary>
    internal Func<IReadOnlyList<Vector3>>? LightViewerPositions;

    /// <summary>Answers the data's <c>PLAYER_1ST_PERSON</c> condition (id 120): whether any human
    /// pilot is flying one of the two first-person views (Cockpit/Nose). Supplied by the session
    /// off the rigs' own view mode; null, a lab, a test, a bootstrap before any rig exists,
    /// reads false, which is what this condition answered before the modes existed.</summary>
    internal Func<bool>? FirstPersonView;

    /// <summary>Whether <see cref="Bootstrap"/> runs the ambient-playback passes (ON_STARTUP defs
    /// and the mission's startanims). True in every game/viewer/flight session. False gives the
    /// animation debugger a quiet stage: base states and mission setup still apply, but nothing
    /// animates until <see cref="StartAmbient"/>. Set before <see cref="Bind"/>.</summary>
    internal bool AutoStart = true;

    /// <summary>The mission's interp boot script (<c>support\&lt;chapter&gt;\&lt;mission&gt;.gw</c>),
    /// run as bootstrap pass 0. It is what decides which world entities this mission shows,
    /// see <see cref="MissionSetup"/>. Null when the mission ships no script, which is normal.
    /// Set before <see cref="Bind"/>.</summary>
    internal MissionSetup? Setup;

    /// <summary>Resolve every node reference by NAME, leaving the resolver's by-index map empty
    /// (<see cref="IndexWorld"/>). ⚠ Never turn this on for the shared world: name matching
    /// resolves C1's <c>caboose</c> to the real consist and to an unrelated <c>caboose.flt</c>.
    /// The per-player crash runtime must have it on, because its def's node ptrs index a planes.zbd
    /// this build never loads and its subtree mixes two colliding gamez index spaces.</summary>
    internal bool NameResolveFallback;

    /// <summary>Lazily builds, indexes and RESET_STATE-poses a pooled copy of a named library root
    /// (<see cref="GameZ.IsLibraryRoot"/>, docs/formats/gamez.md), returning the copy this exact
    /// call owns; another gets a fresh one until the pool wraps. Null off a library root and on
    /// every runtime staging templates eagerly. The third argument is the authored call event,
    /// null when that call names no site: a staged actor is served to a placing call alone.
    /// ⚠ Drive the copy, never the def's whole <see cref="TemplateStage{TNode}.RootsFor"/>.</summary>
    internal Func<string, Node3D, object?, Node3D?>? ResolveLibraryRoot;

    /// <summary>The world-space velocity an <c>IMPACT_FORCE</c> launch adds, transformed into the
    /// launched node's parent frame. Written only by <c>Callback 16</c>, which is the original's
    /// only single-player path to it, and taken WHOLE: the original applies no fraction anywhere
    /// between the dying object's velocity accessor and the add (docs/org/objectMotion.md).
    /// ⚠ Write it only through <see cref="ArmInheritedVelocity"/>: this vector and the arm below
    /// answer different questions, and the original moves them together.</summary>
    internal Vector3 InheritedWorldVelocity;

    /// <summary>Whether that velocity is armed: the original's <c>animInstance+0x9c</c> bit
    /// <c>0x80</c>, which is what <c>IMPACT_FORCE</c> actually gates on.</summary>
    internal bool InheritedVelocityArmed;

    // ---- PUFFER_STATE ----
    /// <summary>What <see cref="Emitters"/> builds through; null, the default, renders no emitters
    /// at all. ⚠ Valid only during the world build, because an emitter bakes its atlas from the
    /// session's <see cref="TextureArchive"/>. A caller whose archive dies with its build must call
    /// <see cref="EmitterDirector.RetireFactory"/> afterwards, so a later request is reported
    /// instead of faulting on a closed zip handle.</summary>
    internal IEmitterFactory? EmitterFactory;

    /// <summary>Where the world's lights are delivered. Null outside a lit session, in which
    /// case LIGHT_STATE is tracked but never rendered.</summary>
    internal WorldLights? Lights;

    /// <summary>Set by <see cref="ContributeLightsTo"/>: <see cref="Lights"/> belongs to another
    /// runtime, which begins and commits its frame, and this one only submits into it.</summary>
    internal bool LightsCommittedElsewhere;

    // The runtime's dice: RANDOM_WEIGHT verdicts, SOUND_GROUPS one-shot picks, crash-debris
    // scatter. One field rather than scattered GD.Randf() calls so the session's master seed can
    // pin the whole sequence. Any future WeaponHit/crash handler's randomness must route through
    // this same _rng, or a replay stops being identical the day the handler lands.
    internal Random _rng = new();

    private const int MaxStartDepth = 8;

    // How many queued first advances one tick's walk may run before the rest wait for the next
    // walk. A ring of definitions that restart each other would otherwise spin inside one tick,
    // where the original's own start gate refuses the second start outright. Far above the widest
    // shipped call fan-out, so no authored beat ever meets it.
    private const int MaxQueuedStarts = 512;

    // Backstop on a single WAIT_FOR_COMPLETION hold, in seconds, well clear of the longest hold the
    // data authors. "Completes" is our instance lifetime, not the authored one, so a callee held
    // open by something the data cannot predict would wedge the caller's sequence silently. Every
    // trip is counted and named (ReportUnhandled).
    private const float WaitCeilingS = 120f;

    // The magic sequence name a destructible's progressive-damage script carries in both the reader
    // and compiled forms (docs/formats/destructibles.md).
    private const string DamageSequenceName = "DAMAGE_SEQUENCE";

    // The deferred-EXECUTION_BY_RANGE sweep quantises the player position to this cell size and
    // re-checks the deferred list only on a cell crossing (the MapEdgeExtender cadence).
    // ⚠ Keep it well under the smallest authored radius in the install, 50 m: the sweep lags an
    // approach by up to one cell diagonal, and the C3 spiderweb's 0.7 s fade needs the trigger to
    // land close to its authored range at cruise speed.
    private const float RangeCheckCellSize = 8f;

    // The two CALLBACK codes the vehicle-death handler acts on, decoded from LAB_00480710
    // (docs/org/vehicleDamage.md's "What happens to the wreck"). 16 hands the instance the dying
    // vehicle's velocity; 15 ends the damage stages.
    // ⚠ Never add code 0. It is the free/delete arm, no compiled def in the install authors it,
    // and acting on it would delete a live wreck.
    private const int CallbackWreckVelocity = 16;
    private const int CallbackStopStages = 15;

    // The third code a destroy def raises, from the mission-script host's case at 0x0047e0ec. Only
    // the human player's own def authors it, twice, at the head of each of its two destroy arms
    // (docs/formats/anim-definitions/cutscenes.md).
    private const int CallbackResetView = 3;

    // Per-axis magnitude a Callback 16 must exceed to ARM the velocity it hands over (`004ee0e0`).
    private const float InheritedVelocityEpsilon = 0.01f;

    // Smallest magnitude a pose-scale component may reach. ⚠ Never let a pose scale reach exactly
    // 0, however the data authors it: a singular basis makes Godot's physics server spam
    // `det == 0` for any StaticBody3D under the node. 1e-3 of a world object is sub-pixel at
    // gameplay distance, and the defs that shrink away hide the node anyway.
    private const float MinPoseScale = 1e-3f;

    // Name resolution, the index, wildcard matcher, memoized FindAll, and the three-tier scope
    // chain, lives in NameResolver.cs; identity is instance id, since Godot object equality is
    // unreliable inside a dictionary/tuple key across proxy instances of the same native node.
    // Constructed in the constructor below: the pool reaches the resolver ONLY as the ownRootsOf
    // hook (TemplateStage.RootsFor), the slot arithmetic lives on the stage.
    private readonly NameResolver<Node3D> _resolver;

    // The effect-template pool + placement as a module: slot
    // arithmetic, the caller-slot claim, template placement, the copy-identity questions
    // and the reveal/retire/sweep ritual live in TemplateStage.cs; this class supplies the engine
    // and runtime hooks in the constructor and calls through. The flags are sealed at construction.
    private readonly TemplateStage<Node3D> _templateStage;

    private readonly Dictionary<Node3D, Transform3D> _rest = new(); // authored pose per touched node

    private readonly List<AnimInstance> _instances = new();

    // The per-frame snapshot Advance walks. ⚠ An instance's own STOP_ANIMATION removes OTHER
    // instances mid-walk, so an index into the live list goes out of range; a cutscene definition
    // that stops the scene it just called is where that happens.
    private readonly List<AnimInstance> _advancing = new();

    // Instances a CALL_ANIMATION started during the walk, waiting for their first advance at the
    // tail of that same walk, paired with the self-invalidate protection their Start asked for.
    // The original's dispatcher reaches a node appended during its own pass, so a callee's first
    // event lands after the caller's remaining events (docs/org/sequences.md).
    private readonly List<(AnimInstance Inst, bool Protect)> _queuedStarts = new();

    // The destructible whose death is directly dispatching right now (a stack, since deaths nest).
    // Lets a death-triggered CALL_ANIMATION landing on the SAME anchor as the dying instance
    // register on its LocalCallTargets, so a reset can Stop and restore it too. Without that, a
    // reset arriving before the called def's own motions finish leaves its pieces flown.
    private readonly Stack<DestructibleRegistry.Instance> _dyingInstances = new();

    // The instance whose own t=0 burst is directly dispatching, one entry per nested CALL_ANIMATION
    // level, paired with whether this Start opted into self-invalidate protection. It stops a
    // same-name self STOP/INVALIDATE_ANIMATION authored early in that burst from tearing its own
    // still-unwinding instance down and orphaning the calls the burst just scheduled.
    // ⚠ Keep the guard opt-in and scoped to the top-of-stack instance. The ambient world boot needs
    // the tolerant behaviour for its startup anims (docs/formats/anim-definitions.md).
    private readonly Stack<(AnimInstance Inst, bool Protect)> _startingInstances = new();

    // Definitions an INVALIDATE_ANIMATION has latched off. ⚠ Treat the event as a one-shot latch,
    // never a stop, and clear it only from RESET_ANIMATION; the original's animation state byte
    // works that way (docs/formats/anim-definitions.md). Keyed by DEFINITION, not (def, anchor),
    // because the event resolves one animation record and never walks the per-node copies.
    private readonly HashSet<AnimDefinition> _invalidated = new();

    // Every definition that has ever been started, so AnimStateOf can tell "has run" from "never
    // ran", and so the animation-list activation prerequisite can count its callers.
    private readonly HashSet<AnimDefinition> _everStarted = new();

    private readonly Dictionary<string, int> _unhandled = new(StringComparer.Ordinal);

    // WAIT_FOR_COMPLETION: callee name -> how many holds it took. NAMES, not just a total: a hold's
    // whole effect is to keep a caller's sequence (and therefore its instance) alive longer, so it
    // surfaces as a live-instance count that moved, and "which one" is the only question worth
    // asking about that. Bounded by the install's 38 distinct flagged callees.
    private readonly Dictionary<string, int> _waitsByCallee = new(StringComparer.OrdinalIgnoreCase);

    // Callees whose routed-away hold has already been named in the log, the print is once per
    // name, not once per call, because a gun's impact effect routes ten times a second.
    private readonly HashSet<string> _routedWaitsNamed = new(StringComparer.OrdinalIgnoreCase);

    // Same, for a flagged call that reached no live callee instance and so held nothing.
    private readonly HashSet<string> _inertWaitsNamed = new(StringComparer.OrdinalIgnoreCase);

    // The roots of every library copy IndexPooledCopy took in, read by InStagedCopy.
    private readonly HashSet<Node3D> _stagedCopies = new();

    private readonly DestructibleRegistry _destructibles = new();

    // The call-site node a PlayEffectAt instance was invoked WITH, the callee's INPUT_NODE. The
    // local CALL_ANIMATION path expresses this by anchoring the callee on the site node; the
    // external path anchors on the staged template root instead, so the sentinel's referent is
    // carried here, keyed by the instance identity. Cleared with the instance.
    private readonly Dictionary<(AnimDefinition Def, Node3D? Anchor), Node3D> _inputNodes = new();

    // Which defs condition on their own INPUT_NODE's active state (a NodeActive sentinel in any
    // sequence), the damage-stage sputters. Their lifetime is authored (loop while the host node
    // is active), so PlayEffectAt gives them the real site node and no TTL.
    private readonly Dictionary<AnimDefinition, bool> _inputGoverned = new();

    // Each def's own FBFX wash gate (WashGateRadiusSquared), scanned once. Four defs per chapter
    // carry a wash and a burst can fire several a second, so the scan is cached rather than repeated
    // per event.
    private readonly Dictionary<AnimDefinition, float> _washGates = new();

    // Keyed by (light name, anchor). The anchor identifies the *instance* of the definition,
    // and a definition's `lights` array is its own symbol table, so two refineries each get
    // their own orange_light. It cannot be keyed by host node the way puffers are: the flicker
    // events are partial updates carrying only {name, range}, with no AT_NODE to resolve from.
    private readonly List<(AnimDefinition Def, Node3D? Anchor, float Deadline)> _effectTtls = new();

    // Per effect anim name, the definitions one PlayEffectAt reaches through CALL_ANIMATION, the
    // set whose RESET_STATE a pool-slot checkout re-applies (ResetCheckedOutCopies). Memoized:
    // the program is fixed at Bind and gun hits check a slot out many times a second.
    private readonly Dictionary<string, List<AnimDefinition>> _checkoutClosure =
        new(StringComparer.OrdinalIgnoreCase);

    // ---- IF/ELSEIF conditions ----
    // Per condition kind: how often it evaluated true / false. Reported after the bootstrap
    // passes, which is the headless proof that (say) the refinery's AnimationLod branch is
    // now TAKEN rather than skipped.
    private readonly Dictionary<string, (int True, int False)> _conditions = new(StringComparer.Ordinal);

    private readonly Dictionary<(string Kind, Node3D? Anchor), bool> _condLast = new();

    // Per host: the collider RIDs a NODE_UNDERCOVER probe from inside that host ignores. Keyed by
    // instance id rather than by the node, so a freed host is never dereferenced as a dictionary
    // key long after the subtree it named is gone.
    private readonly Dictionary<ulong, Godot.Collections.Array<Rid>> _undercoverExclude = new();

    private readonly HashSet<string> _retargetsLogged = new(StringComparer.Ordinal);

    // The (caller anim, target node) pairs AnchorWarnAnimNames has already warned about, which is
    // also where a census reads back the anchors this airframe lacks. Sorted, so that read is
    // order-stable.
    private readonly SortedSet<string> _anchorWarned = new(StringComparer.OrdinalIgnoreCase);

    // The opacity/fade tables, the landing-resume marks and the pose helpers live in
    // Anim/PoseChannel.cs with the rest of the object-pose family; `_rest` stays here (above)
    // because the death flow reads it too.

    // Ambient defs carrying EXECUTION_BY_RANGE wait here instead of starting at bootstrap:
    // each (def, anchor) starts once, the first time the player is inside its distance band.
    // Checked on a cell-crossing cadence (see TickDeferredByRange), never per frame.
    private readonly List<(AnimDefinition Def, Node3D Anchor)> _rangeDeferred = new();

    // The subset deferred from the mission startanim list because their call target is library
    // content. Ordinary ON_STARTUP range defs must retain their old call-resolution behavior.
    private readonly HashSet<AnimDefinition> _rangeLibraryCallDefs = new();

    private readonly List<Vector3I> _rangeCheckCells = new();

    // Each deferred anchor's range origin in the anchor's own frame, measured once it is in the
    // tree. The world-space origin is a mesh-bounds walk over the anchor's subtree, and re-walking
    // 69 world subtrees on every cell crossing allocated tens of MB/s of finalizable Godot
    // wrappers; the local offset only moves when the anchor does, so it is remeasured never.
    private readonly Dictionary<Node3D, Vector3> _rangeOriginLocal = new();

    // Per roster-spawned rig that answers for a chapter library-root vehicle node: its own parts by
    // gamez name, and the rotation it was spawned at. ⚠ These names stay OUT of the resolver's
    // index, the shared airframe spells them `pilot`, `body`, `healthy`, and an unrelated
    // definition would claim them. Only a definition anchored on that rig reads them, which is the
    // original's own first resolution tier (docs/org/sequences.md).
    private readonly Dictionary<ulong, Dictionary<string, Node3D>> _vehicleParts = new();

    // Each of those rigs' rotation as the roster placed it, which is what an AT_NODE_XYZ rotate
    // reads off an aeroplane: see PlacedRotationOf.
    private readonly Dictionary<ulong, Basis> _vehiclePlaced = new();

    // The engine-only nodes between each rig root and the airframe it draws (the shake pivot),
    // which carry the aircraft's presence rather than any gamez node's active bit: see
    // SetTargetActive.
    private readonly Dictionary<ulong, List<Node3D>> _vehicleShell = new();

    // Every definition a mission trigger has started, its CALL_ANIMATION closure included. The
    // _missionCallDepth counter below only covers the trigger's own dispatch; a cutscene's later
    // beats run off delayed sequence events outside it and are still that trigger's work.
    private readonly HashSet<string> _missionTriggerDefs = new(StringComparer.OrdinalIgnoreCase);

    // See RangePositions: the last flying pose of each player, which a cutscene hold answers with.
    private readonly List<Vector3> _rangePositions = new();

    // The seeded nodes ParkDockingHook could not bind through a symbol table, published with the
    // rest of its tally as LastDockingHookPark when the park finishes.
    private readonly List<string> _hookSeedsUnbound = new();

    private Node3D _root = null!;

    private AnimProgram _program = null!;

    // Per called def name, the nodes a CALL_ANIMATION targets it with (CallTargetNames). Memoized:
    // the program is fixed at Bind, and a stepped pre-warm asks for it once a slice.
    private Dictionary<string, HashSet<string>>? _callTargets;

    private int _opsApplied, _opsUnresolved;

    private int _hookSeeds, _hookSeedsBound;

    // Whether the ambient passes have already run, set when Bootstrap runs them inline
    // (AutoStart=true) or when StartAmbient runs them on demand, so StartAmbient is idempotent
    // and a normal bootstrap's ambient toggle is a no-op rather than a second bootstrap.
    private bool _ambientStarted;

    private int? _seed;

    private int _startDepth;

    // True while Advance is walking the live instances, which is the one window where a start
    // belongs at the tail of the walk rather than inside the dispatch that asked for it.
    private bool _walkingInstances;

    // Nonzero while a death's own Start burst (RunDeathSequence) is on the call stack, a counter
    // because a chained CALL_ANIMATION can call again. It lets a death-triggered call relocate its
    // callee's effect-template root onto the struck node, the way TemplateStage.Places does.
    // ⚠ Never widen this to the ambient world boot or RESET_STATE; those must leave a shared
    // template at its gamez origin, and the goldens are byte-identical on that.
    private int _deathCallDepth;

    // Nonzero while a deferred EXECUTION_BY_RANGE definition starts. Its immediate calls may
    // summon mission props from the gamez library; unrelated ambient bootstrap calls may not.
    private int _rangeCallDepth;

    // Nonzero while an explicit mission trigger starts. Its call closure has the same right to
    // summon authored library roots as a range trigger, without widening ordinary Play calls.
    private int _missionCallDepth;

    // The basis the play now dispatching wants its OrientedCallAnimNames callees placed with, null
    // outside such a play. Held here rather than passed down because the dispatch that places a
    // callee sits several event-walk frames below PlayEffectAt's own Start.
    private Basis? _callPlacementOrient;

    private EmitterDirector? _emitters;

    private SoundChannel? _sound;

    private LightChannel? _light;

    private PoseChannel? _pose;

    private float _effectClock;

    private int _damagesLogged;

    // CALL_ANIMATION retargeting tallies. Deliberately NOT routed through Count(), which is
    // the "event kinds not yet acted on" channel, a retargeted call is acted on, and filing
    // it there would report a working feature as a missing one.
    private int _retargeted, _retargetUnresolved;

    private float _debugClock;

    // WAIT_FOR_COMPLETION. The completion test the CallAnimation case just installed,
    // handed to the sequence runner through ISequenceHost.PendingWait and read exactly once,
    // Dispatch clears it on entry, so it can never leak onto a later event. A closure over the
    // (target, anchor) pairs THIS call reached, because that is the only thing that identifies
    // them: re-asking by name would resolve a pooled template copy a second time and take
    // another slot. See the CallAnimation case and analysis/wait-for-completion/FINDINGS.md.
    private Func<bool>? _pendingWait;

    // Sim seconds this runtime has advanced, the clock a wait's ceiling is measured against.
    // Deliberately the runtime's own accumulation of Advance's dt, not a wall clock and not
    // GameClock: a wait is authored animation time and must not scale with the client.
    private float _elapsed;

    // How many waits were installed, and how many hit WaitCeilingS instead of their callee
    // finishing. The second number is the one that matters, it must be 0.
    private int _waitsInstalled, _waitsAbandoned;


    /// <summary>Key puffer emitters by owning def as well as (name, host), see
    /// <see cref="EmitterDirector"/>'s keying remark, which carries the measurement behind each
    /// case. Set on the world-effects runtime, where distinct effect defs declaring same-named
    /// puffers are distinct emitters (the damage-stage sputters); off on the world runtime, where
    /// the collapsed key de-dups same-name multi-def ambient stacks. Read once, when
    /// <see cref="Emitters"/> is first built.</summary>
    private bool DefScopedPufferKeys;

    /// <summary>How long a <see cref="PlayEffectAt"/> effect instance may run before this runtime
    /// stops it (seconds; 0 = never, the default). The world-effects runtime sets it so a stop-less
    /// sustained effect, <c>large_30sec_fire</c>'s <c>fire_n_smoke</c>, which has no ACTIVE_STATE 0
    /// and would otherwise emit for the rest of the session, is bounded. Only effects this runtime
    /// itself started via PlayEffectAt are tracked; ambient/crash runtimes leave it 0 and are
    /// untouched.</summary>
    private float EffectTtl;

    /// <summary>The inert stage: nothing pooled, nothing staged hidden, no called template
    /// relocated. What the ambient world runtime and every plain testing runtime take,
    /// the three flags are sealed, so a runtime built this
    /// way cannot be talked into a template role after the fact.</summary>
    internal AnimRuntime()
        : this(NewTemplateStage())
    {
    }

    /// <summary>Takes a SEALED template stage: the pool/reveal/relocate role is decided by whoever
    /// knows the runtime's job, before this runtime exists. The runtime hooks are wired here rather
    /// than passed to the stage's constructor, because the stage's <c>findAll</c> needs the
    /// resolver and the resolver's <c>ownRootsOf</c> is the stage's
    /// <see cref="TemplateStage{TNode}.RootsFor"/>; both are delegates, invoked after this returns.
    /// One stage per runtime. The resolver's own policy flags follow at <see cref="Bootstrap"/>.</summary>
    internal AnimRuntime(TemplateStage<Node3D> stage)
    {
        _templateStage = stage;
        _resolver = new NameResolver<Node3D>(Node3DIdentity.Instance, _templateStage.RootsFor, IsInstanceValid,
            StagingAdmits, StagedCopyRootOf);
        _templateStage.Wire(
            FindAll,
            Anchors,
            IsLive,
            () => _instances.Select(i => (i.Def, i.Anchor)),
            LevelsTemplate,
            TemplateStillAnimated,
            NameOf,
            s => IndexWorld(s, indexByPointer: false),
            () => _resolver.ClearFindCache(),
            ApplyResetStatesWithin);
    }

    /// <summary>Running count of ballistic <see cref="MotionRuntime"/> bodies launched, the debris
    /// pieces a death or crash flings (translation/translation_range/scale/forward_rotation over a
    /// run time). Zero at bootstrap (nothing ambient fires the ballistic path); the damage harness
    /// samples the delta across a kill to prove the wreck actually tumbles.</summary>
    public int BallisticMotionsLaunched => Motions.LaunchCount;

    /// <summary>The live per-instance HP of every destructible node group in this world.
    /// Built during the bootstrap; the source of the value <c>ANIM_HEALTH</c> conditions read.
    /// Weapon damage and the death sequence act through it.</summary>
    public DestructibleRegistry Destructibles => _destructibles;

    /// <summary>Every definition of the bound program, read-only, for a consumer that selects
    /// defs by a rule <see cref="Play"/>'s name lookup cannot express (the zeppelin damage
    /// runtime finding the hull-death def by its activation prerequisite, M4 F18).</summary>
    public IReadOnlyList<AnimDefinition> ProgramDefs => _program.Defs;

    /// <summary>What the last <see cref="ParkDockingHook"/> left behind (<see cref="DockingHookPark"/>),
    /// zeroed before it starts. A suite reads the park's OWN verdict rather than re-asking the
    /// resolver afterwards, which would answer about a table later stages have grown.</summary>
    public DockingHookPark LastDockingHookPark { get; private set; }

    /// <summary>One-shot SOUND events that resolved to a stream and fired this session (the
    /// destruction/damage/impact audio). Exposed for the damage-test harness, which cannot
    /// screenshot audio: a nonzero delta across a kill is how "the death's explosion sounded" is
    /// verified headless.</summary>
    public int OneShotSoundsPlayed => Sound.OneShotSoundsPlayed;

    /// <summary>How many PUFFER_STATE emitters this runtime has actually built (not just started
    /// the owning def). The world-effects verify checks this rather than "the def ran", a
    /// started effect whose factory is retired or whose textures are missing builds nothing and
    /// renders nothing (verification.md).</summary>
    public int PuffersBuilt => Emitters.Built;

    Action<EventDispatch>? ISequenceHost.OnEventDispatched => OnEventDispatched;

    Func<bool>? ISequenceHost.PendingWait => _pendingWait;

    /// <summary>Whether a landings/mission trigger currently owns the synchronous call closure.
    /// PoseChannel uses this to preserve an authored SI-script clock when a cross-archive actor is
    /// absent; ambient scripts keep their established zero-duration miss.</summary>
    internal bool MissionTriggerActive => _missionCallDepth > 0;

    /// <summary>Sim seconds this runtime has advanced, for a suite reading which callback fed
    /// <see cref="Advance"/>.</summary>
    internal float Elapsed => _elapsed;

    /// <summary>How many <see cref="PlayEffectAt"/> calls took a pool slot whose previous instance
    /// was still live, the pool being smaller than the concurrency it met, so those two calls
    /// share one template copy (<see cref="TemplateStage{TNode}.Pooled"/>). Zero
    /// is "the pool covered everything asked of it"; a growing count is the number to size against
    /// (the pool size itself is a TUNE, `WorldEffectsFactory.EffectPoolSlots`). Forwards to
    /// <see cref="TemplateStage{TNode}.Recycles"/>, which counts both wrap flavours.</summary>
    internal int PoolRecycles => _templateStage.Recycles;

    /// <summary>Every <c>&lt;caller anim&gt;|&lt;target node&gt;</c> pair
    /// <see cref="AnchorWarnAnimNames"/> has warned about on this rig, i.e. the anchors this
    /// airframe does not carry. Empty on an airframe the stages fit, and empty on every runtime
    /// that set no warn list.</summary>
    internal IReadOnlyCollection<string> UnresolvedStageAnchors => _anchorWarned;

    /// <summary>Per-kind counts of events (and puffer/sound sub-reasons) this runtime processed but
    /// could not act on, the same tally <c>ReportUnhandled</c> prints at bootstrap, exposed so a
    /// post-bootstrap harness (the effects test) can see WHY an effect built no puffer
    /// (<c>PufferState(no host node)</c>, <c>PufferState(no texture: …)</c>).</summary>
    internal IReadOnlyDictionary<string, int> UnhandledEventCounts => _unhandled;

    /// <summary>The built world's own root, the node every world subtree hangs under. Exposed so
    /// a caller can ask which top-level world object a node belongs to (a turret gunner's "which
    /// platform am I bolted to"); null before <see cref="Bind"/>.</summary>
    internal Node3D? WorldRoot => _root;

    /// <summary>Pins the runtime's RNG for a reproducible run. Every session sets one, derived from
    /// the master seed (<see cref="Utils.Rng"/>); null leaves it drawn from .NET's own entropy. Set
    /// at construction through the object initializer, before <see cref="Bind"/>.</summary>
    internal int? Seed
    {
        init
        {
            _seed = value;
            if (value is { } s)
                _rng = new Random(s);
        }
    }

    /// <summary>How many <c>WAIT_FOR_COMPLETION</c> holds this runtime has armed, and how many of
    /// those ended at <see cref="WaitCeilingS"/> instead of at their callee. The second is the one
    /// that matters and is expected to stay 0; the `wait-for-completion` suite asserts both.
    /// </summary>
    internal int WaitsInstalled => _waitsInstalled;

    internal int WaitsAbandoned => _waitsAbandoned;

    /// <summary>Every PUFFER_STATE emitter's whole life on this runtime: start, the four stops,
    /// the follow, and the census a suite reads.
    /// ⚠ Built on FIRST USE, inside <see cref="Bind"/>, capturing <see cref="EmitterFactory"/>,
    /// <see cref="DefScopedPufferKeys"/> and <see cref="DebugMotions"/> as they stand then. Set all
    /// three before binding; flipping one afterwards does not reach the director.</summary>
    internal EmitterDirector Emitters => _emitters ??= new EmitterDirector(
        EmitterFactory ?? new SpentEmitterFactory(), DefScopedPufferKeys, DebugMotions, Count);

    /// <summary>The live motion collection and its registration rules. `internal` so a suite can
    /// ask <c>Airborne</c>, the retirement hold's own mechanism, and <c>OwesBounce</c> beside
    /// it.</summary>
    internal MotionSet Motions { get; } = new();

    /// <summary>This runtime's `SOUND_NODE`/`SOUND` family. Reads <see cref="Sounds"/> and
    /// <see cref="SoundHandledElsewhere"/> live through the closures below, not a snapshot at
    /// construction, since both change after this runtime exists (`Sounds` goes non-null once the
    /// world build finishes; a crash runtime's `SoundHandledElsewhere` follows its aircraft).
    /// </summary>
    private SoundChannel Sound => _sound ??= new SoundChannel(
        () => Sounds, () => SoundHandledElsewhere, Resolve, () => _rng, () => _opsApplied++,
        def => FastForward?.RateFor(def) ?? 1f);

    /// <summary>This runtime's `LIGHT_STATE`/`LIGHT_ANIMATION` family. Reads <see cref="Lights"/>
    /// and <see cref="LightViewerPositions"/> live through the closures below, not a snapshot at
    /// construction, for the same reason <see cref="Sound"/> does: both can be assigned or turn
    /// non-null after this runtime already exists.</summary>
    private LightChannel Light => _light ??= new LightChannel(() => Lights, Resolve, () =>
    {
        var viewers = LightViewerPositions?.Invoke();
        return viewers != null && viewers.Count > 0 ? viewers : new[] { PlayerPos() };
    }, () => DebugMotions, () => LightsCommittedElsewhere);

    /// <summary>This runtime's object-pose/visual family: the `OBJECT_*` pose, opacity and motion
    /// events, and the motion-builder role. It takes this runtime itself as one dependency, since
    /// the motion value types it constructs already declare `AnimRuntime` as their host argument
    /// and its pose helpers reach `_rest` through the same <see cref="RestOf"/> seam; everything
    /// else arrives as its own narrow dependency below. `Emitters` and `_program` come through
    /// closures because both are late-bound relative to this property's first use.</summary>
    private PoseChannel Pose => _pose ??= new PoseChannel(this, Targets, Motions, () => Emitters,
        (def, slot) => _program.ScriptFor(def, slot), Count);

    /// <summary>Builds a sealed <see cref="TemplateStage{TNode}"/> over the Godot adapter. ⚠ Spell
    /// the engine hooks here and nowhere else: node identity, the pool-slot ancestry walk, the
    /// <c>TopLevel</c>+<c>GlobalTransform</c> placement write, the visibility write. A caller only
    /// decides the role. <paramref name="debugMotions"/> is baked because the stage exists before
    /// the runtime; it gates the pooled caller-slot log line alone.</summary>
    public static TemplateStage<Node3D> NewTemplateStage(bool pooled = false, bool shown = false,
        bool placesCalled = false, bool debugMotions = false,
        IEnumerable<string>? placeExempt = null)
    {
        return new TemplateStage<Node3D>(
            Node3DIdentity.Instance,
            SlotMarkOf,
            IsInstanceValid,
            n => n.GlobalTransform,
            PlaceNodeAt,
            (n, visible) => n.Visible = visible,
            s => Log.Debug("anim", $"{s}"),
            () => debugMotions,
            pooled,
            shown,
            placesCalled,
            placeExempt);
    }

    /// <summary>Configures (but does not bind) the world-effects runtime: the three invariant flags
    /// a "renders effects at a call site, no ambience of its own" role always takes. The caller
    /// still calls <see cref="Bind"/> and adds the returned node to the tree.
    /// ⚠ <paramref name="stage"/> arrives sealed. Template policy is stage state, never runtime
    /// properties, so build the stage with the role it wants (<see cref="NewTemplateStage"/>).</summary>
    public static AnimRuntime ForEffects(TemplateStage<Node3D> stage, int seed,
        IEmitterFactory emitterFactory,
        bool debugMotions, float effectTtl, Func<Vector3> playerPosition)
    {
        return new AnimRuntime(stage)
        {
            AutoStart = false,
            NameResolveFallback = true,
            SoundHandledElsewhere = true,
            DefScopedPufferKeys = true,
            DebugMotions = debugMotions,
            EmitterFactory = emitterFactory,
            EffectTtl = effectTtl,
            Seed = seed,
            PlayerPosition = playerPosition,
        };
    }

    /// <summary>Configures (but does not bind) the per-player crash rig's construction ritual,
    /// the same three invariant flags and the same sealed <paramref name="stage"/> handover as
    /// <see cref="ForEffects"/>, but with no <c>EffectTtl</c> or
    /// <c>PlayerPosition</c> (the crash def has no PUFFER_STATE that needs either). The caller still
    /// calls <see cref="Bind"/> + adds the returned node to the tree.</summary>
    public static AnimRuntime ForCrashRig(TemplateStage<Node3D> stage, int seed,
        IEmitterFactory emitterFactory,
        bool debugMotions)
    {
        return new AnimRuntime(stage)
        {
            AutoStart = false,
            NameResolveFallback = true,
            SoundHandledElsewhere = true,
            DebugMotions = debugMotions,
            EmitterFactory = emitterFactory,
            Seed = seed,
        };
    }

    /// <summary>The world nodes a definition anchors to, for the inspect tools, the runtime's own
    /// answer, so a readout shows what the bootstrap actually bound rather than a re-derivation.
    /// A null entry is a global (anchorless) instance. Read-only: the list is the cached one.</summary>
    public IReadOnlyList<Node3D?> AnchorsOf(AnimDefinition def) => Anchors(def);

    /// <summary>Every world node matching a NAME pattern, optionally restricted to one subtree,
    /// the same wildcard matching and the same memoized index the dispatch uses, exposed so an
    /// inspect tool asks the engine instead of re-implementing the matcher. Read-only.</summary>
    public IReadOnlyList<Node3D> FindNodes(string pattern, Node3D? scope = null) => FindAll(pattern, scope);

    /// <summary>Runs the bootstrap passes against a built world. A separate call (not folded into
    /// construction) so a caller can set build-time-only collaborators (notably
    /// <see cref="EmitterFactory"/>, which depends on the session TextureArchive's lifetime)
    /// before the passes fire the events that need them. The world runtime binds directly; the
    /// <see cref="ForEffects"/>/<see cref="ForCrashRig"/> role factories return unbound for the same
    /// reason.</summary>
    public void Bind(Node3D worldRoot, AnimProgram program)
    {
        Name = "AnimRuntime";
        Bootstrap(worldRoot, program);
    }

    /// <summary>Re-pins the RNG to the constructed <see cref="Seed"/>, and clears the sound groups'
    /// last-picked memory with it, that recency state sits outside the RNG, so restoring only the
    /// dice would still diverge on the first weighted pick. The animation debugger calls this on
    /// every Play/Restart; without it the stream would continue and a "restart" would branch on the
    /// first RANDOM_WEIGHT.</summary>
    public void Reseed()
    {
        if (_seed is { } s)
        {
            _rng = new Random(s);
        }
        Sounds?.ResetGroupRecency();
    }

    /// <summary>The bind-time resolution census, resolver-owned data, projected here for the
    /// <c>--node=</c> stage's log and the node lab. Empty unless <see cref="ReportResolution"/>
    /// was set before <see cref="Bind"/>; see <see cref="NameResolver{TNode}.ResolutionLines"/>
    /// for what the lines mean and why they exist.</summary>
    public IReadOnlyList<string> ResolutionLines() => _resolver.ResolutionLines();

    /// <summary>Starts every definition carrying this ANIMATION_NAME immediately: an anchored def
    /// starts once per anchor, an unanchored one gets
    /// a single global-resolution instance (null anchor), and each instance's RESET_STATE is
    /// re-applied first. Returns the (def, anchor) pairs started, empty when the name matches no
    /// definition in this program. This is the animation debugger's <c>--play-anim</c> path; its
    /// Restart is <see cref="Stop"/> → <see cref="Reseed"/> → Play.</summary>
    public List<(AnimDefinition Def, Node3D? Anchor)> Play(string animName, Node3D? fallbackAnchor = null,
        bool applyReset = true)
    {
        var started = new List<(AnimDefinition Def, Node3D? Anchor)>();
        foreach (var def in _program.ByAnimName(animName))
        {
            var anchors = Anchors(def);
            if (anchors.Count == 0)
            {
                // A placeless def (its NAME resolves nothing): fall back to the caller's staging
                // anchor, the animation debugger's in-front-of-camera dummy, or null, which is
                // global resolution (the def names world nodes directly). Bootstrap passes null.
                anchors.Add(fallbackAnchor);
            }
            foreach (var anchor in anchors)
            {
                // ⚠ Pass applyReset:false for the crash trigger; its reset restores the healthy
                // panels and hides the wreck. Re-posing IS the reset, so it clears the invalidation
                // latch too, or a self-invalidating def could only ever replay once per session.
                if (applyReset && def.ResetState != null)
                {
                    _invalidated.Remove(def);
                    ApplyInstant(def.ResetState.Events, def, anchor);
                }
                Start(def, anchor);
                started.Add((def, anchor));
            }
        }
        return started;
    }

    /// <summary>Starts a mission trigger and lets its immediate <c>CALL_ANIMATION</c> and
    /// <c>OBJECT_ADD_CHILD</c> closure materialize library-root actors. Use for authored runtime
    /// gates such as <c>landings.zrd</c> rows and the objective script's <c>WAKE_ANIM</c>, never
    /// for ambient bootstrap or the animation debugger. <paramref name="fallbackAnchor"/> is
    /// <see cref="Play"/>'s: the anchor a placeless definition runs on.</summary>
    public List<(AnimDefinition Def, Node3D? Anchor)> PlayMissionTrigger(string animName,
        Node3D? fallbackAnchor = null)
    {
        // ⚠ Before the start, not after: the first CALLBACK can land inside this very dispatch,
        // and a slot written behind it would arrive to an episode already booked to a callee.
        MissionTriggerOwner?.Invoke(animName);
        // ⚠ The depth below covers this dispatch and nothing after it, while a cutscene's later
        // beats run off delayed sequence events seconds outside it, so the closure is what is
        // remembered rather than the call stack.
        foreach (var reached in CallClosureOf(animName))
        {
            if (reached.AnimName is { Length: > 0 } reachedName)
                _missionTriggerDefs.Add(reachedName);
        }
        _missionCallDepth++;
        try
        {
            return Play(animName, fallbackAnchor);
        }
        finally
        {
            _missionCallDepth--;
        }
    }

    /// <summary>Runs a definition's authored <c>RESET_STATE</c> as the event block the original's
    /// reset schedule runs when the definition ends: the <c>CALL_ANIMATION</c>s and child detaches
    /// the bootstrap's pose-only walk suppresses. ⚠ Callbacks stay suppressed, the cutscene host
    /// raises the gameplay end state itself, and raising them here would double-record the
    /// episode's codes. Returns how many definitions ran one.</summary>
    public int RunResetStateEvents(string animName)
    {
        int ran = 0;
        _missionCallDepth++;
        try
        {
            foreach (var def in _program.ByAnimName(animName))
            {
                if (def.ResetState == null)
                    continue;
                var anchors = Anchors(def);
                if (anchors.Count == 0)
                    anchors.Add(null);
                foreach (var anchor in anchors)
                    foreach (var ev in def.ResetState.Events)
                        if (ev.Kind != "Callback")
                            Dispatch(ev, def, anchor, instant: false, out _);
                ran++;
            }
        }
        finally
        {
            _missionCallDepth--;
        }
        return ran;
    }

    /// <summary><see cref="Play"/>, scoped to one world subtree: starts only the instances
    /// whose anchor sits at or under <paramref name="scope"/>. C1 carries three
    /// <c>hangerdoors</c> nodes, the zeppelin's and two ground hangars', so a generator's
    /// door call must not swing every namesake in the chapter (the F20 case; a ground hangar's
    /// doors are the mission script's, through <c>WAKE_ANIM</c> and <see cref="Play"/>).</summary>
    public List<(AnimDefinition Def, Node3D? Anchor)> PlayWithin(Node3D scope, string animName,
        bool applyReset = true)
    {
        var started = new List<(AnimDefinition Def, Node3D? Anchor)>();
        foreach (var def in _program.ByAnimName(animName))
        {
            foreach (var anchor in Anchors(def))
            {
                if (anchor == null || (anchor != scope && !scope.IsAncestorOf(anchor)))
                    continue;
                if (applyReset && def.ResetState != null)
                    ApplyInstant(def.ResetState.Events, def, anchor);
                Start(def, anchor);
                started.Add((def, anchor));
            }
        }
        return started;
    }

    /// <summary><see cref="Stop"/>, scoped like <see cref="PlayWithin"/>: tears down only the
    /// named instances anchored at or under <paramref name="scope"/>.</summary>
    public void StopWithin(Node3D scope, string animName)
    {
        // Collect first: RemoveInstances mutates _instances, one call per distinct anchor.
        var anchors = new HashSet<Node3D>();
        foreach (var inst in _instances)
        {
            if (string.Equals(inst.Def.AnimName, animName, StringComparison.OrdinalIgnoreCase)
                && inst.Anchor is { } anchor && (anchor == scope || scope.IsAncestorOf(anchor)))
                anchors.Add(anchor);
        }
        foreach (var anchor in anchors)
            RemoveInstances(animName, anchor, tearDown: true);
    }

    /// <summary>The world node the animation debugger frames its camera on for one started
    /// instance: the anchor itself when the instance has one, else the first of the def's own
    /// node names that resolves in this world (the unanchored global-resolution case). Null when
    /// nothing resolves, the caller keeps its current framing.</summary>
    public Node3D? FrameTarget(AnimDefinition def, Node3D? anchor)
    {
        if (anchor != null)
        {
            return anchor;
        }
        foreach (var name in def.NodeList)
        {
            if (!string.IsNullOrEmpty(name) && Resolve(name, def, null) is { } node)
            {
                return node;
            }
        }
        return null;
    }

    /// <summary>Runs the ambient-playback passes a quiet-stage bootstrap (<see cref="AutoStart"/>
    /// = false) skipped: the ON_STARTUP definitions and the mission's startanims begin playing.
    /// Idempotent, a second call is a no-op, and it is already a no-op after a normal
    /// (AutoStart=true) bootstrap, so the debugger can bind it to a toggle without stacking
    /// instances.</summary>
    public void StartAmbient()
    {
        if (_ambientStarted)
            return;
        _ambientStarted = true;
        var (startupRun, ran, missing) = RunAmbientPasses();
        Log.Info("anim", $"anim: ambient start, {startupRun} ON_STARTUP + {ran.Count} start anims running, {_instances.Count} live instance(s), {Motions.Count} live motion(s)");
        if (ran.Count > 0 || missing.Count > 0)
            Log.Info("anim", $"anim: start anims [{string.Join(", ", ran)}]{(missing.Count > 0 ? $", undefined here: [{string.Join(", ", missing)}]" : "")}");
    }

    /// <summary>Reverses <see cref="StartAmbient"/>, the animation debugger's ambient-off half.
    /// Tears down every live instance and its resources EXCEPT the one carrying
    /// <paramref name="keepAnim"/> (the def the lab is currently playing, which survives at its
    /// playhead), then re-applies the quiet stage's base states for every torn-down def, so the
    /// world returns to the quiet stage a <c>AutoStart=false</c> bootstrap leaves. A no-op unless
    /// ambient is running, so the toggle is symmetric with <see cref="StartAmbient"/>.</summary>
    public void StopAmbient(string? keepAnim = null)
    {
        if (!_ambientStarted)
            return;
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var inst = _instances[i];
            if (keepAnim != null && string.Equals(inst.Def.AnimName, keepAnim, StringComparison.OrdinalIgnoreCase))
                continue;
            _instances.RemoveAt(i);
            TearDownResourcesOf(inst.Def, inst.Anchor);
            OnInstanceFinished?.Invoke(inst.Def, inst.Anchor);
        }
        // Re-quiet the torn-down defs' nodes: re-apply their RESET_STATE base poses (the same
        // pass 1 the bootstrap runs), skipping the kept def whose live motions still drive it.
        foreach (var def in _program.Defs)
        {
            if (keepAnim != null && string.Equals(def.AnimName, keepAnim, StringComparison.OrdinalIgnoreCase))
                continue;
            if (def.ResetState == null)
                continue;
            var anchors = Anchors(def);
            foreach (var anchor in anchors)
                ApplyInstant(def.ResetState.Events, def, anchor);
        }
        HideUncoveredDestroyed();
        _rangeDeferred.Clear(); // a quiet stage must not proximity-start ambient defs
        _rangeOriginLocal.Clear();
        _ambientStarted = false;
        Log.Info("anim", $"anim: ambient stopped, {_instances.Count} live instance(s) kept, {Motions.Count} live motion(s)");
    }

    /// <summary>Hard-stops everything this runtime created and re-applies every anchored
    /// definition's RESET_STATE: the per-player crash runtime's respawn. The caller re-homes any
    /// node the def moved but has no reset event for.
    /// ⚠ Clear the whole resource pool, not just live instances, and <c>Clear</c> puffers rather
    /// than sustain-ending them. An effect whose sequence already ended is off <c>_instances</c>
    /// with its puffer still emitting. ⚠ Never call this from the shared world runtime.</summary>
    public void ResetToBaseState()
    {
        _instances.Clear();
        Motions.Reset();
        Emitters.Reset();
        _inputNodes.Clear();
        Light.Reset();
        Sound.Reset();

        foreach (var def in _program.Defs)
        {
            if (def.ResetState == null)
                continue;
            foreach (var anchor in Anchors(def))
                if (anchor != null)
                    ApplyInstant(def.ResetState.Events, def, anchor);
        }
    }

    /// <summary>Indexes a subtree added to the world AFTER the bootstrap (the effect-template stage
    /// <see cref="WorldBuilder"/> deliberately skips), then applies the RESET_STATE of every
    /// definition now anchored within it, exactly as bootstrap pass 1 would have. The find cache is
    /// cleared, since the bootstrap may have cached these names as resolving to nothing. Additive:
    /// nothing running is disturbed, and mission setup does not re-run.</summary>
    public void IndexStage(Node3D subtree)
    {
        IndexWorld(subtree);   // appends the subtree's nodes to the resolver (name + gamez index)
        _resolver.ClearFindCache();    // drop stale "resolves to nothing" results cached during bootstrap
        ApplyResetStatesWithin(subtree);
    }

    /// <summary>Indexes a further copy of a chapter library root built after the bootstrap (a
    /// spawned surface vehicle): by name only, since every copy carries the same compiled indices
    /// and the by-index map holds one claimant; then registers the destructible pools of every
    /// definition now anchored within it and applies their RESET_STATE, the two halves of
    /// bootstrap pass 1 a late hull needs. Returns the pools registered on it.</summary>
    public List<DestructibleRegistry.Instance> IndexSpawnedCopy(Node3D subtree)
    {
        IndexWorld(subtree, indexByPointer: false);
        _resolver.ClearFindCache();
        var pools = new List<DestructibleRegistry.Instance>();
        foreach (var def in _program.Defs)
        {
            if (!def.Destructible)
                continue;
            foreach (var a in Anchors(def))
                if (a != null && (a == subtree || subtree.IsAncestorOf(a)))
                    pools.Add(_destructibles.Register(def, a, def.Health, DamageNodeOf(def, a)));
        }
        ApplyResetStatesWithin(subtree);
        return pools;
    }

    /// <summary>Indexes a subtree built from ANOTHER archive: its stamped node indices are shifted
    /// by <paramref name="indexOffset"/> into this chapter's cross-archive block, so a compiled
    /// symbol table binds them (<see cref="AircraftStage.PointerBaseOf"/>). No RESET_STATE pass
    /// runs over it, unlike <see cref="IndexStage"/>: the subtree is a live aircraft its own
    /// builder already parked, and a chapter definition that happens to anchor on one of its
    /// generic node names must not re-pose it.</summary>
    public void IndexRebasedStage(Node3D subtree, int indexOffset)
    {
        // ⚠ Retire first: this is the one stage that puts a subtree in over one its caller may
        // have freed, an airframe swapped for another on the same rig.
        RetireFreedNodes();
        IndexWorld(subtree, indexOffset: indexOffset);
        _resolver.ClearFindCache();
    }

    /// <summary>How many rows of the resolver's node table name a node that has since been freed.
    /// Zero right after <see cref="IndexRebasedStage"/>, which retires them. A suite reads it to
    /// assert that, because the fault a stale row causes needs a hash collision and so shows on
    /// some runs only.</summary>
    public int FreedNodeRows() => _resolver.FreedRows();

    /// <summary>How many keys of the template stage's identity-keyed maps name a node that has
    /// since been freed, the same reading <see cref="FreedNodeRows"/> gives for the node table.
    /// The stage guards the node a call hands it and never the keys it already holds, so a suite
    /// reads this rather than re-running until a collision throws.</summary>
    public int FreedStageKeys() => _templateStage.FreedKeys();

    /// <summary>Retires every node-table row and every template-stage key naming a node that has
    /// been freed, and returns how many went. The two staging entries call it before they grow
    /// their tables; a caller that frees a subtree this runtime resolved against and stages nothing
    /// in its place calls it itself, since the free is the only event either table gets.</summary>
    // ⚠ Both halves REBUILD; a Remove hashes the dead key it is handed, which is the dereference
    // being avoided. Why a dead key throws at all: docs/architecture/Mech3.md.
    public int RetireFreedNodes()
    {
        int rows = _resolver.DropFreed();
        int keys = _templateStage.DropFreed();
        if (rows > 0 || keys > 0)
            Log.Info("anim", $"anim: retired {rows} node-table row(s) and {keys} template-stage key(s) naming freed node(s)");
        return rows + keys;
    }

    /// <summary>Parks a flown airframe's docking hook where its own <c>&lt;x&gt;_hook_retract</c>
    /// RESET_STATE puts it, scoped to a <see cref="PlaneBuilder.IsDockingHook"/> group inside this
    /// model since the general pass skips a rebased aircraft (<see cref="IndexRebasedStage"/>). A
    /// RESET_STATE alone can leave a node unparked or on the wrong axis; <see cref="SeedFromExtend"/>
    /// closes that gap from the matching extend definition's own FROM pose.
    /// Decode: docs/formats/anim-definitions/cutscenes.md. Returns the definitions applied.</summary>
    public int ParkDockingHook(Node3D planeModel)
    {
        ArgumentNullException.ThrowIfNull(planeModel);
        int applied = 0;
        _hookSeeds = 0;
        _hookSeedsBound = 0;
        _hookSeedsUnbound.Clear();
        // Two explicit passes, not one interleaved: _program.Defs lists a group's own extend and
        // retract in whatever order they were loaded, and the seed pass below must win over every
        // RESET_STATE, never race it.
        foreach (var def in _program.Defs)
        {
            if (def.ResetState == null)
                continue;
            foreach (var a in DockingHookAnchors(def, planeModel))
            {
                ApplyInstant(def.ResetState.Events, def, a);
                applied++;
            }
        }
        foreach (var def in _program.Defs)
        {
            if (def.AnimName is not { } name
                || !name.EndsWith("_extend", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var a in DockingHookAnchors(def, planeModel))
            {
                SeedFromExtend(def, a);
                applied++;
            }
        }
        LastDockingHookPark = new DockingHookPark(
            _hookSeeds, _hookSeedsBound, string.Join(", ", _hookSeedsUnbound));
        return applied;
    }

    /// <summary>Makes one live aircraft answer for the gamez LIBRARY-ROOT vehicle node it was
    /// spawned from, by that node's name and compiled index. A chapter's own copy of a vehicle is
    /// never placed, so a definition written against it addresses a name with nothing behind it.
    /// ⚠ The rig root ALONE reaches the resolver's index: the model carries the shared airframe's
    /// own node names, and indexing those globally would let an unrelated definition claim them.
    /// The subtree is kept beside it as a scoped alias instead. Additive.</summary>
    public void IndexSpawnedVehicle(Node3D rig, string libraryRootName, int gamezIndex)
    {
        _resolver.Add(rig, libraryRootName, rig.GetParent() as Node3D, gamezIndex);
        _resolver.ClearFindCache();
        var parts = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        CollectNamed(rig, parts);
        _vehicleParts[rig.GetInstanceId()] = parts;
        _vehiclePlaced[rig.GetInstanceId()] = rig.Basis.Orthonormalized();
        _vehicleShell[rig.GetInstanceId()] = ShellOf(rig, parts);
    }

    /// <summary>Writes an <c>OBJECT_ACTIVE_STATE</c> on one target. For a spawned vehicle rig an
    /// activation also clears the engine-only shell between the rig root and the airframe it
    /// draws: the aircraft's own presence lives there, and a capture that re-asserts its vehicle
    /// ACTIVE against the cutscene's AI park means the aeroplane, not the rig node.</summary>
    public void SetTargetActive(Node3D target, bool active)
    {
        SetSubtreeActive(target, active);
        if (!active || !_vehicleShell.TryGetValue(target.GetInstanceId(), out var shell))
        {
            return;
        }

        foreach (var node in shell)
        {
            if (IsInstanceValid(node))
            {
                node.Visible = true;
            }
        }
    }

    /// <summary>The rotation a spawned vehicle rig was placed at, or null for any other node. The
    /// original stores a node's rotation as a euler triple that only a scripted rotate writes,
    /// while a flying aeroplane's pose goes in as a matrix and leaves that triple alone, so an
    /// <c>AT_NODE_XYZ</c> rotate off an aeroplane reads the attitude it was PLACED at, never the
    /// one it is banking at. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public Basis? PlacedRotationOf(Node3D node) =>
        _vehiclePlaced.TryGetValue(node.GetInstanceId(), out var placed) ? placed : null;

    /// <summary>Opens a resolution census outside <see cref="Bind"/>, so a leg that PLAYS a
    /// definition can report the names it could not reach the way the bind does
    /// (<see cref="ResolutionLines"/>). Read the lines before <see cref="CloseResolutionCensus"/>.
    /// </summary>
    public void OpenResolutionCensus()
    {
        _resolver.ReportResolution = true;
        _resolver.OpenCensus();
    }

    public void CloseResolutionCensus()
    {
        _resolver.CloseCensus();
        _resolver.ReportResolution = ReportResolution;
    }

    // ---- ISequenceHost: the sequence interpreter's 3-point view of this runtime, satisfied by
    // explicit interface implementation so Dispatch/EvaluateCondition stay off AnimRuntime's own
    // surface. See SequenceRunner.cs. ----
    bool ISequenceHost.Dispatch(AnimEvent ev, AnimDefinition def, Node3D? anchor, bool instant, out float duration) =>
        Dispatch(ev, def, anchor, instant, out duration);

    bool ISequenceHost.EvaluateCondition(AnimData? condition, AnimDefinition def, Node3D? anchor) =>
        EvaluateCondition(condition, def, anchor);

    public override void _Process(double delta)
    {
        using var _ = ProcessSiteCost.Enter(ProcessSite.Anim);
        if (ManualAdvance)
        {
            return;
        }
        // Each sub-step is advanced separately rather than summed: the event scheduler resolves
        // per step, so one 4/60 s call and four 1/60 s calls are not the same playback.
        if (GameClock.Current is { } clock)
        {
            if (clock.AuthoredAnimationHeld)
            {
                return;
            }
            // A realtime session advances on the physics tick (_PhysicsProcess); the frame keeps
            // the advance only while the sim is held for a cutscene, since the movie is animation.
            if (clock.Mode == GameClock.RunMode.Realtime && !clock.Halted && !clock.SimHeld)
            {
                return;
            }
            for (int i = 0; i < clock.Steps; i++)
            {
                Advance(clock.Dt);
            }
            return;
        }
        Advance((float)delta);
    }

    /// <summary>The realtime advance: one step per Godot physics tick, the tick the flight models
    /// and the objective graph step on. ⚠ Never the frame's wall delta: physics catch-up is capped
    /// per frame, so under load the sim falls behind wall time and a frame-driven advance runs
    /// every authored motion faster than the aircraft beside it (CM11's trailer, CM10's balloons).
    /// <see cref="GameClock.PhysicsDt"/> answers zero under a cutscene hold and in every
    /// parent-driven mode, where <see cref="_Process"/> owns the advance.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (ManualAdvance || GameClock.Current is not { Mode: GameClock.RunMode.Realtime } clock)
        {
            return;
        }
        // Before the advance, never after: a pose event that seeds a held value from a node's
        // live transform must read the simulation pose, not the one the last frame drew.
        RenderPoses.Restore();
        float dt = clock.PhysicsDt(delta);
        if (dt > 0f)
        {
            // The tick's other consumer beside the session simulation, on the same --perf row.
            SimPhaseCost.Enter(SimPhase.AnimAdvance);
            try
            {
                Advance(dt);
            }
            finally
            {
                SimPhaseCost.Leave();
            }
        }
    }

    /// <summary>Advances the whole runtime by <paramref name="dt"/> seconds: motions, puffers,
    /// lights and sounds, then every live instance's sequences. <see cref="_Process"/> calls this
    /// once a frame with the real frame delta; the animation debugger disables <c>_Process</c>
    /// (<see cref="Node.SetProcess"/>(false)) and drives this itself off a fixed-dt clock, pause =
    /// don't call, step = one fixed call, slow-mo = a scaled accumulator. Same classes and same
    /// code path either way, so the game's behaviour is untouched.</summary>
    public void Advance(float dt)
    {
        // On the incoming dt, before anything reads the rate: the ramp measures real seconds, and
        // both clock paths (realtime physics, parent-driven frame) reach the runtime through here.
        FastForward?.Ramp(dt);
        _elapsed += dt;
        SampleRangePositions();
        // Motions advance ONCE per frame, here, not from the sequence runners, which would
        // apply dt once per running sequence and run the train at 4× speed.
        TickMotions(dt);
        // Before the emitters read their hosts, so a carried fire is fed this frame's hull pose.
        _templateStage.FollowSites();
        Emitters.Tick(dt);
        Light.Tick(dt);
        Sound.ApplyRates(FastForward);
        Sounds?.Tick();
        SweepEffectTtls(dt);
        _templateStage.Sweep();
        TickDeferredByRange();
        if (DebugMotions)
            LogMotions(dt);
        // A raised rate is spent as repeated passes of the WHOLE walk, never as one longer step per
        // instance: two codes an authored frame apart would otherwise land in one pass and be
        // ordered by the walk (docs/formats/anim-definitions/cutscenes.md).
        int passes = FastForward is { Scoped: true } rate && rate.Rate > 1f
            ? Mathf.CeilToInt(rate.Rate)
            : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            WalkInstances(dt, passes, pass == passes - 1);
        }
    }

    /// <summary>Stops every live instance of an animation name (optionally only on one anchor) and
    /// tears down the resources it created. Removing the instance alone would leave its motions
    /// driving nodes, its puffers emitting, its lights lit and its sounds playing;
    /// <see cref="TearDownResourcesOf"/> clears all four. ⚠ <see cref="Start"/>'s own restart must
    /// not route through here; it keeps the resources so re-assertion is a no-op.</summary>
    public void Stop(string? animName, Node3D? anchor = null) =>
        RemoveInstances(animName, anchor, tearDown: true);

    // ---- world-effects runtime ----
    /// <summary>Does this runtime's program hold a definition for an effect animation name? The
    /// world runtime tests this before routing a death's CALL_ANIMATION here, so only the curated
    /// impact/destruction effects are handed off (doors and other calls fall through).</summary>
    public bool Handles(string animName) => _program.ByAnimName(animName).Count > 0;

    /// <summary>The animation's current runtime state in the mission script's own numbering
    /// (<c>ANIM_STATE</c>, docs/formats/objectives.md): <c>RUNNING</c> 2 while any definition of
    /// that name has a live instance, <c>INVALID</c> 4 while one is latched off, <c>EXECUTED</c> 3
    /// once one has run and finished, and 0 for a name this program never carried.</summary>
    public int AnimStateOf(string animName)
    {
        var defs = _program.ByAnimName(animName);
        if (defs.Count == 0)
            return 0;
        foreach (var inst in _instances)
            if (defs.Contains(inst.Def))
                return 2;
        bool started = false;
        foreach (var def in defs)
        {
            if (_invalidated.Contains(def))
                return 4;
            started |= _everStarted.Contains(def);
        }
        return started ? 3 : 0;
    }

    /// <summary>How often one IF/ELSEIF condition kind has evaluated true and false so far, the
    /// same census <c>ReportConditions</c> logs. A suite reads it to say whether a gate has
    /// opened at all, which no node pose can distinguish from a gate that opened and did
    /// nothing.</summary>
    public (int True, int False) ConditionTally(string kind) =>
        _conditions.TryGetValue(kind, out var c) ? c : (0, 0);

    /// <summary>The definitions carrying one ANIMATION_NAME, the same lookup
    /// <c>CALL_ANIMATION</c> dispatch uses, exposed so the zeppelin damage runtime can register
    /// a record's destroy anim as a destructible pool (M4 F18).</summary>
    public IReadOnlyList<AnimDefinition> DefsFor(string animName) => _program.ByAnimName(animName);

    /// <summary>Every definition reachable from <paramref name="animName"/> through
    /// <c>CALL_ANIMATION</c>, itself included: the cutscene host reads which of them author a
    /// <c>CALLBACK</c>, since a row definition can end before the callee carrying its handoff.</summary>
    public IReadOnlyList<AnimDefinition> CallClosureOf(string animName) =>
        _program.Subset(animName).Defs;

    /// <summary>Stages the named effect at a world point: each matching template root moves onto it,
    /// then the definition starts. The basis is <paramref name="orient"/>, or <paramref name="callOrient"/>
    /// for its <see cref="OrientedCallAnimNames"/> callees. The <paramref name="inputNode"/> argument is
    /// the callee's INPUT_NODE, read by its <c>NodeActive</c> gate. With <paramref name="follow"/> the
    /// copy keeps riding it. ⚠ Give an input-governed def no TTL (its lifetime is authored); others
    /// take <paramref name="ttl"/> or <see cref="EffectTtl"/>.</summary>
    public bool PlayEffectAt(string animName, Vector3 worldPoint, Node3D? inputNode = null,
        float ttl = 0f, Basis? orient = null, bool follow = false, Basis? callOrient = null)
    {
        float bound = ttl > 0f ? ttl : EffectTtl;
        bool matched = false;
        // Restored rather than cleared. A nested play (a callee that plays an effect of its own)
        // must hand its caller's orientation back, not leave the runtime unoriented.
        var outerCallOrient = _callPlacementOrient;
        _callPlacementOrient = callOrient;
        try
        {
            // The checkout (TakeNextSlot) plus the Start it feeds, coarse over
            // the (usually one) def this anim name resolves to, not per particle.
            using (PerfSample.Scope(PerfSite.EffectCheckout))
            {
                foreach (var def in _program.ByAnimName(animName))
                {
                    // Anchor on the def's own template root when it resolves: its at_node and
                    // motion targets live under it. Null falls back to global name resolution.
                    // Pooled, that root is this call's slot, and only that copy moves onto the site.
                    var roots = _templateStage.TakeNextSlot(def);
                    if (follow && inputNode != null && IsInstanceValid(inputNode))
                        _templateStage.PlaceFollowing(roots, inputNode, worldPoint, LevelsTemplate(def));
                    else
                        _templateStage.PlaceOn(roots, worldPoint, LevelsTemplate(def), orient);
                    // ⚠ Before Start, every play. The copy is a reused node tree, not the fresh one
                    // the original instances per call. Its last run leaves it in its END pose.
                    ResetCheckedOutCopies(animName, roots);
                    var anchor = roots.FirstOrDefault();
                    // The original clones a fresh, detached light per call (FUN_00520910), so a
                    // reused copy's lights start dark. Without this, gunhit_lt (lit on a 20% roll
                    // with no INACTIVE) would stay lit on every later hit that missed the roll.
                    if (anchor != null)
                        Light.DiscardFor(anchor);
                    bool governed = inputNode != null && IsInstanceValid(inputNode)
                                    && DefConditionsOnInputNode(def);
                    if (governed)
                    {
                        Stop(def.AnimName, anchor);
                        _inputNodes[(def, anchor)] = inputNode!;
                    }
                    Start(def, anchor);
                    // After Start, not with the placement. A governed def's Stop above hides the
                    // root again, so this must be the last word on it for the instance now running.
                    _templateStage.Reveal(def, anchor, visible: true);
                    matched = true;
                    if (!governed && bound > 0f)
                    {
                        // One deadline per (def, anchor). A replay restarts the instance, so an
                        // older entry would stop the NEW instance at the OLD deadline. A second gun
                        // hit 0.2 s after the first would then emit for 0.1 s.
                        _effectTtls.RemoveAll(t => t.Def == def && t.Anchor == anchor);
                        _effectTtls.Add((def, anchor, _effectClock + bound));
                    }
                }
            }
        }
        finally
        {
            _callPlacementOrient = outerCallOrient;
        }
        return matched;
    }

    /// <summary>Builds every emitter this program's <c>PUFFER_STATE 1</c> events can build before
    /// anything plays, and starts or poses nothing. A named <c>at_node</c> resolves to every node
    /// of that name in this runtime's scope, one emitter per pool copy. A call-site host
    /// (<c>INPUT_NODE</c>) is every node a <c>CALL_ANIMATION</c> targets the def with, the def's
    /// own staged copies, and <paramref name="callSiteAnchors"/>. Call once after <see cref="Bind"/>.
    /// ⚠ For the crash-rig and world-effects runtimes only, never the ambient world runtime.</summary>
    public EmitterPrewarm PrewarmEmitters(params Node3D[] callSiteAnchors)
    {
        int built = 0, unhosted = 0, selfHosted = 0;
        int at = 0;
        while (at < _program.Defs.Count)
        {
            at = PrewarmSlice(at, int.MaxValue, out var slice, callSiteAnchors);
            built += slice.Built;
            unhosted += slice.Unhosted;
            selfHosted += slice.SelfHosted;
        }

        return new EmitterPrewarm(built, unhosted, selfHosted);
    }

    /// <summary>One slice of <see cref="PrewarmEmitters"/>: the defs from <paramref name="from"/>
    /// on, stopping once <paramref name="budget"/> emitters have been built, and answering the def
    /// index to carry on at. A caller with a frame to spend steps it, so a rig's couple of hundred
    /// emitters spread over frames rather than landing on one. A def is never split, so the slice
    /// may overrun the budget by one def's worth.</summary>
    public int PrewarmSlice(int from, int budget, out EmitterPrewarm warmed,
        params Node3D[] callSiteAnchors)
    {
        var callTargets = _callTargets ??= CallTargetNames();
        int built = 0, unhosted = 0, selfHosted = 0;
        int at = Math.Max(0, from);
        for (; at < _program.Defs.Count && built < budget; at++)
        {
            var def = _program.Defs[at];
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind != "PufferState" || (ev.Data.Num("active_state") ?? 0f) < 1f
                        || ev.Data.Str("name") is not { } name || !ev.Data.Objects("textures").Any())
                        continue;
                    IEnumerable<Node3D> hosts;
                    if (ev.Data.Str("at_node") is not { } atNode || IsSelfNodeRef(atNode))
                    {
                        selfHosted++;
                        hosts = CallSiteHostsOf(def, callTargets, callSiteAnchors);
                    }
                    else
                    {
                        // Unfiltered: a play anchors this def inside its CALLER's copy, which is
                        // exactly what StagingAdmits rejects with no scope. Over-counting costs one
                        // idle emitter, under-counting the frame this exists to save.
                        hosts = FindAll(atNode, null);
                    }
                    int hosted = 0;
                    foreach (var host in hosts)
                    {
                        hosted++;
                        if (Emitters.Prewarm(name, host, def, ev.Data))
                            built++;
                    }
                    if (hosted == 0)
                        unhosted++;
                }
            }
        }
        warmed = new EmitterPrewarm(built, unhosted, selfHosted);
        return at;
    }

    /// <summary>Applies weapon damage to whatever destructible a struck world node belongs to, and
    /// escalates its visible damage. <paramref name="struck"/> resolves to the owning instance by
    /// walking up to the nearest registered anchor. World destructibles carry HEALTH only, with no
    /// armour pool (docs/formats/destructibles.md). At zero it marks the instance destroyed and
    /// runs the death sequence. Returns true when the hit landed on a destructible. A dormant pool
    /// is out of the world, so the hit finds nothing and returns false, whatever the caller.</summary>
    public bool DamageAt(Node? struck, float healthDamage)
    {
        var inst = _destructibles.Resolve(struck);
        if (inst == null)
            return false;
        // Read live, never captured: a pool dormant at mission start wakes later and must then
        // take damage. Checked before Destroyed so out-of-the-world wins unconditionally.
        if (inst.Dormant)
            return false;
        if (inst.Status == DestructibleRegistry.State.Destroyed)
            return true;   // already dead, the death sequence owns it from here
        if (DamageReplicated)
            return true;   // struck, but another machine decides what it cost
        SpendHealth(inst, inst.Health - Math.Max(0f, healthDamage), healthDamage);
        return true;
    }

    /// <summary>Sets a destructible to the health another machine decided, through the same
    /// stages and death a local hit takes. Only ever lowers it. False when the pool is out of the
    /// world, already dead, or at or below <paramref name="health"/> already.</summary>
    public bool ApplyReplicatedHealth(DestructibleRegistry.Instance inst, float health)
    {
        ArgumentNullException.ThrowIfNull(inst);
        if (inst.Dormant || inst.Status == DestructibleRegistry.State.Destroyed || health >= inst.Health)
            return false;
        SpendHealth(inst, health, inst.Health - health);
        return true;
    }

    /// <summary>Puts a destructible into the state an earlier mission left it in, silently: the
    /// pool reads destroyed at HP 0 (or the carried HP at its damage stage) and its nodes take the
    /// pose the death ends in, with no effects, sounds or choreography. Returns false when there is
    /// nothing to carry. ⚠ Never route a carried state through <see cref="DamageAt"/>; that
    /// replays the death at mission open (docs/formats/destructibles.md "Starting destroyed").</summary>
    public bool CarryState(DestructibleRegistry.Instance inst, bool destroyed, float health)
    {
        if (inst.Status == DestructibleRegistry.State.Destroyed)
            return false;
        if (!destroyed && health >= inst.Health)
            return false;
        inst.Health = destroyed ? 0f : Math.Max(0f, health);
        var stages = inst.Def.Sequences.FirstOrDefault(s =>
            string.Equals(s.Name, DamageSequenceName, StringComparison.OrdinalIgnoreCase));
        if (stages != null)
            inst.DamageStage = Math.Max(inst.DamageStage, DamageStageFor(stages, inst.Health));
        if (!destroyed)
        {
            inst.Status = DestructibleRegistry.State.Damaged;
            return true;
        }
        inst.Status = DestructibleRegistry.State.Destroyed;
        ApplyDeathPose(inst);
        return true;
    }

    /// <summary>A plane collision with a world node. EVERY destructible takes the damage, through
    /// <see cref="DamageAt"/>, so the death is identical to a weapon kill; the return value says
    /// only what happens to the PLANE, true for a <c>WeaponOrCollideHit</c> object it flies
    /// THROUGH and false for a solid one it grazes or crashes on.
    /// ⚠ <c>ACTIVATION</c> gates the plane's fate, never the object's. A damage-side gate here
    /// would leave every rammed building untouched (docs/formats/destructibles.md).</summary>
    public bool CollideDamageAt(Node? struck, float healthDamage)
    {
        var inst = _destructibles.Resolve(struck);
        if (inst == null)
        {
            return false;
        }
        bool flyThrough =
            inst.Def.Activation.Equals("WeaponOrCollideHit", StringComparison.OrdinalIgnoreCase);
        if (CollideDamageGate != null && !CollideDamageGate(inst))
        {
            return flyThrough;   // refused the damage, not the contact
        }
        DamageAt(struck, healthDamage);
        return flyThrough;
    }

    /// <summary>Returns a destroyed destructible to healthy, for the debug tools and respawn: stop
    /// the live death, restore the authored pose of every node it moved, re-apply the def's
    /// <c>RESET_STATE</c>, and restore the HP pool. ⚠ All four steps are needed for idempotence.
    /// Skipping the pose restore leaves the ballistic pieces wherever they flew, so a re-destroy
    /// launches from the wrong place, whether or not a chained death call has fired yet.</summary>
    public void ResetDestructible(DestructibleRegistry.Instance inst)
    {
        var def = inst.Def;
        Stop(def.AnimName, inst.Anchor);
        // ⚠ Re-apply a called def's OWN reset state, on the anchor its call actually ran on (a
        // pooled copy, not necessarily inst.Anchor). It is never inherited from the caller, so
        // without it an ACTIVE_STATE the call flipped on sits inert but still shown.
        void ResetCalled(AnimDefinition called, Node3D calledAnchor)
        {
            Stop(called.AnimName, calledAnchor);
            RestoreRestPoses(called, calledAnchor);
            if (called.ResetState != null)
                ApplyInstant(called.ResetState.Events, called, calledAnchor);
        }
        if (inst.ChainedDeathDef is { } chained)
        {
            ResetCalled(chained, inst.Anchor);
            inst.ChainedDeathDef = null;
        }
        // Every CALL_ANIMATION target the death dispatched onto its own anchor, reset the same way,
        // so the result does not depend on whether the called def's motions had finished.
        foreach (var (local, localAnchor) in inst.LocalCallTargets)
            ResetCalled(local, localAnchor);
        inst.LocalCallTargets.Clear();
        // ⚠ The reset must stop the damage-stage effects itself. They live on the external runtime
        // and loop while the healthy node stays active, which a heal never interrupts. Take the
        // names from the def's own DAMAGE_SEQUENCE calls, never from a hardcoded list.
        if (ExternalEffectStop != null
            && inst.DamageStage > 0
            && def.Sequences.FirstOrDefault(s =>
                string.Equals(s.Name, DamageSequenceName, StringComparison.OrdinalIgnoreCase)) is { } damageSeq)
        {
            foreach (var ev in damageSeq.Events)
                if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } stageEffect)
                    ExternalEffectStop(stageEffect);
        }
        RestoreRestPoses(def, inst.Anchor);
        if (def.ResetState != null)
            ApplyInstant(def.ResetState.Events, def, inst.Anchor);
        inst.Health = inst.MaxHealth;
        inst.Status = DestructibleRegistry.State.Healthy;
        inst.DamageStage = 0;
    }

    // ---- state application ----
    /// <summary>Clamps each near-zero scale component to <see cref="MinPoseScale"/> (sign
    /// preserved) so an animated pose can never write a singular basis. Applied at every
    /// pose-scale write site: <c>PoseScale</c> and <c>FromToMotion.Seek</c>.</summary>
    internal static Vector3 NonSingularScale(Vector3 s) => new(
        Mathf.Abs(s.X) < MinPoseScale ? (s.X < 0f ? -MinPoseScale : MinPoseScale) : s.X,
        Mathf.Abs(s.Y) < MinPoseScale ? (s.Y < 0f ? -MinPoseScale : MinPoseScale) : s.Y,
        Mathf.Abs(s.Z) < MinPoseScale ? (s.Z < 0f ? -MinPoseScale : MinPoseScale) : s.Z);

    // `internal` (not `private`) so EmitterDirector's debug lines name their nodes the same way
    // every other anim log line does, same-assembly only, no wider exposure intended.
    internal static string NameOf(Node3D n) =>
        n.HasMeta(NameMeta) ? n.GetMeta(NameMeta).AsString() : n.Name.ToString();

    /// <summary>Where a world node visually IS, for effect siting and puffer emission. ⚠ Do not
    /// use <c>GlobalPosition</c> for this: an absolute-modelled gamez subtree holds its vertices in
    /// world space under an identity transform, so its origin is the map corner. When the origin
    /// lies outside the subtree's world mesh bounds this returns the bounds centre; a node with a
    /// real transform keeps its origin exactly, and a meshless node has no bounds.</summary>
    internal static Vector3 VisualOriginOf(Node3D node)
    {
        var box = UI.SelectionService.SubtreeWorldAabb(node);
        if (box.Size.LengthSquared() <= 1e-9f)
            return node.GlobalPosition;
        return box.Grow(1f).HasPoint(node.GlobalPosition) ? node.GlobalPosition : box.GetCenter();
    }

    // A node's world transform, valid DURING the bootstrap too, the same detached-subtree problem
    // WorldPos solves, but keeping the basis so an AT_NODE offset still rotates into place.
    // composed reports whether the ancestor chain had to be walked (the world root not yet
    // parented), so the caller can log the fallback rather than let Godot spam !is_inside_tree()
    // and return an origin transform.
    internal static Transform3D WorldTransform(Node3D node, out bool composed)
    {
        composed = !node.IsInsideTree();
        if (!composed)
            return node.GlobalTransform;
        var xform = node.Transform;
        for (var p = node.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D)
            xform = p.Transform * xform;
        return xform;
    }

    // INACTIVE = invisible and non-collidable, the whole subtree. Only visibility is written:
    // world colliders derive their Disabled flag from it (WorldCollision), which is what makes
    // an activation INSIDE an already-hidden subtree stay non-collidable. `internal` so the pose
    // family's OBJECT_ACTIVE_STATE handler writes the same rule the bootstrap passes do.
    internal static void SetSubtreeActive(Node3D node, bool active)
    {
        node.Visible = active;
    }

    // Moves a child under a new parent keeping its LOCAL transform, which is what makes the new
    // parent's frame the one the child's keyframes are read in. `internal` so the cutscene host
    // undoes a definition's own reparent the same way the runtime made it.
    // ⚠ Detach and attach rather than Node.Reparent: an intro reparents during the animation
    // bootstrap, where the world root is not yet in the scene tree and Reparent refuses.
    internal static void Reparent(Node3D child, Node3D parent)
    {
        var local = child.Transform;
        child.GetParent()?.RemoveChild(child);
        parent.AddChild(child);
        child.Transform = local;
    }

    // The world node a gamez node index was built into, or null when this build never created it.
    // The one way to reach a node the caller cannot name, which is what an area-selected toggle
    // needs: the verb carries a rectangle and no name at all.
    internal Node3D? FindNodeByIndex(int gamezIndex) => _resolver.ByGamezIndex(gamezIndex);

    // The subtree toggle by gamez node index, which is how the mission script's area verb reaches
    // its selection: it names no node at all. A node the world build never created is skipped,
    // exactly as an unresolved name is.
    internal void SetSubtreeActiveByIndex(int gamezIndex, bool active)
    {
        if (FindNodeByIndex(gamezIndex) is { } node)
        {
            SetSubtreeActive(node, active);
        }
    }

    /// <summary>Indexes one pooled copy of a library-root call template: everything
    /// <see cref="IndexStage"/> does, except the resolver's by-index map.
    /// ⚠ Never add a pooled copy to that map. Every copy carries the same compiled node indices, so
    /// the map holds only the first claimant and a second copy's events would resolve onto the
    /// first copy's nodes. Anchor-scoped name resolution has no such collision, which is why a copy
    /// resolves against itself as long as it is passed as the anchor.</summary>
    internal void IndexPooledCopy(Node3D subtree)
    {
        // ⚠ Retire first, as IndexRebasedStage does: a copy is built mid-session, so the maps it
        // joins may already key a call-site anchor the world has freed since the last one.
        RetireFreedNodes();
        _templateStage.IndexPooledCopy(subtree);
        PrimeRest(subtree);
        _stagedCopies.Add(subtree);
    }

    /// <summary>Starts a definition on one anchor (null resolves its node names globally), running
    /// every sequence that is not ACTIVATION ON_CALL. Starting a def already live on the same
    /// anchor restarts it; CALL_ANIMATION deliberately does not take this path for a running
    /// animation. <paramref name="protectSelfInvalidate"/> is the death path's opt-in against a
    /// same-name self stop tearing this burst's own instance down; keep it off elsewhere
    /// (<see cref="_startingInstances"/>).</summary>
    internal void Start(AnimDefinition def, Node3D? anchor, bool protectSelfInvalidate = false)
    {
        // ⚠ Keep the depth bound. CALL_ANIMATION chains are data and some chapters author cycles,
        // which recurse until the stack dies, since a start fires its t=0 events immediately.
        if (_startDepth >= MaxStartDepth)
        {
            Count("CallAnimation(depth limit)");
            return;
        }
        // ⚠ Keep this refusal; it is where INVALIDATE_ANIMATION's whole effect lands. Without it a
        // def that invalidated itself would still re-run on the next call.
        if (_invalidated.Contains(def))
        {
            Count("Start(invalidated)");
            return;
        }
        // The node-state prerequisite is the data's own fork: CM07's hangar drop calls both camera
        // legs and lets hdrop_direction's state pick one, so an unmet leg is skipped in silence,
        // the way the hull-death gate is.
        if (!NodePrerequisitesMet(def, anchor))
        {
            Count("Start(prerequisite unmet)");
            return;
        }
        // ⚠ Do not tear down the live resources on a restart; the new instance re-establishes them
        // idempotently, and tearing down rebuilds every one of them instead. A caller that wants
        // them cleared calls Stop directly.
        RemoveInstances(def.AnimName, anchor, tearDown: false);
        _everStarted.Add(def);
        var inst = new AnimInstance(def, anchor);
        foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
            inst.AddRunner(seq);
        if (inst.Finished)
            return;
        _instances.Add(inst);
        OnInstanceStarted?.Invoke(def, anchor);
        // A call made by the tick's own walk hands its callee to the drain that closes the walk;
        // everything else fires whatever is due at t=0 immediately, so instantaneous sequences
        // (zepstate's active-state roster) settle during the build rather than one frame later.
        if (QueueFirstAdvance(inst, protectSelfInvalidate))
            return;
        AdvanceStarted(inst, protectSelfInvalidate);
    }

    /// <summary>Stops every live instance and clears the effect-TTL list, a full reset of what
    /// PlayEffectAt started, so the verify measures each effect in a clean window (these effects
    /// share puffer names/hosts, so a lingering one would contaminate the next). Not used in play.</summary>
    internal void StopAll()
    {
        foreach (var name in _instances.Select(i => i.Def.AnimName).Distinct().ToList())
            Stop(name);
        _effectTtls.Clear();
    }

    /// <summary>Escalates a destructible's visible damage to the stage its current HP sits in,
    /// running its <c>DAMAGE_SEQUENCE</c> (docs/formats/destructibles.md). Call it after the HP
    /// changes; it neither decrements HP nor runs the death sequence. Returns false when no new
    /// threshold was crossed, or the def carries no such sequence.
    /// ⚠ Keep the stage gate. Without it a one-shot damage effect re-fires on every hit inside a
    /// band; only a sustained one is spared by CALL_ANIMATION's own live guard.</summary>
    internal bool ApplyDamageStages(DestructibleRegistry.Instance inst)
    {
        var seq = inst.Def.Sequences.FirstOrDefault(s =>
            string.Equals(s.Name, DamageSequenceName, StringComparison.OrdinalIgnoreCase));
        if (seq == null)
            return false;
        int stage = DamageStageFor(seq, inst.Health);
        if (stage <= inst.DamageStage)
            return false;
        inst.DamageStage = stage;
        if (inst.Status == DestructibleRegistry.State.Healthy)
            inst.Status = DestructibleRegistry.State.Damaged;
        // A one-shot selector, not a persistent instance: the cascade carries no timed events, so
        // one zero-dt advance resolves the whole IF chain. The effect it starts becomes its own
        // live instance and this host is discarded.
        var host = new AnimInstance(inst.Def, inst.Anchor);
        host.AddRunner(seq);
        host.Advance(this, 0f);
        return true;
    }

    /// <summary>Hands this instance the velocity a <c>Callback 16</c> carries, on the original's own
    /// terms (<c>FUN_004ee0e0</c>): the vector is stored whatever it is, and it ARMS only if some
    /// axis exceeds 0.01.
    /// ⚠ Below that the arm is CLEARED, not left alone. A near-stationary <c>Callback 16</c>
    /// disarms one an earlier callback armed, so this cannot collapse into "non-zero means armed".
    /// </summary>
    internal void ArmInheritedVelocity(Vector3 v)
    {
        InheritedWorldVelocity = v;
        InheritedVelocityArmed = Mathf.Abs(v.X) > InheritedVelocityEpsilon
            || Mathf.Abs(v.Y) > InheritedVelocityEpsilon
            || Mathf.Abs(v.Z) > InheritedVelocityEpsilon;
    }

    /// <summary>The node's authored pose, remembered the first time anything moves it, so
    /// every pose op stays an offset from the rest pose rather than compounding.</summary>
    internal Transform3D RestOf(Node3D node)
    {
        if (!_rest.TryGetValue(node, out var rest))
            _rest[node] = rest = node.Transform;
        return rest;
    }

    // Thin forwards into the pose family, kept here because their callers name this runtime:
    // MotionRuntime.Create reads the landing-resume mark as `rt.ConsumeLandingResume`, the
    // `ground-contact` suite arms it through `runtime.MarkLandingResume`, and OpacityFade and the
    // zeppelin dormancy pose write through `rt.SetSubtreeOpacity`. The state and the bodies live
    // in PoseChannel.
    internal bool ConsumeLandingResume(Node3D target) => Pose.ConsumeLandingResume(target);

    internal void MarkLandingResume(Node3D target) => Pose.MarkLandingResume(target);

    internal void SetSubtreeOpacity(Node3D node, float alpha) => Pose.SetSubtreeOpacity(node, alpha);

    /// <summary>Delivers this runtime's <c>LIGHT_STATE</c> lights into a set another runtime owns,
    /// the world runtime's. Both then rank in one frame's commit rather than erasing each other.
    /// Null disconnects.</summary>
    internal void ContributeLightsTo(WorldLights? lights)
    {
        Lights = lights;
        LightsCommittedElsewhere = lights != null;
    }

    /// <summary>Every light this runtime has declared, with its live range and colour.</summary>
    internal List<(string Name, bool Active, float RangeMin, float RangeMax, Color Color)> LightSnapshot() =>
        Light.Snapshot();

    // How many of a DAMAGE_SEQUENCE's health thresholds hp has fallen at or below, the object's
    // current damage stage. Monotonic in falling HP, so it is a safe escalation gate.
    private static int DamageStageFor(AnimSequence seq, float hp)
    {
        int stage = 0;
        foreach (var ev in seq.Events)
        {
            if (ev.Kind != "If" && ev.Kind != "Elseif")
                continue;
            if (DamageThreshold(ev.Data.Obj("condition")) is { } t && hp <= t)
                stage++;
        }
        return stage;
    }

    // The HP a health condition first becomes true at as HP falls: ANIM_HEALTH's operand, or a
    // range's upper bound (its lower bound is left to EvaluateCondition when the cascade actually
    // runs). Null for a non-health condition, which does not stage.
    private static float? DamageThreshold(AnimData? condition)
    {
        if (condition?.Union() is not { } union)
            return null;
        var (kind, value) = union;
        return kind switch
        {
            "AnimHealth" => AnimData.AsNum(value),
            "AnimHealthRange" => value is Dictionary<string, object?> fields
                                 ? new AnimData(fields).Num("max")
                                 : null,
            _ => null,
        };
    }

    // The node name an event targets, node for most compiled kinds (ObjectMotion), name for others
    // (ObjectActiveState's hand-authored shape, ObjectMotionFromTo,
    // ObjectOpacityFromTo/ObjectOpacityState). Matches the exact healthy/destroyed/dbase role words
    // where that matters, never a _dest suffix (docs/formats/destructibles.md).
    private static string RoleName(AnimEvent ev) => ev.Data.Str("node") ?? ev.Data.Str("name") ?? "";

    // Does this event come from the definition's RESET_STATE rather than one of its sequences?
    // Reference identity on the authored event, since a baseline and a death name the same roles.
    private static bool AuthoredInResetState(AnimDefinition def, AnimEvent ev) =>
        def.ResetState is { } reset && reset.Events.Contains(ev);

    // The name of def's own healthy-role node, for DestructibleKilled: the node an OBJECT_ACTIVE_
    // STATE switches off in def's own Initial sequences (the visible-death case), or else the one
    // RESET_STATE holds ACTIVE (the RESET-derived swap ApplyDeathSwap plays instead). Null for a
    // def that authors no healthy/destroyed pair at all, which most destructibles do not.
    private static string? HealthyNodeNameOf(AnimDefinition def)
    {
        foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
            foreach (var ev in seq.Events)
                if (ev.Kind == "ObjectActiveState" && !ev.Data.Bool("state")
                    && RoleName(ev).Contains("healthy", StringComparison.OrdinalIgnoreCase))
                    return RoleName(ev);
        if (def.ResetState is { } reset)
            foreach (var ev in reset.Events)
                if (ev.Kind == "ObjectActiveState"
                    && RoleName(ev).Contains("healthy", StringComparison.OrdinalIgnoreCase))
                    return RoleName(ev);
        return null;
    }

    // Does target's own sequences author the healthy/destroyed swap, an OBJECT_ACTIVE_STATE that
    // activates a destroyed/dbase-role node or deactivates a healthy-role one? Used by
    // ChainedSwapTarget to find a CALL_ANIMATION target that owns the swap the caller's own
    // RESET-derived fallback would otherwise fire early.
    private static bool AuthorsSwap(AnimDefinition target) =>
        target.Sequences.Any(seq => seq.Events.Any(ev =>
        {
            if (ev.Kind != "ObjectActiveState")
                return false;
            var name = RoleName(ev);
            bool active = ev.Data.Bool("state");
            return (active && (name.Contains("destroyed", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("dbase", StringComparison.OrdinalIgnoreCase)))
                || (!active && name.Contains("healthy", StringComparison.OrdinalIgnoreCase));
        }));

    // Does def's own Initial sequences author a visible death on a node outside the
    // healthy/destroyed/dbase role set: a piece moved, faded or switched off? ⚠ RunDeathSequence
    // uses this to withhold ApplyDeathSwap's RESET-derived rescue. A def whose death look is
    // authored that way must keep it; the rescue is only for a def with nothing but puffer calls,
    // where the kill would otherwise be invisible.
    private static bool AuthorsVisibleDeath(AnimDefinition def) =>
        def.Sequences.Where(s => !s.OnCallOnly).Any(seq => seq.Events.Any(ev =>
        {
            bool isDeathKind = ev.Kind is "ObjectMotionFromTo" or "ObjectMotion"
                or "ObjectOpacityFromTo" or "ObjectOpacityState"
                or "ObjectActiveState";
            if (!isDeathKind)
                return false;
            var name = RoleName(ev);
            if (name.Length == 0)
                return false;
            bool isRoleNode = name.Contains("healthy", StringComparison.OrdinalIgnoreCase)
                || name.Contains("destroyed", StringComparison.OrdinalIgnoreCase)
                || name.Contains("dbase", StringComparison.OrdinalIgnoreCase);
            if (isRoleNode)
                return false;
            // An ACTIVE_STATE only counts switching a piece OFF, turning one ON authors
            // nothing visible on its own (and would otherwise flag every def with an
            // unrelated startup toggle).
            return ev.Kind != "ObjectActiveState" || !ev.Data.Bool("state");
        }));

    private static string Describe(object? value) => value switch
    {
        null => "",
        Dictionary<string, object?> d => string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}")),
        _ => value.ToString() ?? "",
    };

    // A node's world position, valid DURING the bootstrap too. The world subtree is still detached
    // while the bootstrap passes run (GameSession parents it after the build), and Godot's
    // GlobalPosition both returns identity and logs an error for a node outside the tree, one line
    // per evaluation, which is thousands. Accumulate the local transforms instead; the world node
    // itself rests at the origin, so the result is the same number either way.
    private static Vector3 WorldPos(Node3D node)
    {
        if (node.IsInsideTree())
            return node.GlobalPosition;
        var xform = node.Transform;
        for (var p = node.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D)
            xform = p.Transform * xform;
        return xform.Origin;
    }

    private static Vector3I CheckCellOf(Vector3 pos) => new(
        Mathf.FloorToInt(pos.X / RangeCheckCellSize),
        Mathf.FloorToInt(pos.Y / RangeCheckCellSize),
        Mathf.FloorToInt(pos.Z / RangeCheckCellSize));

    // The AT_NODE and condition-node sentinels for "the node this definition was invoked on"; both
    // resolve to the anchor. The compiled u32 form and why it is matched by magnitude rather than
    // equality: docs/formats/anim-definitions.md.
    private static bool IsSelfNodeRef(string name) =>
        string.Equals(name, "INPUT_NODE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MAIN_ROOT_NODE", StringComparison.OrdinalIgnoreCase);

    // The stage's raw slot walk (the engine half of TemplateStage.SlotOf, which memoizes
    // it): the nearest ancestor carrying PoolSlotMeta, or -1.
    private static int SlotMarkOf(Node3D node)
    {
        for (Node? n = node; n != null; n = n.GetParent())
            if (n.HasMeta(PoolSlotMeta))
                return (int)n.GetMeta(PoolSlotMeta);
        return -1;
    }

    // The stage's one placement write (see TemplateStage.PlaceOn for why TopLevel, the
    // same family as the puffer TopLevel fix).
    private static void PlaceNodeAt(Node3D root, Transform3D xf)
    {
        root.TopLevel = true;
        root.GlobalTransform = xf;
    }

    /// <summary>Whether this start's first advance belongs at the tail of the tick's instance walk,
    /// which is where the original reaches a definition started during a walk.
    /// ⚠ Only a call the walk itself dispatched qualifies. A bootstrap, a mission or range trigger
    /// and a death burst all need their t=0 events inside the bracket that started them, which
    /// <see cref="RunDeathSequence"/> reads through <see cref="_dyingInstances"/>.</summary>
    private bool QueueFirstAdvance(AnimInstance inst, bool protectSelfInvalidate)
    {
        if (!_walkingInstances || _deathCallDepth > 0 || _rangeCallDepth > 0
            || _missionCallDepth > 0)
        {
            return false;
        }
        _queuedStarts.Add((inst, protectSelfInvalidate));
        return true;
    }

    /// <summary>Runs the first advance of every definition a <c>CALL_ANIMATION</c> started during
    /// this tick's walk, in the order the calls were made, as far as one walk's budget reaches.
    /// The original's dispatcher appends a started instance to the tail of the list it is walking
    /// and re-reads the link after every callback, so a callee is reached in the same pass, after
    /// the caller's remaining events.
    /// Decode: docs/org/sequences.md.</summary>
    private void DrainQueuedStarts()
    {
        for (int started = 0; _queuedStarts.Count > 0; started++)
        {
            if (started >= MaxQueuedStarts)
            {
                // ⚠ Leave the rest QUEUED, never cleared. A start still on the queue is owed its
                // zero-dt advance, and the next walk owes it the same one; dropping it hands the
                // instance a real dt as its first advance, which AdvanceStarted forbids.
                Count("CallAnimation(tick start budget)");
                return;
            }
            var (inst, protect) = _queuedStarts[0];
            _queuedStarts.RemoveAt(0);
            // A later event of the caller can stop what an earlier one started, and the original's
            // dispatcher clears such a node rather than calling it.
            if (!_instances.Contains(inst))
                continue;
            AdvanceStarted(inst, protect);
        }
    }

    /// <summary>The t=0 burst of a just-started instance: its own events fire under the
    /// self-invalidate guard, and an instance the burst drained retires here.
    /// ⚠ Keep the advance at zero dt. A definition's sequences are not charged the delta of the
    /// pass they were started in, and the instance clock they gate against ticks with them
    /// (docs/org/sequences.md).</summary>
    private void AdvanceStarted(AnimInstance inst, bool protectSelfInvalidate)
    {
        _startDepth++;
        _startingInstances.Push((inst, protectSelfInvalidate));
        try
        {
            inst.Advance(this, 0f);
        }
        finally
        {
            _startingInstances.Pop();
            _startDepth--;
        }
        // The t=0 events can finish the instance, so notify only a finish that removed something:
        // the timeline needs start and finish notifications balanced against the live count.
        if (Retirable(inst) && _instances.Remove(inst))
        {
            FinishInputGoverned(inst.Def, inst.Anchor);
            OnInstanceFinished?.Invoke(inst.Def, inst.Anchor);
        }
    }

    /// <summary>INVALIDATE_ANIMATION: latches an animation off without touching what it is doing.
    /// Whatever is running keeps running to its own end; what changes is that nothing can start it
    /// again until a RESET_ANIMATION (or an explicit <see cref="Play"/>) clears the latch. See
    /// <see cref="_invalidated"/> for the state machine this stands in for.</summary>
    private void Invalidate(string? animName)
    {
        if (string.IsNullOrEmpty(animName))
            return;
        foreach (var def in _program.ByAnimName(animName))
            _invalidated.Add(def);
    }

    /// <summary>RESET_ANIMATION: clears the invalidation latch and re-poses the definition's
    /// RESET_STATE on each of its anchors. Does NOT start anything, the original's reset is
    /// state-plus-pose only, and the one authored user (`turnoff_fliteN`) issues its own
    /// STOP_ANIMATION first.</summary>
    private void ResetAnimation(string? animName)
    {
        if (string.IsNullOrEmpty(animName))
            return;
        foreach (var def in _program.ByAnimName(animName))
        {
            _invalidated.Remove(def);
            if (def.ResetState == null)
                continue;
            foreach (var anchor in Anchors(def))
                ApplyInstant(def.ResetState.Events, def, anchor);
        }
    }

    private void Bootstrap(Node3D worldRoot, AnimProgram program)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _root = worldRoot;
        _program = program;
        _callTargets = null;
        // ⚠ Hand these flags over before the first Add/Anchors call; they are construction-time
        // facts about this runtime. The census covers the bootstrap passes only.
        _resolver.NameResolveFallback = NameResolveFallback;
        _resolver.SuppressRootLift = SuppressRootLift;
        _resolver.ReportResolution = ReportResolution;
        _resolver.OpenCensus();
        IndexWorld(worldRoot);
        long indexMs = sw.ElapsedMilliseconds;

        // Pass 0: the per-mission world setup, which switches off the chapter content this mission
        // does not show. ⚠ It must run before any animation state, so a state can still override
        // it and so RestOf records the mission's placed pose as rest.
        Setup?.Apply(
            (name, scope) => FindAll(name, scope),
            SetSubtreeActive,
            (t, pos) => _opsApplied += Pose.PoseTranslate(t, pos, relative: false),
            (t, r) => _opsApplied += Pose.PoseRotate(t, r),
            SetSubtreeActiveByIndex);

        // Pass 1: the registry, THEN base states (docs/formats/destructibles.md "Starting
        // destroyed"). ⚠ Anchored defs only: a def whose NAME matches nothing here must not
        // stomp globally-resolved bare names like 'destroyed'.
        int anchored = 0;
        foreach (var def in program.Defs)
        {
            var anchors = Anchors(def);
            if (anchors.Count == 0)
                continue;
            anchored++;
            if (def.Destructible)
                foreach (var anchor in anchors)
                    if (anchor != null)
                        _destructibles.Register(def, anchor, def.Health, DamageNodeOf(def, anchor));
            if (def.ResetState != null)
                foreach (var anchor in anchors)
                    ApplyInstant(def.ResetState.Events, def, anchor);
        }
        long resetMs = sw.ElapsedMilliseconds;

        // Passes 2 and 3 start the world animating. A quiet-stage bootstrap skips them and runs
        // them later through StartAmbient; the passes around them still run.
        int startupRun = 0;
        var ran = new List<string>();
        var missing = new List<string>();
        if (AutoStart)
        {
            _ambientStarted = true;
            (startupRun, ran, missing) = RunAmbientPasses();
        }

        // Pass 4: safety net for destroyed-variant subtrees no definition covered.
        var netHidden = HideUncoveredDestroyed();

        if (Setup != null)
            Log.Raw(Setup.Report());
        Log.Info("anim", $"anim: {program.Defs.Count} defs ({program.CompiledCount} compiled, {program.ReaderCount} reader), {program.ScriptPoolCount} SI scripts; {anchored} anchored, {_opsApplied} state ops applied, {_opsUnresolved} unresolved");
        Log.Info("anim", $"anim: {startupRun} ON_STARTUP + {ran.Count} start anims running, {_instances.Count} live instance(s), {Motions.Count} live motion(s) [index {indexMs} ms, reset states {resetMs - indexMs} ms, start {sw.ElapsedMilliseconds - resetMs} ms]");
        if (_destructibles.Count > 0)
            Log.Info("anim", $"anim: {_destructibles.Count} destructible instance(s) across {_destructibles.DistinctAnchors} node group(s) registered (mutable HP; inert until weapons land)");
        if (program.MissionLibrarySkipped.Count > 0)
            Log.Info("anim", $"anim: {program.MissionLibrarySkipped.Count} reader def(s) superseded by this mission's compiled manifest (mission-scope + NAME1), not instantiated: {string.Join(", ", program.MissionLibrarySkipped.Take(8))}{(program.MissionLibrarySkipped.Count > 8 ? ", …" : "")}");
        if (program.SharedFilesSkipped.Count > 0)
            Log.Info("anim", $"anim: {program.SharedFilesSkipped.Count} shared reader file(s) no ANIMATION_DEFINITION_FILE list of this mission names, not loaded: {string.Join(", ", program.SharedFilesSkipped.Take(8))}{(program.SharedFilesSkipped.Count > 8 ? ", …" : "")}");
        if (program.ChapterFilesSkipped.Count > 0)
            Log.Info("anim", $"anim: {program.ChapterFilesSkipped.Count} chapter reader file(s) no ANIMATION_DEFINITION_FILE list of this mission names, not loaded: {string.Join(", ", program.ChapterFilesSkipped.Take(8))}{(program.ChapterFilesSkipped.Count > 8 ? ", …" : "")}");
        if (ran.Count > 0 || missing.Count > 0)
            Log.Info("anim", $"anim: start anims [{string.Join(", ", ran)}]{(missing.Count > 0 ? $", undefined here: [{string.Join(", ", missing)}]" : "")}");
        var emitterCensus = Emitters.Census;
        if (emitterCensus.Count > 0)
            Log.Info("anim", $"anim: {emitterCensus.Count} puffer emitter(s): {string.Join(", ", emitterCensus.Select(r => r.Name).Distinct())}");
        if (Light.Count > 0)
            Log.Info("anim", $"anim: {Light.Count} point light(s), {Light.ActiveCount} lit at startup: {string.Join(", ", Light.Names.Take(10))}");
        // Reported on their own line rather than through Count(), which is the "not yet acted on"
        // channel: filing a working feature there would report it as a missing one.
        if (Sound.EmitterCount > 0 || Sound.Unknown > 0 || Sound.AfterBuild > 0)
            Log.Info("anim", $"anim: {Sound.EmitterCount} ambient sound emitter(s): {string.Join(", ", Sound.Names)}{(Sound.Unknown > 0 ? $" [{Sound.Unknown} unknown to sounds.json]" : "")}{(Sound.AfterBuild > 0 ? $" [{Sound.AfterBuild} requested with no audio session]" : "")}");
        // Everything above is a bootstrap snapshot; from here on a failure reports itself.
        Sound.MarkCensusPrinted();
        if (netHidden.Count > 0)
            Log.Info("anim", $"anim: safety net hid {netHidden.Count} uncovered destroyed subtree(s): {string.Join(", ", netHidden.Take(10))}{(netHidden.Count > 10 ? ", …" : "")}");
        ReportConditions();
        ReportRetargets();
        ReportWaits();
        ReportUnhandled();
        _resolver.CloseCensus();
    }

    // Runs the two ambient-playback bootstrap passes, pass 2 (ACTIVATION ON_STARTUP definitions)
    // and pass 3 (the mission's startanims, by ANIMATION_NAME, in list order), and returns their
    // tallies for the census. Shared with Bootstrap, so a quiet-stage bootstrap can defer them to
    // StartAmbient.
    private (int StartupRun, List<string> Ran, List<string> Missing) RunAmbientPasses()
    {
        // Pass 2: ON_STARTUP definitions run for real. ⚠ A def carrying EXECUTION_BY_RANGE must
        // defer to the proximity check rather than fire blind at t=0; it is authored to execute
        // only near the player. An unanchored one has no position to measure from.
        int startupRun = 0;
        _rangeDeferred.Clear();
        _rangeOriginLocal.Clear();
        _rangeLibraryCallDefs.Clear();
        foreach (var def in _program.Defs.Where(d => d.OnStartup))
            foreach (var anchor in Anchors(def))
            {
                if (def.ByRange && anchor != null)
                {
                    _rangeDeferred.Add((def, anchor));
                    continue;
                }
                Start(def, anchor);
                startupRun++;
            }
        _rangeCheckCells.Clear(); // force a sweep on the first Advance
        // Pass 3: the mission's start animations, by ANIMATION_NAME, in list order. Keep the
        // ordinary Play path exact; only a ranged definition whose immediate call points at
        // unplaced library content waits for proximity.
        var ran = new List<string>();
        var missing = new List<string>();
        foreach (var animName in _program.StartAnims)
        {
            var defs = _program.ByAnimName(animName).ToList();
            if (defs.Count == 0)
            {
                missing.Add(animName);
                continue;
            }

            bool needsRangeDeferral = defs.Any(def => def.ByRange
                && CallsUnplacedDefinition(def) && Anchors(def).Count > 0);
            if (!needsRangeDeferral)
            {
                Play(animName);
                ran.Add(animName);
                continue;
            }

            foreach (var def in defs)
            {
                var anchors = Anchors(def);
                if (anchors.Count == 0)
                    anchors.Add(null);
                foreach (var anchor in anchors)
                {
                    if (def.ResetState != null)
                    {
                        _invalidated.Remove(def);
                        ApplyInstant(def.ResetState.Events, def, anchor);
                    }
                    if (def.ByRange && anchor != null && CallsUnplacedDefinition(def))
                    {
                        _rangeDeferred.Add((def, anchor));
                        _rangeLibraryCallDefs.Add(def);
                    }
                    else
                    {
                        Start(def, anchor);
                    }
                }
            }
            ran.Add(animName);
        }
        _rangeCheckCells.Clear();
        if (_rangeDeferred.Count > 0)
            Log.Info("anim", $"anim: {_rangeDeferred.Count} ambient def(s) deferred by EXECUTION_BY_RANGE: {string.Join(", ", _rangeDeferred.Select(e => $"{e.Def.AnimName ?? e.Def.Name}({Mathf.Sqrt(e.Def.RangeMax):0} m)").Distinct().Take(10))}{(_rangeDeferred.Count > 10 ? ", ..." : "")}");
        return (startupRun, ran, missing);
    }

    // A CALL_ANIMATION onto a range-gated cutscene definition: the call ARMS it, and the player
    // reaching its band is what runs it. ⚠ Measure from the callee's OWN anchor, the way the
    // bootstrap deferral does; a call with no target site would otherwise measure from whatever
    // node the caller happens to sit on. Returns whether the call was parked rather than started.
    private bool DeferRangedCall(AnimDefinition target, Node3D? fallbackAnchor)
    {
        if (!target.ByRange || target.AnimName is not { } name || !RangeGatedCalls.Contains(name))
            return false;
        var anchors = Anchors(target);
        var anchor = anchors.Count > 0 ? anchors[0] : fallbackAnchor;
        if (anchor == null)
            return false;
        foreach (var (deferred, at) in _rangeDeferred)
            if (ReferenceEquals(deferred, target) && ReferenceEquals(at, anchor))
                return true;
        float d2 = NearestPlayerDistanceSquared(RangeOriginOf(anchor));
        if (d2 >= target.RangeMin && d2 <= target.RangeMax)
            return false;
        _rangeDeferred.Add((target, anchor));
        _rangeLibraryCallDefs.Add(target);
        _rangeCheckCells.Clear(); // force a sweep on the next Advance
        Log.Info("anim", $"anim: '{name}' armed by call at {Mathf.Sqrt(d2):0} m, waiting for EXECUTION_BY_RANGE ({Mathf.Sqrt(target.RangeMax):0} m)");
        return true;
    }

    private bool CallsUnplacedDefinition(AnimDefinition caller)
    {
        foreach (var seq in caller.Sequences)
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "CallAnimation" || ev.Data.Str("name") is not { } called)
                    continue;
                foreach (var target in _program.ByAnimName(called))
                    if (Anchors(target).Count == 0)
                        return true;
            }
        return false;
    }

    // Where a range gate measures FROM. Not the node origin: an absolute-modelled gamez subtree
    // holds its vertices in world space under an identity transform, so its origin is the map
    // corner and a band around it can never be entered (CM07's `hangar_3` is one). VisualOriginOf
    // IS the origin for a normally-transformed node, so the defs deferred at bootstrap keep the
    // distance they have always measured. Measured once per anchor and carried in the anchor's
    // frame from then on (see _rangeOriginLocal).
    private Vector3 RangeOriginOf(Node3D node)
    {
        if (!node.IsInsideTree())
            return WorldPos(node);
        if (_rangeOriginLocal.TryGetValue(node, out var local))
            return node.GlobalTransform * local;
        var origin = VisualOriginOf(node);
        _rangeOriginLocal[node] = node.GlobalTransform.AffineInverse() * origin;
        return origin;
    }

    // Starts any deferred ambient EXECUTION_BY_RANGE def whose anchor the player has come
    // within range of. One-shot per (def, anchor): once started, the def runs exactly as an
    // undeferred ON_STARTUP would (its own events decide what persists).
    private void TickDeferredByRange()
    {
        if (_rangeDeferred.Count == 0)
            return;
        var positions = RangePositions();
        // No-op until some player crosses a check cell (the common case, every frame).
        bool moved = positions.Count != _rangeCheckCells.Count;
        for (int i = 0; !moved && i < positions.Count; i++)
            moved = CheckCellOf(positions[i]) != _rangeCheckCells[i];
        if (!moved)
            return;
        _rangeCheckCells.Clear();
        foreach (var p in positions)
            _rangeCheckCells.Add(CheckCellOf(p));
        for (int i = _rangeDeferred.Count - 1; i >= 0; i--)
        {
            var (def, anchor) = _rangeDeferred[i];
            if (!IsInstanceValid(anchor))
            {
                _rangeDeferred.RemoveAt(i);
                _rangeOriginLocal.Remove(anchor);
                continue;
            }
            var anchorPos = RangeOriginOf(anchor);
            float d2 = float.MaxValue;
            foreach (var p in positions)
                d2 = Mathf.Min(d2, anchorPos.DistanceSquaredTo(p));
            if (d2 < def.RangeMin || d2 > def.RangeMax)
                continue;
            _rangeDeferred.RemoveAt(i);
            Log.Info("anim", $"anim: EXECUTION_BY_RANGE reached - starting {def.AnimName ?? def.Name} at {Mathf.Sqrt(d2):0} m (range {Mathf.Sqrt(def.RangeMax):0} m)");
            bool libraryCalls = _rangeLibraryCallDefs.Contains(def);
            if (libraryCalls)
                _rangeCallDepth++;
            try
            {
                Start(def, anchor);
            }
            finally
            {
                if (libraryCalls)
                    _rangeCallDepth--;
            }
        }
    }

    // indexByPointer=false is IndexPooledCopy's own case: a second (third, …) copy of the SAME
    // source GameZNode carries the SAME compiled indices as the first, and an index-keyed map can
    // only ever hold one winner per index, see that method's remark for why skipping it is
    // correct, not a loss (Targets' own symbol-miss rescue is anchor-scoped by name instead). The
    // resolver applies the flag, together with its own NameResolveFallback refusal, inside Add.
    private void IndexWorld(Node3D worldRoot, bool indexByPointer = true, int indexOffset = 0)
    {
        void Walk(Node3D n, Node3D? parent)
        {
            var srcName = n.HasMeta(NameMeta) ? n.GetMeta(NameMeta).AsString() : n.Name.ToString();
            int? gamezIndex = n.HasMeta(IndexMeta) ? (int)n.GetMeta(IndexMeta) + indexOffset : null;
            _resolver.Add(n, srcName, parent, gamezIndex, indexByPointer);
            for (int i = 0, count = n.GetChildCount(); i < count; i++)
                if (n.GetChild(i) is Node3D c)
                    Walk(c, n);
        }
        Walk(worldRoot, worldRoot.GetParent() as Node3D);
    }

    // Re-runs the quiet-stage RESET_STATE posing pass (bootstrap pass 1) for whatever now anchors
    // within a subtree added after the fact, IndexStage's (and IndexPooledCopy's) own tail.
    private void ApplyResetStatesWithin(Node3D subtree)
    {
        foreach (var def in _program.Defs)
        {
            if (def.ResetState == null)
                continue;
            foreach (var a in Anchors(def))
                if (a != null && (a == subtree || subtree.IsAncestorOf(a)))
                    ApplyInstant(def.ResetState.Events, def, a);
        }
    }

    // Re-applies the RESET_STATE of every def a PlayEffectAt of animName reaches, on the anchors
    // sitting in the pool slot(s) the call just took: the checkout's stand-in for the original's
    // fresh template copy per CALL_ANIMATION. Without it a copy whose sequences end INACTIVE (the
    // sonic burst's five rings) plays once per slot and is dead from the wrap on. Scoped to the
    // call's own closure, never the whole slot: another effect live on the same slot number must
    // not be re-posed under its running motions. RESET_TIME -1 does not exempt a def.
    private void ResetCheckedOutCopies(string animName, IReadOnlyList<Node3D?> roots)
    {
        var slots = new HashSet<int>();
        foreach (var root in roots)
            if (root != null && IsInstanceValid(root) && _templateStage.SlotOf(root) is >= 0 and var slot)
                slots.Add(slot);
        if (slots.Count == 0)
            return;
        if (!_checkoutClosure.TryGetValue(animName, out var closure))
            _checkoutClosure[animName] = closure = _program.Subset(animName).Defs.ToList();
        foreach (var def in closure)
        {
            foreach (var a in Anchors(def))
            {
                if (a == null || !IsInstanceValid(a) || !slots.Contains(_templateStage.SlotOf(a)))
                    continue;
                // The respawn's three steps (ResetCalled), for its reason: a copy handed out again
                // must match the fresh one the original instances per call. ⚠ RESET_STATE alone
                // leaves the last play's motions driving these nodes, mid-flight, into the new one.
                Stop(def.AnimName, a);
                RestoreRestPoses(def, a);
                if (def.ResetState != null)
                    ApplyInstant(def.ResetState.Events, def, a);
            }
        }
    }

    // Records a flagged call whose callee was handed to the world-effects runtime, where this
    // runtime has no instance to hold on. ⚠ Print it here, once per callee, never through Count:
    // every routed call is a death-time event and the bootstrap census has already printed, so a
    // counter raised afterwards is never seen and the dropped hold becomes a silent skip.
    private void NoteRoutedWait(string callName)
    {
        if (_routedWaitsNamed.Add(callName))
        {
            Log.Info("anim", $"anim: WAIT_FOR_COMPLETION on '{callName}' not held, the callee is routed to the world-effects runtime, which this one cannot poll");
        }
    }

    // Arms this dispatch's WAIT_FOR_COMPLETION hold over the instances the call reached, as the
    // ISequenceHost.PendingWait the runner reads back. Nothing is installed when none of them is
    // live: a callee whose whole choreography fires at t=0 never becomes an instance.
    private void InstallWait(string callName, List<(AnimDefinition Def, Node3D? Anchor)> waitOn)
    {
        if (!waitOn.Any(w => IsLive(w.Def, w.Anchor)))
        {
            // ⚠ Keep this log. "Reached but holding nothing" is a different state from "never
            // dispatched", and without it a probe reports an inert mechanism as untested.
            if (_inertWaitsNamed.Add(callName))
                Log.Info("anim", $"anim: WAIT_FOR_COMPLETION on '{callName}' had nothing to hold, no live callee instance");
            return;
        }
        _waitsInstalled++;
        _waitsByCallee[callName] = _waitsByCallee.TryGetValue(callName, out int n) ? n + 1 : 1;
        float deadline = _elapsed + WaitCeilingS;
        _pendingWait = () =>
        {
            if (!waitOn.Any(w => IsLive(w.Def, w.Anchor)))
                return false;
            if (_elapsed < deadline)
                return true;
            _waitsAbandoned++;
            Log.Warn("anim", $"anim: WAIT_FOR_COMPLETION on '{callName}' abandoned after {WaitCeilingS:0} s, the callee never finished");
            return false;
        };
    }

    // Is this definition already running on this anchor? (Instance identity is (definition, anchor)
    // throughout.)
    private bool IsLive(AnimDefinition def, Node3D? anchor) =>
        _instances.Any(i => i.Def == def && i.Anchor == anchor);

    // Removes matching live instances. tearDown chooses whether to also clear each instance's
    // motions/puffers/lights/sounds: true for an explicit Stop, false for Start's seamless restart,
    // which leaves them for the new instance to re-assert.
    private void RemoveInstances(string? animName, Node3D? anchor, bool tearDown)
    {
        if (string.IsNullOrEmpty(animName))
            return;
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var inst = _instances[i];
            if (!string.Equals(inst.Def.AnimName, animName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (anchor != null && inst.Anchor != anchor)
                continue;
            if (_startingInstances.Count > 0 && _startingInstances.Peek() is { Protect: true } top
                && ReferenceEquals(top.Inst, inst))
                continue;
            _instances.RemoveAt(i);
            if (tearDown)
            {
                TearDownResourcesOf(inst.Def, inst.Anchor);
                _inputNodes.Remove((inst.Def, inst.Anchor));
                _templateStage.Reveal(inst.Def, inst.Anchor, visible: false);
            }
            // Start's seamless restart (tearDown false) keeps the entry, exactly as it keeps
            // the resources the new instance re-asserts.
            OnInstanceFinished?.Invoke(inst.Def, inst.Anchor);
        }
    }

    // An input-governed effect instance (one PlayEffectAt gave a real site node and whose def loops
    // on that node's active state) ends by its own authored exit, the NodeActive gate going false
    //, not by Stop, so its sustained emitters would keep emitting past the sequence's end. Tear
    // them down with the instance. Scoped to instances carrying an input node: ambient defs that
    // finish leaving a resource alive keep today's behaviour.
    private void FinishInputGoverned(AnimDefinition def, Node3D? anchor)
    {
        if (_inputNodes.Remove((def, anchor)))
            TearDownResourcesOf(def, anchor);
    }

    // An instance ends its sustained emitters when its own sequences end: the authored stop for a
    // def that ships none, which would otherwise burn for the whole session.
    // ⚠ Keep this instance-scoped, never sequence-scoped. A lone PufferState in a sequence that
    // ends on the same tick would take the debris trails that work down with it.
    // ⚠ End emission (SustainEnd) rather than tearing the emitter down, so live particles finish
    // their authored LIFETIME_RANGE and a later replay on this pool slot revives the entry.
    private void FinishEffectInstance(AnimDefinition def, Node3D? anchor)
    {
        // Consumes the effects runtime's TTL entry when there is one (this got there first, so
        // the sweep has nothing left to do); a world instance simply carries none.
        _effectTtls.RemoveAll(t => t.Def == def && t.Anchor == anchor);
        Emitters.EndFor(def, anchor);
    }

    // Tears down every live resource a stopped instance created, so nothing of the definition keeps
    // running after Stop. Motions and puffers are attributed to the exact (def, anchor) that
    // registered them; lights and sounds are keyed by (name, anchor) and so clear by anchor, which
    // is the instance identity and the finest attribution available for them.
    private void TearDownResourcesOf(AnimDefinition def, Node3D? anchor)
    {
        Motions.DiscardFor(def, anchor);
        Emitters.Discard(def, anchor);
        Light.DiscardFor(anchor);
        Sound.DiscardFor(anchor);
    }

    // A definition's own anchors, filtered to the ones this plane owns and that are actually a
    // docking-hook group, the shared filter both ParkDockingHook passes need.
    private IEnumerable<Node3D> DockingHookAnchors(AnimDefinition def, Node3D planeModel)
    {
        foreach (var a in Anchors(def))
        {
            if (a != null && planeModel.IsAncestorOf(a) && PlaneBuilder.IsDockingHook(NameOf(a)))
                yield return a;
        }
    }

    // Not every node an extend definition swings is covered by its retract's RESET_STATE (bal
    // and war author no scale reset at all; three more park a wrong axis). This closes that gap:
    // each node's FIRST authored FROM pose, translate/rotate/scale gathered independently, then
    // written as one combined basis, PoseRotate/PoseScale each reset the OTHER component to
    // rest, so composing through them loses whichever channel is applied first.
    private void SeedFromExtend(AnimDefinition extend, Node3D? anchor)
    {
        var rotate = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var scale = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var translate = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var firstEvent = new Dictionary<string, AnimEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var seq in extend.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectMotionFromTo" || ev.Data.Str("name") is not { } name)
                    continue;
                firstEvent.TryAdd(name, ev);
                if (ev.Data.Obj("rotate") is { } r && r.Has("from"))
                    rotate.TryAdd(name, r.Vec3("from"));
                if (ev.Data.Obj("scale") is { } s && s.Has("from"))
                    scale.TryAdd(name, s.Vec3("from"));
                if (ev.Data.Obj("translate") is { } tr && tr.Has("from"))
                    translate.TryAdd(name, tr.Vec3("from"));
            }
        }

        foreach (var (name, ev) in firstEvent)
        {
            if (!rotate.ContainsKey(name) && !scale.ContainsKey(name) && !translate.ContainsKey(name))
                continue;
            _hookSeeds++;
            if (_resolver.SymbolClaims(extend, name, anchor, out var bound) && bound != null)
                _hookSeedsBound++;
            else
                _hookSeedsUnbound.Add($"{extend.AnimName ?? extend.Name}/{name}");
            // Resolved exactly as the dispatch that later moves this same node resolves it
            // (Targets): the definition's own symbol table, and on a claimed-but-unbuilt index the
            // anchor-scoped rescue, never a global name match onto another aircraft's arm.
            foreach (var t in Targets(ev, extend, anchor))
            {
                var rest = RestOf(t);
                var rot = rotate.TryGetValue(name, out var r) ? r
                    : rest.Basis.Orthonormalized().GetEuler(EulerOrder.Yxz);
                var sc = scale.TryGetValue(name, out var s) ? s : rest.Basis.Scale;
                var origin = translate.TryGetValue(name, out var o) ? o : rest.Origin;
                t.Transform = new Transform3D(
                    Basis.FromEuler(rot, EulerOrder.Yxz).Scaled(NonSingularScale(sc)), origin);
            }
        }
    }

    // Applies a list of events with no clock, the RESET_STATE path, where every op is a base state
    // and timed motions collapse to their end pose.
    private void ApplyInstant(List<AnimEvent> events, AnimDefinition def, Node3D? anchor)
    {
        foreach (var ev in events)
            Dispatch(ev, def, anchor, instant: true, out _);
    }

    // ---- the dispatch table ----
    // Executes one event, reporting its duration in seconds so the sequence runner knows when the
    // next event is due: 0 for an instantaneous state change, the run time for a timed motion, the
    // script length for an SI script. Returns false only for control-flow events the runner
    // interprets itself.
    private bool Dispatch(AnimEvent ev, AnimDefinition def, Node3D? anchor, bool instant, out float duration)
    {
        duration = 0f;
        // A wait belongs to exactly the event that authored it. Cleared here rather than after
        // the runner reads it, so a flagged call that installs nothing (its callee never
        // resolved) cannot be answered by the previous call's still-live test.
        _pendingWait = null;
        switch (ev.Kind)
        {
            case "ObjectActiveState":
                // ⚠ Test for a SOUND_NODE emitter first. An ordinary OBJECT_ACTIVE_STATE switches
                // one on, and its name is a sounds.json definition rather than a gamez node, so
                // falling through to Targets() books it as an unresolved op.
                if (Sound.TrySetActive(ev, anchor))
                    return true;
                _opsApplied += Pose.HandleActiveState(ev, def, anchor, instant);
                SyncDestructiblePool(ev, def, anchor);
                return true;

            case "ObjectTranslateState":
                _opsApplied += Pose.HandleTranslateState(ev, def, anchor);
                return true;

            case "ObjectRotateState":
                _opsApplied += Pose.HandleRotateState(ev, def, anchor);
                return true;

            case "ObjectScaleState":
                _opsApplied += Pose.HandleScaleState(ev, def, anchor);
                return true;

            case "ObjectMotionFromTo":
                _opsApplied += Pose.HandleMotionFromTo(ev, def, anchor, instant, out duration);
                return true;

            case "ObjectOpacityState":
                _opsApplied += Pose.HandleOpacityState(ev, def, anchor);
                return true;

            case "ObjectOpacityFromTo":
                _opsApplied += Pose.HandleOpacityFromTo(ev, def, anchor, instant, out duration);
                return true;

            case "ObjectMotion":
                // Counted as handled, not as an unresolved op: the drop is the caller's decision
                // (see SuppressedMotionAnims), not a gap in this table.
                if (def.AnimName is { } motionOwner && SuppressedMotionAnims.Contains(motionOwner))
                    return true;
                _opsApplied += Pose.HandleMotion(ev, def, anchor, instant, out duration);
                return true;

            case "ObjectMotionSiScript":
                _opsApplied += Pose.HandleMotionSiScript(ev, def, anchor, instant, out duration);
                return true;

            case AnimDefinition.AllNamesKind:
                _opsApplied += Pose.HandleMotionSiScriptAllNames(ev, def, anchor, instant, out duration);
                return true;

            // Control flow is the runner's business, not the table's.
            case "Loop":
            case "If":
            case "Elseif":
            case "Else":
            case "Endif":
                return false;

            case "CallSequence":
                if (ev.Data.Str("name") is { } seqName)
                    CallSequence(def, anchor, seqName);
                return true;

            case "StopSequence":
                // ⚠ Halt the named sequence's runners and end its in-flight OBJECT_MOTION bodies
                // where they stand. Nothing is retracted, and what other events launched keeps its
                // own lifetime (docs/formats/anim-definitions.md).
                if (ev.Data.Str("name") is { } stopName)
                    StopSequence(def, anchor, stopName);
                return true;

            case "CallAnimation":
                // ⚠ A call must not restart an animation already live on this anchor. The data's
                // poll idiom re-issues the call every frame its condition holds, and restarting
                // pins a door at its first frame for as long as the player hovers.
                if (ev.Data.Str("name") is { } callName)
                {
                    // ⚠ Honour the call's own target node. That re-anchoring is the data's
                    // template-instancing mechanism, and ignoring it runs every call site on the
                    // CALLER's anchor instead of where the data put it.
                    var (siteNode, siteOffset) = CallTargetSite(ev, def, anchor);
                    var callAnchor = siteNode ?? anchor;
                    // The world runtime cannot render an effect template, its puffer factory being
                    // torn down after the build, so a death's effect call goes to the world-effects
                    // runtime instead and the local Start that would build nothing is skipped.
                    if (ExternalEffect != null && callAnchor != null && IsInstanceValid(callAnchor))
                    {
                        var siteXform = callAnchor.GlobalTransform;
                        // ⚠ Use VisualOriginOf, not the raw origin; an absolute-modelled target's
                        // node origin is the map corner. The site node rides along as the callee's
                        // INPUT_NODE and the effect follows it (a ring's death fireballs move with the hull).
                        if (ExternalEffect(callName, VisualOriginOf(callAnchor) + siteXform.Basis * siteOffset, siteNode, siteNode != null))
                        {
                            // ⚠ Count the dropped hold rather than passing over it. A routed call
                            // leaves no instance here to wait on, and this is the one scope
                            // boundary the wait has.
                            if (ev.WaitsForCompletion && !instant)
                                NoteRoutedWait(callName);
                            return true;
                        }
                    }
                    // ⚠ An OPERAND_NODE call must never relocate or lazily build the callee's own
                    // root. It redirects the callee's node resolution onto the CALLER's subtree, so
                    // a just-built placeholder outranks that and the wreck goes undriven.
                    bool operandRedirect = ev.Data.Str("operand_node") != null;
                    // ⚠ Collect the wait set as the loop resolves it, never re-derive it after.
                    // ResolveLibraryRoot takes a pool slot, so a second ask hands the wait a
                    // different copy from the one running.
                    List<(AnimDefinition Def, Node3D? Anchor)>? waitOn =
                        ev.WaitsForCompletion && !instant ? new() : null;
                    foreach (var target in _program.ByAnimName(callName))
                    {
                        // A range-gated cutscene: the call arms it, the player arriving runs it.
                        if (!instant && DeferRangedCall(target, callAnchor))
                            continue;
                        // ⚠ Before the staging below and before the wait list: an unmet call
                        // starts nothing, so a WAIT_FOR_COMPLETION on it would hold for a callee
                        // that never runs. Silent like the node-state gate in Start.
                        if (!AnimPrerequisitesMet(target))
                        {
                            Count("CallAnimation(anim prerequisite unmet)");
                            continue;
                        }
                        // A call naming a destructible's own death definition kills that
                        // destructible (KillCalledDestructible); the wait, if any, is on the
                        // death now running on its own anchor.
                        if (!instant && !operandRedirect && KillCalledDestructible(target) is { } killed)
                        {
                            waitOn?.Add((target, killed.Anchor));
                            continue;
                        }
                        // ⚠ Keep the library-root test data-driven, never name-based, and gated on
                        // a death or range-triggered mission call. Other ambient calls keep their
                        // authored positions and must not relocate during bootstrap.
                        Node3D? libraryCopy = null;
                        bool relocate = false;
                        if (!instant && callAnchor != null)
                        {
                            if (_templateStage.Places)
                            {
                                relocate = true;
                            }
                            else if ((_deathCallDepth > 0 || _rangeCallDepth > 0
                                    || _missionCallDepth > 0 || StartedByMissionTrigger(def))
                                && !operandRedirect && ResolveLibraryRoot != null)
                            {
                                string rootName = string.IsNullOrEmpty(target.Name)
                                    ? target.RootName ?? "" : target.Name;
                                // ⚠ A call whose AT_NODE site IS the callee's own root node names
                                // the node to run on; a pooled copy beside it would drive one node
                                // while the shot composed in the other hangs off it.
                                if (!string.Equals(NameOf(callAnchor), rootName,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    // ⚠ The site the call NAMES, never the caller's own anchor: a
                                    // placeless call must not move its callee's root, and a staged
                                    // actor's script poses its children in WORLD coordinates.
                                    libraryCopy = ResolveLibraryRoot(rootName, callAnchor,
                                        siteNode != null ? ev : null);
                                    relocate = libraryCopy != null;
                                }
                            }
                        }
                        // A relocating call outside the pool claims its sticky slot before the
                        // placed-where test asks for this call's copy. ⚠ Keyed on the authored
                        // EVENT: a REPEAT call from one anchor must not take the first one's slot.
                        Node3D? repeatCopy = relocate && libraryCopy == null
                            ? _templateStage.AssignCallerSlot(target, callAnchor!, ev) : null;
                        // ⚠ A pooled copy must BE Start's anchor, not the call site: its symbol
                        // lookup falls to the name rescue scoped to its own subtree, and instance
                        // identity is (def, anchor), so two calls on one anchor need two anchors.
                        var ownCopy = libraryCopy ?? repeatCopy;
                        var startAnchor = ownCopy ?? callAnchor;
                        // Added whether or not the Start below fires: "wait until it completes"
                        // is about the named animation, not about which call started it.
                        waitOn?.Add((target, startAnchor));
                        Vector3 wantSite = default;
                        bool movedAway = false;
                        if (relocate)
                        {
                            wantSite = callAnchor!.GlobalTransform.Origin + callAnchor.GlobalTransform.Basis * siteOffset;
                            // ⚠ A placed template called at a DIFFERENT site restarts even while
                            // live; one copy can only be in one place, so the live guard would
                            // otherwise give the second call no effect at all.
                            movedAway = ownCopy != null
                                ? ownCopy.GlobalTransform.Origin.DistanceSquaredTo(wantSite)
                                  > TemplateStage<Node3D>.MoveToleranceSq
                                : !_templateStage.IsAt(target, callAnchor, siteOffset);
                        }
                        // Gated on the site actually having moved, so the data's poll idiom
                        // (`If … CallAnimation; Endif; Loop{-1}`) keeps hitting the guard and does
                        // not restart its callee every frame.
                        if (!IsLive(target, startAnchor) || movedAway)
                        {
                            // ⚠ Move the root, not just the anchor. An effect's puffers ride the
                            // template's own root, so re-anchoring alone emits at the gamez origin.
                            if (relocate)
                            {
                                // A death's callee rides its call site: a carried ring's debris
                                // moves with the hull (the pieces integrate in the root's frame,
                                // docs/org/sequences.md). Other relocating calls hold their placement.
                                bool rides = _deathCallDepth > 0;
                                // Null for every callee the play did not name, which is the whole
                                // faithful path: those keep the axis their template stands on.
                                var placeOrient = OrientedCall(target);
                                if (ownCopy != null)
                                {
                                    // Level a repeat call's copy exactly as PlaceAt would level the
                                    // first one; the library-root pool has never levelled.
                                    bool level = libraryCopy == null && LevelsTemplate(target);
                                    if (rides)
                                        _templateStage.PlaceFollowing(new[] { (Node3D?)ownCopy }, callAnchor!, wantSite, level);
                                    else
                                        _templateStage.PlaceOn(new[] { (Node3D?)ownCopy }, wantSite, level, placeOrient);
                                }
                                else
                                {
                                    _templateStage.PlaceAt(target, callAnchor!, siteOffset, follow: rides, orient: placeOrient);
                                }
                            }
                            Start(target, startAnchor);
                            // ⚠ Reveal the mesh too, after Start. On a stage that hides its
                            // templates, an unrevealed callee shows its particles and none of its
                            // authored geometry, because puffers draw at world level.
                            if (relocate)
                                _templateStage.Reveal(target, startAnchor, visible: true);
                            // A relocated death call on the dying instance's own anchor is the
                            // caller's own choreography, so a reset must restore it too. ⚠ Scope
                            // this to the same test as relocation, or a shared template goes too.
                            if (relocate && startAnchor != null && _dyingInstances.Count > 0
                                && ReferenceEquals(_dyingInstances.Peek().Anchor, callAnchor))
                            {
                                _dyingInstances.Peek().LocalCallTargets.Add((target, startAnchor));
                            }
                        }
                    }
                    if (waitOn is { Count: > 0 })
                        InstallWait(callName, waitOn);
                }
                return true;

            case "StopAnimation":
                Stop(ev.Data.Str("name"));
                return true;

            case "InvalidateAnimation":
                // ⚠ Not a stop. It is the "once started, never again" latch every `*_start` route
                // relies on, and treating it as a stop kills the sequence at event 0
                // (docs/formats/anim-definitions.md, and _invalidated).
                Invalidate(ev.Data.Str("name"));
                return true;

            case "ResetAnimation":
                // Clears the invalidation latch and re-poses the def's RESET_STATE, which is what
                // lets a turned-off light loop be turned on again.
                ResetAnimation(ev.Data.Str("name"));
                return true;

            case "PufferState":
                HandlePufferState(ev, def, anchor);
                return true;

            case "LightState":
                Light.HandleLightState(ev, def, anchor);
                _opsApplied++;
                return true;

            case "LightAnimation":
                // ⚠ Report the ramp as the event's DURATION so the next step of a pulse chain waits
                // for it. The light is tweened asynchronously here, so reporting 0 fires every step
                // in one instant and an authored flicker collapses to a single frame.
                if (Light.HandleLightAnimation(ev, anchor, instant))
                    _opsApplied++;
                else
                    Count("LightAnimation(no light)");
                // A RESET_STATE lands the delta whole (see the handler), so it takes no time.
                duration = instant ? 0f : ev.Data.Num("run_time") ?? 0f;
                return true;

            case "SoundNode":
                Sound.HandleSoundNode(ev, def, anchor);
                return true;

            case "Sound":
                Sound.HandleSound(ev, def, anchor);
                return true;

            case "FbfxColorFromTo":
                {
                    // A full-screen wash, linear RGBA from `from` to `to` over `run_time`.
                    // ⚠ Report the run time as this event's duration. The original holds the
                    // sequence for it, and without that a multi-step wash lands in one instant.
                    float runTime = ev.Data.Num("run_time") ?? 0f;
                    duration = runTime;
                    // A wash is a thing that happens, not a pose, so a RESET_STATE applies nothing.
                    if (!instant && ScreenFlash != null)
                    {
                        // Where the burst is and how far its def admits the wash, so the overlay
                        // paints the panes it reached instead of all four.
                        ScreenFlash(Rgba(ev.Data.Obj("from")), Rgba(ev.Data.Obj("to")), runTime,
                            anchor != null ? WorldPos(anchor) : Vector3.Zero,
                            anchor != null ? WashGateRadiusSquared(def) : 0f);
                        _opsApplied++;
                    }
                    return true;
                }

            case "FogState":
                // Not gated on `instant`: the original writes the fog record from a reset walk too.
                if (FogStateSink != null)
                {
                    FogStateSink(FogStateChange.From(ev.Data));
                    _opsApplied++;
                }
                else
                    Count("FogState(no sink)");
                return true;

            case "Callback":
                // A callback is a thing that happens, not a pose, so a RESET_STATE raises none.
                // The two codes acted on are authored in sequences alone, never in a reset block.
                if (!instant)
                    HandleCallback(ev, def);
                return true;

            case "ObjectAddChild":
                // ⚠ The sound-emitter form wins; only what it declines is a node reparent.
                if (!HandleAddChild(ev, def, anchor)
                    && (instant || !HandleReparent(ev, def, anchor, adopt: true)))
                    Count(ev.Kind);
                return true;

            case "ObjectDeleteChild":
                // ⚠ Sequences only. A RESET_STATE walk runs at the bootstrap, where undoing a
                // reparent no definition has made yet would move shipped nodes off their parents.
                if (instant || !HandleReparent(ev, def, anchor, adopt: false))
                    Count(ev.Kind);
                return true;

            default:
                // One-shot Sound / opacity / texture-cycle / camera and the
                // rest: dispatched, counted, and reported once per kind. Adding a handler is
                // a case above and nothing else.
                Count(ev.Kind);
                return true;
        }
    }

    // The authored code decides what a CALLBACK means, never the def it sits in: the player's
    // crash defs carry `has_callbacks: true` and a code this runtime does not act on, so no family
    // test can stand in for reading the value.
    // ⚠ An unrecognised code is counted under its own key and nothing else. Codes outside the two
    // below belong to the original's mission-script handler, and guessing at one invents behaviour.
    private void HandleCallback(AnimEvent ev, AnimDefinition def)
    {
        int code = (int)(ev.Data.Num("value") ?? -1f);
        // The mission-script host first: its vocabulary is the session's, and it declines every
        // code it does not own, so the two seams below keep the codes they always had.
        if (CallbackHost != null && CallbackHost(code, def.AnimName, RootNodeNameOf(def)))
        {
            _opsApplied++;
            return;
        }

        switch (code)
        {
            case CallbackWreckVelocity when WreckVelocity != null:
                // Runtime-wide because the crash runtime is per aircraft, so its one wreck is the
                // only thing this can reach.
                ArmInheritedVelocity(WreckVelocity());
                _opsApplied++;
                return;
            case CallbackStopStages when StopDamageStages != null || StopWreckFlying != null:
                // Both halves of the original's code-15 arm, in its order: stop the stage anims,
                // then drop the dead vehicle out of the movement update it was still running.
                StopDamageStages?.Invoke();
                StopWreckFlying?.Invoke();
                _opsApplied++;
                return;
            case CallbackResetView when ResetPilotView != null:
                // The original's own case runs before anything else the destroy sequence does, so
                // the death is watched from outside from its first frame.
                ResetPilotView();
                _opsApplied++;
                return;
            case CallbackWreckVelocity:
            case CallbackStopStages:
            case CallbackResetView:
                // Named apart from an unknown code: this one IS understood, and only the caller's
                // seam is missing, which is the whole answer to "why did the wreck not inherit".
                Count($"Callback({code}, no seam wired)");
                return;
            default:
                Count($"Callback({code})");
                return;
        }
    }

    // The node a definition is rooted on, for the mission-script host. ANIMATION_ROOT_NAME first
    // and the anchoring NAME behind it: CM02's capture defs author the same aircraft in both, and
    // a def with no root at all still names the object it belongs to in its NAME.
    private string? RootNodeNameOf(AnimDefinition def) =>
        def.RootName is { Length: > 0 } root ? root
        : def.Name.Length > 0 ? def.Name
        : null;

    // How far from its anchor a definition's own `If PlayerRange` gate admits its wash, in metres
    // SQUARED (the compiled convention both sources normalise to), and 0 for a def that gates on
    // nothing, the routing radius for ScreenFlash, and the authored one.
    // ⚠ Take the largest gate, not the first, so a def with several cannot route a wash by
    // whichever is listed first. ⚠ Never re-derive it from the weapon: wash defs are ground
    // effects on terrain impacts where no aircraft was hit.
    private float WashGateRadiusSquared(AnimDefinition def)
    {
        if (_washGates.TryGetValue(def, out float cached))
            return cached;
        float radiusSq = 0f;
        foreach (var seq in def.Sequences)
            foreach (var ev in seq.Events)
            {
                if (ev.Kind is not ("If" or "Elseif"))
                    continue;
                if (ev.Data.Obj("condition")?.Num("PlayerRange") is { } r && r > radiusSq)
                    radiusSq = r;
            }
        _washGates[def] = radiusSq;
        return radiusSq;
    }

    // An {r,g,b,a} sub-object as a colour; absent → transparent black.
    private Color Rgba(AnimData? d) => d == null
        ? new Color(0f, 0f, 0f, 0f)
        : new Color(d.Num("r") ?? 0f, d.Num("g") ?? 0f, d.Num("b") ?? 0f, d.Num("a") ?? 0f);

    // Every node name a CALL_ANIMATION in this program targets, per callee anim name: the site a
    // called def's INPUT_NODE stands for. Same two spellings CallTargetSite reads.
    private Dictionary<string, HashSet<string>> CallTargetNames()
    {
        var targets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in _program.Defs)
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind != "CallAnimation" || ev.Data.Str("name") is not { } callee)
                        continue;
                    string? target = null;
                    if (ev.Data.Obj("parameters")?.Union() is { Value: Dictionary<string, object?> p })
                        target = new AnimData(p).Str("node");
                    target ??= ev.Data.Str("operand_node");
                    if (target == null)
                        continue;
                    if (!targets.TryGetValue(callee, out var set))
                        targets[callee] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(target);
                }
            }
        }
        return targets;
    }

    // Where a call-site hosted puffer of this def can land: the nodes its calls target, its own
    // staged copies (a placing call anchors the callee on its copy, not the site), and the anchors
    // handed in. An over-count here costs one idle emitter; an under-count costs a frame.
    private IEnumerable<Node3D> CallSiteHostsOf(AnimDefinition def,
        Dictionary<string, HashSet<string>> callTargets, Node3D[] callSiteAnchors)
    {
        var hosts = new List<Node3D>(callSiteAnchors);
        if (def.AnimName != null && callTargets.TryGetValue(def.AnimName, out var targets))
        {
            foreach (var target in targets)
                hosts.AddRange(FindAll(target, null));
        }
        if (!string.IsNullOrEmpty(def.Name))
            hosts.AddRange(FindAll(def.Name, null).Where(n => _templateStage.SlotOf(n) >= 0));
        return hosts.Distinct();
    }

    private void HandlePufferState(AnimEvent ev, AnimDefinition def, Node3D? anchor)
    {
        if (ev.Data.Str("name") is not { } pufferName)
            return;
        // ACTIVE_STATE: 1 = start emitting, 0 = stop. The attach point is AT_NODE; note it is
        // NOT the event's "name" (that is the puffer's own name, a different namespace).
        bool on = (ev.Data.Num("active_state") ?? 0f) >= 1f;
        // AT_NODE INPUT_NODE / MAIN_ROOT_NODE mean "the node this def was invoked on", so a
        // destruction fire emits on the effect's own relocated root and lands at the hit site.
        var host = ev.Data.Str("at_node") is { } atNode
            ? (IsSelfNodeRef(atNode) ? InputNodeOf(def, anchor) ?? anchor : Resolve(atNode, def, anchor))
            : anchor;
        if (host == null)
        {
            Count("PufferState(no host node)");
            return;
        }

        if (!on)
        {
            Emitters.End(pufferName, host, def, anchor);
            return;
        }
        // _opsApplied counts APPLIED ops, and only a build is one: a re-assert of a running
        // emitter is a no-op and a miss is already counted by name. Read off the director's own
        // total rather than re-deriving the verdict here.
        int built = Emitters.Built;
        Emitters.Assert(pufferName, host, def, anchor, ev.Data);
        if (Emitters.Built != built)
            _opsApplied++;
    }

    // The stored call-site node for a live instance, when it is still valid.
    private Node3D? InputNodeOf(AnimDefinition def, Node3D? anchor) =>
        _inputNodes.TryGetValue((def, anchor), out var node) && IsInstanceValid(node) ? node : null;

    // Whether any of the def's sequences conditions on the INPUT_NODE sentinel's active state, the
    // authored "run while my host stands" lifecycle (the damage-stage sputters' If NodeActive →
    // Loop). Cached; the answer is a property of the data.
    private bool DefConditionsOnInputNode(AnimDefinition def)
    {
        if (_inputGoverned.TryGetValue(def, out bool cached))
            return cached;
        bool governed = def.Sequences.Any(s => s.Events.Any(ev =>
            (ev.Kind == "If" || ev.Kind == "Elseif")
            && ev.Data.Obj("condition")?.Union() is { Tag: "NodeActive" } union
            && AnimData.AsNum(union.Value) is { } idx && idx > 1e9f));
        _inputGoverned[def] = governed;
        return governed;
    }

    // The sound-emitter case of OBJECT_ADD_CHILD: attach a declared emitter to the world node that
    // positions it. Returns false for every other use, which HandleReparent then takes
    // (docs/formats/anim-definitions.md).
    private bool HandleAddChild(AnimEvent ev, AnimDefinition def, Node3D? anchor)
    {
        if (Sounds == null || ev.Data.Str("child") is not { } child)
            return false;
        if (!Sound.TryGetChild(child, anchor, out var handle))
            return false;
        if (ev.Data.Str("parent") is not { } parentName)
            return false;
        if (Resolve(parentName, def, anchor) is not { } host)
            return false;
        Sound.Attach(handle, host);
        return true;
    }

    // The node-reparent form of OBJECT_ADD_CHILD / OBJECT_DELETE_CHILD: how a cutscene composes
    // itself. `generic_intro` moves `camera1` into `piratezep` so its authored keyframes read as
    // offsets inside the airship's frame; a delete detaches the child back to the world root, which
    // is where the gamez already keeps `camera1` (docs/formats/anim-definitions/cutscenes.md).
    // ⚠ The LOCAL transform is kept, never the global one: preserving the world pose would leave
    // every keyframe in world space, which is the whole reason the intro camera flies under the sea.
    private bool HandleReparent(AnimEvent ev, AnimDefinition def, Node3D? anchor, bool adopt)
    {
        if (ev.Data.Str("child") is not { } childName || ev.Data.Str("parent") is not { } parentName)
            return false;
        var named = Resolve(parentName, def, anchor);
        if (named == null)
            return false;
        var child = Resolve(childName, def, anchor);
        // Inside a library copy this runtime staged: a staged actor's own choreography runs on
        // ordinary ticks long after the ranged call that staged it (the passenger's wave loop is
        // re-entered from a poll), and its add-child of a further root (the flare) must build it.
        if (child == null && adopt && ResolveLibraryRoot != null
            && (_deathCallDepth > 0 || _rangeCallDepth > 0 || _missionCallDepth > 0
                || StagedCopyRootOf(anchor) != null))
            child = ResolveLibraryRoot(childName, named, null);
        if (child == null)
            return false;
        // A delete names the parent it detaches FROM, so a child hanging somewhere else is a
        // no-op rather than a move: the intro's own RESET_STATE names a parent it never had.
        var parent = adopt ? named : _root;
        if (!adopt && child.GetParent() != named)
            return true;
        if (parent == null || parent == child || child.IsAncestorOf(parent))
            return false;
        if (child.GetParent() != parent)
        {
            Reparent(child, parent);
            _opsApplied++;
        }
        // ⚠ A staged copy is TopLevel from its placement (PlaceNodeAt), which pins it to the
        // world: adopted under a moving parent it would hang where the call site was. The original
        // instances it as an ordinary child at its authored pose, so the adoption does the same.
        if (adopt && child.TopLevel)
        {
            child.TopLevel = false;
            child.Transform = RestOf(child);
        }

        return true;
    }

    // Resolves a single node name for this definition, the compiled symbol table first, then the
    // scoped tier chain. Forwards to NameResolver.Resolve, which owns the tier order (anchor
    // subtree, own template roots, global).
    private Node3D? Resolve(string name, AnimDefinition def, Node3D? anchor) =>
        _resolver.Resolve(name, def, anchor);

    // Resolves a name path for one definition, narrowest scope first. The tier order, call
    // anchor's subtree, then the DEFINITION'S OWN template root(s), then unless LOCAL_NODES_ONLY
    // the whole index, is resolver-owned and structural (see NameResolver.ResolveScoped):
    // this class holds no resolution primitive it could compose in a different order.
    private List<Node3D> ResolveScoped(List<string> path, AnimDefinition def, Node3D? anchor) =>
        _resolver.ResolveScoped(path, def, anchor);

    // The site a CALL_ANIMATION hands its callee: the target node plus the AT_NODE trailing offset
    // in that node's frame. Null when the call names no target, and the caller's anchor stands.
    // ⚠ Resolve the target in the CALLER's namespace; that is where it is written. A
    // named-but-unresolvable target falls back to the caller's anchor rather than dropping the
    // call, and is counted, because silently mis-placing an effect is what this method prevents.
    private (Node3D? Node, Vector3 Offset) CallTargetSite(AnimEvent ev, AnimDefinition def, Node3D? anchor)
    {
        string? targetName = null;
        Vector3 offset = Vector3.Zero;
        // AT_NODE and WITH_NODE both resolve here; the placement does not tell them apart
        // (docs/org/sequences.md, the CALL_ANIMATION section).
        if (ev.Data.Obj("parameters")?.Union() is { Value: Dictionary<string, object?> p })
        {
            var atNode = new AnimData(p);
            targetName = atNode.Str("node");
            offset = atNode.Vec3("position"); // absent → zero
        }
        targetName ??= ev.Data.Str("operand_node");
        if (targetName == null)
            return (null, Vector3.Zero);

        var resolved = Resolve(targetName, def, anchor);
        if (resolved != null)
            _retargeted++;
        else
            _retargetUnresolved++;
        if (resolved == null && AnchorWarnAnimNames != null && (def.AnimName ?? def.Name) is { } caller
            && AnchorWarnAnimNames.Contains(caller) && _anchorWarned.Add($"{caller}|{targetName}"))
        {
            Log.Warn("anim", $"{AnchorWarnLabel ?? "rig"}: '{caller}' calls '{ev.Data.Str("name")}' onto '{targetName}', which this airframe has no node for, it lands on the airframe root instead");
        }
        // Once per distinct (callee, target, caller) triple: the poll idiom re-issues its calls
        // every frame, so an unconditional line here would bury the log.
        if (DebugMotions && _retargetsLogged.Add($"{ev.Data.Str("name")}|{targetName}|{def.AnimName}"))
            Log.Debug("anim", $"anim: retarget '{ev.Data.Str("name")}' onto '{targetName}' ({(resolved != null ? resolved.GetMeta(NameMeta).AsString() : "UNRESOLVED")}) [caller {def.AnimName}]");
        return (resolved, offset);
    }

    // Whether def's placed root should level to world axes (LevelPlacedTemplateNames) rather than
    // inherit its caller's rotation.
    private bool LevelsTemplate(AnimDefinition def) =>
        LevelPlacedTemplateNames != null && LevelPlacedTemplateNames.Contains(def.AnimName ?? def.Name);

    // The basis this callee's relocated copy takes, when the play now dispatching carries one and
    // named this callee (OrientedCallAnimNames). Null leaves the copy on the axis it stands on.
    private Basis? OrientedCall(AnimDefinition def) =>
        _callPlacementOrient is { } basis && OrientedCallAnimNames != null
        && OrientedCallAnimNames.Contains(def.AnimName ?? def.Name) ? basis : null;

    // Whether any live motion is still driving something inside a template copy: the hold that
    // keeps a finished effect's root revealed, since a reveal is paired with the EFFECT's life and
    // not its instance's.
    // ⚠ Ask this of the ROOT, never of the def that just finished; a template's pieces are
    // routinely driven by a callee's motions, so a def-scoped hold hides the copy out from under
    // them. ⚠ Exclude an unbounded spin, which would pin the template revealed for the session.
    private bool TemplateStillAnimated(IReadOnlyList<Node3D?> roots)
    {
        foreach (var motion in Motions.Live)
        {
            if (motion is SpinMotion { Endless: true } || !IsInstanceValid(motion.Target))
                continue;
            foreach (var root in roots)
                if (root != null && (root == motion.Target || root.IsAncestorOf(motion.Target)))
                    return true;
        }

        return false;
    }

    private void SweepEffectTtls(float dt)
    {
        if (_effectTtls.Count == 0)
            return;
        _effectClock += dt;
        for (int i = _effectTtls.Count - 1; i >= 0; i--)
        {
            if (_effectClock < _effectTtls[i].Deadline)
                continue;
            var (def, anchor, _) = _effectTtls[i];
            _effectTtls.RemoveAt(i);
            // Stop, not just drop the instance, so a stop-less emitter stops emitting.
            Stop(def.AnimName, anchor);
            // ⚠ Keep the direct teardown too. Stop reaches resources only through a live instance,
            // and an effect whose sequences already ended has none, which would make this sweep a
            // silent no-op for every effect that outlives only its own emitters.
            TearDownResourcesOf(def, anchor);
        }
    }

    // Whether a finished instance may actually be retired, an instance-retirement question that
    // consults the motions, which is why it stays here rather than moving with them. The narrowness
    // of the hold, and the measurement behind it, are on Airborne.
    private bool Retirable(AnimInstance inst) =>
        inst.Finished && !Motions.Airborne(inst.Def, inst.Anchor);

    // Both sequence events act on the live instance of (def, anchor); on the instant/bootstrap
    // dispatch path no instance exists and both are no-ops.
    private AnimInstance? InstanceOf(AnimDefinition def, Node3D? anchor) =>
        _instances.FirstOrDefault(i => i.Def == def && i.Anchor == anchor);

    private void CallSequence(AnimDefinition def, Node3D? anchor, string name)
    {
        if (InstanceOf(def, anchor) is not { } inst)
            return;
        if (!inst.CallSequence(name))
            Count("CallSequence(missing)");
    }

    private void StopSequence(AnimDefinition def, Node3D? anchor, string name)
    {
        if (InstanceOf(def, anchor) is not { } inst)
            return;
        if (!inst.StopSequence(name))
            Count("StopSequence(missing)");
        foreach (var seq in def.Sequences)
        {
            if (string.Equals(seq.Name, name, StringComparison.OrdinalIgnoreCase))
                Motions.HaltLaunchedBy(def, anchor, seq.Events);
        }
    }

    // Evaluates one IF/ELSEIF condition; every kind the data uses is answerable, and semantics per
    // kind are in docs/formats/anim-definitions.md.
    // ⚠ Two units bite: compiled PlayerRange is metres SQUARED, and compiled AnimHealth is a
    // "damaged down to" threshold, so an undamaged object fails it.
    // ⚠ An unparseable or unknown condition returns false. Skipping a branch is the safe
    // direction; a branch that should not have run poses objects wrongly.
    private bool EvaluateCondition(AnimData? condition, AnimDefinition def, Node3D? anchor)
    {
        if (condition?.Union() is not { } union)
        {
            CountCondition("<absent>", false);
            return false;
        }
        var (kind, value) = union;
        var obj = value is Dictionary<string, object?> fields ? new AnimData(fields) : null;
        float num = AnimData.AsNum(value) ?? 0f;
        bool result = kind switch
        {
            // The exe's roll is inclusive (`draw <= threshold`); NextDouble() is [0,1) so <=
            // matches it exactly (a strict < would silently exclude the num==0 case's draw==0.0).
            "RandomWeight" => _rng.NextDouble() <= num,
            "AnimationLod" => QualityLod >= (int)num,
            // The 4-byte value slot is unused for these two: the reader form takes no
            // argument (`IF HW_RENDER`) and every compiled instance stores 0, so the
            // condition is the runtime flag itself.
            "HwRender" => true,
            "PlayerFirstPerson" => FirstPersonView?.Invoke() ?? false,
            "PlayerRange" => anchor != null
                             && NearestPlayerDistanceSquared(WorldPos(anchor)) <= num,
            // ANIM_HEALTH gates damage effects: "if this object has been worn down to N".
            // Read against the LIVE per-instance HP, not the def's authored value, so a
            // tower damaged to 30 smokes while its undamaged siblings do not.
            "AnimHealth" => HealthOf(def, anchor) <= num,
            "AnimHealthRange" => obj != null
                                 && HealthOf(def, anchor) >= (obj.Num("min") ?? 0f)
                                 && HealthOf(def, anchor) <= (obj.Num("max") ?? 0f),
            "NodeActive" => ConditionNode(value, def, anchor) is { } n && n.Visible,
            "NodeBelowAlt" => obj != null
                              && ConditionNode(obj.Get("node_index") ?? obj.Get("node"), def, anchor)
                                 is { } n2
                              && WorldPos(n2).Y < (obj.Num("altitude") ?? 0f),
            // NODE_UNDERCOVER (the reader spells it NODE_NEAR_GROUND): a vertical probe of the
            // authored signed length from the node, true when it meets world geometry. The
            // operand needs decoding first, and the probe is the surface read.
            "NodeUndercover" => obj != null
                                && ConditionNode(obj.Get("node_index") ?? obj.Get("node"), def, anchor)
                                   is { } n3
                                && NodeUndercover(n3, def, anchor, UndercoverReach(obj.Num("distance") ?? 0f)),
            _ => false,
        };
        CountCondition(kind, result);
        // --debug-anim: each condition on first evaluation and thereafter only when its verdict
        // flips. The poll idiom re-evaluates every frame, so logging each one buries the log.
        if (DebugMotions && Flipped(kind, anchor, result))
        {
            var at = anchor == null ? "<global>"
                : $"{NameOf(anchor)} {WorldPos(anchor).Snapped(Vector3.One)}";
            Log.Debug("anim", $"anim/debug: cond {kind}({Describe(value)}) on {at} [player {PlayerPos().Snapped(Vector3.One)}] → {(result ? "TRUE" : "false")}");
        }
        return result;
    }

    // The live HP an ANIM_HEALTH threshold tests against: the registered destructible instance for
    // this (def, anchor) pair, falling back to the def's authored value when the pair is not a
    // registered destructible (an unanchored evaluation, or a def whose NAME resolved nothing at
    // bootstrap). The fallback is the def's own authored constant, so anything the registry does
    // not cover reads a fixed value.
    private float HealthOf(AnimDefinition def, Node3D? anchor) =>
        _destructibles.Get(def, anchor)?.Health ?? def.Health;

    // NODE_UNDERCOVER's operand is the compiled record's raw 4-byte slot, which the extraction
    // types as u32 while it holds the IEEE-754 bit pattern of a SIGNED length in metres; the
    // reader form spells the same argument as a plain number. Reinterpreting only above 2^24
    // separates them, since no authored probe is 16.7 million metres long. Census and the sign
    // convention: docs/formats/anim-definitions.md.
    private float UndercoverReach(float raw) =>
        raw >= 16777216f && raw <= uint.MaxValue && raw == Mathf.Floor(raw)
            ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)raw))
            : raw;

    // The condition's probe: a vertical segment of `reach` metres from the node, positive up and
    // negative down, true when it meets world geometry. No mask wired means no collision world,
    // which the original answers false the same way (docs/org/sequences.md).
    private bool NodeUndercover(Node3D node, AnimDefinition def, Node3D? anchor, float reach)
    {
        if (ContactMask == 0 || Mathf.IsZeroApprox(reach) || !IsInstanceValid(node))
            return false;
        if (node.GetWorld3D()?.DirectSpaceState is not { } space)
            return false;
        // The node's ORIGIN is what the original casts from, but an absolute-modelled subtree
        // holds its vertices in world space under an identity transform, so its origin is the map
        // corner; VisualOriginOf is that same point corrected, and needs the node in the tree.
        var from = node.IsInsideTree() ? VisualOriginOf(node) : WorldPos(node);
        using var query = PhysicsRayQueryParameters3D.Create(
            from, from + new Vector3(0f, reach, 0f), ContactMask);
        query.Exclude = UndercoverExclusion(UndercoverHost(node, def, anchor));
        using var hit = space.IntersectRay(query);
        return hit.Count > 0;
    }

    // The body the probe must not see: the definition's own root above the probed node, else the
    // anchor. ⚠ Never the anchor alone. A callee runs on its CALL SITE's anchor here
    // (docs/org/sequences.md, CALL_ANIMATION). cargozep1_crash, called by a hydrogen tank's death,
    // is anchored on the tank. An exclusion read off that anchor leaves the hull's own gasbags
    // under the 65 m probe. The wreck then holds in the air, main_altitude_check having opened at
    // cruise altitude.
    private Node3D UndercoverHost(Node3D node, AnimDefinition def, Node3D? anchor)
    {
        if (def.RootName is { } root)
        {
            for (Node? n = node; n is Node3D n3; n = n.GetParent())
            {
                if (string.Equals(NameOf(n3), root, StringComparison.OrdinalIgnoreCase))
                    return n3;
            }
        }
        return anchor != null && IsInstanceValid(anchor) ? anchor : node;
    }

    // The original clears the probed node's own collidable bit for the duration of the cast, so a
    // body cannot detect itself. One gamez node is a whole subtree of collider bodies here, and
    // the probe is authored on a PART of a vehicle (killpzep casts from a gasbag down through the
    // hull it hangs under), so the exclusion has to cover the host, not the part. Cached per host:
    // the poll idiom re-evaluates every frame and a zeppelin carries hundreds of bodies.
    private Godot.Collections.Array<Rid> UndercoverExclusion(Node3D host)
    {
        ulong key = host.GetInstanceId();
        if (_undercoverExclude.TryGetValue(key, out var cached))
            return cached;
        var rids = new Godot.Collections.Array<Rid>();
        var stack = new Stack<Node>();
        stack.Push(host);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is CollisionObject3D body)
                rids.Add(body.GetRid());
            for (int i = 0, count = n.GetChildCount(); i < count; i++)
                stack.Push(n.GetChild(i));
        }
        return _undercoverExclude[key] = rids;
    }

    // Records a whole pooled copy's authored pose while it is still as-built. RestOf captures a
    // node the first time something MOVES it, which on a reused copy is a play that already
    // displaced it, so the pool's restore needs the spawn pose banked before anything runs.
    private void PrimeRest(Node3D node)
    {
        if (!_rest.ContainsKey(node))
            _rest[node] = node.Transform;
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
            if (node.GetChild(i) is Node3D c)
                PrimeRest(c);
    }

    // Restores every node a def's events touched to its authored rest pose (_rest, recorded the
    // first time a motion disturbed it). The membership check confines this to nodes that actually
    // MOVED, the ballistic debris and any FROM_TO movers, so healthy/destroyed visibility nodes
    // (never transformed) are left to RESET_STATE.
    private void RestoreRestPoses(AnimDefinition def, Node3D? anchor)
    {
        var seqs = def.ResetState != null ? def.Sequences.Append(def.ResetState) : def.Sequences;
        foreach (var seq in seqs)
        {
            foreach (var ev in seq.Events)
            {
                foreach (var node in Targets(ev, def, anchor))
                {
                    if (_rest.TryGetValue(node, out var rest))
                    {
                        node.Transform = rest;
                    }
                }
            }
        }
    }

    // A CALL_ANIMATION naming a destructible's own death definition is that destructible's kill,
    // run through its pool on its own anchor: a gasbag burn "destroys" the ring's other cannons this
    // way, and a plain Start on the CALLER's anchor hid their guns through the symbol table while
    // their pools stayed healthy and the broadside kept firing from them. Null when the def is no
    // sole registered pool (a template with several copies stays a plain call); the instance,
    // untouched, when it is already dead or out of the world.
    private DestructibleRegistry.Instance? KillCalledDestructible(AnimDefinition target)
    {
        DestructibleRegistry.Instance? own = null;
        foreach (var inst in _destructibles.All)
        {
            if (inst.Def != target)
                continue;
            if (own != null)
                return null;
            own = inst;
        }
        if (own == null)
            return null;
        if (own.Dormant || own.Status == DestructibleRegistry.State.Destroyed)
            return own;
        own.Health = 0f;
        own.Status = DestructibleRegistry.State.Destroyed;
        // In the `damage:` family on purpose, since this kill spends no HP. Without a line of its
        // own, a sweep for what died reads a demolished part as one nobody ever touched.
        Log.Info("anim", $"damage: {NameOf(own.Anchor)} DESTROYED by a call to '{target.AnimName ?? target.Name}', death sequence run");
        if (HealthyNodeNameOf(own.Def) is { } healthyNode)
            DestructibleKilled?.Invoke(healthyNode);
        RunDeathSequence(own);
        return own;
    }

    // The one place a destructible's pool is lowered, shared by a local hit and a replicated one
    // so the two cannot stage or die differently.
    private void SpendHealth(DestructibleRegistry.Instance inst, float health, float healthDamage)
    {
        float before = inst.Health;
        int stageBefore = inst.DamageStage;
        inst.Health = Math.Max(0f, health);
        ApplyDamageStages(inst);
        bool destroyed = inst.Health <= 0f;
        if (destroyed)
        {
            inst.Status = DestructibleRegistry.State.Destroyed;
            if (HealthyNodeNameOf(inst.Def) is { } healthyNode)
                DestructibleKilled?.Invoke(healthyNode);
            RunDeathSequence(inst);
        }
        // ⚠ Never let the ceiling swallow a death. It exists to keep a firefight's chip hits out of
        // the log, and which parts died is what a sortie is read back for.
        if (destroyed || _damagesLogged < 12)
        {
            if (!destroyed)
                _damagesLogged++;
            Log.Info("anim", $"damage: -{healthDamage:0.##} on {NameOf(inst.Anchor)} HP {before:0.##}→{inst.Health:0.##}{(destroyed ? " DESTROYED, death sequence run" : $" [stage {inst.DamageStage}]")}");
        }
        if (destroyed || inst.DamageStage != stageBefore)
            DestructibleDamaged?.Invoke(inst);
    }

    // Runs a destructible's death the instant its HP reaches zero.
    // ⚠ Play ALL the def's Initial sequences through Start; never try to pick "the death sequence"
    // out by name. The swap sits in a sequence whose name varies and is only reliably Initial
    // (docs/formats/destructibles.md).
    // ⚠ Withhold the ApplyDeathSwap fallback when the swap is one CALL_ANIMATION down, or when the
    // def authors its own visible death; firing it blanks a wreck early or swaps an archway.
    private void RunDeathSequence(DestructibleRegistry.Instance inst)
    {
        _deathCallDepth++;
        _dyingInstances.Push(inst);
        try
        {
            // The same span _deathCallDepth brackets as the whole burst.
            using (PerfSample.Scope(PerfSite.DebrisSpawn))
            {
                Start(inst.Def, inst.Anchor, protectSelfInvalidate: true);
                RunDeathSlot(inst.Def, inst.Anchor);
            }
        }
        finally
        {
            _dyingInstances.Pop();
            _deathCallDepth--;
        }
        if (ChainedSwapTarget(inst.Def) is { } chained)
        {
            inst.ChainedDeathDef = chained;
            return;
        }
        if (AuthorsVisibleDeath(inst.Def))
            return;
        ApplyDeathSwap(inst);
    }

    // Dispatches the def's compiled destruction slot alongside the Initial sequences Start just
    // ran; it carries most of the install's death calls, which the listed sequences never reach.
    // ⚠ Run it as a runner ON the live instance, so its timed events advance with the death and a
    // reset's Stop tears it down too. ⚠ Keep the zero advance inside the death bracket and under
    // Start's self-invalidate protection; slots use the consume-the-trigger idiom.
    private void RunDeathSlot(AnimDefinition def, Node3D? anchor)
    {
        if (def.DeathSlot is not { } slot)
            return;
        var live = InstanceOf(def, anchor);
        if (live == null)
        {
            live = new AnimInstance(def, anchor);
            _instances.Add(live);
            OnInstanceStarted?.Invoke(def, anchor);
        }
        live.AddRunner(slot);
        _startDepth++;
        _startingInstances.Push((live, true));
        try
        {
            live.Advance(this, 0f);
        }
        finally
        {
            _startingInstances.Pop();
            _startDepth--;
        }
        // A slot that drained in the zero advance is retired by the next Advance sweep, which
        // runs the full finish path (emitters, TTLs, template hides), nothing special here.
    }

    // The first CALL_ANIMATION target, one level down from def's own Initial sequences, whose OWN
    // sequences author the healthy/destroyed swap, an OBJECT_ACTIVE_STATE that activates a
    // destroyed/dbase-role node or deactivates a healthy-role one. Resolved via the same
    // _program.ByAnimName the CALL_ANIMATION dispatch itself uses. Null for the ~90%/~10% cases the
    // def's own sequences/RESET_STATE already cover (gate1, the AA guns, everything else).
    private AnimDefinition? ChainedSwapTarget(AnimDefinition def)
    {
        foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
            foreach (var ev in seq.Events)
                if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } callName)
                    foreach (var target in _program.ByAnimName(callName))
                        if (AuthorsSwap(target))
                            return target;
        return null;
    }

    // The generic healthy-to-destroyed swap, for destructibles that declare the pair but author no
    // explicit swap sequence. ⚠ Read it off the def's own RESET_STATE targets, never a world-wide
    // name scan, and apply it only when RESET names a destroyed node; an object with no destroyed
    // variant must be left intact rather than blanked. Match the exact role words, not a suffix.
    private void ApplyDeathSwap(DestructibleRegistry.Instance inst)
    {
        if (inst.Def.ResetState is not { } reset)
            return;
        bool hasDestroyed = reset.Events.Any(ev => ev.Kind == "ObjectActiveState"
            && RoleName(ev).Contains("destroyed", StringComparison.OrdinalIgnoreCase));
        if (!hasDestroyed)
            return;
        foreach (var ev in reset.Events)
        {
            if (ev.Kind != "ObjectActiveState")
                continue;
            var name = RoleName(ev);
            bool? active =
                name.Contains("healthy", StringComparison.OrdinalIgnoreCase) ? false
                : name.Contains("destroyed", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("dbase", StringComparison.OrdinalIgnoreCase) ? true
                : null;
            if (active is not { } state)
                continue;
            foreach (var node in Targets(ev, inst.Def, inst.Anchor))
                SetSubtreeActive(node, state);
        }
    }

    // The pose a death ends in, read off the sequences RunDeathSequence would play: every
    // OBJECT_ACTIVE_STATE switching a node off, and the destroyed/dbase role nodes switched on.
    // ⚠ Leave a non-role piece switched on mid-death off; it flies and is hidden, so switching it
    // on parks debris at its rest pose. A def with no role swap of its own takes the RESET-derived
    // one, under the same visible-death withholding RunDeathSequence applies.
    private void ApplyDeathPose(DestructibleRegistry.Instance inst)
    {
        bool swapped = false;
        foreach (var (def, seq) in DeathSequencesOf(inst.Def))
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectActiveState")
                    continue;
                var name = RoleName(ev);
                bool active = ev.Data.Bool("state");
                bool destroyedRole = name.Contains("destroyed", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("dbase", StringComparison.OrdinalIgnoreCase);
                bool healthyRole = name.Contains("healthy", StringComparison.OrdinalIgnoreCase);
                if (active && !destroyedRole)
                    continue;
                foreach (var node in Targets(ev, def, inst.Anchor))
                {
                    SetTargetActive(node, active);
                    swapped |= destroyedRole || healthyRole;
                }
            }
        }
        if (!swapped && !AuthorsVisibleDeath(inst.Def))
            ApplyDeathSwap(inst);
    }

    // The sequences a death plays, with the def each resolves its targets through: the def's own
    // Initial sequences, its compiled destruction slot, the ON_CALL sequences those two reach
    // through CALL_SEQUENCE, and every sequence of a chained swap target (AuthorsSwap accepts its
    // swap in an ON_CALL sequence too).
    private IEnumerable<(AnimDefinition Def, AnimSequence Seq)> DeathSequencesOf(AnimDefinition def)
    {
        foreach (var seq in OwnDeathSequencesOf(def))
            yield return (def, seq);
        if (ChainedSwapTarget(def) is { } chained)
            foreach (var seq in chained.Sequences)
                yield return (chained, seq);
    }

    // ⚠ Follow CALL_SEQUENCE into def's own ON_CALL sequences. Nothing replays a call in a pose
    // applied without choreography, so a piece hidden from a called sequence is left standing:
    // susp_bridge parks part1 and part5 in part1_fire_puffer and part5_fire_puffer. Breadth-first
    // over a seen set, because the authored call graph is not required to be acyclic.
    private IEnumerable<AnimSequence> OwnDeathSequencesOf(AnimDefinition def)
    {
        var seen = new HashSet<AnimSequence>();
        var pending = new Queue<AnimSequence>();
        foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
            if (seen.Add(seq))
                pending.Enqueue(seq);
        if (def.DeathSlot is { } slot && seen.Add(slot))
            pending.Enqueue(slot);
        while (pending.Count > 0)
        {
            var seq = pending.Dequeue();
            yield return seq;
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "CallSequence" || ev.Data.Str("name") is not { } called)
                    continue;
                foreach (var target in def.Sequences)
                    if (target.Name.Equals(called, StringComparison.OrdinalIgnoreCase)
                        && seen.Add(target))
                        pending.Enqueue(target);
            }
        }
    }

    // Does this event come from the definition's own death choreography, one of the sequences
    // OwnDeathSequencesOf names? Reference identity on the authored event, and a test of where it
    // was authored rather than of what is dying. A death's later events land minutes after its
    // burst has returned. ⚠ Read the chain through that one method. A wreck clearing itself away
    // from a called ON_CALL sequence is as much a death as one doing it from an Initial sequence.
    private bool AuthoredInDeathChoreography(AnimDefinition def, AnimEvent ev) =>
        OwnDeathSequencesOf(def).Any(seq => seq.Events.Contains(ev));

    // Keeps a destructible's HP pool in step with a healthy/destroyed OBJECT_ACTIVE_STATE swap
    // dispatched outside DamageAt's own kill, such as a start-state script authoring an object
    // destroyed before the player arrives. Without this the pool stays Healthy at full HP
    // while the node reads destroyed, so a later hit replays the whole death sequence on an
    // object that already looks dead. A live kill reaches this same event after DamageAt has
    // already set the pool, so the status guards below make it a no-op there.
    private void SyncDestructiblePool(AnimEvent ev, AnimDefinition def, Node3D? anchor)
    {
        if (_destructibles.Get(def, anchor) is not { } inst)
            return;
        var name = RoleName(ev);
        bool active = ev.Data.Bool("state");
        bool dbaseRole = name.Contains("dbase", StringComparison.OrdinalIgnoreCase);
        bool destroyedRole = dbaseRole
            || name.Contains("destroyed", StringComparison.OrdinalIgnoreCase);
        bool healthyRole = name.Contains("healthy", StringComparison.OrdinalIgnoreCase);
        // A RESET_STATE is the baseline of a whole object, so a `dbase` switched on there is the
        // ground under it and never half a death.
        if (active && dbaseRole && AuthoredInResetState(def, ev))
            return;
        if (active && dbaseRole && HealthyRootStands(inst))
            return;
        if ((active && destroyedRole) || (!active && healthyRole))
        {
            if (inst.Status != DestructibleRegistry.State.Destroyed)
            {
                inst.Health = 0f;
                inst.Status = DestructibleRegistry.State.Destroyed;
            }
        }
        else if ((active && healthyRole) || (!active && destroyedRole))
        {
            // Dying is not reviving. A wreck that clears itself away switches its own destroyed-role
            // nodes back off, so only a baseline or another script may put the pool back.
            if (inst.Status == DestructibleRegistry.State.Destroyed
                && !AuthoredInDeathChoreography(def, ev))
            {
                inst.Health = inst.MaxHealth;
                inst.Status = DestructibleRegistry.State.Healthy;
            }
        }
    }

    // Is this pool's own healthy geometry still standing? A `dbase` node is the wreck's ground
    // base, so switching it on is normally half of a death. The two 8-inch cannons author it ON in
    // their RESET_STATE beside `healthy` ACTIVE, where it is a concrete plinth under a live gun and
    // not a death at all (docs/formats/destructibles.md). Every genuine death that touches `dbase`
    // also switches its healthy-role node OFF, so the death still reads.
    private bool HealthyRootStands(DestructibleRegistry.Instance inst) =>
        inst.Def.RootName is { Length: > 0 } root
        && root.Contains("healthy", StringComparison.OrdinalIgnoreCase)
        && DamageNodeOf(inst.Def, inst.Anchor).Visible;

    private bool Flipped(string kind, Node3D? anchor, bool result)
    {
        var key = (kind, anchor);
        if (_condLast.TryGetValue(key, out bool prev) && prev == result)
            return false;
        _condLast[key] = result;
        return true;
    }

    /// <summary>Where a range gate reads the players from, which is not where a cutscene's own
    /// choreography has flown their models to (<see cref="PlayerRangeHeld"/>). Empty until the
    /// first flying frame, so a film that takes the session during the bootstrap still reads live
    /// and every mission intro keeps the answer it had.</summary>
    private IReadOnlyList<Vector3> RangePositions()
    {
        if (PlayerRangeHeld && _rangePositions.Count > 0)
            return _rangePositions;
        var live = PlayerPositions?.Invoke();
        return live is { Count: > 0 } ? live : new[] { PlayerPos() };
    }

    // The last flying pose, refreshed once a frame so a held range gate has one to answer with.
    private void SampleRangePositions()
    {
        if (PlayerRangeHeld)
            return;
        _rangePositions.Clear();
        var live = PlayerPositions?.Invoke();
        if (live is { Count: > 0 })
            _rangePositions.AddRange(live);
        else if (PlayerPosition != null)
            _rangePositions.Add(PlayerPosition());
    }

    private Vector3 PlayerPos()
    {
        if (PlayerPosition != null)
            return PlayerPosition();
        return IsInsideTree() && GetViewport().GetCamera3D() is { } cam ? cam.GlobalPosition : Vector3.Zero;
    }

    // The nearest human's squared distance to a world point, a PLAYER_RANGE condition's own answer,
    // the same nearest-of-every-player rule TickDeferredByRange's EXECUTION_BY_RANGE
    // gate already uses, so a wash or door gated by a burst near player 4 fires even while player 1
    // sits kilometres off. Falls back to PlayerPos when no PlayerPositions seam is wired.
    private float NearestPlayerDistanceSquared(Vector3 point)
    {
        float d2 = float.MaxValue;
        foreach (var p in RangePositions())
            d2 = Mathf.Min(d2, point.DistanceSquaredTo(p));
        return d2 == float.MaxValue ? point.DistanceSquaredTo(PlayerPos()) : d2;
    }

    private Node3D? ConditionNode(object? reference, AnimDefinition def, Node3D? anchor)
    {
        if (reference is string name)
            return IsSelfNodeRef(name) ? anchor : Resolve(name, def, anchor);
        if (AnimData.AsNum(reference) is not { } idx)
            return null;
        if (idx > 1e9f)
            return InputNodeOf(def, anchor) ?? anchor; // negative sentinel = INPUT_NODE
        int i = (int)idx;
        if (i < 1 || i > def.NodeList.Count)
            return null;
        return Resolve(def.NodeList[i - 1], def, anchor);
    }

    private void CountCondition(string kind, bool result)
    {
        var (t, f) = _conditions.TryGetValue(kind, out var c) ? c : (0, 0);
        _conditions[kind] = result ? (t + 1, f) : (t, f + 1);
    }

    private void ReportRetargets()
    {
        if (_retargeted == 0 && _retargetUnresolved == 0)
            return;
        Log.Info("anim", $"anim: {_retargeted} call(s) retargeted onto a named node{(_retargetUnresolved > 0 ? $", {_retargetUnresolved} target(s) unresolved" : "")}");
    }

    // The WAIT_FOR_COMPLETION holds armed so far, by callee, printed with the bootstrap census.
    // Anything armed later is outside this print by construction; a probe reads WaitsInstalled
    // instead, and an abandoned hold reports itself where it happens.
    private void ReportWaits()
    {
        if (_waitsByCallee.Count == 0)
            return;
        var parts = _waitsByCallee.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}");
        Log.Info("anim", $"anim: {_waitsInstalled} WAIT_FOR_COMPLETION hold(s) armed during bootstrap: {string.Join(", ", parts)}");
    }

    private void ReportConditions()
    {
        if (_conditions.Count == 0)
            return;
        var parts = _conditions.OrderByDescending(kv => kv.Value.True + kv.Value.False)
            .Select(kv => $"{kv.Key} {kv.Value.True}✓/{kv.Value.False}✗");
        Log.Info("anim", $"anim: conditions evaluated (lod {QualityLod}): {string.Join(", ", parts)}");
    }

    // Every REQUIRED node prerequisite reads the state it asks for. Active is what
    // OBJECT_ACTIVE_STATE writes, the subtree's visibility. A path that resolves to no node
    // passes: the optional entries and MINIMUM_TO_SATISFY are parsed, not enforced.
    // ⚠ Bind the leaf through its compiled node pointer first, as Targets binds an event's node.
    // A gasbag finisher starts on the dead CANNON's anchor (its call chain), and the name search
    // from there met the intact panels of the nine other zeppelins sharing the Dante's node names.
    private bool NodePrerequisitesMet(AnimDefinition def, Node3D? anchor)
    {
        foreach (var prereq in def.PrereqNodes)
        {
            if (!prereq.Required)
                continue;
            if (prereq.Ptr is { } ptr && prereq.Path.Count > 0
                && _resolver.ClaimedNode(ptr, prereq.Path[^1], anchor) is { } own)
            {
                if (IsInstanceValid(own) && own.Visible != prereq.Active)
                    return false;
                continue;
            }
            foreach (var node in ResolveScoped(new List<string>(prereq.Path), def, anchor))
                if (IsInstanceValid(node) && node.Visible != prereq.Active)
                    return false;
        }
        return true;
    }

    // The anim-list ACTIVATION_PREREQUISITE, a counter over the def's own callers
    // (docs/formats/anim-definitions.md). ⚠ Gate CALL_ANIMATION with it and nothing else: a
    // zeppelin hull death carries the same shape and is also fired from ZeppelinRuntime's damage
    // model through Play, which owns that kill, and gating that path leaves every zeppelin in the
    // game unkillable.
    private bool AnimPrerequisitesMet(AnimDefinition def)
    {
        if (def.PrereqAnims.Count == 0)
            return true;
        int met = 0;
        foreach (var name in def.PrereqAnims)
            if (HasRun(name))
                met++;
        // An unauthored minimum means every entry, the same "absent is all" rule the objective
        // script's completion counts keep; no shipped carrier leaves it out.
        return met >= (def.PrereqMinToSatisfy > 0 ? def.PrereqMinToSatisfy : def.PrereqAnims.Count);
    }

    // ⚠ STARTED, not finished: the last caller's own animation is on the list it must satisfy, so
    // a rule reading "completed" refuses the very call that completes the count.
    private bool HasRun(string animName)
    {
        foreach (var def in _program.ByAnimName(animName))
            if (_everStarted.Contains(def))
                return true;
        return false;
    }

    private void Count(string kind) =>
        _unhandled[kind] = _unhandled.TryGetValue(kind, out var n) ? n + 1 : 1;

    private void ReportUnhandled()
    {
        if (_unhandled.Count == 0)
            return;
        var top = _unhandled.OrderByDescending(kv => kv.Value).Take(12)
            .Select(kv => $"{kv.Key}×{kv.Value}");
        Log.Info("anim", $"anim: {_unhandled.Count} event kind(s) not yet acted on: {string.Join(", ", top)}{(_unhandled.Count > 12 ? ", …" : "")}");
    }

    // --debug-anim: one line per live motion per second. Headless verification that things
    // actually move (and by how much) without flying a camera at them.
    private void LogMotions(float dt)
    {
        _debugClock += dt;
        if (_debugClock < 1f)
            return;
        _debugClock = 0f;
        // ⚠ Split the contact tally by tier. All clock and no contact is the one outcome every
        // other line reports exactly as a working test, and one tier can carry the total alone.
        if (Motions.ContactLandings + Motions.ClockEndings > 0)
        {
            Log.Debug("anim", $"anim/debug: contact-tested bodies ended: {Motions.ContactLandings} by contact, {Motions.ClockEndings} on their run time (column {Motions.ColumnLandings}/{Motions.ColumnClockEndings}, sweep {Motions.SweepLandings}/{Motions.SweepClockEndings}){(Motions.ContactLandings == 0 ? ", NO CONTACT AT ALL (is a mask wired?)" : "")}");
        }

        var emitting = Emitters.Census.Where(r => r.Emitting).ToList();
        if (emitting.Count > 0)
        {
            int live = 0;
            foreach (var r in emitting)
                live += r.LiveParticles;
            // Name them: several runtimes print this line, so a bare count cannot say whose
            // emitters are running.
            Log.Debug("anim", $"anim/debug: {emitting.Count} active puffer(s), {live} live particle(s): {string.Join(", ", emitting.Select(r => r.Name).Distinct())}");
        }
        // ⚠ Totals before the list, which is capped at 12. A debris piece is routinely past the
        // cap, so reading "it never launched" out of the truncated list is unsound.
        Log.Debug("anim", $"anim/debug: {Motions.Count} live motion(s), {BallisticMotionsLaunched} ballistic launch(es) so far");
        if (Motions.Count == 0)
        {
            return;
        }
        foreach (var m in Motions.Live.Take(12))
        {
            var t = m.Target;
            var name = t.HasMeta(NameMeta) ? t.GetMeta(NameMeta).AsString() : t.Name.ToString();
            var p = t.GlobalPosition;
            // Rotation as well as position: a spin turns a prop in place, so a position-only line
            // reads the same every second whether or not it is running.
            var r = t.Transform.Basis.GetEuler() * (180f / Mathf.Pi);
            // Visibility matters as much as position here: a correctly-animated node inside a
            // subtree the mission deactivated moves perfectly and renders nothing.
            bool shown = t.IsVisibleInTree();
            Log.Debug("anim", $"anim/debug: {name} at ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) rot ({r.X:0.0}, {r.Y:0.0}, {r.Z:0.0}) {(shown ? "visible" : "HIDDEN")}");
        }
        if (Motions.Count > 12)
            Log.Debug("anim", $"anim/debug: … and {Motions.Count - 12} more");
    }

    // One pass of the live instances. Instances can finish, stop each other, and be added by
    // CallAnimation during the walk, so it iterates a copy: newest first, one added during the pass
    // runs in the drain that closes it. A definition outside a fast-forwarded episode takes its
    // whole step on the LAST pass and nothing on the others, so the extra passes never re-step the
    // world around the cutscene at a finer grain than it runs at.
    private void WalkInstances(float dt, int passes, bool last)
    {
        _advancing.Clear();
        _advancing.AddRange(_instances);
        // Only a walk that hit the start budget leaves anything queued this far. Those instances
        // are live but still owed their zero-dt first advance, so this pass steps around them and
        // the drain below gives them the advance they were queued for.
        var owedStarts = _queuedStarts.Count == 0
            ? null
            : new HashSet<AnimInstance>(_queuedStarts.Select(q => q.Inst));
        // Saved and restored rather than set and cleared, so a nested advance cannot end the outer
        // walk's queueing window behind it.
        bool wasWalking = _walkingInstances;
        _walkingInstances = true;
        try
        {
            for (int i = _advancing.Count - 1; i >= 0; i--)
            {
                var inst = _advancing[i];
                if (!_instances.Contains(inst) || owedStarts?.Contains(inst) == true)
                {
                    continue;
                }

                float rate = FastForward?.RateFor(inst.Def) ?? 1f;
                float step = rate > 1f ? dt * rate / passes : (last ? dt : 0f);
                if (step <= 0f)
                {
                    continue;
                }

                inst.Advance(this, step);
                if (Retirable(inst) && _instances.Remove(inst))
                {
                    FinishInputGoverned(inst.Def, inst.Anchor);
                    FinishEffectInstance(inst.Def, inst.Anchor);
                    _templateStage.RetireWhenIdle(inst.Def, inst.Anchor);
                    OnInstanceFinished?.Invoke(inst.Def, inst.Anchor);
                }
            }

            DrainQueuedStarts();
        }
        finally
        {
            _walkingInstances = wasWalking;
            _advancing.Clear();
        }
    }

    // Advances every live motion, then dispatches whatever landed. ⚠ The two halves stay in one
    // method, called from one statement in Advance, because the instance walk must not run between
    // them: an instance whose only hold is a landed piece would be Finished with nothing owed, so
    // it retires and FinishEffectInstance SustainEnds the piece's trail emitter mid-flight.
    private void TickMotions(float dt)
    {
        foreach (var landing in Motions.Tick(dt, FastForward))
        {
            // ⚠ Count the miss here. A landing can outlive its own instance, and CallSequence then
            // has nothing to dispatch into and returns silently.
            bool live = InstanceOf(landing.Def, landing.Anchor) != null;
            // ⚠ Mark the resume BEFORE the dispatch; the sequence's own OBJECT_MOTION can fire in
            // the same instant, and a contact landing is a continuation, not a fresh throw.
            if (landing.ByContact)
                MarkLandingResume(landing.Target);
            if (live)
                CallSequence(landing.Def, landing.Anchor, landing.Bounce);
            else
                Count("ObjectMotion(bounce landed after its instance ended)");
            if (DebugMotions)
                Log.Debug("anim", $"anim/debug: '{landing.Target.Name}' landed at {landing.Target.GlobalPosition}, bounce sequence '{landing.Bounce}'{(live ? "" : ", NO LIVE INSTANCE, dispatched nothing")}");
        }
    }

    // Was this definition reached by a mission trigger? Asked of the CALLING definition rather than
    // of the call stack, so a beat the trigger scheduled for seconds later still instances the
    // library roots it names.
    private bool StartedByMissionTrigger(AnimDefinition def) =>
        def.AnimName is { Length: > 0 } name && _missionTriggerDefs.Contains(name);

    // The staged library copy a node sits inside (its root), or null: the resolver narrows a
    // symbol-table claim to it, and an add-child dispatched inside one may build a further root.
    private Node3D? StagedCopyRootOf(Node3D? at)
    {
        for (Node? n = at; n != null; n = n.GetParent())
            if (n is Node3D node && _stagedCopies.Contains(node))
                return node;
        return null;
    }

    // Whether one definition's name resolution may see a staged template copy. The pool is our
    // stand-in for the private node-tree copy the original hands each definition at load, and a
    // copy nothing in this definition references sits in no scope the original would search, so
    // the bail-out's `pilot` reaches the man in the seat and not the parachutist's body.
    // ⚠ Keyed on the def's own symbol table, never on a template name: any staged template
    // carrying a name an airframe also uses hits this, not just `chuteman`.
    private bool StagingAdmits(AnimDefinition def, Node3D? scope, Node3D node)
    {
        if (_templateStage.SlotOf(node) < 0 || CopyRootOf(node) is not { } root)
            return true;
        // Its own template, matched the way every tier matches, so a staged `.flt` copy still
        // answers to the NAME its definition authors.
        if (IsNamed(def.Name, root) || IsNamed(def.RootName, root))
            return true;
        // The scope this tier is searching already sits inside that copy, a CALL_ANIMATION
        // retargeted onto its call site's copy, which is where its own choreography now lives.
        // ⚠ Not while the definition has a staged copy beside it (docs/org/sequences.md).
        if (CopyRootOf(scope) is { } scopeRoot && scopeRoot.GetInstanceId() == root.GetInstanceId()
            && !HasOwnCopyBeside(def, root))
            return true;
        var name = NameOf(root);
        // A reader-sourced def has no symbol table to ask, so it keeps the old, wider view.
        return string.IsNullOrEmpty(name) || def.NodeRefs.Count == 0 || def.NodeRefs.ContainsKey(name);
    }

    // The copy root is the node directly under the poolN container carrying the slot mark.
    private Node3D? CopyRootOf(Node3D? from)
    {
        Node3D? below = null;
        for (Node? at = from; at != null; at = at.GetParent())
        {
            if (at.HasMeta(PoolSlotMeta))
                return below;
            below = at as Node3D;
        }
        return null;
    }

    // Whether this definition has a staged copy of its own in the same pool slot as `other`, which
    // is the private subtree the original hands it at load. A name both copies carry belongs to
    // that one, so a callee placed on its caller's node must not drive the caller's copy.
    private bool HasOwnCopyBeside(AnimDefinition def, Node3D other)
    {
        if (string.IsNullOrEmpty(def.Name))
            return false;
        int slot = _templateStage.SlotOf(other);
        foreach (var candidate in FindAll(def.Name, null))
        {
            if (CopyRootOf(candidate) is { } own && own.GetInstanceId() != other.GetInstanceId()
                && _templateStage.SlotOf(own) == slot)
                return true;
        }
        return false;
    }

    // Every named node under a subtree, first spelling wins, the same NameMeta stamp the world
    // build puts on each node, since Godot's own Name is sanitized and de-duplicated.
    private void CollectNamed(Node node, Dictionary<string, Node3D> into)
    {
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            var child = node.GetChild(i);
            if (child is Node3D n3d && n3d.HasMeta(NameMeta))
            {
                into.TryAdd(n3d.GetMeta(NameMeta).AsString(), n3d);
            }

            CollectNamed(child, into);
        }
    }

    // The unnamed Node3Ds a rig hangs between its own root and the airframe's nodes. They carry no
    // gamez identity, so an activation addressed to the vehicle has to reach through them.
    private List<Node3D> ShellOf(Node3D rig, Dictionary<string, Node3D> parts)
    {
        var shell = new List<Node3D>();
        var seen = new HashSet<ulong>();
        foreach (var part in parts.Values)
        {
            for (var p = part.GetParent() as Node3D; p != null && p != rig; p = p.GetParent() as Node3D)
            {
                if (!p.HasMeta(NameMeta) && seen.Add(p.GetInstanceId()))
                {
                    shell.Add(p);
                }
            }
        }

        return shell;
    }

    // The part named `name` inside the spawned vehicle this event's anchor belongs to. Walks up
    // from the anchor, so a definition anchored on the rig root (or on anything staged under it)
    // sees the aeroplane's own parts and nothing else's.
    private Node3D? VehiclePart(Node3D? anchor, string name)
    {
        if (_vehicleParts.Count == 0)
        {
            return null;
        }

        for (Node? at = anchor; at != null; at = at.GetParent())
        {
            if (at is Node3D n3d && _vehicleParts.TryGetValue(n3d.GetInstanceId(), out var parts)
                && parts.TryGetValue(name, out var part) && IsInstanceValid(part))
            {
                return part;
            }
        }

        return null;
    }

    // Whether a NAME pattern resolves to this exact node, by instance id, Node3D's inherited
    // equality is unreliable across proxies of one native node (see Node3DIdentity).
    private bool IsNamed(string? pattern, Node3D node)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;
        foreach (var match in FindAll(pattern, null))
        {
            if (match.GetInstanceId() == node.GetInstanceId())
                return true;
        }
        return false;
    }

    // ---- node resolution, the rules live in NameResolver.cs; these are the forwards ----
    // World nodes a definition anchors to, NAME match, symbol narrowing, root lift, all
    // resolver-owned (see NameResolver.Anchors, which also records the once-per-def
    // anchoring census).
    private List<Node3D?> Anchors(AnimDefinition def) => _resolver.Anchors(def);

    // The node a destructible's HP pool answers a weapon hit on: this definition's own
    // ANIMATION_ROOT_NAME node inside the anchor, else the anchor itself. Resolved strictly within
    // the anchor, so a shared root name (`healthy`) cannot bind one instance's pool onto another's.
    // Decode: docs/formats/destructibles.md, "Which node takes the hit".
    private Node3D DamageNodeOf(AnimDefinition def, Node3D anchor)
    {
        if (def.RootName is not { Length: > 0 } root)
            return anchor;
        if (_resolver.SymbolClaims(def, root, anchor, out var bound) && bound != null)
            return anchor.IsAncestorOf(bound) ? bound : anchor;
        var found = FindAll(root, anchor);
        return found.Count > 0 ? found[0] : anchor;
    }

    // The world nodes one event targets. Reader-sourced events may carry a parent→child path;
    // compiled events name a single node (under "node" or "name", which upstream spells
    // inconsistently per event type).
    private List<Node3D> Targets(AnimEvent ev, AnimDefinition def, Node3D? anchor)
    {
        // ⚠ Resolve the self-reference sentinels here (IsSelfNodeRef). No node is named that and
        // the symbol table does not bind it, so without this a self-referencing event resolves to
        // nothing and is silently dropped, and a shot-down hull never falls.
        if ((ev.Data.Str("node") ?? ev.Data.Str("name")) is { } selfRef && IsSelfNodeRef(selfRef))
        {
            var host = InputNodeOf(def, anchor) ?? anchor;
            if (host != null)
                return new List<Node3D> { host };
            _opsUnresolved++;
            _resolver.RecordMissingTarget(def, selfRef, "self-ref-no-anchor");
            return new List<Node3D>();
        }

        // Compiled definitions carry a symbol table binding each referenced name to an exact
        // gamez node index, always prefer it. Name matching resolves C1's `caboose` to the
        // real consist AND to an unrelated `caboose.flt` in the rail yard, and drives both.
        if ((ev.Data.Str("node") ?? ev.Data.Str("name")) is { } refName
            && _resolver.SymbolClaims(def, refName, anchor, out var bound))
        {
            if (bound != null)
                return new List<Node3D> { bound };
            // ⚠ A claimed-but-unbuilt index is not an error, and must not be name-matched around
            // in the shared world; C1's `caboose` resolves to the consist and to a `caboose.flt`.
            // The scoped crash runtime is the exception, via NameResolveFallback.
            if (!NameResolveFallback)
            {
                // ⚠ One narrow rescue: a re-anchored exploder template's meshless parameter nodes
                // stand in for the call site's same-named pieces. Resolve strictly inside the
                // anchor's subtree, never globally.
                if (anchor != null && FindAll(refName, anchor) is { Count: > 0 } scoped)
                    return scoped;
                // ⚠ The other narrow rescue, scoped to the anchor's own vehicle and never the
                // index: a chapter's unplaced copy of a vehicle carries the parts a capture
                // animates, and the rig the mission spawned carries the same names.
                if (VehiclePart(anchor, refName) is { } part)
                    return new List<Node3D> { part };
                _opsUnresolved++;
                _resolver.RecordMissingTarget(def, refName, "index-not-built");
                return new List<Node3D>();
            }
        }

        var path = new List<string>();
        if (ev.Data.List("node_path") is { } nodePath)
        {
            foreach (var p in nodePath)
                if (p is string s)
                    path.Add(s);
        }
        else if ((ev.Data.Str("node") ?? ev.Data.Str("name")) is { } single)
        {
            path.Add(single);
        }
        if (path.Count == 0)
            return new List<Node3D>();
        var targets = ResolveScoped(path, def, anchor);
        if (targets.Count == 0)
        {
            _opsUnresolved++;
            _resolver.RecordMissingTarget(def, string.Join("/", path), "name-no-match");
        }
        return targets;
    }

    // Every world node matching a NAME pattern, optionally restricted to one subtree. A full scan
    // of the node index, and the data calls it constantly, because the poll idiom (If …
    // CallAnimation; Endif; Loop{-1}) re-dispatches its body every frame, so C5's ~400 live poll
    // loops asked for hundreds of resolutions per frame. Forwards to NameResolver, which
    // owns the index, the wildcard matcher, and the memoization. Callers must treat the returned
    // list as read-only.
    private List<Node3D> FindAll(string pattern, Node3D? scope) => _resolver.FindAll(pattern, scope);

    // Any still-visible node named like a destroyed variant that no definition touched:
    // hide it and report, each name is a data-coverage gap (a def we failed to anchor).
    // ⚠ Match 'destroyed' only: a '_dest' suffix rule is WRONG, C1's `ref_tank_dest` is
    // the parent GROUP of the five healthy harbor refuel tanks ("destructible", not
    // "destroyed"), and hiding it wipes the visible tanks.
    private List<string> HideUncoveredDestroyed()
    {
        var hidden = new List<string>();
        foreach (var (node, srcName) in _resolver.Rows)
        {
            if (!srcName.Contains("destroyed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!node.Visible || HasHiddenAncestor(node))
                continue;
            SetSubtreeActive(node, false);
            hidden.Add(srcName);
        }
        return hidden;
    }

    private bool HasHiddenAncestor(Node3D node)
    {
        for (var p = node.GetParent() as Node3D; p != null && p != _root; p = p.GetParent() as Node3D)
            if (!p.Visible)
                return true;
        return false;
    }

    // Godot object identity is by native pointer, not the inherited Equals: two managed proxies
    // can wrap the same native node, so NameResolver's dictionary/tuple keys need this rather than
    // trusting Node3D's own equality.
    private sealed class Node3DIdentity : IEqualityComparer<Node3D>
    {
        public static readonly Node3DIdentity Instance = new();

        public bool Equals(Node3D? x, Node3D? y) => (x?.GetInstanceId() ?? 0) == (y?.GetInstanceId() ?? 0);

        public int GetHashCode(Node3D obj) => obj.GetInstanceId().GetHashCode();
    }
}

public sealed partial class AnimRuntime
{
    /// <summary>One <c>FOG_STATE</c> event as authored: an inline fog, not a zone pick. Each field
    /// is present only when the event carries it, mirroring the compiled flag bits the original's
    /// handler tests before each write; an absent field leaves that global as it was. The colour
    /// is in the zone table's own space (sRGB), the ranges in metres.</summary>
    public readonly record struct FogStateChange(string Name, Color? Color, Vector2? Altitude, Vector2? Range)
    {
        /// <summary>Reads the compiled event's payload (<c>name</c>, <c>color</c>, <c>altitude</c>
        /// min/max, <c>range</c> min/max; <c>type_</c> is the D3D fog mode and ships null).</summary>
        public static FogStateChange From(AnimData d)
        {
            Color? color = d.Obj("color") is { } c
                ? new Color(c.Num("r") ?? 0f, c.Num("g") ?? 0f, c.Num("b") ?? 0f)
                : null;
            return new FogStateChange(d.Str("name") ?? string.Empty, color,
                MinMax(d.Obj("altitude")), MinMax(d.Obj("range")));
        }

        private static Vector2? MinMax(AnimData? pair) =>
            pair is { } p ? new Vector2(p.Num("min") ?? 0f, p.Num("max") ?? 0f) : null;
    }

    /// <summary>What one <see cref="ParkDockingHook"/> made of the nodes it seeds, kept so a caller
    /// reads the state that park LEFT: the same question asked later answers about a node table
    /// other stages have grown and other definitions have dispatched against.</summary>
    /// <param name="Seeded">Nodes the flown airframe's own extend definitions author a FROM pose for.</param>
    /// <param name="SymbolBound">Of those, the ones that definition's compiled symbol table bound.</param>
    /// <param name="Unbound">The rest, as <c>anim/node</c>; empty when the table bound them all.</param>
    public readonly record struct DockingHookPark(int Seeded, int SymbolBound, string Unbound);
}
