using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Original hangar module over the hand-authored layout fixture and a hand-written host: the
/// name screen's typing and its two exits, the hub's tab bar as siblings, the dropdowns binding to
/// the shared feature, the defaults ask as a dialog, the totals page committing into a scratch
/// store, the inventory selling, and the cancel that leaves no residue. No <see cref="OriginalShell"/>
/// stands behind these; what the module asks of one (the screen, the focus, a dialog, a roster
/// re-read) the host records. The door from Instant Action and the keyboard's column walk are the
/// shell's and are in <see cref="OriginalShellTests"/>. Every rectangle here is the fixture's
/// invented geometry; the original's is read the same way.
/// </summary>
public class OriginalHangarTests : IDisposable
{
    // The name screen's own refusal, langui 203, which the empty string table falls back to.
    private const string EmptyNameRefusal = "You must enter a name for your new plane.";

    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public OriginalHangarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-original-hangar-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void TheDoorOpensTheNameScreenWhoseOkAnswersAnEmptyBoxWithAMessageBox()
    {
        var host = Host(out var hangar);
        host.Module.OpenHangar(null, HangarFeature.DefaultAirframe);

        Assert.Equal(OriginalScreen.PlaneName, host.Screen);
        Assert.True(hangar.IsOpen);
        Assert.True(host.Module.CapturingText);
        Assert.Equal(
            new[] { OriginalHangarScreen.NameFieldKey, OriginalHangarScreen.NameDefaultsKey, OriginalHangarScreen.NameOkKey, OriginalHangarScreen.NameCancelKey },
            host.Rows.Select(r => r.Key));
        Assert.True(host.Module.LoadDefaultsChecked);

        // OK stands on an empty box and the refusal is the press's answer, not a standing line.
        Assert.True(Row(host, OriginalHangarScreen.NameOkKey).Enabled);
        Assert.DoesNotContain(Compose(host).Lines, l => l.Text == EmptyNameRefusal);
        Click(host, OriginalHangarScreen.NameOkKey);
        Assert.Equal(EmptyNameRefusal, host.Dialog!.Message);
        Assert.Equal(DialogIcon.Warning, host.Dialog!.Icon);
        Assert.Equal(new[] { OriginalShell.DialogOkKey }, host.Dialog!.Answers.Select(a => a.Key));
        Assert.False(host.Module.CapturingText);
        Assert.Equal(OriginalScreen.PlaneName, host.Screen);

        // Its one OK puts the cursor back in the box, which is where the script leaves it.
        Click(host, OriginalShell.DialogOkKey);
        Assert.Null(host.Dialog);
        Assert.Equal(OriginalHangarScreen.NameFieldKey, host.FocusedKey);
        Assert.True(host.Module.CapturingText);

        Type(host, "Ace/1");
        Assert.Equal("Ace1", host.Module.HangarName);
        Erase(host);
        Assert.Equal("Ace", host.Module.HangarName);
        Assert.Equal(8, Row(host, OriginalHangarScreen.NameDefaultsKey).Art!.Frames);

        // The box writes its text alone and hangs the caret off it in its own CursorColor.
        var box = Row(host, OriginalHangarScreen.NameFieldKey);
        var typed = Compose(host).Lines.Single(l => l.Text == "Ace" && l.Caret != null);
        Assert.Equal(new BoardCaret(0xEF, 0x00, 0x10, 2f, box.Height - 2f), typed.Caret);
        Assert.DoesNotContain(Compose(host).Lines, l => l.Text.EndsWith('_'));
    }

    [Fact]
    public void TheNameDialogAndItsRowsRideThePaneCentredOnTheBoard()
    {
        var host = Host(out _);
        OpenName(host, "Ace");

        // The fixture's pane is 260x180 on the 800x600 board, so it centres at 270,210 and every
        // row and text of the section is drawn from that corner rather than from the screen's.
        var board = Compose(host);
        Assert.Contains(board.Backdrop, p => p.Art.Name == "PH_NamePanel.png" && p.X == 270f && p.Y == 210f);
        Assert.Contains(board.Backdrop, p => p.Art.Name == "PH_Back.jpg" && p.X == 0f && p.Y == 0f);
        var field = Row(host, OriginalHangarScreen.NameFieldKey);
        Assert.Equal((290f, 250f), (field.X, field.Y));
        var ok = Row(host, OriginalHangarScreen.NameOkKey);
        Assert.Equal((330f, 350f), (ok.X, ok.Y));
        var defaults = Row(host, OriginalHangarScreen.NameDefaultsKey);
        Assert.Equal((290f, 300f), (defaults.X, defaults.Y));
        Assert.Contains(board.Lines, l => l.X == 300f && l.Y == 230f);
    }

    [Fact]
    public void TheHubsNameBoxRenamesTheScratchPlaneInPlace()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");

        // The box runs from the title's end to the script's own 302, at the row's own line.
        var box = Row(host, OriginalHangarScreen.HubNameFieldKey);
        Assert.Equal(OriginalRowKind.TextField, box.Kind);
        Assert.Equal((120f, 12f, 182f, 17f), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal("Ace", box.Label);
        Assert.False(host.Module.CapturingText);

        Click(host, OriginalHangarScreen.HubNameFieldKey);
        Assert.Equal(OriginalHangarScreen.HubNameFieldKey, host.FocusedKey);
        Assert.True(host.Module.CapturingText);
        Type(host, "2");

        Assert.Equal("Ace2", hangar.Scratch.Name);
        Assert.Null(hangar.DefaultsAsk);
        Assert.Empty(_store.List());

        // Emptying the box commits nothing: the totals page refuses a nameless plane in the
        // screen's own words, which is the refusal the feature already carried.
        for (int i = 0; i < 4; i++)
        {
            Erase(host);
        }

        Assert.Equal(string.Empty, hangar.Scratch.Name);
        Assert.False(hangar.CanCommit);
        Type(host, "Ace2");
        var board = Compose(host);
        var line = board.Lines.Single(l => l.Text == "Ace2" && l.Caret != null);
        Assert.Equal(new BoardCaret(0xEF, 0x00, 0x10, 2f, 15f), line.Caret);
        Assert.Equal(BoardInk.Dialog, line.Ink);
        Assert.Contains(board.Fills, f => f.Border && f.R == 0x73 && f.G == 0x69 && f.B == 0x9C && f.X == box.X);

        // A box nobody is in draws no caret, on this tab or the next.
        Click(host, "PX_B_ENGINE");
        Assert.False(host.Module.CapturingText);
        Assert.Equal("Ace2", Row(host, OriginalHangarScreen.HubNameFieldKey).Label);
        Assert.DoesNotContain(Compose(host).Lines, l => l.Caret != null);
    }

