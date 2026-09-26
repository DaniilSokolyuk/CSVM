using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The PLANENAME screen (BL-441): the name rolled onto a plane that has none, the two word
/// steppers and the reroll that a pad names a plane with, the typing that replaces them, and the
/// warning that stands between a stepped name and somebody else's saved plane.
/// </summary>
public class HangarNamePageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarNamePageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-name-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The name, its two words, and the reroll: four rows whose indices are fixed, so the
    /// cursor never lands on a row that came and went.</summary>
    [Fact]
    public void FourRowsTheNameItsTwoWordsAndTheReroll()
    {
        var flow = OpenOnName();

        Assert.Equal(4, flow.Page.RowCount);
        Assert.StartsWith("Name: ", flow.Page.RowText(HangarNamePage.NameRow), StringComparison.Ordinal);
        Assert.StartsWith("Adjective: ", flow.Page.RowText(HangarNamePage.AdjectiveRow), StringComparison.Ordinal);
        Assert.StartsWith("Noun: ", flow.Page.RowText(HangarNamePage.NounRow), StringComparison.Ordinal);
        Assert.Equal("Roll a new name", flow.Page.RowText(HangarNamePage.RerollRow));
    }

    /// <summary>A plane with no name arrives already named, which is the whole point: a pad reaches
    /// the build press without entering anything.</summary>
    [Fact]
    public void ANamelessPlaneArrivesAlreadyNamed()
    {
        var flow = OpenOnName();

        Assert.False(string.IsNullOrWhiteSpace(flow.Scratch.Name));
        Assert.Equal(2, flow.Scratch.Name.Split(' ').Length);
        Assert.Contains(flow.Scratch.Name.Split(' ')[0], PlaneNameTables.Adjectives);
        Assert.Contains(flow.Scratch.Name.Split(' ')[1], PlaneNameTables.Nouns);
    }

    /// <summary>The pinned seed rolls the name the tables' own roll rolls, so the screen offers what
    /// a --det run says it offers.</summary>
    [Fact]
    public void TheRolledNameIsTheSeedsName()
    {
        var flow = OpenOnName(rng: new Random(7));
        var expected = PlaneNameTables.Roll(new Random(7));

        Assert.Equal(PlaneNameTables.Compose(expected.Adjective, expected.Noun), flow.Scratch.Name);
    }

    /// <summary>A plane that already carries a name keeps it. Arriving on this screen must never
    /// cost a pilot the name they chose, and an imported original save carries one this screen
    /// never composed.</summary>
    [Fact]
    public void AnExistingNameSurvivesArrival()
    {
        _store.Save(new CustomPlaneDef { Name = "Blue Streak", Engine = 0 });
        var flow = OpenOnNameEditing("Blue Streak");

        Assert.Equal("Blue Streak", flow.Scratch.Name);
        Assert.True(((HangarNamePage)flow.Page).Freeform);
    }

    /// <summary>Each stepper walks its own word and wraps, rebuilding the name from both.</summary>
    [Fact]
    public void EachStepperWalksItsOwnWord()
    {
        var flow = OpenOnName();
        string firstNoun = flow.Scratch.Name.Split(' ')[1];

        Assert.True(flow.Page.Step(HangarNamePage.AdjectiveRow, 1));
        Assert.Equal(firstNoun, flow.Scratch.Name.Split(' ')[1]);

        string adjective = flow.Scratch.Name.Split(' ')[0];
        Assert.True(flow.Page.Step(HangarNamePage.NounRow, 1));
        Assert.Equal(adjective, flow.Scratch.Name.Split(' ')[0]);
        Assert.NotEqual(firstNoun, flow.Scratch.Name.Split(' ')[1]);
    }

    /// <summary>Only the two word rows step. The name row is the typing target and the reroll is a
    /// confirm, so a stray ←→ on either does nothing at all.</summary>
    [Fact]
    public void OnlyTheWordRowsStep()
    {
        var flow = OpenOnName();

        Assert.False(flow.Page.Step(HangarNamePage.NameRow, 1));
        Assert.False(flow.Page.Step(HangarNamePage.RerollRow, -1));
        Assert.False(flow.Page.Step(HangarNamePage.AdjectiveRow, 0));
    }

    /// <summary>Confirm on the reroll row rolls both words and stays on the screen; confirm
    /// anywhere else is the flow's own advance, so the row a pilot finishes on is the one that
    /// moves them on.</summary>
    [Fact]
    public void ConfirmRollsOnTheRerollRowAndAdvancesElsewhere()
    {
        var flow = OpenOnName();
        flow.FocusRow(HangarNamePage.RerollRow);
        string before = flow.Scratch.Name;

        Assert.True(flow.Accept());
        Assert.Equal(HangarScreen.Name, flow.Screen);
        Assert.NotEqual(before, flow.Scratch.Name);

        flow.FocusRow(HangarNamePage.NameRow);
        flow.Accept();
        Assert.Equal(HangarScreen.Purchase, flow.Screen);
    }

    /// <summary>Typing replaces the rolled name rather than extending it: a pilot who types at all
    /// wants a different name, not that one with a letter on the end.</summary>
    [Fact]
    public void TypingReplacesTheRolledName()
    {
        var flow = OpenOnName();
        var page = (HangarNamePage)flow.Page;

        Assert.True(page.Type('G'));
        Assert.Equal("G", flow.Scratch.Name);
        Assert.True(page.Type('o'));
        Assert.True(page.Type('\''));
        Assert.Equal("Go'", flow.Scratch.Name);
        Assert.True(page.Freeform);
    }

    /// <summary>Backspace enters freeform in place instead of clearing, which is the way in for
    /// editing a rolled name rather than starting over.</summary>
    [Fact]
    public void BackspaceEditsTheRolledNameInPlace()
    {
        var flow = OpenOnName();
        var page = (HangarNamePage)flow.Page;
        string rolled = flow.Scratch.Name;

        Assert.True(page.Backspace());
        Assert.Equal(rolled[..^1], flow.Scratch.Name);
        Assert.True(page.Freeform);
    }

    /// <summary>Typing stops at the record's cap, and refuses a character the name cannot carry.
    /// </summary>
    [Fact]
    public void TypingStopsAtTheCapAndFiltersTheKeystroke()
    {
        var flow = OpenOnName();
        var page = (HangarNamePage)flow.Page;

        Assert.False(page.Type('/'));
        Assert.False(page.Type('\n'));

        page.Type('A');
        while (flow.Scratch.Name.Length < HangarNamePage.MaxLength)
        {
            Assert.True(page.Type('A'));
        }

        Assert.False(page.Type('A'));
        Assert.Equal(HangarNamePage.MaxLength, flow.Scratch.Name.Length);
    }

    /// <summary>Stepping a word throws a typed name away, with the stepper's own detail line the
    /// only notice given. There is no undo behind it, which is why the notice is there.</summary>
    [Fact]
    public void SteppingAWordDiscardsATypedName()
    {
        var flow = OpenOnName();
        var page = (HangarNamePage)flow.Page;
        page.Type('Q');

        Assert.Contains("Step to drop the typed name", page.Detail(HangarNamePage.AdjectiveRow), StringComparison.Ordinal);
        Assert.True(page.Step(HangarNamePage.AdjectiveRow, 1));
        Assert.False(page.Freeform);
        Assert.DoesNotContain('Q', flow.Scratch.Name);
    }

    /// <summary>The roll never offers a name the store already holds: a machine that hands a pilot
    /// a name which eats their saved plane is a fault of the machine.</summary>
    [Fact]
    public void TheRollNeverOffersASavedPlanesName()
    {
        var first = PlaneNameTables.Roll(new Random(7));
        _store.Save(new CustomPlaneDef { Name = PlaneNameTables.Compose(first.Adjective, first.Noun), Engine = 0 });

        var flow = OpenOnName(rng: new Random(7));

        Assert.NotEqual(PlaneNameTables.Compose(first.Adjective, first.Noun), flow.Scratch.Name);
        Assert.Equal(string.Empty, flow.Page.Detail(HangarNamePage.NameRow));
    }

    /// <summary>A name landing on a saved plane's says so, since the store's save is a silent
    /// overwrite and the steppers do not check the way the roll does.</summary>
    [Fact]
    public void ANameLandingOnASavedPlaneWarns()
    {
        _store.Save(new CustomPlaneDef { Name = "Blue Streak", Engine = 0 });
        var flow = OpenOnName();
        var page = (HangarNamePage)flow.Page;
        foreach (char c in "Blue Streak")
        {
            page.Type(c);
        }

        Assert.Contains("replaces your saved", page.Detail(HangarNamePage.NameRow), StringComparison.Ordinal);
    }

    /// <summary>The plane being edited is exempt from that warning: writing back over its own file
    /// is what editing a saved plane is, and a warning that fires every time is one nobody reads by
    /// the time it means something.</summary>
    [Fact]
    public void EditingASavedPlaneDoesNotWarnAboutItsOwnFile()
    {
        _store.Save(new CustomPlaneDef { Name = "Blue Streak", Engine = 0 });
        var flow = OpenOnNameEditing("Blue Streak");

        Assert.Equal(string.Empty, flow.Page.Detail(HangarNamePage.NameRow));
    }

    /// <summary>A blank name says so in the original's own words (langui 203) where it can still be
    /// fixed, rather than only at the Build press.</summary>
    [Fact]
    public void ABlankNameShowsTheOriginalsRefusal()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":203,\"text\":\"You must enter a name for your new plane.\",\"dll\":\"langui\"}]");
        var flow = OpenOnName(strings);
        var page = (HangarNamePage)flow.Page;
        while (flow.Scratch.Name.Length > 0)
        {
            page.Backspace();
        }

        Assert.Contains("must enter a name", page.Detail(HangarNamePage.NameRow), StringComparison.Ordinal);
        Assert.Equal("Name: -", page.RowText(HangarNamePage.NameRow));
    }

    /// <summary>The name showing is the name the store saves, typed or rolled.</summary>
    [Fact]
    public void TheEnteredNameIsWhatTheStoreSaves()
    {
        var flow = OpenOnName();
        flow.Scratch.Engine = 0;
        var page = (HangarNamePage)flow.Page;
        foreach (char c in "Gabriels Revenge")
        {
            page.Type(c);
        }

        flow.Accept();

        Assert.Equal(HangarScreen.Purchase, flow.Screen);
        Assert.True(flow.Commit());
        Assert.Equal("Gabriels Revenge", Assert.Single(_store.List()).Name);
    }

    private static void WalkToName(HangarFlow flow)
    {
        for (int guard = 0; flow.Screen != HangarScreen.Name && guard < HangarFlow.Order.Length + 3; guard++)
        {
            // A no-op except on the airframe-defaults ask (E41), which the airframe screen's own
            // confirm raises before the next confirm advances (E49).
            flow.AnswerDefaultsAsk(false);
            flow.Accept();
        }

        Assert.Equal(HangarScreen.Name, flow.Screen);
    }

    // A flow standing on the PLANENAME screen with a fresh scratch plane.
    private HangarFlow OpenOnName(UiStrings? strings = null, Random? rng = null)
    {
        var flow = new HangarFlow(_store, strings ?? UiStrings.Empty, nameRng: rng ?? new Random(7));
        WalkToName(flow);
        return flow;
    }

    // The same, but editing the store's first saved plane. Through the selection screen's own row
    // rather than StartFromSaved: row 0 is New Plane, whose confirm would replace the scratch.
    private HangarFlow OpenOnNameEditing(string saved)
    {
        var flow = new HangarFlow(_store, UiStrings.Empty, nameRng: new Random(7));
        flow.FocusRow(1);
        Assert.Equal(saved, flow.Page.RowText(1));
        flow.Accept();
        WalkToName(flow);
        return flow;
    }
}
