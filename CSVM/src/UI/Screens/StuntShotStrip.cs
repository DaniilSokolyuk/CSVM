using System.Collections.Generic;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// A stunt run's Danger Zone photographs as a board section, shared by
/// <see cref="StuntScoreboard"/> and <see cref="IaWrapupBoard"/>: a rule and one captioned
/// thumbnail per shot in marker order, laid out by <see cref="ShotGrid"/> so a long course wraps
/// into rows rather than shrinking every thumbnail. It follows its <see cref="StuntCapture"/> while
/// it is in the tree: a shot latched after the board was drawn joins the grid, and a cell drawn
/// before its frame landed is filled on <see cref="StuntCapture.ShotLanded"/>. <see cref="Cursor"/>
/// is the board's second cursor region, over the landed cells only. Hidden while the run has no
/// shot to draw.
/// </summary>
public sealed partial class StuntShotStrip : VBoxContainer
{
    /// <summary>The thumbnail grid's node name, so a suite can read the strip's own cells rather
    /// than pattern-matching the labels of the split table above it.</summary>
    internal const string StripName = "shot_strip";

    // Base metrics at 720p, scaled by the board's own scale: the room the grid is fitted into, the
    // gap between cells, and the caption under each picture. All TUNE.
    private const int CaptionFont = 12;
    private const float StripWidth = 640f;
    private const float StripHeight = 300f;
    private const float StripGap = 6f;
    private const float CaptionHeight = 20f;
    private const float PendingAspect = 3f / 4f;

    // The cursor's mark on a cell, BoardMenuView's own highlight gold.
    private static readonly Color FocusColor = new(1f, 0.86f, 0.38f);

    // Cells drawn before their shot landed, filled by OnShotLanded.
    private readonly Dictionary<StuntShot, (TextureRect Picture, float Width)> _pending = new();

    // The cells in grid order: the shot each one shows, its picture's focus frame and its caption.
    private readonly List<(StuntShot Shot, ReferenceRect Mark, Label Caption)> _cells = new();

    private StuntCapture _shots = null!;
    private GridContainer _grid = null!;
    private float _scale;

    // Whether the grid was laid out on a landed picture's own proportions rather than the tune.
    private bool _measured;

    /// <summary>A cell the pointer came over, by grid index.</summary>
    public event System.Action<int>? CellPointed;

    /// <summary>A cell the pointer pressed and let go on, by grid index.</summary>
    public event System.Action<int>? CellClicked;

    /// <summary>The cursor over the grid's landed cells, which the board drives.</summary>
    public ShotGridCursor Cursor { get; } = new();

    /// <summary>The grid's columns as last laid out.</summary>
    public int Columns => _grid.Columns;

    /// <summary>How many cells the grid draws.</summary>
    public int CellCount => _cells.Count;

    /// <summary>The shot under the cursor, or null while it is off the grid.</summary>
    public StuntShot? Selected => Cursor.Cell >= 0 && Cursor.Cell < _cells.Count ? _cells[Cursor.Cell].Shot : null;

