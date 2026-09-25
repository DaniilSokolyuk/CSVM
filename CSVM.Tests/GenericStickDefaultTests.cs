using System;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The generic single-stick default: the shape test on a sampled rest, the exactly-one rule over
/// unprofiled models, and the rows it binds. It lives in memory until a changed save. A menu seat
/// navigates on its hat and buttons through the fake stick library.
/// </summary>
public sealed class GenericStickDefaultTests
{
    private const string RSolo = """
        { "model": "231D/0200", "name": "R", "contexts": { "flight": { "FireGuns": ["button:#0"] } } }
        """;

    private const string RIgnored = """
        { "model": "231D/0200", "ignore": true }
        """;

    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly StickModel Tartarus = new(0x1532, 0x022B);
    private static readonly StickModel Generic = new(0x044F, 0xB10A);
    private static readonly StickModel Pedals = new(0x044F, 0xB679);

    // The committed shipped folder, res://data/stick_profiles/ in the game.
    private static readonly string ShippedProfiles = Path.Combine(TestData.RepoRoot, "CSVM", "data", "stick_profiles");

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();

    [Fact]
    public void AStickIsUnsettledUntilSampledThenJudgedByAxesZeroAndOne()
    {
        Assert.Equal(StickFit.Unsettled, StickShape.Judge(8, null).Fit);
        Assert.Equal(StickFit.Stick, StickShape.Judge(8, new[] { 0f, 0.01f, -0.57f, 0f, 0f, 0f, 0f, 0f }).Fit);
        Assert.Equal(StickFit.Stick, StickShape.Judge(8, new[] { 0f, 0.01f, 1f, 0f, 0f, 0f, 0f, 0f }).Fit);
        Assert.Equal(StickFit.Stick, StickShape.Judge(3, new[] { StickShape.CentreTolerance, -StickShape.CentreTolerance, 0f }).Fit);
        Assert.Equal(StickFit.NotStick, StickShape.Judge(3, new[] { 0.11f, 0f, 0f }).Fit);
        Assert.Equal(StickFit.NotStick, StickShape.Judge(3, new[] { 0f, -1f, 0f }).Fit);
        Assert.Equal(StickFit.NotStick, StickShape.Judge(2, new[] { 0f, 0f }).Fit);
    }

    // The keypad rests with every axis centred, so the shape test passes it and only the shipped
    // ignore profile keeps the default off it.
    [Fact]
    public void TheTartarusRestingCentredPassesTheShapeTest()
    {
        Assert.Equal(StickFit.Stick, StickShape.Judge(6, new[] { 0f, 0f, 0f, 0f, 0f, 0f }).Fit);
    }

    [Fact]
    public void TheRosterSamplesTheRestAfterTheSettleUpdatesAndReportsIt()
    {
        var native = new FakeStickNative();
        native.Plug(1, "Stick", Generic, axes: 4);
        native.SetAxis(1, 2, -32768);
        using var roster = new StickRoster(native, () => Array.Empty<StickModel>(), () => true);
        Assert.True(roster.Update());
        var stick = roster.Sticks.Single();

        for (int i = 1; i < StickRoster.SettleUpdates; i++)
        {
            Assert.False(roster.Update());
            Assert.Null(roster.RestingAxes(stick));
        }

        Assert.True(roster.Update());
        Assert.Equal(new[] { 0f, 0f, -1f, 0f }, roster.RestingAxes(stick));
        Assert.False(roster.Update());
        Assert.Equal(StickFit.Stick, StickShape.Of(roster, Generic).Fit);
    }

    [Fact]
    public void ExactlyOneSettledStickShapedCandidateIsPicked()
    {
        StickShape Shape(StickModel model) => model == Pedals
            ? new StickShape(StickFit.NotStick, 3)
            : new StickShape(StickFit.Stick, 8);

        Assert.Null(GenericStickDefault.Pick(Array.Empty<StickModel>(), Shape));
        Assert.Equal(Generic, GenericStickDefault.Pick(new[] { Generic }, Shape));
        Assert.Null(GenericStickDefault.Pick(new[] { Generic, VkbR }, Shape));
        Assert.Equal(Generic, GenericStickDefault.Pick(new[] { Pedals, Generic }, Shape));
        Assert.Null(GenericStickDefault.Pick(new[] { Pedals }, Shape));
        Assert.Null(GenericStickDefault.Pick(new[] { Generic, Pedals }, m => m == Pedals ? new StickShape(StickFit.Unsettled, 3) : Shape(m)));
    }

