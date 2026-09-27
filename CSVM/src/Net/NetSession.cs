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

    /// <summary>What a peer lookup returns when nothing on the transport answers to it.</summary>
    public const int NoPeer = -1;

    private readonly INetTransport _transport;
    private readonly Dictionary<NetMessageType, Handler> _handlers = new();
    private readonly Dictionary<NetMessageType, Relay> _relays = new();
    private readonly List<NetSeat> _seats = new();
    private readonly List<NetSeatEntry> _received = new();
    private readonly IReadOnlyList<string> _airframes;
    private readonly Func<double> _clock;
    private readonly byte[] _scratch = new byte[SendBufferBytes];
    private readonly ulong _seed;

    private NetHandshake? _handshake;
    private bool _rosterArrived;
    private int _hostPeer = NoPeer;

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

    /// <summary>One arrival offered to the star's relay before its own handler sees it. The
    /// channel rides along because a forward keeps the one it arrived on.</summary>
    private delegate void Relay(int from, int channel, ReadOnlySpan<byte> payload);

    /// <summary>Raised when a peer drops off the transport. Its seats stay on the roster; what
    /// leaving means for the aeroplanes it flew is the session's rule.</summary>
    public event Action<int>? PeerLeft;

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

    /// <summary>Whether this session has everything it needs to build. A host always does. A guest
    /// does once the handshake and the roster have arrived and the roster holds the guest's seat.
    /// </summary>
    public bool Joined => IsHost || (_handshake != null && _rosterArrived && HoldsLocalSeat());

    /// <summary>Every peer this session can send to, this end excluded.</summary>
    public IReadOnlyList<int> Peers => _transport.Peers;

    /// <summary>This end's own peer id on the transport.</summary>
    public int LocalPeer => _transport.LocalPeer;

    /// <summary>Payloads handed to the transport since construction.</summary>
    public int Sent { get; private set; }

    /// <summary>Payloads the transport delivered here, malformed and unrouted ones included.
    /// </summary>
    public int Received { get; private set; }

    /// <summary>Arrivals this host forwarded to another peer on the star. Counted apart from
    /// <see cref="Sent"/>, which stays what this machine said itself.</summary>
    public int Relayed { get; private set; }

    /// <summary>The peer the host is reached through: a guest's link to it, or a host's own id.
    /// <see cref="NoPeer"/> before a guest's join lands.</summary>
    public int HostPeer => IsHost ? _transport.LocalPeer : _hostPeer;

    /// <summary>Payloads discarded because no handler claimed the type word, or because the header
    /// did not describe the buffer. The counter a suite reads to prove a handler is bound.</summary>
    public int DroppedUnknown { get; private set; }

    /// <summary>Payloads whose type was routed but whose body would not deserialise. Separate from
    /// <see cref="DroppedUnknown"/>: an unclaimed type is a missing handler, this is bad bytes.
    /// </summary>
    public int Malformed { get; private set; }

    /// <summary>The desync counters over everything this end sent and everything that reached it,
    /// fed before any handler or relay sees an arrival.</summary>
    public NetInstruments Instruments { get; } = new();

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

    /// <summary>Hands a lobby's carrier back when this session still holds it, so the next flight
    /// can bind it. A bare carrier binds once for its life and is left as it is.</summary>
    public void Release() => (_transport as NetLobby)?.Release(this);

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
        Instruments.Said(_scratch.AsSpan(0, length));
        Sent++;
    }

    /// <summary>Sends one message to every peer on the roster. The instruments hear it once,
    /// since it is one thing this machine said however many peers it reached.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    public void Broadcast<T>(in T message, int channel = 0)
        where T : struct, INetMessage<T>
    {
        int length = message.Write(_scratch);
        var peers = _transport.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            _transport.Send(peers[i], _scratch.AsSpan(0, length), T.Reliability, channel);
            Sent++;
        }

        Instruments.Said(_scratch.AsSpan(0, length));
    }

    /// <summary>The peer <paramref name="seat"/> is reached through, <see cref="NoPeer"/> for a
    /// seat this roster does not hold. A guest's every other seat reads as the host, which is what
    /// makes a send to a seat a send into the relay.</summary>
    public int PeerOfSeat(int seat)
    {
        foreach (var entry in _seats)
        {
            if (entry.SeatIndex == seat)
            {
                return entry.PeerId;
            }
        }

        return NoPeer;
    }

    /// <summary>Sends one message to the machine that owns <paramref name="seat"/>, through the
    /// host when that owner is not a peer of this end. False when the seat is flown here or is not
    /// on the roster, which is the caller's cue that nothing left the machine.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    public bool SendToSeat<T>(int seat, in T message, int channel = 0)
        where T : struct, INetMessage<T>
    {
        int peer = PeerOfSeat(seat);
        if (peer == NoPeer || peer == _transport.LocalPeer)
        {
            return false;
        }

        Send(peer, message, channel);
        return true;
    }

    /// <summary>Forwards every arriving <typeparamref name="T"/> to the other guests, byte for
    /// byte. The payload is untouched, so the seat inside it stays the sender's own. The peer
    /// it came from is never sent its own message back.</summary>
    /// <typeparam name="T">The message being relayed.</typeparam>
    public void RelayToOthers<T>()
        where T : struct, INetMessage<T>
    {
        RequireHostRelay();
        _relays[T.Type] = (from, channel, payload) =>
        {
            var peers = _transport.Peers;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] != from)
                {
                    Forward(peers[i], T.Type, channel, payload);
                }
            }
        };
    }

    /// <summary>Forwards every arriving <typeparamref name="T"/> to the one machine that owns the
    /// seat <paramref name="seatOf"/> reads out of it, and to nobody else. Nothing is forwarded
    /// when that seat is flown on the host or by the sender itself.</summary>
    /// <typeparam name="T">The message being relayed.</typeparam>
    public void RelayToSeatOwner<T>(Func<T, int> seatOf)
        where T : struct, INetMessage<T>
    {
        ArgumentNullException.ThrowIfNull(seatOf);
        RequireHostRelay();
        _relays[T.Type] = (from, channel, payload) =>
        {
            if (!T.TryRead(payload, out var message))
            {
                return;
            }

            int peer = PeerOfSeat(seatOf(message));
            if (peer != NoPeer && peer != from && peer != _transport.LocalPeer)
            {
                Forward(peer, T.Type, channel, payload);
            }
        };
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
        PeerLeft?.Invoke(peer);
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

        Instruments.Arrived(payload);

        // The relay runs before the handler, and on the bytes as they arrived. A host that also
        // flies the message's subject still applies it below.
        if (_relays.TryGetValue(type, out var relay))
        {
            relay(peer, channel, payload);
        }

        if (!_handlers.TryGetValue(type, out var handler))
        {
            DroppedUnknown++;
            return;
        }

        handler(peer, payload);
    }

    // NetSeats.Validate's numbering and host-at-seat-0 rules, asked of the wire's entries rather
    // than thrown.
    private static bool WellFormed(IReadOnlyList<NetSeatEntry> seats)
    {
        if (seats.Count == 0 || seats.Count > NetSeats.MaxPlayers)
        {
            return false;
        }

        Span<bool> seen = stackalloc bool[NetSeats.MaxPlayers];
        foreach (var entry in seats)
        {
            if (entry.Seat >= seats.Count || seen[entry.Seat] || (entry.Seat == 0 && !entry.IsHost))
            {
                return false;
            }

            seen[entry.Seat] = true;
        }

        return true;
    }

    private void Forward(int peer, NetMessageType type, int channel, ReadOnlySpan<byte> payload)
    {
        _transport.Send(peer, payload, NetMessage.ReliabilityOf(type), channel);
        Relayed++;
    }

    private void RequireHostRelay()
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("only the host relays; a guest talks to the host alone");
        }
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

    // ⚠ Refuse a seat past the tables and a roster that is not numbered 0 upward without a gap.
    // Either would index every seat-wide table past its end once the guest builds its field.
    private void TakeHandshake(int peer, HandshakeMessage message)
    {
        if (message.Seat != NetMessage.NoSeat && message.Seat >= NetSeats.SeatCapacity)
        {
            Malformed++;
            return;
        }

        _handshake = new NetHandshake(message.Seed, message.HostClock);
        LocalSeat = message.Seat;
        RebuildSeats(peer);
    }

    private void TakeRoster(int peer, SeatRosterMessage message)
    {
        if (!WellFormed(message.Seats))
        {
            Malformed++;
            return;
        }

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
        _hostPeer = from;
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

    private bool HoldsLocalSeat()
    {
        foreach (var seat in _seats)
        {
            if (seat.IsLocal)
            {
                return true;
            }
        }

        return false;
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
