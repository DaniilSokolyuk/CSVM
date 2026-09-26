using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM;

/// <summary>The session shapes, closed. Every launch is exactly one of these; <c>Stunt</c>,
/// <c>Versus</c>, <c>Players</c>, <c>EmptyStage</c>, <c>NodeName</c> and <c>DamageLab</c> are
/// modifiers on top of one, not shapes of their own.</summary>
public enum SessionMode
{
    /// <summary>No content arg (or <c>--menu</c>): the launchscreen picks the session.</summary>
    Menu,
    Fly,
    Viewer,
    Freecam,
    AnimLab,
}

/// <summary>The probes that wear a mode as a disguise: each drives and ends a session by itself,
/// and each asks for the mode that gives it the world it needs. Naming them keeps the coercion
/// visible instead of letting <see cref="SessionMode"/> inherit it as if the user had asked.</summary>
public enum SessionProbe
{
    None,
    /// <summary><c>--damage-test</c>: needs a world with no aircraft in it → freecam.</summary>
    DamageTest,
    /// <summary><c>--effects-test</c>: same world, same reason → freecam.</summary>
    EffectsTest,
    /// <summary><c>--weapon-test</c>: needs a parked plane to mount weapons on → viewer.</summary>
    WeaponTest,
}

/// <summary>The launchscreen's Mode screen, in the order its rows are drawn. Carried in the
/// typed <c>LaunchExit</c> into <see cref="SessionSpec.FromMenu"/>, which turns the pick into
/// <see cref="SessionSpec.Stunt"/>/<see cref="SessionSpec.Versus"/>, kept here rather than on
/// <c>LaunchMenu</c> so the menu's own tests stay engine-free.</summary>
public enum MenuMode
{
    Free,
    Stunt,
    Versus,
}

/// <summary>The enhanced presentation's screen-space passes, as doors a run can close one at a
/// time. Each can lay a pattern of its own over the frame. Bisecting a full-screen artefact means
/// rendering one pose per closed door until the pattern goes. Reaches
/// <c>--graphics=enhanced</c> alone, the faithful path running none of them.</summary>
[Flags]
public enum EnhancedPasses
{
    None = 0,

    /// <summary><c>--no-ssao</c>: the ambient-occlusion pass.</summary>
    Ssao = 1,

    /// <summary><c>--no-ssr</c>: screen-space reflection on the water.</summary>
    Ssr = 2,

    /// <summary><c>--no-glow</c>: the glow pass; the AgX tonemap stays.</summary>
    Glow = 4,

    /// <summary><c>--no-soft-shadows</c>: the sun's penumbra, angular distance and blur both 0,
    /// leaving a hard shadow edge rather than no shadow.</summary>
    SoftShadows = 8,
}

/// <summary>One <c>--ai=</c> entry: the airframe, plus the optional tokens that follow it.
/// <c>Count</c> is the entry's <c>n=</c>, so one entry stands for a whole squadron rather than a
/// plane. <c>Team</c> is its <c>team=</c>, and it is the only way two CLI planes can be put on the
/// SAME side: a spawn that names none takes its own banded id through
/// <see cref="Flight.Weapons.AimAssist.TeamOfPilot"/>, which makes every CLI plane hostile to every other.
/// <c>Pos</c> overrides the squadron's placement outright, in world metres.</summary>
public readonly record struct AiPlaneEntry(string Plane, string? Net = null, int? Accent = null,
    string? Def = null, int? Team = null, int Count = 1, Vector3? Pos = null);

/// <summary>One <c>--zep=</c> request: the zeppelin record to graft onto the empty stage, and the
/// two overrides the stage needs. <c>Team</c> reaches the hull as the borrowed def's own team id,
/// so it arrives through <c>ZeppelinRuntime.AuthoredTeam</c> rather than as a stamp on the part
/// pools, and the rule that gives a zone the team its flagged <c>panels</c> child carries still
/// runs. <c>Pos</c> replaces the record's authored seat, world metres. <c>Net</c> is its
/// <c>net=</c>: name <see cref="Mech3.EmptyStage.PatrolNetName"/> and the hull flies the stage's
/// built-in ring instead of station-keeping on the one-node net the graft otherwise builds.</summary>
public readonly record struct ZepStageSpec(string Chapter, string Mission, string Record,
    int? Team = null, Vector3? Pos = null, string? Net = null);

/// <summary>
/// One immutable value for everything the command line settles about a session: parsed once,
/// then resolved once, so a consumer reads an answer instead of re-deriving one.
/// <see cref="Parse"/> returns a RESOLVED spec; the mode flags arrive as private fields and
/// arbitration turns them into <see cref="Mode"/>, see <see cref="Resolve"/> for the step order.
/// ⚠ Pure, no engine state, no globals, no logging, no clock. Complaints go to
/// <see cref="Warnings"/>, never a print.
/// </summary>
public sealed record SessionSpec
{
    // The sim frame a bare `--crash` (no `=frame`) fires at, early enough that
    // the default `--frames=` screenshot lands mid-break-up rather than pre-impact.
    private const int DefaultCrashFrame = 5;

    // The sim frame a bare `--debug-pause` (no `=frame`) opens the board at: one step, so the
    // scripted pause is as early as a shot of the board alone can want it.
    private const int DefaultDebugPauseFrame = 1;

    // The frame a bare `--hitch-inject=` (no `@frame`) fires at, in FrameCount's own space.
    // The wall-time margin this gives on the dev box is measured in docs/cli.md.
    private const int DefaultHitchInjectFrame = 300;

    private List<Note> _notes = new();

    // What the command line asked for. Private, because a vote is not an outcome: several flags
    // vote for the same mode (--markers/--damage/--weapon-* for the viewer, --damage-test/
    // --effects-test for the freecam, --play-anim=/--debug-anim-ui for the anim lab), and the
    // arbitration below is the only thing entitled to turn them into one.
    private bool _flyArg, _viewerArg, _freecamArg, _animLabArg, _stuntArg, _vsArg, _damageLabArg, _detArg;

    private SessionSpec()
    {
    }

    /// <summary>The user args this spec was parsed from, verbatim and in order.</summary>
    public IReadOnlyList<string> Args { get; private set; } = Array.Empty<string>();

    /// <summary>Every complaint parse and resolution raised, in order. Nothing is logged here, the
    /// caller emits these, which is what keeps the type engine-free.</summary>
    public IReadOnlyList<Note> Warnings => _notes;

    // ---- The mode: raw votes in, one arbitrated answer out -------------------------------------

    /// <summary>A content-selecting arg was given, so the launchscreen is bypassed.</summary>
    public bool HasContentArg { get; private set; }

    /// <summary><b>Resolved.</b> The one session shape, arbitrated from every flag that votes for
    /// one: anim lab &gt; freecam &gt; viewer &gt; fly, with <c>--node=</c> forcing the viewer
    /// unless the anim lab was asked for, and any content arg defaulting to flight.</summary>
    public SessionMode Mode { get; private set; } = SessionMode.Menu;
    public bool Fly => Mode == SessionMode.Fly;
    public bool Viewer => Mode == SessionMode.Viewer;
    public bool Freecam => Mode == SessionMode.Freecam;
    public bool AnimLab => Mode == SessionMode.AnimLab;
    /// <summary><b>Resolved.</b> Flight plus the mission's danger zones, a modifier on
    /// <see cref="SessionMode.Fly"/>, cleared by any mode that beats flight.</summary>
    public bool Stunt { get; private set; }
    /// <summary><b>Resolved.</b> Splitscreen free-for-all deathmatch ("Dogfight"), a modifier on
    /// <see cref="SessionMode.Fly"/>, cleared by any mode that beats flight, same as
    /// <see cref="Stunt"/>. <c>--vs</c> beats <c>--stunt</c> by fixed precedence when both are
    /// given, with a warning, the two are not composable. The menu enforces
    /// <see cref="Players"/> &gt;= 2 before it will start a match; the CLI only warns.</summary>
    public bool Versus { get; private set; }
    /// <summary><c>--coop</c>: plain splitscreen free flight (no <see cref="Versus"/>, no Instant
    /// Action) puts every human on <see cref="AimAssist.PlayerTeam"/> instead of the per-pilot
    /// default (plain flight's default stays FFA, this is the opt-in
    /// to co-op). Dropped with a warning when combined with <c>--vs</c>, whose FFA is explicit and
    /// outranks it. A campaign session resolves it true on its own and ignores the flag in
    /// silence, because the flag asks for what a co-op campaign already is.</summary>
    public bool Coop { get; private set; }
    /// <summary><c>--vs-kills=N</c>: the kill target that ends a match early. Default 5; 0
    /// disables the kill limit (the match then runs to the time limit alone).</summary>
    public int VsKills { get; private set; } = 5;
    /// <summary>Resolved. <c>--vs-kills=</c> was spelled out, so the flag beats a kill
    /// target a menu screen chose (<see cref="FromMenu"/>).</summary>
    public bool VsKillsExplicit { get; private set; }
    /// <summary><c>--vs-time=minutes</c>: the match time limit, in MINUTES. Default 5; 0 disables
    /// the time limit (the match then runs to the kill target alone).</summary>
    public int VsTimeMinutes { get; private set; } = 5;
    /// <summary>Resolved. <c>--vs-time=</c> was spelled out, so the flag beats a time limit
    /// a menu screen chose (<see cref="FromMenu"/>).</summary>
    public bool VsTimeExplicit { get; private set; }
    /// <summary><b>Resolved.</b> Open the aircraft's per-part HP sliders at launch, a modifier on
    /// <see cref="SessionMode.Viewer"/> (the parked plane) or <see cref="SessionMode.Fly"/> (the
    /// flown one), dropped by the modes that build no aircraft at all. The lab itself is always
    /// built; this only decides whether it starts open or waits behind F19.</summary>
    public bool DamageLab { get; private set; }
    public bool ForceMenu { get; private set; }
    public string MenuStartScreen { get; private set; } = "";

    /// <summary><c>--movie=</c>: play that one cinema and quit, with no world and no menu behind
    /// it. Null when the flag is absent. The name is resolved without regard to case
    /// (<see cref="SessionPaths.Cinema"/>) and needs no <c>.mpg</c>. ⚠ It does not imply the
    /// determinism bundle, unlike the other flags that end a run by themselves: the clock a cinema
    /// runs on is the audio device's, so a fixed-step sim clock would say nothing about it.</summary>
    public string? MovieName { get; private set; }

    /// <summary><c>--presentation=</c>: the menu presentation for this run, never persisted.
    /// <see cref="Utils.PresentationResolution"/> ranks it above the Original default and below
    /// <see cref="ForceBuiltInPresentation"/>. Null when the flag is absent.</summary>
    public string? PresentationOverride { get; private set; }

    /// <summary><c>--force-builtin</c>: the startup escape hatch that always resolves to the
    /// Built-in presentation for this run, writing nothing.</summary>
    public bool ForceBuiltInPresentation { get; private set; }

    /// <summary>Which mode-coercing probe (if any) drives this session. The three set a mode at
    /// parse time today; here they vote like anything else and this names the vote.</summary>
    public SessionProbe Probe =>
        DamageTest ? SessionProbe.DamageTest
        : EffectsTest ? SessionProbe.EffectsTest
        : WeaponTest ? SessionProbe.WeaponTest
        : SessionProbe.None;

    /// <summary>Whether <c>_Ready</c> shows the launchscreen before building anything. Independent
    /// of <see cref="Mode"/>: <c>--menu --chapter=C4</c> resolves to flight AND shows the menu.
    /// ⚠ True under <c>--run-tests</c> too, the harness is saved only by returning before the menu
    /// branch. Reproduced deliberately; the equivalence gate compares against it.</summary>
    public bool ShowsMenu => ForceMenu || !HasContentArg;

    /// <summary><c>--skip-intro</c>: no boot sequence on this launch. It exists for the desktop
    /// shortcut, which is the one launch that would otherwise be bare.</summary>
    public bool SkipIntro { get; private set; }

    /// <summary><c>--intro</c>: play the boot sequence even though other arguments were passed.
    /// A dev flag, and the only way to reach the sequence through this repo's launch scripts,
    /// which prepend a volume of their own and would otherwise suppress it. ⚠ Read inside the
    /// launchscreen branch only, so it plays nothing on a launch that builds content.</summary>
    public bool ForceIntro { get; private set; }

    /// <summary>Whether the boot sequence plays before the launchscreen. ⚠ Any argument at all
    /// suppresses it, so no test, golden or perf launch grows by the three minutes those movies
    /// run for. <see cref="SkipIntro"/> is read as well, so the flag denies the sequence by
    /// meaning rather than by being one more argument, and it beats
    /// <see cref="ForceIntro"/> because a suppressor another flag can overrule is not one.</summary>
    public bool PlaysBootSequence => !SkipIntro && (ForceIntro || Args.Count == 0);

    /// <summary>The session shape's name: the log file's, and the startup timing line's. ⚠ The
    /// "dump" arm omits <c>--dump-flight</c>, so a <c>--dump-flight</c> run logs as
    /// <c>menu-*.log</c>. That is today's behaviour, reproduced on purpose.</summary>
    public string ModeName =>
        ExtractInstall != null ? "extract"
        : Mode == SessionMode.AnimLab ? "anim-lab"
        : DamageTest || EffectsTest || WeaponTest || RunTests ? "test"
        : DumpMarkers || DumpWeapons || DumpLoadout || DumpConfig || DumpMips || DumpAi || DumpTileGrid ? "dump"
        : MovieName != null ? "movie"
        : Mode == SessionMode.Freecam ? "freecam"
        : Mode == SessionMode.Viewer ? "viewer"
        : Versus ? "vs"
        : Stunt ? "stunt"
        : Mode == SessionMode.Fly ? "fly"
        : "menu";

    /// <summary>Whether a flag will drive and end this session by itself, so nobody is at the
    /// controls: the window hides instead of asking for focus. ⚠ <c>--dump-flight</c>'s absence is
    /// a drift, not a decision, the same omission as <see cref="ModeName"/>'s, and it means a
    /// <c>--dump-flight</c> run turns the bundle on yet still asks for focus.</summary>
    public bool IsScripted =>
        NoFocus || ScreenshotPath != null || ExportGltfPath != null || RunTests || ExtractInstall != null
        || DumpMarkers || DumpWeapons || DumpLoadout || DumpConfig || DumpMips || DumpAi || DumpTileGrid
        || DamageTest || EffectsTest || WeaponTest;

    /// <summary><b>Resolved.</b> The chapter world is built instead of a single parked plane.</summary>
    public bool WorldMode { get; private set; }
    /// <summary><b>Resolved.</b> <c>--stage=empty</c> survived: no gamez at all, a generated grid
    /// over a collidable ground plane.</summary>
    public bool EmptyStage { get; private set; }

    /// <summary>Whether this session builds the world's colliders, the one definition every
    /// consumer reads. Flight needs them to fly into things; the headless damage sweep and the
    /// world damage lab need them because a kill's collider count would otherwise read zero and
    /// lie; <c>--collision</c> is the interactive request for them in a mode that builds none.</summary>
    public bool BuildsCollision => Fly || DamageTest || ForceCollision || DebugDamage != null;

    // ---- The world ----------------------------------------------------------------------------

