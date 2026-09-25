using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using Godot;

namespace CSVM.Testing;

/// <summary>The mission director over the wire, on two built worlds of one mission. The host's
/// director runs its own rules and a guest's follows it event by event. C5/M02's opening completes
/// on a timer, and its ending is an objective-started cutscene raising the completion code. One
/// run therefore covers a transition, a world action and a cutscene code.
/// The mapping of what a guest replays and what it derives is docs/org/multiplayer-messages.md's.</summary>
internal static class NetDirectorSuites
{
    private const string Chapter = "C5";
    private const string Folder = "M02";
    private const float StepDt = 1f / 60f;
    private const int DockingCode = 13;

    // Long enough for C5/M02's first objective, dormant for two seconds, to wake and complete.
    private const float OpeningS = 3f;

    // As in the cutscene ownership suite: an inert definition's settle, then the ending's budget.
    private const float SettleS = 2f;
    private const float PlayBudgetS = 40f;

    // Several steps of link. In lockstep a shorter one lands in the frame that sent it, and a
    // guest without the catch-up would be in step by accident.
    private const double LatencyS = 0.1;

    private const ulong Seed = 0x5EEDD1EC70000021UL;
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-director-follow",
        "a host director and a guest director over two BUILT C5/M02 worlds, joined by a lossy "
        + "loopback: the guest's graph decides nothing alone and refuses the docking code, then "
        + "replays the host's transitions in order and in state, derives the ending cutscene's "
        + "codes from its own playback, ends Won when the host does, and records no attempt on "
        + "its own profile while the host's does. Under the link's latency the guest's cutscene "
        + "position and objective timers match the host's within one step of arrival, and a third "
        + "world following with the catch-up off trails by the latency")]
    internal static void DirectorFollow(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = CampaignSequence.Load(ctx.ZrdrPath).Cast<CampaignMission?>().FirstOrDefault(m =>
                string.Equals(m!.Value.ChapterFolder, Chapter, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Value.MissionFolder, Folder, StringComparison.OrdinalIgnoreCase))
            ?? throw new SuiteSkippedException($"cm_sequence carries no {Chapter}/{Folder}");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Folder);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(Chapter, collision: false, Folder, hostWorld =>
                ctx.WithWorld(Chapter, collision: false, Folder, guestWorld =>
                    ctx.WithWorld(Chapter, collision: false, Folder, controlWorld =>
                    {
                        if (ReferenceEquals(hostWorld.Runtime, guestWorld.Runtime)
                            || ReferenceEquals(hostWorld.Runtime, controlWorld.Runtime)
                            || ReferenceEquals(guestWorld.Runtime, controlWorld.Runtime))
                        {
                            throw new SuiteSkippedException($"{Chapter}/{Folder} is this run's cached world, so a second build is the same one");
                        }

                        Drive(ctx, script, mission, hostWorld, guestWorld, controlWorld, report);
                    })));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-net-director-follow-{Chapter}-{Folder}.txt", report.ToString());
        ctx.Note($"a guest director followed the host's through {Chapter}/{Folder}'s opening and its ending cutscene");
    }

    private static void Drive(TestContext ctx, ObjectiveScript script, CampaignMission mission,
        TestWorld hostWorld, TestWorld guestWorld, TestWorld controlWorld, StringBuilder report)
    {
        var mesh = LoopbackTransport.Mesh(3, new LoopbackConditions(LatencyS, 0.01, 0.25), new Random(2111));
        // The star: the control hears the host alone, as the guest does.
        mesh[1].Disconnect(2);
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
            new() { PeerId = 2, SeatIndex = 2, Callsign = "control", PlaneNode = Airframes[1] },
        };
        var host = new Peer(ctx, "host", hostWorld, script, mission, NetSession.Host(mesh[0], roster, Seed, null, Airframes), report);
        var guest = new Peer(ctx, "guest", guestWorld, script, mission, NetSession.Guest(mesh[1], Airframes), report);
        var control = new Peer(ctx, "control", controlWorld, script, mission, NetSession.Guest(mesh[2], Airframes), report);
        try
        {
            NetDirectorLink.Publish(host.Net, host.Graph, () => host.Now);
            guest.CatchUp = Follow(guest);
            control.CatchUp = Follow(control);
            control.CatchUp.Enabled = false;
            Play(ctx, script, host, guest, control, report);
        }
        finally
        {
            control.Close();
            guest.Close();
            host.Close();
        }
    }

    // A guest's shared clock is its own plus the slew Link opens, read at the frame's start.
    private static NetDirectorCatchUp Follow(Peer guest)
    {
        var catchUp = new NetDirectorCatchUp(
            () => guest.Slew?.HostTime(guest.Now) ?? guest.Now, guest.World.Runtime, guest.World.Runtime.Sounds);
        NetDirectorLink.Follow(guest.Net, guest.Graph, catchUp);
        return catchUp;
    }

    // The offset between the two clocks when the link opens, as the handshake gives a live guest.
    // It is exact here, since both clocks step together from now on.
    private static void Link(Peer host, Peer guest) => guest.Slew = new NetClockSlew(host.Now - guest.Now);

    private static void Play(TestContext ctx, ObjectiveScript script, Peer host, Peer guest, Peer control, StringBuilder report)
    {
        // The able-to-fail control: the guest alone over the time the host's opening takes.
        for (float t = 0f; t < OpeningS; t += StepDt)
        {
            guest.Frame();
            control.Frame();
        }

        bool refused = !guest.Graph.NotifyDockingComplete();
        report.AppendLine($"guest alone {OpeningS:0.#}s: {guest.Log.Count} transition(s), docking refused={refused}, ending={guest.Graph.Ending}");
        ctx.Check(guest.Log.Count == 0, $"a guest's graph run alone for {OpeningS:0.#}s raises no transition of its own (raised {guest.Log.Count})");
        ctx.Check(refused && !guest.Graph.Ending && !guest.Graph.Ended, $"and refuses the docking code, which is the host's to answer");

        Link(host, guest);
        Link(host, control);
        Linked(host, guest, control, OpeningS);
        report.AppendLine($"host opening {OpeningS:0.#}s: {host.Log.Count} transition(s), the guest replayed {guest.Log.Count}");
        ctx.Check(host.Log.Count > 0, $"the host's graph runs C5/M02's opening by its own rules over the same time ({host.Log.Count} transition(s))");

        var owner = script.Objectives.FirstOrDefault(d => d.WakeAnim is { } w && host.World.Runtime.Handles(w.Anim)
            && host.World.Runtime.CallClosureOf(w.Anim).Any(def => def.Sequences.Any(s => s.Events.Any(e => e.Kind == "Callback"))));
        ctx.Check(owner != null, $"C5/M02's script wakes a definition whose call closure raises a code");
        if (owner is null)
        {
            return;
        }

        string ownerAnim = owner.WakeAnim!.Value.Anim;
        host.Bind(ownerAnim);
        guest.Bind(ownerAnim);
        control.Bind(ownerAnim);
        guest.SampleOn(owner.Number, ownerAnim);
        control.SampleOn(owner.Number, ownerAnim);
        host.Graph.Wake(owner.Number);
        Linked(host, guest, control, SettleS + PlayBudgetS);

        CheckCatchUp(ctx, guest.Sample, control.Sample, report);

        report.AppendLine($"woke OBJECTIVE{owner.Number} '{owner.WakeAnim!.Value.Anim}' on the host");
        report.AppendLine($"host codes  {string.Join(",", host.Codes)}");
        report.AppendLine($"guest codes {string.Join(",", guest.Codes)}");
        foreach (var t in host.Log)
        {
            report.AppendLine($"  {t.Kind} {t.Number} from {t.Source}");
        }

        ctx.Check(host.Log.Select(Shape).SequenceEqual(guest.Log.Select(Shape)),
            $"the guest replays the host's {host.Log.Count} transitions in the order they were raised (guest {guest.Log.Count})");
        int diverged = Enumerable.Range(1, host.Graph.Count).Count(n =>
            (host.Graph.StateOf(n), host.Graph.AliveOf(n), host.Graph.CompletedOf(n))
            != (guest.Graph.StateOf(n), guest.Graph.AliveOf(n), guest.Graph.CompletedOf(n)));
        ctx.Check(diverged == 0 && host.Graph.Rows.SequenceEqual(guest.Graph.Rows),
            $"and holds the host's state for every objective and display row ({diverged} diverged)");
        ctx.Check(host.Codes.Contains(DockingCode) && host.Codes.SequenceEqual(guest.Codes),
            $"the guest's own playback raises the host's cutscene codes, the mission-completion code among them, with none sent");
        ctx.Check(host.Accepted == 1 && guest.Accepted == 0,
            $"the host's graph answers the completion code and the guest's refuses it (host {host.Accepted}, guest {guest.Accepted})");
        ctx.Check(host.Graph.Outcome == MissionOutcome.Won && guest.Graph.Outcome == MissionOutcome.Won,
            $"both graphs end Won (host {host.Graph.Outcome}, guest {guest.Graph.Outcome})");
        ctx.Check(host.Endings.SequenceEqual(guest.Endings) && host.Endings.Count == 1,
            $"the guest's ending carries the host's sounds ({string.Join(",", host.Endings)} against {string.Join(",", guest.Endings)})");
        ctx.Check(host.Director.Result?.Outcome == MissionOutcome.Won && guest.Director.Result?.Outcome == MissionOutcome.Won,
            $"both directors hold the Won result and the leaving hold");
        bool hostRecorded = CampaignProgression.ResultOf(host.Profile, host.Mission.Seq) != null;
        bool guestRecorded = CampaignProgression.ResultOf(guest.Profile, guest.Mission.Seq) != null;
        ctx.Check(hostRecorded && !guestRecorded,
            $"the attempt is recorded on the host's profile alone (host {hostRecorded}, guest {guestRecorded})");
    }

    // The guest ran at the frame it applied the owner's wake, against the host at the same frame.
    // The control shows the link is late enough for the match to mean something.
    private static void CheckCatchUp(TestContext ctx, Sample? guest, Sample? control, StringBuilder report)
    {
        const float Tolerance = StepDt * 1.01f;
        report.AppendLine($"catch-up guest: {guest}");
        report.AppendLine($"control guest:  {control}");
        ctx.Check(guest is { Missing: 0 } && control is { Missing: 0 },
            $"both guests apply the owner's wake and start every definition the host runs from it ({guest?.Missing}, {control?.Missing} missing)");
        if (guest is null || control is null)
        {
            return;
        }

        ctx.Check(guest.Lateness > 2f * StepDt,
            $"the wake arrives {guest.Lateness * 1000f:0} ms after the host raised it, more than two steps");
        ctx.Check(guest.ClockGap <= Tolerance && guest.TimerGap <= Tolerance,
            $"with the catch-up, the guest's cutscene positions and objective timers match the host's within one step of arrival (cutscene {guest.ClockGap * 1000f:0.#} ms, timers {guest.TimerGap * 1000f:0.#} ms)");
        ctx.Check(control.ClockGap > Tolerance && control.TimerGap > Tolerance,
            $"with it off, the control trails the host by the latency (cutscene {control.ClockGap * 1000f:0.#} ms, timers {control.TimerGap * 1000f:0.#} ms)");
    }

    // Host first, the order a listen server runs in, and a tail so the last reliable payloads land.
    private static void Linked(Peer host, Peer guest, Peer control, float seconds)
    {
        for (float t = 0f; t < seconds; t += StepDt)
        {
            host.Frame();
            guest.Frame();
            guest.TakeSample(host);
            control.Frame();
            control.TakeSample(host);
        }

        for (int i = 0; i < 10; i++)
        {
            host.Net.Step(StepDt);
            guest.Net.Step(StepDt);
            control.Net.Step(StepDt);
        }
    }

    private static (int, ObjectiveTransitionKind, int) Shape(ObjectiveTransition t) => (t.Number, t.Kind, t.Source);

    // One guest against the host, at the end of the frame the owner's wake arrived in. It holds the
    // widest cutscene and timer gaps, the owner's own gap, and the lateness.
    private sealed record Sample(float Lateness, float ClockGap, float OwnerGap, float TimerGap, int Missing)
    {
        public override string ToString() =>
            $"late {Lateness * 1000f:0} ms, cutscene gap {ClockGap * 1000f:0.#} ms (owner {OwnerGap * 1000f:0.#} ms), "
            + $"timer gap {TimerGap * 1000f:0.#} ms, {Missing} missing";
    }

    // One end: a built world, its director and cutscene host, and a session end of the link.
    private sealed class Peer
    {
        private readonly TestWorld _world;
        private readonly CutsceneController _cutscene = new();
        private readonly string _name;
        private readonly StringBuilder _report;
        private float _now;
        private int _sampleOwner;
        private string? _sampleAnim;
        private bool _woke;

        public Peer(TestContext ctx, string name, TestWorld world, ObjectiveScript script, CampaignMission mission,
            NetSession net, StringBuilder report)
        {
            _name = name;
            _world = world;
            _report = report;
            Net = net;
            Mission = mission;
            ctx.Host.AddChild(_cutscene);
            Director = CampaignDirector.Create(script, mission, Profile, null);
            Director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => Vector3.Zero,
                PlayerAircraft = () => null,
                Rng = new Random(1),
            });
            Graph = Director.Graph!;
            Graph.Transitioned += Log.Add;
            Graph.Transitioned += t => _woke |= t.Kind == ObjectiveTransitionKind.Woke && t.Number == _sampleOwner;
            Graph.EndingDecided += e => Endings.Add(e);
            _cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
            world.Runtime.MissionTriggerOwner = anim => _cutscene.Own(anim);
            world.Runtime.CallbackHost = (code, anim, root) =>
            {
                Codes.Add(code);
                _report.AppendLine($"  {_name} t={_now,6:0.00} code {code} from '{anim}'");
                return _cutscene.Host(code, anim, root);
            };
            _cutscene.MissionComplete = () =>
            {
                if (Graph.NotifyDockingComplete())
                {
                    Accepted++;
                }
            };
        }

        public TestWorld World => _world;

        public NetSession Net { get; }

        public CampaignMission Mission { get; }

        public CampaignProfileDef Profile { get; } = CampaignProfileDef.NewProfile("Zachary");

        public CampaignDirector Director { get; }

        public ObjectiveGraph Graph { get; }

        public List<ObjectiveTransition> Log { get; } = new();

        public List<MissionEnding> Endings { get; } = new();

        public List<int> Codes { get; } = new();

        public int Accepted { get; private set; }

        public float Now => _now;

        public NetClockSlew? Slew { get; set; }

        public NetDirectorCatchUp? CatchUp { get; set; }

        public Sample? Sample { get; private set; }

        // Arms one sample, taken after the frame this graph replays the owner's wake in.
        public void SampleOn(int owner, string ownerAnim)
        {
            _sampleOwner = owner;
            _sampleAnim = ownerAnim;
        }

        public void TakeSample(Peer host)
        {
            if (!_woke || Sample != null || _sampleAnim is null)
            {
                return;
            }

            float clockGap = 0f;
            float ownerGap = 0f;
            int missing = 0;
            foreach (var def in host._world.Runtime.CallClosureOf(_sampleAnim))
            {
                if (def.AnimName is not { Length: > 0 } name || host._world.Runtime.ClockOf(name) is not { } theirs)
                {
                    continue;
                }

                if (_world.Runtime.ClockOf(name) is not { } ours)
                {
                    missing++;
                    continue;
                }

                float gap = MathF.Abs(theirs - ours);
                clockGap = MathF.Max(clockGap, gap);
                if (string.Equals(name, _sampleAnim, StringComparison.OrdinalIgnoreCase))
                {
                    ownerGap = gap;
                }
            }

            // Only an objective the host has woken or napped has a timer both clocks started together.
            float timerGap = 0f;
            foreach (int n in host.Log.Where(t => t.Kind is ObjectiveTransitionKind.Woke or ObjectiveTransitionKind.Napped)
                         .Select(t => t.Number).Distinct())
            {
                timerGap = MathF.Max(timerGap, MathF.Abs(host.Graph.TimerOf(n) - Graph.TimerOf(n)));
                timerGap = MathF.Max(timerGap, MathF.Abs(host.Graph.NapRemainingOf(n) - Graph.NapRemainingOf(n)));
            }

            if (host.Graph.TimerRunning)
            {
                timerGap = MathF.Max(timerGap, MathF.Abs(host.Graph.TimerRemaining - Graph.TimerRemaining));
            }

            Sample = new Sample(CatchUp?.LastLateness ?? 0f, clockGap, ownerGap, timerGap, missing);
        }

        public void Bind(string ownerAnim)
        {
            var names = new List<string>();
            foreach (var def in _world.Runtime.CallClosureOf(ownerAnim))
            {
                if (def.AnimName is { Length: > 0 } name && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            _cutscene.HostDefinitions(names);
        }

        // The clock moves after the runtime and before the graph. The host stamps an event at its
        // frame's end, and a guest reads its clock at its own frame's start.
        public void Frame()
        {
            Net.Step(StepDt);
            _world.Runtime.Advance(StepDt);
            _now += StepDt;
            Graph.Step(StepDt);
            _cutscene.Tick();
        }

        public void Close()
        {
            foreach (var spawned in Director.Roster.Values)
            {
                spawned.Free();
            }

            _world.Runtime.CallbackHost = null;
            _world.Runtime.MissionTriggerOwner = null;
            _cutscene.Free();
        }
    }
}
