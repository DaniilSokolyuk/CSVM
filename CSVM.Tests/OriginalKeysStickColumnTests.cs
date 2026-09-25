using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The KEYS AND BUTTONS page's Stick column over a whole shell. It lists stick bindings alone and
/// its capture hears sticks and nothing else. What it hears replaces that stick model's binding on
/// the row, while the other stick's, the keys' and the pad's stay. Control A and Control B keep
/// their own slots when a stick binding stands among them. Runs the seat's reader over
/// <see cref="FakeStickNative"/> with the user's VKB L (instance 1) and R (2).
/// </summary>
public class OriginalKeysStickColumnTests
{
    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    [Fact]
    public void TheStickCellHearsAStickAndNotAKeyOrThePad()
    {
        using var rig = new Rig();
        var action = rig.OpenRow(0);
        var before = rig.Controls.Bindings(InputContext.Flight, action).ToList();

        rig.Click(OriginalOptionsScreen.KeysStickCellKey(0));
        Assert.True(rig.Controls.Capturing);
        rig.Devices.Keys.Add((int)Key.M);
        rig.Devices.PadButtons.Add((int)JoyButton.A);
        rig.Frame();
        Assert.True(rig.Controls.Capturing, "a key and a pad button are not a stick's answer");
        Assert.Equal(before, rig.Controls.Bindings(InputContext.Flight, action));

        rig.Native.Press(2, 5);
        rig.Frame();
        var r5 = new Binding(VkbR.Device, BindingControl.Button(5));
        Assert.False(rig.Controls.Capturing);
        Assert.Equal(before.Append(r5), rig.Controls.Bindings(InputContext.Flight, action));
        var text = rig.Shell.Options.KeysCellText(0);
        Assert.Equal(BindingLabels.Describe(r5), text.Stick);
        Assert.Equal(BindingLabels.Describe(before[0]), text.A);
        Assert.DoesNotContain(BindingLabels.Describe(r5), text.B);
    }

    [Fact]
    public void AStickCaptureReplacesThatSticksBindingAndLeavesTheOtherStickKeysAndPad()
    {
        using var rig = new Rig();
        var action = rig.OpenRow(0);
        var r1 = new Binding(VkbR.Device, BindingControl.Button(1));
        var l1 = new Binding(VkbL.Device, BindingControl.Button(1));
        rig.Add(action, r1);
        rig.Add(action, l1);
        var others = rig.Controls.Bindings(InputContext.Flight, action).Where(b => !KeysStickColumn.IsStick(b)).ToList();
        Assert.NotEmpty(others);

        rig.Click(OriginalOptionsScreen.KeysStickCellKey(0));
        rig.Native.Press(2, 7);
        rig.Frame();

        var after = rig.Controls.Bindings(InputContext.Flight, action);
        Assert.Contains(new Binding(VkbR.Device, BindingControl.Button(7)), after);
        Assert.DoesNotContain(r1, after);
        Assert.Contains(l1, after);
        Assert.Equal(others, after.Where(b => !KeysStickColumn.IsStick(b)));
        Assert.EndsWith(" +1", rig.Shell.Options.KeysCellText(0).Stick);
    }

    /// <summary>A full axis counts once for its pair. R's new axis replaces R's old one on both
    /// rows, and L's axis on the other pair row stays.</summary>
    [Fact]
    public void AStickAxisReplacesThatSticksFullAxisOnBothPairRows()
    {
        using var rig = new Rig();
        var movement = OriginalOptionsScreen.ControlTabs[0].Rows;
        int row = Enumerable.Range(0, movement.Count).Single(i => movement[i].Action == InputAction.PitchUp);
        rig.OpenRow(row);
        var oldR = new Binding(VkbR.Device, BindingControl.FullAxis(1, false, StickCapture.FlightDeadzone));
        var l = new Binding(VkbL.Device, BindingControl.FullAxis(1, false, StickCapture.FlightDeadzone));
        rig.Add(InputAction.PitchUp, oldR);
        rig.Add(InputAction.PitchDown, l);

        rig.Click(OriginalOptionsScreen.KeysStickCellKey(row));
        rig.Native.SetAxis(2, 3, 32767);
        rig.Frame();

        var newR = new Binding(VkbR.Device, BindingControl.FullAxis(3, false, StickCapture.FlightDeadzone));
        foreach (var action in new[] { InputAction.PitchUp, InputAction.PitchDown })
        {
            var bindings = rig.Controls.Bindings(InputContext.Flight, action);
            Assert.Contains(newR, bindings);
            Assert.DoesNotContain(oldR, bindings);
            Assert.Contains(l, bindings);
        }
    }

