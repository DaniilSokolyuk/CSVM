using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>The action pairs a full-axis binding drives, and which member of each is the positive
/// side. Positive is the end a pad's shipped half-axis puts on raw positive travel: pulled back
/// pitches up, pushed right rolls right. An uninverted stick therefore flies the way the pad does.
/// Yaw follows roll and throttle names its own up end, since the pad has no axis for either.
/// ⚠ One table for the map, the store and capture. A pair read differently in any of them would
/// resolve a stick backwards on one path only.</summary>
public static class AxisPairs
{
    /// <summary>Every pair as (positive, negative).</summary>
    public static IReadOnlyList<(InputAction Positive, InputAction Negative)> All { get; } = new[]
    {
        (InputAction.PitchUp, InputAction.PitchDown),
        (InputAction.RollRight, InputAction.RollLeft),
        (InputAction.YawRight, InputAction.YawLeft),
        (InputAction.ThrottleUp, InputAction.ThrottleDown),
    };

    /// <summary>The other action of that action's pair, or null for an action in no pair.</summary>
    public static InputAction? PartnerOf(InputAction action)
    {
        foreach (var (positive, negative) in All)
        {
            if (action == positive)
                return negative;
            if (action == negative)
                return positive;
        }

        return null;
    }

    /// <summary>+1 for a pair's positive action, -1 for its negative one, 0 for an action in no
    /// pair. A full axis held by an action reads the side this names.</summary>
    public static int SideOf(InputAction action)
    {
        foreach (var (positive, negative) in All)
        {
            if (action == positive)
                return 1;
            if (action == negative)
                return -1;
        }

        return 0;
    }

    /// <summary>The positive action of that action's pair, the one row a file writes a full axis
    /// under. Null for an action in no pair.</summary>
    public static InputAction? PositiveOf(InputAction action) => SideOf(action) switch
    {
        1 => action,
        -1 => PartnerOf(action),
        _ => null,
    };

    /// <summary>The full axis a capture on <paramref name="row"/> means when the player moved
    /// <paramref name="axis"/> toward <paramref name="movedSign"/> (+1 or -1). Moving toward the row's
    /// own direction binds the axis the way round that fires that row. That is how invert is
    /// inferred rather than asked for.</summary>
    public static BindingControl FullAxisFor(InputAction row, int axis, int movedSign, float deadzone)
    {
        if (movedSign != 1 && movedSign != -1)
            throw new ArgumentOutOfRangeException(nameof(movedSign), movedSign, "A capture moves an axis toward +1 or -1.");
        int side = SideOf(row);
        if (side == 0)
            throw new ArgumentException($"{row} is in no axis pair, so a full axis cannot be bound to it.", nameof(row));
        return BindingControl.FullAxis(axis, inverted: movedSign * side < 0, deadzone);
    }
}
