using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over the mid-mission world behaviours the shipped data drives: the authored
/// <c>flags.active</c> a node starts at whatever its depth, the area-selected node toggle C3's
/// story missions switch their map with, the scripted-path follower that taxis an authored vehicle
/// off a runway and hands it to the flight model, the mission script's hangar door, and the
/// FOG_STATE animation event.</summary>
internal static class WorldFidelitySuites
{
    // The mission whose first objective opens a ground hangar, the definition it wakes, and the
    // door panels that definition slides (the authored 50 m, over 9 and 10 s).
    private const string DoorChapter = "C1";

    private const string DoorMission = "M04";

    private const string DoorAnim = "hangar3_doors";

    private const string DoorHangar = "hangar3";

    private const float DoorTravelM = 50f;

    // The one definition in the install carrying a FOG_STATE, and what its reset block authors.
    private const string FogAnim = "mission_intro_animation";

    private const string FogName = "drop_fog";

    private const float FogGray = 0.69f;

    // The barrage-balloon site whose turret carries an authored-inactive `healthy` variant below a
    // world root, the mission that arms it, and the definition that switches it on.
    private const string ActiveChapter = "C1";

    private const string ActiveMission = "M05";

    private const string ActiveTurret = "bbtur11";

    private const string ActiveVariant = "healthy";

    private const string ActiveAnim = "attack_balloon11";

    // The only chapter authoring the area verb, and the mission that switches the third area off.
    private const string AreaChapter = "C3";

    private const string AreaMission = "M01";

    // The chapter and mission whose roster puts four aircraft on takeoff paths, and the path the
    // first of them is given.
    private const string PathChapter = "C1";

    private const string PathMission = "M04";

    private const string PathName = "pp1";

    private const string PathVehicle = "blakepeace_2_3";

    // The mission and generator whose surface launch flies a ground-roll take-off run.
    private const string RunChapter = "C1";

    private const string RunMission = "M02";

    private const string RunGenerator = "eairg31";

    private static readonly string[] DoorPanels = { "h3_dr1", "h3_dr2", "h3_dr3", "h3_dr4" };

    private static readonly Vector2 FogRange = new(1000f, 1500f);

    private static readonly Vector2 FogAltitude = new(10000f, 11000f);

    // The three rectangles all 25 uses resolve to, as the scripts spell them.
    private static readonly (float X1, float Z1, float X2, float Z2)[] Areas =
    {
        (-10240f, -2048f, -2048f, -6144f),
        (-8192f, -6144f, -2048f, -8192f),
        (-15360f, -7168f, -8192f, -14336f),
    };

