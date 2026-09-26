using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The wobble-oscillator law (<c>docs/formats/shakes.md</c>): <b>fire is a random-walk accumulator</b>
/// (BL-266(a) branch, per-shot uniform step ±7.54·(factor×caliber), explosion, pulled from an
/// injected <see cref="Random"/> so the trace pins without the engine), the being-hit he_factor
/// doubling, the overspeed gate, and determinism of the whole sum. Input is
/// <c>fixtures/zrdr/shakes.json</c> (probe values under the real source ids; <c>bullet_impact</c>/
/// <c>explosion</c> deliberately absent so missing sources prove to no-op).
/// </summary>
public class PlaneShakeTests
{
    private const float Dt = 1f / 60f;

    // fixture fire_bullet.magnitude_factor = 2e-4; × caliber 40 = 8e-3; × the decoded 7.54 gain
    // (2.0×6.2832×1.2 over the rand half-range) = max single-shot step 6.03e-2 rad.
    private const float MaxSingleStep = 2e-4f * 40f * 7.54f;

    [Fact]
    public void AFiredRoundStepsTheAccumulatorByABoundedRandomWalkStep()
    {
        // One shot from a fixed seed: |step| must be in (0, 7.54·(factor×caliber)], the decoded
        // uniform ±6.03e-2 law, not the old ±8e-3 sawtooth envelope.
        var shake = NewShake();
        shake.FireBullet(40f);
        float roll = PeakAfter(shake, seconds: 0.02f); // ~1 tick later, before decay eats the step
        Assert.True(roll > 0f && Math.Abs(roll) <= MaxSingleStep * 1.001f,
            $"single kick step out of the decoded bound: {roll} (max {MaxSingleStep})");
    }

    [Fact]
    public void TheKickStepScalesWithMagnitudeAndIsRandomAcrossSeeds()
    {
        // Same event, different seed → the step direction/size differs (it is a random walk, not a
        // fixed sawtooth)
        float a = SingleKickStep(NewShake(seed: 1));
        float b = SingleKickStep(NewShake(seed: 2));
        Assert.NotEqual(a, b);

        // A bigger caliber (or factor) gives a proportionally bigger step: factor×caliber doubles
        // → the step's absolute upper bound doubles.
        var big = NewShake();
        big.FireBullet(80f); // twice the caliber of the wep40 shot
        float bigRoll = PeakAfter(big, seconds: 0.02f);
        Assert.True(Math.Abs(bigRoll) <= MaxSingleStep * 2f * 1.001f,
            $"step did not scale with caliber: {bigRoll}");
    }

    [Fact]
    public void RepeatedShotsAccumulateThenDampDecaysTheWalkBackToZero()
    {
        // A burst injects many steps; letting fire stop lets the authored damp (12.5, τ=80 ms)
        // pull the walk back to rest, the mechanism is bounded, it does not drift monotonic.
        var shake = NewShake();
        float peak = 0f;
        for (int i = 0; i < 30; i++) // ~0.5 s at 60 Hz
        {
            if (i % 8 == 0)
            {
                shake.FireBullet(40f); // ~8 shots/s
            }
            shake.Advance(Dt);
            peak = Math.Max(peak, Math.Abs(shake.Roll));
        }
        Assert.True(peak > MaxSingleStep * 0.5f, $"burst never reached a sizeable walk: {peak}");

        // several damp time-constants after the last shot the walk is essentially at rest
        for (int i = 0; i < 30; i++) // 0.5 s ≈ 6 damp time-constants (τ=80 ms)
        {
            shake.Advance(Dt);
        }
        Assert.True(Math.Abs(shake.Roll) < peak * 0.05f,
            $"walk did not decay to rest: peak {peak}, final {shake.Roll}");
    }

