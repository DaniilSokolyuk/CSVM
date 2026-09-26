using System.Collections.Generic;
using CSVM.Bindings;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>Per-site proof that every control `FlightController` used to poll directly now reaches
/// the same behaviour through the named action that replaced it. A scripted determinism run cannot
/// show this: it holds no keys, so a site wired to the wrong action reads false either way.
/// The three readers here are the three the flight node keeps, because a keyboard half and a pad
/// half of one action take different processing there and are then summed.</summary>
public class FlightBindingMappingTests
{
    private static readonly DeviceId Pad = DeviceId.Joypad("030000005e040000e002000000007801");

    /// <summary>Every migrated keyboard control, against the action the call site now names.
    /// </summary>
    public static TheoryData<Key, InputAction> KeyboardSites => new()
    {
        { Key.Space, InputAction.FireGuns },
        { Key.X, InputAction.FireRockets },
        { Key.F3, InputAction.SelectGunGroup },
        { Key.F4, InputAction.SelectGunGroupPrev },
        { Key.F5, InputAction.SelectOrdnance },
        { Key.F6, InputAction.SelectOrdnancePrev },
        { Key.N, InputAction.Nitro },
        { Key.Backspace, InputAction.Respawn },
        { Key.A, InputAction.AutoLand },
        { Key.Escape, InputAction.Pause },
        { Key.E, InputAction.TargetNextEnemy },
        { Key.W, InputAction.TargetNextAlly },
        { Key.R, InputAction.TargetNextNonAircraft },
        { Key.Q, InputAction.TargetNearest },
        { Key.T, InputAction.TargetClear },
        { Key.Key1, InputAction.ThrottleSet0 },
        { Key.Key5, InputAction.ThrottleSet4 },
        { Key.Key9, InputAction.ThrottleSet8 },
        { Key.F8, InputAction.CycleCockpitViews },
        { Key.F7, InputAction.FlybyView },
        { Key.Kp0, InputAction.LookBack },
        { Key.Kp5, InputAction.LookCenter },
        { Key.KpAdd, InputAction.ZoomIn },
        { Key.KpSubtract, InputAction.ZoomOut },
    };

    /// <summary>The keyboard rows that carry a modifier, which are a control of their own rather
    /// than the bare key plus a held qualifier.</summary>
    public static TheoryData<Key, KeyModifiers, InputAction> ModifiedKeyboardSites => new()
    {
        { Key.E, KeyModifiers.Shift, InputAction.TargetPreviousEnemy },
        { Key.E, KeyModifiers.Ctrl, InputAction.TargetNearestEnemy },
        { Key.W, KeyModifiers.Shift, InputAction.TargetPreviousAlly },
        { Key.W, KeyModifiers.Ctrl, InputAction.TargetNearestAlly },
        { Key.R, KeyModifiers.Shift, InputAction.TargetPreviousNonAircraft },
        { Key.R, KeyModifiers.Ctrl, InputAction.TargetNearestNonAircraft },
        { Key.S, KeyModifiers.Shift, InputAction.ToggleSpyglass },
    };

    /// <summary>Every migrated pad button, against the action the call site now names.</summary>
    public static TheoryData<JoyButton, InputAction> PadSites => new()
    {
        { JoyButton.B, InputAction.FireGuns },
        { JoyButton.A, InputAction.FireRockets },
        { JoyButton.DpadRight, InputAction.SelectGunGroup },
        { JoyButton.DpadLeft, InputAction.SelectOrdnance },
        { JoyButton.X, InputAction.Nitro },
        { JoyButton.Y, InputAction.Respawn },
        { JoyButton.LeftStick, InputAction.AutoLand },
        { JoyButton.Start, InputAction.Pause },
        { JoyButton.DpadUp, InputAction.TargetNextEnemy },
        { JoyButton.DpadDown, InputAction.CycleCockpitViews },
        { JoyButton.RightStick, InputAction.LookBack },
        { JoyButton.LeftShoulder, InputAction.YawLeft },
        { JoyButton.RightShoulder, InputAction.YawRight },
    };

