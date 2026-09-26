using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over where a mid-mission cutscene leaves the pilot: the definition parks its
/// <c>player</c> node at an authored world pose and raises the re-placement callback, and the
/// handoff has to fly the aeroplane out of THAT pose rather than out of the one it was staged from.
/// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class DropoffPlacementSuites
{
    // The story position driven: C3/M01, whose tex_drop.zrd is the campaign's first re-placing
    // cutscene (the same position IntroAircraftSuites and DropoffChutemanSuites drive).
    private const int MissionSeq = 0;

    // The callback the definition re-places the pilot with. Its own decode lives on the const in
    // CutsceneController; the suite names it to find the authored definition by data rather than
    // by a hard-coded animation name.
    private const int ReplaceCode = 951;

    private const string PlaneNode = "player_bhawk";
    private const float StepDt = 1f / 60f;

    // The drop's own scripts run 4.4 s and its camera 4.45 s; the budget clears both several times
    // over, and the loop leaves as soon as the episode hands off.
    private const float DriveBudgetS = 30f;

    // How closely the released aeroplane has to sit on the authored placement.
    private const float PlacedToleranceM = 1f;

    // How far the placement has to be from where the pilot flew in, or the assertion above would
    // pass on a build that re-places nothing.
    private const float MovedMinM = 100f;

    // C2/M05, the story position whose paratrooper drop raises the same re-placement callback while
    // naming no `player` node at all, so nothing in it ever poses the node the callback reads.
    private const int UnposedMissionSeq = 14;

    // The zeppelin that drop is staged around. Its authored pose is where the pilot is flying when
    // the drop fires, so it is where the episode has to leave them.
    private const string DropZeppelin = "cargozep2";

    // How high above the zeppelin the pilot is flown in, and how far the handoff may then leave the
    // aeroplane from there.
    private const float FlownInAboveM = 120f;
    private const float HeldToleranceM = 2f;

    // The surface probe: cast from this far above a point to this far below it. The span covers the
    // chapter's relief several times over, so "no hit" means off the map rather than out of reach.
    private const float CastUpM = 2000f;
    private const float CastDownM = 8000f;

    // The clearance the released aeroplane needs over whatever surface is under it.
    private const float ClearanceMinM = 10f;

    /// <summary>Drives C3/M01's drop-off definition directly (never the approach cone, so nothing
    /// INSTR-20 warns about is armed) against its BUILT world, with the pilot flown in at the world
    /// origin: the definition's own re-placement callback puts the aeroplane on the pose its
    /// <c>player</c> node was parked at, facing the way that node faces, at the original's release
    /// speed.</summary>
    // BL-553: the drop-off's handoff put the pilot back where the cutscene found them, so the
    // pose its own definition parks the 'player' node at, and the callback that reads it, both
    // went nowhere.
    [Suite("dropoff-placement",
        "where a mid-mission cutscene leaves the pilot, over C3/M01's BUILT world: the mission's "
        + "own cutscenes directory carries the definition raising the re-placement callback, and "
        + "driving it directly (never the approach cone) flies the aeroplane out of the world "
        + "pose that definition parked its 'player' node at, on that node's heading and at the "
        + "original's release speed, rather than out of where the pilot flew in")]
    internal static void DropoffPlacement(TestContext ctx)
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
            ctx.WithWorld(chapter, collision: false, folder,
                world => Drive(ctx, world, missionZrdr, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-dropoff-placement-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s re-placing cutscene over a built world");
    }

    /// <summary>Drives C2/M05's paratrooper drop against its BUILT world with the pilot flown in
    /// beside the zeppelin it is staged around. That definition raises the re-placement callback
    /// without naming the <c>player</c> node, so there is no authored placement to read: the
    /// handoff has to leave the aeroplane where the drop found it, above the surface under it.
    /// </summary>
    // The re-placement callback reads the marker wherever the build parked it, so a
    // definition that raises it without posing that node put the pilot on the world root.
    [Suite("cutscene-handoff-unposed",
        "where a mid-mission cutscene leaves the pilot when it authors no placement, over "
        + "C2/M05's BUILT world: its paratrooper drop raises the same re-placement callback "
        + "while naming no 'player' node at all, and the handoff leaves the aeroplane at the "
        + "pose the drop found it at, above the surface measured under that pose, rather than "
        + "on the world root the marker is parked at")]
    internal static void CutsceneHandoffUnposed(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), UnposedMissionSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {UnposedMissionSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");

        var report = new StringBuilder();
        report.AppendLine($"seq {UnposedMissionSeq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: true, folder,
                world => DriveUnposed(ctx, world, missionZrdr, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cutscene-handoff-unposed-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s unposed re-placement over a built world");
    }

    private static void Drive(TestContext ctx, TestWorld world, string missionZrdr, StringBuilder report)
    {
        var cutscenes = MissionCutscenes.AnimNames(missionZrdr);
        report.AppendLine($"cutscene definitions: {string.Join(", ", cutscenes)}");
        string? anim = ReplacingAnimOf(world, cutscenes);
        report.AppendLine($"re-placing definition: '{anim ?? "-"}' (callback {ReplaceCode})");
        ctx.Check(anim != null,
            $"the mission's own cutscenes directory carries a definition raising callback {ReplaceCode}");
        if (anim == null)
        {
            return;
        }

        if (world.Session.Aircraft is not { PlayerMarker: { } marker })
        {
            ctx.Check(false, $"the world build staged the '{AircraftStage.PlayerNode}' marker the drop poses");
            return;
        }

        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        try
        {
            var rig = BuildRig(ctx, world, pool);
            cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
            cutscene.BindRigs(
                new[]
                {
                    new PlayerRig
                    {
                        Index = 0,
                        Camera = ctx.Camera,
                        HudParent = ctx.Host,
                        Controller = rig,
                    },
                },
                () => Array.Empty<FlightController>());
            cutscene.HostDefinitions(new[] { anim });
            world.Runtime.CallbackHost = cutscene.Host;
            RunTheDrop(ctx, world, cutscene, rig, marker, anim, report);
        }
        finally
        {
            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    private static void DriveUnposed(TestContext ctx, TestWorld world, string missionZrdr,
        StringBuilder report)
    {
        var cutscenes = MissionCutscenes.AnimNames(missionZrdr);
        report.AppendLine($"cutscene definitions: {string.Join(", ", cutscenes)}");
        string? anim = ReplacingAnimOf(world, cutscenes);
        report.AppendLine($"re-placing definition: '{anim ?? "-"}' (callback {ReplaceCode})");
        ctx.Check(anim != null,
            $"the mission's own cutscenes directory carries a definition raising callback {ReplaceCode}");
        if (anim == null)
        {
            return;
        }

        var named = NodesNamedBy(world, anim);
        report.AppendLine($"'{anim}' names: {string.Join(", ", named)}");
        ctx.Check(!named.Contains(AircraftStage.PlayerNode),
            $"'{anim}' raises it while naming no '{AircraftStage.PlayerNode}' node, so this drive is the case where nothing posed the node the callback reads");

        if (world.Session.Aircraft is not { PlayerMarker: { } marker })
        {
            ctx.Check(false, $"the world build staged the '{AircraftStage.PlayerNode}' marker the drop reads");
            return;
        }

        var zeppelin = Zeppelins.Load(missionZrdr).FirstOrDefault(z => z.Node == DropZeppelin);
        if (zeppelin == null)
        {
            ctx.Check(false, $"the mission's zeppelins carry '{DropZeppelin}', the drop's own subject");
            return;
        }

        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        try
        {
            var rig = BuildRig(ctx, world, pool);
            var flyAt = zeppelin.Position + (Vector3.Up * FlownInAboveM);
            rig.PlaceHeld(flyAt, flyAt + Vector3.Forward);
            cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
            cutscene.BindRigs(
                new[]
                {
                    new PlayerRig
                    {
                        Index = 0,
                        Camera = ctx.Camera,
                        HudParent = ctx.Host,
                        Controller = rig,
                    },
                },
                () => Array.Empty<FlightController>());
            cutscene.HostDefinitions(new[] { anim });
            world.Runtime.CallbackHost = cutscene.Host;
            RunTheUnposedDrop(ctx, world, cutscene, rig, marker, anim, report);
        }
        finally
        {
            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The drive. The rig is never stepped, so wherever it ends up is the episode's doing; the two
    // poses the item is about are the one the drop found the pilot at and the one they are released
    // at, with the surface under each measured rather than assumed.
    private static void RunTheUnposedDrop(TestContext ctx, TestWorld world, CutsceneController cutscene,
        FlightController rig, Node3D marker, string anim, StringBuilder report)
    {
        var flewInAt = rig.WorldPosition;
        float? underFlewIn = SurfaceUnder(world, flewInAt);
        Transform3D? read = null;
        float played = 0f;
        world.Runtime.Play(anim);
        for (float t = 0f; t < DriveBudgetS && (played == 0f || cutscene.Playing); t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            played += cutscene.Playing ? StepDt : 0f;
            if (read == null && cutscene.Codes.Contains(ReplaceCode))
            {
                read = AnimRuntime.WorldTransform(marker, out _);
            }
        }

        var released = rig.WorldPosition;
        float? underReleased = SurfaceUnder(world, released);
        float moved = released.DistanceTo(flewInAt);
        report.AppendLine($"'{anim}' episode ran {played:0.##} s, codes {string.Join("/", cutscene.Codes)}");
        report.AppendLine($"flew in at {flewInAt}, surface under it {Height(underFlewIn)}");
        report.AppendLine($"'{AircraftStage.PlayerNode}' marker at the callback: "
            + $"{(read is { } r ? r.Origin.ToString() : "-")}");
        report.AppendLine($"released at {released}, surface under it {Height(underReleased)}, "
            + $"{moved:0.#} m from where the drop found the pilot");
        ctx.Check(played > 0f && !cutscene.Playing,
            $"'{anim}' runs to its handoff with the cutscene host answering its codes");
        ctx.Check(read != null, $"and raises callback {ReplaceCode} on the way");
        if (read == null)
        {
            return;
        }

        ctx.Check(moved < HeldToleranceM,
            $"the handoff leaves the aeroplane where the drop found it ({moved:0.#} m away), since the definition posed no '{AircraftStage.PlayerNode}' node to re-place it on");
        ctx.Check(underReleased is { } surface && released.Y - surface > ClearanceMinM,
            $"and above the surface under it, rather than through the single-sided terrain the pilot cannot climb back out of");
    }

    // The surface under a point, cast from well above it. Null when nothing is there at all, which
    // is a reading rather than a gap: the world root the marker is parked at is off the chapter map.
    private static float? SurfaceUnder(TestWorld world, Vector3 at)
    {
        var space = world.Stage.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            at + (Vector3.Up * CastUpM), at - (Vector3.Up * CastDownM), CollisionLayers.World));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : null;
    }

    private static string Height(float? y) => y is { } h ? $"{h:0.#}" : "(nothing)";

    // Every node name a definition's own support arrays carry, which is what says whether it can
    // pose a node at all.
    private static HashSet<string> NodesNamedBy(TestWorld world, string anim)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in world.Session.Program.ByAnimName(anim))
        {
            foreach (string node in def.NodeList)
            {
                named.Add(node);
            }
        }

        return named;
    }

    // The drive itself. The rig is never stepped, so wherever it ends up is the cutscene's doing and
    // nothing else's; the placement is read off the marker on the frame the callback lands, which is
    // the pose the original's own host reads out of that node.
    private static void RunTheDrop(TestContext ctx, TestWorld world, CutsceneController cutscene,
        FlightController rig, Node3D marker, string anim, StringBuilder report)
    {
        var flewInAt = rig.GlobalTransform;
        Transform3D? placement = null;
        float played = 0f;
        world.Runtime.Play(anim);
        for (float t = 0f; t < DriveBudgetS && (played == 0f || cutscene.Playing); t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            played += cutscene.Playing ? StepDt : 0f;
            if (placement == null && cutscene.Codes.Contains(ReplaceCode))
            {
                placement = AnimRuntime.WorldTransform(marker, out _);
            }
        }

        report.AppendLine($"'{anim}' episode ran {played:0.##} s, codes {string.Join("/", cutscene.Codes)}");
        ctx.Check(played > 0f && !cutscene.Playing,
            $"'{anim}' runs to its handoff with the cutscene host answering its codes");
        ctx.Check(placement != null, $"and raises callback {ReplaceCode} on the way");
        if (placement is not { } placed)
        {
            return;
        }

        float moved = placed.Origin.DistanceTo(flewInAt.Origin);
        float off = rig.WorldPosition.DistanceTo(placed.Origin);
        float facing = (-rig.Attitude.Z).Dot(-placed.Basis.Orthonormalized().Z);
        report.AppendLine($"flew in at {flewInAt.Origin}, authored placement {placed.Origin}, "
            + $"{moved:0.#} m apart");
        report.AppendLine($"released at {rig.WorldPosition}, {off:0.##} m off the placement, "
            + $"facing dot {facing:0.###}, speed {rig.WorldVelocity.Length():0.##} m/s");
        ctx.Check(moved > MovedMinM,
            $"the authored placement is {moved:0} m from where the pilot flew in, so this can fail");
        ctx.Check(off < PlacedToleranceM,
            $"the handoff flies the aeroplane out of the pose the drop parked '{AircraftStage.PlayerNode}' at");
        ctx.Check(facing > 0.999f,
            $"facing the way that node faces, whatever heading the pilot flew in on");
        ctx.Check(rig.WorldVelocity.Length() > 1f,
            $"and moving, the release the original's own callback writes into the vehicle");
    }

    // The mission's own cutscene definition that raises the re-placement callback. Read out of the
    // authored events rather than named, so the suite says WHERE the placement is authored.
    private static string? ReplacingAnimOf(TestWorld world, IReadOnlyList<string> cutscenes)
    {
        foreach (string name in cutscenes)
        {
            foreach (var def in world.Session.Program.ByAnimName(name))
            {
                foreach (var seq in def.Sequences)
                {
                    foreach (var ev in seq.Events)
                    {
                        if (ev.Kind == "Callback" && (int)(ev.Data.Num("value") ?? -1f) == ReplaceCode)
                        {
                            return name;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static FlightController BuildRig(TestContext ctx, TestWorld world, ProjectilePool pool)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        try
        {
            var model = new PlaneBuilder(GameZ.Load(ctx.PlanesGamezPath), textures).Build(PlaneNode);
            var rig = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = true,
                Projectiles = pool,
                UseKeyboard = false,
                PadDevices = Array.Empty<int>(),
                AllowPause = false,
                Team = AimAssist.PlayerTeam,
                Name = "DropoffPlacementPlayer",
            };
            rig.AddChild(model);
            ctx.Host.AddChild(rig);
            rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null, new CamParams(),
                Vector3.Zero, Vector3.Forward, 0f, 0f);
            return rig;
        }
        finally
        {
            textures.Dispose();
        }
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
