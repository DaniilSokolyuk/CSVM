using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Net;

/// <summary>Where one end's link stands. Named here rather than taken from the engine, so a
/// board can show it without learning what carries it.</summary>
public enum EnetLinkState
{
    /// <summary>A join whose handshake has not finished. Nothing can be sent yet.</summary>
    Connecting,

    /// <summary>Open, and sending works.</summary>
    Up,

    /// <summary>Closed, refused, or given up on. Nothing will arrive again.</summary>
    Down,
}

/// <summary>
/// The shipped carrier: <see cref="INetTransport"/> over Godot's ENet peer, which is UDP with
/// ENet's own three delivery classes. Every roster change and every payload is reported from
/// inside <see cref="Step"/>, the one poll this makes. A session therefore sees the loopback's own
/// rule: nothing arrives between steps. A host is peer 1 and a guest takes the id ENet assigns it,
/// which is the id every peer addresses it by.
/// ⚠ This is the only type under <c>CSVM/</c> that may name a Godot networking type;
/// <c>CSVM.Tests/NetNamespaceDependencyTests.cs</c> asserts that over compiled metadata.
/// </summary>
public sealed class EnetTransport : INetTransport, IDisposable
{
    /// <summary>The highest channel a caller may send on. ENet fixes a connection's channel count
    /// during its handshake and keeps some channels for itself. Both ends ask for this many, and a
    /// send past it is a programming error rather than a dropped payload.</summary>
    public const int MaxChannel = 8;

    private readonly ENetMultiplayerPeer _peer;
    private readonly List<int> _peers = new();
    private readonly int _local;
    private INetTransportListener? _listener;
    private bool _closed;

    private EnetTransport(ENetMultiplayerPeer peer)
    {
        _peer = peer;
        _local = peer.GetUniqueId();
        peer.PeerConnected += OnEnetPeerConnected;
        peer.PeerDisconnected += OnEnetPeerDisconnected;
    }

    /// <inheritdoc/>
    public int LocalPeer => _local;

    /// <inheritdoc/>
    public IReadOnlyList<int> Peers => _peers;

    /// <summary>Where this end's link stands. A guest opens on <see cref="EnetLinkState.Connecting"/>
    /// and reaches <see cref="EnetLinkState.Up"/> on the step that announces the host. A link that
    /// fails or is hung up on reads <see cref="EnetLinkState.Down"/> and stays there.</summary>
    public EnetLinkState LinkState => _closed
        ? EnetLinkState.Down
        : _peer.GetConnectionStatus() switch
        {
            MultiplayerPeer.ConnectionStatus.Connected => EnetLinkState.Up,
            MultiplayerPeer.ConnectionStatus.Connecting => EnetLinkState.Connecting,
            _ => EnetLinkState.Down,
        };

