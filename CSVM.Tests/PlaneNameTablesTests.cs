using System;
using System.Collections.Generic;
using System.IO;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The word lists the PLANENAME screen rolls names from: that every one of the names they spell
/// fits the record's cap and the screen's own character set, that the roll is a function of its
/// seed, and that it skips names the store already holds.
/// </summary>
public class PlaneNameTablesTests
{
    /// <summary>Every name the two lists can spell fits the original's 32-character field and uses
    /// only characters the screen would accept typed. Asserted over the whole cross product, not a
    /// sample: the name is the plane's identity, so one long word is a corrupt save, not a
    /// cosmetic slip.</summary>
    [Fact]
    public void EveryComposedNameFitsTheRecordAndTheAlphabet()
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        for (int a = 0; a < PlaneNameTables.Adjectives.Length; a++)
        {
            for (int n = 0; n < PlaneNameTables.Nouns.Length; n++)
            {
                string name = PlaneNameTables.Compose(a, n);
                Assert.InRange(name.Length, 1, HangarNamePage.MaxLength);
                foreach (char c in name)
                {
                    Assert.True(HangarNamePage.Accepts(c), $"'{c}' in \"{name}\" cannot be typed");
                    Assert.DoesNotContain(c, invalid);
                }
            }
        }
    }

    /// <summary>Neither list repeats a word, so the reroll's range is what the counts say it is.
    /// </summary>
    [Fact]
    public void NeitherListRepeatsAWord()
    {
        Assert.Equal(PlaneNameTables.Adjectives.Length, new HashSet<string>(PlaneNameTables.Adjectives).Count);
        Assert.Equal(PlaneNameTables.Nouns.Length, new HashSet<string>(PlaneNameTables.Nouns).Count);
        Assert.Equal(PlaneNameTables.Adjectives.Length * PlaneNameTables.Nouns.Length, PlaneNameTables.Count);
    }

    /// <summary>A pinned seed rolls a pinned name, which is what makes a --det hangar screenshot
    /// reproducible.</summary>
    [Fact]
    public void TheRollIsAFunctionOfItsSeed()
    {
        var first = PlaneNameTables.Roll(new Random(7));
        var again = PlaneNameTables.Roll(new Random(7));

        Assert.Equal(first, again);
        Assert.NotEqual(first, PlaneNameTables.Roll(new Random(8)));
    }

    /// <summary>A name the store already holds is skipped: the generator handing over a name that
    /// would eat a saved plane is the one failure the pilot did not choose.</summary>
    [Fact]
    public void TheRollSkipsATakenName()
    {
        var free = PlaneNameTables.Roll(new Random(7));
        string taken = PlaneNameTables.Compose(free.Adjective, free.Noun);

        var rolled = PlaneNameTables.Roll(new Random(7), name => name == taken);

        Assert.NotEqual(taken, PlaneNameTables.Compose(rolled.Adjective, rolled.Noun));
    }

    /// <summary>With every name taken the draw still stands rather than looping: the screen's own
    /// overwrite warning is what covers the pilot from there.</summary>
    [Fact]
    public void AFullStoreStillYieldsAName()
    {
        var rolled = PlaneNameTables.Roll(new Random(7), _ => true);

        Assert.Equal(PlaneNameTables.Roll(new Random(7)), rolled);
    }

    /// <summary>Composing wraps, so a stepper can hand over its own running index.</summary>
    [Fact]
    public void ComposingWrapsBothIndices()
    {
        Assert.Equal(
            PlaneNameTables.Compose(0, 0),
            PlaneNameTables.Compose(PlaneNameTables.Adjectives.Length, PlaneNameTables.Nouns.Length));
        Assert.Equal(
            PlaneNameTables.Compose(PlaneNameTables.Adjectives.Length - 1, PlaneNameTables.Nouns.Length - 1),
            PlaneNameTables.Compose(-1, -1));
    }
}
