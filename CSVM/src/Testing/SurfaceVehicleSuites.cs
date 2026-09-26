using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The surface-vehicle runtime over the two shipped missions that author a hull: CM08's
/// four <c>patrolboat_1..4</c> roster blocks in C1B's built world (spawned on the water at their
/// authored spots, deactivated until woken, driven along their nets by the scripted-path law,
/// their wake and destruction the chapter's own definitions), and CM12's <c>eshipg31</c> boat
/// generator in C2's (a hull launched off the take-off path, never an airframe, and never at the
/// world origin).</summary>
internal static class SurfaceVehicleSuites
{
    private const string BoatChapter = "C1B";
    private const string BoatMission = "M03";
    private const int BoatGroup = 3;
    private const string BoatDef = "patrolboat";

    // What the four blocks' slot-20 MSG_VEH_PATROLBOAT resolves to in the shipped string table,
    // lowercase b and all. The only mode ship blocks in the install that author the slot.
    private const string BoatTitle = "Patrol boat";

    private const float StepDt = 1f / 30f;

    private const string GenChapter = "C2";
    private const string GenMission = "M01";
    private const string GenHost = "eshipg31";
    private const string GenLaunch = "patrolboat_eg0";

    // A hull anywhere near the origin is the CM12 symptom (three Bloodhawks ramming g34586 at
    // (5,5,-109)); every authored spot in both missions is kilometres away from it.
    private const float OriginClearanceM = 1000f;

    // The water sits at y = 0 on both missions' blocks; a probe that lands a hull more than
    // this off it has read the wrong surface.
    private const float WaterToleranceM = 2f;

    private static readonly string[] BoatBlocks =
    {
        "patrolboat_1", "patrolboat_2", "patrolboat_3", "patrolboat_4",
    };

    [Suite("campaign-surface-vehicles",
        "a mode ship roster block or generator launch builds a hull, not an aircraft: CM08's "
        + "four patrolboat blocks in C1B's built world sit on the water at their authored "
        + "spots on their Patrolboat nets, deactivated and hidden until woken, count for DEDG "
        + "only once woken, start their wake emitters on their own pt_emitter nodes, drive "
        + "their nets under the scripted-path law at the taxi speed with their height pinned "
        + "to the water, land on the HUD's Enemy cycle and the gun aim assist's VehicleList "
        + "once woken under their block's slot-20 'Patrol boat' name line, and die once "
        + "through the chapter's destructible pool; CM12's eshipg31 "
        + "launch resolves a surface launch off Eshipg31_params, builds patrolboat_eg0 on the "
        + "host's first take-off point kilometres from the world origin, runs the path "
        + "westward at the taxi speed, never asks for an aircraft, draws no name line at all "
        + "since its template block authors no slot 20, and a realtime simulation "
        + "request advances it once through the shared session owner")]
    internal static void CampaignSurfaceVehicles(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var report = new StringBuilder();
        RosterBoats(ctx, report);
        GeneratorBoat(ctx, report);
        ctx.WriteArtifact("test-campaign-surface-vehicles.txt", report.ToString());
        ctx.Note($"{BoatChapter}/{BoatMission}'s four patrol boats and {GenChapter}/{GenMission}'s boat generator build hulls, not aircraft");
    }

