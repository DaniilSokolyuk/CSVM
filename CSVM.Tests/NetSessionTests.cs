using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Godot;
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

    // Decision 9's star. Two guests that cannot hear each other at all. Anything one of them
    // learns about the other came through the host's relay and nowhere else.
    [Fact]
    public void A_guests_event_reaches_the_other_guest_carrying_its_own_seat()
    {
        var (host, first, second) = Star(19);
        host.RelayToOthers<FireMessage>();
        var reachedFirst = new List<FireMessage>();
        var reachedSecond = new List<FireMessage>();
        first.On<FireMessage>((_, fire) => reachedFirst.Add(fire));
        second.On<FireMessage>((_, fire) => reachedSecond.Add(fire));
        int sent = host.Sent;

        first.Broadcast(new FireMessage(1, 7, 3, Vector3.Up, Vector3.Forward, NetMessage.NoSeat),
            NetChannels.ForSeat(1));
        host.Step(0.016);
        second.Step(0.016);
        first.Step(0.016);

        Assert.Single(reachedSecond);
        Assert.Equal(1, reachedSecond[0].Seat);
        Assert.Equal(7, reachedSecond[0].Weapon);
        Assert.Empty(reachedFirst);
        Assert.Equal(1, host.Relayed);
        Assert.Equal(sent, host.Sent);
    }

    // A forward is the bytes as they arrived, so the seat inside them is never rewritten to the
    // host's own. Without the relay the second guest hears nothing at all.
    [Fact]
    public void Nothing_reaches_the_other_guest_while_the_relay_is_unregistered()
    {
        var (host, first, second) = Star(20);
        int reached = 0;
        second.On<FireMessage>((_, _) => reached++);

        first.Broadcast(new FireMessage(1, 7, 3, Vector3.Up, Vector3.Forward, NetMessage.NoSeat),
            NetChannels.ForSeat(1));
        host.Step(0.016);
        second.Step(0.016);

        Assert.Equal(0, reached);
        Assert.Equal(0, host.Relayed);
    }

    [Fact]
    public void A_seat_addressed_message_is_forwarded_to_that_seats_owner_alone()
    {
        var (host, first, second) = Star(21);
        host.RelayToSeatOwner<HitMessage>(hit => hit.VictimSeat);
        var reachedFirst = new List<HitMessage>();
        var reachedSecond = new List<HitMessage>();
        first.On<HitMessage>((_, hit) => reachedFirst.Add(hit));
        second.On<HitMessage>((_, hit) => reachedSecond.Add(hit));

        Assert.True(first.SendToSeat(2, new HitMessage(2, 1, 5, 1f, 0, Vector3.Zero)));
        host.Step(0.016);
        second.Step(0.016);
        first.Step(0.016);

        Assert.Single(reachedSecond);
        Assert.Equal(2, reachedSecond[0].VictimSeat);
        Assert.Equal(1, reachedSecond[0].ShooterSeat);
        Assert.Empty(reachedFirst);
        Assert.Equal(1, host.Relayed);
    }

    // The host is the seat's owner, so the relay has nowhere to send it: the host's own handler
    // is the whole delivery.
    [Fact]
    public void A_message_for_a_seat_the_host_flies_is_not_forwarded_anywhere()
    {
        var (host, first, second) = Star(22);
        host.RelayToSeatOwner<HitMessage>(hit => hit.VictimSeat);
        int reachedHost = 0;
        int reachedSecond = 0;
        host.On<HitMessage>((_, _) => reachedHost++);
        second.On<HitMessage>((_, _) => reachedSecond++);

        Assert.True(first.SendToSeat(0, new HitMessage(0, 1, 5, 1f, 0, Vector3.Zero)));
        host.Step(0.016);
        second.Step(0.016);

        Assert.Equal(1, reachedHost);
        Assert.Equal(0, reachedSecond);
        Assert.Equal(0, host.Relayed);
    }

    // A guest talks to the host and to nobody else. It has no relay to register, and a send to
    // its own seat leaves the machine as nothing at all.
    [Fact]
    public void A_guest_relays_nothing_and_never_addresses_itself()
    {
        var (_, first, _) = Star(23);

        Assert.Throws<InvalidOperationException>(() => first.RelayToOthers<FireMessage>());
        Assert.Throws<InvalidOperationException>(
            () => first.RelayToSeatOwner<HitMessage>(hit => hit.VictimSeat));
        Assert.False(first.SendToSeat(1, new HitMessage(1, 1, 5, 1f, 0, Vector3.Zero)));
        Assert.False(first.SendToSeat(9, new HitMessage(9, 1, 5, 1f, 0, Vector3.Zero)));
        Assert.Equal(first.HostPeer, first.PeerOfSeat(2));
    }

    private static NetSeat[] Roster() => new NetSeat[]
    {
        new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
        new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
    };

    // Three sessions in a star: the two guests are unlinked before either binds, so neither ever
    // sees the other as a peer. Both are past their join when this returns.
    private static (NetSession Host, NetSession First, NetSession Second) Star(int seed)
    {
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(seed));
        mesh[1].Disconnect(2);
        var seats = Roster()
            .Append(new NetSeat { PeerId = 2, SeatIndex = 2, Callsign = "second", PlaneNode = Airframes[0] })
            .ToArray();
        var host = NetSession.Host(mesh[0], seats, Seed, null, Airframes);
        var first = NetSession.Guest(mesh[1], Airframes);
        var second = NetSession.Guest(mesh[2], Airframes);
        first.Step(0.016);
        second.Step(0.016);
        return (host, first, second);
    }

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
