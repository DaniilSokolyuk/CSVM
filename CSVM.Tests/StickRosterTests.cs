using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="StickRoster"/> over a fake stick library: the gap-filler, hot-plug, the read gate,
/// normalisation, identical units merging, and the SDL2 load order. The models are the user's
/// hardware (VKB Gladiator EVO L and R, Razer Tartarus V2) and an Xbox pad Godot already reads.
/// </summary>
public class StickRosterTests
{
    private static readonly StickModel VkbL = new(0x231D, 0x0201);
    private static readonly StickModel VkbR = new(0x231D, 0x0200);
    private static readonly StickModel Tartarus = new(0x1532, 0x022B);
    private static readonly StickModel XboxPad = new(0x045E, 0x0B12);

    [Fact]
    public void GapFillDropsEveryModelGodotReadsAndKeepsTheRestInOrder()
    {
        var listed = new[]
        {
            new StickListing(1, "L", VkbL, "g1"),
            new StickListing(2, "Xbox", XboxPad, "g2"),
            new StickListing(3, "R", VkbR, "g3"),
        };

        var kept = StickRoster.GapFill(listed, new[] { XboxPad });

        Assert.Equal(new[] { 1, 3 }, Array.ConvertAll(kept, l => l.Instance));
    }

    [Fact]
    public void GapFillWithAnEmptyGodotRosterKeepsEverything()
        => Assert.Equal(2, StickRoster.GapFill(new[] { new StickListing(1, "L", VkbL, "g"), new StickListing(2, "R", VkbR, "g") }, Array.Empty<StickModel>()).Length);

    [Fact]
    public void FirstUpdateOpensEveryGapFillingDeviceAndNeverOpensAGodotPad()
    {
        var native = new FakeStickNative();
        native.Plug(1, "VKBsim Gladiator EVO L", VkbL, axes: 8, buttons: 128, hats: 1);
        native.Plug(2, "Xbox Wireless Controller", XboxPad, axes: 6, buttons: 11, hats: 1);
        native.Plug(3, "Joystick (Razer Tartarus V2)", Tartarus, axes: 6, buttons: 24, hats: 1);
        using var roster = new StickRoster(native, () => new[] { XboxPad }, () => false);

        Assert.True(roster.Update());

        Assert.Equal(new[] { VkbL, Tartarus }, roster.Sticks.Select(s => s.Model));
        Assert.DoesNotContain(2, native.Opened);
        Assert.Equal(8, roster.Sticks[0].Axes);
        Assert.Equal(128, roster.Sticks[0].Buttons);
    }

    [Fact]
    public void HotPlugAddsAndRemovesSticksOnTheNextUpdate()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        var godot = Array.Empty<StickModel>();
        using var roster = new StickRoster(native, () => godot, () => false);
        roster.Update();

        native.Plug(2, "R", VkbR);
        Assert.True(roster.Update());
        Assert.Equal(2, roster.Sticks.Count);

