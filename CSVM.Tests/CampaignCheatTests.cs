using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's four menu cheats away from any presentation: the typed-prefix latch three screens
/// share, the store the campaign carries, the two wallet writers the cash word and the unlocking
/// pilot name drive, the one character the name box takes only inside that name, and the cabin's
/// mission pull-down over a flow.
/// </summary>
public class CampaignCheatTests : IDisposable
{
    private readonly string _dir;
    private readonly CampaignProfileStore _profiles;
    private readonly CustomPlaneStore _planes;

    public CampaignCheatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-campaign-cheats-" + Guid.NewGuid().ToString("N"));
        _profiles = new CampaignProfileStore(Path.Combine(_dir, "Profiles"));
        _planes = new CustomPlaneStore(Path.Combine(_dir, "Planes"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A latch takes the keyboard on the click inside its region and gives it up on one
    /// anywhere else, each answering whether that press moved anything.</summary>
    [Fact]
    public void ArmingAndDisarmingReportTheEdgeAndLeaveTheBufferAlone()
    {
        var cheat = new TypedCheat(CampaignCheats.MissionWord);
        Assert.False(cheat.Armed);

        Assert.True(cheat.Arm());
        Assert.False(cheat.Arm());
        cheat.Type('i');
        Assert.True(cheat.Disarm());
        Assert.False(cheat.Disarm());

        Assert.Equal("i", cheat.Buffer);
        Assert.True(cheat.Typed);
    }

    /// <summary>Characters accumulate and the word fires on its last one, once.</summary>
    [Fact]
    public void TheWordFiresOnItsLastCharacter()
    {
        var cheat = new TypedCheat(CampaignCheats.MissionWord);

        Assert.False(cheat.Type('i'));
        Assert.False(cheat.Type('d'));
        Assert.False(cheat.Type('a'));
        Assert.False(cheat.Type('h'));
        Assert.True(cheat.Type('o'));
        Assert.Equal("idaho", cheat.Buffer);

        // A character past the completed word is one past its length, so the buffer empties.
        Assert.False(cheat.Type('o'));
        Assert.Equal(string.Empty, cheat.Buffer);
    }

    /// <summary>A character outside the prefix empties the buffer rather than re-anchoring it, so
    /// "iidaho" fires nothing and a retype has to start at the first letter.</summary>
    [Fact]
    public void AMissEmptiesTheBufferSoARetypeStartsOver()
    {
        var cheat = new TypedCheat(CampaignCheats.MissionWord);

        foreach (char c in "iidaho")
        {
            Assert.False(cheat.Type(c));
        }

        Assert.Equal(string.Empty, cheat.Buffer);
        foreach (char c in "idah")
        {
            Assert.False(cheat.Type(c));
        }

        Assert.True(cheat.Type('o'));
    }

    /// <summary>The compare is case-sensitive, which is what the script's <c>left$</c> is.</summary>
    [Fact]
    public void TheWordIsCaseSensitive()
    {
        var cheat = new TypedCheat(CampaignCheats.GalleryWord);

        foreach (char c in "ISPY")
        {
            Assert.False(cheat.Type(c));
        }

        Assert.Equal(string.Empty, cheat.Buffer);
    }

    /// <summary>Leaving the screen rebuilds the widget: no keyboard and no buffer.</summary>
    [Fact]
    public void ResetGivesUpTheKeyboardAndEmptiesTheBuffer()
    {
        var cheat = new TypedCheat(CampaignCheats.CashWord);
        cheat.Arm();
        cheat.Type('g');

        cheat.Reset();

        Assert.False(cheat.Armed);
        Assert.Equal(string.Empty, cheat.Buffer);
        Assert.False(cheat.Typed);
    }

    /// <summary>The store starts with every cheat off and no mission picked.</summary>
    [Fact]
    public void TheStoreStartsWithEverythingOff()
    {
        var cheats = new CampaignCheats();

        Assert.False(cheats.MissionListShown);
        Assert.False(cheats.RevealAll);
        Assert.False(cheats.AllowAll);
        Assert.Equal(-1, cheats.MissionPick);
    }

    /// <summary>The pick is a 1-based ordinal inside the campaign; anything else clears it.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 24)]
    [InlineData(0, -1)]
    [InlineData(25, -1)]
    [InlineData(-3, -1)]
    public void PickMissionTakesOnlyAMissionOfTheCampaign(int ordinal, int expected)
    {
        var cheats = new CampaignCheats();

        cheats.PickMission(ordinal);

        Assert.Equal(expected, cheats.MissionPick);
    }

    /// <summary>Closing the campaign drops the pull-down and its pick and keeps the original's two
    /// globals, which it clears only when the process exits.</summary>
    [Fact]
    public void ClosingTheCampaignDropsThePullDownAndKeepsTheGlobals()
    {
        var cheats = new CampaignCheats();
        cheats.ShowMissionList();
        cheats.PickMission(7);
        cheats.Reveal();
        cheats.AllowEverything();

        cheats.CloseCampaign();

        Assert.False(cheats.MissionListShown);
        Assert.Equal(-1, cheats.MissionPick);
        Assert.True(cheats.RevealAll);
        Assert.True(cheats.AllowAll);
    }

    /// <summary>The pilot name is compared the way the original's own <c>lstrcmpiA</c> compares it,
    /// and a name that merely contains it is not it.</summary>
    [Theory]
    [InlineData("crashcheat!", true)]
    [InlineData("CrashCheat!", true)]
    [InlineData("  crashcheat!  ", true)]
    [InlineData("crashcheat", false)]
    [InlineData("crashcheat!!", false)]
    [InlineData("Zachary", false)]
    [InlineData("", false)]
    public void TheUnlockingNameIsRecognisedWithoutCase(string name, bool unlocks)
    {
        Assert.Equal(unlocks, CampaignCheats.IsUnlockName(name));
    }

    /// <summary>The hub's grant is 25000 while the balance is under 50000 and nothing at or above
    /// it, and every grant reaches the profile on disk.</summary>
    [Fact]
    public void TheTypedGrantStopsAtTheScriptsOwnCeiling()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        _profiles.Save(profile);
        var wallet = new CampaignWallet(_profiles, profile, _planes);

        Assert.True(wallet.GrantCheatCash());
        Assert.Equal(25000, profile.Funds);
        Assert.Equal(25000, _profiles.Load("Zachary")?.Funds);

        Assert.True(wallet.GrantCheatCash());
        Assert.Equal(50000, profile.Funds);

        Assert.False(wallet.GrantCheatCash());
        Assert.Equal(50000, profile.Funds);
    }

    /// <summary>The unlocking name fills the wallet and adds the eleven stock airframes beside the
    /// two starters, and pressing it again adds none of them twice.</summary>
    [Fact]
    public void TheUnlockingNameFillsTheWalletAndAddsTheStockAirframes()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        _profiles.Save(profile);
        int starters = profile.Planes.Count;
        var wallet = new CampaignWallet(_profiles, profile, _planes, UiStrings.Empty);

        wallet.UnlockEverything();

        Assert.Equal(250000, profile.Funds);
        Assert.Equal(starters + HangarEconomy.Airframes.Length, profile.Planes.Count);
        Assert.Equal(profile.Planes.Count, _profiles.Load("Zachary")?.Planes.Count);
        var airframes = profile.Planes.Select(p => p.Airframe).ToList();
        for (int airframe = 0; airframe < HangarEconomy.Airframes.Length; airframe++)
        {
            Assert.Contains(airframe, airframes);
        }

        wallet.UnlockEverything();

        Assert.Equal(starters + HangarEconomy.Airframes.Length, profile.Planes.Count);
    }

