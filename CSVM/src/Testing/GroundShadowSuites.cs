using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The aircraft ground shadow over the flat empty stage and over C1's real terrain: that
/// one lands under the aircraft it belongs to, that it takes the surface height the probe reads,
/// that the player's own runs 1.5 times its altitude ahead while an AI aircraft's sits directly
/// beneath, that the player's own footprint alone grows with altitude, that in two panes each
/// pane's own pilot is the one taking that shape, that the texture carries the airframe's own
/// silhouette rather than any symmetric blob, and that it is absent exactly where the decode says
/// nothing is drawn (over the cutoff altitude, past the far range, and in enhanced graphics
/// mode). Decode: docs/org/shadows.md.</summary>
internal static class GroundShadowSuites
{
    // The empty stage's ground plane, which the shadow must land on rather than on y=0 by luck.
    private const float StageGroundY = 0f;

    // How far apart the two panes' aeroplanes sit, well inside the distance fade so both are drawn
    // in both panes and far enough apart that neither shadow can be read for the other's.
    private const float PaneSeparation = 60f;

    // Altitudes either side of the ramp, and a range inside and outside the distance fade.
    private const float LowAltitude = 40f;
    private const float MidAltitude = 155f;
    private const float OverCutoff = 400f;
    private const float InRange = 150f;
    private const float OutOfRange = 300f;

    // How close a reading has to be to count. The quad is placed from the drawn pose, so the
    // slack covers the lift off the ground and nothing else.
    private const float PlaceTolerance = 1.5f;

    // How many passes the cost reading times, and how many it throws away first. The raster runs
    // once per aircraft per frame, so the timed loop is the frame cost the roster multiplies.
    private const int CostPasses = 200;
    private const int CostWarmups = 20;

    // What an ellipse inscribed in the footprint would cover, which is what the silhouette
    // replaced, against the wing sample that tells the two apart.
    private const float EllipseCoverage = 0.45f;
    private const float TipU = 0.10f;
    private const float WingV = 0.73f;

    // The Hoplite, the one airframe with an overhead rotor, and the seconds of propeller time
    // between the two rasters the turning check compares. A quarter second turns the rotor about
    // 41 degrees, well inside the 120 the three-blade blur repeats at.
    private const string RotorPlane = "player_autogyro";
    private const float PropStep = 0.25f;

    // How many texels must move for a rotor to read as turning, and the most a full circular
    // propeller disc is allowed to move on its own.
    private const int TurnedTexels = 20;
    private const int StillTexels = 4;

    [Suite("ground-shadow",
        "The projected aircraft ground shadow: under the aircraft on the flat stage and on C1's " +
        "own terrain height, running 1.5 times its altitude ahead of the player while an AI " +
        "aircraft's sits beneath, the player's footprint alone doubling by 155 m, that shape " +
        "belonging to each pane's own pilot in a two-pane session, the airframe's own silhouette " +
        "in the texture turning with it, and absent over 250 m of altitude, past 200 m of range " +
        "and under enhanced graphics")]
    internal static void GroundShadow(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var report = new StringBuilder();
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        bool wasEnhanced = GraphicsMode.Enhanced;
        try
        {
            // Every drawing check below is about the original's own path, so pin the mode rather
            // than inheriting whatever the run was launched in; the run's own mode is restored.
            GraphicsMode.Resolve(GraphicsMode.Default);
            FlatStage(ctx, planesGamez, textures, report);
            Panes(ctx, planesGamez, textures, report);
            Turning(ctx, planesGamez, textures, report);
            Terrain(ctx, planesGamez, textures, report);
            Gate(ctx, report);
        }
        finally
        {
            GraphicsMode.Resolve(wasEnhanced ? GraphicsMode.EnhancedWord : GraphicsMode.Default);
            textures.Dispose();
        }

        ctx.WriteArtifact("test-ground-shadow.txt", report.ToString());
        ctx.Note($"ground shadow: placed under the aircraft on both stages, and dropped where the decode drops it");
    }

