using System;
using System.Linq;
using CSVM.UI.Screens;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The load screen's progress off engine: the authored milestone table, the monotonic setter, the
/// repaint's pixel clip, the propeller's frame at its authored rate, and a walk of the build's own
/// steps through all sixteen (docs/org/loading-screen.md).
/// </summary>
public class LoadProgressTests
{
    // The exe's own table at 004a1ddd upward, transcribed here so a change to the shipped list is
    // a failing test rather than a silently different bar.
    private static readonly float[] Authored =
    {
        0.01f, 0.02f, 0.04f, 0.07f, 0.10f, 0.10f, 0.20f, 0.30f,
        0.40f, 0.50f, 0.60f, 0.70f, 0.71f, 0.72f, 0.80f, 0.90f,
    };

    /// <summary>Sixteen authored fractions, monotonic, from 0.01 to 0.90. Nothing sets 1.0: the
    /// screen is torn down from the highest milestone, so a full bar is never drawn.</summary>
    [Fact]
    public void TheMilestoneTableIsTheAuthoredSixteenAndMonotonic()
    {
        Assert.Equal(Authored, LoadProgress.Milestones);
        Assert.Equal(16, LoadProgress.Milestones.Count);
        for (int i = 1; i < LoadProgress.Milestones.Count; i++)
        {
            Assert.True(
                LoadProgress.Milestones[i] >= LoadProgress.Milestones[i - 1],
                $"milestone {i} ({LoadProgress.Milestones[i]}) is below its predecessor");
        }

        Assert.Equal(0.01f, LoadProgress.Milestones[0]);
        Assert.Equal(0.90f, LoadProgress.Milestones[^1]);
        Assert.DoesNotContain(1.0f, LoadProgress.Milestones);
    }

    /// <summary>One step per fraction, in the same order, so a build names a boundary and the
    /// table alone decides what the bar reads there.</summary>
    [Fact]
    public void EveryBuildStepNamesOneMilestoneInOrder()
    {
        var steps = Enum.GetValues<LoadStep>();

        Assert.Equal(LoadProgress.Milestones.Count, steps.Length);
        Assert.Equal(Enumerable.Range(0, steps.Length), steps.Select(s => (int)s));
        Assert.Equal(LoadStep.RenderState, steps[0]);
        Assert.Equal(LoadStep.Finished, steps[^1]);
    }

    /// <summary>The fill is floor(fillWidth * fraction) pixels of the fill bitmap, a pixel clip
    /// against the bitmap's own width rather than a count of lamps, so a boundary cuts a lamp
    /// part-way. The shipped strips are 236 (blackboard) and 338 (chart sheet) wide.</summary>
    [Theory]
    [InlineData(236, 0.00f, 0)]
    [InlineData(236, 0.01f, 2)]
    [InlineData(236, 0.40f, 94)]
    [InlineData(236, 0.72f, 169)]
    [InlineData(236, 0.90f, 212)]
    [InlineData(338, 0.10f, 33)]
    [InlineData(338, 0.90f, 304)]
    public void TheFillIsTheFlooredPixelClipOfTheStripsOwnWidth(int fillWidth, float fraction, int lit)
    {
        Assert.Equal(lit, LoadProgress.FillPixels(fillWidth, fraction));
    }

    /// <summary>A fraction outside [0, 1] and a bitmap the extraction does not carry both clip to
    /// something drawable rather than throwing over a launch.</summary>
    [Fact]
    public void TheFillClampsItsFractionAndAnAbsentStripLightsNothing()
    {
        Assert.Equal(236, LoadProgress.FillPixels(236, 2f));
        Assert.Equal(0, LoadProgress.FillPixels(236, -1f));
        Assert.Equal(0, LoadProgress.FillPixels(0, 0.9f));
    }

    /// <summary>The propeller steps its six frames at 6 fps, so one frame lasts a sixth of a
    /// second and the cycle comes round every second. Six frames, not the 38 the names span: the
    /// extraction carries the range endpoints alone.</summary>
    [Fact]
    public void ThePropellerStepsSixFramesAtTheAuthoredRate()
    {
        Assert.Equal(6.0, LoadProgress.PropellerFps);
        Assert.Equal(6, LoadProgress.PropellerFrames);
        Assert.Equal(6, LoadScreens.Propeller.Count);
        Assert.Equal(
            new[] { "prp0", "prp7", "prp15", "prp22", "prp30", "prp37" }, LoadScreens.Propeller);

        Assert.Equal(0, LoadProgress.FrameAt(0d));
        Assert.Equal(0, LoadProgress.FrameAt(0.16d));
        Assert.Equal(1, LoadProgress.FrameAt(0.17d));
        Assert.Equal(5, LoadProgress.FrameAt(0.9d));
        Assert.Equal(0, LoadProgress.FrameAt(1.0d));
        Assert.Equal(3, LoadProgress.FrameAt(4.6d));
    }

