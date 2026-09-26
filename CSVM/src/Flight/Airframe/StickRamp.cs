using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The original's keyboard stick: an accumulator, not an on/off flag. A held key ramps
/// the axis toward full deflection at a fixed rate, and releasing or reversing it drops the axis
/// to centre in one frame. Gradual on, instant off, the asymmetry is what makes fast stick
/// cadences reach far less deflection than slow ones, and the analogue axes bypass it entirely
/// (decode in docs/org/flightModel.md).</summary>
public static class StickRamp
{
    /// <summary>Deflection per second while a key is held: full travel takes 0.4 s.</summary>
    public const float Rate = 2.5f;

    /// <summary>Advances one axis by <paramref name="dt"/> for a key command of -1, 0 or +1,
    /// returning the new deflection. Zeroing first is the decoded ordering, so a reversal starts
    /// its ramp from centre in the same frame rather than counting down from the old value.</summary>
    public static float Step(float current, float command, float dt)
    {
        if (command <= 0f && current > 0f)
            current = 0f;
        else if (command >= 0f && current < 0f)
            current = 0f;

        if (command == 0f)
            return current;

        return Mathf.Clamp(current + (command * Rate * dt), -1f, 1f);
    }
}
