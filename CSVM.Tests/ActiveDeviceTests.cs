using System.Collections.Generic;
using CSVM.Bindings;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The seat's device memory and the binding a prompt reads off it: which side produced the last real
/// input, what a stick short of a deliberate push counts as, and which of an action's bindings the
/// active side names. Driven over the shipped flight keymap and two fake device states, one per
/// side, which is the split a live seat gets from its own pad-muted reader.
/// </summary>
public class ActiveDeviceTests
{
    private static readonly DeviceId Pad = DeviceId.Joypad("test-pad");

    [Fact]
    public void APadPressTakesThePromptOffTheKeyboardAndAKeyPressTakesItBack()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        Assert.Equal(DeviceSide.Keyboard, device.Side);

        var pad = new Fake();
        pad.Buttons.Add((Pad, (int)JoyButton.LeftStick));
        Assert.True(Observe(device, map, new Fake(), pad));
        Assert.Equal(DeviceSide.Pad, device.Side);

        var keys = new Fake();
        keys.Keys.Add((int)Key.A);
        Assert.True(Observe(device, map, keys, new Fake()));
        Assert.Equal(DeviceSide.Keyboard, device.Side);
    }

    [Fact]
    public void AStickShortOfADeliberatePushIsDriftAndMovesNothing()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        var drifting = new Fake();
        drifting.Axes[(Pad, (int)JoyAxis.LeftY)] = ActiveDevice.PressTravel - 0.1f;

        Assert.False(Observe(device, map, new Fake(), drifting));
        Assert.Equal(DeviceSide.Keyboard, device.Side);

        drifting.Axes[(Pad, (int)JoyAxis.LeftY)] = 0.9f;
        Assert.True(Observe(device, map, new Fake(), drifting));
        Assert.Equal(DeviceSide.Pad, device.Side);
    }

    [Fact]
    public void AHeldStickDoesNotTakeThePromptBackOffAKeyPressedAfterIt()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        var stick = new Fake();
        stick.Axes[(Pad, (int)JoyAxis.LeftY)] = 0.9f;
        Observe(device, map, new Fake(), stick);

        var keys = new Fake();
        keys.Keys.Add((int)Key.Down);
        Assert.True(Observe(device, map, keys, stick));
        Assert.Equal(DeviceSide.Keyboard, device.Side);

        // Both still held, tick after tick: the line must not flicker between the two.
        Assert.False(Observe(device, map, keys, stick));
        Assert.False(Observe(device, map, keys, stick));
        Assert.Equal(DeviceSide.Keyboard, device.Side);

        // The key released with the stick still deflected hands the prompt back.
        Assert.True(Observe(device, map, new Fake(), stick));
        Assert.Equal(DeviceSide.Pad, device.Side);
    }

    [Fact]
    public void AQuietTickLeavesTheSideWhereItWas()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        var pad = new Fake();
        pad.Buttons.Add((Pad, (int)JoyButton.LeftStick));
        Observe(device, map, new Fake(), pad);

        for (int i = 0; i < 3; i++)
            Assert.False(Observe(device, map, new Fake(), new Fake()));
        Assert.Equal(DeviceSide.Pad, device.Side);
    }

    [Fact]
    public void ASeatThatReadsNoKeyboardIsPinnedToThePadWhateverTheKeysSay()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        var keys = new Fake();
        keys.Keys.Add((int)Key.A);

        Assert.True(device.Observe(map.Resolve(keys), map.Resolve(new Fake()), readsKeyboard: false));
        Assert.Equal(DeviceSide.Pad, device.Side);
        Assert.False(device.Observe(map.Resolve(keys), map.Resolve(new Fake()), readsKeyboard: false));
        Assert.Equal(DeviceSide.Pad, device.Side);
    }

    [Fact]
    public void APromptNamesTheActiveSidesBindingAndFallsBackToTheOthers()
    {
        var keyAndPad = new[] { KeyBinding(Key.A), PadBinding(JoyButton.LeftStick) };
        Assert.Equal(KeyBinding(Key.A),
            ActiveDevice.PromptBinding(keyAndPad, DeviceSide.Keyboard, readsKeyboard: true));
        Assert.Equal(PadBinding(JoyButton.LeftStick),
            ActiveDevice.PromptBinding(keyAndPad, DeviceSide.Pad, readsKeyboard: true));

        var keyOnly = new[] { KeyBinding(Key.A) };
        Assert.Equal(KeyBinding(Key.A),
            ActiveDevice.PromptBinding(keyOnly, DeviceSide.Pad, readsKeyboard: true));

        var padOnly = new[] { PadBinding(JoyButton.LeftStick) };
        Assert.Equal(PadBinding(JoyButton.LeftStick),
            ActiveDevice.PromptBinding(padOnly, DeviceSide.Keyboard, readsKeyboard: true));
    }

    [Fact]
    public void APadOnlySeatIsNeverNamedAKeyAndAnUnboundActionNamesNothing()
    {
        var keyAndPad = new[] { KeyBinding(Key.A), PadBinding(JoyButton.LeftStick) };
        Assert.Equal(PadBinding(JoyButton.LeftStick),
            ActiveDevice.PromptBinding(keyAndPad, DeviceSide.Keyboard, readsKeyboard: false));

        var keyOnly = new[] { KeyBinding(Key.A) };
        Assert.Null(ActiveDevice.PromptBinding(keyOnly, DeviceSide.Pad, readsKeyboard: false));
        Assert.Null(ActiveDevice.PromptBinding(System.Array.Empty<Binding>(), DeviceSide.Keyboard, true));
    }

    [Fact]
    public void AKeyBeatsAMouseButtonOnTheKeyboardSideAndAMouseButtonBeatsThePad()
    {
        var all = new[] { MouseBinding(MouseButton.Middle), KeyBinding(Key.A), PadBinding(JoyButton.LeftStick) };
        Assert.Equal(KeyBinding(Key.A),
            ActiveDevice.PromptBinding(all, DeviceSide.Keyboard, readsKeyboard: true));

        var mouseAndPad = new[] { MouseBinding(MouseButton.Middle), PadBinding(JoyButton.LeftStick) };
        Assert.Equal(MouseBinding(MouseButton.Middle),
            ActiveDevice.PromptBinding(mouseAndPad, DeviceSide.Keyboard, readsKeyboard: true));
    }

    [Fact]
    public void AStickPressPutsThePadSideOnTheStickAndAGamepadPressTakesItOff()
    {
        var device = new ActiveDevice();
        var map = FlightMap();
        var one = new Fake();
        one.Buttons.Add((Pad, (int)JoyButton.LeftStick));
        var two = new Fake();
        two.Buttons.Add((Pad, (int)JoyButton.LeftStick));
        two.Buttons.Add((Pad, (int)JoyButton.A));

        // The sticks-alone half holding what the pad half holds: the press came from a stick.
        Assert.True(device.Observe(map.Resolve(new Fake()), map.Resolve(one), true, map.Resolve(one)));
        Assert.Equal(DeviceSide.Pad, device.Side);
        Assert.True(device.OnStick);

        // A second action rising on the pad half alone is a gamepad press, and recomposes the line.
        Assert.True(device.Observe(map.Resolve(new Fake()), map.Resolve(two), true, map.Resolve(one)));
        Assert.False(device.OnStick);
        Assert.False(device.Observe(map.Resolve(new Fake()), map.Resolve(two), true, map.Resolve(one)));
    }

    [Fact]
    public void OnTheStickAPromptNamesTheSticksBindingOverTheGamepadsListedFirst()
    {
        var stick = DeviceId.Joypad("test-stick");
        var stickButton = new Binding(stick, BindingControl.Button(27));
        var bindings = new[] { KeyBinding(Key.A), PadBinding(JoyButton.LeftStick), stickButton };
        var registered = BindingLabels.StickName;
        BindingLabels.StickName = d => d == stick ? "R" : null;
        try
        {
            Assert.Equal(stickButton, ActiveDevice.PromptBinding(bindings, DeviceSide.Pad, true, onStick: true));
            Assert.Equal(PadBinding(JoyButton.LeftStick), ActiveDevice.PromptBinding(bindings, DeviceSide.Pad, true));
            Assert.Equal(stickButton, ActiveDevice.PromptBinding(new[] { KeyBinding(Key.A), stickButton }, DeviceSide.Pad, true));
        }
        finally
        {
            BindingLabels.StickName = registered;
        }
    }

    private static ActionMap FlightMap() => DefaultBindings.MapFor(InputContext.Flight, Pad);

    private static bool Observe(ActiveDevice device, ActionMap map, Fake keys, Fake pad) =>
        device.Observe(map.Resolve(keys), map.Resolve(pad), readsKeyboard: true);

    private static Binding KeyBinding(Key key) => new(DeviceId.Keyboard, BindingControl.Key((int)key));

    private static Binding PadBinding(JoyButton button) => new(Pad, BindingControl.Button((int)button));

    private static Binding MouseBinding(MouseButton button) =>
        new(DeviceId.Mouse, BindingControl.Mouse((int)button));

    // One side's hardware, answering for its own device alone, the same shape BindingModelTests
    // resolves against.
    private sealed class Fake : IDeviceState
    {
        public HashSet<int> Keys { get; } = new();

        public HashSet<(DeviceId Device, int Index)> Buttons { get; } = new();

        public HashSet<int> MouseButtons { get; } = new();

        public Dictionary<(DeviceId Device, int Index), float> Axes { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) => Buttons.Contains((device, button));

        public bool IsMouseButtonDown(DeviceId device, int button) =>
            device == DeviceId.Mouse && MouseButtons.Contains(button);

        public float AxisValue(DeviceId device, int axis) =>
            Axes.TryGetValue((device, axis), out float value) ? value : 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