    /// <summary>The setter is monotonic: a fraction below the stored one is ignored and the
    /// table's repeated 0.10 is harmless, so no step drags the bar backwards.</summary>
    [Fact]
    public void TheSetterIsMonotonicAndAdmitsTheRepeatedMilestone()
    {
        var progress = new LoadProgress(() => 0d);

        Assert.True(progress.Set(0.10f));
        Assert.False(progress.Set(0.10f));
        Assert.False(progress.Set(0.04f));
        Assert.Equal(0.10f, progress.Fraction);
        Assert.True(progress.Set(0.20f));
        Assert.Equal(0.20f, progress.Fraction);
    }

    /// <summary>A walk of the build's own steps reaches every milestone in order and ends at 0.90,
    /// which is where the screen is torn down.</summary>
    [Fact]
    public void APhaseWalkReachesEveryMilestoneInOrder()
    {
        double clock = 0d;
        var progress = new LoadProgress(() => clock);
        var reached = new System.Collections.Generic.List<float>();
        progress.Repaint = () => reached.Add(progress.Fraction);

        foreach (var step in Enum.GetValues<LoadStep>())
        {
            // Past the pump's own throttle at every step, so the walk sees each one drawn rather
            // than coalesced.
            clock += LoadProgress.PumpSeconds * 2d;
            progress.Reach(step);
        }

        Assert.Equal(LoadProgress.Milestones, reached);
        Assert.Equal(0.90f, progress.Fraction);
    }

    /// <summary>A build that crosses every boundary in no time at all still draws each fraction it
    /// reaches: the throttle holds off a repeat, never a step the bar moved on, so a fast build
    /// steps through the table instead of showing its first fraction and then the mission.</summary>
    [Fact]
    public void EveryStepThatMovesTheBarIsDrawnHoweverFastTheBuildIs()
    {
        double clock = 0d;
        var progress = new LoadProgress(() => clock);
        var drawn = new System.Collections.Generic.List<float>();
        progress.Repaint = () => drawn.Add(progress.Fraction);

        foreach (var step in Enum.GetValues<LoadStep>())
        {
            progress.Reach(step);
        }

        Assert.Equal(LoadProgress.Milestones.Distinct(), drawn);
        Assert.Equal(drawn.Count, progress.Draws);
        Assert.Equal(0.90f, progress.Fraction);
    }

    /// <summary>The pump is wall-clock throttled to one draw per 0.1 s, which is what holds the
    /// propeller to its rate when a step leaves the bar where it was.</summary>
    [Fact]
    public void AStepThatMovesTheBarNowhereWaitsForTheThrottle()
    {
        double clock = 0d;
        int draws = 0;
        var progress = new LoadProgress(() => clock);
        progress.Repaint = () => draws++;

        progress.Reach(LoadStep.Paths);
        Assert.Equal(1, draws);

        // The table's repeated 0.10, inside the throttle's own window.
        clock += 0.05d;
        progress.Reach(LoadStep.Directors);
        Assert.Equal(1, draws);

        clock += 0.06d;
        progress.Pump();
        Assert.Equal(2, draws);
        Assert.Equal(LoadProgress.Milestones[(int)LoadStep.Paths], progress.Fraction);
    }

    /// <summary>Every reported step is traced against the build's own wall clock, with the ones
    /// that were not drawn marked, which is what the load screen writes its log line out of.
    /// </summary>
    [Fact]
    public void TheTraceCarriesEveryStepAgainstWallTime()
    {
        double clock = 0d;
        var progress = new LoadProgress(() => clock);

        progress.Reach(LoadStep.Paths);
        clock += 0.05d;
        progress.Reach(LoadStep.Directors);

        Assert.Equal(2, progress.Trace.Count);
        Assert.Equal(LoadStep.Paths, progress.Trace[0].Step);
        Assert.Equal(0d, progress.Trace[0].Seconds);
        Assert.True(progress.Trace[0].Drawn);
        Assert.Equal(0.05d, progress.Trace[1].Seconds);
        Assert.Equal(0.10f, progress.Trace[1].Fraction);
        Assert.False(progress.Trace[1].Drawn);
    }

    /// <summary>A build with no screen over it draws nothing: every CLI launch leaves the ambient
    /// null, which is what keeps a scripted or golden run from gaining a frame.</summary>
    [Fact]
    public void ABuildWithNoScreenOverItReportsToNobody()
    {
        Assert.Null(LoadProgress.Current);
        LoadProgress.Report(LoadStep.Archives);
        Assert.Null(LoadProgress.Current);
    }
}
