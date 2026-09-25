using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A guest flown elsewhere as part of the host's human field. A host and a guest session
/// share a lossy loopback, and a director running C3/M01's shipped script reads the host's field.
/// A second director over the host's panes alone runs beside it as the able-to-fail control.
/// Those panes are the field a session read before guests joined it.</summary>
internal static class NetHumanFieldSuites
{
    private const string Chapter = "C3";

    // The script is C3/M01's; the world both ends build is the same chapter's MP1, a lighter build
    // of the same terrain. The objective is read by coordinates, so it needs no mission node.
    private const string ScriptMission = "M01";
    private const string StageMission = "MP1";

    // C3/M01's OBJECTIVE5: BEGIN_DORMANT with one condition, TRAVELERS ["player", "APPROACHING",
    // [-5458, 120, -5390], 1100]. Re-read off the shipped objectives.zrd before it is flown.
    private const int TravelersObjective = 5;
    private const float TravelersRadius = 1100f;

    // ObjectiveGraph completes at most one objective per tick from a rotating scan, so a leg runs
    // past every armed objective of the mission, not one.
    private const float ScanWindow = 5f;

    // Long enough for a wreck to fall from the objective's height to the ground or under the map.
    private const float WreckWindow = 30f;

    // How close the host's copy of the guest has to stand to the point the guest pinned itself
    // at. The pose crosses as a sample, so this is the buffer's error on a held aeroplane.
    private const float ArrivalToleranceM = 5f;

    private const ulong HostSeed = 0xC23A0001UL;

    // Where the two pilots wait, well clear of the radius and on opposite sides of the point.
    private static readonly Vector3 HostWaits = new(4000f, 400f, 0f);
    private static readonly Vector3 GuestWaits = new(-4000f, 400f, 0f);

