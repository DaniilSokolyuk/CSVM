namespace CSVM.UI.Boards;

/// <summary>
/// The player chip strip both presentations put in a screen's top-right corner once a second seat
/// has joined: <c>P1</c> to <c>P4</c>, each in that player's own identity colour, so a pilot finds
/// the same tag in the same place whichever shell they are in. The tag and the colour themselves
/// are <c>SplitScreen.PlayerTag</c> and <c>SplitScreen.PlayerColor</c>; this is only the shape the
/// two strips share, in authored board points scaled through the screen's own <see cref="BoardFit"/>.
/// Built-in lays its chips out with Godot's own text measurement and reads the face and the inset
/// alone; Original composes at authored coordinates with no font metric at hand, so it centres each
/// chip in a fixed <see cref="Pitch"/> cell and draws its own ground behind them.
/// </summary>
public static class SeatStrip
{
    /// <summary>The chip face, in authored board points.</summary>
    public const float Font = 16f;

    /// <summary>The strip's inset from the screen's top and right edges, in authored points.</summary>
    public const float Inset = 14f;

    /// <summary>One chip's cell, the pitch a strip lays them out on when it cannot measure its own
    /// text. ⚠ Keep four cells clear of the scrapbook's Current Mission tab, whose art ends at
    /// authored x 672: at this pitch and inset the ground starts at 679.</summary>
    public const float Pitch = 26f;

    /// <summary>The ground's margin around the chips, in authored points.</summary>
    public const float Pad = 3f;

    /// <summary>The ground's height, the face plus room for its descenders and the margin.</summary>
    public const float Ground = Font + 4f + (2f * Pad);

    /// <summary>The ink seat <paramref name="index"/>'s chip takes. It resolves to the player's
    /// identity colour rather than to the screen's palette, since the four colours must read the
    /// same on a paper form as on a painted panel.</summary>
    public static BoardInk Ink(int index) => index switch
    {
        0 => BoardInk.Seat1,
        1 => BoardInk.Seat2,
        2 => BoardInk.Seat3,
        _ => BoardInk.Seat4,
    };
}
