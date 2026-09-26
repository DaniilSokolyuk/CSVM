using System;
using System.Collections.Generic;
using System.Linq;
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
    private static End? Open(TestContext ctx, MenuLayout layout, NetPlayFeature door, List<End> ends)
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
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), _ => { });
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
