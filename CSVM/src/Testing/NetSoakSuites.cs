using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A scripted Dogfight between a host and a guest session, flown through a small matrix
/// of link conditions on one pair of worlds. Each cell reads the desync instruments: position
/// error against the owner's own path, dropped and late messages, and events out of their order.
/// The rig is <see cref="NetCombatSuites"/>'s, and the conditions change between cells through
/// <see cref="LoopbackTransport.SetConditions"/>, so the matrix costs one build.</summary>
internal static class NetSoakSuites
{
    private const ulong HostSeed = 0x50A150A1UL;

    // The mesh's own draws. Any seed replays exactly; this one is the run the bars were read off.
    private const int MeshSeed = 7717;

    // The scripted flight each owner flies in every cell, a climbing right-hand roll. The path
    // curves, so an interpolator has something to get wrong (METHOD-1).
    private const string TrackedFlight = "--hold=0.6,0.9,0,1";

    // Sim steps of each cell's flight phase, the stretch position error is read over. The first
    // NetSessionSuites.Track skips is the buffer refilling after the previous cell's respawns.
    private const int FlightSteps = 240;

    // Sim steps between two hit claims a shooter makes on its copy of a victim, a gun's 20 Hz.
    private const int ClaimInterval = 3;

    // Sim steps a shooter is already claiming for before its victim's owner crashes it.
    private const int ClaimLead = 12;

    // How long a shooter's copy is given to leave the fight after the owner's crash, in steps.
    private const int DeathSteps = 90;

    // How long both aeroplanes are given to be back in play after a kill, in steps. It covers the
    // crash wait below, the ask and the grant, over the slowest cell's link.
    private const int ReturnSteps = 240;

    private const float QuickRespawn = 0.5f;

    private const int SettleSteps = 30;

    // Steps of clean link at the end with both guns held. The last loss of every stream then sits
    // behind a delivered payload, so the inferred count can meet the carrier's.
    private const int FlushFireSteps = 12;

    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    // The matrix. The clean cell is the instruments' own control. The other three step latency,
    // jitter and loss up together, from a good broadband link to a poor wireless one.
    private static readonly Cell[] Matrix =
    {
        new("clean", LoopbackConditions.Perfect, 0.25f, 0.5f),
        new("50ms/5%", new LoopbackConditions(0.05, 0.01, 0.05), 1.5f, 3f),
        new("100ms/10%", new LoopbackConditions(0.10, 0.02, 0.10), 2f, 6f),
        new("200ms/20%", new LoopbackConditions(0.20, 0.04, 0.20), 3.5f, 10f),
    };

