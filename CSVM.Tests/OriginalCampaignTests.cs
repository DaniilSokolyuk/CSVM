using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The campaign module alone over the hand-authored layout fixture and a scratch profile store. It
/// covers the profile screen's box, roster rows, refusals and its two-answer delete, and the
/// cabin's plaques under the pointer and the keyboard. The briefing, the flight check's launch, the
/// ammo screen's way back and the hangar door the cabin asks the host for are here too. So are the
/// two flight returns' mapping, the book's doors and tabs, the EXPORT box and the screenshot aids'
/// script. The top level's Campaign door, the seat walk and the messagebox's drawing are the
/// shell's, so their seams are wiring facts in <see cref="OriginalShellTests"/>. Everything here
/// drives the module over a hand-written host, and every rectangle is the fixture's invented
/// geometry, read as the original's is.
/// </summary>
public class OriginalCampaignTests : IDisposable
{
    private readonly string _dir;
    private readonly CampaignProfileStore _store;
    private readonly CustomPlaneStore _planes;

    public OriginalCampaignTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-original-campaign-" + Guid.NewGuid().ToString("N"));
        _store = new CampaignProfileStore(Path.Combine(_dir, "Profiles"));
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

    [Fact]
    public void TheProfileScreenOpensOnItsFourRowsWithTheBoxFocusedAndThePlaquesAtTheirSlots()
    {
        var host = Host(out var campaign);

        host.Module.OpenCampaign();

        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
        Assert.True(campaign.IsOpen && host.Module.IsOpen);
        Assert.Equal(new[] { "ROW:0", "Continue", "DeletePlayer", "CancelProfile" }, host.Rows.Select(r => r.Key));
        Assert.Equal(0, host.Focus);
        // The plaques sit at the fixture's own rows, CONTINUE keeping its pinned strip.
        var cancel = Row(host, "CancelProfile");
        Assert.Equal((450f, 540f, 240f, 50f), Rect(cancel));
        Assert.Equal("PM_B_Cancel.png", cancel.Art!.Name);
        var start = Row(host, "Continue");
        Assert.Equal((470f, 290f), (start.X, start.Y));
        Assert.Equal("CM_B_Start.png", start.Art!.Name);
    }

    [Fact]
    public void TypingFeedsTheNameBoxWithTheEditBoxCuesAndContinueCreatesAndSeatsThePlayer()
    {
        var host = Host(out var campaign);
        OpenCampaign(host);

        var cues = Type(host, "Zac/k");
        Assert.Equal("Zack", host.Module.RosterName);
        Assert.Equal(
            new[] { OriginalCues.Text, OriginalCues.Text, OriginalCues.Text, OriginalCues.TextError, OriginalCues.Text },
            cues);
        Assert.Contains(Compose(host).Lines, l => l.Row == 0 && l.Text == "Zack_");
        Erase(host);
        Assert.Equal("Zac", host.Module.RosterName);

        // Enter in the box is the script's own commit path.
        Assert.Null(_store.Load("Zac"));
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Assert.Equal("Zac", campaign.Profile?.Name);
        Assert.NotNull(_store.Load("Zac"));
        Assert.Equal("Zac", _store.LastPlayed);
        Assert.Contains(OriginalCues.Click, host.TakeCues());
    }

    // The box's second route to the same reject cue, which the character-set route never reached:
    // a character the rule accepts, arriving at a box already at its cap.
    [Fact]
    public void AnAcceptedCharacterAtTheCapRefusesWithTheSameRejectCue()
    {
        var host = Host(out _);
        OpenCampaign(host);

        var cues = Type(host, new string('A', CampaignFeature.MaxNameLength + 1));

        Assert.Equal(CampaignFeature.MaxNameLength, host.Module.RosterName.Length);
        Assert.Equal(CampaignFeature.MaxNameLength + 1, cues.Count);
        Assert.Equal(OriginalCues.TextError, cues[cues.Count - 1]);
        Assert.DoesNotContain(OriginalCues.TextError, cues.Take(cues.Count - 1));
    }

