using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The stick profile files: which file is active for which connected models, and a user file over a
/// shipped one. Saves never touch the shipped set. The ignore flag, hand-typed deadzones and model
/// case survive a re-save. Also the merge into seat 1's keymap, and a live switch on a plug through
/// the fake stick library. The models are the user's VKB Gladiator EVO L and R.
/// </summary>
public sealed class StickProfileTests
{
    private const string RSolo = """
        { "model": "231D/0200", "name": "R", "contexts": { "flight": { "FireGuns": ["button:#0"] } } }
        """;

    private const string RHosas = """
        { "model": "231D/0200", "name": "R", "companions": ["231D/0201"],
          "contexts": { "flight": { "FireGuns": ["button:#1"] } } }
        """;

    private const string LHosas = """
        { "model": "231D/0201", "name": "L", "companions": ["231D/0200"],
          "contexts": { "flight": { "ThrottleUp": ["fullaxis:1-@0.08"] } } }
        """;

    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly StickModel Tartarus = new(0x1532, 0x022B);

    private readonly string _shipped = TestData.TempDir();
    private readonly string _user = TestData.TempDir();

    [Fact]
    public void RAloneTakesItsSoloFileAndRWithLTakesTheFileNamingL()
    {
        var files = new[] { Shipped("231D-0200.json", RSolo), Shipped("231D-0200+231D-0201.json", RHosas), Shipped("231D-0201+231D-0200.json", LHosas) };

        var alone = StickProfileResolver.Resolve(new[] { VkbR }, files);
        var both = StickProfileResolver.Resolve(new[] { VkbR, VkbL }, files);

        Assert.Equal("231D-0200.json", alone[VkbR].FileName);
        Assert.False(alone.ContainsKey(VkbL));
        Assert.Equal("231D-0200+231D-0201.json", both[VkbR].FileName);
        Assert.Equal("231D-0201+231D-0200.json", both[VkbL].FileName);
    }

    [Fact]
    public void LAloneHasNoProfileWhenItsOnlyFileNamesR()
    {
        var files = new[] { Shipped("231D-0201+231D-0200.json", LHosas) };

        Assert.Empty(StickProfileResolver.Resolve(new[] { VkbL }, files));
    }

    [Fact]
    public void AUserFileOverridesTheShippedFileForTheSameLayout()
    {
        var files = new[] { User("zz-mine.json", RSolo), Shipped("231D-0200.json", RSolo) };

        var active = StickProfileResolver.Resolve(new[] { VkbR }, files);

        Assert.Equal(StickProfileSource.User, active[VkbR].Source);
    }

    [Fact]
    public void MoreCompanionsOutranksAUserFileForALesserLayout()
    {
        var files = new[] { User("231D-0200.json", RSolo), Shipped("231D-0200+231D-0201.json", RHosas) };

        var active = StickProfileResolver.Resolve(new[] { VkbR, VkbL }, files);

        Assert.Equal("231D-0200+231D-0201.json", active[VkbR].FileName);
    }

    [Fact]
    public void TwoEqualFilesAreSettledByOrdinalFileNameWhateverTheListOrder()
    {
        var b = User("b.json", RSolo);
        var a = User("a.json", RSolo);

        Assert.Equal("a.json", StickProfileResolver.Resolve(new[] { VkbR }, new[] { b, a })[VkbR].FileName);
        Assert.Equal("a.json", StickProfileResolver.Resolve(new[] { VkbR }, new[] { a, b })[VkbR].FileName);
    }

    /// <summary>The user's Explorer copy: "231D-0200 - Kopie.json" sorts ahead of the file it copies,
    /// and the tie is what the set warns about. A shipped file and a fuller layout are no tie.</summary>
    [Fact]
    public void ACopyBesideAUserFileTiesWithItAndNothingElseDoes()
    {
        var copy = User("231D-0200 - Kopie.json", RSolo);
        var own = User("231D-0200.json", RSolo);
        var shipped = Shipped("231D-0200.json", RSolo);
        var hosas = User("231D-0200+231D-0201.json", RHosas);
        var files = new[] { own, shipped, hosas, copy };
        var connected = new HashSet<StickModel> { VkbR };

        var winner = StickProfileResolver.Resolve(connected, files)[VkbR];

        Assert.Same(copy, winner);
        Assert.Equal(new[] { own }, StickProfileResolver.TiedWith(winner, files, connected));
    }

    [Fact]
    public void FileNamesListTheModelThenItsCompanionsInModelOrder()
    {
        var profile = new StickProfile(VkbR, new[] { Tartarus, VkbL, VkbR, VkbL });

        Assert.Equal(new[] { Tartarus, VkbL }, profile.Companions);
        Assert.Equal("231D-0200+1532-022B+231D-0201.json", StickProfileStore.FileNameFor(profile));
        Assert.Equal("231D-0200.json", StickProfileStore.FileNameFor(new StickProfile(VkbR)));
    }

