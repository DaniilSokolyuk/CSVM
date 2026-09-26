using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>C2/M01's ace against the terrain it is authored inside, over the mission's own built
/// world. The roster puts <c>hkfirebrand_9</c> below the surface, which costs nothing once the
/// collider honours the polygon's own <c>SHOW_BACKFACE</c>: every face of the hill around it faces
/// outward, so a body inside reaches all of them from behind and flies out. The far-field branch
/// computes no gravity, so it also never sinks to the under-map backstop that used to teleport it
/// back to its spawn. Both plants are flown, because a near-field rig at the same pose is handed
/// gravity the ace never gets and reads as a different defect.</summary>
internal static class AceWakeTerrainSuites
{
    private const string Chapter = "C2";
    private const string Mission = "M01";

    // The roster block the mission's OBJECTIVE67 wakes, and the human whose range decides the plant.
    private const string AceBlock = "hkfirebrand_9";
    private const string PlayerBlock = "player";

    // How far the measurement flies the ace on each leg, in sim seconds at the fixed step.
    private const float StepDt = 1f / 60f;
    private const int LegSeconds = 40;

    // How far the pilot-free level track is walked, and at what spacing. 8 km crosses the whole
    // hill group the wake sits in, so a track that meets nothing over it meets nothing at all.
    private const float LevelTrackM = 8000f;
    private const float LevelTrackStepM = 10f;

    [Suite("ace-wake-terrain",
        "CM12's ace pose against C2/M01's own built terrain: the roster authors hkfirebrand_9 " +
        "78.9 m under a surface at 228.92 m, the sheet has no underside so nothing lies below " +
        "that pose, and the surface over it answers nothing from below because every polygon of " +
        "g35052 clears SHOW_BACKFACE. A level track along its authored nose meets no collider in " +
        "8 km, and flown on either plant the ace leaves the hill with no contact, no crash and no " +
        "descent to the under-map backstop")]
    internal static void AceWakeTerrain(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var acePose = PoseOf(blocks, AceBlock);
        var playerPose = PoseOf(blocks, PlayerBlock);
        ctx.Check(acePose != null && playerPose != null,
            $"the mission roster authors both {AceBlock} and {PlayerBlock}");
        if (acePose is not { } ace || playerPose is not { } player)
            return;

        // The horizontal separation the wake starts at, which is what selects the branch.
        float wakeRangeM = Mathf.Sqrt(((ace.Pos.X - player.Pos.X) * (ace.Pos.X - player.Pos.X))
                                      + ((ace.Pos.Z - player.Pos.Z) * (ace.Pos.Z - player.Pos.Z)));
        ctx.Check(wakeRangeM > 1000f,
            $"the ace wakes outside the far-field boundary range={wakeRangeM:0} m from the player's own spawn");

        var report = new StringBuilder();
        report.AppendLine($"{AceBlock} authored at ({ace.Pos.X:0.00}, {ace.Pos.Y:0.00}, {ace.Pos.Z:0.00}) yaw {ace.YawDeg:0}");
        report.AppendLine($"{PlayerBlock} authored at ({player.Pos.X:0}, {player.Pos.Y:0}, {player.Pos.Z:0}); wake range {wakeRangeM:0} m");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        string plane = ctx.PlaneName;
        if (defs.DefForBlock(AceBlock) is { } aceDef && defs.AirframeFor(aceDef) is { } airframe)
            plane = airframe.PlaneNode;
        report.AppendLine($"airframe {plane}");

        ctx.WithWorld(Chapter, collision: true, Mission, world =>
            Drive(ctx, planesGamez, texturesPath, plane, ace, player, wakeRangeM, report));

        ctx.WriteArtifact($"test-ace-wake-terrain-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the ace is authored under the terrain sheet and flies out of it, the surface over it being solid from above alone");
    }

