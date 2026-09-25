using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Sticks;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Capturing a stick control on a rebinding screen: its whole button range and its hat directions.
/// An axis moved on a row that takes a full axis becomes one, invert inferred. Also the
/// resting-lever rule, the stamped deadzones, seat 1's sole ownership and the labels. Runs <see cref="ControlCapture"/> over seat 1's <see cref="SeatDeviceState"/> on a
/// roster over <see cref="FakeStickNative"/>, with the user's VKB L (instance 1) and R (2).
/// </summary>
public class StickCaptureTests
{
    // The resting throttle axis 2 of each VKB as --dump-sticks read it: 1.00 on L, -0.57 on R.
    private const short LeverRestL = 32767;
    private const short LeverRestR = -18677;

    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    [Fact]
    public void AButtonPastThePadRangeIsCapturedOnItsStick()
    {
        using var rig = new Rig();
        var capture = rig.Arm(row: InputAction.FireGuns);

        rig.Native.Press(2, 100);
        var binding = capture.Poll(rig.Seat)!.Value;

        Assert.Equal(VkbR.Device, binding.Device);
        Assert.Equal(BindingControl.Button(100), binding.Control);
    }

    [Fact]
    public void AStickButtonHeldWhenTheCaptureArmedMustBeReleasedFirst()
    {
        using var rig = new Rig();
        rig.Native.Press(1, 0);
        var capture = rig.Arm(row: InputAction.FireGuns);

        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.Release(1, 0);
        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.Press(1, 0);
        Assert.Equal(new Binding(VkbL.Device, BindingControl.Button(0)), capture.Poll(rig.Seat));
    }

    [Theory]
    [InlineData(HatDirection.Up)]
    [InlineData(HatDirection.Right)]
    [InlineData(HatDirection.Down)]
    [InlineData(HatDirection.Left)]
    public void AHatDirectionIsCapturedAsThatDirection(HatDirection direction)
    {
        using var rig = new Rig();
        var capture = rig.Arm(row: InputAction.TargetNextEnemy);

        rig.Native.SetHat(2, 0, (byte)direction);

        Assert.Equal(new Binding(VkbR.Device, BindingControl.Hat(0, direction)), capture.Poll(rig.Seat));
    }

    [Fact]
    public void AHatHeldWhenTheCaptureArmedIsMaskedUntilItCentres()
    {
        using var rig = new Rig();
        rig.Native.SetHat(1, 0, (byte)HatDirection.Up);
        var capture = rig.Arm(row: InputAction.LookBack);

        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.SetHat(1, 0, (byte)(HatDirection.Up | HatDirection.Right));
        Assert.Equal(new Binding(VkbL.Device, BindingControl.Hat(0, HatDirection.Right)), capture.Poll(rig.Seat));
    }

    /// <summary>Moving toward the row's own direction binds the axis uninverted, and away from it
    /// inverted, from either row of the pair. PitchUp is the pair's positive row.</summary>
    [Theory]
    [InlineData(InputAction.PitchUp, 32767, false)]
    [InlineData(InputAction.PitchUp, -32768, true)]
    [InlineData(InputAction.PitchDown, -32768, false)]
    [InlineData(InputAction.PitchDown, 32767, true)]
    [InlineData(InputAction.RollLeft, -32768, false)]
    [InlineData(InputAction.YawRight, -32768, true)]
    public void AnAxisMovedOnAPairRowBindsTheWholeAxisWithInvertInferred(InputAction row, int raw, bool inverted)
    {
        using var rig = new Rig();
        var capture = rig.Arm(row);

        rig.Native.SetAxis(2, 1, (short)raw);
        var binding = capture.Poll(rig.Seat)!.Value;

        Assert.Equal(VkbR.Device, binding.Device);
        Assert.Equal(ControlKind.FullAxis, binding.Control.Kind);
        Assert.Equal(1, binding.Control.Index);
        Assert.Equal(inverted, binding.Control.Inverted);
        Assert.Equal(StickCapture.FlightDeadzone, binding.Control.Deadzone);
    }