    public string Chapter { get; private set; } = "C1";
    /// <summary><b>Resolved.</b> A chapter world was asked for, by <c>--chapter</c>/<c>--chapter=</c>,
    /// or by <c>--node=</c>, whose subtree comes out of the chapter's gamez.</summary>
    public bool ChapterGiven { get; private set; }
    public string Mission { get; private set; } = "IA1";
    /// <summary><b>Resolved.</b> Which instant-action scenario's spawn list to use. <c>--stunt</c>
    /// forces "stunt_flying" and <c>--vs</c> forces "dogfight_ace", unless the tester pinned
    /// another one for a specific spawn.</summary>
    public string Scenario { get; private set; } = "zeppelin_run";
    public bool ScenarioExplicit { get; private set; }
    /// <summary><c>--ia=&lt;path&gt;</c>: fly a mission described by a hand-authored
    /// <c>InstantActionDef</c> JSON file (<c>Mech3.InstantAction.LoadFromJson</c>) instead of the
    /// chapter's own shipped <c>ia.zrd.json</c>. Null when the flag was absent, the value is only
    /// the path; loading it is the runtime's job, which keeps this
    /// type free of file I/O.</summary>
    public string? IaPath { get; private set; }
    /// <summary><c>--campaign=&lt;profile&gt;:&lt;seq&gt;</c>: a campaign session, a selected
    /// <see cref="Session.Campaign.CampaignProfileStore"/> profile plus a <c>cm_sequence.zrd</c> mission
    /// index, carried here as plain values. Null when the flag was absent; loading the profile and
    /// building the mission are the runtime's job, the contract <see cref="IaPath"/> keeps. No new
    /// <see cref="SessionMode"/>: a content arg with no other mode vote resolves to
    /// <see cref="SessionMode.Fly"/>, the same way <c>--stunt</c>/<c>--vs</c> ride it.</summary>
    public string? CampaignProfile { get; private set; }
    /// <summary>The <c>:&lt;seq&gt;</c> half of <c>--campaign=</c>; null when it was omitted or
    /// unparseable, in which case a warning is recorded and only the profile name is kept.</summary>
    public int? CampaignMissionSeq { get; private set; }
    /// <summary><c>--no-crash-loss</c>: losing the aircraft leaves the campaign mission running,
    /// so a session being debugged can fly on past a crash. The game default is the original's
    /// rule, which ends the mission lost (<c>docs/formats/objectives.md</c>, "Win and loss").
    /// Read by <c>GameSession</c> alone; nothing else here derives from it.</summary>
    public bool NoCrashLoss { get; private set; }
    /// <summary>Set only by <see cref="FromMenu"/>: the launchscreen wizard's own built
    /// <c>InstantActionDef</c>, null on every CLI launch since <c>--ia=</c> carries a path
    /// instead. Only ever carried onto the record here, never loaded or built, the same
    /// purity contract <see cref="IaPath"/> (a path, not a load) already keeps.</summary>
    public InstantActionDef? IaDef { get; private set; }
    /// <summary>Set only by <see cref="FromMenu"/>: the wingmen's fit from the wizard's loadout
    /// screen, null for the stock fit and on every CLI launch. Kept beside <see cref="IaDef"/>
    /// rather than on it because a fit is a flight type the Mech3 def must not name.</summary>
    public LoadoutChoice? IaWingmanLoadout { get; private set; }
    /// <summary>The <c>--stage=</c> value as given, unvalidated, only "empty" names a stage.
    /// Whether it survived is <see cref="EmptyStage"/>.</summary>
    public string? Stage { get; private set; }
    public string? NodeName { get; private set; }
    public string SkyZone { get; private set; } = "zone2";
    public bool SkyZoneExplicit { get; private set; }
    public bool NoFog { get; private set; }

    /// <summary>Draw the cockpit interior in a world of its own with the camera and the panel at
    /// the origin (<see cref="Flight.Hud.CockpitOverlay"/>) instead of at chapter-scale world
    /// coordinates under the plane, where its dial faces jitter. On by default;
    /// <c>--no-cockpit-pass</c> draws the interior in the main world for comparison.</summary>
    public bool CockpitPass { get; private set; } = true;

    /// <summary><c>--no-clutter</c>: skip the chapter's ground-clutter build entirely
    /// (<see cref="Mech3.ClutterBuilder"/>, the scattered tree/bush cards and the C2/C5 3D
    /// city-block decorations), so the painted ground they stand on is visible. Does not touch the
    /// ambient cloud field (<c>Effects.FogVolumeClutter</c>), which is a different population that
    /// happens to share the word.</summary>
    public bool NoClutter { get; private set; }

    /// <summary>Resolved. The enhanced passes this run leaves out, one bit per
    /// <c>--no-</c> door. Empty in the faithful presentation, which builds none of them anyway.
    /// See <c>docs/cli.md</c>.</summary>
    public EnhancedPasses SkippedPasses { get; private set; }

    /// <summary><c>--debug-clutterflag</c>: recolour the built world by each polygon's decoded
    /// <c>no_clutter</c> flag (raw polygon bit <c>0x800</c>, <see cref="Mech3.GameZPolygon.NoClutter"/>)
    ///, flagged red, clear green, clutter blue. A build-time recolour, so there is no runtime
    /// toggle. See <c>docs/cli.md</c>.</summary>
    public bool DebugClutterFlag { get; private set; }

    /// <summary><c>--hide-alpha=blend-surfaces,scissor-surfaces,blend-cards,scissor-cards</c>: build
    /// the world without the named transparency classes. A frame where two of them paint in the
    /// wrong order then names the pair by elimination. A build-time drop, like
    /// <see cref="DebugClutterFlag"/>, so there is no runtime toggle. Empty in every ordinary run.
    /// See <c>docs/cli.md</c>.</summary>
    public Mech3.SceneBuilder.TransparencyClass HiddenAlpha { get; private set; }

    /// <summary><c>--clutter-templates=a,b,c</c>: build these clutter templates instead of the
    /// chapter's own <c>AddClutterTemplates</c> list, so one district at a time can be A/B'd
    /// against the original. Names are matched case-insensitively against the gamez's template
    /// roots. The per-polygon <c>no_clutter</c> gate still applies, this replaces the template
    /// SET, not the placement rule. Null → the chapter's own list;
    /// <see cref="NoClutter"/> wins if both are given. See <c>docs/cli.md</c>.</summary>
    public IReadOnlyList<string>? ClutterTemplates { get; private set; }

    /// <summary><c>--no-zone-cull</c>: switch off the gamez <c>zone_id</c> visibility gate
    /// (<see cref="Mech3.ZoneGate"/>), every zone draws at every camera state. The one switch that
    /// isolates a regression anywhere in the world's content at the controls: the gate hides a lot
    /// on purpose (in C1 the whole ground world above the deck, the deck and
    /// the cloud populations below it), so "did the gate eat it?" has to be answerable in one
    /// flag. Not a fidelity switch, the default IS the original's behaviour.</summary>
    public bool NoZoneCull { get; private set; }

    /// <summary><c>--no-flare</c>: suppress the sun's lens flare. Not a fidelity switch,
    /// a verification one. The flare's full-screen wash reaches α ≈ 0.66 and survives terrain
    /// occlusion, so in C2/C3 any capture with the sun near screen centre is contaminated for every
    /// *other* comparison (terrain colour, fog gradient, deck brightness, clutter density). Same
    /// role <c>--no-fog</c> plays for the fog wall.</summary>
    public bool NoFlare { get; private set; }
    /// <summary><c>--mips=authored|generated</c>: whether a texture's levels 1 and 2 come from the
    /// archive's hand-authored <c>_1</c>/<c>_2</c> siblings (the default) or are box-filtered from
    /// the base like every level below them. See <see cref="Mech3.TextureArchive.MipSource"/>.</summary>
    public TextureArchive.MipSource Mips { get; private set; } = TextureArchive.MipSource.Authored;
    public int AnimLod { get; private set; } = AnimRuntime.HighLod;
    public string? DestroyName { get; private set; }
    /// <summary><c>--crash[=frame]</c>: the fixed sim frame (<see cref="Utils.GameClock.Frame"/>)
    /// at which every player's <see cref="Flight.Airframe.FlightController.DebugForceCrash"/> fires, the
    /// only headless trigger for the per-player crash rig (no live collision analog exists).
    /// Null when the flag was absent.</summary>
    public int? CrashFrame { get; private set; }

    // ---- The aircraft -------------------------------------------------------------------------

    public string PlaneName { get; private set; } = "player_bhawk";
    /// <summary>The <c>--plane=</c> list; empty when a single plane (or none) was named.</summary>
    public IReadOnlyList<string> PlaneNames { get; private set; } = Array.Empty<string>();
    /// <summary><b>Resolved.</b> Splitscreen panes. A <c>--plane=</c> list of several states the
    /// count on its own; an explicit <c>--players=</c> still wins. Clamped to the rig's capacity,
    /// and back to 1 for any static view, splitscreen is a flight mode, it needs planes to fly.</summary>
    public int Players { get; private set; } = 1;
    public bool PlayersExplicit { get; private set; }
    public string? LoadoutOverride { get; private set; }
    public string? RocketOverride { get; private set; }

    /// <summary>The fits chosen on the menu's loadout screen, one per player pane and null where a
    /// pane took the stock fit. Empty outside a menu launch. An explicit <see cref="LoadoutOverride"/>
    /// or <see cref="RocketOverride"/> beats these: both are testing flags reaching nothing a player
    /// has, and a playtest row's evidence depends on getting the fit it names.</summary>
    public IReadOnlyList<LoadoutChoice?> MenuLoadouts { get; private set; } = Array.Empty<LoadoutChoice?>();

    /// <summary>The custom plane each player picked, one per pane and null where a pane took a
    /// stock airframe; empty outside a menu launch. The def rides here rather than its store name
    /// because the launch reads it four times (guns, pylons, paint, armour) and a plane deleted
    /// mid-session must not change what is flying.</summary>
    public IReadOnlyList<CustomPlaneDef?> MenuCustomPlanes { get; private set; } =
        Array.Empty<CustomPlaneDef?>();

    public int GunSelect { get; private set; }

    /// <summary><c>--target=</c>: the scripted twin of the targeting keys.
    /// <c>nearest</c> / <c>crosshair</c> / <c>next</c> / <c>none</c>, or the name of a target to pin
    /// (<c>ai1_player_fury</c>). Applied ONCE per human pane, on the first frame its pool has
    /// anything in it; the selection cycles normally afterwards. Null when the flag was absent, which
    /// is not the same as <c>none</c> (absent leaves the ordinary auto-acquire alone).</summary>
    public string? TargetSelect { get; private set; }
    public bool InfiniteAmmo { get; private set; }
    /// <summary>The <c>--ammo=</c> low-ammo start knob: caps both gun groups and pylons to this many
    /// rounds at rig build, bypassing config.json, which <c>--det</c> drops, so the cap survives it.
    /// Null when the flag was absent. Mutually exclusive with <see cref="InfiniteAmmo"/>, whichever
    /// flag comes last in the arg list wins; the loser is logged in <see cref="Warnings"/>.</summary>
    public int? AmmoCap { get; private set; }
    public bool AutoFire { get; private set; }
    public bool AutoFireRockets { get; private set; }
    /// <summary><c>--incoming[=metres[,wep_id]]</c>: the incoming-fire test rig, a phantom shooter
    /// on each player's six walking a burst into the airframe, so both cues and the shield behind
    /// them are reachable without an AI gunner. The metres offset the burst sideways (default none,
    /// on the airframe); a wide one is the rig's own able-to-fail control. Null when the flag was
    /// absent.</summary>
    public float? IncomingPass { get; private set; }
    /// <summary>Which weapon <c>--incoming</c> fires; null takes the target's own first gun.</summary>
    public string? IncomingWeapon { get; private set; }
    /// <summary><c>--ai=&lt;plane&gt;[:&lt;net&gt;][:accent=&lt;id&gt;][:def=&lt;vehicle def&gt;][:team=&lt;id&gt;][:n=&lt;count&gt;][:pos=x,y,z][,…]</c>:
    /// AI-piloted aircraft spawned into the flight session (docs/cli.md). <c>def=</c> names the
    /// militia variant flown; without it the airframe's base def. One entry can stand for a
    /// squadron (<see cref="AiPlaneEntry.Count"/>). Null when the flag was absent.</summary>
    public IReadOnlyList<AiPlaneEntry>? AiPlanes { get; private set; }
    /// <summary><c>--zep=&lt;chapter&gt;/&lt;mission&gt;:&lt;record&gt;[:team=&lt;id&gt;][:pos=x/y/z]</c>: one
    /// zeppelin record's hull grafted onto <c>--stage=empty</c> and wired through
    /// <c>ZeppelinRuntime</c> the way a mission's own is (docs/cli.md). Null when the flag was
    /// absent. ⚠ Takes <see cref="Chapter"/> and <see cref="Mission"/> with it: the record's
    /// gamez, textures, nets and <c>zeppelins.zrd.json</c> are all read off those, so the graft
    /// needs no second chapter/mission pair threaded through the build.</summary>
    public ZepStageSpec? Zep { get; private set; }
    /// <summary><c>--ai-damage=&lt;fraction&gt;</c>: the hull health fraction every <c>--ai=</c> plane
    /// is spent down to as it spawns, so its authored injure_anims stages are already up in a
    /// scripted shot. Null when the flag was absent. <c>--damage=</c> is the player's counterpart
    /// and writes per-part HP, which an AI airframe resolves none of.</summary>
    public float? AiHullDamage { get; private set; }
    /// <summary><c>--canopy-holes[=&lt;1-5&gt;]</c>: open this many of the five canopy glass holes on
    /// the flown aircraft as it spawns. It opens them through the same cue and
    /// <c>cockpit_bulletholes</c> defs an incoming burst uses. A scripted shot then catches the
    /// glass already struck. Null when the flag was absent.</summary>
    public int? CanopyHoles { get; private set; }
    /// <summary><c>--ai-attack[=&lt;1-9&gt;]</c>: arm every AI plane this session spawns with the
    /// D14 forward-gun gunnery at the given skill rating (dead-eye/quick-draw interpolated from
    /// <c>ai_skill_parameters</c>; default 5), auto-targeting the nearest hostile aircraft.
    /// Null when the flag was absent.</summary>
    public int? AiAttackSkill { get; private set; }

    /// <summary><c>--ai-targeting=&lt;aircraft-first|decoded&gt;</c>: whether both AI pickers rank
    /// live enemy aircraft ahead of every turret and structure candidate, which is the default.
    /// The other word runs the decoded picker's own order, which has no class priority
    /// (<see cref="Flight.Ai.AiTargetRanking.AircraftFirst"/>). The class biases are spent either
    /// way.</summary>
    public bool AircraftFirstTargeting { get; private set; } = true;

    /// <summary>True only for <c>--ai-attack=&lt;N&gt;</c>, the typed rating. Bare <c>--ai-attack</c>
    /// arms the gunnery without an opinion on skill, so each plane flies its own vehicle def's
    /// authored slots; a typed rating pins every plane to it instead.</summary>
    public bool AiAttackSkillExplicit { get; private set; }

    /// <summary><c>--difficulty=&lt;normal|hard|hardest&gt;</c> (or <c>game.difficulty</c>): the
    /// setting a hostile spawn's armour and health scale by (0.75 / 1.0 / 1.25) and its pilot's
    /// nine skill ratings shift by (-2 / 0 / +2), both from one k
    /// (<see cref="CSVM.Flight.Hangar.Difficulty"/>). Defaults to Normal, which is what the executable's
    /// own settings registration writes.</summary>
    public int Difficulty { get; private set; } = CSVM.Flight.Hangar.Difficulty.Normal;

    /// <summary>True when <c>--difficulty=</c> named a tier this parser took. The flag outranks
    /// the saved option (<see cref="WithSavedDifficulty"/>), and a flag whose word was refused
    /// counts as absent, so the saved tier still applies under a typo.</summary>
    public bool DifficultyExplicit { get; private set; }

