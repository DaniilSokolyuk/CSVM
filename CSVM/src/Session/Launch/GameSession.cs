using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.InstantAction;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Tooling;
using CSVM.UI.Boards;
using CSVM.UI.Overlays;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Launch;

/// <summary>
/// The per-launch session node: one aircraft from the player's own extracted game data under orbit
/// controls, or with <c>--fly</c> free flight over the chapter world.
///
/// <see cref="Launcher"/> (Main.tscn's root) owns the bootstrap, the launchscreen and the
/// persistent camera/lighting, and instantiates one of these per launch from the settled
/// <see cref="SessionSpec"/> plus a <see cref="LauncherContext"/>. Menu and CLI share StartSession.
///
/// ⚠ Do not add a Teardown(); return-to-menu is a bare QueueFree and the session subtree frees
/// atomically under <c>_worldRoot</c>. ⚠ Do not parse an arg here: a new flag is a SessionSpec
/// change. Module entry: docs/architecture.md on src/Session/Launch/GameSession.cs.
/// </summary>
public partial class GameSession : Node3D
{
    /// <summary>The cutscene host's node and definition names, in the world build's shape. A
    /// session and the test harness both pass this, so they stand up the same cutscene roots.</summary>
    internal static readonly WorldSession.CutsceneNames CutsceneWorldNames = new(
        CutsceneController.CameraNode, CutsceneController.BarsNode, CutsceneController.IntroAnims);

    private const float HorizonScale = 2.5f;

    // The fraction of the camera's far plane the scaled skydome may reach; past it the dome clips
    // and the clear colour shows through. 0.9 leaves room for the one-frame anchor lag.
    // See HorizonScaleFor.
    private const float HorizonFarFraction = 0.9f;

    // How many near-miss names a failed --node= lookup offers: a usable hint, not a census.
    private const int NodeSuggestCap = 20;

    // How many aeroplanes one generator's wave is built ahead for, and how many one airframe and
    // livery holds however many generators order it. A wave arrives a second or more apart, and a
    // quiet frame refills one. A few deep covers a burst without lengthening the load screen.
    private const int WaveAirframeDepth = 4;
    private const int WaveAirframeCap = 8;

    // The empty stage's squadron ring, metres. Two opposed sides therefore start 2000 m apart,
    // AiModeMachine's decoded attack range, so an --ai= sortie engages without a dead approach.
    private const float SquadronRingRadiusM = 1000f;

    // Smallest orbit radius a synthesized pivot may sit at, so an aim ray passing behind the
    // subject still leaves something to orbit rather than spinning about the eye.
    private const float MinOrbitRadius = 1f;

    // Seconds a downed Versus player watches the crash cam before auto-respawning (R skips early).
    // Respawn is at the player's own spawn point, full HP/ammo, no invulnerability window.
    private const float VersusRespawnDelay = 3f;

    // Nathan Zachary's own zeppelin: the world node escape.zrd's shared MYZEP icon stands for, and
    // the name the original looks up before deciding whether to draw it at all.
    private const string PirateZepNode = "piratezep";

    private static readonly string[] InstanceShaderParams =
        { "node_bias", "csky_fog_on", "csky_light_fade", Mech3.SceneBuilder.OpacityParam };

    // Everything this launch settled, parsed and resolved once (see SessionSpec), the command
    // line verbatim, or the launchscreen's pick (SessionSpec.FromMenu). Every consumer below reads
    // it and nothing re-derives a launch setting; the pristine command line stays on the Launcher.
    private readonly SessionSpec _spec;
    // Per-player pad binding chosen in the launchscreen's join flow (null = derive from the
    // connected roster in AssignPads, which is what every CLI launch does).
    private readonly int[][]? _menuPads;
    // The panes handed to a spectator when their pilot ran out of lives, so a rerun can take them
    // back. Freed with this node otherwise.
    private readonly List<SpectatorCamera> _spectatorCameras = new();
    // The --screenshot=/--shots=/--frames= state machine, see
    // src/Tooling/CaptureDirector.cs's entry. Process-scoped and owned by the Launcher (which
    // Ticks it); held here for the Pending reads that gate display choices during the build.
    private readonly Tooling.CaptureDirector _captureDirector;
    // The master seed every subsystem generator derives from (see Utils.Rng), resolved by the
    // Launcher once per process (pinned runs take the spec's value; everything else draws from
    // the clock) and re-applied here at each session build.
    private readonly ulong _masterSeed;
    // One rig per rendered view: its camera plus the camera-anchored copies only it
    // sees (skydome / cloud deck / whiteout). Exactly one entry in single player,
    // wrapping the main-viewport _camera below, so the 1P render path is unchanged.
    private readonly List<PlayerRig> _rigs = new();
    // Every pane's camera, bound once right after BuildRigs, the session-owned "what do the
    // cameras see" registry draw rules read instead of `_rigs[0]`/`GetViewport().GetCamera3D()`.
    // ProjectilePool.Viewers takes this same instance; B11/B13 are its next consumers.
    private readonly ViewerSet _viewers = new();

    // Every AI aircraft spawned into this session, stepped by SessionSimulation after the
    // player rigs, freed with the world subtree.
    // Scratch for LockCandidateAircraft, reused so a target-key press allocates nothing.
    private readonly List<Node3D> _lockCandidates = new();
    // The whole-window boards photo mode has to get out of the way, and what they were showing
    // before it did. Registered at build; a session that builds no board registers none.
    private readonly List<Control> _boards = new();
    private readonly List<bool> _boardWasVisible = new();
    // scratch: the rigs' controllers plus AiPlanes, rebuilt on every AllAircraft() call
    private readonly List<FlightController> _aircraftScan = new();
    // scratch: the rigs' controllers alone, rebuilt on every HumanAircraft() call
    private readonly List<FlightController> _humanScan = new();
    // scratch: the aircraft plus the placed zeppelins, rebuilt on every GatedObjects() call
    private readonly List<Node3D> _gatedScan = new();
    // scratch: rig camera positions for the edge extender
    private readonly List<Vector3> _focusPoints = new();
    // Pickable subtrees that live beside the world content rather than under it, the anim lab's
    // parked --plane= prop. Shared by reference with _selection and _nodeLab, which walk it in
    // addition to the world root; populated during the world build after those tools are created.
    private readonly List<Node3D> _selectionExtraRoots = new();
    // The persistent rendering nodes, owned by the Launcher and kept across sessions; this node
    // only configures them. The mesh lab steers the sun and ambient, which is why both ride the
    // context rather than staying local to the Launcher's lighting setup.
    private readonly Camera3D _camera;
    private readonly DirectionalLight3D _sun;
    private readonly Godot.Environment? _env;
    // The static inspection view's orbit camera (LMB orbit, wheel zoom, AABB framing); the
    // Launcher creates it once and seeds --yaw=/--pitch= into its initial angles.
    private readonly OrbitCamera _orbit;
    // launched into the menu → the boards' Exit item returns there, not quit
    private readonly bool _menuDriven;
    // The presentation this session's boards take, resolved by the Launcher. The only board that
    // reads it is the pause screen, which the Original presentation composes from escape.zrd.
    private readonly UI.Menu.PresentationId _presentation;
    // The boards' Exit item, routed by the Launcher (the screen this flight was launched from, or
    // quit when nothing launched it).
    private readonly Action _exitSession;
    // The boards' Restart item on an Instant Action or campaign mission: the Launcher frees this
    // session and builds a fresh one. Nothing here can put a mission's opposition back on its own.
    private readonly Action _restartSession;
    // A campaign mission's end: the Launcher frees this session and reopens the launchscreen on
    // the named profile's debrief, carrying the result so a page can be opened on it. Null
    // outside a menu-driven process (a --campaign= run from the command line has no cabin to
    // return to and simply stays in the flown world).
    private readonly Action<string, CampaignMissionResult>? _campaignMissionEnded;
    // Where an ended Instant Action mission's final numbers go on a presentation with a wrap-up
    // page of its own, routed by the Launcher. Null leaves the ending to the in-flight board.
    private readonly Action<UI.Menu.IaWrapupSnapshot>? _instantActionWrapup;
    // The process's music channel, owned by the Launcher so one channel outlives every session.
    // Handed to CampaignDirector, which is what routes the mission's own music cues into it.
    private readonly MusicPlayer? _music;
    // Base (chapter-independent) paths, settled by the Launcher once per process and handed in via
    // the context; StartSession reads them each build and recomputes the chapter-dependent
    // gamez/texture/mission paths from _spec.Chapter.
    private readonly string _repoRoot;
    // Where extracted/ lives. Defaults to _repoRoot; overridden by --data-root= or CSVM_DATA_ROOT
    // so a git worktree can run the game, /extracted/, /CrimsonSkiesGame/ and /tools/ are
    // git-ignored, so a worktree checkout has none of them and cannot otherwise build or verify.
    private readonly string _dataRoot;
    private readonly string _planesGamezPath;
    private readonly string _zrdrPath;
    private readonly string _soundsPath;
    private readonly string _interpPath;
    private readonly string _messagesPath;
    // the extracted UI archive (paint patterns)
    private readonly string _rofPath;
    // Process-scoped, owned by the Launcher; the --damage-test/--effects-test/--weapon-test/
    // --destroy= probe wrappers below delegate to it (see src/Tooling/ProbeRunner.cs).
    private readonly Tooling.ProbeRunner _probeRunner;
    // Tap-vs-hold timing for the "." step key: a tap steps once (handled directly in
    // _UnhandledInput), and holding past the grace period steps every rendered frame, polled
    // here rather than through key-repeat events, since the grace period is measured on wall
    // time regardless of the sim being halted.
    private readonly HoldToRepeat _stepHold = new(initialDelay: 0.3f, repeatInterval: 0f);

    // Resolves each player's livery and spawn point against _spec;
    // see src/Session/Roster/LiveryResolver.cs and src/Session/Roster/SpawnPicker.cs.
    private LiveryResolver _liveryResolver = null!;
    private SpawnPicker _spawnPicker = null!;
    // --crash[=frame]: fires once, the frame the sim clock first reaches _spec.CrashFrame.
    private bool _crashFired;
    // --debug-scoreboard --vs: fires once, on the first sim step, see DriveParentSimulation.
    private bool _versusDebugKillFired;
    // --debug-pause[=frame]: fires once, the frame the sim clock first reaches it.
    private bool _debugPauseFired;
    // --debug-wash=N: how many of its two scripted washes have fired, see DriveParentSimulation.
    private int _debugWashesFired;
    private SpectatorCamera? _spectator;
    // The session's shared world selection (--freecam/--anim-lab): the clicked leaf plus its
    // cs_name ancestor ladder, which every inspect tool reads instead of picking for itself.
    private UI.Screens.SelectionService? _selection;
    // The node lab (N, --freecam/--anim-lab): tree panel, search, per-node actions and the
    // dependency readout for whatever the selection holds.
    private UI.Labs.NodeLab? _nodeLab;
    // The world damage lab (F19, --freecam/--anim-lab): HP slider + kill/reset on the selection's
    // destructible pool, the interactive twin of --damage-test.
    private UI.Labs.WorldDamageLab? _worldDamageLab;
    // The aircraft damage lab (F19, --viewer/--fly/--stunt): per-part HP sliders on the parked
    // plane's visuals, or on P1's real PlaneDamage in flight.
    private DamageLab? _damageLab;
    // The effect/crash stage factory, builds the world-effects runtime
    // (lazily, on demand for a plane-less session: --destroy, the damage lab's first kill) and
    // each player's crash runtime. Constructed once per session, same lifetime as _liveryResolver.
    private WorldEffectsFactory _worldEffectsFactory = null!;
    // Loads/applies the flown mission's weather and drives its per-frame rig state
    //, see src/Session/World/WeatherRig.cs's entry. Constructed once per
    // session (same lifetime as _worldEffectsFactory); null before the first weathered build and
    // nulled by ReturnToMenu so _Process's null guard covers the frame before the deferred free.
    private WeatherRig? _weatherRig;
    // A FOG_STATE raised during the world bootstrap, before _weatherRig exists; applied once the
    // rig has written its zone, which keeps the original's order (the zone first, the event over it).
    private Mech3.AnimRuntime.FogStateChange? _fogStateBeforeWeather;
    // The world state every puffer in this session reads but none of them owns, the wind and the
    // camera position (see Effects/WorldWind.cs). Constructed here rather than on
    // _weatherRig because the emitter factories need it at StartSession, long before the first
    // weathered build exists to write it; WeatherRig.Tick is what fills it in.
    private Effects.EffectAmbience _ambience = new();
    private LensFlareRig? _lensFlareRig;
    // The FBFX_COLOR_FROM_TO wash, one ramp per rendered view, painted into the pane(s) the burst
    // was near.
    private UI.Boards.ScreenFlash? _screenFlash;
    // The active smoke screens (D18): laid by the SMOKE_SCREEN fire path, walked over every rig
    // and AI plane each sim step, washing humans through _screenFlash and stunning AI pilots.
    private SmokeScreens? _smokeScreens;
    // The beeper tags (B8/B9): the projectile pool tags on a BEEPER hit and asks for a seeker's
    // target; the list itself counts down here, after every aircraft, in both step paths.
    private BeeperTags<FlightController>? _beeperTags;
    private Node3D? _plane;
    // The session's simulation clock (see GameClock). Also published as GameClock.Current for
    // authored animation and the few consumers that read session time; dropped by ReturnToMenu.
    private GameClock? _clock;
    // The sole owner of haltable session advancement. Both clock adapters request its Step.
    private SessionSimulation? _simulation;
    // Combat owners retained by this orchestrator and advanced through SessionSimulation.
    private ProjectilePool? _projectiles;
    private IncomingFire? _incomingFire;   // --incoming: the incoming-fire test rig
    // The flight roster builds the human field and introduces AI aircraft later. Every AI it
    // returns is stepped by SessionSimulation after the player rigs and freed with the world.
    private FlightRoster? _flightRoster;
    private AiSkills? _aiSkills; // ai_skill_parameters, loaded once on the first AI spawn
    // The roster's own AI stats reader, so a spawn can consult the def it is about to fly (pilot
    // skills, accent) before the roster builds it. Same cache: the read here is not a second parse.
    private Func<string, string?, PlaneStats>? _aiStatsFor;
    // The E16 voice dispatch (built with the rigs when the world has sounds; its mission clock
    // steps in SessionSimulation). Null in a soundless/world-less session, chatter simply off.
    private AiVoiceRuntime? _aiVoice;
    // The mission radio queue the campaign's objective callouts speak on. Built with the rigs when
    // the world has sounds, stepped beside the director, freed with the world subtree.
    private MissionRadio? _radio;
    // The egen enemy generators (--generators): loaded with the rigs, stepped in
    // SessionSimulation before the AI planes it spawns into AiPlanes, freed with the world subtree.
    private AiGeneratorRuntime? _generators;
    private ZeppelinRuntime? _zeppelins;
    // The mission's surface vehicles (a mode ship roster block, a boat generator's launch):
    // built with the roster on a chapter world, stepped after the generators that launch them.
    private SurfaceVehicleRuntime? _surfaceVehicles;
    // The active Instant Action mission's director: the mission runtime, the wave state and (as
    // the deepening proceeds) the sequencing (see InstantActionDirector). Built at the top of
    // StartSession, null outside a mission, which is what keeps every other session mode
    // (free flight, Dogfight) untouched by its existence.
    private InstantActionDirector? _iaDirector;
    // The active campaign mission's director: the objectives graph, its world seam and the mission
    // end/return flow (see CampaignDirector). Built at the top of StartSession alongside the
    // Instant Action one, null outside a --campaign= launch.
    private CampaignDirector? _campaign;
    // The name the HUD kill line prints when the player is the one shot down. It is the flying
    // campaign profile's, and outside a campaign the profile last used. That is where the
    // original's startup takes its PlayerName from (docs/org/vehicleDamage.md "The kill message").
    // ⚠ The second arm is refused under --det. A record of who played last is machine state, and a
    // pinned run reads none of it. No profile on disk leaves the line its unnamed fall-through.
    private string? _pilotName;
    // The cutscene host: built for any flown chapter session, since a story mission's intro
    // definition starts itself out of startanims and needs its CALLBACK codes hosted from the
    // bootstrap on. Null everywhere else, which leaves the world build's node census untouched.
    private CutsceneController? _cutscene;
    // The landings.zrd approach trigger, built and bound alongside the cutscene host it feeds.
    private LandingApproachRuntime? _landings;
    // The rope-ladder switch, bound with the landings trigger off the same pickup sensors.
    private LadderSwitchRuntime? _ladder;
    // The world AA emplacements: built with the rigs whenever a chapter world and the
    // shared pool exist, stepped by SessionSimulation after the zeppelins (slung mounts read the
    // moved pose). Shipped ACTIVATED honoured; --wake-turrets is the WAKEUP_TURRETS stand-in.
    private TurretEmplacementRuntime? _turretEmplacements;
    // The dogfight scorekeeping (--vs): built with the rigs, fed their Downed reports, its clock
    // advanced on the sim dt (never wall time). Null outside Versus, the Downed events then
    // simply have no subscriber. Freed with this node; flight holds no match state.
    private VersusMatch? _versus;
    private VersusSpawnRotation? _versusSpawns;
    // The stunt race (--stunt with several pilots), for the same reason: a rerun resets it rather
    // than each pilot's own run. Null outside a race.
    private StuntRace? _race;
    // One menu reader per player, built with that player's own pad binding, so a board menu can be
    // driven by its owner alone. Null before the rigs exist.
    private UI.Screens.MenuInput[]? _menuInputs;
    // Who is holding the sim clock and why, shared by every rig and by every board that halts.
    // Null before the rigs exist.
    private PauseState? _pauseState;
    // The Original presentation's pause sheet while it is the board in use. Its parchment carries
    // the objectives, so the corner readout is not built beside it.
    private UI.Menu.Original.OriginalPauseBoard? _originalPause;
    // The pause board in use, whichever presentation composed it, and the options leaf that stands
    // over it while PREFERENCES is open. The leaf is the Launcher's to build (only it holds the
    // decoded layout and the options writer); null leaves both boards without that door.
    private Control? _pauseBoard;
    private UI.Screens.PausePreferences? _pauseOptions;
    private Func<UI.Screens.PausePreferences?>? _pauseOptionsFactory;
    // Photo mode's three pieces, all null unless it is engaged: the hint/exit reader, the camera
    // holding the pane, and whose pane it is.
    private UI.Overlays.PhotoModeHud? _photoHud;
    private SpectatorCamera? _photoCamera;
    private FlightController? _photoPilot;
    // The trailer-target resolver every net follower this session builds shares. Built
    // with the rigs (it needs the player rig), so the F13 overlay, built earlier, reads it through
    // this field rather than holding a reference it could not have had yet.
    private NetTrailerTargets? _netTrailers;
    // rolling mirrored-tile window past the map edge
    private Mech3.MapEdgeExtender? _edgeExtender;
    // the splitscreen pane rig (null in single player)
    private UI.Boards.SplitScreen? _split;

    // Session lifecycle (the launchscreen's in-process world rebuild): everything a
    // session builds hangs under _worldRoot, so Esc-to-menu can free it and a new session node
    // build again. The camera, lights and global shader params live on the Launcher and persist.
    private Node3D? _worldRoot;
    // the session's LIGHT_STATE point lights (see WorldLights)
    private WorldLights? _worldLights;
    // The session-owned texture archive, kept open past the build scope so the data-driven crash can
    // bake its effect puffers lazily at crash time (the same reason --anim-lab keeps it open, but that
    // path hands it to the AnimLab node instead). Disposed by ReturnToMenu on teardown so a map reload
    // drops the previous archive instead of leaking it. Null in the anim-lab (lab-owned) case.
    private TextureArchive? _sessionTextures;
    // The world whose origin-parked entities are still being watched, and the poll accumulator.
    // See WorldBuilder.HideUnplacedEntities / RestorePlacedEntities.
    private WorldBuilder? _unplacedWatch;
    private double _unplacedRecheck;
    // This session's startup timing, the always-on [perf] startup line. One per StartSession,
    // published as StartupProfile.Current so the shared build code can record into it, and cleared
    // when the line is emitted.
    private StartupProfile? _startup;
    // DiagTraceRoster's own state: the objective-graph campaign diagnostic below.
    private int _diagTick;
    private AnimRuntime? _diagRuntime;
    private bool _diagRefsDone;

    /// <summary>Constructs the session node for one launch. <paramref name="spec"/> is what this
    /// session is built from (the command line verbatim, or the launchscreen's pick);
    /// <paramref name="ctx"/> carries the Launcher's settled paths, the persistent rendering
    /// nodes, the process-scoped services and the join flow's pad binding. The caller adds the
    /// node to the tree and then runs <see cref="StartSession"/>.</summary>
    public GameSession(SessionSpec spec, LauncherContext ctx)
    {
        // The session clock is advanced at the top of this node's _Process, and every sim consumer
        // reads it during the same frame, so this node has to tick first. Godot runs the lowest
        // priority first (the Launcher sits one notch behind at -999).
        ProcessPriority = -1000;
        Name = "GameSession";
        // ⚠ Resolved here, before any consumer reads Chapter/Mission: a --campaign= launch names a
        // story position, not a chapter, and every path below is derived from those two fields.
        _spec = CampaignDirector.ResolveSeatedPlane(
            ResolveCampaignZeppelins(CampaignDirector.ResolveSpec(spec, ctx.ZrdrPath), ctx.DataRoot));
        _repoRoot = ctx.RepoRoot;
        _dataRoot = ctx.DataRoot;
        _planesGamezPath = ctx.PlanesGamezPath;
        _zrdrPath = ctx.ZrdrPath;
        _soundsPath = ctx.SoundsPath;
        _interpPath = ctx.InterpPath;
        _messagesPath = ctx.MessagesPath;
        _rofPath = ctx.RofPath;
        _probeRunner = ctx.ProbeRunner;
        _captureDirector = ctx.CaptureDirector;
        _masterSeed = ctx.MasterSeed;
        _camera = ctx.Camera;
        _orbit = ctx.Orbit;
        _sun = ctx.Sun;
        _env = ctx.Env;
        _menuDriven = ctx.MenuDriven;
        _presentation = ctx.Presentation;
        _menuPads = ctx.MenuPads;
        _exitSession = ctx.ExitSession;
        _restartSession = ctx.RestartSession;
        _pauseOptionsFactory = ctx.PauseOptions;
        _campaignMissionEnded = ctx.CampaignMissionEnded;
        _instantActionWrapup = ctx.InstantActionWrapup;
        _music = ctx.Music;
    }

    /// <summary>Whether the build completed, the Launcher's Esc routing reads it (return to the
    /// launchscreen only once a world is actually up).</summary>
    public bool InSession { get; private set; }

    /// <summary>Has the session drawn the first frame the player is meant to see. That frame is
    /// an intro's camera posed onto the rigs, or the flown aeroplane on its spawn under its own
    /// HUD. It latches at the end of the frame that reaches it, and it is what the start cover
    /// holds for. No frame between the load screen and the mission then shows the world still
    /// assembling.</summary>
    internal bool FirstFrameReady { get; private set; }

    /// <summary>The session's per-player rigs, the Launcher's F11 placement print reads them.</summary>
    internal List<PlayerRig> Rigs => _rigs;

    /// <summary>The campaign mission's director, null outside a <c>--campaign=</c> launch. The
    /// session layer reads its <see cref="CampaignDirector.ReturnToCabin"/> to know the mission is
    /// over and the player belongs back in the cabin.</summary>
    internal CampaignDirector? Campaign => _campaign;

    /// <summary>The session's subject plane (null until the build lands one), the Launcher's
    /// capture tick reads it, because CaptureDirector only shoots once a plane exists.</summary>
    internal Node3D? Plane => _plane;

    private IReadOnlyList<FlightController> AiPlanes =>
        _flightRoster?.AiAircraft ?? Array.Empty<FlightController>();

    // ⚠ Do not spell "does this session build colliders" any other way; this is the one definition
    // the labs and the C overlay read, so they cannot disagree with what WorldSession built.
    private bool BuildsCollision => _spec.BuildsCollision;

    // Whether P / . may halt this session. Splitscreen flight says no: the freeze halts the shared
    // world, so it is not one player's to press.
    private bool HaltAllowed => !_spec.Fly || _rigs.Count == 1;

