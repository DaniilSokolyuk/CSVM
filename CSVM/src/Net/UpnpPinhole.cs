using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>How one attempt to open the host's IPv6 port in the router ended, in the shapes a host
/// can put in front of a player.</summary>
public enum UpnpPinholeOutcome
{
    /// <summary>The router holds a pinhole, so guests reach the port over IPv6.</summary>
    Opened,

    /// <summary>The router's IPv6 firewall is off, so the port is reachable without a pinhole.</summary>
    FirewallOff,

    /// <summary>The router's firewall takes no pinhole from this machine: its setting is off.</summary>
    Disallowed,

    /// <summary>No router on the LAN answered as an IPv6 firewall service.</summary>
    NoService,

    /// <summary>There is no stable global IPv6 address to open the port for.</summary>
    NoAddress,

    /// <summary>The router answered and the request failed, or nothing answered in time.</summary>
    Failed,
}

/// <summary>The IPv6 firewall calls the pinhole rules make, with no engine type in them. The shipped
/// one speaks SOAP inside <see cref="UpnpPinholeMap"/>; a unit's fake records the calls. Every call
/// may block for up to a gateway search.</summary>
public interface IPinholeGateway
{
    /// <summary>Finds the firewall service. Succeeds when one answered; NoService or Failed when
    /// none did.</summary>
    PinholeReply Discover();

    /// <summary>The service's GetFirewallStatus answer, <c>Answered</c> false when none came.</summary>
    (bool Answered, bool Enabled, bool PinholesAllowed, string Detail) Status();

    /// <summary>Opens UDP <paramref name="port"/> on <paramref name="address"/> to any remote host
    /// for <paramref name="leaseSeconds"/>. The reply carries the UniqueID.</summary>
    PinholeReply Add(string address, int port, int leaseSeconds);

    /// <summary>Renews pinhole <paramref name="id"/> for <paramref name="leaseSeconds"/>.</summary>
    PinholeReply Update(int id, int leaseSeconds);

    /// <summary>Removes pinhole <paramref name="id"/>.</summary>
    PinholeReply Delete(int id);
}

/// <summary>One pinhole the router holds: its UniqueID, the port and address it opens, and when its
/// lease runs out.</summary>
public readonly record struct PinholeLease(int Id, int Port, string Address, DateTime ExpiresUtc)
{
    /// <summary>Whether the lease still runs at <paramref name="nowUtc"/>. Past it the router has
    /// dropped the pinhole and may hand its UniqueID to another program.</summary>
    public bool StandsAt(DateTime nowUtc) => nowUtc < ExpiresUtc;
}

/// <summary>One attempt's result. <c>Lease</c> is the pinhole held once it opened, and
/// <c>StaleCleared</c> says a remembered pinhole was deleted on the way.</summary>
public readonly record struct UpnpPinholeResult(
    UpnpPinholeOutcome Outcome, int Port, string Address, string Detail,
    PinholeLease? Lease = null, int LeaseSeconds = 0, bool StaleCleared = false)
{
    /// <summary>Whether the router holds the pinhole.</summary>
    public bool IsOpen => Outcome == UpnpPinholeOutcome.Opened;
}

/// <summary>One gateway call's answer. <c>Id</c> is the UniqueID an add was granted.</summary>
public readonly record struct PinholeReply(UpnpPinholeOutcome Outcome, string Detail, int Id = 0)
{
    /// <summary>Whether the call did what it was asked.</summary>
    public bool Succeeded => Outcome == UpnpPinholeOutcome.Opened;
}

