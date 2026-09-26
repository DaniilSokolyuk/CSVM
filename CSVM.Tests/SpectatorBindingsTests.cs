using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Per-site evidence for the spectator camera's migration onto the binding seam: every control the
/// camera used to poll by hand resolves the action that replaced it, and with the same sign. A
/// scripted determinism run cannot show this, because it runs with no hands on the hardware and
/// every read is false in both trees, so these facts are what stands behind the claim that the
/// mapping is right. The fake state below is the whole hardware side.
/// </summary>
public class SpectatorBindingsTests
{
    // The seat-local placeholder the camera authors its pad bindings on, standing for the device
    // set that seat owns rather than one piece of hardware.
    private static readonly DeviceId Pad = SpectatorCamera.SeatPad;

    [Theory]
    [InlineData(Key.W, InputAction.CameraForward)]
    [InlineData(Key.Up, InputAction.CameraForward)]
    [InlineData(Key.S, InputAction.CameraBack)]
    [InlineData(Key.Down, InputAction.CameraBack)]
    [InlineData(Key.A, InputAction.CameraLeft)]
    [InlineData(Key.Left, InputAction.CameraLeft)]
    [InlineData(Key.D, InputAction.CameraRight)]
    [InlineData(Key.Right, InputAction.CameraRight)]
    [InlineData(Key.E, InputAction.CameraUp)]
    [InlineData(Key.U, InputAction.CameraUp)]
    [InlineData(Key.Q, InputAction.CameraDown)]
    [InlineData(Key.Z, InputAction.CameraDown)]
    [InlineData(Key.Shift, InputAction.CameraBoost)]
    [InlineData(Key.Ctrl, InputAction.CameraSlow)]
    [InlineData(Key.J, InputAction.CameraLookLeft)]
    [InlineData(Key.L, InputAction.CameraLookRight)]
    [InlineData(Key.I, InputAction.CameraLookDown)]
    [InlineData(Key.K, InputAction.CameraLookUp)]
    public void EachMoveAndLookKeyResolvesTheActionThatReplacedIt(Key key, InputAction action)
    {
        var state = new FakeDevices();
        state.Keys.Add((int)key);

        Assert.True(Camera().Resolve(state).Held(action));
    }

    [Fact]
    public void TheKeyPairsKeepTheSignTheOldAxisHelperGaveThem()
    {
        // Old: Axis(Key.S, Key.W) is +1 for W, Axis(Key.A, Key.D) +1 for D, Axis(Key.Q, Key.E) +1
        // for E, Axis(Key.J, Key.L) +1 for L, Axis(Key.K, Key.I) +1 for I.
        Assert.Equal(1f, Held(Key.W).Axis(InputAction.CameraForward, InputAction.CameraBack));
        Assert.Equal(1f, Held(Key.D).Axis(InputAction.CameraRight, InputAction.CameraLeft));
        Assert.Equal(1f, Held(Key.E).Axis(InputAction.CameraUp, InputAction.CameraDown));
        Assert.Equal(1f, Held(Key.L).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft));
        Assert.Equal(1f, Held(Key.I).Axis(InputAction.CameraLookDown, InputAction.CameraLookUp));

