using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Two whole sessions in one process, a host and a guest, joined over a loopback mesh.
/// This is the rig every later replication item is measured on. A rule that holds here holds
/// between two machines, because the only thing the loopback replaces is the carrier. The two
/// worlds are built under separate <see cref="SubViewport"/>s with their own
/// <see cref="World3D"/>, so neither one's hulls, lights or areas can reach the other's.</summary>
internal static class NetSessionSuites
{
    private const string MpMission = "MP1";

    // What the host and the guest are launched with. Different on purpose: the assertion that the
    // handshake replaced the guest's seed cannot then be satisfied by a shared launch value.
    private const ulong HostSeed = 0xA5A50101UL;
    private const ulong GuestSeed = 0x11112222UL;

    // Sim steps both sessions are driven through after the join, at the fixed step. Long enough
    // for a reliable payload to cross a 30 ms link with 10 ms of jitter on it.
    private const int LockstepSteps = 20;

    // The airframe order both peers read a roster's airframe index against. Two different entries,
    // so a seat's pick crossing the wire cannot be satisfied by the two ends sharing a default.
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-two-session",
        "a host session and a guest session in one process, joined over a two-transport loopback "
        + "mesh and stepped in lockstep: the guest takes the host's seed off the handshake in place "
        + "of its own, its roster matches the host's seat for seat with the local flags "
        + "complementary, both worlds walk every seat onto the same spawn table entry, the join "
        + "shows in the counters, a typed message registered by handler arrives from inside the "
        + "receiving session's own step, and the two worlds stand in separate physics spaces")]
    internal static void TwoSessionsOverOneLoopback(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");

        var spec = SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=1", "--mute",
        });
        var table = new SpawnPicker(spec).LoadSpawnList(missionZrdr, spec.Scenario);
        if (table is not { Count: >= 2 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors no usable net.zrd table");
        }

        // A lossy, jittery link on purpose. The join and everything this suite asserts on is
        // reliable traffic, which the transport must carry in order whatever the conditions.
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(6571));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        NetSeats.Validate(roster);

        // Process-global state two sessions in one process share. Restored below so this suite
        // cannot shift the streams, or the ambient clock, of every suite after it in the shard.
        ulong master = Rng.Master;
        bool pinned = Rng.Pinned;
        var clockWas = GameClock.Current;
        var profileWas = StartupProfile.Current;
        Ends? host = null;
        Ends? guest = null;
        try
        {
            long memBefore = (long)OS.GetStaticMemoryUsage();
            var wall = Stopwatch.StartNew();
            host = Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            double hostMs = wall.Elapsed.TotalMilliseconds;
            long memHost = (long)OS.GetStaticMemoryUsage();
            wall.Restart();
            guest = Open(ctx, spec, mesh[1], isHost: false, GuestSeed, null);
            double guestMs = wall.Elapsed.TotalMilliseconds;
            long memGuest = (long)OS.GetStaticMemoryUsage();

            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            Join(ctx, host.Session, guest.Session);
            Spawns(ctx, host.Session, guest.Session, table);
            Traffic(ctx, host.Session, guest.Session);
            ctx.Check(host.Pane.World3D.Space != guest.Pane.World3D.Space
                      && host.Session.GetWorld3D().Space != guest.Session.GetWorld3D().Space,
                $"the two worlds stand in separate physics spaces, so neither one's hulls can reach the other's");

            // Cold against warm, so the pair is a bound on the second session and not a like-for-like
            // comparison (PERF-7). The decodes the first build paid for are what the second reuses.
            string cost = $"build {guestMs:0} ms against the first's {hostMs:0} ms (warm against cold), static memory {(memGuest - memHost) / 1048576.0:0.0} MiB against the first's {(memHost - memBefore) / 1048576.0:0.0} MiB";
            ctx.Note($"second session cost: {cost}");
        }
        finally
        {
            guest?.Close();
            host?.Close();
            StartupProfile.Current = profileWas;
            GameClock.Current = clockWas;
            Rng.Reset(master, pinned);
        }
    }

    // The seed, the seat and the roster a guest is built from are the host's, and nothing of its
    // own launch survives the join. The local flags are the one thing that must differ.
    private static void Join(TestContext ctx, GameSession host, GameSession guest)
    {
        string seeds = $"host {host.MasterSeed:X}, guest {guest.MasterSeed:X}, its own {GuestSeed:X}";
        ctx.Check(host.MasterSeed == HostSeed && guest.MasterSeed == HostSeed,
            $"the guest builds on the host's seed, not the one it was launched with ({seeds})");
        ctx.Same(host.NetSeats.Count, guest.NetSeats.Count, $"the guest's roster is the whole field");
        var pairs = host.NetSeats.Zip(guest.NetSeats).ToArray();
        ctx.Check(pairs.All(p => p.First.SeatIndex == p.Second.SeatIndex
                                 && p.First.TeamId == p.Second.TeamId
                                 && p.First.PeerId == p.Second.PeerId
                                 && p.First.Callsign == p.Second.Callsign
                                 && p.First.PlaneNode == p.Second.PlaneNode),
            $"every seat crosses intact: {string.Join(", ", guest.NetSeats.Select(s => $"{s.SeatIndex}:{s.Callsign}/{s.PlaneNode}@{s.PeerId}"))}");
        int here = host.NetLink!.LocalSeat;
        int there = guest.NetLink!.LocalSeat;
        ctx.Check(here == 0 && there == 1
                  && host.NetSeats[0].IsLocal && !host.NetSeats[1].IsLocal
                  && !guest.NetSeats[0].IsLocal && guest.NetSeats[1].IsLocal,
            $"and each end flies its own seat alone (host seat {here}, guest seat {there})");
    }

    // The spawn walk is the real proof the two peers agree. With no --spawn the base is drawn
    // from the seeded Spawn stream, so a disagreed seed moves a seat to another table entry.
    private static void Spawns(TestContext ctx, GameSession host, GameSession guest,
        IReadOnlyList<SpawnPoint> table)
    {
        ctx.Same(host.SeatRigs.Count, guest.SeatRigs.Count, $"both worlds size themselves by the field");
        var mine = host.SeatRigs.Select(r => EntryAt(table, r.Controller)).ToArray();
        var theirs = guest.SeatRigs.Select(r => EntryAt(table, r.Controller)).ToArray();
        ctx.Check(mine.All(i => i >= 0) && mine.SequenceEqual(theirs),
            $"every seat opens on the same table entry on both peers (host {string.Join(", ", mine)}, guest {string.Join(", ", theirs)})");
        ctx.Check(new HashSet<int>(mine).Count == mine.Length,
            $"and no two seats share one (entries {string.Join(", ", mine)})");
        string planes = string.Join(", ", guest.NetSeats.Select(s => s.PlaneNode));
        ctx.Check(guest.NetSeats.Select(s => s.PlaneNode).SequenceEqual(Airframes),
            $"and each entry's airframe index resolved back to its own name on the guest ({planes})");
    }

    // The join's own traffic, then the contract a replication feature uses. Register a handler,
    // send a typed message, and have it applied from inside the receiving session's step.
    private static void Traffic(TestContext ctx, GameSession host, GameSession guest)
    {
        var link = host.NetLink!;
        var far = guest.NetLink!;
        string counters = $"sent {link.Sent}, received {far.Received}, unknown {far.DroppedUnknown}, malformed {far.Malformed}";
        ctx.Check(link.Sent == 2 && far.Received == 2 && far.DroppedUnknown == 0 && far.Malformed == 0,
            $"the join is two reliable payloads and nothing else ({counters})");

        int seen = 0;
        var got = default(ScoreMessage);
        far.On<ScoreMessage>((_, message) =>
        {
            seen++;
            got = message;
        });
        link.Broadcast(new ScoreMessage(1, 7, 2, 1));
        Lockstep(host, guest);
        ctx.Check(seen == 1 && got == new ScoreMessage(1, 7, 2, 1),
            $"a registered handler takes its typed message from inside the guest's own step ({seen} arrival(s), {got})");

        // ABLE-TO-FAIL CONTROL. The same path with no handler on the type counts the payload
        // as unclaimed. The zero above is therefore a bound handler, not a silent wire.
        int unknownWas = far.DroppedUnknown;
        link.Broadcast(new MatchStateMessage(60f, 300f, 5, NetMatchEnd.Running));
        Lockstep(host, guest);
        ctx.Check(far.DroppedUnknown == unknownWas + 1 && seen == 1,
            $"ABLE-TO-FAIL CONTROL: a type no handler claims is counted, not dispatched (unknown {unknownWas} to {far.DroppedUnknown})");
    }

    // Both sessions through the same number of fixed steps, host first, the order a listen server
    // runs in. The transport step is inside _PhysicsProcess, so this drives the real arrival path.
    private static void Lockstep(GameSession host, GameSession guest)
    {
        for (int i = 0; i < LockstepSteps; i++)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest._PhysicsProcess(GameClock.FixedDt);
        }
    }

    // One end of the match: its own pane, its own world, its own session node. The pane renders
    // nothing, the suite reads poses and counters rather than pixels.
    private static Ends Open(TestContext ctx, SessionSpec spec, INetTransport transport,
        bool isHost, ulong seed, IReadOnlyList<NetSeat>? roster)
    {
        var pane = new SubViewport
        {
            Size = new Vector2I(640, 480),
            OwnWorld3D = true,
            World3D = new World3D(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        var camera = new Camera3D { Fov = 60f, Far = 20000f };
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-45, 150, 0) };
        pane.AddChild(camera);
        pane.AddChild(sun);
        ctx.Host.AddChild(pane);
        var session = new GameSession(spec, new LauncherContext
        {
            RepoRoot = ctx.RepoRoot,
            DataRoot = ctx.DataRoot,
            PlanesGamezPath = ctx.PlanesGamezPath,
            ZrdrPath = ctx.ZrdrPath,
            SoundsPath = ctx.SoundsPath,
            InterpPath = ctx.InterpPath,
            MessagesPath = ctx.MessagesPath,
            RofPath = System.IO.Path.Combine(ctx.DataRoot, "extracted", "rof"),
            ProbeRunner = new ProbeRunner(ctx.RepoRoot, ctx.DataRoot, ctx.ZrdrPath, ctx.SoundsPath,
                ctx.InterpPath, ctx.MessagesPath, ctx.PlanesGamezPath),
            CaptureDirector = new CaptureDirector(spec),
            MasterSeed = seed,
            Camera = camera,
            Orbit = new UI.OrbitCamera(camera),
            Sun = sun,
            Env = new Godot.Environment(),
            MenuDriven = false,
            MenuPads = null,
            Presentation = UI.Menu.PresentationId.BuiltIn,
            ExitSession = () => { },
            RestartSession = () => { },
            NetSeats = isHost ? roster : null,
            NetTransport = transport,
            NetHost = isHost,
            NetAirframes = Airframes,
        });
        pane.AddChild(session);
        return new Ends(pane, session, session.StartSession());
    }

    // Which table entry a placed aircraft is standing on, or -1. The picker raises a start off the
    // ground under it, so the match is on the horizontal position alone.
    private static int EntryAt(IReadOnlyList<SpawnPoint> table, Node3D? placed)
    {
        if (placed == null)
        {
            return -1;
        }

        var pos = placed.GlobalPosition;
        for (int i = 0; i < table.Count; i++)
        {
            var d = table[i].Position - pos;
            if (Mathf.Abs(d.X) < 1f && Mathf.Abs(d.Z) < 1f)
            {
                return i;
            }
        }

        return -1;
    }

    // One peer's whole rig, so the teardown is one call per end. It cannot then free a pane out
    // from under a session that still has to report its exit.
    private sealed record Ends(SubViewport Pane, GameSession Session, bool Built)
    {
        public void Close()
        {
            Session.Free();
            Pane.Free();
        }
    }
}
