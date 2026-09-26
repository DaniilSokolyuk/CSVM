using System;
using System.Threading;

namespace CSVM.Net;

/// <summary>The gateway calls the lease rules make, with no engine type in them. The shipped one
/// wraps Godot's UPnP client inside <see cref="UpnpPortMap"/>, and a unit's fake records the calls.
/// Every call may block for the length of a gateway search.</summary>
public interface IUpnpGateway
{
    /// <summary>Searches for the gateway. Succeeds when one answered and is valid; Refused when one
    /// answered that cannot take a mapping; NoGateway or TimedOut when none answered.</summary>
    UpnpReply Discover();

    /// <summary>Asks for UDP <paramref name="port"/> forwarded to this machine for
    /// <paramref name="leaseSeconds"/>, 0 meaning a permanent lease.</summary>
    UpnpReply Add(int port, string description, int leaseSeconds);

    /// <summary>Removes the UDP mapping on <paramref name="port"/>. False when none was removed.</summary>
    bool Delete(int port);

    /// <summary>The external address of the gateway the search found, valid or not, or "" when it
    /// will not say.</summary>
    string ExternalAddress();
}

/// <summary>One gateway call's answer, in the port map's own outcome words. It also says whether the
/// refusal was the one a finite lease draws from a gateway that keeps only permanent ones.</summary>
public readonly record struct UpnpReply(UpnpPortMapOutcome Outcome, string Detail, bool PermanentLeaseOnly = false)
{
    /// <summary>Whether the call did what it was asked.</summary>
    public bool Succeeded => Outcome == UpnpPortMapOutcome.Mapped;
}

/// <summary>
/// The rules a host's router mapping is held under, with no engine type in them. A mapping is
/// asked on a finite lease, so a game that dies without closing leaves nothing in the router past
/// the lease. A gateway that keeps only permanent leases gets one, and the close is then all that
/// removes it. Before the first add, the port about to be mapped and the port an earlier run
/// remembered are deleted, and nothing else.
/// ⚠ Do not delete a range or search for mappings. Godot's client cannot ask who holds one, so a
/// delete by port can remove another program's mapping.
/// </summary>
public static class UpnpLease
{
    /// <summary>The lease a mapping is asked on, in seconds. TUNE: long enough that a renewal is
    /// rare, short enough that a crashed host's port closes within the hour.</summary>
    public const int LeaseSeconds = 3600;

    /// <summary>The shortest finite lease the IGD specification allows.</summary>
    public const int MinLeaseSeconds = 120;

    /// <summary>The longest finite lease the IGD specification allows.</summary>
    public const int MaxLeaseSeconds = 86400;

    /// <summary>The remembered port meaning "no earlier run left one".</summary>
    public const int NoPort = 0;

    /// <summary>The share of a granted lease that passes before it is renewed. Half leaves the
    /// other half for the renewal's own gateway search and for its retries.</summary>
    public const double RenewAtFraction = 0.5;

    /// <summary>The share of a lease between retries after a renewal failed, so four fit into the
    /// half that remains.</summary>
    public const double RetryFraction = 0.125;

    /// <summary>Maps <paramref name="port"/> through <paramref name="gateway"/> on
    /// <paramref name="leaseSeconds"/>, clamped into the specification's range. A first mapping
    /// deletes <paramref name="rememberedPort"/> and the port itself before adding; a renewal only
    /// adds, since re-adding its own mapping refreshes the lease. A gateway reporting a private or
    /// shared external address gets no call past that question. Never throws.</summary>
    public static UpnpPortMapResult Map(IUpnpGateway gateway, int port, string description,
        int rememberedPort = NoPort, bool renewing = false, int leaseSeconds = LeaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (!IsPort(port))
        {
            return new UpnpPortMapResult(UpnpPortMapOutcome.Refused, port, "", "port out of range");
        }

        var found = gateway.Discover();
        if (found.Outcome is not (UpnpPortMapOutcome.Mapped or UpnpPortMapOutcome.Refused))
        {
            return new UpnpPortMapResult(found.Outcome, port, "", found.Detail);
        }

        // Asked before any add, and of an unusable gateway too. Behind a carrier's NAT the router
        // still answers, but a mapping there opens nothing the internet can reach.
        string external = gateway.ExternalAddress();
        if (IgdAddress.IsUnreachable(external))
        {
            string kind = IgdAddress.Word(IgdAddress.Kind(external));
            return new UpnpPortMapResult(UpnpPortMapOutcome.NoPublicAddress, port, external,
                $"gateway answered, its external address {external} is {kind}");
        }

        if (!found.Succeeded)
        {
            return new UpnpPortMapResult(found.Outcome, port, external, found.Detail);
        }

        if (!renewing)
        {
            if (IsPort(rememberedPort) && rememberedPort != port)
            {
                gateway.Delete(rememberedPort);
            }

            gateway.Delete(port);
        }

        int lease = Math.Clamp(leaseSeconds, MinLeaseSeconds, MaxLeaseSeconds);
        var added = gateway.Add(port, description, lease);
        if (!added.Succeeded && added.PermanentLeaseOnly)
        {
            lease = 0;
            added = gateway.Add(port, description, lease);
        }

        if (!added.Succeeded)
        {
            return new UpnpPortMapResult(added.Outcome, port, "", added.Detail);
        }

        string detail = lease == 0 ? "mapped, permanent lease only" : "mapped";
        return new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, external, detail, lease);
    }

    /// <summary>How long to wait before the next renewal, after <paramref name="latest"/>.
    /// <paramref name="heldLeaseSeconds"/> is the last finite lease granted, or 0 when none was.
    /// Infinite when nothing is to be renewed: no mapping, or a permanent one.</summary>
    public static TimeSpan NextRenewal(in UpnpPortMapResult latest, int heldLeaseSeconds)
    {
        if (latest.IsMapped)
        {
            return latest.LeaseSeconds > 0
                ? TimeSpan.FromSeconds(latest.LeaseSeconds * RenewAtFraction)
                : Timeout.InfiniteTimeSpan;
        }

        return heldLeaseSeconds > 0
            ? TimeSpan.FromSeconds(heldLeaseSeconds * RetryFraction)
            : Timeout.InfiniteTimeSpan;
    }

    /// <summary>Whether <paramref name="port"/> is one a mapping can name.</summary>
    public static bool IsPort(int port) => port is >= 1 and <= 65535;
}
