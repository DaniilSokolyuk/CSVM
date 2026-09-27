using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CSVM.Utils;

namespace CSVM.Net;

/// <summary>
/// A best-effort IPv6 pinhole in the host's router, for a line whose IPv4 has no public address.
/// It asks the router's IGD v2 WANIPv6FirewallControl service over SOAP, found by an SSDP search,
/// since Godot's UPnP client speaks only IPv4 mapping. Every path returns a result and none throws.
/// The rules are <see cref="UpnpPinhole"/>'s; this class is their gateway over the LAN and the
/// user's <see cref="UpnpPinholeMemory"/>.
/// ⚠ Both calls block for up to a search and a few requests, so neither belongs on a frame. Run
/// them on the door's mapping thread and where hosting closes.
/// </summary>
public static class UpnpPinholeMap
{
    /// <summary>How long the service search and each call's requests may take together.</summary>
    public const int SearchTimeoutMs = 2000;

    private static readonly object Gate = new();

    // The pinhole this process holds, so a second Open of its port renews it by UniqueID.
    private static PinholeLease? _held;

    /// <summary>Opens UDP <paramref name="port"/> in the router's IPv6 firewall for
    /// <paramref name="address"/>, or renews the pinhole this process already holds there.</summary>
    public static UpnpPinholeResult Open(int port, string? address)
    {
        try
        {
            var memory = UserMemory();
            var now = DateTime.UtcNow;
            var remembered = memory.Recall();
            PinholeLease? held;
            lock (Gate)
            {
                held = _held;
            }

            var result = UpnpPinhole.Open(new SoapGateway(), address, port, held, remembered, now);
            if (result.Lease is { } lease)
            {
                lock (Gate)
                {
                    _held = lease;
                }

                memory.Remember(lease);
            }
            else if (remembered is { } r && (result.StaleCleared || !r.StandsAt(now)))
            {
                memory.Forget(r.Id);
            }

            string id = result.Lease is { } l ? l.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            Log.Info("core", $"upnp pinhole port={port} address={result.Address} outcome={result.Outcome} id={id} lease={result.LeaseSeconds}s detail={result.Detail}");
            return result;
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp pinhole failed port={port} error={e.GetType().Name}: {e.Message}");
            return new UpnpPinholeResult(UpnpPinholeOutcome.Failed, port, address ?? "", e.GetType().Name);
        }
    }

    /// <summary>Removes the pinhole this process holds on <paramref name="port"/>. False when it held
    /// none there or the router did not remove it. The memory forgets it only once it is gone.</summary>
    public static bool Close(int port)
    {
        PinholeLease? held;
        lock (Gate)
        {
            held = _held is { } h && h.Port == port ? h : null;
            if (held != null)
            {
                _held = null;
            }
        }

        if (held is not { } lease)
        {
            return false;
        }

        try
        {
            bool closed = UpnpPinhole.Close(new SoapGateway(), lease);
            if (closed)
            {
                UserMemory().Forget(lease.Id);
            }

            Log.Info("core", $"upnp pinhole close port={port} id={lease.Id} removed={closed}");
            return closed;
        }
        catch (Exception e)
        {
            Log.Warn("core", $"upnp pinhole close failed port={port} error={e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>The shipped gateway, for a read-only probe of the router: its search and its
    /// firewall status. A probe must not call its add, update or delete.</summary>
    internal static IPinholeGateway RouterGateway() => new SoapGateway();

    private static UpnpPinholeMemory UserMemory() => new(UpnpPortMap.UserDirectory());

    // The pinhole rules' gateway over the LAN: an SSDP search for the service, then SOAP to the
    // control URL the answering description names.
    private sealed class SoapGateway : IPinholeGateway
    {
        private string _service = IgdPinhole.ServiceType;
        private Uri? _control;

        public PinholeReply Discover()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(SearchTimeoutMs);
            using var socket = LanDiscoverySocket.Bind(0, "0.0.0.0");
            socket.Send(IgdPinhole.SsdpGroup, IgdPinhole.SsdpPort, Encoding.ASCII.GetBytes(IgdPinhole.SearchRequest(1)));
            var asked = new HashSet<Uri>();
            while (DateTime.UtcNow < deadline)
            {
                if (socket.Receive(out _, out _) is not { } datagram)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (IgdPinhole.LocationOf(Encoding.ASCII.GetString(datagram)) is not { } location || !asked.Add(location)
                    || UpnpPortMap.Get(location, deadline) is not { } description)
                {
                    continue;
                }

                foreach (var (service, control) in IgdPinhole.FirewallControls(description, location.AbsoluteUri))
                {
                    _service = service;
                    _control = control;
                    return new PinholeReply(UpnpPinholeOutcome.Opened, $"found at {control}");
                }
            }

            return asked.Count == 0
                ? new PinholeReply(UpnpPinholeOutcome.NoService, "no IPv6 firewall service answered")
                : new PinholeReply(UpnpPinholeOutcome.NoService, "a gateway answered but its description names no IPv6 firewall service");
        }

        public (bool Answered, bool Enabled, bool PinholesAllowed, string Detail) Status()
        {
            var (action, body) = IgdPinhole.StatusRequest(_service);
            var (answer, detail) = Call(action, body);
            var (answered, enabled, allowed) = IgdPinhole.StatusOf(answer);
            return (answered, enabled, allowed, detail);
        }

        public PinholeReply Add(string address, int port, int leaseSeconds)
        {
            var (action, body) = IgdPinhole.AddRequest(_service, address, port, leaseSeconds);
            var (answer, detail) = Call(action, body);
            return IgdPinhole.UniqueIdOf(answer) is { } id
                ? new PinholeReply(UpnpPinholeOutcome.Opened, "added", id)
                : Refusal(answer, detail);
        }

        public PinholeReply Update(int id, int leaseSeconds)
        {
            var (action, body) = IgdPinhole.UpdateRequest(_service, id, leaseSeconds);
            return Plain(action, body, id);
        }

        public PinholeReply Delete(int id)
        {
            var (action, body) = IgdPinhole.DeleteRequest(_service, id);
            return Plain(action, body, id);
        }

        private static PinholeReply Refusal(string answer, string detail)
        {
            var (code, description) = IgdPinhole.FaultOf(answer);
            if (code == 0)
            {
                return new PinholeReply(UpnpPinholeOutcome.Failed, detail);
            }

            var outcome = IgdPinhole.IsRefusal(code) ? UpnpPinholeOutcome.Disallowed : UpnpPinholeOutcome.Failed;
            return new PinholeReply(outcome, $"error {code} {description}");
        }

        // An action whose answer carries nothing: a 200 is success, anything else its fault.
        private PinholeReply Plain(string action, string body, int id)
        {
            var (answer, detail) = Call(action, body);
            return detail == "200"
                ? new PinholeReply(UpnpPinholeOutcome.Opened, "done", id)
                : Refusal(answer, detail);
        }

        // The answer's body and its HTTP status as a word, or "" and why when none came.
        private (string Answer, string Detail) Call(string action, string body)
        {
            if (_control == null)
            {
                return ("", "no control URL");
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(SearchTimeoutMs);
            return UpnpPortMap.Soap(_control, action, body, deadline) is var (status, answer)
                ? (answer, status.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : ("", "no answer in time");
        }
    }
}