    // The airframe order NetCombatSuites.Ends hands every peer.
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-human-field",
        "a host and a guest session over a 30 ms, 25 per cent lossy loopback, with a director on "
        + "the host reading C3/M01's own script: the host's human field holds the guest's seat at "
        + "its interpolated pose, OBJECTIVE5's 'player' TRAVELERS completes when the guest reaches "
        + "the point on its own machine while the host waits 4 km out, the host's death leaves the "
        + "mission flying while the guest does, and the guest's own death report plays its wreck "
        + "on the host and loses the mission there; a director over the host's panes alone "
        + "completes nothing and loses on the host's death")]
    internal static void GuestsAreTheHostsHumanField(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string scriptZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, ScriptMission);
        string stageZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, StageMission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, Chapter), $"{Chapter} gamez");
        ctx.RequireData(scriptZrdr, $"{Chapter}/{ScriptMission} zrdr");
        ctx.RequireData(stageZrdr, $"{Chapter}/{StageMission} zrdr");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(ScriptMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }

        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{ScriptMission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(scriptZrdr);
        var where = AuthoredPoint(ctx, script);
        var report = new StringBuilder();

        // Free flight on the stage, not a match. A Dogfight reports deaths from its own scoring
        // handler, and the campaign's report is the one this suite has to see cross.
        var spec = SessionSpec.Parse(new[]
        {
            "--fly", $"--chapter={Chapter}", $"--mission={StageMission}", "--players=1", "--mute",
            "--no-pads",
        });
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(2323));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        NetSeats.Validate(roster);

        var ambient = NetCombatSuites.Ambient.Save();
        NetCombatSuites.Ends? host = null;
        NetCombatSuites.Ends? guest = null;
        try
        {
            host = NetCombatSuites.Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = NetCombatSuites.Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            var run = new Run(host.Session, guest.Session, script, mission, scriptZrdr);
            Field(ctx, run, report);
            Arrival(ctx, run, where, report);
            Wrecks(ctx, run, report);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            ambient.Restore();
        }

        ctx.WriteArtifact($"test-net-human-field-{Chapter}-{ScriptMission}.txt", report.ToString());
        ctx.Note($"{report.ToString().TrimEnd().Replace(System.Environment.NewLine, "; ")}");
    }

    // The shipped condition, read off the data before anything is flown.
    private static Vector3 AuthoredPoint(TestContext ctx, ObjectiveScript script)
    {
        ObjectiveDef? target = null;
        foreach (var def in script.Objectives)
        {
            if (def.Number == TravelersObjective)
            {
                target = def;
            }
        }

        var spec = target?.Travelers;
        if (spec?.WherePoint is not { Length: 3 } point || spec.Group != null || !spec.Approaching
            || !string.Equals(spec.Who, "player", StringComparison.OrdinalIgnoreCase))
        {
            throw new SuiteSkippedException(
                $"{Chapter}/{ScriptMission} OBJECTIVE{TravelersObjective} no longer authors a player TRAVELERS point");
        }

        ctx.Check(Mathf.IsEqualApprox(spec.Radius, TravelersRadius) && target!.BeginDormant,
            $"OBJECTIVE{TravelersObjective} is the authored dormant 'player' TRAVELERS at {spec.Radius:0} m");
        return new Vector3(point[0], point[1], point[2]);
    }

    // The seat list the field is read over: one pane on each machine, two seats on both.
    private static void Field(TestContext ctx, Run run, StringBuilder report)
    {
        var field = run.Host.HumanField;
        ctx.Check(field.Count == 2 && !field[0].RemoteOwned && field[1].RemoteOwned
                  && ReferenceEquals(field[0], run.HostP1) && ReferenceEquals(field[1], run.HostCopy),
            $"the host's human field is both seats in seat order, its own aeroplane then the guest's copy ({field.Count} human(s))");
        ctx.Check(run.Host.Rigs.Count == 1 && run.PaneField().Count == 1,
            $"…while the host builds one pane, which is the field a session read before a guest joined it");
        var far = run.Guest.HumanField;
        ctx.Check(far.Count == 2 && far[0].RemoteOwned && !far[1].RemoteOwned,
            $"and the guest's field is the same two seats, the host's being the one flown elsewhere there");
        ctx.Check(run.Host.NetSeats[0].IsLocal,
            $"the scripted player is the host's seat 0, which a guest never holds");
        report.AppendLine($"field: host reads {field.Count} human(s), 1 pane; guest reads {far.Count}");
    }

    // OBJECTIVE5 asks where "the player" is. The guest flies there on its own machine and the host
    // learns it only from the guest's samples.
    private static void Arrival(TestContext ctx, Run run, Vector3 where, StringBuilder report)
    {
        run.Pin(run.HostP1, where + HostWaits);
        run.Pin(run.GuestOwn, where + GuestWaits);
        run.WholeGraph.Wake(TravelersObjective);
        run.PaneGraph.Wake(TravelersObjective);
        run.Step(ScanWindow);
        ctx.Check(!run.WholeGraph.CompletedOf(TravelersObjective)
                  && !run.PaneGraph.CompletedOf(TravelersObjective),
            $"with both pilots 4 km out, OBJECTIVE{TravelersObjective} completes on neither director");

        run.Pin(run.GuestOwn, where);
        run.Step(ScanWindow);
        float copyOff = run.HostCopy.WorldPosition.DistanceTo(where);
        ctx.Check(copyOff < ArrivalToleranceM,
            $"the host's copy of the guest stands at the point the guest pinned itself at on its own machine ({copyOff:0.00} m off)");
        ctx.Check(run.WholeGraph.CompletedOf(TravelersObjective),
            $"and OBJECTIVE{TravelersObjective} completes on the host off the guest's arrival");
        float p1Off = run.HostP1.WorldPosition.DistanceTo(where);
        ctx.Check(p1Off > TravelersRadius,
            $"…with the scripted player {p1Off:0} m out, past the {TravelersRadius:0} m radius, so only the guest can have settled it");
        ctx.Check(!run.PaneGraph.CompletedOf(TravelersObjective),
            $"ABLE-TO-FAIL CONTROL: the same objective over the host's panes alone completes nothing");
        report.AppendLine($"arrival: OBJECTIVE{TravelersObjective} completed whole-field {run.WholeGraph.CompletedOf(TravelersObjective)}, panes-only {run.PaneGraph.CompletedOf(TravelersObjective)}, with the guest's copy {copyOff:0.00} m from the point and P1 {p1Off:0} m out");
    }

    // A campaign is lost with the last human's aeroplane. On the host that is the guest's copy, and
    // the copy goes down only when the guest's own report crosses and plays its wreck there.
    private static void Wrecks(TestContext ctx, Run run, StringBuilder report)
    {
        ctx.Check(!run.WholeGraph.Ended && !run.PaneGraph.Ended,
            $"neither director has ended its mission before anybody is lost");
        run.HostP1.DebugForceCrash();
        run.Step(ScanWindow);
        var afterHost = (Panes: run.PaneGraph.Outcome, Whole: run.WholeGraph.Outcome);
        ctx.Check(run.PaneGraph.Outcome == MissionOutcome.Lost,
            $"CONTROL: over the host's panes alone the host's death loses the mission ({run.PaneGraph.Outcome})");
        ctx.Check(!run.WholeGraph.Ended && !run.WholeGraph.Ending && !run.HostCopy.Crashed,
            $"but with the guest in the field the mission flies on while the guest does ({run.WholeGraph.Outcome})");

        run.GuestOwn.DebugForceCrash();
        float crossed = run.StepUntil(() => run.HostCopy.Crashed, WreckWindow);
        ctx.Check(run.HostCopy.Crashed && run.Host.HumanField is { Count: 2 } field && field[1].Crashed,
            $"the guest's own death report crosses and the host's copy is a wreck in the host's field {crossed * 1000f:0} ms later");
        float ended = run.StepUntil(() => run.WholeGraph.Ended, WreckWindow);
        ctx.Check(run.WholeGraph.Outcome == MissionOutcome.Lost,
            $"and with the last human down the host's mission is lost {ended:0.00} s after the wreck ({run.WholeGraph.Outcome})");
        report.AppendLine($"wrecks: with the host down, panes-only {afterHost.Panes} and whole-field {afterHost.Whole}; the guest's report crossed (copy crashed {run.HostCopy.Crashed}) after {crossed * 1000f:0} ms and the whole-field director read {run.WholeGraph.Outcome} {ended:0.00} s after that");
    }

    // The two sessions and the two directors over them, stepped together as a listen server runs.
    // The host steps first, then the guest, then the directors reading the host's settled field.
    private sealed class Run
    {
        private readonly List<FlightController> _panes = new();

        public Run(GameSession host, GameSession guest, ObjectiveScript script,
            CampaignMission mission, string scriptZrdr)
        {
            Host = host;
            Guest = guest;
            HostP1 = host.SeatRigs[0].Controller!;
            HostCopy = host.SeatRigs[1].Controller!;
            GuestOwn = guest.SeatRigs[1].Controller!;
            Whole = Director(script, mission, scriptZrdr, () => Host.HumanField);
            Panes = Director(script, mission, scriptZrdr, PaneField);
        }

        public GameSession Host { get; }

        public GameSession Guest { get; }

        public FlightController HostP1 { get; }

        public FlightController HostCopy { get; }

        public FlightController GuestOwn { get; }

        public CampaignDirector Whole { get; }

        public CampaignDirector Panes { get; }

        public ObjectiveGraph WholeGraph => Whole.Graph!;

        public ObjectiveGraph PaneGraph => Panes.Graph!;

        public IReadOnlyList<FlightController> PaneField()
        {
            _panes.Clear();
            foreach (var rig in Host.Rigs)
            {
                if (rig.Controller is { } pilot)
                {
                    _panes.Add(pilot);
                }
            }

            return _panes;
        }

        public void Pin(FlightController pilot, Vector3 at)
        {
            pilot.Held = true;
            pilot.PlaceHeld(at, at + Vector3.Forward);
        }

        public void Step(float seconds)
        {
            for (float t = 0f; t < seconds; t += GameClock.FixedDt)
            {
                StepOnce();
            }
        }

        // Steps until the condition holds or the window runs out, answering the seconds it took.
        public float StepUntil(Func<bool> done, float window)
        {
            float t = 0f;
            while (!done() && t < window)
            {
                StepOnce();
                t += GameClock.FixedDt;
            }

            return t;
        }

        private void StepOnce()
        {
            Host._PhysicsProcess(GameClock.FixedDt);
            Guest._PhysicsProcess(GameClock.FixedDt);
            Whole.Step(GameClock.FixedDt);
            Panes.Step(GameClock.FixedDt);
        }

        private CampaignDirector Director(ObjectiveScript script, CampaignMission mission,
            string scriptZrdr, Func<IReadOnlyList<FlightController>> humans)
        {
            var director = CampaignDirector.Create(script, mission,
                CampaignProfileDef.NewProfile("Zachary"), null, scriptZrdr);
            director.Attach(new CampaignDirector.WorldInputs
            {
                ListenerPosition = () => HostP1.WorldPosition,
                PlayerAircraft = () => HostP1,
                Humans = humans,
                Rng = new Random(1),
            });
            return director;
        }
    }
}
