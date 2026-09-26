using System;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded control-surface animation: which stick channel reaches which of the original's six
/// angle slots, with what gain, sign and clamp, how fast each slot chases its target, and the
/// player-only guard that leaves an AI aircraft's surfaces frozen. Decode with addresses:
/// docs/org/flightModel.md, "The original's control-surface animation".
/// </summary>
public class ControlSurfaceMixTests
{
    private const float Dt = 1f / 60f;

    private static readonly SurfaceSlot[] AllSlots =
    {
        SurfaceSlot.AileronLeft, SurfaceSlot.AileronRight,
        SurfaceSlot.ElevatorLeft, SurfaceSlot.ElevatorRight, SurfaceSlot.Rudder,
    };

    /// <summary>Each axis alone reaches the decoded slots and no others: roll drives the ailerons
    /// differentially, pitch the elevators in common, yaw the rudder.</summary>
    [Fact]
    public void EachAxisAloneReachesItsDecodedSlots()
    {
        var roll = new FlightInput { Roll = 1f };
        Assert.Equal(-0.5f, Target(SurfaceSlot.AileronLeft, roll), 6);
        Assert.Equal(0.5f, Target(SurfaceSlot.AileronRight, roll), 6);
        Assert.Equal(-0.18f, Target(SurfaceSlot.ElevatorLeft, roll), 6);
        Assert.Equal(0.18f, Target(SurfaceSlot.ElevatorRight, roll), 6);
        Assert.Equal(0f, Target(SurfaceSlot.Rudder, roll), 6);

        var pitch = new FlightInput { Pitch = 1f };
        Assert.Equal(0f, Target(SurfaceSlot.AileronLeft, pitch), 6);
        Assert.Equal(0f, Target(SurfaceSlot.AileronRight, pitch), 6);
        Assert.Equal(-0.6f, Target(SurfaceSlot.ElevatorLeft, pitch), 6);
        Assert.Equal(-0.6f, Target(SurfaceSlot.ElevatorRight, pitch), 6);
        Assert.Equal(0f, Target(SurfaceSlot.Rudder, pitch), 6);

        var yaw = new FlightInput { Yaw = 1f };
        Assert.Equal(-0.61086524f, Target(SurfaceSlot.Rudder, yaw), 6);
        Assert.Equal(0f, Target(SurfaceSlot.AileronLeft, yaw), 6);
        Assert.Equal(0f, Target(SurfaceSlot.ElevatorLeft, yaw), 6);

        // The perturbation control: a sign error on the differential would pass a same-sign
        // assertion, so the two elevators must disagree under roll and agree under pitch.
        Assert.NotEqual(Target(SurfaceSlot.ElevatorLeft, roll), Target(SurfaceSlot.ElevatorRight, roll));
        Assert.Equal(Target(SurfaceSlot.ElevatorLeft, pitch), Target(SurfaceSlot.ElevatorRight, pitch), 6);
    }

    /// <summary>The mixed pair: pitch and roll together sum on the elevators, and only there does
    /// the ±0.6 rad clamp bind, the pure terms are authored exactly at their own clamp.</summary>
    [Fact]
    public void TheMixedPairSumsAndOnlyItsClampBinds()
    {
        var mixed = new FlightInput { Pitch = 1f, Roll = 1f };
        Assert.Equal(-0.6f, Target(SurfaceSlot.ElevatorLeft, mixed), 6);   // −0.78 clamped
        Assert.Equal(-0.42f, Target(SurfaceSlot.ElevatorRight, mixed), 6); // −0.6 + 0.18

        var opposed = new FlightInput { Pitch = -1f, Roll = 1f };
        Assert.Equal(0.42f, Target(SurfaceSlot.ElevatorLeft, opposed), 6);
        Assert.Equal(0.6f, Target(SurfaceSlot.ElevatorRight, opposed), 6); // 0.78 clamped

        // Half pitch with full roll stays inside the clamp on both sides, so the sum is visible.
        var partial = new FlightInput { Pitch = 0.5f, Roll = 1f };
        Assert.Equal(-0.48f, Target(SurfaceSlot.ElevatorLeft, partial), 6);
        Assert.Equal(-0.12f, Target(SurfaceSlot.ElevatorRight, partial), 6);

        // The aileron clamp equals its own full-stick angle, so it cannot bind on a normalised
        // stick; it only shows up on an out-of-range channel.
        Assert.Equal(0.5f, Target(SurfaceSlot.AileronRight, new FlightInput { Roll = 3f }), 6);
    }

    /// <summary>The rudder takes the reverse-authority factor and no clamp, so at cruise it barely
    /// moves; the other four slots ignore the factor entirely.</summary>
    [Fact]
    public void OnlyTheRudderTakesReverseAuthority()
    {
        var full = new FlightInput { Roll = 1f, Pitch = 1f, Yaw = 1f };
        Assert.Equal(-0.61086524f * 0.4f, Target(SurfaceSlot.Rudder, full, 0.4f), 6);
        Assert.Equal(-0.5f, Target(SurfaceSlot.AileronLeft, full, 0.4f), 6);
        Assert.Equal(-0.6f, Target(SurfaceSlot.ElevatorLeft, full, 0.4f), 6);

        // The floor the factor never goes below still leaves a visible rudder.
        Assert.Equal(-0.61086524f * 0.2f, Target(SurfaceSlot.Rudder, full, 0.2f), 6);
    }

