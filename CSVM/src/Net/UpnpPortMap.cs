using System;
using CSVM.Utils;
using Godot;

namespace CSVM.Net;

/// <summary>How one port-mapping attempt ended, in the four shapes a host can put in front of a
/// player.</summary>
public enum UpnpPortMapOutcome
{
    /// <summary>The gateway holds the mapping and the host is reachable from outside it.</summary>
    Mapped,

    /// <summary>No UPnP gateway answered the search, so there was nothing to ask.</summary>
    NoGateway,

    /// <summary>A gateway answered and declined, which is what an off switch or a taken port
    /// looks like.</summary>
    Refused,

    /// <summary>The search or the request ran out of time, or failed at the socket.</summary>
    TimedOut,
}

/// <summary>One attempt's result: what happened, the port it was about, the external address when
/// one was learned, and the gateway's own word for it.</summary>
public readonly record struct UpnpPortMapResult(
    UpnpPortMapOutcome Outcome, int Port, string ExternalAddress, string Detail)
{
    /// <summary>Whether the port is mapped.</summary>
    public bool IsMapped => Outcome == UpnpPortMapOutcome.Mapped;
}

/// <summary>
/// A best-effort port mapping through Godot's UPnP client, for a host that wants to be reachable
/// from outside its own router. It is attempted and reported, never required. Every path returns
/// a result and none throws. A refused mapping costs the host a line on the board, and a guest on
/// the same network still joins.
/// ⚠ Both calls block for as long as the gateway search takes, so neither belongs on a frame or
/// in a transport step. Run them once when hosting opens and once when it closes.
/// </summary>
public static class UpnpPortMap
{
    /// <summary>How long the gateway search may take. Godot's own default, kept because a longer
    /// wait only delays the answer a host already treats as optional.</summary>
    public const int DiscoverTimeoutMs = 2000;

    // ENet carries the match over UDP, so a TCP mapping would open the wrong door.
    private const string Protocol = "UDP";

    /// <summary>Asks the gateway to forward <paramref name="port"/> to this machine.
    /// <paramref name="description"/> is what the router's own mapping table shows.</summary>
    public static UpnpPortMapResult Map(int port, string description = "CSVM")
    {
        if (port is < 1 or > 65535)
        {
            return new UpnpPortMapResult(UpnpPortMapOutcome.Refused, port, "", "port out of range");
        }

        try
        {
            using var upnp = new Upnp();
            var found = (Upnp.UpnpResult)upnp.Discover(DiscoverTimeoutMs);
            if (found != Upnp.UpnpResult.Success)
            {
                return new UpnpPortMapResult(OutcomeOf(found), port, "", found.ToString());
            }

            using var gateway = upnp.GetGateway();
            if (gateway == null || !gateway.IsValidGateway())
            {
                return new UpnpPortMapResult(UpnpPortMapOutcome.NoGateway, port, "", "no valid gateway");
            }

            var mapped = (Upnp.UpnpResult)upnp.AddPortMapping(port, port, description, Protocol, 0);
            if (mapped != Upnp.UpnpResult.Success)
            {
                return new UpnpPortMapResult(OutcomeOf(mapped), port, "", mapped.ToString());
            }

            return new UpnpPortMapResult(
                UpnpPortMapOutcome.Mapped, port, upnp.QueryExternalAddress(), "mapped");
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp mapping failed port={port} error={e.GetType().Name}: {e.Message}");
            return new UpnpPortMapResult(UpnpPortMapOutcome.Refused, port, "", e.GetType().Name);
        }
    }

    /// <summary>Takes the mapping back down. False when there was no gateway, no mapping, or the
    /// gateway declined; a host that stops hosting reports nothing either way.</summary>
    public static bool Unmap(int port)
    {
        try
        {
            using var upnp = new Upnp();
            if ((Upnp.UpnpResult)upnp.Discover(DiscoverTimeoutMs) != Upnp.UpnpResult.Success)
            {
                return false;
            }

            return (Upnp.UpnpResult)upnp.DeletePortMapping(port, Protocol) == Upnp.UpnpResult.Success;
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp unmapping failed port={port} error={e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // Godot returns one flat result code for both the search and the request, so the two calls
    // above share this reading of it.
    private static UpnpPortMapOutcome OutcomeOf(Upnp.UpnpResult result) => result switch
    {
        Upnp.UpnpResult.Success => UpnpPortMapOutcome.Mapped,
        Upnp.UpnpResult.NoGateway or Upnp.UpnpResult.NoDevices or Upnp.UpnpResult.InvalidGateway
            => UpnpPortMapOutcome.NoGateway,
        Upnp.UpnpResult.HttpError or Upnp.UpnpResult.SocketError or Upnp.UpnpResult.InvalidResponse
            => UpnpPortMapOutcome.TimedOut,
        _ => UpnpPortMapOutcome.Refused,
    };
}