    [Suite("campaign-submarine",
        "CM04's authored-inactive barracuda remains built but hidden until sub_movement "
        + "resolves and activates it after the patrol phase")]
    internal static void CampaignSubmarine(TestContext ctx)
    {
        ctx.WithWorld("C3", collision: false, mission: "M03", world =>
        {
            var submarines = world.Runtime.FindNodes("barracuda");
            ctx.Same(1, submarines.Count,
                $"C3/M03 keeps the authored-inactive submarine available to mission choreography");
            if (submarines.Count == 0)
                return;

            var submarine = submarines[0];
            ctx.Check(!submarine.Visible,
                $"the submarine begins hidden until the patrol phase completes");
            var started = world.Runtime.Play("sub_movement");
            ctx.Check(started.Count == 1 && submarine.Visible,
                $"sub_movement resolves its anchor and activates the submarine started={started.Count}");

            string mission = SessionPaths.MissionZrdr(ctx.DataRoot, "C3", "M03");
            var defs = EnemyGenerators.Load(mission);
            var parameters = AiSkills.LoadGeneratorRoster(mission);
            var template = parameters.Count == 1
                ? CampaignRosterPlan.BuildGeneratorTemplate(parameters[0].Name, parameters[0].Fields,
                    VehicleDefs.Load(ctx.ZrdrPath),
                    AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C3")))
                : null;
            ctx.Check(parameters.Count == 1 && parameters[0].Parameter == "BarracudaPlanes"
                && template?.PlaneNode == "player_peacemaker",
                $"BarracudaPlanes resolves the disabled britpeace_5 Peacemaker template");

            EnemyGeneratorDef? launchedDef = null;
            Vector3 launchedAt = default;
            var generators = new AiGeneratorRuntime(defs,
                (name, scope) => world.Runtime.FindNodes(name, scope) is { Count: > 0 } hits
                    ? hits[0] : null,
                AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C3")), ctx.PlaneName,
                (EnemyGeneratorDef def, Vector3 pos, Vector3 look, AiPilot pilot) =>
                {
                    launchedDef = def;
                    launchedAt = pos;
                    return default(LaunchedVehicle);
                });
            generators.SimStep(10f);
            ctx.Check(launchedDef == null, $"the uncredited submarine launches nothing before WAKEUP_GENERATOR");
            ctx.Same(0, generators.GrantWaveCapacity("cargozep1", 4),
                $"an unrelated mission host credits nothing here");
            generators.SimStep(0.01f);
            ctx.Check(launchedDef == null, $"…and that credit releases nothing");
            // The decoded launch point: the first node of the submarine's own take-off path,
            // lifted 0.2 m, and riding the live hull because moving_path keeps it host-relative.
            var runway = First(world.Runtime.FindNodes(
                EnemyGenerators.LaunchPathNode("barracuda", 0), submarine));
            generators.GrantWaveCapacity("barracuda", 4);
            generators.SimStep(0.01f);
            ctx.Check(launchedDef?.VehicleParams == "BarracudaPlanes" && runway != null
                && launchedAt.DistanceTo(runway.GlobalPosition + Vector3.Up * 0.2f) < 0.01f,
                $"the credited launch identifies its template and starts on the hull's take-off path");
            ctx.Check(runway != null && runway.GlobalPosition.DistanceTo(submarine.GlobalPosition) > 1f,
                $"that path point is the deck ahead of the hull's origin, not the origin itself");
        });
    }

    [Suite("world-node-active-depth",
        "a gamez node's flags.active is the built node's starting visibility at EVERY depth, not "
        + "only at a world root: C1's bbtur11/healthy builds switched off while its active parent "
        + "builds shown, and attack_balloon11 still switches the variant on")]
    internal static void WorldNodeActiveDepth(TestContext ctx)
    {
        ctx.WithWorld(ActiveChapter, collision: false, world =>
        {
            if (VariantOf(world, out int turret, out int variant) is not { } names)
            {
                ctx.Check(false, $"C1 ships '{ActiveTurret}/{ActiveVariant}' below a world root");
                return;
            }

            ctx.Check(!names.VariantActive && names.TurretActive,
                $"'{ActiveVariant}' ships inactive under an active '{ActiveTurret}', both under the world root '{names.Root}'");
            var built = world.Runtime.FindNodeByIndex(variant);
            var parent = world.Runtime.FindNodeByIndex(turret);
            ctx.Check(built is { Visible: false },
                $"the authored-inactive variant is built and switched off (built={built != null})");
            ctx.Check(parent is { Visible: true },
                $"its authored-active parent is built and switched on (built={parent != null})");
        });

        ctx.WithWorld(ActiveChapter, collision: false, ActiveMission, world =>
        {
            if (VariantOf(world, out _, out int variant) == null
                || world.Runtime.FindNodeByIndex(variant) is not { } built)
            {
                ctx.Check(false, $"{ActiveChapter}/{ActiveMission} builds the variant");
                return;
            }

            ctx.Check(!built.Visible, $"the mission that arms the site starts it switched off too");
            var started = world.Runtime.Play(ActiveAnim);
            ctx.Check(started.Count == 1 && built.Visible,
                $"'{ActiveAnim}' still switches it on started={started.Count}");
        });
    }

    [Suite("partition-areas",
        "the area-selected node toggle (BL-037) over C3's own three story rectangles: each " +
        "resolves through the partition grid to real world content, the half-open cell rule " +
        "holds (a rectangle inside one cell selects nothing), corner order does not change " +
        "the selection, and C3/M01's built world really has its third area switched off with " +
        "the first left standing")]
    internal static void PartitionAreas(TestContext ctx)
    {
        ctx.WithWorld(AreaChapter, collision: false, AreaMission, world =>
        {
            var grid = WorldPartitionGrid.Of(world.Gamez.FindByName("world1"));
            ctx.Check(grid != null, $"{AreaChapter} carries a usable partition grid");
            if (grid == null)
            {
                return;
            }

            var selections = new List<IReadOnlyList<int>>();
            for (int i = 0; i < Areas.Length; i++)
            {
                var (x1, z1, x2, z2) = Areas[i];
                var span = grid.CellsIn(x1, z1, x2, z2);
                var nodes = grid.NodesIn(x1, z1, x2, z2);
                selections.Add(nodes);
                ctx.Note($"area {i + 1} cells x[{span.ColMin},{span.ColMax}) z[{span.RowMin},{span.RowMax}) -> {nodes.Count} node(s)");
                ctx.Check(nodes.Count > 0, $"area {i + 1} selects world content");
                // The half-open rule: the rectangle's own maximum row and column are excluded, so a
                // rectangle that lands inside one cell selects nothing at all.
                ctx.Check(span.ColMax > span.ColMin && span.RowMax > span.RowMin,
                    $"area {i + 1} spans more than one cell on both axes");
            }

            ctx.Same(0, grid.NodesIn(-5000f, -5000f, -4900f, -4900f).Count,
                $"nodes a rectangle inside one {AreaChapter} cell selects");

            // Corner order is normalised by the engine, and C3 authors the second corner
            // below-left of the first on every one of the three rectangles.
            var (ax1, az1, ax2, az2) = Areas[0];
            ctx.Same(selections[0].Count, grid.NodesIn(ax2, az2, ax1, az1).Count,
                $"nodes area 1 selects with its corners swapped");

            int off = 0, on = 0;
            foreach (int idx in selections[2])
            {
                if (world.Runtime.FindNodeByIndex(idx) is { Visible: false })
                {
                    off++;
                }
            }

            foreach (int idx in selections[0])
            {
                if (world.Runtime.FindNodeByIndex(idx) is { Visible: true })
                {
                    on++;
                }
            }

            ctx.Note($"{AreaMission}: {off} of {selections[2].Count} area-3 node(s) switched off, {on} of {selections[0].Count} area-1 node(s) left on");
            ctx.Check(off > 0, $"{AreaMission}'s area-3 'off' reached built nodes");
            ctx.Check(on > 0, $"{AreaMission} leaves area 1 alone");
        });
    }

    [Suite("scripted-path",
        "the second movement law (BL-361) over C1's authored pp1 takeoff path: a placed " +
        "vehicle sits frozen on its first waypoint however long the mission runs, START_TAXI " +
        "releases it onto a 40 mph taxi, the final leg accelerates past that speed and lifts " +
        "it off the strip, and reaching the final leg's decoded 300 m point hands it back at " +
        "the speed it reached")]
    internal static void ScriptedPathTaxi(TestContext ctx)
    {
        ctx.WithWorld(PathChapter, collision: false, PathMission, world =>
        {
            var path = ScriptedPath.Resolve(PathName, name => world.Runtime.FindNodes(name));
            ctx.Check(path != null, $"{PathChapter} carries the authored path '{PathName}'");
            if (path == null)
            {
                return;
            }

            ctx.Note($"{PathName}: {path.Waypoints.Count} waypoint(s), first {path.Waypoints[0]}, last {path.Waypoints[^1]}");
            var body = new Node3D { Name = "taxi_body" };
            world.Stage.AddChild(body);
            // Off the apron and 200 m up, which is where the roster blocks that author a path put
            // their aeroplane: the placement has to snap it onto waypoint 0, not fly the path from
            // the spawn pose.
            body.GlobalPosition = path.Waypoints[0] + new Vector3(700f, 200f, -400f);
            var registry = new ScriptedPathVehicles(name => world.Runtime.FindNodes(name));
            float handoffSpeed = -1f;
            ctx.Check(registry.Place(PathVehicle, PathName, body, s => handoffSpeed = s),
                $"'{PathVehicle}' is placed on its authored path");
            ctx.Check(body.GlobalPosition.IsEqualApprox(path.Waypoints[0]),
                $"'{PathVehicle}' is snapped onto waypoint 0, not left at its roster pose");
            var firstLeg = path.Waypoints[1] - path.Waypoints[0];
            ctx.Check(Mathf.Abs(Mathf.Wrap(
                    body.GlobalRotation.Y - Mathf.Atan2(-firstLeg.X, -firstLeg.Z),
                    -Mathf.Pi, Mathf.Pi)) < 0.01f,
                $"'{PathVehicle}' faces down the first leg");

            // Placed and waiting: the spawner's freeze holds it on the first waypoint until a
            // mission goal releases it, however long the mission steps.
            var parked = body.GlobalPosition;
            for (int i = 0; i < 60; i++)
            {
                registry.Step(1f / 30f);
            }

            ctx.Check(registry.IsFrozen(PathVehicle), $"'{PathVehicle}' is frozen while placed");
            ctx.Check(body.GlobalPosition.IsEqualApprox(parked),
                $"'{PathVehicle}' does not move while frozen");

            ctx.Check(registry.Release(PathVehicle), $"START_TAXI releases '{PathVehicle}'");
            var before = body.GlobalPosition;
            registry.Step(1f / 30f);
            float rolled = before.DistanceTo(body.GlobalPosition) * 30f;
            ctx.Note($"first released tick: {rolled:F2} m/s of a {PathFollower.TaxiSpeed:F2} m/s taxi");
            ctx.Check(rolled > 0f && rolled <= PathFollower.TaxiSpeed + 0.01f,
                $"'{PathVehicle}' rolls on release, never faster than the taxi speed");

            float peak = 0f, topY = body.GlobalPosition.Y;
            for (int i = 0; i < 6000 && registry.Count > 0; i++)
            {
                var was = body.GlobalPosition;
                registry.Step(1f / 30f);
                peak = Mathf.Max(peak, was.DistanceTo(body.GlobalPosition) * 30f);
                topY = Mathf.Max(topY, body.GlobalPosition.Y);
            }

            ctx.Note($"run: peak {peak:F2} m/s, climbed to y={topY:F1} from y={path.Waypoints[0].Y:F1}, handoff at {handoffSpeed:F2} m/s");
            ctx.Same(0, registry.Count, $"vehicles left on a path '{PathName}' has finished");
            ctx.Check(handoffSpeed > PathFollower.TaxiSpeed,
                $"the final leg hands '{PathVehicle}' over above the taxi speed");
            ctx.Check(peak > PathFollower.TaxiSpeed,
                $"'{PathVehicle}' really moves faster on the final leg than while taxiing");
            ctx.Check(topY > path.Waypoints[0].Y,
                $"the final leg's climb term lifts '{PathVehicle}' off the strip");
            body.QueueFree();
        });
    }

    // A surface generator's launch, from the decoded pose down the whole authored run and into
    // the flight model at the final leg's overshoot point: C1/M02's eairg31 over its five-point
    // eag31 ground roll, judged the instant the hold releases.
    [Suite("generator-takeoff-run",
        "a surface generator's launch flies its take-off run (BL-522) over C1/M02's eairg31: " +
        "the launched aircraft is held on the run from the decoded launch pose, passes every " +
        "eag31_aip point in order at the taxi law's speeds, and at the final leg's decoded " +
        "300 m overshoot point, well past the last point and climbing, is released into the " +
        "flight model well above the taxi speed with the lever open, nose up, its patrol net " +
        "reseated where it arrived")]
    internal static void GeneratorTakeOffRun(TestContext ctx)
    {
        WithGeneratorLaunch(ctx, collision: false, (points, generators, rig) =>
        {
            const float dt = 1f / 60f;
            // Each point in order, judged horizontally against the follower's own 5 m advance.
            var nearest = new float[points.Count];
            Array.Fill(nearest, float.MaxValue);
            int reachedInOrder = 0;
            float peak = 0f, held = 0f;
            for (int i = 0; i < 60 * 120 && rig.Held; i++)
            {
                var was = rig.WorldPosition;
                generators.SimStep(dt);
                if (!rig.Held)
                {
                    break;
                }
                rig.SimStep(dt);
                var now = rig.WorldPosition;
                peak = Mathf.Max(peak, was.DistanceTo(now) / dt);
                held += dt;
                for (int p = 0; p < points.Count; p++)
                {
                    float flat = new Vector2(now.X - points[p].X, now.Z - points[p].Z).Length();
                    nearest[p] = Mathf.Min(nearest[p], flat);
                }
                if (reachedInOrder < points.Count
                    && new Vector2(now.X - points[reachedInOrder].X, now.Z - points[reachedInOrder].Z).Length()
                        <= PathFollower.LegAdvanceDistance + 1f)
                {
                    reachedInOrder++;
                }
            }

            var end = rig.WorldPosition;
            var lastLeg = (points[^1] - points[^2]).Normalized();
            float past = (end - points[^1]).Dot(lastLeg);
            ctx.Note($"run: {held:0.0} s held, peak {peak:0.0} m/s, nearest per point [{string.Join(", ", Array.ConvertAll(nearest, n => n.ToString("0.0")))}] m, points in order {reachedInOrder}/{points.Count}, released {past:0} m past the last point at y={end.Y:0.0} (strip y={points[^1].Y:0.0})");
            ctx.Check(held > 5f && held < 60f, $"the run takes a taxi's while: {held:0.0} s");
            ctx.Same(points.Count, reachedInOrder, $"run points passed in authored order");
            ctx.Check(peak <= PathFollower.TaxiSpeed * 3f,
                $"the run never exceeds a plausible final-leg speed: peak {peak:0.0} m/s");
            ctx.Check(!rig.Held && rig.InPlay, $"the final leg's overshoot point hands the aircraft to the flight model");
            // The decoded handoff point: 300 m along the final leg from the point behind it, so on
            // eag31's 48 m final leg the run flies about 250 m past the last point, climbing.
            float overshoot = PathFollower.FinalLegOvershoot - (points[^1] - points[^2]).Length();
            ctx.Check(Mathf.Abs(past - overshoot) < 10f,
                $"…{overshoot:0} m past the last point, the decoded 300 m from the point behind: {past:0} m");
            ctx.Check(end.Y - points[^1].Y > 20f,
                $"…climbed out above the strip on the final leg: {end.Y - points[^1].Y:0.0} m");
            ctx.Check(rig.WorldVelocity.Length() > PathFollower.TaxiSpeed * 2f,
                $"…well above the taxi speed: {rig.WorldVelocity.Length():0.0} m/s");
            ctx.Check(Mathf.IsEqualApprox(rig.Throttle, 1f),
                $"…with the decoded open lever: {rig.Throttle:0.##}");
            ctx.Check(rig.Pilot?.Patrol is { CurrentIndex: -1 },
                $"…and its patrol net reseated at the release point");
            ctx.Check(rig.NoseDirection.Y > 0.05f && new Vector2(rig.NoseDirection.X, rig.NoseDirection.Z).Normalized()
                    .Dot(new Vector2(lastLeg.X, lastLeg.Z).Normalized()) > 0.9f,
                $"…nose up along the final leg: {rig.NoseDirection}");
        });
    }

    // The same launch on the real airfield with the world's colliders up, flown on for thirty
    // seconds after the hand-off by its own pilot: the launched aircraft must live and climb away
    // from the field, not fly its first turn at ground level into the strip.
    [Suite("generator-launch-climb-out",
        "the same eairg31 launch on C1/M02's real airfield with the world's colliders up, " +
        "flown by its own pilot for 30 s after the hand-off: the Peacemaker is alive with no " +
        "ram and no crash, never came back down to the strip, and ends above the field")]
    internal static void GeneratorLaunchClimbOut(TestContext ctx)
    {
        WithGeneratorLaunch(ctx, collision: true, (points, generators, rig) =>
        {
            const float dt = 1f / 60f;
            bool downed = false;
            rig.Downed += (_, _) => downed = true;
            for (int i = 0; i < 60 * 120 && rig.Held; i++)
            {
                generators.SimStep(dt);
                if (rig.Held)
                {
                    rig.SimStep(dt);
                }
            }
            ctx.Check(!rig.Held && rig.InPlay, $"the run hands the aircraft to the flight model");
            var released = rig.WorldPosition;
            float releaseSpeed = rig.WorldVelocity.Length();
            float strip = points[^1].Y;

            float lowest = released.Y, highest = released.Y, slowest = releaseSpeed;
            int flown = 0;
            for (; flown < 60 * 30 && rig.InPlay && !downed; flown++)
            {
                generators.SimStep(dt);
                rig.SimStep(dt);
                lowest = Mathf.Min(lowest, rig.WorldPosition.Y);
                highest = Mathf.Max(highest, rig.WorldPosition.Y);
                slowest = Mathf.Min(slowest, rig.WorldVelocity.Length());
            }
            var now = rig.WorldPosition;
            ctx.Note($"released at ({released.X:0},{released.Y:0},{released.Z:0}) {releaseSpeed:0.0} m/s; {flown * dt:0.0} s flown: lowest y={lowest:0.0}, highest y={highest:0.0}, slowest {slowest:0.0} m/s, now at ({now.X:0},{now.Y:0},{now.Z:0}) {rig.WorldVelocity.Length():0.0} m/s, mode {rig.Pilot?.Machine?.Mode}");
            ctx.Check(!downed && rig.InPlay && !rig.Crashed,
                $"the launched aircraft is alive 30 s after its release (no ram, no crash)");
            ctx.Check(lowest > strip + 5f,
                $"…and never came back down to the field: lowest y={lowest:0.0} over a strip at y={strip:0.0}");
            ctx.Check(now.Y > strip + 30f,
                $"…flying above the field at the end: y={now.Y:0.0}");
        });
    }


    [Suite("hangar-door-wake",
        "hangar doors over C1/M04's real world (BL-350): OBJECTIVE1's WAKE_ANIM reaches "
        + "'hangar3_doors' through the director at its authored 2 s dormancy and its four "
        + "panels slide their authored 50 m by 12 s; the generator eairg31's own hangar takes "
        + "the loader's node-name door default (eairg_open31, the close resolving the open), "
        + "and its door starts opening the decoded 4 s before the spawn, which lands at that "
        + "hangar")]
    internal static void HangarDoorWake(TestContext ctx)
    {
        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(DoorChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(DoorMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }

        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{DoorChapter}/{DoorMission} is not in cm_sequence");
        }

        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, DoorChapter, DoorMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(DoorChapter, collision: false, DoorMission, world =>
        {
            var runtime = world.Runtime;
            var panels = new List<Node3D>();
            var rest = new List<Vector3>();
            foreach (var name in DoorPanels)
            {
                var hits = runtime.FindNodes(name);
                ctx.Check(hits.Count == 1, $"'{name}' resolves once in {DoorChapter}");
                if (hits.Count == 1)
                {
                    panels.Add(hits[0]);
                    rest.Add(hits[0].Position);
                }
            }

            var hangarHits = runtime.FindNodes(DoorHangar);
            ctx.Check(hangarHits.Count == 1 && panels.Count == DoorPanels.Length,
                $"'{DoorHangar}' and its four panels are in the built world");
            if (hangarHits.Count != 1 || panels.Count != DoorPanels.Length)
            {
                return;
            }

            ctx.Same(0, runtime.AnimStateOf(DoorAnim), $"'{DoorAnim}' state before the mission runs");

            // The generator's own hangar is a different building, opened by the generator's door
            // cycle through the node-name default: eairg31 asks for eairg_open31, which slides
            // the host's ldoor/rdoor 8 m over 4 s, the decoded 4 s door lead.
            var defs = EnemyGenerators.Load(missionZrdr);
            ctx.Check(defs.Count > 0 && defs[0].OpenAnim == EnemyGenerators.DefaultDoorAnim(defs[0].Node)
                      && defs[0].CloseAnim == defs[0].OpenAnim,
                $"an unauthored door pair takes the loader's default name, the close resolving the open");
            var genHost = defs.Count > 0 ? First(runtime.FindNodes(defs[0].Node)) : null;
            var genDoors = new List<Node3D>();
            var genRest = new List<Vector3>();
            foreach (var name in new[] { "ldoor", "rdoor" })
            {
                if (genHost != null && First(runtime.FindNodes(name, genHost)) is { } door)
                {
                    genDoors.Add(door);
                    genRest.Add(door.Position);
                }
            }

            ctx.Check(genHost != null && genDoors.Count == 2,
                $"'{(defs.Count > 0 ? defs[0].Node : "?")}' hosts its own ldoor/rdoor pair");

            // The mission's own generators, with a spawn that records where and when rather than
            // building an aircraft, so the ordering is read off the runtime's own cycle.
            float clock = 0f;
            var spawns = new List<(float At, Vector3 Pos)>();
            var generators = new AiGeneratorRuntime(defs,
                (name, scope) => runtime.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null,
                AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, DoorChapter)), ctx.PlaneName,
                (plane, pos, look, pilot) =>
                {
                    spawns.Add((clock, pos));
                    return default(LaunchedVehicle);
                },
                (name, host) => runtime.PlayWithin(host, name, applyReset: false).Count,
                (name, host) => runtime.StopWithin(host, name));
            ctx.Check(generators.LiveCount > 0, $"{DoorChapter}/{DoorMission} authors live generators");
            // The script's WAKEUP_GENERATOR for this hangar sits deep in the objective chain;
            // granted here in its place, since the door lead and not the credit is under test.
            if (defs.Count > 0)
            {
                ctx.Same(1, generators.GrantWaveCapacity(defs[0].Node, defs[0].WaveSize),
                    $"'{defs[0].Node}' takes one wave of credit in place of its WAKEUP_GENERATOR");
            }

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = runtime,
                Generators = generators,
                Rng = new Random(1),
            });

