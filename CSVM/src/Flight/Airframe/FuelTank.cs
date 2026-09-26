namespace CSVM.Flight.Airframe;

/// <summary>The flown aircraft's tank, and the lever freeze a dry one causes. Once per tick the
/// original takes <c>remaining -= dt · lever · 5</c> off the live lever, and a tank that has
/// reached zero skips the throttle slew as well, so an empty tank holds the lever where it stands
/// rather than closing it. Decode: docs/org/flightModel.md, "Part-throttle equilibrium". Only the
/// local player's aircraft burns; an AI tank is never touched, and neither is thrust, which reads
/// the lever this type gates and nothing else.</summary>
public sealed class FuelTank
{
    /// <summary>Units burned per second at a fully open lever, the multiplier at <c>0x48e609</c>.
    /// The lever scales it linearly, so a half-open lever burns half as fast.</summary>
    public const float BurnRate = 5f;

    /// <summary>A full tank, from the airframe's authored <c>fuel</c> key. Non-positive means no
    /// tank was authored, which frees the lever rather than freezing it at spawn: every shipped
    /// player chain authors the key, so only a fixture reaches that arm.</summary>
    public float Capacity { get; set; }

    /// <summary>What is left in the tank, never below zero.</summary>
    public float Remaining { get; private set; }

    /// <summary>Whether the tank has run out, which is the condition that freezes the lever.</summary>
    public bool Dry => Capacity > 0f && Remaining <= 0f;

    /// <summary>Refills to <see cref="Capacity"/>, the original's spawn-time top-up.</summary>
    public void Fill() => Remaining = Capacity;

    /// <summary>Burns one tick against <paramref name="lever"/> and reports whether the throttle
    /// lever may move this tick. The lever passed in is the value BEFORE this tick's slew, which
    /// is what the original reads, and the test comes first, so a tank that is already empty
    /// burns nothing and returns false.</summary>
    public bool Step(float dt, float lever)
    {
        if (Capacity <= 0f)
            return true;
        if (Remaining <= 0f)
            return false;

        Remaining -= dt * lever * BurnRate;
        if (Remaining < 0f)
            Remaining = 0f;
        return true;
    }
}
