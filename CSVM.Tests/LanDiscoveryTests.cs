using System;
using System.Collections.Generic;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The LAN discovery wire and its two ends over the in-process datagram network. A query and a
/// reply round trip, and anything truncated or foreign goes unanswered. A reply is never larger
/// than the query that asked for it.
/// </summary>
public class LanDiscoveryTests
{
    private static readonly SessionAdvertMessage Coop =
        new(NetSessionKind.CampaignCoop, 7, 2, "Zachary", NetSessionStatus.Waiting, 4);

    private static readonly NetBuildVersion Build = new(0, 7);

    [Fact]
    public void AQueryAndAReplyRoundTripAtTheSameSize()
    {
        var query = new byte[LanDiscovery.Size];
        Assert.Equal(LanDiscovery.Size, LanDiscovery.WriteQuery(query, 0xC0FFEE));
        Assert.True(LanDiscovery.TryReadQuery(query, out uint token));
        Assert.Equal(0xC0FFEEu, token);

        var reply = new byte[LanDiscovery.Size];
        Assert.Equal(LanDiscovery.Size, LanDiscovery.WriteReply(reply, token, 47500, Coop, Build));
        Assert.True(LanDiscovery.TryReadReply(reply, token, out int port, out var advert, out var version));
        Assert.Equal(47500, port);
        Assert.Equal(Coop, advert);
        Assert.Equal(Build, version);

        // ABLE-TO-FAIL CONTROL: a reply is not a query, a query is not a reply, and a reply to
        // another search's token is not this search's.
        Assert.False(LanDiscovery.TryReadQuery(reply, out _));
        Assert.False(LanDiscovery.TryReadReply(query, token, out _, out _, out _));
        Assert.False(LanDiscovery.TryReadReply(reply, token + 1, out _, out _, out _));
    }

    [Fact]
    public void AReplyCarriesTheBuildVersionAndAnUnknownOneRoundTripsAsUnknown()
    {
        // The datagram grew by the version's four bytes, and the query is padded to match.
        Assert.Equal(48, LanDiscovery.Size);
        Assert.Equal(2, LanDiscovery.Version);

        var reply = new byte[LanDiscovery.Size];
        LanDiscovery.WriteReply(reply, 3, 47500, Coop, NetBuildVersion.Unknown);
        Assert.True(LanDiscovery.TryReadReply(reply, 3, out _, out _, out var unknown));
        Assert.False(unknown.Known);

        // ABLE-TO-FAIL CONTROL: a different minor reads back as that minor, not as this build's.
        LanDiscovery.WriteReply(reply, 3, 47500, Coop, new NetBuildVersion(0, 6));
        Assert.True(LanDiscovery.TryReadReply(reply, 3, out _, out _, out var older));
        Assert.Equal(new NetBuildVersion(0, 6), older);
        Assert.NotEqual(Build, older);
    }

    [Fact]
    public void ATruncatedOrForeignDatagramIsNeverRead()
    {
        var query = new byte[LanDiscovery.Size];
        LanDiscovery.WriteQuery(query, 5);
        Assert.False(LanDiscovery.TryReadQuery(query.AsSpan(0, LanDiscovery.Size - 1), out _));
        Assert.False(LanDiscovery.TryReadQuery(new byte[LanDiscovery.Size + 1], out _));

        var foreign = (byte[])query.Clone();
        foreign[0] = (byte)'X';
        Assert.False(LanDiscovery.TryReadQuery(foreign, out _));

        var later = (byte[])query.Clone();
        later[4] = LanDiscovery.Version + 1;
        Assert.False(LanDiscovery.TryReadQuery(later, out _));

        var reply = new byte[LanDiscovery.Size];
        LanDiscovery.WriteReply(reply, 5, 47500, Coop, Build);
        Assert.False(LanDiscovery.TryReadReply(reply.AsSpan(0, LanDiscovery.Size - 4), 5, out _, out _, out _));
    }

