using System.Collections.Generic;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Which of the machine's addresses a host names to its guests and binds for them. The candidates
/// are data here, so each rule is tested against the address it must refuse.
/// </summary>
public class HostAddressTests
{
    private const string Stable = "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90";
    private const string Temporary = "2a04:6ec0:232:6640:d164:886f:63c8:48d2";

    [Fact]
    public void TheStableGlobalAddressIsChosenOverTheTemporaryOneInFrontOfIt()
    {
        var candidates = new[]
        {
            new Ipv6Candidate(Temporary, Temporary: true, Preferred: true),
            new Ipv6Candidate(Stable, Temporary: false, Preferred: true),
        };
        Assert.Equal(Stable, HostAddress.Choose(candidates));

        // ABLE-TO-FAIL CONTROL: the same address marked temporary is refused, leaving nothing.
        candidates[1] = candidates[1] with { Temporary = true };
        Assert.Null(HostAddress.Choose(candidates));
    }

    [Theory]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1c2b:3a4d:5e6f:7081")]
    [InlineData("fe80::1c2b:3a4d:5e6f:7081%12")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.1.2")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("ff02::1")]
    [InlineData("not an address")]
    [InlineData("192.168.1.2")]
    public void AnAddressOutsideGlobalUnicastIsNeverChosen(string address)
    {
        Assert.Null(HostAddress.Choose(new[] { new Ipv6Candidate(address, false, true) }));
        Assert.Equal(Stable, HostAddress.Choose(new[]
        {
            new Ipv6Candidate(address, false, true),
            new Ipv6Candidate(Stable, false, true),
        }));
    }

    [Fact]
    public void ADeprecatedOrTentativeAddressIsNotChosenAndTheFirstEligibleWins()
    {
        const string Second = "2a04:6ec0:232:6640::2";
        Assert.Null(HostAddress.Choose(new[] { new Ipv6Candidate(Stable, false, Preferred: false) }));
        Assert.Equal(Second, HostAddress.Choose(new[]
        {
            new Ipv6Candidate(Stable, false, Preferred: false),
            new Ipv6Candidate(Second, false, true),
            new Ipv6Candidate("2a04:6ec0:232:6640::3", false, true),
        }));
    }

    [Fact]
    public void AZoneIsDroppedFromTheChosenAddress()
    {
        Assert.Equal(Stable, HostAddress.Choose(new[] { new Ipv6Candidate(Stable + "%7", false, true) }));
    }

    [Fact]
    public void TheLanAddressIsThePrivateOneOnAnAdapterWithAGateway()
    {
        var candidates = new List<Ipv4Candidate>
        {
            new("172.28.160.1", HasGateway: false),
            new("84.12.3.4", HasGateway: true),
            new("192.168.178.20", HasGateway: true),
        };
        Assert.Equal("192.168.178.20", HostAddress.ChooseLan(candidates));

        // ABLE-TO-FAIL CONTROL: without the gateway the virtual switch's address comes first.
        candidates[2] = candidates[2] with { HasGateway = false };
        Assert.Equal("172.28.160.1", HostAddress.ChooseLan(candidates));

        Assert.Null(HostAddress.ChooseLan(new[] { new Ipv4Candidate("84.12.3.4", true), new Ipv4Candidate("169.254.3.4", true) }));
        Assert.Equal("10.1.2.3", HostAddress.ChooseLan(new[] { new Ipv4Candidate("10.1.2.3", true) }));
        Assert.Null(HostAddress.ChooseLan(new[] { new Ipv4Candidate("172.32.0.1", true) }));
    }

    [Fact]
    public void LinuxsAddressTableGivesTheTemporaryAndDeprecatedFlags()
    {
        const string Table =
            "2a046ec002326640feb1ff809ed7dd90 02 40 00 00     eth0\n"
            + "2a046ec002326640d164886f63c848d2 02 40 00 01     eth0\n"
            + "2a046ec0023266400000000000000002 02 40 00 20     eth0\n"
            + "2a046ec0023266400000000000000003 02 40 00 c0     eth0\n"
            + "fe800000000000001c2b3a4d5e6f7081 02 40 20 80     eth0\n"
            + "00000000000000000000000000000001 01 80 10 80       lo\n"
            + "garbage line\n";
        var parsed = HostAddress.ParseLinuxTable(Table);

        Assert.Equal(6, parsed.Count);
        Assert.Equal(new Ipv6Candidate(Stable, false, true), parsed[0]);
        Assert.Equal(new Ipv6Candidate(Temporary, true, true), parsed[1]);
        Assert.False(parsed[2].Preferred);
        Assert.False(parsed[3].Preferred);
        Assert.True(parsed[4].Preferred);
        Assert.Equal(Stable, HostAddress.Choose(parsed));

        // ABLE-TO-FAIL CONTROL: with the stable line gone, nothing in the table qualifies.
        Assert.Null(HostAddress.Choose(HostAddress.ParseLinuxTable(Table.Substring(Table.IndexOf('\n') + 1))));
    }

    [Fact]
    public void ReadingTheSystemNeverThrows()
    {
        var read = HostAddress.Ipv6Addresses();
        Assert.NotNull(read);
        string? stable = HostAddress.StableGlobalIPv6();
        Assert.True(stable == null || HostAddress.Choose(new[] { new Ipv6Candidate(stable, false, true) }) == stable);
        _ = HostAddress.LanIPv4();
    }
}
