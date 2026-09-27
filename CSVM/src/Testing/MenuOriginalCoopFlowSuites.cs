using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Net;
using CSVM.Session;
using CSVM.Session.Campaign;
using CSVM.UI;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// The co-op session flow in the Original presentation, driven on two menu hosts over the
/// in-process loopback. A host seated in the cabin opens its network door and a guest joins. The
/// guest follows the host's boards with their navigation greyed, answers Ready on its own check,
/// and follows the host's debrief. The guest's own profile store is byte-identical at the end.
/// </summary>
internal static class MenuOriginalCoopFlowSuites
{
    private const float Dt = 1f / 60f;
    private const string GuestOwnPilot = "Lucy";

    [Suite("menu-original-coop-flow",
        "The Original co-op session flow over the loopback: a joined guest lands on the host's cabin "
        + "with every navigation button greyed and dead and is seated under its own last pilot's name, "
        + "follows the host into the briefing and the flight check, where its pick carries its plane "
        + "and ammunition and the host's FLY MISSION waits until the guest's Ready arrives, which "
        + "repaints the host's check with no input at the host, a host "
        + "back in the cabin clears the Ready, the host's launch names the flight InMission and the "
        + "guest launches into nothing it did not see open, the guest's plane and ammunition outlive "
        + "the host's Restart and the host builds the guest's seat on them, the host's debrief is the "
        + "guest's with the host's cash, RETURN TO CABIN takes both back, after a lost mission REPLAY "
        + "MISSION goes back through the briefing, the check and Ready with the guest still on its "
        + "pick, a plane change puts the guest on the new plane's default ammunition, and the guest's "
        + "own saves are untouched")]
    internal static void TheCoopFlow(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(11));
        var hostDoor = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => throw new InvalidOperationException("the host does not join"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, NetDoorAid.ExternalAddress, "suite"),
            _ => { });
        var guestDoor = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("a guest does not host"),
            (_, _) => mesh[1]);

        // Outside the aids' own directory, which every seeded store call wipes and rebuilds.
        string guestDir = Path.Combine(Path.GetTempPath(), "CSVM", "coop-guest-profiles",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(guestDir))
        {
            Directory.Delete(guestDir, recursive: true);
        }

        Directory.CreateDirectory(guestDir);
        var guestStore = new CampaignProfileStore(guestDir);
        guestStore.Save(CampaignProfileDef.NewProfile(GuestOwnPilot));
        guestStore.RecordLastPlayed(GuestOwnPilot);
        var before = Snapshot(guestDir);

        var exits = new List<MenuExit>();
        End? host = null;
        End? guest = null;
        try
        {
            host = Open(ctx, layout, hostDoor, CampaignAidProfiles.Store(seeded: true, progressed: true), exits);
            guest = Open(ctx, layout, guestDoor, guestStore, new List<MenuExit>());
            if (host == null || guest == null)
            {
                return;
            }

            host.Shell.Campaign.OpenCampaignOver(
                CampaignAidProfiles.Store(seeded: true, progressed: true), CampaignAidProfiles.Planes());
            host.Shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
            ClickRow(ctx, host, OriginalCampaignScreen.CoopDoorKey);
            AwaitMapping(hostDoor);
            Join(ctx, host, guest);
            FollowTheBoards(ctx, host, guest);
            ReadyGatesTheLaunch(ctx, host, guest);
            Launch(ctx, host, guest, exits);
            var remembered = PickOutlivesARestart(ctx, host, guest);
            ShareTheDebrief(ctx, host, guest);
            RetryGoesBackThroughSelection(ctx, host, guest, exits, remembered);
        }
        finally
        {
            host?.Host.Deactivate();
            guest?.Host.Deactivate();
            hostDoor.Discard();
            guestDoor.Discard();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }

        var after = Snapshot(guestDir);
        ctx.Check(after.Count == before.Count && after.All(f => before.TryGetValue(f.Key, out var was) && was.SequenceEqual(f.Value)),
            $"the guest's own profile store is byte-identical after the session ({before.Count} files before, {after.Count} after)");
    }

    // The guest opens the Connection page and joins; the host's word takes it onto the cabin.
    private static void Join(TestContext ctx, End host, End guest)
    {
        ClickRow(ctx, guest, OriginalShell.MultiplayerKey);
        guest.Door.OpenJoin();
        Pump(host, guest, frames: 6);
        ctx.Check(guest.Door.IsCoopGuest && guest.Shell.Campaign.IsGuest && guest.Shell.Screen == OriginalScreen.CampaignCabin,
            $"the joined guest stands on the host's cabin ({guest.Door.Stage}, {guest.Shell.Screen})");
        ctx.Check(host.Door.CoopGuests.Count == 1, $"and the host seats it as a co-op guest ({host.Door.CoopGuests.Count})");
        ctx.Check(host.Door.CoopGuests.Count == 1 && host.Door.CoopGuests[0].Name == GuestOwnPilot,
            $"under the name of the guest's own last pilot, read and never written ({(host.Door.CoopGuests.Count == 1 ? host.Door.CoopGuests[0].Name : "-")})");
        foreach (var key in new[] { nameof(BoardButton.NextMission), nameof(BoardButton.PreviousMissions), nameof(BoardButton.ReturnToMainMenu) })
        {
            ctx.Check(Row(guest.Shell, key) is { Enabled: false }, $"the guest's {key} is greyed");
        }

        ctx.Check(Row(host.Shell, nameof(BoardButton.NextMission)) is { Enabled: true },
            $"ABLE-TO-FAIL CONTROL: the host's own NEXT MISSION is live");
        ClickRow(ctx, guest, nameof(BoardButton.NextMission));
        Pump(host, guest, frames: 2);
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignCabin && host.Shell.Screen == OriginalScreen.CampaignCabin,
            $"a guest's press on the greyed NEXT MISSION moves neither end ({guest.Shell.Screen}, {host.Shell.Screen})");
    }

    // The host walks into the briefing and the check; the guest follows each board.
    private static void FollowTheBoards(TestContext ctx, End host, End guest)
    {
        ClickRow(ctx, host, nameof(BoardButton.NextMission));
        Pump(host, guest, frames: 3);
        ctx.Check(host.Shell.Screen == OriginalScreen.CampaignBriefing && guest.Shell.Screen == OriginalScreen.CampaignBriefing,
            $"the host's NEXT MISSION takes the guest into the briefing ({guest.Shell.Screen})");
        ClickRow(ctx, host, nameof(BoardButton.GoToFlightCheck));
        Pump(host, guest, frames: 3);
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignFlightCheck,
            $"and GO TO FLIGHT CHECK onto the guest's own check ({guest.Shell.Screen})");
        ctx.Check(Row(guest.Shell, nameof(BoardButton.ReturnToBriefing)) is { Enabled: false }
                  && Row(guest.Shell, nameof(BoardButton.FlyMission)) is { Enabled: true },
            $"where its RETURN TO BRIEFING is greyed and its Ready (FLY MISSION) is live");
    }

    // Launch waits on the guest's Ready; the host backing out to the cabin clears it.
    private static void ReadyGatesTheLaunch(TestContext ctx, End host, End guest)
    {
        ctx.Check(Row(host.Shell, nameof(BoardButton.FlyMission)) is { Enabled: false } && !host.Door.CoopAllReady,
            $"the host's FLY MISSION is greyed while the guest is not Ready");
        // The guest takes a stock plane other than the starter, so the pick is seen crossing.
        var campaign = guest.Host.Features.Get<CampaignFeature>();
        var hangar = campaign.Profile;
        ctx.Check(hangar is { Planes.Count: > 1 }, $"the guest's hangar offers more than one plane ({hangar?.Planes.Count})");
        if (hangar != null)
        {
            hangar.SelectedPlane = Math.Max(0, hangar.Planes.FindIndex(plane => plane.Airframe != NetPlayFeature.StarterAirframe));

            // The ammo screen's own write: explosive in the first gun slot.
            hangar.Planes[hangar.SelectedPlane].Ammo[0] = 3;
        }

        int picked = campaign.GuestAirframe;
        var fit = campaign.GuestCoopFit;
        ReadyRepaintsTheHost(ctx, host, guest);
        Pump(host, guest, frames: 4);
        ctx.Check(guest.Door.CoopReady && host.Door.CoopAllReady && host.Door.CoopGuests[0].Ready,
            $"the guest's press answers Ready and the host hears it ({guest.Door.CoopReady}, {host.Door.CoopAllReady})");
        ctx.Check(picked != NetPlayFeature.StarterAirframe && host.Door.CoopGuests[0].Airframe == picked,
            $"with the plane the guest picked ({host.Door.CoopGuests[0].Airframe}, picked {picked})");
        ctx.Check(fit.AmmoAt(0) == 3 && host.Door.CoopGuests[0].Fit == fit,
            $"and the ammunition it set on that plane ({host.Door.CoopGuests[0].Fit.AmmoAt(0)}, set {fit.AmmoAt(0)})");
        ctx.Check(Row(host.Shell, nameof(BoardButton.FlyMission)) is { Enabled: true }, $"so the host's FLY MISSION is live");

        ClickRow(ctx, host, nameof(BoardButton.ReturnToBriefing));
        Pump(host, guest, frames: 4);
        ctx.Check(host.Door.CoopAllReady, $"a move between the check and the briefing keeps the Ready");
        host.Seat.Enqueue(new MenuCommands { Back = true });
        Pump(host, guest, frames: 4);
        ctx.Check(host.Shell.Screen == OriginalScreen.CampaignCabin && !host.Door.CoopAllReady && !guest.Door.CoopReady,
            $"the host backing out to the cabin clears it on both ends ({host.Shell.Screen}, {host.Door.CoopAllReady}, {guest.Door.CoopReady})");
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignCabin, $"and the guest follows back ({guest.Shell.Screen})");
    }

    // The guest's Ready on the host's check, with no input at the host. A quiet frame keeps the
    // board it drew, and the Ready arriving recomposes it with the guest's chip marked.
    private static void ReadyRepaintsTheHost(TestContext ctx, End host, End guest)
    {
        var shown = (host.Host.Active as OriginalPresentation)!;
        host.Host.Tick(Dt);
        var quiet = shown.ShownBoard;
        host.Host.Tick(Dt);
        ctx.Check(quiet != null && ReferenceEquals(shown.ShownBoard, quiet),
            $"ABLE-TO-FAIL CONTROL: a quiet frame on the host's check composes no new board");
        ClickRow(ctx, guest, nameof(BoardButton.FlyMission));

        // The press lands after the guest's door stepped, so its pick leaves on the next frame.
        guest.Host.Tick(Dt);
        int frames = 0;
        while (frames < 2 && ReferenceEquals(shown.ShownBoard, quiet))
        {
            host.Host.Tick(Dt);
            frames++;
        }

        ctx.Check(!ReferenceEquals(shown.ShownBoard, quiet) && host.Door.CoopGuests is [{ Ready: true }],
            $"the guest's Ready repaints the host's check within {frames} frame(s) with no input at the host");
    }

    // The host flies once the guest is Ready again; the flight is advertised InMission.
    private static void Launch(TestContext ctx, End host, End guest, List<MenuExit> exits)
    {
        ClickRow(ctx, host, nameof(BoardButton.NextMission));
        Pump(host, guest, frames: 3);
        ClickRow(ctx, host, nameof(BoardButton.GoToFlightCheck));
        Pump(host, guest, frames: 3);
        ClickRow(ctx, guest, nameof(BoardButton.FlyMission));
        Pump(host, guest, frames: 4);
        ClickRow(ctx, host, nameof(BoardButton.FlyMission));
        ctx.Check(exits.Count == 1 && exits[0] is CampaignMissionExit { Net: not null },
            $"the host's FLY MISSION leaves as a networked campaign launch ({exits.Count})");

        // The menu is hidden in flight, and the launcher steps the door each frame instead.
        for (int i = 0; i < 4; i++)
        {
            host.Door.Step(Dt);
            guest.Host.Tick(Dt);
        }

        ctx.Check(host.Door.Advertising?.Status == NetSessionStatus.InMission,
            $"the host's advert reads In mission while it flies ({host.Door.Advertising?.Status})");
        ctx.Check(guest.Door.CoopFlow?.Screen == NetCoopScreen.InMission && !guest.Door.CoopLaunchDue,
            $"the guest hears the flight but no session opener reached it here, so it waits ({guest.Door.CoopFlow?.Screen})");
    }

    // The host's pause-sheet Restart in the launcher's order, with the guest in the flight. The
    // guest's campaign is rebuilt around its return, and it answers the new round on what it flew.
    private static (byte Airframe, CoopFit Fit) PickOutlivesARestart(TestContext ctx, End host, End guest)
    {
        byte picked = guest.Door.CoopPickAirframe;
        var fit = guest.Door.CoopPickFit;
        ctx.Check(picked != NetPlayFeature.StarterAirframe && fit.AmmoAt(0) == 3,
            $"ABLE-TO-FAIL CONTROL: the pick the guest flew is not a fresh join's starter and stock fit ({picked}, {fit.AmmoAt(0)})");
        var guestWire = guest.Door.BuildLaunch();
        var relaunch = CSVM.Session.Launch.Launcher.CoopRelaunch(host.Door);
        ctx.Check(guestWire != null && relaunch != null, $"the guest flies and the host's Restart relaunches its door");
        if (guestWire == null || relaunch == null)
        {
            return (picked, fit);
        }

        GuestFliesItsPick(ctx, host, relaunch, (picked, fit), "the restart's field builds the guest on the pick it flew");
        for (int i = 0; i < 20 && !CSVM.Session.Launch.Launcher.CoopGuestFlightOver(guest.Door); i++)
        {
            StepInFlight(host, guest, relaunch, guestWire);
        }

        ctx.Check(CSVM.Session.Launch.Launcher.CoopGuestFlightOver(guest.Door), $"the host's new round ends the guest's flight");
        GuestReturns(ctx, host, guest, relaunch, (picked, fit), "after the restart");
        ctx.Check(((NetLobby)relaunch.Transport).Picks.Values.All(pick => pick.Epoch == host.Door.CoopEpoch),
            $"and it answers under the restart's round, which the host waits for");
        GuestFliesItsPick(ctx, host, relaunch, (picked, fit), "so the host builds the guest on that pick again");
        return (picked, fit);
    }

    // A flight's end on the guest's side: its launcher takes the wire back and reopens the menu on
    // the host's boards, which rebuilds its campaign. The pick it flew must survive that.
    private static void GuestReturns(
        TestContext ctx, End host, End guest, MenuNetLaunch hostWire, (byte Airframe, CoopFit Fit) pick, string when)
    {
        guest.Door.Reclaim();
        guest.Host.Show(new CoopGuestReturn(null));
        for (int i = 0; i < 4; i++)
        {
            // A host still in flight has its wire stepped by the session, and one on its boards by its menu.
            if (host.Door.Released)
            {
                hostWire.Transport.Step(Dt);
                host.Door.Step(Dt);
            }
            else
            {
                host.Host.Tick(Dt);
            }

            guest.Host.Tick(Dt);
        }

        var campaign = guest.Host.Features.Get<CampaignFeature>();
        ctx.Check(campaign.IsGuest && campaign.GuestAirframe == pick.Airframe && campaign.GuestCoopFit == pick.Fit,
            $"{when}, the guest's reopened campaign stands on the plane and ammunition it picked ({campaign.GuestAirframe}, ammo {campaign.GuestCoopFit.AmmoAt(0)})");
        ctx.Check(host.Door.CoopGuests.Count == 1 && host.Door.CoopGuests[0].Airframe == pick.Airframe && host.Door.CoopGuests[0].Fit == pick.Fit,
            $"{when}, the host hears the same pick ({(host.Door.CoopGuests.Count == 1 ? host.Door.CoopGuests[0].Fit.AmmoAt(0) : -9)})");
    }

    // One frame of a flight with no session: each released wire is stepped as its session would.
    private static void StepInFlight(End host, End guest, MenuNetLaunch hostWire, MenuNetLaunch guestWire)
    {
        hostWire.Transport.Step(Dt);
        host.Door.Step(Dt);
        guestWire.Transport.Step(Dt);
        guest.Door.Step(Dt);
    }

    // The host comes back to its debrief; the guest follows it and RETURN TO CABIN takes both home.
    private static void ShareTheDebrief(TestContext ctx, End host, End guest)
    {
        var door = host.Door;
        ctx.Check(door.Reclaim(), $"the host takes its wire back after the flight");
        int seq = Math.Max(0, CampaignAidProfiles.MissionsFlown - 1);
        host.Host.Show(new DebriefReturn(CampaignAidProfiles.Pilot, seq, MissionWon: true));
        Pump(host, guest, frames: 4);
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignScrapbook,
            $"the guest follows the host into its debrief ({guest.Shell.Screen})");
        var profile = guest.Host.Features.Get<CampaignFeature>().Profile;
        var run = profile != null ? CampaignProgression.ResultOf(profile, seq)?.Latest : null;
        var hostRun = CampaignProgression.ResultOf(
            CampaignAidProfiles.Store(seeded: true, progressed: true).Load(CampaignAidProfiles.Pilot)!, seq)?.Latest;
        ctx.Check(run != null && hostRun != null && run.Money == hostRun.Money && run.CompletedMask == hostRun.CompletedMask,
            $"its book reads the host's objectives and cash ({run?.CompletedMask}/{hostRun?.CompletedMask}, {run?.Money}/{hostRun?.Money})");
        ctx.Check(Row(guest.Shell, nameof(BoardButton.ReturnToCabin)) is { Enabled: false },
            $"and its RETURN TO CABIN is the host's to press");
        ClickRow(ctx, host, nameof(BoardButton.ReturnToCabin));
        Pump(host, guest, frames: 4);
        ctx.Check(host.Shell.Screen == OriginalScreen.CampaignCabin && guest.Shell.Screen == OriginalScreen.CampaignCabin,
            $"the host's RETURN TO CABIN takes both back ({host.Shell.Screen}, {guest.Shell.Screen})");
    }

    // Retry is the book's REPLAY MISSION after a lost mission both ends flew: both go to the
    // briefing under a new round. The host flies again only once the guest has answered Ready on its
    // check once more, and the guest answers on the pick it flew.
    private static void RetryGoesBackThroughSelection(
        TestContext ctx, End host, End guest, List<MenuExit> exits, (byte Airframe, CoopFit Fit) remembered)
    {
        ClickRow(ctx, host, nameof(BoardButton.NextMission));
        Pump(host, guest, frames: 3);
        ClickRow(ctx, host, nameof(BoardButton.GoToFlightCheck));
        Pump(host, guest, frames: 3);
        ClickRow(ctx, guest, nameof(BoardButton.FlyMission));
        Pump(host, guest, frames: 4);
        ClickRow(ctx, host, nameof(BoardButton.FlyMission));
        var guestWire = guest.Door.BuildLaunch();
        if (exits.LastOrDefault() is not CampaignMissionExit { Net: { } hostWire } || exits.Count != 2 || guestWire == null)
        {
            ctx.Check(false, $"both ends fly the mission the retry follows ({exits.Count} launch(es), guest {guestWire != null})");
            return;
        }

        StepInFlight(host, guest, hostWire, guestWire);
        ctx.Check(host.Door.Reclaim(), $"the host takes its wire back after the lost mission");
        int seq = Math.Max(0, CampaignAidProfiles.MissionsFlown - 1);
        host.Host.Show(new DebriefReturn(CampaignAidProfiles.Pilot, seq, MissionWon: false));
        for (int i = 0; i < 20 && !CSVM.Session.Launch.Launcher.CoopGuestFlightOver(guest.Door); i++)
        {
            host.Host.Tick(Dt);
            guestWire.Transport.Step(Dt);
            guest.Door.Step(Dt);
        }

        ctx.Check(CSVM.Session.Launch.Launcher.CoopGuestFlightOver(guest.Door), $"the host's lost debrief ends the guest's flight");
        GuestReturns(ctx, host, guest, hostWire, remembered, "after the lost mission");
        Pump(host, guest, frames: 4);
        byte debriefRound = host.Door.CoopEpoch;
        // A guest that flew nothing here holds no time, and its book then offers no REPLAY row at all.
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignScrapbook && Row(guest.Shell, nameof(BoardButton.ReplayMission)) is not { Enabled: true },
            $"the guest follows the debrief, where REPLAY MISSION is the host's to press ({guest.Shell.Screen})");
        ClickRow(ctx, host, nameof(BoardButton.ReplayMission));
        Pump(host, guest, frames: 4);
        ctx.Check(host.Shell.Screen == OriginalScreen.CampaignBriefing && guest.Shell.Screen == OriginalScreen.CampaignBriefing,
            $"the host's REPLAY MISSION takes both into the briefing ({host.Shell.Screen}, {guest.Shell.Screen})");
        ctx.Check(host.Door.CoopEpoch != debriefRound && !host.Door.CoopAllReady && !guest.Door.CoopReady,
            $"under a new round of picks with no Ready standing ({debriefRound} to {host.Door.CoopEpoch})");
        ClickRow(ctx, host, nameof(BoardButton.GoToFlightCheck));
        Pump(host, guest, frames: 3);
        ctx.Check(guest.Shell.Screen == OriginalScreen.CampaignFlightCheck && Row(host.Shell, nameof(BoardButton.FlyMission)) is { Enabled: false },
            $"ABLE-TO-FAIL CONTROL: on the check again, the host's FLY MISSION waits for the guest ({guest.Shell.Screen})");
        ClickRow(ctx, guest, nameof(BoardButton.FlyMission));
        Pump(host, guest, frames: 4);
        ctx.Check(host.Door.CoopAllReady && Row(host.Shell, nameof(BoardButton.FlyMission)) is { Enabled: true },
            $"and the guest's Ready makes it live, so the retry went back through selection and Ready");
        GuestFliesItsPick(ctx, host, hostWire, remembered, "the retry's launch builds the guest on the pick it kept");

        // CHANGE PLANE's commit onto the starter, which this guest has not fitted.
        var campaign = guest.Host.Features.Get<CampaignFeature>();
        var planes = campaign.Profile?.Planes;
        int starter = planes?.FindIndex(plane => plane.Airframe == NetPlayFeature.StarterAirframe) ?? -1;
        campaign.CommitPlanes(starter, null);
        Pump(host, guest, frames: 4);
        var fresh = new OwnedPlane();
        var stock = CoopFit.Of(fresh.Ammo, fresh.Ordnance);
        ctx.Check(starter >= 0 && campaign.GuestAirframe == NetPlayFeature.StarterAirframe && campaign.GuestCoopFit == stock,
            $"changing the plane puts the guest on the new plane's default ammunition ({campaign.GuestAirframe}, ammo {campaign.GuestCoopFit.AmmoAt(0)})");
        GuestFliesItsPick(ctx, host, hostWire, ((byte)NetPlayFeature.StarterAirframe, stock), "and the host builds the new plane on it");
        host.Seat.Enqueue(new MenuCommands { Back = true });
        Pump(host, guest, frames: 4);
    }

    // The host's launch field for the guest's seat, over the host's wire.
    private static void GuestFliesItsPick(TestContext ctx, End host, MenuNetLaunch launch, (byte Airframe, CoopFit Fit) pick, string what)
    {
        var own = new[] { UI.Hangar.PlanePickerRoster.AirframeNode(NetPlayFeature.StarterAirframe) };
        var (roster, seatFits) = CSVM.Session.Launch.Launcher.CoopLaunchField(
            host.Door, launch.Transport, own, Array.Empty<Flight.Weapons.LoadoutChoice?>(), Flight.Weapons.StockLoadouts.Load());
        ctx.Check(roster.Length == 2 && roster[1].PlaneNode == UI.Hangar.PlanePickerRoster.AirframeNode(pick.Airframe) && seatFits[1] == pick.Fit,
            $"{what} ({(roster.Length == 2 ? roster[1].PlaneNode : "-")}, ammo {(seatFits.Length == 2 ? seatFits[1].AmmoAt(0) : -9)})");
    }

    private static End? Open(TestContext ctx, MenuLayout layout, NetPlayFeature door, CampaignProfileStore profiles, List<MenuExit> exits)
    {
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, new MenuInput { Keyboard = true })
        {
            CampaignProfiles = profiles,
        });
        var menu = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(menu, ctx.DataRoot, netDoor: door);
        menu.AddSeat(seat);
        menu.Select(forceBuiltIn: false, cliOverride: "original");
        menu.Show(MenuReturnDestination.TopLevel);
        var shell = (menu.Active as OriginalPresentation)?.Shell;
        ctx.Check(shell is { Screen: OriginalScreen.TopLevel }, $"each end shows Original on the top level ({shell?.Screen})");
        if (shell == null)
        {
            menu.Deactivate();
            return null;
        }

        return new End(menu, seat, shell);
    }

    private static Dictionary<string, byte[]> Snapshot(string dir)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (string path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(dir, path)] = File.ReadAllBytes(path);
        }

        return files;
    }

    // The mapping lands on a worker thread, so the wait is on the wall clock rather than a count.
    private static void AwaitMapping(NetPlayFeature door)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (door.PortMap == null && waited.Elapsed.TotalSeconds < 20.0)
        {
            door.Step(0.0);
            System.Threading.Thread.Sleep(1);
        }
    }

    // Idle frames on both ends in turn: the frame is what steps each end's door.
    private static void Pump(End host, End guest, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            host.Host.Tick(Dt);
            guest.Host.Tick(Dt);
        }
    }

    // A pointer click on the row under that key, read fresh: the press arms it and the release fires.
    private static void ClickRow(TestContext ctx, End end, string key)
    {
        var row = Row(end.Shell, key);
        ctx.Check(row != null, $"the showing screen carries {key} ({end.Shell.Screen})");
        if (row == null)
        {
            return;
        }

        var size = ctx.Host.GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        float x = fit.X(row.X + Math.Min(5f, row.Width / 2f));
        float y = fit.Y(row.Y + Math.Min(5f, row.Height / 2f));
        end.Seat.Enqueue(new MenuCommands { Pointer = new MenuPointer(x, y, true, true, 0) });
        end.Host.Tick(Dt);
        end.Seat.Enqueue(new MenuCommands { Pointer = new MenuPointer(x, y, false, false, 0) });
        end.Host.Tick(Dt);
    }

    private static OriginalRow? Row(OriginalShell shell, string key) => shell.Rows.FirstOrDefault(row => row.Key == key);

    private sealed record End(MenuHost Host, ScriptedSeat Seat, OriginalShell Shell)
    {
        public NetPlayFeature Door => Host.Features.Get<NetPlayFeature>();
    }

    private sealed class ScriptedSeat : IMenuInputSource
    {
        private readonly Queue<MenuCommands> _frames = new();

        public string DeviceLabel => "scripted";

        public bool CapturingText { get; set; }

        public void Enqueue(MenuCommands frame) => _frames.Enqueue(frame);

        public MenuCommands Poll(float dt) => _frames.Count > 0 ? _frames.Dequeue() : MenuCommands.None;

        public void Prime()
        {
        }
    }
}
