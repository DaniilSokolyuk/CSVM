using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the look controls, flown through a real human rig with <c>--look=</c>
/// standing in for the right stick and <c>--view=</c> for a held numpad key: the chase camera and
/// the first-person head take the SAME deflection through one reader and one envelope, and the
/// snap cluster swings the chase camera through the same head that aims the cockpit, at the decoded
/// rate and onto the direction the table names. Both paths are read on the camera step rather than
/// through <c>FlightInput</c>, so this is the only machine-driveable route to them. Envelope,
/// rates and snap table: <see cref="HeadLook"/>.</summary>
internal static class LookStickSuites
{
    // A partial deflection on purpose: a full one would pass an implementation that clamped
    // everything to the envelope's edge, and a partial one only passes a proportional map.
    private const float StickX = 0.6f;

    // The snap sortie's held key: numpad 4, which the original's own stills put on the aircraft's
    // starboard flank, level, and which the table reaches as a pure quarter-turn of azimuth.
    private const int SnapDigit = 4;

    // Read the pan partway, at exactly one azimuth time constant, so the reading is a RATE rather
    // than the settled angle any smoothing law would eventually reach.
    private const int PanFrames = 12;

    private const float StepDt = 1f / 60f;

    // Long enough for the head's exponential catch-up to arrive: the slower of the two decoded
    // rates is elevation's 3/s (tau ~ 0.33 s), so two seconds leaves ~0.25% of the swing.
    private const int SettleFrames = 120;

    // The chase camera writes its pose rigidly and the head arrives asymptotically, so the two
    // readings are compared at a degree rather than at float precision.
    private const float ToleranceDeg = 1.5f;

    // The padlock sortie's target, a bare identity in the pool so the acquisition can be asserted
    // by reference rather than by name.
    private static readonly object PadlockMark = new();

    // Where the stick's own swing has to land: StickX of the shared envelope.
    private static float ExpectedSwingDeg => StickX * HeadLook.PadLookYawMaxDeg;

    private static System.Globalization.CultureInfo Inv =>
        System.Globalization.CultureInfo.InvariantCulture;

    private static string StickArg => StickX.ToString("0.##", Inv);

    /// <summary>Flies one aircraft per view with the same pinned stick and reads what the camera
    /// did with it.</summary>
    [Suite("look-stick",
        "the look controls flown in both views with --look= standing in for the right stick and --view= for a held numpad key: the flags parse, clamp and survive a typo, one deflection through one reader and one envelope swings the chase camera and the cockpit head the same angle to the same side of the aeroplane, a snap pans the chase camera through the same head at the decoded azimuth rate onto the table's own direction, and L padlocks that head onto a real selection's own bearing and hands it back to snap, every path returning to its settled pose on release")]
    internal static void LookStick(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");

        var spec = SessionSpec.Parse(new[] { "--fly", $"--look={StickArg},0" });
        ctx.Check(Mathf.Abs(spec.PinnedLook.X - StickX) < 1e-4f && spec.PinnedLook.Y == 0f,
            $"--look= parses into a right-stick deflection ({spec.PinnedLook.X:0.##}, {spec.PinnedLook.Y:0.##})");
        ctx.Check(SessionSpec.Parse(new[] { "--fly", "--look=9,9" }).PinnedLook.X == 1f,
            $"and clamps a past-full deflection into the stick's own [-1, 1]");
        ctx.Check(SessionSpec.Parse(new[] { "--fly", "--look=sideways" }).PinnedLook == Vector2.Zero,
            $"while a malformed pair leaves the stick centred rather than killing the run");

        var report = new StringBuilder();
        var chase = Fly(ctx, texturesPath, chapterZrdr, PilotViewMode.Chase, report);
        var cockpit = Fly(ctx, texturesPath, chapterZrdr, PilotViewMode.Cockpit, report);
        ctx.WriteArtifact("test-look-stick.txt", report.ToString());
        if (chase == null || cockpit == null)
        {
            ctx.Check(false, $"both views build an aircraft to fly");
            return;
        }

        Judge(ctx, "chase", chase.Value);
        Judge(ctx, "cockpit", cockpit.Value);
        // The request itself: one stick, one envelope, so the two views cannot answer it
        // differently. Read off the live cameras, not off the constants they were fed.
        ctx.Check(Mathf.Abs(chase.Value.SwingDeg - cockpit.Value.SwingDeg) < ToleranceDeg,
            $"the chase camera and the cockpit head swing the same angle for one stick position (chase {chase.Value.SwingDeg:0.#}°, cockpit {cockpit.Value.SwingDeg:0.#}°)");
        ctx.Note($"flew --look={StickArg},0 in both views and read the cameras it aimed");
    }

