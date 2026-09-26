using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Sticks;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The two analogue shares of a seat's flight command. One keymap holds pad and stick rows, and
/// <see cref="StickSplit"/> reads them apart. A pad bends through the pad curve and a stick passes
/// linearly. A throttle-pair full axis is a rate in proportion to deflection.
/// A lever on an unplugged stick releases rather than reading as half throttle.
/// </summary>
public class AnalogAxesTests
{
    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(-0.1f)]
    [InlineData(1f)]
    public void AStickPastAZeroDeadzoneCommandsItsOwnDeflection(float raw)
    {
        var map = new ActionMap();
        map.Assign(InputAction.PitchUp, new Binding(VkbR.Device, BindingControl.FullAxis(1, inverted: false, 0f)));
        var seat = new Seat().Axis(VkbR.Device, 1, raw);

        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(seat)));

        Assert.Equal(raw, stick.Pitch, 6);
    }

    [Fact]
    public void AStickDeadzoneRescalesLinearlyFromItsEdge()
    {
        var map = new ActionMap();
        map.Assign(InputAction.PitchUp, new Binding(VkbR.Device, BindingControl.FullAxis(1, inverted: false, 0.02f)));
        var seat = new Seat().Axis(VkbR.Device, 1, 0.51f);

        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(seat)));

        Assert.Equal(0.5f, stick.Pitch, 5);
    }

    /// <summary>The live device path: the roster's raw 10% reaches pitch as 10%. The pad curve
    /// would have read it as zero, inside its 0.15 deadzone.</summary>
    [Fact]
    public void ARosterStickAtTenPercentPitchesTenPercent()
    {
        var native = new FakeStickNative();
        native.Plug(1, "R", VkbR);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        native.SetAxis(1, 1, 3277);
        var map = new ActionMap();
        map.Assign(InputAction.PitchUp, new Binding(VkbR.Device, BindingControl.FullAxis(1, inverted: false, 0f)));
        var state = new StickDeviceState(() => StickDeviceState.OwningSeat, () => roster);

        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(state)));

        Assert.Equal(0.1f, stick.Pitch, 4);
    }

    [Fact]
    public void StickRollAndYawTakeThePadsSigns()
    {
        var map = new ActionMap();
        map.Assign(InputAction.RollRight, new Binding(VkbR.Device, BindingControl.FullAxis(0, inverted: false, 0f)));
        map.Assign(InputAction.YawRight, new Binding(VkbR.Device, BindingControl.FullAxis(5, inverted: false, 0f)));
        map.Add(InputAction.RollRight, new Binding(DefaultBindings.AnyPad, BindingControl.Axis(0, 1, 0f)));
        map.Add(InputAction.YawRight, new Binding(DefaultBindings.AnyPad, BindingControl.Axis(3, 1, 0f)));
        var seat = new Seat()
            .Axis(VkbR.Device, 0, 1f).Axis(VkbR.Device, 5, 1f)
            .Axis(DefaultBindings.AnyPad, 0, 1f).Axis(DefaultBindings.AnyPad, 3, 1f);

        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(seat)));
        var pad = AnalogAxes.Pad(map.Resolve(StickSplit.WithoutSticks(seat)));

        Assert.Equal(pad.Roll, stick.Roll);
        Assert.Equal(pad.Yaw, stick.Yaw);
        Assert.Equal(-1f, stick.Roll);
    }

    /// <summary>The pad keeps the curve bit for bit: the shipped half-axis reads the old formula's
    /// value exactly, at every point across its travel.</summary>
    [Fact]
    public void APadAxisStillBendsThroughThePadCurve()
    {
        var map = new ActionMap();
        map.Add(InputAction.PitchUp, new Binding(DefaultBindings.AnyPad, BindingControl.Axis(1, 1, 0f)));
        map.Add(InputAction.RollRight, new Binding(DefaultBindings.AnyPad, BindingControl.Axis(0, 1, 0f)));
        for (int i = 0; i <= 100; i++)
        {
            float raw = i / 100f;
            var seat = new Seat().Axis(DefaultBindings.AnyPad, 1, raw).Axis(DefaultBindings.AnyPad, 0, raw);

            var pad = AnalogAxes.Pad(map.Resolve(StickSplit.WithoutSticks(seat)));

            Assert.Equal(OldStickCurve(raw), pad.Pitch);
            Assert.Equal(-OldStickCurve(raw), pad.Roll);
        }

        Assert.Equal(0f, AnalogAxes.PadCurve(0.1f));
    }

    /// <summary>Each share reads its own rows only. A stick bound beside a pad on one action is
    /// neither curved nor merged into the pad's deepest-wins read.</summary>
    [Fact]
    public void ThePadAndStickSharesReadOnlyTheirOwnRows()
    {
        var map = new ActionMap();
        map.Add(InputAction.PitchUp, new Binding(DefaultBindings.AnyPad, BindingControl.Axis(1, 1, 0f)));
        map.Assign(InputAction.PitchUp, new Binding(VkbR.Device, BindingControl.FullAxis(1, inverted: false, 0f)));
        var seat = new Seat().Axis(VkbR.Device, 1, 0.4f);

        var pad = AnalogAxes.Pad(map.Resolve(StickSplit.WithoutSticks(seat)));
        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(seat)));

        Assert.Equal(0f, pad.Pitch);
        Assert.Equal(0.4f, stick.Pitch, 6);
    }

    [Fact]
    public void TheSplitSilencesTheOtherSidesButtonsAndHats()
    {
        var seat = new Seat { Buttons = true, Hat = HatDirection.Up };

        Assert.False(StickSplit.SticksOnly(seat).IsButtonDown(DefaultBindings.AnyPad, 0));
        Assert.True(StickSplit.SticksOnly(seat).IsButtonDown(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, StickSplit.WithoutSticks(seat).HatState(VkbL.Device, 0));
        Assert.True(StickSplit.WithoutSticks(seat).IsButtonDown(DefaultBindings.AnyPad, 0));
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(-1f)]
    public void AStickOnTheThrottlePairIsARateInProportionToDeflection(float raw)
    {
        var map = new ActionMap();
        map.Assign(InputAction.ThrottleUp, new Binding(VkbL.Device, BindingControl.FullAxis(1, inverted: false, 0f)));
        var seat = new Seat().Axis(VkbL.Device, 1, raw);

        var stick = AnalogAxes.Stick(map.Resolve(StickSplit.SticksOnly(seat)));

        Assert.Equal(raw, stick.ThrottleRate, 6);
    }

    [Fact]
    public void AnUnboundLeverHasNoPosition()
    {
        Assert.Null(AnalogAxes.LeverPosition(Array.Empty<Binding>(), 0f, 0f, null));
    }

    [Fact]
    public void APadBoundLeverReadsWithNoSticksAtAll()
    {
        var row = new[] { new Binding(DefaultBindings.AnyPad, BindingControl.Axis(5, 1, 0f)) };

        Assert.Equal(0.7f, AnalogAxes.LeverPosition(row, 0.7f, 0f, null));
    }

    /// <summary>An engaged stick lever whose stick is unplugged lets go, instead of reading its
    /// centred axis as a move to half throttle. Plugged back in, its first reading only seeds.</summary>
    [Fact]
    public void UnpluggingTheLeversStickReleasesTheTakeover()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        var row = new[] { new Binding(VkbL.Device, BindingControl.FullAxis(2, inverted: false, 0f)) };
        var takeover = new LeverTakeover();

        Assert.Null(Step(takeover, row, 0.9f, roster));
        Assert.Equal(0.6f, Step(takeover, row, 0.6f, roster));
        Assert.True(takeover.Engaged);

        native.Unplug(1);
        roster.Update();
        Assert.Null(AnalogAxes.LeverPosition(row, 0f, 0.5f, roster));
        Assert.Null(Step(takeover, row, 0.5f, roster));
        Assert.False(takeover.Engaged);

        native.Plug(2, "L", VkbL);
        roster.Update();
        Assert.Null(Step(takeover, row, 0.3f, roster));
        Assert.False(takeover.Engaged);
    }

    [Fact]
    public void ALeverOnBothSidesKeepsReadingThePadWhenTheStickGoes()
    {
        var row = new[]
        {
            new Binding(DefaultBindings.AnyPad, BindingControl.Axis(5, 1, 0f)),
            new Binding(VkbL.Device, BindingControl.FullAxis(2, inverted: false, 0f)),
        };

        Assert.Equal(0.2f, AnalogAxes.LeverPosition(row, 0.2f, 0.5f, null));
    }

    [Fact]
    public void ASeatWithNoRosterHasNoStickConnected()
    {
        Assert.False(AnalogAxes.Connected(null, VkbL));
    }

    private static float? Step(LeverTakeover takeover, Binding[] row, float stickValue, StickRoster roster) =>
        AnalogAxes.StepLever(takeover, AnalogAxes.LeverPosition(row, 0f, stickValue, roster), otherCommand: false);

    // FlightController's StickCurve as it stood before the stick split, kept verbatim as the
    // reference the pad share must match exactly.
    private static float OldStickCurve(float v)
    {
        const float deadzone = 0.15f;
        float a = Godot.Mathf.Abs(v);
        if (a < deadzone)
            return 0f;
        float t = Godot.Mathf.Min(1f, (a - deadzone) / (1f - deadzone));
        return Godot.Mathf.Sign(v) * t * t;
    }

    // A seat that answers every identity: axes by (device, index), every button one flag, every hat
    // one direction.
    private sealed class Seat : IDeviceState
    {
        private readonly Dictionary<(DeviceId, int), float> _axes = new();

        public bool Buttons { get; init; }

        public HatDirection Hat { get; init; }

        public Seat Axis(DeviceId device, int axis, float value)
        {
            _axes[(device, axis)] = value;
            return this;
        }

        public bool IsKeyDown(DeviceId device, int keyCode) => false;

        public bool IsButtonDown(DeviceId device, int button) => Buttons;

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) => _axes.GetValueOrDefault((device, axis));

        public HatDirection HatState(DeviceId device, int hat) => Hat;
    }
}
