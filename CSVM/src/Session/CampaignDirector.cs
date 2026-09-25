using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>What one flown campaign mission ended as: the outcome the objectives graph derived,
/// the attempt recorded into the profile, and what recording it changed. <c>Chapter</c> and
/// <c>SkipCapture</c> are what a screen taking the skip offer needs to record it as the win the
/// original makes it (<see cref="CampaignProgression.AcceptSkip"/>); the capture is null unless
/// this failure raised the offer, since it exists only while the flown world is still up.</summary>
public readonly record struct CampaignMissionResult(
    MissionOutcome Outcome, MissionAttempt Attempt, MissionRecorded Recorded,
    int Chapter = 0, IReadOnlyList<PersistedObject>? SkipCapture = null);

/// <summary>
/// The engine-side runtime of one campaign mission, behind <c>GameSession</c>'s one nullable
/// <c>_campaign</c> field and the sibling of <see cref="InstantActionDirector"/>. A plain sealed
/// class, not a Node: <c>GameSession</c> owns the tick order and calls the phases at its pinned
/// points, and nothing here builds a node of its own. The decoded rules stay engine-free in
/// <see cref="ObjectiveScript"/> and <see cref="ObjectiveGraph"/>; this class is where they meet
/// the engine, and it owns every "campaign:" log line. Mission end records the attempt through
/// <see cref="CampaignProgression"/>, folds the destruction log into the profile and raises
/// <see cref="ReturnToCabin"/> for the session layer to act on.
/// </summary>
public sealed class CampaignDirector
{
    /// <summary>The name the original registers the campaign wingman's aircraft under
    /// (<c>docs/formats/saved-games.md</c>, <c>docs/formats/campaign-screens.md</c>), and the
    /// roster block <see cref="BuildRoster"/> flies the profile's <see cref="WingmanNode"/> and
    /// <see cref="WingmanFit"/> as.</summary>
    public const string WingmanName = "wingman_1";

    /// <summary>How long the world stays up after an ending before the session leaves it. The
    /// original's mission-end path (<c>FUN_00443090</c>) pushes its "Fade State" over a copy of the
    /// frame the ending landed on and runs it for this, its default duration, before the next
    /// screen takes the machine (docs/formats/objectives.md, "Win and loss"). The flown world does
    /// not advance and does not answer the stick while it plays out, so the last flown frame is the
    /// frame the ending landed on.</summary>
    public const float LeavingHoldS = 2f;

    // The other roster code of the mission-script host, and its literal, the way `cargozep1` is
    // 800's: the docking film raises it to take the escorting wingman out of the world, and its very
    // next event is her own "down" line. One occurrence in the install
    // (docs/formats/anim-definitions/cutscenes.md).
    private const int CodeRemoveWingman = 968;
    private const string RemovedWingman = "bswingman_1";

    private readonly CampaignProfileDef _profile;
    private readonly CampaignProfileStore? _store;
    private readonly CampaignMission _mission;
    private readonly string _missionZrdrPath;

    // The objective numbers this attempt's completed danger zones stand for, the second source of
    // the completed-objective mask (docs/org/debrief.md, "The completed-objective mask has two
    // sources"). Kept here rather than in the graph: the numbers are dzones.zrd's, not an
    // IDENTITY's, and no objective node carries one.
    private readonly HashSet<int> _zonesCompleted = new();

    // The story position whose world state this mission opens on: the most recent EARLIER mission
    // of the same chapter, or null when this IS that chapter's first and the original's backwards
    // walk finds nothing. ⚠ Never the mission's own seq: CM07 is chapter 1's first mission, and a
    // fold that included it opened the fort with the previous sortie's AA guns already wrecked.
    private readonly int? _carryThroughSeq;
    private readonly HashSet<string> _gapsLogged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _leaderlessReported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlightController> _roster = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RosterSpawnPlan> _rosterPlans = new(StringComparer.OrdinalIgnoreCase);

    // A block's actual placement, apart from its authored plan: a world node override at spawn,
    // or the authored pose otherwise. WAKEUP_ENEMIES re-places a deactivated block here, not at
    // the plan's authored pose, so a script-moved block wakes where it now stands.
    private readonly Dictionary<string, (Vector3 Position, Vector3 Forward)> _rosterPlacedPose =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SurfaceVehicle> _vessels = new(StringComparer.OrdinalIgnoreCase);

    // A guest's two WARP_VEHICLE halves, keyed by the vehicle's name hash: the directive its graph
    // replayed and the host's drawn index. Either can arrive first, so each waits for the other.
    private readonly Dictionary<int, Queue<(string Vehicle, IReadOnlyList<WarpPoint> Points)>> _warpsAwaitingPick = new();
    private readonly Dictionary<int, Queue<int>> _warpPicksAwaitingDirective = new();

    // Roster blocks authoring their own objectiveTarget flag (aiv slot 37), keyed by block name,
    // with the MSG_OBJ_* label their own slot 39 carries. The mission authors this ON the
    // vehicle, not through a targets.zrd entry, which is why the marker is stamped onto the
    // spawned aeroplane and this stays the record of which blocks carry one.
    private readonly Dictionary<string, string> _rosterObjectiveMarkers =
        new(StringComparer.OrdinalIgnoreCase);

    // The two per-airframe kill tallies (docs/org/debrief.md#what-the-tallies-count),
    // credited as the roster's own aircraft go down. Read into the mission-end attempt; never
    // written from anywhere else.
    private readonly int[] _kills = new int[CampaignProgression.AirframeCount];
    private readonly int[] _aceKills = new int[CampaignProgression.AirframeCount];

    // The death wiring and the loss latch, both per SEAT of the human field rather than per
    // aircraft: a 967 swap rebuilds one seat's aeroplane and a death in the new one counts too,
    // and the seat is what stays down once it has.
    private readonly List<FlightController?> _deathWiredTo = new();
    private readonly HashSet<int> _seatsDown = new();
    private World? _world;
    private ScriptedPathVehicles? _paths;

    // What the three SET_AI_* directives need after the roster phase has run: the chapter's nets
    // by name, the trailer resolver a re-commanded net needs to ride its target, and the floor
    // every activation radius takes. Kept because a mission re-commands its aircraft long after
    // BuildRoster's inputs have gone out of scope.
    private IReadOnlyList<AiNet> _chapterNets = Array.Empty<AiNet>();
    private NetTrailerTargets? _netTrailers;
    private float _minAiActiveDist = 2000f;
    private CampaignDangerZones? _dangerZones;
    private PaneRequest? _playerPane;
    private DangerZoneRibbons? _ribbons;
    private Messages? _strings;
    private bool _cutsceneHold;

    // What is left of the leaving hold below, once an ending has started it.
    private float _leaving;

    // The proximity scan's accumulator and the player aircraft the damage and death events are
    // subscribed on: the player's aircraft is built after Attach runs, so the hookup is made on
    // the first step that finds one, and an airframe swap rebuilds the rig, so a later step that
    // finds a different aircraft hooks that one (the old node is freed with its subscriptions).
    private float _scanClock;
    private FlightController? _damageWiredTo;
    private Action<FlightController>? _beginSpectate;
    private bool _playerLost;

    // The seated pilot's own gunnery is wired the same deferred way as the damage ping, since
    // the scripted player's aircraft is also built after Attach runs.
    private ProjectilePool? _projectiles;
    private FlightController? _scoredShooterWiredTo;

    // This director's link in the anim runtime's CALLBACK host chain, and whatever held the slot
    // before it. Kept so a re-bind neither chains to itself nor drops the rest of the chain.
    private Func<int, string?, string?, bool>? _callbackHost;
    private Func<int, string?, string?, bool>? _innerCallbackHost;

    private CampaignDirector(
        ObjectiveScript script, CampaignMission mission,
        CampaignProfileDef profile, CampaignProfileStore? store, string missionZrdrPath,
        int? carryThroughSeq)
    {
        Script = script;
        _mission = mission;
        _profile = profile;
        _store = store;
        _missionZrdrPath = missionZrdrPath;
        _carryThroughSeq = carryThroughSeq;
    }

    /// <summary>The authored-aircraft spawner, handed in as a delegate for the same reason
    /// <c>InstantActionDirector</c>'s is: the spawner and its roster stay <c>GameSession</c>'s.</summary>
    internal delegate FlightController? SpawnRosterAircraft(RosterSpawnPlan plan, Vector3 pos,
        Vector3 lookAt, AiPilot pilot);

    /// <summary>Fired once the mission has ended and the profile has been written.</summary>
    public event Action<CampaignMissionResult>? MissionEnded;

    /// <summary>Raised on each <c>WARP_VEHICLE</c> draw with the vehicle's name and the index drawn
    /// from the directive's list, before the placement. The host's network link sends it.</summary>
    public event Action<string, int>? WarpDrawn;

    /// <summary>Whether <c>WARP_VEHICLE</c> waits for another machine's draw
    /// (<see cref="TakeHostWarp"/>) instead of drawing its own.</summary>
    public bool WarpsFromHost { get; private set; }

    /// <summary>The process's music channel, or null when the session was built without one. The
    /// mission's own <c>WAKEUP_SOUND_GROUP music_*_sg</c> is what cues prebattle, the stingers and
    /// both success tracks, so nothing here hard-codes a state (<c>docs/org/music.md</c>).</summary>
    public MusicPlayer? Music { get; set; }

    /// <summary>The stock node the wingman's aircraft would fly as, or null when this mission has
    /// no wingman. Read with <see cref="WingmanFit"/> by whoever spawns it.</summary>
    public string? WingmanNode { get; private set; }

    /// <summary>The wingman aircraft's ammunition and ordnance picks off the profile, or null when
    /// this mission has no wingman.</summary>
    public LoadoutChoice? WingmanFit { get; private set; }

    /// <summary>Whether losing the player's aircraft ends the mission, which is the original's rule
    /// and the default here. <c>--no-crash-loss</c> clears it so a session being debugged can fly
    /// on past a crash; <c>GameSession</c> is the only writer.</summary>
    public bool EndsOnPlayerDeath { get; set; } = true;

    /// <summary>The mission's parsed choreography script.</summary>
    public ObjectiveScript Script { get; }

    /// <summary>The objectives runtime, null until <see cref="Attach"/> has run. D33 reads its
    /// <see cref="ObjectiveGraph.Rows"/> and subscribes to its wake/complete events.</summary>
    public ObjectiveGraph? Graph { get; private set; }

    /// <summary>The mission's scripted-path vehicles, null until <see cref="BuildRoster"/> or
    /// <see cref="Attach"/> has run. <see cref="BuildRoster"/> places a vehicle carrying a
    /// <c>taxiPath</c> here; <c>START_TAXI</c> releases it.</summary>
    public ScriptedPathVehicles? Paths => _paths;

    /// <summary>The spawned roster, by block name, empty until <see cref="BuildRoster"/> has run,
    /// plus every generator launch under its launch name (<see cref="RegisterGeneratorLaunch"/>).
    /// The player's own block is not in it, nor is a surface vehicle (<see cref="Vessels"/>).</summary>
    public IReadOnlyDictionary<string, FlightController> Roster => _roster;

