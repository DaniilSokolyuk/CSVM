using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Sticks;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>One analogue source's share of a tick's flight command, before the keyboard and the
/// mouse are summed in. Each of the four reads in [-1, 1].</summary>
public readonly record struct AnalogCommand(float Pitch, float Roll, float Yaw, float ThrottleRate);

/// <summary>How the two analogue sources become flight command. A pad's pitch and roll bend through
/// <see cref="PadCurve"/>. A flight stick's axes arrive already deadzoned and rescaled by their own
/// full-axis bindings, so they pass through linearly. <c>FlightController</c> sums both into the
/// keyboard and mouse deflections and clamps each axis, the rule every other source already follows.
/// A throttle lever reads from whichever source holds it and is present, so an unplugged stick
/// releases its lever rather than reading as half throttle.
/// </summary>
public static class AnalogAxes
{
    /// <summary>Deadzone plus squared response, for fine control around centre. The pad's
    /// curve only. A stick's own binding carries its deadzone.</summary>
    public static float PadCurve(float v)
    {
        const float deadzone = 0.15f;
        float a = Mathf.Abs(v);
        if (a < deadzone)
            return 0f;
        float t = Mathf.Min(1f, (a - deadzone) / (1f - deadzone));
        return Mathf.Sign(v) * t * t;
    }

    /// <summary>The pads' share: stick back is nose up, stick right banks right, yaw and throttle
    /// unbent.</summary>
    public static AnalogCommand Pad(ActionSnapshot pad) => new(
        PadCurve(pad.Axis(InputAction.PitchUp, InputAction.PitchDown)),
        -PadCurve(pad.Axis(InputAction.RollRight, InputAction.RollLeft)),
        pad.Axis(InputAction.YawLeft, InputAction.YawRight),
        pad.Axis(InputAction.ThrottleUp, InputAction.ThrottleDown));

    /// <summary>The sticks' share, with the pad's signs and no curve. A full axis on the throttle
    /// pair is therefore a rate in proportion to deflection.</summary>
    public static AnalogCommand Stick(ActionSnapshot stick) => new(
        stick.Axis(InputAction.PitchUp, InputAction.PitchDown),
        -stick.Axis(InputAction.RollRight, InputAction.RollLeft),
        stick.Axis(InputAction.YawLeft, InputAction.YawRight),
        stick.Axis(InputAction.ThrottleUp, InputAction.ThrottleDown));

    /// <summary>The lever's position this tick, the furthest of the two sources it is bound on, or
    /// null when neither can be read. A source counts while it holds a binding on
    /// <paramref name="row"/>: any non-stick binding for the pads, a stick binding whose model
    /// <paramref name="sticks"/> lists for the sticks. An absent stick's axis reads centred, which
    /// is half throttle on a lever.</summary>
    public static float? LeverPosition(IReadOnlyList<Binding> row, float padValue, float stickValue, StickRoster? sticks)
    {
        bool pads = false;
        bool connected = false;
        foreach (var binding in row)
        {
            if (!StickModel.TryFromDevice(binding.Device, out var model))
                pads = true;
            else if (!connected && Connected(sticks, model))
                connected = true;
        }

        if (!pads && !connected)
            return null;
        return Math.Max(pads ? padValue : 0f, connected ? stickValue : 0f);
    }

    /// <summary>Steps <paramref name="takeover"/> on a position from <see cref="LeverPosition"/>,
    /// releasing it on a tick with none, so a stick plugged back in seeds rather than jumps.</summary>
    public static float? StepLever(LeverTakeover takeover, float? position, bool otherCommand)
    {
        ArgumentNullException.ThrowIfNull(takeover);
        if (position is not { } read)
        {
            takeover.Release();
            return null;
        }

        return takeover.Step(read, otherCommand);
    }

    /// <summary>Whether a unit of <paramref name="model"/> is open in <paramref name="sticks"/>. A
    /// null roster (sticks off, or a seat that reads none) has nothing connected.</summary>
    public static bool Connected(StickRoster? sticks, StickModel model)
    {
        if (sticks is null)
            return false;
        foreach (var stick in sticks.Sticks)
        {
            if (stick.Model == model)
                return true;
        }

        return false;
    }
}
