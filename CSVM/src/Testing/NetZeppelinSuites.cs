using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CSVM.Flight;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A mission's zeppelins flown by a host and followed by a guest over the loopback link,
/// through <see cref="NetSoakSuites"/>' four link cells. The two ends are bare zeppelin runtimes
/// joined by their <see cref="NetWorldLink"/>s. What is measured is the path stream alone, as how
/// far the guest's hull stands from the host's. A replicated guest that is never fed is the
/// control, and a guest flying its own follower is reported beside it.</summary>
internal static class NetZeppelinSuites
{
    // C3/M01 flies two zeppelins on nets of their own from the first step.
    private const string ZepChapter = "C3";
    private const string ZepMission = "M01";

    private const int MeshSeed = 7717;

    // Steps of clean link before the first cell, so every hull is under way when measuring starts.
    private const int PrerollSteps = 600;

    // Steps of each cell. The first WarmSteps after a change of conditions are not read.
    private const int CellSteps = 1800;
    private const int WarmSteps = 120;

    // What the decoded law trails by on a straight leg is speed times (latency + one second over
    // the chase rate). The bar adds jitter and this much slack for the turns.
    private const float TurnSlackS = 0.5f;

    private static readonly (string Name, LoopbackConditions Link)[] Matrix =
    {
        ("clean", LoopbackConditions.Perfect),
        ("50ms/5%", new LoopbackConditions(0.05, 0.01, 0.05)),
        ("100ms/10%", new LoopbackConditions(0.10, 0.02, 0.10)),
        ("200ms/20%", new LoopbackConditions(0.20, 0.04, 0.20)),
    };