    /// <summary>Smoothing is exponential at 2/s: after t seconds of held stick the remaining error
    /// is exp(−2t) of the step, and the shape does not depend on the frame rate.</summary>
    [Fact]
    public void TheSlewIsExponentialAtTwoPerSecond()
    {
        var input = new FlightInput { Pitch = 1f };
        float target = Target(SurfaceSlot.ElevatorLeft, input);

        var mix = default(ControlSurfaceMix);
        for (int i = 0; i < 60; i++)
            mix.Advance(Dt, input, 1f, animate: true);

        float expected = target * (1f - MathF.Exp(-ControlSurfaceMix.SmoothPerSec));
        Assert.Equal(expected, mix[SurfaceSlot.ElevatorLeft], 5);

        // Same second at a third of the frame rate lands in the same place (METHOD-11).
        var coarse = default(ControlSurfaceMix);
        for (int i = 0; i < 20; i++)
            coarse.Advance(3f * Dt, input, 1f, animate: true);
        Assert.Equal(expected, coarse[SurfaceSlot.ElevatorLeft], 5);

        // A linear slew would have reached the target inside that second; the exponential has not,
        // and never does exactly. That is the discriminating check against the old 3 units/s ramp.
        Assert.True(Math.Abs(mix[SurfaceSlot.ElevatorLeft]) < Math.Abs(target));
        Assert.True(Math.Abs(mix[SurfaceSlot.ElevatorLeft]) > 0.8f * Math.Abs(target));
    }

    /// <summary>Held stick settles every slot on its target, and centring brings it back.</summary>
    [Fact]
    public void EverySlotSettlesOnItsTargetAndReturns()
    {
        var input = new FlightInput { Roll = 1f, Pitch = -0.5f, Yaw = 1f };
        var mix = default(ControlSurfaceMix);
        for (int i = 0; i < 600; i++)
            mix.Advance(Dt, input, 0.4f, animate: true);

        foreach (var slot in AllSlots)
            Assert.Equal(Target(slot, input, 0.4f), mix[slot], 4);

        for (int i = 0; i < 600; i++)
            mix.Advance(Dt, default, 0.4f, animate: true);
        foreach (var slot in AllSlots)
            Assert.Equal(0f, mix[slot], 4);
    }

    /// <summary>The player-only guard: an aircraft that is not human-piloted never writes a slot,
    /// so its surfaces stay exactly where they stood, however hard its pilot pulls.</summary>
    [Fact]
    public void AnAiAircraftsSurfacesStayFrozen()
    {
        var input = new FlightInput { Roll = 1f, Pitch = 1f, Yaw = 1f };

        var ai = default(ControlSurfaceMix);
        for (int i = 0; i < 600; i++)
            ai.Advance(Dt, input, 1f, animate: false);
        foreach (var slot in AllSlots)
            Assert.Equal(0f, ai[slot], 6);

        // METHOD-9: the same drive with the guard open moves every slot, so the frozen reading is
        // the guard and not an inert instrument.
        var human = default(ControlSurfaceMix);
        for (int i = 0; i < 600; i++)
            human.Advance(Dt, input, 1f, animate: true);
        foreach (var slot in AllSlots)
            Assert.True(Math.Abs(human[slot]) > 0.1f, $"{slot} moved");

        // A pilot who hands control back mid-deflection leaves the surfaces where they were.
        for (int i = 0; i < 600; i++)
            human.Advance(Dt, default, 1f, animate: false);
        Assert.Equal(-0.5f, human[SurfaceSlot.AileronLeft], 4);
    }

    /// <summary>Node names classify to the slot the original's own list population gives them:
    /// six lists, filled by sprintf'd name from index 1 upward.</summary>
    [Fact]
    public void NodeNamesClassifyToTheDecodedSlots()
    {
        Assert.Equal(ControlSurfaces.Kind.AileronLeft, ControlSurfaces.Classify("l_aileron1"));
        Assert.Equal(ControlSurfaces.Kind.AileronRight, ControlSurfaces.Classify("r_aileron2"));
        Assert.Equal(ControlSurfaces.Kind.ElevatorLeft, ControlSurfaces.Classify("l_elevator1"));
        Assert.Equal(ControlSurfaces.Kind.ElevatorRight, ControlSurfaces.Classify("r_elevator"));
        Assert.Equal(ControlSurfaces.Kind.Rudder, ControlSurfaces.Classify("l_rudder1"));
        Assert.Equal(ControlSurfaces.Kind.Rudder, ControlSurfaces.Classify("r_rudder_rotate"));

        // The hinge parent groups still do not classify, or the deflection would double.
        Assert.Equal(ControlSurfaces.Kind.None, ControlSurfaces.Classify("l_rudder"));
        Assert.Equal(ControlSurfaces.Kind.None, ControlSurfaces.Classify("l_aileron"));

        Assert.Equal(Vector3.Right, ControlSurfaces.HingeAxis(ControlSurfaces.Kind.ElevatorLeft));
        Assert.Equal(Vector3.Up, ControlSurfaces.HingeAxis(ControlSurfaces.Kind.Rudder));
    }

    private static float Target(SurfaceSlot slot, FlightInput input, float reverseAuthority = 1f) =>
        ControlSurfaceMix.TargetFor(slot, input, reverseAuthority);
}
