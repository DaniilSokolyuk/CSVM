using System.IO;
using System.Linq;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The campaign boards' read of the decoded layout: every read answers with its row or with the
/// fallback handed in beside it, never one coordinate from each; a root with no artifact, a
/// malformed one and a foreign one all fall back with a reason and compose the same board the
/// hardcoded chrome always did; and over a layout whose rows sit elsewhere the composed board moves
/// with them, except where a value is pinned to its measurement.
/// </summary>
public class CampaignLayoutTests
{
    private static readonly BoardArt Placeholder = new(BoardArtLibrary.Ui, "Fallback.png", 4);

    [Fact]
    public void ANullRootIsTheFallbackWithNoReason()
    {
        var layout = CampaignLayout.For(null);

        Assert.Same(CampaignLayout.Fallback, layout);
        Assert.Null(layout.Decoded);
        Assert.Null(layout.Reason);
    }

    [Fact]
    public void AMissingArtifactFallsBackWithAReasonAndIsLoadedOnce()
    {
        string root = TestData.TempDir();
        try
        {
            var layout = CampaignLayout.For(root);

            Assert.Null(layout.Decoded);
            Assert.NotNull(layout.Reason);
            Assert.Contains("menu_layout.json", layout.Reason);
            Assert.Same(layout, CampaignLayout.For(root));
            AssertEveryReadFallsBack(layout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMalformedOrForeignArtifactFallsBackWithAReason()
    {
        string broken = TestData.TempDir();
        string foreign = TestData.TempDir();
        try
        {
            string brokenPath = MenuLayout.PathUnder(broken);
            Directory.CreateDirectory(Path.GetDirectoryName(brokenPath)!);
            File.WriteAllText(brokenPath, "{ \"screens\": [ not json");
            string foreignPath = MenuLayout.PathUnder(foreign);
            Directory.CreateDirectory(Path.GetDirectoryName(foreignPath)!);
            File.WriteAllText(foreignPath, "{ \"schema\": 1, \"planes\": [] }");

            var malformed = CampaignLayout.For(broken);
            var other = CampaignLayout.For(foreign);

            Assert.Null(malformed.Decoded);
            Assert.Contains("unreadable", malformed.Reason);
            Assert.Null(other.Decoded);
            Assert.Contains("unreadable", other.Reason);
            AssertEveryReadFallsBack(malformed);
            AssertEveryReadFallsBack(other);
        }
        finally
        {
            Directory.Delete(broken, recursive: true);
            Directory.Delete(foreign, recursive: true);
        }
    }

    /// <summary>A row answers with its own values and a missing row, key or field with the
    /// fallback, whole: a button row has no width, so a box read of it is the fallback triple and
    /// not the row's X and Y with the fallback's width.</summary>
    [Fact]
    public void ReadsAnswerWithTheRowOrWithTheWholeFallback()
    {
        var layout = Fixture();

        Assert.Equal((200f, 540f), layout.At(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", 1f, 2f));
        Assert.Equal((1f, 2f), layout.At(CampaignLayout.RosterSection, "CM_B_NOSUCH", 1f, 2f));
        Assert.Equal((1f, 2f), layout.At("NoSuchSection", "CM_B_DELETEPLAYER", 1f, 2f));
        Assert.Equal((240f, 300f, 230f), layout.Box(CampaignLayout.RosterSection, "CM_E_NAME", 1f, 2f, 3f));
        Assert.Equal((1f, 2f, 3f), layout.Box(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", 1f, 2f, 3f));
        Assert.Equal(24, layout.Int(CampaignLayout.RosterSection, "CM_L_PLAYERS", "ItemHeight", 20));
        Assert.Equal(5, layout.Int(CampaignLayout.RosterSection, "CM_L_PLAYERS", "TotalDisplayed", 7));
        Assert.Equal(20, layout.Int(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", "ItemHeight", 20));
        Assert.Equal(16, layout.Int(CampaignLayout.AmmoSection, "OL_D_AMMO0", "ItemHeight", 15));
        Assert.Equal((560f, 100f, 180f), layout.Box(CampaignLayout.AmmoSection, "OL_S_AMMODESC", 1f, 2f, 3f));
    }

    /// <summary>A row's own art carries the decoder's frame count; an arrow named in another
    /// column and a macro's bitmap carry none, so those keep the fallback's frames.</summary>
    [Fact]
    public void ArtComesWithItsRowsFramesAndAMacroKeepsTheFallbacks()
    {
        var layout = Fixture();

        var icon = layout.Art(CampaignLayout.DialogSection, "MB_P_ICON", Placeholder);
        Assert.Equal(new BoardArt(BoardArtLibrary.Ui, "PM_Icons.png", 3), icon);
        var button = layout.Art(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", Placeholder);
        Assert.Equal(new BoardArt(BoardArtLibrary.Ui, "PM_B_Delete.png", 4), button);
        var slider = layout.Art(CampaignLayout.ContentsSection, "SBTOC_L_TOCList", Placeholder, "Slider");
        Assert.Equal(new BoardArt(BoardArtLibrary.Ui, "PM_B_Slider.png", 4), slider);
        Assert.Same(Placeholder, layout.Art(CampaignLayout.ContentsSection, "SBTOC_L_TOCList", Placeholder, "NoSuchColumn"));
        Assert.Same(Placeholder, layout.Art(CampaignLayout.RosterSection, "CM_B_NOSUCH", Placeholder));
        Assert.Equal(new BoardArt(BoardArtLibrary.Ui, "PM_B_DropDown.png", 4), layout.GlobalArt("GN_DROPDOWN", Placeholder));
        Assert.Same(Placeholder, layout.GlobalArt("GN_DROPUP", Placeholder));
    }

    /// <summary>The three justifications the renderer draws read through; the layout's 3 and 4
    /// and a row with none are the fallback.</summary>
    [Fact]
    public void JustificationReadsTheThreeTheRendererHas()
    {
        var layout = Fixture();

        Assert.Equal(BoardJustify.Center, layout.Justify(CampaignLayout.ContentsSection, "SBTOC_T_CHARACTER", BoardJustify.Right));
        Assert.Equal(BoardJustify.Right, layout.Justify(CampaignLayout.ContentsSection, "SBTOC_T_MISSIONS", BoardJustify.Right));
        Assert.Equal(BoardJustify.Left, layout.Justify(CampaignLayout.DialogSection, "MB_T_MESSAGE", BoardJustify.Right));
        Assert.Equal(BoardJustify.Right, layout.Justify(CampaignLayout.ContentsSection, "SBTOC_T_NOSUCH", BoardJustify.Right));
    }

    [Fact]
    public void AZoomFamilyNeedsAllThreeBoxes()
    {
        var layout = Fixture();

        var b = layout.ZoomFamily('B');
        Assert.NotNull(b);
        Assert.Equal(
            new ScrapbookZoomFamily(24f, 55f, 670f, 82f, 0f, 0f, 700f, 550f, 20f, 140f, 674f, 373f),
            b!.Value);
        Assert.Null(layout.ZoomFamily('C'));
        Assert.Null(layout.ZoomFamily('Z'));
        Assert.Null(CampaignLayout.Fallback.ZoomFamily('B'));
    }

    /// <summary>The profile screen composed over the fixture: the background pane, DELETE PLAYER
    /// and CANCEL follow their rows in position and art, the name field follows CM_E_NAME, and
    /// CONTINUE moves with its row while keeping its measured bitmap, which is the pinned art. The
    /// same page over the fallback composes the hardcoded chrome.</summary>
    [Fact]
    public void TheRosterComposesOverTheLayoutAndKeepsThePinnedArt()
    {
        var fixture = Fixture();
        var moved = CampaignBoards.For(NewRoster(fixture), 0, layout: fixture);
        var kept = CampaignBoards.For(NewRoster(CampaignLayout.Fallback), 0, layout: CampaignLayout.Fallback);

        var pane = Assert.Single(moved.Backdrop, p => p.Art.Name == "PM_Panel.png");
        Assert.Equal((180f, 240f), (pane.X, pane.Y));
        Assert.Contains(kept.Backdrop, p => p.Art.Name == "CM_BackGround.png" && p.X == 193f && p.Y == 251f);

        var delete = Assert.Single(moved.Plaques, p => p.Row == 2);
        Assert.Equal(("PM_B_Delete.png", 200f, 540f), (delete.Art.Name, delete.X, delete.Y));
        var keptDelete = Assert.Single(kept.Plaques, p => p.Row == 2);
        Assert.Equal(("CM_B_DeletePlayer.png", 193f, 547f), (keptDelete.Art.Name, keptDelete.X, keptDelete.Y));

        var start = Assert.Single(moved.Plaques, p => p.Row == 1);
        Assert.Equal(("CM_B_Start.png", 470f, 290f), (start.Art.Name, start.X, start.Y));
        var keptStart = Assert.Single(kept.Plaques, p => p.Row == 1);
        Assert.Equal(("CM_B_Start.png", 474f, 293f), (keptStart.Art.Name, keptStart.X, keptStart.Y));

        var field = Assert.Single(moved.Lines, l => l.Row == 0);
        Assert.Equal((240f, 300f, 230f), (field.X, field.Y, field.Width));
        var keptField = Assert.Single(kept.Lines, l => l.Row == 0);
        Assert.Equal((243f, 297f, 221f), (keptField.X, keptField.Y, keptField.Width));
    }

    /// <summary>The flight check's paper plaques take their row's x and art and keep their
    /// measured y, and the ammo screen's description column takes its row's x and width and keeps
    /// its measured y.</summary>
    [Fact]
    public void PinnedCoordinatesStayMeasuredWhileTheRestOfTheRowReads()
    {
        var fixture = Fixture();

        var plaque = CampaignBoards.SlotOf(CampaignScreen.FlightCheck, BoardButton.ChangePlane, fixture);
        Assert.NotNull(plaque);
        Assert.Equal(("PM_B_Paper.png", 120f, 131f), (plaque!.Value.Art.Name, plaque.Value.X, plaque.Value.Y));
        var fallback = CampaignBoards.SlotOf(CampaignScreen.FlightCheck, BoardButton.ChangePlane);
        Assert.Equal(("FC_B_PaperButton.Png", 128f, 131f), (fallback!.Value.Art.Name, fallback.Value.X, fallback.Value.Y));

        // Both description panes take their column from their own row and keep their measured y.
        Assert.Equal((560f, 92f, 180f), CampaignBoards.DetailSlot(CampaignScreen.Ammo, BoardDetailPane.Upper, fixture));
        Assert.Equal((560f, 328f, 180f), CampaignBoards.DetailSlot(CampaignScreen.Ammo, BoardDetailPane.Lower, fixture));
        Assert.Equal((566f, 92f, 172f), CampaignBoards.DetailSlot(CampaignScreen.Ammo, BoardDetailPane.Upper));
        Assert.Equal((566f, 328f, 172f), CampaignBoards.DetailSlot(CampaignScreen.Ammo, BoardDetailPane.Lower));
        Assert.Null(CampaignBoards.DetailSlot(CampaignScreen.Cabin, BoardDetailPane.Upper, fixture));
        Assert.Null(CampaignBoards.DetailSlot(CampaignScreen.Cabin, BoardDetailPane.Lower, fixture));
    }

    /// <summary>The message box reads its internal geometry off <c>[@MessageBox@]</c> and keeps
    /// the screen position the reference puts the art at.</summary>
    [Fact]
    public void TheDialogReadsItsRowsInsideTheCentredBox()
    {
        var modal = new CampaignModal("Exported.", "OK", null);
        var moved = CampaignBoards.Dialog(modal, Fixture());
        var kept = CampaignBoards.Dialog(modal);

        Assert.Equal(("PM_Box.png", 195f, 150f), (moved.Pictures[0].Art.Name, moved.Pictures[0].X, moved.Pictures[0].Y));
        Assert.Equal(("MB_Background.png", 195f, 150f), (kept.Pictures[0].Art.Name, kept.Pictures[0].X, kept.Pictures[0].Y));
        Assert.Equal(("PM_Icons.png", 225f, 210f, 3), (moved.Pictures[1].Art.Name, moved.Pictures[1].X, moved.Pictures[1].Y, moved.Pictures[1].Art.Frames));
        Assert.Equal((231f, 215f), (kept.Pictures[1].X, kept.Pictures[1].Y));
        Assert.Equal((365f, 400f), (moved.Pictures[2].X, moved.Pictures[2].Y));
        Assert.Equal((369f, 404f), (kept.Pictures[2].X, kept.Pictures[2].Y));
        Assert.Equal((285f, 222f, 280f), (moved.Lines[0].X, moved.Lines[0].Y, moved.Lines[0].Width));
        Assert.Equal((289f, 220f, 282f), (kept.Lines[0].X, kept.Lines[0].Y, kept.Lines[0].Width));

        // The one-button box draws OK on the strip's normal frame in the box's own white, as the
        // reference shows it with the pointer elsewhere; a palette ink would vanish on a paper screen.
        Assert.Equal(1, kept.Pictures[2].Frame);
        var ok = Assert.Single(kept.Lines, l => l.Text == "OK");
        Assert.Equal(BoardInk.Dialog, ok.Ink);
    }

    /// <summary>The table of contents reads its list row once when built: the window, the row
    /// height and the three scroll bitmaps.</summary>
    [Fact]
    public void TheContentsListReadsItsRowWhenBuilt()
    {
        var fixture = Fixture();
        var flow = new CampaignFlow(new CampaignProfileStore(Path.Combine(TestData.TempDir(), "Profiles")), UiStrings.Empty, layout: fixture);
        flow.GoTo(CampaignScreen.PreviousMissions);

        var board = CampaignBoards.For(flow.Page, 0, layout: fixture);

        Assert.Contains(board.Lines, l => l.Text == "Previous Missions" && l.X == 420f && l.Y == 110f && l.Width == 310f && l.Justify == BoardJustify.Right);
        Assert.Contains(board.Lines, l => l.Y == 60f && l.Width == 310f && l.Justify == BoardJustify.Center);
    }

    private static void AssertEveryReadFallsBack(CampaignLayout layout)
    {
        Assert.Equal((1f, 2f), layout.At(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", 1f, 2f));
        Assert.Equal((1f, 2f, 3f), layout.Box(CampaignLayout.RosterSection, "CM_E_NAME", 1f, 2f, 3f));
        Assert.Equal(9, layout.Int(CampaignLayout.RosterSection, "CM_L_PLAYERS", "ItemHeight", 9));
        Assert.Same(Placeholder, layout.Art(CampaignLayout.RosterSection, "CM_B_DELETEPLAYER", Placeholder));
        Assert.Same(Placeholder, layout.GlobalArt("GN_DROPDOWN", Placeholder));
        Assert.Equal(BoardJustify.Right, layout.Justify(CampaignLayout.ContentsSection, "SBTOC_T_CHARACTER", BoardJustify.Right));
        Assert.Null(layout.ZoomFamily('B'));
        var board = CampaignBoards.For(NewRoster(layout), 1, layout: layout);
        var start = Assert.Single(board.Plaques, p => p.Row == 1);
        Assert.Equal(("CM_B_Start.png", 474f, 293f), (start.Art.Name, start.X, start.Y));
    }

    private static CampaignLayout Fixture() => CampaignLayout.Over(MenuLayoutReaderTests.OriginalLayout());

    private static ICampaignPage NewRoster(CampaignLayout layout)
    {
        var flow = new CampaignFlow(new CampaignProfileStore(Path.Combine(TestData.TempDir(), "Profiles")), UiStrings.Empty, layout: layout);
        return flow.Page;
    }
}
