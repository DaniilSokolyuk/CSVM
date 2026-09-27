using CSVM.Net;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The received history of one remote aircraft, off-engine. Samples go in with the time they
/// arrived at, and a read asks for a time on the sender's timeline, which the sequence sets. So
/// every case a real wire produces is scripted here rather than waited for. Two samples straddling
/// the read, a gap where one never came, a delivery that overtook the one before it. A newest
/// sample gone stale, an empty buffer, and the playout clock that walks the timeline.
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
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        Assert.True(buffer.TrySample(1.5, out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(0.5f, pose.Position.X, 3);
        Assert.Equal(0.5f, pose.Throttle, 3);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void AGapInTheStreamStillInterpolatesAcrossTheSamplesThatArrived()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        // Sequence 2 never arrives; 3 does, two seconds of the sender's later.
        buffer.Add(Sample(3, 2f), 2.0);

        Assert.True(buffer.TrySample(2.0, out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(1f, pose.Position.X, 3);
    }

    [Fact]
    public void ALateArrivalIsPlacedByItsSequenceAndNotByWhenItLanded()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);
        // A second and a half late. Stamped by arrival it would stretch the pair before it.
        buffer.Add(Sample(3, 2f), 3.5);

        Assert.True(buffer.TrySample(2.5, out var pose));
        Assert.Equal(1.5f, pose.Position.X, 3);
    }

    [Fact]
    public void ASampleAtOrBelowTheNewestSequenceIsDropped()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(7, 0f), 0.0);
        buffer.Add(Sample(9, 2f), 1.0);

        Assert.False(buffer.Add(Sample(8, 99f), 1.5));
        Assert.False(buffer.Add(Sample(9, 99f), 1.5));
        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)9, buffer.NewestSequence);

        Assert.True(buffer.TrySample(9.0, out var pose));
        Assert.Equal(2f, pose.Position.X, 3);
    }

    [Fact]
    public void TheSequenceWrapIsNotMistakenForAnOldSample()
    {
        var buffer = OneSecondSamples();
        Assert.True(buffer.Add(Sample(65535, 0f), 0.0));
        Assert.True(buffer.Add(Sample(0, 1f), 1.0));
        Assert.True(buffer.Add(Sample(1, 2f), 2.0));

        Assert.Equal(3, buffer.Count);
        Assert.Equal((ushort)1, buffer.NewestSequence);
        // The timeline runs on through the wrap rather than folding back to zero.
        Assert.True(buffer.TrySample(65535.5, out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(0.5f, pose.Position.X, 3);
    }

    [Fact]
    public void AStaleNewestSampleIsFlownAlongItsVelocity()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        // Half the cap past the newest sample, so the answer is the sample plus half a cap of
        // its own velocity, and it says so.
        Assert.True(buffer.TrySample(2.0 + (Cap * 0.5), out var pose));
        Assert.Equal(RemotePoseFeed.Extrapolating, pose.Feed);
        Assert.Equal(1f + (Cap * 0.5f), pose.Position.X, 3);
    }

    [Fact]
    public void PastTheCapTheAnswerHoldsRatherThanFlyingFurther()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);

        Assert.True(buffer.TrySample(2.0 + (Cap * 4.0), out var pose));
        Assert.Equal(RemotePoseFeed.Starved, pose.Feed);
        Assert.Equal(1f + Cap, pose.Position.X, 3);
    }

    [Fact]
    public void TheOwnersPlayoutStopsAtTheCapWhenTheStreamStops()
    {
        var buffer = OneSecondSamples();
        buffer.Receive(Sample(1, 0f));
        for (int i = 0; i < 600; i++)
            buffer.Advance(1f / 60f);

        Assert.Equal(1.0 + Cap, buffer.PlayoutTime, 6);
        Assert.True(buffer.TrySample(out var pose));
        Assert.Equal(RemotePoseFeed.Starved, pose.Feed);
        Assert.Equal(Cap, pose.Position.X, 3);
    }

    [Fact]
    public void AReadBeforeTheOldestSampleHoldsThatSample()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(10, 5f), 10.0);

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
    public void ThePlayoutRunsTheDelayBehindTheNewestArrival()
    {
        // The sender's own cadence, one metre a second, over a lag-free link: the answer is where
        // the owner was one delay ago.
        var buffer = new RemotePoseBuffer();
        ushort sequence = 0;
        for (int step = 0; step < 180; step++)
        {
            if (step % AircraftStateCadence.SendStepInterval == 0)
            {
                buffer.Receive(Sample(sequence, sequence * AircraftStateCadence.SampleSeconds));
                sequence++;
            }

            buffer.Advance(1f / 60f);
        }

        Assert.Equal(3.0, buffer.Now, 3);
        Assert.True(buffer.TrySample(out var pose));
        Assert.Equal(RemotePoseFeed.Interpolating, pose.Feed);
        Assert.Equal(3f - Delay, pose.Position.X, 2);
        Assert.Equal(1.0, buffer.PlayoutRate, 3);
    }

    [Fact]
    public void ASenderClockAtHalfThisOnesRateIsFittedAndFlownSmoothly()
    {
        // The sender's simulation runs at half this machine's clock, so each sample describes
        // half as much time as passes between arrivals. The playout must walk at half speed.
        var buffer = new RemotePoseBuffer();
        ushort sequence = 0;
        float? before = null;
        float slowest = float.MaxValue, fastest = 0f;
        for (int step = 0; step < 900; step++)
        {
            if (step % (AircraftStateCadence.SendStepInterval * 2) == 0)
            {
                buffer.Receive(Sample(sequence, sequence * AircraftStateCadence.SampleSeconds));
                sequence++;
            }

            buffer.Advance(1f / 60f);
            Assert.True(buffer.TrySample(out var pose));
            if (step >= 600 && before is { } x)
            {
                float speed = (pose.Position.X - x) * 60f;
                slowest = Mathf.Min(slowest, speed);
                fastest = Mathf.Max(fastest, speed);
            }

            before = pose.Position.X;
        }

        Assert.Equal(0.5, buffer.PlayoutRate, 2);
        Assert.InRange(slowest, 0.48f, 0.52f);
        Assert.InRange(fastest, 0.48f, 0.52f);
    }

    [Fact]
    public void AStreamThatResumesFarAheadReanchorsThePlayout()
    {
        var buffer = OneSecondSamples();
        buffer.Receive(Sample(1, 0f));
        buffer.Advance(1f);
        buffer.Receive(Sample(2, 1f));
        // The sender's timeline moves on ten seconds between two arrivals one second apart.
        buffer.Advance(1f);
        buffer.Receive(Sample(12, 11f));

        Assert.Equal(1, buffer.Resyncs);
        Assert.Equal(12.0 - Delay, buffer.PlayoutTime, 6);
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
    public void ARespawnKeepsThePlayoutClockRunning()
    {
        // A respawn empties the samples, but the sender's sequence runs on through it. Anchoring
        // afresh on the first arrival after it would move the playout by that arrival's jitter.
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Advance(1f);
        buffer.Add(Sample(2, 1f), 1.0);
        double before = buffer.PlayoutTime;
        buffer.Clear();
        buffer.Advance(1f);
        buffer.Add(Sample(3, 50f), 2.5);  // late, and at the new spawn

        Assert.Equal(before + 1.0, buffer.PlayoutTime, 6);
        Assert.Equal(0, buffer.Resyncs);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void TheHistoryIsBoundedAndKeepsTheNewestSamples()
    {
        var buffer = OneSecondSamples();
        for (int i = 0; i < RemotePoseBuffer.Capacity * 2; i++)
            buffer.Add(Sample((ushort)(i + 1), i), i);

        Assert.Equal(RemotePoseBuffer.Capacity, buffer.Count);
        // The oldest kept is one capacity back, and a read before it holds that one rather than
        // one of the samples the ring has dropped.
        Assert.True(buffer.TrySample(0.0, out var early));
        Assert.Equal(RemotePoseFeed.Starved, early.Feed);
        Assert.Equal((float)RemotePoseBuffer.Capacity, early.Position.X, 3);

        float newest = (RemotePoseBuffer.Capacity * 2) - 1;
        Assert.True(buffer.TrySample(newest + 1.0 + 0.1, out var late));
        Assert.Equal(RemotePoseFeed.Extrapolating, late.Feed);
        Assert.Equal(newest + 0.1f, late.Position.X, 3);
    }

    [Fact]
    public void AQuantisedAttitudeIsTakenAsGivenAndInterpolated()
    {
        var buffer = OneSecondSamples();
        // What comes off the wire: four 16-bit fields, so the quaternion is near unit length and
        // not on it. A reader that demanded a normalised one would throw here.
        var level = new Quaternion(0f, 0f, 0f, 0.999f);
        var banked = new Quaternion(0f, 0.7069f, 0f, 0.7069f);
        buffer.Add(Sample(1, 0f) with { Attitude = level }, 0.0);
        buffer.Add(Sample(2, 1f) with { Attitude = banked }, 1.0);

        Assert.True(buffer.TrySample(1.5, out var pose));
        Assert.Equal(1f, pose.Attitude.Length(), 3);
        Assert.True(pose.Attitude.Y > 0.3f && pose.Attitude.Y < 0.45f);
    }

    [Fact]
    public void TheTallyCountsStaleSamplesAndOnlyTheOwnersReadsByFeed()
    {
        var buffer = OneSecondSamples();
        buffer.Receive(Sample(1, 0f));
        buffer.Receive(Sample(1, 0f));
        buffer.Advance(1f);
        buffer.Receive(Sample(2, 1f));
        buffer.TrySample(out _);
        buffer.TrySample(buffer.Now, out _);
        buffer.Advance(2f);
        buffer.TrySample(out _);

        var tally = buffer.Tally;
        Assert.Equal(2, tally.Accepted);
        Assert.Equal(1, tally.Stale);
        // The explicit-time read is a probe and counts nothing; the owner's two reads do.
        Assert.Equal(1, tally.Interpolating);
        Assert.Equal(1, tally.Starved);
        Assert.Equal(2, tally.Answers);

        // A respawn keeps the tally; only a reset zeroes it.
        buffer.Clear();
        Assert.Equal(2, buffer.Tally.Accepted);
        buffer.ResetTally();
        Assert.Equal(default, buffer.Tally);
    }

    [Fact]
    public void TheExtrapolationErrorIsHowFarASampleLandsFromTheOneBeforeItsVelocity()
    {
        // One second per sample, so a sequence step is a second of flight at one metre a second.
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        buffer.Add(Sample(2, 1f), 1.0);   // exactly where predicted
        buffer.Add(Sample(4, 3.5f), 2.0); // two steps on, half a metre past the prediction

        var tally = buffer.Tally;
        Assert.Equal(2, tally.ErrorSamples);
        Assert.Equal(0.25f, tally.MeanExtrapolationError, 3);
        Assert.Equal(0.5f, tally.WorstExtrapolationError, 3);
        Assert.Equal(0, tally.Jumps);
    }

    [Fact]
    public void ASampleNoFlightCouldReachIsAJumpAndNotAnError()
    {
        var buffer = OneSecondSamples();
        buffer.Add(Sample(1, 0f), 0.0);
        // Two metres of reach either way at one metre a second, and it landed a hundred away.
        buffer.Add(Sample(2, 100f), 1.0);

        var tally = buffer.Tally;
        Assert.Equal(1, tally.Jumps);
        Assert.Equal(0, tally.ErrorSamples);
        Assert.Equal(0f, tally.WorstExtrapolationError);
    }

    [Fact]
    public void TalliesSumAndKeepTheWorseWorst()
    {
        var a = new RemotePoseTally(1, 2, 3, 4, 5, 6, 1.5, 2f, 1);
        var b = new RemotePoseTally(10, 20, 30, 40, 50, 60, 3.0, 7f, 2);

        Assert.Equal(new RemotePoseTally(11, 22, 33, 44, 55, 66, 4.5, 7f, 3), a.Plus(b));
    }

    // One second of the sender's timeline per sequence step, so sequence n sits at n seconds.
    private static RemotePoseBuffer OneSecondSamples() => new(sampleSeconds: 1f);

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
