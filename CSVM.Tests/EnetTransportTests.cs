using System;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The pure half of the ENet carrier: which ENet delivery each reliability class is carried
/// under. The mapping is asserted by the engine's own name for the mode, not by naming the type.
/// No Godot networking type then appears in a second file. The rest of the carrier needs a
/// socket and is the <c>enet-transport</c> engine suite's.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class EnetTransportTests
{
    [Theory]
    [InlineData(NetReliability.Unreliable, "Unreliable")]
    [InlineData(NetReliability.UnreliableSequenced, "UnreliableOrdered")]
    [InlineData(NetReliability.Reliable, "Reliable")]
    public void Each_reliability_class_names_the_enet_delivery_that_implements_it(
        NetReliability reliability, string mode)
    {
        Assert.Equal(mode, EnetTransport.ModeFor(reliability).ToString());
    }

    // Every seat's state and fire channel is one the connection negotiated, and no two of them,
    // nor the events channel, are the same one.
    [Fact]
    public void Every_seats_state_and_fire_channel_is_distinct_and_inside_the_negotiated_count()
    {
        var used = new System.Collections.Generic.HashSet<int> { NetChannels.Events };
        for (int seat = 0; seat < NetSeats.MaxPlayers; seat++)
        {
            Assert.True(used.Add(NetChannels.ForSeat(seat)), $"seat {seat}'s state channel is taken");
            Assert.True(used.Add(NetChannels.ForFire(seat)), $"seat {seat}'s fire channel is taken");
        }

        Assert.All(used, channel => Assert.InRange(channel, 0, EnetTransport.ChannelCount - 1));
        Assert.Equal(EnetTransport.ChannelCount - 1, NetChannels.ForFire(NetSeats.MaxPlayers - 1));
        Assert.Equal(NetChannels.Events, NetChannels.ForFire(NetSeats.MaxPlayers));
    }

    [Fact]
    public void A_class_outside_the_three_is_refused_rather_than_carried_as_something_else()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.ModeFor((NetReliability)9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void A_port_outside_the_range_is_refused_before_a_socket_is_opened(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.Host(port, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.Join("127.0.0.1", port));
    }

    [Fact]
    public void A_host_with_no_room_for_a_guest_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.Host(47099, 0));
    }

    // The wildcard host listens on IPv4's wildcard, the stable address and loopback, so an IPv6
    // reply leaves from the address a guest dials. IPv6's own wildcard is the bug it replaces.
    [Fact]
    public void A_wildcard_host_listens_on_ipv4s_wildcard_the_stable_address_and_loopback()
    {
        const string Stable = "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90";
        Assert.Equal(new[] { "0.0.0.0", Stable, "::1" }, EnetTransport.ListenAddresses(EnetTransport.Wildcard, Stable));
        Assert.Equal(new[] { "0.0.0.0", "::1" }, EnetTransport.ListenAddresses(EnetTransport.Wildcard, null));
        Assert.DoesNotContain(EnetTransport.Wildcard, EnetTransport.ListenAddresses(EnetTransport.Wildcard, Stable));
        Assert.DoesNotContain("::", EnetTransport.ListenAddresses(EnetTransport.Wildcard, Stable));

        // ABLE-TO-FAIL CONTROL: an explicit bind is kept as the only socket, stable address or not.
        Assert.Equal(new[] { "127.0.0.1" }, EnetTransport.ListenAddresses("127.0.0.1", Stable));
        Assert.Equal(new[] { Stable }, EnetTransport.ListenAddresses(Stable, null));
    }

    [Fact]
    public void A_join_with_no_address_is_refused()
    {
        Assert.Throws<ArgumentException>(() => EnetTransport.Join(" ", 47099));
    }
}
