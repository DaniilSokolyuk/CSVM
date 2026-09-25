using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// C4/M02's search for Blacke, the mission whose only door out of its four search locations is a
/// <c>TRAVELERS</c> proximity read against a roster aircraft. The suite pins the authored shape,
/// then drives it over the mission's own BUILT world with a real roster: the warp places him, the
/// spot check resolves him, and a miss reads FALSE rather than unanswerable.
/// </summary>
internal static class CampaignBlackeSearchSuites
{
    private const string Chapter = "C4";

    private const string Mission = "M02";

    // Blacke's autogyro: an aiv roster block, and the reference every spot check names.
    private const string Gyro = "bhatgyro_1";

    // The objective that hides him, five seconds in.
    private const int WarpObjective = 23;

    // The one objective the four spot checks wake, and the mission's PRIMARY 1.
    private const int FoundObjective = 24;

    private const float SpotRadiusM = 500f;

    // What each search location naps its "he is not here" radio line by, and so the whole width of
    // the window a spot check has to read true in.
    private const float SilenceNapS = 2f;

    private const float StepDt = 1f / 60f;

    // How close to an authored waypoint the warped aircraft has to land. It is placed exactly, so
    // this is float slop and nothing else.
    private const float PlacedToleranceM = 1f;

    // The seed the world phase draws the waypoint with, so the suite knows which of the four the
    // mission picked and can assert the placement rather than describe it.
    private const int WarpSeed = 1;

    // How far behind the host the late guest's graph starts, so the host's pick lands before that
    // guest's own directive runs. Several times the link's latency.
    private const float LateGraphS = 1f;

    // The warp run: past the directive's 5 s on the late guest, with time for the pick to land.
    private const float WarpRunS = 7f;

    // The mission's own opening cutscene, shared with every other story mission but for this one
    // authoring no re-placement callback in its RESET_STATE, so the hand-back reads
    // StagePlayerAircraft's captured pose rather than a definition-posed one.
    private const string IntroAnim = "generic_intro";

    private const float IntroStepDt = 1f / 60f;

    // generic_intro's own scripts run under 40 s on either branch; the budget clears that with
    // room to spare, and the loop leaves as soon as the episode hands off.
    private const float IntroDriveBudgetS = 60f;

    // How close the hand-back has to land to the aiv.zrd 'player' block's authored spawn.
    private const float IntroPlacedToleranceM = 5f;

    // The clearance the handed-back aeroplane needs over whatever surface is under it.
    private const float IntroClearanceMinM = 10f;

    // The surface probe: cast from this far above a point to this far below it, several chapter
    // reliefs deep so "no hit" means off the map rather than out of reach.
    private const float IntroCastUpM = 2000f;

    private const float IntroCastDownM = 8000f;

    // The four search locations, and for each the spot check it wakes and the radio line that
    // kills that spot check two seconds later (targets.zrd names them Jimmy's Bar, McCoy's Diner,
    // the Shangri-La dancehall and the C9 brothel).
    private static readonly (int Zone, int Spot, int Silence)[] Locations =
    {
        (2, 31, 35),
        (3, 32, 36),
        (4, 33, 37),
        (6, 34, 38),
    };

    // CM17 could not be finished: WARP_VEHICLE was a named no-op and a node-form TRAVELERS
    // could not see a roster aircraft, so all four of the mission's spot checks went
    // unanswered and its PRIMARY 1 never woke.
    [Suite("campaign-blacke-search",
        "CM17's search for Blacke over C4/M02's own BUILT world: OBJECTIVE23 hides "
        + "bhatgyro_1 on one of four authored waypoints, each of the four search locations "
        + "wakes a 500 m spot check and naps the radio line that kills it two seconds later, "
        + "and nothing but a spot check wakes the mission's PRIMARY 1; the aircraft is a "
        + "roster block with no world node of its name, the warp places it while it is still "
        + "deactivated, a player half a radius away completes the check and wakes the "
        + "primary, and a player four radii away reads FALSE rather than unanswered")]
    internal static void CampaignBlackeSearch(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        if (MissionAt(ctx, Chapter, Mission) is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(missionZrdr);
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        var points = CheckAuthored(ctx, script, report);

        var skills = AiSkills.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, blocks, skills, points, missionZrdr, chapterZrdr,
                texturesPath, report));