    [Fact]
    public void TheDefaultFliesXYAndTwistWithZAsTheLeverAndFiresOnButtonsZeroAndOne()
    {
        var flight = GenericStickDefault.For(Generic, 8).Map(InputContext.Flight);

        Assert.Equal(FullAxis(0, false), flight.Bindings(InputAction.RollRight).Single());
        Assert.Equal(FullAxis(0, false), flight.Bindings(InputAction.RollLeft).Single());
        Assert.Equal(FullAxis(1, false), flight.Bindings(InputAction.PitchUp).Single());
        Assert.Equal(FullAxis(1, false), flight.Bindings(InputAction.PitchDown).Single());
        Assert.Equal(FullAxis(5, false), flight.Bindings(InputAction.YawRight).Single());
        Assert.Equal(FullAxis(2, true), flight.Bindings(InputAction.ThrottleLever).Single());
        Assert.Empty(flight.Bindings(InputAction.ThrottleUp));
        Assert.Equal(Button(0), flight.Bindings(InputAction.FireGuns).Single());
        Assert.Equal(Button(1), flight.Bindings(InputAction.FireRockets).Single());
    }

    [Fact]
    public void AStickWithFewerThanSixAxesGetsNoYaw()
    {
        var flight = GenericStickDefault.For(Generic, 5).Map(InputContext.Flight);

        Assert.Empty(flight.Bindings(InputAction.YawRight));
        Assert.Empty(flight.Bindings(InputAction.YawLeft));
        Assert.NotEmpty(flight.Bindings(InputAction.ThrottleLever));
    }

    [Fact]
    public void TheMenuRowsAreTheHatTheTriggerAndButtonOne()
    {
        var menu = GenericStickDefault.For(Generic, 8).Map(InputContext.Menu);

        Assert.Equal(Hat(HatDirection.Up), menu.Bindings(InputAction.MenuUp).Single());
        Assert.Equal(Hat(HatDirection.Down), menu.Bindings(InputAction.MenuDown).Single());
        Assert.Equal(Hat(HatDirection.Left), menu.Bindings(InputAction.MenuLeft).Single());
        Assert.Equal(Hat(HatDirection.Right), menu.Bindings(InputAction.MenuRight).Single());
        Assert.Equal(Button(0), menu.Bindings(InputAction.MenuAccept).Single());
        Assert.Equal(Button(1), menu.Bindings(InputAction.MenuBack).Single());
    }

    [Fact]
    public void NoUnprofiledStickGetsNothing()
    {
        File.WriteAllText(Path.Combine(_shipped, "231D-0200.json"), RSolo);
        var (_, roster) = Roster((2, VkbR));
        using (roster)
        {
            var set = Set(roster);

            Assert.Equal(StickProfileSource.Shipped, set.Active[VkbR].Source);
            Assert.DoesNotContain(set.Active.Values, f => f.Source == StickProfileSource.Generic);
        }
    }

    [Fact]
    public void OneUnprofiledStickGetsTheDefaultOnceItSettles()
    {
        var (_, roster) = Roster((1, Generic));
        using (roster)
        {
            var set = Set(roster, settle: false);
            Assert.Empty(set.Active);
            int revision = set.Revision;

            Settle(roster);
            Assert.True(set.Refresh());

            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
            Assert.Equal(GenericStickDefault.FileName, set.Active[Generic].FileName);
            Assert.Equal(revision + 1, set.Revision);
            Assert.False(set.Refresh());
            Assert.Equal(FullAxis(1, false), set.Map(InputContext.Flight).Bindings(InputAction.PitchUp).Single());
        }
    }

