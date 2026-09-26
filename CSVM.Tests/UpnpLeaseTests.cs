using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The router mapping's lease rules over a fake gateway, the port memory over a scratch directory,
/// and the door's renewal thread over a fake mapper. Nothing here reaches a router.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class UpnpLeaseTests
{
    private const int Port = 47500;
    private const int Earlier = 47501;

    // The able-to-fail control for the lease: a permanent lease, the one this rule replaced, fails.
    [Fact]
    public void A_mapping_asks_a_finite_lease_and_reports_it()
    {
        var gateway = new FakeGateway();

        var result = UpnpLease.Map(gateway, Port, "CSVM");

        Assert.True(result.IsMapped);
        Assert.Equal(UpnpLease.LeaseSeconds, result.LeaseSeconds);
        Assert.Equal($"add {Port} {UpnpLease.LeaseSeconds}", gateway.Calls[^1]);
        Assert.InRange(gateway.Leases[0], UpnpLease.MinLeaseSeconds, UpnpLease.MaxLeaseSeconds);
        Assert.Equal(FakeGateway.External, result.ExternalAddress);
    }

    [Fact]
    public void The_renewal_and_every_retry_fall_before_the_lease_runs_out()
    {
        var granted = new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, Port, "", "mapped", UpnpLease.LeaseSeconds);
        var failed = new UpnpPortMapResult(UpnpPortMapOutcome.TimedOut, Port, "", "timed out");
        var search = TimeSpan.FromMilliseconds(UpnpPortMap.DiscoverTimeoutMs);

        var renewal = UpnpLease.NextRenewal(granted, UpnpLease.LeaseSeconds);
        var retry = UpnpLease.NextRenewal(failed, UpnpLease.LeaseSeconds);

        // Three retries after the renewal itself failed, each paying a whole gateway search.
        var lastTry = renewal + (3 * retry) + (4 * search);
        Assert.True(lastTry < TimeSpan.FromSeconds(UpnpLease.LeaseSeconds), $"the last retry lands at {lastTry}");
        Assert.True(renewal > TimeSpan.Zero && retry > TimeSpan.Zero);
    }

    [Fact]
    public void Nothing_is_renewed_without_a_finite_lease()
    {
        var permanent = new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, Port, "", "mapped, permanent lease only");
        var refused = new UpnpPortMapResult(UpnpPortMapOutcome.Refused, Port, "", "refused");

        Assert.Equal(Timeout.InfiniteTimeSpan, UpnpLease.NextRenewal(permanent, 0));
        Assert.Equal(Timeout.InfiniteTimeSpan, UpnpLease.NextRenewal(refused, 0));
    }

    [Fact]
    public void A_gateway_that_keeps_only_permanent_leases_gets_one()
    {
        var gateway = new FakeGateway { PermanentOnly = true };

        var result = UpnpLease.Map(gateway, Port, "CSVM");

        Assert.True(result.IsMapped);
        Assert.Equal(0, result.LeaseSeconds);
        Assert.Equal(new[] { UpnpLease.LeaseSeconds, 0 }, gateway.Leases);
        Assert.Contains("permanent", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_refusal_is_reported_and_not_retried_as_permanent()
    {
        var gateway = new FakeGateway { Refuse = true };

        var result = UpnpLease.Map(gateway, Port, "CSVM");

        Assert.Equal(UpnpPortMapOutcome.Refused, result.Outcome);
        Assert.Single(gateway.Leases);
    }

    [Fact]
    public void A_stale_mapping_on_the_port_is_deleted_before_the_add()
    {
        var gateway = new FakeGateway();

        UpnpLease.Map(gateway, Port, "CSVM", rememberedPort: Port);

        Assert.Equal(new[] { "discover", "external", $"delete {Port}", $"add {Port} {UpnpLease.LeaseSeconds}" }, gateway.Calls);
    }

    [Fact]
    public void A_changed_port_deletes_the_remembered_one_too_and_nothing_else()
    {
        var gateway = new FakeGateway();

        UpnpLease.Map(gateway, Port, "CSVM", rememberedPort: Earlier);

        Assert.Equal(
            new[] { "discover", "external", $"delete {Earlier}", $"delete {Port}", $"add {Port} {UpnpLease.LeaseSeconds}" },
            gateway.Calls);
    }

    [Fact]
    public void A_renewal_only_adds()
    {
        var gateway = new FakeGateway();

        UpnpLease.Map(gateway, Port, "CSVM", rememberedPort: Port, renewing: true);

        Assert.Equal(new[] { "discover", "external", $"add {Port} {UpnpLease.LeaseSeconds}" }, gateway.Calls);
    }

    [Fact]
    public void No_gateway_means_no_delete_and_no_add()
    {
        var gateway = new FakeGateway { Missing = true };

        var result = UpnpLease.Map(gateway, Port, "CSVM", rememberedPort: Earlier);

        Assert.Equal(UpnpPortMapOutcome.NoGateway, result.Outcome);
        Assert.Equal(new[] { "discover" }, gateway.Calls);
    }

    // The FRITZ!Box on a carrier's NAT: it answers, Godot calls it invalid, and it reports 100.64/10.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gateway_on_a_shared_address_reports_no_public_address_and_maps_nothing(bool unusable)
    {
        var gateway = new FakeGateway { Unusable = unusable, Reports = "100.72.5.9" };

        var result = UpnpLease.Map(gateway, Port, "CSVM", rememberedPort: Earlier);

        Assert.Equal(UpnpPortMapOutcome.NoPublicAddress, result.Outcome);
        Assert.Equal("100.72.5.9", result.ExternalAddress);
        Assert.Contains("100.72.5.9", result.Detail, StringComparison.Ordinal);
        Assert.Contains("carrier-grade NAT", result.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { "discover", "external" }, gateway.Calls);
        Assert.False(result.IsMapped);
    }

    [Fact]
    public void A_private_external_address_is_no_public_address_too()
    {
        var result = UpnpLease.Map(new FakeGateway { Reports = "192.168.1.1" }, Port, "CSVM");

        Assert.Equal(UpnpPortMapOutcome.NoPublicAddress, result.Outcome);
        Assert.Contains("private", result.Detail, StringComparison.Ordinal);
    }

    // ABLE-TO-FAIL CONTROL for the two above: the same gateways on a public address map as before.
    // One that will not say its address is given the benefit of the doubt.
    [Fact]
    public void A_public_or_unsaid_external_address_lets_the_mapping_proceed()
    {
        var reporting = new FakeGateway { Reports = "198.51.100.20" };
        Assert.True(UpnpLease.Map(reporting, Port, "CSVM").IsMapped);
        Assert.Contains($"add {Port} {UpnpLease.LeaseSeconds}", reporting.Calls);

        Assert.True(UpnpLease.Map(new FakeGateway { Reports = "" }, Port, "CSVM").IsMapped);

        var unusable = UpnpLease.Map(new FakeGateway { Unusable = true, Reports = "198.51.100.20" }, Port, "CSVM");
        Assert.Equal(UpnpPortMapOutcome.Refused, unusable.Outcome);
        Assert.Equal("198.51.100.20", unusable.ExternalAddress);
    }

    [Fact]
    public void The_memory_keeps_one_port_and_forgets_it_only_by_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csvm-tests", "upnp-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new UpnpPortMemory(dir);
            Assert.Equal(UpnpLease.NoPort, memory.Recall());

            memory.Remember(Port);
            Assert.Equal(Port, new UpnpPortMemory(dir).Recall());

            memory.Forget(Earlier);
            Assert.Equal(Port, memory.Recall());

            memory.Forget(Port);
            Assert.Equal(UpnpLease.NoPort, memory.Recall());

            File.WriteAllText(Path.Combine(dir, UpnpPortMemory.FileName), "not a port");
            Assert.Equal(UpnpLease.NoPort, memory.Recall());
            Assert.Throws<ArgumentException>(() => new UpnpPortMemory("relative"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // The renewal runs on the door's own thread and outlives the board, since a launch stops the
    // board's steps. It stops before the unmap, so nothing renews after the port is given back.
    [Fact]
    public void The_door_renews_a_finite_lease_on_its_own_thread_until_it_closes()
    {
        int asked = 0;
        var unmapped = new List<int>();
        var door = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("this door joins nothing"),
            port =>
            {
                Interlocked.Increment(ref asked);
                return new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, "", "mapped", LeaseSeconds: 1);
            },
            unmapped.Add);

        door.OpenHost(1);
        Assert.NotNull(door.BuildLaunch());
        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref asked) < 3 && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
        }

        Assert.True(Volatile.Read(ref asked) >= 3, $"asked {asked} times in {waited.Elapsed}");
        door.Close();
        int atClose = Volatile.Read(ref asked);
        Thread.Sleep(700);

        Assert.Equal(atClose, Volatile.Read(ref asked));
        Assert.Equal(new[] { NetPlayFeature.DefaultPort }, unmapped);
    }

    // A gateway that answers from its switches and writes down every call, in order.
    private sealed class FakeGateway : IUpnpGateway
    {
        public const string External = "203.0.113.7";

        public bool Missing { get; init; }

        public bool PermanentOnly { get; init; }

        public bool Refuse { get; init; }

        // A gateway that answered the search but that the engine will not map through.
        public bool Unusable { get; init; }

        public string Reports { get; init; } = External;

        public List<string> Calls { get; } = new();

        public List<int> Leases { get; } = new();

        public UpnpReply Discover()
        {
            Calls.Add("discover");
            return Missing ? new UpnpReply(UpnpPortMapOutcome.NoGateway, "no gateway")
                : Unusable ? new UpnpReply(UpnpPortMapOutcome.Refused, "unusable")
                : new UpnpReply(UpnpPortMapOutcome.Mapped, "found");
        }

        public UpnpReply Add(int port, string description, int leaseSeconds)
        {
            Calls.Add($"add {port} {leaseSeconds}");
            Leases.Add(leaseSeconds);
            if (Refuse)
            {
                return new UpnpReply(UpnpPortMapOutcome.Refused, "ConflictWithOtherMapping");
            }

            return PermanentOnly && leaseSeconds != 0
                ? new UpnpReply(UpnpPortMapOutcome.Refused, "OnlyPermanentLeaseSupported", PermanentLeaseOnly: true)
                : new UpnpReply(UpnpPortMapOutcome.Mapped, "Success");
        }

        public bool Delete(int port)
        {
            Calls.Add($"delete {port}");
            return true;
        }

        public string ExternalAddress()
        {
            Calls.Add("external");
            return Reports;
        }
    }
}
