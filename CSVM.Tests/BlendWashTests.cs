using CSVM.UI.Boards;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The victim-routed screen wash's rules (<c>docs/org/ordnanceTypes.md</c>, the sonic/flash
/// section): overlapping washes combine their weights as <c>p + w − p·w</c> and mix the colour
/// toward the incoming one, the envelope is attack/sustain/release at 0.15 / 0.35 of the duration,
/// and the paint-time composite over the proximity ramp leaves a pane with no wash showing the
/// ramp alone.
/// </summary>
public class BlendWashTests
{
    private const float Dt = 1f / 60f;

    private static readonly Color Red = new(1f, 0f, 0f);
    private static readonly Color White = new(1f, 1f, 1f);
    private static readonly Color Clear = new(0f, 0f, 0f, 0f);

    [Fact]
    public void AFirstHitStartsAtZeroWeightAndTakesTheHitAsItsPeak()
    {
        var wash = new BlendWash();
        wash.Start(Red, 0.8f, 4f);
        Assert.True(wash.Running);
        Assert.Equal(0f, wash.Weight);
        Assert.Equal(0.8f, wash.Peak, 5);
        Assert.Equal(1f, wash.Colour.R, 5);
        Assert.Equal(0f, wash.Colour.G, 5);
    }

    [Fact]
    public void OverlappingWeightsCombineAsPPlusWMinusPW()
    {
        var wash = new BlendWash();
        wash.Start(Red, 0.5f, 4f);
        wash.Start(Red, 0.5f, 4f);
        // 0.5 + 0.5 − 0.25: worse than one, short of saturation.
        Assert.Equal(0.75f, wash.Peak, 5);
        wash.Start(Red, 1f, 4f);
        // A full-strength hit saturates the weight and nothing pushes it past 1.
        Assert.Equal(1f, wash.Peak, 5);
        wash.Start(Red, 1f, 4f);
        Assert.Equal(1f, wash.Peak, 5);
    }

    [Fact]
    public void AnOverlappingHitMixesTheColourTowardTheNewOne()
    {
        var wash = new BlendWash();
        wash.Start(White, 1f, 4f);
        wash.Start(Red, 1f, 4f);
        // (1,1,1)·1 + (1,0,0)·1 over the new peak plus the incoming weight (1 + 1): pink, not
        // white and not red.
        Assert.Equal(1f, wash.Colour.R, 4);
        Assert.Equal(0.5f, wash.Colour.G, 4);
        Assert.Equal(0.5f, wash.Colour.B, 4);
    }

    [Fact]
    public void AReHitRestartsTheEnvelopeWithoutDroppingTheDisplayedWeight()
    {
        var wash = new BlendWash();
        wash.Start(Red, 1f, 4f);
        StepFor(wash, 2f); // deep in the sustain
        Assert.Equal(1f, wash.Weight, 4);
        wash.Start(Red, 0.5f, 4f);
        Assert.Equal(0f, wash.Elapsed);
        Assert.Equal(1f, wash.Weight, 4);
        wash.Step(Dt);
        Assert.Equal(1f, wash.Weight, 4);
    }

    [Fact]
    public void TheEnvelopeAttacksOverFifteenPercentSustainsAndReleasesOverThirtyFivePercent()
    {
        var wash = new BlendWash();
        wash.Start(Red, 1f, 4f); // attack 0.6 s, sustain to 2.6 s, release 1.4 s

        StepFor(wash, 0.3f);
        Assert.InRange(wash.Weight, 0.45f, 0.55f); // halfway up the attack ramp
        StepFor(wash, 0.35f);
        Assert.Equal(1f, wash.Weight, 4); // at the peak once the attack has run
        StepFor(wash, 1.9f);
        Assert.Equal(1f, wash.Weight, 4); // still sustaining just short of 2.6 s

        // The release: the peak sheds dt/release of itself every step, so over the whole
        // release it decays toward 1/e of the sustain, and the cut at the duration ends it.
        StepFor(wash, 0.7f);
        Assert.InRange(wash.Weight, 0.55f, 0.65f); // half a release in: about e^-0.5
        StepFor(wash, 0.6f);
        Assert.InRange(wash.Weight, 0.35f, 0.45f);
        Assert.True(wash.Running);
        StepFor(wash, 0.2f);
        Assert.False(wash.Running);
        Assert.Equal(0f, wash.Weight);
    }

    [Fact]
    public void AStartDelayHoldsThePictureClearThenRunsTheWholeEnvelope()
    {
        var wash = new BlendWash();
        wash.Start(Red, 1f, 2f, startDelay: 1f);
        StepFor(wash, 0.9f);
        Assert.True(wash.Running);
        Assert.Equal(0f, wash.Weight);
        // Past the delay the envelope starts from zero elapsed, so its attack still takes 0.3 s.
        StepFor(wash, 0.4f);
        Assert.Equal(1f, wash.Weight, 4);
        StepFor(wash, 1.75f);
        Assert.False(wash.Running);
    }

    [Fact]
    public void ANonPositiveDurationClearsTheWash()
    {
        var wash = new BlendWash();
        wash.Start(Red, 1f, 4f);
        StepFor(wash, 1f);
        wash.Start(Red, 1f, 0f);
        Assert.False(wash.Running);
        Assert.Equal(0f, wash.Weight);
        Assert.Equal(0f, wash.Peak);
    }

    [Fact]
    public void CompositeLeavesTheRampAloneWithNoWashAndShowsTheWashAloneWithNoRamp()
    {
        var violet = new Color(0.2f, 0f, 1f, 0.2f);
        Assert.Equal(violet, BlendWash.Composite(violet, Red, 0f));
        Assert.Equal(Clear, BlendWash.Composite(Clear, Red, 0f));
        var washOnly = BlendWash.Composite(Clear, Red, 0.6f);
        Assert.Equal(1f, washOnly.R, 5);
        Assert.Equal(0f, washOnly.G, 5);
        Assert.Equal(0.6f, washOnly.A, 5);
    }

    [Fact]
    public void CompositeIsTheWashOverTheRamp()
    {
        // A half-strength white ramp under a half-strength red wash: alpha 0.5 + 0.5·0.5 = 0.75,
        // premultiplied colour white·0.5·0.5 + red·0.5 = (0.75, 0.25, 0.25), over 0.75.
        var over = BlendWash.Composite(new Color(1f, 1f, 1f, 0.5f), Red, 0.5f);
        Assert.Equal(0.75f, over.A, 5);
        Assert.Equal(1f, over.R, 5);
        Assert.Equal(1f / 3f, over.G, 5);
        Assert.Equal(1f / 3f, over.B, 5);
    }

    private static void StepFor(BlendWash wash, float seconds)
    {
        int steps = (int)System.Math.Round(seconds / Dt);
        for (int i = 0; i < steps; i++)
            wash.Step(Dt);
    }
}
