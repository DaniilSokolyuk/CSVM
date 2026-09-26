using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>CM09 (C1/M04) driven from mission start to the docking over its own BUILT world: the
/// authored chain runs on the real graph against the real roster, the Promised Land's broadside
/// deaths are what open it, and the three closing <c>DEDG</c> objectives nap the docking in once
/// groups 1, 2 and 5 read empty.</summary>
internal static class CampaignDockingSuites
{
    private const string Chapter = "C1";
    private const string Mission = "M04";
    private const float StepDt = 1f / 60f;

    // The step for the mission's authored waits (the intro, the taxi chain's naps, OBJECTIVE20's
    // 90 s). Nothing flown or shot is stepped here: the graph, the animation runtime and the
    // hull's net ride are timers and interpolation, so a quarter-second step lands each objective
    // within a quarter second of where a frame step does at a fifteenth of the cost.
    private const float WaitDt = 0.25f;

    // The mission's own intro, named by its NEW_GAME_START list, and the runtime state that says
    // it is still playing.
    private const string IntroAnim = "mission_intro_animation";
    private const int AnimRunning = 2;

    // The hull the whole mission is built around: the docking target, and the node three
    // objectives read through INACTIVE1.
    private const string PirateZep = "piratezep";

    // The radio tower: the objective target the briefing names, and the healthy model whose
    // switch-off OBJECTIVE15 reads.
    private const string TowerTarget = "ap_transmitter";
    private const string TowerHealthy = "rtwr_healthy";

    // The broadside doors whose deaths burn three gasbags: the same three the burnout suite kills,
    // which is what brings the hull down and, through the death def's self-invalidate, is what
    // OBJECTIVE23 reads.
    private static readonly string[] Doors = { "lbroad4", "lbroad3", "lbroad2" };

    private static readonly string[] Group1 = { "blakebloodhawk_8" };

    private static readonly string[] Group2 =
    {
        "blakebloodhawk_9", "blakebloodhawk_10", "blakebloodhawk_11",
        "blakebloodhawk_12", "blakebloodhawk_13",
    };

    private static readonly string[] Group5 =
    {
        "blakepeace_2_3", "blakepeace_2_4", "blakepeace_2_5", "blakepeace_2_6",
        "blakebloodhawk_1", "blakebloodhawk_2", "blakebloodhawk_3",
    };

    // What one drive of the mission does once the world, the roster and the graph are up.
    private delegate void Chain(TestContext ctx, CampaignDirector director, ObjectiveGraph graph,
        AnimRuntime runtime, ZeppelinRuntime zeps,
        IReadOnlyDictionary<string, FlightController> rigs, List<ObjectiveTransition> log,
        StringBuilder report);

    [Suite("campaign-cm09-docking",
        "CM09 (C1/M04) from mission start to the docking over its own built world: the intro "
        + "holds the objectives and its handoff switches the piratezep hull back on, which is "
        + "what every INACTIVE1 read of it and the TICK_DEPENDS_ON_OBJ 29 gate rest on; the "
        + "timed opening chain then reaches the radio tower, one Promised Land broadside death "
        + "completes OBJECTIVE23 and its nap wakes the group-2 squad with its team, group and "
        + "net intact, wiping that squad to two wakes OBJECTIVE29 and releases the nap "
        + "OBJECTIVE19 holds, OBJECTIVE20's WAKEUP_ENEMIES puts blakebloodhawk_1/2/3/8 into "
        + "play, and with the hull down and groups 1, 2 and 5 emptied OBJECTIVE42/43/44 "
        + "complete and nap in OBJECTIVE31, the docking, with pzhookpoint on the objective "
        + "target list and the INSTANTWIN behind it in play")]
    internal static void CampaignCm09Docking(TestContext ctx)
    {
        Fly(ctx, collision: false, RunChain, "test-campaign-cm09-docking.txt",
            $"{Chapter}/{Mission} runs its authored chain to the docking over its own world");
    }

    private static void Fly(TestContext ctx, bool collision, Chain chain, string artifact,
        FormattableString note)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var mission = CampaignSequence.Load(ctx.ZrdrPath)
                .Where(m => m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                    && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
                .Cast<CampaignMission?>().FirstOrDefault()
            ?? throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");

        var script = ObjectiveScript.Load(missionZrdr);
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var report = new StringBuilder();
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null, missionZrdr);

        ctx.WithWorld(Chapter, collision, Mission, world =>
            Drive(ctx, world, director, blocks, skills, missionZrdr, chapterZrdr, texturesPath,
                report, chain));

