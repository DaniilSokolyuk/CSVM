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

/// <summary>Where a mid-mission drop leaves each of two humans: the mission has one staged
/// <c>player</c> marker, so the episode owner rides it and flies out of the re-placement, and the
/// other human holds the pose it was in when the code took it out of flight. The same shipped
/// definition is driven twice, claimed by the guest and claimed by nobody, so the leg carries its
/// able-to-fail control: an unclaimed episode is the scripted player's and the guest is the one
/// left standing. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
internal static class CoopDropoffSuites
{
    // C3/M01, whose texdrop is the campaign's first re-placing cutscene (the story position
    // DropoffPlacementSuites drives with one human).
    private const int MissionSeq = 0;

    // The re-placement code, which is what finds the authored definition by data rather than by name.
    private const int ReplaceCode = 951;

    private const string PlaneNode = "player_bhawk";
    private const float StepDt = 1f / 60f;

    // The drop's own scripts run 4.4 s and its camera 4.45 s; the loop leaves at the handoff.
    private const float DriveBudgetS = 30f;

    // How closely a posed aeroplane has to sit on the pose it is being measured against.
    private const float PlacedToleranceM = 1f;

    // How far the marker has to travel from where a human flew in, or the ride assertion would pass
    // on a build that stages nobody.
    private const float MovedMinM = 100f;

    // The two humans fly in this far apart, so "held where it stood" and "posed on the marker" can
    // never be read off the same coordinates.
    private const float ApartM = 2000f;

    /// <summary>Drives C3/M01's drop-off definition directly against its BUILT world with two humans
    /// flying: the episode owner is posed on the staged <c>player</c> marker for the whole episode
    /// and flies out of the re-placement the definition authors, while the other human is held inert
    /// at its own coordinates throughout and is back in play there at the handoff.</summary>
    [Suite("campaign-coop-dropoff",
        "the first story mission's own drop-off driven with two humans flying 2 km apart: the "
        + "episode owner rides the staged 'player' marker for every frame of the episode and "
        + "flies out of the re-placement the definition authors, while the other human is inert "
        + "at its own coordinates throughout and back in play there at the handoff; the same "
        + "definition claimed by nobody poses the scripted player and holds the guest instead")]
    internal static void CampaignCoopDropoff(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"aircraft archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), MissionSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {MissionSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

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

        ctx.WriteArtifact($"test-campaign-coop-dropoff-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: the drop poses the episode owner and holds the other human");
    }

    private static void Drive(TestContext ctx, TestWorld world, string missionZrdr, StringBuilder report)
    {
        string? anim = ReplacingAnimOf(world, MissionCutscenes.AnimNames(missionZrdr));
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

        WithTwoHumans(ctx, world, (rigs, cutscene) =>
            RunTheDrop(ctx, world, cutscene, rigs, marker, anim, guest: true, report));
        WithTwoHumans(ctx, world, (rigs, cutscene) =>
            RunTheDrop(ctx, world, cutscene, rigs, marker, anim, guest: false, report));
    }

    // One episode, claimed by the guest or by nobody, driven the way a flown session drives it: the
    // slot is written before the definition starts and the runtime dispatches its codes to the host.
    // Neither rig is ever stepped, so wherever either ends up is this episode's doing.
    private static void RunTheDrop(TestContext ctx, TestWorld world, CutsceneController cutscene,
        IReadOnlyList<PlayerRig> rigs, Node3D marker, string anim, bool guest, StringBuilder report)
    {
        var owner = rigs[guest ? 1 : 0].Controller!;
        var other = rigs[guest ? 0 : 1].Controller!;
        var flewIn = new Vector3[] { owner.WorldPosition, other.WorldPosition };
        string who = guest ? "the guest (P2)" : "nobody";

        cutscene.HostDefinitions(new[] { anim });
        cutscene.Own(anim, guest ? rigs[1] : null);
        world.Runtime.Play(anim);
        var held = new Held();
        Transform3D? placement = null;
        float played = 0f;
        for (float t = 0f; t < DriveBudgetS && (played == 0f || cutscene.Playing); t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            played += cutscene.Playing ? StepDt : 0f;
            if (cutscene.OutOfFlight)
            {
                held.Sample(marker, owner, other, flewIn);
            }

            if (placement == null && cutscene.Playing && cutscene.Codes.Contains(ReplaceCode))
            {
                placement = AnimRuntime.WorldTransform(marker, out _);
            }
        }

        report.AppendLine($"claimed by {who}: episode owner=P{(cutscene.EpisodeOwner?.Index ?? -1) + 1}, " +
            $"ran {played:0.##} s, codes {string.Join("/", cutscene.Codes)}");
        report.AppendLine($"  out of flight for {held.Frames} frame(s), marker drawn on {held.Posed}: " +
            $"owner {held.OwnerOffM:0.##} m off it at worst, other {held.OtherMovedM:0.##} m off its own " +
            $"coordinates, marker travelled {held.MarkerTravelM:0.#} m, other inert on {held.OtherInert}");
        ctx.Check(played > 0f && !cutscene.Playing,
            $"'{anim}' runs to its handoff with the cutscene host answering its codes");
        ctx.Check(ReferenceEquals(cutscene.EpisodeOwner, rigs[guest ? 1 : 0]),
            $"the episode claimed by {who} belongs to P{owner.PlayerIndex - FlightRoster.ShooterIdBase + 1}");
        ctx.Check(held.Frames > 0 && held.Posed > 0,
            $"and takes both humans out of flight on the way, with the marker drawn for part of it");
        ctx.Same(held.Frames, held.OtherInert,
            $"the human who did not earn the drop is inert for every frame of it, the same as the owner");
        ctx.Check(held.MarkerTravelM > MovedMinM,
            $"the marker is posed {held.MarkerTravelM:0} m from where the owner flew in, so this can fail");
        ctx.Check(held.OwnerOffM < PlacedToleranceM,
            $"the owner rides the staged '{AircraftStage.PlayerNode}' marker on every frame it is drawn");
        ctx.Check(held.OtherMovedM < PlacedToleranceM,
            $"…and the other human holds the coordinates it was in, rather than stacking on that marker");

        AfterTheHandoff(ctx, cutscene, placement, owner, other, flewIn, report);
    }