    [Fact]
    public void AHighExplosiveHitDoublesTheMissileMagnitude()
    {
        var he = NewShake();
        he.MissileHit(10f, highExplosive: true);
        var plain = NewShake();
        plain.MissileHit(10f, highExplosive: false);
        float heEnv = MaxAbsRollOver(he, seconds: 0.6f);
        float plainEnv = MaxAbsRollOver(plain, seconds: 0.6f);
        Assert.InRange(heEnv / plainEnv, 1.8f, 2.2f);
    }

    [Fact]
    public void TheOverspeedGateHoldsCruiseSilentAndOpensBeyondRatedMax()
    {
        var shake = NewShake();
        shake.SetSpeedRatio(0.95f); // fast cruise, below the min_speed 1.0 gate
        Assert.Equal(0f, MaxAbsRollOver(shake, seconds: 1f));

        shake.SetSpeedRatio(1.0f);  // exactly at the gate: EXCESS over it, so still zero
        Assert.Equal(0f, MaxAbsRollOver(shake, seconds: 1f));

        shake.SetSpeedRatio(1.2f);  // overspeed dive
        float diving = MaxAbsRollOver(shake, seconds: 1f);
        Assert.True(diving > 0f, "the open gate rattles");

        shake.SetSpeedRatio(0.5f);  // pull out: the ramp law's own decay bleeds it off, no pop
        Assert.True(MaxAbsRollOver(shake, seconds: 1f) < diving);
        MaxAbsRollOver(shake, seconds: 2f); // let the ring-down reach the block's rest threshold
        Assert.Equal(0f, MaxAbsRollOver(shake, seconds: 1f));
    }

    [Fact]
    public void TheDiveRattleKicksTheDecodedVelocityStepOncePerTick()
    {
        // The decoded kick is a VELOCITY uniform in +/-0.6 x mag x frequency x 4 (the sawtooth
        // waveform factor), and one tick of the block's integrator turns it into mag x dt of roll
        // before the reversal test can act, so a single tick pins the step law exactly.
        float Excess(float ratio) => (ratio - 1f) / 70f;
        float Bound(float ratio) => 0.6f * Excess(ratio) * 15f * 4f * Dt;

        var shake = NewShake();
        shake.SetSpeedRatio(1.2f);
        shake.Advance(Dt);
        float step = Math.Abs(shake.Roll);
        Assert.True(step > 0f && step <= Bound(1.2f) * 1.001f,
            $"one dive tick out of the decoded bound: {step} (max {Bound(1.2f)})");

        // Twice the excess over the gate is twice the step, the law being linear in the drive.
        var faster = NewShake();
        faster.SetSpeedRatio(1.4f);
        faster.Advance(Dt);
        Assert.Equal(2f, Math.Abs(faster.Roll) / step, 3);

        // It is a walk: the same dive on another seed steps somewhere else.
        var other = NewShake(seed: 7);
        other.SetSpeedRatio(1.2f);
        other.Advance(Dt);
        Assert.NotEqual(shake.Roll, other.Roll);
    }

    [Fact]
    public void ASustainedDiveWandersInsteadOfRepeatingASawtooth()
    {
        // A deterministic sawtooth under a constant drive settles to one repeated amplitude; the
        // decoded block re-kicks its velocity at random every tick, so the swings wander. The
        // spread of the swing extremes is what separates the two mechanisms.
        var shake = NewShake();
        shake.SetSpeedRatio(1.3f);
        var swings = Swings(shake, seconds: 4f).Select(s => s.Roll).ToList();
        Assert.True(swings.Count > 20, $"a 4 s dive swings repeatedly: {swings.Count}");
        Assert.Contains(swings, s => s > 0f);
        Assert.Contains(swings, s => s < 0f);

        double mean = swings.Average(s => Math.Abs(s));
        double spread = Math.Sqrt(swings.Average(s => (Math.Abs(s) - mean) * (Math.Abs(s) - mean)));
        Assert.True(spread / mean > 0.15,
            $"the swing amplitude wanders rather than repeating: mean {mean}, spread {spread}");
    }