    [Fact]
    public void OkWithTheBoxCheckedOpensTheHubOnADefaultConfiguration()
    {
        var host = Host(out var hangar);
        OpenName(host, "Ace");

        host.FocusKey(OriginalHangarScreen.NameOkKey);
        Accept(host);

        Assert.Equal(OriginalScreen.HangarAirframe, host.Screen);
        Assert.Equal("Ace", hangar.Scratch.Name);
        Assert.True(hangar.AirframeChosen);
        Assert.Equal(HangarFeature.DefaultAirframe, hangar.Scratch.Airframe);
        Assert.Equal(1, hangar.Scratch.Engine);
        Assert.Equal(
            new[] { OriginalHangarScreen.AirframeDropKey, "PX_B_AIRFRAME", "PX_B_ENGINE", "PX_B_ARMOR", "PX_B_GUNS", "PX_B_HARDPOINTS", "PX_B_PAINT", OriginalHangarScreen.SellPlanesKey, OriginalHangarScreen.ReadyKey, OriginalHangarScreen.CancelBuildKey, OriginalHangarScreen.HubNameFieldKey },
            host.Rows.Select(r => r.Key));
        Assert.True(Row(host, "PX_B_AIRFRAME").Enabled);
        Assert.Equal("Airframe 5", Row(host, OriginalHangarScreen.AirframeDropKey).Label);
    }

    [Fact]
    public void TheDefaultConfigurationTakesTheAirframeTheDoorWasOpenedOver()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace", doorAirframe: 2);