    [Fact]
    public void ContinueWithNoNameRaisesTheRefusalAsADialogAndItsOkComesBackToTheScreen()
    {
        var host = Host(out var campaign);
        OpenCampaign(host);
        var start = Row(host, "Continue");

        Click(host, start.X + 2f, start.Y + 2f);

        Assert.NotNull(host.Dialog);
        Assert.Equal(campaign.Strings.Text(200, "You must enter a player name."), host.Dialog!.Message);
        var ok = Assert.Single(host.Dialog.Answers);
        Assert.Equal(OriginalShell.DialogOkKey, ok.Key);
        // CAMPAIGN.SCRIPT raises langui 200 on the 0x1 mask, which is the warning icon.
        Assert.Equal(DialogIcon.Warning, host.Dialog.Icon);
        Assert.Null(_store.Load(string.Empty));
        // The screen under the box draws itself with nothing focused and nothing picked. The
        // roster's own bar and caret stand down while the box does.
        Assert.DoesNotContain(Compose(host).Lines, l => l.Text.EndsWith("_", StringComparison.Ordinal));

        host.Answer(OriginalShell.DialogOkKey);
        Assert.Null(host.Dialog);
        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
        Assert.Equal(4, host.Rows.Count);
    }

    [Fact]
    public void ARosterRowFillsTheBoxOnOneClickAndStartsOnTheSecondWithTheSelectionBarBehindIt()
    {
        _store.Save(CampaignProfileDef.NewProfile("Nathan"));
        _store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var host = Host(out var campaign);
        OpenCampaign(host);
        Assert.Equal(6, host.Rows.Count);
        var nathan = host.Rows[1];
        Assert.Equal((250f, 360f, 300f, 24f), Rect(nathan));
        Assert.Equal(OriginalRowKind.ListRow, nathan.Kind);

        Hover(host, nathan.X + 4f, nathan.Y + 4f);
        Assert.Empty(host.TakeCues());
        Assert.Contains(Compose(host).Fills, f => f.Border && f.X == nathan.X && f.Y == nathan.Y);
        Click(host, nathan.X + 4f, nathan.Y + 4f);
        Assert.Equal("Nathan", host.Module.RosterName);
        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
        Assert.Empty(host.TakeCues());
        Assert.Contains(Compose(host).Fills, f => !f.Border && f.X == nathan.X && f.Y == nathan.Y && f.R == 0x80);
        Assert.DoesNotContain(Compose(host).Lines, l => l.Text.StartsWith("✓", StringComparison.Ordinal));

        Click(host, nathan.X + 4f, nathan.Y + 4f);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Assert.Equal("Nathan", campaign.Profile?.Name);
    }

    // The box's own gesture, which is not a start: CAMPAIGN.SCRIPT commits from CM_B_START, from
    // Enter in the box (the box names START as its default button) and from a second click on a
    // filled roster row. A click in the box is none of the three, so it takes the caret and leaves
    // the screen where it was, however full the box and the roster are.
    [Fact]
    public void AClickInTheNameBoxTakesTheCaretAndStartsNothingWhereEnterStillStarts()
    {
        _store.Save(CampaignProfileDef.NewProfile("Nathan"));
        var host = Host(out var campaign);
        OpenCampaign(host);
        var nathan = host.Rows[1];
        Click(host, nathan.X + 4f, nathan.Y + 4f);
        Assert.Equal("Nathan", host.Module.RosterName);
        var box = host.Rows[0];
        Assert.Equal(OriginalRowKind.TextField, box.Kind);

        Click(host, box.X + 4f, box.Y + 4f);

        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
        Assert.Null(campaign.Profile);
        Assert.Equal("ROW:0", host.FocusedKey);
        Assert.Equal("Nathan", host.Module.RosterName);
        Assert.Contains(OriginalCues.Click, host.TakeCues());

        Accept(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Assert.Equal("Nathan", campaign.Profile?.Name);
    }

    [Fact]
    public void TheBoxOpensOnTheLastPlayerSeatedAndDeletePlayerAsksWithTwoAnswersDeletingOnYes()
    {
        _store.Save(CampaignProfileDef.NewProfile("Nathan"));
        _store.Save(CampaignProfileDef.NewProfile("Zachary"));
        _store.RecordLastPlayed("Zachary");
        var host = Host(out var campaign);
        OpenCampaign(host);
        Assert.Equal("Zachary", host.Module.RosterName);

        var delete = Row(host, "DeletePlayer");
        Click(host, delete.X + 2f, delete.Y + 2f);
        Assert.NotNull(host.Dialog);
        Assert.Equal(
            new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey },
            host.Dialog!.Answers.Select(a => a.Key));
        Assert.Equal(OriginalShell.DialogYesKey, host.FocusedKey);
        // Langui 201 comes up on the 0x4 mask, the one set of masks that keeps the query icon.
        Assert.Equal(DialogIcon.Query, host.Dialog.Icon);

        // Back declines with the last answer, which leaves the profile where it was and hands the
        // focus back to the plaque that asked.
        Back(host);
        Assert.Null(host.Dialog);
        Assert.NotNull(_store.Load("Zachary"));
        Assert.Equal("DeletePlayer", host.FocusedKey);

        Accept(host);
        Down(host);
        Assert.Equal(OriginalShell.DialogNoKey, host.FocusedKey);
        Up(host);
        Assert.Equal(OriginalShell.DialogYesKey, host.FocusedKey);
        Accept(host);
        Assert.Null(_store.Load("Zachary"));
        Assert.NotNull(_store.Load("Nathan"));
        Assert.Equal(string.Empty, host.Module.RosterName);
        Assert.Equal(new[] { "Nathan" }, campaign.Roster);
        Assert.Equal(5, host.Rows.Count);
    }