    [Fact]
    public void SavingAShippedProfileWritesAUserCopyAndLeavesTheShippedFileAlone()
    {
        string shippedPath = Path.Combine(_shipped, "vkb-r.json");
        File.WriteAllText(shippedPath, RSolo);
        var set = Set(() => new[] { VkbR });
        set.Reload();
        Assert.Equal(StickProfileSource.Shipped, set.Active[VkbR].Source);

        var edited = set.ActiveFor(VkbR)!.Clone();
        edited.Map(InputContext.Flight).Assign(InputAction.Nitro, Button(VkbR, 5));
        var saved = set.Save(edited);

        Assert.Equal(RSolo, File.ReadAllText(shippedPath));
        Assert.Equal(StickProfileSource.User, saved.Source);
        Assert.True(File.Exists(Path.Combine(_user, "231D-0200.json")));
        Assert.Same(saved, set.Active[VkbR]);

        set.Reload();
        Assert.Equal(StickProfileSource.User, set.Active[VkbR].Source);
        Assert.Contains(Button(VkbR, 5), set.ActiveFor(VkbR)!.Map(InputContext.Flight).Bindings(InputAction.Nitro));
    }

    [Fact]
    public void SavingAUserProfileRewritesItsOwnFile()
    {
        File.WriteAllText(Path.Combine(_user, "my-r.json"), RSolo);
        var set = Set(() => new[] { VkbR });
        set.Reload();

        var edited = set.ActiveFor(VkbR)!.Clone();
        edited.Name = "Right";
        set.Save(edited);

        Assert.Equal(new[] { "my-r.json" }, Directory.GetFiles(_user).Select(Path.GetFileName));
        Assert.Contains("\"Right\"", File.ReadAllText(Path.Combine(_user, "my-r.json")));
    }

    [Fact]
    public void AnIgnoredModelYieldsNoBindingsButStillCountsAsProfiled()
    {
        var ignored = User("1532-022B.json", """{ "model": "1532/022B", "ignore": true, "contexts": { "flight": { "FireGuns": ["button:#0"] } } }""");
        var active = StickProfileResolver.Resolve(new[] { Tartarus }, new[] { ignored });
        var keymap = BindingProfile.Defaults(default, readsKeyboard: true);

        StickProfileResolver.MergeInto(keymap, active.Values.Select(f => f.Profile));

        Assert.True(active.ContainsKey(Tartarus));
        Assert.True(active[Tartarus].Profile.Ignore);
        Assert.DoesNotContain(keymap.Map(InputContext.Flight).Bindings(InputAction.FireGuns), StickProfileResolver.IsStick);
        Assert.Empty(StickProfileResolver.Rows(active.Values.Select(f => f.Profile), InputContext.Flight).BoundActions);
    }

    [Fact]
    public void AHandTypedDeadzoneSurvivesAReSaveExactly()
    {
        const string json = """
            { "model": "231D/0200",
              "contexts": { "flight": { "PitchUp": ["fullaxis:1+@0.0125"], "ThrottleLever": ["fullaxis:2-@0.3"] } } }
            """;
        var profile = StickProfileStore.Deserialize(json, out _)!;
        var pitch = profile.Map(InputContext.Flight).Bindings(InputAction.PitchDown).Single();

        string saved = StickProfileStore.Serialize(profile);
        var again = StickProfileStore.Deserialize(saved, out _)!;

        Assert.Equal(0.0125f, pitch.Control.Deadzone);
        Assert.Contains("\"fullaxis:1+@0.0125\"", saved);
        Assert.Contains("\"fullaxis:2-@0.3\"", saved);
        Assert.DoesNotContain("PitchDown", saved);
        Assert.Equal(saved, StickProfileStore.Serialize(again));
    }

    [Fact]
    public void ARowThisBuildCannotReadBindsNothingAndIsWrittenBackVerbatim()
    {
        const string json = """
            { "model": "231D/0200",
              "contexts": { "flight": { "PitchUp": ["fullaxis:1+@0.99"], "FireGuns": ["button:#0"], "Warp": ["button:#9"] },
                            "future": { "Thing": ["button:#1"] } } }
            """;
        var profile = StickProfileStore.Deserialize(json, out _)!;

        string saved = StickProfileStore.Serialize(profile);

        Assert.Empty(profile.Map(InputContext.Flight).Bindings(InputAction.PitchUp));
        Assert.Contains("\"PitchUp\": [\"fullaxis:1+@0.99\"]", saved);
        Assert.Contains("\"Warp\": [\"button:#9\"]", saved);
        Assert.Contains("\"future\"", saved);
        Assert.Contains("\"Thing\"", saved);
    }

