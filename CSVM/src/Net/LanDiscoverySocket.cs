using System;
using Godot;

namespace CSVM.Net;

/// <summary>
/// The shipped LAN discovery socket: <see cref="ILanSocket"/> over Godot's UDP peer, with
/// broadcast sends allowed. The responder binds <see cref="LanDiscovery.Port"/> and a search binds
/// a free port; both are polled from the menu frame, never from a thread.
/// ⚠ Besides <see cref="EnetTransport"/>, this is the only type under <c>CSVM/</c> that may name a
/// Godot networking type; <c>CSVM.Tests/NetNamespaceDependencyTests.cs</c> asserts that.
/// </summary>
public sealed class LanDiscoverySocket : ILanSocket
{
    private readonly PacketPeerUdp _udp;
    private bool _closed;

    private LanDiscoverySocket(PacketPeerUdp udp)
    {
        _udp = udp;
    }

    /// <summary>Binds <paramref name="port"/> on <paramref name="bindAddress"/>, where port 0 takes
    /// a free one. A suite binds the loopback: a wildcard bind is what raises a firewall dialog.
    /// Throws when the port is taken or the address is bad.</summary>
    public static LanDiscoverySocket Bind(int port, string bindAddress)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "a port is 0 to 65535");
        }

        var udp = new PacketPeerUdp();
        udp.SetBroadcastEnabled(true);
        var error = udp.Bind(port, bindAddress);
        if (error != Error.Ok)
        {
            udp.Dispose();
            throw new InvalidOperationException($"cannot listen for LAN games on {bindAddress}:{port}: {error}");
        }

        return new LanDiscoverySocket(udp);
    }

    /// <inheritdoc/>
    public void Send(string address, int port, ReadOnlySpan<byte> datagram)
    {
        if (_closed || _udp.SetDestAddress(address, port) != Error.Ok)
        {
            return;
        }

        _ = _udp.PutPacket(datagram);
    }

    /// <inheritdoc/>
    public byte[]? Receive(out string address, out int port)
    {
        address = "";
        port = 0;
        if (_closed || _udp.GetAvailablePacketCount() == 0)
        {
            return null;
        }

        // ⚠ Take the packet before asking where it came from. The UDP peer answers about the one
        // last taken, the opposite of the ENet peer's order.
        byte[] datagram = _udp.GetPacket();
        address = _udp.GetPacketIP();
        port = _udp.GetPacketPort();
        return datagram;
    }

    /// <summary>Closes the socket.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _udp.Close();
        _udp.Dispose();
    }
}