    /// <summary>A stick bound ahead of the keys shifts nothing: Control A still replaces the first
    /// key or pad control and Control B the second.</summary>
    [Fact]
    public void ControlAAndBSlotsSkipAStickBoundAheadOfThem()
    {
        using var rig = new Rig();
        var action = rig.OpenRow(0);
        var shipped = rig.Controls.Bindings(InputContext.Flight, action).ToList();
        Assert.NotEmpty(shipped);
        rig.Controls.Context = InputContext.Flight;
        rig.Controls.Focus(rig.Controls.Actions.ToList().IndexOf(action));
        while (rig.Controls.FocusedBindings.Count > 0)
        {
            rig.Controls.UnbindSlot();
        }

        var r1 = new Binding(VkbR.Device, BindingControl.Button(1));
        foreach (var binding in shipped.Prepend(r1))
        {
            rig.Add(action, binding);
        }

        rig.Click(OriginalOptionsScreen.KeysCellKey(0, second: false));
        Assert.Equal(1, rig.Controls.Slot);
        rig.Controls.CancelCapture();
        rig.Click(OriginalOptionsScreen.KeysCellKey(0, second: true));
        Assert.Equal(2, rig.Controls.Slot);
        rig.Controls.CancelCapture();
        var text = rig.Shell.Options.KeysCellText(0);
        Assert.Equal(BindingLabels.Describe(shipped[0]), text.A);
        Assert.Equal(BindingLabels.Describe(r1), text.Stick);
    }

    [Fact]
    public void ASidewaysStepCrossesControlAThenStickThenControlB()
    {
        using var rig = new Rig();
        rig.OpenRow(0);
        rig.Shell.Step(new MenuCommands { MoveX = 1 });
        Assert.Equal(OriginalOptionsScreen.KeysCellKey(0, second: false), rig.Shell.FocusedKey);
        rig.Shell.Step(new MenuCommands { MoveX = 1 });
        Assert.Equal(OriginalOptionsScreen.KeysStickCellKey(0), rig.Shell.FocusedKey);
        rig.Shell.Step(new MenuCommands { MoveX = 1 });
        Assert.Equal(OriginalOptionsScreen.KeysCellKey(0, second: true), rig.Shell.FocusedKey);
    }

    [Fact]
    public void ThePageDrawsTheStickHeadBetweenTheTwoAuthoredControlHeads()
    {
        using var rig = new Rig();
        rig.OpenRow(0);
        var lines = rig.Shell.Compose().Lines;
        var a = lines.Single(l => l.Text == "Control A");
        var stick = lines.Single(l => l.Text == "Stick");
        var b = lines.Single(l => l.Text == "Control B");
        Assert.True(a.X < stick.X && stick.X < b.X, $"A {a.X}, Stick {stick.X}, B {b.X}");
        Assert.True(a.X + a.Width <= stick.X && stick.X + stick.Width <= b.X,
            $"the three columns do not overlap (A ends {a.X + a.Width}, Stick {stick.X}..{stick.X + stick.Width}, B {b.X})");
    }

    [Fact]
    public void TheThrottleTabEndsWithTheLeverRowWhichCapturesAStickLever()
    {
        using var rig = new Rig();
        var rows = OriginalOptionsScreen.ControlTabs[1].Rows;
        Assert.Equal(InputAction.ThrottleLever, rows[^1].Action);
        int lever = rows.Count - 1;
        rig.Native.SetAxis(2, 2, -18677);
        rig.Shell.Options.OpenKeys();
        rig.Click(OriginalOptionsScreen.KeysTabKey(1));
        rig.Shell.Step(new MenuCommands { MoveX = 1 });
        for (int i = 0; i < rows.Count + 4 && rig.Shell.FocusedKey != OriginalOptionsScreen.KeysCellKey(lever, second: false); i++)
        {
            rig.Shell.Step(new MenuCommands { MoveY = 1 });
        }

        rig.Shell.Step(new MenuCommands { MoveX = 1 });
        Assert.Equal(OriginalOptionsScreen.KeysStickCellKey(lever), rig.Shell.FocusedKey);
        rig.Shell.Step(new MenuCommands { Accept = true });
        Assert.True(rig.Controls.Capturing);
        rig.Frame();
        rig.Frame();
        rig.Native.SetAxis(2, 2, 32767);
        rig.Frame();

        Assert.Equal(
            new[] { new Binding(VkbR.Device, BindingControl.FullAxis(2, false, StickCapture.FlightDeadzone)) },
            rig.Controls.Bindings(InputContext.Flight, InputAction.ThrottleLever));
    }

