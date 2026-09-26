using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

/// <summary>A carrier that can say where its link stands. The seam itself has no word for that,
/// and a join board shows it. Only a real socket implements this; a board asks for it and falls
/// back to the peer roster when a carrier has none.</summary>
public interface INetLink
{
    /// <summary>Where this end's link stands right now.</summary>
    EnetLinkState LinkState { get; }

    /// <summary>Payloads taken off the socket while no listener was bound; see
    /// <see cref="INetTransport.Bind"/>, which replays them.</summary>
    int PendingPayloads { get; }
}

/// <summary>
/// The shipped carrier: <see cref="INetTransport"/> over Godot's ENet peer, which is UDP with
/// ENet's own three delivery classes. Every roster change and every payload is reported from
/// inside <see cref="Step"/>. A session therefore sees the loopback's own rule: nothing arrives
/// between steps. A host is peer 1 and a guest takes the id ENet assigns it, which is the id
/// every peer addresses it by. A service thread polls ENet while the main thread does not step,
/// so a blocking load does not read as a dead link (docs/architecture/Net.md).
/// ⚠ This is the only type under <c>CSVM/</c> that may name a Godot networking type;
/// <c>CSVM.Tests/NetNamespaceDependencyTests.cs</c> asserts that over compiled metadata.
/// </summary>
public sealed class EnetTransport : INetTransport, INetLink, IDisposable
{
    /// <summary>How many channels a connection carries, 0 to one below this. ENet fixes the count
    /// during its handshake, so both ends ask for the same number. It is the reliable events
    /// channel and a state and a fire channel per seat, as <see cref="NetChannels"/> lays them
    /// out. A send past the count is a programming error rather than a dropped payload.</summary>
    public const int ChannelCount = NetChannels.Count;

    /// <summary>How many payloads are held for a listener that has not bound yet. Deep enough for
    /// a join answer and the openers behind it, shallow enough that a carrier nobody ever binds
    /// cannot grow without bound. Past it the oldest held payload is dropped and logged.</summary>
    public const int HeldPayloads = 64;

    /// <summary>How long the main thread may go without a step before the service thread polls in
    /// its place. Several frames at any playable rate, so a stepping end is never polled twice.
    /// </summary>
    public const double ServiceGapSeconds = 0.1;

    /// <summary>How often the service thread looks, in milliseconds. Far inside ENet's timeout
    /// floor, so the acknowledgements it sends keep a stalled end alive.</summary>
    public const int ServiceIntervalMs = 10;

    // ENet's own throttle interval and acceleration. The deceleration beside them is zero.
    // ⚠ Do not let ENet thin unreliable sends. A load's late acknowledgements read as a round-trip
    // jump, and the state channel then drops samples after every load.
    private const int ThrottleIntervalMs = 5000;
    private const int ThrottleAcceleration = 2;

    // A stall at least this long is logged once the step resumes. A player's log then shows which
    // loads the service thread carried.
    private const double StallLogSeconds = 1.0;

    // ⚠ Every touch of _peer happens under this gate. The service thread polls it, and Godot's
    // ENet peer is not safe to use from two threads at once.
    private readonly object _gate = new();
    private readonly ENetMultiplayerPeer _peer;
    private readonly List<int> _peers = new();
    private readonly List<(int Peer, bool Joined)> _roster = new();
    private readonly List<(int Peer, int Channel, byte[] Bytes)> _held = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Keepalive _keepalive;
    private readonly int _local;
    private INetTransportListener? _listener;
    private bool _closed;
    private bool _frozen;
    private double _steppedAt;
    private int _servicePolls;

    private EnetTransport(ENetMultiplayerPeer peer, Keepalive keepalive)
    {
        _peer = peer;
        _keepalive = keepalive;
        _local = peer.GetUniqueId();
        peer.PeerConnected += OnEnetPeerConnected;
        peer.PeerDisconnected += OnEnetPeerDisconnected;
        var service = new Thread(Serve) { IsBackground = true, Name = "enet-service" };
        service.Start();
    }

    /// <inheritdoc/>
    public int LocalPeer => _local;

    /// <inheritdoc/>
    public IReadOnlyList<int> Peers => _peers;