        native.Unplug(1);
        Assert.True(roster.Update());
        Assert.Equal(new[] { VkbR }, roster.Sticks.Select(s => s.Model));
        Assert.DoesNotContain(1, native.Opened);
    }

    [Fact]
    public void AQuietFrameWithAnUnchangedGodotRosterDoesNotRelist()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        var godot = Array.Empty<StickModel>();
        using var roster = new StickRoster(native, () => godot, () => false);
        roster.Update();
        int lists = native.ListCalls;

        Assert.False(roster.Update());
        Assert.Equal(lists, native.ListCalls);
    }

    [Fact]
    public void AStickGodotStartsReadingIsClosedAndComesBackWhenGodotLetsGo()
    {
        var native = new FakeStickNative();
        native.Plug(1, "R", VkbR);
        IReadOnlyCollection<StickModel> godot = Array.Empty<StickModel>();
        using var roster = new StickRoster(native, () => godot, () => false);
        roster.Update();

        godot = new[] { VkbR };
        Assert.True(roster.Update());
        Assert.Empty(roster.Sticks);
        Assert.DoesNotContain(1, native.Opened);

        godot = Array.Empty<StickModel>();
        Assert.True(roster.Update());
        Assert.Single(roster.Sticks);
    }

    [Fact]
    public void ReadsAreNeutralWhileBlockedButTheRosterStillFollowsPlugs()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        bool blocked = true;
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => blocked);
        roster.Update();
        native.Set(1, axis: 0, raw: 32767, button: 5, hat: 1);
        var stick = roster.Sticks[0];

        Assert.Equal(0f, roster.Axis(stick, 0));
        Assert.False(roster.Button(stick, 5));
        Assert.Equal(HatDirection.None, roster.Hat(stick, 0));

        native.Plug(2, "R", VkbR);
        Assert.True(roster.Update());
        Assert.Equal(2, roster.Sticks.Count);

        blocked = false;
        Assert.Equal(1f, roster.Axis(stick, 0));
        Assert.True(roster.Button(stick, 5));
        Assert.Equal(HatDirection.Up, roster.Hat(stick, 0));
    }

    [Theory]
    [InlineData((short)32767, 1f)]
    [InlineData((short)-32768, -1f)]
    [InlineData((short)-32767, -1f)]
    [InlineData((short)0, 0f)]
    public void AxesNormaliseToPlusMinusOne(short raw, float expected)
        => Assert.Equal(expected, StickRoster.Normalise(raw));

    [Fact]
    public void ReadsPastACountOrPastTheButtonCapAreNeutral()
    {
        var native = new FakeStickNative();
        native.Plug(1, "Big", VkbL, axes: 2, buttons: 200, hats: 0);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        var stick = roster.Sticks[0];
        native.Set(1, axis: 1, raw: 1000, button: 127, hat: 0);
        native.Set(1, axis: 1, raw: 1000, button: 128, hat: 0);

        Assert.True(roster.Button(stick, 127));
        Assert.False(roster.Button(stick, 128));
        Assert.Equal(0f, roster.Axis(stick, 2));
        Assert.Equal(HatDirection.None, roster.Hat(stick, 0));
    }

    [Fact]
    public void AnUnpluggedStickReadsNeutralThroughAStaleReference()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        var stale = roster.Sticks[0];
        native.Set(1, axis: 0, raw: 32767, button: 0, hat: 0);

        native.Unplug(1);
        roster.Update();

        Assert.Equal(0f, roster.Axis(stale, 0));
    }

    [Fact]
    public void IdenticalUnitsOfOneModelMergeWhileLAndRStayApart()
    {
        var native = new FakeStickNative();
        native.Plug(1, "R", VkbR);
        native.Plug(2, "R", VkbR);
        native.Plug(3, "L", VkbL);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        native.Set(1, axis: 0, raw: 8000, button: 3, hat: 0);
        native.Set(2, axis: 0, raw: -20000, button: 9, hat: 4);

        Assert.Equal(-20000 / 32767f, roster.ModelAxis(VkbR, 0));
        Assert.True(roster.ModelButton(VkbR, 3));
        Assert.True(roster.ModelButton(VkbR, 9));
        Assert.Equal(HatDirection.Down, roster.ModelHat(VkbR, 0));
        Assert.False(roster.ModelButton(VkbL, 3));
        Assert.Equal(0f, roster.ModelAxis(VkbL, 0));
    }

    /// <summary>Both VKB grips read their twist, axis 5, negated, and every other axis as it comes.
    /// Another model's axis 5 is not touched.</summary>
    [Fact]
    public void TheVkbTwistReadsFlippedAndNothingElseDoes()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        native.Plug(2, "R", VkbR);
        native.Plug(3, "Tartarus", Tartarus, axes: 6);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();
        foreach (int instance in new[] { 1, 2, 3 })
        {
            for (int a = 0; a < 6; a++)
            {
                native.SetAxis(instance, a, 16384);
            }
        }

        float half = 16384 / 32767f;
        foreach (var stick in roster.Sticks)
        {
            for (int a = 0; a < 6; a++)
            {
                bool flipped = a == 5 && stick.Model != Tartarus;
                Assert.Equal(flipped ? -half : half, roster.Axis(stick, a));
                Assert.Equal(flipped, StickQuirks.Flips(stick.Model, a));
            }
        }

        Assert.Equal(-half, roster.ModelAxis(VkbR, 5));
        Assert.Equal(" quirks=[axis 5 flipped]", StickRoster.QuirksText(VkbR));
        Assert.Equal(string.Empty, StickRoster.QuirksText(Tartarus));
    }

    /// <summary>The rest sample goes through the same correction: a flipped twist resting at raw -0.5
    /// is sampled at +0.5. The shape test's axes 0 and 1 are as read.</summary>
    [Fact]
    public void TheRestSampleIsCorrectedToo()
    {
        var native = new FakeStickNative();
        native.Plug(1, "R", VkbR);
        native.SetAxis(1, 1, 300);
        native.SetAxis(1, 5, -16384);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        for (int i = 0; i <= StickRoster.SettleUpdates; i++)
        {
            roster.Update();
        }

        var rest = roster.RestingAxes(roster.Sticks[0])!;
        Assert.Equal(16384 / 32767f, rest[5]);
        Assert.Equal(300 / 32767f, rest[1]);
    }

    [Fact]
    public void AFailedOpenLeavesTheDeviceOutAndTheRosterRunning()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        native.Refuse.Add(1);
        using var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);

        Assert.False(roster.Update());
        Assert.Empty(roster.Sticks);
    }

    [Fact]
    public void DisposeClosesEveryStickAndTheLibrary()
    {
        var native = new FakeStickNative();
        native.Plug(1, "L", VkbL);
        var roster = new StickRoster(native, Array.Empty<StickModel>, () => false);
        roster.Update();

        roster.Dispose();

        Assert.Empty(native.Opened);
        Assert.True(native.Disposed);
    }

    [Theory]
    [InlineData("231D/0201", 0x231D, 0x0201)]
    [InlineData("1532/022b", 0x1532, 0x022B)]
    public void ModelsParseTheirPrintedForm(string text, int vendor, int product)
    {
        Assert.True(StickModel.TryParse(text, out var model));
        Assert.Equal(new StickModel((ushort)vendor, (ushort)product), model);
        Assert.Equal(text.ToUpperInvariant(), model.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("231D")]
    [InlineData("231D/201")]
    [InlineData("XYZW/0201")]
    public void MalformedModelsAreRefused(string? text) => Assert.False(StickModel.TryParse(text, out _));

    [Fact]
    public void GodotsDecimalIdsBecomeAModel()
    {
        Assert.True(StickModel.TryFromDecimal("8989", "513", out var model));
        Assert.Equal(VkbL, model);
        Assert.False(StickModel.TryFromDecimal("", "513", out _));
        Assert.False(StickModel.TryFromDecimal("70000", "513", out _));
    }

    [Fact]
    public void TheDllIsLookedForBesideTheExeThenRepoThenDataRootThenTheGodotCheckout()
    {
        // The fourth rule lands on the repo's own tools/sdl2 here, so it is dropped as a duplicate.
        string root = Path.Combine(Path.GetTempPath(), "csvm-sdl2-order");
        string repo = Path.Combine(root, "repo");
        string exe = Path.Combine(repo, "tools", "godot", "build");
        var paths = Sdl2Sticks.Candidates(exe, repo, Path.Combine(root, "data"));

        Assert.Equal(
            new[]
            {
                Path.Combine(exe, "SDL2.dll"),
                Path.Combine(repo, "tools", "sdl2", "SDL2.dll"),
                Path.Combine(root, "data", "tools", "sdl2", "SDL2.dll"),
            },
            paths);
    }

    [Fact]
    public void AWorktreeWithoutADataRootFallsBackToTheCheckoutSupplyingGodot()
    {
        string root = Path.Combine(Path.GetTempPath(), "csvm-sdl2-worktree");
        string exe = Path.Combine(root, "main", "tools", "godot", "build");
        var paths = Sdl2Sticks.Candidates(exe, Path.Combine(root, "worktree"), null);

        Assert.Equal(Path.Combine(root, "main", "tools", "sdl2", "SDL2.dll"), paths[^1]);
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public void AnExportedBuildSkipsTheRepoRootAndNoCandidateMeansNoLibrary()
    {
        string exe = Path.Combine(Path.GetTempPath(), "csvm-no-sdl2-here", "bin");
        var paths = Sdl2Sticks.Candidates(exe, null, null);

        Assert.Equal(2, paths.Count);
        Assert.Null(Sdl2Sticks.Load(paths, out string outcome));
        Assert.Contains("no SDL2.dll", outcome, StringComparison.Ordinal);
        Assert.Contains(paths[1], outcome, StringComparison.Ordinal);
    }
}
