using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Net;
using CSVM.UI;
using CSVM.UI.Menu;

namespace CSVM.Testing;

/// <summary>
/// Built-in's multiplayer door, driven as a player drives it. The Mode screen's last row opens
/// the board, and the board's five rows edit the port and the address and open a real socket.
/// The status line under them says what the socket is doing. Continue walks on to the Dogfight
/// map screen with one pilot seated, and backing out hangs up. The carrier is the shipped one
/// bound to the loopback address, so nothing here reaches a network or a firewall.
/// </summary>
internal static class MenuNetPlaySuites
{
    // How far the board's port is walked when the shipped default is taken by another run. Every
    // Right on the port row is one step, so the walk below is the board's own gesture.
    private const int PortsToTry = 24;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Right = new() { MoveX = 1 };

    [Suite("menu-net-door",
        "Built-in's multiplayer door driven as a player drives it: the Mode screen's last row "
        + "opens a five-row board, the port row steps, Host opens a real ENet socket on the "
        + "loopback address and the status line reports it, Continue walks on to the Dogfight map "
        + "screen with one pilot seated, and Back off the board hangs up")]
    internal static void TheMultiplayerDoor(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var exits = new List<MenuExit>();
        var host = MenuSuiteHost.Bare(exits, ctx.DataRoot, out var seat);
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        var door = host.Features.Get<NetPlayFeature>();
        ctx.Host.AddChild(menu);
        try
        {
            menu.ShowMenu();
            OpenBoard(ctx, menu);
            int port = HostAMatch(ctx, menu, door);
            if (port == 0)
            {
                return;
            }

            Continue(ctx, menu);
            HangUp(ctx, menu, door);
        }
        finally
        {
            door.Discard();
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
        }
    }