    /// <summary>The unlocking name switches off the availability threshold the hangar gates an
    /// airframe on, which is what <c>fAllowAll</c> does in the three functions that read it.</summary>
    [Fact]
    public void TheUnlockingNameBypassesEveryAirframeThreshold()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var cheats = new CampaignCheats();
        var gated = new CampaignWallet(_profiles, profile, _planes, UiStrings.Empty, cheats);
        Assert.False(gated.IsAirframeAvailable(2));

        cheats.AllowEverything();

        for (int airframe = 0; airframe < HangarEconomy.Airframes.Length; airframe++)
        {
            Assert.True(gated.IsAirframeAvailable(airframe));
        }
    }

    /// <summary>The name box takes the unlocking name's exclamation mark only where what stands in
    /// it is that name's own prefix, so no profile can be created carrying one.</summary>
    [Fact]
    public void TheNameBoxTakesTheUnlockingNameAndNoOtherPunctuation()
    {
        var box = new CampaignTextEntry();
        box.Type("crashcheat!");
        Assert.Equal("crashcheat!", box.Text);
        Assert.False(CampaignTextEntry.Valid("crashcheat!"));

        var other = new CampaignTextEntry();
        other.Type("Zachary!");
        Assert.Equal("Zachary", other.Text);

        Assert.True(CampaignTextEntry.AcceptsNext("crashcheat", '!'));
        Assert.False(CampaignTextEntry.AcceptsNext("crashcheat!", '!'));
        Assert.False(CampaignTextEntry.AcceptsNext(string.Empty, '!'));
    }

    /// <summary>The cabin carries five buttons until the word is typed, and the pull-down after them
    /// once it has been, opened on the campaign's own position.</summary>
    [Fact]
    public void TheCabinsWordShowsThePullDownOnTheCampaignsOwnPosition()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.MissionsCompleted = 6;
        var flow = Cabin(profile);
        Assert.Equal(5, flow.Page.RowCount);
        Assert.Null(flow.Page.Combo(5));

        flow.Cheats.ShowMissionList();

        Assert.Equal(6, flow.Page.RowCount);
        var missions = flow.Page.Combo(5);
        Assert.NotNull(missions);
        Assert.Equal(24, missions!.Entries.Count);
        Assert.Equal(6, missions.Selected);
        Assert.Equal(7, flow.Cheats.MissionPick);
    }

    /// <summary>A finished campaign opens the pull-down on its last row: the script clamps 24 to
    /// 23, the only row of the 24 the list does not otherwise reach.</summary>
    [Fact]
    public void AFinishedCampaignOpensThePullDownOnItsLastRow()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.MissionsCompleted = 24;
        var flow = Cabin(profile);

        flow.Cheats.ShowMissionList();

        Assert.Equal(23, flow.Page.Combo(5)?.Selected);
        Assert.Equal(24, flow.Cheats.MissionPick);
    }

    /// <summary>The pull-down opens on a press and commits the row the cursor stands on, which is
    /// the mission NEXT MISSION then launches.</summary>
    [Fact]
    public void APressOpensThePullDownAndTheNextOneCommitsTheRow()
    {
        var flow = Cabin(CampaignProfileDef.NewProfile("Zachary"));
        flow.Cheats.ShowMissionList();
        flow.FocusRow(5);

        flow.Accept();
        Assert.NotNull(flow.OpenCombo);

        flow.Move(1);
        flow.Move(1);
        flow.Accept();

        Assert.Null(flow.OpenCombo);
        Assert.Equal(2, flow.Page.Combo(5)?.Selected);
        Assert.Equal(3, flow.Cheats.MissionPick);
    }

    /// <summary>The unlocking pilot name re-accepts the player the screen stood on, leaves the box
    /// holding that name and the screen standing, and switches every airframe on.</summary>
    [Fact]
    public void TheUnlockingNameReacceptsThePreviousPlayerAndStays()
    {
        var flow = new CampaignFlow(_profiles, UiStrings.Empty, null, _planes);
        flow.FocusRow(0);
        flow.Accept();
        flow.Type("Zachary");
        flow.Accept();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);

        var roster = new CampaignFlow(_profiles, UiStrings.Empty, null, _planes);
        roster.FocusRow(0);
        roster.Accept();
        roster.Type(CampaignCheats.UnlockName);
        Assert.Equal(CampaignCheats.UnlockName, roster.Page.TextEntry?.Text);

        roster.Accept();

        Assert.Equal(CampaignScreen.Roster, roster.Screen);
        Assert.Equal("Zachary", roster.Page.TextEntry?.Text);
        Assert.True(roster.Cheats.AllowAll);
        Assert.Null(_profiles.Load(CampaignCheats.UnlockName));
        var profile = _profiles.Load("Zachary");
        Assert.Equal(CampaignCheats.UnlockFunds, profile?.Funds);
        Assert.Equal(2 + HangarEconomy.Airframes.Length, profile?.Planes.Count);
    }

    /// <summary>The gallery word opens every spread: the contents list grows to the campaign's
    /// whole 24 missions and no objective bit is consulted.</summary>
    [Fact]
    public void TheGalleryWordOpensEverySpreadWhateverWasFlown()
    {
        var flow = Cabin(CampaignProfileDef.NewProfile("Zachary"));
        flow.GoTo(CampaignScreen.PreviousMissions);
        int flown = flow.Page.RowCount;

        flow.Cheats.Reveal();

        Assert.True(flow.Page.RowCount > flown);
        Assert.True(ScrapbookComposition.Visible(1, 0, revealAll: true));
        Assert.False(ScrapbookComposition.Visible(1, 0));
    }

    // A flow standing on the cabin over a profile written to this test's own store.
    private CampaignFlow Cabin(CampaignProfileDef profile)
    {
        _profiles.Save(profile);
        var flow = new CampaignFlow(_profiles, UiStrings.Empty, null, _planes);
        flow.SelectProfile(profile);
        return flow;
    }
}
