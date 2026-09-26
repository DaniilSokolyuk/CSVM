using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Combat between whole sessions in one process: what one machine fires, what it
/// claims, what it dies of, where it comes back. The rig is
/// <see cref="NetSessionSuites"/>'s, one session per peer under its own <see cref="SubViewport"/>
/// and <see cref="World3D"/>. A rule asserted here is therefore a rule between machines. Two
/// suites add a third session with the guest-to-guest link cut. That cut is the only way to tell
/// a relayed or granted message from a direct one.</summary>
internal static class NetCombatSuites
{
    private const string MpMission = "MP1";

    private const ulong HostSeed = 0xC0FFEE01UL;

    // Steps both ends are driven through between one event and the assertion on it. A reliable
    // payload crosses this link inside a handful; the rest is the damage and death path settling.
    private const int SettleSteps = 20;

    // How long the gun is held for the fire-event reading, in sim steps. It is also how long the
    // last events are given to land after the trigger is released.
    private const int BurstSteps = 60;

    // The scripted flight the star's three owners fly. Each aeroplane is then somewhere its own,
    // so a reconstruction can be told from the aeroplane beside it.
    private const string TrackedFlight = "--hold=0.6,0.9,0,1";

    private const int StarFlightSteps = 180;

    // The crash-camera wait every seat is cut to for the spawn suite, in seconds. The match's own
    // three seconds are 180 sim steps per death, and nothing here reads the wait itself.
    private const float QuickRespawn = 0.5f;

    // How long a death is given to come back, in sim steps: the wait above, the ask to the host
    // and the grant back. Room to spare, on a link that carries each leg in one step.
    private const int GrantSteps = 120;

    // How close to a table entry a placed aeroplane counts as standing on it, in metres,
    // horizontally. Wider than the opening reading's metre: a granted aeroplane already flies at
    // the multiplayer opening speed by the step its grant is seen to land.
    private const float EntryTolerance = 5f;

    // The host's two lobby rows for the match-state suite. The kill target is two so one seat
    // reaches it off two real deaths, and the clock is the shortest whole minute --vs-time= takes.
    private const int HostKillTarget = 2;
    private const int HostTimeMinutes = 1;

    // What the two guests are launched on instead. Neither row is the host's, so a guest showing
    // the host's row is showing something that crossed the wire.
    private const int GuestKillTarget = 9;
    private const int GuestTimeMinutes = 9;

    // Steps that span a whole match-state tick whatever step the window opens on.
    private const int TickSteps = MatchStateCadence.TickStepInterval + 1;

    // How far one host frame is wound on for the slew reading, in seconds. Past
    // NetClockSlew.SnapSeconds on purpose: a snap is countable and a walk is not.
    private const double SlewLeadSeconds = 6.0;

