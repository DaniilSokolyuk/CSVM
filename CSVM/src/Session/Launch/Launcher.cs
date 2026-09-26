using System.Collections.Generic;
using System.IO;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.UI.Overlays;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Launch;

/// <summary>
/// Main.tscn's root: the once-per-process bootstrap, and everything that persists across
/// in-process relaunches. <c>_Ready</c> parses the command line into a <see cref="SessionSpec"/>,
/// settles paths, applies process-wide side effects a pure value cannot, registers the global
/// shader parameters once, runs the early-quit probes, and builds the persistent camera / orbit
/// rig / sun / WorldEnvironment.
/// Each launch instantiates a <see cref="GameSession"/> with the launch's spec and a
/// <see cref="LauncherContext"/>; the boards' Exit frees it (<see cref="ReturnToMenu"/>) and their
/// Restart on a mission frees and rebuilds it (<see cref="RestartSession"/>). Both
/// interactive paths in draw the load screen and build a frame later. Full arg reference:
/// docs/cli.md. Module notes: this file's docs/architecture.md entry.
/// </summary>
public partial class Launcher : Node3D
{
    // Rendered frames per `--perf` report. A frame count rather than a wall second
    // because under the fixed clock one rendered frame is exactly one sim step, so a window is a
    // fixed amount of simulation and two runs of the same scenario yield the same number
    // of samples, which is what makes a paired A/B comparable. At the vsync cap it is also
    // still one report a second, so an interactive run reads as it always did.
    private const int PerfWindowFrames = 60;

    // Nearest-rank p95 index into the sorted window (0-based): `ceil(0.95 x
    // PerfWindowFrames) - 1`. At 60 samples this is index 56, leaving 3 samples above it. p99
    // is deliberately not reported, nearest-rank p99 over 60 samples resolves to index 59, the
    // same slot as max, so it would just be max under another name (see ReportPerf).
    private const int Perf95Index = (PerfWindowFrames * 95 + 99) / 100 - 1;

    // Wall seconds per always-on `[perf] rate` line. Wall time and NOT a frame count, unlike
    // PerfWindowFrames above: a frame-count window stretches exactly as the rate falls, so at
    // 1 fps a 60-frame window would report once a minute and describe the collapse least where
    // it matters most. Ten seconds is quiet enough to leave always on beside the rest of the
    // log and short enough that a drop is placed within the sortie. TUNE.
    private const double RateWindowSeconds = 10;

    // Master-bus index. default_bus_layout.tres sends Music, Effects and Voice into Master, so a
    // gain or a mute written here still reaches every sound while leaving a player's own mix on the
    // three child buses alone.
    private const int MasterBus = 0;

    // TUNE, and the FALLBACK only: a flown mission overwrites this per zone from its own pushed-out
    // fog near (WeatherRig.ApplyEnhancedLighting), so shadows end where that zone's haze ramp
    // begins. This value is what a session with no weather.json gets, and it sits in the middle of
    // the pushed-near range the shipped zones resolve to (2000-4200 m).
    private const float EnhancedShadowMaxDistance = 3000f;

    // TUNE, paired with the distance above and judged the same way (WeatherRig.ApplyEnhancedLighting
    // sets the flown-mission copy). Fades the last cascade out before this fallback distance rather
    // than cutting at a hard edge.
    private const float EnhancedShadowFadeStart = 0.8f;

    // TUNE, judged at the controls, and the pair trades against each other: lower values put
    // dithered acne over every terrain triangle at C1's 25° sun, higher ones dissolve a hangar's
    // shadow along with it. These keep the building and aircraft silhouettes with no acne left.
    private const float EnhancedShadowBias = 0.05f;
    private const float EnhancedShadowNormalBias = 1.25f;

    // TUNE. Fractions of EnhancedShadowMaxDistance, tighter than Godot's 0.1/0.2/0.5 because the
    // shadows a player reads are the aircraft's own and the buildings it passes, all inside the
    // first few hundred metres; the outer cascades only have to carry a skyline into the haze.
    private const float EnhancedShadowSplit1 = 0.06f;
    private const float EnhancedShadowSplit2 = 0.17f;
    private const float EnhancedShadowSplit3 = 0.42f;

    // TUNE. Screen-space reflection on the glossy water arm: the step count buys reflection length
    // along the ray, the fades hide where a ray runs off the screen or past the depth buffer.
    // The fade-out exponent is the lever on the border and aircraft flicker, since it dims a ray
    // before it is lost. Depth tolerance measures inert here, from this value up to 8.0.
    private const int EnhancedSsrMaxSteps = 64;
    private const float EnhancedSsrFadeIn = 0.15f;
    private const float EnhancedSsrFadeOut = 2.5f;
    private const float EnhancedSsrDepthTolerance = 0.2f;

    // TUNE, judged at the controls. The sun's apparent size in degrees; the real sun is about
    // 0.5, softening a cast edge into a penumbra instead of a hard line. A 0.25/0.5/1.0/2.0
    // sweep at the C1 waterfall lake held the edge at 4-6 px through 1.0. Only 2.0 opened it
    // into a visibly soft ~18 px transition.
    private const float EnhancedShadowAngularDistance = 2.0f;

    // TUNE, judged at the controls: Godot's own default. Raising it alongside the angular
    // distance above widened the edge further, but it also dithered the lit water beside it.
    // Kept here rather than trading a hard line for banding.
    private const float EnhancedShadowBlur = 1.0f;

    // ⚠ Do not lower this while EnhancedShadowAngularDistance stays above the sun's real 0.5°.
    // Godot resolves a penumbra by sampling the shadow map through a disc rotated per screen
    // pixel. Too few samples for the disc's width leave that rotation as a woven pattern over
    // every lit surface. This width needs the top rung.
    private const RenderingServer.ShadowQuality EnhancedShadowFilterQuality =
        RenderingServer.ShadowQuality.SoftUltra;

    // TUNE, judged at the controls on C2/C5. Godot's own default (1.0 m) reads a building's own
    // trim but misses the wider contact shading a street canyon wants at this world's scale
    // (buildings tens of metres tall, streets a similar width); this radius picks up a block's
    // base and a hangar's corner without darkening open tarmac.
    private const float EnhancedSsaoRadius = 2.5f;

    // TUNE, judged at the controls: Godot's defaults (intensity 2.0, power 1.5) already read as
    // grounded contact shading rather than a grey wash at this radius, so both are kept.
    private const float EnhancedSsaoIntensity = 2.0f;
    private const float EnhancedSsaoPower = 1.5f;

    // TUNE, Godot defaults: detail keeps small-scale creases (window mullions, girders) from
    // being swallowed by the coarse term above; horizon and sharpness are the denoise pair that
    // keeps the depth-buffer edges from shimmering worse than the effect is worth.
    private const float EnhancedSsaoDetail = 0.5f;
    private const float EnhancedSsaoHorizon = 0.06f;
    private const float EnhancedSsaoSharpness = 0.98f;

    // TUNE, judged at the controls against C21's contract (only the glow-arm sprites exceed 1.0
    // in the HDR buffer). A threshold of 1.0 blooms exactly them; bloom stays 0 so nothing below
    // threshold glows, and screen blend keeps a flare's halo additive without blowing its own
    // core out further.
    private const float EnhancedGlowHdrThreshold = 1.0f;
    private const float EnhancedGlowBloom = 0.0f;
    private const float EnhancedGlowIntensity = 0.9f;
    private const float EnhancedGlowStrength = 1.1f;
    private const Godot.Environment.GlowBlendModeEnum EnhancedGlowBlendMode =
        Godot.Environment.GlowBlendModeEnum.Screen;

    // TUNE. Scale and cap on the values the glow pass reads before it thresholds them; wide enough
    // that a saturated flare core (255 before the tonemap) still separates from its own falloff.
    private const float EnhancedGlowHdrScale = 2.0f;
    private const float EnhancedGlowHdrLuminanceCap = 8.0f;

    // TUNE, judged at the controls against a C4 horizon, a C1 horizon and C5 at night: AgX rolls
    // off the far-ridge washout the authored sun energy produces (Wave B) while keeping the night
    // city's contrast, where Filmic read flatter. Exposure stays neutral; the AgX-specific white
    // point is what recovers the horizon rather than the general TonemapWhite, which AgX ignores.
    private const Godot.Environment.ToneMapper EnhancedTonemapMode = Godot.Environment.ToneMapper.Agx;
    private const float EnhancedTonemapExposure = 1.0f;
    private const float EnhancedTonemapAgxWhite = 6.0f;
    private const float EnhancedTonemapAgxContrast = 1.0f;

    // The zone default FOG_COLOR (Flight/Airframe/Weather.cs's no-weather zone), which is the colour a
    // horizon dome fades into at eye level. Enhanced mode's sky until a flown zone writes its own
    // over it, so a world with no weather.json still reflects a plausible sky.
    private static readonly Color EnhancedDefaultSkyColor = new(0.69f, 0.69f, 0.69f);

    // What F11's placement print receives at the launchscreen, where no session (and no rigs)
    // exists, the same empty list the pre-split root held after a teardown.
    private static readonly List<PlayerRig> NoRigs = new();

    // This window's unaveraged per-frame wall cost, for the max/p95 that ReportPerf reports
    // alongside its means, fully overwritten every window, so it needs no reset. _perfFrameMsSorted
    // is scratch for the sort at window close, kept off the frame path so no window allocates.
    private readonly double[] _perfFrameMs = new double[PerfWindowFrames];
    private readonly double[] _perfFrameMsSorted = new double[PerfWindowFrames];

    // Everything the command line settled, parsed and resolved once (see SessionSpec). _cli is what
    // the user typed; _spec is what the LIVE session was built from, the launchscreen's pick
    // patches it, so every consumer below reads _spec and nothing re-derives a launch setting.
    private SessionSpec _cli = null!;
    private SessionSpec _spec = null!;
    // Per-player pad binding chosen in the launchscreen's join flow (null = derive from the
    // connected roster in AssignPads, which is what every CLI launch does).
    private int[][]? _menuPads;
    // The device-less menu players --debug-join= asks for, held until the launchscreen exists and
    // consumed there: a later return to the menu keeps whoever really joined.
    private int _pendingJoin;
    // --debug-waves=/--debug-wingmen=, the same one-shot hold as _pendingJoin above but for the
    // Instant Action wizard's own screenshot aids.
    private int _pendingWaves, _pendingWingmen;
    // --debug-preset=, same one-shot hold. −1 rather than 0 because preset 0 is a real request.
    private int _pendingPreset = -1;
    // The --screenshot=/--shots=/--frames= state machine and F11/F12's placement print and
    // ad-hoc save, see src/Tooling/CaptureDirector.cs's entry. Process-scoped: constructed once
    // from the launch spec, never re-armed by a menu relaunch.
    private Tooling.CaptureDirector _captureDirector = null!;

    // The --export-gltf= one-shot and F10's ad-hoc glTF save, see src\Tooling\GltfExporter.cs's
    // entry. Constructed once from the launch spec alongside the capture director.
    private Tooling.GltfExporter _gltfExporter = null!;
    // The master seed every subsystem generator derives from (see Utils.Rng). Pinned runs take the
    // spec's value; everything else draws from the clock, which is why it is resolved here and not
    // in the spec.
    private ulong _masterSeed;
    // The once-per-process draw _masterSeed starts at, kept so an unpinned relaunch can step to the
    // next sortie's master from it rather than from whatever the last session used.
    private ulong _processSeed;
    // How many times the launchscreen has started a flight this process, the step count applied to
    // _processSeed for an unpinned run, and the label the seed is logged under.
    private int _sortie;

    // The clock the --run-tests suites hand back (RunTestSuites' out param), ticked below so a
    // suite's teardown frame behaves as it always did. Every other clock is the session node's.
    private GameClock? _clock;

    private Camera3D _camera = null!;
    // Built once in _Ready and kept across sessions (like the camera). The mesh lab steers
    // both, sun direction/energy and the ambient, so held here rather than local to
    // SetupLighting.
    private DirectionalLight3D _sun = null!;
    private Godot.Environment? _env;
    // The static inspection view's orbit camera (LMB orbit, wheel zoom, AABB framing); created in
    // _Ready and kept across sessions, like _camera. --yaw=/--pitch= seed its initial angles.
    private OrbitCamera _orbit = null!;

    // Session lifecycle (the launchscreen's in-process world rebuild): each launch instantiates a
    // GameSession node, freed again by ReturnToMenu or RestartSession. The camera, lights and
    // global shader params live on `this` and persist across sessions.
    private GameSession? _session; // the current session node (null at the launchscreen)
    // The process-lifetime menu host and its audio service, built on the first show of the menu
    // (a no-content-arg launch): the host owns the shared features, the first seat and the active
    // presentation, and hands every typed exit to OnMenuExit.
    private MenuHost? _menuHost;
    // Wheel steps turned since the menu seat last read them, positive toward a list's foot.
    private int _menuWheel;
    private MenuAudioService? _menuAudio;
    // The decoded menu layout the Original presentation composes from, loaded once by the
    // availability check and handed to every Original instance the registry creates.
    private MenuLayout? _originalLayout;
    // An Options apply, acted on at the top of the next frame: the exit arrives inside the active
    // presentation's own tick, which is no place to free it.
    private OptionsApplyExit? _pendingApply;
    // The --menu= aid, held for the cold start alone: the first presentation created reads it and
    // the first ShowMenu consumes it, so no return from flight and no switch re-enters its screen.
    private string? _menuAid;
    private bool _menuDriven;      // launched into the menu → Esc from flight returns here, not quit
    // Where a flight left early lands, settled by the launch that started it rather than by the
    // exit press: the menu's own launch paths write it (MenuReturnDestination.ForLaunch) and a
    // restart keeps it, since a restarted mission was launched from the same screen.
    private MenuReturnDestination _exitDestination = MenuReturnDestination.TopLevel;
    // The process's one chapter cinema, so a chapter's film plays once per program run. Entering
    // the campaign plays it; leaving to the main menu and coming back does not. A restart plays the
    // current chapter's film again until that chapter's first mission has been flown. This is a
    // chosen behaviour, not a decoded one: the original's rule sits behind GUI callback 2151, which
    // nothing here decodes. Do not replace it with a latch persisted in the profile.
    private ChapterCinema? _chapterCinema;
    // The process's one closing cinema, so the campaign's last film plays once per program run
    // however often the player reopens the book afterwards. Its gate is the seated profile's own
    // completion, not a flag stored anywhere, so a second profile that finishes in the same run
    // does not get it again. Do not replace it with a latch persisted in the profile.
    private ClosingCinema? _closingCinema;
    // The score, and the archive it streams from. Both are process-lifetime, unlike the
    // build-scoped SessionArchives.Sounds: one channel has to survive a mission launch, or the
    // cabin track would restart every time the player left a board. See docs/org/music.md.
    private MusicPlayer? _music;
    private SoundArchive? _musicArchive;
    private System.Random _musicRng = new();
    // The profile a just-ended campaign mission belongs to, acted on at the top of the next frame:
    // the mission ends inside the session's own physics step, which is no place to free it.
    private (string Profile, CampaignMissionResult Result)? _pendingDebrief;

    // The final numbers of an ended Instant Action mission, acted on at the top of the next frame.
    // The reason is the debrief's: the ending arrives inside the session's own step.
    private IaWrapupSnapshot? _pendingWrapup;

