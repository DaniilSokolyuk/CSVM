using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CSVM.Utils;

/// <summary>One IPv6 address the machine holds, as the operating system describes it.</summary>
/// <param name="Address">The address as text, without a zone.</param>
/// <param name="Temporary">A privacy-extension address, which rotates and which no router rule
/// names (Windows <c>SuffixOrigin</c> Random, Linux <c>IFA_F_TEMPORARY</c>).</param>
/// <param name="Preferred">Usable for new traffic: not deprecated, not tentative, not failed.</param>
public readonly record struct Ipv6Candidate(string Address, bool Temporary, bool Preferred);

/// <summary>One IPv4 address the machine holds, and whether its adapter has a default gateway.</summary>
public readonly record struct Ipv4Candidate(string Address, bool HasGateway);

/// <summary>
/// The addresses a guest should dial to reach this machine as a host, read from the operating
/// system. <see cref="StableGlobalIPv6"/> is the one a router rule names. The ENet host binds it,
/// so a reply leaves from the address the guest dialled. It lives outside <c>CSVM.Net</c>
/// because that namespace may not name <c>System.Net</c>. The choice itself is <see cref="Choose"/>
/// and <see cref="ChooseLan"/>, which take the candidates as data so a unit test supplies them.
/// </summary>
public static class HostAddress
{
    // Linux's per-address flags in /proc/net/if_inet6 (include/uapi/linux/if_addr.h).
    private const int LinuxTemporary = 0x01;
    private const int LinuxDadFailed = 0x08;
    private const int LinuxDeprecated = 0x20;
    private const int LinuxTentative = 0x40;

    private const string LinuxAddressTable = "/proc/net/if_inet6";

    /// <summary>This machine's stable global IPv6 address, or null when it has none or the system
    /// will not say. Never throws.</summary>
    public static string? StableGlobalIPv6() => Choose(Ipv6Addresses());

    /// <summary>Every IPv6 address the system describes, in its own order; empty when it will not
    /// say. Never throws.</summary>
    public static IReadOnlyList<Ipv6Candidate> Ipv6Addresses()
    {
        try
        {
            return Ipv6Candidates();
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or IOException
                                   or UnauthorizedAccessException)
        {
            Log.Info("core", $"host address: IPv6 addresses unreadable: {e.Message}");
            return Array.Empty<Ipv6Candidate>();
        }
    }

    /// <summary>This machine's address on its local IPv4 network, or null when it has none.
    /// Never throws.</summary>
    public static string? LanIPv4()
    {
        try
        {
            return ChooseLan(Ipv4Candidates());
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            Log.Info("core", $"host address: IPv4 addresses unreadable: {e.Message}");
            return null;
        }
    }

    /// <summary>The first candidate that is global unicast (2000::/3, Teredo excluded), not
    /// temporary and preferred, in the order given; null when none is. ULA, link-local, loopback
    /// and mapped addresses all fall outside 2000::/3.</summary>
    public static string? Choose(IEnumerable<Ipv6Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
        {
            if (!candidate.Temporary && candidate.Preferred && IsGlobalUnicast(candidate.Address, out var address))
            {
                return address.ToString();
            }
        }

        return null;
    }

    /// <summary>The first private IPv4 address on an adapter with a default gateway, else the first
    /// private one at all; null when none is private. A virtual switch's private network has no
    /// gateway, which is what keeps it behind the adapter a guest can reach.</summary>
    public static string? ChooseLan(IEnumerable<Ipv4Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string? fallback = null;
        foreach (var candidate in candidates)
        {
            if (!IsPrivateIpv4(candidate.Address))
            {
                continue;
            }

            if (candidate.HasGateway)
            {
                return candidate.Address;
            }

            fallback ??= candidate.Address;
        }

        return fallback;
    }

    /// <summary>Reads Linux's address table, one line per address. A line holds the address as 32
    /// hex digits, the interface index, prefix length, scope, flags and interface name. A
    /// malformed line is skipped.</summary>
    public static IReadOnlyList<Ipv6Candidate> ParseLinuxTable(string table)
    {
        var found = new List<Ipv6Candidate>();
        foreach (string line in (table ?? "").Split('\n'))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[0].Length != 32
                || !int.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int flags))
            {
                continue;
            }

            var bytes = new byte[16];
            bool hex = true;
            for (int i = 0; i < 16 && hex; i++)
            {
                hex = byte.TryParse(fields[0].AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]);
            }

            if (hex)
            {
                bool preferred = (flags & (LinuxDeprecated | LinuxTentative | LinuxDadFailed)) == 0;
                found.Add(new Ipv6Candidate(new IPAddress(bytes).ToString(), (flags & LinuxTemporary) != 0, preferred));
            }
        }

        return found;
    }

    private static bool IsGlobalUnicast(string text, out IPAddress address)
    {
        if (!IPAddress.TryParse(text, out address!) || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        bool teredo = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0;
        address.ScopeId = 0;
        return (bytes[0] & 0xE0) == 0x20 && !teredo;
    }

    private static bool IsPrivateIpv4(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168);
    }

    // Windows says which address is temporary per address; Linux says it only in its own table,
    // since .NET's origin properties throw there. Anything else offers nothing to choose from.
    private static IReadOnlyList<Ipv6Candidate> Ipv6Candidates()
    {
        if (OperatingSystem.IsLinux())
        {
            return File.Exists(LinuxAddressTable) ? ParseLinuxTable(File.ReadAllText(LinuxAddressTable)) : Array.Empty<Ipv6Candidate>();
        }

        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<Ipv6Candidate>();
        }

        var found = new List<Ipv6Candidate>();
        foreach (var adapter in UpAdapters())
        {
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    found.Add(new Ipv6Candidate(unicast.Address.ToString(),
                        unicast.SuffixOrigin == SuffixOrigin.Random,
                        unicast.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred));
                }
            }
        }

        return found;
    }

    private static IReadOnlyList<Ipv4Candidate> Ipv4Candidates()
    {
        var found = new List<Ipv4Candidate>();
        foreach (var adapter in UpAdapters())
        {
            var properties = adapter.GetIPProperties();
            bool gateway = false;
            foreach (var route in properties.GatewayAddresses)
            {
                gateway |= route.Address.AddressFamily == AddressFamily.InterNetwork && !route.Address.Equals(IPAddress.Any);
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    found.Add(new Ipv4Candidate(unicast.Address.ToString(), gateway));
                }
            }
        }

        return found;
    }

    private static IEnumerable<NetworkInterface> UpAdapters()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            {
                yield return adapter;
            }
        }
    }
}