        // The other end of each pair is the negative half, as the old helper's subtraction was.
        Assert.Equal(-1f, Held(Key.S).Axis(InputAction.CameraForward, InputAction.CameraBack));
        Assert.Equal(-1f, Held(Key.A).Axis(InputAction.CameraRight, InputAction.CameraLeft));
        Assert.Equal(-1f, Held(Key.Q).Axis(InputAction.CameraUp, InputAction.CameraDown));
        Assert.Equal(-1f, Held(Key.J).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft));
        Assert.Equal(-1f, Held(Key.K).Axis(InputAction.CameraLookDown, InputAction.CameraLookUp));
    }

    [Fact]
    public void BothEndsOfAPairHeldReadsZero()
    {
        // Every pair this camera reads is the symmetric positive-minus-negative form the old
        // helpers had, so ActionSnapshot.Axis is the right rule for all of them. MenuInput's pairs
        // are not, which is why that migration needed its own.
        var keys = new FakeDevices();
        keys.Keys.Add((int)Key.W);
        keys.Keys.Add((int)Key.S);
        Assert.Equal(0f, Camera().Resolve(keys).Axis(InputAction.CameraForward, InputAction.CameraBack));

        var shoulders = new FakeDevices();
        shoulders.Buttons.Add((Pad, (int)JoyButton.RightShoulder));
        shoulders.Buttons.Add((Pad, (int)JoyButton.LeftShoulder));
        Assert.Equal(0f, Camera().Resolve(shoulders).Axis(InputAction.CameraUp, InputAction.CameraDown));
    }

    [Fact]
    public void TheSeatPlaceholderIsTheOnlyPadIdentityTheDefaultsCarry()
    {
        // The seat reads a device SET, which a single-DeviceId binding cannot name, so every pad
        // default is retargeted onto the seat's own placeholder and SeatDevices answers for that
        // alone. A real hardware identity reaching a binding here would pin the seat to one pad.
        var map = Camera();
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Camera))
        {
            foreach (var binding in map.Bindings(action))
            {
                Assert.True(binding.Device == DeviceId.Keyboard || binding.Device == SpectatorCamera.SeatPad);
            }
        }
    }

    [Fact]
    public void TheLeftStickCarriesTheSignTheOldPadAxisHelperReturned()
    {
        // Old: PadAxis(LeftX) is the raw value, +1 to the right; Move added it to the strafe term
        // and subtracted PadAxis(LeftY) from the forward one, so a stick pushed forward (LeftY
        // negative) flew forward.
        Assert.Equal(1f, Stick(JoyAxis.LeftX, 1f).Axis(InputAction.CameraRight, InputAction.CameraLeft));
        Assert.Equal(-1f, Stick(JoyAxis.LeftX, -1f).Axis(InputAction.CameraRight, InputAction.CameraLeft));
        Assert.Equal(1f, Stick(JoyAxis.LeftY, -1f).Axis(InputAction.CameraForward, InputAction.CameraBack));
        Assert.Equal(-1f, Stick(JoyAxis.LeftY, 1f).Axis(InputAction.CameraForward, InputAction.CameraBack));
    }

    [Fact]
    public void TheRightStickCarriesTheSignTheOldLookReadReturned()
    {
        // Old: yaw took PadAxis(RightX) and pitch PadAxis(RightY), both raw, so a stick pushed
        // right turned right and a stick pushed down looked down.
        Assert.Equal(1f, Stick(JoyAxis.RightX, 1f).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft));
        Assert.Equal(-1f, Stick(JoyAxis.RightX, -1f).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft));
        Assert.Equal(1f, Stick(JoyAxis.RightY, 1f).Axis(InputAction.CameraLookDown, InputAction.CameraLookUp));
        Assert.Equal(-1f, Stick(JoyAxis.RightY, -1f).Axis(InputAction.CameraLookDown, InputAction.CameraLookUp));
    }

    [Fact]
    public void TheShouldersAreTheVerticalPairTheOldPadButtonAxisRead()
    {
        var up = new FakeDevices();
        up.Buttons.Add((Pad, (int)JoyButton.RightShoulder));
        Assert.Equal(1f, Camera().Resolve(up).Axis(InputAction.CameraUp, InputAction.CameraDown));

        var down = new FakeDevices();
        down.Buttons.Add((Pad, (int)JoyButton.LeftShoulder));
        Assert.Equal(-1f, Camera().Resolve(down).Axis(InputAction.CameraUp, InputAction.CameraDown));
    }

    [Fact]
    public void TheTriggersCrossTheSameHalfTravelBoostGateTheOldCodeUsed()
    {
        // Old: PadTrigger(TriggerRight) > 0.5f boosted and PadTrigger(TriggerLeft) > 0.5f slowed.
        Assert.False(Stick(JoyAxis.TriggerRight, 0.5f).Held(InputAction.CameraBoost));
        Assert.True(Stick(JoyAxis.TriggerRight, 0.51f).Held(InputAction.CameraBoost));
        Assert.False(Stick(JoyAxis.TriggerLeft, 0.5f).Held(InputAction.CameraSlow));
        Assert.True(Stick(JoyAxis.TriggerLeft, 0.51f).Held(InputAction.CameraSlow));
    }

    [Fact]
    public void AStickInsideTheOldDeadzoneStillReadsNothing()
    {
        // Old: PadAxis returned 0 below 0.18 of travel. The seam keeps the same number on the
        // binding, so the gate is where it was.
        Assert.Equal(0f, Stick(JoyAxis.LeftX, 0.17f).Axis(InputAction.CameraRight, InputAction.CameraLeft));
        Assert.True(Stick(JoyAxis.LeftX, 0.19f).Held(InputAction.CameraRight));
    }

    [Fact]
    public void PastTheDeadzoneTheSeamPassesTheRawTravelTheOldHelperPassed()
    {
        // The seam briefly rescaled the travel past 0.18 onto [0, 1], which made a stick at 0.508
        // read 0.40 where the old PadAxis read 0.508. It passes the raw value again, so this camera
        // swings at the rate it swung at before the migration across the whole of the travel.
        Assert.Equal(0.508f, Stick(JoyAxis.RightX, 0.508f).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft), 5);
        Assert.Equal(1f, Stick(JoyAxis.RightX, 1f).Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft), 5);
    }

    [Fact]
    public void TheOrbitDollyReadsTheWholeTriggerTravelOnItsOwnAction()
    {
        // Old: the dolly took PadTrigger raw, from zero, while only the boost gate compared it to
        // 0.5. Borrowing CameraBoost for both left the trigger's first half inert here, so the
        // dolly has its own action at a deadzone of zero and the boost keeps the gate.
        Assert.Equal(0.2f, Stick(JoyAxis.TriggerRight, 0.2f).Value(InputAction.CameraDollyOut), 5);
        Assert.Equal(0.2f, Stick(JoyAxis.TriggerLeft, 0.2f).Value(InputAction.CameraDollyIn), 5);
        Assert.False(Stick(JoyAxis.TriggerRight, 0.2f).Held(InputAction.CameraBoost));
    }

    [Fact]
    public void TheCameraContextBindsNoMouseControl()
    {
        // The spectator's free-look, wheel and pick are InputEvents and stay that way, so nothing
        // in this context may resolve off a mouse button.
        var map = Camera();
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Camera))
        {
            foreach (var binding in map.Bindings(action))
            {
                Assert.NotEqual(ControlKind.Mouse, binding.Control.Kind);
            }
        }
    }

    // The camera map as a seat holds it, with the shipped pad placeholder resolved onto one pad.
    private static ActionMap Camera() => DefaultBindings.MapFor(InputContext.Camera, Pad);

    private static ActionSnapshot Held(Key key)
    {
        var state = new FakeDevices();
        state.Keys.Add((int)key);
        return Camera().Resolve(state);
    }

    private static ActionSnapshot Stick(JoyAxis axis, float value)
    {
        var state = new FakeDevices();
        state.Axes[(Pad, (int)axis)] = value;
        return Camera().Resolve(state);
    }

    private sealed class FakeDevices : IDeviceState
    {
        public HashSet<int> Keys { get; } = new();

        public HashSet<(DeviceId Device, int Index)> Buttons { get; } = new();

        public Dictionary<(DeviceId Device, int Index), float> Axes { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) => Buttons.Contains((device, button));

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) =>
            Axes.TryGetValue((device, axis), out float value) ? value : 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
