using System;

namespace CSVM.Net;

/// <summary>
/// Which carrier a match runs over, chosen once. The menu door the launcher registers and the
/// command line's own open both come through here. A build therefore changes carrier with no edit
/// above the seam. The switch is <see cref="SteamTransport.SteamBuild"/>: without it a match runs
/// over ENet by direct IP, with it over the Steam carrier.
/// ⚠ Nothing above the seam may branch on <see cref="UsesSteam"/>. A carrier that will not open
/// throws, and a board shows that the way it shows a taken port.
/// </summary>
public static class NetCarrier
{
    private static readonly Func<int, UpnpPortMapResult> MapPort = port => UpnpPortMap.Map(port);
    private static readonly Action<int> UnmapPort = port => UpnpPortMap.Unmap(port);

    /// <summary>Whether this build selects the Steam carrier.</summary>
    public static bool UsesSteam => SteamTransport.SteamBuild;

    /// <summary>The selected carrier as one word, for a log line and a board.</summary>
    public static string Name => UsesSteam ? "steam" : "enet";

    /// <summary>The router door a direct-IP host asks for, or null for a carrier that is reachable
    /// without one. A door handed null simply shows no mapping.</summary>
    public static Func<int, UpnpPortMapResult>? PortMap => UsesSteam ? null : MapPort;

    /// <summary>The way that mapping comes back down, or null when none was asked for.</summary>
    public static Action<int>? PortUnmap => UsesSteam ? null : UnmapPort;

    /// <summary>Opens a listen server for <paramref name="maxGuests"/> guests on
    /// <paramref name="port"/> over the selected carrier. <paramref name="bindAddress"/> is the
    /// direct-IP interface. Throws what that carrier throws when the socket will not open.</summary>
    public static INetTransport Host(int port, int maxGuests, string bindAddress = "*") => UsesSteam
        ? SteamTransport.Host(port, maxGuests, bindAddress)
        : EnetTransport.Host(port, maxGuests, bindAddress);

    /// <summary>Starts a join to <paramref name="address"/> on <paramref name="port"/> over the
    /// selected carrier. Over Steam the address names a lobby or an identity.</summary>
    public static INetTransport Join(string address, int port) => UsesSteam
        ? SteamTransport.Join(address, port)
        : EnetTransport.Join(address, port);
}
