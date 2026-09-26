using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's keyboard stick: a held key ramps the axis at 2.5/s, a released or reversed one
/// drops it to centre in a single frame. Decode: docs/org/flightModel.md.
/// The asymmetry is the whole mechanism. A symmetric ramp, one that walks back to centre at the
/// same rate, matches on a held key and differs on every release and every reversal, which is
/// where the pitch-cadence roll-off comes from, so those are the cases pinned here.
/// </summary>
public class StickRampTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void AHeldKeyTakesFourTenthsOfASecondToReachFullDeflection()
    {
        float axis = 0f;
        for (int i = 0; i < 24; i++)   // 24 frames at 1/60 = 0.4 s
        {
            axis = StickRamp.Step(axis, 1f, Dt);
        }

        Assert.True(Mathf.IsEqualApprox(axis, 1f, 1e-5f),
            $"0.4 s of held key reached {axis:0.000000}, expected full deflection");

        // Halfway there is halfway deflected: the ramp is linear, not an exponential approach.
        float half = 0f;
        for (int i = 0; i < 12; i++)
        {
            half = StickRamp.Step(half, 1f, Dt);
        }

        Assert.True(Mathf.IsEqualApprox(half, 0.5f, 1e-5f),
            $"0.2 s of held key reached {half:0.000000}, expected 0.5");
    }

    [Fact]
    public void ATapNeverReachesTheDeflectionItsKeyNominallyCommands()
    {
        // The original's own duty-control clip pressed for 116.6 ms; at 2.5/s that is 0.29 of
        // travel, which is what its measured pitch rate came to as a fraction of the sustained one.
        float axis = 0f;
        for (float t = 0f; t < 0.1166f; t += Dt)
        {
            axis = StickRamp.Step(axis, 1f, Dt);
        }

        Assert.True(axis is > 0.25f and < 0.33f,
            $"a 116.6 ms press reached {axis:0.000}, expected ~0.29 of full travel");
    }

    [Fact]
    public void ReleasingCentresTheStickInOneFrameRatherThanRampingBack()
    {
        float axis = 0f;
        for (int i = 0; i < 24; i++)
        {
            axis = StickRamp.Step(axis, 1f, Dt);
        }

        float released = StickRamp.Step(axis, 0f, Dt);
        Assert.Equal(0f, released);
    }

    [Fact]
    public void AReversalStartsFromCentreInTheSameFrameNotFromTheOldDeflection()
    {
        float axis = 0f;
        for (int i = 0; i < 24; i++)
        {
            axis = StickRamp.Step(axis, 1f, Dt);
        }

        // Zero-snap first, then this frame's ramp: -Rate·dt, NOT 1 - Rate·dt. A ramp that merely
        // counts down from the old value would still be at +0.958 here and would take another
        // 0.4 s to cross centre, halving the roll-off a fast cadence produces.
        float reversed = StickRamp.Step(axis, -1f, Dt);
        float expected = -StickRamp.Rate * Dt;
        Assert.True(Mathf.IsEqualApprox(reversed, expected, 1e-6f),
            $"the reversal frame gave {reversed:0.000000}, expected {expected:0.000000} "
            + $"(counting down from full deflection would give {1f - (StickRamp.Rate * Dt):0.000000})");
    }

    [Fact]
    public void AFrameWithNoKeyAtAllCentresTheAxisFromEitherSign()
    {
        // Centring is driven by the command, not by time: a frame with no key at all still zeroes
        // a deflected axis, which is what makes release instant.
        Assert.Equal(0f, StickRamp.Step(0.7f, 0f, Dt));
        Assert.Equal(0f, StickRamp.Step(-0.7f, 0f, Dt));
    }
}
