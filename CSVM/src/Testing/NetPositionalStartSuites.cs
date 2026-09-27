using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Session.Campaign;
using CSVM.Session.Launch;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The host-decided positional start and the airframe swap it leads to, for a guest.
/// The first suite flies a guest into one of CM02's shipped Balmoral capture rows over two built
/// worlds. The second swaps a guest's airframe in two network sessions and reads whether its fire
/// and death still cross.</summary>
internal static class NetPositionalStartSuites
{
    private const int Cm02Seq = 1;
    private const float StepDt = 1f / 60f;

    // As in the episode-owner suite: long enough to outlast the wing walk's own motion.
    private const float PlayBudgetS = 30f;

    // How long one approach is flown for, past the buffer delay and the link.
    private const float ApproachBudgetS = 3f;

    // Where the guest starts along the row's axis, and how it flies it: the landings suites' own.
    private const float AxisFraction = 0.6f;
    private const float ApproachSpeedMps = 45f;
    private const float ApproachThrottle = 0.5f;

    // How far off the Balmoral is staged, and how far above the scripted player waits.
    private const float StagedAheadM = 300f;
    private const float HostWaitsAboveM = 1000f;

    // A group no human starts in. The reading is that the swap copies the captured aircraft's.
    private const int CapturedGroup = 5;

    // How close the host's copy has to follow the guest after the swap: the buffer's error on a
    // held aeroplane, as net-human-field reads it.
    private const float FollowToleranceM = 5f;

    private const ulong Seed = 0xC26A0001UL;

    // The swap-rewire stage: free flight on C3/MP1, as net-human-field flies it.
    private const string SwapChapter = "C3";
    private const string SwapStage = "MP1";
    private const float SwapApartM = 200f;
    private const float CrashWindowS = 5f;

    // As NetCombatSuites holds a burst and lets it land.
    private const int BurstSteps = 60;
    private const int SettleSteps = 20;

    private static readonly string[] Airframes = { "player_bhawk", "player_pfighter" };