    private static void Drive(TestContext ctx, GameZ planesGamez, string texturesPath, string plane,
        (Vector3 Pos, float YawDeg) ace, (Vector3 Pos, float YawDeg) player, float wakeRangeM,
        StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, plane);
        var humans = new List<Vector3> { player.Pos };
        FlightController? far = null;
        FlightController? near = null;
        try
        {
            // The surface over the authored pose, read through the built colliders rather than the
            // source mesh: a ray from well above, straight down past the spawn altitude.
            var space = ctx.Host.GetWorld3D().DirectSpaceState;
            float surfaceY = float.NaN;
            string surfaceInto = "nothing";
            var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                ace.Pos with { Y = 2000f }, ace.Pos with { Y = -500f }, CollisionLayers.World));
            if (down.Count > 0)
            {
                surfaceY = down["position"].AsVector3().Y;
                surfaceInto = down["collider"].Obj is Node body
                    ? $"{body.GetParent()?.Name}/{body.Name}" : "?";
            }

            report.AppendLine($"terrain surface over the pose: y={surfaceY:0.00} into {surfaceInto}");
            ctx.Check(surfaceY > ace.Pos.Y,
                $"the ace's authored altitude is UNDER the terrain surface there spawn={ace.Pos.Y:0} surface={surfaceY:0.00} m into {surfaceInto}");
            ctx.Check(surfaceInto.EndsWith("/col", StringComparison.Ordinal),
                $"the surface over the pose is a built world collider {surfaceInto}");

            // Under-the-sheet test, used every leg. ⚠ Read DOWN from above, never up from below:
            // terrain is one-sided now, so an upward ray answers nothing whether or not there is a
            // surface overhead, and would report every pose as open air.
            bool UnderSheet(Vector3 at)
            {
                var column = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    at with { Y = 4000f }, at with { Y = -4000f }, CollisionLayers.World));
                return column.Count > 0 && column["position"].AsVector3().Y > at.Y;
            }

            // The item's own contact: the ace is under the surface, and the surface above it answers
            // nothing from behind, which is the test the original runs and CSVM used to fail.
            var fromBelow = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                ace.Pos, ace.Pos with { Y = surfaceY + 50f }, CollisionLayers.World));
            ctx.Same(0, fromBelow.Count,
                $"the surface over the authored pose answers nothing from below (all of g35052 clears SHOW_BACKFACE)");
            ctx.Check(UnderSheet(ace.Pos),
                $"the authored pose sits under the terrain sheet: the column over it answers above the pose");
            ctx.Check(!UnderSheet(ace.Pos with { Y = surfaceY + 50f }),
                $"the control 50 m above that surface is in open air");

            // Terrain is a SHEET, not a closed volume. Below it there is no underside to meet, so an
            // aircraft under the surface is in free space with ground overhead, which is why nothing
            // registers while it stays there and why a near-field one descends without contact.
            var beneath = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                ace.Pos, ace.Pos with { Y = -2000f }, CollisionLayers.World));
            ctx.Check(beneath.Count == 0,
                $"nothing lies beneath the authored pose: the terrain sheet has no underside, so being below it is open space");

            // One AI rig at the ace's authored pose and nose, flying its spawn course with no
            // orders: the plant is what the leg measures, so nothing else may steer it.
            (FlightController Rig, FlightModel Plant) BuildAce(int index)
            {
                var nose = new Basis(Vector3.Up, Mathf.DegToRad(ace.YawDeg)) * Vector3.Forward;
                var model = new PlaneBuilder(planesGamez, textures).Build(plane);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(stats.DestroyableParts),
                    PlayerIndex = FlightRoster.ShooterIdBase + index,
                    IsHumanPiloted = false,
                    Pilot = AiPilot.HoldingCourse(ace.Pos, ace.Pos + nose),
                    UseKeyboard = false,
                    PadDevices = Array.Empty<int>(),
                    AllowPause = false,
                    HumanPositions = () => humans,
                };
                rig.AddChild(model);
                var plant = new FlightModel(stats, aiForcePath: true);
                rig.Setup(plant, null, new CamParams(), ace.Pos, ace.Pos + nose);
                ctx.Host.AddChild(rig);
                return (rig, plant);
            }

            // The rig is built first so the level track below can be walked at the speed the
            // far-field plant actually holds this airframe at, rather than a guessed one.
            var (farRig, farPlant) = BuildAce(0);
            far = farRig;
            int grazes = 0;
            farRig.GrazeEffectSink = (_, _) => grazes++;
            farRig.SimStep(StepDt);
            ctx.Check(farPlant.FarFieldPlant,
                $"the ace wakes on the far-field plant with the player at its own spawn");

            // --- The geometry, with no pilot in it: a level track along the authored nose at the
            // far-field hold speed. This is where the ace would leave the sheet if it flew the
            // course it wakes on, and it answers the timing question without the control law.
            var noseDir = new Basis(Vector3.Up, Mathf.DegToRad(ace.YawDeg)) * Vector3.Forward;
            float holdSpeed = (stats.FdSpeed * farPlant.Throttle) + 5f;
            float clearedAtM = -1f;
            float crossedAtM = -1f;
            var walk = ace.Pos;
            for (float d = 0f; d <= LevelTrackM; d += LevelTrackStepM)
            {
                var next = ace.Pos + (noseDir * d);
                if (d > 0f && crossedAtM < 0f)
                {
                    var seg = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        walk, next, CollisionLayers.World));
                    if (seg.Count > 0)
                        crossedAtM = d;
                }

                if (clearedAtM < 0f && !UnderSheet(next))
                    clearedAtM = d;
                walk = next;
                if (clearedAtM >= 0f && crossedAtM >= 0f)
                    break;
            }

            report.AppendLine($"level track along the authored nose at {holdSpeed:0.0} m/s: "
                + $"first clear of the sheet at {clearedAtM:0} m, first crossing at {crossedAtM:0} m");
            // The crossing on the way out is what used to end the ace, and the original culls it.
            // Nothing along the whole track may answer now: a hill's faces all face outward, so a
            // body inside one reaches every one of them from behind.
            ctx.Check(crossedAtM < 0f,
                $"the level track along the authored nose meets no collider over {LevelTrackM:0} m crossed_at={crossedAtM:0} m");
            ctx.Check(clearedAtM >= 0f,
                $"and it is out from under the sheet within that track clear={clearedAtM:0} m");
            report.AppendLine($"a mover at {holdSpeed:0.0} m/s needs {(wakeRangeM - 1000f) / holdSpeed:0.0} s "
                + $"to close {wakeRangeM - 1000f:0} m from the wake range to the far-field boundary");

            // --- The flown leg: the same wake on the real plant and the real control law, which is
            // what decides whether the level track above is the track it actually flies.
            int leftAt = -1;
            float minY = ace.Pos.Y;
            float maxRangeM = 0f;
            int farSteps = LegSeconds * 60;
            for (int i = 0; i < farSteps && !farRig.Crashed; i++)
            {
                farRig.SimStep(StepDt);
                var p = farRig.WorldPosition;
                if (leftAt < 0 && !UnderSheet(p))
                    leftAt = i;
                minY = Mathf.Min(minY, p.Y);
                maxRangeM = Mathf.Max(maxRangeM, new Vector2(p.X - ace.Pos.X, p.Z - ace.Pos.Z).Length());
                if (i % 300 == 0 || i == farSteps - 1)
                {
                    report.AppendLine($"far t={i / 60f:0.0}s pos=({p.X:0},{p.Y:0},{p.Z:0}) "
                        + $"plant={(farPlant.FarFieldPlant ? "far" : "near")} spd={farPlant.Speed:0.0} "
                        + $"under={UnderSheet(p)} grazes={grazes} crashed={farRig.Crashed}");
                }
            }

            var end = farRig.WorldPosition;
            report.AppendLine($"far leg: first clear at t={(leftAt >= 0 ? leftAt / 60f : -1f):0.0} s, "
                + $"end ({end.X:0},{end.Y:0},{end.Z:0}), min y {minY:0}, "
                + $"furthest {maxRangeM:0} m from the wake, grazes {grazes}, crashed {farRig.Crashed}");
            ctx.Check(farPlant.FarFieldPlant && !farRig.Crashed,
                $"nothing ends the far-field ace over {LegSeconds} s under the sheet crashed={farRig.Crashed} grazes={grazes}");
            ctx.Same(0, grazes,
                $"the ace flies out of the hill with no contact at all over {LegSeconds} s");
            ctx.Check(leftAt >= 0,
                $"and comes out from under the surface rather than being held under it clear_at={(leftAt >= 0 ? leftAt / 60f : -1f):0.0} s");
            // FlightController's under-map backstop respawns anything below y = 0, which is what
            // the repeated teleport to the spawn point was before the cull landed.
            ctx.Check(minY > 0f,
                $"and never sinks to the under-map backstop that was teleporting it back min_y={minY:0} m");

            // ⚠ The far rig stays in the world and the sweep masks aircraft as well as world, so a
            // second rig built on the same pose would ram it. Retire it before the control leg.
            far = null;
            farRig.Free();

            // --- The control: the same pose with a human inside the boundary, which is the plant
            // the ace only reaches once somebody closes on it.
            humans[0] = ace.Pos with { Y = ace.Pos.Y + 100f };
            var (nearRig, nearPlant) = BuildAce(1);
            near = nearRig;
            int nearGrazes = 0;
            nearRig.GrazeEffectSink = (_, _) => nearGrazes++;
            nearRig.SimStep(StepDt);
            ctx.Check(!nearPlant.FarFieldPlant,
                $"a human inside the boundary puts the same pose on the near-field plant");

            float nearMinY = ace.Pos.Y;
            int nearSteps = LegSeconds * 60;
            int endedAt = -1;
            for (int i = 0; i < nearSteps && !nearRig.Crashed; i++)
            {
                nearRig.SimStep(StepDt);
                var p = nearRig.WorldPosition;
                nearMinY = Mathf.Min(nearMinY, p.Y);
                if (i % 300 == 0)
                {
                    report.AppendLine($"near t={i / 60f:0.0}s pos=({p.X:0},{p.Y:0},{p.Z:0}) "
                        + $"plant={(nearPlant.FarFieldPlant ? "far" : "near")} spd={nearPlant.Speed:0.0} "
                        + $"under={UnderSheet(p)} grazes={nearGrazes} crashed={nearRig.Crashed}");
                }

                endedAt = i;
            }

            var nearEnd = nearRig.WorldPosition;
            report.AppendLine($"near leg: end ({nearEnd.X:0},{nearEnd.Y:0},{nearEnd.Z:0}) at t={endedAt / 60f:0.0}s, "
                + $"min y {nearMinY:0}, grazes {nearGrazes}, crashed {nearRig.Crashed}");

            // ⚠ Neither flown leg says where the ace goes. The far branch forces all three rotation
            // authorities to 1, and the AI's altitude hold does not settle against that, so both
            // rigs circle. The track that answers the item is the pilot-free one above.
            ctx.Note($"flown {LegSeconds} s: far-field min y {minY:0} m furthest {maxRangeM:0} m contacts {grazes}, near-field min y {nearMinY:0} m contacts {nearGrazes}");

            // The ground blow reads a ray along the NOSE, not downward, so it answers what lies ahead
            // rather than what lies overhead, and a body under a flat sheet gets nothing from it.
            ctx.Note($"ground blow elev={stats.GroundBlowElev:0} m, ai term={stats.AiGroundBlow:0.###}, probed along the nose rather than downward");
        }
        finally
        {
            near?.Free();
            far?.Free();
            textures.Dispose();
        }
    }

    // The authored pose of one roster block, in the mission-data convention CampaignRoster reads.
    private static (Vector3 Pos, float YawDeg)? PoseOf(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, string name)
    {
        foreach (var (blockName, fields) in blocks)
        {
            if (!blockName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (AiSkills.RosterSpawnPose(fields) is { } pose)
                return (pose.Position, pose.YawDeg);
        }

        return null;
    }
}