    /// <summary>Opens a listen server on <paramref name="port"/> for up to
    /// <paramref name="maxPeers"/> guests. <paramref name="bindAddress"/> is every interface by
    /// default. A suite passes the loopback address instead, so a run never asks the firewall for
    /// anything. Throws when the socket cannot be opened, which is a taken port or a bad
    /// address.</summary>
    public static EnetTransport Host(int port, int maxPeers, string bindAddress = "*")
    {
        RequirePort(port);
        if (maxPeers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPeers), maxPeers, "a host admits at least one guest");
        }

        var peer = new ENetMultiplayerPeer();
        peer.SetBindIP(bindAddress);
        var error = peer.CreateServer(port, maxPeers, MaxChannel);
        if (error != Error.Ok)
        {
            peer.Dispose();
            throw new InvalidOperationException($"cannot host on {bindAddress}:{port}: {error}");
        }

        return new EnetTransport(peer);
    }

    /// <summary>Starts a join to <paramref name="address"/> on <paramref name="port"/>. The
    /// returned transport is usable at once but not yet connected. The host arrives as an
    /// <see cref="INetTransportListener.OnPeerConnected"/> for peer 1 on a later
    /// <see cref="Step"/>, and a join that fails ends at <see cref="EnetLinkState.Down"/>.</summary>
    public static EnetTransport Join(string address, int port)
    {
        RequirePort(port);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("a join needs an address", nameof(address));
        }

        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, port, MaxChannel);
        if (error != Error.Ok)
        {
            peer.Dispose();
            throw new InvalidOperationException($"cannot join {address}:{port}: {error}");
        }

        return new EnetTransport(peer);
    }

    /// <inheritdoc/>
    public void Bind(INetTransportListener listener)
    {
        if (_listener != null)
        {
            throw new InvalidOperationException($"peer {_local} already has a listener bound");
        }

        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        foreach (int peer in _peers.ToArray())
        {
            listener.OnPeerConnected(peer);
        }
    }

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
    {
        if (channel is < 0 or > MaxChannel)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"a channel is 0 to {MaxChannel}");
        }

        if (LinkState != EnetLinkState.Up || !_peers.Contains(peer))
        {
            return;
        }

        _peer.SetTargetPeer(peer);
        _peer.TransferMode = ModeFor(reliability);
        _peer.TransferChannel = channel;
        var error = _peer.PutPacket(payload);
        if (error != Error.Ok)
        {
            Log.Warn("core", $"net send refused peer={peer} channel={channel} bytes={payload.Length} error={error}");
        }
    }

    /// <summary>Hangs up on <paramref name="peer"/>. ENet asks the other end and waits for its
    /// answer, so the roster change lands on a later <see cref="Step"/> rather than inside this
    /// call. A peer that has already gone costs a timeout first.</summary>
    public void Disconnect(int peer)
    {
        if (LinkState == EnetLinkState.Down || !_peers.Contains(peer))
        {
            return;
        }

        _peer.DisconnectPeer(peer);
    }

    /// <summary>Polls ENet once and reports everything it produced: the joins, the departures and
    /// every arrived payload, in that order. Its <paramref name="dt"/> is checked and then unused,
    /// because ENet times its retransmissions off the wall clock rather than off a caller's
    /// step.</summary>
    public void Step(double dt)
    {
        if (dt < 0.0 || double.IsNaN(dt))
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "a transport does not step backwards");
        }

        if (_closed || _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
        {
            return;
        }

        _peer.Poll();
        while (_peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected
               && _peer.GetAvailablePacketCount() > 0)
        {
            // ⚠ Read the source and the channel before taking the packet. Both answer about the
            // one still at the head of the queue, which taking it pops.
            int from = _peer.GetPacketPeer();
            int channel = _peer.GetPacketChannel();
            byte[] payload = _peer.GetPacket();
            _listener?.OnPayload(from, channel, payload);
        }
    }

    /// <summary>Drops the whole link and releases the socket. The departures are not reported to
    /// the listener, because this end is the one leaving. A peer that wants the roster emptied
    /// hangs up first and steps until it is.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _peer.PeerConnected -= OnEnetPeerConnected;
        _peer.PeerDisconnected -= OnEnetPeerDisconnected;
        _peer.Close();
        _peer.Dispose();
        _peers.Clear();
    }

    /// <inheritdoc cref="Close"/>
    public void Dispose() => Close();

    /// <summary>Which ENet delivery carries a reliability class. An unsequenced packet carries
    /// <see cref="NetReliability.Unreliable"/>, and a reliable ordered one carries
    /// <see cref="NetReliability.Reliable"/>. ENet's sequenced unreliable delivery carries
    /// <see cref="NetReliability.UnreliableSequenced"/>, and itself discards a payload older than
    /// the newest on its channel.</summary>
    internal static MultiplayerPeer.TransferModeEnum ModeFor(NetReliability reliability) => reliability switch
    {
        NetReliability.Unreliable => MultiplayerPeer.TransferModeEnum.Unreliable,
        NetReliability.UnreliableSequenced => MultiplayerPeer.TransferModeEnum.UnreliableOrdered,
        NetReliability.Reliable => MultiplayerPeer.TransferModeEnum.Reliable,
        _ => throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "not a reliability class"),
    };

    private static void RequirePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "a port is 1 to 65535");
        }
    }

    private void OnEnetPeerConnected(long id)
    {
        int peer = (int)id;
        if (_peers.Contains(peer))
        {
            return;
        }

        _peers.Add(peer);
        _listener?.OnPeerConnected(peer);
    }

    private void OnEnetPeerDisconnected(long id)
    {
        int peer = (int)id;
        if (!_peers.Remove(peer))
        {
            return;
        }

        _listener?.OnPeerDisconnected(peer);
    }
}