            const float dt = 1f / 30f;
            float? wokeAt = null, openAt = null, genDoorAt = null;
            for (int i = 0; i < 30 * 30; i++)
            {
                clock += dt;
                director.Step(dt);
                runtime.Advance(dt);
                generators.SimStep(dt);
                if (genDoorAt == null && genDoors.Count == 2 && !AllClosed(genDoors, genRest))
                {
                    genDoorAt = clock;
                }

                // The wake is read off the panels: the definition's instance finishes its
                // dispatch the step it starts, the 9 and 10 s motions running on after it.
                if (wokeAt == null && !AllClosed(panels, rest))
                {
                    wokeAt = clock;
                    string travel = string.Join(", ", PanelTravel(panels, rest));
                    ctx.Note($"'{DoorAnim}' woke by t={clock:0.00} s (state {runtime.AnimStateOf(DoorAnim)}); panels at {travel} m of travel");
                }

                if (openAt == null && AllOpen(panels, rest))
                {
                    openAt = clock;
                }
            }

            string woke = wokeAt?.ToString("0.00") ?? "never";
            string open = openAt?.ToString("0.00") ?? "never";
            string first = spawns.Count > 0 ? spawns[0].At.ToString("0.00") : "none";
            string genDoor = genDoorAt?.ToString("0.00") ?? "never";
            string genTravel = string.Join(", ", PanelTravel(genDoors, genRest));
            ctx.Note($"woke {woke} s, open {open} s, {spawns.Count} spawn(s), first at {first} s; generator door moving from {genDoor} s, panels at {genTravel} m");
            ctx.Check(wokeAt is { } w && w > 1.9f && w < 2.2f,
                $"OBJECTIVE1's WAKE_ANIM starts '{DoorAnim}' at its authored 2 s dormancy, and the panels move from the first step");
            ctx.Check(openAt is { } o && o > 11f && o < 13f,
                $"all four panels have slid their authored {DoorTravelM:0} m by 12 s (the 9 and 10 s motions)");
            ctx.Check(spawns.Count > 0, $"a generator spawned during the 30 s window");
            if (spawns.Count > 0 && openAt is { } opened)
            {
                ctx.Check(spawns[0].At > opened, $"the first spawn ({spawns[0].At:0.0} s) comes after the script's doors are open");
            }

