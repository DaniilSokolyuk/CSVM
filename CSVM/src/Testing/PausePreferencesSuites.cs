using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over the Preferences leaf the pause opens: the real
/// <see cref="PausePreferences"/> and a real <see cref="OriginalPauseBoard"/> over a real
/// <see cref="PauseState"/>, inside a SubViewport standing in for the flight's own, so a display
/// change can be walked end to end and the viewport resized under both without touching the
/// window this process is captured from. Decode: docs/org/pause-screen.md.</summary>
internal static class PausePreferencesSuites
{
    // The size saved beside the display mode the page must open on: a word from the fixed
    // vocabulary rather than a size read off this machine, so the walk reads the same on every one.
    private const string SavedResolution = "1024x768";

    // The viewport the leaf is opened in, and the one it is resized to. The aspect changes with the
    // size, so BoardFit's letterbox origin moves too and a stale fit cannot pass the hit test.
    private static readonly Vector2I FlightWindow = new(1280, 720);
    private static readonly Vector2I ResizedWindow = new(1024, 1024);

    // The turn the live-options seats hold: nose rising and yawing to starboard, in the plant's own
    // half-angle body rates, so an autohead that is on has a lead to aim along.
    private static readonly Vector3 SeatBodyRates = new(0.1f, -0.15f, 0f);

