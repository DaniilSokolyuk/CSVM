using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.InstantAction;

/// <summary>
/// The engine-side sequencing of one Instant Action mission: the mission's wave state, the ace,
/// and (as the deepening proceeds) the actor build phases, the sequencer tick and the end-condition
/// wiring. The decoded rules stay engine-free in <see cref="InstantActionRuntime"/> and
/// <see cref="InstantActionWaves"/>; this class is where they meet the engine. Held as
/// <c>GameSession</c>'s one nullable director field, null outside a mission, which is what keeps
/// every other session mode (free flight, Dogfight) untouched by its existence.
///
/// Not a Node: <see cref="SessionSimulation"/> owns the tick order and calls the phases at their
/// decoded points. Every node built for this mission parents under the session's <c>_worldRoot</c>,
/// so the session's no-Teardown rule holds unchanged.
/// </summary>
public sealed class InstantActionDirector
{
    // The launch's settled spec (see SessionSpec), read for the chapter/mission identity, the
    // score key and the --debug-scoreboard gate.
    private readonly SessionSpec _spec;

    // E11's wave sequencer: null on dogfight_ace (every wave count is forced to 0).
    // _waveRosters[w] is wave w+1's built (inert until activated) members, indexed the same way;
    // the sequencer tick polls the current wave's alive count and activates whatever
    // InstantActionWaves.Step hands back, through the teleport arm, or (F12, zeppelin_run)
    // through the zeppelin generator's own wave credit.
    private InstantActionWaves? _waves;
    private List<FlightController>[]? _waveRosters;
    private List<SpawnPoint>? _waveSpawnList;

    // F12: the wave whose parked members the objective zeppelin's generator is releasing, the
    // decoded group stamp (FUN_0045b9d0 writes the new counter to the generator's +0x64). 0
    // outside a zeppelin run, or before the first wave becomes current.
    private int _launchWave;

    // G14: the authored ace, kept past the build (not just a build-phase local) so
    // --debug-scoreboard can force it down from the drive path, well after every Downed
    // subscription the end-condition block wires is in place. Null outside dogfight_ace.
    private FlightController? _ace;

    // --debug-scoreboard (IA): single-fire, same shape as GameSession's
    // _crashFired/_versusDebugKillFired.
    private bool _debugForceFired;

    // The militia -> paint-pattern table, loaded once per mission for the wave liveries.
    private Dictionary<string, string>? _militiaPatterns;

    // The session's rigs and (zeppelin run only) its generator runtime, kept from the build
    // phases so the sequencer tick can activate a wave without reaching back into GameSession.
    private List<PlayerRig>? _rigs;
    private AiGeneratorRuntime? _generators;

    // The references WireEndConditions handed over, kept for the spectate hand-off and the
    // wrap-up, which run long after the build.
    private EndConditionInputs? _end;

    private InstantActionDirector(InstantActionRuntime runtime, SessionSpec spec)
    {
        Runtime = runtime;
        _spec = spec;
    }

    /// <summary>The roster's authoring spawn operation, handed in as a lambda for the same reason
    /// AiGeneratorRuntime takes one: the spawner and its roster stay GameSession's. The director
    /// writes the whole <see cref="AiSpawn"/>, so every authored fact of an actor, its name
    /// included, is set here where a suite can drive it.</summary>
    internal delegate FlightController? SpawnAuthoredAircraft(AiSpawn spawn);

    /// <summary>GameSession.RegisterAiVoice: the voice runtime serves non-mission spawns too.</summary>
    internal delegate void RegisterAiVoice(FlightController? ai, int? accentId, int? talkerOverride,
        int? constitutionOverride);

    /// <summary>The engine-free mission runtime: the loaded def, the objective bookkeeping and
    /// the decoded actor rules. Never null on a built director.</summary>
    public InstantActionRuntime Runtime { get; }

    /// <summary>Every built wave member, across all four waves. 0 until BuildActors has run.</summary>
    private int WaveEnemyCount => _waveRosters?.Sum(r => r.Count) ?? 0;

    // ⚠ Keep both producers (the wizard's SessionSpec.IaDef and --ia=<path>) converging on the
    // one InstantActionRuntime construction; two similar calls is the failure this avoids.
    // A failed --ia= load warns and flies without a mission rather than aborting the launch.
    public static InstantActionDirector? TryCreate(SessionSpec spec)
    {
        if (spec.IaDef is { } wizardDef)
        {
            Log.Info("core", $"ia: wizard mission_type={wizardDef.MissionType} player='{wizardDef.PlayerPlane}' ace='{wizardDef.AceName}' ({wizardDef.AcePlane})");
            return new InstantActionDirector(new InstantActionRuntime(wizardDef), spec);
        }
        if (spec.IaPath != null)
        {
            try
            {
                var def = Mech3.InstantAction.LoadFromJson(spec.IaPath);
                Log.Info("core", $"ia: '{spec.IaPath}' mission_type={def.MissionType} player='{def.PlayerPlane}' ace='{def.AceName}' ({def.AcePlane})");
                return new InstantActionDirector(new InstantActionRuntime(def), spec);
            }
            catch (Exception e)
            {
                GD.PushWarning($"--ia={spec.IaPath}: cannot load ({e.Message}), flying without a mission");
            }
        }
        return null;
    }

    // Split out so a suite can drive it over a bare StuntMission. ⚠ Gated on THIS run's own
    // AllComplete, never the mission's win/loss flag: splitscreen can end with the other pilot
    // still short. prevBest stays read before the gated record, reorder it and a completing
    // new-best run would show its own just-written time as "previous" (docs/architecture.md).
    internal static StuntSummary BuildStuntSummary(StuntMission run, ScoreStore store, string key)
    {
        float? prevBest = store.GetBest(key);
        bool newBest = run.AllComplete && store.RecordIfBest(key, run.Elapsed);
        return new StuntSummary(run, run.Elapsed, prevBest, newBest);
    }

