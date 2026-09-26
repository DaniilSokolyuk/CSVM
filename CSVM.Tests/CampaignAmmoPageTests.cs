using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The ammo selection screen: four gun-group picks and eight pylon picks derived from a
/// plane's build (hangar-built or the airframe's stock fit), the progress-threshold ordnance
/// filter, and the working-copy commit ACCEPT/CANCEL implement. Tests drive the page directly
/// (not through <see cref="CampaignFlow"/>'s row dispatch) so the injected
/// <see cref="CustomPlaneStore"/>/<see cref="StockLoadouts"/> stay engine-free.</summary>
public class CampaignAmmoPageTests
{
    private const int AcceptRow = 12;
    private const int CancelRow = 13;

    private static readonly string StockLoadoutsPath =
        Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json");

    [Fact]
    public void GroupsAndPylonsDeriveFromTheBuiltPlane()
    {
        var (flow, page, planes) = NewFlow(out string profileDir);
        var built = new CustomPlaneDef { Name = "Built1", Airframe = 5, LeftHardpoints = 1, RightHardpoints = 3 };
        built.Guns[0] = new GunChoice(2, Twin: false);
        planes.Save(built);

        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Built1", Airframe = 5 });
        profile.SelectedPlane = 0;
        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);

        Assert.Contains("Slug", page.RowText(0));
        Assert.Null(page.Combo(1));
        Assert.Null(page.Combo(2));
        Assert.Null(page.Combo(3));
        Assert.Equal(3, CaptionsSaying(page, "No Gun").Count);

        Assert.NotNull(page.Combo(4)); // left cell 0, LeftHardpoints=1
        Assert.Null(page.Combo(5));    // left cell 1, inactive
        Assert.NotNull(page.Combo(8));  // right cell 0, RightHardpoints=3
        Assert.NotNull(page.Combo(9));  // right cell 1
        Assert.NotNull(page.Combo(10)); // right cell 2
        Assert.Null(page.Combo(11));    // right cell 3, inactive