    [Fact]
    public void ANitroEngageKicksTheAuthoredAbsoluteMagnitudeOnce()
    {
        // fixture nitro.magnitude = 0.05, absolute (no caliber or damage multiplies it), frequency
        // 4, damp 3, sawtooth. The kick is a velocity, so one tick in the roll is at most
        // 0.6 x 0.05 x 4 x 4 x dt.
        const float StepBound = 0.6f * 0.05f * 4f * 4f * Dt;
        var shake = NewShake();
        shake.NitroEngaged();
        shake.Advance(Dt);
        float first = Math.Abs(shake.Roll);
        Assert.True(first > 0f && first <= StepBound * 1.001f,
            $"the engage step is out of the decoded bound: {first} (max {StepBound})");

        // The block swings out and back rather than jumping: the largest single-tick move stays
        // far under the peak, where the sawtooth it replaces wrapped by twice its own amplitude.
        var engage = NewShake();
        engage.NitroEngaged();
        float peak = 0f, biggestStep = 0f, previous = 0f;
        for (int i = 0; i < 120; i++) // 2 s
        {
            engage.Advance(Dt);
            peak = Math.Max(peak, Math.Abs(engage.Roll));
            biggestStep = Math.Max(biggestStep, Math.Abs(engage.Roll - previous));
            previous = engage.Roll;
        }
        Assert.True(biggestStep < peak * 0.5f,
            $"the wobble ramps rather than wrapping: peak {peak}, biggest tick {biggestStep}");

        // Rate and decay both come out of the ramp law, not the authored 4 Hz it replaces: a swing
        // every (1+k)/(4·frequency·k) = 0.153 s with k = e^(-damp/2·frequency), each 0.687 of the
        // one before (tick sampling undershoots a peak by up to 18%, the band's width).
        var decay = NewShake();
        decay.NitroEngaged();
        var swings = Swings(decay, seconds: 1f);
        Assert.True(swings.Count >= 4, $"the engage swings several times: {swings.Count}");
        for (int i = 2; i < swings.Count; i++)
        {
            Assert.InRange(Math.Abs(swings[i].Roll) / Math.Abs(swings[i - 1].Roll), 0.55f, 0.85f);
            Assert.InRange(swings[i].Tick - swings[i - 1].Tick, 8, 11);
        }
        MaxAbsRollOver(decay, seconds: 4f); // let the ring-down finish
        Assert.Equal(0f, MaxAbsRollOver(decay, seconds: 1f));
    }

    [Fact]
    public void AContactKicksBlockFiveAtTheDecodedMagnitude()
    {
        // 120 m/s into a 0.6 cosine is far over the ceiling, which is where any contact at flight
        // speed lands: the decoded law is min(speed x severity x 0.03, 0.15) radians.
        float magnitude = CollisionDamage.ContactShake(speed: 120f, severity: 0.6f);
        Assert.Equal(CollisionDamage.ContactShakeCap, magnitude, 5);

        // Block 5 keeps the constructor's law (2 Hz, damp 4.5) because no def authors it, so the
        // sine peaks a quarter cycle in with the envelope already down to e^(-4.5 x 0.125).
        var shake = NewShake();
        shake.ContactHit(magnitude);
        float peak = MaxAbsRollOver(shake, seconds: 0.5f);
        Assert.InRange(peak, magnitude * 0.5f, magnitude * 1.001f);

        // It is an impulse, so the authored damp takes it back to rest and leaves it there.
        Assert.True(MaxAbsRollOver(shake, seconds: 4f) < peak);
        Assert.Equal(0f, MaxAbsRollOver(shake, seconds: 1f));
    }

