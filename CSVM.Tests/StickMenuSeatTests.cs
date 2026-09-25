using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The menu rows of the user's VKB R across a flight. They are bound on the menu's Controls page,
/// kept through a yaw fix accepted on the pause menu's Controls page, and read by both seats. One <see cref="ControlsFeature"/> serves both pages, as in the game. The R is
/// instance 2 of <see cref="FakeStickNative"/>, claimed by the generic default until the first save.
/// A reader is polled with the keyboard off, since a key read needs the engine.
/// </summary>
public sealed class StickMenuSeatTests : IDisposable
{
    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly StickModel VkbL = new(0x231D, 0x0201);

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();
    private readonly FakeStickNative _native = new();
    private readonly StickRoster _roster;
    private readonly StickProfileSet _set;

    public StickMenuSeatTests()
    {
        _native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
        _roster = new StickRoster(_native, Array.Empty<StickModel>, () => false);
        for (int i = 0; i <= StickRoster.SettleUpdates; i++)
        {
            _roster.Update();
        }

        _set = new StickProfileSet(
            new StickProfileStore(() => StickProfileStore.ReadDirectory(_shipped), _user),
            () => StickProfileSet.ModelsOf(_roster),
            model => StickShape.Of(_roster, model));
        _set.Reload();
    }

    public void Dispose() => _roster.Dispose();

    [Fact]
    public void ThePauseMenuSeatOfPlayerOneNavigatesOnTheStick()
    {
        var pause = MenuInput.ForSessionSeat(0, Array.Empty<int>(), () => _roster, () => _set);
        pause.Keyboard = false;
        pause.Prime();

        _native.SetHat(2, 0, (byte)HatDirection.Down);
        pause.Poll(0.016f);
        Assert.Equal(1, pause.Move);

        _native.SetHat(2, 0, 0);
        _native.Press(2, 0);
        pause.Poll(0.016f);
        Assert.True(pause.Accept);
    }

    [Fact]
    public void ThePauseMenuSeatOfPlayerTwoReadsNoStick()
    {
        var pause = MenuInput.ForSessionSeat(1, Array.Empty<int>(), () => _roster, () => _set);
        pause.Prime();

        _native.Press(2, 0);
        pause.Poll(0.016f);

        Assert.False(pause.Accept);
        Assert.DoesNotContain(pause.Map.Bindings(InputAction.MenuAccept), StickProfileResolver.IsStick);
    }

    // The user's sequence. Menu and flight rows are bound on the menu page and accepted. In flight the
    // twist is inverted on the pause page and accepted, then the menu comes back.
    [Fact]
    public void MenuRowsBoundInTheMenuSurviveAYawFixAcceptedOnThePauseMenu()
    {
        var feature = new ControlsFeature((player, keymap) => StickScreens.Save(player, keymap, _set, (_, _) => { }));

        // The menu's seat 0, loaded as player 1 the way Launcher builds it.
        var menuSeat = new MenuInput(() => _roster, () => _set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
        menuSeat.Prime();
        var menuPage = new MenuControlsSeats(feature, Saved);
        Register(menuPage, menuSeat);

        feature.Context = InputContext.Menu;
        Bind(feature, InputAction.MenuUp, Button(5));
        Bind(feature, InputAction.MenuDown, Button(7));
        Bind(feature, InputAction.MenuBack, Button(2));
        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.Nitro, Button(4));
        feature.Accept();
        AssertMenuRows();
        _native.Press(2, 7);
        menuSeat.Poll(0.016f);
        Assert.Equal(1, menuSeat.Move);
        _native.Release(2, 7);
        menuSeat.Poll(0.016f);

        // The flight's pause seat for player 1, primed and registered as PausePreferences.Open does.
        var pauseSeat = MenuInput.ForSessionSeat(0, Array.Empty<int>(), () => _roster, () => _set);
        pauseSeat.Keyboard = false;
        pauseSeat.Prime();
        var pausePage = new MenuControlsSeats(feature, Saved);
        Register(pausePage, pauseSeat);

        _native.Press(2, 5);
        pauseSeat.Poll(0.016f);
        Assert.Equal(-1, pauseSeat.Move);
        _native.Release(2, 5);

        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.YawRight, FullAxis(5, inverted: true));
        feature.Accept();

        AssertMenuRows();
        var flight = _set.ActiveFor(VkbR)!.Map(InputContext.Flight);
        Assert.Equal(Button(4), Assert.Single(flight.Bindings(InputAction.Nitro)));
        Assert.Equal(FullAxis(5, inverted: true), Assert.Single(flight.Bindings(InputAction.YawRight)));
        Assert.NotEmpty(flight.Bindings(InputAction.PitchUp));

