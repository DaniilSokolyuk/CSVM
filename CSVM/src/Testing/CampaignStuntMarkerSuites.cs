using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>CM11's two stunt planes, <c>secfury_5</c>/<c>secfury_6</c>, author the roster's own
/// <c>objectiveTarget</c> flag (aiv slot 37) and <c>MSG_OBJ_FOLLOW</c> label (slot 39) rather than
/// a <c>targets.zrd</c> entry. Drives C2/M02's real roster and objective graph and checks both
/// aircraft reach the pilot's Enemy cycle ONCE, labelled Follow and named by their own slot 20,
/// while OBJECTIVE1, the primary that starts the follow and is awake from mission start, is still
/// open.</summary>
internal static class CampaignStuntMarkerSuites
{
    private const string Chapter = "C2";
    private const string Mission = "M02";
    private const string PlaneA = "secfury_5";
    private const string PlaneB = "secfury_6";
    private const string FollowLabel = "Follow";
    private const string StuntPlaneName = "Stunt Plane";

    // OBJECTIVE1, the shipped PRIMARY awake from mission start that starts the follow.
    private const int FollowObjective = 1;

    [Suite("campaign-cm11-stunt-marker",
        "CM11's two stunt planes over C2/M02's own BUILT roster and graph: secfury_5/secfury_6 "
        + "author the roster's own objectiveTarget flag and MSG_OBJ_FOLLOW label rather than a "
        + "targets.zrd entry, and both reach the pilot's Enemy cycle labelled Follow while "
        + "OBJECTIVE1, awake from mission start, is still open")]
    internal static void CampaignCm11StuntMarker(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
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

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        CheckAuthored(ctx, blocks, report);

        var script = ObjectiveScript.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, chapterZrdr);
        var messages = Messages.Load(ctx.MessagesPath);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, blocks, missionZrdr, chapterZrdr, texturesPath, targets, messages, report));

        ctx.WriteArtifact($"test-campaign-cm11-stunt-marker-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: secfury_5/secfury_6 carry the roster's own Follow marker while OBJECTIVE{FollowObjective} is open");
    }

    // The shipped roster: both blocks author slot 37 = 1 and slot 39 = MSG_OBJ_FOLLOW, the census
    // this item's plan asked for.
    private static void CheckAuthored(TestContext ctx,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, StringBuilder report)
    {
        foreach (var name in new[] { PlaneA, PlaneB })
        {
            if (Fields(blocks, name) is not { } fields)
            {
                ctx.Check(false, $"{Chapter}/{Mission} authors a '{name}' roster block");
                continue;
            }

            bool flag = AiSkills.RosterObjectiveTarget(fields);
            string? label = AiSkills.RosterHelpLabel(fields);
            report.AppendLine($"'{name}': objectiveTarget={flag} helpLabel='{label}'");
            ctx.Check(flag, $"'{name}' authors the roster's own objectiveTarget flag (slot 37)");
            ctx.Check(label == "MSG_OBJ_FOLLOW", $"'{name}' authors the MSG_OBJ_FOLLOW help label (slot 39)");
        }
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks,
        string missionZrdr, string chapterZrdr, string texturesPath,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? player = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;

            string what = director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = AiSkills.Load(ctx.ZrdrPath).MinAiActiveDist,
                Player = () => human,
                NetTrailers = new NetTrailerTargets(
                    () => human.WorldPosition,
                    name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });
            report.AppendLine($"build summary suffix: '{what}'");

            var rigs = director.Roster;
            ctx.Check(rigs.ContainsKey(PlaneA) && rigs.ContainsKey(PlaneB),
                $"'{PlaneA}' and '{PlaneB}' both spawn into the roster");
            report.AppendLine($"RosterObjectiveMarkers: [{string.Join(", ", director.RosterObjectiveMarkers.Keys)}]");
            ctx.Check(director.RosterObjectiveMarkers.TryGetValue(PlaneA, out var labelA) && labelA == "MSG_OBJ_FOLLOW",
                $"'{PlaneA}' is booked into RosterObjectiveMarkers under 'MSG_OBJ_FOLLOW'");
            ctx.Check(director.RosterObjectiveMarkers.TryGetValue(PlaneB, out var labelB) && labelB == "MSG_OBJ_FOLLOW",
                $"'{PlaneB}' is booked into RosterObjectiveMarkers under 'MSG_OBJ_FOLLOW'");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Gamez = world.Gamez,
                Strings = messages,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });
            var graph = director.Graph!;
            graph.Step(0.1f);
            ctx.Check(graph.StateOf(FollowObjective) == ObjectiveState.Awake,
                $"OBJECTIVE{FollowObjective}, the objective that starts the follow, is awake from mission start");

            var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
            CheckOffered(ctx, sites, rigs, report, "while the follow is open");
        }
        finally
        {
            player?.Free();
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            foreach (var m in members)
            {
                m.QueueFree();
            }
            pool?.QueueFree();
        }
    }

    // ⚠ OBJECTIVE1's own REMOVE_OBJECTIVE_TARGET (gate2/spy_switch inactive) is not driven here:
    // it needs the studio approach built for real, which is out of this suite's reach.
    private static void CheckOffered(TestContext ctx, ObjectiveSites sites,
        IReadOnlyDictionary<string, FlightController> rigs, StringBuilder report, string when)
    {
        var candidates = new List<AimCandidate>();
        sites.Collect(candidates);
        // The marker rides each aeroplane's OWN vehicle candidate now, so the scan has to hold the
        // spawned rigs: a site collector that offered one beside them is what this item removed.
        var scan = new AimCandidateSet();
        foreach (var rig in rigs.Values)
        {
            scan.AddVehicle(rig.WorldPosition, rig.WorldVelocity, rig.Team, rig.InPlay, rig);
        }

        var selection = new TargetSelection();
        selection.Rebuild(scan, null, AimAssist.PlayerTeam, null, Vector3.Zero, Basis.Identity, candidates);

        foreach (var name in new[] { PlaneA, PlaneB })
        {
            var target = Find(selection.Pool.Enemy, name);
            report.AppendLine(target is { } t
                ? $"{when}: '{name}' offered objective={t.Objective} category=\"{t.CategoryLine}\" name='{t.DisplayName}' at {t.Position}"
                : $"{when}: '{name}' NOT offered");
            ctx.Check(target is { Objective: true }, $"'{name}' reaches the Enemy cycle as an objective {when}");
            ctx.Check(string.Equals(target?.Category, FollowLabel, StringComparison.Ordinal),
                $"'{name}'s marker carries its own roster block's Follow label {when}");
            ctx.Check(Count(selection.Pool, name) == 1,
                $"'{name}' is offered exactly once across all three cycles {when}");
            ctx.Check(string.Equals(target?.DisplayName, StuntPlaneName, StringComparison.Ordinal),
                $"'{name}'s marker prints its block's own slot-20 name '{StuntPlaneName}'");
        }
    }

    private static int Count(TargetPool pool, string name)
    {
        int n = 0;
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            foreach (var target in pool.Of(cls))
            {
                if (string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }
        }
        return n;
    }

    private static TargetRef? Find(IReadOnlyList<TargetRef> cycle, string name)
    {
        foreach (var target in cycle)
        {
            if (string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        return null;
    }

    private static List<object?>? Fields(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, string name)
    {
        foreach (var (block, fields) in blocks)
        {
            if (block.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return fields;
            }
        }
        return null;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        if (Fields(blocks, CampaignRosterPlan.PlayerBlock) is { } fields
            && AiSkills.RosterSpawnPose(fields) is { } pose)
        {
            return (pose.Position, new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
        }
        return (new Vector3(0f, 800f, 0f), Vector3.Forward);
    }

    // The human rig this suite flies nothing with: it exists so the leader pass has a player and
    // the ranking has a human to resolve the authored 'player' role against.
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
