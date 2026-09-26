using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// A carrier's first listener, standing between a socket and the session that later binds it.
/// A carrier binds once, and the menu reads the host's advert before any session exists. So this
/// binds the carrier at once and is itself the transport the session binds.
/// <see cref="Advertise"/> reaches every peer on connect and on each change. A lobby message that
/// arrives, from the advert to a chat line, is kept here and never passed on. Any other payload is
/// held until a listener binds, then replayed behind the roster announcement. A bound session sees
/// only the peers present when it bound, and <see cref="Unbind"/> frees the carrier for the next.
/// </summary>
public sealed class NetLobby : INetTransport, INetTransportListener, IDisposable
{
    /// <summary>How many payloads are held for a session that has not bound yet. The socket's own
    /// depth, for the same reason: deep enough for the join answer and the openers behind it.
    /// </summary>
    public const int HeldPayloads = 64;

    private readonly INetTransport _inner;
    private readonly List<(int Peer, int Channel, byte[] Bytes)> _held = new();
    private readonly List<int> _bound = new();
    private readonly Dictionary<int, CoopPickMessage> _picks = new();
    private readonly Dictionary<int, CoopFit> _seatFits = new();

    private readonly List<(int Peer, LobbyChatMessage Line)> _chat = new();

    // Wide enough for the widest lobby message, the Dogfight player list.
    private readonly byte[] _scratch = new byte[DogfightRosterMessage.Size];
    private INetTransportListener? _listener;
    private SessionAdvertMessage? _advertising;

    /// <summary>A lobby over <paramref name="inner"/>, which it binds at once.</summary>
    public NetLobby(INetTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.Bind(this);
    }

    /// <summary>The carrier under this lobby, for the link readout a real socket offers.</summary>
    public INetTransport Inner => _inner;

    /// <summary>The last advert a peer sent here, or null while none has arrived.</summary>
    public SessionAdvertMessage? Advert { get; private set; }

    /// <summary>The close notice a host sent here, or null while none has arrived. Kept here like the
    /// advert, so the notice reaches a guest board whether or not a session has bound.</summary>
    public SessionClosedMessage? Closed { get; private set; }

    /// <summary>The co-op host's latest word about its boards, or null while none has arrived.
    /// </summary>
    public CoopFlowMessage? Flow { get; private set; }

    /// <summary>How many co-op flows have arrived, so a board can tell a repeat from news.</summary>
    public int Flows { get; private set; }

    /// <summary>Each connected guest's latest co-op pick, by peer.</summary>
    public IReadOnlyDictionary<int, CoopPickMessage> Picks => _picks;

    /// <summary>Each seat's fit as the co-op host last launched it, by seat. A launch names every
    /// seat again, so an entry from an earlier flight is always overwritten before it is read.
    /// </summary>
    public IReadOnlyDictionary<int, CoopFit> SeatFits => _seatFits;

    /// <summary>The Dogfight host's latest Mission Options, or null while none has arrived.</summary>
    public DogfightOptionsMessage? DogfightOptions { get; private set; }

    /// <summary>The Dogfight host's latest player list, or null while none has arrived.</summary>
    public DogfightRosterMessage? DogfightRoster { get; private set; }

    /// <summary>The advert this end hands out, or null while it hands out none.</summary>
    public SessionAdvertMessage? Advertising => _advertising;

    /// <summary>Payloads waiting for a listener. A guest's first held payload is the host's join
    /// answer, which is how a guest board learns that the host has launched.</summary>
    public int Held => _held.Count;

    /// <summary>Whether a session has bound this lobby.</summary>
    public bool Bound => _listener != null;

    /// <inheritdoc/>
    public int LocalPeer => _inner.LocalPeer;

    /// <summary>The peers a bound session flies with: those present when it bound and still
    /// connected. Every connected peer while nothing is bound. A peer arriving mid-flight waits
    /// here with the advert, since no guest joins a mission in flight.</summary>
    public IReadOnlyList<int> Peers => _listener != null ? _bound : _inner.Peers;

    /// <summary>Every connected peer, a bound session's or not: the count a host's advert reads.
    /// </summary>
    public IReadOnlyList<int> AllPeers => _inner.Peers;