    /// <summary>The lever row's direction is toward full throttle. L's lever, resting at raw +1 and
    /// pushed toward -1, infers inverted; R's, moved toward +1, does not.</summary>
    [Theory]
    [InlineData(1, LeverRestL, -32768, true)]
    [InlineData(2, LeverRestR, 32767, false)]
    public void ALeverMovedOnTheLeverRowBindsItFromWhereItRested(int instance, int rest, int moved, bool inverted)
    {
        using var rig = new Rig();
        rig.Native.SetAxis(instance, 2, (short)rest);
        var capture = rig.Arm(InputAction.ThrottleLever);

        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.SetAxis(instance, 2, (short)moved);
        var binding = capture.Poll(rig.Seat)!.Value;

        Assert.Equal(instance == 1 ? VkbL.Device : VkbR.Device, binding.Device);
        Assert.Equal(BindingControl.FullAxis(2, inverted, StickCapture.FlightDeadzone), binding.Control);
    }

    /// <summary>A lever parked far off centre, twitching by less than the move threshold, is never
    /// captured on any row. A zero-relative rest band would latch it or mask it forever.</summary>
    [Theory]
    [InlineData(InputAction.ThrottleLever)]
    [InlineData(InputAction.PitchUp)]
    [InlineData(InputAction.FireGuns)]
    public void ARestingOffCentreAxisDoesNotTrigger(InputAction row)
    {
        using var rig = new Rig();
        rig.Native.SetAxis(1, 2, LeverRestL);
        rig.Native.SetAxis(2, 2, LeverRestR);
        var capture = rig.Arm(row);

        foreach (float wobble in new[] { 0f, -0.1f, -0.3f, -0.45f })
        {
            rig.Native.SetAxis(1, 2, Raw(1f + wobble));
            rig.Native.SetAxis(2, 2, Raw(-0.57f - (wobble / 2f)));
            Assert.Null(capture.Poll(rig.Seat));
        }
    }

    /// <summary>A capture armed while reads are blocked takes its rests from the first unblocked
    /// read. The blocked zeros would otherwise make L's lever at 1.00 a move of 1.0.</summary>
    [Fact]
    public void ACaptureArmedWhileBlockedRestsFromTheFirstUnblockedRead()
    {
        using var rig = new Rig();
        rig.Native.SetAxis(1, 2, LeverRestL);
        rig.Blocked = true;
        var capture = rig.Arm(InputAction.ThrottleLever);

        Assert.Null(capture.Poll(rig.Seat));
        rig.Blocked = false;
        Assert.Null(capture.Poll(rig.Seat));
        Assert.Null(capture.Poll(rig.Seat));

        rig.Native.SetAxis(1, 2, -32768);
        Assert.Equal(new Binding(VkbL.Device, BindingControl.FullAxis(2, true, StickCapture.FlightDeadzone)), capture.Poll(rig.Seat));
    }

    /// <summary>A block during a capture drops the rests. Neither the blocked zeros nor a lever moved
    /// while unfocused counts, and the scan re-seeds on the next unblocked read.</summary>
    [Fact]
    public void ABlockDuringACaptureReSeedsTheRests()
    {
        using var rig = new Rig();
        rig.Native.SetAxis(1, 2, LeverRestL);
        rig.Native.Press(2, 7);
        var capture = rig.Arm(InputAction.ThrottleLever);

        rig.Blocked = true;
        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.SetAxis(1, 2, -32768);
        rig.Blocked = false;
        Assert.Null(capture.Poll(rig.Seat));
        Assert.Null(capture.Poll(rig.Seat));

        rig.Native.SetAxis(1, 2, LeverRestL);
        Assert.Equal(new Binding(VkbL.Device, BindingControl.FullAxis(2, false, StickCapture.FlightDeadzone)), capture.Poll(rig.Seat));
    }

