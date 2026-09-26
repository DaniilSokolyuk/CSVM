using System;
using System.Collections.Generic;

namespace CSVM.Sticks;

/// <summary>What the shape test made of a device: not judged yet, a flight stick, or not one.</summary>
public enum StickFit
{
    Unsettled,
    NotStick,
    Stick,
}

/// <summary>
/// Whether a device looks like a flight stick, which is what the generic single-stick default asks
/// before it claims one (<see cref="GenericStickDefault"/>). A stick has at least
/// <see cref="MinAxes"/> axes, and axes 0 and 1 rest within <see cref="CentreTolerance"/> of centre.
/// Its other axes may rest anywhere, because a throttle lever parks where the player left it. The
/// judgement is made once per connection, from the roster's rest sample, so deflecting the stick
/// later never withdraws it. The rule is in <c>docs/org/input.md</c>, "The generic stick default".
/// </summary>
public readonly record struct StickShape(StickFit Fit, int Axes)
{
    /// <summary>The fewest axes a stick has: roll, pitch and a throttle.</summary>
    public const int MinAxes = 3;

    /// <summary>How far from centre axes 0 and 1 may rest. The VKB sticks rest within 0.01; TUNE.
    /// </summary>
    public const float CentreTolerance = 0.1f;

    /// <summary>Judges a device with <paramref name="axes"/> axes from its sampled
    /// <paramref name="rest"/>, which is null until the roster has sampled it.</summary>
    public static StickShape Judge(int axes, IReadOnlyList<float>? rest)
    {
        if (rest is null)
        {
            return new StickShape(StickFit.Unsettled, axes);
        }

        bool centred = axes >= MinAxes && rest.Count >= 2
            && Math.Abs(rest[0]) <= CentreTolerance && Math.Abs(rest[1]) <= CentreTolerance;
        return new StickShape(centred ? StickFit.Stick : StickFit.NotStick, axes);
    }

    /// <summary>The shape of <paramref name="model"/> in <paramref name="roster"/>, judged by its
    /// first unit; unsettled until every unit of it has been sampled, or when none is open.</summary>
    public static StickShape Of(StickRoster roster, StickModel model)
    {
        ArgumentNullException.ThrowIfNull(roster);
        StickShape? first = null;
        foreach (var stick in roster.Sticks)
        {
            if (stick.Model != model)
            {
                continue;
            }

            var shape = Judge(stick.Axes, roster.RestingAxes(stick));
            if (shape.Fit == StickFit.Unsettled)
            {
                return shape;
            }

            first ??= shape;
        }

        return first ?? new StickShape(StickFit.Unsettled, 0);
    }
}
