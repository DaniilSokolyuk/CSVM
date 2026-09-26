using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-565: an awake <c>DEDG</c> widens the engagement volume of every live member of the
/// group it watches (docs/formats/objectives.md's DEDG row, <c>FUN_00465850</c>), which is what
/// keeps a watched wave from disengaging by distance. C3/M05 is the worked case: OBJECTIVE5
/// (<c>DEDG [1, 0]</c>) and OBJECTIVE22 (<c>DEDG [5, 0]</c>) are awake from the first tick over
/// groups 1 and 5, while the group-2 and group-4 clauses ship dormant, so one mission carries both
/// the widened set and its control. Driven over that mission's own roster, world and graph.</summary>
internal static class CampaignDedgVolumeSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M05";

    private const float StepDt = 1f / 60f;

    // The two clauses that are awake at mission start, and the group each watches.
    private const int AwakeDedgObjective = 5;
    private const int AwakeGroup = 1;
    private const int BomberDedgObjective = 22;
    private const int BomberGroup = 5;

    // A clause over group 2 that ships dormant: its members must stay at the floor.
    private const int DormantDedgObjective = 11;
    private const int DormantGroup = 2;

    // The member whose radius is raised past the widening before the first tick, so the direction
    // of the write is measured rather than assumed.
    private const string AlreadyWiderBlock = "britpeace_2";
    private const float AlreadyWiderM = 12000f;

    // The disengage geometry, in metres: the chase is entered this far from the member's spawn,
    // well inside the attack radius, then the AI strays this far from the anchor the promotion took
    // with the target this far off, which is outside the 2,000 m floor and inside the widened one.
    private const float FarFromSpawnM = 6000f;
    private const float EntryRangeM = 1000f;
    private const float InsideStrayM = 900f;
    private const float StrayM = 500f;
    private const float DisengageRangeM = 3000f;

    [Suite("campaign-dedg-volume",
        "BL-565 over C3/M05's own roster, world and graph: an awake DEDG raises every live member "
        + "of the group it watches to a 9000 m activation radius on its first tick, groups 1 and 5 "
        + "(OBJECTIVE5 and OBJECTIVE22) being the mission's two awake clauses; a member already "
        + "wider keeps its own radius, the deactivated group-2/4 blocks behind dormant clauses and "
        + "the group-0 aircraft nothing watches all stay at the min_ai_active_dist floor; and a "
        + "watched member that caught its quarry 6 km from its spawn holds the chase on the return "
        + "cylinder about that anchor, reverting when it leaves the cylinder and not before")]
    internal static void CampaignDedgVolume(TestContext ctx)
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

        var script = ObjectiveScript.Load(missionZrdr);
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        CheckAuthored(ctx, script, blocks, report);

        var skills = AiSkills.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, blocks, skills, missionZrdr, chapterZrdr, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-dedg-volume-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: its two awake DEDG clauses widen groups {AwakeGroup} and {BomberGroup} and nothing else");
    }

    // The authored shape this suite rests on, read off the shipped files: which clauses are awake
    // at mission start, which group each watches, and which blocks sit in those groups.
    private static void CheckAuthored(TestContext ctx, ObjectiveScript script,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, StringBuilder report)
    {
        var awake = script.ByNumber(AwakeDedgObjective);
        var bomber = script.ByNumber(BomberDedgObjective);
        var dormant = script.ByNumber(DormantDedgObjective);
        ctx.Check(awake?.Dedg is { Group: AwakeGroup, Max: 0 } && !awake.BeginDormant,
            $"OBJECTIVE{AwakeDedgObjective} watches group {AwakeGroup} and is awake at mission start");
        ctx.Check(bomber?.Dedg is { Group: BomberGroup, Max: 0 } && !bomber.BeginDormant,
            $"OBJECTIVE{BomberDedgObjective} watches group {BomberGroup} and is awake at mission start");
        ctx.Check(dormant?.Dedg is { Group: DormantGroup } && dormant.BeginDormant,
            $"OBJECTIVE{DormantDedgObjective}, the group-{DormantGroup} clause, ships dormant");

        int awakeClauses = 0;
        var watched = new HashSet<int>();
        foreach (var def in script.Objectives)
        {
            if (def.Dedg is not { } dedg)
            {
                continue;
            }
            report.AppendLine($"authored: OBJECTIVE{def.Number} DEDG [{dedg.Group}, {dedg.Max}] "
                + $"dormant={def.BeginDormant} gate={def.TickDependsOn}");
            if (!def.BeginDormant && def.TickDependsOn <= 0)
            {
                awakeClauses++;
                watched.Add(dedg.Group);
            }
        }
        ctx.Check(watched.Contains(AwakeGroup) && watched.Contains(BomberGroup) && watched.Count == 2,
            $"exactly groups {AwakeGroup} and {BomberGroup} are watched from the first tick: [{string.Join(", ", watched)}]");
        report.AppendLine($"authored: {awakeClauses} DEDG clause(s) awake at start over {watched.Count} group(s)");

        foreach (var (name, fields) in blocks)
        {
            report.AppendLine($"authored: {name} group={AiSkills.RosterGroup(fields)} "
                + $"deactivated={AiSkills.RosterDeactivated(fields)}");
        }
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, AiSkills skills,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
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
            roster = CampaignRosterSuites.Spawner(ctx, planesGamez, textures, live);

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
                    name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });

            var rigs = director.Roster;
            var groups = Groups(blocks);
            float floor = skills.MinAiActiveDist;

            // The baseline: nothing has ticked, so every member sits on the volume its block, its
            // net and the min_ai_active_dist floor gave it, which is what a wave survivor keeps
            // while the objective waits on it.
            var before = Ranges(rigs);
            foreach (var (name, range) in before)
            {
                report.AppendLine($"before: {name} group={Group(groups, name)} activation={range:0} m");
            }
            ctx.Check(before.Count > 0, $"the mission's roster spawned aircraft to measure: {before.Count}");
            int atFloor = 0;
            foreach (var (_, range) in before)
            {
                atFloor += Mathf.IsEqualApprox(range, floor) ? 1 : 0;
            }
            ctx.Same(before.Count, atFloor,
                $"every spawned member starts on the {floor:0} m min_ai_active_dist floor");

            // Raised past the widening before the first tick: the write must not pull it back down.
            if (rigs.TryGetValue(AlreadyWiderBlock, out var wider) && wider.Pilot?.Machine is { } widerMachine)
            {
                widerMachine.ActivationRange = AlreadyWiderM;
            }

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
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

            director.Step(StepDt);
            ctx.Check(graph.StateOf(AwakeDedgObjective) == ObjectiveState.Awake,
                $"OBJECTIVE{AwakeDedgObjective} is awake on the tick that was driven");
            ctx.Check(graph.StateOf(DormantDedgObjective) == ObjectiveState.Dormant,
                $"…while OBJECTIVE{DormantDedgObjective}, the group-{DormantGroup} clause, is still dormant");

            var after = Ranges(rigs);
            int widened = 0, held = 0, kept = 0;
            foreach (var (name, range) in after)
            {
                int group = Group(groups, name);
                bool watchedGroup = group == AwakeGroup || group == BomberGroup;
                bool deactivated = rigs.TryGetValue(name, out var rig) && rig.Deactivated;
                report.AppendLine($"after: {name} group={group} watched={watchedGroup} "
                    + $"deactivated={deactivated} activation={range:0} m");
                if (name.Equals(AlreadyWiderBlock, StringComparison.OrdinalIgnoreCase))
                {
                    kept += Mathf.IsEqualApprox(range, AlreadyWiderM) ? 1 : 0;
                }
                else if (watchedGroup && !deactivated)
                {
                    widened += Mathf.IsEqualApprox(range, CampaignRosterPlan.DedgActivationRangeM) ? 1 : 0;
                }
                else
                {
                    held += Mathf.IsEqualApprox(range, floor) ? 1 : 0;
                }
            }
            int expectWidened = 0, expectHeld = 0;
            foreach (var (name, _) in after)
            {
                if (name.Equals(AlreadyWiderBlock, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int group = Group(groups, name);
                bool watchedGroup = group == AwakeGroup || group == BomberGroup;
                bool deactivated = rigs.TryGetValue(name, out var rig) && rig.Deactivated;
                if (watchedGroup && !deactivated)
                {
                    expectWidened++;
                }
                else
                {
                    expectHeld++;
                }
            }
            ctx.Check(expectWidened > 0 && expectHeld > 0,
                $"the mission carries both arms to judge: {expectWidened} watched live, {expectHeld} not");
            ctx.Same(expectWidened, widened,
                $"one tick raises every live member of a watched group to {CampaignRosterPlan.DedgActivationRangeM:0} m");
            ctx.Same(expectHeld, held,
                $"…and leaves every other member on the {floor:0} m floor");
            ctx.Same(1, kept,
                $"…and leaves '{AlreadyWiderBlock}', already at {AlreadyWiderM:0} m, where it was");

            CheckDisengage(ctx, rigs, floor, report);
        }
        finally
        {
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

    // What holds a watched member on its quarry: the return cylinder about the anchor the promotion
    // took, so a member that caught it 6 km from its spawn keeps chasing there and is recalled only
    // where the original recalls it. The activation radius the DEDG widens reaches none of that.
    private static void CheckDisengage(TestContext ctx,
        IReadOnlyDictionary<string, FlightController> rigs, float floor, StringBuilder report)
    {
        if (!rigs.TryGetValue("britpeace_1", out var member) || member.Pilot?.Machine is not { } widened)
        {
            return;
        }

        var control = new AiModeMachine(new Random(1))
        {
            ActivationRange = floor,
            AttackRange = widened.AttackRange,
            ReturnRange = widened.ReturnRange,
            AssistEnabled = false,
        };
        widened.AssistEnabled = false;

        var spawn = member.WorldPosition;
        var chaseStart = spawn + (Vector3.Right * FarFromSpawnM);
        AiMode Chase(AiModeMachine machine, float stray)
        {
            machine.Enter(AiMode.Patrol, "suite: reset");
            machine.Update(chaseStart, Vector3.Forward, chaseStart + (Vector3.Forward * EntryRangeM),
                null, StepDt);
            var strayed = chaseStart + (Vector3.Right * stray);
            machine.Update(strayed, Vector3.Forward, strayed + (Vector3.Forward * DisengageRangeM),
                null, StepDt);
            return machine.Mode;
        }

        float outsideM = widened.ReturnRange + StrayM;
        var held = Chase(widened, InsideStrayM);
        var anchor = widened.PursuitAnchor;
        var heldFloor = Chase(control, InsideStrayM);
        var leftWide = Chase(widened, outsideM);
        var leftFloor = Chase(control, outsideM);
        report.AppendLine($"disengage: chase entered {FarFromSpawnM:0} m from the spawn, target "
            + $"{DisengageRangeM:0} m off (return {widened.ReturnRange:0} m): {InsideStrayM:0} m from the "
            + $"anchor -> widened {AiModeMachine.NameOf(held)} / floor {AiModeMachine.NameOf(heldFloor)}; "
            + $"{outsideM:0} m -> widened {AiModeMachine.NameOf(leftWide)} / floor {AiModeMachine.NameOf(leftFloor)}");
        ctx.Check(anchor is { } a && a.IsEqualApprox(chaseStart),
            $"the anchor is where the chase began, {FarFromSpawnM:0} m from the spawn, not the spawn");
        ctx.Check(held == AiMode.Pursue && heldFloor == AiMode.Pursue,
            $"…and inside the cylinder the member holds its quarry {DisengageRangeM:0} m off, widened or floored: {AiModeMachine.NameOf(held)}/{AiModeMachine.NameOf(heldFloor)}");
        ctx.Check(leftWide == AiMode.Patrol && leftFloor == AiMode.Patrol,
            $"…and leaving it recalls both, {FarFromSpawnM + outsideM:0} m from the spawn and still inside the widened {widened.ActivationRange:0} m: {AiModeMachine.NameOf(leftWide)}/{AiModeMachine.NameOf(leftFloor)}");
        ctx.Note($"the pursue ENTRY gate is still the {widened.AttackRange:0} m attack radius, which no DEDG widens");
    }

    private static List<(string Name, float Range)> Ranges(
        IReadOnlyDictionary<string, FlightController> rigs)
    {
        var ranges = new List<(string, float)>();
        foreach (var (name, rig) in rigs)
        {
            if (rig.Pilot?.Machine is { } machine)
            {
                ranges.Add((name, machine.ActivationRange));
            }
        }
        return ranges;
    }

    private static Dictionary<string, int> Groups(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, fields) in blocks)
        {
            groups[name] = AiSkills.RosterGroup(fields);
        }
        return groups;
    }

    private static int Group(Dictionary<string, int> groups, string name) =>
        groups.TryGetValue(name, out int group) ? group : -1;

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

    // The human rig the roster build needs a player for; nothing flies it here.
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
}