    [Theory]
    [InlineData(InputAction.PitchUp, StickCapture.FlightDeadzone)]
    [InlineData(InputAction.RollRight, StickCapture.FlightDeadzone)]
    [InlineData(InputAction.YawLeft, StickCapture.FlightDeadzone)]
    [InlineData(InputAction.ThrottleLever, StickCapture.FlightDeadzone)]
    [InlineData(InputAction.ThrottleUp, StickCapture.RateThrottleDeadzone)]
    [InlineData(InputAction.ThrottleDown, StickCapture.RateThrottleDeadzone)]
    public void TheCapturedFullAxisCarriesItsRowsDeadzone(InputAction row, float deadzone)
    {
        using var rig = new Rig();
        var capture = rig.Arm(row);

        rig.Native.SetAxis(1, 1, -32768);

        Assert.Equal(deadzone, capture.Poll(rig.Seat)!.Value.Control.Deadzone);
        Assert.Equal(0.02f, StickCapture.FlightDeadzone);
        Assert.Equal(0.08f, StickCapture.RateThrottleDeadzone);
    }

    /// <summary>A row in no pair takes a stick axis as a half axis toward the direction moved. It is
    /// captured only once it sits where that half axis fires.</summary>
    [Fact]
    public void AnAxisOnARowWithoutAFullAxisIsAHalfAxis()
    {
        using var rig = new Rig();
        rig.Native.SetAxis(2, 4, Raw(-0.2f));
        var capture = rig.Arm(row: InputAction.FireGuns);

        rig.Native.SetAxis(2, 4, Raw(0.4f));
        Assert.Null(capture.Poll(rig.Seat));
        rig.Native.SetAxis(2, 4, Raw(0.8f));

        Assert.Equal(
            new Binding(VkbR.Device, BindingControl.Axis(4, 1, ControlCapture.CapturedDeadzone)),
            capture.Poll(rig.Seat));
    }

    [Fact]
    public void TheAxisMovedFurthestWinsADiagonalPush()
    {
        using var rig = new Rig();
        var capture = rig.Arm(InputAction.RollRight);

        rig.Native.SetAxis(2, 0, Raw(0.7f));
        rig.Native.SetAxis(2, 1, Raw(-0.9f));

        Assert.Equal(1, capture.Poll(rig.Seat)!.Value.Control.Index);
    }

    [Fact]
    public void AnAxisCanBeCapturedOnTheLastIndex()
    {
        using var rig = new Rig();
        var capture = rig.Arm(InputAction.YawRight);

        rig.Native.SetAxis(1, StickCapture.Axes - 1, 32767);

        Assert.Equal(StickCapture.Axes - 1, capture.Poll(rig.Seat)!.Value.Control.Index);
    }

    [Fact]
    public void ASecondSeatCapturesNoStick()
    {
        using var rig = new Rig(seatIndex: 1);
        var capture = rig.Arm(InputAction.PitchUp);

        rig.Native.Press(2, 100);
        rig.Native.SetHat(2, 0, (byte)HatDirection.Up);
        rig.Native.SetAxis(2, 1, 32767);

        Assert.Empty(rig.Seat.Devices());
        Assert.Null(capture.Poll(rig.Seat));
    }

    [Fact]
    public void ASticksOnlyCaptureIgnoresThePadButStillCancels()
    {
        using var rig = new Rig();
        var seat = new PadAndSticks(rig.Seat);
        var capture = new ControlCapture(DefaultBindings.AnyPad, readsKeyboard: false, InputAction.FireGuns, sticksOnly: true);
        capture.Arm(seat);

        seat.PadButtons.Add((int)JoyButton.A);
        Assert.Null(capture.Poll(seat));
        rig.Native.Press(1, 3);
        Assert.Equal(new Binding(VkbL.Device, BindingControl.Button(3)), capture.Poll(seat));

        seat.PadButtons.Add((int)ControlCapture.CancelButton);
        Assert.True(capture.Cancelled(seat));
    }

