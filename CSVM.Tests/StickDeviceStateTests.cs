using System;
using CSVM.Bindings;
using CSVM.Sticks;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Stick identities as the binding model reads them. Covers the model-keyed <see cref="DeviceId"/>
/// and its stored token, and <see cref="StickDeviceState"/> over a roster on a fake library. Also
/// covers seat 1's <see cref="SeatDeviceState"/> answering for sticks beside its pad placeholder.
/// The models are the user's VKB Gladiator EVO L and R.
/// </summary>
public class StickDeviceStateTests
{
    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    [Fact]
    public void AStickIdentityIsItsModelBehindTheStickPrefix()
    {
        Assert.Equal(DeviceId.Joypad("stick:231D/0201"), VkbL.Device);
        Assert.Equal("pad:stick:231D/0201", VkbL.Device.ToString());
        Assert.True(StickModel.TryFromDevice(VkbL.Device, out var model));
        Assert.Equal(VkbL, model);
    }

    [Theory]
    [InlineData("stick:231d/0201")]
    [InlineData(" stick:231D/0201 ")]
    public void AHandEditedStickIdentityStillNamesItsModel(string id)
    {
        Assert.True(StickModel.TryFromDevice(DeviceId.Joypad(id), out var model));
        Assert.Equal(VkbL, model);
    }

    [Theory]
    [InlineData("03005fcf1d2300000002000000000000")]
    [InlineData("stick:")]
    [InlineData("stick:231D")]
    [InlineData("stick:231D/0201/1")]
    [InlineData("Stick:231D/0201")]
    [InlineData("*")]
    public void APadIdentityOrAMalformedStickIsNoStick(string id)
        => Assert.False(StickModel.TryFromDevice(DeviceId.Joypad(id), out _));

    [Fact]
    public void KeyboardMouseAndNoneAreNoStick()
    {
        Assert.False(StickModel.TryFromDevice(DeviceId.Keyboard, out _));
        Assert.False(StickModel.TryFromDevice(DeviceId.Mouse, out _));
        Assert.False(StickModel.TryFromDevice(default, out _));
    }

    [Theory]
    [InlineData("pad:stick:231D/0201/button:#100")]
    [InlineData("pad:stick:231D/0200/fullaxis:1+@0.02")]
    [InlineData("pad:stick:231D/0200/fullaxis:6-@0.08")]
    [InlineData("pad:stick:231D/0201/hat:0:Up")]
    public void StickTokensRoundTripThroughTheStore(string token)
    {
        var binding = BindingStore.Decode(token);

        Assert.NotNull(binding);
        Assert.True(StickModel.TryFromDevice(binding!.Value.Device, out _));
        Assert.Equal(token, BindingStore.Encode(binding.Value));
    }