    [Fact]
    public void TheCabinsPlaquesTakeThePointerAndTheKeyboardAndReturnToMainMenuClosesTheCampaign()
    {
        var host = Host(out var campaign);
        Seat(host, "Zachary");
        Assert.Equal(
            new[] { "NextMission", "PreviousMissions", "PlaneConstruction", "ReturnToMainMenu", "ChangeMemento" },
            host.Rows.Select(r => r.Key));
        Assert.Equal("NextMission", host.FocusedKey);
        var previous = host.Rows[1];
        Assert.Equal((360f, 540f, 240f, 50f), Rect(previous));

        Hover(host, previous.X + 3f, previous.Y + 3f);
        Assert.Equal("PreviousMissions", host.FocusedKey);
        Assert.Equal(new[] { OriginalCues.Rollover }, host.TakeCues());
        var plaque = Compose(host).Plaques.Single(p => p.Art.Name == "PM_B_Previous.png");
        Assert.Equal(2, plaque.Frame);
        Press(host, previous.X + 3f, previous.Y + 3f);
        Assert.Equal(3, Compose(host).Plaques.Single(p => p.Art.Name == "PM_B_Previous.png").Frame);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);

        Release(host, previous.X + 3f, previous.Y + 3f);
        Assert.Equal(OriginalScreen.CampaignPreviousMissions, host.Screen);
        Assert.Equal("ROW:0", host.FocusedKey); // the career row, a fresh profile's one list row
        Back(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);

