using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session.Campaign;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A shared cutscene across machines. The first suite plays a guest's docking on the
/// host, where the guest's aeroplane is a seat copy in no pane. It reads that copy riding the
/// staged <c>player</c> marker. The second runs the network skip over two joined cutscene hosts.
/// Whoever skips, the host decides, and each skip ends only the episode it names.
/// </summary>
internal static class NetCutsceneSuites
{
    // C3/M01, whose auto row is the campaign's first zeppelin docking.
    private const int FirstSeq = 0;
    private const float StepDt = 1f / 60f;
    private const float PlayBudgetS = 30f;

    // How closely a posed aeroplane has to sit on the marker. And how far the marker has to travel
    // from where the guest flew in, or the ride would pass on a build staging nobody.
    private const float PlacedToleranceM = 1f;
    private const float MovedMinM = 100f;

    // The guest flies in this far from the host, so "on the marker" can be read for one of them.
    private const float ApartM = 2000f;

    // Link steps given to a skip to cross: the lossy link's delay and resends several times over.
    private const int CrossSteps = 120;

    private const ulong Seed = 0xC5C1_0001UL;

    private static readonly string[] Airframes = { "player_bhawk", "player_bhawk" };

    [Suite("net-cutscene-remote-owner",
        "the first story mission's docking, owned by a guest, played on the host over its BUILT "
        + "world: the host's copy of the guest's seat is in no pane and is written by its pose "
        + "feed every step, yet it rides the staged 'player' marker on every frame the marker is "
        + "drawn and is visible there, while the host's own aeroplane holds where it stood")]
    internal static void RemoteOwner(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = CampaignSequence.Load(ctx.ZrdrPath).Cast<CampaignMission?>()
                .FirstOrDefault(m => m!.Value.Seq == FirstSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {FirstSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {FirstSeq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder, world => Dock(ctx, world, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-net-cutscene-remote-owner-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"{report.ToString().TrimEnd().Replace(System.Environment.NewLine, "; ")}");
    }

    [Suite("net-cutscene-skip-episode",
        "two cutscene hosts, a host's and a guest's, joined over a 30 ms, 25 per cent lossy "
        + "loopback and playing the same episodes: a guest's skip is an ask that leaves both "
        + "playing until the host's word returns, then ends the episode on both; a host's skip "
        + "ends it on the guest; a stale or repeated skip of an episode already over ends nothing "
        + "on either end; and a skip that reaches the guest before its own replay has started "
        + "the episode ends it the moment it does")]
    internal static void SkipEpisode(TestContext ctx)
    {
        var report = new StringBuilder();
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(5858));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        var hostNet = NetSession.Host(mesh[0], roster, Seed, null, Airframes);
        var guestNet = NetSession.Guest(mesh[1], Airframes);
        for (int i = 0; i < 200 && !guestNet.Joined; i++)
        {
            hostNet.Step(StepDt);
            guestNet.Step(StepDt);
        }

        ctx.Check(guestNet.Joined && guestNet.LocalSeat == 1, $"the guest joins as seat 1 over the lossy link");
        var host = new CutsceneController();
        var guest = new CutsceneController();
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(guest);
        try
        {
            const string film = "net_cutscene_film";
            host.HostDefinitions(new[] { film });
            guest.HostDefinitions(new[] { film });
            var hostLink = NetCutsceneLink.Open(hostNet, host, pane => pane);
            var guestLink = NetCutsceneLink.Open(guestNet, guest, pane => pane + 1);
            void Cross(int steps)
            {
                for (int i = 0; i < steps; i++)
                {
                    hostNet.Step(StepDt);
                    guestNet.Step(StepDt);
                    host.Tick();
                    guest.Tick();
                }
            }

            void Start(CutsceneController end) => end.Host(CutsceneController.CodeHoldsWorld, film);

            // The guest's skip.
            Start(host);
            Start(guest);
            bool asked = guest.Skip(0);
            report.AppendLine($"episode #{guest.EpisodeOrdinal}: the guest asked={asked}, then host playing={host.Playing} guest playing={guest.Playing}");
            ctx.Check(asked && host.Playing && guest.Playing,
                $"ABLE-TO-FAIL CONTROL: a guest's skip is an ask, and until the host's word returns both ends still play");
            Cross(CrossSteps);
            report.AppendLine($"  after the link: host playing={host.Playing} guest playing={guest.Playing}, asks sent {guestLink.SkipsSent}, decisions {hostLink.SkipsSent}");
            ctx.Check(!host.Playing && !guest.Playing,
                $"the guest's skip ends the episode on the host and on the guest");

            // The host's skip.
            Start(host);
            Start(guest);
            bool skipped = host.Skip(0);
            ctx.Check(skipped && !host.Playing && guest.Playing,
                $"ABLE-TO-FAIL CONTROL: the host's skip ends its own episode at once and the guest's only over the link");
            Cross(CrossSteps);
            report.AppendLine($"episode #{guest.EpisodeOrdinal}: the host skipped, then guest playing={guest.Playing}");
            ctx.Check(!guest.Playing, $"the host's skip ends the episode on the guest");

            // A skip of an episode already over, delivered into the next one on both ends.
            Start(host);
            Start(guest);
            int key = host.EpisodeKey;
            int over = host.EpisodeOrdinal - 1;
            hostNet.Broadcast(new CutsceneSkipMessage(0, (ushort)over, key), NetChannels.Events);
            guestNet.Send(guestNet.HostPeer, new CutsceneSkipMessage(1, (ushort)over, key), NetChannels.Events);
            Cross(CrossSteps);
            report.AppendLine($"episode #{host.EpisodeOrdinal}: skips of #{over} sent both ways, host playing={host.Playing} guest playing={guest.Playing}");
            ctx.Check(host.Playing && guest.Playing,
                $"a skip naming episode #{over}, already over, ends episode #{host.EpisodeOrdinal} on neither end");
            host.Skip(0);
            Cross(CrossSteps);

            // The host's skip reaching the guest ahead of the guest's own replay.
            Start(host);
            host.Skip(0);
            Cross(CrossSteps);
            bool early = !guest.Playing;
            Start(guest);
            bool startedHeld = guest.Playing;
            guest.Tick();
            report.AppendLine($"episode #{guest.EpisodeOrdinal}: the host's skip arrived first, guest started={startedHeld} then playing={guest.Playing}");
            ctx.Check(early && startedHeld && !guest.Playing,
                $"a skip that arrives before the guest's replay starts the episode ends it on the guest's next frame");
        }
        finally
        {
            host.Free();
            guest.Free();
        }

        ctx.WriteArtifact("test-net-cutscene-skip-episode.txt", report.ToString());
        ctx.Note($"{report.ToString().TrimEnd().Replace(System.Environment.NewLine, "; ")}");
    }

    // The host's end of a guest's docking. P1 is the host's pane. The guest's seat is a copy fed by
    // a pose buffer, as a network session builds it, stepped every frame as the sim steps it. The
    // episode is claimed by that copy, as a replicated row start claims it.
    private static void Dock(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var rows = LandingApproaches.Resolve(SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter),
            world.Gamez, name => world.Runtime.Handles(name));
        var row = rows.FirstOrDefault(r => r.Auto);
        if (row == null || world.Session.Aircraft is not { PlayerMarker: { } marker } stage)
        {
            ctx.Check(false, $"the mission carries an auto row and stages the '{AircraftStage.PlayerNode}' marker it poses");
            return;
        }

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var mine = CoopDropoffSuites.BuildPlane(ctx, textures, pool, 0, Vector3.Zero);
        var copy = CoopDropoffSuites.BuildPlane(ctx, textures, pool, 1, new Vector3(ApartM, 0f, 0f));
        copy.RemotePoses = new RemotePoseBuffer();
        var savedHost = world.Runtime.CallbackHost;
        try
        {
            var pane = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = ctx.Host, Controller = mine };
            var guestSeat = new PlayerRig { Index = 1, Camera = null!, HudParent = ctx.Host, Controller = copy };
            cutscene.BindWorld(world.Runtime, stage);
            cutscene.BindRigs(new[] { pane }, () => Array.Empty<FlightController>());
            cutscene.HostDefinitions(world.Session.Program.Subset(new[] { row.Anim }).Defs
                .Select(d => d.AnimName).OfType<string>().Distinct());
            world.Runtime.CallbackHost = cutscene.Host;
            var flewIn = new[] { mine.GlobalTransform.Origin, copy.GlobalTransform.Origin };

            cutscene.Own(row.Anim, guestSeat);
            world.Runtime.Play(row.Anim);
            int posed = 0;
            int shown = 0;
            float copyOff = 0f;
            float mineOn = float.MaxValue;
            float travel = 0f;
            float played = 0f;
            for (float t = 0f; t < PlayBudgetS && (played == 0f || cutscene.Playing); t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                copy.SimStep(StepDt);
                cutscene.Tick();
                played += cutscene.Playing ? StepDt : 0f;
                if (!cutscene.OutOfFlight || !marker.Visible)
                {
                    continue;
                }

                var at = AnimRuntime.WorldTransform(marker, out _).Origin;
                posed++;
                shown += copy.PlaneModel is { Visible: true } && copy.IsVisibleInTree() ? 1 : 0;
                copyOff = Mathf.Max(copyOff, copy.GlobalTransform.Origin.DistanceTo(at));
                mineOn = Mathf.Min(mineOn, mine.GlobalTransform.Origin.DistanceTo(at));
                travel = Mathf.Max(travel, at.DistanceTo(flewIn[1]));
            }

            report.AppendLine($"row '{row.Anim}' owned by the guest's copy: owner=P{(cutscene.EpisodeOwner?.Index ?? -1) + 1}, ran {played:0.##} s, " +
                $"marker drawn on {posed} frame(s), copy shown on {shown}, copy {copyOff:0.##} m off it at worst, " +
                $"host P1 {mineOn:0.#} m from it at nearest, marker travelled {travel:0.#} m");
            ctx.Check(played > 0f && ReferenceEquals(cutscene.EpisodeOwner, guestSeat),
                $"the docking plays on the host with the episode belonging to the guest's seat");
            ctx.Check(posed > 0 && travel > MovedMinM,
                $"ABLE-TO-FAIL CONTROL: the marker is drawn on {posed} frame(s) and travels {travel:0} m from where the guest's copy stood");
            ctx.Check(mineOn > MovedMinM,
                $"ABLE-TO-FAIL CONTROL: the host's own aeroplane is never on the marker ({mineOn:0} m at nearest), so the ride below is the copy's");
            ctx.Check(copyOff < PlacedToleranceM,
                $"the guest's copy rides the '{AircraftStage.PlayerNode}' marker on every frame it is drawn ({copyOff:0.##} m off at worst)");
            ctx.Same(posed, shown, $"…and is visible on every one of those frames");
            ctx.Check(!cutscene.Playing, $"and the docking runs to its handoff within {PlayBudgetS:0} s");
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            mine.Free();
            copy.Free();
            cutscene.Free();
            pool.Free();
            textures.Dispose();
        }
    }
}