    /// <summary>Appends the section to <paramref name="body"/> at scale <paramref name="s"/> and
    /// returns it. Null <paramref name="shots"/> adds nothing; a run with none yet adds the hidden
    /// section, which shows once a shot latches.</summary>
    public static StuntShotStrip? Add(VBoxContainer body, StuntCapture? shots, float s)
    {
        if (shots == null)
        {
            return null;
        }

        var strip = new StuntShotStrip { _shots = shots, _scale = s };
        strip.AddThemeConstantOverride("separation", Mathf.RoundToInt(7f * s));
        strip.AddChild(ResultsBoard.Separator(s));
        strip._grid = new GridContainer { Name = StripName, MouseFilter = MouseFilterEnum.Ignore };
        strip._grid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(StripGap * s));
        strip._grid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(StripGap * s));
        strip.AddChild(ResultsBoard.Centered(strip._grid));
        strip.Rebuild();
        body.AddChild(strip);
        return strip;
    }

    /// <summary>The grid for <paramref name="count"/> thumbnails of <paramref name="aspect"/>
    /// (height over width) at board scale <paramref name="s"/>: columns and thumbnail width.</summary>
    public static (int Columns, float Width) Grid(int count, float aspect, float s) =>
        ShotGrid.Fit(count, StripWidth * s, StripHeight * s, aspect, StripGap * s, 0f, CaptionHeight * s,
            StuntCapture.ThumbWidth * s);

    public override void _EnterTree()
    {
        _shots.ShotLatched += OnShotLatched;
        _shots.ShotLanded += OnShotLanded;
    }

    public override void _ExitTree()
    {
        _shots.ShotLatched -= OnShotLatched;
        _shots.ShotLanded -= OnShotLanded;
    }

    /// <summary>Redraws which cell carries the cursor's mark.</summary>
    public void Refresh()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            bool focused = i == Cursor.Cell;
            _cells[i].Mark.Visible = focused;
            _cells[i].Caption.AddThemeColorOverride("font_color", focused ? FocusColor : ResultsBoard.HeaderColor);
        }
    }

    /// <summary>The pointer's two gestures on a cell, for a suite standing in for the mouse.</summary>
    internal void Point(int cell) => CellPointed?.Invoke(cell);

    /// <inheritdoc cref="Point"/>
    internal void Click(int cell) => CellClicked?.Invoke(cell);

    private static void Fill(TextureRect picture, Image thumb, float width)
    {
        picture.Texture = ImageTexture.CreateFromImage(thumb);
        picture.CustomMinimumSize = new Vector2(width, width * thumb.GetHeight() / Mathf.Max(1, thumb.GetWidth()));
    }

    // Marker order rather than the flown order the splits use: the grid is the course, and a
    // pilot reads it against the markers they know.
    private void Rebuild()
    {
        var selected = Selected;
        foreach (var cell in _grid.GetChildren())
        {
            _grid.RemoveChild(cell);
            cell.QueueFree();
        }
        _pending.Clear();
        _cells.Clear();

        var shots = new List<StuntShot>();
        float aspect = 0f;
        foreach (var shot in _shots.InMarkerOrder())
        {
            if (shot.Landed && shot.Thumb == null)
            {
                continue;
            }

            shots.Add(shot);
            if (aspect <= 0f && shot.Thumb is { } thumb && thumb.GetWidth() > 0)
            {
                aspect = (float)thumb.GetHeight() / thumb.GetWidth();
            }
        }

        _measured = aspect > 0f;
        aspect = _measured ? aspect : PendingAspect;
        var (columns, width) = Grid(shots.Count, aspect, _scale);
        _grid.Columns = columns;
        for (int i = 0; i < shots.Count; i++)
        {
            _grid.AddChild(Cell(shots[i], i, width, aspect));
        }

        Cursor.Layout(_cells.Count, columns, i => _cells[i].Shot.Landed && _cells[i].Shot.Thumb != null);
        if (selected != null)
        {
            Cursor.MoveTo(_cells.FindIndex(c => c.Shot == selected));
        }

        Refresh();
        Visible = _cells.Count > 0;
    }

    // One cell: the picture (empty until its frame lands) with the cursor's frame over it, and
    // the marker's caption. The cell itself takes the pointer, its parts none.
    private VBoxContainer Cell(StuntShot shot, int index, float width, float aspect)
    {
        var cell = new VBoxContainer { MouseFilter = MouseFilterEnum.Stop };
        var picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        cell.AddChild(picture);
        if (shot.Thumb is { } thumb)
        {
            Fill(picture, thumb, width);
        }
        else
        {
            // The run's last crossing usually wakes the board before its frame has landed.
            picture.CustomMinimumSize = new Vector2(width, width * aspect);
            _pending[shot] = (picture, width);
        }

        var mark = new ReferenceRect
        {
            BorderColor = FocusColor,
            BorderWidth = Mathf.Max(2f, 2f * _scale),
            EditorOnly = false,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        mark.SetAnchorsPreset(LayoutPreset.FullRect);
        picture.AddChild(mark);

        var caption = ResultsBoard.Label(shot.DzName, Mathf.Max(8, (int)(CaptionFont * _scale)), ResultsBoard.HeaderColor);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        cell.AddChild(caption);

        cell.MouseEntered += () => CellPointed?.Invoke(index);
        // The release reaches the cell the press landed on wherever it happens, so one let go off
        // the cell confirms nothing, the launchscreen's own pointer rule.
        cell.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up
                && new Rect2(Vector2.Zero, cell.Size).HasPoint(up.Position))
            {
                CellClicked?.Invoke(index);
            }
        };
        _cells.Add((shot, mark, caption));
        return cell;
    }

    // Every cell may resize and the grid may take another column, so it is drawn again rather
    // than appended to.
    private void OnShotLatched(StuntShot shot) => Rebuild();

    private void OnShotLanded(StuntShot shot)
    {
        if (!_pending.Remove(shot, out var cell) || !IsInstanceValid(cell.Picture))
        {
            return;
        }

        if (shot.Thumb is { } thumb && _measured)
        {
            Fill(cell.Picture, thumb, cell.Width);
        }
        else
        {
            // A frame that never arrived leaves the grid, the cells after it moving up one, and the
            // first to arrive under a tuned layout sets the grid's real proportions.
            Rebuild();
        }
    }
}
