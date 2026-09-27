using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CSVM.Net;

/// <summary>
/// The text an IGD v2 IPv6 firewall is asked and answers in, read without the engine. It holds
/// the SSDP search, the control URL in a description, and the four SOAP requests of a pinhole's
/// life with their answers and faults. The exchange itself is
/// <see cref="UpnpPinholeMap"/>'s. Every parse returns empty rather than throwing, since a
/// gateway's text is untrusted. The shapes are the UPnP WANIPv6FirewallControl:1 service's;
/// <c>docs/architecture/Net.md</c> names the router they were read against.
/// </summary>
public static class IgdPinhole
{
    /// <summary>The service a pinhole is asked of, as an SSDP search and a description name it.</summary>
    public const string ServiceType = "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1";

    /// <summary>The SSDP multicast group and port every UPnP device listens on.</summary>
    public const string SsdpGroup = "239.255.255.250";

    /// <summary>The SSDP port.</summary>
    public const int SsdpPort = 1900;

    /// <summary>The IANA protocol number for UDP, which ENet carries a match over.</summary>
    public const int Udp = 17;

    /// <summary>The UPnP fault a gateway answers when the caller may not take the action.</summary>
    public const int ActionNotAuthorized = 606;

    /// <summary>The WANIPv6FirewallControl fault for a firewall that takes no inbound pinhole.</summary>
    public const int InboundPinholeNotAllowed = 703;

    // Matched against a description's serviceType, any version.
    private static readonly string[] FirewallServices = { ":service:WANIPv6FirewallControl:" };

    /// <summary>The M-SEARCH datagram that asks every IPv6 firewall service on the LAN to answer,
    /// within <paramref name="mxSeconds"/>.</summary>
    public static string SearchRequest(int mxSeconds) =>
        "M-SEARCH * HTTP/1.1\r\n"
        + $"HOST: {SsdpGroup}:{SsdpPort.ToString(CultureInfo.InvariantCulture)}\r\n"
        + "MAN: \"ssdp:discover\"\r\n"
        + $"MX: {Math.Clamp(mxSeconds, 1, 5).ToString(CultureInfo.InvariantCulture)}\r\n"
        + $"ST: {ServiceType}\r\n\r\n";

