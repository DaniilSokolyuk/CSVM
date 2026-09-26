using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace CSVM.Net;

/// <summary>What kind of IPv4 address a gateway reported as its external one. Only a public one can
/// be reached from the internet, so any other kind means no router mapping helps.</summary>
public enum ExternalAddressKind
{
    /// <summary>Nothing reported, or not an IPv4 address.</summary>
    Unknown,

    /// <summary>An address the internet routes to this line.</summary>
    Public,

    /// <summary>A private range (10/8, 172.16/12, 192.168/16): the router sits behind another router.</summary>
    Private,

    /// <summary>The shared range 100.64/10, which a provider's carrier-grade NAT hands its lines.</summary>
    Shared,

    /// <summary>Any other range the internet does not route, such as 0/8, loopback or link-local.</summary>
    Reserved,
}

/// <summary>
/// An internet gateway's external address, read without the engine. It holds the kind of address
/// and the description and SOAP text a direct question is made of. The question itself is
/// <see cref="UpnpPortMap"/>'s, since only it may open a connection.
/// Every parse returns empty rather than throwing, since a gateway's text is untrusted.
/// </summary>
public static class IgdAddress
{
    // The two connection services an IGD's external address is asked of.
    private static readonly string[] ConnectionServices = { ":service:WANIPConnection:", ":service:WANPPPConnection:" };

    /// <summary>The kind of <paramref name="address"/>. The documentation ranges (RFC 5737) read as
    /// public, since no line is given one and the fixtures stand them for a public address.</summary>
    public static ExternalAddressKind Kind(string address)
    {
        if (!TryOctets(address, out byte[] o))
        {
            return ExternalAddressKind.Unknown;
        }

        if (o[0] == 10 || (o[0] == 172 && (o[1] & 0xF0) == 16) || (o[0] == 192 && o[1] == 168))
        {
            return ExternalAddressKind.Private;
        }

        if (o[0] == 100 && (o[1] & 0xC0) == 64)
        {
            return ExternalAddressKind.Shared;
        }

        bool reserved = o[0] is 0 or 127 or >= 224
            || (o[0] == 169 && o[1] == 254)
            || (o[0] == 192 && o[1] == 0 && o[2] == 0)
            || (o[0] == 198 && (o[1] & 0xFE) == 18);
        return reserved ? ExternalAddressKind.Reserved : ExternalAddressKind.Public;
    }

    /// <summary>Whether <paramref name="address"/> is one the internet cannot reach, so a mapping
    /// through the gateway that reported it cannot make this host reachable.</summary>
    public static bool IsUnreachable(string address) =>
        Kind(address) is ExternalAddressKind.Private or ExternalAddressKind.Shared or ExternalAddressKind.Reserved;

    /// <summary>The kind as a board and a log line word it, such as "shared (carrier-grade NAT)".</summary>
    public static string Word(ExternalAddressKind kind) => kind switch
    {
        ExternalAddressKind.Shared => "shared (carrier-grade NAT)",
        ExternalAddressKind.Private => "private",
        ExternalAddressKind.Reserved => "reserved",
        ExternalAddressKind.Public => "public",
        _ => "unknown",
    };

    /// <summary>Every WANIPConnection and WANPPPConnection service in the device description
    /// <paramref name="xml"/>, as its service type and absolute control URL, in document order.
    /// Relative URLs resolve against the description's URLBase, else <paramref name="descriptionUrl"/>.</summary>
    public static IReadOnlyList<(string ServiceType, Uri Control)> Connections(string xml, string descriptionUrl)
    {
        var found = new List<(string, Uri)>();
        if (Parse(xml) is not { } doc || !Uri.TryCreate(descriptionUrl, UriKind.Absolute, out var described))
        {
            return found;
        }

        var root = doc.Root!;
        string urlBase = root.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim() ?? "";
        var basis = Uri.TryCreate(urlBase, UriKind.Absolute, out var declared) ? declared : described;
        foreach (var service in root.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            string type = Child(service, "serviceType");
            string control = Child(service, "controlURL");
            if (ConnectionServices.Any(s => type.Contains(s, StringComparison.Ordinal))
                && control.Length > 0 && Uri.TryCreate(basis, control, out var url))
            {
                found.Add((type, url));
            }
        }

        return found;
    }

    /// <summary>The SOAPAction header and the envelope that ask <paramref name="serviceType"/> for
    /// its external address.</summary>
    public static (string SoapAction, string Body) ExternalAddressRequest(string serviceType) => (
        $"\"{serviceType}#GetExternalIPAddress\"",
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
        + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
        + $"<u:GetExternalIPAddress xmlns:u=\"{serviceType}\"></u:GetExternalIPAddress></s:Body></s:Envelope>");

    /// <summary>The address in a GetExternalIPAddress answer, or "" when it names none. An unset
    /// 0.0.0.0, which a connection that is down reports, is none.</summary>
    public static string ExternalAddressOf(string xml)
    {
        string address = Parse(xml)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value.Trim() ?? "";
        return address == "0.0.0.0" ? "" : address;
    }

    private static string Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    // A gateway's text may carry a DTD; refusing it keeps a hostile one from expanding entities.
    private static XDocument? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new System.IO.StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static bool TryOctets(string address, out byte[] octets)
    {
        octets = new byte[4];
        string[] parts = (address ?? "").Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3
                || !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
            {
                return false;
            }
        }

        return true;
    }
}
