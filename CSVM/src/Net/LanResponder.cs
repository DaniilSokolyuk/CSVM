using System;

namespace CSVM.Net;

/// <summary>
/// An open door's answer to a LAN search. It listens on <see cref="LanDiscovery.Port"/> only while
/// a door is open. Each well-formed query gets the door's current advert and game port, sent back
/// to the address the query came from. Engine-free: the socket is handed in.
/// </summary>
public sealed class LanResponder : IDisposable
{
    /// <summary>The most queries one poll answers. A flood of forged queries costs a bounded
    /// amount of work per menu frame, and the rest wait or are dropped by the socket.</summary>
    public const int QueriesPerPoll = 16;

    private readonly ILanSocket _socket;
    private readonly NetBuildVersion _version;
    private readonly byte[] _reply = new byte[LanDiscovery.Size];

    /// <summary>A responder over <paramref name="socket"/>, which it owns from here on. Each answer
    /// names this build as <paramref name="version"/>.</summary>
    public LanResponder(ILanSocket socket, NetBuildVersion version = default)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _version = version;
    }

    /// <summary>How many queries this responder has answered.</summary>
    public int Answered { get; private set; }

    /// <summary>Answers the queries waiting with <paramref name="advert"/> and
    /// <paramref name="gamePort"/>. A datagram that is not a query is read and dropped.</summary>
    public void Poll(SessionAdvertMessage advert, int gamePort)
    {
        for (int i = 0; i < QueriesPerPoll; i++)
        {
            byte[]? datagram = _socket.Receive(out string address, out int port);
            if (datagram == null)
            {
                return;
            }

            if (!LanDiscovery.TryReadQuery(datagram, out uint token))
            {
                continue;
            }

            int length = LanDiscovery.WriteReply(_reply, token, gamePort, advert, _version);
            _socket.Send(address, port, _reply.AsSpan(0, length));
            Answered++;
        }
    }

    /// <summary>Closes the socket, so nothing listens once the door is shut.</summary>
    public void Dispose() => _socket.Dispose();
}
