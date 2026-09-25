using System;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The stick's cutscene skip. The generic default binds it to the trigger. Seat 1 reads a press
/// edge and the held state off the active profile's menu rows, and every other seat reads nothing.
/// A stick press ends exactly the cinemas a pad button ends. The skip shares its control rather
/// than taking it, so the trigger stays menu accept. Driven through <see cref="FakeStickNative"/>.
/// </summary>
public sealed class StickSkipTests : IDisposable
{
    private const int Instance = 1;

    private const string NoSkipRow = """
        { "model": "044F/B10A", "name": "T", "contexts": { "menu": { "MenuAccept": ["button:#0"] } } }
        """;

    private const string SkipOnButtonFive = """
        { "model": "044F/B10A", "name": "T", "contexts": { "menu": { "SkipCutscene": ["button:#5"] } } }
        """;

    private static readonly StickModel Generic = new(0x044F, 0xB10A);

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();
    private readonly FakeStickNative _native = new();
    private readonly StickRoster _roster;

    public StickSkipTests()
    {
        _native.Plug(Instance, "Generic stick", Generic);
        _roster = new StickRoster(_native, Array.Empty<StickModel>, () => false);
        _roster.Update();
        for (int i = 0; i < StickRoster.SettleUpdates; i++)
        {
            _roster.Update();
        }
    }

    public void Dispose() => _roster.Dispose();

    [Fact]
    public void TheGenericDefaultSkipsOnTheTriggerBesideMenuAccept()
    {
        var menu = GenericStickDefault.For(Generic, 8).Map(InputContext.Menu);

        var trigger = new Binding(Generic.Device, BindingControl.Button(0));
        Assert.Equal(trigger, menu.Bindings(InputAction.SkipCutscene).Single());
        Assert.Equal(trigger, menu.Bindings(InputAction.MenuAccept).Single());
        Assert.Equal(InputContext.Menu, DefaultBindings.ContextOf(InputAction.SkipCutscene));
    }

    [Fact]
    public void TheKeyboardAndPadShipNoSkipRowSinceEveryPressAlreadySkips()
    {
        Assert.Empty(DefaultBindings.MapFor(InputContext.Menu, DefaultBindings.AnyPad).Bindings(InputAction.SkipCutscene));
        Assert.Contains(InputAction.SkipCutscene, DefaultBindings.Unbound);
    }

    [Fact]
    public void SeatOneReadsAPressEdgeAndTheHeldState()
    {
        var skip = Reader(seat: StickDeviceState.OwningSeat, Profiles());
        skip.Prime();

        _native.Press(Instance, 0);
        skip.Poll();
        Assert.True(skip.Pressed);
        Assert.True(skip.Held);

        skip.Poll();
        Assert.False(skip.Pressed);
        Assert.True(skip.Held);

        _native.Release(Instance, 0);
        skip.Poll();
        Assert.False(skip.Pressed);
        Assert.False(skip.Held);
    }

    [Fact]
    public void ATriggerHeldWhenTheReaderPrimesIsNoPress()
    {
        _native.Press(Instance, 0);
        var skip = Reader(seat: StickDeviceState.OwningSeat, Profiles());
        skip.Prime();

        skip.Poll();
        Assert.False(skip.Pressed);
        Assert.True(skip.Held);

        _native.Release(Instance, 0);
        skip.Poll();
        _native.Press(Instance, 0);
        skip.Poll();
        Assert.True(skip.Pressed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AnySeatButSeatOneReadsNoSkip(int seat)
    {
        var skip = Reader(seat, Profiles());
        skip.Prime();

        _native.Press(Instance, 0);
        skip.Poll();

        Assert.False(skip.Pressed);
        Assert.False(skip.Held);
    }

    [Fact]
    public void WithSticksOffNothingIsRead()
    {
        var skip = new StickSkip(() => StickDeviceState.OwningSeat, () => null, () => null);
        skip.Prime();

        _native.Press(Instance, 0);
        skip.Poll();

        Assert.False(skip.Pressed);
        Assert.False(skip.Held);
    }

    [Fact]
    public void AProfileWithoutTheRowDoesNotSkipAndARowOnAnotherButtonDoes()
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(_user, "044F-B10A.json"), NoSkipRow);
        var set = Profiles();
        var skip = Reader(StickDeviceState.OwningSeat, set);
        skip.Prime();
        _native.Press(Instance, 0);
        skip.Poll();
        Assert.False(skip.Pressed);

        System.IO.File.WriteAllText(System.IO.Path.Combine(_user, "044F-B10A.json"), SkipOnButtonFive);
        set.Reload();
        _native.Press(Instance, 5);
        skip.Poll();
        Assert.True(skip.Pressed);
    }

