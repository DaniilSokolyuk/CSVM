using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The plane selection screen: its rows with and without a wingman, the combos filled from
/// the profile, the cursor opening on the slot that was pressed, an open list owning the cursor,
/// the refusal of a plane the other crew slot flies, ACCEPT writing both picks and CANCEL restoring
/// them.</summary>
public class CampaignPlaneSelectionPageTests
{
    [Fact]
    public void WithNoWingmanTheScreenDrawsOneSlotAndTheTwoCommitButtons()
    {
        var page = NewPage(out _, wingman: false);

        Assert.Equal(4, page.RowCount); // pick, export, accept, cancel
        Assert.Equal(1, page.ActiveSlots);
        Assert.NotNull(page.Combo(0));
        Assert.Equal(BoardButton.ExportPlane, page.Button(1).Button);
        Assert.Equal("ACCEPT SELECTIONS", page.RowText(2));
        Assert.Equal("CANCEL SELECTIONS", page.RowText(3));
    }

    [Fact]
    public void WithAWingmanBothSlotsGetACombiAndAnExport()
    {
        var page = NewPage(out _, wingman: true);

        Assert.Equal(6, page.RowCount);
        Assert.Equal(2, page.ActiveSlots);
        Assert.NotNull(page.Combo(0));
        Assert.NotNull(page.Combo(2));
        Assert.Equal(1, page.Button(3).Slot);
    }

    [Fact]
    public void TheCombosCarryEveryOwnedPlaneNamedWithItsAirframe()
    {
        var page = NewPage(out _, wingman: true);

        var combo = page.Combo(0)!;
        Assert.Equal(3, combo.Entries.Count);
        Assert.Equal("Gypsy Magic - Airframe 5", combo.Entries[0]);
        Assert.Equal("Blue Streak - Airframe 3", combo.Entries[2]);
    }

    /// <summary>The pilot's and the wingman's combos open on the profile's own two indices, which
    /// differ from the start (the pair's division of labour, CampaignProfileDef.NewProfile).</summary>
    [Fact]
    public void EachComboOpensOnItsOwnSlotsPlane()
    {
        var page = NewPage(out _, wingman: true);

        Assert.Equal(0, page.Combo(0)!.Selected);
        Assert.Equal(1, page.Combo(2)!.Selected);
    }

    [Fact]
    public void TheCursorOpensOnTheSlotWhosePressOpenedTheScreen()
    {
        var page = NewPage(out var flow, wingman: true);

        flow.SetPlaneSlot(1);
        Assert.Equal(2, page.OpeningRow);

        flow.SetPlaneSlot(0);
        Assert.Equal(0, page.OpeningRow);
    }

    [Fact]
    public void ConfirmOnAPickRowOpensTheListAndTheNextConfirmTakesTheRowUnderTheCursor()
    {
        var page = NewPage(out var flow, wingman: false);
        var combo = page.Combo(0)!;

        Assert.True(page.Accept(0));
        Assert.True(combo.Open);

        combo.Move(1);
        Assert.True(page.Accept(0));
        Assert.False(combo.Open);
        Assert.Equal(1, combo.Selected);
    }

    [Fact]
    public void AClosedFieldStillSteps()
    {
        var page = NewPage(out _, wingman: false);

        Assert.True(page.Step(0, 1));
        Assert.Equal(1, page.Combo(0)!.Selected);
    }

    [Fact]
    public void BackClosesAnOpenListBeforeItLeavesTheScreen()
    {
        var page = NewPage(out _, wingman: false);
        page.Accept(0);

        Assert.True(page.Back());          // consumed: the list closed
        Assert.False(page.Combo(0)!.Open);
        Assert.False(page.Back());         // nothing left to close, so the flow leaves
    }

