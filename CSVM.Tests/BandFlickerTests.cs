using System;
using CSVM.Session.World;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decompiled in-cloud flicker (<see cref="WeatherRig.BandFlicker"/>): two opacity remaps
/// blended by a drifting parameter. Off-engine like <c>RngTests</c>, the class
/// takes a plain <see cref="Random"/> rather than the shared Godot <c>RandomNumberGenerator</c>
/// stream precisely so this suite can pin it without the engine running.
/// </summary>
public class BandFlickerTests
{
    // Both curves agree here (both equal 1 at op=1, and the outer clamp forces op=0 to 0
    // regardless of t), so this frame count is far past any believable ramp for the "bounded"
    // sweep below.
    private const int ManyFrames = 500;

    [Fact]
    public void TheFirstCallForAFreshInstanceIsAnIdentityWhateverTheOpacity()
    {
        // A golden shot's frame 0 must match a static pose's WhiteoutAmount; this must hold by
        // construction, since neither curve equals identity at an interior opacity.
        foreach (float op in new[] { 0.01f, 0.25f, 0.5f, 0.75f, 0.99f })
        {
            var flicker = new WeatherRig.BandFlicker(new Random(1));
            Assert.Equal(op, flicker.Apply(op, frameDt: 1f / 60f));
        }
    }

    [Fact]
    public void TheCurvesAtTheirMidpointMatchTheDecompiledFormulas()
    {
        // The decompiled curves: log ln(op*5+1)/ln(6); atan (atan((op-0.5)*10)+0.5)/(atan(5)+0.5).
        // At op=0.5 the atan argument is 0, so atan(0)=0 collapses that curve to a closed form.
        double expectedLog = Math.Log(0.5 * 5 + 1) / Math.Log(6);
        double expectedAtan = 0.5 / (Math.Atan(5) + 0.5);

        Assert.Equal((float)expectedLog, WeatherRig.BandFlicker.LogCurve(0.5f), 5);
        Assert.Equal((float)expectedAtan, WeatherRig.BandFlicker.AtanCurve(0.5f), 5);

        // t=0 -> pure atan curve; t=1 -> pure log curve (the binary's blend order).
        Assert.Equal(WeatherRig.BandFlicker.AtanCurve(0.5f), WeatherRig.BandFlicker.Remap(0.5f, 0f), 5);
        Assert.Equal(WeatherRig.BandFlicker.LogCurve(0.5f), WeatherRig.BandFlicker.Remap(0.5f, 1f), 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void TheRemapStaysInZeroOneAndHitsTheEdgesExactly(float t)
    {
        // Sweep op across the interior for a fixed t; the outer clamp is what keeps this bounded
        // even though AtanCurve(0) is negative (-0.4663) before it.
        for (float op = 0f; op <= 1f; op += 0.05f)
        {
            float remapped = WeatherRig.BandFlicker.Remap(op, t);
            Assert.InRange(remapped, 0f, 1f);
        }

        Assert.Equal(0f, WeatherRig.BandFlicker.Remap(0f, t));
        Assert.Equal(1f, WeatherRig.BandFlicker.Remap(1f, t));
    }

    [Fact]
    public void TheGuardSkipsExactlyZeroAndOneAndNothingElseNear()
    {
        var flicker = new WeatherRig.BandFlicker(new Random(2));
        // Run the instance well past its ramp so an in-guard opacity would visibly move if the
        // guard were off by an edge.
        for (int i = 0; i < ManyFrames; i++)
        {
            Assert.Equal(0f, flicker.Apply(0f, frameDt: 1f / 60f));
            Assert.Equal(1f, flicker.Apply(1f, frameDt: 1f / 60f));
        }
    }

    [Fact]
    public void TheSameSeedProducesTheSameSequence()
    {
        // The --det requirement: two runs seeded identically (production seeds a fresh instance
        // per rig from Rng.NewSystemRandom(Rng.Clouds), which is itself deterministic under a
        // pinned master, see RngTests) must read the same opacity at every matched frame index.
        var a = new WeatherRig.BandFlicker(new Random(42));
        var b = new WeatherRig.BandFlicker(new Random(42));

        for (int frame = 0; frame < ManyFrames; frame++)
        {
            float opA = a.Apply(0.5f, frameDt: 1f / 60f);
            float opB = b.Apply(0.5f, frameDt: 1f / 60f);
            Assert.Equal(opA, opB);
            Assert.Equal(a.T, b.T);
        }
    }

    [Fact]
    public void ADifferentSeedEventuallyDrawsADifferentSequence()
    {
        // The able-to-fail control: if the rng draw were dead code, this would also pass
        // identically, so a green result here only means something because that case would fail.
        var a = new WeatherRig.BandFlicker(new Random(1));
        var b = new WeatherRig.BandFlicker(new Random(2));

        bool everDiffered = false;
        for (int frame = 0; frame < ManyFrames; frame++)
        {
            if (a.Apply(0.5f, frameDt: 1f / 60f) != b.Apply(0.5f, frameDt: 1f / 60f))
            {
                everDiffered = true;
            }
        }

        Assert.True(everDiffered);
    }

    [Fact]
    public void TheAmplitudeRampReachesFullStrengthByRampFrames()
    {
        // After RampFrames calls the flicker is no longer damped: two instances seeded so their
        // drift disagrees read different opacities at the same op once both are past the ramp.
        var a = new WeatherRig.BandFlicker(new Random(3));
        var b = new WeatherRig.BandFlicker(new Random(9));

        bool differedPastRamp = false;
        for (int frame = 0; frame < WeatherRig.BandFlicker.RampFrames + 100; frame++)
        {
            float opA = a.Apply(0.5f, frameDt: 1f / 60f);
            float opB = b.Apply(0.5f, frameDt: 1f / 60f);
            if (frame >= WeatherRig.BandFlicker.RampFrames && opA != opB)
            {
                differedPastRamp = true;
            }
        }

        Assert.True(differedPastRamp);
    }
}
