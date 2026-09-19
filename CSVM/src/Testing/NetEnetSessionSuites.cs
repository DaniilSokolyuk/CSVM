using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// The join a player makes, over the shipped carrier rather than over the loopback mesh. Two
/// whole sessions run in one process, linked by two <see cref="EnetTransport"/>s on 127.0.0.1.
/// It covers what the mesh cannot: the real socket and the real wall clock. It also covers the
/// window where a guest's board has stepped the socket before its session exists.
/// ⚠ Both ends are in this process: a second game process cannot be driven from the hidden
/// desktop without putting a window on somebody's screen. The carrier is therefore what crosses
/// here, not the process boundary.
/// </summary>
internal static class NetEnetSessionSuites
{
    private const string MpMission = "MP1";

    // Loopback only. A wildcard bind is what makes Windows ask about the firewall, and a test run
    // must never put a dialog on anybody's screen.
    private const string Loopback = "127.0.0.1";

    // Below the ephemeral range, and clear of the enet-transport suite's own block, so the two
    // can run in one shard. Walked, so a port a sibling still holds costs nothing.
    private const int FirstPort = 47160;
    private const int PortsToTry = 20;

    // What the host and the guest are launched with. Different on purpose: the assertion that the
    // handshake replaced the guest's seed cannot then be satisfied by a shared launch value.
    private const ulong HostSeed = 0xC0FFEE11UL;
    private const ulong GuestSeed = 0x22223333UL;

    // The give-up on a socket wait. Loopback delivery is sub-millisecond; this bounds a hang.
    private const double WaitSeconds = 5.0;

    // Sim steps both sessions are driven through after the join, at the fixed step.
    private const int LockstepSteps = 24;

