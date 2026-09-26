using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CSVM.Tooling;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// BL-489: the screenshot key on the menu screens. The key is an input, so the check pushes a real
/// <see cref="InputEventKey"/> through the real viewport with a real menu standing, rather than
/// calling the capture directly: what has to hold is that a press reaches the capture, and that the
/// menu is the thing that takes it. Both layouts are covered, since the launchscreen is built as
/// controls and the campaign screens are drawn through <c>ComposedBoardView</c>.
/// </summary>
internal static class MenuCaptureSuites
{
    // BL-489: the screenshot key reached the launcher only when no menu was up, so a menu
    // defect could be described but not shown.
    [Suite("menu-screenshot-key",
        "the screenshot key on the menu screens: a real key event is pushed through the real "
        + "viewport with a LaunchMenu standing, once on the launchscreen and once on a "
        + "campaign board, and each press must ask for the frame once, write nothing on the press "
        + "itself, and leave one more file in the folder the flight capture writes to once the "
        + "frame lands on a worker")]
    internal static void MenuScreenshotKey(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var written = new List<string>();
        var menu = MenuSuiteHost.Menu(ctx);
        ctx.Host.AddChild(menu);
        try
        {
            menu.ShowMenu();
            Press(ctx, menu, written, "the launchscreen");
            menu.ShowMenu("campaign-briefing:0");
            ctx.Check(menu.ShownBoard != null,
                $"the campaign board really was up for its press (ComposedBoardView, not the control layout)");
            Press(ctx, menu, written, "a campaign board");
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();

            // The frames themselves are not the evidence, so the folder a pilot collects their own
            // shots in is left as it was found.
            foreach (var path in written)
            {
                File.Delete(path);
            }
        }
    }

    private static void Press(TestContext ctx, LaunchMenu menu, List<string> written, string where)
    {
        var before = new HashSet<string>(Shots());
        var viewport = ctx.Host.GetViewport();
        var pane = new HeldPane(viewport);
        menu.ScreenshotPane = pane.Request;
        viewport.PushInput(new InputEventKey { Keycode = Key.F12, PhysicalKeycode = Key.F12, Pressed = true });
        bool taken = viewport.IsInputHandled();
        ctx.Check(pane.Grabs == 1, $"the press on {where} asked for the frame once ({pane.Grabs})");
        ctx.Check(Fresh(before).Count == 0,
            $"…and wrote nothing on the press itself, while the frame is still on its way");

        // The frame lands on a worker, as a live readback's does, and the file is written there, so
        // the check waits for that hand-over to finish before it looks.
        bool landed = pane.Release(System.TimeSpan.FromSeconds(10));
        ctx.Check(landed, $"…and the landed frame's write on {where} finished on its worker");
        var fresh = Fresh(before);
        written.AddRange(fresh);
        long size = fresh.Count == 1 ? new FileInfo(fresh[0]).Length : 0;
        ctx.Note($"{where}: handled={taken}, new files={fresh.Count}, bytes={size}");
        ctx.Check(fresh.Count == 1,
            $"one press on {where} wrote exactly one file once its frame landed ({fresh.Count}), so the shot lands and nothing captures it twice");
        ctx.Check(taken,
            $"and the menu took the key itself on {where} rather than leaving it to whatever is above it");
        ctx.Check(size > 0, $"and the file written from {where} has content ({size} bytes)");
        if (fresh.Count == 1)
        {
            ctx.Check(Path.GetDirectoryName(fresh[0]) == CaptureDirector.ShotDir(),
                $"the folder a menu shot lands in is the one the flight capture writes to ({CaptureDirector.ShotDir()})");
        }
    }

    private static List<string> Fresh(HashSet<string> before)
    {
        var fresh = new List<string>();
        foreach (var path in Shots())
        {
            if (!before.Contains(path))
            {
                fresh.Add(path);
            }
        }
        return fresh;
    }

    private static string[] Shots()
    {
        var dir = CaptureDirector.ShotDir();
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png") : System.Array.Empty<string>();
    }

    // The menu's frame source for one press. A suite runs inside one frame, so the live readback,
    // which lands frames later, would never arrive; this holds the request instead and hands the
    // viewport's frame over on a worker when released.
    private sealed class HeldPane
    {
        private readonly Viewport _viewport;
        private System.Action<Image?>? _held;

        public HeldPane(Viewport viewport)
        {
            _viewport = viewport;
        }

        public int Grabs { get; private set; }

        public bool Request(System.Action<Image?> landed)
        {
            Grabs++;
            _held = landed;
            return true;
        }

        public bool Release(System.TimeSpan timeout)
        {
            if (_held is not { } landed)
            {
                return false;
            }

            _held = null;
            var frame = _viewport.GetTexture()?.GetImage();
            return Task.Run(() => landed(frame)).Wait(timeout);
        }
    }
}