    /// <summary>Builds one flight/view session from the spec (mode, chapter, plane, spawn, …)
    /// into a fresh <see cref="_worldRoot"/> so Esc-to-menu can tear it all down and a new session
    /// node build again, the launchscreen's in-process world rebuild. The camera, lights and
    /// global shader params live on the Launcher and persist across sessions. Called by the
    /// Launcher once this node is in the tree. Returns true on success; false (leaving the partial
    /// _worldRoot for the caller to free) when the build threw.</summary>
    public bool StartSession()
    {
        // Published as the ambient Current so WorldSession, which the test harness also drives with
        // no session around it, can record its phases blind.
        _startup = new StartupProfile(_spec.ModeName, Time.GetTicksMsec())
        {
            Subject = _spec.EmptyStage ? "stage=empty"
                : _spec.NodeName != null ? $"chapter={_spec.Chapter} node={_spec.NodeName}"
                : _spec.WorldMode ? $"chapter={_spec.Chapter}"
                : $"plane={_spec.PlaneName}",
        };
        StartupProfile.Current = _startup;
        _worldRoot = new Node3D { Name = "Session" };
        AddChild(_worldRoot);
        // Built fresh per session: a launchscreen relaunch replaces _spec wholesale (FromMenu),
        // so these must not be cached across a rebuild.
        _liveryResolver = new LiveryResolver(_spec, _rofPath);
        _spawnPicker = new SpawnPicker(_spec);
        _ambience = new Effects.EffectAmbience();
        _worldEffectsFactory = new WorldEffectsFactory(_spec, _worldRoot,
            () => (_rigs.Count > 0 ? _rigs[0].Camera : _camera) is { } cam ? cam.GlobalPosition : Vector3.Zero,
            _ambience, PlayerPositionsSnapshot, AnyPilotFirstPerson);
        // Re-derive every subsystem RNG from the master before anything draws, so this session's
        // content is a function of its master alone rather than of how long the previous one ran.
        // The launcher decides that master: held for a pinned run, stepped per sortie otherwise.
        Rng.Reset(_masterSeed, _spec.SeedPinned);
        // One simulation clock per session. --det pins a fixed step, the animation lab is fixed-dt
        // by nature, everything else runs at the wall delta.
        _clock = new GameClock
        {
            Mode = _spec.Det || (_spec.AnimLab && _captureDirector.Pending) ? GameClock.RunMode.FixedStep
                : _spec.AnimLab ? GameClock.RunMode.FixedAccum
                : GameClock.RunMode.Realtime,
        };
        GameClock.Current = _clock;
        LoadProgress.Report(LoadStep.Scaffold);
        // Every world camera outside the cockpit interior draws at the one decoded base
        // (CameraController.ExternalFovDeg). The 50 is the static viewer's own framing, which
        // shows a model rather than the world and is not one of the original's views.
        _camera.Fov = _spec.Fly || _spec.Freecam || _spec.AnimLab
            ? CameraController.ExternalFovDeg : 50f;
        // One rig per rendered view, before anything camera-anchored is built (the skydome and
        // weather visuals below are per-rig). Single player reuses the main-viewport camera.
        BuildRigs(_spec.Fly ? _spec.Players : 1);
        // ⚠ Do not let a draw-rule consumer re-derive its camera set from _rigs; every one shares
        // this single registration so they cannot disagree about what the cameras see.
        _viewers.Bind(_rigs.Count > 0 ? _rigs.Select(r => r.Camera)
            : _camera != null ? new[] { _camera } : System.Array.Empty<Camera3D>());
        // The FBFX_COLOR_FROM_TO wash: one ramp per rendered view, built as soon as the rigs exist
        // so every runtime below takes the same sink. The viewer set goes with it because the
        // routing rule is per pane, and one rig list builds both index-aligned.
        _screenFlash = UI.Boards.ScreenFlash.Build(_rigs.Select(r => r.HudParent), _viewers);
        _worldRoot!.AddChild(_screenFlash);
        _worldEffectsFactory.ScreenFlash = _screenFlash.Play;
        // The smoke screens ride the same wash and read the roster through a closure, since the
        // rigs and the AI planes are both built later than this. player.json missing falls back
        // to the static image's tunables with a warning rather than aborting the launch.
        SmokeScreenTunables smokeTunables;
        try
        {
            smokeTunables = SmokeScreenTunables.Load(_zrdrPath);
        }
        catch (Exception e)
        {
            GD.PushWarning($"smoke screen: player.json unavailable ({e.Message}), flying on the image defaults");
            smokeTunables = SmokeScreenTunables.Image;
        }
        var screenFlashSink = _screenFlash;
        _smokeScreens = new SmokeScreens(smokeTunables, AllAircraft,
            (playerIndex, colour, weight, duration) => screenFlashSink.PlayBlend(playerIndex, colour, weight, duration));
        // A fresh list per build: a tag never outlives the session that painted it.
        _beeperTags = new BeeperTags<FlightController>();
        LoadProgress.Report(LoadStep.Rigs);

        // The chapter-dependent paths are recomputed here so a new launchscreen chapter selection
        // takes effect on rebuild, and ride BuildState so no phase method re-derives them.
        var state = new BuildState
        {
            DataRoot = _dataRoot,
            ZrdrPath = _zrdrPath,
            SoundsPath = _soundsPath,
            InterpPath = _interpPath,
            MessagesPath = _messagesPath,
            PlanesGamezPath = _planesGamezPath,
            Mute = _spec.Mute,
            DebugCollision = _spec.DebugCollision,
        };
        state.TexturesPath = _spec.Textures ?? SessionPaths.ChapterTextures(_dataRoot, _spec.Chapter);
        // --zep= grafts one chapter node onto the empty stage, so it needs that chapter's gamez
        // where the bare stage needs only planes.zbd. Chapter is already the flag's own by here.
        state.GamezPath = _spec.Gamez
            ?? (_spec.WorldMode || _spec.Zep != null
                ? SessionPaths.ChapterGamez(_dataRoot, _spec.Chapter)
                : _planesGamezPath);
        state.MissionZrdrPath = SessionPaths.MissionZrdr(_dataRoot, _spec.Chapter, _spec.Mission);
        LoadProgress.Report(LoadStep.Paths);

        // A fresh director per build, or null: construction (and its one-InstantActionRuntime ⚠)
        // lives on InstantActionDirector.TryCreate.
        _iaDirector = InstantActionDirector.TryCreate(_spec);
        // The campaign's sibling, on the same "a load failure flies without a mission" contract.
        _campaign = CampaignDirector.TryCreate(_spec, _zrdrPath, state.MissionZrdrPath);
        if (_campaign is { } campaign)
        {
            // The mission's own WAKEUP_SOUND_GROUP is what cues every campaign track, so the
            // channel is handed over rather than driven from here; the end event is this
            // session's cue to hand the player back to the cabin.
            campaign.Music = _music;
            campaign.MissionEnded += OnCampaignMissionEnded;
            // Losing the aircraft loses the mission; --no-crash-loss keeps the debugging
            // convenience of flying on past a crash.
            campaign.EndsOnPlayerDeath = !_spec.NoCrashLoss;
        }

        // Resolved once here rather than per death: a kill must not reach the disk.
        _pilotName = _campaign?.PilotName
            ?? (_spec.Det ? null : CampaignProfileStore.UserProfiles().LastPlayedPilotName);

        // The cutscene host, before the world build hands it to the animation runtime. Its world
        // hold stops the objectives update as well as the per-step world update (callback 20).
        _cutscene = _spec.Fly && _spec.WorldMode ? new CutsceneController() : null;
        if (_cutscene != null)
        {
            // The clock carries the authoritative hold into SessionSimulation; authored animation
            // reads the same fact but remains outside the held session step.
            _cutscene.WorldHeld = held =>
            {
                if (_clock != null)
                {
                    _clock.SimHeld = held;
                }

                _campaign?.HoldForCutscene(held);
                // The handoff (held -> false) is also when a --pos= this cutscene made
                // BuildFlightRigs withhold gets applied, so the intro's own progression tested the
                // player's pose against the authored spawn it expected all along.
                if (!held)
                {
                    ApplyDeferredSpawnOverride();
                }
            };
            // Read live, not captured: the pane rig is built later in this same build, and an
            // intro's first code lands in the animation bootstrap before it exists (BindRigs
            // re-raises this for that episode).
            _cutscene.FillsWindow = fills => _split?.Fill(fills);
            AddChild(_cutscene);
            // The mid-mission cutscene trigger, hosted by the same controller. ⚠ Story missions
            // only: C3/IA1 carries hooked_to_klondike with its approach armed, so an Instant
            // Action sortie would take a docking cutscene (WorldSession.Options.LandingTriggers).
            if (_campaign != null)
            {
                _landings = new LandingApproachRuntime();
                AddChild(_landings);
                _ladder = new LadderSwitchRuntime();
                AddChild(_ladder);
            }
        }

        LoadProgress.Report(LoadStep.Directors);

        Stopwatch sw;
        try
        {
            sw = Stopwatch.StartNew();
            LoadArchives(state);
            LoadProgress.Report(LoadStep.Archives);
            // The SOUND archive is scoped to this build everywhere but the lab, whose node owns its
            // disposal. ⚠ Do not scope the TEXTURE archive here: the world runtime bakes puffers
            // all session, so LoadArchives hands it to the session instead.
            using var soundsScope = _spec.AnimLab ? null : state.Sounds;
            if (!ResolveNodeSubtree(state))
                return false;
            if (_spec.EmptyStage && _spec.Zep != null)
            {
                // The graft goes through the chapter-world path with NodeSubtree set: one subtree,
                // no MissionSetup and no clutter, but a real AnimRuntime, so the damage, targeting
                // and sub-part feeds bind to the airship as they do to a mission's own.
                if (!BuildWorldStage(state))
                    return false;
                AttachEmptyStageGrid(state);
            }
            else if (_spec.EmptyStage)
            {
                BuildEmptyStage(state);
            }
            else if (_spec.WorldMode)
            {
                if (!BuildWorldStage(state))
                    return false;
            }
            else
            {
                BuildStaticStage(state);
            }
            if (!AttachPlaneAndLabs(state))
                return false;
            AssignCloudDeckIfBuilt(state);
            BuildFreecamSpectator(state);
            LoadProgress.Report(LoadStep.WorldStage);
            if (_spec.Fly)
            {
                BuildFlightRigs(state);
                ApplyDebugSpectate();
                // AFTER the rigs, for the same reason the spectate override is: a cutscene that
                // started during the world build has nothing to hide until they exist.
                _cutscene?.BindRigs(_rigs, () => AiPlanes);
                if (_cutscene != null)
                {
                    // The original parks the AI at the start of a mission of every type, before
                    // its start list runs. The park comes from the bootstrap definition. Missions
                    // only: free flight has no counterpart, so nothing outside a mission is held.
                    if (_iaDirector != null || _campaign != null)
                    {
                        _cutscene.ParkAtMissionStart();
                    }

                    _cutscene.SwapAirframe = SwapPlayerAirframe;
                    // The docking's own ending. Instant Action has no objectives graph to complete,
                    // and its rows never raise the code, so an unbound seam is the right answer
                    // there rather than a guarded one here.
                    _cutscene.MissionComplete = () => _campaign?.Graph?.NotifyDockingComplete();
                    // The result is banked the frame the ending lands, which is the frame the
                    // leaving hold and its fade start on, so this reads true for every ending that
                    // landed under a film whatever raised it.
                    _cutscene.EndingLanded = () => _campaign?.Result != null;
                    // Read through the field rather than captured: the pool is built by the world
                    // phase, which a lab or bench session skips entirely.
                    _cutscene.ClearOrdnance = () => _projectiles?.Clear();
                }
            }
            LoadProgress.Report(LoadStep.PlayerRigs);
            ApplyDestroyOverride(state);
            ApplyObjectiveOverride(state);
            LogBuildSummary(state, sw);
        }
        catch (Exception e)
        {
            Log.Error("core", $"failed to load session: {e}");
            // A failed build: nothing yet owns the archives kept open past the build scope (the
            // AnimLab node in the lab case, ReturnToMenu's teardown otherwise), so dispose them here.
            if (state.AnimLabNode == null)
            {
                state.LabTextures?.Dispose();
                state.LabSounds?.Dispose();
            }
            _sessionTextures?.Dispose();
            _sessionTextures = null;
            if (_captureDirector.Pending)
            {
                GetTree().Quit(1);
            }
            return false;
        }

        FinishFraming(state);
        _simulation = new SessionSimulation(new SessionSimulationRuntime(this));
        _startup?.EndBuild();
        InSession = true;
        LoadProgress.Report(LoadStep.Finished);
        return true;
    }

    public override void _Notification(int what)
    {
        // ⚠ Add only NON-CHILD teardown duties here. The whole session subtree hangs under
        // _worldRoot and frees atomically with this node, so a manual null-out is dead code.
        if (what == (int)NotificationExitTree)
        {
            _flightRoster?.ClearMembership();
            // A run that quits inside the session build (the headless probes) never renders a
            // frame, so this is the only place its startup breakdown can still be reported.
            // Idempotent: a session that did render has already emitted and this does nothing.
            _startup?.Emit();
            // A static pointer, not a child: null it so a node outliving this teardown falls back
            // to its raw frame delta. The menu relaunch is a frame later, so the two never race.
            GameClock.Current = null;
            // Beside the clock, and for the same reason: a static book of this world's nodes would
            // otherwise be the next session's starting membership.
            RenderPoses.Clear();
            // Clears csky_light_count so the next world does not inherit this one's lights;
            // idempotent, and null-guarded (a failed build never set it).
            _worldLights?.Dispose();
            _worldLights = null;
            // Not a node, so QueueFree cannot reach it. Null-guarded: a failed build already
            // disposed and nulled it, so there is no double free.
            _sessionTextures?.Dispose();
            _sessionTextures = null;
            // Restore the persistent (Launcher-owned) main camera: splitscreen stood it down while
            // the panes rendered, and the launchscreen and the next session expect it current.
            _camera.Current = true;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // A cutscene skips on any input, as the original's state core does. ⚠ Escape is exempt: it
        // is the way out of the session. ⚠ Pads count only where this session reads pads at all,
        // since a connected pad reports button 0 pressed as it arrives.
        if (_cutscene is { Playing: true }
            && (@event is InputEventKey { Pressed: true, Echo: false, Keycode: not Key.Escape }
                || (!_spec.PadsDisabled && @event is InputEventJoypadButton { Pressed: true })))
        {
            int skipper = SkipperIndex(@event);
            if (_cutscene.Skip(skipper))
            {
                // Named on screen, because with a field of humans the picture ending is one
                // player's decision the others did not make.
                _split?.NoteSkip(skipper);
                return;
            }

            // The skip was declined, so this scene is one the original plays out in full: the same
            // input held fast-forwards it instead (docs/formats/anim-definitions/cutscenes.md).
            _cutscene.NoteHeld(@event);
        }
        // P halts the sim and . steps it one frame, in freecam and the static viewer. ⚠ Do not
        // handle either for flight or the animation lab; both own their own transport.
        if (!_spec.AnimLab && @event is InputEventKey { Pressed: true, Echo: false } clockKey
            && _clock != null && HaltAllowed)
        {
            if (clockKey.Keycode == Key.P && !_spec.Fly)
            {
                _clock.Halted = !_clock.Halted;
                Log.Info("core", $"{(_clock.Halted ? "clock: halted (P resumes, . steps one frame, hold . to run)" : "clock: running")}");
                return;
            }
            if (clockKey.Keycode == Key.Period)
            {
                _clock.Halted = true;
                _clock.StepOnce();
                _stepHold.Press();
                return;
            }
        }
        if (!_spec.AnimLab && @event is InputEventKey { Pressed: false } clockKeyUp
            && clockKeyUp.Keycode == Key.Period)
        {
            _stepHold.Release();
        }
        // Esc (menu-or-quit routing) and the F11/F12 capture keys are the Launcher's: they are
        // meaningful at the launchscreen too, where no session node exists.
        if (_spec.Fly || _spec.Freecam || _spec.AnimLab)
            return; // the FlightController / SpectatorCamera owns the camera; no orbit controls
        _orbit.HandleInput(@event);
    }

    public override void _Process(double delta)
    {
        using var _ = ProcessSiteCost.Enter(ProcessSite.Session);
        // First thing in the frame (ProcessPriority): decide how much sim time this rendered frame
        // is worth, then, when the clock is not realtime, step the physics-driven consumers
        // ourselves, in the tree order Godot's physics tick would have used.
        if (_clock is { } clock)
        {
            if (_stepHold.Tick((float)delta))
            {
                clock.StepOnce();
            }
            clock.BeginFrame(delta);
            if (clock.ParentDriven)
            {
                DriveParentSimulation(clock);
            }
            // --debug-wash=N: two scripted blend washes to viewer N, red on the first sim frame
            // and white two seconds in, overlapping so the blend and not just the routing is on
            // screen. Sim time, every clock mode; a viewer no pane answers to paints nothing.
            if (_spec.DebugWash is int washViewer && _debugWashesFired < 2 && _screenFlash != null
                && clock.Time >= _debugWashesFired * 2.0)
            {
                _debugWashesFired++;
                var colour = _debugWashesFired == 1 ? new Color(1f, 0f, 0f) : new Color(1f, 1f, 1f);
                _screenFlash.PlayBlend(washViewer - 1, colour, _debugWashesFired == 1 ? 1f : 0.5f, 5f);
                Log.Info("flight", $"--debug-wash: wash {_debugWashesFired} addressed to viewer {washViewer} of {_screenFlash.PaneCount}");
            }
        }
        // The startup line goes out on the frame that proves the first one was drawn.
        _startup?.Frame();
        // One step of one deferred crash rig. A mid-flight AI introduction leaves its rig unbuilt
        // so the launch frame carries only what puts the aeroplane in the world; this is where the
        // rest of it lands, on the frames after.
        _flightRoster?.PumpDeferredCrashRigs();
        // Entities switched off as unplaced but since moved off the world origin are put back: a
        // motion starting is the proof a definition owns them. ⚠ Do not defer this once instead of
        // polling, and keep it on wall time: an OnCall definition can start its motion at any time.
        if (_unplacedWatch != null)
        {
            _unplacedRecheck += delta;
            if (_unplacedRecheck >= 1.0)
            {
                _unplacedRecheck = 0.0;
                if (_unplacedWatch.RestorePlacedEntities() is { Count: > 0 } restored)
                {
                    Log.Info("world", $"world: {restored.Count} entit(y/ies) moved off the origin after all, restored: {string.Join(", ", restored)}");
                }
            }
        }
        // Everything below is anchored to *a* camera, so it runs once per rig, one in single
        // player, one per pane in splitscreen (each on that player's own visual layer). See
        // src/Session/World/WeatherRig.cs's Tick.
        _weatherRig?.Tick(_rigs);
        // After the weather tick: that is where each rig's dome is re-centred on its camera, and
        // the flare reads the sun node inside it.
        _lensFlareRig?.Tick(delta);

        // One mirrored-tile window serves every pane (the union of the rings around each player),
        // so two players at opposite edges both get continued terrain. A no-op until one of them
        // crosses a cell boundary.
        if (_edgeExtender != null)
        {
            _focusPoints.Clear();
            foreach (var rig in _rigs)
                _focusPoints.Add(rig.Camera.Position);
            _edgeExtender.Update(_focusPoints);
        }

        // ⚠ Last in the frame, and the ONLY call anywhere: a suite that steps the simulation
        // itself must never reach this, so that it always reads the simulation pose
        // (docs/architecture.md, src/Utils/RenderPoses.cs).
        RenderPoses.Draw();
        // After the pose draw, because that is the state this frame renders. The cover the launcher
        // holds over the start comes off on what the player is meant to see, not on the build
        // finishing.
        FirstFrameReady = FirstFrameReady || ShowsFirstRealFrame();
    }

    /// <summary>The realtime clock adapter: request one complete session-simulation step from
    /// Godot's physics tick. Parent-driven modes request their substeps from <see cref="_Process"/>.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0.0 || _clock is not { ParentDriven: false })
            return;
        // Before the step, never after: everything below reads world poses, and a follower or a
        // held pose seeded from a drawn one would feed the interpolation back into the simulation.
        RenderPoses.Restore();
        _simulation?.Step((float)delta);
    }

    // BL-451: a --campaign= launch never carries --zeppelins/--generators (FromCampaign resolves
    // before the mission is known), so this peeks both loaders once ResolveSpec has settled
    // Chapter/Mission. Internal, not private, so the campaign-zeppelins suite exercises the exact
    // code the constructor runs rather than a copy of it. See WithCampaignZeppelins for the why.
    internal static SessionSpec ResolveCampaignZeppelins(SessionSpec spec, string dataRoot)
    {
        if (spec.CampaignProfile == null)
        {
            return spec;
        }
        var missionZrdr = SessionPaths.MissionZrdr(dataRoot, spec.Chapter, spec.Mission);
        return spec.WithCampaignZeppelins(
            HasMissionRecords(() => Zeppelins.Load(missionZrdr)),
            HasMissionRecords(() => EnemyGenerators.Load(missionZrdr)));
    }

    // Whether a session binds the mission's own targets.zrd, read with nothing editing it while the
    // session runs. The director owns that channel when one exists, so this asks for the director
    // and not for the launch. A --campaign= launch whose profile did not load flies without one and
    // still gets the table its mission ships. ⚠ Keep the stunt term. A stunt run binds the same
    // channel per pane. Internal so the mode-target-table suite pins the rule the build runs.
    internal static bool BindsMissionTargetTable(
        SessionSpec spec, bool hasDirector, bool stunting, bool hasWorld, int rigs) =>
        !hasDirector && spec.WorldMode && !spec.EmptyStage && !stunting && hasWorld && rigs > 0;

    // The splitscreen stunt race's shared results board, or none in Instant Action. ⚠ Never build
    // one there. It wakes on the last pilot's finish, which is also the mission's win. Its halt
    // stops the clock the director's hold counts down on, so the wrap-up never comes. Internal so
    // the instant-action-end suite builds the board the session builds.
    internal static StuntRaceBoard? RaceBoardFor(StuntRace race, bool instantAction, string context,
        bool exitsToMenu, PauseState pauseState, Func<int, MenuInput> inputFor) =>
        instantAction ? null : StuntRaceBoard.Build(race, context, exitsToMenu, pauseState, inputFor);

    /// <summary>Orders the aeroplanes this mission's generators will launch, off the roster blocks
    /// they launch from. The load screen builds them instead of the launch frame. Depth is the
    /// generator's own authored wave size: that is how many arrive before the cycle rests, and a
    /// quiet frame refills one. Internal so the wave-launch hitch suite orders exactly as a launch
    /// does rather than modelling it.</summary>
    internal static void OrderWaveAirframes(FlightRoster roster,
        IReadOnlyList<EnemyGeneratorDef> defs, IReadOnlyDictionary<string, RosterSpawnPlan> templates)
    {
        int ordered = 0;
        var pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward);
        foreach (var def in defs)
        {
            if (CampaignRosterPlan.ResolveGeneratorLaunch(templates, def.VehicleParams, out var plan)
                != GeneratorLaunch.Template || plan == null)
            {
                continue;
            }

            int depth = Math.Clamp(def.WaveSize, 1, WaveAirframeDepth);
            if (roster.OrderWaveAirframes(
                CampaignRosterPlan.SpawnFor(plan, Vector3.Zero, Vector3.Forward, pilot),
                depth, WaveAirframeCap))
            {
                ordered += depth;
            }
        }

