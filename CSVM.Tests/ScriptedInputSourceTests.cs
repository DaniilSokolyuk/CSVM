using CSVM.Flight.Airframe;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="ScriptedInputSource"/>'s playback of a fixed hold profile: each segment holds its
/// input for its duration, the last segment holds forever, and <see cref="ScriptedInputSource.Reset"/>
/// restarts the sequence from the first segment (a respawn's fresh start). Constructed directly
/// from a segment list, with no <see cref="FlightController"/> in the process.
/// </summary>
public class ScriptedInputSourceTests
{
    /// <summary>A profile of two timed segments then an open-ended third: each read stays on its
    /// segment's input until the cumulative elapsed time crosses into the next one.</summary>
    [Fact]
    public void EachSegmentHoldsItsInputForExactlyItsDuration()
    {
        var source = new ScriptedInputSource(new (FlightInput, float)[]
        {
            (Pitch(1f), 0.5f),
            (Pitch(2f), 0.5f),
            (Pitch(3f), 0f), // the last segment: holds forever regardless of its own duration
        });

        // 0.0 -> 0.2s: still inside segment 0
        Assert.Equal(1f, source.Read(0.2f).Pitch);
        // 0.2 -> 0.4s: still inside segment 0 (0.4 < 0.5)
        Assert.Equal(1f, source.Read(0.2f).Pitch);
        // 0.4 -> 0.7s: crossed into segment 1 (0.7 - 0.5 = 0.2 < 0.5)
        Assert.Equal(2f, source.Read(0.3f).Pitch);
        // 0.7 -> 1.1s: crossed into segment 2, which holds regardless of further elapsed time
        Assert.Equal(3f, source.Read(0.4f).Pitch);
        Assert.Equal(3f, source.Read(10f).Pitch);
    }

    /// <summary>A duration of 0 (or less) on a non-last segment holds it forever too, the same
    /// rule the last segment gets implicitly, spelled out for an interior segment.</summary>
    [Fact]
    public void ANonPositiveDurationHoldsThatSegmentForever()
    {
        var source = new ScriptedInputSource(new (FlightInput, float)[]
        {
            (Pitch(1f), 0f),
            (Pitch(2f), 0.5f),
        });

        Assert.Equal(1f, source.Read(100f).Pitch);
        Assert.Equal(1f, source.Read(100f).Pitch);
    }

    /// <summary>Reset restarts the sequence from its first segment, matching
    /// <see cref="FlightController.Respawn"/>'s "scripted hold sequences restart from the spawn".</summary>
    [Fact]
    public void ResetRestartsTheSequenceFromTheFirstSegment()
    {
        var source = new ScriptedInputSource(new (FlightInput, float)[]
        {
            (Pitch(1f), 0.5f),
            (Pitch(2f), 0f),
        });

        Assert.Equal(1f, source.Read(0.2f).Pitch);
        Assert.Equal(2f, source.Read(0.4f).Pitch); // 0.6s elapsed: into segment 1

        source.Reset();

        Assert.Equal(1f, source.Read(0.2f).Pitch); // back to segment 0
    }

    private static FlightInput Pitch(float p) => new() { Pitch = p };
}
