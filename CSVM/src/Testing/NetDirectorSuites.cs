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

    private const ulong Seed = 0x5EEDD1EC70000021UL;
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-director-follow",
        "a host director and a guest director over two BUILT C5/M02 worlds, joined by a lossy "
        + "loopback: the guest's graph decides nothing alone and refuses the docking code, then "
        + "replays the host's transitions in order and in state, derives the ending cutscene's "
        + "codes from its own playback, ends Won when the host does, and records no attempt on "
        + "its own profile while the host's does")]
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
                {
                    if (ReferenceEquals(hostWorld.Runtime, guestWorld.Runtime))
                    {
                        throw new SuiteSkippedException($"{Chapter}/{Folder} is this run's cached world, so a second build is the same one");
                    }

                    Drive(ctx, script, mission, hostWorld, guestWorld, report);
                }));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-net-director-follow-{Chapter}-{Folder}.txt", report.ToString());
        ctx.Note($"a guest director followed the host's through {Chapter}/{Folder}'s opening and its ending cutscene");
    }

    private static void Drive(TestContext ctx, ObjectiveScript script, CampaignMission mission,
        TestWorld hostWorld, TestWorld guestWorld, StringBuilder report)
    {
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(2111));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        var host = new Peer(ctx, "host", hostWorld, script, mission, NetSession.Host(mesh[0], roster, Seed, null, Airframes), report);
        var guest = new Peer(ctx, "guest", guestWorld, script, mission, NetSession.Guest(mesh[1], Airframes), report);
        try
        {
            NetDirectorLink.Publish(host.Net, host.Graph);
            NetDirectorLink.Follow(guest.Net, guest.Graph);
            Play(ctx, script, host, guest, report);
        }
        finally
        {
            guest.Close();
            host.Close();
        }
    }

    private static void Play(TestContext ctx, ObjectiveScript script, Peer host, Peer guest, StringBuilder report)
    {
        // The able-to-fail control: the guest alone over the time the host's opening takes.
        for (float t = 0f; t < OpeningS; t += StepDt)
        {
            guest.Frame();
        }

        bool refused = !guest.Graph.NotifyDockingComplete();
        report.AppendLine($"guest alone {OpeningS:0.#}s: {guest.Log.Count} transition(s), docking refused={refused}, ending={guest.Graph.Ending}");
        ctx.Check(guest.Log.Count == 0, $"a guest's graph run alone for {OpeningS:0.#}s raises no transition of its own (raised {guest.Log.Count})");
        ctx.Check(refused && !guest.Graph.Ending && !guest.Graph.Ended, $"and refuses the docking code, which is the host's to answer");

        Linked(host, guest, OpeningS);
        report.AppendLine($"host opening {OpeningS:0.#}s: {host.Log.Count} transition(s), the guest replayed {guest.Log.Count}");
        ctx.Check(host.Log.Count > 0, $"the host's graph runs C5/M02's opening by its own rules over the same time ({host.Log.Count} transition(s))");

        var owner = script.Objectives.FirstOrDefault(d => d.WakeAnim is { } w && host.World.Runtime.Handles(w.Anim)
            && host.World.Runtime.CallClosureOf(w.Anim).Any(def => def.Sequences.Any(s => s.Events.Any(e => e.Kind == "Callback"))));
        ctx.Check(owner != null, $"C5/M02's script wakes a definition whose call closure raises a code");
        if (owner is null)
        {
            return;
        }

        host.Bind(owner.WakeAnim!.Value.Anim);
        guest.Bind(owner.WakeAnim!.Value.Anim);
        host.Graph.Wake(owner.Number);
        Linked(host, guest, SettleS + PlayBudgetS);

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

    // Host first, the order a listen server runs in, and a tail so the last reliable payloads land.
    private static void Linked(Peer host, Peer guest, float seconds)
    {
        for (float t = 0f; t < seconds; t += StepDt)
        {
            host.Frame();
            guest.Frame();
        }

        for (int i = 0; i < 10; i++)
        {
            host.Net.Step(StepDt);
            guest.Net.Step(StepDt);
        }
    }

    private static (int, ObjectiveTransitionKind, int) Shape(ObjectiveTransition t) => (t.Number, t.Kind, t.Source);

    // One end: a built world, its director and cutscene host, and a session end of the link.
    private sealed class Peer
    {
        private readonly TestWorld _world;
        private readonly CutsceneController _cutscene = new();
        private readonly string _name;
        private readonly StringBuilder _report;
        private float _now;

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

        public void Frame()
        {
            Net.Step(StepDt);
            _world.Runtime.Advance(StepDt);
            Graph.Step(StepDt);
            _cutscene.Tick();
            _now += StepDt;
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