    /// <summary>The roster's surface vehicles, by block name: the <c>mode ship</c> blocks a
    /// <see cref="RosterInputs.SpawnSurface"/> built, plus a ship generator's hulls under their
    /// launch names.</summary>
    public IReadOnlyDictionary<string, SurfaceVehicle> Vessels => _vessels;

    /// <summary>Roster blocks that carry their own objective-target flag (aiv slot 37), by block
    /// name, with the MSG_OBJ_* label their own slot 39 authors. The mission's record of which
    /// blocks carry a marker, for the sortie log and the suites. ⚠ Not what draws one: the marker
    /// is stamped onto the block's own aircraft at the spawn
    /// (<see cref="FlightController.ObjectiveTarget"/>), so a bay-launched block gets one too and
    /// nothing here has to be looked up per frame.</summary>
    public IReadOnlyDictionary<string, string> RosterObjectiveMarkers => _rosterObjectiveMarkers;

    /// <summary>The directive kinds this session has declined for want of a consumer, one entry per
    /// kind, which is the set behind the <c>Gap</c> line. A suite asserts an arm exists by a
    /// directive's absence here after the clause has run.</summary>
    public IReadOnlyCollection<string> Gaps => _gapsLogged;

    /// <summary>The story position being flown.</summary>
    public int Seq => _mission.Seq;

    /// <summary>The flying profile's own name, the original's <c>PlayerName</c>: the subject of
    /// the HUD kill line when the player is the one shot down. A session with no campaign takes
    /// the same name from the profile last used
    /// (<see cref="CampaignProfileStore.LastPlayedPilotName"/>).</summary>
    public string? PilotName => _profile.Name;

    /// <summary>The picture the flying profile hangs, as the bitmap name a sheet draws: what the
    /// pause screen's memento slot takes, and the same one the cabin wall carries. A profile that
    /// has chosen none draws the seeded pin-up (<c>docs/org/pause-screen.md</c>).</summary>
    public string Memento => CampaignMementos.BitmapFor(_profile);

    /// <summary>The mission's storage address, the ZBD world folder number and its <c>M0n</c>
    /// number. ⚠ Not the act and its position; it is the pair every per-mission dialog key is built
    /// from (docs/formats/campaign-missions.md).</summary>
    public (int Campaign, int Mission) Address => (_mission.Campaign, _mission.Mission);

    /// <summary>The mission's own <c>objectives.zrd</c> path, for a screen that reads the reader
    /// rather than this director's own graph.</summary>
    public string MissionZrdrPath => _missionZrdrPath;

    /// <summary>Raised when the mission has ended and its result is banked: the session layer's cue
    /// to leave the world and put the player back in the cabin. The cabin screen itself is C22's.</summary>
    public bool ReturnToCabin { get; private set; }

    /// <summary>Whether the mission has ended and the leaving hold is still running. The outcome is
    /// already decided and the profile already written; what has not happened yet is leaving the
    /// world. Nothing in the world may advance while this is true, and no input may reach the
    /// player's aircraft.</summary>
    public bool Leaving => _leaving > 0f;

    /// <summary>The result of the flown mission, null until it ends. Set on the frame the ending
    /// lands, which is <see cref="LeavingHoldS"/> before <see cref="ReturnToCabin"/>.</summary>
    public CampaignMissionResult? Result { get; private set; }

    /// <summary>How black the mission-end overlay should sit right now: 0 before any ending, then
    /// the same ramp <see cref="LeavingHoldS"/> counts down, reaching 1 the frame the hold ends and
    /// staying there, since <see cref="Leaving"/> itself has already gone false by then. Reads
    /// <see cref="Result"/> rather than a dedicated flag because the two are set together in
    /// <see cref="OnMissionEnded"/>.</summary>
    public float LeavingFade => Result is null ? 0f : _leaving > 0f ? (LeavingHoldS - _leaving) / LeavingHoldS : 1f;

    /// <summary>How many danger-zone gates <see cref="Attach"/> armed from a real
    /// <see cref="WorldInputs.Gamez"/>. 0 before <see cref="Attach"/>, or when the mission
    /// names no <c>DANGER_ZONES_COMPLETED</c> zone, or none resolved.</summary>
    internal int ArmedDangerZones => _dangerZones?.Count ?? 0;

    /// <summary>Resolves a <c>--campaign=&lt;profile&gt;:&lt;seq&gt;</c> launch's chapter and
    /// mission out of <c>cm_sequence.zrd</c>, so the rest of the build sees an ordinary
    /// chapter/mission session. Returns the spec unchanged when no campaign mission was asked for,
    /// or when the story position is not one the sequence carries.</summary>
    public static SessionSpec ResolveSpec(SessionSpec spec, string zrdrPath)
    {
        if (spec.CampaignProfile == null || spec.CampaignMissionSeq is not { } seq)
        {
            return spec;
        }

        if (MissionFor(zrdrPath, seq) is not { } mission || mission.ChapterFolder.Length == 0)
        {
            GD.PushWarning($"--campaign: seq {seq} is not in cm_sequence, flying the CLI chapter instead");
            return spec;
        }

        Log.Info("core", $"campaign: seq {seq} '{mission.Desc}' -> {mission.ChapterFolder}/{mission.MissionFolder}");
        return spec.WithCampaignMission(
            mission.ChapterFolder.ToUpperInvariant(), mission.MissionFolder.ToUpperInvariant());
    }

    /// <summary>Seats the profile's own selected aircraft on a command-line
    /// <c>--campaign=</c> launch, which passed no launchscreen and so carries nobody's aeroplane.
    /// <c>--plane=</c> entry 0 beats the profile and says so; entries 1 and up name guests and are
    /// left alone. Returns the spec unchanged outside a campaign launch, and for a profile that
    /// cannot be read (<see cref="TryCreate"/> reports that one).</summary>
    public static SessionSpec ResolveSeatedPlane(SessionSpec spec)
    {
        if (spec.CampaignProfile == null
            || CampaignProfileStore.UserProfiles().Load(spec.CampaignProfile) is not { } profile
            || profile.Planes.Count == 0)
        {
            return spec;
        }

        int at = Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1);
        var plane = profile.Planes[at];
        string node = UI.PlanePickerRoster.AirframeNode(plane.Airframe);
        // A named entry 0 wins whether it came from --plane= or from the launchscreen's own seat,
        // which is already this node. Comparing the node rather than tracking where the name came
        // from is what keeps a cabin launch silent: it seated the same aeroplane.
        if (spec.PlaneNames.Count > 0)
        {
            if (!string.Equals(spec.PlaneNames[0], node, StringComparison.OrdinalIgnoreCase))
            {
                GD.PushWarning($"--plane={spec.PlaneNames[0]} overrides the seated aircraft: " +
                               $"'{profile.Name}' flies \"{plane.Name}\" ({node}) at story position " +
                               $"{spec.CampaignMissionSeq}, and it is flying stock {spec.PlaneNames[0]} instead");
            }

            return spec;
        }

