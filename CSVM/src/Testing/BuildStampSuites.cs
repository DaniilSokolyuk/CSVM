using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Extraction;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// The version stamp's two folder icons as built controls. They are mouse-only, carry their
/// PromptFont glyphs and tooltips, and share the stamp's visibility. They open the log file's own
/// directory and Godot's user folder, and Godot's focus walk on the extraction screen never reaches
/// them. The opener is swapped for a recorder, so no suite opens a window on the desktop.
/// ⚠ This pins the wiring, not the look; the menu-original-flag golden is where the icons are seen.
/// </summary>
internal static class BuildStampSuites
{
    [Suite("build-stamp-icons",
        "the stamp holds the logs and user-folder icons left of its text, each unfocusable, clickable over "
        + "a mouse-ignoring parent chain on a layer above the boards, with its PromptFont glyph and tooltip; "
        + "the stamp shows and hides on its tick, and the icons open the log file's directory and the user data folder")]
    internal static void BuildStampIcons(TestContext ctx)
    {
        var stamp = new BuildStamp(ctx.RepoRoot, exported: false);
        var opened = new List<(string Path, string What)>();
        stamp.Opener = (path, what) =>
        {
            opened.Add((path, what));
            return path;
        };
        ctx.Host.AddChild(stamp);
        try
        {
            var logs = stamp.LogsButton;
            var user = stamp.UserButton;
            ctx.Check(logs != null && user != null && stamp.IsAncestorOf(logs) && stamp.IsAncestorOf(user),
                $"both icons are built under the stamp");
            if (logs == null || user == null)
            {
                return;
            }

            Parts(ctx, logs, user);
            Visibility(ctx, stamp, logs);
            Folders(ctx, stamp, logs, user, opened);
        }
        finally
        {
            ctx.Host.RemoveChild(stamp);
            stamp.QueueFree();
        }
    }

