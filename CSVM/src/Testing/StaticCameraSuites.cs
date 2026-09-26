using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suites over the two random static cameras, flown through a real human rig on the
/// empty stage: the death camera the pilot's own destruction cuts to, and the flyby the view key
/// enters. Both hold ONE world point and re-aim at the aeroplane, and both take the shared terrain
/// clearance, so the readings here are the placement law, the hold, the lift and the flyby's own
/// re-site cadence. Decode and constants: docs/formats/camparam.md.</summary>
internal static class StaticCameraSuites
{
    private const float StepDt = 1f / 60f;

    // The shipped default camparam block's own numbers, restated so a wrong field reads as a wrong
    // number in a failure line rather than as a silent agreement with itself.
    private const float DeathX = 80f, DeathInterval = 2f, DeathAlt = 5f, DeathMinAlt = 15.1f;
    private const float CrashElev = 40f;
    private const float FlybyMinRadius = 5.5f, FlybyMaxRadius = 7f;
    private const float FlybyMinWatch = 3.8f, FlybyMaxWatch = 4.3f;
    private const float FlybyMinSwitch = 70f, FlybyMaxSwitch = 85f;
    private const float WedgeDeg = 15f;

    // The flown start: level, heading down the world's own −Z, so the aircraft basis is the
    // identity and a world offset IS the plane-local offset the law wrote.
    private const float StartSpeed = 100f;
    private const float HighAltitude = 800f;   // no candidate angle can reach the clearance floor
    private const float LowAltitude = 60f;     // a downward angle lands under it

    // The empty stage's ground plane is at y=0, so this is where the shared clearance holds a
    // candidate placed under it.
    private const float ClearanceFloor = CrashElev;

    private const float Tol = 0.5f;            // metres, against a 60 Hz drawn pose

    // Long enough to prove the eye holds and the aim swings, short enough to stay inside the
    // crash's own auto-respawn delay (1.5 s), which would otherwise end the camera mid-reading.
    private const int HoldFrames = 60;

    // Ten seconds: two flyby watch deadlines (3.8-4.3 s each) plus the pass either side of them.
    private const int FlybyFrames = 600;

    /// <summary>Kills the player's aircraft on the empty stage and reads the death camera over its
    /// whole hold, twice: high up where the placement law is exact, and low down where the shared
    /// clearance has to lift the chosen spot off the ground.</summary>
    [Suite("death-camera",
        "the death camera flown on the empty stage: the player's own aircraft destroyed through "
        + "the production damage path cuts to a spot the death_* fields chose, death_x off the "
        + "flight axis and speed times death_interval along it, which then holds still for its "
        + "whole hold while the view keeps re-aiming at the wreck; and the same kill low over the "
        + "stage's ground, with the camera stream pinned to a downward angle, is lifted to "
        + "crash_elev above the terrain by the clearance all three static cameras share rather "
        + "than left on the death_min_alt floor the law alone would give it")]
    internal static void DeathCamera(TestContext ctx)
    {
        var (textures, chapterZrdr) = Inputs(ctx);
        var report = new StringBuilder();
        var stage = EmptyStage.Build(collision: true);
        ctx.Host.AddChild(stage.Root);
        try
        {
            Register(stage);
            Fly(ctx, textures, chapterZrdr, pinnedView: null, HighAltitude,
                (plane, camera, clock) => HighKill(ctx, plane, camera, clock, report));
            Fly(ctx, textures, chapterZrdr, pinnedView: null, LowAltitude,
                (plane, camera, clock) => LowKill(ctx, plane, camera, clock, report));
        }
        finally
        {
            ctx.Host.RemoveChild(stage.Root);
            stage.Root.QueueFree();
            textures.Dispose();
        }

        ctx.WriteArtifact("test-death-camera.txt", report.ToString());
        ctx.Note($"killed the player at {HighAltitude:0} m and {LowAltitude:0} m and read the death camera over {HoldFrames} frames each");
    }