    // A shell on the KEYS page over one seat. Its flight reader holds scripted keys and pad buttons
    // beside a stick roster of L and R.
    private sealed class Rig : IDisposable
    {
        private readonly StickRoster _roster;

        public Rig()
        {
            Native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
            Native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
            _roster = new StickRoster(Native, Array.Empty<StickModel>, () => false);
            _roster.Update();
            var pad = MenuControlsSeats.PadOf(InputContext.Flight);
            Devices = new KeysPadAndSticks(pad, new SeatDeviceState(
                pad, () => null, sticks: new StickDeviceState(() => 0, () => _roster)));
            Controls = new ControlsFeature((_, _) => { });
            Controls.AddSeat(1, OriginalControlsTests.Profile(), new CaptureDevices(Devices), readsKeyboard: true);
            var setup = new PlayerSetupFeature();
            setup.SetRoster(OriginalPresentation.Roster(Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
            setup.Join(new ScriptedMenuSeat());
            Shell = new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), setup, Measure, controls: Controls);
        }

        public FakeStickNative Native { get; } = new();

        public KeysPadAndSticks Devices { get; }

        public ControlsFeature Controls { get; }

        public OriginalShell Shell { get; }

        // Opens the page on its Movement tab and names the action on that row.
        public InputAction OpenRow(int row)
        {
            Shell.Options.OpenKeys();
            return OriginalOptionsScreen.ControlTabs[0].Rows[row].Action;
        }

        // Adds one control to an action in the staged keymap, the way a capture on an empty slot does.
        public void Add(InputAction action, Binding binding)
        {
            Controls.Context = InputContext.Flight;
            Controls.Focus(Controls.Actions.ToList().IndexOf(action));
            Controls.MoveSlot(Controls.FocusedBindings.Count);
            Controls.Offer(binding);
            Controls.ConfirmSteal();
        }

        // One click as the shell reads it: the press arms the row and the release on it fires.
        public void Click(string key)
        {
            var row = Shell.Rows.Single(r => r.Key == key);
            float x = row.X + (row.Width / 2f);
            float y = row.Y + (row.Height / 2f);
            Shell.Step(new MenuCommands { Pointer = new MenuPointer(x, y, true, true) });
            Shell.Step(new MenuCommands { Pointer = new MenuPointer(x, y, false, false) });
        }

        public void Frame() => Shell.Step(MenuCommands.None);

        public void Dispose() => _roster.Dispose();

        private static (int Width, int Height)? Measure(string art) => art switch
        {
            "PP_B_Large.png" => (120, 148),
            "PP_B_KbBar.png" => (16, 11),
            "PP_B_KbUp.png" or "PP_B_KbDown.png" => (16, 44),
            _ when art.StartsWith("PM_B_", StringComparison.Ordinal) || art.StartsWith("PP_B_", StringComparison.Ordinal)
                => (240, 200),
            _ => null,
        };
    }

    private sealed class CaptureDevices : ICaptureDevices
    {
        private readonly IDeviceState _flight;

        public CaptureDevices(IDeviceState flight) => _flight = flight;

        public DeviceId PadOf(InputContext context) => MenuControlsSeats.PadOf(context);

        public IDeviceState For(InputContext context) => _flight;
    }

    // Scripted keys and pad buttons, with every stick identity answered by the seat's own reader.
    private sealed class KeysPadAndSticks : IDeviceState, IStickDevices
    {
        private readonly DeviceId _pad;
        private readonly SeatDeviceState _sticks;

        public KeysPadAndSticks(DeviceId pad, SeatDeviceState sticks)
        {
            _pad = pad;
            _sticks = sticks;
        }

        public HashSet<int> Keys { get; } = new();

        public HashSet<int> PadButtons { get; } = new();

        public bool ReadsBlocked => _sticks.ReadsBlocked;

        public bool IsKeyDown(DeviceId device, int keyCode) => device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public bool IsButtonDown(DeviceId device, int button) =>
            device == _pad ? PadButtons.Contains(button) : _sticks.IsButtonDown(device, button);

        public float AxisValue(DeviceId device, int axis) => device == _pad ? 0f : _sticks.AxisValue(device, axis);

        public HatDirection HatState(DeviceId device, int hat) => device == _pad ? HatDirection.None : _sticks.HatState(device, hat);

        public IReadOnlyList<DeviceId> Devices() => _sticks.Devices();
    }
}