    [Suite("net-zeppelin-path",
        "C3/M01's zeppelins flown by a host and followed by a guest through four link cells from "
        + "clean to 200 ms with 20 per cent loss: the host samples every hull on the original's half "
        + "second, the guest's hull stays within speed times (latency + jitter + the chase lag + "
        + "slack) of the host's in every cell, and a replicated guest never fed exceeds that bar")]
    internal static void ZeppelinPathsFollowTheHost(TestContext ctx)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, ZepChapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ZepChapter, ZepMission);
        ctx.RequireData(chapterZrdr, $"{ZepChapter} zrdr");
        ctx.RequireData(missionZrdr, $"{ZepChapter}/{ZepMission} zrdr");
        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        if (defs.Count == 0)
        {
            throw new SuiteSkippedException($"{ZepChapter}/{ZepMission} authors no zeppelin");
        }

        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(MeshSeed));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = "player_pfighter" },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = "player_fbrand" },
        };
        NetSeats.Validate(roster);

        var ends = new List<End>();
        try
        {
            var host = End.Build(ctx, "host", defs, nets);
            var guest = End.Build(ctx, "guest", defs, nets);
            var unfed = End.Build(ctx, "unfed", defs, nets);
            var local = End.Build(ctx, "local", defs, nets);
            ends.AddRange(new[] { host, guest, unfed, local });
            int count = host.Runtime.LiveCount;
            ctx.Check(count > 0 && ends.All(e => e.Runtime.LiveCount == count),
                $"every end places the same {count} zeppelin(s) in the same order");
            if (count == 0)
            {
                return;
            }

            var hostNet = NetSession.Host(mesh[0], roster, 0x2E99UL);
            var guestNet = NetSession.Guest(mesh[1]);
            var hostLink = new NetWorldLink(hostNet, NoSeats(), null);
            var guestLink = new NetWorldLink(guestNet, NoSeats(), null);
            hostLink.FollowZeppelins(host.Runtime);
            guestLink.FollowZeppelins(guest.Runtime);
            unfed.Runtime.Replicate();
            ctx.Check(host.Runtime.ReplicaAt(0) == null && guest.Runtime.ReplicaAt(0) != null,
                $"the host flies its own paths and the guest follows the host's");

            var rig = new Rig(host, guest, unfed, local, hostLink, guestLink, hostNet, guestNet);
            rig.Step(PrerollSteps, null);
            ctx.Check(guestNet.Joined, $"the guest joined over the clean link");

            var report = new StringBuilder();
            foreach (var (name, link) in Matrix)
            {
                mesh[0].SetConditions(1, link);
                mesh[1].SetConditions(0, link);
                rig.Step(WarmSteps, null);
                var cell = new CellRead(name, link, count);
                int sentBefore = hostLink.ZeppelinSamplesSent;
                int takenBefore = guestLink.ZeppelinSamplesTaken;
                rig.Step(CellSteps - WarmSteps, cell);
                cell.Sent = hostLink.ZeppelinSamplesSent - sentBefore;
                cell.Taken = guestLink.ZeppelinSamplesTaken - takenBefore;
                Judge(ctx, cell, count);
                report.AppendLine(cell.Describe());
                ctx.Note($"{cell.Describe()}");
            }

            ctx.WriteArtifact("test-net-zeppelin-path.txt", report.ToString());
        }
        finally
        {
            foreach (var end in ends)
            {
                end.Free();
            }
        }
    }

    private static void Judge(TestContext ctx, CellRead cell, int count)
    {
        int expected = (CellSteps - WarmSteps) / NetWorldLink.ZeppelinSendSteps * count;
        ctx.Check(Math.Abs(cell.Sent - expected) <= count,
            $"{cell.Name}: the host sent {cell.Sent} samples, one per hull every {NetWorldLink.ZeppelinSendSteps} steps ({expected})");
        double arrived = cell.Taken / (double)Math.Max(1, cell.Sent);
        ctx.Check(arrived >= 1.0 - cell.Link.Loss - 0.1,
            $"{cell.Name}: the guest took {cell.Taken} of {cell.Sent}, what a {cell.Link.Loss:P0} loss leaves");
        for (int i = 0; i < count; i++)
        {
            float bar = cell.Bar(i);
            ctx.Check(cell.Flown[i] > bar,
                $"{cell.Name} zeppelin {i}: the host's hull flew {cell.Flown[i]:0} m, more than the {bar:0.0} m bar, so the bar can be failed");
            ctx.Check(cell.GuestMax[i] <= bar,
                $"{cell.Name} zeppelin {i}: the guest's hull stays within {bar:0.0} m of the host's (mean {cell.GuestMean(i):0.0}, max {cell.GuestMax[i]:0.0})");
            ctx.Check(cell.UnfedMean(i) > bar,
                $"{cell.Name} zeppelin {i}: a guest never fed stands {cell.UnfedMean(i):0} m off on the mean, beyond the bar");
        }
    }

    private static NetWorldSeats NoSeats() => new()
    {
        SeatOfShooter = _ => -1,
        IsLocal = _ => false,
        ShooterOfSeat = _ => null,
        WeaponIndex = _ => -1,
        WeaponAt = _ => null,
    };

    private static float YawGap(float a, float b) =>
        Mathf.Abs(Mathf.Wrap(a - b, -Mathf.Pi, Mathf.Pi));

    // One end: its own parent node, one host node per record and a runtime placing them.
    private sealed class End
    {
        private End(Node3D root, ZeppelinRuntime runtime, Dictionary<string, Node3D> hulls)
        {
            Root = root;
            Runtime = runtime;
            Hulls = hulls;
        }

        public Node3D Root { get; }

        public ZeppelinRuntime Runtime { get; }

        public Dictionary<string, Node3D> Hulls { get; }

        public static End Build(TestContext ctx, string name, IReadOnlyList<ZeppelinDef> defs, IReadOnlyList<AiNet> nets)
        {
            var root = new Node3D { Name = $"zep_{name}" };
            ctx.Host.AddChild(root);
            var hulls = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                var hull = new Node3D { Name = def.Node };
                root.AddChild(hull);
                hulls[def.Node] = hull;
            }

            var runtime = new ZeppelinRuntime(defs, n => hulls.TryGetValue(n, out var h) ? h : null, nets);
            foreach (var def in defs)
            {
                runtime.Wake(def.Node);
            }

            return new End(root, runtime, hulls);
        }

        public Vector3 At(int index) =>
            Runtime.TryReadPath(index, out var p, out _, out _, out _) ? p : Vector3.Zero;

        public float YawAt(int index) =>
            Runtime.TryReadPath(index, out _, out _, out float yaw, out _) ? yaw : 0f;

        public void Free()
        {
            Runtime.Free();
            Root.Free();
        }
    }

    // The four ends stepped in the session's order: the zeppelin phase, the sends, then the link.
    // The link step is where the guest takes what arrived, for its next step.
    private sealed class Rig
    {
        private readonly End _host;
        private readonly End _guest;
        private readonly End _unfed;
        private readonly End _local;
        private readonly NetWorldLink _hostLink;
        private readonly NetSession _hostNet;
        private readonly NetSession _guestNet;

        public Rig(End host, End guest, End unfed, End local, NetWorldLink hostLink, NetWorldLink guestLink,
            NetSession hostNet, NetSession guestNet)
        {
            _host = host;
            _guest = guest;
            _unfed = unfed;
            _local = local;
            _hostLink = hostLink;
            _ = guestLink;
            _hostNet = hostNet;
            _guestNet = guestNet;
        }

        public void Step(int steps, CellRead? read)
        {
            const float dt = GameClock.FixedDt;
            for (int s = 0; s < steps; s++)
            {
                var before = read == null ? null : Enumerable.Range(0, read.Count).Select(_host.At).ToArray();
                foreach (var end in new[] { _host, _guest, _unfed, _local })
                {
                    end.Runtime.SimStep(dt);
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
                    var truth = _host.At(i);
                    read.Flown[i] += truth.DistanceTo(before[i]);
                    _host.Runtime.TryReadPath(i, out _, out float speed, out _, out _);
                    read.TopSpeed[i] = Mathf.Max(read.TopSpeed[i], speed);
                    float gap = _guest.At(i).DistanceTo(truth);
                    read.GuestSum[i] += gap;
                    read.GuestMax[i] = Mathf.Max(read.GuestMax[i], gap);
                    read.YawMax[i] = Mathf.Max(read.YawMax[i], YawGap(_guest.YawAt(i), _host.YawAt(i)));
                    read.UnfedSum[i] += _unfed.At(i).DistanceTo(truth);
                    read.LocalMax[i] = Mathf.Max(read.LocalMax[i], _local.At(i).DistanceTo(truth));
                }

                read.Samples++;
            }
        }
    }

    // One cell's reading per zeppelin, over the steps after the warm-up.
    private sealed class CellRead
    {
        public CellRead(string name, LoopbackConditions link, int count)
        {
            Name = name;
            Link = link;
            Count = count;
            Flown = new float[count];
            TopSpeed = new float[count];
            GuestSum = new float[count];
            GuestMax = new float[count];
            YawMax = new float[count];
            UnfedSum = new float[count];
            LocalMax = new float[count];
        }

        public string Name { get; }

        public LoopbackConditions Link { get; }

        public int Count { get; }

        public int Samples { get; set; }

        public int Sent { get; set; }

        public int Taken { get; set; }

        public float[] Flown { get; }

        public float[] TopSpeed { get; }

        public float[] GuestSum { get; }

        public float[] GuestMax { get; }

        public float[] YawMax { get; }

        public float[] UnfedSum { get; }

        public float[] LocalMax { get; }

        public float GuestMean(int i) => GuestSum[i] / Math.Max(1, Samples);

        public float UnfedMean(int i) => UnfedSum[i] / Math.Max(1, Samples);

        // Speed times what the decoded law trails by, plus the link's jitter and the turn slack.
        public float Bar(int i) => TopSpeed[i] * (float)(Link.Latency + Link.Jitter
            + (1.0 / ZeppelinReplica.ChaseRatePerS) + TurnSlackS);

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{Name}: sent {Sent}, taken {Taken}");
            for (int i = 0; i < Count; i++)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"; zep {i} flew {Flown[i]:0} m at up to {TopSpeed[i]:0.0} m/s, guest mean {GuestMean(i):0.0} max {GuestMax[i]:0.0} m (bar {Bar(i):0.0}), yaw max {Mathf.RadToDeg(YawMax[i]):0.0} deg, unfed mean {UnfedMean(i):0}, own follower max {LocalMax[i]:0.0}");
            }

            return text.ToString();
        }
    }
}
