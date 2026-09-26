using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// A carrier's first listener, standing between a socket and the session that later binds it.
/// A carrier binds once and the menu reads the advert before any session exists, so this binds it
/// at once as the session's transport. Its <see cref="Advertise"/> reaches every peer on connect and on
/// each change, and a lobby message that arrives is kept here and never passed on. Any other payload
/// is held until a listener binds, then replayed behind the roster announcement. A bound session
/// sees only the peers present when it bound, and <see cref="Unbind"/> frees the carrier for the
/// next. A peer whose build version does not play with this one leaves every peer list, and only its
/// close notice is read.
/// </summary>
public sealed class NetLobby : INetTransport, INetTransportListener, IDisposable
{
    /// <summary>How many payloads are held for a session that has not bound yet. The socket's own
    /// depth, for the same reason: deep enough for the join answer and the openers behind it.
    /// </summary>
    public const int HeldPayloads = 64;

    private readonly INetTransport _inner;
    private readonly NetBuildVersion _version;
    private readonly Dictionary<int, NetBuildVersion> _heard = new();
    private readonly List<int> _clashing = new();
    private readonly List<(int Peer, int Channel, byte[] Bytes)> _held = new();
    private readonly List<int> _bound = new();
    private readonly Dictionary<int, CoopPickMessage> _picks = new();
    private readonly Dictionary<int, CoopFit> _seatFits = new();
    private readonly List<int> _unpicked = new();

    private readonly List<(int Peer, LobbyChatMessage Line)> _chat = new();

    // Wide enough for the widest lobby message, the Dogfight player list.
    private readonly byte[] _scratch = new byte[DogfightRosterMessage.Size];
    private INetTransportListener? _listener;
    private SessionAdvertMessage? _advertising;

    // The co-op flow a guest's session bound under, and whether the host has since opened another
    // flight. ⚠ Past that point nothing reaches or leaves the bound session. The host's next
    // opener is held for the next bind, and the old flight says nothing into the new one.
    private CoopFlowMessage? _boundFlow;
    private bool _flightOver;