        // The same three things CampaignLaunch carries, resolved the way FlyCampaignMission does:
        // a reward aircraft with no file in the build store falls back to its own award template.
        var custom = CustomPlaneStore.UserPlanes().Load(plane.Name)
                     ?? CampaignProgression.BuildForOwned(plane);
        Log.Info("core", $"campaign: '{profile.Name}' seated in \"{plane.Name}\" as {node}");
        return spec.WithSeatedAircraft(node, custom, CampaignLoadout.For(plane, StockLoadouts.Load()));
    }

    /// <summary>Construction, the shape <see cref="InstantActionDirector.TryCreate"/> has: null
    /// outside a campaign launch, and a profile that cannot be loaded warns and flies without a
    /// director rather than aborting the launch.</summary>
    public static CampaignDirector? TryCreate(SessionSpec spec, string zrdrPath, string missionZrdrPath)
    {
        if (spec.CampaignProfile == null || spec.CampaignMissionSeq is not { } seq)
        {
            return null;
        }

        var store = CampaignProfileStore.UserProfiles();
        if (store.Load(spec.CampaignProfile) is not { } profile)
        {
            GD.PushWarning($"--campaign={spec.CampaignProfile}: " +
                           $"{store.LoadProblem(spec.CampaignProfile)}, flying without a mission");
            return null;
        }

        if (MissionFor(zrdrPath, seq) is not { } mission)
        {
            return null;
        }

        var script = ObjectiveScript.Load(missionZrdrPath);
        Log.Info("core", $"campaign: '{profile.Name}' flying {mission.ChapterFolder}/{mission.MissionFolder}, {script.Objectives.Count} objective(s)");
        var director = new CampaignDirector(script, mission, profile, store, missionZrdrPath,
            CampaignSequence.PreviousInSameChapter(CampaignSequence.Load(zrdrPath), seq)?.Seq);
        director.BindWingman();
        return director;
    }

    /// <summary>The suite/test entry: a director over an already-loaded script, mission and
    /// profile, with no profile file behind it unless one is handed in. <paramref
    /// name="missionZrdrPath"/> is only needed to arm danger zones (its <c>dzones.zrd</c> overrides,
    /// which say what is disabled and which objective each zone photographs into); omitted, every
    /// zone is armed and none is bound to an objective.</summary>
    internal static CampaignDirector Create(
        ObjectiveScript script, CampaignMission mission,
        CampaignProfileDef profile, CampaignProfileStore? store, string missionZrdrPath = "",
        int? carryThroughSeq = null) =>
        new(script, mission, profile, store, missionZrdrPath, carryThroughSeq);

    /// <summary>Hands every wingman whose leader has left play the leader's own patrol net, and
    /// returns how many were moved. A netless escort has no orders of its own once its leader is
    /// gone: <see cref="AiPilot"/>'s netless arm projects the heading and altitude its last pursuit
    /// wrote, so the pilot holds a bearing to a dead aeroplane. Every escort block in the shipped
    /// campaign names a netted leader, so the leader's own route is authored data rather than an
    /// invented fallback. <paramref name="reported"/> keeps the no-net case to one line per pilot.</summary>
    internal static int TakeLostLeadersNets(IReadOnlyDictionary<string, FlightController> roster,
        NetTrailerTargets? trailers, float minAiActiveDist, HashSet<string> reported)
    {
        int moved = 0;
        foreach (var (name, rig) in roster)
        {
            if (rig.Pilot is not { Patrol: null } pilot
                || pilot.Escort is not { Leader: { InPlay: false } leader })
            {
                continue;
            }

            // A leader that flies no net (a human, or a block a script re-seated) leaves the
            // wingman exactly as it is: there is no second route in the data to give it, and
            // guessing one would put the aeroplane somewhere nothing authored.
            if (leader.Pilot?.Patrol?.Net is not { } net)
            {
                if (reported.Add(name))
                {
                    Log.Info("core", $"campaign: '{name}' lost its leader '{leader.Name}', which flies no net: it holds its course");
                }

                continue;
            }

            SeatOnNet(rig, pilot, net, trailers, minAiActiveDist);
            moved++;
            Log.Info("core", $"campaign: '{name}' lost its leader '{leader.Name}' and takes its net '{net.Name}#{net.Id}'");
        }

        return moved;
    }

    /// <summary>Seats a pilot on a patrol net: the assignment <c>SET_AI_NET</c> makes and the one a
    /// lost leader makes. The escort buffer goes with it, since a net outranks wingman mode, and a
    /// fresh walk seats itself at the node nearest wherever the aeroplane IS, so a mid-flight swap
    /// captures the new route instead of restarting it. ⚠ Re-baseline the machine on the vehicle's
    /// own ranges BEFORE the net's volumes: <see cref="CampaignRosterPlan.ApplyVolumes"/> skips a
    /// radius the net authors as zero, and a stale gate keeps a bomb-run escort in patrol.</summary>
    internal static void SeatOnNet(FlightController rig, AiPilot pilot, AiNet net,
        NetTrailerTargets? trailers, float minAiActiveDist)
    {
        pilot.Escort = null;
        pilot.Patrol = new AiNetFollower(net, Utils.Rng.NewSystemRandom(Utils.Rng.Ai),
            trailerTarget: trailers?.For(net));
        if (pilot.Machine is { } gates && rig.Stats is { } defs)
        {
            gates.AttackRange = defs.AiAttackRange;
            gates.ReturnRange = defs.AiReturnRange;
        }

        CampaignRosterPlan.ApplyVolumes(pilot.Machine, net.Volumes, minAiActiveDist);
    }

    /// <summary>The roster phase: spawns every non-player block of the mission's <c>aiv</c>
    /// roster, at the point of <c>GameSession</c>'s build where the human rigs exist. The plan is
    /// <see cref="CampaignRosterPlan"/>; this is the placement, the leader pass and the log.
    /// Returns the build-summary suffix, the shape <c>InstantActionDirector.BuildActors</c> has.</summary>
    internal string BuildRoster(RosterInputs inputs)
    {
        List<(string Name, List<object?> Fields)> blocks;
        List<AiNet> nets;
        VehicleDefs defs;
        try
        {
            blocks = AiSkills.LoadRoster(inputs.MissionZrdrPath);
            nets = AiNets.Load(inputs.ChapterZrdrPath);
            defs = VehicleDefs.Load(inputs.ZrdrPath);
        }
        catch (Exception e)
        {
            GD.PushWarning($"campaign: cannot read the roster ({e.Message}): no roster spawned");
            return "";
        }

        _chapterNets = nets;
        _netTrailers = inputs.NetTrailers;
        _minAiActiveDist = inputs.MinAiActiveDist;

        // wingman_4 flies the player's own aeroplane in the two missions the swap hands it over in,
        // and its own def's everywhere else.
        var handover = AirframeHandover.Resolves(_mission.ChapterFolder, _mission.MissionFolder)
            ? inputs.PlayerAirframe
            : null;
        var plan = CampaignRosterPlan.Build(blocks, defs, nets, WingmanNode, WingmanFit,
            netDraw: count => inputs.Rng.Next(count), handover: handover);
        foreach (var (name, why) in plan.Skipped)
        {
            if (!name.Equals(CampaignRosterPlan.PlayerBlock, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("core", $"campaign: roster '{name}' not spawned: {why}");
            }
        }

        if (_paths == null && inputs.FindNodes is { } findNodes)
        {
            _paths = new ScriptedPathVehicles(findNodes);
        }

        float minActive = inputs.MinAiActiveDist;
        foreach (var spawn in plan.Spawns)
        {
            // The block's own coordinates and yaw, as the original's roster spawn places. A world
            // node of the block's name is preferred where one exists, so a mission that animates
            // the aircraft into place agrees with the roster; most blocks have none.
            var pos = spawn.Position;
            var fwd = spawn.Forward;
            if (inputs.FindNodes?.Invoke(spawn.Name) is { Count: > 0 } nodes
                && nodes[0].IsInsideTree())
            {
                pos = nodes[0].GlobalPosition;
                fwd = -nodes[0].GlobalTransform.Basis.Z;
            }

            if (spawn.Surface)
            {
                PlaceSurface(spawn, pos, fwd, inputs);
                continue;
            }

            var pilot = AiPilot.HoldingCourse(pos, pos + fwd);
            if (spawn.Net is { } net)
            {
                pilot.Patrol = new AiNetFollower(net, Utils.Rng.NewSystemRandom(Utils.Rng.Ai),
                    trailerTarget: inputs.NetTrailers?.For(net));
            }
            var rig = inputs.Spawn(spawn, pos, pos + fwd, pilot);
            if (rig == null)
            {
                continue;
            }
            _roster[spawn.Name] = rig;
            _rosterPlans[spawn.Name] = spawn;
            _rosterPlacedPose[spawn.Name] = (pos, fwd);
            RegisterObjectiveMarker(spawn.Name, spawn);
            rig.Group = spawn.Group;
            rig.Downed += (_, killer) => CreditKill(spawn, killer);
            // The chapter's own copy of this vehicle is never placed, so anything it authors past
            // the shared airframe is grafted onto the rig here, while the rig is the plane the
            // block named and is already in the tree.
            inputs.AttachMarkers?.Invoke(spawn.Name, rig);

            CampaignRosterPlan.ApplyPlan(pilot, spawn, minActive);
            inputs.RegisterVoice(rig, spawn.AccentId, spawn.Skills.Talker, spawn.Skills.Constitution);

            bool placed = false;
            if (spawn.TaxiPath is { } taxi && _paths != null)
            {
                placed = PlaceOnPath(rig, spawn.Name, taxi);
                if (!placed)
                {
                    Log.Info("core", $"campaign: roster '{spawn.Name}' authors taxi path '{taxi}', which this world does not carry: it flies");
                }
            }

            // A downward probe against the built terrain collision, at the placed position: names
            // whether a roster spawn actually lands above ground (an under-ground spawn report is
            // settled off this line), sharing the World mask the flight model's ground-blow probe uses.
            string groundNote = new GodotWorldQuery(rig).Ray(pos + Vector3.Up * 3000f,
                pos - Vector3.Up * 3000f, CollisionLayers.World, null, out var groundHit)
                ? Log.Format($" terrain={groundHit.Position.Y:0} ({pos.Y - groundHit.Position.Y:+0;-0} above it)")
                : " terrain=(no hit)";

            Log.Info("core", $"campaign: roster '{spawn.Name}' ({spawn.Def} as {spawn.PlaneNode}, {spawn.Mode}) team={spawn.Team?.ToString() ?? "-"} group={spawn.Group} {(spawn.Net is { } n ? $"net='{n.Name}#{n.Id}'" : spawn.MissingNetId is { } missing ? $"net #{missing} MISSING from this chapter" : spawn.Escorts ? $"escorts '{spawn.LeaderName ?? "(no leader)"}'" : "no net")}{(spawn.Inert ? " DEACTIVATED" : "")}{(placed ? $" on path '{spawn.TaxiPath}'" : "")}{(spawn.Volumes.IsAuthored ? $" volumes act={spawn.Volumes.Activation.Radius:0} att={spawn.Volumes.Attack.Radius:0} ret={spawn.Volumes.Return.Radius:0}" : "")} spawn=({pos.X:0},{pos.Y:0},{pos.Z:0}){groundNote}");
        }

        // The leader pass, once every rig exists: primary_target names a block that may be
        // spawned after its follower (wingman_2 before devastator_2 in the shipped order).
        int escorts = 0;
        foreach (var (name, spawn) in _rosterPlans)
        {
            if (!spawn.Escorts || !_roster.TryGetValue(name, out var escort) || escort.Pilot is not { } pilot)
            {
                continue;
            }
            var leader = CampaignRosterPlan.ResolveLeader(spawn.LeaderName, _roster, inputs.Player());
            if (leader == null)
            {
                Log.Info("core", $"campaign: roster '{name}' escorts '{spawn.LeaderName ?? ""}', which is not spawned: it holds its course");
                continue;
            }
            pilot.Escort = new AiEscort { Leader = leader };
            escorts++;
        }

        if (_roster.Count == 0 && _vessels.Count == 0)
        {
            return "";
        }
        int inert = 0;
        foreach (var spawn in _rosterPlans.Values)
        {
            inert += spawn.Inert ? 1 : 0;
        }
        Log.Info("core", $"campaign: roster spawned {_roster.Count + _vessels.Count} of {blocks.Count} block(s): {escorts} escort(s), {inert} deactivated, {_paths?.Count ?? 0} on a path, {_vessels.Count} surface vehicle(s)");
        return $" + {_roster.Count} roster aircraft" + (_vessels.Count > 0 ? $" + {_vessels.Count} surface vehicle(s)" : "");
    }

    /// <summary>The world phase: binds the graph to the built world's runtimes. Called by
    /// <c>GameSession</c> once every runtime the objectives can touch is up.</summary>
    internal void Attach(WorldInputs inputs)
    {
        _world = new World(this, inputs);
        _beginSpectate = inputs.BeginSpectate;
        _projectiles = inputs.Projectiles;
        _playerPane = inputs.PlayerPane;
        _paths ??= inputs.Runtime is { } animRuntime
            ? new ScriptedPathVehicles(name => animRuntime.FindNodes(name))
            : null;
        _strings = inputs.Strings;
        Graph = new ObjectiveGraph(Script, _world);
        Graph.MissionEnded += OnMissionEnded;
        Graph.Transitioned += OnObjectiveTransition;
        Graph.TargetsChanged += ApplyHelpLabels;
        _dangerZones = inputs.Gamez is { } gamez
            ? CampaignDangerZones.Load(Script, gamez, _missionZrdrPath)
            : null;
        // The AI's ribbons are every dzpath of the world, not the mission's objective names: a
        // net node may send a pilot through a zone the script never scores.
        _ribbons = inputs.Gamez is { } ribbonGamez
            ? DangerZoneRibbons.Load(ribbonGamez, CampaignDangerZones.ReadDisabled(_missionZrdrPath))
            : null;
        foreach (var rig in _roster.Values)
        {
            if (rig.Pilot is { } pilot)
                pilot.DangerZones = _ribbons;
        }
        int chapter = _mission.Campaign;
        int applied = inputs.Runtime != null
            ? _profile.PersistLog.ApplyTo(inputs.Runtime, chapter, _carryThroughSeq)
            : 0;
        Log.Info("core", $"campaign: {Graph.Count} objective(s) armed, {Graph.Rows.Count} display row(s), {applied} object(s) restored from the chapter {chapter} persist log{(_dangerZones is { } dz ? $", {dz.Count} danger zone(s) armed" : "")}");
    }

    /// <summary>Takes the anim runtime's <c>CALLBACK</c> host slot, chaining to whatever held it.
    /// A launch definition places no aircraft itself: it flies its hook, raises one code, and the
    /// code is what puts the next aircraft of that block family into the air.</summary>
    internal void BindCallbackHost(AnimRuntime runtime)
    {
        _callbackHost ??= LaunchHookCallback;
        if (runtime.CallbackHost != _callbackHost)
        {
            _innerCallbackHost = runtime.CallbackHost;
            runtime.CallbackHost = _callbackHost;
        }
    }

    /// <summary>One sim step of the objectives graph, the scripted-path vehicles and the music
    /// channel's own battle detector. ⚠ Called from BOTH of <c>GameSession</c>'s drive paths, like
    /// the Instant Action sequencer: a realtime session never enters the stepped path. Under a
    /// cutscene hold nothing advances, which is callback 20's objectives half.</summary>
    internal void Step(float dt)
    {
        // ⚠ Before the cutscene hold, and before anything else: the ending that started the leaving
        // hold usually lands under a cutscene that is still running, and the hold has to run down
        // regardless of what is holding the world.
        if (_leaving > 0f)
        {
            _leaving -= dt;
            if (_leaving <= 0f)
            {
                Leave();
            }

            return;
        }

        WirePlayerDeath();
        WireScoredShooter();
        if (_cutsceneHold)
        {
            return;
        }

        StepPlayerLost();
        TakeLostLeadersNets(_roster, _netTrailers, _minAiActiveDist, _leaderlessReported);
        Graph?.Step(dt);
        if (_dangerZones != null && _world is { } world)
        {
            _dangerZones.Update(world.SnapshotHumans(), NotifyDangerZoneCompleted);
        }
        _paths?.Step(dt);
        StepMusic(dt);
    }

    /// <summary>The cutscene hold: while a cutscene owns the session the objectives update stops
    /// with the rest of the world, which is what callback 20 does in the original
    /// (docs/formats/anim-definitions/cutscenes.md). Dormancy timers and reminder fuses do not
    /// advance under the movie.</summary>
    internal void HoldForCutscene(bool held) => _cutsceneHold = held;

    /// <summary>A danger zone the player completed: routed into the graph's awake objectives, then
    /// into the scrapbook, where the zone's own objective number both records a mask bit and names
    /// the photograph the pilot's pane is written to.</summary>
    internal void NotifyDangerZoneCompleted(string zone)
    {
        Graph?.NotifyDangerZoneCompleted(zone);
        if (_dangerZones is not { } zones
            || !zones.TryZone(zone, out int objective, out bool snapshot)
            || objective < 0)
        {
            return;
        }

        _zonesCompleted.Add(objective);
        if (snapshot && ProfileDirectory() is { } directory)
        {
            CampaignSnapshot.Stage(directory, _mission.Ordinal, objective, _playerPane);
        }
    }

    /// <summary>What a <c>DEDG</c> over <paramref name="group"/> counts right now: the roster
    /// members of that group neither crashed nor deactivated. Null before <see cref="Attach"/> or
    /// with no roster spawned. Exposed so a suite can assert the count against the world it built
    /// rather than infer it from which objective fired.</summary>
    internal int? GroupLiveCount(int group) => _world?.GroupLiveCount(group, null);

    /// <summary>Books a generator launch into the roster under its launch name, carrying its
    /// template's group the way the original's spawn copies it onto the new vehicle. The
    /// <c>DEDG</c> walk, the <c>TRAVELERS</c> group form and the <c>SET_AI_*</c> lookups then see
    /// it. ⚠ C5/M04's Miles is group 5's only member: an unbooked launch reads the group as wiped
    /// out the moment he leaves the Dante, and that objective is the fuse on the instant loss.</summary>
    internal void RegisterGeneratorLaunch(string launchName, LaunchedVehicle launched,
        RosterSpawnPlan template)
    {
        if (launched.Aircraft is { } rig)
        {
            _roster[launchName] = rig;
            _rosterPlans[launchName] = template;
            rig.Group = template.Group;
            rig.Downed += (_, killer) => CreditKill(template, killer);
            // A launch takes a tagged net node's dzpath like any netted jet, so the ribbons the
            // world phase handed the roster must reach an aircraft that was not built yet
            // (docs/formats/mission-entities/enemy-generators.md).
            if (rig.Pilot is { } pilot)
                pilot.DangerZones = _ribbons;
        }
        else if (launched.Vessel is { } vessel)
        {
            _vessels[launchName] = vessel;
            _rosterPlans[launchName] = template;
        }
        else
        {
            return;
        }
        RegisterObjectiveMarker(launchName, template);
        Log.Info("core", $"campaign: launch '{launchName}' ({template.Name}) booked into the roster team={template.Team?.ToString() ?? "-"} group={template.Group}");
    }

    /// <summary>Makes every later <c>WARP_VEHICLE</c> wait for the host's draw instead of drawing
    /// from this machine's world stream. Set on a guest, whose aircraft the host flies.</summary>
    internal void TakeWarpsFromHost() => WarpsFromHost = true;

    /// <summary>The host's <c>WARP_VEHICLE</c> draw for the vehicle whose name hashes to
    /// <paramref name="vehicleKey"/> (<see cref="NetWorldLink.NameKey"/>). Placed at once when the
    /// directive already ran here, else held until it does.</summary>
    internal void TakeHostWarp(int vehicleKey, int index)
    {
        if (_warpsAwaitingPick.TryGetValue(vehicleKey, out var waiting) && waiting.Count > 0)
        {
            var (vehicle, points) = waiting.Dequeue();
            PlaceHostWarp(vehicle, points, index);
            return;
        }

        if (!_warpPicksAwaitingDirective.TryGetValue(vehicleKey, out var picks))
        {
            _warpPicksAwaitingDirective[vehicleKey] = picks = new Queue<int>();
        }

        picks.Enqueue(index);
    }

    private static CampaignMission? MissionFor(string zrdrPath, int seq)
    {
        foreach (var mission in CampaignSequence.Load(zrdrPath))
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }

    // The block family a launch-hook CALLBACK reactivates. The three codes and their families are
    // the original host's own literals, the way `cargozep1` is 800's
    // (docs/formats/anim-definitions/cutscenes.md). Any other code belongs further down the chain.
    private static string? LaunchHookFamily(int code) => code switch
    {
        801 => "bhatwarhawk",
        802 => "bhatbrigand",
        803 => "bhatgyro",
        _ => null,
    };

    // Every human's death, subscribed on the first step that finds that seat's aircraft: the flight
    // rigs are built after Attach has run, the same reason the music channel's damage ping waits.
    // Wired by identity per seat, so a 967 swap's new aeroplane is hooked as well.
    private void WirePlayerDeath()
    {
        var humans = _world?.HumanRigs();
        if (humans == null)
        {
            return;
        }

        for (int seat = 0; seat < humans.Count; seat++)
        {
            while (_deathWiredTo.Count <= seat)
            {
                _deathWiredTo.Add(null);
            }

            var human = humans[seat];
            if (ReferenceEquals(human, _deathWiredTo[seat]))
            {
                continue;
            }

            _deathWiredTo[seat] = human;
            int down = seat;
            human.Downed += (_, _) => OnPlayerDown(down, human);
            // Pinned beside the death wiring, and for its reason: a story mission is lost with the
            // aeroplane, so a respawn taken while flying would repair, restock and refuel for free.
            // A 967 swap's new aeroplane arrives here as a new identity and is pinned with it.
            human.AllowLiveRespawn = false;
        }
    }

    // The seated pilot is the only shooter Shots/Hits answers for.
    // ProjectilePool.ScoredShooters is the sole gate its two counters have. Registering just the
    // scripted player's aircraft here keeps a guest's cannon fire out of them, whatever the human
    // field's size.
    private void WireScoredShooter()
    {
        if (_world?.Player() is not { } player || ReferenceEquals(player, _scoredShooterWiredTo))
        {
            return;
        }

        _scoredShooterWiredTo = player;
        _projectiles?.ScoredShooters.Add(player.PlayerIndex);
    }

    // Losing the aircraft loses the mission, in the original's two stages: the death stops the
    // objectives, and the wreck reaching the ground reaches the debrief. With a human field it is
    // the LAST seat's death that stops them, and an earlier one only takes that human out of the
    // flight; the downed pilot does not respawn, and the survivors fly on. ⚠ Read the aircraft's own Downed
    // report, which is raised once per real death; the under-map backstop teleports without one,
    // so an altitude test here would end missions nobody lost (docs/verification.md INSTR-22).
    private void OnPlayerDown(int seat, FlightController human)
    {
        // --no-crash-loss is a debugging session flying on past a crash, which means the wreck is
        // NOT pinned and R still flies it again: taking the pane would leave that pilot blind.
        if (!EndsOnPlayerDeath || !_seatsDown.Add(seat))
        {
            return;
        }

        int seats = _world?.HumanRigs().Count ?? 1;
        if (_seatsDown.Count < seats)
        {
            _beginSpectate?.Invoke(human);
            Log.Info("core", $"campaign: seat {seat + 1} of {seats} is lost, spectating; {seats - _seatsDown.Count} human(s) still flying");
            return;
        }

        if (Graph is not { } graph || !graph.NotifyPlayerLost())
        {
            return;
        }

        _playerLost = true;
        Log.Info("core", $"campaign: the last of {seats} human aircraft is lost, the objectives stop, and the mission ends where the wreck does");
    }

    // The second stage: a hull that is still falling has not landed yet, which is the whole of the
    // delay between the kill and the debrief. With N humans that is N wrecks, and the wait is for
    // the last of them.
    private void StepPlayerLost()
    {
        if (!_playerLost || Graph is not { } graph)
        {
            return;
        }

        foreach (var human in _world!.HumanRigs())
        {
            if (human.WreckFalling)
            {
                return;
            }
        }

        _playerLost = false;
        graph.EndAfterPlayerLost();
    }

    // A path-driven aircraft: held (no flight integration) and re-pinned to the follower's pose
    // every tick, which is the original's exclusive movement-law switch; the handoff un-holds it
    // and re-activates it at the speed the path left it (docs/org/flightModel.md "The
    // scripted-path follower").
    private bool PlaceOnPath(FlightController rig, string name, string taxi)
    {
        bool placed = _paths!.Place(name, taxi, rig,
            onComplete: speed =>
            {
                rig.Held = false;
                var nose = rig.NoseDirection;
                rig.Activate(rig.WorldPosition, rig.WorldPosition + nose, nose * speed);
            },
            setPose: (p, heading) =>
            {
                var nose = new Basis(Vector3.Up, heading) * Vector3.Forward;
                rig.PlaceHeld(p, p + nose);
            });
        if (placed)
        {
            rig.Held = true;
        }
        return placed;
    }

    // A mode ship block: a hull on the water from the surface-vehicle runtime, on its authored
    // net from the spot the block authors. A stage with no such runtime reports it, as the
    // aircraft path reports a block it cannot spawn.
    private void PlaceSurface(RosterSpawnPlan spawn, Vector3 pos, Vector3 fwd, RosterInputs inputs)
    {
        if (inputs.SpawnSurface is not { } spawnSurface)
        {
            Log.Info("core", $"campaign: roster '{spawn.Name}' ({spawn.Def}, {spawn.Mode}) not spawned: no surface-vehicle runtime on this stage");
            return;
        }
        if (spawnSurface(spawn, pos, fwd) is not { } vessel)
        {
            return;
        }
        _vessels[spawn.Name] = vessel;
        _rosterPlans[spawn.Name] = spawn;
        RegisterObjectiveMarker(spawn.Name, spawn);
        if (spawn.Net is { } net)
        {
            vessel.Patrol(net);
        }
        Log.Info("core", $"campaign: roster '{spawn.Name}' ({spawn.Def} hull, {spawn.Mode}) team={spawn.Team?.ToString() ?? "-"} group={spawn.Group} {(spawn.Net is { } n ? $"net='{n.Name}#{n.Id}'" : spawn.MissingNetId is { } missing ? $"net #{missing} MISSING from this chapter" : "no net")}{(spawn.Inert ? " DEACTIVATED" : "")} spawn=({vessel.Position.X:0},{vessel.Position.Y:0.##},{vessel.Position.Z:0})");
    }

    // Books a spawned block's own objective-target flag and label under its roster name, the way
    // targets.zrd's own objective-flagged entries are already known by their target key.
    private void RegisterObjectiveMarker(string name, RosterSpawnPlan spawn)
    {
        if (spawn.ObjectiveTarget && spawn.HelpLabel != null)
        {
            _rosterObjectiveMarkers[name] = spawn.HelpLabel;
        }

        // A bay launch can arrive AFTER the script wrote its label: C5/M04 sets Miles's to Destroy
        // and only then credits the generator that builds him, and the launch is booked under the
        // very name the write used (block 'stihellhound_5_7' launches as 'stihellhound_5_eg0').
        ApplyHelpLabel(name);
    }

    // SET_HELP_LABEL over an aircraft that carries its own marker: the script's category outranks
    // the block's slot 39, and the aeroplane owns the resolved string, so the write lands on the
    // rig once here rather than being re-read against the graph on every frame of every pane.
    private void ApplyHelpLabels()
    {
        if (Graph is not { } graph)
        {
            return;
        }

        foreach (var key in graph.HelpLabels.Keys)
        {
            ApplyHelpLabel(key);
        }
    }

    private void ApplyHelpLabel(string name)
    {
        if (Graph is not { } graph
            || !graph.HelpLabels.TryGetValue(name, out var key)
            || !_roster.TryGetValue(name, out var rig)
            || !GodotObject.IsInstanceValid(rig)
            || !rig.ObjectiveTarget)
        {
            return;
        }

        // A blank write is how a mission clears a label, and an unresolved key printed verbatim is
        // right for a readout and wrong for a marker: both read as no label at all.
        string text = _strings == null || string.IsNullOrWhiteSpace(key) ? "" : _strings.Get(key).Trim();
        rig.ObjectiveCategory =
            text.Length == 0 || text.StartsWith("MSG_", StringComparison.Ordinal) ? null : text;
    }

    // The wingman's aircraft and fit off the profile, for the mission that has one. Resolved here
    // rather than by the launch shell because the profile is already open here; BuildRoster is
    // the consumer.
    private void BindWingman()
    {
        if (!_mission.Wingman)
        {
            return;
        }

        int at = _profile.WingmanPlane;
        if (at < 0 || at >= _profile.Planes.Count)
        {
            GD.PushWarning($"campaign: wingman plane index {at} is not one '{_profile.Name}' owns, no wingman fit bound");
            return;
        }

        var plane = _profile.Planes[at];
        WingmanNode = UI.PlanePickerRoster.AirframeNode(plane.Airframe);
        WingmanFit = CampaignLoadout.For(plane, StockLoadouts.Load());
        Log.Info("core", $"campaign: {WingmanName} flies '{plane.Name}' as {WingmanNode}, bound for the roster spawn");
    }

    // The music channel's battle detector: the decoded five-second proximity scan, which runs only
    // while battle music is silent, plus the player-damage ping. The player's aircraft is built
    // after Attach, so the damage hookup waits for the first step that finds one.
    private void StepMusic(float dt)
    {
        if (Music == null || _world == null)
        {
            return;
        }

        if (_world.Player() is { } player && !ReferenceEquals(player, _damageWiredTo))
        {
            _damageWiredTo = player;
            player.DamageApplied += (_, _) => Music?.NoteCombat();
        }

        if (Music.State == MusicState.Battle)
        {
            return;
        }

        _scanClock += dt;
        if (_scanClock < MusicPlayer.BattleScanSeconds)
        {
            return;
        }

        _scanClock = 0f;
        if (MusicPlayer.ScanPings(_world.NearbyVehicles()))
        {
            Music.NoteCombat();
        }
    }

    // The sortie log's objective lines: one per graph transition, through the file sink, so a
    // stalled chain can be read back from the log rather than re-flown.
    private void OnObjectiveTransition(ObjectiveTransition t)
    {
        string kind = t.Kind.ToString().ToLowerInvariant();
        string by = t.Source > 0 ? $" by {t.Source}" : "";
        string nap = t.Kind == ObjectiveTransitionKind.Napped ? Log.Format($" for {t.Seconds:0.#}s") : "";
        string gated = t.Gated ? " (held: TICK_DEPENDS_ON_OBJ dependency not awake)" : "";
        Log.Info("core", $"objective {t.Number} {kind}{by}{nap} at {t.Elapsed:0.0}s{gated}");
        if (t.Kind == ObjectiveTransitionKind.Completed)
        {
            RetireObjectiveMarkers(t.Number);
        }
    }

    // A completing objective's own REMOVE_OBJECTIVE_TARGET retires a roster block's marker the way
    // it retires a targets.zrd site: CM11's OBJECTIVE1 does this to both stunt planes when the
    // follow ends. The stamp lives on the aeroplane, so it is cleared once here rather than
    // re-tested against the script on every frame of every pane.
    private void RetireObjectiveMarkers(int number)
    {
        foreach (var def in Script.Objectives)
        {
            if (def.Number != number)
            {
                continue;
            }

            foreach (var target in def.RemoveObjectiveTarget)
            {
                _rosterObjectiveMarkers.Remove(target.Key);
                if (_roster.TryGetValue(target.Key, out var rig) && GodotObject.IsInstanceValid(rig))
                {
                    rig.ObjectiveTarget = false;
                }
            }
        }
    }

    // The single credit site, mirroring the original's one damage-resolver branch
    // (docs/org/debrief.md#what-the-tallies-count): the player did it, the victim is hostile to
    // the player, and the victim's airframe resolves against the eleven stock nodes. Everything
    // else (a wingman going down, a mutual kill between two enemies, a surface or turret target,
    // which never reaches this event) scores nowhere the debrief can see.
    private void CreditKill(RosterSpawnPlan victim, int? killer)
    {
        if (killer is not int shooter || _world?.Player() is not { } player || shooter != player.PlayerIndex)
        {
            return;
        }

        if (victim.Team is not int side || !AimAssist.Hostile(AimAssist.PlayerTeam, side))
        {
            return;
        }

        if (UI.PlanePickerRoster.AirframeOf(victim.PlaneNode) is not { } airframe)
        {
            return;
        }

        if (victim.Ace)
        {
            _aceKills[airframe]++;
        }
        else
        {
            _kills[airframe]++;
        }
    }

    private void OnMissionEnded(MissionOutcome outcome)
    {
        var graph = Graph!;
        var plane = _profile.SelectedPlane >= 0 && _profile.SelectedPlane < _profile.Planes.Count
            ? _profile.Planes[_profile.SelectedPlane]
            : null;
        // No money is passed: what the mission pays is the reward table's, gated on what this
        // profile already banked, so CampaignProgression works it out (docs/org/debrief.md).
        // ⚠ Shots/Hits stay the seated pilot's alone (WireScoredShooter): a guest's never count.
        int objectives = graph.CompletedMask | DangerZoneMask();
        var attempt = new MissionAttempt(
            _mission.Seq,
            outcome == MissionOutcome.Won
                ? objectives | CampaignProgression.PrimaryObjectiveMask
                : objectives & ~CampaignProgression.PrimaryObjectiveMask,
            (int)(graph.Elapsed * 1000f),
            _world?.Shots ?? 0,
            _world?.Hits ?? 0,
            plane?.Airframe ?? 0,
            plane?.Name ?? string.Empty,
            (int[])_kills.Clone(),
            (int[])_aceKills.Clone());
        // ⚠ A replicated mission is another machine's attempt, and that machine records it.
        // Nothing below the hold may write a profile, a photograph or an award.
        if (graph.Replicated)
        {
            Result = new CampaignMissionResult(outcome, attempt,
                new MissionRecorded(false, false, 0, Array.Empty<int>(), Array.Empty<CustomPlaneDef>()),
                _mission.Campaign);
            _leaving = LeavingHoldS;
            Log.Info("core", $"campaign: mission {_mission.Ordinal} {outcome} as the host ended it, recorded on the host's profile alone; holding the world {LeavingHoldS:0.#}s before leaving it");
            return;
        }

        if (_world?.Runtime is { } runtime && CampaignPersistLog.CommitsOn(outcome))
        {
            _profile.PersistLog.Merge(_mission.Campaign, _mission.Seq, CampaignPersistLog.Capture(runtime));
        }

        var recorded = CampaignProgression.Record(_profile, attempt);
        // The skip offer's Yes is a synthetic win whose save gate writes what the failed attempt
        // left (docs/org/debrief.md), and that state exists only while the world is up. Captured
        // here and carried; nothing commits it unless the offer is taken.
        var skipCapture = recorded.SkipOffered && _world?.Runtime is { } lostWorld
            ? CampaignPersistLog.Capture(lostWorld)
            : null;
        _store?.Save(_profile);
        CommitSnapshots(outcome == MissionOutcome.Won);
        SaveAwardedBuilds(recorded);
        Result = new CampaignMissionResult(
            outcome, attempt, recorded, _mission.Campaign, skipCapture);
        _leaving = LeavingHoldS;
        Log.Info("core", $"campaign: mission {_mission.Ordinal} {outcome}, mask 0x{attempt.CompletedMask:x}, {attempt.TimeMs / 1000}s, primary={recorded.PrimaryCompleted}, advanced={recorded.Advanced}, log {_profile.PersistLog.Count} object(s), attempt {CampaignProgression.ResultOf(_profile, _mission.Seq)?.Attempts ?? 0} (skip offered={recorded.SkipOffered}); holding the world {LeavingHoldS:0.#}s before leaving it");
    }

    // The second source of the completed-objective mask: a bit per completed danger zone, by the
    // objective number dzones.zrd gives that zone. ⚠ The band stops at 30, not at 31: the original
    // reads ids 18 through 30 only, so the zone C4/M04 and C5 number 31 scores and photographs but
    // never lights its scrapbook row (docs/org/debrief.md).
    private int DangerZoneMask()
    {
        int mask = 0;
        foreach (int objective in _zonesCompleted)
        {
            if (objective is >= 18 and <= 30)
            {
                mask |= 1 << objective;
            }
        }

        return mask;
    }

    // Mission end: the staged photographs are kept under their scrapbook names on a win and dropped
    // on a loss, the original's own sweep over the profile directory.
    private void CommitSnapshots(bool won)
    {
        if (ProfileDirectory() is not { } directory)
        {
            return;
        }

        int acted = CampaignSnapshot.Commit(directory, _mission.Ordinal, won);
        if (acted > 0)
        {
            Log.Info("core", $"danger zone snapshots: {acted} staged photograph(s) {(won ? "kept" : "dropped")}");
        }
    }

    // The seated profile's own directory, where the scrapbook resolves a Snap_ capture. Null with
    // no store bound, which is every suite that builds a director without one.
    private string? ProfileDirectory() =>
        _store is { } store && _profile.Name.Length > 0 ? store.DirFor(_profile.Name) : null;

    // The far end of the leaving hold: the world has stood still for its length and the session may
    // go. The original reaches here when its fade over the last flown frame has run out and the next
    // screen takes the state machine.
    private void Leave()
    {
        _leaving = 0f;
        ReturnToCabin = true;
        if (Result is { } result)
        {
            MissionEnded?.Invoke(result);
        }
    }

    // An award is a whole aircraft in the original, not just an ownership row: its template record
    // carries the guns, hardpoints, armour, paint and the engine tier that decides the nitrous
    // injector. The cabin looks a plane's fit up in the build store by name at launch, so the grant
    // has to land there as well as in the profile, or the aircraft flies as its stock airframe.
    private void SaveAwardedBuilds(MissionRecorded recorded)
    {
        if (recorded.AwardedBuilds.Count == 0)
        {
            return;
        }

        var planes = CustomPlaneStore.UserPlanes();
        foreach (var build in recorded.AwardedBuilds)
        {
            // A granted aircraft is the campaign's, so it waits for EXPORT like a campaign-built one.
            build.AwaitingExport = true;
            Log.Info("core", $"campaign: award '{build.Name}' saved to {planes.Save(build)}");
        }
    }

    // This director's place in the CALLBACK host chain. Owning a code means answering it here even
    // when the family is spent, or the declined code would fall through to a seam that never meant
    // it.
    private bool LaunchHookCallback(int code, string? animName, string? rootName)
    {
        if (code == CodeRemoveWingman)
        {
            RemoveNamedWingman();
            return true;
        }

        if (LaunchHookFamily(code) is not { } family)
        {
            return _innerCallbackHost?.Invoke(code, animName, rootName) ?? false;
        }

        if (FirstDormantOf(family) is { } launched && ActivateDormantRoster(launched))
        {
            Log.Info("core", $"campaign: CALLBACK {code} launched '{launched}' off the hook");
        }
        else
        {
            Log.Info("core", $"campaign: CALLBACK {code} has no deactivated '{family}_n' left to launch");
        }

        return true;
    }

    // Callback 968's whole body: the named wingman leaves the world unless she is already out of it,
    // the test the original makes on her own out-of-flight byte before hiding her. The hide is the
    // deactivate, not the cutscene park, so an objective walk stops counting her from here.
    private void RemoveNamedWingman()
    {
        if (Commanded(RemovedWingman) is not { InPlay: true } rig)
        {
            Log.Info("core", $"campaign: CALLBACK {CodeRemoveWingman} leaves '{RemovedWingman}' alone: not in play");
            return;
        }

        rig.Inert = true;
        Log.Info("core", $"campaign: CALLBACK {CodeRemoveWingman} took '{RemovedWingman}' out of the world");
    }

    // The lowest-numbered member of the family still deactivated, so a hook called six times
    // launches _1 through _6 in order. Read off the block's own suffix rather than off the spawn
    // dictionary, whose enumeration order is not part of its contract.
    private string? FirstDormantOf(string family)
    {
        string? first = null;
        int lowest = int.MaxValue;
        foreach (var name in _rosterPlans.Keys)
        {
            if (name.Length <= family.Length + 1
                || !name.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                || name[family.Length] != '_'
                || !int.TryParse(name[(family.Length + 1)..], out int ordinal)
                || ordinal >= lowest
                || !_roster.TryGetValue(name, out var rig)
                || !rig.Inert)
            {
                continue;
            }

            lowest = ordinal;
            first = name;
        }

        return first;
    }

    // The one un-dormanting of a roster aircraft, shared by WAKEUP_ENEMIES and the launch hook: a
    // deactivated block comes back where it now stands rather than at its authored plan pose, so a
    // script that moved it keeps the move. False when the name is no dormant roster block.
    private bool ActivateDormantRoster(string name)
    {
        if (!_roster.TryGetValue(name, out var rig) || !_rosterPlans.TryGetValue(name, out var plan)
            || !rig.Inert)
        {
            return false;
        }

        var (pos, fwd) = _rosterPlacedPose.TryGetValue(name, out var placed)
            ? placed
            : (plan.Position, plan.Forward);
        rig.Activate(pos, pos + fwd);
        return true;
    }

    // A directive whose consumer this session has no seam for. Logged once per kind so a mission
    // that fires it every reminder loop does not flood the sink.
    private void Gap(string directive, string detail)
    {
        if (_gapsLogged.Add(directive))
        {
            Log.Info("core", $"campaign: {directive} has no consumer in this session yet ({detail})");
        }
    }

    // The rig a mission clause's vehicle name addresses, or null when no roster block of that name
    // was spawned. The three SET_AI_* directives are one lookup with three different writes, so
    // they share this; a miss is reported by the caller rather than swallowed, because a mission
    // naming an aircraft that is not there is a real signal.
    private FlightController? Commanded(string name) =>
        _roster.TryGetValue(name, out var rig) ? rig : null;

    // The directive's half on a guest: placed at once when the host's draw is already here.
    private void AwaitHostWarp(string vehicle, IReadOnlyList<WarpPoint> points)
    {
        int key = NetWorldLink.NameKey(vehicle);
        if (_warpPicksAwaitingDirective.TryGetValue(key, out var picks) && picks.Count > 0)
        {
            PlaceHostWarp(vehicle, points, picks.Dequeue());
            return;
        }

        if (!_warpsAwaitingPick.TryGetValue(key, out var waiting))
        {
            _warpsAwaitingPick[key] = waiting = new Queue<(string, IReadOnlyList<WarpPoint>)>();
        }

        waiting.Enqueue((vehicle, points));
        Log.Info("core", $"campaign: WARP_VEHICLE '{vehicle}' waits for the host's draw");
    }

    private void PlaceHostWarp(string vehicle, IReadOnlyList<WarpPoint> points, int index)
    {
        if (index < 0 || index >= points.Count || Commanded(vehicle) is not { } rig)
        {
            Gap("WARP_VEHICLE", $"the host's draw {index} for '{vehicle}' names no waypoint or vehicle here");
            return;
        }

        Log.Info("core", $"campaign: WARP_VEHICLE '{vehicle}' takes the host's waypoint {index + 1} of {points.Count}");
        PlaceWarp(rig, vehicle, points[index], points.Count);
    }

    // One drawn waypoint. A plain entry teleports the vehicle (FUN_00493fb0); an entry naming a
    // path this chapter has puts it on that path and releases it moving (FUN_004940d0).
    private void PlaceWarp(FlightController rig, string vehicle, WarpPoint point, int count)
    {
        if (point.PointName is { Length: > 0 } path && _paths != null && PlaceOnPath(rig, vehicle, path))
        {
            _paths.Release(vehicle);
            Log.Info("core", $"campaign: WARP_VEHICLE put '{vehicle}' on path '{path}', moving");
            return;
        }

        // A named point whose path this chapter lacks is the original's own no-op branch. It
        // leaves the vehicle where it stands and still writes the velocity below.
        bool authored = point.PointName is not { Length: > 0 };
        var at = authored ? new Vector3(point.X, point.Y, point.Z) : rig.WorldPosition;
        float heading = authored ? point.Heading : Mathf.RadToDeg(rig.GlobalRotation.Y);
        // The speed it flies out at, along the placed nose: the original takes
        // min(plane_speed_max, fd_speed). CSVM models no plane_speed_max
        // (docs/org/flightModel.md, "What this changes" #13), so fd_speed stands in alone.
        rig.WarpTo(at, heading, rig.Stats?.FdSpeed ?? 0f);
        Log.Info("core", $"campaign: WARP_VEHICLE moved '{vehicle}' to ({at.X:0},{at.Y:0},{at.Z:0}) heading {heading:0} deg, 1 of {count} waypoint(s)");
    }

    // The hull a clause names: a roster block's, or a generator launch's through the runtime,
    // since a boat generator's launches are what C2/M01's SET_AI_NET clauses address.
    private SurfaceVehicle? CommandedVessel(string name) =>
        _vessels.TryGetValue(name, out var vessel) ? vessel : _world?.SurfaceVehicles?.ByName(name);

    /// <summary>What <see cref="BuildRoster"/> reads from the session's build: the data paths,
    /// the first human, the net trailer resolver, the world's node lookup, and the two delegates
    /// <c>GameSession</c> keeps private behaviour behind (the spawner and the voice
    /// registration), the shape <c>InstantActionDirector.ActorBuildInputs</c> has.</summary>
    internal sealed class RosterInputs
    {
        public string ChapterZrdrPath = "";
        public string MissionZrdrPath = "";
        public string ZrdrPath = "";

        /// <summary>player.json's <c>min_ai_active_dist</c>, the floor under every activation
        /// radius.</summary>
        public float MinAiActiveDist = 2000f;

        /// <summary>What player 1 is flying, for the airframe hand-over's own roster block. Null
        /// leaves that block on its own def, which is what every other mission wants.</summary>
        public FlyingAirframe? PlayerAirframe;

        /// <summary>What <c>ResolveLeader</c>'s <c>player</c> means. ⚠ The SCRIPTED PLAYER, never
        /// the human field: an escorting block follows one aeroplane, and a leader chosen from
        /// whichever human is handiest would hand the wing a different lead every mission.</summary>
        public Func<FlightController?> Player = () => null;

        /// <summary>⚠ Anchored on the scripted player for the same reason as
        /// <see cref="Player"/>: a net's trailer target is ONE aircraft the graph is drawn behind,
        /// and there is no field-wide answer to what it should trail.</summary>
        public NetTrailerTargets? NetTrailers;
        public Func<string, IReadOnlyList<Node3D>>? FindNodes;
        public SpawnRosterAircraft Spawn = null!;

        /// <summary>Builds a <c>mode ship</c> block's hull at a spot facing a direction
        /// (<c>Session/SurfaceVehicleRuntime.cs</c>). Null on a stage with no chapter world, which
        /// reports the block rather than spawning an aircraft in its place.</summary>
        public Func<RosterSpawnPlan, Vector3, Vector3, SurfaceVehicle?>? SpawnSurface;

        /// <summary>Grafts the block's authored marker scaffolding onto the rig it just spawned
        /// (<c>Mech3/RosterMarkers.cs</c>). Null in a build with no world runtime, which leaves a
        /// chapter's additions to a vehicle unreachable exactly as they were before.</summary>
        public Action<string, Node3D>? AttachMarkers;

        public Action<FlightController?, int?, int?, int?> RegisterVoice = (_, _, _, _) => { };
        public Random Rng = new();
    }

    /// <summary>The built world's runtimes, handed over as one value the way
    /// <c>InstantActionDirector</c>'s build inputs are. Every reference may be null: a mission
    /// built without one simply leaves the directives that need it unconsumed.</summary>
    internal sealed class WorldInputs
    {
        public AnimRuntime? Runtime;
        public TurretEmplacementRuntime? Turrets;
        public AiGeneratorRuntime? Generators;
        public ZeppelinRuntime? Zeppelins;
        public SurfaceVehicleRuntime? SurfaceVehicles;
        public WorldSounds? Sounds;
        public ProjectilePool? Projectiles;
        public Func<Vector3>? ListenerPosition;

        /// <summary>The chapter's parsed world geometry, for <see cref="CampaignDangerZones"/>'s
        /// gate read. Null leaves the mission's <c>DANGER_ZONES_COMPLETED</c> conditions
        /// unarmed, the same "no seam yet" shape as every other null here.</summary>
        public GameZ? Gamez;

        /// <summary>The string table a <c>SET_HELP_LABEL</c> write is resolved through before it
        /// reaches a marker-carrying aircraft's own label. Null leaves a written label unresolved
        /// and so unprinted, the same "no seam yet" shape as every other null here.</summary>
        public Messages? Strings;

        /// <summary>The SCRIPTED PLAYER's aircraft once one exists, for the music channel's damage
        /// ping and the lost ending. One aeroplane, always P1's. A delegate rather than a value
        /// because the flight rigs are built after the world.</summary>
        public Func<FlightController?>? PlayerAircraft;

        /// <summary>The scripted player's pane, requested on the crossing frame, for the Danger Zone
        /// photograph. Null leaves a completed zone scored but unphotographed, which is what every
        /// suite without a viewport gets.</summary>
        public PaneRequest? PlayerPane;

        /// <summary>The HUMAN FIELD: every joined player's aircraft, read fresh. Left unset by a
        /// solo sortie and by every suite that builds one rig, where the scripted player is the
        /// whole field and <see cref="PlayerAircraft"/> answers for it.</summary>
        public Func<IReadOnlyList<FlightController>>? Humans;

        /// <summary>Every aircraft in the session, read fresh: the music channel's proximity scan
        /// counts the ones near the player.</summary>
        public Func<IReadOnlyList<FlightController>>? Aircraft;

        /// <summary>Hand this human's pane to a spectator camera: it is out of the mission, but the
        /// mission is not over. The director decides WHEN and never builds the camera itself, which
        /// is the same "this class adds no node" rule the rest of it keeps. Unset by a session with
        /// no rigs to hand over, which leaves a death with no camera to move and nothing else.</summary>
        public Action<FlightController>? BeginSpectate;

        public Random Rng = new();
    }

    // The engine side of IObjectiveWorld: the conditions the graph cannot answer itself, and the
    // world-touching actions. Every directive with no seam in this session is a NAMED no-op, never
    // an invented behaviour (docs/formats/objectives.md is the contract).
    private sealed class World : IObjectiveWorld
    {
        private readonly CampaignDirector _owner;
        private readonly WorldInputs _in;

        // Refilled by SnapshotHumans and handed straight to CampaignHumanField, never held: the
        // reads that use it are one graph step apart, so one buffer serves all of them.
        private readonly List<HumanState> _field = new();

        // HumanRigs' fallback field, the scripted player alone, for a session that names none.
        private readonly List<FlightController> _soloField = new();

        public World(CampaignDirector owner, WorldInputs inputs)
        {
            _owner = owner;
            _in = inputs;
        }

        public AnimRuntime? Runtime => _in.Runtime;

        public SurfaceVehicleRuntime? SurfaceVehicles => _in.SurfaceVehicles;

        public int Shots => _in.Projectiles?.CannonRoundsFired ?? 0;

        public int Hits => _in.Projectiles?.CannonHits ?? 0;

        public bool? NodeInactive(IReadOnlyList<string> path)
        {
            var node = Resolve(path);
            return node == null ? null : !node.Visible;
        }

        public int AnimState(string anim) => _in.Runtime?.AnimStateOf(anim) ?? 0;

        public int? GroupLiveCount(int group, string? generator)
        {
            // Null while no roster is spawned keeps every DEDG false rather than reading an empty
            // world as "the group is wiped out", which would win a mission on its first tick.
            // Deactivated, never Inert: a cutscene-parked member still counts (the DEDG row).
            if (_owner._roster.Count == 0 && _owner._vessels.Count == 0)
            {
                _owner.Gap("DEDG", $"group {group} has no spawned aiv roster to count");
                return null;
            }
            int alive = 0;
            foreach (var (name, plan) in _owner._rosterPlans)
            {
                if (plan.Group != group)
                {
                    continue;
                }
                if (_owner._roster.TryGetValue(name, out var rig) && !rig.Crashed && !rig.Deactivated)
                {
                    alive++;
                }
                else if (_owner._vessels.TryGetValue(name, out var vessel) && !vessel.IsDestroyed && !vessel.Inert)
                {
                    alive++;
                }
            }

            // The human rig after a 967 capture carries the captured aircraft's group and stands
            // in for it, or CM02's wiped-out DEDG would nap the instant loss. Every human, not the
            // scripted player alone: a guest can be the one holding the captured aeroplane.
            alive += CampaignHumanField.LiveInGroup(SnapshotHumans(), group);
            if (generator != null)
            {
                _owner.Gap("DEDG", $"the generator form ('{generator}') adds no remaining capacity yet");
            }
            return alive;
        }

        public void WidenGroupEngagement(int group)
        {
            // The same liveness test the count uses, so the widened set is exactly the counted one.
            // A hull carries no activation volume in this engine, so only the aircraft are reached.
            foreach (var (name, plan) in _owner._rosterPlans)
            {
                if (plan.Group != group)
                {
                    continue;
                }
                if (_owner._roster.TryGetValue(name, out var rig) && !rig.Crashed && !rig.Deactivated)
                {
                    CampaignRosterPlan.WidenForDedg(rig.Pilot?.Machine);
                }
            }
        }

        public bool? TravelersMet(TravelersSpec spec)
        {
            Vector3? reference = spec.WherePoint is { } p
                ? new Vector3(p[0], p[1], p[2])
                : Where(spec.WhereNode ?? string.Empty);
            if (reference == null)
            {
                return null;
            }

            // The group form: count the named aiv roster group's non-deactivated members (a parked
            // one counts) inside the radius, the roster walk GroupLiveCount uses for DEDG. Null
            // (not yet decidable) while no roster is spawned, so an empty world never wins early.
            if (spec.Group is { } group)
            {
                if (_owner._rosterPlans.Count == 0)
                {
                    _owner.Gap("TRAVELERS", $"group {group} has no spawned aiv roster to count");
                    return null;
                }

                int matching = 0;
                foreach (var (name, plan) in _owner._rosterPlans)
                {
                    if (plan.Group != group)
                    {
                        continue;
                    }
                    Vector3 where;
                    if (_owner._roster.TryGetValue(name, out var rig) && !rig.Deactivated)
                    {
                        where = rig.WorldPosition;
                    }
                    else if (_owner._vessels.TryGetValue(name, out var vessel) && !vessel.Inert)
                    {
                        where = vessel.Position;
                    }
                    else
                    {
                        continue;
                    }

                    bool memberInside = where.DistanceSquaredTo(reference.Value) <= spec.Radius * spec.Radius;
                    if (memberInside == spec.Approaching)
                    {
                        matching++;
                    }
                }

                return matching >= spec.Count;
            }

            if (_in.ListenerPosition == null)
            {
                return null;
            }

            if (!string.Equals(spec.Who, "player", StringComparison.OrdinalIgnoreCase))
            {
                if (Where(spec.Who) is not { } who)
                {
                    return null;
                }

                bool at = who.DistanceSquaredTo(reference.Value) <= spec.Radius * spec.Radius;
                return spec.Approaching ? at : !at;
            }

            // The authored `player` subject is the whole human field, nearest first.
            // A field of none falls back to the listener, which is where this read has always been
            // and is what a suite that builds no rig still answers with.
            var field = SnapshotHumans();
            return field.Count > 0
                ? CampaignHumanField.Travelers(field, reference.Value, spec.Radius, spec.Approaching)
                : CampaignHumanField.Travelers(
                    new[] { new HumanState(_in.ListenerPosition(), null, false) },
                    reference.Value, spec.Radius, spec.Approaching);
        }

        public void WakeupEnemies(IReadOnlyList<string> names)
        {
            // The partner of BOTH deactivated flags (docs/formats/objectives.md): the roster's,
            // which puts an inert aircraft back in play at its placed pose, and a zeppelin record's,
            // which puts a hidden airship into the world. A name is one or the other, never both.
            int aircraft = 0, zeppelins = 0, vessels = 0;
            foreach (var name in names)
            {
                if (_owner.ActivateDormantRoster(name))
                {
                    aircraft++;
                }
                else if (_owner._vessels.TryGetValue(name, out var vessel) && vessel.Wake())
                {
                    vessels++;
                }
                else if (_in.Zeppelins?.Wake(name) == true)
                {
                    zeppelins++;
                }
            }
            if (aircraft + zeppelins + vessels > 0)
            {
                Log.Info("core", $"campaign: WAKEUP_ENEMIES activated {aircraft} of {names.Count} named aircraft, {vessels} surface vehicle(s), and woke {zeppelins} zeppelin(s)");
            }
            else if (names.Count > 0)
            {
                _owner.Gap("WAKEUP_ENEMIES", $"'{names[0]}' and {names.Count - 1} more name no deactivated roster aircraft, surface vehicle or zeppelin");
            }
        }

        public void WakeupTurrets(IReadOnlyList<string> patterns)
        {
            if (_in.Turrets == null || _in.Runtime == null)
            {
                return;
            }

            int armed = 0;
            foreach (var pattern in patterns)
            {
                foreach (var node in _in.Runtime.FindNodes(pattern))
                {
                    armed += _in.Turrets.SetActivatedUnder(node, true);
                }
            }

            // Logged even when it arms nothing: silence on armed=0 makes a script that matched no
            // node indistinguishable from no script at all.
            Log.Info("flight", $"campaign: WAKEUP_TURRETS armed {armed} emplacement(s) from {patterns.Count} pattern(s); {_in.Turrets.AwakeCount} awake and {_in.Turrets.AliveCount} alive of {_in.Turrets.Count}");
        }

        public void WakeupZepTurrets(IReadOnlyList<string> nodes) => WakeupTurrets(nodes);

        public void WakeupGenerator(string name, int count)
        {
            int granted = _in.Generators?.GrantWaveCapacity(name, count) ?? 0;
            Log.Info("core", $"campaign: WAKEUP_GENERATOR '{name}' +{count} (granted {granted})");
        }

        public void WakeAnim(string anim, string? node)
        {
            var anchor = node != null ? Resolve(new[] { node }) : null;
            // A mission trigger, not a plain Play: the definition's immediate closure may stage a
            // library root, which only a trigger call is allowed to build (CM11's activate_pickup
            // parents the dock's approach cone under the trailer's sensor this way).
            int started = _in.Runtime?.PlayMissionTrigger(anim, anchor).Count ?? 0;
            Log.Info("core", $"campaign: WAKE_ANIM '{anim}' started {started} definition(s)");
        }

        /// <summary>The SCRIPTED PLAYER: the one aeroplane an authored <c>player</c> token means,
        /// always P1's, and the aircraft whose loss ends the mission. ⚠ Not the human field. A
        /// condition that asks "has a human done this" reads <see cref="SnapshotHumans"/> instead,
        /// and the two answers differ the moment a guest joins.</summary>
        public FlightController? Player() => _in.PlayerAircraft?.Invoke();

        /// <summary>The human field as the RIGS themselves, for the two seams that must hold an
        /// aeroplane rather than a reading of one: the per-seat death wiring and the wreck wait.
        /// Same fallback as <see cref="SnapshotHumans"/>: a session that names no field exposes the
        /// scripted player as its sole human rig.</summary>
        public IReadOnlyList<FlightController> HumanRigs()
        {
            if (_in.Humans?.Invoke() is { Count: > 0 } humans)
            {
                return humans;
            }

            _soloField.Clear();
            if (Player() is { } player)
            {
                _soloField.Add(player);
            }

            return _soloField;
        }

        /// <summary>The HUMAN FIELD as the conditions read it, refilled into one reused buffer. A
        /// session that names no field is one where the scripted player IS the field, which keeps
        /// the co-op read and the solo read on one code path.</summary>
        public IReadOnlyList<HumanState> SnapshotHumans()
        {
            _field.Clear();
            if (_in.Humans?.Invoke() is { Count: > 0 } humans)
            {
                foreach (var human in humans)
                {
                    _field.Add(new HumanState(human.WorldPosition, human.Group, human.Crashed));
                }
            }
            else if (Player() is { } player)
            {
                _field.Add(new HumanState(player.WorldPosition, player.Group, player.Crashed));
            }

            return _field;
        }

        /// <summary>How many other live aircraft sit inside the decoded scan radius of the player.
        /// Wrecks are excluded; whether the original's own skip predicate excludes more than that
        /// is a named gap in <c>docs/org/music.md</c>.</summary>
        public int NearbyVehicles()
        {
            if (Player() is not { } player || _in.Aircraft == null)
            {
                return 0;
            }

            int near = 0;
            float radius = MusicPlayer.BattleScanRadiusM;
            foreach (var craft in _in.Aircraft())
            {
                if (!ReferenceEquals(craft, player) && !craft.Destroyed
                    && craft.WorldPosition.DistanceSquaredTo(player.WorldPosition) <= radius * radius)
                {
                    near++;
                }
            }

            return near;
        }

        public void PlaySoundGroup(string group)
        {
            // The music channel is a routing flag on the name, not a subsystem the data asks for:
            // a mu* group goes to the one streaming channel and never to a positional emitter
            // (docs/org/music.md, FUN_00593590).
            if (group.StartsWith("mu", StringComparison.OrdinalIgnoreCase))
            {
                _owner.Music?.Cue(group, _in.Rng);
                return;
            }

            if (_in.Sounds == null || _in.ListenerPosition == null)
            {
                return;
            }

            // A callout's definition carries QUEUE and never 3D, so it belongs on the radio queue
            // and not at a point in the world (docs/formats/sounds.md). A cue the radio does not
            // own returns 0 and falls through to the positional path that does.
            if (_in.Sounds.Radio is { } radio && radio.Cue(group, _in.Rng) > 0)
            {
                return;
            }

            _in.Sounds.PlayOneShot(group, _in.ListenerPosition(), _in.Rng);
        }

        public void StopQueuedSounds(IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            if (_in.Sounds?.Radio is { } radio)
            {
                radio.Cancel(names);
                return;
            }

            _owner.Gap("STOP_QUEUED_SOUNDS", "this session built no mission radio queue");
        }

        /// <summary>`WARP_VEHICLE` (<c>FUN_0046a490</c> at <c>0x0046a8f2</c>): ONE waypoint drawn
        /// from the list at random, placed by <see cref="PlaceWarp"/>. The forward velocity is the
        /// caller's, written only for a vehicle that did NOT end up path-driven. ⚠ A guest never
        /// draws: its stream stands elsewhere than the host's, so it waits for the host's pick
        /// (<see cref="WarpsFromHost"/>).</summary>
        public void WarpVehicle(string vehicle, IReadOnlyList<WarpPoint> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            if (_owner.Commanded(vehicle) is not { } rig)
            {
                _owner.Gap("WARP_VEHICLE", $"'{vehicle}' is not a spawned mission vehicle");
                return;
            }

            if (_owner.WarpsFromHost)
            {
                _owner.AwaitHostWarp(vehicle, points);
                return;
            }

            // The goal runtime's own `rand() % count`, on the world phase's stream. It is
            // uniform over the authored list, so a mission hiding one aircraft in four places
            // hides it evenly.
            int index = _in.Rng.Next(points.Count);
            _owner.WarpDrawn?.Invoke(vehicle, index);
            _owner.PlaceWarp(rig, vehicle, points[index], points.Count);
        }

        /// <summary>The vehicle arm of <c>SET_AI_TEAM</c> (<c>FUN_00469e20</c>): the script's raw
        /// integer becomes the vehicle's team id with no conversion, in the one space the roster
        /// block's own <c>team</c> slot writes, and the setter drops the current target with it
        /// (docs/org/targeting.md).</summary>
        public void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries)
        {
            int set = 0;
            var unmatched = new List<string>();
            foreach (var (name, team) in entries)
            {
                if (_owner.Commanded(name) is not { } rig)
                {
                    if (_owner.CommandedVessel(name) is { } vessel)
                    {
                        vessel.Team = team;
                        set++;
                        continue;
                    }
                    // An airship takes the same clause, and its own arm fans the value across the
                    // whole hull because the record's team reaches every part of it.
                    if (_in.Zeppelins?.SetTeam(name, team) == true)
                    {
                        set++;
                        continue;
                    }
                    unmatched.Add(name);
                    continue;
                }

                rig.Team = team;
                if (rig.Pilot?.Gunner is { } gunner)
                {
                    gunner.Target = null;
                }
                set++;
            }
            Report("SET_AI_TEAM", set, entries.Count, unmatched);
        }

        /// <summary>Moves each named aircraft onto the named patrol net, the vehicle arm of
        /// <c>SET_AI_NET</c> (<c>FUN_00475f30</c> into <c>FUN_00475fc0</c>, docs/org/aiPilot.md
        /// "Net assignment"). A chapter that carries no net of that name leaves the aircraft on
        /// its current route, which is the engine's own no-match branch.</summary>
        public void SetAiNet(IReadOnlyList<(string Name, string Net)> entries)
        {
            int moved = 0;
            var unmatched = new List<string>();
            foreach (var (name, netName) in entries)
            {
                if (_owner.Commanded(name) is not { } rig || rig.Pilot is not { } pilot)
                {
                    // A hull takes the same clause: its route restarts from where it is.
                    if (_owner.CommandedVessel(name) is { } vessel
                        && AiNets.ByName(_owner._chapterNets, netName) is { } water)
                    {
                        vessel.Patrol(water);
                        moved++;
                        Log.Info("core", $"campaign: SET_AI_NET '{name}' (hull) onto '{water.Name}#{water.Id}'");
                        continue;
                    }
                    // An airship re-seats on its own arm: no heading is offered to the seat, since
                    // the nose-aligned edge pick is the aeroplane's rule and not a zeppelin's.
                    if (_in.Zeppelins?.SetNet(name, netName) == true)
                    {
                        moved++;
                        continue;
                    }
                    // The runtime knows this airship and has warned that the chapter carries no
                    // such net: no move, but not a name this session failed to build either.
                    if (_in.Zeppelins?.MotionFor(name) != null)
                    {
                        continue;
                    }
                    unmatched.Add(name);
                    continue;
                }

                if (AiNets.ByName(_owner._chapterNets, netName) is not { } net)
                {
                    Log.Info("core", $"campaign: SET_AI_NET '{netName}' is not a net this chapter carries: '{name}' keeps the route it is on");
                    continue;
                }

                // The same seat a lost leader's net takes. The net's own volumes overwrite the
                // vehicle's where it authors them: only the spawn runs the roster block's copy
                // afterwards, so here the net wins outright.
                SeatOnNet(rig, pilot, net, _owner._netTrailers, _owner._minAiActiveDist);
                moved++;
                Log.Info("core", $"campaign: SET_AI_NET '{name}' onto '{net.Name}#{net.Id}'");
            }
            Report("SET_AI_NET", moved, entries.Count, unmatched);
        }

        /// <summary>Writes each named aircraft's attack volume radius (<c>FUN_00469f70</c>, vehicle
        /// <c>+0x328</c>). The altitude band the same write carries has no consumer here, the same
        /// place <see cref="CampaignRosterPlan.ApplyVolumes"/> leaves it. No shipped mission
        /// authors this directive, so only a modified script reaches it.</summary>
        public void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries)
        {
            int set = 0;
            var unmatched = new List<string>();
            foreach (var (name, radius) in entries)
            {
                if (_owner.Commanded(name) is not { } rig || rig.Pilot?.Machine is not { } machine)
                {
                    unmatched.Add(name);
                    continue;
                }

                machine.AttackRange = radius;
                set++;
            }
            Report("SET_AI_ATTACK_RADIUS", set, entries.Count, unmatched);
        }

        /// <summary>Writes each named zeppelin's broadside engage flag (<c>FUN_0046a0b0</c>,
        /// zeppelin byte <c>+0xc</c>): the only thing that lets a broadside deploy and fire.</summary>
        public void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            if (_in.Zeppelins is not { } zeppelins)
            {
                _owner.Gap("COMPLETED_ZEPCANNONS", "no zeppelin runtime in this session");
                return;
            }

            foreach (var (name, flag) in entries)
            {
                if (!zeppelins.SetCannonsEngaged(name, flag != 0))
                {
                    _owner.Gap("COMPLETED_ZEPCANNONS", $"'{name}' is no cannon-bearing zeppelin here");
                }
            }
        }

        public void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            if (_in.Zeppelins is not { } zeppelins)
            {
                _owner.Gap("COMPLETED_STOPPOINT", "no zeppelin runtime in this session");
                return;
            }

            foreach (var (net, stop, flag) in entries)
            {
                zeppelins.SetStopPoint(net, stop, flag != 0);
            }
        }

        public void StartTaxi(IReadOnlyList<string> names)
        {
            int released = 0;
            foreach (var name in names)
            {
                if (_owner._paths?.Release(name) == true)
                {
                    released++;
                }
            }

            if (released > 0)
            {
                Log.Info("core", $"campaign: START_TAXI released {released} vehicle(s) onto their paths");
            }
            else if (names.Count > 0)
            {
                _owner.Gap("START_TAXI", $"'{names[0]}' is not a spawned mission vehicle");
            }
        }

        // The shared tail of the three SET_AI_* directives: one line for what landed, one Gap for
        // what did not. An unmatched name has been through every arm the lookup has, so it names
        // something this session did not build rather than an arm that is missing.
        private void Report(string directive, int applied, int total, List<string> unmatched)
        {
            if (applied > 0)
            {
                Log.Info("core", $"campaign: {directive} applied to {applied} of {total} named vehicle(s)");
            }

            if (unmatched.Count > 0)
            {
                _owner.Gap(directive, $"'{unmatched[0]}' and {unmatched.Count - 1} more name no " +
                                      "aircraft, hull or zeppelin this mission built");
            }
        }

        // Either end of a node-form TRAVELERS: a built world node first, then the roster's own
        // aircraft and hulls, which is where an aiv block's name usually lives.
        // ⚠ Never gate this on Deactivated. C4/M02 spots Blacke while he is still inert; the
        // reading is in docs/formats/objectives.md ("TRAVELERS and the roster").
        private Vector3? Where(string name)
        {
            if (Resolve(new[] { name }) is { } node)
            {
                return node.GlobalPosition;
            }

            if (_owner._roster.TryGetValue(name, out var rig))
            {
                return rig.WorldPosition;
            }

            return _owner._vessels.TryGetValue(name, out var vessel) ? vessel.Position : null;
        }

        // A node path: the first name resolved globally, then each named child inside the one
        // before it, which is how INACTIVEn walks ["piratezep","leng22","healthy"].
        private Node3D? Resolve(IReadOnlyList<string> path)
        {
            if (_in.Runtime == null || path.Count == 0)
            {
                return null;
            }

            Node3D? node = null;
            for (int i = 0; i < path.Count; i++)
            {
                var found = _in.Runtime.FindNodes(path[i], node);
                if (found.Count == 0)
                {
                    return null;
                }

                node = found[0];
            }

            return node;
        }
    }
}
