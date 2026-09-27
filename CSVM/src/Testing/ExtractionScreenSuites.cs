using System;
using System.IO;
using System.Threading;
using CSVM.Extraction;
using CSVM.Session.Launch;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>The extraction screen as a built layer, the half a unit cannot reach. It pins each
/// state's view and default press, the stale variant's extra way on, and the picker's mode and pad
/// buttons. Esc cancels a run rather than quitting. A fake runner stands in for
/// the extraction, so nothing here reads or writes a real install.
/// ⚠ This pins the screen's wiring, not its look or the Deck's controls; both are judged by eye.</summary>
internal static class ExtractionScreenSuites
{
    [Suite("extraction-screen",
        "the missing-data screen opens on Extract with the pre-filled folder and no Play anyway, the stale "
        + "variant has its own title and Play anyway, a non-install pick shows the check's message, a run "
        + "shows the progress view with Cancel focused and the bar fed through the frame tick, a failed run "
        + "shows its failures with Try again focused, each view has its own body, and a successful run hands back exactly once")]
    internal static void ExtractionScreen(TestContext ctx)
    {
        string root = Scratch(ctx);
        try
        {
            string install = Install(root);
            Asking(ctx, root, install);
            Stale(ctx, root, install);
            Running(ctx, root, install);
            Failing(ctx, root, install);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Suite("extraction-picker",
        "the install picker is Godot's own directory dialog over the whole file system, opens on the folder "
        + "it is given, the left shoulder goes up a folder, Y takes the folder shown into the field with the "
        + "check's message when it is not an install, and Esc during a run cancels it instead of quitting")]
    internal static void ExtractionPicker(TestContext ctx)
    {
        string root = Scratch(ctx);
        try
        {
            string install = Install(root);
            string inside = Directory.CreateDirectory(Path.Combine(install, "GOSDATA", "ASSETS")).FullName;
            var screen = Screen(ctx, new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (r, _, _) => Succeeded(r), _ => { }, _ => { }), out _);
            try
            {
                var picker = screen.Picker;
                ctx.Check(picker.FileMode == FileDialog.FileModeEnum.OpenDir && !picker.UseNativeDialog
                    && picker.Access == FileDialog.AccessEnum.Filesystem,
                    $"the picker is Godot's own dialog choosing a folder anywhere on disk (mode {picker.FileMode}, native {picker.UseNativeDialog}, access {picker.Access})");

                screen.OpenPicker(inside);
                ctx.Check(picker.Visible, $"Choose folder shows the picker");
                ctx.Check(Same(picker.CurrentDir, inside), $"it opens on the folder it was given ({picker.CurrentDir})");

                picker.HandlePad(new InputEventJoypadButton { ButtonIndex = InstallPicker.UpButton, Pressed = true });
                ctx.Check(Same(picker.CurrentDir, Path.Combine(install, "GOSDATA")),
                    $"the left shoulder goes up one folder ({picker.CurrentDir})");

                picker.HandlePad(new InputEventJoypadButton { ButtonIndex = InstallPicker.TakeButton, Pressed = true });
                ctx.Check(!picker.Visible, $"Y closes the picker");
                ctx.Check(Same(screen.PathText, Path.Combine(install, "GOSDATA")), $"and puts the folder shown in the field ({screen.PathText})");
                ctx.Check(screen.NoticeText.Contains(install, StringComparison.Ordinal),
                    $"a pick inside the install names the install to choose instead ({screen.NoticeText})");

                screen.TakePick(install);
                ctx.Check(screen.NoticeText.Length == 0, $"a pick that is the install clears the notice ({screen.NoticeText})");

                screen.Extract();
                screen._Process(0);
                screen._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
                ctx.Check(screen.Flow.Cancelling, $"Esc during a run asks it to stop ({screen.Flow.View})");
            }
            finally
            {
                Drop(ctx, screen);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void Asking(TestContext ctx, string root, string install)
    {
        var screen = Screen(ctx, new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (r, _, _) => Succeeded(r), _ => { }, work => work()), out _);
        try
        {
            ctx.Check(screen.Shown == ExtractionView.Asking, $"a fresh screen asks first ({screen.Shown})");
            ctx.Check(screen.TitleText == NoGameDataScreen.Title(DataProblem.Missing), $"under the missing-data title ({screen.TitleText})");
            ctx.Check(screen.PathText == install, $"with the pre-filled folder in the field ({screen.PathText})");
            ctx.Check(screen.BodyText == NoGameDataScreen.Body(DataProblem.Missing, null), $"and the missing-data body ({screen.BodyText})");
            ctx.Check(!screen.PlayAnywayButton.Visible, $"missing data offers no Play anyway");
            ctx.Check(screen.GetViewport().GuiGetFocusOwner() == screen.ExtractButton,
                $"Extract holds the focus, so Enter or A extracts ({screen.GetViewport().GuiGetFocusOwner()?.Name})");

            string notInstall = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
            screen.TakePick(notInstall);
            ctx.Check(screen.NoticeText == InstallLocator.Check(notInstall).Message,
                $"a folder that is not an install shows the check's message ({screen.NoticeText})");
        }
        finally
        {
            Drop(ctx, screen);
        }
    }

    private static void Stale(TestContext ctx, string root, string install)
    {
        var screen = Screen(ctx, new ExtractionFlow(DataProblem.Older, root, "unzbd", install, (r, _, _) => Succeeded(r), _ => { }, work => work()), out var handbacks);
        try
        {
            ctx.Check(screen.TitleText == NoGameDataScreen.Title(DataProblem.Older) && screen.TitleText != NoGameDataScreen.Title(DataProblem.Missing),
                $"stale data has its own title ({screen.TitleText})");
            ctx.Check(screen.PlayAnywayButton.Visible, $"stale data offers Play anyway");
            ctx.Check(screen.Flow.Force, $"and re-extracts with force, since file times call the old outputs current");
            screen.PlayAnywayButton.EmitSignal(BaseButton.SignalName.Pressed);
            screen.PlayAnywayButton.EmitSignal(BaseButton.SignalName.Pressed);
            ctx.Check(handbacks[0] == 1, $"Play anyway hands back to the launcher once ({handbacks[0]})");
        }
        finally
        {
            Drop(ctx, screen);
        }
    }

    private static void Running(TestContext ctx, string root, string install)
    {
        using var reported = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? remembered = null;
        Thread? worker = null;
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (request, progress, cancel) =>
        {
            progress(new ExtractionProgress(ExtractionPhase.Rof, 0.5, new[] { "  crimson.rof" }, null));
            reported.Set();
            release.Wait(cancel);
            return Succeeded(request);
        }, path => remembered = path, work => (worker = new Thread(() => work()) { IsBackground = true }).Start());
        var screen = Screen(ctx, flow, out var handbacks);
        try
        {
            screen.Extract();
            ctx.Check(screen.Shown == ExtractionView.Running, $"Extract shows the progress view ({screen.Shown})");
            ctx.Check(screen.GetViewport().GuiGetFocusOwner() == screen.CancelButton, $"with Cancel focused");
            ctx.Check(screen.BodyText == NoGameDataScreen.RunningBody, $"and the body says what the run does, not what is missing ({screen.BodyText})");

            ctx.Check(reported.Wait(TimeSpan.FromSeconds(10)), $"the worker reported progress");
            ctx.Check(screen.BarValue == 0, $"which the bar has not seen before the frame tick ({screen.BarValue})");
            screen._Process(0);
            ctx.Check(Math.Abs(screen.BarValue - 50) < 0.01, $"the tick moves the bar to the report ({screen.BarValue})");
            ctx.Check(screen.PhaseLine == flow.PhaseText() && screen.PhaseLine.Length > 0, $"and names the phase ({screen.PhaseLine})");

            release.Set();
            worker?.Join(TimeSpan.FromSeconds(10));
            screen._Process(0);
            screen._Process(0);
            ctx.Check(handbacks[0] == 1, $"a finished run hands back to the launcher once ({handbacks[0]})");
            ctx.Check(remembered == install, $"and remembers the install it read ({remembered})");
        }
        finally
        {
            release.Set();
            Drop(ctx, screen);
        }
    }

    private static void Failing(TestContext ctx, string root, string install)
    {
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (request, _, _) =>
        {
            var result = Succeeded(request);
            result.Failures.Add("unzbd was not found at unzbd");
            return result;
        }, _ => { }, work => work());
        var screen = Screen(ctx, flow, out var handbacks);
        try
        {
            screen.Extract();
            screen._Process(0);
            ctx.Check(screen.Shown == ExtractionView.Failed, $"a failed run shows the failure view ({screen.Shown})");
            ctx.Check(screen.FailureText.Contains("unzbd was not found", StringComparison.Ordinal), $"naming the failure ({screen.FailureText})");
            ctx.Check(screen.GetViewport().GuiGetFocusOwner() == screen.RetryButton, $"with Try again focused");
            ctx.Check(handbacks[0] == 0, $"and no hand-back ({handbacks[0]})");
            ctx.Check(screen.BodyText == NoGameDataScreen.FailedBody, $"under the failure body ({screen.BodyText})");
        }
        finally
        {
            Drop(ctx, screen);
        }
    }

    // The screen in the tree, with a counter of its hand-backs to the launcher in handbacks[0].
    private static NoGameDataScreen Screen(TestContext ctx, ExtractionFlow flow, out int[] handbacks)
    {
        var count = new int[1];
        handbacks = count;
        var screen = NoGameDataScreen.Build(flow, () => count[0]++, () => { });
        ctx.Host.AddChild(screen);
        return screen;
    }

    private static void Drop(TestContext ctx, NoGameDataScreen screen)
    {
        ctx.Host.RemoveChild(screen);
        screen.QueueFree();
    }

    private static string Scratch(TestContext ctx) =>
        Directory.CreateDirectory(Path.Combine(ctx.ScratchDir, "extraction-screen-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string Install(string root)
    {
        string install = Path.Combine(root, "Crimson Skies");
        Directory.CreateDirectory(Path.Combine(install, "ZBD"));
        Directory.CreateDirectory(Path.Combine(install, "GOSDATA", "ASSETS"));
        File.WriteAllText(Path.Combine(install, "ZBD", "planes.zbd"), "x");
        return install;
    }

    private static ExtractionResult Succeeded(ExtractionRequest request) =>
        new() { Install = InstallLocator.Check(request.InstallFolder) };

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }
}