    [Fact]
    public void AGrazeKicksTooAndScalesWithSpeedAndTheRawCosine()
    {
        // The original gates the call on a positive severity cosine and nothing else, so a graze
        // kicks like any other contact. Under the ceiling the law is linear in both terms.
        Assert.Equal(0f, CollisionDamage.ContactShake(120f, 0f));
        Assert.Equal(0.06f, CollisionDamage.ContactShake(20f, 0.1f), 5);
        Assert.Equal(0.12f, CollisionDamage.ContactShake(40f, 0.1f), 5);

        var shake = NewShake();
        shake.ContactHit(CollisionDamage.ContactShake(20f, 0.1f));
        Assert.InRange(MaxAbsRollOver(shake, seconds: 0.5f), 0.06f * 0.5f, 0.06f * 1.001f);
    }

    [Fact]
    public void AMissingSourceIsANoOpNotACrash()
    {
        var shake = NewShake(); // fixture has no bullet_impact / explosion
        shake.BulletHit(40f);
        shake.ExplosionAt(25f);
        Assert.Equal(0f, MaxAbsRollOver(shake, seconds: 0.5f));

        // …and an install whose file authors nothing at all, where the two block sources have no
        // law to run either.
        var bare = new PlaneShake(new ShakeDefs(), new Random(1));
        bare.FireBullet(40f);
        bare.NitroEngaged();
        bare.SetSpeedRatio(1.5f);
        Assert.Equal(0f, MaxAbsRollOver(bare, seconds: 0.5f));
    }

    [Fact]
    public void TheSameEventScriptReplaysToTheSameRollTrace()
    {
        float[] Run(int seed)
        {
            var shake = NewShake(seed);
            var trace = new float[120];
            for (int i = 0; i < trace.Length; i++)
            {
                if (i == 10 || i == 40)
                {
                    shake.FireBullet(40f);
                }
                if (i == 60)
                {
                    shake.MissileHit(12f, highExplosive: true);
                }
                shake.SetSpeedRatio(i > 80 ? 1.1f : 0.7f);
                shake.Advance(Dt);
                trace[i] = shake.Roll;
            }
            return trace;
        }

        Assert.Equal(Run(1), Run(1));       // same seed (fixed) → identical wobble
        Assert.NotEqual(Run(1), Run(2));    // the fire walk is seeded → different mission, different buzz
    }

    private static PlaneShake NewShake() => NewShake(seed: 1);

    private static PlaneShake NewShake(int seed) =>
        new(ShakeDefs.Load(TestData.Fixture("zrdr")), new Random(seed));

    private static float SingleKickStep(PlaneShake shake)
    {
        shake.FireBullet(40f);
        return PeakAfter(shake, seconds: 0.02f);
    }

    private static float PeakAfter(PlaneShake shake, float seconds)
    {
        float max = 0f;
        for (float t = 0f; t < seconds; t += Dt)
        {
            shake.Advance(Dt);
            max = Math.Max(max, Math.Abs(shake.Roll));
        }
        return max;
    }

    // Every turning point of the roll trace over the window, with the tick it turned on: the
    // swing amplitude and the swing rate, which is what separates a random walk through the
    // decoded block from the periodic sawtooth it replaced.
    private static List<(float Roll, int Tick)> Swings(PlaneShake shake, float seconds)
    {
        var swings = new List<(float Roll, int Tick)>();
        float previous = shake.Roll;
        float last = 0f;
        for (int tick = 0; tick < (int)(seconds / Dt); tick++)
        {
            shake.Advance(Dt);
            float step = shake.Roll - previous;
            if (step != 0f)
            {
                if (last != 0f && Math.Sign(step) != Math.Sign(last))
                {
                    swings.Add((previous, tick));
                }
                last = step;
            }
            previous = shake.Roll;
        }
        return swings;
    }

    private static float MaxAbsRollOver(PlaneShake shake, float seconds)
    {
        float max = 0f;
        for (float t = 0f; t < seconds; t += Dt)
        {
            shake.Advance(Dt);
            max = Math.Max(max, Math.Abs(shake.Roll));
        }
        return max;
    }
}
