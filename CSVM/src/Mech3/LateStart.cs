using System;
using System.Collections.Generic;

namespace CSVM.Mech3;

/// <summary>
/// Where a run of back-to-back clips stands when started late. It names the clip playing
/// <c>overdue</c> seconds after the run was due, and how far into it. A guest that hears of a
/// radio call late joins it there, not at its start.
/// </summary>
internal static class LateStart
{
    /// <summary>The clip <paramref name="overdue"/> seconds falls in and the offset into it.
    /// An index equal to the count means every clip is already over. A non-positive or
    /// non-finite overdue starts the first clip from its start.</summary>
    public static (int Index, double Offset) Into(IReadOnlyList<double> lengths, double overdue)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        if (!double.IsFinite(overdue) || overdue <= 0.0)
            return (0, 0.0);
        for (int i = 0; i < lengths.Count; i++)
        {
            double length = Math.Max(0.0, lengths[i]);
            if (overdue < length)
                return (i, overdue);
            overdue -= length;
        }

        return (lengths.Count, 0.0);
    }
}
