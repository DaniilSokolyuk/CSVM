using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// C4/M03's Blacke drop, the mission's mid-air pickup cutscene. Two camera definitions fork on
/// <c>blk_e_marker</c>'s active state the way CM07's hangar drop forks its pair, and the marker
/// that carries both the camera and the dropped pilot is re-posed by a second definition polling
/// PLAYER_RANGE against the player. The suite flies each approach side in over the mission's own
/// BUILT world and reads which camera takes the shot, whether the stage holds still under it, and
/// where the hand-back leaves the pilot.
/// </summary>
internal static class CampaignBlackeDropSuites
{
    private const string Chapter = "C4";

    private const string Mission = "M03";

    private const string EastCam = "bdrop_ew_cam";

    private const string WestCam = "bdrop_we_cam";

    private const string DropMarker = "blacke_marker";

    private const string EastMarker = "blk_e_marker";

    private const float StepDt = 1f / 60f;

    // The drop's own scripts run about 4.5 s; the budget clears the approach and the shot together.
    private const float BudgetS = 30f;

    // Where the approach starts, well outside both 64 m gates (a compiled PLAYER_RANGE 4096 is
    // metres squared), and the speed it is flown in at.
    private const float EntryM = 400f;

    private const float FlySpeed = 60f;

    private const float EntryClimbM = 90f;

    private const float OverMarkerM = 30f;

    // How far the stage the camera hangs off may turn while the shot plays. The two poses the
    // approach fork writes are half a turn apart, so anything real is far above this.
    private const float StageTurnMaxDeg = 1f;

    private const float ClearanceMinM = 5f;

    // Two steps at the release speed cover under 2 m; a sweep from the trigger moves it 200 m.
    private const float FlownOnMaxM = 10f;

    private const float CastUpM = 2000f;

    private const float CastDownM = 8000f;

