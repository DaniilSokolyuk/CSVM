using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The IPv6 pinhole's rules over a fake gateway, and its memory over a scratch directory. The SOAP
/// and SSDP text is read against the shapes a FRITZ!Box 7590 serves. The door's pinhole thread
/// runs over a fake opener. Nothing here reaches a router.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class UpnpPinholeTests
{
    private const int Port = 47500;
    private const string Address = "2001:db8:232:6640:feb1:ff80:9ed7:dd90";
    private const string Control = "http://192.168.178.1:49000/igd2upnp/control/WANIPv6Firewall1";

    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // The FRITZ!Box's IGD v2 description, cut to the path down to the firewall service. The v1
    // description it also serves names the same service at the same control URL.
    private static readonly string Igd2Description =
        "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>"
        + "<device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:2</deviceType>"
        + "<serviceList><service><serviceType>urn:schemas-any-com:service:Any:1</serviceType><controlURL>/igd2upnp/control/any</controlURL></service></serviceList>"
        + "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:2</deviceType>"
        + "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:2</deviceType><serviceList>"
        + "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType><controlURL>/igd2upnp/control/WANIPConn2</controlURL></service>"
        + "<service><serviceType>urn:schemas-upnp-org:service:WANIPv6FirewallControl:1</serviceType>"
        + "<serviceId>urn:upnp-org:serviceId:WANIPv6Firewall1</serviceId><controlURL>/igd2upnp/control/WANIPv6Firewall1</controlURL>"
        + "<SCPDURL>/igd2ipv6fwcSCPD.xml</SCPDURL></service>"
        + "</serviceList></device></deviceList></device></deviceList><presentationURL>http://192.168.178.1</presentationURL></device></root>";

    // The FRITZ!Box's own GetFirewallStatus answer, with pinholes not allowed for the asking machine.
    private static readonly string StatusAnswer =
        "<?xml version=\"1.0\"?>\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n"
        + "<s:Body>\n<u:GetFirewallStatusResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPv6FirewallControl:1\">\n"
        + "<FirewallEnabled>1</FirewallEnabled>\n<InboundPinholeAllowed>0</InboundPinholeAllowed>\n"
        + "</u:GetFirewallStatusResponse>\n</s:Body>\n</s:Envelope>";

    [Fact]
    public void A_status_that_allows_no_pinhole_asks_for_no_add()
    {
        var gateway = new FakeGateway { Allowed = false };

        var result = UpnpPinhole.Open(gateway, Address, Port, null, null, Now);

        Assert.Equal(UpnpPinholeOutcome.Disallowed, result.Outcome);
        Assert.Equal(new[] { "discover", "status" }, gateway.Calls);
        Assert.Null(result.Lease);
    }

    // ABLE-TO-FAIL CONTROL for the one above: the same gateway allowing pinholes gets the add.
    [Fact]
    public void An_allowed_pinhole_is_added_for_the_address_on_a_finite_lease()
    {
        var gateway = new FakeGateway();

        var result = UpnpPinhole.Open(gateway, Address, Port, null, null, Now);

        Assert.True(result.IsOpen);
        Assert.Equal(new[] { "discover", "status", $"add {Address} {Port} {UpnpLease.LeaseSeconds}" }, gateway.Calls);
        Assert.Equal(new PinholeLease(FakeGateway.FirstId, Port, Address, Now.AddSeconds(UpnpLease.LeaseSeconds)), result.Lease);
        Assert.Equal(UpnpLease.LeaseSeconds, result.LeaseSeconds);
    }

    [Fact]
    public void A_held_pinhole_is_renewed_by_its_id_and_deleted_on_close()
    {
        var gateway = new FakeGateway();
        var opened = UpnpPinhole.Open(gateway, Address, Port, null, null, Now).Lease!.Value;
        gateway.Calls.Clear();

        var renewed = UpnpPinhole.Open(gateway, Address, Port, opened, opened, Now.AddMinutes(30));
        Assert.True(renewed.IsOpen);
        Assert.Equal(opened.Id, renewed.Lease!.Value.Id);
        Assert.Equal(new[] { "discover", $"update {opened.Id} {UpnpLease.LeaseSeconds}" }, gateway.Calls);

        gateway.Calls.Clear();
        Assert.True(UpnpPinhole.Close(gateway, renewed.Lease!.Value));
        Assert.Equal(new[] { "discover", $"delete {opened.Id}" }, gateway.Calls);
    }

    // A router that restarted forgot the id: the renewal fails and a fresh add replaces it.
    [Fact]
    public void A_renewal_the_router_refuses_adds_afresh()
    {
        var gateway = new FakeGateway { ForgetsIds = true };
        var held = new PinholeLease(9, Port, Address, Now.AddMinutes(30));

        var result = UpnpPinhole.Open(gateway, Address, Port, held, held, Now);

        Assert.True(result.IsOpen);
        Assert.Equal(FakeGateway.FirstId, result.Lease!.Value.Id);
        Assert.Equal(
            new[] { "discover", "update 9 3600", "status", "delete 9", $"add {Address} {Port} {UpnpLease.LeaseSeconds}" },
            gateway.Calls);
    }

    [Fact]
    public void A_crashed_runs_pinhole_is_deleted_before_the_add_while_its_lease_runs()
    {
        var gateway = new FakeGateway();
        var stale = new PinholeLease(41, Port, Address, Now.AddMinutes(20));

        var result = UpnpPinhole.Open(gateway, Address, Port, null, stale, Now);

        Assert.True(result.StaleCleared);
        Assert.Equal(new[] { "discover", "status", "delete 41", $"add {Address} {Port} {UpnpLease.LeaseSeconds}" }, gateway.Calls);
    }

    // ABLE-TO-FAIL CONTROL for the one above: past its lease the id may be another program's.
    [Fact]
    public void A_remembered_pinhole_past_its_lease_is_left_alone()
    {
        var gateway = new FakeGateway();
        var lapsed = new PinholeLease(41, Port, Address, Now.AddSeconds(-1));

        var result = UpnpPinhole.Open(gateway, Address, Port, null, lapsed, Now);

        Assert.True(result.IsOpen);
        Assert.False(result.StaleCleared);
        Assert.DoesNotContain("delete 41", gateway.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1%12")]
    [InlineData("192.168.178.20")]
    [InlineData("2001:db8::1</InternalClient>")]
    public void No_global_address_means_no_gateway_call(string? address)
    {
        var gateway = new FakeGateway();

        var result = UpnpPinhole.Open(gateway, address, Port, null, null, Now);

        Assert.Equal(UpnpPinholeOutcome.NoAddress, result.Outcome);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public void No_service_and_a_disabled_firewall_each_stop_before_the_add()
    {
        var missing = new FakeGateway { Missing = true };
        Assert.Equal(UpnpPinholeOutcome.NoService, UpnpPinhole.Open(missing, Address, Port, null, null, Now).Outcome);
        Assert.Equal(new[] { "discover" }, missing.Calls);

        var open = new FakeGateway { Enabled = false };
        Assert.Equal(UpnpPinholeOutcome.FirewallOff, UpnpPinhole.Open(open, Address, Port, null, null, Now).Outcome);
        Assert.Equal(new[] { "discover", "status" }, open.Calls);
    }

    [Fact]
    public void An_add_the_router_refuses_is_reported_as_its_outcome()
    {
        var gateway = new FakeGateway { RefuseAdd = true };

        var result = UpnpPinhole.Open(gateway, Address, Port, null, null, Now);

        Assert.Equal(UpnpPinholeOutcome.Disallowed, result.Outcome);
        Assert.Contains("606", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_memory_keeps_one_pinhole_and_forgets_it_only_by_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csvm-tests", "upnp-pinhole-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new UpnpPinholeMemory(dir);
            Assert.Null(memory.Recall());

            var lease = new PinholeLease(7, Port, Address, Now);
            memory.Remember(lease);
            Assert.Equal(lease, new UpnpPinholeMemory(dir).Recall());

            memory.Forget(8);
            Assert.Equal(lease, memory.Recall());

            memory.Forget(7);
            Assert.Null(memory.Recall());

            File.WriteAllText(Path.Combine(dir, UpnpPinholeMemory.FileName), "7 47500 fe80::1 1893499200");
            Assert.Null(memory.Recall());
            Assert.Throws<ArgumentException>(() => new UpnpPinholeMemory("relative"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_fritzbox_description_names_its_firewall_control_url()
    {
        var found = IgdPinhole.FirewallControls(Igd2Description, "http://192.168.178.1:49000/igd2desc.xml");

        var (service, control) = Assert.Single(found);
        Assert.Equal(IgdPinhole.ServiceType, service);
        Assert.Equal(new Uri(Control), control);
        Assert.Empty(IgdAddress.Connections(Igd2Description, "http://192.168.178.1:49000/igd2desc.xml")
            .Where(c => c.ServiceType.Contains("Firewall", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_ssdp_answer_names_its_description_only_for_the_firewall_search()
    {
        string fritz = "HTTP/1.1 200 OK\r\nCache-Control: max-age=1800\r\nLocation: http://192.168.178.1:49000/igd2desc.xml\r\n"
            + "Server: FRITZ!Box 7590 UPnP/1.0 AVM FRITZ!Box 7590\r\nExt: \r\nST: urn:schemas-upnp-org:service:WANIPv6FirewallControl:1\r\n\r\n";
        string bridge = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.178.20:80/description.xml\r\nST: upnp:rootdevice\r\n\r\n";

        Assert.Equal(new Uri("http://192.168.178.1:49000/igd2desc.xml"), IgdPinhole.LocationOf(fritz));
        Assert.Null(IgdPinhole.LocationOf(bridge));
        Assert.Null(IgdPinhole.LocationOf(""));

        string search = IgdPinhole.SearchRequest(1);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", search, StringComparison.Ordinal);
        Assert.Contains($"ST: {IgdPinhole.ServiceType}\r\n", search, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", search, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fritzbox_status_answer_reads_as_enabled_and_not_allowed()
    {
        Assert.Equal((true, true, false), IgdPinhole.StatusOf(StatusAnswer));
        Assert.Equal((false, false, false), IgdPinhole.StatusOf(Fault(501, "Action Failed")));
        Assert.Equal((false, false, false), IgdPinhole.StatusOf("not xml"));
    }

    [Fact]
    public void The_add_request_carries_the_services_argument_order_with_a_wildcard_remote()
    {
        var (action, body) = IgdPinhole.AddRequest(IgdPinhole.ServiceType, Address, Port, 3600);

        Assert.Equal($"\"{IgdPinhole.ServiceType}#AddPinhole\"", action);
        var call = XDocument.Parse(body).Descendants().Single(e => e.Name.LocalName == "AddPinhole");
        Assert.Equal(IgdPinhole.ServiceType, call.Name.NamespaceName);
        Assert.Equal(
            new[] { ("RemoteHost", ""), ("RemotePort", "0"), ("InternalClient", Address), ("InternalPort", "47500"), ("Protocol", "17"), ("LeaseTime", "3600") },
            call.Elements().Select(e => (e.Name.LocalName, e.Value)).ToArray());
        Assert.All(call.Elements(), e => Assert.Equal("", e.Name.NamespaceName));
    }

    [Fact]
    public void The_update_and_delete_requests_name_the_id()
    {
        var update = XDocument.Parse(IgdPinhole.UpdateRequest(IgdPinhole.ServiceType, 12, 3600).Body)
            .Descendants().Single(e => e.Name.LocalName == "UpdatePinhole");
        Assert.Equal(new[] { ("UniqueID", "12"), ("NewLeaseTime", "3600") }, update.Elements().Select(e => (e.Name.LocalName, e.Value)).ToArray());

        var (action, body) = IgdPinhole.DeleteRequest(IgdPinhole.ServiceType, 12);
        Assert.EndsWith("#DeletePinhole\"", action, StringComparison.Ordinal);
        Assert.Equal("12", XDocument.Parse(body).Descendants().Single(e => e.Name.LocalName == "UniqueID").Value);
    }

    [Fact]
    public void An_add_answer_names_its_id_and_a_fault_names_its_code()
    {
        string added = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
            + $"<u:AddPinholeResponse xmlns:u=\"{IgdPinhole.ServiceType}\"><UniqueID>3</UniqueID></u:AddPinholeResponse></s:Body></s:Envelope>";

        Assert.Equal(3, IgdPinhole.UniqueIdOf(added));
        Assert.Null(IgdPinhole.UniqueIdOf(Fault(606, "Action not authorized")));
        Assert.Equal((606, "Action not authorized"), IgdPinhole.FaultOf(Fault(606, "Action not authorized")));
        Assert.Equal((0, ""), IgdPinhole.FaultOf(added));
        Assert.True(IgdPinhole.IsRefusal(606));
        Assert.True(IgdPinhole.IsRefusal(703));
        Assert.False(IgdPinhole.IsRefusal(501));
    }

    [Theory]
    [InlineData("2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", true)]
    [InlineData("3fff::1", true)]
    [InlineData("4000::1", false)]
    [InlineData("200::1", false)]
    [InlineData("fe80::feb1:ff80:9ed7:dd90", false)]
    [InlineData("2001:db8::1%12", false)]
    [InlineData("2001:db8::1 ", false)]
    public void Only_a_global_unicast_address_takes_a_pinhole(string address, bool global)
    {
        Assert.Equal(global, IgdPinhole.IsGlobalUnicast(address));
    }

    [Fact]
    public void Every_pinhole_outcome_has_its_own_status_clause()
    {
        var clauses = Enum.GetValues<UpnpPinholeOutcome>()
            .Select(o => CoopDoorText.PinholeStatus(new UpnpPinholeResult(o, Port, Address, "detail")))
            .ToList();

        Assert.Equal(clauses.Count, clauses.Distinct().Count());
        Assert.All(clauses, c => Assert.StartsWith("IPv6: ", c, StringComparison.Ordinal));
        Assert.Contains("allow", CoopDoorText.PinholeStatus(new UpnpPinholeResult(UpnpPinholeOutcome.Disallowed, Port, Address, "")), StringComparison.Ordinal);
    }

    // The door holds the pinhole on its own thread beside the mapping, and closes it by port.
    [Fact]
    public void The_door_renews_an_open_pinhole_and_closes_it_with_the_door()
    {
        int asked = 0;
        var closed = new List<int>();
        var door = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("this door joins nothing"))
        {
            OpenPinhole = port =>
            {
                Interlocked.Increment(ref asked);
                return new UpnpPinholeResult(UpnpPinholeOutcome.Opened, port, Address, "opened", LeaseSeconds: 1);
            },
            ClosePinhole = closed.Add,
        };

        door.OpenHost(1);
        var waited = Stopwatch.StartNew();
        while ((Volatile.Read(ref asked) < 3 || door.Pinhole == null) && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            door.Step(0.016);
            Thread.Sleep(10);
        }

        Assert.True(Volatile.Read(ref asked) >= 3, $"asked {asked} times in {waited.Elapsed}");
        Assert.True(door.Pinhole!.Value.IsOpen);
        door.Close();
        int atClose = Volatile.Read(ref asked);
        Thread.Sleep(700);

        Assert.Equal(atClose, Volatile.Read(ref asked));
        Assert.Equal(new[] { NetPlayFeature.DefaultPort }, closed);
        Assert.Null(door.Pinhole);
    }

    // ABLE-TO-FAIL CONTROL for the one above: a pinhole that never opened is not closed.
    [Fact]
    public void The_door_closes_no_pinhole_it_was_refused()
    {
        var closed = new List<int>();
        var door = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("this door joins nothing"))
        {
            OpenPinhole = port => new UpnpPinholeResult(UpnpPinholeOutcome.Disallowed, port, Address, "not allowed"),
            ClosePinhole = closed.Add,
        };

        door.OpenHost(1);
        var waited = Stopwatch.StartNew();
        while (door.Pinhole == null && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            door.Step(0.016);
            Thread.Sleep(1);
        }

        Assert.Equal(UpnpPinholeOutcome.Disallowed, door.Pinhole!.Value.Outcome);
        door.Close();
        Assert.Empty(closed);
    }

    private static string Fault(int code, string description) =>
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault>"
        + "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
        + $"<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode><errorDescription>{description}</errorDescription></UPnPError>"
        + "</detail></s:Fault></s:Body></s:Envelope>";

    // A firewall service that answers from its switches and writes down every call, in order.
    private sealed class FakeGateway : IPinholeGateway
    {
        public const int FirstId = 1;

        private int _next = FirstId;

        public bool Missing { get; init; }

        public bool Enabled { get; init; } = true;

        public bool Allowed { get; init; } = true;

        public bool RefuseAdd { get; init; }

        // A router that restarted and no longer knows any UniqueID.
        public bool ForgetsIds { get; init; }

        public List<string> Calls { get; } = new();

        public PinholeReply Discover()
        {
            Calls.Add("discover");
            return Missing
                ? new PinholeReply(UpnpPinholeOutcome.NoService, "no service")
                : new PinholeReply(UpnpPinholeOutcome.Opened, "found");
        }

        public (bool Answered, bool Enabled, bool PinholesAllowed, string Detail) Status()
        {
            Calls.Add("status");
            return (true, Enabled, Allowed, "200");
        }

        public PinholeReply Add(string address, int port, int leaseSeconds)
        {
            Calls.Add($"add {address} {port} {leaseSeconds}");
            return RefuseAdd
                ? new PinholeReply(UpnpPinholeOutcome.Disallowed, "error 606 Action not authorized")
                : new PinholeReply(UpnpPinholeOutcome.Opened, "added", _next++);
        }

        public PinholeReply Update(int id, int leaseSeconds)
        {
            Calls.Add($"update {id} {leaseSeconds}");
            return ForgetsIds
                ? new PinholeReply(UpnpPinholeOutcome.Failed, "error 704 NoSuchEntry")
                : new PinholeReply(UpnpPinholeOutcome.Opened, "done", id);
        }

        public PinholeReply Delete(int id)
        {
            Calls.Add($"delete {id}");
            return new PinholeReply(UpnpPinholeOutcome.Opened, "done", id);
        }
    }
}
