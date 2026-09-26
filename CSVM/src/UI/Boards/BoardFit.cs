using System;

namespace CSVM.UI.Boards;

/// <summary>
/// How the original's fixed 800x600 dialog space lands on an arbitrary window. A campaign board
/// places every element at its authored pixel coordinate and maps it through one of these, so the
/// composition stays the original's and only the scale changes: one uniform scale on both axes,
/// the board centred, the remainder letterboxed. The two rejected alternatives and why the
/// authored art must never be resampled smoothly are in <c>docs/org/campaign-board.md</c>.
/// </summary>
public readonly record struct BoardFit(float Scale, float OriginX, float OriginY)
{
    /// <summary>The authored dialog's width. Every campaign background bitmap is exactly this
    /// wide, which is what fixes the space (<c>docs/org/campaign-board.md</c>).</summary>
    public const int AuthoredWidth = 800;

    /// <summary>The authored dialog's height, and with the width the 4:3 the composition
    /// assumes: the button plaques hang off the bottom edge and only sit there at this ratio.</summary>
    public const int AuthoredHeight = 600;

    /// <summary>The fit for a viewport of the given size. A viewport with no area at all (a
    /// window being restored) falls back to 1:1 rather than a zero scale nothing can draw at.</summary>
    public static BoardFit For(float viewportWidth, float viewportHeight)
    {
        float scale = Math.Min(viewportWidth / AuthoredWidth, viewportHeight / AuthoredHeight);
        if (!(scale > 0f))
        {
            scale = 1f;
        }

        return new BoardFit(
            scale,
            (viewportWidth - (AuthoredWidth * scale)) / 2f,
            (viewportHeight - (AuthoredHeight * scale)) / 2f);
    }

    /// <summary>An authored x in viewport pixels.</summary>
    public float X(float authoredX) => OriginX + (authoredX * Scale);

    /// <summary>An authored y in viewport pixels.</summary>
    public float Y(float authoredY) => OriginY + (authoredY * Scale);

    /// <summary>An authored width, height or font size in viewport pixels.</summary>
    public float Length(float authored) => authored * Scale;
}