    [Fact]
    public void LButtonOneAndRButtonOneResolveApart()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => 0, () => roster);
        native.Press(1, 1);

        Assert.True(state.IsButtonDown(VkbL.Device, 1));
        Assert.False(state.IsButtonDown(VkbR.Device, 1));
        Assert.True(new Binding(VkbL.Device, BindingControl.Button(1)).Resolve(state).Pressed);
        Assert.False(new Binding(VkbR.Device, BindingControl.Button(1)).Resolve(state).Pressed);
    }

    [Fact]
    public void AxesResolvePerModel()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => 0, () => roster);
        native.SetAxis(2, 1, 32767);

        Assert.Equal(1f, state.AxisValue(VkbR.Device, 1));
        Assert.Equal(0f, state.AxisValue(VkbL.Device, 1));
    }

    [Theory]
    [InlineData(HatDirection.Up)]
    [InlineData(HatDirection.Right)]
    [InlineData(HatDirection.Down)]
    [InlineData(HatDirection.Left)]
    public void HatsResolvePerDirection(HatDirection pushed)
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => 0, () => roster);
        native.SetHat(2, 0, (byte)pushed);

        foreach (var direction in new[] { HatDirection.Up, HatDirection.Right, HatDirection.Down, HatDirection.Left })
        {
            bool held = new Binding(VkbR.Device, BindingControl.Hat(0, direction)).Resolve(state).Pressed;
            Assert.Equal(direction == pushed, held);
        }

        Assert.Equal(HatDirection.None, state.HatState(VkbL.Device, 0));
    }

    [Fact]
    public void ADiagonalHoldsBothOfItsDirections()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => 0, () => roster);
        native.SetHat(1, 0, (byte)(HatDirection.Up | HatDirection.Left));

        Assert.True(new Binding(VkbL.Device, BindingControl.Hat(0, HatDirection.Up)).Resolve(state).Pressed);
        Assert.True(new Binding(VkbL.Device, BindingControl.Hat(0, HatDirection.Left)).Resolve(state).Pressed);
        Assert.False(new Binding(VkbL.Device, BindingControl.Hat(0, HatDirection.Down)).Resolve(state).Pressed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ASplitscreenSeatNeverReadsAStick(int playerIndex)
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => playerIndex, () => roster);
        native.Set(1, axis: 0, raw: 32767, button: 1, hat: 1);

        Assert.False(state.IsButtonDown(VkbL.Device, 1));
        Assert.Equal(0f, state.AxisValue(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, state.HatState(VkbL.Device, 0));
        Assert.Empty(state.Devices());
    }

    [Fact]
    public void TheSeatIsReadEachTimeSoAReseatedPlayerLosesTheSticks()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        int seat = 0;
        var state = new StickDeviceState(() => seat, () => roster);
        native.Press(1, 4);

        Assert.True(state.IsButtonDown(VkbL.Device, 4));
        seat = 1;
        Assert.False(state.IsButtonDown(VkbL.Device, 4));
    }

    [Fact]
    public void BlockedReadsAreNeutralWhileTheDevicesStayListed()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        bool blocked = true;
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => blocked);
        roster.Update();
        var state = new StickDeviceState(() => 0, () => roster);
        native.Set(1, axis: 0, raw: 32767, button: 1, hat: 1);

        Assert.False(state.IsButtonDown(VkbL.Device, 1));
        Assert.Equal(0f, state.AxisValue(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, state.HatState(VkbL.Device, 0));
        Assert.Equal(new[] { VkbL.Device }, state.Devices());

        blocked = false;
        Assert.True(state.IsButtonDown(VkbL.Device, 1));
    }

    [Fact]
    public void ANullRosterReadsNothing()
    {
        var state = new StickDeviceState(() => 0, () => null);

        Assert.False(state.IsButtonDown(VkbL.Device, 0));
        Assert.Equal(0f, state.AxisValue(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, state.HatState(VkbL.Device, 0));
        Assert.Empty(state.Devices());
    }

    [Fact]
    public void AStickReaderAnswersNothingForAPadKeyOrMouse()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var state = new StickDeviceState(() => 0, () => roster);
        native.Set(1, axis: 0, raw: 32767, button: 0, hat: 1);

        Assert.False(state.IsButtonDown(DefaultBindings.AnyPad, 0));
        Assert.Equal(0f, state.AxisValue(DefaultBindings.AnyPad, 0));
        Assert.False(state.IsKeyDown(DeviceId.Keyboard, 0));
        Assert.False(state.IsMouseButtonDown(DeviceId.Mouse, 1));
    }

    [Fact]
    public void DevicesListsEachConnectedModelOnceInRosterOrder()
    {
        var native = new FakeStickNative();
        native.Plug(1, "R", VkbR);
        native.Plug(2, "L", VkbL);
        native.Plug(3, "R", VkbR);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        var state = new StickDeviceState(() => 0, () => roster);

        Assert.Equal(new[] { VkbR.Device, VkbL.Device }, state.Devices());
    }

    [Fact]
    public void SeatOneAnswersForSticksBesideItsPadPlaceholder()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var seat = new SeatDeviceState(DefaultBindings.AnyPad, () => null, sticks: new StickDeviceState(() => 0, () => roster));
        native.Press(2, 1);
        native.SetAxis(1, 3, -32768);
        native.SetHat(2, 0, (byte)HatDirection.Right);

        Assert.True(seat.IsButtonDown(VkbR.Device, 1));
        Assert.False(seat.IsButtonDown(VkbL.Device, 1));
        Assert.Equal(-1f, seat.AxisValue(VkbL.Device, 3));
        Assert.Equal(HatDirection.Right, seat.HatState(VkbR.Device, 0));
    }

    [Fact]
    public void APadMutedSeatReadsNoStickEither()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var keysOnly = new SeatDeviceState(DefaultBindings.AnyPad, () => null, readsPads: false, sticks: new StickDeviceState(() => 0, () => roster));
        native.Set(1, axis: 0, raw: 32767, button: 1, hat: 1);

        Assert.False(keysOnly.IsButtonDown(VkbL.Device, 1));
        Assert.Equal(0f, keysOnly.AxisValue(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, keysOnly.HatState(VkbL.Device, 0));
    }

    [Fact]
    public void ASeatWithoutSticksReadsNoStick()
    {
        var (native, roster) = Hosas();
        using var _ = roster;
        var seat = new SeatDeviceState(DefaultBindings.AnyPad, () => null);
        native.Set(1, axis: 0, raw: 32767, button: 1, hat: 1);

        Assert.False(seat.IsButtonDown(VkbL.Device, 1));
        Assert.Equal(0f, seat.AxisValue(VkbL.Device, 0));
        Assert.Equal(HatDirection.None, seat.HatState(VkbL.Device, 0));
    }

    // L is instance 1 and R instance 2, both opened and readable.
    private static (FakeStickNative Native, StickRoster Roster) Hosas()
    {
        var native = new FakeStickNative();
        native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
        native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
        var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        return (native, roster);
    }
}