    // The load screen and the deferred build behind it (BeginLaunch → _Process). A build is one
    // synchronous block, so the screen has to be DRAWN before it starts: _launchFramesWaited counts
    // the frames since the request and the build runs on the first one that proves a frame rendered.
    // ⚠ Null/-1 means no launch is owed; a CLI launch does not come through here at all.
    private CanvasLayer? _loadLayer;
    private int _launchFramesWaited = -1;
    // This is the load screen's second half. Once the session is built, the work it ordered for the
    // load is carried out one step a frame with the screen still up. That is what makes the screen
    // a real yield of several frames. -1 means nothing is owed.
    private int _loadStepsRun = -1;
    // The cover that bridges the load screen and the session's first real frame. It is raised with
    // the screen, or with the build on a CLI launch. It stays opaque until the session says that
    // frame is ready, then fades up from dark. Null under --det and once it has finished.
    private UI.Screens.SessionStartFade? _startFade;

    private double _perfClock;
    private int _perfFrames;
    private double _perfProcess, _perfGpu, _perfCpuRender, _perfPhysics;
    private double _perfDraws, _perfPrims, _perfNodes, _perfMem;

    // The --perf GC readout. Built with the first --perf frame rather than in _Ready, so a run
    // without the flag subscribes to no runtime events at all.
    private Utils.GcTrace? _gcTrace;

    // The always-on rate window (ReportRate). Separate accumulators from the --perf ones above
    // rather than shared: those are opt-in and reset on a frame count, these run every session.
    private double _rateWallMs;
    private double _rateWorstMs;
    private int _rateFrames;

    // The always-on frame-hitch instrument and the viewport whose render times feed it. Both are
    // settled in _Ready: the monitor's constructor is what registers its hitchMonitor.* config keys,
    // and measured render time is opt-in per viewport, so both have to happen before the first
    // frame rather than lazily on one.
    private HitchMonitor _hitchMonitor = null!;
    // The F14 / --debug-fps frame-cost readout, ticked every frame like
    // the instrument above it, but drawing (if switched on) is its own concern, not this class's.
    private UI.Overlays.PerfHud _perfHud = null!;
    // The version stamp drawn in the menu's corner, shown and hidden off the host's own "the menu
    // is up" so no presentation has to carry one and no flight capture ever sees it.
    private UI.Screens.BuildStamp _buildStamp = null!;
    private Rid _viewportRid;
    // The previous frame's QPC stamp, so the monitor is fed a raw wall cost rather than Godot's
    // post-processed `delta`. 0 on the first frame, which reports 0 ms and trips nothing.
    private long _lastFrameStamp;
    // B6's write path for the monitor above: queues a tripped record and drains it a few seconds
    // later, never inline on the hitching frame. Built after Log.Open (its path derives from
    // Log.SinkPath), so it lives a step later in _Ready than _hitchMonitor does.
    private HitchSidecar _hitchSidecar = null!;

    // Base (chapter-independent) paths + parse state, set once in _Ready; each session node
    // receives them via LauncherContext and recomputes the chapter-dependent gamez/texture/mission
    // paths from its spec. In the editor (and editor-run builds) _repoRoot is the repo checkout
    // root (res://'s parent on disk); in an exported build it is the exe's own directory, since
    // GlobalizePath("res://") only maps to a real directory inside the editor.
    private string _repoRoot = "";
    // Set beside the root above, by the same editor check. The log directory is all that reads it
    // (Log.DirectoryFor): a recipient's log must not land in a hidden developer folder.
    private bool _exported;
    // Where extracted/ lives. Defaults to _repoRoot; overridden by --data-root= or CSVM_DATA_ROOT
    // so a git worktree can run the game, /extracted/, /CrimsonSkiesGame/ and /tools/ are
    // git-ignored, so a worktree checkout has none of them and cannot otherwise build or verify.
    private string _dataRoot = "";
    private string _planesGamezPath = "";  // extracted/planes.zip, the aircraft models (always this)
    private string _zrdrPath = "";
    private string _soundsPath = "";
    private string _interpPath = "";
    private string _messagesPath = "";
    private string _rofPath = "";          // the extracted UI archive (paint patterns)
    // Constructed once the base paths above are settled; every --dump-*/--run-tests/--*-test/
    // --destroy= probe wrapper delegates to it (see src/Tooling/ProbeRunner.cs).
    private Tooling.ProbeRunner _probeRunner = null!;

    // Alt-tabbing away silences the game; alt-tabbing back restores it, via an AudioServer
    // master-bus mute rather than a factor threaded through the audio code. See this file's
    // docs/architecture.md entry for why (FlightAudio vs WorldSounds gain plumbing).
    // ⚠ `--mute` cannot be reused for this: it is load-time and never constructs the audio
    // players, so there is nothing here to toggle.
    private bool _focusMuted;

    // The screen ExitSession takes a flight back to, as the last launch settled it. The setter is
    // the launch-return suite's way of putting the live node's own destination back afterwards.
    internal MenuReturnDestination ExitDestination
    {
        get => _exitDestination;
        set => _exitDestination = value;
    }

    // The session clock as this frame sees it: the suites' own under
    // `--run-tests`, the live session's (published as GameClock.Current by its
    // build, nulled by its teardown) otherwise, null at the launchscreen.
    private GameClock? ClockNow => _clock ?? GameClock.Current;

    // The launchscreen while the Built-in presentation is the active one, else null: the door for
    // what is Built-in's alone (its one-shot debug aids, its failed-build note). Null-safe under
    // Original, where both are absent by design; every other reading of the menu goes through the host.
    private LaunchMenu? BuiltInMenu => (_menuHost?.Active as BuiltInPresentation)?.Menu;

    // The presentation a session's boards take. A menu launch has already settled it on the host,
    // availability included. A CLI launch builds no host, so only the flags speak for it there.
    private PresentationId SessionPresentation =>
        _menuHost is { } host ? host.Selected
        : _spec.ForceBuiltInPresentation ? PresentationId.BuiltIn
        : new PresentationId(Utils.PresentationResolution.Requested(_spec.PresentationOverride));

    public override void _Ready()
    {
        // One notch behind the session node (ProcessPriority -1000), so the shader clock and
        // capture pipeline run at the same point in the frame they did on one root. Godot runs
        // the lowest priority first.
        ProcessPriority = -999;

        // Both ends of the physics-tick bracket (see PhysicsTickCost). At the launcher rather than
        // the session, because the pair must survive a session rebuild for the tick rate either
        // side of one to be comparable.
        AddChild(PhysicsTickCost.MakeBracket(false));
        AddChild(PhysicsTickCost.MakeBracket(true));

        // The same pair around the process pass (see ProcessPassCost), for the same reason.
        AddChild(ProcessPassCost.MakeBracket(false));
        AddChild(ProcessPassCost.MakeBracket(true));

        // Load the optional tuning-override file first, before any module reads a Config value.
        // Missing/malformed file → in-code defaults (never throws); see src/Config.cs.
        Config.Load();

        if (OS.HasFeature("editor"))
        {
            // res:// is the CSVM/ project dir on disk only while the editor (or an editor-run
            // build) hosts the game; the repo root is its parent.
            var projectDir = ProjectSettings.GlobalizePath("res://");
            _repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
        }
        else
        {
            // Exported build: res:// lives inside the pck, so the root is the exe's own folder,
            // extracted/ ships beside the exe, and logs/ and .scratch/ output land there too.
            _exported = true;
            _repoRoot = Path.GetFullPath(Path.GetDirectoryName(OS.GetExecutablePath())!);
        }

        // Everything the command line settles is parsed and resolved in one place; see
        // SessionSpec. What's left here is what a pure value cannot do: process-wide side
        // effects, and the warnings below, emitted before the log so they read in launch order.
        _cli = SessionSpec.Parse(OS.GetCmdlineUserArgs());
        _spec = _cli;
        foreach (var note in _spec.Warnings)
        {
            if (note.Category.Length == 0)
            {
                Log.Info("core", $"{note.Message}");
            }
            else
            {
                Log.Warn(note.Category, $"{note.Message}");
            }
        }

        // Every base path derives from the data root, so it is settled first.
        // Precedence: --data-root= beats CSVM_DATA_ROOT beats the repo root.
        _dataRoot = _repoRoot;
        var dataRootEnv = OS.GetEnvironment("CSVM_DATA_ROOT");
        if (!string.IsNullOrEmpty(dataRootEnv)) _dataRoot = Path.GetFullPath(dataRootEnv);
        if (_spec.DataRoot is { } dataRootArg) _dataRoot = Path.GetFullPath(dataRootArg);
        if (_dataRoot != _repoRoot)
            Log.Info("core", $"data root: {_dataRoot} (repo root {_repoRoot})");

        // An override is used verbatim; everything else derives from the data root.
        var planesGamezPath = Path.Combine(_dataRoot, "extracted", "planes.zip");
        _zrdrPath = _spec.Zrdr ?? Path.Combine(_dataRoot, "extracted", "zrdr.zip");
        _soundsPath = _spec.Sounds ?? Path.Combine(_dataRoot, "extracted", "soundsh.zip");
        _interpPath = _spec.Interp ?? Path.Combine(_dataRoot, "extracted", "interp.json");
        _messagesPath = _spec.Messages ?? Path.Combine(_dataRoot, "extracted", "messages.json");
        _rofPath = _spec.Rof ?? Path.Combine(_dataRoot, "extracted", "rof");

        // The extraction tree's provenance check, at most one warning line, never a block. An
        // --extract run is about to write that tree, so a warning about it would only mislead.
        if (_spec.ExtractInstall == null)
        {
            ExtractionStamp.Check(_dataRoot);
        }

        // The drop-in writes statics every material built afterwards reads, so it is applied here
        // rather than carried as a session value.
        foreach (string name in _spec.TexOverrides)
        {
            TextureDropIn.SetScratchDir(_repoRoot);
            TextureDropIn.AddOverride(name);
        }
        if (_spec.TexCensus)
        {
            TextureDropIn.SetScratchDir(_repoRoot);
            TextureDropIn.EnableCensus(_spec.TexCensusFilter);
        }
        // Read by every archive built afterwards, for the same reason the drop-in's state is a
        // static: it settles once per launch and no consumer chooses it.
        TextureArchive.Mips = _spec.Mips;
        // A connected pad with stick drift steers the free camera and nudges the flight model,
        // which quietly makes a "deterministic" scripted run not one. SDL's hints don't help
        // (Godot 4.7 enumerates the pad regardless), so the switch is ours.
        if (_spec.PadsDisabled)
        {
            Pads.Disabled = true;
        }
        // Read by both AI pickers, for the same reason the two statics above are statics. It
        // settles once per launch, and no pilot or gunner chooses it for itself.
        Flight.Ai.AiTargetRanking.AircraftFirst = _spec.AircraftFirstTargeting;
        _captureDirector = new Tooling.CaptureDirector(_spec);
        _gltfExporter = new Tooling.GltfExporter(_spec);
        _pendingJoin = _spec.DebugJoin;
        _pendingWaves = _spec.DebugWaves;
        _pendingWingmen = _spec.DebugWingmen;
        _pendingPreset = _spec.DebugPreset;
        // Built alongside the other process-scoped services, ahead of every probe's early quit
        // and of --dump-config: its constructor is what registers the five hitchMonitor.* keys.
        // See this file's docs/architecture.md entry.
        _hitchMonitor = new HitchMonitor();
        // Measured render time is opt-in per viewport and reads 0 until it is, so it is enabled once
        // here rather than per frame from ReportPerf (which used to own the call): the hitch record
        // needs the CPU/GPU split on every frame, not only on a --perf run.
        _viewportRid = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);