    [Fact]
    public void TwoUnprofiledSticksGetNothing()
    {
        var (_, roster) = Roster((1, Generic), (2, VkbR));
        using (roster)
        {
            var set = Set(roster);

            Assert.Empty(set.Active);
        }
    }

    [Fact]
    public void AProfiledStickBesideAnUnprofiledOneLeavesTheDefaultToTheUnprofiledOne()
    {
        File.WriteAllText(Path.Combine(_shipped, "231D-0200.json"), RSolo);
        var (_, roster) = Roster((1, Generic), (2, VkbR));
        using (roster)
        {
            var set = Set(roster);

            Assert.Equal(StickProfileSource.Shipped, set.Active[VkbR].Source);
            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
        }
    }

    [Fact]
    public void AnIgnoredStickCountsAsProfiled()
    {
        File.WriteAllText(Path.Combine(_user, "231D-0200.json"), RIgnored);
        var (_, roster) = Roster((1, Generic), (2, VkbR));
        using (roster)
        {
            var set = Set(roster);

            Assert.True(set.Active[VkbR].Profile.Ignore);
            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
        }
    }

    [Fact]
    public void TheShippedProfilesIgnoreTheTartarusSoItNeverTakesTheDefault()
    {
        var (_, roster) = Roster((4, Tartarus));
        using (roster)
        {
            var set = Set(roster, shipped: ShippedProfiles);

            var tartarus = set.Active[Tartarus];
            Assert.Equal(StickProfileSource.Shipped, tartarus.Source);
            Assert.Equal("1532-022B.json", tartarus.FileName);
            Assert.True(tartarus.Profile.Ignore);
            Assert.DoesNotContain(set.Active.Values, f => f.Source == StickProfileSource.Generic);
            Assert.DoesNotContain(set.Map(InputContext.Flight).Bindings(InputAction.FireGuns), StickProfileResolver.IsStick);
        }
    }

    [Fact]
    public void WithTheShippedProfilesAStickBesideTheTartarusStillTakesTheDefault()
    {
        var (_, roster) = Roster((1, Generic), (4, Tartarus));
        using (roster)
        {
            var set = Set(roster, shipped: ShippedProfiles);

            Assert.True(set.Active[Tartarus].Profile.Ignore);
            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
        }
    }

    [Fact]
    public void ANonStickBesideOneStickLeavesTheDefaultToTheStick()
    {
        var (native, roster) = Roster((1, Generic), (3, Pedals));
        native.SetAxis(3, 0, -32768);
        native.SetAxis(3, 1, -32768);
        using (roster)
        {
            var set = Set(roster);

            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
            Assert.False(set.Active.ContainsKey(Pedals));
        }
    }

    [Fact]
    public void UnpluggingTheSecondStickHandsTheDefaultToTheOneLeft()
    {
        var (native, roster) = Roster((1, Generic), (2, VkbR));
        using (roster)
        {
            var set = Set(roster);
            Assert.Empty(set.Active);

            native.Unplug(2);
            Assert.True(roster.Update());
            Assert.True(set.Refresh());

            Assert.Equal(StickProfileSource.Generic, set.Active[Generic].Source);
        }
    }

    [Fact]
    public void TheDefaultIsWrittenOnlyWhenASaveChangesIt()
    {
        var (_, roster) = Roster((1, Generic));
        using (roster)
        {
            var set = Set(roster);
            var keymap = BindingProfile.Defaults(default, readsKeyboard: true);
            set.MergeInto(keymap);

            Assert.Empty(set.SaveFrom(keymap));
            Assert.Empty(Directory.GetFiles(_user));

            keymap.Map(InputContext.Flight).Assign(InputAction.Nitro, Button(3));
            var written = set.SaveFrom(keymap);

            Assert.Equal(new[] { "044F-B10A.json" }, written.Select(f => f.FileName));
            Assert.Equal(StickProfileSource.User, set.Active[Generic].Source);
            var saved = set.ActiveFor(Generic)!.Map(InputContext.Flight);
            Assert.Equal(FullAxis(2, true), saved.Bindings(InputAction.ThrottleLever).Single());
            Assert.Equal(Button(3), saved.Bindings(InputAction.Nitro).Single());
        }
    }

