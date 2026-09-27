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
/// <see cref="HoldToRepeat"/>. A fresh press fires immediately, and a held direction repeats after
/// the initial delay at the repeat interval. A direction flip fires immediately with a fresh delay,
/// releasing resets the delay, and idle input produces nothing. Then the typed-text half over
/// <see cref="TypedText"/>. A key event's own character arrives whatever the layout, once per
/// reader, and a refused one still reaches the box for its reject sound. The device reads
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
    public void AColonFromAGermanLayoutArrivesAsTheColonTheLayoutTyped()
    {
        // Shift and the period key: a US key table reads that position as '>', the event says ':'.
        var feed = new TypedText();
        long mark = feed.Count;

        Assert.True(feed.Feed(pressed: true, echo: false, unicode: ':'));
        Assert.Equal(":", feed.Since(ref mark));
        Assert.Equal(string.Empty, feed.Since(ref mark));
    }

    [Fact]
    public void ReleasesEchoesAndControlCodesTypeNothing()
    {
        var feed = new TypedText();
        long mark = feed.Count;

        Assert.False(feed.Feed(pressed: false, echo: false, unicode: 'a'));
        Assert.False(feed.Feed(pressed: true, echo: true, unicode: 'a'));
        Assert.False(feed.Feed(pressed: true, echo: false, unicode: 0));
        Assert.False(feed.Feed(pressed: true, echo: false, unicode: '\r'));
        Assert.False(feed.Feed(pressed: true, echo: false, unicode: '\b'));
        Assert.False(feed.Feed(pressed: true, echo: false, unicode: 0x1F600));
        Assert.True(feed.Feed(pressed: true, echo: false, unicode: 0xE4));
        Assert.Equal("\u00e4", feed.Since(ref mark));
    }

    [Fact]
    public void EveryReaderSeesEachCharacterOnceWhateverTheOthersRead()
    {
        var feed = new TypedText();
        long first = feed.Count;
        long second = feed.Count;

        feed.Feed(true, false, 'a');
        Assert.Equal("a", feed.Since(ref first));
        feed.Feed(true, false, 'b');
        Assert.Equal("ab", feed.Since(ref second));
        Assert.Equal("b", feed.Since(ref first));
    }

    [Fact]
    public void AReaderFarBehindIsHandedOnlyTheNewestCharacters()
    {
        var feed = new TypedText();
        long mark = feed.Count;
        for (int i = 0; i < TypedText.Kept + 10; i++)
        {
            feed.Feed(true, false, i < 10 ? 'x' : 'y');
        }

        Assert.Equal(new string('y', TypedText.Kept), feed.Since(ref mark));
        Assert.Equal(feed.Count, mark);
    }

    [Fact]
    public void APasteReachesEveryReaderOnceAndTypesNoCharacter()
    {
        var feed = new TypedText();
        long first = feed.Pastes;
        long second = feed.Pastes;
        long typed = feed.Count;

        feed.FeedPaste();
        feed.FeedPaste();
        Assert.True(feed.PastedSince(ref first));
        Assert.False(feed.PastedSince(ref first));
        Assert.True(feed.PastedSince(ref second));
        Assert.Equal(string.Empty, feed.Since(ref typed));
    }

    [Fact]
    public void APunctuationCharacterArrivesSoTheBoxHasSomethingToRefuse()
    {
        var feed = new TypedText();
        long mark = feed.Count;
        feed.Feed(true, false, '/');

        var box = new CampaignTextEntry();
        Assert.False(box.Type(feed.Since(ref mark)));
        Assert.False(CampaignTextEntry.Accepts('/'));
    }

    [Fact]
    public void TheUnlockingPilotNameCanBeTypedIntoTheNameBox()
    {
        var feed = new TypedText();
        var box = new CampaignTextEntry();
        long mark = feed.Count;
        foreach (char c in CampaignCheats.UnlockName)
        {
            feed.Feed(true, false, c);
            box.Type(feed.Since(ref mark));
        }

        Assert.Equal(CampaignCheats.UnlockName, box.Text);
        Assert.True(CampaignCheats.IsUnlockName(box.Text));
    }

    [Fact]
    public void TheTypeableKeysAreTheLettersDigitsSpaceAndPunctuationTextEntryUnbinds()
    {
        Assert.Contains(Key.W, MenuInput.TypeableKeys);
        Assert.Contains(Key.Key1, MenuInput.TypeableKeys);
        Assert.Contains(Key.Space, MenuInput.TypeableKeys);
        Assert.Contains(Key.Period, MenuInput.TypeableKeys);
        Assert.DoesNotContain(Key.Enter, MenuInput.TypeableKeys);
        Assert.Equal(MenuInput.TypeableKeys.Count, new HashSet<Key>(MenuInput.TypeableKeys).Count);
    }
}