        // The window is created without focus (no_focus in project.godot); an interactive
        // session asks for it explicitly instead, since setting the flag at runtime measured
        // not to hand focus back. See docs/verification.md's SHELL-13.
        if (!_spec.IsScripted)
        {
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, false);
            DisplayServer.WindowMoveToForeground();
            Log.Debug("core", $"window: focus requested (interactive session)");
        }
        else
        {
            ScriptedWindow.Hide();
        }

        // ⚠ The driver and method IN USE, never the project setting: a machine that fell back off
        // Forward+/Vulkan gets none of the export's baked pipelines, and nothing else in the log
        // would say so. Ahead of the vsync line, whose meaning rests on the refresh rate here.
        Log.Info("perf", $"gpu={Tooling.GoldenShot.Adapter()} driver={RenderingServer.GetCurrentRenderingDriverName()} method={RenderingServer.GetCurrentRenderingMethod()} refresh_hz={DisplayServer.ScreenGetRefreshRate():0.#}");

        // --run-tests must never read or write the player's options file, and this must be set
        // before the first UserOptions() call, the vsync read below. One scratch directory per
        // process: docs/architecture.md's OptionsStore entry has the shard race that settled it.
        if (_spec.RunTests)
        {
            string scratchOptions = Path.Combine(Path.GetTempPath(), "CSVM", "run-tests-options",
                System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(scratchOptions))
            {
                Directory.Delete(scratchOptions, recursive: true);
            }

            Directory.CreateDirectory(scratchOptions);
            OptionsStore.DirectoryOverride = scratchOptions;
            CSVM.Bindings.BindingStore.DirectoryOverride = scratchOptions;
        }

        // The pacing's three sources and both engine calls are VSyncSetting's; --no-vsync uncaps
        // the loop so frame/fps/script report work done rather than a refresh cap. The config read
        // stays unconditional so it self-registers, see Utils/Config.cs's entry.
        bool vsyncOnByConfig = Config.GetBool(VSyncSetting.Key, VSyncSetting.ConfigDefault);
        VSyncSetting.Apply(VSyncSetting.Resolve(_spec.NoVsync, VSyncSetting.SavedWord(_spec.Det), vsyncOnByConfig));

        // The saved screen, then the mode, then the size, for a session someone is at only: a
        // scripted run's window is hidden off screen and its capture compared against the viewport
        // project.godot pins, so none may reach one. None asks for focus; docs/architecture/Utils.md.
        if (!_spec.IsScripted)
        {
            MonitorSetting.Apply(MonitorSetting.Resolve(MonitorSetting.SavedWord(_spec.Det), MonitorSetting.Screens()));
            var displayMode = DisplayModeSetting.Resolve(DisplayModeSetting.SavedWord(_spec.Det));
            DisplayModeSetting.Apply(displayMode);
            ResolutionSetting.Apply(
                ResolutionSetting.Resolve(ResolutionSetting.SavedWord(_spec.Det), ResolutionSetting.ScreenSizes(), displayMode.Word),
                GetWindow());
        }

        // --debug-anim opens the call-site gates of the anim and sound families, so it is also the
        // legacy spelling of their console filter; an explicit --log= is applied after it and can
        // still narrow either one.
        if (_spec.DebugAnim)
        {
            Log.Configure("anim:debug,sound:debug");
        }
        foreach (string logSpec in _spec.LogSpecs)
        {
            Log.Configure(logSpec);
        }
        // Opened under the session shape's name (it names the file) and before anything else can
        // log. The sink always takes every category at every level; --log= only widens what the
        // console additionally shows.
        Log.Open(_repoRoot, _spec.ModeName, BuildVersion.Current, _exported);
        // Every persisted store resolves against this root (profiles, hangar planes, bindings,
        // options), and it is derived from the project name alone, so a worktree run shares it with
        // the main checkout. Printed so a "not found" against a file on disk is answered, not guessed.
        Log.Info("core", $"user={ProjectSettings.GlobalizePath("user://")}");
        // Named while the run is live, because a crash never reaches the mirror in _ExitTree and
        // this is then the only pointer to the traces our own sink cannot see.
        Log.Info("core", $"engine log={Path.Combine(OS.GetUserDataDir(), "logs", "godot.log")} (mirrored beside this one on quit)");
        foreach (var (old, replacement) in _spec.Deprecated)
        {
            Log.Warn("core", $"deprecated flag={old} use={replacement}");
        }
        // Ahead of --dump-config, same reason as _hitchMonitor: registers the two
        // hitchSidecar.* keys. The fallback path only matters if Log.Open itself failed.
        string hitchLogPath = Log.SinkPath
            ?? Path.Combine(Log.DirectoryFor(_repoRoot, _exported), $"{_spec.ModeName}-nolog.hitches.jsonl");
        _hitchSidecar = new HitchSidecar(hitchLogPath, _hitchMonitor.Last.Ring.Length);

        // --extract builds no world and no menu: it writes the data root's extracted/ and quits.
        if (_spec.ExtractInstall is { } extractInstall)
        {
            StartHeadlessExtraction(extractInstall);
            return;
        }

        // --headless + --screenshot can never produce a frame: the dummy renderer's GetImage()
        // never returns, so the capture loop never counts down. Reject the combo here, before
        // any session builds, rather than let it hang as an orphan holding the log handle.
        if (_spec.ScreenshotPath != null && DisplayServer.GetName() == "headless")
        {
            Log.Error("core", $"--screenshot needs a real GPU context; --headless never renders a capturable frame, drop one of the two flags");
            GetTree().Quit(1);
            return;
        }
        // What a pure value cannot do: draw an unpinned seed from the clock. A deterministic run
        // (and the anim debugger) pins it; applied here too so the dump tools, which quit before
        // any session builds, draw the resolved master rather than a zero one.
        _processSeed = _spec.PinnedSeed ?? Rng.TimeSeed();
        _masterSeed = _processSeed;
        Rng.Reset(_masterSeed, _spec.SeedPinned);
        LogMasterSeed();
        // Announce the whole resolved bundle on one line, so any capture or log carries the exact
        // conditions it was taken under instead of relying on the reader remembering what --det
        // implies. Every constituent is named with its value, including the ones a flag overrode.
        if (_spec.Det)
        {
            // The git-ignored dev tuning file would otherwise make a deterministic capture a
            // function of one machine's uncommitted state, see docs/verification.md's DET-8.
            // Pass --no-det to capture with your overrides applied.
            int dropped = Config.OverrideCount;
            Config.ClearOverrides();
            ulong liverySeed = _spec.PaintSeedExplicit ? _spec.PaintSeed : Rng.SeedFor(Rng.Paint);
            float dtMs = GameClock.FixedDt * 1000f;
            Log.Info("core", $"det clock=fixed dt_ms={dtMs:0.###} seed={_masterSeed} spawn={_spec.SpawnIndex} livery_seed={liverySeed} pads=off jitter={_spec.JitterDeg:0.###} config=defaults dropped_overrides={dropped} via={_spec.DetVia}");
        }
        else if (_spec.NoDet && (_spec.DetExplicit || _spec.ScriptedBy.Length > 0))
        {
            string wouldBe = _spec.DetExplicit ? "--det" : _spec.ScriptedBy;
            Log.Info("core", $"no-det: {wouldBe} would run deterministically, wall-clock sim clock, unpinned randomness seed={_masterSeed}");
        }
        // After the --det block, so a deterministic run reads the flag but not the tuning file that
        // ClearOverrides just dropped; before the early-quit probes below, so --run-tests and the
        // --dump-* wrappers are covered by the same gain an interactive launch gets.
        ApplyMasterVolume();
        // After it, so the two are read in the order they multiply: the developer gain on bus 0,
        // then the player's mix on the three buses under it. ⚠ --det reads no saved level, the same
        // rule the saved graphics word below follows; SavedLevels owns that drop.
        var savedMix = AudioMix.SavedLevels(_spec.Det);
        AudioMix.Apply(savedMix.Master, savedMix.Music, savedMix.Effects, savedMix.Voice);
        // Logged on every launch, not only when a flag moved it. The shipped factor is a departure
        // from the decoded radii, so a run's record has to say what reach it played at.
        Mech3.SoundFalloff.SetRangeScale(_spec.SoundRangeScale);
        string rangeVia = Mech3.SoundFalloff.RangeScale == Mech3.SoundFalloff.ShippedRangeScale
            ? "shipped" : "--sound-range-scale";
        Log.Info("sound", $"sound range scale=x{Mech3.SoundFalloff.RangeScale:0.###} via={rangeVia}, every positional RANGE pair and its cull multiplied");
        // The haptics toggle, read under the same rule and defaulting ON where nothing is saved.
        // ⚠ A deterministic run never rumbles. A golden sweep or a scripted probe must not reach
        // the hardware on the desk, and no screen is drawn from this.
        PadRumble.Enabled = !_spec.Det && OptionsStore.UserOptions().Load().Rumble != false;
        // The rocket carve, defaulting OFF: the original carves nothing in play. ⚠ No screen offers
        // it. The saved key and --craters are its only doors, read here and nowhere else. A --det
        // run drops the key, keeping every golden clear of a bowl; the flag survives it, for a probe.
        CraterGate.Enabled = _spec.Craters
            || (!_spec.Det && OptionsStore.UserOptions().Load().RocketCraters == true);
        // Before the first PreferUnzipped call and process-wide, so every later resolution (the
        // chapter paths in StartSession, the menu pages' own lookups) takes the same asset shape.
        SessionPaths.ForceZipped = _spec.ZipAssets;
        if (_spec.ZipAssets)
        {
            Log.Info("core", $"assets: --zip-assets, reading .zip archives, ignoring unpacked folders");
        }
        // Prefer the unpacked sibling folder from ExtractAssets.ps1 -Unzip when it exists (loose
        // JSON/PNG/WAV: no zip decompression at load). Base (chapter-independent) paths resolve now;
        // the chapter-dependent gamez/texture/mission paths resolve per-session in StartSession.
        _planesGamezPath = SessionPaths.PreferUnzipped(planesGamezPath);
        if (_spec.Zrdr == null) { _zrdrPath = SessionPaths.PreferUnzipped(_zrdrPath); }
        if (_spec.Sounds == null) { _soundsPath = SessionPaths.PreferUnzipped(_soundsPath); }
        _probeRunner = new Tooling.ProbeRunner(_repoRoot, _dataRoot, _zrdrPath, _soundsPath,
            _interpPath, _messagesPath, _planesGamezPath);

        // Registers the distance-fog params SceneBuilder's shaders reference; defaults are a
        // no-op until --fly's weather.json overrides them.
        // ⚠ Runs once here, GlobalShaderParameterAdd errors on a second call; a rebuild must Set.
        RenderingServer.GlobalShaderParameterAdd("csky_fog_color",
            RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.69f, 0.69f, 0.69f));
        RenderingServer.GlobalShaderParameterAdd("csky_fog_range",
            RenderingServer.GlobalShaderParameterType.Vec2, new Vector2(1e8f, 1e9f));
        RenderingServer.GlobalShaderParameterAdd("csky_fog_alt",
            RenderingServer.GlobalShaderParameterType.Vec2, new Vector2(1e8f, 1e9f));
        // The fullbright world's per-mission brightness from the weather's SUNLIGHT:
        // 1.0 = fullbright (no darkening) for static views / missions without weather; --fly
        // overrides it from WeatherState.WorldLight below.
        RenderingServer.GlobalShaderParameterAdd("csky_world_light",
            RenderingServer.GlobalShaderParameterType.Float, 1.0f);
        // The same SUNLIGHT uncollapsed, for the camera-facing cloud cards the collapse cannot
        // describe (docs/org/vertexLighting.md). The defaults are the no-directional-term state,
        // ambient 1 and diffuse 0, so a view without mission weather draws a card as authored.
        RenderingServer.GlobalShaderParameterAdd("csky_sun_dir",
            RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0f, 1f, 0f));
        RenderingServer.GlobalShaderParameterAdd("csky_sun_light",
            RenderingServer.GlobalShaderParameterType.Vec2, new Vector2(1f, 0f));
        // The same pair with its colours (WeatherRig.SunVertexLight), which the faithful aircraft
        // reads. SetupLighting replaces these registration defaults with the day pair.
        RenderingServer.GlobalShaderParameterAdd("csky_sun_ambient_rgb",
            RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        RenderingServer.GlobalShaderParameterAdd("csky_sun_diffuse_rgb",
            RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        // The Danger Zone photograph's ambient half for its own pilot (WeatherRig.PhotographFill);
        // ambient 1 fills to 1, so the default agrees with the ambient default above.
        RenderingServer.GlobalShaderParameterAdd("csky_sun_fill_rgb",
            RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        // The world sampler's mip LOD bias. 0 is the original's own device default, so a chapter
        // authoring no MipBias renders exactly as it did (docs/org/textures.md).
        RenderingServer.GlobalShaderParameterAdd("csky_mip_bias",
            RenderingServer.GlobalShaderParameterType.Float, 0.0f);
        // Which keymap a seat is built on, resolved before the first seat exists. Both flags close
        // the door for the same reason: a run whose result is compared against a committed golden
        // must not depend on the keymap saved at whoever's machine ran it (verification.md, DET-8).
        CSVM.Bindings.LaunchBindings.Configure(_spec.Det || _spec.RunTests);

        // After the --det block, so ClearOverrides has dropped a config graphics.mode; ahead of the
        // clutter fade, which needs the mode to follow the pushed fog. ⚠ --det reads no saved
        // option either: options.json is one machine's state (docs/cli.md's --graphics bullet).
        string? savedGraphics = _spec.Det ? null : OptionsStore.UserOptions().Load().GraphicsMode;
        bool graphicsEnhanced = Utils.GraphicsMode.Resolve(_spec.GraphicsMode, savedGraphics);
        Log.Info("world", $"graphics mode: {Utils.GraphicsMode.Key}={(graphicsEnhanced ? "enhanced" : "original")}");
        // The graphics EffectsLevel's one global: the clutter fade's squared distance scale, 0 when
        // the fade is off. Enhanced mode pushes the fade out by the fog range's own factor (the
        // scale shrinks) so clutter reaches the pushed haze; original mode's factor is identity.
        float clutterFadeScaleSq = Utils.EffectsLevel.ResolveClutterFadeScaleSq(WeatherRig.EnhancedFogScale());
        RenderingServer.GlobalShaderParameterAdd(Utils.EffectsLevel.ShaderParam,
            RenderingServer.GlobalShaderParameterType.Float, clutterFadeScaleSq);
        string clutterFarFade = Utils.EffectsLevel.ClutterFarFadeEnabled() ? "true" : "false";
        Log.Info("world", $"clutter fade: {Utils.EffectsLevel.FadeKey}={clutterFarFade} {Utils.EffectsLevel.Key}={Config.GetString(Utils.EffectsLevel.Key, Utils.EffectsLevel.Default)} scale_sq={clutterFadeScaleSq}");
        // The animated world's LIGHT_STATE point lights. Defaults to an empty set, so a session
        // with no lit animations renders exactly as it did before they existed.
        WorldLights.RegisterGlobals();
        // Registered before the --dump-* branches below, which build materials of their own:
        // registering after them left every dump run emitting a missing-global error.
        ShaderTime.RegisterGlobal();

        // --dump-markers: a pure-data report, print the marker rig tables and quit. Runs
        // whether or not a content arg was given; --headless makes it windowless. Each dump
        // quits with its own verdict, like --run-tests, never a false-clean exit code.
        if (_spec.DumpMarkers)
        {
            GetTree().Quit(_probeRunner.DumpMarkers(_spec) ? 0 : 1);
            return;
        }
        // --dump-weapons: the same pure-data pattern for the typed weapons.json reader,
        // dump every def and assert no key went unmapped.
        if (_spec.DumpWeapons)
        {
            GetTree().Quit(_probeRunner.DumpWeapons(_spec) ? 0 : 1);
            return;
        }
        // --dump-loadout: bind each plane's stock loadout to its built model and report the
        // resolved gun groups + hardpoints, a missing marker is a loud error here.
        if (_spec.DumpLoadout)
        {
            GetTree().Quit(_probeRunner.DumpLoadout(_spec) ? 0 : 1);
            return;
        }
        // --dump-flight: pure data again, no world and no model, just the zrdr stats stepped
        // through the manoeuvres the original was measured flying.
        if (_spec.DumpFlight)
        {
            GetTree().Quit(_probeRunner.DumpFlight(_spec) ? 0 : 1);
            return;
        }
        // --dump-mips: what the texture archive's mip chains actually hold, level by level, beside
        // the authored levels they should be, the before/after instrument for --mips=.
        if (_spec.DumpMips)
        {
            GetTree().Quit(_probeRunner.DumpMips(_spec) ? 0 : 1);
            return;
        }
        // --dump-ai: the five AI data families read straight off the extraction, no world, no
        // scene, no readers built for the families that don't have one yet (aiv/ai.zrd/zeppelins/
        // egen). See Probes.Ai.
        if (_spec.DumpAi)
        {
            GetTree().Quit(_probeRunner.DumpAi(_spec) ? 0 : 1);
            return;
        }

        // Exercises the wired modules once so Config's tuning registry is complete, then flags
        // any config.json key no tunable matched, data-free, so a typo is caught before flight.
        TuningWarmup.Run();
        Config.ReportOrphans();
        // --dump-config: write a fully-populated tuning template (every registered key + its default,
        // nested by block) to the scratch folder and quit, the copy-and-edit source for config.json.
        if (_spec.DumpConfig)
        {
            string dumpPath = Path.Combine(_repoRoot, ".scratch", "config.dump.json");
            Config.DumpConfig(dumpPath);
            Log.Info("core", $"config: wrote {Config.RegisteredCount}-key tuning template to ./.scratch/config.dump.json");
            GetTree().Quit();
            return;
        }

        // Gamepad hotplug: every input read polls Pads.Connected() fresh, so a pad plugged in
        // mid-game works the moment the engine reports it. Log the roster at launch and every
        // connect/disconnect so a silent pad is diagnosable from the console.
        if (Pads.Disabled)
            Log.Info("core", $"gamepad: off (--no-pads, or the --det bundle), ignoring every device (keyboard/scripted input only)");
        else
            Input.Singleton.JoyConnectionChanged += (device, connected) =>
            {
                if (connected)
                    Log.Info("core", $"gamepad connected: device {device} \"{Input.GetJoyName((int)device)}\" guid={Input.GetJoyGuid((int)device)}");
                else
                    Log.Info("core", $"gamepad disconnected: device {device}");
            };
        var padsAtLaunch = Pads.Connected();
        if (Pads.Disabled)
        {
            // nothing more to report, the roster is deliberately empty
        }
        else if (padsAtLaunch.Count == 0)
            Log.Info("core", $"gamepad: none at launch (hotplug live, connect any time)");
        else
            foreach (int p in padsAtLaunch)
                Log.Info("core", $"gamepad: device {p} \"{Input.GetJoyName(p)}\" guid={Input.GetJoyGuid(p)} info={Input.GetJoyInfo(p)}");

        SetupLighting();
        // GameSession re-applies these same two framings per launch. The decoded world base serves
        // every camera that draws the world, and the viewer's own 50 a model on a stage.
        _camera = new Camera3D
        {
            Fov = _spec.Fly || _spec.Freecam || _spec.AnimLab ? CameraController.ExternalFovDeg : 50f,
            Far = 40000f,
        };
        AddChild(_camera);
        _orbit = new OrbitCamera(_camera);
        if (_spec.Yaw is { } argYaw) _orbit.Yaw = argYaw;
        if (_spec.Pitch is { } argPitch) _orbit.Pitch = argPitch;

        // --run-tests: the in-engine assertion suites. Dispatched here, after the camera exists (a
        // suite building a world resolves PLAYER_RANGE from it) and before any session is built,
        // the suites build exactly the world/plane each of them needs and nothing else.
        if (_spec.RunTests)
        {
            int code = _probeRunner.RunTestSuites(_spec, this, _camera, out _clock);
            GetTree().Quit(code);
            return;
        }

        // The F14 / --debug-fps readout, process-wide like the camera so it works at the
        // launchscreen too. Built after the --run-tests/--dump-* early exits, which render no
        // frame it would have anything to show.
        _perfHud = new UI.Overlays.PerfHud
        {
            InitialMode = _spec.DebugFps == null ? UI.Overlays.PerfHud.Mode.Off : UI.Overlays.PerfHud.ParseMode(_spec.DebugFps),
            Monitor = _hitchMonitor,
        };
        AddChild(_perfHud);

        // The version stamp on the menu, process-wide for the same reason and built beside it: the
        // build a capture came from is a fact about the binary, not about a presentation.
        _buildStamp = new UI.Screens.BuildStamp();
        AddChild(_buildStamp);

        // The music channel, once per process and after every early-quit probe: one player that
        // outlives every session, over a sound archive of its own for the same reason (D37's
        // wiring contract, step 1).
        BuildMusic();

        // --movie=: one cinema, then quit. No world, no menu, and no session behind it, which is
        // what makes the audio sync judgeable at the controls before any flow plays a cinema.
        if (_spec.MovieName is { } cinemaName)
        {
            PlayCinema(cinemaName, () => GetTree().Quit());
            return;
        }

        // No content-selecting arg (or explicit --menu): show the launchscreen. Its selection
        // derives the session spec and calls LaunchSession, so there is one downstream build
        // path; Esc from a menu-launched flight returns here (ReturnToMenu).
        if (_spec.ShowsMenu)
        {
            // Only on the path a recipient takes. Every other entry carries a content arg, which
            // is a developer's launch, and the log is where that reader already looks.
            if (UI.Screens.NoGameDataScreen.Missing(_dataRoot))
            {
                ShowNoGameData();
                return;
            }

            _menuDriven = true;
            if (_spec.PlaysBootSequence)
            {
                PlayBootSequence(() => ShowMenu(MenuReturnDestination.TopLevel));
                return;
            }

            ShowMenu(MenuReturnDestination.TopLevel);
            return;
        }

        // --debug-load stands the real screen over a CLI launch, deferred build and all. That is
        // the only way to watch the bar with nobody at the menu.
        if (_spec.DebugLoad != null)
        {
            BeginLaunch();
            return;
        }

        // A CLI launch has no load screen, so the cover is the whole of what stands between the
        // build and the session's first real frame. Same rule as the interactive paths: a session
        // starts from dark, whatever opened it.
        RaiseStartCover();
        LaunchSession();
    }

    public override void _Notification(int what)
    {
        // The APPLICATION_* pair, not WM_WINDOW_*: that's what a real focus change delivers
        // here. See docs/verification.md's SHELL-14 for the alt-tab-vs-minimise gotcha this was
        // measured against. Godot 4's constants are longs; _Notification hands us an int.
        if (what == (int)NotificationApplicationFocusOut)
        {
            SetFocusMuted(true);
        }
        else if (what == (int)NotificationApplicationFocusIn)
        {
            SetFocusMuted(false);
        }
    }

    // The root node's own teardown, reached on an ordinary quit
    // (GetTree().Quit() or the window's close button), never on a kill/crash, which is what the
    // sidecar's flush-interval loss bound (HitchSidecar's own doc) covers instead.
    public override void _ExitTree()
    {
        _hitchSidecar.Flush();
        _musicArchive?.Dispose();
        _musicArchive = null;
        MirrorEngineLog();
    }

    public override void _Input(InputEvent @event)
    {
        // The wheel is an event, never a held state, so it is counted here and handed to the
        // menu seat's poll; nothing else about it is read while the menu is up.
        if (_menuHost is { Shown: true } && @event is InputEventMouseButton { Pressed: true } wheel)
        {
            switch (wheel.ButtonIndex)
            {
                case MouseButton.WheelDown:
                    _menuWheel++;
                    break;
                case MouseButton.WheelUp:
                    _menuWheel--;
                    break;
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            // While the menu is up it owns Esc (back / quit from the Mode screen).
            if (_menuHost is { Shown: true })
                return;
            // ⚠ Esc no longer leaves a live flight; it opens the pause board, whose Exit item does.
            // FlightController polls it as a pause toggle, so nothing is done here.
            if (_session is { InSession: true })
                return;
            BlankAndQuit();
            return;
        }
        // F12 anywhere (orbit view or free flight): grab the current frame to a file.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F12 })
        {
            Tooling.CaptureDirector.SaveScreenshot(GetViewport());
            return;
        }
        // F11 anywhere: print the mode's subject placement as ready-to-paste --pos=/--direction=
        // args, so a hand-framed orbit (or a spot found while flying) can be reproduced for a
        // deterministic --screenshot run.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F11 })
        {
            _captureDirector.PrintPlacement(_spec, _session?.Rigs ?? NoRigs, _camera, _orbit);
            return;
        }
        // F10 in the viewer: export the plane on screen, current livery and damage state baked
        // in, to a timestamped .glb under the repo's git-ignored Exports/ folder.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10 })
        {
            Tooling.GltfExporter.ExportToExports(_session?.Plane, _spec.PlaneName);
            return;
        }
    }

    public override void _Process(double delta)
    {
        // The --run-tests clock is the only one this node owns; the session node advances its own
        // at the very top of the frame (ProcessPriority -1000, one notch ahead of this).
        if (_clock is { } clock)
        {
            clock.BeginFrame(delta);
        }
        // Publishes the frame's instant to the shaders so animated surfaces and the CPU sim
        // never disagree. Written unconditionally: with no session clock it runs on wall time,
        // so nothing stalls behind the menu.
        ShaderTime.Advance(ClockNow, delta);
        // Both instruments stay on wall time: an instrument that freezes with the thing it measures
        // reports nothing. One counter read per frame feeds both: the hitch monitor wants them
        // unaveraged and the --perf window wants them summed, but they are the same eight numbers.
        var counters = ReadFrameCounters();
        // Fires one QPC read before the stamp below, so the stall inflates THIS frame's wall
        // cost. Matched against HitchMonitor's own frame counter, never the sim frame: the
        // injector has to work with no session built at all.
        if (_spec.HitchInjectMs is float injectMs
            && _hitchMonitor.FrameCount + 1 == _spec.HitchInjectFrame)
        {
            InjectHitch(injectMs, _spec.HitchInjectAlloc);
        }
        // Detection is unconditional; logging is not, a hitch nobody watched for is what this
        // catches. Fed our own QPC pair, never Godot's post-processed `delta`, which measures
        // as a quantised constant.
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        double frameMs = _lastFrameStamp == 0
            ? 0
            : (stamp - _lastFrameStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastFrameStamp = stamp;
        // Closes the attribution window at the same instant the wall cost is stamped, so the
        // scopes that ran and the frame_ms they ran inside describe the same span. The session
        // node processes one notch ahead (-1000), so its declared work is already in.
        PerfSample.EndFrame();
        // B6: a trip queues its record (a cheap, preallocated copy) rather than writing anything
        // here, the sidecar's own Tick below drains the queue a few quiet frames later.
        if (_hitchMonitor.Tick(frameMs, counters))
        {
            _hitchSidecar.Enqueue(_hitchMonitor.Last);
        }
        _hitchSidecar.Tick(frameMs);
        // Fed the same wall cost the monitor above gets, and unconditional where --perf is opt-in:
        // the rate a player actually saw is the one thing a report from someone else's machine
        // cannot be reconstructed without.
        ReportRate(frameMs);
        // Early-quit probes do not construct the readout, but Godot may process one shutdown frame.
        _perfHud?.Tick(frameMs, counters);
        _buildStamp?.Tick(_menuHost is { Shown: true });
        if (_spec.Perf)
        {
            (_gcTrace ??= Utils.GcTrace.Create(_spec.GcTypes)).Tick();
            ReportPerf(delta, counters);
        }

        // Wall time, like the instruments above: the score is not part of the simulation, and a
        // paused or stepped session must not stall a fade halfway.
        _music?.Tick((float)delta, _musicRng);

        _captureDirector.Tick(GetViewport(), GetTree(), _spec, ClockNow, _orbit, _camera,
            _session?.Plane, _menuHost is { Shown: true });
        _gltfExporter.Tick(_session?.Plane, GetTree(), _spec);

        // A campaign mission that ended during the session's own step: free it and reopen the
        // launchscreen on the debrief, here where a QueueFree is safe.
        if (_pendingDebrief is { } debrief)
        {
            _pendingDebrief = null;
            OpenDebrief(debrief.Profile, debrief.Result);
        }

        // The same handover for an Instant Action mission whose presentation carries a wrap-up page.
        if (_pendingWrapup is { } wrapup)
        {
            _pendingWrapup = null;
            Log.Info("core", $"ia: {(wrapup.Won ? "COMPLETE" : "FAILED")}: arrived at the menu's wrap-up page");
            ReturnToMenu(new InstantActionWrapupReturn(wrapup));
        }

        // An Options apply, one frame after the exit that asked for it.
        if (_pendingApply is { } applied)
        {
            _pendingApply = null;
            ApplyOptions(applied);
        }

        // Dropped here rather than by the cover itself, so one node owns both screens a launch
        // raises. It ticks after this node, so what is read is the state it last painted.
        if (_startFade is { Finished: true })
        {
            DropStartCover();
        }

        // Last in the frame, where the build used to happen anyway: the launchscreen and the boards
        // are children, so they process AFTER this node, and a build they asked for landed here.
        RunOwedLaunch();
        // After everything above, which is where the launchscreen's own process callback ran when
        // it was a child ticking itself: the capture director reads the menu as it stood before
        // this frame's presses, as it always did.
        _menuHost?.Tick(MenuStep(delta));
    }

    /// <summary>Plays one cinema over the whole window and runs <paramref name="then"/> on the
    /// frame it stops, whether it played out or was skipped. A name that resolves to no readable
    /// file runs the continuation straight away, so a flow costs a screen rather than stalling on
    /// a cinema this install does not carry. This is the seam every movie sequence goes
    /// through.</summary>
    public void PlayCinema(string name, System.Action then, UI.Screens.CinemaSkip skip = UI.Screens.CinemaScreen.BootKeys)
    {
        if (UI.Screens.CinemaScreen.Open(_dataRoot, name, skip) is not { } cinema)
        {
            Log.Warn("ui", $"cinema {name} not played; looked under {SessionPaths.CinemaFolder(_dataRoot)}");
            then();
            return;
        }

        // A repo run's developer gain defaults to zero, and a cinema whose whole point is its
        // sound track is the one place that reads as a defect rather than as a quiet run.
        if (MasterVolume.Resolve(_spec.Volume, _exported) <= 0f)
        {
            Log.Warn("sound", $"cinema {name} is playing at master volume 0, pass --volume=1.0 to hear it");
        }

        Log.Info("ui", $"cinema {name} playing skip={skip}");
        cinema.Ended = then;
        AddChild(cinema);
    }

    // Where a flight left early lands, taken from the launch that starts it. Every menu launch path
    // writes it here, ExitSession reads it back, and the rule stands in one place.
    // ⚠ Keep it internal rather than private. Nothing instantiates a Launcher headlessly, so the
    // launch-return suite pins this round trip on the live node or not at all.
    internal void LaunchedFrom(MenuExit exit) => ExitDestination = MenuReturnDestination.ForLaunch(exit);

    // Runs on a worker thread, since the extraction must not hold the main thread. The per-frame
    // callbacks are switched off until the quit, because _Ready returned before building what they read.
    private void StartHeadlessExtraction(string install)
    {
        SetProcess(false);
        SetPhysicsProcess(false);
        SetProcessInput(false);
        SetProcessUnhandledInput(false);
        string unzbd = _spec.UnzbdPath is { } named ? Path.GetFullPath(named) : Extraction.ExtractionRun.DefaultUnzbd(_repoRoot, _exported);
        var request = new Extraction.ExtractionRequest(install, _dataRoot, unzbd, _spec.ExtractForce, _spec.ExtractUnzip);
        System.Threading.Tasks.Task.Run(() =>
        {
            int code;
            try
            {
                code = Extraction.ExtractionRun.RunToConsole(request, Log.Raw, line => Log.Error("core", $"{line}"));
            }
            catch (System.Exception e)
            {
                // Anything unforeseen still ends the process with a verdict, never a hang.
                Log.Error("core", $"extraction crashed", e);
                code = 1;
            }

            Callable.From(() => GetTree().Quit(code)).CallDeferred();
        });
    }

    // The boot sequence in fmv.zrd's own order, its card and waits and fade included: the reader's
    // eight actions live in BootSequence and not one of them is written down here. A press ends the
    // action it lands in and the next begins. Whether the original abandoned the rest of the block
    // instead is not decoded (docs/formats/cinemas.md).
    private void PlayBootSequence(System.Action then) =>
        UI.Screens.BootCard.Play(this, _dataRoot, PlayCinema, then);

    // The step the menus advance on. A deterministic run gives them the sim's own, for the reason
    // the sim takes it: what a capture shows must be a function of the frame count and nothing
    // else, and the screens that animate (the briefing's reveal, a board's background movie)
    // otherwise land wherever this machine's frame times put them.
    private float MenuStep(double delta) => _spec.Det ? GameClock.FixedDt : (float)delta;

    // The process's one music channel and the archive it streams from. Everything here is
    // optional: an install without soundsh or without a readable sounds.json leaves the game
    // silent, which is what a missing extraction has always meant.
    /// <summary>Copies Godot's own log next to ours in <c>.scratch/logs/</c> on an ordinary quit,
    /// so one folder is the whole bug report. The file sink takes <see cref="Log"/> calls only, so
    /// a managed exception escaping a callback reaches the engine's log and NOT ours.
    /// ⚠ Best effort throughout, and never on a crash: the engine holds the file open, and nothing
    /// here may take the quit with it.</summary>
    private void MirrorEngineLog()
    {
        if (Log.SinkPath is not { } sinkPath)
        {
            return;
        }
        string source = Path.Combine(OS.GetUserDataDir(), "logs", "godot.log");
        string target = Path.ChangeExtension(sinkPath, ".godot.log");
        try
        {
            if (!File.Exists(source))
            {
                return;
            }
            // Share ReadWrite: the engine's own writer is still open on it, and a plain
            // File.Copy would fail on Windows against that handle.
            using var src = new FileStream(source, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(target, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
            Log.Info("core", $"engine log mirrored from={source} to={target} bytes={dst.Length}");
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException
                                             or System.NotSupportedException)
        {
            Log.Warn("core", $"engine log mirror failed from={source} error={e.GetType().Name}: {e.Message}");
        }
    }

    private void BuildMusic()
    {
        try
        {
            _musicArchive = new SoundArchive(_soundsPath);
            _music = new MusicPlayer(SoundDefs.Load(_zrdrPath), SoundDefs.LoadGroups(_zrdrPath))
            {
                Loader = (def, looped) => _musicArchive.Find(def.WavName, looped, warn: false),
            };
            AddChild(_music);
            _musicRng = new System.Random((int)(_masterSeed & 0x7fffffff));
        }
        catch (System.Exception e)
        {
            _music = null;
            _musicArchive = null;
            Log.Warn("sound", $"music: no channel this process ({e.GetType().Name}: {e.Message})");
        }
    }

    // The deferred half of BeginLaunch: builds once the load screen has had a frame to render.
    // A QueueFree'd session is gone by now too, so the outgoing world's exit-tree duties (the
    // published clock, the world lights, the camera restore) cannot land on top of the new one.
    private void RunOwedLaunch()
    {
        // This is the load screen's second half. The session exists, and what it ordered for the
        // load is carried out a step a frame behind the same screen. A step is one wave aeroplane,
        // so the launch frame that needs it later binds a finished one instead of building it.
        if (_loadStepsRun >= 0)
        {
            if (_session is { } loading && loading.StepOwedLoad())
            {
                _loadStepsRun++;
                return;
            }

            Log.Info("ui", $"load screen: {_loadStepsRun} owed build step(s) run behind the screen");
            _loadStepsRun = -1;
            HideLoadScreen();
            return;
        }

        if (_launchFramesWaited < 0 || _launchFramesWaited++ < 1)
        {
            return;
        }
        _launchFramesWaited = -1;
        bool built = LaunchSession();
        // The screen stays up while the build's own owed steps run, and comes down on the frame
        // they finish. A load screen left up past that would draw over the first frame of the
        // world, and over a --screenshot capture.
        if (built && _session is { } loaded && loaded.StepOwedLoad())
        {
            _loadStepsRun = 1;
            return;
        }
        HideLoadScreen();
        if (!built)
        {
            // Nothing to uncover: the cover would otherwise hold over whatever the failure left on
            // screen for the whole of its cap.
            DropStartCover();
        }

        if (built || !_menuDriven)
        {
            return; // a CLI launch leaves the log to tell the story, as it always did
        }
        ReturnToMenu(MenuReturnDestination.TopLevel);
        // Built-in's error line is its own; Original has no note and its top level shows bare, so
        // the log carries the fact for both presentations.
        Log.Warn("ui", $"menu: the build failed, back at the top level of {_menuHost?.Selected}");
        BuiltInMenu?.ShowError($"Could not load {_spec.Chapter} / {string.Join(", ", _spec.PlaneNames)}, see the log.");
    }

    // Shows the load screen and owes a build from the next frame. Every interactive path in (the
    // launchscreen's Fly, the boards' Restart) comes through here; the CLI launch in _Ready does
    // not, so no scripted or golden run gains a frame it did not have before.
    private void BeginLaunch()
    {
        // The one place the session leaves the boards for a mission, so the one place the menu
        // score stops. What plays next is the mission's own business: a campaign mission cues
        // prebattle from its objectives graph, and Instant Action ships silent (docs/org/music.md).
        _music?.Stop();
        RaiseStartCover();
        ShowLoadScreen(_spec.CampaignProfile != null, _spec.IaDef?.MissionType);
        _launchFramesWaited = 0;
    }

    // The cover goes up before the load screen that hides it. The frame the screen comes down on is
    // then already covered, and no frame between the two shows the world. Replacing a cover still
    // up (a relaunch straight out of a session) starts the hold again, which is what a fresh build
    // wants.
    private void RaiseStartCover()
    {
        DropStartCover();
        _startFade = UI.Screens.SessionStartFade.Build(
            _spec.Det, () => _session is { InSession: true, FirstFrameReady: true });
        if (_startFade != null)
        {
            AddChild(_startFade);
        }
    }

    private void DropStartCover()
    {
        if (_startFade == null)
        {
            return;
        }

        RemoveChild(_startFade);
        _startFade.QueueFree();
        _startFade = null;
    }

    // The load screen over the whole window, on the board layer. A campaign launch takes the
    // original's chart sheet for the mission being built and everything else its blackboard
    // (docs/org/loading-screen.md).
    private void ShowLoadScreen(bool campaign, string? missionType, int? missionSeq = null)
    {
        // The blackboard writes its dialog's own four texts, and takes this heading only when the
        // mode is ours and no dialog describes it. The chart sheet writes no words of ours at all.
        string subject = campaign ? string.Empty : LaunchSubject().ToUpperInvariant();
        var sheet = campaign
            ? CampaignLoadSheet(missionSeq ?? _spec.CampaignMissionSeq ?? 0)
            : null;
        _loadLayer = new CanvasLayer { Name = "load_board", Layer = UI.Boards.HudLayers.Board };
        var board = UI.Screens.LoadBoard.Build(
            _dataRoot, _zrdrPath, _messagesPath, campaign, subject, missionType, sheet);
        board.CaptureDir = _spec.DebugLoad ?? string.Empty;
        _loadLayer.AddChild(board);
        AddChild(_loadLayer);
    }

    // The chart sheet a story position resolves: the loading dialog the mission's own storage
    // address names, that mission's objectives for the parchment, and the profile's memento.
    private UI.Screens.LoadSheet? CampaignLoadSheet(int seq)
    {
        if (CampaignMissionAt(seq) is not { } named)
        {
            Log.Warn("ui", $"load screen: cm_sequence names no mission at seq {seq}");
            return null;
        }

        string key = UI.Menu.EscapeDialog.CampaignKey(named.Campaign, named.Mission);
        var sheet = UI.Screens.LoadSheet.Load(
            _zrdrPath, _messagesPath,
            SessionPaths.MissionZrdr(_dataRoot, named.ChapterFolder, named.MissionFolder),
            key, SeatedMemento());
        if (sheet == null)
        {
            Log.Warn("ui",
                $"load screen: Loading.zrd has no sheet for {named.ChapterFolder}/{named.MissionFolder}");
            return null;
        }

        // The screen is torn down before the world appears, so this line is the only record of
        // which mission's chart a launch actually drew.
        string map = sheet.State.Map?.Bitmap ?? "-";
        Log.Info("ui",
            $"load screen: {named.ChapterFolder}/{named.MissionFolder} {key} map={map} objectives={sheet.Objectives.Count}");
        return sheet;
    }

    // The mission a sequence position names, or null where cm_sequence carries none.
    private CampaignMission? CampaignMissionAt(int seq)
    {
        CampaignMission? found = null;
        foreach (var mission in Mech3.CampaignSequence.Load(_zrdrPath))
        {
            if (mission.Seq == seq)
            {
                found = mission;
            }
        }

        return found;
    }

    // The Original presentation's pause sheet over the whole window, standing on its own with no
    // mission behind it. The objectives are the named mission's own and the ownship icon sits on
    // its authored PLAYER_INIT spawn, so nothing on the screen is invented; there is no world, so
    // the zeppelin icon is absent. docs/org/pause-screen.md.
    private void ShowPauseSheet(int ordinal, int completed)
    {
        int last = System.Math.Max(0, Mech3.CampaignSequence.Load(_zrdrPath).Count - 1);
        int seq = System.Math.Clamp(ordinal - 1, 0, last);
        if (CampaignMissionAt(seq) is not { } named)
        {
            Log.Warn("ui", $"pause aid: cm_sequence names no mission at seq {seq}");
            return;
        }

        string missionZrdr = SessionPaths.MissionZrdr(_dataRoot, named.ChapterFolder, named.MissionFolder);
        var sheet = UI.Boards.PauseSheet.Load(
            _zrdrPath, _messagesPath,
            UI.Menu.EscapeDialog.CampaignKey(named.Campaign, named.Mission), instantAction: false);
        if (sheet == null)
        {
            Log.Warn("ui", $"pause aid: no escape.zrd sheet for {named.ChapterFolder}/{named.MissionFolder}");
            return;
        }

        var readout = PauseAidReadout(sheet, missionZrdr, completed);
        var pause = new Flight.Modes.PauseState();
        var board = OriginalPauseBoard.Build(
            pause, _ => new UI.Screens.MenuInput { Keyboard = true }, _dataRoot, sheet, () => readout);
        var layer = new CanvasLayer { Name = "pause_board_aid", Layer = UI.Boards.HudLayers.Board };
        layer.AddChild(board);
        AddChild(layer);
        pause.TryToggle(0);
        Log.Info("ui",
            $"pause aid: {named.ChapterFolder}/{named.MissionFolder} sheet with {completed} objective(s) marked");
    }

    // The Instant Action sortie's own pause sheet with no sortie behind it: the blackboard its
    // chapter and mission type resolve, which carries no map, memento or parchment and so needs no
    // readout at all. docs/org/pause-screen.md.
    private void ShowInstantActionPauseSheet(string chapter, string missionType)
    {
        if (UI.Screens.LoadScreens.LetterFor(missionType) is not { } letter)
        {
            Log.Warn("ui", $"pause aid: '{missionType}' is no Instant Action mission type");
            return;
        }

        string key = UI.Menu.EscapeDialog.InstantActionKey(
            Mech3.CampaignSequence.ChapterNumber(chapter), letter);
        var sheet = UI.Boards.PauseSheet.Load(_zrdrPath, _messagesPath, key, instantAction: true);
        if (sheet == null)
        {
            Log.Warn("ui", $"pause aid: no ia_escape.zrd sheet for {chapter} {missionType} ({key})");
            return;
        }

        var pause = new Flight.Modes.PauseState();
        var board = OriginalPauseBoard.Build(
            pause, _ => new UI.Screens.MenuInput { Keyboard = true }, _dataRoot, sheet,
            () => UI.Boards.PauseReadout.Empty);
        var layer = new CanvasLayer { Name = "pause_board_aid", Layer = UI.Boards.HudLayers.Board };
        layer.AddChild(board);
        AddChild(layer);
        pause.TryToggle(0);
        Log.Info("ui", $"pause aid: {chapter} {missionType} draws {sheet.State.Key}");
    }

    private UI.Boards.PauseReadout PauseAidReadout(UI.Boards.PauseSheet sheet, string missionZrdr, int completed)
    {
        var objectives = UI.Menu.BriefingObjectives.Load(
            Mech3.Zrdr.LoadFile(missionZrdr, "objectives.json"), Mech3.Messages.Load(_messagesPath));
        var rows = new List<UI.Boards.PauseObjective>(objectives.Count);
        for (int i = 0; i < objectives.Count; i++)
        {
            rows.Add(new UI.Boards.PauseObjective(objectives[i].Text, i < completed));
        }

        var icons = new List<UI.Boards.PauseWorldIcon>();
        if (sheet.Shared.OwnShip.Length > 0
            && Flight.Modes.SpawnPoints.LoadPlayerInit(missionZrdr) is { } init)
        {
            // Through the readout's own conversion off a nose vector, never off the spawn's heading
            // degrees. Those are the mission data's yaw, which runs opposite the compass.
            var nose = new Basis(Vector3.Up, Mathf.DegToRad(init.Spawn.HeadingDeg)) * Vector3.Forward;
            if (UI.Boards.PauseReadout.Icon(
                sheet.Shared.OwnShip, init.Spawn.Position.X, init.Spawn.Position.Z,
                nose.X, nose.Z) is { } ship)
            {
                icons.Add(ship);
            }

            if (sheet.State.Map is { } map
                && !map.TryProject(init.Spawn.Position.X, init.Spawn.Position.Z, out _))
            {
                Log.Info("ui",
                    $"pause aid: the mission's spawn sits off the chart's window, so no ownship icon draws");
            }
        }

        return new UI.Boards.PauseReadout(rows, SeatedMemento(), icons);
    }

    // The picture the seated profile hangs, read back off the store the cabin's chooser writes.
    // This screen, a real pause and the cabin wall all draw the one name. A launch or a door with
    // no profile behind it draws the seeded keepsake (docs/org/pause-screen.md).
    private string SeatedMemento() =>
        CampaignMementos.BitmapFor(
            _spec.CampaignProfile is { } name
                ? CampaignProfileStore.UserProfiles().Load(name)
                : null);

    // What the load screen calls this flight: an Instant Action mission by the wizard's own name
    // for it ("Attacking a Zeppelin"), anything else by its mode. ⚠ Not ModeName, that is the log
    // file's and the startup line's internal tag ("fly", "stunt"), which is not a player's word.
    private string LaunchSubject()
    {
        if (_spec.IaDef is { } def)
        {
            return Mech3.InstantAction.MissionTypeLabel(def.MissionType);
        }
        return _spec.Versus ? "Dogfight" : "Free Flight";
    }

    private void HideLoadScreen()
    {
        if (_loadLayer == null)
        {
            return;
        }
        RemoveChild(_loadLayer);
        _loadLayer.QueueFree();
        _loadLayer = null;
    }

    // Instantiates this launch's session node from the current _spec and
    // the persistent references, and runs its build. Returns the build's verdict; on false the
    // partial session node is left for the caller (the menu flow returns to the launchscreen, a
    // CLI launch leaves the log to tell the story, exactly as the single-root class did).
    private bool LaunchSession()
    {
        // Undoes the menu's black before anything reads the environment: the world's reflections
        // and the cockpit pass's copy of it both take the sky from here (Utils/WorldBackdrop.cs).
        WorldBackdrop.Sky(_env);
        // The saved gameplay options, read at every launch so an Options apply reaches the next
        // flight in the same process. The flag and --det rules are the spec's own.
        var saved = OptionsStore.UserOptions().Load();
        _spec = _spec.WithSavedDifficulty(saved.Difficulty).WithSavedNearestAfterKill(saved.NearestAfterKill)
            .WithSavedDefaultView(saved.DefaultView).WithSavedAutoHeadTurn(saved.AutoHeadTurn);
        LoadProgress.Report(LoadStep.RenderState);
        // Set per launch, not once at startup: a relaunch can change chapter, and the original
        // re-sources the new chapter's adjust.gw at the same point.
        float mipBias = Mech3.TextureArchive.MipBias(_interpPath, _spec.Chapter);
        RenderingServer.GlobalShaderParameterSet("csky_mip_bias", mipBias);
        Log.Info("world", $"mip bias: {_spec.Chapter} adjust.gw MipBias={mipBias.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
        LoadProgress.Report(LoadStep.ChapterPaths);
        _session = new GameSession(_spec, new LauncherContext
        {
            RepoRoot = _repoRoot,
            DataRoot = _dataRoot,
            PlanesGamezPath = _planesGamezPath,
            ZrdrPath = _zrdrPath,
            SoundsPath = _soundsPath,
            InterpPath = _interpPath,
            MessagesPath = _messagesPath,
            RofPath = _rofPath,
            ProbeRunner = _probeRunner,
            CaptureDirector = _captureDirector,
            MasterSeed = _masterSeed,
            Camera = _camera,
            Orbit = _orbit,
            Sun = _sun,
            Env = _env,
            MenuDriven = _menuDriven,
            MenuPads = _menuPads,
            Presentation = SessionPresentation,
            ExitSession = ExitSession,
            RestartSession = RestartSession,
            PauseOptions = BuildPauseOptions,
            CampaignMissionEnded = _menuDriven
                ? (profile, result) => _pendingDebrief = (profile, result)
                : null,
            InstantActionWrapup = _menuDriven ? snapshot => _pendingWrapup = snapshot : null,
            Music = _music,
        });
        AddChild(_session);
        bool built = _session.StartSession();
        // A CLI launch has no load screen to yield behind. What the build ordered for the load runs
        // here, inside the same block as the rest of the build. The menu path steps it one a frame
        // with the screen still up instead (RunOwedLaunch).
        if (built && _loadLayer == null)
        {
            int steps = 0;
            while (_session.StepOwedLoad())
            {
                steps++;
            }

            if (steps > 0)
            {
                Log.Info("world", $"load: {steps} owed build step(s) run inside the build (no load screen on this launch)");
            }
        }
        // A build stalls the frame loop, and the frames either side of it are not neighbours, so
        // the hitch monitor drops its baseline here rather than reporting the build as a hitch.
        // Flushed first so nothing queued from before the build is held through it.
        _hitchSidecar.Flush();
        _hitchMonitor.Rearm();
        RearmRate();
        // C8: the build's own scopes (loads, material creation) belong to no frame, and the frame
        // that closes over the build would otherwise report them all at once.
        PerfSample.Reset();
        // Every bracket takes the same boundary. A build that spans the tail leaves a half-open
        // tick, pass, AI walk or phase. Its next close would charge the whole build to one step.
        PhysicsTickCost.Reset();
        ProcessPassCost.Reset();
        AiStepCost.Reset();
        SimPhaseCost.Reset();
        ProcessSiteCost.Reset();
        // D10: same reasoning as HitchMonitor.Rearm above, the build's own stall must never read
        // as the readout's worst recent frame.
        _perfHud.Rearm();
        return built;
    }

    private void SetupLighting()
    {
        _sun = new DirectionalLight3D
        {
            // The defaults only, hand-picked so the plane model reads in the viewer, the menu, and
            // any mission with no weather.json. A flight with weather overwrites the bearing and
            // the energy per zone-apply (WeatherRig.ApplyZone), off the zone's authored SUNLIGHT.
            RotationDegrees = new Vector3(-45, 150, 0),
            LightEnergy = WeatherRig.DefaultEnergies.Sun,
            // Off in the faithful path: the world is built fullbright and unshaded, so the only
            // thing a shadow pass reaches is one aircraft shadowing another, and the original's own
            // projected-blob shadow is not a shadow map either. Enhanced mode turns it on below.
            ShadowEnabled = false,
        };
        // Enhanced mode alone: a lit world has surfaces a shadow pass can land on.
        if (GraphicsMode.Enhanced)
            EnableSunShadows(_sun);
        AddChild(_sun);
        // The faithful aircraft's per-vertex term gets the same defaults, the modal day pair under
        // this bearing. A view without mission weather then shades a plane like a day zone would.
        (Vector3 dayDiffuse, Vector3 dayAmbient) = WeatherRig.DefaultSunlightRgb;
        RenderingServer.GlobalShaderParameterSet("csky_sun_dir",
            (_sun.IsInsideTree() ? _sun.GlobalBasis : _sun.Basis).Z.Normalized());
        RenderingServer.GlobalShaderParameterSet("csky_sun_ambient_rgb", dayAmbient);
        RenderingServer.GlobalShaderParameterSet("csky_sun_diffuse_rgb", dayDiffuse);
        RenderingServer.GlobalShaderParameterSet("csky_sun_fill_rgb",
            Vector3.One * WeatherRig.PhotographFillAmbient(WeatherState.DefaultAmbient));

        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            // ⚠ Colour-sourced in BOTH modes, never AmbientSource.Sky. A sky ambient fills a night
            // chapter's aircraft off the same daylight gradient a day one gets, and ignores the
            // pair written here. Per-zone values: WeatherRig.ApplyZone (docs/architecture.md).
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = WeatherRig.DefaultEnergies.Ambient,
        };
        // Enhanced mode alone: the faithful path's world is fullbright, so none of these passes has
        // anything to work on. The cockpit pass duplicates this Environment
        // (CockpitOverlay.NewOverlay) and inherits the settings.
        if (GraphicsMode.Enhanced)
        {
            UseMissionSky(_env);
            if (!Skipped(EnhancedPasses.Ssao))
            {
                EnableAmbientOcclusion(_env);
            }
            if (!Skipped(EnhancedPasses.Ssr))
            {
                EnableWaterReflections(_env);
            }
            if (!Skipped(EnhancedPasses.Glow))
            {
                EnableGlow(_env);
            }
            UseFilmicTonemap(_env);
        }
        AddChild(new WorldEnvironment { Environment = _env });
    }

    // Enhanced mode alone: the sky a reflection reads is the mission's own colour, not Godot's
    // procedural gradient. The dome is gamez geometry drawn over the background, so this is
    // normally unseen; what it feeds is the glossy water's specular. WeatherRig.WriteSkyColor
    // writes the flown zone's own FOG_COLOR over the default here on every zone apply. The ambient
    // is colour-sourced already, built that way above, so a flat panorama reaches reflection alone.
    private void UseMissionSky(Godot.Environment env)
    {
        env.Sky = new Sky { SkyMaterial = new PanoramaSkyMaterial() };
        WeatherRig.WriteSkyColor(env, EnhancedDefaultSkyColor);
    }

    // Every setting here is TUNE: nothing in the original authors a shadow map, so there is no
    // decoded magnitude to match. Four splits because the useful range spans an aircraft's own
    // shadow a few metres below it and a skyline several kilometres out.
    private void EnableSunShadows(DirectionalLight3D sun)
    {
        sun.ShadowEnabled = true;
        sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        sun.DirectionalShadowMaxDistance = EnhancedShadowMaxDistance;
        sun.DirectionalShadowFadeStart = EnhancedShadowFadeStart;
        sun.DirectionalShadowSplit1 = EnhancedShadowSplit1;
        sun.DirectionalShadowSplit2 = EnhancedShadowSplit2;
        sun.DirectionalShadowSplit3 = EnhancedShadowSplit3;
        sun.DirectionalShadowBlendSplits = true;
        sun.ShadowBias = EnhancedShadowBias;
        sun.ShadowNormalBias = EnhancedShadowNormalBias;
        // Both zero leaves a hard shadow edge rather than no shadow. That isolates the penumbra
        // filter, which is the part resolving with a screen-space sample pattern.
        bool hard = Skipped(EnhancedPasses.SoftShadows);
        sun.LightAngularDistance = hard ? 0f : EnhancedShadowAngularDistance;
        sun.ShadowBlur = hard ? 0f : EnhancedShadowBlur;
        // A renderer-wide setting rather than a light property. It is set here beside the width it
        // carries, not in project.godot, where the faithful path would inherit it.
        RenderingServer.DirectionalSoftShadowFilterSetQuality(
            hard ? RenderingServer.ShadowQuality.Hard : EnhancedShadowFilterQuality);
    }

    // Ambient occlusion, which darkens the ambient term where geometry occludes it. The faithful
    // path's world is fullbright, so this pass would find nothing there to occlude.
    private void EnableAmbientOcclusion(Godot.Environment env)
    {
        env.SsaoEnabled = true;
        env.SsaoRadius = EnhancedSsaoRadius;
        env.SsaoIntensity = EnhancedSsaoIntensity;
        env.SsaoPower = EnhancedSsaoPower;
        env.SsaoDetail = EnhancedSsaoDetail;
        env.SsaoHorizon = EnhancedSsaoHorizon;
        env.SsaoSharpness = EnhancedSsaoSharpness;
    }

    // Screen-space reflection, for the one glossy population in the world: the water surfaces
    // SceneBuilder.ClassifySurface names. Every other enhanced surface is matte, so nothing else
    // can reflect. ⚠ SSR reflects only what the camera already draws; content off-screen or behind
    // the near plane has no reflection at all.
    private void EnableWaterReflections(Godot.Environment env)
    {
        env.SsrEnabled = true;
        env.SsrMaxSteps = EnhancedSsrMaxSteps;
        env.SsrFadeIn = EnhancedSsrFadeIn;
        env.SsrFadeOut = EnhancedSsrFadeOut;
        env.SsrDepthTolerance = EnhancedSsrDepthTolerance;
    }

    // Enhanced mode alone. A lit world's real light energy feeds the HDR colour buffer, and
    // C21's glow-arm sprites are the only surfaces meant to bloom out of it.
    private void EnableGlow(Godot.Environment env)
    {
        env.GlowEnabled = true;
        env.GlowHdrThreshold = EnhancedGlowHdrThreshold;
        env.GlowBloom = EnhancedGlowBloom;
        env.GlowIntensity = EnhancedGlowIntensity;
        env.GlowStrength = EnhancedGlowStrength;
        env.GlowBlendMode = EnhancedGlowBlendMode;
        env.GlowHdrScale = EnhancedGlowHdrScale;
        env.GlowHdrLuminanceCap = EnhancedGlowHdrLuminanceCap;
    }

    // Enhanced mode alone: without it the HDR values a lit world produces clip instead of rolling
    // off. It is not a pass a bisect door closes, since every enhanced frame's exposure depends on
    // it. The cockpit pass duplicates this Environment at build time (CockpitOverlay.NewOverlay),
    // so its own tonemap matches the world pass exactly.
    private void UseFilmicTonemap(Godot.Environment env)
    {
        env.TonemapMode = EnhancedTonemapMode;
        env.TonemapExposure = EnhancedTonemapExposure;
        env.TonemapAgxWhite = EnhancedTonemapAgxWhite;
        env.TonemapAgxContrast = EnhancedTonemapAgxContrast;
    }

    // Whether this run asked for that enhanced pass to be left out. Closing one door at a time
    // bisects a full-screen artefact to the pass that draws it; docs/cli.md holds them.
    private bool Skipped(EnhancedPasses pass) => (_spec.SkippedPasses & pass) != 0;

    // The dead end for a launch with no extraction under the data root: the screen goes up and
    // nothing else is built, so the window carries the answer instead of the log. Esc leaves
    // through _UnhandledInput, which quits with neither a menu nor a session up.
    private void ShowNoGameData()
    {
        Log.Error("core", $"no extracted game data path={Path.Combine(_dataRoot, "extracted")}, {UI.Screens.NoGameDataScreen.Instruction(_exported)}");
        AddChild(UI.Screens.NoGameDataScreen.Build(_dataRoot, _exported));
    }

    // Shows the menu at a semantic destination, building the host on first use. Re-shown by
    // ReturnToMenu after the boards' Exit and a failed build, and by OpenDebrief after a campaign
    // mission. The --menu= aid is the cold start's alone: the presentation created inside the
    // first Show reads it, and it is consumed here so a return shows the destination itself.
    private void ShowMenu(MenuReturnDestination destination)
    {
        // A presentation is hidden a frame before the switch that replaces it, and nothing opaque
        // stands over the environment in between, so the menu owns a black background from its
        // first show until a launch takes it back (Utils/WorldBackdrop.cs).
        WorldBackdrop.Black(_env);
        _menuHost ??= BuildMenuHost();
        string aid = _menuAid ?? string.Empty;
        _menuHost.Show(destination);
        _menuAid = null;
        // The load screen is up for two frames during a build and torn down before anything
        // renders, so a shot of it needs a door of its own that leaves it standing. The campaign
        // door's argument names the mission, the blackboard's the Instant Action mission type.
        if (aid.StartsWith("loadboard-campaign", System.StringComparison.Ordinal))
        {
            var parts = aid.Split(':');
            ShowLoadScreen(
                campaign: true,
                missionType: null,
                missionSeq: System.Math.Max(
                    1, parts.Length > 1 && int.TryParse(parts[1], out int cm) ? cm : 1) - 1);
        }
        else if (aid.StartsWith("loadboard", System.StringComparison.Ordinal))
        {
            int colon = aid.IndexOf(':');
            string missionType = colon < 0 ? string.Empty : aid[(colon + 1)..];
            ShowLoadScreen(
                false, missionType.Length > 0 ? missionType : _spec.IaDef?.MissionType);
        }

        // The pause sheet stands only while a mission is halted, so a shot of it needs the same
        // kind of door. Its arguments name the campaign mission and how many of its objectives
        // have been marked, since that is the whole of what the reference stills differ by.
        if (aid.StartsWith("pauseboard-ia", System.StringComparison.Ordinal))
        {
            var parts = aid.Split(':');
            ShowInstantActionPauseSheet(
                parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "C1",
                parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "stunt_flying");
        }
        else if (aid.StartsWith("pauseboard", System.StringComparison.Ordinal))
        {
            var parts = aid.Split(':');
            ShowPauseSheet(
                parts.Length > 1 && int.TryParse(parts[1], out int cm) ? cm : 1,
                parts.Length > 2 && int.TryParse(parts[2], out int done) ? done : 0);
        }

        // Safe on every entry: a cue for the track already playing is a no-op, which is exactly
        // what the original's own mail(11004) does (docs/org/music.md).
        _music?.Enter(MusicState.Menu, _musicRng);
        // The launchscreen's own screenshot aids, one-shot: a return to the menu keeps whoever
        // really joined. Built-in's alone, so they reach it through the presentation's door.
        if (BuiltInMenu is { } menu)
        {
            ApplyMenuDebugAids(menu);
        }
    }

    // The menu host: both presentations registered under their ids, the shared Free Flight and
    // player-setup features, the first seat (keyboard plus every unclaimed pad behind the
    // launchscreen's own poller, the mouse as its pointer), the audio service over the process's music, archive and
    // the rof tree's menu sounds, and OnMenuExit as the sink. The active presentation is settled
    // through PresentationResolution: the force flag, --presentation=, then Original;
    // availability is registration plus, for Original, OriginalAvailable below.
    private MenuHost BuildMenuHost()
    {
        // ⚠ No live mix preview in a run that drives itself, or a scripted walk across the AUDIO page
        // would make that run's mix a function of the walk; --no-det with a scripted flag is still
        // such a run, which is why both halves are read rather than Det alone.
        _menuAudio = new MenuAudioService(_music, wav => _musicArchive?.Find(wav, false, warn: false),
            Path.Combine(_rofPath, "ASSETS", "SOUNDS"), previews: !_spec.Det && _spec.ScriptedBy.Length == 0);
        AddChild(_menuAudio);
        var seatInput = new MenuInput { Keyboard = true };
        // Seat 0 is player 1, so it navigates on the menu keymap that player saved.
        seatInput.LoadSavedKeymap(1);
        var builtInSeat = new BuiltInSeat(seatInput);
        var seat = new PointerSeat(
            builtInSeat, MousePosition, () => Input.IsMouseButtonPressed(MouseButton.Left), TakeMenuWheel,
            () => Input.IsMouseButtonPressed(MouseButton.Right));
        var registry = new PresentationRegistry();
        // The factories read the aid when they run, which is inside a Show: the cold start's
        // instance gets it, and the fresh instance a switch creates gets none.
        _menuAid = _cli.MenuStartScreen;
        registry.Register(PresentationId.BuiltIn,
            () => new BuiltInPresentation(this, _zrdrPath, _dataRoot, _menuAid ?? string.Empty, builtInSeat.Input));
        registry.Register(PresentationId.Original,
            () => new OriginalPresentation(this, _dataRoot, _originalLayout!, _menuAid ?? string.Empty, builtInSeat.Input, _spec.DebugJoin)
            {
                DebugPointer = _spec.DebugPointer,
            });
        var host = new MenuHost(registry, _menuAudio, OnMenuExit);
        host.Availability = OriginalAvailable;
        host.Features.Add(new FreeFlightFeature());
        host.Features.Add(InstantActionFeature.ForDataRoot(_dataRoot));
        // Before the seat: the host lends the setup feature's seat list once the feature is in,
        // so seat 0 has to be joined through it.
        host.Features.Add(new PlayerSetupFeature());
        var strings = UiStrings.TryLoad(_dataRoot) ?? UiStrings.Empty;
        host.Features.Add(new HangarFeature(strings, PlanePickerRoster.AirframeNode, () => StockLoadouts.Load(), _zrdrPath));
        // The campaign feature carries both cinemas because both presentations already read that
        // one feature, and neither of them can reach a Launcher to play a film through.
        _chapterCinema ??= new ChapterCinema(PlayCinema);
        _closingCinema ??= new ClosingCinema(PlayCinema);
        host.Features.Add(new CampaignFeature(
            strings, PlanePickerRoster.AirframeNode, _chapterCinema, _closingCinema));
        // The keymap editor writes through C21's per-player store. The write is injected rather
        // than reached for, so the feature itself stays engine-free and a suite can hold a
        // different one.
        host.Features.Add(new ControlsFeature((player, profile) =>
            CSVM.Bindings.BindingStore.UserBindings().Save(player, profile)));
        host.AddSeat(seat);
        string? reason = host.Select(_spec.ForceBuiltInPresentation, _spec.PresentationOverride);
        string why = reason == null ? "" : $" reason={reason}";
        Log.Info("ui", $"menu presentation active={host.Selected} requested={host.Requested}{why}");
        return host;
    }

    // The host's availability answer: Original needs the decoded layout and every file its asset
    // manifest classes required; the layout it loads is kept for the presentation itself. Asked
    // again on every switch, so a tree repaired between the two answers is seen. Every other id is
    // available once registered.
    private string? OriginalAvailable(PresentationId id)
    {
        if (id != PresentationId.Original)
        {
            return null;
        }

        var layout = OriginalAvailability.Load(_dataRoot, out var reason, out var degraded);
        if (layout != null)
        {
            _originalLayout = layout;
        }

        if (degraded != null)
        {
            Log.Info("ui", $"original presentation: {degraded}");
        }

        return reason;
    }

    // The mouse in window pixels, the pointer half of seat 0: the viewport's last known position,
    // which the Original presentation maps into its authored space.
    private (float X, float Y)? MousePosition()
    {
        var at = GetViewport().GetMousePosition();
        return (at.X, at.Y);
    }

    // The wheel steps counted since the last read, the wheel half of seat 0's pointer.
    private int TakeMenuWheel()
    {
        int steps = _menuWheel;
        _menuWheel = 0;
        return steps;
    }

    // The Options route's apply from the menu: persist and apply every choice, then end the active
    // presentation (discarding every feature's transient state) and show the same one again at its
    // top level. No re-select: the presentation is the command line's alone, settled once.
    private void ApplyOptions(OptionsApplyExit applied)
    {
        if (_menuHost == null)
        {
            return;
        }

        PersistOptions(applied);
        _menuHost.Deactivate();
        ShowMenu(MenuReturnDestination.TopLevel);
    }

    // The options file's one writer, shared by the menu's apply above and by the pause leaf's.
    // It saves every choice the screen took. The display settings and the mix are applied now.
    // ⚠ The graphics word, the opening view and the difficulty are saved and no more. Each is
    // read once, at launch or when a flight is built, so do not rebuild anything here. The head
    // turn and targeting switch are saved for the next sortie and put on the seats flying now by
    // the pause leaf itself (PausePreferences.FeedGameOptions).
    private void PersistOptions(OptionsApplyExit applied)
    {
        var store = OptionsStore.UserOptions();
        var options = store.Load();
        options.GraphicsMode = applied.Graphics;
        options.Difficulty = applied.Difficulty;
        options.NearestAfterKill = applied.NearestAfterKill;
        options.Rumble = applied.Rumble;
        options.DefaultView = applied.DefaultView;
        options.AutoHeadTurn = applied.AutoHeadTurn;
        options.MonitorIndex = applied.MonitorIndex;
        options.Resolution = applied.Resolution;
        options.DisplayMode = applied.DisplayMode;
        options.VSync = applied.VSync;
        options.AudioMaster = applied.AudioMaster;
        options.AudioMusic = applied.AudioMusic;
        options.AudioEffects = applied.AudioEffects;
        options.AudioVoice = applied.AudioVoice;
        store.Save(options);
        // The display settings take effect now instead of at the next start, through the same calls
        // the startup path makes and in the order the window needs them: the screen it sits on, the
        // mode, the size, then the pacing, which --no-vsync still beats (docs/menu-presentations.md).
        MonitorSetting.Apply(MonitorSetting.Resolve(applied.MonitorIndex, MonitorSetting.Screens()));
        DisplayModeSetting.Apply(DisplayModeSetting.Resolve(applied.DisplayMode));
        ResolutionSetting.Apply(
            ResolutionSetting.Resolve(applied.Resolution, ResolutionSetting.ScreenSizes(), applied.DisplayMode),
            GetWindow());
        VSyncSetting.Apply(VSyncSetting.Resolve(_spec.NoVsync, applied.VSync, Config.GetBool(VSyncSetting.Key, VSyncSetting.ConfigDefault)));
        // The mix takes effect now too, through the same call the startup path makes. Apply is
        // idempotent, so an accept from a page that shows no slider rewrites the same three gains.
        AudioMix.Apply(applied.AudioMaster, applied.AudioMusic, applied.AudioEffects, applied.AudioVoice);
        // The haptics toggle takes effect now for the same reason. A pilot turning it off over the
        // pause sheet flies the rest of the sortie with a quiet pad.
        PadRumble.Enabled = !_spec.Det && applied.Rumble != false;
        // ⚠ The carve is NOT re-armed here. No screen offers it, so the saved key is untouched by an
        // apply and the gate keeps what boot gave it (see the arming above).
        Log.Info("ui", $"options applied: {Utils.GraphicsMode.Key}={applied.Graphics} difficulty={applied.Difficulty}");
    }

    // The in-flight Preferences leaf both pause boards open. It takes the decoded layout the
    // Original presentation composes from, and the menu's audio service for its cues. The host's
    // own rebinding feature means a rebind over the pause edits the keymap the menu edits, saved
    // through the one writer. PersistOptions is the apply. ⚠ No ShowMenu: the flight returns to
    // the sheet over its own world, not tearing down what the pause stands on. Null where no
    // layout reads.
    private UI.Screens.PausePreferences? BuildPauseOptions()
    {
        OriginalAvailable(PresentationId.Original);
        var controls = _menuHost != null && _menuHost.Features.TryGet<ControlsFeature>(out var feature)
            ? feature
            : null;
        return UI.Screens.PausePreferences.Build(_dataRoot, _originalLayout, controls, PersistOptions, _menuAudio);
    }

    // --debug-join=N synthesizes N extra device-less players so the splitscreen aircraft select
    // can be screenshot on a one-controller machine; --debug-waves=/--debug-wingmen=/--debug-preset=
    // are the same aid for the Instant Action wizard's screens. The preset goes last, so it
    // overwrites the two before it: a preset fills the wave and wingman fields itself.
    private void ApplyMenuDebugAids(LaunchMenu menu)
    {
        if (_pendingJoin > 0)
        {
            menu.DebugJoin(_pendingJoin);
            _pendingJoin = 0;
        }
        if (_pendingWaves > 0)
        {
            menu.DebugWaves(_pendingWaves);
            _pendingWaves = 0;
        }
        if (_pendingWingmen > 0)
        {
            menu.DebugWingmen(_pendingWingmen);
            _pendingWingmen = 0;
        }
        if (_pendingPreset >= 0)
        {
            menu.DebugPreset(_pendingPreset);
            _pendingPreset = -1;
        }
    }

    // The host's exit sink: the one place a menu leaves through. The host has already hidden the
    // presentation, with its state kept so a failed build can show it again where it stood.
    private void OnMenuExit(MenuExit exit)
    {
        switch (exit)
        {
            case LaunchExit launch:
                StartSessionFromMenu(launch);
                break;
            case CampaignMissionExit mission:
                StartCampaignFromMenu(mission);
                break;
            case QuitExit:
                BlankAndQuit();
                break;
            case OptionsApplyExit applied:
                _pendingApply = applied;
                break;
        }
    }

    // A non-campaign launch: derive this session's spec, bind pads, start the session. A build
    // failure returns to the menu with a note instead of a blank screen.
    // ⚠ Derive the spec from _cli, never the outgoing _spec, so nothing the last session
    // settled leaks into this one. Pads are the deliberate exception: they come from the join
    // flow, not args, so they stay session state rather than a spec field.
    private void StartSessionFromMenu(LaunchExit launch)
    {
        var (planes, pads, fits, customs) = Unpack(launch.Seats);
        LaunchedFrom(launch);
        _spec = SessionSpec.FromMenu(_cli, launch.Chapter, planes, launch.Mode, launch.InstantAction, fits, customs,
            launch.Match?.KillTarget, launch.Match?.TimeLimitMinutes, launch.WingmanLoadout);
        // Step the master so flying again is a new mission rather than a replay: without this every
        // relaunch re-derives the same spawn, opposition and liveries. ⚠ A pinned run must hold
        // still, which is what keeps the goldens and the perf harnesses reproducible.
        StepSortieSeed();
        BindMenuPads(pads);
        BeginLaunch();
    }

    // The seat choices as the four parallel lists the spec factories take. The fits ride
    // alongside the planes rather than inside them: FromMenu writes each menu-settable field
    // explicitly, so a chosen loadout has to be handed over or it would be dropped. A plane the
    // campaign exported flies with the ammunition and ordnance EXPORT wrote into it; a fit set on
    // the loadout screen is this sortie's own explicit pick and stands instead of the stored one.
    private (List<string> Planes, List<int[]> Pads, List<Flight.Weapons.LoadoutChoice?> Fits,
        List<Flight.Hangar.CustomPlaneDef?> Customs) Unpack(IReadOnlyList<MenuSeatChoice> seats)
    {
        var planes = new List<string>(seats.Count);
        var pads = new List<int[]>(seats.Count);
        var fits = new List<Flight.Weapons.LoadoutChoice?>(seats.Count);
        var customs = new List<Flight.Hangar.CustomPlaneDef?>(seats.Count);
        Flight.Weapons.StockLoadouts? stock = null;
        foreach (var seat in seats)
        {
            planes.Add(seat.PlaneNode);
            pads.Add(seat.Pads as int[] ?? new List<int>(seat.Pads).ToArray());
            customs.Add(seat.Custom);
            fits.Add(seat.Fit ?? (seat.Custom is { HasLoadout: true } custom
                ? CampaignLoadout.For(custom, stock ??= Flight.Weapons.StockLoadouts.Load())
                : null));
        }

        return (planes, pads, fits, customs);
    }

    // Steps the master so flying again is a new mission rather than a replay. A pinned run holds
    // still, which is what keeps the goldens and the perf harnesses reproducible.
    private void StepSortieSeed()
    {
        if (!_spec.SeedPinned)
        {
            _sortie++;
            _masterSeed = Rng.SortieSeed(_processSeed, _sortie);
        }
        LogMasterSeed();
    }

    // Honour the join flow's device binding rather than re-deriving it from the connected roster:
    // the pad that joined as P2 in the menu must be the pad that flies P2. Single player keeps the
    // any-pad policy (null), so every connected pad flies the one plane, as before.
    // ⚠ EVERY menu launch path has to come through here. Pads.AssignPads, the fallback a path that
    // skips it lands on, gives P1 every pad no later seat claimed, so a two-seat launch with one
    // pad and the keyboard flew both seats off both devices, and P2's plane sat still.
    private void BindMenuPads(IReadOnlyList<int[]> pads)
    {
        _menuPads = null;
        if (_spec.Players <= 1)
        {
            return;
        }

        _menuPads = new int[_spec.Players][];
        for (int i = 0; i < _spec.Players; i++)
        {
            _menuPads[i] = pads[i];
        }
    }

    // The campaign cabin's FLY MISSION: the same derive-spec-then-build path as the launchscreen's
    // own launch, over the profile and story position the flow settled. The chapter and mission are
    // NOT named here, CampaignDirector.ResolveSpec reads them out of cm_sequence in the session's
    // constructor, so one place resolves a story position whether it came from a cabin or a
    // --campaign= command line.
    private void StartCampaignFromMenu(CampaignMissionExit mission)
    {
        var (planes, pads, fits, customs) = Unpack(mission.Seats);
        LaunchedFrom(mission);
        _spec = SessionSpec.FromCampaign(_cli, mission.Profile, mission.MissionSeq, planes,
            pads.Count, fits, customs);
        StepSortieSeed();
        BindMenuPads(pads);
        BeginLaunch();
    }

    // A flown campaign mission is over, won or lost: free the world and reopen the menu on the
    // debrief for the mission just flown, which each presentation maps onto its own book. Reached
    // from the session's CampaignMissionEnded by way of _pendingDebrief, one frame later, carrying
    // the result the mission ended with.
    private void OpenDebrief(string profile, CampaignMissionResult result)
    {
        Log.Info("core", $"campaign: {result.Outcome}, arrived at the debrief with '{profile}'");
        ReturnToMenu(new DebriefReturn(profile, result.Attempt.Seq, result.Outcome == MissionOutcome.Won));
    }

    // The mission boards' Restart (Instant Action and campaign): free this session and build a
    // fresh one from the same spec, behind the load screen. A rerun in place cannot put the
    // mission's opposition or objectives back (waves, the ace, a killed zeppelin and the campaign
    // graph all live in the world), so the world is rebuilt instead.
    // ⚠ Steps the seed exactly as flying again from the menu does, so an unpinned restart is a new
    // mission and a pinned one (--seed=/--det) still repeats.
    private void RestartSession()
    {
        if (_session != null)
        {
            // Freed at the end of THIS frame, so the build owed for the next one finds it gone.
            _session.QueueFree();
            _session = null;
        }
        StepSortieSeed();
        Log.Info("core", $"restart: rebuilding {_spec.Chapter} / {_spec.ModeName} from the same settings");
        BeginLaunch();
    }

    // Prints the master the next session will draw from. Per session rather than per process
    // because an unpinned run advances it: the seed a mission actually flew on is the one worth
    // having in the log, so an interesting one can be pinned with `--seed=`.
    private void LogMasterSeed()
    {
        string how = _spec.SeedPinned ? "pinned" : Log.Format($"sortie {_sortie}, --seed=N to pin");
        Log.Info("core", $"rng: master seed {_masterSeed} ({how})");
    }

    // The boards' Exit item, the pause sheet's among them: back to the screen this flight was
    // launched from when the process launched into the menu, out of the game otherwise, and
    // reachable from a pad as much as from Esc. Every way a session ends without a result
    // (the sheet, the wrap-up board, the scoreboard) arrives here, so one destination serves them.
    private void ExitSession()
    {
        if (_menuDriven && _session is { InSession: true })
        {
            ReturnToMenu(_exitDestination);
            return;
        }
        BlankAndQuit();
    }

    /// <summary>The three quits reached from a frame that is still drawing: blacks the persistent
    /// <c>WorldEnvironment</c>'s background, then ends the frame. <c>Quit()</c> ends the frame
    /// rather than the process, so one more frame is drawn and that image is held on screen for
    /// the whole shutdown, and the menu host hides its opaque backdrop one call before the exit
    /// reaches here. The held frame would otherwise be the procedural sky.
    /// ⚠ Every probe exit keeps the bare <c>Quit()</c>: no headless run may pay for this.</summary>
    private void BlankAndQuit()
    {
        WorldBackdrop.Black(_env);
        GetTree().Quit();
    }

    // Frees the current session node and shows the menu again at a semantic destination, the
    // in-process rebuild path for the boards' Exit item, for failed builds and for the debrief.
    // The whole session subtree hangs under the node, so `QueueFree` tears it down; the non-child
    // duties (the published clock, the world lights, the session texture archive, the main-camera
    // restore) run in the node's `_Notification` on `NotificationExitTree`. The camera, lights and
    // shader globals persist on `this`.
    private void ReturnToMenu(MenuReturnDestination destination)
    {
        if (_session != null)
        {
            _session.QueueFree();
            _session = null;
        }

        // The menu is not a session start. A cover left over from one (a mission exited inside its
        // own fade) has nothing left to uncover.
        DropStartCover();
        // Same reason as the build in LaunchSession: a teardown legitimately stalls the loop.
        _hitchSidecar.Flush();
        _hitchMonitor.Rearm();
        RearmRate();
        PerfSample.Reset();
        PhysicsTickCost.Reset();
        ProcessPassCost.Reset();
        AiStepCost.Reset();
        SimPhaseCost.Reset();
        ProcessSiteCost.Reset();
        _perfHud.Rearm();
        ShowMenu(destination);
    }

    // Writes the master output gain Utils/MasterVolume.cs resolves, and is its only caller.
    // Deliberately not --mute: at volume 0 both audio paths still load, play, count and log, so
    // the run is silent but not blind. A bus write for the same reason SetFocusMuted is one,
    // see this file's docs/architecture.md entry. ⚠ Bus 0 alone: the player's four levels are
    // written on the three buses under it by Utils/AudioMix.cs, so neither gain can stand in for
    // the other and a saved level cannot lift a run this silenced.
    private void ApplyMasterVolume()
    {
        // The exported switch is the one that settles the log directory, so an export's audible
        // default and its logs\ folder are decided together.
        float volume = MasterVolume.Resolve(_spec.Volume, _exported);
        string source = _spec.Volume.HasValue ? "--volume" : "config";
        // Full volume is the bus's own resting state, so leaving it alone keeps a full-volume
        // launch byte-identical in both output and console log.
        if (Mathf.IsEqualApprox(volume, MasterVolume.Unattenuated))
        {
            return;
        }
        AudioServer.SetBusVolumeDb(MasterBus, MasterVolume.VolumeDb(volume));
        string note = volume <= 0f ? ", sounds still load, play, count and log" : "";
        Log.Info("sound", $"master volume={volume:0.###} via={source}{note}");
    }

    // Mutes/unmutes the master bus and gates pad reads, on window focus. Idempotent,
    // the notification can arrive more than once, and it only ever clears a mute it set itself,
    // so it cannot stomp on a mute from anywhere else.
    private void SetFocusMuted(bool muted)
    {
        if (_focusMuted == muted)
        {
            return;
        }
        _focusMuted = muted;
        AudioServer.SetBusMute(MasterBus, muted);
        // Pad reads follow focus (Pads.For), so a stick drifting while alt-tabbed cannot fly
        // the plane. The roster deliberately does not, see Pads.cs's docs/architecture.md
        // entry. Keyboard needs no gate: Godot releases held keys on focus loss.
        Pads.Focused = !muted;
        if (muted)
            Log.Info("sound", $"focus: lost, audio muted, pad reads gated");
        else
            Log.Info("sound", $"focus: regained, audio restored, pad reads live");
    }

    // Samples the engine's eight per-frame counters once, for both instruments. The two
    // `TIME_*` monitors are seconds and are converted here, so everything downstream of this
    // is in milliseconds. Read at priority -999, so (like `delta` itself) these describe the
    // frame that just ended rather than the one being built; the two agree with each other, which
    // is what a hitch record needs.
    private FrameCounters ReadFrameCounters() => new(
        ScriptMs: 1000 * Performance.GetMonitor(Performance.Monitor.TimeProcess),
        RenderCpuMs: RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewportRid),
        GpuMs: RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid),
        PhysicsMs: 1000 * Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess),
        Draws: (long)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
        Prims: (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
        Nodes: (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
        MemBytes: (long)Performance.GetMonitor(Performance.Monitor.MemoryStatic));

    // --hitch-inject=: burns wall time synchronously for about `ms`, so a stall of known
    // magnitude exists to verify against. The busy-wait form proves the timing path; `alloc`
    // burns the same time allocating 4 KB buffers instead, the only way to move the GC columns
    // on demand.
    // ⚠ Never wrap this in a PerfSample scope. An injected fault must show as unattributed
    // time, not a breadcrumb mistaken for the thing under test.
    private void InjectHitch(float ms, bool alloc)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double freq = System.Diagnostics.Stopwatch.Frequency;
        long sink = 0;
        while ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / freq < ms)
        {
            if (alloc)
            {
                sink += new byte[4096].Length;
            }
        }
        Log.Info("perf", $"hitch-inject fired ms={ms:0.0} alloc={alloc} bytes={sink}");
    }

    /// <summary>The always-on frame-rate trace: one <c>[perf] rate</c> line per
    /// <see cref="RateWindowSeconds"/> carrying the rate the window ACHIEVED. This is the question
    /// <see cref="HitchMonitor"/> cannot answer and never could: its trigger is a multiple of a
    /// rolling median, so a sustained collapse drags the median up with it and trips nothing, and a
    /// whole sortie spent at a fraction of the refresh rate leaves a clean log. Mean and worst frame
    /// sit side by side so a rate drop and a single stall read differently.</summary>
    private void ReportRate(double frameMs)
    {
        _rateFrames++;
        _rateWallMs += frameMs;
        _rateWorstMs = System.Math.Max(_rateWorstMs, frameMs);
        if (_rateWallMs < RateWindowSeconds * 1000)
        {
            return;
        }
        double seconds = _rateWallMs / 1000;
        double fps = _rateFrames / seconds;
        double meanMs = _rateWallMs / _rateFrames;
        double worstMs = _rateWorstMs;
        Log.Info("perf", $"rate fps={fps:0.0} frames={_rateFrames} wall_s={seconds:0.0} frame_ms={meanMs:0.00} worst_ms={worstMs:0.00}");
        RearmRate();
    }

    // Drops the open rate window. Called wherever the frames on either side are not each other's
    // neighbours, for the same reason HitchMonitor.Rearm is: a window spanning a session build
    // would report a rate no part of the run ever ran at.
    private void RearmRate()
    {
        _rateWallMs = 0;
        _rateWorstMs = 0;
        _rateFrames = 0;
    }

    // --perf: the headless stand-in for the editor's profiler, meaned over the window so a
    // single hitch doesn't read as a regression, A/B two builds by comparing the same line.
    // `script_ms`/`physics_ms` are Godot's two worst-of-the-last-second monitors, kept only
    // because older records hold them. The measured terms are `proc_ms`, `phys_tick_ms` and
    // `ai_ms` (verification PERF-1). `max_ms`/`p95_ms` answer "how bad did it get"; no
    // `p99_ms` since a 60-sample window's nearest-rank p99 is just `max_ms` (Perf95Index).
    private void ReportPerf(double delta, in FrameCounters counters)
    {
        _perfFrames++;
        _perfFrameMs[_perfFrames - 1] = delta * 1000;
        _perfClock += delta;
        _perfProcess += counters.ScriptMs;
        _perfPhysics += counters.PhysicsMs;
        _perfCpuRender += counters.RenderCpuMs;
        _perfGpu += counters.GpuMs;
        // Counts, averaged like every ms term, but with no timing noise in them: a scene that
        // starts drawing more says so exactly, where an ms term has to clear a noise band first.
        _perfDraws += counters.Draws;
        _perfPrims += counters.Prims;
        _perfNodes += counters.Nodes;
        _perfMem += counters.MemBytes;
        if (_perfFrames < PerfWindowFrames)
        {
            return;
        }
        // Locals, not one very long expression: Log takes a single interpolated string (two
        // concatenated ones are a plain string, which would already have formatted its floats in
        // the current culture and so does not compile against it).
        double n = _perfFrames;
        long simFrame = ClockNow?.Frame ?? 0;
        double wallMs = 1000 * _perfClock;
        double fps = n / _perfClock;
        double frameMs = wallMs / n;
        // Every ms term is already in milliseconds here: ReadFrameCounters does the seconds-to-ms
        // conversion on Godot's two TIME_* monitors once, at the read.
        double scriptMs = _perfProcess / n;
        double renderCpuMs = _perfCpuRender / n;
        double gpuMs = _perfGpu / n;
        double physicsMs = _perfPhysics / n;
        // ⚠ These are the physics terms to read, not physics_ms above (verification PERF-1). One
        // tick is one 1/60 sim step on a realtime clock, so phys_hz is sim seconds per wall second
        // and a step over its 16.7 ms budget shows here as a rate under 60.
        var (physTickMs, physTickMaxMs, physTicks) = PhysicsTickCost.Take();
        // ⚠ The script term to read, not script_ms above (verification PERF-1). Meaned over the
        // passes that CLOSED, one fewer than the window's frames: this runs inside the pass, whose
        // tail lands in the next window.
        var (procTotalMs, procMaxMs, procPasses) = ProcessPassCost.Take();
        double procMs = procPasses > 0 ? procTotalMs / procPasses : 0;
        // ⚠ The only term that attributes frame cost to the AI. Meaned over the window's FRAMES,
        // not its walks, because a parent-driven clock runs several walks per rendered frame and
        // the question is what the AI cost that frame. Zero with no AI spawned.
        var (aiTotalMs, aiSteps, aiPlaneSum) = AiStepCost.Take();
        double aiMs = aiTotalMs / n;
        double aiPlanes = aiSteps > 0 ? (double)aiPlaneSum / aiSteps : 0;
        double physHz = _perfClock > 0 ? physTicks / _perfClock : 0;
        double physTick = physTicks > 0 ? physTickMs / physTicks : 0;
        // These split the two whole-pass terms above by what ran. The sim step goes per TICK beside
        // phys_tick_ms, the named _Process consumers per FRAME beside proc_ms (src/Utils/PhaseCost.cs).
        string simRow = SimPhaseCost.TakeRow(physTicks);
        string simAllocRow = SimPhaseCost.AllocRow();
        string procSites = ProcessSiteCost.TakeRow(n);
        double draws = _perfDraws / n;
        double prims = _perfPrims / n;
        double nodes = _perfNodes / n;
        double memMb = _perfMem / n / (1024 * 1024);
        System.Array.Copy(_perfFrameMs, _perfFrameMsSorted, PerfWindowFrames);
        System.Array.Sort(_perfFrameMsSorted);
        double maxMs = _perfFrameMsSorted[PerfWindowFrames - 1];
        double p95Ms = _perfFrameMsSorted[Perf95Index];
        Log.Info("perf", $"window sim_frame={simFrame} frames={_perfFrames} wall_ms={wallMs:0.00} fps={fps:0.0} frame_ms={frameMs:0.00} script_ms={scriptMs:0.00} proc_ms={procMs:0.000} proc_max_ms={procMaxMs:0.000} proc_passes={procPasses} ai_ms={aiMs:0.000} ai_planes={aiPlanes:0.0} render_cpu_ms={renderCpuMs:0.00} gpu_ms={gpuMs:0.00} physics_ms={physicsMs:0.00} phys_tick_ms={physTick:0.000} phys_tick_max_ms={physTickMaxMs:0.000} phys_hz={physHz:0.0} draws={draws:0.0} prims={prims:0.0} nodes={nodes:0.0} mem_mb={memMb:0.00} max_ms={maxMs:0.00} p95_ms={p95Ms:0.00} sim_ms={simRow} proc_sites_ms={procSites}");
        // Its own line, not another term on the window above. The BYTE figure answers a different
        // question from the millisecond one: which phase feeds the collector, rather than which
        // phase the pause landed in (PERF-34). The two are read side by side.
        Log.Info("perf", $"alloc sim_frame={simFrame} sim_alloc_b={simAllocRow}");
        _perfClock = 0; _perfFrames = 0; _perfProcess = _perfGpu = _perfCpuRender = _perfPhysics = 0;
        _perfDraws = _perfPrims = _perfNodes = _perfMem = 0;
    }
}

/// <summary>What a session node needs from the launcher: the settled base paths, the persistent
/// rendering nodes it configures but does not own, the process-scoped services, and the join
/// flow's session state. A snapshot per launch, <see cref="MenuPads"/> is the field that varies.
/// ⚠ Not a second home for args: a new flag is a <see cref="SessionSpec"/> change, never a
/// context field.</summary>
public sealed class LauncherContext
{
    public required string RepoRoot { get; init; }
    public required string DataRoot { get; init; }
    public required string PlanesGamezPath { get; init; }
    public required string ZrdrPath { get; init; }
    public required string SoundsPath { get; init; }
    public required string InterpPath { get; init; }
    public required string MessagesPath { get; init; }
    public required string RofPath { get; init; }
    public required Tooling.ProbeRunner ProbeRunner { get; init; }
    public required Tooling.CaptureDirector CaptureDirector { get; init; }
    public required ulong MasterSeed { get; init; }
    public required Camera3D Camera { get; init; }
    public required OrbitCamera Orbit { get; init; }
    public required DirectionalLight3D Sun { get; init; }
    public required Godot.Environment? Env { get; init; }
    /// <summary>Whether this process launched into the menu, Esc from flight returns there
    /// instead of quitting (the flight HUD's exit hint reads it too).</summary>
    public required bool MenuDriven { get; init; }
    /// <summary>Per-player pad binding from the launchscreen's join flow (null = derive from the
    /// connected roster, which is what every CLI launch does).</summary>
    public required int[][]? MenuPads { get; init; }

    /// <summary>The presentation this session's own boards take, already resolved: the menu's
    /// active one, or what the flags name on a CLI launch. A resolved answer rather than a flag,
    /// which is why it rides here beside <see cref="MenuDriven"/>.</summary>
    public required UI.Menu.PresentationId Presentation { get; init; }

    /// <summary>Leaves the session the way <see cref="MenuDriven"/> says: back to the launchscreen,
    /// or out of the game. The boards' Exit item calls it, so the one routing rule lives on the
    /// Launcher rather than being restated per board.</summary>
    public required System.Action ExitSession { get; init; }

    /// <summary>Frees this session and builds a fresh one from the same settings, behind the load
    /// screen, the mission boards' Restart item (Instant Action and campaign). The mission's
    /// opposition lives in the world, so putting it back means rebuilding the world, which only
    /// the Launcher can do.</summary>
    public required System.Action RestartSession { get; init; }

    /// <summary>Builds the in-flight Preferences leaf either pause board opens over the held world,
    /// or answers null where the install carries no decoded menu layout for it to compose from. A
    /// factory rather than the node, since the session parents it and only the Launcher holds the
    /// layout, the shared rebinding feature, the menu's audio and the options file's writer.</summary>
    public System.Func<UI.Screens.PausePreferences?>? PauseOptions { get; init; }

    /// <summary>A campaign mission ended, won or lost: the Launcher frees this session a frame later
    /// and shows the menu at the debrief of the named profile's flown mission, carrying the result
    /// intact from <c>MissionEnded</c>. Null when this process was not launched into the menu,
    /// where there is no menu to return to.</summary>
    public System.Action<string, CampaignMissionResult>? CampaignMissionEnded { get; init; }

    /// <summary>Frees the session and shows the menu at the Instant Action wrap-up, carrying the
    /// final numbers the ending left. Null when this process was not launched into the menu, and
    /// unused by a presentation whose own board takes the ending inside the flight.</summary>
    public System.Action<IaWrapupSnapshot>? InstantActionWrapup { get; init; }

    /// <summary>The process's music channel, so a mission's own cues reach the one player that
    /// outlives every session. Null when the sound archive or the sound definitions would not
    /// load, which leaves the game silent rather than refusing to launch.</summary>
    public MusicPlayer? Music { get; init; }
}
