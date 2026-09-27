using System;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The build version two peers compare before they play: MAJOR.MINOR parsed off the SemVer string,
/// patch ignored. An unreadable version plays only with another unreadable one.
/// </summary>
[Trait("Tier", "Quick")]
public class NetBuildVersionTests
{
    [Theory]
    [InlineData("0.7.2", 0, 7)]
    [InlineData("0.7", 0, 7)]
    [InlineData("v0.7.0-rc.1", 0, 7)]
    [InlineData(" 1.12.3+build.5 ", 1, 12)]
    public void ASemVerStringParsesToItsMajorAndMinor(string text, int major, int minor)
    {
        var version = NetBuildVersion.Parse(text);
        Assert.True(version.Known);
        Assert.Equal(new NetBuildVersion(major, minor), version);
        Assert.Equal($"{major}.{minor}", version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("a.b")]
    [InlineData("0.7.x")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.2")]
    [InlineData("65535.0")]
    public void AnythingElseIsUnknown(string? text)
    {
        var version = NetBuildVersion.Parse(text);
        Assert.False(version.Known);
        Assert.Equal(NetBuildVersion.Unknown, version);
        Assert.Equal(NetBuildVersion.UnknownText, version.ToString());
    }

    [Fact]
    public void BuildsThatDifferOnlyInThePatchPlayTogether()
    {
        Assert.True(NetBuildVersion.Parse("0.7.0").PlaysWith(NetBuildVersion.Parse("0.7.9")));

        // ABLE-TO-FAIL CONTROL: a minor or a major apart is refused, both ways round.
        Assert.False(NetBuildVersion.Parse("0.7.0").PlaysWith(NetBuildVersion.Parse("0.6.0")));
        Assert.False(NetBuildVersion.Parse("0.6.0").PlaysWith(NetBuildVersion.Parse("0.7.0")));
        Assert.False(NetBuildVersion.Parse("1.7.0").PlaysWith(NetBuildVersion.Parse("0.7.0")));
    }

    [Fact]
    public void AnUnknownBuildPlaysOnlyWithAnotherUnknownBuild()
    {
        Assert.True(NetBuildVersion.Unknown.PlaysWith(NetBuildVersion.Parse("unknown")));

        // ABLE-TO-FAIL CONTROL: unknown against any known version is refused, both ways round,
        // including 0.0, which is what an unknown's zero fields would compare equal to.
        Assert.False(NetBuildVersion.Unknown.PlaysWith(new NetBuildVersion(0, 0)));
        Assert.False(new NetBuildVersion(0, 0).PlaysWith(NetBuildVersion.Unknown));
        Assert.False(NetBuildVersion.Parse("0.7.1").PlaysWith(NetBuildVersion.Unknown));
    }

    [Fact]
    public void TheWireFormRoundTripsAndRefusesAHalfUnknownVersion()
    {
        Span<byte> wire = stackalloc byte[NetBuildVersion.WireBytes];
        foreach (var sent in new[] { new NetBuildVersion(0, 7), new NetBuildVersion(0, 0), NetBuildVersion.Unknown })
        {
            sent.Write(wire);
            Assert.True(NetBuildVersion.TryRead(wire, out var got));
            Assert.Equal(sent, got);
        }

        // ABLE-TO-FAIL CONTROL: one word at the unknown value and the other not is no version a
        // writer sends. A short span is no version at all.
        Assert.True(NetBuildVersion.TryFromWords(0xFFFF, 0xFFFF, out _));
        Assert.False(NetBuildVersion.TryFromWords(0xFFFF, 7, out _));
        Assert.False(NetBuildVersion.TryFromWords(0, 0xFFFF, out _));
        Assert.False(NetBuildVersion.TryRead(wire[..3], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetBuildVersion(0xFFFF, 0));
    }
}
