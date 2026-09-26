using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites asserting the zeppelins: motion along their nets, fighter launch, multi-zone
/// damage, and the broadside cannons.</summary>
internal static class ZeppelinSuites
{
    [Suite("zeppelin-motion",
        "zeppelin motion (F17): C1/M04's piratezep record loads, its world node is placed at " +
        "the authored pose, which is PirateZep1's node 0 and an ARMED stop point, so it sits " +
        "docked until SetStopPoint releases stop-point id 1, then flown between manual sim " +
        "steps (every hop an EDGE, displacement never over max_speed·dt) until the route's " +
        "far end, armed under the unaddressable id 0, docks it for good; total engine loss " +
        "decelerates it to a stop through the decoded sqrt curve, and a deactivated record " +
        "is placed but held")]
    internal static void ZeppelinMotionSuite(TestContext ctx)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M04");
        ctx.RequireData(missionZrdr, $"C1/M04 zrdr");

        var defs = Zeppelins.Load(missionZrdr);
        ctx.Check(defs.Count == 1 && defs[0].Node == "piratezep",
            $"C1/M04 authors one zeppelin, piratezep count={defs.Count}");
        if (defs.Count != 1)
            return;
        var def = defs[0];

        var nets = AiNets.Load(chapterZrdr);
        var net = AiNets.ByName(nets, def.Net);
        ctx.Check(net != null, $"its net '{def.Net}' resolves in C1's neindex");
        if (net == null)
            return;
        int tagged = 0, armed = 0;
        foreach (var n in net.Nodes)
        {
            if (n.StopPointId > 0)
                tagged++;
            if (n.StopsHere)
                armed++;
        }
        ctx.Check(tagged > 0 && armed > 0,
            $"the route carries stop points, armed in the file ids={tagged} armed={armed} of {net.Nodes.Count} nodes");
        ctx.Check(net.Nodes[0].StopPointId == 1 && net.Nodes[0].StopsHere
            && def.Position.DistanceTo(net.Nodes[0].Position) < 5f,
            $"…and the record spawns ON node 0, which is stop point 1 and armed, so C1/M04's PANDORA starts docked, not flying");

        Node3D? host = null;
        Node3D? heldHost = null;
        ZeppelinRuntime? runtime = null;
        ZeppelinRuntime? heldRuntime = null;
        try
        {
            // A mission zeppelin is a world/anim node; the runtime moves the NODE, no flight
            // model, so a bare Node3D is the exact contract (resolver stands in for the world
            // runtime's FindNodes).
            host = new Node3D { Name = "piratezep" };
            ctx.Host.AddChild(host);
            var resolvedHost = host;
            runtime = new ZeppelinRuntime(defs, name =>
                name.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase) ? resolvedHost : null, nets);
            ctx.Same(1, runtime.LiveCount, $"the zeppelin is live");
            ctx.Check(host.GlobalPosition.DistanceTo(def.Position) < 0.1f,
                $"placed at the authored position at load pos={host.GlobalPosition}");

            var motion = runtime.MotionFor("piratezep");
            ctx.Check(motion != null, $"MotionFor finds the live motion by node name");
            if (motion == null)
                return;

            const float dt = 1f / 60f;

            // Docked: the armed stop point under the spawn holds the hull where it was placed,
            // which is the state OBJECTIVE-side COMPLETED_STOPPOINT releases.
            var docked = host.GlobalPosition;
            for (int i = 0; i < 60 * 60; i++)
                runtime.SimStep(dt);
            ctx.Check(motion.Follower.Holding && motion.Speed == 0f
                && docked.DistanceTo(host.GlobalPosition) < 1f,
                $"a minute on an armed stop point moves it {docked.DistanceTo(host.GlobalPosition):0.##} m, speed={motion.Speed:0.##}");
            ctx.Same(0, motion.Follower.Advances, $"…and the walk never left node 0");

            // COMPLETED_STOPPOINT ["PirateZep1", 1, 0]: the mission's own release.
            ctx.Same(1, runtime.SetStopPoint(def.Net, 1, false),
                $"SetStopPoint('{def.Net}', 1, false) writes the one follower flying it");
            ctx.Same(-1, runtime.MotionFor("piratezep")!.Follower.StopPointNode(9),
                $"…and an id no node carries addresses nothing");

            // Fly until three node captures, checking per-step displacement against the
            // record's own speed limit and every hop against the edge list.
            var hops = new List<(int From, int To)>();
            int last = motion.Follower.CurrentIndex;
            int speedViolations = 0;
            var start = host.GlobalPosition;
            int steps = 0, budget = 60 * 900;
            while (motion.Follower.Advances < 3 && steps < budget)
            {
                steps++;
                var before = host.GlobalPosition;
                runtime.SimStep(dt);
                if (before.DistanceTo(host.GlobalPosition) > (def.MaxSpeed * dt) + 0.01f)
                    speedViolations++;
                if (motion.Follower.CurrentIndex != last)
                {
                    if (last >= 0)
                        hops.Add((last, motion.Follower.CurrentIndex));
                    last = motion.Follower.CurrentIndex;
                }
            }
            ctx.Check(motion.Follower.Advances >= 3,
                $"the node captures 3 net nodes advances={motion.Follower.Advances} in {steps / 60f:0} s of sim");
            ctx.Check(start.DistanceTo(host.GlobalPosition) > 100f,
                $"…moving the world node dist={start.DistanceTo(host.GlobalPosition):0} m");
            bool allEdges = hops.Count > 0;
            foreach (var (from, to) in hops)
            {
                if (!net.Edges.Contains((from, to)) && !net.Edges.Contains((to, from)))
                    allEdges = false;
            }
            ctx.Check(allEdges,
                $"every hop is an EDGE of the graph hops={string.Join(" ", hops.ConvertAll(h => $"{h.From}→{h.To}"))}");
            ctx.Same(0, speedViolations, $"no step ever moved farther than max_speed·dt");

            // Total engine loss: the decoded sqrt curve takes max_speed to 0 (accel keeps its
            // 20 % floor), so the zeppelin decelerates to a stop. This is the seam F18 drives.
            motion.AliveEngines = 0;
            for (int i = 0; i < 60 * 60 && motion.Speed > 0f; i++)
                runtime.SimStep(dt);
            ctx.Check(motion.Speed == 0f,
                $"total engine loss decelerates to a stop speed={motion.Speed:0.##}");
            var stopped = host.GlobalPosition;
            runtime.SimStep(dt);
            ctx.Check(stopped.DistanceTo(host.GlobalPosition) < 1e-3f,
                $"…and the node no longer moves");

            // Engines back, and the route's far end holds it for good: node 4 is armed but carries
            // stop-point id 0, the "no stop point" id the script side refuses, so nothing can
            // release it. An open path therefore ENDS at its dock instead of shuttling back.
            motion.AliveEngines = motion.TotalEngines;
            for (int i = 0; i < 60 * 600 && !motion.Follower.Holding; i++)
                runtime.SimStep(dt);
            // The hold engages while the last of the way bleeds off at max_accel; give it that.
            for (int i = 0; i < 60 * 5; i++)
                runtime.SimStep(dt);
            ctx.Check(motion.Follower.Holding && motion.Follower.CurrentIndex == net.Nodes.Count - 1
                && motion.Speed == 0f,
                $"the far end holds it for good node={motion.Follower.CurrentIndex} id={net.Nodes[^1].StopPointId} speed={motion.Speed:0.##}");
            ctx.Same(-1, motion.Follower.SetStopPoint(0, false),
                $"…and stop-point id 0 addresses no node, so no script can release that dock");