    // The airframe order both peers read a roster's airframe index against. Two different entries,
    // so a seat's pick crossing the wire cannot be satisfied by the two ends sharing a default.
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-enet-join",
        "a host session and a guest session in one process joined over two ENet sockets on "
        + "127.0.0.1: the sockets link, the host's handshake and roster are held by the guest's "
        + "socket while no session is bound to it, binding replays them, the guest builds on the "
        + "host's seed rather than its own with the whole field in seat order, and the two ends "
        + "keep stepping each other over the real wall clock")]
    internal static void JoinOverEnetLoopback(TestContext ctx)
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

        var hostWire = OpenHost(out int port, out string why);
        if (hostWire == null)
        {
            ctx.Check(false, $"ENet cannot host on {Loopback} in this process: {why}");
            return;
        }

        // Process-global state two sessions in one process share. Restored below so this suite
        // cannot shift the streams, or the ambient clock, of every suite after it in the shard.
        ulong master = Rng.Master;
        bool pinned = Rng.Pinned;
        var clockWas = GameClock.Current;
        var profileWas = StartupProfile.Current;
        EnetTransport? guestWire = null;
        Ends? host = null;
        Ends? guest = null;
        try
        {
            guestWire = EnetTransport.Join(Loopback, port);
            double linked = Pump(hostWire, guestWire,
                () => hostWire.Peers.Count == 1 && guestWire.LinkState == EnetLinkState.Up);
            ctx.Check(hostWire.Peers.Count == 1 && guestWire.LinkState == EnetLinkState.Up,
                $"two sockets link over {Loopback}:{port} in {linked:0.000} s (host peers {hostWire.Peers.Count}, guest link {guestWire.LinkState})");
            if (hostWire.Peers.Count != 1)
            {
                return;
            }

            var roster = new NetSeat[]
            {
                new() { PeerId = hostWire.LocalPeer, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
                new() { PeerId = hostWire.Peers[0], SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
            };
            NetSeats.Validate(roster);

            host = Open(ctx, spec, hostWire, isHost: true, HostSeed, roster);
            ctx.Check(host.Built, $"the host session builds over the ENet carrier");
            if (!host.Built)
            {
                return;
            }

            Held(ctx, host.Session, guestWire);
            guest = Open(ctx, spec, guestWire, isHost: false, GuestSeed, null);
            ctx.Check(guest.Built && guestWire.PendingPayloads == 0,
                $"the guest session builds on the replay, and nothing is left held ({guestWire.PendingPayloads} payloads)");
            if (!guest.Built)
            {
                return;
            }

            Join(ctx, host.Session, guest.Session);
            var link = host.Session.NetLink!;
            var far = guest.Session.NetLink!;
            // The host flies while the guest's socket is held. An aircraft-state sample can land
            // before the guest exists to claim it, and the replay drops that one unclaimed. The
            // next sample follows three steps later, so only a parse failure is a fault.
            int sentAtJoin = link.Sent;
            int receivedAtJoin = far.Received;
            ctx.Check(sentAtJoin >= 2 && receivedAtJoin >= 2 && far.Malformed == 0,
                $"the join's two reliable payloads crossed the socket and nothing since failed to parse (sent {sentAtJoin}, received {receivedAtJoin}, malformed {far.Malformed}, unclaimed before the guest built {far.DroppedUnknown})");
            Lockstep(host.Session, guest.Session);
            ctx.Check(link.Sent > sentAtJoin && far.Received > receivedAtJoin && far.Malformed == 0,
                $"and once both fly, the owners' aircraft state keeps crossing the same socket (sent {sentAtJoin} then {link.Sent}, received {receivedAtJoin} then {far.Received}, malformed {far.Malformed})");
            ctx.Check(hostWire.LinkState == EnetLinkState.Up && guestWire.LinkState == EnetLinkState.Up,
                $"and both links stand after {LockstepSteps} lockstepped frames ({hostWire.LinkState} and {guestWire.LinkState})");
        }
        finally
        {
            guest?.Close();
            host?.Close();
            guestWire?.Close();
            hostWire.Close();
            StartupProfile.Current = profileWas;
            GameClock.Current = clockWas;
            Rng.Reset(master, pinned);
        }
    }

    // The board's own window, which the loopback mesh cannot show: the guest's socket is stepped
    // while no session is bound to it. ABLE-TO-FAIL CONTROL for the hold and replay rule: a
    // carrier that dropped what arrived here would leave the guest's build below unjoined.
    private static void Held(TestContext ctx, GameSession host, EnetTransport guestWire)
    {
        double waited = PumpSession(host, guestWire, () => guestWire.PendingPayloads >= 2);
        ctx.Check(guestWire.PendingPayloads >= 2,
            $"the guest's socket holds the host's answer while no session owns it ({guestWire.PendingPayloads} payloads after {waited:0.000} s)");
    }

    // The seed, the seat and the roster a guest is built from are the host's, and nothing of its
    // own launch survives the join. The peer ids are each end's own, so they are not compared.
    // A guest rebuilds every remote seat against the peer that sent it the roster.
    private static void Join(TestContext ctx, GameSession host, GameSession guest)
    {
        string seeds = $"host {host.MasterSeed:X}, guest {guest.MasterSeed:X}, its own {GuestSeed:X}";
        ctx.Check(host.MasterSeed == HostSeed && guest.MasterSeed == HostSeed,
            $"the guest builds on the host's seed, not the one it was launched with ({seeds})");
        ctx.Same(host.NetSeats.Count, guest.NetSeats.Count, $"the guest's roster is the whole field");
        var pairs = host.NetSeats.Zip(guest.NetSeats).ToArray();
        ctx.Check(pairs.All(p => p.First.SeatIndex == p.Second.SeatIndex
                                 && p.First.TeamId == p.Second.TeamId
                                 && p.First.Callsign == p.Second.Callsign
                                 && p.First.PlaneNode == p.Second.PlaneNode),
            $"every seat crosses intact: {string.Join(", ", guest.NetSeats.Select(s => $"{s.SeatIndex}:{s.Callsign}/{s.PlaneNode}"))}");
        int here = host.NetLink!.LocalSeat;
        int there = guest.NetLink!.LocalSeat;
        ctx.Check(here == 0 && there == 1
                  && host.NetSeats[0].IsLocal && !host.NetSeats[1].IsLocal
                  && !guest.NetSeats[0].IsLocal && guest.NetSeats[1].IsLocal,
            $"and each end flies its own seat alone (host seat {here}, guest seat {there})");
    }

    private static EnetTransport? OpenHost(out int port, out string why)
    {
        why = "no port tried";
        for (int i = 0; i < PortsToTry; i++)
        {
            port = FirstPort + i;
            try
            {
                return EnetTransport.Host(port, maxPeers: 4, bindAddress: Loopback);
            }
            catch (InvalidOperationException e)
            {
                why = e.Message;
            }
        }

        port = 0;
        return null;
    }

    // Steps both raw ends until the condition holds or the give-up passes. The sleep is what lets
    // the loopback socket carry between two polls: ENet runs on the wall clock, not on the step.
    private static double Pump(EnetTransport host, EnetTransport guest, Func<bool> until)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            host.Step(0.001);
            guest.Step(0.001);
            if (until() || watch.Elapsed.TotalSeconds >= WaitSeconds)
            {
                return watch.Elapsed.TotalSeconds;
            }

            Thread.Sleep(1);
        }
    }

    // The same wait with a whole session on the sending end. That is how the host's answer is
    // flushed, since its transport step sits inside its own physics frame.
    private static double PumpSession(GameSession host, EnetTransport guest, Func<bool> until)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest.Step(0.001);
            if (until() || watch.Elapsed.TotalSeconds >= WaitSeconds)
            {
                return watch.Elapsed.TotalSeconds;
            }

            Thread.Sleep(1);
        }
    }

    // Both sessions through the same number of fixed steps, host first, the order a listen server
    // runs in. The sleep is the socket's: a run of tight steps gives ENet no wall time to deliver.
    private static void Lockstep(GameSession host, GameSession guest)
    {
        for (int i = 0; i < LockstepSteps; i++)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest._PhysicsProcess(GameClock.FixedDt);
            Thread.Sleep(1);
        }
    }

    // One end of the match: its own pane, its own world, its own session node. The pane renders
    // nothing, the suite reads the roster and the counters rather than pixels.
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

    // One end's nodes, kept together so the finally block can take both down in one call.
    private sealed record Ends(SubViewport Pane, GameSession Session, bool Built)
    {
        public void Close()
        {
            Session.Free();
            Pane.Free();
        }
    }
}
