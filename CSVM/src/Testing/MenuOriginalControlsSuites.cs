using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// Original's rebinding pages through the presentation boundary over the install's decoded layout:
/// the Preferences page's CONTROLS door, the CONTROLS page's seat row and KEYS AND BUTTONS door,
/// the KEYS page's tabs and control cells, a capture armed on a cell and abandoned with Escape,
/// a second capture that binds, ACCEPT CHANGES writing the seat's own keymap file, and the list's
/// scrollbar over a category longer than its window.
/// </summary>
internal static class MenuOriginalControlsSuites
{
    private const float Dt = 1f / 60f;

    [Suite("menu-original-controls",
        "Original's rebinding pages through the presentation boundary over the install's decoded "
        + "layout: PREFERENCES opens the Options screen with its CONTROLS door live over the shared "
        + "feature, the door opens the decoded CONTROLS page whose seat row names player 1, whose "
        + "mouse row hands the mouse between the stick and head-look a press at a time over the "
        + "authored Mouse Sensitivity slider, and whose "
        + "KEYS AND BUTTONS button opens the decoded KEYS page on its Movement tab with the seven "
        + "category strips, the three column heads and CANCEL CHANGES authored left of ACCEPT "
        + "CHANGES, a click on a control cell arms a capture on that row's own action and slot, "
        + "Escape abandons it with the live keymap untouched, a second capture binds the key it is "
        + "given, ACCEPT CHANGES returns to the CONTROLS page and writes player 1's keymap file "
        + "carrying it, the scrollbar of a category longer than its window keeps its top and its "
        + "arrows on the window's first line while only the thumb walks the track, a tab press "
        + "stands another category, and CANCEL CHANGES on the CONTROLS page returns to the Options "
        + "screen")]
    internal static void MenuOriginalControls(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = System.IO.Path.Combine(ctx.ScratchDir, "menu-original-controls");
        System.IO.Directory.CreateDirectory(dir);
        // ⚠ Before the host: the save below writes through the store, and without the override it
        // would land on the keymap saved at this machine's controls.
        string? previous = BindingStore.DirectoryOverride;
        BindingStore.DirectoryOverride = dir;
        var written = new List<int>();
        var exits = new List<MenuExit>();
        var seat = new ScriptedSeat();
        var player1 = new MenuInput { Keyboard = true };
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, player1));
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot, (player, profile) =>
        {
            written.Add(player);
            BindingStore.UserBindings().Save(player, profile);
        });
        host.AddSeat(seat);
        try
        {
            host.Select(forceBuiltIn: false, cliOverride: "original");
            host.Show(MenuReturnDestination.TopLevel);
            var shell = (host.Active as OriginalPresentation)?.Shell;
            ctx.Check(shell != null, $"Show activates Original ({host.Active?.Id})");
            if (shell == null)
            {
                return;
            }

            var size = ctx.Host.GetViewport().GetVisibleRect().Size;
            var fit = BoardFit.For(size.X, size.Y);
            var controls = host.Features.Get<ControlsFeature>();
            Doors(ctx, host, seat, shell, fit, controls);
            Capture(ctx, host, seat, shell, fit, controls, written);
            Scrollbar(ctx, host, seat, shell, fit);
            Leave(ctx, host, seat, shell, fit, exits);
        }
        finally
        {
            host.Deactivate();
            BindingStore.DirectoryOverride = previous;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    // The three doors: the top level's PREFERENCES, the Options screen's CONTROLS and the CONTROLS
    // page's KEYS AND BUTTONS, each over the shared feature rather than a page-local copy.
    private static void Doors(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit, ControlsFeature controls)
    {
        Click(host, seat, shell, fit, "MM_B_PREFERENCES");
        ctx.Check(shell.Screen == OriginalScreen.Options, $"PREFERENCES opens the Options screen ({shell.Screen})");
        var door = Row(shell, OriginalOptionsScreen.ControlsDoorKey);
        ctx.Check(door is { Enabled: true }, $"its CONTROLS door is live over the shared feature ({door?.Enabled})");

        Click(host, seat, shell, fit, OriginalOptionsScreen.ControlsDoorKey);
        ctx.Check(shell.Screen == OriginalScreen.ControlsPrefs, $"the door opens the CONTROLS page ({shell.Screen})");
        ctx.Check(controls.Players.Count == 1 && controls.Player == 1,
            $"with one seat registered on it ({controls.Players.Count} seats, player {controls.Player})");
        ctx.Check(Row(shell, OriginalOptionsScreen.ControlsPlayerKey)?.Label == "Player 1",
            $"and the seat row naming player 1 ({Row(shell, OriginalOptionsScreen.ControlsPlayerKey)?.Label})");
        var mouse = Row(shell, OriginalOptionsScreen.ControlsMouseKey);
        ctx.Check(mouse is { Enabled: true, Label: "Look" },
            $"the Mouse panel's title line carries the flying scheme, on head-look ({mouse?.Label}, live {mouse?.Enabled})");
        var slider = Row(shell, OriginalOptionsScreen.ControlsSensitivityKey);
        ctx.Check(slider is { Enabled: true, Kind: OriginalRowKind.Slider, Slider.Value: 50 } && mouse != null && mouse.Y + mouse.Height <= slider.Y,
            $"and the authored Mouse Sensitivity slider stands under it, live at the default's middle level ({slider?.Kind}, {slider?.Slider?.Value})");
        Click(host, seat, shell, fit, OriginalOptionsScreen.ControlsMouseKey);
        ctx.Check(controls.MouseFlying && Row(shell, OriginalOptionsScreen.ControlsMouseKey)?.Label == "Fly",
            $"a press hands the mouse to the stick ({controls.MouseFlying}, {Row(shell, OriginalOptionsScreen.ControlsMouseKey)?.Label})");
        Click(host, seat, shell, fit, OriginalOptionsScreen.ControlsMouseKey);
        ctx.Check(!controls.MouseFlying, $"and the next press hands it back ({controls.MouseFlying})");

        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysDoorKey);
        ctx.Check(shell.Screen == OriginalScreen.Keys, $"KEYS AND BUTTONS opens the KEYS page ({shell.Screen})");
        ctx.Check(shell.Options.KeysTab == 0, $"on its first category ({OriginalOptionsScreen.ControlTabs[shell.Options.KeysTab].Name})");
        int tabs = 0;
        for (int i = 0; i < OriginalOptionsScreen.KeysTabCount; i++)
        {
            if (Row(shell, OriginalOptionsScreen.KeysTabKey(i)) is { Enabled: true })
            {
                tabs++;
            }
        }

        ctx.Check(tabs == OriginalOptionsScreen.KeysTabCount, $"the seven category strips all stand ({tabs})");
        var cancel = Row(shell, OriginalOptionsScreen.KeysCancelKey);
        var accept = Row(shell, OriginalOptionsScreen.KeysAcceptKey);
        ctx.Check(cancel != null && accept != null && cancel.X < accept.X && cancel.Y == accept.Y,
            $"CANCEL CHANGES is authored left of ACCEPT CHANGES on one line ({cancel?.X} vs {accept?.X})");
        var board = shell.Compose();
        int heads = 0;
        foreach (var line in board.Lines)
        {
            if (line.Text is "Action" or "Control A" or "Control B")
            {
                heads++;
            }
        }

        ctx.Check(heads == 3, $"the page draws its three authored column heads ({heads})");
    }

    // A capture armed on one cell: Escape abandons it, a second one binds, and ACCEPT CHANGES is
    // what reaches the keymap file.
    private static void Capture(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        ControlsFeature controls, List<int> written)
    {
        // The seat again, with scripted hardware behind it: the presentation's own registration
        // reads the real keyboard, which a scripted run never presses. The bookkeeping's list is
        // untouched, so its per-frame sync leaves this registration standing.
        var devices = new ScriptedCaptureDevices();
        controls.AddSeat(1, ShippedProfile(), devices, readsKeyboard: true);
        var action = OriginalOptionsScreen.ControlTabs[0].Rows[0].Action;
        string cell = OriginalOptionsScreen.KeysCellKey(0, second: false);

        Click(host, seat, shell, fit, cell);
        ctx.Check(controls.Capturing && controls.Focused == action && controls.Slot == 0,
            $"a click on the first Control A cell arms a capture on that row's own action and slot ({controls.Capturing}, {controls.Focused}, slot {controls.Slot})");

        devices.Flight.Keys.Add((int)Godot.Key.Escape);
        Press(host, seat, MenuCommands.None);
        ctx.Check(!controls.Capturing && controls.Status == "Cancelled.",
            $"Escape abandons the capture ({controls.Capturing}, \"{controls.Status}\")");
        ctx.Check(!HoldsKey(controls.Bindings(InputContext.Flight, action), Godot.Key.M),
            $"and nothing was bound to {BindingLabels.Name(action)}");

        devices.Flight.Keys.Clear();
        Click(host, seat, shell, fit, cell);
        devices.Flight.Keys.Add((int)Godot.Key.M);
        Press(host, seat, MenuCommands.None);
        ctx.Check(!controls.Capturing && HoldsKey(controls.Bindings(InputContext.Flight, action), Godot.Key.M),
            $"a second capture binds the key it is given ({controls.Status})");
        ctx.Check(written.Count == 0, $"with nothing written yet ({written.Count} saves)");

        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysAcceptKey);
        ctx.Check(shell.Screen == OriginalScreen.ControlsPrefs,
            $"ACCEPT CHANGES returns to the CONTROLS page ({shell.Screen})");
        ctx.Check(written.Count == 1 && written[0] == 1,
            $"and writes player 1's keymap and nobody else's ([{string.Join(", ", written)}])");
        var saved = BindingStore.UserBindings().Load(1, MenuControlsSeats.PadOf(InputContext.Flight), readsKeyboard: true);
        ctx.Check(HoldsKey(saved.Map(InputContext.Flight).Bindings(action), Godot.Key.M),
            $"the saved keymap read back carries the rebind on {BindingLabels.Name(action)}");
    }

    // The list's scrollbar over the one category longer than its window: the bar spans the window,
    // so its top and its arrows hold still while the list scrolls under them and the thumb alone
    // walks the track. Ends back on the CONTROLS page, which is where it was entered from.
    private static void Scrollbar(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit)
    {
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysDoorKey);
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysTabKey(OriginalOptionsScreen.KeysTabCount - 1));
        if (KeysWindow(shell) is not { } head)
        {
            ctx.Check(false, $"the last category's list is longer than its window and carries a bar");
            return;
        }

        ctx.Check(head.Count > head.Rows,
            $"the last category is longer than its window ({head.Count} rows over {head.Rows})");
        float arrowHeight = head.TrackTop - head.Y;
        ctx.Check(Same(head.Y, Row(shell, OriginalOptionsScreen.KeysCellKey(0, second: false))?.Y ?? -1f),
            $"its bar stands on the window's first line ({head.Y})");

        Wheel(host, seat, fit, head, head.Count);
        ctx.Check(shell.Options.KeysTop == head.LastTop,
            $"a wheel step scrolls the list to its last window (top {shell.Options.KeysTop} of {head.LastTop})");
        if (KeysWindow(shell) is not { } end)
        {
            ctx.Check(false, $"the scrolled list still carries its bar");
            return;
        }

        ctx.Check(Same(end.Y, head.Y) && Same(end.TrackTop, head.TrackTop),
            $"the bar's top and its track stay put over the scrolled list ({end.Y} was {head.Y})");
        ctx.Check(Same(end.Y, Row(shell, OriginalOptionsScreen.KeysCellKey(shell.Options.KeysTop, second: false))?.Y ?? -1f),
            $"which is still the window's first line, now the tab's row {shell.Options.KeysTop}");
        ctx.Check(end.ThumbY > head.ThumbY
            && Same(end.ThumbY, end.TrackTop + end.TrackHeight - end.ThumbHeight),
            $"and the thumb alone walks the track, flush at its foot ({head.ThumbY} to {end.ThumbY})");
        var tops = BarTops(shell.Compose(), end.ThumbX);
        ctx.Check(tops.Count >= 2 && Same(Lowest(tops), end.Y) && Holds(tops, end.ThumbY)
            && Highest(tops) <= end.Y + end.Height - arrowHeight + 0.5f,
            $"the drawn bar's marks all stand inside the window, its head on that line ({tops.Count} marks)");
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysCancelKey);
    }

    // The way out: a tab press stands another category, and the CONTROLS page's CANCEL CHANGES
    // returns to the Options screen without an exit.
    private static void Leave(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit, List<MenuExit> exits)
    {
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysDoorKey);
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysTabKey(2));
        ctx.Check(shell.Options.KeysTab == 2, $"a tab press stands its category ({OriginalOptionsScreen.ControlTabs[shell.Options.KeysTab].Name})");
        Click(host, seat, shell, fit, OriginalOptionsScreen.KeysCancelKey);
        ctx.Check(shell.Screen == OriginalScreen.ControlsPrefs, $"CANCEL CHANGES leaves the KEYS page ({shell.Screen})");
        Click(host, seat, shell, fit, OriginalOptionsScreen.ControlsCancelKey);
        ctx.Check(shell.Screen == OriginalScreen.Options, $"and the CONTROLS page's own returns to Options ({shell.Screen})");
        ctx.Check(exits.Count == 0, $"neither page leaves through an exit ({exits.Count})");
    }

    private static bool HoldsKey(IReadOnlyList<Binding> bindings, Godot.Key key)
    {
        foreach (var binding in bindings)
        {
            if (binding.Control.Kind == ControlKind.Key && binding.Control.Index == (int)key)
            {
                return true;
            }
        }

        return false;
    }

    // One seat's three shipped keymaps, each on the identity its own polling site reads.
    private static BindingProfile ShippedProfile()
    {
        var maps = new Dictionary<InputContext, ActionMap>();
        foreach (var context in Enum.GetValues<InputContext>())
        {
            maps[context] = DefaultBindings.MapFor(context, MenuControlsSeats.PadOf(context));
        }

        return new BindingProfile(maps, readsKeyboard: true);
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

    // The KEYS page's action list as the pointer sees it, or null while the standing tab fits its
    // window and the page draws no bar.
    private static ListWindow? KeysWindow(OriginalShell shell)
    {
        foreach (var list in shell.Lists)
        {
            if (list.Key == OriginalOptionsScreen.KeysListKey)
            {
                return list.Window;
            }
        }

        return null;
    }

    // Every picture or fill standing in the scrollbar's own column, by its top, so the arrows and
    // the thumb are read wherever the page's art puts them.
    private static List<float> BarTops(ComposedBoard board, float barX)
    {
        var tops = new List<float>();
        foreach (var picture in board.Pictures)
        {
            if (Same(picture.X, barX))
            {
                tops.Add(picture.Y);
            }
        }

        foreach (var fill in board.Fills)
        {
            if (Same(fill.X, barX))
            {
                tops.Add(fill.Y);
            }
        }

        return tops;
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) < 0.5f;

    private static bool Holds(List<float> tops, float y)
    {
        foreach (float top in tops)
        {
            if (Same(top, y))
            {
                return true;
            }
        }

        return false;
    }

    private static float Lowest(List<float> tops)
    {
        float y = float.MaxValue;
        foreach (float top in tops)
        {
            y = Math.Min(y, top);
        }

        return y;
    }

    private static float Highest(List<float> tops)
    {
        float y = float.MinValue;
        foreach (float top in tops)
        {
            y = Math.Max(y, top);
        }

        return y;
    }

    // A wheel of that many rows at the middle of a list's window, in window pixels.
    private static void Wheel(MenuHost host, ScriptedSeat seat, BoardFit fit, ListWindow window, int steps)
    {
        float x = fit.X(window.X + (window.Width / 2f));
        float y = fit.Y(window.Y + (window.Height / 2f));
        Press(host, seat, new MenuCommands { Pointer = new MenuPointer(x, y, false, false, steps) });
    }

    // One click on a row by key: the press arms it and the release still on it fires, so a click is
    // two frames rather than one.
    private static void Click(MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit, string key)
    {
        if (Row(shell, key) is not { } row)
        {
            return;
        }

        float x = fit.X(row.X + (row.Width / 2f));
        float y = fit.Y(row.Y + (row.Height / 2f));
        Press(host, seat, new MenuCommands { Pointer = new MenuPointer(x, y, true, true) });
        Press(host, seat, new MenuCommands { Pointer = new MenuPointer(x, y, false, false) });
    }

    private static void Press(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        seat.Enqueue(frame);
        host.Tick(Dt);
    }

    // A seat whose frames a suite writes, and which reads nothing of its own.
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

    // The seat's capture readers with nothing behind them but what this suite puts there, one per
    // context on the identity that context's rows sit on.
    private sealed class ScriptedCaptureDevices : ICaptureDevices
    {
        private readonly Dictionary<InputContext, ScriptedDevices> _states = new();

        public ScriptedCaptureDevices()
        {
            foreach (var context in Enum.GetValues<InputContext>())
            {
                _states[context] = new ScriptedDevices(MenuControlsSeats.PadOf(context));
            }
        }

        public ScriptedDevices Flight => _states[InputContext.Flight];

        public DeviceId PadOf(InputContext context) => MenuControlsSeats.PadOf(context);

        public IDeviceState For(InputContext context) => _states[context];
    }

    // One frame of scripted hardware: the keys and buttons the suite says are held.
    private sealed class ScriptedDevices : IDeviceState
    {
        private readonly DeviceId _pad;

        public ScriptedDevices(DeviceId pad) => _pad = pad;

        public HashSet<int> Keys { get; } = new();

        public HashSet<int> Buttons { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) => device == _pad && Buttons.Contains(button);

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) => 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