    /// <summary>The saved targeting setting (<see cref="WithSavedNearestAfterKill"/>), off by
    /// default: a target that dies is replaced by the nearest live member of the current cycle
    /// rather than by the cycle's head. No flag names it, an Options screen is its only source.</summary>
    public bool NearestAfterKill { get; private set; }

    /// <summary><c>--no-assist</c>: disable the D15 rubber-band assist, every spawned AI mode
    /// machine gets <c>AssistEnabled</c> false, so the lay-off mode is never entered (pursue
    /// only). Default off: the assist is the original's shipped behaviour.</summary>
    public bool NoAssist { get; private set; }

    /// <summary><c>--craters</c>: arm the rocket carve for this run, so every rocket warhead's
    /// ground burst digs a bowl. Off by default and saved nowhere. No screen offers the carve, so
    /// this flag and the options file's own <c>rocketCraters</c> key are its two doors. This one
    /// survives <c>--det</c>, which reads no saved option, so a probe can photograph a bowl
    /// without any pinned golden ever seeing one.</summary>
    public bool Craters { get; private set; }

    /// <summary><c>--generators[=plane]</c>: run the mission's egen enemy generators;
    /// each surviving generator spawns AI aircraft on its decoded wave/period cycle through the
    /// same runtime spawn seam <c>--ai=</c> uses. The optional value picks the airframe of a
    /// generator that authors no <c>vehicle.params</c>; a label that names no block builds
    /// nothing, the decoded rule, never this airframe.</summary>
    public bool Generators { get; private set; }
    /// <summary>Which plane <c>--generators</c> spawns; the default is the default flight plane.</summary>
    public string GeneratorsPlane { get; private set; } = "player_bhawk";
    /// <summary><c>--zeppelins</c>: place and fly the mission's zeppelins (M4 F17), each
    /// <c>zeppelins.zrd.json</c> record whose world node and net resolve is set to its authored
    /// position/yaw/pitch and moved along its net under the record's speed/accel/pitch/rate
    /// limits (a kinematic node follow, no flight model).</summary>
    public bool Zeppelins { get; private set; }
    /// <summary><c>--wake-turrets</c>: wake every dormant world AA emplacement at build (M4
    /// C9b). The emplacements themselves always build with a chapter flight; 22 of the 26
    /// standalone <c>ai.zrd</c> entries ship <c>ACTIVATED 0</c> and the real wake mechanism is
    /// the mission script's <c>WAKEUP_TURRETS</c>, which M4 does not implement, this flag is
    /// the documented, logged stand-in, never a silent default.</summary>
    public bool WakeTurrets { get; private set; }
    /// <summary><c>--wake-generators</c>: grant every generator a campaign mission's script gates
    /// behind <c>WAKEUP_GENERATOR</c> the whole of that script's credit at session build, so a
    /// headless run launches without playing the mission up to the objective. A logged stand-in
    /// for the objective's own grant, never a silent default; inert outside a campaign mission,
    /// whose generators run uncredited anyway.</summary>
    public bool WakeGenerators { get; private set; }
    public (FlightInput, float)[][]? HoldSets { get; private set; }

    /// <summary>The <c>--lever=</c> schedule: commanded-lever steps in eighths, each at its own
    /// sim-second, played on every human-flown seat as the digit row's keys play them. Null when
    /// the flag is absent, which is every ordinary run.</summary>
    public IReadOnlyList<(float At, float Lever)>? LeverSteps { get; private set; }
    /// <summary>The <c>--damage=</c> preset pairs (part, fraction 0–1); null when <c>--damage</c>
    /// carried no value.</summary>
    public IReadOnlyList<(string Part, float Fraction)>? DamagePreset { get; private set; }

    // ---- Liveries -----------------------------------------------------------------------------

    public IReadOnlyList<string>? PaintNames { get; private set; }
    public IReadOnlyList<Color>? PaintColorOverride { get; private set; }
    public IReadOnlyList<int>? PaintDecalOverride { get; private set; }
    public ulong PaintSeed { get; private set; }
    public bool PaintSeedExplicit { get; private set; }

    // ---- The --det bundle, resolved in one place -----------------------------------------------

    /// <summary>Which flag turns the bundle on by driving and ending the session itself, in
    /// precedence order; empty when none does.</summary>
    public string ScriptedBy =>
        ScreenshotPath != null ? "--screenshot"
        : ExportGltfPath != null ? "--export-gltf"
        : DumpMarkers ? "--dump-markers"
        : DumpWeapons ? "--dump-weapons"
        : DumpLoadout ? "--dump-loadout"
        : DumpFlight ? "--dump-flight"
        : DumpConfig ? "--dump-config"
        : DumpMips ? "--dump-mips"
        : DumpAi ? "--dump-ai"
        : DumpTileGrid ? "--dump-tilegrid"
        : DamageTest ? "--damage-test"
        : EffectsTest ? "--effects-test"
        : WeaponTest ? "--weapon-test"
        : RunTests ? "--run-tests"
        : "";

    /// <summary><c>--det</c> was passed outright, rather than implied.</summary>
    public bool DetExplicit => _detArg;
    /// <summary><b>Resolved.</b> The deterministic bundle is on: fixed-dt clock, pinned master
    /// seed, spawn 0, pinned liveries, no pads, no jitter. <c>--no-det</c> beats both the
    /// implication and an explicit <c>--det</c>, there is one way to ask for wall-clock
    /// behaviour, whatever else is on the command line.</summary>
    public bool Det => !NoDet && (DetExplicit || ScriptedBy.Length > 0);
    public bool NoDet { get; private set; }
    /// <summary>What turned the bundle on, for the announcement line.</summary>
    public string DetVia => DetExplicit ? "--det" : ScriptedBy;
    /// <summary><c>--seed=N</c>, null when unset.</summary>
    public ulong? Seed { get; private set; }
    /// <summary>Whether the master seed is pinned rather than drawn from the clock. The anim lab
    /// pins it by nature, its whole point is an identical replay.</summary>
    public bool SeedPinned => Seed != null || Det || Mode == SessionMode.AnimLab;
    /// <summary>The master seed when it is pinned, else null, the clock draw stays outside, or
    /// this type would not be a function of its args (and a baseline would differ from itself).</summary>
    public ulong? PinnedSeed => SeedPinned ? Seed ?? Rng.DefaultSeed : null;
    /// <summary><b>Resolved.</b> <c>--spawn=N</c>; &lt; 0 means "random pick". <c>--det</c> pins it
    /// to 0, a pinned CHOICE beats a pinned dice roll, since a seeded pick still moves when the
    /// mission's spawn list grows. An explicit <c>--spawn=N</c> still wins.</summary>
    public int SpawnIndex { get; private set; } = -1;
    /// <summary><c>--no-pads</c> was passed. Nothing here touches <c>Pads.Disabled</c>.</summary>
    public bool NoPads { get; private set; }
    /// <summary>Whether every gamepad is ignored: asked for, or implied by the bundle, a stick
    /// with drift steers the free camera and nudges the flight model.</summary>
    public bool PadsDisabled => NoPads || Det;
    /// <summary><b>Resolved.</b> Burst camera dither in degrees. Bursts default it on so
    /// z-fighting flickers across frames; <c>--det</c> defaults it off instead, since
    /// bit-identical frames are the one property a deterministic run is for.</summary>
    public float JitterDeg { get; private set; } = -1f;

    // ---- Placement, as written on the command line --------------------------------------------

    public Vector3? Pos { get; private set; }
    /// <summary><b>Resolved.</b> Normalised, and derived from <see cref="LookAt"/> in flight, a
    /// point converted against the eye. Null once a degenerate vector is dropped.</summary>
    public Vector3? Direction { get; private set; }
    /// <summary>A POINT, not a vector: the orbit view's pivot, and the freecam's aim when no
    /// <c>--pos</c> was given. The conversion to a direction is one-way and flight-only.</summary>
    public Vector3? LookAt { get; private set; }
    /// <summary><b>Resolved.</b> Where the plane spawns: <c>--pos</c> routed here in flight, or the
    /// deprecated <c>--spawn-at=</c>, or the empty stage's default.</summary>
    public Vector3? SpawnAt { get; private set; }
    /// <summary><b>Resolved.</b> The nose direction there, <c>--direction</c> routed in flight, or
    /// the deprecated <c>--spawn-dir=</c>.</summary>
    public Vector3? SpawnDir { get; private set; }
    /// <summary><b>Resolved.</b> The camera eye: <c>--pos</c> routed here outside flight, or the
    /// deprecated <c>--campos=</c> (which never places the plane, in any mode).</summary>
    public Vector3? CamPos { get; private set; }
    /// <summary><b>Resolved.</b> The camera's aim, held apart from <see cref="LookAt"/> because a
    /// direction names no pivot and the orbit view needs one.</summary>
    public Vector3? CamDir { get; private set; }
    public float? Yaw { get; private set; }
    public float? Pitch { get; private set; }
    /// <summary><b>Resolved.</b> The <c>--view=</c> numpad digit (0 = chase;
    /// <see cref="Flight.Camera.CameraController.PinnedBackView"/> = the look-behind, <c>--view=back</c>).
    /// The numpad views orbit a FLYING plane, so one asked for outside flight is dropped.</summary>
    public int View { get; private set; }
    /// <summary><b>Resolved.</b> The <c>--view=</c> SELECTED view mode, <c>chase</c> (the default),
    /// <c>cockpit</c> or <c>nose</c>. Held apart from <see cref="View"/> because the two are
    /// different things: a numpad digit is a momentary pose held for the run, this is the view the
    /// pilot flies in and the one the cycle key changes. Dropped outside flight, like
    /// <see cref="View"/>.</summary>
    public Flight.Camera.PilotViewMode ViewMode { get; private set; }
    /// <summary>Whether <see cref="ViewMode"/> came from a <c>--view=</c> naming a mode rather than
    /// from the default. Held because Chase is both the default and a nameable mode, so the value
    /// alone cannot say whether the command line asked for it. That matters because
    /// <see cref="WithSavedDefaultView"/> must not overrule a <c>--view=chase</c>.</summary>
    public bool ViewModeExplicit { get; private set; }
    /// <summary>Whether the pilot's head turns with the aircraft in the cockpit, as the options
    /// file has it. It is null where never set, which leaves the <c>headLook.autohead</c> config
    /// key deciding (<see cref="Flight.Airframe.FlightController.AutoHeadTurn"/>). Dropped under
    /// <c>--det</c> like every other saved option.</summary>
    public bool? AutoHeadTurn { get; private set; }
    /// <summary>The <c>--look=x,y</c> right-stick deflection held for the whole
    /// run, both in [−1, 1], +x right and +y up: the scripted twin of pushing the look stick, and
    /// the only way a headless run aims it. Drives the chase camera's swing and the first-person
    /// head alike, so one run can compare the two. Zero (the default) is a centred stick, i.e.
    /// exactly today's behaviour, and a live stick beats it while deflected.</summary>
    public Vector2 PinnedLook { get; private set; }
    /// <summary>Deprecated spellings seen, first-seen order, deduplicated, with their replacement.</summary>
    public IReadOnlyList<(string Old, string New)> Deprecated { get; private set; }
        = Array.Empty<(string, string)>();

    // ---- Capture ------------------------------------------------------------------------------

    public string? ScreenshotPath { get; private set; }
    public int ScreenshotFrames { get; private set; } = 15;
    public int ScreenshotShots { get; private set; } = 1;
    /// <summary>The <c>--export-gltf=</c> target path; null when the flag was absent. The plane
    /// subtree is written there as glTF once the session builds it.</summary>
    public string? ExportGltfPath { get; private set; }

    // ---- The probes that drive and end a session themselves ------------------------------------

    public bool DumpMarkers { get; private set; }
    public string DumpMarkersPlane { get; private set; } = "";
    public bool DumpWeapons { get; private set; }
    public string DumpWeaponsFilter { get; private set; } = "";
    public bool DumpLoadout { get; private set; }
    public string DumpLoadoutFilter { get; private set; } = "";
    public bool DumpFlight { get; private set; }
    public string DumpFlightPlane { get; private set; } = "";
    public bool DumpConfig { get; private set; }
    public bool DumpMips { get; private set; }
    public string DumpMipsFilter { get; private set; } = "";

    /// <summary><c>--dump-ai</c>: a pure-data report over the five AI data families (patrol
    /// nets, <c>aiv</c> rosters, <c>ai.zrd</c> turrets, zeppelins, generators), no world, no
    /// scene. Scans every chapter/mission dir under the data root so its totals are the
    /// install-wide counts; an optional value restricts the nets/aiv/zeppelins/egen half to one
    /// chapter (turrets are one shared file and are always reported in full).</summary>
    public bool DumpAi { get; private set; }
    public string DumpAiChapter { get; private set; } = "";

    /// <summary><c>--dump-tilegrid</c>: build the chapter world, write the map-edge tile census
    /// and quit. The written twin of <c>--debug-tilegrid</c>, and strictly more: the
    /// overlay can only paint the tiles the extender ACCEPTED, so a rejected border tile, the
    /// thing that leaves a void strip through the continuation, is visible on screen solely as
    /// the hole it causes, and here as a row saying which node and why.</summary>
    public bool DumpTileGrid { get; private set; }

    /// <summary>Where <c>--dump-tilegrid=</c> writes; empty means <c>./.scratch/</c> under a
    /// per-chapter name.</summary>
    public string DumpTileGridPath { get; private set; } = "";

    public bool DamageTest { get; private set; }
    public string DamageTestFilter { get; private set; } = "";
    public float DamageHd { get; private set; }
    public bool EffectsTest { get; private set; }
    public bool WeaponTest { get; private set; }
    public bool RunTests { get; private set; }
    public string RunTestsFilter { get; private set; } = "";
    public bool HudFontTest { get; private set; }
    public string HudFontTestText { get; private set; } = "GUNS 30: 2000  ROCKETS 06: 9";

    // ---- Inspection overlays and labs ----------------------------------------------------------

    public bool DebugAnim { get; private set; }
    public bool DebugAnimUi { get; private set; }
    public string? PlayAnim { get; private set; }
    public bool DebugDzPaths { get; private set; }
    /// <summary><c>--debug-ainets[=name,…]</c>: open the AI patrol-net overlay (F13) at
    /// launch. Null = flag absent; empty = every net; else the comma-separated net names to
    /// build.</summary>
    public string? DebugAiNets { get; private set; }

    /// <summary><c>--debug-targets</c>: open the targeting overlay (F14) at launch, a line from
    /// every turret gunner and AI gunner to the target it has acquired, coloured by the gate that
    /// is holding its trigger.</summary>
    public bool DebugTargets { get; private set; }

    public bool DebugScoreboard { get; private set; }

    /// <summary><c>--debug-pause[=frame]</c>: open the pause board at that sim frame, the scripted
    /// twin of the Start press, so a <c>--screenshot</c> captures the pause screen with nobody at
    /// the controls. A frame late enough for the mission to have run is the point: the objectives
    /// readout is drawn there, and it can only show a completion the sim has actually reached.
    /// Null when the flag was absent.</summary>
    public int? DebugPauseFrame { get; private set; }

    /// <summary><c>--debug-objective=N</c>: wake campaign objective N on the first sim step, the
    /// scripted twin of whatever the mission normally wakes it with. A dormant objective cannot
    /// complete, so this is what lets a headless run reach a completion (pair it with
    /// <c>--destroy=</c> on the nodes its INACTIVEn conditions name). Null when the flag was
    /// absent.</summary>
    public int? DebugObjective { get; private set; }