        Directory.Delete(profileDir, true);
    }

    [Fact]
    public void AStarterPlaneReadsTheAirframesStockFit()
    {
        // The two profile-seeded starters never touch CustomPlaneStore (B13): the Devastator
        // (airframe 5) stock fit is three guns and 2+2 hardpoints (loadouts.md's stock table).
        var (flow, page, _) = NewFlow(out string profileDir);
        var profile = CampaignProfileDef.NewProfile("Zachary"); // "Gypsy Magic" / "The Knave", airframe 5
        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);

        Assert.NotNull(page.Combo(0));
        Assert.NotNull(page.Combo(1));
        Assert.NotNull(page.Combo(2));
        Assert.Null(page.Combo(3));
        Assert.Single(CaptionsSaying(page, "No Gun"));

        Assert.NotNull(page.Combo(4));  // left cell 0
        Assert.NotNull(page.Combo(5));  // left cell 1
        Assert.Null(page.Combo(6));     // left cell 2, only 2 hardpoints per wing
        Assert.NotNull(page.Combo(8));  // right cell 0
        Assert.NotNull(page.Combo(9));  // right cell 1
        Assert.Null(page.Combo(10));    // right cell 2

        Directory.Delete(profileDir, true);
    }

    [Fact]
    public void SteppingAGreyedNoGunGroupIsANoOp()
    {
        var (flow, page, planes) = NewFlow(out string profileDir);
        var built = new CustomPlaneDef { Name = "Built2", Airframe = 5 }; // no guns at all
        planes.Save(built);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Built2", Airframe = 5 });
        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);

        Assert.False(page.Step(0, 1));
        Assert.Null(page.Combo(0));
        Assert.Equal(4, CaptionsSaying(page, "No Gun").Count);

        Directory.Delete(profileDir, true);
    }

    [Fact]
    public void TheOrdnanceFilterHonoursTheProgressThresholdTableOnAReplay()
    {
        var (flow, page, planes) = NewFlow(out string profileDir);
        var built = new CustomPlaneDef { Name = "Built3", Airframe = 5, LeftHardpoints = 1 };
        planes.Save(built);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Built3", Airframe = 5 });
        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);

        // A profile that has flown nothing on the campaign's first mission: only AP, HE and None
        // (thresholds 1) may appear on cell 0, and the torpedo row is not among them.
        flow.SetMission(0);
        Assert.Equal(3, page.Combo(4)!.Entries.Count);
        Assert.DoesNotContain("Torpedo", page.Combo(4)!.Entries);
        for (int i = 0; i < 12; i++)
        {
            page.Step(4, 1);
            string label = page.RowText(4);
            Assert.True(label is "Armor-piercing" or "High explosive" or "None",
                $"unlocked too early at ordinal 1: {label}");
        }

        // Nineteen missions completed is ordinal 20, where torpedoes unlock. The sortie stays the
        // campaign's first mission, so this is the replay case: the offer follows the progress.
        profile.MissionsCompleted = 19;
        bool sawTorpedo = false;
        for (int i = 0; i < 12; i++)
        {
            page.Step(4, 1);
            if (page.RowText(4) == "Torpedo")
            {
                sawTorpedo = true;
            }
        }

        Assert.Equal(0, flow.MissionSeq);
        Assert.True(sawTorpedo, "torpedoes should be reachable at ordinal 20");

        Directory.Delete(profileDir, true);
    }

    [Fact]
    public void AcceptPersistsAndCancelLeavesTheStoredFitUntouched()
    {
        var (flow, page, planes) = NewFlow(out string profileDir);
        var built = new CustomPlaneDef { Name = "Built4", Airframe = 5, LeftHardpoints = 1 };
        built.Guns[0] = new GunChoice(0, Twin: false);
        planes.Save(built);

        var store = new CampaignProfileStore(profileDir);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Built4", Airframe = 5 });
        store.Save(profile);

        flow.SelectProfile(profile);
        flow.SetAmmoSlot(0);

        // Edit, then CANCEL: the stored file must not move.
        page.Step(0, 1); // ammo index 0 -> 1
        page.Step(4, 1); // pylon cell 0 to the next available ordnance
        Assert.True(page.Accept(CancelRow));

        var afterCancel = store.Load("Zachary")!;
        Assert.Equal(0, afterCancel.Planes[0].Ammo[0]);
        Assert.Equal(0, afterCancel.Planes[0].Ordnance[0]);

        // Re-entering the same target must read the still-unedited stored fit, not the discard.
        Assert.Contains("Slug", page.RowText(0));

        // Edit again, then ACCEPT: the stored file must carry the edit.
        page.Step(0, 1);
        Assert.True(page.Accept(AcceptRow));

        var afterAccept = store.Load("Zachary")!;
        Assert.Equal(1, afterAccept.Planes[0].Ammo[0]);

        Directory.Delete(profileDir, true);
    }

    [Fact]
    public void TheWingmanSlotEditsTheWingmansPlaneNotThePilots()
    {
        var (flow, page, planes) = NewFlow(out string profileDir);
        var pilotPlane = new CustomPlaneDef { Name = "Pilot", Airframe = 5 };
        pilotPlane.Guns[0] = new GunChoice(0, Twin: false); // gun mounted: reads an ammo pick
        var wingmanPlane = new CustomPlaneDef { Name = "Wingman", Airframe = 5 }; // default: no gun, reads "No Gun"
        planes.Save(pilotPlane);
        planes.Save(wingmanPlane);
        planes.Save(wingmanPlane);

        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "Pilot", Airframe = 5 });
        profile.Planes.Add(new OwnedPlane { Name = "Wingman", Airframe = 5 });
        profile.SelectedPlane = 0;
        profile.WingmanPlane = 1;
        flow.SelectProfile(profile);

        flow.SetAmmoSlot(0);
        string pilotAmmo = page.RowText(0);

        flow.SetAmmoSlot(1);
        string wingmanAmmo = page.RowText(0);

        Assert.NotEqual(pilotAmmo, wingmanAmmo);

        Directory.Delete(profileDir, true);
    }

    // ⚠ A guest's ammunition edits are free and discarded. ACCEPT writes their own
    // session-scoped record, and the seated profile's file is not rewritten at all.
    [Fact]
    public void AGuestsAcceptWritesTheirOwnRecordAndLeavesTheProfileFileUntouched()
    {
        var (flow, page, _) = NewFlow(out string profileDir);
        flow.Store.Save(CampaignProfileDef.NewProfile("Zachary"));
        flow.SelectProfile(flow.Store.Load("Zachary")!);
        flow.SetPlayers(2);
        flow.Field.Advance();
        string file = Path.Combine(profileDir, "Zachary", "profile.json");
        byte[] before = File.ReadAllBytes(file);

        Assert.True(page.Step(0, 2));
        Assert.True(page.Accept(AcceptRow));

        Assert.Equal(2, flow.Field.Plane(1)!.Ammo[0]);
        Assert.Equal(before, File.ReadAllBytes(file));

        Directory.Delete(profileDir, true);
    }

    // The caption is the group's calibre and the field is a drop-down under it, both at
    // [@OrdinanceLayout@]'s own coordinates (OL_T_GunName0 at 142,105 and OL_D_AMMO0 at 136,120,
    // DROPWIDTH 148, STDITEMH 15). Nothing may sit at the other's position.
    [Fact]
    public void CaptionsAndFieldsTakeTheirAuthoredPositions()
    {
        var (flow, page, _) = NewFlow(out string profileDir);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary")); // Devastator: three guns
        flow.SetAmmoSlot(0);

        var captions = CaptionsSaying(page, "-cal.");
        Assert.Equal(3, captions.Count);
        Assert.Equal(new[] { 105f, 147f, 189f }, captions.Select(c => c.Y));
        Assert.All(captions, c => Assert.Equal(142f, c.X));

        var field = page.Combo(0)!;
        Assert.Equal((136f, 120f, 148f, 15f), (field.X, field.Y, field.Width, field.RowHeight));
        Assert.Equal(204f, page.Combo(2)!.Y);  // OL_D_AMMO2, its caption fifteen pixels above it
        Assert.Equal(136f, page.Combo(4)!.X);  // OL_D_ROCKETS0, the left rocket column
        Assert.Equal(410f, page.Combo(8)!.X);  // OL_D_ROCKETS4, the right one
        Assert.Equal(new[] { 320f, 348f }, new[] { page.Combo(4)!.Y, page.Combo(5)!.Y });

        Directory.Delete(profileDir, true);
    }

    // The field opens over the screen, the axis moves inside it, and the confirm is
    // what writes the pick. The rocket list holds only what the profile's progress has unlocked.
    [Fact]
    public void ARocketFieldOpensItsListAndTheConfirmTakesThePick()
    {
        var (flow, page, _) = NewFlow(out string profileDir);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        flow.SetAmmoSlot(0);
        flow.SetMission(0); // a profile that has flown nothing: only the three threshold-1 rows

        var field = page.Combo(4)!;
        Assert.Equal(3, field.Entries.Count);
        Assert.False(field.Scrolls);

        Assert.True(page.Accept(4));
        Assert.True(field.Open);
        string before = field.Text;

        // The axis moves inside the open list, which is the seam the flow hands it through.
        Assert.True(field.Move(1));
        Assert.True(page.Accept(4));
        Assert.False(field.Open);
        Assert.NotEqual(before, field.Text);

        // Back on a closed field leaves the screen instead of collapsing anything.
        Assert.True(page.Accept(4));
        Assert.True(page.Back());
        Assert.False(field.Open);

        Directory.Delete(profileDir, true);
    }

    // [@OrdinanceLayout@] authors a description pane per half of the screen, each under its own
    // heading, and ORDINANCELAYOUT.SCRIPT's gui_init fills both at once: the half the cursor is not
    // in describes the first armed group or the first fitted pylon, and ACCEPT is in neither half.
    [Fact]
    public void BothDescriptionPanesFillAtOnce()
    {
        var (flow, page, _) = NewFlow(out string profileDir);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary")); // Devastator: three guns, two hardpoints a wing
        flow.SetAmmoSlot(0);

        Assert.Equal(0, page.DetailRow(BoardDetailPane.Upper, 0));
        Assert.Equal(4, page.DetailRow(BoardDetailPane.Lower, 0));
        Assert.Equal(0, page.DetailRow(BoardDetailPane.Upper, 5));
        Assert.Equal(5, page.DetailRow(BoardDetailPane.Lower, 5));
        Assert.Equal(0, page.DetailRow(BoardDetailPane.Upper, AcceptRow));
        Assert.Equal(4, page.DetailRow(BoardDetailPane.Lower, AcceptRow));

        // A pick row's own words reach a pane on either half; ACCEPT's hint reaches none, which is
        // what leaves it to the shell's hint band.
        Assert.True(CampaignBoards.DetailPaned(page, 0));
        Assert.True(CampaignBoards.DetailPaned(page, 5));
        Assert.False(CampaignBoards.DetailPaned(page, AcceptRow));

        var board = CampaignBoards.For(page, focusedRow: 5, detail: page.Detail(5));
        var panes = board.Lines.Where(l => l.X == 566f && (l.Y == 92f || l.Y == 328f)).OrderBy(l => l.Y).ToList();
        Assert.Equal(2, panes.Count);
        Assert.Equal(page.Detail(0), panes[0].Text);
        Assert.Equal(page.Detail(5), panes[1].Text);
        Assert.NotEqual(panes[0].Text, panes[1].Text);

        var headings = page.Captions.Where(c => c.Text.Contains("DESCRIPTION")).ToList();
        Assert.Equal(new[] { (566f, 76f), (566f, 312f) }, headings.Select(h => (h.X, h.Y)));

        Directory.Delete(profileDir, true);
    }

    // The lines the page draws itself, matched on their words.
    private static List<BoardLine> CaptionsSaying(CampaignAmmoPage page, string words)
    {
        var found = new List<BoardLine>();
        foreach (var line in page.Captions)
        {
            if (line.Text.Contains(words))
            {
                found.Add(line);
            }
        }

        return found;
    }

    private static (CampaignFlow Flow, CampaignAmmoPage Page, CustomPlaneStore Planes) NewFlow(out string profileDir)
    {
        profileDir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(profileDir);
        var planesDir = Path.Combine(TestData.TempDir(), "Planes");
        var planes = new CustomPlaneStore(planesDir);
        var stock = StockLoadouts.Load(StockLoadoutsPath);
        // The flow carries both stores so the flight check the page returns to resolves fits
        // off-engine too, the way the shell's flow does on-engine.
        var flow = new CampaignFlow(new CampaignProfileStore(profileDir), UiStrings.Empty, null, planes, stock);
        var page = new CampaignAmmoPage(flow, planes, stock);
        return (flow, page, planes);
    }
}
