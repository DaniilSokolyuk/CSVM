using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// The Steam carrier's place in the seam, with nothing behind it yet. The Steamworks SDK cannot
/// be committed here under its licence, so no build links one. Every way in therefore throws
/// rather than pretend to open a socket. The define <see cref="NetCarrier"/> selects on is
/// <see cref="SteamBuild"/>, and a build that sets it routes the door, the launch and the session
/// through this type. That is the seam proven with no SDK, no app id and no store page.
/// ⚠ Fill these members when an SDK arrives, and add nothing above the seam that asks which
/// carrier it has.
/// </summary>
public sealed class SteamTransport : INetTransport
{
    /// <summary>Whether this build defines <c>CSVM_STEAM</c>, which the <c>CsvmSteam</c> build
    /// property sets. The one thing the define changes, and the whole of the flag.</summary>
    public const bool SteamBuild =
#if CSVM_STEAM
        true;
#else
        false;
#endif

    private SteamTransport() => throw Unavailable();

    /// <inheritdoc/>
    public int LocalPeer => throw Unavailable();

    /// <inheritdoc/>
    public IReadOnlyList<int> Peers => throw Unavailable();

    /// <summary>Would open a Steam listen server for <paramref name="maxPeers"/> guests.
    /// <paramref name="port"/> and <paramref name="bindAddress"/> are taken so the selection reads
    /// the same for either carrier; the relay has neither. Throws until an SDK is linked.</summary>
    public static SteamTransport Host(int port, int maxPeers, string bindAddress = "*") => throw Unavailable();

    /// <summary>Would join the host named by <paramref name="address"/> on
    /// <paramref name="port"/>, which over Steam is a lobby or an identity rather than an address.
    /// Throws until an SDK is linked.</summary>
    public static SteamTransport Join(string address, int port) => throw Unavailable();

    /// <inheritdoc/>
    public void Bind(INetTransportListener listener) => throw Unavailable();

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
        => throw Unavailable();

    /// <inheritdoc/>
    public void Disconnect(int peer) => throw Unavailable();

    /// <inheritdoc/>
    public void Step(double dt) => throw Unavailable();

    // InvalidOperationException rather than a kind of its own. A door and a launch already catch
    // it from a socket that will not open. An unbuilt carrier therefore reaches a board as a line
    // of text, not a crash. The two arms say which case this build is.
    private static InvalidOperationException Unavailable() => new(SteamBuild
        ? "not built with the Steamworks SDK: CSVM_STEAM is defined but no SDK is linked into this build"
        : "not built with the Steamworks SDK: this build has no CSVM_STEAM define and no Steam carrier");
}
