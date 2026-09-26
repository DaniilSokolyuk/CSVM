using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over CM15's capture cutscene: C2/M05's paratrooper drop is rooted on the
/// aircraft archive's <c>balmoral</c>, reparented onto the placed <c>cargozep2</c> for its own
/// shot and moved through it, so staging that node is what lets the cutscene camera frame the
/// aeroplane it is filmed around. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class BalmoralCaptureSuites
{
    private const int MissionSeq = 14; // CM15, C2/M05.
    private const string DropAnim = "drop_paratroopers";
    private const string DropZeppelin = "cargozep2";

    // The mission roster block the staged Balmoral stands in for: Tex's bomber, the aeroplane the
    // drop is filmed around.
    private const string StandInBlock = "balmoral_1";

    // The vehicle def whose own nodename IS the intro prop, and the base of the wingman def that
    // derives from it, so it is where that model's authored pattern lives.
    private const string PropDef = "devastator";

    // C3/M01: a mission that stages an aircraft (its own chuteman drop-off needs one) but never
    // names 'balmoral' in any of its own definitions, the same position dropoff-chuteman-stage
    // and IntroAircraftSuites drive.
    private const int OtherMissionSeq = 0;

    // A few seconds of that mission's own bootstrap and start anims, long enough for anything
    // that might incidentally reach the node to have had its chance.
    private const int OtherMissionDriveFrames = 300;

    private const float StepDt = 1f / 60f;

    // The camera's own SI-script shot and the 6 s OBJECT_MOTION_FROM_TO run concurrently; the
    // budget clears both several times over.
    private const float DriveBudgetS = 15f;

    // The widest half-angle a shot can frame: an authored FOV
    // narrower than this puts a posed prop off screen at any of them.
    private const float FrustumHalfAngleDeg = 60f;

    // A node still at the archive's own build origin has not been posed by anything.
    private const float PosedMinM = 10f;

    [Suite("campaign-cm15-capture",
        "CM15's capture cutscene over C2/M05's BUILT world: the paratrooper drop is rooted on "
        + "the aircraft archive's 'balmoral', and staging that node lets the drop's own "
        + "OBJECT_ADD_CHILD/OBJECT_MOTION_FROM_TO pair reparent it onto 'cargozep2', pose it and "
        + "draw it inside the cutscene camera's frustum through the shot, then hand it back to "
        + "the world root; the pilot's own re-placement is cutscene-handoff-unposed's, unchanged "
        + "here")]
    internal static void Cm15Capture(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), MissionSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {MissionSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");

        var report = new StringBuilder();
        report.AppendLine($"seq {MissionSeq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder, world => Drive(ctx, world, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cm15-capture-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s '{DropAnim}' and watched the Balmoral through the shot");
    }

    /// <summary>Drives C3/M01, a mission with no <c>balmoral</c> definition at all, over its BUILT
    /// world: the staged node ships ACTIVE, so it must sit under a switched-off holder rather than
    /// the world root, or it draws at the archive's own build origin in every mission that stages
    /// an aircraft, never posed and never asked for. Regression cover for a hash the
    /// unconditionally-visible staging moved once already.</summary>
    [Suite("campaign-balmoral-hidden",
        "nothing draws at the world origin when no definition asks for it, over C3/M01's BUILT "
        + "world: that mission stages an aircraft (its own chuteman drop-off needs one) but never "
        + "names 'balmoral', so the archive's own ACTIVE node must stay undrawn under its "
        + "switched-off holder through the mission's own bootstrap and start anims, at the "
        + "position it was built at")]
    internal static void Cm15BalmoralHidden(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), OtherMissionSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {OtherMissionSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");

        var report = new StringBuilder();
        report.AppendLine($"seq {OtherMissionSeq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder, world => DriveHidden(ctx, world, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cm15-balmoral-hidden-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder} and confirmed 'balmoral' stays undrawn there");
    }

    /// <summary>The livery half of the same shot: CM15 puts two Balmoral models on screen for one
    /// aeroplane, so the staged prop has to wear the paint the mission's own <c>balmoral_1</c> rig
    /// resolved rather than the archive's shipped skins. Checked as the substitution reaching the
    /// staged subtree's built materials, with the parachutist beside it as the control.</summary>
    [Suite("campaign-cm15-staged-paint",
        "the livery of CM15's staged Balmoral over C2/M05's BUILT world: the drop prop's own "
        + "materials name an aircraft skin prefix, the mission's 'balmoral_1' block flies on the "
        + "player's team with a pattern authored up its own def chain, painting that scheme onto "
        + "the staged subtree swaps its built skins, the intro prop takes the pattern its own "
        + "vehicle def authors, and the parachutist carries no skins so nothing reaches it")]
    internal static void Cm15StagedPaint(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), MissionSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {MissionSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");
        string rof = Path.Combine(ctx.DataRoot, "extracted", "rof");
        ctx.RequireData(rof, $"extracted UI archive (paint patterns)");

        var report = new StringBuilder();
        report.AppendLine($"seq {MissionSeq} -> {chapter}/{folder}");
        var liveries = new LiveryResolver(SessionSpec.Parse(Array.Empty<string>()), rof);
        if (liveries.Patterns.IsEmpty)
        {
            throw new SuiteSkippedException("the extracted UI archive ships no paint patterns");
        }

        var stand = StandInPlan(ctx, chapter, missionZrdr);
        var balmoralScheme = RigScheme(ctx, liveries, stand);
        var propScheme = liveries.DefScheme(ctx.ZrdrPath, PropDef);
        report.AppendLine($"'{StandInBlock}' -> def '{stand?.Def}' ai '{stand?.AiDef}' "
            + $"airframe '{stand?.PlaneNode}' team {stand?.Team} -> {balmoralScheme}");
        report.AppendLine($"'{AircraftStage.PropNode}' -> def '{PropDef}' -> {propScheme}");
        ctx.Check(stand is { Team: AimAssist.PlayerTeam },
            $"CM15's '{StandInBlock}' flies on the player's own team, so its rig draws a livery");
        // ⚠ Its def chain authors none, so this scheme is the default-pattern rule's, not a value
        // read out of the data; only the intro prop's own is authored.
        ctx.Check(balmoralScheme != null && stand?.AiDef != null
            && liveries.DefScheme(ctx.ZrdrPath, stand.AiDef) == null
            && string.Equals(balmoralScheme.Pattern, LiveryResolver.DefaultPattern, StringComparison.OrdinalIgnoreCase),
            $"and wears the default pattern ('{balmoralScheme?.Pattern}'), its own def chain authoring none");
        ctx.Check(propScheme != null && balmoralScheme != null
            && string.Equals(propScheme.Pattern, balmoralScheme.Pattern, StringComparison.OrdinalIgnoreCase),
            $"the intro prop's own '{PropDef}' def AUTHORS that same militia's pattern ('{propScheme?.Pattern}')");

        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => PaintStaged(ctx, world, balmoralScheme, propScheme, liveries.Patterns, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cm15-staged-paint-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"painted {chapter}/{folder}'s staged props from their own stand-ins' schemes");
    }

    // What the block's own rig is painted in, resolved in the spawn's order (Session/
    // AiFlightAssembler.cs): the AI def's authored militia first, then the default pattern an
    // aircraft on the player's own team draws when nothing authored one for it.
    private static PaintScheme? RigScheme(TestContext ctx, LiveryResolver liveries,
        RosterSpawnPlan? plan)
    {
        if (plan == null)
        {
            return null;
        }

        if (plan.AiDef is { } aiDef && liveries.DefScheme(ctx.ZrdrPath, aiDef) is { } authored)
        {
            return authored;
        }

        return liveries.SchemeFor(0, ctx.ZrdrPath, liveries.NewPaintRng(),
            useDefaultPattern: plan.Team == AimAssist.PlayerTeam);
    }

    // The mission's own roster block the staged Balmoral stands in for, planned off the mission
    // data alone: what its rig resolves is decided here, before any aeroplane is built.
    private static RosterSpawnPlan? StandInPlan(TestContext ctx, string chapter, string missionZrdr)
    {
        var plan = CampaignRosterPlan.Build(
            AiSkills.LoadRoster(missionZrdr),
            VehicleDefs.Load(ctx.ZrdrPath),
            AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, chapter)),
            netDraw: _ => 0);
        foreach (var spawn in plan.Spawns)
        {
            if (spawn.Name.Equals(StandInBlock, StringComparison.OrdinalIgnoreCase))
            {
                return spawn;
            }
        }

        return null;
    }

    private static void PaintStaged(TestContext ctx, TestWorld world, PaintScheme? balmoralScheme,
        PaintScheme? propScheme, PatternLibrary patterns, StringBuilder report)
    {
        if (world.Session.Aircraft is not { Balmoral: { } balmoral } stage)
        {
            ctx.Check(false, $"the world build staged the archive's '{AircraftStage.BalmoralNode}' node");
            return;
        }

        foreach (string node in StagedNodeNames())
        {
            report.AppendLine($"staged '{node}': skins {stage.SkinPrefixOf(node) ?? "(none)"}");
        }

        ctx.Check(stage.SkinPrefixOf(AircraftStage.BalmoralNode) != null,
            $"the staged Balmoral's own materials name an aircraft skin prefix, so a livery can reach it");
        ctx.Check(stage.SkinPrefixOf(AircraftStage.ChuteNode) == null,
            $"and '{AircraftStage.ChuteNode}' names none, being a parachutist rather than an airframe");

        var before = Albedos(balmoral);
        var painter = stage.Paint(AircraftStage.BalmoralNode, balmoralScheme, patterns);
        int changed = Changed(before, Albedos(balmoral));
        report.AppendLine($"'{AircraftStage.BalmoralNode}': {before.Count} textured material(s), "
            + $"{changed} re-resolved, {painter?.PaintedSkins ?? 0} skin(s) painted");
        ctx.Check(painter != null && painter.PaintedSkins > 0,
            $"painting the rig's scheme onto the staged Balmoral composites its skins ({painter?.PaintedSkins ?? 0})");
        ctx.Check(changed > 0,
            $"and the substitution reaches the built materials ({changed} of {before.Count} re-resolved)");
        PaintProp(ctx, stage, propScheme, patterns, report);
        PaintChute(ctx, stage, balmoralScheme, patterns, report);
    }

    // The intro prop, on the same route off its own def's authored pattern.
    private static void PaintProp(TestContext ctx, AircraftStage stage, PaintScheme? scheme,
        PatternLibrary patterns, StringBuilder report)
    {
        if (stage.Prop is not { } prop)
        {
            ctx.Check(false, $"the world build staged '{AircraftStage.PropNode}' too");
            return;
        }

        var before = Albedos(prop);
        var painter = stage.Paint(AircraftStage.PropNode, scheme, patterns);
        int changed = Changed(before, Albedos(prop));
        report.AppendLine($"'{AircraftStage.PropNode}': {before.Count} textured material(s), "
            + $"{changed} re-resolved, {painter?.PaintedSkins ?? 0} skin(s) painted");
        ctx.Check(painter != null && painter.PaintedSkins > 0 && changed > 0,
            $"the intro prop takes its own def's pattern the same way ({changed} material(s) re-resolved)");
    }

    // The control: one builder no longer serves every subtree, so a scheme aimed at the aeroplane
    // must not reach the parachutist that shares the stage with it.
    private static void PaintChute(TestContext ctx, AircraftStage stage, PaintScheme? scheme,
        PatternLibrary patterns, StringBuilder report)
    {
        if (stage.Chuteman is not { } chute)
        {
            ctx.Check(false, $"the world build staged '{AircraftStage.ChuteNode}' too");
            return;
        }

        var before = Albedos(chute);
        bool painted = stage.Paint(AircraftStage.ChuteNode, scheme, patterns) != null;
        int changed = Changed(before, Albedos(chute));
        report.AppendLine($"'{AircraftStage.ChuteNode}': {before.Count} textured material(s), "
            + $"{changed} re-resolved, painted={painted}");
        ctx.Check(!painted && changed == 0,
            $"'{AircraftStage.ChuteNode}' keeps the textures the archive shipped it with");
    }

    // Every textured material under a staged subtree paired with the texture it currently
    // resolves to, which is what a repaint rewrites and a comparison can therefore see.
    private static List<(ShaderMaterial Material, ulong Texture)> Albedos(Node node)
    {
        var found = new List<(ShaderMaterial, ulong)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh })
            {
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                {
                    if (mesh.SurfaceGetMaterial(i) is ShaderMaterial sm
                        && sm.GetShaderParameter("albedo_tex").As<Texture2D>() is { } tex)
                    {
                        found.Add((sm, tex.GetInstanceId()));
                    }
                }
            }

            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }

        Walk(node);
        return found;
    }

    private static int Changed(List<(ShaderMaterial Material, ulong Texture)> before,
        List<(ShaderMaterial Material, ulong Texture)> after)
    {
        int changed = 0;
        for (int i = 0; i < before.Count && i < after.Count; i++)
        {
            if (before[i].Texture != after[i].Texture)
            {
                changed++;
            }
        }

        return changed;
    }

    private static IEnumerable<string> StagedNodeNames()
    {
        yield return AircraftStage.PropNode;
        yield return AircraftStage.ChuteNode;
        yield return AircraftStage.BalmoralNode;
        foreach (string name in AircraftStage.FigureNodes)
        {
            yield return name;
        }

        foreach (string name in AircraftStage.PropNodes)
        {
            yield return name;
        }
    }

    private static void DriveHidden(TestContext ctx, TestWorld world, StringBuilder report)
    {
        if (world.Session.Aircraft is not { Balmoral: { } balmoral })
        {
            ctx.Check(false,
                $"the world build staged the archive's '{AircraftStage.BalmoralNode}' node here too");
            return;
        }

        ctx.Check(world.Session.Program.ByAnimName(DropAnim).Count == 0,
            $"{world.Chapter} compiles no '{DropAnim}' definition, so nothing here ever names '{AircraftStage.BalmoralNode}'");

        var startAt = balmoral.GlobalPosition;
        int drawn = 0;
        for (int i = 0; i < OtherMissionDriveFrames; i++)
        {
            world.Runtime.Advance(StepDt);
            if (balmoral.IsVisibleInTree())
            {
                drawn++;
            }
        }

        var endAt = balmoral.GlobalPosition;
        report.AppendLine($"'{AircraftStage.BalmoralNode}' over {OtherMissionDriveFrames} frame(s): "
            + $"drawn {drawn}, started at {startAt}, ended at {endAt}");

        ctx.Check(drawn == 0,
            $"'{AircraftStage.BalmoralNode}' is never drawn over the mission's own bootstrap and start anims (drawn {drawn} frame(s))");
        ctx.Check(startAt.IsEqualApprox(endAt),
            $"and stays exactly where it was built ({startAt} -> {endAt}), reparented by nothing");
    }

    private static void Drive(TestContext ctx, TestWorld world, StringBuilder report)
    {
        if (world.Session.Aircraft is not { Balmoral: { } balmoral })
        {
            ctx.Check(false,
                $"the world build staged the archive's '{AircraftStage.BalmoralNode}' node the drop poses");
            return;
        }

        ctx.Check(world.Session.Program.ByAnimName(DropAnim).Count > 0,
            $"{world.Chapter} compiles '{DropAnim}'");

        var camera = world.Runtime.FindNodes(CutsceneController.CameraNode);
        if (camera.Count == 0)
        {
            ctx.Check(false, $"the world build stood up '{CutsceneController.CameraNode}'");
            return;
        }

        var zeppelin = world.Runtime.FindNodes(DropZeppelin);
        if (zeppelin.Count == 0)
        {
            ctx.Check(false, $"{world.Chapter} places '{DropZeppelin}', the shot's own anchor");
            return;
        }

        var startAt = balmoral.GlobalPosition;
        int frames = 0, drawn = 0, posed = 0, framed = 0, onZeppelin = 0;
        float bestAngle = 999f;
        var bestAt = startAt;
        var lastAt = startAt;
        world.Runtime.PlayMissionTrigger(DropAnim);
        for (float t = 0f; t < DriveBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            frames++;
            lastAt = balmoral.GlobalPosition;
            bool isDrawn = balmoral.IsVisibleInTree();
            bool isPosed = lastAt.DistanceTo(startAt) > PosedMinM;
            float angle = AngleOffAxis(camera[0], lastAt);
            if (isDrawn)
            {
                drawn++;
            }

            if (isDrawn && isPosed)
            {
                posed++;
            }

            if (isDrawn && isPosed && angle <= FrustumHalfAngleDeg)
            {
                framed++;
            }

            if (ReferenceEquals(balmoral.GetParent(), zeppelin[0]))
            {
                onZeppelin++;
            }

            if (angle < bestAngle)
            {
                bestAngle = angle;
                bestAt = lastAt;
            }
        }

        bool endsOffZeppelin = !ReferenceEquals(balmoral.GetParent(), zeppelin[0]);
        report.AppendLine($"'{DropAnim}' ran {frames} frame(s): drawn {drawn}, posed {posed}, "
            + $"framed {framed}, on '{DropZeppelin}' {onZeppelin}, ends off it {endsOffZeppelin}");
        report.AppendLine($"'{AircraftStage.BalmoralNode}' started at {startAt}, ended at {lastAt}, "
            + $"moved {lastAt.DistanceTo(startAt):0.#} m; best angle {bestAngle:0.#} deg at {bestAt}");

        ctx.Check(onZeppelin > 0,
            $"the drop's own OBJECT_ADD_CHILD reparents '{AircraftStage.BalmoralNode}' onto '{DropZeppelin}' for the shot");
        ctx.Check(posed > 0,
            $"and it is posed off the archive's own build origin, {lastAt.DistanceTo(startAt):0.#} m moved");
        ctx.Check(framed > frames / 2,
            $"drawn and inside the cutscene camera's frustum through most of the shot (best {bestAngle:0.#} deg off axis)");
        ctx.Check(endsOffZeppelin,
            $"and the drop's own OBJECT_DELETE_CHILD hands it back off '{DropZeppelin}' once the shot ends");
    }

    // The angle between the camera's forward axis and the ray to the point, 180 for a point
    // straight behind. Godot's camera looks down its local -Z.
    private static float AngleOffAxis(Node3D camera, Vector3 worldPoint)
    {
        var local = camera.GlobalTransform.AffineInverse() * worldPoint;
        if (local.Length() < 1e-3f)
        {
            return 0f;
        }

        return Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(-local.Normalized().Z, -1f, 1f)));
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }
}
