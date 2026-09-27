using System.Linq;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// A gateway's external address read without the engine. These cover the ranges the internet
/// cannot reach and the text a direct question is made of. The description is the shape a
/// FRITZ!Box serves, trimmed to its services.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class IgdAddressTests
{
    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
        <device>
        <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
        <serviceList><service>
        <serviceType>urn:schemas-any-com:service:Any:1</serviceType>
        <controlURL>/igdupnp/control/any</controlURL>
        </service></serviceList>
        <deviceList><device>
        <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
        <deviceList><device>
        <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
        <serviceList>
        <service>
        <serviceType>urn:schemas-upnp-org:service:WANDSLLinkConfig:1</serviceType>
        <controlURL>/igdupnp/control/WANDSLLinkC1</controlURL>
        </service>
        <service>
        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
        <controlURL>/igdupnp/control/WANIPConn1</controlURL>
        </service>
        <service>
        <serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>
        <controlURL>http://192.168.178.1:49000/igdupnp/control/WANPPPConn1</controlURL>
        </service>
        </serviceList>
        </device></deviceList>
        </device></deviceList>
        </device>
        </root>
        """;

    private const string Answer = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
        <s:Body>
        <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
        <NewExternalIPAddress>100.72.5.9</NewExternalIPAddress>
        </u:GetExternalIPAddressResponse>
        </s:Body>
        </s:Envelope>
        """;

    [Theory]
    [InlineData("100.64.0.1", ExternalAddressKind.Shared)]
    [InlineData("100.127.255.254", ExternalAddressKind.Shared)]
    [InlineData("10.1.2.3", ExternalAddressKind.Private)]
    [InlineData("172.16.0.1", ExternalAddressKind.Private)]
    [InlineData("172.31.255.1", ExternalAddressKind.Private)]
    [InlineData("192.168.178.1", ExternalAddressKind.Private)]
    [InlineData("0.1.2.3", ExternalAddressKind.Reserved)]
    [InlineData("127.0.0.1", ExternalAddressKind.Reserved)]
    [InlineData("169.254.3.4", ExternalAddressKind.Reserved)]
    [InlineData("198.18.0.1", ExternalAddressKind.Reserved)]
    [InlineData("224.0.0.1", ExternalAddressKind.Reserved)]
    [InlineData("255.255.255.255", ExternalAddressKind.Reserved)]
    public void Ranges_the_internet_does_not_route_are_unreachable(string address, ExternalAddressKind kind)
    {
        Assert.Equal(kind, IgdAddress.Kind(address));
        Assert.True(IgdAddress.IsUnreachable(address));
    }

    // ABLE-TO-FAIL CONTROL: the neighbours of each range, and what is no IPv4 address at all.
    [Theory]
    [InlineData("100.63.255.255", ExternalAddressKind.Public)]
    [InlineData("100.128.0.1", ExternalAddressKind.Public)]
    [InlineData("172.15.0.1", ExternalAddressKind.Public)]
    [InlineData("172.32.0.1", ExternalAddressKind.Public)]
    [InlineData("11.0.0.1", ExternalAddressKind.Public)]
    [InlineData("203.0.113.7", ExternalAddressKind.Public)]
    [InlineData("", ExternalAddressKind.Unknown)]
    [InlineData("2001:db8::1", ExternalAddressKind.Unknown)]
    [InlineData("1.2.3", ExternalAddressKind.Unknown)]
    [InlineData("1.2.3.256", ExternalAddressKind.Unknown)]
    [InlineData("+1.2.3.4", ExternalAddressKind.Unknown)]
    public void Public_and_unparsed_addresses_are_not_unreachable(string address, ExternalAddressKind kind)
    {
        Assert.Equal(kind, IgdAddress.Kind(address));
        Assert.False(IgdAddress.IsUnreachable(address));
    }

    [Fact]
    public void The_description_names_both_connection_services_with_absolute_control_urls()
    {
        var found = IgdAddress.Connections(Description, "http://192.168.178.1:49000/igddesc.xml");

        Assert.Equal(
            new[]
            {
                "urn:schemas-upnp-org:service:WANIPConnection:1 http://192.168.178.1:49000/igdupnp/control/WANIPConn1",
                "urn:schemas-upnp-org:service:WANPPPConnection:1 http://192.168.178.1:49000/igdupnp/control/WANPPPConn1",
            },
            found.Select(f => $"{f.ServiceType} {f.Control}"));
        Assert.Empty(IgdAddress.Connections("<root", "http://192.168.178.1:49000/igddesc.xml"));
        Assert.Empty(IgdAddress.Connections(Description, "not a url"));
    }

    [Fact]
    public void The_answer_names_the_address_and_an_unset_one_is_none()
    {
        Assert.Equal("100.72.5.9", IgdAddress.ExternalAddressOf(Answer));
        Assert.Equal("", IgdAddress.ExternalAddressOf(Answer.Replace("100.72.5.9", "0.0.0.0", System.StringComparison.Ordinal)));
        Assert.Equal("", IgdAddress.ExternalAddressOf("<s:Envelope>"));
        Assert.Equal("", IgdAddress.ExternalAddressOf(""));

        var (action, body) = IgdAddress.ExternalAddressRequest("urn:schemas-upnp-org:service:WANIPConnection:1");
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"", action);
        Assert.Contains("<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", body, System.StringComparison.Ordinal);
    }
}
