using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The remake Controls screen with a flight stick. A whole axis is captured on a pair row, and the
/// steal prompt leaves the pair partner out. The Throttle (lever) row takes a lever. An accepted
/// save sends player 1's stick rows to the profile files and never to the keymap file. Seat 1 reads
/// the user's VKB R (instance 2) through <see cref="FakeStickNative"/>.
/// </summary>
public sealed class ControlsStickTests : IDisposable
{
    private static readonly StickModel VkbR = new(0x231D, 0x0200);

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();
    private readonly FakeStickNative _native = new();
    private readonly StickRoster _roster;
    private readonly SeatDeviceState _seat;

    public ControlsStickTests()
    {
        _native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
        _roster = new StickRoster(_native, Array.Empty<StickModel>, () => false);
        _roster.Update();
        _seat = new SeatDeviceState(DefaultBindings.AnyPad, () => null, sticks: new StickDeviceState(() => 0, () => _roster));
    }

    public void Dispose() => _roster.Dispose();

    [Fact]
    public void AStickAxisMovedOnAPairRowBindsTheWholeAxisOnBothRows()
    {
        var (feature, _) = Screen();
        feature.Focus(IndexOf(feature, InputAction.PitchDown));

        feature.BeginCapture();
        Assert.Contains("move a stick axis toward", feature.Status);
        Assert.False(feature.Poll()); // the first unblocked poll records where every axis rests
        _native.SetAxis(2, 1, -32768);
        Assert.True(feature.Poll());

        var bound = new Binding(VkbR.Device, BindingControl.FullAxis(1, false, StickCapture.FlightDeadzone));
        Assert.Null(feature.Pending);
        Assert.Contains(bound, feature.Bindings(InputAction.PitchDown));
        Assert.Contains(bound, feature.Bindings(InputAction.PitchUp));
    }

    [Fact]
    public void TheThrottleLeverRowIsListedAndTakesALever()
    {
        var (feature, _) = Screen();
        Assert.Contains(InputAction.ThrottleLever, feature.Actions);
        feature.Focus(IndexOf(feature, InputAction.ThrottleLever));

        feature.BeginCapture();
        Assert.Contains("lever", feature.Status);
        feature.Poll();
        _native.SetAxis(2, 2, 32767);
        Assert.True(feature.Poll());

        Assert.Equal(
            new Binding(VkbR.Device, BindingControl.FullAxis(2, false, StickCapture.FlightDeadzone)),
            Assert.Single(feature.Bindings(InputAction.ThrottleLever)));
    }

    [Fact]
    public void RecapturingTheAxisFromThePartnerRowAsksNothing()
    {
        var (feature, _) = Screen();
        feature.Focus(IndexOf(feature, InputAction.PitchUp));
        feature.MoveSlot(9);
        Assert.True(feature.Offer(FullAxis(1, inverted: false)));
        feature.Focus(IndexOf(feature, InputAction.PitchDown));
        feature.MoveSlot(9);

        feature.Offer(FullAxis(1, inverted: true));

        Assert.Null(feature.Pending);
        Assert.Contains(FullAxis(1, inverted: true), feature.Bindings(InputAction.PitchUp));
        Assert.DoesNotContain(FullAxis(1, inverted: false), feature.Bindings(InputAction.PitchDown));
    }

    [Fact]
    public void TheStealPromptNamesAnotherRowHoldingTheAxisButNotThePartner()
    {
        var (feature, _) = Screen();
        var half = new Binding(VkbR.Device, BindingControl.Axis(1, -1, 0.2f));
        feature.Focus(IndexOf(feature, InputAction.RollLeft));
        feature.MoveSlot(9);
        feature.Offer(half);
        feature.Focus(IndexOf(feature, InputAction.PitchDown));
        feature.MoveSlot(9);
        feature.Offer(new Binding(VkbR.Device, BindingControl.Axis(1, 1, 0.2f)));
        feature.Focus(IndexOf(feature, InputAction.PitchUp));
        feature.MoveSlot(9);

        feature.Offer(FullAxis(1, inverted: false));

        Assert.Equal(new[] { InputAction.RollLeft }, feature.Pending!.Losers);
        feature.ConfirmSteal();
        Assert.DoesNotContain(half, feature.Bindings(InputAction.RollLeft));
        Assert.Contains(FullAxis(1, inverted: false), feature.Bindings(InputAction.PitchDown));
    }

