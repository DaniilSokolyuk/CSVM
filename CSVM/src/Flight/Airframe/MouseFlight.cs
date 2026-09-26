using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The stick the mouse flies, decoded from the mouse arm of <c>FUN_00487460</c>
/// (`docs/org/flightModel.md`). Three offsets in, three deflections out, each through the same
/// deadzone-and-rescale law, and the autogyro exchange between them. Pure arithmetic with no device
/// in it, so a suite drives the arm without a window and the seat that holds the cursor decides
/// nothing about the law.</summary>
public static class MouseFlight
{
    /// <summary>How far the roll source travels before it deflects at all, and the same number on
    /// the pitch source. The immediate at <c>0x006034a8</c> (roll, <c>0x48770e</c>) and the double
    /// at <c>0x00608078</c> (pitch, <c>0x4876af</c>).</summary>
    public const float AttitudeDeadzone = 0.1f;

    /// <summary>The yaw source's own, three times the other two: <c>0x006034ac</c> and its negative
    /// at <c>0x006035ac</c>, read at <c>0x48774c</c>. It belongs to the third axis, so an autogyro,
    /// whose yaw is fed by the cursor's own sideways travel, does not take it
    /// (<see cref="Read"/>).</summary>
    public const float YawDeadzone = 0.3f;

    /// <summary>One source past its deadzone, rescaled so the travel left over spans the whole
    /// deflection: the original subtracts the deadzone by sign and multiplies by its reciprocal
    /// complement (<c>0x006080bc</c> is 1/0.9, <c>0x006080b4</c> is 1/0.7). Inside the deadzone the
    /// source contributes nothing at all rather than a small deflection.</summary>
    public static float Gate(float source, float deadzone)
    {
        if (source > deadzone)
            return (source - deadzone) / (1f - deadzone);
        return source < -deadzone ? (source + deadzone) / (1f - deadzone) : 0f;
    }

    /// <summary>What the cursor adds to this frame's stick. <paramref name="x"/> is the cursor's
    /// offset across the pane and <paramref name="y"/> its offset down it, both in [-1, 1];
    /// <paramref name="wheel"/> is the original's third mouse axis, which this port has none of and
    /// passes as zero. The three are summed into the keyboard and pad deflections, which is what the
    /// original's arm does to its own accumulator slots (<c>+0x100</c>, <c>+0x108</c>,
    /// <c>+0x10c</c>).</summary>
    public static FlightInput Read(float x, float y, float wheel, bool isAutogyro)
    {
        float rollSource = x;
        float yawSource = wheel;
        float yawDeadzone = YawDeadzone;
        // 0x4876f4: an autogyro takes its roll off the third axis and its yaw off the sideways
        // travel, each negated, so it yaws where an aeroplane banks. The pitch write at 0x4876e1
        // has already happened and the exchange leaves it alone.
        if (isAutogyro)
        {
            (rollSource, yawSource) = (-yawSource, -rollSource);
            // ⚠ Remake-only, and NOT the decoded 0.3: the exchange has just put the cursor's own
            // sideways travel in the yaw slot, so that travel keeps its own dead band rather than
            // the absent third axis's (docs/controls.md, "Flying with the mouse").
            yawDeadzone = AttitudeDeadzone;
        }

        return new FlightInput
        {
            Roll = -Gate(rollSource, AttitudeDeadzone),
            Pitch = Gate(y, AttitudeDeadzone),
            Yaw = Gate(yawSource, yawDeadzone),
            Throttle = 0f,
        };
    }

    /// <summary>Where the cursor stands inside a pane, as the pair <see cref="Read"/> takes: the
    /// offset from the pane's middle over its half extent, clamped, so an edge reads exactly ±1 and
    /// the middle reads zero. A pane with no extent reads centred rather than dividing by nothing.
    /// </summary>
    public static Vector2 Offset(Vector2 cursor, Vector2 middle, Vector2 halfExtent)
    {
        float x = halfExtent.X > 0f ? Mathf.Clamp((cursor.X - middle.X) / halfExtent.X, -1f, 1f) : 0f;
        float y = halfExtent.Y > 0f ? Mathf.Clamp((cursor.Y - middle.Y) / halfExtent.Y, -1f, 1f) : 0f;
        return new Vector2(x, y);
    }
}
