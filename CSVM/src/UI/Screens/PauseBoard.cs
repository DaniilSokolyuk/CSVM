using System;
using System.Collections.Generic;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The shared pause board and its menu, <see cref="ResultsBoard"/>'s chrome on the same
/// WHOLE-window CanvasLayer shape, since pausing stops the game for everybody at once, but not its
/// shell: pausing is a held clock, not an ended run, so this board follows
/// <see cref="PauseState.Changed"/> instead of the halt-and-retire contract. Names the pausing
/// player (their own <see cref="SplitScreen.PlayerColor"/>) and hands them the cursor:
/// <see cref="PauseState"/> lets only that player resume, so only that player's pad drives the
/// menu. Everyone else's input is ignored while it is up rather than fighting over a cursor whose
/// Restart and Exit decide the whole session.
/// </summary>
public sealed partial class PauseBoard : Control
{
    // Base metrics at 720p (scaled by window height), mirrors VersusBoard so a shared board reads
    // as the same screen whichever one is up. All TUNE.
    private const int TitleFont = 30;
    private const int ContextFont = 18;

    private PauseState _state = null!;
    private string _exitLabel = "";
    private Func<int, MenuInput> _inputFor = null!;
    private CenterContainer _center = null!;
    private PanelContainer? _panel;
    private BoardMenuHost? _host;

    /// <summary>Rerun the running mode in place, chosen from the menu.</summary>
    public Action? Restart { get; set; }

    /// <summary>Leave the session, chosen from the menu.</summary>
    public Action? Exit { get; set; }

    /// <summary>Hand the pausing player's pane to a free camera over the frozen world, chosen from
    /// the menu. The session suspends this board for the duration and brings it back on Escape;
    /// the halt is never dropped, so the world stays the still frame it already is.</summary>
    public Action? PhotoMode { get; set; }

    /// <summary>Open the options over the pause, the same <see cref="PausePreferences"/> leaf the
    /// Original sheet's PREFERENCES strip opens. ⚠ Null leaves the row off the menu rather than
    /// offering a row that does nothing, which is why it is read at every fresh pause and set
    /// before the board sees one.</summary>
    public Action? Preferences { get; set; }

    /// <summary>Builds the (hidden) board and subscribes to the shared pause state. Add it to a
    /// CanvasLayer above the splitscreen panes; it wakes on <see cref="PauseState.Changed"/> and
    /// hides itself the same way. <paramref name="inputFor"/> answers with a player's own menu
    /// reader, which is what binds the cursor to the pauser.</summary>
    public static PauseBoard Build(PauseState state, bool exitsToMenu, Func<int, MenuInput> inputFor)
    {
        var board = new PauseBoard
        {
            _state = state,
            _exitLabel = exitsToMenu ? "Exit to Menu" : "Quit Game",
            _inputFor = inputFor,
        };
        board._center = ResultsBoard.BuildShell(board);
        state.Changed += board.OnChanged;
        return board;
    }

    public override void _ExitTree() => _state.Changed -= OnChanged;

    public override void _Process(double delta)
    {
        // Track the window (resizable) so the backdrop always covers it, VersusBoard's same rule.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;

        if (Visible)
            _host?.Poll((float)delta);
    }

    private void OnChanged()
    {
        if (_state.Paused)
            Populate();
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
            case BoardMenuItem.Preferences:
                Preferences?.Invoke();  // and so is the options leaf, which stands over this board
                break;
            case BoardMenuItem.Restart:
                _state.ForceResume();   // the rerun runs against a live clock, not a held one
                Restart?.Invoke();
                break;
            case BoardMenuItem.Exit:
                Exit?.Invoke();
                break;
        }
    }

    private void Populate()
    {
        // Whole window, not a pane, scales on window height alone, same reason VersusBoard does.
        float s = Mathf.Max(0.5f, Size.Y > 0f ? Size.Y / 720f : 1f);
        var body = ResultsBoard.RebuildPanel(_center, ref _panel, s);

        // OwnerPlayerIndex is captured once here (Populate runs only from a fresh pause), so the
        // board keeps naming the player who paused even if ownership were ever cleared underneath it.
        int owner = _state.OwnerPlayerIndex;
        string ownerTag = SplitScreen.PlayerTag(owner);
        var ownerColor = SplitScreen.PlayerColor(owner);

        body.AddChild(ResultsBoard.Centered(ResultsBoard.Label("PAUSED", (int)(TitleFont * s), ResultsBoard.TitleColor)));
        body.AddChild(ResultsBoard.Centered(ResultsBoard.Label($"{ownerTag} paused", (int)(ContextFont * s), ownerColor)));

        // A fresh menu each pause: the cursor starts on Resume, so a stray confirm on a board that
        // just appeared cannot restart or leave the session. The owner's own reader drives it, and
        // the options row stands only where a leaf was built for it to open.
        var rows = new List<(BoardMenuItem Item, string Label)>
        {
            (BoardMenuItem.Resume, "Resume"),
            (BoardMenuItem.Photo, "Photo Mode"),
        };
        if (Preferences != null)
        {
            rows.Add((BoardMenuItem.Preferences, "Preferences"));
        }

        rows.Add((BoardMenuItem.Restart, "Restart"));
        rows.Add((BoardMenuItem.Exit, _exitLabel));
        var menu = new BoardMenu(dismissable: true, rows.ToArray());
        menu.Activated += OnActivated;
        menu.Dismissed += () => _state.ForceResume();
        // No control hints under it, since the original's pause sheet carries none.
        _host = BoardMenuHost.Build(menu, _inputFor(owner), s, legend: false);
        body.AddChild(_host.View);
    }
}
