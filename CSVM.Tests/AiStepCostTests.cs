using System.Diagnostics;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The open / close / drain cycle behind <see cref="AiStepCost"/>, the bracket that gives
/// <c>--perf</c> its only term attributing frame cost to the AI. What the drain reports, that the
/// plane count rides the same bracket as the time so a sweep can divide one by the other, and that
/// several walks in one rendered frame sum into that frame rather than being meaned away.
/// ⚠ This is the ONLY class that writes <see cref="AiStepCost"/>'s ambient statics. xUnit runs
/// test classes in parallel but the tests within one class serially, so that is what keeps these
/// deterministic; a second class bracketing walks would have to join this one.
/// </summary>
public class AiStepCostTests
{
    public AiStepCostTests() => AiStepCost.Reset();

    [Fact]
    public void ADrainReportsOneEntryPerClosedWalkAndSumsThePlanes()
    {
        for (int i = 0; i < 3; i++)
        {
            AiStepCost.Open();
            AiStepCost.Close(4);
        }
        Assert.Equal(3, AiStepCost.Steps);

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(3, steps);
        Assert.Equal(12, planes);
        // What the --perf line prints as ai_planes: the roster size, recovered from the sum.
        Assert.Equal(4.0, (double)planes / steps);
        Assert.True(ms >= 0, $"the banked total cannot be negative: {ms}");
    }

    [Fact]
    public void BTheTotalIsEveryWalkAndNotTheWorstOfThem()
    {
        // Six walks that each burn the same time, so a total banking only the worst of them would
        // read one walk's cost. The claim is a floor: load can only inflate it, never redden it.
        // WallCostBankTests asserts the same sum by value, on a clock it advances itself.
        const double walkMs = 5;
        const int walks = 6;
        for (int i = 0; i < walks; i++)
        {
            AiStepCost.Open();
            Spin(walkMs);
            AiStepCost.Close(8);
        }

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(walks, steps);
        Assert.Equal(48, planes);
        Assert.True(ms >= walks * walkMs, $"every walk is inside the total, not just the worst: {ms} ms");
    }

    [Fact]
    public void CASeveralWalksInOneFrameSumIntoThatFrame()
    {
        // A parent-driven clock runs GameClock.Steps walks per rendered frame. ai_ms divides the
        // window total by FRAMES, so two 10 ms walks in a one-frame window are 20 ms of AI, not 10.
        const int frames = 1;
        for (int i = 0; i < 2; i++)
        {
            AiStepCost.Open();
            Spin(10);
            AiStepCost.Close(2);
        }

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(2, steps);
        Assert.Equal(4, planes);
        Assert.True(ms / frames >= 20, $"both walks belong to the one frame: {ms / frames} ms");
    }

    [Fact]
    public void DADrainResetsEveryAccumulator()
    {
        AiStepCost.Open();
        Spin(10);
        AiStepCost.Close(3);
        AiStepCost.Take();

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(0, steps);
        Assert.Equal(0, planes);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void EACloseWithNoOpenStandingBanksNothing()
    {
        // The shape a throwing sim phase leaves behind: SessionSimulation ends the step, so the
        // close never runs and the next open replaces the stamp. Neither may bank a plane.
        AiStepCost.Close(6);
        AiStepCost.Close(6);

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(0, steps);
        Assert.Equal(0, planes);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void FAResetDropsAHalfOpenWalkRatherThanChargingItToTheNextClose()
    {
        AiStepCost.Open();
        Spin(15);
        AiStepCost.Reset();
        AiStepCost.Close(5);

        var (ms, steps, planes) = AiStepCost.Take();
        Assert.Equal(0, steps);
        Assert.Equal(0, planes);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void GABracketAllocatesNothing()
    {
        // The same contract PerfSampleTests.AScopeAllocatesNothing holds, for the same reason: an
        // instrument on the frame path that allocated would manufacture the stalls it measures.
        Brackets(); // warm-up: pays for tiered JIT and OSR outside the windows (PERF-28, PERF-29)

        int firstCharged = PerfSampleTests.MeasureWindows(Brackets, out long[] charged, out int[] collections);

        Assert.True(firstCharged < 0, firstCharged < 0 ? string.Empty
            : $"window {firstCharged} charged {charged[firstCharged]} bytes over {collections[firstCharged]} gen0 collections");
        AiStepCost.Take();
    }

    private static void Brackets()
    {
        for (int i = 0; i < 10_000; i++)
        {
            AiStepCost.Open();
            AiStepCost.Close(4);
        }
    }

    // Burns wall time without sleeping, so the slow walk is slow for the same reason a real one is.
    private static void Spin(double ms)
    {
        long start = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < ms)
        {
        }
    }
}