/// <summary>
/// The rules the host's IPv6 pinhole is held under, with no engine type in them. A missing global
/// address, firewall service or allowing status is each its own outcome, with no add. A pinhole
/// takes <see cref="UpnpLease"/>'s finite lease, renewed by UniqueID and re-added if the router
/// forgot it. A fresh add first deletes the pinhole an earlier run remembered.
/// ⚠ Do not delete a remembered UniqueID past its lease. The router frees the number then, and it
/// may already name another program's pinhole.
/// </summary>
public static class UpnpPinhole
{
    /// <summary>Opens UDP <paramref name="port"/> for <paramref name="address"/> through the gateway.
    /// This process's own pinhole, <paramref name="held"/>, is renewed in place on the same port and
    /// address. The one an earlier run left is <paramref name="remembered"/>. Never throws past a
    /// null gateway.</summary>
    public static UpnpPinholeResult Open(IPinholeGateway gateway, string? address, int port,
        PinholeLease? held, PinholeLease? remembered, DateTime nowUtc, int leaseSeconds = UpnpLease.LeaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        string shown = address ?? "";
        if (!UpnpLease.IsPort(port))
        {
            return new UpnpPinholeResult(UpnpPinholeOutcome.Failed, port, shown, "port out of range");
        }

        if (!IgdPinhole.IsGlobalUnicast(address))
        {
            string why = shown.Length == 0 ? "no stable global IPv6 address" : $"{shown} is not a global IPv6 address";
            return new UpnpPinholeResult(UpnpPinholeOutcome.NoAddress, port, shown, why);
        }

        var found = gateway.Discover();
        if (!found.Succeeded)
        {
            return new UpnpPinholeResult(found.Outcome, port, shown, found.Detail);
        }

        int lease = Math.Clamp(leaseSeconds, UpnpLease.MinLeaseSeconds, UpnpLease.MaxLeaseSeconds);
        if (held is { } own && own.Port == port && own.Address == address)
        {
            // A router that restarted has forgotten the UniqueID, so a failed renewal adds afresh.
            if (gateway.Update(own.Id, lease).Succeeded)
            {
                return Opened(port, address, own.Id, lease, nowUtc, "renewed", false);
            }
        }

        var (answered, enabled, allowed, detail) = gateway.Status();
        if (!answered)
        {
            return new UpnpPinholeResult(UpnpPinholeOutcome.Failed, port, shown, $"no firewall status: {detail}");
        }

        if (!enabled)
        {
            return new UpnpPinholeResult(UpnpPinholeOutcome.FirewallOff, port, shown, "the router's IPv6 firewall is off");
        }

        if (!allowed)
        {
            return new UpnpPinholeResult(UpnpPinholeOutcome.Disallowed, port, shown, "the router allows no inbound pinhole");
        }

        bool cleared = false;
        foreach (var stale in Stale(held, remembered, nowUtc))
        {
            bool gone = gateway.Delete(stale).Succeeded;
            cleared |= gone && remembered?.Id == stale;
        }

        var added = gateway.Add(address!, port, lease);
        return added.Succeeded
            ? Opened(port, address!, added.Id, lease, nowUtc, "opened", cleared)
            : new UpnpPinholeResult(added.Outcome, port, shown, added.Detail, StaleCleared: cleared);
    }

    /// <summary>Removes <paramref name="lease"/>'s pinhole. False when no service answered or the
    /// router did not remove it.</summary>
    public static bool Close(IPinholeGateway gateway, PinholeLease lease)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        return gateway.Discover().Succeeded && gateway.Delete(lease.Id).Succeeded;
    }

    // The UniqueIDs a fresh add clears first, each once. They are a held pinhole the renewal could
    // not keep and the one an earlier run remembered, each only while its lease runs.
    private static List<int> Stale(PinholeLease? held, PinholeLease? remembered, DateTime nowUtc)
    {
        var ids = new List<int>();
        foreach (var lease in new[] { held, remembered })
        {
            if (lease is { } l && l.StandsAt(nowUtc) && !ids.Contains(l.Id))
            {
                ids.Add(l.Id);
            }
        }

        return ids;
    }

    private static UpnpPinholeResult Opened(int port, string address, int id, int lease, DateTime nowUtc, string detail, bool cleared) =>
        new(UpnpPinholeOutcome.Opened, port, address, detail,
            new PinholeLease(id, port, address, nowUtc.AddSeconds(lease)), lease, cleared);
}