    [Fact]
    public void AnEditedRowReplacesTheUnreadRowItStoodFor()
    {
        var profile = StickProfileStore.Deserialize(
            """{ "model": "231D/0200", "contexts": { "flight": { "PitchUp": ["fullaxis:1+@0.99"] } } }""", out _)!;

        profile.Map(InputContext.Flight).Assign(InputAction.PitchUp, new Binding(VkbR.Device, BindingControl.FullAxis(1, false, 0.02f)));
        string saved = StickProfileStore.Serialize(profile);

        Assert.DoesNotContain("0.99", saved);
        Assert.Contains("\"fullaxis:1+@0.02\"", saved);
    }

    [Fact]
    public void ModelsAndTokensInAnyCaseLoadOntoTheCanonicalIdentity()
    {
        const string json = """
            { "model": "231d/0200", "companions": ["231d/0201"],
              "contexts": { "flight": { "FireGuns": ["pad:stick:231d/0200/button:#3"], "Nitro": ["button:#4"] } } }
            """;
        var profile = StickProfileStore.Deserialize(json, out _)!;
        var map = profile.Map(InputContext.Flight);

        Assert.Equal(VkbR, profile.Model);
        Assert.Equal(new[] { VkbL }, profile.Companions);
        Assert.Equal(Button(VkbR, 3), map.Bindings(InputAction.FireGuns).Single());
        Assert.Equal("stick:231D/0200", map.Bindings(InputAction.FireGuns).Single().Device.Id);
        Assert.Equal(Button(VkbR, 4), map.Bindings(InputAction.Nitro).Single());
        Assert.Contains("\"model\": \"231D/0200\"", StickProfileStore.Serialize(profile));
    }