    [Theory]
    [MemberData(nameof(KeyboardSites))]
    public void EachMigratedKey_DrivesTheActionItsCallSiteNames(Key key, InputAction action)
    {
        var (state, actions) = Seat();
        Assert.False(actions.Held(action));

        state.Keys.Add((int)key);
        actions.Poll(state);
        Assert.True(actions.Held(action));
    }

    /// <summary>A modified row fires only with its own modifier down, and the bare key on the same
    /// letter stands down while it is, so one press is one action.</summary>
    [Theory]
    [MemberData(nameof(ModifiedKeyboardSites))]
    public void EachModifiedKey_DrivesItsOwnActionAndSilencesTheBareOne(
        Key key, KeyModifiers modifiers, InputAction action)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);
        Assert.False(actions.Held(action));

        state.Keys.Add(BindingControl.KeyCodeOf(modifiers));
        actions.Poll(state);
        Assert.True(actions.Held(action));
        Assert.False(actions.Held(InputAction.TargetNextEnemy));
        Assert.False(actions.Held(InputAction.TargetNextAlly));
        Assert.False(actions.Held(InputAction.TargetNextNonAircraft));
    }

    [Theory]
    [MemberData(nameof(PadSites))]
    public void EachMigratedPadButton_DrivesTheActionItsCallSiteNames(JoyButton button, InputAction action)
    {
        var (state, actions) = Seat();
        Assert.False(actions.Held(action));

        state.Buttons.Add((Pad, (int)button));
        actions.Poll(state);
        Assert.True(actions.Held(action));
    }

    /// <summary>The three attitude axes and the throttle, keyboard half: the pairs the old
    /// `KeyAxis` calls named, in the sign the flight model reads.</summary>
    [Theory]
    [InlineData(Key.Down, 1f)]
    [InlineData(Key.Up, -1f)]
    public void ThePitchKeyPairs_ReadAsTheirOldSignedAxis(Key key, float expected)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);

        Assert.Equal(expected, actions.Axis(InputAction.PitchUp, InputAction.PitchDown));
    }

    [Theory]
    [InlineData(Key.Left, 1f)]
    [InlineData(Key.Right, -1f)]
    public void TheRollKeyPairs_ReadAsTheirOldSignedAxis(Key key, float expected)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);

        Assert.Equal(expected, actions.Axis(InputAction.RollLeft, InputAction.RollRight));
    }

    [Theory]
    [InlineData(Key.Comma, 1f)]
    [InlineData(Key.Period, -1f)]
    public void TheYawKeyPair_ReadsAsItsOldSignedAxis(Key key, float expected)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);

        Assert.Equal(expected, actions.Axis(InputAction.YawLeft, InputAction.YawRight));
    }

    [Theory]
    [InlineData(Key.Equal, 1f)]
    [InlineData(Key.Minus, -1f)]
    public void TheThrottleKeyPair_ReadsAsItsOldSignedAxis(Key key, float expected)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);

        Assert.Equal(expected, actions.Axis(InputAction.ThrottleUp, InputAction.ThrottleDown));
    }

    /// <summary>The pad half of the same four, still the raw stick travel the old `PadAxis` handed
    /// `StickCurve`: no deadzone is applied on the way, so the curve sees exactly what it saw.
    /// </summary>
    [Theory]
    [InlineData(JoyAxis.LeftY, 0.62f)]
    [InlineData(JoyAxis.LeftY, -0.62f)]
    public void TheLeftStickY_ReadsAsTheRawPitchAxis(JoyAxis axis, float travel)
    {
        var (state, actions) = PadOnlySeat();
        state.Axes[(Pad, (int)axis)] = travel;
        actions.Poll(state);

        Assert.Equal(travel, actions.Axis(InputAction.PitchUp, InputAction.PitchDown), 5);
    }

    [Theory]
    [InlineData(0.62f)]
    [InlineData(-0.62f)]
    public void TheLeftStickX_ReadsAsTheRawRollAxis(float travel)
    {
        var (state, actions) = PadOnlySeat();
        state.Axes[(Pad, (int)JoyAxis.LeftX)] = travel;
        actions.Poll(state);

        Assert.Equal(travel, actions.Axis(InputAction.RollRight, InputAction.RollLeft), 5);
    }

    [Fact]
    public void TheTriggers_ReadAsTheOldThrottleDifference()
    {
        var (state, actions) = PadOnlySeat();
        state.Axes[(Pad, (int)JoyAxis.TriggerRight)] = 0.75f;
        state.Axes[(Pad, (int)JoyAxis.TriggerLeft)] = 0.25f;
        actions.Poll(state);

        Assert.Equal(0.5f, actions.Axis(InputAction.ThrottleUp, InputAction.ThrottleDown), 5);
    }

    [Fact]
    public void TheShoulders_ReadAsTheOldPadYawDifference()
    {
        var (state, actions) = PadOnlySeat();
        state.Buttons.Add((Pad, (int)JoyButton.LeftShoulder));
        actions.Poll(state);
        Assert.Equal(1f, actions.Axis(InputAction.YawLeft, InputAction.YawRight));

        state.Buttons.Add((Pad, (int)JoyButton.RightShoulder));
        actions.Poll(state);
        Assert.Equal(0f, actions.Axis(InputAction.YawLeft, InputAction.YawRight));
    }

    /// <summary>The look stick, in the raw sign the old `PadLookInput` handed `StickCurve`.
    /// </summary>
    [Theory]
    [InlineData(0.4f)]
    [InlineData(-0.4f)]
    public void TheRightStick_ReadsAsTheRawLookAxes(float travel)
    {
        var (state, actions) = PadOnlySeat();
        state.Axes[(Pad, (int)JoyAxis.RightX)] = travel;
        state.Axes[(Pad, (int)JoyAxis.RightY)] = travel;
        actions.Poll(state);

        Assert.Equal(travel, actions.Axis(InputAction.LookAimRight, InputAction.LookAimLeft), 5);
        Assert.Equal(travel, actions.Axis(InputAction.LookAimDown, InputAction.LookAimUp), 5);
    }

    /// <summary>The snap-look cluster, which the old site composed by ORing three keys per
    /// direction. The corners drive two directions at once, which is why they are on two actions.
    /// </summary>
    [Theory]
    [InlineData(Key.Kp9, 1f, 1f)]
    [InlineData(Key.Kp6, 1f, 0f)]
    [InlineData(Key.Kp3, 1f, -1f)]
    [InlineData(Key.Kp8, 0f, 1f)]
    [InlineData(Key.Kp2, 0f, -1f)]
    [InlineData(Key.Kp7, -1f, 1f)]
    [InlineData(Key.Kp4, -1f, 0f)]
    [InlineData(Key.Kp1, -1f, -1f)]
    public void TheSnapLookCluster_ComposesTheSameDirection(Key key, float x, float y)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)key);
        actions.Poll(state);

        Assert.Equal(x, actions.Axis(InputAction.LookRight, InputAction.LookLeft));
        Assert.Equal(y, actions.Axis(InputAction.LookUp, InputAction.LookDown));
    }

    /// <summary>Every flight key pair really is the subtract-both form <see cref="ActionSnapshot.Axis"/>
    /// implements, rather than one end taking priority over the other. Holding both ends read zero
    /// before the migration and reads zero after it, pair by pair.</summary>
    [Theory]
    [InlineData(Key.Down, Key.Up, InputAction.PitchUp, InputAction.PitchDown)]
    [InlineData(Key.Left, Key.Right, InputAction.RollLeft, InputAction.RollRight)]
    [InlineData(Key.Comma, Key.Period, InputAction.YawLeft, InputAction.YawRight)]
    [InlineData(Key.Equal, Key.Minus, InputAction.ThrottleUp, InputAction.ThrottleDown)]
    [InlineData(Key.Kp8, Key.Kp2, InputAction.LookUp, InputAction.LookDown)]
    [InlineData(Key.Kp6, Key.Kp4, InputAction.LookRight, InputAction.LookLeft)]
    public void BothEndsOfAKeyPairHeld_ReadsZeroRatherThanFavouringOneEnd(
        Key positive, Key negative, InputAction up, InputAction down)
    {
        var (state, actions) = Seat();
        state.Keys.Add((int)positive);
        state.Keys.Add((int)negative);
        actions.Poll(state);

        Assert.Equal(0f, actions.Axis(up, down));
    }

    /// <summary>The seat reads the three mouse rows apart: the middle button is the free-look pan
    /// and the two beside it are the weapons, so a pan pulls neither trigger.</summary>
    [Fact]
    public void TheMouseButtons_ReadAsThePanAndTheTwoTriggers()
    {
        var (state, actions) = Seat();
        Assert.False(actions.Held(InputAction.FreeLook));

        state.MouseButtons.Add((int)MouseButton.Middle);
        actions.Poll(state);
        Assert.True(actions.Held(InputAction.FreeLook));
        Assert.False(actions.Held(InputAction.FireGuns));
        Assert.False(actions.Held(InputAction.FireRockets));

        state.MouseButtons.Clear();
        state.MouseButtons.Add((int)MouseButton.Left);
        state.MouseButtons.Add((int)MouseButton.Right);
        actions.Poll(state);
        Assert.True(actions.Held(InputAction.FireGuns));
        Assert.True(actions.Held(InputAction.FireRockets));
        Assert.False(actions.Held(InputAction.FreeLook));
    }

    /// <summary>The tap/hold splitter reads the pad half alone. Sharing the action with the `E` key
    /// is deliberate, and a full read would let a keypress feed the splitter as well as its own
    /// edge-detected slot.</summary>
    [Fact]
    public void TheTapHoldSplitter_ReadsTheDpadWithoutTheTargetingKey()
    {
        var (state, padActions) = PadOnlySeat();
        state.Keys.Add((int)Key.E);
        padActions.Poll(state);
        Assert.False(padActions.Held(InputAction.TargetNextEnemy));

        state.Buttons.Add((Pad, (int)JoyButton.DpadUp));
        padActions.Poll(state);
        Assert.True(padActions.Held(InputAction.TargetNextEnemy));
    }

    /// <summary>And the edge-detected keyboard slot reads the keyboard half alone, for the same
    /// reason from the other side.</summary>
    [Fact]
    public void TheTargetingKeySlot_ReadsTheKeyWithoutTheDpad()
    {
        var state = new FakeDevices();
        var keyActions = new PlayerActions(FlightMap(), true);
        state.Buttons.Add((Pad, (int)JoyButton.DpadUp));
        keyActions.Poll(new PadMuted(state));
        Assert.False(keyActions.Held(InputAction.TargetNextEnemy));

        state.Keys.Add((int)Key.E);
        keyActions.Poll(new PadMuted(state));
        Assert.True(keyActions.Held(InputAction.TargetNextEnemy));
    }

    /// <summary>The view-mode inputs keep four independent edge slots, so the keyboard reader must
    /// not see the pad's half of the same two actions. The chase view ships unbound, so a pad
    /// control is given to it here: what this pins is the reader split, not a default.</summary>
    [Fact]
    public void TheViewModeSlots_SplitTheKeyboardFromThePad()
    {
        var state = new FakeDevices();
        state.Buttons.Add((Pad, (int)JoyButton.DpadDown));
        state.Buttons.Add((Pad, (int)JoyButton.Back));

        var map = FlightMap();
        map.Add(InputAction.SelectChaseView,
            new Binding(Pad, BindingControl.Button((int)JoyButton.Back)));

        var keyActions = new PlayerActions(map, true);
        keyActions.Poll(new PadMuted(state));
        Assert.False(keyActions.Held(InputAction.CycleCockpitViews));
        Assert.False(keyActions.Held(InputAction.SelectChaseView));

        var padActions = new PlayerActions(map, false);
        padActions.Poll(state);
        Assert.True(padActions.Held(InputAction.CycleCockpitViews));
        Assert.True(padActions.Held(InputAction.SelectChaseView));
    }

    /// <summary>The camera reads Look Back and the zoom pair through the whole-seat reader. Giving
    /// the shipped key to another action takes the camera action off it, and the new key drives it.
    /// </summary>
    [Theory]
    [InlineData(InputAction.LookBack, Key.Kp0)]
    [InlineData(InputAction.ZoomIn, Key.KpAdd)]
    [InlineData(InputAction.ZoomOut, Key.KpSubtract)]
    public void ARebindCameraKey_FollowsTheKeymap(InputAction action, Key shipped)
    {
        var map = FlightMap();
        map.Assign(InputAction.FireRockets, new Binding(DeviceId.Keyboard, BindingControl.Key((int)shipped)));
        map.Assign(action, new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.B)));
        var state = new FakeDevices();
        var actions = new PlayerActions(map, true);

        state.Keys.Add((int)shipped);
        actions.Poll(state);
        Assert.False(actions.Held(action));
        Assert.True(actions.Held(InputAction.FireRockets));

        state.Keys.Clear();
        state.Keys.Add((int)Key.B);
        actions.Poll(state);
        Assert.True(actions.Held(action));
    }

    /// <summary>The dropped pad reads, recorded so a later change cannot restore them by accident.
    /// The nearest-target key still has no pad control of its own, and pad `X` is Nitro's
    /// alone.</summary>
    [Fact]
    public void TheDroppedPadRoutes_StayUnbound()
    {
        var map = FlightMap();
        Assert.DoesNotContain(map.Bindings(InputAction.TargetNearest),
            b => b.Device.Kind == DeviceKind.Joypad);
        Assert.Contains(map.Bindings(InputAction.Nitro),
            b => b.Control.Kind == ControlKind.Button && b.Control.Index == (int)JoyButton.X);
    }

    /// <summary>D-pad up is the pad's ONE targeting binding and drives nothing else. A Danger Zone
    /// is an objective on the same cycle, so stepping it is this action and needs no control of its
    /// own.</summary>
    [Fact]
    public void TheDpadUpButton_DrivesTheTargetCycleAlone()
    {
        var (state, actions) = Seat();
        state.Buttons.Add((Pad, (int)JoyButton.DpadUp));
        actions.Poll(state);

        Assert.True(actions.Held(InputAction.TargetNextEnemy));
        var map = FlightMap();
        var owners = new List<InputAction>();
        foreach (var action in map.BoundActions)
        {
            foreach (var b in map.Bindings(action))
            {
                if (b.Control.Kind == ControlKind.Button && b.Control.Index == (int)JoyButton.DpadUp)
                {
                    owners.Add(action);
                }
            }
        }

        Assert.Equal(new[] { InputAction.TargetNextEnemy }, owners);
    }

    /// <summary>A pad-only splitscreen seat still reads nothing off the one keyboard and the one
    /// mouse, which is the gate the old `KeyDown` carried.</summary>
    [Fact]
    public void APadOnlySeat_ReadsNeitherKeyboardNorMouse()
    {
        var (state, padActions) = PadOnlySeat();
        state.Keys.Add((int)Key.Space);
        state.Keys.Add((int)Key.Down);
        state.MouseButtons.Add((int)MouseButton.Middle);
        state.MouseButtons.Add((int)MouseButton.Left);
        padActions.Poll(state);

        Assert.False(padActions.Held(InputAction.FireGuns));
        Assert.False(padActions.Held(InputAction.FreeLook));
        Assert.Equal(0f, padActions.Axis(InputAction.PitchUp, InputAction.PitchDown));
    }

    private static ActionMap FlightMap() => DefaultBindings.MapFor(InputContext.Flight, Pad);

    private static (FakeDevices State, PlayerActions Actions) Seat()
    {
        var state = new FakeDevices();
        var actions = new PlayerActions(FlightMap(), true);
        actions.Poll(state);
        return (state, actions);
    }

    private static (FakeDevices State, PlayerActions Actions) PadOnlySeat()
    {
        var state = new FakeDevices();
        var actions = new PlayerActions(FlightMap(), false);
        actions.Poll(state);
        return (state, actions);
    }

    // The keyboard half's view of a tick: pads read as idle, everything else passes through. The
    // flight node's own device state does this; the shape is reproduced here so the split the call
    // sites depend on is asserted rather than assumed.
    private sealed class PadMuted : IDeviceState
    {
        private readonly IDeviceState _source;

        public PadMuted(IDeviceState source) => _source = source;

        public bool IsKeyDown(DeviceId device, int keyCode) => _source.IsKeyDown(device, keyCode);

        public bool IsButtonDown(DeviceId device, int button) => false;

        public bool IsMouseButtonDown(DeviceId device, int button) =>
            _source.IsMouseButtonDown(device, button);

        public float AxisValue(DeviceId device, int axis) => 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }

    private sealed class FakeDevices : IDeviceState
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
