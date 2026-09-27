using CSVM.Net;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// When an owner sends and what sequence the sample carries, off-engine. The cadence is counted
/// in simulation steps, so a whole flight's worth of them is scripted here rather than flown.
/// The sequence is what a receiver decides staleness by. The two cases that matter are two
/// seats not sharing a ladder, and the 16-bit wrap still being accepted by the buffer that
/// reads it. The last test is the pair together: a wrapped sample lands rather than dropping.
/// </summary>
[Trait("Tier", "Quick")]
public class AircraftStateCadenceTests
{
    [Fact]
    public void TheFirstStepSendsAndTheIntervalHoldsFromThere()
    {
        var cadence = new AircraftStateCadence();

        Assert.True(cadence.StepSends());
        for (int i = 1; i < AircraftStateCadence.SendStepInterval; i++)
        {
            Assert.False(cadence.StepSends());
        }

        Assert.True(cadence.StepSends());
    }

    [Fact]
    public void OneSampleEveryIntervalOverAWholeFlight()
    {
        var cadence = new AircraftStateCadence();
        int sent = 0;
        for (int step = 0; step < 600; step++)
        {
            if (cadence.StepSends())
            {
                sent++;
            }
        }

        Assert.Equal(600 / AircraftStateCadence.SendStepInterval, sent);
        Assert.Equal(600, cadence.Steps);
    }

    [Fact]
    public void EachSeatCountsItsOwnSequence()
    {
        var cadence = new AircraftStateCadence();

        Assert.Equal(0, cadence.Next(0));
        Assert.Equal(1, cadence.Next(0));
        Assert.Equal(0, cadence.Next(3));
        Assert.Equal(2, cadence.Next(0));
        Assert.Equal(1, cadence.Next(3));
    }

    [Fact]
    public void EverySeatTheRosterAdmitsHasACounter()
    {
        var cadence = new AircraftStateCadence();
        for (int seat = 0; seat < NetSeats.SeatCapacity; seat++)
        {
            Assert.Equal(0, cadence.Next(seat));
        }
    }

    [Fact]
    public void TheSequenceWrapsAndTheBufferStillTakesTheSampleAfterIt()
    {
        var cadence = new AircraftStateCadence();
        for (int i = 0; i < 65535; i++)
        {
            cadence.Next(1);
        }

        ushort last = cadence.Next(1);
        ushort wrapped = cadence.Next(1);
        Assert.Equal(65535, last);
        Assert.Equal(0, wrapped);

        // The wrap is only correct if the reader takes it, which the buffer's comparison owns.
        // A plain "is it bigger" there would freeze the aeroplane for a whole cycle.
        var buffer = new RemotePoseBuffer();
        Assert.True(buffer.Add(Sample(last), 0.0));
        Assert.True(buffer.Add(Sample(wrapped), 0.1));
    }

    private static AircraftStateMessage Sample(ushort sequence) =>
        new(1, sequence, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 1f, 0f, 0f, 0f, false);
}
