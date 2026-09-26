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

/// <summary>CM19's launch hook, the one mechanism in the shipped data that puts an aircraft into
/// the air off a definition rather than off a generator or a <c>WAKEUP_ENEMIES</c> list. C4/M04's
/// twelve Black Hat blocks ship deactivated and no objective names one: the objectives wake
/// <c>launch_warhawk</c>, the definition flies the hook and raises one <c>CALLBACK</c>, and that
/// code is what reactivates the next member of the family. Drives the mission's own objectives
/// over its BUILT world, and separately the staged display aeroplanes the hooks carry.
/// ⚠ <c>campaign-squad-wakeup</c> covers the <c>WAKEUP_ENEMIES</c> arm of roster dormancy; a launch
/// hook names no aircraft at all and neither suite stands in for the other.</summary>
internal static class CampaignLaunchHookSuites
{
    private const string Chapter = "C4";
    private const string Mission = "M04";

    // The first two objectives that wake the Warhawk hook, and the one that wakes the Brigand's.
    private const int FirstWarhawkObjective = 67;
    private const int SecondWarhawkObjective = 69;
    private const int BrigandObjective = 93;

    private const string WarhawkAnim = "launch_warhawk";
    private const string BrigandAnim = "launch_brigand";
    private const string WarhawkFamily = "bhatwarhawk";
    private const string BrigandFamily = "bhatbrigand";
    private const int WarhawkCallback = 801;
    private const int BrigandCallback = 802;
    private const int WarhawkCount = 6;

    // The display aeroplanes the place definitions park on the hooks, and the hook each belongs to.
    // Three nodes in C4's gamez are called `bmhookpoint`, so a prop on the wrong hook would read as
    // a pass on a bare name match.
    private const string LaunchHook = "warlaunchhook";
    private const string PlayerHook = "warhawk_bmhook";
    private const string HookPoint = "bmhookpoint";
    private const string LaunchProp = "anim2_warhawk";
    private const string PlayerProp = "anim_warhawk";

    // The hook's own sequence runs a motion script before its callback, so the launch is not
    // instantaneous; this is the ceiling the step loop below gives it, not a measured latency.
    private const float LaunchLimitS = 10f;
    private const float StepDt = 1f / 60f;

