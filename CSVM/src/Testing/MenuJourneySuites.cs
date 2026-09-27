using System.Collections.Generic;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// Built-in's Free Flight journey, characterized. A real <see cref="LaunchMenu"/> is driven from
/// the Mode screen through Chapter and Aircraft to the typed exit, backed out of at every step,
/// re-entered the way a return from flight re-enters it, and opened through the aids the screens
/// carry. Every check pins what the screens do today, quirks included, so a change behind them
/// can be told from a change of behaviour.
/// </summary>
internal static class MenuJourneySuites
{
    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Loadout = new() { Loadout = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };

    [Suite("menu-free-flight-journey",
        "Built-in's Free Flight journey pinned end to end: a real LaunchMenu is driven Mode to "
        + "Chapter to Aircraft to the typed launch exit and back out at every step, the payload is "
        + "read, a return from flight re-enters it, the --menu= aids open their screens, and a "
        + "second seat holds the gate; every check is what the screens do today")]
    internal static void MenuFreeFlightJourney(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var exits = new List<MenuExit>();
        var host = MenuSuiteHost.Bare(exits, ctx.DataRoot, out var seat);
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        ctx.Host.AddChild(menu);
        var launches = new Exits<LaunchExit>(exits);
        var quits = new Exits<QuitExit>(exits);
        try
        {
            TopLevel(ctx, menu);
            ChapterScreen(ctx, menu);
            AircraftScreen(ctx, menu, launches);
            Launch(ctx, menu, launches);
            Return(ctx, menu, launches);
            Aids(ctx, menu);
            Quit(ctx, menu, quits);
            SecondSeat(ctx, menu, launches);
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
        }
    }

    private static void TopLevel(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu();
        Is(ctx, "a cold start opens on the Mode screen", "Mode", menu.ShownScreen);
        Is(ctx, "its heading", "SELECT MODE", menu.ShownHeading);
        Is(ctx, "its breadcrumb", "Mode  ›  Map  ›  Aircraft", menu.ShownBreadcrumb);
        Is(ctx, "the cursor stands on the first row", "Free Flight", menu.ShownRowText);
        ctx.Check(menu.ShownRowCount == 7,
            $"the Mode screen has seven rows, the three modes and the four doors ({menu.ShownRowCount})");
        menu.Drive(Up);
        Is(ctx, "the last row is the multiplayer door", LaunchMenu.NetworkRow, menu.ShownRowText);
        menu.Drive(Up);
        Is(ctx, "and the one over it is Options", LaunchMenu.OptionsRow, menu.ShownRowText);
        menu.Drive(Down);
        menu.Drive(Down);
        Is(ctx, "Free Flight's description", "Explore the map freely, no objectives, no clock.", menu.ShownDetail);
        Has(ctx, "the top level's Back is Quit", "Esc / B  Quit", menu.ShownFooter);
        Is(ctx, "joining is not open here", "(other players join at aircraft select)", menu.ShownJoinHint);
    }

    private static void ChapterScreen(TestContext ctx, LaunchMenu menu)
    {
        menu.Drive(Accept);
        Is(ctx, "Accept on Free Flight opens the Chapter screen", "Chapter", menu.ShownScreen);
        Is(ctx, "its heading", "SELECT MAP", menu.ShownHeading);
        Is(ctx, "its breadcrumb names the mode", "Free Flight  ›  Map  ›  Aircraft", menu.ShownBreadcrumb);
        ctx.Check(menu.ShownRowCount == 8, $"Free Flight offers all eight chapters ({menu.ShownRowCount})");
        Is(ctx, "the first row", "Sea Haven (night), IA: an airfield", menu.ShownRowText);
        Is(ctx, "its description is the region code", "Region C1", menu.ShownDetail);
        Has(ctx, "Back here is Back, not Quit", "Esc / B  Back", menu.ShownFooter);

        menu.Drive(Up);
        ctx.Check(menu.ShownRow == 7 && menu.ShownDetail == "Region C5",
            $"the cursor wraps from the first row to the last (row {menu.ShownRow}, {menu.ShownDetail})");
        menu.Drive(Down);
        menu.Drive(Down);
        ctx.Check(menu.ShownRow == 1 && menu.ShownDetail == "Region C1B",
            $"and wraps back down past the end (row {menu.ShownRow}, {menu.ShownDetail})");

        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRow == 0,
            $"Back returns to the Mode screen with its cursor where it was ({menu.ShownScreen}, row {menu.ShownRow})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownRow == 1,
            $"and the chapter cursor survives the trip out and back ({menu.ShownScreen}, row {menu.ShownRow})");

