using System;
using System.Collections.Generic;
using CSVM.Bindings;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The full-axis binding a flight stick flies with: one control on an action pair. Each side reads
/// 0 at the deadzone edge and 1 at full travel, and invert swaps the sides. It is one binding held
/// by both rows, so capturing, stealing and clearing treat the pair as one. It shares its physical
/// axis with any half-axis binding on the same device.
/// </summary>
public class FullAxisBindingTests
{
    private static readonly DeviceId Stick = DeviceId.Joypad("03005fcf1d2300000002000000000000");
    private static readonly DeviceId OtherStick = DeviceId.Joypad("03003fc91d2300000102000000000000");

    [Fact]
    public void EachSideReadsZeroAtTheDeadzoneEdgeAndOneAtFullTravel()
    {
        var binding = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0.2f));
        var state = new FakeDevices();

        state.Axes[(Stick, 1)] = 0.2f;
        Assert.Equal(ControlValue.None, binding.Resolve(state, default, 1));

        state.Axes[(Stick, 1)] = 0.6f;
        Assert.Equal(0.5f, binding.Resolve(state, default, 1).Value, 5);
        Assert.Equal(ControlValue.None, binding.Resolve(state, default, -1));

        state.Axes[(Stick, 1)] = 1f;
        Assert.Equal(1f, binding.Resolve(state, default, 1).Value, 5);

        state.Axes[(Stick, 1)] = -1f;
        Assert.Equal(1f, binding.Resolve(state, default, -1).Value, 5);
        Assert.Equal(ControlValue.None, binding.Resolve(state, default, 1));
    }

    [Fact]
    public void InvertSwapsWhichSideTheTravelFeeds()
    {
        var binding = new Binding(Stick, BindingControl.FullAxis(1, inverted: true, 0f));
        var state = new FakeDevices();
        state.Axes[(Stick, 1)] = 0.75f;

        Assert.Equal(ControlValue.None, binding.Resolve(state, default, 1));
        Assert.Equal(0.75f, binding.Resolve(state, default, -1).Value, 5);
    }

    /// <summary>A full axis resolved without a side has no action to feed, so it reads nothing
    /// rather than guessing one.</summary>
    [Fact]
    public void AFullAxisResolvedWithoutASideReadsNothing()
    {
        var binding = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0f));
        var state = new FakeDevices();
        state.Axes[(Stick, 1)] = 1f;

        Assert.Equal(ControlValue.None, binding.Resolve(state));
    }

    [Fact]
    public void TheFactoryHonoursADeadzoneUpToPointNineFiveAndNoFurther()
    {
        Assert.Equal(0.95f, BindingControl.FullAxis(0, false, 0.95f).Deadzone);
        Assert.Equal(0f, BindingControl.FullAxis(0, false, 0f).Deadzone);
        Assert.Throws<ArgumentOutOfRangeException>(() => BindingControl.FullAxis(0, false, 0.951f));
        Assert.Throws<ArgumentOutOfRangeException>(() => BindingControl.FullAxis(0, false, -0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => BindingControl.FullAxis(0, false, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => BindingControl.FullAxis(-1, false, 0f));
        Assert.True(BindingControl.FullAxis(0, true, 0f).Inverted);
        Assert.False(BindingControl.FullAxis(0, false, 0f).Inverted);
    }

    /// <summary>The snapshot's signed axis is the stick, linear past the deadzone. That is the point
    /// of resolving into the existing pair: the flight model learns no new read.</summary>
    [Fact]
    public void AMapResolvesOneFullAxisIntoBothActionsOfItsPair()
    {
        var map = new ActionMap();
        var binding = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0.02f));
        Assert.True(map.Add(InputAction.PitchUp, binding));
        var state = new FakeDevices();

        Assert.Equal(new[] { binding }, map.Bindings(InputAction.PitchUp));
        Assert.Equal(new[] { binding }, map.Bindings(InputAction.PitchDown));

        state.Axes[(Stick, 1)] = 0.51f;
        var snapshot = map.Resolve(state);
        Assert.Equal(0.5f, snapshot.Axis(InputAction.PitchUp, InputAction.PitchDown), 4);
        Assert.True(snapshot.Held(InputAction.PitchUp));
        Assert.False(snapshot.Held(InputAction.PitchDown));

        state.Axes[(Stick, 1)] = -1f;
        snapshot = map.Resolve(state);
        Assert.Equal(-1f, snapshot.Axis(InputAction.PitchUp, InputAction.PitchDown), 4);
    }

    [Theory]
    [InlineData(InputAction.PitchUp, InputAction.PitchDown)]
    [InlineData(InputAction.RollRight, InputAction.RollLeft)]
    [InlineData(InputAction.YawRight, InputAction.YawLeft)]
    [InlineData(InputAction.ThrottleUp, InputAction.ThrottleDown)]
    public void EveryPairNamesItsPartnerAndItsSides(InputAction positive, InputAction negative)
    {
        Assert.Equal(negative, AxisPairs.PartnerOf(positive));
        Assert.Equal(positive, AxisPairs.PartnerOf(negative));
        Assert.Equal(1, AxisPairs.SideOf(positive));
        Assert.Equal(-1, AxisPairs.SideOf(negative));
        Assert.Equal(positive, AxisPairs.PositiveOf(negative));
    }

    [Fact]
    public void AnActionOutsideEveryPairHasNoPartnerAndTakesNoFullAxis()
    {
        var map = new ActionMap();
        var binding = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0f));

        Assert.Null(AxisPairs.PartnerOf(InputAction.FireGuns));
        Assert.Equal(0, AxisPairs.SideOf(InputAction.FireGuns));
        Assert.False(map.Add(InputAction.FireGuns, binding));
        Assert.Empty(map.Bindings(InputAction.FireGuns));
        Assert.Throws<ArgumentException>(() => map.Assign(InputAction.FireGuns, binding));
    }

    /// <summary>Capture infers invert from the row: moving toward the row's own direction binds
    /// the axis the way round that makes that row fire.</summary>
    [Theory]
    [InlineData(InputAction.PitchUp, 1, false)]
    [InlineData(InputAction.PitchUp, -1, true)]
    [InlineData(InputAction.PitchDown, 1, true)]
    [InlineData(InputAction.PitchDown, -1, false)]
    [InlineData(InputAction.RollLeft, -1, false)]
    [InlineData(InputAction.ThrottleUp, -1, true)]
    public void ACaptureOnEitherRowInfersInvertFromTheDirectionMoved(InputAction row, int moved, bool inverted)
    {
        var control = AxisPairs.FullAxisFor(row, 3, moved, 0.08f);

        Assert.Equal(ControlKind.FullAxis, control.Kind);
        Assert.Equal(3, control.Index);
        Assert.Equal(inverted, control.Inverted);
        Assert.Equal(0.08f, control.Deadzone);

        var state = new FakeDevices();
        state.Axes[(Stick, 3)] = moved;
        var map = new ActionMap();
        map.Assign(row, new Binding(Stick, control));
        Assert.True(map.Resolve(state).Held(row));
    }

    [Fact]
    public void ACaptureOnARowOutsideEveryPairIsRefused()
    {
        Assert.Throws<ArgumentException>(() => AxisPairs.FullAxisFor(InputAction.FireGuns, 0, 1, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AxisPairs.FullAxisFor(InputAction.PitchUp, 0, 0, 0f));
    }

    /// <summary>A full axis covers both halves of its travel. A half-axis on the same axis of the
    /// same device is therefore the same control whichever sign it names. Invert and deadzone are
    /// not identity.</summary>
    [Fact]
    public void AFullAxisIsTheSameControlAsEitherHalfOfItsAxisOnTheSameDevice()
    {
        var full = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0.02f));

        Assert.True(ActionMap.SameControl(full, new Binding(Stick, BindingControl.Axis(1, 1, 0.5f))));
        Assert.True(ActionMap.SameControl(new Binding(Stick, BindingControl.Axis(1, -1, 0f)), full));
        Assert.True(ActionMap.SameControl(full, new Binding(Stick, BindingControl.FullAxis(1, true, 0.3f))));
        Assert.False(ActionMap.SameControl(full, new Binding(Stick, BindingControl.Axis(2, 1, 0f))));
        Assert.False(ActionMap.SameControl(full, new Binding(OtherStick, BindingControl.Axis(1, 1, 0f))));
        Assert.False(ActionMap.SameControl(full, new Binding(Stick, BindingControl.Button(1))));
        Assert.False(ActionMap.SameControl(
            new Binding(Stick, BindingControl.Axis(1, 1, 0f)), new Binding(Stick, BindingControl.Axis(1, -1, 0f))));
    }

    [Fact]
    public void AssigningAFullAxisStealsAHalfOfItsAxisAndDoesNotReportThePartner()
    {
        var map = new ActionMap();
        var half = new Binding(Stick, BindingControl.Axis(1, 1, 0.5f));
        map.Add(InputAction.FireGuns, half);
        map.Add(InputAction.PitchDown, new Binding(Stick, BindingControl.Axis(1, -1, 0f)));
        var full = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0.02f));

        Assert.Equal(new[] { InputAction.FireGuns }, map.Assign(InputAction.PitchUp, full));
        Assert.Empty(map.Bindings(InputAction.FireGuns));
        Assert.Equal(new[] { full }, map.Bindings(InputAction.PitchUp));
        Assert.Equal(new[] { full }, map.Bindings(InputAction.PitchDown));
    }

    /// <summary>A half-axis taken for another action takes the full axis off both rows: it was one
    /// binding, and half of it cannot stay behind.</summary>
    [Fact]
    public void AssigningAHalfOfTheAxisTakesTheFullAxisOffBothRows()
    {
        var map = new ActionMap();
        map.Assign(InputAction.PitchUp, new Binding(Stick, BindingControl.FullAxis(1, false, 0f)));
        var half = new Binding(Stick, BindingControl.Axis(1, -1, 0.5f));

        Assert.Equal(new[] { InputAction.PitchUp, InputAction.PitchDown }, map.Assign(InputAction.Nitro, half));
        Assert.Empty(map.Bindings(InputAction.PitchUp));
        Assert.Empty(map.Bindings(InputAction.PitchDown));
        Assert.Equal(new[] { half }, map.Bindings(InputAction.Nitro));
    }

    [Fact]
    public void RecapturingTheSameAxisOnThePairReplacesItAndStealsNothing()
    {
        var map = new ActionMap();
        map.Assign(InputAction.RollLeft, new Binding(Stick, BindingControl.FullAxis(0, false, 0.02f)));
        var flipped = new Binding(Stick, BindingControl.FullAxis(0, true, 0.02f));

        Assert.Empty(map.Assign(InputAction.RollRight, flipped));
        Assert.Equal(new[] { flipped }, map.Bindings(InputAction.RollLeft));
        Assert.Equal(new[] { flipped }, map.Bindings(InputAction.RollRight));
        Assert.Equal(new[] { InputAction.RollLeft, InputAction.RollRight }, map.OwnersOf(flipped));
    }

    [Fact]
    public void UnassigningFromEitherRowClearsBoth()
    {
        var map = new ActionMap();
        var full = new Binding(Stick, BindingControl.FullAxis(2, false, 0f));
        var key = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Comma));
        map.Assign(InputAction.YawLeft, full);
        map.Add(InputAction.YawLeft, key);

        Assert.True(map.Unassign(InputAction.YawRight, full));
        Assert.Equal(new[] { key }, map.Bindings(InputAction.YawLeft));
        Assert.Empty(map.Bindings(InputAction.YawRight));
    }

    [Fact]
    public void ClearingEitherRowTakesTheFullAxisOffItsPartner()
    {
        var map = new ActionMap();
        var key = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Minus));
        map.Assign(InputAction.ThrottleUp, new Binding(Stick, BindingControl.FullAxis(1, true, 0.08f)));
        map.Add(InputAction.ThrottleDown, key);

        map.Clear(InputAction.ThrottleUp);

        Assert.Empty(map.Bindings(InputAction.ThrottleUp));
        Assert.Equal(new[] { key }, map.Bindings(InputAction.ThrottleDown));
    }

    /// <summary>A second copy with another deadzone replaces the first rather than stacking, so the
    /// pair never holds two readings of one axis.</summary>
    [Fact]
    public void AddingTheSameFullAxisWithAnotherDeadzoneReplacesIt()
    {
        var map = new ActionMap();
        map.Add(InputAction.PitchUp, new Binding(Stick, BindingControl.FullAxis(1, false, 0.02f)));
        var wider = new Binding(Stick, BindingControl.FullAxis(1, false, 0.1f));

        map.Add(InputAction.PitchDown, wider);

        Assert.Equal(new[] { wider }, map.Bindings(InputAction.PitchUp));
        Assert.Equal(new[] { wider }, map.Bindings(InputAction.PitchDown));
    }

    /// <summary>Pads keep their half-axis defaults untouched: a stick's full axis is on another
    /// device identity, so the steal rule never reaches the pad's rows.</summary>
    [Fact]
    public void AStickFullAxisLeavesThePadHalfAxisDefaultsAlone()
    {
        var pad = DeviceId.Joypad("030000005e040000e002000000007801");
        var map = DefaultBindings.MapFor(InputContext.Flight, pad);
        var before = new List<Binding>(map.Bindings(InputAction.PitchUp));

        Assert.Empty(map.Assign(InputAction.PitchUp, new Binding(Stick, BindingControl.FullAxis(1, false, 0.02f))));
        Assert.Contains(new Binding(pad, BindingControl.Axis((int)JoyAxis.LeftY, 1, 0f)), map.Bindings(InputAction.PitchUp));
        Assert.Equal(before.Count + 1, map.Bindings(InputAction.PitchUp).Count);
    }

    [Fact]
    public void AStickHatDirectionResolvesThroughAMap()
    {
        var map = new ActionMap();
        map.Assign(InputAction.SelectGunGroup, new Binding(Stick, BindingControl.Hat(0, HatDirection.Right)));
        var state = new FakeDevices();
        state.Hats[(Stick, 0)] = HatDirection.Up | HatDirection.Right;

        Assert.True(map.Resolve(state).Held(InputAction.SelectGunGroup));
        state.Hats[(Stick, 0)] = HatDirection.Left;
        Assert.False(map.Resolve(state).Held(InputAction.SelectGunGroup));
    }

    [Fact]
    public void APromptOnThePadSideNamesAFullAxis()
    {
        var full = new Binding(Stick, BindingControl.FullAxis(1, false, 0f));
        var key = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Down));

        Assert.Equal(full, ActiveDevice.PromptBinding(new[] { key, full }, DeviceSide.Pad, readsKeyboard: true));
    }

    private sealed class FakeDevices : IDeviceState
    {
        public Dictionary<(DeviceId Device, int Index), float> Axes { get; } = new();

        public Dictionary<(DeviceId Device, int Index), HatDirection> Hats { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) => false;

        public bool IsButtonDown(DeviceId device, int button) => false;

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) =>
            Axes.TryGetValue((device, axis), out float value) ? value : 0f;

        public HatDirection HatState(DeviceId device, int hat) =>
            Hats.TryGetValue((device, hat), out var state) ? state : HatDirection.None;
    }
}
