using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// An in-process datagram network for the LAN search, the discovery counterpart of
/// <see cref="LoopbackTransport"/>. Every socket bound on it has an address and a port. A send to
/// the broadcast address reaches every socket on that port. Any other send reaches the one socket
/// at that address and port, and delivery is immediate and lossless. Nothing here opens a real
/// socket, so a suite or an aid raises no firewall dialog.
/// </summary>
public sealed class LoopbackLan
{
    /// <summary>The address a send reaches every socket on its port through.</summary>
    public const string Broadcast = "255.255.255.255";

    // Ephemeral ports start here, as an operating system hands them out above the registered range.
    private const int FirstEphemeral = 49152;

    private readonly List<Socket> _sockets = new();
    private int _nextEphemeral = FirstEphemeral;

    /// <summary>Binds a socket at <paramref name="address"/> on <paramref name="port"/>, where port
    /// 0 takes a free one. Throws when that address and port are taken, as a real bind does.
    /// </summary>
    public ILanSocket Bind(string address, int port)
    {
        string at = string.IsNullOrWhiteSpace(address) || address == "*" ? "127.0.0.1" : address;
        int on = port == 0 ? _nextEphemeral++ : port;
        foreach (var bound in _sockets)
        {
            if (bound.Address == at && bound.Port == on)
            {
                throw new InvalidOperationException($"cannot bind {at}:{on}: in use");
            }
        }

        var socket = new Socket(this, at, on);
        _sockets.Add(socket);
        return socket;
    }

    private void Deliver(Socket from, string address, int port, ReadOnlySpan<byte> datagram)
    {
        foreach (var socket in _sockets)
        {
            if (socket != from && socket.Port == port && (address == Broadcast || socket.Address == address))
            {
                socket.Inbox.Enqueue((datagram.ToArray(), from.Address, from.Port));
            }
        }
    }

    private sealed class Socket : ILanSocket
    {
        private readonly LoopbackLan _lan;

        public Socket(LoopbackLan lan, string address, int port)
        {
            _lan = lan;
            Address = address;
            Port = port;
        }

        public string Address { get; }

        public int Port { get; }

        public Queue<(byte[] Bytes, string Address, int Port)> Inbox { get; } = new();

        public void Send(string address, int port, ReadOnlySpan<byte> datagram) =>
            _lan.Deliver(this, address, port, datagram);

        public byte[]? Receive(out string address, out int port)
        {
            if (Inbox.Count == 0)
            {
                address = "";
                port = 0;
                return null;
            }

            var (bytes, from, fromPort) = Inbox.Dequeue();
            address = from;
            port = fromPort;
            return bytes;
        }

        public void Dispose()
        {
            _lan._sockets.Remove(this);
            Inbox.Clear();
        }
    }
}