    // The airframe order NetCombatSuites.Ends hands every peer.
    private static readonly string[] SessionAirframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-positional-start",
        "a guest flies one of CM02's shipped Balmoral capture rows over two BUILT C3/M05 worlds "
        + "joined by a 30 ms, 25 per cent lossy loopback: with the host's trigger over its panes "
        + "alone nothing starts anywhere, and the guest's replicated trigger starts nothing of its "
        + "own; over the whole field the host starts the row for the guest's seat and the guest "
        + "replays it, both episodes belong to the guest's seat, the capture's swap rebuilds the "
        + "host's copy of the guest still fed by the guest's samples and in the captured Balmoral's "
        + "group, the guest's own aeroplane likewise, the captured Balmoral leaves both worlds and "
        + "the host's own aeroplane is untouched")]
    internal static void PositionalStart(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = CampaignSequence.Load(ctx.ZrdrPath).Cast<CampaignMission?>()
                .FirstOrDefault(m => m!.Value.Seq == Cm02Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm02Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm02Seq} -> {chapter}/{folder}");
        var savedClock = GameClock.Current;
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder, hostWorld =>
                ctx.WithWorld(chapter, collision: false, folder, guestWorld =>
                {
                    if (ReferenceEquals(hostWorld.Runtime, guestWorld.Runtime))
                    {
                        throw new SuiteSkippedException($"{chapter}/{folder} is this run's cached world, so a second build is the same one");
                    }

                    Drive(ctx, chapter, hostWorld, guestWorld, report);
                }));
        }
        finally
        {
            ctx.CutsceneRoots = false;
            GameClock.Current = savedClock;
        }

        ctx.WriteArtifact($"test-net-positional-start-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"{report.ToString().TrimEnd().Replace(System.Environment.NewLine, "; ")}");
    }

    [Suite("net-swap-rewire",
        "a host and a guest session over a lossless loopback, both running the "
        + "capture code's airframe swap on the guest's seat as a replayed episode does: the host's "
        + "rebuilt copy still follows the guest's samples, every round the guest's new aeroplane "
        + "fires is built again on the host, and the guest's death report plays its wreck there; "
        + "the same swap with the rewire withheld leaves the copy without its hit route and the "
        + "host building none of the guest's rounds")]
    internal static void SwapRewire(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, SwapChapter), $"{SwapChapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, SwapChapter), $"{SwapChapter} gamez");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, SwapChapter, SwapStage), $"{SwapChapter}/{SwapStage} zrdr");
        AirframeSwapCode? found = null;
        foreach (var code in AirframeSwapCodes.Table)
        {
            if (found == null && AirframeHandover.CarriesCapturedGroup(code))
            {
                found = code;
            }
        }

        var capture = found ?? throw new SuiteSkippedException("no airframe swap code carries a captured group");

        var spec = SessionSpec.Parse(new[]
        {
            "--fly", $"--chapter={SwapChapter}", $"--mission={SwapStage}", "--players=1", "--mute", "--no-pads",
        });
        // Lossless: a fire event is unreliable by design, and the census counts every round.
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(2627));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = SessionAirframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = SessionAirframes[1] },
        };
        var report = new StringBuilder();
        var ambient = NetCombatSuites.Ambient.Save();
        NetCombatSuites.Ends? host = null;
        NetCombatSuites.Ends? guest = null;
        try
        {
            host = NetCombatSuites.Ends.Open(ctx, spec, mesh[0], isHost: true, Seed, roster);
            guest = NetCombatSuites.Ends.Open(ctx, spec, mesh[1], isHost: false, Seed + 1, null);
            ctx.Check(host.Built && guest.Built, $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            Rewire(ctx, host.Session, guest.Session, capture, report);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            ambient.Restore();
        }

        ctx.WriteArtifact($"test-net-swap-rewire-{SwapChapter}-{SwapStage}.txt", report.ToString());
        ctx.Note($"{report.ToString().TrimEnd().Replace(System.Environment.NewLine, "; ")}");
    }

    private static void Drive(TestContext ctx, string chapter, TestWorld hostWorld, TestWorld guestWorld,
        StringBuilder report)
    {
        var capture = CaptureRow(ctx, hostWorld)
            ?? throw new SuiteSkippedException($"{chapter}'s landings table carries no row whose closure raises an airframe swap");
        report.AppendLine($"row '{capture.Row.Anim}' node '{capture.Row.Node}' auto={capture.Row.Auto} " +
            $"owner '{capture.Owner}', callback {capture.Code} from '{capture.Root}'");
        ctx.Check(string.Equals(capture.Owner, capture.Root, StringComparison.OrdinalIgnoreCase),
            $"the row's approach node hangs under '{capture.Owner}', the aircraft its capture code is raised from");

        GameClock.Current = new GameClock { Mode = GameClock.RunMode.Realtime };
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(2626));
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
        using var host = End.Open(ctx, "host", hostWorld, chapter, capture, roster, local: 0);
        using var guest = End.Open(ctx, "guest", guestWorld, chapter, capture, guestNet.Seats.ToArray(), local: 1);
        host.Pipe(hostNet);
        guest.Pipe(guestNet);
        bool overSeats = false;
        host.Trigger.Bind(host.World.Runtime, host.Rows, host.Cutscene,
            () => overSeats ? host.Seats : host.Panes);
        guest.Trigger.Bind(guest.World.Runtime, guest.Rows, guest.Cutscene, () => guest.Seats);
        var hostLink = NetPositionalStartLink.Open(hostNet, () => host.Seats, host.Trigger, null);
        var guestLink = NetPositionalStartLink.Open(guestNet, () => guest.Seats, guest.Trigger, null);
        var frame = new Frame(host, guest, hostNet, guestNet, guestLink);

        var guestOwn = guest.Seats[1];
        var hostCopy = host.Seats[1];
        var hostP1 = host.Seats[0].Controller!;
        var p1Before = hostP1;
        host.Park(hostP1, host.Staged.WorldPosition + (Vector3.Up * HostWaitsAboveM));

        // ABLE-TO-FAIL CONTROL: the trigger as it was bound before guests joined, over the panes.
        guest.FlyInto(guestOwn.Controller!, capture.Row);
        frame.Run(ApproachBudgetS, () => false);
        report.AppendLine($"panes only: host started '{host.Trigger.LastStarted ?? "(none)"}', guest started '{guest.Trigger.LastStarted ?? "(none)"}', copy offered press={hostCopy.Controller!.RemoteAutoLand}");
        ctx.Check(host.Trigger.LastStarted == null,
            $"CONTROL: with the host's trigger over its panes alone, the guest flying '{capture.Row.Node}' starts nothing on the host");
        ctx.Check(guest.Trigger.Replicated && guest.Trigger.LastStarted == null,
            $"and the guest's replicated trigger starts nothing of its own although its pane flew the row");

        overSeats = true;
        guest.FlyInto(guestOwn.Controller!, capture.Row);
        var copyBefore = hostCopy.Controller!;
        var ownBefore = guestOwn.Controller!;
        float started = frame.Run(ApproachBudgetS, () => guest.Trigger.LastStarted != null);
        report.AppendLine($"whole field: host started '{host.Trigger.LastStarted ?? "(none)"}' by seat {host.Trigger.LastStartedBy?.ToString() ?? "-"}, " +
            $"guest replayed '{guest.Trigger.LastStarted ?? "(none)"}' by seat {guest.Trigger.LastStartedBy?.ToString() ?? "-"} at t={started:0.00}s; " +
            $"links sent {hostLink.RowStarts}, replayed {guestLink.RowStarts}, refused {guestLink.RowsRefused}");
        ctx.Check(host.Trigger.LastStartedBy == 1 && host.Trigger.LastStarted == capture.Row.Anim,
            $"over the whole field the host starts '{capture.Row.Anim}' for the guest's seat off its copy of the guest");
        ctx.Check(guest.Trigger.LastStartedBy == 1 && guestLink.RowStarts == 1 && guestLink.RowsRefused == 0,
            $"and the guest replays that start for its own seat");
        ctx.Check(ReferenceEquals(host.Cutscene.EpisodeOwner, hostCopy) && ReferenceEquals(guest.Cutscene.EpisodeOwner, guestOwn),
            $"on both machines the episode belongs to the guest's seat, the host's copy there and the guest's own pane here");

        frame.Run(PlayBudgetS, () => false);
        CheckSwapped(ctx, "host", hostCopy, copyBefore, host, capture, remote: true, report);
        CheckSwapped(ctx, "guest", guestOwn, ownBefore, guest, capture, remote: false, report);
        ctx.Check(ReferenceEquals(host.Seats[0].Controller, p1Before),
            $"the host's own aeroplane is the one it was flying, so the swap followed the seat that flew the row");

        // The feed is looked up by seat on every arrival, so a rebuilt copy takes the next sample.
        guest.Park(guestOwn.Controller!, guest.Staged.WorldPosition + (Vector3.Left * StagedAheadM));
        frame.Run(1f, () => false);
        float off = hostCopy.Controller!.WorldPosition.DistanceTo(guestOwn.Controller!.WorldPosition);
        report.AppendLine($"after the swap the host's copy stands {off:0.00} m from the guest's aeroplane");
        ctx.Check(off < FollowToleranceM,
            $"and the host's rebuilt copy still follows the guest's own samples ({off:0.00} m off)");
    }

    private static void Rewire(TestContext ctx, GameSession host, GameSession guest, AirframeSwapCode capture,
        StringBuilder report)
    {
        var hostPin = host.SeatRigs[0].Controller!.WorldPosition;
        Pin(host.SeatRigs[0].Controller!, hostPin);
        Pin(guest.SeatRigs[1].Controller!, hostPin + (Vector3.Right * SwapApartM));
        Lockstep(SettleSteps, host, guest);

        // ABLE-TO-FAIL CONTROL: the swap as it ran before, leaving the replacement off the wire.
        host.SkipSwapRewire = true;
        guest.SkipSwapRewire = true;
        Swap(host, guest, capture);
        int unwired = Burst(host, guest);
        bool routed = host.SeatRigs[1].Controller!.HitRouter != null;
        report.AppendLine($"rewire withheld: host rebuilt {unwired} of the guest's rounds, copy hit route {routed}");
        ctx.Check(unwired == 0 && !routed,
            $"CONTROL: with the rewire withheld the host builds none of the swapped guest's rounds ({unwired}) and its copy has no hit route");

        host.SkipSwapRewire = false;
        guest.SkipSwapRewire = false;
        var copyBefore = host.SeatRigs[1].Controller!;
        Swap(host, guest, capture);
        var copy = host.SeatRigs[1].Controller!;
        var own = guest.SeatRigs[1].Controller!;
        Pin(own, hostPin + (Vector3.Left * SwapApartM));
        Lockstep(SettleSteps * 3, host, guest);
        float off = copy.WorldPosition.DistanceTo(own.WorldPosition);
        ctx.Check(!ReferenceEquals(copy, copyBefore) && copy.RemoteOwned && off < FollowToleranceM,
            $"the host's rebuilt copy is still fed from the guest's samples ({off:0.00} m from the guest's aeroplane)");
        ctx.Check(copy.HitRouter != null, $"…and routes the hits it takes, so a round striking it is decided by its shooter");

        int fired = Burst(host, guest, out int rebuilt);
        report.AppendLine($"rewired: the guest fired {fired} round(s), the host rebuilt {rebuilt}; copy {off:0.00} m off");
        ctx.Check(fired > 0 && rebuilt == fired,
            $"every round the swapped guest fires is built again on the host ({fired} fired, {rebuilt} rebuilt)");

        own.Held = false;
        own.DebugForceCrash();
        float crossed = 0f;
        for (; crossed < CrashWindowS && !copy.Crashed; crossed += GameClock.FixedDt)
        {
            Lockstep(1, host, guest);
        }

        report.AppendLine($"the guest's death crossed in {crossed * 1000f:0} ms (copy crashed {copy.Crashed})");
        ctx.Check(copy.Crashed, $"and the swapped guest's death report plays its wreck on the host {crossed * 1000f:0} ms later");
    }

    // Both machines run the capture code's swap on the guest's seat, as each one's replay of the
    // episode raises it there.
    private static void Swap(GameSession host, GameSession guest, AirframeSwapCode capture)
    {
        foreach (var session in new[] { host, guest })
        {
            session.Cutscene!.SwapAirframe!(new AirframeSwapOrder(capture, null, session.SeatRigs[1]));
        }

        Lockstep(SettleSteps, host, guest);
    }

    private static int Burst(GameSession host, GameSession guest)
    {
        Burst(host, guest, out int rebuilt);
        return rebuilt;
    }

    // The guest's own aeroplane fires; the host counts what it builds for the guest's seat.
    private static int Burst(GameSession host, GameSession guest, out int rebuilt)
    {
        var shooter = guest.SeatRigs[1].Controller!;
        var here = shooter.Projectiles!;
        var there = host.SeatRigs[1].Controller!.Projectiles!;
        here.ScoredShooters.Add(shooter.PlayerIndex);
        there.ScoredShooters.Add(host.SeatRigs[1].Controller!.PlayerIndex);
        int mineBefore = here.CannonRoundsFired;
        int theirsBefore = there.CannonRoundsFired;
        shooter.AutoFire = true;
        Lockstep(BurstSteps, host, guest);
        shooter.AutoFire = false;
        Lockstep(SettleSteps, host, guest);
        rebuilt = there.CannonRoundsFired - theirsBefore;
        return here.CannonRoundsFired - mineBefore;
    }

    private static void Pin(FlightController pilot, Vector3 at)
    {
        pilot.Held = true;
        pilot.PlaceHeld(at, at + Vector3.Forward);
    }

    private static void Lockstep(int steps, params GameSession[] sessions)
    {
        for (int i = 0; i < steps; i++)
        {
            foreach (var session in sessions)
            {
                session._PhysicsProcess(GameClock.FixedDt);
            }
        }
    }

    private static void CheckSwapped(TestContext ctx, string who, PlayerRig seat, FlightController before,
        End end, Capture capture, bool remote, StringBuilder report)
    {
        var after = seat.Controller;
        report.AppendLine($"{who}: seat 1 '{before.Loadout?.Def.Def ?? "-"}' -> '{after?.Loadout?.Def.Def ?? "-"}' " +
            $"remote={after?.RemoteOwned} group={after?.Group} captured inert={end.Staged.Inert}");
        ctx.Check(after != null && !ReferenceEquals(after, before)
                  && string.Equals(after.Loadout?.Def.Def, capture.Wanted.Def, StringComparison.OrdinalIgnoreCase),
            $"on the {who}, the guest's seat is rebuilt on '{capture.Wanted.Def}', the record callback {capture.Code} names");
        ctx.Check(after?.RemoteOwned == remote,
            $"…{(remote ? "still a copy fed from the guest's samples" : "and still flown here")} on the {who}");
        ctx.Check(after?.Group == CapturedGroup,
            $"…and in the captured Balmoral's group on the {who}, which a DEDG or LiveInGroup read there counts");
        ctx.Check(end.Staged.Inert,
            $"and the captured Balmoral has left the {who}'s world");
    }

    // The shipped row whose definition closure raises an airframe swap. It comes with the aircraft
    // its approach node hangs under and the definition root the code is raised from.
    private static Capture? CaptureRow(TestContext ctx, TestWorld world)
    {
        var rows = LandingApproaches.Resolve(SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter),
            world.Gamez, name => world.Runtime.Handles(name));
        foreach (var row in rows)
        {
            foreach (var def in world.Runtime.CallClosureOf(row.Anim))
            {
                foreach (var ev in def.Sequences.SelectMany(s => s.Events))
                {
                    if (ev.Kind == "Callback" && AirframeSwapCodes.For((int)(ev.Data.Num("value") ?? -1f)) is { } wanted
                        && AirframeHandover.CarriesCapturedGroup(wanted))
                    {
                        string root = def.RootName is { Length: > 0 } named ? named : def.Name;
                        return new Capture(rows, row, LandingApproachSuites.TopAncestorOf(world.Gamez, row.Node),
                            root, wanted.Code, wanted);
                    }
                }
            }
        }

        return null;
    }

    private sealed record Capture(IReadOnlyList<LandingApproach> Rows, LandingApproach Row, string Owner,
        string Root, int Code, AirframeSwapCode Wanted);

    // Both ends through one step, the host first, as a listen server runs.
    private sealed class Frame
    {
        private readonly End _host;
        private readonly End _guest;
        private readonly NetSession _hostNet;
        private readonly NetSession _guestNet;
        private readonly NetPositionalStartLink _guestLink;

        public Frame(End host, End guest, NetSession hostNet, NetSession guestNet, NetPositionalStartLink guestLink)
        {
            _host = host;
            _guest = guest;
            _hostNet = hostNet;
            _guestNet = guestNet;
            _guestLink = guestLink;
        }

        // Steps until the condition holds or the window runs out, answering the seconds it took.
        public float Run(float seconds, Func<bool> done)
        {
            float t = 0f;
            for (; t < seconds && !done(); t += StepDt)
            {
                GameClock.Current!.BeginFrame(StepDt);
                _hostNet.Step(StepDt);
                _guestNet.Step(StepDt);
                _host.Step(_hostNet);
                _guest.Step(_guestNet);
                _guestLink.Step();
            }

            return t;
        }
    }

    // One machine: its world, its roster of two seats, its cutscene host and its trigger.
    private sealed class End : IDisposable
    {
        private readonly TestContext _ctx;
        private readonly TextureArchive _textures;
        private readonly ProjectilePool _pool;
        private readonly SubViewport _pane;
        private readonly Node3D _home;
        private readonly FlightRoster _roster;
        private readonly int _local;
        private readonly ushort[] _sequence = new ushort[2];
        private Func<int, string?, string?, bool>? _savedHost;
        private Action<string>? _savedOwner;

        private End(TestContext ctx, TestWorld world, TextureArchive textures, ProjectilePool pool,
            SubViewport pane, Node3D home, FlightRoster roster, PlayerRig[] seats, int local, Capture capture)
        {
            _ctx = ctx;
            World = world;
            _textures = textures;
            _pool = pool;
            _pane = pane;
            _home = home;
            _roster = roster;
            Seats = seats;
            _local = local;
            Panes = new[] { seats[local] };
            Rows = capture.Rows;
            Cutscene = new CutsceneController();
            ctx.Host.AddChild(Cutscene);
            Trigger = new LandingApproachRuntime();
            ctx.Host.AddChild(Trigger);
            Staged = null!;
        }

        public TestWorld World { get; }

        public PlayerRig[] Seats { get; }

        public PlayerRig[] Panes { get; }

        public IReadOnlyList<LandingApproach> Rows { get; }

        public CutsceneController Cutscene { get; }

        public LandingApproachRuntime Trigger { get; }

        public FlightController Staged { get; private set; }

        public static End Open(TestContext ctx, string name, TestWorld world, string chapter, Capture capture,
            IReadOnlyList<NetSeat> netSeats, int local)
        {
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
            var pool = new ProjectilePool(textures, null, null);
            ctx.Host.AddChild(pool);
            var pane = new SubViewport();
            ctx.Host.AddChild(pane);
            var seats = new PlayerRig[2];
            var ordered = netSeats.OrderBy(s => s.SeatIndex).ToArray();
            for (int i = 0; i < seats.Length; i++)
            {
                seats[i] = i == local
                    ? new PlayerRig { Index = i, Camera = ctx.Camera, HudParent = pane, Viewport = pane }
                    : new PlayerRig { Index = i, Camera = null!, HudParent = pane, VisualLayer = 0 };
            }

            var home = new Node3D { Name = name };
            ctx.Host.AddChild(home);
            var roster = CoopEpisodeOwnerSuites.BuildRoster(ctx, chapter, textures, pool, seats, ordered, home);
            roster.BuildPlayers(seats);
            var end = new End(ctx, world, textures, pool, pane, home, roster, seats, local, capture);
            end.Stage(capture, name);
            return end;
        }

        // The session's own pose feed: a sample goes to the seat it names, looked up on arrival.
        public void Pipe(NetSession net) =>
            net.On<AircraftStateMessage>((_, sample) =>
            {
                if (sample.Seat < Seats.Length)
                {
                    Seats[sample.Seat].Controller?.RemotePoses?.Receive(sample);
                }
            });

        public void Park(FlightController plane, Vector3 at)
        {
            plane.Held = true;
            plane.PlaceHeld(at, at + Vector3.Forward);
        }

        public void FlyInto(FlightController plane, LandingApproach row)
        {
            var frame = World.Runtime.FindNodes(row.Node)[0].GlobalTransform;
            plane.Held = false;
            plane.AutoLand = row.Auto;
            plane.Setup(new FlightModel(PlaneStats.Load(_ctx.ZrdrPath, Airframes[_local])), null,
                new CamParams(), frame * (row.Apex + ((row.BaseCentre - row.Apex) * AxisFraction)),
                frame * row.Apex, ApproachThrottle, ApproachSpeedMps);
        }

        public void Step(NetSession net)
        {
            foreach (var seat in Seats)
            {
                seat.Controller?.SimStep(StepDt);
            }

            if (Seats[_local].Controller is { } own)
            {
                var stick = own.LastCommand;
                net.Broadcast(new AircraftStateMessage((byte)_local, _sequence[_local]++, own.WorldPosition,
                    own.Attitude.GetRotationQuaternion(), own.WorldVelocity, own.Throttle,
                    stick.Roll, stick.Pitch, stick.Yaw, own.Nitro.Boosting), NetChannels.ForSeat(_local));
            }

            World.Runtime.Advance(StepDt);
            Trigger.Tick();
            Cutscene.Tick();
        }

        public void Dispose()
        {
            World.Runtime.PlayerPositions = null;
            World.Runtime.CallbackHost = _savedHost;
            World.Runtime.MissionTriggerOwner = _savedOwner;
            var members = new List<FlightController>(_roster.AiAircraft);
            _roster.ClearMembership();
            foreach (var seat in Seats)
            {
                seat.Controller?.Free();
            }

            foreach (var ai in members)
            {
                ai.Free();
            }

            Trigger.Free();
            Cutscene.Free();
            _pane.Free();
            _home.Free();
            _pool.Free();
            _textures.Dispose();
        }

        // The captured Balmoral, staged where both worlds put it. It carries the marker scaffolding
        // its roster block authors (the row's approach node among it) and its row armed. The arming is
        // the director's, which a guest replays; the landings suites drive the mission's own gate.
        private void Stage(Capture capture, string name)
        {
            var lead = Seats[0].Controller!;
            Staged = CoopEpisodeOwnerSuites.StageAi(_roster, capture.Root, capture.Wanted.PlaneNode,
                lead.WorldPosition + (lead.NoseDirection * StagedAheadM));
            Staged.Group = CapturedGroup;
            int grafted = RosterMarkers.Attach(World.Gamez, World.Session.Builder.Scene, World.Runtime,
                capture.Owner, Staged);
            var nodes = World.Runtime.FindNodes(capture.Row.Node);
            var arm = nodes.Count > 0 ? World.Runtime.FindNodes(LandingApproaches.ArmNode, nodes[0]) : Array.Empty<Node3D>();
            if (arm.Count > 0)
            {
                arm[0].Visible = true;
            }

            _ctx.Check(grafted > 0 && arm.Count > 0,
                $"the {name}'s staged '{capture.Root}' carries the row's approach node and its land_on ({grafted} graft(s))");

            Cutscene.BindWorld(World.Runtime, null);
            Cutscene.HostDefinitions(ClosureOf(capture.Rows));
            Cutscene.BindRigs(Panes, () => _roster.AiAircraft, Seats[0]);
            Cutscene.SwapAirframe = order => _roster.RunSwap(order.Owner ?? Seats[0], order, handsOver: false);
            _savedHost = World.Runtime.CallbackHost;
            _savedOwner = World.Runtime.MissionTriggerOwner;
            World.Runtime.CallbackHost = Cutscene.Host;
            World.Runtime.PlayerPositions = () => Seats.Where(s => s.Controller != null)
                .Select(s => s.Controller!.WorldPosition).ToArray();
        }

        private List<string> ClosureOf(IReadOnlyList<LandingApproach> rows)
        {
            var names = new List<string>();
            foreach (var row in rows)
            {
                foreach (var def in World.Session.Program.Subset(new[] { row.Anim }).Defs)
                {
                    if (def.AnimName is { } anim && !names.Contains(anim))
                    {
                        names.Add(anim);
                    }
                }
            }

            return names;
        }
    }
}