    /// <summary>Hands <paramref name="advert"/> to every peer now and to every peer that connects
    /// later. Sent only when it differs from the last one, so a board may call this every frame.
    /// </summary>
    public void Advertise(SessionAdvertMessage advert)
    {
        if (_advertising == advert)
        {
            return;
        }

        _advertising = advert;
        var peers = _inner.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            SendAdvert(peers[i]);
        }
    }

    /// <inheritdoc/>
    public void Bind(INetTransportListener listener)
    {
        if (_listener != null)
        {
            throw new InvalidOperationException("a lobby hands its carrier to one session at a time");
        }

        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _bound.Clear();
        _bound.AddRange(_inner.Peers);
        foreach (int peer in new List<int>(_bound))
        {
            listener.OnPeerConnected(peer);
        }

        var held = _held.ToArray();
        _held.Clear();
        foreach (var (peer, channel, bytes) in held)
        {
            listener.OnPayload(peer, channel, bytes);
        }
    }

    /// <summary>Takes the carrier back from the session that bound it, so the next flight can bind
    /// it again. What was held for the old session is dropped with it.</summary>
    public void Unbind()
    {
        _listener = null;
        _bound.Clear();
        _held.Clear();
    }

    /// <summary>Hands over every chat line that arrived since the last call, with the peer that
    /// sent it, and forgets them.</summary>
    public IReadOnlyList<(int Peer, LobbyChatMessage Line)> TakeChat()
    {
        if (_chat.Count == 0)
        {
            return Array.Empty<(int, LobbyChatMessage)>();
        }

        var lines = _chat.ToArray();
        _chat.Clear();
        return lines;
    }

    /// <summary>Drops every held payload. A guest does this whenever the host names a board. What
    /// trails in from a flight that ended is not the next flight's join answer.</summary>
    public void DropHeld() => _held.Clear();

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0) =>
        _inner.Send(peer, payload, reliability, channel);

    /// <summary>Sends one lobby message to <paramref name="peer"/>, outside any session.</summary>
    /// <typeparam name="T">The message being sent.</typeparam>
    public void Tell<T>(int peer, in T message)
        where T : struct, INetMessage<T>
    {
        int length = message.Write(_scratch);
        _inner.Send(peer, _scratch.AsSpan(0, length), T.Reliability);
    }

    /// <inheritdoc/>
    public void Disconnect(int peer) => _inner.Disconnect(peer);

    /// <inheritdoc/>
    public void Step(double dt) => _inner.Step(dt);

    /// <inheritdoc/>
    public void OnPeerConnected(int peer)
    {
        // The advert goes first, so a guest names the session before the join answer lands.
        // Nothing more: a bound session's field was fixed when it bound, so a newcomer never
        // reaches it. An unbound lobby announces every peer when a session binds.
        SendAdvert(peer);
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(int peer)
    {
        _held.RemoveAll(held => held.Peer == peer);
        _picks.Remove(peer);
        if (_listener != null && _bound.Remove(peer))
        {
            _listener.OnPeerDisconnected(peer);
        }
    }

    /// <inheritdoc/>
    public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload)
    {
        if (SessionAdvertMessage.TryRead(payload, out var advert))
        {
            Advert = advert;
            return;
        }

        if (SessionClosedMessage.TryRead(payload, out var closed))
        {
            Closed = closed;
            return;
        }

        if (CoopFlowMessage.TryRead(payload, out var flow))
        {
            Flow = flow;
            Flows++;
            return;
        }

        if (CoopPickMessage.TryRead(payload, out var pick))
        {
            // A guest picks only on a board, so whatever it sent before is a flight's that ended.
            _picks[peer] = pick;
            _held.RemoveAll(held => held.Peer == peer);
            return;
        }

        if (CoopSeatFitMessage.TryRead(payload, out var seatFit))
        {
            _seatFits[seatFit.Seat] = seatFit.Fit;
            return;
        }

        if (TakeDogfight(peer, payload))
        {
            return;
        }

        if (_listener != null)
        {
            if (_bound.Contains(peer))
            {
                _listener.OnPayload(peer, channel, payload);
            }

            return;
        }

        // Past the depth the oldest goes. A lobby nobody ever binds must not grow without bound.
        if (_held.Count >= HeldPayloads)
        {
            _held.RemoveAt(0);
        }

        _held.Add((peer, channel, payload.ToArray()));
    }

    /// <summary>Tells <paramref name="peer"/> why it is being sent away. The caller still hangs up;
    /// the notice only lets the guest's board name the reason.</summary>
    public void Farewell(int peer, NetCloseReason reason) => Tell(peer, new SessionClosedMessage(reason));

    /// <summary>Closes the carrier underneath, when it is one that can be closed.</summary>
    public void Dispose()
    {
        _held.Clear();
        (_inner as IDisposable)?.Dispose();
    }

    // The Dogfight lobby's three messages. A chat inbox past the held depth drops its oldest line,
    // so a lobby nobody reads cannot grow without bound.
    private bool TakeDogfight(int peer, ReadOnlySpan<byte> payload)
    {
        if (DogfightOptionsMessage.TryRead(payload, out var options))
        {
            DogfightOptions = options;
            return true;
        }

        if (DogfightRosterMessage.TryRead(payload, out var roster))
        {
            DogfightRoster = roster;
            return true;
        }

        if (!LobbyChatMessage.TryRead(payload, out var line))
        {
            return false;
        }

        if (_chat.Count >= HeldPayloads)
        {
            _chat.RemoveAt(0);
        }

        _chat.Add((peer, line));
        return true;
    }

    private void SendAdvert(int peer)
    {
        if (_advertising is { } advert)
        {
            Tell(peer, advert);
        }
    }
}
