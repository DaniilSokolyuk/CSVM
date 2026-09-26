using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The name field's contract: it holds only what the original's own rule admits (letters,
/// digits and spaces, 32 characters), the keyboard and the pad reach the same buffer, and nothing
/// it produces needs the profile store's directory-name sanitisation.</summary>
public class CampaignTextEntryTests
{
    [Fact]
    public void TypingKeepsOnlyWhatTheOriginalsRuleAdmits()
    {
        var entry = new CampaignTextEntry();

        entry.Type("Zach/ary*2");

        Assert.Equal("Zachary2", entry.Text);
    }

    [Fact]
    public void TypingStopsAtTheLengthCap()
    {
        var entry = new CampaignTextEntry();

        entry.Type(new string('A', CampaignTextEntry.MaxLength + 10));

        Assert.Equal(CampaignTextEntry.MaxLength, entry.Text.Length);
    }

    [Fact]
    public void SetFiltersAndTruncatesLikeTyping()
    {
        var entry = new CampaignTextEntry();

        entry.Set("a\\b" + new string('c', CampaignTextEntry.MaxLength));

        Assert.Equal(CampaignTextEntry.MaxLength, entry.Text.Length);
        Assert.DoesNotContain('\\', entry.Text);
    }

    /// <summary>The pad's whole alphabet: add a character, step it, and remove it again, which is
    /// what makes the screen reachable with no keyboard at all.</summary>
    [Fact]
    public void ThePadAppendsStepsAndRemovesCharacters()
    {
        var entry = new CampaignTextEntry();

        Assert.True(entry.Append());
        Assert.Equal("A", entry.Text);
        Assert.True(entry.StepLast(1));
        Assert.Equal("B", entry.Text);
        Assert.True(entry.StepLast(-1));
        Assert.Equal("A", entry.Text);
        Assert.True(entry.Backspace());
        Assert.Equal(string.Empty, entry.Text);
        Assert.False(entry.Backspace());
    }

    [Fact]
    public void SteppingWrapsAroundTheAlphabet()
    {
        var entry = new CampaignTextEntry();
        entry.Set("A");

        entry.StepLast(-1);

        Assert.Equal(" ", entry.Text);
    }

    [Fact]
    public void ValidRejectsEmptyOverlongAndIllegalNames()
    {
        Assert.True(CampaignTextEntry.Valid("Zachary 2"));
        Assert.False(CampaignTextEntry.Valid(string.Empty));
        Assert.False(CampaignTextEntry.Valid("con:flict"));
        Assert.False(CampaignTextEntry.Valid(new string('A', CampaignTextEntry.MaxLength + 1)));
    }

    [Fact]
    public void TheCaretShowsOnlyWhileArmed()
    {
        var entry = new CampaignTextEntry();
        entry.Set("Zachary");

        Assert.Equal("Zachary", entry.Display);
        entry.Arm();
        Assert.Equal("Zachary_", entry.Display);
        entry.Disarm();
        Assert.Equal("Zachary", entry.Display);
    }
}