    /// <summary>Enters the flyby on the empty stage and flies past it: the camera holds a world
    /// point out on the aircraft's flank and AHEAD of it, so the aeroplane closes, passes within
    /// the authored radius and leaves, after which the camera re-sites.</summary>
    [Suite("flyby-camera",
        "the flyby camera entered with --view=flyby and flown on the empty stage: the camera takes "
        + "a world point out on the aircraft's flank and ahead of it, holds that point while the "
        + "aeroplane closes and passes within the authored flyby radius -- which a spot placed "
        + "behind the aircraft could never do -- keeps re-aiming at it the whole way, then re-sites "
        + "once the drawn watch time has passed and the drawn switch distance is exceeded, every "
        + "re-site landing outside the two 15 degree wedges on the aircraft's own vertical")]
    internal static void FlybyCamera(TestContext ctx)
    {
        var (textures, chapterZrdr) = Inputs(ctx);
        var report = new StringBuilder();
        try
        {
            Fly(ctx, textures, chapterZrdr, pinnedView: "flyby", HighAltitude,
                (plane, camera, clock) => FlyPast(ctx, plane, camera, clock, report));
        }
        finally
        {
            textures.Dispose();
        }

        ctx.WriteArtifact("test-flyby-camera.txt", report.ToString());
        ctx.Note($"flew {FlybyFrames} frames in --view=flyby and read every spot the camera took");
    }

    // --- the death legs ---------------------------------------------------------------------

    // High up, nothing can lift the spot, so the placement law is read exactly: the circle about
    // the flight axis at death_x, and the longitudinal carry at speed times death_interval.
    private static void HighKill(TestContext ctx, FlightController plane, Camera3D camera,
        GameClock clock, StringBuilder report)
    {
        Settle(clock, plane, 10);
        float speed = plane.WorldVelocity.Length();
        var at = plane.GlobalPosition;
        Kill(ctx, plane);
        ctx.Check(plane.Crashed, $"the production damage path took the aircraft out hull={plane.Damage?.WholeHealth ?? -1f:0.0}");

        var eye = camera.GlobalPosition;
        var offset = plane.GlobalBasis.Inverse() * (eye - at);
        report.AppendLine($"high: kill at {at} speed={speed:0.0} eye={eye} plane-frame offset={offset}");
        ctx.Check(Math.Abs(new Vector2(offset.X, offset.Y - DeathAlt).Length() - DeathX) < Tol,
            $"the spot sits death_x off the flight axis (read {new Vector2(offset.X, offset.Y - DeathAlt).Length():0.00} m against {DeathX:0} m)");
        ctx.Check(Math.Abs(offset.Z + (speed * DeathInterval)) < Tol,
            $"…and speed times death_interval along it (read {-offset.Z:0.0} m against {speed * DeathInterval:0.0} m)");
        ctx.Check(offset.Z < 0f,
            $"…ahead of the nose rather than behind the tail (plane-frame z={offset.Z:0.0}, nose is −z)");
        ctx.Check(eye.Y > ClearanceFloor + DeathX,
            $"…far above the clearance floor, so this leg reads the law and not the lift (y={eye.Y:0.0} m)");

        Hold(ctx, plane, camera, clock, eye, "high", report);
    }

    // Low over the stage's ground with the camera stream pinned to a downward angle: the law alone
    // would leave the spot on its own death_min_alt floor, and the shared clearance lifts it.
    private static void LowKill(TestContext ctx, FlightController plane, Camera3D camera,
        GameClock clock, StringBuilder report)
    {
        Settle(clock, plane, 10);
        float draw = PinDownwardDraw();
        var at = plane.GlobalPosition;
        float unlifted = Mathf.Max(
            at.Y + StaticCameras.DeathLocalOffset(new CamParams(), plane.WorldVelocity.Length(), draw).Y + DeathAlt,
            DeathMinAlt);
        Kill(ctx, plane);

        var eye = camera.GlobalPosition;
        report.AppendLine($"low: kill at {at} draw={draw:0.0000} law would give y={unlifted:0.00} eye={eye}");
        ctx.Check(Math.Abs(unlifted - DeathMinAlt) < Tol,
            $"the pinned angle puts the law's own spot on the death_min_alt floor (y={unlifted:0.00} m), so the lift has something to do");
        ctx.Check(Math.Abs(eye.Y - ClearanceFloor) < Tol,
            $"…and the shared clearance lifts the flown one to crash_elev above the stage ground (y={eye.Y:0.00} m against {ClearanceFloor:0} m)");
        ctx.Check(Math.Abs(eye.Z - at.Z + (plane.WorldVelocity.Length() * DeathInterval)) < Tol,
            $"…without moving it along the flight path (z offset {eye.Z - at.Z:0.0} m)");

        Hold(ctx, plane, camera, clock, eye, "low", report);
    }