    /// <inheritdoc/>
    /// <remarks>A guest opens on <see cref="EnetLinkState.Connecting"/> and reaches
    /// <see cref="EnetLinkState.Up"/> on the step that announces the host. A link that fails or is
    /// hung up on reads <see cref="EnetLinkState.Down"/> and stays there.</remarks>
    public EnetLinkState LinkState
    {
        get
        {
            lock (_gate)
            {
                return _closed
                    ? EnetLinkState.Down
                    : _peer.GetConnectionStatus() switch
                    {
                        MultiplayerPeer.ConnectionStatus.Connected => EnetLinkState.Up,
                        MultiplayerPeer.ConnectionStatus.Connecting => EnetLinkState.Connecting,
                        _ => EnetLinkState.Down,
                    };
            }
        }
    }

    /// <inheritdoc/>
    public int PendingPayloads => _held.Count;

    /// <summary>Opens a listen server on <paramref name="port"/> for up to
    /// <paramref name="maxPeers"/> guests. <paramref name="bindAddress"/> is every interface by
    /// default. A suite passes the loopback address instead, so a run never asks the firewall for
    /// anything. Throws when the socket cannot be opened, which is a taken port or a bad
    /// address. <paramref name="keepalive"/> is <see cref="Keepalive.Shipped"/> unless a suite
    /// shortens it.</summary>
    public static EnetTransport Host(int port, int maxPeers, string bindAddress = "*", Keepalive? keepalive = null)
    {
        RequirePort(port);
        if (maxPeers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPeers), maxPeers, "a host admits at least one guest");
        }

        var peer = new ENetMultiplayerPeer();
        peer.SetBindIP(bindAddress);
        var error = peer.CreateServer(port, maxPeers, ChannelCount);
        if (error != Error.Ok)
        {
            peer.Dispose();
            throw new InvalidOperationException($"cannot host on {bindAddress}:{port}: {error}");
        }

        return new EnetTransport(peer, keepalive ?? Keepalive.Shipped);
    }

    /// <summary>Starts a join to <paramref name="address"/> on <paramref name="port"/>. The
    /// returned transport is usable at once but not yet connected. The host arrives as an
    /// <see cref="INetTransportListener.OnPeerConnected"/> for peer 1 on a later
    /// <see cref="Step"/>, and a join that fails ends at <see cref="EnetLinkState.Down"/>.</summary>
    public static EnetTransport Join(string address, int port, Keepalive? keepalive = null)
    {
        RequirePort(port);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("a join needs an address", nameof(address));
        }

        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, port, ChannelCount);
        if (error != Error.Ok)
        {
            peer.Dispose();
            throw new InvalidOperationException($"cannot join {address}:{port}: {error}");
        }

        return new EnetTransport(peer, keepalive ?? Keepalive.Shipped);
    }

    /// <inheritdoc/>
    /// <remarks>The peers this end already has are announced from inside the call, and so is
    /// every payload taken while nothing was bound, in arrival order. ⚠ Do not drop the replay.
    /// A join board steps the socket to show its link. The host's answer can land in that
    /// window, before the session that owns the wire exists.</remarks>
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

        var held = _held.ToArray();
        _held.Clear();
        foreach (var (peer, channel, bytes) in held)
        {
            listener.OnPayload(peer, channel, bytes);
        }
    }

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
    {
        if (channel is < 0 or >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"a channel is 0 to {ChannelCount - 1}");
        }

        Error error;
        lock (_gate)
        {
            if (LinkState != EnetLinkState.Up || !_peers.Contains(peer))
            {
                return;
            }

            _peer.SetTargetPeer(peer);
            _peer.TransferMode = ModeFor(reliability);
            _peer.TransferChannel = channel;
            error = _peer.PutPacket(payload);
        }

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
        lock (_gate)
        {
            if (LinkState == EnetLinkState.Down || !_peers.Contains(peer))
            {
                return;
            }

            _peer.DisconnectPeer(peer);
        }
    }

    /// <summary>Polls ENet once and reports what it and the service thread's polls produced since
    /// the last step. The joins and departures come first, then every arrived payload. Its
    /// <paramref name="dt"/> is checked and then unused, because ENet times its retransmissions
    /// off the wall clock rather than off a caller's step.</summary>
    public void Step(double dt)
    {
        if (dt < 0.0 || double.IsNaN(dt))
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "a transport does not step backwards");
        }

        lock (_gate)
        {
            NoteStep();
            if (_closed)
            {
                return;
            }

            if (_peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected)
            {
                _peer.Poll();
            }

            // Before the payloads, which is the order a single poll reports them in. A departure
            // the service thread heard mid-load is reported here, on the first step after it.
            AnnounceRoster();
            while (!_closed && _peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected
                   && _peer.GetAvailablePacketCount() > 0)
            {
                // ⚠ Read the source and the channel before taking the packet. Both answer about
                // the one still at the head of the queue, which taking it pops.
                int from = _peer.GetPacketPeer();
                int channel = _peer.GetPacketChannel();
                byte[] payload = _peer.GetPacket();
                if (_listener is { } listener)
                {
                    listener.OnPayload(from, channel, payload);
                }
                else
                {
                    Hold(from, channel, payload);
                }
            }
        }
    }

    /// <summary>Drops the whole link and releases the socket. The departures are not reported to
    /// the listener, because this end is the one leaving. A peer that wants the roster emptied
    /// hangs up first and steps until it is. The service thread ends on its next look.</summary>
    public void Close()
    {
        lock (_gate)
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
            _roster.Clear();
            _held.Clear();
        }
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

    /// <summary>Stops the service thread without closing the socket or telling anyone. With the
    /// stepping stopped too, that is a crashed process as the other end sees it. For a suite;
    /// nothing in the game calls it.</summary>
    internal void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    private static void RequirePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "a port is 1 to 65535");
        }
    }

    // A payload nobody is bound to take yet, kept for the listener that binds next. The oldest
    // goes first at the cap: a held payload is a join answer, and the newest is the one still
    // worth having.
    private void Hold(int peer, int channel, byte[] payload)
    {
        if (_held.Count >= HeldPayloads)
        {
            Log.Warn("core", $"net: {HeldPayloads} payloads held with no listener bound, dropping the oldest");
            _held.RemoveAt(0);
        }

        _held.Add((peer, channel, payload));
    }

    // Raised inside a poll, on whichever thread polled, so it only queues. The main thread's step
    // is the one place the roster changes and the listener hears of it.
    private void OnEnetPeerConnected(long id)
    {
        int peer = (int)id;
        var link = _peer.GetPeer(peer);
        link?.SetTimeout(_keepalive.TimeoutLimit, _keepalive.TimeoutMinimumMs, _keepalive.TimeoutMaximumMs);
        link?.ThrottleConfigure(ThrottleIntervalMs, ThrottleAcceleration, 0);
        _roster.Add((peer, true));
    }

    private void OnEnetPeerDisconnected(long id) => _roster.Add(((int)id, false));

    private void AnnounceRoster()
    {
        for (int i = 0; i < _roster.Count && !_closed; i++)
        {
            var (peer, joined) = _roster[i];
            if (joined && !_peers.Contains(peer))
            {
                _peers.Add(peer);
                _listener?.OnPeerConnected(peer);
            }
            else if (!joined && _peers.Remove(peer))
            {
                _listener?.OnPeerDisconnected(peer);
            }
        }

        _roster.Clear();
    }

    private void NoteStep()
    {
        double now = _wall.Elapsed.TotalSeconds;
        double gap = now - _steppedAt;
        _steppedAt = now;
        if (_servicePolls > 0 && gap >= StallLogSeconds)
        {
            Log.Info("core", $"net: the service thread kept the link up through a {gap:0.00} s stall ({_servicePolls} poll(s))");
        }

        _servicePolls = 0;
    }

    // The service thread's whole life. It polls only once the main thread has missed a gap, and
    // gives up past the ceiling, so a hung game still drops its link.
    private void Serve()
    {
        while (true)
        {
            Thread.Sleep(ServiceIntervalMs);
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                double idle = _wall.Elapsed.TotalSeconds - _steppedAt;
                if (!_keepalive.Service || _frozen || idle < ServiceGapSeconds || idle > _keepalive.CeilingSeconds
                    || _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
                {
                    continue;
                }

                _peer.Poll();
                _servicePolls++;
            }
        }
    }

    /// <summary>How a link rides out silence: when ENet gives a peer up, and how long a stall the
    /// service thread covers.</summary>
    /// <param name="TimeoutLimit">ENet's retry limit, a power of two, which arms the floor.</param>
    /// <param name="TimeoutMinimumMs">How old an unacknowledged send may be once the limit is hit.</param>
    /// <param name="TimeoutMaximumMs">How old an unacknowledged send may ever be.</param>
    /// <param name="CeilingSeconds">The longest main-thread stall the service thread covers.</param>
    /// <param name="Service">Whether the service thread polls at all.</param>
    public readonly record struct Keepalive(
        int TimeoutLimit, int TimeoutMinimumMs, int TimeoutMaximumMs, double CeilingSeconds, bool Service = true)
    {
        /// <summary>What a match runs on: Godot's own ENet timeouts, set explicitly on every peer,
        /// and a five-minute ceiling, far past the longest measured mission load.</summary>
        public static Keepalive Shipped => new(32, 5000, 30000, 300.0);
    }
}