    [Suite("build-stamp-focus",
        "Godot's focus walk over the extraction screen, forward, back and in all four directions from every "
        + "control it reaches, never lands on either folder icon while the stamp is shown over the screen, and "
        + "a click pushed through the viewport reaches the logs icon over the screen's backdrop and leaves Extract focused")]
    internal static void BuildStampFocus(TestContext ctx)
    {
        string root = Directory.CreateDirectory(Path.Combine(ctx.ScratchDir, "build-stamp-" + Guid.NewGuid().ToString("N"))).FullName;
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", root, (r, _, _) => new ExtractionResult { Install = InstallLocator.Check(r.InstallFolder) }, _ => { }, work => work());
        var screen = NoGameDataScreen.Build(flow, () => { }, () => { });
        var stamp = new BuildStamp(ctx.RepoRoot, exported: false) { Opener = (path, _) => path };
        ctx.Host.AddChild(screen);
        ctx.Host.AddChild(stamp);
        try
        {
            stamp.Tick(true);
            var reached = Walk(screen.ExtractButton);
            ctx.Note($"the focus walk reached {reached.Count} controls");
            ctx.Check(reached.Count > 1, $"the walk moved off Extract, so it ran over a real screen ({reached.Count})");
            ctx.Check(!reached.Contains(stamp.LogsButton) && !reached.Contains(stamp.UserButton),
                $"and never reached a folder icon");

            // A real click through the viewport's GUI dispatch. It has to reach the icon over the
            // screen's full-window backdrop, and leave the focus where the pad left it.
            var viewport = screen.GetViewport();
            ctx.Check(viewport.GuiGetFocusOwner() == screen.ExtractButton, $"Extract holds the focus before the click");
            var at = Settle(stamp.LogsButton).GetCenter();
            string? opened = null;
            stamp.Opener = (path, _) => opened = path;
            viewport.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at });
            viewport.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
            ctx.Check(opened == stamp.LogsFolder, $"a click at {at} reaches the logs icon above the screen ({opened ?? "nothing opened"})");
            ctx.Check(viewport.GuiGetFocusOwner() == screen.ExtractButton,
                $"and leaves Extract focused, so Enter or A after a click still extracts ({viewport.GuiGetFocusOwner()?.Name})");
        }
        finally
        {
            ctx.Host.RemoveChild(stamp);
            stamp.QueueFree();
            ctx.Host.RemoveChild(screen);
            screen.QueueFree();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Parts(TestContext ctx, Button logs, Button user)
    {
        ctx.Check(logs.FocusMode == Control.FocusModeEnum.None && user.FocusMode == Control.FocusModeEnum.None,
            $"neither icon takes focus ({logs.FocusMode}, {user.FocusMode}), so no keyboard, pad or stick walk can stop on one");
        ctx.Check(PromptFontGlyphs.Font != null, $"the PromptFont face loaded, so the icons draw as glyphs");
        // The selector is what keeps the glyph on PromptFont rather than a system colour emoji.
        const string TextPresentation = "︎";
        ctx.Check(logs.Text == char.ConvertFromUtf32(0x1F4DD) + TextPresentation && user.Text == char.ConvertFromUtf32(0x1F4BE) + TextPresentation,
            $"the logs icon is U+1F4DD and the user icon U+1F4BE, each in text presentation ({Codepoint(logs.Text)}, {Codepoint(user.Text)})");
        ctx.Check(logs.TooltipText == "Open logs folder" && user.TooltipText == "Open user folder",
            $"each carries its tooltip ({logs.TooltipText}; {user.TooltipText})");

        var row = logs.GetParent();
        int text = -1;
        for (int i = 0; i < row.GetChildCount(); i++)
        {
            if (row.GetChild(i) is Label)
            {
                text = i;
            }
        }

        ctx.Check(user.GetParent() == row && logs.GetIndex() < user.GetIndex() && user.GetIndex() < text,
            $"left to right they read logs, user folder, version text ({logs.GetIndex()}, {user.GetIndex()}, {text})");
        ctx.Check(logs.MouseFilter == Control.MouseFilterEnum.Stop && user.MouseFilter == Control.MouseFilterEnum.Stop,
            $"both take the mouse ({logs.MouseFilter}, {user.MouseFilter})");

        Node? up = logs.GetParent();
        bool ignored = true;
        CanvasLayer? layer = null;
        while (up != null && layer == null)
        {
            if (up is Control control)
            {
                ignored &= control.MouseFilter == Control.MouseFilterEnum.Ignore;
            }

            layer = up as CanvasLayer;
            up = up.GetParent();
        }

        ctx.Check(ignored, $"every control between an icon and its layer ignores the mouse, so the rest of the corner still reaches the menu");
        ctx.Check(layer != null && layer.Layer > HudLayers.Board,
            $"the icons draw on layer {layer?.Layer}, above the boards' {HudLayers.Board}, so a click reaches them first");
    }

    private static void Visibility(TestContext ctx, BuildStamp stamp, Button logs)
    {
        ctx.Check(!stamp.Shown, $"a fresh stamp is hidden until the launcher ticks it");
        stamp.Tick(true);
        ctx.Check(stamp.Shown && logs.IsVisibleInTree(), $"a tick with the menu up shows it, icons included");

        var rect = Settle(logs);
        ctx.Note($"logs icon {rect}, row {((Control)logs.GetParent()).GetGlobalRect()}");
        ctx.Check(rect.Size.X > 0 && stamp.HoldsPointer(rect.GetCenter()) && !stamp.HoldsPointer(Vector2.Zero),
            $"a click on an icon is the stamp's and one elsewhere is not ({rect})");

        stamp.Tick(false);
        ctx.Check(!stamp.Shown && !logs.IsVisibleInTree() && !stamp.HoldsPointer(rect.GetCenter()),
            $"a tick in flight hides it and gives every click back");
    }

    private static void Folders(TestContext ctx, BuildStamp stamp, Button logs, Button user, List<(string Path, string What)> opened)
    {
        if (Log.SinkPath is { } sink)
        {
            ctx.Check(File.Exists(sink) && stamp.LogsFolder == Path.GetDirectoryName(sink),
                $"the logs icon opens the directory the open log file is in ({stamp.LogsFolder}, sink {sink})");
        }
        else
        {
            ctx.Note($"no log file is open in this run, so only the fallback below is checked");
        }

        ctx.Check(BuildStamp.LogsFolderFor(null, ctx.RepoRoot, false) == Log.DirectoryFor(ctx.RepoRoot, false)
            && BuildStamp.LogsFolderFor(null, ctx.RepoRoot, true) == Log.DirectoryFor(ctx.RepoRoot, true),
            $"with no log file open it falls back to where the sink would write, for a repo run and an export alike");
        ctx.Check(BuildStamp.UserFolder == OS.GetUserDataDir(), $"the user icon opens Godot's user folder ({BuildStamp.UserFolder})");

        logs.EmitSignal(BaseButton.SignalName.Pressed);
        user.EmitSignal(BaseButton.SignalName.Pressed);
        ctx.Check(opened.Count == 2 && opened[0].Path == stamp.LogsFolder && opened[1].Path == OS.GetUserDataDir(),
            $"a click hands the opener exactly its own folder ({string.Join("; ", opened)})");
    }

    // A container sorts its children on a deferred call, which a suite inside one frame never
    // reaches. So the icon's row is resized and sorted here before the icon's rect is read.
    private static Rect2 Settle(Button icon)
    {
        var row = (Container)icon.GetParent();
        row.OffsetTop += 1f;
        row.OffsetTop -= 1f;
        row.Notification((int)Container.NotificationSortChildren);
        return icon.GetGlobalRect();
    }

    // Every control Godot's focus reaches from start: next, previous and the four directions,
    // from each control reached in turn.
    private static HashSet<Control> Walk(Control start)
    {
        var reached = new HashSet<Control> { start };
        var open = new Queue<Control>();
        open.Enqueue(start);
        while (open.Count > 0)
        {
            var at = open.Dequeue();
            foreach (var next in new[]
            {
                at.FindNextValidFocus(), at.FindPrevValidFocus(),
                at.FindValidFocusNeighbor(Side.Left), at.FindValidFocusNeighbor(Side.Right),
                at.FindValidFocusNeighbor(Side.Top), at.FindValidFocusNeighbor(Side.Bottom),
            })
            {
                if (next != null && reached.Add(next))
                {
                    open.Enqueue(next);
                }
            }
        }

        return reached;
    }

    private static string Codepoint(string text) =>
        text.Length > 0 && char.IsSurrogatePair(text, 0)
            ? "U+" + char.ConvertToUtf32(text, 0).ToString("X", CultureInfo.InvariantCulture)
            : text;
}
