using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The Preferences leaf over a paused mission: the Original presentation's Options screen with the
/// Game Options, AUDIO, VIDEO and rebinding pages behind its doors, hosted over the held world
/// rather than over the menu. Either pause board's PREFERENCES opens it, the pausing player's own
/// reader drives it with that seat's mouse as its pointer, and every way out (RETURN TO MAIN MENU,
/// Back, or a page's ACCEPT CHANGES) closes it back onto the sheet with what it applied already in
/// force. The halt is never touched here: this is a screen over the pause, not a resume. The
/// display rows are the same <see cref="DisplaySettingRows"/> both Options screens draw.
/// Decode: docs/org/pause-screen.md.
/// </summary>
public sealed partial class PausePreferences : Control
{
    private readonly List<MenuInput?> _pollers = new();
    private readonly List<FlightController> _flying = new();
    private OriginalShell _shell = null!;
    private ControlsFeature? _controls;
    private MenuControlsSeats? _controlsSeats;
    private IMenuAudio? _audio;
    private Action<OptionsApplyExit>? _applied;
    private string _dataRoot = string.Empty;
    private BoardPalette _palette = BoardPalette.Chalk;
    private ComposedBoardView? _view;
    private PointerSeat? _seat;
    private MenuInput? _input;

    // Wheel steps turned since the seat last read them, positive toward a list's foot: the wheel is
    // an event and never a held state, so it is counted rather than polled.
    private int _wheel;

    // What the mouse mode was when the leaf went up, so closing it puts back what stood behind it:
    // the Original sheet has already hidden the OS cursor, Built-in's board has not.
    private Input.MouseModeEnum _mouseMode = Input.MouseModeEnum.Visible;
    private bool _tookCursor;

    /// <summary>The leaf has closed and the pause sheet beneath it belongs back on screen. Raised
    /// once per close, by every door out.</summary>
    public event Action? Closed;

    /// <summary>The pointer in WINDOW pixels and whether its primary button is down, or null for no
    /// pointer this frame. Defaults to the driving seat's own mouse, mapped into the authored space
    /// through the same <see cref="BoardFit"/> the view draws at; a suite replaces it to point by
    /// hand.</summary>
    public Func<(float X, float Y, bool Pressed)?> WindowPointer { get; set; } = () => null;

    /// <summary>The hosted shell, for a suite that reads the page back or drives it by key.</summary>
    public OriginalShell Shell => _shell;

    /// <summary>The screen as last composed, for a suite that reads what is drawn rather than a
    /// screenshot of it.</summary>
    public ComposedBoard? Shown => _view?.Board;

    /// <summary>Builds the (hidden) leaf over <paramref name="layout"/>, or answers null where the
    /// install carries no decoded layout, which leaves both boards' PREFERENCES with nothing behind
    /// it. <paramref name="controls"/> is the shared rebinding feature, so an in-flight rebind edits
    /// the maps the menu edits and saves through the one writer; <paramref name="applied"/> takes
    /// every accepted page's settings, and <paramref name="audio"/> sounds its cues.</summary>
    public static PausePreferences? Build(
        string dataRoot,
        MenuLayout? layout,
        ControlsFeature? controls,
        Action<OptionsApplyExit> applied,
        IMenuAudio? audio = null)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(applied);
        if (layout == null)
        {
            return null;
        }

