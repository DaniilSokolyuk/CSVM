using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.UI;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;

namespace CSVM.Testing;

/// <summary>
/// The Original presentation's network doors, driven on three menu hosts at once. A host sits in
/// the campaign cabin and two guests stand on the top level. The games wire is the in-process
/// loopback and the LAN search runs over <see cref="LoopbackLan"/>, both bound on the loopback
/// address. The router is a stub that records what it was asked to give back. A second suite
/// stands the shipped discovery socket up on 127.0.0.1 and searches it by unicast.
/// </summary>
internal static class MenuOriginalConnectionSuites
{
    private const float Dt = 1f / 60f;
    private const string Loopback = "127.0.0.1";

    [Suite("menu-original-connection",
        "The Original presentation's network doors over the loopback and an in-process LAN: the "
        + "cabin's HOST CO-OP opens the carrier, the router mapping and the LAN answer, and CLOSE "
        + "NETWORK gives all three back, the Multiplayer plaque opens the Connection page, its "
        + "Connect over LAN TCP/IP lists the host as one row of five columns, Join Game lands the "
        + "guest on the host's cabin, a second guest joins, a fourth human is seated and a fifth is "
        + "refused as full, a silent drop tells a guest the host left, and CLOSE NETWORK tells the "
        + "other the host closed the game and puts it back on the Connection page")]
    internal static void TheConnectionPage(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        // The first host wire serves the open-and-close control, the second the match. A loopback
        // end binds once, so every open needs its own. The mesh is connected from the start, so the
        // host's end is gated: a guest reaches it only when that guest joins.
        var lan = new LoopbackLan();
        var spare = LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(3));
        var mesh = LoopbackTransport.Mesh(5, LoopbackConditions.Perfect, new Random(5));
        var gate = new ArrivalGate(mesh[0]);
        var hostWires = new Queue<INetTransport>(new INetTransport[] { spare[0], gate });
        var unmapped = new List<int>();
        var hostDoor = new NetPlayFeature(
            (_, _, _) => hostWires.Dequeue(),
            (_, _) => throw new InvalidOperationException("the host does not join"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, NetDoorAid.ExternalAddress, "suite"),
            unmapped.Add,
            lan.Bind);
        var doors = new List<NetPlayFeature> { hostDoor };
        for (int i = 1; i < mesh.Count; i++)
        {
            int end = i;
            doors.Add(new NetPlayFeature(
                (_, _, _) => throw new InvalidOperationException("a guest does not host"),
                (_, _) =>
                {
                    gate.Arrive(mesh[end].LocalPeer);
                    return mesh[end];
                },
                lan: lan.Bind));
        }

        foreach (var door in doors)
        {
            door.BindAddress = Loopback;
            door.SearchAddress = Loopback;
        }

