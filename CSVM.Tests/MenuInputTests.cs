using System;
using System.Collections.Generic;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The d-pad shape driving every menu cursor, via <see cref="MenuInput.StepAxis"/> over a real
/// <see cref="HoldToRepeat"/>: a fresh press fires immediately, a held direction repeats after the
/// initial delay at the repeat interval, a direction flip fires immediately with a fresh delay,
/// releasing resets the delay, and idle input produces nothing. Then the typed-text half over
/// <see cref="MenuInput.TypedFrom"/>: the typeable set reaches past what a name box accepts, so a
/// refused character arrives at the box and the box cues its reject sound. The device reads
/// themselves are unreachable here; these are the two rules alone.
/// </summary>
public class MenuInputTests
{
    private const float Delay = 0.42f;
    private const float Interval = 0.12f;
    private const float Frame = 0.016f;

    [Fact]
    public void FreshPressFiresImmediately()
    {
        var repeat = new HoldToRepeat(Delay, Interval);
        int prev = 0;

        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, Frame));
        Assert.Equal(1, prev);
    }

    [Fact]
    public void HeldDirectionRepeatsAfterDelayAtInterval()
    {
        var repeat = new HoldToRepeat(Delay, Interval);
        int prev = 0;

        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, Frame));
        Assert.Equal(0, MenuInput.StepAxis(1, ref prev, repeat, 0.3f));   // inside the delay
        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, 0.12f));  // delay spent exactly
        Assert.Equal(0, MenuInput.StepAxis(1, ref prev, repeat, 0.05f));  // inside the interval
        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, 0.07f));  // interval elapsed
    }

    [Fact]
    public void DirectionFlipFiresImmediatelyWithAFreshDelay()
    {
        var repeat = new HoldToRepeat(Delay, Interval);
        int prev = 0;

        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, Frame));
        Assert.Equal(0, MenuInput.StepAxis(1, ref prev, repeat, 0.4f));   // most of the delay spent
        Assert.Equal(-1, MenuInput.StepAxis(-1, ref prev, repeat, Frame)); // the flip fires now
        Assert.Equal(0, MenuInput.StepAxis(-1, ref prev, repeat, 0.3f));  // and re-armed the FULL delay
        Assert.Equal(-1, MenuInput.StepAxis(-1, ref prev, repeat, 0.2f));
    }

    [Fact]
    public void ReleaseThenRepressWaitsTheFullDelayAgain()
    {
        var repeat = new HoldToRepeat(Delay, Interval);
        int prev = 0;

        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, Frame));
        Assert.Equal(0, MenuInput.StepAxis(1, ref prev, repeat, 0.4f));   // most of the delay spent
        Assert.Equal(0, MenuInput.StepAxis(0, ref prev, repeat, Frame));  // let go
        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, Frame));  // re-press fires
        Assert.Equal(0, MenuInput.StepAxis(1, ref prev, repeat, 0.3f));   // not a leftover 0.02s
        Assert.Equal(1, MenuInput.StepAxis(1, ref prev, repeat, 0.2f));
    }

    [Fact]
    public void IdleProducesNothing()
    {
        var repeat = new HoldToRepeat(Delay, Interval);
        int prev = 0;

        Assert.Equal(0, MenuInput.StepAxis(0, ref prev, repeat, 1f));
        Assert.Equal(0, MenuInput.StepAxis(0, ref prev, repeat, 1f));
        Assert.Equal(0, prev);
    }

    [Fact]
    public void APunctuationKeyTypesItsCharacterSoTheBoxHasSomethingToRefuse()
    {
        bool[] prev = FreshEdges();

        Assert.Equal("/", MenuInput.TypedFrom(k => k == Key.Slash, shift: false, prev));
        Assert.False(CampaignTextEntry.Accepts('/'));
        // Still held on the next frame, so it types once per press like every other key.
        Assert.Equal(string.Empty, MenuInput.TypedFrom(k => k == Key.Slash, shift: false, prev));
    }

    [Fact]
    public void EveryTypeableKeyHasItsOwnEdgeSlotAndItsOwnCharacter()
    {
        var seen = new HashSet<char>();
        foreach (var key in MenuInput.TypeableKeys)
        {
            string typed = MenuInput.TypedFrom(k => k == key, shift: false, FreshEdges());
            Assert.Single(typed);
            Assert.True(seen.Add(typed[0]), $"{key} typed a character another key already typed");
        }

        Assert.Equal(MenuInput.TypeableKeys.Count, seen.Count);
    }

    [Fact]
    public void TheTypeableSetCoversTheAcceptedNameCharactersAndReachesPastThem()
    {
        var typed = new HashSet<char>();
        foreach (var key in MenuInput.TypeableKeys)
        {
            typed.Add(MenuInput.TypedFrom(k => k == key, shift: false, FreshEdges())[0]);
            typed.Add(MenuInput.TypedFrom(k => k == key, shift: true, FreshEdges())[0]);
        }

        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 '`,-./;=[\\]")
            Assert.Contains(c, typed);

        Assert.Contains(typed, c => !CampaignTextEntry.Accepts(c));
    }

    [Fact]
    public void ShiftGivesADigitOrPunctuationKeyTheSymbolItPrints()
    {
        Assert.Equal("!", MenuInput.TypedFrom(k => k == Key.Key1, shift: true, FreshEdges()));
        Assert.Equal("1", MenuInput.TypedFrom(k => k == Key.Key1, shift: false, FreshEdges()));

        // The whole US-layout shifted row, in TypeableKeys order after the letters and the space.
        var shifted = new List<char>();
        foreach (var key in MenuInput.TypeableKeys)
        {
            if (key is >= Key.A and <= Key.Z || key == Key.Space)
                continue;
            shifted.Add(MenuInput.TypedFrom(k => k == key, shift: true, FreshEdges())[0]);
        }

        Assert.Equal(")!@#$%^&*(\"<_>?:+{|}~", new string(shifted.ToArray()));
    }

    [Fact]
    public void TheUnlockingPilotNameCanBeTypedKeyByKeyIntoTheNameBox()
    {
        var box = new CampaignTextEntry();
        bool[] prev = FreshEdges();
        foreach (char c in CampaignCheats.UnlockName)
        {
            (Key key, bool shift) = c == '!' ? (Key.Key1, true) : ((Key)char.ToUpperInvariant(c), false);
            box.Type(MenuInput.TypedFrom(k => k == key, shift, prev));
            MenuInput.TypedFrom(_ => false, shift: false, prev);
        }

        Assert.Equal(CampaignCheats.UnlockName, box.Text);
        Assert.True(CampaignCheats.IsUnlockName(box.Text));
    }

    [Fact]
    public void EdgeStateOfTheWrongLengthIsRefusedRatherThanReadingTheWrongKey()
    {
        bool[] tooShort = new bool[MenuInput.TypeableKeys.Count - 1];

        Assert.Throws<ArgumentException>(() => MenuInput.TypedFrom(_ => false, shift: false, tooShort));
    }

    // Nothing held, sized off the table so the parallel array cannot drift from it.
    private static bool[] FreshEdges() => new bool[MenuInput.TypeableKeys.Count];
}
