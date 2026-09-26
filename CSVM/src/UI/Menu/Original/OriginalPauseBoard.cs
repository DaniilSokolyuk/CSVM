using System;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The Original presentation's pause screen: the mission's chart filling the window with its flags
/// and icons, the objectives parchment, the profile's memento and the button strips, the authored
/// four and the remake's photo strip among them, composed from <c>escape.zrd</c> the way the load
/// and briefing screens are. An Instant Action sortie hands it an <c>ia_escape.zrd</c> sheet
/// instead, the load screen's blackboard under the same strips, written in chalk. Follows
/// <see cref="PauseState.Changed"/> and drives its cursor from the pausing player's reader alone,
/// which is <see cref="PauseBoard"/>'s contract unchanged; that seat's pointer shares the cursor,
/// hovering a strip to move it and clicking to fire, the rule every Original page keeps. The
/// Built-in presentation keeps <see cref="PauseBoard"/>. Decode: docs/org/pause-screen.md.
/// </summary>
public sealed partial class OriginalPauseBoard : Control
{
    private PauseState _state = null!;
    private Func<int, MenuInput> _inputFor = null!;
    private Func<PauseReadout> _readout = null!;
    private PauseSheet _sheet = null!;
    private string _dataRoot = "";
    private ComposedBoardView? _view;
    private BoardMenu? _menu;
    private MenuInput? _input;

    // The pointer as the sheet last saw it, in authored pixels, null while nobody is pointing at
    // it; the strip a press took hold of, -1 for none; and whether that strip is drawing held.
    private (float X, float Y)? _pointer;
    private int _armed = -1;
    private bool _held;
    private bool _wasPressed;

    // What the mouse mode was when the sheet went up, so the resume puts back what the flight was
    // using rather than assuming it was visible.
    private Input.MouseModeEnum _mouseMode = Input.MouseModeEnum.Visible;
    private bool _tookCursor;

    /// <summary>Rerun the running mission in place, chosen from RESTART.</summary>
    public Action? Restart { get; set; }

    /// <summary>Leave the session, chosen from QUIT.</summary>
    public Action? Exit { get; set; }

    /// <summary>Hand the pausing player's pane to a free camera over the halted world, chosen from
    /// PHOTO MODE, the one strip the original does not author. The session suspends this sheet for
    /// the duration and brings it back on Escape; the halt is never dropped, so the world stays the
    /// still frame it already is.</summary>
    public Action? PhotoMode { get; set; }

    /// <summary>Open the options over the pause, chosen from PREFERENCES: the session hides this
    /// sheet, stands <see cref="PausePreferences"/> over the held world and calls
    /// <see cref="Reprime"/> when it closes. ⚠ Null where the install carries no decoded layout for
    /// the leaf to compose from; the strip is authored and drawn either way, and a null action makes
    /// the press a no-op rather than a crash.</summary>
    public Action? Preferences { get; set; }

    /// <summary>The pointer in the board's authored 800x600 space and whether its button is down,
    /// or null for no pointer this frame. Defaults to the pausing seat's mouse through the same fit
    /// the view draws at; a suite replaces it to move the pointer by hand.</summary>
    public Func<(float X, float Y, bool Pressed)?> PointerSource { get; set; } = () => null;

    /// <summary>The screen as last composed, for a suite that reads what is drawn rather than a
    /// screenshot of it.</summary>
    public ComposedBoard? Shown => _view?.Board;

    /// <summary>The strip the cursor stands on, or -1 before the first pause.</summary>
    public int FocusedRow => _menu?.Index ?? -1;

    /// <summary>Builds the (hidden) board over a loaded sheet and subscribes to the shared pause
    /// state. <paramref name="readout"/> is read afresh on each pause, since the objectives, the
    /// memento and the icons all follow the running mission.</summary>
    public static OriginalPauseBoard Build(
        PauseState state,
        Func<int, MenuInput> inputFor,
        string dataRoot,
        PauseSheet sheet,
        Func<PauseReadout> readout)
    {
        var board = new OriginalPauseBoard
        {
            _state = state,
            _inputFor = inputFor,
            _dataRoot = dataRoot,
            _sheet = sheet,
            _readout = readout,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
            Visible = false,
        };
        board.PointerSource = board.SeatPointer;
        board.SetAnchorsPreset(LayoutPreset.FullRect);
        state.Changed += board.OnChanged;
        return board;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // The view sizes itself off the viewport, so it cannot be populated before it is in the
        // tree; the first pause is what composes anything.
        _view = ComposedBoardView.Build(_dataRoot);
        AddChild(_view);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        _state.Changed -= OnChanged;
        GiveCursorBack();
    }

