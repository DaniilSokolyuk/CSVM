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

    // The scripted flight both owners fly in the replication suite: full throttle in a climbing
    // right-hand roll, so the path curves continuously. A straight line at a constant speed is
    // reconstructed exactly by any interpolator and would measure nothing (METHOD-1).
    private const string TrackedFlight = "--hold=0.6,0.9,0,1";

    // How long that flight is measured for, in SIM STEPS at the fixed step. This harness asks
    // for each step itself, so the reading is in sim time and never in wall time.
    private const int FlightSteps = 240;

    // The alignment search's width, in sim steps, and the index the measurement starts at. It has
    // to cover the buffer delay plus the link's latency and jitter. Starting there also skips the
    // opening steps, where the shown aeroplane holds its spawn because nothing has arrived.
    private const int MaxLagSteps = 30;

    // How finely the lag is fitted, in divisions of one sim step. A whole-step fit leaves half a
    // step of misalignment in the residual, which is most of a metre at these speeds.
    private const int LagFitSteps = 20;

    // The tracking bars, in metres, at the link below. Set from repeated readings of this suite
    // with headroom, not from a standard anybody has stated. What a player will accept is
    // unmeasured, so these are a regression tripwire on a measured number.
    private const float MeanErrorBar = 1.5f;
    private const float WorstErrorBar = 5f;

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
        string missionZrdr = RequireMatchData(ctx);
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

    [Suite("net-aircraft-replication",
        "a host and a guest fly one scripted curve each over a 30 ms, 25 per cent lossy loopback "
        + "mesh: each owner puts its own SIM pose on the wire on the send cadence, the far peer's "
        + "interpolated aeroplane traces that path to a measured error once the deliberate lag is "
        + "fitted out, the fitted lag is the buffer delay plus the link and no more, nearly every "
        + "answer comes out of the buffer interpolating, and the same metric against the OTHER "
        + "aeroplane's path is an order of magnitude worse")]
    internal static void AircraftStateTracksItsOwner(TestContext ctx)
    {
        string missionZrdr = RequireMatchData(ctx);
        var spec = SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=1", "--mute",
            "--no-pads", TrackedFlight,
        });
        var table = new SpawnPicker(spec).LoadSpawnList(missionZrdr, spec.Scenario);
        if (table is not { Count: >= 2 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors no usable net.zrd table");
        }

        // The same link the join is asserted over. Aircraft state is the unreliable sequenced
        // class, so a quarter of these samples never land and the buffer covers the gaps.
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(9311));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        NetSeats.Validate(roster);

        ulong master = Rng.Master;
        bool pinned = Rng.Pinned;
        var clockWas = GameClock.Current;
        var profileWas = StartupProfile.Current;
        Ends? host = null;
        Ends? guest = null;
        try
        {
            host = Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = Open(ctx, spec, mesh[1], isHost: false, GuestSeed, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            var flight = Fly(host.Session, guest.Session);
            Tracking(ctx, flight);
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

    // One tracked flight: both sessions stepped together, with the four paths that matter
    // recorded after every step. The case the guest's buffer answered from is counted
    // beside them.
    private static TrackedRun Fly(GameSession host, GameSession guest)
    {
        var flight = new TrackedRun();
        for (int i = 0; i < FlightSteps; i++)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest._PhysicsProcess(GameClock.FixedDt);
            flight.HostOwn.Add(Pose(host, 0));
            flight.HostShown.Add(Pose(host, 1));
            flight.GuestOwn.Add(Pose(guest, 1));
            flight.GuestShown.Add(Pose(guest, 0));
            // Over the window the error is measured on, not from the first step. Before the link
            // has delivered anything the buffer is empty or holds its oldest sample. That is the
            // opening, and it says nothing about the stream.
            if (i >= MaxLagSteps
                && guest.SeatRigs[0].Controller?.RemotePoses is { } received
                && received.TrySample(out var answer))
            {
                flight.Feeds[(int)answer.Feed]++;
            }

            flight.Stick = host.SeatRigs[0].Controller?.LastCommand ?? default;
            flight.Flying = host.SeatRigs[0].Controller is { InPlay: true }
                            && guest.SeatRigs[1].Controller is { InPlay: true };
        }

        return flight;
    }

    // What the flight proves. The order matters: the scripted curve is established first, because
    // every tracking number below is meaningless over a path nobody flew.
    private static void Tracking(TestContext ctx, TrackedRun flight)
    {
        float flown = Length(flight.HostOwn);
        float turn = Turn(flight.HostOwn);
        var stick = flight.Stick;
        ctx.Check(flight.Flying && flown > 200f && turn > 30f,
            $"the scripted owners fly a curve worth measuring ({flown:0} m flown, {turn:0} degrees of turn, on a held stick of {stick.Pitch:0.0},{stick.Roll:0.0},{stick.Yaw:0.0},{stick.Throttle:0.0}, both still in play {flight.Flying})");

        var there = Track(flight.HostOwn, flight.GuestShown);
        var back = Track(flight.GuestOwn, flight.HostShown);
        foreach (var (name, fit) in new[] { ("guest", there), ("host", back) })
        {
            float seconds = fit.Lag * GameClock.FixedDt;
            ctx.Check(fit.Mean < MeanErrorBar && fit.Max < WorstErrorBar,
                $"the {name}'s remote aeroplane traces its owner's own path to {fit.Mean:0.00} m mean and {fit.Max:0.00} m worst, at a fitted lag of {seconds * 1000f:0} ms");
            // The deliberate part of the delay. Below the buffer's own read-behind the far peer
            // would be guessing ahead; far above the link plus one send interval something is
            // holding samples back.
            float low = RemotePoseBuffer.BufferDelaySeconds;
            float high = RemotePoseBuffer.BufferDelaySeconds + 0.04f
                + (AircraftStateCadence.SendStepInterval * GameClock.FixedDt);
            ctx.Check(seconds >= low - GameClock.FixedDt && seconds <= high,
                $"and that lag is the buffer delay plus the link, nothing more ({seconds * 1000f:0} ms, expected {low * 1000f:0} to {high * 1000f:0} ms)");
        }

        // A quarter of the samples never land, so a minority of answers ride the newest one's
        // velocity by design. What must not happen is a starved answer: that is the buffer out
        // of history altogether, past the cap, showing an aeroplane nobody is steering.
        int answers = flight.Feeds.Sum();
        string census = $"{flight.Feeds[(int)RemotePoseFeed.Interpolating]} interpolating, {flight.Feeds[(int)RemotePoseFeed.Extrapolating]} extrapolating, {flight.Feeds[(int)RemotePoseFeed.Starved]} starved of {answers}";
        ctx.Check(answers > 0 && flight.Feeds[(int)RemotePoseFeed.Starved] == 0
                  && flight.Feeds[(int)RemotePoseFeed.Interpolating] > answers * 3 / 4,
            $"and the buffer answers from two samples over three quarters of the time on a quarter-lossy link, never starved ({census})");

        // ABLE-TO-FAIL CONTROL. The same metric between the guest's reconstruction and the OTHER
        // aeroplane in its own world. A metric that cannot tell two aircraft apart would pass
        // every assertion above while replicating nothing (METHOD-14).
        var wrong = Track(flight.GuestOwn, flight.GuestShown);
        ctx.Check(wrong.Mean > there.Mean * 10f,
            $"ABLE-TO-FAIL CONTROL: matched against the other aeroplane's path the same metric reads {wrong.Mean:0.0} m mean, against {there.Mean:0.00} m for the right one");
    }

    // The error between an owner's own path and the path the far peer showed for it. The shown
    // path is DELIBERATELY late, by the buffer's read-behind plus the link. The lag is fitted
    // first, and the residual at it is the tracking error (METHOD-32). ⚠ The fit is in fractions
    // of a step. A whole-step grid leaves up to half a step of misalignment, most of a metre at
    // 80 m/s. The fitted lag is reported with the error, since a fit at an end of the search is
    // a fit that failed.
    private static (float Lag, float Mean, float Max) Track(
        IReadOnlyList<Vector3> own, IReadOnlyList<Vector3> shown)
    {
        float best = 0f;
        float bestMean = float.MaxValue;
        int n = shown.Count - MaxLagSteps;
        for (int step = 0; step <= MaxLagSteps * LagFitSteps; step++)
        {
            float lag = (float)step / LagFitSteps;
            float sum = 0f;
            for (int i = MaxLagSteps; i < shown.Count; i++)
            {
                sum += shown[i].DistanceTo(Along(own, i - lag));
            }

            if (sum / n < bestMean)
            {
                bestMean = sum / n;
                best = lag;
            }
        }

        float max = 0f;
        for (int i = MaxLagSteps; i < shown.Count; i++)
        {
            max = Mathf.Max(max, shown[i].DistanceTo(Along(own, i - best)));
        }

        return (best, bestMean, max);
    }

    // Where a path was between two of its steps, which is what a fractional lag asks for. The
    // step is short against the curve, so the chord is the path to well under the error measured.
    private static Vector3 Along(IReadOnlyList<Vector3> path, float at)
    {
        int first = Mathf.Clamp((int)Mathf.Floor(at), 0, path.Count - 2);
        return path[first].Lerp(path[first + 1], Mathf.Clamp(at - first, 0f, 1f));
    }

    private static Vector3 Pose(GameSession session, int seat) =>
        session.SeatRigs[seat].Controller?.WorldPosition ?? Vector3.Zero;

    private static float Length(IReadOnlyList<Vector3> path)
    {
        float sum = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            sum += path[i].DistanceTo(path[i - 1]);
        }

        return sum;
    }

    // How far the path bent in total, in degrees: the turn between one step and the next, summed.
    // The angle between the first heading and the last is the wrong measure, since a curve that
    // comes back around reads as straight.
    private static float Turn(IReadOnlyList<Vector3> path)
    {
        float turned = 0f;
        for (int i = 2; i < path.Count; i++)
        {
            var before = path[i - 1] - path[i - 2];
            var after = path[i] - path[i - 1];
            if (before.LengthSquared() > 0f && after.LengthSquared() > 0f)
            {
                turned += Mathf.RadToDeg(before.AngleTo(after));
            }
        }

        return turned;
    }

    // The five data files both ends of a match are built from.
    private static string RequireMatchData(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");
        return missionZrdr;
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
        // as unclaimed. The zero above is therefore a bound handler, not a silent wire. The
        // director's transition is the unclaimed one: a guest in a match claims the match state.
        int unknownWas = far.DroppedUnknown;
        link.Broadcast(new DirectorTransitionMessage(3, 11));
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

    // What one tracked flight recorded: each owner's own sim path, and the path the far peer
    // showed for it. The census of which case the guest's buffer answered from is here too.
    private sealed class TrackedRun
    {
        public List<Vector3> HostOwn { get; } = new();
        public List<Vector3> HostShown { get; } = new();
        public List<Vector3> GuestOwn { get; } = new();
        public List<Vector3> GuestShown { get; } = new();
        public int[] Feeds { get; } = new int[3];
        public bool Flying { get; set; }

        public CSVM.Flight.FlightInput Stick { get; set; }
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