        var ends = new List<End>();
        try
        {
            var host = Open(ctx, layout, doors[0], ends);
            var told = Open(ctx, layout, doors[1], ends);
            var dropped = Open(ctx, layout, doors[2], ends);
            if (host == null || told == null || dropped == null)
            {
                return;
            }

            host.Shell.Campaign.OpenCampaignOver(
                CampaignAidProfiles.Store(seeded: true, progressed: true), CampaignAidProfiles.Planes());
            host.Shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
            OpenAndCloseControl(ctx, host, hostDoor, unmapped);
            OpenForTheMatch(ctx, host, hostDoor);
            JoinThroughTheList(ctx, told, ends, 1, "the first guest");
            JoinThroughTheList(ctx, dropped, ends, 2, "the second guest");
            FillTheGame(ctx, ends, hostDoor, doors[3], doors[4]);
            DropOne(ctx, dropped, mesh[0], mesh[2].LocalPeer);
            CloseTheGame(ctx, host, told, ends, hostDoor, unmapped);
        }
        finally
        {
            foreach (var end in ends)
            {
                end.Host.Deactivate();
            }

            doors.ForEach(door => door.Discard());
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    [Suite("menu-original-lobby",
        "The Multiplayer Lobby over the loopback: the Connection page's Host opens it as a Dogfight "
        + "host, the games list reads the Dogfight, its environment and Waiting, a joined guest "
        + "lands in it, the host's map, Time 5 and Limited Lives reach the guest, a guest's option "
        + "set is refused, a Ready guest's plane box stays live and its changed pick clears its Ready "
        + "on both ends, the guest's second stock plane and a non-default shell build its seat "
        + "in the host's field, one chat line arrives once on each end, LAUNCH waits for every "
        + "Ready, the guest launches behind the host on the same rules, a completed match lands "
        + "both ends on Game Scores with the same scores and every Ready cleared, a second LAUNCH "
        + "goes out with nobody rejoining, and Leave Game lands a second guest on the Connection "
        + "page, whose games list's Create Game opens a hosted lobby of its own")]
    internal static void TheLobby(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        var lan = new LoopbackLan();
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(11));
        var gate = new ArrivalGate(mesh[0]);
        var hostDoor = new NetPlayFeature(
            (_, _, _) => gate,
            (_, _) => throw new InvalidOperationException("the host does not join"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, NetDoorAid.ExternalAddress, "suite"),
            _ => { },
            lan.Bind);
        var doors = new List<NetPlayFeature> { hostDoor };
        for (int i = 1; i < mesh.Count; i++)
        {
            // A guest hosts only through the games list's Create Game, on a lone wire of its own.
            int end = i;
            doors.Add(new NetPlayFeature(
                (_, _, _) => LoopbackTransport.Mesh(1, LoopbackConditions.Perfect, new Random(end))[0],
                (_, _) =>
                {
                    gate.Arrive(mesh[end].LocalPeer);
                    return new Hangup(mesh[end]);
                },
                lan: lan.Bind));
        }

        foreach (var door in doors)
        {
            door.BindAddress = Loopback;
            door.SearchAddress = Loopback;
        }

        var ends = new List<End>();
        var hostExits = new List<MenuExit>();
        var guestExits = new List<MenuExit>();
        try
        {
            var host = Open(ctx, layout, doors[0], ends, hostExits);
            var guest = Open(ctx, layout, doors[1], ends, guestExits);
            var leaver = Open(ctx, layout, doors[2], ends);
            if (host == null || guest == null || leaver == null)
            {
                return;
            }

            if (!HostTheLobby(ctx, host) || !JoinTheLobby(ctx, guest, ends) || !JoinTheLobby(ctx, leaver, ends))
            {
                return;
            }

            LeaveTheLobby(ctx, host, leaver, ends);
            SetTheOptions(ctx, host, guest, ends);
            int slot = PickThePlane(ctx, host, guest, ends);
            Chat(ctx, host, guest, ends);
            ctx.Check(Row(host.Shell, OriginalLobbyScreen.ScoresTabKey) is { Enabled: false },
                $"ABLE-TO-FAIL CONTROL: Game Scores is greyed before any match has landed");
            var launch = LaunchTheMatch(ctx, host, guest, ends, hostExits);
            if (launch?.Net is { } wire)
            {
                var roster = HostField(ctx, launch, wire, slot);
                GuestLaunch(ctx, wire, roster, guest, guestExits);
                LandOnTheScores(ctx, host, guest, ends, mesh, guestExits);
                LaunchAgain(ctx, host, guest, ends, hostExits, guestExits);
            }
        }
        finally
        {
            foreach (var end in ends)
            {
                end.Host.Deactivate();
            }

            doors.ForEach(door => door.Discard());
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    [Suite("menu-original-builtin-host",
        "An Original guest against a Built-in Dogfight host over the loopback: the host's door opens "
        + "with its lobby unshown, the guest finds it on the games list and lands in the lobby, its "
        + "third stock plane reaches the host, the host's launch waits for the guest's Ready and "
        + "then writes its map and time into the options, and the guest launches behind the host "
        + "on that map and time in its own pick")]
    internal static void TheBuiltInHost(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        // The host has no menu of its own here: the Built-in door is the NetPlayFeature it opens,
        // stepped by hand between the guest's frames.
        var lan = new LoopbackLan();
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(13));
        var gate = new ArrivalGate(mesh[0]);
        var hostDoor = new NetPlayFeature(
            (_, _, _) => gate,
            (_, _) => throw new InvalidOperationException("the host does not join"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, NetDoorAid.ExternalAddress, "suite"),
            _ => { },
            lan.Bind);
        var guestDoor = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("the guest does not host"),
            (_, _) =>
            {
                gate.Arrive(mesh[1].LocalPeer);
                return new Hangup(mesh[1]);
            },
            lan: lan.Bind);
        foreach (var door in new[] { hostDoor, guestDoor })
        {
            door.BindAddress = Loopback;
            door.SearchAddress = Loopback;
        }

        var ends = new List<End>();
        var guestExits = new List<MenuExit>();
        try
        {
            var guest = Open(ctx, layout, guestDoor, ends, guestExits);
            if (guest == null)
            {
                return;
            }

            hostDoor.OpenHost(NetSeats.MaxPlayers - 1);
            ctx.Check(hostDoor.Dogfight is { IsHost: true, Shown: false }, $"the Built-in host's door opens with its lobby unshown ({hostDoor.Stage})");
            ClickRow(ctx, guest, OriginalShell.MultiplayerKey);
            ClickRow(ctx, guest, OriginalConnectionScreen.ConnectKey);
            for (int frame = 0; frame < 6 && guest.Shell.Connection.Listed.Count == 0; frame++)
            {
                Frames(hostDoor, guest, 1);
            }

            if (guest.Shell.Connection.Listed.Count != 1)
            {
                ctx.Check(false, $"the search lists the Built-in host's game ({guest.Shell.Connection.Listed.Count})");
                return;
            }

            ClickRow(ctx, guest, OriginalConnectionScreen.GameKey(0));
            ClickRow(ctx, guest, OriginalConnectionScreen.JoinKey);
            Frames(hostDoor, guest, 6);
            ctx.Check(guest.Shell.Screen == OriginalScreen.Lobby && guestDoor.Dogfight is { IsHost: false, HasOptions: true },
                $"Join Game lands the guest in the Built-in host's lobby ({guest.Shell.Screen}, {guestDoor.Stage}, {guest.Shell.Dialog?.Message})");
            if (guest.Shell.Screen != OriginalScreen.Lobby)
            {
                return;
            }

            ClickRow(ctx, guest, OriginalLobbyScreen.PlaneTabKey);
            ClickRow(ctx, guest, OriginalLobbyScreen.PlaneKey);
            ClickRow(ctx, guest, OriginalLobbyScreen.PlaneKey + ":2");
            Frames(hostDoor, guest, 4);
            var lobby = hostDoor.Dogfight!;
            ctx.Check(lobby.Players.Count == 2 && lobby.Players[1].Airframe == 2,
                $"the guest's third stock plane reaches the Built-in host ({string.Join(",", lobby.Players.Select(p => p.Airframe))})");
            string chapter = DogfightLobby.ChapterOf(2);
            var rules = new VersusRules(0, 5);
            ctx.Check(lobby.CheckBuiltInLaunch(chapter, rules) == DogfightLobby.GuestsNotReady,
                $"ABLE-TO-FAIL CONTROL: the host's launch waits while the guest is not Ready");
            ClickRow(ctx, guest, OriginalLobbyScreen.ReadyKey);
            Frames(hostDoor, guest, 4);
            ctx.Check(lobby.CheckBuiltInLaunch(chapter, rules) == null,
                $"once the guest is Ready the launch may go ({lobby.Players[1].Ready})");
            Frames(hostDoor, guest, 4);
            var heard = guestDoor.Dogfight!.Options;
            ctx.Check(heard is { Environment: 2, TimeMinutes: 5, Victory: DogfightVictory.Time },
                $"and the launch's map and time reach the guest as the lobby's options ({heard.Environment}, {heard.TimeMinutes}, {heard.Victory})");
            HostTheBuiltInLaunch(ctx, hostDoor, guest, guestExits, chapter);
        }
        finally
        {
            foreach (var end in ends)
            {
                end.Host.Deactivate();
            }

            hostDoor.Discard();
            guestDoor.Discard();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    [Suite("lan-discovery",
        "The shipped LAN discovery socket on the loopback: a responder bound on the discovery port "
        + "answers a search sent to 127.0.0.1 by unicast with the advert and game port it was "
        + "handed, a datagram that is not a query is read and gets no answer, and a game that "
        + "stops answering survives one silent round and leaves on the next")]
    internal static void TheDiscoverySocket(TestContext ctx)
    {
        LanDiscoverySocket answer;
        LanDiscoverySocket ask;
        try
        {
            answer = LanDiscoverySocket.Bind(LanDiscovery.Port, Loopback);
        }
        catch (InvalidOperationException e)
        {
            ctx.Check(false, $"the discovery port binds on the loopback ({e.Message})");
            return;
        }

        try
        {
            ask = LanDiscoverySocket.Bind(0, Loopback);
        }
        catch (InvalidOperationException e)
        {
            answer.Dispose();
            ctx.Check(false, $"a search socket binds a free port on the loopback ({e.Message})");
            return;
        }

        var responder = new LanResponder(answer);
        var search = new LanSearch(ask, Loopback, LanDiscovery.Port);
        try
        {
            var advert = new SessionAdvertMessage(NetSessionKind.CampaignCoop, 3, 2, CampaignAidProfiles.Pilot, NetSessionStatus.Waiting, 4);
            search.Ask();
            Settle(() => responder.Poll(advert, NetPlayFeature.DefaultPort), search, () => search.Games.Count > 0);
            var games = search.Games;
            ctx.Check(games.Count == 1, $"the search hears the one responder ({games.Count})");
            if (games.Count == 1)
            {
                ctx.Check(games[0].Address == Loopback && games[0].Port == NetPlayFeature.DefaultPort && games[0].Advert == advert,
                    $"at the address it answered from, with the game port and advert it was handed ({games[0].Address}, {games[0].Port}, {games[0].Advert.Host})");
            }

            ctx.Check(responder.Answered == 1, $"the responder answered one query ({responder.Answered})");

            // ABLE-TO-FAIL CONTROL: a datagram that is not a query is read and never answered.
            ask.Send(Loopback, LanDiscovery.Port, new byte[] { 0x43, 0x53, 0x56, 0x4D });
            Settle(() => responder.Poll(advert, NetPlayFeature.DefaultPort), search, () => false, seconds: 0.25);
            ctx.Check(responder.Answered == 1, $"a datagram that is not a query gets no answer ({responder.Answered})");

            search.Ask();
            Settle(() => { }, search, () => false, seconds: 0.25);
            ctx.Check(search.Games.Count == 1, $"a game that misses one round is still listed ({search.Games.Count})");
            search.Ask();
            Settle(() => { }, search, () => false, seconds: 0.25);
            ctx.Check(search.Games.Count == 0, $"and leaves once it misses a second ({search.Games.Count})");
        }
        finally
        {
            responder.Dispose();
            search.Dispose();
        }
    }

    // The Connection page's Host, the door's advert and the lobby it opens. True when it opened.
    private static bool HostTheLobby(TestContext ctx, End host)
    {
        ClickRow(ctx, host, OriginalShell.MultiplayerKey);
        ctx.Check(Row(host.Shell, OriginalConnectionScreen.HostKey) is { Enabled: true },
            $"Host is live on the Connection page over the network door ({host.Shell.Screen})");
        ClickRow(ctx, host, OriginalConnectionScreen.HostKey);
        Pump(host);
        var door = host.Door;
        ctx.Check(host.Shell.Screen == OriginalScreen.Lobby && door.Dogfight is { IsHost: true },
            $"Host opens the lobby as a Dogfight's host ({host.Shell.Screen}, {door.Stage})");
        ctx.Check(door.Advertising is { Kind: NetSessionKind.Dogfight, MissionSeq: 0, Status: NetSessionStatus.Waiting },
            $"and advertises a waiting Dogfight on the first environment ({door.Advertising?.Kind}, {door.Advertising?.MissionSeq}, {door.Advertising?.Status})");
        return host.Shell.Screen == OriginalScreen.Lobby;
    }

    // A guest's walk to the lobby: the plaque, Connect, the one row that reads as a Dogfight, Join.
    private static bool JoinTheLobby(TestContext ctx, End guest, List<End> ends)
    {
        var shell = guest.Shell;
        ClickRow(ctx, guest, OriginalShell.MultiplayerKey);
        ClickRow(ctx, guest, OriginalConnectionScreen.ConnectKey);
        for (int frame = 0; frame < 6 && shell.Connection.Listed.Count == 0; frame++)
        {
            Pump(ends.ToArray());
        }

        if (shell.Connection.Listed.Count != 1)
        {
            ctx.Check(false, $"the search lists the one open game ({shell.Connection.Listed.Count})");
            return false;
        }

        var cells = shell.Connection.Cells(shell.Connection.Listed[0]);
        ctx.Check(cells.Count == 5 && cells[2] == "Dogfight" && cells[3] == DogfightLobby.EnvironmentName(0) && cells[4] == "Waiting"
                  && cells[1].EndsWith($"/{NetSeats.MaxPlayers}", StringComparison.Ordinal),
            $"the games list reads the Dogfight, its environment, n of cap and Waiting ({string.Join(" | ", cells)})");
        ClickRow(ctx, guest, OriginalConnectionScreen.GameKey(0));
        ClickRow(ctx, guest, OriginalConnectionScreen.JoinKey);
        for (int frame = 0; frame < 6; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(shell.Screen == OriginalScreen.Lobby && guest.Door.Dogfight is { IsHost: false, HasOptions: true },
            $"Join Game lands the guest in the lobby with the host's options ({shell.Screen}, {guest.Door.Stage}, {shell.Dialog?.Message})");
        return shell.Screen == OriginalScreen.Lobby;
    }

    // The second guest's Leave Game: back on the Connection page, and out of the host's count.
    private static void LeaveTheLobby(TestContext ctx, End host, End leaver, List<End> ends)
    {
        var lobby = host.Door.Dogfight!;
        ctx.Check(lobby.Players.Count == 3, $"the host lists three players ({lobby.Players.Count})");
        ClickRow(ctx, leaver, OriginalLobbyScreen.LeaveKey);
        ctx.Check(leaver.Shell.Screen == OriginalScreen.Connection && leaver.Door.Stage == NetDoorStage.Shut,
            $"Leave Game lands on the Connection page with the door shut ({leaver.Shell.Screen}, {leaver.Door.Stage})");
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(lobby.Players.Count == 2, $"and the host's list drops the pilot who left ({lobby.Players.Count})");
        CreateFromTheList(ctx, leaver, ends);
    }

    // The games list's Create Game is the second door to a hosted lobby, left again at once.
    private static void CreateFromTheList(TestContext ctx, End leaver, List<End> ends)
    {
        var shell = leaver.Shell;
        ClickRow(ctx, leaver, OriginalConnectionScreen.ConnectKey);
        for (int frame = 0; frame < 6 && shell.Connection.Listed.Count == 0; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(Row(shell, OriginalConnectionScreen.CreateKey) is { Enabled: true },
            $"Create Game is live on the games list over the network door ({shell.Screen}, {shell.Connection.Listed.Count} listed)");
        if (Row(shell, OriginalConnectionScreen.CreateKey) is not { Enabled: true })
        {
            return;
        }

        ClickRow(ctx, leaver, OriginalConnectionScreen.CreateKey);
        Pump(leaver);
        ctx.Check(shell.Screen == OriginalScreen.Lobby && leaver.Door.Dogfight is { IsHost: true },
            $"Create Game opens the lobby as a Dogfight's host ({shell.Screen}, {leaver.Door.Stage}, {shell.Dialog?.Message})");
        if (shell.Screen == OriginalScreen.Lobby)
        {
            ClickRow(ctx, leaver, OriginalLobbyScreen.LeaveKey);
        }

        ctx.Check(shell.Screen == OriginalScreen.Connection && leaver.Door.Stage == NetDoorStage.Shut,
            $"and its Leave Game shuts the door again ({shell.Screen}, {leaver.Door.Stage})");
    }

    // The host's map, Time 5 and Limited Lives, read on the guest. The guest's own boxes are dead.
    private static void SetTheOptions(TestContext ctx, End host, End guest, List<End> ends)
    {
        ctx.Check(Row(guest.Shell, OriginalLobbyScreen.EnvironmentKey) is { Enabled: false }
                  && Row(guest.Shell, OriginalLobbyScreen.TimeRadioKey) is { Enabled: false }
                  && guest.Door.Dogfight!.SetEnvironment(3) == false,
            $"a guest's option controls are greyed and its option set is refused");
        ctx.Check(Row(host.Shell, OriginalLobbyScreen.TypeKey) is { Enabled: true } && host.Door.Dogfight!.SetMissionType(DogfightMissionType.CaptureTheFlag) == false,
            $"the host's Type box is live but a greyed type is refused");
        ClickRow(ctx, host, OriginalLobbyScreen.EnvironmentKey);
        ctx.Check(host.Shell.Lobby.OpenDropdown == OriginalLobbyScreen.EnvironmentKey, $"the Environment box opens its list ({host.Shell.Lobby.OpenDropdown})");
        ClickRow(ctx, host, OriginalLobbyScreen.EnvironmentKey + ":3");
        ClickRow(ctx, host, OriginalLobbyScreen.TimeKey);
        TypeInto(host, new MenuCommands { Erase = true }, new MenuCommands { Erase = true }, new MenuCommands { Typed = "5" });
        ClickRow(ctx, host, OriginalLobbyScreen.LimitedLivesKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        var heard = guest.Door.Dogfight!.Options;
        ctx.Check(heard is { Environment: 3, TimeMinutes: 5, LimitedLives: true, Victory: DogfightVictory.Time },
            $"the guest reads the host's map, Time 5 and Limited Lives ({heard.Environment}, {heard.TimeMinutes}, {heard.LimitedLives}, {heard.Victory})");
        ctx.Check(host.Door.Advertising?.MissionSeq == 3, $"and the advert names the new environment ({host.Door.Advertising?.MissionSeq})");
    }

    // The guest's second stock plane and a shell of its own on its first gun, picked while Ready,
    // which the change clears on both ends. Returns that gun's slot.
    private static int PickThePlane(TestContext ctx, End host, End guest, List<End> ends)
    {
        var lobby = guest.Door.Dogfight!;
        ClickRow(ctx, guest, OriginalLobbyScreen.ReadyKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(lobby.Ready && host.Door.Dogfight!.Players[1].Ready,
            $"ABLE-TO-FAIL CONTROL: the guest's Ready reaches the host before any pick changes ({lobby.Ready}, {host.Door.Dogfight!.Players[1].Ready})");
        ClickRow(ctx, guest, OriginalLobbyScreen.PlaneTabKey);
        ctx.Check(Row(guest.Shell, OriginalLobbyScreen.PlaneKey) is { Enabled: true },
            $"a Ready guest's plane box stays live ({Row(guest.Shell, OriginalLobbyScreen.PlaneKey)?.Enabled})");
        ClickRow(ctx, guest, OriginalLobbyScreen.PlaneKey);
        ClickRow(ctx, guest, OriginalLobbyScreen.PlaneKey + ":1");
        ctx.Check(lobby.Airframe == 1, $"the Select Plane list picks the second stock plane ({lobby.Airframe})");
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(!lobby.Ready && host.Door.Dogfight!.Players[1] is { Ready: false, Airframe: 1 },
            $"and the changed pick clears the guest's Ready on both ends ({lobby.Ready}, {host.Door.Dogfight!.Players[1].Ready}, {host.Door.Dogfight!.Players[1].Airframe})");
        ClickRow(ctx, guest, OriginalLobbyScreen.AmmoTabKey);
        var gun = guest.Shell.Rows.FirstOrDefault(r => r.Enabled && r.Kind == OriginalRowKind.Dropdown
                                                      && r.Key.StartsWith("MPL_D_GUN_", StringComparison.Ordinal));
        if (gun == null)
        {
            ctx.Check(false, $"the Select Ammo page carries a live gun box");
            return -1;
        }

        int slot = int.Parse(gun.Key["MPL_D_GUN_".Length..], System.Globalization.CultureInfo.InvariantCulture);
        ClickRow(ctx, guest, gun.Key);
        ClickRow(ctx, guest, gun.Key + ":2");
        ctx.Check(lobby.Fit.AmmoAt(slot) == 2 && !lobby.Fit.IsStock,
            $"its gun box stores armour-piercing on slot {slot} ({lobby.Fit.AmmoAt(slot)})");
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        return slot;
    }

    // One line typed in the guest's chat box and sent: it lands once on each end.
    private static void Chat(TestContext ctx, End host, End guest, List<End> ends)
    {
        ClickRow(ctx, guest, OriginalLobbyScreen.ChatKey);
        TypeInto(guest, new MenuCommands { Typed = "hello" });
        ctx.Check(guest.Shell.Lobby.ChatDraft == "hello", $"the chat box takes the typed line ({guest.Shell.Lobby.ChatDraft})");
        ClickRow(ctx, guest, OriginalLobbyScreen.SendKey);
        for (int frame = 0; frame < 6; frame++)
        {
            Pump(ends.ToArray());
        }

        var here = host.Door.Dogfight!.Chat;
        var there = guest.Door.Dogfight!.Chat;
        ctx.Check(here.Count == 1 && there.Count == 1 && here[0].Text == "hello" && there[0].Text == "hello",
            $"one chat line arrives once on each end (host {here.Count}, guest {there.Count})");
    }

    // LAUNCH! waits for both Ready marks, then hands the host's launch out.
    private static LaunchExit? LaunchTheMatch(TestContext ctx, End host, End guest, List<End> ends, List<MenuExit> exits)
    {
        ClickRow(ctx, host, OriginalLobbyScreen.MissionTabKey);
        ClickRow(ctx, host, OriginalLobbyScreen.ReadyKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(Row(host.Shell, OriginalLobbyScreen.LaunchKey) is { Enabled: false } && host.Door.Dogfight!.Ready,
            $"ABLE-TO-FAIL CONTROL: with the host Ready and the guest not, LAUNCH! is greyed");
        ClickRow(ctx, guest, OriginalLobbyScreen.ReadyKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(Row(host.Shell, OriginalLobbyScreen.LaunchKey) is { Enabled: true },
            $"once the guest is Ready too LAUNCH! is live ({string.Join(",", host.Door.Dogfight!.Players.Select(p => p.Ready))})");
        int before = exits.Count;
        ClickRow(ctx, host, OriginalLobbyScreen.LaunchKey);
        var launch = exits.Skip(before).OfType<LaunchExit>().FirstOrDefault();
        ctx.Check(launch is { Mode: MenuMode.Versus, Net.IsHost: true, Match: { TimeLimitMinutes: 5, KillTarget: 0, Lives: DogfightLobby.DefaultLives } }
                  && launch.Chapter == DogfightLobby.ChapterOf(3),
            $"LAUNCH! hands out a Versus launch on the lobby's chapter and rules ({launch?.Chapter}, {launch?.Match})");
        return launch;
    }

    // The host's field off its launch: the guest's seat on the guest's pick. The control reads the
    // same peers with the picks withheld.
    private static NetSeat[] HostField(TestContext ctx, LaunchExit launch, MenuNetLaunch wire, int slot)
    {
        var planes = launch.Seats.Select(s => s.PlaneNode).ToList();
        var fits = launch.Seats.Select(s => s.Fit).ToList();
        var stock = StockLoadouts.Load();
        var (roster, seatFits) = CSVM.Session.Launcher.VersusLaunchField(wire.Transport, planes, fits, stock);
        ctx.Check(roster.Length == 2 && roster[1].PlaneNode == PlanePickerRoster.AirframeNode(1)
                  && slot >= 0 && seatFits[1].AmmoAt(slot) == 2,
            $"the host's roster builds the guest's seat on its pick and fit ({string.Join(", ", roster.Select(s => s.PlaneNode))})");
        var bare = ((NetLobby)wire.Transport).Inner;
        var (withheld, _) = CSVM.Session.Launcher.VersusLaunchField(bare, planes, fits, stock);
        ctx.Check(withheld.Length == 2 && withheld[1].PlaneNode == planes[0],
            $"ABLE-TO-FAIL CONTROL: with the pick withheld the seat takes the local airframe ({withheld.LastOrDefault()?.PlaneNode})");
        return roster;
    }

    // The host's session opener reaching the guest's door, which then launches the guest on the
    // same chapter and rules, in its own pick.
    private static void GuestLaunch(TestContext ctx, MenuNetLaunch wire, NetSeat[] roster, End guest, List<MenuExit> exits)
    {
        int before = exits.Count;
        _ = NetSession.Host((NetLobby)wire.Transport, roster, 7UL);
        for (int frame = 0; frame < 4 && exits.Count == before; frame++)
        {
            Pump(guest);
        }

        var launch = exits.Skip(before).OfType<LaunchExit>().FirstOrDefault();
        ctx.Check(launch is { Mode: MenuMode.Versus, Net.IsHost: false, Match: { TimeLimitMinutes: 5, Lives: DogfightLobby.DefaultLives } }
                  && launch.Chapter == DogfightLobby.ChapterOf(3) && launch.Seats.Count == 1
                  && launch.Seats[0].PlaneNode == PlanePickerRoster.AirframeNode(1),
            $"the guest launches behind the host on the same chapter and rules in its own pick ({launch?.Chapter}, {launch?.Match}, {launch?.Seats.FirstOrDefault()?.PlaneNode})");
    }

    // A completed match's Exit on both ends, as the launcher runs it. Each door takes its wire back,
    // and the menu comes back on a LobbyReturn built off that end's own match.
    private static void LandOnTheScores(TestContext ctx, End host, End guest, List<End> ends, IReadOnlyList<LoopbackTransport> mesh, List<MenuExit> guestExits)
    {
        var played = new VersusMatch(2, killTarget: 0, timeLimit: 60f);
        played.RegisterKill(1, 0);
        played.RegisterKill(1, 0);
        played.RegisterDeath(1);
        var heard = new VersusMatch(2, killTarget: 0, timeLimit: 60f);
        heard.Replicate();
        foreach (var line in played.Standings())
        {
            heard.ApplyScore(line.PlayerIndex, line.Score, line.Kills, line.Deaths);
        }

        ctx.Check(CSVM.Session.Launcher.LobbyLanding(true, host.Door.Dogfight, played) == null,
            $"ABLE-TO-FAIL CONTROL: a match still running lands nowhere near the lobby");
        played.Advance(60f);
        heard.ApplyState(0, 60f, 0f, ended: true);
        var hostLanding = CSVM.Session.Launcher.LobbyLanding(true, host.Door.Dogfight, played);
        var guestLanding = CSVM.Session.Launcher.LobbyLanding(true, guest.Door.Dogfight, heard);
        ctx.Check(hostLanding != null && guestLanding != null, $"a completed match lands both ends on their lobby");
        ctx.Check(host.Door.Reclaim() && guest.Door.Reclaim(), $"and both doors take their wire back");
        if (hostLanding == null || guestLanding == null)
        {
            return;
        }

        // The match's tail: a session payload the host sent before its own Exit, still in flight.
        int launched = guestExits.Count;
        mesh[0].Send(mesh[1].LocalPeer, new byte[] { 0xEE, 1, 2, 3 }, NetReliability.Reliable);
        host.Host.Show(hostLanding);
        guest.Host.Show(guestLanding);
        for (int frame = 0; frame < 6; frame++)
        {
            Pump(ends.ToArray());
        }

        var here = host.Door.Dogfight!;
        var there = guest.Door.Dogfight!;
        ctx.Check(host.Shell is { Screen: OriginalScreen.Lobby, Lobby.Tab: LobbyTab.Scores }
                  && guest.Shell is { Screen: OriginalScreen.Lobby, Lobby.Tab: LobbyTab.Scores },
            $"both ends stand in the lobby on Game Scores ({host.Shell.Screen}/{host.Shell.Lobby.Tab}, {guest.Shell.Screen}/{guest.Shell.Lobby.Tab})");
        string board = string.Join(" ", here.Scores.Select(s => $"{s.Name}:{s.Points}/{s.Kills}K/{s.Deaths}D"));
        ctx.Check(here.Scores.Count == 2 && here.Scores.SequenceEqual(there.Scores)
                  && here.Scores[0] is { Points: 1, Kills: 2, Deaths: 1 } && here.Scores[0].Name == here.LaunchNames[1],
            $"and both show the match's scores, the guest's seat first by name ({board} | {string.Join(" ", there.Scores.Select(s => s.Name))})");
        ctx.Check(Row(host.Shell, OriginalLobbyScreen.ScoresTabKey) is { Enabled: true }, $"Game Scores is live once a match has landed");
        ctx.Check(!here.Ready && !there.Ready && here.Players.All(p => !p.Ready),
            $"every Ready is cleared for the next round ({string.Join(",", here.Players.Select(p => p.Ready))})");
        ctx.Check(guestExits.Count == launched && !guest.Door.DogfightLaunchDue,
            $"and the payload left over from the match launches the guest into nothing ({guestExits.Count - launched} exit(s))");
    }

    // The next round off the landed lobby, with nobody rejoining. Both mark Ready, and LAUNCH!
    // hands out a second launch the guest follows on its kept pick.
    private static void LaunchAgain(TestContext ctx, End host, End guest, List<End> ends, List<MenuExit> hostExits, List<MenuExit> guestExits)
    {
        ClickRow(ctx, host, OriginalLobbyScreen.ReadyKey);
        ClickRow(ctx, guest, OriginalLobbyScreen.ReadyKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        // LAUNCH! stands on the Mission Options page, as the host's first launch found it.
        ClickRow(ctx, host, OriginalLobbyScreen.MissionTabKey);
        ctx.Check(Row(host.Shell, OriginalLobbyScreen.LaunchKey) is { Enabled: true },
            $"both Ready again, LAUNCH! is live for a second round ({string.Join(",", host.Door.Dogfight!.Players.Select(p => p.Ready))})");
        int before = hostExits.Count;
        ClickRow(ctx, host, OriginalLobbyScreen.LaunchKey);
        var launch = hostExits.Skip(before).OfType<LaunchExit>().FirstOrDefault();
        ctx.Check(launch is { Mode: MenuMode.Versus, Net.IsHost: true } && launch.Chapter == DogfightLobby.ChapterOf(3),
            $"and it hands out a second Versus launch on the same lobby ({launch?.Chapter})");
        if (launch?.Net is not { } wire)
        {
            return;
        }

        var planes = launch.Seats.Select(s => s.PlaneNode).ToList();
        var fits = launch.Seats.Select(s => s.Fit).ToList();
        var (roster, _) = CSVM.Session.Launcher.VersusLaunchField(wire.Transport, planes, fits, StockLoadouts.Load());
        GuestLaunch(ctx, wire, roster, guest, guestExits);
    }

    // The Built-in host's wire taken out and its field built off the guest's pick. The session
    // opener then launches the guest on the host's map and time in its own pick.
    private static void HostTheBuiltInLaunch(TestContext ctx, NetPlayFeature hostDoor, End guest, List<MenuExit> guestExits, string chapter)
    {
        var wire = hostDoor.BuildLaunch();
        if (wire == null)
        {
            ctx.Check(false, $"the Built-in host's door hands its wire out ({hostDoor.Stage})");
            return;
        }

        var planes = new[] { PlanePickerRoster.AirframeNode(0) };
        var fits = new LoadoutChoice?[] { null };
        var (roster, _) = CSVM.Session.Launcher.VersusLaunchField(wire.Transport, planes, fits, StockLoadouts.Load());
        ctx.Check(roster.Length == 2 && roster[1].PlaneNode == PlanePickerRoster.AirframeNode(2),
            $"the host's roster builds the guest's seat on its pick ({string.Join(", ", roster.Select(s => s.PlaneNode))})");
        int before = guestExits.Count;
        _ = NetSession.Host((NetLobby)wire.Transport, roster, 7UL);
        for (int frame = 0; frame < 4 && guestExits.Count == before; frame++)
        {
            Pump(guest);
        }

        var launch = guestExits.Skip(before).OfType<LaunchExit>().FirstOrDefault();
        ctx.Check(launch is { Mode: MenuMode.Versus, Net.IsHost: false, Match.TimeLimitMinutes: 5 }
                  && launch.Chapter == chapter && launch.Seats.Count == 1
                  && launch.Seats[0].PlaneNode == PlanePickerRoster.AirframeNode(2),
            $"the guest launches behind the Built-in host on its map and time in its own pick ({launch?.Chapter}, {launch?.Match}, {launch?.Seats.FirstOrDefault()?.PlaneNode})");
    }

    // Frames of a Built-in host's door stepped by hand, each followed by one guest frame.
    private static void Frames(NetPlayFeature hostDoor, End guest, int count)
    {
        for (int frame = 0; frame < count; frame++)
        {
            hostDoor.Step(Dt);
            Pump(guest);
        }
    }

    // Frames of typing on the focused box, one command each.
    private static void TypeInto(End end, params MenuCommands[] frames)
    {
        foreach (var frame in frames)
        {
            end.Seat.Enqueue(frame);
            end.Host.Tick(Dt);
        }
    }

    // A real socket's datagrams land on the wall clock, so the wait is on it rather than a count.
    private static void Settle(Action answer, LanSearch search, Func<bool> done, double seconds = 5.0)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed.TotalSeconds < seconds)
        {
            answer();
            search.Poll();
            if (done())
            {
                return;
            }

            System.Threading.Thread.Sleep(1);
        }
    }

    // One Original presentation over its own host, door and scripted seat, shown on the top level.
    // A launch it hands out is added to exits when given.
    private static End? Open(TestContext ctx, MenuLayout layout, NetPlayFeature door, List<End> ends, List<MenuExit>? exits = null)
    {
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, new MenuInput { Keyboard = true })
        {
            CampaignProfiles = CampaignAidProfiles.Store(seeded: true, progressed: true),
        });
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exit => exits?.Add(exit));
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot, netDoor: door);
        host.AddSeat(seat);
        host.Select(forceBuiltIn: false, cliOverride: "original");
        host.Show(MenuReturnDestination.TopLevel);
        var shell = (host.Active as OriginalPresentation)?.Shell;
        ctx.Check(shell is { Screen: OriginalScreen.TopLevel }, $"each end shows Original on the top level ({shell?.Screen})");
        if (shell == null)
        {
            host.Deactivate();
            return null;
        }

        var end = new End(host, seat, shell);
        ends.Add(end);
        return end;
    }

    // ABLE-TO-FAIL CONTROL. The door closes what it opened. A second press that left the socket,
    // the mapping or the LAN answer up leaves a machine reachable with no band saying so.
    private static void OpenAndCloseControl(TestContext ctx, End host, NetPlayFeature door, List<int> unmapped)
    {
        ctx.Check(host.Shell.Screen == OriginalScreen.CampaignCabin, $"the host is seated in the cabin ({host.Shell.Screen})");
        var plaque = Row(host.Shell, OriginalCampaignScreen.CoopDoorKey);
        ctx.Check(plaque is { Enabled: true, Label: CoopDoorText.HostCoopButton },
            $"the cabin carries a live {CoopDoorText.HostCoopButton} plaque ({plaque?.Label})");
        ctx.Check(!Draws(host.Shell.Compose(), "NETWORK OPEN"), $"and draws no band while the door is shut");
        ClickRow(ctx, host, OriginalCampaignScreen.CoopDoorKey);
        ctx.Check(door.IsCoopHost && door.Answering, $"HOST CO-OP opens the carrier as a campaign host answering the LAN ({door.Stage}, {door.Answering})");
        AwaitMapping(door);
        Pump(host);
        ctx.Check(Row(host.Shell, OriginalCampaignScreen.CoopDoorKey)?.Label == CoopDoorText.CloseNetworkButton
                  && Draws(host.Shell.Compose(), "NETWORK OPEN"),
            $"the plaque turns to {CoopDoorText.CloseNetworkButton} over the host's band");
        ClickRow(ctx, host, OriginalCampaignScreen.CoopDoorKey);
        ctx.Check(door.Stage == NetDoorStage.Shut && !door.Answering && unmapped.Count == 1,
            $"ABLE-TO-FAIL CONTROL: CLOSE NETWORK closes the carrier, the LAN answer and the mapping ({door.Stage}, {door.Answering}, {unmapped.Count} unmapped)");
    }

    // The open the match stands on: the advert names the cabin's next mission under the profile.
    private static void OpenForTheMatch(TestContext ctx, End host, NetPlayFeature door)
    {
        ClickRow(ctx, host, OriginalCampaignScreen.CoopDoorKey);
        AwaitMapping(door);
        Pump(host);
        var advert = door.Advertising;
        ctx.Check(advert is { Kind: NetSessionKind.CampaignCoop, Host: CampaignAidProfiles.Pilot, Players: 1 }
                  && advert.Value.MissionSeq == CampaignAidProfiles.MissionsFlown,
            $"the advert names a campaign, the profile, its next mission and one player ({advert?.Kind}, {advert?.Host}, {advert?.MissionSeq}, {advert?.Players})");
    }

    // A guest's walk: the plaque, the Connection page, Connect over LAN TCP/IP, the one row, Join.
    private static void JoinThroughTheList(TestContext ctx, End guest, List<End> ends, int players, string who)
    {
        var shell = guest.Shell;
        var plaque = Row(shell, OriginalShell.MultiplayerKey);
        ctx.Check(plaque is { Enabled: true }, $"{who}'s Multiplayer plaque is live over the network door");
        ClickRow(ctx, guest, OriginalShell.MultiplayerKey);
        ctx.Check(shell.Screen == OriginalScreen.Connection && shell.Connection.Way == OriginalConnectionScreen.LanKey,
            $"a click on it opens the Connection page on LAN TCP/IP ({shell.Screen}, {shell.Connection.Way})");
        ClickRow(ctx, guest, OriginalConnectionScreen.ConnectKey);
        ctx.Check(shell.Screen == OriginalScreen.ConnectionGames && Row(shell, OriginalConnectionScreen.CancelKey) != null,
            $"Connect opens the games list behind the Searching box ({shell.Screen})");
        for (int frame = 0; frame < 6 && shell.Connection.Listed.Count == 0; frame++)
        {
            Pump(ends.ToArray());
        }

        var listed = shell.Connection.Listed;
        ctx.Check(listed.Count == 1, $"the search lists the one open game ({listed.Count})");
        if (listed.Count != 1)
        {
            return;
        }

        var cells = shell.Connection.Cells(listed[0]);
        ctx.Check(
            cells.Count == 5 && cells[0] == $"{CampaignAidProfiles.Pilot}'s campaign" && cells[1] == $"{players}/4"
            && cells[2] == "Campaign co-op" && cells[3].Length > 0 && cells[4] == "Waiting",
            $"its row reads five columns ({string.Join(" | ", cells)})");
        ctx.Check(shell.FocusedKey == OriginalConnectionScreen.GameKey(0), $"the cursor lands on the first game ({shell.FocusedKey})");
        ctx.Check(Row(shell, OriginalConnectionScreen.JoinKey) is { Enabled: false }, $"Join Game is greyed until a row is picked");
        ClickRow(ctx, guest, OriginalConnectionScreen.GameKey(0));
        ctx.Check(Row(shell, OriginalConnectionScreen.JoinKey) is { Enabled: true }, $"and live once the row is picked");
        ClickRow(ctx, guest, OriginalConnectionScreen.JoinKey);
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        var door = guest.Door;
        ctx.Check(door.IsCoopGuest && door.Advert?.Host == CampaignAidProfiles.Pilot,
            $"Join Game lands {who} on the host's campaign ({door.Stage}, {door.Advert?.Host})");
        ctx.Check(shell.Dialog == null && shell.Screen == OriginalScreen.CampaignCabin && shell.Campaign.IsGuest,
            $"and stands it on the host's cabin as a guest ({shell.Screen}, {shell.Dialog?.Message})");
    }

    // Two plain doors take the last seat and knock past it: four humans fit, the fifth hears why.
    private static void FillTheGame(TestContext ctx, List<End> ends, NetPlayFeature hostDoor, NetPlayFeature fourth, NetPlayFeature fifth)
    {
        fourth.OpenJoin();
        Pump(ends.ToArray());
        fourth.Step(Dt);
        fifth.OpenJoin();
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
            fourth.Step(Dt);
            fifth.Step(Dt);
        }

        ctx.Check(fourth.Stage == NetDoorStage.Joined && hostDoor.Peers == 3,
            $"a fourth human takes the last seat ({fourth.Stage}, {hostDoor.Peers} guests)");
        ctx.Check(hostDoor.Advertising is { Status: NetSessionStatus.Full, Players: 4 },
            $"and the advert reads full ({hostDoor.Advertising?.Status}, {hostDoor.Advertising?.Players})");
        ctx.Check(fifth.Stage == NetDoorStage.Failed && fifth.Fault == CoopDoorText.GameFull,
            $"a fifth human is refused as full ({fifth.Stage}, {fifth.Fault})");
    }

    // A link cut with no close notice reads as the host leaving. The page takes the guest back to
    // Connection under that word.
    private static void DropOne(TestContext ctx, End guest, INetTransport hostWire, int guestPeer)
    {
        hostWire.Disconnect(guestPeer);
        Pump(guest);
        ctx.Check(guest.Door.Fault == CoopDoorText.HostLeft && guest.Shell.Dialog?.Message == CoopDoorText.HostLeft,
            $"a silent drop tells the guest the host left ({guest.Door.Fault}, {guest.Shell.Dialog?.Message})");
        ctx.Check(guest.Shell.Screen == OriginalScreen.Connection, $"over the Connection page ({guest.Shell.Screen})");
    }

    // CLOSE NETWORK: the host's notice reaches the guest before its link goes, and OK hangs up.
    private static void CloseTheGame(TestContext ctx, End host, End guest, List<End> ends, NetPlayFeature hostDoor, List<int> unmapped)
    {
        ClickRow(ctx, host, OriginalCampaignScreen.CoopDoorKey);
        ctx.Check(hostDoor.Stage == NetDoorStage.Shut && unmapped.Count == 2,
            $"CLOSE NETWORK shuts the host and gives the port back ({hostDoor.Stage}, {unmapped.Count} unmapped)");
        for (int frame = 0; frame < 4; frame++)
        {
            Pump(ends.ToArray());
        }

        ctx.Check(guest.Door.Fault == CoopDoorText.HostClosed && guest.Shell.Dialog?.Message == CoopDoorText.HostClosed,
            $"the guest hears the host closed the game ({guest.Door.Fault}, {guest.Shell.Dialog?.Message})");
        ctx.Check(guest.Shell.Screen == OriginalScreen.Connection, $"over the Connection page ({guest.Shell.Screen})");
        ClickRow(ctx, guest, OriginalShell.DialogOkKey);
        ctx.Check(guest.Shell.Dialog == null && guest.Door.Stage == NetDoorStage.Shut,
            $"OK takes the box down and hangs up ({guest.Shell.Dialog?.Message}, {guest.Door.Stage})");
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

    // One idle frame on each end, in order: the frame is what steps each end's door.
    private static void Pump(params End[] ends)
    {
        foreach (var end in ends)
        {
            end.Host.Tick(Dt);
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

    private static bool Draws(ComposedBoard board, string text) =>
        board.Lines.Any(line => line.Text.Contains(text, StringComparison.Ordinal));

    // One end of the wire: its menu host, the seat the suite drives, and the shell it shows.
    private sealed record End(MenuHost Host, ScriptedSeat Seat, OriginalShell Shell)
    {
        public NetPlayFeature Door => Host.Features.Get<NetPlayFeature>();
    }

    // The host's end of a mesh whose guests arrive one at a time. A peer joins its roster, and its
    // payloads cross, only once that peer has arrived.
    private sealed class ArrivalGate : INetTransport, INetTransportListener
    {
        private readonly INetTransport _inner;
        private readonly HashSet<int> _arrived = new();
        private INetTransportListener? _listener;

        public ArrivalGate(INetTransport inner) => _inner = inner;

        public int LocalPeer => _inner.LocalPeer;

        public IReadOnlyList<int> Peers => _inner.Peers.Where(_arrived.Contains).ToList();

        public void Arrive(int peer)
        {
            if (_arrived.Add(peer) && _inner.Peers.Contains(peer))
            {
                _listener?.OnPeerConnected(peer);
            }
        }

        public void Bind(INetTransportListener listener)
        {
            _listener = listener;
            _inner.Bind(this);
        }

        public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
        {
            if (_arrived.Contains(peer))
            {
                _inner.Send(peer, payload, reliability, channel);
            }
        }

        public void Disconnect(int peer) => _inner.Disconnect(peer);

        public void Step(double dt) => _inner.Step(dt);

        public void OnPeerConnected(int peer)
        {
            if (_arrived.Contains(peer))
            {
                _listener?.OnPeerConnected(peer);
            }
        }

        public void OnPeerDisconnected(int peer)
        {
            if (_arrived.Contains(peer))
            {
                _listener?.OnPeerDisconnected(peer);
            }
        }

        public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload)
        {
            if (_arrived.Contains(peer))
            {
                _listener?.OnPayload(peer, channel, payload);
            }
        }
    }

    // A guest's loopback end that hangs up when its door closes, as a real socket does. The
    // loopback itself outlives a close, so a host would never hear the guest leave.
    private sealed class Hangup : INetTransport, IDisposable
    {
        private readonly INetTransport _inner;

        public Hangup(INetTransport inner) => _inner = inner;

        public int LocalPeer => _inner.LocalPeer;

        public IReadOnlyList<int> Peers => _inner.Peers;

        public void Bind(INetTransportListener listener) => _inner.Bind(listener);

        public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0) =>
            _inner.Send(peer, payload, reliability, channel);

        public void Disconnect(int peer) => _inner.Disconnect(peer);

        public void Step(double dt) => _inner.Step(dt);

        public void Dispose()
        {
            foreach (int peer in _inner.Peers.ToList())
            {
                _inner.Disconnect(peer);
            }
        }
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
