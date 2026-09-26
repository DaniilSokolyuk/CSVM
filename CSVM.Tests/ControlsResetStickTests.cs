using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// A Controls screen reset with flight sticks plugged in. Each stick's rows go back to its shipped
/// profile, the one unshipped stick-shaped model takes the generic default, and any other stick
/// keeps its rows. Keyboard and pad rows reset as before, and Accept saves the restored stick rows.
/// Runs over <see cref="FakeStickNative"/>.
/// </summary>
public sealed class ControlsResetStickTests : IDisposable
{
    private const string RShipped = """
        { "model": "231D/0200", "name": "R", "contexts": {
            "flight": { "FireGuns": ["button:#0"], "PitchUp": ["fullaxis:1+@0.02"], "ThrottleLever": ["fullaxis:2-@0.02"] },
            "menu": { "MenuAccept": ["button:#0"] } } }
        """;

    private const string RUser = """
        { "model": "231D/0200", "name": "R", "contexts": {
            "flight": { "FireGuns": ["button:#5"], "PitchUp": ["fullaxis:3+@0.02"] },
            "menu": { "MenuAccept": ["button:#5"] } } }
        """;

    private const string TartarusIgnored = """
        { "model": "1532/022B", "ignore": true }
        """;

    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly StickModel Generic = new(0x044F, 0xB10A);
    private static readonly StickModel Other = new(0x044F, 0xB10B);
    private static readonly StickModel Tartarus = new(0x1532, 0x022B);

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();
    private readonly FakeStickNative _native = new();
    private StickRoster? _roster;

    public void Dispose() => _roster?.Dispose();

    [Fact]
    public void AShippedProfileRestoresItsRowsOverTheUserFile()
    {
        Ship("231D-0200.json", RShipped);
        Own("231D-0200.json", RUser);
        var set = Profiles(VkbR);
        var (feature, _) = Screen(set);
        Assert.Contains(Bind(VkbR, BindingControl.Button(5)), feature.Bindings(InputContext.Flight, InputAction.FireGuns));

        feature.ResetSeat();

        var fire = feature.Bindings(InputContext.Flight, InputAction.FireGuns);
        Assert.Contains(Bind(VkbR, BindingControl.Button(0)), fire);
        Assert.DoesNotContain(Bind(VkbR, BindingControl.Button(5)), fire);
        var pitch = Bind(VkbR, BindingControl.FullAxis(1, false, 0.02f));
        Assert.Contains(pitch, feature.Bindings(InputContext.Flight, InputAction.PitchUp));
        Assert.Contains(pitch, feature.Bindings(InputContext.Flight, InputAction.PitchDown));
        Assert.DoesNotContain(Bind(VkbR, BindingControl.FullAxis(3, false, 0.02f)), feature.Bindings(InputContext.Flight, InputAction.PitchDown));
        Assert.Equal(
            new[] { Bind(VkbR, BindingControl.FullAxis(2, true, 0.02f)) },
            feature.Bindings(InputContext.Flight, InputAction.ThrottleLever).Where(StickProfileResolver.IsStick));
        Assert.Contains(Bind(VkbR, BindingControl.Button(0)), feature.Bindings(InputContext.Menu, InputAction.MenuAccept));
    }

