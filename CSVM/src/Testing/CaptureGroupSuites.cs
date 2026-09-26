using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>CM02's capture as the objectives see it: the player takes the last live bomber of
/// group 5 through the 967 wing-walk swap, so the group must go on reading one live member (the
/// bomber while the cutscene parks it, the player once flying it) and the <c>DEDG [5, 0]</c>
/// objective that naps the instant loss must stay incomplete. Driven over C3/M05's own roster
/// through the session's <see cref="FlightRoster"/> and its own objective graph, with the
/// mission's capture definition played through the cutscene host the way the approach row starts
/// it, so the 913 park, the 967 swap and the 914 reveal land in their authored order. The same
/// swap re-points the wingman escorting the player onto the rebuilt rig and keeps the director's
/// death hook on it, so both are read here as well.</summary>
internal static class CaptureGroupSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M05";

    // The bomber the capture animation is rooted on, the group its three blocks author, and the
    // definition the approach row on that bomber starts.
    private const string CaptureRoot = "britbalmoral_1";
    private const int BomberGroup = 5;
    private const string CaptureAnim = "ww_balmoral1";

    private const float StepDt = 1f / 60f;

    // The whole episode (the wing walk's 19.25 s motion) and the nap the wiped-out DEDG would put
    // on the instant loss, held past its full length so a completion anywhere in the episode has
    // had its 20 s to end the mission. The budget bounds a definition that never hands off.
    private const float HoldSeconds = 20f;
    private const float EpisodeBudgetS = 30f;

    // The player's death is a crash already on the ground, so the lost ending is due at once.
    private const float DeathWindowSeconds = 2f;

    // The CM02 capture lost the mission 20 s into the wing walk: the cutscene's 913 parked the
    // last bomber inert, its group read empty, and the wiped-out DEDG napped the instant loss
    // awake. The original's park sets a hold flag, never the dead byte DEDG reads.
    [Suite("campaign-capture-group",
        "CM02's capture over C3/M05's own roster and objective graph, played through the "
        + "cutscene host from the approach row's trigger: two bombers down through the debug "
        + "kill complete the at-most-one DEDG and not the at-zero one, the wing walk's 913 "
        + "parks britbalmoral_1 and the group goes on counting it, the 967 swap hides that "
        + "bomber, stamps its group on the rebuilt rig and re-points wingman_4's escort onto "
        + "it, the group never reads below one from the trigger to 20 s past the handoff with "
        + "the at-zero DEDG incomplete throughout and no exception, and the rebuilt rig's "
        + "death still ends the mission lost")]
    internal static void CaptureGroup(TestContext ctx)
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
        var report = new StringBuilder();
        var chain = CheckAuthored(ctx, script, report);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        // The capture's wing walk stages aircraft-archive figures, which only a world built with
        // its cutscene library roots can answer for.
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(Chapter, collision: false, Mission, world =>
                Drive(ctx, world, director, chain, skills, missionZrdr, chapterZrdr, texturesPath, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-campaign-capture-group-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the captured bomber's group reads one through the whole capture and after it, so the instant loss stays asleep");
    }

    // The chain as the mission authors it: a DEDG over group 5 at most one, a DEDG at zero, and
    // the nap the latter puts on an INSTANTLOSS objective. Read off the script, not restated.
    private static Chain CheckAuthored(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        int shootTwo = 0, wipedOut = 0, instantLoss = 0;
        float napSeconds = 0f;
        foreach (var def in script.Objectives)
        {
            // Two objectives author the at-most-one read: the dormant PRIMARY the briefing names
            // and the awake fan-out (OBJECTIVE63), which is the one a fresh graph can complete.
            if (def.Dedg is { Group: BomberGroup, Max: 1 } && !def.BeginDormant)
            {
                shootTwo = def.Number;
            }
            else if (def.Dedg is { Group: BomberGroup, Max: 0 })
            {
                wipedOut = def.Number;
                if (def.NapWhenComplete is { } nap)
                {
                    instantLoss = nap.Target;
                    napSeconds = nap.Seconds;
                }
            }
        }

        report.AppendLine($"authored: shoot-two OBJECTIVE{shootTwo}, wiped-out OBJECTIVE{wipedOut} naps OBJECTIVE{instantLoss} {napSeconds:0} s");
        ctx.Check(shootTwo > 0 && wipedOut > 0,
            $"{Chapter}/{Mission} authors a DEDG over group {BomberGroup} at most one (OBJECTIVE{shootTwo}) and another at zero (OBJECTIVE{wipedOut})");
        var lossDef = instantLoss >= 1 && instantLoss <= script.Objectives.Count ? script.Objectives[instantLoss - 1] : null;
        ctx.Check(lossDef is { InstantLoss: true },
            $"and the wiped-out one naps an INSTANTLOSS objective (OBJECTIVE{instantLoss}), which is the loss the swap has to keep asleep");
        ctx.Check(napSeconds > 0f && napSeconds <= HoldSeconds,
            $"whose nap ({napSeconds:0} s) fits inside the {HoldSeconds:0} s this suite holds past the handoff, so a completion anywhere in the episode would have ended the mission");
        return new Chain(shootTwo, wipedOut, instantLoss);
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director, Chain chain,
        AiSkills skills, string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        var rigs = new[] { rig };
        FlightRoster? roster = null;
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            roster = BuildRoster(ctx, planesGamez, textures, pool, rigs);
            roster.BuildPlayers(rigs);
            var before = rig.Controller ?? throw new InvalidOperationException("no rig was built");
            var flightRoster = roster;

            // The marker graft is what puts the capture's own scaffolding (the wing-walk frame's
            // host) onto the spawned bomber, the same way the session's roster build does.
            int grafted = 0;
            string what = director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                PlayerAirframe = roster.FlyingAirframeOf(0),
                Player = () => rig.Controller,
                NetTrailers = new NetTrailerTargets(
                    () => rig.Controller?.WorldPosition ?? Vector3.Zero,
                    name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => flightRoster.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                AttachMarkers = (block, node) => grafted += RosterMarkers.Attach(
                    world.Gamez, world.Session.Builder.Scene, world.Runtime, block, node),
                Rng = new Random(1),
            });
            report.AppendLine($"build summary suffix: '{what}', {grafted} marker graft(s)");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = pool,
                ListenerPosition = () => rig.Controller?.WorldPosition ?? Vector3.Zero,
                PlayerAircraft = () => rig.Controller,
                Aircraft = () => flightRoster.AiAircraft,
                Rng = new Random(1),
            });

            var bombers = Bombers(director.Roster, report);
            ctx.Check(bombers.Count >= 3 && bombers.ContainsKey(CaptureRoot),
                $"the roster carries group {BomberGroup}'s bombers, '{CaptureRoot}' among them: [{string.Join(",", bombers.Keys)}]");
            ctx.Check(director.Roster.TryGetValue(AirframeHandover.WingmanName, out var wingman)
                      && wingman.Pilot?.Escort is { } escort && ReferenceEquals(escort.Leader, before),
                $"'{AirframeHandover.WingmanName}' escorts the player's aircraft before the swap");
            if (bombers.Count < 3 || wingman == null)
            {
                return;
            }

            ShootTwo(ctx, director, chain, bombers, report);
            var after = Capture(ctx, world, cutscene, director, chain, roster, rig, before,
                bombers[CaptureRoot], wingman, report);
            if (after == null)
            {
                return;
            }

            Die(ctx, director, rig, report);
        }
        finally
        {
            var live = rig.Controller;
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            live?.Free();
            foreach (var ai in members)
            {
                ai.Free();
            }

            cutscene.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // Group 5's members, read off the group the roster phase stamped on each rig.
    private static Dictionary<string, FlightController> Bombers(
        IReadOnlyDictionary<string, FlightController> roster, StringBuilder report)
    {
        var bombers = new Dictionary<string, FlightController>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rig) in roster)
        {
            if (rig.Group == BomberGroup)
            {
                bombers[name] = rig;
            }
        }

        report.AppendLine($"group {BomberGroup}: [{string.Join(",", bombers.Keys)}]");
        return bombers;
    }

    // Two of the three down through the debug kill, which the DEDG walk sees exactly as a shot
    // down: the shoot-two objective completes and the wiped-out one does not.
    private static void ShootTwo(TestContext ctx, CampaignDirector director, Chain chain,
        Dictionary<string, FlightController> bombers, StringBuilder report)
    {
        foreach (var (name, bomber) in bombers)
        {
            if (!name.Equals(CaptureRoot, StringComparison.OrdinalIgnoreCase))
            {
                bomber.DebugForceCrash();
            }
        }

        for (int i = 0; i < 30; i++)
        {
            director.Step(StepDt);
        }

        int? live = director.GroupLiveCount(BomberGroup);
        report.AppendLine($"two down: group {BomberGroup} counts {live?.ToString() ?? "-"}, " +
            $"OBJECTIVE{chain.ShootTwo} complete={director.Graph?.CompletedOf(chain.ShootTwo)}, " +
            $"OBJECTIVE{chain.WipedOut} complete={director.Graph?.CompletedOf(chain.WipedOut)}");
        ctx.Same(1, live ?? -1, $"with two bombers down, group {BomberGroup} counts the one left");
        ctx.Check(director.Graph?.CompletedOf(chain.ShootTwo) == true,
            $"and OBJECTIVE{chain.ShootTwo} (at most one) completes");
        ctx.Check(director.Graph?.CompletedOf(chain.WipedOut) == false,
            $"while OBJECTIVE{chain.WipedOut} (wiped out) does not");
    }

    // The capture as the flown session runs it: the mission's definition started through the
    // trigger seam the approach row starts it with and hosted by the cutscene controller, on a
    // realtime clock with each aircraft stepping itself and the director stepping beside them
    // (INSTR-26). The group is read every step from the start to 20 s past the handoff, since a
    // walk that drops the parked bomber reads zero only while the park holds.
    private static FlightController? Capture(TestContext ctx, TestWorld world, CutsceneController cutscene,
        CampaignDirector director, Chain chain, FlightRoster roster, PlayerRig rig, FlightController before,
        FlightController captured, FlightController wingman, StringBuilder report)
    {
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        int minLive = int.MaxValue, minLiveParked = int.MaxValue;
        float swappedAt = -1f, handoffAt = -1f, parkedFor = 0f, elapsed = 0f;
        bool parkedCounted = false;
        Exception? thrown = null;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(ClosureOf(world, CaptureAnim));
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);
            cutscene.WorldHeld += held => director.HoldForCutscene(held);
            world.Runtime.CallbackHost = cutscene.Host;
            cutscene.Own(CaptureAnim);
            int started = world.Runtime.PlayMissionTrigger(CaptureAnim).Count;
            ctx.Check(started > 0 && cutscene.Playing,
                $"the approach row's trigger starts '{CaptureAnim}' and the cutscene host owns the episode");

            int budget = (int)((EpisodeBudgetS + HoldSeconds) / StepDt);
            for (int i = 0; i < budget && thrown == null; i++)
            {
                elapsed = i * StepDt;
                if (handoffAt >= 0f && elapsed - handoffAt >= HoldSeconds)
                {
                    break;
                }

                try
                {
                    clock.BeginFrame(StepDt);
                    world.Runtime.Advance(StepDt);
                    cutscene.Tick();
                    rig.Controller?._PhysicsProcess(StepDt);
                    captured._PhysicsProcess(StepDt);
                    wingman._PhysicsProcess(StepDt);
                    director.Step(StepDt);
                }
                catch (Exception e)
                {
                    thrown = e;
                    break;
                }

                int live = director.GroupLiveCount(BomberGroup) ?? -1;
                minLive = Math.Min(minLive, live);
                if (captured.Parked)
                {
                    parkedFor += StepDt;
                    parkedCounted = true;
                    minLiveParked = Math.Min(minLiveParked, live);
                }

                if (swappedAt < 0f && !ReferenceEquals(rig.Controller, before))
                {
                    swappedAt = elapsed;
                    report.AppendLine($"t={elapsed:0.00} s swapped: group {BomberGroup} counts {live}, " +
                        $"'{CaptureRoot}' inert={captured.Inert} parked={captured.Parked}");
                }

                if (handoffAt < 0f && swappedAt >= 0f && !cutscene.Playing)
                {
                    handoffAt = elapsed;
                    report.AppendLine($"t={elapsed:0.00} s handoff: group {BomberGroup} counts {live}, " +
                        $"OBJECTIVE{chain.WipedOut} complete={director.Graph?.CompletedOf(chain.WipedOut)}");
                }
            }
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }

        var after = rig.Controller;
        int? liveAtEnd = director.GroupLiveCount(BomberGroup);
        report.AppendLine($"episode: swap at {swappedAt:0.00} s, handoff at {handoffAt:0.00} s, stepped to {elapsed:0.00} s, " +
            $"exception={(thrown == null ? "none" : thrown.GetType().Name)}, '{CaptureRoot}' parked for {parkedFor:0.00} s, " +
            $"group {BomberGroup} min {minLive} (min while parked {(parkedCounted ? minLiveParked.ToString() : "-")}), at end {liveAtEnd?.ToString() ?? "-"}, " +
            $"OBJECTIVE{chain.WipedOut} complete={director.Graph?.CompletedOf(chain.WipedOut)}, " +
            $"OBJECTIVE{chain.InstantLoss} state={director.Graph?.StateOf(chain.InstantLoss)}, ended={director.Result != null}");
        ctx.Check(thrown == null,
            $"the episode, the aircraft and the director step through the capture and {HoldSeconds:0} s past it with no exception ({thrown?.GetType().Name ?? "none"}: {thrown?.Message ?? ""})");
        ctx.Check(swappedAt >= 0f && after != null && !ReferenceEquals(after, before),
            $"the hosted capture swaps the player's rig at {swappedAt:0.00} s");
        ctx.Check(handoffAt > swappedAt,
            $"and the episode hands off after the swap, at {handoffAt:0.00} s");
        if (after == null || handoffAt < 0f)
        {
            return null;
        }

        ctx.Check(parkedCounted && parkedFor > 1f,
            $"the wing walk's 913 parks '{CaptureRoot}' for a measurable stretch ({parkedFor:0.00} s) before the swap hides it");
        ctx.Same(1, parkedCounted ? minLiveParked : -1,
            $"and group {BomberGroup} counts the parked bomber the whole time, the way the original's DEDG walk reads a held vehicle");
        ctx.Check(captured.Inert && !captured.Parked,
            $"'{CaptureRoot}', the aircraft the player took, ends hidden and no longer parked, so the reveal did not put it back");
        ctx.Same(BomberGroup, after.Group ?? -1,
            $"the rebuilt rig carries group {BomberGroup}, the captured bomber's own (the +0x388 copy)");
        ctx.Same(1, minLive,
            $"group {BomberGroup} never reads below one from the trigger to {HoldSeconds:0} s past the handoff");
        ctx.Same(1, liveAtEnd ?? -1,
            $"and counts one at the end: the hidden bomber is gone and the player stands in for it");
        ctx.Check(wingman.Pilot?.Escort is { } escort && ReferenceEquals(escort.Leader, after),
            $"'{AirframeHandover.WingmanName}' escorts the rebuilt rig rather than the freed one");
        ctx.Check(wingman.InPlay,
            $"and is in play, flying the aeroplane the player left");
        ctx.Check(director.Graph?.CompletedOf(chain.WipedOut) == false,
            $"OBJECTIVE{chain.WipedOut} stays incomplete from the trigger to {HoldSeconds:0} s past the handoff, so no instant loss is napped awake");
        ctx.Check(director.Result == null, $"and the mission is still running");
        return after;
    }

    // Every definition the capture can reach, its own plus the CALL_ANIMATION closure: what the
    // session's own host answers for, and the only way the called wing walk's codes are hosted.
    private static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(new[] { anim }).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The player's death in the new rig still reaches the director: the hook moved with the swap.
    private static void Die(TestContext ctx, CampaignDirector director, PlayerRig rig, StringBuilder report)
    {
        rig.Controller?.DebugForceCrash();
        float elapsed = 0f, endedAt = -1f;
        while (elapsed < DeathWindowSeconds && endedAt < 0f)
        {
            director.Step(StepDt);
            elapsed += StepDt;
            if (director.Result != null)
            {
                endedAt = elapsed;
            }
        }

        report.AppendLine($"death: ended at {endedAt:0.00} s outcome={director.Result?.Outcome.ToString() ?? "-"}");
        ctx.Check(director.Result is { Outcome: MissionOutcome.Lost },
            $"losing the rebuilt rig ends the mission lost, so the death hook followed the swap: {director.Result?.Outcome.ToString() ?? "still running"}");
    }

    private static FlightRoster BuildRoster(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool pool, IReadOnlyList<PlayerRig> rigs)
    {
        var spec = SessionSpec.Parse(new[] { "--plane=player_bhawk" });
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            CamParamsFor = _ => new CamParams(),
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
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host, resources,
            new FlightWorldBindings
            {
                Projectiles = pool,
                Gamez = planesGamez,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                PauseState = new PauseState(),
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
            }, new HighStarts());
    }

    private readonly record struct Chain(int ShootTwo, int WipedOut, int InstantLoss);

    // One start, high over the mission's terrain, so the rig is flying when the swap rebuilds it.
    private sealed class HighStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 1200f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 90f);
            }

            return starts;
        }
    }
}
