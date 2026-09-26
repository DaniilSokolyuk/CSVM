using System.Collections.Generic;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// The Free Flight tracer through the presentation boundary: a real <see cref="MenuHost"/> with
/// the Built-in presentation registered under its id, shown at the top level, ticked with frames
/// whose player-1 commands arrive through the host's first seat, and left through the typed exit
/// the host hands its sink. The host then hides the presentation and, as the launcher does after
/// a flight, shows it again at the top level; the screens must re-enter with the state
/// <c>menu-free-flight-journey</c> pins for a return. A presentation that only works on a cold
/// start has not proved the seam. The pointer suite drives the same rig with the mouse events
/// Godot dispatches to a row control, injected through the control's own signals.
/// </summary>
internal static class MenuHostSuites
{
    private const float Dt = 1f / 60f;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Down = new() { MoveY = 1 };

    [Suite("menu-host-pointer",
        "Built-in's launchscreen under the mouse, through the host's frame: a motion over a row "
        + "moves the cursor onto it, a press and release on one row confirms it in one frame, a "
        + "release after the pointer left the row confirms nothing, the right button goes back a "
        + "screen on its press alone and quits nothing at the top level, a wheel notch over the "
        + "aircraft list steps the cursor either way and wraps, a pad step in the same frame as a "
        + "hover keeps the pad's row, and a click off the locked airframe is refused")]
    internal static void MenuHostPointer(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var exits = new List<MenuExit>();
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot);
        host.AddSeat(seat);
        try
        {
            host.Show(MenuReturnDestination.TopLevel);
            if ((host.Active as BuiltInPresentation)?.Menu is not { } menu)
            {
                ctx.Check(false, $"the Built-in presentation stood its launchscreen up");
                return;
            }

            HoverAndClick(ctx, host, menu);
            RightBack(ctx, host, menu, exits);
            Wheel(ctx, host, seat, menu, exits);
        }
        finally
        {
            host.Deactivate();
        }
    }

    [Suite("menu-host-tracer",
        "Built-in Free Flight through the presentation boundary: the Built-in presentation is "
        + "registered and shown by a real MenuHost, player 1's commands arrive through the "
        + "host's first seat frame by frame, the launch leaves as a LaunchExit through the host's "
        + "sink with the presentation hidden, an unknown requested presentation falls back to "
        + "Built-in with a reason, and a top-level return re-enters the screens with the chapter "
        + "and airframe cursors kept and the selection dropped")]
    internal static void MenuHostTracer(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var exits = new List<MenuExit>();
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot);
        host.AddSeat(seat);
        BuiltInPresentation? built = null;
        try
        {
            Selection(ctx, host);
            var menu = ColdStart(ctx, host, out built);
            if (menu == null)
            {
                return;
            }

            Fly(ctx, host, seat, menu, exits);
            Return(ctx, host, menu, exits);
        }
        finally
        {
            host.Deactivate();
        }

        ctx.Check(built?.Menu == null, $"Deactivate tears the launchscreen down");
        ctx.Check(host.Active == null && !host.Shown, $"and the host holds no presentation afterwards");
    }

    // Resolution over the registry: an unknown request falls back with a reason rather than
    // crashing, the force flag beats a request, nothing asked for requests Original and lands on
    // Built-in with a reason in a registry without it, and a Built-in override resolves silently.
    private static void Selection(TestContext ctx, MenuHost host)
    {
        string? reason = host.Select(forceBuiltIn: false, cliOverride: "no-such-presentation");
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason != null,
            $"an unknown requested presentation falls back to Built-in with a reason ({reason})");
        ctx.Check(host.Requested == new PresentationId("no-such-presentation"),
            $"while the request itself is kept for the startup log to report ({host.Requested})");
        reason = host.Select(forceBuiltIn: true, cliOverride: "original");
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason != null,
            $"the force flag resolves to Built-in over a CLI override ({reason})");
        reason = host.Select(forceBuiltIn: false, cliOverride: null);
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason != null && host.Requested == PresentationId.Original,
            $"with nothing asked for the request is Original, which this registry lacks, so Built-in stands in with a reason ({reason})");
        reason = host.Select(forceBuiltIn: false, cliOverride: PresentationId.BuiltIn.Value);
        ctx.Check(host.Selected == PresentationId.BuiltIn && reason == null,
            $"and a --presentation=built-in override is the one request that resolves silently");
    }

    private static LaunchMenu? ColdStart(TestContext ctx, MenuHost host, out BuiltInPresentation? built)
    {
        host.Show(MenuReturnDestination.TopLevel);
        built = host.Active as BuiltInPresentation;
        ctx.Check(host.Shown && built != null,
            $"Show activates a fresh Built-in presentation from the registry ({host.Active?.Id})");
        var menu = built?.Menu;
        ctx.Check(menu is { Visible: true },
            $"and the presentation stood the launchscreen up under the host's parent, visible");
        if (menu == null)
        {
            return null;
        }

        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRowText == "Free Flight",
            $"a cold start opens on the Mode screen with the cursor on Free Flight ({menu.ShownScreen}, {menu.ShownRowText})");
        return menu;
    }

    // Mode, Chapter (New York), Aircraft (Balmoral), select, confirm: every press a frame through
    // the host's seat, none through the launchscreen's own Drive.
    private static void Fly(TestContext ctx, MenuHost host, ScriptedSeat seat, LaunchMenu menu, List<MenuExit> exits)
    {
        Press(host, seat, Accept);
        ctx.Check(menu.ShownScreen == "Chapter",
            $"a frame through the host's seat drives the screens: Accept opens the Chapter screen ({menu.ShownScreen})");
        Press(host, seat, Up);
        ctx.Check(menu.ShownRowText == "New York, IA: Manhattan",
            $"Up wraps the chapter cursor onto the last row ({menu.ShownRowText})");
        Press(host, seat, Accept);
        Press(host, seat, Down);
        Press(host, seat, Down);
        ctx.Check(menu.ShownScreen == "Plane" && menu.ShownRowText == "Balmoral",
            $"the Aircraft screen, two rows down ({menu.ShownScreen}, {menu.ShownRowText})");
        Press(host, seat, Accept);
        ctx.Check(exits.Count == 0 && menu.ShownHeading == "AIRCRAFT SELECTED",
            $"the first Accept selects and nothing has left yet ({exits.Count}, {menu.ShownHeading})");
        Press(host, seat, Accept);
        ctx.Check(exits.Count == 1 && exits[0] is LaunchExit,
            $"the second Accept leaves through the host as one LaunchExit ({exits.Count})");
        if (exits.Count == 1 && exits[0] is LaunchExit launch)
        {
            ctx.Check(launch.Chapter == "C5" && launch.Mode == MenuMode.Free && launch.InstantAction == null,
                $"carrying the picked chapter, mode Free and no Instant Action def ({launch.Chapter}, {launch.Mode})");
            ctx.Check(launch.Seats.Count == 1 && launch.Seats[0].PlaneNode == "player_balmoral",
                $"and the one seat's airframe ({launch.Seats.Count}, {launch.Seats[0].PlaneNode})");
        }

        ctx.Check(!host.Shown && !menu.Visible,
            $"the host hid the presentation on the exit, so the launcher can build (shown={host.Shown}, visible={menu.Visible})");
        seat.Enqueue(Accept);
        host.Tick(Dt);
        ctx.Check(exits.Count == 1, $"a frame while hidden reaches nothing, so nothing relaunches ({exits.Count})");
        seat.Clear();
    }

    // The launcher's return from flight: the same instance shown again at the top level.
    private static void Return(TestContext ctx, MenuHost host, LaunchMenu menu, List<MenuExit> exits)
    {
        var before = host.Active;
        host.Show(MenuReturnDestination.TopLevel);
        ctx.Check(ReferenceEquals(before, host.Active) && host.Shown && menu.Visible,
            $"a return shows the same presentation instance again, not a fresh one (shown={host.Shown}, visible={menu.Visible})");
        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRow == 0,
            $"a bare launch's return re-enters on the Mode screen ({menu.ShownScreen}, row {menu.ShownRow})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Chapter" && menu.ShownRowText == "New York, IA: Manhattan",
            $"the chapter cursor survives the flight ({menu.ShownRowText})");
        menu.Drive(Accept);
        ctx.Check(menu.ShownHeading == "SELECT AIRCRAFT" && menu.ShownRowText == "Balmoral",
            $"the airframe cursor survives it and the selection does not ({menu.ShownHeading}, {menu.ShownRowText})");
        ctx.Check(exits.Count == 1, $"and nothing relaunched on the way back in ({exits.Count})");
    }

    private static void Press(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        seat.Enqueue(frame);
        host.Tick(Dt);
    }

    // The Mode screen: a motion focuses, a click confirms, a drag off the row does not.
    private static void HoverAndClick(TestContext ctx, MenuHost host, LaunchMenu menu)
    {
        ctx.Check(menu.RowControl(0) != null && menu.RowControl(2) != null,
            $"the Mode screen's rows are individual controls a pointer can land on");
        Emit(menu.RowControl(2), new InputEventMouseMotion());
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 2 && menu.ShownRowText == "Dogfight",
            $"a motion over the third row moves the cursor onto it ({menu.ShownRow}, {menu.ShownRowText})");
        Emit(menu.RowControl(2), new InputEventMouseMotion());
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 2, $"a motion over the row the cursor already sits on leaves it there ({menu.ShownRow})");

        var row = menu.RowControl(0);
        Emit(row, Button(MouseButton.Left, pressed: true));
        row?.EmitSignal(Control.SignalName.MouseExited);
        Emit(row, Button(MouseButton.Left, pressed: false));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Mode" && menu.ShownRow == 2,
            $"a release after the pointer left the pressed row confirms nothing ({menu.ShownScreen}, row {menu.ShownRow})");

        row = menu.RowControl(0);
        Emit(row, Button(MouseButton.Left, pressed: true));
        Emit(row, Button(MouseButton.Left, pressed: false));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Chapter",
            $"a press and release on the Free Flight row steps the cursor there and confirms it in one frame ({menu.ShownScreen})");
    }

    // The right button, from the chapter list the click above reached: Back on a screen that has
    // one, inert on the Mode screen where Back is the quit, and nothing on a release of its own.
    // Leaves the cursor back on Chapter, which is where Wheel starts.
    private static void RightBack(TestContext ctx, MenuHost host, LaunchMenu menu, List<MenuExit> exits)
    {
        Emit(menu.RowControl(0), Button(MouseButton.Right, pressed: false));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Chapter",
            $"a right-button release with no press before it does nothing ({menu.ShownScreen})");

        RightClick(menu.RowControl(0));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Mode" && exits.Count == 0,
            $"a right click on the chapter list goes back a screen ({menu.ShownScreen}, {exits.Count} exit(s))");

        RightClick(menu.RowControl(0));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Mode" && exits.Count == 0,
            $"and a right click at the top level quits nothing ({menu.ShownScreen}, {exits.Count} exit(s))");

        Click(menu.RowControl(0));
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Chapter",
            $"the chapter list is back for the wheel checks ({menu.ShownScreen})");
    }

    // The aircraft list: the wheel steps and wraps, the pad outranks a hover in the same frame,
    // and a click selects, is refused off the locked row, and confirms on it.
    private static void Wheel(TestContext ctx, MenuHost host, ScriptedSeat seat, LaunchMenu menu, List<MenuExit> exits)
    {
        seat.Enqueue(Accept);
        host.Tick(Dt);
        ctx.Check(menu.ShownScreen == "Plane" && menu.ShownRow == 0,
            $"Accept through the seat reaches the aircraft list on its first row ({menu.ShownScreen}, {menu.ShownRow})");
        int rows = menu.ShownRowCount;
        Emit(menu.RowControl(0), Button(MouseButton.WheelDown, pressed: true));
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 1, $"a wheel notch down over the list steps the cursor down one ({menu.ShownRow})");
        Emit(menu.RowControl(1), Button(MouseButton.WheelUp, pressed: true));
        host.Tick(Dt);
        Emit(menu.RowControl(0), Button(MouseButton.WheelUp, pressed: true));
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == rows - 1,
            $"two notches up step back and wrap onto the last row, as the keys do ({menu.ShownRow} of {rows})");

        seat.Enqueue(Down);
        Emit(menu.RowControl(3), new InputEventMouseMotion());
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 0,
            $"a pad step and a hover in one frame leave the cursor on the pad's row ({menu.ShownRow})");

        Click(menu.RowControl(2));
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 2 && menu.ShownHeading == "AIRCRAFT SELECTED",
            $"a click on the third airframe selects it ({menu.ShownRow}, {menu.ShownHeading})");
        Click(menu.RowControl(4));
        host.Tick(Dt);
        ctx.Check(menu.ShownRow == 2 && exits.Count == 0 && menu.ShownHeading == "AIRCRAFT SELECTED",
            $"a click on another row while one is selected is refused ({menu.ShownRow}, {exits.Count})");
        Click(menu.RowControl(2));
        host.Tick(Dt);
        ctx.Check(exits.Count == 1 && exits[0] is LaunchExit,
            $"a second click on the selected airframe leaves as one LaunchExit ({exits.Count})");
    }

    private static void Click(Control? row)
    {
        Emit(row, Button(MouseButton.Left, pressed: true));
        Emit(row, Button(MouseButton.Left, pressed: false));
    }

    // Both halves, so a Back taken twice from one click would show up as a second screen step.
    private static void RightClick(Control? row)
    {
        Emit(row, Button(MouseButton.Right, pressed: true));
        Emit(row, Button(MouseButton.Right, pressed: false));
    }

    // Dispatched the way Godot's gui_input reaches a handler; a missing row is a failed check
    // upstream, so nothing is emitted rather than throwing.
    private static void Emit(Control? row, InputEvent ev) =>
        row?.EmitSignal(Control.SignalName.GuiInput, ev);

    private static InputEventMouseButton Button(MouseButton index, bool pressed) =>
        new() { ButtonIndex = index, Pressed = pressed };

    // Seat 0 fed from a queue of frames; an empty queue reads idle. What the host's seam carries
    // is the frame, so the devices behind a seat are nobody's business here.
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