    [Fact]
    public void AcceptWritesBothPicksIntoTheProfileAndSavesIt()
    {
        var page = NewPage(out var flow, wingman: true);
        page.Step(2, 1);  // wingman off plane 1 first, or the pilot's step onto it is refused
        page.Step(0, 1);  // pilot onto plane 1

        Assert.True(page.Accept(4));

        var profile = flow.Profile!;
        Assert.Equal(1, profile.SelectedPlane);
        Assert.Equal(2, profile.WingmanPlane);
        Assert.Equal(1, flow.Store.Load(profile.Name)!.SelectedPlane);
        Assert.Equal(CampaignScreen.FlightCheck, flow.Screen);
    }

    [Fact]
    public void CancelLeavesBothCrewSlotsFlyingWhatTheyFlew()
    {
        var page = NewPage(out var flow, wingman: true);
        page.Step(2, 1);
        page.Step(0, 1);

        Assert.True(page.Accept(5));

        Assert.Equal(0, flow.Profile!.SelectedPlane);
        Assert.Equal(1, flow.Profile!.WingmanPlane);
        Assert.Equal(0, page.Combo(0)!.Selected);
        Assert.Equal(1, page.Combo(2)!.Selected);
        Assert.Equal(CampaignScreen.FlightCheck, flow.Screen);
    }

    /// <summary>The script's own refusal on message 10015: the pilot cannot take the plane the
    /// wingman is flying, and neither pick moves.</summary>
    [Fact]
    public void APickTheOtherCrewSlotFliesIsRefusedAndNeitherPickMoves()
    {
        var page = NewPage(out var flow, wingman: true);

        Assert.True(page.Step(0, 1)); // onto plane 1, which the wingman flies

        Assert.Equal("Pilot and Wingman must fly different planes.", flow.Modal?.Message);
        Assert.Equal(DialogIcon.Warning, flow.Modal!.Icon); // langui 710's 0x1 mask
        Assert.Equal(0, page.Combo(0)!.Selected);
        Assert.Equal(1, page.Combo(2)!.Selected);
    }

    /// <summary>The script's other arm, <c>!POA || !QOA</c>: a screen with one active crew slot has
    /// nothing to clash with, so every pick is taken.</summary>
    [Fact]
    public void OnAWingmanlessMissionTheSamePickIsPermitted()
    {
        var page = NewPage(out var flow, wingman: false);

        Assert.True(page.Step(0, 1));

        Assert.Null(flow.Modal);
        Assert.Equal(1, page.Combo(0)!.Selected);
    }

    [Fact]
    public void ARefusedListPickLeavesTheComboClosedOnItsOldValueAfterTheDialogIsAnswered()
    {
        var page = NewPage(out var flow, wingman: true);
        var combo = page.Combo(0)!;
        page.Accept(0);
        combo.Move(1); // the cursor onto the wingman's plane

        Assert.True(page.Accept(0));
        Assert.NotNull(flow.Modal);

        Assert.True(flow.Accept()); // the dialog answered, not the screen underneath
        Assert.Null(flow.Modal);
        Assert.False(combo.Open);
        Assert.Equal(0, combo.Selected);
    }

    /// <summary>The four ratings of the two aircraft the reference screenshots show: the seeded
    /// Devastator reads Average on all four, and the awarded Bloodhawk Average, Excellent and Fair
    /// under a top speed the original leaves blank.</summary>
    [Fact]
    public void TheFourRatingsMatchTheReferenceScreenshots()
    {
        var starter = PlaneFit.For(5, null, DevastatorStock());
        Assert.Equal(new[] { 2, 2, 2, 2 }, PlaneRatings.For(starter));

        var blueStreak = PlaneFit.For(3, CampaignProgression.AwardBuild(3), null);
        Assert.Equal(new[] { 4, 2, 4, 1 }, PlaneRatings.For(blueStreak));
    }

    /// <summary>Two awards the ratings' own arithmetic separates from any power-to-weight or
    /// calibre-count reading of them: the Balmoral is not the slowest airframe on the screen and is
    /// not armoured Excellent, and the Warhawk's two twin mounts do not reach Good.</summary>
    [Fact]
    public void TheRatingsReadTheArmamentsWeightAndTheEnginesPower()
    {
        var jumpingJane = PlaneFit.For(2, CampaignProgression.AwardBuild(2), null);
        Assert.Equal(new[] { 1, 3, 0, 3 }, PlaneRatings.For(jumpingJane));

        var annie = PlaneFit.For(10, CampaignProgression.AwardBuild(10), null);
        Assert.Equal(new[] { 2, 3, 0, 2 }, PlaneRatings.For(annie));
    }