            if (spawns.Count > 0 && genHost != null && genDoorAt is { } genOpened)
            {
                float lead = spawns[0].At - genOpened;
                ctx.Check(lead > GeneratorCycle.DoorLeadSeconds - 0.1f && lead < GeneratorCycle.DoorLeadSeconds + 0.1f,
                    $"the generator's own door starts opening {lead:0.00} s before its spawn (the decoded {GeneratorCycle.DoorLeadSeconds:0} s lead)");
                ctx.Check(genDoors.Count == 2 && genDoors[0].Position.DistanceTo(genRest[0]) > 7.9f,
                    $"the generator's door has slid its authored 8 m by the spawn");
                ctx.Check(spawns[0].Pos.DistanceTo(genHost.GlobalPosition) < 25f,
                    $"the spawn ({spawns[0].Pos}) is at the generator's own hangar, not the script's");
            }

            generators.Free();
        });
    }

    // Which Godot callback feeds the runtime under each clock mode. Read off the runtime's own
    // sim-seconds accumulator, with the clock's BeginFrame run the way GameSession runs it.
    [Suite("anim-clock-realtime",
        "which callback feeds the animation runtime under each clock mode: on a realtime "
        + "session the physics tick advances it by its own dt and the frame's wall delta "
        + "advances nothing, a cutscene hold hands the advance back to the frame, a halt "
        + "advances neither until a queued step, and fixed-step takes exactly one step per "
        + "frame from the frame; a mission-ending hold keeps both callbacks quiet on the "
        + "last flown pose")]
    internal static void AnimClockRealtime(TestContext ctx)
    {
        const float physicsDt = 1f / 60f;
        const float wallDt = 0.5f;
        var stage = new Node3D { Name = "ClockStage" };
        var runtime = new AnimRuntime { AutoStart = false, SoundHandledElsewhere = true };
        var saved = GameClock.Current;
        var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
        GameClock.Current = clock;
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, new AnimProgram());

            clock.BeginFrame(wallDt);
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsZeroApprox(runtime.Elapsed),
                $"realtime: the frame's wall delta advances nothing");
            runtime._PhysicsProcess(physicsDt);
            runtime._PhysicsProcess(physicsDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, 2f * physicsDt),
                $"realtime: each physics tick advances the runtime by its own dt");

            clock.SimHeld = true;
            clock.BeginFrame(wallDt);
            runtime._PhysicsProcess(physicsDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, 2f * physicsDt),
                $"under a cutscene hold the physics tick advances nothing");
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (2f * physicsDt) + wallDt),
                $"and the frame keeps the movie running on wall time");
            clock.SimHeld = false;

            clock.SimHeld = true;
            clock.AuthoredAnimationHeld = true;
            clock.BeginFrame(wallDt);
            runtime._PhysicsProcess(physicsDt);
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (2f * physicsDt) + wallDt),
                $"under an ending hold neither callback advances the authored animation");
            clock.AuthoredAnimationHeld = false;
            clock.SimHeld = false;

            clock.Halted = true;
            clock.BeginFrame(wallDt);
            runtime._PhysicsProcess(physicsDt);
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (2f * physicsDt) + wallDt),
                $"halted: neither callback advances");
            clock.StepOnce();
            clock.BeginFrame(wallDt);
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (3f * physicsDt) + wallDt),
                $"a queued single step advances one fixed step from the frame");
            clock.Halted = false;

            clock.Mode = GameClock.RunMode.FixedStep;
            clock.BeginFrame(wallDt);
            runtime._PhysicsProcess(physicsDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (3f * physicsDt) + wallDt),
                $"fixed-step: the physics tick advances nothing");
            runtime._Process(wallDt);
            ctx.Check(Mathf.IsEqualApprox(runtime.Elapsed, (4f * physicsDt) + wallDt),
                $"fixed-step: the frame advances exactly one fixed step, wall time ignored");
        }
        finally
        {
            GameClock.Current = saved;
            runtime.Free();
            stage.Free();
        }
    }

    [Suite("fog-state",
        "the FOG_STATE animation event (BL-038) over C1/M04's intro definition: a weather "
        + "rig writes the zone's fog at build, playing the intro raises its RESET_STATE fog "
        + "through the runtime's own dispatch, the three fog globals change to the event's "
        + "authored 'drop_fog' values, an event raised before the rig has built lands after "
        + "the zone, and a field the event omits is left as the zone wrote it")]
    internal static void FogStateEvent(TestContext ctx)
    {
        ctx.WithWorld(DoorChapter, collision: false, DoorMission, world =>
        {
            string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, DoorChapter, DoorMission);
            var spec = SessionSpec.Parse(new[] { $"--chapter={DoorChapter}", $"--mission={DoorMission}", "--mute" });
            var sun = new DirectionalLight3D();
            world.Stage.AddChild(sun);
            var rig = new WeatherRig(spec, world.Stage, sun);
            rig.Build(missionZrdr, Array.Empty<PlayerRig>(), Array.Empty<HorizonZone>(), _ => { });
            var zoneRange = rig.FogGlobals.Range;
            var zoneAlt = rig.FogGlobals.Altitude;
            ctx.Note($"zone fog at build: range {zoneRange.X:0}–{zoneRange.Y:0} m, altitude {zoneAlt.X:0}–{zoneAlt.Y:0} m");
            ctx.Check(zoneRange != FogRange && zoneAlt != FogAltitude,
                $"the zone's fog differs from the event's, so a change is observable");

            var stage = new Node3D { Name = "FogStage" };
            var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
            var received = new List<AnimRuntime.FogStateChange>();
            runtime.FogStateSink = fog =>
            {
                received.Add(fog);
                rig.ApplyFogState(fog);
            };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(FogAnim));
                runtime.Play(FogAnim);
                ctx.Same(1, received.Count, $"FOG_STATE events '{FogAnim}' raised through the dispatch");
                if (received.Count == 1)
                {
                    var fog = received[0];
                    ctx.Check(fog.Name == FogName, $"the event's authored name is '{FogName}'");
                    ctx.Check(fog.Range == FogRange && fog.Altitude == FogAltitude,
                        $"the event carries its inline range {FogRange} and altitude {FogAltitude}");
                    ctx.Check(fog.Color is { } c && Mathf.IsEqualApprox(c.R, FogGray),
                        $"the event carries its inline {FogGray} gray");
                }

                var range = rig.FogGlobals.Range;
                var alt = rig.FogGlobals.Altitude;
                var color = rig.FogGlobals.ColorLinear;
                float expectedGray = new Color(FogGray, FogGray, FogGray).SrgbToLinear().R;
                ctx.Note($"after the event: range {range.X:0}–{range.Y:0} m, altitude {alt.X:0}–{alt.Y:0} m, colour {color.X:0.000} (linear)");
                ctx.Check(range == FogRange, $"the fog range global changed to the event's {FogRange}");
                ctx.Check(alt == FogAltitude, $"the fog altitude global changed to the event's {FogAltitude}");
                ctx.Check(Mathf.IsEqualApprox(color.X, expectedGray), $"the fog colour global is the event's gray, in linear");

                // The session's order: the intro's reset block fires inside the world bootstrap,
                // before any weather rig exists, and the zone must not land on top of it.
                var late = new WeatherRig(spec, world.Stage, sun);
                late.ApplyFogState(received[0]);
                ctx.Check(late.FogGlobals.Range == Vector2.Zero, $"an event before the build writes nothing yet");
                late.Build(missionZrdr, Array.Empty<PlayerRig>(), Array.Empty<HorizonZone>(), _ => { });
                ctx.Check(late.FogGlobals.Range == FogRange && late.FogGlobals.Altitude == FogAltitude,
                    $"an event held through the build lands after the zone, not under it");

                // A field the event omits stays as the zone wrote it: the record's dirty bits.
                rig.ApplyFogState(new AnimRuntime.FogStateChange("range_only", null, null, new Vector2(2000f, 2500f)));
                ctx.Check(rig.FogGlobals.Range == new Vector2(2000f, 2500f) && rig.FogGlobals.Altitude == FogAltitude,
                    $"a range-only event leaves the altitude global alone");
            }
            finally
            {
                runtime.Free();
                stage.Free();
                sun.Free();
            }
        });
    }

    private static Node3D? First(IReadOnlyList<Node3D> found) => found.Count > 0 ? found[0] : null;

    // The turret and its authored variant child as gamez indices, plus the world root they hang
    // under, so the suite reports the data it stands on instead of hard-coding node numbers.
    private static (string Root, bool TurretActive, bool VariantActive)? VariantOf(
        TestWorld world, out int turret, out int variant)
    {
        turret = variant = -1;
        if (world.Gamez.FindByName(ActiveTurret) is not { } node)
        {
            return null;
        }

        foreach (int child in node.Children)
        {
            if (child >= 0 && child < world.Gamez.Nodes.Count
                && world.Gamez.Nodes[child].Name.Equals(ActiveVariant, StringComparison.OrdinalIgnoreCase))
            {
                variant = child;
                break;
            }
        }

        if (variant < 0)
        {
            return null;
        }

        turret = node.Index;
        string root = ActiveTurret;
        for (var at = node; at != null; at = ParentOf(world, at))
        {
            root = at.Name;
        }

        return (root, node.Active, world.Gamez.Nodes[variant].Active);
    }

    private static GameZNode? ParentOf(TestWorld world, GameZNode node)
    {
        foreach (var candidate in world.Gamez.Nodes)
        {
            if (candidate.Children.Contains(node.Index))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool AllClosed(List<Node3D> panels, List<Vector3> rest)
    {
        for (int i = 0; i < panels.Count; i++)
        {
            if (panels[i].Position.DistanceTo(rest[i]) > 0.01f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllOpen(List<Node3D> panels, List<Vector3> rest)
    {
        for (int i = 0; i < panels.Count; i++)
        {
            if (panels[i].Position.DistanceTo(rest[i]) < DoorTravelM - 0.1f)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> PanelTravel(List<Node3D> panels, List<Vector3> rest)
    {
        var travel = new List<string>();
        for (int i = 0; i < panels.Count; i++)
        {
            travel.Add(panels[i].Position.DistanceTo(rest[i]).ToString("0.0"));
        }

        return travel;
    }

    // The surface launch rig both suites share: C1/M02 with eairg31 alone on a real FlightRoster,
    // stepped at the sim rate until the uncredited cycle launches, then handed to the body held
    // at the decoded launch pose on its run.
    private static void WithGeneratorLaunch(TestContext ctx, bool collision,
        Action<List<Vector3>, AiGeneratorRuntime, FlightController> body)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, RunChapter);
        ctx.RequireData(texturesPath, $"{RunChapter} textures");
        ctx.WithWorld(RunChapter, collision, RunMission, world =>
        {
            string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, RunChapter, RunMission);
            string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, RunChapter);
            var nets = AiNets.Load(chapterZrdr);
            EnemyGeneratorDef? def = null;
            foreach (var d in EnemyGenerators.Load(missionZrdr))
            {
                if (d.Node.Equals(RunGenerator, StringComparison.OrdinalIgnoreCase))
                {
                    def = d;
                }
            }
            ctx.Check(def != null, $"{RunChapter}/{RunMission} authors the generator '{RunGenerator}'");
            if (def == null)
            {
                return;
            }

            var host = First(world.Runtime.FindNodes(RunGenerator));
            var points = new List<Vector3>();
            for (int i = 0; host != null && i < 16; i++)
            {
                if (First(world.Runtime.FindNodes(EnemyGenerators.LaunchPathNode(RunGenerator, i), host)) is not { } point)
                {
                    break;
                }
                points.Add(point.GlobalPosition);
            }
            ctx.Same(5, points.Count, $"points on '{RunGenerator}'s take-off run");
            if (points.Count < 2)
            {
                return;
            }

            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(texturesPath);
            var pool = new ProjectilePool(textures, null, null);
            ctx.Host.AddChild(pool);
            // Every launch, not the last: a body that flies long enough sees the generator's next
            // credit launch a second aircraft, and a rig left on the shared host keeps its `ai1_`
            // name from the next suite's spawn.
            FlightController? launched = null;
            var spawned = new List<FlightController>();
            try
            {
                var spec = SessionSpec.Parse(Array.Empty<string>());
                var resources = new AircraftAssemblyResources
                {
                    PlanesGamez = planesGamez,
                    StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                    AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                    PaintRng = new RandomNumberGenerator(),
                    ZrdrPath = ctx.ZrdrPath,
                    StockLoadouts = StockLoadouts.Load(),
                    WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                    Textures = textures,
                    Shakes = ShakeDefs.Load(ctx.ZrdrPath),
                };
                var roster = new FlightRoster(FlightRosterPolicy.From(spec),
                    new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                    null, ctx.Host, resources,
                    new FlightWorldBindings { Projectiles = pool, Gamez = planesGamez, ChapterZrdrPath = chapterZrdr },
                    new HumanRosterBindings());
                var generators = new AiGeneratorRuntime(new[] { def },
                    (name, scope) => world.Runtime.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null,
                    nets, ctx.PlaneName,
                    (EnemyGeneratorDef d, Vector3 pos, Vector3 look, AiPilot pilot) =>
                    {
                        var rig = roster.SpawnAi(new AiSpawn(ctx.PlaneName, pos, look, pilot, ShippedSkins: true));
                        spawned.Add(rig);
                        launched ??= rig;
                        return rig;
                    });
                ctx.Same(1, generators.LiveCount, $"'{RunGenerator}' is live with its take-off path");

                // Credited as the mission's WAKEUP_GENERATOR would, the cycle launches on its own
                // period, stepped at the sim rate so the launch frame is an ordinary one.
                generators.GrantWaveCapacity(def.Node, def.WaveSize);
                const float dt = 1f / 60f;
                for (int i = 0; launched == null && i * dt < def.IndPeriod + def.WavePeriod + 1f; i++)
                {
                    generators.SimStep(dt);
                }
                ctx.Check(launched != null, $"the generator launches an aircraft");
                if (launched == null)
                {
                    return;
                }
                var rig = launched;
                ctx.Same(1, generators.RunningCount, $"launches on their take-off run");
                ctx.Check(rig.Held, $"the launch is held on the run, not flight-integrated on the strip");
                ctx.Check(rig.WorldPosition.DistanceTo(points[0] + Vector3.Up * 0.2f) < 0.05f,
                    $"…from the decoded launch pose: {rig.WorldPosition} vs {points[0]}");
                body(points, generators, rig);
                roster.ClearMembership();
            }
            finally
            {
                foreach (var rig in spawned)
                {
                    rig.Free();
                }
                pool.Free();
                textures.Dispose();
            }
        });
    }
}
