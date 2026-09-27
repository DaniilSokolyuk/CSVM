using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Net;
using CSVM.UI;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// The campaign's network door, driven on two launchscreens at once: a host seated in the
/// campaign cabin and a guest on the Multiplayer board. The wire between them is the in-process
/// loopback. The router is a stub that records what it was asked to give back. Nothing here opens
/// a socket or reaches a router.
/// </summary>
internal static class MenuCoopDoorSuites
{
    private const string Plane = "player_bhawk";

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Network = new() { Loadout = true };

    [Suite("menu-coop-door",
        "The campaign's network door on two launchscreens over the loopback: L / Y in the cabin "
        + "opens and closes the carrier and the router mapping and shows the address, the guest's "
        + "Multiplayer board names the campaign session it joined, Continue holds the guest on a "
        + "waiting board, the remote guest counts in the host's chip strip without a local seat, "
        + "takes a net seat the guest's session hears, and leaving the campaign unmaps the port")]
    internal static void TheCoopDoor(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");

        // The first host wire serves the open-and-close control, the second the match itself. A
        // loopback end binds once, so every open needs its own.
        var spare = LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(3));
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(5));
        var hostWires = new Queue<INetTransport>(new INetTransport[] { spare[0], mesh[0] });
        var unmapped = new List<int>();
        var hostDoor = new NetPlayFeature(
            (_, _, _) => hostWires.Dequeue(),
            (_, _) => throw new InvalidOperationException("the host does not join"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, NetDoorAid.ExternalAddress, "suite"),
            unmapped.Add);
        var guestDoor = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("the guest does not host"),
            (_, _) => mesh[1]);

        var hostMenu = Menu(ctx, hostDoor, out var hostSetup);
        var guestMenu = Menu(ctx, guestDoor, out _);
        hostMenu.CampaignProfiles = CampaignAidProfiles.Store(seeded: true, progressed: true);
        try
        {
            hostMenu.ShowMenu();
            hostMenu.OpenCampaignCabin(CampaignAidProfiles.Pilot);
            OpenAndCloseControl(ctx, hostMenu, hostDoor, unmapped);
            OpenForTheMatch(ctx, hostMenu, hostDoor);
            JoinAndWait(ctx, guestMenu, guestDoor);
            CountTheGuest(ctx, hostMenu, hostSetup);
            SeatTheGuest(ctx, guestMenu, hostDoor, guestDoor);
            LeaveBothEnds(ctx, hostMenu, guestMenu, hostDoor, guestDoor, unmapped);
        }
        finally
        {
            hostDoor.Discard();
            guestDoor.Discard();
            foreach (var menu in new[] { hostMenu, guestMenu })
            {
                ctx.Host.RemoveChild(menu);
                menu.QueueFree();
            }
        }
    }

    // A launchscreen whose only input is the suite's own frames, over its own door.
    private static LaunchMenu Menu(TestContext ctx, NetPlayFeature door, out PlayerSetupFeature setup)
    {
        var host = MenuSuiteHost.Bare(new List<MenuExit>(), ctx.DataRoot, out var seat, netDoor: door);
        setup = host.Features.Get<PlayerSetupFeature>();
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        ctx.Host.AddChild(menu);
        menu.SetProcess(false);
        return menu;
    }

    // ABLE-TO-FAIL CONTROL. The press closes what it opened. A door left open behind a second press
    // leaves a socket listening and a port mapped, with no band to say so.
    private static void OpenAndCloseControl(
        TestContext ctx, LaunchMenu menu, NetPlayFeature door, List<int> unmapped)
    {
        ctx.Check(menu.ShownScreen == "Campaign" && menu.Campaign?.Screen == CampaignScreen.Cabin,
            $"the host is seated in the cabin ({menu.ShownScreen}, {menu.Campaign?.Screen})");
        ctx.Check(menu.ShownFooter.Contains(CoopDoorText.TogglePress, StringComparison.Ordinal),
            $"the cabin's footer names the network press ({menu.ShownFooter})");
        ctx.Check(menu.ShownChips.Count == 0 && menu.ShownNetBand.Length == 0,
            $"a solo cabin with the door shut draws no strip ({menu.ShownChips.Count}, '{menu.ShownNetBand}')");

        menu.Drive(Network);
        ctx.Check(door.IsCoopHost && menu.ShownNetBand.StartsWith("NETWORK OPEN", StringComparison.Ordinal),
            $"L / Y opens the carrier as a campaign host and the band shows it ({door.Stage}, '{menu.ShownNetBand}')");
        AwaitMapping(door);
        menu.Drive(Network);
        ctx.Check(door.Stage == NetDoorStage.Shut && unmapped.Count == 1 && menu.ShownNetBand.Length == 0,
            $"ABLE-TO-FAIL CONTROL: a second press closes the carrier and gives the port back ({door.Stage}, {unmapped.Count} unmapped)");
    }

    // The open the match stands on: the band reads the router's address and the advert names the
    // cabin's next mission under the seated profile.
    private static void OpenForTheMatch(TestContext ctx, LaunchMenu menu, NetPlayFeature door)
    {
        menu.Drive(Network);
        AwaitMapping(door);
        menu.Drive(MenuCommands.None);
        string address = $"{NetDoorAid.ExternalAddress}:{NetPlayFeature.DefaultPort.ToString(CultureInfo.InvariantCulture)}";
        ctx.Check(menu.ShownNetBand.Contains(address, StringComparison.Ordinal),
            $"the band shows where guests reach the host ({menu.ShownNetBand})");
        door.Step(0.016);
        var advert = door.Advertising;
        int next = menu.Campaign!.Feature.NextMissionSeq;
        ctx.Check(advert is { Kind: NetSessionKind.CampaignCoop, Host: CampaignAidProfiles.Pilot } && advert.Value.MissionSeq == next,
            $"the advert names a campaign, the profile and its next mission ({advert?.Kind}, {advert?.Host}, {advert?.MissionSeq} vs {next})");
    }

    // The guest's side: the board names the session and Continue holds it on the waiting board.
    private static void JoinAndWait(TestContext ctx, LaunchMenu menu, NetPlayFeature door)
    {
        menu.ShowMenu();
        menu.Drive(Up);
        menu.Drive(Accept);
        menu.Drive(Down);
        ctx.Check(menu.ShownScreen == "Network" && menu.ShownRowText == "Join that address",
            $"the guest's board, cursor on Join ({menu.ShownScreen}, {menu.ShownRowText})");
        menu.Drive(Accept);
        door.Step(0.016);
        menu.Drive(Down);
        ctx.Check(door.IsCoopGuest, $"the join lands and the host's advert names a campaign ({door.Stage}, {door.Advert?.Kind})");
        ctx.Check(menu.ShownDetail.Contains("Campaign co-op, chapter", StringComparison.Ordinal)
                  && menu.ShownDetail.Contains($"hosted by {CampaignAidProfiles.Pilot}", StringComparison.Ordinal),
            $"the board names the session as campaign co-op ({menu.ShownDetail})");
        ctx.Check(menu.ShownRowText == CoopDoorText.WaitRow,
            $"and its way on leads to the wait, not to a map ({menu.ShownRowText})");

        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == LaunchMenu.CoopWaitScreen && menu.ShownHeading == CoopDoorText.WaitingHeading,
            $"Continue holds the guest on the waiting board ({menu.ShownScreen}, {menu.ShownHeading})");
        ctx.Check(menu.ShownRowCount == 1 && menu.ShownRowText == CoopDoorText.LeaveRow,
            $"whose one row leaves ({menu.ShownRowCount}, {menu.ShownRowText})");
        ctx.Check(menu.ShownDetail.EndsWith("Waiting for the host to launch the mission.", StringComparison.Ordinal),
            $"and whose status says what it waits for ({menu.ShownDetail})");
    }

    // The host counts the remote guest in its strip, while the local field keeps its one seat.
    private static void CountTheGuest(TestContext ctx, LaunchMenu menu, PlayerSetupFeature setup)
    {
        menu.Drive(MenuCommands.None);
        var chips = menu.ShownChips;
        ctx.Check(chips.Count == 2 && chips[0] == "P1" && chips[1] == "P2" + LaunchMenu.RemoteChipMark,
            $"the chip strip counts the remote guest after the local pilot ({string.Join(", ", chips)})");
        ctx.Check(setup.Seats.Count == 1 && menu.Campaign!.Field.Players == 1,
            $"and the guest takes no local seat and no pane ({setup.Seats.Count} seats, field {menu.Campaign!.Field.Players})");
    }

    // The launch's wire, as the next step takes it: the host's field seats the guest, and the
    // guest's session hears which seat is its own.
    private static void SeatTheGuest(TestContext ctx, LaunchMenu guestMenu, NetPlayFeature hostDoor, NetPlayFeature guestDoor)
    {
        var hostLaunch = hostDoor.BuildLaunch()!;
        var guestLaunch = guestDoor.BuildLaunch()!;
        var roster = NetSeats.Field(hostLaunch.Transport.LocalPeer, new[] { Plane }, hostLaunch.Transport.Peers, Plane);
        _ = NetSession.Host(hostLaunch.Transport, roster, seed: 11);
        guestLaunch.Transport.Step(0.016);
        ctx.Check(guestDoor.HostStarted,
            $"the host's answer is held for the guest's session, which is the sign the host launched");
        guestMenu.Drive(MenuCommands.None);
        ctx.Check(guestMenu.ShownDetail.EndsWith("The host has launched the mission.", StringComparison.Ordinal),
            $"the waiting board says so ({guestMenu.ShownDetail})");

        var guest = NetSession.Guest(guestLaunch.Transport);
        int guestPeer = guestLaunch.Transport.LocalPeer;
        ctx.Check(guest.Joined && guest.LocalSeat == 1 && roster[1].PeerId == guestPeer && !roster[1].IsLocal,
            $"the remote guest holds net seat 1 and its session knows it ({guest.Joined}, seat {guest.LocalSeat}, peer {roster[1].PeerId} vs {guestPeer})");
        ctx.Check(guest.DroppedUnknown == 0,
            $"and no payload reached the guest's session unrouted: the advert stays in the lobby ({guest.DroppedUnknown})");
    }

    // Leaving: the guest's Leave hangs up, and the host backing out of the campaign closes its
    // door and gives the router's port back.
    private static void LeaveBothEnds(TestContext ctx, LaunchMenu hostMenu, LaunchMenu guestMenu,
        NetPlayFeature hostDoor, NetPlayFeature guestDoor, List<int> unmapped)
    {
        guestMenu.Drive(Accept);
        ctx.Check(guestMenu.ShownScreen == "Network" && guestDoor.Stage == NetDoorStage.Shut,
            $"Leave on the waiting board hangs up onto the shut board ({guestMenu.ShownScreen}, {guestDoor.Stage})");

        for (int i = 0; i < 4 && hostMenu.ShownScreen != "Mode"; i++)
        {
            hostMenu.Drive(Back);
        }

        ctx.Check(hostMenu.ShownScreen == "Mode" && hostDoor.Stage == NetDoorStage.Shut,
            $"leaving the campaign closes the host's door ({hostMenu.ShownScreen}, {hostDoor.Stage})");
        ctx.Check(unmapped.Count == 2 && unmapped.All(port => port == NetPlayFeature.DefaultPort),
            $"and gives the router's port back ({string.Join(", ", unmapped)})");
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
}
