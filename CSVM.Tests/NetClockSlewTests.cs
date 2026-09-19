using System;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The guest's clock offset. It converges on the host's reading over a window instead of snapping,
/// and it never overshoots. Its rate is bounded, so host time stays close to real time while it
/// corrects. A reading too far out to hide is applied at once and counted.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetClockSlewTests
{
    [Fact]
    public void TheHandshakeOffsetIsInForceBeforeAnythingIsObserved()
    {
        var slew = new NetClockSlew(137.5);
        Assert.Equal(137.5, slew.Offset);
        Assert.True(slew.Settled);
        Assert.Equal(0, slew.Snaps);
        Assert.Equal(140.5, slew.HostTime(3.0), 9);
    }

    [Fact]
    public void ANonFiniteOffsetOrReadingIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetClockSlew(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetClockSlew(double.PositiveInfinity));
        var slew = new NetClockSlew(0.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => slew.Observe(double.NaN, 1.0));
    }

    [Fact]
    public void AFreshReadingIsWalkedToRatherThanWritten()
    {
        var slew = new NetClockSlew(10.0);
        slew.Observe(hostClock: 10.2, guestClock: 0.0);

        Assert.Equal(10.2, slew.Target, 9);
        Assert.Equal(10.0, slew.Offset, 9); // the frame the reading lands on moves nothing
        Assert.False(slew.Settled);
        Assert.True(slew.Rate > 0.0);
    }

    [Fact]
    public void TheWalkArrivesInsideTheStatedWindow()
    {
        var slew = new NetClockSlew(10.0);
        slew.Observe(10.1, 0.0);

        // An error the rate bound does not cap, so the window alone decides: exactly one window.
        Advance(slew, NetClockSlew.ConvergeSeconds, 120);

        Assert.True(slew.Settled, $"offset {slew.Offset} never reached {slew.Target}");
        Assert.Equal(10.1, slew.Offset, 9);
    }

    [Fact]
    public void TheWalkNeverPassesItsTarget()
    {
        var slew = new NetClockSlew(0.0);
        slew.Observe(0.05, 0.0);
        // One step far longer than the whole window: the overshoot would be enormous if it existed.
        slew.Advance(60.0);
        Assert.Equal(0.05, slew.Offset, 9);
        Assert.True(slew.Settled);
        Assert.Equal(0.0, slew.Rate); // arrived, so it stops pushing
    }

    [Fact]
    public void TheRateStaysInsideItsBound()
    {
        var slew = new NetClockSlew(0.0);
        // An error the window alone would eat at 2.4x real time.
        slew.Observe(NetClockSlew.ConvergeSeconds * NetClockSlew.MaxRateOffset * 24.0, 0.0);
        Assert.True(Math.Abs(slew.Rate) <= NetClockSlew.MaxRateOffset + 1e-12,
            $"rate {slew.Rate} is outside the {NetClockSlew.MaxRateOffset} bound");

        double moved = slew.Advance(0.5);
        Assert.True(Math.Abs(moved) <= NetClockSlew.MaxRateOffset * 0.5 + 1e-12,
            $"a 0.5 s frame moved the offset {moved} s");
    }

    [Fact]
    public void AnEarlyGuestClockWalksTheOffsetBackwards()
    {
        var slew = new NetClockSlew(10.0);
        slew.Observe(hostClock: 10.0, guestClock: 0.3); // the guest is ahead of where it thought

        Assert.True(slew.Rate < 0.0);
        // 0.3 s at the capped rate takes longer than one window, which is the bound doing its job.
        Advance(slew, 4.0, 240);
        Assert.Equal(9.7, slew.Offset, 9);
    }

    [Fact]
    public void AReadingTooFarOutToHideIsAppliedAtOnce()
    {
        var slew = new NetClockSlew(10.0);
        slew.Observe(10.0 + NetClockSlew.SnapSeconds + 1.0, 0.0);

        Assert.Equal(1, slew.Snaps);
        Assert.Equal(slew.Target, slew.Offset);
        Assert.True(slew.Settled);
        Assert.Equal(0.0, slew.Rate);
    }

    [Fact]
    public void AReadingInsideTheThresholdIsNotCountedASnap()
    {
        var slew = new NetClockSlew(10.0);
        slew.Observe(10.0 + NetClockSlew.SnapSeconds - 0.1, 0.0);
        Assert.Equal(0, slew.Snaps);
        Assert.False(slew.Settled);
    }

    [Fact]
    public void TheNewestReadingReplacesTheOneBeingWalkedTo()
    {
        var slew = new NetClockSlew(0.0);
        slew.Observe(0.2, 0.0);
        slew.Advance(0.8);
        double partway = slew.Offset;
        Assert.True(partway is > 0.05 and < 0.2, $"the walk stopped at {partway}");

        slew.Observe(0.05, 0.0); // the link improved; the old target is not finished first
        Assert.Equal(0.05, slew.Target, 9);
        Assert.True(slew.Rate < 0.0);
        Advance(slew, 4.0, 240);
        Assert.Equal(0.05, slew.Offset, 9);
    }

    [Fact]
    public void ASettledSlewIsFreeOfCharge()
    {
        var slew = new NetClockSlew(4.0);
        slew.Observe(4.0, 0.0);
        Assert.Equal(0.0, slew.Advance(1.0));
        Assert.Equal(4.0, slew.Offset);
    }

    // Counted steps rather than an accumulated clock. The total handed to the slew is then exactly
    // the window under test, and a rounding crumb cannot decide whether it arrived.
    private static void Advance(NetClockSlew slew, double seconds, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            slew.Advance(seconds / steps);
        }
    }
}