    [Fact]
    public void AResponderAnswersEachQueryWithNoMoreBytesThanItWasSentAndDropsTheRest()
    {
        var lan = new LoopbackLan();
        using var responder = new LanResponder(lan.Bind("10.0.0.2", LanDiscovery.Port), Build);
        using var asker = lan.Bind("10.0.0.3", 0);

        var query = new byte[LanDiscovery.Size];
        int sent = LanDiscovery.WriteQuery(query, 9);
        asker.Send(LoopbackLan.Broadcast, LanDiscovery.Port, query.AsSpan(0, sent));
        asker.Send("10.0.0.2", LanDiscovery.Port, new byte[] { 1, 2, 3 });
        responder.Poll(Coop, 47500);

        Assert.Equal(1, responder.Answered);
        byte[]? answer = asker.Receive(out string from, out int fromPort);
        Assert.NotNull(answer);
        Assert.True(answer!.Length <= sent);
        Assert.Equal("10.0.0.2", from);
        Assert.Equal(LanDiscovery.Port, fromPort);
        Assert.True(LanDiscovery.TryReadReply(answer, 9, out int port, out var advert, out var version));
        Assert.Equal(47500, port);
        Assert.Equal("Zachary", advert.Host);
        Assert.Equal(Build, version);
        Assert.Null(asker.Receive(out _, out _));
    }

    [Fact]
    public void ASearchHearsEveryAnsweringDoorAndForgetsOneSilentForAWholeRound()
    {
        var lan = new LoopbackLan();
        var first = new LanResponder(lan.Bind("10.0.0.2", LanDiscovery.Port));
        using var second = new LanResponder(lan.Bind("10.0.0.4", LanDiscovery.Port));
        using var search = new LanSearch(lan.Bind("10.0.0.3", 0), LoopbackLan.Broadcast, LanDiscovery.Port, new Random(3));

        search.Ask();
        first.Poll(Coop, 47500);
        second.Poll(Coop with { Host = "Nathan" }, 47510);
        search.Poll();
        Assert.Equal(2, search.Games.Count);
        Assert.Equal(new LanGame("10.0.0.4", 47510, Coop with { Host = "Nathan" }), search.Games[1]);

        // The first door closes. It survives the round after its last answer, then goes.
        first.Dispose();
        search.Ask();
        second.Poll(Coop, 47510);
        search.Poll();
        Assert.Equal(2, search.Games.Count);
        search.Ask();
        second.Poll(Coop, 47510);
        search.Poll();
        Assert.Single(search.Games);
        Assert.Equal("10.0.0.4", search.Games[0].Address);
    }

    [Fact]
    public void TheLoopbackLanRefusesATakenPortAsARealBindDoes()
    {
        var lan = new LoopbackLan();
        using var taken = lan.Bind("127.0.0.1", LanDiscovery.Port);
        Assert.Throws<InvalidOperationException>(() => lan.Bind("*", LanDiscovery.Port));
    }

    [Fact]
    public void ADirectedBroadcastSetsEveryHostBitOfTheNetwork()
    {
        Assert.Equal("192.168.178.255", LanBroadcast.Directed("192.168.178.26", "255.255.255.0"));
        Assert.Equal("172.19.239.255", LanBroadcast.Directed("172.19.224.1", "255.255.240.0"));
        Assert.Equal("169.254.255.255", LanBroadcast.Directed("169.254.123.4", "255.255.0.0"));
        Assert.Equal("10.255.255.255", LanBroadcast.Directed("10.1.2.3", "255.0.0.0"));

        // ABLE-TO-FAIL CONTROL: what is not a dotted IPv4 quad has no broadcast.
        Assert.Null(LanBroadcast.Directed("192.168.178", "255.255.255.0"));
        Assert.Null(LanBroadcast.Directed("192.168.178.256", "255.255.255.0"));
        Assert.Null(LanBroadcast.Directed("fe80::1", "255.255.255.0"));
    }

