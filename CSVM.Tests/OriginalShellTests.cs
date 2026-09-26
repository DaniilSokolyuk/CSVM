using System.Linq;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Original presentation's engine-free shell over the hand-authored layout fixture. It covers
/// the top level's rows and states, pointer hit-testing and rollover, and keyboard and pad focus
/// by column. The Free Flight picks and launch, the Options chooser, the exits, and the
/// composition each state draws are here too. Every rectangle here is the fixture's invented
/// geometry; the original's is read the same way.
/// </summary>
public class OriginalShellTests
{
    private const float Dt = 1f / 60f;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Right = new() { MoveX = 1 };

    [Fact]
    public void TheTopLevelIsTheSixDecodedRowsPlusTheThreeRemakeDoorsAndOpensFocusedOnFreeFlight()
    {
        var shell = Shell(out _);

        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.Equal(
            new[] { OriginalShell.FreeFlightKey, OriginalShell.DogfightKey, OriginalShell.JoinBoardKey, "MM_B_CAMPAIGN", "MM_B_INSTANTACTION", "MM_B_MULTIPLAYER", "MM_B_PREFERENCES", "MM_B_CREDITS", "MM_B_QUIT" },
            shell.Rows.Select(r => r.Key));
        Assert.Equal(OriginalShell.FreeFlightKey, shell.FocusedKey);
        // The decoded rows with no remake destination yet are disabled; Instant Action,
        // Preferences (the Options door), Credits and Quit react. The hangar is reached through
        // Instant Action's Build Custom Plane, and MULTIPLAYER is the original's network play.
        Assert.Equal(new[] { true, true, true, false, true, false, true, true, true }, shell.Rows.Select(r => r.Enabled));
        // A decoded button's rectangle is its authored corner and its measured strip's frame.
        var quit = shell.Rows.Single(r => r.Key == "MM_B_QUIT");
        Assert.Equal((280f, 530f, 240f, 50f), (quit.X, quit.Y, quit.Width, quit.Height));
        Assert.Equal(4, quit.Art!.Frames);
    }

    [Fact]
    public void ThePointerHoveringALiveButtonTakesFocusAndCuesARolloverOnceAndADisabledOneDoesNeither()
    {
        var shell = Shell(out _);

        var step = shell.Step(Pointer(290f, 540f));
        Assert.Equal("MM_B_QUIT", shell.FocusedKey);
        Assert.Equal(new[] { OriginalCues.Rollover }, step.Cues);
        Assert.True(step.Changed);

        step = shell.Step(Pointer(295f, 545f));
        Assert.Empty(step.Cues);

        step = shell.Step(Pointer(290f, 290f));
        Assert.Equal("MM_B_QUIT", shell.FocusedKey);
        Assert.Empty(step.Cues);
        Assert.Equal(3, shell.Hover);
    }

    [Fact]
    public void AClickOnTheDoorOpensFreeFlightWithAClickCueAndThePressedFrameDrawsWhileHeld()
    {
        var shell = Shell(out _);
        var door = shell.Rows[0];

        shell.Step(Pointer(door.X + 2f, door.Y + 2f, pressed: true, clicked: true));
        var plaque = shell.Compose().Plaques.Single(p => p.Label == "FREE FLIGHT");
        Assert.Equal(3, plaque.Frame);
        Assert.Equal(BoardInk.LabelActivate, plaque.Ink);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);

