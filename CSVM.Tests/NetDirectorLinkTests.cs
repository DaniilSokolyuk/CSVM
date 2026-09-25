using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The objectives graph over the wire with no engine under it. A host graph runs its own rules, a
/// replicated graph on a second session replays what the host's raised, and a loopback carries
/// it. A replicated graph must decide nothing, so every test here also reads
/// what it did NOT do.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetDirectorLinkTests
{
    private const ulong Seed = 0x0badcafe0bad1dea;
    private const float Step = 0.1f;

    // A lossy, jittery link. The director's events are all reliable, so neither the loss nor the
    // jitter may reorder or thin what the guest replays.
    private static readonly LoopbackConditions Link = new(0.03, 0.01, 0.25);

    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Fact]
    public void A_replicated_graph_decides_nothing_of_its_own()
    {
        const string body = "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0]],\"OBJECTIVE2\",[\"INSTANTWIN\"]";
        var (graph, world) = Build(body);
        graph.Replicate();
        var log = new List<ObjectiveTransition>();
        graph.Transitioned += log.Add;
        for (int i = 0; i < 100; i++)
        {
            graph.Step(Step);
        }

        graph.Wake(1);
        Assert.False(graph.NotifyDockingComplete());
        Assert.False(graph.NotifyPlayerLost());
        Assert.Empty(log);
        Assert.False(graph.Ending || graph.Ended);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(1));
        Assert.Equal(0, world.Queries);
        Assert.InRange(graph.Elapsed, 9.9f, 10.1f);

        // ABLE-TO-FAIL CONTROL. The same script run by its own rules wakes, completes and ends
        // inside those same steps. The stillness above is the replication, not the script.
        var (own, _) = Build(body);
        for (int i = 0; i < 100; i++)
        {
            own.Step(Step);
        }

        Assert.Equal(MissionOutcome.Won, own.Outcome);
        Assert.True(own.CompletedOf(1));
    }

    [Fact]
    public void A_hidden_objective_is_a_transition_and_the_guest_hides_it_too()
    {
        var pair = Pair("\"OBJECTIVE1\",[\"HIDE_OBJ\",[2]],"
            + "\"OBJECTIVE2\",[\"INACTIVE1\",[\"never\"]]");
        pair.Run(1f);
        Assert.Contains(pair.HostLog, t => t is { Number: 2, Kind: ObjectiveTransitionKind.Hidden, Source: 1 });
        Assert.True(pair.Guest.CompletedOf(2));
        Assert.Equal(ObjectiveState.Retired, pair.Guest.StateOf(2));
        pair.AssertMirrored();
    }

    [Fact]
    public void An_ending_crosses_with_its_sounds_and_the_wrap_up_stays_the_hosts()
    {
        var pair = Pair("\"MISSION_WON_SOUND\",[\"snd_won\"],\"OBJECTIVES_WON_SOUND\",[\"snd_all\"],"
            + "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[0.5],\"WON\"]");
        int guestEnded = 0;
        pair.Guest.MissionEnded += _ => guestEnded++;
        pair.Run(1.2f);
        Assert.True(pair.Host.Ending);
        Assert.True(pair.Guest.Ending);
        Assert.Equal(0, guestEnded);
        Assert.Equal(new[] { "snd_all", "snd_won" }, pair.GuestWorld.Sounds.TakeLast(2));

        pair.Run(4f);
        Assert.Equal(MissionOutcome.Won, pair.Host.Outcome);
        Assert.Equal(MissionOutcome.Won, pair.Guest.Outcome);
        Assert.Equal(1, guestEnded);
        pair.AssertMirrored();
    }

    [Fact]
    public void The_countdown_runs_on_the_guest_but_only_the_host_lets_it_expire()
    {
        var pair = Pair("\"MISSION_TIMER\",[10.0],\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[0.1],"
            + "\"RESET_TIMER\",[1.0],\"INACTIVE1\",[\"never\"]]");
        int expired = 0;
        pair.Guest.TimerExpired += () => expired++;
        pair.Run(0.6f);
        Assert.True(pair.Guest.TimerRunning);
        Assert.InRange(pair.Guest.TimerRemaining, 0.3f, 0.8f);

        pair.Run(5f);
        Assert.Equal(1, expired);
        Assert.False(pair.Guest.TimerRunning);
        Assert.Equal(MissionOutcome.Lost, pair.Guest.Outcome);
    }

    [Fact]
    public void A_late_wake_starts_the_guests_timers_as_far_along_as_the_hosts()
    {
        const string body = "\"MISSION_TIMER\",[10.0],\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[0.5],"
            + "\"RESET_TIMER\",[5.0],\"INACTIVE1\",[\"never\"]]";
        var slow = new LoopbackConditions(0.35, 0.0, 0.0);
        var pair = new Linked(Script(body), slow, catchUp: true);
        pair.Run(1.5f);
        Assert.InRange(pair.CatchUp!.LastLateness, 0.15f, 0.45f);
        Assert.Equal(pair.Host.TimerOf(1), pair.Guest.TimerOf(1), 3);
        Assert.Equal(pair.Host.TimerRemaining, pair.Guest.TimerRemaining, 3);

        // ABLE-TO-FAIL CONTROL. With the catch-up off the guest's timers trail by the link. The
        // match above is therefore the catch-up, not a prompt link.
        var control = new Linked(Script(body), slow, catchUp: false);
        control.Run(1.5f);
        Assert.InRange(control.CatchUp!.LastLateness, 0.15f, 0.45f);
        Assert.True(control.Host.TimerOf(1) - control.Guest.TimerOf(1) > 0.15f);
        Assert.True(control.Guest.TimerRemaining - control.Host.TimerRemaining > 0.15f);
    }

    [Fact]
    public void Lateness_is_the_shared_clock_past_the_stamp_and_never_negative()
    {
        Assert.Equal(0.25f, NetDirectorCatchUp.Lateness(100.25, 100f), 5);
        Assert.Equal(0f, NetDirectorCatchUp.Lateness(99.5, 100f));
        Assert.Equal(0f, NetDirectorCatchUp.Lateness(double.NaN, 100f));
        Assert.Equal(0f, NetDirectorCatchUp.Lateness(100.0, float.PositiveInfinity));
    }

    [Fact]
    public void A_late_start_lands_in_the_clip_the_overdue_falls_in()
    {
        var lengths = new[] { 1.0, 0.0, 2.0 };
        Assert.Equal((0, 0.0), LateStart.Into(lengths, 0.0));
        Assert.Equal((0, 0.0), LateStart.Into(lengths, -3.0));
        Assert.Equal((0, 0.5), LateStart.Into(lengths, 0.5));
        Assert.Equal((2, 0.5), LateStart.Into(lengths, 1.5));
        Assert.Equal((3, 0.0), LateStart.Into(lengths, 3.0));
        Assert.Equal((0, 0.0), LateStart.Into(lengths, double.NaN));
    }

    [ExtractedDataFact]
    public void A_replicated_graph_replays_C1_M04s_chain_in_state_and_in_world_actions()
    {
        // The same route ObjectiveGraphTests drives on one graph. It takes the tower down inside
        // the distress window, then a Promised Land hatch, the squad wake and the DEDG chain.
        var script = ObjectiveScript.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "M04"));
        var pair = new Linked(script);
        pair.Host.Wake(14);
        pair.HostWorld.Inactive.Add("rtwr_healthy");
        pair.Run(22f);
        pair.HostWorld.Anims["destroy_hkzep_rbroad1"] = 4;
        pair.Run(30f);
        pair.HostWorld.GroupLive[2] = 2;
        pair.Run(6f);
        pair.Run(95f);
        pair.HostWorld.Inactive.Add("panelleft1");
        pair.Run(1f);
        pair.HostWorld.GroupLive[1] = 0;
        pair.HostWorld.GroupLive[2] = 0;
        pair.HostWorld.GroupLive[5] = 0;
        pair.Run(3f);

        Assert.True(pair.Host.CompletedOf(31));
        Assert.True(pair.HostLog.Count > 30);
        pair.AssertMirrored();
        Assert.Contains("pzhookpoint", pair.Guest.ObjectiveTargets);
        Assert.Contains("blakebloodhawk_1", pair.GuestWorld.Actions);
    }

    private static (ObjectiveGraph Graph, TraceWorld World) Build(string body)
    {
        var world = new TraceWorld();
        return (new ObjectiveGraph(Script(body), world), world);
    }

    private static Linked Pair(string body) => new(Script(body));

    private static ObjectiveScript Script(string body)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "objectives.json"), "[[" + body + "]]");
        return ObjectiveScript.Load(dir);
    }

    // A host graph and a replicated graph over one script, each on its own session end of a
    // lossy loopback and its own recording world. Only the host's world answers conditions.
    private sealed class Linked
    {
        private readonly NetSession _hostNet;
        private readonly NetSession _guestNet;

        private float _hostNow;
        private float _guestNow;

        public Linked(ObjectiveScript script, LoopbackConditions? link = null, bool? catchUp = null)
        {
            var mesh = LoopbackTransport.Mesh(2, link ?? Link, new Random(2111));
            var roster = new NetSeat[]
            {
                new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
                new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
            };
            _hostNet = NetSession.Host(mesh[0], roster, Seed, null, Airframes);
            _guestNet = NetSession.Guest(mesh[1], Airframes);
            Host = new ObjectiveGraph(script, HostWorld);
            Guest = new ObjectiveGraph(script, GuestWorld);
            Host.Transitioned += HostLog.Add;
            Guest.Transitioned += GuestLog.Add;
            NetDirectorLink.Publish(_hostNet, Host, () => _hostNow);
            if (catchUp is { } enabled)
            {
                CatchUp = new NetDirectorCatchUp(() => _guestNow, null, null) { Enabled = enabled };
            }

            NetDirectorLink.Follow(_guestNet, Guest, CatchUp);
        }

        public NetDirectorCatchUp? CatchUp { get; }

        public TraceWorld HostWorld { get; } = new();

        public TraceWorld GuestWorld { get; } = new();

        public ObjectiveGraph Host { get; }

        public ObjectiveGraph Guest { get; }

        public List<ObjectiveTransition> HostLog { get; } = new();

        public List<ObjectiveTransition> GuestLog { get; } = new();

        // Host first, the order a listen server runs in, with the net stepped ahead of each graph.
        // Each clock moves before its graph steps. The host stamps an event at its frame's end, and
        // the guest reads its clock at its frame's start. A tail lets the last payloads land.
        public void Run(float seconds)
        {
            for (float t = 0f; t < seconds; t += Step)
            {
                _hostNet.Step(Step);
                _hostNow += Step;
                Host.Step(Step);
                _guestNet.Step(Step);
                _guestNow += Step;
                Guest.Step(Step);
            }

            for (int i = 0; i < 3; i++)
            {
                _hostNet.Step(Step);
                _guestNet.Step(Step);
            }
        }

        public void AssertMirrored()
        {
            Assert.NotEmpty(HostLog);
            Assert.Equal(HostLog.Select(Shape), GuestLog.Select(Shape));
            for (int n = 1; n <= Host.Count; n++)
            {
                Assert.Equal((Host.StateOf(n), Host.AliveOf(n), Host.CompletedOf(n)),
                    (Guest.StateOf(n), Guest.AliveOf(n), Guest.CompletedOf(n)));
            }

            Assert.Equal(Host.Rows, Guest.Rows);
            Assert.Equal(Host.ObjectiveTargets.OrderBy(k => k), Guest.ObjectiveTargets.OrderBy(k => k));
            Assert.Equal(Host.OtherTargets.OrderBy(k => k), Guest.OtherTargets.OrderBy(k => k));
            Assert.Equal(HostWorld.Actions, GuestWorld.Actions);
            Assert.Equal(0, GuestWorld.Queries);
            Assert.Equal(0, Guest.UnresolvedConditions);
        }

        // What a transition is on both machines. The mission time is each machine's own reading
        // and the guest's runs a link behind, so it is left out.
        private static (int, ObjectiveTransitionKind, int, float, bool) Shape(ObjectiveTransition t) =>
            (t.Number, t.Kind, t.Source, t.Seconds, t.Gated);
    }

    // Answers only what a test sets and counts every condition it is asked. It records every
    // world action in order, so two graphs' traces can be compared call for call.
    private sealed class TraceWorld : IObjectiveWorld
    {
        public HashSet<string> Inactive { get; } = new();

        public Dictionary<string, int> Anims { get; } = new();

        public Dictionary<int, int> GroupLive { get; } = new();

        public List<string> Actions { get; } = new();

        public List<string> Sounds { get; } = new();

        public int Queries { get; private set; }

        public bool? NodeInactive(IReadOnlyList<string> path)
        {
            Queries++;
            return Inactive.Contains(path[^1]);
        }

        public int AnimState(string anim)
        {
            Queries++;
            return Anims.TryGetValue(anim, out int s) ? s : 0;
        }

        public int? GroupLiveCount(int group, string? generator)
        {
            Queries++;
            return GroupLive.TryGetValue(group, out int live) ? live : null;
        }

        // A condition's own side effect, so a query rather than an action.
        public void WidenGroupEngagement(int group) => Queries++;

        public bool? TravelersMet(TravelersSpec spec)
        {
            Queries++;
            return null;
        }

        public void WakeupEnemies(IReadOnlyList<string> names) => Actions.AddRange(names);

        public void WakeupTurrets(IReadOnlyList<string> patterns) => Record("turrets", patterns);

        public void WakeupZepTurrets(IReadOnlyList<string> nodes) => Record("zepturrets", nodes);

        public void WakeupGenerator(string name, int count) => Actions.Add($"generator {name} {count}");

        public void WakeAnim(string anim, string? node) => Actions.Add($"anim {anim} {node}");

        public void PlaySoundGroup(string group)
        {
            Sounds.Add(group);
            Actions.Add($"sound {group}");
        }

        public void StopQueuedSounds(IReadOnlyList<string> names) => Record("stop", names);

        public void WarpVehicle(string vehicle, IReadOnlyList<WarpPoint> points) => Actions.Add($"warp {vehicle}");

        public void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries) =>
            Record("team", entries.Select(e => $"{e.Name}={e.Team}").ToList());

        public void SetAiNet(IReadOnlyList<(string Name, string Net)> entries) =>
            Record("net", entries.Select(e => $"{e.Name}={e.Net}").ToList());

        public void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries) =>
            Record("radius", entries.Select(e => $"{e.Name}={e.Radius}").ToList());

        public void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries) =>
            Record("zepcannons", entries.Select(e => $"{e.Zeppelin}={e.Flag}").ToList());

        public void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries) =>
            Record("stoppoint", entries.Select(e => $"{e.Net}/{e.Stop}={e.Flag}").ToList());

        public void StartTaxi(IReadOnlyList<string> names) => Record("taxi", names);

        // An empty list is still a call the graph made, so it is recorded. The two traces then
        // agree call for call, not only on the calls that carried something.
        private void Record(string what, IReadOnlyList<string> items) =>
            Actions.Add($"{what} {string.Join(",", items)}");
    }
}
