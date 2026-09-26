using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// What the two rebinding pages need a whole shell for: the hub's CONTROLS door standing only where
/// the shared feature is behind it, an armed cell answered from the shell's own frame (the Escape
/// cancel, a bound key as the answer, and CANCEL CHANGES dropping the whole visit), and the list
/// window following the cursor a frame drives. The pages themselves are the options module's, and
/// <see cref="OriginalOptionsTests"/> drives them over it.
/// </summary>
public class OriginalControlsTests
{
    private static readonly MenuCommands None = MenuCommands.None;

    [Fact]
    public void TheControlsDoorIsLiveOnlyWhereTheSharedFeatureStandsBehindIt()
    {
        var without = Shell(controls: null, out _, out _);
        without.Open(OriginalScreen.Options);
        Assert.True(Row(without, OriginalOptionsScreen.ControlsDoorKey) is { Enabled: false });

        var with = Shell(out _, out _);
        with.Open(OriginalScreen.Options);
        Assert.True(Row(with, OriginalOptionsScreen.ControlsDoorKey) is { Enabled: true });
        Click(with, Row(with, OriginalOptionsScreen.ControlsDoorKey)!);
        Assert.Equal(OriginalScreen.ControlsPrefs, with.Screen);
    }

    [Fact]
    public void AnArmedCellTakesEscapeAsTheAbandonAndABoundKeyAsTheAnswer()
    {
        var shell = Shell(out var controls, out var devices, out var written);
        shell.Options.OpenKeys();
        var action = OriginalOptionsScreen.ControlTabs[0].Rows[0].Action;
        Click(shell, Row(shell, OriginalOptionsScreen.KeysCellKey(0, second: false))!);
        Assert.True(controls.Capturing);

        devices.Flight.Keys.Add((int)Godot.Key.Escape);
        shell.Step(None);
        Assert.False(controls.Capturing);
        Assert.Equal("Cancelled.", controls.Status);
        Assert.DoesNotContain(controls.Bindings(InputContext.Flight, action),
            b => b.Control.Kind == ControlKind.Key && b.Control.Index == (int)Godot.Key.M);

        devices.Flight.Keys.Clear();
        Click(shell, Row(shell, OriginalOptionsScreen.KeysCellKey(0, second: false))!);
        devices.Flight.Keys.Add((int)Godot.Key.M);
        shell.Step(None);
        Assert.False(controls.Capturing);
        Assert.Contains(controls.Bindings(InputContext.Flight, action),
            b => b.Control.Kind == ControlKind.Key && b.Control.Index == (int)Godot.Key.M);
        Assert.Empty(written);

        Click(shell, Row(shell, OriginalOptionsScreen.KeysAcceptKey)!);
        Assert.Equal(OriginalScreen.ControlsPrefs, shell.Screen);
        Assert.Equal(new[] { 1 }, written.ToArray());
    }

    [Fact]
    public void CancelChangesDropsTheWholeVisitAndLeavesTheLiveKeymapAlone()
    {
        var shell = Shell(out var controls, out var devices, out var written);
        shell.Options.OpenKeys();
        var action = OriginalOptionsScreen.ControlTabs[0].Rows[0].Action;
        Click(shell, Row(shell, OriginalOptionsScreen.KeysCellKey(0, second: false))!);
        devices.Flight.Keys.Add((int)Godot.Key.M);
        shell.Step(None);
        Assert.True(controls.Dirty);

        Click(shell, Row(shell, OriginalOptionsScreen.KeysCancelKey)!);
        Assert.Equal(OriginalScreen.ControlsPrefs, shell.Screen);
        Assert.False(controls.Dirty);
        Assert.DoesNotContain(controls.Bindings(InputContext.Flight, action),
            b => b.Control.Kind == ControlKind.Key && b.Control.Index == (int)Godot.Key.M);
        Assert.Empty(written);
    }