    /// <summary>A lobby over <paramref name="inner"/>, which it binds at once. It names this build
    /// to every peer as <paramref name="version"/>.</summary>
    public NetLobby(INetTransport inner, NetBuildVersion version = default)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _version = version;
        _inner.Bind(this);
    }

    /// <summary>The carrier under this lobby, for the link readout a real socket offers.</summary>
    public INetTransport Inner => _inner;

    /// <summary>The build version this end names to its peers.</summary>
    public NetBuildVersion Version => _version;

    /// <summary>The connected peers whose named version does not play with this one, in the order
    /// they named it. None of them is on any other peer list.</summary>
    public IReadOnlyList<int> Clashing => _clashing;

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

    /// <summary>Whether the co-op host opened another flight while a session was bound here. That
    /// session then hears nothing more, and what arrives is held for the next bind.</summary>
    public bool FlightOver => _flightOver;

    /// <inheritdoc/>
    public int LocalPeer => _inner.LocalPeer;

    /// <summary>The peers a bound session flies with: those present when it bound and still
    /// connected. Every connected peer while nothing is bound. A peer arriving mid-flight waits
    /// here with the advert, since no guest joins a mission in flight.</summary>
    public IReadOnlyList<int> Peers => _listener != null ? _bound : AllPeers;

    /// <summary>Every connected peer, a bound session's or not: the count a host's advert reads. A
    /// peer on <see cref="Clashing"/> is left out.</summary>
    public IReadOnlyList<int> AllPeers
    {
        get
        {
            var peers = _inner.Peers;
            if (_clashing.Count == 0)
            {
                return peers;
            }

            var playing = new List<int>(peers.Count);
            for (int i = 0; i < peers.Count; i++)
            {
                if (!_clashing.Contains(peers[i]))
                {
                    playing.Add(peers[i]);
                }
            }

            return playing;
        }
    }

    /// <summary>The version <paramref name="peer"/> named on connect. False while it has named
    /// none.</summary>
    public bool TryVersionOf(int peer, out NetBuildVersion version) => _heard.TryGetValue(peer, out version);

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
        _boundFlow = Flow;
        _flightOver = false;
        _bound.Clear();
        _bound.AddRange(AllPeers);
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
    /// it again. What was held for the old session is dropped with it. What arrived after the host
    /// opened another flight is kept, since it is that flight's opener.</summary>
    public void Unbind()
    {
        _listener = null;
        _bound.Clear();
        if (!_flightOver)
        {
            _held.Clear();
        }

        _flightOver = false;
        _boundFlow = null;
    }

    /// <summary>Unbinds only when <paramref name="listener"/> is the session bound here. A session
    /// being freed calls this, so a carrier a door already took back is left alone.</summary>
    public void Release(INetTransportListener listener)
    {
        if (_listener != null && ReferenceEquals(_listener, listener))
        {
            Unbind();
        }
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
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
    {
        if (!_flightOver)
        {
            _inner.Send(peer, payload, reliability, channel);
        }
    }

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
        // The version goes first, then the advert, so a guest names the session before the join
        // answer lands. Nothing more: a bound session's field was fixed when it bound, so a
        // newcomer never reaches it. An unbound lobby announces every peer when a session binds.
        Tell(peer, new BuildVersionMessage(_version));
        SendAdvert(peer);
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(int peer)
    {
        _heard.Remove(peer);
        _clashing.Remove(peer);
        _held.RemoveAll(held => held.Peer == peer);
        _picks.Remove(peer);
        _unpicked.Remove(peer);
        if (_listener != null && _bound.Remove(peer))
        {
            _listener.OnPeerDisconnected(peer);
        }
    }

    /// <summary>A co-op host's restart: every guest's flight payloads are dropped until that guest
    /// picks under the new round. What its old session sent meanwhile never reaches the new one.
    /// </summary>
    public void AwaitPicks()
    {
        _unpicked.Clear();
        _unpicked.AddRange(AllPeers);
        _held.Clear();
    }

    /// <inheritdoc/>
    public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload)
    {
        if (BuildVersionMessage.TryRead(payload, out var named))
        {
            Heard(peer, named.Version);
            return;
        }

        // A refused guest reads why it was refused, so a close notice is taken from any peer.
        if (SessionClosedMessage.TryRead(payload, out var closed))
        {
            Closed = closed;
            return;
        }

        if (_clashing.Contains(peer))
        {
            return;
        }

        if (SessionAdvertMessage.TryRead(payload, out var advert))
        {
            Advert = advert;
            return;
        }

        if (CoopFlowMessage.TryRead(payload, out var flow))
        {
            Flow = flow;
            Flows++;
            // A flight under a new round is the host's restart. The ending of a flight names a
            // board instead, and a bound session still hears that flight's tail.
            _flightOver |= _listener != null && _boundFlow is { Screen: NetCoopScreen.InMission } under
                && flow.Screen == NetCoopScreen.InMission && flow.Epoch != under.Epoch;
            return;
        }

        if (CoopPickMessage.TryRead(payload, out var pick))
        {
            // A guest picks only on a board, so whatever it sent before is a flight's that ended.
            _picks[peer] = pick;
            _held.RemoveAll(held => held.Peer == peer);
            _unpicked.Remove(peer);
            return;
        }

        if (_unpicked.Contains(peer))
        {
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

        if (_listener != null && !_flightOver)
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

    /// <summary>Tells <paramref name="peer"/> why it is being sent away, with this end's version and
    /// the one the peer named. The caller still hangs up; the notice only lets the guest's board
    /// name the reason.</summary>
    public void Farewell(int peer, NetCloseReason reason)
    {
        _heard.TryGetValue(peer, out var theirs);
        Tell(peer, new SessionClosedMessage(reason, _version, theirs));
    }

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

    // A peer that clashes leaves the peer lists at once, and the bound session with it. What it
    // sent before is dropped, since none of it was meant for a build of this version.
    private void Heard(int peer, NetBuildVersion version)
    {
        _heard[peer] = version;
        if (version.PlaysWith(_version) || _clashing.Contains(peer))
        {
            return;
        }

        _clashing.Add(peer);
        _held.RemoveAll(held => held.Peer == peer);
        _picks.Remove(peer);
        if (_listener != null && _bound.Remove(peer))
        {
            _listener.OnPeerDisconnected(peer);
        }
    }

    private void SendAdvert(int peer)
    {
        if (_advertising is { } advert)
        {
            Tell(peer, advert);
        }
    }
}
