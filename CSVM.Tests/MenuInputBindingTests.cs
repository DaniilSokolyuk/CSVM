using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.UI.Screens;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Per-site mapping evidence for the menu migration: every control the old <c>MenuInput</c> polled
/// resolves the action that replaced it, and resolves no other menu action. The scripted
/// determinism run cannot show this, because it holds nothing down and every read is false there.
/// The fake state below is the whole hardware side, as in <see cref="BindingModelTests"/>.
/// </summary>
public class MenuInputBindingTests
{
    // The old RawDir/RawDirX read the stick past this; the shipped menu bindings carry it as their
    // deadzone, so a reading either side of it is what proves the number survived the migration.
    private const float PastDeadzone = 0.6f;

    public static TheoryData<Key, InputAction> KeySites => new()
    {
        { Key.Up, InputAction.MenuUp },
        { Key.W, InputAction.MenuUp },
        { Key.Down, InputAction.MenuDown },
        { Key.S, InputAction.MenuDown },
        { Key.Left, InputAction.MenuLeft },
        { Key.A, InputAction.MenuLeft },
        { Key.Right, InputAction.MenuRight },
        { Key.D, InputAction.MenuRight },
        { Key.Enter, InputAction.MenuAccept },
        { Key.KpEnter, InputAction.MenuAccept },
        { Key.Space, InputAction.MenuAccept },
        { Key.Escape, InputAction.MenuBack },
        { Key.L, InputAction.MenuLoadout },
        { Key.P, InputAction.MenuPresets },
    };

    public static TheoryData<JoyButton, InputAction> ButtonSites => new()
    {
        { JoyButton.DpadUp, InputAction.MenuUp },
        { JoyButton.DpadDown, InputAction.MenuDown },
        { JoyButton.DpadLeft, InputAction.MenuLeft },
        { JoyButton.DpadRight, InputAction.MenuRight },
        { JoyButton.A, InputAction.MenuAccept },
        { JoyButton.B, InputAction.MenuBack },
        { JoyButton.Start, InputAction.MenuStart },
        { JoyButton.Y, InputAction.MenuLoadout },
        { JoyButton.X, InputAction.MenuPresets },
    };

    public static TheoryData<JoyAxis, float, InputAction> AxisSites => new()
    {
        { JoyAxis.LeftY, -PastDeadzone, InputAction.MenuUp },
        { JoyAxis.LeftY, PastDeadzone, InputAction.MenuDown },
        { JoyAxis.LeftX, -PastDeadzone, InputAction.MenuLeft },
        { JoyAxis.LeftX, PastDeadzone, InputAction.MenuRight },
    };

    // The keys a screen taking a profile name types with, which are exactly the ones the old
    // AliasDown suppressed there.
    public static TheoryData<Key> TypeableSites => new() { Key.W, Key.S, Key.A, Key.D, Key.Space, Key.L, Key.P };

    // The keys that stayed live under text entry, because none of them types a character.
    public static TheoryData<Key> DedicatedSites => new()
    {
        Key.Up, Key.Down, Key.Left, Key.Right, Key.Enter, Key.KpEnter, Key.Escape,
    };

    [Theory]
    [MemberData(nameof(KeySites))]
    public void EachKeyTheOldCodePolledDrivesItsMenuActionAndNoOther(Key key, InputAction action)
    {
        var state = new FakeDevices();
        state.Keys.Add((int)key);

        AssertOnly(action, Poll(state, keyboard: true));
    }

    [Theory]
    [MemberData(nameof(ButtonSites))]
    public void EachPadButtonTheOldCodePolledDrivesItsMenuActionAndNoOther(JoyButton button, InputAction action)
    {
        var state = new FakeDevices();
        state.Buttons.Add((MenuInput.SeatPads, (int)button));

        AssertOnly(action, Poll(state, keyboard: true));
    }

    [Theory]
    [MemberData(nameof(AxisSites))]
    public void EachStickHalfTheOldCodePolledDrivesItsMenuActionAndNoOther(JoyAxis axis, float value, InputAction action)
    {
        var state = new FakeDevices();
        state.Axes[(MenuInput.SeatPads, (int)axis)] = value;

        AssertOnly(action, Poll(state, keyboard: true));
    }

