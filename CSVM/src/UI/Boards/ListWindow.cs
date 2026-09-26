using System;

namespace CSVM.UI.Boards;

/// <summary>
/// A scrolled list as a pointer sees it, in the board's authored pixels: the window's box, where
/// a wheel step moves the window by rows; the thumb's box on its track, where a held pointer
/// drags the window in proportion; and the window's place in the list. Each list widget builds
/// one from its own geometry, and the presentation that owns the pointer decides what a new top
/// writes back, so the arrows and the keyboard keep their own rules untouched.
/// </summary>
public readonly record struct ListWindow(
    float X, float Y, float Width, float Height,
    float ThumbX, float ThumbY, float ThumbWidth, float ThumbHeight,
    float TrackTop, float TrackHeight,
    int Count, int Rows, int Top)
{
    /// <summary>Whether the list is longer than its window, which is when a wheel or a drag has
    /// anything to move.</summary>
    public bool Scrolls => Count > Rows;

    /// <summary>The furthest top the window can stand at.</summary>
    public int LastTop => Math.Max(0, Count - Rows);

    /// <summary>Whether an authored point lies inside the window's box.</summary>
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    /// <summary>Whether an authored point lies on the thumb, which only a scrolling list has.</summary>
    public bool OnThumb(float x, float y) =>
        Scrolls && x >= ThumbX && x < ThumbX + ThumbWidth && y >= ThumbY && y < ThumbY + ThumbHeight;

    /// <summary>The top a wheel of <paramref name="steps"/> rows lands on, clamped.</summary>
    public int TopAfterWheel(int steps) => Math.Clamp(Top + steps, 0, LastTop);

    /// <summary>The top a drag begun with the window at <paramref name="startTop"/> lands on once
    /// the pointer has moved <paramref name="dy"/> pixels: the thumb's free run down the track
    /// maps onto the rows the window can move, rounded to the nearest row.</summary>
    public int TopAfterDrag(int startTop, float dy)
    {
        float run = TrackHeight - ThumbHeight;
        if (run <= 0f || LastTop == 0)
        {
            return Math.Clamp(startTop, 0, LastTop);
        }

        return Math.Clamp(startTop + (int)Math.Round(dy * LastTop / run), 0, LastTop);
    }

    /// <summary>How tall a thumb stands on a track of <paramref name="trackHeight"/>: the share of
    /// the list its window shows, never shorter than <paramref name="floor"/> (the scroll tile's own
    /// height, below which the thumb stops reading as a grip) and never longer than the track.
    /// A list whose window is not smaller than itself gets the whole track.</summary>
    public static float ThumbHeightFor(float trackHeight, int rows, int count, float floor)
    {
        float least = Math.Min(trackHeight, floor);
        return count <= 0 || rows <= 0
            ? least
            : Math.Clamp(trackHeight * rows / count, least, trackHeight);
    }

    /// <summary>Where a thumb stands on its track for a window at <paramref name="top"/>: at
    /// the head unscrolled, flush at the foot on the last row.</summary>
    public static float ThumbYFor(float trackTop, float trackHeight, float thumbHeight, int top, int lastTop) =>
        trackTop + (Math.Max(0f, trackHeight - thumbHeight) * top / Math.Max(1, lastTop));
}
