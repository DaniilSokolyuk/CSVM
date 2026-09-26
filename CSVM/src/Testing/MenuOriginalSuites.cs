using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.UI;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.Utils;

namespace CSVM.Testing;

/// <summary>
/// The Original presentation through the boundary, over the install's own decoded layout: a real
/// <see cref="MenuHost"/> with both presentations registered, Original selected by the CLI
/// override, shown at the top level, driven by pointer frames (window pixels, mapped through the
/// view's own <see cref="BoardFit"/>) and by keyboard frames through the host's first seat, the
/// launch leaving as one <see cref="LaunchExit"/>, the return re-entering the top level, then the
/// switches: to Built-in from mid-setup with the picked chapter discarded, back to Original fresh,
/// the Built-in Options route producing its switch exit, force-Built-in recovery, and the
/// availability fallback keeping the request.
/// </summary>
internal static class MenuOriginalSuites
{
    private const float Dt = 1f / 60f;
    // A connection index past anything the platform hands out. A seat placed on it is off the
    // roster whatever is plugged into the machine running the suite.
    private const int AbsentPad = 99;

    // The scrapbook's Current Mission tab, the one plaque a campaign board puts in the top band.
    // Its art starts at authored x 558 and is 114 wide, so a chip row in the corner clears this.
    private const float BookTabRight = 672f;

    // The [@GameOptions@] section's authored plate: its corner, the art's own height, the pitch
    // between row panels, and the line its two plaques stand on. Held here rather than read off
    // the layout so the check has something of its own to compare the composed page against.
    private const float AuthoredPlateY = 215f;
    private const float AuthoredPlateHeight = 289f;
    private const float AuthoredGameOptionPitch = 62f;
    private const float AuthoredPlaqueY = 457f;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Right = new() { MoveX = 1 };
    private static readonly MenuCommands Left = new() { MoveX = -1 };
    private static readonly MenuCommands Back = new() { Back = true };