    [Fact]
    public void AStickPressEndsExactlyTheCinemasAPadButtonEnds()
    {
        Assert.Equal(CinemaPress.PadButton, CinemaSkips.StickPress);
        foreach (var set in new[] { CinemaScreen.ChapterKeys, CinemaScreen.ClosingKeys, CinemaScreen.BootKeys, CinemaSkip.Escape, CinemaSkip.None })
        {
            Assert.Equal(set.Skips(CinemaPress.PadButton), set.Skips(CinemaSkips.StickPress));
        }

        Assert.True(CinemaScreen.ChapterKeys.Skips(CinemaSkips.StickPress));
        Assert.True(CinemaScreen.ClosingKeys.Skips(CinemaSkips.StickPress));
        Assert.True(CinemaScreen.BootKeys.Skips(CinemaSkips.StickPress));
    }

    [Fact]
    public void TheSkipSharesItsControlInsteadOfStealingIt()
    {
        var map = DefaultBindings.MapFor(InputContext.Menu, DefaultBindings.AnyPad);
        var a = new Binding(DefaultBindings.AnyPad, BindingControl.Button((int)JoyButton.A));

        Assert.Empty(map.Assign(InputAction.SkipCutscene, a));
        Assert.Contains(a, map.Bindings(InputAction.MenuAccept));
        Assert.Contains(a, map.Bindings(InputAction.SkipCutscene));

        Assert.Empty(map.Assign(InputAction.MenuAccept, a));
        Assert.Contains(a, map.Bindings(InputAction.SkipCutscene));

        // Any other pair of actions still steals.
        Assert.Equal(new[] { InputAction.MenuAccept }, map.Assign(InputAction.MenuBack, a));
    }

    [Fact]
    public void TheControlsScreenAsksNothingWhenTheSkipTakesTheTrigger()
    {
        var feature = new ControlsFeature();
        var profile = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: false);
        var trigger = new Binding(Generic.Device, BindingControl.Button(0));
        profile.Map(InputContext.Menu).Add(InputAction.MenuAccept, trigger);
        feature.AddSeat(1, profile, new NoDevices(), readsKeyboard: false);
        feature.Context = InputContext.Menu;
        Assert.Contains(InputAction.SkipCutscene, feature.Actions);
        feature.Focus(feature.Actions.ToList().IndexOf(InputAction.SkipCutscene));

        feature.Offer(trigger);

        Assert.Null(feature.Pending);
        Assert.Contains(trigger, feature.Bindings(InputAction.SkipCutscene));
        Assert.Contains(trigger, feature.Bindings(InputAction.MenuAccept));
        Assert.Equal("Skip Cutscene", BindingLabels.Name(InputAction.SkipCutscene));
    }

    private StickSkip Reader(int seat, StickProfileSet set) => new(() => seat, () => _roster, () => set);

    private StickProfileSet Profiles()
    {
        var set = new StickProfileSet(
            new StickProfileStore(() => StickProfileStore.ReadDirectory(_shipped), _user),
            () => StickProfileSet.ModelsOf(_roster),
            model => StickShape.Of(_roster, model));
        set.Reload();
        return set;
    }

    private sealed class NoDevices : ICaptureDevices
    {
        public DeviceId PadOf(InputContext context) => DefaultBindings.AnyPad;

        public IDeviceState For(InputContext context) => new SeatDeviceState(DefaultBindings.AnyPad, () => null);
    }
}
