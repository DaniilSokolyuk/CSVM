using System;
using CSVM.Net;
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

    [Fact]
    public void AQueryAndAReplyRoundTripAtTheSameSize()
    {
        var query = new byte[LanDiscovery.Size];
        Assert.Equal(LanDiscovery.Size, LanDiscovery.WriteQuery(query, 0xC0FFEE));
        Assert.True(LanDiscovery.TryReadQuery(query, out uint token));
        Assert.Equal(0xC0FFEEu, token);

        var reply = new byte[LanDiscovery.Size];
        Assert.Equal(LanDiscovery.Size, LanDiscovery.WriteReply(reply, token, 47500, Coop));
        Assert.True(LanDiscovery.TryReadReply(reply, token, out int port, out var advert));
        Assert.Equal(47500, port);
        Assert.Equal(Coop, advert);

        // ABLE-TO-FAIL CONTROL: a reply is not a query, a query is not a reply, and a reply to
        // another search's token is not this search's.
        Assert.False(LanDiscovery.TryReadQuery(reply, out _));
        Assert.False(LanDiscovery.TryReadReply(query, token, out _, out _));
        Assert.False(LanDiscovery.TryReadReply(reply, token + 1, out _, out _));
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
        LanDiscovery.WriteReply(reply, 5, 47500, Coop);
        Assert.False(LanDiscovery.TryReadReply(reply.AsSpan(0, LanDiscovery.Size - 4), 5, out _, out _));
    }

    [Fact]
    public void AResponderAnswersEachQueryWithNoMoreBytesThanItWasSentAndDropsTheRest()
    {
        var lan = new LoopbackLan();
        using var responder = new LanResponder(lan.Bind("10.0.0.2", LanDiscovery.Port));
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
        Assert.True(LanDiscovery.TryReadReply(answer, 9, out int port, out var advert));
        Assert.Equal(47500, port);
        Assert.Equal("Zachary", advert.Host);
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
}
