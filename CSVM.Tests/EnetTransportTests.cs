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

    [Fact]
    public void A_join_with_no_address_is_refused()
    {
        Assert.Throws<ArgumentException>(() => EnetTransport.Join(" ", 47099));
    }
}