        menu.Drive(Up);
        menu.Drive(Up);
        Is(ctx, "the pick for the launch below", "New York, IA: Manhattan", menu.ShownRowText);
    }

    private static void AircraftScreen(TestContext ctx, LaunchMenu menu, Exits<LaunchExit> launches)
    {
        menu.Drive(Accept);
        Is(ctx, "Accept on a chapter opens the Aircraft screen", "Plane", menu.ShownScreen);
        Is(ctx, "its heading", "SELECT AIRCRAFT", menu.ShownHeading);
        Is(ctx, "its breadcrumb names the chapter", "Free Flight  ›  New York, IA: Manhattan  ›  Aircraft", menu.ShownBreadcrumb);
        Is(ctx, "the cursor opens on the roster's first airframe", "Autogyro", menu.ShownRowText);
        ctx.Check(menu.ShownRowCount >= 11, $"the roster holds the eleven stock airframes at least ({menu.ShownRowCount})");
        Has(ctx, "the focused airframe's stats line", "Top Speed", menu.ShownDetail);
        Has(ctx, "the footer offers the weapons list", "L / Y  Weapons", menu.ShownFooter);
        Has(ctx, "and a first Select", "Enter / A  Select", menu.ShownFooter);
        ctx.Check(menu.ShownJoinHint != "(other players join at aircraft select)",
            $"joining is open here, so the hint changes ({menu.ShownJoinHint})");

        menu.Drive(Down);
        menu.Drive(Down);
        Is(ctx, "two rows down", "Balmoral", menu.ShownRowText);

        menu.Drive(Accept);
        Is(ctx, "the first Accept selects the airframe", "AIRCRAFT SELECTED", menu.ShownHeading);
        Has(ctx, "and the footer now offers FLY", "Enter / A  FLY", menu.ShownFooter);
        ctx.Check(launches.Count == 0, $"nothing has launched on the first press ({launches.Count})");
        menu.Drive(Down);
        ctx.Check(menu.ShownRowText == "Balmoral", $"a selected airframe's cursor does not move ({menu.ShownRowText})");

        menu.Drive(Loadout);
        Is(ctx, "Weapons on a selected airframe opens its list", "AMMO SELECTION  (Balmoral)", menu.ShownHeading);
        Has(ctx, "with the list's own controls", "Choose mount", menu.ShownFooter);
        menu.Drive(Back);
        Is(ctx, "Back leaves the list with the selection standing", "AIRCRAFT SELECTED", menu.ShownHeading);

        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Plane" && menu.ShownHeading == "SELECT AIRCRAFT" && menu.ShownRowText == "Balmoral",
            $"Back on a selected airframe unselects it and keeps the cursor ({menu.ShownHeading}, {menu.ShownRowText})");
        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownRowText == "New York, IA: Manhattan",
            $"Back while browsing returns to the Chapter screen on the same chapter ({menu.ShownScreen}, {menu.ShownRowText})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Plane" && menu.ShownRowText == "Balmoral",
            $"and the airframe cursor survives that trip too ({menu.ShownScreen}, {menu.ShownRowText})");
    }

    private static void Launch(TestContext ctx, LaunchMenu menu, Exits<LaunchExit> launches)
    {
        menu.Drive(Accept);
        menu.Drive(Accept);
        ctx.Check(launches.Count == 1, $"the second Accept on a selected airframe launches, once ({launches.Count})");
        if (launches.Count != 1)
        {
            return;
        }

        var launch = launches[0];
        Is(ctx, "the launch carries the picked chapter's code", "C5", launch.Chapter);
        ctx.Check(launch.Mode == MenuMode.Free, $"in mode Free ({launch.Mode})");
        ctx.Check(launch.InstantAction == null, $"with no Instant Action def");
        ctx.Check(launch.Seats.Count == 1, $"for the one joined seat ({launch.Seats.Count})");
        var seat = launch.Seats[0];
        Is(ctx, "flying the selected airframe's node", "player_balmoral", seat.PlaneNode);
        ctx.Check(seat.Fit == null, $"with the stock fit (null), since the list was left untouched");
        ctx.Check(seat.Custom == null, $"and no custom plane behind a stock pick");
        ctx.Check(seat.Pads.Count == 0, $"and no pad bound to a keyboard seat ({seat.Pads.Count})");
        ctx.Check(menu.ShownScreen == "Plane" && menu.Visible,
            $"the menu keeps its state and stays for the host to hide, so a failed build can come back ({menu.ShownScreen})");
    }

    private static void Return(TestContext ctx, LaunchMenu menu, Exits<LaunchExit> launches)
    {
        menu.HideMenu();
        menu.ShowMenu();
        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRow == 0,
            $"a bare launch's return re-enters on the Mode screen ({menu.ShownScreen}, row {menu.ShownRow})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownRowText == "New York, IA: Manhattan",
            $"the chapter cursor survives the flight ({menu.ShownRowText})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownHeading == "SELECT AIRCRAFT" && menu.ShownRowText == "Balmoral",
            $"the airframe cursor survives it and the selection does not ({menu.ShownHeading}, {menu.ShownRowText})");
        ctx.Check(launches.Count == 1, $"and nothing relaunched on the way back in ({launches.Count})");
    }

    private static void Aids(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu("chapter");
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownBreadcrumb.StartsWith("Free Flight", System.StringComparison.Ordinal),
            $"--menu=chapter opens the Chapter screen under the mode last picked ({menu.ShownBreadcrumb})");
        menu.ShowMenu("plane");
        ctx.Check(menu.ShownScreen == "Plane" && menu.ShownHeading == "SELECT AIRCRAFT",
            $"--menu=plane opens the Aircraft screen browsing ({menu.ShownHeading})");
        menu.ShowMenu("selected");
        Is(ctx, "--menu=selected opens it with the airframe selected", "AIRCRAFT SELECTED", menu.ShownHeading);
        menu.ShowMenu("loadout");
        Has(ctx, "--menu=loadout opens its weapons list", "AMMO SELECTION", menu.ShownHeading);

        // The aids read whatever mode the Mode screen last picked, Dogfight included.
        menu.ShowMenu();
        menu.Drive(Down);
        menu.Drive(Down);
        Is(ctx, "two rows down the Mode screen", "Dogfight", menu.ShownRowText);
        menu.Drive(Accept);
        Is(ctx, "Dogfight opens the same Chapter screen", "Dogfight  ›  Map  ›  Aircraft", menu.ShownBreadcrumb);
        menu.ShowMenu("chapter");
        ctx.Check(menu.ShownBreadcrumb.StartsWith("Dogfight", System.StringComparison.Ordinal),
            $"and --menu=chapter then re-enters under Dogfight, not Free Flight ({menu.ShownBreadcrumb})");
        menu.ShowMenu();
        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRowText == "Dogfight",
            $"the Mode cursor survives a re-entry too ({menu.ShownRowText})");
        menu.Drive(Up);
        menu.Drive(Up);
        menu.Drive(Accept);
        Is(ctx, "picking Free Flight again puts the Chapter screen back under it", "Free Flight  ›  Map  ›  Aircraft", menu.ShownBreadcrumb);
    }

    private static void Quit(TestContext ctx, LaunchMenu menu, Exits<QuitExit> quits)
    {
        menu.ShowMenu();
        menu.Drive(Back);
        ctx.Check(quits.Count == 1, $"Back on the Mode screen asks the host to quit, once ({quits.Count})");
        ctx.Check(menu.ShownScreen == "Mode", $"and leaves the screen standing for the host ({menu.ShownScreen})");
    }

    private static void SecondSeat(TestContext ctx, LaunchMenu menu, Exits<LaunchExit> launches)
    {
        menu.DebugJoin(1);
        menu.ShowMenu();
        menu.Drive(Accept);
        menu.Drive(Accept);
        Is(ctx, "two seats split the Aircraft screen", "SELECT AIRCRAFT: ALL PLAYERS", menu.ShownHeading);
        menu.Drive(Accept);
        menu.Drive(Accept);
        ctx.Check(launches.Count == 1,
            $"player 1 selecting and confirming alone does not launch while the second seat has not confirmed ({launches.Count})");
    }

    private static void Is(TestContext ctx, string what, string expected, string actual) =>
        ctx.Check(expected == actual, $"{what}: expected '{expected}', got '{actual}'");

    private static void Has(TestContext ctx, string what, string expected, string actual) =>
        ctx.Check(actual.Contains(expected, System.StringComparison.Ordinal),
            $"{what}: expected '{expected}' in '{actual}'");

    // The exits of one kind among everything the host's sink recorded, read live.
    private sealed class Exits<T>
        where T : MenuExit
    {
        private readonly List<MenuExit> _all;

        public Exits(List<MenuExit> all)
        {
            _all = all;
        }

        public int Count
        {
            get
            {
                int n = 0;
                foreach (var exit in _all)
                {
                    if (exit is T)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public T this[int index]
        {
            get
            {
                int seen = 0;
                foreach (var exit in _all)
                {
                    if (exit is T match && seen++ == index)
                    {
                        return match;
                    }
                }

                throw new System.ArgumentOutOfRangeException(nameof(index));
            }
        }
    }
}