    [Suite("menu-original-tracer",
        "Original Free Flight through the presentation boundary over the install's decoded layout: "
        + "selected by the CLI override, shown at the top level with the pointer hidden and the "
        + "Free Flight door focused, a pointer frame over Quit takes focus and cues the rollover, a "
        + "press on a plaque arms it and opens nothing while the pointer over it draws the active "
        + "bitmap, a release on another plaque activates neither, a "
        + "click on the door opens Free Flight, keyboard frames pick a chapter and an airframe and "
        + "FLY leaves as one LaunchExit, the return re-enters the top level, PREFERENCES and its "
        + "GAME OPTIONS door open the decoded page whose Difficulty dropdown stands first and whose "
        + "five rows take every choice, none of them the menu presentation, on a plate grown a band "
        + "to hold them where each row stands wholly inside one band and one band carries the pair "
        + "the canvas leaves no band for, and whose CANCEL CHANGES "
        + "drops them, a wheel step over the "
        + "aircraft column and over Instant Action's contents window moves each one row and clamps "
        + "at the head, a drag down each thumb's track lands the window on its last row without "
        + "activating what the click stood over, and the contents arrows still step it, seat 0 steering with a "
        + "pad claims it so it can never join as another seat, a pad a guest already joined on is "
        + "not claimable and a rebind of seat 0's set drops the stale last-active reading with it, "
        + "a seat whose pad drops off the roster holds it through the grace and leaves once the "
        + "device stays gone past it, joining is open on the Instant Action "
        + "screen and a second seat joined there stays seated, the campaign flight check carries the "
        + "seat strip with two seats and none with one, drawn as Built-in's own chip row in the "
        + "top-right corner clear of the book tab, a switch to Built-in "
        + "from mid-setup discards the pick and shows Built-in's Mode screen, a switch back starts "
        + "Original fresh, Built-in's Options route steps the difficulty, the opening view, the "
        + "automatic head turn, the targeting setting on and back off, the rumble toggle off, the "
        + "graphics mode, the rocket carve on from the off it opens at and "
        + "its four display rows over the machine's own screens and sizes, the two vocabularies and "
        + "a wrap onto the last frame cap, its four volume rows stepped by the AUDIO page's own "
        + "step and clamped at both ends, "
        + "opens and leaves the rebinding screen behind its Controls door and emits the apply exit "
        + "carrying all fifteen with no presentation row among them, "
        + "Original's VIDEO door opens the decoded page on its Display Mode dropdown "
        + "which fits its authored window and draws no bar, over the V-Sync one whose five words "
        + "window into four with the arrows and the thumb inside the box's right edge and the fifth "
        + "kept for the walk but unseen and unhit, that list wheeling and dragging like any other "
        + "and picking a frame cap, and the Rocket Craters and Enhanced Graphics checkboxes "
        + "that flip, whose CANCEL CHANGES drops them all with no exit and whose ACCEPT CHANGES "
        + "leaves as one more apply exit carrying them, Original's AUDIO door opens the decoded page "
        + "on its Master slider over four thumbs, a sideways step moves a level and clamps at "
        + "silence, the open page states its mix to the host every frame and names the level a "
        + "frame moved only where one moved, its CANCEL CHANGES drops the edit with no exit and "
        + "ends the preview, and its ACCEPT CHANGES leaves as "
        + "one more apply exit carrying the levels, the force flag recovers and a missing layout "
        + "falls back with the request kept")]
    internal static void MenuOriginalTracer(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        var exits = new List<MenuExit>();
        var audio = new RecordingAudio();
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        // ⚠ The debrief return below opens the campaign, so the presentation is pointed at a
        // scratch store: nothing here may read or write user://Profiles.
        string profiles = System.IO.Path.Combine(ctx.ScratchDir, "menu-original-tracer", "Profiles");
        var player1 = new MenuInput { Keyboard = true };
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, player1)
        {
            CampaignProfiles = new CSVM.Session.CampaignProfileStore(profiles),
        });
        var host = new MenuHost(registry, audio, exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot);
        host.AddSeat(seat);
        try
        {
            var shell = ColdStart(ctx, host);
            if (shell == null)
            {
                return;
            }

            Pointer(ctx, host, seat, shell, audio);
            Fly(ctx, host, seat, shell, exits);
            Return(ctx, host, shell, exits);
            Lists(ctx, host, seat, shell);
            Seats(ctx, host, seat, shell, player1);
            OriginalOptionsRoute(ctx, host, seat, shell, exits);
            SwitchToBuiltIn(ctx, host, seat, shell);
            BuiltInOptionsRoute(ctx, host, seat, exits);
            // The VIDEO route runs on the shell the switch back creates, and last of the Original
            // walks: its ACCEPT CHANGES hides the presentation for the launcher to act, so nothing
            // after it can drive the same shell.
            OriginalVideoRoute(ctx, host, seat, SwitchBackToOriginal(ctx, host), exits);
            // The AUDIO route needs a shell of its own for the same reason, the VIDEO route's
            // ACCEPT CHANGES having hidden the presentation the walk before it drove.
            OriginalAudioRoute(ctx, host, seat, SwitchBackToOriginal(ctx, host), exits, audio);
            Recovery(ctx, host, registry, audio);
        }
        finally
        {
            host.Deactivate();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }

        ctx.Check(host.Active == null && !host.Shown, $"Deactivate leaves the host holding no presentation");
    }

    private static OriginalShell? ColdStart(TestContext ctx, MenuHost host)
    {
        string? reason = host.Select(forceBuiltIn: false, cliOverride: "original");
        ctx.Check(host.Selected == PresentationId.Original && reason == null,
            $"--presentation=original selects Original over the install's layout ({host.Selected}, {reason ?? "no reason"})");
        host.Show(MenuReturnDestination.TopLevel);
        var original = host.Active as OriginalPresentation;
        ctx.Check(original != null && host.Shown, $"Show activates a fresh Original presentation ({host.Active?.Id})");
        var shell = original?.Shell;
        ctx.Check(shell is { Screen: OriginalScreen.TopLevel },
            $"a cold start opens on the top level ({shell?.Screen})");
        ctx.Check(shell?.FocusedKey == OriginalShell.FreeFlightKey,
            $"with the Free Flight door focused ({shell?.FocusedKey})");
        ctx.Check(shell?.Rows.Count == 8, $"the top level is the six decoded rows plus the two doors ({shell?.Rows.Count})");
        ctx.Check(Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Hidden,
            $"the OS pointer is hidden while Original draws its own ({Godot.Input.MouseMode})");
        return shell;
    }

    // Pointer frames in window pixels: the presentation maps them through the same BoardFit the
    // view draws with, so a point over the authored Quit plaque lands on Quit whatever the window.
    private static void Pointer(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, RecordingAudio audio)
    {
        var size = ctx.Host.GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        var quit = Row(shell, "MM_B_QUIT");
        var campaign = Row(shell, "MM_B_CAMPAIGN");
        var door = Row(shell, OriginalShell.FreeFlightKey);
        ctx.Check(quit != null && campaign != null && door != null, $"the top level carries Quit, Campaign and the door");
        if (quit == null || campaign == null || door == null)
        {
            return;
        }

        audio.Cues.Clear();
        Press(host, seat, Pointer(fit, quit.X + 5f, quit.Y + 5f));
        ctx.Check(shell.FocusedKey == "MM_B_QUIT", $"a pointer over Quit takes the focus ({shell.FocusedKey})");
        ctx.Check(audio.Cues.Count == 1 && audio.Cues[0] == OriginalCues.Rollover,
            $"and cues one rollover through the host's audio ({string.Join(",", audio.Cues)})");
        var multiplayer = Row(shell, "MM_B_MULTIPLAYER");
        ctx.Check(multiplayer is { Enabled: true }, $"the Multiplayer plaque is live over the network door");
        if (multiplayer != null)
        {
            Press(host, seat, Pointer(fit, multiplayer.X + 5f, multiplayer.Y + 5f));
            ctx.Check(shell.FocusedKey == OriginalShell.MultiplayerKey && audio.Cues.Count == 2,
                $"a pointer over the Multiplayer plaque takes the focus and cues a rollover ({shell.FocusedKey}, {audio.Cues.Count})");
        }

        Press(host, seat, Pointer(fit, campaign.X + 5f, campaign.Y + 5f));
        ctx.Check(shell.FocusedKey == OriginalShell.CampaignKey && audio.Cues.Count == 3,
            $"the Campaign plaque is live over the campaign feature: a pointer over it takes the focus and cues a rollover ({shell.FocusedKey}, {audio.Cues.Count})");
        var board = shell.Compose();
        ctx.Check(board.Overlays.Count == 1 && board.Overlays[0].Pictures.Count == 1,
            $"the pointer is composed as the last overlay ({board.Overlays.Count})");
        // The press arms the plaque and opens nothing: what fires is the release still on it.
        Press(host, seat, Pointer(fit, campaign.X + 5f, campaign.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && shell.ArmedKey == OriginalShell.CampaignKey,
            $"a press on Campaign arms it and opens nothing ({shell.Screen}, {shell.ArmedKey})");
        string bitmap = shell.Compose().Overlays[^1].Pictures[0].Art.Name;
        ctx.Check(bitmap == "activepointerz.png", $"the pointer over a live plaque is the active bitmap ({bitmap})");
        Press(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true));
        Press(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f));
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && shell.ArmedKey == string.Empty,
            $"and a release on another plaque activates neither of them ({shell.Screen}, {shell.ArmedKey})");

        Click(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.FreeFlight,
            $"a click on the door opens the Free Flight screen ({shell.Screen})");
        ctx.Check(audio.Cues.Contains(OriginalCues.Click), $"with a click cue ({string.Join(",", audio.Cues)})");
    }

    // Keyboard frames: down the chapter column, across to the airframe column, up onto FLY.
    private static void Fly(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, List<MenuExit> exits)
    {
        Press(host, seat, Down);
        Press(host, seat, Down);
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.PickedChapter == "C2", $"three rows down the chapter column and Accept picks Hollywood ({shell.PickedChapter})");
        ctx.Check(host.Features.Get<FreeFlightFeature>().Chapter?.Code == "C2",
            $"and the shared feature holds the pick ({host.Features.Get<FreeFlightFeature>().Chapter?.Code})");
        Press(host, seat, Right);
        Press(host, seat, Accept);
        ctx.Check(shell.PickedAirframe == "player_bhawk",
            $"Right lands on the same row of the airframe column, Accept picks it ({shell.PickedAirframe})");
        Press(host, seat, Up);
        Press(host, seat, Up);
        Press(host, seat, Up);
        Press(host, seat, Up);
        ctx.Check(shell.FocusedKey == OriginalShell.FlyKey, $"Up past the column's top wraps onto FLY ({shell.FocusedKey})");
        Press(host, seat, Accept);
        ctx.Check(exits.Count == 1 && exits[0] is LaunchExit,
            $"FLY leaves through the host as one LaunchExit ({exits.Count})");
        if (exits.Count == 1 && exits[0] is LaunchExit launch)
        {
            ctx.Check(launch.Chapter == "C2" && launch.Mode == MenuMode.Free && launch.InstantAction == null,
                $"carrying the picked chapter, mode Free and no Instant Action def ({launch.Chapter}, {launch.Mode})");
            ctx.Check(launch.Seats.Count == 1 && launch.Seats[0].PlaneNode == "player_bhawk",
                $"and the one seat's airframe ({launch.Seats.Count}, {launch.Seats[0].PlaneNode})");
        }

        ctx.Check(!host.Shown, $"the host hid the presentation on the exit (shown={host.Shown})");
        ctx.Check(Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Visible,
            $"and the OS pointer is back ({Godot.Input.MouseMode})");
        Press(host, seat, Accept);
        ctx.Check(exits.Count == 1, $"a frame while hidden reaches nothing ({exits.Count})");
        seat.Clear();
    }

    private static void Return(TestContext ctx, MenuHost host, OriginalShell shell, List<MenuExit> exits)
    {
        var before = host.Active;
        host.Show(MenuReturnDestination.TopLevel);
        ctx.Check(ReferenceEquals(before, host.Active) && host.Shown,
            $"a return shows the same Original instance again (shown={host.Shown})");
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && shell.PickedAirframe == null,
            $"on the top level with the airframe pick dropped ({shell.Screen}, {shell.PickedAirframe ?? "none"})");
        ctx.Check(exits.Count == 1, $"and nothing relaunched on the way back in ({exits.Count})");
        // The scratch store holds no such profile, so the return opens the campaign on its
        // profile screen rather than the book; menu-original-campaign drives the seated case.
        host.Show(new DebriefReturn("Nathan", 2, MissionWon: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignRoster && host.Features.Get<CampaignFeature>().Profile == null,
            $"a debrief return for a profile the store lacks lands on the profile screen with nobody seated ({shell.Screen})");
        host.Show(MenuReturnDestination.TopLevel);
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && !host.Features.Get<CampaignFeature>().IsOpen,
            $"and a top-level show closes that campaign again ({shell.Screen})");
    }

    // Seats and joining. A scripted run has no pad, so the claim is driven through the poller's
    // own record of the pad seat 0 last steered with, and the join through the feature, which is
    // where a pad's Start lands; the per-screen rule itself is read off the shell.
    // The pointer's wheel and thumb over the two lists this screen graph reaches: the sortie
    // screens' aircraft column and Instant Action's contents window, each wheeled a row, dragged
    // down its track and left back at its head, with the list's own arrows still stepping it.
    private static void Lists(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell)
    {
        var size = ctx.Host.GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        WalkTo(host, seat, shell, OriginalShell.FreeFlightKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.FreeFlight, $"the Free Flight door opens the aircraft column again ({shell.Screen})");
        WheelAndDrag(ctx, host, seat, shell, fit, "AIRFRAMES", "the sortie screens' aircraft column");
        ctx.Check(shell.PickedAirframe == null && shell.Screen == OriginalScreen.FreeFlight,
            $"and the wheel and the drag picked nothing and left no screen ({shell.PickedAirframe ?? "none"}, {shell.Screen})");
        Press(host, seat, Back);

        WalkTo(host, seat, shell, "MM_B_INSTANTACTION");
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.InstantAction, $"Instant Action opens its contents window ({shell.Screen})");
        WheelAndDrag(ctx, host, seat, shell, fit, OriginalInstantActionScreen.ContentsKey, "Instant Action's contents window");
        var down = Row(shell, OriginalInstantActionScreen.ContentsDownKey);
        ctx.Check(down != null, $"the contents window carries its authored down arrow");
        if (down != null)
        {
            int top = shell.InstantAction.ContentsTop;
            Click(host, seat, Pointer(fit, down.X + 2f, down.Y + 2f, pressed: true, clicked: true));
            ctx.Check(shell.InstantAction.ContentsTop == top + 1,
                $"and the arrow still steps the window one row, the wheel having changed nothing about it ({top} -> {shell.InstantAction.ContentsTop})");
            var up = Row(shell, OriginalInstantActionScreen.ContentsUpKey)!;
            Click(host, seat, Pointer(fit, up.X + 2f, up.Y + 2f, pressed: true, clicked: true));
            ctx.Check(shell.InstantAction.ContentsTop == top, $"and the up arrow steps it back ({shell.InstantAction.ContentsTop})");
        }

        Press(host, seat, Back);
        ctx.Check(shell.Screen == OriginalScreen.TopLevel, $"Back leaves Instant Action for the top level ({shell.Screen})");
    }

    // One list under the pointer: a wheel step down moves its window by exactly one row, a step
    // past the head clamps there, and a thumb taken at the top of its track and dragged to the
    // foot lands the window on its last row without activating whatever the click stood over.
    private static void WheelAndDrag(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell,
        BoardFit fit, string key, string what)
    {
        var list = List(shell, key);
        ctx.Check(list is { Window.Scrolls: true },
            $"{what} is a scrolling list the pointer can see ({list?.Window.Count ?? -1} rows in a window of {list?.Window.Rows ?? -1})");
        if (list is not { Window.Scrolls: true })
        {
            return;
        }

        var window = list.Window;
        float x = window.X + (window.Width / 2f);
        float y = window.Y + (window.Height / 2f);
        Press(host, seat, Pointer(fit, x, y, wheel: 1));
        ctx.Check(List(shell, key)?.Window.Top == window.Top + 1,
            $"a wheel step over it moves the window one row ({window.Top} -> {List(shell, key)?.Window.Top})");
        Press(host, seat, Pointer(fit, x, y, wheel: -3));
        ctx.Check(List(shell, key)?.Window.Top == 0, $"and three steps back up clamp at the head ({List(shell, key)?.Window.Top})");

        var head = List(shell, key)!.Window;
        Press(host, seat, Pointer(fit, head.ThumbX + 1f, head.ThumbY + 1f, pressed: true, clicked: true));
        ctx.Check(shell.Dragging == key, $"a click on its thumb takes hold of it ({shell.Dragging ?? "nothing"})");
        Press(host, seat, Pointer(fit, head.ThumbX + 1f, head.ThumbY + 1f + head.TrackHeight - head.ThumbHeight, pressed: true));
        ctx.Check(List(shell, key)?.Window.Top == head.LastTop,
            $"and dragging it the length of its track lands the window on its last row ({List(shell, key)?.Window.Top} of {head.LastTop})");
        Press(host, seat, Pointer(fit, head.ThumbX + 1f, head.ThumbY + 1f));
        ctx.Check(shell.Dragging == null, $"letting the button go ends the drag ({shell.Dragging ?? "nothing"})");
        Press(host, seat, Pointer(fit, x, y, wheel: -head.Count));
        ctx.Check(List(shell, key)?.Window.Top == 0, $"and the wheel brings it home ({List(shell, key)?.Window.Top})");
    }

    private static void Seats(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, MenuInput player1)
    {
        var original = host.Active as OriginalPresentation;
        var setup = host.Features.Get<PlayerSetupFeature>();
        ctx.Check(original?.Devices != null && !shell.JoiningOpen,
            $"the top level keeps joining closed ({shell.Screen}, open={shell.JoiningOpen})");
        if (original?.Devices is not { } devices)
        {
            return;
        }

        player1.LastActivePad = 2;
        host.Tick(Dt);
        ctx.Check(devices.P1Pad == 2 && devices.IsClaimed(2),
            $"seat 0 steering with pad 2 claims it, so the join scan skips that pad ({devices.P1Pad}, claimed={devices.IsClaimed(2)})");
        player1.LastActivePad = -1;
        host.Tick(Dt);
        GuestPadClaim(ctx, host, setup, devices, player1);
        PadBlipGrace(ctx, host, setup, devices, player1);

        WalkTo(host, seat, shell, "MM_B_INSTANTACTION");
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.InstantAction && shell.JoiningOpen,
            $"the Instant Action screen opens joining ({shell.Screen}, open={shell.JoiningOpen})");
        var s2 = new ScriptedSeat();
        ctx.Check(setup.Join(s2) != null && host.Seats.Count == 2, $"a second seat joins there ({host.Seats.Count})");
        Press(host, seat, Down);
        ctx.Check(host.Seats.Count == 2 && shell.Screen == OriginalScreen.InstantAction,
            $"and stays seated while seat 0 keeps steering the screen ({host.Seats.Count}, {shell.Screen})");

        host.Show(MenuReturnDestination.TopLevel);
        shell.Campaign.OpenCampaignOver(CampaignAidProfiles.Store(seeded: true), CampaignAidProfiles.Planes());
        ctx.Check(shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot), $"the scratch campaign seats its pilot");
        shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignFlightCheck);
        ctx.Check(shell.Screen == OriginalScreen.CampaignFlightCheck && shell.JoiningOpen,
            $"the flight check opens joining too ({shell.Screen}, open={shell.JoiningOpen})");
        ctx.Check(StripSeats(shell) == 2, $"its board carries the seat strip naming both seats ({StripSeats(shell)} lines)");
        ctx.Check(StripIsChipRow(shell, out string chips),
            $"drawn as Built-in's chip row, tags in their own seat inks in the top-right corner clear of the book tab ({chips})");
        Press(host, s2, Back);
        ctx.Check(host.Seats.Count == 1, $"the second seat's Back unjoins it ({host.Seats.Count})");
        ctx.Check(StripSeats(shell) == 0, $"and the strip goes with it, leaving the authored board alone ({StripSeats(shell)} lines)");
        host.Show(MenuReturnDestination.TopLevel);
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && !host.Features.Get<CampaignFeature>().IsOpen,
            $"a top-level show closes the scratch campaign again ({shell.Screen})");
    }

    // Seat 0 borrows every unclaimed pad, so a guest steering the menu before they press Start
    // leaves their own pad in its last-active reading. Both halves of what keeps a later claim off
    // that pad. The claim skips a pad another seat holds, and a rebind of seat 0's set drops the
    // reading it was taken from.
    private static void GuestPadClaim(
        TestContext ctx, MenuHost host, PlayerSetupFeature setup, MenuSeatDevices devices, MenuInput player1)
    {
        var guest = new MenuInput { Pads = new[] { 2 } };
        var seat = setup.Join(new BuiltInSeat(guest));
        player1.LastActivePad = 2;
        ctx.Check(seat != null && !devices.ClaimP1Pad() && devices.P1Pad < 0,
            $"a guest's joined pad is not claimable by seat 0, so neither seat flies the other's ({devices.P1Pad})");

        player1.Pads = new[] { 2 };
        devices.Sync(0f);
        ctx.Check(player1.LastActivePad < 0,
            $"and rebinding seat 0's pads drops the stale reading with them ({player1.LastActivePad})");
        ctx.Check(!devices.ClaimP1Pad() && devices.P1Pad < 0,
            $"leaving the claim nothing to take from a set seat 0 no longer reads ({devices.P1Pad})");
        if (seat != null)
        {
            setup.Unjoin(seat);
        }

        host.Tick(Dt);
    }

    // Steam Input re-enumerates its virtual pads mid-menu, and a seat that leaves on every blip is
    // the join flow coming apart. The roster is live here because --no-pads has none to come back
    // to. The pad index is one no real device can hold, so the grace is what decides.
    private static void PadBlipGrace(
        TestContext ctx, MenuHost host, PlayerSetupFeature setup, MenuSeatDevices devices, MenuInput player1)
    {
        bool disabled = Pads.Disabled;
        var bound = player1.Pads;
        Pads.Disabled = false;
        int before = setup.Seats.Count;
        var seat = setup.Join(new BuiltInSeat(new MenuInput { Pads = new[] { AbsentPad } }));
        devices.Sync(MenuSeatDevices.DeviceGrace / 2f);
        ctx.Check(seat != null && setup.Seats.Count == before + 1,
            $"a seat whose pad drops off the roster holds it through the grace ({setup.Seats.Count} seats)");
        devices.Sync(MenuSeatDevices.DeviceGrace);
        ctx.Check(setup.Seats.Count == before,
            $"and leaves once the device stays gone past it ({setup.Seats.Count} seats)");

        Pads.Disabled = disabled;
        player1.Pads = bound;
        if (seat != null && setup.Seats.Count > before)
        {
            setup.Unjoin(seat);
        }

        host.Tick(Dt);
    }

    // How many seat lines the composed board's overlays carry: the strip's lines are the only
    // overlay text that begins with a player tag.
    private static int StripSeats(OriginalShell shell)
    {
        int count = 0;
        foreach (var panel in shell.Compose().Overlays)
        {
            foreach (var line in panel.Lines)
            {
                count += line.Text.StartsWith("P", System.StringComparison.Ordinal) && line.Text.Length > 1 && char.IsDigit(line.Text[1]) ? 1 : 0;
            }
        }

        return count;
    }

    // Whether the strip is the chip row Built-in draws: the player tags alone, each in its own
    // seat's ink. The row sits on one line of the corner and begins right of the book tab.
    private static bool StripIsChipRow(OriginalShell shell, out string report)
    {
        var chips = new List<BoardLine>();
        foreach (var panel in shell.Compose().Overlays)
        {
            foreach (var line in panel.Lines)
            {
                if (line.Text.Length == 2 && line.Text[0] == 'P' && char.IsDigit(line.Text[1]))
                {
                    chips.Add(line);
                }
            }
        }

        bool ok = chips.Count > 0;
        float left = BoardFit.AuthoredWidth;
        for (int i = 0; i < chips.Count; i++)
        {
            ok &= chips[i].Text == SplitScreen.PlayerTag(i) && chips[i].Ink == SeatStrip.Ink(i)
                && chips[i].Y == SeatStrip.Inset;
            left = System.Math.Min(left, chips[i].X);
        }

        ok &= left > BookTabRight;
        report = $"{chips.Count} chips from x {(int)left}, first {(chips.Count > 0 ? chips[0].Ink.ToString() : "none")}";
        return ok;
    }

    // The host's switch between presentations: Deactivate, Select and Show. From mid-setup on the
    // Free Flight screen, Deactivate discards the feature's pick and Built-in opens on its Mode screen.
    private static void SwitchToBuiltIn(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell)
    {
        Press(host, seat, Accept);
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.FreeFlight && host.Features.Get<FreeFlightFeature>().Chapter != null,
            $"mid-setup: on the Free Flight screen with a chapter picked ({shell.Screen})");
        host.Deactivate();
        ctx.Check(host.Active == null && host.Features.Get<FreeFlightFeature>().Chapter == null,
            $"Deactivate frees Original and discards the unfinished pick");
        string? reason = host.Select(forceBuiltIn: false, cliOverride: "built-in");
        host.Show(MenuReturnDestination.TopLevel);
        var menu = (host.Active as BuiltInPresentation)?.Menu;
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason == null && menu is { Visible: true },
            $"a Built-in request re-selects it and Show stands the launchscreen up ({host.Selected})");
        ctx.Check(menu?.ShownScreen == "Mode" && menu.ShownRowText == "Free Flight",
            $"at its own top level, the Mode screen ({menu?.ShownScreen}, {menu?.ShownRowText})");
        ctx.Check(menu?.ShownRowCount == 7, $"whose last two rows are the Options and multiplayer doors ({menu?.ShownRowCount})");
    }

    // Built-in's Options route: the last Mode row opens Options, and Right steps the difficulty to
    // Hard. The two rows under it step the opening view and the automatic head turn, the two under
    // those the targeting setting and the rumble. The graphics mode row comes next; the command
    // line alone chooses a presentation, so no row does. The four display rows step over the
    // machine's own screens and sizes. The four volume rows step the levels the AUDIO page writes.
    // The apply row's Accept is the one exit the launcher persists every choice from.
    private static void BuiltInOptionsRoute(TestContext ctx, MenuHost host, ScriptedSeat seat, List<MenuExit> exits)
    {
        var menu = (host.Active as BuiltInPresentation)?.Menu;
        if (menu == null)
        {
            return;
        }

        Press(host, seat, Up);
        Press(host, seat, Up);
        ctx.Check(menu.ShownRowText == LaunchMenu.OptionsRow,
            $"Up from Free Flight wraps onto the multiplayer door, and again onto Options ({menu.ShownRowText})");
        Press(host, seat, Accept);
        ctx.Check(menu.ShownScreen == "Options" && menu.ShownRowCount == 17 && menu.ShownRowText == "Difficulty: Normal",
            $"Accept opens the Options screen with its seventeen rows, the difficulty stepper first ({menu.ShownScreen}, {menu.ShownRowCount}, {menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Difficulty: Hard", $"Right steps the difficulty to Hard ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Default View: Exterior",
            $"the second row is the opening view, unsaved showing the chase view the flight already opens in ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Default View: Cockpit",
            $"Right wraps onto the first of the original's own three views ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Auto Head Turn: Off",
            $"the third row is the head turn, unsaved showing the Off the headLook.autohead key ships ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Auto Head Turn: On",
            $"Right turns it on ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Nearest target after a kill: Off",
            $"the fourth row is the targeting setting, unsaved showing the decoded Off ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Nearest target after a kill: On",
            $"Right turns it on ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Nearest target after a kill: Off",
            $"and Right again turns it back off ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Controller rumble: On",
            $"the fifth row is the rumble toggle, unsaved showing the default On ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Controller rumble: Off",
            $"Right turns the rumble off ({menu.ShownRowText})");
        Press(host, seat, Down);
        string beforeGraphics = menu.ShownRowText;
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText != beforeGraphics && menu.ShownRowText.StartsWith("Graphics: ", System.StringComparison.Ordinal),
            $"the sixth row is the graphics mode, straight under the rumble with no presentation row between, and Right steps it ({beforeGraphics} -> {menu.ShownRowText})");
        string graphics = menu.ShownRowText.EndsWith("Enhanced", System.StringComparison.Ordinal) ? "enhanced" : "original";
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Rocket craters: Off",
            $"the seventh row is the rocket carve, straight under the graphics mode and unsaved showing Off, which is what keeps a shipped default digging nothing ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Rocket craters: On",
            $"Right turns the carve on ({menu.ShownRowText})");
        var display = BuiltInDisplayRows(ctx, host, seat, menu);
        BuiltInAudioRows(ctx, host, seat, menu);
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == LaunchMenu.ControlsRow && menu.ShownHeading == "OPTIONS  (16/17)",
            $"the sixteenth row is the Controls door, the heading counting the window's position ({menu.ShownRowText}, {menu.ShownHeading})");
        Press(host, seat, Accept);
        ctx.Check(menu.ShownScreen == "Controls" && menu.ShownRowCount > 2,
            $"which opens the rebinding screen over a seat's own keymap ({menu.ShownScreen}, {menu.ShownRowCount} rows)");
        Press(host, seat, Back);
        ctx.Check(menu.ShownScreen == "Options" && menu.ShownRowText == LaunchMenu.ControlsRow,
            $"and Back returns to Options on the door it came from ({menu.ShownScreen}, {menu.ShownRowText})");
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(exits.Count == 2 && exits[1] is OptionsApplyExit,
            $"Apply leaves through the host as an OptionsApplyExit ({exits.Count}, {exits[^1].GetType().Name})");
        if (exits.Count == 2 && exits[1] is OptionsApplyExit applied)
        {
            ctx.Check(applied.Graphics == graphics && applied.Difficulty == "hard"
                && applied.NearestAfterKill == false && applied.Rumble == false
                && applied.DefaultView == CSVM.Flight.PilotView.Name(CSVM.Flight.PilotViewMode.Cockpit)
                && applied.AutoHeadTurn == true && applied.RocketCraters == true,
                $"carrying every stepped choice, the targeting setting stepped back off, the rumble turned off, the opening view, the head turn and the carve among them ({applied.Graphics}, {applied.Difficulty}, {applied.NearestAfterKill}, {applied.Rumble}, {applied.DefaultView ?? "none"}, {applied.AutoHeadTurn}, {applied.RocketCraters})");
            ctx.Check(applied.MonitorIndex == display.Monitor && applied.Resolution == display.Resolution
                && applied.DisplayMode == display.DisplayMode && applied.VSync == display.VSync,
                $"and all four display settings the rows stepped ({applied.MonitorIndex}, {applied.Resolution}, {applied.DisplayMode}, {applied.VSync})");
            ctx.Check(applied.AudioMaster == null && applied.AudioMusic == AudioMix.DefaultMusic - SliderControl.KeyStep
                && applied.AudioEffects == null && applied.AudioVoice == AudioMix.MinLevel,
                $"and the levels the volume rows moved, the two left alone still never set ({Level(applied.AudioMaster)}, {Level(applied.AudioMusic)}, {Level(applied.AudioEffects)}, {Level(applied.AudioVoice)})");
            var plan = MonitorSetting.Resolve(applied.MonitorIndex, MonitorSetting.Screens());
            ctx.Check(plan.Source == "options.json" && ResolutionSetting.Resolve(applied.Resolution,
                    ResolutionSetting.ScreenSizes(), applied.DisplayMode).Source == "options.json",
                $"which the launcher's own resolvers take as saved rather than dropping ({plan.Word})");
        }

        ctx.Check(!host.Shown, $"and the presentation is hidden for the launcher to act (shown={host.Shown})");
    }

    // The four display rows, walked from the graphics row: each steps in the store's own words, the
    // monitor row over the machine's screens (one step wraps within a single-screen list, which is
    // why its label is checked against the enumeration rather than for a change), the resolution row
    // over the sizes the standing screen offers once the mode leaves borderless, which pins it, and
    // the mode and pacing rows over their vocabularies. Returns the four words the apply carries.
    private static (string? Monitor, string? Resolution, string? DisplayMode, string? VSync) BuiltInDisplayRows(
        TestContext ctx, MenuHost host, ScriptedSeat seat, LaunchMenu menu)
    {
        var screens = MonitorSetting.Screens();
        int standing = MonitorSetting.Resolve(null, screens).Screen;
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == $"Monitor: {screens.Labels[standing]}",
            $"the sixth row is the monitor, an unsaved index showing the screen the window stands on ({menu.ShownRowText})");
        Press(host, seat, Right);
        int stepped = DisplaySettingRows.Step(standing, 1, screens.Labels.Count);
        ctx.Check(menu.ShownRowText == $"Monitor: {screens.Labels[stepped]}",
            $"Right steps it to the next screen the engine reports ({menu.ShownRowText})");

        var sizes = ResolutionSetting.ScreenSizes();
        Press(host, seat, Down);
        string opened = menu.ShownRowText;
        ctx.Check(opened == $"Resolution: {sizes.Fallback}",
            $"the seventh row is the resolution, showing the screen's own size ({opened})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == opened,
            $"which the shipped borderless mode pins, so Right steps it nowhere ({menu.ShownRowText})");
        ctx.Check(menu.ShownDetail.StartsWith("Borderless runs at", System.StringComparison.Ordinal),
            $"and the row says who owns the size rather than refusing in silence ({menu.ShownDetail})");

        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "Display mode: Borderless",
            $"the eighth row is the display mode, unsaved showing the shipped borderless default ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "Display mode: Fullscreen",
            $"Right steps it one word along the vocabulary ({menu.ShownRowText})");

        // Back up to the size row, which exclusive fullscreen leaves to the player. The pin above
        // is the mode's and not the row's, and this mode flies at the size the row names.
        Press(host, seat, Up);
        Press(host, seat, Right);
        string size = menu.ShownRowText["Resolution: ".Length..];
        ctx.Check(menu.ShownRowText != opened && Offers(sizes.Words, size),
            $"under Fullscreen the same Right steps it to another size the screen can hold ({opened} -> {menu.ShownRowText})");
        Press(host, seat, Down);

        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == "V-Sync: Off", $"the ninth row is V-Sync, unsaved showing the shipped Off ({menu.ShownRowText})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == "V-Sync: 60 FPS",
            $"Right steps it onto the first cap ({menu.ShownRowText})");
        Press(host, seat, Left);
        Press(host, seat, Left);
        ctx.Check(menu.ShownRowText == "V-Sync: On",
            $"and two Lefts step back past Off onto On without wrapping ({menu.ShownRowText})");
        Press(host, seat, Left);
        ctx.Check(menu.ShownRowText == "V-Sync: 144 FPS",
            $"and one more Left wraps onto the last cap rather than stopping ({menu.ShownRowText})");

        return (MonitorSetting.Word(stepped), size, DisplayWords.Fullscreen, "144");
    }

    // The four volume rows, walked from the V-Sync row on a mix nothing has saved. Each shows the
    // shipped level, steps by the AUDIO page's own keyboard step, and clamps at both ends. A step
    // that moves nothing writes nothing, so Master stepped up off full stays never set.
    private static void BuiltInAudioRows(TestContext ctx, MenuHost host, ScriptedSeat seat, LaunchMenu menu)
    {
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == $"Master volume: {AudioMix.DefaultMaster}" && menu.ShownHeading == "OPTIONS  (12/17)",
            $"the twelfth row is the Master level, unsaved showing the shipped full level ({menu.ShownRowText}, {menu.ShownHeading})");
        Press(host, seat, Right);
        ctx.Check(menu.ShownRowText == $"Master volume: {AudioMix.MaxLevel}",
            $"Right at full clamps rather than wrapping to silence ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == $"Music volume: {AudioMix.DefaultMusic}",
            $"the Music level follows, unsaved showing its authored default ({menu.ShownRowText})");
        Press(host, seat, Left);
        ctx.Check(menu.ShownRowText == $"Music volume: {AudioMix.DefaultMusic - SliderControl.KeyStep}",
            $"Left steps it down by the AUDIO page's own step ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == $"Effects volume: {AudioMix.DefaultEffects}",
            $"the Effects level follows ({menu.ShownRowText})");
        Press(host, seat, Down);
        ctx.Check(menu.ShownRowText == $"Voice volume: {AudioMix.DefaultVoice}",
            $"and the Voice level last, the order the AUDIO page draws them in ({menu.ShownRowText})");
        for (int i = 0; i <= (AudioMix.DefaultVoice / SliderControl.KeyStep); i++)
        {
            Press(host, seat, Left);
        }

        ctx.Check(menu.ShownRowText == $"Voice volume: {AudioMix.MinLevel}",
            $"a step past silence clamps there rather than wrapping to full ({menu.ShownRowText})");
    }

    private static string Level(int? level) => level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset";

    private static bool Offers(IReadOnlyList<string> sizes, string word)
    {
        foreach (string size in sizes)
        {
            if (size == word)
            {
                return true;
            }
        }

        return false;
    }

    // Original's own Options route over the install's decoded sections. PREFERENCES opens the
    // Preferences page, and its GAME OPTIONS door the decoded page. That page's rows take every
    // choice, and its CANCEL CHANGES drops them. The walk leaves the top level as it found it.
    // The first three rows are the original's own, in its order, the two after them this port's.
    private static void OriginalOptionsRoute(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, List<MenuExit> exits)
    {
        WalkTo(host, seat, shell, "MM_B_PREFERENCES");
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Options && shell.FocusedKey == OriginalOptionsScreen.GameOptionsDoorKey,
            $"PREFERENCES opens the Preferences page with its GAME OPTIONS door focused ({shell.Screen}, {shell.FocusedKey})");
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.GameOptions && shell.FocusedKey == OriginalOptionsScreen.DifficultyKey,
            $"GAME OPTIONS opens the decoded page on its Difficulty dropdown, the first row ({shell.Screen}, {shell.FocusedKey})");
        var board = shell.Compose();
        int titles = 0;
        int menuTitles = 0;
        foreach (var line in board.Lines)
        {
            titles += line.Text is "GAME OPTIONS" or "Difficulty" or "Default View" or "Auto Head Turn"
                or "Next Target" or "Rumble" ? 1 : 0;
            menuTitles += line.Text is "Menu" ? 1 : 0;
        }

        ctx.Check(titles == 6 && menuTitles == 0,
            $"drawing the section's own tab title over the five row titles and no Menu row ({titles} of 6, {menuTitles} Menu)");
        OriginalGameOptionsPlate(ctx, shell, board);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.OpenGameOption == OriginalOptionsScreen.DifficultyKey && shell.Rows.Count == 3
            && List(shell, OriginalOptionsScreen.DifficultyKey) == null,
            $"Accept opens the dropdown over the three campaign tiers, inside its window and with no bar ({shell.Options.OpenGameOption}, {shell.Rows.Count})");
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.DifficultyChoice == CSVM.Flight.Difficulty.Hard && shell.FocusedKey == OriginalOptionsScreen.DifficultyKey,
            $"and picking the second closes it on Hard ({shell.Options.DifficultyChoice}, {shell.FocusedKey})");
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.OpenGameOption == OriginalOptionsScreen.DefaultViewKey && shell.Rows.Count == 3,
            $"Accept on the Default View row under it opens the dropdown over the original's three views ({shell.Options.OpenGameOption}, {shell.Rows.Count})");
        // The list opens on the item the row stands at, which with nothing saved is the last of the
        // three. One step down wraps onto the first.
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.DefaultViewChoice == CSVM.Flight.PilotView.Name(CSVM.Flight.PilotViewMode.Cockpit),
            $"and a step down wraps onto the list's own first view and picks it ({shell.Options.DefaultViewChoice ?? "none"})");
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.AutoHeadTurnChoice == true && shell.FocusedKey == OriginalOptionsScreen.AutoHeadTurnKey,
            $"Accept on the Auto Head Turn checkbox under it turns the head turn on ({shell.Options.AutoHeadTurnChoice})");
        Press(host, seat, Down);
        ctx.Check(shell.FocusedKey == OriginalOptionsScreen.NearestAfterKillKey,
            $"the row straight under the head turn is Next Target, no presentation row standing between ({shell.FocusedKey})");
        Press(host, seat, Accept);
        ctx.Check(shell.Options.NearestAfterKillChoice == true,
            $"Accept on the Next Target checkbox turns the targeting setting on ({shell.Options.NearestAfterKillChoice})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.GameOptionsCancelKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Options
            && shell.Options.DifficultyChoice == CSVM.Flight.Difficulty.Normal && shell.Options.NearestAfterKillChoice == null
            && shell.Options.DefaultViewChoice == null && shell.Options.AutoHeadTurnChoice == null,
            $"CANCEL CHANGES lands back on Preferences with every edit dropped ({shell.Screen}, {shell.Options.DifficultyChoice}, {shell.Options.NearestAfterKillChoice}, {shell.Options.DefaultViewChoice ?? "none"}, {shell.Options.AutoHeadTurnChoice})");
        WalkTo(host, seat, shell, OriginalShell.OptionsBackKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.TopLevel && exits.Count == 1,
            $"and RETURN TO MAIN MENU leaves the page without an exit ({shell.Screen}, {exits.Count})");
        WalkTo(host, seat, shell, OriginalShell.FreeFlightKey);
    }

    // The grown plate and what stands on it. The page carries more rows than the section's three.
    // The plate is drawn as a head, the repeated row band and a tail rather than one picture. It
    // reaches a whole band further down than the art it is cut from. The two plaques stand that
    // same band below their authored line. Every row stands wholly inside one band, a single band
    // carries two of them, and no row's own reach crosses the moved plaque line.
    private static void OriginalGameOptionsPlate(TestContext ctx, OriginalShell shell, ComposedBoard board)
    {
        var plate = new List<BoardPicture>();
        foreach (var picture in board.Backdrop)
        {
            if (picture.Art.Name.Equals("GO_BackGround.png", StringComparison.OrdinalIgnoreCase))
            {
                plate.Add(picture);
            }
        }

        float top = float.MaxValue;
        float bottom = 0f;
        bool cropped = plate.Count > 0;
        foreach (var picture in plate)
        {
            cropped &= picture.Crop is { } crop && crop.Height > 0f && picture.Width == 0f && picture.Height == 0f;
            top = Math.Min(top, picture.Y);
            bottom = Math.Max(bottom, picture.Y + (picture.Crop?.Height ?? 0f));
        }

        ctx.Check(plate.Count >= 4 && cropped,
            $"the plate is tiled from crops of its own art rather than drawn once or stretched ({plate.Count} pieces, crops {cropped})");
        ctx.Check(Math.Abs(top - AuthoredPlateY) < 0.5f && Math.Abs(bottom - (AuthoredPlateY + AuthoredPlateHeight + AuthoredGameOptionPitch)) < 0.5f,
            $"standing at its authored corner and reaching one whole band further down ({top}, {bottom})");

        float accept = RowY(shell, OriginalOptionsScreen.GameOptionsAcceptKey);
        float cancel = RowY(shell, OriginalOptionsScreen.GameOptionsCancelKey);
        ctx.Check(Math.Abs(accept - (AuthoredPlaqueY + AuthoredGameOptionPitch)) < 0.5f && Math.Abs(cancel - accept) < 0.5f,
            $"with ACCEPT CHANGES and CANCEL CHANGES that same band below their authored line ({accept}, {cancel})");

        // The first band opens one band above the head crop's bottom edge, so every band after it is
        // a whole pitch down from there; a row wholly inside one of them stands on drawn panel.
        float first = plate.Count > 0 ? plate[0].Y + (plate[0].Crop?.Height ?? 0f) - AuthoredGameOptionPitch : 0f;
        var held = new int[Math.Max(1, plate.Count)];
        bool banded = plate.Count > 0;
        float lowest = 0f;
        foreach (var row in shell.Rows)
        {
            if (row.Key is OriginalOptionsScreen.GameOptionsAcceptKey or OriginalOptionsScreen.GameOptionsCancelKey)
            {
                continue;
            }

            lowest = Math.Max(lowest, row.Y + row.Height);
            int band = (int)Math.Floor((row.Y - first) / AuthoredGameOptionPitch);
            banded &= band >= 0 && band < held.Length
                && row.Y + row.Height <= first + ((band + 1) * AuthoredGameOptionPitch);
            held[Math.Clamp(band, 0, held.Length - 1)]++;
        }

        int shared = 0;
        foreach (int count in held)
        {
            shared += count > 1 ? 1 : 0;
        }

        ctx.Check(banded && shared == 1,
            $"every option row standing wholly inside one band, one band carrying the pair the canvas leaves no band for ({banded}, {shared} shared)");
        ctx.Check(lowest > 0f && lowest <= accept,
            $"and no option row reaching past that line, which would take one press for two rows ({lowest} of {accept})");
    }

    private static float RowY(OriginalShell shell, string key)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Key == key)
            {
                return row.Y;
            }
        }

        return -1f;
    }

    // Walks the focus down onto a row by key, the keyboard's own way there.
    private static void WalkTo(MenuHost host, ScriptedSeat seat, OriginalShell shell, string key)
    {
        for (int guard = 0; guard < 32 && shell.FocusedKey != key; guard++)
        {
            Press(host, seat, Down);
        }
    }

    private static OriginalShell? SwitchBackToOriginal(TestContext ctx, MenuHost host)
    {
        host.Deactivate();
        string? reason = host.Select(forceBuiltIn: false, cliOverride: "original");
        host.Show(MenuReturnDestination.TopLevel);
        var shell = (host.Active as OriginalPresentation)?.Shell;
        ctx.Check(host.Selected == PresentationId.Original && reason == null && shell != null,
            $"an Original request re-selects it and Show creates a fresh instance ({host.Selected})");
        ctx.Check(shell is { Screen: OriginalScreen.TopLevel, PickedChapter: null, FocusedKey: OriginalShell.FreeFlightKey },
            $"standing on its top level with nothing picked and the door focused ({shell?.Screen}, {shell?.PickedChapter ?? "none"}, {shell?.FocusedKey})");
        return shell;
    }

    // Original's VIDEO route over the install's decoded sections: the Preferences page's third door
    // opens the decoded page on its monitor dropdown, the rows under it take a screen, a size, a
    // display mode and a frame cap, the checkbox flips the graphics word, CANCEL CHANGES drops them
    // all without an exit, and ACCEPT CHANGES on a second visit leaves as one apply exit carrying them.
    private static void OriginalVideoRoute(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell? shell, List<MenuExit> exits)
    {
        if (shell == null)
        {
            return;
        }

        int before = exits.Count;
        WalkTo(host, seat, shell, "MM_B_PREFERENCES");
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.VideoDoorKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Video && shell.FocusedKey == OriginalOptionsScreen.MonitorKey,
            $"VIDEO opens the decoded page on its Monitor row, the first of the authored rows it carries ({shell.Screen}, {shell.FocusedKey})");
        var board = shell.Compose();
        int titles = 0;
        foreach (var line in board.Lines)
        {
            titles += line.Text is "VIDEO" or "Monitor" or "Resolution" or "Display Mode" or "V-Sync"
                or "Rocket Craters" or "Enhanced Graphics" ? 1 : 0;
        }

        ctx.Check(titles == 7, $"drawing the section's own tab title over the six row titles ({titles} of 7)");
        bool box = false;
        foreach (var plaque in board.Plaques)
        {
            box |= plaque.Art.Frames == 8;
        }

        ctx.Check(box, $"with the checkbox drawn from its eight-state strip ({board.Plaques.Count} plaques)");
        // The monitor row's words are this machine's screens, so the claim is the count rather than
        // the labels: one per screen the engine reports, which is one on a single-screen machine.
        ctx.Check(shell.Options.MonitorWords.Count == Godot.DisplayServer.GetScreenCount(),
            $"the monitor row offers one label per screen ({string.Join(" ", shell.Options.MonitorWords)})");
        Press(host, seat, Right);
        ctx.Check(shell.Options.MonitorChoice != null
            && OptionsStore.TryParseMonitorIndex(shell.Options.MonitorChoice, out int picked) && picked < shell.Options.MonitorWords.Count,
            $"and a sideways step on it takes a screen this machine has ({shell.Options.MonitorChoice ?? "unset"} of {shell.Options.MonitorWords.Count})");
        // The size row under the borderless default, which owns the size. It draws dead and the
        // walk cannot land on it, so the row the walk ends on is what the check names.
        WalkTo(host, seat, shell, OriginalOptionsScreen.ResolutionKey);
        ctx.Check(shell.Options.ResolutionPinned && Row(shell, OriginalOptionsScreen.ResolutionKey) is { Enabled: false }
            && shell.FocusedKey != OriginalOptionsScreen.ResolutionKey && shell.Options.ResolutionChoice == null,
            $"borderless leaves the size row dead, out of the walk's reach ({shell.FocusedKey}, {shell.Options.ResolutionChoice ?? "unset"})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.DisplayModeKey);
        // A list inside the window its own row authors is exactly as tall as its items: no arrows,
        // no thumb and nothing for the pointer to scroll, which is the shape the film shows.
        Press(host, seat, Accept);
        ctx.Check(shell.Rows.Count == DisplayWords.DisplayModes.Count
            && List(shell, OriginalOptionsScreen.DisplayModeKey) == null
            && Row(shell, OriginalOptionsScreen.DisplayModeKey + ":down") == null,
            $"the Display Mode list fits its authored window and draws no bar ({shell.Rows.Count} rows)");
        Press(host, seat, Back);
        Press(host, seat, Right);
        ctx.Check(shell.Options.DisplayModeChoice == DisplayWords.Fullscreen,
            $"a sideways step on the focused row takes the display mode after the borderless default ({shell.Options.DisplayModeChoice ?? "unset"})");
        // The row's own words are this machine's screen sizes, so what it steps to is read back off
        // the shell rather than named. The claim is that the step lands on a size the screen offers,
        // and not on the screen's own size the row opened at.
        WalkTo(host, seat, shell, OriginalOptionsScreen.ResolutionKey);
        Press(host, seat, Right);
        ctx.Check(shell.FocusedKey == OriginalOptionsScreen.ResolutionKey && shell.Options.ResolutionChoice != null
            && shell.Options.ResolutionChoice != ResolutionSetting.ScreenSizes().Fallback
            && shell.Options.ResolutionWords.Contains(shell.Options.ResolutionChoice),
            $"the size row is live again under fullscreen and steps to the next size this screen offers ({shell.Options.ResolutionChoice ?? "unset"} of {shell.Options.ResolutionWords.Count})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.VSyncKey);
        Press(host, seat, Accept);
        OpenVideoListWindow(ctx, host, seat, shell);
        WalkTo(host, seat, shell, OriginalOptionsScreen.VSyncKey);
        Press(host, seat, Accept);
        Press(host, seat, Down);
        Press(host, seat, Down);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.VSyncChoice == "120" && shell.FocusedKey == OriginalOptionsScreen.VSyncKey,
            $"and picking two below the off default closes it on the 120 fps cap ({shell.Options.VSyncChoice ?? "unset"}, {shell.FocusedKey})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.RocketCratersKey);
        ctx.Check(shell.Options.RocketCratersChoice != true,
            $"the carve row above the graphics one opens off, which is what leaves a shipped default digging nothing ({shell.Options.RocketCratersChoice?.ToString() ?? "unset"})");
        Press(host, seat, Accept);
        ctx.Check(shell.Options.RocketCratersChoice == true,
            $"and Accept on its checkbox arms the carve ({shell.Options.RocketCratersChoice?.ToString() ?? "unset"})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.GraphicsKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Options.GraphicsChoice == GraphicsMode.EnhancedWord,
            $"Accept on the checkbox under it flips the graphics word ({shell.Options.GraphicsChoice})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.VideoCancelKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Options && shell.Options.GraphicsChoice == GraphicsMode.Default
            && shell.Options.VSyncChoice == null && shell.Options.DisplayModeChoice == null && shell.Options.ResolutionChoice == null
            && shell.Options.MonitorChoice == null && shell.Options.RocketCratersChoice != true && exits.Count == before,
            $"CANCEL CHANGES lands back on Preferences with all six edits dropped and no exit ({shell.Screen}, {shell.Options.GraphicsChoice}, {shell.Options.VSyncChoice ?? "unset"}, {shell.Options.DisplayModeChoice ?? "unset"}, {shell.Options.ResolutionChoice ?? "unset"}, {shell.Options.MonitorChoice ?? "unset"}, {shell.Options.RocketCratersChoice?.ToString() ?? "unset"})");

        WalkTo(host, seat, shell, OriginalOptionsScreen.VideoDoorKey);
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.DisplayModeKey);
        Press(host, seat, Right);
        WalkTo(host, seat, shell, OriginalOptionsScreen.VSyncKey);
        Press(host, seat, Right);
        WalkTo(host, seat, shell, OriginalOptionsScreen.RocketCratersKey);
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.GraphicsKey);
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.VideoAcceptKey);
        Press(host, seat, Accept);
        ctx.Check(exits.Count == before + 1 && exits[^1] is OptionsApplyExit
        {
            Graphics: GraphicsMode.EnhancedWord, VSync: "60",
            DisplayMode: DisplayWords.Fullscreen, RocketCraters: true,
        },
            $"and ACCEPT CHANGES leaves through the host as one OptionsApplyExit carrying all three words and the carve ({exits.Count - before}, {exits[^1].GetType().Name})");
        ctx.Check(!host.Shown, $"with the presentation hidden for the launcher to act (shown={host.Shown})");
    }

    // The V-Sync list, whose five words outrun the four-row window its own layout row authors: every
    // word is a row so the walk still reaches it, only the window's own are drawn, the arrows and the
    // thumb stand inside the box's right edge, the pointer wheels and drags the window, and a press
    // on the word outside it reaches no row at all. The list is left closed on nothing picked.
    private static void OpenVideoListWindow(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell)
    {
        int drawn = 0;
        foreach (var row in shell.Rows)
        {
            drawn += row.Kind == OriginalRowKind.ListRow && row.Visible ? 1 : 0;
        }

        ctx.Check(shell.Options.OpenVideoOption == OriginalOptionsScreen.VSyncKey
            && shell.Rows.Count == DisplayWords.VSyncChoices.Count + 2 && drawn == 4,
            $"Accept on the V-Sync row opens its five choices in the authored four-row window ({shell.Rows.Count} rows, {drawn} drawn)");
        var bar = List(shell, OriginalOptionsScreen.VSyncKey);
        var arrow = Row(shell, OriginalOptionsScreen.VSyncKey + ":down");
        float edge = (bar?.Window.X ?? 0f) + (bar?.Window.Width ?? 0f);
        ctx.Check(bar is { Window.Scrolls: true } && arrow != null
            && bar.Window.ThumbX + bar.Window.ThumbWidth <= edge && arrow.X + arrow.Width <= edge,
            $"with its thumb and its arrows inside the box's own right edge (thumb {bar?.Window.ThumbX ?? -1}, arrow {arrow?.X ?? -1}, edge {edge})");
        var size = ctx.Host.GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        WheelAndDrag(ctx, host, seat, shell, fit, OriginalOptionsScreen.VSyncKey, "the V-Sync page's open list");
        var offscreen = Row(shell, OriginalOptionsScreen.VSyncKey + ":4");
        ctx.Check(offscreen is { Visible: false },
            $"the word outside the window keeps its place for the walk, unseen ({offscreen?.Visible.ToString() ?? "missing"})");
        if (offscreen == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, offscreen.X + 2f, offscreen.Y + 2f, pressed: true, clicked: true));
        ctx.Check(shell.Options.VSyncChoice == null && shell.Options.OpenVideoOption == null,
            $"and a press on it picks nothing, the pointer reaching no row it cannot see ({shell.Options.VSyncChoice ?? "unset"})");
    }

    // Original's AUDIO route over the install's decoded sections: the Preferences page's second door
    // opens the decoded page on its Master slider, the page draws a thumb per row, a sideways step
    // moves a level and clamps at silence rather than wrapping to full, the open page states its mix
    // to the host every frame and names the level a frame moved only when one did, CANCEL CHANGES
    // drops the edits without an exit and ends the preview, and ACCEPT CHANGES on a second visit
    // leaves as one apply exit.
    private static void OriginalAudioRoute(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell? shell, List<MenuExit> exits, RecordingAudio audio)
    {
        if (shell == null)
        {
            return;
        }

        int before = exits.Count;
        WalkTo(host, seat, shell, "MM_B_PREFERENCES");
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioDoorKey);
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Audio && shell.FocusedKey == OriginalOptionsScreen.AudioMasterKey,
            $"AUDIO opens the decoded page on its Master row, the first of the authored rows it carries ({shell.Screen}, {shell.FocusedKey})");
        var board = shell.Compose();
        int titles = 0;
        int thumbs = 0;
        foreach (var line in board.Lines)
        {
            titles += line.Text is "AUDIO" or "Master" or "Music Volume" or "Effects Volume" or "Voice Volume" ? 1 : 0;
        }

        foreach (var picture in board.Pictures)
        {
            thumbs += picture.Art.Name == "PF_B_Slider.png" ? 1 : 0;
        }

        ctx.Check(titles == 5, $"drawing the section's own tab title over the four row titles ({titles} of 5)");
        ctx.Check(thumbs == 4, $"with the authored thumb drawn once per row ({thumbs} of 4)");
        // The store is a scratch one under --run-tests, so the page opens on the shipped mix: every
        // level never set, each row standing at its own default.
        ctx.Check(shell.Options.AudioMasterChoice == null && shell.Options.AudioMusicChoice == null
            && shell.Options.AudioEffectsChoice == null && shell.Options.AudioVoiceChoice == null,
            $"on a mix nothing has saved, every level reading as never set ({shell.Options.AudioMasterChoice?.ToString() ?? "unset"})");
        audio.Mixes.Clear();
        Press(host, seat, Right);
        ctx.Check(shell.Options.AudioMasterChoice == null,
            $"a step off the top of the Master row moves nothing, so it writes nothing ({shell.Options.AudioMasterChoice?.ToString() ?? "unset"})");
        // A step at an end moves no level, so the frame states the mix and names no moved level:
        // this is what keeps a host from sounding a category once per pointer frame of a drag.
        ctx.Check(audio.Mixes.Count == 1 && audio.Mixes[0].Moved == MenuMixLevel.None
            && audio.Mixes[0].Levels == new AudioLevels(
                AudioMix.DefaultMaster, AudioMix.DefaultMusic, AudioMix.DefaultEffects, AudioMix.DefaultVoice),
            $"the open page states the shipped mix to the host and names no moved level on a frame that moved none ({audio.Mixes.Count}, {(audio.Mixes.Count > 0 ? audio.Mixes[0].Moved : MenuMixLevel.None)})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioMusicKey);
        audio.Mixes.Clear();
        Press(host, seat, Left);
        ctx.Check(shell.Options.AudioMusicChoice == AudioMix.DefaultMusic - SliderControl.KeyStep,
            $"a sideways step on the focused row moves that level by the control's own step ({shell.Options.AudioMusicChoice?.ToString() ?? "unset"})");
        ctx.Check(audio.Mixes.Count == 1 && audio.Mixes[0].Moved == MenuMixLevel.Music
            && audio.Mixes[0].Levels.Music == AudioMix.DefaultMusic - SliderControl.KeyStep,
            $"and names Music as the level that moved, carrying the level it moved to ({(audio.Mixes.Count > 0 ? audio.Mixes[0].Moved : MenuMixLevel.None)})");
        int endsBefore = audio.MixEnds;
        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioCancelKey);
        audio.Mixes.Clear();
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.Options && shell.Options.AudioMusicChoice == null && exits.Count == before,
            $"CANCEL CHANGES lands back on Preferences with the edit dropped and no exit ({shell.Screen}, {shell.Options.AudioMusicChoice?.ToString() ?? "unset"})");
        // Off the page there is no mix to state, and the host is told to put back the one the page
        // opened over, which is the half a player notices when it is wrong.
        ctx.Check(audio.Mixes.Count == 0 && audio.MixEnds > endsBefore,
            $"ending the preview and stating no mix off the page ({audio.Mixes.Count}, ends={audio.MixEnds - endsBefore})");

        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioDoorKey);
        Press(host, seat, Accept);
        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioVoiceKey);
        for (int step = 0; step < 12; step++)
        {
            Press(host, seat, Left);
        }

        ctx.Check(shell.Options.AudioVoiceChoice == AudioMix.MinLevel,
            $"and stepping the Voice row past its floor clamps at silence rather than wrapping to full ({shell.Options.AudioVoiceChoice?.ToString() ?? "unset"})");
        WalkTo(host, seat, shell, OriginalOptionsScreen.AudioAcceptKey);
        Press(host, seat, Accept);
        ctx.Check(exits.Count == before + 1 && exits[^1] is OptionsApplyExit { AudioVoice: AudioMix.MinLevel, AudioMaster: null },
            $"and ACCEPT CHANGES leaves through the host as one OptionsApplyExit carrying the levels ({exits.Count - before}, {exits[^1].GetType().Name})");
        ctx.Check(!host.Shown, $"with the presentation hidden for the launcher to act (shown={host.Shown})");
    }

    // Recovery: the force flag beats an Original override, and a registered Original whose layout
    // is not there falls back to Built-in with the availability reason and the request kept.
    private static void Recovery(TestContext ctx, MenuHost host, PresentationRegistry registry, RecordingAudio audio)
    {
        string? reason = host.Select(forceBuiltIn: true, cliOverride: "original");
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason != null && host.Requested == PresentationId.Original,
            $"--force-builtin resolves Built-in over the Original override and keeps the request ({reason})");

        var missing = new MenuHost(registry, audio, _ => { });
        missing.Availability = id => id == PresentationId.Original ? OriginalAvailability.Load(EmptyDataRoot(ctx), out var why) == null ? why : null : null;
        reason = missing.Select(forceBuiltIn: false, cliOverride: null);
        ctx.Check(missing.Selected == PresentationId.BuiltIn && missing.Requested == PresentationId.Original,
            $"a flagless run over a data root with no decoded layout selects Built-in and keeps the Original request ({missing.Selected}, {missing.Requested})");
        ctx.Check(reason != null && reason.Contains("menu_layout.json", System.StringComparison.Ordinal),
            $"with the reason naming the missing file ({reason})");
    }

    private static string EmptyDataRoot(TestContext ctx)
    {
        string dir = System.IO.Path.Combine(ctx.ScratchDir, "menu-original-empty-root");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static OriginalRow? Row(OriginalShell shell, string key)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    private static MenuCommands Pointer(BoardFit fit, float authoredX, float authoredY, bool pressed = false, bool clicked = false, int wheel = 0) =>
        new() { Pointer = new MenuPointer(fit.X(authoredX), fit.Y(authoredY), pressed, clicked, wheel) };

    // The screen's list under that key, or null. Read fresh after every frame, since a window that
    // moved is a new record.
    private static OriginalList? List(OriginalShell shell, string key)
    {
        foreach (var list in shell.Lists)
        {
            if (list.Key == key)
            {
                return list;
            }
        }

        return null;
    }

    // One click as the Original shell reads it: the press arms the row it lands on and the
    // release still on that row is what fires, so a click is two frames rather than one.
    private static void Click(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        Press(host, seat, frame);
        Press(host, seat, frame with { Pointer = frame.Pointer!.Value with { Pressed = false, Clicked = false } });
    }

    private static void Press(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        seat.Enqueue(frame);
        host.Tick(Dt);
    }

    private sealed class RecordingAudio : IMenuAudio
    {
        public List<string> Cues { get; } = new();

        // What a mix page stated this frame, and how often the preview was ended: the page names
        // four levels and the one that moved, and a real service is what applies and sounds them.
        public List<(AudioLevels Levels, MenuMixLevel Moved)> Mixes { get; } = new();

        public int MixEnds { get; private set; }

        public void Cue(MenuCue cue) => Cues.Add(cue.Name);

        public void BeginNarration(string wavName)
        {
        }

        public void EndNarration()
        {
        }

        public void PreviewMix(AudioLevels levels, MenuMixLevel moved) => Mixes.Add((levels, moved));

        public void EndMixPreview() => MixEnds++;
    }

    private sealed class ScriptedSeat : IMenuInputSource
    {
        private readonly Queue<MenuCommands> _frames = new();

        public string DeviceLabel => "scripted";

        public bool CapturingText { get; set; }

        public void Enqueue(MenuCommands frame) => _frames.Enqueue(frame);

        public void Clear() => _frames.Clear();

        public MenuCommands Poll(float dt) => _frames.Count > 0 ? _frames.Dequeue() : MenuCommands.None;

        public void Prime()
        {
        }
    }
}