    /// <summary><c>--debug-wash=N</c>: address two scripted blend washes to viewer N (1-based)
    /// through <c>ScreenFlash.PlayBlend</c>, a red one on the first sim step and a white one two
    /// seconds later, so the victim-routed channel and its blending can be seen at the controls
    /// with no weapon firing it. Null = flag absent.</summary>
    public int? DebugWash { get; private set; }

    /// <summary><c>--debug-markers</c>: mark EVERY live aircraft on the targeting HUD at once,
    /// red for a hostile team and blue for your own, instead of the shipped single
    /// nearest-hostile marker. A watching aid for AI work (whose plane is where), never a
    /// gameplay feature.</summary>
    public bool DebugMarkers { get; private set; }

    /// <summary><c>--debug-spectate</c>: build the session exactly as it would be flown, then take
    /// every human OUT of it: the aircraft goes inert (undrawn, uncollidable, and absent from
    /// every AI's live candidate list, so nothing pursues you) and the pane switches to the
    /// <c>SpectatorCamera</c>. The way to watch what the AI does when no player is provoking
    /// it.</summary>
    public bool DebugSpectate { get; private set; }

    /// <summary><c>--debug-load[=dir]</c>: stand the real load screen over a CLI launch, the way a
    /// menu launch does. It writes one PNG per presented pump frame into that folder. The only way
    /// to watch the bar fill with nobody at the menu; empty string = screen but no captures.</summary>
    public string? DebugLoad { get; private set; }
    public int? DebugLivery { get; private set; }
    public string? DebugMesh { get; private set; }
    public string? DebugNames { get; private set; }
    /// <summary><c>--debug-fps[=compact|full]</c>: start the frame-cost
    /// readout (<c>F14</c>) at launch, the scripted twin for a deterministic screenshot of it.
    /// Null = flag absent (off); no value = compact.</summary>
    public string? DebugFps { get; private set; }
    /// <summary><b>Resolved.</b> Null outside <c>--freecam</c>/<c>--anim-lab</c>: the shared
    /// selection lives in the two world-observation modes, the viewer's LMB is already the orbit
    /// drag, and flight has no cursor.</summary>
    public string? DebugSelect { get; private set; }
    /// <summary><b>Resolved.</b> Filtered to the lab's token grammar
    /// (<c>UI.Labs.NodeLab.ParseDebugSpec</c>, called with a reject list so it hands them back as data
    /// instead of logging), and null outside <c>--freecam</c>/<c>--anim-lab</c>.</summary>
    public string? DebugNodeLab { get; private set; }
    /// <summary><b>Resolved.</b> Same treatment as <see cref="DebugNodeLab"/>, through
    /// <c>UI.Labs.WorldDamageLab.ParseDebugSpec</c>.</summary>
    public string? DebugDamage { get; private set; }
    public int DebugJoin { get; private set; }
    /// <summary><c>--debug-waves=N</c> (launchscreen only): pre-configure the first N
    /// (clamped 0-4) Instant Action wizard wave slots with a representative load, so the wave
    /// editor's "N waves configured" states are screenshot-able with nobody at the controls. Same
    /// role <see cref="DebugJoin"/> plays for the plane screen's join strip.</summary>
    public int DebugWaves { get; private set; }
    /// <summary><c>--debug-wingmen=N</c> (launchscreen only): pre-configure the Instant
    /// Action wizard's wingman count (clamped 0-5), so the plane screen's flown-wingmen re-clamp
    /// (decision 8a) is screenshot-able alongside <c>--debug-join=</c>.</summary>
    public int DebugWingmen { get; private set; }
    /// <summary><c>--debug-preset=N</c> (launchscreen only): apply Table of Contents preset N and
    /// open on the wizard's step 1, so the FILLED wizard is screenshot-able. −1 = not asked for,
    /// since preset 0 ("Girl Trouble") is a real request unlike a 0 wave or wingman count.</summary>
    public int DebugPreset { get; private set; } = -1;
    /// <summary><c>--debug-pointer=x,y[,down][,right]</c> (the Original presentation only): stand
    /// seat 0's pointer at that authored 800x600 point, its buttons held by the words that follow,
    /// so a plaque's rollover and held states are screenshot-able with nobody at the controls.
    /// Authored rather than window pixels, so a shot lands on the same widget whatever the window.
    /// Null = not asked for.</summary>
    public (float X, float Y, bool Down, bool Right)? DebugPointer { get; private set; }
    public bool MarkersOverlay { get; private set; }
    public bool WeaponLab { get; private set; }
    public string? WeaponSelect { get; private set; }
    public string? WeaponMount { get; private set; }
    public bool WeaponFire { get; private set; }
    /// <summary><c>--weapon-cycle[=frames]</c>: in a weapon-lab session, step the current bank's
    /// weapon list one entry every N physics frames, the scripted twin of the panel's weapon
    /// stepper, so the arm/ordnance-rebuild path can be walked with nobody at the controls. 0 = off.</summary>
    public int WeaponCycle { get; private set; }
    /// <summary><c>--weapon-click[=x,y]</c>: replay one weapon-lab click at that viewport pixel on
    /// the first physics frame, the scripted twin of click-to-place. Set with no value to click
    /// the viewport centre, which is why the flag and its coordinates are separate fields.</summary>
    public bool WeaponClick { get; private set; }
    /// <summary><b>Resolved.</b> The pixel <see cref="WeaponClick"/> names, or null for the
    /// viewport centre.</summary>
    public Vector2? WeaponClickAt { get; private set; }
    /// <summary><b>Resolved.</b> The <c>,aim</c> suffix: aim at the picked point without
    /// re-parking, the scripted twin of shift-click.</summary>
    public bool WeaponClickAimOnly { get; private set; }
    /// <summary><c>--weapon-target=x,y,z</c>: park the lab's aircraft facing that world point.</summary>
    public Vector3? WeaponTarget { get; private set; }
    /// <summary><c>--weapon-surface=&lt;registry name&gt;</c>: park facing the nearest collider
    /// carrying that surface id. <b>Resolved</b>, a name outside the registry is warned about and
    /// dropped.</summary>
    public string? WeaponSurface { get; private set; }
    /// <summary><c>--weapon-standoff=&lt;m&gt;</c>: the lab's parking distance, 0 for the 90 m
    /// default.</summary>
    public float WeaponStandoff { get; private set; }
    /// <summary><c>--weapon-camera=free</c>: the lab starts with the view handed to its free
    /// camera rather than the orbit.</summary>
    public bool WeaponFreeCamera { get; private set; }
    /// <summary><c>--weapon-camera=&lt;N&gt;</c>: hand the view over and back every N physics
    /// frames, the scripted twin of tapping V. 0 = off.</summary>
    public int WeaponCameraToggle { get; private set; }

    // ---- Collision ----------------------------------------------------------------------------

    public bool ForceCollision { get; private set; }
    public bool ShowColliders { get; private set; }
    public bool DebugCollision { get; private set; }
    public bool ShowClassOverlay { get; private set; }
    public bool ShowTileGrid { get; private set; }

    // ---- Map-edge continuation: the fold past the map boundary ---------------------------------

    /// <summary><c>--map-edge-block=N</c>: how many border cells deep the repeated block is.
    /// <b>Null = not given</b>, which resolves to <c>MapEdgeExtender.DefaultBlockCells</c> for the
    /// chapter (2 on C1/C2/C4, 1 elsewhere). Kept nullable rather than defaulted here
    /// precisely so "the user asked for 2" stays distinguishable from "this chapter is 2", which a
    /// plain int would erase. <c>MapEdgeExtender</c> clamps it to the chapter's grid, so an
    /// out-of-range value is not an error.</summary>
    public int? MapEdgeBlock { get; private set; }

    /// <summary><c>--map-edge-mode=mirror</c> turns this off. <b>True by default: repetition is
    /// what the original does</b>, A/B'd at the controls. Alternating reflection is kept only as
    /// something to look at.</summary>
    public bool MapEdgeRepeat { get; private set; } = true;

    // ---- Where the data comes from: override VALUES only, null = not given ---------------------

    /// <summary><c>--data-root=</c> verbatim. The precedence against <c>CSVM_DATA_ROOT</c> and the
    /// repo root, and the paths derived from the winner, are resolution.</summary>
    public string? DataRoot { get; private set; }

    /// <summary><c>--extract=&lt;install&gt;</c> verbatim: extract that install into the data root's
    /// <c>extracted</c> folder and quit with the verdict. Empty for a bare <c>--extract</c>, which
    /// the install check refuses; null when the flag was absent.</summary>
    public string? ExtractInstall { get; private set; }

    /// <summary><c>--extract-force</c>: redo every output however new.</summary>
    public bool ExtractForce { get; private set; }

    /// <summary><c>--extract-unzip</c>: also expand every produced zip into its sibling folder.</summary>
    public bool ExtractUnzip { get; private set; }

    /// <summary><c>--unzbd=</c> verbatim: the unzbd an extraction runs instead of the default.</summary>
    public string? UnzbdPath { get; private set; }

    public string? Gamez { get; private set; }
    public string? Textures { get; private set; }
    public string? Zrdr { get; private set; }
    public string? Sounds { get; private set; }
    public string? Interp { get; private set; }
    public string? Messages { get; private set; }
    public string? Rof { get; private set; }

    // ---- Texture drop-in (applied to statics by the caller, not here) --------------------------

    public IReadOnlyList<string> TexOverrides { get; private set; } = Array.Empty<string>();
    public bool TexCensus { get; private set; }
    public string TexCensusFilter { get; private set; } = "";

    /// <summary><c>--graphics=original|enhanced</c>. Null when not given, which lets the
    /// <c>graphics.mode</c> config key supply the value instead, an explicit flag always beats
    /// the config file, including under <c>--det</c>, so a golden or a deterministic capture can
    /// still ask for the enhanced path on purpose. An unrecognised word is null with a warning
    /// (kept, not treated as given), the same rule <c>--collision=</c> uses for a bad value.</summary>
    public string? GraphicsMode { get; private set; }

    // ---- Everything else ------------------------------------------------------------------------

    public bool Mute { get; private set; }

    /// <summary>Master output gain, linear, 0 (silent) to 1 (unattenuated). Null when
    /// <c>--volume=</c> was not given, which is what lets the <c>audio.volume</c> config key
    /// supply the value instead, an explicit flag always beats the tuning file.
    ///
    /// <para>Unrelated to <see cref="Mute"/>, which skips loading the audio subsystem entirely.
    /// At volume 0 every sound still loads, plays, counts and logs; it is simply inaudible.</para></summary>
    public float? Volume { get; private set; }

    /// <summary><c>--sound-range-scale=</c>: the multiplier on every positional sound's <c>RANGE</c>
    /// radii, read into <see cref="Mech3.SoundFalloff.RangeScale"/>. It is
    /// <see cref="Mech3.SoundFalloff.ShippedRangeScale"/>, the remake-only departure that gives the
    /// world emitters the original's reach, when absent; 1 is the data's own radii. A value that is
    /// not a positive number is ignored with a warning.</summary>
    public float SoundRangeScale { get; private set; } = Mech3.SoundFalloff.ShippedRangeScale;

    /// <summary><c>--cloud-jitter=</c>: a remake-only extra X/Z offset, in metres, on every
    /// lattice-scattered <c>fvol</c> cloud card (<see cref="Effects.FogVolumeClutter"/>). It is
    /// <see cref="Effects.FogVolumeClutter.ShippedJitter"/>, the offset that breaks the decoded
    /// lattice's rows, when absent; 0 is the decoded field untouched. A value that is not a
    /// non-negative number is ignored with a warning.</summary>
    public float CloudJitter { get; private set; } = Effects.FogVolumeClutter.ShippedJitter;

    public bool NoVsync { get; private set; }
    public bool Perf { get; private set; }
    public bool GcTypes { get; private set; }
    public bool NoFocus { get; private set; }

    /// <summary><c>--zip-assets</c>: read the <c>.zip</c> archives even where an unpacked sibling
    /// folder exists, so a development machine runs the asset shape an export ships. See
    /// <see cref="SessionPaths.ForceZipped"/> for why the two shapes fail differently.</summary>
    public bool ZipAssets { get; private set; }

    /// <summary><c>--hitch-inject=</c>: a synthetic stall of known
    /// magnitude, in milliseconds, so every later item in the plan has something deterministic to
    /// verify against instead of an incidental hitch. Null when the flag was absent.</summary>
    public float? HitchInjectMs { get; private set; }
    /// <summary>Whether the stall burns its time allocating and discarding 4 KB buffers (moves the
    /// GC/allocated-bytes columns) rather than spinning the CPU (proves only the timing path), the
    /// <c>alloc:</c> value prefix. Meaningless when <see cref="HitchInjectMs"/> is null.</summary>
    public bool HitchInjectAlloc { get; private set; }
    /// <summary>The <see cref="Utils.HitchMonitor.FrameCount"/>-space frame ordinal the stall fires
    /// on, never the sim frame, since the injector has to work with no session built at all (the
    /// launchscreen, <c>--viewer</c>). Meaningless when <see cref="HitchInjectMs"/> is null.</summary>
    public int HitchInjectFrame { get; private set; } = DefaultHitchInjectFrame;
    /// <summary>The <c>--log=</c> specs in command-line order. <c>--debug-anim</c>'s implied
    /// "anim:debug,sound:debug" is not one of them, that is resolution.</summary>
    public IReadOnlyList<string> LogSpecs { get; private set; } = Array.Empty<string>();

