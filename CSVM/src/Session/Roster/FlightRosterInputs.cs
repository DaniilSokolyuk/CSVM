using System;
using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hangar;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Session.Roster;

internal sealed class FlightRosterPolicy
{
    public (FlightInput Input, float Duration)[][]? HoldSets { get; init; }
    public IReadOnlyList<(float At, float Lever)>? LeverSteps { get; init; }
    public IReadOnlyList<LoadoutChoice?> MenuLoadouts { get; init; } = Array.Empty<LoadoutChoice?>();
    public IReadOnlyList<CustomPlaneDef?> MenuCustomPlanes { get; init; } = Array.Empty<CustomPlaneDef?>();
    public IReadOnlyList<string> PlaneNames { get; init; } = Array.Empty<string>();
    public string PlaneName { get; init; } = "player_bhawk";
    public string Chapter { get; init; } = "";
    public string Mission { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string? LoadoutOverride { get; init; }
    public string? RocketOverride { get; init; }
    public string? TargetSelect { get; init; }
    public string HudFontTestText { get; init; } = "";
    public int AnimLod { get; init; }
    public int GunSelect { get; init; }
    public int View { get; init; }
    public PilotViewMode ViewMode { get; init; }
    public Vector2 PinnedLook { get; init; }
    public int? AmmoCap { get; init; }
    public int? AiAttackSkill { get; init; }
    public bool AiAttackSkillExplicit { get; init; }

    /// <summary>The session difficulty (<see cref="CSVM.Flight.Hangar.Difficulty"/>), which scales enemy
    /// armour and health at spawn and nothing else. An Instant Action wave's skill overrides it per
    /// spawn through <see cref="AiSpawn.Difficulty"/>.</summary>
    public int Difficulty { get; init; } = CSVM.Flight.Hangar.Difficulty.Normal;
    /// <summary>The saved targeting setting (<see cref="CSVM.Flight.Weapons.TargetSelection.NearestAfterKill"/>),
    /// off by default, which is the decoded head rule.</summary>
    public bool NearestAfterKill { get; init; }
    /// <summary>The saved automatic head turn
    /// (<see cref="CSVM.Flight.Airframe.FlightController.AutoHeadTurn"/>), null where never set, which
    /// leaves the <c>headLook.autohead</c> config key deciding.</summary>
    public bool? AutoHeadTurn { get; init; }
    public bool AutoFire { get; init; }
    public bool AutoFireRockets { get; init; }
    public bool DebugMarkers { get; init; }
    public bool DebugScoreboard { get; init; }
    public bool EmptyStage { get; init; }
    public bool HudFontTest { get; init; }
    public bool InfiniteAmmo { get; init; }
    public bool NoAssist { get; init; }
    public bool WeaponLab { get; init; }

    /// <summary>Whether a human seat may take the desktop mouse while it flies
    /// (<see cref="MouseCapture.Allowed"/>, whose ⚠ carries the reason for each half of the
    /// answer).</summary>
    public bool MouseCaptureAllowed { get; init; }

    public static FlightRosterPolicy From(SessionSpec spec) => new()
    {
        HoldSets = spec.HoldSets,
        LeverSteps = spec.LeverSteps,
        MenuLoadouts = spec.MenuLoadouts,
        MenuCustomPlanes = spec.MenuCustomPlanes,
        PlaneNames = spec.PlaneNames,
        PlaneName = spec.PlaneName,
        Chapter = spec.Chapter,
        Mission = spec.Mission,
        Scenario = spec.Scenario,
        LoadoutOverride = spec.LoadoutOverride,
        RocketOverride = spec.RocketOverride,
        TargetSelect = spec.TargetSelect,
        HudFontTestText = spec.HudFontTestText,
        AnimLod = spec.AnimLod,
        GunSelect = spec.GunSelect,
        View = spec.View,
        ViewMode = spec.ViewMode,
        PinnedLook = spec.PinnedLook,
        AmmoCap = spec.AmmoCap,
        AiAttackSkill = spec.AiAttackSkill,
        AiAttackSkillExplicit = spec.AiAttackSkillExplicit,
        Difficulty = spec.Difficulty,
        NearestAfterKill = spec.NearestAfterKill,
        AutoHeadTurn = spec.AutoHeadTurn,
        AutoFire = spec.AutoFire,
        AutoFireRockets = spec.AutoFireRockets,
        DebugMarkers = spec.DebugMarkers,
        DebugScoreboard = spec.DebugScoreboard,
        EmptyStage = spec.EmptyStage,
        HudFontTest = spec.HudFontTest,
        InfiniteAmmo = spec.InfiniteAmmo,
        NoAssist = spec.NoAssist,
        WeaponLab = spec.WeaponLab,
        // The display is read here because it is a property of the launch, not of the seat. It
        // answers for a headless host alone; the hidden test desktop is a real one, and the
        // scripted arm is what keeps a suite's mouse mode its own.
        MouseCaptureAllowed = MouseCapture.Allowed(
            DisplayServer.GetName() != "headless", spec.Det, spec.IsScripted),
    };
}

internal sealed class AircraftAssemblyResources
{
    public GameZ PlanesGamez { get; init; } = null!;
    public Func<string, PlaneStats> StatsFor { get; init; } = null!;
    public Func<string, string?, PlaneStats> AiStatsFor { get; init; } = null!;
    public Func<string, CamParams> CamParamsFor { get; init; } = null!;
    public WeaponDefs WeaponDefs { get; init; } = null!;
    public Messages WeaponMessages { get; init; } = null!;
    public StockLoadouts StockLoadouts { get; init; } = null!;
    public TurretDefs? TurretDefs { get; init; }
    public ShakeDefs Shakes { get; init; } = null!;
    public TextureArchive Textures { get; init; } = null!;
    public HudFont? HudFont { get; init; }
    public Texture2D? ReticleTex { get; init; }
    public RandomNumberGenerator PaintRng { get; init; } = null!;
    public string ZrdrPath { get; init; } = "";
}

internal sealed class FlightWorldBindings
{
    public ProjectilePool Projectiles { get; init; } = null!;
    public Func<IReadOnlyList<Vector3>>? HumanPositions { get; init; }
    public EffectAmbience Ambience { get; init; } = EffectAmbience.Still;
    public GameZ Gamez { get; init; } = null!;
    public SceneBuilder? WorldScene { get; init; }
    public AnimRuntime? WorldRuntime { get; init; }
    public AnimRuntime? WorldEffects { get; init; }
    public SurfaceVehicleRuntime? SurfaceVehicles { get; init; }
    public SurfaceDefTable? TouchdownDefs { get; init; }
    public AnimProgram? CrashProgram { get; init; }
    public SoundArchive? Sounds { get; init; }
    public Dictionary<string, SoundDef>? SoundDefs { get; init; }
    public Dictionary<string, SoundGroup>? SoundGroups { get; init; }
    public string ChapterZrdrPath { get; init; } = "";
    public string MissionZrdrPath { get; init; } = "";
    public bool DebugCollision { get; init; }