    private static void Judge(TestContext ctx, string view, in Swing swing)
    {
        ctx.Check(Mathf.Abs(swing.SwingDeg - ExpectedSwingDeg) < ToleranceDeg,
            $"{view}: a {StickX:0.##} stick swings {ExpectedSwingDeg:0.#}° of the shared envelope (read {swing.SwingDeg:0.#}°)");
        // Chase moves the CAMERA to the aircraft's right and looks back at it; the cockpit head
        // AIMS to the right. Opposite view vectors, the same side of the aeroplane, which is what
        // "the same" means between an orbit and a head.
        ctx.Check(swing.SideX > 0f,
            $"{view}: pushing the stick right puts the view on the aircraft's right (x={swing.SideX:0.###})");
        ctx.Check(swing.ReleasedDeg < ToleranceDeg,
            $"{view}: centring the stick returns the view to its settled pose (read {swing.ReleasedDeg:0.#}° off)");
    }

    // One flight: build a single human in the named view with the stick pinned, settle, read the
    // swing, then centre the stick and settle again to read what release leaves behind.
    private static Swing? Fly(TestContext ctx, string texturesPath, string chapterZrdr,
        PilotViewMode mode, StringBuilder report)
    {
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var savedClock = GameClock.Current;
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        FlightRoster? roster = null;
        try
        {
            var spec = SessionSpec.Parse(new[]
            {
                "--fly", $"--look={StickArg},0", $"--view={PilotView.Name(mode)}",
            });
            var rigs = new[] { rig };
            roster = BuildRoster(ctx, spec, planesGamez, textures, pool, chapterZrdr, rigs);
            roster.BuildPlayers(rigs);
            if (rig.Controller is not { } plane)
            {
                return null;
            }

            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            Settle(clock, plane);
            float swingDeg = SwingDegOf(mode, ctx.Camera, plane, out float sideX);
            // Release: the pin is the only stick in a headless run, so zeroing it IS letting go.
            plane.PinnedLook = Vector2.Zero;
            Settle(clock, plane);
            float releasedDeg = SwingDegOf(mode, ctx.Camera, plane, out _);
            report.AppendLine($"{PilotView.Name(mode)}: held {swingDeg:0.##}° side x={sideX:0.###}, " +
                $"released {releasedDeg:0.##}° (envelope {ExpectedSwingDeg:0.#}°)");
            if (mode == PilotViewMode.Chase)
            {
                // Flown on the back of this sortie rather than in its own: the aeroplane is
                // already built, already airborne and already settled at the chase pose.
                SnapSortie(ctx, clock, plane, report);
                PadlockSortie(ctx, clock, plane, report);
            }

            return new Swing(swingDeg, sideX, releasedDeg);
        }
        finally
        {
            GameClock.Current = savedClock;
            var live = rig.Controller;
            roster?.ClearMembership();
            live?.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The snap cluster in the chase view: the camera starts settled, a held numpad key pans the
    // one head at the decoded azimuth rate onto the direction the table names, and releasing it
    // returns the camera to the pose it held before any look input.
    private static void SnapSortie(TestContext ctx, GameClock clock, FlightController plane,
        StringBuilder report)
    {
        var settled = OffsetDir(ctx.Camera, plane);
        ctx.Check(plane.Head is { } floored
            && Mathf.Abs(floored.ElevationFloor - HeadLook.ChaseElevationFloor) < 1e-6f,
            $"a chase frame floors the head at the decoded {HeadLook.ChaseElevationFloor:0.0000} rad rather than first person's level floor (read {plane.Head?.ElevationFloor:0.0000})");

        plane.PinnedView = SnapDigit;
        Step(clock, plane, PanFrames);
        float panRad = plane.Head?.Azimuth ?? 0f;
        float expectedRad = (Mathf.Pi / 2f)
            * (1f - Mathf.Exp(-HeadLook.AzimuthSmoothRate * PanFrames * clock.FrameDt));
        ctx.Check(Mathf.Abs(panRad - expectedRad) < 0.02f,
            $"a held numpad {SnapDigit} pans the head at the decoded {HeadLook.AzimuthSmoothRate:0.#}/s: {Mathf.RadToDeg(panRad):0.#}° after one time constant, against {Mathf.RadToDeg(expectedRad):0.#}°");

        Step(clock, plane, SettleFrames);
        var held = OffsetDir(ctx.Camera, plane);
        ctx.Check(held.X > 0.9f && Mathf.Abs(held.Z) < 0.05f,
            $"and settles the camera on the aircraft's starboard flank, the table's entry for that key (dir {Fmt(held)})");
        ctx.Check(Mathf.Abs(held.Y - settled.Y) < 0.02f,
            $"at the chase rig's own elevation, a swing about the up axis lifting nothing (held y={held.Y:0.###}, settled y={settled.Y:0.###})");

        plane.PinnedView = 0;
        Step(clock, plane, SettleFrames);
        var back = OffsetDir(ctx.Camera, plane);
        float offDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(back.Dot(settled), -1f, 1f)));
        ctx.Check(offDeg < ToleranceDeg,
            $"and releasing it puts the camera back where it sat with no look input at all (read {offDeg:0.##}° off)");
        report.AppendLine($"chase snap {SnapDigit}: panned {Mathf.RadToDeg(panRad):0.##}° against " +
            $"{Mathf.RadToDeg(expectedRad):0.##}°, held dir {Fmt(held)}, released {Fmt(back)} " +
            $"against {Fmt(settled)}");
    }