    [Fact]
    public void ATabPressStandsItsCategoryAndTheListWindowHoldsTheCursor()
    {
        var shell = Shell(out _, out _);
        shell.Options.OpenKeys();
        Assert.Equal(0, shell.Options.KeysTab);
        int last = OriginalOptionsScreen.ControlTabs.Count - 1;
        Click(shell, Row(shell, OriginalOptionsScreen.KeysTabKey(last))!);
        Assert.Equal(last, shell.Options.KeysTab);
        Assert.Equal(0, shell.Options.KeysTop);

        // The Other tab outruns the list's window, so a cell past its foot is off screen until the
        // cursor walks onto it and the window follows.
        int rows = OriginalOptionsScreen.ControlTabs[last].Rows.Count;
        Assert.True(rows > 11, $"the Other tab outruns the authored window ({rows} rows)");
        Assert.False(Row(shell, OriginalOptionsScreen.KeysCellKey(rows - 1, second: false))!.Visible);
        shell.Step(new MenuCommands { MoveX = 1 });
        for (int i = 0; i < rows + 4 && shell.FocusedKey != OriginalOptionsScreen.KeysCellKey(rows - 1, second: false); i++)
        {
            shell.Step(new MenuCommands { MoveY = 1 });
        }

        Assert.Equal(OriginalOptionsScreen.KeysCellKey(rows - 1, second: false), shell.FocusedKey);
        Assert.True(Row(shell, OriginalOptionsScreen.KeysCellKey(rows - 1, second: false))!.Visible);
        Assert.True(shell.Options.KeysTop > 0, $"the window followed the cursor down ({shell.Options.KeysTop})");
    }

    // The shipped keymaps as one seat's profile, shared with OriginalOptionsTests, whose rebinding
    // facts stage their edits in the same feature.
    internal static BindingProfile Profile()
    {
        var maps = new Dictionary<InputContext, ActionMap>();
        foreach (var context in Enum.GetValues<InputContext>())
        {
            maps[context] = DefaultBindings.MapFor(context, MenuControlsSeats.PadOf(context));
        }

        return new BindingProfile(maps, readsKeyboard: true);
    }

    private static OriginalRow? Row(OriginalShell shell, string key)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    // One click as the shell reads it: the press arms the row and the release on it fires.
    private static void Click(OriginalShell shell, OriginalRow row)
    {
        float x = row.X + (row.Width / 2f);
        float y = row.Y + (row.Height / 2f);
        shell.Step(new MenuCommands { Pointer = new MenuPointer(x, y, true, true) });
        shell.Step(new MenuCommands { Pointer = new MenuPointer(x, y, false, false) });
    }

    private static OriginalShell Shell(out ControlsFeature controls, out FakeCaptureDevices devices) =>
        Shell(out controls, out devices, out _);

    private static OriginalShell Shell(
        out ControlsFeature controls, out FakeCaptureDevices devices, out List<int> written)
    {
        var saved = new List<int>();
        written = saved;
        devices = new FakeCaptureDevices();
        controls = new ControlsFeature((player, _) => saved.Add(player));
        controls.AddSeat(1, Profile(), devices, readsKeyboard: true);
        return Shell(controls, out _, out _);
    }

    private static OriginalShell Shell(
        ControlsFeature? controls, out FreeFlightFeature free, out PlayerSetupFeature setup)
    {
        free = new FreeFlightFeature();
        setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        return new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), free, setup, Measure, controls: controls);
    }

    // The fixture's strips: the button strips 240x200 in four frames, the tab strip 120x148 in
    // four, the list's bar and arrows at the shipped sizes, the plates unmeasured.
    private static (int Width, int Height)? Measure(string art) => art switch
    {
        "PP_B_Large.png" => (120, 148),
        "PP_B_KbBar.png" => (16, 11),
        "PP_B_KbUp.png" or "PP_B_KbDown.png" => (16, 44),
        "PP_B_SliderSlot.png" or "PF_B_SliderSlot.png" => (171, 3),
        "PP_B_Slider.png" or "PF_B_Slider.png" => (43, 21),
        "PM_B_Paper.png" => (160, 112),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) || art.StartsWith("PP_B_", StringComparison.Ordinal)
            => (240, 200),
        _ => null,
    };

    /// <summary>A seat's capture readers with nothing behind them but what a test puts there, one
    /// per context on the identity that context's rows sit on.</summary>
    internal sealed class FakeCaptureDevices : ICaptureDevices
    {
        private readonly Dictionary<InputContext, FakeDevices> _states = new();

        public FakeCaptureDevices()
        {
            foreach (var context in Enum.GetValues<InputContext>())
            {
                _states[context] = new FakeDevices(MenuControlsSeats.PadOf(context));
            }
        }

        /// <summary>The flight keymap's reader, which the six named tabs capture through.</summary>
        public FakeDevices Flight => _states[InputContext.Flight];

        public DeviceId PadOf(InputContext context) => MenuControlsSeats.PadOf(context);

        public IDeviceState For(InputContext context) => _states[context];
    }

    /// <summary>One frame of scripted hardware: the keys and buttons a test says are held.</summary>
    internal sealed class FakeDevices : IDeviceState
    {
        private readonly DeviceId _pad;

        public FakeDevices(DeviceId pad) => _pad = pad;

        public HashSet<int> Keys { get; } = new();

        public HashSet<int> Buttons { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) =>
            device == _pad && Buttons.Contains(button);

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) => 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