    // The Mode screen's last row, and what the board looks like before anything is open.
    private static void OpenBoard(TestContext ctx, LaunchMenu menu)
    {
        menu.Drive(Up);
        ctx.Check(menu.ShownRowText == LaunchMenu.NetworkRow,
            $"the Mode screen's last row is the multiplayer door ({menu.ShownRowText})");
        ctx.Check(menu.ShownDetail == "Host a Dogfight over the network, or join one by address.",
            $"and its description says what it is for ({menu.ShownDetail})");

        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Network" && menu.ShownRowCount == 5,
            $"Accept opens the board, five rows ({menu.ShownScreen}, {menu.ShownRowCount})");
        ctx.Check(menu.ShownHeading == "MULTIPLAYER"
                  && menu.ShownBreadcrumb == $"{LaunchMenu.NetworkRow}  ›  Map  ›  Aircraft",
            $"its heading and breadcrumb ({menu.ShownHeading}, {menu.ShownBreadcrumb})");
        ctx.Check(menu.ShownRow == 2 && menu.ShownRowText == "Host a match",
            $"a shut door opens under the cursor on Host ({menu.ShownRow}, {menu.ShownRowText})");
        ctx.Check(menu.ShownDetail.StartsWith("Host a match, or type an address", StringComparison.Ordinal),
            $"and the status line says nothing is open ({menu.ShownDetail})");
        ctx.Check(menu.ShownFooter.Contains("Type / Backspace  Address", StringComparison.Ordinal),
            $"the footer names the address field, whose letters the cursor keymap gives up ({menu.ShownFooter})");
    }

    // Opening the socket, and the port row that decides where. Returns the port that opened, or
    // 0 when every port tried was taken, which is a machine this suite cannot measure on.
    private static int HostAMatch(TestContext ctx, LaunchMenu menu, NetPlayFeature door)
    {
        // Up twice from Host is the port row, which is the board's own way to a free port.
        menu.Drive(Up);
        menu.Drive(Up);
        ctx.Check(menu.ShownRowText == $"Port            {NetPlayFeature.DefaultPort.ToString(CultureInfo.InvariantCulture)}",
            $"the first row is the port, on the door's own default ({menu.ShownRowText})");
        menu.Drive(Right);
        ctx.Check(door.Port == NetPlayFeature.DefaultPort + 1,
            $"and Right steps it ({door.Port.ToString(CultureInfo.InvariantCulture)})");

        for (int i = 0; i < PortsToTry; i++)
        {
            menu.Drive(Down);
            menu.Drive(Down);
            menu.Drive(Accept);
            if (door.Stage == NetDoorStage.Hosting)
            {
                break;
            }

            menu.Drive(Up);
            menu.Drive(Up);
            menu.Drive(Right);
        }

        if (door.Stage != NetDoorStage.Hosting)
        {
            ctx.Check(false, $"no port in {PortsToTry.ToString(CultureInfo.InvariantCulture)} tries would open: {door.Fault}");
            return 0;
        }

        string port = door.Port.ToString(CultureInfo.InvariantCulture);
        ctx.Check(door.IsHost && door.CanLaunch,
            $"Host opens a listen server on {port} and the launch gate with it");
        ctx.Check(menu.ShownRow == 4 && menu.ShownRowText == "Continue → Map",
            $"and the cursor moves on to the row that leaves ({menu.ShownRow}, {menu.ShownRowText})");
        ctx.Check(menu.ShownDetail.StartsWith($"Hosting on port {port}", StringComparison.Ordinal)
                  && menu.ShownDetail.Contains("link up", StringComparison.Ordinal)
                  && menu.ShownDetail.Contains("0 joined", StringComparison.Ordinal),
            $"the status line reports the port, the link and the field ({menu.ShownDetail})");

        // ABLE-TO-FAIL CONTROL. The fields belong to the player, not to the socket, so an open
        // door refuses to move the port under itself. A board that let this through would host
        // on one port and tell the player another. Down from the last row wraps onto the first.
        menu.Drive(Down);
        menu.Drive(Right);
        ctx.Check(door.Port.ToString(CultureInfo.InvariantCulture) == port,
            $"ABLE-TO-FAIL CONTROL: the port row will not move while the socket is open ({door.Port.ToString(CultureInfo.InvariantCulture)})");
        for (int i = 0; i < 4; i++)
        {
            menu.Drive(Down);
        }

        return door.Port;
    }

    // The way on: a network match is a Dogfight, so the mode is the door's to set and the map
    // screen is next. The local two-seat minimum does not apply, the opponent is elsewhere.
    private static void Continue(TestContext ctx, LaunchMenu menu)
    {
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownHeading == "SELECT MAP AND MATCH RULES",
            $"Continue walks on to the Dogfight map screen ({menu.ShownScreen}, {menu.ShownHeading})");
        ctx.Check(menu.ShownBreadcrumb.StartsWith("Dogfight", StringComparison.Ordinal),
            $"and the breadcrumb names the mode the wire flies ({menu.ShownBreadcrumb})");

        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Plane",
            $"the map leads to aircraft select as any Dogfight does ({menu.ShownScreen})");
        ctx.Check(!menu.ShownJoinHint.Contains("needs a fight", StringComparison.Ordinal),
            $"which no longer asks for a second local pilot ({menu.ShownJoinHint})");
        menu.Drive(Back);
        menu.Drive(Back);
    }

    // Backing off the board closes the socket. A listening socket behind an abandoned screen is
    // the one outcome a player cannot see and cannot undo anywhere else.
    private static void HangUp(TestContext ctx, LaunchMenu menu, NetPlayFeature door)
    {
        ctx.Check(menu.ShownScreen == "Network" && door.Stage == NetDoorStage.Hosting,
            $"back at the board with the socket still open ({menu.ShownScreen}, {door.Stage})");
        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Mode" && door.Stage == NetDoorStage.Shut,
            $"Back hangs up and returns to the Mode screen ({menu.ShownScreen}, {door.Stage})");
    }
}