    /// <summary>A record with no engine rates Poor for speed without touching the power table.</summary>
    [Fact]
    public void AnEngineLessRecordRatesPoorForSpeed()
    {
        var def = CampaignProgression.AwardBuild(3)!;
        def.Engine = CustomPlaneDef.EngineNone;

        Assert.Equal(0, PlaneRatings.Speed(PlaneFit.For(3, def, null)));
    }

    [Fact]
    public void TheScreenIsRegisteredAndComposesOverItsOwnBackground()
    {
        Assert.True(CampaignFlow.HasPage(CampaignScreen.PlaneSelection));
        var page = NewPage(out var flow, wingman: true);

        var board = CampaignBoards.For(page, flow.Row);

        Assert.Contains(board.Backdrop, art => art.Art.Name == "PS_BackGround.jpg");
        Assert.Contains(board.Lines, line => line.Text == "PLANE SELECTION");
        Assert.Contains(board.Lines, line => line.Text == "PILOT");
        Assert.Contains(board.Lines, line => line.Text == "WINGMAN");
        Assert.Contains(board.Lines, line => line.Text.StartsWith("AGILITY:"));
        Assert.Equal(2, System.Linq.Enumerable.Count(
            board.Plaques, p => p.Label == "Export"));
    }

    [Fact]
    public void AnOpenListDrawsAsAnOverlayOverTheScreen()
    {
        var page = NewPage(out var flow, wingman: false);
        page.Accept(0);

        var board = CampaignBoards.For(page, 0);

        var panel = Assert.Single(board.Overlays);
        Assert.Equal(3, panel.Lines.Count); // the three entries, no scrollbar at three of thirteen
    }

