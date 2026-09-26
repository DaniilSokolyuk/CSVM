using System;
using System.Threading;
using CSVM.Utils;
using Godot;

namespace CSVM.Net;

/// <summary>How one port-mapping attempt ended, in the five shapes a host can put in front of a
/// player.</summary>
public enum UpnpPortMapOutcome
{
    /// <summary>The gateway holds the mapping and the host is reachable from outside it.</summary>
    Mapped,

    /// <summary>No UPnP gateway answered the search, so there was nothing to ask.</summary>
    NoGateway,

    /// <summary>A gateway answered and declined, or cannot take a mapping at all, which is what an
    /// off switch or a taken port looks like.</summary>
    Refused,

    /// <summary>A gateway answered, but its own internet address is private or carrier-shared, so
    /// no mapping can make the host reachable over IPv4. The result carries that address.</summary>
    NoPublicAddress,

    /// <summary>The search or the request ran out of time, or failed at the socket.</summary>
    TimedOut,
}

/// <summary>One attempt's result: what happened, the port it was about, the external address when
/// one was learned, and the gateway's own word for it. <c>LeaseSeconds</c> is the lease granted,
/// 0 for a permanent one or none.</summary>
public readonly record struct UpnpPortMapResult(
    UpnpPortMapOutcome Outcome, int Port, string ExternalAddress, string Detail, int LeaseSeconds = 0)
{
    /// <summary>Whether the port is mapped.</summary>
    public bool IsMapped => Outcome == UpnpPortMapOutcome.Mapped;
}

/// <summary>
/// A best-effort port mapping through Godot's UPnP client, for a host that wants to be reachable
/// from outside its own router. It is attempted and reported, never required. Every path returns
/// a result and none throws. The lease rules are <see cref="UpnpLease"/>'s; this class is the
/// engine's gateway under them and the user's <see cref="UpnpPortMemory"/>.
/// ⚠ Both calls block for as long as the gateway search takes, so neither belongs on a frame or
/// in a transport step. Run them on the door's mapping thread and where hosting closes.
/// </summary>
public static class UpnpPortMap
{
    /// <summary>How long the gateway search may take. Godot's own default, kept because a longer
    /// wait only delays the answer a host already treats as optional.</summary>
    public const int DiscoverTimeoutMs = 2000;

    // ENet carries the match over UDP, so a TCP mapping would open the wrong door.
    private const string Protocol = "UDP";

    // The port this process holds mapped, so a second Map of it is a renewal rather than a fresh
    // mapping that deletes before it adds.
    private static int _held;

    /// <summary>Asks the gateway to forward <paramref name="port"/> to this machine on a finite
    /// lease. When this process already holds it, the call renews the lease.
    /// <paramref name="description"/> is what the router's own mapping table shows.</summary>
    public static UpnpPortMapResult Map(int port, string description = "CSVM")
    {
        try
        {
            using var upnp = new Upnp();
            var memory = UserMemory();
            bool renewing = Volatile.Read(ref _held) == port;
            var result = UpnpLease.Map(new EngineGateway(upnp), port, description, memory.Recall(), renewing);
            if (result.IsMapped)
            {
                Volatile.Write(ref _held, port);
                memory.Remember(port);
            }

            string verb = renewing ? "renewal" : "mapping";
            Log.Info("core", $"upnp {verb} port={port} outcome={result.Outcome} lease={result.LeaseSeconds}s external={result.ExternalAddress} detail={result.Detail}");
            return result;
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp mapping failed port={port} error={e.GetType().Name}: {e.Message}");
            return new UpnpPortMapResult(UpnpPortMapOutcome.Refused, port, "", e.GetType().Name);
        }
    }