    // One spot, held: the eye must not move for the whole hold, and the aim must stay on the
    // aeroplane. There is no re-frame timer in the original, so a camera that moved at all failed.
    private static void Hold(TestContext ctx, FlightController plane, Camera3D camera,
        GameClock clock, Vector3 eye, string label, StringBuilder report)
    {
        float drift = 0f, worstAimDeg = 0f;
        for (int i = 0; i < HoldFrames; i++)
        {
            Settle(clock, plane, 1);
            drift = Math.Max(drift, camera.GlobalPosition.DistanceTo(eye));
            worstAimDeg = Math.Max(worstAimDeg, AimErrorDeg(camera, plane.GlobalPosition));
        }
        report.AppendLine($"{label}: over {HoldFrames} frames drift={drift:0.0000} m worst aim={worstAimDeg:0.000}°");
        ctx.Check(drift < 1e-3f,
            $"{label}: the death camera holds one spot for its whole hold (drift {drift:0.0000} m)");
        ctx.Check(worstAimDeg < 0.5f,
            $"{label}: …while the view keeps re-aiming at the wreck (worst {worstAimDeg:0.00}° off)");
        ctx.Check(plane.Crashed, $"{label}: the aircraft is still down at the end of the hold");
    }

    // --- the flyby leg ----------------------------------------------------------------------

    private static void FlyPast(TestContext ctx, FlightController plane, Camera3D camera,
        GameClock clock, StringBuilder report)
    {
        Settle(clock, plane, 1);   // the entry frame, which is where the first spot is taken
        var spots = new List<Vector3> { camera.GlobalPosition };
        var placedAt = new List<float> { (float)clock.Time };
        var wedgeDeg = new List<float> { VerticalWedgeDeg(plane, camera.GlobalPosition) };
        float firstRadius = LateralOffset(plane, camera.GlobalPosition);
        float firstAlong = (plane.GlobalBasis.Inverse() * (camera.GlobalPosition - plane.GlobalPosition)).Z;
        float closest = float.MaxValue, worstAimDeg = 0f, farthestBeforeMove = 0f;

        for (int i = 0; i < FlybyFrames; i++)
        {
            Settle(clock, plane, 1);
            var eye = camera.GlobalPosition;
            float range = eye.DistanceTo(plane.GlobalPosition);
            if (eye.DistanceTo(spots[^1]) > 1f)
            {
                spots.Add(eye);
                placedAt.Add((float)clock.Time);
                wedgeDeg.Add(VerticalWedgeDeg(plane, eye));
            }
            else
            {
                farthestBeforeMove = Math.Max(farthestBeforeMove, range);
                closest = Math.Min(closest, range);
            }
            worstAimDeg = Math.Max(worstAimDeg, AimErrorDeg(camera, plane.GlobalPosition));
        }

        report.AppendLine($"flyby: first spot {firstRadius:0.00} m off axis, {-firstAlong:0.0} m along it");
        for (int i = 1; i < spots.Count; i++)
        {
            report.AppendLine($"  re-site {i} at t={placedAt[i]:0.00} s, {spots[i - 1].DistanceTo(spots[i]):0} m from the last");
        }
        report.AppendLine($"closest approach {closest:0.00} m, farthest held {farthestBeforeMove:0} m, worst aim {worstAimDeg:0.00}°");
        report.AppendLine($"wedge clearance per spot: {string.Join(", ", wedgeDeg.ConvertAll(d => $"{d:0.0}°"))}");

        ctx.Check(firstRadius > FlybyMinRadius - Tol && firstRadius < FlybyMaxRadius + Tol,
            $"the spot sits inside the authored flyby radius (read {firstRadius:0.00} m against {FlybyMinRadius:0.0}-{FlybyMaxRadius:0.0} m)");
        ctx.Check(firstAlong < 0f,
            $"…AHEAD of the aircraft rather than behind it (plane-frame z={firstAlong:0.0}, nose is −z)");
        // The able-to-fail reading of that sign: a spot placed behind the aeroplane can never be
        // approached, so the closest the two ever come would be the placement distance itself.
        ctx.Check(closest < FlybyMaxRadius * 4f,
            $"…so the aeroplane actually flies PAST the camera (closest {closest:0.00} m, placed {-firstAlong:0} m out)");
        ctx.Check(worstAimDeg < 0.5f,
            $"the camera re-aims at the aeroplane every frame while it holds (worst {worstAimDeg:0.00}° off)");
        ctx.Check(spots.Count >= 2,
            $"the camera re-sites once the watch time and the switch distance are both past (spots={spots.Count})");
        ctx.Check(farthestBeforeMove > FlybyMinSwitch,
            $"…having first let the aeroplane run past the switch distance (farthest held {farthestBeforeMove:0} m)");
        for (int i = 1; i < spots.Count; i++)
        {
            float gap = placedAt[i] - placedAt[i - 1];
            ctx.Check(gap > FlybyMinWatch,
                $"re-site {i} came no sooner than the shortest authored watch time (t+{gap:0.00} s against {FlybyMinWatch:0.0} s)");
        }
        for (int i = 0; i < spots.Count; i++)
        {
            ctx.Check(wedgeDeg[i] > WedgeDeg - 1f,
                $"spot {i} sits outside the wedge on the aircraft's own vertical ({wedgeDeg[i]:0.0}° off it, against {WedgeDeg:0}°)");
        }
        ctx.Check(spots.Count < 2 || placedAt[1] < FlybyMaxWatch + 2f,
            $"…and no later than one watch plus the run out past the threshold (t={placedAt[1]:0.00} s)");
    }

