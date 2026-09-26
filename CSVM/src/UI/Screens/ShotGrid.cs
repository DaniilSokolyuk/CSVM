using System;

namespace CSVM.UI.Screens;

/// <summary>What one cursor step on a <see cref="ShotGridCursor"/> did.</summary>
public enum ShotGridStep
{
    /// <summary>Nothing selectable lay that way, so the cursor stayed on its cell.</summary>
    Stayed,

    /// <summary>The cursor moved onto another cell.</summary>
    Moved,

    /// <summary>The step went down past the grid's last row: the cursor is off the grid.</summary>
    Left,
}

/// <summary>
/// The Danger Zone photographs' grid rule, shared by the Original wrap-up page's prints and the
/// built-in boards' strip: the fewest rows whose pictures come within <see cref="Slack"/> of the
/// widest any grid in the room allows, capped at a maximum picture width, and the widest of those.
/// A few shots therefore read as one strip and a long course wraps before its thumbnails shrink.
/// Engine-free, so the rule unit-tests.
/// </summary>
public static class ShotGrid
{
    /// <summary>How much narrower than the widest possible a grid's pictures may be and still win
    /// by having fewer rows. A look, not a decode.</summary>
    public const float Slack = 0.85f;

    /// <summary>The grid for <paramref name="count"/> pictures in a <paramref name="width"/> by
    /// <paramref name="height"/> room: columns and picture width. <paramref name="aspect"/> is a
    /// picture's height over its width; <paramref name="gap"/> stands between two cells and
    /// <paramref name="padX"/>/<paramref name="padY"/> is what a cell adds round its picture (a
    /// print's border, a caption). No pictures, or no room, answers one column of width 0.</summary>
    public static (int Columns, float Picture) Fit(
        int count, float width, float height, float aspect, float gap, float padX, float padY, float maxPicture)
    {
        if (count <= 0 || aspect <= 0f)
        {
            return (1, 0f);
        }

        var pictures = new float[count];
        float widest = 0f;
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            float across = ((width - ((columns - 1) * gap)) / columns) - padX;
            float down = (((height - ((rows - 1) * gap)) / rows) - padY) / aspect;
            pictures[columns - 1] = Math.Max(0f, Math.Min(maxPicture, Math.Min(across, down)));
            widest = Math.Max(widest, pictures[columns - 1]);
        }

        (int Columns, int Rows, float Picture) best = (1, int.MaxValue, 0f);
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            float picture = pictures[columns - 1];
            if (picture > 0f && picture >= widest * Slack
                && (rows < best.Rows || (rows == best.Rows && picture > best.Picture)))
            {
                best = (columns, rows, picture);
            }
        }

        return (best.Columns, best.Picture);
    }
}

/// <summary>
/// A cursor over a grid of cells in reading order, some of which may refuse it (a photograph whose
/// frame has not landed). Off the grid it answers -1, which is where a board's menu rows hold the
/// cursor instead. Sideways steps walk the selectable cells in reading order without wrapping; an
/// upward or downward step takes the nearest selectable cell by column in the next row that has
/// one, and a downward step past the last such row leaves the grid. Engine-free, so the walk
/// unit-tests.
/// </summary>
public sealed class ShotGridCursor
{
    private int _count;
    private int _columns = 1;
    private Func<int, bool> _selectable = _ => false;

    /// <summary>The cell under the cursor, or -1 while it is off the grid.</summary>
    public int Cell { get; private set; } = -1;

    /// <summary>Whether the cursor is on a cell.</summary>
    public bool OnGrid => Cell >= 0;

    /// <summary>Whether any cell would take the cursor.</summary>
    public bool AnySelectable
    {
        get
        {
            for (int i = 0; i < _count; i++)
            {
                if (_selectable(i))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private int RowCount => (_count + _columns - 1) / _columns;

    /// <summary>Sets the grid the cursor walks. A cursor left on a cell outside it, or on one that
    /// no longer takes the cursor, leaves the grid.</summary>
    public void Layout(int count, int columns, Func<int, bool> selectable)
    {
        _count = Math.Max(0, count);
        _columns = Math.Max(1, columns);
        _selectable = selectable ?? throw new ArgumentNullException(nameof(selectable));
        if (Cell >= _count || (Cell >= 0 && !_selectable(Cell)))
        {
            Cell = -1;
        }
    }

    /// <summary>Comes onto the grid from below, the way an upward step off a menu under it does:
    /// the leftmost selectable cell of the last row that has one. False, and still off the grid,
    /// when no cell is selectable.</summary>
    public bool Enter()
    {
        int rows = RowCount;
        for (int row = rows - 1; row >= 0; row--)
        {
            int cell = NearestInRow(row, 0);
            if (cell >= 0)
            {
                Cell = cell;
                return true;
            }
        }

        return false;
    }

    /// <summary>Puts the cursor on <paramref name="cell"/>, the pointer's way on. Refused, leaving
    /// the cursor where it was, for a cell outside the grid or one that does not take it.</summary>
    public bool MoveTo(int cell)
    {
        if (cell < 0 || cell >= _count || !_selectable(cell))
        {
            return false;
        }

        Cell = cell;
        return true;
    }

    /// <summary>Takes the cursor off the grid.</summary>
    public void Leave() => Cell = -1;

    /// <summary>One cursor step while on the grid: <paramref name="dx"/> walks the reading order,
    /// <paramref name="dy"/> the rows. A vertical step wins a frame that carries both.</summary>
    public ShotGridStep Step(int dx, int dy)
    {
        if (Cell < 0 || (dx == 0 && dy == 0))
        {
            return ShotGridStep.Stayed;
        }

        if (dy != 0)
        {
            int column = Cell % _columns;
            int direction = Math.Sign(dy);
            for (int row = (Cell / _columns) + direction; row >= 0 && row < RowCount; row += direction)
            {
                int cell = NearestInRow(row, column);
                if (cell >= 0)
                {
                    Cell = cell;
                    return ShotGridStep.Moved;
                }
            }

            if (direction > 0)
            {
                Cell = -1;
                return ShotGridStep.Left;
            }

            return ShotGridStep.Stayed;
        }

        int step = Math.Sign(dx);
        for (int i = Cell + step; i >= 0 && i < _count; i += step)
        {
            if (_selectable(i))
            {
                Cell = i;
                return ShotGridStep.Moved;
            }
        }

        return ShotGridStep.Stayed;
    }

    // The selectable cell of one row nearest a column, the left one on a tie, or -1.
    private int NearestInRow(int row, int column)
    {
        int best = -1;
        int bestDistance = int.MaxValue;
        for (int c = 0; c < _columns; c++)
        {
            int cell = (row * _columns) + c;
            if (cell >= _count || !_selectable(cell))
            {
                continue;
            }

            int distance = Math.Abs(c - column);
            if (distance < bestDistance)
            {
                best = cell;
                bestDistance = distance;
            }
        }

        return best;
    }
}
