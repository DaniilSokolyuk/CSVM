using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A mission's surface vehicles driven by a host and followed by a guest over the loopback
/// link, through <see cref="NetSoakSuites"/>' four link cells. The ends are four surface-vehicle
/// runtimes over one built world, joined by their <see cref="NetWorldLink"/>s. What is measured is
/// the patrol stream alone, as how far the guest's hull stands from the host's on the water. A
/// replicated guest that is never fed is the control; a guest walking its own nets is reported.
/// </summary>
internal static class NetSurfaceVehicleSuites
{
    // C1B/M03 authors four patrol boats on nets of their own, the install's only roster hulls.
    private const string BoatChapter = "C1B";
    private const string BoatMission = "M03";

    // C2/M01 launches its hulls from a boat generator and re-nets them with SET_AI_NET clauses.
    private const string GenChapter = "C2";
    private const string GenMission = "M01";

    private const int MeshSeed = 7717;

    // Steps of clean link before the first cell, so every hull is under way when measuring starts.
    private const int PrerollSteps = 600;

    // Steps of each cell. The first WarmSteps after a change of conditions are not read.
    private const int CellSteps = 1800;
    private const int WarmSteps = 120;

    // The chase trails a straight leg by speed times (latency + one second over the chase rate).
    // The bar adds jitter and this much slack for the corners at each net node.
    private const float TurnSlackS = 0.5f;

    private static readonly (string Name, LoopbackConditions Link)[] Matrix =
    {
        ("clean", LoopbackConditions.Perfect),
        ("50ms/5%", new LoopbackConditions(0.05, 0.01, 0.05)),
        ("100ms/10%", new LoopbackConditions(0.10, 0.02, 0.10)),
        ("200ms/20%", new LoopbackConditions(0.20, 0.04, 0.20)),
    };

    [Suite("net-surface-patrol",
        "C1B/M03's four patrol boats driven by a host and followed by a guest through four link "
        + "cells from clean to 200 ms with 20 per cent loss: the host samples every patrolling hull "
        + "each half second, the guest's hull walks no net of its own and stays within speed times "
        + "(latency + jitter + the chase lag + slack) of the host's in every cell, and a replicated "
        + "guest never fed exceeds that bar")]
    internal static void SurfacePatrolsFollowTheHost(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BoatChapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BoatChapter, BoatMission);
        ctx.RequireData(chapterZrdr, $"{BoatChapter} zrdr");
        ctx.RequireData(missionZrdr, $"{BoatChapter}/{BoatMission} zrdr");
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var nets = AiNets.Load(chapterZrdr);
        var plans = CampaignRosterPlan.Build(AiSkills.LoadRoster(missionZrdr), defs, nets).Spawns
            .Where(p => p.Surface).ToList();
        if (plans.Count == 0)
        {
            throw new SuiteSkippedException($"{BoatChapter}/{BoatMission} authors no surface vehicle");
        }