        ctx.WriteArtifact($"test-campaign-blacke-search-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: Blacke is warped, spotted at 500 m, and a miss reads false");
    }

    [Suite("net-blacke-warp",
        "C4/M02's WARP_VEHICLE over a 100 ms, 10 per cent lossy loopback: the host's director draws "
        + "Blacke's waypoint and sends its index; two guest directors whose own world streams would "
        + "draw another waypoint draw nothing and put him on the host's, one whose directive runs "
        + "before the pick lands and one whose graph runs a second late, so the pick is waiting; "
        + "and a control director drawing for itself lands elsewhere")]
    internal static void NetBlackeWarp(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");
        if (MissionAt(ctx, Chapter, Mission) is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(missionZrdr);
        var points = Numbered(script, WarpObjective)?.Warp?.Points ?? new List<WarpPoint>();
        if (points.Count < 2)
        {
            throw new SuiteSkippedException($"OBJECTIVE{WarpObjective} authors fewer than two waypoints");
        }

        // A guest seed whose own draw is another waypoint, so a guest that drew would be caught.
        int hostPick = new Random(WarpSeed).Next(points.Count);
        int guestSeed = WarpSeed + 1;
        while (new Random(guestSeed).Next(points.Count) == hostPick)
        {
            guestSeed++;
        }

        var report = new StringBuilder();
        report.AppendLine($"host draws waypoint {hostPick + 1} of {points.Count}; a guest drawing on seed {guestSeed} would take {new Random(guestSeed).Next(points.Count) + 1}");
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            DriveWarp(ctx, world, script, mission, blocks, skills, points, hostPick, guestSeed,
                missionZrdr, chapterZrdr, texturesPath, report));
        ctx.WriteArtifact($"test-net-blacke-warp-{Chapter}-{Mission}.txt", report.ToString());
    }

    // generic_intro's RESET_STATE reparents 'player' back to the world root and raises no 951, so
    // CutsceneController.Restore hands the aeroplane back through StagePlayerAircraft's captured
    // home rather than through a definition-posed placement (no 951 names 'player' here at all).
    // The suite drives the real intro over CM17's own BUILT world, collision up, and reads where
    // that leaves the pilot against the aiv.zrd 'player' block's authored spawn and the terrain
    // under it.
    [Suite("blacke-intro-handoff",
        "C4/M02's own generic_intro over its BUILT world, collision up: the definition's "
        + "RESET_STATE authors no re-placement callback, so the hand-back reads "
        + "StagePlayerAircraft's captured pose, and the episode has to leave the aeroplane "
        + "within tolerance of the aiv.zrd 'player' block's authored spawn, clear of the "
        + "terrain measured under it")]
    internal static void BlackeIntroHandoff(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var spawn = PlayerPose(blocks);
        var report = new StringBuilder();
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(Chapter, collision: true, Mission,
                world => DriveIntroHandoff(ctx, world, spawn, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-blacke-intro-handoff-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: {IntroAnim} hands back onto the authored spawn, clear of terrain");
    }

    // A hand-back naming no re-placement (this mission's own generic_intro, and any other episode
    // that raises no 951) was leaving the flight model at the zero speed and throttle Held pins it
    // at for every held step, rather than the airspeed and power the aircraft actually carried the
    // instant staging began. No mission data or cutscene definition needed: this is StageAt's own
    // contract, isolated with a bare rig and a bare marker.
    [Suite("cutscene-handoff-speed",
        "a hand-back naming no re-placement resumes the airspeed and throttle the aeroplane held "
        + "before a cutscene staged it, rather than the zero speed Held pins the model at for "
        + "every held step")]
    internal static void CutsceneHandoffSpeed(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        Node3D? marker = null;
        try
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            var pos = new Vector3(0f, 500f, 0f);
            var rig = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                PlayerIndex = FlightRoster.ShooterIdBase - 1,
                IsHumanPiloted = true,
                Projectiles = pool,
                UseKeyboard = false,
                PadDevices = Array.Empty<int>(),
                AllowPause = false,
                Team = AimAssist.PlayerTeam,
                Name = "CutsceneHandoffSpeedPlayer",
            };
            rig.AddChild(model);
            ctx.Host.AddChild(rig);
            rig.Setup(new FlightModel(stats), null, new CamParams(), pos, pos + Vector3.Forward,
                spawnThrottle: 0.8f, spawnSpeed: 180f);
            float speedBefore = rig.WorldVelocity.Length();

            marker = new Node3D { Name = "CutsceneHandoffSpeedMarker" };
            ctx.Host.AddChild(marker);
            marker.GlobalTransform = new Transform3D(Basis.Identity, pos + (Vector3.Up * 2000f));

            rig.StageAt(marker.GlobalTransform); // staging begins: captures the pose and the model
            rig.Held = true;
            for (int i = 0; i < 3; i++)
            {
                rig.SimStep(1f / 60f); // a held step pins the model at zero speed/throttle
            }

            rig.Held = false; // ApplyOutOfFlight(false), ahead of StagePlayerAircraft in Restore
            rig.StageAt(null); // hand-back, no re-placement named

            float speedAfter = rig.WorldVelocity.Length();
            ctx.Check(speedBefore > 100f,
                $"the rig spawns at its authored speed ({speedBefore:0.#} m/s)");
            ctx.Check(speedAfter > speedBefore * 0.9f,
                $"the hand-back resumes near that speed ({speedAfter:0.#} m/s) rather than the zero Held pinned it at");
        }
        finally
        {
            marker?.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    private static void DriveIntroHandoff(TestContext ctx, TestWorld world,
        (Vector3 Position, Vector3 Forward) spawn, StringBuilder report)
    {
        if (world.Session.Aircraft is not { PlayerMarker: not null })
        {
            ctx.Check(false, $"the world build staged the '{AircraftStage.PlayerNode}' marker the intro poses");
            return;
        }

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var rig = HumanRig(ctx, planesGamez, textures, pool, spawn.Position,
                spawn.Position + spawn.Forward);
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
            world.Runtime.CallbackHost = cutscene.Host;
            // The bootstrap raised this before a host or a rig existed, which is the one thing the
            // session's own BindRigs re-applies; the suite has to say it for the same reason
            // (IntroAircraftSuites).
            cutscene.Host(CutsceneController.CodeOutOfFlight, IntroAnim);

            float? surfaceUnderSpawn = SurfaceUnder(world, spawn.Position);
            float played = 0f;
            var marker = world.Session.Aircraft!.PlayerMarker!;
            var lastMarkerPose = Transform3D.Identity;
            float markerFarthestFromSpawn = 0f;
            float sampledAt = -1f;
            for (float t = 0f; t < IntroDriveBudgetS && (played == 0f || cutscene.Playing);
                t += IntroStepDt)
            {
                world.Runtime.Advance(IntroStepDt);
                cutscene.Tick();
                played += cutscene.Playing ? IntroStepDt : 0f;
                if (cutscene.Playing)
                {
                    lastMarkerPose = AnimRuntime.WorldTransform(marker, out _);
                    markerFarthestFromSpawn = Mathf.Max(markerFarthestFromSpawn,
                        lastMarkerPose.Origin.DistanceTo(spawn.Position));
                    if (t - sampledAt >= 1f)
                    {
                        sampledAt = t;
                        string parent = marker.GetParent() is Node3D p ? AnimRuntime.NameOf(p) ?? "?" : "-";
                        report.AppendLine($"  t={t:0.00} parent={parent} local={marker.Position} "
                            + $"world={lastMarkerPose.Origin}");
                    }
                }
            }

            var handback = rig.WorldPosition;
            float? surfaceUnderHandback = SurfaceUnder(world, handback);
            float offSpawn = handback.DistanceTo(spawn.Position);
            report.AppendLine($"'{IntroAnim}' episode ran {played:0.##} s, "
                + $"codes {string.Join("/", cutscene.Codes)}");
            report.AppendLine($"authored spawn {spawn.Position}, "
                + $"surface under it {Height(surfaceUnderSpawn)}");
            report.AppendLine($"'{AircraftStage.PlayerNode}' marker's last pose while playing: "
                + $"{lastMarkerPose.Origin}, {markerFarthestFromSpawn:0.#} m from spawn at its farthest");
            report.AppendLine($"handback pose {handback}, surface under it {Height(surfaceUnderHandback)}, "
                + $"{offSpawn:0.#} m from the authored spawn");
            ctx.Check(played > 0f && !cutscene.Playing,
                $"'{IntroAnim}' runs to its handoff with the cutscene host answering its codes");
            ctx.Check(offSpawn < IntroPlacedToleranceM,
                $"the hand-back lands within {IntroPlacedToleranceM:0} m of the aiv.zrd 'player' block's authored spawn");
            ctx.Check(surfaceUnderHandback is { } surface && handback.Y - surface > IntroClearanceMinM,
                $"and above the surface measured under it, rather than through the single-sided terrain the pilot cannot climb back out of");
        }
        finally
        {
            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The surface under a point, cast from well above it. Null when nothing is there at all.
    private static float? SurfaceUnder(TestWorld world, Vector3 at)
    {
        var space = world.Stage.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            at + (Vector3.Up * IntroCastUpM), at - (Vector3.Up * IntroCastDownM), CollisionLayers.World));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : null;
    }

    private static string Height(float? y) => y is { } h ? $"{h:0.#}" : "(nothing)";

    // The authored shape this suite consumes, read off the shipped file rather than restated. The
    // claim that matters is the last one: nothing but a spot check wakes the mission's PRIMARY 1,
    // so a spot check that cannot resolve its reference is a mission that cannot be finished.
    private static IReadOnlyList<WarpPoint> CheckAuthored(TestContext ctx, ObjectiveScript script,
        StringBuilder report)
    {
        var warp = Numbered(script, WarpObjective);
        ctx.Check(warp?.Warp is { } w && w.Vehicle.Equals(Gyro, StringComparison.OrdinalIgnoreCase),
            $"OBJECTIVE{WarpObjective} warps '{Gyro}'");
        var points = warp?.Warp?.Points ?? new List<WarpPoint>();
        ctx.Same(Locations.Length, points.Count,
            $"…to one of as many authored waypoints as there are search locations");
        foreach (var p in points)
        {
            report.AppendLine($"waypoint ({p.X:0},{p.Y:0},{p.Z:0}) heading {p.Heading:0} deg"
                + (p.PointName is { } name ? $" on path '{name}'" : ""));
        }

        int wakes = 0;
        foreach (var (zone, spot, silence) in Locations)
        {
            var spotter = Numbered(script, spot);
            bool named = spotter?.Travelers is { } t && t.Approaching && t.Group == null
                && string.Equals(t.WhereNode, Gyro, StringComparison.OrdinalIgnoreCase)
                && Mathf.IsEqualApprox(t.Radius, SpotRadiusM);
            ctx.Check(named,
                $"OBJECTIVE{spot} spots '{Gyro}' inside {SpotRadiusM:0} m");
            ctx.Check(Numbered(script, zone)?.WakeWhenComplete.Contains(spot) == true,
                $"…and search location OBJECTIVE{zone} is what wakes it");
            ctx.Check(Numbered(script, zone)?.NapWhenComplete is { } nap
                    && nap.Target == silence && Mathf.IsEqualApprox(nap.Seconds, SilenceNapS),
                $"…which also naps OBJECTIVE{silence} awake {SilenceNapS:0} s later");
            ctx.Check(Numbered(script, silence)?.KillWhenComplete.Contains(spot) == true,
                $"…and OBJECTIVE{silence} kills OBJECTIVE{spot} when it does");
            wakes += spotter?.WakeWhenComplete.Contains(FoundObjective) == true ? 1 : 0;
        }

        ctx.Same(Locations.Length, wakes,
            $"every spot check wakes OBJECTIVE{FoundObjective}, the mission's PRIMARY 1");
        ctx.Same(Locations.Length, WakersOf(script, FoundObjective),
            $"…and NOTHING ELSE does, so a spot check is the mission's only door");
        return points;
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, AiSkills skills,
        IReadOnlyList<WarpPoint> points, string missionZrdr, string chapterZrdr,
        string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? player = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position,
                pose.Position + pose.Forward);
            var human = player;
            var listener = pose.Position;

            director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                Player = () => human,
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });

            if (!director.Roster.TryGetValue(Gyro, out var gyro))
            {
                ctx.Check(false, $"the roster spawned '{Gyro}'");
                return;
            }

            ctx.Check(gyro.Inert, $"'{Gyro}' ships deactivated, and is warped while he still is");

            // ⚠ The reason the node-only resolve failed: the aiv block's name reaches the resolver
            // only through a gamez library root of the SAME name, and this chapter's is 'bhatgyro'.
            ctx.Same(0, world.Runtime.FindNodes(Gyro).Count,
                $"'{Gyro}' is NOT a world node, so only the roster can answer where he is");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                Gamez = world.Gamez,
                ListenerPosition = () => listener,
                PlayerAircraft = () => human,
                Rng = new Random(WarpSeed),
            });

            var graph = director.Graph;
            ctx.Check(graph != null, $"the world phase armed the objective graph");
            if (graph == null)
            {
                return;
            }

            var spawnedAt = gyro.WorldPosition;
            Advance(director, 6f);
            ctx.Check(graph.CompletedOf(WarpObjective),
                $"OBJECTIVE{WarpObjective} ran at its BEGIN_DORMANT 5 s");

            var hidAt = gyro.WorldPosition;
            var drawn = points[new Random(WarpSeed).Next(points.Count)];
            report.AppendLine($"spawned at ({spawnedAt.X:0},{spawnedAt.Y:0},{spawnedAt.Z:0}), "
                + $"hid at ({hidAt.X:0},{hidAt.Y:0},{hidAt.Z:0})");
            if (drawn.PointName is { Length: > 0 } path)
            {
                ctx.Check(director.Paths?.IsFrozen(Gyro) == false,
                    $"the drawn waypoint names path '{path}', which releases him onto it moving");
            }
            else
            {
                var want = new Vector3(drawn.X, drawn.Y, drawn.Z);
                ctx.Check(hidAt.DistanceTo(want) <= PlacedToleranceM,
                    $"WARP_VEHICLE put him on the drawn waypoint ({want.X:0},{want.Y:0},{want.Z:0})");
                ctx.Check(gyro.Inert, $"…and the placement left his INERT bit alone");
            }

            // The spot check reads the human field, so moving the listener alone leaves the
            // aeroplane the condition actually asks about back at its start point.
            Spot(ctx, director, graph, gyro, at =>
            {
                human.PlaceHeld(at, at + pose.Forward);
                listener = at;
            }, report);
        }
        finally
        {
            player?.Free();
            var members = new List<FlightController>(
                roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            textures.Dispose();
        }
    }

    // Four directors over one world, each with a roster of Blacke alone. They are the host, two
    // linked guests (one in step with the host, one a second behind) and an unlinked control.
    private static void DriveWarp(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, IReadOnlyList<(string Name, List<object?> Fields)> blocks,
        AiSkills skills, IReadOnlyList<WarpPoint> points, int hostPick, int guestSeed,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var live = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(live);
        var pose = PlayerPose(blocks);
        var human = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
        var ends = new List<WarpEnd>();
        try
        {
            WarpEnd Build(string name, int seed)
            {
                var roster = Spawner(ctx, planesGamez, textures, live);
                var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile(name), null);
                var end = new WarpEnd(name, director, roster, new CountingRandom(seed));
                ends.Add(end);
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    Player = () => human,
                    FindNodes = n => world.Runtime.FindNodes(n),
                    Spawn = (plan, at, look, pilot) => plan.Name.Equals(Gyro, StringComparison.OrdinalIgnoreCase)
                        ? roster.SpawnAi(CampaignRosterPlan.SpawnFor(plan, at, look, pilot))
                        : null,
                    Rng = new Random(1),
                });
                director.Attach(new CampaignDirector.WorldInputs
                {
                    Runtime = world.Runtime,
                    Projectiles = live,
                    Gamez = world.Gamez,
                    ListenerPosition = () => pose.Position,
                    PlayerAircraft = () => human,
                    Rng = end.Draws,
                });
                return end;
            }

            var host = Build("host", WarpSeed);
            var early = Build("early", guestSeed);
            var late = Build("late", guestSeed);
            var control = Build("control", guestSeed);
            if (ends.Any(e => e.Blacke == null || e.Director.Graph == null))
            {
                ctx.Check(false, $"every director spawned '{Gyro}' and armed its graph");
                return;
            }

            var spawnedAt = host.Blacke!.WorldPosition;
            var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(WarpSeed));
            var seats = new NetSeat[]
            {
                new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = "player_pfighter" },
                new() { PeerId = 1, SeatIndex = 1, Callsign = "early", PlaneNode = "player_fbrand" },
                new() { PeerId = 2, SeatIndex = 2, Callsign = "late", PlaneNode = "player_fbrand" },
            };
            NetSeats.Validate(seats);
            var nets = new[] { NetSession.Host(mesh[0], seats, 0x2E43UL), NetSession.Guest(mesh[1]), NetSession.Guest(mesh[2]) };
            var links = nets.Select(n => new NetWorldLink(n, NoSeats(), null)).ToArray();
            links[0].FollowVehicles(null, host.Director);
            links[1].FollowVehicles(null, early.Director);
            links[2].FollowVehicles(null, late.Director);
            early.Blacke!.RemotePoses = new RemotePoseBuffer();
            late.Blacke!.RemotePoses = new RemotePoseBuffer();
            ctx.Check(!host.Director.WarpsFromHost && early.Director.WarpsFromHost
                    && late.Director.WarpsFromHost && !control.Director.WarpsFromHost,
                $"the linked guests wait for the host's draw, the host and the control draw their own");

            for (int s = 0; s < 60; s++)
            {
                StepNets(nets);
            }

            ctx.Check(nets[1].Joined && nets[2].Joined, $"both guests joined over the clean link");
            var lossy = new LoopbackConditions(0.10, 0.02, 0.10);
            for (int peer = 1; peer < 3; peer++)
            {
                mesh[0].SetConditions(peer, lossy);
                mesh[peer].SetConditions(0, lossy);
            }

            for (float t = 0f; t < WarpRunS + LateGraphS; t += StepDt)
            {
                foreach (var end in ends)
                {
                    if (end != late || t >= LateGraphS)
                    {
                        end.Step(StepDt);
                    }
                }

                StepNets(nets);
            }

            foreach (var end in ends)
            {
                var at = end.Blacke!.WorldPosition;
                report.AppendLine($"{end.Name}: draws {end.Draws.Count}, OBJECTIVE{WarpObjective} ran with him at "
                    + $"({end.RanAt.X:0},{end.RanAt.Y:0},{end.RanAt.Z:0}), now ({at.X:0},{at.Y:0},{at.Z:0}), "
                    + $"{at.DistanceTo(host.Blacke.WorldPosition):0.#} m from the host's");
                ctx.Check(end.Director.Graph!.CompletedOf(WarpObjective), $"{end.Name}: OBJECTIVE{WarpObjective} ran");
            }

            ctx.Check(host.Draws.Count >= 1 && control.Draws.Count >= 1,
                $"the host and the control draw their own waypoint ({host.Draws.Count}, {control.Draws.Count} draw(s))");
            ctx.Check(early.Draws.Count == 0 && late.Draws.Count == 0,
                $"neither guest draws from its world stream ({early.Draws.Count}, {late.Draws.Count})");
            ctx.Check(early.RanAt.DistanceTo(spawnedAt) <= PlacedToleranceM,
                $"the early guest's directive ran before the pick landed and left him where he was spawned");
            ctx.Check(late.RanAt.DistanceTo(spawnedAt) > PlacedToleranceM,
                $"the late guest's directive found the pick waiting and placed him in the same step");
            if (points[hostPick].PointName is { Length: > 0 } path)
            {
                foreach (var end in new[] { host, early, late })
                {
                    ctx.Check(end.Director.Paths?.IsFrozen(Gyro) == false,
                        $"{end.Name}: the host's waypoint names path '{path}', which releases him onto it");
                }
            }
            else
            {
                var want = new Vector3(points[hostPick].X, points[hostPick].Y, points[hostPick].Z);
                foreach (var end in new[] { host, early, late })
                {
                    ctx.Check(end.Blacke!.WorldPosition.DistanceTo(want) <= PlacedToleranceM,
                        $"{end.Name}: Blacke stands on the host's waypoint ({want.X:0},{want.Y:0},{want.Z:0})");
                }

                ctx.Check(control.Blacke!.WorldPosition.DistanceTo(want) > PlacedToleranceM,
                    $"the control, drawing for itself, put him elsewhere: {control.Blacke.WorldPosition.DistanceTo(want):0} m off");
            }
        }
        finally
        {
            human.Free();
            foreach (var end in ends)
            {
                end.Free();
            }

            live.Free();
            textures.Dispose();
        }
    }

    private static void StepNets(NetSession[] nets)
    {
        foreach (var net in nets)
        {
            net.Step(StepDt);
        }
    }

    private static NetWorldSeats NoSeats() => new()
    {
        SeatOfShooter = _ => -1,
        IsLocal = _ => false,
        ShooterOfSeat = _ => null,
        WeaponIndex = _ => -1,
        WeaponAt = _ => null,
    };

    // The regression itself. A miss must read FALSE, not "cannot answer": an unresolved reference
    // is counted rather than decided, and four of those in a row is the mission that cannot end.
    private static void Spot(TestContext ctx, CampaignDirector director, ObjectiveGraph graph,
        FlightController gyro, Action<Vector3> putPlayer, StringBuilder report)
    {
        int unresolvedBefore = graph.UnresolvedConditions;
        var (_, missSpot, _) = Locations[0];
        putPlayer(gyro.WorldPosition + new Vector3(0f, 0f, SpotRadiusM * 4f));
        graph.Wake(missSpot);
        Advance(director, StepDt * 4f);
        ctx.Check(!graph.CompletedOf(missSpot),
            $"a spot check {SpotRadiusM * 4f:0} m away does not complete");
        ctx.Same(unresolvedBefore, graph.UnresolvedConditions,
            $"…and it read FALSE rather than going unanswered, which is the whole bug");
        ctx.Check(graph.StateOf(FoundObjective) == ObjectiveState.Dormant,
            $"so OBJECTIVE{FoundObjective} is still dormant");

        var (_, hitSpot, _) = Locations[1];
        putPlayer(gyro.WorldPosition + new Vector3(0f, 0f, SpotRadiusM * 0.5f));
        graph.Wake(hitSpot);
        Advance(director, StepDt * 4f);
        ctx.Check(graph.CompletedOf(hitSpot),
            $"a spot check inside {SpotRadiusM:0} m of the warped aircraft completes");
        ctx.Check(graph.StateOf(FoundObjective) != ObjectiveState.Dormant,
            $"…which is what wakes OBJECTIVE{FoundObjective}, the mission's PRIMARY 1");
        ctx.Same(unresolvedBefore, graph.UnresolvedConditions,
            $"…with no condition left unanswered on either read");
        report.AppendLine($"spot: OBJECTIVE{missSpot} missed, OBJECTIVE{hitSpot} found, "
            + $"OBJECTIVE{FoundObjective} {graph.StateOf(FoundObjective)}, "
            + $"unresolved {graph.UnresolvedConditions}");
    }

    private static int WakersOf(ObjectiveScript script, int target)
    {
        int wakers = 0;
        foreach (var def in script.Objectives)
        {
            wakers += def.WakeWhenComplete.Contains(target) ? 1 : 0;
        }
        return wakers;
    }

    private static ObjectiveDef? Numbered(ObjectiveScript script, int number)
    {
        foreach (var def in script.Objectives)
        {
            if (def.Number == number)
            {
                return def;
            }
        }
        return null;
    }

    private static CampaignMission? MissionAt(TestContext ctx, string chapter, string mission)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(mission, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        return null;
    }

    private static void Advance(CampaignDirector director, float seconds)
    {
        for (float t = 0f; t < seconds; t += StepDt)
        {
            director.Step(StepDt);
        }
    }

    private static List<object?>? Fields(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, string name)
    {
        foreach (var (block, fields) in blocks)
        {
            if (block.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return fields;
            }
        }
        return null;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        if (Fields(blocks, CampaignRosterPlan.PlayerBlock) is { } fields
            && AiSkills.RosterSpawnPose(fields) is { } pose)
        {
            return (pose.Position, new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
        }
        return (new Vector3(0f, 800f, 0f), Vector3.Forward);
    }

    // The human rig this suite flies nothing with: it exists so the leader pass has a player.
    private static FlightController HumanRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, Vector3 pos, Vector3 lookAt)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase - 1,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, lookAt);
        rig.Name = "player1";
        ctx.Host.AddChild(rig);
        return rig;
    }

    // The session's own AI spawner with no world effects, the shape CampaignSquadWakeSuites uses.
    private static FlightRoster Spawner(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live)
    {
        var spec = SessionSpec.Parse(Array.Empty<string>());
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
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
            null!, ctx.Host, resources,
            new FlightWorldBindings { Projectiles = live, Gamez = planesGamez },
            new HumanRosterBindings());
    }

    // A world stream that counts what is drawn from it, so a guest that drew is caught.
    private sealed class CountingRandom : Random
    {
        public CountingRandom(int seed)
            : base(seed)
        {
        }

        public int Count { get; private set; }

        public override int Next(int maxValue)
        {
            Count++;
            return base.Next(maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            Count++;
            return base.Next(minValue, maxValue);
        }

        public override double NextDouble()
        {
            Count++;
            return base.NextDouble();
        }
    }

    // One director of the warp run, its roster of Blacke alone, and where he stood the step its
    // OBJECTIVE23 ran.
    private sealed class WarpEnd
    {
        private readonly FlightRoster _roster;

        public WarpEnd(string name, CampaignDirector director, FlightRoster roster, CountingRandom draws)
        {
            Name = name;
            Director = director;
            _roster = roster;
            Draws = draws;
        }

        public string Name { get; }

        public CampaignDirector Director { get; }

        public CountingRandom Draws { get; }

        public FlightController? Blacke => Director.Roster.TryGetValue(Gyro, out var rig) ? rig : null;

        public Vector3 RanAt { get; private set; }

        public void Step(float dt)
        {
            bool before = Director.Graph?.CompletedOf(WarpObjective) == true;
            Director.Step(dt);
            if (!before && Director.Graph?.CompletedOf(WarpObjective) == true && Blacke is { } rig)
            {
                RanAt = rig.WorldPosition;
            }
        }

        public void Free()
        {
            var members = new List<FlightController>(_roster.AiAircraft);
            _roster.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
        }
    }
}
