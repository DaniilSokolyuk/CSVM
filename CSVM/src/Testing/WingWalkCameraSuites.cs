using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over CM02's capture cutscene framing: the mission's own capture definition
/// played through the runtime on a realtime clock, with the aircraft it belongs to spawned as a
/// roster block, reading where the definition puts <c>camera1</c>. The camera rides the wing walk's
/// moving frame, which the capture poses onto the captured aeroplane, so the reading separates a
/// shot on that aeroplane from one parked at the world origin. Decode:
/// docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class WingWalkCameraSuites
{
    // CM02's story position and the airframe the player flies into the capture.
    private const int Cm02Seq = 1;
    private const string StartPlane = "player_bhawk";

    // The captured aeroplane's roster block, the animation the capture's approach trigger starts,
    // and the moving frame the wing walk's own definition drives.
    private const string CaptureBlock = "britbalmoral_1";
    private const string CaptureAnim = "ww_balmoral1";
    private const string WalkFrame = "wingwalk_parent";

    // The played leg's frame: past the wing walk's own 19.25 s motion, sampled once a second.
    private const float StepDt = 1f / 60f;
    private const float PlayBudgetS = 24f;
    private const int SamplesPerSecond = 60;

    // How far the shot may open from the aeroplane it is about, and how far the wing walk's frame
    // may land from that aeroplane. The authored camera scripts open ~30 m off the frame and the
    // frame is posed onto the aeroplane itself, so a composed shot reads well inside both and one
    // playing in world space reads thousands of metres out.
    private const float OpeningRangeM = 100f;
    private const float FrameToleranceM = 5f;

    // The wing walk's own authored motion, which is how long the shot lasts. A definition that
    // completes instantly satisfies every end-state check, so the episode's DURATION is asserted
    // beside the framing (INSTR-21).
    private const float WalkRunTimeS = 19.25f;
    private const float RunTimeToleranceS = 1f;

    // The frame's rotate is an AT_NODE pose onto the captured aeroplane's own basis (no offset
    // authored), so once read it should reproduce that heading almost exactly.
    private const float HeadingToleranceDeg = 2f;

    // How far the aeroplane is rolled while the capture fires. An AT_NODE_XYZ rotate reads the
    // rotation the host was PLACED at, never the one it is banking at, so the walk frame stays
    // level through this; a frame that composed off the flown basis reads the whole 90 degrees.
    private const float HostRollDeg = 90f;
    private const float RollToleranceDeg = 2f;

    // The capture's own cast: the two aeroplane parts its SI scripts drive (the enemy pilot's body
    // and head, inside the chapter's own copy of the vehicle, which only the spawned rig can
    // answer for), the canopy its from-to motion opens, and the two aircraft-archive figures the
    // wing walk adds under the frame.
    private static readonly string[] VehicleParts = { "body", "head", "hatch" };

    private static readonly string[] Figures = { "rope_ladder", "pickup_cpilot" };

    /// <summary>Plays CM02's own capture definition over that mission's built world and reads the
    /// camera it poses: the shot has to sit on the captured aeroplane rather than at the world
    /// origin, which is where a wing-walk frame with no host to hang off lands.</summary>
    // BL-542: the capture cutscene's camera rides the wing walk's moving frame, and that frame
    // is posed onto the aeroplane the capture belongs to.
    [Suite("campaign-wingwalk-camera",
        "CM02's capture cutscene framing over that mission's BUILT world: the mission's own "
        + "capture definition started through the mission-trigger seam its approach table "
        + "starts it with, played on a realtime clock as isolated camera choreography, "
        + "with the captured aeroplane spawned from its own roster block at the position the "
        + "mission authors it -- far from the world origin, so a shot posed off nothing is not "
        + "mistaken for a framed one -- and camera1 read once a second against that "
        + "aeroplane's own position")]
    internal static void WingWalkCamera(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
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

        ctx.WriteArtifact($"test-wingwalk-camera-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"played {chapter}/{folder}'s '{CaptureAnim}' and read the camera it poses");
    }

    // Every definition the capture can reach, its own plus the CALL_ANIMATION closure: what the
    // session's own host answers for, and the only way the called wing walk's codes are hosted.
    internal static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
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

    internal static RosterSpawnPlan? PlanNamed(TestContext ctx, string chapter, string folder,
        string name)
    {
        var blocks = AiSkills.LoadRoster(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder));
        var plan = CampaignRosterPlan.Build(blocks, VehicleDefs.Load(ctx.ZrdrPath),
            Array.Empty<AiNet>());
        foreach (var spawn in plan.Spawns)
        {
            if (string.Equals(spawn.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return spawn;
            }
        }

        return null;
    }

    internal static FlightRoster BuildRoster(TestContext ctx, TestWorld world, string chapter,
        TextureArchive textures, ProjectilePool pool, IReadOnlyList<PlayerRig> rigs, Vector3 near)
    {
        var spec = SessionSpec.Parse(new[] { $"--plane={StartPlane}" });
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
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
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                PauseState = new PauseState(),
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
            }, new WingWalkStarts(near));
    }

    internal static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
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

    private static void Drive(TestContext ctx, TestWorld world, string chapter, string folder,
        StringBuilder report)
    {
        var plan = PlanNamed(ctx, chapter, folder, CaptureBlock);
        if (plan == null)
        {
            ctx.Check(false, $"{chapter}/{folder}'s roster carries a '{CaptureBlock}' block for the capture to belong to");
            return;
        }

        report.AppendLine($"'{CaptureBlock}' authored at {plan.Position} yaw {plan.YawDeg:0.#} " +
            $"flying '{plan.PlaneNode}'");
        // ⚠ Far from the gamez origin on purpose: a shot posed off nothing lands at the origin, and
        // a subject spawned near it would read as correctly framed (DIAG-19).
        ctx.Check(plan.Position.Length() > OpeningRangeM * 10f,
            $"and authored far enough from the world origin that a shot parked there is not mistaken for a framed one");

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
            roster = BuildRoster(ctx, world, chapter, textures, pool, rigs, plan.Position);
            roster.BuildPlayers(rigs);
            // Off world-zero on purpose: the mission authors this block at yaw 0, which a rotate
            // that fell through to an absolute zero-euler pose would satisfy by accident, so the
            // heading check below needs a facing that fallback would not land on.
            var aimDir = new Basis(Vector3.Up, Mathf.DegToRad(37f)) * Vector3.Forward;
            var aim = plan.Position + aimDir;
            var captured = roster.SpawnAi(CampaignRosterPlan.SpawnFor(plan, plan.Position, aim,
                AiPilot.HoldingCourse(plan.Position, aim)));
            if (captured == null)
            {
                ctx.Check(false, $"the '{CaptureBlock}' block spawns an aircraft to capture");
                return;
            }

            int grafted = RosterMarkers.Attach(world.Gamez, world.Session.Builder.Scene,
                world.Runtime, CaptureBlock, captured);
            report.AppendLine($"spawned '{CaptureBlock}' at {captured.WorldPosition}, " +
                $"{grafted} marker graft(s)");
            report.AppendLine($"before: {Census(world, captured)}");
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

    // The capture as a flown session runs it: started through the mission-trigger seam the approach
    // table starts it with, on a realtime clock; this leg isolates the authored camera choreography.
    private static void Play(TestContext ctx, TestWorld world, PlayerRig rig,
        CutsceneController cutscene, FlightRoster roster, FlightController captured,
        StringBuilder report)
    {
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(ClosureOf(world, CaptureAnim));
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);
            world.Runtime.CallbackHost = cutscene.Host;
            var camera = First(world.Runtime.FindNodes(CutsceneController.CameraNode));
            ctx.Check(camera != null,
                $"the built world stands up the '{CutsceneController.CameraNode}' node the capture poses");
            var subject = captured.WorldPosition;
            // ⚠ Held rolled on every step, not set once: the aeroplane flies itself on this leg and
            // rewrites its own transform each tick, so a one-shot bank is gone by the pose event.
            var banked = new Basis(captured.GlobalBasis.Z.Normalized(),
                Mathf.DegToRad(HostRollDeg)).Orthonormalized() * captured.GlobalBasis;
            var parts = PartsOf(captured);
            var partsAtStart = new Dictionary<string, Transform3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, node) in parts)
            {
                partsAtStart[name] = node.Transform;
            }

            world.Runtime.OpenResolutionCensus();
            // ⚠ Bank BEFORE the trigger. The capture poses its frame inside the start dispatch, so
            // a bank first applied on the first advance is a frame late and the pose never sees it.
            captured.GlobalBasis = banked;
            world.Runtime.PlayMissionTrigger(CaptureAnim);

            // ⚠ Read only while the shot is COMPOSED, which is while `camera1` hangs under the wing
            // walk's frame. The episode's own end detaches it back to the world root inside the
            // last advance, and a sample past that reads the park rather than the shot.
            float openedAt = -1f;
            float framedAt = -1f;
            float composedTo = 0f;
            bool nearerTheOriginEver = false;
            int samples = 0;
            float frameHeadingDeg = float.NaN;
            float capturedHeadingDeg = float.NaN;
            float frameRollDeg = float.NaN;
            float hostRollDeg = float.NaN;
            var figuresFramed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var partsShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
            {
                clock.BeginFrame(StepDt);
                captured.GlobalBasis = banked;
                // ⚠ Read the bank HERE. The aeroplane's own tick below puts its flown attitude
                // back, so a read after it reports the flight model, not the pose event's host.
                float bankNow = RollDegOf(captured.GlobalBasis);
                world.Runtime.Advance(StepDt);
                // ⚠ Read the shot BEFORE the host ticks. The handoff parks `camera1` back under
                // the world root inside that tick, so a read after it takes the park for the shot
                // on the very step the episode ends.
                bool composed = cutscene.Playing && camera != null && IsUnder(camera, WalkFrame);
                var eye = camera?.GlobalTransform.Origin ?? Vector3.Zero;
                float frameOff = composed ? FrameOffset(world, subject) : -1f;
                cutscene.Tick();
                if (!composed)
                {
                    continue;
                }

                float now = i * StepDt;
                composedTo = now;
                float off = eye.DistanceTo(subject);
                if (openedAt < 0f)
                {
                    openedAt = off;
                    framedAt = frameOff;
                    frameHeadingDeg = FrameHeadingDeg(world);
                    capturedHeadingDeg = AiPilot.HeadingDegOf(captured.NoseDirection);
                    frameRollDeg = FrameRollDeg(world);
                    hostRollDeg = bankNow;
                }

                foreach (string part in VehicleParts)
                {
                    if (parts.TryGetValue(part, out var node) && node.IsVisibleInTree())
                    {
                        partsShown.Add(part);
                    }
                }

                foreach (string figure in Figures)
                {
                    if (First(world.Runtime.FindNodes(figure)) is { } node && node.IsVisibleInTree()
                        && IsUnder(node, WalkFrame))
                    {
                        figuresFramed.Add(figure);
                    }
                }

                if (off > eye.Length())
                {
                    nearerTheOriginEver = true;
                }

                if (i % SamplesPerSecond != 0)
                {
                    continue;
                }

                samples++;
                report.AppendLine($"t={now,5:0.0} s camera1 {eye} " +
                    $"{off:0} m off the capture, {eye.Length():0} m off the world origin; " +
                    $"frame {FrameOrigin(world)}");
            }

            foreach (string line in world.Runtime.ResolutionLines())
            {
                report.AppendLine($"resolution: {line}");
            }

            world.Runtime.CloseResolutionCensus();
            report.AppendLine($"after: {Census(world, captured)}");
            report.AppendLine($"shot composed for {composedTo:0.##} s over {samples} sample(s), " +
                $"opened {openedAt:0} m off the capture with the walk frame {framedAt:0} m off it, " +
                $"nearer-the-origin-ever={nearerTheOriginEver}");
            report.AppendLine($"'{WalkFrame}' heading {frameHeadingDeg:0.#}° versus '{CaptureBlock}' " +
                $"heading {capturedHeadingDeg:0.#}°");
            ctx.Check(samples > 0, $"the played capture composes its shot inside the wing walk's own frame");
            ctx.Check(Mathf.Abs(composedTo - WalkRunTimeS) < RunTimeToleranceS,
                $"and holds it for the walk's own {WalkRunTimeS:0.##} s rather than completing in the frame it started (read {composedTo:0.##} s)");
            ctx.Check(framedAt >= 0f && framedAt < FrameToleranceM,
                $"the capture poses its '{WalkFrame}' onto the aeroplane it belongs to, within {FrameToleranceM:0} m of it");
            ctx.Check(openedAt >= 0f && openedAt < OpeningRangeM,
                $"so the shot opens within {OpeningRangeM:0} m of that aeroplane rather than at the world origin");
            ctx.Check(!nearerTheOriginEver,
                $"and never sits nearer the world origin than the aeroplane it is filming, which is the water the capture was seen over");
            // An AT_NODE rotate that fell through to an absolute world-axis zero would read a
            // constant heading regardless of the aeroplane's own, so this only passes once the
            // handler reaches the compiled spelling too.
            ctx.Check(!float.IsNaN(frameHeadingDeg) && !float.IsNaN(capturedHeadingDeg)
                && Mathf.Abs(Mathf.Wrap(frameHeadingDeg - capturedHeadingDeg, -180f, 180f)) < HeadingToleranceDeg,
                $"and the wing-walk frame faces the captured aeroplane's own heading, within {HeadingToleranceDeg:0}° of it");

            report.AppendLine($"host rolled {hostRollDeg:0.#}° when the capture fired, " +
                $"'{WalkFrame}' roll {frameRollDeg:0.#}°");
            // METHOD-15: the perturbation has to have taken before its absence means anything.
            ctx.Check(Mathf.Abs(hostRollDeg) > HostRollDeg - 10f,
                $"the aeroplane really is rolled about {HostRollDeg:0}° at the instant the capture poses its frame");
            ctx.Check(!float.IsNaN(frameRollDeg) && Mathf.Abs(frameRollDeg) < RollToleranceDeg,
                $"and the frame stays level anyway, within {RollToleranceDeg:0}° of upright, because an AT_NODE_XYZ rotate reads the rotation the aeroplane was placed at");

            var moved = new List<string>();
            foreach (var (name, node) in parts)
            {
                if (partsAtStart.TryGetValue(name, out var was)
                    && (!was.Origin.IsEqualApprox(node.Transform.Origin)
                        || !was.Basis.IsEqualApprox(node.Transform.Basis)))
                {
                    moved.Add(name);
                }
            }

            var readings = new List<string>();
            foreach (string part in VehicleParts)
            {
                readings.Add($"{part} in-rig={parts.ContainsKey(part)} " +
                    $"shown={partsShown.Contains(part)} driven={moved.Contains(part)}");
            }

            report.AppendLine($"vehicle parts ({parts.Count} in the rig): {string.Join(", ", readings)}");
            foreach (string part in VehicleParts)
            {
                ctx.Check(parts.ContainsKey(part) && moved.Contains(part) && partsShown.Contains(part),
                    $"the capture's '{part}' resolves inside the aeroplane the mission spawned, is drawn while the shot is composed, and its authored motion drives it");
            }

            report.AppendLine($"figures framed: [{string.Join(", ", figuresFramed)}]");
            foreach (string figure in Figures)
            {
                ctx.Check(figuresFramed.Contains(figure),
                    $"the wing walk's '{figure}' is staged, visible and under the walk frame while the shot is composed");
            }
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }
    }

    private static string FrameOrigin(TestWorld world)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        if (found.Count == 0)
        {
            return "absent";
        }

        var parts = new List<string>();
        foreach (var node in found)
        {
            parts.Add($"{node.GlobalTransform.Origin}");
        }

        return string.Join(" ", parts);
    }

    private static bool IsUnder(Node3D node, string ancestorName)
    {
        for (var p = node.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D)
        {
            if (string.Equals(AnimRuntime.NameOf(p), ancestorName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // How far the wing walk's frame sits from the aeroplane the capture belongs to; -1 when the
    // frame is not in the world at all, which is the reading before it was stood up.
    private static float FrameOffset(TestWorld world, Vector3 subject)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        return found.Count > 0 ? found[0].GlobalTransform.Origin.DistanceTo(subject) : -1f;
    }

    // The frame's own facing (nose -Z, this project's convention), NaN when the frame is not in
    // the world yet.
    private static float FrameHeadingDeg(TestWorld world)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        return found.Count > 0
            ? AiPilot.HeadingDegOf(-found[0].GlobalTransform.Basis.Z)
            : float.NaN;
    }

    // The frame's roll about its own nose, NaN when the frame is not in the world yet.
    private static float FrameRollDeg(TestWorld world)
    {
        var found = world.Runtime.FindNodes(WalkFrame);
        return found.Count > 0 ? RollDegOf(found[0].GlobalTransform.Basis) : float.NaN;
    }

    private static float RollDegOf(Basis basis) =>
        Mathf.RadToDeg(basis.Orthonormalized().GetEuler(EulerOrder.Yxz).Z);

    // The aeroplane's own parts by their gamez name, what the capture's `body`/`head`/`hatch`
    // events have to reach, since the chapter's copy of this vehicle is never placed.
    private static Dictionary<string, Node3D> PartsOf(Node3D rig)
    {
        var map = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta))
                {
                    map.TryAdd(n3d.GetMeta(AnimRuntime.NameMeta).AsString(), n3d);
                }

                Walk(child);
            }
        }

        Walk(rig);
        return map;
    }

    // What the capture's node table can actually reach: every name the definition addresses, with
    // how many nodes answer it and where the first one sits. The three vehicle parts are counted
    // inside the spawned aeroplane, which is the only place they exist.
    private static string Census(TestWorld world, Node3D rig)
    {
        var parts = new List<string>();
        foreach (string name in new[] { CaptureBlock, WalkFrame, "camera1", "player" })
        {
            var found = world.Runtime.FindNodes(name);
            parts.Add(found.Count == 0
                ? $"'{name}' 0"
                : $"'{name}' {found.Count} at {found[0].GlobalTransform.Origin} visible={found[0].IsVisibleInTree()}");
        }

        foreach (string name in Figures)
        {
            var found = world.Runtime.FindNodes(name);
            parts.Add(found.Count == 0
                ? $"'{name}' 0"
                : $"'{name}' {found.Count} visible={found[0].IsVisibleInTree()}");
        }

        var own = PartsOf(rig);
        foreach (string name in VehicleParts)
        {
            parts.Add(own.TryGetValue(name, out var node)
                ? $"'{name}' in-rig visible={node.IsVisibleInTree()}"
                : $"'{name}' not in the rig");
        }

        return string.Join(", ", parts);
    }

    private static Node3D? First(IReadOnlyList<Node3D> found) => found.Count > 0 ? found[0] : null;

    // The player flown alongside the captured aeroplane rather than at the gamez origin, so the
    // camera reading below separates the two places instead of finding them the same.
    private sealed class WingWalkStarts : IFlightStarts
    {
        private readonly Vector3 _near;

        internal WingWalkStarts(Vector3 near) => _near = near;

        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = _near + new Vector3(i * 60f, 0f, 200f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 90f);
            }

            return starts;
        }
    }
}
