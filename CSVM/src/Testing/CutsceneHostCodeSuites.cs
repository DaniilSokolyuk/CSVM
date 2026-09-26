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
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The two mission-script host codes that belong to one shipped definition each, driven
/// at the position that authors them. C1/M02's hangar drop raises 86, which the original answers by
/// despawning every round still in flight; C4/M03's docking film raises 968, which takes the
/// escorting wingman out of the world one event before her own "down" line. Decode:
/// docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class CutsceneHostCodeSuites
{
    private const string DropChapter = "C1";
    private const string DropMission = "M02";
    private const string DropAnim = "hangar_drop";
    private const int ClearOrdnanceCode = 86;
    private const int SwapCode = 965;

    private const string HookChapter = "C4";
    private const string HookMission = "M03";
    private const string HookAnim = "cg_hookup_player";
    private const int RemoveWingmanCode = 968;
    private const string Wingman = "bswingman_1";

    // A cannon round, so the shot needs no target and no lock to stay in the air for the step below.
    private const string TestWeapon = "wep_00";
    private const float StepDt = 1f / 60f;

    // How many rounds the suite puts in the air before the code is raised. More than one, so a clear
    // that reached only the newest slot would still leave something to find.
    private const int RoundsFired = 4;

    [Suite("cutscene-clear-ordnance",
        "CALLBACK 86 over C1/M02's own world: the hangar drop is the one definition in the install "
        + "that raises it, and it is the same definition that swaps the airframe, so the code "
        + "clears the air of what the outgoing aeroplane fired; rounds in flight are gone the "
        + "instant the hosted definition raises it, with no detonation, and the same code from a "
        + "definition this host does not answer for is declined and leaves them flying")]
    internal static void CutsceneClearOrdnance(TestContext ctx)
    {
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, DropChapter);
        ctx.RequireData(texturesPath, $"{DropChapter} textures");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        if (!weapons.TryGet(TestWeapon, out var gun))
        {
            throw new SuiteSkippedException($"{TestWeapon} is not in this install's weapons");
        }

        ctx.WithWorld(DropChapter, collision: false, DropMission,
            world => DriveClear(ctx, world, texturesPath, gun));
    }

    [Suite("campaign-wingman-removal",
        "CALLBACK 968 over C4/M03's BUILT world and its own aiv roster: the docking film is the "
        + "one definition in the install that raises it, one event ahead of the wingman's own "
        + "down line, bswingman_1 spawns in play from the mission's roster, the code raised "
        + "through the director's place in the host chain takes her out of the world as a "
        + "deactivate rather than a cutscene park, and raising it again on an aircraft already "
        + "out of play changes nothing")]
    internal static void CampaignWingmanRemoval(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, HookChapter, HookMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, HookChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, HookChapter);
        ctx.RequireData(missionZrdr, $"{HookChapter}/{HookMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{HookChapter} zrdr");
        ctx.RequireData(texturesPath, $"{HookChapter} textures");

        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), HookChapter, HookMission)
            ?? throw new SuiteSkippedException($"cm_sequence carries no {HookChapter}/{HookMission}");
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        var report = new StringBuilder();
        ctx.WithWorld(HookChapter, collision: false, HookMission,
            world => DriveRemoval(ctx, world, director, missionZrdr, chapterZrdr, texturesPath, report));
        ctx.WriteArtifact($"test-wingman-removal-{HookChapter}-{HookMission}.txt", report.ToString());
        ctx.Note($"{HookChapter}/{HookMission}: the docking film's own code takes '{Wingman}' out of the world");
    }

    private static void DriveClear(TestContext ctx, TestWorld world, string texturesPath, WeaponDef gun)
    {
        // The authored shape first: the code belongs to this one definition, beside the swap.
        var raisers = RaisersOf(world.Runtime, ClearOrdnanceCode);
        ctx.Check(raisers.Contains(DropAnim),
            $"'{DropAnim}' raises CALLBACK {ClearOrdnanceCode} raisers=[{string.Join(",", raisers)}]");
        ctx.Same(1, raisers.Count,
            $"and it is the only definition of this mission that does");
        ctx.Check(RaisersOf(world.Runtime, SwapCode).Contains(DropAnim),
            $"the same definition raises the airframe swap, CALLBACK {SwapCode}");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        CutsceneController? cutscene = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            cutscene = new CutsceneController();
            ctx.Host.AddChild(cutscene);
            cutscene.HostDefinitions(new[] { DropAnim });
            cutscene.ClearOrdnance = () => live.Clear();

            // ⚠ The control before the act: an unhosted definition raising the same code must leave
            // the air alone, or the assertion below would pass on a host that clears unconditionally.
            int flying = FireAndCount(live, gun);
            ctx.Check(flying >= RoundsFired,
                $"{flying} rounds are in the air before the code is raised");
            ctx.Check(!cutscene.Host(ClearOrdnanceCode, "fury", null),
                $"ABLE-TO-FAIL CONTROL: CALLBACK {ClearOrdnanceCode} from a definition this host does not answer for is declined");
            ctx.Same(flying, LiveRounds(live), $"…and all {flying} rounds are still flying");

            ctx.Check(cutscene.Host(ClearOrdnanceCode, DropAnim, null),
                $"CALLBACK {ClearOrdnanceCode} from '{DropAnim}' is answered by the cutscene host");
            ctx.Same(0, LiveRounds(live),
                $"…and the {flying} rounds are gone on the frame it lands");
        }
        finally
        {
            cutscene?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // Fires the suite's rounds and steps once so the pool has integrated them, returning how many
    // are alive. A round that struck something on its first step is not one this can count.
    private static int FireAndCount(ProjectilePool pool, WeaponDef gun)
    {
        var muzzle = new Transform3D(Basis.Identity, new Vector3(0f, 4000f, 0f));
        for (int i = 0; i < RoundsFired; i++)
        {
            pool.Spawn(gun, muzzle with { Origin = muzzle.Origin + new Vector3(i * 5f, 0f, 0f) },
                Vector3.Zero);
        }

        pool.SimStep(StepDt);
        return LiveRounds(pool);
    }

    private static int LiveRounds(ProjectilePool pool)
    {
        var rounds = new List<(Vector3 Pos, Vector3 Velocity)>();
        pool.CollectLiveRounds(rounds);
        return rounds.Count;
    }

    private static void DriveRemoval(TestContext ctx, TestWorld world, CampaignDirector director,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var raisers = RaisersOf(world.Runtime, RemoveWingmanCode);
        ctx.Check(raisers.Contains(HookAnim),
            $"'{HookAnim}' raises CALLBACK {RemoveWingmanCode} raisers=[{string.Join(",", raisers)}]");
        ctx.Same(1, raisers.Count, $"and it is the only one of this mission's {world.Runtime.ProgramDefs.Count} definitions that does");
        ctx.Check(SoundAfterCode(world.Runtime, HookAnim, RemoveWingmanCode) is { Length: > 0 } cue,
            $"the event straight after it is the wingman's own line, '{SoundAfterCode(world.Runtime, HookAnim, RemoveWingmanCode) ?? "(none)"}'");

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
            var blocks = AiSkills.LoadRoster(missionZrdr);
            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;
            var spawner = roster;

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
                Spawn = (plan, pos, look, pilot) => spawner.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });

            if (!director.Roster.TryGetValue(Wingman, out var wingman))
            {
                ctx.Check(false, $"{HookChapter}/{HookMission}'s roster spawns a '{Wingman}' block");
                return;
            }

            report.AppendLine($"'{Wingman}' spawned in play={wingman.InPlay} at {wingman.WorldPosition}");
            ctx.Check(wingman.InPlay, $"'{Wingman}' spawns in play, escorting the player");

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
            ctx.Check(world.Runtime.CallbackHost != null, $"the director holds the CALLBACK host slot for {HookAnim}");
            if (world.Runtime.CallbackHost is not { } host)
            {
                return;
            }

            ctx.Check(host(RemoveWingmanCode, HookAnim, null),
                $"CALLBACK {RemoveWingmanCode} is answered by the director's link in the chain");
            ctx.Check(!wingman.InPlay, $"…and '{Wingman}' is out of the world");
            ctx.Check(wingman.Deactivated && !wingman.Parked,
                $"…as a deactivate, not a cutscene park (deactivated={wingman.Deactivated} parked={wingman.Parked})");
            report.AppendLine($"after {RemoveWingmanCode}: in play={wingman.InPlay} " +
                $"deactivated={wingman.Deactivated} parked={wingman.Parked}");

            ctx.Check(host(RemoveWingmanCode, HookAnim, null),
                $"a second {RemoveWingmanCode} is still answered here rather than falling down the chain");
            ctx.Check(!wingman.InPlay && wingman.Deactivated,
                $"…and leaves '{Wingman}' exactly where the first one left her");
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

    // Every definition of the loaded program that authors this code, in a sequence or a reset block,
    // by anim name. Read off the built mission rather than restated, so a data change shows here.
    private static List<string> RaisersOf(AnimRuntime runtime, int code)
    {
        var names = new List<string>();
        foreach (var def in runtime.ProgramDefs)
        {
            if (def.AnimName is not { Length: > 0 } animName)
            {
                continue;
            }

            var blocks = new List<AnimSequence>(def.Sequences);
            if (def.ResetState is { } reset)
            {
                blocks.Add(reset);
            }

            foreach (var block in blocks)
            {
                foreach (var ev in block.Events)
                {
                    if (ev.Kind == "Callback" && (int)(ev.Data.Num("value") ?? -1f) == code
                        && !names.Contains(animName))
                    {
                        names.Add(animName);
                    }
                }
            }
        }

        return names;
    }

    // The name of the first SOUND authored after this code in the same block, which is how the data
    // says what the code was for. Null when the code is followed by no sound.
    private static string? SoundAfterCode(AnimRuntime runtime, string animName, int code)
    {
        foreach (var def in runtime.DefsFor(animName))
        {
            foreach (var block in def.Sequences)
            {
                bool seen = false;
                foreach (var ev in block.Events)
                {
                    if (seen && ev.Kind == "Sound")
                    {
                        return ev.Data.Str("name");
                    }

                    seen |= ev.Kind == "Callback" && (int)(ev.Data.Num("value") ?? -1f) == code;
                }
            }
        }

        return null;
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> sequence,
        string chapter, string folder)
    {
        foreach (var m in sequence)
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        foreach (var (name, fields) in blocks)
        {
            if (name.Equals(CampaignRosterPlan.PlayerBlock, StringComparison.OrdinalIgnoreCase)
                && AiSkills.RosterSpawnPose(fields) is { } pose)
            {
                return (pose.Position,
                    new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
            }
        }

        return (new Vector3(0f, 800f, 0f), Vector3.Forward);
    }

    // The human rig this suite flies nothing with: it exists so the roster's authored 'player' role
    // resolves to something and the escort leader pass has a leader.
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