    [Suite("blacke-drop-cameras",
        "C4/M03's Blacke drop flown in from each side over the mission's own BUILT world: exactly "
        + "one of the two camera definitions takes the shot and it is the one the approach side's "
        + "marker state selects, the stage both the camera and the dropped pilot hang off holds "
        + "the pose it was given for the whole shot rather than being re-posed by the approach "
        + "fork still polling the player it is itself carrying, the hand-back leaves the "
        + "pilot above the surface under him, and the first flown steps carry on from that pose "
        + "rather than sweeping from where the trigger found him back into the drop site")]
    internal static void BlackeDropCameras(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");
        var report = new StringBuilder();
        ctx.CutsceneRoots = true;
        try
        {
            foreach (bool fromEast in new[] { true, false })
            {
                bool east = fromEast;
                ctx.WithWorld(Chapter, collision: true, Mission, world => Drive(ctx, world, east, report));
            }
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-blacke-drop-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the drop runs one camera per side over a stage that holds still");
    }

    private static void Drive(TestContext ctx, TestWorld world, bool fromEast, StringBuilder report)
    {
        var rt = world.Runtime;
        string side = fromEast ? "east" : "west";
        report.AppendLine($"=== approach from the {side}");
        if (world.Session.Aircraft is not { PlayerMarker: not null })
        {
            ctx.Check(false, $"the world build staged the '{AircraftStage.PlayerNode}' marker the drop poses");
            return;
        }

        var stage = First(rt.FindNodes(DropMarker));
        var eastMarker = First(rt.FindNodes(EastMarker));
        if (stage == null || eastMarker == null)
        {
            ctx.Check(false, $"the drop's '{DropMarker}' and '{EastMarker}' are both in the built world");
            return;
        }

        var dropAt = AnimRuntime.WorldTransform(stage, out _).Origin;
        var eastAt = AnimRuntime.WorldTransform(eastMarker, out _).Origin;
        var toEast = (eastAt - dropAt).Normalized();
        // Flown down a slope from above the ridges either entry sits behind to 30 m over the
        // marker: inside the drop's 64 m trigger, and inside the east fork's own 64 m gate on
        // its marker for the approach that should take that leg, clear of it for the other.
        var axis = new Vector3(toEast.X, 0f, toEast.Z).Normalized();
        var entry = dropAt + (axis * (fromEast ? EntryM : -EntryM)) + (Vector3.Up * EntryClimbM);
        var heading = (dropAt + (Vector3.Up * OverMarkerM) - entry).Normalized();
        var profile = new StringBuilder();
        for (float d = 0f; d <= EntryM; d += 50f)
        {
            var at = entry + (heading * d);
            float? under = SurfaceUnder(world, at);
            profile.Append(under is { } h ? $" {h:0}" : " ?");
        }

        report.AppendLine($"surface along the approach every 50 m:{profile}");
        report.AppendLine($"'{DropMarker}' at {dropAt}, '{EastMarker}' {dropAt.DistanceTo(eastAt):0.#} m away; "
            + $"entering at {entry}, flying in at {FlySpeed:0} m/s");

        var savedClock = GameClock.Current;
        var savedHost = rt.CallbackHost;
        var savedStarted = rt.OnInstanceStarted;
        var savedPositions = rt.PlayerPositions;
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        FlightController? rig = null;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.FixedStep };
            GameClock.Current = clock;
            rig = HumanRig(ctx, textures, pool, entry, dropAt);
            cutscene.BindWorld(rt, world.Session.Aircraft);
            // The drop is one of the mission's own `cutscenes\` definitions, so the session's own
            // registration is what hosts its codes; without it the host declines every one.
            cutscene.HostDefinitions(world.Session.LandingCutsceneAnims);
            cutscene.BindRigs(
                new[]
                {
                    new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = ctx.Host, Controller = rig },
                },
                () => Array.Empty<FlightController>());
            cutscene.WorldHeld = held => clock.SimHeld = held;
            rt.CallbackHost = cutscene.Host;
            var flown = rig;
            rt.PlayerPositions = () => new[] { flown.GlobalPosition };
            Fly(ctx, world, cutscene, rig, entry, heading, stage, eastMarker, fromEast, report);
        }
        finally
        {
            rt.PlayerPositions = savedPositions;
            rt.OnInstanceStarted = savedStarted;
            rt.CallbackHost = savedHost;
            GameClock.Current = savedClock;
            cutscene.Free();
            rig?.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    private static void Fly(TestContext ctx, TestWorld world, CutsceneController cutscene,
        FlightController rig, Vector3 entry, Vector3 heading, Node3D stage, Node3D eastMarker,
        bool fromEast, StringBuilder report)
    {
        var rt = world.Runtime;
        var started = new List<string>();
        rt.OnInstanceStarted = (def, _) =>
        {
            if (def.AnimName == EastCam || def.AnimName == WestCam)
            {
                started.Add(def.AnimName);
            }
        };
        float shotFor = 0f;
        float stageTurnDeg = 0f;
        float? stageYawAtOpen = null;
        bool everPlayed = false;
        // The roster yaw convention WarpTo takes (CampaignRoster.Forward), solved for the axis.
        float headingDeg = Mathf.RadToDeg(Mathf.Atan2(-heading.X, -heading.Z));
        for (float t = 0f; t < BudgetS; t += StepDt)
        {
            if (!cutscene.Playing)
            {
                // Placed along the axis so the trigger is reached at a known time on both sides.
                // Each placement also flies two steps: a pilot reaching the trigger has a sweep in
                // progress with a carried origin, and the hand-back is read against that state.
                rig.WarpTo(entry + (heading * (FlySpeed * t)), headingDeg, FlySpeed);
                rig.SimStep(StepDt);
                rig.SimStep(StepDt);
            }

            rt.Advance(StepDt);
            cutscene.Tick();
            if (!cutscene.Playing)
            {
                if (everPlayed)
                {
                    break;
                }

                continue;
            }

            everPlayed = true;
            shotFor += StepDt;
            float yaw = AnimRuntime.WorldTransform(stage, out _).Basis.GetEuler().Y;
            stageYawAtOpen ??= yaw;
            stageTurnDeg = Mathf.Max(stageTurnDeg,
                Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(stageYawAtOpen.Value, yaw))));
        }

        var handback = rig.GlobalPosition;
        float? surface = SurfaceUnder(world, handback);
        // The first flown steps out of the hand-back: the aeroplane must fly on from the pose
        // the re-placement wrote, not be swept back to wherever the trigger found it.
        float hullBefore = rig.Damage?.WholeHealth ?? -1f;
        rig.SimStep(StepDt);
        rig.SimStep(StepDt);
        float flownOn = handback.DistanceTo(rig.GlobalPosition);
        float hullAfter = rig.Damage?.WholeHealth ?? -1f;
        report.AppendLine($"two flown steps after the hand-back move the pilot {flownOn:0.#} m, hull "
            + $"{hullBefore:0.#} -> {hullAfter:0.#}, crashed={rig.Crashed}");
        report.AppendLine($"the shot ran {shotFor:0.##} s, camera definitions started: "
            + $"[{string.Join(", ", started)}], '{EastMarker}' active={eastMarker.Visible} at the end");
        report.AppendLine($"the stage turned at most {stageTurnDeg:0.##} deg while the shot played");
        report.AppendLine($"hand-back at {handback}, surface under it "
            + $"{(surface is { } s ? $"{s:0.#}" : "(nothing)")}");
        string want = fromEast ? EastCam : WestCam;
        string side = fromEast ? "east" : "west";
        string oneCamera = $"exactly one camera definition starts and it is '{want}', the leg this "
            + $"side's '{EastMarker}' state selects";
        string stageHolds = $"'{DropMarker}', which the camera and the dropped pilot both hang off, "
            + $"holds its pose for the whole shot";
        ctx.Check(everPlayed && shotFor > 1f,
            $"the drop takes the session on the approach from the {side} and runs its shot");
        ctx.Check(started.Count == 1 && started[0] == want, $"{oneCamera}");
        ctx.Check(stageTurnDeg < StageTurnMaxDeg, $"{stageHolds}");
        ctx.Check(surface is { } under && handback.Y - under > ClearanceMinM,
            $"and the hand-back leaves the pilot above the surface measured under him");
        string fliesOn = "and the first flown steps carry on from that pose with no contact, rather "
            + "than sweeping from where the trigger found the pilot back at the drop site";
        ctx.Check(flownOn < FlownOnMaxM && !rig.Crashed && hullAfter >= hullBefore,
            $"{fliesOn} ({flownOn:0.#} m)");
    }

    private static float? SurfaceUnder(TestWorld world, Vector3 at)
    {
        var space = world.Stage.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            at + (Vector3.Up * CastUpM), at - (Vector3.Up * CastDownM), CollisionLayers.World));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : null;
    }

    // The human the drop takes: a real rig, because the cutscene stages the episode owner's
    // aeroplane and the hand-back pose is read off it.
    private static FlightController HumanRig(TestContext ctx, TextureArchive textures,
        ProjectilePool live, Vector3 pos, Vector3 lookAt)
    {
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            PlayerIndex = FlightRoster.ShooterIdBase - 1,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
            Name = "BlackeDropPlayer",
        };
        rig.AddChild(model);
        ctx.Host.AddChild(rig);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, lookAt);
        return rig;
    }

    private static Node3D? First(IReadOnlyList<Node3D> nodes) => nodes.Count > 0 ? nodes[0] : null;
}
