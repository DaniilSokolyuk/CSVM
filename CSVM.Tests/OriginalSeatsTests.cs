using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Original shell's sortie screens over the shared player setup, on the hand-authored layout
/// fixture: the Dogfight door and its gate, seat 0's pick opening the per-seat aircraft screen
/// for each joined seat in turn (its plane-selection shape, the list open while browsing, the
/// picking seat's device and the mouse driving it and seat 0's stick nothing), its WEAPON LOADOUT
/// row over that seat's own fit and what the launch then carries per seat, the last seat's
/// confirm as the launch on both sortie screens and on Instant Action, the sortie screen returning
/// with FLY to press when FLY's own gate is unmet, the hint at each stage, and the aircraft window
/// over a roster with saved customs.
/// </summary>
public class OriginalSeatsTests
{
    // The per-seat screen's WEAPON LOADOUT row, keyed by the board button it draws as.
    private const string LoadoutRow = nameof(BoardButton.ChangeAmmo);

    private static readonly MenuCommands None = new();
    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Right = new() { MoveX = 1 };

    [Fact]
    public void TheDogfightDoorOpensTheDogfightScreenWhoseFlyWaitsForASecondSeat()
    {
        var shell = Shell(out var setup, out _);

        shell.Step(Down);
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.Dogfight, shell.Screen);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "DOGFIGHT");
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick a map, then an aircraft");
        Assert.False(Fly(shell).Enabled);

        shell.Step(Accept);
        Assert.Equal("C1", shell.PickedDogfightChapter);
        Assert.Null(shell.PickedChapter);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick an aircraft");
        shell.Step(Right);
        shell.Step(Accept);
        Assert.Equal("player_autogyro", shell.PickedAirframe);
        Assert.True(setup.Seats[0].Locked);
        Assert.False(Fly(shell).Enabled);
        Assert.Contains(shell.Compose().Lines, l => l.Text.StartsWith("Dogfight needs a second seat", StringComparison.Ordinal));

        // A disabled FLY takes no keyboard focus (Up wraps past it) and no click.
        shell.Step(Up);
        Assert.Equal(OriginalShell.AirframeKey(10), shell.FocusedKey);
        var fly = Fly(shell);
        var step = Click(shell, fly.X + 4f, fly.Y + 4f);
        Assert.Null(step.Exit);
        Assert.False(setup.Seats[0].Confirmed);
    }

    [Fact]
    public void ASecondSeatPicksOnItsOwnScreenAndItsConfirmLaunchesTheDogfightForBoth()
    {
        PlayerSetupFeature setup = null!;
        var shell = Shell(out setup, out _, seat => ReferenceEquals(seat, setup.Seats[1]) ? new[] { 3 } : Array.Empty<int>());
        shell.Step(Down);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var pad = new ScriptedMenuSeat();
        var second = setup.Join(pad)!;

        // The join lands between frames; seat 0's next frame opens the second seat's screen.
        Assert.True(shell.Step(None).Changed);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);
        Assert.Equal(OriginalScreen.Dogfight, shell.SeatReturn);
        var board = shell.Compose();
        Assert.Contains(board.Backdrop, p => p.Art.Name == "PM_Plane.jpg");
        Assert.Contains(board.Lines, l => l.Text == "PLANE SELECTION");
        Assert.Contains(board.Lines, l => l.Text == "P2  scripted   choose your aircraft");
        Assert.Contains(board.Overlays.SelectMany(o => o.Lines), l => l.Text == "Hellhound");
        Assert.Contains(board.Overlays.SelectMany(o => o.Lines), l => l.Text == "P2" && l.Ink == SeatStrip.Ink(1));
        Assert.Equal(new[] { OriginalShell.SeatPlaneFieldKey, "AcceptSelections", "CancelSelections" },
            shell.Rows.Where(r => !r.Key.StartsWith("ENTRY:", StringComparison.Ordinal)).Select(r => r.Key));

        var walk = shell.StepSeat(1, Down);
        Assert.True(walk.Changed);
        Assert.Equal(0, second.Cursor);
        Assert.Contains(shell.Compose().Pictures, p => p.Art.Name == "FC_PlaneIcons.Png" && p.Frame == 1);

        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        Assert.Equal(1, second.Cursor);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(4, shell.Rows.Count);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "P2  scripted   A again to confirm, B to change");
        Assert.Contains(shell.Compose().Lines, l => l.Text.StartsWith("AGILITY:", StringComparison.Ordinal));

        // The last seat's confirm is the launch: the Dogfight screen returns behind it with both
        // seats confirmed, so nobody has to reach FLY.
        var step = shell.StepSeat(1, Accept);
        Assert.True(second.Confirmed);
        Assert.Equal(OriginalScreen.Dogfight, shell.Screen);
        Assert.Equal(-1, shell.PickingSeat);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "P2  scripted   Hellhound  READY");
        var launch = Assert.IsType<LaunchExit>(step.Exit);
        Assert.Equal(MenuMode.Versus, launch.Mode);
        Assert.Equal("C1", launch.Chapter);
        Assert.Equal(new[] { "player_autogyro", "player_avenger" }, launch.Seats.Select(s => s.PlaneNode));
        Assert.Empty(launch.Seats[0].Pads);
        Assert.Equal(new[] { 3 }, launch.Seats[1].Pads);
        Assert.True(setup.Seats[0].Confirmed);
    }

    [Fact]
    public void TheLastSeatsConfirmLaunchesTwoPilotFreeFlightThroughTheFeature()
    {
        var shell = Shell(out var setup, out var free);
        shell.Step(Accept);
        shell.Step(Down);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        shell.StepSeat(1, Down);
        shell.StepSeat(1, Down);
        shell.StepSeat(1, Accept);

        var launch = Assert.IsType<LaunchExit>(shell.StepSeat(1, Accept).Exit);

        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Equal(MenuMode.Free, launch.Mode);
        Assert.Equal("C1B", launch.Chapter);
        Assert.Equal("C1B", free.Chapter?.Code);
        Assert.Equal(new[] { "player_avenger", "player_balmoral" }, launch.Seats.Select(s => s.PlaneNode));
        Assert.True(setup.Seats[0].Confirmed);
        Assert.Equal(2, second.Cursor);
    }

    [Fact]
    public void AWalkEndingWithNoMapPickedReturnsTheSortieScreenWithFlyToPress()
    {
        var shell = Shell(out var setup, out _);
        shell.Open(OriginalScreen.FreeFlight);
        var first = shell.Rows.Single(r => r.Key == OriginalShell.AirframeKey(0));
        Click(shell, first.X + 4f, first.Y + 4f);
        Assert.Null(shell.PickedChapter);
        var second = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);

        // Every seat confirmed but FLY's own gate unmet: the screen returns, nothing launches.
        shell.StepSeat(1, Accept);
        Assert.Null(shell.StepSeat(1, Accept).Exit);
        Assert.True(second.Confirmed);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.False(Fly(shell).Enabled);
        Assert.False(setup.Seats[0].Confirmed);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick a map, then FLY");

        // The map picked, FLY stands and seat 0's own press is the launch.
        var map = shell.Rows.Single(r => r.Key == "C1");
        Click(shell, map.X + 4f, map.Y + 4f);
        var fly = Fly(shell);
        Assert.True(fly.Enabled);
        var launch = Assert.IsType<LaunchExit>(
            Click(shell, fly.X + 4f, fly.Y + 4f).Exit);
        Assert.Equal("C1", launch.Chapter);
        Assert.Equal(2, launch.Seats.Count);
    }

    [Fact]
    public void TheSortieHintNamesWhatIsOutstandingRatherThanTheMapAlone()
    {
        var shell = Shell(out _, out _);
        shell.Open(OriginalScreen.FreeFlight);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick a map, then an aircraft");

        // The aircraft picked and no map: the press is what is left, so the hint names FLY rather
        // than the aircraft already chosen.
        var plane = shell.Rows.Single(r => r.Key == OriginalShell.AirframeKey(0));
        Click(shell, plane.X + 4f, plane.Y + 4f);
        Assert.Null(shell.PickedChapter);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick a map, then FLY");

        // The map picked, the hint stops naming it.
        var map = shell.Rows.Single(r => r.Key == "C1");
        Click(shell, map.X + 4f, map.Y + 4f);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "FLY when ready");

        // A Dogfight short of its second seat names that seat even with no map picked, the press
        // being further off than one press.
        var dogfight = Shell(out _, out _);
        dogfight.Open(OriginalScreen.Dogfight);
        var versusPlane = dogfight.Rows.Single(r => r.Key == OriginalShell.AirframeKey(0));
        Click(dogfight, versusPlane.X + 4f, versusPlane.Y + 4f);
        Assert.Null(dogfight.PickedDogfightChapter);
        Assert.Contains(dogfight.Compose().Lines, l => l.Text.StartsWith("Dogfight needs a second seat", StringComparison.Ordinal));
    }

    [Fact]
    public void EachJoinedSeatPicksInPlayerOrderOnItsOwnDeviceWhileSeatZerosMovesNothing()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        var third = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        Assert.Equal(1, shell.PickingSeat);

        // Seat 0's stick and buttons move nothing on a screen that is not its own.
        Assert.False(shell.Step(Down).Changed);
        Assert.False(shell.Step(Accept).Changed);
        Assert.False(shell.Step(Back).Changed);
        Assert.False(second.Locked);
        Assert.Equal(0, second.Cursor);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);

        // The picking seat's own device drives it: select, then confirm.
        shell.StepSeat(1, Down);
        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        shell.StepSeat(1, Accept);
        Assert.True(second.Confirmed);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(2, shell.PickingSeat);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "P3  scripted   choose your aircraft");

        // Seat 0's pointer still drives the screen, one mouse at the desk serving whoever picks: a
        // click on a list entry selects it for the picking seat, ACCEPT SELECTIONS confirms.
        var entry = shell.Rows.Single(r => r.Key == "ENTRY:2");
        Click(shell, entry.X + 4f, entry.Y + 4f);
        Assert.True(third.Locked);
        Assert.Equal(2, third.Cursor);
        var accept = shell.Rows.Single(r => r.Key == "AcceptSelections");
        var last = Click(shell, accept.X + 4f, accept.Y + 4f);
        Assert.True(third.Confirmed);

        // The last seat confirmed, so that press is the launch for all three, whichever device it
        // came from, and the Free Flight screen returns behind it.
        Assert.Equal(3, Assert.IsType<LaunchExit>(last.Exit).Seats.Count);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.True(Fly(shell).Enabled);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "FLY when ready");
    }

    [Fact]
    public void SeatZerosBackIsNotThePerSeatScreensExitAndTheMouseLeavesThroughCancelSelections()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);

        // Back here belongs to the picking seat, so seat 0's leaves nothing and keeps its pick.
        Assert.False(shell.Step(Back).Changed);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.NotNull(shell.PickedAirframe);

        // CANCEL SELECTIONS over the open list is the pointer's own way out of the walk.
        var cancel = shell.Rows.Single(r => r.Key == "CancelSelections");
        Assert.Null(Click(shell, cancel.X + 4f, cancel.Y + 4f).Exit);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Null(shell.PickedAirframe);
        Assert.True(second.Joined);
        Assert.Equal(2, setup.Seats.Count);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Pick an aircraft");

        // Picking again walks the seat again; a frame with nothing pressed changes nothing.
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.False(shell.Step(None).Changed);
    }

    [Fact]
    public void BackOnSeatZerosPickUndoesItBeforeLeavingTheScreen()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        Assert.NotNull(shell.PickedAirframe);

        Assert.Null(shell.Step(Back).Exit);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Null(shell.PickedAirframe);
        Assert.Equal("C1", shell.PickedChapter);

        Assert.Null(shell.Step(Back).Exit);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.Single(setup.Seats);
    }

    [Fact]
    public void AClickOnAnotherAircraftRePicksAndTheSameRowAgainChangesNothing()
    {
        var shell = Shell(out var setup, out _);
        shell.Open(OriginalScreen.FreeFlight);
        var first = shell.Rows.Single(r => r.Key == OriginalShell.AirframeKey(0));
        var third = shell.Rows.Single(r => r.Key == OriginalShell.AirframeKey(2));

        Click(shell, first.X + 4f, first.Y + 4f);
        Assert.Equal("player_autogyro", shell.PickedAirframe);
        Click(shell, first.X + 4f, first.Y + 4f);
        Assert.Equal("player_autogyro", shell.PickedAirframe);
        Assert.True(setup.Seats[0].Locked);

        Click(shell, third.X + 4f, third.Y + 4f);
        Assert.Equal("player_balmoral", shell.PickedAirframe);
        Assert.True(setup.Seats[0].Locked);
        Assert.Contains(shell.Compose().Fills, f => f.X == third.X && f.Y == third.Y);
    }

    [Fact]
    public void ALaterSeatsBackUnselectsThenLeavesTheWalkAndUnjoinsFromAnyOtherScreenAtOnce()
    {
        var shell = Shell(out var setup, out _);
        var pad = new ScriptedMenuSeat();
        setup.Join(pad);

        Assert.True(shell.StepSeat(1, Back).Changed);
        Assert.Single(setup.Seats);
        Assert.False(shell.StepSeat(1, Back).Changed);

        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(pad)!;
        shell.Step(None);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        Assert.Equal(4, shell.Rows.Count);
        shell.StepSeat(1, Back);
        Assert.False(second.Locked);
        Assert.True(second.Joined);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.True(shell.Rows.Count > 4);

        // Over the open list Back leaves the walk with both seats kept, seat 0's pick going with
        // it so the sortie screen does not put the walk straight back up.
        shell.StepSeat(1, Back);
        Assert.True(second.Joined);
        Assert.Equal(2, setup.Seats.Count);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.Null(shell.PickedAirframe);
        Assert.False(Fly(shell).Enabled);

        // Seat 0 picking again reopens the walk at the same seat, so the return is not a dead end.
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);

        // CANCEL SELECTIONS drops the selection and reopens the list, leaving neither the walk nor
        // the sortie.
        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        var cancel = shell.Rows.Single(r => r.Key == "CancelSelections");
        Click(shell, cancel.X + 4f, cancel.Y + 4f);
        Assert.False(second.Locked);
        Assert.True(second.Joined);
        Assert.Equal(2, setup.Seats.Count);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.True(shell.Rows.Count > 4);

        // With no selection left to drop, the same press leaves the walk, both seats kept: the one
        // exit a mouse has, Back belonging to the picking seat's device.
        var again = shell.Rows.Single(r => r.Key == "CancelSelections");
        Assert.Null(Click(shell, again.X + 4f, again.Y + 4f).Exit);
        Assert.Equal(OriginalScreen.FreeFlight, shell.Screen);
        Assert.True(second.Joined);
        Assert.Equal(2, setup.Seats.Count);
        Assert.Null(shell.PickedAirframe);
    }

    [Fact]
    public void FlyMissionWithASecondSeatWalksItThroughThePerSeatScreenThenLaunchesBoth()
    {
        PlayerSetupFeature setup = null!;
        var shell = Shell(out setup, out _, seat => ReferenceEquals(seat, setup.Seats[1]) ? new[] { 2 } : Array.Empty<int>());
        shell.InstantAction.OpenInstantAction();
        Assert.DoesNotContain(shell.Compose().Overlays.SelectMany(o => o.Lines), l => l.Text.StartsWith("P1", StringComparison.Ordinal));
        var second = setup.Join(new ScriptedMenuSeat())!;
        Assert.Contains(shell.Compose().Overlays.SelectMany(o => o.Lines), l => l.Text == "P2" && l.Ink == SeatStrip.Ink(1));

        var fly = shell.Rows.Single(r => r.Key == OriginalInstantActionScreen.FlyMissionKey);
        Assert.Null(Click(shell, fly.X + 4f, fly.Y + 4f).Exit);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);
        Assert.Equal(OriginalScreen.InstantAction, shell.SeatReturn);
        Assert.True(setup.Seats[0].Locked);
        Assert.Same(shell.InstantAction.PilotRoster, setup.Roster);

        shell.StepSeat(1, Down);
        shell.StepSeat(1, Down);
        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        var launch = Assert.IsType<LaunchExit>(shell.StepSeat(1, Accept).Exit);
        Assert.Equal(OriginalScreen.InstantAction, shell.Screen);
        Assert.NotNull(launch.InstantAction);
        Assert.Equal(2, launch.Seats.Count);
        Assert.Equal(setup.Roster[shell.InstantAction.PilotRow].Node, launch.Seats[0].PlaneNode);
        Assert.Equal("player_balmoral", launch.Seats[1].PlaneNode);
        Assert.Equal(new[] { 2 }, launch.Seats[1].Pads);
        Assert.True(setup.Seats[0].Confirmed && second.Confirmed);
    }

    [Fact]
    public void EachPickingSeatsWeaponLoadoutRowEditsItsOwnFitAndTheLaunchCarriesEveryOne()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        var third = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        Assert.Equal(1, shell.PickingSeat);

        // While the seat browses there is no loadout row: a fit needs an aeroplane to hang on.
        Assert.DoesNotContain(shell.Rows, r => r.Key == LoadoutRow);
        shell.StepSeat(1, Accept);
        Assert.True(second.Locked);
        Assert.Equal(4, shell.Rows.Count);
        Assert.Equal("WEAPON LOADOUT", shell.Rows.Single(r => r.Key == LoadoutRow).Label);

        // The row opens the loadout screen on the picking seat's own fit and its own aeroplane.
        shell.StepSeat(1, Down);
        Assert.Equal(LoadoutRow, shell.FocusedKey);
        shell.StepSeat(1, Accept);
        Assert.Equal(OriginalScreen.InstantActionLoadout, shell.Screen);
        Assert.Equal(1, shell.InstantAction.LoadoutSeat);
        Assert.Same(second.Fit, shell.InstantAction.LoadoutFit);
        Assert.Equal(setup.Roster[second.Cursor].Node, shell.InstantAction.LoadoutNode);

        // The walk stands on this screen too: seat 0's frame still moves nothing, and the picking
        // seat's Back is CANCEL LOADOUT rather than an unjoin, so it lands back on its own row.
        Assert.False(shell.Step(Back).Changed);
        second.Fit.SetGunAmmo(1, "wep_ap");
        shell.StepSeat(1, Back);
        Assert.Equal(3, setup.Seats.Count);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);
        Assert.Equal(LoadoutRow, shell.FocusedKey);
        Assert.True(second.Fit.IsStock);

        // ACCEPT LOADOUT keeps the picks, and on that seat's fit alone.
        shell.StepSeat(1, Accept);
        second.Fit.SetGunAmmo(1, "wep_ap");
        var keep = shell.Rows.Single(r => r.Key == OriginalInstantActionScreen.LoadoutAcceptKey);
        Click(shell, keep.X + 4f, keep.Y + 4f);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(LoadoutRow, shell.FocusedKey);
        Assert.Equal("wep_ap", second.Fit.GunAmmoFor(1));
        Assert.True(setup.Seats[0].Fit.IsStock);
        Assert.True(third.Fit.IsStock);

        // Every seat's own fit reaches the launch, seat 0's carrying nothing because it picked none.
        var confirm = shell.Rows.Single(r => r.Key == "AcceptSelections");
        Click(shell, confirm.X + 4f, confirm.Y + 4f);
        Assert.True(second.Confirmed);
        Assert.Equal(2, shell.PickingSeat);
        shell.StepSeat(2, Accept);
        third.Fit.SetPylon(1, "wep_rocket");
        var launch = Assert.IsType<LaunchExit>(shell.StepSeat(2, Accept).Exit);
        Assert.Null(launch.Seats[0].Fit);
        Assert.Same(second.Fit, launch.Seats[1].Fit);
        Assert.Same(third.Fit, launch.Seats[2].Fit);
    }

    [Fact]
    public void AWalkLosingItsPickingSeatOverTheLoadoutScreenMovesOnWithoutIt()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        setup.Join(new ScriptedMenuSeat());
        shell.Step(None);
        shell.StepSeat(1, Accept);
        shell.StepSeat(1, Down);
        shell.StepSeat(1, Accept);
        Assert.Equal(OriginalScreen.InstantActionLoadout, shell.Screen);

        setup.Unjoin(second);
        Assert.True(shell.Step(None).Changed);
        Assert.Equal(OriginalScreen.SeatPlane, shell.Screen);
        Assert.Equal(1, shell.PickingSeat);
        Assert.Equal(-1, shell.InstantAction.LoadoutSeat);
        Assert.Null(shell.InstantAction.LoadoutFit);
    }

    [Fact]
    public void TheAircraftColumnIsAWindowThatFollowsTheFocusAndHidesTheRest()
    {
        var customs = Enumerable.Range(1, 7).Select(i => new CustomPlaneDef { Name = $"Custom {i}", Airframe = i }).ToArray();
        var shell = Shell(out _, out _, customs: customs);
        shell.Step(Accept);
        var column = shell.Rows.Where(r => r.Column == 1 && r.Kind == OriginalRowKind.ListRow).ToList();
        Assert.Equal(18, column.Count);
        Assert.Equal(OriginalShell.AirframeWindow, column.Count(r => r.Visible));
        Assert.Equal(0, shell.AirframeTop);
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Text == "▲");
        Assert.Contains(shell.Compose().Lines, l => l.Text == "▼");
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Text == "Custom 7");

        shell.Step(Right);
        for (int i = 0; i < OriginalShell.AirframeWindow; i++)
        {
            shell.Step(Down);
        }

        Assert.Equal(OriginalShell.AirframeKey(11), shell.FocusedKey);
        Assert.Equal(1, shell.AirframeTop);
        var hidden = shell.Rows.Single(r => r.Key == OriginalShell.AirframeKey(0));
        Assert.False(hidden.Visible);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "▲");
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Text == "Autogyro");

        // The hidden row's rectangle is above the window; a pointer there hits nothing.
        shell.Step(Pointer(hidden.X + 4f, hidden.Y + 4f));
        Assert.Equal(-1, shell.Hover);

        for (int i = 0; i < 6; i++)
        {
            shell.Step(Down);
        }

        Assert.Equal(OriginalShell.AirframeKey(17), shell.FocusedKey);
        Assert.Equal(7, shell.AirframeTop);
        Assert.Contains(shell.Compose().Lines, l => l.Text == "Custom 7");
        Assert.DoesNotContain(shell.Compose().Lines, l => l.Text == "▼");

        // FLY is disabled with nothing picked, so Down wraps past it onto the first row and the
        // window follows.
        shell.Step(Down);
        Assert.Equal(OriginalShell.AirframeKey(0), shell.FocusedKey);
        Assert.Equal(0, shell.AirframeTop);
    }

    [Fact]
    public void AReturnToTheTopLevelResetsEverySeatsPickAndKeepsTheSeats()
    {
        var shell = Shell(out var setup, out _);
        shell.Step(Accept);
        shell.Step(Accept);
        shell.Step(Right);
        shell.Step(Accept);
        var second = setup.Join(new ScriptedMenuSeat())!;
        shell.Step(None);
        shell.StepSeat(1, Accept);
        shell.StepSeat(1, Accept);

        shell.ReturnToTopLevel();

        Assert.Equal(2, setup.Seats.Count);
        Assert.Null(shell.PickedAirframe);
        Assert.False(second.Locked || second.Confirmed);
        Assert.Equal("C1", shell.PickedChapter);
        Assert.Equal(-1, shell.PickingSeat);
    }

    private static OriginalRow Fly(OriginalShell shell) => shell.Rows.Single(r => r.Key == OriginalShell.FlyKey);

    // Seat 0 is a scripted source over the three-stock-plus-customs roster the fixture's shell picks from.
    private static OriginalShell Shell(
        out PlayerSetupFeature setup,
        out FreeFlightFeature free,
        Func<PlayerSeat, IReadOnlyList<int>>? flightDevices = null,
        IReadOnlyList<CustomPlaneDef>? customs = null)
    {
        free = new FreeFlightFeature();
        setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(customs ?? Array.Empty<CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        return new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), free, setup, Measure, flightDevices);
    }

    private static (int Width, int Height)? Measure(string art) => art switch
    {
        "PM_B_Paper.png" => (160, 112),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    private static MenuCommands Pointer(float x, float y, bool pressed = false, bool clicked = false) =>
        new() { Pointer = new MenuPointer(x, y, pressed, clicked) };

    // One click as the shell reads it: the press arms the row and the release on it fires, so the
    // step that carries the activation is the second one.
    private static OriginalStep Click(OriginalShell shell, float x, float y)
    {
        shell.Step(Pointer(x, y, pressed: true, clicked: true));
        return shell.Step(Pointer(x, y));
    }
}
