using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// A carrier's first listener, standing between a socket and the session that later binds it.
/// A carrier binds once, and the menu reads the host's advert before any session exists. So this
/// binds the carrier at once and is itself the transport the session binds.
/// <see cref="Advertise"/> reaches every peer on connect and on each change. An arriving advert or
/// close notice is kept here and never passed on. Any other payload is held until a listener
/// binds, then replayed behind the roster announcement, and no engine type is named.
/// </summary>
public sealed class NetLobby : INetTransport, INetTransportListener, IDisposable
{
    /// <summary>How many payloads are held for a session that has not bound yet. The socket's own
    /// depth, for the same reason: deep enough for the join answer and the openers behind it.
    /// </summary>
    public const int HeldPayloads = 64;

    private readonly INetTransport _inner;
    private readonly List<(int Peer, int Channel, byte[] Bytes)> _held = new();
    private readonly byte[] _scratch = new byte[SessionAdvertMessage.Size];
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

    /// <summary>The advert this end hands out, or null while it hands out none.</summary>
    public SessionAdvertMessage? Advertising => _advertising;

    /// <summary>Payloads waiting for a listener. A guest's first held payload is the host's join
    /// answer, which is how a guest board learns that the host has launched.</summary>
    public int Held => _held.Count;

    /// <summary>Whether a session has bound this lobby.</summary>
    public bool Bound => _listener != null;

    /// <inheritdoc/>
    public int LocalPeer => _inner.LocalPeer;

    /// <inheritdoc/>
    public IReadOnlyList<int> Peers => _inner.Peers;

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
            throw new InvalidOperationException("a lobby hands its carrier to one session only");
        }

        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        foreach (int peer in new List<int>(_inner.Peers))
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

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0) =>
        _inner.Send(peer, payload, reliability, channel);

    /// <inheritdoc/>
    public void Disconnect(int peer) => _inner.Disconnect(peer);

    /// <inheritdoc/>
    public void Step(double dt) => _inner.Step(dt);

    /// <inheritdoc/>
    public void OnPeerConnected(int peer)
    {
        // The advert goes first, so a guest names the session before the join answer lands.
        if (_advertising != null)
        {
            SendAdvert(peer);
        }

        _listener?.OnPeerConnected(peer);
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(int peer)
    {
        _held.RemoveAll(held => held.Peer == peer);
        _listener?.OnPeerDisconnected(peer);
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

        if (_listener != null)
        {
            _listener.OnPayload(peer, channel, payload);
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
    public void Farewell(int peer, NetCloseReason reason)
    {
        int length = new SessionClosedMessage(reason).Write(_scratch);
        _inner.Send(peer, _scratch.AsSpan(0, length), SessionClosedMessage.Reliability);
    }

    /// <summary>Closes the carrier underneath, when it is one that can be closed.</summary>
    public void Dispose()
    {
        _held.Clear();
        (_inner as IDisposable)?.Dispose();
    }

    private void SendAdvert(int peer)
    {
        if (_advertising is not { } advert)
        {
            return;
        }

        int length = advert.Write(_scratch);
        _inner.Send(peer, _scratch.AsSpan(0, length), SessionAdvertMessage.Reliability);
    }
}
