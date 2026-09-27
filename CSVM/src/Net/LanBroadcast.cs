using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// Where a LAN search sends its query each round: the limited broadcast, then the directed
/// broadcast of every IPv4 network the machine sits on. Engine-free, and it names no socket API.
/// ⚠ Do not ask at the limited broadcast alone. Windows sends it out of one interface only, so a
/// machine with several adapters can miss the network its host is on.
/// </summary>
public static class LanBroadcast
{
    /// <summary>The limited broadcast address, asked at every round whatever else is.</summary>
    public const string Limited = "255.255.255.255";

    /// <summary>The directed broadcast of <paramref name="address"/> under <paramref name="mask"/>:
    /// the address with every host bit set. Null when either is not a dotted IPv4 quad.</summary>
    public static string? Directed(string address, string mask)
    {
        if (!TryParse(address, out uint at) || !TryParse(mask, out uint under))
        {
            return null;
        }

        return Format(at | ~under);
    }

    /// <summary>The limited broadcast first, then each network's directed broadcast once, in the
    /// order given. A loopback network, a single-host mask or one that does not parse adds
    /// nothing.</summary>
    public static IReadOnlyList<string> Targets(IEnumerable<(string Address, string Mask)> networks)
    {
        var targets = new List<string> { Limited };
        if (networks == null)
        {
            return targets;
        }

        foreach (var (address, mask) in networks)
        {
            if (!TryParse(address, out uint at) || !TryParse(mask, out uint under)
                || at >> 24 == 127 || under == uint.MaxValue)
            {
                continue;
            }

            string directed = Format(at | ~under);
            if (!targets.Contains(directed))
            {
                targets.Add(directed);
            }
        }

        return targets;
    }

    private static bool TryParse(string text, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || !byte.TryParse(part, out byte octet))
            {
                return false;
            }

            value = (value << 8) | octet;
        }

        return true;
    }

    private static string Format(uint value) =>
        $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
}