        var report = new StringBuilder();
        SurveyRouteDraws(ctx, report, BoatChapter, BoatMission, defs);
        SurveyRouteDraws(ctx, report, GenChapter, GenMission, defs);
        ctx.WithWorld(BoatChapter, collision: false, BoatMission, world =>
        {
            var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
            var ends = new List<SurfaceVehicleRuntime>();
            try
            {
                SurfaceVehicleRuntime Build(string name)
                {
                    var runtime = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                        world.Runtime, defs, worldRoot)
                    { Name = $"surface_{name}" };
                    worldRoot.AddChild(runtime);
                    ends.Add(runtime);
                    foreach (var plan in plans)
                    {
                        if (runtime.Spawn(plan, plan.Position, plan.Forward) is { } hull && plan.Net is { } net)
                        {
                            hull.Patrol(net);
                        }
                    }

                    return runtime;
                }

                var host = Build("host");
                var guest = Build("guest");
                var unfed = Build("unfed");
                var local = Build("local");
                int count = host.Vessels.Count;
                ctx.Check(count == plans.Count && ends.All(e => e.Vessels.Count == count),
                    $"every end builds the same {count} hull(s) in the same order");
                ctx.Check(host.Vessels.All(v => v.Net != null),
                    $"every hull patrols a net of its own");
                if (count == 0)
                {
                    return;
                }

                var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(MeshSeed));
                var roster = new NetSeat[]
                {
                    new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = "player_pfighter" },
                    new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = "player_fbrand" },
                };
                NetSeats.Validate(roster);
                var hostNet = NetSession.Host(mesh[0], roster, 0x2E43UL);
                var guestNet = NetSession.Guest(mesh[1]);
                var hostLink = new NetWorldLink(hostNet, NoSeats(), null);
                var guestLink = new NetWorldLink(guestNet, NoSeats(), null);
                hostLink.FollowVehicles(host, null);
                guestLink.FollowVehicles(guest, null);
                unfed.Replicate();
                ctx.Check(!host.Replicated && host.Vessels.All(v => !v.Replicated)
                        && guest.Replicated && guest.Vessels.All(v => v.Replicated),
                    $"the host drives its own hulls and the guest's follow the host's");

                foreach (var end in ends)
                {
                    foreach (var hull in end.Vessels)
                    {
                        hull.Wake();
                    }
                }

                var rig = new Rig(host, guest, unfed, local, hostLink, hostNet, guestNet);
                rig.Step(PrerollSteps, null);
                ctx.Check(guestNet.Joined, $"the guest joined over the clean link");
                ctx.Check(guest.Vessels.All(v => v.Velocity.Length() > 0.1f),
                    $"every guest hull is under way on the host's samples");

                foreach (var (name, link) in Matrix)
                {
                    mesh[0].SetConditions(1, link);
                    mesh[1].SetConditions(0, link);
                    rig.Step(WarmSteps, null);
                    var cell = new CellRead(name, link, count);
                    int sentBefore = hostLink.SurfaceSamplesSent;
                    int takenBefore = guestLink.SurfaceSamplesTaken;
                    rig.Step(CellSteps - WarmSteps, cell);
                    cell.Sent = hostLink.SurfaceSamplesSent - sentBefore;
                    cell.Taken = guestLink.SurfaceSamplesTaken - takenBefore;
                    Judge(ctx, cell, count);
                    report.AppendLine(cell.Describe());
                    ctx.Note($"{cell.Describe()}");
                }
            }
            finally
            {
                foreach (var end in ends)
                {
                    end.Free();
                }
            }
        });

        ctx.WriteArtifact("test-net-surface-patrol.txt", report.ToString());
    }

    private static void Judge(TestContext ctx, CellRead cell, int count)
    {
        int expected = (CellSteps - WarmSteps) / NetWorldLink.SurfaceSendSteps * count;
        ctx.Check(Math.Abs(cell.Sent - expected) <= count,
            $"{cell.Name}: the host sent {cell.Sent} samples, one per hull every {NetWorldLink.SurfaceSendSteps} steps ({expected})");
        double arrived = cell.Taken / (double)Math.Max(1, cell.Sent);
        ctx.Check(arrived >= 1.0 - cell.Link.Loss - 0.1,
            $"{cell.Name}: the guest took {cell.Taken} of {cell.Sent}, what a {cell.Link.Loss:P0} loss leaves");
        for (int i = 0; i < count; i++)
        {
            float bar = cell.Bar(i);
            ctx.Check(cell.Moved[i] > bar,
                $"{cell.Name} hull {i}: the host's hull moved {cell.Moved[i]:0} m, more than the {bar:0.0} m bar, so the bar can be failed");
            ctx.Check(cell.GuestMax[i] <= bar,
                $"{cell.Name} hull {i}: the guest's hull stays within {bar:0.0} m of the host's (mean {cell.GuestMean(i):0.0}, max {cell.GuestMax[i]:0.0})");
            ctx.Check(cell.UnfedMean(i) > bar,
                $"{cell.Name} hull {i}: a guest never fed stands {cell.UnfedMean(i):0} m off on the mean, beyond the bar");
        }
    }

    // Where a hull's route could draw: the nodes of each net a surface vehicle rides with three or
    // more neighbours. A net with none walks the same nodes on every machine whatever the seed.
    private static void SurveyRouteDraws(TestContext ctx, StringBuilder report, string chapter,
        string missionName, VehicleDefs defs)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, missionName);
        if (!System.IO.Directory.Exists(chapterZrdr) || !System.IO.Directory.Exists(missionZrdr))
        {
            ctx.Note($"{chapter}/{missionName}: no zrdr at {chapterZrdr} or {missionZrdr}, route survey skipped");
            return;
        }

        var nets = AiNets.Load(chapterZrdr);
        var ridden = new List<(string Who, AiNet Net)>();
        var hullDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in CampaignRosterPlan.Build(AiSkills.LoadRoster(missionZrdr), defs, nets).Spawns)
        {
            if (plan.Surface)
            {
                hullDefs.Add(plan.Def);
                if (plan.Net is { } net)
                {
                    ridden.Add((plan.Name, net));
                }
            }
        }

        foreach (var (name, plan) in CampaignRosterPlan.GeneratorTemplates(missionZrdr, defs, nets))
        {
            if (plan.Surface)
            {
                hullDefs.Add(plan.Def);
                if (plan.Net is { } net)
                {
                    ridden.Add(($"launch {name}", net));
                }
            }
        }

        foreach (var objective in ObjectiveScript.Load(missionZrdr).Objectives)
        {
            foreach (var (who, netName) in objective.SetAiNet)
            {
                if (hullDefs.Any(d => who.Contains(d, StringComparison.OrdinalIgnoreCase))
                    && nets.FirstOrDefault(n => n.Name.Equals(netName, StringComparison.OrdinalIgnoreCase)) is { } net)
                {
                    ridden.Add(($"SET_AI_NET {who} (OBJECTIVE{objective.Number})", net));
                }
            }
        }

        foreach (var (who, net) in ridden)
        {
            string line = $"{chapter}/{missionName} {who}: net '{net.Name}' {net.Nodes.Count} node(s), {BranchNodes(net)} with three or more neighbours";
            report.AppendLine(line);
            ctx.Note($"{line}");
        }
    }

    private static int BranchNodes(AiNet net)
    {
        var neighbours = new HashSet<int>[net.Nodes.Count];
        for (int i = 0; i < neighbours.Length; i++)
        {
            neighbours[i] = new HashSet<int>();
        }

        foreach (var (a, b) in net.Edges)
        {
            if (a != b && a >= 0 && b >= 0 && a < neighbours.Length && b < neighbours.Length)
            {
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }
        }

        return neighbours.Count(n => n.Count >= 3);
    }

    private static NetWorldSeats NoSeats() => new()
    {
        SeatOfShooter = _ => -1,
        IsLocal = _ => false,
        ShooterOfSeat = _ => null,
        WeaponIndex = _ => -1,
        WeaponAt = _ => null,
    };

    // Distance on the water: each end reads its own water height, so height is not the stream's.
    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    // The four ends stepped in the session's order: the surface phase, the sends, then the link.
    // The link step is where the guest takes what arrived, for its next step.
    private sealed class Rig
    {
        private readonly SurfaceVehicleRuntime _host;
        private readonly SurfaceVehicleRuntime _guest;
        private readonly SurfaceVehicleRuntime _unfed;
        private readonly SurfaceVehicleRuntime _local;
        private readonly NetWorldLink _hostLink;
        private readonly NetSession _hostNet;
        private readonly NetSession _guestNet;

        public Rig(SurfaceVehicleRuntime host, SurfaceVehicleRuntime guest, SurfaceVehicleRuntime unfed,
            SurfaceVehicleRuntime local, NetWorldLink hostLink, NetSession hostNet, NetSession guestNet)
        {
            _host = host;
            _guest = guest;
            _unfed = unfed;
            _local = local;
            _hostLink = hostLink;
            _hostNet = hostNet;
            _guestNet = guestNet;
        }

        public void Step(int steps, CellRead? read)
        {
            const float dt = GameClock.FixedDt;
            for (int s = 0; s < steps; s++)
            {
                var before = read == null ? null : _host.Vessels.Select(v => v.Position).ToArray();
                foreach (var end in new[] { _host, _guest, _unfed, _local })
                {
                    end.SimStep(dt);
                }

                _hostLink.StepSends();
                _hostNet.Step(dt);
                _guestNet.Step(dt);
                if (read == null || before == null)
                {
                    continue;
                }

                for (int i = 0; i < read.Count; i++)
                {
                    var truth = _host.Vessels[i].Position;
                    read.Moved[i] += Flat(truth, before[i]);
                    read.TopSpeed[i] = Mathf.Max(read.TopSpeed[i], _host.Vessels[i].Velocity.Length());
                    float gap = Flat(_guest.Vessels[i].Position, truth);
                    read.GuestSum[i] += gap;
                    read.GuestMax[i] = Mathf.Max(read.GuestMax[i], gap);
                    read.UnfedSum[i] += Flat(_unfed.Vessels[i].Position, truth);
                    read.LocalMax[i] = Mathf.Max(read.LocalMax[i], Flat(_local.Vessels[i].Position, truth));
                }

                read.Samples++;
            }
        }
    }

    // One cell's reading per hull, over the steps after the warm-up.
    private sealed class CellRead
    {
        public CellRead(string name, LoopbackConditions link, int count)
        {
            Name = name;
            Link = link;
            Count = count;
            Moved = new float[count];
            TopSpeed = new float[count];
            GuestSum = new float[count];
            GuestMax = new float[count];
            UnfedSum = new float[count];
            LocalMax = new float[count];
        }

        public string Name { get; }

        public LoopbackConditions Link { get; }

        public int Count { get; }

        public int Samples { get; set; }

        public int Sent { get; set; }

        public int Taken { get; set; }

        public float[] Moved { get; }

        public float[] TopSpeed { get; }

        public float[] GuestSum { get; }

        public float[] GuestMax { get; }

        public float[] UnfedSum { get; }

        public float[] LocalMax { get; }

        public float GuestMean(int i) => GuestSum[i] / Math.Max(1, Samples);

        public float UnfedMean(int i) => UnfedSum[i] / Math.Max(1, Samples);

        // Speed times what the chase trails by, plus the link's jitter and the corner slack.
        public float Bar(int i) => TopSpeed[i] * (float)(Link.Latency + Link.Jitter
            + (1.0 / ZeppelinReplica.ChaseRatePerS) + TurnSlackS);

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{Name}: sent {Sent}, taken {Taken}");
            for (int i = 0; i < Count; i++)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"; hull {i} moved {Moved[i]:0} m at up to {TopSpeed[i]:0.0} m/s, guest mean {GuestMean(i):0.0} max {GuestMax[i]:0.0} m (bar {Bar(i):0.0}), unfed mean {UnfedMean(i):0}, own nets max {LocalMax[i]:0.0}");
            }

            return text.ToString();
        }
    }
}
