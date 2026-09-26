using System;
using System.Buffers.Binary;

namespace CSVM.Net;

/// <summary>A datagram socket the LAN search and its responder speak through. The shipped one is
/// <see cref="LanDiscoverySocket"/>, and a suite passes the in-process <see cref="LoopbackLan"/>.
/// Nothing here names an engine type.</summary>
public interface ILanSocket : IDisposable
{
    /// <summary>Sends one datagram to <paramref name="address"/> on <paramref name="port"/>. A
    /// datagram that cannot go is dropped, as the network itself may drop one.</summary>
    void Send(string address, int port, ReadOnlySpan<byte> datagram);

    /// <summary>The next datagram waiting, with where it came from, or null when none is.</summary>
    byte[]? Receive(out string address, out int port);
}

/// <summary>One open door a LAN search heard: where it answered from and the game port a join
/// aims at. It also carries the advert it answered with and the host build's version.</summary>
public readonly record struct LanGame(
    string Address, int Port, SessionAdvertMessage Advert, NetBuildVersion Version = default);

/// <summary>
/// The LAN discovery wire, engine-free. A query and a reply share a 12-byte header: the magic
/// "CSLD", a version, a kind, two reserved bytes and the asker's token. A reply adds the game
/// port, two reserved bytes, the host build's <see cref="NetBuildVersion"/> and the host's
/// <see cref="SessionAdvertMessage"/>. Both are exactly <see cref="Size"/> bytes, laid out in
/// <c>docs/org/multiplayer-messages.md</c>.
/// ⚠ The query is padded to the reply's size. A reply larger than the query that asked for it
/// makes the responder a reflection amplifier for anyone who forges a source address.
/// </summary>
public static class LanDiscovery
{
    /// <summary>The port every responder listens on. The game port plus one, unregistered.</summary>
    public const int Port = 47501;

    /// <summary>The wire version this build speaks. A datagram of any other is not answered.
    /// ⚠ Do not raise it for a new build version. A build of another minor is listed and marked
    /// only while both builds read this layout.</summary>
    public const byte Version = 2;

    /// <summary>The width of a query and of a reply alike.</summary>
    public const int Size = AdvertAt + SessionAdvertMessage.Size;

    private const int BuildAt = HeaderSize + 4;
    private const int AdvertAt = BuildAt + NetBuildVersion.WireBytes;

    private const int HeaderSize = 12;
    private const byte QueryKind = 1;
    private const byte ReplyKind = 2;
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'L', (byte)'D' };

    /// <summary>Writes a query carrying <paramref name="token"/>, zero-padded to
    /// <see cref="Size"/>, and returns its length.</summary>
    public static int WriteQuery(Span<byte> into, uint token)
    {
        Header(into, QueryKind, token);
        into[HeaderSize..Size].Clear();
        return Size;
    }

    /// <summary>Reads a query's token. False for anything that is not a whole query of this
    /// version, so a stray or truncated datagram is never answered.</summary>
    public static bool TryReadQuery(ReadOnlySpan<byte> from, out uint token) =>
        TryHeader(from, QueryKind, out token);

    /// <summary>Writes the answer to <paramref name="token"/>: the game port a join aims at, the
    /// host build's <paramref name="version"/> and the host's advert. Returns its length, which is
    /// <see cref="Size"/>.</summary>
    public static int WriteReply(
        Span<byte> into, uint token, int gamePort, SessionAdvertMessage advert, NetBuildVersion version)
    {
        Header(into, ReplyKind, token);
        BinaryPrimitives.WriteUInt16LittleEndian(into[HeaderSize..], (ushort)gamePort);
        into.Slice(HeaderSize + 2, 2).Clear();
        version.Write(into[BuildAt..]);
        advert.Write(into[AdvertAt..]);
        return Size;
    }

    /// <summary>Reads a reply to <paramref name="token"/>. False for a truncated or foreign
    /// datagram, for an answer to another search, and for a port, version or advert that does not
    /// read.</summary>
    public static bool TryReadReply(
        ReadOnlySpan<byte> from, uint token, out int gamePort, out SessionAdvertMessage advert, out NetBuildVersion version)
    {
        gamePort = 0;
        advert = default;
        version = default;
        if (!TryHeader(from, ReplyKind, out uint answered) || answered != token)
        {
            return false;
        }

        gamePort = BinaryPrimitives.ReadUInt16LittleEndian(from[HeaderSize..]);
        return gamePort != 0
            && NetBuildVersion.TryRead(from[BuildAt..AdvertAt], out version)
            && SessionAdvertMessage.TryRead(from[AdvertAt..Size], out advert);
    }

    private static void Header(Span<byte> into, byte kind, uint token)
    {
        if (into.Length < Size)
        {
            throw new ArgumentException($"a discovery datagram needs {Size} bytes", nameof(into));
        }

        Magic.CopyTo(into);
        into[4] = Version;
        into[5] = kind;
        into[6] = 0;
        into[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], token);
    }

    private static bool TryHeader(ReadOnlySpan<byte> from, byte kind, out uint token)
    {
        token = 0;
        if (from.Length != Size || !from[..4].SequenceEqual(Magic) || from[4] != Version || from[5] != kind)
        {
            return false;
        }

        token = BinaryPrimitives.ReadUInt32LittleEndian(from[8..]);
        return true;
    }
}