    // --- shared plumbing --------------------------------------------------------------------

    private static (TextureArchive Textures, string ChapterZrdr) Inputs(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");
        return (new TextureArchive(texturesPath), chapterZrdr);
    }

    // The stage's collider answers a ray only once its transform has reached the physics server,
    // which a suite body never yields a frame for.
    private static void Register(EmptyStage stage)
    {
        foreach (var body in Bodies(stage.Root))
        {
            body.ForceUpdateTransform();
        }
    }

    private static IEnumerable<StaticBody3D> Bodies(Node node)
    {
        if (node is StaticBody3D body)
        {
            yield return body;
        }
        foreach (var child in node.GetChildren())
        {
            foreach (var found in Bodies(child))
            {
                yield return found;
            }
        }
    }

    // Rounds through the pool's own entry point until whole-vehicle health is spent, which is the
    // one path that reaches Destroy and therefore the death camera.
    // ⚠ Ordnance, never a CANNON round: the incoming-fire shield discards gun damage on a human rig
    // until sustained fire saturates it, and these hits are fed inside one frame, where the
    // accumulator never ticks at all. Anything else about the kill is the same call.
    private static void Kill(TestContext ctx, FlightController plane)
    {
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        WeaponDef? round = null;
        foreach (var w in weapons.All)
        {
            if (!w.IsCannon && w.HealthDamage > 0f
                && (round == null || w.HealthDamage > round.HealthDamage))
            {
                round = w;
            }
        }
        if (round == null)
        {
            throw new SuiteSkippedException("no non-cannon weapon carries health damage");
        }
        for (int i = 0; i < 4000 && !plane.Crashed; i++)
        {
            plane.TakeProjectileHit(round, plane.GlobalPosition, "fuselage", 0);
        }
    }

