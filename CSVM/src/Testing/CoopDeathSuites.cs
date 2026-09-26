using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>A co-op mission's loss rule: losing one aeroplane costs that human their
/// aircraft and nothing else, and the mission ends only when the last of them is down. Driven over
/// the first story mission of C3, the same world <c>campaign-player-death</c> uses and for the same
/// reason: C3/M01 authors no <c>INSTANTLOSS</c> and no <c>LOST</c> objective, so a Lost outcome
/// here can only be a human's own death. Four legs measure the guest down first, the SCRIPTED
/// PLAYER down first, <c>--no-crash-loss</c>, and the solo sortie's single-aircraft loss rule.</summary>
internal static class CoopDeathSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";

    private const float StepDt = 1f / 60f;

    // How long each leg flies on after a death before the next one. Well past the shortest wrap-up
    // either of the graph's own authored endings runs, so a mission still flying here is flying.
    private const float PhaseWindow = 4f;

    // Two humans, 2 km apart, the same spacing the sibling co-op suites fly: far enough that
    // neither death is the other's collision.
    private const float ApartM = 2000f;

    [Suite("campaign-coop-death",
        "the co-op loss rule over C3/M01, a mission whose script authors no loss at all: one "
        + "human down leaves the mission running and hands that pane a spectator camera with "
        + "the wreck pinned, while the other keeps flying; the LAST human's death is what ends "
        + "it lost once no wreck is still falling, whichever of them went first; "
        + "--no-crash-loss pins neither, and a solo death ends the mission on its only human")]
    internal static void CampaignCoopDeath(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }

        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission,
            world => Drive(ctx, world, script, mission, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-coop-death-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: a downed human spectates and the last one lost ends the mission");
    }

    private static void Drive(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var run = new Run(ctx, world, script, mission, planesGamez, textures, live, report);

            var guestFirst = run.Fly("guest-first", humans: 2, endsOnPlayerDeath: true, new[] { 1, 0 });
            ctx.Check(!guestFirst.EndedAfter(0),
                $"one human down leaves the mission running for the other: {guestFirst.Line}");
            ctx.Check(guestFirst.Spectating[1] && guestFirst.HandedTo(1),
                $"…and hands that human's pane a spectator camera, their wreck pinned: {guestFirst.Line}");
            ctx.Check(!guestFirst.Spectating[0] && !guestFirst.HandedTo(0),
                $"…while the human still flying keeps their aeroplane and their own camera: {guestFirst.Line}");
            ctx.Check(guestFirst.EndedAfter(1) && guestFirst.Outcome == MissionOutcome.Lost,
                $"the LAST human's death ends the mission lost: {guestFirst.Line}");
            ctx.Check(guestFirst.ReturnToCabin && !guestFirst.WreckFallingAtEnd,
                $"…handed back to the cabin, with no wreck of either of them still falling: {guestFirst.Line}");

            // ⚠ Able-to-fail control: the scripted player goes down first, so this leg fails if
            // the terminal rule reads P1 alone instead of the whole human field.
            var playerFirst = run.Fly("player-first", humans: 2, endsOnPlayerDeath: true, new[] { 0, 1 });
            ctx.Check(!playerFirst.EndedAfter(0),
                $"the SCRIPTED PLAYER's own death is one seat of the field, not the mission: {playerFirst.Line}");
            ctx.Check(playerFirst.Spectating[0] && playerFirst.HandedTo(0),
                $"…that human spectates while the guest flies the mission on: {playerFirst.Line}");
            ctx.Check(playerFirst.EndedAfter(1) && playerFirst.Outcome == MissionOutcome.Lost,
                $"…and the guest's death, last, is what ends it: {playerFirst.Line}");

            var flewOn = run.Fly("no-crash-loss", humans: 2, endsOnPlayerDeath: false, new[] { 1, 0 });
            ctx.Check(!flewOn.EndedAfter(0) && !flewOn.EndedAfter(1),
                $"--no-crash-loss leaves both crashes flying: {flewOn.Line}");
            ctx.Check(!flewOn.Spectating[0] && !flewOn.Spectating[1] && flewOn.Handed.Count == 0,
                $"…and pins neither wreck, so the debugged session can still fly them again: {flewOn.Line}");

            var solo = run.Fly("solo", humans: 1, endsOnPlayerDeath: true, new[] { 0 });
            ctx.Check(solo.EndedAfter(0) && solo.Outcome == MissionOutcome.Lost && solo.ReturnToCabin,
                $"a 1P campaign death ends the mission lost at once: {solo.Line}");
            ctx.Check(solo.Handed.Count == 0 && !solo.Spectating[0],
                $"…with no pane handed over, since nothing is left to watch: {solo.Line}");
        }
        finally
        {
            pool?.Free();
            textures.Dispose();
        }
    }

    /// <summary>What one leg answered: whether the mission had ended by the end of each death's
    /// window, what it ended as, and the state the humans were left in.</summary>
    internal readonly record struct Leg(
        string Label, IReadOnlyList<bool> Ended, MissionOutcome Outcome, bool ReturnToCabin,
        IReadOnlyList<bool> Spectating, IReadOnlyList<int> Handed, bool WreckFallingAtEnd,
        string Line)
    {
        /// <summary>Whether the mission had ended by the end of the window after death
        /// <paramref name="index"/>, counting from the first this leg drove.</summary>
        internal bool EndedAfter(int index) => index < Ended.Count && Ended[index];

        /// <summary>Whether seat <paramref name="seat"/>'s pane was handed to a spectator.</summary>
        internal bool HandedTo(int seat)
        {
            foreach (int taken in Handed)
            {
                if (taken == seat)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // One flown leg's world: everything a leg needs that outlives it, so a leg is the flight alone.
    private sealed class Run
    {
        private readonly TestContext _ctx;
        private readonly TestWorld _world;
        private readonly ObjectiveScript _script;
        private readonly CampaignMission _mission;
        private readonly GameZ _planesGamez;
        private readonly TextureArchive _textures;
        private readonly ProjectilePool _live;
        private readonly StringBuilder _report;

        internal Run(TestContext ctx, TestWorld world, ObjectiveScript script,
            CampaignMission mission, GameZ planesGamez, TextureArchive textures,
            ProjectilePool live, StringBuilder report)
        {
            _ctx = ctx;
            _world = world;
            _script = script;
            _mission = mission;
            _planesGamez = planesGamez;
            _textures = textures;
            _live = live;
            _report = report;
        }

        /// <summary>One leg: a fresh director and its own rigs over the shared world, the named
        /// seats downed in order with <see cref="PhaseWindow"/> flown between them, each death
        /// going through the production <c>Downed</c> path.</summary>
        internal Leg Fly(string label, int humans, bool endsOnPlayerDeath, IReadOnlyList<int> downOrder)
        {
            var profile = CampaignProfileDef.NewProfile("Zachary");
            var director = CampaignDirector.Create(_script, _mission, profile, null);
            director.EndsOnPlayerDeath = endsOnPlayerDeath;
            var rigs = new List<PlayerRig>();
            var cameras = new List<SpectatorCamera>();
            var handed = new List<int>();
            var field = new List<FlightController>();
            try
            {
                for (int i = 0; i < humans; i++)
                {
                    rigs.Add(BuildRig(i, new Vector3(i * ApartM, 800f, 0f)));
                    field.Add(rigs[i].Controller!);
                }

                director.Attach(new CampaignDirector.WorldInputs
                {
                    Runtime = _world.Runtime,
                    Sounds = _world.Runtime.Sounds,
                    Projectiles = _live,
                    ListenerPosition = () => field[0].WorldPosition,
                    PlayerAircraft = () => field[0],
                    Humans = () => field,
                    BeginSpectate = pilot => HandOver(pilot, rigs, cameras, handed),
                    Rng = new Random(1),
                });

                // One step first: the director subscribes to each seat's death on the first step
                // that finds its aircraft, because the rigs are built after Attach has run.
                director.Step(StepDt);
                var endedAfter = new List<bool>();
                foreach (int seat in downOrder)
                {
                    field[seat].DebugForceCrash();
                    bool ended = director.Result != null;
                    for (float t = 0f; t < PhaseWindow; t += StepDt)
                    {
                        director.Step(StepDt);
                        ended |= director.Result != null;
                    }

                    endedAfter.Add(ended);
                }

                return Record(label, director, field, endedAfter, handed);
            }
            finally
            {
                foreach (var camera in cameras)
                {
                    camera.Free();
                }

                foreach (var rig in rigs)
                {
                    rig.Controller!.Free();
                    rig.Camera.Free();
                }
            }
        }

        private static Func<IReadOnlyList<Node3D>> LiveOf(IReadOnlyList<PlayerRig> rigs)
            => () =>
            {
                var live = new List<Node3D>();
                foreach (var rig in rigs)
                {
                    if (rig.Controller is { InPlay: true } pilot)
                    {
                        live.Add(pilot);
                    }
                }

                return live;
            };

        // The hand-off under test is the production one, called exactly as GameSession calls it: a
        // suite that recorded the seat alone would pin the director's decision and nothing else.
        private void HandOver(FlightController pilot, IReadOnlyList<PlayerRig> rigs,
            List<SpectatorCamera> cameras, List<int> handed)
        {
            foreach (var rig in rigs)
            {
                if (ReferenceEquals(rig.Controller, pilot)
                    && SpectateHandoff.Begin(rig, rigs, _ctx.Host, LiveOf(rigs), cameras, out _))
                {
                    handed.Add(rig.Index);
                    return;
                }
            }
        }

        private Leg Record(string label, CampaignDirector director, IReadOnlyList<FlightController> field,
            IReadOnlyList<bool> endedAfter, IReadOnlyList<int> handed)
        {
            var spectating = new List<bool>();
            bool falling = false;
            foreach (var human in field)
            {
                spectating.Add(human.Spectating);
                falling |= human.WreckFalling;
            }

            var result = director.Result;
            var leg = new Leg(label, endedAfter, result?.Outcome ?? MissionOutcome.None,
                director.ReturnToCabin, spectating, handed, falling,
                $"{label}: {field.Count} human(s), ended after death "
                + $"[{string.Join(",", endedAfter)}] outcome={result?.Outcome.ToString() ?? "-"} "
                + $"cabin={director.ReturnToCabin} spectating=[{string.Join(",", spectating)}] "
                + $"panes handed=[{string.Join(",", handed)}] wreck falling={falling}");
            _report.AppendLine(leg.Line);
            return leg;
        }

        // A human rig with a camera of its own: the spectate hand-off writes that camera, so two
        // seats sharing one would hide a pane taken from the wrong human.
        private PlayerRig BuildRig(int index, Vector3 at)
        {
            var stats = PlaneStats.Load(_ctx.ZrdrPath, _ctx.PlaneName);
            var model = new PlaneBuilder(_planesGamez, _textures).Build(_ctx.PlaneName);
            var pilot = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                    ? PlaneDamage.For(stats) : null,
                PlayerIndex = FlightRoster.ShooterIdBase + index,
                IsHumanPiloted = true,
                Projectiles = _live,
                UseKeyboard = false,
                PadDevices = Array.Empty<int>(),
                AllowPause = false,
                Team = AimAssist.PlayerTeam,
                Name = $"CoopDeathPlayer{index}",
            };
            pilot.AddChild(model);
            _ctx.Host.AddChild(pilot);
            pilot.Setup(new FlightModel(stats), null, new CamParams(), at, at + Vector3.Forward);
            var camera = new Camera3D();
            _ctx.Host.AddChild(camera);
            return new PlayerRig
            {
                Index = index,
                Camera = camera,
                HudParent = _ctx.Host,
                Controller = pilot,
            };
        }
    }
}