        if (ordered > 0)
        {
            Log.Info("world", $"egen: {roster.OwedAirframes} wave aeroplane(s) ordered from {ordered} block slot(s); the load screen builds them so a launch binds one instead of building it");
        }
    }

    /// <summary>Carries out one step of the build the load screen still owes and answers
    /// whether more is left. The wave aeroplanes are that work. They belong to the load, where
    /// there is no frame budget, rather than to the launch frame that needs one.</summary>
    internal bool StepOwedLoad() => _flightRoster?.BuildOrderedAirframe() ?? false;

    private static void CopyInstanceShaderParams(Node source, Node copy)
    {
        if (source is GeometryInstance3D from && copy is GeometryInstance3D to)
            foreach (var name in InstanceShaderParams)
            {
                var value = from.GetInstanceShaderParameter(name);
                if (value.VariantType != Variant.Type.Nil)
                    to.SetInstanceShaderParameter(name, value);
            }
        int n = Math.Min(source.GetChildCount(), copy.GetChildCount());
        for (int i = 0; i < n; i++)
            CopyInstanceShaderParams(source.GetChild(i), copy.GetChild(i));
    }

    // The --campaign= zeppelin/generator peek's plumbing: true when the loader's list is
    // non-empty, false on an empty (authored [null]) or altogether missing mission file. Both
    // loaders already tolerate [null]; only the "no file at all" case needs the catch.
    private static bool HasMissionRecords<T>(Func<List<T>> load)
    {
        try
        {
            return load().Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // One placement slot per side on a ring about the grid origin, each squadron facing the centre.
    // Two sides therefore start 2 * SquadronRingRadiusM apart, which is AiModeMachine's decoded
    // engagement gate, so they are in contact from the first frames. A teamless entry is its own
    // side: a spawn naming no team= takes its own banded id (AimAssist.TeamOfPilot) regardless.
    // ⚠ Slot order is first appearance on the command line, not team id, so adding an entry does
    // not renumber the sides already there.
    private static List<(Vector3 Anchor, Vector3 Facing)> SquadronAnchors(IReadOnlyList<AiPlaneEntry> entries)
    {
        var slotOf = new Dictionary<int, int>();
        var keys = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            keys[i] = entries[i].Team ?? int.MinValue + i;
            if (!slotOf.ContainsKey(keys[i]))
                slotOf[keys[i]] = slotOf.Count;
        }
        var anchors = new List<(Vector3, Vector3)>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            float angle = Mathf.Tau * slotOf[keys[i]] / slotOf.Count;
            var anchor = new Vector3(Mathf.Sin(angle) * SquadronRingRadiusM,
                EmptyStage.SpawnAltitude, -Mathf.Cos(angle) * SquadronRingRadiusM);
            if (entries[i].Pos is { } over)
                anchor = over;
            var toCentre = new Vector3(-anchor.X, 0f, -anchor.Z);
            anchors.Add((anchor, toCentre.LengthSquared() > 0.001f
                ? toCentre.Normalized() : Vector3.Forward));
        }
        return anchors;
    }

    // --zep=: the named record alone, on a one-node net at the stage seat unless net= names the
    // stage's built-in ring (EmptyStage.PatrolNet). The net is synthetic rather than the chapter's
    // because a chapter net would fly the hull off to its own authored coordinates, thousands of
    // metres from the squadron ring. ⚠ It must have a net at all: a def whose net does not resolve
    // is placed but held OUT of the live list, so its zones never wire and nothing on it can be
    // shot. The seat defaults to the grid origin at the record's own altitude, inside that ring.
    private static (List<ZeppelinDef> Defs, IReadOnlyList<AiNet> Nets, Vector3? Seat) GraftedZeppelin(
        IReadOnlyList<ZeppelinDef> all, ZepStageSpec graft)
    {
        var only = new List<ZeppelinDef>();
        foreach (var def in all)
        {
            if (def.Node.Equals(graft.Record, StringComparison.OrdinalIgnoreCase))
                only.Add(def);
        }
        if (only.Count == 0)
        {
            Log.Info("world", $"zep: '{graft.Record}' has no record in {graft.Chapter}/{graft.Mission}'s zeppelins.zrd.json, the hull is built but nothing is wired to it");
            return (only, System.Array.Empty<AiNet>(), null);
        }
        var seat = graft.Pos ?? new Vector3(0f, only[0].Position.Y, 0f);
        // net= puts the hull on the stage's built-in ring rather than the one-node net above, so a
        // grafted airship patrols the same graph an --ai= plane can be named onto. It is rebadged
        // with the record's own net name, since the runtime resolves a def's route BY that name.
        var ring = graft.Net != null ? EmptyStage.ResolveNet(graft.Net) : null;
        if (graft.Net != null && ring == null)
        {
            Log.Info("world", $"zep: net '{graft.Net}' is not the built-in '{EmptyStage.PatrolNetName}' ring; '{graft.Record}' station-keeps at its seat instead");
        }
        var nets = new List<AiNet>
        {
            new AiNet
            {
                Id = ring?.Id ?? 0,
                Name = only[0].Net,
                Nodes = ring is { } onRing
                    ? onRing.Nodes
                    : new[] { new AiNetNode(seat, System.Array.Empty<float>()) },
                Edges = ring?.Edges ?? System.Array.Empty<(int A, int B)>(),
                Volumes = ring?.Volumes ?? AiVolumeSet.None,
            },
        };
        Log.Info("world", $"zep: graft '{graft.Record}' from {graft.Chapter}/{graft.Mission} seated at ({seat.X:0},{seat.Y:0},{seat.Z:0}), {(ring != null ? $"flying the '{EmptyStage.PatrolNetName}' ring, " : "")}{(graft.Team is { } t ? $"team {t}" : "team as the record authors it")}");
        return (only, nets, seat);
    }

    // A campaign mission has ended and its result is banked (the director records the attempt and
    // saves the profile before raising this). The world stays up for the rest of the frame; the
    // Launcher frees this session and shows the menu at the debrief, which re-reads the profile
    // this director just wrote.
    private void OnCampaignMissionEnded(CampaignMissionResult result)
    {
        if (_spec.CampaignProfile is not { } profile || _campaignMissionEnded == null)
        {
            return;
        }

        Log.Info("core", $"campaign: {result.Outcome}, handing '{profile}' back to the menu's debrief");
        _campaignMissionEnded(profile, result);
    }

    // What the start cover waits for. An intro owns the eye, so its camera having posed the rigs is
    // the frame. Otherwise it is the flown aeroplane placed under a live rig. A mode with no rig at
    // all (viewer, freecam) has nothing to wait for beyond the built world's first processed frame.
    private bool ShowsFirstRealFrame()
    {
        if (!InSession)
        {
            return false;
        }

        if (_cutscene is { Playing: true })
        {
            return _cutscene.CameraPosed;
        }

        return _rigs.Count == 0 || _rigs[0].Controller != null;
    }

    // Places every rig at the --pos= placement BuildFlightRigs withheld while the intro owned
    // the session, the moment the intro's own handoff (WorldHeld -> false) says it is done
    // testing the player against the authored spawn. A no-op when nothing was withheld: most
    // WorldHeld(false) calls are an ordinary mid-mission cutscene ending, not this one.
    private void ApplyDeferredSpawnOverride()
    {
        bool applied = false;
        for (int i = 0; i < _rigs.Count; i++)
        {
            string tag = _rigs.Count > 1 ? $"P{i + 1} " : "";
            if (_spawnPicker.TakeDeferredOverride(i, tag) is not { } placement
                || _rigs[i].Controller is not { } pilot)
            {
                continue;
            }

            pilot.Activate(placement.pos, placement.lookAt);
            applied = true;
        }

        if (applied)
        {
            _spawnPicker.ClearDeferredOverride();
            Log.Info("core", $"campaign: --pos= applied at the intro's handoff");
        }
    }

    /// <summary>Every aircraft in the session, the rigs' controllers first and then the AI
    /// planes, in one reused list read fresh on each call: waves activate and generators spawn
    /// long after the consumers holding this delegate are built. Not to be held across a step.</summary>
    private IReadOnlyList<FlightController> AllAircraft()
    {
        _aircraftScan.Clear();
        foreach (var rig in _rigs)
        {
            if (rig.Controller is { } c)
            {
                _aircraftScan.Add(c);
            }
        }
        _aircraftScan.AddRange(AiPlanes);
        return _aircraftScan;
    }

    // Every joined player's aircraft and no AI, in the same reused-list shape as AllAircraft.
    // Read fresh because an airframe swap rebuilds a rig's controller; do not hold across a step.
    // Outside splitscreen the sole entry is the scripted player.
    private IReadOnlyList<FlightController> HumanAircraft()
    {
        _humanScan.Clear();
        foreach (var rig in _rigs)
        {
            if (rig.Controller is { } c)
            {
                _humanScan.Add(c);
            }
        }

        return _humanScan;
    }

    // Every drawn thing the cloud band's per-object zone gate judges by its own altitude, meaning
    // the session's aircraft and its placed zeppelins. Same reused-list shape as AllAircraft, read
    // fresh each frame because both rosters change all flight long.
    private IReadOnlyList<Node3D> GatedObjects()
    {
        _gatedScan.Clear();
        _gatedScan.AddRange(AllAircraft());
        _zeppelins?.CollectHosts(_gatedScan);
        return _gatedScan;
    }

    // Loads the session's core archives (gamez, textures, sounds, sound defs/groups) and routes the
    // texture/sound archives to whichever owner outlives this build scope.
    private void LoadArchives(BuildState state)
    {
        // ⚠ Do not defer this to the flight audio below: the animation bootstrap builds the world's
        // ambient SOUND_NODE emitters and needs the archive while it runs.
        var archives = SessionArchives.OpenFor(
            _spec.AnimLab ? ArchiveIntent.Lab : ArchiveIntent.Session,
            state.GamezPath, state.TexturesPath, state.SoundsPath, state.ZrdrPath, state.Mute);
        state.Gamez = archives.Gamez;
        state.Textures = archives.Textures;
        state.Sounds = archives.Sounds;
        state.SoundDefs = archives.SoundDefs;
        state.SoundGroups = archives.SoundGroups;
        state.TexturesOutliveBuild = archives.TexturesOutliveBuild;
        state.SoundsOutliveBuild = archives.SoundsOutliveBuild;
        // ⚠ Keep the texture archive open past this build scope; the data-driven crash bakes its
        // effect puffers lazily. The lab owns its copy, every other mode hands it to the session.
        if (_spec.AnimLab)
        {
            state.LabTextures = archives.Textures;
            state.LabSounds = archives.Sounds;
        }
        else
        {
            _sessionTextures = archives.Textures;
        }
    }

    // --node=<cs_name>: resolve the request against the chapter gamez BEFORE anything is built, so
    // a miss reports its candidates and quits instead of half-building a world. Returns false if
    // the session must abort. ⚠ Match on the source name, never the Godot node name, which is
    // sanitized and auto-renamed; duplicates are normal, so the first match builds.
    private bool ResolveNodeSubtree(BuildState state)
    {
        // --zep= is the same subtree build under another name: the record's hull instead of a node
        // the user named, so the graft borrows this resolution rather than repeating it.
        if (_spec.Zep is { } zep)
        {
            var zepMatches = WorldBuilder.MatchNodes(state.Gamez, zep.Record);
            if (zepMatches.Count == 0)
            {
                var zepNear = WorldBuilder.SuggestNodes(state.Gamez, zep.Record, NodeSuggestCap);
                Log.Warn("world", $"--zep: '{zep.Record}' matches no node in {zep.Chapter}'s gamez ({state.Gamez.Nodes.Count} nodes)");
                if (zepNear.Count > 0)
                {
                    Log.Warn("world", $"--zep= candidates containing '{zep.Record}': {string.Join(", ", zepNear)}");
                }
                _sessionTextures?.Dispose();
                _sessionTextures = null;
                GetTree().Quit();
                return false;
            }
            state.NodeSubtree = zepMatches[0];
            Log.Info("world", $"--zep='{zep.Chapter}/{zep.Mission}:{zep.Record}' grafting '{zepMatches[0].Name}'#{zepMatches[0].Index} onto the empty stage");
            return true;
        }
        if (_spec.NodeName == null || !_spec.WorldMode)
            return true;
        var matches = WorldBuilder.MatchNodes(state.Gamez, _spec.NodeName);
        if (matches.Count == 0)
        {
            var near = WorldBuilder.SuggestNodes(state.Gamez, _spec.NodeName, NodeSuggestCap);
            Log.Warn("world", $"--node='{_spec.NodeName}' matches no node in {_spec.Chapter}'s gamez ({state.Gamez.Nodes.Count} nodes)");
            if (near.Count > 0)
            {
                Log.Warn("world", $"--node= candidates containing '{_spec.NodeName}': {string.Join(", ", near)}");
            }
            else
            {
                Log.Warn("world", $"--node= no name in {_spec.Chapter} contains '{_spec.NodeName}' either, run the full world with --debug-names to read names off the objects");
            }
            _sessionTextures?.Dispose();
            _sessionTextures = null;
            GetTree().Quit();
            return false;
        }
        state.NodeSubtree = matches[0];
        var labels = new List<string>();
        foreach (var m in matches)
        {
            labels.Add($"{m.Name}#{m.Index}");
        }
        Log.Info("world", $"--node='{_spec.NodeName}' matched {matches.Count} node(s): {string.Join(", ", labels)}");
        if (matches.Count > 1)
        {
            Log.Warn("world", $"--node='{_spec.NodeName}' is ambiguous, building the first ({state.NodeSubtree.Name}#{state.NodeSubtree.Index}); name a unique node or pick by eye from the list above");
        }
        return true;
    }

    // --stage=empty: no gamez, no mission, no animation program, just a flat collidable ground
    // plane under a grid drawn in code. Flight, weapons and colliders work; nothing else is built.
    private void BuildEmptyStage(BuildState state)
    {
        long mark = StartupProfile.Mark();
        var stage = EmptyStage.Build(collision: _spec.Fly || _spec.ForceCollision);
        StartupProfile.Record("world", mark);
        _plane = stage.Root;
        state.MeshInstances = stage.MeshInstanceCount;
        state.Colliders = stage.ColliderCount;
        state.What = "empty stage";
    }

    // --zep=: the graft's ground. The same grid and collidable plane the bare stage builds, added
    // beside the subtree rather than as the subject, since on this path _plane is the airship and
    // AttachPlaneAndLabs joins that one.
    private void AttachEmptyStageGrid(BuildState state)
    {
        long mark = StartupProfile.Mark();
        var stage = EmptyStage.Build(collision: _spec.Fly || _spec.ForceCollision);
        StartupProfile.Record("world", mark);
        _worldRoot!.AddChild(stage.Root);
        state.MeshInstances += stage.MeshInstanceCount;
        state.Colliders += stage.ColliderCount;
        state.What += " on the empty stage";
    }

    // Builds the chapter world through WorldSession and binds its animation program, then the
    // per-view steps that read it back: node-name framing, the damage/effects-test probes, the
    // shared selection + node/damage labs, unplaced-entity hiding, the map-edge extender, per-rig
    // weather and the --anim-lab stage. Returns false if the session must abort.
    // ⚠ WorldSession does not add its Root to the tree; the AddChild(_plane) below owns that.
    private bool BuildWorldStage(BuildState state)
    {
        // AI voice lines are first reached at runtime, after the sound archive closes, so their
        // clips must join the prewarm set now. ⚠ Prewarm the mission roster's own accents, not the
        // whole voice bank; a mission without a roster prewarms none.
        IReadOnlyCollection<string>? voiceClips = null;
        if (_spec.Fly && state.NodeSubtree == null
            && state.SoundDefs is { } voiceDefs && state.SoundGroups is { } voiceGroups)
        {
            // Accents assigned outside the roster (--ai=…:accent=N, the Instant Action actors) join
            // the roster set. Their clips are first reached at runtime too, so an unprewarmed
            // accent would be a silent pilot.
            var extraAccents = new List<int>();
            if (_spec.AiPlanes is { } aiEntries)
            {
                foreach (var entry in aiEntries)
                {
                    if (entry.Accent is { } accent)
                    {
                        extraAccents.Add(accent);
                    }
                }
            }
            if (_iaDirector is { } iaVoice)
            {
                extraAccents.AddRange(InstantActionRuntime.VoiceAccentIds(iaVoice.Runtime.Def));
            }
            voiceClips = CombatVoice.SessionPrewarmNames(
                state.ZrdrPath, state.MissionZrdrPath, voiceDefs, voiceGroups, extraAccents);
        }
        var session = WorldSession.Build(
            new WorldSession.Options
            {
                DataRoot = state.DataRoot,
                Chapter = _spec.Chapter,
                Mission = _spec.Mission,
                ZrdrPath = state.ZrdrPath,
                InterpPath = state.InterpPath,
                MissionZrdrPath = state.MissionZrdrPath,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(state.DataRoot, _spec.Chapter),
                EffectsParent = _worldRoot!,
                // The one instance the weather rig publishes to and the player's own effects read.
                // Without it the world's emitters hold the camera-less still-air null object, which
                // runs neither the authored distance fade nor either of its culls.
                ArchiveEmitterFactory = (textures, parent) =>
                    new Effects.PufferEmitterFactory(textures, parent, _ambience),
                // The PLAYER_RANGE fallback for a runtime with no PlayerPositions wired: player 1's
                // camera, resolved per call because none of those cameras exist yet here.
                PlayerPosition = () => (_rigs.Count > 0 ? _rigs[0].Camera : _camera) is { } cam
                    ? cam.GlobalPosition
                    : Vector3.Zero,
                // Every pane camera is a 3D audio listener, so the world is heard from the nearest
                // of them (UI.Boards.SplitScreen). Same set as the rigs, for the debug log's column only.
                ListenerPositions = () =>
                {
                    if (_rigs.Count == 0)
                        return _camera is { } cam ? new[] { cam.GlobalPosition } : System.Array.Empty<Vector3>();
                    var positions = new Vector3[_rigs.Count];
                    for (int i = 0; i < _rigs.Count; i++)
                        positions[i] = _rigs[i].Camera.GlobalPosition;
                    return positions;
                },
                // ⚠ Measure EXECUTION_BY_RANGE and PLAYER_RANGE from the aircraft, not the camera:
                // the chase camera trails far enough behind to eat most of a 50 m radius. The same
                // closure goes to the world-effects runtime, so the two agree on who is nearest.
                PlayerPositions = PlayerPositionsSnapshot,
                // PLAYER_1ST_PERSON: any pilot in Cockpit or Nose. There is one world runtime for
                // every pane, so in splitscreen the condition is "someone is in a cockpit", the
                // per-pane reading needs per-pane runtimes and is E41's filed splitscreen item.
                FirstPersonView = AnyPilotFirstPerson,
                // The world lights' own nearest-viewer budget, the draw-rule seam A3
                // promoted, so a light beside player 4's pane stays lit even while player 1 is
                // far from it. Single player: one entry, same as every other _viewers consumer.
                LightViewerPositions = () => _viewers.Positions(),
                // Resolved on the spec: the damage-test census, --collision's overlay and
                // --debug-damage's collider count all need real bodies in a non-fly harness.
                Collision = BuildsCollision,
                DebugAnim = _spec.DebugAnim,
                AnimLod = _spec.AnimLod,
                DebugDzPaths = _spec.DebugDzPaths,
                NoClutter = _spec.NoClutter,
                DebugClutterFlag = _spec.DebugClutterFlag,
                HiddenAlpha = _spec.HiddenAlpha,
                ClutterTemplates = _spec.ClutterTemplates,
                // ⚠ Do not set these by hand; they come from LoadArchives's ArchiveIntent. The
                // textures belong to the session so the runtime keeps a live PufferFactory; the
                // sounds do not, being a `using` of this build outside the lab.
                TexturesOutliveBuild = state.TexturesOutliveBuild,
                SoundsOutliveBuild = state.SoundsOutliveBuild,
                VoiceClipNames = voiceClips,
                // The mission's own WAKEUP_SOUND_GROUP/COMPLETED_SOUND_GROUP names, never
                // referenced by the anim program, so nothing else would prewarm them (D33).
                ExtraPrewarmNames = _campaign?.Script.SoundGroupNames(),
                // The lab's quiet stage: ambient playback deferred to its A toggle, staged templates
                // relocated onto the call site. Safe this early, since a quiet-stage bootstrap
                // dispatches only RESET_STATEs and never consults it.
                AutoStart = !_spec.AnimLab,
                PlacesCalledTemplates = _spec.AnimLab,
                // The world's dice, RANDOM_WEIGHT verdicts, SOUND_GROUPS picks, crash-debris
                // scatter, in every mode, not just the lab.
                RuntimeSeed = Rng.IntSeedFor(Rng.Anim),
                // --node=: one subtree instead of the whole world (null = the full build).
                NodeSubtree = state.NodeSubtree,
                // The cutscene seam, wired before the bootstrap because an intro definition raises
                // its codes the instant startanims starts it, long before a rig exists.
                Cutscenes = _cutscene != null ? CutsceneWorldNames : null,
                LandingTriggers = _landings != null,
                PlanesGamezPath = state.PlanesGamezPath,
                CallbackHost = _cutscene != null ? _cutscene.Host : null,
                // No owner named: the opening cutscene has no triggering human, so the episode is
                // the scripted player's, which is what the intro has always meant.
                TriggerOwner = _cutscene is { } host ? anim => host.Own(anim) : null,
                // The weather rig is built after the world, and the intro's fog fires inside the
                // bootstrap, so the event is held until the rig has applied its zone.
                FogStateSink = fog =>
                {
                    if (_weatherRig != null)
                        _weatherRig.ApplyFogState(fog);
                    else
                        _fogStateBeforeWeather = fog;
                },
            },
            state.Gamez, state.Textures, state.Sounds, state.SoundDefs, state.SoundGroups);
        _plane = session.Root;
        var builder = session.Builder;
        state.CloudDeck = session.CloudDeck;
        // The deck's other lit variant, built beside it, the weather rig swaps it in above the
        // cloud band (WorldBuilder.CloudDeckUndimmedMeshes).
        state.DeckUndimmedMeshes = builder.CloudDeckUndimmedMeshes;
        // Owned by the session so a teardown drops the previous world's lights.
        _worldLights = session.Lights;
        state.CrashProgram = session.Program;
        state.WorldScene = session.Builder.Scene;
        state.WorldRuntime = session.Runtime;
        // The mission's craters, which need world colliders both to find the terrain a round struck
        // and to cut that terrain's own trimesh. Owned by the session, so they last exactly as long
        // as the mission does and a new launch starts on uncratered ground.
        state.Craters = BuildsCollision ? new CraterField(session.Root) : null;
        // After the bootstrap: an intro definition has already raised its codes, and this is where
        // the host picks up the two nodes it drives.
        _cutscene?.BindWorld(session.Runtime, session.Aircraft);
        state.Aircraft = session.Aircraft;
        state.StagedAircraftMeshes = session.Aircraft?.MeshInstances ?? 0;
        state.Landings = session.Landings;
        state.Pickups = session.Pickups;
        if (_cutscene != null && _landings != null)
        {
            _cutscene.HostDefinitions(session.LandingCutsceneAnims);
            _landings.Bind(session.Runtime, session.Landings, _cutscene, () => _rigs);
            _ladder?.Bind(session.Runtime, _cutscene, () => _rigs, session.Pickups);
        }
        // The screen wash. Set here rather than inside WorldSession for the same reason the
        // contact mask below is: the overlay is a session-owned surface and WorldSession builds
        // runtimes for the test harness too, where there is no session to own one.
        session.Runtime.ScreenFlash = _screenFlash != null ? _screenFlash.Play : null;
        // Ground contact for the world's own do_intersections bodies. Set here, not inside
        // WorldSession, because the mask is a flight-layer constant and the runtime is the
        // animation layer. Left at 0 in a collider-less build, which is the whole fallback.
        if (BuildsCollision)
        {
            session.Runtime.ContactMask = CollisionLayers.World;
            // Bound to the ONE surface-id read, so a landing piece, a round's impact and a
            // wingtip graze cannot disagree about what they hit.
            session.Runtime.SurfaceIsWater = body => ProjectilePool.SurfaceIsWater(body as Node);
        }

        // F13 / --debug-ainets: the chapter's AI patrol nets (ne0NNNNN + neindex, AI route
        // data the original never renders; docs/formats/ai-nets.md). Chapter-scoped data, so
        // the --node= partial stage skips it with the rest of the mission dressing.
        if (state.NodeSubtree == null)
        {
            _plane.AddChild(new UI.Overlays.AiNetsOverlay(
                SessionPaths.ChapterZrdr(state.DataRoot, _spec.Chapter), _spec.Chapter)
            {
                DebugShow = _spec.DebugAiNets != null,
                Filter = _spec.DebugAiNets ?? "",
                // The live leashes: each patrolling AI's own follower state, read per frame off
                // the list this session keeps (the overlay is built before any AI exists, so it
                // takes a supplier rather than a snapshot).
                CollectLeashes = into =>
                {
                    foreach (var ai in AiPlanes)
                    {
                        if (ai is { InPlay: true } && ai.Pilot is { Patrol: { CurrentIndex: >= 0 } patrol } pilot)
                        {
                            into.Add(new UI.Overlays.AiNetLeash(ai.WorldPosition, patrol.CurrentTarget,
                                patrol.Net.Id, pilot.SteeringPatrol));
                        }
                    }
                },
                // An anchored net is drawn where it actually is, not where the file says.
                // Zero until the rigs are built, which is before anything flies it.
                TrailerOffsetOf = net => _netTrailers?.OffsetOf(net) ?? Vector3.Zero,
            });
        }

        // --node=: the built subtree's WORLD-frame box. ⚠ Do not read it from GlobalTransform (the
        // subtree is not in the scene tree yet) or from the gamez child_bbox, which is stored in
        // the node's own frame.
        if (state.NodeSubtree != null && WorldBuilder.DetachedWorldAabb(session.Root) is { } box)
        {
            state.NodeAabb = box;
            Log.Info("world", $"node stage: '{state.NodeSubtree.Name}'#{state.NodeSubtree.Index} built, {builder.MeshInstanceCount} mesh instance(s), centre=({box.GetCenter().X:0},{box.GetCenter().Y:0},{box.GetCenter().Z:0}) size=({box.Size.X:0.#},{box.Size.Y:0.#},{box.Size.Z:0.#})");
        }
        else if (state.NodeSubtree != null)
        {
            Log.Warn("world", $"node stage: '{state.NodeSubtree.Name}'#{state.NodeSubtree.Index} built no geometry at all, it is a group node; the camera framing has nothing to aim at");
        }

        // --damage-test: drive one destructible's HP through its DAMAGE_SEQUENCE stages and quit.
        // ⚠ The world subtree must be in the tree first (with ManualAdvance, so _Process does not
        // double-drive): ticking a death sequence reads global transforms.
        if (_spec.DamageTest)
        {
            _worldRoot!.AddChild(_plane);
            session.Runtime.ManualAdvance = true;
            _probeRunner.RunDamageTest(_spec, session.Runtime);
            GetTree().Quit();
            return false;
        }

        // --effects-test: play every impact/destruction effect and report which resolve and which
        // actually build a puffer, then quit. ⚠ Both subtrees must be in the tree first, or the
        // census counts nothing and the templates' global transforms are identity.
        if (_spec.EffectsTest && state.WorldScene != null)
        {
            _worldRoot!.AddChild(_plane);
            if (_worldEffectsFactory.EnsureWorldEffects(state.Gamez, state.WorldScene, state.Textures,
                    session.Program, session.Runtime) is { } effects)
            {
                _probeRunner.RunEffectsTest(_spec, _camera, effects,
                    EffectCatalogue.WorldEffectAnimNames(session.Program),
                    _worldEffectsFactory.EffectStage);
            }
            GetTree().Quit();
            return false;
        }

        // The shared world selection: click-pick plus the cs_name ancestor ladder, in the two modes
        // that observe a live world with a cursor. Created here so the anim lab below can bind its
        // camera-follow to it; it builds no HUD until something is picked.
        if (_spec.Freecam || _spec.AnimLab)
        {
            _selection = new UI.Screens.SelectionService(_plane, _camera)
            {
                DebugPick = _spec.DebugSelect != null ? UI.Screens.SelectionService.ParseDebugPick(_spec.DebugSelect) : null,
                ExtraRoots = _selectionExtraRoots,
            };
            // The node lab reads that selection. Its camera is resolved through a
            // delegate: the freecam is created further down, after this point.
            _nodeLab = new UI.Labs.NodeLab(_plane, _selection, session.Runtime, session.Program,
                session.Builder.Scene, BuildsCollision)
            {
                ExtraRoots = _selectionExtraRoots,
                CameraSource = () => _spectator,
                DebugSpec = _spec.DebugNodeLab,
                // The anim lab's timeline strip and transport panel own the bottom of the
                // window; plain freecam has nothing there.
                BottomMargin = _spec.AnimLab ? 252 : 16,
            };
            // The world damage lab reads the same selection. Its effects runtime is built
            // on the first damage action, not now, an untouched session pays nothing.
            var damageScene = session.Builder.Scene;
            var damageProgram = session.Program;
            var damageRuntime = session.Runtime;
            _worldDamageLab = new UI.Labs.WorldDamageLab(_selection, damageRuntime, BuildsCollision)
            {
                SelectByName = name => _nodeLab?.SelectByName(name) ?? false,
                EffectsSource = () => _worldEffectsFactory.EnsureWorldEffects(state.Gamez, damageScene, state.Textures,
                    damageProgram, damageRuntime),
                DebugSpec = _spec.DebugDamage,
                BottomMargin = _spec.AnimLab ? 252 : 16,
            };
        }

        // ⚠ Do not move this earlier: every mechanism that places or hides a world entity must have
        // run, so that anything still on the world origin is content this mission never placed.
        // Switched off at once; _Process polls RestorePlacedEntities for what a motion moves.
        _unplacedWatch = builder;
        if (builder.HideUnplacedEntities() is { Count: > 0 } unplaced)
        {
            Log.Info("world", $"world: {unplaced.Count} unplaced entit(y/ies) left at the origin, switched off: {string.Join(", ", unplaced)}");
        }

        // Map-edge continuation: a rolling window of mirrored terrain tiles that follows the plane
        // past the map boundary. On in --fly and in static weathered views; ⚠ leave it off for
        // plain orbit viewing, which is an honest view of the data.
        if (_spec.Fly || _spec.Freecam || _spec.SkyZoneExplicit)
        {
            long edgeMark = StartupProfile.Mark();
            // Block depth is per chapter (A/B'd against the original); --map-edge-block= overrides
            // it, which is why the spec keeps it nullable rather than pre-defaulted.
            int block = _spec.MapEdgeBlock ?? Mech3.MapEdgeExtender.DefaultBlockCells(_spec.Chapter);
            _edgeExtender = builder.CreateEdgeExtender(session.Clutter, block, _spec.MapEdgeRepeat,
                census: _spec.DumpTileGrid);
            StartupProfile.Record("edge", edgeMark);
            if (_edgeExtender != null)
            {
                _plane.AddChild(_edgeExtender);
                Log.Info("world", $"map edge: rolling tile window active, block {_edgeExtender.BlockCells} cell(s), {(_edgeExtender.RepeatInsteadOfMirror ? "repeat" : "mirror (NOT what the original does)")}");
            }

            // --dump-tilegrid: the census is complete the moment the extender exists (it is built
            // in ScanTiles), so the report is written here and the session ends, no window is
            // ever needed, and nothing later in the build can change what was scanned.
            if (_spec.DumpTileGrid)
            {
                string? report = _edgeExtender?.WriteCensus(_spec.Chapter);
                if (report == null)
                {
                    Log.Error("world", $"--dump-tilegrid: {_spec.Chapter} builds no map-edge continuation (no area/partition grid, or no recognizable ground tiles at all).");
                    GetTree().Quit(1);
                    return false;
                }
                string name = _spec.DumpTileGridPath.Length > 0
                    ? _spec.DumpTileGridPath
                    : $"tilegrid_{_spec.Chapter}.json";
                _probeRunner.WriteScratch(name, report);
                // WriteScratch resolves a relative name under ./.scratch/ and passes an absolute
                // one straight through, so the flag takes either.
                Log.Info("world", $"--dump-tilegrid: {_spec.Chapter} census → {name}");
                GetTree().Quit();
                return false;
            }
        }
        if (_spec.Fly || _spec.Freecam || _spec.SkyZoneExplicit)
        {
            long weatherMark = StartupProfile.Mark();
            // The ambient cloud field: fogvol.zrd clutter scattered through the fvol* boxes this
            // gamez authors. World-anchored, so it is built once beside the world rather than per
            // rig, and needs no per-frame driving unlike the dome/deck/whiteout below.
            var fogVolumes = Mech3.FogVolumeSpec.VolumesOf(state.Gamez);
            var fogVolumeSpec = Mech3.FogVolumeSpec.Load(SessionPaths.ChapterZrdr(_dataRoot, _spec.Chapter));
            var cloudField = Effects.FogVolumeClutter.Create(state.Gamez, state.Textures,
                fogVolumeSpec, fogVolumes, _spec.CloudJitter);
            if (cloudField != null)
            {
                _worldRoot!.AddChild(cloudField);
                // The jitter is named on every launch, not only when a flag moved it. The shipped
                // offset is a departure from the decoded lattice, so a run's record has to say
                // which field it drew.
                string jitterVia = _spec.CloudJitter > 0f
                    ? $", remake jitter {_spec.CloudJitter:0.#} m" : ", decoded lattice, no jitter";
                Log.Info("world", $"fogvol clouds: {cloudField.InstanceCount} sprites ({cloudField.BaseCount} base + {cloudField.ExtensionCount} map-edge extension) over {fogVolumes.Count} volume(s), {cloudField.Summary}{jitterVia}");
            }
            // ⚠ Read the fvol zone from the data, never assume it: a chapter authoring -1 keeps the
            // default layer and renders below its deck, which is authored and not a bug. One
            // MultiMesh spans every volume, so it cannot carry a per-volume layer.
            int fvolZone = Mech3.WorldBuilder.FogVolumeZoneIdOf(state.Gamez);
            if (cloudField != null && Mech3.ZoneGate.LayerFor(fvolZone) is var fvolLayer and not 0)
            {
                UI.Boards.SplitScreen.SetVisualLayer(cloudField, fvolLayer);
            }

            // The sun goes in with the weather: its bearing is the zone's own SUNLIGHT_ORIENTATION,
            // applied by the same zone-apply that writes the fog. The ambience is the wind seam and
            // the viewer set carries each pane's camera pose for the puffer distance fade.
            _weatherRig = new WeatherRig(_spec, _worldRoot!, _sun, _ambience, _viewers, _env);
            // The deck's own zone_id, the one gated population that cannot ride a visual layer (it
            // is a per-rig camera-anchored copy, see WeatherRig.SetDeckZoneId).
            _weatherRig.SetDeckZoneId(builder.CloudDeckZoneId);
            // ⚠ Read the deck's altitude off the built data; do not hardcode it or pin the deck to
            // the CLOUD_COVER band centre.
            _weatherRig.SetDeckAltitude(builder.CloudDeckAltitude);
            // The same census and parsed fogvol.zrd the cloud field was built from, handed to a
            // second consumer rather than re-loaded. Tick resolves each camera's weather state from
            // it, and its in-volume whiteout where fog_zone is armed.
            _weatherRig.SetFogVolumes(fogVolumes, fogVolumeSpec);
            // The flown objects the band's per-object gate moves between layers (ObjectZoneGate).
            // A plane or a zeppelin on the far side of the overcast stops drawing.
            _weatherRig.SetGatedObjects(GatedObjects);
            // The horizon's zone children go in with the mission's weather: the zone the fog and
            // the dome share is picked from both (three chapters ship an empty zone2).
            _weatherRig.Build(state.MissionZrdrPath, _rigs, builder.HorizonZones(),
                activeZone =>
            {
                // One dome per horizon zone the gate can tell apart, not just the flown one: below
                // the cloud deck the camera is in state 1 and zone1 is the sky. The per-rig
                // container is what Tick anchors, so each dome keeps its own scale and gate.
                var zones = builder.HorizonZones();
                var domeZones = Mech3.WorldBuilder.DomeZonesToBuild(zones, activeZone);
                // The volumetric layer below stands in for the dome's painted cloud cards too.
                builder.HideCloudCards = Utils.GraphicsMode.Enhanced
                    && (_spec.SkippedPasses & EnhancedPasses.Clouds) == 0;
                builder.SkyAtInfinity = builder.HideCloudCards;
                foreach (var rig in _rigs)
                {
                    var anchor = new Node3D { Name = "horizon" };
                    foreach (string zoneName in domeZones)
                    {
                        var dome = builder.BuildHorizon(zoneName);
                        if (dome == null)
                            continue;
                        dome.Name = $"dome_{zoneName}";
                        // ⚠ Scale per dome, never once for the container: a chapter's two zone domes
                        // differ in size, and the flown one's scale must not move because a second
                        // was added beside it. See HorizonScaleFor.
                        dome.Scale = Vector3.One * HorizonScaleFor(dome);
                        anchor.AddChild(dome);
                        int zoneId = -1;
                        foreach (var z in zones)
                            if (z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
                                zoneId = z.ZoneId;
                        rig.HorizonDomes.Add(new HorizonDome(dome, zoneId));
                    }
                    if (anchor.GetChildCount() == 0)
                    {
                        anchor.QueueFree();
                        break;
                    }
                    if (rig.VisualLayer != 0)
                        UI.Boards.SplitScreen.SetVisualLayer(anchor, rig.VisualLayer);
                    _worldRoot!.AddChild(anchor);
                    rig.Horizon = anchor;
                }

                // The evidence that the swap exists at all, since a broken gate and a one-dome
                // chapter render identically at the state they share (docs/verification.md).
                builder.HideCloudCards = false;
                builder.SkyAtInfinity = false;

                if (_rigs.Count > 0)
                {
                    var built = _rigs[0].HorizonDomes;
                    var parts = new List<string>();
                    foreach (var d in built)
                        parts.Add($"{d.Node.Name} (zone_id {d.ZoneId})");
                    Log.Info("world", $"horizon: {built.Count} dome(s) per rig, {string.Join(", ", parts)}{(built.Count > 1 ? "; shown by camera weather state" : "")}");
                }
            });
            if (_fogStateBeforeWeather is { } heldFog)
            {
                _fogStateBeforeWeather = null;
                _weatherRig.ApplyFogState(heldFog);
            }
            // Enhanced mode alone: the baked cloud field, overcast at the band where the chapter has a
            // deck, scattered at its authored cloud height where it has none (Effects.VolumetricClouds).
            if (Utils.GraphicsMode.Enhanced && (_spec.SkippedPasses & EnhancedPasses.Clouds) == 0)
            {
                var (floor, overcast) = Effects.VolumetricClouds.LayerFor(
                    builder.CloudDeck != null, _weatherRig.CloudBand, builder.CloudClusters, state.Gamez);
                // Where the original fills the sky: its fvol boxes and the placed cloudparent clusters.
                var authored = new List<Aabb>();
                foreach (var v in fogVolumes)
                    authored.Add(v.Box);
                foreach (var cluster in builder.CloudClusters)
                {
                    if (Effects.VolumetricClouds.ClusterBox(cluster, state.Gamez) is { } clusterBox)
                        authored.Add(clusterBox);
                }
                if (Effects.VolumetricClouds.Create(state.Gamez, floor, overcast, _spec.Chapter, authored) is { } clouds)
                {
                    _worldRoot!.AddChild(clouds);
                    _weatherRig.DeckReplaced = true;
                    // The volume stands in for every authored cloud population, not only the deck: the
                    // fvol sprite field and the placed cloudparent clusters would float inside it.
                    if (cloudField != null)
                        cloudField.Visible = false;
                    foreach (var cluster in builder.CloudClusters)
                        cluster.Visible = false;
                }
            }
            StartupProfile.Record("weather", weatherMark);
        }

        // ⚠ Build the flare after the weather: the sun node it anchors to is a child of each rig's
        // horizon subtree, and finding it is one of the two gates. Safe unconditionally, since the
        // chapter data decides whether anything is built.
        _lensFlareRig = new LensFlareRig(_spec);
        _lensFlareRig.Build(_rigs, state.Textures, _interpPath, _spec.Chapter);
        state.MeshInstances = builder.MeshInstanceCount + state.StagedAircraftMeshes;
        state.Colliders = builder.ColliderCount;
        // Read after the domes, since C1's daytime sky layer is a horizon child.
        if (builder.ScrollingModelCount > 0)
            Log.Info("world", $"texture scroll: {builder.ScrollingModelCount} model(s) animating UVs");
        // The evidence that the authored render flags reached the materials, a night chapter
        // reporting 0 self-lit models means they did not.
        Log.Info("world", $"model flags: {builder.UnlitModelCount} self-lit (lighting: false), {builder.UnfoggedModelCount} unfogged (fog: false)");
        // The per-polygon second material pass. A declined count above zero means a
        // sprite/facade mesh carried one and it was dropped, never observed in this install.
        if (builder.OverlayPassSurfaceCount > 0 || builder.OverlayPassDeclinedCount > 0)
            Log.Info("world", $"overlay passes: {builder.OverlayPassSurfaceCount} surface(s) built, {builder.OverlayPassDeclinedCount} polygon(s) declined");
        // The evidence that SHOW_BACKFACE reached the colliders: a build with collision on that
        // reports no one-sided faces is back to the old blanket two-sided flag.
        if (builder.CollisionSidedness.Count > 0)
        {
            var classes = builder.CollisionSidedness
                .Select(kv => $"{(kv.Key.Length == 0 ? "default" : kv.Key)} {kv.Value.OneSided}/{kv.Value.OneSided + kv.Value.TwoSided}");
            Log.Info("world", $"collision sidedness: one-sided faces per class {string.Join(", ", classes)}; {builder.CollisionBackToBackPairs} back-to-back pair(s)");
        }
        // C3's skydome cloud cards, and nothing else in the install: the tripwire if the
        // absent-and-undrawn rule ever reaches a chapter that ships the texture.
        if (builder.UndrawnPolygonCount > 0)
            Log.Info("world", $"undrawn polygons: {builder.UndrawnPolygonCount} dropped (texture absent from game data)");
        state.What = $"chapter {_spec.Chapter} world";

        // The animation debugger (--anim-lab): the lab node owns the clock and the
        // transport; the world above is its quiet stage (AutoStart=false, reset states
        // and mission setup applied, nothing playing until A or --play-anim).
        if (_spec.AnimLab)
        {
            BuildAnimLabStage(state, session);
        }
        return true;
    }

    // The anchor scale for one built skydome: HorizonScale, reduced where that would push the
    // dome's far wall past the camera's far plane and let the clear colour show through.
    // ⚠ HorizonScale is a MAXIMUM, not a constant, and the fit is measured from the built dome's
    // own AABB, never a per-chapter table. Scaling only Y is refuted; the domes keep one uniform
    // fitted scale.
    private float HorizonScaleFor(Node3D dome)
    {
        if (Mech3.WorldBuilder.DetachedWorldAabb(dome) is not { } aabb)
            return HorizonScale;
        var min = aabb.Position;
        var max = aabb.End;
        float radius = Mathf.Max(
            Mathf.Max(Mathf.Abs(min.X), Mathf.Abs(max.X)),
            Mathf.Max(
                Mathf.Max(Mathf.Abs(min.Y), Mathf.Abs(max.Y)),
                Mathf.Max(Mathf.Abs(min.Z), Mathf.Abs(max.Z))));
        if (radius <= 0f)
            return HorizonScale;
        float fitted = Mathf.Min(HorizonScale, _camera.Far * HorizonFarFraction / radius);
        if (fitted < HorizonScale)
            Log.Info("world", $"horizon: dome radius {radius:0} m x {HorizonScale:0.##} would reach past the {_camera.Far:0} m far plane, scaled {fitted:0.##}x instead");
        return fitted;
    }

    // --anim-lab: the animation debugger's quiet stage, the effect/crash anchor stage, the mission
    // spawn point, the freecam-style SpectatorCamera, an optional parked --plane= prop, and the
    // AnimLab node itself.
    private void BuildAnimLabStage(BuildState state, WorldSession session)
    {
        // ⚠ Use ManualAdvance, never SetProcess(false): Godot re-enables processing at READY for a
        // node overriding _Process, and the runtime enters the tree after this line, so the world
        // would run at double speed on fixed steps plus wall dt.
        session.Runtime.ManualAdvance = true;

        // The lab's effect/crash stage under ONE staging node it moves in front of the camera.
        // ⚠ Keep it indexed as its own subtree, so a played def resolves its puffer hosts and its
        // healthy/destroyed anchors here instead of onto a generic world node of the same name.
        var labStage = new Node3D { Name = "lab_stage_anchor" };
        // Which template roots: derived from the crash/damage defs themselves against this stage's
        // own scope, the crash-anchor set, built first and parented after, so the lab stages
        // exactly what BuildFlightCrashRuntime derives without changing this subtree's child order.
        var labAnchors = WorldEffectsFactory.BuildCrashAnchorSet();
        int effectRoots = WorldEffectsFactory.BuildEffectStage(state.Gamez, session.Builder.Scene,
            labStage, WorldEffectsFactory.CrashStageRootNames(session.Program, state.Gamez, labAnchors));
        labStage.AddChild(labAnchors);
        session.Root.AddChild(labStage);
        session.Runtime.IndexStage(labStage);
        Log.Info("anim", $"anim-lab: stage, {effectRoots} effect template(s) + player anchor set built + indexed");

        // The spawn the mission would place the player at, the camera starts here so
        // the interesting part of the map is in view, and (below) an optional parked
        // plane sits on it. Resolved once so the camera and plane agree.
        var labSpawns = SpawnPoints.LoadIa(state.MissionZrdrPath, _spec.Scenario);
        var (spawnPos, spawnLook) = _spawnPicker.ChooseSpawn(labSpawns, state.MissionZrdrPath,
            _spawnPicker.ChooseSpawnBase(labSpawns), 0, "");

        // Camera: the freecam SpectatorCamera (RMB look, WASD/QE move), like --freecam,
        // in place of the orbit view, the lab drives it (Frame/FollowNode) on
        // play/pick. Starts at the mission spawn; --pos/--direction override.
        var camPos = _spec.CamPos ?? spawnPos;
        var camLook = _spec.CamDir is { } labDir ? camPos + labDir : _spec.LookAt ?? spawnLook;
        var labCam = new SpectatorCamera(_camera, camPos, camLook)
        {
            ShowReadout = false,
            LockCandidates = LockCandidateAircraft,
        };
        // --node=: the mission spawn is meaningless on a single-subtree stage, frame
        // the subject instead, unless the tester placed the eye themselves.
        if (state.NodeAabb is { } nodeBox && _spec.CamPos == null && _spec.LookAt == null && _spec.CamDir == null)
        {
            labCam.Frame(nodeBox);
        }
        _worldRoot!.AddChild(labCam);
        _spectator = labCam;

        // Optional stage prop: --plane= parks that aircraft at the mission spawn point, in the
        // Fortune Hunters livery and with no FlightController. It carries its docking-hook group
        // and is indexed, or a hook definition resolves nothing and plays placeless.
        if (_spec.PlaneNames.Count > 0)
        {
            long mark = StartupProfile.Mark();
            var planesGamez = GameZ.Load(state.PlanesGamezPath);
            StartupProfile.Record("gamez", mark);
            mark = StartupProfile.Mark();
            var parkedBuilder = new PlaneBuilder(planesGamez, state.Textures,
                scheme: _liveryResolver.SchemeFor(0, state.ZrdrPath, _liveryResolver.NewPaintRng(),
                    _liveryResolver.PatternsForPlane(planesGamez, _spec.PlaneName)),
                patterns: _liveryResolver.Patterns, dockingHook: true);
            var parked = parkedBuilder.Build(_spec.PlaneName);
            StartupProfile.Record("plane", mark);
            state.MeshInstances += parkedBuilder.MeshInstanceCount;
            _worldRoot!.AddChild(parked);
            parked.Position = spawnPos;
            if ((spawnLook - spawnPos).LengthSquared() > 1e-6f)
            {
                parked.LookAtFromPosition(spawnPos, spawnLook, Vector3.Up);
            }
            // Indexed the way the effect stage is, since the bootstrap indexed the world before
            // this prop existed; the RESET_STATE tail is what parks its hook arms.
            session.Runtime.IndexStage(parked);
            state.What += $" + parked '{_spec.PlaneName}'";
            // The parked prop hangs beside the world content, outside the selection/node-lab walk,
            // so register it as an extra pick root or neither a click nor the lab tree reaches it.
            _selectionExtraRoots.Add(parked);
        }

        var animLab = new UI.Labs.AnimLab(session.Runtime, session.Program, labCam,
            labStage, state.Textures, state.Sounds, _masterSeed, _spec.PlayAnim,
            // On a --node= stage the subject IS the stage and is already framed; letting
            // the lab re-aim on every Play swings the camera off the only object there
            // (measured: the tower left the frame entirely on its own destruction).
            autoFrame: _spec.CamPos == null && _spec.LookAt == null && _spec.CamDir == null
                       && state.NodeSubtree == null)
        {
            // Interactive shows the whole lab UI; a scripted --screenshot hides it so
            // the 3D shot stays byte-identical, unless --debug-anim-ui forces it on
            // to capture the timeline (the same convention as --debug-livery).
            ShowUi = !_captureDirector.Pending || _spec.DebugAnimUi,
            // The lab's camera follows whichever rung of the shared selection is current.
            Selection = _selection,
        };
        _worldRoot!.AddChild(animLab);
        state.AnimLabNode = animLab;
        Log.Info("anim", $"anim-lab: quiet stage, seed {_masterSeed}, fixed dt 1/60{(_spec.PlayAnim != null ? $", playing '{_spec.PlayAnim}'" : "")}, freecam (RMB look, WASD/QE move); transport on the button panel, P pause · . step · R restart · F picker · N node lab; click an object to follow");
        state.What += " + anim lab";
    }

    // The parked-plane static view (--viewer or a bare --plane=): builds the model unpainted
    // (--viewer) or pre-painted, then the damage and livery labs that only make sense parked.
    private void BuildStaticStage(BuildState state)
    {
        // ⚠ Resolve the scheme once here, so the livery lab below opens on exactly what the plane
        // wears rather than a second roll of --paint=random.
        long mark = StartupProfile.Mark();
        var staticPatterns = _liveryResolver.PatternsForPlane(state.Gamez, _spec.PlaneName);
        var staticScheme = _liveryResolver.SchemeFor(0, state.ZrdrPath, _liveryResolver.NewPaintRng(), staticPatterns);
        // In --viewer the LIVERY LAB owns the livery and applies it itself, so the
        // model is built bare and there is one write path for paint (its Repaint).
        // cockpitInterior rides the same gate as damagePanels: pcdp4/pcdp6 hidden, B12.
        var builder = new PlaneBuilder(state.Gamez, state.Textures, damagePanels: _spec.Viewer,
            scheme: _spec.Viewer ? null : staticScheme, patterns: _liveryResolver.Patterns,
            cockpitInterior: _spec.Viewer);
        _plane = builder.Build(_spec.PlaneName);
        StartupProfile.Record("plane", mark);
        state.MeshInstances = builder.MeshInstanceCount;
        state.What = $"'{_spec.PlaneName}'";

        // Damage lab: per-part HP sliders driving the same DamageVisuals/puffer pipeline as flight.
        // Present in every --viewer session, opened at launch only by --damage.
        if (_spec.Viewer)
        {
            mark = StartupProfile.Mark();
            var stats = PlaneStats.Load(state.ZrdrPath, _spec.PlaneName);
            StartupProfile.Record("zrdr", mark);
            if (stats.DestroyableParts.Count == 0)
            {
                Log.Info("flight", $"damage lab: '{_spec.PlaneName}' ({stats.DefName}) has no destroyable_parts");
            }
            else
            {
                // Stand-in puffers: the parked plane travels no distance, so the authored
                // distance-interval trail defs the flight lab plays would emit nothing here.
                var smoke = Effects.Puffer.MakePuffer(state.ZrdrPath, state.Textures, _worldRoot!, "pufftrails.json", "smokepuffer", ambience: _ambience);
                var fire = Effects.Puffer.MakePuffer(state.ZrdrPath, state.Textures, _worldRoot!, "pufftrails.json", "firepuffer", ambience: _ambience);
                var panelTrails = new List<Effects.Puffer>();
                for (int i = 0; i < 8; i++) // pool one per pdp panel, the lab can flip all of them
                    if (Effects.Puffer.MakePuffer(state.ZrdrPath, state.Textures, _worldRoot!, "pufftrails.json", "firepuffer", ambience: _ambience) is { } pt)
                        panelTrails.Add(pt);
                // The healthy↔torn candidate sets from the authored defs, the viewer
                // has no anim program, so the two reader files are loaded directly.
                var pairingDefs = new List<Mech3.AnimDefinition>();
                pairingDefs.AddRange(Mech3.AnimDefs.LoadFileDefs(state.ZrdrPath, "player_destruct_reset.json"));
                pairingDefs.AddRange(Mech3.AnimDefs.LoadFileDefs(state.ZrdrPath, "player-1.json"));
                var visuals = new DamageVisuals(builder.DamagePanels, _plane, stats, smoke, fire, panelTrails,
                    DamageVisuals.PanelPairingSets(pairingDefs), cockpitPanels: builder.CockpitDamagePanels);
                // the HUD gauge cluster as a lab toggle (user request): the damage
                // dial mirrors the sliders, blinks on decreases like a flight hit
                var labGauges = GaugeCluster.Build(state.Gamez, _spec.PlaneName, state.Textures, stats.DestroyableParts);
                _damageLab = new DamageLab(stats, new ViewerDamageTarget(visuals),
                    _spec.DamagePreset, labGauges)
                {
                    StartHidden = !_spec.DamageLab, // --damage opens it; plain --viewer waits for F19
                };
                _worldRoot!.AddChild(_damageLab);
                Log.Info("flight", $"damage lab: {stats.DestroyableParts.Count} part sliders, {visuals.PanelCount} panels, {panelTrails.Count} panel fire trails{(_spec.DamageLab ? "" : " (hidden, F19)")}");
                state.What += _spec.DamageLab ? " + damage lab" : " + damage lab (F19)";
            }
        }

        // Livery lab (--viewer, L): pattern, RGB sliders and decal slots repainting the parked plane
        // through PlaneBuilder.Repaint. Hidden and unpainted unless --paint named a scheme, so an
        // unadorned --viewer screenshot is unchanged.
        if (_spec.Viewer && builder.SkinPrefix != null)
        {
            var lab = new UI.Labs.LiveryLab(builder, _liveryResolver.PaintCatalog(state.ZrdrPath), state.Textures, staticScheme,
                _liveryResolver.Patterns.PatternsFor(builder.SkinPrefix))
            {
                DebugShow = _spec.DebugLivery.HasValue,
                DebugPatternSteps = _spec.DebugLivery ?? 0,
            };
            _worldRoot!.AddChild(lab);
            state.What += " + livery lab";
        }

        if (_spec.Viewer)
            state.What += " + mesh lab";
    }

    // Joins the built subject to the tree, then the labs shared by every mode that observes it: the
    // world selection/node/damage labs, the viewer's mesh lab, the marker overlay and the weapon
    // lab. Returns false if the session must abort.
    private bool AttachPlaneAndLabs(BuildState state)
    {
        _worldRoot!.AddChild(_plane);
        // The shared selection joins after the world does: its pick walk and its highlight box
        // both read GlobalTransform, which on a detached subtree is identity + error spam.
        if (_selection != null)
        {
            _worldRoot!.AddChild(_selection);
        }
        // The node lab joins after the selection, so its first _Process (which carries the
        // scripted dump) runs once the selection's own scripted pick has settled.
        if (_nodeLab != null)
        {
            _worldRoot!.AddChild(_nodeLab);
        }
        // The damage lab joins after the node lab, so a scripted script can select through the
        // node lab's name index on the frame it runs.
        if (_worldDamageLab != null)
        {
            _worldRoot!.AddChild(_worldDamageLab);
        }
        // Mesh lab (--viewer, M): normals, wireframe, zone boxes, lighting and the cull/normal
        // overrides. ⚠ Build it after the plane joins the tree; it reads geometry back through
        // GlobalTransform, which on a detached node returns identity and logs per call.
        if (_spec.Viewer && _plane != null)
            _worldRoot!.AddChild(new UI.Labs.MeshLab(_plane, PlaneCollider.Build(_plane),
                _sun, _env, _camera)
            { DebugSpec = _spec.DebugMesh });
        // Marker overlay (--viewer --plane, K): the firepoint, pylon and target gizmos. Only on the
        // parked plane, since a chapter world has no marker rig, and after it joins the tree,
        // since the overlay reads each marker's GlobalPosition.
        if (_spec.Viewer && !_spec.WorldMode && _plane != null)
        {
            _worldRoot!.AddChild(new UI.Overlays.MarkerOverlay(_plane) { StartHidden = !_spec.MarkersOverlay });
            state.What += _spec.MarkersOverlay ? " + marker overlay" : " + marker overlay (K)";
        }
        // --weapon-test: the whole-catalogue pass check on a PARKED plane. ⚠ Keep it on this cheap
        // no-world path: it asks only whether every weapon mounts and spawns without throwing, and
        // the interactive lab lives in flight because it needs a real world, pool and trigger.
        if (_spec.WeaponTest && _spec.Viewer && !_spec.WorldMode && _plane != null)
        {
            long mark = StartupProfile.Mark();
            var labWeapons = WeaponDefs.Load(state.ZrdrPath, Messages.Load(state.MessagesPath));
            StartupProfile.Record("zrdr", mark);
            // The bench fires the airframe's WHOLE rig, seeded from the plane's stock entry where
            // it has one, so a weapon stock never mounts still gets a mount of its own class.
            LoadoutDef? stock = null;
            foreach (var ldef in StockLoadouts.Load().All.Values)
            {
                if (ldef.Model == _spec.PlaneName)
                {
                    stock = ldef;
                    break;
                }
            }
            var benchLoadout = Loadout.ForRig(_plane, labWeapons, stock);
            // The bench's own scene-less pool: rockets fly streak-only, impacts show stand-ins, and
            // there is no DamageSink. ⚠ Do not build a WeaponLab here; the pass check is the bench's.
            var benchPool = new ProjectilePool(state.Textures, null, null);
            _worldRoot!.AddChild(benchPool);
            if (_camera != null)
                benchPool.Viewers.Bind(new[] { _camera });
            // Fire every weapon once per mount and report any that throw, then quit. The report is
            // synchronous, so no world tick is required.
            string report = WeaponBench.Run(_plane, benchLoadout, labWeapons, benchPool).Report;
            Log.Raw(report);
            _probeRunner.WriteScratch("weapon_test.txt", report);
            GetTree().Quit();
            return false;
        }
        return true;
    }

    // The deck is now in the tree at its original position; remember its centre so _Process can
    // re-anchor it under each player every frame, and give every rig past the first its own copy.
    private void AssignCloudDeckIfBuilt(BuildState state)
    {
        if (state.CloudDeck != null)
        {
            _weatherRig?.SetDeckCenter(OrbitCamera.MergedAabb(state.CloudDeck).GetCenter());
            if (state.DeckUndimmedMeshes != null)
                _weatherRig?.SetDeckUndimmedMeshes(state.DeckUndimmedMeshes);
            AssignCloudDecks(state.CloudDeck);
        }
    }

    // Spectator mode (--freecam): the live world with no aircraft, observed from a free-flying
    // camera that starts where the mission would have spawned the player, or wherever --pos put it.
    private void BuildFreecamSpectator(BuildState state)
    {
        if (!_spec.Freecam)
            return;
        Vector3 camPos, camLookAt;
        if (_spec.EmptyStage)
        {
            // The empty stage has no mission and therefore no spawn list: look at the grid
            // origin, which is where a --stage=empty subject is put.
            camPos = EmptyStage.CameraPos;
            camLookAt = Vector3.Zero;
        }
        else
        {
            var freecamSpawns = SpawnPoints.LoadIa(state.MissionZrdrPath, _spec.Scenario);
            (camPos, camLookAt) = _spawnPicker.ChooseSpawn(freecamSpawns, state.MissionZrdrPath,
                _spawnPicker.ChooseSpawnBase(freecamSpawns), 0, "");
        }
        if (_spec.CamPos is { } cp) camPos = cp;
        // The aim: a direction from wherever the eye ended up, or the named point.
        if (_spec.CamDir is { } cd) camLookAt = camPos + cd;
        else if (_spec.LookAt is { } la) camLookAt = la;
        _spectator = new SpectatorCamera(_camera, camPos, camLookAt)
        {
            // A scripted --screenshot run wants the frame clean of the overlay.
            ShowReadout = !_captureDirector.Pending,
            LockCandidates = LockCandidateAircraft,
        };
        _worldRoot!.AddChild(_spectator);
        state.What += " + freecam";
        Log.Info("core", $"freecam: spectator camera at ({camPos.X:0}, {camPos.Y:0}, {camPos.Z:0}), hold RMB to look, WASD/QE to move, Shift boost, wheel sets speed; click an object to select it, PgUp/PgDn walk its ancestor ladder (Home/End jump), N opens the node lab, F19 the damage lab on whatever destructible is selected");
    }

    // --fly (and --stunt): builds every rendered rig's aircraft (model, loadout, HUD, audio,
    // stunt/crash hookup) over the session-wide flight data loaded once above the per-player loop.
    // The per-player body lives in its own HumanFlightAdapter.
    /// <summary>Decodes every sound the weapons catalogue binds, at the <c>LOOPED</c> flag its play
    /// site asks for (<see cref="WeaponDefs.SoundCues"/>). A name may be a <c>SOUND_GROUPS</c>
    /// group, whose members are picked at random per shot, so every member decodes.
    /// ⚠ Silent on a miss: a per-chapter archive legitimately lacks weapons another chapter uses,
    /// and the report that matters is the one at the point of use.</summary>
    private void PrewarmWeaponSounds(BuildState state, WeaponDefs weaponDefs)
    {
        if (state.Sounds is not { } archive || state.SoundDefs is not { } soundDefs)
        {
            return;
        }
        long mark = StartupProfile.Mark();
        int decoded = 0;
        foreach (var (name, looped) in weaponDefs.SoundCues())
        {
            foreach (string member in ExpandSoundGroup(name, state.SoundGroups))
            {
                if (soundDefs.TryGetValue(member, out var def)
                    && archive.Find(def.WavName, looped, warn: false) != null)
                {
                    decoded++;
                }
            }
        }
        StartupProfile.Record("prewarm", mark);
        Log.Info("sound", $"weapons prewarm: {decoded} stream(s) decoded before the archive closed");
    }

    // A cue name is either a SOUND_GROUPS group or a plain definition. Mirrors WorldSounds.Prewarm's
    // expansion, including the dialogue chains a group can carry.
    private IEnumerable<string> ExpandSoundGroup(string name, Dictionary<string, SoundGroup>? groups)
    {
        if (groups == null || !groups.TryGetValue(name, out var group))
        {
            yield return name;
            yield break;
        }
        foreach (var (member, _) in group.Members)
        {
            yield return member;
        }
        foreach (var chain in group.Chains)
        {
            foreach (string line in chain)
            {
                yield return line;
            }
        }
    }

    private void BuildFlightRigs(BuildState state)
    {
        long mark = StartupProfile.Mark();
        // On the empty stage the session gamez IS planes.zbd (there is no chapter world), so there
        // is nothing to load a second time. ⚠ Unless --zep= grafted a chapter node on: the session
        // gamez is then that chapter's, and planes.zbd has to be loaded here as everywhere else.
        var planesGamez = _spec.EmptyStage && _spec.Zep == null
            ? state.Gamez
            : GameZ.Load(state.PlanesGamezPath);
        StartupProfile.Record("gamez", mark);
        // Stats are per plane, not per player (splitscreen players can pick
        // different aircraft), load each distinct one once, logging it as it appears.
        var statsCache = new Dictionary<string, PlaneStats>();
        PlaneStats StatsFor(string plane)
        {
            if (statsCache.TryGetValue(plane, out var cached))
                return cached;
            var loaded = PlaneStats.Load(state.ZrdrPath, plane);
            statsCache[plane] = loaded;
            Log.Info("flight", $"flight stats [{loaded.DefName}]: fd_speed={loaded.FdSpeed} m/s weight={loaded.VehWeight} engine={loaded.EnginePower:0.00} torques=({loaded.PitchTorque},{loaded.RollTorque},{loaded.RudderTorque})");
            return loaded;
        }
        // The AI flavour of the same airframe: a different object off the same key, so it needs its
        // own dictionary. Loaded lazily, so a session with no AI spawns parses nothing twice.
        var aiStatsCache = new Dictionary<string, PlaneStats>();
        PlaneStats AiStatsFor(string plane, string? aiDef)
        {
            // Keyed by both: one airframe flown by two militias is two different sets of weapons,
            // paint and skills off the same node name.
            string key = aiDef == null ? plane : $"{plane}/{aiDef}";
            if (aiStatsCache.TryGetValue(key, out var cached))
                return cached;
            var loaded = PlaneStats.LoadForAi(state.ZrdrPath, plane, aiDef);
            aiStatsCache[key] = loaded;
            Log.Info("flight", $"ai flight stats [{loaded.DefName} damage:{loaded.AiDefName}]: armor={loaded.VehicleArmor:0.#} health={loaded.VehicleHealth:0.#} zones={loaded.DestroyableParts.Count} injure_anims={loaded.VehicleInjureAnims.Count}");
            return loaded;
        }
        _aiStatsFor = AiStatsFor;
        // The camera's per-plane tuning, cached the same way and for the same reason. Only the
        // chase distance is applied; the line names it so a capture's evidence is in its own log.
        var camCache = new Dictionary<string, CamParams>();
        CamParams CamParamsFor(string plane)
        {
            if (camCache.TryGetValue(plane, out var cached))
                return cached;
            var loaded = CamParams.Load(state.ZrdrPath, plane);
            camCache[plane] = loaded;
            Log.Info("flight", $"camera [{loaded.DisplayName ?? plane}]: dist={loaded.Dist:0.##} m{(loaded.FromData ? loaded.DisplayName == null ? " (camparam default, no block of its own)" : "" : " (no camparam.json, built-in defaults)")}");
            return loaded;
        }
        // Splitscreen: several own-ship engine stacks in one mix, equal-power scale them.
        float mixGain = 1f / Mathf.Sqrt(_rigs.Count);
        // The launchscreen's join flow binds the pads; a CLI launch derives them
        // from the connected roster instead.
        var padAssignment = _menuPads ?? Pads.AssignPads(_rigs.Count);
        // Ahead of the rigs, because the assembler hands both to the per-pane stunt board.
        _menuInputs = BuildMenuInputs(padAssignment);
        _pauseState = new PauseState();
        if (_menuPads != null)
            Pads.LogPads(_menuPads);
        // One livery RNG for the session, so P1..P4 draw distinct colours from one
        // stream and --paint-seed reproduces the whole field.
        var paintRng = _liveryResolver.NewPaintRng();
        // Instant Action: the mission's own mission_type IS the scenario key, and its player_plane
        // overrides whichever --plane= was given, so an --ia= launch needs neither flag.
        var iaRt = _iaDirector?.Runtime;
        string iaScenario = iaRt?.Def.MissionType ?? _spec.Scenario;
        string? iaPlayerNode = iaRt != null
            ? Mech3.InstantAction.PlaneNodeFor(iaRt.Def.PlayerPlane) : null;
        if (iaRt != null && iaPlayerNode == null)
        {
            GD.PushWarning($"ia: player plane '{iaRt.Def.PlayerPlane}' is not one of " +
                            $"the eleven airframes, flying '{_spec.PlaneName}' instead");
        }

        // What the mission forces on every human, which the wizard's own def does not: its
        // player_plane IS player 1's pick, so PlaneRoster.InstantActionOverride hands it back
        // as null and each pane flies the aircraft its pilot selected.
        string? iaOverride = PlaneRoster.InstantActionOverride(_spec, iaPlayerNode);
        // One spawn list for the session; each player takes the next index (wrapping).
        _spawnPicker.ScenarioOverride = iaRt != null ? iaScenario : null;
        // The world build (above) has already run the intro's own animation bootstrap, so
        // _cutscene.Playing is settled before the player's spawn is chosen. Only a campaign intro
        // withholds --pos=; every other --pos= flight keeps landing on it immediately.
        _spawnPicker.WithholdOverrideForCutscene = _campaign != null && _cutscene is { Playing: true };
        var spawnList = _spawnPicker.LoadSpawnList(state.MissionZrdrPath, iaScenario);
        int spawnBase = _spawnPicker.ChooseSpawnBase(spawnList);

        // The weapons catalogue and stock loadouts, loaded once, and ONE shared projectile pool
        // every player's guns fire into, since projectiles live in the shared world. The pool
        // reuses the session archives and the world gamez, so rockets get their flyout bodies.
        mark = StartupProfile.Mark();
        var weaponMessages = Messages.Load(state.MessagesPath);
        var weaponDefs = WeaponDefs.Load(state.ZrdrPath, weaponMessages);
        // Decode the catalogue's own sounds while the archive is open. Nothing else covers them:
        // weapons.json names them, not the anim program, and every one is first reached in flight
        // from a trigger pull or an impact, long after the build scope closes.
        PrewarmWeaponSounds(state, weaponDefs);
        var stockLoadouts = StockLoadouts.Load();
        var shakeDefs = ShakeDefs.Load(state.ZrdrPath);
        // The ai.zrd turret table. A missing/broken file costs the gunners, not the session.
        TurretDefs? turretDefs = null;
        try
        {
            turretDefs = TurretDefs.Load(state.ZrdrPath);
        }
        catch (Exception e)
        {
            GD.PushWarning($"turrets: ai.zrd unavailable, no turret gunners: {e.Message}");
        }
        StartupProfile.Record("zrdr", mark);
        // flyoutAnims: the world program also carries the rockets' FLYOUT MODEL_ANIMATION defs
        // (cam_anim / missile_puffers), from which the pool builds each type's smoke trail.
        // Null on the empty stage (no world program), rockets there fly trail-less, like the body.
        var projectiles = new ProjectilePool(state.Textures, state.Sounds, state.SoundDefs,
            flyoutGamez: state.Gamez, flyoutScene: state.WorldScene, flyoutAnims: state.CrashProgram,
            soundGroups: state.SoundGroups, ambience: _ambience)
        {
            // Route weapon hits to the world's destructibles: the pool's raycast
            // reports the struck collider, the runtime resolves it to a destructible and
            // spends the weapon's HEALTH_DAMAGE. Null runtime ⇒ impacts stay cosmetic.
            DamageSink = state.WorldRuntime != null ? state.WorldRuntime.DamageAt : null,
            // Route a CRATER weapon's ground strike to the mission's crater field. The pool asks only
            // for a node carrying can_modify, which no shipped node does (Mech3.CraterField).
            CraterSink = state.Craters != null ? state.Craters.TryCarve : null,
            // The same equal-power splitscreen factor FlightAudio's own-ship loops take, plus the
            // nearest-human snapshot shared with WorldSession and the world-effects runtime.
            MixGain = mixGain,
            PlayerPositions = PlayerPositionsSnapshot,
            // muzzle_burst's PLAYER_1ST_PERSON: the same closure the world runtime gets, so the
            // shot's lights pick the same testfp branch the anim data would.
            FirstPersonView = AnyPilotFirstPerson,
            // The Cockpit view's own rule is per shooter, not per session. The pilot in the canopy
            // loses their own flash quads, and every other aeroplane keeps theirs.
            CockpitViewOfPilot = PilotInCockpitView,
            BeeperTags = _beeperTags,
            WashSink = _screenFlash != null ? _screenFlash.PlayBlend : null,
            EngineDeadBounds = TanglerChoke.EngineDeadBounds(weaponDefs),
        };
        // ⚠ Bind EVERY pane's camera, never player 1's alone. The tracer pixel floor is a
        // screen-space rule over one shared world mesh, so a single viewer sizes every round
        // against that pane and draws the same geometry oversized in all the others.
        projectiles.Viewers = _viewers;
        // The first-person muzzle pair joins the world's point lights, which is how it reaches
        // the cockpit interior. The empty stage has no set and keeps the omni flash.
        if (_worldLights != null)
            projectiles.BindPointLights(_worldLights);
        _worldRoot!.AddChild(projectiles);
        _projectiles = projectiles;

        // The original's per-frame ground shadow, one quad under every aircraft. Roster and rigs
        // are read fresh, so waves are covered and each pane's own pilot takes the player's shape
        // there. Enhanced graphics mode builds nothing here and casts real shadow maps instead.
        GroundShadowPass.Build(_worldRoot!, AllAircraft, PlayerPositionsSnapshot, () => _rigs,
            () => _weatherRig?.SunlightRgb ?? WeatherRig.DefaultSunlightRgb);

        // The smoke screens' own smoke, wired here rather than at their construction because the
        // chapter's textures and anim program are only resolved this far into the build. Same
        // program as the rockets' flyout trails: missile_puffers carries generate_smokescreen too.
        if (_smokeScreens != null)
        {
            var smokeEmitters = new SmokeScreenEmitters(state.CrashProgram?.Defs, state.Textures,
                _worldRoot, _ambience);
            _smokeScreens.Emitters = smokeEmitters.Create;
        }

        // The world-effects runtime: one per session, rendering the impact and destruction puffers
        // the world runtime cannot. ⚠ Go through EnsureWorldEffects, never construct it directly,
        // or a later --destroy= or damage-lab demand on the same session builds a second one.
        AnimRuntime? worldEffects = null;
        if (state.WorldScene != null && state.WorldRuntime != null)
        {
            worldEffects = _worldEffectsFactory.EnsureWorldEffects(state.Gamez, state.WorldScene,
                state.Textures, state.CrashProgram!, state.WorldRuntime, projectiles); // the rigs' graze reaction plays through the same runtime
        }

        // Stunt run: the mission's danger-zone objectives, positions resolved against this chapter
        // world's gamez. Parsed once for the session; each player then races an independent copy.
        StuntMission? stuntZones = null;
        StuntRace? race = null;
        // An Instant Action stunt_flying mission IS a stunt run: the mission type asks for the
        // zones, so --stunt is not the tester's flag to remember on an --ia= launch.
        bool iaStunt = iaRt is { } iaStuntMission
            && string.Equals(iaStuntMission.Def.MissionType, "stunt_flying", StringComparison.OrdinalIgnoreCase);
        bool wantStunt = _spec.Stunt || iaStunt;
        if (wantStunt && _spec.EmptyStage)
        {
            Log.Info("flight", $"--stunt has no danger zones on the empty stage (no mission, no world), flying free");
        }
        else if (wantStunt)
        {
            stuntZones = StuntMission.Load(state.Gamez, state.MissionZrdrPath, Messages.Load(state.MessagesPath));
            if (stuntZones == null)
                // Expected for the chapters whose IA1 has no dzones (C1C, C2B), a data
                // fact, not a fault, so a plain line (log hygiene: no stack traces).
                Log.Info("flight", $"--stunt: no danger zones for {_spec.Chapter}/{_spec.Mission}, flying free");
            else if (_rigs.Count > 1)
                race = new StuntRace(); // splitscreen: a race, ranked on the shared board
            if (stuntZones != null && iaStunt && !_spec.Stunt)
                Log.Info("flight", $"ia: stunt_flying, {stuntZones.TotalCount} danger zone(s) from {_spec.Chapter}/{_spec.Mission}, the mission type's own objective");
        }

        // Dogfight (--vs): built here, before the rigs, same reason Race is (HumanFlightAdapter
        // binds every pane's VersusHud to this one instance below); the score/respawn plumbing
        // that feeds it Downed reports only runs once every rig exists, further down.
        VersusMatch? versus = _spec.Versus
            ? new VersusMatch(_rigs.Count, _spec.VsKills, _spec.VsTimeMinutes * 60f)
            : null;

        // The original's HUD bitmap font, loaded once and shared across panes. Null when the rimage
        // atlas is absent, and its consumers are then simply not built.
        HudFont? hudFont = HudFont.Load(Path.Combine(_dataRoot, "extracted", "rimage"));

        // The gun aiming reticle's pipper: the game's own impact_point.png, loaded once
        // and shared across panes (it carries its own alpha, no colour-keying). Null (no file)
        // simply omits the reticle.
        Texture2D? reticleTex = ImpactReticle.LoadTexture(
            Path.Combine(_dataRoot, "extracted", "rimage"), "impact_point.png");

        // ⚠ No --det bypass on this arm, unlike the race arm below: a story mission has one
        // PLAYER_INIT, so the plain walk stacks the whole field on it
        // (docs/architecture.md, ## src/Session/Roster/StartGrid.cs).
        bool coopCampaign = _spec.CampaignProfile != null && _rigs.Count > 1;
        // ⚠ Choose the spawn placement ONCE, by picking an implementation here, never by a runtime
        // flag inside one: a --det race stays byte-identical because StartGrid is then not built at
        // all. It cannot move up beside new SpawnPicker, which runs before `race` is settled.
        IFlightStarts flightStarts = coopCampaign || (race != null && !_spec.Det)
            ? new StartGrid(_spawnPicker, GroundSampler())
            : _spawnPicker;
        var aircraftResources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = StatsFor,
            AiStatsFor = AiStatsFor,
            CamParamsFor = CamParamsFor,
            PaintRng = paintRng,
            WeaponDefs = weaponDefs,
            WeaponMessages = weaponMessages,
            StockLoadouts = stockLoadouts,
            TurretDefs = turretDefs,
            Shakes = shakeDefs,
            HudFont = hudFont,
            ReticleTex = reticleTex,
            Textures = state.Textures,
            ZrdrPath = state.ZrdrPath,
        };
        // Ensured here, before the roster reads it, rather than left to the later mode-ship/
        // generator spawn sites: those cache-check the same field, so this only moves WHEN the
        // runtime is first built, not whether it is built twice.
        var surfaceVehicleRuntime = EnsureSurfaceVehicles(state);
        // The pool holds the session's live rosters (aircraft, world emplacements), and the hulls
        // are the rest of the engine's VehicleList. A turret gunner sees this pool and nothing
        // else, so without it a gun's candidate list is the aircraft half of the pool alone.
        projectiles.SurfaceVehicles = surfaceVehicleRuntime;
        // The same reasoning for the third pool: a gun standing beside a hostile mission structure
        // has nothing else to see it through.
        projectiles.Structures = state.WorldRuntime?.Destructibles;
        if (surfaceVehicleRuntime != null)
        {
            // The other direction: a hull's own gun scans through the pool and fires through it,
            // and resolves the weapon id its def authors against the same catalogue everything
            // else uses. Without both, hulls build unarmed (docs/org/aiPilot.md).
            surfaceVehicleRuntime.Projectiles = projectiles;
            surfaceVehicleRuntime.Weapons = weaponDefs;
            // A hull's gun voice hangs on the world's own sound node, the same home a world
            // emplacement's takes and for the same reason: the animation runtime memoizes the
            // lookups inside the subtree a hull is built from.
            surfaceVehicleRuntime.Voices = state.WorldRuntime?.Sounds is { } hullVoiceHome
                ? new GunVoiceHome(hullVoiceHome, state.Sounds, state.SoundDefs,
                    PlayerPositionsSnapshot)
                : null;
        }
        var worldBindings = new FlightWorldBindings
        {
            Ambience = _ambience,
            Projectiles = projectiles,
            HumanPositions = PlayerPositionsSnapshot,
            ChapterZrdrPath = SessionPaths.ChapterZrdr(_dataRoot, _spec.Chapter),
            MissionZrdrPath = state.MissionZrdrPath,
            Gamez = state.Gamez,
            WorldScene = state.WorldScene,
            WorldRuntime = state.WorldRuntime,
            WorldEffects = worldEffects,
            SurfaceVehicles = surfaceVehicleRuntime,
            TouchdownDefs = _worldEffectsFactory.TouchdownDefs,
            CrashProgram = state.CrashProgram,
            Sounds = state.Sounds,
            SoundDefs = state.SoundDefs,
            SoundGroups = state.SoundGroups,
            DebugCollision = state.DebugCollision,
            // Read through the field rather than captured by value: the rig is built after these
            // bindings, and a zone apply rewrites the band while the mission runs.
            FogRange = () => _weatherRig?.FogGlobals.Range ?? Vector2.Zero,
        };
        var humanBindings = new HumanRosterBindings
        {
            RigCount = _rigs.Count,
            MixGain = mixGain,
            PadAssignment = padAssignment,
            PauseState = _pauseState!,
            MenuInputFor = MenuInputFor,
            ExitsToMenu = _menuDriven,
            ExitSession = _exitSession,
            SpawnList = spawnList,
            SpawnBase = spawnBase,
            StuntZones = stuntZones,
            Race = race,
            VersusMatch = versus,
            Rigs = _rigs,
            InstantActionPlayerPlaneNode = iaOverride,
            InstantActionActive = iaRt != null,
            Coop = _spec.Coop,
            SmokeScreens = _smokeScreens,
        };
        var flightRoster = new FlightRoster(FlightRosterPolicy.From(_spec), _liveryResolver,
            _worldEffectsFactory, _worldRoot!, aircraftResources, worldBindings, humanBindings,
            flightStarts);
        var rosterBuild = flightRoster.BuildPlayers(_rigs);
        state.MeshInstances += rosterBuild.MeshInstances;
        state.What += rosterBuild.SummarySuffix;
        // One shared PauseState on every rig: any human pauses everybody, and only the pauser may
        // resume. The whole-window board covers every pane; single player uses the same path.
        var pauseState = _pauseState!;
        // Built before the boards so each of them knows whether it has a PREFERENCES door at all:
        // the Original sheet draws its strip either way and leaves the press a no-op, Built-in's
        // menu leaves the row off rather than offering one that does nothing.
        var pauseOptions = _pauseOptionsFactory?.Invoke();
        Action? preferences = pauseOptions == null
            ? null
            : () => OpenPauseOptions(pauseState.OwnerPlayerIndex);
        Control pauseBoard;
        if (BuildOriginalPauseBoard(pauseState, state.WorldRuntime) is { } sheet)
        {
            sheet.Restart = Rerun;
            sheet.Exit = _exitSession;
            sheet.Preferences = preferences;
            // Read at press time, not captured: the owner is whoever paused THIS time, and only
            // that player drives the cursor that reached this row.
            sheet.PhotoMode = () => EnterPhotoMode(pauseState.OwnerPlayerIndex);
            _originalPause = sheet;
            pauseBoard = sheet;
        }
        else
        {
            var builtIn = PauseBoard.Build(pauseState, exitsToMenu: _menuDriven, MenuInputFor);
            builtIn.Restart = Rerun;
            builtIn.Exit = _exitSession;
            builtIn.Preferences = preferences;
            // Read at press time, not captured: the owner is whoever paused THIS time, and only that
            // player drives the cursor that reached this row.
            builtIn.PhotoMode = () => EnterPhotoMode(pauseState.OwnerPlayerIndex);
            pauseBoard = builtIn;
        }

        _boards.Add(pauseBoard);
        _pauseBoard = pauseBoard;
        var pauseLayer = new CanvasLayer { Name = "pause_board", Layer = UI.Boards.HudLayers.Board };
        pauseLayer.AddChild(pauseBoard);
        _worldRoot!.AddChild(pauseLayer);
        if (pauseOptions != null)
        {
            _pauseOptions = pauseOptions;
            pauseOptions.Closed += ClosePauseOptions;
            _boards.Add(pauseOptions);
            var optionsLayer = new CanvasLayer { Name = "pause_options", Layer = UI.Boards.HudLayers.Board };
            optionsLayer.AddChild(pauseOptions);
            _worldRoot!.AddChild(optionsLayer);
        }
        // The per-pane stunt scoreboards are built with their rigs (HumanFlightAdapter), so they
        // are collected here rather than at a construction site of their own.
        foreach (var rig in _rigs)
        {
            if (rig.Controller?.Scoreboard is not StuntScoreboard scoreboard)
                continue;
            int owner = rig.Index;
            scoreboard.PhotoMode = () => EnterPhotoMode(owner);
            _boards.Add(scoreboard);
        }
        BuildCockpitPasses();

        // Damage lab in flight (F19): the panel --viewer hosts, bound to P1's real PlaneDamage
        // rather than visuals alone, so a dialled-in state drives the HUD and can then be flown.
        // Splitscreen binds P1 only: the panel is one overlay, not one per pane.
        if (_rigs.Count > 0 && _rigs[0].Controller is { Damage: not null } p1)
        {
            var p1Stats = StatsFor(iaOverride ?? PlaneRoster.PlaneFor(_spec, 0));
            _damageLab = new DamageLab(p1Stats,
                new FlightDamageTarget(p1, _rigs.Count > 1 ? "P1" : null), _spec.DamagePreset)
            {
                StartHidden = !_spec.DamageLab, // --damage opens it; a plain flight waits for F19
                RightAligned = true,            // the top-left corner is the flight HUD's
            };
            _worldRoot!.AddChild(_damageLab);
            Log.Info("flight", $"damage lab: {p1Stats.DestroyableParts.Count} part sliders on the flown plane's armor+HP{(_spec.DamageLab ? "" : " (hidden, F19)")}");
            state.What += _spec.DamageLab ? " + damage lab" : " + damage lab (F5)";
        }
        else if (_spec.DamageLab)
        {
            Log.Info("flight", $"damage lab: '{_spec.PlaneName}' has no destroyable_parts");
        }

        // --canopy-holes= gives struck glass from the spawn frame on. A scripted cockpit shot need
        // not wait on an AI burst and a 0.3 draw. Every human pane, a splitscreen canopy being per
        // pilot, and through the cue's own ledger, so a later round picks up where this left off.
        if (_spec.CanopyHoles is { } holes)
        {
            foreach (var rig in _rigs)
            {
                rig.Controller?.PresetCanopyHoles(holes);
            }
        }

        // The weapon lab in flight, bound to player 1's held aircraft inside a real chapter world.
        // ⚠ Keep it firing through the session's own fully-wired ProjectilePool, never a scene-less
        // pool of its own; --weapon-test is the parked-plane probe and never reaches here.
        if (_spec.WeaponLab && _rigs.Count > 0 && _rigs[0] is { Controller: { PlaneModel: not null } p1c } labRig)
        {
            // A soak run must never dry up: the lab exists to watch a weapon fire, not to manage
            // ammo. Explicit flags still win, --ammo=N caps the load on purpose.
            p1c.InfiniteAmmo = true;
            // --weapon-fire holds the real trigger, the one free flight pulls (decision 3), which
            // one follows the panel's bank, so the lab sets it rather than this call site.
            var lab = new UI.Labs.WeaponLab(p1c.PlaneModel, weaponDefs, p1c.Loadout, _spec.PlaneName,
                host: p1c, camera: labRig.Camera)
            {
                DebugShow = true,   // the lab IS the session now, the panel is why you launched it
                InitialWeapon = _spec.WeaponSelect,
                InitialMount = _spec.WeaponMount,
                AutoFireAtStart = _spec.WeaponFire,
                CycleFrames = _spec.WeaponCycle,
                DebugClickRequested = _spec.WeaponClick,
                DebugClick = _spec.WeaponClickAt,
                DebugClickAimOnly = _spec.WeaponClickAimOnly,
                DebugTarget = _spec.WeaponTarget,
                DebugSurface = _spec.WeaponSurface,
                StandoffAtStart = _spec.WeaponStandoff,
                FreeCameraAtStart = _spec.WeaponFreeCamera,
                CameraToggleFrames = _spec.WeaponCameraToggle,
            };
            _worldRoot!.AddChild(lab);
            // The lab is one overlay on one aircraft (like the damage lab), and its camera hand-off
            // takes that rig's camera, so in splitscreen it binds P1 and says so rather than
            // silently leaving the other panes' pilots without a panel they can see.
            if (_rigs.Count > 1)
            {
                Log.Info("weapons", $"weapon lab: {_rigs.Count} players, the lab binds P1's aircraft and P1's pane only; the other panes fly normally");
            }
            Log.Info("weapons", $"weapon lab: '{_spec.PlaneName}' held {(_spec.EmptyStage ? "on the empty stage" : $"in {_spec.Chapter}")}, firing through the session pool{(_spec.WeaponFire ? " (--weapon-fire: trigger held)" : "")}{(_spec.WeaponCycle > 0 ? $" (--weapon-cycle: a weapon every {_spec.WeaponCycle} frames)" : "")}");
            // The authored impact/destruction effects need the world-effects runtime, which is only
            // built when there IS a world program, say so rather than silently drawing stand-ins.
            if (state.WorldScene == null)
            {
                Log.Info("weapons", $"weapon lab: no world program on this stage, impacts fall back to the pool's stand-in burst and rockets fly without their FLYOUT body/trail");
            }
            state.What += " + weapon lab";
        }
        else if (_spec.WeaponLab)
        {
            Log.Info("weapons", $"weapon lab: no flight rig to host it (nothing was built to hold)");
        }

        // The race's shared results board, one ranked row per player over the whole window; R
        // rematches every plane through the session. Instant Action keeps the race for the run
        // HUD's placings alone and builds no board.
        var raceBoard = race == null ? null
            : RaceBoardFor(race, instantAction: iaRt != null,
                $"{_spec.Chapter}   ·   {PlaneRoster.Humanize(_spec.Scenario)}", exitsToMenu: _menuDriven,
                _pauseState!, MenuInputFor);
        if (race != null && raceBoard != null)
        {
            _race = race;
            raceBoard.Restart = () => RestartRace(race);
            raceBoard.Exit = _exitSession;
            // Player 1: a results board reads _inputFor(0), so its cursor is P1's whoever won.
            raceBoard.PhotoMode = () => EnterPhotoMode(0);
            _boards.Add(raceBoard);
            var boardLayer = new CanvasLayer { Name = "race_board", Layer = UI.Boards.HudLayers.Board };
            boardLayer.AddChild(raceBoard);
            _worldRoot!.AddChild(boardLayer);
            foreach (var rig in _rigs)
                if (rig.Controller != null)
                    rig.Controller.RestartRace = () => RestartRace(race);
            Log.Info("flight", $"stunt race: {_rigs.Count} pilots over {stuntZones!.TotalCount} danger zones, own progress + clock each, shared ranked board");
        }
        else if (race != null)
        {
            Log.Info("flight", $"stunt race: {_rigs.Count} pilots over {stuntZones!.TotalCount} danger zones, placings on each run HUD, no race board (the Instant Action wrap-up ends the run)");
        }

        // Dogfight (--vs): the match bookkeeping, fed by every rig's Downed report. A killer inside
        // the roster scores a kill. Anything else is a death with no killer and costs a point.
        // ⚠ Do not guard post-completion events here. The match ignores them, and rigs report facts.
        if (versus is { } match)
        {
            _versus = match;
            // Spawn rotation: a downed seat comes back on a point picked against the living field.
            // The fixed spawn can be camped at. The rotation's Rng is seeded from the master alone,
            // so no pick here draws from Rng.Spawn and shifts the launch spawn index.
            _versusSpawns = VersusSpawnRotation.For(spawnList, spawnBase, _rigs.Count,
                new Random(Rng.IntSeedFor(Rng.VersusSpawn)));
            // Who downed each seat last, which the rotation weighs heaviest: the Downed report
            // carries it, and the respawn that reads it happens seconds later.
            var lastKiller = new int?[_rigs.Count];
            foreach (var rig in _rigs)
                if (rig.Controller is { } pilot)
                {
                    int seat = rig.Index;
                    pilot.AutoRespawnAfter = VersusRespawnDelay; // crash cam, then back in, R skips
                    pilot.Match = match;                  // R-ownership gate: board-up ⇒ rematch
                    pilot.RestartMatch = () => RestartMatch(match);
                    if (_versusSpawns != null)
                        pilot.RespawnPlacement = () => VersusRespawn(seat, lastKiller[seat]);
                    pilot.Downed += (victim, killer) =>
                    {
                        if (victim >= 0 && victim < lastKiller.Length)
                            lastKiller[victim] = killer;
                        if (killer is int k && k >= 0 && k < match.PlayerCount)
                            match.RegisterKill(k, victim);
                        else
                            match.RegisterDeath(victim);
                    };
                }
            match.MatchCompleted += () => Log.Info("flight", $"dogfight: match complete, {string.Join(", ", match.Standings().Select(s => $"P{s.PlayerIndex + 1} {s.Score}pts {s.Kills}K/{s.Deaths}D (#{s.Rank})"))}");
            Log.Info("flight", $"dogfight: {_rigs.Count} pilots, {(match.KillTarget > 0 ? $"first to {match.KillTarget} points" : "no kill target")}, {(match.TimeLimit > 0f ? $"{match.TimeLimit / 60f:0.#} min limit" : "no time limit")}");

            // The match's shared results board: same construction as the race board above,
            // one CanvasLayer over the whole window (the match ends for everybody at once), R
            // routed back through this session via RestartMatch.
            var board = VersusBoard.Build(match, $"{_spec.Chapter}   ·   {PlaneRoster.Humanize(_spec.Scenario)}",
                exitsToMenu: _menuDriven, _pauseState!, MenuInputFor);
            board.Restart = () => RestartMatch(match);
            board.Exit = _exitSession;
            // Player 1, for the same reason the race board is: the cursor is _inputFor(0)'s.
            board.PhotoMode = () => EnterPhotoMode(0);
            _boards.Add(board);
            var boardLayer = new CanvasLayer { Name = "dogfight_board", Layer = UI.Boards.HudLayers.Board };
            boardLayer.AddChild(board);
            _worldRoot!.AddChild(boardLayer);
        }

        // --incoming: the incoming-fire test rig, a phantom shooter on every pilot's six, so both
        // cues and the shield are reachable with one player, no AI gunner needed.
        if (_spec.IncomingPass is float incomingPass)
        {
            var incoming = new IncomingFire(projectiles, weaponDefs, incomingPass, _spec.IncomingWeapon);
            foreach (var rig in _rigs)
                if (rig.Controller != null)
                    incoming.AddTarget(rig.Controller);
            _worldRoot!.AddChild(incoming);
            _incomingFire = incoming;
            Log.Info("weapons", $"--incoming: rounds {incomingPass:0.0} m off every player's track{(_spec.IncomingWeapon != null ? $" ({_spec.IncomingWeapon})" : " (their own gun)")}");
        }

        // The roster shares the session data the human field was built from, so later AI spawns
        // works from here on, at build or at any later sim step.
        _flightRoster = flightRoster;

        // A spent hull posts one line into every pane's stack, worded and coloured as that pane
        // reads it (docs/org/vehicleDamage.md). One flown into the world takes the notice instead.
        // ⚠ The roster hook too: an aircraft a wave releases never passes through the _rigs loop.
        void PostKillLine(FlightController victim, int victimId, int? killer)
        {
            if (!HudMessages.WordsKillLine(victim))
            {
                return;
            }

            foreach (var pane in _rigs)
            {
                if (pane.Controller is not { MessageStack: { } stack } viewer)
                {
                    continue;
                }
                // Dogfight words a death by seat, so the gate is the VICTIM being one. The decoded
                // post reads nothing off the killer, and an unattributed death (a mid-air, the
                // ground) still posts its line. An AI in a match keeps the decoded wording.
                if (_versus is { } m && victimId >= 0 && victimId < m.PlayerCount)
                {
                    int? seat = killer is int k && k >= 0 && k < m.PlayerCount ? k : null;
                    stack.Post(VersusHud.KillLine(seat, victimId),
                        HudMessages.SideOf(victim.Team, viewer.Team));
                    continue;
                }
                HudMessages.PostKill(stack, weaponMessages, victim, viewer.Team,
                    ReferenceEquals(victim, viewer), _pilotName);
            }
        }

        foreach (var rig in _rigs)
        {
            if (rig.Controller is { } human)
            {
                human.Downed += (victimId, killer) => PostKillLine(human, victimId, killer);
                // The crash notice is the local player's own announcement, so it lands in the
                // crashing pane's stack alone rather than in every pane's.
                human.GroundImpact += crashed =>
                {
                    if (crashed.MessageStack is { } own)
                    {
                        HudMessages.PostCrash(own, weaponMessages);
                    }
                };
            }
        }

        flightRoster.VehicleDowned += (victim, killer) =>
            PostKillLine(victim, victim.PlayerIndex, killer);

        // The voice dispatcher needs the world's WorldSounds (prewarmed
        // above) and the sound defs. Built before the --ai loop so spawns can register; the
        // players register as damage sources only (WA-HighDmg's broadcast trigger).
        if (state.WorldRuntime?.Sounds is { } worldSounds
            && state.SoundDefs is { } vDefs && state.SoundGroups is { } vGroups)
        {
            // The radio plays the streams the world's prewarm already decoded, so a callout survives
            // the sound archive's build scope closing exactly as a one-shot does.
            _radio = new MissionRadio(vDefs, vGroups, worldSounds.StreamFor);
            worldSounds.Radio = _radio;
            _worldRoot!.AddChild(_radio);
            var combatVoice = new CombatVoice(vDefs, vGroups, CombatVoice.LoadAccents(state.ZrdrPath));
            _aiVoice = new AiVoiceRuntime(combatVoice, worldSounds, _radio, Rng.NewSystemRandom(Rng.Ai));
            _worldRoot!.AddChild(_aiVoice); // its realtime tick; freed with the world subtree
            // WA-Turret: subscribed to the pool, not to a turret list, so the emplacements built
            // further down and every carried gunner report through one seam.
            _aiVoice.WatchTurrets(projectiles);
            foreach (var rig in _rigs)
            {
                if (rig.Controller is { } human)
                {
                    _aiVoice.RegisterPlayer(human);
                }
            }
        }
        // An anchored net rides its trailer target, so every follower built below takes a supplier
        // for the object its net names. The player is rig 0, anything else is a world node, and a
        // name that resolves to nothing leaves the net at its authored coordinates.
        var netTrailers = _netTrailers = new NetTrailerTargets(
            () => _rigs.Count > 0 && _rigs[0].Controller is { } trailedRig
                ? trailedRig.WorldPosition
                : null,
            name => worldBindings.WorldRuntime?.FindNodes(name) is { Count: > 0 } trailerHits
                ? trailerHits[0]
                : null);
        // Instant Action's actor build (director phase), at this exact point: the ace's spawn
        // draw follows the player's ChooseSpawnBase draw in the same stream, and the wingman
        // fan reads P1's built pose.
        if (_iaDirector is { } iaDirActors)
        {
            state.What += iaDirActors.BuildActors(new InstantActionDirector.ActorBuildInputs
            {
                Rigs = _rigs,
                ChapterZrdrPath = worldBindings.ChapterZrdrPath,
                MissionZrdrPath = state.MissionZrdrPath,
                ZrdrPath = state.ZrdrPath,
                MessagesPath = state.MessagesPath,
                SpawnList = spawnList,
                SpawnBase = spawnBase,
                LiveryResolver = _liveryResolver,
                NetTrailers = netTrailers,
                Spawn = flightRoster.SpawnAi,
                RegisterVoice = RegisterAiVoice,
            });
        }
        // The campaign's roster build, at the same point and for the same reason: every block is
        // placed against the built human field, and the leader pass names the player rig.
        if (_campaign is { } campaignRoster)
        {
            int grafted = 0;
            var surfaceVehicles = EnsureSurfaceVehicles(state);
            state.What += campaignRoster.BuildRoster(new CampaignDirector.RosterInputs
            {
                SpawnSurface = surfaceVehicles != null
                    ? (plan, pos, forward) => surfaceVehicles.Spawn(plan, pos, forward)
                    : null,
                ChapterZrdrPath = worldBindings.ChapterZrdrPath,
                MissionZrdrPath = state.MissionZrdrPath,
                ZrdrPath = state.ZrdrPath,
                MinAiActiveDist = MinAiActiveDist(),
                Player = () => _rigs.Count > 0 ? _rigs[0].Controller : null,
                PlayerAirframe = flightRoster.FlyingAirframeOf(0),
                NetTrailers = netTrailers,
                FindNodes = worldBindings.WorldRuntime is { } rosterWorld
                    ? name => rosterWorld.FindNodes(name)
                    : null,
                Spawn = (plan, pos, look, pilot) => flightRoster.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                AttachMarkers = state.WorldRuntime is { } markerWorld
                    && state.WorldScene is { } markerScene && state.Gamez is { } markerGamez
                    ? (block, rig) => grafted +=
                        RosterMarkers.Attach(markerGamez, markerScene, markerWorld, block, rig)
                    : null,
                RegisterVoice = RegisterAiVoice,
                Rng = Rng.NewSystemRandom(Rng.Ai),
            });
            // ⚠ The first bind ran before any roster rig existed, so a row whose approach node the
            // graft above has just created was dropped there. Re-bound here, and only when
            // something was grafted, so a mission that adds nothing keeps one bind and one log line.
            if (grafted > 0 && _landings != null && _cutscene != null
                && state.WorldRuntime is { } landingWorld && state.Landings is { } landingRows)
            {
                _landings.Bind(landingWorld, landingRows, _cutscene, () => _rigs);
                _ladder?.Bind(landingWorld, _cutscene, () => _rigs, state.Pickups);
            }
        }
        // After every roster build, because a staged prop's livery is the one its own aeroplane's
        // rig resolved and that rig does not exist earlier.
        PaintStagedAircraft(state);
        if (_spec.AiPlanes is { Count: > 0 } aiPlanes && _rigs.Count > 0
            && _rigs[0].Controller is { } lead)
        {
            // Without a net: ahead of P1 on its own spawn heading, fanned right/left, holding that
            // course. With one: on the net's first node, patrolling the graph. ⚠ The empty stage
            // anchors each side to the grid origin instead, so a count sweep repeats one geometry.
            var basis = lead.GlobalTransform.Basis;
            var fwd = -basis.Z;
            var right = basis.X;
            var anchors = _spec.EmptyStage ? SquadronAnchors(aiPlanes) : null;
            List<AiNet>? nets = null;
            bool netsTried = false;
            int fanIndex = 0;
            int spawnedTotal = 0;
            for (int i = 0; i < aiPlanes.Count; i++)
            {
                var entry = aiPlanes[i];
                string planeName = entry.Plane;
                string? aiDef = entry.Def;
                int? accentId = entry.Accent;
                AiNet? net = null;
                if (entry.Net != null)
                {
                    // The stage's built-in ring answers first, since --stage=empty has no chapter
                    // index to name and its name is reserved against every shipped one. A chapter
                    // net is still named the way it always was, on any stage.
                    net = EmptyStage.ResolveNet(entry.Net);
                    if (net == null && !netsTried)
                    {
                        netsTried = true;
                        try
                        {
                            nets = AiNets.Load(SessionPaths.ChapterZrdr(_dataRoot, _spec.Chapter));
                        }
                        catch (Exception e)
                        {
                            GD.PushWarning($"--ai: cannot read {_spec.Chapter}'s patrol nets: {e.Message}");
                        }
                    }
                    net ??= nets != null ? AiNets.Resolve(nets, entry.Net) : null;
                    if (net == null)
                        GD.PushWarning($"--ai: net '{entry.Net}' is neither the built-in " +
                                       $"'{EmptyStage.PatrolNetName}' ring nor a net in " +
                                       $"{_spec.Chapter}'s neindex; '{planeName}' spawns without a patrol");
                }
                for (int k = 0; k < entry.Count; k++)
                {
                    // Per squadron on the ring, per session otherwise, so a command line of
                    // one-plane entries keeps exactly the spread it had before n= existed.
                    int fi = anchors != null ? k : fanIndex;
                    fanIndex++;
                    float lateral = 60f * ((fi + 1) / 2) * (fi % 2 == 0 ? 1f : -1f);
                    if (net != null)
                    {
                        // Spawned on the net itself, so a scripted run sees it patrolling within
                        // seconds. ⚠ Take node positions off the follower, not the record, or an
                        // anchored net puts the plane where the ring is not.
                        var follower = new AiNetFollower(net, Rng.NewSystemRandom(Rng.Ai),
                            trailerTarget: netTrailers.For(net));
                        var pos = follower.NodePosition(0) + right * lateral;
                        var look = net.Nodes.Count > 1 ? follower.NodePosition(1) : pos + fwd;
                        var pilot = AiPilot.HoldingCourse(pos, look);
                        pilot.Patrol = follower;
                        var spawnedOnNet = flightRoster.SpawnAi(new AiSpawn(
                            planeName, pos, look, pilot, Team: entry.Team, AiDef: aiDef));
                        // The net's own volumes over the gates the assembler took from the airframe
                        // def, the same write a campaign net assignment makes. Without it a CLI
                        // plane flies the machine's decoded defaults whatever net it is given.
                        CampaignRosterPlan.ApplyVolumes(pilot.Machine, net.Volumes, MinAiActiveDist());
                        RegisterAiVoice(spawnedOnNet, accentId ?? AiStatsForSpawn(planeName, aiDef)?.AiAccentId);
                        ApplyAiHullPreset(spawnedOnNet);
                    }
                    else if (anchors != null)
                    {
                        var (anchor, facing) = anchors[i];
                        var pos = anchor + facing.Cross(Vector3.Up).Normalized() * lateral;
                        var look = pos + facing;
                        var spawnedOnRing = flightRoster.SpawnAi(new AiSpawn(planeName, pos, look,
                            AiPilot.HoldingCourse(pos, look), Team: entry.Team, AiDef: aiDef));
                        RegisterAiVoice(spawnedOnRing, accentId ?? AiStatsForSpawn(planeName, aiDef)?.AiAccentId);
                        ApplyAiHullPreset(spawnedOnRing);
                    }
                    else
                    {
                        var pos = lead.WorldPosition + fwd * 250f + right * lateral;
                        var spawnedAhead = flightRoster.SpawnAi(new AiSpawn(planeName, pos, pos + fwd,
                            AiPilot.HoldingCourse(pos, pos + fwd), Team: entry.Team, AiDef: aiDef));
                        RegisterAiVoice(spawnedAhead, accentId ?? AiStatsForSpawn(planeName, aiDef)?.AiAccentId);
                        ApplyAiHullPreset(spawnedAhead);
                    }
                    spawnedTotal++;
                }
            }
            state.What += $" + {spawnedTotal} AI";
        }

        // --zeppelins: the mission's zeppelin instances, placed at their authored pose and flown
        // along their nets as kinematic world nodes. ⚠ Build them before --generators below, so a
        // zeppelin generator's min_altitude gate reads the flown host's live Y from the first step.
        bool iaZeppelinRun = iaRt?.IsZeppelinRun ?? false;
        if (_spec.Zeppelins || iaZeppelinRun || _spec.Zep != null)
        {
            List<ZeppelinDef> zepDefs;
            try
            {
                zepDefs = Zeppelins.Load(state.MissionZrdrPath);
            }
            catch (IOException e)
            {
                Log.Info("world", $"zep: no zeppelins file for {_spec.Chapter}/{_spec.Mission}: {e.Message}");
                zepDefs = new List<ZeppelinDef>();
            }
            IReadOnlyList<AiNet> zepNets;
            int? zepTeamOverride = null;
            Vector3? zepSeat = null;
            if (_spec.Zep is { } graft)
            {
                (zepDefs, zepNets, zepSeat) = GraftedZeppelin(zepDefs, graft);
                zepTeamOverride = graft.Team;
            }
            else
            {
                zepNets = AiNets.Load(worldBindings.ChapterZrdrPath);
            }
            _zeppelins = new ZeppelinRuntime(zepDefs,
                name => worldBindings.WorldRuntime?.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null,
                zepNets, netTrailers.For,
                // The bootstrap has already run the start anims: a hull an SI script owns from
                // its first frame must not be placed at the record seat on top of it.
                host => worldBindings.WorldRuntime?.Motions.DrivesTransform(host) ?? false,
                zepTeamOverride, zepSeat);
            _worldRoot!.AddChild(_zeppelins);
            // F18: the multi-zone damage half, per-part pools over the world registry, the
            // survivor-count kill, and the DAMAGES_ZEPPELIN gate on the shared pool.
            if (worldBindings.WorldRuntime is { } zepRuntime)
            {
                _zeppelins.WireDamage(zepRuntime);
                if (_projectiles != null)
                {
                    _projectiles.WorldDamageGate = _zeppelins.GateWeaponDamage;
                }
            }
            // F19: the broadside cannons, real wep_28 rounds through the shared pool, the
            // authored deploy/retract anims, targets from the record. After WireDamage so
            // F18's cannon pools exist (a destroyed cannon thins the volley).
            if (_projectiles != null)
            {
                _zeppelins.WireCannons(_projectiles, weaponDefs);
            }
            // B14: the zeppelin sub-parts are the one thing that makes a structure selectable, and
            // the zeppelins are built AFTER the rigs, so the feed is bound here rather than in the
            // assembler. Every pane shares the one runtime; each fills its own list from it.
            var zepTargets = _zeppelins;
            flightRoster.SetTargetSubParts(into => zepTargets.CollectTargetParts(into));
            Log.Info("world", $"zep: {_zeppelins.LiveCount} of {zepDefs.Count} zeppelin(s) placed for {_spec.Chapter}/{_spec.Mission}");
            state.What += $" + {_zeppelins.LiveCount} zeppelin(s)";
        }

        // Instant Action's own zeppelin switch (the director's phase, between the --zeppelins and
        // --generators blocks so a held hull never feeds a generator's altitude gate). The switched
        // nodes feed the turret arm inside the emplacement block below.
        var iaZepTurretSwitch = _iaDirector != null && worldBindings.WorldRuntime is { } iaZepWorld
            ? _iaDirector.SwitchZeppelins(iaZepWorld, _zeppelins, _spec.Chapter)
            : new List<(Node3D Node, bool Objective, string Name)>();

        // --generators: the mission's egen enemy generators, spawning through the seam above and
        // loaded here because the drop rules need the built world. An Instant Action zeppelin run
        // asks for them itself: its generator is the only way an enemy reaches the air.
        if (_spec.Generators || iaZeppelinRun)
        {
            List<EnemyGeneratorDef> egenDefs;
            try
            {
                egenDefs = EnemyGenerators.Load(state.MissionZrdrPath);
            }
            catch (IOException e)
            {
                Log.Info("world", $"egen: no generator file for {_spec.Chapter}/{_spec.Mission}: {e.Message}");
                egenDefs = new List<EnemyGeneratorDef>();
            }
            var chapterNets = AiNets.Load(worldBindings.ChapterZrdrPath);
            // The parameter blocks a generator's vehicle.params names are mission data, not
            // campaign state: a launch resolves its block on any run that turns the generators on,
            // or the aircraft flies a CLI airframe with none of the block's authored fields.
            var generatorTemplates = CampaignRosterPlan.GeneratorTemplates(
                state.MissionZrdrPath, VehicleDefs.Load(state.ZrdrPath), chapterNets);
            float generatorActiveDist = MinAiActiveDist();
            var generatorSurface = EnsureSurfaceVehicles(state);

            // The template's own fields, applied the way the campaign roster applies them, then
            // its accent so a generated pilot is heard as the block the mission authored.
            LaunchedVehicle SpawnFromGenerator(EnemyGeneratorDef def, Vector3 pos, Vector3 look,
                AiPilot pilot)
            {
                // The decoded launch name: one counter across the mission's generators, so a
                // second launch off the same template is a distinct node rather than a rename.
                int ordinal = _generators?.LaunchOrdinal ?? 0;
                switch (CampaignRosterPlan.ResolveGeneratorLaunch(
                    generatorTemplates, def.VehicleParams, out var plan))
                {
                    case GeneratorLaunch.Empty:
                        // The decoded empty launch: a label naming no block builds nothing, and
                        // the runtime counts the launch anyway. Never an airframe in its place.
                        Log.Info("world", $"egen: '{def.Node}' params '{def.VehicleParams}' names no roster block: the launch builds nothing");
                        return default;
                    case GeneratorLaunch.Surface:
                        // A hull off a ship generator: never an airframe in its place. With no
                        // surface runtime on this stage the launch is the counted empty one.
                        var hull = plan!;
                        if (generatorSurface == null)
                        {
                            Log.Info("world", $"egen: '{def.Node}' params '{def.VehicleParams}' names the hull '{hull.Def}', which this stage cannot build: the launch builds nothing");
                            return default;
                        }
                        string hullName = EnemyGenerators.LaunchName(EnemyGenerators.LaunchBase(hull.Name), ordinal);
                        var hullLaunch = new LaunchedVehicle(null, generatorSurface.Spawn(hull, pos, look - pos, hullName));
                        _campaign?.RegisterGeneratorLaunch(hullName, hullLaunch, hull);
                        return hullLaunch;
                    case GeneratorLaunch.Airframe:
                        // ⚠ shippedSkins: a generated aircraft is the mission's enemy, so it
                        // keeps its own textures rather than the player militia's default.
                        return flightRoster.SpawnAi(new AiSpawn(
                            _spec.GeneratorsPlane, pos, look, pilot, ShippedSkins: true,
                            NodeName: EnemyGenerators.LaunchName(_spec.GeneratorsPlane, ordinal)));
                }
                var template = plan!;
                string launchName = EnemyGenerators.LaunchName(EnemyGenerators.LaunchBase(template.Name), ordinal);
                var launched = flightRoster.SpawnAi(CampaignRosterPlan.SpawnFor(template, pos, look, pilot, launchName));
                CampaignRosterPlan.ApplyPlan(pilot, template, generatorActiveDist);
                RegisterAiVoice(launched, template.AccentId);
                // The mission script counts and commands the launch by this name, so the campaign
                // roster must hold it or a DEDG over its group reads the group as empty.
                _campaign?.RegisterGeneratorLaunch(launchName, launched, template);
                return launched;
            }

            var wr = worldBindings.WorldRuntime;
            _generators = new AiGeneratorRuntime(egenDefs,
                wr == null ? null
                    : (name, scope) => wr.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null,
                chapterNets, _spec.GeneratorsPlane, SpawnFromGenerator,
                wr == null ? null : (name, host) => wr.PlayWithin(host, name, applyReset: false).Count,
                wr == null ? null : (name, host) => wr.StopWithin(host, name),
                netTrailers.For);
            _worldRoot!.AddChild(_generators);
            // Cutscene callback 800 credits through the runtime's host chain. Bound after the
            // ladder switch's last bind, which its chaining requires.
            if (wr != null)
            {
                _generators.BindCallbackHost(wr);
            }
            if (_campaign is { } campaignGenerators)
            {
                var wakeupCredits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var objective in campaignGenerators.Script.Objectives)
                {
                    if (objective.WakeupGenerator is { } wakeup)
                    {
                        wakeupCredits.TryGetValue(wakeup.Name, out int sum);
                        wakeupCredits[wakeup.Name] = sum + wakeup.Count;
                    }
                }
                // --wake-generators: the script's whole credit granted at build, the headless
                // stand-in for playing up to each WAKEUP_GENERATOR objective.
                if (_spec.WakeGenerators)
                {
                    foreach (var (host, credit) in wakeupCredits)
                    {
                        int fed = _generators.GrantWaveCapacity(host, credit);
                        Log.Info("world", $"egen: '{host}' woken by --wake-generators: +{credit} credit (granted {fed}, stand-in for the script's WAKEUP_GENERATOR)");
                    }
                }
            }
            // A dead zeppelin permanently disables its generator (the decoded rule; F18
            // supplies the death the B6 stub waited on).
            if (_zeppelins != null)
            {
                var generators = _generators;
                _zeppelins.ZeppelinKilled += node => generators.NotifyHostDied(node);
            }
            // A fixed installation (the submarine) has no destroyed flag of its own; its death is
            // its healthy node going inactive, which DamageAt now raises the same way.
            if (wr != null)
            {
                var generators = _generators;
                wr.DestructibleKilled += node => generators.NotifyHostDied(node);
            }
            Log.Info("world", $"egen: {_generators.LiveCount} of {egenDefs.Count} generator(s) live for {_spec.Chapter}/{_spec.Mission}, spawning '{_spec.GeneratorsPlane}'");
            state.What += $" + {_generators.LiveCount} generator(s)";
            OrderWaveAirframes(flightRoster, egenDefs, generatorTemplates);
        }

        // The zeppelin run's wave arm (the director's phase): the objective zeppelin's generator
        // releases the waves built inert above. ⚠ It can only run here, after the generator block:
        // starting wave 1 on that mode means crediting a generator that did not exist earlier.
        _iaDirector?.ArmZeppelinRun(_generators);

        // Instant Action's end conditions, lives, spectate and wrap-up board (the director's
        // phase): every signal already exists above, so it only routes them into the mission
        // runtime, and its boards parent under _worldRoot like every board this method builds.
        _iaDirector?.WireEndConditions(new InstantActionDirector.EndConditionInputs
        {
            StuntZones = stuntZones,
            Zeppelins = _zeppelins,
            Projectiles = _projectiles,
            WorldRoot = _worldRoot!,
            SpectatorCameras = _spectatorCameras,
            LockCandidates = LockCandidateAircraft,
            RespawnDelay = VersusRespawnDelay,
            ExitsToMenu = _menuDriven,
            RestartSession = _restartSession,
            ExitSession = _exitSession,
            PauseState = _pauseState!,
            MenuInputFor = MenuInputFor,
            EnterPhotoMode = EnterPhotoMode,
            RegisterBoard = _boards.Add,
            // Only the Original presentation has a wrap-up page to go to. Everywhere else, and on a
            // command-line launch with no menu behind it, the in-flight board takes the ending.
            WrapupToMenu = _menuDriven && _presentation == UI.Menu.PresentationId.Original
                ? _instantActionWrapup : null,
        });

        // World AA emplacements: the standalone ai.zrd family, placed against this chapter's built
        // world unconditionally, like the original's own placement pass. Shipped ACTIVATED decides
        // which are awake; --wake-turrets stands in for the mission script's WAKEUP_TURRETS.
        if (state.WorldRuntime is { } worldRt && turretDefs != null)
        {
            var placedRt = worldRt;
            // A world gun's voice hangs under the world's own sound node, never inside the subtree
            // it fires from: the animation runtime's node memoization holds only while nothing adds
            // to a world subtree at runtime. Null home leaves every gun silent, as --mute does.
            var gunVoices = placedRt.Sounds is { } soundHome
                ? new GunVoiceHome(soundHome, state.Sounds, state.SoundDefs, PlayerPositionsSnapshot)
                : null;
            _turretEmplacements = new TurretEmplacementRuntime(turretDefs, weaponDefs,
                (pattern, scope) => placedRt.FindNodes(pattern, scope), projectiles,
                placedRt.WorldRoot, gunVoices);
            // ⚠ Into the tree AFTER the zeppelin runtime: the physics tick follows tree order, so
            // this is what lets a slung mount read its ride's moved pose on a realtime clock.
            _worldRoot!.AddChild(_turretEmplacements);
            // The rest of the zeppelin record's team fan: its guns, which do not exist until here.
            _zeppelins?.FanTeamsOntoTurrets(_turretEmplacements);
            int awakeByData = _turretEmplacements.AwakeCount;
            // The zeppelin turret arm recorded above. ⚠ Run it BEFORE --wake-turrets, which stands
            // in for a mission script and therefore wins, the same order the original has.
            foreach (var (zepNode, objective, zepName) in iaZepTurretSwitch)
            {
                int touched = _turretEmplacements.SetActivatedUnder(zepNode, objective);
                if (touched > 0)
                {
                    string arm = objective ? "ACTIVATED with the objective" : "stowed with the hull";
                    Log.Info("flight", $"ia: zeppelin '{zepName}' turrets: {touched} emplacement(s) {arm}");
                }
            }
            int woken = _spec.WakeTurrets ? _turretEmplacements.WakeAll() : 0;
            string wokenTail = woken > 0 ? $", {woken} woken by --wake-turrets" : string.Empty;
            // ⚠ Alive as well as awake: a census of the wake state alone reads identically
            // whether the guns can fire or not.
            Log.Info("flight", $"turrets: {_turretEmplacements.Count} world emplacement(s) placed for {_spec.Chapter} ({awakeByData} awake by data, {_turretEmplacements.Count - awakeByData} dormant{wokenTail}); {_turretEmplacements.AwakeCount} awake and {_turretEmplacements.AliveCount} alive now");
            if (_turretEmplacements.Count > 0)
            {
                state.What += $" + {_turretEmplacements.Count} emplacement(s)";
            }
        }

        // The campaign objectives graph (D31): armed once every runtime a directive can touch is
        // up, which is why it sits after the emplacement block rather than with the other
        // directors. It builds no node of its own.
        _diagRuntime = state.WorldRuntime;
        // Loaded before the graph is armed rather than with the readouts below: a SET_HELP_LABEL
        // write reaches a marker-carrying aircraft through the director, which needs the table.
        var objectiveMessages = _campaign != null ? Messages.Load(state.MessagesPath) : null;
        _campaign?.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = state.WorldRuntime,
            Turrets = _turretEmplacements,
            Generators = _generators,
            Zeppelins = _zeppelins,
            SurfaceVehicles = _surfaceVehicles,
            // The campaign's danger-zone gates are chapter-world geometry, so the
            // tracker needs the built gamez to resolve its dzpathN subtrees.
            Gamez = state.Gamez,
            Strings = objectiveMessages,
            Sounds = state.WorldRuntime?.Sounds,
            Projectiles = _projectiles,
            ListenerPosition = () => _rigs.Count > 0 && _rigs[0].Controller is { } pilot
                ? pilot.WorldPosition
                : Vector3.Zero,
            PlayerAircraft = () => _rigs.Count > 0 ? _rigs[0].Controller : null,
            // P1's Danger Zone photograph, taken the way the stunt camera takes its own. It goes
            // through the pilot's posed eye, or, with no frames drawn, off the seat's pane.
            PlayerPane = landed => _rigs.Count != 0
                && (_rigs[0].Controller?.Photograph is { } eye && DangerZonePhotograph.Drawable
                    ? eye.Request(landed)
                    : PaneReadback.Request(_rigs[0].Viewport ?? (IsInsideTree() ? GetViewport() : null), landed)),
            Humans = HumanAircraft,
            Aircraft = AllAircraft,
            // The flight's Danger Zone praise (id 15), elected on P1's team. The original raises
            // it in the zone's completion routine, for the local player's vehicle alone.
            DangerZoneSpoken = () =>
            {
                if (_aiVoice is { } voice && _rigs.Count > 0 && _rigs[0].Controller is { } flown)
                {
                    voice.DangerZoneCompleted(flown);
                }
            },
            BeginSpectate = BeginCampaignSpectate,
            Rng = Rng.NewSystemRandom(Rng.Ai),
        });

        // The launch hook's CALLBACK codes, taken after the generator runtime's own bind so the
        // director sits ahead of it and the rest of the chain still answers everything else.
        if (state.WorldRuntime is { } callbackRuntime)
        {
            _campaign?.BindCallbackHost(callbackRuntime);
        }

        // F15 / --debug-targets: who is aiming at whom. Reads the live gunners through closures
        // rather than a snapshot, waves activate, AI planes spawn and emplacements die long
        // after this line runs. The roster list is reused, not rebuilt per frame.
        _worldRoot!.AddChild(new UI.Overlays.TargetingOverlay(
            () => _turretEmplacements?.Emplacements ?? Array.Empty<TurretController>(),
            AllAircraft)
        {
            DebugShow = _spec.DebugTargets,
        });

        // F17: kill P1's TargetSelection.Current through its own death path, the playtester's
        // escape hatch when a stray enemy blocks an objective chain. P1-only, the same precedent
        // F19/F51 set for a single-pane debug tool.
        _worldRoot!.AddChild(new UI.Overlays.DebugKillTarget(
            () => _rigs.Count > 0 ? _rigs[0].Controller : null,
            () => _diagRuntime));

        // F16 / --debug-markers: every live aircraft marked on every human pane's targeting HUD.
        // Session-level like F17, and read through a closure because a pane's HUD is built after
        // this line and a rig can lose its aircraft mid-session.
        _worldRoot!.AddChild(new UI.Overlays.DebugMarkerToggle(() =>
        {
            var huds = new List<Flight.Hud.TargetHud>();
            foreach (var rig in _rigs)
            {
                if (rig.Controller?.PilotHud.TargetHud is { } hud)
                {
                    huds.Add(hud);
                }
            }

            return huds;
        }));

        // The objectives readout, the mission-end fade and the objective-site feed: all campaign
        // only, the first two polling _campaign once Attach (above) has built it, the sites landing
        // on the player's target cycle.
        if (_campaign is { } campaign && objectiveMessages is { } objectiveStrings)
        {
            // One readout and one fade per rig, under that rig's own HudParent, so every pane
            // draws its own copy, the pattern every other per-rig HUD follows. The completion
            // mark's art is loaded once and shared across them, as the HUD font is.
            var objectiveMark = UI.Overlays.ObjectivesHud.LoadMark(
                Path.Combine(_dataRoot, "extracted", "rimage"));
            foreach (var rig in _rigs)
            {
                // ⚠ Not beside the Original pause sheet: that screen's own parchment already
                // carries the objectives, and two readouts over one pause is one too many.
                if (_originalPause == null)
                {
                    rig.HudParent.AddChild(UI.Overlays.ObjectivesHud.Build(
                        campaign, objectiveStrings, _pauseState!, objectiveMark));
                }

                rig.HudParent.AddChild(UI.Screens.MissionEndFade.Build(campaign));
            }

            // The mission clock running out posts its two notices into the same stack a kill line
            // lands in, every pane's. Every seat is flying the mission that just expired.
            if (campaign.Graph is { } timerGraph)
            {
                timerGraph.TimerExpired += () =>
                {
                    foreach (var rig in _rigs)
                    {
                        if (rig.Controller?.MessageStack is { } stack)
                        {
                            HudMessages.PostTimeExpired(stack, objectiveStrings);
                        }
                    }
                };
            }
            var sites = new ObjectiveSites(campaign, objectiveStrings,
                MissionTargets.Load(state.MissionZrdrPath,
                    SessionPaths.ChapterZrdr(_dataRoot, _spec.Chapter)),
                state.WorldRuntime);
            flightRoster.SetTargetObjectives(into => sites.Collect(into));
            // Verification breadcrumb: how many sites the mission starts with, split by the flag
            // that picks their cycle. A zero here and a populated objectives readout means the
            // target table, not the graph, is the problem.
            var offered = new List<AimCandidate>();
            sites.Collect(offered);
            int flagged = offered.Count(c => c.Source is ObjectiveSite { Objective: true });
            Log.Info("core", $"campaign: {flagged} objective site(s) and {offered.Count - flagged} other-target site(s) on the player's target cycles");
        }
        else if (BindsMissionTargetTable(_spec, _campaign != null, stuntZones != null,
                                         state.WorldRuntime != null, _rigs.Count))
        {
            // Instant Action, the multiplayer modes, and a campaign launch flying without a
            // director take the same site feed with no graph behind it. The table's own objective
            // and other_target keys are the whole answer (see BindsMissionTargetTable).
            var sites = new ObjectiveSites(Messages.Load(state.MessagesPath),
                MissionTargets.Load(state.MissionZrdrPath,
                    SessionPaths.ChapterZrdr(_dataRoot, _spec.Chapter)),
                state.WorldRuntime);
            flightRoster.SetTargetObjectives(into => sites.Collect(into));
            var modeSites = new List<AimCandidate>();
            sites.Collect(modeSites);
            int modeFlagged = modeSites.Count(c => c.Source is ObjectiveSite { Objective: true });
            Log.Info("core", $"{_spec.Chapter}/{_spec.Mission}: {modeFlagged} objective site(s) and {modeSites.Count - modeFlagged} other-target site(s) from the mission's own targets.zrd");
        }

        if (_rigs.Count > 1)
        {
            var flown = new List<string>(_rigs.Count);
            for (int pi = 0; pi < _rigs.Count; pi++)
                flown.Add($"P{pi + 1} '{iaOverride ?? PlaneRoster.PlaneFor(_spec, pi)}'");
            state.What += $" + splitscreen {string.Join(", ", flown)}";
        }
        else
        {
            state.What += $" + '{iaOverride ?? _spec.PlaneName}' flying";
        }
    }

    // Dresses each staged cutscene prop in the livery of the aeroplane it stands in for: the
    // scheme its own roster block's rig resolved, else the one its vehicle def authors
    // (Mech3/AircraftStage.cs, StandIns). ⚠ Nothing here draws a default pattern of its own, and
    // nothing touches the paint RNG, which --det pins for every rig in the session.
    private void PaintStagedAircraft(BuildState state)
    {
        if (state.Aircraft is not { } stage || stage.PaintableNodes.Count == 0)
        {
            return;
        }

        var painted = new List<string>();
        foreach (var (node, block, def) in AircraftStage.StandIns)
        {
            PaintScheme? scheme = null;
            // The rig's own resolved livery first: it is the aeroplane the shot is about, and a rig
            // wearing the shipped skins (an enemy militia) must leave the prop wearing them too.
            if (block != null && _campaign is { } director
                && director.Roster.TryGetValue(block, out var rig))
            {
                scheme = rig.Scheme;
            }
            else if (def != null)
            {
                scheme = _liveryResolver.DefScheme(state.ZrdrPath, def);
            }

            if (stage.Paint(node, scheme, _liveryResolver.Patterns) is { } painter)
            {
                painted.Add($"'{node}' {painter.Scheme.Label} ({painter.PaintedSkins} skin(s))");
            }
        }

        string what = painted.Count > 0
            ? string.Join(", ", painted)
            : "no stand-in resolved a livery";
        Log.Info("world", $"aircraft stage paint: {what}");
    }

    // --debug-objective=N: the scripted twin of flying whatever completes campaign objective N, so
    // a --screenshot can show a marked objectives line with nobody at the controls. Runs at build,
    // before the graph's first step, which is what lets that step complete it off its own
    // conditions rather than a mark being faked into the display.
    private void ApplyObjectiveOverride(BuildState state)
    {
        if (_spec.DebugObjective is not int number || _campaign is not { } campaign)
        {
            return;
        }

        bool armed = Tooling.ProbeRunner.ForceObjective(state.WorldRuntime, campaign, number);
        state.What += armed ? $" + forced OBJECTIVE{number}" : $" + OBJECTIVE{number} (not armed here)";
    }

    // --destroy=<name>: kill a named destructible at session build so a --screenshot captures its
    // destruction with nobody at the controls. Reuses the weapon-damage path, so DamageAt runs the
    // full death. A plane-less --freecam asks for a world-effects runtime here, gated on the flag
    // so a plain --freecam builds nothing extra.
    private void ApplyDestroyOverride(BuildState state)
    {
        if (_spec.DestroyName != null && state.WorldRuntime != null)
        {
            if (state.WorldScene != null)
            {
                _worldEffectsFactory.EnsureWorldEffects(state.Gamez, state.WorldScene, state.Textures, state.CrashProgram!, state.WorldRuntime);
            }
            int killed = Tooling.ProbeRunner.TriggerDestroy(state.WorldRuntime, _spec.DestroyName, out var destroyBounds);
            state.What += killed > 0 ? $" + destroyed {killed}× '{_spec.DestroyName}'"
                               : $" + destroy '{_spec.DestroyName}' (no match)";
            // Auto-frame the plane-less freecam on what it killed, unless the tester placed the
            // camera themselves (--pos/--direction), so a bare `--freecam --chapter=CX
            // --destroy=name --screenshot=x.png` is a complete, self-framing destruction shot.
            if (killed > 0 && _spectator != null && _spec.CamPos == null && _spec.LookAt == null
                && _spec.CamDir == null && destroyBounds.Size.LengthSquared() > 0f)
            {
                _spectator.Frame(destroyBounds);
            }
        }
        else if (_spec.DestroyName != null)
        {
            Log.Info("world", $"--destroy='{_spec.DestroyName}' ignored: no chapter world (pair it with --freecam/--fly + --chapter=)");
        }
    }

    // The "loaded ..." summary line and the per-pane/texture-census follow-ups, printed once the
    // whole build has finished.
    private void LogBuildSummary(BuildState state, Stopwatch sw)
    {
        Log.Info("world", $"loaded {state.What}: {state.Gamez.Nodes.Count} gamez nodes, {state.MeshInstances} mesh instances, {state.Colliders} colliders, {Mech3.SceneBuilder.ClampedSurfaceTotal} uv-clamped + {Mech3.SceneBuilder.EdgeClampedSurfaceTotal} edge-clamped surfaces, {sw.ElapsedMilliseconds} ms");
        if (_rigs.Count > 1)
            foreach (var rig in _rigs)
                Log.Info("flight", $"view P{rig.Index + 1}: layer {Mathf.Log(rig.VisualLayer) / Mathf.Log(2) + 1:0} cull 0x{rig.Camera.CullMask:X5}, sky={(rig.Horizon != null ? "own" : "none")} deck={(rig.Deck != null ? "own" : "none")} whiteout={(rig.Whiteout != null ? "own" : "none")}");
        // The authored-mip coverage, said out loud per chapter: a chapter never loads its whole
        // archive, so "installed N" alone cannot show whether a level was missed or simply unused.
        var tex = state.Textures;
        if (tex.AuthoredMipsAvailable > 0)
        {
            string refused = tex.AuthoredMipsRefused > 0 ? $", {tex.AuthoredMipsRefused} REFUSED" : "";
            Log.Info("world", $"[textures] authored mip levels: {(Mech3.TextureArchive.Mips == Mech3.TextureArchive.MipSource.Authored ? $"{tex.AuthoredMipsInstalled} installed on {tex.AuthoredMipTextures} texture(s), of {tex.AuthoredMipsAvailable} this archive ships{refused}" : $"off (--mips=generated); this archive ships {tex.AuthoredMipsAvailable}")}");
        }
        if (state.Textures.MissingTextures.Count > 0)
            Log.Info("world", $"[textures] {state.Textures.MissingTextures.Count} referenced texture(s) absent from this install: {string.Join(", ", state.Textures.MissingTextures)}");
    }

    // The post-build framing pass: subject framing for the static views, the freecam/anim-lab mesh
    // lab, the collider wireframe overlay and the node-name label layer.
    private void FinishFraming(BuildState state)
    {
        // Only the static views frame their subject; flight and the spectator camera (both
        // --freecam and --anim-lab) place their own eye (FrameCamera would yank the freecam back
        // to the world's AABB orbit).
        if (!_spec.Fly && !_spec.Freecam && !_spec.AnimLab)
            FrameCamera(state.NodeAabb);

        // Mesh lab on the selection (M), the freecam/anim-lab twin of the viewer's lab above. It
        // owns no subtree until M attaches it to whatever the shared selection has, and restores
        // that subtree exactly when M lets go, so it builds and changes nothing until then.
        if (_selection != null)
        {
            _worldRoot!.AddChild(new UI.Labs.MeshLab(_selection, _sun, _env, _camera)
            {
                DebugSpec = _spec.DebugMesh,
                // A scripted capture is about the geometry, not the panel over it.
                ShowPanel = !_captureDirector.Pending,
            });
        }

        // Collider wireframes (C). Built in the modes that observe a live world: it draws what the
        // collision build produced, and says so loudly when the mode built none rather than
        // rendering an empty overlay that reads as "nothing here is solid".
        if ((_spec.Freecam || _spec.AnimLab || _spec.Fly) && _plane != null)
        {
            var planeColliders = new List<(Node3D, PlaneCollider)>();
            foreach (var rig in _rigs)
            {
                // ⚠ Collider.Parts.Local is in the plane MODEL's parent frame, not the model's own:
                // the model root carries its own GameZ local transform, so drawing the boxes as its
                // children would apply that transform twice.
                if (rig.Controller is { Collider: { } airframe, PlaneModel: { } } controller)
                {
                    planeColliders.Add((controller, airframe));
                }
            }
            _worldRoot!.AddChild(new UI.Overlays.ColliderOverlay(_plane, BuildsCollision)
            {
                DebugShow = _spec.ShowColliders,
                Planes = planeColliders,
                // What each surface id resolves to on contact, asked of this session's own program
                // rather than listed: the overlay colours by the id a touch will select, and which
                // ids have a def of their own is whatever the bound program defines.
                ResolvedSurfaceIds = state.CrashProgram != null
                    ? EffectCatalogue.ResolvedSurfaceIds(state.CrashProgram)
                    : null,
            });
            Log.Info("world", $"collider overlay ready (C){(BuildsCollision ? "" : ", but this mode built NO collision; relaunch with --collision")}");

            // Colour-by-class overlay (H): same mode set as the collider overlay, since it reads
            // the same live world, a findable-targets view, not a collision one.
            _worldRoot!.AddChild(new UI.Overlays.ClassOverlay(_plane, state.Gamez, state.WorldRuntime)
            {
                DebugShow = _spec.ShowClassOverlay,
            });
            Log.Info("world", $"class overlay ready (H)");
        }
        else if (_spec.ForceCollision && _spec.WorldMode)
        {
            // The static viewer builds the bodies but binds no overlay: C there cycles the mesh
            // lab's cull override, and silently rebinding a lab key would be worse than saying so.
            Log.Info("world", $"--collision built the world's colliders, but the C overlay is not bound in this mode (C is the mesh lab's cull cycler), use --freecam to see them");
        }

        // Map-edge tile grid (--debug-tilegrid, flag-only). Gated on the extender rather than a
        // mode list, because "there is a continuation to colour" is exactly the precondition.
        if (_edgeExtender != null && _worldRoot != null && _plane != null)
        {
            _worldRoot.AddChild(new UI.Overlays.TileGridOverlay(_plane, _edgeExtender)
            {
                DebugShow = _spec.ShowTileGrid,
            });
            Log.Info("world", $"tile-grid overlay ready (--debug-tilegrid)");
        }
        else if (_spec.ShowTileGrid)
        {
            Log.Warn("world", $"--debug-tilegrid: this mode builds no map-edge continuation, so there is no tile grid to colour (it exists in --fly, --freecam, and a --sky-zone viewer)");
        }

        // Node-name labels (T), in both the viewer and flight, over the whole session subtree, so
        // the world and the aircraft are labelled alike. Off until pressed, and it builds nothing
        // until then. In splitscreen the selection follows P1 but the labels render in every pane.
        var flownPlanes = new List<Node3D>();
        foreach (var rig in _rigs)
            if (rig.Controller?.PlaneModel is { } flown)
                flownPlanes.Add(flown);
        _worldRoot!.AddChild(new UI.Overlays.NodeLabels(_worldRoot!, _rigs.Count > 0 ? _rigs[0].Camera : _camera)
        {
            InitialMode = _spec.DebugNames == null ? UI.Overlays.NodeLabels.Mode.Off : UI.Overlays.NodeLabels.ParseMode(_spec.DebugNames),
            // The flown aircraft sits metres from the camera while the world is hundreds of
            // metres away, so without this it wins every label slot. Empty in --viewer, where
            // the parked aircraft IS the subject.
            Deprioritise = flownPlanes,
        });
    }

    // The production ground sampler StartGrid probes its slots with: the world height under a point,
    // or null when the physics space answered nothing.
    // ⚠ Report null, never a fabricated height: the anchor is an authored, flyable point, and a
    // made-up correction would move a race field for no reason. This is the only place that knows
    // what an empty probe means. The warning below is a tripwire for a changed build order, not a
    // description of what happens today.
    private Func<Vector3, float?> GroundSampler()
    {
        // The probe column starts above the slot, so one fanned into a hillside finds the surface
        // rather than the terrain it is buried in. Generous on purpose: this runs a few times at
        // build, not per frame.
        const float ProbeAbove = 2000f;
        const float ProbeBelow = 20000f;

        bool warned = false;
        return at =>
        {
            if (GetWorld3D()?.DirectSpaceState is { } space)
            {
                // ⚠ World, NOT WorldAndAircraft: a placement pick stays blind to planes, or a later
                // caller probing while the field is airborne would stack a slot on an aircraft.
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    at + (Vector3.Up * ProbeAbove), at - (Vector3.Up * ProbeBelow),
                    CollisionLayers.World));
                if (hit.Count > 0)
                    return ((Vector3)hit["position"]).Y;
            }

            if (!warned)
            {
                warned = true;
                Log.Warn("flight", $"grid ground probe found nothing under a slot (probe {ProbeAbove:0}m up / {ProbeBelow:0}m down, mask=World). Measured, this does not happen at session build, so suspect the probe ran before the world stage was added rather than an empty map; the field keeps the spawn data's own altitude. Reported once per session.");
            }
            return null;
        };
    }

    // --ai-damage=: spends this AI plane's hull down to the ordered fraction at build, so a scripted
    // shot catches its injure_anims stages already up. Armour first and health second, in two exact
    // spends, because that is the order the take-hit flow spends them in; an AI airframe resolves no
    // zones, so both land in the whole pair the ladder reads. ⚠ Never drives the pool to zero, a
    // preset that kills would leave a wreck where the point is a flying, burning aircraft.
    private void ApplyAiHullPreset(FlightController? controller)
    {
        if (_spec.AiHullDamage is not { } fraction || controller?.Damage is not { } damage)
            return;
        if (damage.WholeArmor > 0f)
            damage.Apply("hull", 0f, damage.WholeArmor);
        float spend = damage.WholeHealth - (Mathf.Max(fraction, 0.01f) * damage.WholeHealthMax);
        if (spend > 0f)
            damage.Apply("hull", spend, 0f);
        controller.Visuals?.OnHullDamage(damage.SummaryHealthFraction);
        Log.Info("flight", $"ai damage preset: {controller.Name} hull at {damage.SummaryHealthFraction * 100f:0}% ({damage.WholeHealth:0.0}/{damage.WholeHealthMax:0})");
    }

    // Every player's own position: the flown aircraft where a rig has a bound FlightController,
    // that rig's camera otherwise. ⚠ One shared snapshot behind both PlayerPositions consumers, so
    // EXECUTION_BY_RANGE and PLAYER_RANGE cannot answer "who is nearest" differently.
    private IReadOnlyList<Vector3> PlayerPositionsSnapshot()
    {
        if (_rigs.Count == 0)
            return _camera is { } cam ? new[] { cam.GlobalPosition } : Array.Empty<Vector3>();
        var positions = new Vector3[_rigs.Count];
        for (int i = 0; i < _rigs.Count; i++)
            positions[i] = _rigs[i].Controller is { } fc
                ? fc.GlobalPosition
                : _rigs[i].Camera.GlobalPosition;
        return positions;
    }

    // Which human that key or button belongs to. A pad is bound to exactly one seat by
    // Pads.AssignPads, and the keyboard is P1's alone (HumanFlightAdapter); an unmatched device is
    // the scripted player's, so a skip always has a skipper to name rather than a hole.
    private int SkipperIndex(InputEvent @event)
    {
        foreach (var rig in _rigs)
        {
            if (rig.Controller is not { IsHumanPiloted: true } pilot)
            {
                continue;
            }

            if (@event is InputEventJoypadButton pad
                ? pilot.PadDevices != null && System.Array.IndexOf(pilot.PadDevices, pad.Device) >= 0
                : pilot.UseKeyboard)
            {
                return rig.Index;
            }
        }

        return 0;
    }

    // Whether any human pilot is flying one of the two first-person views, the answer the anim
    // data's PLAYER_1ST_PERSON condition gets. Read per call, since the cycle key changes it
    // mid-session; false with no rigs bound (every non-flight mode, and the bootstrap passes).
    private bool AnyPilotFirstPerson()
    {
        for (int i = 0; i < _rigs.Count; i++)
            if (_rigs[i].Controller is { FirstPersonView: true })
                return true;
        return false;
    }

    // Whether the pilot this shooter id names is flying the full Cockpit view, which draws no
    // muzzle flash on that pilot's own guns. Keyed on the controller's own PlayerIndex, the id
    // every round is stamped with, so another aeroplane's flash is untouched.
    private bool PilotInCockpitView(int shooterId)
    {
        for (int i = 0; i < _rigs.Count; i++)
            if (_rigs[i].Controller is { CockpitView: true } c && c.PlayerIndex == shooterId)
                return true;
        return false;
    }

    // Creates this session's PlayerRigs, one per rendered view. One player keeps the main-viewport
    // camera and the default visual layers, so that render path is unchanged. Two or more build the
    // SplitScreen pane rig, each pane culling every other player's private sky/deck/puff layer.
    private void BuildRigs(int count)
    {
        // Photo mode belongs to the rigs and boards about to be replaced: leaving it engaged would
        // point a camera at a freed aircraft, and the old boards would linger in the suspend list.
        ExitPhotoMode();
        _boards.Clear();
        _rigs.Clear();
        _split = null;
        if (count <= 1)
        {
            _camera.Current = true;
            // ⚠ Reopen the whole zone band before this session's first frame. The main camera is
            // the Launcher's and outlives the session, so it arrives carrying the last flight's
            // gate, and WeatherRig.Tick only ever NARROWS the band.
            _camera.CullMask = SplitScreen.PaneCullMask(Mech3.ZoneGate.OpenCullMask(_camera.CullMask));
            _rigs.Add(new PlayerRig { Index = 0, Camera = _camera, HudParent = _worldRoot!, VisualLayer = 0 });
            return;
        }

        _camera.Current = false; // the panes render the world now; nothing draws the main viewport's 3D
        _split = SplitScreen.Build(count, GetViewport());
        _worldRoot!.AddChild(_split);
        for (int i = 0; i < count; i++)
        {
            var view = _split.Views[i];
            var camera = new Camera3D
            {
                Name = $"camera{i + 1}",
                Fov = CameraController.ExternalFovDeg,
                Far = _camera.Far,
                CullMask = SplitScreen.PlayerCullMask(i),
                Current = true,
            };
            view.AddChild(camera);
            _rigs.Add(new PlayerRig
            {
                Index = i,
                Camera = camera,
                Viewport = view,
                HudParent = view,
                VisualLayer = SplitScreen.PlayerVisualLayer(i),
            });
        }
        Log.Info("flight", $"splitscreen: {count} panes sharing one world ({SplitScreen.LayoutName(count, GetViewport().GetVisibleRect().Size)})");
    }

    // One interior render pass per rig, on that player's own HUD parent, so splitscreen gets a
    // pass per pane rather than one for the window (--no-cockpit-pass opts out). Built after the rigs, since
    // the interior it moves is the plane build's and the sun and environment it copies are the
    // session's. ⚠ An airframe swap rebuilds the interior and leaves this pass holding the old
    // node; the prototype hides itself rather than drawing a freed one (CockpitOverlay.Sync).
    private void BuildCockpitPasses()
    {
        if (!_spec.CockpitPass)
        {
            return;
        }
        foreach (var rig in _rigs)
        {
            if (rig.Controller is not { CockpitInterior: { } interior } controller)
                continue;
            var overlay = Flight.Hud.CockpitOverlay.Build(rig.HudParent, interior, _sun, _env);
            controller.CockpitPass = overlay;
            // The overlay's cloned sun/env carry the zone live at its build; registering them
            // keeps a later mid-flight zone change reaching the interior pass too. Both modes,
            // since the faithful path's aircraft light moves per zone as well.
            if (overlay?.Sun != null)
                _weatherRig?.RegisterExtraLighting(overlay.Sun, overlay.Env);
        }
        Log.Info("flight", $"cockpit: interior drawn in its own pass at the origin for {_rigs.Count} rig(s)");
    }

    // Gives every rig a cloudlayer deck to anchor under its own camera: rig 0 takes the world's
    // deck, the rest get copies on their player's visual layer. ⚠ Re-apply the instance uniforms
    // from the source; they are RenderingServer state and Duplicate drops them.
    private void AssignCloudDecks(Node3D deck)
    {
        _rigs[0].Deck = deck;
        if (_rigs[0].VisualLayer != 0)
            SplitScreen.SetVisualLayer(deck, _rigs[0].VisualLayer);
        var parent = deck.GetParent();
        for (int i = 1; i < _rigs.Count; i++)
        {
            var copy = (Node3D)deck.Duplicate();
            copy.Name = $"cloud_deck{i + 1}";
            CopyInstanceShaderParams(deck, copy);
            SplitScreen.SetVisualLayer(copy, _rigs[i].VisualLayer);
            parent.AddChild(copy);
            _rigs[i].Deck = copy;
        }
    }

    // One menu reader per player, bound the way that player's plane is bound: player 1 also has the
    // keyboard, and a session with no per-player split reads every connected pad (null).
    private UI.Screens.MenuInput[] BuildMenuInputs(int[][]? padAssignment)
    {
        var inputs = new UI.Screens.MenuInput[Math.Max(1, _rigs.Count)];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new UI.Screens.MenuInput { Keyboard = i == 0, Pads = padAssignment?[i] };
        return inputs;
    }

    // The reader a board menu drives its cursor from. An owner outside the roster (a board that
    // named no player) falls back to player 1, who always exists.
    private UI.Screens.MenuInput MenuInputFor(int playerIndex)
    {
        var inputs = _menuInputs ??= BuildMenuInputs(null);
        return playerIndex >= 0 && playerIndex < inputs.Length ? inputs[playerIndex] : inputs[0];
    }

    // The Original presentation's pause sheet, or null where it does not apply: the Built-in
    // presentation, a mode the original authors no dialog for, or an extraction the sheet cannot be
    // read out of. Falling back to the Built-in board is what keeps a pause always available.
    private UI.Menu.Original.OriginalPauseBoard? BuildOriginalPauseBoard(
        Flight.Modes.PauseState pauseState, AnimRuntime? runtime)
    {
        if (_presentation != UI.Menu.PresentationId.Original)
        {
            return null;
        }

        if (_campaign is not { } campaign)
        {
            return BuildInstantActionPauseBoard(pauseState);
        }

        var (chapterNumber, missionNumber) = campaign.Address;
        var sheet = UI.Boards.PauseSheet.Load(
            _zrdrPath, _messagesPath,
            UI.Menu.EscapeDialog.CampaignKey(chapterNumber, missionNumber), instantAction: false);
        if (sheet == null)
        {
            Log.Warn("ui", $"pause: no escape.zrd sheet for C{chapterNumber}/M0{missionNumber}");
            return null;
        }

        var objectives = ReadPauseObjectives(campaign);
        return UI.Menu.Original.OriginalPauseBoard.Build(
            pauseState, MenuInputFor, _dataRoot, sheet,
            () => PauseReadout(sheet, campaign, objectives, pauseState, runtime));
    }

    // An Instant Action sortie's own sheet: ia_escape.zrd's blackboard for the sortie's chapter and
    // mission type, which carries no map, memento or parchment and so needs no readout. Free flight
    // and the dogfight are modes of ours that no shipped dialog describes, so they keep the Built-in
    // board, the same split the load screen makes (docs/org/pause-screen.md).
    private UI.Menu.Original.OriginalPauseBoard? BuildInstantActionPauseBoard(Flight.Modes.PauseState pauseState)
    {
        if (_iaDirector?.Runtime is not { } ia
            || UI.Screens.LoadScreens.LetterFor(ia.Def.MissionType) is not { } letter)
        {
            return null;
        }

        string key = UI.Menu.EscapeDialog.InstantActionKey(
            CampaignSequence.ChapterNumber(_spec.Chapter), letter);
        var sheet = UI.Boards.PauseSheet.Load(_zrdrPath, _messagesPath, key, instantAction: true);
        if (sheet == null)
        {
            Log.Warn("ui", $"pause: no ia_escape.zrd sheet for {_spec.Chapter} {ia.Def.MissionType}");
            return null;
        }

        Log.Info("ui", $"pause: {_spec.Chapter} {ia.Def.MissionType} draws {sheet.State.Key}");
        return UI.Menu.Original.OriginalPauseBoard.Build(
            pauseState, MenuInputFor, _dataRoot, sheet, () => UI.Boards.PauseReadout.Empty);
    }

    // The parchment's own row order, which is the briefing's: every keyed IDENTITY by priority.
    // The dialog's script indexes THIS list, so the graph's rows cannot stand in for it.
    private IReadOnlyList<UI.Menu.BriefingObjective> ReadPauseObjectives(CampaignDirector campaign)
    {
        try
        {
            return UI.Menu.BriefingObjectives.Load(
                Zrdr.LoadFile(campaign.MissionZrdrPath, "objectives.json"),
                Messages.Load(_messagesPath));
        }
        catch (Exception e) when (
            e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return Array.Empty<UI.Menu.BriefingObjective>();
        }
    }

    private UI.Boards.PauseReadout PauseReadout(
        UI.Boards.PauseSheet sheet,
        CampaignDirector campaign,
        IReadOnlyList<UI.Menu.BriefingObjective> objectives,
        Flight.Modes.PauseState pauseState,
        AnimRuntime? runtime)
    {
        var graph = campaign.Graph;
        var rows = UI.Boards.PauseReadout.Rows(objectives, n => graph?.CompletedOf(n) ?? false);

        var icons = new List<UI.Boards.PauseWorldIcon>();
        if (RigOf(pauseState.OwnerPlayerIndex)?.Controller is { } own)
        {
            var forward = -own.GlobalTransform.Basis.Z;
            if (UI.Boards.PauseReadout.Icon(
                sheet.Shared.OwnShip, own.GlobalPosition.X, own.GlobalPosition.Z,
                forward.X, forward.Z) is { } ship)
            {
                icons.Add(ship);
            }
        }

        // The original looks its own zeppelin up by name and draws nothing when the mission has
        // none, which is the same answer an empty find gives here. Its icon turns with the hull,
        // which is what the reference stills show: the art is drawn along the course flown.
        foreach (var hull in runtime?.FindNodes(PirateZepNode) ?? Array.Empty<Node3D>())
        {
            var nose = -hull.GlobalTransform.Basis.Z;
            if (UI.Boards.PauseReadout.Icon(
                sheet.Shared.MyZep, hull.GlobalPosition.X, hull.GlobalPosition.Z,
                nose.X, nose.Z) is { } zeppelin)
            {
                icons.Add(zeppelin);
            }

            break;
        }

        // Where each icon landed, one line per pause. A pose outside the dialog's own world window
        // draws nothing at all, and the sheet that results cannot be told from one whose lookup
        // found nothing, so the two are separated here rather than at the controls.
        foreach (var icon in icons)
        {
            string where = sheet.State.Map is { } chart
                && chart.TryProject(icon.WorldX, icon.WorldZ, out _) ? "on" : "off";
            Log.Info("ui", $"pause icon {icon.Bitmap} at ({icon.WorldX:0}, {icon.WorldZ:0}) {where} the chart");
        }

        return new UI.Boards.PauseReadout(rows, campaign.Memento, icons);
    }

    private Flight.Camera.PlayerRig? RigOf(int playerIndex)
    {
        foreach (var rig in _rigs)
        {
            if (rig.Index == playerIndex)
            {
                return rig;
            }
        }

        return _rigs.Count > 0 ? _rigs[0] : null;
    }

    // A board menu's Restart item. An Instant Action or campaign mission is REBUILT by the
    // Launcher, because its opposition and objective state live in the world and nothing here can
    // put them back; every other mode reruns in place, the race and the match through their own
    // bookkeeping and anything else per-plane.
    private void Rerun()
    {
        if (_iaDirector != null || _campaign != null)
        {
            _restartSession();
            return;
        }
        if (_race is { } race)
        {
            RestartRace(race);
            return;
        }
        if (_versus is { } match)
        {
            RestartMatch(match);
            return;
        }
        foreach (var rig in _rigs)
            rig.Controller?.Rerun();
    }

    // Rematch from the shared race board (R): every player's zones, clock and placing cleared, then
    // every plane back to its own spawn. The session owns the planes, so the restart lands here
    // rather than in the FlightController that read the button.
    private void RestartRace(StuntRace race)
    {
        Log.Info("flight", $"stunt race: rematch, fresh clocks and zones for every pilot");
        race.Restart();
        foreach (var rig in _rigs)
        {
            // The race owns every pilot's zones and clock, but each pane's own camera is the
            // controller's, so the rematch clears the photographs seat by seat.
            rig.Controller?.StuntShots?.Reset();
            rig.Controller?.Respawn();
        }
    }

    // Rematch from the dogfight results board (R): every score and the clock reset, then every
    // plane back to its own spawn. Mirrors RestartRace exactly.
    private void RestartMatch(VersusMatch match)
    {
        Log.Info("flight", $"dogfight: rematch, scores and clock reset for every pilot");
        match.Restart();
        // A rematch is a fresh round, so it opens on the opening spawns rather than on wherever
        // the last round's rotation had left each seat.
        _versusSpawns?.Restart();
        foreach (var rig in _rigs)
            rig.Controller?.Respawn();
    }

    // Where a downed dogfight seat comes back, the rotation's pick against the field as it stands
    // at the respawn. A seat still on its own crash camera neither holds a point nor pulls one
    // away. Null with no rotation built, which leaves the seat on the pose it was given.
    private (Vector3 Pos, Vector3 LookAt)? VersusRespawn(int seat, int? killer)
    {
        if (_versusSpawns is not { } rotation)
            return null;
        var field = new Vector3?[_rigs.Count];
        for (int i = 0; i < _rigs.Count; i++)
            field[i] = _rigs[i].Controller is { Crashed: false, Inert: false } flying
                ? flying.GlobalPosition : null;
        var point = rotation.Choose(seat, field, killer);
        string list = _spawnPicker.ScenarioOverride ?? _spec.Scenario;
        Log.Info("flight", $"dogfight: P{seat + 1} respawns on {list} #{rotation.IndexOf(seat)} of {rotation.PointCount}{(killer is { } k ? $", downed by P{k + 1}" : "")}");
        return (point.Position, point.Position + point.Forward);
    }

    // Frames the parked plane in the orbit view. ⚠ --lookat is a POINT and is used verbatim;
    // --direction is only an aim, so a pivot is synthesized on the ray. Either way the orbit still
    // orbits the subject.
    private void FrameCamera(Aabb? subject = null)
    {
        // ⚠ Use the --node= stage's own box, measured before the labs joined the subtree: MeshLab
        // parks empty overlay meshes at the session origin, and a merge over the live tree would
        // stretch a distant subtree's box back to them.
        var aabb = subject ?? OrbitCamera.MergedAabb(_plane!);
        var pivot = _spec.LookAt;
        if (pivot == null && _spec.CamDir is { } dir)
        {
            if (_spec.CamPos is { } eye)
            {
                float ahead = Mathf.Max((aabb.GetCenter() - eye).Dot(dir), MinOrbitRadius);
                pivot = eye + dir * ahead;
                Log.Info("core", $"orbit pivot from --direction: --lookat={Tooling.CaptureDirector.Vec3Arg(pivot.Value)} radius={ahead:0.###}");
            }
            else
            {
                // No eye: keep the AABB pivot and the framing distance Frame derives, placing the
                // eye along the aim.
                _orbit.Pitch = Mathf.Asin(Mathf.Clamp(-dir.Y, -1f, 1f));
                _orbit.Yaw = Mathf.Atan2(-dir.X, -dir.Z);
                Log.Info("core", $"orbit pivot from --direction: subject centre, eye swung to the aim");
            }
        }
        _orbit.Frame(aabb, _spec.CamPos, pivot);
    }

    // The mission-script host's airframe swap (callback codes 965 to 967). The episode owner's rig,
    // which is the human whose trigger started the episode: the original has one player vehicle and
    // the codes name it, and with a field the aeroplane it names is the one that earned the swap.
    // An episode nobody claimed is the scripted player's, so a 1P mission swaps exactly what it
    // swapped before. A failed swap is reported rather than thrown: the player keeps the aircraft
    // the exception left them without, and the mission goes on.
    private AirframeSwapResult SwapPlayerAirframe(AirframeSwapOrder order)
    {
        if (_flightRoster == null || _rigs.Count == 0)
        {
            return default;
        }

        try
        {
            return _flightRoster.RunSwap(order.Owner ?? _rigs[0], order,
                AirframeHandover.Resolves(_spec.Chapter, _spec.Mission));
        }
        catch (Exception e)
        {
            GD.PushWarning($"airframe swap to '{order.Airframe.PlaneNode}' failed: {e.Message}");
            return default;
        }
    }

    // --debug-spectate: build the whole session as it would be flown, then take every human out of
    // it, so the AI can be watched with nobody provoking it. Each human aircraft goes Held and
    // Inert, and its pane takes a SpectatorCamera following the first AI aircraft.
    // ⚠ Run this AFTER BuildFlightRigs: the wingman fan, the ace's spawn draw and wave 1's
    // placement all read the player's position, so removing the player earlier moves what is
    // being watched. The mission's own end conditions are untouched.
    private void ApplyDebugSpectate()
    {
        if (!_spec.DebugSpectate)
        {
            return;
        }
        FlightController? follow = null;
        foreach (var ai in AiPlanes)
        {
            if (ai is { InPlay: true })
            {
                follow = ai;
                break;
            }
        }
        foreach (var rig in _rigs)
        {
            if (rig.Controller is not { } pilot)
            {
                continue;
            }
            var eye = pilot.WorldPosition + Vector3.Up * 30f;
            pilot.Held = true;
            pilot.Inert = true;
            pilot.CameraOwned = true;   // The controller writes this pane's camera no more.
            // No hand-back leg: the mode holds the pane for the rest of the session.
            pilot.SetViewedFromOutside(true);
            // The cockpit instruments belong to an aircraft nobody is flying; the marker HUD is a
            // sibling on the same canvas and stays, which is the whole point of the mode.
            pilot.PilotHud.SetInstrumentsVisible(false);
            var spectator = new SpectatorCamera(rig.Camera, eye,
                follow != null ? follow.WorldPosition : eye - rig.Camera.Basis.Z)
            {
                ShowReadout = _rigs.Count == 1,   // one pane, so the freecam readout has room
                LockCandidates = LockCandidateAircraft,
            };
            _worldRoot!.AddChild(spectator);
            if (follow != null)
            {
                spectator.FollowNode(follow);
            }
        }
        Log.Info("flight", $"--debug-spectate: {_rigs.Count} human(s) pinned, inert and untargetable; {(follow != null ? $"camera following {follow.Name}" : "camera free at the spawn")} ({AiPlanes.Count} AI aircraft flying)");
    }

    // A co-op campaign human whose aircraft is lost while the others fly on uses the same
    // hand-off Instant Action's last life runs, with the campaign's own loss rule deciding when.
    private void BeginCampaignSpectate(FlightController pilot)
    {
        foreach (var rig in _rigs)
        {
            if (!ReferenceEquals(rig.Controller, pilot))
            {
                continue;
            }

            SpectateHandoff.Begin(rig, _rigs, _worldRoot!, LockCandidateAircraft,
                _spectatorCameras, out var follow);
            Log.Info("core", $"campaign: P{rig.Index + 1}'s pane is spectating{(follow != null ? $", following P{follow.PlayerIndex + 1}" : " from the crash camera")}");
            return;
        }
    }

    /// <summary>What a <see cref="SpectatorCamera"/>'s target key may lock onto: every aircraft
    /// still in play, AI and human alike. Rebuilt per press, so a plane that has since been shot
    /// down or stood down (<see cref="FlightController.InPlay"/> covers crashed and inert both)
    /// drops out without anyone pruning a list. Empty on a stage with no aircraft, which leaves
    /// the key inert rather than special-cased.</summary>
    private IReadOnlyList<Node3D> LockCandidateAircraft()
    {
        _lockCandidates.Clear();
        foreach (var ai in AiPlanes)
            if (ai is { InPlay: true })
                _lockCandidates.Add(ai);
        foreach (var rig in _rigs)
            if (rig.Controller is { InPlay: true } pilot)
                _lockCandidates.Add(pilot);
        return _lockCandidates;
    }

    /// <summary>Take <paramref name="playerIndex"/>'s pane to photo mode, chosen from whichever
    /// board is up. The halt is NEVER dropped: the world stays the still frame the board froze, so
    /// this only moves an eye. Idempotent, so a second press of the row while already in it does
    /// nothing rather than stacking cameras.</summary>
    private void EnterPhotoMode(int playerIndex)
    {
        if (_photoHud != null)
            return;
        PlayerRig? found = null;
        foreach (var r in _rigs)
            if (r.Index == playerIndex)
                found = r;
        if (found is not { Controller: { } pilot } rig)
            return;
        SuspendBoards();
        pilot.BeginPhotoMode();
        pilot.CameraOwned = true;   // The controller writes this pane's camera no more.
        pilot.SetViewedFromOutside(true);
        pilot.SetPilotHudVisible(false);
        var eye = rig.Camera.Position;
        _photoCamera = new SpectatorCamera(rig.Camera, eye, eye - rig.Camera.Basis.Z,
            pilot.PadDevices, pilot.UseKeyboard)
        {
            Name = "photo_mode_camera",
            ShowReadout = false,   // the hint line is this mode's only furniture
            LockCandidates = LockCandidateAircraft,
        };
        _worldRoot!.AddChild(_photoCamera);
        // ⚠ Lock onto this player's OWN aircraft, wreck included, and nothing else. FollowNode
        // seeds the orbit from the current eye, so following what the camera is already looking at
        // never jumps, while locking any other plane would keep the offset and teleport the view.
        _photoCamera.FollowNode(pilot);
        _photoPilot = pilot;
        _photoHud = UI.Overlays.PhotoModeHud.Build(pilot.PadDevices, pilot.UseKeyboard);
        _photoHud.Exit += ExitPhotoMode;
        _worldRoot!.AddChild(_photoHud);
        Log.Info("flight", $"photo mode: P{playerIndex + 1}'s pane, over the frame the board froze");
    }

    /// <summary>Escape (or pad B) out of photo mode: the board comes back and the pilot's HUD with
    /// it. The camera is left where it was flown to, so the board returns over the frame just
    /// composed and re-entering continues from the same eye.</summary>
    private void ExitPhotoMode()
    {
        if (_photoHud == null)
            return;
        _photoHud.QueueFree();
        _photoHud = null;
        _photoCamera?.QueueFree();
        _photoCamera = null;
        if (_photoPilot is { } pilot && IsInstanceValid(pilot))
        {
            pilot.SetPilotHudVisible(true);
            pilot.CameraOwned = false;
            // ⚠ The arm cannot cover this edge: photo mode returns to the halted world the board
            // froze, and halted is its own no-write branch, so the rules would stay off until
            // flight resumed.
            pilot.SetViewedFromOutside(false);
            pilot.EndPhotoMode();   // seeds the pause edge, or the held Escape unpauses too
        }
        _photoPilot = null;
        RestoreBoards();
        // The sheet's pointer too, for the reason the options leaf re-primes it. A mouse button
        // still down as the mode is left reads as a fresh click on the strip it rests over.
        _originalPause?.Reprime();
        // ⚠ Prime every board reader: MenuInput POLLS raw keys, so the Escape still under the
        // player's finger would read as a fresh press on the board that just returned and dismiss
        // the pause it was meant to reopen (BL-279's mechanism, docs/architecture.md).
        foreach (var rig in _rigs)
            MenuInputFor(rig.Index).Prime();
    }

    // PREFERENCES on either pause board: the sheet steps aside and the options leaf stands over the
    // held world in its place. The halt is never dropped, so the mission stays the still frame the
    // pause made of it, and every rig's pause key goes silent for the duration.
    private void OpenPauseOptions(int owner)
    {
        if (_pauseOptions is not { Visible: false } leaf || _pauseBoard == null)
        {
            return;
        }

        // Hide AND stop processing, SuspendBoards' own rule: a board left processing still polls
        // the owner's reader, so the keys driving the leaf would drive the menu under it too.
        _pauseBoard.Visible = false;
        _pauseBoard.ProcessMode = ProcessModeEnum.Disabled;
        var pollers = new List<UI.Screens.MenuInput>();
        var flying = new List<FlightController>();
        foreach (var rig in _rigs)
        {
            if (rig.Controller is { } controller)
            {
                controller.BeginPauseLeaf();
                flying.Add(controller);
            }

            pollers.Add(MenuInputFor(rig.Index));
        }

        if (pollers.Count == 0)
        {
            pollers.Add(MenuInputFor(0));
        }

        // The seats go in so an accepted Controls page reaches the flight behind the leaf now, not
        // at the next restart.
        leaf.Open(pollers, owner, flying);
    }

    // The leaf's own door out, by RETURN TO MAIN MENU, Back or an accepted page: the sheet comes
    // back over the world it never resumed, with its pointer and every board reader re-primed.
    private void ClosePauseOptions()
    {
        if (_pauseBoard == null)
        {
            return;
        }

        _pauseBoard.ProcessMode = ProcessModeEnum.Inherit;
        _pauseBoard.Visible = _pauseState?.Paused ?? false;
        _originalPause?.Reprime();
        // ⚠ Prime every board reader and re-seed every pause edge, ExitPhotoMode's own hazard: the
        // Escape that left the leaf is still under the player's finger, and would otherwise dismiss
        // the sheet that just came back or resume the mission behind it.
        foreach (var rig in _rigs)
        {
            rig.Controller?.EndPauseLeaf();
            MenuInputFor(rig.Index).Prime();
        }
    }

    // ⚠ Suspending a board is hide AND stop processing, not hide alone. A board left processing
    // still polls its owner's menu reader, so the cursor keys would drive a menu nobody can see
    // while the same keys fly the photo camera, the very collision this mode exists to remove.
    private void SuspendBoards()
    {
        _boardWasVisible.Clear();
        foreach (var board in _boards)
        {
            bool alive = IsInstanceValid(board);
            _boardWasVisible.Add(alive && board.Visible);
            if (!alive)
                continue;
            board.Visible = false;
            board.ProcessMode = ProcessModeEnum.Disabled;
        }
    }

    // Back to exactly what was on screen. A results board would recompute its own visibility on the
    // next _Process anyway, but the pause board's is event-driven and no event is coming.
    private void RestoreBoards()
    {
        for (int i = 0; i < _boards.Count; i++)
        {
            if (!IsInstanceValid(_boards[i]))
                continue;
            _boards[i].ProcessMode = ProcessModeEnum.Inherit;
            _boards[i].Visible = i < _boardWasVisible.Count && _boardWasVisible[i];
        }
        _boardWasVisible.Clear();
    }

    // Parent-driven clock adapter. Debug forces stay outside the session-simulation order as
    // outer-frame input injection; each clock substep below enters the same module as realtime.
    private void DriveParentSimulation(GameClock clock)
    {
        // The mission-ending hold rejects outer-frame input before every request enters the same
        // SessionSimulation admission path as realtime.
        if (_campaign?.Leaving != true)
        {
            // --crash[=frame]: force every player's crash rig at a fixed sim frame, the only
            // headless trigger for a crash a live collision otherwise gates. Spawned AI planes
            // crash too, while an inert one declines, since DebugForceCrash is gated on InPlay.
            if (_spec.CrashFrame is int crashFrame && !_crashFired && clock.Frame >= crashFrame)
            {
                _crashFired = true;
                foreach (var rig in _rigs)
                    rig.Controller?.DebugForceCrash();
                foreach (var plane in AiPlanes)
                    plane.DebugForceCrash();
            }
            // --debug-pause[=frame]: the scripted Start press, so a --screenshot catches the pause
            // screen. Player 0 owns it, as a solo press would. Same single-fire shape as --crash.
            if (_spec.DebugPauseFrame is int pauseFrame && !_debugPauseFired && clock.Frame >= pauseFrame)
            {
                _debugPauseFired = true;
                _pauseState?.TryToggle(0);
            }
            // --debug-scoreboard --vs: one scripted, ATTRIBUTED kill on the first sim step,
            // through the same Downed path a real kill takes, so a screenshot has a real K/D and
            // kill banner without scripting a shot. Same single-fire shape as --crash above.
            if (_spec.Versus && _spec.DebugScoreboard && !_versusDebugKillFired && _rigs.Count > 1)
            {
                _versusDebugKillFired = true;
                _rigs[1].Controller?.DebugForceCrash(_rigs[0].Controller?.PlayerIndex);
            }
            // --debug-scoreboard (IA): the director's single-fire force, the same shape as the two
            // blocks above, attributed to P1 so the wrap-up board reads non-zero. Which modes have
            // a force at all: docs/architecture.md on GameSession.cs.
            if (_spec.DebugScoreboard)
            {
                _iaDirector?.ForceDebugScoreboard();
            }
        }
        for (int i = 0; i < clock.Steps; i++)
        {
            _simulation?.Step(clock.Dt);
        }

        DiagTraceRoster();
    }

    // The mission-end hold presents one unchanged flown frame: the session and authored-animation
    // clocks both stay quiet while CampaignDirector alone counts down the hand-off.
    private void HoldEndingFrame()
    {
        if (_clock == null)
        {
            return;
        }
        _clock.SimHeld = true;
        _clock.AuthoredAnimationHeld = true;
    }

    private void DiagTraceRoster()
    {
        if (_campaign?.Graph is not { } graph)
        {
            return;
        }

        if (!_diagRefsDone)
        {
            _diagRefsDone = true;
            foreach (var def in _campaign.Script.Objectives)
            {
                if (def.Travelers is not { } spec)
                {
                    continue;
                }

                string where;
                if (spec.WherePoint is { } pt)
                {
                    where = Log.Format($"POINT ({pt[0]:0},{pt[1]:0},{pt[2]:0})");
                }
                else
                {
                    var found = _diagRuntime?.FindNodes(spec.WhereNode ?? "");
                    where = found is { Count: > 0 }
                        ? Log.Format($"NODE '{spec.WhereNode}' x{found.Count} -> ({found[0].GlobalPosition.X:0},{found[0].GlobalPosition.Y:0},{found[0].GlobalPosition.Z:0}) inTree={found[0].IsInsideTree()} vis={found[0].Visible}")
                        : $"NODE '{spec.WhereNode}' UNRESOLVED";
                }

                Log.Info("core", $"DIAGREF OBJ{def.Number} id={def.Identity?.Class.ToString() ?? "-"}/{def.Identity?.Priority.ToString() ?? "-"} dormant={def.BeginDormant}:{def.DormantUntil} who='{spec.Who}' r={spec.Radius:0} {where}");
            }
        }

        if ((_diagTick++ % 60) != 0)
        {
            return;
        }

        var p = _rigs.Count > 0 ? _rigs[0].Controller : null;
        var sb = new System.Text.StringBuilder();
        sb.Append($"DIAG t={_diagTick / 60}s ");
        if (p != null)
        {
            sb.Append(Log.Format($"player=({p.WorldPosition.X:0},{p.WorldPosition.Y:0},{p.WorldPosition.Z:0}) "));
        }

        foreach (var def in _campaign.Script.Objectives)
        {
            if (def.Travelers is not { } spec)
            {
                continue;
            }

            Vector3? refPos = spec.WherePoint is { } pt
                ? new Vector3(pt[0], pt[1], pt[2])
                : _diagRuntime?.FindNodes(spec.WhereNode ?? "") is { Count: > 0 } f
                    ? f[0].GlobalPosition
                    : null;
            string d = refPos is { } r && p != null ? Log.Format($"{p.WorldPosition.DistanceTo(r):0}") : "?";
            sb.Append(Log.Format($"| O{def.Number} {graph.StateOf(def.Number)}{(graph.CompletedOf(def.Number) ? "*" : "")} d={d}/r{spec.Radius:0} "));
        }

        Log.Info("core", $"{sb}");
    }

    // The surface-vehicle runtime, built once on the first roster or generator that can need one
    // and only where a chapter world exists to copy a hull out of. Null on a bare stage.
    private SurfaceVehicleRuntime? EnsureSurfaceVehicles(BuildState state)
    {
        if (_surfaceVehicles != null)
        {
            return _surfaceVehicles;
        }
        if (state.Gamez is not { } gamez || state.WorldScene is not { } scene
            || state.WorldRuntime is not { } runtime || _worldRoot == null)
        {
            return null;
        }
        _surfaceVehicles = new SurfaceVehicleRuntime(gamez, scene, runtime,
            VehicleDefs.Load(state.ZrdrPath), runtime.WorldRoot ?? _worldRoot)
        {
            Strings = Messages.Load(state.MessagesPath),
        };
        _worldRoot.AddChild(_surfaceVehicles);
        return _surfaceVehicles;
    }

    // player.json's activation floor, on the same lazily loaded skills table the spawner reads;
    // the shipped default when the table cannot be read, so a roster still spawns.
    private float MinAiActiveDist()
    {
        try
        {
            _aiSkills ??= AiSkills.Load(_zrdrPath);
            return _aiSkills.MinAiActiveDist;
        }
        catch (Exception e)
        {
            GD.PushWarning($"campaign: cannot load ai_skill_parameters: {e.Message}");
            return 2000f;
        }
    }

    // The AI flavour of one airframe as the roster would load it, or null before the rigs are
    // built. Read for the def's own pilot facts at spawn; the roster reads the same cached object.
    private PlaneStats? AiStatsForSpawn(string planeName, string? aiDef)
    {
        if (_aiStatsFor == null)
            return null;
        try
        {
            return _aiStatsFor(planeName, aiDef);
        }
        catch (Exception e)
        {
            GD.PushWarning($"ai: cannot resolve '{aiDef ?? planeName}': {e.Message}");
            return null;
        }
    }

    // Gives a spawned AI aircraft its voice. Each chance comes from ai_skill_parameters at its
    // own override when given, else the session's skill rating, so talker and constitution read
    // two independent curves. A missing accent or skills table means a silent pilot whose mode
    // machine is still watched. No voice runtime at all means nothing, never an error.
    private void RegisterAiVoice(FlightController? ai, int? accentId, int? talkerOverride = null,
        int? constitutionOverride = null)
    {
        if (ai == null || _aiVoice == null)
        {
            if (accentId != null && _aiVoice == null)
            {
                Log.Info("sound", $"ai voice: accent {accentId} ignored, no voice runtime in this session");
            }
            return;
        }
        _aiSkills ??= _flightRoster?.AiSkills;
        // ⚠ Hand over every AI, accent or none. The runtime watches an accentless aircraft's mode
        // machine, and the shipped rosters leave nearly every enemy on accentID -1. Dropping those
        // here silences the call-outs the player's own flight speaks about them.
        if (_aiSkills is not { } skills)
        {
            _aiVoice.RegisterAi(ai, null, 0f, 0f);
            return;
        }
        int talkerRating = talkerOverride ?? _spec.AiAttackSkill ?? 5;
        int constitutionRating = constitutionOverride ?? _spec.AiAttackSkill ?? 5;
        _aiVoice.RegisterAi(ai, accentId, skills.At("talker_chance", talkerRating),
            skills.At("constitution_chance", constitutionRating));
    }

    // Maps the simulation's named phases onto this session's concrete owners.
    private sealed class SessionSimulationRuntime(GameSession session) : ISessionSimulationRuntime
    {
        private readonly List<FlightController> _eligibleAiAircraft = new();

        public bool SimHeld => session._clock?.SimHeld ?? false;
        public bool EndingHold => session._campaign?.Leaving ?? false;

        public void StepEndingHold(float dt)
        {
            session.HoldEndingFrame();
            session._campaign?.Step(dt);
        }

        public void CaptureAiAircraft()
        {
            _eligibleAiAircraft.Clear();
            _eligibleAiAircraft.AddRange(session.AiPlanes);
        }

        public void StepIncomingFire(float dt) => session._incomingFire?.SimStep(dt);
        public void StepProjectiles(float dt) => session._projectiles?.SimStep(dt);

        public void StepHumanAircraft(float dt)
        {
            foreach (var rig in session._rigs)
                rig.Controller?.SimStep(dt);
        }

        public void StepZeppelins(float dt) => session._zeppelins?.SimStep(dt);
        public void StepTurretEmplacements(float dt) => session._turretEmplacements?.SimStep(dt);
        public void StepGenerators(float dt) => session._generators?.SimStep(dt);
        public void StepSurfaceVehicles(float dt) => session._surfaceVehicles?.SimStep(dt);

        // The one walk --perf's ai_ms measures (src/Utils/AiStepCost.cs): every AI aircraft's whole
        // sim step, bracketed here rather than per aircraft so the count it divides by is the
        // membership captured at step entry.
        public void StepCapturedAiAircraft(float dt)
        {
            AiStepCost.Open();
            foreach (var aircraft in _eligibleAiAircraft)
                aircraft.SimStep(dt);
            AiStepCost.Close(_eligibleAiAircraft.Count);
        }

        public void StepLandingApproaches()
        {
            session._landings?.Tick();
            if (session._landings is not { } landings)
            {
                return;
            }

            // Per pane: only the human inside the sphere is shown the prompt, and only their own
            // button starts the row.
            foreach (var rig in session._rigs)
            {
                if (rig.Controller is { } flown)
                {
                    flown.AutoLandOffered = landings.OffersAutoLandTo(rig.Index);
                }
            }
        }

        public void StepInstantAction(float dt) => session._iaDirector?.Step(dt);
        public void StepCampaign(float dt)
        {
            session._campaign?.Step(dt);
            if (EndingHold)
            {
                session.HoldEndingFrame();
            }
        }
        public void StepRadio(float dt) => session._radio?.Tick(dt);
        public void StepSmokeScreens(float dt) => session._smokeScreens?.SimStep(dt);
        public void StepBeeperTags(float dt) => session._beeperTags?.SimStep(dt);
        public void StepAiVoice(float dt) => session._aiVoice?.Step(dt);
        public void StepVersus(float dt) => session._versus?.Advance(dt);
    }

    // Per-build state threaded through StartSession's phase methods: the archives, world-build
    // outputs and running counts. ⚠ Nothing here may be cached across a rebuild.
    private sealed class BuildState
    {
        public string DataRoot = "", ZrdrPath = "", SoundsPath = "", InterpPath = "",
            MessagesPath = "", PlanesGamezPath = "";
        public bool Mute, DebugCollision;
        public string TexturesPath = "", GamezPath = "", MissionZrdrPath = "";

        public GameZ Gamez = null!;
        public TextureArchive Textures = null!;
        public SoundArchive? Sounds;
        public Dictionary<string, SoundDef>? SoundDefs;
        public Dictionary<string, SoundGroup>? SoundGroups;
        // Set by LoadArchives from the ArchiveIntent (Session/Lab) it opened the archives for,
        // BuildWorldStage's WorldSession.Options carries them through unchanged.
        public bool TexturesOutliveBuild;
        public bool SoundsOutliveBuild;
        // The archives that outlive this build scope in the anim lab (the lab node owns their
        // disposal); a failed build closes them from StartSession's catch instead.
        public TextureArchive? LabTextures;
        public SoundArchive? LabSounds;
        public UI.Labs.AnimLab? AnimLabNode;

        public GameZNode? NodeSubtree;
        // The --node= subtree's world-frame box, measured at build time and kept for the framing
        // step at the very end, see FrameCamera on why the live-tree merge is the wrong instrument.
        public Aabb? NodeAabb;

        public int MeshInstances;
        // The intro's staged prop aircraft, counted apart because the world builder never saw it:
        // it comes off the aircraft archive on its own SceneBuilder (Mech3/AircraftStage.cs).
        public int StagedAircraftMeshes;
        // The same stage, kept so the roster build can hand a staged prop the livery of the
        // aeroplane it stands in for once the rigs that resolved it exist.
        public AircraftStage? Aircraft;
        public int Colliders;
        public string What = "";

        public Node3D? CloudDeck;
        public IReadOnlyDictionary<Rid, ArrayMesh>? DeckUndimmedMeshes;
        public AnimProgram? CrashProgram;
        public SceneBuilder? WorldScene;
        public AnimRuntime? WorldRuntime;
        public CraterField? Craters;

        /// <summary>The chapter's resolved approach rows, kept so the actor build can re-bind the
        /// trigger once the roster's own approach nodes exist (<see cref="Mech3.RosterMarkers"/>).
        /// </summary>
        public IReadOnlyList<LandingApproach>? Landings;

        public IReadOnlyList<PickupSpec>? Pickups;
    }
}
