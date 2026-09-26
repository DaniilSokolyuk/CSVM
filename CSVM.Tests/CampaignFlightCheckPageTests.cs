using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The flight check screen: row composition for the pilot's plane with and without a
/// wingman, the plane-change gate on ordinals 13/17 and under three planes, CHANGE PLANE opening the
/// picker on its own slot, the ammo route setting the flow's slot, and the fly request.</summary>
public class CampaignFlightCheckPageTests
{
    private static readonly StockLoadouts Stock =
        StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));

    [Fact]
    public void ThePilotRowShowsTheSelectedPlaneAndItsChangeAmmoRow()
    {
        var page = NewPage(out _, out _, wingman: false);

        Assert.StartsWith("PILOT", page.RowText(0));
        Assert.Contains("Gypsy Magic", page.RowText(0));
        Assert.Contains("1)", page.Detail(0));
        Assert.Equal("CHANGE AMMO", page.RowText(1));
    }

    // The crew headings are the aircraft's name written where the layout puts it, not a control:
    // the cursor reaches CHANGE AMMO and CHANGE PLANE under them and never the heading itself.
    [Fact]
    public void TheCrewHeadingRowsTakeNoFocus()
    {
        var page = NewPage(out _, out _, wingman: true);

        for (int row = 0; row < page.RowCount; row++)
        {
            string text = page.RowText(row);
            bool heading = text.StartsWith("PILOT") || text.StartsWith("WINGMAN");
            Assert.Equal(!heading, page.Focusable(row));
        }
    }

    [Fact]
    public void WithNoWingmanFlagTheWingmanRowsAreAbsent()
    {
        var page = NewPage(out _, out _, wingman: false);

        for (int row = 0; row < page.RowCount; row++)
        {
            Assert.False(page.RowText(row).StartsWith("WINGMAN"));
        }

        Assert.Equal("RETURN TO BRIEFING", page.RowText(page.RowCount - 2));
        Assert.Equal("FLY MISSION", page.RowText(page.RowCount - 1));
    }

    [Fact]
    public void WithTheWingmanFlagSetTheWingmanBlockAndItsChangeAmmoRowAppear()
    {
        var page = NewPage(out _, out _, wingman: true);

        bool sawWingman = false;
        int changeAmmoCount = 0;
        for (int row = 0; row < page.RowCount; row++)
        {
            string text = page.RowText(row);
            if (text.StartsWith("WINGMAN"))
            {
                sawWingman = true;

                // A fresh profile flies the pilot in Gypsy Magic and the wingman in The Knave,
                // which is what NewProfile's WingmanPlane of 1 names.
                Assert.Contains("The Knave", text);
            }

            if (text == "CHANGE AMMO")
            {
                changeAmmoCount++;
            }
        }

        Assert.True(sawWingman);
        Assert.Equal(2, changeAmmoCount);
    }

    [Fact]
    public void EightRowGunAndRocketListsBlankRatherThanPad()
    {
        var page = NewPage(out _, out _, wingman: false);

        // The Devastator's stock fit (loadouts.md) fills all 6 barrel rows but only pylons
        // 1, 5, 2, 6 (Loadout.PylonFillOrder), so rocket rows 3, 4, 7, 8 stay bare "n)".
        string block = page.Detail(0);
        var lines = block.Split('\n');
        Assert.Equal(8, lines.Length);
        Assert.Equal("3)", lines[2].Split("    ")[1]);
        Assert.Equal("4)", lines[3].Split("    ")[1]);
        Assert.Equal("7)", lines[6].Split("    ")[1]);
        Assert.Equal("8)", lines[7].Split("    ")[1]);
        Assert.Contains(".50-cal", lines[0]);
    }

    // A gun row is the calibre short name and the ammunition, nothing else. The maker's name
    // (IDS_GUNLONGNAME, langui 3310) belongs to the hangar and the ammo screen; the flight check
    // reads IDS_GUNSHORTNAME at 3320, whose string owns the leading space the row draws.
    [Fact]
    public void AGunRowIsTheCalibreShortNameAndTheAmmunitionAndNothingElse()
    {
        var page = NewPage(out _, out _, wingman: false);

        var lines = page.Detail(0).Split('\n');
        Assert.Equal("1) .50-cal. Slug", lines[0].Split("    ")[0]);
        Assert.Equal("7)", lines[6].Split("    ")[0]);
    }

    // The ammo screen and the flight check must read one plane's stored picks the same way. The
    // stored pylon value is one-based with 0 meaning "never picked" (CampaignLoadout.PylonRow), so
    // reading it as a plain rocket-table row drew a pylon the player never touched as
    // armor-piercing and shifted every deliberate pick by one.
    [Fact]
    public void TheRowsShowWhatTheAmmoScreenJustCommitted()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        var store = new CampaignProfileStore(dir);
        store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var flow = new CampaignFlow(store, UiStrings.Empty);
        flow.SelectProfile(store.Load("Zachary")!);
        flow.SetMission(0);
        var planes = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "Planes"));
        var page = new CampaignFlightCheckPage(flow, planes, Stock, wingman: false);
        var ammo = new CampaignAmmoPage(flow, planes, Stock);
        flow.SetAmmoSlot(0);

        // An untouched pylon is the universal high-explosive stock fit on both screens.
        Assert.Equal("High explosive", ammo.RowText(4));
        Assert.Contains("1)High explosive", page.Detail(0));

        // Group 0 to armor-piercing, pylon 0 back one row to armor-piercing, then ACCEPT LOADOUT.
        Assert.True(ammo.Step(0, 2));
        Assert.True(ammo.Step(4, -1));
        string pylonPick = ammo.RowText(4);
        Assert.Equal("Armor-piercing", pylonPick);
        Assert.True(ammo.Accept(12));

        var lines = page.Detail(0).Split('\n');
        Assert.Equal("1) .50-cal. Armor-piercing", lines[0].Split("    ")[0]);
        Assert.Equal("1)" + pylonPick, lines[0].Split("    ")[1]);
    }

    [Theory]
    [InlineData(12, 3, true)]   // ordinal 13
    [InlineData(16, 3, true)]   // ordinal 17
    [InlineData(11, 3, false)]
    [InlineData(15, 3, false)]
    [InlineData(0, 2, true)]    // under three planes
    public void ChangePlaneIsBarredOnOrdinals13And17AndUnderThreePlanes(int seq, int planeCount, bool barred)
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int i = profile.Planes.Count; i < planeCount; i++)
        {
            profile.Planes.Add(new OwnedPlane { Name = $"Extra {i}", Airframe = 5 });
        }

        var page = NewPage(out _, out _, wingman: false, profile: profile, missionSeq: seq);

        bool hasChangePlane = false;
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.RowText(row).StartsWith("CHANGE PLANE"))
            {
                hasChangePlane = true;
            }
        }

        Assert.Equal(!barred, hasChangePlane);
    }

    // The seated player's CHANGE PLANE is a plain button onto the picker, named for its slot. The
    // horizontal axis does nothing on it, and the label names no plane.
    [Fact]
    public void ChangePlaneOpensThePickerOnItsOwnSlotAndNoLongerSteps()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Third Plane", Airframe = 5 });
        var page = NewPage(out var flow, out _, wingman: true, profile: profile, missionSeq: 0);

        int wingmanRow = LastChangePlaneRow(page);
        Assert.Equal("CHANGE PLANE", page.RowText(wingmanRow));
        Assert.False(page.Step(wingmanRow, 1));
        Assert.Equal(0, flow.Profile!.SelectedPlane);
        Assert.Equal(1, flow.Profile.WingmanPlane);

        Assert.True(page.Accept(wingmanRow));
        Assert.Equal(CampaignScreen.PlaneSelection, flow.Screen);
        Assert.Equal(1, flow.PlaneSlot);
    }

    // What the stepper protected is still protected: a plane change lands on the profile and is
    // saved. It happens on the picker's ACCEPT now, not on the flight check's own row.
    [Fact]
    public void APickTakenOnThePickerLandsOnTheProfileAndIsSaved()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Third Plane", Airframe = 5 });
        var page = NewPage(out var flow, out _, wingman: false, profile: profile, missionSeq: 0);

        Assert.True(page.Accept(LastChangePlaneRow(page)));
        Assert.Equal(0, flow.PlaneSlot);

        var picker = new CampaignPlaneSelectionPage(flow, wingman: false);
        Assert.True(picker.Step(0, 1));
        Assert.True(picker.Accept(2)); // ACCEPT SELECTIONS

        Assert.Equal(1, flow.Profile!.SelectedPlane);
        Assert.Equal(1, flow.Store.Load("Zachary")!.SelectedPlane);
        Assert.Equal(CampaignScreen.FlightCheck, flow.Screen);
    }

    // The footer stops offering a change the screen no longer makes. Read off the flow's own page,
    // since the hint is a statement about the focused row.
    [Fact]
    public void TheFooterNoLongerOffersTheHorizontalChangeOnTheSeatedPlayersRow()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Third Plane", Airframe = 5 });
        NewPage(out var flow, out _, wingman: false, profile: profile, missionSeq: 0);
        flow.GoTo(CampaignScreen.FlightCheck);

        for (int i = 0; i < flow.Page.RowCount && flow.Page.RowText(flow.Row) != "CHANGE PLANE"; i++)
        {
            flow.Move(1);
        }

        Assert.Equal("CHANGE PLANE", flow.Page.RowText(flow.Row));
        Assert.DoesNotContain("Change Plane", flow.Page.Footer);
        Assert.False(flow.Step(1));
    }

    [Fact]
    public void ChangeAmmoSetsTheFlowsSlotAndOpensAmmo()
    {
        var page = NewPage(out var flow, out _, wingman: true);

        int wingmanChangeAmmoRow = -1;
        bool first = true;
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.RowText(row) != "CHANGE AMMO")
            {
                continue;
            }

            if (first)
            {
                first = false;
                continue;
            }

            wingmanChangeAmmoRow = row;
            break;
        }

        Assert.True(wingmanChangeAmmoRow >= 0);
        Assert.True(page.Accept(wingmanChangeAmmoRow));
        Assert.Equal(CampaignScreen.Ammo, flow.Screen);
        Assert.Equal(1, flow.AmmoSlot);
    }

    [Fact]
    public void FlyMissionRequestsTheExit()
    {
        var page = NewPage(out var flow, out _, wingman: false);

        Assert.True(page.Accept(page.RowCount - 1));
        Assert.Equal(CampaignExit.FlyMission, flow.Exit);
    }

    [Fact]
    public void ReturnToBriefingNavigatesBack()
    {
        var page = NewPage(out var flow, out _, wingman: false);
        flow.SetPlayers(2);
        flow.Field.Advance();

        Assert.True(flow.Field.Locked);

        Assert.True(page.Accept(page.RowCount - 2));
        Assert.Equal(CampaignScreen.Briefing, flow.Screen);
        Assert.False(flow.Field.Locked);
    }

    // C22: FLY MISSION advances to the next joined player's check and only the last one launches,
    // and the screen says whose turn it is, since the whole walk happens in one window.
    [Fact]
    public void FlyMissionWalksEveryJoinedPlayerBeforeAskingForTheLaunch()
    {
        var page = NewPage(out var flow, out _, wingman: false);
        flow.SetPlayers(3);
        flow.GoTo(CampaignScreen.FlightCheck);

        Assert.Equal("FLIGHT CHECK", page.Heading);
        Assert.True(page.Accept(page.RowCount - 1));
        Assert.Equal(CampaignExit.None, flow.Exit);
        Assert.Equal("FLIGHT CHECK P2", page.Heading);

        Assert.True(page.Accept(page.RowCount - 1));
        Assert.Equal(CampaignExit.None, flow.Exit);
        Assert.Equal("FLIGHT CHECK P3", page.Heading);

        Assert.True(page.Accept(page.RowCount - 1));
        Assert.Equal(CampaignExit.FlyMission, flow.Exit);
    }

    // A guest's page is one PILOT block and its two action rows: the wingman belongs to the seated
    // profile, and CHANGE PLANE is always offered because a guest picks out of their own roster.
    [Fact]
    public void AGuestsPageCarriesOnePilotBlockAndNoWingman()
    {
        var page = NewPage(out var flow, out _, wingman: true);
        flow.SetPlayers(2);
        flow.Field.Advance();

        Assert.Equal(5, page.RowCount);
        Assert.StartsWith("PILOT", page.RowText(0));
        Assert.Equal("CHANGE AMMO", page.RowText(1));
        Assert.StartsWith("CHANGE PLANE", page.RowText(2));
        Assert.Equal("RETURN TO BRIEFING", page.RowText(3));
        Assert.Equal("FLY MISSION", page.RowText(4));
    }

    [Fact]
    public void TheSeatedPlayersOwnPageIsUnchangedWithGuestsJoined()
    {
        var solo = NewPage(out _, out _, wingman: true);
        var page = NewPage(out var flow, out _, wingman: true);
        flow.SetPlayers(3);

        Assert.Equal(solo.RowCount, page.RowCount);
        for (int row = 0; row < page.RowCount; row++)
        {
            Assert.Equal(solo.RowText(row), page.RowText(row));
        }
    }

    // Back on a guest's check is the inverse of FLY MISSION; on the seated player's own it is
    // unconsumed, so the flow leaves the screen the way it always did.
    [Fact]
    public void BackWalksTheSequenceInReverseAndThenLeavesTheScreen()
    {
        var page = NewPage(out var flow, out _, wingman: false);
        flow.SetPlayers(2);
        flow.Field.Advance();

        Assert.True(page.Back());
        Assert.Equal(0, flow.Field.Current);
        Assert.True(flow.Field.Locked);
        Assert.False(page.Back());
        Assert.False(flow.Field.Locked);
    }

    // A guest's CHANGE PLANE takes the same door the seated player's does, on its own slot. The
    // horizontal axis does nothing on either: one screen changes a plane, so one place enforces the
    // duplicate rule.
    [Fact]
    public void AGuestsChangePlaneOpensThePickerAndStepsNothingInPlace()
    {
        var page = NewPage(out var flow, out _, wingman: false);
        flow.SetPlayers(2);
        flow.Field.Advance();
        flow.SetPlaneSlot(1);
        var before = flow.Field.Plane(1)!;

        Assert.Equal("CHANGE PLANE", page.RowText(2));
        Assert.False(page.Step(2, 1));
        Assert.Same(before, flow.Field.Plane(1));

        Assert.True(page.Accept(2));
        Assert.Equal(CampaignScreen.PlaneSelection, flow.Screen);
        Assert.Equal(0, flow.PlaneSlot);
        Assert.Equal(0, flow.Store.Load("Zachary")!.SelectedPlane);
    }

    // A custom-built plane's own guns/hardpoints, not the airframe's stock fit, drive the lists
    // when the plane's name has a CustomPlaneStore entry.
    [Fact]
    public void ACustomBuildsOwnGunsOverrideTheAirframesStockFit()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        string planesDir = Path.Combine(TestData.TempDir(), "Planes");
        var store = new CampaignProfileStore(dir);
        var planes = new CustomPlaneStore(planesDir);

        var custom = new CustomPlaneDef { Name = "Gypsy Magic", Airframe = 5, LeftHardpoints = 1, RightHardpoints = 0 };
        custom.Guns[0] = new GunChoice(0, Twin: false); // .30-cal, single
        planes.Save(custom);

        var profile = CampaignProfileDef.NewProfile("Zachary");
        store.Save(profile);

        var flow = new CampaignFlow(store, UiStrings.Empty);
        flow.SelectProfile(store.Load("Zachary")!);
        flow.SetMission(0);
        var page = new CampaignFlightCheckPage(flow, planes, Stock, wingman: false);

        string block = page.Detail(0);
        var lines = block.Split('\n');

        // Row 1 (group 0, single .30-cal) has content; row 2 (the same group's second barrel) is
        // blank because the custom build is not twinned, unlike the Devastator's stock fit.
        Assert.Contains(".30-cal", lines[0]);
        Assert.Equal("2)", lines[1].Split("    ")[0]);
    }

    // Real-data checks: the wingman flag reads from cm_sequence.zrd (not a test override) and the
    // objectives note reads from the mission's own objectives.zrd, both through Flow.DataRoot.
    [ExtractedDataFact]
    public void TheWingmanFlagAgreesWithCampaignSequenceForARealMission()
    {
        var missions = CampaignSequence.Load(
            SessionPaths.PreferUnzipped(Path.Combine(TestData.DataRoot!, "extracted", "zrdr.zip")));
        int withWingman = -1;
        int withoutWingman = -1;
        foreach (var m in missions)
        {
            if (m.Wingman && withWingman < 0)
            {
                withWingman = m.Seq;
            }

            if (!m.Wingman && withoutWingman < 0)
            {
                withoutWingman = m.Seq;
            }
        }

        Assert.True(withWingman >= 0);
        Assert.True(withoutWingman >= 0);
        Assert.True(HasWingmanForReal(withWingman));
        Assert.False(HasWingmanForReal(withoutWingman));
    }

    // The note is a caption at the authored geometry of fc_t_objtitle and fc_t_objectives, not a
    // row and not the focused row's description: it is the screen's own parchment down the right.
    [ExtractedDataFact]
    public void TheObjectivesNoteIsWrittenOnTheParchmentAtItsAuthoredPosition()
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        var store = new CampaignProfileStore(dir);
        var def = CampaignProfileDef.NewProfile("Zachary");
        store.Save(def);

        var flow = new CampaignFlow(store, UiStrings.Empty, TestData.DataRoot);
        flow.SelectProfile(store.Load("Zachary")!);
        flow.SetMission(0);
        var planes = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "Planes"));
        var page = new CampaignFlightCheckPage(flow, planes, Stock);

        BoardLine? title = null;
        BoardLine? note = null;
        foreach (var line in page.Captions)
        {
            if (line.X == 558f && line.Y == 80f)
            {
                title = line;
            }

            if (line.X == 554f && line.Y == 120f)
            {
                note = line;
            }
        }

        Assert.NotNull(title);
        Assert.True(title!.Italic);
        Assert.NotNull(note);
        Assert.True(note!.Italic);
        Assert.Equal(206f, note.Width);
        Assert.StartsWith("1) ", note.Text);
        Assert.Contains("\n2) ", note.Text);

        // The description panel carries the plane's own loadout block and nothing else; the note
        // has a place of its own now, so it is not appended there.
        Assert.DoesNotContain(note.Text, page.Detail(0));
    }

    // A separate page/flow pair with a real DataRoot, isolated from the no-DataRoot fixtures
    // above, so the wingman flag test above reads Mission() rather than the test override.
    private static bool HasWingmanForReal(int seq)
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        var store = new CampaignProfileStore(dir);
        var def = CampaignProfileDef.NewProfile("Zachary");
        store.Save(def);

        var flow = new CampaignFlow(store, UiStrings.Empty, TestData.DataRoot);
        flow.SelectProfile(store.Load("Zachary")!);
        flow.SetMission(seq);
        var planes = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "Planes"));
        var page = new CampaignFlightCheckPage(flow, planes, Stock);

        bool sawWingman = false;
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.RowText(row).StartsWith("WINGMAN"))
            {
                sawWingman = true;
            }
        }

        return sawWingman;
    }

    // The last CHANGE PLANE row on the page, which is the wingman's where the mission has one.
    private static int LastChangePlaneRow(CampaignFlightCheckPage page)
    {
        int found = -1;
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.RowText(row) == "CHANGE PLANE")
            {
                found = row;
            }
        }

        Assert.True(found >= 0);
        return found;
    }

    private static CampaignFlightCheckPage NewPage(
        out CampaignFlow flow, out string dir, bool wingman,
        CampaignProfileDef? profile = null, int missionSeq = 0)
    {
        dir = Path.Combine(TestData.TempDir(), "Profiles");
        var store = new CampaignProfileStore(dir);
        var def = profile ?? CampaignProfileDef.NewProfile("Zachary");
        store.Save(def);

        // flow.GoTo(CampaignScreen.FlightCheck) is deliberately not called: the flow's own
        // registry-built page (no test seams) would touch Godot.ProjectSettings outside the
        // engine. The page under test is a second instance built with explicit stores below.
        flow = new CampaignFlow(store, UiStrings.Empty);
        flow.SelectProfile(store.Load(def.Name)!);
        flow.SetMission(missionSeq);

        var planes = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "Planes"));
        return new CampaignFlightCheckPage(flow, planes, Stock, wingman);
    }
}