    // Track Target over the shipped keymap and a real selection: L reaches the padlock state, the
    // head holds the target's own bearing rather than any input's, a target the cockpit floor would
    // bar still reaches the chase camera's, and L again gives the head back to the snap state.
    private static void PadlockSortie(TestContext ctx, GameClock clock, FlightController plane,
        StringBuilder report)
    {
        plane.Targeting = new TargetSelection();
        // Offered fresh every frame off the plane's CURRENT pose, so the bearing under test stays
        // put while the aeroplane flies: abeam to port and below, 500 m out.
        var offset = new Vector3(-500f, -250f, 0f);
        plane.TargetObjectives = list => list.Add(new AimCandidate
        {
            Position = plane.WorldPosition + (plane.GlobalBasis * offset),
            Team = InstantActionRuntime.EnemyTeam,
            Live = true,
            Source = PadlockMark,
            ConeOverride = AimAssist.NoConeOverride,
        });
        Step(clock, plane, 2);
        ctx.Check(plane.Targeting.Current is { } acquired && ReferenceEquals(acquired.Source, PadlockMark),
            $"the pilot has a real selection to padlock onto before the key is pressed ({plane.Targeting.Current?.Name})");

        Press(clock, plane, InputAction.TrackTarget);
        ctx.Check(plane.Head?.Mode == LookMode.Padlock,
            $"L puts the one head into the decoded padlock state (read {plane.Head?.Mode})");

        Step(clock, plane, SettleFrames);
        var (elevation, azimuth) = HeadLook.PadlockTargets(offset);
        float azDeg = Mathf.RadToDeg(Mathf.Abs((plane.Head?.Azimuth ?? 0f) - azimuth));
        float elDeg = Mathf.RadToDeg(Mathf.Abs((plane.Head?.Elevation ?? 0f) - elevation));
        ctx.Check(azDeg < ToleranceDeg && elDeg < ToleranceDeg,
            $"and the head settles on the target's own bearing, {Mathf.RadToDeg(azimuth):0.#}° round and {Mathf.RadToDeg(elevation):0.#}° down, with no look input at all (off by {azDeg:0.##}°, {elDeg:0.##}°)");
        var held = OffsetDir(ctx.Camera, plane);
        // An orbit swings AWAY from what the head aims at, so that both the aeroplane and the thing
        // it is watching stay in frame: a target abeam to port and below puts the camera to
        // starboard and above, the same sign the snap sortie above reads for a leftward azimuth.
        ctx.Check(held.X > 0.5f && held.Y > 0.3f,
            $"the chase camera swinging with it, opposite the target so both stay in frame (dir {Fmt(held)})");
        // The chase floor is what lets the head follow a target BELOW the aeroplane; the cockpit's
        // level floor would hold this same bearing flat, which is the pair's whole difference.
        ctx.Check((plane.Head?.Elevation ?? 0f) < -0.1f,
            $"below level, which only the chase floor allows (elevation {plane.Head?.Elevation:0.###} rad)");

        Press(clock, plane, InputAction.TrackTarget);
        ctx.Check(plane.Head?.Mode == LookMode.Snap,
            $"and a second L leaves the state for snap, never for free-look (read {plane.Head?.Mode})");
        Step(clock, plane, SettleFrames);
        ctx.Check(Mathf.Abs(plane.Head?.Azimuth ?? 1f) < 0.02f,
            $"the released head returning to straight ahead as that state's own idle frame does (azimuth {plane.Head?.Azimuth:0.###} rad)");
        report.AppendLine($"padlock: bearing {Mathf.RadToDeg(azimuth):0.##}°/{Mathf.RadToDeg(elevation):0.##}° " +
            $"off by {azDeg:0.##}°/{elDeg:0.##}°, camera dir {Fmt(held)}");

        plane.TargetObjectives = null;
        plane.Targeting = null;
    }

