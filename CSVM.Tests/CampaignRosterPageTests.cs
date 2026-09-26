using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The player-profile screen: the name field over the roster, CONTINUE creating or
/// continuing a player, a roster row selecting then continuing, the original's own refusals, and a
/// delete that is confirmed and takes only the profile's own directory.</summary>
public class CampaignRosterPageTests
{
    [Fact]
    public void AnEmptyRosterDrawsTheFieldAndTheThreeButtons()
    {
        var flow = NewFlow(out _);
        var page = flow.Page;

        Assert.Equal(4, page.RowCount);
        Assert.StartsWith("Name:", page.RowText(0));
        Assert.Equal("CONTINUE", page.RowText(1));
        Assert.Equal("DELETE PLAYER", page.RowText(2));
        Assert.Equal("CANCEL", page.RowText(3));
    }

    [Fact]
    public void ContinueCreatesTheNamedProfileAndOpensTheCabin()
    {
        var flow = NewFlow(out string dir);
        TypeName(flow, "Zachary");
        flow.FocusRow(1); // CONTINUE
        flow.Accept();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal("Zachary", flow.Profile?.Name);
        Assert.Equal(new[] { "Zachary" }, new CampaignProfileStore(dir).List());
    }

    /// <summary>Enter in the name box is the same commit path as the CONTINUE button, which is what
    /// the original's own script does with both.</summary>
    [Fact]
    public void ConfirmingInTheArmedFieldContinuesToo()
    {
        var flow = NewFlow(out _);
        TypeName(flow, "Zachary");
        flow.FocusRow(0);
        flow.Accept();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    [Fact]
    public void AnEmptyNameIsRefusedInTheOriginalsWords()
    {
        var flow = NewFlow(out _);
        flow.FocusRow(1);
        flow.Accept();

        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        Assert.Equal("You must enter a player name.", flow.Message);
    }

    [Fact]
    public void ContinuingAnExistingNameLoadsItRatherThanReplacingIt()
    {
        var flow = NewFlow(out string dir);
        var store = new CampaignProfileStore(dir);
        var saved = CampaignProfileDef.NewProfile("Zachary");
        saved.Funds = 900;
        store.Save(saved);
        flow.RefreshRoster();

        TypeName(flow, "Zachary");
        flow.FocusRow(flow.Roster.Count + 1);
        flow.Accept();

        Assert.Equal(900, flow.Profile?.Funds);
        Assert.Single(store.List());
    }

    [Fact]
    public void ARosterRowSelectsOnTheFirstConfirmAndContinuesOnTheSecond()
    {
        var flow = Seeded(out _, "Zachary");
        flow.FocusRow(1);

        flow.Accept();
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        Assert.Equal("✓ Zachary", flow.Page.RowText(1));

        flow.Accept();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal("Zachary", flow.Profile?.Name);
    }

    /// <summary>Deleting is confirmed, opens on the answer that keeps the campaign, and removes
    /// only the named profile's directory: the other profile and anything beside the store (the
    /// hangar's own plane files) are untouched.</summary>
    [Fact]
    public void DeletingIsConfirmedAndTakesOnlyThatProfile()
    {
        var flow = Seeded(out string dir, "Nathan", "Zachary");
        var sentinel = Path.Combine(Directory.GetParent(dir)!.FullName, "plane.json");
        File.WriteAllText(sentinel, "a hangar plane");

        flow.FocusRow(2); // the Zachary row
        flow.Accept();    // select it, filling the name field
        flow.FocusRow(flow.Roster.Count + 2); // DELETE PLAYER
        flow.Accept();

        Assert.Equal(2, flow.Page.RowCount);
        Assert.Equal("Delete Zachary", flow.Page.RowText(0));
        Assert.Equal(1, flow.Row); // the keep answer, not the destructive one

        flow.Accept(); // keep
        Assert.Equal(2, flow.Roster.Count);

        flow.FocusRow(flow.Roster.Count + 2);
        flow.Accept();
        flow.FocusRow(0);
        flow.Accept(); // delete

        Assert.Equal(new[] { "Nathan" }, flow.Roster);
        Assert.True(File.Exists(sentinel));
        Assert.True(Directory.Exists(Path.Combine(dir, "Nathan")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Zachary")));
    }

    [Fact]
    public void DeletingANameNoProfileCarriesRefusesRatherThanConfirming()
    {
        var flow = NewFlow(out _);
        TypeName(flow, "Nobody");
        flow.FocusRow(2); // DELETE PLAYER

        flow.Accept();

        Assert.Contains("Nobody", flow.Message);
        Assert.Equal(4, flow.Page.RowCount);
    }

    [Fact]
    public void AFullRosterRefusesANewPlayer()
    {
        var flow = NewFlow(out string dir);
        var store = new CampaignProfileStore(dir);
        for (int i = 0; i < CampaignFlow.MaxProfiles; i++)
        {
            store.Save(CampaignProfileDef.NewProfile($"Pilot {i}"));
        }

        flow.RefreshRoster();
        TypeName(flow, "One More");
        flow.FocusRow(flow.Roster.Count + 1);
        flow.Accept();

        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        Assert.Contains("24", flow.Message);
        Assert.Equal(CampaignFlow.MaxProfiles, flow.Roster.Count);
    }

    /// <summary>While the field is armed the cursor axis edits the name instead of the list, which
    /// is the whole of a pad's text entry: up adds a character, the stepper picks it.</summary>
    [Fact]
    public void TheArmedFieldTakesTheCursorAxesForTheName()
    {
        var flow = NewFlow(out _);
        flow.Accept(); // arm the field on row 0

        Assert.True(flow.CapturesText);
        flow.Move(-1);
        flow.Step(1);

        Assert.Equal(0, flow.Row);
        Assert.Equal("B", flow.Page.TextEntry?.Text);
        flow.Move(1);
        Assert.Equal(string.Empty, flow.Page.TextEntry?.Text);
    }

    [Fact]
    public void BackDisarmsTheFieldBeforeItLeavesTheScreen()
    {
        var flow = NewFlow(out _);
        flow.Accept();

        flow.Back();
        Assert.False(flow.CapturesText);
        Assert.Equal(CampaignExit.None, flow.Exit);

        flow.Back();
        Assert.Equal(CampaignExit.Cancelled, flow.Exit);
    }

    [Fact]
    public void CancelLeavesTheCampaign()
    {
        var flow = NewFlow(out _);
        flow.FocusRow(3); // CANCEL

        flow.Accept();

        Assert.Equal(CampaignExit.Cancelled, flow.Exit);
    }

    /// <summary>Returning to a campaign is one press: the screen opens on the profile last played,
    /// already ticked, and the confirm on that row flies it. Driven through the cursor the way a
    /// pad drives it, over a roster of three where the remembered one is neither first nor last.</summary>
    [Fact]
    public void TheScreenOpensOnTheProfileLastPlayedAndOnePressContinuesIt()
    {
        var first = Seeded(out string dir, "Amelia", "Nathan", "Zachary");
        first.FocusRow(2); // the Nathan row
        first.Accept();    // select
        first.Accept();    // continue, which is what records him
        Assert.Equal("Nathan", first.Profile?.Name);

        var reopened = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);

        Assert.Equal(2, reopened.Row);
        Assert.Equal("✓ Nathan", reopened.Page.RowText(2));
        Assert.Equal("Name:  Nathan", reopened.Page.RowText(0));
        reopened.Accept();
        Assert.Equal(CampaignScreen.Cabin, reopened.Screen);
        Assert.Equal("Nathan", reopened.Profile?.Name);
    }

    /// <summary>A remembered name is resolved against the roster, never trusted: the roster is
    /// alphabetical, so a row index would have moved anyway, and the profile may be gone.</summary>
    [Fact]
    public void AProfileDeletedSinceItWasPlayedOpensOnTheNameFieldInstead()
    {
        var first = Seeded(out string dir, "Amelia", "Nathan", "Zachary");
        first.FocusRow(2);
        first.Accept();
        first.Accept();

        Directory.Delete(Path.Combine(dir, "Nathan"), recursive: true);
        var reopened = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);

        Assert.Equal(0, reopened.Row);
        Assert.Equal("Name:  (none)", reopened.Page.RowText(0));
        Assert.Equal(new[] { "Amelia", "Zachary" }, reopened.Roster);
        reopened.Accept(); // arms the field rather than flying a profile that is not there
        Assert.True(reopened.CapturesText);
        Assert.Equal(CampaignScreen.Roster, reopened.Screen);
    }

    /// <summary>Deleting the remembered profile through the screen clears the record too, so the
    /// next visit opens on the field rather than on a name nothing answers to.</summary>
    [Fact]
    public void DeletingTheRememberedProfileForgetsIt()
    {
        var first = Seeded(out string dir, "Nathan", "Zachary");
        first.FocusRow(1);
        first.Accept();
        first.Accept();
        Assert.Equal("Nathan", new CampaignProfileStore(dir).LastPlayed);

        var reopened = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
        reopened.FocusRow(reopened.Roster.Count + 2); // DELETE PLAYER
        reopened.Accept();
        Assert.Equal("Delete Nathan", reopened.Page.RowText(0));
        reopened.FocusRow(0);
        reopened.Accept();

        Assert.Equal(string.Empty, new CampaignProfileStore(dir).LastPlayed);
        Assert.Equal(0, new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty).Row);
    }

    private static CampaignFlow NewFlow(out string dir)
    {
        dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        return new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty);
    }

    private static CampaignFlow Seeded(out string dir, params string[] names)
    {
        var flow = NewFlow(out dir);
        var store = new CampaignProfileStore(dir);
        foreach (string name in names)
        {
            store.Save(CampaignProfileDef.NewProfile(name));
        }

        flow.RefreshRoster();
        return flow;
    }

    // What the keyboard does on the screen: arm the field on its row, then type.
    private static void TypeName(CampaignFlow flow, string name)
    {
        flow.FocusRow(0);
        flow.Accept();
        flow.Type(name);
    }
}