    // One aircraft, parked at a pose rather than flown: the pass reads the pose the model is
    // drawn at, so moving the rig between passes is the same input a flown frame gives it.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        bool human, Vector3 at, string? plane = null, bool spinning = false)
    {
        string name = plane ?? ctx.PlaneName;
        var stats = PlaneStats.Load(ctx.ZrdrPath, name);
        var model = new PlaneBuilder(planesGamez, textures, spinningProps: spinning).Build(name);
        var nose = at + Vector3.Forward;
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = PlaneDamage.For(stats),
            PlayerIndex = human ? FlightRoster.ShooterIdBase - 1 : FlightRoster.ShooterIdBase,
            IsHumanPiloted = human,
            Pilot = human ? null : AiPilot.HoldingCourse(at, nose),
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
            Props = spinning ? PropAnimator.Build(model) : null,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats, aiForcePath: !human), null, new CamParams(), at, nose);
        rig.Name = $"shadow_{name}_{(human ? "player" : "ai")}";
        ctx.Host.AddChild(rig);
        return rig;
    }

    // One pane as the pass reads the session's rigs: whose aeroplane it is, and the visual layer
    // this pane's camera alone draws (0 outside splitscreen, where one camera draws everything).
    private static PlayerRig Pane(int index, FlightController? flying, uint layer,
        Camera3D? camera = null) =>
        new() { Index = index, Camera = camera!, Controller = flying, VisualLayer = layer };

    // The rasterised silhouette as text, for the artifact: one row per texel row of the mask.
    private static void Mask(GroundShadowPass pass, FlightController rig, string who,
        StringBuilder report)
    {
        if (pass.ShapeFor(rig) is not { } shape)
            return;
        int size = GroundShadowLaw.TextureSize;
        report.AppendLine($"{who} silhouette: node={shape.Node} triangles={shape.TriangleCount} vertices={shape.VertexCount} covered={shape.CoveredFraction:0.000}");
        for (int y = 0; y < size; y++)
        {
            var row = new StringBuilder();
            for (int x = 0; x < size; x++)
                row.Append(shape.CoveredAt((x + 0.5f) / size, (y + 0.5f) / size) ? '#' : '.');
            report.AppendLine($"  {row}");
        }
    }

    // Covered texels along one row of the mask (across the aircraft) and one column (along it).
    private static int Row(GroundShadowSilhouette shape, float v)
    {
        int size = GroundShadowLaw.TextureSize;
        int covered = 0;
        for (int x = 0; x < size; x++)
        {
            if (shape.CoveredAt((x + 0.5f) / size, v))
                covered++;
        }

        return covered;
    }

    private static int Column(GroundShadowSilhouette shape, float u)
    {
        int size = GroundShadowLaw.TextureSize;
        int covered = 0;
        for (int y = 0; y < size; y++)
        {
            if (shape.CoveredAt(u, (y + 0.5f) / size))
                covered++;
        }

        return covered;
    }

    // The silhouette, on the AI aircraft because its projection is the plain top-down one: the
    // model's own outline rasterised into the modulate texture, which no symmetric blob stands in
    // for, and which turns with the aircraft.
    private static void Silhouette(TestContext ctx, GroundShadowPass pass, FlightController ai,
        StringBuilder report)
    {
        var shape = pass.ShapeFor(ai);
        ctx.Check(shape != null, $"the aircraft's shadow carries a silhouette of its own model");
        if (shape is not { } mask)
            return;
        int size = GroundShadowLaw.TextureSize;
        Mask(pass, ai, "ai", report);
        ctx.Check(mask.TriangleCount > 100,
            $"rasterised from the airframe's own triangles n={mask.TriangleCount}");
        ctx.Check(mask.CoveredFraction > 0.05f && mask.CoveredFraction < EllipseCoverage,
            $"and it covers a fraction of its footprint no inscribed ellipse could {mask.CoveredFraction:0.000} against {EllipseCoverage:0.00}");

        // The airframe is mirror-symmetric across its own fuselage and nothing like it nose to
        // tail, so the mask must be the first and must not be the second.
        int mirrored = 0;
        int flipped = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                if (mask.CoveredAt(u, v) != mask.CoveredAt(1f - u, v))
                    mirrored++;
                if (mask.CoveredAt(u, v) != mask.CoveredAt(u, 1f - v))
                    flipped++;
            }
        }

        report.AppendLine($"ai silhouette: {mirrored} texels break the mirror across the fuselage, {flipped} break it nose to tail");
        ctx.Same(0, mirrored, $"mirror-symmetric across the fuselage, as the airframe is");
        ctx.Check(flipped > size * size / 10,
            $"and not symmetric nose to tail, as no ellipse in this footprint can manage n={flipped}");

        // One sample pair an ellipse cannot satisfy: out at the wing, the span is covered behind
        // the aircraft's middle and empty the same distance ahead of it.
        ctx.Check(mask.CoveredAt(TipU, WingV),
            $"the wing's span is covered out at ({TipU:0.00}, {WingV:0.00}) of the footprint");
        ctx.Check(!mask.CoveredAt(TipU, 1f - WingV),
            $"and the mirrored point ahead of it is empty, where an ellipse covers both");
        report.AppendLine($"ai silhouette: wing row {Row(mask, WingV)} texels, the row ahead of it {Row(mask, 1f - WingV)}");

        // Turned across the projection, the same wing band lands along the other axis: the
        // silhouette is the model's shape in the world, not a fixed picture in the footprint.
        int wingRow = Row(mask, WingV);
        ai.Rotation = new Vector3(0f, Mathf.Pi * 0.5f, 0f);
        pass.Tick();
        report.AppendLine($"ai silhouette yawed 90 deg: wing row {Row(mask, WingV)} texels, columns {Column(mask, WingV)} at {WingV:0.00} and {Column(mask, 1f - WingV)} at {1f - WingV:0.00}");
        ctx.Check(Row(mask, WingV) < wingRow / 2,
            $"a quarter-turn empties the row the wing filled {wingRow} -> {Row(mask, WingV)} texels");
        ctx.Check(Column(mask, WingV) > wingRow / 2,
            $"and fills the column the same distance behind the nose instead {Column(mask, WingV)} texels");
        ai.Rotation = Vector3.Zero;
        pass.Tick();
    }

    // What one pass costs per aircraft, wall clock, over the stage. Reported and never checked: a
    // wall-clock number on a shared machine is awareness, not a threshold (docs/verification.md
    // PERF-5). It is here because the texture's area sets it, so a size change is read off it.
    private static void Cost(GroundShadowPass pass, int aircraft, string who, StringBuilder report)
    {
        for (int i = 0; i < CostWarmups; i++)
            pass.Tick();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < CostPasses; i++)
            pass.Tick();
        clock.Stop();
        int size = GroundShadowLaw.TextureSize;
        double each = clock.Elapsed.TotalMilliseconds / (CostPasses * (double)aircraft);
        report.AppendLine($"cost ({who}): {each:0.0000} ms per aircraft per frame over {CostPasses} passes at {aircraft} aircraft, texture {size}x{size} = {size * size} bytes rebuilt and uploaded per aircraft per frame");
    }

    // The flat stage: known ground at y=0 under every pose, so a reading that disagrees is the
    // placement law and never the terrain.
    private static void FlatStage(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        StringBuilder report)
    {
        Node3D? stage = null;
        FlightController? player = null;
        FlightController? ai = null;
        GroundShadowPass? pass = null;
        var players = new List<Vector3>();
        try
        {
            stage = EmptyStage.Build(collision: true).Root;
            ctx.Host.AddChild(stage);
            player = Rig(ctx, planesGamez, textures, human: true, new Vector3(0f, LowAltitude, 0f));
            ai = Rig(ctx, planesGamez, textures, human: false, new Vector3(InRange, LowAltitude, 0f));
            var rigs = new List<FlightController> { player, ai };
            var panes = new List<PlayerRig> { Pane(0, player, 0u) };
            pass = GroundShadowPass.Build(ctx.Host, () => rigs, () => players, () => panes,
                () => WeatherRig.DefaultSunlightRgb);
            ctx.Check(pass != null, $"original graphics mode builds the ground-shadow pass");
            if (pass == null)
                return;

            players.Add(player.GlobalPosition);
            pass.Tick();
            ctx.Same(2, pass.Drawn, $"both aircraft over the stage cast a shadow");

            // The player's own, running ahead along its own flight direction. Nose is -Z here, so
            // the whole displacement must land in -Z and none of it in X.
            var own = pass.QuadFor(player);
            ctx.Check(own != null, $"the player's own aircraft casts one at {LowAltitude:0} m");
            float lead = -GroundShadowLaw.PlayerSkew * LowAltitude;
            if (own is { } ownQuad)
            {
                var at = ownQuad.GlobalPosition;
                report.AppendLine($"player at 0,{LowAltitude:0},0: shadow ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), expected z {lead:0.00}");
                ctx.Check(Mathf.Abs(at.X) <= PlaceTolerance,
                    $"the player's shadow is not displaced across its flight direction x={at.X:0.00}");
                ctx.Check(Mathf.Abs(at.Z - lead) <= PlaceTolerance,
                    $"and runs 1.5x its altitude ahead of it along that direction z={at.Z:0.00} expected={lead:0.00}");
                ctx.Check(Mathf.Abs(at.Y - StageGroundY) <= PlaceTolerance,
                    $"and lies on the stage's ground y={at.Y:0.00}");
            }

            // An AI aircraft's, which takes none of the player's three special cases.
            var other = pass.QuadFor(ai);
            ctx.Check(other != null, $"another aircraft {InRange:0} m away casts one too");
            float aiWidth = 0f;
            if (other is { } aiQuad)
            {
                var at = aiQuad.GlobalPosition;
                aiWidth = aiQuad.GlobalTransform.Basis.Scale.X;
                report.AppendLine($"ai at {InRange:0},{LowAltitude:0},0: shadow ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), footprint {aiWidth:0.00} m across");
                ctx.Check(Mathf.Abs(at.X - InRange) <= PlaceTolerance && Mathf.Abs(at.Z) <= PlaceTolerance,
                    $"and it sits directly beneath that aircraft ({at.X:0.00}, {at.Z:0.00})");
            }

            float ownWidth = own?.GlobalTransform.Basis.Scale.X ?? 0f;

            Silhouette(ctx, pass, ai, report);
            Mask(pass, player, "player", report);
            Cost(pass, rigs.Count, "still airframes", report);

            // Climb both. The player's footprint grows with the altitude ramp and the AI's does
            // not, which is the size law's whole content.
            player.GlobalPosition = new Vector3(0f, MidAltitude, 0f);
            ai.GlobalPosition = new Vector3(InRange, MidAltitude, 0f);
            players[0] = player.GlobalPosition;
            pass.Tick();
            float ownHigh = pass.QuadFor(player)?.GlobalTransform.Basis.Scale.X ?? 0f;
            float aiHigh = pass.QuadFor(ai)?.GlobalTransform.Basis.Scale.X ?? 0f;
            report.AppendLine($"at {MidAltitude:0} m: player footprint {ownHigh:0.00} m across (was {ownWidth:0.00}), ai {aiHigh:0.00} m (was {aiWidth:0.00})");
            ctx.Check(Mathf.Abs(ownHigh - (2f * ownWidth)) <= 0.2f,
                $"the player's footprint doubles by {MidAltitude:0} m {ownWidth:0.00} -> {ownHigh:0.00} m across");
            ctx.Check(Mathf.Abs(aiHigh - aiWidth) <= 0.2f,
                $"and no other aircraft's grows at all {aiWidth:0.00} -> {aiHigh:0.00} m across");

            // Where the decode draws nothing: over the cutoff altitude, and past the far range.
            player.GlobalPosition = new Vector3(0f, OverCutoff, 0f);
            ai.GlobalPosition = new Vector3(OutOfRange, LowAltitude, 0f);
            players[0] = player.GlobalPosition;
            pass.Tick();
            ctx.Check(pass.QuadFor(player) == null,
                $"nothing is drawn over the {GroundShadowLaw.CutoffAltitude:0} m cutoff altitude");
            ctx.Check(pass.QuadFor(ai) == null,
                $"and nothing past the {GroundShadowLaw.FarDistance:0} m far range");
            ctx.Same(0, pass.Drawn, $"so the stage draws no shadow at all in that pass");
        }
        finally
        {
            pass?.Free();
            player?.Free();
            ai?.Free();
            stage?.Free();
        }
    }

    // What one pane sees under its own pilot's aeroplane: the skew, on layers this pane's camera
    // draws and the other pane's does not.
    private static void Pilot(TestContext ctx, GroundShadowPass pass, List<PlayerRig> panes,
        FlightController rig, int pane, float x, StringBuilder report)
    {
        var drawn = pass.QuadFor(rig, pane);
        ctx.Check(drawn != null, $"P{pane + 1}'s pane draws a shadow under the aeroplane it is flying");
        if (drawn is not { } quad)
            return;
        var at = quad.GlobalPosition;
        float lead = -GroundShadowLaw.PlayerSkew * LowAltitude;
        report.AppendLine($"P{pane + 1}'s pane, its own aeroplane at x={x:0}: shadow ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), expected z {lead:0.00}");
        ctx.Check(Mathf.Abs(at.X - x) <= PlaceTolerance && Mathf.Abs(at.Z - lead) <= PlaceTolerance,
            $"and it runs 1.5x its altitude ahead of it there z={at.Z:0.00} expected={lead:0.00}");
        ctx.Check((quad.Layers & panes[pane].Camera.CullMask) != 0,
            $"on a layer this pane's own camera draws 0x{quad.Layers:X5} against 0x{panes[pane].Camera.CullMask:X5}");
        ctx.Check((quad.Layers & panes[1 - pane].Camera.CullMask) == 0,
            $"and on none the other pane's draws, so no second pane sees the skew");
    }

    // And what a pane sees under the other pilot's aeroplane: an ordinary shadow directly beneath
    // it, which is what every pane but its own pilot's draws.
    private static void Other(TestContext ctx, GroundShadowPass pass, List<PlayerRig> panes,
        FlightController rig, int pane, float x, StringBuilder report)
    {
        var drawn = pass.QuadFor(rig, pane);
        ctx.Check(drawn != null, $"P{pane + 1}'s pane draws one under the other pilot's aeroplane too");
        if (drawn is not { } quad)
            return;
        var at = quad.GlobalPosition;
        report.AppendLine($"P{pane + 1}'s pane, the other pilot's aeroplane at x={x:0}: shadow ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
        ctx.Check(Mathf.Abs(at.X - x) <= PlaceTolerance && Mathf.Abs(at.Z) <= PlaceTolerance,
            $"directly beneath it rather than skewed ({at.X:0.00}, {at.Z:0.00})");
        ctx.Check((quad.Layers & panes[pane].Camera.CullMask) != 0
            && (quad.Layers & panes[1 - pane].Camera.CullMask) == 0,
            $"on a layer this pane draws and that aeroplane's own pilot's does not 0x{quad.Layers:X5}");
    }

    // One shadow's footprint across, or zero where that pane draws none.
    private static float Width(GroundShadowPass pass, FlightController rig, int pane) =>
        pass.QuadFor(rig, pane)?.GlobalTransform.Basis.Scale.X ?? 0f;

    // Two panes over one world, each pilot flying his own aeroplane and both aeroplanes in both
    // panes. The player's skew and growth belong to the pane whose pilot is at those controls, so
    // one aeroplane carries two shadows at once, kept apart by the layers each pane's camera draws.
    private static void Panes(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        StringBuilder report)
    {
        Node3D? stage = null;
        FlightController? one = null;
        FlightController? two = null;
        Camera3D? camera1 = null;
        Camera3D? camera2 = null;
        GroundShadowPass? pass = null;
        try
        {
            stage = EmptyStage.Build(collision: true).Root;
            ctx.Host.AddChild(stage);
            one = Rig(ctx, planesGamez, textures, human: true, new Vector3(0f, LowAltitude, 0f));
            two = Rig(ctx, planesGamez, textures, human: true,
                new Vector3(PaneSeparation, LowAltitude, 0f));
            camera1 = SuiteViewers.Camera(ctx, one.GlobalPosition);
            camera2 = SuiteViewers.Camera(ctx, two.GlobalPosition);
            camera1.CullMask = UI.Boards.SplitScreen.PlayerCullMask(0);
            camera2.CullMask = UI.Boards.SplitScreen.PlayerCullMask(1);
            var panes = new List<PlayerRig>
            {
                Pane(0, one, UI.Boards.SplitScreen.PlayerVisualLayer(0), camera1),
                Pane(1, two, UI.Boards.SplitScreen.PlayerVisualLayer(1), camera2),
            };
            var rigs = new List<FlightController> { one, two };
            var players = new List<Vector3> { one.GlobalPosition, two.GlobalPosition };
            pass = GroundShadowPass.Build(ctx.Host, () => rigs, () => players, () => panes,
                () => WeatherRig.DefaultSunlightRgb);
            ctx.Check(pass != null, $"a two-pane session builds the ground-shadow pass");
            if (pass == null)
                return;
            pass.Tick();
            ctx.Same(4, pass.Drawn,
                $"two aeroplanes over two panes draw four shadows, a pilot's own and an ordinary one each");

            Pilot(ctx, pass, panes, one, 0, 0f, report);
            Pilot(ctx, pass, panes, two, 1, PaneSeparation, report);
            Other(ctx, pass, panes, two, 0, PaneSeparation, report);
            Other(ctx, pass, panes, one, 1, 0f, report);

            // The growth is the asking pane's as well. Climbing both aeroplanes at once, each
            // footprint follows the ramp in its own pilot's pane and stays put in the other's.
            float oneOwn = Width(pass, one, 0);
            float oneSeen = Width(pass, one, 1);
            float twoOwn = Width(pass, two, 1);
            float twoSeen = Width(pass, two, 0);
            one.GlobalPosition = new Vector3(0f, MidAltitude, 0f);
            two.GlobalPosition = new Vector3(PaneSeparation, MidAltitude, 0f);
            players[0] = one.GlobalPosition;
            players[1] = two.GlobalPosition;
            pass.Tick();
            report.AppendLine($"at {MidAltitude:0} m: P1's aeroplane {oneOwn:0.00} -> {Width(pass, one, 0):0.00} m across in its own pane and {oneSeen:0.00} -> {Width(pass, one, 1):0.00} in P2's, P2's {twoOwn:0.00} -> {Width(pass, two, 1):0.00} in its own and {twoSeen:0.00} -> {Width(pass, two, 0):0.00} in P1's");
            ctx.Check(Mathf.Abs(Width(pass, one, 0) - (2f * oneOwn)) <= 0.2f,
                $"P1's own footprint doubles in P1's pane {oneOwn:0.00} -> {Width(pass, one, 0):0.00} m across");
            ctx.Check(Mathf.Abs(Width(pass, one, 1) - oneSeen) <= 0.2f,
                $"and the same aeroplane does not grow at all in P2's {oneSeen:0.00} -> {Width(pass, one, 1):0.00} m");
            ctx.Check(Mathf.Abs(Width(pass, two, 1) - (2f * twoOwn)) <= 0.2f,
                $"P2's own footprint doubles in P2's pane {twoOwn:0.00} -> {Width(pass, two, 1):0.00} m across");
            ctx.Check(Mathf.Abs(Width(pass, two, 0) - twoSeen) <= 0.2f,
                $"and does not grow in P1's {twoSeen:0.00} -> {Width(pass, two, 0):0.00} m");
        }
        finally
        {
            pass?.Free();
            camera1?.Free();
            camera2?.Free();
            one?.Free();
            two?.Free();
            stage?.Free();
        }
    }

    // How many texels of one aircraft's mask move when only its blur discs turn. The aircraft is
    // not touched between the two rasters, so the difference can have come from nothing else.
    private static int Turned(GroundShadowPass pass, FlightController rig, float seconds)
    {
        if (pass.ShapeFor(rig) is not { } shape)
            return -1;
        int size = GroundShadowLaw.TextureSize;
        var before = new bool[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
                before[(y * size) + x] = shape.CoveredAt((x + 0.5f) / size, (y + 0.5f) / size);
        }

        rig.Props?.Advance(seconds, 1f);
        pass.Tick();
        int moved = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (before[(y * size) + x] != shape.CoveredAt((x + 0.5f) / size, (y + 0.5f) / size))
                    moved++;
            }
        }

        return moved;
    }

    // What one aircraft's own pass costs, with the roster narrowed to it. A roster the pass has
    // not seen before rebuilds its caster, which the cost reading's own warmups absorb.
    private static void Alone(GroundShadowPass pass, List<FlightController> rigs,
        FlightController? only, string who, StringBuilder report)
    {
        if (only == null)
            return;
        rigs.Clear();
        rigs.Add(only);
        Cost(pass, 1, who, report);
    }

    // The turning blur discs, which the original carries into its shadow by rasterising the live
    // node tree every frame. The Hoplite's rotor is three blades of a disc, so its shadow turns
    // with it; the fixed-wing propeller beside it is the control, a full circle about its own axis
    // whose turn changes no outline at all.
    private static void Turning(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        StringBuilder report)
    {
        Node3D? stage = null;
        FlightController? rotor = null;
        FlightController? propeller = null;
        FlightController? parked = null;
        GroundShadowPass? pass = null;
        var players = new List<Vector3> { new(0f, LowAltitude, 0f) };
        try
        {
            stage = EmptyStage.Build(collision: true).Root;
            ctx.Host.AddChild(stage);
            rotor = Rig(ctx, planesGamez, textures, human: false, new Vector3(0f, LowAltitude, 0f),
                RotorPlane, spinning: true);
            propeller = Rig(ctx, planesGamez, textures, human: false,
                new Vector3(40f, LowAltitude, 0f), ctx.PlaneName, spinning: true);
            parked = Rig(ctx, planesGamez, textures, human: false,
                new Vector3(80f, LowAltitude, 0f), RotorPlane);
            var rigs = new List<FlightController> { rotor, propeller };
            var panes = new List<PlayerRig> { Pane(0, null, 0u) };
            pass = GroundShadowPass.Build(ctx.Host, () => rigs, () => players, () => panes,
                () => WeatherRig.DefaultSunlightRgb);
            if (pass == null)
                return;
            pass.Tick();

            var shape = pass.ShapeFor(rotor);
            ctx.Check(shape != null, $"the Hoplite casts a shadow with a silhouette of its own");
            if (shape is not { } rotorShape)
                return;
            report.AppendLine($"{RotorPlane}: {rotorShape.TurningCount} turning groups of the shape, {rotorShape.TriangleCount} triangles in all");
            ctx.Check(rotorShape.TurningCount > 0,
                $"its blur discs are held apart from the still airframe n={rotorShape.TurningCount}");

            // Nothing turned, nothing moves: the raster itself is steady, so the reading below is
            // the rotor and not the instrument.
            ctx.Same(0, Turned(pass, rotor, 0f), $"a pass that turns nothing rebuilds the same mask");

            int turned = Turned(pass, rotor, PropStep);
            int still = pass.ShapeFor(propeller) != null ? Turned(pass, propeller, PropStep) : -1;
            report.AppendLine($"after {PropStep:0.00} s of rotor: {turned} texels of the Hoplite's mask move, {still} of the fixed-wing propeller's");
            ctx.Check(turned >= TurnedTexels,
                $"the rotor's shadow turns with the rotor n={turned} texels against {TurnedTexels}");
            ctx.Check(still >= 0 && still <= StillTexels,
                $"and a full circular propeller disc moves no outline n={still} texels against {StillTexels}");

            // One aircraft at a time, so each reading is that airframe's own: the same Hoplite
            // with its discs turning and with the still disc the exterior build keeps.
            Alone(pass, rigs, rotor, $"{RotorPlane}, blur discs turning", report);
            Alone(pass, rigs, parked, $"{RotorPlane}, still disc", report);
            Alone(pass, rigs, propeller, $"{ctx.PlaneName}, blur discs turning", report);
        }
        finally
        {
            pass?.Free();
            rotor?.Free();
            propeller?.Free();
            parked?.Free();
            stage?.Free();
        }
    }

    // C1's own terrain: the same placement, with the surface height coming from the world's
    // colliders rather than from a known plane.
    private static void Terrain(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        StringBuilder report)
    {
        ctx.WithWorld("C1", collision: true, _ =>
        {
            var space = ctx.Host.GetWorld3D().DirectSpaceState;
            Vector3? found = null;
            float surfaceY = 0f;
            // The HIGHEST surface of the probed set, not the first: C1's sea sits at y=0, and a
            // shadow read off a level surface cannot tell a real height from a hardcoded one.
            foreach (var candidate in TerrainProbes())
            {
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    candidate with { Y = 4000f }, candidate with { Y = -1000f },
                    CollisionLayers.World));
                if (hit.Count == 0)
                    continue;
                float y = ((Vector3)hit["position"]).Y;
                if (found != null && y <= surfaceY)
                    continue;
                surfaceY = y;
                found = candidate;
            }

            ctx.Check(found != null, $"C1's built colliders answer a downward ray somewhere over the map");
            ctx.Check(surfaceY > 1f,
                $"and the probed set reaches ground above C1's sea level surface={surfaceY:0.00} m");
            if (found is not { } ground)
                return;

            FlightController? ai = null;
            GroundShadowPass? pass = null;
            try
            {
                var at = ground with { Y = surfaceY + MidAltitude };
                ai = Rig(ctx, planesGamez, textures, human: false, at);
                var rigs = new List<FlightController> { ai };
                var players = new List<Vector3> { at };
                var panes = new List<PlayerRig> { Pane(0, null, 0u) };
                pass = GroundShadowPass.Build(ctx.Host, () => rigs, () => players, () => panes,
                    () => WeatherRig.DefaultSunlightRgb);
                pass?.Tick();
                var quad = pass?.QuadFor(ai);
                ctx.Check(quad != null, $"an aircraft {MidAltitude:0} m over C1's terrain casts a shadow");
                if (quad is not { } placed)
                    return;
                var shadow = placed.GlobalPosition;
                report.AppendLine($"C1 at ({at.X:0}, {at.Y:0.0}, {at.Z:0}) over a surface at {surfaceY:0.00}: shadow ({shadow.X:0.00}, {shadow.Y:0.00}, {shadow.Z:0.00})");
                ctx.Check(Mathf.Abs(shadow.Y - surfaceY) <= PlaceTolerance,
                    $"on the surface the probe read, not on a level guess y={shadow.Y:0.00} surface={surfaceY:0.00}");
                ctx.Check(Mathf.Abs(shadow.X - at.X) <= PlaceTolerance
                    && Mathf.Abs(shadow.Z - at.Z) <= PlaceTolerance,
                    $"and directly beneath the aircraft ({shadow.X:0.00}, {shadow.Z:0.00}) against ({at.X:0}, {at.Z:0})");
            }
            finally
            {
                pass?.Free();
                ai?.Free();
            }
        });
    }

    // The graphics-mode gate: the shadow is the original's own drawing, and enhanced mode lights
    // and shadows the world itself, so nothing is built there at all.
    private static void Gate(TestContext ctx, StringBuilder report)
    {
        var none = new List<FlightController>();
        var nobody = new List<Vector3>();
        var unwatched = Array.Empty<PlayerRig>();
        GraphicsMode.Resolve(GraphicsMode.EnhancedWord);
        var absent = GroundShadowPass.Build(ctx.Host, () => none, () => nobody, () => unwatched,
            () => WeatherRig.DefaultSunlightRgb);
        ctx.Check(absent == null, $"enhanced graphics mode builds no ground-shadow pass");
        GraphicsMode.Resolve(GraphicsMode.Default);
        var present = GroundShadowPass.Build(ctx.Host, () => none, () => nobody, () => unwatched,
            () => WeatherRig.DefaultSunlightRgb);
        ctx.Check(present != null, $"and original graphics mode builds one");
        report.AppendLine($"graphics gate: enhanced={(absent == null ? "no pass" : "a pass")}, original={(present == null ? "no pass" : "a pass")}");
        present?.Free();
    }

    // Where to look for C1 ground: a coarse grid over the map rather than one named spot, so the
    // suite does not depend on any particular place still being an island.
    private static IEnumerable<Vector3> TerrainProbes()
    {
        const float reach = 8000f;
        const float step = 1000f;
        for (float x = -reach; x <= reach; x += step)
        {
            for (float z = -reach; z <= reach; z += step)
                yield return new Vector3(x, 0f, z);
        }
    }
}