        Assert.True(hangar.AirframeChosen);
        Assert.Equal(2, hangar.Scratch.Airframe);
        Assert.Equal(1, hangar.Scratch.Engine);
        Assert.Equal("Airframe 2", Row(host, OriginalHangarScreen.AirframeDropKey).Label);
    }

    [Fact]
    public void OkWithTheBoxClearedStartsBareAndAnUneditedBuildsAirframeSwapAsksNothing()
    {
        var host = Host(out var hangar);
        OpenName(host, "Ace");
        Click(host, OriginalHangarScreen.NameDefaultsKey);
        Assert.False(host.Module.LoadDefaultsChecked);
        Click(host, OriginalHangarScreen.NameOkKey);

        Assert.Equal(OriginalScreen.HangarAirframe, host.Screen);
        Assert.False(hangar.AirframeChosen);
        Assert.Equal(string.Empty, Row(host, OriginalHangarScreen.AirframeDropKey).Label);
        Assert.Contains(Compose(host).Pictures, p => p.Art.Name == "PX_0_BLUEPRINT.TGA" && p.X == 16f && p.Y == 44f);

        // The tab opens focused on its dropdown, so Accept opens the list on its first row.
        Accept(host);
        Assert.Equal(OriginalHangarScreen.AirframeDropKey, host.Module.OpenHangarDropdown);
        Assert.Equal(11, host.Rows.Count);
        Assert.Equal(OriginalHangarScreen.AirframeDropKey + ":0", host.FocusedKey);
        host.FocusKey(OriginalHangarScreen.AirframeDropKey + ":1");
        Accept(host);

        // Nothing had been edited away from the opened build, so the swap takes the box's own
        // answer (clear: a bare airframe) and raises no question.
        Assert.Null(hangar.DefaultsAsk);
        Assert.Null(host.Dialog);
        Assert.Equal(1, hangar.Scratch.Airframe);
        Assert.Equal(CustomPlaneDef.EngineNone, hangar.Scratch.Engine);
        Assert.Equal(OriginalHangarScreen.AirframeDropKey, host.FocusedKey);
    }

    [Fact]
    public void AnEditedBuildsAirframeSwapAsksWithThreeAnswersAndCancelPutsTheAirframeBack()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace", doorAirframe: 2);
        Assert.Equal(2, hangar.Scratch.Airframe);
        Assert.Equal(1, hangar.Scratch.Engine);

        // The engine is the edit; the swap that follows is what the original asks about.
        Click(host, "PX_B_ENGINE");
        PickEngine(host, 2);
        Assert.Equal(2, hangar.Scratch.Engine);
        Click(host, "PX_B_AIRFRAME");
        SwapAirframeTo(host, 4);

        Assert.Equal(4, hangar.DefaultsAsk);
        Assert.Equal(hangar.DefaultsAskText, host.Dialog!.Message);
        Assert.Equal(DialogIcon.Query, host.Dialog!.Icon);
        Assert.Equal(
            new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey, OriginalShell.DialogCancelKey },
            host.Dialog!.Answers.Select(a => a.Key));
        Assert.Equal(
            new[] { CampaignBoards.DialogLeftKey, CampaignBoards.DialogCenterKey, CampaignBoards.DialogRightKey },
            host.Dialog!.Answers.Select(a => a.LayoutKey));

        // Cancel is the only answer that keeps the edit, which is what string 206 says of it.
        Click(host, OriginalShell.DialogCancelKey);
        Assert.Null(hangar.DefaultsAsk);
        Assert.Equal(2, hangar.Scratch.Airframe);
        Assert.Equal(2, hangar.Scratch.Engine);
        Assert.Equal(OriginalHangarScreen.AirframeDropKey, host.FocusedKey);
    }

    [Fact]
    public void TheAsksYesTakesTheStockBuildAndItsNoTakesABareAirframe()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace", doorAirframe: 2);
        Click(host, "PX_B_ENGINE");
        PickEngine(host, 2);
        Click(host, "PX_B_AIRFRAME");
        SwapAirframeTo(host, 4);

        Click(host, OriginalShell.DialogNoKey);
        Assert.Equal(4, hangar.Scratch.Airframe);
        Assert.Equal(CustomPlaneDef.EngineNone, hangar.Scratch.Engine);
        Assert.Equal(0, hangar.Scratch.ArmourNose);
        Assert.All(hangar.Scratch.Guns, gun => Assert.True(gun.IsEmpty));
        Assert.Equal(0, hangar.Scratch.LeftHardpoints);

        // The answered build is the new opened-as build, so an edit has to come first again.
        SwapAirframeTo(host, 5);
        Assert.Null(hangar.DefaultsAsk);
        Click(host, "PX_B_ENGINE");
        PickEngine(host, 2);
        Click(host, "PX_B_AIRFRAME");
        SwapAirframeTo(host, 6);

        Click(host, OriginalShell.DialogYesKey);
        Assert.Equal(6, hangar.Scratch.Airframe);
        Assert.Equal(1, hangar.Scratch.Engine);
    }

    [Fact]
    public void TheTabsAreSiblingsAndASidewaysStepOnADropdownChangesItsValue()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");

        Click(host, "PX_B_PAINT");
        Assert.Equal(OriginalScreen.HangarPaint, host.Screen);
        Click(host, "PX_B_ENGINE");
        Assert.Equal(OriginalScreen.HangarEngine, host.Screen);
        Assert.Equal(OriginalHangarScreen.EngineDropKey, host.FocusedKey);
        int engine = hangar.Scratch.Engine;
        Right(host);
        Assert.Equal(engine + 1, hangar.Scratch.Engine);

        Accept(host);
        Assert.Equal(CustomPlaneDef.EngineNone + 1, host.Rows.Count);
        Assert.Equal(OriginalHangarScreen.EngineDropKey + ":" + (engine + 1), host.FocusedKey);
        Back(host);
        Assert.Null(host.Module.OpenHangarDropdown);
        Assert.Equal(OriginalScreen.HangarEngine, host.Screen);

        Click(host, "PX_B_ARMOR");
        Click(host, "AR_D_POINT1");
        Assert.Equal(CustomPlaneDef.MaxArmourUnits + 1, host.Rows.Count);
        Click(host, "AR_D_POINT1:3");
        Assert.Equal(3, hangar.Scratch.ArmourTail);
        Assert.Equal("15 units", Row(host, "AR_D_POINT1").Label);

        Click(host, "PX_B_HARDPOINTS");
        Click(host, "HP_D_POINT0");
        Click(host, "HP_D_POINT0:2");
        Assert.Equal(2, hangar.Scratch.LeftHardpoints);
        Click(host, "PX_B_GUNS");
        Click(host, "GN_D_GUN3");
        Click(host, "GN_D_GUN3:6");
        Assert.Equal(new GunChoice(1, true), hangar.Scratch.Guns[3]);
    }

    [Fact]
    public void ASidewaysStepOnTheTabBarWalksAlongItAndABoxTakesNone()
    {
        var host = Host(out _);
        OpenHub(host, "Ace");

        // The standing tab is a sibling like the other five, so the walk steps onto it too, and
        // past the last tab it reaches the buttons beside the bar.
        host.FocusKey("PX_B_AIRFRAME");
        Right(host);
        Assert.Equal("PX_B_ENGINE", host.FocusedKey);
        host.FocusKey("PX_B_PAINT");
        Right(host);
        Assert.Equal(OriginalHangarScreen.SellPlanesKey, host.FocusedKey);
        Assert.True(host.Module.StepSideways(host.Rows, host.Focus, -1));
        Assert.Equal("PX_B_PAINT", host.FocusedKey);

        host.FocusKey(OriginalHangarScreen.HubNameFieldKey);
        Assert.False(host.Module.StepSideways(host.Rows, host.Focus, 1));
        Assert.Equal(OriginalHangarScreen.HubNameFieldKey, host.FocusedKey);
    }

    [Fact]
    public void ThePaintTabWindowsItsColourListAndDrawsSwatchesAndTiles()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        Click(host, "PX_B_PAINT");

        var colours = Row(host, "PT_D_COLORS0");
        Assert.Equal(string.Empty, colours.Label);
        var decal = Row(host, "PT_D_DECALS2");
        Assert.Equal(73f, decal.Height);
        Assert.Contains(Compose(host).Fills, f => f.X == colours.X + 2f && f.Y == colours.Y + 2f && !f.Border);

        int start = hangar.Scratch.PaintColours[0];
        Click(host, "PT_D_COLORS0");
        int swatches = HangarPaintTables.Default.Swatches.Count;
        Assert.Equal(swatches + 2, host.Rows.Count);
        Assert.Equal(18, host.Rows.Count(r => r.Visible && r.Kind == OriginalRowKind.ListRow));
        Assert.Equal("PT_D_COLORS0:" + start, host.FocusedKey);
        Assert.True(Row(host, host.FocusedKey).Visible);
        Assert.True(Row(host, "PT_D_COLORS0:up").Enabled || Row(host, "PT_D_COLORS0:down").Enabled);

        // The window follows the focus onto the last swatch, so the first scrolls out of it.
        int last = swatches - 1;
        host.FocusKey("PT_D_COLORS0:" + last);
        Assert.True(Row(host, "PT_D_COLORS0:" + last).Visible);
        Assert.False(Row(host, "PT_D_COLORS0:0").Visible);
        Assert.False(Row(host, "PT_D_COLORS0:down").Enabled);
        Accept(host);
        Assert.Equal(last, hangar.Scratch.PaintColours[0]);
        Assert.Null(host.Module.OpenHangarDropdown);

        Click(host, "PT_D_DECALS0");
        Assert.Equal(10, host.Rows.Count(r => r.Visible && r.Kind == OriginalRowKind.ListRow));
        Click(host, "PT_D_DECALS0:1");
        Assert.Equal(1, hangar.Scratch.NoseDecal);
        Assert.Contains(Compose(host).Pictures, p => p.Art.Name == "PH_Decals.tga" && p.Frame == 1);
        Assert.Contains(Compose(host).Pictures, p => p.Art.Name.StartsWith("PX_ICON_5_", StringComparison.Ordinal) && p.Tint != null);
    }

    /// <summary>The decal picker as the original opens it: a five-across, two-down grid of the
    /// sheet's own tiles on the page, wider than the 87-pixel box it hangs from, its scroll chrome
    /// inside its own right edge, and every step a whole row of five.</summary>
    [Fact]
    public void TheDecalListOpensAsAFiveAcrossGridWhoseChromeMovesWithIt()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        Click(host, "PX_B_PAINT");
        hangar.SetDecal(0, 40);

        Click(host, "PT_D_DECALS0");

        // Ten of the fifty tiles show, five across and two down, at the sheet's own tile size and
        // on the page's own rectangle rather than under the box.
        var cells = host.Rows.Where(r => r.Visible && r.Kind == OriginalRowKind.ListRow).ToList();
        Assert.Equal(10, cells.Count);
        Assert.All(cells, cell => Assert.Equal((66f, 66f), (cell.Width, cell.Height)));
        Assert.Equal(new[] { 407f, 473f, 539f, 605f, 671f }, cells.Take(5).Select(c => c.X).ToArray());
        Assert.Equal(new[] { 375f, 441f }, cells.Select(c => c.Y).Distinct().ToArray());

        // The window opens on the row its pick stands in, so decal 40 heads the grid, and the
        // tiles are drawn over the whole cell with no name beside them.
        Assert.Equal("PT_D_DECALS0:40", host.FocusedKey);
        Assert.Equal(new[] { "PT_D_DECALS0:40", "PT_D_DECALS0:49" }, new[] { cells[0].Key, cells[9].Key });
        var panel = Compose(host).Overlays.Single(o => o.Fills.Count > 0);
        Assert.Equal(10, panel.Pictures.Count(p => p.Art.Name == "PH_Decals.tga"));
        Assert.Contains(panel.Pictures, p => p.Art.Name == "PH_Decals.tga" && p.Frame == 40 && p.X == 407f && p.Y == 375f);
        Assert.Empty(panel.Lines);

        // The panel is the grid's own: five tiles plus the scroll column inside a one-pixel frame,
        // the column as wide as the row's own arrow art (15 in this fixture).
        var back = panel.Fills[0];
        Assert.Equal((406f, 374f), (back.X, back.Y));
        Assert.Equal(((5f * 66f) + 15f + 2f, (2f * 66f) + 2f), (back.Width, back.Height));
        var up = Row(host, "PT_D_DECALS0:up");
        var down = Row(host, "PT_D_DECALS0:down");
        Assert.Equal((737f, 375f), (up.X, up.Y));
        Assert.Equal((737f, 374f + (2f * 66f) + 1f - up.Height), (down.X, down.Y));
        Assert.True(up.Enabled);
        Assert.False(down.Enabled);

        // The scrollbar counts grid rows: ten of them, two showing, the window on the last pair,
        // and the thumb drawn to the height the window gives it rather than the art's own.
        var list = Lists(host).Single(l => l.Key == "PT_D_DECALS0");
        Assert.Equal((10, 2, 8), (list.Window.Count, list.Window.Rows, list.Window.Top));
        Assert.Contains(panel.Pictures, p => p.Art.Name == "PH_B_ScrollBar.png" && p.Height == list.Window.ThumbHeight);

        // A wheel notch over the grid and an arrow press are both one row of five.
        Assert.True(list.Window.Contains(500f, 400f));
        list.ScrollTo(list.Window.TopAfterWheel(-1));
        Assert.Equal("PT_D_DECALS0:35", host.Rows.First(r => r.Visible && r.Kind == OriginalRowKind.ListRow).Key);
        Click(host, "PT_D_DECALS0:up");
        Assert.Equal("PT_D_DECALS0:30", host.Rows.First(r => r.Visible && r.Kind == OriginalRowKind.ListRow).Key);
        Click(host, "PT_D_DECALS0:down");
        Assert.Equal("PT_D_DECALS0:35", host.Rows.First(r => r.Visible && r.Kind == OriginalRowKind.ListRow).Key);

        // A tile picks the decal its own cell carries.
        Click(host, "PT_D_DECALS0:36");
        Assert.Equal(36, hangar.Scratch.NoseDecal);
        Assert.Null(host.Module.OpenHangarDropdown);

        // The colour lists stay one column wide.
        Click(host, "PT_D_COLORS0");
        Assert.Equal(18, host.Rows.Count(r => r.Visible && r.Kind == OriginalRowKind.ListRow));
        Assert.Single(host.Rows.Where(r => r.Visible && r.Kind == OriginalRowKind.ListRow).Select(r => r.X).Distinct());
    }

    /// <summary>The tab pages' description box: the component's figures on their own lines, the
    /// heading the shipped info string ends with, and the component's prose flowed as a note that
    /// stays inside the authored box. The engine figures are the still's, for the airframe the
    /// default configuration opens on.</summary>
    [Fact]
    public void TheTabPagesDescriptionBoxCarriesTheHeadingAndTheComponentsProse()
    {
        var host = Host(out _, strings: HangarInfoStrings());
        OpenHub(host, "Ace");
        Click(host, "PX_B_ENGINE");

        var board = Compose(host);
        Assert.Contains(board.Lines, l => l.Text == "COST: $1700" && l.Italic);
        Assert.Contains(board.Lines, l => l.Text == "WEIGHT: 2000 lbs.");
        Assert.Contains(board.Lines, l => l.Text == "TOP SPEED: 251 m.p.h.");
        Assert.Contains(board.Lines, l => l.Text == "NITRO-BOOST: No");

        // The blank line the shipped string carries stands between the figures and the heading,
        // and the prose is a note because its wrapped height is a font measurement.
        var heading = board.Lines.Single(l => l.Text == "DESCRIPTION");
        Assert.Equal(330f + 4f + (5f * 14f), heading.Y);
        Assert.True(heading.Italic);
        var note = Assert.Single(board.Notes);
        Assert.Equal("A Devastator engine.", Assert.Single(note.Entries));
        Assert.Equal((416f, heading.Y + 14f), (note.X, note.Y));
        Assert.True(note.Italic && note.Cut);
        Assert.True(note.Y + note.Height <= 330f + 160f, "the prose stays inside the authored box");

        // Armour names itself off the shipped string and carries its prose inside it.
        Click(host, "PX_B_ARMOR");
        board = Compose(host);
        Assert.Contains(board.Lines, l => l.Text == "ABOUT ARMOR");
        Assert.Contains(board.Lines, l => l.Text == "COST: $20/5 units" && l.Italic);
        Assert.Contains(board.Lines, l => l.Text == "NOTE: Left and right wings must be balanced!");
        Assert.Contains(board.Lines, l => l.Text == "DESCRIPTION");
        Assert.Equal("Aero-armor.", Assert.Single(Assert.Single(board.Notes).Entries));

        // An empty gun slot is its own string, with no figures and no heading over it.
        Click(host, "PX_B_GUNS");
        Click(host, "GN_D_GUN0");
        Click(host, "GN_D_GUN0:" + (HangarFeature.GunCycleRows - 1));
        board = Compose(host);
        Assert.DoesNotContain(board.Lines, l => l.Text == "DESCRIPTION");
        Assert.Equal("No Information Available", Assert.Single(Assert.Single(board.Notes).Entries));
    }

    [Fact]
    public void ReadyOpensTheTotalsWhosePurchaseCommitsIntoTheStoreAndAsksForTheRosters()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");

        Click(host, OriginalHangarScreen.ReadyKey);
        Assert.Equal(OriginalScreen.HangarPurchase, host.Screen);
        var purchase = Row(host, OriginalHangarScreen.PurchaseNowKey);
        Assert.True(purchase.Enabled);
        // The wallet-free door commits with Export, over the row's own Purchase Now.
        Assert.Equal("Export", purchase.Label);
        Assert.False(Row(host, OriginalHangarScreen.ReadyKey).Enabled);
        var board = Compose(host);
        Assert.Contains(board.Lines, l => l.Text == "Airframe 5");
        Assert.Contains(board.Lines, l => l.Text == "$" + hangar.Bill.Total.Cost);

        Back(host);
        Assert.Equal(OriginalScreen.HangarAirframe, host.Screen);
        Click(host, OriginalHangarScreen.ReadyKey);
        Click(host, OriginalHangarScreen.PurchaseNowKey);

        // The commit returns to the screen the door was pressed on, the Instant Action screen,
        // asking the host for the sortie roster and that screen's Pilot Plane list re-read so the
        // build is offered at once.
        Assert.Equal(OriginalScreen.InstantAction, host.Screen);
        Assert.Equal("Ace", host.Module.LastBuiltPlane);
        Assert.False(hangar.IsOpen);
        Assert.Equal(HangarFeature.DefaultAirframe, _store.Load("Ace")!.Airframe);
        Assert.Equal(1, host.RosterRefreshes);
        Assert.Equal(1, host.InstantActionRefreshes);
        Assert.Equal(0, host.CampaignResumes);
    }

    [Fact]
    public void AnUnbuildablePlaneKeepsPurchaseNowDisabledWithTheReasonShowing()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        hangar.SetEngine(CustomPlaneDef.EngineNone);

        Click(host, OriginalHangarScreen.ReadyKey);
        Assert.False(Row(host, OriginalHangarScreen.PurchaseNowKey).Enabled);
        Assert.Contains(Compose(host).Lines, l => l.Text == "CAN'T PURCHASE: No Engine Selected");
    }

    [Fact]
    public void CancelFromATabAndBackFromTheNameScreenLeaveNoResidue()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        Click(host, "PX_B_GUNS");
        Click(host, OriginalHangarScreen.CancelBuildKey);

        Assert.Equal(OriginalScreen.InstantAction, host.Screen);
        Assert.False(hangar.IsOpen);
        Assert.Empty(_store.List());

        OpenName(host, "Ace");
        Back(host);
        Assert.Equal(OriginalScreen.InstantAction, host.Screen);
        Assert.False(hangar.IsOpen);
        Assert.Empty(_store.List());
        Assert.Equal(0, host.RosterRefreshes);
    }

    [Fact]
    public void SellPlanesOpensTheInventoryWhoseDeleteRemovesTheSavedPlane()
    {
        _store.Save(new CustomPlaneDef { Name = "Old", Airframe = 2, Engine = 1 });
        _store.Save(new CustomPlaneDef { Name = "Spare", Airframe = 3, Engine = 1 });
        var host = Host(out var hangar);
        OpenHub(host, "Ace");

        Click(host, OriginalHangarScreen.SellPlanesKey);
        Assert.Equal(OriginalScreen.HangarInventory, host.Screen);
        // Wallet-free the Export row is not built at all: Instant Action has nowhere to export to,
        // and the removal is a delete rather than a sale.
        Assert.Equal(
            new[] { OriginalHangarScreen.InventoryPlanesKey, OriginalHangarScreen.InventorySellKey, OriginalHangarScreen.InventoryDoneKey },
            host.Rows.Select(r => r.Key));
        Assert.Equal("Delete", Row(host, OriginalHangarScreen.InventorySellKey).Label);
        Assert.Equal("Old", host.Rows[0].Label);
        Assert.Contains(Compose(host).Pictures, p => p.Art.Name == "PH_PlaneIcons.png" && p.Frame == 2);

        Right(host);
        Assert.Equal(1, host.Module.InventoryIndex);
        Assert.Equal("Spare", host.Rows[0].Label);
        // Sell asks first, the two-answer messagebox opening on its first answer; No keeps the
        // plane, Yes sells it, and Back declines.
        Click(host, OriginalHangarScreen.InventorySellKey);
        Assert.NotNull(host.Dialog);
        Assert.Equal(new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey }, host.Dialog!.Answers.Select(a => a.Key));
        Assert.Equal(OriginalShell.DialogYesKey, host.FocusedKey);
        // The question is the delete one, with no price on a plane that cost nothing.
        Assert.Contains("delete it?", host.Dialog!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("$", host.Dialog!.Message, StringComparison.Ordinal);
        // The sell question is HANGAR.SCRIPT's 0x4 mask, so it keeps the query icon.
        Assert.Equal(DialogIcon.Query, host.Dialog!.Icon);
        Back(host);
        Assert.Null(host.Dialog);
        Assert.NotNull(_store.Load("Spare"));
        Assert.Equal(OriginalHangarScreen.InventorySellKey, host.FocusedKey);
        Click(host, OriginalHangarScreen.InventorySellKey);
        Click(host, OriginalShell.DialogYesKey);
        Assert.Null(host.Dialog);
        Assert.Null(_store.Load("Spare"));
        Assert.Equal(new[] { "Old" }, hangar.Saved.Select(p => p.Name));
        Assert.Equal(1, host.RosterRefreshes);
        Assert.Equal("Ace", hangar.Scratch.Name);

        Click(host, OriginalHangarScreen.InventoryDoneKey);
        Assert.Equal(OriginalScreen.HangarAirframe, host.Screen);
    }

    /// <summary>The inventory's plane line stands on the row <c>HANGAR.SCRIPT</c> binds,
    /// <c>HA_T_PLANE</c> at the box's own left edge with no width. It is centred on that box, off
    /// the authored 108 whose baseline lands on the box's bottom rule. The wide
    /// <c>HA_T_PILOTPLANE</c> the shipped build authors and never draws stays empty.</summary>
    [Fact]
    public void TheInventoryWritesThePlaneLineOnTheRowTheScriptBinds()
    {
        _store.Save(new CustomPlaneDef { Name = "Old", Airframe = 2, Engine = 1 });
        var host = Host(out _);
        OpenHub(host, "Ace");

        Click(host, OriginalHangarScreen.SellPlanesKey);
        var board = Compose(host);
        var line = board.Lines.Single(l => l.Text == "Old   Airframe 2");
        Assert.Equal((138f, OriginalHangarScreen.InventoryPlaneRow, 0f), (line.X, line.Y, line.Width));
        Assert.True(line.Y + line.Size < OriginalHangarScreen.InventoryPlaneBoxBottom);
        Assert.DoesNotContain(board.Lines, l => l.X == 236f && l.Y == 110f);
    }

    /// <summary>A plane named for its airframe is one fact: the row writes the name once rather
    /// than the doubled title the concatenated line read.</summary>
    [Fact]
    public void TheInventoryWritesAPlaneNamedForItsAirframeOnce()
    {
        _store.Save(new CustomPlaneDef { Name = "Airframe 3", Airframe = 3, Engine = 1 });
        var host = Host(out _);
        OpenHub(host, "Ace");

        Click(host, OriginalHangarScreen.SellPlanesKey);
        var board = Compose(host);
        var line = board.Lines.Single(l => l.X == 138f && l.Y == OriginalHangarScreen.InventoryPlaneRow);
        Assert.Equal("Airframe 3", line.Text);
        Assert.DoesNotContain(board.Lines, l => l.Text.Contains("Airframe 3   Airframe 3", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWalletsInventoryKeepsExportWhoseConfirmationKeepsThePlane()
    {
        var owned = new CustomPlaneDef { Name = "Old", Airframe = 2, Engine = 1 };
        _store.Save(owned);
        var host = Host(out var hangar);
        var wallet = new OwningWallet(owned);
        host.Module.OpenHangarTab(OriginalScreen.HangarAirframe, "Ace", wallet, HangarFeature.DefaultAirframe);
        Click(host, OriginalHangarScreen.SellPlanesKey);

        // Export is the campaign's own verb, so the wallet's page keeps both shipped words.
        Assert.Equal(
            new[] { OriginalHangarScreen.InventoryPlanesKey, OriginalHangarScreen.InventorySellKey, OriginalHangarScreen.InventoryExportKey, OriginalHangarScreen.InventoryDoneKey },
            host.Rows.Select(r => r.Key));
        Assert.Equal("Sell", Row(host, OriginalHangarScreen.InventorySellKey).Label);
        Assert.True(Row(host, OriginalHangarScreen.InventoryExportKey).Enabled);
        Click(host, OriginalHangarScreen.InventoryExportKey);
        Assert.NotNull(host.Dialog);
        // The export box is the one-button 0x1 mask, so it keeps the notice icon.
        Assert.Equal(DialogIcon.Warning, host.Dialog!.Icon);
        Assert.Equal(new[] { OriginalShell.DialogOkKey }, host.Dialog!.Answers.Select(a => a.Key));
        Assert.Contains("exported", host.Dialog!.Message, StringComparison.Ordinal);
        Click(host, OriginalShell.DialogOkKey);

        Assert.Null(host.Dialog);
        Assert.NotNull(_store.Load("Old"));
        Assert.Equal(new[] { "Old" }, hangar.Saved.Select(p => p.Name));
    }

    [Fact]
    public void TheHubComposesTheChromeTabsAndButtonsFromTheLayout()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        Click(host, "PX_B_ENGINE");

        var board = Compose(host);
        Assert.Equal("PH_Back.jpg", Assert.Single(board.Backdrop).Art.Name);
        // The standing tab is latched, not gated: the depressed frame in that frame's own ink,
        // against the normal frame the other five draw.
        var current = board.Plaques.Single(p => p.Label == "Engine");
        Assert.Equal(3, current.Frame);
        Assert.Equal(BoardInk.LabelActivate, current.Ink);
        Assert.Equal(1, board.Plaques.Single(p => p.Label == "Airframe").Frame);
        Assert.Equal((150f, 520f), (current.X, current.Y));
        Assert.Contains(board.Lines, l => l.Text == "PLANE COST:  $" + hangar.Bill.Total.Cost);
        Assert.Contains(board.Lines, l => l.Text == "AIRFRAME: Airframe 5");
        Assert.Contains(board.Lines, l => l.Text == "Ace" && l.Ink == BoardInk.Dialog);
        Assert.Contains(board.Plaques, p => p.Art.Name == "PH_B_Ready.png");
        // The cash note stands on the wallet-free door too, over the export funds the hub's own
        // script writes there.
        Assert.Contains(board.Lines, l => l.Text == "$$$ on Hand:");
        Assert.Contains(board.Lines, l => l.Text == "$50000" && l.Ink == BoardInk.Row);
    }

    [Fact]
    public void TheHubReddensThePlaneCostPastTheWalletAndTheWeightPastTheCapacity()
    {
        var host = Host(out var hangar);
        host.Module.OpenHangarTab(OriginalScreen.HangarAirframe, "Ace", new TightWallet(1_000_000), HangarFeature.DefaultAirframe);
        var board = Compose(host);
        Assert.Equal(BoardInk.Dialog, Figure(board, "PLANE COST").Ink);
        Assert.Equal(BoardInk.Dialog, Figure(board, "CURRENT WEIGHT").Ink);

        // The same build over funds that cannot cover it: the cost line alone reddens, since the
        // weight is unchanged by what the wallet holds.
        host.Module.OpenHangarTab(OriginalScreen.HangarAirframe, "Ace", new TightWallet(1), HangarFeature.DefaultAirframe);
        board = Compose(host);
        Assert.Equal(BoardInk.Alarm, Figure(board, "PLANE COST").Ink);
        Assert.Equal(BoardInk.Dialog, Figure(board, "CURRENT WEIGHT").Ink);

        for (int zone = 0; zone < 4; zone++)
        {
            hangar.SetArmour(zone, CustomPlaneDef.MaxArmourUnits);
        }

        hangar.SetHardpoints(0, CustomPlaneDef.MaxHardpointsPerWing);
        hangar.SetHardpoints(1, CustomPlaneDef.MaxHardpointsPerWing);
        Assert.Equal(PurchaseVerdict.Overweight, hangar.Bill.Verdict);
        board = Compose(host);
        Assert.Equal(BoardInk.Alarm, Figure(board, "CURRENT WEIGHT").Ink);
    }

    [Fact]
    public void TheHubLeavesBothFiguresPlainBeforeAnAirframeIsChosen()
    {
        var host = Host(out var hangar);
        OpenName(host, "Ace");
        Click(host, OriginalHangarScreen.NameDefaultsKey);
        Click(host, OriginalHangarScreen.NameOkKey);
        Assert.False(hangar.AirframeChosen);

        var board = Compose(host);
        Assert.EndsWith("Pending", Figure(board, "CURRENT WEIGHT").Text, StringComparison.Ordinal);
        Assert.Equal(BoardInk.Dialog, Figure(board, "CURRENT WEIGHT").Ink);
        Assert.Equal(BoardInk.Dialog, Figure(board, "PLANE COST").Ink);
    }

    [Fact]
    public void TheHubFiguresFollowTheFocusedRowAndComeBackWhenTheListCloses()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        Click(host, "PX_B_ENGINE");
        var committed = hangar.Bill;
        Click(host, OriginalHangarScreen.EngineDropKey);
        Assert.Equal(OriginalHangarScreen.EngineDropKey, host.Module.OpenHangarDropdown);
        int row = int.Parse(host.FocusedKey[(OriginalHangarScreen.EngineDropKey.Length + 1)..], CultureInfo.InvariantCulture) + 1;
        host.FocusKey(OriginalHangarScreen.EngineDropKey + ":" + row.ToString(CultureInfo.InvariantCulture));
        var preview = hangar.BillWithEngine(row);
        Assert.NotEqual(committed.Total.Cost, preview.Total.Cost);

        var board = Compose(host);
        Assert.Contains("$" + preview.Total.Cost, Figure(board, "PLANE COST").Text, StringComparison.Ordinal);
        Assert.Contains(preview.Total.Weight.ToString(CultureInfo.InvariantCulture), Figure(board, "CURRENT WEIGHT").Text, StringComparison.Ordinal);
        Assert.Equal(committed.Total.Cost, hangar.Bill.Total.Cost);

        // Back closes the list without taking the row, so both figures are the standing build's again.
        Back(host);
        board = Compose(host);
        Assert.Contains("$" + committed.Total.Cost, Figure(board, "PLANE COST").Text, StringComparison.Ordinal);
        Assert.Contains(committed.Total.Weight.ToString(CultureInfo.InvariantCulture), Figure(board, "CURRENT WEIGHT").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscardingTheFeatureMidBuildLeavesNothingBehind()
    {
        var host = Host(out var hangar);
        OpenHub(host, "Ace");
        hangar.Discard();

        Assert.False(hangar.IsOpen);
        Assert.Empty(_store.List());
        Assert.Equal(string.Empty, hangar.Scratch.Name);
    }

    // The fixture's strips: the tabs and buttons four frames each, the checkbox eight, the paper
    // plaque 160x112, the decal sheet fifty 66-pixel tiles, the blueprint and icon sets present,
    // the name dialog's pane smaller than the board and the full-page backgrounds unmeasured.
    internal static (int Width, int Height)? Measure(string art) => art switch
    {
        "PH_NamePanel.png" => (260, 180),
        "PM_B_Paper.png" or "PH_B_Paper.png" => (160, 112),
        "PH_Tab.png" => (120, 120),
        "PH_B_OkCancel.png" => (80, 96),
        "PH_B_Check8.png" => (16, 128),
        "PH_B_DropUp.png" or "PH_B_DropDown.png" => (15, 56),
        "PH_Decals.tga" => (66, 3300),
        "PH_PlaneIcons.png" => (100, 1200),
        _ when art.StartsWith("PH_B_", StringComparison.Ordinal) => (200, 128),
        _ when art.StartsWith("PX_ICON_", StringComparison.Ordinal) => (358, 335),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    // One of the hub's figure lines by the words it opens with, the layout's own text around them.
    private static BoardLine Figure(ComposedBoard board, string label) =>
        board.Lines.Single(l => l.Text.StartsWith(label, StringComparison.Ordinal));

    private static OriginalRow Row(HangarHost host, string key) => host.Rows.Single(r => r.Key == key);

    // One click as the shell reads it: the press puts the focus on the row and the release on it
    // activates. Over a dialog the key names one of its answers.
    private static void Click(HangarHost host, string key)
    {
        if (host.Dialog != null)
        {
            host.Answer(key);
            return;
        }

        var rows = host.Rows;
        int index = rows.ToList().FindIndex(r => r.Key == key);
        Assert.True(index >= 0, $"no row {key}");
        Assert.True(rows[index].Enabled, $"{key} is disabled");
        host.FocusedRow = index;
        host.Module.Activate(rows[index]);
    }

    // Accept on the focused row, or on a dialog's focused answer.
    private static void Accept(HangarHost host)
    {
        if (host.Dialog != null)
        {
            host.Answer(host.FocusedKey);
            return;
        }

        var rows = host.Rows;
        host.Module.Activate(rows[host.Focus]);
    }

    // Back declines a dialog with its last answer, else steps back inside the hangar.
    private static void Back(HangarHost host)
    {
        if (host.Dialog != null)
        {
            host.Answer(host.Dialog.Answers[^1].Key);
            return;
        }

        host.Module.Back();
    }

    private static void Right(HangarHost host) => host.Module.StepSideways(host.Rows, host.Focus, 1);

    private static void Type(HangarHost host, string text) =>
        host.Module.TypeName(new MenuCommands { Typed = text }, new List<string>());

    private static void Erase(HangarHost host) =>
        host.Module.TypeName(new MenuCommands { Erase = true }, new List<string>());

    private static IReadOnlyList<OriginalList> Lists(HangarHost host)
    {
        var lists = new List<OriginalList>();
        host.Module.Lists(lists);
        return lists;
    }

    // The screen as the module draws it, assembled the way the shell assembles its own board.
    private static ComposedBoard Compose(HangarHost host)
    {
        var rows = host.Rows;
        // The pen the seam offers is the campaign scrapbook's alone, so the hangar writes no stroke.
        var layers = new BoardLayers();
        host.Module.Compose(rows, host.Focus, layers);
        return new ComposedBoard(layers.Pictures, layers.Strokes, layers.Lines, layers.Plaques, layers.Notes,
            backdrop: layers.Backdrop, fills: layers.Fills, overlays: layers.Overlays);
    }

    // The airframe tab's own swap, through the presses a pilot has: the closed box opens its list
    // and the row is picked out of it. The engine tab's pick is the same gesture.
    private static void SwapAirframeTo(HangarHost host, int airframe) =>
        PickFromList(host, OriginalHangarScreen.AirframeDropKey, airframe);

    private static void PickEngine(HangarHost host, int engine) =>
        PickFromList(host, OriginalHangarScreen.EngineDropKey, engine);

    private static void PickFromList(HangarHost host, string key, int row)
    {
        Click(host, key);
        Click(host, key + ":" + row.ToString(CultureInfo.InvariantCulture));
    }

    // The way in: the wallet-free door from the Instant Action screen, which is where CANCEL and
    // a commit return to, over the airframe the door names.
    private static void OpenName(HangarHost host, string name, int doorAirframe = HangarFeature.DefaultAirframe)
    {
        host.Open(OriginalScreen.InstantAction);
        host.Module.OpenHangar(null, doorAirframe);
        Assert.Equal(OriginalScreen.PlaneName, host.Screen);
        Type(host, name);
    }

    // The name screen's OK with the box checked: the default configuration under the typed name.
    private static void OpenHub(HangarHost host, string name, int doorAirframe = HangarFeature.DefaultAirframe)
    {
        OpenName(host, name, doorAirframe);
        Click(host, OriginalHangarScreen.NameOkKey);
        Assert.Equal(OriginalScreen.HangarAirframe, host.Screen);
    }

    // The info rows the boxes are built from, with stand-in prose: the two shipped format strings
    // the cases read, the Devastator's engine row, the No Gun row and the words the figures take.
    private static UiStrings HangarInfoStrings() => UiStrings.Parse(JsonSerializer.Serialize(
        new[]
        {
            new { id = 1150, dll = UiStrings.Table, text = "ABOUT ARMOR" },
            new
            {
                id = 1154, dll = UiStrings.Table,
                text = "COST: $%1!d!\nWEIGHT: %2!d! lbs.\nTOP SPEED: %3!d! m.p.h.\nNITRO-BOOST: %4!s!\n\nDESCRIPTION\n",
            },
            new
            {
                id = 1155, dll = UiStrings.Table,
                text = "COST: $%1!d!/%2!d! units\nWEIGHT: %3!d! lbs./%4!d! units\n"
                    + "NOTE: Left and right wings must be balanced!\n\nDESCRIPTION\nAero-armor.",
            },
            new { id = 1166, dll = UiStrings.Table, text = "No" },
            new { id = 3271, dll = UiStrings.Table, text = "A Devastator engine." },
            new { id = 3335, dll = UiStrings.Table, text = "No Information Available" },
        }));

    // The module over a fresh feature, the suite's scratch store and the layout fixture, standing
    // on the Instant Action screen, which is the wallet-free door's own.
    private HangarHost Host(out HangarFeature hangar, UiStrings? strings = null)
    {
        hangar = new HangarFeature(strings ?? UiStrings.Empty, PlanePickerRoster.AirframeNode);
        var host = new HangarHost();
        host.Module = new OriginalHangarScreen(hangar, _store, MenuLayoutReaderTests.OriginalLayout(), Measure, host);
        return host;
    }

    // The hangar's side of the seam, over the shared fake. Its rows are the module's whatever screen
    // stands, because the hangar draws over the screen it was opened from. A focus put on a row
    // stays there even once the row goes dark. Rows the module has no drawing of its own for become
    // a plaque or a line, standing in for the shell's rule.
    private sealed class HangarHost : OriginalTestHost<OriginalHangarScreen>
    {
        internal HangarHost()
            : base(OriginalScreen.InstantAction, OriginalHangarTests.Measure, canBuildPlane: true)
        {
        }

        protected override bool RowsFollowOwnership => false;

        protected override bool FocusLeavesDeadRows => false;

        public override void ComposeGenericRow(
            OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers)
        {
            if (row.Art != null)
            {
                int frame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, frame, row.Label, BoardInk.Row));
                return;
            }

            layers.Lines.Add(new BoardLine(row.Label, row.X, row.Y, row.Width, 12f, BoardInk.Row, index));
        }
    }

    // A wallet with a stated purse and no aircraft, for the pages whose subject is the money: what
    // it can cover is what its funds cover, which is the check the cost line's ink reads.
    private sealed class TightWallet : IHangarWallet
    {
        internal TightWallet(int funds) => Funds = funds;

        public int Funds { get; }

        public bool HasFreeSlot => true;

        public bool CanAfford(int cost) => cost <= Funds;

        public bool IsAirframeAvailable(int airframe) => true;

        public bool IsSpecial(string planeName) => false;

        public bool CanSell(string planeName) => true;

        public int? OwnedAirframe(string planeName) => null;

        public IReadOnlyList<CustomPlaneDef> OwnedBuilds() => Array.Empty<CustomPlaneDef>();

        public void Purchase(string planeName, int airframe, int cost)
        {
        }

        public bool Sell(string planeName) => false;
    }

    // A wallet that owns the planes it is built over and funds anything, which is all the cabin
    // door's own page needs: what the wallet decides here is the verbs, not the money.
    private sealed class OwningWallet : IHangarWallet
    {
        private readonly List<CustomPlaneDef> _owned;

        internal OwningWallet(params CustomPlaneDef[] owned) => _owned = owned.ToList();

        public int Funds => 1_000_000;

        public bool HasFreeSlot => true;

        public bool CanAfford(int cost) => true;

        public bool IsAirframeAvailable(int airframe) => true;

        public bool IsSpecial(string planeName) => false;

        public bool CanSell(string planeName) => true;

        public int? OwnedAirframe(string planeName) =>
            _owned.FirstOrDefault(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase))?.Airframe;

        public IReadOnlyList<CustomPlaneDef> OwnedBuilds() => _owned.ToList();

        public void Purchase(string planeName, int airframe, int cost) =>
            _owned.Add(new CustomPlaneDef { Name = planeName, Airframe = airframe });

        public bool Sell(string planeName)
        {
            _owned.RemoveAll(p => string.Equals(p.Name, planeName, StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }
}