    [Suite("pause-preferences",
        "the Preferences leaf over a paused mission: the Original pause sheet's PREFERENCES strip "
        + "opens it on the Options screen with the world still held, the VIDEO page behind its door "
        + "opens on the settings saved for this machine with the size row dead under borderless and "
        + "live again the moment the mode leaves it, a display mode stepped there rides out on "
        + "the apply exit while the leaf writes no options file of its own, the way out closes the "
        + "leaf back onto the sheet with the mission still paused, the returning sheet cannot fire "
        + "a strip from a mouse button that was already down, applying what the exit carried leaves "
        + "the page reopening on the new mode, and resizing the viewport under the leaf and the "
        + "sheet re-fits both, so a size or mode change accepted in flight leaves the pointer "
        + "hitting the rows it is drawn over")]
    internal static void PausePreferencesLeaf(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (missions.Count == 0)
        {
            throw new SuiteSkippedException($"cm_sequence names no campaign mission");
        }

        var mission = missions[0];
        var sheet = PauseSheet.Load(
            ctx.ZrdrPath, ctx.MessagesPath,
            EscapeDialog.CampaignKey(mission.Campaign, mission.Mission), instantAction: false);
        if (sheet == null)
        {
            ctx.Check(false, $"{mission.ChapterFolder}/{mission.MissionFolder} resolves its pause dialog");
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "pause-preferences");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef
            {
                DisplayMode = DisplayWords.Borderless,
                Resolution = SavedResolution,
                VSync = DisplayWords.VSyncOff,
            });
            Walk(ctx, layout, sheet);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
        }
    }

    [Suite("pause-preferences-live-options",
        "Auto Head Turn and Next Target changed over the pause reach the seats already flying: two "
        + "human seats in the cockpit in a held turn to lead into and a live target selection, the "
        + "real Preferences leaf opened over them, both Game Options switches toggled and ACCEPT "
        + "CHANGES pressed, puts the applied values on both seats at once, the targeting switch "
        + "written into the selection the seat already holds rather than a new one, the next "
        + "frame's head target leads into the turn where on and sits straight ahead where off, "
        + "and an accept carrying never-set values hands the head turn back to the "
        + "headLook.autohead config key and the targeting switch back to the decoded head rule")]
    internal static void PausePreferencesLiveOptions(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "pause-preferences-live-options");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        var savedClock = GameClock.Current;
        var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
        GameClock.Current = clock;
        var panes = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        ctx.Host.AddChild(panes);
        var stats = PlaneStats.Load(ctx.ZrdrPath, "player_bhawk");
        var one = CockpitSeat(ctx, panes, stats, 0);
        var two = CockpitSeat(ctx, panes, stats, 1);
        var seats = new[] { one, two };
        var selection = one.Targeting;
        OptionsApplyExit? applied = null;
        // Launcher.PersistOptions' own write of the two fields, so the page reopens on what it saved.
        var leaf = PausePreferences.Build(ctx.DataRoot, layout, null, a =>
        {
            applied = a;
            var store = OptionsStore.UserOptions();
            var options = store.Load();
            options.AutoHeadTurn = a.AutoHeadTurn;
            options.NearestAfterKill = a.NearestAfterKill;
            store.Save(options);
        });
        try
        {
            ctx.Check(leaf != null, $"the leaf builds over the install's decoded layout");
            if (leaf == null)
            {
                return;
            }

            bool fallback = Config.GetBool("headLook.autohead", false);
            Frame(clock, seats);
            ctx.Check(one.AutoHeadTurn == null && Leans(one) == fallback && Leans(two) == fallback,
                $"ABLE-TO-FAIL CONTROL: with nothing saved both seats fly the config key's head turn ({fallback}; leaning {Leans(one)}, {Leans(two)})");
            ctx.Check(!Nearest(one) && !Nearest(two),
                $"ABLE-TO-FAIL CONTROL: and the decoded head rule after a kill ({Nearest(one)}, {Nearest(two)})");

            AcceptGameOptions(leaf, seats, toggle: true);
            ctx.Check(applied?.AutoHeadTurn == true && one.AutoHeadTurn == true && two.AutoHeadTurn == true,
                $"toggling Auto Head Turn over the pause puts it on both flying seats at once (applied {applied?.AutoHeadTurn}, seats {one.AutoHeadTurn}, {two.AutoHeadTurn})");
            ctx.Check(applied?.NearestAfterKill == true && Nearest(one) && Nearest(two),
                $"and toggling Next Target puts both seats on the nearest target after a kill (applied {applied?.NearestAfterKill}, seats {Nearest(one)}, {Nearest(two)})");
            ctx.Check(ReferenceEquals(one.Targeting, selection),
                $"written into the selection the seat already held, so its cycle and lock survive the accept");
            Frame(clock, seats);
            ctx.Check(Leans(one) && Leans(two) && OnTurn(one),
                $"and the next frame's head target leads into the turn ({Target(one)} against {Expected(one)}, seat 2 {Target(two)})");

            AcceptGameOptions(leaf, seats, toggle: true);
            Frame(clock, seats);
            ctx.Check(one.AutoHeadTurn == false && two.AutoHeadTurn == false && !Leans(one) && !Leans(two),
                $"toggling the head turn off again puts the head straight ahead on the next frame (seats {one.AutoHeadTurn}, {two.AutoHeadTurn}; {Target(one)}, {Target(two)})");
            ctx.Check(!Nearest(one) && !Nearest(two),
                $"and toggling Next Target off puts both seats back on the head rule ({Nearest(one)}, {Nearest(two)})");

            // Values the file never carried ride out as null on any accept, which must hand the seat
            // back to the launch's own fallbacks rather than keep what it flew with.
            OptionsStore.UserOptions().Save(new OptionsDef());
            foreach (var seat in seats)
            {
                seat.AutoHeadTurn = !fallback;
                seat.Targeting!.NearestAfterKill = true;
            }

            AcceptGameOptions(leaf, seats, toggle: false);
            Frame(clock, seats);
            ctx.Check(applied?.AutoHeadTurn == null && one.AutoHeadTurn == null && two.AutoHeadTurn == null
                && Leans(one) == fallback && Leans(two) == fallback,
                $"an accept carrying never-set values restores the config key's head turn on both seats (seats {one.AutoHeadTurn?.ToString() ?? "null"}, {two.AutoHeadTurn?.ToString() ?? "null"}; leaning {Leans(one)}, {Leans(two)})");
            ctx.Check(applied?.NearestAfterKill == null && !Nearest(one) && !Nearest(two),
                $"and the decoded head rule after a kill ({Nearest(one)}, {Nearest(two)})");
        }
        finally
        {
            leaf?.Close();
            leaf?.Free();
            one.QueueFree();
            two.QueueFree();
            ctx.Host.RemoveChild(panes);
            panes.QueueFree();
            GameClock.Current = savedClock;
            OptionsStore.DirectoryOverride = previous;
        }

        ctx.Note($"the head turn and the targeting switch accepted over the pause reach every flying seat");
    }

    // The whole journey in one viewport: pause, strip, leaf, page, apply, return, resize.
    private static void Walk(TestContext ctx, MenuLayout layout, PauseSheet sheet)
    {
        var report = new StringBuilder();
        var view = new SubViewport
        {
            Size = FlightWindow,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        ctx.Host.AddChild(view);
        var reader = new MenuInput { Keyboard = false, Pads = Array.Empty<int>() };
        var pause = new PauseState();
        var board = OriginalPauseBoard.Build(
            pause, _ => reader, ctx.DataRoot, sheet,
            () => new PauseReadout(
                Array.Empty<PauseObjective>(), string.Empty, Array.Empty<PauseWorldIcon>()));
        OptionsApplyExit? applied = null;
        var leaf = PausePreferences.Build(ctx.DataRoot, layout, null, a => applied = a);
        ctx.Check(leaf != null, $"the leaf builds over the install's decoded layout");
        if (leaf == null)
        {
            ctx.Host.RemoveChild(view);
            view.QueueFree();
            return;
        }

        view.AddChild(board);
        view.AddChild(leaf);
        try
        {
            // The session's own wiring, restated: the sheet steps aside while the leaf stands and
            // comes back re-primed on its close, and neither of them touches the halt.
            board.Preferences = () =>
            {
                board.Visible = false;
                board.ProcessMode = Node.ProcessModeEnum.Disabled;
                leaf.Open(new[] { reader }, 0);
            };
            leaf.Closed += () =>
            {
                board.ProcessMode = Node.ProcessModeEnum.Inherit;
                board.Visible = pause.Paused;
                board.Reprime();
            };

            var pointer = OpenFromStrip(ctx, board, leaf, pause, sheet);
            string stepped = StepDisplayMode(ctx, leaf, report);
            AcceptChanges(ctx, board, leaf, pause, pointer, () => applied, stepped, report);
            HeldButtonOnReturn(ctx, board, leaf, pointer, report);
            ReopenOnApplied(ctx, board, leaf, applied, stepped, report);
            Resized(ctx, board, leaf, view, report);
        }
        finally
        {
            view.RemoveChild(leaf);
            leaf.QueueFree();
            view.RemoveChild(board);
            board.QueueFree();
            ctx.Host.RemoveChild(view);
            view.QueueFree();
        }

        ctx.WriteArtifact($"test-pause-preferences.txt", report.ToString());
    }

    // The pause, then the third strip pressed with the pointer, which is the press a player makes.
    // Answers the pointer cell the board reads, so the rest of the walk can hold the button down.
    private static Cell OpenFromStrip(
        TestContext ctx, OriginalPauseBoard board, PausePreferences leaf, PauseState pause, PauseSheet sheet)
    {
        var cell = new Cell();
        board.PointerSource = () => cell.At;
        pause.TryToggle(0);
        ctx.Check(board.Visible && !leaf.Visible, $"the pause raises the sheet with no leaf over it");
        var strip = sheet.Strips[PauseScreens.PreferencesRow];
        if (strip == null)
        {
            ctx.Check(false, $"the shared block authors the PREFERENCES strip");
            return cell;
        }

        cell.OnStrip = (strip.At.X + (PauseScreens.StripWidth / 2f), strip.At.Y + (PauseScreens.StripHeight / 2f));
        cell.At = (cell.OnStrip.X, cell.OnStrip.Y, true);
        board._Process(0.0);
        cell.At = (cell.OnStrip.X, cell.OnStrip.Y, false);
        board._Process(0.0);
        ctx.Check(leaf.Visible && !board.Visible,
            $"the release on PREFERENCES opens the leaf and takes the sheet away (leaf={leaf.Visible}, sheet={board.Visible})");
        ctx.Check(pause.Paused, $"with the mission still held, the leaf being a screen over the pause and not a resume");
        ctx.Check(leaf.Shell.Screen == OriginalScreen.Options,
            $"the leaf opens on the Options screen, the original's own door ({leaf.Shell.Screen})");
        ctx.Check(leaf.Shown is { } shown && shown.Lines.Count + shown.Plaques.Count > 0,
            $"and composes that screen rather than an empty board");
        return cell;
    }

    // Through the VIDEO door and one step along the display-mode row, the walk a player makes.
    // Answers the word the row now stands on.
    private static string StepDisplayMode(TestContext ctx, PausePreferences leaf, StringBuilder report)
    {
        WalkTo(leaf, OriginalOptionsScreen.VideoDoorKey);
        leaf.Drive(new MenuCommands { Accept = true });
        ctx.Check(leaf.Shell.Screen == OriginalScreen.Video,
            $"the VIDEO door behind the Options screen opens in flight ({leaf.Shell.Screen})");
        ctx.Check(leaf.Shell.Options.DisplayModeChoice == DisplayWords.Borderless
            && leaf.Shell.Options.ResolutionChoice == SavedResolution,
            $"on this machine's saved display settings ({leaf.Shell.Options.DisplayModeChoice ?? "unset"}, {leaf.Shell.Options.ResolutionChoice ?? "unset"})");
        string pinned = ResolutionSetting.ScreenSizes().Fallback;
        ctx.Check(leaf.Shell.Options.ResolutionPinned && RowOf(leaf, OriginalOptionsScreen.ResolutionKey) is { Enabled: false } dead
            && dead.Label == pinned,
            $"with the size row dead at this screen's own size, borderless owning it ({SizeRow(leaf)})");
        WalkTo(leaf, OriginalOptionsScreen.DisplayModeKey);
        leaf.Drive(new MenuCommands { MoveX = 1 });
        string stepped = leaf.Shell.Options.DisplayModeChoice ?? string.Empty;
        ctx.Check(stepped.Length > 0 && stepped != DisplayWords.Borderless,
            $"a sideways step on the Display Mode row picks another word ({stepped})");
        ctx.Check(!leaf.Shell.Options.ResolutionPinned && RowOf(leaf, OriginalOptionsScreen.ResolutionKey) is { Enabled: true } live
            && live.Label == SavedResolution,
            $"which hands the size row back, standing on the size saved all along ({SizeRow(leaf)})");
        report.AppendLine($"display mode: {DisplayWords.Borderless} stepped to {stepped}");
        report.AppendLine($"size row: dead at {pinned} under borderless, live at {SavedResolution} under {stepped}");
        return stepped;
    }

    // ACCEPT CHANGES with the mouse button held down, so the sheet's return meets a button that was
    // already pressed. The exit carries the stepped word and the options file is still untouched.
    private static void AcceptChanges(
        TestContext ctx, OriginalPauseBoard board, PausePreferences leaf, PauseState pause,
        Cell cell, Func<OptionsApplyExit?> applied, string stepped, StringBuilder report)
    {
        cell.At = (cell.OnStrip.X, cell.OnStrip.Y, true);
        WalkTo(leaf, OriginalOptionsScreen.VideoAcceptKey);
        leaf.Drive(new MenuCommands { Accept = true });
        var exit = applied();
        ctx.Check(exit != null && exit.DisplayMode == stepped,
            $"ACCEPT CHANGES hands the stepped mode to the apply ({exit?.DisplayMode ?? "no exit"})");
        ctx.Check(exit?.Resolution == SavedResolution,
            $"with every setting the page did not show riding out unchanged ({exit?.Resolution ?? "none"})");
        ctx.Check(!leaf.Visible && board.Visible,
            $"the apply closes the leaf back onto the sheet (leaf={leaf.Visible}, sheet={board.Visible})");
        ctx.Check(pause.Paused, $"and the mission is still the still frame the pause made of it");
        ctx.Check(OptionsStore.UserOptions().Load().DisplayMode == DisplayWords.Borderless,
            $"the leaf wrote no options file of its own, the exit being what carries the choice");
        var plan = DisplayModeSetting.Resolve(stepped);
        ctx.Check(plan.Source == "options.json" && plan.Mode != DisplayModeSetting.Resolve(DisplayWords.Borderless).Mode,
            $"and the apply resolves it to a window mode the flight was not in ({plan.Mode}, {plan.Source})");
        report.AppendLine($"apply exit: mode={exit?.DisplayMode ?? "-"} size={exit?.Resolution ?? "-"} plan={plan.Mode}");
    }

    // The returning sheet against a mouse button that never came up. Reprime is what makes the held
    // button old news; without it the first frame back reads it as a fresh click on the strip under
    // the pointer and opens the leaf again on its release.
    private static void HeldButtonOnReturn(
        TestContext ctx, OriginalPauseBoard board, PausePreferences leaf, Cell cell, StringBuilder report)
    {
        board._Process(0.0);
        ctx.Check(!leaf.Visible, $"a frame of the sheet with the button still down opens nothing");
        cell.At = (cell.OnStrip.X, cell.OnStrip.Y, false);
        board._Process(0.0);
        ctx.Check(!leaf.Visible,
            $"and letting that button up over PREFERENCES fires no strip, the press having begun inside the leaf");
        report.AppendLine($"held button on return: leaf stayed closed across the press and the release");
    }

    // The apply written the way Launcher.PersistOptions writes it, then the door opened again: the
    // page a pilot reopens in the same flight stands on what they just accepted.
    private static void ReopenOnApplied(
        TestContext ctx, OriginalPauseBoard board, PausePreferences leaf, OptionsApplyExit? applied,
        string stepped, StringBuilder report)
    {
        if (applied == null)
        {
            return;
        }

        var store = OptionsStore.UserOptions();
        var options = store.Load();
        options.DisplayMode = applied.DisplayMode;
        store.Save(options);
        board.Preferences?.Invoke();
        WalkTo(leaf, OriginalOptionsScreen.VideoDoorKey);
        leaf.Drive(new MenuCommands { Accept = true });
        ctx.Check(leaf.Shell.Options.DisplayModeChoice == stepped,
            $"the VIDEO page reopened in the same flight stands on the mode just applied ({leaf.Shell.Options.DisplayModeChoice ?? "unset"})");
        report.AppendLine($"reopened page: {leaf.Shell.Options.DisplayModeChoice ?? "-"}");
    }

    // A display change mid-flight resizes the viewport the flight draws into, and the leaf
    // and the sheet are both standing in it. Both must re-fit, and the pointer must go on hitting
    // the rows it is drawn over, which is a hit test against a fit read afresh every frame.
    private static void Resized(
        TestContext ctx, OriginalPauseBoard board, PausePreferences leaf, SubViewport view, StringBuilder report)
    {
        var row = RowOf(leaf, OriginalOptionsScreen.VideoAcceptKey);
        if (row == null)
        {
            ctx.Check(false, $"the VIDEO page draws the row the pointer is aimed at");
            return;
        }

        float authoredX = row.X + (row.Width / 2f);
        float authoredY = row.Y + (row.Height / 2f);
        var small = BoardFit.For(FlightWindow.X, FlightWindow.Y);
        var big = BoardFit.For(ResizedWindow.X, ResizedWindow.Y);
        var before = (small.OriginX + (authoredX * small.Scale), small.OriginY + (authoredY * small.Scale));
        var after = (big.OriginX + (authoredX * big.Scale), big.OriginY + (authoredY * big.Scale));
        ctx.Check(before != after,
            $"the two viewports letterbox the authored screen differently, so a stale fit cannot pass ({before} then {after})");

        leaf.WindowPointer = () => (before.Item1, before.Item2, false);
        leaf._Process(0.0);
        ctx.Check(HoverKey(leaf) == OriginalOptionsScreen.VideoAcceptKey,
            $"the pointer hits the row it is drawn over at {FlightWindow.X}x{FlightWindow.Y} ({HoverKey(leaf)})");

        view.Size = ResizedWindow;
        leaf._Process(0.0);
        ctx.Check(leaf.Size.X == ResizedWindow.X && leaf.Size.Y == ResizedWindow.Y,
            $"the resized viewport is the one the leaf covers ({leaf.Size})");
        ctx.Check(HoverKey(leaf) != OriginalOptionsScreen.VideoAcceptKey,
            $"the window pixel that hit that row no longer does, the screen having moved under it ({HoverKey(leaf)})");
        leaf.WindowPointer = () => (after.Item1, after.Item2, false);
        leaf._Process(0.0);
        ctx.Check(HoverKey(leaf) == OriginalOptionsScreen.VideoAcceptKey,
            $"and the pixel the new fit puts it at does, so the leaf re-fit rather than kept the old one ({HoverKey(leaf)})");
        ctx.Check(leaf.Shown is { } shown && shown.Lines.Count + shown.Plaques.Count > 0,
            $"with the page still composing at the new size");

        leaf.Drive(new MenuCommands { Back = true });
        leaf.Drive(new MenuCommands { Back = true });
        ctx.Check(!leaf.Visible && board.Visible,
            $"Back out of the page and out of the Options screen closes the leaf onto the sheet (leaf={leaf.Visible}, sheet={board.Visible})");
        board._Process(0.0);
        ctx.Check(board.Size.X == ResizedWindow.X && board.Size.Y == ResizedWindow.Y && board.Shown != null,
            $"and the sheet beneath it covers and composes at the new size too ({board.Size})");
        report.AppendLine(
            $"resize: {FlightWindow.X}x{FlightWindow.Y} -> {ResizedWindow.X}x{ResizedWindow.Y}, "
            + $"the ACCEPT CHANGES row moved from {before} to {after}");
    }

    // The walk over the pause: Options, the GAME OPTIONS door, the Auto Head Turn and Next Target
    // switches (pressed or passed by), then ACCEPT CHANGES, whose exit closes the leaf.
    private static void AcceptGameOptions(PausePreferences leaf, IReadOnlyList<FlightController> seats, bool toggle)
    {
        var reader = new MenuInput { Keyboard = false, Pads = Array.Empty<int>() };
        leaf.Open(new[] { reader, reader }, 0, seats);
        WalkTo(leaf, OriginalOptionsScreen.GameOptionsDoorKey);
        leaf.Drive(new MenuCommands { Accept = true });
        if (toggle)
        {
            WalkTo(leaf, OriginalOptionsScreen.AutoHeadTurnKey);
            leaf.Drive(new MenuCommands { Accept = true });
            WalkTo(leaf, OriginalOptionsScreen.NearestAfterKillKey);
            leaf.Drive(new MenuCommands { Accept = true });
        }

        WalkTo(leaf, OriginalOptionsScreen.GameOptionsAcceptKey);
        leaf.Drive(new MenuCommands { Accept = true });
    }

    // One unhalted frame of each seat's view step, the one that asks the head for its idle aim.
    // The sim step is left out so the body rates the seats were given are the turn it reads.
    private static void Frame(GameClock clock, IReadOnlyList<FlightController> seats)
    {
        clock.BeginFrame(1f / 60f);
        foreach (var seat in seats)
        {
            seat._Process(1f / 60f);
        }
    }

    private static bool Nearest(FlightController seat) => seat.Targeting?.NearestAfterKill == true;

    private static bool Leans(FlightController seat) =>
        seat.Head is { } head && (Mathf.Abs(head.TargetAzimuth) > 1e-3f || Mathf.Abs(head.TargetElevation) > 1e-3f);

    // The target the decoded lead names for the turn every seat is given, the shipped airframe's
    // three autohead values applied.
    private static (float Elevation, float Azimuth)? Expected(FlightController seat) =>
        seat.Stats is { } stats
            ? HeadLook.AutoheadTarget(SeatBodyRates,
                stats.AutoheadTurnTime, stats.AutoheadTurnMax, stats.AutoheadTurnMinPitch)
            : null;

    private static bool OnTurn(FlightController seat) =>
        Expected(seat) is { } want && seat.Head is { } head
        && Mathf.Abs(head.TargetElevation - want.Elevation) < 1e-4f
        && Mathf.Abs(head.TargetAzimuth - want.Azimuth) < 1e-4f;

    private static string Target(FlightController seat) =>
        seat.Head is { } head ? $"({head.TargetElevation:0.####}, {head.TargetAzimuth:0.####})" : "no head";

    // One human seat in the cockpit over its own camera, airborne and turning to starboard and
    // climbing, so an autohead that is on has a turn to lead into, holding the selection a launch
    // with nothing saved builds. No roster: the head, its gate and the two fields are the subject.
    private static FlightController CockpitSeat(TestContext ctx, SubViewport panes, PlaneStats stats, int index)
    {
        var camera = new Camera3D();
        panes.AddChild(camera);
        var model = new Node3D { Name = $"HeadTurnSeat{index}Model" };
        var seat = new FlightController
        {
            PlaneModel = model,
            PlayerIndex = index,
            IsHumanPiloted = true,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            PinnedViewMode = PilotViewMode.Cockpit,
            Targeting = new TargetSelection(),
            Name = $"HeadTurnSeat{index}",
        };
        seat.AddChild(model);
        var spawn = new Vector3(index * 60f, 800f, 0f);
        var flight = new FlightModel(stats);
        seat.Setup(flight, camera, new CamParams(), spawn, spawn + Vector3.Forward);
        flight.Speed = 60f;
        flight.BodyRates = SeatBodyRates;
        ctx.Host.AddChild(seat);
        return seat;
    }

    // Walks the page's focus onto a key, the keyboard's own way across a screen.
    private static void WalkTo(PausePreferences leaf, string key)
    {
        for (int guard = 0; guard < 32 && leaf.Shell.FocusedKey != key; guard++)
        {
            leaf.Drive(new MenuCommands { MoveY = 1 });
        }
    }

    // The size row as one line, for a check that has to say what it saw rather than only that it
    // disagreed: the size drawn and whether the row takes a press.
    private static string SizeRow(PausePreferences leaf) =>
        RowOf(leaf, OriginalOptionsScreen.ResolutionKey) is { } row
            ? $"{row.Label}, enabled={row.Enabled}" : "no size row";

    private static OriginalRow? RowOf(PausePreferences leaf, string key)
    {
        foreach (var row in leaf.Shell.Rows)
        {
            if (row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    private static string HoverKey(PausePreferences leaf)
    {
        var rows = leaf.Shell.Rows;
        int hover = leaf.Shell.Hover;
        return hover >= 0 && hover < rows.Count ? rows[hover].Key : "none";
    }

    // The pointer the board and the leaf read, held in one place so a press can outlive the screen
    // it began on, which is the whole point of the held-button check.
    private sealed class Cell
    {
        public (float X, float Y, bool Pressed)? At { get; set; }

        public (float X, float Y) OnStrip { get; set; }
    }
}