        var leaf = new PausePreferences
        {
            _dataRoot = dataRoot,
            _audio = audio,
            _applied = applied,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
            Visible = false,
        };
        // ⚠ The two features are this leaf's own throwaways, never the menu host's. The Preferences
        // family reads neither, and lending it the live ones would let a screen over the pause
        // disturb the roster and the picks a return to the menu stands on.
        leaf._shell = new OriginalShell(
            layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            new OriginalArtSizes(dataRoot, "pause preferences").Measure,
            dataRoot: dataRoot,
            options: () => OptionsStore.UserOptions().Load(),
            screenSizes: ResolutionSetting.ScreenSizes,
            screens: MonitorSetting.Screens,
            controls: controls);
        leaf._controls = controls;
        leaf._controlsSeats = controls == null ? null : new MenuControlsSeats(controls);
        leaf._palette = OriginalPresentation.PaletteFor(leaf._shell.PreferencesInks, leaf._shell.Inks);
        leaf.WindowPointer = leaf.SeatPointer;
        leaf.SetAnchorsPreset(LayoutPreset.FullRect);
        return leaf;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // The view sizes itself off the viewport, so it cannot draw before it is in the tree; the
        // first open is what composes anything.
        _view = ComposedBoardView.Build(_dataRoot);
        AddChild(_view);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        StopFeedingSeats();
        GiveCursorBack();
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (Visible && @event is InputEventMouseButton { Pressed: true } wheel)
        {
            _wheel += wheel.ButtonIndex == MouseButton.WheelDown ? 1
                : wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 0;
        }
    }

    /// <summary>Raises the leaf on the Options screen, the original's own door out of the pause.
    /// <paramref name="pollers"/> is one reader per seated player in order, which the rebinding pages
    /// register their rows from; <paramref name="owner"/> is the player who paused, whose reader
    /// drives the cursor. <paramref name="flying"/> is every seat behind the leaf, which an accepted
    /// rebinding page puts on its keymap and scheme (<see cref="FeedSeats"/>) and any accepted page
    /// on its head turn and targeting switch (<see cref="FeedGameOptions"/>), both at once.</summary>
    public void Open(IReadOnlyList<MenuInput> pollers, int owner, IReadOnlyList<FlightController>? flying = null)
    {
        ArgumentNullException.ThrowIfNull(pollers);
        if (pollers.Count == 0)
        {
            return;
        }

        _pollers.Clear();
        foreach (var poller in pollers)
        {
            _pollers.Add(poller);
        }

        StopFeedingSeats();
        if (flying != null)
        {
            _flying.AddRange(flying);
        }

        if (_controls != null)
        {
            _controls.Accepted += FeedSeats;
        }

        _input = pollers[Math.Clamp(owner, 0, pollers.Count - 1)];
        _seat = new PointerSeat(new BuiltInSeat(_input), PointerAt, PointerDown, TakeWheel);
        // Primed, and the wheel drained with it: a click still held from the strip that opened this
        // leaf must not read as a fresh press on the row the page opens under it.
        _seat.Prime();
        _controlsSeats?.Sync(_pollers);
        _shell.Open(OriginalScreen.Options);
        TakeCursor();
        Visible = true;
        Compose();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        if (!Visible || _seat == null || _view == null)
        {
            return;
        }

        // Read afresh every frame rather than kept: a display change accepted on the VIDEO page
        // resizes the window the flight draws into, and this leaf keeps standing over it.
        var size = GetViewportRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        // Before the poll, so a page capturing a control reads the press as a binding and not as a
        // menu command; and again after it, so a page opened this frame is what the next poll reads.
        _seat.CapturingText = _shell.CapturingText;
        var commands = _seat.Poll((float)delta);
        if (commands.Pointer is { } pointer)
        {
            commands = commands with
            {
                Pointer = pointer with
                {
                    X = (pointer.X - fit.OriginX) / fit.Scale,
                    Y = (pointer.Y - fit.OriginY) / fit.Scale,
                },
            };
        }

        bool changed = Drive(commands);
        if (!Visible)
        {
            return;
        }

        _seat.CapturingText = _shell.CapturingText;
        bool picture = _view.AdvanceMovies(delta);
        picture |= _view.AdvanceCaret(delta);
        if (changed)
        {
            Compose();
        }
        else if (picture)
        {
            _view.QueueRedraw();
        }
    }

    /// <summary>One frame of commands applied to the hosted shell, the pointer in AUTHORED pixels:
    /// its cues sound, an accepted page's settings reach the injected writer, and leaving the
    /// Preferences family by any door closes the leaf. Answers whether the picture changed. The
    /// engine path calls it from <see cref="_Process"/>; a suite calls it to press by hand.</summary>
    public bool Drive(MenuCommands commands)
    {
        SyncControlsSeats();
        var step = _shell.Step(commands);
        foreach (string cue in step.Cues)
        {
            _audio?.Cue(new MenuCue(cue));
        }

        // The AUDIO page's levels are heard while it stands and the mix it opened over goes back
        // the moment it is left, by any door, exactly as the menu's own host does it.
        if (_shell.Options.AudioPreviewMix is { } mix)
        {
            _audio?.PreviewMix(mix, _shell.Options.TakeAudioMoved());
        }
        else
        {
            _audio?.EndMixPreview();
        }

        SyncControlsSeats();
        if (step.Exit is OptionsApplyExit applied)
        {
            // ⚠ End the preview before the apply, never after, the close below ending it too. A
            // restore running last puts the page's opening mix back over the accepted levels, heard
            // then only at the next start.
            _audio?.EndMixPreview();
            _applied?.Invoke(applied);
            FeedGameOptions(applied);
            Close();
            return false;
        }

        // Any other exit is the shell asking to leave the menu, which over a flight means nothing
        // but "close"; so does the top level, which is where RETURN TO MAIN MENU and Back land.
        if (step.Exit != null || _shell.Screen == OriginalScreen.TopLevel)
        {
            Close();
            return false;
        }

        return step.Changed;
    }

    /// <summary>Closes the leaf without applying anything, the session's own way to take it away
    /// (a mission that ends underneath it, a restart). Idempotent.</summary>
    public void Close()
    {
        if (!Visible)
        {
            return;
        }

        _audio?.EndMixPreview();
        if (_seat != null)
        {
            _seat.CapturingText = false;
        }

        _seat = null;
        _input = null;
        _pollers.Clear();
        StopFeedingSeats();
        GiveCursorBack();
        Visible = false;
        Closed?.Invoke();
    }

    // The rebinding pages' player rows, in step with the seats this leaf was opened over. Kept off
    // every other screen, since nothing else reads them and the sync walks the whole roster.
    private void SyncControlsSeats()
    {
        if (_shell.Screen is OriginalScreen.ControlsPrefs or OriginalScreen.Keys)
        {
            _controlsSeats?.Sync(_pollers);
        }
    }

    // An accepted rebinding page reaching the flight behind the leaf. The page stages from the saved
    // file, not from the seats, so without this push a seat keeps the keymap and scheme it was built
    // on until a restart rebuilds it. The seat a keymap file names is the human one numbered by it.
    private void FeedSeats(int player, BindingProfile profile)
    {
        foreach (var seat in _flying)
        {
            if (IsInstanceValid(seat) && seat.IsHumanPiloted && seat.PlayerIndex + 1 == player)
            {
                seat.ApplyProfile(profile);
            }
        }
    }

    // The Auto Head Turn and Next Target switches reaching the flight behind the leaf, the head turn
    // taking effect mid-mission as the original's does. One saved value serves every human seat and
    // both are read per frame or per kill, so the resume flies them. A never-set value falls back as
    // a launch with nothing saved does: the head turn to the headLook.autohead config key, the
    // targeting switch to the decoded head rule. The selection is written in place, never rebuilt,
    // so the pilot keeps the cycle and lock the pause stood over. Difficulty takes the next sortie.
    private void FeedGameOptions(OptionsApplyExit applied)
    {
        foreach (var seat in _flying)
        {
            if (IsInstanceValid(seat) && seat.IsHumanPiloted)
            {
                seat.AutoHeadTurn = applied.AutoHeadTurn;
                if (seat.Targeting is { } targeting)
                {
                    targeting.NearestAfterKill = applied.NearestAfterKill == true;
                }
            }
        }
    }

    // ⚠ The feature is the menu's and outlives this flight, so the listener comes off on every close
    // and at teardown: left on, a rebind accepted later from the menu would reach freed seats.
    private void StopFeedingSeats()
    {
        if (_controls != null)
        {
            _controls.Accepted -= FeedSeats;
        }

        _flying.Clear();
    }

    private (float X, float Y)? PointerAt() =>
        WindowPointer() is { } at ? (at.X, at.Y) : null;

    private bool PointerDown() => WindowPointer()?.Pressed ?? false;

    private int TakeWheel()
    {
        int steps = _wheel;
        _wheel = 0;
        return steps;
    }

    // The driving seat's mouse in window pixels. Only a seat that reads the keyboard holds one
    // (MenuInput's rule for seat 0), so a pad player's pause leaf is driven by the pad alone.
    private (float X, float Y, bool Pressed)? SeatPointer()
    {
        if (_input is not { Keyboard: true } || !IsInsideTree())
        {
            return null;
        }

        var at = GetViewport().GetMousePosition();
        return (at.X, at.Y, Input.IsMouseButtonPressed(MouseButton.Left));
    }

    // The pages draw the original's own pointer, so the OS one goes away while the leaf stands and
    // whatever stood before it comes back on the close, sheet-hidden or flight-visible alike.
    private void TakeCursor()
    {
        if (_tookCursor || _input is not { Keyboard: true })
        {
            return;
        }

        _mouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Hidden;
        _tookCursor = true;
    }

    private void GiveCursorBack()
    {
        if (!_tookCursor)
        {
            return;
        }

        Input.MouseMode = MouseCapture.Restorable(_mouseMode);
        _tookCursor = false;
    }

    private void Compose() =>
        _view?.Show(_shell.Compose(), _palette, string.Empty, string.Empty);
}
