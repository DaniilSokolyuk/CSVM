using System;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Steam build flag, from both sides of the define. Without it the carrier is ENet and the
/// stub throws its "not built" line at every way in. With it the selection moves and the stub
/// throws the other line. Both flavours run this file, which is what keeps the flag from rotting.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class SteamTransportTests
{
    private const string NotBuilt = "not built with the Steamworks SDK";

    [Fact]
    public void EveryWayIntoTheSteamCarrierThrowsUntilAnSdkIsLinked()
    {
        var host = Assert.Throws<InvalidOperationException>(() => SteamTransport.Host(47500, 4));
        var join = Assert.Throws<InvalidOperationException>(() => SteamTransport.Join("127.0.0.1", 47500));

        string arm = SteamTransport.SteamBuild
            ? "CSVM_STEAM is defined but no SDK is linked"
            : "this build has no CSVM_STEAM define";
        foreach (var thrown in new[] { host, join })
        {
            Assert.Contains(NotBuilt, thrown.Message, StringComparison.Ordinal);
            Assert.Contains(arm, thrown.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDefineIsWhatSelectsTheCarrier()
    {
        Assert.Equal(SteamTransport.SteamBuild, NetCarrier.UsesSteam);
        Assert.Equal(SteamTransport.SteamBuild ? "steam" : "enet", NetCarrier.Name);

        // The router door belongs to the direct-IP carrier, so a Steam build hands the menu door
        // none and the board shows no mapping.
        Assert.Equal(SteamTransport.SteamBuild, NetCarrier.PortMap == null);
        Assert.Equal(SteamTransport.SteamBuild, NetCarrier.PortUnmap == null);
    }
}