    [Fact]
    public void OneUnprofiledStickGetsTheGenericDefault()
    {
        var set = Profiles(Generic);
        var (feature, _) = Screen(set);
        Stage(feature, InputContext.Flight, InputAction.FireGuns, Bind(Generic, BindingControl.Button(7)));

        feature.ResetSeat();

        var expected = GenericStickDefault.For(Generic, 8);
        foreach (var context in Enum.GetValues<InputContext>())
        {
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                Assert.Equal(
                    expected.Map(context).Bindings(action),
                    feature.Bindings(context, action).Where(StickProfileResolver.IsStick));
            }
        }
    }

    [Fact]
    public void TwoUnprofiledSticksKeepTheirRows()
    {
        Own("044F-B10A.json", """{ "model": "044F/B10A", "contexts": { "flight": { "FireGuns": ["button:#7"] } } }""");
        Own("044F-B10B.json", """{ "model": "044F/B10B", "contexts": { "flight": { "FireGuns": ["button:#8"] } } }""");
        var set = Profiles(Generic, Other);
        var (feature, _) = Screen(set);
        var before = StickRows(feature);

        feature.ResetSeat();

        Assert.Equal(before, StickRows(feature));
        Assert.Contains(Bind(Generic, BindingControl.Button(7)), feature.Bindings(InputContext.Flight, InputAction.FireGuns));
        Assert.Contains(Bind(Other, BindingControl.Button(8)), feature.Bindings(InputContext.Flight, InputAction.FireGuns));
        Assert.Empty(feature.Bindings(InputContext.Flight, InputAction.PitchUp).Where(StickProfileResolver.IsStick));
    }

    // Its user file is the active profile, but a reset counts shipped files alone. Beside a shipped
    // model it is the one unshipped stick, so it takes the generic default.
    [Fact]
    public void AUserFileOnlyStickBesideAShippedModelTakesTheGenericDefault()
    {
        Ship("231D-0200.json", RShipped);
        Own("044F-B10A.json", """{ "model": "044F/B10A", "contexts": { "flight": { "FireGuns": ["button:#7"] } } }""");
        var set = Profiles(Generic, VkbR);
        Assert.Equal(StickProfileSource.User, set.Active[Generic].Source);
        var (feature, _) = Screen(set);

        feature.ResetSeat();

        var fire = feature.Bindings(InputContext.Flight, InputAction.FireGuns);
        Assert.Contains(Bind(VkbR, BindingControl.Button(0)), fire);
        Assert.Contains(Bind(Generic, BindingControl.Button(0)), fire);
        Assert.DoesNotContain(Bind(Generic, BindingControl.Button(7)), fire);
        Assert.Contains(Bind(Generic, BindingControl.FullAxis(1, false, GenericStickDefault.AxisDeadzone)), feature.Bindings(InputContext.Flight, InputAction.PitchUp));
    }

    [Fact]
    public void AnIgnoredModelGetsNoRowsAndLeavesTheDefaultToTheStick()
    {
        Ship("1532-022B.json", TartarusIgnored);
        var set = Profiles(Generic, Tartarus);
        var (feature, _) = Screen(set);

        feature.ResetSeat();

        Assert.DoesNotContain(StickRows(feature), row => row.Binding.Device == Tartarus.Device);
        Assert.Contains(Bind(Generic, BindingControl.Button(0)), feature.Bindings(InputContext.Flight, InputAction.FireGuns));
    }

    [Fact]
    public void ResettingOneContextLeavesTheOtherContextsStickRows()
    {
        Ship("231D-0200.json", RShipped);
        Own("231D-0200.json", RUser);
        var set = Profiles(VkbR);
        var (feature, _) = Screen(set);
        feature.Context = InputContext.Menu;

        feature.ResetContext();

        Assert.Contains(Bind(VkbR, BindingControl.Button(0)), feature.Bindings(InputContext.Menu, InputAction.MenuAccept));
        Assert.DoesNotContain(Bind(VkbR, BindingControl.Button(5)), feature.Bindings(InputContext.Menu, InputAction.MenuAccept));
        Assert.Contains(Bind(VkbR, BindingControl.Button(5)), feature.Bindings(InputContext.Flight, InputAction.FireGuns));
        Assert.Contains(Bind(VkbR, BindingControl.FullAxis(3, false, 0.02f)), feature.Bindings(InputContext.Flight, InputAction.PitchUp));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KeyboardAndPadRowsResetToTheShippedDefaults(int player)
    {
        Ship("231D-0200.json", RShipped);
        var set = Profiles(VkbR);
        var (feature, _) = Screen(set, player: player);
        Stage(feature, InputContext.Flight, InputAction.FireGuns, new Binding(DeviceId.Keyboard, BindingControl.Key(75)));

        feature.ResetSeat();

        foreach (var context in Enum.GetValues<InputContext>())
        {
            var shipped = DefaultBindings.MapFor(context, DefaultBindings.AnyPad);
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                Assert.Equal(shipped.Bindings(action), feature.Bindings(context, action).Where(b => !StickProfileResolver.IsStick(b)));
            }
        }

        // Only seat 1 holds stick rows.
        Assert.Equal(player == 1, StickRows(feature).Count > 0);
    }

    [Fact]
    public void AcceptAfterAResetWritesTheRestoredRows()
    {
        Ship("231D-0200.json", RShipped);
        Own("231D-0200.json", RUser);
        var set = Profiles(VkbR);
        var written = new Dictionary<int, BindingProfile>();
        var (feature, live) = Screen(set, (player, keymap) => StickScreens.Save(player, keymap, set, (who, file) => written[who] = file));

        feature.ResetSeat();
        feature.Accept();

        var saved = set.ActiveFor(VkbR)!;
        Assert.Equal(StickProfileSource.User, set.Active[VkbR].Source);
        Assert.Equal(new[] { Bind(VkbR, BindingControl.Button(0)) }, saved.Map(InputContext.Flight).Bindings(InputAction.FireGuns));
        Assert.Equal(new[] { Bind(VkbR, BindingControl.FullAxis(2, true, 0.02f)) }, saved.Map(InputContext.Flight).Bindings(InputAction.ThrottleLever));
        Assert.Equal(new[] { Bind(VkbR, BindingControl.Button(0)) }, saved.Map(InputContext.Menu).Bindings(InputAction.MenuAccept));
        Assert.Contains("button:#0", File.ReadAllText(Path.Combine(_user, "231D-0200.json")));
        Assert.DoesNotContain("button:#5", File.ReadAllText(Path.Combine(_user, "231D-0200.json")));
        Assert.Contains(Bind(VkbR, BindingControl.Button(0)), live.Map(InputContext.Flight).Bindings(InputAction.FireGuns));
        Assert.DoesNotContain("stick:", BindingStore.Serialize(1, written[1]));
    }

    [Fact]
    public void WithSticksOffAResetClearsStickRowsAsBefore()
    {
        var feature = new ControlsFeature(stickRows: () => null);
        var profile = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: true);
        profile.Map(InputContext.Flight).Add(InputAction.FireGuns, Bind(VkbR, BindingControl.Button(5)));
        feature.AddSeat(1, profile, new NoDevices(), readsKeyboard: true);

        feature.ResetSeat();

        Assert.Empty(StickRows(feature));
    }

    private static Binding Bind(StickModel model, BindingControl control) => new(model.Device, control);

    private static List<(InputContext Context, InputAction Action, Binding Binding)> StickRows(ControlsFeature feature)
    {
        var rows = new List<(InputContext, InputAction, Binding)>();
        foreach (var context in Enum.GetValues<InputContext>())
        {
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                foreach (var binding in feature.Bindings(context, action).Where(StickProfileResolver.IsStick))
                {
                    rows.Add((context, action, binding));
                }
            }
        }

        return rows;
    }

    // One control added to an action in the staged keymap, as a capture on an empty slot adds it.
    private static void Stage(ControlsFeature feature, InputContext context, InputAction action, Binding binding)
    {
        feature.Context = context;
        feature.Focus(feature.Actions.ToList().IndexOf(action));
        feature.MoveSlot(feature.FocusedBindings.Count);
        feature.Offer(binding);
        feature.ConfirmSteal();
        Assert.Contains(binding, feature.Bindings(context, action));
    }

    private void Ship(string name, string json) => File.WriteAllText(Path.Combine(_shipped, name), json);

    private void Own(string name, string json) => File.WriteAllText(Path.Combine(_user, name), json);

    // A settled roster of the given models on instances 1, 2, ... and the profile set over it.
    private StickProfileSet Profiles(params StickModel[] models)
    {
        for (int i = 0; i < models.Length; i++)
        {
            _native.Plug(i + 1, models[i].ToString(), models[i]);
        }

        var roster = new StickRoster(_native, Array.Empty<StickModel>, () => false);
        _roster = roster;
        for (int i = 0; i <= StickRoster.SettleUpdates; i++)
        {
            roster.Update();
        }

        var set = new StickProfileSet(
            new StickProfileStore(() => StickProfileStore.ReadDirectory(_shipped), _user),
            () => StickProfileSet.ModelsOf(roster),
            model => StickShape.Of(roster, model));
        set.Reload();
        return set;
    }

    // The player's live keymap as a launch builds it: the shipped defaults with the stick rows
    // merged in for player 1.
    private (ControlsFeature Feature, BindingProfile Live) Screen(
        StickProfileSet set, Action<int, BindingProfile>? save = null, int player = 1)
    {
        var feature = new ControlsFeature(save, stickRows: () => set);
        var profile = BindingProfile.Defaults(DefaultBindings.AnyPad, readsKeyboard: true);
        if (player == 1)
        {
            set.MergeInto(profile);
        }

        feature.AddSeat(player, profile, new NoDevices(), readsKeyboard: true);
        return (feature, profile);
    }

    private sealed class NoDevices : ICaptureDevices
    {
        private readonly SeatDeviceState _state = new(DefaultBindings.AnyPad, () => null);

        public DeviceId PadOf(InputContext context) => DefaultBindings.AnyPad;

        public IDeviceState For(InputContext context) => _state;
    }
}