    /// <summary>The description URL an SSDP answer names in its LOCATION header, or null when it
    /// names no plain-HTTP one. Answers for another search target are null too, since devices on a
    /// LAN answer searches they were not asked.</summary>
    public static Uri? LocationOf(string answer)
    {
        string? location = null;
        bool firewall = false;
        foreach (string line in (answer ?? "").Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
            {
                location = value;
            }
            else if (name.Equals("ST", StringComparison.OrdinalIgnoreCase))
            {
                firewall = value.Contains(FirewallServices[0], StringComparison.Ordinal);
            }
        }

        return firewall && Uri.TryCreate(location, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttp
            ? url
            : null;
    }

    /// <summary>Every IPv6 firewall service in the device description <paramref name="xml"/>, as
    /// its service type and absolute control URL, in document order.</summary>
    public static IReadOnlyList<(string ServiceType, Uri Control)> FirewallControls(string xml, string descriptionUrl) =>
        IgdAddress.Services(xml, descriptionUrl, FirewallServices);

    /// <summary>The SOAPAction header and envelope of GetFirewallStatus.</summary>
    public static (string SoapAction, string Body) StatusRequest(string serviceType) =>
        Request(serviceType, "GetFirewallStatus");

    /// <summary>The SOAPAction header and envelope of an AddPinhole that lets any remote host and
    /// port reach UDP <paramref name="port"/> on <paramref name="internalClient"/>.</summary>
    public static (string SoapAction, string Body) AddRequest(string serviceType, string internalClient, int port, int leaseSeconds) =>
        Request(serviceType, "AddPinhole",
            ("RemoteHost", ""),
            ("RemotePort", "0"),
            ("InternalClient", internalClient),
            ("InternalPort", port.ToString(CultureInfo.InvariantCulture)),
            ("Protocol", Udp.ToString(CultureInfo.InvariantCulture)),
            ("LeaseTime", leaseSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The SOAPAction header and envelope of an UpdatePinhole that renews
    /// <paramref name="uniqueId"/> for <paramref name="leaseSeconds"/>.</summary>
    public static (string SoapAction, string Body) UpdateRequest(string serviceType, int uniqueId, int leaseSeconds) =>
        Request(serviceType, "UpdatePinhole",
            ("UniqueID", uniqueId.ToString(CultureInfo.InvariantCulture)),
            ("NewLeaseTime", leaseSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The SOAPAction header and envelope of a DeletePinhole.</summary>
    public static (string SoapAction, string Body) DeleteRequest(string serviceType, int uniqueId) =>
        Request(serviceType, "DeletePinhole", ("UniqueID", uniqueId.ToString(CultureInfo.InvariantCulture)));

    /// <summary>A GetFirewallStatus answer's two flags. <c>Answered</c> is false when the text is
    /// not such an answer, a fault included.</summary>
    public static (bool Answered, bool Enabled, bool PinholesAllowed) StatusOf(string xml)
    {
        var doc = IgdAddress.Parse(xml);
        string? enabled = Value(doc, "FirewallEnabled");
        string? allowed = Value(doc, "InboundPinholeAllowed");
        return enabled == null || allowed == null
            ? (false, false, false)
            : (true, Flag(enabled), Flag(allowed));
    }

    /// <summary>The UniqueID an AddPinhole answer names, or null when it names none that fits the
    /// service's two-byte identifier.</summary>
    public static int? UniqueIdOf(string xml) =>
        int.TryParse(Value(IgdAddress.Parse(xml), "UniqueID"), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            && id is >= 0 and <= ushort.MaxValue
            ? id
            : null;

    /// <summary>A SOAP fault's UPnP error code and description, or (0, "") when the text is none.</summary>
    public static (int Code, string Description) FaultOf(string xml)
    {
        var doc = IgdAddress.Parse(xml);
        string code = Value(doc, "errorCode") ?? "";
        return int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
            ? (parsed, Value(doc, "errorDescription") ?? "")
            : (0, "");
    }

    /// <summary>Whether <paramref name="code"/> is a gateway's way of saying this caller may not
    /// open a pinhole, as against a pinhole that failed.</summary>
    public static bool IsRefusal(int code) => code is ActionNotAuthorized or InboundPinholeNotAllowed;

    /// <summary>Whether <paramref name="address"/> reads as a global unicast IPv6 address (2000::/3)
    /// in plain text. A link-local, unique-local or zoned address cannot be reached from outside,
    /// and a pinhole for one opens nothing.</summary>
    public static bool IsGlobalUnicast(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 45
            || address.Any(c => !(Uri.IsHexDigit(c) || c is ':' or '.')) || !address.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        string first = address[..address.IndexOf(':', StringComparison.Ordinal)];
        // A group below 0x1000 has fewer than four digits and lies outside 2000::/3 anyway.
        return first.Length == 4
            && int.TryParse(first, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int group)
            && (group & 0xE000) == 0x2000;
    }

    // One action on the service, its arguments escaped as element text.
    private static (string SoapAction, string Body) Request(string serviceType, string action, params (string Name, string Value)[] args)
    {
        XNamespace u = serviceType;
        var call = new XElement(u + action, new XAttribute(XNamespace.Xmlns + "u", serviceType),
            args.Select(a => new XElement(a.Name, a.Value)));
        string inner = call.ToString(SaveOptions.DisableFormatting);
        return (
            $"\"{serviceType}#{action}\"",
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + inner + "</s:Body></s:Envelope>");
    }

    private static string? Value(XDocument? doc, string name) =>
        doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static bool Flag(string value) => value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