        ctx.WriteArtifact(artifact, report.ToString());
        ctx.Note(note);
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, AiSkills skills,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report,
        Chain chain)
    {
        var runtime = world.Runtime;
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? player = null;
        ZeppelinRuntime? zeps = null;
        uint maskWas = runtime.ContactMask;
        try
        {
            // A real session wires the mask from BuildsCollision and a suite world does not, so a
            // collision world left unwired answers every NODE_UNDERCOVER false and a wreck's whole
            // water-gated half goes missing (docs/verification.md, WORLD-28).
            if (world.Collision)
            {
                runtime.ContactMask = CollisionLayers.World;
                runtime.SurfaceIsWater = body => ProjectilePool.SurfaceIsWater(body as Node);
            }

            var live = new ProjectilePool(textures, null, null) { DamageSink = runtime.DamageAt };
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;

            director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                Player = () => human,
                NetTrailers = new NetTrailerTargets(
                    () => human.WorldPosition,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });

            var zepDefs = Zeppelins.Load(missionZrdr);
            zeps = new ZeppelinRuntime(zepDefs,
                name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null,
                AiNets.Load(chapterZrdr));
            zeps.WireDamage(runtime);

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = runtime,
                Sounds = runtime.Sounds,
                Projectiles = live,
                Zeppelins = zeps,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });

            var graph = director.Graph;
            ctx.Check(graph != null, $"the world phase armed the objective graph");
            if (graph == null)
            {
                return;
            }

            var log = new List<ObjectiveTransition>();
            graph.Transitioned += log.Add;
            var rigs = director.Roster;
            chain(ctx, director, graph, runtime, zeps, rigs, log, report);
        }
        finally
        {
            runtime.ContactMask = maskWas;
            runtime.SurfaceIsWater = null;
            zeps?.Dispose();
            player?.Free();
            var members = new List<FlightController>(
                roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            textures.Dispose();
        }
    }

    private static void RunChain(TestContext ctx, CampaignDirector director, ObjectiveGraph graph,
        AnimRuntime runtime, ZeppelinRuntime zeps,
        IReadOnlyDictionary<string, FlightController> rigs, List<ObjectiveTransition> log,
        StringBuilder report)
    {
        float StepUntil(Func<bool> done, float limit)
        {
            float t = 0f;
            for (; t < limit && !done(); t += WaitDt)
            {
                runtime.Advance(WaitDt);
                zeps.SimStep(WaitDt);
                director.Step(WaitDt);
            }
            return t;
        }

        // ⚠ Never drive this mission without the hold and the handoff. The intro's last shot
        // switches the hull off and only the handoff switches it back on, so a run without them
        // manufactures the stall (docs/formats/objectives.md, TICK_DEPENDS_ON_OBJ).
        HoldForIntro(ctx, runtime, zeps, report);

        // Killing the three sabotaged broadside doors is the player's part of the opening: the
        // death def's own INVALIDATE_ANIMATION is what OBJECTIVE23's ANIM_STATE reads, and the
        // same three deaths bring the Promised Land down for the primary.
        KillDoors(ctx, runtime, report);

        // The opening leg is timed rather than flown: OBJECTIVE2 wakes at 15 s and the taxi chain
        // runs itself down to OBJECTIVE14, the radio-tower call.
        StepUntil(() => graph.CompletedOf(14), 200f);
        report.AppendLine($"OBJECTIVE14 completed: 23={graph.StateOf(23)} 24={graph.StateOf(24)} "
            + $"28={graph.StateOf(28)} 30={graph.StateOf(30)} 15={graph.StateOf(15)}");
        ctx.Check(graph.CompletedOf(23), $"OBJECTIVE23 read a broadside death and woke the group-2 squad's chain");

        // The tower goes down inside OBJECTIVE16's 15 s distress window, which is the route the
        // report came from: 15 kills 16 and naps 17, and 17 naps 19 rather than 18.
        KillTower(ctx, runtime, report);
        StepUntil(() => graph.CompletedOf(15), 20f);
        ctx.Check(graph.CompletedOf(15) && !graph.CompletedOf(16),
            $"the tower is down inside the distress window: OBJECTIVE15 completed and killed OBJECTIVE16");

        // Group 2's squad is in play once OBJECTIVE24 has fired; wiping it to two completes 28,
        // which wakes 29 and releases the nap OBJECTIVE19 is holding.
        report.AppendLine($"group 2 live={director.GroupLiveCount(2)} inPlay="
            + $"{InPlay(rigs, Group2)} of {Group2.Length}");
        ctx.Same(Group2.Length, InPlay(rigs, Group2),
            $"OBJECTIVE24's WAKEUP_ENEMIES put the whole group-2 squad into play");
        ReportWoken(ctx, rigs, Group2, report);
        Crash(rigs, Group2.Take(3));
        StepUntil(() => graph.CompletedOf(20), 200f);
        report.AppendLine($"after the wake chain: 19={graph.StateOf(19)} 20={graph.StateOf(20)} "
            + $"40={graph.StateOf(40)} 42={graph.StateOf(42)} "
            + $"1 in play={InPlay(rigs, Group1)} 5 in play={InPlay(rigs, Group5)}");
        ctx.Check(graph.CompletedOf(19),
            $"OBJECTIVE19's held nap resumed once OBJECTIVE29 was awake, which is the tower-down route");
        ctx.Check(graph.CompletedOf(20),
            $"OBJECTIVE20 has fired, which is the wake that puts the Paladin Blake squad in play");
        ctx.Same(Group1.Length, InPlay(rigs, Group1),
            $"OBJECTIVE20's WAKEUP_ENEMIES put group 1 into play");
        ctx.Same(Group5.Length, InPlay(rigs, Group5),
            $"…and group 5's three parked members beside the four that fly from the start");

        // Everything left in the three groups the closing DEDG chain counts.
        Crash(rigs, Group1);
        Crash(rigs, Group2);
        Crash(rigs, Group5);
        float toDock = StepUntil(() => graph.CompletedOf(31), 30f);
        report.AppendLine($"live: 1={director.GroupLiveCount(1)} 2={director.GroupLiveCount(2)} "
            + $"5={director.GroupLiveCount(5)}; 42={graph.StateOf(42)} 43={graph.StateOf(43)} "
            + $"44={graph.StateOf(44)} 31={graph.StateOf(31)} after {toDock:0.0} s");
        ctx.Same(0, director.GroupLiveCount(1) ?? -1, $"DEDG reads group 1 empty");
        ctx.Same(0, director.GroupLiveCount(2) ?? -1, $"DEDG reads group 2 empty");
        ctx.Same(0, director.GroupLiveCount(5) ?? -1, $"DEDG reads group 5 empty");
        ctx.Check(graph.CompletedOf(44), $"OBJECTIVE44 completes on group 5 reading empty");
        ctx.Check(graph.CompletedOf(31), $"…and naps in the docking, OBJECTIVE31");
        ctx.Check(graph.ObjectiveTargets.Contains("pzhookpoint"),
            $"the docking's ADD_OBJECTIVE_TARGET put pzhookpoint on the target list");
        ctx.Check(graph.StateOf(32) != ObjectiveState.Dormant,
            $"…and OBJECTIVE32, the INSTANTWIN on hooked_to_klondike, is in play");

        DumpLog(log, report);
    }

    private static void DumpLog(List<ObjectiveTransition> log, StringBuilder report)
    {
        foreach (var t in log)
        {
            report.AppendLine($"t={t.Elapsed,7:0.00} objective {t.Number} {t.Kind}"
                + (t.Source != 0 ? $" by {t.Source}" : "")
                + (t.Seconds > 0f ? $" {t.Seconds:0.#} s" : "") + (t.Gated ? " gated" : ""));
        }
    }

    // The mission's own intro, run to its end with the objectives held, then handed off the way
    // CutsceneController does it. Nothing here plays the definition: the mission's NEW_GAME_START
    // list already did at the world build, and this is the hold and the handoff around it.
    private static void HoldForIntro(TestContext ctx, AnimRuntime runtime, ZeppelinRuntime zeps,
        StringBuilder report)
    {
        var pz = runtime.FindNodes(PirateZep).FirstOrDefault();
        float held = 0f;
        for (; held < 120f && runtime.AnimStateOf(IntroAnim) == AnimRunning; held += WaitDt)
        {
            runtime.Advance(WaitDt);
            zeps.SimStep(WaitDt);
        }
        report.AppendLine($"intro '{IntroAnim}' held the objectives for {held:0.0} s; "
            + $"piratezep visible={pz?.Visible} before the handoff");
        int ran = runtime.RunResetStateEvents(IntroAnim);
        report.AppendLine($"handoff: {ran} definition(s) ran their RESET_STATE; "
            + $"piratezep visible={pz?.Visible}");
        ctx.Check(pz is { Visible: true },
            $"the intro's handoff leaves '{PirateZep}' switched on, which every INACTIVE1 read of it depends on");
    }

    // The three sabotaged broadside doors, killed through the pool a rocket would spend: each
    // death runs its own def, which self-invalidates and burns its gasbag.
    private static void KillDoors(TestContext ctx, AnimRuntime runtime, StringBuilder report)
    {
        var hull = runtime.FindNodes("hk_zep").FirstOrDefault();
        ctx.Check(hull != null, $"the hk_zep world node resolves in the {Mission} world");
        if (hull == null)
        {
            return;
        }

        foreach (string door in Doors)
        {
            var node = runtime.FindNodes(door, hull).FirstOrDefault();
            var doorPool = node != null ? runtime.Destructibles.PoolsOn(node).FirstOrDefault() : null;
            ctx.Check(doorPool != null, $"{door} carries a destructible pool");
            if (node != null && doorPool != null)
            {
                runtime.DamageAt(node, doorPool.MaxHealth + 1f);
            }
        }
        report.AppendLine($"killed {Doors.Length} broadside door(s) on hk_zep");
    }

    // What a woken squad is left holding. A wake that put the aircraft in the world without the
    // pilot, the net or the team it was built with is a squad the player never meets, and a group
    // that never reads empty is the same stall as one that never woke.
    private static void ReportWoken(TestContext ctx,
        IReadOnlyDictionary<string, FlightController> rigs, IReadOnlyList<string> names,
        StringBuilder report)
    {
        int netted = 0;
        foreach (string name in names)
        {
            if (!rigs.TryGetValue(name, out var rig))
            {
                continue;
            }
            var patrol = rig.Pilot?.Patrol;
            netted += patrol != null ? 1 : 0;
            report.AppendLine($"woken {name}: at {rig.WorldPosition} team={rig.Team} "
                + $"group={rig.Group} net='{patrol?.Net.Name ?? "-"}'");
        }
        ctx.Same(names.Count, netted, $"each woken block still walks the patrol net it was built with");
    }

    // The radio tower, killed through its own pool: the death switches the healthy model off,
    // which is the node OBJECTIVE15 reads.
    private static void KillTower(TestContext ctx, AnimRuntime runtime, StringBuilder report)
    {
        var tower = runtime.FindNodes(TowerTarget).FirstOrDefault();
        var healthy = runtime.FindNodes(TowerHealthy).FirstOrDefault();
        report.AppendLine($"tower: '{TowerTarget}'={(tower != null ? "found" : "missing")} "
            + $"'{TowerHealthy}' visible={healthy?.Visible}");
        ctx.Check(tower != null && healthy != null,
            $"the radio tower's '{TowerTarget}' and '{TowerHealthy}' nodes resolve in the {Mission} world");
        if (tower == null)
        {
            return;
        }

        var pool = runtime.Destructibles.PoolsOn(tower).FirstOrDefault();
        ctx.Check(pool != null, $"'{TowerTarget}' carries a destructible pool");
        if (pool != null)
        {
            runtime.DamageAt(tower, pool.MaxHealth + 1f);
        }
    }

    private static void Crash(IReadOnlyDictionary<string, FlightController> rigs,
        IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (rigs.TryGetValue(name, out var rig))
            {
                rig.DebugForceCrash();
            }
        }
    }

    private static int InPlay(IReadOnlyDictionary<string, FlightController> rigs,
        IReadOnlyList<string> names)
    {
        int n = 0;
        foreach (string name in names)
        {
            n += rigs.TryGetValue(name, out var rig) && rig.InPlay ? 1 : 0;
        }
        return n;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        foreach (var (name, fields) in blocks)
        {
            if (name.Equals(CampaignRosterPlan.PlayerBlock, StringComparison.OrdinalIgnoreCase)
                && AiSkills.RosterSpawnPose(fields) is { } pose)
            {
                return (pose.Position, new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
            }
        }
        return (new Vector3(0f, 800f, 0f), Vector3.Forward);
    }

    // The human rig this suite flies nothing with: the leader pass and the 'player' role need one.
    private static FlightController HumanRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, Vector3 pos, Vector3 lookAt)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase - 1,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, lookAt);
        rig.Name = "player1";
        ctx.Host.AddChild(rig);
        return rig;
    }

    // The session's own AI spawner with no world effects, the shape CampaignSquadWakeSuites uses.
    private static FlightRoster Spawner(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live)
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
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            null!, ctx.Host, resources,
            new FlightWorldBindings { Projectiles = live, Gamez = planesGamez },
            new HumanRosterBindings());
    }
}
