using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// The one object a session owns to talk to its peers. It holds the transport, routes an arrival
/// to the handler registered on its type, and sends under the class the type itself declares.
/// The only meaning it knows is the join: a host answers a peer with the handshake and the
/// roster, and a guest applies them. Everything else is a handler the session registers, so no
/// rule about aircraft, scores or missions lives here.
/// ⚠ Nothing arrives until <see cref="Step"/> runs. A session steps this before its own
/// simulation step, so a payload is applied on the step that follows its arrival.
/// </summary>
public sealed class NetSession : INetTransportListener
{
    /// <summary>The send scratch's width, set by the widest message, a full roster. One buffer
    /// per session rather than one per send, since a send completes inside the call.</summary>
    public const int SendBufferBytes = 512;

    private readonly INetTransport _transport;
    private readonly Dictionary<NetMessageType, Handler> _handlers = new();
    private readonly List<NetSeat> _seats = new();
    private readonly List<NetSeatEntry> _received = new();
    private readonly IReadOnlyList<string> _airframes;
    private readonly Func<double> _clock;
    private readonly byte[] _scratch = new byte[SendBufferBytes];
    private readonly ulong _seed;

    private NetHandshake? _handshake;
    private bool _rosterArrived;

    private NetSession(INetTransport transport, bool isHost, ulong seed, Func<double>? clock,
        IReadOnlyList<NetSeat>? roster, IReadOnlyList<string>? airframes)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        IsHost = isHost;
        _seed = seed;
        _clock = clock ?? (() => 0.0);
        _airframes = airframes ?? Array.Empty<string>();
        if (roster != null)
        {
            _seats.AddRange(roster);
            LocalSeat = SeatOf(transport.LocalPeer);
        }

        if (!isHost)
        {
            Route<HandshakeMessage>(TakeHandshake);
            Route<SeatRosterMessage>(TakeRoster);
        }

