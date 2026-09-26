using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the two aircraft a story-mission intro animates: both reachable through
/// the definition's own cross-archive pointers, both flown away from the world origin by the
/// intro's own scripts, and the flown airframe drawn on the <c>player</c> marker while the cutscene
/// holds the pilot out of flight. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class IntroAircraftSuites
{
    // The story position driven: the shared generic_intro, on the campaign's first mission.
    private const int IntroSeq = 0;
    private const string IntroAnim = "generic_intro";

    // The definition whose Grahams_sound sequence carries the launch cue, and the cue itself.
    private const string DropAnim = "gi_1stperson";
    private const string LaunchCue = "snd_droplaunch";

    // The airship the intro reparents both aircraft under.
    private const string CarrierNode = "piratezep";

    private const string PlaneNode = "player_bhawk";
    private const float StepDt = 1f / 60f;

    // How long the intro is driven for. The prop is staged around 17 s in and the drop around 33 s,
    // both inside the definition's own camera scripts, so a shorter budget reads a working intro as
    // a stage that never appeared.
    private const float DriveBudgetS = 45f;

    // How far off its host's origin a staged aircraft has to be posed to count. Only rules out the
    // identity pose an unbound event leaves behind; both authored offsets are past 60 m.
    private const float PosedMinM = 10f;

    // How closely the flown airframe has to track the marker it is staged on.
    private const float TrackToleranceM = 1f;

    /// <summary>Drives the campaign's first mission's intro against its BUILT world: the aircraft
    /// archive's <c>player</c> and <c>piratefighter</c> join the runtime's node table at the
    /// chapter's own cross-archive base, the intro's scripts fly both away from the origin, the prop
    /// ends up under the airship, the flown airframe is drawn on the marker while the pilot is out
    /// of flight, and the launch cue fires.</summary>
    // BL-482: the world build put no aircraft-archive node in the runtime's node table, so both
    // names an intro animates claimed a symbol with a null binding and the camera moved over an
    // empty sky.
    [Suite("intro-aircraft-stage",
        "the two aircraft a story-mission intro animates, over the first story mission's BUILT "
        + "world: the aircraft archive's 'player' and 'piratefighter' answer the compiled "
        + "intro's own cross-archive pointers in the runtime's node table, the intro's scripts "
        + "fly both away from the world origin, its OBJECT_ADD_CHILD puts the prop under the "
        + "airship, the flown airframe is drawn on the 'player' marker while callback 11 holds "
        + "the pilot out of flight, and the drop's launch cue fires")]
    internal static void IntroAircraftStage(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), IntroSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {IntroSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");

        var report = new StringBuilder();
        report.AppendLine($"seq {IntroSeq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder, world => Drive(ctx, world, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-intro-aircraft-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s {IntroAnim} over a built world with its aircraft staged");
    }

    private static void Drive(TestContext ctx, TestWorld world, StringBuilder report)
    {
        if (world.Session.Aircraft is not { } stage)
        {
            ctx.Check(false, $"the world build staged the intro's aircraft");
            return;
        }

        int expected = AircraftStage.PointerBaseOf(world.Gamez.Nodes.Count);
        report.AppendLine($"{world.Gamez.Nodes.Count} chapter nodes -> cross-archive base {expected}");
        CheckNodeTable(ctx, world, stage, report);
        if (stage.PlayerMarker is not { } marker || stage.Prop is not { } prop)
        {
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
            cutscene.BindWorld(world.Runtime, stage);
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
            // session's own BindRigs re-applies; the suite has to say it for the same reason.
            cutscene.Host(CutsceneController.CodeOutOfFlight, IntroAnim);
            DriveIntro(ctx, world, cutscene, rig, marker, prop, report);
        }
        finally
        {
            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The node table half: the compiled intro addresses both aircraft by a pointer past the end of
    // the chapter's own table, and this build has to answer that pointer with the staged node.
    private static void CheckNodeTable(TestContext ctx, TestWorld world, AircraftStage stage,
        StringBuilder report)
    {
        var defs = world.Session.Program.ByAnimName(IntroAnim);
        if (defs.Count == 0)
        {
            throw new SuiteSkippedException($"{world.Chapter} carries no compiled {IntroAnim}");
        }

        int expected = AircraftStage.PointerBaseOf(world.Gamez.Nodes.Count);
        foreach (var (name, staged) in new[]
        {
            (AircraftStage.PlayerNode, stage.PlayerMarker),
            (AircraftStage.PropNode, stage.Prop),
        })
        {
            if (!defs[0].NodeRefs.TryGetValue(name, out int ptr))
            {
                ctx.Check(false, $"{IntroAnim}'s symbol table names '{name}'");
                continue;
            }

            var bound = world.Runtime.FindNodeByIndex(ptr);
            report.AppendLine($"'{name}' ptr {ptr} -> archive index {ptr - expected}, "
                + $"bound={(bound != null ? "yes" : "no")}");
            ctx.Check(ptr > world.Gamez.Nodes.Count,
                $"'{name}' ptr {ptr} sits past the chapter's own {world.Gamez.Nodes.Count} nodes");
            ctx.Check(staged != null && bound != null && ReferenceEquals(bound, staged),
                $"and the runtime's node table answers it with the staged '{name}'");
        }
    }

    // The pose half: the intro's own scripts, driven on the runtime's fixed step, with the cutscene
    // host ticking last exactly as it does in a session.
    private static void DriveIntro(TestContext ctx, TestWorld world, CutsceneController cutscene,
        FlightController rig, Node3D marker, Node3D prop, StringBuilder report)
    {
        int cuesBefore = world.Runtime.OneShotSoundsPlayed;
        float propPosed = -1f, markerPosed = -1f, tracked = -1f;
        bool propUnderCarrier = false, markerUnderCarrier = false;
        for (float t = 0f; t < DriveBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            if (propPosed < 0f && Staged(prop))
            {
                propPosed = t;
                propUnderCarrier = IsUnder(prop, CarrierNode);
            }

            if (markerPosed < 0f && Staged(marker))
            {
                markerPosed = t;
                markerUnderCarrier = IsUnder(marker, CarrierNode);
            }

            if (tracked < 0f && markerPosed >= 0f && rig.PlaneModel is { Visible: true }
                && rig.GlobalPosition.DistanceTo(marker.GlobalPosition) < TrackToleranceM)
            {
                tracked = t;
            }
        }

        int cues = world.Runtime.OneShotSoundsPlayed - cuesBefore;
        report.AppendLine($"prop posed at {propPosed:0.##} s under_carrier={propUnderCarrier} "
            + $"offset {prop.Position}");
        report.AppendLine($"marker posed at {markerPosed:0.##} s under_carrier={markerUnderCarrier} "
            + $"offset {marker.Position}");
        report.AppendLine($"airframe tracking at {tracked:0.##} s, {cues} one-shot(s) over the window, "
            + $"{IntroAnim} state {world.Runtime.AnimStateOf(IntroAnim)}");
        ctx.Check(propPosed >= 0f,
            $"'{AircraftStage.PropNode}' is flown off its host's origin by the intro's own script");
        ctx.Check(propUnderCarrier,
            $"and the intro's OBJECT_ADD_CHILD put it under '{CarrierNode}'");
        ctx.Check(markerPosed >= 0f,
            $"'{AircraftStage.PlayerNode}' is flown off its host's origin too");
        ctx.Check(markerUnderCarrier,
            $"and it rides '{CarrierNode}' too, which is the frame its keyframes are written in");
        ctx.Check(tracked >= 0f,
            $"and the flown airframe is drawn on it while the pilot is out of flight");
        CheckLaunchCue(ctx, world, cues, report);
    }

    // The launch cue. Its own definition has to have run, and a one-shot has to have started: a
    // count alone cannot name the clip, and the definition alone cannot say a stream resolved.
    // ⚠ A muted run counts nothing, so it reports rather than fails there.
    private static void CheckLaunchCue(TestContext ctx, TestWorld world, int cues, StringBuilder report)
    {
        bool authored = false;
        foreach (var def in world.Session.Program.ByAnimName(DropAnim))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    authored |= string.Equals(ev.Data.Str("name"), LaunchCue,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        ctx.Check(authored, $"'{DropAnim}' authors the '{LaunchCue}' launch cue");
        ctx.Check(world.Runtime.AnimStateOf(DropAnim) != 0,
            $"and this program carries it, so the intro can reach it");
        if (world.Runtime.Sounds == null)
        {
            report.AppendLine("no audio session: the cue's own one-shot is not observable here");
            ctx.Note($"muted run: '{LaunchCue}' checked as authored and reachable, not as sounded");
            return;
        }

        ctx.Check(cues > 0, $"and the intro started {cues} one-shot(s), the cue's own dispatch path");
    }

    // Staged = drawn and posed off its host's origin. ⚠ Measured in the host's own frame: the
    // airship a story session places from its roster sits at the world origin in a harness world,
    // so a world-space distance would report a correctly staged aeroplane as never having moved.
    private static bool Staged(Node3D node) =>
        node.IsVisibleInTree() && node.Position.Length() > PosedMinM;

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
                Name = "IntroAircraftPlayer",
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
