using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// How often a host repeats the match state, off-engine. The rate is counted in simulation steps,
/// so a whole round's worth of them is scripted here rather than flown. Two facts matter. The
/// first step ticks, so a guest has the limits inside one step of the build. The rate stays at
/// the whole second the versus HUD reads in, which keeps the clock off the per-step wire.
/// </summary>
[Trait("Tier", "Quick")]
public class MatchStateCadenceTests
{
    [Fact]
    public void TheFirstStepTicksAndTheIntervalHoldsFromThere()
    {
        var cadence = new MatchStateCadence();

        Assert.True(cadence.StepSends());
        for (int i = 1; i < MatchStateCadence.TickStepInterval; i++)
        {
            Assert.False(cadence.StepSends());
        }

        Assert.True(cadence.StepSends());
    }

    [Fact]
    public void OneTickASecondOverAWholeRound()
    {
        var cadence = new MatchStateCadence();
        int sent = 0;
        for (int step = 0; step < 3600; step++)
        {
            if (cadence.StepSends())
            {
                sent++;
            }
        }

        Assert.Equal(60, sent); // a minute of fixed steps, one tick a second
        Assert.Equal(3600, cadence.Steps);
        Assert.Equal(3600 / MatchStateCadence.TickStepInterval, sent);
    }
}