    [Fact]
    public void UnbindingAFullAxisFromEitherRowClearsBothAndSaysSo()
    {
        var (feature, _) = Screen();
        feature.Focus(IndexOf(feature, InputAction.YawLeft));
        feature.MoveSlot(9);
        feature.Offer(FullAxis(5, inverted: false));
        feature.Focus(IndexOf(feature, InputAction.YawRight));
        feature.MoveSlot(IndexOfBinding(feature.FocusedBindings, FullAxis(5, inverted: false)));

        feature.UnbindSlot();

        Assert.DoesNotContain(FullAxis(5, inverted: false), feature.Bindings(InputAction.YawLeft));
        Assert.DoesNotContain(FullAxis(5, inverted: false), feature.Bindings(InputAction.YawRight));
        Assert.Contains(BindingLabels.Name(InputAction.YawLeft), feature.Status);
        Assert.Contains(BindingLabels.Name(InputAction.YawRight), feature.Status);
    }

    [Fact]
    public void AcceptingPlayerOneSendsStickRowsToTheProfileAndKeepsThemOutOfTheKeymapFile()
    {
        var set = Profiles();
        var written = new Dictionary<int, BindingProfile>();
        var (feature, live) = Screen((player, keymap) => StickScreens.Save(player, keymap, set, (who, file) => written[who] = file));
        feature.Focus(IndexOf(feature, InputAction.PitchUp));
        feature.MoveSlot(9);
        feature.Offer(FullAxis(1, inverted: false));

        feature.Accept();

        Assert.Contains(FullAxis(1, inverted: false), live.Map(InputContext.Flight).Bindings(InputAction.PitchDown));
        Assert.DoesNotContain("stick:", BindingStore.Serialize(1, written[1]));
        Assert.True(File.Exists(Path.Combine(_user, "231D-0200.json")));
        Assert.Contains(FullAxis(1, inverted: false), set.ActiveFor(VkbR)!.Map(InputContext.Flight).Bindings(InputAction.PitchUp));
    }

    [Fact]
    public void AnotherPlayersKeymapIsWrittenAsItIsAndNoProfileIsTouched()
    {
        var set = Profiles();
        var keymap = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: false);
        BindingProfile? written = null;

        StickScreens.Save(2, keymap, set, (_, file) => written = file);

        Assert.Same(keymap, written);
        Assert.Empty(Directory.GetFiles(_user));
    }

    [Fact]
    public void PlayerOnesKeymapFileCarriesNoStickRowsEvenWithSticksOff()
    {
        var keymap = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: true);
        keymap.Map(InputContext.Flight).Assign(InputAction.PitchUp, FullAxis(1, inverted: false));
        BindingProfile? written = null;

        StickScreens.Save(1, keymap, sticks: null, (_, file) => written = file);

        Assert.DoesNotContain("stick:", BindingStore.Serialize(1, written!));
        Assert.Contains(FullAxis(1, inverted: false), keymap.Map(InputContext.Flight).Bindings(InputAction.PitchUp));
    }

    [Fact]
    public void TheProfilesFolderRowReportsWhereItOpenedOrThatItCouldNot()
    {
        var opened = new ControlsFeature(openProfilesFolder: () => _user);
        var missing = new ControlsFeature();

        opened.OpenProfilesFolder();
        missing.OpenProfilesFolder();

        Assert.Contains(_user, opened.Status);
        Assert.Contains("could not be opened", missing.Status);
    }

    private static Binding FullAxis(int axis, bool inverted) =>
        new(VkbR.Device, BindingControl.FullAxis(axis, inverted, StickCapture.FlightDeadzone));

    private static int IndexOf(ControlsFeature feature, InputAction action)
    {
        int at = 0;
        foreach (var candidate in feature.Actions)
        {
            if (candidate == action)
                return at;
            at++;
        }

        throw new InvalidOperationException($"{action} is not listed");
    }

    private static int IndexOfBinding(IReadOnlyList<Binding> bindings, Binding binding)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (bindings[i] == binding)
                return i;
        }

        throw new InvalidOperationException("binding not on the row");
    }

    // Player 1 on the flight keymap, reading the pad placeholder and seat 1's sticks.
    private (ControlsFeature Feature, BindingProfile Live) Screen(Action<int, BindingProfile>? save = null)
    {
        var feature = new ControlsFeature(save);
        var profile = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: false);
        feature.AddSeat(1, profile, new StickSeat(_seat), readsKeyboard: false);
        feature.Context = InputContext.Flight;
        return (feature, profile);
    }

    private StickProfileSet Profiles()
    {
        var set = new StickProfileSet(
            new StickProfileStore(() => StickProfileStore.ReadDirectory(_shipped), _user), () => StickProfileSet.ModelsOf(_roster));
        set.Reload();
        return set;
    }

    // Every context reads the one seat state; the pad identity is the placeholder it answers for.
    private sealed class StickSeat : ICaptureDevices
    {
        private readonly SeatDeviceState _state;

        public StickSeat(SeatDeviceState state) => _state = state;

        public DeviceId PadOf(InputContext context) => DefaultBindings.AnyPad;

        public IDeviceState For(InputContext context) => _state;
    }
}