    // CM08: the roster half, with the mission's aircraft blocks left unspawned so only the hulls
    // are under test.
    private static void RosterBoats(TestContext ctx, StringBuilder report)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BoatChapter, BoatMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BoatChapter);
        ctx.RequireData(missionZrdr, $"{BoatChapter}/{BoatMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{BoatChapter} zrdr");
        var mission = MissionOf(ctx, BoatChapter, BoatMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        ctx.Check(defs.ModeOf(BoatDef) == VehicleDefs.ShipMode && defs.AirframeFor(BoatDef) == null,
            $"'{BoatDef}' is a mode ship def with no player airframe");
        string startAnims = string.Join(", ", defs.StartAnimsOf(BoatDef));
        ctx.Check(defs.StartAnimsOf(BoatDef).Count == 2 && defs.InjureAnimsOf(BoatDef).Count == 2,
            $"'{BoatDef}' authors two start anims and a two-rung injure ladder: [{startAnims}]");

        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Boats"), null);
        // A counting factory: the harness's own archive closes with the build, and the wake is
        // reached only after it, so the real factory would build nothing (a session's outlives it).
        ctx.EmitterFactory = new CountingEmitterFactory();
        ctx.WithWorld(BoatChapter, collision: true, BoatMission, world =>
        {
            var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
            var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                world.Runtime, defs, worldRoot)
            {
                Strings = Messages.Load(ctx.MessagesPath),
            };
            worldRoot.AddChild(vessels);
            int aircraftAsked = 0;
            director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (_, _, _, _) =>
                {
                    aircraftAsked++;
                    return null;
                },
                SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                Rng = new Random(1),
            });
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                SurfaceVehicles = vessels,
                Rng = new Random(1),
            });

            ctx.Check(aircraftAsked > 0, $"the mission's aircraft blocks still go to the aircraft spawner ({aircraftAsked})");
            ctx.Same(BoatBlocks.Length, director.Vessels.Count, $"the four patrolboat blocks build hulls");
            foreach (var name in BoatBlocks)
            {
                if (!director.Vessels.TryGetValue(name, out var vessel))
                {
                    ctx.Check(false, $"'{name}' has a hull");
                    continue;
                }
                var at = vessel.Position;
                report.AppendLine($"{name}: at ({at.X:0.#},{at.Y:0.##},{at.Z:0.#}) net='{vessel.Net?.Name}' inert={vessel.Inert} hp={vessel.Health}");
                ctx.Check(at.Length() > OriginClearanceM, $"'{name}' is nowhere near the world origin: {at.Length():0} m");
                ctx.Check(Mathf.Abs(at.Y) <= WaterToleranceM, $"'{name}' sits at water height: y={at.Y:0.##}");
                ctx.Check(vessel.Net?.Name.StartsWith("Patrolboat", StringComparison.OrdinalIgnoreCase) == true,
                    $"'{name}' is on its authored Patrolboat net: '{vessel.Net?.Name}'");
                ctx.Check(vessel.Inert, $"'{name}' is built deactivated, waiting for WAKEUP_ENEMIES");
                ctx.Check(!vessel.Body.Visible, $"'{name}' is hidden while deactivated");
                ctx.Check(vessel.Health is > 0f, $"'{name}' carries the chapter's destructible pool: {vessel.Health}");
                ctx.Check(vessel.Team == 2, $"'{name}' carries its block's team: {vessel.Team}");
                ctx.Same(1, world.Runtime.FindNodes("pt_emitter1", vessel.Body).Count,
                    $"'{name}' carries its own pt_emitter1, the wake's anchor");
            }
            ctx.Same(0, director.GroupLiveCount(BoatGroup) ?? -1,
                $"DEDG over group {BoatGroup} counts no deactivated hull");

            // Held: a deactivated hull does not move however long the mission steps.
            var parked = Positions(director.Vessels);
            for (int i = 0; i < 60; i++)
            {
                vessels.SimStep(StepDt);
            }
            ctx.Check(Unmoved(parked, director.Vessels), $"a deactivated hull holds its spot");
            foreach (var vessel in director.Vessels.Values)
            {
                ctx.Check(vessel.Velocity == Vector3.Zero,
                    $"'{vessel.Name}' reports no velocity while deactivated: {vessel.Velocity}");
            }

            // Woken: the hull appears, its wake starts on its own emitters, and it drives its net
            // at the scripted-path law's taxi speed with its height pinned to the water.
            int woken = 0;
            foreach (var vessel in director.Vessels.Values)
            {
                woken += vessel.Wake() ? 1 : 0;
            }
            ctx.Same(BoatBlocks.Length, woken, $"every hull wakes once");
            ctx.Same(BoatBlocks.Length, director.GroupLiveCount(BoatGroup) ?? -1,
                $"DEDG over group {BoatGroup} counts the four woken hulls");
            int wakeEmitters = 0;
            foreach (var row in world.Runtime.Emitters.Census)
            {
                if (row.Name.StartsWith("wake_emit", StringComparison.OrdinalIgnoreCase))
                {
                    wakeEmitters++;
                }
            }
            report.AppendLine($"wake emitters after the wake: {wakeEmitters}");
            ctx.Check(wakeEmitters >= BoatBlocks.Length * 2,
                $"the wake reaches the hulls: {wakeEmitters} wake_emit* emitters over four boats");

            // A woken hull is on VehicleList, the SAME candidate list the aircraft roster feeds
            // (docs/org/aim-assist.md "The four lists"), so both the HUD's Enemy cycle and the
            // player's own gun aim assist see it, asserted here without flying a mission.
            var candidates = new AimCandidateSet();
            vessels.CollectVehicles(candidates);
            ctx.Same(BoatBlocks.Length, candidates.Vehicles.Count, $"every hull is a vehicle candidate");
            foreach (var c in candidates.Vehicles)
            {
                ctx.Check(c.Live, $"'{TargetPool.NameOf(c.Source)}' is live once woken");
            }
            var pool = new TargetPool();
            pool.Rebuild(candidates, subParts: null, AimAssist.PlayerTeam, self: null);
            var marked = director.Vessels[BoatBlocks[0]];
            bool onEnemyCycle = false;
            foreach (var t in pool.Enemy)
            {
                onEnemyCycle |= ReferenceEquals(t.Source, marked);
            }
            ctx.Check(onEnemyCycle, $"'{marked.Name}' (team {marked.Team}) is on the Enemy cycle the HUD brackets");

            // The name line: these four blocks are the only mode ship blocks in the install that
            // author slot 20, and the original draws that title over the box (docs/org/targeting.md).
            foreach (var t in pool.Enemy)
            {
                if (t.Source is not SurfaceVehicle named)
                {
                    continue;
                }
                var lines = new List<string>();
                TargetHud.LabelLines(t, null, lines, keepSlots: false);
                report.AppendLine($"{named.Name}: identity '{t.Name}' marker '{t.DisplayName}' lines [{string.Join("|", lines)}]");
                ctx.Check(t.DisplayName == BoatTitle,
                    $"'{named.Name}' prints its block's slot-20 title: '{t.DisplayName}'");
                ctx.Check(t.Name == named.Name,
                    $"'{named.Name}' keeps its block name as its identity: '{t.Name}'");
                ctx.Check(lines.Count == 1 && lines[0] == BoatTitle,
                    $"'{named.Name}' draws that title as the box's only label line");
            }

            var shooterPos = marked.Position + new Vector3(0f, 0f, 100f);
            var aimScan = new AimScan
            {
                MuzzlePosition = shooterPos,
                ShooterVelocity = Vector3.Zero,
                Forward = (marked.Position - shooterPos).Normalized(),
                Team = AimAssist.PlayerTeam,
                Speed = 300f,
                RangeSquared = 4000f * 4000f,
                ConeCos = Mathf.Cos(Mathf.DegToRad(30f)),
                DistFactor = 0f,
                Self = null,
            };
            bool snapped = AimAssist.Scan(aimScan, candidates, out var aimResult);
            ctx.Check(snapped && aimResult.Kind == AimTargetKind.Vehicle && ReferenceEquals(aimResult.Source, marked),
                $"the gun aim assist snaps onto '{marked.Name}': found={snapped} kind={aimResult.Kind}");

            const float runS = 20f;
            for (int i = 0; i < (int)(runS / StepDt); i++)
            {
                vessels.SimStep(StepDt);
            }
            foreach (var (name, vessel) in director.Vessels)
            {
                float moved = new Vector2(vessel.Position.X - parked[name].X, vessel.Position.Z - parked[name].Z).Length();
                report.AppendLine($"{name}: moved {moved:0.#} m in {runS:0} s, y={vessel.Position.Y:0.##}");
                ctx.Check(moved > 0f && moved <= PathFollower.TaxiSpeed * runS + 1f,
                    $"'{name}' drives its net after the wake, never above the taxi speed: {moved:0.#} m in {runS:0} s");
                ctx.Check(Mathf.Abs(vessel.Position.Y - parked[name].Y) < 0.01f,
                    $"'{name}' stays on the water while driving: y={vessel.Position.Y:0.##}");
            }

            // A driving hull carries its velocity into the candidate set, so the lead solver aims
            // ahead of it. Flat, since the hull is pinned to the water, and never above the
            // taxi speed, which the turn scaling only ever reduces.
            candidates.Clear();
            vessels.CollectVehicles(candidates);
            foreach (var c in candidates.Vehicles)
            {
                string who = c.Source is SurfaceVehicle sv ? sv.Name : "?";
                report.AppendLine($"{who}: candidate velocity ({c.Velocity.X:0.##},{c.Velocity.Y:0.##},{c.Velocity.Z:0.##})");
                ctx.Check(c.Velocity.Length() > 0.1f && c.Velocity.Length() <= PathFollower.TaxiSpeed + 0.01f,
                    $"'{who}' is a candidate with its own velocity: {c.Velocity.Length():0.##} m/s");
                ctx.Check(Mathf.Abs(c.Velocity.Y) < 1e-4f,
                    $"'{who}' leads flat, the route's height being the water's: vy={c.Velocity.Y:0.####}");
            }

            // The lead is real: the assisted line off a stationary shooter abeam a driving hull
            // points ahead of where the hull is, not at it.
            var lead = director.Vessels[BoatBlocks[0]];
            var leadFrom = lead.Position + new Vector3(0f, 0f, 300f);
            var leadScan = new AimScan
            {
                MuzzlePosition = leadFrom,
                ShooterVelocity = Vector3.Zero,
                Forward = (lead.Position - leadFrom).Normalized(),
                Team = AimAssist.PlayerTeam,
                Speed = 300f,
                RangeSquared = 4000f * 4000f,
                ConeCos = Mathf.Cos(Mathf.DegToRad(30f)),
                DistFactor = 0f,
                Self = null,
            };
            bool led = AimAssist.Scan(leadScan, candidates, out var leadResult);
            float ahead = led ? leadResult.Direction.Dot(lead.Velocity.Normalized()) : 0f;
            report.AppendLine($"lead: |v|={lead.Velocity.Length():0.##} m/s, aim.v={ahead:0.###}");
            ctx.Check(led && ahead > 0.01f,
                $"the assisted line leads '{lead.Name}' along its own velocity: {ahead:0.###}");

            // Killed: the pool reaches zero through the same call a weapon hit makes, the hull
            // reports its death once, stops where it died and leaves the DEDG count.
            var target = director.Vessels[BoatBlocks[0]];
            int deaths = 0;
            target.Destroyed += _ => deaths++;
            ctx.Check(world.Runtime.DamageAt(target.Body, (target.Health ?? 0f) + 1f),
                $"a hit on '{target.Name}' lands on its destructible pool");
            vessels.SimStep(StepDt);
            var dead = target.Position;
            for (int i = 0; i < 30; i++)
            {
                vessels.SimStep(StepDt);
            }
            ctx.Check(target.IsDestroyed && deaths == 1, $"'{target.Name}' is destroyed and reported once");
            ctx.Check(target.Position.IsEqualApprox(dead), $"a dead hull stops where it died");
            ctx.Same(BoatBlocks.Length - 1, director.GroupLiveCount(BoatGroup) ?? -1,
                $"DEDG over group {BoatGroup} drops the dead hull");
            vessels.Free();
        });
    }

    // CM12: the generator half. The label resolves a hull, the launch builds one on the host's
    // take-off path and it runs that path; no aircraft, nothing at the origin.
    private static void GeneratorBoat(TestContext ctx, StringBuilder report)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, GenChapter, GenMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, GenChapter);
        ctx.RequireData(missionZrdr, $"{GenChapter}/{GenMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{GenChapter} zrdr");
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var nets = AiNets.Load(chapterZrdr);
        var templates = CampaignRosterPlan.GeneratorTemplates(missionZrdr, defs, nets);
        EnemyGeneratorDef? def = null;
        foreach (var d in EnemyGenerators.Load(missionZrdr))
        {
            if (d.Node.Equals(GenHost, StringComparison.OrdinalIgnoreCase))
            {
                def = d;
            }
        }
        ctx.Check(def != null, $"{GenChapter}/{GenMission} authors the generator '{GenHost}'");
        if (def == null)
        {
            return;
        }
        var kind = CampaignRosterPlan.ResolveGeneratorLaunch(templates, def.VehicleParams, out var plan);
        ctx.Check(kind == GeneratorLaunch.Surface && plan is { Surface: true, Def: BoatDef },
            $"'{def.VehicleParams}' resolves a surface launch of '{plan?.Def}' ({kind})");
        if (plan == null)
        {
            return;
        }

        ctx.WithWorld(GenChapter, collision: false, GenMission, world =>
        {
            var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
            var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                world.Runtime, defs, worldRoot)
            {
                Strings = Messages.Load(ctx.MessagesPath),
            };
            worldRoot.AddChild(vessels);
            int aircraftAsked = 0;
            int ordinal = 0;
            var generators = new AiGeneratorRuntime(new[] { def },
                (name, scope) => world.Runtime.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null,
                nets, ctx.PlaneName,
                (d, pos, look, _) =>
                {
                    if (CampaignRosterPlan.ResolveGeneratorLaunch(templates, d.VehicleParams, out var hull)
                        != GeneratorLaunch.Surface || hull == null)
                    {
                        aircraftAsked++;
                        return default(LaunchedVehicle);
                    }
                    return new LaunchedVehicle(null, vessels.Spawn(hull, pos, look - pos,
                        EnemyGenerators.LaunchName(EnemyGenerators.LaunchBase(hull.Name), ordinal++)));
                });
            ctx.Same(1, generators.LiveCount, $"'{GenHost}' is live with its take-off path");
            var host = world.Runtime.FindNodes(GenHost) is { Count: > 0 } hosts ? hosts[0] : null;
            var first = host != null && world.Runtime.FindNodes(EnemyGenerators.LaunchPathNode(GenHost, 0), host)
                is { Count: > 0 } points ? points[0].GlobalPosition : (Vector3?)null;
            ctx.Check(first != null, $"'{GenHost}' carries its first take-off point");

            generators.GrantWaveCapacity(GenHost, def.WaveSize);
            for (int i = 0; vessels.Vessels.Count == 0 && i * StepDt < def.IndPeriod + def.WavePeriod + 2f; i++)
            {
                generators.SimStep(StepDt);
            }
            ctx.Same(0, aircraftAsked, $"the boat generator asks for no aircraft");
            ctx.Same(0, generators.RunningCount, $"a hull launch runs no aircraft take-off run");
            ctx.Check(vessels.Vessels.Count == 1 && vessels.ByName(GenLaunch) is { } launched
                      && launched.Net?.Name == def.Nets[0],
                $"the launch builds one hull named '{GenLaunch}' on '{def.Nets[0]}'");
            if (vessels.ByName(GenLaunch) is not { } boat || first is not { } start)
            {
                return;
            }
            var at = boat.Position;
            report.AppendLine($"{GenLaunch}: launched at ({at.X:0.#},{at.Y:0.##},{at.Z:0.#}), path point 0 at ({start.X:0.#},{start.Y:0.##},{start.Z:0.#})");
            ctx.Check(new Vector2(at.X - start.X, at.Z - start.Z).Length() < 1f,
                $"the hull launches on the host's first take-off point, not at the host node");
            ctx.Check(at.Length() > OriginClearanceM, $"the hull is nowhere near the world origin: {at.Length():0} m");

            // The blank half of the name line. This generator's own template block authors no
            // slot 20, so the original draws a box with no name over it and so must CSVM: a
            // fallback to the def's title or to the block name would invent one.
            var blankCandidates = new AimCandidateSet();
            vessels.CollectVehicles(blankCandidates);
            var blankPool = new TargetPool();
            blankPool.Rebuild(blankCandidates, subParts: null, AimAssist.PlayerTeam, self: null);
            var blankLines = new List<string>();
            bool onCycle = false;
            foreach (var t in blankPool.Enemy)
            {
                if (ReferenceEquals(t.Source, boat))
                {
                    onCycle = true;
                    TargetHud.LabelLines(t, null, blankLines, keepSlots: false);
                    ctx.Check(t.DisplayName.Length == 0,
                        $"'{GenLaunch}' authors no slot 20 and so prints no name: '{t.DisplayName}'");
                    ctx.Check(t.Name == GenLaunch,
                        $"'{GenLaunch}' still keeps its launch name as its identity: '{t.Name}'");
                }
            }
            report.AppendLine($"{GenLaunch}: marker '{boat.MarkerName}' lines [{string.Join("|", blankLines)}]");
            ctx.Check(boat.MarkerName.Length == 0,
                $"'{GenLaunch}' carries no marker name off its template block: '{boat.MarkerName}'");
            ctx.Check(onCycle, $"'{GenLaunch}' is on the Enemy cycle to be labelled at all");
            ctx.Same(0, blankLines.Count, $"'{GenLaunch}' draws no label line at all");

            // The realtime adapter and the shared session simulation together must still advance
            // one step. A concrete runtime callback here would make every interactive hull run 2x.
            var savedClock = GameClock.Current;
            try
            {
                var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
                GameClock.Current = clock;
                ctx.Check(!clock.ParentDriven, $"the surface-vehicle callback check uses a realtime clock");
                var simulation = new SessionSimulation(new SurfaceStepRuntime(vessels));
                var beforeStep = boat.Position;
                vessels._PhysicsProcess(StepDt);
                simulation.Step(StepDt);
                float oneStep = new Vector2(boat.Position.X - beforeStep.X,
                    boat.Position.Z - beforeStep.Z).Length();
                report.AppendLine($"{GenLaunch}: realtime adapter + session request moved {oneStep:0.###} m in one {StepDt:0.###} s step");
                ctx.Check(oneStep > 0f && oneStep <= PathFollower.TaxiSpeed * StepDt + 0.05f,
                    $"a realtime session request advances the hull once: {oneStep:0.###} m");
            }
            finally
            {
                GameClock.Current = savedClock;
            }

            const float runS = 20f;
            for (int i = 0; i < (int)(runS / StepDt); i++)
            {
                generators.SimStep(StepDt);
                vessels.SimStep(StepDt);
            }
            float moved = new Vector2(boat.Position.X - at.X, boat.Position.Z - at.Z).Length();
            report.AppendLine($"{GenLaunch}: moved {moved:0.#} m in {runS:0} s to ({boat.Position.X:0.#},{boat.Position.Y:0.##},{boat.Position.Z:0.#})");
            ctx.Check(moved > 50f && moved <= PathFollower.TaxiSpeed * runS + 1f,
                $"the hull runs the take-off path at the taxi law's speed: {moved:0.#} m in {runS:0} s");
            ctx.Check(boat.Position.X < at.X, $"…westward down esg31's path, the way the points run");
            vessels.Free();
        });
    }

    private static CampaignMission MissionOf(TestContext ctx, string chapter, string folder)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        throw new SuiteSkippedException($"{chapter}/{folder} is not in cm_sequence");
    }

    private static Dictionary<string, Vector3> Positions(IReadOnlyDictionary<string, SurfaceVehicle> vessels)
    {
        var at = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, vessel) in vessels)
        {
            at[name] = vessel.Position;
        }
        return at;
    }

    private static bool Unmoved(Dictionary<string, Vector3> before, IReadOnlyDictionary<string, SurfaceVehicle> vessels)
    {
        foreach (var (name, vessel) in vessels)
        {
            if (!vessel.Position.IsEqualApprox(before[name]))
            {
                return false;
            }
        }
        return true;
    }

    private sealed class SurfaceStepRuntime(SurfaceVehicleRuntime vessels) : ISessionSimulationRuntime
    {
        public bool SimHeld => false;
        public bool EndingHold => false;

        public void StepEndingHold(float dt) { }
        public void CaptureAiAircraft() { }
        public void StepIncomingFire(float dt) { }
        public void StepProjectiles(float dt) { }
        public void StepHumanAircraft(float dt) { }
        public void StepZeppelins(float dt) { }
        public void StepTurretEmplacements(float dt) { }
        public void StepGenerators(float dt) { }
        public void StepSurfaceVehicles(float dt) => vessels.SimStep(dt);
        public void StepCapturedAiAircraft(float dt) { }
        public void StepLandingApproaches() { }
        public void StepInstantAction(float dt) { }
        public void StepCampaign(float dt) { }
        public void StepRadio(float dt) { }
        public void StepSmokeScreens(float dt) { }
        public void StepBeeperTags(float dt) { }
        public void StepAiVoice(float dt) { }
        public void StepVersus(float dt) { }
    }
}