        // Back in the menu the seat still navigates on button 5. The page is forgotten on the
        // presentation's activation, so it edits its own map again rather than the pause reader's.
        _native.Press(2, 5);
        menuSeat.Poll(0.016f);
        Assert.Equal(-1, menuSeat.Move);
        Assert.Same(pauseSeat.Map, feature.ProfileOf(1)!.Map(InputContext.Menu));
        menuPage.Forget();
        Register(menuPage, menuSeat);
        Assert.Same(menuSeat.Map, feature.ProfileOf(1)!.Map(InputContext.Menu));
        Assert.Contains(FullAxis(5, inverted: true), feature.Bindings(InputAction.YawRight));
    }

    [Fact]
    public void AMenuPageForgottenOnActivationDropsTheFlightsSecondPlayer()
    {
        var feature = new ControlsFeature((_, _) => { });
        var menuSeat = new MenuInput(() => _roster, () => _set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
        var menuPage = new MenuControlsSeats(feature, Saved);
        Register(menuPage, menuSeat);

        var pausePage = new MenuControlsSeats(feature, Saved);
        var pauseOne = MenuInput.ForSessionSeat(0, Array.Empty<int>(), () => _roster, () => _set);
        var pauseTwo = MenuInput.ForSessionSeat(1, new[] { 0 }, () => _roster, () => _set);
        pausePage.Sync(new[] { pauseOne, pauseTwo });
        Assert.Equal(new[] { 1, 2 }, feature.Players);

        menuPage.Forget();
        Register(menuPage, menuSeat);

        Assert.Equal(new[] { 1 }, feature.Players);
        Assert.Same(menuSeat.Map, feature.ProfileOf(1)!.Map(InputContext.Menu));
    }

    [Fact]
    public void ASavedGenericDefaultWritesNoEmptyName()
    {
        var keymap = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: false);
        _set.MergeInto(keymap);
        keymap.Map(InputContext.Flight).Assign(InputAction.Nitro, Button(4));

        _set.SaveFrom(keymap);

        string text = File.ReadAllText(Path.Combine(_user, "231D-0200.json"));
        Assert.DoesNotContain("\"name\"", text);
        Assert.Equal(string.Empty, _set.ActiveFor(VkbR)!.Name);
    }

    [Fact]
    public void AFlightRowAcceptedOverTheGenericDefaultKeepsItsMenuRows()
    {
        var feature = new ControlsFeature((player, keymap) => StickScreens.Save(player, keymap, _set, (_, _) => { }));
        var menuSeat = new MenuInput(() => _roster, () => _set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
        menuSeat.Prime();
        Register(new MenuControlsSeats(feature, Saved, () => _set), menuSeat);

        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.Nitro, Button(4));
        feature.Accept();

        _set.Reload();
        var saved = _set.ActiveFor(VkbR)!;
        Assert.Equal(Button(4), Assert.Single(saved.Map(InputContext.Flight).Bindings(InputAction.Nitro)));
        Assert.Equal(Button(0), Assert.Single(saved.Map(InputContext.Menu).Bindings(InputAction.MenuAccept)));
        Assert.NotEmpty(saved.Map(InputContext.Menu).Bindings(InputAction.MenuDown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFlightRowAcceptedOverAUserFileKeepsItsMenuRows(bool fromPause)
    {
        var own = _set.ActiveFor(VkbR)!.Clone();
        own.Map(InputContext.Menu).Assign(InputAction.MenuUp, Button(5));
        _set.Save(own);

        var feature = new ControlsFeature((player, keymap) => StickScreens.Save(player, keymap, _set, (_, _) => { }));
        var seat = fromPause
            ? MenuInput.ForSessionSeat(0, Array.Empty<int>(), () => _roster, () => _set)
            : new MenuInput(() => _roster, () => _set, player: 1) { Pads = Array.Empty<int>() };
        seat.Keyboard = false;
        seat.Prime();
        seat.Poll(0.016f);
        Register(new MenuControlsSeats(feature, Saved, () => _set), seat);

        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.Nitro, Button(4));
        feature.Accept();

        _set.Reload();
        var menu = _set.ActiveFor(VkbR)!.Map(InputContext.Menu);
        Assert.Contains(Button(5), menu.Bindings(InputAction.MenuUp));
        Assert.Equal(Button(0), Assert.Single(menu.Bindings(InputAction.MenuAccept)));
    }

    /// <summary>The user's pair: with L and R both unprofiled the generic default claims neither, and
    /// R's first save hands it to L. That save writes no L file. A page registered before it stages
    /// L's rows from the default before its next Accept, which then keeps L's menu rows.</summary>
    [Fact]
    public void TheDefaultThatRsFirstSaveHandsToLReachesLsFileFromAPageOpenedBefore()
    {
        _native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
        for (int i = 0; i <= StickRoster.SettleUpdates; i++)
        {
            _roster.Update();
        }

        _set.Refresh();
        Assert.Null(_set.ActiveFor(VkbL));
        Assert.Null(_set.ActiveFor(VkbR));

        var feature = new ControlsFeature((player, keymap) => StickScreens.Save(player, keymap, _set, (_, _) => { }));
        var menuSeat = new MenuInput(() => _roster, () => _set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
        menuSeat.Prime();
        var menuPage = new MenuControlsSeats(feature, Saved, () => _set);
        Register(menuPage, menuSeat);

        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.Nitro, Button(4));
        feature.Accept();
        Assert.NotNull(_set.ActiveFor(VkbL));
        Assert.False(File.Exists(Path.Combine(_user, "231D-0201.json")), "L's rows were not on the page, so R's save writes no L file");

        // The frames after: the seat follows the set and the page syncs while it is up.
        menuSeat.Poll(0.016f);
        Register(menuPage, menuSeat);
        var l3 = new Binding(VkbL.Device, BindingControl.Button(3));
        feature.Context = InputContext.Flight;
        Bind(feature, InputAction.Nitro, l3);
        feature.Accept();

        _set.Reload();
        var l = _set.ActiveFor(VkbL)!;
        Assert.Contains(l3, l.Map(InputContext.Flight).Bindings(InputAction.Nitro));
        Assert.NotEmpty(l.Map(InputContext.Flight).Bindings(InputAction.PitchUp));
        Assert.Contains(new Binding(VkbL.Device, BindingControl.Button(0)), l.Map(InputContext.Menu).Bindings(InputAction.MenuAccept));
    }

    [Fact]
    public void AStickButtonOnTheLoadoutRowClearsAControl()
    {
        var own = _set.ActiveFor(VkbR)!.Clone();
        own.Map(InputContext.Menu).Assign(InputAction.MenuLoadout, Button(9));
        _set.Save(own);
        var seat = new MenuInput(() => _roster, () => _set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
        seat.Prime();

        _native.Press(2, 9);
        seat.Poll(0.016f);

        Assert.True(seat.Unbind);
        Assert.True(seat.Loadout);
        seat.Poll(0.016f);
        Assert.False(seat.Unbind);
    }

    private static Binding Button(int index) => new(VkbR.Device, BindingControl.Button(index));

    private static Binding FullAxis(int axis, bool inverted) =>
        new(VkbR.Device, BindingControl.FullAxis(axis, inverted, StickCapture.FlightDeadzone));

    // The KEYS AND BUTTONS Stick column's rule: replace R's binding on the row, steal if asked.
    private static void Bind(ControlsFeature feature, InputAction action, Binding binding)
    {
        feature.Focus(IndexOf(feature, action));
        feature.OfferStick(binding);
        feature.ConfirmSteal();
    }

    private static int IndexOf(ControlsFeature feature, InputAction action)
    {
        for (int i = 0; i < feature.Actions.Count; i++)
        {
            if (feature.Actions[i] == action)
                return i;
        }

        throw new InvalidOperationException($"{action} is not listed");
    }

    // A page registers only a seat with something to press, and a key read needs the engine. So
    // the keyboard is on for the registration alone.
    private static void Register(MenuControlsSeats page, MenuInput seat)
    {
        seat.Keyboard = true;
        page.Sync(new[] { seat });
        seat.Keyboard = false;
    }

    private void AssertMenuRows()
    {
        var menu = _set.ActiveFor(VkbR)!.Map(InputContext.Menu);
        Assert.Equal(Button(5), Assert.Single(menu.Bindings(InputAction.MenuUp)));
        Assert.Equal(Button(7), Assert.Single(menu.Bindings(InputAction.MenuDown)));
        Assert.Equal(Button(2), Assert.Single(menu.Bindings(InputAction.MenuBack)));
        Assert.Equal(Button(0), Assert.Single(menu.Bindings(InputAction.MenuAccept)));
    }

    // LaunchBindings.Profile with the gate shut and this suite's set as seat 1's stick rows.
    private BindingProfile Saved(int player, bool readsKeyboard)
    {
        var profile = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard);
        if (player == 1)
        {
            _set.MergeInto(profile);
        }

        return profile;
    }
}