    [Suite("net-soak",
        "a host and a guest fly a scripted Dogfight through four link cells from clean to 200 ms "
        + "with 20 per cent loss, each cell a curve flown under fire and a kill each way: every cell "
        + "reports position error against the owner's own path, dropped and late messages and "
        + "events out of order, the reliable events never leave their causal order, the clean "
        + "cell drops nothing, the 50 ms cell discards nothing as overtaken because fire rides "
        + "apart from state, the drop count each machine infers from its sequence gaps equals "
        + "what the carrier really lost or discarded, and an injected respawn nobody died for is "
        + "counted")]
    internal static void SoakTheLink(TestContext ctx)
    {
        var spec = SoakSpec(ctx);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath);
        var gun = weapons.All.FirstOrDefault(w => w.IsCannon && w.HealthDamage is > 0f);
        if (gun == null)
        {
            throw new SuiteSkippedException($"the weapon catalogue holds no cannon with health damage");
        }

        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(MeshSeed));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        NetSeats.Validate(roster);

        var ambient = NetCombatSuites.Ambient.Save();
        NetCombatSuites.Ends? host = null;
        NetCombatSuites.Ends? guest = null;
        try
        {
            host = NetCombatSuites.Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = NetCombatSuites.Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            var pair = new Pair(host.Session, guest.Session, mesh, gun);
            foreach (var session in pair.Both)
            {
                foreach (var rig in session.SeatRigs)
                {
                    if (rig.Controller is { } pilot)
                    {
                        pilot.AutoRespawnAfter = QuickRespawn;
                    }
                }
            }

            pair.Step(SettleSteps);
            var results = Matrix.Select(cell => RunCell(pair, cell)).ToList();
            foreach (var result in results)
            {
                ctx.Note($"{result.Describe()}");
            }

            Judge(ctx, results);
            Truth(ctx, pair);
            InjectedViolation(ctx, pair);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    // One cell: the conditions both ways, a flight under fire read for position error, then a
    // kill each way, then both aeroplanes back in play. Every number is this cell's own delta.
    private static CellResult RunCell(Pair pair, Cell cell)
    {
        pair.Mesh[0].SetConditions(1, cell.Conditions);
        pair.Mesh[1].SetConditions(0, cell.Conditions);
        int lostBefore = pair.Mesh.Sum(m => m.Lost);
        int discardedBefore = pair.Mesh.Sum(m => m.DiscardedStale);
        var hostBefore = pair.Host.NetLink!.Instruments.Reading;
        var guestBefore = pair.Guest.NetLink!.Instruments.Reading;
        foreach (var buffer in pair.Buffers())
        {
            buffer.ResetTally();
        }

        var flight = Fly(pair);
        var flown = pair.Buffers().Aggregate(default(RemotePoseTally), (sum, b) => sum.Plus(b.Tally));
        var killed = Kill(pair, shooterIsHost: true);
        var killedBack = Kill(pair, shooterIsHost: false);
        int returned = Return(pair);
        pair.Step(SettleSteps);

        var there = NetSessionSuites.Track(flight.HostOwn, flight.GuestShown);
        var back = NetSessionSuites.Track(flight.GuestOwn, flight.HostShown);
        var poses = pair.Buffers().Aggregate(default(RemotePoseTally), (sum, b) => sum.Plus(b.Tally));
        return new CellResult(
            cell, there, back, flown, poses,
            pair.Host.NetLink!.Instruments.Reading - hostBefore,
            pair.Guest.NetLink!.Instruments.Reading - guestBefore,
            flight.RoundsFired, new[] { killed, killedBack }, returned, flight.Flying,
            pair.Mesh.Sum(m => m.Lost) - lostBefore, pair.Mesh.Sum(m => m.DiscardedStale) - discardedBefore);
    }

    // Both owners fly their held curve with the guns down, and the four paths are recorded.
    private static Flight Fly(Pair pair)
    {
        var flight = new Flight();
        var hostGun = pair.Host.SeatRigs[0].Controller!;
        var guestGun = pair.Guest.SeatRigs[1].Controller!;
        // The pool counts only a scored shooter's rounds.
        hostGun.Projectiles?.ScoredShooters.Add(hostGun.PlayerIndex);
        guestGun.Projectiles?.ScoredShooters.Add(guestGun.PlayerIndex);
        int roundsBefore = Rounds(hostGun) + Rounds(guestGun);
        hostGun.AutoFire = true;
        guestGun.AutoFire = true;
        for (int i = 0; i < FlightSteps; i++)
        {
            pair.Step(1);
            flight.HostOwn.Add(Pose(pair.Host, 0));
            flight.HostShown.Add(Pose(pair.Host, 1));
            flight.GuestOwn.Add(Pose(pair.Guest, 1));
            flight.GuestShown.Add(Pose(pair.Guest, 0));
        }

        hostGun.AutoFire = false;
        guestGun.AutoFire = false;
        flight.RoundsFired = Rounds(hostGun) + Rounds(guestGun) - roundsBefore;
        flight.Flying = pair.Both.All(s => s.SeatRigs.All(r => r.Controller is { InPlay: true }));
        return flight;
    }

    // One kill the way a match has it. The shooter keeps claiming hits on its copy of the victim
    // until the death reaches it. The victim's own machine is where the aeroplane goes down.
    // Answers the steps the death took to reach the shooter, which is the late-hit window.
    private static int Kill(Pair pair, bool shooterIsHost)
    {
        var shooter = shooterIsHost ? pair.Host : pair.Guest;
        var owner = shooterIsHost ? pair.Guest : pair.Host;
        int shooterSeat = shooterIsHost ? 0 : 1;
        int victimSeat = 1 - shooterSeat;
        var copy = shooter.SeatRigs[victimSeat].Controller!;
        int gunner = shooter.SeatRigs[shooterSeat].Controller!.PlayerIndex;
        for (int step = 0; step < ClaimLead + DeathSteps; step++)
        {
            if (step == ClaimLead && owner.SeatRigs[victimSeat].Controller is { InPlay: true } victim)
            {
                victim.DebugForceCrash(owner.SeatRigs[shooterSeat].Controller!.PlayerIndex);
            }

            if (step >= ClaimLead && !copy.InPlay)
            {
                return step - ClaimLead;
            }

            if (step % ClaimInterval == 0 && copy.InPlay && copy.Body is { } body)
            {
                body.TakeProjectileHit(pair.Gun, copy.WorldPosition, 0, gunner);
            }

            pair.Step(1);
        }

        return DeathSteps;
    }

    // Steps until every aeroplane on both machines is flying again, answering how many that took.
    private static int Return(Pair pair)
    {
        for (int step = 1; step <= ReturnSteps; step++)
        {
            pair.Step(1);
            if (pair.Both.All(s => s.SeatRigs.All(r => r.Controller is { InPlay: true })))
            {
                return step;
            }
        }

        return ReturnSteps;
    }

    // What every cell must show, and the bars each cell's own conditions set. The reliable
    // classes are asserted whatever the link: an order violation or a stale arrival is a broken
    // guarantee, never a bad network.
    private static void Judge(TestContext ctx, IReadOnlyList<CellResult> results)
    {
        foreach (var r in results)
        {
            ctx.Check(r.Flying && r.Returned < ReturnSteps && r.DeathSteps.All(s => s < DeathSteps),
                $"{r.Cell.Name}: both aeroplanes flew the whole curve, each kill reached its shooter ({r.DeathSteps[0]} and {r.DeathSteps[1]} steps) and both were back in play after {r.Returned} steps");
            ctx.Check(r.Host.OrderViolations == 0 && r.Guest.OrderViolations == 0
                      && r.Host.StaleArrivals == 0 && r.Guest.StaleArrivals == 0,
                $"{r.Cell.Name}: no reliable event out of its causal order and no sequenced payload behind a newer one (order {r.Host.OrderViolations}/{r.Guest.OrderViolations}, stale {r.Host.StaleArrivals}/{r.Guest.StaleArrivals})");
            float mean = Math.Max(r.There.Mean, r.Back.Mean);
            float worst = Math.Max(r.There.Max, r.Back.Max);
            ctx.Check(mean < r.Cell.MeanBar && worst < r.Cell.WorstBar,
                $"{r.Cell.Name}: position error {mean.ToString("0.00", CultureInfo.InvariantCulture)} m mean, {worst.ToString("0.00", CultureInfo.InvariantCulture)} m worst, under {r.Cell.MeanBar} m and {r.Cell.WorstBar} m");
        }

        var clean = results[0];
        // Starved only over the flight. After a respawn a buffer starts empty, and its first reads
        // starve on any link until the render delay is covered.
        ctx.Check(clean.Host.Dropped == 0 && clean.Guest.Dropped == 0 && clean.Flown.Starved == 0,
            $"the clean cell drops nothing and starves no read in flight (dropped {clean.Host.Dropped}/{clean.Guest.Dropped}, starved {clean.Flown.Starved})");

        // State samples 50 ms apart cannot swap under 10 ms of jitter. A discard in this cell is
        // a payload judged against another stream sharing its channel.
        var broadband = results[1];
        ctx.Check(broadband.Discarded == 0,
            $"the {broadband.Cell.Name} cell's carriers discard nothing as overtaken, since each seat's fire rides apart from its state (discarded {broadband.Discarded}, {broadband.Host.ReorderedFire + broadband.Guest.ReorderedFire} fire event(s) drawn out of order)");

        // ABLE-TO-FAIL CONTROL. The instruments must move with the link: the worst cell drops
        // more than the clean one and its kills reach the shooter later.
        var worstCell = results[^1];
        ctx.Check(worstCell.Host.Dropped + worstCell.Guest.Dropped > 0
                  && worstCell.DeathSteps.Sum() > clean.DeathSteps.Sum(),
            $"ABLE-TO-FAIL CONTROL: the {worstCell.Cell.Name} cell drops {worstCell.Host.Dropped + worstCell.Guest.Dropped} and its deaths take {worstCell.DeathSteps.Sum()} steps against the clean cell's {clean.Host.Dropped + clean.Guest.Dropped} and {clean.DeathSteps.Sum()}");
    }

    // The inferred drop count against the carrier's own. A clean flush first, so every stream's
    // last loss sits behind a payload that arrived and a gap can be read off it.
    private static void Truth(TestContext ctx, Pair pair)
    {
        pair.Mesh[0].SetConditions(1, LoopbackConditions.Perfect);
        pair.Mesh[1].SetConditions(0, LoopbackConditions.Perfect);
        pair.Host.SeatRigs[0].Controller!.AutoFire = true;
        pair.Guest.SeatRigs[1].Controller!.AutoFire = true;
        pair.Step(FlushFireSteps);
        pair.Host.SeatRigs[0].Controller!.AutoFire = false;
        pair.Guest.SeatRigs[1].Controller!.AutoFire = false;
        pair.Step(SettleSteps);

        // The events channel carries no sequence stream. Its unreliable losses, the clock's
        // questions and answers, leave no gap to infer and are not part of the truth.
        int toGuest = pair.Mesh[0].Lost - pair.Mesh[0].LostOn(NetChannels.Events);
        int toHost = pair.Mesh[1].Lost - pair.Mesh[1].LostOn(NetChannels.Events);
        int guestInferred = pair.Guest.NetLink!.Instruments.Dropped;
        int guestTruth = toGuest + pair.Mesh[1].DiscardedStale;
        int hostInferred = pair.Host.NetLink!.Instruments.Dropped;
        int hostTruth = toHost + pair.Mesh[0].DiscardedStale;
        ctx.Check(guestTruth > 0 && guestInferred == guestTruth && hostInferred == hostTruth,
            $"each machine's drop count read off its sequence gaps is what the carrier really lost or discarded (guest {guestInferred} of {toGuest} lost + {pair.Mesh[1].DiscardedStale} discarded, host {hostInferred} of {toHost} lost + {pair.Mesh[0].DiscardedStale} discarded, events channel losses {pair.Mesh[0].LostOn(NetChannels.Events)}/{pair.Mesh[1].LostOn(NetChannels.Events)} left out)");
    }

    // ABLE-TO-FAIL CONTROL. The host grants a respawn to a seat that is flying. Every cell above
    // read zero order violations, and this is the same counter moving on a real breach.
    private static void InjectedViolation(TestContext ctx, Pair pair)
    {
        int before = pair.Guest.NetLink!.Instruments.OrderViolations;
        pair.Host.NetLink!.Broadcast(
            new SpawnMessage(1, NetSpawnKind.Respawn, NetMessage.NoSpawnEntry), NetChannels.Events);
        pair.Step(SettleSteps);
        int after = pair.Guest.NetLink!.Instruments.OrderViolations;
        ctx.Check(after == before + 1,
            $"ABLE-TO-FAIL CONTROL: a respawn granted to a seat nobody reported dead is counted as out of order ({before} to {after})");
    }

    private static SessionSpec SoakSpec(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, "MP1");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/MP1 zrdr");
        // Both limits off, so no number of kills or minutes ends the match mid-matrix and holds
        // every world at the wrap-up board.
        return SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", "--mission=MP1", "--players=1", "--mute",
            "--no-pads", TrackedFlight, "--vs-kills=0", "--vs-time=0",
        });
    }

    private static int Rounds(FlightController gun) =>
        gun.Projectiles is { } pool ? pool.CannonRoundsFired : 0;

    private static Vector3 Pose(GameSession session, int seat) =>
        session.SeatRigs[seat].Controller?.WorldPosition ?? Vector3.Zero;

    // One matrix cell: its link both ways, and the tracking bars read off this suite's own runs
    // with headroom. What a player accepts is unmeasured, so a bar is a regression tripwire.
    private sealed record Cell(string Name, LoopbackConditions Conditions, float MeanBar, float WorstBar);

    // The two sessions and their carriers, stepped together, host first, as a listen server runs.
    private sealed record Pair(GameSession Host, GameSession Guest, IReadOnlyList<LoopbackTransport> Mesh, WeaponDef Gun)
    {
        public GameSession[] Both => new[] { Host, Guest };

        public void Step(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Host._PhysicsProcess(GameClock.FixedDt);
                Guest._PhysicsProcess(GameClock.FixedDt);
            }
        }

        // Every remote aeroplane's buffer on both machines: the guest's copy of the host's seat and
        // the host's copy of the guest's.
        public IEnumerable<RemotePoseBuffer> Buffers()
        {
            foreach (var session in Both)
            {
                foreach (var rig in session.SeatRigs)
                {
                    if (rig.Controller?.RemotePoses is { } buffer)
                    {
                        yield return buffer;
                    }
                }
            }
        }
    }

    private sealed class Flight
    {
        public List<Vector3> HostOwn { get; } = new();

        public List<Vector3> HostShown { get; } = new();

        public List<Vector3> GuestOwn { get; } = new();

        public List<Vector3> GuestShown { get; } = new();

        public int RoundsFired { get; set; }

        public bool Flying { get; set; }
    }

    // What one cell measured. "There" is the host's aeroplane as the guest showed it, "back" the
    // guest's as the host showed it; the readings are each machine's own instruments.
    private sealed record CellResult(
        Cell Cell,
        (float Lag, float Mean, float Max) There,
        (float Lag, float Mean, float Max) Back,
        RemotePoseTally Flown,
        RemotePoseTally Poses,
        NetInstrumentReading Host,
        NetInstrumentReading Guest,
        int RoundsFired,
        int[] DeathSteps,
        int Returned,
        bool Flying,
        int Lost,
        int Discarded)
    {
        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"{Cell.Name}: position error guest-shown {There.Mean:0.00}/{There.Max:0.00} m at {There.Lag * GameClock.FixedDt * 1000f:0} ms, host-shown {Back.Mean:0.00}/{Back.Max:0.00} m at {Back.Lag * GameClock.FixedDt * 1000f:0} ms; extrapolation error {Poses.MeanExtrapolationError:0.00}/{Poses.WorstExtrapolationError:0.00} m, {Poses.Jumps} jumps; flight reads {Flown.Interpolating} interp {Flown.Extrapolating} extrap {Flown.Starved} starved, whole cell {Poses.Starved} starved; dropped state {Host.StateGaps}/{Guest.StateGaps} fire {Host.FireGaps}/{Guest.FireGaps} of {RoundsFired} rounds ({Host.ReorderedFire}/{Guest.ReorderedFire} drawn out of order), the carriers losing {Lost} and discarding {Discarded} as overtaken; late hits {Host.LateHits}/{Guest.LateHits} fire {Host.LateFire}/{Guest.LateFire} stale {Host.StaleArrivals}/{Guest.StaleArrivals}; order {Host.OrderViolations}/{Guest.OrderViolations}; deaths reached the shooter in {DeathSteps[0]} and {DeathSteps[1]} steps, both back after {Returned} (host/guest per pair)");
    }
}
