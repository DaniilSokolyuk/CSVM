using System;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Bindings;
using CSVM.UI.Screens;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>The launch-time read and the gate over it: a stored profile reaches a seat, a
/// deterministic run cannot see the same file, and a file this build cannot make sense of costs the
/// player nothing but the rows it could not read.
/// ⚠ The gate and the store's directory are process-wide statics. Every fact here sets both and
/// restores them through <see cref="Dispose"/>, and nothing outside this class touches either, so
/// the file this suite reads is never the one saved at this machine's controls.</summary>
public sealed class LaunchBindingsTests : IDisposable
{
    private static readonly Binding Zed = new(DeviceId.Keyboard, BindingControl.Key((int)Key.Z));

    private readonly string _dir = TestData.TempDir();
    private readonly string? _previousDir = BindingStore.DirectoryOverride;

    public LaunchBindingsTests() => BindingStore.DirectoryOverride = _dir;

    public void Dispose()
    {
        BindingStore.DirectoryOverride = _previousDir;
        LaunchBindings.Configure(deterministic: true);
    }

    /// <summary>The headline: a control the player rebound and saved is the control the flight seat
    /// is handed when it is built.</summary>
    [Fact]
    public void SavedProfile_ReachesAFlightSeatAtLaunch()
    {
        Save(1, Rebound(InputContext.Flight, InputAction.Nitro));
        LaunchBindings.Configure(deterministic: false);

        var map = LaunchBindings.Map(1, InputContext.Flight, default, readsKeyboard: true);

        Assert.Equal(new[] { Zed }, map.Bindings(InputAction.Nitro));
        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, default).Bindings(InputAction.FireGuns),
            map.Bindings(InputAction.FireGuns));
    }

    /// <summary>DET-8. The file exists, it genuinely differs from the shipped set, and a
    /// deterministic run reads none of it: without the gate this fact fails on the first
    /// assertion.</summary>
    [Fact]
    public void DeterministicRun_IgnoresAStoredProfileThatDiffersFromTheDefaults()
    {
        Save(1, Rebound(InputContext.Flight, InputAction.Nitro));
        LaunchBindings.Configure(deterministic: true);

        var profile = LaunchBindings.Profile(1, default, readsKeyboard: true);

        Assert.DoesNotContain(Zed, profile.Map(InputContext.Flight).Bindings(InputAction.Nitro));
        Assert.False(LaunchBindings.ReadsSaved);
        AssertSameMaps(BindingProfile.Defaults(default, true), profile);
    }

    /// <summary>The same file, read once with the gate open and once with it shut, so the two
    /// answers are compared rather than each judged alone.</summary>
    [Fact]
    public void TheGateIsTheOnlyDifferenceBetweenTheTwoReads()
    {
        Save(1, Rebound(InputContext.Flight, InputAction.Nitro));

        LaunchBindings.Configure(deterministic: false);
        var open = LaunchBindings.Map(1, InputContext.Flight, default, true).Bindings(InputAction.Nitro);
        LaunchBindings.Configure(deterministic: true);
        var shut = LaunchBindings.Map(1, InputContext.Flight, default, true).Bindings(InputAction.Nitro);

        Assert.Equal(new[] { Zed }, open);
        Assert.NotEqual(open, shut);
    }

    /// <summary>A file that is not JSON at all, which is what a half-written or truncated save looks
    /// like: the seat launches on the shipped defaults and nothing throws.</summary>
    [Fact]
    public void AFileThatIsNotJson_LaunchesOnTheShippedDefaults()
    {
        Save(1, "{ \"version\": 1, \"contexts\": { \"flight\": { \"Nitro\": [\"keyb");
        LaunchBindings.Configure(deterministic: false);

        var profile = LaunchBindings.Profile(1, default, readsKeyboard: true);

        AssertSameMaps(BindingProfile.Defaults(default, true), profile);
    }

    /// <summary>A valid file naming an action this build no longer has: that row is dropped, every
    /// other row the file carries is kept, and every action it does not name stays at its shipped
    /// default.</summary>
    [Fact]
    public void AFileNamingAnActionThisBuildDropped_CostsThatRowAndNothingElse()
    {
        Save(1, """
        {
          "version": 1,
          "player": 1,
          "contexts": {
            "flight": { "DeployAirbrake": ["keyboard/key:B"], "Nitro": ["keyboard/key:Z"] }
          }
        }
        """);
        LaunchBindings.Configure(deterministic: false);

        var map = LaunchBindings.Map(1, InputContext.Flight, default, readsKeyboard: true);

        Assert.Equal(new[] { Zed }, map.Bindings(InputAction.Nitro));
        Assert.Equal(
            DefaultBindings.MapFor(InputContext.Flight, default).Bindings(InputAction.FireGuns),
            map.Bindings(InputAction.FireGuns));
    }

    /// <summary>A fire action bound to nothing is the player's own unbind and survives the reload,
    /// which is C21's rule. What must not follow from it is an unflyable seat, so the rest of the
    /// keymap is asserted intact beside it.</summary>
    [Fact]
    public void AFireActionBoundToNothing_LoadsUnboundAndLeavesTheSeatFlyable()
    {
        var profile = BindingProfile.Defaults(default, readsKeyboard: true);
        profile.Map(InputContext.Flight).Clear(InputAction.FireGuns);
        Save(1, BindingStore.Serialize(1, profile));
        LaunchBindings.Configure(deterministic: false);

        var map = LaunchBindings.Map(1, InputContext.Flight, default, readsKeyboard: true);

        Assert.Empty(map.Bindings(InputAction.FireGuns));
        Assert.NotEmpty(map.Bindings(InputAction.FireRockets));
        Assert.NotEmpty(map.Bindings(InputAction.PitchUp));
        Assert.NotEmpty(map.Bindings(InputAction.ThrottleUp));
    }

    /// <summary>Splitscreen: seat two's load reads seat two's file and leaves seat one's keymap
    /// exactly where it was.</summary>
    [Fact]
    public void PerPlayer_LoadingSeatTwoDoesNotTouchSeatOne()
    {
        Save(1, Rebound(InputContext.Flight, InputAction.Nitro));
        Save(2, Rebound(InputContext.Flight, InputAction.FireRockets));
        LaunchBindings.Configure(deterministic: false);

        var one = LaunchBindings.Map(1, InputContext.Flight, default, true);
        var two = LaunchBindings.Map(2, InputContext.Flight, default, false);

        Assert.Equal(new[] { Zed }, one.Bindings(InputAction.Nitro));
        Assert.Equal(new[] { Zed }, two.Bindings(InputAction.FireRockets));
        Assert.DoesNotContain(Zed, one.Bindings(InputAction.FireRockets));
        Assert.DoesNotContain(Zed, two.Bindings(InputAction.Nitro));
    }

    /// <summary>The menu poller reads the map object it was constructed with, so the load has to
    /// fill that object rather than hand back a new one.</summary>
    [Fact]
    public void MenuInput_LoadsIntoTheVeryMapItsReadersHold()
    {
        Save(1, Rebound(InputContext.Menu, InputAction.MenuLoadout));
        LaunchBindings.Configure(deterministic: false);
        var input = new MenuInput { Keyboard = true };
        var before = input.Map;

        input.LoadSavedKeymap(1);

        Assert.Same(before, input.Map);
        Assert.Equal(new[] { Zed }, input.Map.Bindings(InputAction.MenuLoadout));
    }

    /// <summary>The fill replaces rather than merges: a binding the seat held and the file does not
    /// is gone afterwards, which is what makes a saved unbind stick.</summary>
    [Fact]
    public void Fill_ReplacesTheMapRatherThanMergingIntoIt()
    {
        var live = DefaultBindings.MapFor(InputContext.Flight, default);
        live.Add(InputAction.Nitro, Zed);
        var source = DefaultBindings.MapFor(InputContext.Flight, default);
        source.Clear(InputAction.FireGuns);

        live.Fill(source);

        Assert.DoesNotContain(Zed, live.Bindings(InputAction.Nitro));
        Assert.Empty(live.Bindings(InputAction.FireGuns));
    }

    /// <summary>The fill adds rather than assigns, so the shipped set's deliberate sharing survives
    /// it: a steal on the way in would take each numpad diagonal off one of its two actions.
    /// </summary>
    [Fact]
    public void Fill_KeepsAControlThatDrivesTwoActions()
    {
        var live = new ActionMap();
        var kp7 = new Binding(DeviceId.Keyboard, BindingControl.Key((int)Key.Kp7));

        live.Fill(DefaultBindings.MapFor(InputContext.Flight, default));

        Assert.Contains(kp7, live.Bindings(InputAction.LookUp));
        Assert.Contains(kp7, live.Bindings(InputAction.LookLeft));
    }

    private static string Rebound(InputContext context, InputAction action)
    {
        var profile = BindingProfile.Defaults(default, readsKeyboard: true);
        var map = profile.Map(context);
        map.Clear(action);
        map.Add(action, Zed);
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

    private void Save(int player, string json) =>
        File.WriteAllText(Path.Combine(_dir, BindingStore.FileNameFor(player)), json, new UTF8Encoding(false));
}