    [Fact]
    public void TheStickFiresStrictlyPastTheOldHalfDeflectionAndNotAtIt()
    {
        var state = new FakeDevices();
        foreach (float value in new[] { 0.4f, 0.5f })
        {
            state.Axes[(MenuInput.SeatPads, (int)JoyAxis.LeftY)] = value;
            Assert.False(Poll(state, keyboard: true).Held(InputAction.MenuDown));
        }

        state.Axes[(MenuInput.SeatPads, (int)JoyAxis.LeftY)] = 0.51f;
        Assert.True(Poll(state, keyboard: true).Held(InputAction.MenuDown));
    }

    [Fact]
    public void APadOnlySeatReadsItsPadAndNoneOfTheKeyboardRows()
    {
        var state = new FakeDevices();
        state.Keys.Add((int)Key.Escape);
        Assert.False(Poll(state, keyboard: false).Held(InputAction.MenuBack));

        state.Keys.Clear();
        state.Buttons.Add((MenuInput.SeatPads, (int)JoyButton.B));
        Assert.True(Poll(state, keyboard: false).Held(InputAction.MenuBack));
    }

    [Theory]
    [MemberData(nameof(TypeableSites))]
    public void ATypeableKeyIsDeadInTheTextEntryMap(Key key)
    {
        var state = new FakeDevices();
        state.Keys.Add((int)key);

        foreach (var action in DefaultBindings.ActionsIn(InputContext.Menu))
            Assert.False(PollTyping(state).Held(action));
    }

    [Theory]
    [MemberData(nameof(DedicatedSites))]
    public void ADedicatedKeyStaysLiveInTheTextEntryMap(Key key)
    {
        var state = new FakeDevices();
        state.Keys.Add((int)key);

        Assert.True(Any(PollTyping(state)));
    }

    [Fact]
    public void TheTextEntryMapLeavesThePadAlone()
    {
        var state = new FakeDevices();
        foreach (var (button, action) in new[]
        {
            (JoyButton.A, InputAction.MenuAccept),
            (JoyButton.Y, InputAction.MenuLoadout),
            (JoyButton.X, InputAction.MenuPresets),
        })
        {
            state.Buttons.Clear();
            state.Buttons.Add((MenuInput.SeatPads, (int)button));
            Assert.True(PollTyping(state).Held(action));
        }
    }

    [Fact]
    public void TheCursorAxisTakesTheNegativeEndWhenBothAreHeld()
    {
        var state = new FakeDevices();
        state.Keys.Add((int)Key.Up);
        Assert.Equal(-1, MenuInput.Dir(Poll(state, keyboard: true), InputAction.MenuUp, InputAction.MenuDown));

        state.Keys.Clear();
        state.Keys.Add((int)Key.Down);
        Assert.Equal(1, MenuInput.Dir(Poll(state, keyboard: true), InputAction.MenuUp, InputAction.MenuDown));

        state.Keys.Add((int)Key.Up);
        Assert.Equal(-1, MenuInput.Dir(Poll(state, keyboard: true), InputAction.MenuUp, InputAction.MenuDown));

        state.Keys.Clear();
        Assert.Equal(0, MenuInput.Dir(Poll(state, keyboard: true), InputAction.MenuUp, InputAction.MenuDown));
    }

    [Fact]
    public void TheJoinGestureIsTheOnlyMenuActionTheSeatCannotResolve()
    {
        var map = MenuMap();
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Menu))
        {
            if (action == InputAction.MenuJoin)
                Assert.Empty(map.Bindings(action));
            else
                Assert.NotEmpty(map.Bindings(action));
        }
    }

    private static ActionMap MenuMap() => DefaultBindings.MapFor(InputContext.Menu, MenuInput.SeatPads);

    private static PlayerActions Poll(IDeviceState state, bool keyboard)
    {
        var seat = new PlayerActions(MenuMap(), keyboard);
        seat.Poll(state);
        return seat;
    }

    private static PlayerActions PollTyping(IDeviceState state)
    {
        var seat = new PlayerActions(MenuInput.TypingMap(MenuMap()), true);
        seat.Poll(state);
        return seat;
    }

    private static bool Any(PlayerActions seat)
    {
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Menu))
        {
            if (seat.Held(action))
                return true;
        }

        return false;
    }

    private static void AssertOnly(InputAction expected, PlayerActions seat)
    {
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Menu))
            Assert.Equal(action == expected, seat.Held(action));
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