    // BL-730: the WAKE_ANIM fired and one definition started, and nothing tied that definition to
    // the dormant roster, so CM19's Black Hats never left the hook.
    [Suite("campaign-launch-hook",
        "CM19's Black Hat launch hook over C4/M04's own BUILT world: the six bhatwarhawk blocks "
        + "ship deactivated and no objective names one in WAKEUP_ENEMIES, OBJECTIVE67 wakes "
        + "launch_warhawk, that definition raises CALLBACK 801, waking the objective through the "
        + "real graph puts bhatwarhawk_1 into the air and leaves the other five on the hook, the "
        + "next wake launches bhatwarhawk_2, and the Brigand hook's own code launches off its own "
        + "family rather than the Warhawks'")]
    internal static void CampaignLaunchHook(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath))
            ?? throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        var script = ObjectiveScript.Load(missionZrdr);
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        CheckAuthored(ctx, script, blocks, report);

        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, blocks, missionZrdr, chapterZrdr, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-launch-hook-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: a Black Hat leaves the hook on its launch definition's own CALLBACK");
    }

    [Suite("campaign-launch-hook-prop",
        "CM19's staged launch-hook aeroplanes over C4/M04's own BUILT world: anim2_warhawk and "
        + "anim_warhawk ship unplaced and switched off, the mission's NEW_GAME_START place "
        + "definitions park each on its OWN hook's bmhookpoint (three nodes carry that name), the "
        + "launch hook and its Black Hat ride hidden until launch_warhawk switches both on, and the "
        + "Black Swan's own hook keeps its aeroplane visible from the first frame")]
    internal static void CampaignLaunchHookProp(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.WithWorld(Chapter, collision: false, Mission, world => DriveProps(ctx, world));
    }

    // The authored shape this suite consumes, read off the shipped files rather than restated: the
    // family ships deactivated, the objectives carry the WAKE_ANIM, and nothing names an aircraft.
    private static void CheckAuthored(TestContext ctx, ObjectiveScript script,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, StringBuilder report)
    {
        int deactivated = 0;
        for (int n = 1; n <= WarhawkCount; n++)
        {
            deactivated += Fields(blocks, $"{WarhawkFamily}_{n}") is { } f
                && AiSkills.RosterDeactivated(f) ? 1 : 0;
        }
        ctx.Same(WarhawkCount, deactivated,
            $"all {WarhawkCount} '{WarhawkFamily}_n' blocks ship deactivated");

        int named = 0, wakes = 0;
        foreach (var def in script.Objectives)
        {
            foreach (var name in def.WakeupEnemies)
            {
                named += name.StartsWith(WarhawkFamily, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
            wakes += def.WakeAnim is { Anim: WarhawkAnim } ? 1 : 0;
        }
        ctx.Same(0, named,
            $"no objective names a '{WarhawkFamily}' block in WAKEUP_ENEMIES, so the hook is the only way one flies");
        ctx.Check(wakes > 1, $"{wakes} objectives wake '{WarhawkAnim}', one per Black Hat the mission sends up");

        ctx.Check(ObjectiveNumbered(script, FirstWarhawkObjective) is
        { BeginDormant: true, WakeAnim: { Anim: WarhawkAnim } },
            $"OBJECTIVE{FirstWarhawkObjective} is dormant and its WAKE_ANIM is '{WarhawkAnim}'");
        ctx.Check(ObjectiveNumbered(script, SecondWarhawkObjective)?.WakeAnim is { Anim: WarhawkAnim },
            $"OBJECTIVE{SecondWarhawkObjective} wakes the same hook again");
        ctx.Check(ObjectiveNumbered(script, BrigandObjective)?.WakeAnim is { Anim: BrigandAnim },
            $"OBJECTIVE{BrigandObjective} wakes '{BrigandAnim}', the second family's own hook");
        report.AppendLine($"authored: {wakes} '{WarhawkAnim}' wakes, {deactivated} deactivated Warhawk blocks, {named} named in WAKEUP_ENEMIES");
    }

    private static void DriveProps(TestContext ctx, TestWorld world)
    {
        var runtime = world.Runtime;
        var launchHook = Only(runtime, LaunchHook);
        var playerHook = Only(runtime, PlayerHook);
        ctx.Check(launchHook != null && playerHook != null,
            $"C4/M04 builds both hooks, '{LaunchHook}' and '{PlayerHook}'");
        if (launchHook == null || playerHook == null)
        {
            return;
        }

        ctx.Same(3, runtime.FindNodes(HookPoint).Count,
            $"three nodes are called '{HookPoint}', so a prop's parent has to be the right one");

        var launchPoint = Only(runtime, HookPoint, launchHook);
        var playerPoint = Only(runtime, HookPoint, playerHook);
        var launchProp = Only(runtime, LaunchProp);
        var playerProp = Only(runtime, PlayerProp);
        ctx.Check(launchProp != null, $"the launch hook's display aeroplane '{LaunchProp}' is built");
        ctx.Check(playerProp != null, $"the Black Swan hook's display aeroplane '{PlayerProp}' is built");
        if (launchProp == null || playerProp == null || launchPoint == null || playerPoint == null)
        {
            return;
        }

        ctx.Check(launchPoint.IsAncestorOf(launchProp),
            $"'{LaunchProp}' hangs under the LAUNCH hook's own '{HookPoint}'");
        ctx.Check(playerPoint.IsAncestorOf(playerProp),
            $"'{PlayerProp}' hangs under the Black Swan hook's own '{HookPoint}'");

        ctx.Check(!Shown(launchProp), $"'{LaunchProp}' rides switched off before a launch");
        ctx.Check(!Shown(launchHook), $"…and so does the launch hook itself");
        ctx.Check(Shown(playerProp),
            $"'{PlayerProp}' is visible on the Black Swan's hook from the first frame");

        // The definition's own timeline is what reveals the pair, so drive it rather than assert on
        // the events: a revealed prop is the whole of what the empty hook was missing.
        runtime.Play(WarhawkAnim);
        bool propShown = false, hookShown = false;
        float shownAt = -1f;
        for (float t = 0f; t < LaunchLimitS && !propShown; t += StepDt)
        {
            runtime.Advance(StepDt);
            propShown = Shown(launchProp);
            hookShown |= Shown(launchHook);
            shownAt = propShown ? t : shownAt;
        }

        ctx.Check(propShown, $"'{WarhawkAnim}' puts '{LaunchProp}' on the hook, {shownAt:0.00} s in");
        ctx.Check(hookShown, $"…and switches the hook itself on for the ride");
        var seat = launchProp.GlobalTransform.Origin;
        ctx.Note($"{Chapter}/{Mission}: the launch hook carries a staged aeroplane rather than flying empty, seated at ({seat.X:0}, {seat.Y:0}, {seat.Z:0})");
    }

    // Visible in the world, not merely switched on: a prop under a hidden hook draws nothing, and
    // the parent's state is half of what the reparent decides.
    private static bool Shown(Node3D node)
    {
        for (Node3D? n = node; n != null; n = n.GetParent() as Node3D)
        {
            if (!n.Visible)
            {
                return false;
            }
        }
        return true;
    }

    private static Node3D? Only(AnimRuntime runtime, string name, Node3D? scope = null) =>
        runtime.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null;

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks,
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
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;

            director.BuildRoster(new CampaignDirector.RosterInputs
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

            var rigs = director.Roster;
            ctx.Same(0, InPlayCount(rigs, WarhawkFamily, WarhawkCount),
                $"every '{WarhawkFamily}' block spawns inert, on the hook");

            // The definition's own CALLBACK, read off the built mission rather than restated: it is
            // the whole of what ties this hook to the roster.
            ctx.Check(RaisesCallback(world.Runtime, WarhawkAnim, WarhawkCallback),
                $"'{WarhawkAnim}' raises CALLBACK {WarhawkCallback}");
            ctx.Check(RaisesCallback(world.Runtime, BrigandAnim, BrigandCallback),
                $"'{BrigandAnim}' raises CALLBACK {BrigandCallback}");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });
            director.BindCallbackHost(world.Runtime);

            var graph = director.Graph;
            ctx.Check(graph != null, $"the world phase armed the objective graph");
            if (graph == null)
            {
                return;
            }

            graph.Wake(FirstWarhawkObjective);
            float first = StepUntilLaunched(director, world, rigs, WarhawkFamily, 1);
            report.AppendLine($"OBJECTIVE{FirstWarhawkObjective}: '{WarhawkFamily}_1' in play {first:0.00} s after the wake");
            ctx.Check(first >= 0f,
                $"OBJECTIVE{FirstWarhawkObjective}'s WAKE_ANIM launches '{WarhawkFamily}_1' through the real graph, {first:0.00} s in");
            ctx.Same(1, InPlayCount(rigs, WarhawkFamily, WarhawkCount),
                $"…and exactly one Black Hat leaves the hook, not the whole family");

            graph.Wake(SecondWarhawkObjective);
            float second = StepUntilLaunched(director, world, rigs, WarhawkFamily, 2);
            report.AppendLine($"OBJECTIVE{SecondWarhawkObjective}: '{WarhawkFamily}_2' in play {second:0.00} s after the wake");
            ctx.Check(second >= 0f,
                $"the next wake takes the next member of the family, '{WarhawkFamily}_2'");
            ctx.Same(2, InPlayCount(rigs, WarhawkFamily, WarhawkCount),
                $"…leaving four Warhawks on the hook");

            graph.Wake(BrigandObjective);
            float brigand = StepUntilLaunched(director, world, rigs, BrigandFamily, 1);
            report.AppendLine($"OBJECTIVE{BrigandObjective}: '{BrigandFamily}_1' in play {brigand:0.00} s after the wake");
            ctx.Check(brigand >= 0f,
                $"the Brigand hook's own code launches '{BrigandFamily}_1'");
            ctx.Same(2, InPlayCount(rigs, WarhawkFamily, WarhawkCount),
                $"…off its own family, with the Warhawk count unmoved");
        }
        finally
        {
            player?.Free();
            var members = new List<FlightController>(
                roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var r in members)
            {
                r.Free();
            }
            pool?.Free();
            textures.Dispose();
        }
    }

    // Seconds until that family member is in play, or -1 if it never is. Steps the director and the
    // anim runtime together, since the launch is a definition's own timeline reaching its callback.
    private static float StepUntilLaunched(CampaignDirector director, TestWorld world,
        IReadOnlyDictionary<string, FlightController> rigs, string family, int ordinal)
    {
        if (!rigs.TryGetValue($"{family}_{ordinal}", out var rig))
        {
            return -1f;
        }

        float elapsed = 0f;
        while (elapsed < LaunchLimitS)
        {
            if (rig.InPlay)
            {
                return elapsed;
            }
            director.Step(StepDt);
            world.Runtime.Advance(StepDt);
            elapsed += StepDt;
        }

        return rig.InPlay ? elapsed : -1f;
    }

    private static bool RaisesCallback(AnimRuntime runtime, string animName, int code)
    {
        foreach (var def in runtime.DefsFor(animName))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "Callback" && (int)(ev.Data.Num("value") ?? -1f) == code)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static int InPlayCount(IReadOnlyDictionary<string, FlightController> rigs,
        string family, int count)
    {
        int n = 0;
        for (int i = 1; i <= count; i++)
        {
            n += rigs.TryGetValue($"{family}_{i}", out var rig) && rig.InPlay ? 1 : 0;
        }
        return n;
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> sequence)
    {
        foreach (var m in sequence)
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        return null;
    }

    private static ObjectiveDef? ObjectiveNumbered(ObjectiveScript script, int number) =>
        number >= 1 && number <= script.Objectives.Count ? script.Objectives[number - 1] : null;

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
    // the roster's authored 'player' role resolves to something.
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

    // The session's own AI spawner with no world effects, the shape the other campaign suites use.
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