    // The hand-back: the owner flies out of the pose the definition parked the marker at, and the
    // other human is back in play exactly where it was standing, neither moved nor re-placed.
    private static void AfterTheHandoff(TestContext ctx, CutsceneController cutscene,
        Transform3D? placement, FlightController owner, FlightController other, Vector3[] flewIn,
        StringBuilder report)
    {
        ctx.Check(placement != null, $"the episode raises callback {ReplaceCode} on the way");
        if (placement is not { } placed)
        {
            return;
        }

        float ownerOff = owner.WorldPosition.DistanceTo(placed.Origin);
        float otherOff = other.WorldPosition.DistanceTo(flewIn[1]);
        report.AppendLine($"  handed off: owner at {owner.WorldPosition} ({ownerOff:0.##} m off the " +
            $"placement, {owner.WorldVelocity.Length():0.##} m/s), other at {other.WorldPosition} " +
            $"({otherOff:0.##} m off its own), inert owner={owner.Inert} other={other.Inert}");
        ctx.Check(!cutscene.OutOfFlight && !owner.Inert && !other.Inert && !owner.Held && !other.Held,
            $"both humans are back in play at the handoff");
        ctx.Check(ownerOff < PlacedToleranceM,
            $"the owner flies out of the pose the drop parked '{AircraftStage.PlayerNode}' at");
        ctx.Check(owner.WorldVelocity.Length() > 1f,
            $"…and moving, the release the original's own callback writes into the vehicle");
        ctx.Check(otherOff < PlacedToleranceM,
            $"and the other human is back in play at its own position, which no re-placement moved");
    }

    // Two humans over the mission's built world, flown in {ApartM} apart, with a host of their own.
    // Fresh per leg: a leg leaves its owner re-placed kilometres away and its staging state spent.
    private static void WithTwoHumans(TestContext ctx, TestWorld world,
        Action<IReadOnlyList<PlayerRig>, CutsceneController> leg)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var planes = new List<FlightController>();
        var savedHost = world.Runtime.CallbackHost;
        try
        {
            var rigs = new PlayerRig[2];
            for (int i = 0; i < rigs.Length; i++)
            {
                var plane = BuildPlane(ctx, textures, pool, i, new Vector3(i * ApartM, 0f, 0f));
                planes.Add(plane);
                rigs[i] = new PlayerRig
                {
                    Index = i,
                    Camera = ctx.Camera,
                    HudParent = ctx.Host,
                    Controller = plane,
                };
            }

            cutscene.BindWorld(world.Runtime, world.Session.Aircraft);
            cutscene.BindRigs(rigs, () => Array.Empty<FlightController>());
            world.Runtime.CallbackHost = cutscene.Host;
            leg(rigs, cutscene);
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            foreach (var plane in planes)
            {
                plane.Free();
            }

            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    private static FlightController BuildPlane(TestContext ctx, TextureArchive textures,
        ProjectilePool pool, int index, Vector3 at)
    {
        var model = new PlaneBuilder(GameZ.Load(ctx.PlanesGamezPath), textures).Build(PlaneNode);
        var plane = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            PlayerIndex = FlightRoster.ShooterIdBase + index,
            IsHumanPiloted = true,
            Projectiles = pool,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
            Name = $"CoopDropoffPlayer{index}",
        };
        plane.AddChild(model);
        ctx.Host.AddChild(plane);
        plane.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null, new CamParams(),
            at, at + Vector3.Forward, 0f, 0f);
        return plane;
    }

    // The mission's own cutscene definition that raises the re-placement callback, read out of the
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

    // The worst reading over every out-of-flight frame, rather than one sampled frame: the owner is
    // asserted to ride the marker on every frame it is drawn, and the other human never to move at
    // all. ⚠ The marker is switched off for part of a drop (`player_setup`'s own state), and the
    // host poses nobody on a frame it is not drawn on, so the ride is measured over Posed and the
    // hold over Frames.
    private sealed class Held
    {
        public int Frames { get; private set; }

        public int Posed { get; private set; }

        public int OtherInert { get; private set; }

        public float OwnerOffM { get; private set; }

        public float OtherMovedM { get; private set; }

        public float MarkerTravelM { get; private set; }

        public void Sample(Node3D marker, FlightController owner, FlightController other, Vector3[] flewIn)
        {
            // ⚠ The NODE pose, not WorldPosition: an out-of-flight aircraft is not stepped, so the
            // staging writes what is drawn and deliberately leaves the flight model where it was.
            Frames++;
            OtherInert += other.Inert && other.Held ? 1 : 0;
            OtherMovedM = Mathf.Max(OtherMovedM, other.GlobalTransform.Origin.DistanceTo(flewIn[1]));
            if (!marker.Visible)
            {
                return;
            }

            var at = AnimRuntime.WorldTransform(marker, out _).Origin;
            Posed++;
            OwnerOffM = Mathf.Max(OwnerOffM, owner.GlobalTransform.Origin.DistanceTo(at));
            MarkerTravelM = Mathf.Max(MarkerTravelM, at.DistanceTo(flewIn[0]));
        }
    }
}