    /// <summary>Takes the mapping back down. False when there was no gateway, no mapping, or the
    /// gateway declined; a host that stops hosting reports nothing either way. The remembered
    /// port is forgotten only once its mapping is gone.</summary>
    public static bool Unmap(int port)
    {
        Interlocked.CompareExchange(ref _held, 0, port);
        try
        {
            using var upnp = new Upnp();
            var gateway = new EngineGateway(upnp);
            if (!gateway.Discover().Succeeded || !gateway.Delete(port))
            {
                return false;
            }

            UserMemory().Forget(port);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp unmapping failed port={port} error={e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static UpnpPortMemory UserMemory() => new(ProjectSettings.GlobalizePath("user://"));

    // Godot returns one flat result code for both the search and the request, so every call
    // below shares this reading of it.
    private static UpnpPortMapOutcome OutcomeOf(Upnp.UpnpResult result) => result switch
    {
        Upnp.UpnpResult.Success => UpnpPortMapOutcome.Mapped,
        Upnp.UpnpResult.NoGateway or Upnp.UpnpResult.NoDevices or Upnp.UpnpResult.InvalidGateway
            => UpnpPortMapOutcome.NoGateway,
        Upnp.UpnpResult.HttpError or Upnp.UpnpResult.SocketError or Upnp.UpnpResult.InvalidResponse
            => UpnpPortMapOutcome.TimedOut,
        _ => UpnpPortMapOutcome.Refused,
    };

    // Asks an unusable gateway's connection services for the external address, first answer wins.
    // Godot's own query needs a valid gateway, so this reads the description and asks over SOAP.
    private static string AskExternalAddress(string descriptionUrl)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(DiscoverTimeoutMs);
        if (!Uri.TryCreate(descriptionUrl, UriKind.Absolute, out var described)
            || Fetch(described, HttpClient.Method.Get, Array.Empty<string>(), "", deadline) is not { } description)
        {
            return "";
        }

        foreach (var (service, control) in IgdAddress.Connections(description, descriptionUrl))
        {
            var (action, body) = IgdAddress.ExternalAddressRequest(service);
            string[] headers = { "Content-Type: text/xml; charset=\"utf-8\"", $"SOAPAction: {action}" };
            string address = IgdAddress.ExternalAddressOf(Fetch(control, HttpClient.Method.Post, headers, body, deadline) ?? "");
            if (address.Length > 0)
            {
                return address;
            }
        }

        return "";
    }

    // One plain-HTTP exchange on a LAN gateway, polled until the deadline. Null on any failure,
    // a non-200 answer included.
    private static string? Fetch(Uri url, HttpClient.Method method, string[] headers, string body, DateTime deadline)
    {
        using var http = new HttpClient();
        if (url.Scheme != Uri.UriSchemeHttp || http.ConnectToHost(url.Host, url.Port) != Error.Ok
            || !PollWhile(http, deadline, HttpClient.Status.Resolving, HttpClient.Status.Connecting)
            || http.GetStatus() != HttpClient.Status.Connected
            || http.Request(method, url.PathAndQuery, headers, body) != Error.Ok
            || !PollWhile(http, deadline, HttpClient.Status.Requesting)
            || !http.HasResponse() || http.GetResponseCode() != 200)
        {
            return null;
        }

        var bytes = new System.Collections.Generic.List<byte>();
        while (http.GetStatus() == HttpClient.Status.Body && DateTime.UtcNow < deadline)
        {
            http.Poll();
            byte[] chunk = http.ReadResponseBodyChunk();
            if (chunk.Length == 0)
            {
                Thread.Sleep(1);
            }

            bytes.AddRange(chunk);
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    // False when the deadline passed with the client still in one of the waiting states.
    private static bool PollWhile(HttpClient http, DateTime deadline, params HttpClient.Status[] waiting)
    {
        while (Array.IndexOf(waiting, http.GetStatus()) >= 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            http.Poll();
            Thread.Sleep(1);
        }

        return true;
    }

    // The lease rules' gateway over one Godot client, which holds the device the search found.
    private sealed class EngineGateway : IUpnpGateway
    {
        private readonly Upnp _upnp;

        // The description of a gateway that answered but that Godot will not map through, kept so
        // its external address can still be asked directly.
        private string _unusable = "";

        public EngineGateway(Upnp upnp) => _upnp = upnp;

        public UpnpReply Discover()
        {
            _unusable = "";
            var found = (Upnp.UpnpResult)_upnp.Discover(DiscoverTimeoutMs);
            if (found != Upnp.UpnpResult.Success)
            {
                return new UpnpReply(OutcomeOf(found), found.ToString());
            }

            using (var gateway = _upnp.GetGateway())
            {
                if (gateway != null && gateway.IsValidGateway())
                {
                    return new UpnpReply(UpnpPortMapOutcome.Mapped, "found");
                }
            }

            // An invalid gateway still answered the search. Godot's connection check does not say
            // why it failed, so the lease rules ask its address before calling it refused.
            for (int i = 0; i < _upnp.GetDeviceCount(); i++)
            {
                using var device = _upnp.GetDevice(i);
                if (device != null && device.DescriptionUrl.Length > 0)
                {
                    _unusable = device.DescriptionUrl;
                    return new UpnpReply(UpnpPortMapOutcome.Refused, $"gateway answered but is unusable, igd_status={device.IgdStatus}");
                }
            }

            return new UpnpReply(UpnpPortMapOutcome.NoGateway, "no UPnP device answered");
        }

        public UpnpReply Add(int port, string description, int leaseSeconds)
        {
            var added = (Upnp.UpnpResult)_upnp.AddPortMapping(port, port, description, Protocol, leaseSeconds);
            return new UpnpReply(OutcomeOf(added), added.ToString(),
                added == Upnp.UpnpResult.OnlyPermanentLeaseSupported);
        }

        public bool Delete(int port) =>
            (Upnp.UpnpResult)_upnp.DeletePortMapping(port, Protocol) == Upnp.UpnpResult.Success;

        public string ExternalAddress() =>
            _unusable.Length > 0 ? AskExternalAddress(_unusable) : _upnp.QueryExternalAddress();
    }
}