    // Pins the camera stream to a draw whose angle puts the death spot well BELOW the aircraft, so
    // the clearance leg is a fixed reading rather than whatever the seed happened to give.
    private static float PinDownwardDraw()
    {
        var stream = Rng.Stream(Rng.Camera);
        for (ulong seed = 1; seed < 4096; seed++)
        {
            stream.Seed = seed;
            float draw = stream.Randf();
            if (Mathf.Sin(draw * Mathf.Tau) < -0.95f)
            {
                stream.Seed = seed;   // rewound, so the camera's own first draw is this one
                return draw;
            }
        }
        throw new SuiteSkippedException("no seed in the search window drew a downward death angle");
    }

    private static float AimErrorDeg(Camera3D camera, Vector3 target)
    {
        var toTarget = target - camera.GlobalPosition;
        if (toTarget.LengthSquared() < 1e-6f)
        {
            return 0f;
        }
        return Mathf.RadToDeg(
            Mathf.Acos(Mathf.Clamp((-camera.GlobalBasis.Z).Normalized().Dot(toTarget.Normalized()), -1f, 1f)));
    }

    // How far a spot's lateral bearing lies from the aircraft's own vertical, taking the nearer of
    // the up and down axes: the flyby excludes a wedge about both, so this is the one number that
    // says whether a spot is out on a flank.
    private static float VerticalWedgeDeg(FlightController plane, Vector3 eye)
    {
        var offset = plane.GlobalBasis.Inverse() * (eye - plane.GlobalPosition);
        float fromUp = Mathf.RadToDeg(Mathf.Atan2(Math.Abs(offset.X), offset.Y));
        return Math.Min(fromUp, 180f - fromUp);
    }

    // How far off the aircraft's own flight axis a world point sits: the lateral pair of the
    // plane-frame offset, which is the radius both placements draw.
    private static float LateralOffset(FlightController plane, Vector3 eye)
    {
        var offset = plane.GlobalBasis.Inverse() * (eye - plane.GlobalPosition);
        return new Vector2(offset.X, offset.Y).Length();
    }

    private static void Settle(GameClock clock, FlightController plane, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            clock.BeginFrame(StepDt);
            plane.SimStep(StepDt);      // the session's own simulation step, so the aeroplane flies
            plane._Process(StepDt);     // then the camera step, in the order a live frame runs them
        }
    }

    // One built human rig on a level start at the named altitude, handed to the body with the
    // session camera it flies and the clock that steps it.
    private static void Fly(TestContext ctx, TextureArchive textures, string chapterZrdr,
        string? pinnedView, float altitude, Action<FlightController, Camera3D, GameClock> body)
    {
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var savedClock = GameClock.Current;
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        FlightRoster? roster = null;
        try
        {
            var args = pinnedView == null
                ? new[] { "--fly" }
                : new[] { "--fly", $"--view={pinnedView}" };
            var spec = SessionSpec.Parse(args);
            var rigs = new[] { rig };
            roster = BuildRoster(ctx, spec, planesGamez, textures, pool, chapterZrdr, rigs,
                new LevelStart(altitude));
            roster.BuildPlayers(rigs);
            if (rig.Controller is not { } plane)
            {
                ctx.Check(false, $"the roster builds an aircraft to fly");
                return;
            }
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            body(plane, ctx.Camera, clock);
        }
        finally
        {
            GameClock.Current = savedClock;
            var live = rig.Controller;
            roster?.ClearMembership();
            live?.Free();
            pane.Free();
            pool.Free();
        }
    }

    private static FlightRoster BuildRoster(TestContext ctx, SessionSpec spec, GameZ planesGamez,
        TextureArchive textures, ProjectilePool pool, string chapterZrdr,
        IReadOnlyList<PlayerRig> rigs, IFlightStarts starts)
    {
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
                ChapterZrdrPath = chapterZrdr,
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                PauseState = new PauseState(),
                MenuInputFor = _ => new UI.Screens.MenuInput(),
                ExitSession = () => { },
            }, starts);
    }

    // Level, over the grid origin, heading down the world's own −Z: the aircraft basis is then the
    // identity, so a world offset reads directly as the plane-local offset the law wrote.
    private sealed class LevelStart : IFlightStarts
    {
        private readonly float _altitude;

        public LevelStart(float altitude) => _altitude = altitude;

        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, _altitude, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, StartSpeed);
            }

            return starts;
        }
    }
}