    // The airframe order every peer reads a roster's airframe index against.
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-combat-events",
        "a host session and a guest session in one process: the rounds one owner fires are spawned "
        + "on the other from its fire events and nowhere else, a hit on an aeroplane flown "
        + "elsewhere spends nothing locally and lands as damage on the machine that owns it, a "
        + "guest kills the host and the host kills the guest with the score agreeing on both "
        + "peers, and the suicide and turret-kill causes score as the decode says")]
    internal static void CombatEventsCrossTheWire(TestContext ctx)
    {
        var spec = MatchSpec(ctx, out _);
        // A clean link: every assertion below is about a rule, and a dropped round would read as
        // a broken rule. The lossy link is asserted on in net-aircraft-replication.
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(4211));
        var roster = Roster(2);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath);
        var gun = weapons.All.FirstOrDefault(w => w.IsCannon && w.HealthDamage is > 0f);
        if (gun == null)
        {
            throw new SuiteSkippedException($"the weapon catalogue holds no cannon with health damage");
        }

        var ambient = Ambient.Save();
        Ends? host = null;
        Ends? guest = null;
        try
        {
            host = Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            Lockstep(SettleSteps, host.Session, guest.Session);
            FireEvents(ctx, host.Session, guest.Session);
            HitRouting(ctx, host.Session, guest.Session, gun);
            Kills(ctx, host.Session, guest.Session);
            Causes(ctx, host.Session, guest.Session);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    [Suite("net-relay-star",
        "three sessions in one process with the two guests unlinked from each other, so the host "
        + "is their only path: each guest's aeroplane moves on the other guest's world and stays "
        + "on its owner's own track rather than the third aeroplane's, a guest's gunfire spawns "
        + "rounds under its own seat on the other guest, and the host forwards every arrival "
        + "exactly once, never back to the peer it came from")]
    internal static void TheHostRelaysBetweenGuests(TestContext ctx)
    {
        var spec = MatchSpec(ctx, out var table, TrackedFlight);
        if (table.Count < 3)
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors fewer than three spawns");
        }

        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(8123));
        // The star itself, cut before any session binds. Neither guest ever sees the other as a
        // peer, so anything that reaches it came through the host.
        mesh[1].Disconnect(2);
        var roster = Roster(3);

        var ambient = Ambient.Save();
        Ends? host = null;
        Ends? first = null;
        Ends? second = null;
        try
        {
            host = Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            first = Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            second = Ends.Open(ctx, spec, mesh[2], isHost: false, HostSeed + 2, null);
            ctx.Check(host.Built && first.Built && second.Built,
                $"three sessions build in one process (host {host.Built}, first {first.Built}, second {second.Built})");
            if (!host.Built || !first.Built || !second.Built)
            {
                return;
            }

            ctx.Check(first.Session.NetLink!.Peers.Count == 1 && second.Session.NetLink!.Peers.Count == 1
                      && host.Session.NetLink!.Peers.Count == 2,
                $"the two guests hold one peer each and the host holds both ({first.Session.NetLink!.Peers.Count}, {second.Session.NetLink!.Peers.Count}, {host.Session.NetLink!.Peers.Count})");

            Relay(ctx, host.Session, first.Session, second.Session);
        }
        finally
        {
            second?.Close();
            first?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    [Suite("net-spawn-rotation",
        "three sessions in one process with the two guests unlinked from each other: every seat "
        + "opens on the same spawn table entry with no spawn event applied anywhere, so the "
        + "opening placement is the shared seed's walk alone; over a sequence of deaths each "
        + "returning seat is put back by exactly one host grant, on one real table entry, the same "
        + "entry on all three peers and never the one it was downed at; and a grant naming an "
        + "entry the host's own rotation refuses is still obeyed to the letter by both guests")]
    internal static void TheHostGrantsEverySpawn(TestContext ctx)
    {
        var spec = MatchSpec(ctx, out var table);
        if (table.Count < 4)
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors fewer than four spawns");
        }

        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(3307));
        // The star, cut before any session binds. An ask reaches the host alone and a grant comes
        // back from it alone, so nothing here is two guests agreeing between themselves.
        mesh[1].Disconnect(2);
        var roster = Roster(3);

        var ambient = Ambient.Save();
        Ends? host = null;
        Ends? first = null;
        Ends? second = null;
        try
        {
            host = Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            first = Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            second = Ends.Open(ctx, spec, mesh[2], isHost: false, HostSeed + 2, null);
            ctx.Check(host.Built && first.Built && second.Built,
                $"three sessions build in one process (host {host.Built}, first {first.Built}, second {second.Built})");
            if (!host.Built || !first.Built || !second.Built)
            {
                return;
            }

            var peers = new[] { host.Session, first.Session, second.Session };
            var placed = Opening(ctx, table, peers);
            Returns(ctx, table, peers, placed);
            ObeyedVerbatim(ctx, table, peers, placed);
        }
        finally
        {
            second?.Close();
            first?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    [Suite("net-match-state",
        "three sessions in one process with the two guests unlinked from each other and launched "
        + "on limits of their own: both guests take the host's kill target and time limit off the "
        + "wire, neither moves a match clock when stepped without the host, the host's session "
        + "clock reaches both slews through the same tick, a guest that counts the target locally "
        + "neither ends its match nor raises its board, its own rematch key does nothing, and a "
        + "match ended on the kill limit and then on the time limit holds all three machines with "
        + "the same reason and the same scoreboard")]
    internal static void MatchStateIsTheHostsAlone(TestContext ctx)
    {
        // The scripted climb the star suites fly. This suite is the longest of the three, and a
        // seat that flies itself into the ground scores a suicide against the kill limit below.
        var spec = MatchSpec(ctx, out var table, TrackedFlight,
            $"--vs-kills={HostKillTarget}", $"--vs-time={HostTimeMinutes}");
        // The guests are launched on limits that are not the host's. A limit one of them shows
        // therefore crossed the wire, rather than being one its own command line held.
        var guestSpec = MatchSpec(ctx, out _, TrackedFlight,
            $"--vs-kills={GuestKillTarget}", $"--vs-time={GuestTimeMinutes}");
        if (table.Count < 3)
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors fewer than three spawns");
        }

        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(5507));
        // The star, cut before any session binds. A guest's state came from the host or from
        // nowhere, since the two guests never see each other at all.
        mesh[1].Disconnect(2);
        var roster = Roster(3);

        var ambient = Ambient.Save();
        Ends? host = null;
        Ends? first = null;
        Ends? second = null;
        try
        {
            host = Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            first = Ends.Open(ctx, guestSpec, mesh[1], isHost: false, HostSeed + 1, null);
            second = Ends.Open(ctx, guestSpec, mesh[2], isHost: false, HostSeed + 2, null);
            ctx.Check(host.Built && first.Built && second.Built,
                $"three sessions build in one process (host {host.Built}, first {first.Built}, second {second.Built})");
            if (!host.Built || !first.Built || !second.Built)
            {
                return;
            }

            var peers = new[] { host.Session, first.Session, second.Session };
            Lockstep(SettleSteps, peers);
            Limits(ctx, peers);
            ClockOwnership(ctx, peers);
            Slew(ctx, peers);
            NoEarlyHold(ctx, peers);
            EndsOnTheKillLimit(ctx, peers);
            Rematch(ctx, peers);
            EndsOnTheTimeLimit(ctx, peers);
        }
        finally
        {
            second?.Close();
            first?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    // Both limits are the host's lobby rows on every machine, and only the host holds a match it
    // may write. A guest was launched on other rows, so what it shows came off the wire.
    private static void Limits(TestContext ctx, GameSession[] peers)
    {
        string reading = string.Join(" | ",
            peers.Select(p => $"{p.Versus!.KillTarget} kills / {p.Versus!.TimeLimit:0} s"));
        ctx.Check(peers.All(p => p.Versus!.KillTarget == HostKillTarget
                                 && Mathf.IsEqualApprox(p.Versus!.TimeLimit, HostTimeMinutes * 60f)),
            $"every machine runs the host's two limits, not the {GuestKillTarget} kills / {GuestTimeMinutes * 60} s the guests were launched on ({reading})");
        ctx.Check(!peers[0].Versus!.Replicated && peers[1].Versus!.Replicated && peers[2].Versus!.Replicated,
            $"and the match is the host's to write on the host alone (replicated: {string.Join(", ", peers.Select(p => p.Versus!.Replicated))})");
    }

    // ⚠ A guest may never advance the match clock itself. Stepped without the host it therefore
    // stands still, and the host's clock is what moves it when the two run together again.
    private static void ClockOwnership(TestContext ctx, GameSession[] peers)
    {
        var guests = new[] { peers[1], peers[2] };
        float[] before = guests.Select(g => g.Versus!.Elapsed).ToArray();
        Lockstep(TickSteps, guests);
        float[] alone = guests.Select(g => g.Versus!.Elapsed).ToArray();
        ctx.Check(alone.SequenceEqual(before),
            $"a guest stepped without the host moves no match clock at all ({string.Join(", ", alone.Select(e => $"{e:0.000} s"))})");

        // ABLE-TO-FAIL CONTROL. The same steps through the same phase on the host, whose clock
        // does move. The stillness above is therefore the replication rule and not a dead path.
        float hostWas = peers[0].Versus!.Elapsed;
        Lockstep(TickSteps, peers[0]);
        float hostMoved = peers[0].Versus!.Elapsed - hostWas;
        ctx.Check(hostMoved > 0.5f,
            $"ABLE-TO-FAIL CONTROL: the host's own clock moves {hostMoved:0.000} s over those same {TickSteps} steps");

        Lockstep(TickSteps, peers);
        float host = peers[0].Versus!.Elapsed;
        float[] now = guests.Select(g => g.Versus!.Elapsed).ToArray();
        string reading = $"host {host:0.00} s, guests {string.Join(", ", now.Select(e => $"{e:0.00} s"))}";
        // Behind by at most one tick, which is the whole of the clock's replication error.
        ctx.Check(now.All(e => e > alone[0] && host - e >= 0f
                               && host - e <= MatchStateCadence.TickStepInterval * GameClock.FixedDt + 0.02f),
            $"and the host's clock reaches both guests, no more than one tick behind it ({reading})");
    }

    // Whether the clock the tick carries is read. ⚠ Under this rig only the host's session clock
    // can move. GameClock.Time advances in the frame callback and a suite drives the fixed step
    // alone (docs/verification.md INSTR-92). One host frame is the whole of the difference.
    private static void Slew(TestContext ctx, GameSession[] peers)
    {
        var guests = new[] { peers[1], peers[2] };
        // ABLE-TO-FAIL CONTROL. Every tick so far carried a host clock of zero against a guest
        // clock of zero, and left the offset alone. The reading below is the clock, not the
        // arrival of a message.
        ctx.Check(guests.All(g => g.NetClock!.Snaps == 0 && Math.Abs(g.NetClock!.Target) < 1e-6),
            $"ABLE-TO-FAIL CONTROL: ticks with both clocks at zero leave the offset at zero ({string.Join(", ", guests.Select(g => $"{g.NetClock!.Target:0.000} s over {g.NetClock!.Snaps} snap(s)"))})");

        // One frame of the host alone, which is the only thing in this rig that moves a session
        // clock. Long enough that the reading is past the slew's snap threshold.
        peers[0]._Process(SlewLeadSeconds);
        Lockstep(TickSteps, peers);
        string reading = string.Join(", ",
            guests.Select(g => $"offset {g.NetClock!.Offset:0.000} s, target {g.NetClock!.Target:0.000} s over {g.NetClock!.Snaps} snap(s)"));
        ctx.Check(guests.All(g => g.NetClock!.Snaps == 1
                                  && g.NetClock!.Target > NetClockSlew.SnapSeconds
                                  && Math.Abs(g.NetClock!.Target - SlewLeadSeconds) < 0.05),
            $"the host's session clock reaches both guests' slew through the same tick ({reading})");
        ctx.Note($"slew over a harness match: {reading}");
    }

    // ⚠ A guest that reaches the end locally must not hold early. Counting the target into its
    // own match is the way it would, so the match refuses to complete on anything but the host.
    private static void NoEarlyHold(TestContext ctx, GameSession[] peers)
    {
        var guest = peers[1];
        for (int i = 0; i < HostKillTarget + 3; i++)
        {
            guest.Versus!.RegisterKill(shooter: 1, victim: 0);
        }

        Lockstep(SettleSteps, peers);
        ctx.Check(!guest.Versus!.Completed && guest.Pause is { Ended: false }
                  && !peers[0].Versus!.Completed,
            $"a guest that counts {HostKillTarget + 3} kills of its own ends nothing and raises no board (completed {guest.Versus!.Completed}, held {guest.Pause!.Ended}, host completed {peers[0].Versus!.Completed})");

        // ABLE-TO-FAIL CONTROL. The same calls into a match nobody replicates do end it. The
        // refusal above is therefore the rule, not a scorekeeping that counts nothing.
        var alone = new VersusMatch(3, HostKillTarget, HostTimeMinutes * 60f);
        for (int i = 0; i < HostKillTarget + 3; i++)
        {
            alone.RegisterKill(shooter: 1, victim: 0);
        }

        ctx.Check(alone.Completed,
            $"ABLE-TO-FAIL CONTROL: the same kills into a match of its own do end it ({alone.ScoreOf(1)} points against a target of {HostKillTarget})");
    }

    // The kill limit, reached by two real deaths reported from two different machines. The host
    // is the only scorer, so the end it broadcasts is the only one any machine acts on.
    private static void EndsOnTheKillLimit(TestContext ctx, GameSession[] peers)
    {
        // The crash camera cut short. The seat that is downed twice is then back in the fight in
        // half a second rather than the match's own three.
        foreach (var rig in peers.SelectMany(p => p.SeatRigs))
        {
            if (rig.Controller is { } pilot)
            {
                pilot.AutoRespawnAfter = QuickRespawn;
            }
        }

        // Seat 1 downs the host's seat twice, a point each time. The victim is reported by the
        // machine that flies it, which is the only one that may. The host alone turns those
        // reports into a score.
        peers[0].SeatRigs[0].Controller!.DebugForceCrash(peers[0].SeatRigs[1].Controller!.PlayerIndex);
        Lockstep(GrantSteps, peers);
        peers[0].SeatRigs[0].Controller!.DebugForceCrash(peers[0].SeatRigs[1].Controller!.PlayerIndex);
        Lockstep(SettleSteps, peers);

        ctx.Check(peers[0].Versus!.ScoreOf(1) >= HostKillTarget,
            $"seat 1 reaches the host's kill target of {HostKillTarget} ({Scoreboard(peers[0])})");
        Held(ctx, peers, "the kill limit", NetMatchEnd.ScoreTarget);
    }

    // The host's rematch, and the guest's own key before it. Only the host may call one: a guest
    // that restarted here would fly a round nobody else is in (BL-1026).
    private static void Rematch(TestContext ctx, GameSession[] peers)
    {
        peers[1].SeatRigs[1].Controller!.RestartMatch!();
        Lockstep(SettleSteps, peers);
        ctx.Check(peers.All(p => p.Versus!.Completed),
            $"a guest's own rematch key restarts nothing, on its own machine or any other ({string.Join(", ", peers.Select(p => p.Versus!.Completed))})");

        peers[0].SeatRigs[0].Controller!.RestartMatch!();
        Lockstep(SettleSteps, peers);
        string scores = string.Join(" | ",
            peers.Select(p => string.Join(",", Enumerable.Range(0, 3).Select(s => p.Versus!.ScoreOf(s)))));
        ctx.Check(peers.All(p => !p.Versus!.Completed && p.MatchEnd == NetMatchEnd.Running),
            $"the host's rematch runs the round again on every machine ({string.Join(", ", peers.Select(p => p.MatchEnd))})");
        ctx.Check(peers.All(p => Enumerable.Range(0, 3).All(s => p.Versus!.ScoreOf(s) == 0)),
            $"and every score is zero on every machine, the host's rewrite rather than each machine's own ({scores})");
    }

    // The other way a match stops. The host's clock is wound to just short of the limit, then
    // stepped over it through the phase a flown match runs. The end is therefore the real one.
    private static void EndsOnTheTimeLimit(TestContext ctx, GameSession[] peers)
    {
        float limit = HostTimeMinutes * 60f;
        peers[0].Versus!.Advance(limit - peers[0].Versus!.Elapsed - 0.5f);
        ctx.Check(!peers[0].Versus!.Completed,
            $"the host's clock stands half a second short of its limit ({peers[0].Versus!.TimeRemaining:0.00} s left)");

        Lockstep(TickSteps, peers);
        Held(ctx, peers, "the time limit", NetMatchEnd.TimeLimit);
        ctx.Check(peers.All(p => p.Versus!.TimeRemaining <= 0f),
            $"and no machine has time left on it ({string.Join(", ", peers.Select(p => $"{p.Versus!.TimeRemaining:0.00} s"))})");
    }

    // One ending, read on all three machines. The match over, the board holding the world, the
    // host's own reason, and one ranked scoreboard derived from the scores each was sent.
    private static void Held(TestContext ctx, GameSession[] peers, string what, NetMatchEnd reason)
    {
        ctx.Check(peers.All(p => p.Versus!.Completed),
            $"{what}: the match is over on all three machines ({string.Join(", ", peers.Select(p => p.Versus!.Completed))})");
        ctx.Check(peers.All(p => p.Pause is { Ended: true }),
            $"and the wrap-up board holds every one of them ({string.Join(", ", peers.Select(p => p.Pause!.Ended))})");
        ctx.Check(peers.All(p => p.MatchEnd == reason),
            $"and each names the host's own reason, {reason} ({string.Join(", ", peers.Select(p => p.MatchEnd))})");

        var boards = peers.Select(Scoreboard).ToArray();
        ctx.Check(boards.All(b => b == boards[0]),
            $"and the scoreboard is identical on all three, derived from the scores and never sent ({string.Join(" | ", boards)})");
    }

    // The ranked board as one line, which is what a results screen draws from.
    private static string Scoreboard(GameSession session) =>
        string.Join(" ", session.Versus!.Standings()
            .Select(s => $"#{s.Rank}P{s.PlayerIndex + 1}:{s.Score}/{s.Kills}K/{s.Deaths}D"));

    // The opening placement, read before any session has stepped. Nothing has crossed the wire
    // but the join, so an agreement here is the shared seed's walk and can be nothing else.
    private static int[] Opening(TestContext ctx, IReadOnlyList<SpawnPoint> table, GameSession[] peers)
    {
        var entries = peers
            .Select(p => p.SeatRigs.Select(r => EntryAt(table, r.Controller)).ToArray()).ToArray();
        string reading = string.Join(" | ", entries.Select(e => string.Join(",", e)));
        ctx.Check(entries[0].All(i => i >= 0) && entries.All(e => e.SequenceEqual(entries[0])),
            $"every seat opens on the same table entry on all three peers ({reading})");
        ctx.Check(new HashSet<int>(entries[0]).Count == entries[0].Length,
            $"and no two seats share one ({string.Join(",", entries[0])})");
        string grants = string.Join(",", peers.Select(p => p.SpawnsTaken));
        ctx.Check(peers.All(p => p.SpawnsTaken == 0),
            $"with no spawn grant applied anywhere, so the opening is the seed's walk and not an event ({grants} grant(s))");
        ctx.Check(table.Count >= NetSeats.MaxPlayers,
            $"and the block holds a point for every seat the match admits, so no two of a full field open together ({table.Count} entries against {NetSeats.MaxPlayers} seats)");
        return entries[0];
    }

    // A sequence of deaths, each on a different machine. Every one of them must come back through
    // the host, because a guest that rotated for itself would diverge on the first of them.
    private static void Returns(TestContext ctx, IReadOnlyList<SpawnPoint> table, GameSession[] peers,
        int[] placed)
    {
        foreach (var session in peers)
        {
            foreach (var rig in session.SeatRigs)
            {
                if (rig.Controller is { } pilot)
                {
                    pilot.AutoRespawnAfter = QuickRespawn;
                }
            }
        }

        Downed(ctx, table, peers, placed, owner: 1, seat: 1, killer: 0);
        Downed(ctx, table, peers, placed, owner: 0, seat: 0, killer: 1);
        Downed(ctx, table, peers, placed, owner: 1, seat: 1, killer: 2);
    }

    // One death and the return that answers it. The owner's machine is the only one told to
    // crash: everything after that is the death report, the ask and the grant doing their work.
    // ⚠ The agreement is read off the granted entry, not off three positions. A seat flown
    // elsewhere stands where its owner's latest pose puts it. A pose sent before the death and
    // delivered after the grant is a replication lag, not a second placement rule.
    private static void Downed(TestContext ctx, IReadOnlyList<SpawnPoint> table, GameSession[] peers,
        int[] placed, int owner, int seat, int killer)
    {
        int was = placed[seat];
        var before = peers.Select(p => (int?)p.SpawnsTaken).ToArray();
        peers[owner].SeatRigs[seat].Controller!
            .DebugForceCrash(peers[owner].SeatRigs[killer].Controller!.PlayerIndex);

        int steps = StepUntilGranted(peers, before);
        var now = peers.Select(p => p.SpawnEntries[seat]).ToArray();
        var grants = peers.Select((p, i) => p.SpawnsTaken - before[i]!.Value).ToArray();
        int flown = EntryAt(table, peers[owner].SeatRigs[seat].Controller);
        string reading = $"seat {seat} downed by seat {killer} at entry {was}, back on {string.Join("/", now)} after {steps} step(s)";
        ctx.Check(now[0] >= 0 && now[0] < table.Count && now.All(e => e == now[0]),
            $"a downed seat is placed on one real table entry, the same one on all three peers ({reading})");
        ctx.Check(now[0] != was, $"and never the entry it was downed at ({reading})");
        ctx.Check(flown == now[0],
            $"and the aeroplane itself stands on that entry on the machine that flies it (entry {flown} against {now[0]})");
        ctx.Check(grants.All(g => g == 1),
            $"placed by exactly one host grant on every peer, the owner's own machine included ({string.Join(",", grants)} grant(s))");
        placed[seat] = now[0];
    }

    // ABLE-TO-FAIL CONTROL. The host hands out an entry its own rotation would refuse, the one a
    // living seat is standing on. Both guests place the aeroplane there anyway, which a guest
    // running a rotation of its own could not do. The agreements above are therefore the host's
    // word being followed, not three machines picking alike.
    private static void ObeyedVerbatim(TestContext ctx, IReadOnlyList<SpawnPoint> table,
        GameSession[] peers, int[] placed)
    {
        int seat = 2;
        int odd = placed[0];
        var before = new int?[] { null, peers[1].SpawnsTaken, peers[2].SpawnsTaken };
        peers[0].NetLink!.Broadcast(
            new SpawnMessage((byte)seat, NetSpawnKind.Respawn, (ushort)odd), NetChannels.Events);
        StepUntilGranted(peers, before);

        int owner = peers[2].SpawnEntries[seat];
        int watcher = peers[1].SpawnEntries[seat];
        int flown = EntryAt(table, peers[2].SeatRigs[seat].Controller);
        ctx.Check(owner == odd && watcher == odd && flown == odd,
            $"ABLE-TO-FAIL CONTROL: a grant naming entry {odd}, which the rotation refuses, is obeyed by the owner and by the other guest ({owner} and {watcher}, aeroplane on {flown})");
        placed[seat] = odd;
    }

    // Steps every peer together until each one being watched has applied one more grant, and
    // answers with how many steps that took. A null is a peer the grant never reaches, which is
    // the host's own broadcast coming back to nobody.
    private static int StepUntilGranted(GameSession[] peers, int?[] before)
    {
        for (int step = 1; step <= GrantSteps; step++)
        {
            Lockstep(1, peers);
            bool all = true;
            for (int i = 0; i < peers.Length; i++)
            {
                all &= before[i] is not { } was || peers[i].SpawnsTaken > was;
            }

            if (all)
            {
                return step;
            }
        }

        return GrantSteps;
    }

    // Which table entry a placed aircraft is standing on, or -1. A start is raised off the ground
    // under it, so the match is on the horizontal position alone.
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
            if (Mathf.Abs(d.X) < EntryTolerance && Mathf.Abs(d.Z) < EntryTolerance)
            {
                return i;
            }
        }

        return -1;
    }

    // What one owner's guns put in the world, counted on the far peer. The pool's own scored-shooter
    // filter is the census, told on both ends to count seat 0 alone. The host's number is the
    // rounds it fired and the guest's is the rounds its events built.
    private static void FireEvents(TestContext ctx, GameSession host, GameSession guest)
    {
        var shooter = host.SeatRigs[0].Controller!;
        var here = shooter.Projectiles!;
        var there = guest.SeatRigs[0].Controller!.Projectiles!;
        here.ScoredShooters.Add(shooter.PlayerIndex);
        there.ScoredShooters.Add(guest.SeatRigs[0].Controller!.PlayerIndex);
        int mineBefore = here.CannonRoundsFired;
        int theirsBefore = there.CannonRoundsFired;

        shooter.AutoFire = true;
        Lockstep(BurstSteps, host, guest);
        shooter.AutoFire = false;
        Lockstep(SettleSteps, host, guest);

        int mine = here.CannonRoundsFired - mineBefore;
        int theirs = there.CannonRoundsFired - theirsBefore;
        ctx.Check(mine > 0 && theirs == mine,
            $"every round the host's guns fired was built again on the guest from its fire events ({mine} fired, {theirs} rebuilt)");

        // ABLE-TO-FAIL CONTROL. The guest's own aeroplane held no trigger, and its copy on the
        // host runs no fire control at all. The same counter for the other seat stays at zero.
        var idle = host.SeatRigs[1].Controller!;
        here.ScoredShooters.Add(idle.PlayerIndex);
        int quiet = here.CannonRoundsFired;
        Lockstep(SettleSteps, host, guest);
        ctx.Check(here.CannonRoundsFired == quiet,
            $"ABLE-TO-FAIL CONTROL: the seat flown elsewhere fires nothing of its own on this machine ({here.CannonRoundsFired - quiet} round(s))");
    }

    // Decision 8's fork. The shooter's machine decides the hit and spends nothing on its own copy
    // of the victim; the victim's machine is where the ledger moves.
    private static void HitRouting(TestContext ctx, GameSession host, GameSession guest, WeaponDef gun)
    {
        var shown = host.SeatRigs[1].Controller!;
        var owned = guest.SeatRigs[1].Controller!;
        float shownBefore = Ledger(shown);
        float ownedBefore = Ledger(owned);

        shown.Body!.TakeProjectileHit(gun, shown.WorldPosition, 0, host.SeatRigs[0].Controller!.PlayerIndex);
        ctx.Check(Mathf.IsEqualApprox(Ledger(shown), shownBefore),
            $"the shooter spends nothing on its own copy of the aeroplane it hit (pools {Ledger(shown):0.0} of {shownBefore:0.0})");

        Lockstep(SettleSteps, host, guest);
        ctx.Check(Ledger(owned) < ownedBefore,
            $"and the machine that owns that aeroplane applied the claim ({ownedBefore:0.0} to {Ledger(owned):0.0} armour plus health)");

        // The other side of the same fork. A round fired by a seat flown elsewhere is that
        // machine's to decide. This machine's copy of it spends nothing on its own aeroplane
        // either, and waits for the claim.
        var mine = host.SeatRigs[0].Controller!;
        float waiting = Ledger(mine);
        mine.Body!.TakeProjectileHit(gun, mine.WorldPosition, 0, shown.PlayerIndex);
        ctx.Check(Mathf.IsEqualApprox(Ledger(mine), waiting),
            $"a round fired by a seat flown elsewhere spends nothing here, its owner decides it (pools {Ledger(mine):0.0} of {waiting:0.0})");

        // ABLE-TO-FAIL CONTROL. The same call with a round this machine does own spends at once.
        // The two silences above are the routing and not a call that does nothing.
        mine.Body.TakeProjectileHit(gun, mine.WorldPosition, 0, mine.PlayerIndex);
        ctx.Check(Ledger(mine) < waiting,
            $"ABLE-TO-FAIL CONTROL: a round this machine owns spends at once on the same aeroplane ({waiting:0.0} to {Ledger(mine):0.0})");
    }

    // Both directions of a kill, each reported by the machine that owns the dying pilot and scored
    // by the host alone. The board is read on both peers after each one.
    private static void Kills(TestContext ctx, GameSession host, GameSession guest)
    {
        // The guest kills the host: the victim is flown here, so the host reports its own death.
        host.SeatRigs[0].Controller!.DebugForceCrash(host.SeatRigs[1].Controller!.PlayerIndex);
        Lockstep(SettleSteps, host, guest);
        Board(ctx, host, guest, "the guest kills the host", (0, 1, 0), (1, 0, 1));
        ctx.Check(guest.SeatRigs[0].Controller is { InPlay: false },
            $"and the guest's copy of that aeroplane is out of the fight with it");

        // The host kills the guest: the victim is flown on the guest, whose report crosses the
        // wire to the scorer.
        guest.SeatRigs[1].Controller!.DebugForceCrash(guest.SeatRigs[0].Controller!.PlayerIndex);
        Lockstep(SettleSteps, host, guest);
        Board(ctx, host, guest, "the host kills the guest", (1, 1, 1), (1, 1, 1));
        ctx.Check(host.SeatRigs[1].Controller is { InPlay: false },
            $"and the host's copy of that aeroplane is out of the fight with it");
    }

    // The two causes gameplay does not raise here: a death with nobody to charge, and a death
    // charged to a turret's owner. Both are put on the wire as the dying client's own report.
    private static void Causes(TestContext ctx, GameSession host, GameSession guest)
    {
        var link = guest.NetLink!;
        link.Send(link.HostPeer, new DeathMessage(1, NetMessage.NoSeat, NetDeathCause.Suicide, 0u),
            NetChannels.Events);
        Lockstep(SettleSteps, host, guest);
        Board(ctx, host, guest, "a death with nobody to charge", (1, 1, 1), (0, 2, 1));

        link.Send(link.HostPeer, new DeathMessage(1, 0, NetDeathCause.TurretOwner, 0u),
            NetChannels.Events);
        Lockstep(SettleSteps, host, guest);
        Board(ctx, host, guest, "a death charged to a turret's owner", (2, 1, 2), (0, 3, 1));
    }

    // One reading of the board on both peers: the expected (score, deaths, kills) per seat. The
    // guest's copy is written from the host's score messages. An agreement here is the host's
    // count reaching it rather than two machines counting alike.
    private static void Board(TestContext ctx, GameSession host, GameSession guest, string what,
        (int Score, int Deaths, int Kills) seat0, (int Score, int Deaths, int Kills) seat1)
    {
        var mine = host.Versus!;
        var theirs = guest.Versus!;
        string reading = $"host {Line(mine, 0)} / {Line(mine, 1)}, guest {Line(theirs, 0)} / {Line(theirs, 1)}";
        bool right = Matches(mine, 0, seat0) && Matches(mine, 1, seat1);
        ctx.Check(right, $"{what}: the host scores it as the decode says ({reading})");
        ctx.Check(Matches(theirs, 0, seat0) && Matches(theirs, 1, seat1),
            $"and the guest's board is the host's, seat for seat ({reading})");
    }

    // Both damage pools together. A round spends armour before health, so a single strike on a
    // pristine airframe moves the armour alone. A health-only reading would call it silent.
    private static float Ledger(FlightController rig) =>
        rig.Damage!.WholeArmor + rig.Damage.WholeHealth;

    private static bool Matches(VersusMatch match, int seat, (int Score, int Deaths, int Kills) want) =>
        match.ScoreOf(seat) == want.Score && match.DeathsOf(seat) == want.Deaths
        && match.KillsOf(seat) == want.Kills;

    private static string Line(VersusMatch match, int seat) =>
        $"P{seat + 1} {match.ScoreOf(seat)}pts {match.KillsOf(seat)}K/{match.DeathsOf(seat)}D";

    // Decision 9. Two guests that cannot reach each other at all, flying and firing, with the
    // host's forwarding the only thing between them.
    private static void Relay(TestContext ctx, GameSession host, GameSession first, GameSession second)
    {
        var link = host.NetLink!;
        int receivedBefore = link.Received;
        int relayedBefore = link.Relayed;
        int answeredBefore = host.NetPing?.Answered ?? 0;
        var gun = first.SeatRigs[1].Controller!;
        var pool = second.SeatRigs[1].Controller!.Projectiles!;
        pool.ScoredShooters.Add(gun.PlayerIndex);
        int roundsBefore = pool.CannonRoundsFired;

        gun.AutoFire = true;
        Lockstep(StarFlightSteps, host, first, second);
        gun.AutoFire = false;
        Lockstep(SettleSteps, host, first, second);

        // The aeroplane one guest flies, as the other guest has it. The owner's own sim pose is
        // what it is measured against, allowing for the buffer's deliberate read-behind.
        var own = first.SeatRigs[1].Controller!.WorldPosition;
        var shown = second.SeatRigs[1].Controller!.WorldPosition;
        var third = second.SeatRigs[2].Controller!.WorldPosition;
        float toOwner = shown.DistanceTo(own);
        float toThird = shown.DistanceTo(third);
        ctx.Check(toOwner < 50f,
            $"a guest's aeroplane stands where its owner has it on the other guest's world, {toOwner:0.0} m out, with no link between the two");
        // ABLE-TO-FAIL CONTROL. The same distance to the third aeroplane in that same world. A
        // reading that cannot tell two aircraft apart would pass the line above over nothing.
        ctx.Check(toThird > toOwner * 10f,
            $"ABLE-TO-FAIL CONTROL: the third aeroplane in that world is {toThird:0} m away, against {toOwner:0.0} m for the right one");

        int rounds = pool.CannonRoundsFired - roundsBefore;
        ctx.Check(rounds > 0,
            $"and the rounds one guest fired were built on the other under that guest's own seat ({rounds} round(s))");

        // Every arrival is forwarded to exactly one machine, the other guest. An echo would
        // forward it twice, and a relay that rewrote the sender would land it on the wrong seat.
        // A clock question is the host's own to answer and is the one arrival never forwarded.
        int questions = (host.NetPing?.Answered ?? 0) - answeredBefore;
        int received = link.Received - receivedBefore - questions;
        int relayed = link.Relayed - relayedBefore;
        ctx.Check(received > 0 && relayed == received,
            $"and the host forwarded every one of its {received} arrivals exactly once, never back to the peer it came from ({relayed} forwarded, {questions} clock question(s) answered instead)");
    }

    // Both or all three sessions through the same number of fixed steps, host first, the order a
    // listen server runs in.
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

    // One Dogfight launch, plus the spawn table both ends walk. Every peer is launched with the
    // same arguments unless a suite hands one its own. What otherwise differs between them is the
    // roster and the seed, which the join sets.
    private static SessionSpec MatchSpec(TestContext ctx, out IReadOnlyList<SpawnPoint> table,
        params string[] extraArgs)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");
        var args = new List<string>
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=1", "--mute",
            "--no-pads",
        };
        args.AddRange(extraArgs);
        var spec = SessionSpec.Parse(args.ToArray());
        var loaded = new SpawnPicker(spec).LoadSpawnList(missionZrdr, spec.Scenario);
        if (loaded is not { Count: >= 2 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors no usable net.zrd table");
        }

        table = loaded;
        return spec;
    }

    // The field, seat 0 on the host and one seat per guest after it.
    private static NetSeat[] Roster(int seats)
    {
        var roster = new NetSeat[seats];
        for (int i = 0; i < seats; i++)
        {
            roster[i] = new NetSeat
            {
                PeerId = i,
                SeatIndex = i,
                IsLocal = i == 0,
                Callsign = i == 0 ? "host" : $"guest{i}",
                PlaneNode = Airframes[i % Airframes.Length],
            };
        }

        NetSeats.Validate(roster);
        return roster;
    }

    // The process-global state several sessions in one process share. Saved before the first is
    // opened and put back after the last is freed. This suite cannot then shift the streams, or
    // the ambient clock, of every suite after it in the shard.
    internal readonly record struct Ambient(ulong Master, bool Pinned, GameClock? Clock, StartupProfile? Profile)
    {
        public static Ambient Save() =>
            new(Rng.Master, Rng.Pinned, GameClock.Current, StartupProfile.Current);

        public void Restore()
        {
            StartupProfile.Current = Profile;
            GameClock.Current = Clock;
            Rng.Reset(Master, Pinned);
        }
    }

    // One peer's whole rig: its own pane, its own world, its own session node.
    internal sealed record Ends(SubViewport Pane, GameSession Session, bool Built)
    {
        public static Ends Open(TestContext ctx, SessionSpec spec, INetTransport transport,
            bool isHost, ulong seed, IReadOnlyList<NetSeat>? roster,
            IReadOnlyList<string>? airframes = null, Func<int, LoadoutChoice?>? seatFit = null)
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
                NetAirframes = airframes ?? Airframes,
                NetSeatFit = seatFit,
            });
            pane.AddChild(session);
            return new Ends(pane, session, session.StartSession());
        }

        // Safe to call twice, since a suite may end a flight before its own cleanup runs.
        public void Close()
        {
            if (GodotObject.IsInstanceValid(Session))
            {
                Session.Free();
            }

            if (GodotObject.IsInstanceValid(Pane))
            {
                Pane.Free();
            }
        }
    }
}
