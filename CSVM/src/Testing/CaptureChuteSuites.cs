using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the crew that bails out at the end of CM02's capture: the wing walk's
/// player definition calls the mission's chute definition three times, each call placed at the same
/// authored offset off the walk frame, so three figures hang in the air together and each drifts
/// for the length of its own script. Played over C3/M05's built world with the captured aeroplane
/// spawned from its roster block, through the cutscene host, the way the approach row starts it.
/// The figure is staged out of the aircraft archive rather than the chapter gamez, so the three
/// calls are also what proves that stage takes copies (docs/architecture.md).</summary>
internal static class CaptureChuteSuites
{
    private const int Cm02Seq = 1;
    private const string CaptureBlock = "britbalmoral_1";
    private const string CaptureAnim = "ww_balmoral1";
    private const string ChuteAnim = "ww_chuteman";
    private const string WalkFrame = "wingwalk_parent";

    private const float StepDt = 1f / 60f;
    private const float PlayBudgetS = 30f;

    // How many calls the wing walk's player definition authors, and how long the chute definition's
    // own scripts run: the shortest a figure may be drawn for once it is in the air.
    private const int CrewCount = 3;
    private const float DriftS = 5.5f;

    // How far from the walk frame a figure placed at the call's authored offset may sit. The offset
    // itself is (0, -2, 8.5) and the frame moves 15 m/s, so a frame's slack covers both.
    private const float SiteToleranceM = 30f;

    [Suite("campaign-capture-chutes",
        "the crew bailing out at the end of CM02's capture over C3/M05's BUILT world, the "
        + "capture played through the cutscene host from the approach row's trigger: the wing "
        + "walk's player definition calls the chute definition three times, each figure is "
        + "placed at the walk frame the call sites it on rather than at the archive's own "
        + "origin, three hang in the air at once, and each stays drawn for its whole drift")]
    internal static void CaptureChutes(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = WingWalkCameraSuites.MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm02Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm02Seq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => Drive(ctx, world, chapter, folder, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-capture-chutes-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"played {chapter}/{folder}'s '{CaptureAnim}' and watched its crew bail out");
    }