        // Last, and only once every field stands. Bind announces the peers the transport already
        // has, and a host answers each of them from inside that call.
        _transport.Bind(this);
    }

    /// <summary>One payload as it arrived, routed to the deserialiser its type word names. A
    /// delegate rather than a generic handler list because a span cannot be a type argument.
    /// </summary>
    private delegate void Handler(int peer, ReadOnlySpan<byte> payload);

    /// <summary>Whether this peer owns the match: the seed, the roster and every host-authoritative
    /// rule. A guest holds the mirror of what it is told.</summary>
    public bool IsHost { get; }

    /// <summary>The seat this machine flies, or <see cref="NetMessage.NoSeat"/> before a guest has
    /// been given one. A host reads it off its own roster at construction.</summary>
    public int LocalSeat { get; private set; } = NetMessage.NoSeat;

    /// <summary>The whole match's roster in seat order: the host's own, or the guest's copy of it.
    /// Empty on a guest until the join lands.</summary>
    public IReadOnlyList<NetSeat> Seats => _seats;

    /// <summary>The seed and host clock a guest was joined with, or what a host would send now.
    /// </summary>
    public NetHandshake Handshake => _handshake ?? new NetHandshake(_seed, _clock());

    /// <summary>Whether this session has everything it needs to build: a host always does, a guest
    /// once both the handshake and the roster have arrived.</summary>
    public bool Joined => IsHost || (_handshake != null && _rosterArrived);

    /// <summary>Every peer this session can send to, this end excluded.</summary>
    public IReadOnlyList<int> Peers => _transport.Peers;

    /// <summary>This end's own peer id on the transport.</summary>
    public int LocalPeer => _transport.LocalPeer;

    /// <summary>Payloads handed to the transport since construction.</summary>
    public int Sent { get; private set; }

    /// <summary>Payloads the transport delivered here, malformed and unrouted ones included.
    /// </summary>
    public int Received { get; private set; }

    /// <summary>Payloads discarded because no handler claimed the type word, or because the header
    /// did not describe the buffer. The counter a suite reads to prove a handler is bound.</summary>
    public int DroppedUnknown { get; private set; }

    /// <summary>Payloads whose type was routed but whose body would not deserialise. Separate from
    /// <see cref="DroppedUnknown"/>: an unclaimed type is a missing handler, this is bad bytes.
    /// </summary>
    public int Malformed { get; private set; }

    /// <summary>Opens the match's own end: <paramref name="roster"/> is the whole field as this
    /// machine has it, <paramref name="seed"/> the master every peer draws from, and
    /// <paramref name="clock"/> what the handshake stamps. Every peer already on the transport is
    /// answered at once.</summary>
    public static NetSession Host(INetTransport transport, IReadOnlyList<NetSeat> roster,
        ulong seed, Func<double>? clock = null, IReadOnlyList<string>? airframes = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return new NetSession(transport, isHost: true, seed, clock, roster, airframes);
    }

    /// <summary>Opens a joining end, which knows nothing until the host answers.
    /// <paramref name="airframes"/> is the order both peers read a roster's airframe index
    /// against.</summary>
    public static NetSession Guest(INetTransport transport, IReadOnlyList<string>? airframes = null)
        => new(transport, isHost: false, seed: 0, clock: null, roster: null, airframes);

    /// <summary>Routes <typeparamref name="T"/> to <paramref name="handler"/>, replacing any
    /// handler already on that type. The join's two types are this class's own and are refused, so
    /// a later feature cannot unhook the join by registering over it.</summary>
    /// <typeparam name="T">The message this handler takes.</typeparam>
    public void On<T>(Action<int, T> handler)
        where T : struct, INetMessage<T>
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (T.Type is NetMessageType.Handshake or NetMessageType.SeatRoster)
        {
            throw new ArgumentException($"{T.Type} is the join, which NetSession owns", nameof(handler));
        }

        Route(handler);
    }

    /// <summary>Sends one message to <paramref name="peer"/> under the class its own type
    /// declares, on <paramref name="channel"/>. The bytes are packed into this session's scratch
    /// and copied by the transport, so nothing is retained.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    public void Send<T>(int peer, in T message, int channel = 0)
        where T : struct, INetMessage<T>
    {
        int length = message.Write(_scratch);
        _transport.Send(peer, _scratch.AsSpan(0, length), T.Reliability, channel);
        Sent++;
    }

    /// <summary>Sends one message to every peer on the roster.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    public void Broadcast<T>(in T message, int channel = 0)
        where T : struct, INetMessage<T>
    {
        var peers = _transport.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            Send(peers[i], message, channel);
        }
    }

    /// <summary>Advances the transport by <paramref name="dt"/> seconds, which is where every
    /// arrival is handed to its handler. The one place this session does anything on its own.
    /// </summary>
    public void Step(double dt) => _transport.Step(dt);

    /// <inheritdoc/>
    public void OnPeerConnected(int peer)
    {
        if (IsHost)
        {
            SendJoin(peer);
        }
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(int peer)
    {
        // The seat stays in the roster: every seat-indexed table is addressed by the index
        // directly, so closing a gap would renumber the field mid-match (NetSeats.Validate).
    }

    /// <inheritdoc/>
    public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload)
    {
        Received++;
        if (!NetMessage.TryReadHeader(payload, out var type, out int length) || length != payload.Length)
        {
            DroppedUnknown++;
            return;
        }

        if (!_handlers.TryGetValue(type, out var handler))
        {
            DroppedUnknown++;
            return;
        }

        handler(peer, payload);
    }

    private void Route<T>(Action<int, T> handler)
        where T : struct, INetMessage<T>
    {
        _handlers[T.Type] = (peer, payload) =>
        {
            if (T.TryRead(payload, out var message))
            {
                handler(peer, message);
            }
            else
            {
                Malformed++;
            }
        };
    }

    // The host's answer to one joining peer, handshake first: it names the seat, and the roster
    // that follows is read against it. Reliable, so the order the guest sees is this order.
    private void SendJoin(int peer)
    {
        int seat = SeatOf(peer);
        Send(peer, new HandshakeMessage(_seed, _clock(), (byte)seat));
        Send(peer, new SeatRosterMessage((uint)_seed, RosterEntries()));
    }

    private NetSeatEntry[] RosterEntries()
    {
        var entries = new NetSeatEntry[_seats.Count];
        for (int i = 0; i < _seats.Count; i++)
        {
            var seat = _seats[i];
            entries[i] = new NetSeatEntry(
                (byte)seat.SeatIndex, (byte)seat.TeamId, AirframeIndex(seat.PlaneNode),
                seat.PeerId == _transport.LocalPeer, seat.Callsign);
        }

        return entries;
    }

    private int SeatOf(int peer)
    {
        foreach (var seat in _seats)
        {
            if (seat.PeerId == peer)
            {
                return seat.SeatIndex;
            }
        }

        return NetMessage.NoSeat;
    }

    private void TakeHandshake(int peer, HandshakeMessage message)
    {
        _handshake = new NetHandshake(message.Seed, message.HostClock);
        LocalSeat = message.Seat;
        RebuildSeats(peer);
    }

    private void TakeRoster(int peer, SeatRosterMessage message)
    {
        _received.Clear();
        _received.AddRange(message.Seats);
        _rosterArrived = true;
        RebuildSeats(peer);
    }

    // A guest's roster, rebuilt whenever either half of the join lands, because the seat the
    // handshake names is what decides which entry this machine flies. Every other seat is reached
    // through the peer that sent the roster, which in a listen server is the host for all of them.
    private void RebuildSeats(int from)
    {
        if (!_rosterArrived)
        {
            return;
        }

        _seats.Clear();
        foreach (var entry in _received)
        {
            bool local = entry.Seat == LocalSeat;
            _seats.Add(new NetSeat
            {
                PeerId = local ? _transport.LocalPeer : from,
                SeatIndex = entry.Seat,
                TeamId = entry.Team,
                IsLocal = local,
                Callsign = entry.Callsign,
                PlaneNode = AirframeName(entry.Plane),
            });
        }
    }

    private byte AirframeIndex(string plane)
    {
        for (int i = 0; i < _airframes.Count; i++)
        {
            if (string.Equals(_airframes[i], plane, StringComparison.Ordinal))
            {
                return (byte)i;
            }
        }

        return NetMessage.NoSeat;
    }

    private string AirframeName(byte index) =>
        index < _airframes.Count ? _airframes[index] : "";
}
