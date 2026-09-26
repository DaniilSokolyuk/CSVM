using System;

namespace CSVM.UI.Boards;

/// <summary>
/// A continuous control's track as a pointer sees it, in the board's authored pixels: the slot the
/// thumb slides along, the thumb's own size, and the whole numbers the slot spans. The thumb's
/// middle follows the pointer, so <see cref="ValueAt"/> and <see cref="ThumbX"/> are one reading
/// taken in two directions and the drawn thumb sits under the finger that moved it. Every answer
/// is clamped into the range and none wraps: a track whose ends are silence and full volume must
/// not cross between them in one step. Each slider widget builds one from its own geometry, and
/// the presentation that owns the pointer decides what a new value writes back.
/// </summary>
public readonly record struct SliderTrack(
    float X, float Y, float Width, float Height,
    float ThumbWidth, float ThumbHeight, int Min, int Max)
{
    /// <summary>The thumb's line, centred on the slot, which is where its art is drawn. The
    /// authored slot is three pixels tall and the thumb twenty-one, so the thumb stands well
    /// clear of the slot on both sides.</summary>
    public float ThumbY => Y + ((Height - ThumbHeight) / 2f);

    /// <summary>How far the thumb's left edge travels between the two ends.</summary>
    public float Run => Math.Max(0f, Width - ThumbWidth);

    /// <summary>Whether the track spans anything, which a slot narrower than its own thumb, or a
    /// range of one value, does not.</summary>
    public bool Spans => Run > 0f && Max > Min;

    /// <summary>A value clamped into the track's own range.</summary>
    public int Clamp(int value) => Math.Clamp(value, Min, Max);

    /// <summary>The value a pointer at authored <paramref name="x"/> names: the thumb's middle
    /// under the pointer, rounded to the nearer whole number and clamped, so pressing anywhere
    /// past an end of the slot reaches that end.</summary>
    public int ValueAt(float x)
    {
        if (!Spans)
        {
            return Min;
        }

        float t = (x - X - (ThumbWidth / 2f)) / Run;
        return Clamp(Min + (int)Math.Round(t * (Max - Min)));
    }

    /// <summary>The thumb's left edge for a value.</summary>
    public float ThumbX(int value) =>
        Spans ? X + (Run * (Clamp(value) - Min) / (Max - Min)) : X;

    /// <summary>The value a sideways step of <paramref name="step"/> in
    /// <paramref name="direction"/> lands on, clamped at both ends rather than wrapped.</summary>
    public int Stepped(int value, int direction, int step) => Clamp(Clamp(value) + (direction * step));
}