    [Fact]
    public void SeatOnesMenuNavigatesOnTheHatConfirmsOnTheTriggerAndBacksOutOnButtonOne()
    {
        var (native, roster) = Roster((1, Generic));
        using (roster)
        {
            var set = Set(roster);
            var input = new MenuInput(() => roster, () => set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
            input.Prime();

            native.SetHat(1, 0, (byte)HatDirection.Down);
            input.Poll(0.016f);
            Assert.Equal(1, input.Move);
            Assert.Equal(1, input.PadMove);

            native.SetHat(1, 0, (byte)HatDirection.Left);
            input.Poll(0.016f);
            Assert.Equal(-1, input.MoveX);

            native.SetHat(1, 0, 0);
            native.Press(1, 0);
            input.Poll(0.016f);
            Assert.True(input.Accept);
            Assert.Equal(0, input.Move);
            input.Poll(0.016f);
            Assert.False(input.Accept);

            native.Release(1, 0);
            native.Press(1, 1);
            input.Poll(0.016f);
            Assert.True(input.Back);
            Assert.True(input.PadBack);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AnySeatButPlayerOneReadsNoStick(int player)
    {
        var (native, roster) = Roster((1, Generic));
        using (roster)
        {
            var set = Set(roster);
            var input = new MenuInput(() => roster, () => set, player) { Keyboard = false, Pads = Array.Empty<int>() };
            input.Prime();

            native.SetHat(1, 0, (byte)HatDirection.Down);
            native.Press(1, 0);
            input.Poll(0.016f);

            Assert.Equal(0, input.Move);
            Assert.False(input.Accept);
            Assert.DoesNotContain(input.Map.Bindings(InputAction.MenuAccept), StickProfileResolver.IsStick);
        }
    }

    [Fact]
    public void AMenuSeatPicksTheDefaultUpWhenTheStickSettlesAfterTheSeatWasBuilt()
    {
        var (native, roster) = Roster((1, Generic));
        using (roster)
        {
            var set = Set(roster, settle: false);
            var input = new MenuInput(() => roster, () => set, player: 1) { Keyboard = false, Pads = Array.Empty<int>() };
            input.Prime();
            native.Press(1, 0);
            input.Poll(0.016f);
            Assert.False(input.Accept);

            Settle(roster);
            set.Refresh();
            input.Poll(0.016f);

            Assert.True(input.Accept);
        }
    }

    private static Binding FullAxis(int axis, bool inverted) =>
        new(Generic.Device, BindingControl.FullAxis(axis, inverted, GenericStickDefault.AxisDeadzone));

    private static Binding Button(int index) => new(Generic.Device, BindingControl.Button(index));

    private static Binding Hat(HatDirection direction) => new(Generic.Device, BindingControl.Hat(0, direction));

    private static (FakeStickNative Native, StickRoster Roster) Roster(params (int Instance, StickModel Model)[] sticks)
    {
        var native = new FakeStickNative();
        foreach (var (instance, model) in sticks)
        {
            native.Plug(instance, model.ToString(), model, axes: model == Tartarus ? 6 : 8);
        }

        var roster = new StickRoster(native, () => Array.Empty<StickModel>(), () => false);
        roster.Update();
        return (native, roster);
    }

    private static void Settle(StickRoster roster)
    {
        for (int i = 0; i < StickRoster.SettleUpdates; i++)
        {
            roster.Update();
        }
    }

    private StickProfileSet Set(StickRoster roster, bool settle = true, string? shipped = null)
    {
        if (settle)
        {
            Settle(roster);
        }

        string shippedDirectory = shipped ?? _shipped;
        var set = new StickProfileSet(
            new StickProfileStore(() => StickProfileStore.ReadDirectory(shippedDirectory), _user),
            () => StickProfileSet.ModelsOf(roster),
            model => StickShape.Of(roster, model));
        set.Reload();
        return set;
    }
}