    /// <summary>The wave-clear walk's "still alive" count over one wave's roster.
    /// ⚠ A member still waiting in the zeppelin's bay COUNTS as present, as the decoded walk counts
    /// a still-deactivated enemy (docs/formats/instant-action.md). So does a member under the
    /// mission-start cutscene park, which is inert but still in the mission: reading
    /// <see cref="FlightController.InPlay"/> alone clears wave 1 on the first step.</summary>
    internal static int WaveMembersPresent(IEnumerable<FlightController> roster, bool zeppelinRun) =>
        zeppelinRun
            ? roster.Count(fc => !fc.Crashed)
            : roster.Count(fc => fc.InPlay || (fc.Parked && !fc.Crashed));

    /// <summary>The mission's actor build: the chapter's patrol net, the ace (dogfight_ace), the
    /// wingmen, and every wave's inert roster, one contiguous phase of GameSession's
    /// BuildFlightRigs, called at the same point in its build order. Returns the build-summary
    /// suffix ("+ IA ace" and kin) for the session's one-line report.</summary>
    internal string BuildActors(ActorBuildInputs inputs)
    {
        string what = "";
        _rigs = inputs.Rigs;
        // ⚠ Hand every Instant Action actor the chapter's FIRST patrol net, ace, wingmen and wave
        // members alike, and read "first" as neindex FILE order, never the lowest id
        // (docs/formats/instant-action.md).
        AiNet? iaPatrolNet = null;
        try
        {
            iaPatrolNet = AiNets.ChapterFirst(AiNets.Load(inputs.ChapterZrdrPath),
                inputs.ChapterZrdrPath);
        }
        catch (Exception e)
        {
            GD.PushWarning($"ia: cannot read {_spec.Chapter}'s patrol nets: {e.Message}");
        }
        if (iaPatrolNet is { Nodes.Count: 0 })
        {
            iaPatrolNet = null;
        }
        string iaPatrolAnchor = iaPatrolNet?.Trailer is { NodeIndex: >= 0, Name: { } anchorName } anchorTrailer
            ? Log.Format($", anchored to '{anchorName}' at node {anchorTrailer.NodeIndex}")
            : "";
        if (iaPatrolNet != null)
            Log.Info("core", $"ia: actors patrol '{iaPatrolNet.Name}' (net {iaPatrolNet.Id}), the chapter's first{iaPatrolAnchor}");
        else
            Log.Info("core", $"ia: {_spec.Chapter} has no first patrol net, actors fly their spawn course");
        // The net IS the standing order. ⚠ Do not let it bring its own volumes: the original copies
        // the roster block's volumes over the net's afterwards, so ApplyActorVolumes has the last
        // word (docs/formats/instant-action.md).
        Action<AiPilot> armIaPatrol = pilot =>
        {
            if (iaPatrolNet == null)
                return;
            pilot.Patrol = new AiNetFollower(iaPatrolNet, Rng.NewSystemRandom(Rng.Ai),
                trailerTarget: inputs.NetTrailers.For(iaPatrolNet));
        };
        var ia = Runtime;
        if (string.Equals(ia.Def.MissionType, "dogfight_ace", StringComparison.OrdinalIgnoreCase))
        {
            string? aceNode = Mech3.InstantAction.PlaneNodeFor(ia.Def.AcePlane);
            if (aceNode == null)
            {
                GD.PushWarning($"ia: ace plane '{ia.Def.AcePlane}' is not one of the eleven " +
                                "airframes, no ace spawned");
            }
            else if (SpawnPoints.LoadIa(inputs.MissionZrdrPath, ia.Def.MissionType) is not { Count: > 0 } aceSpawns)
            {
                GD.PushWarning($"ia: no '{ia.Def.MissionType}' spawn points for " +
                                $"{_spec.Chapter}/{_spec.Mission}, no ace spawned");
            }
            else
            {
                int playerSpawnIndex = inputs.SpawnBase % aceSpawns.Count;
                uint draw = Rng.Stream(Rng.Spawn).Randi();
                var (spIndex, sp) = InstantActionRuntime.ChooseAceSpawn(aceSpawns, playerSpawnIndex, draw);
                var fwd = new Basis(Vector3.Up, Mathf.DegToRad(sp.HeadingDeg)) * Vector3.Forward;
                var pilot = AiPilot.HoldingCourse(sp.Position, sp.Position + fwd);
                armIaPatrol(pilot);
                int rating = InstantActionRuntime.RepresentativeRating(ia.Def.AceStats);
                // shippedSkins for the same reason as a wave member below: the ace flies for an
                // enemy militia, so an ia.json without ace_pattern (hand-authored only) falls back
                // to its own textures, never the player militia's Fortune Hunters default.
                var ace = inputs.Spawn(new AiSpawn(aceNode, sp.Position, sp.Position + fwd, pilot,
                    ia.Def.AceLivery, InstantActionRuntime.EnemyTeam, Inert: false,
                    ShippedSkins: true, AttackRating: rating, PilotName: ia.Def.AceName,
                    Difficulty: Flight.Hangar.Difficulty.Parse(ia.Def.AceSkill)));
                inputs.RegisterVoice(ace, ia.Def.AceAccentId, rating, rating);
                _ace = ace;
                InstantActionRuntime.ApplyActorVolumes(pilot.Machine);
                if (ace != null)
                {
                    Log.Info("core", $"ia: ace '{ia.Def.AceName}' ({aceNode}) rating={rating} team={InstantActionRuntime.EnemyTeam} spawn #{spIndex} of {aceSpawns.Count}");
                    what += " + IA ace";
                }
            }
        }
        // The wingmen. NumWingmen is forced to 0 on dogfight_ace at parse time, so this and the
        // ace block above are exclusive without an extra mission-type test.
        if (ia.Def.NumWingmen > 0
            && inputs.Rigs.Count > 0 && inputs.Rigs[0].Controller is { } leadForWingmen)
        {
            string? wingmanNode = Mech3.InstantAction.PlaneNodeFor(ia.Def.WingmanPlane);
            if (wingmanNode == null)
            {
                GD.PushWarning($"ia: wingman plane '{ia.Def.WingmanPlane}' is not one of " +
                                "the eleven airframes, no wingmen spawned");
            }
            else
            {
                int humans = inputs.Rigs.Count;
                int flown = InstantActionRuntime.FlownWingmen(ia.Def.NumWingmen, humans);
                if (flown < ia.Def.NumWingmen)
                {
                    Log.Info("core", $"ia: wingmen clamped to {flown} of {ia.Def.NumWingmen} configured ({humans} human(s), flight cap 6, decision 8a)");
                }
                // player_fortune: the wingmen's shared livery. The colour and decal values ride the
                // setup screen in the original, not ia.json, so the catalog entry stands in
                // (docs/formats/paint.md).
                var wingmanScheme = inputs.LiveryResolver.PaintCatalog(inputs.ZrdrPath)
                    .Find(s => string.Equals(s.Pattern, LiveryResolver.DefaultPattern, StringComparison.OrdinalIgnoreCase));
                if (wingmanScheme == null)
                {
                    GD.PushWarning("ia: no 'player_fortune' entry in the paint catalog, wingmen " +
                                    "fly unpainted/random");
                }
                var wmBasis = leadForWingmen.GlobalTransform.Basis;
                var wmFwd = -wmBasis.Z;
                var wmLeadPos = leadForWingmen.WorldPosition;
                var wingmen = new FlightController?[flown];
                for (int i = 0; i < flown; i++)
                {
                    var slot = InstantActionRuntime.WingmanSlotFor(i);
                    var offsetDir = wmFwd.Rotated(Vector3.Up, Mathf.DegToRad(slot.OffsetDeg));
                    var pos = wmLeadPos + offsetDir * slot.MetresOut;
                    var pilot = AiPilot.HoldingCourse(pos, pos + wmFwd);
                    // The wingman takes the same net the ace and the waves do, which in the original
                    // demotes it out of wingman mode, so the escort chain below is a target
                    // assignment and not a flown formation (docs/org/aiPilot.md).
                    armIaPatrol(pilot);
                    // ⚠ Pass an explicit rating, never null: a wingman's Gunner and Machine are only
                    // built when one resolves, and null would arm them solely on a launch that
                    // happened to carry --ai-attack= (docs/formats/instant-action.md).

                    // The wizard's one wingman fit, covering the whole flight as the original's
                    // Player/Wingman radio does. Passed per spawn, never as a blanket default: the
                    // stock-table branch it lands in also catches enemies on player airframes.
                    var wingman = inputs.Spawn(new AiSpawn(wingmanNode, pos, pos + wmFwd, pilot,
                        wingmanScheme, AimAssist.PlayerTeam, Inert: false, ShippedSkins: false,
                        Fit: _spec.IaWingmanLoadout, AttackRating: 5, PilotName: slot.TitleKey,
                        Difficulty: null)); // on the player's team, so the scale never reaches it
                    wingmen[i] = wingman;
                    if (wingman == null)
                        continue;
                    // primary_target: 0, 1 and 3 escort the player; 2 and 4 escort
                    // wingmen 1 and 3, FlightController.SelectRankedTarget's own by-name/"player"
                    // match, the same seam the D12 ranking already reads.
                    if (pilot.Gunner != null)
                    {
                        pilot.Gunner.PrimaryTargetName = slot.PrimaryTargetIsWingman is { } escortIdx
                            ? wingmen[escortIdx]?.Name.ToString()
                            : "player";
                    }
                    // An Instant Action actor's volumes are all authored far wider than the airframe
                    // defaults the roster spawn arms (docs/formats/instant-action.md).
                    InstantActionRuntime.ApplyActorVolumes(pilot.Machine);
                    inputs.RegisterVoice(wingman, slot.AccentId, null, null);
                }
                int wmSpawned = wingmen.Count(w => w != null);
                if (wmSpawned > 0)
                {
                    Log.Info("core", $"ia: {wmSpawned} wingman(s) ({wingmanNode}) team={AimAssist.PlayerTeam}");
                    what += $" + {wmSpawned} IA wingmen";
                }
            }
        }
        // The wave sequencer. ⚠ Build EVERY wave inert here, wave 1 included, so one
        // build-then-activate path serves them all; the mission type decides a wave's ARRIVAL, not
        // whether it is built (docs/formats/instant-action.md).
        var waveSizes = ia.Def.Waves.Select(w => w.NumEnemies).ToArray();
        var rosters = new List<FlightController>[4];
        for (int w = 0; w < 4; w++)
        {
            var roster = new List<FlightController>();
            var wave = ia.Def.Waves[w];
            if (wave.NumEnemies > 0)
            {
                string? waveNode = Mech3.InstantAction.PlaneNodeFor(wave.EnemyPlane);
                if (waveNode == null)
                {
                    GD.PushWarning($"ia: wave {w + 1} plane '{wave.EnemyPlane}' is not one " +
                                    "of the eleven airframes, no wave enemies spawned");
                }
                else
                {
                    for (int m = 0; m < wave.NumEnemies; m++)
                    {
                        // Built at the origin, inert, position is irrelevant until
                        // ActivateInstantActionWave teleports it in, same as the original's
                        // own "deactivated at the world origin".
                        var pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward);
                        // Armed at build, but the follower seats itself at its first update and
                        // Activate re-seats it, so a member patrols from where it arrives
                        // rather than from this parking pose.
                        armIaPatrol(pilot);
                        // ⚠ The militia paints it and nothing more: the original spawns a wave
                        // member from the PLAIN AI def of its aircraft. An unnamed militia keeps
                        // its own skins, never the Fortune Hunters default.
                        var waveScheme = WaveMilitiaScheme(inputs, wave.EnemyName);
                        int rating = InstantActionRuntime.RepresentativeRating(
                            InstantActionRuntime.RandomPilotStats(Rng.Stream(Rng.Ai).Randi()));
                        // The wave's skill is not a pilot rating: its whole effect is standing in
                        // as the difficulty for this one spawn (docs/formats/instant-action.md).
                        // enemy_name names the member on its marker as well as choosing its paint.
                        var enemy = inputs.Spawn(new AiSpawn(waveNode, Vector3.Zero,
                            Vector3.Forward, pilot, waveScheme, InstantActionRuntime.EnemyTeam,
                            Inert: true, ShippedSkins: waveScheme == null, AttackRating: rating,
                            PilotName: wave.EnemyName,
                            Difficulty: Flight.Hangar.Difficulty.Parse(wave.EnemySkill)));
                        if (enemy == null)
                        {
                            continue;
                        }
                        if (pilot.Gunner != null)
                        {
                            pilot.Gunner.PrimaryTargetName = "player";
                        }
                        // The same authored actor volumes as the ace and wingmen
                        // (docs/formats/instant-action.md).
                        InstantActionRuntime.ApplyActorVolumes(pilot.Machine);
                        int accentId = InstantActionRuntime.ResolveWaveAccentId(
                            wave.EnemyAccentId, Rng.Stream(Rng.Ai).Randi());
                        inputs.RegisterVoice(enemy, accentId, rating, rating);
                        roster.Add(enemy);
                    }
                }
            }
            rosters[w] = roster;
        }
        _waveRosters = rosters;
        _waveSpawnList = inputs.SpawnList;
        _waves = new InstantActionWaves(waveSizes);
        // On zeppelin_run the first wave is started by ArmZeppelinRun after the generator block,
        // since "activating" it there means crediting the zeppelin's generator, which does not
        // exist yet at this point in the build.
        if (!ia.IsZeppelinRun)
        {
            int firstWave = _waves.Start();
            if (firstWave != 0)
            {
                ActivateWave(firstWave);
            }
        }
        int iaWaveEnemies = rosters.Sum(r => r.Count);
        if (iaWaveEnemies > 0)
        {
            Log.Info("core", $"ia: {iaWaveEnemies} wave enemies across {rosters.Count(r => r.Count > 0)} wave(s), built inert, team={InstantActionRuntime.EnemyTeam}");
            what += $" + {iaWaveEnemies} IA wave enemies";
        }
        return what;
    }

    /// <summary>The mission's zeppelin switch: every distinct authored zeppelin node written
    /// Visible = objective (the decoded gwNodeSetActive), the non-objectives held. Returns the
    /// switched nodes for the turret arm, which GameSession runs inside its own emplacement block
    /// (⚠ there: BEFORE --wake-turrets). ⚠ Visible is the WHOLE write, since world colliders
    /// derive Disabled from it, and each switched node must reach that turret arm or the
    /// objective zeppelin flies unarmed.</summary>
    internal List<(Node3D Node, bool Objective, string Name)> SwitchZeppelins(
        AnimRuntime world, ZeppelinRuntime? zeppelins, string chapter)
    {
        var switched = new List<(Node3D Node, bool Objective, string Name)>();
        string selectedZep = InstantActionRuntime.SelectedZeppelinNode(Runtime.Def);
        var switchedZeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string zepName in InstantActionRuntime.ZeppelinNodes(Runtime.Def))
        {
            if (!switchedZeps.Add(zepName))
            {
                continue;   // all 8 shipped chapters name the same node three times
            }
            bool objective = Runtime.IsZeppelinRun
                && zepName.Equals(selectedZep, StringComparison.OrdinalIgnoreCase);
            if (world.FindNodes(zepName) is not { Count: > 0 } zepNodes)
            {
                Log.Info("core", $"ia: zeppelin '{zepName}' is not in {chapter}'s world, nothing to switch");
                continue;
            }
            foreach (var zepNode in zepNodes)
            {
                zepNode.Visible = objective;   // colliders derive from this (WorldCollision)
                switched.Add((zepNode, objective, zepName));
            }
            if (!objective)
            {
                zeppelins?.Hold(zepName);
            }
            Log.Info("core", $"ia: zeppelin '{zepName}' {(objective ? "ACTIVATED as this mission's objective (zeppelin_run)" : "deactivated by the Instant Action builder")}");
        }
        return switched;
    }

    /// <summary>The zeppelin run's wave arm: the objective zeppelin's generator releases the waves
    /// built inert by BuildActors on a budget the sequencer credits wave by wave. ⚠ The first wave
    /// can only start here, since activating it means crediting a generator that did not exist at
    /// BuildActors time. A no-op on every other mission type.</summary>
    internal void ArmZeppelinRun(AiGeneratorRuntime? generators)
    {
        if (!Runtime.IsZeppelinRun || _waves is not { } waves)
        {
            return;
        }
        _generators = generators;
        string objectiveZep = InstantActionRuntime.SelectedZeppelinNode(Runtime.Def);
        int claimed = generators?.UseInstantActionLaunches(
            objectiveZep, ReleaseWaveMember) ?? 0;
        if (claimed == 0)
        {
            // ⚠ Do not invent a fallback spawn path: the original burns through every wave the
            // same way, its counter advancing whether or not the top-up lands
            // (docs/formats/instant-action.md).
            GD.PushWarning($"ia: zeppelin '{objectiveZep}' carries no egen generator, no " +
                            "wave will ever launch on this zeppelin run");
        }
        else
        {
            Log.Info("core", $"ia: zeppelin run: '{objectiveZep}' launches every wave ({claimed} generator(s) on the wave-credit budget). A drop is not gated on the bay doors opening, so a wave can leave a zeppelin that is still shut.");
        }
        int firstZepWave = waves.Start();
        if (firstZepWave != 0)
        {
            ActivateWave(firstZepWave);
        }
    }

    /// <summary>One sim step of the mission: the wave sequencer's tick, the mission clock and the
    /// wave-cleared win signal. SessionSimulation calls it after aircraft and landing evaluation.
    /// </summary>
    internal void Step(float dt)
    {
        var ia = Runtime;
        ia.Advance(dt);
        if (_waves is not { Finished: false, CurrentWave: >= 1 } waves)
        {
            return;
        }
        int alive = WaveMembersPresent(_waveRosters![waves.CurrentWave - 1], ia.IsZeppelinRun);
        int next = waves.Step(alive);
        if (next != 0)
        {
            ActivateWave(next);
        }
        else if (waves.Finished)
        {
            // Every configured wave cleared, the squadron mode's win. Reported on every mode;
            // the runtime drops it on the ones that do not run on it (a zeppelin run's waves all
            // clear too, and the zeppelin is what decides that mission).
            ia.ReportObjective(InstantActionObjective.WavesCleared);
        }
    }

    /// <summary>--debug-scoreboard: force this mission's own win signal on the first sim step,
    /// single-fire, attributed to P1 so the wrap-up board reads non-zero. The dogfight modes go
    /// through DebugForceCrash; stunt_flying is already forced by HumanFlightAdapter's own wiring;
    /// zeppelin_run has no force, its verification drove the mode through real damage.</summary>
    internal void ForceDebugScoreboard()
    {
        if (_debugForceFired)
        {
            return;
        }
        _debugForceFired = true;
        int? attributedTo = _rigs is { Count: > 0 } rigs ? rigs[0].Controller?.PlayerIndex : null;
        if (Runtime.Objective == InstantActionObjective.AceDown)
        {
            _ace?.DebugForceCrash(attributedTo);
        }
        else if (Runtime.Objective == InstantActionObjective.WavesCleared && _waveRosters is { } rosters)
        {
            // DebugForceCrash self-gates on InPlay, so this reaches only whatever wave
            // is currently active, the rest are still parked inert awaiting their own turn.
            foreach (var roster in rosters)
            {
                foreach (var member in roster)
                {
                    member.DebugForceCrash(attributedTo);
                }
            }
        }
    }

    /// <summary>The mission's end conditions, lives ledger, spectate hand-off and wrap-up board:
    /// every signal already exists by this point in the build, so this only routes them into the
    /// runtime that decides the outcome. ⚠ Each source reports the objective it satisfied and the
    /// runtime drops what this mission does not run on.</summary>
    internal void WireEndConditions(EndConditionInputs inputs)
    {
        _end = inputs;
        var iaEnd = Runtime;
        // "Enemies Shot Down": every EnemyTeam actor downed by an attributed shooter, on every
        // mission type. ⚠ Keep the killer != null filter: a bare terrain or mid-air crash never
        // reaches the take-hit body the original counts in (docs/formats/instant-action.md).
        int enemiesShotDown = 0;
        if (_ace is { } aceForCount)
        {
            aceForCount.Downed += (_, killer) => { if (killer != null) enemiesShotDown++; };
        }
        if (_waveRosters is { } endRosters)
        {
            foreach (var roster in endRosters)
            {
                foreach (var member in roster)
                {
                    member.Downed += (_, killer) => { if (killer != null) enemiesShotDown++; };
                }
            }
        }

        var objective = iaEnd.Objective;
        if (objective == InstantActionObjective.AceDown && _ace is { } aceForWin)
        {
            aceForWin.Downed += (_, _) => iaEnd.ReportObjective(InstantActionObjective.AceDown);
        }
        else if (objective == InstantActionObjective.WavesCleared && WaveEnemyCount > 0)
        {
            // Reported by Step off InstantActionWaves.Finished, the sequencer owns "every
            // configured wave is cleared" and nothing here re-derives it.
        }
        else if (objective == InstantActionObjective.ZonesFlown && inputs.StuntZones != null)
        {
            // ⚠ All-finished, never first past the post, and evaluated only over pilots who can
            // still fly, so one out of lives cannot deadlock a mission the survivors finished.
            // Checked on the two events that can make it true, never polled.
            foreach (var rig in _rigs!)
            {
                if (rig.Controller?.Stunt is { } run)
                {
                    run.RunCompleted += CheckZoneSets;
                }
            }
        }
        else if (objective == InstantActionObjective.ZeppelinDisabled && inputs.Zeppelins is { } endZeps)
        {
            string endZep = InstantActionRuntime.SelectedZeppelinNode(iaEnd.Def);
            Action<string> reportZeppelin = node =>
            {
                // The OBJECTIVE's own signal only: a mission world may fly other zeppelins,
                // and disabling or shooting down one of those is not this mission's win.
                if (string.Equals(node, endZep, StringComparison.OrdinalIgnoreCase))
                {
                    iaEnd.ReportObjective(InstantActionObjective.ZeppelinDisabled);
                }
            };
            // Both decoded paths report the ONE objective, engines first: the mode is for
            // disabling them, and the hull dying on the gasbag threshold wins it too.
            endZeps.ZeppelinEnginesDisabled += reportZeppelin;
            endZeps.ZeppelinKilled += reportZeppelin;
        }
        else
        {
            // A mission that cannot be won says so at build. Deliberate, in the shape
            // VersusMatch's disabled kill target/time limit already has, the mission still
            // flies and can still be lost.
            iaEnd.DisableObjective();
            GD.PushWarning("ia: this mission has NO win condition, " + (objective switch
            {
                null => $"mission type '{iaEnd.Def.MissionType}' has none in this build",
                InstantActionObjective.AceDown => "no ace was spawned",
                InstantActionObjective.WavesCleared => "no wave enemy is configured",
                InstantActionObjective.ZonesFlown => "this mission ships no danger zones",
                _ => "no zeppelin runtime was built",
            }) + " (it can still be lost)");
        }
        // Lives are a remake-only rule; no ia.json key carries one. Every human seat joins the
        // ledger, and NotifyPilotDown decides whether the crash cam ends in a respawn.
        foreach (var rig in _rigs!)
        {
            if (rig.Controller is not { } pilot)
            {
                continue;
            }
            iaEnd.RegisterPilot(pilot.PlayerIndex);
            // G14's "Shot %": the decode's "the local player" filter, generalised to every
            // human seat for splitscreen (ProjectilePool.ScoredShooters).
            inputs.Projectiles?.ScoredShooters.Add(pilot.PlayerIndex);
            pilot.AutoRespawnAfter = inputs.RespawnDelay; // crash cam, then back in, R skips
            // A mission with lives counts them, so R is the crash cam's skip and nothing else: a
            // respawn taken while flying would repair, restock and refuel for free.
            pilot.AllowLiveRespawn = false;
            pilot.Downed += (victim, _) =>
            {
                // ⚠ Ahead of the ledger: the ending settled the result, so a hull lost inside the
                // hold spends no life and takes no pane. It just falls, and the board still
                // reports what the ending decided.
                if (iaEnd.Ended)
                {
                    return;
                }
                if (iaEnd.NotifyPilotDown(victim))
                {
                    Log.Info("core", $"ia: P{victim + 1} down, {(iaEnd.Def.Lives == 0 ? "unlimited lives" : Log.Format($"{iaEnd.LivesLeft(victim)} life/lives left"))}, respawning in {inputs.RespawnDelay:0.#} s");
                    return;
                }
                BeginSpectate(rig);
            };
        }
        iaEnd.MissionEnded += outcome => Log.Info("core", $"ia: mission {(outcome == InstantActionOutcome.Won ? "COMPLETE" : "FAILED")}, {iaEnd.Def.MissionType} after {iaEnd.Elapsed:0.0} s; holding the world {InstantActionRuntime.WrapupHoldS:0.#} s before the wrap-up board");
        Log.Info("core", $"ia: {iaEnd.Def.MissionType}, win: {(iaEnd.ObjectiveEnabled ? objective!.Value.ToString() : "none")}, loss: every human out of lives ({(iaEnd.Def.Lives == 0 ? "unlimited" : Log.Format($"{iaEnd.Def.Lives}"))} per pilot), {iaEnd.PilotCount} human seat(s)");

        // The wrap-up board, shared over the WHOLE window like the race and dogfight boards,
        // never per pane: the mission ends for every human at once. ⚠ Danger Zones Completed
        // and Shot % are summed across every human seat, never picked from one pane.
        string context = $"{_spec.Chapter}   ·   {Mech3.InstantAction.MissionTypeLabel(iaEnd.Def.MissionType)}";
        // The Original presentation takes the ending onto its own menu page instead, so the board is
        // not built at all there: one wrap-up shows, never two.
        IaWrapupBoard? wrapupBoard = null;
        if (inputs.WrapupToMenu == null)
        {
            wrapupBoard = IaWrapupBoard.Build(
                context, exitsToMenu: inputs.ExitsToMenu, inputs.PauseState, inputs.MenuInputFor);
            wrapupBoard.Restart = inputs.RestartSession;
            wrapupBoard.Exit = inputs.ExitSession;
            // Player 1, for the same reason the race and dogfight boards are.
            wrapupBoard.PhotoMode = () => inputs.EnterPhotoMode(0);
            inputs.RegisterBoard(wrapupBoard);
            var wrapupLayer = new CanvasLayer { Name = "ia_wrapup_board", Layer = UI.Boards.HudLayers.Board };
            wrapupLayer.AddChild(wrapupBoard);
            inputs.WorldRoot.AddChild(wrapupLayer);
        }

        // The four counters are read at the ENDING, not when the board appears: what the world does
        // through the hold is no longer this mission's score. The splits are flattened to text here
        // too, the menu page outliving the zones they are read off.
        (UI.Menu.IaWrapupSnapshot Snapshot, StuntSummary? Stunt)? ended = null;
        iaEnd.MissionEnded += outcome =>
        {
            var stunt = StuntSummary();
            ended = (
                new UI.Menu.IaWrapupSnapshot(
                    outcome == InstantActionOutcome.Won, context, iaEnd.Elapsed, enemiesShotDown,
                    _rigs!.Sum(r => r.Controller?.Stunt?.CompletedCount ?? 0),
                    InstantActionRuntime.ShotPercent(
                        inputs.Projectiles?.CannonHits ?? 0, inputs.Projectiles?.CannonRoundsFired ?? 0),
                    stunt is { } run ? StuntSplits.Lines(run) : null),
                stunt);
            // A win is flown out: the original leaves the stick live for the whole hold, so only
            // the discrete commands go. A loss holds the seat whole, standing in for the crash
            // animation the original waits out there.
            HoldPilotControls(outcome == InstantActionOutcome.Won
                ? FlightControlHold.CommandsOnly
                : FlightControlHold.All);
        };
        iaEnd.WrapupDue += _ =>
        {
            // Released before the board is up, so the board's own menu primes over whatever is
            // still down and its Restart row answers the first press after it appears.
            HoldPilotControls(FlightControlHold.None);
            if (ended is not { } final)
            {
                return;
            }

            // The photographs are read at the handover rather than the ending: a marker crossed on
            // the frame the run completes is latched after the run's test, and its frame lands
            // later still. A marker first entered after that frame is never photographed.
            var camera = final.Stunt != null && _rigs is { Count: > 0 } ? _rigs[0].Controller?.StuntShots : null;
            if (inputs.WrapupToMenu is { } toMenu)
            {
                toMenu(camera != null
                    ? final.Snapshot with { Shots = camera.InMarkerOrder().ToList() }
                    : final.Snapshot);
                return;
            }

            var shown = final.Snapshot;
            wrapupBoard!.Present(shown.Won, shown.Elapsed, shown.EnemiesShotDown, shown.ZonesCompleted,
                shown.ShotPercent, final.Stunt, camera);
        };
    }

    // The hold's input half. The world, the cameras and every wreck carry on either way, so only
    // the seats change: a win keeps flying and loses its commands, a loss loses the stick too.
    private void HoldPilotControls(FlightControlHold hold)
    {
        if (_rigs is not { } rigs)
        {
            return;
        }
        foreach (var rig in rigs)
        {
            if (rig.Controller is { } pilot)
            {
                pilot.ControlHold = hold;
            }
        }
    }

    // Teleports and activates waveNumber's built (inert) roster: a spawn drawn against every live
    // human's CURRENT position, then the fan pattern off that point's heading. A missing spawn list
    // leaves the wave parked inert with a warning rather than guessing a position.
    // ⚠ On zeppelin_run the generator arm REPLACES all of that, never adds to it: the wave is not
    // moved and no spawn is drawn, the objective zeppelin's generator is credited instead.
    private void ActivateWave(int waveNumber)
    {
        var roster = _waveRosters![waveNumber - 1];
        if (roster.Count == 0)
        {
            return; // InstantActionWaves.Start/Step never hand back an empty wave; stay defensive
        }
        if (Runtime is { IsZeppelinRun: true } iaZepRun)
        {
            _launchWave = waveNumber;   // the decoded group stamp (the generator's +0x64)
            string objectiveZep = InstantActionRuntime.SelectedZeppelinNode(iaZepRun.Def);
            int fed = _generators?.GrantWaveCapacity(objectiveZep, roster.Count) ?? 0;
            Log.Info("core", $"ia: wave {waveNumber} ({roster.Count} aircraft) credited to '{objectiveZep}' ({fed} generator(s)), they launch from the bay, not teleported");
            return;
        }
        if (_waveSpawnList is not { Count: > 0 } spawns)
        {
            GD.PushWarning($"ia: no spawn points for wave {waveNumber}, {roster.Count} " +
                            "aircraft stay parked inert");
            return;
        }
        var humanPositions = new List<Vector3>();
        foreach (var rig in _rigs!)
        {
            if (rig.Controller is { } human)
            {
                humanPositions.Add(human.WorldPosition);
            }
        }
        uint draw = Rng.Stream(Rng.Spawn).Randi();
        var (spIndex, sp) = InstantActionWaves.ChooseWaveSpawn(spawns, humanPositions, draw);
        var fwd = new Basis(Vector3.Up, Mathf.DegToRad(sp.HeadingDeg)) * Vector3.Forward;
        for (int m = 0; m < roster.Count; m++)
        {
            var (metres, offsetDeg) = InstantActionWaves.FanOffset(m);
            var dir = fwd.Rotated(Vector3.Up, Mathf.DegToRad(offsetDeg));
            var pos = sp.Position + dir * metres;
            roster[m].Activate(pos, pos + fwd);
        }
        Log.Info("core", $"ia: wave {waveNumber} ({roster.Count} aircraft) activated at spawn #{spIndex} of {spawns.Count}");
    }

    // A pilot has spent its last life, which is this mission type's own "out for good": the shared
    // hand-off pins the wreck and puts a SpectatorCamera on the pane (Session/Roster/SpectateHandoff.cs).
    private void BeginSpectate(PlayerRig rig)
    {
        var end = _end!;
        if (!SpectateHandoff.Begin(rig, _rigs!, end.WorldRoot, end.LockCandidates,
                end.SpectatorCameras, out var follow))
        {
            return;
        }
        var pilot = rig.Controller!;
        Log.Info("core", $"ia: P{pilot.PlayerIndex + 1} is out of lives, spectating{(follow != null ? Log.Format($", following P{follow.PlayerIndex + 1}") : " from the crash camera")}");
        // One of the two events that can complete a stunt mission's zone sets: this pilot has
        // stopped being one the mission waits for.
        CheckZoneSets();
    }

    // The stunt-flying end test: the zone sets are flown once every pilot who can still fly has
    // finished. ⚠ InstantActionRuntime.ZoneSetsFlown owns the rule, so the suites test the same
    // predicate the session runs. A no-op on every other mission type.
    private void CheckZoneSets()
    {
        var pilots = new List<(bool OutOfLives, bool Finished)>(_rigs!.Count);
        foreach (var rig in _rigs)
        {
            if (rig.Controller is { } pilot)
            {
                pilots.Add((pilot.Spectating, pilot.Stunt is { AllComplete: true }));
            }
        }
        if (InstantActionRuntime.ZoneSetsFlown(pilots))
        {
            Runtime.ReportObjective(InstantActionObjective.ZonesFlown);
        }
    }

    // Player 1's stunt run for the wrap-up board's split section, on a stunt mission alone. The
    // best time is recorded here rather than on the board, under the same chapter/mission/plane key
    // the solo scoreboard uses, a different mission id, so Instant Action bests stay their own.
    private StuntSummary? StuntSummary()
    {
        if (Runtime.Objective != InstantActionObjective.ZonesFlown)
            return null;
        if (_rigs is not { Count: > 0 } || _rigs[0].Controller?.Stunt is not { } run)
            return null;
        var store = ScoreStore.Load();
        string key = $"{_spec.Chapter}/{_spec.Mission}/{PlaneRoster.PlaneFor(_spec, 0)}";
        return BuildStuntSummary(run, store, key);
    }

    // The launch hook handed to the objective zeppelin's generator: releases the next still-parked
    // member of the CURRENT wave at the generator's own drop point and attitude. ⚠ Do not re-derive
    // that point here. Null once the wave has nothing parked left, which the generator accounts as
    // a failed spawn.
    private FlightController? ReleaseWaveMember(Vector3 pos, Vector3 lookAt,
        Vector3 launchVelocity)
    {
        if (_waveRosters is not { } rosters || _launchWave is < 1 or > 4)
        {
            return null;
        }
        foreach (var member in rosters[_launchWave - 1])
        {
            if (!member.Inert)
            {
                continue;
            }
            member.Activate(pos, lookAt, launchVelocity, carrierDrop: true);
            return member;
        }
        return null;
    }

    // The livery a wave flies in: its militia's pattern, in that pattern's shipped colours. The
    // original paints a wave member from the setup screen rather than from a vehicle def, so this
    // holds for a pair the install ships no def for (Sacred Trust's Warhawk) as much as for one it
    // does. Null when the militia is not named or names no pattern, and the member keeps its skins.
    private PaintScheme? WaveMilitiaScheme(ActorBuildInputs inputs, string enemyName)
    {
        if (MilitiaPaint.PatternForWave(MilitiaPatterns(inputs), enemyName) is not { } pattern)
            return null;
        foreach (var scheme in inputs.LiveryResolver.PaintCatalog(inputs.ZrdrPath))
            if (string.Equals(scheme.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                return scheme;
        return null;
    }

    private IReadOnlyDictionary<string, string> MilitiaPatterns(ActorBuildInputs inputs)
    {
        if (_militiaPatterns != null)
            return _militiaPatterns;
        try
        {
            _militiaPatterns = MilitiaPaint.PatternByMilitia(inputs.ZrdrPath,
                Messages.Load(inputs.MessagesPath));
        }
        catch (Exception e)
        {
            GD.PushWarning($"ia: cannot read the militia paint patterns: {e.Message}");
            _militiaPatterns = new Dictionary<string, string>();
        }
        return _militiaPatterns;
    }

    /// <summary>What BuildActors reads from the session's build, in the shape of
    /// stable mission references plus the two delegates GameSession keeps
    /// private behaviour behind (the spawner and the voice registration).</summary>
    internal sealed class ActorBuildInputs
    {
        public List<PlayerRig> Rigs = null!;
        public string ChapterZrdrPath = "";
        public string MissionZrdrPath = "";
        public string ZrdrPath = "";
        public string MessagesPath = "";
        public List<SpawnPoint>? SpawnList;
        public int SpawnBase;
        public LiveryResolver LiveryResolver = null!;
        public NetTrailerTargets NetTrailers = null!;
        public SpawnAuthoredAircraft Spawn = null!;
        public RegisterAiVoice RegisterVoice = null!;
    }

    /// <summary>What the end-condition wiring, the spectate hand-off and the wrap-up board read
    /// from the session: the shared runtimes' references, the board plumbing, and the tree root
    /// every node this mission builds parents under (the session's no-Teardown rule).</summary>
    internal sealed class EndConditionInputs
    {
        public StuntMission? StuntZones;
        public ZeppelinRuntime? Zeppelins;
        public ProjectilePool? Projectiles;
        public Node3D WorldRoot = null!;
        public List<SpectatorCamera> SpectatorCameras = null!;
        public Func<IReadOnlyList<Node3D>> LockCandidates = null!;
        public float RespawnDelay;
        public bool ExitsToMenu;
        public Action RestartSession = null!;
        public Action ExitSession = null!;
        public PauseState PauseState = null!;
        public Func<int, UI.Screens.MenuInput> MenuInputFor = null!;
        public Action<int> EnterPhotoMode = null!;
        public Action<Control> RegisterBoard = null!;
        // Where the ending goes on a presentation with a wrap-up page of its own. The final numbers
        // leave for the menu and no board is built. Null keeps the in-flight board.
        public Action<UI.Menu.IaWrapupSnapshot>? WrapupToMenu;
    }
}
