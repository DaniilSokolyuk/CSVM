using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CSVM.Utils;

/// <summary>
/// The IPv4 networks this machine sits on, read from the operating system for the LAN search.
/// It lives outside <c>CSVM.Net</c> because that namespace may not name <c>System.Net</c>, and
/// Godot's interface list carries no masks.
/// </summary>
public static class LocalNetworks
{
    /// <summary>Each IPv4 address and mask on an adapter that is up and not the loopback. Empty
    /// when the system will not say, so a search still asks at the limited broadcast.</summary>
    public static IReadOnlyList<(string Address, string Mask)> Ipv4()
    {
        var found = new List<(string Address, string Mask)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask != null)
                    {
                        found.Add((unicast.Address.ToString(), unicast.IPv4Mask.ToString()));
                    }
                }
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            Log.Info("core", $"lan search: adapters unreadable, limited broadcast only: {e.Message}");
        }

        return found;
    }
}
