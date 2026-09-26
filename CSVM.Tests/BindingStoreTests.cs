using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Bindings;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>The persistence contract: a profile round-trips through the file whole, an edit and an
/// unbind survive it, a file from a bumped version keeps the rows this build can read, and an
/// unknown action, an unreadable token or a hat row costs that action its saved bindings and
/// nothing else.</summary>
public class BindingStoreTests
{
    private const string StickId = "03005fcf1d2300000002000000000000";
    private static readonly DeviceId Pad = DeviceId.Joypad("030000004c050000c405000000010000");
    private static readonly DeviceId Stick = DeviceId.Joypad(StickId);

    [Fact]
    public void RoundTrip_PreservesEveryBinding()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var loaded = BindingStore.Deserialize(BindingStore.Serialize(1, profile), Pad, readsKeyboard: true);

        AssertSameMaps(profile, loaded);
    }

    /// <summary>An edited map round-trips as edited: the rebind, the action that lost the control,
    /// and an action the player unbound outright all come back the way they were saved.</summary>
    [Fact]
    public void RoundTrip_PreservesARebindAndAnUnbind()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var map = profile.Map(InputContext.Flight);
        var space = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Space));
        Assert.Equal(new[] { InputAction.FireGuns }, map.Assign(InputAction.FireRockets, space));
        map.Clear(InputAction.Nitro);

        var loaded = BindingStore.Deserialize(BindingStore.Serialize(1, profile), Pad, readsKeyboard: true);
        var reloaded = loaded.Map(InputContext.Flight);

        Assert.Contains(space, reloaded.Bindings(InputAction.FireRockets));
        Assert.DoesNotContain(space, reloaded.Bindings(InputAction.FireGuns));
        Assert.Empty(reloaded.Bindings(InputAction.Nitro));
        AssertSameMaps(profile, loaded);
    }

    /// <summary>The seat's flying scheme rides in the same file as the rows it competes with, so a
    /// player who chose the mouse flies with it the next time they launch.</summary>
    [Fact]
    public void RoundTrip_PreservesTheFlyingScheme()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        profile.MouseFlying = true;

        var json = BindingStore.Serialize(1, profile);
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true);

        Assert.Contains("\"mouseFlying\": true", json);
        Assert.True(loaded.MouseFlying);
        AssertSameMaps(profile, loaded);
    }

    /// <summary>A file written before the field existed names no scheme and reads as the shipped
    /// one, which is why the field costs no version bump.</summary>
    [Fact]
    public void AFileNamingNoSchemeLeavesTheSeatOnTheKeyboardAndPad()
    {
        var json = BindingStore.Serialize(1, BindingProfile.Defaults(Pad, readsKeyboard: true));
        var stripped = json.Replace("\"mouseFlying\": false,", string.Empty, StringComparison.Ordinal);

        var loaded = BindingStore.Deserialize(stripped, Pad, readsKeyboard: true);

        Assert.DoesNotContain("mouseFlying", stripped, StringComparison.Ordinal);
        Assert.False(loaded.MouseFlying);
    }

    /// <summary>The shipped sensitivity is written and read back as the unscaled multiplier, and a
    /// moved one comes back as moved.</summary>
    [Fact]
    public void RoundTrip_PreservesTheMouseSensitivity()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        Assert.Equal(SensitivityScale.Default, profile.MouseSensitivity);

        var json = BindingStore.Serialize(1, profile);
        Assert.Contains("\"mouseSensitivity\": 1", json);
        Assert.Equal(1f, BindingStore.Deserialize(json, Pad, readsKeyboard: true).MouseSensitivity);

        profile.MouseSensitivity = SensitivityScale.FromLevel(65);
        var moved = BindingStore.Deserialize(BindingStore.Serialize(1, profile), Pad, readsKeyboard: true);
        Assert.Equal(profile.MouseSensitivity, moved.MouseSensitivity);
        AssertSameMaps(profile, moved);
    }

    /// <summary>A file written before the field existed names no sensitivity and flies the captured
    /// stick unscaled, and a hand-edited value outside the range is clamped rather than obeyed.
    /// </summary>
    [Theory]
    [InlineData(null, 1f)]
    [InlineData("0", 0.25f)]
    [InlineData("-3", 0.25f)]
    [InlineData("100", 4f)]
    [InlineData("\"fast\"", 1f)]
    public void AnOlderOrHandEditedFileReadsASensitivityInsideTheRange(string? field, float expected)
    {
        var json = BindingStore.Serialize(1, BindingProfile.Defaults(Pad, readsKeyboard: true));
        var stripped = json.Replace("\"mouseSensitivity\": 1,", field == null ? string.Empty : $"\"mouseSensitivity\": {field},", StringComparison.Ordinal);
        Assert.NotEqual(json, stripped);

        var loaded = BindingStore.Deserialize(stripped, Pad, readsKeyboard: true);

        Assert.Equal(expected, loaded.MouseSensitivity);
        if (field == null)
        {
            Assert.DoesNotContain("mouseSensitivity", stripped, StringComparison.Ordinal);
        }
    }

    /// <summary>The snap-look diagonals are one key on two actions, so the file has to carry a
    /// control twice inside one context and read it back that way.</summary>
    [Fact]
    public void RoundTrip_KeepsAControlThatDrivesTwoActions()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var loaded = BindingStore.Deserialize(BindingStore.Serialize(1, profile), Pad, readsKeyboard: true)
            .Map(InputContext.Flight);
        var kp7 = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Kp7));

        Assert.Contains(kp7, loaded.Bindings(InputAction.LookUp));
        Assert.Contains(kp7, loaded.Bindings(InputAction.LookLeft));
    }

    /// <summary>A mouse binding round-trips through the file like any other control: the token names
    /// the device and the button in words, and reads back to the same binding.</summary>
    [Fact]
    public void RoundTrip_PreservesAMouseBinding()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var json = BindingStore.Serialize(1, profile);

        Assert.Contains("mouse/mouse:Middle", json, StringComparison.Ordinal);
        Assert.Contains("mouse/mouse:Left", json, StringComparison.Ordinal);

        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);
        Assert.Equal(
            new[] { new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Middle)) },
            loaded.Bindings(InputAction.FreeLook));
        Assert.Contains(
            new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Right)),
            loaded.Bindings(InputAction.FireRockets));
    }

    /// <summary>A keymap saved before the mouse rows moved keeps every row it saved: the free look
    /// stays on the right button the player's file names, the two weapons keep their saved keys and
    /// pad buttons and gain no mouse row, and nothing the file bound is left unbound.</summary>
    [Fact]
    public void AKeymapSavedBeforeTheMouseRowsMoved_KeepsItsOwnRightButtonPan()
    {
        var loaded = BindingStore.Deserialize(SavedBeforeTheMove(), Pad, readsKeyboard: true)
            .Map(InputContext.Flight);

        Assert.Equal(
            new[] { new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Right)) },
            loaded.Bindings(InputAction.FreeLook));
        Assert.Equal(
            new[]
            {
                new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Space)),
                new Binding(Pad, BindingControl.Button((int)JoyButton.B)),
            },
            loaded.Bindings(InputAction.FireGuns));
        Assert.Equal(
            new[]
            {
                new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.X)),
                new Binding(Pad, BindingControl.Button((int)JoyButton.A)),
            },
            loaded.Bindings(InputAction.FireRockets));
    }

    /// <summary>A file naming the free-look row alone: the saved right button is the free look's, so
    /// the default rockets row loses that button rather than holding it at the same time, and the
    /// rockets keep their key and their pad button.</summary>
    [Fact]
    public void ASavedRow_TakesItsControlOffTheDefaultActionThatHeldIt()
    {
        var json = """
        {
          "version": 2,
          "player": 1,
          "contexts": { "flight": { "FreeLook": ["mouse/mouse:Right"] } }
        }
        """;
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);
        var right = new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Right));

        Assert.Equal(new[] { InputAction.FreeLook }, loaded.OwnersOf(right));
        Assert.Equal(
            new[]
            {
                new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.X)),
                new Binding(Pad, BindingControl.Button((int)JoyButton.A)),
            },
            loaded.Bindings(InputAction.FireRockets));
        Assert.Contains(
            new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Left)),
            loaded.Bindings(InputAction.FireGuns));
    }

    /// <summary>The claim is one action's over a default's, not over another saved row's: the four
    /// snap-look diagonals are one key the file names on two actions, and both keep it.</summary>
    [Fact]
    public void ASavedRow_TakesNothingOffAnotherRowTheSameFileNames()
    {
        var loaded = BindingStore.Deserialize(
            BindingStore.Serialize(1, BindingProfile.Defaults(Pad, readsKeyboard: true)),
            Pad,
            readsKeyboard: true).Map(InputContext.Flight);
        var kp7 = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Kp7));

        Assert.Equal(new[] { InputAction.LookUp, InputAction.LookLeft }, loaded.OwnersOf(kp7));
    }

    /// <summary>A hand-written file from a bumped version: the version says how the tokens are
    /// encoded, so a reader that understands the tokens it is given keeps them and every action the
    /// file does not name stays at its shipped default.</summary>
    [Fact]
    public void Load_FileFromABumpedVersion_KeepsTheRowsItCanRead()
    {
        var json = """
        {
          "version": 99,
          "player": 1,
          "contexts": {
            "flight": { "FireGuns": ["keyboard/key:Z", "pad:*/button:X"] },
            "somethingNew": { "Whatever": ["keyboard/key:A"] }
          }
        }
        """;
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            new[]
            {
                new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Z)),
                new Binding(Pad, BindingControl.Button((int)JoyButton.X)),
            },
            loaded.Bindings(InputAction.FireGuns));
        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.FireRockets),
            loaded.Bindings(InputAction.FireRockets));
    }

    [Fact]
    public void Load_UnknownActionName_LeavesEveryOtherActionAlone()
    {
        var json = Row("flight", "\"Teleport\": [\"keyboard/key:Z\"], \"Nitro\": [\"keyboard/key:Z\"]");
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Z)) },
            loaded.Bindings(InputAction.Nitro));
    }

    /// <summary>A hat row on the pad placeholder can only be a hand edit, and on a Godot pad it would
    /// alias a d-pad button binding. The action keeps its default instead.</summary>
    [Fact]
    public void Load_HatTokenOnThePadPlaceholder_LeavesTheActionAtItsDefault()
    {
        var json = Row("flight", "\"SelectGunGroup\": [\"pad:*/hat:0:Right\"]");
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.SelectGunGroup),
            loaded.Bindings(InputAction.SelectGunGroup));
    }

    /// <summary>One unreadable token costs the whole row, not part of it: half a keymap the player
    /// never chose is worse than the default they know.</summary>
    [Fact]
    public void Load_UnreadableToken_LeavesTheWholeRowAtItsDefault()
    {
        var json = Row("flight", "\"Nitro\": [\"keyboard/key:Z\", \"keyboard/key:Nonsense\"]");
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.Nitro),
            loaded.Bindings(InputAction.Nitro));
    }

    [Fact]
    public void Load_EmptyRow_IsADeliberateUnbind()
    {
        var loaded = BindingStore.Deserialize(Row("flight", "\"Nitro\": []"), Pad, readsKeyboard: true);

        Assert.Empty(loaded.Map(InputContext.Flight).Bindings(InputAction.Nitro));
    }

    [Fact]
    public void Load_MalformedJson_ReadsAsTheShippedDefaults()
    {
        var loaded = BindingStore.Deserialize("{ not json", Pad, readsKeyboard: true);

        AssertSameMaps(BindingProfile.Defaults(Pad, true), loaded);
    }

    /// <summary>A file saved on one machine and read where the pad is a different one: a row saved
    /// on the placeholder identity follows the seat, while a row naming a real pad keeps naming it.
    /// </summary>
    [Fact]
    public void Load_PlaceholderPadRow_FollowsTheSeatsPad()
    {
        var other = DeviceId.Joypad("some-other-pad");
        var json = Row("flight", "\"Nitro\": [\"pad:*/button:Y\", \"pad:some-other-pad/button:A\"]");
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            new[]
            {
                new Binding(Pad, BindingControl.Button((int)JoyButton.Y)),
                new Binding(other, BindingControl.Button((int)JoyButton.A)),
            },
            loaded.Bindings(InputAction.Nitro));
    }

    [Fact]
    public void Load_MissingFile_ReadsAsTheShippedDefaults()
    {
        var store = new BindingStore(TestData.TempDir());

        AssertSameMaps(BindingProfile.Defaults(Pad, true), store.Load(1, Pad, readsKeyboard: true));
    }

    /// <summary>Through the file itself, and per player: player two's save does not disturb player
    /// one's, which is the whole reason the file is named for the seat.</summary>
    [Fact]
    public void SaveAndLoad_KeepThePlayersApart()
    {
        var dir = TestData.TempDir();
        var store = new BindingStore(dir);
        var second = BindingProfile.Defaults(Pad, readsKeyboard: false);
        second.Map(InputContext.Flight).Clear(InputAction.Nitro);
        store.Save(1, BindingProfile.Defaults(Pad, readsKeyboard: true));
        store.Save(2, second);

        Assert.True(File.Exists(Path.Combine(dir, "bindings_p2.json")));
        Assert.NotEmpty(store.Load(1, Pad, true).Map(InputContext.Flight).Bindings(InputAction.Nitro));
        Assert.Empty(store.Load(2, Pad, false).Map(InputContext.Flight).Bindings(InputAction.Nitro));
    }

    /// <summary>The file is text a player can read and correct, which is the property the original's
    /// 2400 unversioned raw bytes do not have.</summary>
    [Fact]
    public void Serialize_NamesActionsAndControlsInWords()
    {
        var json = BindingStore.Serialize(1, BindingProfile.Defaults(Pad, readsKeyboard: true));

        Assert.Contains("\"version\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"FireGuns\"", json, StringComparison.Ordinal);
        Assert.Contains("keyboard/key:Space", json, StringComparison.Ordinal);
        Assert.Contains("keyboard/key:Shift+S", json, StringComparison.Ordinal);
        Assert.Contains("axis:TriggerRight+@0", json, StringComparison.Ordinal);
    }

    /// <summary>A modified key round-trips through its own token: the prefix is part of the control,
    /// so a file written before it existed and one written after it are both read as written.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesAModifiedKey()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var loaded = BindingStore.Deserialize(BindingStore.Serialize(1, profile), Pad, readsKeyboard: true)
            .Map(InputContext.Flight);

        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.E, KeyModifiers.Shift)) },
            loaded.Bindings(InputAction.TargetPreviousEnemy));
    }

    /// <summary>A version 1 file, which has no modifier prefix on any token, still loads whole: the
    /// reader checks no version and a bare key token means a bare key in either version. A player who
    /// last played before the modifiers existed keeps the keymap they chose.</summary>
    [Fact]
    public void Load_AVersionOneFile_KeepsEveryRowItNames()
    {
        var json = """
        {
          "version": 1,
          "player": 1,
          "contexts": {
            "flight": {
              "FireGuns": ["keyboard/key:Z"],
              "TargetPreviousEnemy": ["keyboard/key:Key5"],
              "ToggleSpyglass": ["keyboard/key:F2"]
            }
          }
        }
        """;
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Z)) },
            loaded.Bindings(InputAction.FireGuns));
        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Key5)) },
            loaded.Bindings(InputAction.TargetPreviousEnemy));
        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.F2)) },
            loaded.Bindings(InputAction.ToggleSpyglass));
    }

    /// <summary>An unreadable modifier name costs the row it stands on and nothing else, on the same
    /// rule as any other unreadable token.</summary>
    [Fact]
    public void Load_UnknownModifierName_LeavesTheWholeRowAtItsDefault()
    {
        var json = Row("flight", "\"ToggleSpyglass\": [\"keyboard/key:Hyper+S\"]");
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.ToggleSpyglass),
            loaded.Bindings(InputAction.ToggleSpyglass));
    }

    /// <summary>A saved keymap is written atomically, so a run killed mid-write leaves the previous
    /// save rather than a half-written one.</summary>
    [Fact]
    public void Load_LeftoverTempFile_IsIgnored()
    {
        var dir = TestData.TempDir();
        var store = new BindingStore(dir);
        store.Save(1, BindingProfile.Defaults(Pad, readsKeyboard: true));
        File.WriteAllText(Path.Combine(dir, "bindings_p1.json.tmp"), "{ half-writ", new UTF8Encoding(false));

        AssertSameMaps(BindingProfile.Defaults(Pad, true), store.Load(1, Pad, true));
    }

    /// <summary>A full axis is one binding, written once under its pair's positive row with its
    /// deadzone exactly as held, and read back onto both rows.</summary>
    [Fact]
    public void RoundTrip_WritesAFullAxisOnceAndKeepsItsDeadzoneExactly()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var full = new Binding(Stick, BindingControl.FullAxis(1, inverted: true, 0.0125f));
        profile.Map(InputContext.Flight).Assign(InputAction.PitchDown, full);

        var json = BindingStore.Serialize(1, profile);
        var loaded = BindingStore.Deserialize(json, Pad, readsKeyboard: true);
        var map = loaded.Map(InputContext.Flight);

        const string token = "pad:" + StickId + "/fullaxis:1-@0.0125";
        Assert.Equal(json.IndexOf(token, StringComparison.Ordinal), json.LastIndexOf(token, StringComparison.Ordinal));
        Assert.Contains(full, map.Bindings(InputAction.PitchUp));
        Assert.Contains(full, map.Bindings(InputAction.PitchDown));
        AssertSameMaps(profile, loaded);
        Assert.Equal(json, BindingStore.Serialize(1, loaded));
    }

    /// <summary>A deadzone a player types into the file is honoured anywhere in 0..0.95 and written
    /// back as typed. Past that the row is unreadable and keeps its default.</summary>
    [Theory]
    [InlineData("0", 0f)]
    [InlineData("0.3", 0.3f)]
    [InlineData("0.95", 0.95f)]
    [InlineData("0.96", null)]
    [InlineData("-0.1", null)]
    [InlineData("NaN", null)]
    public void Load_AHandEditedFullAxisDeadzone(string typed, float? expected)
    {
        var json = Row("flight", "\"RollRight\": [\"pad:" + StickId + "/fullaxis:0+@" + typed + "\"]");
        var map = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);

        if (expected is not { } deadzone)
        {
            Assert.Equal(
                DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.RollRight),
                map.Bindings(InputAction.RollRight));
            return;
        }

        var full = new Binding(Stick, BindingControl.FullAxis(0, inverted: false, deadzone));
        Assert.Equal(new[] { full }, map.Bindings(InputAction.RollRight));
        Assert.Contains(full, map.Bindings(InputAction.RollLeft));
        Assert.Contains("/fullaxis:0+@" + typed + "\"", BindingStore.Serialize(1, ProfileOf(json)), StringComparison.Ordinal);
    }

    /// <summary>A hand edit that names the full axis on the negative row, or on both, still binds
    /// the pair once.</summary>
    [Fact]
    public void Load_AFullAxisOnTheNegativeRow_BindsThePair()
    {
        var json = Row(
            "flight",
            "\"YawLeft\": [\"keyboard/key:Comma\", \"pad:" + StickId + "/fullaxis:#2+@0.02\"], "
            + "\"YawRight\": [\"keyboard/key:Period\"]");
        var map = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);
        var full = new Binding(Stick, BindingControl.FullAxis(2, inverted: false, 0.02f));

        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Comma)), full },
            map.Bindings(InputAction.YawLeft));
        Assert.Equal(
            new[] { new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Period)), full },
            map.Bindings(InputAction.YawRight));
    }

    /// <summary>A file naming only the positive row keeps the negative row's defaults and adds the
    /// full axis to it. The partner is not a default action that loses the control.</summary>
    [Fact]
    public void Load_AFullAxisOnOneNamedRow_KeepsThePartnersDefaults()
    {
        var json = Row("flight", "\"PitchUp\": [\"pad:" + StickId + "/fullaxis:1+@0.02\"]");
        var map = BindingStore.Deserialize(json, Pad, readsKeyboard: true).Map(InputContext.Flight);
        var full = new Binding(Stick, BindingControl.FullAxis(1, inverted: false, 0.02f));
        var defaults = DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(InputAction.PitchDown);

        Assert.Equal(new[] { full }, map.Bindings(InputAction.PitchUp));
        Assert.Equal(defaults.Append(full).ToArray(), map.Bindings(InputAction.PitchDown).ToArray());
    }

    [Theory]
    [InlineData("\"FireGuns\": [\"pad:" + StickId + "/fullaxis:1+@0\"]", InputAction.FireGuns)]
    [InlineData("\"PitchUp\": [\"keyboard/fullaxis:1+@0\"]", InputAction.PitchUp)]
    [InlineData("\"PitchUp\": [\"pad:" + StickId + "/fullaxis:1@0\"]", InputAction.PitchUp)]
    [InlineData("\"PitchUp\": [\"pad:" + StickId + "/fullaxis:1+\"]", InputAction.PitchUp)]
    [InlineData("\"FireGuns\": [\"keyboard/hat:0:Up\"]", InputAction.FireGuns)]
    [InlineData("\"FireGuns\": [\"pad:" + StickId + "/hat:0:Diagonal\"]", InputAction.FireGuns)]
    [InlineData("\"FireGuns\": [\"pad:" + StickId + "/hat:0:3\"]", InputAction.FireGuns)]
    public void Load_AMisplacedOrMalformedStickToken_LeavesTheRowAtItsDefault(string row, InputAction action)
    {
        var map = BindingStore.Deserialize(Row("flight", row), Pad, readsKeyboard: true).Map(InputContext.Flight);

        Assert.Equal(DefaultBindings.MapFor(InputContext.Flight, Pad).Bindings(action), map.Bindings(action));
    }

    [Fact]
    public void RoundTrip_PreservesAStickHatDirection()
    {
        var json = Row("flight", "\"SelectOrdnance\": [\"pad:" + StickId + "/hat:0:left\"]");
        var profile = ProfileOf(json);
        var hat = new Binding(Stick, BindingControl.Hat(0, HatDirection.Left));

        Assert.Equal(new[] { hat }, profile.Map(InputContext.Flight).Bindings(InputAction.SelectOrdnance));
        Assert.Contains("pad:" + StickId + "/hat:0:Left", BindingStore.Serialize(1, profile), StringComparison.Ordinal);
    }

    /// <summary>A stick's buttons and axes past the gamepad range are written as numbers, never as
    /// the engine enum's range sentinels, which name no control.</summary>
    [Fact]
    public void Encode_WritesStickControlsPastTheGamepadRangeAsNumbers()
    {
        int buttonSentinel = (int)JoyButton.SdlMax;
        int axisSentinel = (int)JoyAxis.SdlMax;
        Assert.Equal(
            "pad:" + StickId + "/button:#" + buttonSentinel,
            BindingStore.Encode(new Binding(Stick, BindingControl.Button(buttonSentinel))));
        Assert.Equal(
            "pad:" + StickId + "/button:#127",
            BindingStore.Encode(new Binding(Stick, BindingControl.Button(127))));
        Assert.Equal(
            "pad:" + StickId + "/axis:#" + axisSentinel + "+@0.5",
            BindingStore.Encode(new Binding(Stick, BindingControl.Axis(axisSentinel, 1, 0.5f))));
        Assert.Equal(
            new Binding(Stick, BindingControl.Button(buttonSentinel)),
            BindingStore.Decode("pad:" + StickId + "/button:SdlMax"));
    }

    private static BindingProfile ProfileOf(string json) => BindingStore.Deserialize(json, Pad, readsKeyboard: true);

    private static string Row(string context, string body) =>
        "{\"version\": 1, \"player\": 1, \"contexts\": {\"" + context + "\": {" + body + "}}}";

    // A whole file as the build before the mouse rows moved wrote one: every action of the context,
    // the free look on the right button, and no mouse row on either weapon.
    private static string SavedBeforeTheMove()
    {
        var profile = BindingProfile.Defaults(Pad, readsKeyboard: true);
        var map = profile.Map(InputContext.Flight);
        map.Unassign(
            InputAction.FireGuns, new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Left)));
        map.Unassign(
            InputAction.FireRockets, new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Right)));
        map.Clear(InputAction.FreeLook);
        map.Add(InputAction.FreeLook, new Binding(DeviceId.Mouse, BindingControl.Mouse((int)MouseButton.Right)));
        return BindingStore.Serialize(1, profile);
    }

    private static void AssertSameMaps(BindingProfile expected, BindingProfile actual)
    {
        foreach (var context in Enum.GetValues<InputContext>())
        {
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                Assert.Equal(
                    expected.Map(context).Bindings(action).ToArray(),
                    actual.Map(context).Bindings(action).ToArray());
            }
        }
    }
}