            // A deactivated record is placed at its pose but held (mission script would wake
            // it; out of M4 scope).
            var held = new ZeppelinDef
            {
                Node = "heldzep",
                Position = new Vector3(500f, 640f, -500f),
                YawDeg = 90f,
                MaxSpeed = def.MaxSpeed,
                MaxAccel = def.MaxAccel,
                AccelPitchDeg = def.AccelPitchDeg,
                AccelYawDeg = def.AccelYawDeg,
                MaxRateYawDeg = def.MaxRateYawDeg,
                MaxRatePitchDeg = def.MaxRatePitchDeg,
                MinPitchDeg = def.MinPitchDeg,
                MaxPitchDeg = def.MaxPitchDeg,
                Net = def.Net,
                Targets = System.Array.Empty<string>(),
                Healthy = System.Array.Empty<ZeppelinHealthyZone>(),
                NumHealthyRequired = 1,
                Engines = System.Array.Empty<string>(),
                Gasbags = System.Array.Empty<ZeppelinGasbag>(),
                LeftCannons = System.Array.Empty<ZeppelinCannon>(),
                RightCannons = System.Array.Empty<ZeppelinCannon>(),
                CannonHealth = System.Array.Empty<ZeppelinCannonHealth>(),
                Deactivated = true,
            };
            heldHost = new Node3D { Name = "heldzep" };
            ctx.Host.AddChild(heldHost);
            var resolvedHeld = heldHost;
            heldRuntime = new ZeppelinRuntime(new[] { held }, _ => resolvedHeld, nets);
            for (int i = 0; i < 60; i++)
                heldRuntime.SimStep(dt);
            ctx.Check(heldHost.GlobalPosition.DistanceTo(held.Position) < 1e-3f,
                $"a deactivated record is placed but held pos={heldHost.GlobalPosition}");
        }
        finally
        {
            runtime?.Free();
            heldRuntime?.Free();
            host?.Free();
            heldHost?.Free();
        }
    }

    // CM08 (C1B/M03, "the Pandora"): Klondike1 is an open 13-node chain carrying two armed
    // stops, ids 7 (node 5) and 8 (node 7, the CARGO POINT over the freighter). Its far end,
    // node 0, authors no stop point at all, unlike C1/M04's PirateZep1 above, whose far end is
    // armed under an unaddressable id, so one walk of this route proves all three rules: an
    // armed stop settles the hull ON its node, the cargo sequence starts from that stop, and a
    // bare end still parks the airship instead of shuttling its 150-to-400 m altitude swing.
    [Suite("zeppelin-pandora-dead-end",
        "CM08's piratezep on its own Klondike1 chain: the file's stop 7 (node 5) settles the " +
        "hull ON the node rather than a hold distance short of it, releasing it (what " +
        "OBJECTIVE11 does) carries the Pandora to the cargo point over the freighter (stop 8, " +
        "node 7), where the mission's own zepgetcargo (the freighter's hold doors, the " +
        "Pandora's cargo doors, the crane on its chain) starts; releasing that too runs the " +
        "chain to node 0, an open end with NO stop point authored at all, where the structural " +
        "dead-end hold parks it for good; the dock glide leaves the hull level at both stops, " +
        "and the pitch never rings up on the way")]
    internal static void ZeppelinPandoraDeadEndSuite(TestContext ctx)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1B");
        ctx.RequireData(chapterZrdr, $"C1B zrdr");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1B", "M03");
        ctx.RequireData(missionZrdr, $"C1B/M03 zrdr");

        var defs = Zeppelins.Load(missionZrdr);
        ZeppelinDef? def = null;
        foreach (var d in defs)
        {
            if (d.Node.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase))
                def = d;
        }
        ctx.Check(def != null && def.Net == "Klondike1",
            $"C1B/M03 authors piratezep on Klondike1 net={def?.Net}");
        if (def == null)
            return;

        var nets = AiNets.Load(chapterZrdr);
        var net = AiNets.ByName(nets, def.Net);
        ctx.Check(net != null, $"'{def.Net}' resolves in C1B's neindex");
        if (net == null)
            return;
        ctx.Check(net.Nodes.Count == 13 && net.Nodes[0].StopPointId == 0,
            $"the far end (node 0) authors no stop point at all count={net.Nodes.Count} id={net.Nodes[0].StopPointId}");
        // The chain is 3-4-5-6-7-8-9-10-11-12-2-1-0: piratezep's own authored position seats it
        // on node 3 (the NEAR end, 50 m off in altitude only), so node 0, not the last-indexed
        // node 12, which is a degree-2 waypoint mid-chain, is the far end it walks the whole
        // route to reach.
        const int farEnd = 0;
        const int cargoNode = 7;
        ctx.Check(net.Nodes[5].StopPointId == 7 && net.Nodes[5].StopsHere
            && net.Nodes[cargoNode].StopPointId == 8 && net.Nodes[cargoNode].StopsHere,
            $"the file arms exactly stops 7 (node 5) and 8 (node {cargoNode}, the cargo point)");

        Node3D? host = null;
        ZeppelinRuntime? runtime = null;
        try
        {
            host = new Node3D { Name = "piratezep" };
            ctx.Host.AddChild(host);
            var resolvedHost = host;
            runtime = new ZeppelinRuntime(new[] { def }, name =>
                name.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase) ? resolvedHost : null, nets);
            var motion = runtime.MotionFor("piratezep");
            ctx.Check(motion != null, $"MotionFor finds the live motion");
            if (motion == null)
                return;

            // The steepest leg of the chain. The law pitches at the slope from the HULL to the
            // node, over the edge's own while a levelled hull lags below a climbing leg. The
            // margin below is that transient, not slack; a ring-up would sit at the ±30° band.
            float steepest = 0f;
            foreach (var (a, b) in net.Edges)
            {
                var d = net.Nodes[b].Position - net.Nodes[a].Position;
                steepest = Mathf.Max(steepest, Mathf.Abs(Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
            }
            const float dt = 1f / 60f;
            float worstPitch = 0f;

            // Leg one, under the file's own arming: the first armed stop the walk meets. The
            // hold latches on the hold sphere and the dock glide then settles the hull onto the
            // node, so the resting pose is read after it, not at the moment the walk stops.
            int steps = FlyUntilHold(runtime, motion, 5, ref worstPitch, 60 * 300);
            Settle(runtime, dt);
            float shortOf = motion.Position.DistanceTo(net.Nodes[5].Position);
            ctx.Check(motion.Follower.Holding && motion.Follower.CurrentIndex == 5 && shortOf < 1f,
                $"stop 7 settles the hull ON node 5 in {steps / 60f:0} s, {shortOf:0.##} m off it, not a hold distance short");
            ctx.Check(Mathf.Abs(Mathf.RadToDeg(motion.PitchRad)) < 0.5f,
                $"…and level, which the dock glide does and the speed-scaled steer law cannot: pitch={Mathf.RadToDeg(motion.PitchRad):0.00}°");

            // OBJECTIVE11's own COMPLETED_STOPPOINT ["Klondike1", 7, 0] releases it onward.
            ctx.Same(1, runtime.SetStopPoint(def.Net, 7, false), $"stop 7 (node 5) releases");
            steps = FlyUntilHold(runtime, motion, cargoNode, ref worstPitch, 60 * 300);
            Settle(runtime, dt);
            float offCargo = motion.Position.DistanceTo(net.Nodes[cargoNode].Position);
            ctx.Check(motion.Follower.Holding && motion.Follower.CurrentIndex == cargoNode && offCargo < 1f,
                $"…and stop 8 settles it ON the cargo point, node {cargoNode}, in {steps / 60f:0} s, {offCargo:0.##} m off it");
            ctx.Check(Mathf.Abs(motion.Position.Y - net.Nodes[cargoNode].Position.Y) < 1f,
                $"…at the node's own altitude, which a crane lowering 54 m of chain needs: y={motion.Position.Y:0.#} against {net.Nodes[cargoNode].Position.Y:0.#}");
            ctx.Check(Mathf.Abs(Mathf.RadToDeg(motion.PitchRad)) < 0.5f,
                $"…and level over it, so the chain hangs off a level hull: pitch={Mathf.RadToDeg(motion.PitchRad):0.00}°");

            // OBJECTIVE21/22/23's ["Klondike1", 8, 0]: the rest of the chain to the bare end.
            ctx.Same(1, runtime.SetStopPoint(def.Net, 8, false), $"stop 8 (node {cargoNode}) releases");
            steps = FlyUntilHold(runtime, motion, farEnd, ref worstPitch, 60 * 900);
            ctx.Check(worstPitch <= steepest + Mathf.DegToRad(4f),
                $"pitch stayed inside the steepest leg's {Mathf.RadToDeg(steepest):0.#}° and the departure lag, worst {Mathf.RadToDeg(worstPitch):0.#}°, nowhere near a ring-up");
            ctx.Check(motion.Follower.Holding && motion.Follower.CurrentIndex == farEnd,
                $"the bare far end (node {farEnd}) holds it in {steps / 60f:0} s idx={motion.Follower.CurrentIndex} holding={motion.Follower.Holding}");
            ctx.Check(motion.Follower.Advances == net.Nodes.Count - 1,
                $"…having walked every edge from the near end, node 3 advances={motion.Follower.Advances}");

            // Held for good: no shuttle back toward node 4, ever.
            for (int i = 0; i < 60 * 20; i++)
                runtime.SimStep(dt);
            ctx.Same(farEnd, motion.Follower.CurrentIndex,
                $"…and stays there, no shuttle back along the altitude swing it just flew");
            ctx.Check(motion.Speed == 0f, $"…with the throttle cut speed={motion.Speed:0.##}");
        }
        finally
        {
            runtime?.Free();
            host?.Free();
        }

        ctx.WithWorld("C1B", collision: false, "M03", world => CheckCargoSequence(ctx, world, net, cargoNode));
    }

    // CM04 (C3/M03): NEW_GAME_START runs pzep_todrydock, one ObjectMotionSiScript on piratezep
    // whose 185-frame script flies the Pandora from (-11314,554,-13697) to the record's own seat,
    // node 0 of M3PirateZep. The record is where the script ENDS: the script owns the pose from
    // its first frame, the follower parks under it and resumes from its last frame.
    [Suite("zeppelin-scripted-pose",
        "a zeppelin under an ObjectMotionSiScript: CM04's pzep_todrydock owns piratezep's " +
        "pose from its frame 0 over C3/M03's built world, the zeppelin runtime places " +
        "nothing over it and its follower parks (never stepped, no frame jumps, never at the " +
        "record seat) until the script ends 61.65 s later ON the record seat, where the " +
        "follower resumes from the script's last frame and holds node 0's armed stop point")]
    internal static void ZeppelinScriptedPoseSuite(TestContext ctx)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C3");
        ctx.RequireData(chapterZrdr, $"C3 zrdr");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C3", "M03");
        ctx.RequireData(missionZrdr, $"C3/M03 zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, "C3"), $"C3 textures");

        ZeppelinDef? def = null;
        foreach (var d in Zeppelins.Load(missionZrdr))
        {
            if (d.Node.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase))
                def = d;
        }
        ctx.Check(def != null && def.Net == "M3PirateZep",
            $"C3/M03 authors piratezep on M3PirateZep net={def?.Net}");
        if (def == null)
            return;
        var nets = AiNets.Load(chapterZrdr);
        var net = AiNets.ByName(nets, def.Net);
        ctx.Check(net != null && net.Nodes[0].Position.DistanceTo(def.Position) < 1f && net.Nodes[0].StopsHere,
            $"the record seats on node 0, an armed stop point: the dock the script ends at");
        if (net == null)
            return;

        ctx.WithWorld("C3", collision: false, "M03", world => DriveScriptedPose(ctx, world, def, nets));
        ctx.Note($"C3/M03: pzep_todrydock owns piratezep's pose for 61.65 s from its frame 0, the follower parks under it and resumes at the record seat");
    }

    [Suite("zeppelin-launch",
        "zeppelin fighter launch (F20): C1/IA1's zeppelin-launch generator authors the " +
        "decoded shape (cargobay origin, −90° drop, mp1 door anims, both shipped as " +
        "compiled OnCall defs over door_left/door_right), holds below the 100 m gate with " +
        "the door shut, and on F17's flown zeppelin opens the door, holds the overdue drop " +
        "for the decoded door lead, then drops fighters at " +
        "the origin node's LIVE position on the composed 7 s schedule, the fast cycle " +
        "leaving the hangar open (close early only past an 8 s gap), while a max_active 1 " +
        "clone stops after one live spawn")]
    internal static void ZeppelinLaunch(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(missionZrdr, $"C1/IA1 zrdr");

        // The authored generator: the decoded zeppelin-launch shape, doors named explicitly.
        var egen = EnemyGenerators.Load(missionZrdr);
        ctx.Check(egen.Count == 1 && egen[0].IsZeppelin,
            $"C1/IA1 authors one zeppelin-launch generator count={egen.Count}");
        if (egen.Count != 1)
            return;
        var def = egen[0];
        ctx.Check(def.Node == "multiplayer1zep" && def.Origin == "cargobay"
            && def.OpenAnim == "mp1_open_doors" && def.CloseAnim == "mp1_close_doors"
            && def.MinAltitude is 100f,
            $"…on multiplayer1zep: cargobay origin, mp1 door anims, 100 m gate");
        ctx.Check(def.RotationDeg is { } rot && Mathf.Abs(rot.X + 90f) < 0.01f,
            $"the authored drop attitude is pitch −90° rot={def.RotationDeg}");
        ctx.Check(Mathf.Abs(def.IndPeriod + def.WavePeriod - 7f) < 0.01f,
            $"the composed spawn gap is ind 5 + wave 2 = 7 s");

        // What the model ships for doors: the compiled mis_anim carries both OnCall defs,
        // each moving the hull's door_left/door_right nodes (this is the visual F20 wires).
        var (_, missionAnim) = AnimProgram.ArchivePaths(ctx.DataRoot, "C1", "IA1");
        var archive = AnimArchive.Load(missionAnim, "mis_anim");
        ctx.Check(archive != null, $"C1/IA1's compiled mis_anim archive loads");
        if (archive == null)
            return;
        foreach (var animName in new[] { def.OpenAnim!, def.CloseAnim! })
        {
            AnimDefinition? doorDef = null;
            foreach (var d in archive.Defs)
            {
                if (string.Equals(d.AnimName, animName, System.StringComparison.OrdinalIgnoreCase))
                    doorDef = d;
            }
            var movedNodes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (doorDef != null)
                foreach (var seq in doorDef.Sequences)
                    foreach (var ev in seq.Events)
                        if (ev.Data.Str("name") is { Length: > 0 } target)
                            movedNodes.Add(target);
            ctx.Check(doorDef != null && movedNodes.Contains("door_left") && movedNodes.Contains("door_right"),
                $"'{animName}' ships as a compiled OnCall def over the hull's door nodes nodes=[{string.Join(",", movedNodes)}]");
        }

        var nets = AiNets.Load(chapterZrdr);
        var zepDefs = new List<ZeppelinDef>();
        foreach (var z in Zeppelins.Load(missionZrdr))
        {
            if (z.Node.Equals(def.Node, System.StringComparison.OrdinalIgnoreCase))
                zepDefs.Add(z);
        }
        ctx.Check(zepDefs.Count == 1 && zepDefs[0].Position.Y >= 100f,
            $"the host zeppelin record exists and its authored altitude clears the gate y={(zepDefs.Count > 0 ? zepDefs[0].Position.Y : 0):0}");
        if (zepDefs.Count != 1)
            return;

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        var spawned = new List<FlightController>();
        var spawnPositions = new List<Vector3>();
        var spawnLooks = new List<Vector3>();
        var animPlays = new List<string>();
        Node3D? host = null;
        Node3D? host2 = null;
        AiGeneratorRuntime? gens = null;
        AiGeneratorRuntime? capped = null;
        ZeppelinRuntime? zeps = null;
        try
        {
            // The world stand-in: the host node with the cargobay drop point riding under the
            // hull, exactly the parent/child shape the chapter gamez builds.
            host = new Node3D { Name = "multiplayer1zep" };
            var cargobay = new Node3D { Name = "cargobay", Position = new Vector3(0f, -20f, 0f) };
            host.AddChild(cargobay);
            ctx.Host.AddChild(host);
            var resolvedHost = host;
            var resolvedBay = cargobay;

            FlightController? SpawnPlane(EnemyGeneratorDef generator, Vector3 pos, Vector3 look,
                AiPilot pilot)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var c = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    PlayerIndex = FlightRoster.ShooterIdBase + spawned.Count,
                    IsHumanPiloted = false,
                    Pilot = pilot,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                c.AddChild(model);
                c.Setup(new FlightModel(stats), null, new CamParams(), pos, look);
                ctx.Host.AddChild(c);
                spawned.Add(c);
                spawnPositions.Add(pos);
                spawnLooks.Add(look);
                return c;
            }

            gens = new AiGeneratorRuntime(new[] { def },
                (name, scope) => name.Equals("multiplayer1zep", System.StringComparison.OrdinalIgnoreCase)
                    ? resolvedHost
                    : name.Equals("cargobay", System.StringComparison.OrdinalIgnoreCase) ? resolvedBay : null,
                nets, ctx.PlaneName, (d, p, l, pi) => SpawnPlane(d, p, l, pi),
                (name, _) => { animPlays.Add(name); return 1; }, (_, _) => { });
            ctx.Same(1, gens.LiveCount, $"the generator is live (host + IAZep net resolve)");
            ctx.Same(1, gens.GrantWaveCapacity("multiplayer1zep", 99),
                $"credited, so the budget never enters the timing below");

            // Held below the gate: the unplaced host sits at y −20/0. Ten seconds of sim spawn
            // nothing and never open the door (the blocked branch only ever CLOSES it).
            const float dt = 1f / 60f;
            for (int i = 0; i < 600; i++)
                gens.SimStep(dt);
            ctx.Check(spawned.Count == 0 && animPlays.Count == 0,
                $"held below the 100 m gate: no spawn, door shut spawns={spawned.Count} plays={animPlays.Count}");

            // F17 places and flies the zeppelin; the gate releases. The held spawn is overdue
            // (timer 10 s > the 7 s threshold), so the first unblocked step opens the door and
            // the drop follows the lead behind it rather than passing through shut panels.
            zeps = new ZeppelinRuntime(zepDefs, name =>
                name.Equals("multiplayer1zep", System.StringComparison.OrdinalIgnoreCase) ? resolvedHost : null, nets);
            ctx.Same(1, zeps.LiveCount, $"the zeppelin is placed and flying (F17)");
            for (int i = 0; i < 120; i++)
                zeps.SimStep(dt);   // two seconds aloft: the hull is moving before any drop
            ctx.Check(host.GlobalPosition.DistanceTo(zepDefs[0].Position) > 1f,
                $"…and has left its authored pose dist={host.GlobalPosition.DistanceTo(zepDefs[0].Position):0.#} m");

            gens.SimStep(dt);
            ctx.Check(spawned.Count == 0, $"the first step at altitude drops nobody spawns={spawned.Count}");
            ctx.Check(animPlays.Count == 1 && animPlays[0] == "mp1_open_doors",
                $"…it opens the door with the authored anim plays=[{string.Join(",", animPlays)}]");
            int leadSteps = 0;
            while (spawned.Count < 1 && leadSteps < 60 * 8)
            {
                leadSteps++;
                zeps.SimStep(dt);
                gens.SimStep(dt);
            }
            float heldFor = leadSteps / 60f;
            ctx.Check(spawned.Count == 1 && heldFor > GeneratorCycle.DoorLeadSeconds - 0.1f
                && heldFor < GeneratorCycle.DoorLeadSeconds + 0.1f,
                $"…and the held fighter drops {heldFor:0.00} s behind it, the decoded {GeneratorCycle.DoorLeadSeconds:0} s lead");
            ctx.Check(spawnPositions.Count == 1
                && spawnPositions[0].DistanceTo(cargobay.GlobalPosition) < 0.1f,
                $"the fighter dropped at the origin node's LIVE position (riding the moving hull) pos={spawnPositions[0]} bay={cargobay.GlobalPosition}");
            ctx.Check(spawnLooks.Count == 1 && (spawnLooks[0] - spawnPositions[0]).Normalized().Y < -0.9f,
                $"…in the authored drop attitude (pitch −90°, clamped shy of vertical) dropY={(spawnLooks[0] - spawnPositions[0]).Normalized().Y:0.##}");

            // The fast cycle: the next spawn is 7 s away, never more than 8, so the door stays
            // open through the second drop (no close anim, no second open).
            var firstPos = spawnPositions[0];
            int steps = 0;
            while (spawned.Count < 2 && steps < 60 * 12)
            {
                steps++;
                zeps.SimStep(dt);
                gens.SimStep(dt);
            }
            ctx.Check(spawned.Count == 2, $"the second fighter drops on the 7 s composed schedule t=+{steps / 60f:0.#} s");
            ctx.Check(animPlays.Count == 1,
                $"the hangar stayed open across it (close early only past an 8 s gap) plays=[{string.Join(",", animPlays)}]");
            ctx.Check(spawnPositions.Count == 2 && spawnPositions[1].DistanceTo(firstPos) > 5f,
                $"…again at the live drop point, which has flown on dist={spawnPositions[1].DistanceTo(firstPos):0.#} m");

            // max_active: a well-credited clone capped at 1 live spawn blocks after its first
            // drop (wave_size − spawned + active > max_active) for as long as it lives.
            var cappedDef = new EnemyGeneratorDef
            {
                Node = def.Node,
                VehicleParams = def.VehicleParams,
                Nets = def.Nets,
                Capacity = def.Capacity,
                MaxActive = 1,
                WaveSize = def.WaveSize,
                WavePeriod = def.WavePeriod,
                IndPeriod = def.IndPeriod,
                IsZeppelin = def.IsZeppelin,
                OpenAnim = def.OpenAnim,
                CloseAnim = def.CloseAnim,
                Origin = def.Origin,
                RotationDeg = def.RotationDeg,
                MinAltitude = def.MinAltitude,
            };
            host2 = new Node3D { Name = "multiplayer1zep", Position = new Vector3(0f, 500f, 0f) };
            ctx.Host.AddChild(host2);
            var resolvedHost2 = host2;
            int before = spawned.Count;
            capped = new AiGeneratorRuntime(new[] { cappedDef },
                (name, scope) => name.Equals("multiplayer1zep", System.StringComparison.OrdinalIgnoreCase)
                    ? resolvedHost2 : null,
                nets, ctx.PlaneName, (d, p, l, pi) => SpawnPlane(d, p, l, pi), (name, _) => 1, (_, _) => { });
            capped.GrantWaveCapacity("multiplayer1zep", 99);
            for (int i = 0; i < 60 * 30; i++)
                capped.SimStep(dt);
            ctx.Same(1, spawned.Count - before,
                $"max_active 1 allows exactly one live spawn in 30 s");
        }
        finally
        {
            gens?.Free();
            capped?.Free();
            zeps?.Free();
            foreach (var c in spawned)
                c.Free();
            host?.Free();
            host2?.Free();
            textures.Dispose();
        }
    }

    // The multi-zone damage chain on a real mission zeppelin, in the mission's own world (C1/M04, where
    // piratezep is live; the run's default IA1 switches it off). Real rounds prove the pipeline, then
    // the bulk gasbag kills go through runtime.DamageAt directly, the same sink minus the flight time.
    // ⚠ Aim rounds at the gasbag collider's BUILT pose, captured before ZeppelinRuntime places the
    // node: a moved physics body never re-enters the space queries inside one frame (INSTR-13).
    [Suite("zeppelin-damage",
        "multi-zone zeppelin damage (F18) on C1/M04's piratezep in its own mission world: " +
        "gasbag pools seeded from the record (120 over the def-less 0), engines from their " +
        "compiled defs (40), a no-DAMAGES_ZEPPELIN gun round strikes a gasbag and is refused " +
        "while a DAMAGES_ZEPPELIN round spends real hp, an engine kill slows the zeppelin " +
        "through the F17 sqrt seam, and the survivor threshold kills with the decoded " +
        "polarity (dead at survivors 3 < required 4, NOT at the design's destroy count), " +
        "playing the authored all_pzep_gasbags death and stopping the motion")]
    internal static void ZeppelinDamageSuite(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M04");
        ctx.RequireData(missionZrdr, $"C1/M04 zrdr");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        // Data-driven picks: the first flagged weapon (wep_14/wep_28 ship the flag), a
        // blast-less one preferred so its damage lands on exactly the struck pool, and the
        // first unflagged gun.
        WeaponDef? zepWeapon = weapons.All.FirstOrDefault(w =>
                w.DamagesZeppelin && w.HealthDamage is > 0f && w.ImpactProximity is not > 0f)
            ?? weapons.All.FirstOrDefault(w => w.DamagesZeppelin && w.HealthDamage is > 0f);
        WeaponDef? gun = weapons.All.FirstOrDefault(w =>
            w.IsGun && !w.DamagesZeppelin && w.HealthDamage is > 0f);
        ctx.Check(zepWeapon != null, $"a DAMAGES_ZEPPELIN weapon with HEALTH_DAMAGE ships");
        ctx.Check(gun != null, $"a gun without DAMAGES_ZEPPELIN ships");
        if (zepWeapon == null || gun == null)
            return;

        ctx.WithWorld("C1", collision: true, mission: "M04", world =>
        {
            var runtime = world.Session.Runtime;
            var defs = Zeppelins.Load(missionZrdr);
            ctx.Check(defs.Count == 1 && defs[0].Node == "piratezep",
                $"C1/M04 authors one zeppelin, piratezep count={defs.Count}");
            if (defs.Count != 1)
                return;
            var def = defs[0];
            var host = runtime.FindNodes("piratezep").FirstOrDefault();
            ctx.Check(host != null, $"the piratezep world node resolves in the M04 world");
            if (host == null)
                return;

            // The built pose, BEFORE ZeppelinRuntime moves the node (INSTR-13, see summary).
            var bagNode = runtime.FindNodes("gasbag1", host).FirstOrDefault();
            var engNode = runtime.FindNodes(def.Engines[0], host).FirstOrDefault();
            ctx.Check(bagNode != null && engNode != null,
                $"gasbag1 and {def.Engines[0]} resolve under the piratezep subtree");
            if (bagNode == null || engNode == null)
                return;
            var builtBagPos = bagNode.GlobalPosition;

            var nets = AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C1"));
            ZeppelinRuntime? zeps = null;
            ProjectilePool? pool = null;
            TextureArchive? textures = null;
            var started = new List<string>();
            try
            {
                zeps = new ZeppelinRuntime(defs,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, nets);
                zeps.WireDamage(runtime);
                var motion = zeps.MotionFor("piratezep")!;

                // Seeding: the gasbag pool carries the RECORD's 120 hp (its def has HEALTH 0),
                // the engine pool its compiled def's 40, record where authored, def where not.
                var bagPool = runtime.Destructibles.PoolsOn(bagNode).FirstOrDefault();
                var engPool = runtime.Destructibles.PoolsOn(engNode).FirstOrDefault();
                ctx.Check(bagPool != null && Mathf.IsEqualApprox(bagPool.MaxHealth, 120f),
                    $"gasbag1's pool is seeded from the record hp={bagPool?.MaxHealth ?? -1f} (authored 120)");
                ctx.Check(engPool != null && Mathf.IsEqualApprox(engPool.MaxHealth, 40f),
                    $"{def.Engines[0]}'s pool keeps its compiled def HEALTH hp={engPool?.MaxHealth ?? -1f} (authored 40)");
                ctx.Same(def.Healthy.Count, zeps.SurvivorsOf("piratezep"),
                    $"all {def.Healthy.Count} healthy entries start alive");
                if (bagPool == null || engPool == null)
                    return;

                // A live pool with the gate wired, damage routed exactly as flight wires it.
                int gateRefusals = 0;
                textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C1"));
                pool = new ProjectilePool(textures, null, null)
                {
                    DamageSink = runtime.DamageAt,
                };
                pool.WorldDamageGate = (struckNode, weapon) =>
                {
                    bool allowed = zeps.GateWeaponDamage(struckNode, weapon);
                    if (!allowed)
                        gateRefusals++;
                    return allowed;
                };
                ctx.Host.AddChild(pool);

                // The muzzle 80 m from the gasbag's BUILT pose, aimed straight at it.
                var muzzlePos = builtBagPos + new Vector3(0f, 80f, 0f);
                var aim = (builtBagPos - muzzlePos).Normalized();
                var muzzle = new Transform3D(Basis.LookingAt(aim, Vector3.Right), muzzlePos);

                // 1. The unflagged gun: the round strikes the gasbag, the gate refuses the
                // damage, the pool is untouched.
                float before = bagPool.Health;
                pool.Spawn(gun, muzzle, Vector3.Zero);
                for (int i = 0; i < 180 && gateRefusals == 0; i++)
                    pool.SimStep(1f / 60f);
                ctx.Check(gateRefusals > 0,
                    $"the {gun.Id} round STRUCK the gasbag and was refused by the gate refusals={gateRefusals}");
                ctx.Check(Mathf.IsEqualApprox(bagPool.Health, before),
                    $"…and gasbag hp is untouched hp={bagPool.Health:0.##}");

                // 2. The DAMAGES_ZEPPELIN weapon: the same geometry spends real hp.
                pool.Spawn(zepWeapon, muzzle, Vector3.Zero);
                for (int i = 0; i < 180 && Mathf.IsEqualApprox(bagPool.Health, before); i++)
                    pool.SimStep(1f / 60f);
                ctx.Check(bagPool.Health <= before - zepWeapon.HealthDamage!.Value + 0.01f,
                    $"the {zepWeapon.Id} round spends its HEALTH_DAMAGE hp {before:0.##}→{bagPool.Health:0.##}");

                // 3. An engine kill drives the F17 sqrt seam: fewer alive engines, lower cap.
                runtime.OnInstanceStarted = (d, _) => { if (d.AnimName != null) started.Add(d.AnimName); };
                float capBefore = motion.EffectiveMaxSpeed;
                runtime.DamageAt(engNode, engPool.MaxHealth + 1f);
                zeps.SimStep(1f / 60f);
                ctx.Same(motion.TotalEngines - 1, motion.AliveEngines,
                    $"the destroyed engine leaves the alive count");
                ctx.Check(motion.EffectiveMaxSpeed < capBefore,
                    $"…and the sqrt curve lowers the speed cap {capBefore:0.##}→{motion.EffectiveMaxSpeed:0.##} m/s");

                // 4. The survivor threshold, in the engine: required 4 of 6. Two gasbags dead
                // (survivors 4) lives; the third (survivors 3 < 4) kills, the decoded
                // polarity. The design's destroy-count reading would still be alive here.
                runtime.DamageAt(bagNode, 10_000f); // finishes gasbag1
                runtime.DamageAt(runtime.FindNodes("gasbag2", host).FirstOrDefault(), 10_000f);
                zeps.SimStep(1f / 60f);
                ctx.Same(4, zeps.SurvivorsOf("piratezep"), $"two gasbags down leaves 4 survivors");
                ctx.Check(!zeps.IsDead("piratezep"),
                    $"survivors 4 >= required {def.NumHealthyRequired}: alive");
                runtime.DamageAt(runtime.FindNodes("gasbag3", host).FirstOrDefault(), 10_000f);
                zeps.SimStep(1f / 60f);
                ctx.Same(3, zeps.SurvivorsOf("piratezep"), $"the third leaves 3 survivors");
                ctx.Check(zeps.IsDead("piratezep"),
                    $"survivors 3 < required {def.NumHealthyRequired}: the zeppelin DIES (the decoded polarity)");
                ctx.Check(started.Contains("all_pzep_gasbags"),
                    $"the kill plays the authored hull death started=[{string.Join(", ", started)}]");

                // The dead hull stops flying: the node no longer moves.
                var restingPos = host.GlobalPosition;
                zeps.SimStep(1f);
                ctx.Check(restingPos.DistanceTo(host.GlobalPosition) < 1e-3f,
                    $"the dead zeppelin's motion is stopped");
            }
            finally
            {
                runtime.OnInstanceStarted = null;
                pool?.Free();
                zeps?.Free();
                textures?.Dispose();
            }
        });
    }

    // The broadside chain: C3/M03's Pandora as shipped (never engaged, so nothing opens or fires
    // on a player abeam), then C1/M04's piratezep engaged as COMPLETED_ZEPCANNONS would: deploy,
    // a volley at a player stand-in (repositioned through its flight model each step to hold the
    // bearing), hold-and-retract out of arc, thinning, scatter, and the gasbag pick.
    // ⚠ Assert rounds at the spawn seam, count and direction, never as hits: a moved body never
    // re-enters the one-frame space queries (INSTR-13).
    [Suite("zeppelin-broadside",
        "broadside cannons (F19): C3/M03's Pandora as shipped (no COMPLETED_ZEPCANNONS) " +
        "neither deploys nor fires on a player abeam inside range for 30 s and does both " +
        "once engaged; then on C1/M04's engaged, flying piratezep a player inside the port " +
        "arc triggers the authored deploy anims (durations read from the defs, 4 s), the " +
        "readied side volleys real unowned wep_28 rounds lead-solved at the player while " +
        "the far side stays stowed, out-of-arc holds fire and retracts after the invented " +
        "idle window, an F18-destroyed cannon thins the next volley to 5, a " +
        "cannon_inaccuracy clone shows real scatter, and the zeppelin-vs-zeppelin arm " +
        "rand()-picks only the target's IN-ARC gasbags on constructed geometry, and a targets " +
        "name that is neither player nor a zeppelin resolves through the world node table and " +
        "is volleyed at by that node's own position while a roster name still aims at gasbags; " +
        "then C5/M04's " +
        "Dante, sharing its twelve cannon node names with two other zeppelins on the same " +
        "mission, wires its own F18 pool per cannon, volleys a held target zeppelin's gasbags, " +
        "and thins the same way once one cannon is destroyed")]
    internal static void ZeppelinBroadsideSuite(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M04");
        ctx.RequireData(missionZrdr, $"C1/M04 zrdr");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, "C3", "M03"), $"C3/M03 zrdr");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, "C5", "M04"), $"C5/M04 zrdr");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var wep28 = weapons.Get(ZeppelinRuntime.BroadsideWeaponId);
        ctx.Check(wep28 is { Velocity: not null },
            $"the hardcoded {ZeppelinRuntime.BroadsideWeaponId} resolves in weapons.zrd v={wep28?.Velocity ?? 0f:0}");
        if (wep28 == null)
            return;

        ctx.WithWorld("C3", collision: false, mission: "M03", world =>
            DisengagedPandora(ctx, world, weapons));

        ctx.WithWorld("C1", collision: true, mission: "M04", world =>
        {
            var runtime = world.Session.Runtime;
            var defs = Zeppelins.Load(missionZrdr);
            var def = defs[0];
            var host = runtime.FindNodes("piratezep").FirstOrDefault();
            ctx.Check(defs.Count == 1 && host != null && def.CannonInaccuracyDeg == null,
                $"C1/M04 authors piratezep (no cannon_inaccuracy), and its node resolves");
            if (host == null)
                return;

            var nets = AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C1"));
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            TextureArchive? textures = null;
            ProjectilePool? pool = null;
            ZeppelinRuntime? zeps = null;
            ZeppelinRuntime? scatterZeps = null;
            ZeppelinRuntime? zvz = null;
            FlightController? player = null;
            Node3D? attackerHost = null;
            Node3D? targetHost = null;
            var started = new List<string>();
            try
            {
                textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C1"));
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);
                runtime.OnInstanceStarted = (d, _) => { if (d.AnimName != null) started.Add(d.AnimName); };

                zeps = new ZeppelinRuntime(defs,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, nets);
                zeps.WireDamage(runtime);
                zeps.WireCannons(live, weapons);
                var bs = zeps.BroadsideOf("piratezep");
                ctx.Check(bs != null && bs.Cannons.Count == 12,
                    $"the broadside wires 6+6 cannons count={bs?.Cannons.Count ?? 0}");
                if (bs == null)
                    return;
                // C1/M04's script never runs COMPLETED_ZEPCANNONS either; the suite stands in
                // for it so the arm past the gate is exercised on shipped geometry.
                ctx.Check(!bs.CannonsEngaged && zeps.SetCannonsEngaged("piratezep", true),
                    $"the broadside wires disengaged and the script's flag engages it");
                ctx.Check(bs.Cannons.All(c => Mathf.IsEqualApprox(c.DeploySeconds, 4f)),
                    $"deploy durations are read from the authored anim defs (4 s run_time) first={bs.Cannons[0].DeploySeconds:0.##}");

                // The player stand-in: a real registered aircraft whose flight-model position
                // the suite steers to hold each phase's bearing on the FLYING hull.
                var fm = new FlightModel(stats);
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                player = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    PlayerIndex = 0,
                    Projectiles = live,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                player.AddChild(model);
                player.Setup(fm, null, new CamParams(), host.GlobalPosition + Vector3.Right * 300f,
                    host.GlobalPosition);
                ctx.Host.AddChild(player);

                var motion = zeps.MotionFor("piratezep")!;
                const float dt = 1f / 60f;
                Vector3 PortAbeam() => host.GlobalPosition + ZeppelinBroadside.SideNormal(
                    motion.YawRad, motion.PitchRad, BroadsideSide.Left, bs.RightSign) * 300f;
                void StepAt(System.Func<Vector3> where, int steps, ZeppelinRuntime target)
                {
                    for (int i = 0; i < steps; i++)
                    {
                        fm.Position = where();
                        target.SimStep(dt);
                        live.SimStep(dt);
                    }
                }

                // 1. In the port arc: the port cannons deploy (authored anims), starboard
                // stays stowed, and at 4 s the readied side volleys 6 real rounds.
                StepAt(PortAbeam, 30, zeps);
                ctx.Check(started.Count(a => a.StartsWith("deploy_pzep_lbroad")) == 6
                          && !started.Any(a => a.StartsWith("deploy_pzep_rbroad")),
                    $"the port six deploy, starboard stays stowed anims=[{string.Join(",", started)}]");
                ctx.Same(0, zeps.BroadsideShotsOf("piratezep"),
                    $"mid-deploy nothing fires (stowed cannons deploy INSTEAD of firing)");
                StepAt(PortAbeam, (int)(4.5f / dt), zeps);
                ctx.Same(6, zeps.BroadsideShotsOf("piratezep"),
                    $"the readied port side volleys one round per live cannon");
                // A 30 degree bound: the hull flies on between volley and check and the muzzles sit ~100 m along
                // it. The solve's exactness is pinned engine-free (ZeppelinBroadsideTests); the in-engine claim is
                // only "at the player, out of the port side".
                var volley = zeps.LastVolleyOf("piratezep");
                var portNow = ZeppelinBroadside.SideNormal(
                    motion.YawRad, motion.PitchRad, BroadsideSide.Left, bs.RightSign);
                float worstOff = 0f;
                float worstSide = 1f;
                foreach (var dir in volley)
                {
                    worstOff = Mathf.Max(worstOff, dir.AngleTo(fm.Position - host.GlobalPosition));
                    worstSide = Mathf.Min(worstSide, dir.Normalized().Dot(portNow));
                }
                ctx.Check(volley.Count == 6 && worstOff < 0.52f && worstSide > 0.5f,
                    $"every round leaves lead-solved toward the player, out of the port side dirs={volley.Count} worstOff={Mathf.RadToDeg(worstOff):0.#}° minPortDot={worstSide:0.##}");

                // 2. Out of arc: dead ahead. Fire holds through the 20 s re-fire horizon and
                // the idle window retracts the port cannons.
                started.Clear();
                int shotsBefore = zeps.BroadsideShotsOf("piratezep");
                Vector3 Ahead() => host.GlobalPosition + motion.Forward * 300f;
                StepAt(Ahead, (int)(25f / dt), zeps);
                ctx.Same(shotsBefore, zeps.BroadsideShotsOf("piratezep"),
                    $"out of both arcs the broadside holds fire for 25 s");
                ctx.Check(started.Count(a => a.StartsWith("retract_pzep_lbroad")) == 6,
                    $"the idle port cannons retract (invented {ZeppelinBroadside.StowAfterIdleSeconds:0} s window) anims=[{string.Join(",", started.Where(a => a.Contains("retract")))}]");

                // 3. F18 thinning: destroy one port cannon (its compiled def pool, HEALTH 60),
                // return to the port arc, the redeployed volley is 5, not 6.
                var lbroadNode = runtime.FindNodes(def.LeftCannons[0].Node, host).FirstOrDefault();
                var lbroadPool = lbroadNode == null ? null : runtime.Destructibles.PoolsOn(lbroadNode).FirstOrDefault();
                ctx.Check(lbroadPool != null,
                    $"'{def.LeftCannons[0].Node}' carries its compiled def pool hp={lbroadPool?.MaxHealth ?? 0f:0}");
                if (lbroadPool == null)
                    return;
                runtime.DamageAt(lbroadNode, lbroadPool.MaxHealth + 1f);
                shotsBefore = zeps.BroadsideShotsOf("piratezep");
                int guard = 0;
                while (zeps.BroadsideShotsOf("piratezep") == shotsBefore && guard++ < (int)(30f / dt))
                {
                    fm.Position = PortAbeam();
                    zeps.SimStep(dt);
                    live.SimStep(dt);
                }
                ctx.Same(5, zeps.BroadsideShotsOf("piratezep") - shotsBefore,
                    $"the destroyed cannon drops out, the volley thins to 5 after {guard * dt:0.#} s");

                // 4. Scatter: a clone authoring cannon_inaccuracy 10° (the C2B/M04 value) on
                // the same hull spreads a volley the exact solve would collapse to a point.
                var scatterDef = CloneWithInaccuracy(def, 10f);
                scatterZeps = new ZeppelinRuntime(new[] { scatterDef },
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, nets);
                scatterZeps.WireCannons(live, weapons);
                scatterZeps.SetCannonsEngaged("piratezep", true);
                var sMotion = scatterZeps.MotionFor("piratezep")!;
                var sBs = scatterZeps.BroadsideOf("piratezep")!;
                Vector3 SPort() => host.GlobalPosition + ZeppelinBroadside.SideNormal(
                    sMotion.YawRad, sMotion.PitchRad, BroadsideSide.Left, sBs.RightSign) * 300f;
                StepAt(SPort, (int)(5f / dt), scatterZeps);
                var scattered = scatterZeps.LastVolleyOf("piratezep");
                float maxPair = 0f;
                for (int i = 0; i < scattered.Count; i++)
                    for (int j = i + 1; j < scattered.Count; j++)
                        maxPair = Mathf.Max(maxPair, scattered[i].AngleTo(scattered[j]));
                ctx.Check(scattered.Count == 6 && maxPair > Mathf.DegToRad(1f),
                    $"cannon_inaccuracy 10° spreads the volley max pair angle {Mathf.RadToDeg(maxPair):0.##}° over {scattered.Count} rounds");

                // 5. The zeppelin-vs-zeppelin arm, constructed geometry (see summary): a near-
                // static attacker whose target zeppelin is deactivated abeam, one gasbag
                // placed outside the 0.707 arc, every pick lands on an IN-ARC bag.
                attackerHost = new Node3D { Name = "attackzep" };
                var cb1 = new Node3D { Name = "cb1", Position = new Vector3(20f, 0f, -30f) };
                var cb2 = new Node3D { Name = "cb2", Position = new Vector3(20f, 0f, 30f) };
                attackerHost.AddChild(cb1);
                attackerHost.AddChild(cb2);
                targetHost = new Node3D { Name = "targetzep" };
                var g1 = new Node3D { Name = "g1", Position = new Vector3(0f, 0f, -40f) };
                var g2 = new Node3D { Name = "g2", Position = new Vector3(0f, 0f, 40f) };
                var g3 = new Node3D { Name = "g3", Position = new Vector3(-290f, 0f, -290f) };
                targetHost.AddChild(g1);
                targetHost.AddChild(g2);
                targetHost.AddChild(g3);
                ctx.Host.AddChild(attackerHost);
                ctx.Host.AddChild(targetHost);
                var attacker = SyntheticZep("attackzep", new Vector3(0f, 4000f, 0f), nets[0].Name,
                    targets: new[] { "targetzep" });
                var target = SyntheticZep("targetzep", new Vector3(300f, 4000f, 0f), nets[0].Name,
                    deactivated: true,
                    healthy: new[] { "g1", "g2", "g3" });
                var resolvedAtt = attackerHost;
                var resolvedTgt = targetHost;
                zvz = new ZeppelinRuntime(new[] { attacker, target }, name =>
                    name == "attackzep" ? resolvedAtt : name == "targetzep" ? resolvedTgt : null, nets);
                zvz.WireCannons(live, weapons);
                zvz.SetCannonsEngaged("attackzep", true);
                for (int i = 0; i < (int)(6f / dt) && zvz.BroadsideShotsOf("attackzep") == 0; i++)
                {
                    zvz.SimStep(dt);
                    live.SimStep(dt);
                }
                var picks = zvz.LastVolleyOf("attackzep");
                ctx.Same(2, picks.Count, $"both starboard cannons fire at the target zeppelin");
                bool onlyInArc = picks.Count > 0 && picks.All(dir =>
                {
                    var fromCb = dir.Normalized();
                    float toG1 = fromCb.AngleTo(g1.GlobalPosition - attackerHost.GlobalPosition);
                    float toG2 = fromCb.AngleTo(g2.GlobalPosition - attackerHost.GlobalPosition);
                    float toG3 = fromCb.AngleTo(g3.GlobalPosition - attackerHost.GlobalPosition);
                    return Mathf.Min(toG1, toG2) < toG3;
                });
                ctx.Check(onlyInArc,
                    $"every rand()-picked aim point is an IN-ARC gasbag, never the out-of-arc g3");

                // 6. Name resolution past the roster: a targets name that is neither `player`
                // nor a zeppelin is a world node, engaged at that node's own position.
                WorldNodeTargets(ctx, nets, live, weapons);
            }
            finally
            {
                runtime.OnInstanceStarted = null;
                pool?.Free();
                zeps?.Free();
                scatterZeps?.Free();
                zvz?.Free();
                player?.Free();
                attackerHost?.Free();
                targetHost?.Free();
                textures?.Dispose();
            }
        });

        // CM24 (C5/M04)'s Dante: three zeppelins share the twelve `lbroadNN`/`rbroadNN` node
        // names on one mission, unlike C1/M04's lone piratezep above; proves its own record
        // still wires an F18 pool per cannon and thins the volley the same way.
        ctx.WithWorld("C5", collision: true, mission: "M04", world =>
            DestroyedDanteCannon(ctx, world, weapons));
    }

    // CM24 (C5/M04)'s Dante broadside: real record data for both the shooter and its
    // zeppelin-vs-zeppelin target (piratezep, held static so its own net motion never fights the
    // test's placement), engaged and volleyed at short range, then one port cannon destroyed
    // through its own F18 pool to prove the thinning F19 promises.
    internal static void DestroyedDanteCannon(TestContext ctx, TestWorld world, WeaponDefs weapons)
    {
        var runtime = world.Session.Runtime;
        var defs = Zeppelins.Load(SessionPaths.MissionZrdr(ctx.DataRoot, "C5", "M04"));
        var dante = defs.FirstOrDefault(d => d.Node.Equals("dantezep", System.StringComparison.OrdinalIgnoreCase));
        var pirate = defs.FirstOrDefault(d => d.Node.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase));
        var danteHost = runtime.FindNodes("dantezep").FirstOrDefault();
        var pirateHost = runtime.FindNodes("piratezep").FirstOrDefault();
        ctx.Check(dante is { LeftCannons.Count: 6, RightCannons.Count: 6 } && pirate != null
                  && danteHost != null && pirateHost != null,
            $"C5/M04 authors dantezep with 6+6 broadside cannons targeting [{string.Join(",", dante?.Targets ?? new List<string>())}], and both hulls resolve");
        if (dante == null || pirate == null || danteHost == null || pirateHost == null)
            return;

        var nets = AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C5"));
        TextureArchive? textures = null;
        ProjectilePool? pool = null;
        ZeppelinRuntime? zeps = null;
        try
        {
            textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C5"));
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            var started = new List<string>();
            runtime.OnInstanceStarted = (d, _) => { if (d.AnimName != null) started.Add(d.AnimName); };

            var resolvedDante = danteHost;
            var resolvedPirate = pirateHost;
            zeps = new ZeppelinRuntime(new[] { dante, pirate },
                name => name.Equals("dantezep", System.StringComparison.OrdinalIgnoreCase) ? resolvedDante
                    : name.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase) ? resolvedPirate
                    : null,
                nets);
            ctx.Host.AddChild(zeps);
            zeps.WireDamage(runtime);
            zeps.WireCannons(live, weapons);
            // Held static the instant it is wired (Speed is still 0 from construction): the
            // zeppelin-vs-zeppelin target must sit still for the test's own placement, never fly
            // its own net or carry a phantom velocity into the lead solve.
            zeps.Hold("piratezep");
            var bs = zeps.BroadsideOf("dantezep");
            ctx.Check(bs != null && bs.Cannons.Count == 12,
                $"the Dante wires 6+6 cannons count={bs?.Cannons.Count ?? 0}");
            if (bs == null)
                return;

            // The fire gate this item's evidence questioned: every one of the twelve authored
            // cannon nodes carries its own F18 destructible pool, despite three zeppelins sharing
            // the same twelve node names on this one mission.
            int pooled = 0;
            foreach (var cannon in dante.LeftCannons.Concat(dante.RightCannons))
            {
                var node = runtime.FindNodes(cannon.Node, danteHost).FirstOrDefault();
                if (node != null && runtime.Destructibles.PoolsOn(node).Count > 0)
                {
                    pooled++;
                }
            }
            ctx.Same(12, pooled, $"all twelve Dante cannon nodes carry an F18 destructible pool");

            ctx.Check(!bs.CannonsEngaged && zeps.SetCannonsEngaged("dantezep", true),
                $"the broadside wires disengaged and the script's flag engages it");

            var motion = zeps.MotionFor("dantezep")!;
            const float dt = 1f / 60f;
            Vector3 PortAbeam() => danteHost.GlobalPosition + ZeppelinBroadside.SideNormal(
                motion.YawRad, motion.PitchRad, BroadsideSide.Left, bs.RightSign) * 300f;
            void Step(int steps)
            {
                for (int i = 0; i < steps; i++)
                {
                    pirateHost.GlobalPosition = PortAbeam();
                    zeps.SimStep(dt);
                    live.SimStep(dt);
                    runtime.Advance(dt);
                }
            }

            Step((int)(4.5f / dt));
            ctx.Same(6, zeps.BroadsideShotsOf("dantezep"),
                $"the port six volley wep_28 at the target zeppelin's in-arc gasbags shots={zeps.BroadsideShotsOf("dantezep")}");

            var lbroadNode = runtime.FindNodes(dante.LeftCannons[0].Node, danteHost).FirstOrDefault();
            var lbroadPool = lbroadNode == null ? null : runtime.Destructibles.PoolsOn(lbroadNode).FirstOrDefault();
            ctx.Check(lbroadPool != null,
                $"'{dante.LeftCannons[0].Node}' carries its own compiled def pool hp={lbroadPool?.MaxHealth ?? 0f:0}");
            if (lbroadPool == null)
                return;
            started.Clear();
            runtime.DamageAt(lbroadNode, lbroadPool.MaxHealth + 1f);
            // The destroy def's own death sequence calls its paired panel's burn anim
            // (dtzepleft_gasbag1 at +1 s, dtzepright_gasbag1 at +8 s), an authored link from a
            // cannon's death to a gasbag skin fire; the +8 s delay needs real sim time to fire.
            Step((int)(8.5f / dt));
            // The gasbag half: already reachable through ordinary CallAnimation dispatch, with
            // no bespoke code, once the sequence has had time to run.
            ctx.Check(started.Any(a => a.StartsWith("dtzepleft_gasbag") || a.StartsWith("dtzepright_gasbag")),
                $"the destroyed cannon's own death sequence calls its paired gasbag panel's burn anim started=[{string.Join(",", started)}]");
            int shotsBefore = zeps.BroadsideShotsOf("dantezep");
            int guard = 0;
            while (zeps.BroadsideShotsOf("dantezep") == shotsBefore && guard++ < (int)(30f / dt))
            {
                pirateHost.GlobalPosition = PortAbeam();
                zeps.SimStep(dt);
                live.SimStep(dt);
            }
            // The burn the death called destroys the cannon's side-mate on the same gasbag
            // (destroy_dtzep_lbroad12, a call the runtime routes through that cannon's pool), so
            // the port volley loses two of its six, not one.
            ctx.Same(4, zeps.BroadsideShotsOf("dantezep") - shotsBefore,
                $"the destroyed Dante cannon and its burnt side-mate drop out, the volley thins to 4 after {guard * dt:0.#} s");
        }
        finally
        {
            runtime.OnInstanceStarted = null;
            pool?.Free();
            zeps?.Free();
            textures?.Dispose();
        }
    }

    // CM04 (C3/M03) as shipped: the Pandora authors targets [player] and 500 m of range, and its
    // objectives never run COMPLETED_ZEPCANNONS, so with the player abeam inside range through a
    // full deploy plus the 20 s fire delay no hatch opens and no round leaves. The same hull
    // engaged is the positive control: the gate, not the geometry, is what holds fire.
    internal static void DisengagedPandora(TestContext ctx, TestWorld world, WeaponDefs weapons)
    {
        var runtime = world.Session.Runtime;
        var defs = Zeppelins.Load(SessionPaths.MissionZrdr(ctx.DataRoot, "C3", "M03"));
        var def = defs.FirstOrDefault(d => d.Node.Equals("piratezep", System.StringComparison.OrdinalIgnoreCase));
        var host = runtime.FindNodes("piratezep").FirstOrDefault();
        ctx.Check(def != null && host != null && def.Targets.Count == 1
                  && ZeppelinBroadside.IsPlayerTarget(def.Targets[0]) && def.CannonFireRange == 500f,
            $"C3/M03 authors piratezep with targets [player], range 500 m, and its node resolves");
        var objectives = ObjectiveScript.Load(SessionPaths.MissionZrdr(ctx.DataRoot, "C3", "M03")).Objectives;
        ctx.Check(objectives.Count > 0 && objectives.All(o => o.CompletedZepcannons.Count == 0),
            $"no C3/M03 objective runs COMPLETED_ZEPCANNONS over {objectives.Count} objectives");
        if (def == null || host == null)
            return;

        var nets = AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C3"));
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        TextureArchive? textures = null;
        ProjectilePool? pool = null;
        ZeppelinRuntime? zeps = null;
        FlightController? player = null;
        var started = new List<string>();
        try
        {
            textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C3"));
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            runtime.OnInstanceStarted = (d, _) => { if (d.AnimName != null) started.Add(d.AnimName); };

            var resolvedHost = host;
            zeps = new ZeppelinRuntime(new[] { def }, _ => resolvedHost, nets, null,
                runtime.Motions.DrivesTransform);
            ctx.Host.AddChild(zeps);
            zeps.WireDamage(runtime);
            zeps.WireCannons(live, weapons);
            var bs = zeps.BroadsideOf("piratezep");
            ctx.Check(bs is { Cannons.Count: 12, CannonsEngaged: false },
                $"the Pandora's 12 cannons wire disengaged count={bs?.Cannons.Count ?? 0}");
            if (bs == null)
                return;

            var fm = new FlightModel(stats);
            var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            player = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                PlayerIndex = 0,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            player.AddChild(model);
            player.Setup(fm, null, new CamParams(), host.GlobalPosition + Vector3.Right * 300f,
                host.GlobalPosition);
            ctx.Host.AddChild(player);

            var motion = zeps.MotionFor("piratezep")!;
            const float dt = 1f / 60f;
            Vector3 PortAbeam() => host.GlobalPosition + ZeppelinBroadside.SideNormal(
                motion.YawRad, motion.PitchRad, BroadsideSide.Left, bs.RightSign) * 300f;
            void Hold(float seconds)
            {
                for (int i = 0; i < (int)(seconds / dt); i++)
                {
                    fm.Position = PortAbeam();
                    zeps.SimStep(dt);
                    live.SimStep(dt);
                }
            }

            // Abeam at 300 m of 500, through the 4 s deploy, the 20 s fire delay and a margin.
            Hold(30f);
            ctx.Check(!started.Any(a => a.Contains("broad")),
                $"30 s abeam the shipped Pandora opens no hatch anims=[{string.Join(",", started.Where(a => a.Contains("broad")))}]");
            ctx.Same(0, zeps.BroadsideShotsOf("piratezep"),
                $"...and fires no {ZeppelinRuntime.BroadsideWeaponId} at the player");
            ctx.Check(bs.Cannons.All(c => c.State == ZeppelinCannonState.Stowed),
                $"every cannon is still stowed");

            // Positive control: the same hull with the script's flag set opens up on the player.
            zeps.SetCannonsEngaged("piratezep", true);
            Hold(5f);
            ctx.Check(started.Count(a => a.StartsWith("deploy_pzep_lbroad")) == 6,
                $"engaged, the port six deploy anims=[{string.Join(",", started)}]");
            ctx.Check(zeps.BroadsideShotsOf("piratezep") > 0,
                $"...and the readied side fires shots={zeps.BroadsideShotsOf("piratezep")}");
        }
        finally
        {
            runtime.OnInstanceStarted = null;
            pool?.Free();
            zeps?.Free();
            player?.Free();
            textures?.Dispose();
        }
    }

    // A copy of a shipped record with `cannon_inaccuracy` authored, the scatter
    // phase's instrument (no C1 record authors one; C2B/M04's 10° is the shipped value).
    // The name-resolution arm the original reaches through its general node table: a `targets`
    // name that is neither `player` nor a zeppelin resolves to a world node and is fired on by
    // that node's position, while a roster name still takes the gasbag branch. Constructed
    // geometry throughout, since no shipped record authors the first case at all.
    internal static void WorldNodeTargets(TestContext ctx, IReadOnlyList<AiNet> nets,
        ProjectilePool live, WeaponDefs weapons)
    {
        const float dt = 1f / 60f;

        // A constructed hull carrying the two starboard cannon nodes SyntheticZep authors.
        static Node3D Attacker(string name)
        {
            var built = new Node3D { Name = name };
            built.AddChild(new Node3D { Name = "cb1", Position = new Vector3(20f, 0f, -30f) });
            built.AddChild(new Node3D { Name = "cb2", Position = new Vector3(20f, 0f, 30f) });
            return built;
        }

        var nodeHost = Attacker("nodeattackzep");
        var surface = new Node3D { Name = "surfacehull" };
        var bagHost = Attacker("bagattackzep");
        var bagTarget = new Node3D { Name = "bagtargetzep" };
        var bag = new Node3D { Name = "bg1", Position = new Vector3(0f, 0f, -300f) };
        bagTarget.AddChild(bag);
        ZeppelinRuntime? zeps = null;
        try
        {
            ctx.Host.AddChild(nodeHost);
            ctx.Host.AddChild(surface);
            ctx.Host.AddChild(bagHost);
            ctx.Host.AddChild(bagTarget);
            var nodes = new Dictionary<string, Node3D>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["nodeattackzep"] = nodeHost,
                ["surfacehull"] = surface,
                ["bagattackzep"] = bagHost,
                ["bagtargetzep"] = bagTarget,
            };
            var defs = new[]
            {
                SyntheticZep("nodeattackzep", new Vector3(0f, 5000f, 0f), nets[0].Name,
                    targets: new[] { "surfacehull" }),
                SyntheticZep("bagattackzep", new Vector3(0f, 6000f, 0f), nets[0].Name,
                    targets: new[] { "bagtargetzep" }),
                SyntheticZep("bagtargetzep", new Vector3(400f, 6000f, 0f), nets[0].Name,
                    healthy: new[] { "bg1" }, deactivated: true),
            };
            zeps = new ZeppelinRuntime(defs,
                name => nodes.TryGetValue(name, out var hit) ? hit : null, nets);
            zeps.WireCannons(live, weapons);
            ctx.Check(zeps.SetCannonsEngaged("nodeattackzep", true)
                      && zeps.SetCannonsEngaged("bagattackzep", true),
                $"both constructed attackers wire a broadside and the script's flag engages it");
            // Off the attacker's beam, so aiming at the node is distinguishable from aiming at
            // nothing; inside the record's 500 m fire range and the 0.707 starboard arc.
            surface.GlobalPosition = nodeHost.GlobalPosition + new Vector3(400f, 0f, 60f);

            for (int i = 0; i < (int)(8f / dt)
                            && (zeps.BroadsideShotsOf("nodeattackzep") == 0
                                || zeps.BroadsideShotsOf("bagattackzep") == 0); i++)
            {
                zeps.SimStep(dt);
                live.SimStep(dt);
            }

            var atNode = zeps.LastVolleyOf("nodeattackzep");
            var toSurface = surface.GlobalPosition - nodeHost.GlobalPosition;
            float worstNode = 0f;
            foreach (var dir in atNode)
                worstNode = Mathf.Max(worstNode, dir.AngleTo(toSurface));
            ctx.Same(2, atNode.Count,
                $"a targets name that is no zeppelin resolves through the world node table, and both starboard cannons fire on it");
            ctx.Check(atNode.Count == 2 && worstNode < Mathf.DegToRad(15f),
                $"every round leaves lead-solved at the world node's own position worstOff={Mathf.RadToDeg(worstNode):0.#}°");

            // The other half of the same gate: a roster name resolves as a zeppelin as it always
            // did, so its rounds go to the gasbag 300 m off the hull axis, not to the hull origin.
            var atBags = zeps.LastVolleyOf("bagattackzep");
            var toBag = bag.GlobalPosition - bagHost.GlobalPosition;
            var toHull = bagTarget.GlobalPosition - bagHost.GlobalPosition;
            float worstBag = 0f;
            float nearestHull = Mathf.Pi;
            foreach (var dir in atBags)
            {
                worstBag = Mathf.Max(worstBag, dir.AngleTo(toBag));
                nearestHull = Mathf.Min(nearestHull, dir.AngleTo(toHull));
            }
            ctx.Check(atBags.Count == 2 && worstBag < Mathf.DegToRad(15f)
                      && nearestHull > Mathf.DegToRad(25f),
                $"a zeppelin name still takes the gasbag branch: worst off the gasbag {Mathf.RadToDeg(worstBag):0.#}°, nearest the hull origin {Mathf.RadToDeg(nearestHull):0.#}°");
        }
        finally
        {
            zeps?.Free();
            nodeHost.Free();
            surface.Free();
            bagHost.Free();
            bagTarget.Free();
        }
    }

    internal static ZeppelinDef CloneWithInaccuracy(ZeppelinDef def, float inaccuracyDeg) => new()
    {
        Node = def.Node,
        Position = def.Position,
        YawDeg = def.YawDeg,
        PitchDeg = def.PitchDeg,
        MaxSpeed = def.MaxSpeed,
        MaxAccel = def.MaxAccel,
        AccelPitchDeg = def.AccelPitchDeg,
        AccelYawDeg = def.AccelYawDeg,
        MaxRateYawDeg = def.MaxRateYawDeg,
        MaxRatePitchDeg = def.MaxRatePitchDeg,
        MinPitchDeg = def.MinPitchDeg,
        MaxPitchDeg = def.MaxPitchDeg,
        Net = def.Net,
        Targets = def.Targets,
        Healthy = def.Healthy,
        NumHealthyRequired = def.NumHealthyRequired,
        Engines = def.Engines,
        Gasbags = def.Gasbags,
        CannonFireDelay = def.CannonFireDelay,
        CannonFireRange = def.CannonFireRange,
        LeftCannons = def.LeftCannons,
        RightCannons = def.RightCannons,
        CannonHealth = def.CannonHealth,
        CannonInaccuracyDeg = inaccuracyDeg,
    };

    // A minimal constructed zeppelin record for the zeppelin-vs-zeppelin phase:
    // near-static (rates/speed floored) so the constructed bearings hold while cannons
    // deploy on the fallback timing.
    internal static ZeppelinDef SyntheticZep(string node, Vector3 pos, string net,
        string[]? targets = null, string[]? healthy = null, bool deactivated = false)
    {
        var zones = new List<ZeppelinHealthyZone>();
        foreach (var h in healthy ?? System.Array.Empty<string>())
            zones.Add(new ZeppelinHealthyZone(h, "panels"));
        return new ZeppelinDef
        {
            Node = node,
            Position = pos,
            YawDeg = 0f,
            MaxSpeed = 0.1f,
            MaxAccel = 4.47f,
            AccelYawDeg = 0.1f,
            AccelPitchDeg = 0.1f,
            MaxRateYawDeg = 0.1f,
            MaxRatePitchDeg = 0.1f,
            MinPitchDeg = -30f,
            MaxPitchDeg = 30f,
            Net = net,
            Targets = targets ?? System.Array.Empty<string>(),
            Healthy = zones,
            NumHealthyRequired = 1,
            Engines = System.Array.Empty<string>(),
            Gasbags = System.Array.Empty<ZeppelinGasbag>(),
            CannonHealth = System.Array.Empty<ZeppelinCannonHealth>(),
            LeftCannons = System.Array.Empty<ZeppelinCannon>(),
            RightCannons = targets == null
                ? (IReadOnlyList<ZeppelinCannon>)System.Array.Empty<ZeppelinCannon>()
                : new[]
                {
                    new ZeppelinCannon("cb1", "deploy_cb1", "retract_cb1"),
                    new ZeppelinCannon("cb2", "deploy_cb2", "retract_cb2"),
                },
            CannonFireDelay = 20f,
            CannonFireRange = 500f,
            Deactivated = deactivated,
        };
    }

    // Long enough for the dock glide's exponential to close whatever the hold latched at: 40 s
    // takes a 30 m gap under a centimetre.
    private static void Settle(ZeppelinRuntime runtime, float dt)
    {
        for (int i = 0; i < 60 * 40; i++)
            runtime.SimStep(dt);
    }

    // Steps until the walk holds on `node`, carrying the route's worst pitch along. Returns the
    // steps taken, which is the budget when it never got there and the caller's check then fails.
    private static int FlyUntilHold(ZeppelinRuntime runtime, ZeppelinMotion motion, int node,
        ref float worstPitch, int budget)
    {
        const float dt = 1f / 60f;
        int steps = 0;
        while (!(motion.Follower.Holding && motion.Follower.CurrentIndex == node) && steps < budget)
        {
            runtime.SimStep(dt);
            worstPitch = Mathf.Max(worstPitch, Mathf.Abs(motion.PitchRad));
            steps++;
        }
        return steps;
    }

    // The rendezvous the mission authors at Klondike1's cargo point: freighteraground beaches the
    // freighter under it and calls its twelve tankerfreight crates up, and OBJECTIVE17's
    // WAKE_ANIM ["zepgetcargo"] opens the freighter's hold doors, the Pandora's cargo doors 8 s
    // later, and 5 s after that rides the crane down its chain and back up.
    private static void CheckCargoSequence(TestContext ctx, TestWorld world, AiNet net, int cargoNode)
    {
        var calls = new List<string>();
        foreach (var d in world.Runtime.DefsFor("zepgetcargo"))
        {
            foreach (var seq in d.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } name)
                        calls.Add(name);
                }
            }
        }
        ctx.Check(calls.Contains("freighterdoors") && calls.Contains("open_pzepcargodoors")
            && calls.Contains("activate_pzep_crane"),
            $"zepgetcargo is the door-and-crane sequence, calls=[{string.Join(" ", calls)}]");

        var door = First(world, "holddoora");
        var crane = First(world, "crane");
        ctx.Check(door != null && crane != null,
            $"the built world carries the freighter's hold door and the Pandora's crane");
        if (door == null || crane == null)
            return;

        // The ship sails: its build pose is kilometres from the route, and it is its own
        // shipaground script, the branch a destroyed powerhut takes, that beaches it where the
        // Pandora is authored to hover, so that script's LAST pose is what node 7 answers to.
        var cargoPoint = net.Nodes[cargoNode].Position;
        var beached = LastPoseOf(world, "freighteraground");
        ctx.Check(beached != null, $"freighteraground carries the freighter's own shipaground script");
        if (beached is { } rest)
        {
            ctx.Check(Horizontal(rest, cargoPoint) < 100f,
                $"the beached freighter comes to rest under node {cargoNode}: {Horizontal(rest, cargoPoint):0} m from it in plan, ship at {rest}");
            ctx.Check(cargoPoint.Y - rest.Y is > 80f and < 110f,
                $"…with the cargo point {cargoPoint.Y - rest.Y:0} m above its deck, the drop a 54 m chain off a hatch 33 m under the hull needs");
        }

        float doorBefore = door.Rotation.X;
        float craneBefore = crane.Position.Y;
        int started = world.Runtime.PlayMissionTrigger("zepgetcargo").Count;
        ctx.Check(started > 0, $"WAKE_ANIM 'zepgetcargo' starts {started} definition(s)");
        Advance(world, 25f);
        ctx.Check(Mathf.Abs(Mathf.AngleDifference(doorBefore, door.Rotation.X)) > Mathf.DegToRad(80f),
            $"…the freighter's hold door has swung open, {Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(doorBefore, door.Rotation.X))):0}°");
        ctx.Check(Mathf.Abs(crane.Position.Y - craneBefore) > 10f,
            $"…and the crane has ridden its chain, {Mathf.Abs(crane.Position.Y - craneBefore):0} m of travel");
    }

    private static void Advance(TestWorld world, float seconds)
    {
        const float dt = 1f / 60f;
        for (int i = 0; i < (int)(seconds * 60f); i++)
            world.Runtime.Advance(dt);
    }

    // Where the named definition's first SI script leaves its object: the last frame's translate
    // channel at that frame's own end. Null when the def carries no script.
    private static Vector3? LastPoseOf(TestWorld world, string animName)
    {
        foreach (var def in world.Runtime.DefsFor(animName))
        {
            if (def.Archive == null || def.SiScriptIds.Length == 0)
                continue;
            var script = def.Archive.Script(def.SiScriptIds[0]);
            if (script is not { Frames.Count: > 0 })
                continue;
            var last = script.Frames[^1];
            if (last.Translate is { } translate)
                return translate.At(last.EndTime - last.StartTime);
        }
        return null;
    }

    private static Node3D? First(TestWorld world, string name) =>
        world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null;

    private static float Horizontal(Vector3 a, Vector3 b) =>
        new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static void DriveScriptedPose(TestContext ctx, TestWorld world, ZeppelinDef def,
        IReadOnlyList<AiNet> nets)
    {
        var host = world.Runtime.FindNodes("piratezep", null) is { Count: > 0 } hits ? hits[0] : null;
        ctx.Check(host != null, $"the built world carries the piratezep node");
        if (host == null)
            return;
        ctx.Check(world.Runtime.Motions.DrivesTransform(host),
            $"the bootstrap's pzep_todrydock owns piratezep's transform channel before any zeppelin runtime exists");
        var frame0 = new Vector3(-11313.7f, 553.7f, -13696.8f);
        var scriptStart = host.GlobalPosition;
        ctx.Check(scriptStart.DistanceTo(frame0) < 2f,
            $"…and has posed it at the script's frame 0 base, off by {scriptStart.DistanceTo(frame0):0.##} m");

        ZeppelinRuntime? zeps = null;
        try
        {
            var resolvedHost = host;
            zeps = new ZeppelinRuntime(new[] { def }, _ => resolvedHost, nets, null,
                world.Runtime.Motions.DrivesTransform);
            ctx.Host.AddChild(zeps);
            zeps.WireDamage(world.Runtime);
            ctx.Check(host.GlobalPosition.DistanceTo(scriptStart) < 0.01f,
                $"placement writes nothing over the script's frame 0 pos={host.GlobalPosition}");
            var motion = zeps.MotionFor("piratezep");
            ctx.Check(motion != null, $"MotionFor finds the parked motion");
            if (motion == null)
                return;

            const float dt = 1f / 60f;
            const float scriptEnd = 61.65f;
            float t = 0f, nearestSeat = float.MaxValue, longestStep = 0f;
            // The approach: the script's last ten seconds settle onto the seat, so the flight
            // proper is judged before them. A frame moves the hull about a metre at its cruise.
            while (t < scriptEnd - 10f)
            {
                var before = host.GlobalPosition;
                world.Runtime.Advance(dt);
                zeps.SimStep(dt);
                t += dt;
                nearestSeat = Mathf.Min(nearestSeat, host.GlobalPosition.DistanceTo(def.Position));
                longestStep = Mathf.Max(longestStep, before.DistanceTo(host.GlobalPosition));
            }
            ctx.Check(nearestSeat > 50f,
                $"the first {scriptEnd - 10f:0} s never put the hull at the record seat, closest {nearestSeat:0} m");
            ctx.Check(longestStep < 5f,
                $"…and no frame of them jumped, longest step {longestStep:0.##} m");
            ctx.Check(ReferenceEquals(zeps.MotionFor("piratezep"), motion)
                && motion.Position.DistanceTo(def.Position) < 0.01f && motion.Speed == 0f,
                $"the follower's motion never stepped under the script: still seated on the record, speed {motion.Speed:0.##}");

            while (t < scriptEnd + (2f * dt))
            {
                world.Runtime.Advance(dt);
                zeps.SimStep(dt);
                t += dt;
            }
            var handback = host.GlobalPosition;
            ctx.Check(!world.Runtime.Motions.DrivesTransform(host),
                $"the script has ended at {t:0.00} s and released the channel");
            ctx.Check(handback.DistanceTo(def.Position) < 2f,
                $"the script's last frame IS the record seat, off by {handback.DistanceTo(def.Position):0.##} m");
            var resumed = zeps.MotionFor("piratezep");
            ctx.Check(resumed != null && ReferenceEquals(resumed, motion)
                && resumed.Position.DistanceTo(handback) < 0.5f,
                $"the same motion resumed in place from the script's last frame, not from the record pos={resumed?.Position}");
            if (resumed == null)
                return;
            float yawErr = Mathf.Abs(Mathf.AngleDifference(resumed.YawRad, Mathf.DegToRad(def.YawDeg)));
            ctx.Check(yawErr < Mathf.DegToRad(3f) && Mathf.Abs(resumed.PitchRad) < Mathf.DegToRad(3f),
                $"…carrying the script's end attitude, which is the record's yaw {def.YawDeg:0} pitch 0: yaw off {Mathf.RadToDeg(yawErr):0.#}° pitch {Mathf.RadToDeg(resumed.PitchRad):0.#}°");

            for (int i = 0; i < 60 * 5; i++)
            {
                world.Runtime.Advance(dt);
                zeps.SimStep(dt);
            }
            ctx.Check(resumed.Follower.Holding && resumed.Follower.CurrentIndex == 0
                && host.GlobalPosition.DistanceTo(handback) < 1f,
                $"…and holds the dock, node 0's armed stop point, moved {host.GlobalPosition.DistanceTo(handback):0.##} m in 5 s idx={resumed.Follower.CurrentIndex} holding={resumed.Follower.Holding}");
        }
        finally
        {
            zeps?.Free();
        }
    }
}