        var step = shell.Step(Pointer(door.X + 2f, door.Y + 2f));
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Contains(OriginalCues.Click, step.Cues);
        Assert.Null(step.Exit);
    }

    [Fact]
    public void APressReleasedOffTheRowItLandedOnFiresNothingAndDropsTheHeldFrame()
    {
        var shell = Shell(out _);
        var door = shell.Rows[0];
        var other = shell.Rows.First(r => r.Key == "MM_B_PREFERENCES");

        shell.Step(Pointer(door.X + 2f, door.Y + 2f, pressed: true, clicked: true));
        Assert.Equal(door.Key, shell.ArmedKey);

        // Still held, the pointer leaves for another live plaque: neither draws held, and the
        // release there activates neither of them.
        shell.Step(Pointer(other.X + 2f, other.Y + 2f, pressed: true));
        Assert.Empty(shell.Compose().Plaques.Where(p => p.Frame == 3));
        var step = shell.Step(Pointer(other.X + 2f, other.Y + 2f));
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.Equal(string.Empty, shell.ArmedKey);
        Assert.DoesNotContain(OriginalCues.Click, step.Cues);
    }

    [Fact]
    public void ThePointerBitmapAnswersAnEnterOrALeaveAndNotWhateverTheScreenPutsUnderIt()
    {
        var shell = Shell(out _);
        var door = shell.Rows[0];
        float x = door.X + 2f;
        float y = door.Y + 2f;

        shell.Step(Pointer(1f, 1f));
        Assert.Equal("passivepointerz.png", PointerArt(shell));
        shell.Step(Pointer(x, y));
        Assert.Equal("activepointerz.png", PointerArt(shell));

        // A screen drawn under a still pointer is no enter, so the bitmap it arrived with stands
        // even where nothing live is under it now.
        shell.Open(OriginalScreen.Options);
        Assert.Equal(string.Empty, HitTestKey(shell, x, y));
        shell.Step(Pointer(x, y));
        Assert.Equal("activepointerz.png", PointerArt(shell));

        shell.Step(Pointer(x + 1f, y));
        Assert.Equal("passivepointerz.png", PointerArt(shell));
    }

    [Fact]
    public void KeyboardFocusWalksEnabledRowsWithinAColumnAndWrapsAndSkipsDisabledOnes()
    {
        var shell = Shell(out _);

        shell.Step(Down);
        Assert.Equal(OriginalShell.DogfightKey, shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal(OriginalShell.JoinBoardKey, shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal("MM_B_INSTANTACTION", shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal("MM_B_PREFERENCES", shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal(OriginalShell.CreditsDoorKey, shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal("MM_B_QUIT", shell.FocusedKey);
        shell.Step(Down);
        Assert.Equal(OriginalShell.FreeFlightKey, shell.FocusedKey);
        shell.Step(Up);
        Assert.Equal("MM_B_QUIT", shell.FocusedKey);
    }

    [Fact]
    public void FreeFlightPicksAChapterAndAnAirframeAcrossTwoColumnsThenFliesThroughTheFeature()
    {
        var shell = Shell(out var free);
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Equal("C1", shell.FocusedKey);
        Assert.False(shell.Rows.Single(r => r.Key == OriginalShell.FlyKey).Enabled);

        shell.Step(Down);
        shell.Step(Down);
        shell.Step(Accept);
        Assert.Equal("C1C", shell.PickedChapter);
        Assert.Equal("C1C", free.Chapter?.Code);

        shell.Step(Right);
        Assert.Equal(1, shell.Rows[shell.Focus].Column);
        Assert.Equal(OriginalShell.AirframeKey(2), shell.FocusedKey);
        shell.Step(Accept);
        Assert.Equal("player_balmoral", shell.PickedAirframe);
        Assert.True(shell.Rows.Single(r => r.Key == OriginalShell.FlyKey).Enabled);

        // Up from the top of the airframe column wraps onto FLY, the column's last row.
        shell.Step(Up);
        shell.Step(Up);
        shell.Step(Up);
        Assert.Equal(OriginalShell.FlyKey, shell.FocusedKey);
        var step = shell.Step(Accept);
        var launch = Assert.IsType<LaunchExit>(step.Exit);
        Assert.Equal("C1C", launch.Chapter);
        Assert.Equal(MenuMode.Free, launch.Mode);
        Assert.Equal("player_balmoral", Assert.Single(launch.Seats).PlaneNode);
        Assert.Contains(OriginalCues.Click, step.Cues);
    }

    [Fact]
    public void ListRowsUnderThePointerTakeFocusWithoutARolloverCueAndAClickPicks()
    {
        var shell = Shell(out _);
        shell.Open(OriginalScreen.FreeFlight);
        var hawaii = shell.Rows.Single(r => r.Key == "C3");

        var step = shell.Step(Pointer(hawaii.X + 10f, hawaii.Y + 5f));
        Assert.Equal("C3", shell.FocusedKey);
        Assert.Empty(step.Cues);

        step = Click(shell, hawaii.X + 10f, hawaii.Y + 5f);
        Assert.Equal("C3", shell.PickedChapter);
        Assert.Empty(step.Cues);
        Assert.Contains(shell.Compose().Fills, f => f.X == hawaii.X && f.Y == hawaii.Y && !f.Border);
    }

    [Fact]
    public void BackLeavesFreeFlightForTheTopLevelAndQuitsFromTheTopLevel()
    {
        var shell = Shell(out _);
        shell.Step(Accept);
        shell.Step(Down);
        shell.Step(Accept);

        var step = shell.Step(Back);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.Null(step.Exit);
        Assert.Equal("C1B", shell.PickedChapter);

        step = shell.Step(Back);
        Assert.IsType<QuitExit>(step.Exit);

        shell.Step(Down);
        shell.Step(Down);
        shell.Step(Down);
        shell.Step(Down);
        shell.Step(Down);
        shell.Step(Down);
        Assert.Equal("MM_B_QUIT", shell.FocusedKey);
        Assert.IsType<QuitExit>(shell.Step(Accept).Exit);
    }

    [Fact]
    public void AReturnToTheTopLevelKeepsTheListCursorsAndDropsTheAirframePick()
    {
        var shell = Shell(out _);
        shell.Step(Accept);
        shell.Step(Down);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Down);
        shell.Step(Accept);
        Assert.NotNull(shell.PickedAirframe);

        shell.ReturnToTopLevel();

        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.Null(shell.PickedAirframe);
        Assert.Equal("C1B", shell.PickedChapter);
        shell.Step(Accept);
        Assert.Equal(OriginalShell.AirframeKey(2), shell.FocusedKey);
    }

    /// <summary>A drag holds the pointer, so the click that took hold of a thumb is spent on it
    /// and the row the thumb is drawn over is not activated. The shell makes that distinction once
    /// for anything that holds the pointer; the list thumb is the hold an engine-free screen has,
    /// and a slider row joins the same gate (<see cref="SliderControlTests"/> drives that
    /// side).</summary>
    [Fact]
    public void ADragHoldsThePointerSoItsClickActivatesNoRowUnderIt()
    {
        // A roster longer than the eleven-row window, so the aircraft column has a thumb at all.
        var setup = new PlayerSetupFeature();
        var roster = OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()).ToList();
        roster.AddRange(roster.Take(3).ToList());
        setup.SetRoster(roster);
        setup.Join(new ScriptedMenuSeat());
        var shell = new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), setup, Measure);
        shell.Open(OriginalScreen.FreeFlight);

        var window = shell.Lists.Single(l => l.Key == "AIRFRAMES").Window;
        Assert.True(window.Scrolls);
        // The thumb runs down the column's own right edge, so the point it stands on is an
        // aircraft row that a click would otherwise pick.
        float x = window.ThumbX + 1f;
        float y = window.ThumbY + 1f;
        Assert.StartsWith("AIRFRAME", HitTestKey(shell, x, y), System.StringComparison.Ordinal);

        shell.Step(Pointer(x, y, pressed: true, clicked: true));
        Assert.Equal("AIRFRAMES", shell.Dragging);
        Assert.Null(shell.PickedAirframe);

        shell.Step(Pointer(x, y + window.Height, pressed: true));
        shell.Step(Pointer(x, y + window.Height));
        Assert.Null(shell.Dragging);
        Assert.Null(shell.PickedAirframe);
    }

    [Fact]
    public void TheOptionsScreenIsComposedOverThePreferencesChromeWithItsThreeBuiltDoorsLive()
    {
        var shell = Shell(out _);
        shell.Open(OriginalScreen.Options);

        // The four decoded page doors at their authored corners, the first three live and CONTROLS
        // disabled, then the section's own RETURN TO MAIN MENU.
        Assert.Equal(
            new[] { "PF_B_GAMEOPTIONS", "PF_B_AUDIO", "PF_B_VIDEO", "PF_B_CONTROLS", OriginalShell.OptionsBackKey },
            shell.Rows.Select(r => r.Key));
        Assert.Equal(new[] { true, true, true, false, true }, shell.Rows.Select(r => r.Enabled));
        var back = shell.Rows.Single(r => r.Key == OriginalShell.OptionsBackKey);
        Assert.Equal((460f, 500f, 240f, 50f), (back.X, back.Y, back.Width, back.Height));

        var board = shell.Compose();
        Assert.Equal(new[] { "PM_Logo.png", "PP_Back.png" }, board.Pictures.Select(p => p.Art.Name));
        Assert.Equal((100f, 200f), (board.Pictures[1].X, board.Pictures[1].Y));
        Assert.Contains(board.Lines, l => l.Text == "PREFERENCES" && l.X == 120f && l.Justify == BoardJustify.Center);
        Assert.Contains(board.Lines, l => l.Text == "Change the audio settings." && l.X == 340f && l.Y == 320f);
        Assert.Contains(board.Lines, l => l.Text == "Change the difficulty level and default views." && l.X == 340f);
        Assert.Equal(1, board.Plaques.Single(p => p.Art.Name == "PP_B_Audio.png").Frame);
        Assert.Equal(0, board.Plaques.Single(p => p.Art.Name == "PP_B_Controls.png").Frame);
        Assert.Equal(2, board.Plaques.Single(p => p.Art.Name == "PP_B_GameOptions.png").Frame);
        Assert.Equal(new MenuLayoutColor(0xFF, 0xFF, 0xDD, 0xC4), shell.PreferencesInks.Text);
        Assert.Equal(new MenuLayoutColor(0xFF, 0xC0, 0xBA, 0xAD), shell.PreferencesInks.Title);

        // A click on RETURN TO MAIN MENU leaves; Back leaves too.
        Click(shell, back.X + 2f, back.Y + 2f);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        shell.Open(OriginalScreen.Options);
        shell.Step(Back);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
    }

    /// <summary>The seam to the options module (<see cref="OriginalOptionsTests"/> drives the
    /// module alone). Each of the hub's doors opens the page it names on that page's own first row.
    /// The cursor's walk down a page is over the module's rows, and Back on one is the module's
    /// answer.</summary>
    [Fact]
    public void ThePreferencesDoorsOpenTheModulesPagesAndTheWalkIsOverItsRows()
    {
        var shell = Shell(out _);
        shell.Open(OriginalScreen.Options);

        Click(shell, OriginalOptionsScreen.GameOptionsDoorKey);
        Assert.Equal(OriginalScreen.GameOptions, shell.Screen);
        Assert.Equal(OriginalOptionsScreen.DifficultyKey, shell.FocusedKey);

        // Down is the shell's own column walk over the page's rows. Right is the module's own
        // sideways step, which changes the value there and keeps the focus.
        shell.Step(Down);
        Assert.Equal(OriginalOptionsScreen.DefaultViewKey, shell.FocusedKey);
        shell.Step(Right);
        Assert.Equal(OriginalOptionsScreen.DefaultViewKey, shell.FocusedKey);
        Assert.Equal("Cockpit", Row(shell, OriginalOptionsScreen.DefaultViewKey).Label);

        // Back on the page is the module's, answered the way that page's own CANCEL CHANGES is.
        Assert.Null(shell.Step(Back).Exit);
        Assert.Equal(OriginalScreen.Options, shell.Screen);

        Click(shell, OriginalOptionsScreen.AudioDoorKey);
        Assert.Equal(OriginalScreen.Audio, shell.Screen);
        Assert.Equal(OriginalOptionsScreen.AudioMasterKey, shell.FocusedKey);
        shell.Step(Back);

        Click(shell, OriginalOptionsScreen.VideoDoorKey);
        Assert.Equal(OriginalScreen.Video, shell.Screen);
        Assert.Equal(OriginalOptionsScreen.MonitorKey, shell.FocusedKey);
        shell.Step(Back);
        Assert.Equal(OriginalScreen.Options, shell.Screen);

        // The CONTROLS door stands dead with no rebinding feature behind it, which is where this
        // shell's walk stops; OriginalControlsTests drives the live one.
        Assert.False(Row(shell, OriginalOptionsScreen.ControlsDoorKey).Enabled);
    }

    [Fact]
    public void TheTopLevelComposesTheDecodedPanesAndPlaquesInTheirStateFramesAndThePointerLast()
    {
        var shell = Shell(out _);
        shell.Step(Pointer(290f, 440f));

        var board = shell.Compose();
        Assert.Equal(new[] { "PM_Logo.png", "PM_Frame.png" }, board.Pictures.Select(p => p.Art.Name));
        Assert.Equal((100f, 10f), (board.Pictures[0].X, board.Pictures[0].Y));
        var campaign = board.Plaques.Single(p => p.Art.Name == "PM_B_Campaign.png");
        Assert.Equal(0, campaign.Frame);
        var preferences = board.Plaques.Single(p => p.Art.Name == "PM_B_Preferences.png");
        Assert.Equal(2, preferences.Frame);
        var quit = board.Plaques.Single(p => p.Art.Name == "PM_B_Quit.png");
        Assert.Equal(1, quit.Frame);
        var door = board.Plaques.Single(p => p.Label == "FREE FLIGHT");
        Assert.Equal("PM_B_Paper.png", door.Art.Name);
        Assert.Equal(BoardInk.LabelNormal, door.Ink);
        var pointer = Assert.Single(board.Overlays);
        var picture = Assert.Single(pointer.Pictures);
        Assert.Equal("activepointerz.png", picture.Art.Name);
        Assert.Equal((290f, 440f), (picture.X, picture.Y));

        shell.Step(Pointer(10f, 10f));
        Assert.Equal("passivepointerz.png", Assert.Single(shell.Compose().Overlays).Pictures[0].Art.Name);
    }

    [Fact]
    public void TheFlagIsTheBottomLayerOfBothScreensThatAuthorItAtTheRowsOwnCornerAndScale()
    {
        var shell = Shell(out _);

        var flag = Assert.Single(shell.Compose().Backdrop);
        Assert.Equal(BoardArtLibrary.Movie, flag.Art.Library);
        Assert.Equal("PM_Flag.MPG", flag.Art.Name);
        // The row's own 250 percent of the picture's 320x240, at the row's own corner, which fills
        // the authored space exactly. Nothing here writes either number down.
        Assert.Equal((0f, 0f, 800f, 600f), (flag.X, flag.Y, flag.Width, flag.Height));

        shell.Open(OriginalScreen.Options);
        Assert.Equal("PM_Flag.MPG", Assert.Single(shell.Compose().Backdrop).Art.Name);

        // A screen whose section authors no movie row composes none, however deep in the front end.
        shell.Open(OriginalScreen.Credits);
        Assert.Empty(shell.Compose().Backdrop);
    }

    [Fact]
    public void AFlagTheMeasurerCannotReadComposesNothingAndLeavesTheRestOfTheScreenWhole()
    {
        var free = new FreeFlightFeature();
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        var shell = new OriginalShell(
            MenuLayoutReaderTests.OriginalLayout(), free, setup,
            art => art == "PM_Flag.MPG" ? null : Measure(art));

        var board = shell.Compose();
        Assert.Empty(board.Backdrop);
        Assert.Equal(new[] { "PM_Logo.png", "PM_Frame.png" }, board.Pictures.Select(p => p.Art.Name));
        Assert.Equal(9, board.Plaques.Count);
    }

    [Fact]
    public void TheInksComeOffTheLayoutsGlobalsAndThePaperPlaquesOwnTail()
    {
        var shell = Shell(out _);

        Assert.Equal(new MenuLayoutColor(0xFF, 0xBC, 0xBC, 0xBC), shell.Inks.Disabled);
        Assert.Equal(new MenuLayoutColor(0xFF, 0xFF, 0xFF, 0xFF), shell.Inks.Active);
        Assert.Equal(new MenuLayoutColor(0xFF, 0x13, 0x3D, 0x77), shell.Inks.LabelNormal);
        Assert.Equal(new MenuLayoutColor(0xFF, 0x37, 0x4D, 0x6B), shell.Inks.LabelRollover);
        Assert.Equal(new MenuLayoutColor(0xFF, 0x00, 0x00, 0x00), shell.Inks.LabelDepressed);
    }

    [Fact]
    public void ALayoutWithNoPlaqueRowStillComposesTextButtonsAsOutlinedLabels()
    {
        var layout = MenuLayout.Parse(
            "{\"schema\":1,\"widgetTypes\":[],\"globals\":[],\"screens\":[{\"section\":\"MainMenu\",\"script\":\"\",\"widgets\":[]}],\"navigation\":[],\"externalAssets\":[]}");
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(), _ => null);

        var rows = shell.Rows;
        Assert.Equal(
            new[] { OriginalShell.FreeFlightKey, OriginalShell.DogfightKey, OriginalShell.JoinBoardKey },
            rows.Select(r => r.Key));
        var board = shell.Compose();
        Assert.Empty(board.Plaques);
        Assert.Contains(board.Lines, l => l.Text == "FREE FLIGHT");
        Assert.Contains(board.Lines, l => l.Text == "DOGFIGHT");
        Assert.Contains(board.Lines, l => l.Text == "JOIN BOARD");
        Assert.Contains(board.Fills, f => f.Border);
        Assert.Equal(new MenuLayoutColor(0xFF, 0xFF, 0xFF, 0xFF), shell.Inks.LabelNormal);
    }

    /// <summary>ABOUT raises the box the credits screen asks for rather than the shared one: the
    /// <c>ma_</c> widget set over its own background, centred by that background's own size, with
    /// the skull the icon row draws at its one authored place and the set's own single OK.</summary>
    [Fact]
    public void AboutRaisesTheMaWidgetSetCentredOnItsOwnBackgroundWithTheSkullAndOneOk()
    {
        var shell = CreditsShell();
        var about = shell.Rows.Single(r => r.Key == OriginalShell.CreditsAboutKey);
        Assert.True(about.Enabled);

        Click(shell, about.X + (about.Width / 2f), about.Y + (about.Height / 2f));

        Assert.NotNull(shell.Dialog);
        Assert.Equal(DialogIcon.Death, shell.Dialog!.Icon);
        var box = shell.Compose().Overlays[0];
        // The fixture's 400x300 About background, so the script's own centring lands it here.
        Assert.Equal(new[] { "PM_AboutBox.png", "PM_Icons.png", "PM_B_Small.png" }, box.Pictures.Select(p => p.Art.Name));
        Assert.Equal((200f, 150f), (box.Pictures[0].X, box.Pictures[0].Y));
        // The icon keeps the mb_ row's place inside the pane and takes the skull frame.
        Assert.Equal((230f, 210f, 2), (box.Pictures[1].X, box.Pictures[1].Y, box.Pictures[1].Frame));
        // MA_B_CENTER's own 220,370, not MB_B_CENTER's 170,250.
        Assert.Equal((420f, 520f), (box.Pictures[2].X, box.Pictures[2].Y));
        var message = box.Lines[0];
        Assert.Equal((295f, 225f, 370f), (message.X, message.Y, message.Width));
        Assert.EndsWith("???", message.Text);
        Assert.DoesNotContain("<B>", message.Text);
        Assert.DoesNotContain("[COUR9]", message.Text);
        Assert.Equal("OK", box.Lines[1].Text);

        Click(shell, box.Pictures[2].X + 10f, box.Pictures[2].Y + 10f);
        Assert.Null(shell.Dialog);
    }

    /// <summary>A dialog answer's strip frame follows the pointer and nothing else: the rollover
    /// frame while the pointer is on it, the normal frame the moment the pointer is anywhere else,
    /// and the normal frame for a player who has no pointer at all. The cursor that a pad walks is
    /// the focus mark instead, drawn clear of the strip and only while the pointer is elsewhere,
    /// so the box never opens with an answer already lit.</summary>
    [Fact]
    public void ADialogAnswerTakesTheRolloverFrameFromThePointerAndTheFocusMarkFromTheCursor()
    {
        var shell = CreditsShell();

        // Raised from the pad, no pointer having ever been in play.
        shell.Step(Accept);
        Assert.NotNull(shell.Dialog);
        Assert.Equal(OriginalShell.DialogOkKey, shell.FocusedKey);
        var ok = shell.Rows.Single();
        Assert.Equal(1, AnswerFrame(shell));
        var mark = Assert.Single(Marks(shell));
        Assert.True(mark.Border);
        Assert.Equal((ok.X - 3f, ok.Y - 3f, ok.Width + 6f, ok.Height + 6f), (mark.X, mark.Y, mark.Width, mark.Height));
        Assert.Equal((shell.Inks.Disabled.R, shell.Inks.Disabled.G, shell.Inks.Disabled.B), (mark.R, mark.G, mark.B));

        // The mark stands over the box, or the box's own background would paint it out.
        var overlays = shell.Compose().Overlays;
        Assert.Equal(mark, Assert.Single(overlays[^1].Fills));
        Assert.NotEmpty(overlays[^2].Lines);

        shell.Step(Pointer(ok.X + 10f, ok.Y + 10f));
        Assert.Equal(2, AnswerFrame(shell));
        Assert.Empty(Marks(shell));

        // Off the answer and the strip is back where the reference shot has it, the mark returning
        // because the hover left the focus on the answer it walked onto.
        shell.Step(Pointer(ok.X - 40f, ok.Y + 10f));
        Assert.Equal(1, AnswerFrame(shell));
        Assert.Single(Marks(shell));

        // A press on the answer is the depressed frame, and no mark stands under a lit answer.
        shell.Step(Pointer(ok.X + 10f, ok.Y + 10f, pressed: true, clicked: true));
        Assert.Equal(3, AnswerFrame(shell));
        Assert.Empty(Marks(shell));
    }

    /// <summary>The credits screen's hidden line: the secondary button held inside the authored
    /// region shows it and letting go there takes it away, the line standing at its own corner in
    /// its own ink with the stored characters shifted back down.</summary>
    [Fact]
    public void TheHiddenCreditsLineStandsWhileTheSecondaryButtonIsHeldInsideItsRegion()
    {
        var shell = CreditsShell();

        shell.Step(Pointer(320f, 320f));
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Ink == BoardInk.Secret);

        shell.Step(Pointer(320f, 320f, right: true));
        var line = Assert.Single(shell.Compose().Lines.Where(l => l.Ink == BoardInk.Secret));
        Assert.Equal((288f, 308f), (line.X, line.Y));
        Assert.Equal(Shifted("xl#ghy#ohdg=#ulfk#hl}hqkrhihu"), line.Text);

        shell.Step(Pointer(320f, 320f));
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Ink == BoardInk.Secret);

        // Outside the region the button does nothing at all.
        shell.Step(Pointer(400f, 400f, right: true));
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Ink == BoardInk.Secret);
        shell.Step(Pointer(400f, 400f));

        // A hold carried out of the region and let go there leaves the line standing, the script's
        // own reading: its whole handler is inside the region test.
        shell.Step(Pointer(320f, 320f, right: true));
        shell.Step(Pointer(400f, 400f, right: true));
        shell.Step(Pointer(400f, 400f));
        Assert.Contains(shell.Compose().Lines, l => l.Ink == BoardInk.Secret);

        // The line goes with the screen, its widget being created deactivated every time.
        shell.Open(OriginalScreen.Credits);
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Ink == BoardInk.Secret);
    }

    /// <summary>The seam to the Instant Action module (<see cref="OriginalInstantActionTests"/>
    /// drives the module alone). The top level's own door opens the module's first screen with the
    /// environment confirmed. Its rows are the ones the shell composes and walks.</summary>
    [Fact]
    public void TheInstantActionDoorOpensTheModulesScreenOnItsFirstRow()
    {
        var shell = Shell(out _);
        Assert.True(Row(shell, "MM_B_INSTANTACTION").Enabled);

        var step = Click(shell, "MM_B_INSTANTACTION");

        Assert.Equal(OriginalScreen.InstantAction, shell.Screen);
        Assert.Contains(OriginalCues.Click, step.Cues);
        Assert.Equal($"{OriginalInstantActionScreen.ContentsKey}:0", shell.FocusedKey);
        Assert.Contains(shell.Rows, r => r.Key == OriginalInstantActionScreen.FlyMissionKey);
    }

    [Fact]
    public void TheKeyboardsColumnWalkLandsOnTheInstantActionModulesOwnRows()
    {
        var shell = Shell(out _);
        Click(shell, "MM_B_INSTANTACTION");

        // Down is the shell's own column walk down the contents window. Right is its crossing into
        // the module's second column, landing on the dropdown level with the row it left. A further
        // Right is the module's own sideways step, changing that value and keeping the focus.
        shell.Step(Down);
        Assert.Equal($"{OriginalInstantActionScreen.ContentsKey}:1", shell.FocusedKey);
        shell.Step(Right);
        Assert.Equal(OriginalInstantActionScreen.WingmenKey, shell.FocusedKey);
        shell.Step(Up);
        Assert.Equal(OriginalInstantActionScreen.PlayerPlaneKey, shell.FocusedKey);
        shell.Step(Right);
        Assert.Equal(OriginalInstantActionScreen.PlayerPlaneKey, shell.FocusedKey);
        Assert.Equal("Stock Hellhound", Row(shell, OriginalInstantActionScreen.PlayerPlaneKey).Label);

        // Back on the screen is the shell's own return, the module declining it.
        Assert.Null(shell.Step(Back).Exit);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
    }

    /// <summary>The seam to the hangar module (<see cref="OriginalHangarTests"/> drives the module
    /// alone). The Build door opens it on the screen the door stands on. A dialog it raises is the
    /// shell's messagebox, and its typing goes through the shell's one text seam. A commit comes
    /// back to the door's screen with both rosters re-read off the store.</summary>
    [Fact]
    public void TheBuildDoorOpensTheHangarModuleAndACommitComesBackWithTheRostersReRead()
    {
        WithHangarShell((shell, hangar, setup, store) =>
        {
            Assert.DoesNotContain(shell.Rows, r => r.Key == "HANGAR");
            Click(shell, "MM_B_INSTANTACTION");
            var door = Row(shell, OriginalInstantActionScreen.BuildKey);
            Assert.True(door.Enabled);
            Click(shell, door.X + 2f, door.Y + 2f);

            Assert.Equal(OriginalScreen.PlaneName, shell.Screen);
            Assert.True(shell.IsHangarScreen);
            Assert.True(hangar.IsOpen);
            Assert.True(shell.CapturingText);
            Assert.Equal(OriginalHangarScreen.NameFieldKey, shell.FocusedKey);

            // The refusal the module raises is the shell's messagebox. Its rows are the answer
            // alone, it is drawn as an overlay, and its OK hands the focus back to the box.
            Click(shell, OriginalHangarScreen.NameOkKey);
            Assert.NotNull(shell.Dialog);
            Assert.Equal(new[] { OriginalShell.DialogOkKey }, shell.Rows.Select(r => r.Key));
            Assert.False(shell.CapturingText);
            Assert.Contains(Box(shell).Lines, l => l.Text == shell.Dialog!.Message);
            Click(shell, OriginalShell.DialogOkKey);
            Assert.Null(shell.Dialog);
            Assert.Equal(OriginalHangarScreen.NameFieldKey, shell.FocusedKey);

            shell.Step(new MenuCommands { Typed = "Ace" });
            Assert.Equal("Ace", shell.Hangar!.HangarName);
            Click(shell, OriginalHangarScreen.NameOkKey);
            Assert.Equal(OriginalScreen.HangarAirframe, shell.Screen);
            Assert.False(shell.CapturingText);

            Click(shell, OriginalHangarScreen.ReadyKey);
            Click(shell, OriginalHangarScreen.PurchaseNowKey);
            Assert.Equal(OriginalScreen.InstantAction, shell.Screen);
            Assert.False(shell.IsHangarScreen);
            Assert.Equal(OriginalInstantActionScreen.BuildKey, shell.FocusedKey);
            Assert.Equal("Ace", shell.Hangar!.LastBuiltPlane);
            Assert.NotNull(store.Load("Ace"));
            Assert.Contains(setup.Roster, a => a.Name == "Ace" && a.IsCustom);
            Assert.Contains(shell.InstantAction.PilotRoster, a => a.Name == "Ace" && a.IsCustom);
        });
    }

    [Fact]
    public void TheKeyboardWalksFromTheHangarDropdownsOntoTheTabBarAndAlongIt()
    {
        WithHangarShell((shell, _, _, _) =>
        {
            shell.OpenHangarTab(OriginalScreen.HangarAirframe, "Ace");
            Assert.Equal(OriginalHangarScreen.AirframeDropKey, shell.FocusedKey);

            // Down is the shell's own column walk, and Right along the bar is the module's sideways
            // step. The standing tab is a sibling like the other five, so the walk steps onto it.
            shell.Step(Down);
            Assert.Equal("PX_B_AIRFRAME", shell.FocusedKey);
            shell.Step(Right);
            Assert.Equal("PX_B_ENGINE", shell.FocusedKey);
            shell.Step(Right);
            shell.Step(Right);
            shell.Step(Right);
            shell.Step(Right);
            Assert.Equal("PX_B_PAINT", shell.FocusedKey);
            shell.Step(Right);
            Assert.Equal(OriginalHangarScreen.SellPlanesKey, shell.FocusedKey);
            shell.Step(Up);
            Assert.Equal("PX_B_PAINT", shell.FocusedKey);
        });
    }

    /// <summary>The seam to the campaign module (<see cref="OriginalCampaignTests"/> drives the
    /// module alone). The top level's own door opens its profile screen. The box there takes seat
    /// 0's typed characters through the shell's one text seam, and the rows are the module's
    /// own.</summary>
    [Fact]
    public void TheCampaignDoorOpensTheModulesProfileScreen()
    {
        WithCampaignShell((shell, campaign, _, _, _) =>
        {
            var door = Row(shell, OriginalShell.CampaignKey);
            Assert.True(door.Enabled);

            var step = Click(shell, door.X + 2f, door.Y + 2f);

            Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);
            Assert.True(campaign.IsOpen && shell.Campaign.IsOpen);
            Assert.Contains(OriginalCues.Click, step.Cues);
            Assert.True(shell.CapturingText);
            Assert.Equal(
                new[] { "ROW:0", "Continue", "DeletePlayer", "CancelProfile" }, shell.Rows.Select(r => r.Key));
            Assert.Equal(0, shell.Focus);
            shell.Step(new MenuCommands { Typed = "Zac" });
            Assert.Equal("Zac", shell.Campaign.RosterName);
        });
    }

    [Fact]
    public void TheKeyboardsColumnWalkLandsOnTheCampaignModulesOwnRows()
    {
        WithCampaignShell((shell, campaign, _, _, _) =>
        {
            Seat(shell, "Zachary");

            // Down is the shell's own column walk over the cabin's plaques. The door it ends on
            // is the module's own way out of the campaign.
            Assert.Equal("NextMission", shell.FocusedKey);
            shell.Step(Down);
            Assert.Equal("PreviousMissions", shell.FocusedKey);
            shell.Step(Down);
            shell.Step(Down);
            Assert.Equal("ReturnToMainMenu", shell.FocusedKey);
            shell.Step(Accept);
            Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
            Assert.False(campaign.IsOpen);
            Assert.False(shell.Campaign.IsOpen);
        });
    }

    /// <summary>The two-answer box as the campaign raises it. The box is the shell's, so its
    /// answers are the rows the pointer and the cursor see, at the messagebox pane's own places.
    /// The mark stands on the one Accept would take, and the rollover frame on whichever the
    /// pointer rests on.</summary>
    [Fact]
    public void TheCampaignsTwoAnswerBoxIsTheShellsOwnMessagebox()
    {
        WithCampaignShell((shell, _, _, _, store) =>
        {
            store.Save(CampaignProfileDef.NewProfile("Zachary"));
            store.RecordLastPlayed("Zachary");
            shell.Campaign.OpenCampaignOver(store);
            Click(shell, "DeletePlayer");

            Assert.NotNull(shell.Dialog);
            Assert.Equal(
                new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey }, shell.Rows.Select(r => r.Key));
            Assert.Equal(OriginalShell.DialogYesKey, shell.FocusedKey);
            Assert.False(shell.CapturingText);
            // The messagebox buttons at their own rows inside the centred pane.
            var yes = shell.Rows[0];
            var no = shell.Rows[1];
            Assert.Equal((195f + 70f, 150f + 250f), (yes.X, yes.Y));
            var box = Box(shell);
            Assert.Contains(box.Lines, l => l.Text == shell.Dialog!.Message);
            Assert.Equal((int)DialogIcon.Query, DialogIconTests.IconFrame(box));

            // Raised from a click, the pointer resting where DELETE PLAYER was. Neither answer is
            // lit, so both keep the normal frame and the mark alone says which Accept would take.
            Assert.Equal(new[] { 1, 1 }, box.Pictures.Skip(2).Select(p => p.Frame));
            var mark = Assert.Single(Marks(shell));
            Assert.True(mark.Border);
            Assert.Equal((yes.X - 3f, yes.Y - 3f), (mark.X, mark.Y));

            // The pointer carries the rollover frame and the cursor with it. A hovered answer is
            // the lit one and wears no mark, and the answer left behind is back on its normal frame.
            shell.Step(Pointer(no.X + 4f, no.Y + 4f));
            Assert.Equal(new[] { 1, 2 }, Box(shell).Pictures.Skip(2).Select(p => p.Frame));
            Assert.Empty(Marks(shell));
            Assert.Equal(OriginalShell.DialogNoKey, shell.FocusedKey);
        });
    }

    /// <summary>A box raised over a campaign screen keeps the messagebox's own inks and strip
    /// frames. It does not take the paper palette the page under it is drawn in.</summary>
    [Fact]
    public void TheBoxOverAPaperCampaignScreenKeepsTheMessageboxsOwnInk()
    {
        WithCampaignShell((shell, _, _, _, store) =>
        {
            store.Save(CampaignProfileDef.NewProfile("Zachary"));
            shell.Campaign.OpenCampaignOver(store);
            Assert.True(shell.Campaign.ShowCabin("Zachary"));
            shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignPlaneSelection);

            shell.Campaign.PressExport();

            Assert.NotNull(shell.Dialog);
            var ok = Assert.Single(shell.Rows);
            Assert.Equal(OriginalShell.DialogOkKey, ok.Key);
            // With no pointer on it OK stands on its normal frame under the focus mark, in the box's
            // white rather than the paper palette.
            var panel = Box(shell);
            Assert.Equal(BoardInk.Dialog, Assert.Single(panel.Lines, l => l.Text == ok.Label).Ink);
            Assert.Contains(panel.Pictures, p => p.Art.Name == "PM_B_Small.png" && p.Frame == 1);
            Assert.True(Assert.Single(Marks(shell)).Border);

            // Held under the pointer it takes the depressed frame and the black that reads on it.
            shell.Step(Pointer(ok.X + 2f, ok.Y + 2f, pressed: true, clicked: true));
            panel = Box(shell);
            Assert.Equal(BoardInk.DialogPressed, Assert.Single(panel.Lines, l => l.Text == ok.Label).Ink);
            Assert.Contains(panel.Pictures, p => p.Art.Name == "PM_B_Small.png" && p.Frame == 3);
        });
    }

    /// <summary>The cabin's own crossing into the hangar module. The door opens it over the seated
    /// profile's purse. The way back out of the hangar comes through the host onto the cabin, with
    /// its profile re-read.</summary>
    [Fact]
    public void PlaneConstructionOpensTheHangarOverTheWalletWithTheCabinAsItsReturn()
    {
        WithCampaignShell((shell, campaign, hangar, _, _) =>
        {
            Seat(shell, "Zachary");
            var door = Row(shell, "PlaneConstruction");

            Click(shell, door.X + 2f, door.Y + 2f);
            Assert.Equal(OriginalScreen.PlaneName, shell.Screen);
            Assert.True(hangar.IsOpen);
            Assert.NotNull(hangar.Wallet);
            Assert.Equal(campaign.Profile!.Funds, hangar.Wallet!.Funds);

            shell.Step(Back);
            Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
            Assert.False(hangar.IsOpen);
            Assert.True(campaign.IsOpen);
            Assert.Equal("PlaneConstruction", shell.FocusedKey);
        });
    }

    /// <summary>The check standing on a guest belongs to that guest's device and to the mouse
    /// riding seat 0's source, nothing else of seat 0's. Its cursor, Accept and Back would
    /// otherwise change a pilot's ammunition, aircraft and readiness from another chair. Seat 0
    /// keeps the whole frame on its own check, which is what the field's index answers. The
    /// per-seat walk is the shell's, so the fact stands here rather than over the module
    /// alone.</summary>
    [Fact]
    public void SeatZeroDrivesItsOwnCheckAndOnlyItsPointerReachesAGuests()
    {
        WithCampaignShell((shell, campaign, _, setup, _) =>
        {
            Seat(shell, "Zachary");
            setup.Join(new ScriptedMenuSeat());
            shell.Step(Accept);
            var go = Row(shell, "GoToFlightCheck");
            Click(shell, go.X + 2f, go.Y + 2f);
            Assert.Equal(OriginalScreen.CampaignFlightCheck, shell.Screen);

            // Seat 0's own check takes its whole frame, into ammo selection and back out.
            Assert.Equal("ChangeAmmo", shell.FocusedKey);
            shell.Step(Accept);
            Assert.Equal(OriginalScreen.CampaignAmmo, shell.Screen);
            shell.Step(Back);
            Assert.Equal(OriginalScreen.CampaignFlightCheck, shell.Screen);

            // FLY MISSION hands the screen to the guest. Seat 0's cursor, Accept and Back then
            // move nothing: no row, no ammo screen, no retreat off the guest's check.
            var fly = Row(shell, "FlyMission");
            Assert.Null(Click(shell, fly.X + 2f, fly.Y + 2f).Exit);
            Assert.Equal((1, 2), (campaign.Field.Current, campaign.Field.Players));
            Assert.Equal("ChangeAmmo", shell.FocusedKey);
            Assert.False(shell.Step(Down).Changed);
            Assert.False(shell.Step(Accept).Changed);
            Assert.False(shell.Step(Back).Changed);
            Assert.Equal(OriginalScreen.CampaignFlightCheck, shell.Screen);
            Assert.Equal(1, campaign.Field.Current);
            Assert.Equal("ChangeAmmo", shell.FocusedKey);

            // The guest's own device drives it, and the ammo screen its row opens is the guest's too.
            Assert.True(shell.StepSeat(1, Accept).Changed);
            Assert.Equal(OriginalScreen.CampaignAmmo, shell.Screen);
            Assert.False(shell.Step(Back).Changed);
            Assert.Equal(OriginalScreen.CampaignAmmo, shell.Screen);
            shell.StepSeat(1, Back);
            Assert.Equal(OriginalScreen.CampaignFlightCheck, shell.Screen);
            Assert.Equal(1, campaign.Field.Current);

            // Seat 0's pointer still reaches the guest's check, the one device a pilot with no pad
            // of their own has. The last check's FLY MISSION is the launch for both seats.
            fly = Row(shell, "FlyMission");
            var exit = Assert.IsType<CampaignMissionExit>(Click(shell, fly.X + 2f, fly.Y + 2f).Exit);
            Assert.Equal(2, exit.Seats.Count);
        });
    }

    [Fact]
    public void AShellWithoutAHangarFeatureHasNoModuleAndNoHangarDoor()
    {
        var shell = Shell(out _);
        Assert.Null(shell.Hangar);
        shell.OpenHangar();
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.False(shell.IsHangarScreen);
        Assert.False(shell.CapturingText);
    }

    // The characters the script stores, shifted down by three the way its own loop shifts them.
    private static string Shifted(string cipher) => string.Concat(cipher.Select(c => (char)(c - 3)));

    // The credits screen with a string table behind it, since the About box's words are langui
    // 1301 filled with the product id and its OK is langui 100.
    private static OriginalShell CreditsShell()
    {
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        var strings = UiStrings.Parse(
            """
            [
              { "dll": "langui", "id": 100, "text": "OK" },
              { "dll": "langui", "id": 1301, "text": "[COUR9](c) 2000. Your product identification number is:\n\n<B>%1!s!<b>" }
            ]
            """);
        var shell = new OriginalShell(
            MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), setup, Measure,
            hangar: new HangarFeature(strings, PlanePickerRoster.AirframeNode));
        shell.Open(OriginalScreen.Credits);
        return shell;
    }

    // A shell with the hangar behind its Build door, over a scratch store in a temp directory
    // that goes with the test. It takes the hangar's own art measure, since the door's screens are
    // the module's. The Devastator is the Instant Action pick a default build inherits.
    private static void WithHangarShell(System.Action<OriginalShell, HangarFeature, PlayerSetupFeature, CSVM.Flight.Hangar.CustomPlaneStore> test)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csvm-original-shell-hangar-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CSVM.Flight.Hangar.CustomPlaneStore(dir);
            var setup = new PlayerSetupFeature();
            setup.SetRoster(OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
            setup.Join(new ScriptedMenuSeat());
            var hangar = new HangarFeature(UiStrings.Empty, PlanePickerRoster.AirframeNode);
            var instantAction = new InstantActionFeature(_ => InstantAction.Defaults());
            instantAction.SelectPlayerPlane(HangarFeature.DefaultAirframe);
            var shell = new OriginalShell(
                MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), setup, OriginalHangarTests.Measure,
                instantAction: instantAction, hangar: hangar, planes: store);
            test(shell, hangar, setup, store);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
    }

    // A shell with the campaign behind its own door. The profile store and build store are
    // scratch, in a temp directory that goes with the test. A hangar stands behind the cabin's
    // PLANE CONSTRUCTION.
    private static void WithCampaignShell(
        System.Action<OriginalShell, CampaignFeature, HangarFeature, PlayerSetupFeature, CampaignProfileStore> test)
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "csvm-original-shell-campaign-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CampaignProfileStore(System.IO.Path.Combine(dir, "Profiles"));
            var planes = new CSVM.Flight.Hangar.CustomPlaneStore(System.IO.Path.Combine(dir, "Planes"));
            var setup = new PlayerSetupFeature();
            setup.SetRoster(OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
            setup.Join(new ScriptedMenuSeat());
            var hangar = new HangarFeature(UiStrings.Empty, PlanePickerRoster.AirframeNode);
            var campaign = new CampaignFeature(UiStrings.Empty, airframe => $"node{airframe}");
            var shell = new OriginalShell(
                MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), setup, Measure,
                hangar: hangar, planes: planes, campaign: campaign, profiles: () => store);
            test(shell, campaign, hangar, setup, store);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
    }

    // A player typed into the campaign's name box and started, landing on the cabin.
    private static void Seat(OriginalShell shell, string name)
    {
        Click(shell, OriginalShell.CampaignKey);
        shell.Step(new MenuCommands { Typed = name });
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    // Seat 0 is a scripted source; the roster is the eleven stock airframes with no customs. No
    // options reader by default, so the Options screen opens on the defaults and no test can reach
    // the player's own user://options.json.
    private static OriginalShell Shell(out FreeFlightFeature free, System.Func<OptionsDef>? options = null)
    {
        free = new FreeFlightFeature();
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(System.Array.Empty<CSVM.Flight.Hangar.CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        return new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), free, setup, Measure, options: options);
    }

    // The fixture's strips: every button strip 240x200 (four 50-pixel frames), the paper plaque
    // 160x112 (four 28-pixel frames), the checkbox 16x128 (eight 16-pixel frames), the dropdown
    // arrows 15x56, the slider's slot and thumb and the scroll art at their shipped sizes, the
    // panes unmeasured.
    private static (int Width, int Height)? Measure(string art) => art switch
    {
        // The fixture's movie at the shipped files' own picture size, which the layout row scales.
        "PM_Flag.MPG" => (320, 240),
        "PM_B_Paper.png" => (160, 112),
        // The About box's own background, which the box is centred by.
        "PM_AboutBox.png" => (400, 300),
        "PP_B_Check8.png" => (16, 128),
        "PP_B_DropUp.png" or "PP_B_DropDown.png" => (15, 56),
        // The option pages' scroll art at the shipped sizes: four-frame arrows and a one-frame bar.
        "PP_B_ScrollUp.png" or "PP_B_ScrollDown.png" => (16, 44),
        "PP_B_ScrollBar.png" => (16, 11),
        // The section's own slider art and the shipped names a row with no slider widget falls
        // back to, both at the shipped sizes: a three-pixel slot and a thumb that clears it.
        "PP_B_SliderSlot.png" or "PF_B_SliderSlot.png" => (171, 3),
        "PP_B_Slider.png" or "PF_B_Slider.png" => (43, 21),
        _ when art.StartsWith("PM_B_", System.StringComparison.Ordinal) => (240, 200),
        _ when art.StartsWith("PP_B_", System.StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    private static MenuCommands Pointer(float x, float y, bool pressed = false, bool clicked = false, bool right = false) =>
        new() { Pointer = new MenuPointer(x, y, pressed, clicked, 0, right) };

    // One row of the screen by its key.
    private static OriginalRow Row(OriginalShell shell, string key) =>
        shell.Rows.Single(r => r.Key == key);

    // One click as the shell reads it: the press arms the row and the release on it fires, so the
    // step that carries the activation is the second one.
    private static OriginalStep Click(OriginalShell shell, float x, float y)
    {
        shell.Step(Pointer(x, y, pressed: true, clicked: true));
        return shell.Step(Pointer(x, y));
    }

    // The same click, landing just inside the corner of the row carrying a key.
    private static OriginalStep Click(OriginalShell shell, string key)
    {
        var row = Row(shell, key);
        return Click(shell, row.X + 2f, row.Y + 2f);
    }

    // The standing dialog's panel: the one overlay carrying words, the pointer's own carrying none.
    private static BoardPanel Box(OriginalShell shell) =>
        shell.Compose().Overlays.First(o => o.Lines.Count > 0);

    // The focus marks standing over the box, which ride a panel of nothing but fills.
    private static System.Collections.Generic.List<BoardFill> Marks(OriginalShell shell) =>
        shell.Compose().Overlays
            .Where(o => o.Pictures.Count == 0 && o.Lines.Count == 0)
            .SelectMany(o => o.Fills)
            .ToList();

    // The strip frame the box's single answer draws, its picture standing after the background
    // and the icon.
    private static int AnswerFrame(OriginalShell shell) => Box(shell).Pictures[2].Frame;

    // The bitmap the composed pointer overlay draws, which is the last overlay's one picture.
    private static string PointerArt(OriginalShell shell) =>
        shell.Compose().Overlays[^1].Pictures[0].Art.Name;

    // The key of the topmost row a point lands on, the shell's own last-hit-wins reading.
    private static string HitTestKey(OriginalShell shell, float x, float y)
    {
        var rows = shell.Rows;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Visible && rows[i].Contains(x, y))
            {
                return rows[i].Key;
            }
        }

        return string.Empty;
    }
}