    [Theory]
    [InlineData("""{ "contexts": {} }""")]
    [InlineData("""{ "model": "231D-0200" }""")]
    [InlineData("""{ "model": "231D/0200", "companions": ["L"] }""")]
    [InlineData("""{ "model": "231D/0200", "companions": "231D/0201" }""")]
    [InlineData("""[ "231D/0200" ]""")]
    [InlineData("""{ "model": """)]
    public void AFileWithNoUsableModelOrCompanionsIsRefused(string json)
    {
        Assert.Null(StickProfileStore.Deserialize(json, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ATokenNamingAnotherDeviceMakesItsRowUnreadable()
    {
        const string json = """
            { "model": "231D/0200", "contexts": { "flight": { "FireGuns": ["pad:stick:231D/0201/button:#3"], "Nitro": ["keyboard/key:Space"] } } }
            """;
        var profile = StickProfileStore.Deserialize(json, out _)!;

        Assert.Empty(profile.Map(InputContext.Flight).Bindings(InputAction.FireGuns));
        Assert.Empty(profile.Map(InputContext.Flight).Bindings(InputAction.Nitro));
        Assert.Equal(2, profile.Unread.Count);
    }

    [Fact]
    public void AMalformedFileIsSkippedAndTheRestLoad()
    {
        File.WriteAllText(Path.Combine(_user, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(_user, "231D-0200.json"), RSolo);

        var files = Store().LoadAll();

        Assert.Equal("231D-0200.json", files.Single().FileName);
    }

    [Fact]
    public void TheMergeReplacesStickRowsAndLeavesEveryOtherRowAlone()
    {
        var keymap = BindingProfile.Defaults(default, readsKeyboard: true);
        var flight = keymap.Map(InputContext.Flight);
        var shippedGuns = flight.Bindings(InputAction.FireGuns).ToArray();
        var stray = new Binding(DeviceId.Joypad("stick:231d/0200"), BindingControl.Button(7));
        flight.Add(InputAction.Nitro, stray);
        var active = new[] { Load(RHosas), Load(LHosas) };

        StickProfileResolver.MergeInto(keymap, active);

        Assert.Equal(shippedGuns.Append(Button(VkbR, 1)), flight.Bindings(InputAction.FireGuns));
        Assert.DoesNotContain(stray, flight.Bindings(InputAction.Nitro));
        var throttle = new Binding(VkbL.Device, BindingControl.FullAxis(1, true, 0.08f));
        Assert.Contains(throttle, flight.Bindings(InputAction.ThrottleUp));
        Assert.Contains(throttle, flight.Bindings(InputAction.ThrottleDown));
    }

    [Fact]
    public void TheKeymapFileIsSavedWithoutStickRows()
    {
        var keymap = BindingProfile.Defaults(default, readsKeyboard: true);
        keymap.MouseFlying = true;
        StickProfileResolver.MergeInto(keymap, new[] { Load(RSolo) });

        var stripped = StickProfileResolver.WithoutStickRows(keymap);

        Assert.Contains(Button(VkbR, 0), keymap.Map(InputContext.Flight).Bindings(InputAction.FireGuns));
        Assert.DoesNotContain(Button(VkbR, 0), stripped.Map(InputContext.Flight).Bindings(InputAction.FireGuns));
        Assert.DoesNotContain("stick:", BindingStore.Serialize(1, stripped));
        Assert.True(stripped.MouseFlying);
    }

    [Fact]
    public void SaveFromWritesOnlyTheModelsWhoseRowsChanged()
    {
        File.WriteAllText(Path.Combine(_shipped, "231D-0200.json"), RSolo);
        var set = Set(() => new[] { VkbR, VkbL });
        set.Reload();
        var keymap = BindingProfile.Defaults(default, readsKeyboard: true);
        set.MergeInto(keymap);

        Assert.Empty(set.SaveFrom(keymap));

        keymap.Map(InputContext.Flight).Assign(InputAction.FireRockets, Button(VkbL, 2));
        var written = set.SaveFrom(keymap);

        Assert.Equal(new[] { "231D-0201.json" }, written.Select(f => f.FileName));
        Assert.False(File.Exists(Path.Combine(_user, "231D-0200.json")));
        Assert.Contains(Button(VkbL, 2), set.ActiveFor(VkbL)!.Map(InputContext.Flight).Bindings(InputAction.FireRockets));
    }

    [Fact]
    public void PluggingLInSwitchesRToItsHosasFileAndUnpluggingSwitchesBack()
    {
        File.WriteAllText(Path.Combine(_shipped, "231D-0200.json"), RSolo);
        File.WriteAllText(Path.Combine(_shipped, "231D-0200+231D-0201.json"), RHosas);
        File.WriteAllText(Path.Combine(_shipped, "231D-0201+231D-0200.json"), LHosas);
        var native = new FakeStickNative();
        native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
        using var roster = new StickRoster(native, () => Array.Empty<StickModel>(), () => false);
        roster.Update();
        var set = Set(() => StickProfileSet.ModelsOf(roster));
        int changes = 0;
        set.Changed += () => changes++;
        set.Reload();
        Assert.Equal("231D-0200.json", set.Active[VkbR].FileName);

        native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
        Assert.True(roster.Update());
        Assert.True(set.Refresh());

        Assert.Equal("231D-0200+231D-0201.json", set.Active[VkbR].FileName);
        Assert.Equal(Button(VkbR, 1), set.Map(InputContext.Flight).Bindings(InputAction.FireGuns).Single());
        Assert.NotEmpty(set.Map(InputContext.Flight).Bindings(InputAction.ThrottleDown));

        native.Unplug(1);
        Assert.True(roster.Update());
        Assert.True(set.Refresh());
        Assert.False(set.Refresh());

        Assert.Equal("231D-0200.json", set.Active[VkbR].FileName);
        Assert.False(set.Active.ContainsKey(VkbL));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void AFlyingSeatsMapFollowsAPlugInPlace()
    {
        File.WriteAllText(Path.Combine(_shipped, "231D-0200.json"), RSolo);
        File.WriteAllText(Path.Combine(_shipped, "231D-0200+231D-0201.json"), RHosas);
        var native = new FakeStickNative();
        native.Plug(2, "VKBsim Gladiator EVO R", VkbR);
        using var roster = new StickRoster(native, () => Array.Empty<StickModel>(), () => false);
        roster.Update();
        var set = Set(() => StickProfileSet.ModelsOf(roster));
        set.Reload();
        var keymap = BindingProfile.Defaults(default, readsKeyboard: true);
        var flight = keymap.Map(InputContext.Flight);
        int seen = -1;

        Assert.True(set.MergeIfChanged(keymap, ref seen));
        Assert.False(set.MergeIfChanged(keymap, ref seen));
        Assert.Contains(Button(VkbR, 0), flight.Bindings(InputAction.FireGuns));

        native.Plug(1, "VKBsim Gladiator EVO L", VkbL);
        roster.Update();
        set.Refresh();

        Assert.True(set.MergeIfChanged(keymap, ref seen));
        Assert.Same(flight, keymap.Map(InputContext.Flight));
        Assert.DoesNotContain(Button(VkbR, 0), flight.Bindings(InputAction.FireGuns));
        Assert.Contains(Button(VkbR, 1), flight.Bindings(InputAction.FireGuns));
    }

    private static Binding Button(StickModel model, int index) => new(model.Device, BindingControl.Button(index));

    private static StickProfile Load(string json) => StickProfileStore.Deserialize(json, out _)!;

    private static StickProfileFile Shipped(string name, string json) => new(StickProfileSource.Shipped, name, Load(json));

    private static StickProfileFile User(string name, string json) => new(StickProfileSource.User, name, Load(json));

    private StickProfileStore Store() => new(() => StickProfileStore.ReadDirectory(_shipped), _user);

    private StickProfileSet Set(Func<IReadOnlyCollection<StickModel>> connected) => new(Store(), connected);
}