    [Fact]
    public void TheTargetsAreTheLimitedBroadcastThenEachNetworkOnce()
    {
        // The guest machine the search failed on: Ethernet, two unplugged adapters on one
        // link-local network, the WSL switch, a second Ethernet address and the loopback.
        var targets = LanBroadcast.Targets(new[]
        {
            ("192.168.178.26", "255.255.255.0"),
            ("169.254.123.4", "255.255.0.0"),
            ("169.254.183.49", "255.255.0.0"),
            ("172.19.224.1", "255.255.240.0"),
            ("192.168.178.40", "255.255.255.0"),
            ("127.0.0.1", "255.0.0.0"),
            ("10.8.0.6", "255.255.255.255"),
            ("not an address", "255.255.255.0"),
        });

        Assert.Equal(
            new[] { "255.255.255.255", "192.168.178.255", "169.254.255.255", "172.19.239.255" },
            targets);
        Assert.Equal(new[] { LanBroadcast.Limited }, LanBroadcast.Targets(Array.Empty<(string, string)>()));
    }

    [Fact]
    public void ASearchAsksAtEveryTargetEachRoundAndSoHearsAHostOnlyADirectedBroadcastReaches()
    {
        var guestNetworks = new[] { ("192.168.178.26", "255.255.255.0"), ("172.19.224.1", "255.255.240.0") };
        var socket = new MultiHomedLan("192.168.178.255");
        using var search = new LanSearch(socket, () => LanBroadcast.Targets(guestNetworks), LanDiscovery.Port, new Random(5));

        search.Ask();
        search.Poll();
        var game = Assert.Single(search.Games);
        Assert.Equal(MultiHomedLan.Host, game.Address);
        Assert.Equal(new[] { "255.255.255.255", "192.168.178.255", "172.19.239.255" }, socket.Sent);
        Assert.All(socket.Sizes, size => Assert.Equal(LanDiscovery.Size, size));

        // The seam is asked afresh each round, so the next round sends to every target again.
        search.Ask();
        search.Poll();
        Assert.Single(search.Games);
        Assert.Equal(6, socket.Sent.Count);

        // ABLE-TO-FAIL CONTROL: asked at the limited broadcast alone, as before, the same host is
        // never heard, since that broadcast leaves by another adapter.
        var alone = new MultiHomedLan("192.168.178.255");
        using var limited = new LanSearch(alone, LoopbackLan.Broadcast, LanDiscovery.Port, new Random(5));
        limited.Ask();
        limited.Poll();
        Assert.Empty(limited.Games);
        Assert.Equal(new[] { "255.255.255.255" }, alone.Sent);
    }

    [Fact]
    public void ADoorSearchingTheBroadcastAddressAlsoAsksAtItsNetworks()
    {
        var socket = new MultiHomedLan("192.168.178.255");
        var door = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("no host here"),
            (_, _) => throw new InvalidOperationException("no join here"),
            lan: (_, _) => socket)
        {
            LanNetworks = () => new[] { ("192.168.178.26", "255.255.255.0") },
        };

        door.Search();
        Assert.Equal(new[] { "255.255.255.255", "192.168.178.255" }, socket.Sent);

        // ABLE-TO-FAIL CONTROL: a door a suite pointed elsewhere asks there alone.
        door.StopSearch();
        socket.Sent.Clear();
        door.SearchAddress = "127.0.0.1";
        door.Search();
        Assert.Equal(new[] { "127.0.0.1" }, socket.Sent);
    }

    // A guest on several adapters with the host on one of them. The limited broadcast leaves by
    // another adapter, so only a query sent to the reachable address is answered.
    private sealed class MultiHomedLan : ILanSocket
    {
        public const string Host = "192.168.178.35";

        private readonly string _reaches;
        private readonly Queue<byte[]> _inbox = new();

        public MultiHomedLan(string reaches) => _reaches = reaches;

        public List<string> Sent { get; } = new();

        public List<int> Sizes { get; } = new();

        public void Send(string address, int port, ReadOnlySpan<byte> datagram)
        {
            Sent.Add(address);
            Sizes.Add(datagram.Length);
            if (address == _reaches && LanDiscovery.TryReadQuery(datagram, out uint token))
            {
                var reply = new byte[LanDiscovery.Size];
                LanDiscovery.WriteReply(reply, token, 47500, Coop, Build);
                _inbox.Enqueue(reply);
            }
        }

        public byte[]? Receive(out string address, out int port)
        {
            address = Host;
            port = LanDiscovery.Port;
            return _inbox.Count == 0 ? null : _inbox.Dequeue();
        }

        public void Dispose()
        {
        }
    }
}