    // One press and release of a bound action, each on its own frame, so the head sees the press
    // EDGE its mode writers are stated on.
    private static void Press(GameClock clock, FlightController plane, InputAction action)
    {
        plane.HoldActionForTest(action, true);
        Step(clock, plane, 1);
        plane.HoldActionForTest(action, false);
        Step(clock, plane, 1);
    }

    // The camera's offset from the aeroplane, as a unit direction in the PLANE's frame, so the
    // dynamic radius and the aircraft's own manoeuvring both drop out of every reading.
    private static Vector3 OffsetDir(Camera3D camera, FlightController plane) =>
        (plane.GlobalBasis.Inverse() * (camera.GlobalTransform.Origin - plane.WorldPosition))
        .Normalized();

    private static string Fmt(Vector3 v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";

    private static void Settle(GameClock clock, FlightController plane) =>
        Step(clock, plane, SettleFrames);

    // The sim step and the camera step, in the order a live frame runs them: the camera reads the
    // drawn pose the sim step just left.
    private static void Step(GameClock clock, FlightController plane, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            clock.BeginFrame(StepDt);
            plane._PhysicsProcess(StepDt);
            plane._Process(StepDt);
        }
    }

    // How far the view sits off the settled pose, about the aircraft's own up axis, and which side
    // of the aircraft it went to. Chase is read off the camera's POSITION (it orbits the plane);
    // first person off the camera's AIM (it turns in place). Both are read in the plane's frame,
    // so the aeroplane's own manoeuvring cancels out.
    private static float SwingDegOf(PilotViewMode mode, Camera3D camera, FlightController plane,
        out float sideX)
    {
        var toPlane = plane.GlobalBasis.Inverse();
        if (PilotView.IsFirstPerson(mode))
        {
            var aim = toPlane * -camera.GlobalTransform.Basis.Z;
            sideX = aim.X;
            // The nose is -Z, so this is the aim's bearing off the nose: the head's own azimuth.
            return Mathf.Abs(Mathf.RadToDeg(Mathf.Atan2(-aim.X, -aim.Z)));
        }

        var offset = toPlane * (camera.GlobalTransform.Origin - plane.WorldPosition);
        sideX = offset.X;
        // Behind is +Z, which is where the chase offset sits with the stick centred.
        return Mathf.Abs(Mathf.RadToDeg(Mathf.Atan2(offset.X, offset.Z)));
    }

    private static FlightRoster BuildRoster(TestContext ctx, SessionSpec spec, GameZ planesGamez,
        TextureArchive textures, ProjectilePool pool, string chapterZrdr, IReadOnlyList<PlayerRig> rigs)
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
            }, new LevelStart());
    }

    // What one flight reports: how far the view swung off the settled pose while the stick was
    // held, which side of the aircraft it swung to, and how much of the swing survived release.
    private readonly record struct Swing(float SwingDeg, float SideX, float ReleasedDeg);

    // One aeroplane, airborne and level, with no world to fly into: the readings below are all
    // taken in the aircraft's own frame, so where it is does not matter, only that it flies.
    private sealed class LevelStart : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 500f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 100f);
            }

            return starts;
        }
    }
}