    /// <summary>Parses the user args and resolves them: the returned spec is the finished answer.
    /// Last occurrence of a flag wins, as it does in the loop this mirrors; an unrecognised arg is
    /// ignored silently, also as today.</summary>
    public static SessionSpec Parse(IEnumerable<string> args)
    {
        var argv = args.ToArray();
        var s = new SessionSpec { Args = argv };
        var notes = new List<Note>();
        var deprecated = new List<(string Old, string New)>();
        var logSpecs = new List<string>();
        var texOverrides = new List<string>();

        void Deprecate(string old, string replacement)
        {
            if (!deprecated.Exists(d => d.Old == old))
            {
                deprecated.Add((old, replacement));
            }
        }

        foreach (var arg in argv)
        {
            if (arg.StartsWith("--plane="))
            {
                s.PlaneNames = ParsePlanes(arg["--plane=".Length..]);
                if (s.PlaneNames.Count > 0)
                {
                    s.PlaneName = s.PlaneNames[0];
                }
                s.HasContentArg = true;
            }
            else if (arg == "--viewer") { s._viewerArg = true; s.HasContentArg = true; }
            else if (arg == "--damage") { s._damageLabArg = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--damage="))
            {
                s._damageLabArg = true;
                var rejected = new List<string>();
                s.DamagePreset = ParseDamagePreset(arg["--damage=".Length..], rejected);
                foreach (var bad in rejected)
                {
                    notes.Add(new Note("", $"--damage: cannot parse '{bad}' (want part:fraction)"));
                }
                s.HasContentArg = true;
            }
            else if (arg == "--chapter") { s.ChapterGiven = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--chapter=")) { s.Chapter = arg["--chapter=".Length..]; s.ChapterGiven = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--stage=")) { s.Stage = arg["--stage=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--node=")) { s.NodeName = arg["--node=".Length..]; s.HasContentArg = true; }
            else if (arg == "--fly") { s._flyArg = true; s.HasContentArg = true; }
            else if (arg == "--stunt") { s._stuntArg = true; s.HasContentArg = true; }
            else if (arg == "--vs") { s._vsArg = true; s.HasContentArg = true; }
            else if (arg == "--coop") { s.Coop = true; }
            else if (arg.StartsWith("--vs-kills=")) { s.VsKills = int.Parse(arg["--vs-kills=".Length..]); s.VsKillsExplicit = true; }
            else if (arg.StartsWith("--vs-time=")) { s.VsTimeMinutes = int.Parse(arg["--vs-time=".Length..]); s.VsTimeExplicit = true; }
            else if (arg == "--freecam") { s._freecamArg = true; s.HasContentArg = true; }
            else if (arg == "--anim-lab") { s._animLabArg = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--play-anim=")) { s.PlayAnim = arg["--play-anim=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--seed=")) { s.Seed = ulong.Parse(arg["--seed=".Length..]); }
            else if (arg == "--debug-anim-ui") { s.DebugAnimUi = true; s.HasContentArg = true; }
            else if (arg == "--debug-anim") { s.DebugAnim = true; }
            else if (arg == "--no-pads") { s.NoPads = true; }
            else if (arg == "--no-crash-loss") { s.NoCrashLoss = true; }
            else if (arg == "--det") { s._detArg = true; }
            else if (arg == "--no-det") { s.NoDet = true; }
            else if (arg == "--perf") { s.Perf = true; }
            else if (arg == "--gc-types") { s.Perf = true; s.GcTypes = true; }
            else if (arg.StartsWith("--log=")) { logSpecs.Add(arg["--log=".Length..]); }
            else if (arg.StartsWith("--anim-lod=")) { s.AnimLod = int.Parse(arg["--anim-lod=".Length..]); }
            else if (arg.StartsWith("--movie=")) { s.MovieName = arg["--movie=".Length..]; s.HasContentArg = true; }
            else if (arg == "--skip-intro") { s.SkipIntro = true; }
            else if (arg == "--intro") { s.ForceIntro = true; }
            else if (arg == "--menu") { s.ForceMenu = true; }
            else if (arg.StartsWith("--menu=")) { s.ForceMenu = true; s.MenuStartScreen = arg["--menu=".Length..]; }
            else if (arg.StartsWith("--presentation=")) { s.PresentationOverride = arg["--presentation=".Length..]; }
            else if (arg == "--force-builtin") { s.ForceBuiltInPresentation = true; }
            else if (arg == "--debug-dzpaths") { s.DebugDzPaths = true; }
            else if (arg == "--debug-ainets") { s.DebugAiNets ??= ""; }
            else if (arg.StartsWith("--debug-ainets=")) { s.DebugAiNets = arg["--debug-ainets=".Length..]; }
            else if (arg == "--debug-targets") { s.DebugTargets = true; }
            else if (arg.StartsWith("--debug-join=")) { s.DebugJoin = int.Parse(arg["--debug-join=".Length..]); }
            else if (arg.StartsWith("--debug-waves=")) { s.DebugWaves = int.Parse(arg["--debug-waves=".Length..]); }
            else if (arg.StartsWith("--debug-wingmen=")) { s.DebugWingmen = int.Parse(arg["--debug-wingmen=".Length..]); }
            else if (arg.StartsWith("--debug-preset=")) { s.DebugPreset = int.Parse(arg["--debug-preset=".Length..]); }
            else if (arg.StartsWith("--debug-pointer=")) { s.DebugPointer = ParseDebugPointer(arg["--debug-pointer=".Length..]); }
            else if (arg.StartsWith("--paint=")) { s.PaintNames = arg["--paint=".Length..].Split(',', StringSplitOptions.TrimEntries); }
            else if (arg.StartsWith("--paint-color=")) { s.PaintColorOverride = ParsePaintColors(arg["--paint-color=".Length..]); }
            else if (arg.StartsWith("--paint-decal=")) { s.PaintDecalOverride = ParsePaintDecals(arg["--paint-decal=".Length..]); }
            else if (arg.StartsWith("--paint-seed=")) { s.PaintSeed = ulong.Parse(arg["--paint-seed=".Length..]); s.PaintSeedExplicit = true; }
            else if (arg.StartsWith("--rof=")) { s.Rof = arg["--rof=".Length..]; }
            else if (arg == "--debug-scoreboard") { s.DebugScoreboard = true; }
            else if (arg == "--debug-pause") { s.DebugPauseFrame = DefaultDebugPauseFrame; }
            else if (arg.StartsWith("--debug-pause=")) { s.DebugPauseFrame = int.Parse(arg["--debug-pause=".Length..]); }
            else if (arg.StartsWith("--debug-objective=")) { s.DebugObjective = int.Parse(arg["--debug-objective=".Length..]); }
            else if (arg.StartsWith("--debug-wash=")) { s.DebugWash = int.Parse(arg["--debug-wash=".Length..]); }
            else if (arg == "--debug-markers") { s.DebugMarkers = true; }
            else if (arg == "--debug-spectate") { s.DebugSpectate = true; }
            else if (arg == "--debug-load") { s.DebugLoad ??= ""; }
            else if (arg.StartsWith("--debug-load=")) { s.DebugLoad = arg["--debug-load=".Length..]; }
            else if (arg == "--debug-livery") { s.DebugLivery ??= 0; }
            else if (arg.StartsWith("--debug-livery=")) { s.DebugLivery = int.Parse(arg["--debug-livery=".Length..]); }
            else if (arg == "--debug-mesh") { s.DebugMesh ??= ""; }
            else if (arg.StartsWith("--debug-mesh=")) { s.DebugMesh = arg["--debug-mesh=".Length..]; }
            else if (arg == "--debug-names") { s.DebugNames ??= "meshes"; }
            else if (arg.StartsWith("--debug-names=")) { s.DebugNames = arg["--debug-names=".Length..]; }
            else if (arg == "--debug-fps") { s.DebugFps ??= "compact"; }
            else if (arg.StartsWith("--debug-fps=")) { s.DebugFps = arg["--debug-fps=".Length..]; }
            else if (arg == "--debug-select") { s.DebugSelect ??= ""; }
            else if (arg.StartsWith("--debug-select=")) { s.DebugSelect = arg["--debug-select=".Length..]; }
            else if (arg == "--debug-nodelab") { s.DebugNodeLab ??= ""; }
            else if (arg.StartsWith("--debug-nodelab=")) { s.DebugNodeLab = arg["--debug-nodelab=".Length..]; }
            else if (arg == "--collision") { s.ForceCollision = true; }
            else if (arg.StartsWith("--collision="))
            {
                s.ForceCollision = true;
                string want = arg["--collision=".Length..];
                s.ShowColliders = want == "show";
                if (!s.ShowColliders && want.Length > 0)
                {
                    notes.Add(new Note("world", $"--collision='{want}' is not a value it takes (only '=show', which opens the C overlay), building collision anyway"));
                }
            }
            else if (arg == "--debug-colliders") { s.ShowColliders = true; }
            else if (arg == "--debug-classoverlay") { s.ShowClassOverlay = true; }
            else if (arg == "--debug-tilegrid") { s.ShowTileGrid = true; }
            else if (arg == "--debug-clutterflag") { s.DebugClutterFlag = true; }
            else if (arg.StartsWith("--hide-alpha="))
            {
                foreach (var want in arg["--hide-alpha=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var one = want.ToLowerInvariant() switch
                    {
                        "blend-surfaces" => Mech3.SceneBuilder.TransparencyClass.BlendSurface,
                        "scissor-surfaces" => Mech3.SceneBuilder.TransparencyClass.ScissorSurface,
                        "blend-cards" => Mech3.SceneBuilder.TransparencyClass.BlendCard,
                        "scissor-cards" => Mech3.SceneBuilder.TransparencyClass.ScissorCard,
                        _ => Mech3.SceneBuilder.TransparencyClass.None,
                    };
                    if (one == Mech3.SceneBuilder.TransparencyClass.None)
                    {
                        notes.Add(new Note("world", $"--hide-alpha='{want}' is not a class it takes "
                            + "(blend-surfaces, scissor-surfaces, blend-cards, scissor-cards), ignoring it"));
                    }
                    s.HiddenAlpha |= one;
                }
            }
            else if (arg.StartsWith("--clutter-templates="))
            {
                var names = arg["--clutter-templates=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Length == 0)
                {
                    notes.Add(new Note("world", "--clutter-templates= names no templates, keeping the chapter's own list"));
                }
                else
                {
                    s.ClutterTemplates = names;
                }
            }
            else if (arg.StartsWith("--map-edge-block="))
            {
                string want = arg["--map-edge-block=".Length..];
                if (int.TryParse(want, out int block) && block >= 1)
                {
                    s.MapEdgeBlock = block;
                }
                else
                {
                    notes.Add(new Note("world", $"--map-edge-block='{want}' is not a cell count >= 1, keeping the chapter's measured default"));
                }
            }
            else if (arg.StartsWith("--map-edge-mode="))
            {
                string want = arg["--map-edge-mode=".Length..];
                s.MapEdgeRepeat = want != "mirror";
                if (s.MapEdgeRepeat && want != "repeat")
                {
                    notes.Add(new Note("world", $"--map-edge-mode='{want}' is not a mode it takes (mirror|repeat), keeping 'repeat', the behaviour the original was A/B'd against"));
                }
            }
            else if (arg == "--debug-damage") { s.DebugDamage ??= ""; }
            else if (arg.StartsWith("--debug-damage=")) { s.DebugDamage = arg["--debug-damage=".Length..]; }
            else if (arg == "--markers") { s.MarkersOverlay = true; s.HasContentArg = true; }
            else if (arg == "--dump-markers") { s.DumpMarkers = true; }
            else if (arg.StartsWith("--dump-markers=")) { s.DumpMarkers = true; s.DumpMarkersPlane = arg["--dump-markers=".Length..]; }
            else if (arg == "--dump-weapons") { s.DumpWeapons = true; }
            else if (arg.StartsWith("--dump-weapons=")) { s.DumpWeapons = true; s.DumpWeaponsFilter = arg["--dump-weapons=".Length..]; }
            else if (arg == "--dump-loadout") { s.DumpLoadout = true; }
            else if (arg.StartsWith("--dump-loadout=")) { s.DumpLoadout = true; s.DumpLoadoutFilter = arg["--dump-loadout=".Length..]; }
            else if (arg == "--dump-flight") { s.DumpFlight = true; }
            else if (arg.StartsWith("--dump-flight=")) { s.DumpFlight = true; s.DumpFlightPlane = arg["--dump-flight=".Length..]; }
            else if (arg == "--dump-config") { s.DumpConfig = true; }
            else if (arg == "--damage-test") { s.DamageTest = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--damage-test=")) { s.DamageTest = true; s.DamageTestFilter = arg["--damage-test=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--damage-hd=")) { s.DamageHd = Flt(arg["--damage-hd=".Length..]); }
            else if (arg == "--effects-test") { s.EffectsTest = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--destroy=")) { s.DestroyName = arg["--destroy=".Length..]; }
            else if (arg == "--crash") { s.CrashFrame = DefaultCrashFrame; }
            else if (arg.StartsWith("--crash=")) { s.CrashFrame = int.Parse(arg["--crash=".Length..]); }
            else if (arg.StartsWith("--loadout=")) { s.LoadoutOverride = arg["--loadout=".Length..]; }
            else if (arg == "--infinite-ammo")
            {
                s.InfiniteAmmo = true;
                if (s.AmmoCap != null)
                {
                    notes.Add(new Note("weapons", "--infinite-ammo overrides the earlier --ammo=; using infinite ammo"));
                    s.AmmoCap = null;
                }
            }
            else if (arg.StartsWith("--ammo="))
            {
                s.AmmoCap = int.Parse(arg["--ammo=".Length..]);
                if (s.InfiniteAmmo)
                {
                    notes.Add(new Note("weapons", "--ammo= overrides the earlier --infinite-ammo; using the capped load"));
                    s.InfiniteAmmo = false;
                }
            }
            else if (arg == "--incoming") { s.IncomingPass = IncomingFire.DefaultPass; }
            else if (arg.StartsWith("--incoming="))
            {
                var parts = arg["--incoming=".Length..].Split(',');
                s.IncomingPass = parts[0].Length > 0 ? Flt(parts[0]) : IncomingFire.DefaultPass;
                if (parts.Length > 1 && parts[1].Length > 0)
                    s.IncomingWeapon = parts[1];
            }
            else if (arg.StartsWith("--ai="))
            {
                var entries = new List<AiPlaneEntry>();
                foreach (var token in arg["--ai=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var segments = token.Split(':');
                    string plane = segments[0];
                    string? net = null;
                    int? accent = null;
                    string? def = null;
                    int? team = null;
                    int count = 1;
                    Vector3? pos = null;
                    for (int si = 1; si < segments.Length; si++)
                    {
                        if (segments[si].StartsWith("accent="))
                            accent = int.Parse(segments[si]["accent=".Length..]);
                        else if (segments[si].StartsWith("def="))
                            def = segments[si]["def=".Length..];
                        else if (segments[si].StartsWith("team="))
                            team = int.Parse(segments[si]["team=".Length..]);
                        else if (segments[si].StartsWith("n="))
                            count = Math.Max(1, int.Parse(segments[si]["n=".Length..]));
                        else if (segments[si].StartsWith("pos="))
                            pos = ParseVec3Slashed(segments[si]["pos=".Length..]);
                        else if (segments[si].Length > 0 && net == null)
                            net = segments[si];
                    }
                    if (plane.Length > 0)
                        entries.Add(new AiPlaneEntry(plane, net, accent, def, team, count, pos));
                }
                if (entries.Count > 0)
                    s.AiPlanes = entries;
            }
            else if (arg.StartsWith("--ai-damage=")) { s.AiHullDamage = Math.Clamp(Flt(arg["--ai-damage=".Length..]), 0f, 1f); }
            else if (arg == "--canopy-holes") { s.CanopyHoles = 1; }
            else if (arg.StartsWith("--canopy-holes="))
            {
                s.CanopyHoles = Math.Clamp(int.Parse(arg["--canopy-holes=".Length..]),
                    1, Flight.Hud.CanopyHoleCue.HoleCount);
            }
            // An unknown word keeps the default rather than picking a policy, the same rule
            // --difficulty= follows. A misspelling must not quietly change what the AI fights.
            else if (arg.StartsWith("--ai-targeting="))
            {
                string mode = arg["--ai-targeting=".Length..];
                if (mode is "decoded" or "aircraft-first")
                    s.AircraftFirstTargeting = mode == "aircraft-first";
                else
                    s.Print($"--ai-targeting='{mode}' is not decoded or aircraft-first; ignoring it");
            }
            else if (arg == "--ai-attack") { s.AiAttackSkill = 5; }
            else if (arg.StartsWith("--ai-attack="))
            {
                s.AiAttackSkill = Math.Clamp(int.Parse(arg["--ai-attack=".Length..]), 1, 9);
                s.AiAttackSkillExplicit = true;
            }
            // An unparseable tier keeps the default rather than picking one: silently flying
            // Hardest because a name was misspelled is a balance change nobody asked for.
            else if (arg.StartsWith("--difficulty="))
            {
                if (Flight.Hangar.Difficulty.Parse(arg["--difficulty=".Length..]) is { } tier)
                {
                    s.Difficulty = tier;
                    s.DifficultyExplicit = true;
                }
            }
            else if (arg == "--no-assist") { s.NoAssist = true; }
            else if (arg == "--craters") { s.Craters = true; }
            else if (arg == "--generators") { s.Generators = true; }
            else if (arg.StartsWith("--generators=")) { s.Generators = true; s.GeneratorsPlane = arg["--generators=".Length..]; }
            else if (arg == "--zeppelins") { s.Zeppelins = true; }
            else if (arg.StartsWith("--zep="))
            {
                var zepSegments = arg["--zep=".Length..].Split(':');
                var zepWhere = zepSegments[0].Split('/');
                if (zepWhere.Length != 2 || zepWhere[0].Length == 0 || zepWhere[1].Length == 0
                    || zepSegments.Length < 2 || zepSegments[1].Length == 0)
                {
                    s.Print($"--zep='{arg["--zep=".Length..]}' is not <chapter>/<mission>:<record>; ignoring it");
                }
                else
                {
                    int? zepTeam = null;
                    Vector3? zepPos = null;
                    string? zepNet = null;
                    for (int si = 2; si < zepSegments.Length; si++)
                    {
                        if (zepSegments[si].StartsWith("team="))
                            zepTeam = int.Parse(zepSegments[si]["team=".Length..]);
                        else if (zepSegments[si].StartsWith("pos="))
                            zepPos = ParseVec3Slashed(zepSegments[si]["pos=".Length..]);
                        else if (zepSegments[si].StartsWith("net="))
                            zepNet = zepSegments[si]["net=".Length..];
                    }
                    s.Zep = new ZepStageSpec(zepWhere[0], zepWhere[1], zepSegments[1], zepTeam, zepPos, zepNet);
                    s.HasContentArg = true;
                }
            }
            else if (arg == "--wake-turrets") { s.WakeTurrets = true; }
            else if (arg == "--wake-generators") { s.WakeGenerators = true; }
            else if (arg == "--fire") { s.AutoFire = true; }
            else if (arg == "--fire-rockets") { s.AutoFireRockets = true; }
            else if (arg.StartsWith("--gun-select=")) { s.GunSelect = int.Parse(arg["--gun-select=".Length..]); }
            else if (arg.StartsWith("--target=")) { s.TargetSelect = arg["--target=".Length..].Trim(); }
            else if (arg.StartsWith("--rocket=")) { s.RocketOverride = arg["--rocket=".Length..]; }
            else if (arg == "--hud-font-test") { s.HudFontTest = true; }
            else if (arg.StartsWith("--hud-font-test=")) { s.HudFontTest = true; s.HudFontTestText = arg["--hud-font-test=".Length..]; }
            else if (arg == "--weapon-lab") { s.WeaponLab = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-lab=")) { s.WeaponLab = true; s.WeaponSelect = arg["--weapon-lab=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-mount=")) { s.WeaponMount = arg["--weapon-mount=".Length..]; s.HasContentArg = true; }
            else if (arg == "--weapon-fire") { s.WeaponFire = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-target=")) { s.WeaponTarget = ParseVec3(arg["--weapon-target=".Length..]); s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-surface=")) { s.WeaponSurface = arg["--weapon-surface=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-standoff=")) { s.WeaponStandoff = Flt(arg["--weapon-standoff=".Length..]); s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-camera="))
            {
                // free | orbit (the default) | a frame count that toggles between the two.
                string want = arg["--weapon-camera=".Length..];
                s.HasContentArg = true;
                if (string.Equals(want, "free", StringComparison.OrdinalIgnoreCase))
                {
                    s.WeaponFreeCamera = true;
                }
                else if (int.TryParse(want, NumberStyles.Integer, CultureInfo.InvariantCulture, out int every) && every > 0)
                {
                    s.WeaponCameraToggle = every;
                }
                else if (!string.Equals(want, "orbit", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(new Note("ui", $"--weapon-camera={want} is not free/orbit/<frames>, keeping the orbit camera"));
                }
            }
            else if (arg == "--weapon-click") { s.WeaponClick = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-click="))
            {
                s.WeaponClick = true;
                s.HasContentArg = true;
                string want = arg["--weapon-click=".Length..];
                var parts = want.Split(',', StringSplitOptions.TrimEntries);
                // A trailing "aim" is the scripted shift-click: aim without re-parking.
                if (parts.Length == 3 && string.Equals(parts[2], "aim", StringComparison.OrdinalIgnoreCase))
                {
                    s.WeaponClickAimOnly = true;
                    parts = new[] { parts[0], parts[1] };
                }
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float cx)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float cy))
                {
                    s.WeaponClickAt = new Vector2(cx, cy);
                }
                else if (!string.Equals(want, "aim", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(new Note("ui", $"--weapon-click={want} is not x,y[,aim], clicking the viewport centre instead"));
                }
                else
                {
                    s.WeaponClickAimOnly = true;
                }
            }
            else if (arg == "--weapon-cycle") { s.WeaponCycle = 30; s.HasContentArg = true; }
            else if (arg.StartsWith("--weapon-cycle=")) { s.WeaponCycle = Math.Max(1, int.Parse(arg["--weapon-cycle=".Length..])); s.HasContentArg = true; }
            else if (arg == "--weapon-test") { s.WeaponTest = true; s.HasContentArg = true; }
            else if (arg == "--run-tests") { s.RunTests = true; }
            else if (arg.StartsWith("--run-tests=")) { s.RunTests = true; s.RunTestsFilter = arg["--run-tests=".Length..]; }
            else if (arg.StartsWith("--mission=")) { s.Mission = arg["--mission=".Length..]; }
            else if (arg.StartsWith("--scenario=")) { s.Scenario = arg["--scenario=".Length..]; s.ScenarioExplicit = true; }
            else if (arg.StartsWith("--ia=")) { s.IaPath = arg["--ia=".Length..]; }
            else if (arg.StartsWith("--campaign="))
            {
                string val = arg["--campaign=".Length..];
                int lastColon = val.LastIndexOf(':');
                if (lastColon > 0
                    && int.TryParse(val[(lastColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq))
                {
                    s.CampaignProfile = val[..lastColon];
                    s.CampaignMissionSeq = seq;
                }
                else
                {
                    s.CampaignProfile = val;
                    notes.Add(new Note("core", $"--campaign={val} has no ':<seq>' mission index, profile only, no mission chosen"));
                }
                s.HasContentArg = true;
            }
            else if (arg.StartsWith("--spawn=")) { s.SpawnIndex = int.Parse(arg["--spawn=".Length..]); }
            else if (arg.StartsWith("--spawn-at=")) { s.SpawnAt = ParseVec3(arg["--spawn-at=".Length..]); Deprecate("--spawn-at", "--pos"); }
            else if (arg.StartsWith("--spawn-dir=")) { s.SpawnDir = ParseVec3(arg["--spawn-dir=".Length..]); Deprecate("--spawn-dir", "--direction"); }
            else if (arg.StartsWith("--sky-zone=")) { s.SkyZone = arg["--sky-zone=".Length..]; s.SkyZoneExplicit = true; }
            else if (arg.StartsWith("--data-root=")) { s.DataRoot = arg["--data-root=".Length..]; }
            else if (arg == "--extract") { s.ExtractInstall = ""; }
            else if (arg.StartsWith("--extract=")) { s.ExtractInstall = arg["--extract=".Length..]; }
            else if (arg == "--extract-force") { s.ExtractForce = true; }
            else if (arg == "--extract-unzip") { s.ExtractUnzip = true; }
            else if (arg.StartsWith("--unzbd=")) { s.UnzbdPath = arg["--unzbd=".Length..]; }
            else if (arg.StartsWith("--gamez=")) { s.Gamez = arg["--gamez=".Length..]; }
            else if (arg.StartsWith("--textures=")) { s.Textures = arg["--textures=".Length..]; }
            else if (arg.StartsWith("--zrdr=")) { s.Zrdr = arg["--zrdr=".Length..]; }
            else if (arg.StartsWith("--interp=")) { s.Interp = arg["--interp=".Length..]; }
            else if (arg.StartsWith("--sounds=")) { s.Sounds = arg["--sounds=".Length..]; }
            else if (arg.StartsWith("--messages=")) { s.Messages = arg["--messages=".Length..]; }
            else if (arg == "--no-fog") { s.NoFog = true; }
            else if (arg == "--no-cockpit-pass") { s.CockpitPass = false; }
            else if (arg == "--no-clutter") { s.NoClutter = true; }
            else if (arg == "--no-zone-cull") { s.NoZoneCull = true; }
            else if (arg == "--no-flare") { s.NoFlare = true; }
            else if (arg == "--no-ssao") { s.SkippedPasses |= EnhancedPasses.Ssao; }
            else if (arg == "--no-ssr") { s.SkippedPasses |= EnhancedPasses.Ssr; }
            else if (arg == "--no-glow") { s.SkippedPasses |= EnhancedPasses.Glow; }
            else if (arg == "--no-soft-shadows") { s.SkippedPasses |= EnhancedPasses.SoftShadows; }
            else if (arg.StartsWith("--mips=")) { s.SetMips(arg["--mips=".Length..]); }
            else if (arg.StartsWith("--graphics="))
            {
                string want = arg["--graphics=".Length..];
                if (Utils.GraphicsMode.TryParse(want, out _))
                {
                    s.GraphicsMode = want;
                }
                else
                {
                    notes.Add(new Note("world", $"--graphics={want} is not original/enhanced, keeping the config key's value"));
                }
            }
            else if (arg == "--dump-mips") { s.DumpMips = true; }
            else if (arg.StartsWith("--dump-mips=")) { s.DumpMips = true; s.DumpMipsFilter = arg["--dump-mips=".Length..]; }
            else if (arg == "--dump-ai") { s.DumpAi = true; }
            else if (arg.StartsWith("--dump-ai=")) { s.DumpAi = true; s.DumpAiChapter = arg["--dump-ai=".Length..]; }
            else if (arg == "--dump-tilegrid") { s.DumpTileGrid = true; s.HasContentArg = true; }
            else if (arg.StartsWith("--dump-tilegrid=")) { s.DumpTileGrid = true; s.DumpTileGridPath = arg["--dump-tilegrid=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--tex-override=")) { texOverrides.Add(arg["--tex-override=".Length..]); }
            else if (arg == "--tex-census") { s.TexCensus = true; }
            else if (arg.StartsWith("--tex-census=")) { s.TexCensus = true; s.TexCensusFilter = arg["--tex-census=".Length..]; }
            else if (arg == "--no-focus") { s.NoFocus = true; }
            else if (arg == "--no-vsync") { s.NoVsync = true; }
            else if (arg == "--zip-assets") { s.ZipAssets = true; }
            else if (arg.StartsWith("--hitch-inject="))
            {
                (s.HitchInjectMs, s.HitchInjectAlloc, s.HitchInjectFrame) =
                    ParseHitchInject(arg["--hitch-inject=".Length..]);
            }
            else if (arg == "--mute") { s.Mute = true; }
            else if (arg.StartsWith("--volume="))
            {
                // TryParse rather than Flt: a typo in a volume must not take the launch down with a
                // FormatException, and the run is still perfectly usable at the default gain.
                string want = arg["--volume=".Length..];
                if (!float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out float volume))
                {
                    notes.Add(new Note("core", $"--volume={want} is not a number (0-1), leaving the volume alone"));
                }
                else if (volume is < 0f or > 1f)
                {
                    s.Volume = Math.Clamp(volume, 0f, 1f);
                    notes.Add(new Note("core", $"--volume={want} is outside 0-1, using {s.Volume}"));
                }
                else
                {
                    s.Volume = volume;
                }
            }
            else if (arg.StartsWith("--sound-range-scale="))
            {
                string want = arg["--sound-range-scale=".Length..];
                if (float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale)
                    && float.IsFinite(scale) && scale > 0f)
                {
                    s.SoundRangeScale = scale;
                }
                else
                {
                    notes.Add(new Note("core", $"--sound-range-scale={want} is not a positive number, leaving the shipped factor in place"));
                }
            }
            else if (arg.StartsWith("--cloud-jitter="))
            {
                string want = arg["--cloud-jitter=".Length..];
                if (float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out float metres)
                    && float.IsFinite(metres) && metres >= 0f)
                {
                    s.CloudJitter = metres;
                }
                else
                {
                    notes.Add(new Note("core", $"--cloud-jitter={want} is not a non-negative number of metres, leaving the shipped offset in place"));
                }
            }
            else if (arg == "--debug-collision") { s.DebugCollision = true; }
            else if (arg.StartsWith("--players=")) { s.Players = int.Parse(arg["--players=".Length..]); s.PlayersExplicit = true; }
            else if (arg.StartsWith("--hold=")) { s.HoldSets = ParseHold(arg["--hold=".Length..]); }
            else if (arg.StartsWith("--lever=")) { s.LeverSteps = ParseLever(arg["--lever=".Length..]); }
            else if (arg.StartsWith("--frames=")) { s.ScreenshotFrames = int.Parse(arg["--frames=".Length..]); }
            else if (arg.StartsWith("--shots=")) { s.ScreenshotShots = Math.Max(1, int.Parse(arg["--shots=".Length..])); }
            else if (arg.StartsWith("--jitter=")) { s.JitterDeg = Flt(arg["--jitter=".Length..]); }
            else if (arg.StartsWith("--screenshot=")) { s.ScreenshotPath = arg["--screenshot=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--export-gltf=")) { s.ExportGltfPath = arg["--export-gltf=".Length..]; s.HasContentArg = true; }
            else if (arg.StartsWith("--yaw=")) { s.Yaw = Flt(arg["--yaw=".Length..]); }
            else if (arg.StartsWith("--pitch=")) { s.Pitch = Flt(arg["--pitch=".Length..]); }
            else if (arg.StartsWith("--pos=")) { s.Pos = ParseVec3(arg["--pos=".Length..]); }
            else if (arg.StartsWith("--direction=")) { s.Direction = ParseVec3(arg["--direction=".Length..]); }
            else if (arg.StartsWith("--campos=")) { s.CamPos = ParseVec3(arg["--campos=".Length..]); Deprecate("--campos", "--pos"); }
            else if (arg.StartsWith("--lookat=")) { s.LookAt = ParseVec3(arg["--lookat=".Length..]); }
            else if (arg.StartsWith("--view="))
            {
                string want = arg["--view=".Length..];
                // The selected modes are checked first: they are names, so they cannot collide
                // with a digit or with 'back', and a numpad digit means the other concept.
                if (Flight.Camera.PilotView.Parse(want) is { } mode)
                {
                    s.ViewMode = mode;
                    s.ViewModeExplicit = true;
                }
                else
                {
                    s.View = ParseView(want);
                    if (s.View == 0)
                    {
                        notes.Add(new Note("core", $"--view={want} is not a numpad view (1-4, 6-9), 'back', 'flyby', or a view mode (chase/cockpit/nose), using the chase camera"));
                    }
                }
            }
            else if (arg.StartsWith("--look="))
            {
                string want = arg["--look=".Length..];
                if (ParseLook(want) is { } look)
                {
                    s.PinnedLook = look;
                }
                else
                {
                    notes.Add(new Note("core", $"--look={want} is not an x,y pair, leaving the look stick centred"));
                }
            }
        }

        // The development options mean nothing to a session, so a stray one is named rather than
        // silently dropped.
        if (s.ExtractInstall == null)
        {
            foreach (var (given, name) in new[] { (s.ExtractForce, "--extract-force"), (s.ExtractUnzip, "--extract-unzip"), (s.UnzbdPath != null, "--unzbd=") })
            {
                if (given)
                {
                    notes.Add(new Note("core", $"{name} does nothing without --extract=<install>, ignoring it"));
                }
            }
        }

        s._notes = notes;
        s.Deprecated = deprecated;
        s.LogSpecs = logSpecs;
        s.TexOverrides = texOverrides;
        s.Resolve();
        return s;
    }

    /// <summary>The spec for a launchscreen launch, one plane per player, derived from the pristine
    /// command line <paramref name="cli"/>, never the last session's spec. ⚠ Does not re-resolve:
    /// every menu-settable field must be written here, or the pristine base drops it. The 2-player
    /// Dogfight lock is <see cref="UI.Screens.LaunchMenu"/>'s job. An <paramref name="iaDef"/> decides
    /// <see cref="Scenario"/> and <see cref="Stunt"/> instead. The two vs arguments are a screen's
    /// match rules, null where none offers them (<see cref="VsKillsExplicit"/>).</summary>
    public static SessionSpec FromMenu(SessionSpec cli, string chapter, IReadOnlyList<string> planeNodes,
        MenuMode mode, InstantActionDef? iaDef = null, IReadOnlyList<LoadoutChoice?>? loadouts = null,
        IReadOnlyList<CustomPlaneDef?>? customPlanes = null, int? vsKills = null, int? vsTimeMinutes = null,
        LoadoutChoice? iaWingmanLoadout = null)
    {
        var names = planeNodes.ToArray();
        return cli with
        {
            MenuLoadouts = loadouts ?? Array.Empty<LoadoutChoice?>(),
            MenuCustomPlanes = customPlanes ?? Array.Empty<CustomPlaneDef?>(),
            Chapter = chapter,
            PlaneNames = names,
            // An empty pick cannot come from the launchscreen (it launches only when every joined
            // slot is locked, and it needs at least one), so this falls back to the command line's
            // plane rather than to whatever the last session flew.
            PlaneName = names.Length > 0 ? names[0] : cli.PlaneName,
            Players = Mathf.Clamp(names.Length, 1, UI.Boards.SplitScreen.MaxPlayers),
            Stunt = iaDef != null ? iaDef.MissionType == "stunt_flying" : mode == MenuMode.Stunt,
            Versus = mode == MenuMode.Versus,
            VsKills = cli.VsKillsExplicit ? cli.VsKills : vsKills ?? cli.VsKills,
            VsTimeMinutes = cli.VsTimeExplicit ? cli.VsTimeMinutes : vsTimeMinutes ?? cli.VsTimeMinutes,
            Mode = SessionMode.Fly,
            WorldMode = true,
            Scenario = cli.ScenarioExplicit ? cli.Scenario : iaDef?.MissionType ?? mode switch
            {
                MenuMode.Stunt => "stunt_flying",
                MenuMode.Versus => "dogfight_ace",
                _ => "zeppelin_run",
            },
            IaDef = iaDef,
            IaWingmanLoadout = iaDef != null ? iaWingmanLoadout : null,
        };
    }

    /// <summary>The campaign cabin's FLY MISSION, <see cref="FromMenu"/>'s counterpart for a story
    /// mission: the profile and story position <c>--campaign=</c> would name, and
    /// <paramref name="planeNodes"/>, one entry per joined human in player order (entry 0 the
    /// SEATED pilot's aircraft, entries 1 and up guests'). The chapter and mission are settled by
    /// <see cref="Session.Campaign.CampaignDirector.ResolveSpec"/> in the constructor, out of
    /// <c>cm_sequence.zrd</c>, as for a command line. ⚠ Derived from the pristine <paramref name="cli"/>.</summary>
    public static SessionSpec FromCampaign(SessionSpec cli, string profile, int seq,
        IReadOnlyList<string> planeNodes, int players,
        IReadOnlyList<LoadoutChoice?>? fits = null, IReadOnlyList<CustomPlaneDef?>? customs = null) =>
        cli with
        {
            CampaignProfile = profile,
            CampaignMissionSeq = seq,
            MenuLoadouts = fits ?? Array.Empty<LoadoutChoice?>(),
            MenuCustomPlanes = customs ?? Array.Empty<CustomPlaneDef?>(),
            PlaneNames = planeNodes.ToArray(),
            PlaneName = planeNodes.Count > 0 ? planeNodes[0] : cli.PlaneName,
            Players = Mathf.Clamp(players, 1, UI.Boards.SplitScreen.MaxPlayers),
            Coop = true,
            Stunt = false,
            Versus = false,
            IaDef = null,
            IaWingmanLoadout = null,
            Mode = SessionMode.Fly,
            WorldMode = true,
            ChapterGiven = true,
        };

    /// <summary>The saved difficulty option folded in, the rule the saved graphics mode follows:
    /// the <c>--difficulty=</c> flag beats the saved word, and a <c>--det</c> run reads no saved
    /// option at all, since options.json is one machine's state and a deterministic run must not
    /// depend on it. A word <see cref="CSVM.Flight.Hangar.Difficulty.Parse"/> refuses, or null, changes
    /// nothing. Applied per launch rather than once per process, so a tier saved on an Options
    /// screen reaches the next flight without a restart.</summary>
    public SessionSpec WithSavedDifficulty(string? savedWord)
    {
        if (DifficultyExplicit || Det || CSVM.Flight.Hangar.Difficulty.Parse(savedWord) is not { } saved)
        {
            return this;
        }

        return this with { Difficulty = saved };
    }

    /// <summary>The saved targeting setting folded in, on the difficulty's own rules less the
    /// flag. Nothing on the command line names it, and a never-set field leaves the decoded head
    /// rule standing. A <c>--det</c> run reads no saved option, since a golden shot must not
    /// depend on one machine's options file. Applied per launch, so an Options apply reaches the
    /// next flight without a restart.</summary>
    public SessionSpec WithSavedNearestAfterKill(bool? saved) =>
        Det || saved is not { } on ? this : this with { NearestAfterKill = on };

    /// <summary>The saved opening view folded in, on the difficulty's own rules with <c>--view=</c>
    /// in the flag's place. A command line naming a mode beats the saved word. A word
    /// <see cref="CSVM.Flight.Camera.PilotView.Parse"/> refuses, or null, changes nothing, and a
    /// <c>--det</c> run reads no saved option. Not folded outside <see cref="Fly"/>, since the two
    /// first-person views sit on a flown aircraft's camera and the other modes have cameras of
    /// their own. It runs after <c>Validate</c>, so it keeps that rule rather than warning.</summary>
    public SessionSpec WithSavedDefaultView(string? savedWord)
    {
        if (ViewModeExplicit || Det || !Fly
            || CSVM.Flight.Camera.PilotView.Parse(savedWord ?? string.Empty) is not { } saved)
        {
            return this;
        }

        return this with { ViewMode = saved };
    }

    /// <summary>The saved automatic head turn folded in, on
    /// <see cref="WithSavedNearestAfterKill"/>'s rules. Nothing on the command line names it, and
    /// a never-set field leaves the <c>headLook.autohead</c> config key deciding. A <c>--det</c>
    /// run reads no saved option.</summary>
    public SessionSpec WithSavedAutoHeadTurn(bool? saved) =>
        Det || saved is not { } on ? this : this with { AutoHeadTurn = on };

    /// <summary>Parse <c>--plane=</c>: one node name, or a comma-separated list, one plane per
    /// player for splitscreen (the launchscreen's simultaneous pick produces the same list).</summary>
    public static IReadOnlyList<string> ParsePlanes(string value)
    {
        var names = new List<string>();
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            names.Add(name.Trim());
        }
        return names;
    }

    /// <summary>Parse an "x,y,z" triple, invariant culture.</summary>
    public static Vector3 ParseVec3(string s)
    {
        var parts = s.Split(',');
        return new Vector3(Flt(parts[0]), Flt(parts[1]), Flt(parts[2]));
    }

    /// <summary>Parse an "x/y/z" triple, invariant culture. Slash-separated because <c>--ai=</c>
    /// has already spent the comma separating one entry from the next, so a <c>pos=</c> token
    /// inside an entry cannot use one; <c>--zep=</c> spells it the same way so that one form
    /// serves both flags.</summary>
    public static Vector3 ParseVec3Slashed(string s)
    {
        var parts = s.Split('/');
        return new Vector3(Flt(parts[0]), Flt(parts[1]), Flt(parts[2]));
    }

    /// <summary>Parse <c>--look=x,y</c> into a right-stick deflection, clamped to [−1, 1] on both
    /// axes. Null on anything that is not two invariant-culture numbers, which the caller reports
    /// as a note rather than throwing: a typo in a capture script should not kill the run.</summary>
    public static Vector2? ParseLook(string s)
    {
        var parts = s.Split(',');
        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
        {
            return null;
        }
        return new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
    }

    /// <summary>The numpad view digit for <c>--view=</c>, the look-behind
    /// (<c>--view=back</c>) or the flyby (<c>--view=flyby</c>), the last two as sentinels above the
    /// digit range. 5 has no perspective of its own (the middle of the pad is the chase camera),
    /// and anything else is a typo, both give 0, the chase camera, which the caller reports.</summary>
    public static int ParseView(string s)
    {
        if (string.Equals(s, "back", StringComparison.OrdinalIgnoreCase))
        {
            return Flight.Camera.CameraController.PinnedBackView;
        }
        if (string.Equals(s, "flyby", StringComparison.OrdinalIgnoreCase))
        {
            return Flight.Camera.CameraController.PinnedFlybyView;
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n >= 1 && n <= 9 && n != 5)
        {
            return n;
        }
        return 0;
    }

    /// <summary>Parse the scripted hold argument: '|' separates one sequence per player (the last
    /// one covers any remaining players, so a single-sequence argument still drives everyone),
    /// ';' separates that sequence's segments, each "pitch,roll,yaw,throttle" with an optional
    /// "@seconds" duration. The last segment (or one without a duration) holds forever.</summary>
    public static (FlightInput, float)[][] ParseHold(string s)
    {
        var players = new List<(FlightInput, float)[]>();
        foreach (var perPlayer in s.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var segments = new List<(FlightInput, float)>();
            foreach (var seg in perPlayer.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = seg.Split('@');
                var p = at[0].Split(',');
                segments.Add((new FlightInput { Pitch = Flt(p[0]), Roll = Flt(p[1]), Yaw = Flt(p[2]), Throttle = Flt(p[3]) },
                              at.Length > 1 ? Flt(at[1]) : 0f));
            }
            players.Add(segments.ToArray());
        }
        return players.ToArray();
    }

    /// <summary>Parse the lever schedule: ';'-separated "eighths@seconds" steps, ascending in time,
    /// where eighths is 0 to 8 as the digit row reads it. A step is one press of that digit at that
    /// sim-second, so the commanded lever jumps and stays. The live lever then slews to it at its
    /// own rate, and that gap is what the exhaust smoke charges from.</summary>
    public static (float At, float Lever)[] ParseLever(string s)
    {
        var steps = new List<(float, float)>();
        foreach (var step in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = step.Split('@');
            steps.Add((at.Length > 1 ? Flt(at[1]) : 0f, Math.Clamp(Flt(at[0]), 0f, 8f) / 8f));
        }
        steps.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return steps.ToArray();
    }

    /// <summary>Parse <c>--paint-color=</c>: up to three '/'-separated byte triples (body / dark
    /// trim / light trim); a missing slot repeats the last one given.</summary>
    public static Color[] ParsePaintColors(string spec)
    {
        var parts = spec.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var outc = new Color[3];
        for (int i = 0; i < 3; i++)
        {
            var v = ParseVec3(parts[Math.Min(i, parts.Length - 1)]);
            outc[i] = PaintScheme.FromBytes((int)v.X, (int)v.Y, (int)v.Z);
        }
        return outc;
    }

    /// <summary>Parse <c>--paint-decal=</c>: up to three comma-separated indices (nose / tail /
    /// wing); a missing slot repeats the last one given.</summary>
    /// <summary>Parse <c>--debug-pointer=</c>: "x,y" in authored 800x600 pixels, then any of the
    /// words "down" and "right" for a button held. A spec without two numbers stands no pointer at
    /// all, since a shot of a half-read one would read as an answer about where the pointer
    /// goes.</summary>
    public static (float X, float Y, bool Down, bool Right)? ParseDebugPointer(string spec)
    {
        var parts = (spec ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
        {
            return null;
        }

        bool down = false;
        bool right = false;
        for (int i = 2; i < parts.Length; i++)
        {
            down |= parts[i].Equals("down", StringComparison.OrdinalIgnoreCase);
            right |= parts[i].Equals("right", StringComparison.OrdinalIgnoreCase);
        }

        return (x, y, down, right);
    }

    public static int[] ParsePaintDecals(string spec)
    {
        var parts = spec.Split(',', StringSplitOptions.TrimEntries);
        var outd = new int[3];
        for (int i = 0; i < 3; i++)
        {
            outd[i] = int.Parse(parts[Math.Min(i, parts.Length - 1)]);
        }
        return outd;
    }

    /// <summary>Parse <c>--damage=</c> presets: "nose:0.25,leftwing:40", part:fraction pairs,
    /// values &gt; 1 read as percent. Malformed pairs are skipped, and appended to
    /// <paramref name="rejected"/> when one is supplied, so the caller can report them.</summary>
    public static List<(string Part, float Fraction)> ParseDamagePreset(string s, List<string>? rejected = null)
    {
        var list = new List<(string, float)>();
        foreach (var item in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = item.Split(':');
            if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                list.Add((kv[0].Trim(), Mathf.Clamp(v > 1f ? v / 100f : v, 0f, 1f)));
            }
            else
            {
                rejected?.Add(item);
            }
        }
        return list;
    }

    /// <summary>Parse <c>--hitch-inject=</c>: <c>[alloc:]&lt;ms&gt;[@frame]</c>. The <c>alloc:</c>
    /// prefix is the allocation-burst form (moves the GC/allocated-bytes columns); its absence is
    /// the busy-wait form (proves only the timing path). <paramref name="defaultFrame"/> is what a
    /// bare <c>&lt;ms&gt;</c> with no <c>@frame</c> resolves to.</summary>
    public static (float Ms, bool Alloc, int Frame) ParseHitchInject(string s, int defaultFrame = DefaultHitchInjectFrame)
    {
        bool alloc = s.StartsWith("alloc:", StringComparison.Ordinal);
        if (alloc)
        {
            s = s["alloc:".Length..];
        }
        int at = s.IndexOf('@');
        int frame = defaultFrame;
        if (at >= 0)
        {
            frame = int.Parse(s[(at + 1)..], CultureInfo.InvariantCulture);
            s = s[..at];
        }
        return (Flt(s), alloc, frame);
    }

    /// <summary>A copy pointed at the chapter and mission a <c>--campaign=</c> story position
    /// resolves to. Resolving it needs <c>cm_sequence.zrd</c> off disk, which this type never
    /// touches, so <see cref="Session.Campaign.CampaignDirector.ResolveSpec"/> reads the sequence and calls
    /// this; the rest of the build then sees an ordinary chapter/mission session.</summary>
    public SessionSpec WithCampaignMission(string chapter, string mission) =>
        this with { Chapter = chapter, Mission = mission, ChapterGiven = true };

    /// <summary>A copy with <see cref="Zeppelins"/>/<see cref="Generators"/> turned on for a
    /// campaign mission that ships the data; <see cref="Session.Launch.GameSession"/> calls this once
    /// <see cref="WithCampaignMission"/> has settled the chapter/mission <see cref="FromCampaign"/>
    /// could not yet know. ORs rather than overwrites, so an explicit CLI flag survives.</summary>
    public SessionSpec WithCampaignZeppelins(bool hasZeppelins, bool hasGenerators) =>
        this with { Zeppelins = Zeppelins || hasZeppelins, Generators = Generators || hasGenerators };

    /// <summary>A copy flying <paramref name="planeNode"/> as the seated pilot's aircraft, with the
    /// hangar build and stored fit that go with it. The counterpart of <see cref="FromCampaign"/>'s
    /// seat for a command-line <c>--campaign=</c>, whose spec never passed a launchscreen: reading
    /// the profile needs the store off disk, which this type never touches, so
    /// <see cref="Session.Campaign.CampaignDirector.ResolveSeatedPlane"/> reads it and calls this. Guests
    /// keep falling back to entry 0 the way <see cref="FromCampaign"/> leaves them.</summary>
    public SessionSpec WithSeatedAircraft(string planeNode, CustomPlaneDef? custom, LoadoutChoice? fit) =>
        this with
        {
            PlaneNames = new[] { planeNode },
            PlaneName = planeNode,
            MenuCustomPlanes = new[] { custom },
            MenuLoadouts = new[] { fit },
        };

    private static float Flt(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    // Turns the parsed votes into the one answer each: the mode, its modifiers, the world
    // selection, the player count, the `--det` bundle and the placement routing. Runs once.
    // ⚠ The step ORDER below is the behaviour; do not reorder it while tidying. `--stunt` moves
    // Scenario BEFORE arbitration can clear Stunt; the freecam/anim-lab-only debug tools are
    // dropped AFTER `--node=` has forced the viewer. Each step below carries its own reason.
    private void Resolve()
    {
        // Every flag that votes for a mode, gathered before anything is arbitrated. The probes vote
        // like the rest: they need a world without an aircraft (or a parked plane to shoot at), and
        // asking for it through the mode is how they get one.
        bool fly = _flyArg || _stuntArg || _vsArg;
        bool stunt = _stuntArg;
        bool vs = _vsArg;
        bool damageLab = _damageLabArg;
        bool viewer = _viewerArg || MarkersOverlay || WeaponTest;
        // --dump-tilegrid votes freecam for one reason: the map-edge extender is only built in the
        // freecam/fly/sky-zone arm, and the census is a report ABOUT that extender. A dump that
        // resolved to the viewer would build a world with no continuation and report nothing.
        bool freecam = _freecamArg || DamageTest || EffectsTest || DumpTileGrid;
        bool animLab = _animLabArg || PlayAnim != null || DebugAnimUi;

        // --vs and --stunt are both flight modifiers, but not composable, one match mode has to
        // be chosen ahead of time rather than by argument order, so --vs beats --stunt by FIXED
        // precedence (unlike the rest of this file's last-flag-wins parsing).
        if (vs && stunt)
        {
            Warn("core", "--vs beats --stunt (fixed precedence, not last-wins); dropping stunt mode");
            stunt = false;
        }
        // --stunt is free flight over the mission's danger zones: the flight path plus the
        // stunt_flying spawn list, unless the tester pinned another scenario for a specific spawn.
        if (stunt && !ScenarioExplicit)
        {
            Scenario = "stunt_flying";
        }
        // --vs is free-for-all splitscreen deathmatch: the flight path plus the dogfight_ace
        // spawn list, unless the tester pinned another scenario for a specific spawn.
        if (vs && !ScenarioExplicit)
        {
            Scenario = "dogfight_ace";
        }
        // --anim-lab is the animation debugger's stage: the chapter world under the lab's own
        // clock, with no flight controller. The most specific mode of all, so it wins outright,
        // combining it with a flight/viewer/spectator mode is a contradiction.
        if (animLab && (fly || viewer || freecam))
        {
            Print("--anim-lab is the animation lab; ignoring --fly/--stunt/--vs/--viewer/--damage/--freecam");
            fly = stunt = vs = viewer = damageLab = freecam = false;
        }
        // --freecam is the spectator world view: not flight (no aircraft) and not the parked-plane
        // viewer. Asking for a plane-less world AND a plane is a contradiction either way.
        if (freecam && (fly || viewer))
        {
            Print("--freecam is a world view with no aircraft; ignoring --fly/--stunt/--vs/--viewer/--damage");
            fly = stunt = vs = viewer = damageLab = false;
        }
        // The damage lab has two hosts now, the parked plane and the flown one, so --damage
        // asks for a lab, not for a mode. It only picks the parked viewer when nothing else
        // claimed the session, which keeps a bare --damage= meaning what it always did.
        if (damageLab && !fly && !freecam && !animLab)
        {
            viewer = true;
        }
        // Flight is the default for any content arg; --viewer opts out into the static inspection
        // view. The explicit --viewer wins, since a bare --fly is now just the default spelled out.
        if (viewer && fly)
        {
            Print("--viewer and --fly/--stunt/--vs are opposites (flight is the default); using --viewer");
            fly = stunt = vs = false;
        }
        // --node= is a single-subtree INSPECTION stage: the static viewer unless the anim lab was
        // asked for. Neither flight nor the spectator view has anything to do with one object.
        if (NodeName != null && !animLab)
        {
            if (fly || stunt || freecam)
            {
                Print("--node= is a single-subtree inspection stage; ignoring --fly/--stunt/--vs/--freecam");
                fly = stunt = vs = freecam = false;
            }
            viewer = true;
            ChapterGiven = true; // the subtree comes out of the chapter's gamez
        }
        if (HasContentArg && !viewer && !freecam && !animLab)
        {
            fly = true;
        }
        Mode = animLab ? SessionMode.AnimLab
            : freecam ? SessionMode.Freecam
            : viewer ? SessionMode.Viewer
            : fly ? SessionMode.Fly
            : SessionMode.Menu;
        Stunt = stunt;
        Versus = vs;
        DamageLab = damageLab;
        // --vs's FFA is explicit and outranks --coop; the deathmatch is the point of the mode.
        if (Coop && Versus)
        {
            Warn("core", "--coop has no effect with --vs (its FFA is explicit); ignoring --coop");
            Coop = false;
        }

        // A campaign sortie is co-op by construction: the authored AI teams assume one player side.
        // Silent, not warned, --coop asks for exactly this. !Versus so the rule above still holds.
        if (CampaignProfile != null && !Versus)
        {
            Coop = true;
        }

        // The numpad views orbit a FLYING plane; the other modes have their own cameras (the
        // viewer's orbit, the spectator freecam) placed with --pos/--direction instead.
        if (View != 0 && !Fly)
        {
            Warn("core", $"--view={View} is a flight camera; ignoring it outside --fly/--stunt");
            View = 0;
        }
        // Same rule for the two first-person modes: they sit on a flown aircraft's camera.
        if (ViewMode != Flight.Camera.PilotViewMode.Chase && !Fly)
        {
            Warn("core", $"--view={Flight.Camera.PilotView.Name(ViewMode)} is a flight camera; "
                         + "ignoring it outside --fly/--stunt");
            ViewMode = Flight.Camera.PilotViewMode.Chase;
        }
        // An unknown surface name would otherwise search for an id no collider can carry and
        // report it missing, which reads as a map fact rather than a typo.
        if (WeaponSurface is { } wantSurface && Mech3.SurfaceRegistry.IdForName(wantSurface) == null)
        {
            Warn("ui", $"--weapon-surface={wantSurface} is not a surface-registry name "
                       + $"({string.Join('/', Mech3.SurfaceRegistry.Names)}), ignoring it");
            WeaponSurface = null;
        }
        // A non-flight mode that won the arbitration above would silently leave the weapon lab
        // half-built, so this is reported instead.
        if ((WeaponLab || WeaponMount != null || WeaponFire || WeaponCycle > 0 || WeaponClick
             || WeaponTarget != null || WeaponSurface != null || WeaponStandoff > 0f
             || WeaponFreeCamera || WeaponCameraToggle > 0) && !Fly)
        {
            Warn("core", "the --weapon-lab flags need flight; another mode flag won this session, so the lab will not build");
        }
        bool observing = Mode == SessionMode.Freecam || Mode == SessionMode.AnimLab;
        if (DebugSelect != null && !observing)
        {
            Warn("ui", "--debug-select is a --freecam/--anim-lab tool; ignoring it here");
            DebugSelect = null;
        }
        if (DebugNodeLab != null && !observing)
        {
            Warn("ui", "--debug-nodelab is a --freecam/--anim-lab tool; ignoring it here");
            DebugNodeLab = null;
        }
        if (DebugDamage != null && !observing)
        {
            Warn("ui", "--debug-damage is a --freecam/--anim-lab tool; ignoring it here (--damage-test is the headless twin)");
            DebugDamage = null;
        }
        // A preset with nothing to apply to would otherwise be a silent no-op, and a run that shows
        // no damage would read as the staging being broken rather than as the flag being unused.
        if (AiHullDamage != null && AiPlanes == null)
        {
            Warn("ui", "--ai-damage needs --ai=<plane> to spend on; no AI aircraft were asked for");
            AiHullDamage = null;
        }
        // Same rule for the canopy: the cue and its defs belong to a flown aeroplane's own rig. A
        // parked viewer plane has no crash rig to run them through.
        if (CanopyHoles != null && !Fly && !Stunt)
        {
            Warn("ui", "--canopy-holes needs a flown aircraft (--fly/--stunt); ignoring it");
            CanopyHoles = null;
        }
        // The two lab spec grammars live in their labs; the reject list keeps them from logging,
        // which is what lets this run with no engine under it.
        DebugNodeLab = FilterSpec(DebugNodeLab, UI.Labs.NodeLab.ParseDebugSpec, "--debug-nodelab token",
            "is not deps/dest/open/all/node=<cs_name>");
        DebugDamage = FilterSpec(DebugDamage, UI.Labs.WorldDamageLab.ParseDebugSpec, "--debug-damage step",
            "is not node=/pool=/hp=/kill/reset/tick=/open");

        // --stage= replaces the chapter world outright, so it is a flight/spectator affair: there
        // is no gamez to inspect, which is what the static viewer and the anim lab exist for.
        if (Stage != null)
        {
            if (!string.Equals(Stage, "empty", StringComparison.OrdinalIgnoreCase))
            {
                Print($"--stage='{Stage}' is not a known stage (only 'empty'); ignoring");
            }
            else if (Mode == SessionMode.Viewer || Mode == SessionMode.AnimLab || NodeName != null)
            {
                Print("--stage=empty has no gamez to inspect; ignoring it in --viewer/--anim-lab/--node=");
            }
            else
            {
                EmptyStage = true;
            }
        }
        // --zep= reads its record's gamez, textures, nets and zeppelins.zrd off the chapter and
        // mission it names, so it takes both rather than threading a second pair through the build.
        // ⚠ Empty stage only: a chapter world already places that hull through --zeppelins.
        if (Zep is { } zep)
        {
            if (!EmptyStage)
            {
                Print("--zep= grafts onto --stage=empty; a chapter world places its own with --zeppelins. Ignoring it");
                Zep = null;
            }
            else
            {
                Chapter = zep.Chapter;
                Mission = zep.Mission;
                ChapterGiven = true;
            }
        }
        // The static viewer shows a chapter world when asked for one, else the parked plane.
        // Flight, the spectator view and the anim lab always need the world built.
        WorldMode = !EmptyStage && (Fly || Freecam || AnimLab || (Viewer && ChapterGiven));
        // A --plane= list of several aircraft states the player count on its own; an explicit
        // --players= still wins.
        if (!PlayersExplicit && PlaneNames.Count > 1)
        {
            Players = PlaneNames.Count;
        }
        Players = Mathf.Clamp(Players, 1, UI.Boards.SplitScreen.MaxPlayers);
        if (Players > 1 && !Fly)
        {
            Print($"--players={Players} needs flight (nothing to fly in --viewer); using 1");
            Players = 1;
        }
        // The menu refuses to start a Dogfight below 2 joined pilots; the CLI has no join flow to
        // gate on, so it only warns and runs with whatever --players= asked for.
        if (Versus && Players < 2)
        {
            Warn("core", $"--vs with --players={Players} needs at least 2 pilots to fight (the menu enforces this; the CLI only warns)");
        }
        if (DamageLab && WorldMode)
        {
            Print("--damage is the plane lab (use --viewer --plane without --chapter); ignoring");
            DamageLab = false;
        }

        // --mute never builds the audio subsystem, so there is no bus carrying anything for a gain
        // to attenuate. Neither flag is cleared, the result is silence either way, but a run
        // asking for both wanted the sounds to still play, and would not get them.
        if (Mute && Volume != null)
        {
            Warn("core", "--mute skips loading audio entirely, so --volume has nothing to attenuate; drop --mute to hear the sounds counted and logged");
        }

        // The --det bundle. Det/PadsDisabled/SeedPinned are computed from what is settled by now;
        // the two values it PINS are the only ones that have to be written down.
        if (Det && SpawnIndex < 0)
        {
            SpawnIndex = 0;
        }
        if (JitterDeg < 0f)
        {
            JitterDeg = ScreenshotShots > 1 && !Det ? 0.15f : 0f;
        }

        ResolvePlacement();
    }

    // Routes `--pos`/`--direction` onto the per-mode plumbing (docs/cli.md "the placement pair"):
    // the plane's spawn override in flight, the camera's placement everywhere else. Routed here,
    // in one place, so no downstream consumer asks what mode it is in.
    private void ResolvePlacement()
    {
        if (Fly && Direction == null && LookAt is { } aimPoint && (Pos ?? SpawnAt) is { } eye)
        {
            Direction = aimPoint - eye;
        }
        if (Direction is { } aim)
        {
            Direction = aim.LengthSquared() > 1e-6f ? aim.Normalized() : null;
        }
        if (Pos is { } place)
        {
            if (Fly)
            {
                SpawnAt = place;
            }
            else
            {
                CamPos = place;
            }
        }
        if (Direction is { } dir)
        {
            if (Fly)
            {
                SpawnDir = dir;
            }
            else
            {
                CamDir = dir;
            }
        }
        // A nose direction with nothing to place it on is a silently ignored argument: the spawn
        // override only engages when a position was given.
        if (Fly && SpawnDir != null && SpawnAt == null)
        {
            Warn("core", "--direction ignored: flight steers the nose from the spawn override, which needs --pos");
        }
        // The empty stage has no mission spawn list to draw from, so the subject starts over the
        // grid origin, through the same fields --pos resolves into, so an explicit placement wins.
        if (EmptyStage)
        {
            if (Fly)
            {
                SpawnAt ??= new Vector3(0f, Mech3.EmptyStage.SpawnAltitude, 0f);
            }
            else
            {
                CamPos ??= Mech3.EmptyStage.CameraPos;
            }
        }
    }

    // Runs a lab's own token filter over a spec value without letting it log.
    private string? FilterSpec(string? spec, Func<string, List<string>?, string> filter,
        string what, string wanted)
    {
        if (spec == null)
        {
            return null;
        }
        var rejected = new List<string>();
        string kept = filter(spec, rejected);
        foreach (var token in rejected)
        {
            Warn("ui", $"{what} '{token}' {wanted}, ignoring it");
        }
        return kept;
    }

    // Reads `--mips=`. An unreadable value keeps the current policy rather than
    // quietly falling back to one of them, a run whose mip source is not what was asked for is a
    // run whose pixel evidence means nothing.
    private void SetMips(string value)
    {
        if (Enum.TryParse<TextureArchive.MipSource>(value, ignoreCase: true, out var source))
        {
            Mips = source;
        }
        else
        {
            Warn("world", $"--mips='{value}' is neither 'authored' nor 'generated', "
                          + $"keeping {Mips.ToString().ToLowerInvariant()}");
        }
    }

    private void Warn(string category, string message) => _notes.Add(new Note(category, message));

    // A complaint that names no category of its own; Launcher logs it under `core`, at info.
    private void Print(string message) => _notes.Add(new Note("", message));

    /// <summary>A parse- or resolve-time complaint, held rather than logged so the spec stays
    /// engine-free. The <paramref name="Category"/> is the <c>Log</c> category to emit it under,
    /// at warning level. Empty means the note has no category, and <c>Launcher</c> logs it under
    /// <c>core</c> at info, where an argument complaint sits in launch order.</summary>
    public readonly record struct Note(string Category, string Message);
}
