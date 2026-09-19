using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The session end of the wire with no engine under it. It covers the join a host answers with,
/// the dispatch from a type word to a registered handler, and the counters a suite reads.
/// Everything here runs on the loopback carrier, whose own contract is asserted next door, so a
/// failure is this class's.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetSessionTests
{
    private const ulong Seed = 0xfeedfacecafebeefUL;

    // The order both ends read a roster's airframe index against. Real node names, since the
    // index-to-name mapping is the one thing a roster cannot carry as text.
    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Fact]
    public void A_guest_is_not_joined_until_it_steps_and_then_it_has_the_whole_field()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(11));
        var host = NetSession.Host(mesh[0], Roster(), Seed, () => 42.5, Airframes);
        var guest = NetSession.Guest(mesh[1], Airframes);

        Assert.True(host.Joined);
        Assert.False(guest.Joined);
        Assert.Empty(guest.Seats);
        Assert.Equal(NetMessage.NoSeat, guest.LocalSeat);

        guest.Step(0.016);

        Assert.True(guest.Joined);
        Assert.Equal(Seed, guest.Handshake.Seed);
        Assert.Equal(42.5, guest.Handshake.HostClock);
        Assert.Equal(1, guest.LocalSeat);
        Assert.Equal(new[] { 0, 1 }, guest.Seats.Select(s => s.SeatIndex));
        Assert.Equal(new[] { "host", "guest" }, guest.Seats.Select(s => s.Callsign));
        Assert.Equal(Airframes, guest.Seats.Select(s => s.PlaneNode));
        Assert.Equal(new[] { false, true }, guest.Seats.Select(s => s.IsLocal));
        Assert.Equal(new[] { 0, 1 }, guest.Seats.Select(s => s.PeerId));
    }

    // The seat a guest flies is in the handshake, not the roster. The roster's entries carry no
    // peer id, so without it a guest could not tell which of them is its own.
    [Fact]
    public void The_handshake_names_the_seat_and_the_roster_is_read_against_it()
    {
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(12));
        var seats = Roster().Append(new NetSeat { PeerId = 2, SeatIndex = 2, Callsign = "third" }).ToArray();
        var host = NetSession.Host(mesh[0], seats, Seed, null, Airframes);
        var second = NetSession.Guest(mesh[1], Airframes);
        var third = NetSession.Guest(mesh[2], Airframes);

        second.Step(0.016);
        third.Step(0.016);

        Assert.Equal(4, host.Sent);
        Assert.Equal(1, second.LocalSeat);
        Assert.Equal(2, third.LocalSeat);
        Assert.Equal(new[] { false, true, false }, second.Seats.Select(s => s.IsLocal));
        Assert.Equal(new[] { false, false, true }, third.Seats.Select(s => s.IsLocal));
    }

    [Fact]
    public void A_registered_handler_takes_its_own_type_and_nothing_else()
    {
        var (host, guest) = Joined(13);
        var scores = new List<(int Peer, ScoreMessage Message)>();
        guest.On<ScoreMessage>((peer, message) => scores.Add((peer, message)));
        int received = guest.Received;

        host.Broadcast(new ScoreMessage(1, -3, 4, 5));
        host.Send(1, new ScoreMessage(0, 9, 1, 0));
        guest.Step(0.016);

        Assert.Equal(2, scores.Count);
        Assert.Equal(new ScoreMessage(1, -3, 4, 5), scores[0].Message);
        Assert.Equal(new ScoreMessage(0, 9, 1, 0), scores[1].Message);
        Assert.Equal(0, scores[0].Peer);
        Assert.Equal(received + 2, guest.Received);
        Assert.Equal(0, guest.DroppedUnknown);
        Assert.Equal(0, guest.Malformed);
    }

    [Fact]
    public void A_type_no_handler_claims_is_counted_rather_than_dispatched()
    {
        var (host, guest) = Joined(14);
        int unknown = guest.DroppedUnknown;

        host.Send(1, new MatchStateMessage(30f, 300f, 5, NetMatchEnd.Running));
        guest.Step(0.016);

        Assert.Equal(unknown + 1, guest.DroppedUnknown);
        Assert.Equal(0, guest.Malformed);
    }

    // The two counters answer different questions. An unclaimed type is a handler nobody bound,
    // a malformed body is bad bytes on a bound type, and conflating them hides a bug.
    [Fact]
    public void A_bound_type_whose_body_will_not_read_is_malformed_not_unknown()
    {
        var (_, guest) = Joined(15);
        int reached = 0;
        guest.On<ScoreMessage>((_, _) => reached++);
        int unknown = guest.DroppedUnknown;

        // A whole, self-consistent header on a body two bytes short of the message it names.
        var payload = new byte[ScoreMessage.Size - 2];
        payload[0] = (byte)NetMessageType.Score;
        payload[1] = (byte)((int)NetMessageType.Score >> 8);
        payload[2] = (byte)payload.Length;
        guest.OnPayload(0, 0, payload);

        Assert.Equal(1, guest.Malformed);
        Assert.Equal(unknown, guest.DroppedUnknown);
        Assert.Equal(0, reached);
    }

    [Fact]
    public void A_payload_whose_header_disagrees_with_its_length_is_dropped_unread()
    {
        var (_, guest) = Joined(16);
        int unknown = guest.DroppedUnknown;

        guest.OnPayload(0, 0, new byte[] { 0x27, 0x00, 0x40, 0x00 });
        guest.OnPayload(0, 0, new byte[] { 0x13 });

        Assert.Equal(unknown + 2, guest.DroppedUnknown);
        Assert.Equal(0, guest.Malformed);
    }

    // The join is this class's own business. A later feature cannot unhook it by registering over
    // its two types, which would leave a guest waiting for a seed that goes nowhere.
    [Fact]
    public void The_joins_own_types_cannot_be_registered_over()
    {
        var (host, guest) = Joined(17);

        Assert.Throws<ArgumentException>(() => guest.On<HandshakeMessage>((_, _) => { }));
        Assert.Throws<ArgumentException>(() => guest.On<SeatRosterMessage>((_, _) => { }));
        Assert.Throws<ArgumentException>(() => host.On<HandshakeMessage>((_, _) => { }));
    }

    // A message goes out under the class its own type declares, never under one the caller picked.
    [Fact]
    public void Every_send_carries_the_reliability_its_type_declares()
    {
        var wire = new Recorder();
        var host = NetSession.Host(wire, Roster(), Seed, null, Airframes);

        host.Send(1, new AircraftStateMessage(
            0, 1, default, default, default, 0f, 0f, 0f, 0f, false));
        host.Broadcast(new ScoreMessage(0, 1, 0, 0));

        Assert.Equal(
            new[] { NetReliability.Reliable, NetReliability.Reliable, NetReliability.UnreliableSequenced, NetReliability.Reliable },
            wire.Classes);
        Assert.Equal(4, host.Sent);
        Assert.Equal(ScoreMessage.Size, wire.Lengths[^1]);
    }

    [Fact]
    public void A_seat_that_hangs_up_keeps_its_place_in_the_roster()
    {
        var (host, guest) = Joined(18);

        host.OnPeerDisconnected(1);

        Assert.Equal(2, host.Seats.Count);
        Assert.Equal(new[] { 0, 1 }, host.Seats.Select(s => s.SeatIndex));
        Assert.Equal(1, guest.LocalSeat);
    }

    private static NetSeat[] Roster() => new NetSeat[]
    {
        new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
        new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
    };

    // A host and a guest past their join, the state every test above the join starts from.
    private static (NetSession Host, NetSession Guest) Joined(int seed)
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(seed));
        var host = NetSession.Host(mesh[0], Roster(), Seed, null, Airframes);
        var guest = NetSession.Guest(mesh[1], Airframes);
        guest.Step(0.016);
        return (host, guest);
    }

    // A transport that keeps what it was handed instead of carrying it. A send's reliability
    // class and width can then be read back without a peer on the other end.
    private sealed class Recorder : INetTransport
    {
        public List<NetReliability> Classes { get; } = new();

        public List<int> Lengths { get; } = new();

        public int LocalPeer => 0;

        public IReadOnlyList<int> Peers { get; } = new[] { 1 };

        public void Bind(INetTransportListener listener)
        {
            foreach (int peer in Peers)
            {
                listener.OnPeerConnected(peer);
            }
        }

        public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
        {
            Classes.Add(reliability);
            Lengths.Add(payload.Length);
        }

        public void Disconnect(int peer)
        {
        }

        public void Step(double dt)
        {
        }
    }
}
