using System;
using CSVM.Net;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The desync counters one machine keeps, fed scripted payloads so every rule is met on purpose.
/// The cases are a sequence gap, a stale arrival, the wrap, a late hit or burst, and a reliable
/// event out of order. Each counter is also shown to
/// stay at zero on the traffic a healthy match sends, which is what makes a nonzero one mean
/// something.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetInstrumentsTests
{
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Fact]
    public void A_gap_in_a_seats_sequence_counts_what_went_missing_and_the_first_sample_only_sets_the_baseline()
    {
        var net = new NetInstruments();
        net.Arrived(State(1, 100));
        net.Arrived(State(1, 101));
        net.Arrived(State(1, 104));
        net.Arrived(Fire(1, 7));
        net.Arrived(Fire(1, 9));

        Assert.Equal(2, net.StateGaps);
        Assert.Equal(1, net.FireGaps);
        Assert.Equal(3, net.Dropped);
        Assert.Equal(0, net.StaleArrivals);
    }

    [Fact]
    public void Seats_and_streams_keep_their_own_ladders()
    {
        var net = new NetInstruments();
        net.Arrived(State(1, 5));
        net.Arrived(State(2, 900));
        net.Arrived(Fire(1, 40));
        net.Arrived(State(1, 6));
        net.Arrived(State(2, 901));
        net.Arrived(Fire(1, 41));

        Assert.Equal(0, net.Dropped);
    }

    [Fact]
    public void An_arrival_at_or_below_the_newest_is_stale_and_the_wrap_is_not()
    {
        var net = new NetInstruments();
        net.Arrived(State(1, 65534));
        net.Arrived(State(1, 65535));
        net.Arrived(State(1, 0));
        net.Arrived(State(1, 1));
        Assert.Equal(0, net.StaleArrivals);
        Assert.Equal(0, net.StateGaps);

        net.Arrived(State(1, 1));
        net.Arrived(State(1, 65535));
        Assert.Equal(2, net.StaleArrivals);
        Assert.Equal(2, net.Late);
    }

    [Fact]
    public void A_hit_or_a_burst_for_a_seat_already_dead_is_late_until_the_seat_is_placed_again()
    {
        var net = new NetInstruments();
        net.Arrived(Fire(1, 1));
        net.Arrived(Bytes(new HitMessage(1, 0, 0, 1f, 0, Vector3.Zero)));
        Assert.Equal(0, net.Late);

        net.Arrived(Bytes(new DeathMessage(1, 0, NetDeathCause.Killer, 0)));
        net.Arrived(Bytes(new HitMessage(1, 0, 0, 1f, 0, Vector3.Zero)));
        net.Arrived(Fire(1, 2));
        Assert.Equal(1, net.LateHits);
        Assert.Equal(1, net.LateFire);

        net.Arrived(Bytes(new SpawnMessage(1, NetSpawnKind.Respawn, 0)));
        net.Arrived(Bytes(new HitMessage(1, 0, 0, 1f, 0, Vector3.Zero)));
        net.Arrived(Fire(1, 3));
        Assert.Equal(1, net.LateHits);
        Assert.Equal(1, net.LateFire);
        Assert.Equal(0, net.OrderViolations);
    }

    [Fact]
    public void A_score_line_is_in_order_only_when_its_deaths_were_reported_first()
    {
        var net = new NetInstruments();
        net.Arrived(Bytes(new ScoreMessage(1, 0, 0, 3)));          // the baseline, whatever it says
        net.Arrived(Bytes(new DeathMessage(1, 0, NetDeathCause.Killer, 0)));
        net.Arrived(Bytes(new ScoreMessage(1, -1, 0, 4)));
        Assert.Equal(0, net.OrderViolations);

        net.Arrived(Bytes(new ScoreMessage(1, -2, 0, 5)));         // a death nobody reported
        Assert.Equal(1, net.OrderViolations);

        net.Arrived(Bytes(new ScoreMessage(1, 0, 0, 0)));          // a rematch resets the line
        net.Arrived(Bytes(new DeathMessage(1, 0, NetDeathCause.Killer, 0)));
        net.Arrived(Bytes(new ScoreMessage(1, -1, 0, 1)));
        Assert.Equal(1, net.OrderViolations);
    }

    [Fact]
    public void A_respawn_for_a_seat_nobody_reported_dead_is_out_of_order_once_the_seat_is_known()
    {
        var net = new NetInstruments();
        // A seat this machine has never heard of may have died before it joined.
        net.Arrived(Bytes(new SpawnMessage(1, NetSpawnKind.Respawn, 0)));
        Assert.Equal(0, net.OrderViolations);

        net.Arrived(Bytes(new SpawnMessage(1, NetSpawnKind.Respawn, 0)));
        Assert.Equal(1, net.OrderViolations);

        // The opening placement is never a respawn, so it breaks no order.
        net.Arrived(Bytes(new SpawnMessage(1, NetSpawnKind.Opening, 0)));
        Assert.Equal(1, net.OrderViolations);
    }

    [Fact]
    public void What_this_machine_says_itself_counts_toward_a_seats_life_and_never_toward_a_stream()
    {
        var net = new NetInstruments();
        net.Said(Bytes(new DeathMessage(0, 1, NetDeathCause.Killer, 0)));
        net.Said(State(0, 1));
        net.Said(State(0, 9));
        net.Arrived(Bytes(new SpawnMessage(0, NetSpawnKind.Respawn, 0)));

        Assert.Equal(0, net.Dropped);
        Assert.Equal(0, net.OrderViolations);
    }

    [Fact]
    public void Readings_subtract_to_what_one_stretch_added()
    {
        var net = new NetInstruments();
        net.Arrived(State(1, 1));
        net.Arrived(State(1, 3));
        var before = net.Reading;
        net.Arrived(State(1, 6));
        net.Arrived(State(1, 6));

        var delta = net.Reading - before;
        Assert.Equal(new NetInstrumentReading(2, 0, 1, 0, 0, 0), delta);
        Assert.Equal(2, delta.Dropped);
        Assert.Equal(1, delta.Late);
    }

    [Fact]
    public void A_session_feeds_its_instruments_and_counts_a_broadcast_once_however_many_peers()
    {
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(21));
        var seats = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "first", PlaneNode = Airframes[1] },
            new() { PeerId = 2, SeatIndex = 2, Callsign = "second", PlaneNode = Airframes[0] },
        };
        var host = NetSession.Host(mesh[0], seats, 1UL, null, Airframes);
        var first = NetSession.Guest(mesh[1], Airframes);
        var second = NetSession.Guest(mesh[2], Airframes);
        first.Step(0.016);
        second.Step(0.016);
        int sent = host.Sent;

        host.Broadcast(new DeathMessage(0, 1, NetDeathCause.Killer, 0));
        host.Broadcast(new ScoreMessage(0, 0, 0, 1));
        first.Step(0.016);

        // Two peers each took both messages, and the host's own ledger saw each once.
        Assert.Equal(sent + 4, host.Sent);
        Assert.Equal(0, host.Instruments.OrderViolations);
        Assert.Equal(0, first.Instruments.OrderViolations);

        host.Broadcast(new ScoreMessage(0, 0, 0, 2));
        first.Step(0.016);
        Assert.Equal(1, host.Instruments.OrderViolations);
        Assert.Equal(1, first.Instruments.OrderViolations);
    }

    [Fact]
    public void The_readout_line_names_the_end_and_every_counter_in_the_invariant_culture()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(22));
        var seats = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        var host = NetSession.Host(mesh[0], seats, 1UL, null, Airframes);
        var poses = new RemotePoseTally(0, 0, 7, 2, 1, 2, 3.0, 2.25f, 1);

        string line = NetInstruments.Describe(host, poses);

        Assert.StartsWith("net host seat 0 peers 1:", line, StringComparison.Ordinal);
        Assert.Contains("| poses interp 7 extrap 2 starved 1 jumps 1 |", line, StringComparison.Ordinal);
        Assert.EndsWith("extrap err 1.50 m mean 2.25 m worst", line, StringComparison.Ordinal);
    }

    private static byte[] State(byte seat, ushort sequence) => Bytes(new AircraftStateMessage(
        seat, sequence, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0f, 0f, 0f, 0f, false));

    private static byte[] Fire(byte seat, ushort sequence) =>
        Bytes(new FireMessage(seat, 0, sequence, Vector3.Zero, Vector3.Forward, NetMessage.NoSeat));

    private static byte[] Bytes<T>(T message)
        where T : struct, INetMessage<T>
    {
        var buffer = new byte[256];
        int length = message.Write(buffer);
        return buffer.AsSpan(0, length).ToArray();
    }
}
