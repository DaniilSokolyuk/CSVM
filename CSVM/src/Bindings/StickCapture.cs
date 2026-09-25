using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>The stick half of <see cref="ControlCapture"/>. It scans every button, hat direction and
/// axis of each stick the seat lists (<see cref="IStickDevices"/>) over a stick's whole range. An
/// axis move on a row that takes a full axis becomes one (<see cref="AxisPairs.FullAxisFor"/>),
/// stamped with <see cref="DeadzoneFor"/>.
/// ⚠ Do not measure a stick axis from zero. A lever rests anywhere (axis 2: 1.00 on L, -0.57 on R).
/// A move counts from the armed value; the pad's rest band would mask such a lever forever.</summary>
public sealed class StickCapture
{
    /// <summary>The highest button index scanned, exclusive: DirectInput's own limit.</summary>
    public const int Buttons = 128;

    /// <summary>The highest axis index scanned, exclusive: DirectInput's eight axes.</summary>
    public const int Axes = 8;

    /// <summary>The highest hat index scanned, exclusive. DirectInput reports at most four.</summary>
    public const int Hats = 4;

    /// <summary>How far a stick axis must travel from its armed value to count as the player's
    /// choice. Below the pad's threshold, since a lever parked near one end cannot travel as far.
    /// TUNE.</summary>
    public const float MoveThreshold = 0.5f;

    /// <summary>The deadzone stamped on a captured flight axis and on the lever (Decision 12). TUNE.
    /// </summary>
    public const float FlightDeadzone = 0.02f;

    /// <summary>The deadzone stamped on a full axis captured on the throttle rate pair, wider so a
    /// spring-centred stick's rest cannot creep the throttle (Decision 12). TUNE.</summary>
    public const float RateThrottleDeadzone = 0.08f;

    private static readonly HatDirection[] Directions =
    {
        HatDirection.Up, HatDirection.Right, HatDirection.Down, HatDirection.Left,
    };

    private readonly InputAction? _row;
    private readonly List<DeviceId> _devices = new();
    private readonly HashSet<(DeviceId Device, int Index)> _maskedButtons = new();
    private readonly HashSet<(DeviceId Device, int Hat, HatDirection Direction)> _maskedHats = new();
    private readonly Dictionary<(DeviceId Device, int Axis), float> _armed = new();
    private bool _seeded;

    /// <summary>A stick scan for a capture on <paramref name="row"/>. With no row, an axis is captured
    /// as a half axis the way a pad's is.</summary>
    public StickCapture(InputAction? row) => _row = row;

    /// <summary>The deadzone a full axis captured on that row is stamped with: the throttle rate pair
    /// gets <see cref="RateThrottleDeadzone"/>, every other row <see cref="FlightDeadzone"/>.</summary>
    public static float DeadzoneFor(InputAction row) =>
        row is InputAction.ThrottleUp or InputAction.ThrottleDown ? RateThrottleDeadzone : FlightDeadzone;

    /// <summary>Takes the sticks connected now. The first unblocked read seeds the scan: it masks
    /// every button and hat direction held and records each axis where it sits. A block re-seeds.
    /// A stick plugged in later waits for the next arm, since its first readings are not at rest.
    /// </summary>
    public void Arm(IDeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _devices.Clear();
        _seeded = false;
        if (state is IStickDevices sticks)
            _devices.AddRange(sticks.Devices());
        Ready(state);
    }

    /// <summary>A stick button or hat direction pressed since <see cref="Arm"/>, or null. Buttons
    /// first, then hats, device by device in the order the seat lists them.</summary>
    public Binding? PollPresses(IDeviceState state)
    {
        if (!Ready(state))
            return null;

        foreach (var device in _devices)
        {
            for (int b = 0; b < Buttons; b++)
            {
                if (Fresh(_maskedButtons, (device, b), state.IsButtonDown(device, b)))
                    return new Binding(device, BindingControl.Button(b));
            }
        }

        foreach (var device in _devices)
        {
            for (int h = 0; h < Hats; h++)
            {
                var held = state.HatState(device, h);
                foreach (var direction in Directions)
                {
                    if (Fresh(_maskedHats, (device, h, direction), (held & direction) != 0))
                        return new Binding(device, BindingControl.Hat(h, direction));
                }
            }
        }

        return null;
    }

    /// <summary>The stick axis moved furthest past <see cref="MoveThreshold"/> from where it was
    /// armed, as the binding the row takes, or null. A half axis is only captured once it sits past
    /// its own deadzone, so the moment of capture is a moment the binding fires.</summary>
    public Binding? PollAxes(IDeviceState state)
    {
        if (!Ready(state))
            return null;

        DeviceId best = default;
        int bestAxis = -1;
        float bestTravel = 0f;
        foreach (var ((device, axis), armed) in _armed)
        {
            float value = state.AxisValue(device, axis);
            float travel = value - armed;
            if (Math.Abs(travel) < MoveThreshold || Math.Abs(travel) <= Math.Abs(bestTravel))
                continue;
            if (!FullAxisRow() && value * Math.Sign(travel) <= ControlCapture.CapturedDeadzone)
                continue;
            best = device;
            bestAxis = axis;
            bestTravel = travel;
        }

        if (bestAxis < 0)
            return null;
        int sign = Math.Sign(bestTravel);
        var control = FullAxisRow() && _row is { } row
            ? AxisPairs.FullAxisFor(row, bestAxis, sign, DeadzoneFor(row))
            : BindingControl.Axis(bestAxis, sign, ControlCapture.CapturedDeadzone);
        return new Binding(best, control);
    }

    private static bool Fresh<T>(HashSet<T> masked, T key, bool down)
    {
        if (!down)
        {
            masked.Remove(key);
            return false;
        }

        return !masked.Contains(key);
    }

    // Whether the scan may read this frame. ⚠ Do not seed from a blocked read. Every axis reads 0
    // there, so a lever resting at 1.00 would count as moved once reads resume.
    private bool Ready(IDeviceState state)
    {
        if (state is IStickDevices { ReadsBlocked: true })
        {
            _seeded = false;
            return false;
        }

        if (_seeded)
            return true;
        Seed(state);
        return false;
    }

    private void Seed(IDeviceState state)
    {
        _seeded = true;
        _maskedButtons.Clear();
        _maskedHats.Clear();
        _armed.Clear();
        foreach (var device in _devices)
        {
            for (int b = 0; b < Buttons; b++)
            {
                if (state.IsButtonDown(device, b))
                    _maskedButtons.Add((device, b));
            }

            for (int h = 0; h < Hats; h++)
            {
                var held = state.HatState(device, h);
                foreach (var direction in Directions)
                {
                    if ((held & direction) != 0)
                        _maskedHats.Add((device, h, direction));
                }
            }

            for (int a = 0; a < Axes; a++)
                _armed[(device, a)] = state.AxisValue(device, a);
        }
    }

    private bool FullAxisRow() => _row is { } row && AxisPairs.TakesFullAxis(row);
}