    private static void Drive(TestContext ctx, TestWorld world, string chapter, string folder,
        StringBuilder report)
    {
        var plan = WingWalkCameraSuites.PlanNamed(ctx, chapter, folder, CaptureBlock);
        if (plan == null)
        {
            ctx.Check(false, $"{chapter}/{folder}'s roster carries a '{CaptureBlock}' block");
            return;
        }

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
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
            roster = WingWalkCameraSuites.BuildRoster(ctx, world, chapter, textures, pool, rigs, plan.Position);
            roster.BuildPlayers(rigs);
            var aim = plan.Position + Vector3.Forward;
            var captured = roster.SpawnAi(CampaignRosterPlan.SpawnFor(plan, plan.Position, aim,
                AiPilot.HoldingCourse(plan.Position, aim)));
            if (captured == null)
            {
                ctx.Check(false, $"the '{CaptureBlock}' block spawns an aircraft to capture");
                return;
            }

            RosterMarkers.Attach(world.Gamez, world.Session.Builder.Scene, world.Runtime, CaptureBlock, captured);
            Play(ctx, world, rig, cutscene, roster, captured, report);
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

    private static void Play(TestContext ctx, TestWorld world, PlayerRig rig,
        CutsceneController cutscene, FlightRoster roster, FlightController captured,
        StringBuilder report)
    {
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        float now = 0f;
        var chuteStarts = new List<(float At, string Anchor, float OffFrameM)>();
        var chuteEnds = new List<(float At, string Anchor)>();
        void Started(AnimDefinition def, Node3D? anchor)
        {
            if (string.Equals(def.AnimName, ChuteAnim, StringComparison.OrdinalIgnoreCase))
            {
                chuteStarts.Add((now, ChainOf(anchor), OffFrame(world, anchor)));
            }
        }

        void Finished(AnimDefinition def, Node3D? anchor)
        {
            if (string.Equals(def.AnimName, ChuteAnim, StringComparison.OrdinalIgnoreCase))
            {
                chuteEnds.Add((now, ChainOf(anchor)));
            }
        }

        world.Runtime.OnInstanceStarted += Started;
        world.Runtime.OnInstanceFinished += Finished;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
            cutscene.HostDefinitions(WingWalkCameraSuites.ClosureOf(world, CaptureAnim));
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);
            world.Runtime.CallbackHost = (code, anim, root) =>
            {
                report.AppendLine($"  t={now,6:0.00} code {code} from '{anim}'");
                return cutscene.Host(code, anim, root);
            };
            cutscene.Own(CaptureAnim);
            world.Runtime.PlayMissionTrigger(CaptureAnim);
            ctx.Check(cutscene.Playing, $"the approach row's trigger starts '{CaptureAnim}' under the host");

            float handoffAt = -1f;
            int maxVisible = 0;
            var visibleSpans = new Dictionary<ulong, (float From, float To, string Name)>();
            for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
            {
                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                rig.Controller?._PhysicsProcess(StepDt);
                captured._PhysicsProcess(StepDt);
                now = (i + 1) * StepDt;
                if (handoffAt < 0f && !cutscene.Playing)
                {
                    handoffAt = now;
                    report.AppendLine($"  t={now,6:0.00} handoff");
                }

                var chutes = world.Runtime.FindNodes(AircraftStage.ChuteNode);
                int visible = 0;
                foreach (var chute in chutes)
                {
                    if (!chute.IsVisibleInTree())
                    {
                        continue;
                    }

                    visible++;
                    ulong id = chute.GetInstanceId();
                    if (!visibleSpans.TryGetValue(id, out var span))
                    {
                        span = (now, now, ChainOf(chute));
                    }

                    visibleSpans[id] = (span.From, now, span.Name);
                }

                maxVisible = Math.Max(maxVisible, visible);
                if (i % 30 == 0)
                {
                    var lines = new List<string>();
                    foreach (var chute in chutes)
                    {
                        lines.Add($"[{ChainOf(chute)} vis={chute.IsVisibleInTree()} at {chute.GlobalPosition}]");
                    }

                    report.AppendLine($"t={now,5:0.0} playing={cutscene.Playing} {ChuteAnim}={world.Runtime.AnimStateOf(ChuteAnim)} " +
                        $"chutes={chutes.Count} visible={visible} frame={FrameOrigin(world)} {string.Join(" ", lines)}");
                }
            }

            foreach (var (at, anchor, offFrame) in chuteStarts)
            {
                report.AppendLine($"chute started t={at:0.00} on {anchor}, {offFrame:0.#} m off the walk frame");
            }

            foreach (var (at, anchor) in chuteEnds)
            {
                report.AppendLine($"chute finished t={at:0.00} on {anchor}");
            }

            foreach (var (id, span) in visibleSpans)
            {
                report.AppendLine($"chute {span.Name} visible {span.From:0.00} .. {span.To:0.00} s ({span.To - span.From:0.00} s)");
            }

            report.AppendLine($"handoff at {handoffAt:0.00}, max visible at once {maxVisible}");
            ctx.Same(CrewCount, chuteStarts.Count,
                $"the wing walk's own definition calls '{ChuteAnim}' {CrewCount} times, one figure per thrown-out crewman");
            ctx.Same(CrewCount, maxVisible,
                $"and that many chute figures are drawn at once rather than one call taking the others' figure");
            float farthest = 0f;
            foreach (var (_, _, offFrame) in chuteStarts)
            {
                farthest = Math.Max(farthest, offFrame);
            }

            ctx.Check(chuteStarts.Count > 0 && farthest <= SiteToleranceM,
                $"each starts at the walk frame the call sites it on, within {SiteToleranceM:0} m, not at the world origin the archive staged it at (farthest {farthest:0.#} m)");
            ctx.Same(CrewCount, chuteEnds.Count, $"each call runs its own script to its end");
            float shortest = float.MaxValue;
            foreach (var (_, span) in visibleSpans)
            {
                shortest = Math.Min(shortest, span.To - span.From);
            }

            ctx.Check(visibleSpans.Count == CrewCount && shortest >= DriftS,
                $"and each figure stays drawn for its whole authored drift, at least {DriftS:0.#} s (shortest {shortest:0.##} s)");
        }
        finally
        {
            world.Runtime.OnInstanceStarted -= Started;
            world.Runtime.OnInstanceFinished -= Finished;
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }
    }

    // How far the started figure sits from the frame the call sites it on. The whole of the defect
    // is that an unplaced figure plays at the archive's own origin, kilometres from the aeroplane.
    private static float OffFrame(TestWorld world, Node3D? anchor)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        return found.Count > 0 && anchor != null
            ? anchor.GlobalTransform.Origin.DistanceTo(found[0].GlobalTransform.Origin)
            : float.MaxValue;
    }

    private static string FrameOrigin(TestWorld world)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        return found.Count > 0 ? $"{found[0].GlobalTransform.Origin}" : "absent";
    }

    private static string ChainOf(Node? node)
    {
        var names = new List<string>();
        for (Node? at = node; at != null && names.Count < 6; at = at.GetParent())
        {
            names.Add(at is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                ? n3d.GetMeta(AnimRuntime.NameMeta).AsString()
                : at.Name);
        }

        return names.Count == 0 ? "(none)" : string.Join("<", names);
    }
}