        Down(host);
        Down(host);
        Assert.Equal("ReturnToMainMenu", host.FocusedKey);
        Down(host);
        Assert.Equal("ChangeMemento", host.FocusedKey);
        Down(host);
        Assert.Equal("NextMission", host.FocusedKey);
        Up(host);
        Up(host);
        Assert.Equal("ReturnToMainMenu", host.FocusedKey);
        Accept(host);
        Assert.Equal(OriginalScreen.TopLevel, host.Screen);
        Assert.False(campaign.IsOpen);
        Assert.False(host.Module.IsOpen);
    }

    [Fact]
    public void BackFromTheCabinReturnsToTheProfileScreenAndBackAgainLeaves()
    {
        var host = Host(out var campaign);
        Seat(host, "Zachary");

        Back(host);
        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
        Assert.True(campaign.IsOpen);
        Assert.Equal("Zachary", host.Module.RosterName);

        Back(host);
        Assert.Equal(OriginalScreen.TopLevel, host.Screen);
        Assert.False(campaign.IsOpen);
    }

    [Fact]
    public void NextMissionOpensTheBriefingWhoseThreePlaquesLeadBackAndOnAndReplayRestartsTheReveal()
    {
        var host = Host(out var campaign);
        Seat(host, "Zachary");

        Accept(host);
        Assert.Equal(OriginalScreen.CampaignBriefing, host.Screen);
        Assert.Equal(0, campaign.MissionSeq);
        Assert.Equal(new[] { "ReplayBriefing", "ReturnToCabin", "GoToFlightCheck" }, host.Rows.Select(r => r.Key));
        var flightCheck = host.Rows[2];
        Assert.Equal((597f, 560f, 196f, 32f), Rect(flightCheck));
        Assert.Equal(BoardArtLibrary.Rimage, flightCheck.Art!.Library);
        // No extraction here, so the briefing has no state; the plaques still stand.
        Assert.Equal(0, host.Module.NarrationStarts);
        Assert.False(host.Module.AdvanceBriefing(1.0 / 60.0));

        Down(host);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignBriefing, host.Screen);
        Back(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
    }

    [Fact]
    public void TheFlightCheckOpensAmmoAndFliesAsOneCampaignMissionExitWithTheProfileSaved()
    {
        var host = Host(out var campaign);
        Seat(host, "Zachary");
        Accept(host);
        var go = Row(host, "GoToFlightCheck");
        Click(host, go.X + 2f, go.Y + 2f);
        Assert.Equal(OriginalScreen.CampaignFlightCheck, host.Screen);
        // The pilot heading is text the cursor steps over; CHANGE PLANE is barred under three planes.
        Assert.Equal(new[] { "ROW:0", "ChangeAmmo", "ReturnToBriefing", "FlyMission" }, host.Rows.Select(r => r.Key));
        Assert.False(host.Rows[0].Enabled);
        Assert.False(host.Rows[0].Visible);
        Assert.Equal("ChangeAmmo", host.FocusedKey);
        var ammo = host.Rows[1];
        Assert.Equal((270f, 131f, 160f, 28f), Rect(ammo));

        Accept(host);
        Assert.Equal(OriginalScreen.CampaignAmmo, host.Screen);
        Assert.Equal(0, campaign.AmmoSlot);
        Assert.Contains(host.Rows, r => r.Key == "AcceptLoadout" && r.X == 340f && r.Y == 550f);
        Back(host);
        Assert.Equal(OriginalScreen.CampaignFlightCheck, host.Screen);

        Down(host);
        Assert.Equal("ReturnToBriefing", host.FocusedKey);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignBriefing, host.Screen);
        Down(host);
        Down(host);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignFlightCheck, host.Screen);

        var fly = Row(host, "FlyMission");
        var profile = campaign.Profile!;
        var exit = Assert.IsType<CampaignMissionExit>(Click(host, fly.X + 2f, fly.Y + 2f));
        Assert.Equal(("Zachary", 0, 1), (exit.Profile, exit.MissionSeq, exit.Seats.Count));
        Assert.Equal("node5", exit.Seats[0].PlaneNode);
        Assert.Equal(
            CampaignProfileStore.Serialize(profile),
            File.ReadAllText(Path.Combine(_store.DirFor("Zachary"), "profile.json")));
        Assert.Contains(OriginalCues.Click, host.TakeCues());
    }

    /// <summary>PLANE CONSTRUCTION is the one door out of the campaign that comes back. The module
    /// asks the host for the hangar over the seated profile's own purse. The return the host makes
    /// re-reads that profile and lands on the plaque the door was pressed from.</summary>
    [Fact]
    public void PlaneConstructionAsksTheHostForTheHangarOverTheWalletAndTheReturnLandsOnTheCabin()
    {
        var host = Host(out var campaign);
        Seat(host, "Zachary");
        var door = Row(host, "PlaneConstruction");

        Click(host, door.X + 2f, door.Y + 2f);

        Assert.Equal(1, host.HangarOpens);
        Assert.NotNull(host.HangarWallet);
        Assert.Equal(campaign.Profile!.Funds, host.HangarWallet!.Funds);

        host.ResumeCampaign();
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Assert.True(campaign.IsOpen);
        Assert.Equal("PlaneConstruction", host.FocusedKey);
    }

    [Fact]
    public void TheTwoFlightReturnsLandOnTheCabinAndTheBookAndClosingDropsTheCampaign()
    {
        _store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var host = Host(out var campaign);

        host.Module.OpenCampaignOver(_store);
        Assert.True(host.Module.ShowCabin("Zachary"));
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
        Assert.Equal("Zachary", campaign.Profile?.Name);
        Assert.Equal("Zachary", _store.LastPlayed);

        host.Module.OpenCampaignOver(_store);
        Assert.True(host.Module.ShowScrapbook("Zachary", 0, missionWon: true));
        Assert.Equal(OriginalScreen.CampaignScrapbook, host.Screen);
        Assert.Equal(0, campaign.MissionSeq);
        Assert.Equal(1, campaign.ScrapbookEntry);
        Assert.Equal("ReturnToCabin", host.FocusedKey);
        Assert.Contains(host.Rows, r => r.Key == "ReturnToCabin" && r.X == 590f && r.Y == 560f);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);

        host.Module.OpenCampaignOver(_store);
        Assert.False(host.Module.ShowCabin("Nobody"));
        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);

        host.Module.CloseCampaign();
        Assert.False(campaign.IsOpen);
        Assert.False(host.Module.IsOpen);
    }

    [Fact]
    public void TheBookBacksToWhateverOpenedItAndTheContentsRowsAreHitAtTheirWindow()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int seq = 0; seq < 2; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, 5, "Gypsy Magic"));
        }

        _store.Save(profile);
        var host = Host(out var campaign);
        Seat(host, "Zachary");
        Down(host);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignPreviousMissions, host.Screen);
        // The career row and two mission rows at the fixture's list box, then the buttons.
        Assert.Equal((410f, 150f, 330f, 60f), Rect(host.Rows[0]));
        Assert.Equal((410f, 210f), (host.Rows[1].X, host.Rows[1].Y));
        Assert.Equal((410f, 270f), (host.Rows[2].X, host.Rows[2].Y));
        Assert.Contains(host.Rows, r => r.Key == "ViewMission" && r.X == 440f && r.Y == 500f);

        var second = host.Rows[2];
        Click(host, second.X + 5f, second.Y + 5f);
        Assert.Equal(OriginalScreen.CampaignPreviousMissions, host.Screen);
        Assert.Contains(Compose(host).Fills, f => f.Y == second.Y && !f.Border);
        var view = Row(host, "ViewMission");
        Click(host, view.X + 2f, view.Y + 2f);
        Assert.Equal(OriginalScreen.CampaignScrapbook, host.Screen);
        Assert.Equal(1, campaign.MissionSeq);
        Back(host);
        Assert.Equal(OriginalScreen.CampaignPreviousMissions, host.Screen);

        var replay = Row(host, "ReplayMission");
        Click(host, replay.X + 2f, replay.Y + 2f);
        Assert.Equal(OriginalScreen.CampaignBriefing, host.Screen);
        Back(host);
        Assert.Equal(OriginalScreen.CampaignPreviousMissions, host.Screen);
        Back(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
    }

    /// <summary>The book's unselected results tab presses no authored button, so it is the page's
    /// own art that gives it a rectangle: without one the pointer passed over a row the keyboard
    /// could reach. The scrap rows keep answering at their own regions beside it.</summary>
    [Fact]
    public void TheBooksUnselectedTabIsHitAtItsArtAndSwitchesTheHalfTheCardReads()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(
            0, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, 5, "Gypsy Magic"));
        _store.Save(profile);
        var host = Host(out _);

        host.Module.OpenCampaignOver(_store);
        Assert.True(host.Module.ShowScrapbook("Zachary", 0, missionWon: true));

        // Most Recent is the plaque the card shows, Best to Date the picture beside it, at the
        // slot's authored 432,283 and the fixture's unmeasured-strip fallback size.
        Assert.Contains(host.Rows, r => r.Key == nameof(BoardButton.MostTab));
        var best = Assert.Single(host.Rows, r => r.X == 432f && r.Y == 283f);
        Assert.Equal((113f, 34f, true), (best.Width, best.Height, best.Visible));

        Click(host, best.X + 2f, best.Y + 2f);

        Assert.Contains(host.Rows, r => r.Key == nameof(BoardButton.BestTab));
        Assert.DoesNotContain(host.Rows, r => r.Key == nameof(BoardButton.MostTab));
        Assert.Contains(host.Rows, r => r.X == 594f && r.Y == 283f && r.Width == 113f);
    }

    [Fact]
    public void TheExportPressRaisesTheOneButtonBoxAndWritesTheGivenStore()
    {
        _store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var scratchPlanes = new CustomPlaneStore(Path.Combine(_dir, "AidPlanes"));
        var host = Host(out var campaign);
        host.Module.OpenCampaignOver(_store, scratchPlanes);
        Assert.True(host.Module.ShowCabin("Zachary"));
        host.Module.ShowMissionScreen(OriginalScreen.CampaignPlaneSelection);
        Assert.Equal(OriginalScreen.CampaignPlaneSelection, host.Screen);

        host.Module.PressExport();

        Assert.NotNull(host.Dialog);
        Assert.Equal(OriginalShell.DialogOkKey, Assert.Single(host.Dialog!.Answers).Key);
        Assert.NotNull(scratchPlanes.Load(campaign.Profile!.Planes[0].Name));
        Assert.Null(_planes.Load(campaign.Profile.Planes[0].Name));

        // A second press off the screen does nothing. The box stands and the screen under it is
        // left alone, the answers being the rows a player can reach.
        host.Module.PressExport();
        Assert.NotNull(host.Dialog);
    }

    [Fact]
    public void AnAidScriptSpellsTheSamePressesUnderOriginalAsItDoesUnderBuiltIn()
    {
        _store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var host = Host(out _);
        host.Module.OpenCampaignOver(_store, _planes);
        Assert.True(host.Module.ShowCabin("Zachary"));
        host.Module.ShowMissionScreen(OriginalScreen.CampaignPlaneSelection);

        // A confirm on the opening row stands the pilot's list open, the entries joining the rows.
        int closed = host.Rows.Count;
        host.Module.RunAidScript("a");
        Assert.Equal("FIELD:0", host.Rows[host.Focus].Key);
        Assert.Contains(host.Rows, r => r.Key.StartsWith("ENTRY:", StringComparison.Ordinal));

        host.Module.RunAidScript("b");
        Assert.Equal(closed, host.Rows.Count);

        // A count with no verb is that many rows down, and the button word is that plaque's press.
        host.Module.RunAidScript("2");
        Assert.Equal("AcceptSelections", host.Rows[host.Focus].Key);
        host.Module.RunAidScript(CampaignAidProfiles.ExportArgument);
        Assert.NotNull(host.Dialog);

        // A word spelling no press at all leaves the screen where it stood.
        host.Module.RunAidScript("qqq");
        Assert.NotNull(host.Dialog);
    }

    private static void OpenCampaign(CampaignHost host)
    {
        host.Module.OpenCampaign();
        Assert.Equal(OriginalScreen.CampaignRoster, host.Screen);
    }

    // A player typed into the box and started, landing on the cabin.
    private static void Seat(CampaignHost host, string name)
    {
        OpenCampaign(host);
        Type(host, name);
        Accept(host);
        Assert.Equal(OriginalScreen.CampaignCabin, host.Screen);
    }

    private static OriginalRow Row(CampaignHost host, string key) => host.Rows.Single(r => r.Key == key);

    private static (float X, float Y, float Width, float Height) Rect(OriginalRow row) =>
        (row.X, row.Y, row.Width, row.Height);

    private static List<string> Type(CampaignHost host, string text) =>
        host.Type(new MenuCommands { Typed = text });

    private static List<string> Erase(CampaignHost host) => host.Type(new MenuCommands { Erase = true });

    private static MenuExit? Accept(CampaignHost host) => host.AcceptPress();

    private static void Back(CampaignHost host) => host.BackPress();

    private static void Down(CampaignHost host) => host.Walk(1);

    private static void Up(CampaignHost host) => host.Walk(-1);

    // The pointer moved onto a point with no button down.
    private static void Hover(CampaignHost host, float x, float y)
    {
        var rows = host.Rows;
        host.Point(rows, HitTest(rows, x, y), x, y, press: false);
    }

    private static void Press(CampaignHost host, float x, float y)
    {
        var rows = host.Rows;
        host.Point(rows, HitTest(rows, x, y), x, y, press: true);
    }

    private static MenuExit? Release(CampaignHost host, float x, float y)
    {
        var rows = host.Rows;
        return host.Release(rows, HitTest(rows, x, y));
    }

    // One click as the shell reads it: the press arms the row and the release on it fires. The
    // step that carries the activation is the second one.
    private static MenuExit? Click(CampaignHost host, float x, float y)
    {
        Press(host, x, y);
        return Release(host, x, y);
    }

    // The fixture's strips: every button strip 240x200 (four 50-pixel frames), the paper plaque
    // 160x112 (four 28-pixel frames), everything else unmeasured.
    private static (int Width, int Height)? Measure(string art) => art switch
    {
        "PM_B_Paper.png" => (160, 112),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    private static int HitTest(IReadOnlyList<OriginalRow> rows, float x, float y)
    {
        // Later rows draw over earlier ones, so the last hit wins.
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Visible && rows[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    // The screen as the module draws it, assembled the way the shell assembles its own board. The
    // shell's own layers (the flag movie, the standing box and the pointer overlay) are not the
    // module's. The strokes the scrapbook writes ride the board like every other layer.
    private static ComposedBoard Compose(CampaignHost host)
    {
        var rows = host.Rows;
        var layers = new BoardLayers();
        int focus = host.Dialog == null ? host.Focus : -1;
        host.Module.Compose(rows, focus, layers);
        return new ComposedBoard(layers.Pictures, layers.Strokes, layers.Lines, layers.Plaques, layers.Notes,
            backdrop: layers.Backdrop, fills: layers.Fills, overlays: layers.Overlays);
    }

    // The module over the layout fixture, a scripted seat, no extraction, and a campaign feature
    // over this test's scratch stores.
    private CampaignHost Host(out CampaignFeature campaign)
    {
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(Array.Empty<CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        campaign = new CampaignFeature(UiStrings.Empty, airframe => $"node{airframe}");
        var store = _store;
        var host = new CampaignHost();
        host.Module = new OriginalCampaignScreen(
            campaign, setup, _planes, CampaignLayout.Over(MenuLayoutReaderTests.OriginalLayout()), host, () => store);
        return host;
    }

    // The campaign's side of the seam, over the shared fake: the cues a frame collected, the
    // pointer's press and release, and the cursor's walk. The rows are the module's own even while
    // a box stands, the box's answers being the shell's rows then. The build store and the words
    // are the module's, which is what the cabin's wallet and its plaques read. Every campaign row
    // is drawn by a shared board component, so the shell's own plate rule never runs behind it.
    private sealed class CampaignHost : OriginalTestHost<OriginalCampaignScreen>
    {
        private readonly List<string> _cues = new();

        internal CampaignHost()
            : base(OriginalScreen.TopLevel, OriginalCampaignTests.Measure, canBuildPlane: true)
        {
        }

        public override CustomPlaneStore? CampaignPlanes => Module.Planes;

        public override UiStrings MenuStrings => Module.Strings ?? UiStrings.Empty;

        // A door onto another screen lets go of whatever the pointer was holding down.
        public override void Open(OriginalScreen screen)
        {
            base.Open(screen);
            PressedRow = -1;
        }

        /// <summary>One frame of the driving seat's commands, the shell's own ApplyFrame narrowed
        /// to the presses a campaign screen takes. Those are the typing, the axis, then Accept or
        /// Back. What the screenshot aids' script replays through the module.</summary>
        public override void Frame(MenuCommands commands)
        {
            ArgumentNullException.ThrowIfNull(commands);
            if (commands.Typed.Length > 0 || commands.Erase)
            {
                Module.TypeName(commands, _cues);
            }

            if (commands.MoveY != 0)
            {
                Walk(commands.MoveY);
            }

            if (commands.MoveX != 0)
            {
                Module.StepSideways(Rows, Focus, commands.MoveX);
            }

            if (commands.Accept)
            {
                AcceptPress();
            }
            else if (commands.Back)
            {
                BackPress();
            }
        }

        // The hangar's own return, which the shell makes by handing it back to this module.
        public override void ResumeCampaign()
        {
            base.ResumeCampaign();
            Module.ResumeCampaign();
        }

        internal List<string> Type(MenuCommands commands)
        {
            var cues = new List<string>();
            Module.TypeName(commands, cues);
            return cues;
        }

        internal List<string> TakeCues()
        {
            var cues = _cues.ToList();
            _cues.Clear();
            return cues;
        }

        // The cursor's walk, the shell's own rule restated. Inside an open list the axis walks the
        // list's entries, and over a box its answers. Otherwise it steps within the focused row's
        // column over enabled rows, wrapping at either end.
        internal void Walk(int direction)
        {
            _cues.Clear();
            if (Dialog is { } dialog)
            {
                DialogFocus = (DialogFocus + direction + dialog.Answers.Count) % dialog.Answers.Count;
                return;
            }

            if (Module.OpenCombo is { } combo && combo.Move(direction))
            {
                return;
            }

            var rows = Rows;
            int focus = Focus;
            if (focus < 0)
            {
                return;
            }

            int i = focus;
            for (int n = 0; n < rows.Count; n++)
            {
                i = (i + direction + rows.Count) % rows.Count;
                if (rows[i].Column == rows[focus].Column && rows[i].Enabled)
                {
                    FocusedRow = i;
                    return;
                }
            }
        }

        internal MenuExit? AcceptPress()
        {
            _cues.Clear();
            if (Dialog != null)
            {
                _cues.Add(OriginalCues.Click);
                Answer(FocusedKey);
                return null;
            }

            var rows = Rows;
            int focus = Focus;
            return focus >= 0 && rows[focus].Enabled ? Fire(rows[focus], byPointer: false) : null;
        }

        internal void BackPress()
        {
            _cues.Clear();
            if (Dialog is { } dialog)
            {
                Answer(dialog.Answers[dialog.Answers.Count - 1].Key);
                return;
            }

            Module.Back();
        }

        // The pointer on one row, as a frame under the cursor leaves the shell. The row it lands on
        // is hovered and, where it is live, focused and cued once. An open list's entry takes the
        // list's own highlight instead of the focus.
        internal void Point(IReadOnlyList<OriginalRow> rows, int over, float x, float y, bool press)
        {
            _cues.Clear();
            Pointer = (x, y);
            PressedRow = -1;
            if (over != HoveredRow)
            {
                HoveredRow = over;
                if (over >= 0 && rows[over].Enabled)
                {
                    if (OriginalWidgets.HoverOnly(rows[over]))
                    {
                        OriginalWidgets.Highlight(Module.OpenCombo, rows[over].Key);
                    }
                    else
                    {
                        FocusedRow = over;
                    }

                    if (rows[over].Kind != OriginalRowKind.ListRow)
                    {
                        _cues.Add(OriginalCues.Rollover);
                    }
                }
            }

            if (press && over >= 0 && rows[over].Enabled)
            {
                PressedRow = over;
            }
        }

        // The release that fires only on the row the press took hold of. A click off every row
        // closes an open list and picks nothing.
        internal MenuExit? Release(IReadOnlyList<OriginalRow> rows, int over)
        {
            _cues.Clear();
            MenuExit? exit = null;
            if (over >= 0 && over == PressedRow && rows[over].Enabled)
            {
                exit = Fire(rows[over], byPointer: true);
            }
            else if (over < 0)
            {
                Module.CloseDropdown();
            }

            PressedRow = -1;
            return exit;
        }

        // The shell's own Activate: every press but a list row's cues the click. A standing box
        // takes the answer whatever screen it stands over. A click in an edit box puts the caret
        // there and does nothing else.
        private MenuExit? Fire(OriginalRow row, bool byPointer)
        {
            if (row.Kind != OriginalRowKind.ListRow)
            {
                _cues.Add(OriginalCues.Click);
            }

            if (Dialog != null)
            {
                Answer(row.Key);
                return null;
            }

            return byPointer && row.Kind == OriginalRowKind.TextField ? null : Module.Activate(row);
        }
    }
}