    /// <summary>Re-reads the pointer's button and drops any strip a press had taken hold of, the
    /// session's call when a screen that stood over this sheet closes. ⚠ Without it the button
    /// still down from the leaf's own last click reads as a fresh press on the strip the pointer
    /// happens to be over, which fires that strip the instant the sheet comes back.</summary>
    public void Reprime()
    {
        _pointer = null;
        _armed = -1;
        _held = false;
        _wasPressed = PointerSource()?.Pressed ?? false;
        if (Visible)
        {
            Compose();
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        if (!Visible || _menu == null || _input == null)
        {
            return;
        }

        _input.Poll((float)delta);

        // PadBack only: Escape and Start already reach the pause toggle through FlightController,
        // so reading the combined back here would act twice.
        bool changed = _menu.Handle(_input.Move, _input.Accept, _input.PadBack);
        if (Visible)
        {
            changed |= StepPointer();
        }

        if (changed && Visible)
        {
            Compose();
        }
    }

    private void OnChanged()
    {
        if (_state.Paused)
        {
            Populate();
            TakeCursor();
        }
        else
        {
            GiveCursorBack();
        }

        Visible = _state.Paused;
    }

    private void OnActivated(BoardMenuItem item)
    {
        switch (item)
        {
            case BoardMenuItem.Resume:
                _state.ForceResume();
                break;
            case BoardMenuItem.Photo:
                PhotoMode?.Invoke();    // the halt stays: photo mode is a still frame, not a resume
                break;
            case BoardMenuItem.Restart:
                _state.ForceResume();   // the rerun runs against a live clock, not a held one
                Restart?.Invoke();
                break;
            case BoardMenuItem.Preferences:
                Preferences?.Invoke();
                break;
            case BoardMenuItem.Exit:
                Exit?.Invoke();
                break;
        }
    }

    // A fresh menu each pause, so the cursor starts on RESUME and a stray confirm on a board that
    // just appeared cannot restart or leave the session. The owner's own reader drives it, and the
    // pointer's button is read once here so one already down is not a fresh click on the next frame.
    private void Populate()
    {
        var menu = new BoardMenu(
            dismissable: true,
            (BoardMenuItem.Resume, _sheet.ButtonLabels[PauseScreens.ResumeRow]),
            (BoardMenuItem.Photo, _sheet.ButtonLabels[PauseScreens.PhotoRow]),
            (BoardMenuItem.Restart, _sheet.ButtonLabels[PauseScreens.RestartRow]),
            (BoardMenuItem.Preferences, _sheet.ButtonLabels[PauseScreens.PreferencesRow]),
            (BoardMenuItem.Exit, _sheet.ButtonLabels[PauseScreens.QuitRow]));
        menu.Activated += OnActivated;
        menu.Dismissed += () => _state.ForceResume();
        _menu = menu;
        _input = _inputFor(_state.OwnerPlayerIndex);
        _input.Prime();
        _pointer = null;
        _armed = -1;
        _held = false;
        _wasPressed = PointerSource()?.Pressed ?? false;
        Compose();
    }

    // One frame of the pointer over the strips: standing on one moves the shared cursor there,
    // a press takes hold of the strip it lands on, and that strip fires when the button comes up
    // still on it, so a press released anywhere else fires nothing. Answers whether to repaint.
    private bool StepPointer()
    {
        if (PointerSource() is not { } at)
        {
            bool had = _pointer != null || _held;
            _pointer = null;
            _armed = -1;
            _held = false;
            return had;
        }

        bool changed = _pointer != (at.X, at.Y);
        _pointer = (at.X, at.Y);
        int over = PauseScreens.RowAt(_sheet, at.X, at.Y);
        if (over >= 0)
        {
            changed |= _menu!.MoveTo(over);
        }

        bool clicked = at.Pressed && !_wasPressed;
        bool released = !at.Pressed && _wasPressed;
        _wasPressed = at.Pressed;
        if (clicked && over >= 0)
        {
            _armed = over;
        }

        bool held = _armed >= 0 && at.Pressed && over == _armed;
        changed |= held != _held;
        _held = held;
        if (!released)
        {
            return changed;
        }

        bool fires = _armed >= 0 && over == _armed;
        _armed = -1;
        if (!fires)
        {
            return changed;
        }

        // The hover already moved the cursor onto this strip, so the shared confirm fires the row
        // under the pointer and the pad and the pointer reach the actions through one path.
        _menu!.Handle(0, accept: true, back: false);
        return true;
    }

    // The pausing seat's mouse in authored pixels. Only a seat that reads the keyboard holds one
    // (MenuInput's rule for seat 0), so a pad player's pause is driven by the pad alone.
    private (float X, float Y, bool Pressed)? SeatPointer()
    {
        if (_input is not { Keyboard: true } || !IsInsideTree())
        {
            return null;
        }

        var size = GetViewportRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        var at = GetViewport().GetMousePosition();
        return (
            (at.X - fit.OriginX) / fit.Scale,
            (at.Y - fit.OriginY) / fit.Scale,
            Input.IsMouseButtonPressed(MouseButton.Left));
    }

    // The dialog authors its own pointer, so the OS one goes away while the sheet stands. Taken
    // only where the seat has a mouse to point with and the dialog a cursor to draw in its place.
    private void TakeCursor()
    {
        if (_tookCursor || _sheet.State.Cursor == null || _input is not { Keyboard: true })
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

    // A pad confirm is an edge and the action it runs takes the board away in the same frame, so
    // the cursor's row wears the rollover strip; the held frame is the pointer's alone, drawn while
    // its button is down on the strip it took hold of.
    private void Compose() =>
        _view?.Show(
            PauseScreens.For(_sheet, _readout(), _menu?.Index ?? 0, _held, _pointer),
            _sheet.InstantAction ? BoardPalette.EscapeBlackboard : BoardPalette.Escape,
            string.Empty,
            string.Empty);
}