    /// <summary>The live fog band (near, far) the spyglass's range gate reads. A closure rather
    /// than the pair itself: the zone apply rewrites it mid-mission, and the weather rig is built
    /// after these bindings are.</summary>
    public Func<Vector2>? FogRange { get; init; }
}

internal sealed class HumanRosterBindings
{
    /// <summary>Seats, not panes. A network match's remote guests are counted here too, which is
    /// what sizes the spawn walk and the scoreboard over the whole field.</summary>
    public int RigCount { get; init; }

    /// <summary>The network match's seat roster, indexed by seat, empty outside one (which reads
    /// as every seat local). A seat that is not <see cref="Net.NetSeat.IsLocal"/> is assembled
    /// with an aircraft, a spawn slot, a score row and a marker colour. It gets no pane, HUD,
    /// camera, listener or input device. Where a seat names an airframe, the roster's pick beats
    /// this machine's launch flags: every peer has to build the same field.</summary>
    public IReadOnlyList<Net.NetSeat> NetSeats { get; init; } = Array.Empty<Net.NetSeat>();

    /// <summary>The fit a seat flown elsewhere carries, by seat, or null for its stock fit.</summary>
    public Func<int, LoadoutChoice?>? SeatFit { get; init; }
    public float MixGain { get; init; } = 1f;
    public int[][]? PadAssignment { get; init; }
    public PauseState PauseState { get; init; } = null!;
    public Func<int, MenuInput> MenuInputFor { get; init; } = null!;
    public bool ExitsToMenu { get; init; }
    public Action ExitSession { get; init; } = null!;
    public List<SpawnPoint>? SpawnList { get; init; }
    public int SpawnBase { get; init; }
    public StuntMission? StuntZones { get; init; }
    public StuntRace? Race { get; init; }
    public VersusMatch? VersusMatch { get; init; }
    public IReadOnlyList<PlayerRig> Rigs { get; init; } = Array.Empty<PlayerRig>();
    public string? InstantActionPlayerPlaneNode { get; init; }
    public bool InstantActionActive { get; init; }
    public bool Coop { get; init; }
    public SmokeScreens? SmokeScreens { get; init; }
}