    /// <summary>EXPORT writes the plane under its own name into the build store the Instant Action
    /// and multiplayer pickers list, carrying the ammunition and ordnance the campaign fitted, and
    /// answers in langui 702's words.</summary>
    [Fact]
    public void ExportWritesThePlanesCampaignPicksIntoTheBuildStore()
    {
        var page = NewPage(out var flow, out var planes, wingman: false);
        var plane = flow.Profile!.Planes[0];
        plane.Ammo = new[] { 2, 1, 0, 4 };
        plane.Ordnance = new[] { 3, 0, 0, 0, 11, 0, 0, 0 };

        Assert.True(page.Accept(1));

        var exported = planes.Load("Gypsy Magic");
        Assert.NotNull(exported);
        Assert.Equal(new[] { 2, 1, 0, 4 }, exported!.Ammo);
        Assert.Equal(new[] { 3, 0, 0, 0, 11, 0, 0, 0 }, exported.Ordnance);
        // ⚠ langui 702's placeholder is the airframe's title, not the plane's name: the original
        // answers "Your Devastator has been exported" for an aircraft named "The Knave".
        Assert.Contains("Airframe 5", flow.Modal!.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Gypsy Magic", flow.Modal!.Message, System.StringComparison.Ordinal);
        Assert.Contains("exported", flow.Modal!.Message, System.StringComparison.Ordinal);
        // The export notice is PLANESELECTION.SCRIPT's 0x1 mask, so it draws the warning and not
        // the query the reference shot rules out.
        Assert.Equal(DialogIcon.Warning, flow.Modal!.Icon);
        Assert.Equal((int)DialogIcon.Warning, DialogIconTests.IconFrame(CampaignBoards.Dialog(flow.Modal)));
    }

    /// <summary>A starter or a granted aircraft has no record at all, so exporting creates one on
    /// its own airframe rather than refusing.</summary>
    [Fact]
    public void ExportingAPlaneWithNoBuildCreatesOneOnItsAirframe()
    {
        var page = NewPage(out var flow, out var planes, wingman: false);
        flow.Profile!.Planes[0].Ammo = new[] { 1, 1, 1, 1 };

        Assert.True(page.Accept(1));

        var exported = Assert.Single(planes.List());
        Assert.Equal("Gypsy Magic", exported.Name);
        Assert.Equal(5, exported.Airframe);
    }

    /// <summary>Export sets the loadout and leaves the build alone, or a
    /// hangar plane loses its paint, armour and engine the first time the campaign exports it.</summary>
    [Fact]
    public void ExportLeavesAnExistingBuildsPaintArmourAndEngineAlone()
    {
        var page = NewPage(out var flow, out var planes, wingman: false);
        planes.Save(new CustomPlaneDef
        {
            Name = "Gypsy Magic",
            Airframe = 5,
            Engine = 3,
            ArmourNose = 7,
            PaintPattern = 9,
        });
        flow.Profile!.Planes[0].Ammo = new[] { 3, 3, 3, 3 };

        Assert.True(page.Accept(1));

        var exported = planes.Load("Gypsy Magic")!;
        Assert.Equal(3, exported.Engine);
        Assert.Equal(7, exported.ArmourNose);
        Assert.Equal(9, exported.PaintPattern);
        Assert.Equal(new[] { 3, 3, 3, 3 }, exported.Ammo);
    }

    /// <summary>With no build store bound there is nowhere to export to, which is a refusal rather
    /// than a modal claiming a write that never happened.</summary>
    [Fact]
    public void ExportWithNoBuildStoreIsRefused()
    {
        var page = NewPage(out var flow, wingman: false);

        Assert.False(page.Accept(1));
        Assert.Null(flow.Modal);
    }

    /// <summary>A guest's picker is one PILOT slot over that guest's own roster, the stock airframes
    /// first and the seated profile's aircraft copied after them, and it draws no EXPORT: a guest
    /// flies a session copy carrying the owner's plane name.</summary>
    [Fact]
    public void AGuestsPickerIsOneSlotOverTheirOwnRosterAndDrawsNoExport()
    {
        var page = GuestPage(out _, players: 2);

        Assert.Equal(1, page.ActiveSlots);
        Assert.Equal(3, page.RowCount); // pick, accept, cancel
        Assert.Equal("ACCEPT SELECTIONS", page.RowText(1));
        Assert.Equal("CANCEL SELECTIONS", page.RowText(2));
        for (int row = 0; row < page.RowCount; row++)
        {
            Assert.NotEqual(BoardButton.ExportPlane, page.Button(row).Button);
        }

        var combo = page.Combo(0)!;
        Assert.Equal(14, combo.Entries.Count); // 11 stock airframes + the profile's three
        Assert.Equal("Airframe 0", combo.Entries[0]);
        Assert.Equal("Gypsy Magic - Airframe 5", combo.Entries[11]);
        Assert.Equal(5, combo.Selected); // the starter airframe the guest opened on
    }

    /// <summary>Langui 710 names a Pilot and a Wingman a guest's check has no concept
    /// of, so the refusal is a line of ours. The pick reverts, exactly as the seated player's does.</summary>
    [Fact]
    public void AGuestPickingTheSeatedPlayersAircraftIsRefusedInItsOwnWordsAndReverts()
    {
        var page = GuestPage(out var flow, players: 2);
        var combo = page.Combo(0)!;

        Assert.True(page.Accept(0));
        while (combo.Highlight != 11)
        {
            combo.Move(1);
        }

        Assert.True(page.Accept(0));

        Assert.Equal("Each player must fly a different plane.", flow.Modal?.Message);
        Assert.Equal(5, combo.Selected);
        Assert.False(combo.Open);
    }

    /// <summary>The other half of the same rule: what another guest took is refused too, which is
    /// the comparison only <c>CampaignFlightField</c> can make.</summary>
    [Fact]
    public void AGuestPickingWhatAnotherGuestFliesIsRefusedAndReverts()
    {
        var page = GuestPage(out var flow, players: 3);
        var combo = page.Combo(0)!;

        Assert.Equal(6, combo.Selected); // P2 opened on stock airframe 5, so P3 opened past it

        Assert.True(page.Step(0, -1));

        Assert.Equal("Each player must fly a different plane.", flow.Modal?.Message);
        Assert.Equal(6, combo.Selected);
    }

    /// <summary>⚠ A guest's ACCEPT moves their own session-scoped pick and touches neither the
    /// seated profile in memory nor the file on disk.</summary>
    [Fact]
    public void AGuestsAcceptedPickMovesTheGuestAndLeavesTheSeatedProfileAlone()
    {
        var page = GuestPage(out var flow, players: 2);

        Assert.True(page.Step(0, 1));
        Assert.True(page.Accept(1)); // ACCEPT SELECTIONS

        var flown = flow.Field.Plane(1)!;
        Assert.Equal(6, flown.Airframe);
        Assert.True(flow.Field.IsStock(flown));
        Assert.Equal(0, flow.Profile!.SelectedPlane);
        Assert.Equal(1, flow.Profile!.WingmanPlane);
        Assert.Equal(0, flow.Store.Load("Zachary")!.SelectedPlane);
        Assert.Equal(CampaignScreen.FlightCheck, flow.Screen);
    }

    /// <summary>CANCEL on a guest's screen is the same restore the seated player's is: the pick the
    /// screen opened with, and nothing written.</summary>
    [Fact]
    public void CancelOnAGuestsPickerLeavesThemFlyingWhatTheyFlew()
    {
        var page = GuestPage(out var flow, players: 2);

        Assert.True(page.Step(0, 1));
        Assert.True(page.Accept(2)); // CANCEL SELECTIONS

        Assert.Equal(5, flow.Field.Plane(1)!.Airframe);
        Assert.Equal(5, page.Combo(0)!.Selected);
    }

    // The Devastator's shipped stock fit, the one the two profile-seeded starters fly: three twin
    // wing mounts and four hardpoints.
    private static LoadoutDef DevastatorStock()
    {
        var def = new LoadoutDef { Def = "pdevastator", Model = "player_pfighter" };
        int[] calibres = { 50, 40, 30 };
        for (int slot = 0; slot < calibres.Length; slot++)
        {
            def.Guns.Add(new GunSpec
            {
                Slot = slot + 1,
                Caliber = calibres[slot],
                Markers = { "a", "b" },
            });
        }

        def.Hardpoints = new HardpointSpec { Count = 4 };
        return def;
    }

    // The picker for the last joined player, the flight-check walk having reached them. The
    // mission carries a wingman, so a seated screen here would draw two slots and two EXPORTs.
    private static CampaignPlaneSelectionPage GuestPage(out CampaignFlow flow, int players)
    {
        var page = NewPage(out flow, wingman: true);
        flow.SetPlayers(players);
        for (int i = 1; i < players; i++)
        {
            flow.Field.Advance();
        }

        return page;
    }

    private static CampaignPlaneSelectionPage NewPage(out CampaignFlow flow, bool wingman) =>
        NewPage(out flow, out _, wingman, withPlanes: false);

    private static CampaignPlaneSelectionPage NewPage(
        out CampaignFlow flow, out CustomPlaneStore planes, bool wingman) =>
        NewPage(out flow, out planes, wingman, withPlanes: true);

    private static CampaignPlaneSelectionPage NewPage(
        out CampaignFlow flow, out CustomPlaneStore planes, bool wingman, bool withPlanes)
    {
        string root = TestData.TempDir();
        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        planes = new CustomPlaneStore(Path.Combine(root, "Planes"));
        flow = new CampaignFlow(store, UiStrings.Empty, planes: withPlanes ? planes : null);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Blue Streak", Airframe = 3 });
        store.Save(profile);
        flow.SelectProfile(profile);
        flow.SetPlaneSlot(0);
        flow.GoTo(CampaignScreen.PlaneSelection);
        return new CampaignPlaneSelectionPage(flow, wingman);
    }
}
