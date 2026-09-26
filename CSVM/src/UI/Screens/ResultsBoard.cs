using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The shared shell of the results boards (<see cref="VersusBoard"/>, <see cref="StuntRaceBoard"/>,
/// <see cref="StuntScoreboard"/>, <see cref="IaWrapupBoard"/>): the dimmed backdrop and centred
/// panel, the board palette and label factories, the halt-and-retire contract on the sim clock,
/// and the standard Photo Mode · Restart · Exit menu. A subclass keeps only its Build signature,
/// its completion event, its Populate content and <see cref="StillEnded"/>.
/// <see cref="PauseBoard"/> shares the chrome statics but not the shell: pausing is a held clock,
/// not an ended run, so its lifecycle follows <see cref="PauseState"/> instead of this contract.
/// </summary>
public abstract partial class ResultsBoard : Control
{
    // The share of the window an over-tall panel is shrunk into. TUNE.
    internal const float PanelRoom = 0.96f;

    // The shared board style: one palette so every board reads as the same screen. All TUNE.
    internal static readonly Color TitleColor = new(0.93f, 0.96f, 1f);
    internal static readonly Color ContextColor = new(0.60f, 0.75f, 0.95f);
    internal static readonly Color HeaderColor = new(0.50f, 0.62f, 0.80f);
    internal static readonly Color RowColor = new(0.86f, 0.89f, 0.94f);

    private PauseState _state = null!;
    private string _exitLabel = "";
    private System.Func<int, MenuInput> _inputFor = null!;
    private CenterContainer _center = null!;
    private PanelContainer? _panel;
    private BoardMenuHost? _host;
    private StuntShotStrip? _strip;
    private ShotViewer _viewer = null!;

    /// <summary>Rerun the ended mode in place, chosen from the menu (or R / pad Y where the mode
    /// offers them directly).</summary>
    public System.Action? Restart { get; set; }

    /// <summary>Leave the session, chosen from the menu.</summary>
    public System.Action? Exit { get; set; }

    /// <summary>Hand player 1's pane to a free camera over the frozen world, chosen from the menu.
    /// The halt is never dropped and no result is spent, so this is the one row that leaves the
    /// board's own state exactly as it found it.</summary>
    public System.Action? PhotoMode { get; set; }

    /// <summary>The standard menu while it is up, or null, the test harness's way to drive the
    /// activation routing without a device.</summary>
    internal BoardMenu? StandardMenu => _host?.Menu;

    /// <summary>The board's photographs while it carries any, the second cursor region above the
    /// menu, for the suites.</summary>
    internal StuntShotStrip? ShotStrip => _strip;

    /// <summary>The full-size photograph viewer the board opens over itself.</summary>
    internal ShotViewer Viewer => _viewer;

    /// <summary>Whether the menu rows draw the highlight, false while the cursor is on the
    /// photographs.</summary>
    internal bool RowsShowCursor => _host?.View.ShowsCursor == true;

    /// <summary>Whether the run this board reported is still over. A live flag here (the match's
    /// <c>Completed</c>, the race's <c>AllFinished</c>) makes a rerun retire the board from
    /// <c>_Process</c>; a board nothing retires answers true and overrides
    /// <see cref="OnRestartChosen"/> instead.</summary>
    protected abstract bool StillEnded { get; }

    public sealed override void _Process(double delta)
    {
        // Track the window (resizable) so the backdrop always covers it.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        FitPanel();
        // A rerun clears the subclass's live flag, retire the board and release the clock until
        // the next run ends. R and pad Y reach the rerun without the menu, so the release belongs
        // here rather than only on the menu's own Restart.
        if (Visible && !StillEnded)
        {
            Retire();
            return;
        }
        if (Visible)
            _host?.Poll((float)delta, input => HandleShots(input.Move, input.MoveX, input.Accept, input.Back));
    }

    /// <summary>The whole-window shell under any board: hides it, ignores input focus, and adds
    /// the dimmed backdrop plus the returned centre container.</summary>
    internal static CenterContainer BuildShell(Control board)
    {
        board.MouseFilter = MouseFilterEnum.Ignore;
        board.FocusMode = FocusModeEnum.None;
        board.Visible = false;
        board.SetAnchorsPreset(LayoutPreset.FullRect);

        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f), MouseFilter = MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        board.AddChild(backdrop);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        board.AddChild(center);
        return center;
    }

    /// <summary>The styled board panel: frees <paramref name="panel"/> if it exists, builds a
    /// fresh one under <paramref name="center"/> and returns its body VBox.</summary>
    internal static VBoxContainer RebuildPanel(CenterContainer center, ref PanelContainer? panel, float s)
    {
        if (panel != null)
        {
            center.RemoveChild(panel);
            panel.QueueFree();
        }

        panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.94f),
            BorderColor = new Color(0.34f, 0.48f, 0.72f),
            ContentMarginLeft = 34f * s,
            ContentMarginRight = 34f * s,
            ContentMarginTop = 24f * s,
            ContentMarginBottom = 22f * s,
        };
        style.SetBorderWidthAll(Mathf.RoundToInt(2f * s));
        style.SetCornerRadiusAll(Mathf.RoundToInt(6f * s));
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", Mathf.RoundToInt(7f * s));
        panel.AddChild(body);
        return body;
    }

    internal static Label Label(string text, int fontSize, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 1);
        return l;
    }

    // Wraps a control in a CenterContainer so it centres in the body's full width.
    internal static CenterContainer Centered(Control c)
    {
        var cc = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        cc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cc.AddChild(c);
        return cc;
    }

    internal static void AddCell(GridContainer grid, string text, int fontSize, Color color,
        HorizontalAlignment align, int minWidth)
    {
        var l = Label(text, fontSize, color);
        l.HorizontalAlignment = align;
        l.CustomMinimumSize = new Vector2(minWidth, 0);
        grid.AddChild(l);
    }

    internal static HSeparator Separator(float s)
    {
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", Mathf.RoundToInt(8f * s));
        return sep;
    }

    /// <summary>One frame of player 1's cursor where the photographs can take it, answering whether
    /// they did; the menu rows read the frame otherwise. An open viewer takes every frame, and
    /// confirm or back closes it. On the grid the cursor walks the landed cells, confirm opens one
    /// and back returns to the rows; up from the first row enters the grid, down out of its last
    /// row lands on the first row again. Back is Escape as well as pad B here, since the pause key
    /// does nothing while a results board is up.</summary>
    internal bool HandleShots(int move, int moveX, bool accept, bool back)
    {
        if (_viewer.IsOpen)
        {
            if (accept || back)
            {
                CloseViewer();
            }

            return true;
        }

        if (_strip is not { } strip || _host == null)
        {
            return false;
        }

        var cursor = strip.Cursor;
        if (cursor.OnGrid)
        {
            if (back || cursor.Step(moveX, move) == ShotGridStep.Left)
            {
                LeaveGrid();
                return true;
            }

            strip.Refresh();
            if (accept && strip.Selected is { } shot)
            {
                _viewer.Open(shot);
            }

            return true;
        }

        // A relayout that took the cell away leaves the cursor on no region; the rows take it back.
        if (!_host.View.ShowsCursor)
        {
            LeaveGrid();
        }

        // ⚠ Only from the first row: the resting row stays Photo Mode, and the grid is reached by
        // stepping up off it, the way the photographs stand above the menu.
        if (move < 0 && _host.Menu.Index == 0 && cursor.Enter())
        {
            ShowGridCursor();
            return true;
        }

        return false;
    }

    /// <summary>The board's own state for the Build methods: hidden, input-transparent, full-rect,
    /// with the backdrop and centre container underneath. Call once from the subclass Build.</summary>
    protected void InitShell(PauseState state, bool exitsToMenu, System.Func<int, MenuInput> inputFor)
    {
        _state = state;
        _exitLabel = exitsToMenu ? "Exit to Menu" : "Quit Game";
        _inputFor = inputFor;
        _center = BuildShell(this);
        _viewer = ShotViewer.Build(clickCloses: true);
        _viewer.Dismissed += CloseViewer;
        AddChild(_viewer);
    }

    /// <summary>Shows the populated board and stops the world under it, rather than leaving the
    /// pilots to fly on beneath a screen that has already counted the run.</summary>
    protected void Wake()
    {
        Visible = true;
        _state.Raise(HaltReason.Ended);
    }

    /// <summary>Hides the board, drops its menu and releases the sim clock.</summary>
    protected void Retire()
    {
        Visible = false;
        _host = null;
        _viewer.Close();
        _state.Clear(HaltReason.Ended);
    }

    // The board spans the whole window, not a pane, so it scales on the window height alone
    // (no HudMetrics pane damping, which is for HUD elements drawn inside a pane). The one
    // per-pane board, StuntScoreboard, overrides this.
    protected virtual float BoardScale() => Mathf.Max(0.5f, Size.Y > 0f ? Size.Y / 720f : 1f);

    /// <summary>Frees any previous panel, builds the styled panel at scale <paramref name="s"/>
    /// and returns its body. Populate implementations start here.</summary>
    protected VBoxContainer BeginPanel(float s)
    {
        _strip = null;
        _viewer.Close();
        return RebuildPanel(_center, ref _panel, s);
    }

    /// <summary>Appends the run's photographs as a <see cref="StuntShotStrip"/> and takes them as
    /// the cursor's second region, above the standard menu. Null <paramref name="shots"/> adds
    /// nothing.</summary>
    protected void AddShotStrip(VBoxContainer body, StuntCapture? shots, float s)
    {
        _strip = StuntShotStrip.Add(body, shots, s);
        if (_strip != null)
        {
            _strip.CellPointed += OnCellPointed;
            _strip.CellClicked += OnCellClicked;
        }
    }

    /// <summary>Appends the standard non-dismissable Photo Mode · Restart · Exit menu, driven by
    /// player 1: an ended run is a session-wide decision, and no single player raised the board.</summary>
    protected void AddStandardMenu(VBoxContainer body, float s)
    {
        var menu = new BoardMenu(
            dismissable: false,
            (BoardMenuItem.Photo, "Photo Mode"),
            (BoardMenuItem.Restart, "Restart"),
            (BoardMenuItem.Exit, _exitLabel));
        menu.Activated += OnActivated;
        _host = BoardMenuHost.Build(menu, _inputFor(0), s, legend: true);
        body.AddChild(_host.View);
    }

    // The default leaves retiring to StillEnded clearing, so R and pad Y behave exactly like the
    // menu row. A board with no live flag retires here instead.
    protected virtual void OnRestartChosen() => Restart?.Invoke();

    private void OnActivated(BoardMenuItem item)
    {
        if (item == BoardMenuItem.Photo)
        {
            // ⚠ Ahead of the Exit test and returning: the tail of this method is the Restart path.
            PhotoMode?.Invoke();
            return;
        }
        if (item == BoardMenuItem.Exit)
        {
            Exit?.Invoke();
            return;
        }
        OnRestartChosen();
    }

    // The pointer puts the cursor on a landed cell it comes over; a pending one refuses it.
    private void OnCellPointed(int cell)
    {
        if (!_viewer.IsOpen && _strip != null && _strip.Cursor.MoveTo(cell))
        {
            ShowGridCursor();
        }
    }

    private void OnCellClicked(int cell)
    {
        if (!_viewer.IsOpen && _strip != null && _strip.Cursor.MoveTo(cell) && _strip.Selected is { } shot)
        {
            ShowGridCursor();
            _viewer.Open(shot);
        }
    }

    // Closing leaves the cursor on the cell the photograph was opened from.
    private void CloseViewer()
    {
        _viewer.Close();
        _strip?.Refresh();
    }

    private void ShowGridCursor()
    {
        if (_host != null)
        {
            _host.View.ShowsCursor = false;
            _host.View.Refresh();
        }

        _strip?.Refresh();
    }

    // Back onto the menu's first row, Photo Mode, the harmless one.
    private void LeaveGrid()
    {
        _strip?.Cursor.Leave();
        _strip?.Refresh();
        if (_host != null)
        {
            _host.Menu.MoveTo(0);
            _host.View.ShowsCursor = true;
            _host.View.Refresh();
        }
    }

    // A long course's splits and photographs stand taller than the window, and the centre
    // container grows downward with them, taking the total and the menu off the bottom. So the
    // container is re-centred on the window and the panel shrunk about its own centre to fit.
    private void FitPanel()
    {
        if (_panel == null || _panel.Size.X <= 0f || _panel.Size.Y <= 0f)
        {
            return;
        }

        _center.Position = (Size - _center.Size) / 2f;
        var room = Size * PanelRoom;
        float fit = Mathf.Min(1f, Mathf.Min(room.X / _panel.Size.X, room.Y / _panel.Size.Y));
        _panel.PivotOffset = _panel.Size / 2f;
        _panel.Scale = new Vector2(fit, fit);
    }
}
