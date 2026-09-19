using CSVM.Net;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The received history of one remote aircraft, off-engine. Samples go in with the time they
/// arrived at, and a read asks for a render time. So every case a real wire produces is scripted
/// here rather than waited for. Two samples straddling the read, a gap where one never came, a
/// delivery that overtook the one before it. A newest sample gone stale, and an empty buffer. The
/// three answers the reader reports are what an instrument counts, so each is asserted by name.
/// </summary>
[Trait("Tier", "Quick")]
public class RemotePoseBufferTests
{
    private const float Delay = RemotePoseBuffer.BufferDelaySeconds;
    private const float Cap = RemotePoseBuffer.ExtrapolationCapSeconds;

    // The velocity every scripted sample flies at, and the axis it flies along. One metre per
    // second down +X makes a position reading and an elapsed time the same number.
    private static readonly Vector3 Along = new(1f, 0f, 0f);

    [Fact]
    public void TwoSamplesStraddlingTheReadInterpolateBetweenThem()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        Assert.True(buffer.TrySample(0.5 + Delay, out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(0.5f, pose.Position.X, 3);
        Assert.Equal(0.5f, pose.Throttle, 3);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void AGapInTheStreamStillInterpolatesAcrossTheSamplesThatArrived()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(1, 0f), 0.0);
        // Sequence 2 never arrives; 3 does, a second later.
        buffer.Add(Sample(3, 2f), 2.0);

        Assert.True(buffer.TrySample(1.0 + Delay, out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(1f, pose.Position.X, 3);
    }

    [Fact]
    public void ASampleAtOrBelowTheNewestSequenceIsDropped()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(7, 0f), 0.0);
        buffer.Add(Sample(9, 2f), 1.0);

        Assert.False(buffer.Add(Sample(8, 99f), 1.5));
        Assert.False(buffer.Add(Sample(9, 99f), 1.5));
        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)9, buffer.NewestSequence);

        Assert.True(buffer.TrySample(1.0 + Delay, out var pose));
        Assert.Equal(2f, pose.Position.X, 3);
    }

    [Fact]
    public void TheSequenceWrapIsNotMistakenForAnOldSample()
    {
        var buffer = new RemotePoseBuffer();
        Assert.True(buffer.Add(Sample(65535, 0f), 0.0));
        Assert.True(buffer.Add(Sample(0, 1f), 1.0));
        Assert.True(buffer.Add(Sample(1, 2f), 2.0));

        Assert.Equal(3, buffer.Count);
        Assert.Equal((ushort)1, buffer.NewestSequence);
    }

    [Fact]
    public void AStaleNewestSampleIsFlownAlongItsVelocity()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        // Half the cap past the newest sample, so the answer is the sample plus half a cap of
        // its own velocity, and it says so.
        Assert.True(buffer.TrySample(1.0 + Delay + (Cap * 0.5), out var pose));
        Assert.Equal(RemotePoseFeed.Extrapolating, pose.Feed);
        Assert.Equal(1f + (Cap * 0.5f), pose.Position.X, 3);
    }

    [Fact]
    public void PastTheCapTheAnswerHoldsRatherThanFlyingFurther()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        Assert.True(buffer.TrySample(1.0 + Delay + (Cap * 4.0), out var pose));
        Assert.Equal(RemotePoseFeed.Starved, pose.Feed);
        Assert.Equal(1f + Cap, pose.Position.X, 3);
    }

    [Fact]
    public void AReadBeforeTheOldestSampleHoldsThatSample()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(1, 5f), 10.0);

        Assert.True(buffer.TrySample(1.0, out var pose));
        Assert.Equal(RemotePoseFeed.Starved, pose.Feed);
        Assert.Equal(5f, pose.Position.X, 3);
    }

    [Fact]
    public void AnEmptyBufferAnswersNothingAtAll()
    {
        var buffer = new RemotePoseBuffer();

        Assert.False(buffer.TrySample(1.0, out var pose));
        Assert.Equal(RemotePoseFeed.Starved, pose.Feed);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void TheBuffersOwnClockStampsArrivalsAndTimesReads()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Receive(Sample(1, 0f));
        for (int i = 0; i < 60; i++)
            buffer.Advance(1f / 60f);
        buffer.Receive(Sample(2, 1f));

        Assert.Equal(1.0, buffer.Now, 3);
        Assert.True(buffer.TrySample(out var pose));
        // The read is the delay behind Now, which is inside the pair just received.
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(1f - Delay, pose.Position.X, 2);
    }

    [Fact]
    public void ClearForgetsTheHistoryAndTheSequenceWithIt()
    {
        var buffer = new RemotePoseBuffer();
        buffer.Add(Sample(500, 0f), 0.0);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TrySample(1.0, out _));
        // The sequence went with it, so a fresh stream numbered from anywhere is taken.
        Assert.True(buffer.Add(Sample(3, 7f), 1.0));
    }

    [Fact]
    public void TheHistoryIsBoundedAndKeepsTheNewestSamples()
    {
        var buffer = new RemotePoseBuffer();
        for (int i = 0; i < RemotePoseBuffer.Capacity * 2; i++)
            buffer.Add(Sample((ushort)(i + 1), i), i);

        Assert.Equal(RemotePoseBuffer.Capacity, buffer.Count);
        // The oldest kept is one capacity back, and a read before it holds that one rather than
        // one of the samples the ring has dropped.
        Assert.True(buffer.TrySample(0.0, out var early));
        Assert.Equal(RemotePoseFeed.Starved, early.Feed);
        Assert.Equal((float)RemotePoseBuffer.Capacity, early.Position.X, 3);

        float newest = (RemotePoseBuffer.Capacity * 2) - 1;
        Assert.True(buffer.TrySample(newest + Delay + 0.1, out var late));
        Assert.Equal(RemotePoseFeed.Extrapolating, late.Feed);
        Assert.Equal(newest + 0.1f, late.Position.X, 3);
    }

    [Fact]
    public void AQuantisedAttitudeIsTakenAsGivenAndInterpolated()
    {
        var buffer = new RemotePoseBuffer();
        // What comes off the wire: four 16-bit fields, so the quaternion is near unit length and
        // not on it. A reader that demanded a normalised one would throw here.
        var level = new Quaternion(0f, 0f, 0f, 0.999f);
        var banked = new Quaternion(0f, 0.7069f, 0f, 0.7069f);
        buffer.Add(Sample(1, 0f) with { Attitude = level }, 0.0);
        buffer.Add(Sample(2, 1f) with { Attitude = banked }, 1.0);

        Assert.True(buffer.TrySample(0.5 + Delay, out var pose));
        Assert.Equal(1f, pose.Attitude.Length(), 3);
        Assert.True(pose.Attitude.Y > 0.3f && pose.Attitude.Y < 0.45f);
    }

    // One sample of an aeroplane one metre per second along +X, at x = position. The lever and
    // the stick carry that same number, so a read tells which sample it came from.
    private static AircraftStateMessage Sample(ushort sequence, float position) =>
        new(
            Seat: 1,
            Sequence: sequence,
            Position: new Vector3(position, 0f, 0f),
            Attitude: Quaternion.Identity,
            Velocity: Along,
            Throttle: position,
            Aileron: 0f,
            Elevator: 0f,
            Rudder: 0f,
            Nitro: false);
}