    [Fact]
    public void AStickIsCapturedAfterThePadsButtons()
    {
        using var rig = new Rig();
        var seat = new PadAndSticks(rig.Seat);
        var capture = new ControlCapture(DefaultBindings.AnyPad, readsKeyboard: false, InputAction.FireGuns);
        capture.Arm(seat);

        seat.PadButtons.Add((int)JoyButton.A);
        rig.Native.Press(1, 3);

        Assert.Equal(DefaultBindings.AnyPad, capture.Poll(seat)!.Value.Device);
    }

    [Fact]
    public void AStickLabelLeadsWithItsProfilesShortName()
    {
        Func<DeviceId, string?> names = d => StickLabels.Prefix(d, m => m == VkbR ? "R" : null);

        Assert.Equal("R Button 17", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.Button(17)), names));
        Assert.Equal("R Axis 3", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.FullAxis(3, false, 0.02f)), names));
        Assert.Equal("R Axis 1 inverted", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.FullAxis(1, true, 0.02f)), names));
        Assert.Equal("R Axis 4 -", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.Axis(4, -1, 0.5f)), names));
        Assert.Equal("R Hat Up", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.Hat(0, HatDirection.Up)), names));
        Assert.Equal("R Hat 1 Left", BindingLabels.Describe(new Binding(VkbR.Device, BindingControl.Hat(1, HatDirection.Left)), names));
        Assert.Equal("Stick 231D/0201 Button 0", BindingLabels.Describe(new Binding(VkbL.Device, BindingControl.Button(0)), names));
    }

    [Fact]
    public void ANonStickDeviceKeepsItsOwnLabel()
    {
        Func<DeviceId, string?> names = d => StickLabels.Prefix(d, _ => "R");
        var pad = new Binding(DefaultBindings.AnyPad, BindingControl.Button((int)JoyButton.A));

        Assert.Null(StickLabels.Prefix(DefaultBindings.AnyPad, _ => "R"));
        Assert.Equal(BindingLabels.Describe(pad, null), BindingLabels.Describe(pad, names));
        Assert.Equal("Stick 231D/0200", StickLabels.Prefix(VkbR.Device, _ => "  "));
    }

    private static short Raw(float value) => (short)Math.Round(value * 32767f);

    // Seat 1's reader over a roster holding L (instance 1) and R (2), every control at rest.
    private sealed class Rig : IDisposable
    {
        public Rig(int seatIndex = 0)
        {
            Native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
            Native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
            Roster = new StickRoster(Native, Array.Empty<StickModel>, () => Blocked);
            Roster.Update();
            Seat = new SeatDeviceState(DefaultBindings.AnyPad, () => null, sticks: new StickDeviceState(() => seatIndex, () => Roster));
        }

        public FakeStickNative Native { get; } = new();

        public StickRoster Roster { get; }

        public bool Blocked { get; set; }

        public SeatDeviceState Seat { get; }

        public ControlCapture Arm(InputAction row)
        {
            var capture = new ControlCapture(DefaultBindings.AnyPad, readsKeyboard: false, row);
            capture.Arm(Seat);
            return capture;
        }

        public void Dispose() => Roster.Dispose();
    }

    // A seat whose pad placeholder answers from a set of held buttons, with sticks from the rig.
    // It shows the pad-then-stick order and the sticks-only mode without the engine's pads.
    private sealed class PadAndSticks : IDeviceState, IStickDevices
    {
        private readonly SeatDeviceState _sticks;

        public PadAndSticks(SeatDeviceState sticks) => _sticks = sticks;

        public HashSet<int> PadButtons { get; } = new();

        public bool ReadsBlocked => _sticks.ReadsBlocked;

        public bool IsKeyDown(DeviceId device, int keyCode) => false;

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public bool IsButtonDown(DeviceId device, int button) =>
            device == DefaultBindings.AnyPad ? PadButtons.Contains(button) : _sticks.IsButtonDown(device, button);

        public float AxisValue(DeviceId device, int axis) =>
            device == DefaultBindings.AnyPad ? 0f : _sticks.AxisValue(device, axis);

        public HatDirection HatState(DeviceId device, int hat) => _sticks.HatState(device, hat);

        public IReadOnlyList<DeviceId> Devices() => _sticks.Devices();
    }
}
