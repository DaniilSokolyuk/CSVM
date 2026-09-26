using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// The Original presentation's Instant Action wrap-up page over the install's decoded layout.
/// A real <see cref="InstantActionRuntime"/> is ended and held. The ending's snapshot crosses the
/// session boundary the way the launcher hands it over. The page draws the four decoded rows and
/// the further lines the built-in board carries, and CONTINUE lands back on the Instant Action
/// screen. Readings: docs/formats/instant-action/wrap-up.md.
/// </summary>
internal static class MenuOriginalWrapupSuites
{
    [Suite("menu-original-wrapup",
        "Original's Instant Action wrap-up page end to end over the install's decoded layout: a real "
        + "runtime's ending records the four counters as final and holds, nothing leaves for the menu until "
        + "the hold ends, the handover then opens the decoded [@IA_WrapUp@] page with the magazine "
        + "spread, the four brushstrokes, the heading and the eight row lines off the snapshot, the "
        + "context and the stunt splits (without their total) on a yellow post-it and a ticked box "
        + "above CONTINUE, a later ending's numbers never reach the page that is already standing, a "
        + "failed run leaves the box empty, a real camera's C4/IA1 photographs stand in marker order "
        + "beside the post-its under the rows and a frame landing after the page woke fills its "
        + "print, each landed print takes the cursor and the pointer and opens its PNG full size in "
        + "the presentation's viewer, Back or a click closing it with the cursor back on the print "
        + "while a pending print takes neither, and CONTINUE and Back both return to the Instant Action "
        + "screen with the run forgotten")]
    internal static void MenuOriginalWrapup(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        var exits = new List<MenuExit>();
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, new MenuInput { Keyboard = true }));
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot);
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
            Handover(ctx, host, shell);
            Page(ctx, shell);
            Frozen(ctx, host, shell);
            Photographs(ctx, host, seat, shell, fit);
            Doors(ctx, host, seat, shell, fit);
        }
        finally
        {
            host.Deactivate();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    // The seam the director and the launcher make between them. The numbers are read once at the
    // ending, and the hold runs with the world still up. Only the hold's end sends them to the menu.
    private static void Handover(TestContext ctx, MenuHost host, OriginalShell shell)
    {
        var run = new InstantActionRuntime(
            InstantActionSuites.EndDef(ctx, "wrapup-original", "dogfight_ace"));
        IaWrapupSnapshot? frozen = null;
        run.MissionEnded += outcome => frozen = new IaWrapupSnapshot(
            outcome == InstantActionOutcome.Won, "C1   ·   Ace Duel", run.Elapsed, 4, 3, 27,
            new[] { "1.  Pier   12.4   12.4", "TOTAL   12.4" });
        run.WrapupDue += _ =>
        {
            if (frozen is { } snapshot)
            {
                host.Show(new InstantActionWrapupReturn(snapshot));
            }
        };

        run.Advance(186f);
        run.ReportObjective(InstantActionObjective.AceDown);
        ctx.Check(frozen != null && shell.Screen != OriginalScreen.InstantActionWrapup,
            $"the ending makes the numbers final with the world still up and no page yet ({shell.Screen})");
        run.Advance(InstantActionRuntime.WrapupHoldS);
        ctx.Check(shell.Screen == OriginalScreen.InstantActionWrapup && shell.Wrapup.Snapshot != null,
            $"the hold's end hands the snapshot to the menu's own page ({shell.Screen})");
        ctx.Check(shell.Wrapup.Snapshot?.Elapsed == 186f,
            $"carrying the mission clock as the ending stopped it ({shell.Wrapup.Snapshot?.Elapsed})");
    }

    // What the page draws, read off the shell's own composition rather than the page's builder.
    private static void Page(TestContext ctx, OriginalShell shell)
    {
        var board = shell.Compose();
        var text = board.Lines.Select(l => l.Text).ToList();
        ctx.Check(text.Contains("Instant Action"), $"the decoded heading stands over the page ({text.Count} lines)");
        ctx.Check(text.Contains("Time to Complete Mission") && text.Contains("03:06"),
            $"the time row reads the final clock ({string.Join(" / ", text)})");
        ctx.Check(text.Contains("Enemies Shot Down") && text.Contains("4"), $"the kill row reads the final tally");
        ctx.Check(text.Contains("Danger Zones Completed") && text.Contains("3"), $"the zone row reads the final count");
        ctx.Check(text.Contains("Shot %") && text.Contains("27%"), $"the shot row reads the final percentage");

        var backdrop = board.Backdrop.FirstOrDefault();
        ctx.Check(backdrop != null && backdrop.Art.Name.Length > 0,
            $"the magazine spread is the backdrop ({backdrop?.Art.Name})");
        ctx.Check(board.Pictures.Count >= 4, $"a brushstroke stands under each of the four rows ({board.Pictures.Count})");

        var note = board.Notes.FirstOrDefault();
        ctx.Check(board.Notes.Count == 1 && note != null && note.Entries.Count == 2
                && note.Entries[0] == "C1  ·  Ace Duel" && note.Entries[1] == "1.  Pier  12.4  12.4",
            $"the context and the splits are the further lines, with no headline and no second time ({(note == null ? "none" : string.Join(" / ", note.Entries))})");
        ctx.Check(note != null && note.Shrink && board.Fills.Any(f => f.X <= note.X && f.Y <= note.Y
                && f.X + f.Width >= note.X + note.Width && f.Y + f.Height >= note.Y + note.Height),
            $"written on a post-it that holds them ({board.Fills.Count} fills)");
        float plaqueY = shell.Rows.Count > 0 ? shell.Rows[0].Y : 0f;
        ctx.Check(board.Strokes.Count > 8 && board.Strokes.All(s => s.Y1 < plaqueY && s.Y2 < plaqueY),
            $"a ticked box stands above CONTINUE on a win ({board.Strokes.Count} strokes, plaque at y {plaqueY})");
        ctx.Check(board.Plaques.Any(p => p.Row == 0) && shell.Rows.Count == 1
                && shell.Rows[0].Key == OriginalWrapupScreen.ContinueKey,
            $"with CONTINUE as the page's one row ({shell.Rows.Count} rows)");
    }

    // The page shows the run it was handed, and a later ending cannot edit the one already drawn.
    private static void Frozen(TestContext ctx, MenuHost host, OriginalShell shell)
    {
        var stale = shell.Wrapup.Snapshot;
        var later = new InstantActionRuntime(
            InstantActionSuites.EndDef(ctx, "wrapup-original-later", "dogfight_ace"));
        later.Advance(600f);
        later.ReportObjective(InstantActionObjective.AceDown);
        var board = shell.Compose();
        ctx.Check(board.Lines.Any(l => l.Text == "03:06") && !board.Lines.Any(l => l.Text == "10:00"),
            $"a second mission ending elsewhere leaves the drawn page alone");
        ctx.Check(ReferenceEquals(shell.Wrapup.Snapshot, stale), $"the page still holds its own run");

        host.Show(new InstantActionWrapupReturn(new IaWrapupSnapshot(
            false, "C3   ·   Dogfight", 74.5f, 1, 0, 8)));
        var failed = shell.Compose();
        ctx.Check(failed.Notes.Count == 1 && failed.Notes[0].Entries.Count == 1
                && failed.Notes[0].Entries[0] == "C3  ·  Dogfight",
            $"a failed run carries only its context and no splits ({string.Join(" / ", failed.Notes[0].Entries)})");
        ctx.Check(failed.Strokes.Count == 8, $"and leaves the tick box empty ({failed.Strokes.Count} strokes)");
        ctx.Check(failed.Lines.Any(l => l.Text == "01:14") && failed.Lines.Any(l => l.Text == "8%"),
            $"and its own numbers");
    }

    // A real camera's shots over C4/IA1's markers reach the page in marker order, beside the
    // post-its. The last frame is held back past the page waking and fills its print on landing.
    private static void Photographs(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit)
    {
        string gamez = SessionPaths.ChapterGamez(ctx.DataRoot, "C4");
        string zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C4", "IA1");
        ctx.RequireData(gamez, $"C4 gamez");
        ctx.RequireData(zrdr, $"C4/IA1 zrdr");
        ctx.RequireData(ctx.MessagesPath, $"messages.json");
        var run = StuntMission.Load(GameZ.Load(gamez), zrdr, Messages.Load(ctx.MessagesPath))
            ?? throw new SuiteSkippedException("C4/IA1 ships no Danger Zones");

        string? previous = StuntCapture.DirectoryOverride;
        try
        {
            StuntCapture.DirectoryOverride = Path.Combine(ctx.ScratchDir, "WrapupPhotographs");
            PhotographsOver(ctx, host, seat, shell, fit, run);
        }
        finally
        {
            StuntCapture.DirectoryOverride = previous;
        }
    }

    private static void PhotographsOver(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit, StuntMission run)
    {
        System.Action<Image?>? held = null;
        var last = run.Zones[^1];
        bool hold = false;
        var capture = new StuntCapture(run, "C4", landed =>
        {
            if (hold)
            {
                held = landed;
            }
            else
            {
                landed(Frame());
            }

            return true;
        });
        var landedEvents = new List<string>();
        capture.ShotLanded += shot => landedEvents.Add(shot.DzName);
        foreach (var zone in run.Zones)
        {
            run.Tick(1f);
            hold = zone == last;
            capture.Update(zone.Position);
            capture.Update(new Vector3(0f, 60000f, 0f));
        }

        capture.Settle();
        var markers = run.Zones.Select(z => z.DzName).ToList();
        host.Show(new InstantActionWrapupReturn(
            InstantActionWrapupPage.Sample(won: true) with { Shots = capture.InMarkerOrder().ToList() }));
        var prints = shell.Wrapup.Prints;
        ctx.Check(prints.Select(p => p.Shot.DzName).SequenceEqual(markers),
            $"the page carries the run's photographs in marker order ({string.Join(" ", prints.Select(p => p.Shot.DzName))})");

        var board = shell.Compose();
        float lastRow = board.Lines.Where(l => l.Text == "27%").Select(l => l.Y).DefaultIfEmpty(0f).Max();
        float leftmost = board.Notes.Select(n => n.X).DefaultIfEmpty(800f).Min();
        ctx.Check(prints.All(p => p.Y > lastRow && p.X >= 0f && p.X + p.Width < leftmost && p.Y + p.Height <= 600f),
            $"beside the post-its in the band under the rows ({prints.Count} prints, rows end at y {lastRow}, post-its from x {leftmost})");
        ctx.Same(markers.Count - 1, Held(board), $"every landed photograph draws in its print, the held one's stays empty");
        ctx.Check(!shell.Wrapup.TakeLanded(), $"with nothing landed since the page woke, nothing asks for a repaint");
        Viewer(ctx, host, seat, shell, fit, prints.Count - 1);

        held?.Invoke(Frame());
        capture.Settle();
        ctx.Check(landedEvents.Contains(last.DzName) && prints[^1].Shot.Landed,
            $"the held frame lands after the page woke ({last.DzName}, ShotLanded raised)");
        ctx.Check(shell.Wrapup.TakeLanded() && !shell.Wrapup.TakeLanded(),
            $"its landing asks the page to repaint, once");
        ctx.Same(markers.Count, Held(shell.Compose()), $"…and the repainted page fills its print");
        ctx.Check(shell.Rows.FirstOrDefault(r => r.Key == OriginalWrapupScreen.PrintKey(prints.Count - 1))?.Enabled == true,
            $"…and its print takes the cursor from then on");
    }

    // The prints open full size. The cursor walks CONTINUE and the landed prints. Confirm opens one
    // in the presentation's viewer from its PNG. Back closes it with the cursor back on its print.
    // The pointer does the same, a click anywhere on the open photograph closing it. The print
    // whose frame is still on its way takes neither.
    private static void Viewer(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit, int pending)
    {
        var prints = shell.Wrapup.Prints;
        var presentation = host.Active as OriginalPresentation;
        var rows = shell.Rows;
        var printRows = Enumerable.Range(0, prints.Count)
            .Select(i => rows.FirstOrDefault(r => r.Key == OriginalWrapupScreen.PrintKey(i))).ToList();
        ctx.Check(rows.Count == prints.Count + 1 && printRows.All(r => r != null),
            $"each print is a row on the page after CONTINUE ({rows.Count} rows, {prints.Count} prints)");
        ctx.Check(printRows.Select((r, i) => r?.Enabled == (i != pending)).All(ok => ok),
            $"…the landed ones enabled and the pending one ({prints[pending].Shot.DzName}) not");
        ctx.Check(shell.FocusedKey == OriginalWrapupScreen.ContinueKey,
            $"the cursor rests on CONTINUE ({shell.FocusedKey})");

        shell.Step(new MenuCommands { MoveY = 1 });
        ctx.Check(shell.FocusedKey == OriginalWrapupScreen.PrintKey(0),
            $"down from CONTINUE reaches the first print ({shell.FocusedKey})");
        ctx.Check(shell.Compose().Fills.Any(f => f.Border && f.X == prints[0].X && f.Y == prints[0].Y),
            $"…which draws the focus box round it");
        shell.Step(new MenuCommands { Accept = true });
        host.Tick(1f / 60f);
        var viewer = presentation?.PhotoViewer;
        ctx.Check(shell.Wrapup.Viewing == prints[0].Shot && viewer is { IsOpen: true } && viewer.Shot == prints[0].Shot
                && viewer.ImageSize == new Vector2I(64, 36),
            $"confirm opens {prints[0].Shot.DzName} full size from its file: open={viewer?.IsOpen} size={viewer?.ImageSize}");
        ctx.Check(shell.Rows.Count == 1 && shell.Rows[0].Key == OriginalWrapupScreen.ViewerKey,
            $"…and the page stands as the one row the photograph covers ({shell.Rows.Count} rows)");
        shell.Step(new MenuCommands { Back = true });
        host.Tick(1f / 60f);
        ctx.Check(shell.Screen == OriginalScreen.InstantActionWrapup && shell.Wrapup.Viewing == null
                && viewer?.IsOpen == false && shell.FocusedKey == OriginalWrapupScreen.PrintKey(0),
            $"Back closes it, on the page still, with the cursor back on its print ({shell.Screen}, {shell.FocusedKey})");

        shell.Step(new MenuCommands { MoveY = -1 });
        shell.Step(new MenuCommands { MoveY = -1 });
        ctx.Check(shell.FocusedKey == OriginalWrapupScreen.PrintKey(pending - 1),
            $"up past CONTINUE wraps to the last landed print, passing over the pending one ({shell.FocusedKey})");

        var held = prints[pending];
        Click(host, seat, fit, held.X + (held.Width / 2f), held.Y + (held.Height / 2f));
        ctx.Check(shell.Wrapup.Viewing == null && viewer?.IsOpen == false,
            $"a click on the pending print opens nothing");
        var second = prints[1];
        Click(host, seat, fit, second.X + (second.Width / 2f), second.Y + (second.Height / 2f));
        ctx.Check(shell.Wrapup.Viewing == second.Shot && viewer?.IsOpen == true,
            $"a click on a landed print opens it ({shell.Wrapup.Viewing?.DzName})");
        Click(host, seat, fit, 796f, 4f);
        ctx.Check(shell.Wrapup.Viewing == null && viewer?.IsOpen == false
                && shell.FocusedKey == OriginalWrapupScreen.PrintKey(1),
            $"a click on the open photograph closes it, the cursor on its print ({shell.FocusedKey})");
    }

    // How many photographs a composed board draws.
    private static int Held(ComposedBoard board) => board.Pictures.Count(p => p.Art.Library == BoardArtLibrary.Held);

    // A stand-in frame, a flat colour, so the check rests on the page and not on what a host drew.
    private static Image Frame()
    {
        var image = Image.CreateEmpty(64, 36, false, Image.Format.Rgba8);
        image.Fill(new Color(0.3f, 0.5f, 0.7f));
        return image;
    }

    // Both ways off the page, and what they leave behind.
    private static void Doors(
        TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit)
    {
        var back = shell.Step(new MenuCommands { Back = true });
        ctx.Check(shell.Screen == OriginalScreen.InstantAction && shell.Wrapup.Snapshot == null && back.Exit == null,
            $"Back returns to the Instant Action screen with the run forgotten ({shell.Screen})");

        host.Show(new InstantActionWrapupReturn(InstantActionWrapupPage.Sample(won: true)));
        ctx.Check(shell.Screen == OriginalScreen.InstantActionWrapup, $"the page stands again ({shell.Screen})");
        var plaque = shell.Rows.FirstOrDefault(r => r.Key == OriginalWrapupScreen.ContinueKey);
        ctx.Check(plaque != null, $"CONTINUE is on the page");
        if (plaque == null)
        {
            return;
        }

        Click(host, seat, fit, plaque.X + 5f, plaque.Y + 5f);
        ctx.Check(shell.Screen == OriginalScreen.InstantAction && shell.Wrapup.Snapshot == null,
            $"a click on CONTINUE lands on the Instant Action screen ({shell.Screen})");
        ctx.Check(shell.Rows.Any(r => r.Key == OriginalInstantActionScreen.ExitKey),
            $"the sortie's own rows are back ({shell.Rows.Count} rows)");
    }

    // One click as the shell reads it: the press arms the row and the release on it fires.
    private static void Click(MenuHost host, ScriptedSeat seat, BoardFit fit, float x, float y)
    {
        var down = new MenuCommands { Pointer = new MenuPointer(fit.X(x), fit.Y(y), true, true) };
        seat.Enqueue(down);
        host.Tick(1f / 60f);
        seat.Enqueue(down with { Pointer = down.Pointer!.Value with { Pressed = false, Clicked = false } });
        host.Tick(1f / 60f);
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
}
