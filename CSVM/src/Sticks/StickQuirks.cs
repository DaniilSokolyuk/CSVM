using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSVM.Sticks;

/// <summary>
/// Per-model corrections applied where <see cref="StickRoster"/> reads an axis, so every reader
/// (flight, capture, menus, the rest sample, <c>--dump-sticks</c>) sees the corrected value. The one
/// correction is a flipped axis. An axis reading opposite to DirectInput's usual polarity is
/// negated, so a twist right reads positive. Profile tokens name the corrected value.
/// ⚠ Do not move this table into the profile files. A user profile replaces the shipped one whole,
/// so the flip would vanish with the first save (<c>docs/org/input.md</c>, axis polarity quirks).
/// </summary>
public static class StickQuirks
{
    // Both VKB Gladiator EVO grips, R (0200) and L (0201), read their twist negative twisted right.
    private static readonly Dictionary<StickModel, int[]> Flipped = new()
    {
        [new StickModel(0x231D, 0x0200)] = new[] { 5 },
        [new StickModel(0x231D, 0x0201)] = new[] { 5 },
    };

    /// <summary>Whether <paramref name="model"/>'s <paramref name="axis"/> is read negated.</summary>
    public static bool Flips(StickModel model, int axis) =>
        Flipped.TryGetValue(model, out var axes) && Array.IndexOf(axes, axis) >= 0;

    /// <summary>The model's quirks as the dump prints them, <c>axis 5 flipped</c>, or empty.</summary>
    public static string Describe(StickModel model) =>
        Flipped.TryGetValue(model, out var axes)
            ? string.Join(", ", Array.ConvertAll(axes, a => string.Create(CultureInfo.InvariantCulture, $"axis {a} flipped")))
            : string.Empty;
}
